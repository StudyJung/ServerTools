static class Timer	// 타이머: 별도 스레드에서 X초마다 Batch, X분마다 Alive 로그 + 1분 타이머 (클라 분당 교체, 스레드 풀)
{
	private const int	m_nTickMs		= 1000;	// Batch 호출 간격 (ms)
	private const int	m_nAliveMin		= 1;	// Alive 로그 간격 (분)
	private const int	m_nStopWaitMs	= 5000;	// 중지 때 Batch 끝 대기 (ms, 넘음: 색인 쓰기는 종료 Flush 가 이어서 대기)

	private static Thread?					m_kThread;							// 타이머 스레드
	private static CancellationTokenSource	m_kStop		= new CancellationTokenSource();	// 중지 요청 (대기 즉시 깸)
	private static DateTimeOffset			m_kStartTime;						// 시작 시각 (KST, 가동 시간)
	private static long						m_nAliveTick;						// 마지막 Alive 로그 (TickCount64)
	private static System.Threading.Timer?	m_kMinuteTimer;						// 클라 분당 교체 (1분마다, 스레드 풀: 타이머 스레드 Batch·색인 Flush 와 따로 → 안 밀림)

	public static void Start()	// 타이머 시작 (백그라운드 스레드)
	{
		if (m_kThread != null)	// 중복 시작 방지
		{
			return;
		}

		m_kStop			= new CancellationTokenSource();
		m_kStartTime	= GetNow();
		m_nAliveTick	= Environment.TickCount64;
		m_kThread		= new Thread(Run) { IsBackground = true, Name = "Timer" };
		m_kMinuteTimer	= new System.Threading.Timer(_ => OnMinute(), null, 60 * 1000, 60 * 1000);

		m_kThread.Start();

		Log.Write(INFO_, $"타이머: 시작 (Batch {m_nTickMs}ms, Alive {m_nAliveMin}분)");
	}

	public static void Stop()	// 타이머 중지 (진행 중 Batch 완료 대기, 최대 X초, 넘음: 색인 쓰기는 TabQuery.Close Flush 가 플러시 잠금으로 이어서 대기)
	{
		if (m_kThread == null)
		{
			return;
		}

		m_kStop.Cancel();

		if (!m_kThread.Join(m_nStopWaitMs))	// Batch (색인 Flush) 진행 중
		{
			Log.Write(INFO_, $"타이머: 중지 대기 {m_nStopWaitMs}ms 넘음 (Batch 진행 중 → 색인 쓰기 끝: 종료 정리가 대기)");
		}

		m_kMinuteTimer?.Dispose();

		m_kThread		= null;
		m_kMinuteTimer	= null;

		Log.Write(INFO_, "타이머: 중지");
	}

	private static void OnMinute()	// 1분마다 (스레드 풀, 색인 Flush 로 안 밀림): 클라 분당 교체·로그 (예외: 출력 후 다음 분)
	{
		try
		{
			Main.OnMinute();
		}
		catch (Exception kException)
		{
			Log.Write(ERROR, $"클라 분당 오류: {kException.Message}");
		}
	}

	private static void Run()	// 타이머 스레드: Batch → 남은 시간 대기
	{
		while (!m_kStop.IsCancellationRequested)
		{
			long nBegin = Environment.TickCount64;

			try
			{
				Batch();
			}
			catch (Exception kException)	// 로그 후 계속 (스레드 예외 → 프로세스 종료 방지)
			{
				Log.Write(ERROR, $"타이머 오류: {kException.Message}");
			}

			long nWait = Math.Max(0, m_nTickMs - (Environment.TickCount64 - nBegin));	// Batch 소요 시간 제외

			m_kStop.Token.WaitHandle.WaitOne((int)nWait);
		}
	}

	private static void Batch()	// X초마다: Alive 로그, 색인 버퍼 → 세그먼트 (TabQuery.Tick, 몇 초 걸릴 수 있음 → 클라 분당 교체는 m_kMinuteTimer 따로)
	{
		if (Environment.TickCount64 - m_nAliveTick >= m_nAliveMin * 60 * 1000L)
		{
			m_nAliveTick = Environment.TickCount64;

			Log.Write(INFO_, $"Alive: 가동 {(GetNow() - m_kStartTime).ToString(@"d\.hh\:mm\:ss")}");
		}

		Main.OnTick();
	}
}

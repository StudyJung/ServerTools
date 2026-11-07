static class Timer	// 타이머: 별도 스레드에서 X초마다 Batch 호출, X분마다 Alive 로그 (Start → Stop)
{
	private const int	m_nTickMs		= 1000;	// Batch 호출 간격 (ms)
	private const int	m_nAliveMin		= 10;	// Alive 로그 간격 (분)

	private static Thread?					m_kThread;							// 타이머 스레드 (Start 생성, Stop 해제)
	private static CancellationTokenSource	m_kStop		= new CancellationTokenSource();	// 중지 요청 (대기 중이면 즉시 깸)
	private static DateTimeOffset			m_kStartTime;						// 시작 시각 (Alive 로그 가동 시간)
	private static long						m_nAliveTick;						// 마지막 Alive 로그 시각 (Environment.TickCount64)

	public static void Start()	// 타이머 시작 (백그라운드 스레드: 프로세스 종료 시 함께 종료)
	{
		if (m_kThread != null)	// 중복 시작 방지
		{
			return;
		}

		m_kStop			= new CancellationTokenSource();
		m_kStartTime	= GetNow();
		m_nAliveTick	= Environment.TickCount64;
		m_kThread		= new Thread(Run) { IsBackground = true, Name = "Timer" };

		m_kThread.Start();

		Log.Write(INFO_, $"타이머: 시작 (Batch {m_nTickMs}ms, Alive {m_nAliveMin}분)");
	}

	public static void Stop()	// 타이머 중지: 중지 요청 → 진행 중인 Batch 완료 대기 (최대 X초)
	{
		if (m_kThread == null)
		{
			return;
		}

		m_kStop.Cancel();
		m_kThread.Join(5000);

		m_kThread = null;

		Log.Write(INFO_, "타이머: 중지");
	}

	private static void Run()	// 타이머 스레드: Batch → 남은 시간 대기 (Batch 소요 시간 제외)
	{
		while (!m_kStop.IsCancellationRequested)
		{
			long nBegin = Environment.TickCount64;

			try
			{
				Batch();
			}
			catch (Exception kException)	// 로그 후 계속 (스레드 미처리 예외 → 프로세스 종료 방지)
			{
				Log.Write(ERROR, $"타이머 오류: {kException.Message}");
			}

			long nWait = Math.Max(0, m_nTickMs - (Environment.TickCount64 - nBegin));	// 남은 대기 시간 (Batch 가 X초 넘으면 바로 다음 호출)

			m_kStop.Token.WaitHandle.WaitOne((int)nWait);
		}
	}

	private static void Batch()	// X초마다 호출 (타이머 스레드): X분마다 Alive 로그
	{
		if (Environment.TickCount64 - m_nAliveTick >= m_nAliveMin * 60 * 1000L)
		{
			m_nAliveTick = Environment.TickCount64;

			Log.Write(INFO_, $"Alive: 가동 {(GetNow() - m_kStartTime).ToString(@"d\.hh\:mm\:ss")}");
		}

		// 차후 작업할 코드 아래에 추가
	}
}

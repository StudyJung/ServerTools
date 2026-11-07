class Gate	// 부하 자리 (쿼리·서치·보기·덤프 다운·파일 다운 따로 1개씩): 세션 (로그인 계정, 없음: IP) 1건 → 전체 X건, 넘침: 대기 → 한 사용자가 오래 써도 다른 사용자 동시 사용, Yield: 스레드 양보 (CPU 루프 공통)
{
	private const int	m_nYieldBusyMs	= 500;	// 양보: 스레드 연속 실행 (ms)
	private const int	m_nYieldRestMs	= 100;	// 양보: 쉼 (ms)

	[ThreadStatic] private static long	m_nYieldSlice;	// 이 스레드 연속 실행 시작 (Stopwatch)
	[ThreadStatic] private static long	m_nYieldLast;	// 이 스레드 마지막 Yield (Stopwatch, 0: 처음)

	private readonly SemaphoreSlim									m_kAll;	// 전체 자리
	private readonly ConcurrentDictionary<string, SemaphoreSlim>	m_hmapSession	= new ConcurrentDictionary<string, SemaphoreSlim>();	// 세션 → 1건 자리

	public Gate(int nAll)
	{
		m_kAll = new SemaphoreSlim(nAll, nAll);
	}

	public static string GetSession(HttpContext kContext)	// 세션 키: 로그인 계정, 없음: IP
	{
		string sID = kContext.User.Identity?.Name ?? "";

		return sID != "" ? sID : Web.GetAddr(kContext);
	}

	public Task<IDisposable> EnterAsync(HttpContext kContext)	// 자리 잡음 (요청 끊김: OperationCanceledException, Dispose: 반납)
	{
		return EnterAsync(GetSession(kContext), kContext.RequestAborted);
	}

	public async Task<IDisposable?> TryEnterAsync(HttpContext kContext)	// 자리 잡음 (요청 끊김: null)
	{
		try
		{
			return await EnterAsync(kContext);
		}
		catch (OperationCanceledException)
		{
			return null;
		}
	}

	public async Task<IDisposable> EnterAsync(string sSession, CancellationToken kStop)	// 자리 잡음: 세션 → 전체 (배경 작업: 세션 키·토큰 직접)
	{
		SemaphoreSlim kSession = m_hmapSession.GetOrAdd(sSession, _ => new SemaphoreSlim(1, 1));

		await kSession.WaitAsync(kStop);

		try
		{
			await m_kAll.WaitAsync(kStop);
		}
		catch
		{
			kSession.Release();

			throw;
		}

		return new Release(m_kAll, kSession);
	}

	public static void Yield(CancellationToken kStop)	// 양보: 취소 확인 + 이 스레드 연속 실행 m_nYieldBusyMs 넘음 → m_nYieldRestMs 쉼 (앞 Yield 뒤 m_nYieldBusyMs 넘게 지남: 쉬었던 스레드 → 새로 셈)
	{
		kStop.ThrowIfCancellationRequested();

		long nNow = Stopwatch.GetTimestamp();

		if (m_nYieldLast == 0 || Stopwatch.GetElapsedTime(m_nYieldLast, nNow).TotalMilliseconds >= m_nYieldBusyMs)
		{
			m_nYieldSlice = nNow;
		}
		else if (Stopwatch.GetElapsedTime(m_nYieldSlice, nNow).TotalMilliseconds >= m_nYieldBusyMs)
		{
			Thread.Sleep(m_nYieldRestMs);

			nNow			= Stopwatch.GetTimestamp();
			m_nYieldSlice	= nNow;
		}

		m_nYieldLast = nNow;
	}

	private sealed class Release(SemaphoreSlim kAll, SemaphoreSlim kSession) : IDisposable	// 자리 반납 (전체 → 세션)
	{
		public void Dispose()
		{
			kAll.Release();
			kSession.Release();
		}
	}
}

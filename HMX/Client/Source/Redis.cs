using StackExchange.Redis;	// Redis 클라이언트 (이 파일·Process·TabProcess 만, 전역 using 안 함 → 이름 겹침 방지)

static class Redis	// Redis 연결: 설정 Redis 항목 (sHost, nPort, nDB, sUser, sCode → sPass, sPrefix 목록 키 앞, sError 에러 리스트 키 앞, 실제 키: 설정 값 + 존 + ":"), 최초 사용 시 1회 연결 후 재사용·자동 재연결 (OLD HMXRedis 이식, 서버 Redis 와 같은 모양)
{
	private const int		m_nDefaultPort		= 6379;				// 기본 포트 (Redis:nPort 없음, 서버와 같음)
	private const int		m_nTimeoutMs		= 5000;				// 연결·응답 대기 (ms, 죽은 연결 무한 대기 방지)
	private const string	m_sDefaultProcess	= "HMX:Process:";	// 목록 키 앞 기본값 (Redis:sPrefix 없음, 서버 Redis:sProcess 와 같게)
	private const string	m_sDefaultError		= "HMX:Error:";		// 에러 리스트 키 앞 기본값 (Redis:sError 없음, 서버 Redis:sError 와 같게)

	private static readonly Lazy<ConnectionMultiplexer> m_kConnect = new Lazy<ConnectionMultiplexer>(Connect);	// 연결 (최초 사용 시 생성, 스레드 안전, Config.Load() 이후 사용)

	private static ConnectionMultiplexer Connect()	// 연결 생성 (실패해도 백그라운드 재연결, 연결 전 명령은 예외)
	{
		string sHost = Config.GetString("Redis:sHost");

		if (sHost == "")	// 호스트 미설정
		{
			throw new InvalidOperationException("Redis 호스트 미설정 (Redis:sHost)");
		}

		int		nPort	= Config.GetInt("Redis:nPort", m_nDefaultPort);

		if (nPort <= 0)	// 0·음수: 기본 포트
		{
			nPort = m_nDefaultPort;
		}
		string	sUser	= Config.GetString("Redis:sUser");
		string	sPass	= Config.GetString("Redis:sPass");	// sCode 복호화 값

		ConfigurationOptions kOption = new ConfigurationOptions
		{
			AbortOnConnectFail	= false,		// 연결 실패해도 생성 후 백그라운드 재연결
			ConnectTimeout		= m_nTimeoutMs,
			SyncTimeout			= m_nTimeoutMs,
			AsyncTimeout		= m_nTimeoutMs,
			ClientName			= GetProjectName(),
			DefaultDatabase		= GetDbNumber(),
		};

		kOption.EndPoints.Add(sHost, nPort);

		if (sPass != "")	// 비밀번호 있을 때만 인증 (사용자 지정 시 ACL 인증)
		{
			kOption.User		= sUser == "" ? null : sUser;
			kOption.Password	= sPass;
		}

		ConnectionMultiplexer kConnect = ConnectionMultiplexer.Connect(kOption);

		Log.Write(INFO_, $"Redis 연결: {sHost}:{nPort} (DB: {GetDbNumber()}, 목록: {GetPrefix()}*, 연결: {kConnect.IsConnected})");	// 비밀번호는 출력 안 함

		return kConnect;
	}

	public static bool IsSet()	// Redis 설정 있음 (Redis:sHost)
	{
		return Config.GetString("Redis:sHost") != "";
	}

	public static IDatabase GetDb()	// DB 명령용 (설정 DB 번호)
	{
		return m_kConnect.Value.GetDatabase(GetDbNumber());
	}

	public static IServer GetServer()	// 키 목록 조회용 서버 (단일 엔드포인트, 목록 SCAN)
	{
		ConnectionMultiplexer kConnect = m_kConnect.Value;

		return kConnect.GetServer(kConnect.GetEndPoints()[0]);
	}

	public static int GetDbNumber()	// DB 번호 (Redis:nDB, 없으면 0, 서버와 같음)
	{
		return Config.GetInt("Redis:nDB");
	}

	public static string GetZonePrefix(string sConfigKey, string sZone, string sDefault = "")	// 키 앞: 설정 값 + 존 + ":" (예: Redis:sError HMX:Error: + Dev → HMX:Error:Dev:, 존 없음: 설정 값만, 서버와 같음)
	{
		string sPrefix = Config.GetString(sConfigKey, sDefault);

		sZone = sZone.Trim();	// 존 앞뒤 공백 (서버·클라 키 갈림 방지)

		return sZone == "" ? sPrefix : sPrefix + sZone + ":";
	}

	public static string GetPrefix()	// 목록 키 앞: Redis:sPrefix + 존 (sZone) + ":" (예: HMX:Process:Dev:, 서버 Redis:sProcess + 존 과 같게)
	{
		return GetZonePrefix("Redis:sPrefix", Config.GetString("sZone"), m_sDefaultProcess);
	}

	public static string GetErrorKey()	// 에러 리스트 키: Redis:sError + 존 (sZone) + ":" (예: HMX:Error:Dev:, LPUSH → index 0 최신, 서버 반영 탭 조회)
	{
		return GetZonePrefix("Redis:sError", Config.GetString("sZone"), m_sDefaultError);
	}
}

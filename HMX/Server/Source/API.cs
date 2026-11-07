class API	// 공통 API (서버 정보·에코·접속 클라) + 외부 API (로그 수신·쿼리·서치) + 클라 요청 조각 읽기·응답 조립 (탭 공용)
{
	public const string	EXTERNAL_PATH	= "/api/external/";			// 외부 API 앞 (로그인 대신 경로 뒤 토큰 = 그 API 권한 계정 토큰 (sCode 를 sRand 키로 암호화, Auth.GetToken), 로그인해도 필요)
	public const string	LOG_PATH		= EXTERNAL_PATH + "log/";	// 외부 API: 로그 수신 앞
	public const string	QUERY_PATH		= EXTERNAL_PATH + "query/";	// 외부 API: 쿼리 앞
	public const string	SEARCH_PATH		= EXTERNAL_PATH + "search/";	// 외부 API: 서치 앞

	public static readonly (string sPath, string sMenu)[]	m_arrExternal	=	// 외부 API 목록 (경로 앞 → 권한 메뉴: 계정 aMenu, Auth 토큰 검사·요청 로그 토큰 가림, 추가: 여기 + MapRoute)
	{
		(LOG_PATH, Auth.MENU_LOG),		// 로그 수신 (쿼리 탭 켜짐만 등록, 권한: aAPI 로그)
		(QUERY_PATH, Auth.API_QUERY),	// 쿼리 (쿼리 탭 켜짐만 등록, 권한: aAPI 쿼리)
		(SEARCH_PATH, Auth.API_SEARCH),	// 서치 (서치 탭 켜짐만 등록, 권한: aAPI 서치, TabSearch.MapRoute)
	};

	private const int	m_nReadRetry		= 5;				// 클라 바쁨 거절 시 재요청 횟수
	private const int	m_nReadRetryMs		= 1000;				// 재요청 전 대기 (ms)
	private const string	m_sClientBusy	= "요청이 많음 (잠시 후 다시)";	// 클라 바쁨 거절 문구 (Client 동시 처리 초과 sError 와 같음)

	private static readonly byte[]	m_arrResultKey	= Encoding.UTF8.GetBytes(",\"kResult\":");	// 응답 조립: kResult 키
	private static readonly byte[]	m_arrNull		= Encoding.UTF8.GetBytes("null");			// 응답 조립: 결과 없음
	private static readonly byte[]	m_arrClose		= Encoding.UTF8.GetBytes("}");				// 응답 조립: 끝
	private static readonly byte[]	m_arrNewLine	= Encoding.UTF8.GetBytes("\n");				// 검색 응답: 머신 1대 결과 끝

	private readonly DateTimeOffset		m_kStartTime;		// 서버 시작 시각 (KST)
	private readonly TCPServer?				m_kListener;		// Client TCP 수신 (Main 이 넘김, null: 클라 쓰는 탭 없음)

	public API(TCPServer? kListener)
	{
		m_kStartTime	= GetNow();
		m_kListener		= kListener;
	}

	public void MapRoute(WebApplication WebApp)	// 라우트 등록 (공통 API·외부 API, Main.MapRoute)
	{
		WebApp.MapGet("/api/info", OnInfo);	// 서버 정보
		WebApp.Map("/api/echo", OnEcho);		// ? 뒤 문구 그대로 반환 (모든 메서드)
		if (m_kListener != null)	// 클라 쓰는 탭 있음
		{
			WebApp.MapGet("/api/client", OnClient);	// 접속 클라 목록
		}

		if (Main.IsTab(Main.TAB_QUERY))	// 쿼리 탭 켜짐
		{
			WebApp.MapMethods(LOG_PATH + "{**sToken}", new[] { HttpMethods.Get, HttpMethods.Post }, TCPServer.OnLog);	// 외부 API 로그 수신 (경로 토큰: 로그 권한 계정, 쿼리 문자열·본문 JSON, TCP Log 와 같은 처리)
			WebApp.MapMethods(QUERY_PATH + "{**sToken}", new[] { HttpMethods.Get, HttpMethods.Post }, TabQuery.OnExternalQuery);	// 외부 API 쿼리 (경로 토큰: 쿼리 권한 계정, 쿼리 문자열·본문 JSON sKey·nStart·nEnd·nMinute·nPage, /api/query 와 같은 응답)
		}
	}

	private object OnInfo()	// 서버 정보
	{
		return new
		{
			sMachine	= Environment.MachineName,
			sOs			= RuntimeInformation.OSDescription,
			sRuntime	= RuntimeInformation.FrameworkDescription,
			nPid		= Environment.ProcessId,
			sStartTime	= m_kStartTime.ToString("yyyy-MM-dd HH:mm:ss"),
			sUptime		= (GetNow() - m_kStartTime).ToString(@"d\.hh\:mm\:ss"),
			sTime		= GetNowText(),
			sTitle		= Web.GetTitle(),		// 제목 (예: HMX Alpha)
			sTitleColor	= Web.GetTitleColor(),	// 제목 색 (sZone 별)
		};
	}

	private object OnClient()	// 접속 클라 목록
	{
		return new
		{
			nPort		= m_kListener!.GetPort(),
			arrClient	= m_kListener.GetClientList(),
			sTime		= GetNowText(),
		};
	}

	public static async Task<(byte[]? arrData, long nSize, string sError)> RequestRead(TCPServer kListener, string sType, string sMachine, string sPath, long nOffset, int nCount)	// 조각 1개 요청 (sType: DumpRead·FileRead, 클라 바쁨 거절: 재요청 → 다운 중간 끊김 방지)
	{
		for (int nTry = 0; ; ++nTry)
		{
			(byte[]? arrData, long nSize, string sError) kChunk = await RequestReadOnce(kListener, sType, sMachine, sPath, nOffset, nCount);

			if (kChunk.arrData != null || kChunk.sError != m_sClientBusy || nTry >= m_nReadRetry)	// 받음·다른 오류·재요청 다 씀
			{
				return kChunk;
			}

			await Task.Delay(m_nReadRetryMs);
		}
	}

	private static async Task<(byte[]? arrData, long nSize, string sError)> RequestReadOnce(TCPServer kListener, string sType, string sMachine, string sPath, long nOffset, int nCount)	// 조각 요청 1번 → base64 해제 (오류: arrData null)
	{
		Dictionary<string, object> hmapRequest = new Dictionary<string, object>	// 경로 허용 검사: 클라
		{
			["sType"]	= sType,
			["sPath"]	= sPath,
			["nOffset"]	= nOffset,
			["nCount"]	= nCount,
		};

		(byte[]? arrResult, string sError) = await kListener.Request(sMachine, hmapRequest, Define.m_nDumpWaitSec);

		if (arrResult == null)	// 미접속·시간 초과·끊김
		{
			return (null, 0, sError);
		}

		try
		{
			using JsonDocument kDoc = JsonDocument.Parse(arrResult);

			JsonElement	kRoot			= kDoc.RootElement;
			string		sResultError	= kRoot.GetProperty("sError").GetString() ?? "";	// 클라 오류 (허용 안 된 경로·읽기 실패)

			if (sResultError != "")
			{
				return (null, 0, sResultError);
			}

			return (kRoot.GetProperty("arrData").GetBytesFromBase64(), kRoot.GetProperty("nSize").GetInt64(), "");
		}
		catch (Exception)
		{
			return (null, 0, "결과 형식 오류");
		}
	}

	public static IResult MakeResult(string? sMachine, string sError, byte[]? arrResult)	// 응답 {sMachine, sError, kResult} (보기)
	{
		return Results.Stream((Stream kBody) => WriteResult(kBody, new { sMachine, sError }, arrResult), "application/json; charset=utf-8");
	}

	public static async Task WriteSearchLine(Stream kBody, int nIndex, string sMachine, string sError, byte[]? arrResult)	// 검색 결과 한 줄 {nIndex, sMachine, sError, kResult} + '\n' (바로 flush → 끝난 머신부터)
	{
		await WriteResult(kBody, new { nIndex, sMachine, sError }, arrResult);
		await kBody.WriteAsync(m_arrNewLine);
		await kBody.FlushAsync();
	}

	public static async Task WriteResult(Stream kBody, object kHead, byte[]? arrResult)	// {kHead…, "kResult": 클라 결과 바이트 그대로} (큰 결과 재파싱·직렬화 안 함)
	{
		byte[] arrHead = JsonSerializer.SerializeToUtf8Bytes(kHead, Define.m_kJsonOption);

		await kBody.WriteAsync(arrHead.AsMemory(0, arrHead.Length - 1));	// 끝 '}' 빼고
		await kBody.WriteAsync(m_arrResultKey);
		await kBody.WriteAsync(arrResult ?? m_arrNull);
		await kBody.WriteAsync(m_arrClose);
	}

	private static IResult OnEcho(HttpRequest kRequest)	// ? 뒤 문구 그대로 반환 (%XX 복원)
	{
		string sQuery = kRequest.QueryString.HasValue ? kRequest.QueryString.Value!.Substring(1) : "";	// '?' 제외

		return Results.Text(Uri.UnescapeDataString(sQuery), "text/plain; charset=utf-8");
	}
}

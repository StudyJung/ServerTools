using StackExchange.Redis;	// Redis 명령 (이 파일·Redis·TabOperation 만, 전역 using 안 함 → ClientInfo 등 이름 겹침 방지)

class Process	// 반영 탭 API: /api/process?action=... (HMXServer HMXApi 이식, 요청·응답 형식 같음): 서버 목록 (공통 Redis 의 Redis:sProcess + 존 키, 예: HMX:Process:Dev:) 조회·등록·수정·삭제·이름 변경, On·Off·Down 지시 → 머신별 HMXClient 처리, 작업 로그·에러 (로그인: 공통 Auth)
{
	private const string	TEXT_PLAIN		= "text/plain; charset=utf-8";	// 텍스트 응답 형식
	private const string	DEFAULT_PROCESS	= "HMX:Process:";				// 서버 목록 키 접두사 기본값 (Redis:sProcess 없음)
	private const string	DEFAULT_ERROR	= "HMX:Error:";					// 에러 로그 리스트 키 기본값 (Redis:sError 없음)
	private const int		PAGE_LIMIT		= 300;							// 로그·에러 페이지당 X줄
	private const int		ERROR_DELAY		= 3000;							// Redis 오류 응답 지연 (ms, 재시도 폭주 완화)

	private readonly SemaphoreSlim	m_lockEdit;	// 버전 검증~증가 직렬화 (동시 등록·수정·삭제: 한쪽만 성공, 나머지 버전 불일치)

	public Process()
	{
		m_lockEdit = new SemaphoreSlim(1, 1);

		if (!Config.GetBool("bAuth"))	// 로그인 안 씀: 반영 API (서버 시작·정지·명령 등록) 인증 없음
		{
			ProcessLog.Write(WARN_, "반영 탭: 로그인 안 함 (bAuth false) → 반영 API 인증 없이 열림");
		}
	}

	public async Task<IResult> OnRequest(HttpRequest kRequest)	// 요청 처리 (action 없음·모르는 값: 목록 조회, 로그인: 공통 Auth 미들웨어가 먼저 검사)
	{
		HttpContext	kContext	= kRequest.HttpContext;
		bool		bAction		= kRequest.Query.ContainsKey("action");	// API 요청 (오류: JSON 응답)
		string		sAction		= GetQuery(kRequest, "action");

		if (IsChange(sAction) && IsNavigate(kRequest))	// 바꾸는 요청 + 링크·주소창 이동·다른 사이트 (인증 쿠키 Lax → 다른 사이트 링크 GET 이동에도 전송, 화면 fetch 아님): 차단
		{
			ProcessLog.Write(WARN_, $"이동 요청 차단(action={sAction}) ({GetUser(kContext)})");

			return Results.Json(new { success = false, error = "허용되지 않는 요청입니다." }, statusCode: StatusCodes.Status403Forbidden);
		}

		switch (sAction)	// Redis 불필요 요청
		{
			case "check":	// 시작 확인: TestOn·TestOff 표시 (설정 bTest)
			{
				return Results.Json(new { auth = true, test = Config.GetBool("bTest") });
			}

			case "version":
			{
				return Results.Text(ProcessVersion.Get().ToString(), TEXT_PLAIN);
			}

			case "actlog":
			{
				return OnActLog(kContext);
			}

			case "log":
			{
				return OnLog(kRequest);
			}

			default:
			{
				break;
			}
		}

		if (!Redis.IsSet())	// Redis 설정 없음: 로그·지연 없이 바로 오류
		{
			return bAction ? Results.Json(new { success = false, error = "Redis 설정 없음 (Redis:sHost)" }) : Results.Text("오류: Redis 설정 없음 (Redis:sHost)\n", TEXT_PLAIN);
		}

		bool	bVersioned	= kRequest.Query.ContainsKey("version") && (sAction == "set" || sAction == "rename" || sAction == "del");	// 버전 검증·증가 대상 (등록·수정·삭제·이름 변경)

		try
		{
			if (bVersioned)
			{
				await m_lockEdit.WaitAsync();
			}

			try
			{
				switch (sAction)
				{
					case "errlog":
					{
						return await OnErrLog(kRequest);
					}

					case "set":
					{
						return await OnSet(kContext, bVersioned);
					}

					case "rename":
					{
						return await OnRename(kContext, bVersioned);
					}

					case "del":
					{
						return await OnDel(kContext, bVersioned);
					}

					default:	// 목록 조회
					{
						return await OnList();
					}
				}
			}
			finally
			{
				if (bVersioned)
				{
					m_lockEdit.Release();
				}
			}
		}
		catch (Exception kException)	// Redis 오류 등: 에러 로그 후 지연
		{
			string	sError	= GetErrorText(kException);

			ProcessLog.Write(ERROR, $"요청 처리 오류(action={(bAction ? sAction : "list")}): {sError} ({GetUser(kContext)})");

			await Task.Delay(ERROR_DELAY);

			return bAction ? Results.Json(new { success = false, error = sError }) : Results.Text($"오류: {sError}\n", TEXT_PLAIN);
		}
	}

	private static IResult OnActLog(HttpContext kContext)	// 시작·정지·다운 클릭 로그 (전체·그룹·개별 한 줄, 예: 그룹: 888,1_Center,On → (id@10.1.2.3))
	{
		string	sKind	= GetQuery(kContext.Request, "kind");	// 전체·그룹·개별
		string	sTarget	= GetQuery(kContext.Request, "target");	// 대상 키 파트 (예: 888 / 888,1_Center / 888,1_Center,0_Common)
		string	sStatus	= GetQuery(kContext.Request, "status");	// On·Off·Down

		if (sKind == "" || sTarget == "" || (sStatus != "On" && sStatus != "Off" && sStatus != "Down"))	// 파라미터 검사 및 조기 반환
		{
			return Results.Json(new { success = false, error = "kind, target, status가 필요합니다." });
		}

		ProcessLog.Write(INFO_, $"{sKind}: {sTarget},{sStatus} → ({GetUser(kContext)})");

		return Results.Json(new { success = true });
	}

	// 로그 조회: 오프셋 커서 페이징 (커서 = 파일 스탬프:바이트 오프셋, 그 지점부터 필요분만 읽음, 파일: 공통 Log 시간별 파일, 실행 줄만: [v.버전]·로그인·예전 [HMX_] 태그)
	// 응답: 첫 줄 "#최신 커서|최과거 커서" + 로그 본문 (최신 순 X줄), 결과 없음: 빈 응답
	private static IResult OnLog(HttpRequest kRequest)
	{
		string[]	arrFormat	= { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" };	// datetime-local 형식 ("2026-07-21T07:30" → T: 공백)
		bool		bStart		= DateTime.TryParseExact(GetQuery(kRequest, "start").Replace('T', ' '), arrFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kStart);
		bool		bEnd		= DateTime.TryParseExact(GetQuery(kRequest, "end").Replace('T', ' '), arrFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kEnd);

		if (!bStart || !bEnd || kStart > kEnd)	// 범위 검사 및 조기 반환
		{
			return Results.Text("시간 범위가 올바르지 않습니다.\n", TEXT_PLAIN);
		}

		kEnd = kEnd.AddSeconds(59);	// 끝 분의 59초까지

		string	sDir	= GetQuery(kRequest, "dir");	// "": 최신부터, next: 더 과거, prev: 더 최신
		string	sCursor	= GetQuery(kRequest, "cursor");	// "yyyyMMdd_HH:바이트 오프셋" (줄 시작 위치)

		List<string>	listStamp	= new List<string>();	// 범위 안 있는 시간별 파일 스탬프 (오름차순, 고정 폭 → 문자열 비교 = 시간순)

		for (DateTime kStamp = kStart.Date.AddHours(kStart.Hour); kStamp <= kEnd; kStamp = kStamp.AddHours(1))
		{
			string	sStamp	= kStamp.ToString("yyyyMMdd_HH", CultureInfo.InvariantCulture);

			if (File.Exists(Log.GetFile(sStamp)))
			{
				listStamp.Add(sStamp);
			}
		}

		string	sCurStamp	= "";
		long	nCurOffset	= -1;
		int		nColon		= sCursor.IndexOf(':');

		if (nColon >= 0)	// 커서 분해
		{
			sCurStamp	= sCursor.Substring(0, nColon);
			nCurOffset	= long.TryParse(sCursor.Substring(nColon + 1), out long nValue) ? nValue : 0;
		}

		List<ProcessLogLine>	listOut	= new List<ProcessLogLine>();	// 모은 줄 (최종 내림차순)

		if (sDir == "prev" && sCurStamp != "")	// 더 최신: 커서 줄 다음부터 정방향 → 내림차순
		{
			foreach (string sStamp in listStamp)
			{
				if (string.CompareOrdinal(sStamp, sCurStamp) < 0)	// 커서보다 과거 파일
				{
					continue;
				}

				bool	bCursorFile	= sStamp == sCurStamp;
				bool	bReachedEnd	= ProcessLog.CollectForward(sStamp, bCursorFile ? nCurOffset : 0, bCursorFile, PAGE_LIMIT - listOut.Count, kStart, kEnd, listOut);

				if (listOut.Count >= PAGE_LIMIT || bReachedEnd)
				{
					break;
				}
			}

			listOut.Reverse();
		}
		else	// 최신부터·더 과거: 역방향 (이미 내림차순)
		{
			for (int nIndex = listStamp.Count - 1; nIndex >= 0; --nIndex)
			{
				string	sStamp	= listStamp[nIndex];

				if (sCurStamp != "" && string.CompareOrdinal(sStamp, sCurStamp) > 0)	// 커서보다 미래 파일
				{
					continue;
				}

				bool	bCursorFile		= sStamp == sCurStamp;
				bool	bReachedStart	= ProcessLog.CollectBackward(sStamp, bCursorFile ? nCurOffset : -1, PAGE_LIMIT - listOut.Count, kStart, kEnd, listOut);

				if (listOut.Count >= PAGE_LIMIT || bReachedStart)
				{
					break;
				}
			}
		}

		if (listOut.Count == 0)	// 결과 없음
		{
			return Results.Text("", TEXT_PLAIN);
		}

		ProcessLogLine	kNewest	= listOut[0];
		ProcessLogLine	kOldest	= listOut[listOut.Count - 1];
		StringBuilder	kText	= new StringBuilder();

		kText.Append($"#{kNewest.sStamp}:{kNewest.nOffset}|{kOldest.sStamp}:{kOldest.nOffset}\n");	// 첫 줄: 최신 커서|최과거 커서 (페이지 이전·다음 요청)

		foreach (ProcessLogLine kLine in listOut)
		{
			kText.Append(kLine.sLine).Append('\n');
		}

		return Results.Text(kText.ToString(), TEXT_PLAIN);
	}

	private static async Task<IResult> OnErrLog(HttpRequest kRequest)	// 에러 로그: Redis 에러 리스트 (Redis:sError + 존) offset 부터 X개
	{
		long	nOffset	= long.TryParse(GetQuery(kRequest, "offset"), out long nValue) ? Math.Max(0, nValue) : 0;	// 시작 위치 (0: 최신)

		RedisValue[]	arrValue	= await Redis.GetDb().ListRangeAsync(GetErrorKey(), nOffset, nOffset + PAGE_LIMIT - 1);

		return Results.Text(string.Join("\n", arrValue.Select(kValue => kValue.ToString())), TEXT_PLAIN);
	}

	private static async Task<IResult> OnSet(HttpContext kContext, bool bVersioned)	// 값 저장 (등록·수정: 버전 검증·증가, On·Off·Down: version 없음 → 제외)
	{
		IResult?	kStale	= bVersioned ? CheckVersion(kContext) : null;

		if (kStale != null)	// 버전 불일치: 다른 곳에서 변경
		{
			return kStale;
		}

		string	sField	= GetQuery(kContext.Request, "field");
		string	sValue	= GetQuery(kContext.Request, "value");
		string	sLog	= GetQuery(kContext.Request, "log");	// 동작 구분 (toggle·add·edit)

		if (sField == "")	// 파라미터 검사 및 조기 반환
		{
			return Results.Json(new { success = false, error = "field is required" });
		}

		if ((sLog == "add" || sLog == "edit") && IsBadField(sField))	// 등록·수정 새 키: 금지 글자
		{
			return Results.Json(new { success = false, error = "':'·'|' 사용 불가: " + sField });
		}

		await Redis.GetDb().StringSetAsync(GetListPrefix() + sField, sValue);

		if (sLog == "add")
		{
			ProcessLog.Write(INFO_, $"등록: {sField} → [{sValue}] ({GetUser(kContext)})");
		}
		else if (sLog == "edit")
		{
			ProcessLog.Write(INFO_, $"수정: {sField} → [{sValue}] ({GetUser(kContext)})");
		}
		else	// On·Off 버튼 등 (아이디@IP: 전체·그룹·개별 클릭 로그)
		{
			ProcessLog.Write(INFO_, $"액션: {sField} → [{GetState(sValue)}]");
		}

		int	nVersion	= bVersioned ? ProcessVersion.Up() : ProcessVersion.Get();	// 등록·수정 성공: 버전 +1

		return Results.Json(new { success = true, field = sField, value = sValue, version = nVersion });
	}

	private static async Task<IResult> OnRename(HttpContext kContext, bool bVersioned)	// 이름 변경: field·newfield 쌍 여러 개 → 버전 검증 1번으로 일괄 (하나라도 어긋남: 전체 중단)
	{
		IResult?	kStale	= bVersioned ? CheckVersion(kContext) : null;

		if (kStale != null)	// 버전 불일치: 다른 곳에서 변경
		{
			return kStale;
		}

		(string[] arrField, string[] arrNewField) = await GetRenamePair(kContext.Request);

		if (arrField.Length == 0 || arrField.Length != arrNewField.Length || arrField.Contains("") || arrNewField.Contains(""))	// 파라미터 검사 및 조기 반환
		{
			return Results.Json(new { success = false, error = "field and newfield are required" });
		}

		string?	sBad	= arrNewField.FirstOrDefault(IsBadField);	// 새 이름: 금지 글자

		if (sBad != null)
		{
			return Results.Json(new { success = false, error = "':'·'|' 사용 불가: " + sBad });
		}

		IDatabase	kDb		= Redis.GetDb();
		string		sPrefix	= GetListPrefix();

		for (int nIndex = 0; nIndex < arrField.Length; ++nIndex)	// 미리 검사: 원래 키 있음·새 키 없음 (하나라도 어긋남: 아무것도 안 바꿈)
		{
			if (!await kDb.KeyExistsAsync(sPrefix + arrField[nIndex]))
			{
				return Results.Json(new { success = false, error = "존재하지 않는 Field 입니다: " + arrField[nIndex] });
			}

			if (await kDb.KeyExistsAsync(sPrefix + arrNewField[nIndex]))
			{
				return Results.Json(new { success = false, error = "이미 존재하는 Field 입니다: " + arrNewField[nIndex] });
			}
		}

		List<string>	listFail	= new List<string>();

		for (int nIndex = 0; nIndex < arrField.Length; ++nIndex)
		{
			bool	bRenamed	= await kDb.KeyRenameAsync(sPrefix + arrField[nIndex], sPrefix + arrNewField[nIndex], When.NotExists);	// RENAMENX

			if (!bRenamed)	// 미리 검사 뒤 다른 곳에서 생성
			{
				listFail.Add(arrNewField[nIndex]);

				continue;
			}

			ProcessLog.Write(INFO_, $"이름: {arrField[nIndex]} → [{arrNewField[nIndex]}] ({GetUser(kContext)})");
		}

		int	nVersion	= bVersioned ? ProcessVersion.Up() : ProcessVersion.Get();	// 이름 변경: 버전 +1 (일괄 1번)

		if (listFail.Count > 0)
		{
			return Results.Json(new { success = false, error = "이미 존재하는 Field 입니다: " + string.Join(", ", listFail), version = nVersion });
		}

		return Results.Json(new { success = true, field = arrNewField[0], version = nVersion });
	}

	private static async Task<(string[], string[])> GetRenamePair(HttpRequest kRequest)	// 이름 변경 쌍: POST 본문 JSON {arrField, arrNewField} (키 많음: 쿼리 길이 제한 넘음 → IIS 404) 또는 쿼리 field·newfield (예전 형식)
	{
		if (HttpMethods.IsPost(kRequest.Method) && kRequest.HasJsonContentType())
		{
			ProcessRenameParam?	kParam	= await kRequest.ReadFromJsonAsync<ProcessRenameParam>(kRequest.HttpContext.RequestAborted);

			return ((kParam?.arrField ?? Array.Empty<string?>()).Select(s => s ?? "").ToArray(), (kParam?.arrNewField ?? Array.Empty<string?>()).Select(s => s ?? "").ToArray());
		}

		return (kRequest.Query["field"].Select(s => s ?? "").ToArray(), kRequest.Query["newfield"].Select(s => s ?? "").ToArray());
	}

	private static async Task<IResult> OnDel(HttpContext kContext, bool bVersioned)	// 서버 삭제 (삭제 전 값: 로그)
	{
		IResult?	kStale	= bVersioned ? CheckVersion(kContext) : null;

		if (kStale != null)	// 버전 불일치: 다른 곳에서 변경
		{
			return kStale;
		}

		string	sField	= GetQuery(kContext.Request, "field");

		if (sField == "")	// 파라미터 검사 및 조기 반환
		{
			return Results.Json(new { success = false, error = "field is required" });
		}

		IDatabase	kDb		= Redis.GetDb();
		string		sKey	= GetListPrefix() + sField;
		string?		sOld	= null;	// 삭제 전 값 (String 아닌 키: 없음)

		try
		{
			sOld = await kDb.StringGetAsync(sKey);
		}
		catch (RedisServerException)	// WRONGTYPE 등: 삭제는 계속
		{
			sOld = null;
		}

		if (!await kDb.KeyDeleteAsync(sKey))	// 키 없음
		{
			return Results.Json(new { success = false, error = "존재하지 않는 String 입니다: " + sField });
		}

		int	nVersion	= bVersioned ? ProcessVersion.Up() : ProcessVersion.Get();	// 삭제 성공: 버전 +1

		ProcessLog.Write(INFO_, $"삭제: {sField} → [{sOld}] ({GetUser(kContext)})");

		return Results.Json(new { success = true, field = sField, version = nVersion });
	}

	private static async Task<IResult> OnList()	// 목록 조회: 접두사 키 전체 → "필드|값" 줄 (키 이름순)
	{
		string				sPrefix	= GetListPrefix();
		SortedSet<string>	setKey	= new SortedSet<string>(StringComparer.Ordinal);	// 키 이름순 + 중복 제거 (SCAN: 순서 미보장·중복 가능)

		await foreach (RedisKey kKey in Redis.GetServer().KeysAsync(Redis.GetDbNumber(), sPrefix + "*"))	// SCAN (KEYS 와 달리 Redis 블로킹 없음)
		{
			setKey.Add(kKey.ToString());
		}

		if (setKey.Count == 0)
		{
			return Results.Text($"'{sPrefix}*' 키가 존재하지 않습니다.\n", TEXT_PLAIN);
		}

		string[]		arrKey		= setKey.ToArray();
		RedisValue[]	arrValue	= await Redis.GetDb().StringGetAsync(arrKey.Select(sKey => (RedisKey)sKey).ToArray());	// MGET 일괄 (String 아닌 키·사이 삭제: null)
		StringBuilder	kText		= new StringBuilder();

		for (int nIndex = 0; nIndex < arrKey.Length; ++nIndex)
		{
			string	sField	= arrKey[nIndex].Substring(sPrefix.Length);	// 접두사 뺌

			if (sField == "" || sField.Contains(':') || arrValue[nIndex].IsNull)	// 빈 필드·다른 존 키 (존 없는 설정: 접두사 아래 존 키 섞임)·String 아닌 키·삭제된 키
			{
				continue;
			}

			kText.Append(sField).Append('|').Append(arrValue[nIndex].ToString()).Append('\n');
		}

		return Results.Text(kText.ToString(), TEXT_PLAIN);
	}

	private static string GetListPrefix()	// 서버 목록 키 접두사: 설정 Redis:sProcess + 존 + ":" (예: HMX:Process:Dev:, HMXClient Client.json Redis:sPrefix + 존 과 같게, 관리 sManager 와 따로)
	{
		return Redis.GetZonePrefix("Redis:sProcess", Config.GetString("sZone"), DEFAULT_PROCESS);
	}

	private static string GetErrorKey()	// 에러 로그 리스트 키: 설정 Redis:sError + 존 + ":" (예: HMX:Error:Dev:, HMXClient Client.json Redis:sError + 존 과 같게, LPUSH → index 0 최신)
	{
		return Redis.GetZonePrefix("Redis:sError", Config.GetString("sZone"), DEFAULT_ERROR);
	}

	private static IResult? CheckVersion(HttpContext kContext)	// 버전 일치 검사 (일치: null, 불일치: stale 응답)
	{
		int	nClient	= int.TryParse(GetQuery(kContext.Request, "version"), out int nValue) ? nValue : 0;
		int	nFile	= ProcessVersion.Get();

		if (nClient == nFile)
		{
			return null;
		}

		ProcessLog.Write(WARN_, $"버전 불일치: 클라 v.{nClient} / 서버 v.{nFile} ({GetUser(kContext)})");

		return Results.Json(new { success = false, stale = true, error = "버전 불일치", version = nFile });
	}

	private static bool IsBadField(string sField)	// 키 금지 글자: ':' (존 구분, 목록에서 다른 존 키로 빠짐)·'|' (목록 "필드|값" 구분)
	{
		return sField.IndexOfAny(new[] { ':', '|' }) >= 0;
	}

	private static bool IsChange(string sAction)	// 바꾸는 요청 (등록·수정·On·Off·Down·이름 변경·삭제·클릭 기록)
	{
		return sAction == "set" || sAction == "rename" || sAction == "del" || sAction == "actlog";
	}

	private static bool IsNavigate(HttpRequest kRequest)	// 문서 이동 (링크·주소창·폼) 또는 다른 사이트 요청 (화면 fetch: 같은 사이트 cors·same-origin)
	{
		return string.Equals(kRequest.Headers["Sec-Fetch-Mode"].ToString(), "navigate", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(kRequest.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase)
			|| kRequest.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
	}

	private static string GetErrorText(Exception kException)	// 오류 메시지 (Redis 진단 정보 등 긴 메시지: 앞부분만)
	{
		string	sText	= kException.Message;

		return sText.Length > 200 ? sText.Substring(0, 200) + "…" : sText;
	}

	private static string GetQuery(HttpRequest kRequest, string sKey)	// 쿼리 값 (없음: 빈 문자열, 중복 키: 마지막 값)
	{
		return kRequest.Query[sKey].LastOrDefault() ?? "";
	}

	private static string GetUser(HttpContext kContext)	// 로그 끝 '아이디@IP' (아이디: 인증 쿠키 이름, 로그인 안 씀: -)
	{
		return $"{kContext.User.Identity?.Name ?? "-"}@{Web.GetAddr(kContext)}";
	}

	private static string GetState(string sValue)	// 로그에 State (앞 4파트: 상태,적용,PID,커밋) 만
	{
		return string.Join(',', sValue.Split(',').Take(4));
	}
}

record ProcessLogLine(string sStamp, long nOffset, string sLine);	// 조회한 로그 줄 (커서 = 파일 스탬프 + 줄 시작 오프셋)

class ProcessRenameParam	// 이름 변경 요청 본문 (POST /api/process?action=rename, JSON)
{
	public string?[]?	arrField	{ get; set; }	// 기존 키
	public string?[]?	arrNewField	{ get; set; }	// 새 키 (arrField 와 같은 순서)
}

static class ProcessVersion	// 설정 버전: 기준 폴더 하위 data\프로젝트명.txt (등록·수정·삭제·이름 변경 성공: +1, 다른 곳 변경 감지)
{
	private static readonly object	m_lockFile	= new object();	// 버전 파일 읽기·쓰기 직렬화

	public static string GetPath()	// 버전 파일 경로 (예: Data\Server.txt)
	{
		return Path.Combine(GetBaseDir(), "data", GetProjectName() + ".txt");
	}

	public static int Get()	// 지금 버전 (파일 없음: 1 로 만듦, 읽기 실패·숫자 아님: 0)
	{
		lock (m_lockFile)
		{
			string	sPath	= GetPath();

			try
			{
				if (!File.Exists(sPath))
				{
					Directory.CreateDirectory(Path.GetDirectoryName(sPath)!);
					File.WriteAllText(sPath, "1");
				}

				if (int.TryParse(File.ReadAllText(sPath).Trim(), out int nVersion))
				{
					return nVersion;
				}

				Log.Write(ERROR, $"[v.0] 버전 파일 숫자 아님: {sPath}");	// ProcessLog 아님 (재귀 방지), [v.0]: 반영 로그 표시 (버전 모름)

				return 0;
			}
			catch (Exception kException)	// 읽기 실패 (권한 등)
			{
				Log.Write(ERROR, $"[v.0] 버전 파일 읽기 실패: {sPath} ({kException.Message})");	// ProcessLog 아님 (ProcessLog.Write 가 버전 조회 → 재귀 방지), [v.0]: 반영 로그 표시 (버전 모름)

				return 0;
			}
		}
	}

	public static int Up()	// 버전 +1 (반환: 새 버전)
	{
		int		nVersion	= 0;
		bool	bFail		= false;

		lock (m_lockFile)
		{
			nVersion = Get() + 1;

			if (nVersion == 1)	// 읽기 실패 (0): 쓰면 1 로 초기화 → 뒤 요청 모두 버전 불일치 → 안 씀
			{
				return 0;
			}

			try
			{
				string	sPath	= GetPath();

				File.WriteAllText(sPath + ".tmp", nVersion.ToString());	// 임시 파일 후 교체 (쓰는 중 종료: 원본 유지)
				File.Move(sPath + ".tmp", sPath, true);
			}
			catch (Exception)	// 쓰기 실패 (권한 등)
			{
				bFail = true;
			}
		}

		if (bFail)	// 잠금 밖에서 로그 (로그도 버전 조회 → 잠금 순서 꼬임 방지)
		{
			ProcessLog.Write(ERROR, $"버전 파일 쓰기 실패: {GetPath()} (v.{nVersion})");

			return nVersion - 1;	// 저장 안 된 버전 반환 → 다음 요청 버전 불일치로 막힘 → 파일 값 그대로
		}

		return nVersion;
	}
}

static class ProcessLog	// 반영 로그: 공통 Log 파일 (log/YYYYMMDD/프로젝트명_YYYYMMDD_HH.log) 에 레벨 뒤 [v.버전] 표시로 기록·조회 (예: [2026/06/18 09:36:17.123] [HMX_][INFO_] [v.1] 등록: ..., [HMX_]: 공통 sTitle 태그), 조회: [v.버전] 줄·로그인 줄·예전 [HMX_] 태그 줄만
{
	private const int	READ_CHUNK	= 131072;	// 역읽기 조각 (바이트)
	private const int	READ_BUFFER	= 65536;	// 정방향 읽기 버퍼 (바이트)
	private const int	TIME_LENGTH	= 25;		// 줄 앞 시각 글자 수 ([yyyy/MM/dd HH:mm:ss.fff])
	private const int	TIME_GRACE	= 60;		// 범위 끝 판정 여유 (초, 여러 스레드 기록 → 시각 순서 조금 어긋남)

	private static readonly Regex	m_kTime		= new Regex(@"^\[(\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2})\.\d{3}\]", RegexOptions.Compiled);	// 줄 앞 시각
	private static readonly Regex	m_kProcess	= new Regex(@"^\[[^\]]+\] \S*?\[\w+\] (\[v\.\d+\]|\[HMX_\]|로그인 (성공|실패))", RegexOptions.Compiled);	// 반영 로그 줄: 레벨 바로 뒤 [v.버전]·공통 Auth 로그인·예전 [HMX_] 태그 (레벨 앞 sTitle 태그: 건너뜀, 본문 안 글자: 아님)

	public static void Write(string sLevel, string sMessage)	// 로그 한 줄: [v.버전] 표시 + 메시지 (화면 로그 조회: 표시로 실행 동작 구분)
	{
		int	nVersion	= ProcessVersion.Get();	// 지금 버전 (Log 잠금 밖에서 조회)

		Log.Write(sLevel, $"[v.{nVersion}] {sMessage}");
	}

	private static bool IsProcessLine(string sLine)	// 반영 로그 줄: [v.버전]·로그인·예전 [HMX_] 태그 (그 밖 서버 로그: 뺌)
	{
		return m_kProcess.IsMatch(sLine);
	}

	private static bool IsProcessBytes(ReadOnlySpan<byte> kLine)	// 반영 로그 줄 후보 (전체 디코드 전 바이트로 거름)
	{
		return kLine.IndexOf("] [v."u8) >= 0 || kLine.IndexOf("] [HMX_]"u8) >= 0 || kLine.IndexOf("] 로그인 "u8) >= 0;
	}

	private static bool TryLineTime(string sLine, out DateTime kTime)	// 줄 앞 시각 (형식 이상: false)
	{
		kTime = default;

		Match	kMatch	= m_kTime.Match(sLine);

		if (!kMatch.Success)
		{
			return false;
		}

		return DateTime.TryParseExact(kMatch.Groups[1].Value, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out kTime);
	}

	// 파일 [0, nEndOffset) 뒤에서부터 조각 단위 역읽기 → 범위 안 실행 줄 최신 순 (필요분만 읽음)
	// 반환: 범위보다 과거 도달 (true: 이전 파일도 모두 과거 → 중단)
	public static bool CollectBackward(string sStamp, long nEndOffset, int nNeed, DateTime kStart, DateTime kEnd, List<ProcessLogLine> listOut)
	{
		try
		{
			using FileStream kFile = new FileStream(Log.GetFile(sStamp), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

			long	nBufStart	= nEndOffset < 0 ? kFile.Length : nEndOffset;	// 버퍼가 담는 파일 구간 시작 (-1: 파일 끝부터)
			byte[]	arrBuf		= Array.Empty<byte>();
			int		nBufLen		= 0;	// 버퍼 유효 길이 (파일 [nBufStart, nBufStart + nBufLen))

			while (nNeed > 0)
			{
				int	nCut	= nBufLen;	// 버퍼 끝 개행 (\r\n) 뺀 길이

				if (nCut > 0 && arrBuf[nCut - 1] == '\n')
				{
					--nCut;
				}

				if (nCut > 0 && arrBuf[nCut - 1] == '\r')
				{
					--nCut;
				}

				int	nNewLine	= arrBuf.AsSpan(0, nCut).LastIndexOf((byte)'\n');	// 마지막 줄 앞 개행

				if (nNewLine < 0 && nBufStart > 0)	// 줄 잘림: 앞 조각 더 읽음
				{
					int		nRead	= (int)Math.Min(READ_CHUNK, nBufStart);
					byte[]	arrNew	= new byte[nRead + nBufLen];

					nBufStart -= nRead;

					kFile.Seek(nBufStart, SeekOrigin.Begin);
					kFile.ReadExactly(arrNew, 0, nRead);

					Array.Copy(arrBuf, 0, arrNew, nRead, nBufLen);

					arrBuf	= arrNew;
					nBufLen	+= nRead;

					continue;
				}

				if (nCut <= 0)	// 파일 앞까지 다 읽음
				{
					break;
				}

				int		nLineStart	= nNewLine + 1;				// 버퍼 안 줄 시작 (개행 없음: 0)
				int		nLineLen	= nCut - nLineStart;
				long	nOffset		= nBufStart + nLineStart;	// 파일 기준 줄 시작 (커서)
				string	sHead		= Encoding.ASCII.GetString(arrBuf, nLineStart, Math.Min(nLineLen, TIME_LENGTH));	// 줄 앞 시각만 (전체 디코드: 실행 줄만)

				nBufLen = nLineStart;	// 처리한 줄 뺌 (앞 줄 개행까지 유지, 버퍼 내용: 그대로)

				if (!TryLineTime(sHead, out DateTime kTime) || kTime > kEnd)	// 형식 이상·범위보다 미래
				{
					continue;
				}

				if (kTime < kStart)	// 범위보다 과거: 여유 넘음 → 파일 앞·이전 파일 모두 과거 → 중단 (여유 안: 순서 어긋난 줄 → 건너뜀)
				{
					if (kTime < kStart.AddSeconds(-TIME_GRACE))
					{
						return true;
					}

					continue;
				}

				if (!IsProcessBytes(arrBuf.AsSpan(nLineStart, nLineLen)))	// 반영 로그 아님 (디코드 전)
				{
					continue;
				}

				string	sLine	= Encoding.UTF8.GetString(arrBuf, nLineStart, nLineLen);

				if (!IsProcessLine(sLine))	// 반영 로그 아님
				{
					continue;
				}

				listOut.Add(new ProcessLogLine(sStamp, nOffset, sLine));

				--nNeed;
			}
		}
		catch (Exception kException)	// 파일 열기·읽기 실패: 모은 데까지
		{
			Write(WARN_, $"로그 읽기 실패: {sStamp} ({kException.Message})");
		}

		return false;
	}

	// 파일 nSeekOffset 부터 정방향 → 범위 안 실행 줄 (bSkipFirst: 커서 줄 건너뜀)
	// 반환: 범위보다 미래 도달 (true: 다음 파일도 모두 미래 → 중단)
	public static bool CollectForward(string sStamp, long nSeekOffset, bool bSkipFirst, int nNeed, DateTime kStart, DateTime kEnd, List<ProcessLogLine> listOut)
	{
		try
		{
			using FileStream kFile = new FileStream(Log.GetFile(sStamp), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, READ_BUFFER);

			kFile.Seek(nSeekOffset, SeekOrigin.Begin);

			if (bSkipFirst)
			{
				ReadLine(kFile);
			}

			while (nNeed > 0)
			{
				long	nOffset	= kFile.Position;	// 줄 시작 (커서)
				string?	sLine	= ReadLine(kFile);

				if (sLine == null)	// 파일 끝
				{
					break;
				}

				if (!TryLineTime(sLine, out DateTime kTime) || kTime < kStart)	// 형식 이상·범위보다 과거
				{
					continue;
				}

				if (kTime > kEnd)	// 범위보다 미래: 여유 넘음 → 파일 뒤·다음 파일 모두 미래 → 중단 (여유 안: 순서 어긋난 줄 → 건너뜀)
				{
					if (kTime > kEnd.AddSeconds(TIME_GRACE))
					{
						return true;
					}

					continue;
				}

				if (!IsProcessLine(sLine))	// 반영 로그 아님
				{
					continue;
				}

				listOut.Add(new ProcessLogLine(sStamp, nOffset, sLine));

				--nNeed;
			}
		}
		catch (Exception kException)	// 파일 열기·읽기 실패: 모은 데까지
		{
			Write(WARN_, $"로그 읽기 실패: {sStamp} ({kException.Message})");
		}

		return false;
	}

	private static string? ReadLine(FileStream kFile)	// 지금 위치에서 한 줄 (\r\n 뺌, 파일 끝: null)
	{
		int	nByte	= kFile.ReadByte();

		if (nByte < 0)
		{
			return null;
		}

		List<byte>	listByte	= new List<byte>();

		while (nByte >= 0 && nByte != '\n')
		{
			listByte.Add((byte)nByte);

			nByte = kFile.ReadByte();
		}

		return Encoding.UTF8.GetString(listByte.ToArray()).TrimEnd('\r');
	}
}

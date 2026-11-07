static class Auth	// 웹 로그인 (설정 bAuth): 쿠키 인증 등록·로그인 검사·로그인 API·로그인 페이지, 계정 (관리 계정 + aAccount)·계정 메뉴 검사, 외부 API 토큰 검사
{
	private const string	CLAIM_STAMP	= "sStamp";	// 인증 쿠키 항목: 계정 해시 (계정 변경: 기존 쿠키 무효)

	public const string	LOGIN_PATH	= "/api/login";	// 로그인 API (미인증 허용)

	private const string	LOGIN_ERROR	= "계정 또는 비번 오류";	// 로그인 창 오류 문구 (모든 실패 같음, 사유: 로그만)

	public const string	LOGOUT_PATH		= "/api/logout";	// 로그아웃 API
	public const string	ACCOUNT_PATH	= "/api/account";	// 계정 API (계정 팝업 목록·추가·수정·삭제)
	public const string	MENU_LOG		= "로그";			// 계정 aAPI 로그 수신 권한 (외부 API /api/external/log/키/토큰)
	public const string	API_QUERY		= "쿼리";			// 계정 aAPI 쿼리 권한 (외부 API /api/external/query/키/토큰, 메일 링크 로그인)
	public const string	API_SEARCH		= "서치";			// 계정 aAPI 서치 권한 (외부 API /api/external/search/키/토큰)

	private const int	m_nAuthDay			= 365;	// 인증 유지 (일, 절반 지나 접속 시 연장)
	private const int	m_nAccountIDMax		= 50;	// 계정 아이디 최대 글자
	private const int	m_nAccountPassMax	= 100;	// 계정 비밀번호 최대 글자
	private const int	m_nAccountMailMax	= 254;	// 계정 메일 최대 글자
	private const int	m_nAccountPhoneMax	= 20;	// 계정 전화 번호 최대 글자

	private static readonly object	m_kAccountLock	= new object();	// 계정 추가·수정·삭제 1건씩 (설정 파일 쓰기)
	private static bool				m_bAuth;						// 로그인 사용 (설정 bAuth, 시작 때)
	private static readonly object	m_kUnlockLock	= new object();	// 마스터 키 입력 1건씩 (설정 다시 로딩)

	private const int	m_nKeyLength	= 20;	// 계정 키 sKey 글자 수 (외부 API 경로 키, 페이지 ACCOUNT_KEY_LENGTH 와 같음)

	private static readonly object					m_kKeyLock		= new object();													// 키 맵 다시 만들기 1건씩
	private static int								m_nKeyVersion	= -1;															// 키 맵 만든 설정 버전 (다름: 다시 만듦)
	private static Dictionary<string, KeyAccount>	m_hmapKey		= new Dictionary<string, KeyAccount>(StringComparer.Ordinal);	// 외부 API 키 맵: 계정 sKey → 계정 (GetKeyMap)
	private static readonly string[]				m_arrApiMenu	= { API_QUERY, API_SEARCH, MENU_LOG };							// 계정 aAPI 고를 외부 API 권한 (계정 팝업 외부 줄, 이 순서)

	private static readonly (string sPath, string[] arrTab)[]	m_arrMenuPath	=	// 라우트 → 탭 (계정 aMenu 검사, 앞쪽 우선, 없음: 공통 → 통과)
	{
		("/api/files/download",	new[] { Main.TAB_SEARCH, Main.TAB_FILE }),	// 서치·파일 공용
		("/api/files",			new[] { Main.TAB_FILE }),
		("/api/view",			new[] { Main.TAB_SEARCH, Main.TAB_FILE }),	// 서치·파일 공용
		("/view",				new[] { Main.TAB_SEARCH, Main.TAB_FILE }),
		("/api/search",			new[] { Main.TAB_SEARCH }),
		("/api/progress",		new[] { Main.TAB_SEARCH }),
		("/api/stop",			new[] { Main.TAB_SEARCH }),
		("/api/query",			new[] { Main.TAB_QUERY }),
		("/query/field",		new[] { Main.TAB_QUERY }),
		("/api/alarm",			new[] { Main.TAB_QUERY }),
		("/api/field",			new[] { Main.TAB_QUERY }),
		("/api/dump",			new[] { Main.TAB_DUMP }),
		("/api/manager",		new[] { Main.TAB_MANAGER }),
		("/api/process",		new[] { Main.TAB_PROCESS }),
		("/api/client",			new[] { Main.TAB_QUERY, Main.TAB_SEARCH, Main.TAB_FILE, Main.TAB_DUMP }),	// 접속 클라 목록: 클라 쓰는 탭 공용
	};

	public static void AddService(WebApplicationBuilder kBuilder)	// 인증 서비스 등록 (Web.StartAsync): 쿠키 암호화 키 (메모리)·쿠키 인증
	{
		kBuilder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();	// 쿠키 암호화 키: 메모리 (서버 재시작: 기존 쿠키 무효)
		kBuilder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(kOption =>
		{
			kOption.ExpireTimeSpan		= TimeSpan.FromDays(m_nAuthDay);
			kOption.SlidingExpiration	= true;								// 절반 지나 접속 시 연장
			kOption.Cookie.Name			= GetProjectName() + Config.GetString("sZone") + ".Auth";
			kOption.Cookie.HttpOnly		= true;
			kOption.Cookie.IsEssential	= true;
			kOption.Cookie.SameSite		= SameSiteMode.Lax;		// 다른 사이트 링크 GET: 전송 (메신저 링크 재로그인 방지), POST: 미전송 (CSRF 완화)
		});
	}

	public static void Use(WebApplication WebApp)	// 미인증 차단 (Web.StartAsync, 정적 파일·라우트보다 먼저)
	{
		WebApp.UseAuthentication();	// 절반 지난 쿠키 연장

		UseLogin(WebApp);
	}

	public static void UseMenu(WebApplication WebApp)	// 계정 메뉴 검사 (Web.StartAsync, 라우팅 뒤): 탭 라우트 → 계정 메뉴에 그 탭 없음: 403 (공통 라우트·관리 계정: 통과)
	{
		WebApp.Use(async (HttpContext kContext, RequestDelegate funcNext) =>
		{
			string[]?	arrTab	= GetRouteTab((kContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "");	// 그 라우트 탭 (공통: null)

			if (arrTab != null && !GetMenu(kContext).Any(arrTab.Contains))
			{
				Log.Write(WARN_, $"메뉴 권한 없음: {kContext.User.Identity?.Name ?? ""} {kContext.Request.Path.Value} ({Web.GetAddr(kContext)})");

				kContext.Response.StatusCode = StatusCodes.Status403Forbidden;

				await kContext.Response.WriteAsJsonAsync(new { success = false, sError = "메뉴 권한 없음" });

				return;
			}

			await funcNext(kContext);
		});
	}

	public static void MapRoute(WebApplication WebApp, bool bAuth)	// 라우트 등록 (Main.MapRoute): 계정 API (늘), 로그인·로그아웃 API (bAuth)
	{
		m_bAuth = bAuth;

		WebApp.MapGet(ACCOUNT_PATH, OnAccountList);						// 계정 목록 (관리 계정 + aAccount, 비밀번호 빼고)
		WebApp.MapPost(ACCOUNT_PATH, OnAccountSave);					// 계정 추가·수정 (관리 계정·관리 비밀번호, 본문 JSON)
		WebApp.MapPost(ACCOUNT_PATH + "/delete", OnAccountDelete);		// 계정 삭제 (관리 계정·관리 비밀번호, 본문 JSON)
		WebApp.MapPost(ACCOUNT_PATH + "/key", OnAccountKey);			// 계정 외부 API 키 새로 발급 (관리 계정·관리 비밀번호, 본문 JSON sID·sAdmin)

		if (bAuth)
		{
			WebApp.MapPost(LOGIN_PATH, OnLogin);
			WebApp.MapPost(LOGOUT_PATH, OnLogout);
		}
	}

	public static string GetUI(HttpContext kContext)	// 페이지 UI (body data-ui): 관리 계정 "" (모든 메뉴 보임, "account": 제목 + 계정·새로 고침 버튼만 → 지금 안 씀), 다른 계정 "user" (계정 버튼 숨김), 로그인 안 씀 ""
	{
		if (!m_bAuth)
		{
			return "";
		}

		return FindAccount(kContext.User.Identity?.Name ?? "")?.bMain == true ? "" : "user";
	}

	public static string[] GetMenu(HttpContext kContext)	// 요청 계정 메뉴 (켜진 탭 중 계정 aMenu, 관리 계정·로그인 안 씀: 켜진 탭 전체)
	{
		return m_bAuth ? FindAccount(kContext.User.Identity?.Name ?? "")?.arrMenu ?? Array.Empty<string>() : Main.GetMenu();
	}

	public static void LogAccount()	// 시작 로그: 로그인 계정 수 (관리 계정 + aAccount), 빠진 aAccount 항목 (빈 값·복호화 실패·겹침) 경고
	{
		List<AccountInfo>	listAccount	= GetAccountList();
		int					nRaw		= Config.GetChildren("aAccount").Count();	// 설정 aAccount 항목 수

		if (listAccount.Count == 0)
		{
			Log.Write(WARN_, "로그인 계정 없음 (sID·sCode, Account.json aAccount): 모든 로그인 차단");

			return;
		}

		if (nRaw != listAccount.Count(kAccount => !kAccount.bMain))
		{
			Log.Write(WARN_, $"계정 설정 오류: aAccount {nRaw - listAccount.Count(kAccount => !kAccount.bMain)}개 뺌 (sID·sCode 없음·복호화 실패·아이디 겹침)");
		}

		Log.Write(INFO_, $"로그인: 사용 (설정 bAuth 가 true, 계정 {listAccount.Count}개)");
	}

	private static void UseLogin(WebApplication WebApp)	// 로그인 검사: 쿠키 없음·무효 차단 (페이지 요청: 로그인 페이지, 그 외: 401 JSON, 로그인 API: 통과, 외부 API: 그 API 권한 계정 토큰 일치만 (로그인 무관))
	{
		WebApp.Use(async (HttpContext kContext, RequestDelegate funcNext) =>
		{
			if (IsExternalPath(kContext) && GetMasterKey() == "")	// 마스터 키 입력 전: 첫 접속 계정만 → 외부 API 안 받음 (503 → 보낸 쪽 다시)
			{
				kContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

				await kContext.Response.WriteAsJsonAsync(new { sError = GetMasterError(), nLine = 0 });

				return;
			}

			if (IsExternalPath(kContext) ? IsExternalToken(kContext) : IsLogin(kContext) || string.Equals(kContext.Request.Path.Value, LOGIN_PATH, StringComparison.OrdinalIgnoreCase))
			{
				await funcNext(kContext);

				return;
			}

			if (IsExternalPath(kContext))	// 외부 API 토큰 불일치: 응답 지연 (무차별 대입 완화)
			{
				Log.Write(WARN_, $"외부 API 토큰 불일치: {GetLogPath(kContext)} ({Web.GetAddr(kContext)})");

				await Task.Delay(1000);
			}

			kContext.Response.StatusCode			= StatusCodes.Status401Unauthorized;
			kContext.Response.Headers.CacheControl	= "no-store";	// 로그인 페이지 캐시 방지 (로그인 후 새로고침: 요청 페이지)

			if (HttpMethods.IsGet(kContext.Request.Method) && kContext.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))	// 브라우저 페이지 요청 (주소창·링크)
			{
				kContext.Response.ContentType = "text/html; charset=utf-8";

				await kContext.Response.WriteAsync(LoginHtml());

				return;
			}

			await kContext.Response.WriteAsJsonAsync(new { success = false, error = "인증이 필요합니다." });	// API·정적 파일 등
		});
	}

	private static bool IsLogin(HttpContext kContext)	// 인증 쿠키 유효: 쿠키 아이디 계정 있음 + 발급 당시 계정 표시 = 지금 계정 표시 (비밀번호 바뀜·계정 삭제: false) + 접근 메뉴 있음
	{
		AccountInfo?	kAccount	= FindAccount(kContext.User.Identity?.Name ?? "");

		return kAccount != null && kAccount.arrMenu.Length > 0 && IsSame(kContext.User.FindFirst(CLAIM_STAMP)?.Value ?? "", GetStamp(kAccount));
	}

	public static bool IsWait()	// 마스터 키 입력 대기: 로그인 사용 + 서버 오픈 뒤 첫 접속 전 (Redis 연결·메일 인증 안 함)
	{
		return GetMasterKey() == "" && Config.GetBool("bWeb") && Config.GetBool("bAuth");
	}

	public static bool IsExternalPath(HttpContext kContext)	// 외부 API 경로 (/api/external/..., /api/external 도: 목록에 없음 → 토큰 불일치)
	{
		string	sPath	= kContext.Request.Path.Value ?? "";

		return sPath.StartsWith(API.EXTERNAL_PATH, StringComparison.OrdinalIgnoreCase) || sPath.Equals(API.EXTERNAL_PATH.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
	}

	public static string GetLogPath(HttpContext kContext)	// 요청 로그 경로 (외부 API: 토큰 ****)
	{
		if (!IsExternalPath(kContext))
		{
			return kContext.Request.Path.ToString();
		}

		int	nIndex	= FindExternal(kContext);

		return (nIndex < 0 ? API.EXTERNAL_PATH : API.m_arrExternal[nIndex].sPath) + "****";
	}

	private static int FindExternal(HttpContext kContext)	// 경로 맞는 외부 API 목록 번호 (없음: -1)
	{
		string	sPath	= kContext.Request.Path.Value ?? "";

		return Array.FindIndex(API.m_arrExternal, kApi => sPath.StartsWith(kApi.sPath, StringComparison.OrdinalIgnoreCase));
	}

	private static bool IsExternalToken(HttpContext kContext)	// 외부 API 키·토큰 맞음 (FindKeyAccount)
	{
		return FindKeyAccount(kContext) != null;
	}

	private static KeyAccount? FindKeyAccount(HttpContext kContext)	// 외부 API 계정: 목록 경로 뒤 "키/토큰" → 키 맵에서 계정 (관리 계정·aAccount) → 그 API 권한 (aAPI) 있음 + 토큰을 그 계정 sRand 키로 복호화 = 그 계정 sCode (끝 '/' 1개 허용, %2F: '/', 목록에 없음·없는 키·권한 없음·불일치: null)
	{
		int	nIndex	= FindExternal(kContext);

		if (nIndex < 0)
		{
			return null;
		}

		string	sRest	= (kContext.Request.Path.Value ?? "").Substring(API.m_arrExternal[nIndex].sPath.Length).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);	// 경로 뒤 키/토큰 (없음: "", 경로 '/' 인코딩: 되돌림)
		int		nSlash	= sRest.IndexOf('/');							// 키 끝 (키: '/' 없음)
		string	sKey	= nSlash < 0 ? sRest : sRest[..nSlash];			// 계정 키 (sKey)
		string	sToken	= nSlash < 0 ? "" : sRest[(nSlash + 1)..];		// 토큰 ('/' 포함 가능)
		string	sTrim	= sToken.EndsWith('/') ? sToken[..^1] : sToken;	// 끝 '/' 뗀 토큰 (토큰 끝 '/': 원래 그대로도 확인)
		string	sMenu	= API.m_arrExternal[nIndex].sMenu;				// 그 API 권한 메뉴

		if (!GetKeyMap().TryGetValue(sKey, out KeyAccount? kAccount) || !(kAccount.bMain || kAccount.arrApi.Contains(sMenu)))	// 없는 키·권한 없음 (aAPI: 관리 계정 전체)
		{
			return null;
		}

		bool	bMatch	= IsSame(AES.Decrypt(FromUrlToken(sToken), kAccount.sRand) ?? "", kAccount.sCode) | IsSame(AES.Decrypt(FromUrlToken(sTrim), kAccount.sRand) ?? "", kAccount.sCode);	// URL 용·예전 표준 형식 모두, 복호화 실패: "" → 불일치

		return bMatch ? kAccount : null;
	}

	public static async Task<bool> SignInByKey(HttpContext kContext)	// 외부 API 키·토큰 계정으로 로그인 (메일 링크 브라우저 열기: 인증 쿠키 발급, 로그인 안 씀·이미 그 계정: 그대로, 키·토큰 불일치·접근 탭 없음: false)
	{
		if (!m_bAuth)
		{
			return true;
		}

		KeyAccount?		kKey		= FindKeyAccount(kContext);
		AccountInfo?	kAccount	= kKey == null ? null : FindAccount(kKey.sID);	// 로그인 계정 (sCode 복호화 sPass)

		if (kAccount == null || kAccount.arrMenu.Length == 0)
		{
			return false;
		}

		if (IsLogin(kContext) && kContext.User.Identity?.Name == kAccount.sID)	// 이미 그 계정 로그인
		{
			return true;
		}

		ClaimsIdentity	kIdentity	= new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, kAccount.sID), new Claim(CLAIM_STAMP, GetStamp(kAccount)) }, CookieAuthenticationDefaults.AuthenticationScheme);	// 아이디 + 계정 표시 (OnLogin 과 같음)

		await kContext.SignInAsync(new ClaimsPrincipal(kIdentity));

		Log.Write(INFO_, $"링크 로그인: {kAccount.sID} ({Web.GetAddr(kContext)})");

		return true;
	}

	private static Dictionary<string, KeyAccount> GetKeyMap()	// 외부 API 키 맵: 계정 sKey → 계정 (메모리, 관리 계정 sKey (Account.json 최상위) + aAccount sKey, 키·sRand 없음·빈 값·아이디·키 겹침: 뺌, 설정 다시 로딩: 다시 만듦)
	{
		lock (m_kKeyLock)
		{
			if (m_nKeyVersion == Config.GetVersion())
			{
				return m_hmapKey;
			}

			Dictionary<string, KeyAccount>	hmapKey		= new Dictionary<string, KeyAccount>(StringComparer.Ordinal);
			string							sMainID		= Config.GetString("sID");
			string							sMainCode	= Config.GetString("sCode");
			string							sMainKey	= Config.GetString("sKey");
			string							sMainRand	= Config.GetString("sRand");

			if (sMainID != "" && sMainCode != "" && sMainKey != "" && sMainRand != "")
			{
				hmapKey[sMainKey] = new KeyAccount(sMainID, sMainCode, sMainRand, Array.Empty<string>(), true);
			}

			foreach (IConfigurationSection kSection in Config.GetChildren("aAccount"))
			{
				string	sID		= kSection["sID"]?.Trim() ?? "";
				string	sCode	= kSection["sCode"] ?? "";
				string	sKey	= kSection["sKey"] ?? "";
				string	sRand	= kSection["sRand"] ?? "";

				if (sID != "" && sCode != "" && sKey != "" && sRand != "" && !string.Equals(sID, sMainID, StringComparison.OrdinalIgnoreCase) && !hmapKey.ContainsKey(sKey))
				{
					hmapKey[sKey] = new KeyAccount(sID, sCode, sRand, GetConfigApi(kSection), false);
				}
			}

			m_hmapKey		= hmapKey;
			m_nKeyVersion	= Config.GetVersion();

			return hmapKey;
		}
	}

	private static string NewKey()	// 계정 키 sKey·sRand: 영문·숫자 X자리 무작위
	{
		return RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", m_nKeyLength);
	}

	public static string GetToken(string sRand, string sCode)	// 외부 API 토큰 (Account.json sToken 저장 형식): sCode 를 그 계정 sRand 키로 암호화 = TO_BASE64(AES_ENCRYPT(sCode, sRand)) 표준 base64 (+·/·=), sRand·sCode 없음: ""
	{
		return sRand == "" || sCode == "" ? "" : AES.Encrypt(sCode, sRand);
	}

	public static string GetUrlToken(string sRand, string sCode)	// 외부 API 토큰 (보이는 곳·링크 형식: 계정 팝업·메일·훅·외부 호출): GetToken 의 + → -, / → _, 끝 = 뺌 (IIS 이중 이스케이프 거부 (404.11) 피함, 받을 때: 두 형식 다)
	{
		return GetToken(sRand, sCode).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	private static string FromUrlToken(string sToken)	// 받은 토큰 → 표준 base64 (URL 용: - → +, _ → /, = 채움, 예전 표준 형식: 그대로)
	{
		string	sBase	= sToken.Replace('-', '+').Replace('_', '/');	// URL 용 글자 되돌림

		return sBase.Length % 4 == 0 ? sBase : sBase.PadRight(sBase.Length + 4 - sBase.Length % 4, '=');	// 뺀 = 채움
	}

	public static string GetMainPath()	// 관리 계정 외부 API "키/토큰" (알람 메일·훅 쿼리 링크, 관리 계정·키·sRand 없음: "")
	{
		string	sMainKey	= Config.GetString("sKey");
		string	sMainToken	= GetUrlToken(Config.GetString("sRand"), Config.GetString("sCode"));	// 훅 링크: URL 용 (sRand·sCode 없음: "")

		return Config.GetString("sID") != "" && sMainKey != "" && sMainToken != "" ? sMainKey + "/" + sMainToken : "";
	}

	public static (string sAddr, string sPath) GetMailTarget(string sTarget)	// 알람 메일 받는 곳 → 메일 주소·외부 API "키/토큰" (계정 아이디: 그 계정 sMail·키/토큰 (sKey·sRand 없음: 경로 ""), 메일 주소 (예전 형식): 그대로·경로 "", 메일 없는 계정·없는 계정: "")
	{
		if (sTarget.Contains('@'))	// 예전 형식: 메일 주소
		{
			return (sTarget, "");
		}

		IConfigurationSection?	kSection	= Config.GetChildren("aAccount").FirstOrDefault(kItem => string.Equals(kItem["sID"]?.Trim(), sTarget, StringComparison.OrdinalIgnoreCase));	// 그 계정
		string					sMail		= kSection?["sMail"] ?? "";
		string					sKey		= kSection?["sKey"] ?? "";
		string					sToken		= GetUrlToken(kSection?["sRand"] ?? "", kSection?["sCode"] ?? "");	// 메일 링크: URL 용 (sRand·sCode 없음: "")

		return (sMail, sMail != "" && sKey != "" && sToken != "" ? sKey + "/" + sToken : "");
	}

	public static object[] GetAlarmAccounts()	// 알람 받는 계정 고르기 목록 [{sID, sMail, sPhone}] (aAccount 전체, 설정 순서, 메일: sMail 있는 계정만·SMS: sPhone 있는 계정만)
	{
		return Config.GetChildren("aAccount").Select(kSection => (sID: kSection["sID"]?.Trim() ?? "", sMail: kSection["sMail"] ?? "", sPhone: kSection["sPhone"] ?? "")).Where(kItem => kItem.sID != "").Select(kItem => (object)new { kItem.sID, kItem.sMail, kItem.sPhone }).ToArray();
	}

	public static bool IsAccount(string sID)	// aAccount 계정 있음 (대소문자 무시, 알람 SMS 받는 계정 검사)
	{
		return Config.GetChildren("aAccount").Any(kSection => string.Equals(kSection["sID"]?.Trim(), sID, StringComparison.OrdinalIgnoreCase));
	}

	public static bool HasPhone(string sID)	// aAccount 계정 전화 번호 있음 (대소문자 무시, 알람 SMS 받는 계정 검사)
	{
		return Config.GetChildren("aAccount").Any(kSection => string.Equals(kSection["sID"]?.Trim(), sID, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(kSection["sPhone"]));
	}

	private static bool IsMail(string sMail)	// 메일 형식 (알람 메일과 같음)
	{
		return Regex.IsMatch(sMail, @"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$");
	}

	private static string GetStamp(AccountInfo kAccount)	// 계정 표시: 아이디·비밀번호 해시 (비밀번호 바뀜: 기존 쿠키 무효)
	{
		return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(kAccount.sID + "\n" + kAccount.sPass)));
	}

	private static List<AccountInfo> GetAccountList()	// 로그인 계정: 관리 계정 (설정 sID·sCode, 켜진 탭 전체) + aAccount (Account.json sID·sCode·aMenu, 빈 값·복호화 실패·아이디 겹침: 뺌)
	{
		List<AccountInfo>	listAccount	= new List<AccountInfo>();
		string				sMainID		= Config.GetString("sID");
		string				sMainPass	= Config.GetString("sPass");	// sCode 복호화 값

		if (sMainID != "" && sMainPass != "")
		{
			listAccount.Add(new AccountInfo(sMainID, sMainPass, Array.Empty<string>(), Main.GetMenu(), true));
		}

		foreach (IConfigurationSection kSection in Config.GetChildren("aAccount"))
		{
			string	sID		= kSection["sID"]?.Trim() ?? "";
			string	sPass	= kSection["sPass"] ?? "";	// sCode 복호화 값 (실패: 없음)

			if (sID == "" || sPass == "" || listAccount.Any(kAccount => string.Equals(kAccount.sID, sID, StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}

			string[]	arrMenu	= GetConfigMenu(kSection);	// 설정 aMenu (없음: 전체)

			listAccount.Add(new AccountInfo(sID, sPass, arrMenu, arrMenu.Length == 0 ? Main.GetMenu() : Main.GetMenu().Where(arrMenu.Contains).ToArray(), false));
		}

		return listAccount;
	}

	private static AccountInfo? FindAccount(string sID)	// 아이디 계정 (없음: null)
	{
		return sID == "" ? null : GetAccountList().FirstOrDefault(kAccount => kAccount.sID == sID);
	}

	private static string[] GetConfigApi(IConfigurationSection kSection)	// 계정 설정 aAPI (외부 API 권한: 쿼리·서치·로그, 아는 항목만·겹침 뺌·m_arrApiMenu 순서, 예전: aMenu 의 로그 → 로그 권한, 없음: 외부 API 못 씀)
	{
		return kSection.GetSection("aAPI").GetChildren().Select(kApi => (kApi.Value ?? "").Trim()).Concat(GetConfigMenu(kSection).Where(sMenu => sMenu == MENU_LOG)).Where(m_arrApiMenu.Contains).Distinct().OrderBy(sApi => Array.IndexOf(m_arrApiMenu, sApi)).ToArray();
	}

	private static string[] GetConfigMenu(IConfigurationSection kSection)	// 계정 설정 aMenu (앞뒤 빈칸 뺌·빈 값·겹침 뺌, 그 순서)
	{
		return kSection.GetSection("aMenu").GetChildren().Select(kMenu => (kMenu.Value ?? "").Trim()).Where(sMenu => sMenu != "").Distinct().ToArray();
	}

	private static string[]? GetRouteTab(string sRoute)	// 라우트 → 탭 (공통: null)
	{
		foreach ((string sPath, string[] arrTab) in m_arrMenuPath)
		{
			if (sRoute.Equals(sPath, StringComparison.OrdinalIgnoreCase) || sRoute.StartsWith(sPath + "/", StringComparison.OrdinalIgnoreCase))
			{
				return arrTab;
			}
		}

		return null;
	}

	private static async Task<IResult> OnLogin(HttpRequest kRequest)	// 로그인: POST 폼 id·pass 가 계정 (관리 계정·aAccount) 일치 + 접근 메뉴 있음 → 인증 쿠키 발급 (서버 오픈 뒤 첫 접속: 계정 + 마스터 키 key)
	{
		HttpContext			kContext	= kRequest.HttpContext;	// HttpContext 인자: RequestDelegate 로 바인딩되어 IResult 버려짐 → HttpRequest 인자
		IFormCollection?	kForm		= kRequest.HasFormContentType ? await kRequest.ReadFormAsync() : null;	// 폼 아님: 불일치 처리
		string				sID			= kForm?["id"].LastOrDefault() ?? "";
		string				sPass		= kForm?["pass"].LastOrDefault() ?? "";
		string				sAddr		= Web.GetAddr(kContext);

		if (GetMasterKey() == "" && !Unlock(sID, sPass, kForm?["key"].LastOrDefault() ?? ""))	// 첫 접속: 계정 (관리 계정·aAccount) 아이디·비밀번호·마스터 키 (마스터 키·비밀번호 로그 미기록)
		{
			string	sError	= GetMasterError();	// 줄 번호 (로그만, 응답: LOGIN_ERROR)

			Log.Write(WARN_, sError);

			await Task.Delay(1000);	// 실패 응답 지연 (무차별 대입 완화)

			return Results.Json(new { success = false, error = LOGIN_ERROR });
		}

		AccountInfo?	kAccount	= GetAccountList().FirstOrDefault(kItem => IsSame(sID, kItem.sID) & IsSame(sPass, kItem.sPass));	// 맞는 계정 (& : 둘 다 비교)

		if (kAccount == null || kAccount.arrMenu.Length == 0)	// 계정 없음·불일치·접근 메뉴 없음 (비밀번호 로그 미기록)
		{
			Log.Write(WARN_, $"로그인 실패: {sID}{(kAccount == null ? "" : " 접근 메뉴 없음")} ({sAddr})");

			await Task.Delay(1000);	// 실패 응답 지연 (무차별 대입 완화)

			return Results.Json(new { success = false, error = LOGIN_ERROR });
		}

		ClaimsIdentity	kIdentity	= new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, kAccount.sID), new Claim(CLAIM_STAMP, GetStamp(kAccount)) }, CookieAuthenticationDefaults.AuthenticationScheme);	// 아이디 + 계정 표시

		await kContext.SignInAsync(new ClaimsPrincipal(kIdentity));	// 세션 쿠키 (브라우저 종료 시 삭제)

		Log.Write(INFO_, $"로그인 성공: {sID} ({sAddr})");

		return Results.Json(new { success = true });
	}

	private static bool Unlock(string sID, string sPass, string sKey)	// 마스터 키 입력 (서버 오픈 뒤 첫 접속 1회): 계정 (관리 계정·aAccount) 아이디 일치 + 그 계정 sCode 를 입력 키로 복호화 = 입력 비밀번호 → 마스터 키 메모리 + 설정 다시 로딩 (모든 sCode → sPass 메모리)
	{
		bool	bMatch	= false;	// 맞는 계정 있음

		foreach ((string sCodeID, string sCode) in GetCodeList())	// 모두 비교 (비교 시간 일정)
		{
			string?	sCodePass	= sKey == "" ? null : AES.Decrypt(sCode, sKey);	// 입력 키로 푼 비밀번호 (실패: null)

			bMatch |= sCodePass != null & IsSame(sID, sCodeID) & IsSame(sPass, sCodePass ?? "");
		}

		if (!bMatch)
		{
			return false;
		}

		lock (m_kUnlockLock)
		{
			if (GetMasterKey() != "")	// 다른 요청이 먼저 입력
			{
				return true;
			}

			SetMasterKey(sKey);

			if (!Config.Load())	// 설정 형식 오류: 잠금 그대로
			{
				SetMasterKey("");

				return false;
			}
		}

		Main.RunAll();	// 미룬 기능 시작 (타이머·알람·TCP 수신 (클라 접속·로그)·운영 탭 동접 기록)

		return true;
	}

	private static List<(string sID, string sCode)> GetCodeList()	// 마스터 키 확인 계정 아이디·sCode (암호화 값 그대로): 관리 계정 + aAccount (빈 값·아이디 겹침: 뺌, GetAccountList 와 같은 규칙)
	{
		List<(string sID, string sCode)>	listCode	= new List<(string sID, string sCode)>();
		string	sMainID		= Config.GetString("sID");
		string	sMainCode	= Config.GetString("sCode");

		if (sMainID != "" && sMainCode != "")
		{
			listCode.Add((sMainID, sMainCode));
		}

		foreach (IConfigurationSection kSection in Config.GetChildren("aAccount"))
		{
			string	sID		= kSection["sID"]?.Trim() ?? "";
			string	sCode	= kSection["sCode"] ?? "";

			if (sID == "" || sCode == "" || string.Equals(sID, sMainID, StringComparison.OrdinalIgnoreCase) || listCode.Any(kCode => string.Equals(kCode.sID, sID, StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}

			listCode.Add((sID, sCode));
		}

		return listCode;
	}

	private static IResult OnAccountList(HttpRequest kRequest)	// 계정 목록 (관리 계정만, 다른 계정: 403) → {sError, bAuth, sID (지금 로그인), bAdmin (추가·수정·삭제 가능: 관리 계정), arrTab (모든 탭: 메뉴 고름), arrApi (외부 API 권한: 쿼리·서치·로그, 따로 고름), arrAccount: [{sID, aMenu, aAPI (없음: 전체), bMain, sKey, sToken (외부 API 키·토큰)}]} (비밀번호: 안 보냄)
	{
		if (!IsAdmin(kRequest.HttpContext))	// 다른 계정: 계정 안 보임
		{
			return Results.Json(new { sError = "관리 계정만 가능" }, statusCode: StatusCodes.Status403Forbidden);
		}

		return Results.Json(new { sError = "", bAuth = m_bAuth, sID = kRequest.HttpContext.User.Identity?.Name ?? "", bAdmin = IsAdmin(kRequest.HttpContext), arrTab = Main.m_arrTab, arrApi = m_arrApiMenu, arrAccount = GetAccountView() });
	}

	private static bool IsAdmin(HttpContext kContext)	// 계정 추가·수정·삭제 가능: 관리 계정 로그인 (로그인 안 씀: 관리 비밀번호만)
	{
		return !m_bAuth || FindAccount(kContext.User.Identity?.Name ?? "")?.bMain == true;
	}

	private static async Task<IResult> OnAccountSave(AccountParam kParam, HttpRequest kRequest)	// 계정 추가·수정: 관리 계정·관리 비밀번호 → 검사 → sOldID 없음: 끝에 추가, 그 외: 그 자리 → Account.json aAccount (비밀번호: AES.Encrypt → sCode, 수정 비움: 그대로, 요청자 IP 로그)
	{
		string		sOldID	= kParam.sOldID?.Trim() ?? "";	// 수정할 계정 (없음: 추가)
		string		sID		= kParam.sID?.Trim() ?? "";
		string		sPass	= kParam.sPass ?? "";
		string		sMail	= kParam.sMail?.Trim() ?? "";	// 알람 메일 받는 주소 (없음: 메일 안 받음)
		string		sPhone	= kParam.sPhone?.Trim() ?? "";	// 전화 번호 (없음: 안 씀)
		string[]	arrMenu	= (kParam.aMenu ?? Array.Empty<string>()).Select(sMenu => sMenu.Trim()).Where(Main.m_arrTab.Contains).Distinct().ToArray();	// 탭만 (아는 항목)
		string[]	arrApi	= (kParam.aAPI ?? Array.Empty<string>()).Select(sApi => sApi.Trim()).Where(m_arrApiMenu.Contains).Distinct().OrderBy(sApi => Array.IndexOf(m_arrApiMenu, sApi)).ToArray();	// 외부 API 권한 (쿼리·서치·로그)
		string		sError	= await CheckAdmin(kParam.sAdmin, kRequest.HttpContext);

		if (sError == "")
		{
			sError = CheckAccount(sID, sPass, sOldID == "", arrMenu.Concat(arrApi).ToArray());	// 메뉴·외부 1개 이상
		}

		if (sError == "" && sMail != "" && (sMail.Length > m_nAccountMailMax || !IsMail(sMail)))
		{
			sError = "메일 형식 오류";
		}

		if (sError == "" && sPhone != "" && (sPhone.Length > m_nAccountPhoneMax || !sPhone.All(cPhone => char.IsAsciiDigit(cPhone) || cPhone == '-' || cPhone == '+') || !sPhone.Any(char.IsAsciiDigit)))	// 숫자·-·+
		{
			sError = $"전화 번호 형식 오류 (숫자·-·+, {m_nAccountPhoneMax}자 이하)";
		}

		if (sError == "")
		{
			lock (m_kAccountLock)
			{
				List<IConfigurationSection>	listSection	= Config.GetChildren("aAccount").ToList();				// 설정 aAccount (빠진 항목 포함)
				int							nOld		= sOldID == "" ? -1 : FindSection(listSection, sOldID);	// 수정할 자리
				int							nSame		= FindSection(listSection, sID);						// 같은 아이디

				if (sOldID != "" && nOld < 0)
				{
					sError = "없는 계정";
				}
				else if ((nSame >= 0 && nSame != nOld) || string.Equals(sID, Config.GetString("sID"), StringComparison.OrdinalIgnoreCase))
				{
					sError = "이미 있는 아이디";
				}
				else
				{
					JsonArray	kArray	= new JsonArray(listSection.Select(ToNode).ToArray());				// 저장할 목록
					string		sCode	= sPass != "" ? AES.Encrypt(sPass) : GetCode(listSection[nOld]);	// 수정 비움: 그대로
					JsonObject	kNode	= new JsonObject
					{
						["sID"]		= sID,
						["sMail"]	= sMail,	// 알람 메일 받는 주소
						["sPhone"]	= sPhone,	// 전화 번호
						["sCode"]	= sCode,
						["sKey"]	= nOld >= 0 ? listSection[nOld]["sKey"] ?? "" : "",	// 외부 API 키 (추가: 없음, 수정: 그대로, 만들기: 관리 계정 팝업 새 키)
						["sRand"]	= nOld >= 0 ? listSection[nOld]["sRand"] ?? "" : "",	// 키 적용 때 같이 만든 무작위 (화면 안 보임, 수정: 그대로)
						["sToken"]	= GetToken(nOld >= 0 ? listSection[nOld]["sRand"] ?? "" : "", sCode),	// 외부 API 토큰 (sCode 를 sRand 키로 암호화, 비밀번호 바뀜: 다시, sRand 없음: "")
						["aMenu"]	= new JsonArray(arrMenu.Select(sMenu => (JsonNode?)sMenu).ToArray()),
						["aAPI"]	= new JsonArray(arrApi.Select(sApi => (JsonNode?)sApi).ToArray()),	// 외부 API 권한
					};

					if (nOld < 0)
					{
						kArray.Add(kNode);
					}
					else
					{
						kArray[nOld] = kNode;
					}

					sError = SetAccounts(kArray);
				}

				if (sError == "")
				{
					Log.Write(INFO_, $"계정 {(nOld < 0 ? "추가" : "수정")}: {(sOldID != "" && sOldID != sID ? sOldID + " → " : "")}{sID} ({string.Join("·", arrMenu)}{(arrApi.Length > 0 ? " / 외부 " + string.Join("·", arrApi) : "")}{(nOld >= 0 && sPass != "" ? ", 비밀번호 바뀜" : "")}) 요청자 {Web.GetAddr(kRequest.HttpContext)}");
				}
			}
		}

		return Results.Json(new { sError, arrAccount = GetAccountView() });
	}

	private static async Task<IResult> OnAccountDelete(AccountParam kParam, HttpRequest kRequest)	// 계정 삭제: 관리 계정·관리 비밀번호 → Account.json aAccount 에서 뺌 + 모든 알람 메일·SMS 받는 계정에서 뺌 (없는 아이디: 오류, 그 계정 쿠키: 무효, 요청자 IP 로그)
	{
		string	sID		= kParam.sID?.Trim() ?? "";
		string	sError	= await CheckAdmin(kParam.sAdmin, kRequest.HttpContext);

		if (sError == "")
		{
			lock (m_kAccountLock)
			{
				List<IConfigurationSection>	listSection	= Config.GetChildren("aAccount").ToList();
				int							nOld		= FindSection(listSection, sID);	// 지울 자리

				if (nOld < 0)
				{
					sError = "없는 계정";
				}
				else
				{
					listSection.RemoveAt(nOld);

					sError = SetAccounts(new JsonArray(listSection.Select(ToNode).ToArray()));
				}

				if (sError == "")
				{
					Log.Write(INFO_, $"계정 삭제: {sID} 요청자 {Web.GetAddr(kRequest.HttpContext)}");

					if (Main.IsTab(Main.TAB_QUERY))	// 알람 메일·SMS 받는 계정에서도 뺌 (쿼리 탭 꺼짐: 알람 없음)
					{
						TabQuery.RemoveAlarmAccount(sID);
					}
				}
			}
		}

		return Results.Json(new { sError, arrAccount = GetAccountView() });
	}

	private static async Task<IResult> OnAccountKey(AccountParam kParam, HttpRequest kRequest)	// 계정 외부 API 키 적용 (키는 여기서만 바뀜): 관리 계정·관리 비밀번호 → 그 계정 sKey = 수정 창 새 키 (영문·숫자 X자리, 없음: 서버가 만듦, 다른 계정 키와 겹침: 오류) + sToken 다시 + sKey 밑 sRand 무작위 X자리 새로 (화면 안 보임) (관리 계정: Account.json 최상위 sKey·sRand·sToken, aAccount: 그 항목) → 저장 (예전 키·링크: 못 씀, 요청자 IP 로그) → {sError, arrAccount}
	{
		string	sID		= kParam.sID?.Trim() ?? "";
		string	sKey	= kParam.sKey?.Trim() ?? "";	// 적용할 키 (없음: 서버가 만듦)
		string	sError	= await CheckAdmin(kParam.sAdmin, kRequest.HttpContext);	// 관리 계정 로그인 + 관리 비밀번호 (불일치: X초 지연)

		if (sError == "" && sKey == "")
		{
			sKey = NewKey();
		}

		if (sError == "" && (sKey.Length != m_nKeyLength || !sKey.All(char.IsAsciiLetterOrDigit)))
		{
			sError = $"키 형식 오류 (영문·숫자 {m_nKeyLength}자)";
		}

		if (sError == "" && GetKeyMap().TryGetValue(sKey, out KeyAccount? kSame) && kSame.sID != sID)	// 다른 계정 키와 겹침 (키 맵: 키 → 계정 하나)
		{
			sError = "이미 있는 키";
		}

		if (sError != "")
		{
			return Results.Json(new { sError, arrAccount = GetAccountView() });
		}

		lock (m_kAccountLock)
		{
			if (sID != "" && string.Equals(sID, Config.GetString("sID"), StringComparison.Ordinal))	// 관리 계정: Account.json 최상위 sToken·sKey
			{
				string	sRand	= NewKey();	// sKey 밑 무작위 X자리 (화면 안 보임, 토큰 암호화 키)

				sError = Config.SetValue("sKey", sKey);

				if (sError == "")
				{
					sError = Config.SetValue("sRand", sRand);
				}

				if (sError == "")
				{
					sError = Config.SetValue("sToken", GetToken(sRand, Config.GetString("sCode")));	// sRand 밑
				}
			}
			else
			{
				List<IConfigurationSection>	listSection	= Config.GetChildren("aAccount").ToList();
				int							nOld		= FindSection(listSection, sID);	// 바꿀 자리

				if (sID == "" || nOld < 0)
				{
					sError = "없는 계정";
				}
				else
				{
					JsonArray	kArray	= new JsonArray(listSection.Select(ToNode).ToArray());	// 저장할 목록

					string	sRand	= NewKey();	// sKey 밑 무작위 X자리 (화면 안 보임, 토큰 암호화 키)

					kArray[nOld]!["sKey"]	= sKey;
					kArray[nOld]!["sRand"]	= sRand;
					kArray[nOld]!["sToken"]	= GetToken(sRand, kArray[nOld]!["sCode"]?.GetValue<string>() ?? "");

					sError = SetAccounts(kArray);
				}
			}

			if (sError == "")
			{
				Log.Write(INFO_, $"계정 키 적용: {sID} 요청자 {Web.GetAddr(kRequest.HttpContext)}");
			}
		}

		return Results.Json(new { sError, arrAccount = GetAccountView() });
	}

	private static async Task<string> CheckAdmin(string? sAdmin, HttpContext kContext)	// 관리 계정 로그인 + 관리 비밀번호 (관리 계정 sCode 복호화 값) 확인 (반환: 오류, 통과: "", 다른 계정: 경고 로그, 비밀번호 불일치: X초 지연·경고 로그)
	{
		string	sMainPass	= Config.GetString("sPass");

		if (!IsAdmin(kContext))	// 관리 계정 아님
		{
			Log.Write(WARN_, $"계정 수정 실패: 관리 계정 아님 {kContext.User.Identity?.Name ?? ""} ({Web.GetAddr(kContext)})");

			return "관리 계정만 가능";
		}

		if (sMainPass == "")
		{
			return "관리 계정 미설정 (Server.json sID·sCode)";
		}

		if (IsSame(sAdmin ?? "", sMainPass))
		{
			return "";
		}

		Log.Write(WARN_, $"계정 수정 실패: 관리 비밀번호 불일치 {kContext.User.Identity?.Name ?? ""} ({Web.GetAddr(kContext)})");

		await Task.Delay(1000);	// 실패 응답 지연 (무차별 대입 완화)

		return "관리 비밀번호 불일치";
	}

	private static string CheckAccount(string sID, string sPass, bool bAdd, string[] arrMenu)	// 계정 값 검사 (반환: 오류, 없음: "")
	{
		if (sID == "")
		{
			return "아이디 없음";
		}

		if (sID.Length > m_nAccountIDMax || sID.Any(cID => char.IsWhiteSpace(cID) || cID == '/'))	// '/': 로그 수신 경로 구분
		{
			return $"아이디: 빈칸·'/' 없이 {m_nAccountIDMax}자 이하";
		}

		if (bAdd && sPass == "")
		{
			return "비밀번호 없음";
		}

		if (sPass.Length > m_nAccountPassMax)
		{
			return $"비밀번호: {m_nAccountPassMax}자 이하";
		}

		return arrMenu.Length == 0 ? "메뉴·외부 없음 (1개 이상)" : "";
	}

	private static int FindSection(List<IConfigurationSection> listSection, string sID)	// 설정 aAccount 에서 아이디 자리 (대소문자 무시, 없음: -1)
	{
		return listSection.FindIndex(kSection => string.Equals(kSection["sID"]?.Trim(), sID, StringComparison.OrdinalIgnoreCase));
	}

	private static string SetAccounts(JsonArray kArray)	// 설정 aAccount 저장 (Account.json, sID 오름차순: 숫자 크기 순, 반환: 오류, 성공: "")
	{
		List<JsonNode?>	listNode	= kArray.ToList();	// 항목 (부모 떼고 다시 담음)

		kArray.Clear();

		return Config.SetArray("aAccount", new JsonArray(listNode.OrderBy(kNode => kNode?["sID"]?.GetValue<string>().Trim() ?? "", Main.m_kNaturalOrder).ToArray()));
	}

	private static JsonNode ToNode(IConfigurationSection kSection)	// 설정 aAccount 항목 → 저장 JSON (sID·sMail·sPhone·sCode·sKey·sRand·sToken·aMenu·aAPI, sToken: sCode 로 다시 계산, sKey·sRand: 그대로)
	{
		string		sID		= kSection["sID"] ?? "";
		string		sCode	= GetCode(kSection);
		string[]	arrRaw	= GetConfigMenu(kSection);	// 설정 aMenu (예전: 로그 포함)
		string[]	arrTab	= arrRaw.Where(sMenu => sMenu != MENU_LOG).ToArray();	// 탭만 (로그: aAPI 로)

		return new JsonObject
		{
			["sID"]		= sID,
			["sMail"]	= kSection["sMail"] ?? "",	// 알람 메일 받는 주소
			["sPhone"]	= kSection["sPhone"] ?? "",	// 전화 번호
			["sCode"]	= sCode,
			["sKey"]	= kSection["sKey"] ?? "",	// 없음: "" (관리 계정 팝업 새 키로만 만듦)
			["sRand"]	= kSection["sRand"] ?? "",	// 키 적용 때 같이 만든 무작위 (화면 안 보임)
			["sToken"]	= GetToken(kSection["sRand"] ?? "", sCode),	// sCode 를 sRand 키로 (sRand 없음: "")
			["aMenu"]	= new JsonArray((arrTab.Length == 0 && arrRaw.Length > 0 ? arrRaw : arrTab).Select(sMenu => (JsonNode?)sMenu).ToArray()),	// 로그만 있던 예전 계정: 그대로 (빈 값 = 전체 막음)
			["aAPI"]	= new JsonArray(GetConfigApi(kSection).Select(sApi => (JsonNode?)sApi).ToArray()),	// 외부 API 권한
		};
	}

	private static string GetCode(IConfigurationSection kSection)	// 설정 aAccount 항목 sCode (없음: 평문 sPass → 암호화, 둘 다 없음: "")
	{
		string	sPass	= kSection["sPass"] ?? "";	// sCode 있음: 복호화 값, 없음: 파일 평문

		return kSection["sCode"] ?? (sPass != "" ? AES.Encrypt(sPass) : "");
	}

	private static object[] GetAccountView()	// 계정 목록 응답 (아이디·메일·전화·설정 aMenu·관리 계정·외부 API 키·토큰 (키 없음: ""), 비밀번호 빼고, 관리 계정 팝업만)
	{
		Dictionary<string, (string sKey, string sToken)>	hmapKey	= new Dictionary<string, (string sKey, string sToken)>(StringComparer.Ordinal);	// 아이디 → 키·토큰

		foreach (KeyValuePair<string, KeyAccount> kPair in GetKeyMap())
		{
			hmapKey.TryAdd(kPair.Value.sID, (kPair.Key, GetUrlToken(kPair.Value.sRand, kPair.Value.sCode)));	// 계정 팝업 표시: URL 용
		}

		Dictionary<string, (string sMail, string sPhone, string[] arrApi)>	hmapInfo	= new Dictionary<string, (string sMail, string sPhone, string[] arrApi)>(StringComparer.Ordinal);	// 아이디 → 메일·전화·외부 API 권한 (aAccount)

		foreach (IConfigurationSection kSection in Config.GetChildren("aAccount"))
		{
			hmapInfo.TryAdd(kSection["sID"]?.Trim() ?? "", (kSection["sMail"] ?? "", kSection["sPhone"] ?? "", GetConfigApi(kSection)));
		}

		return GetAccountList().Select(kAccount =>
		{
			(string sKey, string sToken)	= hmapKey.GetValueOrDefault(kAccount.sID, ("", ""));
			(string sMail, string sPhone, string[] arrApi)	= kAccount.bMain ? ("", "", m_arrApiMenu) : hmapInfo.GetValueOrDefault(kAccount.sID, ("", "", Array.Empty<string>()));	// 관리 계정: Server.json (메일·전화 없음, 외부 API 전체)

			return (object)new { kAccount.sID, sMail, sPhone, aMenu = kAccount.arrConfigMenu, aAPI = arrApi, kAccount.bMain, sKey, sToken };
		}).ToArray();
	}

	private static async Task<IResult> OnLogout(HttpRequest kRequest)	// 로그아웃: 인증 쿠키 삭제 (이 브라우저만, 요청자 IP 로그)
	{
		HttpContext	kContext	= kRequest.HttpContext;	// HttpContext 인자: RequestDelegate 로 바인딩되어 IResult 버려짐 → HttpRequest 인자

		await kContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

		Log.Write(INFO_, $"로그아웃: {kContext.User.Identity?.Name ?? ""} ({Web.GetAddr(kContext)})");

		return Results.Json(new { success = true });
	}

	private static bool IsSame(string sLeft, string sRight)	// 문자열 일치 (비교 시간 일정: 타이밍 공격 완화)
	{
		return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sLeft), Encoding.UTF8.GetBytes(sRight));
	}

	private static string LoginHtml()	// 로그인 페이지 HTML (로그인 주소 = form action: ToolManager 경유 주소 변환 대응, 성공: 새로고침 → 요청 페이지, 마스터 키 입력 전: 마스터 키 칸)
	{
		string	sTitle	= System.Net.WebUtility.HtmlEncode(Web.GetTitle());
		string sKeyHtml	= GetMasterKey() != "" ? "" : """<label for="key">마스터 키</label><input id="key" type="password" autocomplete="off">""";	// 첫 접속만

		return $$"""
		<!doctype html>
		<html lang="ko">
		<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{{sTitle}} 로그인</title><link rel="icon" href="{{Web.GetFavicon()}}">
		<style>
		:root{--bgColor-default:#ffffff;--bgColor-muted:#f6f8fa;--bgColor-emphasis:#25292e;--fgColor-default:#1f2328;--fgColor-danger:#d1242f;--fgColor-onEmphasis:#ffffff;--borderColor-default:#d1d9e0;--borderColor-muted:#d1d9e0b3;
		--button-primary-fgColor-rest:#ffffff;--button-primary-borderColor-rest:#1f232826;--shadow-inset:inset 0 1px 0 0 #1f23280a;--shadow-resting-small:0 1px 1px 0 #1f23280a,0 1px 2px 0 #1f232808;--focus-outlineColor:var(--zone,var(--bgColor-emphasis));--borderRadius-medium:0.375rem;
		--fontStack-sansSerif:"Mona Sans VF",-apple-system,BlinkMacSystemFont,"Segoe UI","Noto Sans Backtick Fix","Noto Sans",Helvetica,Arial,sans-serif,"Apple Color Emoji","Segoe UI Emoji";--zone:{{Web.GetTitleColor()}};color-scheme:light}
		body{margin:0;padding:16px;background:var(--bgColor-default);color:var(--fgColor-default);font:400 0.875rem/1.5 var(--fontStack-sansSerif)}
		.panel{position:relative;top:30vh;transform:translateY(-30%);max-width:320px;margin:0 auto;padding:16px;border:1px solid var(--borderColor-muted);border-radius:var(--borderRadius-medium);background:var(--bgColor-muted)}
		h1{margin:0 0 16px;font-size:1.25rem;font-weight:600}label{display:block;margin:0 0 4px;font-weight:600}
		input{box-sizing:border-box;width:100%;height:32px;margin:0 0 16px;padding:0 12px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--bgColor-default);box-shadow:var(--shadow-inset);color:var(--fgColor-default);font:inherit}
		input:focus{border-color:var(--focus-outlineColor);outline:none;box-shadow:inset 0 0 0 1px var(--focus-outlineColor)}
		button{box-sizing:border-box;width:100%;height:32px;padding:0 12px;border:1px solid var(--button-primary-borderColor-rest);border-radius:var(--borderRadius-medium);background:var(--zone,var(--bgColor-emphasis));box-shadow:var(--shadow-resting-small);color:var(--button-primary-fgColor-rest);font:500 0.875rem/1 var(--fontStack-sansSerif);cursor:pointer}
		button:focus-visible{outline:2px solid var(--focus-outlineColor);outline-offset:-2px;box-shadow:inset 0 0 0 3px var(--fgColor-onEmphasis)}button:disabled{opacity:.6;cursor:default}
		.err{min-height:20px;margin:0 0 8px;color:var(--fgColor-danger)}
		</style>
		</head>
		<body>
		<form class="panel" id="login" method="post" action="{{LOGIN_PATH}}">
		<h1 style="color:{{Web.GetTitleColor()}}">{{sTitle}}</h1>
		<label for="id">아이디</label><input id="id" autocomplete="username" autofocus>
		<label for="pass">비밀번호</label><input id="pass" type="password" autocomplete="current-password">
		{{sKeyHtml}}
		<p class="err" id="err"></p>
		<button id="btn">로그인</button>
		</form>
		<script>
		document.getElementById('login').addEventListener('submit', async kEvent => {
			kEvent.preventDefault();

			const	kId	= document.getElementById('id'), kPass = document.getElementById('pass'), kKey = document.getElementById('key'), kErr = document.getElementById('err'), kBtn = document.getElementById('btn');

			if (kPass.value === '') {	// 아이디 Enter: 비밀번호로 이동
				kPass.focus();

				return;
			}

			if (kKey != null && kKey.value === '') {	// 첫 접속 비밀번호 Enter: 마스터 키로 이동 (확인: 셋 다 받은 뒤)
				kKey.focus();

				return;
			}

			kBtn.disabled = true;

			try {
				const	kRes	= await fetch(kEvent.target.action, { method: 'POST', body: new URLSearchParams({ id: kId.value.trim(), pass: kPass.value, key: kKey?.value ?? '' }) });
				const	kData	= await kRes.json();

				if (kData.success) {	// 새로고침 → 요청 페이지
					location.reload();

					return;
				}

				kErr.textContent = '{{LOGIN_ERROR}}';
			} catch (kError) {
				kErr.textContent = '{{LOGIN_ERROR}}';
			}

			kBtn.disabled = false;
		});
		</script>
		</body>
		</html>
		""";
	}
}

record KeyAccount(string sID, string sCode, string sRand, string[] arrApi, bool bMain);	// 외부 API 키 맵 계정 (sCode: 토큰 확인 = 토큰을 sRand 키로 복호화한 값, sRand: 토큰 암호화 키, arrApi: 설정 aAPI 외부 API 권한 (없음: 못 씀), bMain: 관리 계정 (전체))

record AccountInfo(string sID, string sPass, string[] arrConfigMenu, string[] arrMenu, bool bMain);	// 로그인 계정 (sPass: sCode 복호화 값, arrConfigMenu: 설정 aMenu (없음: 전체), arrMenu: 접근 탭 (켜진 탭 중, 관리 계정: 전체))

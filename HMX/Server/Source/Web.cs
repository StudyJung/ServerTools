class Web : IHostedService	// 웹 서버 (Kestrel + Minimal API, Main.Service 호스트와 함께 시작·중지): 미들웨어·기본 페이지 (로그인: Auth, 라우트·TCP 수신: Main 연결)
{

	private readonly string	m_sUrl;	// 바인딩 주소

	public static string	m_sLinkUrl	= "";	// 알람 쿼리 링크 주소 (메일·훅 본문 끝, 알람 기록 내용 클릭, 설정 sUrl, 없음: 바인딩 주소 (localhost·모든 주소: 이 머신 내부 IP, IIS: 포트만 첫 요청 포트), 웹 없음: "")

	private static int		m_nLinkWait;	// IIS 실행·sUrl 없음: 첫 요청 포트 기다림 (1, Interlocked)
	private WebApplication?	m_kWebApp;		// 웹 앱

	public static bool	m_bHttps;	// HTTPS 운영 (설정 bHttps + IIS 실행): 로그인 쿠키 Secure·HSTS 헤더·알람 링크 https

	private const string	m_sHsts	= "max-age=31536000";	// HSTS 유지 (초, 1년: 브라우저가 이 주소를 https 로만)

	public Web(int nPort, string sHost)
	{
		bool	bIIS	= Environment.GetEnvironmentVariable("HTTP_PLATFORM_PORT") != null;	// IIS 실행 (HttpPlatformHandler)

		m_bHttps			= Config.GetBool("bHttps") && bIIS;	// IIS 만 (콘솔·서비스: Kestrel http 그대로)
		m_sUrl				= $"http://{sHost}:{nPort}";			// Kestrel: http (IIS: https 받아 http 로 넘김)
		m_sLinkUrl			= Config.GetString("sUrl") != "" ? Config.GetString("sUrl").TrimEnd('/') : GetLinkUrl(sHost, nPort);
		m_nLinkWait			= Config.GetString("sUrl") == "" && bIIS ? 1 : 0;	// IIS 실행 (HttpPlatformHandler): 바인딩 포트 = IIS 내부 포트 → 첫 요청 포트 (IIS 사이트 포트) 로 링크, exe 실행: 바인딩 포트 그대로
	}

	public async Task StartAsync(CancellationToken kToken)	// 앱 생성 → 미들웨어 → 라우트 → 시작 (비차단)
	{
		WebApplicationBuilder kBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,	// 서비스: exe 폴더 (기본 System32 → wwwroot 못 찾음)
		});

		kBuilder.WebHost.UseUrls(m_sUrl);
		kBuilder.Logging.SetMinimumLevel(LogLevel.Warning);	// 기본 로그 최소화 (요청 로그: UseRequestLog)

		bool	bAuth	= Config.GetBool("bAuth");	// false·키 없음: 모두 허용

		if (bAuth)
		{
			Auth.AddService(kBuilder);	// 쿠키 암호화 키·쿠키 인증
		}

		WebApplication	WebApp	= kBuilder.Build();

		WebApp.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor });	// IIS 실행: X-Forwarded-For → 실제 IP (신뢰 프록시: 루프백만 → 헤더 위조 무시)

		UseHsts(WebApp);
		UseLinkPort(WebApp);
		UseRequestLog(WebApp);

		if (bAuth)	// 미인증 차단 (정적 파일·라우트보다 먼저)
		{
			Auth.Use(WebApp);	// 절반 지난 쿠키 연장 + 로그인 검사
		}

		bool	bStatic	= UseStaticFile(WebApp);

		WebApp.UseRouting();

		if (bAuth)	// 계정 메뉴 검사 (라우팅 뒤: 라우트 패턴)
		{
			Auth.UseMenu(WebApp);
		}

		Main.MapRoute(WebApp, bAuth);	// 기본 페이지·공통 API·탭·로그인

		Main.StartRun(WebApp.Lifetime.ApplicationStopping);	// 기능 시작 (타이머·알람·TCP 수신·동접 기록, 마스터 키 입력 대기: 첫 접속 뒤, 웹 서버 종료 시 함께 중지)

		if (!bAuth)
		{
			Log.Write(NOTI_, "로그인: 안함 (설정 bAuth 가 false)");
		}
		else
		{
			Auth.LogAccount();	// 로그인 계정 수·설정 오류
		}

		Log.Write(INFO_, $"웹 서버 시작: {m_sUrl}  (알람 링크: {m_sLinkUrl}{(m_nLinkWait == 1 ? " → IIS: 첫 요청 포트로" : "")}, 정적 파일: {(bStatic ? "wwwroot" : "없음")})");

		m_kWebApp = WebApp;

		await WebApp.StartAsync(kToken);
	}

	public async Task StopAsync(CancellationToken kToken)	// 웹 서버 중지 (클라 수신 중지) → 모든 클라 수신 끝 대기 → 해제 → 데이터 정리 (서비스 중지·Ctrl+C, 서비스 중지 응답 전에 끝냄)
	{
		if (m_kWebApp == null)
		{
			return;
		}

		await m_kWebApp.StopAsync(kToken);	// ApplicationStopping → 클라 수신 중지·연결 끊음
		await Main.WaitTCP();			// 모든 클라 수신 끝 (이후 Log 안 들어옴)
		await m_kWebApp.DisposeAsync();

		m_kWebApp = null;

		Main.Shutdown();	// 타이머 → 적재 (원문·받은 진행) → 색인 → 알람
	}

	private static void UseLinkPort(WebApplication WebApp)	// IIS 실행: 첫 요청 Host 포트 (IIS 사이트 포트, 프로세스: IIS 첫 요청에 시작) → 알람 링크 포트 (호스트: 그대로, 1번만, exe 실행·sUrl 있음: 안 함)
	{
		if (m_nLinkWait == 0)
		{
			return;
		}

		WebApp.Use(async (HttpContext kContext, RequestDelegate funcNext) =>
		{
			if (Volatile.Read(ref m_nLinkWait) == 1 && Interlocked.Exchange(ref m_nLinkWait, 0) == 1)	// 첫 요청만
			{
				int		nDefault	= m_bHttps ? 443 : 80;						// 기본 포트 (https: 443)
				int		nPort		= kContext.Request.Host.Port ?? nDefault;	// Host 포트 없음: 기본 포트
				string	sHost		= new Uri(m_sLinkUrl).Host;					// 호스트 (내부 IP) 그대로

				m_sLinkUrl = $"{(m_bHttps ? "https" : "http")}://{sHost}{(nPort == nDefault ? "" : ":" + nPort)}";	// 포트만 (기본 포트: 생략)

				Log.Write(INFO_, $"알람 링크: {m_sLinkUrl} (IIS 사이트 포트, 첫 요청)");
			}

			await funcNext(kContext);
		});
	}

	private static void UseHsts(WebApplication WebApp)	// HTTPS 운영: 모든 응답 HSTS 헤더 (IIS 가 https → http 로 넘김: UseHsts 의 https 검사 대신 늘)
	{
		if (!m_bHttps)
		{
			return;
		}

		WebApp.Use(async (HttpContext kContext, RequestDelegate funcNext) =>
		{
			kContext.Response.Headers.StrictTransportSecurity = m_sHsts;

			await funcNext(kContext);
		});
	}

	private static void UseRequestLog(WebApplication WebApp)	// 요청 로그: [KST 시각] 메서드 경로 -> 상태코드 (소요ms)
	{
		WebApp.Use(async (HttpContext kContext, RequestDelegate funcNext) =>
		{
			long	nBegin	= Stopwatch.GetTimestamp();

			await funcNext(kContext);

			Console.WriteLine($"[{GetNowText()}] {kContext.Request.Method} {Auth.GetLogPath(kContext)}{kContext.Request.QueryString} -> {kContext.Response.StatusCode} ({Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds:F1}ms)");
		});
	}

	private static bool UseStaticFile(WebApplication WebApp)	// 정적 파일: wwwroot 있을 때만 (index.html: "/" 보다 우선)
	{
		if (!Directory.Exists(Path.Combine(WebApp.Environment.ContentRootPath, "wwwroot")))
		{
			return false;
		}

		WebApp.UseDefaultFiles();
		WebApp.UseStaticFiles(new StaticFileOptions
		{
			OnPrepareResponse = kFile => kFile.Context.Response.Headers.CacheControl = "no-cache",	// 매 요청 재검증 (캐시 페이지가 로그인 검사 없이 표시 방지)
		});

		return true;
	}

	public static void MapRoute(WebApplication WebApp)	// 라우트 등록 (기본 페이지, Main.MapRoute)
	{
		WebApp.MapGet("/", OnHome);			// 기본 페이지
	}

	private static IResult OnHome(HttpContext kContext)	// 기본 페이지 (보일 탭: 계정 메뉴, UI: 관리 계정 제목 + 계정·새로 고침 버튼만·다른 계정 계정 버튼 숨김, 동작 그대로)
	{
		return MakePage("main", Auth.GetMenu(kContext), Auth.GetUI(kContext));
	}

	public static IResult MakePage(string sMode, string[]? arrMenu = null, string sUI = "")	// 페이지 HTML (sMode: main 기본, view 파일 보기 새 창, field 필드 값 목록 새 창, arrMenu: 보일 탭 (없음: 켜진 탭 전체), sUI: Auth.GetUI (account·user·""))
	{
		string	sHtml	= IndexHtml();

		sHtml = sHtml.Replace("__YESTERDAY__", GetNow().AddDays(-1).ToString("yyyy-MM-dd"));	// 시작일 기본값 = 어제 (KST)
		sHtml = sHtml.Replace("__TODAY__", GetNow().ToString("yyyy-MM-dd"));	// 종료일 기본값 = 오늘 (KST)
		sHtml = sHtml.Replace("__TITLE__", WebUtility.HtmlEncode(GetTitle()));
		sHtml = sHtml.Replace("__ZONE_COLOR__", GetTitleColor());
		sHtml = sHtml.Replace("__MANAGER_ZONE_COLOR__", GetZoneColor(TabOperation.GetZone()));	// 운영 탭 존 색 (Manager:sZone, 운영 탭 켜짐: 제목 색 포함 페이지 전체 존 색만)
		sHtml = sHtml.Replace("__MANAGER_ZONE__", WebUtility.HtmlEncode(TabOperation.GetZone()));	// 운영 탭 존 이름 (사이트 존과 다를 때만 탭 존 색)
		sHtml = sHtml.Replace("__MENU__", JsonSerializer.Serialize(arrMenu ?? Main.GetMenu()));	// 보일 탭 (설정 aMenu: 탭 버튼 이름·순서, 없음: 전체, 서버 켜진 탭과 같음)
		sHtml = sHtml.Replace("__FAVICON__", GetFavicon());	// 탭 아이콘 (L + 존 색)
		sHtml = sHtml.Replace("__MODE__", sMode);
		sHtml = sHtml.Replace("__UI__", sUI);	// 관리 계정 account: 제목 + 계정·새로 고침 버튼만, 다른 계정 user: 계정 버튼 숨김 (동작 그대로)

		return Results.Content(sHtml, "text/html; charset=utf-8");
	}

	private static string GetLinkUrl(string sHost, int nPort)	// 알람 링크 기본 주소: 바인딩 호스트 (localhost·루프백·모든 주소: 이 머신 내부 IP (사설 10·172.16~31·192.168·게이트웨이 우선), 못 찾음: 바인딩 호스트)
	{
		bool	bAny		= sHost.ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "0.0.0.0" or "*" or "+" or "::" or "[::]";	// 루프백·모든 주소
		string	sLinkHost	= bAny ? GetNicIp(false) : sHost;	// 내부에서 열 주소 (IP 바인딩: 그 IP)

		return $"{(m_bHttps ? "https" : "http")}://{(sLinkHost != "" ? sLinkHost : sHost)}:{nPort}";
	}

	private static bool IsPublicIp(IPAddress kIp)	// 공인 IPv4 (사설 10·172.16~31·192.168, 공유 100.64~127, 루프백 127, 자동 사설 169.254, 0·멀티캐스트·예약 224~ 제외)
	{
		byte[]	arrIp	= kIp.GetAddressBytes();

		return kIp.AddressFamily == AddressFamily.InterNetwork && !(arrIp[0] is 0 or 10 or 127 or >= 224 || (arrIp[0] == 172 && arrIp[1] >= 16 && arrIp[1] <= 31) || (arrIp[0] == 192 && arrIp[1] == 168) || (arrIp[0] == 100 && arrIp[1] >= 64 && arrIp[1] <= 127) || (arrIp[0] == 169 && arrIp[1] == 254));
	}

	private static string GetNicIp(bool bPublic)	// 이 머신 랜 IPv4 (켜진 랜, 루프백·자동 사설 169.254 제외, bPublic: 공인 IP만, 우선: 사설 대역 10·172.16~31·192.168 → 게이트웨이 있음, 없음: "")
	{
		string	sPick	= "";	// 고른 IP
		int		nPick	= 0;	// 고른 점수 (사설 2 + 게이트웨이 1)

		foreach (System.Net.NetworkInformation.NetworkInterface kNic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
		{
			if (kNic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || kNic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
			{
				continue;
			}

			System.Net.NetworkInformation.IPInterfaceProperties	kProp	= kNic.GetIPProperties();
			bool	bGateway	= kProp.GatewayAddresses.Any(kGate => kGate.Address.AddressFamily == AddressFamily.InterNetwork && !kGate.Address.Equals(IPAddress.Any));	// 기본 게이트웨이 (실제 랜)

			foreach (System.Net.NetworkInformation.UnicastIPAddressInformation kAddr in kProp.UnicastAddresses)
			{
				byte[]	arrIp	= kAddr.Address.GetAddressBytes();

				if (kAddr.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(kAddr.Address) || (arrIp[0] == 169 && arrIp[1] == 254) || (bPublic && !IsPublicIp(kAddr.Address)))
				{
					continue;
				}

				bool	bPrivate	= arrIp[0] == 10 || (arrIp[0] == 172 && arrIp[1] >= 16 && arrIp[1] <= 31) || (arrIp[0] == 192 && arrIp[1] == 168);	// 사설 대역
				int		nScore		= (bPrivate ? 2 : 0) + (bGateway ? 1 : 0) + 1;

				if (nScore > nPick)
				{
					sPick = kAddr.Address.ToString();
					nPick = nScore;
				}
			}
		}

		return sPick;
	}

	public static string GetQueryLink(string sQuery, DateTimeOffset kStart, DateTimeOffset kEnd)	// 쿼리 탭 바로 실행 주소 (새 창 복원 형식: 쿼리·S·E, D 해제, 웹 없음: "")
	{
		return m_sLinkUrl == "" ? "" : m_sLinkUrl + GetQueryPath(sQuery, kStart.DateTime, kEnd.DateTime);
	}

	public static string GetQueryPath(string sQuery, DateTime kStart, DateTime kEnd)	// 쿼리 탭 바로 실행 경로 /?... (페이지 RestoreState: 쿼리 탭·쿼리·S·E (초까지)·D 해제 → 바로 쿼리, 외부 API 쿼리 브라우저 열기 이동)
	{
		Dictionary<string, string> hmapParam = new Dictionary<string, string>	// 페이지 RestoreState 입력칸 (OPEN_STATE_INPUT)
		{
			["sTab"]			= "QueryButton",
			["bQuery"]		= "1",
			["QueryRange"]	= "",
			["StartDate"]		= kStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
			["StartHour"]		= kStart.ToString("HH", CultureInfo.InvariantCulture),
			["StartMinute"]	= kStart.ToString("mm", CultureInfo.InvariantCulture),
			["StartSecond"]	= kStart.ToString("ss", CultureInfo.InvariantCulture),
			["EndDate"]		= kEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
			["EndHour"]		= kEnd.ToString("HH", CultureInfo.InvariantCulture),
			["EndMinute"]		= kEnd.ToString("mm", CultureInfo.InvariantCulture),
			["EndSecond"]		= kEnd.ToString("ss", CultureInfo.InvariantCulture),
			["QueryKey"]		= sQuery,
		};

		return $"/?{string.Join("&", hmapParam.Select(kPair => $"{kPair.Key}={Uri.EscapeDataString(kPair.Value)}"))}";
	}

	public static string GetApiQueryLink(string sQuery, DateTimeOffset kStart, DateTimeOffset kEnd, string sPath)	// 외부 API 쿼리 주소 /api/external/query/키/토큰/?sKey=&nStart=&nEnd= (sPath: "키/토큰" (Auth.GetMainPath: 관리 계정), 기간 yyyyMMddHHmmss: 시작 포함·끝 미포함, 웹·경로 없음: "")
	{
		if (m_sLinkUrl == "" || sPath == "")
		{
			return "";
		}

		return $"{m_sLinkUrl}{API.QUERY_PATH}{sPath}/?sKey={Uri.EscapeDataString(sQuery)}&nStart={kStart.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}&nEnd={kEnd.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";	// 토큰: URL 용 base64 (-·_, = 없음)
	}

	public static string GetTitle()	// 제목: 설정 sTitle (없음: HMX) + 설정 sZone (예: HMX Alpha, sZone 없음: 이름만)
	{
		string	sTitle	= Config.GetString("sTitle").Trim();	// 이름 (로그 태그 [이름_] 과 같음)

		return $"{(sTitle == "" ? "HMX" : sTitle)} {Config.GetString("sZone")}".Trim();
	}

	public static string GetTitleColor()	// 제목 색: sZone 별 (그 외: 기본 글자색)
	{
		return GetZoneColor(Config.GetString("sZone"));
	}

	public static string GetZoneColor(string sZone)	// 존 색: Dev 초록·Alpha 파랑·Real 빨강 (그 외: inherit → 기본 글자색, 사이트 sZone·탭 존 공용)
	{
		switch (sZone.ToLowerInvariant())
		{
			case "dev":
				return "#1a7f37";
			case "alpha":
				return "#2f6fde";
			case "real":
				return "#d1242f";
			default:
				return "inherit";
		}
	}

	public static string GetFavicon()	// 탭 아이콘 (SVG data URI): 도구 한 글자 흰 글자 + 존 색 바탕
	{
		string	sText	= "X";	// 탭 아이콘 글자 (도구마다 한 글자: HMX X·SM S·HTB B·ToolManager T·DataCompare C·DataFinder F)
		string	sColor	= GetTitleColor().StartsWith('#') ? GetTitleColor() : "#25292e";	// 존 없음: 검정
		string sFit		= (sText.Length > 1) ? $" textLength='{((sText.Length > 2) ? 30 : 29)}' lengthAdjust='spacingAndGlyphs'" : "";	// 칸 폭 꽉 채움 (16px 탭 가독성)
		string sSvg		= $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'><rect width='32' height='32' rx='6' fill='{sColor}'/><text x='16' y='24.5' text-anchor='middle' font-family='Arial,Segoe UI,sans-serif' font-size='24' font-weight='900' fill='#fff'{sFit}>{sText}</text></svg>";

		return "data:image/svg+xml," + Uri.EscapeDataString(sSvg);	// 글자 y 24.5: 대문자 기준선 → 세로 중앙
	}

	public static string GetAddr(HttpContext kContext)	// 접속 IP (IIS: X-Forwarded-For 반영, IPv4 매핑 → IPv4)
	{
		IPAddress?	kAddr	= kContext.Connection.RemoteIpAddress;

		if (kAddr == null)
		{
			return "-";
		}

		return (kAddr.IsIPv4MappedToIPv6 ? kAddr.MapToIPv4() : kAddr).ToString();
	}

	private static string IndexHtml()    // 기본 페이지 HTML (데이터: textContent 로만 표시)
	{
		return string.Join("\r\n\r\n", IndexHead(), TabSearch.SearchHTML(), TabDump.DumpHTML(), TabFile.FileHTML(), TabOperation.ManagerHTML(), TabProcess.ProcessHTML(), QueryPage.QueryHTML(), IndexTail());	// 공통 앞 → 탭 스크립트 (서치·덤프·파일·운영·반영·쿼리) → 공통 뒤
	}

	private static string IndexHead()	// 기본 페이지 공통 앞: 머리·스타일·본문·공통 스크립트 (Key 까지)
	{
		return """
		<!doctype html>
		<html lang="ko">
		<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>__TITLE__</title><link rel="icon" href="__FAVICON__">
		<style>
		:root{--bgColor-default:#ffffff;--bgColor-muted:#f6f8fa;--bgColor-accent-muted:#ddf4ff;--bgColor-accent-emphasis:#0969da;--fgColor-default:#1f2328;--fgColor-muted:#59636e;--fgColor-accent:#0969da;--fgColor-success:#1a7f37;--fgColor-danger:#d1242f;--fgColor-disabled:#818b98;--bgColor-emphasis:#25292e;--bgColor-neutral-emphasis:#59636e;--borderColor-emphasis:#818b98;--chart-barColor:#afb8c1;--fgColor-onEmphasis:#ffffff;
		--borderColor-default:#d1d9e0;--borderColor-disabled:#818b981a;--borderColor-accent-emphasis:#0969da;--button-default-fgColor-rest:#25292e;--button-default-bgColor-rest:#f6f8fa;--button-default-bgColor-hover:#eff2f5;--button-default-bgColor-active:#e6eaef;--button-default-bgColor-selected:#e6eaef;--button-default-bgColor-disabled:#eff2f5;--button-default-shadow-resting:0 1px 0 0 #1f23280a;
		--button-primary-fgColor-rest:var(--zone-primary-fg,var(--button-default-fgColor-rest));--button-primary-fgColor-disabled:var(--zone-primary-fg-disabled,var(--fgColor-disabled));--zone-primary-hover:color-mix(in srgb,var(--zone) 90%,#000);--zone-primary-active:color-mix(in srgb,var(--zone) 80%,#000);--zone-primary-disabled:color-mix(in srgb,var(--zone) 45%,#fff);--zone-primary-fg:color-mix(in srgb,var(--zone) 0%,#fff);--zone-primary-fg-disabled:color-mix(in srgb,var(--zone) 0%,#ffffffcc);--zone-primary-border:color-mix(in srgb,var(--zone) 0%,#1f232826);--button-primary-bgColor-rest:var(--zone,var(--button-default-bgColor-rest));--button-primary-bgColor-hover:var(--zone-primary-hover,var(--button-default-bgColor-hover));--button-primary-bgColor-active:var(--zone-primary-active,var(--button-default-bgColor-active));--button-primary-bgColor-disabled:var(--zone-primary-disabled,var(--button-default-bgColor-disabled));--button-primary-borderColor-rest:var(--zone-primary-border,var(--borderColor-default));--button-primary-shadow-selected:inset 0 1px 0 0 #002d114d;
		--button-danger-fgColor-rest:var(--zone,#d1242f);--button-danger-fgColor-hover:#ffffff;--button-danger-fgColor-disabled:color-mix(in srgb,var(--zone,#d1242f) 50%,transparent);--button-danger-bgColor-hover:var(--zone,#cf222e);--borderColor-muted:#d1d9e0b3;--control-transparent-bgColor-hover:#818b981a;--control-transparent-bgColor-selected:#818b9826;--treeViewItem-leadingVisual-iconColor-rest:#54aeff;--highlight-neutral-bgColor:#fff8c5;
		--overlay-bgColor:#ffffff;--overlay-backdrop-bgColor:#c8d1da66;--shadow-inset:inset 0 1px 0 0 #1f23280a;--shadow-resting-small:0 1px 1px 0 #1f23280a,0 1px 2px 0 #1f232808;--shadow-floating-small:0 0 0 1px #d1d9e040,0 6px 12px -3px #25292e0a,0 6px 18px 0 #25292e1f;--focus-outlineColor:var(--zone,var(--bgColor-emphasis));--borderRadius-medium:0.375rem;--borderRadius-large:0.75rem;
		--fontStack-sansSerif:"Mona Sans VF",-apple-system,BlinkMacSystemFont,"Segoe UI","Noto Sans Backtick Fix","Noto Sans",Helvetica,Arial,sans-serif,"Apple Color Emoji","Segoe UI Emoji";--fontStack-monospace:ui-monospace,SFMono-Regular,SF Mono,Menlo,Consolas,Liberation Mono,monospace;--zone:__ZONE_COLOR__;--split:var(--bgColor-emphasis);color-scheme:light}
		body{box-sizing:border-box;margin:0;padding:16px;min-width:400px;background:var(--bgColor-default);color:var(--fgColor-default);font:400 0.875rem/1.5 var(--fontStack-sansSerif)}
		h1{margin:0 0 16px;padding:8px 16px;border:1px solid var(--zone,var(--borderColor-default));border-radius:var(--borderRadius-medium);background:var(--bgColor-default);font-size:1.25rem;font-weight:600}.panel{padding:16px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--bgColor-default)}
		p{margin:12px 0 0;color:var(--fgColor-muted)}
		#ResultPanel.query #ResultTitle,#ResultPanel.query #ResultPager,#ResultPanel.query .scrollwrap,#ResultPanel:not(.query) #QueryTitle,#ResultPanel:not(.query) #QueryChart{display:none}#QueryScroll{height:480px;container-type:inline-size}#QueryPrev,#QueryNext,#QueryDepth,#ViewPrev,#ViewNext,#ViewShift,#FilePrev,#FileNext,#FileShift{width:28px;padding:0}#QueryDepth,#ViewShift,#FileShift{width:auto;min-width:28px;padding:0 6px}#ViewPrev,#ViewNext,#FilePrev,#FileNext{font-size:16px;line-height:1}#ResultTitle .count,#QueryTitle .count{color:var(--zone)}#QueryDepth,#ViewShift,#FileShift{font-variant-numeric:tabular-nums}#QueryDepth{cursor:text}#QueryDepthInput,#ViewShiftInput,#FileShiftInput{box-sizing:border-box;padding:0 4px;text-align:center;font-variant-numeric:tabular-nums}#QueryPrev:focus-visible,#QueryNext:focus-visible,#QueryDepth:focus-visible,#ViewPrev:focus-visible,#ViewNext:focus-visible,#ViewShift:focus-visible,#FilePrev:focus-visible,#FileNext:focus-visible,#FileShift:focus-visible{outline:none;box-shadow:var(--shadow-resting-small)}.chartbars.click>div{cursor:pointer}#FilterPanel:not(.query) .queryonly,#FilterPanel.query .searchonly{display:none}#ResultPanel{position:relative}#QueryRange{min-width:0;width:64px}input[type="date"]{width:calc(10ch + 36px);padding-right:6px}#QueryTable col:not(:last-child){width:1px}#QueryTable th{white-space:nowrap}#QueryBody tr.row>td:not(.log){cursor:pointer}td.fileact{white-space:nowrap}.qdown{display:inline-flex;vertical-align:-3px;cursor:pointer}.qdel{flex:none;display:inline-flex;vertical-align:-3px;width:16px;height:16px;margin-left:4px;padding:0;border:0;background:transparent;box-shadow:none;color:var(--button-danger-fgColor-rest);font-size:0;cursor:pointer;--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M11 1.75V3h2.25a.75.75 0 0 1 0 1.5H2.75a.75.75 0 0 1 0-1.5H5V1.75C5 .784 5.784 0 6.75 0h2.5C10.216 0 11 .784 11 1.75ZM4.496 6.675l.66 6.6a.25.25 0 0 0 .249.225h5.19a.25.25 0 0 0 .249-.225l.66-6.6a.75.75 0 0 1 1.492.149l-.66 6.6A1.748 1.748 0 0 1 10.595 15h-5.19a1.75 1.75 0 0 1-1.741-1.575l-.66-6.6a.75.75 0 1 1 1.492-.15ZM6.5 1.75V3h3V1.75a.25.25 0 0 0-.25-.25h-2.5a.25.25 0 0 0-.25.25Z'/%3E%3C/svg%3E")}.qdel::before{content:"";flex:none;width:16px;height:16px;background:currentColor;-webkit-mask:var(--icon) center/16px no-repeat;mask:var(--icon) center/16px no-repeat}.qdel:hover:not(:disabled){background:var(--button-danger-bgColor-hover);color:var(--button-danger-fgColor-hover)}.qdown.nozip{color:var(--fgColor-muted)}.qdown:disabled,.qdel:disabled{opacity:0.4;cursor:default}#DumpPanel{position:relative}#QueryBody tr.open{background:var(--bgColor-accent-muted)}#QueryBody tr.detail{cursor:default;background:var(--bgColor-muted)}#QueryBody tr.detail>td{overflow:visible;white-space:normal}#QueryBody tr.detail>td>div{position:sticky;left:9px;box-sizing:border-box;width:calc(100cqw - 18px)}.querymeta,.querytext{font-family:var(--fontStack-monospace);word-break:break-all}.querymeta{padding:4px 0 0;color:var(--fgColor-muted)}.queryfield{display:flex;flex-wrap:wrap;column-gap:6px}.queryfield>span{min-width:0}.queryfield>span:not(:last-child)::after{content:" ·"}.querytext{padding:4px 0;white-space:pre-wrap}
		#QueryChart{position:relative;display:grid;grid-template-columns:auto auto minmax(0,1fr);column-gap:8px;margin-top:8px;color:var(--fgColor-muted);font-size:0.75rem;line-height:1}.charty{position:relative;height:var(--chartHeight,120px);min-width:24px}.charty span{position:absolute;right:0;transform:translateY(50%);white-space:nowrap}.chartplot{position:relative;height:var(--chartHeight,120px);border-bottom:1px solid var(--borderColor-default)}.chartgrid{position:absolute;left:0;right:0;border-top:1px solid var(--borderColor-muted)}
		.chartbars,.chartx{display:flex;gap:2px}.chartbars{position:absolute;inset:0;align-items:flex-end}.chartbars>div{flex:1;min-width:0;height:100%;display:flex;align-items:flex-end}.chartbars>div:hover{background:var(--control-transparent-bgColor-hover)}.chartbars>div>div{width:100%;border-radius:4px 4px 0 0;background:color-mix(in srgb,var(--zone,var(--chart-barColor)) 50%,#fff)}.chartx{grid-column:3;height:12px;margin-top:4px;overflow:hidden}.chartx span{display:flex;justify-content:center;flex:1;min-width:0;white-space:nowrap}.chartx span.month{color:var(--zone,var(--fgColor-default));font-weight:600}
		.chartlens{display:flex;flex-direction:column;align-self:center}.chartlens>*{box-sizing:border-box;width:28px;height:20px;padding:0;border-radius:0}.chartlens>:first-child{border-radius:var(--borderRadius-medium) var(--borderRadius-medium) 0 0}.chartlens>:last-child{border-radius:0 0 var(--borderRadius-medium) var(--borderRadius-medium)}#LensStep{display:flex;align-items:center;justify-content:center;border:solid var(--borderColor-default);border-width:0 1px;background:var(--button-default-bgColor-rest);color:var(--button-default-fgColor-rest);font-weight:500;font-variant-numeric:tabular-nums;cursor:default}#LensUp:focus-visible,#LensDown:focus-visible{outline:none;box-shadow:var(--shadow-resting-small)}
		.btngroup{display:inline-flex}.btngroup>*{border-radius:0}.btngroup>:first-child{border-radius:var(--borderRadius-medium) 0 0 var(--borderRadius-medium)}.btngroup>:last-child{border-radius:0 var(--borderRadius-medium) var(--borderRadius-medium) 0}.btngroup>:not(:first-child):not(:last-child){border-left-width:0;border-right-width:0}
		.charttip{position:absolute;z-index:3;padding:4px 8px;border-radius:var(--borderRadius-medium);background:var(--overlay-bgColor);box-shadow:var(--shadow-floating-small);color:var(--fgColor-default);font-weight:600;white-space:nowrap;pointer-events:none;transform:translate(-50%,calc(-100% - 4px))}
		.querysplit{position:relative;margin-top:8px;padding-left:calc(var(--fieldWidth,120px) + 14px)}#QueryFieldSplitter{position:absolute;top:0;bottom:0;left:var(--fieldWidth,120px)}.querysplit>.scroll{margin-top:0}#QueryField{position:absolute;top:0;bottom:0;left:0;width:var(--fieldWidth,120px);box-sizing:border-box;overflow-y:auto;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);font-size:0.75rem}.fieldhead{position:sticky;top:0;z-index:1;display:flex;align-items:center;justify-content:space-between;gap:8px;padding:6px 8px;border-bottom:1px solid var(--borderColor-muted);background:var(--bgColor-default);font-weight:600}#QueryFieldList,#QueryFieldList ul{margin:0;padding:0;list-style:none}#QueryFieldList button{display:flex;align-items:flex-start;justify-content:space-between;gap:8px;width:100%;height:auto;min-height:28px;padding:6px 8px;border:0;border-radius:0;background:transparent;box-shadow:none;color:var(--fgColor-default);font-size:inherit;font-weight:400;text-align:left;white-space:normal}#QueryFieldList button:hover:not(:disabled){background:var(--control-transparent-bgColor-hover)}#QueryFieldList .fieldname{font-weight:600}#QueryFieldList li.baseend{border-bottom:1px solid var(--borderColor-muted)}#QueryFieldList button>span:first-child{flex:1;min-width:0;white-space:normal;word-break:break-all;overflow-wrap:anywhere}#QueryFieldList .count{flex:none;color:var(--fgColor-muted);font-variant-numeric:tabular-nums}
		#QuerySaveDialog{min-width:600px}#QuerySaveDialog .setting{grid-template-columns:max-content minmax(0,1fr);align-items:start}#QuerySaveDialog .setting label{line-height:28px}#QuerySaveDialog .setting input{width:100%}.savedlist{max-height:360px;overflow-y:auto;margin:16px 0}#QueryEditTable td{border:0;border-bottom:1px solid var(--borderColor-muted);padding:6px 4px;vertical-align:middle}#QueryEditTable td:first-child{max-width:0;width:100%;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;cursor:grab}#QueryEditTable td.act{width:1px}#QueryEditTable tr.off td:first-child{color:var(--fgColor-disabled)}#QueryEditTable tr.drag{opacity:.5}#QueryEditTable tr.dropbefore td{box-shadow:inset 0 2px 0 var(--zone,var(--bgColor-emphasis))}#QueryEditTable tr.dropafter td{box-shadow:inset 0 -2px 0 var(--zone,var(--bgColor-emphasis))}#QuerySaveDialog:not(.alarm) .alarmrow,#QuerySaveDialog.alarm .queryrow{display:none}#QuerySaveDialog.alarm #QuerySaveQuery{height:96px}#QuerySaveDialog span.alarmrow,#QuerySaveDialog span.queryrow{display:flex;align-items:center;gap:8px;line-height:28px}#QuerySaveDialog label.delay{margin-left:16px}#AlarmTest{margin-right:auto}#QuerySaveDialog span.alarmadd{flex-direction:column;align-items:stretch;gap:4px}.alarminput{display:flex;align-items:center;gap:8px}.alarmlist{display:flex;flex-wrap:wrap;gap:4px;line-height:20px}.alarmlist:empty{display:none}.alarmitem{display:inline-flex;align-items:center;gap:4px;padding:1px 6px 1px 10px;border:1px solid var(--borderColor-done-emphasis);border-radius:999px;background:var(--bgColor-default);color:var(--fgColor-done);font-size:0.75rem;font-weight:500;white-space:nowrap}.alarmlist button.alarmdel{height:auto;min-width:0;margin:0;padding:0 2px;border:0;border-radius:999px;background:transparent;box-shadow:none;color:var(--fgColor-done);font-size:0.875rem;font-weight:600;line-height:1;cursor:pointer}.alarmlist button.alarmdel:hover{background:var(--bgColor-done-muted);color:var(--fgColor-done)}#QuerySaveDialog .setting span.alarmrow input[type="text"],#QuerySaveDialog .alarminput select{flex:1;width:auto}#QuerySaveDialog .setting input[type="number"]{width:80px}#QuerySaveDialog .setting input[type="checkbox"]{width:auto;margin:0}#QuerySaveQuery,#FieldRuleRegex{box-sizing:border-box;width:100%;padding:6px 8px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--bgColor-default);box-shadow:var(--shadow-inset);color:var(--fgColor-default);font-family:var(--fontStack-monospace);font-size:0.75rem;line-height:1.5;resize:vertical}#QuerySaveDialog .toolright{justify-content:flex-end}#QueryFieldDialog{min-width:360px}#QueryFieldDialog .setting{grid-template-columns:auto 280px}#QueryFieldDialog .setting input{width:100%}#QueryFieldDialog .toolright{justify-content:flex-end}#FieldRuleDialog{min-width:600px}#FieldRuleDialog .setting{grid-template-columns:max-content minmax(0,1fr);align-items:start}#FieldRuleDialog .setting label{line-height:28px}#FieldRuleDialog .setting input{width:100%}#FieldRuleDialog .rulehint{color:var(--fgColor-muted);font-size:0.75rem}#FieldRuleDialog .toolright{justify-content:flex-end}
		h1{display:flex;flex-wrap:wrap;align-items:center;gap:4px 12px}#OffText{position:relative;top:3px;color:var(--fgColor-danger);font-size:0.75rem;font-weight:400}#TabBar{display:flex;flex-wrap:wrap;align-items:center;gap:4px 12px;margin-left:auto}body:not([data-mode="main"]) #TabBar{display:none}#TabRefresh{width:28px;padding:0}#TabRefresh:not(:disabled){color:var(--fgColor-muted)}#TabRefresh svg{fill:currentColor}#TabRefresh.turn svg{animation:ManagerTurn .6s ease-in-out}body:not([data-mode="main"]) #TabRefresh{display:none}
		.panel+.panel{margin-top:16px}.filter{display:flex;flex-wrap:wrap;align-items:center;gap:8px 12px}.filter label{display:flex;flex-wrap:wrap;align-items:center;gap:8px}
		select,input{box-sizing:border-box;height:28px;padding:0 8px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background-color:var(--bgColor-default);box-shadow:var(--shadow-inset);color:var(--fgColor-default);font:inherit;font-size:0.75rem}select{min-width:160px;padding-right:24px;background:var(--bgColor-default) url("data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iMTYiIGhlaWdodD0iMTYiIHZpZXdCb3g9IjAgMCAxNiAxNiIgZmlsbD0iIzU4NjA2OSIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj48cGF0aCBkPSJNNC40MjcgOS40MjdsMy4zOTYgMy4zOTZhLjI1MS4yNTEgMCAwMC4zNTQgMGwzLjM5Ni0zLjM5NkEuMjUuMjUgMCAwMDExLjM5NiA5SDQuNjA0YS4yNS4yNSAwIDAwLS4xNzcuNDI3ek00LjQyMyA2LjQ3TDcuODIgMy4wNzJhLjI1LjI1IDAgMDEuMzU0IDBMMTEuNTcgNi40N2EuMjUuMjUgMCAwMS0uMTc3LjQyN0g0LjZhLjI1LjI1IDAgMDEtLjE3Ny0uNDI3eiIgLz48L3N2Zz4=") no-repeat right 4px center/16px;appearance:none}select.hour{min-width:0}.pickwrap{display:flex;align-items:center;gap:8px}#MachinePick{position:relative}#MachineBox{display:block;width:64px;overflow:hidden;text-overflow:ellipsis;line-height:26px;text-align:left;padding:0 24px 0 8px;background:var(--bgColor-default) url("data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iMTYiIGhlaWdodD0iMTYiIHZpZXdCb3g9IjAgMCAxNiAxNiIgZmlsbD0iIzU4NjA2OSIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj48cGF0aCBkPSJNNC40MjcgOS40MjdsMy4zOTYgMy4zOTZhLjI1MS4yNTEgMCAwMC4zNTQgMGwzLjM5Ni0zLjM5NkEuMjUuMjUgMCAwMDExLjM5NiA5SDQuNjA0YS4yNS4yNSAwIDAwLS4xNzcuNDI3ek00LjQyMyA2LjQ3TDcuODIgMy4wNzJhLjI1LjI1IDAgMDEuMzU0IDBMMTEuNTcgNi40N2EuMjUuMjUgMCAwMS0uMTc3LjQyN0g0LjZhLjI1LjI1IDAgMDEtLjE3Ny0uNDI3eiIgLz48L3N2Zz4=") no-repeat right 4px center/16px;box-shadow:var(--shadow-inset);color:var(--fgColor-default);font-weight:400}#MachineMenu{position:absolute;top:calc(100% + 4px);left:0;z-index:20;width:max-content;min-width:100%;max-height:min(480px,70vh);overflow:auto;padding:4px 0;border-radius:var(--borderRadius-large);background:var(--overlay-bgColor);box-shadow:var(--shadow-floating-small)}#MachineMenu label{display:flex;align-items:center;gap:8px;padding:4px 12px;font-size:0.75rem;white-space:nowrap;cursor:pointer}#MachineMenu label:hover{background:var(--control-transparent-bgColor-hover)}#MachineMenu label.all{border-bottom:1px solid var(--borderColor-muted);font-weight:600}#MachineMenu input{flex:none;width:auto;height:auto;margin:0;padding:0;box-shadow:none}
		select:focus,input:focus{border-color:var(--focus-outlineColor);outline:none;box-shadow:inset 0 0 0 1px var(--focus-outlineColor)}:focus-visible{outline:2px solid var(--focus-outlineColor);outline-offset:-2px}.octicon{flex:none;fill:var(--fgColor-muted)}#ResetButton{width:28px;padding:0}#ResetButton .octicon{fill:var(--zone,var(--fgColor-muted))}#ResetButton:disabled .octicon{fill:var(--fgColor-disabled)}#ResetButton.flip .octicon{animation:ResetFlip .5s ease-in-out}@keyframes ResetFlip{from{transform:rotate(0)}to{transform:rotate(180deg)}}.rangeop{color:var(--fgColor-muted);white-space:nowrap}select.hour option{color:var(--fgColor-default)}select.hour option:disabled{color:color-mix(in srgb,var(--fgColor-disabled) 50%,#fff)}#TimerButton{min-width:calc(4ch + 18px);font-variant-numeric:tabular-nums}#TimerButton.on,#TimerButton.on:hover:not(:disabled){color:var(--zone,var(--fgColor-default))}#TimerButton.flash{animation:TimerFlash .8s ease-out}@keyframes TimerFlash{from{border-color:var(--zone,var(--bgColor-emphasis));background:var(--zone,var(--bgColor-emphasis));color:var(--fgColor-onEmphasis)}}
		button{box-sizing:border-box;display:inline-flex;align-items:center;justify-content:center;gap:4px;height:28px;padding:0 8px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--button-default-bgColor-rest);box-shadow:var(--button-default-shadow-resting);color:var(--button-default-fgColor-rest);font:500 0.75rem/1 var(--fontStack-sansSerif);white-space:nowrap;cursor:pointer;user-select:none}
		button:hover:not(:disabled){background:var(--button-default-bgColor-hover)}button:active:not(:disabled){background:var(--button-default-bgColor-active)}button:disabled{border-color:var(--borderColor-disabled);background:var(--button-default-bgColor-disabled);box-shadow:none;color:var(--fgColor-disabled);cursor:not-allowed}.filter+.filter{margin-top:10px}#SearchKey,#QueryKey{flex:1;min-width:135px}.searchbox{display:flex;align-items:center;gap:8px;flex:1 0 100%}
		button.primary{color:var(--zone,var(--button-default-fgColor-rest))}
		button.primary:disabled{color:var(--fgColor-disabled)}
		button.danger,td.act button[data-action="delete"]{color:var(--button-danger-fgColor-rest)}button.danger:hover:not(:disabled),td.act button[data-action="delete"]:hover:not(:disabled){border-color:var(--button-primary-borderColor-rest);background:var(--button-danger-bgColor-hover);box-shadow:var(--shadow-resting-small);color:var(--button-danger-fgColor-hover)}button.danger:disabled,td.act button[data-action="delete"]:disabled{color:var(--button-danger-fgColor-disabled)}
		.scrollwrap{position:relative}.progress{position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);z-index:2;padding:12px 24px;border-radius:var(--borderRadius-large);background:var(--overlay-bgColor);box-shadow:var(--shadow-floating-small);font-weight:600;line-height:1.7;text-align:center;white-space:pre-line;pointer-events:none}
		.title{font-weight:600;word-break:break-all}.scroll{position:relative;overflow:auto;margin-top:8px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium)}.hscroll{position:sticky;bottom:0;z-index:2;overflow-x:auto;overflow-y:hidden;margin-top:-16px;border:1px solid var(--borderColor-default);border-radius:0 0 var(--borderRadius-medium) var(--borderRadius-medium);background:var(--bgColor-default)}.hscroll>div{height:1px}#ResultScroll{height:135px}#ViewScroll{height:480px}
		.scroll::-webkit-scrollbar,.hscroll::-webkit-scrollbar{width:15px;height:15px}.scroll::-webkit-scrollbar-track,.hscroll::-webkit-scrollbar-track,.scroll::-webkit-scrollbar-corner{background:transparent}.scroll::-webkit-scrollbar-thumb,.hscroll::-webkit-scrollbar-thumb{border:0 solid transparent;background:var(--borderColor-emphasis) padding-box}.scroll::-webkit-scrollbar-thumb:vertical{border-width:0 4px;border-radius:7.5px/3.5px}.scroll::-webkit-scrollbar-thumb:horizontal,.hscroll::-webkit-scrollbar-thumb:horizontal{border-width:4px 0;border-radius:3.5px/7.5px}.scroll::-webkit-scrollbar-thumb:hover,.scroll::-webkit-scrollbar-thumb:active,.hscroll::-webkit-scrollbar-thumb:hover,.hscroll::-webkit-scrollbar-thumb:active{background-color:var(--fgColor-muted)}
		.scroll::-webkit-scrollbar-button:single-button,.hscroll::-webkit-scrollbar-button:single-button{display:block;background:no-repeat}.scroll::-webkit-scrollbar-button:single-button:vertical{width:15px;height:13px;background-size:7px 5px}.scroll::-webkit-scrollbar-button:single-button:horizontal,.hscroll::-webkit-scrollbar-button:single-button:horizontal{width:13px;height:15px;background-size:5px 7px}
		.scroll::-webkit-scrollbar-button:single-button:vertical:decrement{background-position:4px 4px;background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 7 5'%3E%3Cpath fill='%23818b98' stroke='%23818b98' stroke-linejoin='round' d='M3.5 .5l3 4h-6z'/%3E%3C/svg%3E")}.scroll::-webkit-scrollbar-button:single-button:vertical:increment{background-position:4px 4px;background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 7 5'%3E%3Cpath fill='%23818b98' stroke='%23818b98' stroke-linejoin='round' d='M.5 .5h6l-3 4z'/%3E%3C/svg%3E")}.scroll:has(>table)::-webkit-scrollbar-button:single-button:vertical:decrement{height:39px;border-top-right-radius:5px;background:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 7 5'%3E%3Cpath fill='%23818b98' stroke='%23818b98' stroke-linejoin='round' d='M3.5 .5l3 4h-6z'/%3E%3C/svg%3E") 4px 30px/7px 5px no-repeat,linear-gradient(var(--bgColor-muted) 25px,var(--borderColor-default) 25px 26px,transparent 26px)}
		.scroll::-webkit-scrollbar-button:single-button:horizontal:decrement,.hscroll::-webkit-scrollbar-button:single-button:horizontal:decrement{background-position:4px 4px;background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 5 7'%3E%3Cpath fill='%23818b98' stroke='%23818b98' stroke-linejoin='round' d='M.5 3.5l4-3v6z'/%3E%3C/svg%3E")}.scroll::-webkit-scrollbar-button:single-button:horizontal:increment,.hscroll::-webkit-scrollbar-button:single-button:horizontal:increment{background-position:4px 4px;background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 5 7'%3E%3Cpath fill='%23818b98' stroke='%23818b98' stroke-linejoin='round' d='M.5 .5l4 3-4 3z'/%3E%3C/svg%3E")}
		.panel>.resulthead:first-child,#ViewPanel>.filter:first-child{margin:-16px -16px 8px;padding:8px 16px;border-bottom:1px solid var(--borderColor-default);border-radius:calc(var(--borderRadius-medium) - 1px) calc(var(--borderRadius-medium) - 1px) 0 0;background:var(--bgColor-muted)}.resulthead .toolright button,#ViewPanel>.filter .toolright button{height:28px;padding:0 8px;gap:4px;font-size:0.75rem}.filter .toolright.row{flex:0 0 100%;justify-content:flex-end}.filter{position:relative}.filter .toolright.fix{position:absolute;top:0;right:0;margin:0}.filter.tight{column-gap:6px}.filter.tight label{gap:6px}
		.splitter{display:flex;align-items:center;height:14px;cursor:ns-resize;touch-action:none}.splitter::before{content:"";flex:1;height:4px;border-radius:2px;background:var(--borderColor-default)}.splitter:hover::before,.splitter.drag::before{background:var(--split)}.splitter+.panel{margin-top:0}
		body[data-mode="view"] #FilterPanel,body[data-mode="view"] #FilterSplitter,body[data-mode="view"] #ResultPanel,body[data-mode="view"] #Splitter{display:none}body[data-mode="view"] #ViewPanel{margin-top:0}body[data-mode="view"] #ViewScroll{height:calc(100vh - 161px)}
		table{border-collapse:collapse;width:100%;font-size:0.75rem}.scroll>table{border-style:hidden hidden none hidden}.scroll>table th{border-bottom-style:hidden}th,td{border:1px solid var(--borderColor-default);padding:4px 8px;text-align:left;vertical-align:top}th{position:sticky;top:0;z-index:1;background:var(--bgColor-muted);box-shadow:inset 0 -1px 0 var(--borderColor-default);color:var(--fgColor-muted);font-weight:600}
		td.num{text-align:right;white-space:nowrap}td.nowrap{white-space:nowrap}td.log{font-family:var(--fontStack-monospace);white-space:nowrap}
		.colgrip{position:absolute;top:0;right:0;bottom:0;width:6px;cursor:col-resize;touch-action:none}th.foldhead{cursor:pointer}.colgrip::after{content:"";position:absolute;top:0;right:0;bottom:0;width:2px}.colgrip:hover::after,.colgrip.drag::after{background:var(--split)}.colguide{position:fixed;z-index:10;width:2px;margin-left:-1px;background:var(--split);pointer-events:none}
		table.fixed{table-layout:fixed}table.fixed th,table.fixed td{overflow:hidden;white-space:nowrap}table.fixed td.log{text-overflow:ellipsis}
		html{overflow-y:scroll}#TabBar button.cur,#TabBar button.cur:hover:not(:disabled),#ProcessPanel button.chip.cur,#ProcessPanel button.chip.cur:hover:not(:disabled),#ProcessSummaryButton.cur,#ProcessSummaryButton.cur:hover:not(:disabled){border-color:var(--zone,var(--bgColor-emphasis));background:var(--zone,var(--bgColor-emphasis));box-shadow:none;color:var(--fgColor-onEmphasis)}#AccountButton,#AccountButton:hover:not(:disabled),#HelpButton,#HelpButton:hover:not(:disabled){color:var(--zone,var(--fgColor-default))}#DumpScroll{height:calc(100vh - 286px);min-height:200px}
		#ViewBody,#FileViewBody,#ViewBody .lno,#FileViewBody .lno{color:#000}#ViewScroll{overflow-anchor:none}
		body[data-mode="field"] #FilterPanel,body[data-mode="field"] #FilterSplitter,body[data-mode="field"] #ResultPanel,body[data-mode="field"] #Splitter,body[data-mode="field"] #ViewPanel{display:none}body[data-mode="field"] #QueryFieldPanel{margin-top:0}body[data-mode="field"] #QueryFieldScroll{max-height:calc(100vh - 197px)}
		body[data-ui="account"]>:not(h1):not(dialog),body[data-ui="account"] #TabBar button:not(#AccountButton),body[data-ui="user"] #AccountButton{display:none!important}#QueryFieldTable col:not(:last-child){width:1px}#QueryFieldTable th{white-space:nowrap;cursor:pointer;user-select:none}#QueryFieldTable .sortmark:not(:empty){margin-left:4px;font-size:0.625rem}#QueryFieldTable td.log{white-space:nowrap}#QueryFieldBody tr.row{cursor:pointer}#QueryFieldPager,#ResultPager,#QueryPager,#QueryFieldPager{justify-content:center;margin-top:8px}#ResultPager,#QueryPager,#QueryFieldPager{gap:2px;margin:1px 0 -15px}#ResultPager button,#QueryPager button,#QueryFieldPager button{height:20px;min-width:20px;padding:0 6px;font-size:0.75rem}
		#FilterPanel[hidden]~#DumpPanel #DumpScroll,#FilterPanel[hidden]~#FilePanel .filesplit{height:calc(100vh - 161px)}
		.filesplit{display:flex;margin-top:8px;height:calc(100vh - 286px);min-height:200px}#FileScroll{flex:none;width:26%;min-width:120px;margin-top:0}.fileview{flex:1;min-width:120px;display:flex;flex-direction:column}#FileViewScroll{flex:1;margin-top:0;overflow-anchor:none}
		#FileTitle{flex:none;width:calc(26% + 2px)}#FileViewTitle{flex:1;min-width:0}
		.vsplitter{flex:none;display:flex;justify-content:center;width:14px;cursor:ew-resize;touch-action:none}.vsplitter::before{content:"";width:4px;border-radius:2px;background:var(--borderColor-default)}.vsplitter:hover::before,.vsplitter.drag::before{background:var(--split)}
		.tree{margin:0;padding:8px;list-style:none}.tree ul{margin:0;padding-left:16px;list-style:none}.tree .node{position:relative;display:flex;align-items:center;gap:4px;min-height:2rem;padding:0 8px;border-radius:var(--borderRadius-medium);cursor:pointer;white-space:nowrap}.tree .node[data-kind="file"]{cursor:default}.tree .node.view{cursor:pointer}
		.tree .node:hover{background:var(--control-transparent-bgColor-hover)}.tree .node.sel{background:var(--control-transparent-bgColor-selected)}.tree .node.sel::after{content:"";position:absolute;top:calc(50% - 12px);left:-8px;width:4px;height:24px;border-radius:var(--borderRadius-medium);background:var(--borderColor-accent-emphasis)}
		.tree .label{display:flex;align-items:center;gap:8px}.tree .tog,.tree .ticon,.qdown::before{flex:none;width:16px;height:16px;font-size:0;background:currentColor;-webkit-mask:var(--icon) center/16px no-repeat;mask:var(--icon) center/16px no-repeat}.tree .tog:empty{visibility:hidden}.tree .info{margin-left:8px;color:var(--fgColor-muted);font-size:0.75rem}.tree .msg{padding:0 8px 0 28px;line-height:2rem;color:var(--fgColor-muted)}
		.tree .tog{color:var(--fgColor-muted);--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M6.22 3.22a.75.75 0 0 1 1.06 0l4.25 4.25a.75.75 0 0 1 0 1.06l-4.25 4.25a.751.751 0 0 1-1.042-.018.751.751 0 0 1-.018-1.042L9.94 8 6.22 4.28a.75.75 0 0 1 0-1.06Z'/%3E%3C/svg%3E")}
		.tree li:has(>ul)>.node>.tog{--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M12.78 5.22a.749.749 0 0 1 0 1.06l-4.25 4.25a.749.749 0 0 1-1.06 0L3.22 6.28a.749.749 0 1 1 1.06-1.06L8 8.939l3.72-3.719a.749.749 0 0 1 1.06 0Z'/%3E%3C/svg%3E")}
		.tree .ticon{color:var(--treeViewItem-leadingVisual-iconColor-rest);--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M1.75 1A1.75 1.75 0 0 0 0 2.75v10.5C0 14.216.784 15 1.75 15h12.5A1.75 1.75 0 0 0 16 13.25v-8.5A1.75 1.75 0 0 0 14.25 3H7.5a.25.25 0 0 1-.2-.1l-.9-1.2C6.07 1.26 5.55 1 5 1H1.75Z'/%3E%3C/svg%3E")}
		
		.qdown{flex:none;width:16px;height:16px;padding:0;border:0;background:transparent;box-shadow:none;color:var(--fgColor-accent);font-size:0;--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M2.75 14A1.75 1.75 0 0 1 1 12.25v-2.5a.75.75 0 0 1 1.5 0v2.5c0 .138.112.25.25.25h10.5a.25.25 0 0 0 .25-.25v-2.5a.75.75 0 0 1 1.5 0v2.5A1.75 1.75 0 0 1 13.25 14Z'/%3E%3Cpath d='M7.25 7.689V2a.75.75 0 0 1 1.5 0v5.689l1.97-1.969a.749.749 0 1 1 1.06 1.06l-3.25 3.25a.749.749 0 0 1-1.06 0L4.22 6.78a.749.749 0 1 1 1.06-1.06l1.97 1.969Z'/%3E%3C/svg%3E")}.qdown::before{content:""}.qdown:hover:not(:disabled){background:var(--control-transparent-bgColor-hover)}
		#ResultTable col:not(:last-child),#DumpTable col:not(:last-child){width:1px}#DumpTable tr>:last-child{position:sticky;right:0;background:var(--bgColor-default);box-shadow:inset 1px 0 0 var(--borderColor-default)}#DumpTable tbody tr:hover>:last-child{background:var(--bgColor-muted)}#DumpTable th:last-child{z-index:2;background:var(--bgColor-muted);box-shadow:inset 1px 0 0 var(--borderColor-default),inset 0 -1px 0 var(--borderColor-default)}#ResultTable th,#DumpTable th{white-space:nowrap}#ResultTable th,#ResultTable td,#QueryTable th,#QueryTable td{border-left:0;border-right:0;box-shadow:inset 1px 0 0 var(--borderColor-default)}#ResultTable th,#QueryTable th{box-shadow:inset 1px 0 0 var(--borderColor-default),inset 0 -1px 0 var(--borderColor-default)}#ResultTable td:first-child,#QueryTable td:first-child{box-shadow:none}#ResultTable th:first-child,#QueryTable th:first-child{box-shadow:inset 0 -1px 0 var(--borderColor-default)}#DumpBody tr{cursor:default}td.act{white-space:nowrap}td.act button{height:20px;padding:0 8px;font-size:0.75rem}td.act button+button{margin-left:4px}
		dialog{top:30%;bottom:auto;margin:0 auto;transform:translateY(-30%);padding:16px;border:0;border-radius:var(--borderRadius-large);background:var(--overlay-bgColor);color:var(--fgColor-default);box-shadow:var(--shadow-floating-small)}dialog::backdrop{background:var(--overlay-backdrop-bgColor)}
		.setting{display:grid;grid-template-columns:auto 110px auto;align-items:center;gap:8px 12px;margin:16px 0}.setting select{width:100%;min-width:0}.hint{color:var(--fgColor-muted)}#SettingDialog{width:max-content}#SettingDialog .setting{grid-template-columns:max-content 110px max-content}#SettingDialog .setting input,#SettingDialog .setting select{box-sizing:border-box;width:110px}#SettingDialog .toolright{justify-content:flex-end}#SettingDialog.query .srow,#SettingDialog:not(.query) .qrow{display:none}#QuerySettingDialog{width:400px}#QuerySettingDialog[open]{display:flex;flex-direction:column}#QueryEditScroll{flex:0 1 600px;min-height:0;max-height:none}#QuerySettingDialog .toolright{justify-content:flex-end}#AlarmHistoryDialog{width:400px}#AlarmHistoryDialog[open]{display:flex;flex-direction:column}#AlarmHistoryScroll{flex:0 1 600px;min-height:0;max-height:none}#AlarmHistoryTable td{border:0;border-bottom:1px solid var(--borderColor-muted);padding:6px 4px;vertical-align:middle;white-space:nowrap}#AlarmHistoryTable td.key{max-width:0;width:100%;overflow:hidden;text-overflow:ellipsis;cursor:pointer}#AlarmHistoryTable td.key:hover{text-decoration:underline}#AlarmHistoryTable td.fail{color:var(--fgColor-danger)}#AlarmHistoryTable td.empty{color:var(--fgColor-muted);text-align:center}#AlarmHistoryDialog .toolright,#AlarmHistoryViewDialog .toolright{justify-content:flex-end}#AlarmHistoryViewDialog{width:600px}#AlarmHistoryViewInfo{margin:4px 0 0}#AlarmHistoryViewBody.link{cursor:pointer}#AlarmHistoryViewBody hr{margin:4px 0;border:0;border-top:1px solid var(--borderColor-default)}#AlarmHistoryViewBody{max-height:60vh;overflow:auto;margin:12px 0 16px;padding:8px;border:1px solid var(--borderColor-muted);border-radius:var(--borderRadius-medium);background:var(--bgColor-muted);font-family:var(--fontStack-monospace);font-size:0.75rem;line-height:1.5;white-space:pre-wrap;word-break:break-all}#DownDialog{min-width:320px;max-width:600px}#AccountDialog{box-sizing:border-box;width:400px;height:550px}#AccountDialog[open]{display:flex;flex-direction:column}#AccountEditDialog{width:480px}#DownDialog .toolright,#AccountDialog .toolright,#AccountEditDialog .toolright{justify-content:flex-end}#AccountTable td{border:0;border-bottom:1px solid var(--borderColor-muted);padding:6px 4px;vertical-align:middle}#AccountDialog .savedlist{flex:0 1 auto;min-height:0;max-height:none;margin-bottom:8px}#AccountPager{flex:none;height:28px;margin:0 0 12px}#AccountDialog .toolright{margin-top:auto}#AccountTable td:first-child{width:100%}#AccountTable td.act{width:1px;color:var(--fgColor-muted);white-space:nowrap}#AccountEditDialog .setting{grid-template-columns:max-content minmax(0,1fr)}#AccountEditDialog .setting input:not([type="checkbox"]){width:100%}#AccountEditMenu,#AccountEditApi{display:flex;flex-wrap:wrap;gap:4px 12px;line-height:28px}#AccountEditMenu label,#AccountEditApi label{display:inline-flex;align-items:center;gap:4px}#AccountEditMenu input,#AccountEditApi input{margin:0}#AccountEditDialog.delete .editrow{display:none}#AccountEditDialog:not(.edit) .keyrow{display:none}#AccountEditDialog .keybox{display:flex;align-items:center;justify-content:space-between;gap:8px;line-height:28px}#AccountEditKey,#AccountEditToken{font-family:var(--fontStack-monospace);word-break:break-all;line-height:28px}#AccountEditKey.pending{color:var(--fgColor-accent);font-weight:600}#AccountEditDialog .adminbox{display:flex;align-items:center;gap:8px}#AccountEditDialog .adminbox input{flex:1;min-width:0}#DownName{margin:12px 0 4px;color:var(--fgColor-default);font-weight:600;word-break:break-all}#DownPath{margin:0 0 16px;word-break:break-all}#DeleteDialog{min-width:320px;max-width:600px}#DeleteDialog .toolright{justify-content:flex-end}#DeleteText{margin:12px 0 16px;color:var(--fgColor-default);word-break:break-all}#DeleteName{margin:12px 0 4px;color:var(--fgColor-default);font-weight:600;word-break:break-all}#DeleteName:empty{display:none}#DeleteName:not(:empty)+#DeleteText{margin-top:0}#HelpDialog{box-sizing:border-box;width:max-content;max-width:calc(100vw - 32px)}#HelpScroll{overflow-y:auto;margin-bottom:12px}#HelpTable{width:100%;margin-bottom:0;border-collapse:collapse;font-size:0.875rem}#HelpTable tbody th{position:static}#HelpTable td:first-child{font-weight:600;white-space:nowrap}#HelpTable tr.group th{text-align:left}#HelpTabs{margin-bottom:8px}#HelpTabs button.cur,#HelpTabs button.cur:hover:not(:disabled){border-color:var(--zone,var(--bgColor-emphasis));background:var(--zone,var(--bgColor-emphasis));box-shadow:none;color:var(--fgColor-onEmphasis)}#HelpTable[data-help="common"] tr:not([data-help="common"]),#HelpTable[data-help="rule"] tr:not([data-help="rule"]),#HelpTable[data-help="query"] tr:not([data-help="query"]),#HelpTable[data-help="search"] tr:not([data-help="search"]),#HelpTable[data-help="manager"] tr:not([data-help="manager"]),#HelpTable[data-help="process"] tr:not([data-help="process"]){display:none}#HelpTable tr.sep td{border-top:2px solid var(--borderColor-default)}#HelpDialog .toolright{justify-content:flex-end}
		tbody tr{cursor:pointer}tbody tr:hover{background:var(--bgColor-muted)}tbody tr.sel{background:var(--bgColor-accent-muted)}mark{background:var(--highlight-neutral-bgColor);color:inherit;padding:0}
		[hidden]{display:none!important}.resulthead{display:flex;flex-wrap:wrap;align-items:center;gap:6px 12px}.toolright{display:flex;flex-wrap:wrap;align-items:center;gap:4px 8px;margin-left:auto}.pager{display:flex;flex-wrap:wrap;align-items:center;gap:4px}.pager button{min-width:28px;padding:0 8px;border-color:transparent;background:transparent;box-shadow:none;color:var(--fgColor-default);font-weight:400}.pager button:hover:not(:disabled){border-color:var(--borderColor-default);background:transparent}
		.pager button.cur,.pager button.cur:hover:not(:disabled){border-color:transparent;background:var(--zone,var(--bgColor-emphasis));box-shadow:none;color:var(--fgColor-onEmphasis)}
		pre{margin:0;padding:8px;font:400 0.8125rem/1.5 var(--fontStack-monospace)}.line{display:block;white-space:pre}.line.target{background:var(--bgColor-accent-muted)}.lno{color:var(--fgColor-muted);user-select:none}
		#ManagerPanel{display:grid;grid-template-columns:var(--managerWidth,max-content) 14px minmax(0,1fr);align-items:start;margin-top:16px;--zone-dev:#1b7a3b;--zone-dev-bg:#e2f4e7;--zone-alpha:#2f6fde;--zone-alpha-bg:#e7effc;--zone-real:#c42b2b;--zone-real-bg:#fce6e6}#ManagerPanel>.panel{position:relative;min-width:0;margin-top:0}#ManagerPanel>.vsplitter{align-self:stretch;width:auto}@media (max-width:1100px){#ManagerPanel{grid-template-columns:minmax(0,1fr);row-gap:16px}#ManagerPanel>.vsplitter{display:none}}
		#ManagerSideTab{display:flex;flex-wrap:wrap;align-items:center;gap:8px;min-height:48px;margin:-16px -16px 16px;padding:0 8px;box-shadow:inset 0 -1px 0 var(--borderColor-muted)}#ManagerSideTab button:not(.iconbtn){height:32px;padding:0 8px;border-color:transparent;background:transparent;box-shadow:none;color:var(--fgColor-default);font-size:0.875rem;font-weight:400}#ManagerSideTab button:not(.iconbtn):hover:not(:disabled){border-color:transparent;background:var(--control-transparent-bgColor-hover);box-shadow:none}#ManagerSideTab button.cur,#ManagerSideTab button.cur:hover:not(:disabled){border-color:var(--zone,var(--bgColor-emphasis));background:var(--zone,var(--bgColor-emphasis));color:var(--fgColor-onEmphasis);font-weight:600}#ManagerSidePanel>div>.resulthead:first-child{margin:0;padding:0;border:0;border-radius:0;background:none}
		#ManagerPanel .chip{display:inline-flex;align-items:center;height:20px;padding:0 7px;border:1px solid var(--borderColor-default);border-radius:624.9375rem;font-size:0.75rem;font-weight:500;line-height:1;white-space:nowrap}#ManagerPanel .chip.zone-dev{border-color:transparent;background:var(--zone-dev-bg);color:var(--zone-dev)}#ManagerPanel .chip.zone-alpha{border-color:transparent;background:var(--zone-alpha-bg);color:var(--zone-alpha)}#ManagerPanel .chip.zone-real{border-color:transparent;background:var(--zone-real-bg);color:var(--zone-real)}#ManagerPanel .chip.off{color:var(--fgColor-muted)}#ManagerPanel .chip.before{border-color:#cf222e;color:var(--fgColor-danger)}#ManagerPanel .chip.after,#ManagerPanel .chip.cds{border-color:#1a7f37;color:var(--fgColor-success)}
		#ManagerPanel .tag{margin-left:4px;font-size:0.75rem}#ManagerPanel a,#ManagerPanel a:visited,#ProcessPanel a,#ProcessPanel a:visited{color:var(--fgColor-accent);text-decoration:none}#ManagerPanel a:hover,#ProcessPanel a:hover{text-decoration:underline}#ManagerPanel .mono,#ProcessPanel .mono{font-family:var(--fontStack-monospace)}#ManagerPanel .error,#ProcessPanel .error{box-sizing:border-box;width:0;min-width:100%;margin-top:8px;padding:8px 16px;border:1px solid #ff818266;border-radius:var(--borderRadius-medium);background:#ffebe9;word-break:break-all}#ManagerPanel .subtitle{margin-top:16px}#ManagerPanel td{vertical-align:middle}#ManagerPanel td.num{font-variant-numeric:tabular-nums}#ManagerPanel td.empty{text-align:center;color:var(--fgColor-muted)}#ManagerPanel tr.down td{color:var(--fgColor-muted)}#ManagerPanel tr.spacer td{height:12px;padding:0}#ManagerPanel tbody tr.spacer:hover{background:transparent}#ManagerPanel details{margin-top:16px}#ManagerPanel summary{cursor:pointer;font-weight:600}#ManagerPanel .scroll.tall{max-height:90vh}#ManagerPanel .scroll.tall.vbar{scrollbar-gutter:stable}
		#ManagerPanel button.iconbtn{width:28px;padding:0}#ManagerPanel button.iconbtn:not(:disabled){color:var(--fgColor-muted)}#ManagerPanel button.iconbtn svg{fill:currentColor}#ManagerPanel button.iconbtn.turn svg{animation:ManagerTurn .6s ease-in-out}@keyframes ManagerTurn{from{transform:rotate(0)}to{transform:rotate(360deg)}}#ManagerChartBox{position:relative;height:630px;margin-top:8px}#ManagerMonitorFrame{display:block;width:100%;height:105vh;margin-top:8px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:#fff}#ManagerPatchDialog{min-width:320px;max-width:600px}#ManagerPatchDialog .managerpatch{display:grid;grid-template-columns:auto minmax(0,1fr);align-items:center;gap:8px 12px;margin:16px 0}#ManagerPatchInput{width:100%}
		#ProcessPanel,#ProcessToasts,#HelpTable{--bgColor-neutral-muted:#818b981f;--bgColor-success-emphasis:#1f883d;--bgColor-danger-muted:#ffebe9;--bgColor-danger-emphasis:#cf222e;--bgColor-attention-muted:#fff8c5;--bgColor-severe-muted:#fff1e5;--bgColor-done-muted:#fbefff;--fgColor-attention:#9a6700;--fgColor-severe:#bc4c00;--fgColor-done:#8250df;--borderColor-translucent:#1f232826;--borderColor-success-emphasis:#1a7f37;--borderColor-danger-emphasis:#cf222e;--borderColor-danger-muted:#ff818266;--borderColor-attention-emphasis:#9a6700;--borderColor-attention-muted:#d4a72c66;--borderColor-severe-emphasis:#bc4c00;--borderColor-done-emphasis:#8250df;--button-default-borderColor-rest:#d1d9e0;--buttonCounter-default-bgColor-rest:#818b981f;--controlTrack-bgColor-rest:#e6eaef;--controlTrack-borderColor-rest:#d1d9e0;--controlKnob-bgColor-rest:#ffffff;--controlKnob-borderColor-rest:#d1d9e0;--borderRadius-full:624.9375rem;--l1-bg:color-mix(in srgb,var(--zone,var(--bgColor-emphasis)) 50%,#fff);--l1-hover:color-mix(in srgb,var(--l1-bg) 90%,#000);--l1-line:color-mix(in srgb,var(--l1-bg) 80%,#000)}
		#ProcessPanel{margin-top:16px}#ProcessPanel .muted{color:var(--fgColor-muted)}#ProcessPanel .spacer{flex:1}#ProcessVersion{color:var(--fgColor-muted);font-size:0.75rem}#ProcessTest{display:inline-flex;gap:4px}#ProcessStale{display:flex;align-items:center;gap:10px;margin:0 0 16px;padding:8px 16px;border:1px solid var(--borderColor-attention-muted);border-radius:var(--borderRadius-medium);background:var(--bgColor-attention-muted);font-size:0.875rem}#ProcessStale button{margin-left:auto}#ProcessSummary{margin-bottom:16px}#ProcessCards{display:flex;gap:8px;margin-bottom:16px;overflow-x:auto}#ProcessSearch{width:240px}#ProcessPanel button.card{display:block;flex:1 1 0;min-width:150px;height:auto;padding:8px 12px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--bgColor-default);box-shadow:none;color:var(--fgColor-default);font:400 0.875rem/1.5 var(--fontStack-sansSerif);text-align:left;white-space:normal}#ProcessPanel button.card:hover:not(:disabled),#ProcessPanel button.card:active:not(:disabled){border-color:var(--zone,var(--borderColor-emphasis));background:var(--bgColor-default)}#ProcessPanel .card>span{display:block}#ProcessPanel .card .c-name{margin-bottom:4px;font-weight:600}#ProcessPanel .c-num{font-size:1.25rem;font-weight:600}#ProcessPanel .c-num small{color:var(--fgColor-muted);font-size:0.75rem;font-weight:400}#ProcessPanel .card .c-tags{display:flex;flex-wrap:wrap;gap:4px;min-height:20px;margin-top:4px}
		#ProcessPanel .tag{display:inline-flex;align-items:center;gap:4px;height:20px;padding:0 7px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-full);background:var(--bgColor-default);font-size:0.75rem;font-weight:500;line-height:1;white-space:nowrap}#ProcessPanel .tag.fail{border-color:var(--borderColor-danger-emphasis);color:var(--fgColor-danger)}#ProcessPanel .tag.warn{border-color:var(--borderColor-attention-emphasis);color:var(--fgColor-attention)}#ProcessPanel .tag.run{border-color:var(--borderColor-success-emphasis);color:var(--fgColor-success)}#ProcessPanel .tag.start{border-color:var(--borderColor-accent-emphasis);color:var(--fgColor-accent)}#ProcessPanel .tag.off{color:var(--fgColor-muted)}#ProcessPanel .spin{display:inline-block;width:10px;height:10px;border:2px solid currentColor;border-right-color:transparent;border-radius:50%;animation:ProcessSpin .7s linear infinite}@keyframes ProcessSpin{to{transform:rotate(360deg)}}#ProcessPanel .chips{display:flex;flex-wrap:wrap;gap:4px}#ProcessPanel button.chip .n{display:inline-block;min-width:20px;padding:0 6px;border:1px solid transparent;border-radius:var(--borderRadius-full);background:var(--buttonCounter-default-bgColor-rest);color:var(--fgColor-default);font-size:0.75rem;font-weight:500;line-height:18px;text-align:center}#ProcessPanel button.chip.cur .n{background:color-mix(in srgb,var(--zone,var(--bgColor-emphasis)) 70%,#000);color:inherit}
		#ProcessTables .scroll{margin-top:0}#ProcessTables .scroll>table{min-width:860px;border-bottom-style:hidden}#ProcessTables td{height:22px;padding-top:0;padding-bottom:0;vertical-align:middle;text-overflow:ellipsis}#ProcessTables tr.sec-row td{height:26px}#ProcessTables .sec-block{margin-bottom:50px}#ProcessTables .sec-block:last-child{margin-bottom:0}#ProcessTables .sec-block.tier-gap{margin-top:150px}#ProcessTables .c-btn{width:80px;padding-right:4px}#ProcessTables .c-state{width:110px;text-align:center}#ProcessTables .c-machine{width:130px}#ProcessTables .c-pid{width:64px}#ProcessTables .c-commit{width:96px}#ProcessTables .c-act{width:72px;text-align:right;white-space:nowrap}#ProcessTables button.th-help{height:auto;padding:0;border:0;background:none;box-shadow:none;color:inherit;font:inherit}#ProcessTables button.th-help:hover:not(:disabled),#ProcessTables button.th-help:active:not(:disabled){background:none;color:var(--fgColor-accent)}#ProcessPanel .empty{padding:40px;color:var(--fgColor-muted);text-align:center;white-space:normal}
		#ProcessTables .row-head{display:flex;align-items:center;gap:10px}#ProcessTables .row-head .acts{display:flex;align-items:center;gap:4px;margin-left:auto}#ProcessTables .chev{display:inline-block;transition:transform .12s}#ProcessTables .open>td .chev{transform:rotate(90deg)}#ProcessTables .sec-name{font-size:0.875rem;font-weight:600}#ProcessTables .grp-name{font-weight:600}#ProcessTables tr.sec-row td{border-bottom-color:var(--l1-line);background:var(--l1-bg);color:var(--fgColor-default);cursor:pointer}#ProcessTables tr.sec-row:hover td{background:var(--l1-hover)}#ProcessTables tr.sec-row.gray{--l1-bg:color-mix(in srgb,var(--bgColor-neutral-emphasis) 80%,#fff);--l1-hover:color-mix(in srgb,var(--l1-bg) 90%,#000);--l1-line:color-mix(in srgb,var(--l1-bg) 80%,#000)}#ProcessTables tr.sec-row.gray td,#ProcessTables tr.sec-row.gray .muted,#ProcessTables tr.sec-row.gray .chev,#ProcessTables tr.sec-row.gray button.kebab{color:var(--fgColor-onEmphasis)}#ProcessTables tr.grp-row td{background:var(--bgColor-neutral-muted);color:var(--fgColor-default);cursor:pointer}#ProcessTables tr.grp-row:hover td{background:linear-gradient(var(--control-transparent-bgColor-hover),var(--control-transparent-bgColor-hover)) var(--bgColor-neutral-muted)}
		#ProcessTables tr.grp-row .pair{margin-left:14px}#ProcessTables tr.srv-row td.c-btn .pair{margin-left:28px}#ProcessTables tr.sec-row .muted,#ProcessTables tr.sec-row .chev,#ProcessTables tr.sec-row button.kebab,#ProcessTables tr.grp-row button.kebab{color:var(--fgColor-default)}#ProcessTables tr.grp-row .muted,#ProcessTables tr.grp-row .chev{color:var(--fgColor-muted)}#ProcessTables tr.sec-row .pbtn:not(.lit):not(.part):not(.pend):not(.fail),#ProcessTables tr.grp-row .pbtn:not(.lit):not(.part):not(.pend):not(.fail){border-color:var(--borderColor-translucent);background:var(--button-default-bgColor-rest);color:var(--fgColor-default)}#ProcessTables tr.sec-row .pbtn.on.part,#ProcessTables tr.grp-row .pbtn.on.part{background:linear-gradient(var(--bgColor-attention-muted),var(--bgColor-attention-muted)) var(--bgColor-default);color:var(--fgColor-attention)}#ProcessTables tr.srv-row.active td{box-shadow:inset 3px 0 0 var(--borderColor-accent-emphasis)}#ProcessTables tr.srv-row td.c-name{padding-left:34px;font-weight:500}#ProcessTables tr.srv-row.failed td.c-name{color:var(--fgColor-danger)}#ProcessTables tr.flash td{animation:ProcessFlash 1.6s ease-out}@keyframes ProcessFlash{from{background:var(--bgColor-accent-muted)}}
		#ProcessPanel button.kebab{width:22px;height:18px;padding:0;border-color:transparent;background:transparent;box-shadow:none;color:var(--fgColor-muted)}#ProcessPanel button.kebab:hover:not(:disabled),#ProcessPanel button.kebab:active:not(:disabled){background:var(--control-transparent-bgColor-hover)}:is(#ProcessPanel,#HelpTable) .pair{display:inline-flex;flex:none;gap:4px}:is(#ProcessPanel,#HelpTable) .pbtn{display:inline-flex;align-items:center;justify-content:center;gap:0;width:18px;height:18px;padding:0;border:1px solid var(--button-default-borderColor-rest);border-radius:var(--borderRadius-medium);background:var(--button-default-bgColor-rest);box-shadow:none;color:var(--fgColor-default);font-size:0.75rem;font-weight:600;line-height:1;vertical-align:middle;cursor:pointer}:is(#ProcessPanel,#HelpTable) .pbtn:hover:not(.lit):not(:disabled){background:var(--button-default-bgColor-hover)}:is(#ProcessPanel,#HelpTable) .pbtn.on,:is(#ProcessPanel,#HelpTable) .pbtn.off{border-color:var(--borderColor-emphasis)!important;font-size:0}:is(#ProcessPanel,#HelpTable) .pbtn.on::before{content:"";color:var(--fgColor-default);--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath transform='matrix(1.797 0 0 1.097 -5.78 -0.777)' d='m6.427 4.427 3.396 3.396a.25.25 0 0 1 0 .354l-3.396 3.396A.25.25 0 0 1 6 11.396V4.604a.25.25 0 0 1 .427-.177Z'/%3E%3C/svg%3E")}:is(#ProcessPanel,#HelpTable) .pbtn.off::before{content:"";color:var(--fgColor-default);--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M5.75 4h4.5c.966 0 1.75.784 1.75 1.75v4.5A1.75 1.75 0 0 1 10.25 12h-4.5A1.75 1.75 0 0 1 4 10.25v-4.5C4 4.784 4.784 4 5.75 4Z'/%3E%3C/svg%3E")}
		:is(#ProcessPanel,#HelpTable) .pbtn.lit{cursor:not-allowed}:is(#ProcessPanel,#HelpTable) .pbtn.on.lit{border-color:var(--borderColor-success-emphasis);background:var(--bgColor-success-emphasis);color:var(--fgColor-onEmphasis)}:is(#ProcessPanel,#HelpTable) .pbtn.off.lit{background:var(--button-default-bgColor-rest);color:var(--fgColor-default)}:is(#ProcessPanel,#HelpTable) .pbtn.on.part{border-color:var(--borderColor-attention-emphasis);background:linear-gradient(var(--bgColor-attention-muted),var(--bgColor-attention-muted)) var(--bgColor-default);color:var(--fgColor-attention)}:is(#ProcessPanel,#HelpTable) .pbtn.off.fail{border-color:var(--borderColor-danger-emphasis);background:var(--bgColor-danger-emphasis);color:var(--fgColor-onEmphasis)}:is(#ProcessPanel,#HelpTable) .pbtn.pend{border-color:var(--borderColor-severe-emphasis);background:linear-gradient(var(--bgColor-severe-muted),var(--bgColor-severe-muted)) var(--bgColor-default);color:var(--fgColor-severe);animation:ProcessPulse 1s infinite}:is(#ProcessPanel,#HelpTable) .pbtn.down{color:var(--fgColor-done);font-size:0}:is(#ProcessPanel,#HelpTable) .pbtn.down::before{content:"";--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M2.75 14A1.75 1.75 0 0 1 1 12.25v-2.5a.75.75 0 0 1 1.5 0v2.5c0 .138.112.25.25.25h10.5a.25.25 0 0 0 .25-.25v-2.5a.75.75 0 0 1 1.5 0v2.5A1.75 1.75 0 0 1 13.25 14Z'/%3E%3Cpath d='M7.25 7.689V2a.75.75 0 0 1 1.5 0v5.689l1.97-1.969a.749.749 0 1 1 1.06 1.06l-3.25 3.25a.749.749 0 0 1-1.06 0L4.22 6.78a.749.749 0 1 1 1.06-1.06l1.97 1.969Z'/%3E%3C/svg%3E")}:is(#ProcessPanel,#HelpTable) .pbtn.down.pend{border-color:var(--borderColor-done-emphasis);background:linear-gradient(var(--bgColor-done-muted),var(--bgColor-done-muted)) var(--bgColor-default);color:var(--fgColor-done)}:is(#ProcessPanel,#HelpTable) .pbtn:disabled{opacity:.45;cursor:not-allowed}:is(#ProcessPanel,#HelpTable) .pbtn.pend:disabled,:is(#ProcessPanel,#HelpTable) .pbtn.lit:disabled,:is(#ProcessPanel,#HelpTable) .pbtn.off:disabled{opacity:1}:is(#ProcessPanel,#HelpTable) .pbtn.off:disabled:not(.pend):not(.fail){background:var(--button-default-bgColor-rest)}@keyframes ProcessPulse{50%{opacity:.25}}
		:is(#ProcessPanel,#HelpTable) .badge{display:inline-flex;align-items:center;gap:4px;height:20px;padding:0 7px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-full);font-size:0.75rem;font-weight:500;line-height:1}:is(#ProcessPanel,#HelpTable) .badge::before{content:"";width:7px;height:7px;border-radius:50%;background:currentColor}:is(#ProcessPanel,#HelpTable) .badge.running{border-color:var(--borderColor-success-emphasis);color:var(--fgColor-success)}:is(#ProcessPanel,#HelpTable) .badge.starting{border-color:var(--borderColor-accent-emphasis);color:var(--fgColor-accent)}:is(#ProcessPanel,#HelpTable) .badge.stopping{border-color:var(--borderColor-severe-emphasis);color:var(--fgColor-severe)}:is(#ProcessPanel,#HelpTable) .badge.stopped{color:var(--fgColor-muted)}:is(#ProcessPanel,#HelpTable) .badge.stopped::before{width:5px;height:5px;border:1.5px solid currentColor;background:transparent}:is(#ProcessPanel,#HelpTable) .badge.failed{border-color:var(--borderColor-danger-emphasis);color:var(--fgColor-danger)}:is(#ProcessPanel,#HelpTable) .badge.failed::before{width:12px;height:12px;border-radius:0;background:currentColor;-webkit-mask:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 12 12'%3E%3Cpath d='M2.22 2.22a.749.749 0 0 1 1.06 0L6 4.939 8.72 2.22a.749.749 0 1 1 1.06 1.06L7.061 6 9.78 8.72a.749.749 0 1 1-1.06 1.06L6 7.061 3.28 9.78a.749.749 0 1 1-1.06-1.06L4.939 6 2.22 3.28a.749.749 0 0 1 0-1.06Z'/%3E%3C/svg%3E") center/12px no-repeat;mask:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 12 12'%3E%3Cpath d='M2.22 2.22a.749.749 0 0 1 1.06 0L6 4.939 8.72 2.22a.749.749 0 1 1 1.06 1.06L7.061 6 9.78 8.72a.749.749 0 1 1-1.06 1.06L6 7.061 3.28 9.78a.749.749 0 1 1-1.06-1.06L4.939 6 2.22 3.28a.749.749 0 0 1 0-1.06Z'/%3E%3C/svg%3E") center/12px no-repeat}:is(#ProcessPanel,#HelpTable) .badge.downloading{border-color:var(--borderColor-done-emphasis);color:var(--fgColor-done)}:is(#ProcessPanel,#HelpTable) .badge.starting::before,:is(#ProcessPanel,#HelpTable) .badge.stopping::before,:is(#ProcessPanel,#HelpTable) .badge.downloading::before{animation:ProcessPulse 1s infinite}:is(#ProcessPanel,#HelpTable) .commit{padding:.2em .4em;border-radius:var(--borderRadius-medium);background:var(--bgColor-neutral-muted);font-family:var(--fontStack-monospace);font-size:0.75rem}:is(#ProcessPanel,#HelpTable) .commit.drift{border:1px solid var(--borderColor-attention-muted);background:var(--bgColor-attention-muted);color:var(--fgColor-attention)}
		#ProcessPanel .chev,#ProcessPanel button.copy,#ProcessPanel button.cb-btn,#ProcessPanel button.kebab{font-size:0}#ProcessPanel .chev,#ProcessPanel button.copy::before,#ProcessPanel button.cb-btn::before,#ProcessPanel button.kebab::before,:is(#ProcessPanel,#HelpTable) .pbtn.on::before,:is(#ProcessPanel,#HelpTable) .pbtn.off::before,:is(#ProcessPanel,#HelpTable) .pbtn.down::before{flex:none;width:16px;height:16px;background:currentColor;-webkit-mask:var(--icon) center/16px no-repeat;mask:var(--icon) center/16px no-repeat}#ProcessPanel button.kebab::before{content:"";--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M8 9a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3ZM1.5 9a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3Zm13 0a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3Z'/%3E%3C/svg%3E")}#ProcessPanel button.copy::before{content:"";display:block;--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M0 6.75C0 5.784.784 5 1.75 5h1.5a.75.75 0 0 1 0 1.5h-1.5a.25.25 0 0 0-.25.25v7.5c0 .138.112.25.25.25h7.5a.25.25 0 0 0 .25-.25v-1.5a.75.75 0 0 1 1.5 0v1.5A1.75 1.75 0 0 1 9.25 16h-7.5A1.75 1.75 0 0 1 0 14.25Z'/%3E%3Cpath d='M5 1.75C5 .784 5.784 0 6.75 0h7.5C15.216 0 16 .784 16 1.75v7.5A1.75 1.75 0 0 1 14.25 11h-7.5A1.75 1.75 0 0 1 5 9.25Zm1.75-.25a.25.25 0 0 0-.25.25v7.5c0 .138.112.25.25.25h7.5a.25.25 0 0 0 .25-.25v-7.5a.25.25 0 0 0-.25-.25Z'/%3E%3C/svg%3E")}#ProcessPanel button.cb-btn::before{content:"";--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='m4.427 7.427 3.396 3.396a.25.25 0 0 0 .354 0l3.396-3.396A.25.25 0 0 0 11.396 7H4.604a.25.25 0 0 0-.177.427Z'/%3E%3C/svg%3E")}#ProcessPanel .chev{--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M6.22 3.22a.75.75 0 0 1 1.06 0l4.25 4.25a.75.75 0 0 1 0 1.06l-4.25 4.25a.751.751 0 0 1-1.042-.018.751.751 0 0 1-.018-1.042L9.94 8 6.22 4.28a.75.75 0 0 1 0-1.06Z'/%3E%3C/svg%3E")}
		#ProcessMenu{position:fixed;z-index:60;min-width:180px;padding:4px 0;border-radius:var(--borderRadius-large);background:var(--overlay-bgColor);box-shadow:var(--shadow-floating-small)}#ProcessMenu button{display:flex;justify-content:flex-start;width:100%;height:auto;padding:6px 12px;border:0;border-radius:0;background:transparent;box-shadow:none;color:var(--fgColor-default);font-size:0.75rem;font-weight:400;text-align:left}#ProcessMenu button:hover:not(:disabled){background:var(--control-transparent-bgColor-hover)}#ProcessMenu button.danger{color:var(--button-danger-fgColor-rest)}#ProcessMenu button.danger:hover:not(:disabled){background:var(--button-danger-bgColor-hover);box-shadow:none;color:var(--button-danger-fgColor-hover)}#ProcessMenu hr{margin:4px 0;border:0;border-top:1px solid var(--borderColor-muted)}#ProcessDrawer{position:fixed;top:0;right:0;bottom:0;z-index:40;display:flex;flex-direction:column;width:440px;max-width:100vw;background:var(--overlay-bgColor);box-shadow:var(--shadow-floating-small);transform:translateX(105%);transition:transform .18s ease}#ProcessDrawer.show{transform:none}#ProcessDrawer>.resulthead{padding:8px 16px;border-bottom:1px solid var(--borderColor-default);background:var(--bgColor-muted)}
		#ProcessDrawer .d-body{flex:1;overflow:auto;padding:8px 16px 16px}#ProcessDrawer .crumb{color:var(--fgColor-muted);font-size:0.75rem}#ProcessDrawer .d-badge{margin-top:4px}#ProcessDrawer .d-sec{margin-top:14px}#ProcessDrawer .d-sec h4{margin:0 0 6px;color:var(--fgColor-muted);font-size:0.75rem;font-weight:600}#ProcessDrawer summary{color:var(--fgColor-muted);font-size:0.75rem;cursor:pointer}#ProcessDrawer dl.kv{display:grid;grid-template-columns:96px 1fr;gap:6px 10px;margin:0}#ProcessDrawer dl.kv dt{color:var(--fgColor-muted)}#ProcessDrawer dl.kv dd{min-width:0;margin:0;word-break:break-all}#ProcessDrawer .raw{font-size:0.75rem}#ProcessDrawer .codebox{display:flex;align-items:flex-start;gap:8px;padding:4px 8px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--bgColor-muted);font-family:var(--fontStack-monospace);font-size:0.75rem;word-break:break-all}#ProcessDrawer .codebox span{flex:1}#ProcessDrawer button.copy{width:20px;height:18px;padding:0;border:0;background:transparent;box-shadow:none;color:var(--fgColor-muted)}#ProcessDrawer button.copy:hover:not(:disabled),#ProcessDrawer button.copy:active:not(:disabled){background:transparent;color:var(--fgColor-accent)}#ProcessDrawer .d-foot{display:flex;flex-wrap:wrap;align-items:center;gap:6px;padding:16px;border-top:1px solid var(--borderColor-default)}#ProcessDrawer .d-foot .pbtn{width:28px;height:28px}
		#ProcessPanel .notice{margin-top:12px;padding:16px;border-radius:var(--borderRadius-medium);font-size:0.875rem}#ProcessPanel .notice.warn{border:1px solid var(--borderColor-attention-muted);background:var(--bgColor-attention-muted);color:var(--fgColor-default)}#ProcessPanel .notice.err{border:1px solid var(--borderColor-danger-muted);background:var(--bgColor-danger-muted);color:var(--fgColor-default)}#ProcessPanel .notice ul{margin:4px 0 0;padding-left:18px}#ProcessPanel.offline .needs-conn{pointer-events:none;opacity:.4}#ProcessPanel.offline #ProcessTables{opacity:.7}
		#ProcessDialog{width:520px;max-width:calc(100vw - 32px)}#ProcessDialog.wide{width:680px}#ProcessDialog.xl{width:1100px;height:86vh}#ProcessDialog[open].xl{display:flex;flex-direction:column}#ProcessDialog.xl .scrollwrap{display:flex;flex-direction:column;flex:1;min-height:0}#ProcessDialog.xl .scroll{flex:1;min-height:0}#ProcessDialog>.toolright{justify-content:flex-end;margin-top:16px}#ProcessDialog .dbody{margin:16px 0}#ProcessDialog .resulthead+.filter{margin-top:12px}#ProcessDialog .impact{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:12px}#ProcessDialog .impact .box{flex:1;min-width:110px;padding:8px 12px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--bgColor-muted)}#ProcessDialog .impact .box b{display:block;font-size:1.25rem;font-weight:600}#ProcessDialog .impact .box.block b{color:var(--fgColor-attention)}#ProcessDialog .impact .box span{color:var(--fgColor-muted);font-size:0.75rem}#ProcessDialog .flow{display:flex;flex-wrap:wrap;align-items:center;gap:6px;margin-bottom:12px;font-size:0.75rem}#ProcessDialog .flow .st{padding:3px 8px;border-radius:var(--borderRadius-medium);background:var(--bgColor-neutral-muted);font-weight:600}#ProcessDialog .flow .ar{color:var(--fgColor-muted)}
		#ProcessDialog .tlist{max-height:230px;overflow:auto;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium)}#ProcessDialog .tlist .tg{position:sticky;top:0;padding:6px 12px;border-bottom:1px solid var(--borderColor-default);background:var(--bgColor-muted);font-size:0.75rem;font-weight:600}#ProcessDialog .tlist .ti{display:flex;flex-wrap:wrap;gap:4px;padding:8px 12px;border-bottom:1px solid var(--borderColor-default)}#ProcessDialog .tlist .ti span{padding:1px 7px;border-radius:var(--borderRadius-medium);background:var(--bgColor-neutral-muted);font-size:0.75rem}#ProcessRenamePreview{margin-top:6px;font-size:0.75rem}#ProcessDialog #ProcessRenamePreview .ti{display:block}#ProcessDialog .typecheck{margin-top:14px}#ProcessDialog .typecheck label{display:block;margin-bottom:5px;font-size:0.875rem}#ProcessDialog .typecheck input{width:100%}#ProcessDialog .typecheck code{padding:.2em .4em;border-radius:var(--borderRadius-medium);background:var(--bgColor-neutral-muted);font-family:var(--fontStack-monospace);font-weight:600}
		#ProcessDialog .setting{grid-template-columns:max-content minmax(0,1fr);align-items:start}#ProcessDialog .setting>label{line-height:28px}#ProcessDialog .field{display:flex;flex-direction:column;gap:4px;min-width:0}#ProcessDialog .field>input,#ProcessDialog .field>select,#ProcessDialog .field .combo input{width:100%;min-width:0}#ProcessDialog .field .help,#ProcessDialog .opt{color:var(--fgColor-muted);font-size:0.75rem;font-weight:400}#ProcessDialog .field .err{display:none;color:var(--fgColor-danger);font-size:0.75rem}#ProcessDialog .field.invalid .err{display:block}#ProcessDialog .field.invalid input{border-color:var(--borderColor-danger-emphasis)}#ProcessDialog input[readonly]{background:var(--bgColor-muted);color:var(--fgColor-muted)}#ProcessDialog .setting>.form-sec,#ProcessDialog .setting>.notice,#ProcessDialog .setting>details.adv,#ProcessDialog .setting>.preview{grid-column:1/-1}#ProcessDialog .form-sec{margin-top:4px;padding-top:6px;border-top:1px solid var(--borderColor-muted);color:var(--fgColor-muted);font-size:0.75rem;font-weight:600}#ProcessDialog .setting>.form-sec:first-child,#ProcessDialog .setting>.notice+.form-sec{border-top:0}#ProcessDialog .setting>.notice{margin-top:0}#ProcessDialog input[type="checkbox"],#ProcessDialog input[type="radio"]{flex:none;width:auto;height:auto;margin:0;padding:0;box-shadow:none;cursor:pointer;vertical-align:middle}
		#ProcessDialog .combo{position:relative}#ProcessDialog .combo input{padding-right:32px}#ProcessDialog button.cb-btn{position:absolute;top:1px;right:1px;bottom:1px;width:28px;height:auto;padding:0;border:0;border-radius:0 calc(var(--borderRadius-medium) - 1px) calc(var(--borderRadius-medium) - 1px) 0;background:none;box-shadow:none;color:var(--fgColor-muted)}#ProcessDialog button.cb-btn:hover:not(:disabled),#ProcessDialog button.cb-btn:active:not(:disabled){background:var(--control-transparent-bgColor-hover)}#ProcessDialog .cb-list{position:absolute;left:0;z-index:61;min-width:100%;max-height:240px;overflow:auto;padding:8px;border-radius:var(--borderRadius-large);background:var(--overlay-bgColor);box-shadow:var(--shadow-floating-small)}#ProcessDialog .cb-list div{display:flex;justify-content:space-between;gap:16px;padding:6px 8px;border-radius:var(--borderRadius-medium);font-family:var(--fontStack-monospace);font-size:0.75rem;cursor:pointer}#ProcessDialog .cb-list div.on{background:var(--control-transparent-bgColor-hover)}#ProcessDialog .cb-list div.cur{color:var(--fgColor-accent);font-weight:600}#ProcessDialog .cb-list small{color:var(--fgColor-muted);font-family:var(--fontStack-sansSerif);font-weight:400}#ProcessDialog .cb-list .cb-empty{margin:0;padding:6px 8px;color:var(--fgColor-muted);font-size:0.75rem}
		#ProcessDialog .seg{display:inline-flex;align-self:flex-start;overflow:hidden;border:1px solid var(--controlTrack-borderColor-rest);border-radius:var(--borderRadius-medium);background:var(--controlTrack-bgColor-rest)}#ProcessDialog .seg label{padding:4px 8px;border-right:1px solid var(--controlTrack-borderColor-rest);font-size:0.75rem;font-weight:400;cursor:pointer}#ProcessDialog .seg label:last-child{border-right:0}#ProcessDialog .seg input{display:none}#ProcessDialog .seg label:has(input:checked){border-radius:var(--borderRadius-medium);background:var(--controlKnob-bgColor-rest);box-shadow:inset 0 0 0 1px var(--controlKnob-borderColor-rest);font-weight:600}#ProcessDialog .switch{display:inline-flex;align-items:center;gap:8px;min-height:28px;font-size:0.75rem;cursor:pointer}#ProcessDialog .preview{padding:8px 12px;border:1px dashed var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--bgColor-muted);font-family:var(--fontStack-monospace);font-size:0.75rem;word-break:break-all}#ProcessDialog .preview div+div{margin-top:4px}#ProcessDialog .preview b{margin-right:6px;color:var(--fgColor-muted);font-family:var(--fontStack-sansSerif);font-weight:600}#ProcessDialog details.adv{padding:8px 12px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium)}#ProcessDialog details.adv summary{color:var(--fgColor-muted);font-size:0.75rem;font-weight:600;cursor:pointer}#ProcessDialog details.adv .muted{margin-top:8px;font-size:0.75rem}#ProcessDialog details.adv .switch{margin:4px 0}
		#ProcessLogKey,#ProcessErrKey{flex:1;min-width:135px}#ProcessLogCount,#ProcessErrCount{margin-right:auto;font-size:0.75rem}#ProcessDialog table.log tbody tr{cursor:default}#ProcessDialog table.log th.c-time{width:170px}#ProcessDialog table.log th.c-level{width:70px}#ProcessDialog table.log th.c-type{width:84px}#ProcessDialog table.log td.t{color:var(--fgColor-muted);font-family:var(--fontStack-monospace);white-space:nowrap}#ProcessDialog table.log td.msg{word-break:break-all}#ProcessDialog .ltype{display:inline-flex;align-items:center;height:20px;padding:0 7px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-full);font-size:0.75rem;font-weight:500;line-height:1;white-space:nowrap}#ProcessDialog .ltype.action{border-color:var(--borderColor-accent-emphasis);color:var(--fgColor-accent)}#ProcessDialog .ltype.edit{border-color:var(--borderColor-attention-emphasis);color:var(--fgColor-attention)}#ProcessDialog .ltype.login{color:var(--fgColor-muted)}#ProcessDialog .ltype.system{border-color:var(--borderColor-done-emphasis);color:var(--fgColor-done)}#ProcessDialog .ltype.error{border-color:var(--borderColor-danger-emphasis);color:var(--fgColor-danger)}
		#ProcessErrBody{padding:8px}#ProcessDialog .eg{margin-bottom:8px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--bgColor-default)}#ProcessDialog .eg:last-child{margin-bottom:0}#ProcessDialog .eg-head{display:flex;align-items:center;gap:10px;padding:8px 12px;cursor:pointer}#ProcessDialog .eg-head:hover{background:var(--bgColor-muted)}#ProcessDialog .eg-head button{height:20px;padding:0 8px}#ProcessDialog .eg-head .lv{display:inline-flex;align-items:center;height:20px;padding:0 7px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-full);font-size:0.75rem;font-weight:500;line-height:1}#ProcessDialog .lv.ERROR{border-color:var(--borderColor-danger-emphasis);color:var(--fgColor-danger)}#ProcessDialog .lv.FATAL{border-color:transparent;background:var(--bgColor-danger-emphasis);color:var(--fgColor-onEmphasis)}#ProcessDialog .lv.WARN{border-color:var(--borderColor-attention-emphasis);color:var(--fgColor-attention)}#ProcessDialog .eg-title{flex:1;min-width:0}#ProcessDialog .eg-title b{display:block}#ProcessDialog .eg-title .mono{color:var(--fgColor-muted);font-size:0.75rem;word-break:break-all}#ProcessDialog .eg-meta{color:var(--fgColor-muted);font-size:0.75rem;text-align:right;white-space:nowrap}#ProcessDialog .eg-count{color:var(--fgColor-default);font-size:0.875rem;font-weight:600}#ProcessDialog .eg-raw{display:none;max-height:220px;overflow:auto;padding:8px 12px;border-top:1px solid var(--borderColor-default);border-radius:0 0 var(--borderRadius-medium) var(--borderRadius-medium);background:var(--bgColor-muted);font-family:var(--fontStack-monospace);font-size:0.75rem;white-space:pre-wrap;word-break:break-all}#ProcessDialog .eg.open .eg-raw{display:block}
		#ProcessToasts{position:fixed;inset:auto;top:60px;right:20px;z-index:70;display:flex;flex-direction:column;gap:8px;width:auto;height:auto;margin:0;padding:0;overflow:visible;border:0;background:transparent;color:var(--fgColor-default)}#ProcessToasts .toast{min-width:260px;max-width:400px;padding:12px 16px;border-left:4px solid var(--borderColor-accent-emphasis);border-radius:var(--borderRadius-medium);background:var(--overlay-bgColor);box-shadow:var(--shadow-floating-small);font-size:0.875rem;white-space:pre-line;animation:ProcessToastIn .2s ease-out}#ProcessToasts .toast.ok{border-left-color:var(--borderColor-success-emphasis)}#ProcessToasts .toast.fail{border-left-color:var(--borderColor-danger-emphasis)}#ProcessToasts .toast.warn{border-left-color:var(--borderColor-severe-emphasis)}@keyframes ProcessToastIn{from{opacity:0;transform:translateY(-6px)}}
		</style>
		</head>
		<body data-mode="__MODE__" data-ui="__UI__">
		<h1 style="color:__ZONE_COLOR__"><button type="button" id="TabRefresh" class="iconbtn" title="새로 고침(지금 탭 리셋, F5)" aria-label="새로 고침"><svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M1.705 8.005a.75.75 0 0 1 .834.656 5.5 5.5 0 0 0 9.592 2.97l-1.204-1.204a.25.25 0 0 1 .177-.427h3.646a.25.25 0 0 1 .25.25v3.646a.25.25 0 0 1-.427.177l-1.38-1.38A7.002 7.002 0 0 1 1.05 8.84a.75.75 0 0 1 .656-.834ZM8 2.5a5.487 5.487 0 0 0-4.131 1.869l1.204 1.204A.25.25 0 0 1 4.896 6H1.25A.25.25 0 0 1 1 5.75V2.104a.25.25 0 0 1 .427-.177l1.38 1.38A7.002 7.002 0 0 1 14.95 7.16a.75.75 0 0 1-1.49.178A5.5 5.5 0 0 0 8 2.5Z"/></svg></button><span id="TitleText">__TITLE__</span><span id="OffText" hidden></span><span id="TabBar"><button type="button" id="QueryButton" class="cur">쿼리</button><button type="button" id="LogButton">서치</button><button type="button" id="FileButton">파일</button><button type="button" id="DumpButton">덤프</button><button type="button" id="ManagerButton" data-zone="__MANAGER_ZONE__" data-zonecolor="__MANAGER_ZONE_COLOR__">운영</button><button type="button" id="ProcessButton">반영</button><button type="button" id="AccountButton" title="계정 (목록·로그아웃)">계정</button><button type="button" id="HelpButton" title="단축키">힌트</button></span></h1>
		<section class="panel" id="FilterPanel">
		<div class="filter">
		<button type="button" id="ResetButton" title="이전 실행(최대 10회)" aria-label="이전 실행" disabled><svg class="octicon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M2.75 1h10.5a.75.75 0 0 1 0 1.5h-.75v1.25a4.75 4.75 0 0 1-1.9 3.8l-.333.25a.25.25 0 0 0 0 .4l.333.25a4.75 4.75 0 0 1 1.9 3.8v1.25h.75a.75.75 0 0 1 0 1.5H2.75a.75.75 0 0 1 0-1.5h.75v-1.25a4.75 4.75 0 0 1 1.9-3.8l.333-.25a.25.25 0 0 0 0-.4L5.4 7.55a4.75 4.75 0 0 1-1.9-3.8V2.5h-.75a.75.75 0 0 1 0-1.5ZM11 2.5H5v1.25c0 1.023.482 1.986 1.3 2.6l.333.25c.934.7.934 2.1 0 2.8l-.333.25a3.251 3.251 0 0 0-1.3 2.6v1.25h6v-1.25a3.251 3.251 0 0 0-1.3-2.6l-.333-.25a1.748 1.748 0 0 1 0-2.8l.333-.25a3.251 3.251 0 0 0 1.3-2.6Z"/></svg></button>
		<span class="pickwrap" title="Machine"><span id="MachinePick"><button type="button" id="MachineBox" title="대상 머신(전체, 체크)" aria-haspopup="true" aria-expanded="false">전체</button><div id="MachineMenu" hidden><label class="all"><input type="checkbox" id="MachineAll" checked><span>전체</span></label><div id="MachineList"></div></div></span></span>
		<label title="Duration(최근 기간)"><select id="QueryRange"><option value="" hidden>기타</option><option value="10">10분</option><option value="30">30분</option><option value="60" selected>60분</option><option value="180">3시간</option><option value="360">6시간</option><option value="540">9시간</option><option value="1440">1일</option><option value="4320">3일</option><option value="10080">7일</option><option value="21600">15일</option><option value="43200">30일</option><option value="86400">60일</option></select></label>
		<label title="Start(날짜, 시, 분, 초, 포함, 쿼리: E 기준 최대 3년)"><input type="date" id="StartDate" value="__YESTERDAY__"><select id="StartHour" class="hour"></select><select id="StartMinute" class="hour"></select><select id="StartSecond" class="hour"></select><span class="rangeop">이상</span></label>
		<label title="End(날짜, 시, 분, 초, 미포함)"><input type="date" id="EndDate" value="__TODAY__"><select id="EndHour" class="hour"></select><select id="EndMinute" class="hour"></select><select id="EndSecond" class="hour"></select><span class="rangeop">미만</span></label>
		<span class="toolright" id="FilterTool"><button type="button" id="TimerButton" title="타이머(입력 엔터 시작, 클릭 중지)">10</button><button type="button" id="SearchSavedButton" class="searchonly" title="저장 서치(추가, 수정, 실행)">저장</button><button type="button" id="QueryAlarmButton" class="queryonly" title="알람(추가, 수정, 켜기)">알람</button><button type="button" id="QueryHistoryButton" class="queryonly" title="알람 기록(발송, 실패, 최신 1,000건)">기록</button><button type="button" id="QueryRuleButton" class="queryonly" title="필드 정의(추가, 수정, 순서, 설정 aField)">필드</button><button type="button" id="QuerySavedButton" class="queryonly" title="저장 쿼리(추가, 수정, 실행)">저장</button><button type="button" id="SettingButton" title="설정">설정</button></span>
		<div class="searchbox" id="SearchBox">
		<input type="text" id="SearchKey" class="searchonly" title="검색어(AND, OR, NOT, 괄호)" autocomplete="off" spellcheck="false" autocapitalize="off">
		<input type="text" id="QueryKey" class="queryonly" title="쿼리(단어, 필드:값, AND, OR, NOT, 괄호)" autocomplete="off" spellcheck="false" autocapitalize="off">
		<button type="button" id="SearchButton" class="primary searchonly" title="서치 실행(Enter)">실행</button>
		<button type="button" id="QueryRunButton" class="primary queryonly" title="쿼리 실행(Enter)">실행</button>
		<button type="button" id="QueryStopButton" class="danger queryonly" title="쿼리 중단" disabled>중단</button>
		<button type="button" id="StopButton" class="danger searchonly" title="검색 중단" disabled>중단</button>
		</div>
		</div>
		</section>
		<div class="splitter" id="FilterSplitter" title="위아래 크기 조절"></div>
		<section class="panel" id="ResultPanel">
		<div class="resulthead">
		<div class="title" id="ResultTitle"></div>
		<div class="title" id="QueryTitle"></div>
		<div class="toolright">
		<div class="btngroup">
		<button type="button" id="QueryPrev" title="뒤로(이전 단계)" aria-label="뒤로" disabled><svg class="octicon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M7.78 12.53a.75.75 0 0 1-1.06 0L2.47 8.28a.75.75 0 0 1 0-1.06l4.25-4.25a.751.751 0 0 1 1.042.018.751.751 0 0 1 .018 1.042L4.81 7h7.44a.75.75 0 0 1 0 1.5H4.81l2.97 2.97a.75.75 0 0 1 0 1.06Z"/></svg></button>
		<button type="button" id="QueryDepth" tabindex="-1" title="단계 입력" aria-label="단계">0</button>
		<button type="button" id="QueryNext" title="앞으로(다음 단계)" aria-label="앞으로" disabled><svg class="octicon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M8.22 2.97a.75.75 0 0 1 1.06 0l4.25 4.25a.75.75 0 0 1 0 1.06l-4.25 4.25a.751.751 0 0 1-1.042-.018.751.751 0 0 1-.018-1.042l2.97-2.97H3.75a.75.75 0 0 1 0-1.5h7.44L8.22 4.03a.75.75 0 0 1 0-1.06Z"/></svg></button>
		</div>
		</div>
		</div>
		<div id="QueryChart" hidden><div class="chartlens" id="QueryLens">
		<button type="button" id="LensUp" title="큰 단위" aria-label="렌즈 위" disabled><svg class="octicon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path transform="rotate(90 8 8)" d="M7.78 12.53a.75.75 0 0 1-1.06 0L2.47 8.28a.75.75 0 0 1 0-1.06l4.25-4.25a.751.751 0 0 1 1.042.018.751.751 0 0 1 .018 1.042L4.81 7h7.44a.75.75 0 0 1 0 1.5H4.81l2.97 2.97a.75.75 0 0 1 0 1.06Z"/></svg></button>
		<span id="LensStep" title="일봉">0</span>
		<button type="button" id="LensDown" title="작은 단위" aria-label="렌즈 아래" disabled><svg class="octicon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path transform="rotate(-90 8 8)" d="M7.78 12.53a.75.75 0 0 1-1.06 0L2.47 8.28a.75.75 0 0 1 0-1.06l4.25-4.25a.751.751 0 0 1 1.042.018.751.751 0 0 1 .018 1.042L4.81 7h7.44a.75.75 0 0 1 0 1.5H4.81l2.97 2.97a.75.75 0 0 1 0 1.06Z"/></svg></button>
		</div></div>
		<div class="progress" id="QueryProgress" hidden></div>
		<div class="scrollwrap">
		<div class="scroll" id="ResultScroll">
		<table id="ResultTable">
		<colgroup><col><col><col><col><col><col></colgroup>
		<thead><tr><th>No.</th><th>Machine</th><th>Folder</th><th>File</th><th>Line</th><th>Log</th></tr></thead>
		<tbody id="ResultBody"></tbody>
		</table>
		</div>
		<div class="progress" id="SearchProgress" hidden></div>
		</div>
		<div class="pager" id="ResultPager" hidden></div>
		</section>
		<div class="splitter" id="QuerySplitter" title="위아래 크기 조절" hidden></div>
		<section class="panel" id="QueryPanel" hidden>
		<div class="resulthead">
		<div class="title" id="QueryLogTitle"></div>
		<div class="toolright">
		</div>
		</div>
		<div class="querysplit" id="QuerySplit">
		<aside id="QueryField">
		<div class="fieldhead"><span>필드</span></div>
		<ul id="QueryFieldList"></ul>
		</aside>
		<div class="vsplitter" id="QueryFieldSplitter" title="좌우 폭 조절"></div>
		<div class="scroll" id="QueryScroll">
		<table id="QueryTable">
		<colgroup><col><col><col><col><col><col></colgroup>
		<thead><tr><th>No.</th><th>Machine</th><th>Folder</th><th>File</th><th title="파일 줄 번호">Line</th><th title="단어 조건(AND, OR, NOT)">Log</th></tr></thead>
		<tbody id="QueryBody"></tbody>
		</table>
		</div>
		</div>
		<div class="pager" id="QueryPager" hidden></div>
		</section>
		<section class="panel" id="QueryFieldPanel" hidden>
		<div class="resulthead">
		<div class="title" id="QueryFieldTitle"></div>
		</div>
		<div class="scroll" id="QueryFieldScroll">
		<table id="QueryFieldTable">
		<colgroup><col><col><col></colgroup>
		<thead><tr title="누를 때마다 내림차순(▼) → 오름차순(▲) → 원래 순서 (보이는 페이지만)"><th>No.<span class="sortmark"></span></th><th id="QueryFieldColumn">Value<span class="sortmark"></span></th><th>Count<span class="sortmark"></span></th></tr></thead>
		<tbody id="QueryFieldBody"></tbody>
		</table>
		</div>
		<div class="pager" id="QueryFieldPager" hidden></div>
		</section>
		<div class="splitter" id="Splitter" title="위아래 크기 조절"></div>
		<section class="panel" id="ViewPanel">
		<div class="filter">
		<span class="title" id="ViewTitle"></span>
		<div class="toolright">
		<div class="btngroup">
		<button type="button" id="ViewPrev" title="포지션 위로(줄 단위, 위에 쌓임)" aria-label="포지션 위로" disabled>«</button>
		<button type="button" id="ViewShift" tabindex="-1" aria-label="포지션">0</button>
		<button type="button" id="ViewNext" title="포지션 아래로(줄 단위, 아래에 쌓임)" aria-label="포지션 아래로" disabled>»</button>
		</div>
		</div>
		</div>
		<div class="scroll" id="ViewScroll"><pre id="ViewBody"></pre></div>
		<div class="hscroll" id="ViewHScroll" hidden><div id="ViewHScrollSize"></div></div>
		</section>
		<section class="panel" id="DumpPanel" hidden>
		<div class="resulthead">
		<div class="title" id="DumpTitle"></div>
		<div class="toolright">
		<button type="button" id="DumpDeleteAll" class="danger" title="전체 삭제(dmp, zip)" disabled>삭제</button>
		</div>
		</div>
		<div class="scroll" id="DumpScroll">
		<table id="DumpTable">
		<colgroup><col><col><col><col><col><col><col></colgroup>
		<thead><tr><th>No.</th><th>Machine</th><th>Folder</th><th>File</th><th>Date</th><th>Size</th><th title="다운, 압축, 삭제(dmp, zip)">Ac.</th></tr></thead>
		<tbody id="DumpBody"></tbody>
		</table>
		</div>
		<div class="progress" id="DumpProgress" hidden></div>
		</section>
		<section class="panel" id="FilePanel" hidden>
		<div class="resulthead">
		<div class="title" id="FileTitle"></div>
		<div class="title" id="FileViewTitle">*.log·*.txt·*.json·*.ini·*.bat·*.xml·*.config·*.md·*.lua</div>
		<div class="toolright">
		<div class="btngroup">
		<button type="button" id="FilePrev" title="포지션 위로(줄 단위, 위에 쌓임)" aria-label="포지션 위로" disabled>«</button>
		<button type="button" id="FileShift" tabindex="-1" aria-label="포지션">0</button>
		<button type="button" id="FileNext" title="포지션 아래로(줄 단위, 아래에 쌓임)" aria-label="포지션 아래로" disabled>»</button>
		</div>
		</div>
		</div>
		<div class="filesplit">
		<div class="scroll" id="FileScroll"><ul class="tree" id="FileTree"></ul></div>
		<div class="vsplitter" id="FileSplitter" title="좌우 폭 조절"></div>
		<div class="fileview">
		<div class="scroll" id="FileViewScroll"><pre id="FileViewBody"></pre></div>
		</div>
		</div>
		</section>
		<section id="ManagerPanel" hidden>
		<section class="panel" id="ManagerServerPanel">
		<div class="resulthead">
		<div class="title" id="ManagerServerTitle">서버</div>
		<div class="toolright">
		<button type="button" id="ManagerPatchButton" title="패치 시간(unix)">패치</button>
		<span class="chip before" title="패치 시간 이전 기동">전</span>
		<span class="chip after" title="패치 시간 이후 기동">후</span>
		<b class="mono" id="ManagerPatchTime" title="패치 시간">-</b>
		<button type="button" id="ManagerRefresh" class="iconbtn" title="새로 고침" aria-label="새로 고침"><svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M1.705 8.005a.75.75 0 0 1 .834.656 5.5 5.5 0 0 0 9.592 2.97l-1.204-1.204a.25.25 0 0 1 .177-.427h3.646a.25.25 0 0 1 .25.25v3.646a.25.25 0 0 1-.427.177l-1.38-1.38A7.002 7.002 0 0 1 1.05 8.84a.75.75 0 0 1 .656-.834ZM8 2.5a5.487 5.487 0 0 0-4.131 1.869l1.204 1.204A.25.25 0 0 1 4.896 6H1.25A.25.25 0 0 1 1 5.75V2.104a.25.25 0 0 1 .427-.177l1.38 1.38A7.002 7.002 0 0 1 14.95 7.16a.75.75 0 0 1-1.49.178A5.5 5.5 0 0 0 8 2.5Z"/></svg></button>
		</div>
		</div>
		<div class="error" id="ManagerError" hidden></div>
		<div class="scroll">
		<table id="ManagerServerTable">
		<thead><tr><th>Zone</th><th>Server</th><th>URL</th><th>StartTime</th><th>AuthKey</th><th>CDS</th></tr></thead>
		<tbody id="ManagerServerBody"></tbody>
		</table>
		</div>
		<details>
		<summary>미운영 <span id="ManagerDownCount">-</span></summary>
		<div class="scroll">
		<table id="ManagerDownTable">
		<thead><tr><th>Zone</th><th>Server</th><th>URL</th></tr></thead>
		<tbody id="ManagerDownBody"></tbody>
		</table>
		</div>
		</details>
		<div class="progress" id="ManagerProgress" hidden></div>
		</section>
		<div class="vsplitter" id="ManagerSplitter" title="좌우 폭 조절"></div>
		<section class="panel" id="ManagerSidePanel">
		<nav id="ManagerSideTab"><button type="button" id="ManagerCcuButton" class="cur">동접</button><button type="button" id="ManagerChartButton">차트</button><button type="button" id="ManagerCcuRefresh" class="iconbtn" title="새로 고침(동접 조회)" aria-label="새로 고침"><svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M1.705 8.005a.75.75 0 0 1 .834.656 5.5 5.5 0 0 0 9.592 2.97l-1.204-1.204a.25.25 0 0 1 .177-.427h3.646a.25.25 0 0 1 .25.25v3.646a.25.25 0 0 1-.427.177l-1.38-1.38A7.002 7.002 0 0 1 1.05 8.84a.75.75 0 0 1 .656-.834ZM8 2.5a5.487 5.487 0 0 0-4.131 1.869l1.204 1.204A.25.25 0 0 1 4.896 6H1.25A.25.25 0 0 1 1 5.75V2.104a.25.25 0 0 1 .427-.177l1.38 1.38A7.002 7.002 0 0 1 14.95 7.16a.75.75 0 0 1-1.49.178A5.5 5.5 0 0 0 8 2.5Z"/></svg></button><button type="button" id="ManagerMonitorButton">모니터</button></nav>
		<div id="ManagerCcuPanel">
		<div class="resulthead">
		<div class="title">Peak Concurrent Users</div>
		</div>
		<div class="error" id="ManagerCcuError" hidden></div>
		<div class="scroll">
		<table id="ManagerMaxTable">
		<thead><tr><th>Game</th><th>Time</th><th class="num">Count</th></tr></thead>
		<tbody id="ManagerMaxBody"></tbody>
		</table>
		</div>
		<div class="title subtitle">User Connection History</div>
		<div class="scroll tall">
		<table id="ManagerCcuTable">
		<thead><tr><th>LogTime</th><th class="num">Baduki</th><th class="num">7Poker</th><th class="num">Highlow</th><th class="num">Holdem</th><th class="num">Blackjack</th><th class="num">Total</th></tr></thead>
		<tbody id="ManagerCcuBody"></tbody>
		</table>
		</div>
		<div class="progress" id="ManagerCcuProgress" hidden></div>
		</div>
		<div id="ManagerChartPanel" hidden>
		<div class="resulthead">
		<div class="title">동접 차트</div>
		<div class="toolright">
		<button type="button" id="ManagerChartStack" title="누적·개별">toggle</button>
		<button type="button" id="ManagerChartLine" title="막대·선">line</button>
		<button type="button" id="ManagerChartTotal" title="total 만">onlyTotal</button>
		<button type="button" id="ManagerChartAll" title="전체">showAll</button>
		</div>
		</div>
		<div class="error" id="ManagerChartError" hidden></div>
		<div id="ManagerChartBox"><canvas id="ManagerChartCanvas"></canvas></div>
		</div>
		<div id="ManagerMonitorPanel" hidden>
		<div class="resulthead">
		<div class="title" id="ManagerMonitorTitle">XLO 모니터 (서버 목록 URL)</div>
		</div>
		<iframe name="ManagerMonitor" id="ManagerMonitorFrame" title="XLO 모니터"></iframe>
		</div>
		</section>
		</section>
		<section id="ProcessPanel" hidden>
		<div id="ProcessStale" hidden><b>다른 곳에서 변경되었습니다</b><span id="ProcessStaleText"></span><button type="button" id="ProcessStaleReload">새로고침</button></div>
		<div id="ProcessSummary" hidden>
		<div id="ProcessCards"></div>
		<section class="panel" id="ProcessFilter">
		<div class="filter">
		<input type="text" id="ProcessSearch" title="검색(이름, 머신, 커밋, 명령)" autocomplete="off" spellcheck="false" autocapitalize="off">
		<div class="chips" id="ProcessChips"></div>
		<select id="ProcessMachine" title="머신"><option value="">모든 머신</option></select>
		</div>
		</section>
		</div>
		<section class="panel" id="ProcessListPanel">
		<div class="resulthead">
		<div class="title">서버 목록</div>
		<span id="ProcessVersion" title="설정 버전(Data\Server.txt)"></span>
		<div class="toolright">
		<span id="ProcessTest" hidden><button type="button" id="ProcessTestOn" class="needs-conn" title="모든 서버 바로 On,True(테스트)">TestOn</button><button type="button" id="ProcessTestOff" class="needs-conn" title="모든 서버 바로 Off,True,0(테스트)">TestOff</button></span>
		<button type="button" id="ProcessSummaryButton" title="현황·필터">현황</button>
		<button type="button" id="ProcessLogButton" title="작업 로그">로그</button>
		<button type="button" id="ProcessErrButton" title="에러 로그(Redis)">에러</button>
		<button type="button" id="ProcessAddButton" class="needs-conn" title="서버 등록">등록</button>
		<button type="button" id="ProcessExpandButton" title="모두 펴기">펴기</button>
		<button type="button" id="ProcessCollapseButton" title="모두 접기">접기</button>
		</div>
		</div>
		<div class="error" id="ProcessError" hidden></div>
		<div id="ProcessTables"></div>
		</section>
		<aside id="ProcessDrawer"></aside>
		<div id="ProcessMenu" hidden></div>
		<dialog id="ProcessDialog"></dialog>
		</section>
		<div id="ProcessToasts" popover="manual"></div>
		<div class="colguide" id="ColGuide" hidden></div>
		<dialog id="SettingDialog">
		<div class="resulthead">
		<div class="title">설정</div>
		<div class="toolright">
		<button type="button" id="SettingDefault" title="기본값">기본</button>
		</div>
		</div>
		<div class="setting">
		<label for="MachineMaxInput" class="srow">한 머신당 최대</label><input type="number" id="MachineMaxInput" class="srow" min="1" step="1" autofocus><span class="hint srow" id="MachineMaxHint"></span>
		<label for="ResultMaxInput" class="srow">모든 머신 최대</label><input type="number" id="ResultMaxInput" class="srow" min="1" step="1"><span class="hint srow" id="ResultMaxHint"></span>
		<label for="SearchLimitInput" class="srow">서치 제한 시간</label><input type="number" id="SearchLimitInput" class="srow" min="1" step="1"><span class="hint srow" id="SearchLimitHint"></span>
		<label for="SortSelect" class="srow">결과 정렬</label><select id="SortSelect" class="srow"><option value="time">시간 정렬</option><option value="base">머신 정렬</option></select><span class="srow"></span>
		<label for="QueryLimitInput" class="qrow">쿼리 제한 시간</label><input type="number" id="QueryLimitInput" class="qrow" min="1" step="1" autofocus><span class="hint qrow" id="QueryLimitHint"></span>
		</div>
		<div class="toolright">
		<button type="button" id="SettingSave" class="primary">저장</button>
		<button type="button" id="SettingCancel">취소</button>
		</div>
		</dialog>
		<dialog id="QuerySettingDialog">
		<div class="title" id="QuerySettingTitle">저장</div>
		<div class="savedlist" id="QueryEditScroll"><table id="QueryEditTable"><tbody id="QueryEditList"></tbody></table></div>
		<div class="toolright">
		<button type="button" id="QueryEditAdd" title="쿼리 추가">추가</button>
		<button type="button" id="QuerySettingCancel" autofocus>닫기</button>
		</div>
		</dialog>
		<dialog id="AlarmHistoryDialog">
		<div class="title">기록</div>
		<div class="savedlist" id="AlarmHistoryScroll"><table id="AlarmHistoryTable"><tbody id="AlarmHistoryList"></tbody></table></div>
		<div class="toolright">
		<button type="button" id="AlarmHistoryClose" autofocus>닫기</button>
		</div>
		</dialog>
		<dialog id="AlarmHistoryViewDialog">
		<div class="title" id="AlarmHistoryViewTitle"></div>
		<p class="hint" id="AlarmHistoryViewInfo"></p>
		<pre id="AlarmHistoryViewBody"></pre>
		<div class="toolright">
		<button type="button" id="AlarmHistoryViewClose" autofocus>닫기</button>
		</div>
		</dialog>
		<dialog id="QuerySaveDialog">
		<div class="title" id="QuerySaveTitle">쿼리 추가</div>
		<div class="setting">
		<label for="QuerySaveKey">제목</label><input type="text" id="QuerySaveKey" maxlength="100" autocomplete="off" autofocus>
		<label for="QuerySaveQuery" id="QuerySaveQueryLabel">쿼리</label><textarea id="QuerySaveQuery" rows="10" maxlength="1000" spellcheck="false"></textarea>
		<label for="QuerySaveMinute" class="queryrow">간격</label><span class="queryrow"><input type="number" id="QuerySaveMinute" min="1" max="129600" step="1"> 분</span>
		<label for="AlarmMinute" class="alarmrow">간격</label><span class="alarmrow"><input type="number" id="AlarmMinute" min="1" max="1440" step="1"> 분 동안 <input type="number" id="AlarmCount" min="1" step="1"> 건 이상 <label for="AlarmDelay" class="delay">억제</label><input type="number" id="AlarmDelay" min="0" max="1440" step="1"> 분</span>
		<label for="AlarmMail" class="alarmrow">메일</label><span class="alarmrow alarmadd"><span class="alarminput"><select id="AlarmMail"></select><button type="button" id="AlarmMailAdd">추가</button></span><span class="alarmlist" id="AlarmMailList"></span></span>
		<label for="AlarmSMS" class="alarmrow">문자</label><span class="alarmrow alarmadd"><span class="alarminput"><select id="AlarmSMS"></select><button type="button" id="AlarmSMSAdd">추가</button></span><span class="alarmlist" id="AlarmSMSList"></span></span>
		<label for="AlarmHook" class="alarmrow">훅</label><input type="text" id="AlarmHook" class="alarmrow" maxlength="1000" autocomplete="off">
		<label for="AlarmEnable" class="alarmrow">사용</label><span class="alarmrow"><input type="checkbox" id="AlarmEnable"></span>
		</div>
		<div class="toolright">
		<button type="button" id="AlarmTest" class="alarmrow" title="메일·훅 바로 발송">테스트</button>
		<button type="button" id="QuerySaveOk" class="primary">저장</button>
		<button type="button" id="QuerySaveCancel">취소</button>
		</div>
		</dialog>
		<dialog id="FieldRuleDialog">
		<div class="title" id="FieldRuleTitle">필드 추가</div>
		<div class="setting">
		<label for="FieldRuleKey">이름</label><input type="text" id="FieldRuleKey" maxlength="32" autocomplete="off" spellcheck="false" autofocus>
		<label for="FieldRuleRegex">정규식</label><textarea id="FieldRuleRegex" rows="4" maxlength="500" spellcheck="false"></textarea>
		<label for="FieldRuleValue">고정값</label><input type="text" id="FieldRuleValue" maxlength="260" autocomplete="off" spellcheck="false">
		<span></span><span class="rulehint">정규식: 줄에서 첫 () 값 (없음: 맞은 부분), 고정값 있음: 정규식 안 씀·모든 줄 그 값 (예: 존)</span>
		</div>
		<div class="toolright">
		<button type="button" id="FieldRuleOk" class="primary">저장</button>
		<button type="button" id="FieldRuleCancel">취소</button>
		</div>
		</dialog>
		<dialog id="DownDialog">
		<div class="title">파일 다운</div>
		<p id="DownName"></p>
		<p id="DownPath"></p>
		<div class="toolright">
		<button type="button" id="DownYes" class="primary">예</button>
		<button type="button" id="DownNo">아니오</button>
		</div>
		</dialog>
		<dialog id="AccountDialog">
		<div class="title">계정</div>
		<p id="AccountText" hidden></p>
		<div class="savedlist"><table id="AccountTable"><tbody id="AccountList"></tbody></table></div>
		<div class="pager" id="AccountPager"></div>
		<div class="toolright">
		<button type="button" id="AccountAdd" title="계정 추가 (관리 계정, 관리 비밀번호)" hidden>추가</button>
		<button type="button" id="AccountClose" autofocus>닫기</button>
		</div>
		</dialog>
		<dialog id="AccountEditDialog">
		<div class="title" id="AccountEditTitle">계정 추가</div>
		<div class="setting">
		<label for="AccountEditID">아이디</label><input type="text" id="AccountEditID" maxlength="50" autocomplete="off" spellcheck="false">
		<label for="AccountEditPass" class="editrow">비번</label><input type="password" id="AccountEditPass" class="editrow" maxlength="100" autocomplete="new-password">
		<label for="AccountEditMail" class="editrow">메일</label><input type="text" id="AccountEditMail" class="editrow" maxlength="254" autocomplete="off" spellcheck="false">
		<label for="AccountEditPhone" class="editrow">전화</label><input type="text" id="AccountEditPhone" class="editrow" maxlength="20" autocomplete="off" spellcheck="false" inputmode="tel">
		<label class="editrow">메뉴</label><span class="editrow" id="AccountEditMenu"></span>
		<label class="editrow">외부</label><span class="editrow" id="AccountEditApi"></span>
		<label class="keyrow">키</label><span class="keyrow keybox"><span id="AccountEditKey"></span><button type="button" id="AccountEditKeyNew" title="새 키 만들기 (관리 비밀번호 입력 → 적용: 저장, 예전 키·링크: 못 씀)">새 키</button></span>
		<label class="keyrow">토큰</label><span class="keyrow" id="AccountEditToken"></span>
		<label for="AccountEditAdmin">관리 비번</label><span class="adminbox"><input type="password" id="AccountEditAdmin" maxlength="100" autocomplete="new-password"><button type="button" id="AccountEditKeyApply" class="keyrow" title="새 키 적용 (관리 비밀번호 확인 → 저장)" disabled>적용</button></span>
		</div>
		<div class="toolright">
		<button type="button" id="AccountEditOk" class="primary">저장</button>
		<button type="button" id="AccountEditCancel">취소</button>
		</div>
		</dialog>
		<dialog id="DeleteDialog">
		<div class="title" id="DeleteTitle">덤프 전체 삭제</div>
		<p id="DeleteName"></p>
		<p id="DeleteText"></p>
		<div class="toolright">
		<button type="button" id="ProcessDeleteStop" hidden>■ 먼저 정지</button>
		<button type="button" id="DeleteYes" class="danger">삭제</button>
		<button type="button" id="DeleteNo">취소</button>
		</div>
		</dialog>
		<dialog id="ManagerPatchDialog">
		<div class="title">패치 시작</div>
		<div class="managerpatch">
		<label for="ManagerPatchInput">패치 시간</label><input type="text" id="ManagerPatchInput" class="mono" autocomplete="off" spellcheck="false" autocapitalize="off">
		</div>
		<div class="toolright">
		<button type="button" id="ManagerPatchOk" class="primary">확인</button>
		<button type="button" id="ManagerPatchCancel">취소</button>
		</div>
		</dialog>
		<dialog id="HelpDialog">
		<div class="btngroup" id="HelpTabs">
		<button type="button" data-help="common">공통</button>
		<button type="button" data-help="rule">검색</button>
		<button type="button" data-help="query" data-tab="QueryButton">쿼리</button>
		<button type="button" data-help="search" data-tab="LogButton">서치</button>
		<button type="button" data-help="manager" data-tab="ManagerButton">운영</button>
		<button type="button" data-help="process" data-tab="ProcessButton">반영</button>
		</div>
		<div id="HelpScroll">
		<table id="HelpTable">
		<tbody>
		<tr class="group" data-help="common"><th>단축키</th><th>명령</th></tr>
		<tr data-help="common"><td>?</td><td>힌트</td></tr>
		<tr data-help="common" class="sep"><td>F5</td><td>탭 새로 고침 (전체 새로 고침 → Ctrl + F5)</td></tr>
		<tr data-help="common"><td>Esc</td><td>닫기</td></tr>
		<tr data-help="common"><td>Enter</td><td>실행</td></tr>
		<tr data-help="common" class="sep"><td>Tab</td><td>탭 이동</td></tr>
		<tr data-help="common" class="sep"><td>Ctrl + 단어 클릭</td><td>입력 + AND</td></tr>
		<tr data-help="common"><td>Alt + 단어 클릭</td><td>입력 + OR</td></tr>
		<tr data-help="common" class="sep"><td>Ctrl + Alt + 단어 클릭</td><td>입력 + NOT</td></tr>
		<tr data-help="common"><td>Ctrl · Alt + 같은 단어 클릭</td><td>입력 제외</td></tr>
		<tr data-help="common"><td>Ctrl · Alt + 필드:값 클릭</td><td>입력 + 필드:값</td></tr>
		<tr data-help="common"><td>Ctrl · Alt · Ctrl + Alt 놓기</td><td>입력 1초 후 실행</td></tr>
		<tr data-help="common"><td>Ctrl + A</td><td>포커싱 표 전체 선택 (제목 포함)</td></tr>
		<tr data-help="common" class="sep"><td>Ctrl + UI 클릭</td><td>새 창</td></tr>
		<tr data-help="common"><td>Alt + UI 클릭</td><td>새 탭</td></tr>
		<tr data-help="common" class="sep"><td>Step 버튼 클릭</td><td>숫자 입력 + Enter → Step 이동</td></tr>
		<tr data-help="common"><td>Pos 버튼 클릭</td><td>숫자 입력 + Enter → Pos 이동</td></tr>
		<tr data-help="common"><td>Timer 버튼 클릭</td><td>초 입력 + Enter → 초 반복 (클릭 → 중지)</td></tr>
		<tr data-help="common"><td>Sand 버튼 클릭</td><td>이전 실행 (최근 10회)</td></tr>
		<tr data-help="common" class="sep"><td>Ctrl + 파일 클릭</td><td>뷰어</td></tr>
		<tr data-help="common"><td>Shift + 파일 클릭</td><td>다운</td></tr>
		<tr class="group" data-help="rule"><th>검색어</th><th>검색 규칙 (쿼리 · 서치 공통)</th></tr>
		<tr data-help="rule"><td>AND</td><td>동일 라인 검색</td></tr>
		<tr data-help="rule"><td>OR</td><td>다른 라인 검색</td></tr>
		<tr data-help="rule"><td>NOT</td><td>모든 라인 미포함</td></tr>
		<tr data-help="rule"><td>A AND (B NOT C) OR D</td><td>1. A 검색 → B 검색 → C 미포함<br>2. D 검색<br>3. 1, 2번 결과 병합</td></tr>
		<tr data-help="rule"><td>A AND B = a and b</td><td>대소문자 무시</td></tr>
		<tr data-help="rule"><td>ab* · *ab · *ab*</td><td>와일드카드 (쿼리는 선행 조건 필수)</td></tr>
		<tr class="group" data-help="query" data-tab="QueryButton LogButton"><th>단축키</th><th>명령</th></tr>
		<tr data-help="query" data-tab="QueryButton LogButton"><td>Shift + 입력 클릭</td><td>서치로 입력·기간 복사</td></tr>
		<tr data-help="query" data-tab="QueryButton LogButton"><td>Shift + 단어·실행 클릭</td><td>서치로 입력·기간 복사 + 실행</td></tr>
		<tr data-help="query" data-tab="QueryButton LogButton"><td>Shift + Ctrl + 입력·단어·실행 클릭</td><td>새 창에서 위와 같게</td></tr>
		<tr data-help="query" data-tab="QueryButton LogButton"><td>Shift + Alt + 입력·단어·실행 클릭</td><td>새 탭에서 위와 같게</td></tr>
		<tr class="group" data-help="query"><th>검색어</th><th>검색 규칙 (색인 단어)</th></tr>
		<tr data-help="query"><td>abc</td><td>단어 완전 일치</td></tr>
		<tr data-help="query"><td>test AND *ab*</td><td>와일드카드 (선행 조건 필수)</td></tr>
		<tr data-help="query"><td>a/b</td><td>특수 기호 두 단어 (a AND b로 검색)</td></tr>
		<tr data-help="query"><td>a b</td><td>빈칸 두 단어 (a AND b로 검색)</td></tr>
		<tr data-help="query"><td>a_b-c</td><td>바 · 언더바 한 단어 취급 (a AND b로도 검색 가능)</td></tr>
		<tr data-help="query"><td>noti</td><td>앞뒤 바 · 언더바 제외 ([NOTI_] 포함)</td></tr>
		<tr data-help="query"><td>127.0.0.1</td><td>아이피 한 단어 취급</td></tr>
		<tr data-help="query"><td>2000/01/01</td><td>년도 한 단어 취급</td></tr>
		<tr data-help="query"><td>01/01</td><td>날짜 한 단어 취급</td></tr>
		<tr data-help="query"><td>00:00</td><td>시간 한 단어 취급</td></tr>
		<tr data-help="query"><td>Type:ERROR</td><td>필드 값 일치 → 색인 검색</td></tr>
		<tr data-help="query"><td>*</td><td>전체 검색 (미입력 실행과 동일)</td></tr>
		<tr data-help="query"><td>검색 제한</td><td>원문 최초 시간 · 원문 100자 초과 단어</td></tr>
		<tr class="group" data-help="search" data-tab="LogButton"><th>단축키</th><th>명령</th></tr>
		<tr data-help="search" data-tab="LogButton"><td>Shift + 입력 클릭</td><td>쿼리로 입력·기간 복사</td></tr>
		<tr data-help="search" data-tab="LogButton"><td>Shift + 단어·실행 클릭</td><td>쿼리로 입력·기간 복사 + 실행</td></tr>
		<tr data-help="search" data-tab="LogButton"><td>Shift + Ctrl + 입력·단어·실행 클릭</td><td>새 창에서 위와 같게</td></tr>
		<tr data-help="search" data-tab="LogButton"><td>Shift + Alt + 입력·단어·실행 클릭</td><td>새 탭에서 위와 같게</td></tr>
		<tr class="group" data-help="search"><th>검색어</th><th>검색 규칙 (원문 글자)</th></tr>
		<tr data-help="search"><td>모든 검색어</td><td>원문 글자 포함 (대소문자 무시, 단어 경계 없음)</td></tr>
		<tr data-help="search"><td>abc</td><td>abcd 검색 포함</td></tr>
		<tr data-help="search"><td>a b c</td><td>a b c 순서대로 있는 경우</td></tr>
		<tr data-help="search"><td>a/b</td><td>a/b 글자 그대로만</td></tr>
		<tr data-help="search"><td>ab* · *ab</td><td>와일드카드 (선행 조건 불필요)</td></tr>
		<tr data-help="search"><td>검색 제한</td><td>머신당 최대 건수 · 60초</td></tr>
		<tr class="group" data-help="manager"><th>단축키</th><th>명령</th></tr>
		<tr data-help="manager" data-tab="ManagerButton"><td>URL 클릭</td><td>우측 XLO 모니터</td></tr>
		<tr class="group" data-help="process"><th>단축키 · 표시</th><th>명령 · 뜻</th></tr>
		<tr data-help="process" data-tab="ProcessButton"><td>Esc</td><td>줄 메뉴 → 상세 패널 닫기 → 입력칸 포커스 해제</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td>↑ · ↓ (머신이름)</td><td>목록 이동 (Enter → 고름, Esc → 목록 닫기)</td></tr>
		<tr data-help="process" class="sep" data-tab="ProcessButton"><td><span class="badge running">Running</span></td><td>실행 중 (On,True)</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td><span class="badge starting">Starting</span></td><td>시작 요청 · HMXClient 처리 대기 (On,False)</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td><span class="badge stopping">Stopping</span></td><td>정지 요청 · 종료 대기 (Off,False)</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td><span class="badge stopped">Stopped</span></td><td>정지 (Off,True · Down,True)</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td><span class="badge failed">Failed</span></td><td>실행 실패 · 에러 로그 확인 (PID 음수)</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td><span class="badge downloading">Downloading</span></td><td>Git 갱신 중 · 프로세스 미실행 (Down,False)</td></tr>
		<tr data-help="process" class="sep" data-tab="ProcessButton"><td><span class="commit drift">⚠ 커밋</span></td><td>커밋 불일치 → 같은 중분류 다수와 다른 커밋</td></tr>
		<tr data-help="process" class="sep" data-tab="ProcessButton"><td><span class="pbtn on">▶</span> 시작</td><td>On · 대분류 → 위 그룹부터 (그룹 모두 Running → 다음, 실패 → 중단)</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td><span class="pbtn off">■</span> 정지</td><td>Off · 대분류 → 아래 그룹부터</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td><span class="pbtn down">↓</span> 다운</td><td>Down · Git 갱신만 (대상에 실행 중 있음 → 전체 안 함)</td></tr>
		<tr data-help="process" class="sep" data-tab="ProcessButton"><td><span class="pbtn on lit">▶</span></td><td>전부 실행 중 (누름 → 이미 선택된 값)</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td><span class="pbtn on part">▶</span></td><td>일부 실행 중 (대분류·중분류)</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td><span class="pbtn off lit">■</span></td><td>전부 정지 (누름 → 이미 선택된 값)</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td><span class="pbtn off lit fail">■</span></td><td>실행 실패 (PID 음수) 포함</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td><span class="pbtn on pend">▶</span><span class="pbtn off pend">■</span></td><td>적용 대기 (깜빡임, 상태,False)</td></tr>
		<tr data-help="process" data-tab="ProcessButton"><td><span class="pbtn down pend">↓</span></td><td>다운 중 (깜빡임, Down,False)</td></tr>
		</tbody>
		</table>
		</div>
		<div class="toolright">
		<button type="button" id="HelpClose" autofocus>닫기</button>
		</div>
		</dialog>
		<script>
		const	funcFetch	= window.fetch.bind(window);	// 원래 fetch

		const	OFFLINE_RETRY_MS	= 1000;	// 서버 연결 끊김 재시도 주기 (ms)
		const	kOffText			= document.getElementById("OffText");	// 제목 옆 연결 끊김 글 (재시도 N초, 오류 글: 툴팁)

		let	nOnlineTime		= Date.now();	// 마지막 서버 응답 시각 (끊김 경과 초)
		let	nOfflineTimer	= 0;			// 끊김 재시도 타이머 (0: 연결됨)
		let	bRetrying		= false;		// 재시도 요청 중 (응답 늦음: 겹쳐 보내지 않음)

		window.fetch = async (...arrArg) =>	// 401 (쿠키 만료·계정 변경·캐시 페이지) → 새로고침 → 로그인 페이지, 접속 안 됨 (경유 서버 502 포함) → 제목 옆 끊김 + 초당 재시도
		{
		    let	kResponse;	// 응답

		    try
		    {
		        kResponse = await funcFetch(...arrArg);
		    }
		    catch (kError)	// 접속 안 됨 (사용자 중단: 끊김 아님)
		    {
		        if (kError?.name !== "AbortError")
		        {
		            SetOffline(kError);
		        }

		        throw kError;
		    }

		    if (kResponse.status === 502)	// 경유 서버 (ToolManager·IIS): 뒤 서버 꺼짐
		    {
		        SetOffline(new Error("HTTP 502"));
		    }
		    else
		    {
		        SetOnline();
		    }

		    if (kResponse.status === 401)
		    {
		        location.reload();
		    }

		    return kResponse;
		};

		function SetOffline(kError)	// 서버 연결 끊김: 제목 옆 빨강 글 (재시도 N초, 오류 글: 툴팁) + OFFLINE_RETRY_MS 마다 재시도
		{
		    kOffText.title = ErrorText(kError);

		    if (nOfflineTimer !== 0)	// 이미 끊김
		    {
		        return;
		    }

		    nOfflineTimer = setInterval(RetryOnline, OFFLINE_RETRY_MS);

		    ShowOffline();

		    kOffText.hidden = false;
		}

		function SetOnline()	// 서버 응답: 끊김 해제·재시도 끝 → 접속 머신 목록 다시
		{
		    nOnlineTime = Date.now();

		    if (nOfflineTimer === 0)	// 연결됨 그대로
		    {
		        return;
		    }

		    clearInterval(nOfflineTimer);

		    nOfflineTimer		= 0;
		    kOffText.hidden	= true;

		    LoadMachine();
		}

		function ShowOffline()	// 끊김 글: 마지막 응답 뒤 경과 초
		{
		    kOffText.textContent = `서버 연결 끊김 - 재시도 ${Math.round((Date.now() - nOnlineTime) / 1000)}초`;
		}

		async function RetryOnline()	// OFFLINE_RETRY_MS 마다: 서버 확인 (응답: 해제, 실패: 경과 초만 갱신, 앞 확인 응답 대기 중: 건너뜀)
		{
		    ShowOffline();

		    if (bRetrying)
		    {
		        return;
		    }

		    bRetrying = true;

		    try
		    {
		        await fetch("/api/echo?ping", { cache: "no-store" });
		    }
		    catch (kError)	// 아직 끊김 (SetOffline 이 오류 글 갱신)
		    {
		    }
		    finally
		    {
		        bRetrying = false;
		    }
		}

		const	sZoneName	= document.getElementById("TitleText").textContent.split(" ").slice(1).join(" ").toLowerCase();	// 존: 제목 HMX 뒤 (예: HMX Alpha → alpha)

		const	kTitleText		= document.getElementById("TitleText");	// 제목 글자
		const	kTitleBox		= kTitleText.parentElement;				// 제목 상자 (h1)
		const	sSiteTitleColor	= kTitleBox.style.color;				// 사이트 제목 색
		const	MENU_LIST		= __MENU__;								// 보일 탭 (설정 aMenu: 탭 버튼 이름, 그 순서대로, 없음·맞는 이름 없음: 전체)

		function GetSplitColor(sZone)	// 창 사이 바·칸 경계 색: SM 존 색 (존 이름 포함 여부, 제목 색과 별개)
		{
		    return sZone.includes("real") ? "#c42b2b" : (sZone.includes("alpha") ? "#2f6fde" : (sZone.includes("dev") ? "#1b7a3b" : "var(--bgColor-emphasis)"));
		}

		function SetTabZone(kButton)	// 탭 존 (탭 버튼 data-zone·data-zonecolor, 예: 운영 탭 Manager:sZone): 사이트 존과 다름 → 존 색만 탭 존 (제목 색 포함 페이지 전체, 제목 글자: 그대로), 그 외: 사이트 존 색
		{
		    const	sZone	= kButton?.dataset.zone ?? "";		// 탭 존 (없음: "")
		    const	sColor	= kButton?.dataset.zonecolor ?? "";	// 탭 존 색 (서버 GetZoneColor)
		    const	bTab	= sZone !== "" && sZone.toLowerCase() !== sZoneName && sColor.startsWith("#");
		    const	kRoot	= document.documentElement.style;	// 루트: --zone 바꿈 → :root 존 파생 변수 (버튼·포커스 색) 다시 계산

		    if (bTab)
		    {
		        kRoot.setProperty("--zone", sColor);
		    }
		    else
		    {
		        kRoot.removeProperty("--zone");
		    }

		    kRoot.setProperty("--split", GetSplitColor(bTab ? sZone.toLowerCase() : sZoneName));

		    kTitleBox.style.color = bTab ? sColor : sSiteTitleColor;	// 제목 색 (글자: 그대로)
		}

		function ApplyMenu()	// 탭 메뉴: aMenu 이름 탭만 그 순서대로 (계정·힌트 버튼: 늘 끝, 없는 이름: 무시, 맞는 이름 없음: 전체)
		{
		    const	kTabBar		= document.getElementById("TabBar");
		    const	kHelp		= document.getElementById("HelpButton");
		    const	kAccount	= document.getElementById("AccountButton");																			// 계정 버튼 (힌트 왼쪽)
		    const	arrTab		= [...kTabBar.querySelectorAll("button")].filter(kButton => kButton !== kHelp && kButton !== kAccount);				// 탭 버튼 (계정·힌트 제외)
		    const	arrShow		= MENU_LIST.map(sName => arrTab.find(kButton => kButton.textContent === sName)).filter(kButton => kButton != null);	// 보일 탭 (aMenu 순서)

		    if (arrShow.length === 0)	// aMenu 없음·맞는 이름 없음: 전체
		    {
		        return;
		    }

		    for (const kButton of arrTab)
		    {
		        kButton.hidden = !arrShow.includes(kButton);
		    }

		    for (const kButton of arrShow)	// aMenu 순서 (계정 앞)
		    {
		        kTabBar.insertBefore(kButton, kAccount);
		    }

		    for (const kRow of document.querySelectorAll("#HelpTable tr[data-tab], #HelpTabs button[data-tab]"))	// 힌트 표 줄·힌트 탭 버튼: 숨은 탭 것 숨김 (data-tab 탭 하나라도 숨음)
		    {
		        kRow.hidden = kRow.dataset.tab.split(" ").some(sId => document.getElementById(sId)?.hidden !== false);
		    }
		}

		document.documentElement.style.setProperty("--split", GetSplitColor(sZoneName));	// 창 사이 바·칸 경계 = SM 존 색 (제목 색과 별개)

		const	kFilterPanel			= document.getElementById("FilterPanel");																											// 명령 창: 검색 조건 (덤프·파일 모드 숨김)
		const	kMachinePick			= document.getElementById("MachinePick");																											// 머신 드롭 박스 묶음 (밖 클릭: 목록 닫음)
		const	kMachineBox				= document.getElementById("MachineBox");																											// 머신 드롭 박스 (전체·고른 머신 표시)
		const	kMachineMenu			= document.getElementById("MachineMenu");																											// 펼친 목록 (왼쪽 체크 박스)
		const	kMachineList			= document.getElementById("MachineList");
		const	kMachineAll				= document.getElementById("MachineAll");																											// 전체 (체크: 모든 머신)
		const	kStart					= document.getElementById("StartDate");																												// 시작일 (기본: 어제 KST)
		const	kStartHour				= document.getElementById("StartHour");																												// 시작 시 (00~23, 기본 00)
		const	kEnd					= document.getElementById("EndDate");																												// 종료일 (기본: 오늘 KST)
		const	kEndHour				= document.getElementById("EndHour");																												// 종료 시 (00~24, 기본 24 = 그날 23시 파일까지)
		const	kStartMinute			= document.getElementById("StartMinute");																											// 시작 분 (00~59, 서치·쿼리)
		const	kEndMinute				= document.getElementById("EndMinute");																												// 종료 분 (00~59, 서치·쿼리)
		const	kStartSecond			= document.getElementById("StartSecond");																											// 시작 초 (00~59, 서치·쿼리, 포함)
		const	kEndSecond				= document.getElementById("EndSecond");																												// 종료 초 (00~59, 서치·쿼리, 미포함)
		const	kQueryRange				= document.getElementById("QueryRange");																											// D: 쿼리 최근 기간 (분, 첫 칸 선택: D 해제)
		const	kQuerySaveDialog		= document.getElementById("QuerySaveDialog");																										// 쿼리·알람 추가·수정 창 (알람: class alarm → 알람 칸)
		const	kQuerySaveKey			= document.getElementById("QuerySaveKey");																											// 쿼리 추가·수정 제목
		const	kQuerySaveQuery			= document.getElementById("QuerySaveQuery");																										// 쿼리 추가·수정 쿼리 (줄바꿈: 빈칸으로 저장)
		const	kQuerySaveQueryLabel	= document.getElementById("QuerySaveQueryLabel");																									// 쿼리 칸 이름 (저장 서치: 검색어)
		const	kQuerySaveTitle			= document.getElementById("QuerySaveTitle");																										// 쿼리 추가·수정 창 제목
		const	kQuerySaveMinute		= document.getElementById("QuerySaveMinute");																										// 쿼리 추가·수정 간격 (분)
		const	kAlarmMinute			= document.getElementById("AlarmMinute");																											// 알람 간격 (분)
		const	kAlarmCount				= document.getElementById("AlarmCount");																											// 알람 건수
		const	kAlarmDelay				= document.getElementById("AlarmDelay");																											// 알람 억제 (분)
		const	kAlarmMail				= document.getElementById("AlarmMail");																												// 알람 메일 받는 계정 고르기 (추가 → 밑 목록)
		const	kAlarmMailList			= document.getElementById("AlarmMailList");																											// 알람 메일 받는 계정 목록
		const	kAlarmHook				= document.getElementById("AlarmHook");																												// 알람 훅 주소
		const	kAlarmSMS				= document.getElementById("AlarmSMS");																												// 알람 SMS 받는 계정 고르기 (추가 → 밑 목록)
		const	kAlarmSMSList			= document.getElementById("AlarmSMSList");																											// 알람 SMS 목록
		const	kAlarmEnable			= document.getElementById("AlarmEnable");																											// 알람 사용
		const	kQuerySettingTitle		= document.getElementById("QuerySettingTitle");																										// 설정 창 제목 (누른 버튼: 저장·알람·필드)
		const	kQueryEditAdd			= document.getElementById("QueryEditAdd");																											// 설정 창 추가 버튼
		const	kAlarmHistoryDialog		= document.getElementById("AlarmHistoryDialog");																									// 알람 기록 창 (날짜·제목·채널·결과, 전체 스크롤)
		const	kAlarmHistoryScroll		= document.getElementById("AlarmHistoryScroll");																									// 알람 기록 줄 상자
		const	kAlarmHistoryList		= document.getElementById("AlarmHistoryList");																										// 알람 기록 줄
		const	kAlarmHistoryViewDialog	= document.getElementById("AlarmHistoryViewDialog");																								// 알람 기록 내용 창 (제목 클릭)
		const	kAlarmHistoryViewTitle	= document.getElementById("AlarmHistoryViewTitle");																									// 알람 기록 내용 창 제목
		const	kAlarmHistoryViewInfo	= document.getElementById("AlarmHistoryViewInfo");																									// 알람 기록 내용 창 날짜·채널·결과
		const	kAlarmHistoryViewBody	= document.getElementById("AlarmHistoryViewBody");																									// 알람 기록 내용
		const	kQueryEditScroll		= document.getElementById("QueryEditScroll");																										// 설정 창 줄 상자 (높이 고정, 넘침: 스크롤)
		const	kQueryEditList			= document.getElementById("QueryEditList");																											// 설정 창 줄 (저장 쿼리·알람)
		const	kSettingButton			= document.getElementById("SettingButton");																											// 설정 버튼 (서치·쿼리 공통, 맨 오른쪽: 서치 최대 건수·제한 시간·정렬, 쿼리 제한 시간)
		const	kSearchSavedButton		= document.getElementById("SearchSavedButton");																										// 저장 버튼 (서치 모드: 저장 서치, 타이머 오른쪽)
		const	kQuerySavedButton		= document.getElementById("QuerySavedButton");																										// 저장 버튼 (쿼리 모드: 쿼리 설정 저장 탭, 실행·중단 위)
		const	kQueryAlarmButton		= document.getElementById("QueryAlarmButton");																										// 알람 버튼 (쿼리 모드: 쿼리 설정 알람 탭, 실행·중단 위)
		const	kQueryHistoryButton		= document.getElementById("QueryHistoryButton");																									// 기록 버튼 (쿼리 모드: 알람 기록 창, 알람 버튼 오른쪽)
		const	kQueryRuleButton		= document.getElementById("QueryRuleButton");																										// 필드 버튼 (쿼리 모드: 쿼리 설정 필드 탭 = 설정 aField, 실행·중단 위)
		const	kFieldRuleDialog		= document.getElementById("FieldRuleDialog");																										// 필드 추가·수정 창
		const	kFieldRuleTitle			= document.getElementById("FieldRuleTitle");																										// 필드 추가·수정 창 제목
		const	kFieldRuleKey			= document.getElementById("FieldRuleKey");																											// 필드 이름
		const	kFieldRuleRegex			= document.getElementById("FieldRuleRegex");																										// 필드 정규식 (첫 () 값)
		const	kFieldRuleValue			= document.getElementById("FieldRuleValue");																										// 필드 고정값 (있음: 정규식 안 씀)
		const	kSettingDialog			= document.getElementById("SettingDialog");																											// 설정 창 (class query: 쿼리 줄만, 아니면 서치 줄만)
		const	kQuerySettingDialog		= document.getElementById("QuerySettingDialog");																									// 쿼리 설정 창
		const	kDownDialog				= document.getElementById("DownDialog");																											// 다운 확인 팝업 (File 칸·파일 트리·덤프 zip 공통)
		let		funcDownYes				= null;																																				// 다운 확인 예 동작 (null: 확인 중 아님)
		let		funcDeleteYes			= null;																																				// 삭제 확인 동작 (null: 확인 중 아님)
		const	kMachineMaxInput		= document.getElementById("MachineMaxInput");
		const	kResultMaxInput			= document.getElementById("ResultMaxInput");
		const	kSearchLimitInput		= document.getElementById("SearchLimitInput");																										// 설정 창 서치 제한 시간 (초)
		const	kQueryLimitInput		= document.getElementById("QueryLimitInput");																										// 설정 창 쿼리 제한 시간 (초)
		const	kSortSelect				= document.getElementById("SortSelect");																											// 설정 창 결과 정렬
		const	kColGuide				= document.getElementById("ColGuide");																												// 칸 너비 드래그 위치 세로선 (표는 놓을 때 한 번만 배치)
		const	kKey					= document.getElementById("SearchKey");																												// 검색어 (서치만)
		const	kSearchButton			= document.getElementById("SearchButton");																											// 검색 버튼
		const	kQueryKey				= document.getElementById("QueryKey");																												// 쿼리 입력칸
		const	kQueryRunButton			= document.getElementById("QueryRunButton");																										// 쿼리 버튼
		const	kTimerButton			= document.getElementById("TimerButton");																											// 타이머 버튼 (기능 버튼 맨 앞: 쿼리 알람·서치 저장 왼쪽, 초 입력 → 초 줄여 감 → 0: 실행 → 다시, 지금 탭 타이머 표시)
		const	kTimer					= { kSearch: { sName: "서치", kRun: kSearchButton, nSec: 10, nDue: 0, nId: 0, kFrom: null, kTo: null, nLast: 0, sPick: "" }, kQuery: { sName: "쿼리", kRun: kQueryRunButton, nSec: 10, nDue: 0, nId: 0, kFrom: null, kTo: null, nLast: 0, sPick: "" } };	// 서치·쿼리 타이머 따로 (실행 버튼, 주기 초 (기본 X초), 다음 실행 시각 ms, 1초 setInterval: 0 = 꺼짐, 실행 기간 (초 포함), 마지막 실행 시각 ms, 마지막으로 넣은 S·E 표시)
		const	TIMER_SEC_DEFAULT		= 10;																																				// 타이머 기본 주기 (초, 리셋 때)
		const	QUERY_DAY_MAX			= 1096;																																				// 쿼리 기간 최대 (일, 3년, 서버 m_nQueryDayMax 와 같음 → 쿼리 S 달력 최소)
		const	TIMER_SEC_MIN			= 1;																																				// 타이머 최소 주기 (초)
		const	TIMER_SEC_MAX			= 86400;																																			// 타이머 최대 주기 (초, 1일)
		const	PROGRESS_DELAY_MS		= 2000;																																				// 진행 팝업 지연 (ms, 로딩 인디케이터 형식 모든 팝업: 이보다 빨리 끝남 → 안 띄움, 깜빡임 방지)
		const	kQueryStopButton		= document.getElementById("QueryStopButton");																										// 쿼리 중단 버튼
		const	kQueryProgress			= document.getElementById("QueryProgress");																											// 쿼리 진행 표시
		const	kStopButton				= document.getElementById("StopButton");																											// 검색 중지 버튼
		const	kFilterTool				= document.getElementById("FilterTool");																											// 기능 버튼 묶음 (타이머 ~ 설정: 1줄 오른쪽 끝, 창 좁음: 2줄 실행·중단 오른쪽, PlaceToolButtons)
		const	kSearchBox				= document.getElementById("SearchBox");																												// 2줄: 입력칸·실행·중단 (기능 버튼 내려오는 곳)
		const	TOOL_GAP				= 6;																																				// 명령 창 줄 요소 최소 간격 (px, 공통 규칙: 평소 12·안 8 → 창 좁으면 이 값까지 줄여 버팀 (.filter.tight), 기능 버튼도 E "미만" 과 이 간격까지, 그보다 좁으면 내려감)
		const	kProgress				= document.getElementById("SearchProgress");																										// 검색 진행 표시
		const	kResultPanel			= document.getElementById("ResultPanel");																											// 서치 1번 창: 검색 결과
		const	kResultTitle			= document.getElementById("ResultTitle");
		const	kResultPager			= document.getElementById("ResultPager");																											// 페이지 번호
		const	kResultScroll			= document.getElementById("ResultScroll");
		const	kResultTable			= document.getElementById("ResultTable");																											// 서치 1번 창 표
		const	kResultBody				= document.getElementById("ResultBody");
		const	kQueryTitle				= document.getElementById("QueryTitle");																											// 쿼리 1번 창 제목
		const	kQueryPager				= document.getElementById("QueryPager");																											// 쿼리 1번 창 페이지 번호
		const	kQueryBody				= document.getElementById("QueryBody");																												// 쿼리 1번 창 결과 줄
		const	kQueryChart				= document.getElementById("QueryChart");																											// 쿼리 1번 창 시간별 건수 막대
		const	kQueryPrev				= document.getElementById("QueryPrev");																												// ← 이전 단계
		const	kQueryDepth				= document.getElementById("QueryDepth");																											// 지금 단계 (0: 최초, 누름: 숫자 입력)
		const	kQueryNext				= document.getElementById("QueryNext");																												// → 다음 단계
		const	kQueryLens				= document.getElementById("QueryLens");																												// 쿼리 막대 렌즈 (막대 왼쪽 ↑·스텝·↓)
		const	kLensUp					= document.getElementById("LensUp");																												// 렌즈 ↑ 큰 단위 (스텝 0: 기간 넓혀 쿼리)
		const	kLensStep				= document.getElementById("LensStep");																												// 렌즈 숫자 (넓힌 깊이 - 스텝: 0 처음 기간 기본 단위, ↓ -1·↑ +1, 툴팁: 지금 단위)
		const	kLensDown				= document.getElementById("LensDown");																												// 렌즈 ↓ 작은 단위 (초봉까지)
		const	kQueryPanel				= document.getElementById("QueryPanel");																											// 쿼리 2번 창: 로그
		const	kQueryLogTitle			= document.getElementById("QueryLogTitle");																											// 쿼리 2번 창 제목
		const	kQueryScroll			= document.getElementById("QueryScroll");
		const	kQuerySplitter			= document.getElementById("QuerySplitter");																											// 쿼리 1·2번 창 사이 바
		const	kFilterSplitter			= document.getElementById("FilterSplitter");																										// 명령 창·결과 창 사이 바 (쿼리·서치)
		const	kQuerySplit				= document.getElementById("QuerySplit");																											// 쿼리 2번 창 필드 + 로그 (--fieldWidth: 필드 폭)
		const	kQueryField				= document.getElementById("QueryField");																											// 쿼리 2번 창 필드 상자
		const	kQueryFieldSplitter		= document.getElementById("QueryFieldSplitter");																									// 필드·로그 사이 세로선
		const	kQueryFieldList			= document.getElementById("QueryFieldList");																										// 쿼리 2번 창 필드 목록
		const	kQueryFieldPanel		= document.getElementById("QueryFieldPanel");																										// 필드 값 목록 새 창 (필드 모드만)
		const	kQueryFieldTitle		= document.getElementById("QueryFieldTitle");																										// 필드 새 창 제목
		const	kQueryFieldColumn		= document.getElementById("QueryFieldColumn");																										// 필드 새 창 값 칸 이름
		const	kQueryFieldScroll		= document.getElementById("QueryFieldScroll");
		const	kQueryFieldBody			= document.getElementById("QueryFieldBody");																										// 필드 새 창 값 줄
		const	kQueryFieldPager		= document.getElementById("QueryFieldPager");																										// 필드 새 창 아래 페이지 번호
		const	kViewPanel				= document.getElementById("ViewPanel");																												// 서치 2번 창: 파일 보기
		const	kViewTitle				= document.getElementById("ViewTitle");
		const	kViewScroll				= document.getElementById("ViewScroll");
		const	kViewBody				= document.getElementById("ViewBody");
		const	kViewPrev				= document.getElementById("ViewPrev");																												// 서치 2번 창 포지션 « 위로 VIEW_COUNT 줄 (읽어 쌓인 범위 안)
		const	kViewShift				= document.getElementById("ViewShift");																												// 서치 2번 창 위치 (0: 처음 연 위치)
		const	kViewNext				= document.getElementById("ViewNext");																												// 서치 2번 창 포지션 » 아래로 VIEW_COUNT 줄 (읽어 쌓인 범위 안)
		const	kLogButton				= document.getElementById("LogButton");																												// 서치 탭 버튼
		const	kDumpButton				= document.getElementById("DumpButton");																											// 덤프 탭 버튼
		const	kDumpPanel				= document.getElementById("DumpPanel");																												// 덤프 1번 창: 덤프 파일
		const	kDumpTitle				= document.getElementById("DumpTitle");
		const	kDumpScroll				= document.getElementById("DumpScroll");
		const	kDumpTable				= document.getElementById("DumpTable");																												// 덤프 1번 창 표
		const	kDumpBody				= document.getElementById("DumpBody");
		const	kDumpProgress			= document.getElementById("DumpProgress");																											// 덤프 진행 팝업
		const	kDumpDeleteAll			= document.getElementById("DumpDeleteAll");																											// 덤프 전체 삭제 버튼
		const	kDeleteDialog			= document.getElementById("DeleteDialog");																											// 삭제 확인 팝업
		const	kDeleteText				= document.getElementById("DeleteText");
		const	kFileButton				= document.getElementById("FileButton");																											// 파일 탭 버튼
		const	kFilePanel				= document.getElementById("FilePanel");																												// 파일 1번 창: 클라 감시 폴더 트리
		const	kManagerButton			= document.getElementById("ManagerButton");																											// 운영 탭 버튼
		const	kManagerPanel			= document.getElementById("ManagerPanel");																											// 운영 창: 게임 서버 목록·동접·차트·모니터
		const	kProcessButton			= document.getElementById("ProcessButton");																											// 반영 탭 버튼
		const	kProcessPanel			= document.getElementById("ProcessPanel");																											// 반영 창: 게임 서버 프로세스 목록·작업 (서랍·메뉴·팝업 포함)
		const	kFileTitle				= document.getElementById("FileTitle");
		const	kFileTree				= document.getElementById("FileTree");
		const	kFileScroll				= document.getElementById("FileScroll");																											// 파일 1번 창 왼쪽: 트리
		const	kFileSplitter			= document.getElementById("FileSplitter");																											// 파일 1번 창 좌우 사이 세로선
		const	kFileViewTitle			= document.getElementById("FileViewTitle");																											// 파일 1번 창 오른쪽: 파일 보기
		const	kFileViewScroll			= document.getElementById("FileViewScroll");
		const	kFileViewBody			= document.getElementById("FileViewBody");
		const	kFilePrev				= document.getElementById("FilePrev");																												// 파일 보기 포지션 « (위로, 위에 쌓임)
		const	kFileShift				= document.getElementById("FileShift");																												// 파일 보기 포지션 (0: 파일 맨 아래)
		const	kFileNext				= document.getElementById("FileNext");																												// 파일 보기 포지션 » (아래로)
		const	kQueryButton			= document.getElementById("QueryButton");																											// 쿼리 탭 버튼
		const	kHelpDialog				= document.getElementById("HelpDialog");																											// 힌트 팝업
		const	kAccountDialog			= document.getElementById("AccountDialog");																											// 계정 팝업
		const	kAccountEditDialog		= document.getElementById("AccountEditDialog");																										// 계정 추가·수정·삭제 창
		const	MACHINE_LIMIT			= 1000000;																																			// 머신당 최대 건수 상한 (서버·클라 m_nSearchMax 와 같음)
		const	RESULT_LIMIT			= 1000000;																																			// 전체 최대 건수 상한
		const	MACHINE_DEFAULT			= 1000;																																				// 머신당 최대 건수 기본값 (서버·클라 m_nSearchDefault 와 같음)
		const	RESULT_DEFAULT			= 10000;																																			// 전체 최대 건수 기본값 (모든 머신 합)
		const	LIMIT_KEY				= "HMX.Limit";																																		// 설정 localStorage 키 (서치·쿼리)
		const	LIMIT_SEC_DEFAULT		= 60;																																				// 서치·쿼리 제한 시간 기본값 (초, 서버 m_nSearchLimitSec·m_nQueryLimitSec 와 같음)
		const	LIMIT_SEC_MAX			= 600;																																				// 서치·쿼리 제한 시간 상한 (초, 서버와 같음)
		let		nMachineMax				= MACHINE_DEFAULT;																																	// 머신당 최대 건수 (설정)
		let		nResultMax				= RESULT_DEFAULT;																																	// 전체 최대 건수 (설정, 넘음: 이후 머신 검색 안 함)
		let		nSearchLimitSec			= LIMIT_SEC_DEFAULT;																																// 서치 제한 시간 (설정, 초, /api/search nLimit → 클라 마감)
		let		nQueryLimitSec			= LIMIT_SEC_DEFAULT;																																// 쿼리 제한 시간 (설정, 초, /api/query·stat·lens nLimit)
		const	SORT_TIME				= "time";																																			// 시간 정렬 (Search.SortTime)
		const	SORT_BASE				= "base";																																			// 머신 정렬 (받은 순서)
		const	SORT_DEFAULT			= SORT_TIME;																																		// 결과 정렬 기본값
		const	SORT_INFO				= { [SORT_TIME]: "시간 정렬", [SORT_BASE]: "머신 정렬" };																							// 결과 정렬 값 → 표시
		let		sSort					= SORT_DEFAULT;																																		// 결과 정렬 (설정)
		const	MACHINE_REFRESH_MS		= 1000;																																				// 접속 머신 목록 자동 갱신 주기 (ms, 서버 연결 끊김 감지 겸)
		let		bMachineLoading			= false;																																			// 접속 머신 조회 중 (겹침 방지)
		let		sMachineSignature		= "";																																				// 마지막 그린 머신 목록 (바뀐 경우만 다시 그림)
		let		arrMachineList			= [];																																				// 드롭 박스 머신 (이름순 + 끊긴 체크 머신)
		const	setMachineCheck			= new Set();																																		// 체크한 머신 (비면: 전체)
		let		nMachineCount			= 0;																																				// 접속 머신 수
		let		arrMachineOption		= [];																																				// 서치 드롭 박스 목록 [값, 표시] (쿼리 모드 갔다 오면 다시 그림)
		let		arrQueryMachine			= [];																																				// 쿼리 드롭 박스 색인 머신 (이름순, 접속 머신·쿼리칸 머신과 합쳐 그림)
		const	QUERY_MACHINE_REGEX		= /^\s*(?:\(\s*(Machine:[^\s()]+(?:\s+OR\s+Machine:[^\s()]+)+)\s*\)|(Machine:[^\s()]+))(?:\s+AND\s+|\s*$)/;											// 쿼리칸 앞 머신 조건 (Machine:A·(Machine:A OR Machine:B) 뒤 AND·끝)
		const	OPEN_WORD				= "#ResultBody td.log, #ViewBody .line, #QueryBody td.log, #QueryBody .querytext, #QueryBody .queryfield span[data-field]";							// Ctrl·Alt+클릭 단어 칸 (입력칸에 단어 추가)
		const	OPEN_TARGET				= "a[href], button, input, select, textarea, label, td.filename, #FileTree div.node";																// Ctrl·Alt+클릭 새 창 대상
		const	OPEN_SELF				= ".fieldname";																																		// 원래 새 창 여는 것 (필드 이름): 그대로
		const	OPEN_SKIP				= "td.act, td.fileact, #ProcessTables tbody, #ProcessDrawer, #ProcessMenu";																			// 줄 동작 (덤프 Ac.·반영 줄·상세 패널·줄 메뉴): 원래 동작
		const	OPEN_STATE_INPUT		= ["QueryRange", "StartDate", "StartHour", "StartMinute", "StartSecond", "EndDate", "EndHour", "EndMinute", "EndSecond", "SearchKey", "QueryKey"];	// 새 창에 넘길 입력칸
		let		sOpenMode				= "";																																				// OpenWindow 방식 ("": 크기 지정 새 창, browser: 같은 크기·자리 새 창 (Ctrl), tab: 새 탭 (Alt))
		const	OPEN_TITLE				= ".panel .resulthead, #ViewPanel > .filter";																										// Ctrl·Alt+클릭 하위 창 제목란
		let		kSelectTable			= null;																																				// 마지막 누른 표 (Ctrl+A 대상, 없음: 맨 위 표)
		const	SHIFT_QUERY_WORD		= "#QueryBody td.log, #QueryBody .querytext";																										// Shift+클릭 쿼리 단어 칸 (→ 서치)
		let		bShiftClick				= false;																																			// Shift+누름 실행함: 뒤따르는 클릭 막음

		for (let nHour = 0; nHour <= 24; ++nHour)	// 시 선택: 시작 00~23, 종료 00~24
		{
		    const	sHour	= String(nHour).padStart(2, "0");

		    if (nHour < 24)
		    {
		        kStartHour.add(new Option(sHour, sHour));
		    }

		    kEndHour.add(new Option(sHour, sHour));
		}

		kStartHour.value	= "00";	// 기본: 어제 0시
		kEndHour.value		= "24";	// 기본: 오늘 24시

		for (let nMinute = 0; nMinute < 60; ++nMinute)	// 분·초 선택: 00~59
		{
		    const	sMinute	= String(nMinute).padStart(2, "0");

		    kStartMinute.add(new Option(sMinute, sMinute));
		    kEndMinute.add(new Option(sMinute, sMinute));
		    kStartSecond.add(new Option(sMinute, sMinute));
		    kEndSecond.add(new Option(sMinute, sMinute));
		}

		function GetFilter()	// 선택 값 (nStart·nEnd: yyyyMMddHH 끝 포함, nStartMin·nEndMin: yyyyMMddHHmm, nStartSec·nEndSec: yyyyMMddHHmmss, 시작 포함·끝 미포함)
		{
		    const	nStart	= Number(kStart.value.replaceAll("-", "")) * 100 + Number(kStartHour.value);
		    const	nEnd	= Number(kEnd.value.replaceAll("-", "")) * 100 + Number(kEndHour.value);

		    return {
		        sMachine:	GetMachineText(),
		        arrMachine:	GetMachines(),
		        nStart:		nStart,
		        nEnd:		nEnd,
		        nStartMin:	nStart * 100 + Number(kStartMinute.value),
		        nEndMin:	nEnd * 100 + (kEndHour.value === "24" ? 0 : Number(kEndMinute.value)),
		        nStartSec:	(nStart * 100 + Number(kStartMinute.value)) * 100 + Number(kStartSecond.value),
		        nEndSec:	(nEnd * 100 + (kEndHour.value === "24" ? 0 : Number(kEndMinute.value))) * 100 + (kEndHour.value === "24" ? 0 : Number(kEndSecond.value)),
		        sKey:		kKey.value.trim(),
		    };
		}

		async function FetchJson(sUrl, kOption)	// 요청 → JSON (HTTP 오류: throw "HTTP 상태", 경유 서버 오류 페이지 → Bad Data 대신 상태)
		{
		    const	kResponse	= await fetch(sUrl, kOption);

		    if (!kResponse.ok)
		    {
		        throw new Error(`HTTP ${kResponse.status}`);
		    }

		    return await kResponse.json();
		}

		function PlaceToolButtons()	// 기능 버튼 자리 (창 크기·탭 바뀔 때): 1줄 오른쪽 끝 (E "미만" 과 TOOL_GAP px 만 남아도 둠, 흐름 밖 고정) → 못 들어가면 입력칸 줄 실행·중단 오른쪽 → 그래서 입력칸이 E 줄 (종료일 미만) 보다 좁아지면 기능 버튼만 한 줄 (입력칸 위, 오른쪽 끝)
		{
		    if (kFilterPanel.hidden)
		    {
		        return;
		    }

		    const	kRow		= kSearchBox.parentElement;			// 명령 창 (.filter)
		    const	kEndLabel	= kEnd.parentElement;				// E 줄 (날짜·시·분·초·미만)
		    const	nTop		= kRow.firstElementChild.offsetTop;	// 1줄 위치 (리셋 버튼)

		    kFilterTool.classList.remove("row", "fix");
		    kFilterTool.style.height = "";
		    kRow.classList.remove("tight");

		    if (kFilterTool.parentElement === kSearchBox)	// 입력칸 줄에 있음: 1줄 흐름 자리로 되돌려 잼
		    {
		        kSearchBox.before(kFilterTool);
		    }

		    if (kEndLabel.offsetTop > nTop + 1)	// 1줄 요소 (M·D·S·E) 가 내려감: 간격 TOOL_GAP 까지 줄여 버팀 (공통 규칙), 그래도 내려감: 원래 간격
		    {
		        kRow.classList.add("tight");

		        if (kEndLabel.offsetTop > nTop + 1)
		        {
		            kRow.classList.remove("tight");
		        }
		    }

		    if (kEndLabel.offsetTop <= nTop + 1 && kEndLabel.getBoundingClientRect().right + TOOL_GAP + kFilterTool.offsetWidth <= kRow.getBoundingClientRect().right)	// E 가 1줄에 있고 그 오른쪽에 기능 버튼 들어감
		    {
		        kFilterTool.classList.add("fix");	// 1줄 오른쪽 끝 고정 (flex 줄바꿈 간격보다 가깝게 허용)
		        kFilterTool.style.height = `${kEndLabel.offsetHeight}px`;	// 1줄 높이 → 버튼 세로 가운데

		        return;
		    }

		    kSearchBox.append(kFilterTool);	// 입력칸 줄 실행·중단 오른쪽

		    if ((IsQueryMode() ? kQueryKey : kKey).offsetWidth < kEnd.parentElement.offsetWidth)	// 입력칸이 E 줄보다 좁음: 기능 버튼 줄 따로
		    {
		        kSearchBox.before(kFilterTool);
		        kFilterTool.classList.add("row");
		    }
		}

		function ShowStatus()	// 설정 버튼 툴팁 (지금 탭 설정·상태 요약)
		{
		    kSettingButton.title = IsQueryMode() ? `설정(쿼리 제한 ${nQueryLimitSec}초)` : `설정(접속 ${nMachineCount}, 머신당 ${nMachineMax}, 최대 ${nResultMax}, 서치 제한 ${nSearchLimitSec}초, ${sSort === SORT_TIME ? "시간" : "머신"} 정렬)`;
		}

		function ClampLimit(value, nDefault, nLimit)	// 최대 건수 값 정리 (정수 1 ~ nLimit, 그 외: nDefault)
		{
		    const	nValue	= Math.floor(Number(value));

		    if (value === "" || value == null || !Number.isFinite(nValue))
		    {
		        return nDefault;
		    }

		    return Math.min(Math.max(1, nValue), nLimit);
		}

		function GetSort(sValue)	// 결과 정렬 값 정리 (SORT_INFO 에 없음: SORT_DEFAULT)
		{
		    return Object.hasOwn(SORT_INFO, sValue) ? sValue : SORT_DEFAULT;
		}

		function LoadLimit()	// 저장된 설정 읽기 (서치·쿼리, 없음·오류: 기본값)
		{
		    try
		    {
		        const	kSaved	= JSON.parse(localStorage.getItem(LIMIT_KEY) ?? "null");

		        if (kSaved != null)
		        {
		            nMachineMax		= ClampLimit(kSaved.nMachineMax, MACHINE_DEFAULT, MACHINE_LIMIT);
		            nResultMax		= ClampLimit(kSaved.nResultMax, RESULT_DEFAULT, RESULT_LIMIT);
		            nSearchLimitSec	= ClampLimit(kSaved.nSearchLimitSec, LIMIT_SEC_DEFAULT, LIMIT_SEC_MAX);
		            nQueryLimitSec	= ClampLimit(kSaved.nQueryLimitSec, LIMIT_SEC_DEFAULT, LIMIT_SEC_MAX);
		            sSort			= GetSort(kSaved.sSort);	// 저장 전 항목: 기본값
		        }
		    }
		    catch (kError)	// 저장소 사용 불가·형식 오류
		    {
		        console.error(kError);
		    }
		}

		function OpenSetting()	// 설정 창 열기 (지금 탭 항목만: 서치 최대 건수·제한 시간·정렬, 쿼리 제한 시간, 서치 중: 안 엶)
		{
		    const	bQuery	= IsQueryMode();

		    if (!bQuery && Search.bSearching)
		    {
		        return;
		    }

		    kSettingDialog.classList.toggle("query", bQuery);

		    kMachineMaxInput.value	= nMachineMax;
		    kResultMaxInput.value	= nResultMax;
		    kSearchLimitInput.value	= nSearchLimitSec;
		    kQueryLimitInput.value	= nQueryLimitSec;
		    kSortSelect.value		= sSort;

		    kSettingDialog.showModal();
		}

		function SaveSetting()	// 설정 저장 (서치·쿼리 항목 모두 한 키, 저장 실패: 이 창만 적용, 정렬 바뀜: 지금 결과 다시 정렬)
		{
		    const	sSortBefore	= sSort;

		    nMachineMax		= ClampLimit(kMachineMaxInput.value, MACHINE_DEFAULT, MACHINE_LIMIT);
		    nResultMax		= ClampLimit(kResultMaxInput.value, RESULT_DEFAULT, RESULT_LIMIT);
		    nSearchLimitSec	= ClampLimit(kSearchLimitInput.value, LIMIT_SEC_DEFAULT, LIMIT_SEC_MAX);
		    nQueryLimitSec	= ClampLimit(kQueryLimitInput.value, LIMIT_SEC_DEFAULT, LIMIT_SEC_MAX);
		    sSort			= GetSort(kSortSelect.value);

		    try
		    {
		        localStorage.setItem(LIMIT_KEY, JSON.stringify({ nMachineMax, nResultMax, nSearchLimitSec, nQueryLimitSec, sSort }));
		    }
		    catch (kError)	// 저장소 사용 불가
		    {
		        console.error(kError);
		    }

		    kSettingDialog.close();

		    ShowStatus();

		    if (sSort !== sSortBefore && Search.arrReceived.length > 0)	// 정렬 바뀜: 다시 정렬 (다시 검색 안 함)
		    {
		        Search.SortResult();
		        Search.ShowResultPage(0);
		    }
		}

		function FixRange(bStartChanged)	// S·E 직접 고침: D 해제 + 시작 > 종료: 바꾼 쪽 기준 반대쪽 맞춤 (쿼리 모드: D 해제만)
		{
		    kQueryRange.selectedIndex = 0;	// D 해제 (서치·쿼리)

		    if (kQueryButton.classList.contains("cur"))	// 쿼리 모드
		    {
		        return;
		    }

		    const	kFilter	= GetFilter();

		    if (kFilter.nStartSec > kFilter.nEndSec)	// 초까지 비교
		    {
		        if (bStartChanged)
		        {
		            kEnd.value			= kStart.value;
		            kEndHour.value		= kStartHour.value;
		            kEndMinute.value	= kStartMinute.value;
		            kEndSecond.value	= kStartSecond.value;
		        }
		        else
		        {
		            kStart.value		= kEnd.value;
		            kStartHour.value	= kEndHour.value === "24" ? "23" : kEndHour.value;
		            kStartMinute.value	= kEndHour.value === "24" ? "59" : kEndMinute.value;
		            kStartSecond.value	= kEndHour.value === "24" ? "59" : kEndSecond.value;
		        }
		    }

		    ShowStatus();
		}

		function GetDayBefore(sDate, nDay)	// yyyy-MM-dd 의 nDay 일 전 날짜 (yyyy-MM-dd, 달력 min)
		{
		    const [nYear, nMonth, nDate]	= sDate.split("-").map(Number);
		    const	kDate	= new Date(nYear, nMonth - 1, nDate - nDay);	// 일 넘침: 달·해 맞춤
		    const	funcPad	= (nValue) => String(nValue).padStart(2, "0");

		    return `${kDate.getFullYear()}-${funcPad(kDate.getMonth() + 1)}-${funcPad(kDate.getDate())}`;
		}

		function LimitRange()	// S·E 고르기 제한: S 는 E 보다 늦게, E 는 S 보다 이르게 못 고름, 쿼리 S 는 E 기준 최대 기간보다 이르게 못 고름 (날짜: 달력 S 최대 E·E 최소 S·쿼리 S 최소 E - (QUERY_DAY_MAX - 1)일, 같은 날짜: 시, 같은 시: 분, 같은 분: 초 꺼짐, E 24시: 그날 끝 → 제한 없음, S = E: 됨)
		{
		    const	bDay	= kStart.value === kEnd.value && kEndHour.value !== "24";	// 같은 날짜 (E 24시: 그날 끝 → 시·분·초 제한 없음)
		    const	bHour	= bDay && kStartHour.value === kEndHour.value;				// 같은 시
		    const	bMinute	= bHour && kStartMinute.value === kEndMinute.value;			// 같은 분
		    const funcLimit	= (kSelect, bOn, funcOff) =>	// 선택 칸 꺼짐 (bOn: 제한 적용)
		    {
		        for (const kOption of kSelect.options)
		        {
		            kOption.disabled = bOn && funcOff(kOption.value);
		        }
		    };

		    kStart.max	= kEnd.value;	// S 달력: E 날짜까지
		    kStart.min	= IsQueryMode() && kEnd.value !== "" ? GetDayBefore(kEnd.value, QUERY_DAY_MAX - 1) : "";	// S 달력 (쿼리): E 날짜 - (최대 기간 - 1)일부터 (E 24시·시각 차이 1일 여유 → 서버 최대 안쪽), 서치: 제한 없음
		    kEnd.min	= kStart.value;	// E 달력: S 날짜부터

		    funcLimit(kStartHour, bDay, (sValue) => sValue > kEndHour.value);		// S: E 보다 큰 값 꺼짐
		    funcLimit(kStartMinute, bHour, (sValue) => sValue > kEndMinute.value);
		    funcLimit(kStartSecond, bMinute, (sValue) => sValue > kEndSecond.value);
		    funcLimit(kEndHour, bDay, (sValue) => sValue < kStartHour.value);		// E: S 보다 작은 값 꺼짐 (24: 항상 됨)
		    funcLimit(kEndMinute, bHour, (sValue) => sValue < kStartMinute.value);
		    funcLimit(kEndSecond, bMinute, (sValue) => sValue < kStartSecond.value);
		}

		function AddCell(kRow, sText, sClass)	// 표 칸 추가 (textContent)
		{
		    const	kCell	= kRow.insertCell();

		    kCell.textContent = sText;

		    if (sClass !== "")
		    {
		        kCell.className = sClass;
		    }

		    return kCell;
		}

		const	LINE_PAD	= 8;	// 줄 보기 pre 기본 위·아래 여백 (px, CSS pre padding)
		const	LINE_NEAR	= 20;	// 줄 보기 이어 읽기 시작 (읽은 끝에서 X줄 안)

		function GetLineHeight(kBody)	// 줄 보기 한 줄 높이 (소수까지: offsetHeight 반올림 → 줄 많으면 위치 어긋남, 줄 없음: 0)
		{
		    return kBody.querySelector(".line")?.getBoundingClientRect().height ?? 0;
		}

		function SetLineSpace(kBody, kState)	// 줄 보기 위·아래 빈 높이: 안 읽은 줄 수 × 줄 높이 → 스크롤 바 처음부터 전체 크기 (kState.nExtra: 짧은 파일 아래 여백)
		{
		    const	nLineH	= GetLineHeight(kBody);

		    kBody.style.paddingTop		= nLineH > 0 && kState.nFirst > 1 ? `${LINE_PAD + (kState.nFirst - 1) * nLineH}px` : "";
		    kBody.style.paddingBottom	= nLineH > 0 ? `${LINE_PAD + Math.max(0, kState.nTotal - kState.nLast) * nLineH + (kState.nExtra ?? 0)}px` : "";
		}

		function GetLineRange(kScroll, kBody)	// 줄 보기 보이는 줄 번호 [첫, 끝] (빈 높이 포함, 줄 없음: null)
		{
		    const	nLineH	= GetLineHeight(kBody);

		    if (nLineH === 0)
		    {
		        return null;
		    }

		    const	nTop	= kScroll.scrollTop - kBody.offsetTop - LINE_PAD;	// 첫 줄 위치 기준

		    return [Math.max(1, Math.floor(nTop / nLineH) + 1), Math.max(1, Math.ceil((nTop + kScroll.clientHeight) / nLineH))];
		}

		function GetCenterAbove(kScroll, kBody)	// 줄 보기 기준 줄 (클릭·검색 줄) 위에 보일 줄 수: 보이는 줄 수의 가운데 (공통, 줄 없음: 0)
		{
		    const	nLineH	= GetLineHeight(kBody);

		    return nLineH > 0 ? Math.floor(Math.floor(kScroll.clientHeight / nLineH) / 2) : 0;
		}

		// 포지션 버튼 « N » 공통 (줄 보기: 서치 2번 창·파일 보기), kPos: { kScroll, kBody, kPrev, kShift, kNext, sInput (입력칸 id), nStep (포지션 줄 수), funcState (보는 파일 → { nAnchor: 포지션 0 줄, nFirst·nLast: 읽어 쌓인 줄, nTotal, nShiftGo·nGoTop: 옮긴 포지션·스크롤 위치 }) }

		function GetPosRange(kPos)	// 포지션 범위 [최소, 최대] (읽어 쌓인 줄 범위 끝까지: 끝 포지션 = 끝 줄, 예: 위로 -11 까지 쌓임: -11 ~ 0, 없음: null)
		{
		    const	kState	= kPos.funcState();

		    if (kState == null || kState.nAnchor == null || kState.nAnchor <= 0 || kState.nTotal === 0 || kState.nLast === 0 || kState.nLast < kState.nFirst)
		    {
		        return null;
		    }

		    return [Math.floor((kState.nFirst - kState.nAnchor) / kPos.nStep) || 0, Math.ceil((kState.nLast - kState.nAnchor) / kPos.nStep) || 0];	// -0 → 0
		}

		function GetPosLine(kPos, nShift)	// 포지션 줄 (포지션 0 줄 + nStep 줄 단위, 읽어 쌓인 줄 범위로 맞춤)
		{
		    const	kState	= kPos.funcState();

		    return Math.min(Math.max(kState.nAnchor + nShift * kPos.nStep, kState.nFirst), kState.nLast);
		}

		function GetPosRef(kPos)	// 기준 줄 (보이는 줄 가운데, 1 ~ 끝 줄, 줄 없음: 포지션 0 줄)
		{
		    const	kState		= kPos.funcState();
		    const	arrRange	= GetLineRange(kPos.kScroll, kPos.kBody);

		    if (kState == null || arrRange == null)
		    {
		        return kState?.nAnchor ?? 0;
		    }

		    return Math.min(Math.max(1, arrRange[0] + GetCenterAbove(kPos.kScroll, kPos.kBody)), Math.max(1, kState.nTotal));
		}

		function GetPosNow(kPos)	// 지금 포지션 (옮긴 뒤 스크롤 전: 옮긴 포지션, 맨 위: 최소, 맨 아래: 최대, 그 외: 기준 줄에 가장 가까운 포지션 줄, 범위 없음: 0)
		{
		    const	kState		= kPos.funcState();
		    const	kScroll		= kPos.kScroll;
		    const	arrRange	= GetPosRange(kPos);

		    if (arrRange == null)
		    {
		        return 0;
		    }

		    if (kState.nShiftGo != null && Math.abs(kScroll.scrollTop - kState.nGoTop) < 1)	// 열기·포지션 이동 뒤 스크롤 안 함: 그 포지션 (끝 맞춤으로 가운데 못 옴도 그대로)
		    {
		        return Math.min(Math.max(kState.nShiftGo, arrRange[0]), arrRange[1]) || 0;
		    }

		    if (kScroll.scrollTop <= 0)
		    {
		        return arrRange[0];
		    }

		    if (kScroll.scrollTop + kScroll.clientHeight >= kScroll.scrollHeight - 1)
		    {
		        return arrRange[1];
		    }

		    const	nRef	= GetPosRef(kPos);										// 기준 줄
		    const	nNear	= Math.round((nRef - kState.nAnchor) / kPos.nStep);		// 끝 맞춤 없는 가까운 포지션
		    let		nBest	= Math.min(Math.max(nNear, arrRange[0]), arrRange[1]);	// 가장 가까운 포지션

		    for (let nShift = Math.max(nNear - 1, arrRange[0]); nShift <= Math.min(nNear + 1, arrRange[1]); ++nShift)	// 끝 포지션 (끝 줄로 맞춤) 도 비교
		    {
		        if (Math.abs(GetPosLine(kPos, nShift) - nRef) < Math.abs(GetPosLine(kPos, nBest) - nRef))
		        {
		            nBest = nShift;
		        }
		    }

		    return nBest || 0;	// -0 → 0
		}

		function GoPos(kPos, nShift)	// 포지션 nShift 로 스크롤 (포지션 범위로 맞춤, 포지션 줄을 보이는 줄 가운데에, 스크롤 이벤트: 포지션 표시)
		{
		    const	kState		= kPos.funcState();
		    const	arrRange	= GetPosRange(kPos);
		    const	nLineH		= GetLineHeight(kPos.kBody);

		    if (arrRange == null || nLineH === 0)
		    {
		        return;
		    }

		    const	nGo		= Math.min(Math.max(nShift, arrRange[0]), arrRange[1]);	// 갈 포지션
		    const	nLine	= GetPosLine(kPos, nGo);	// 갈 기준 줄

		    kPos.kScroll.scrollTop = kPos.kBody.offsetTop + LINE_PAD + (Math.max(1, nLine - GetCenterAbove(kPos.kScroll, kPos.kBody)) - 1) * nLineH;

		    kState.nShiftGo	= nGo;						// 옮긴 포지션 (그 뒤 스크롤 전: 그대로 표시)
		    kState.nGoTop	= kPos.kScroll.scrollTop;	// 옮긴 뒤 스크롤 위치
		}

		function ShiftPos(kPos, nDirection)	// 포지션 « (-1)·» (1): 지금 포지션 앞·뒤 포지션으로 (포지션 범위 안만, 읽기는 스크롤만)
		{
		    const	arrRange	= GetPosRange(kPos);
		    const	nShift		= GetPosNow(kPos) + nDirection;	// 갈 포지션

		    if (arrRange != null && nShift >= arrRange[0] && nShift <= arrRange[1])
		    {
		        GoPos(kPos, nShift);
		    }
		}

		function SetPosStart(kPos)	// 파일 열기 위치: 포지션 0 (스크롤 전: 0 그대로)
		{
		    const	kState	= kPos.funcState();

		    kState.nShiftGo	= 0;
		    kState.nGoTop	= kPos.kScroll.scrollTop;
		}

		function ShowPos(kPos)	// 포지션 « » 켬·끔 (범위 끝: 끔), 가운데 포지션 (0: 처음 연 위치, 스크롤 따라감, 툴팁: 포지션 범위)
		{
		    const	arrRange	= GetPosRange(kPos);
		    const	nNow		= GetPosNow(kPos);	// 지금 포지션

		    kPos.kPrev.disabled		= arrRange == null || nNow <= arrRange[0];
		    kPos.kNext.disabled		= arrRange == null || nNow >= arrRange[1];
		    kPos.kShift.textContent	= String(nNow);
		    kPos.kShift.title		= arrRange == null ? "포지션" : `포지션(${arrRange[0]} ~ ${arrRange[1]})`;
		}

		function SchedulePos(kPos)	// 스크롤: 포지션 표시 예약 (스크롤 여러 번: 화면 그릴 때 1번만)
		{
		    if (kPos.nFrame)
		    {
		        return;
		    }

		    kPos.nFrame = requestAnimationFrame(() =>
		    {
		        kPos.nFrame = 0;

		        ShowPos(kPos);
		    });
		}

		function EditPos(kPos)	// 가운데 포지션 누름: 숫자 입력 (- 포함) → Enter: 그 포지션으로 (포지션 범위로 맞춤, 끝 포지션: 끝까지), Esc·밖 누름: 취소 (포지션 1개뿐: 안 함)
		{
		    const	arrRange	= GetPosRange(kPos);
		    const	kShift		= kPos.kShift;

		    if (arrRange == null || arrRange[0] === arrRange[1] || document.getElementById(kPos.sInput) != null)
		    {
		        return;
		    }

		    const	kInput	= document.createElement("input");	// 포지션 입력 (포지션 버튼 자리, 같은 크기)
		    let		bDone	= false;	// 끝냄 (Enter·Esc·밖 누름 중 한 번만)

		    kInput.type			= "text";
		    kInput.id			= kPos.sInput;
		    kInput.title		= `포지션(${arrRange[0]} ~ ${arrRange[1]})`;
		    kInput.value		= kShift.textContent;
		    kInput.style.width	= `${Math.max(kShift.offsetWidth, 44)}px`;
		    kInput.style.height	= `${kShift.offsetHeight}px`;

		    const funcClose = (bGo) =>	// 입력 닫고 포지션 버튼 다시 (bGo: 그 포지션으로)
		    {
		        if (bDone)
		        {
		            return;
		        }

		        bDone = true;

		        const	nTarget	= Number.parseInt(kInput.value, 10);	// 입력한 포지션

		        kInput.remove();

		        kShift.style.display = "";

		        if (bGo && Number.isFinite(nTarget))
		        {
		            GoPos(kPos, nTarget);
		        }
		    };

		    kInput.addEventListener("keydown", (kEvent) =>	// Enter: 그 포지션으로, Esc: 취소
		    {
		        if (kEvent.key === "Enter" && !kEvent.isComposing)
		        {
		            kEvent.preventDefault();

		            funcClose(true);
		        }
		        else if (kEvent.key === "Escape")
		        {
		            kEvent.preventDefault();

		            funcClose(false);
		        }
		    });

		    kInput.addEventListener("blur", () => funcClose(false));	// 밖 누름: 취소

		    kShift.style.display = "none";
		    kShift.before(kInput);

		    kInput.focus();
		    kInput.select();
		}

		function GetTimer()	// 지금 탭 타이머 (서치·쿼리 따로, 덤프·파일: null)
		{
		    return kFilterPanel.hidden ? null : (IsQueryMode() ? kTimer.kQuery : kTimer.kSearch);
		}

		function OnTimerButton()	// 타이머 버튼 누름 (지금 탭 타이머): 도는 중 → 중지, 아님 → 초 입력 (정수, TIMER_SEC_MIN ~ MAX) → Enter: 시작, Esc·밖 누름: 취소 (포지션 입력과 같음)
		{
		    const	kItem	= GetTimer();

		    if (kItem == null || document.getElementById("TimerInput") != null)
		    {
		        return;
		    }

		    if (kItem.nId !== 0)	// 도는 중: 중지
		    {
		        StopTimer(kItem);

		        return;
		    }

		    const	kInput	= document.createElement("input");	// 초 입력 (타이머 버튼 자리, 같은 크기)
		    let		bDone	= false;	// 끝냄 (Enter·Esc·밖 누름 중 한 번만)

		    kInput.type			= "text";
		    kInput.id			= "TimerInput";
		    kInput.title		= `${kItem.sName} 타이머 주기(초, ${TIMER_SEC_MIN} ~ ${TIMER_SEC_MAX})`;
		    kInput.value		= String(kItem.nSec);
		    kInput.style.width	= `${Math.max(kTimerButton.offsetWidth, 44)}px`;
		    kInput.style.height	= `${kTimerButton.offsetHeight}px`;

		    const funcClose = (bStart) =>	// 입력 닫고 타이머 버튼 다시 (bStart: 그 초로 시작)
		    {
		        if (bDone)
		        {
		            return;
		        }

		        bDone = true;

		        const	nSec	= Number.parseInt(kInput.value, 10);	// 입력한 초

		        kInput.remove();

		        kTimerButton.style.display = "";

		        if (bStart && Number.isFinite(nSec))
		        {
		            StartTimer(kItem, Math.min(TIMER_SEC_MAX, Math.max(TIMER_SEC_MIN, nSec)));
		        }
		    };

		    kInput.addEventListener("keydown", (kEvent) =>	// Enter: 시작, Esc: 취소 (명령 창 Enter 실행으로 안 넘김)
		    {
		        if (kEvent.key === "Enter" && !kEvent.isComposing)
		        {
		            kEvent.preventDefault();
		            kEvent.stopPropagation();

		            funcClose(true);
		        }
		        else if (kEvent.key === "Escape")
		        {
		            kEvent.preventDefault();
		            kEvent.stopPropagation();

		            funcClose(false);
		        }
		    });

		    kInput.addEventListener("blur", () => funcClose(false));	// 밖 누름: 취소

		    kTimerButton.style.display = "none";
		    kTimerButton.before(kInput);

		    kInput.focus();
		    kInput.select();
		}

		function StartTimer(kItem, nSec)	// 타이머 시작 (서치·쿼리 따로): S·E 를 지금 + 간격으로 맞춤 → 바로 1회 실행 → 초 줄여 감 (1초마다, 시각 기준 → 백그라운드 탭에서도 맞음), 0: 존 색 1회 반짝 + S·E 지난 시간만큼 밀고 실행 → 다시
		{
		    const	kFrom	= Query.GetPicker(kStart, kStartHour, kStartMinute);	// 지금 명령 창 기간 (간격: D 없음일 때)
		    const	kTo		= Query.GetPicker(kEnd, kEndHour, kEndMinute);
		    const	kNow	= new Date();	// 지금 (초 단위)

		    kNow.setMilliseconds(0);

		    Query.SetPadRange(kNow, kQueryRange.selectedIndex > 0 ? Number(kQueryRange.value) : Math.max(1000, (kTo ?? kNow) - (kFrom ?? kNow)) / 60000);	// E = 지금 + 여유, S = E - 간격 (D 있음: D, 없음: 지금 S·E 길이, 최소 X초)

		    kItem.nSec	= nSec;
		    kItem.nDue	= Date.now() + nSec * 1000;
		    kItem.nId	= setInterval(() => OnTimerTick(kItem), 1000);
		    kItem.kFrom	= Query.GetPicker(kStart, kStartHour, kStartMinute);	// 기준 기간 (명령 창)
		    kItem.kTo	= Query.GetPicker(kEnd, kEndHour, kEndMinute);
		    kItem.nLast	= Date.now();	// 기준 시각 (다음 실행: 여기부터 지난 시간만큼 밂)
		    kItem.sPick	= GetPickText();

		    FireTimer(kItem);	// 최초 1회 실행 (맞춘 기간 그대로: 지난 시간 0)

		    ShowTimer();
		}

		function FireTimer(kItem)	// 타이머 실행 1회 (시작·0 공통): 존 색 1회 반짝 + 실행 (실행 중: 건너뜀, 밀지 않음 → 다음 실행이 이어서)
		{
		    kTimerButton.classList.remove("flash");
		    void kTimerButton.offsetWidth;	// 반짝 처음부터 다시 (애니메이션 재시작)
		    kTimerButton.classList.add("flash");

		    if (!kItem.kRun.disabled)
		    {
		        RunTimer(kItem);
		    }
		}

		function GetPickText()	// 명령 창 S·E 값 (타이머: 사용자가 바꿨는지 비교)
		{
		    return [kStart.value, kStartHour.value, kStartMinute.value, kEnd.value, kEndHour.value, kEndMinute.value, kStartSecond.value, kEndSecond.value].join(" ");
		}

		function RunTimer(kItem)	// 타이머 실행: S·E 를 지난 시간 (마지막 실행부터, 초) 만큼 함께 밀고 그 기간으로 실행 → 앞 기간과 빈틈없이 이어짐 (D 다시 계산 안 함, 사용자가 S·E·D 바꿈: 그 값부터, 쿼리·서치: 초까지)
		{
		    if (kItem === kTimer.kSearch && kKey.value.trim() === "")	// 서치 검색어 없음: 건너뜀 (알림 반복 방지, 밀지 않음 → 다음 실행이 이어서)
		    {
		        return;
		    }

		    if (GetPickText() !== kItem.sPick)	// 사용자가 S·E·D 바꿈: 명령 창 값부터
		    {
		        kItem.kFrom	= Query.GetPicker(kStart, kStartHour, kStartMinute);
		        kItem.kTo	= Query.GetPicker(kEnd, kEndHour, kEndMinute);
		    }

		    const	nShift	= Math.round((Date.now() - kItem.nLast) / 1000) * 1000;	// 지난 시간 (초 단위, 건너뛴 주기 포함 → 밀림 없이 따라감)

		    kItem.nLast	+= nShift;
		    kItem.kFrom	= new Date(kItem.kFrom.getTime() + nShift);
		    kItem.kTo	= new Date(kItem.kTo.getTime() + nShift);

		    Query.SetPicker(kStart, kStartHour, kStartMinute, kItem.kFrom);	// 표시: 초까지
		    Query.SetPicker(kEnd, kEndHour, kEndMinute, kItem.kTo);

		    kItem.sPick = GetPickText();

		    if (kItem === kTimer.kQuery)
		    {
		        Query.Query(kQueryKey.value.trim() || "*", kItem.kFrom, kItem.kTo, true, true);	// 쿼리 버튼과 같음 (초 있음: 초 기간)
		    }
		    else
		    {
		        Search.SearchLog(false, false, true);	// 서치 버튼과 같음 (명령 창 기간 그대로)
		    }
		}

		function OnTimerTick(kItem)	// 1초마다: 0 → 그 탭 보는 중만 반짝 + 실행 (다른 탭·덤프·파일 보는 중: 건너뜀 → 화면 안 바뀜) → 다음 주기, 지금 탭 타이머: 남은 초 표시
		{
		    if (Date.now() >= kItem.nDue)
		    {
		        if (kItem === GetTimer())	// 그 탭 보는 중만
		        {
		            FireTimer(kItem);
		        }

		        kItem.nDue = Date.now() + kItem.nSec * 1000;	// 다시 (루프)
		    }

		    if (kItem === GetTimer())
		    {
		        ShowTimer();
		    }
		}

		function StopTimer(kItem)	// 타이머 중지 (그 탭만): 버튼 주기 초 표시로
		{
		    clearInterval(kItem.nId);

		    kItem.nId = 0;

		    kTimerButton.classList.remove("flash");

		    ShowTimer();
		}

		function ShowTimer()	// 타이머 버튼 글자: 지금 탭 타이머 (숫자만, 도는 중: 남은 초·존 색 글자, 아님: 주기 초)
		{
		    const	kItem	= GetTimer();

		    if (kItem == null)
		    {
		        return;
		    }

		    const	bOn	= kItem.nId !== 0;

		    kTimerButton.classList.toggle("on", bOn);
		    kTimerButton.textContent	= String(bOn ? Math.max(0, Math.ceil((kItem.nDue - Date.now()) / 1000)) : kItem.nSec);
		}

		function TrimLines(kBody, kState, nMax, bTop)	// 줄 보기 최대 줄 수 넘음: 반대쪽 줄 지움 (bTop: 위쪽, 빈 높이로 바뀌어 위치 유지)
		{
		    for (let nOver = kState.nLast - kState.nFirst + 1 - nMax; nOver > 0; --nOver)
		    {
		        if (bTop)
		        {
		            kBody.firstElementChild?.remove();
		            ++kState.nFirst;
		        }
		        else
		        {
		            kBody.lastElementChild?.remove();
		            --kState.nLast;
		        }
		    }
		}

		function FillLines(kScroll, kBody, kState, nCount, funcLoad)	// 줄 보기 스크롤: 빈 높이 근처면 funcLoad(시작 줄, 줄 수, 방향) (가까움: 위 -1·아래 1 이어 읽기, 멂: 그 위치로 바꿈 2)
		{
		    const	arrRange	= GetLineRange(kScroll, kBody);

		    if (kState == null || kState.bLoading || kState.bError || kState.nFirst === 0 || kState.nTotal === 0 || arrRange == null)
		    {
		        return;
		    }

		    const [nTop, nBottom] = arrRange;

		    if (nBottom < kState.nFirst - nCount || nTop > kState.nLast + nCount)	// 멂: 그 위치로 바꿈
		    {
		        const	nStart	= Math.max(1, nTop - nCount);

		        funcLoad(nStart, Math.min(kState.nTotal, nBottom + nCount) - nStart + 1, 2);
		    }
		    else if (kState.nFirst > 1 && nTop - LINE_NEAR < kState.nFirst)	// 위 가까움: 앞 줄
		    {
		        const	nStart	= Math.max(1, Math.min(kState.nFirst - nCount, nTop - LINE_NEAR));

		        funcLoad(nStart, kState.nFirst - nStart, -1);
		    }
		    else if (kState.nLast < kState.nTotal && nBottom + LINE_NEAR > kState.nLast)	// 아래 가까움: 뒤 줄
		    {
		        funcLoad(kState.nLast + 1, Math.max(nCount, nBottom + LINE_NEAR - kState.nLast), 1);
		    }
		}

		function GetRestColumn(kTable)	// 나머지 너비 칸 번호 (data-rest, 없음: 마지막 칸)
		{
		    return Number(kTable.dataset.rest ?? kTable.tHead.rows[0].cells.length - 1);
		}

		function FixColumn(kTable)	// 칸 너비 고정 (처음 드래그 놓을 때 자동 너비 → px, 나머지 너비 칸: 표 오른쪽 끝 고정)
		{
		    if (kTable.classList.contains("fixed"))
		    {
		        return;
		    }

		    const	arrCol		= kTable.querySelectorAll("col");
		    const	nRest		= GetRestColumn(kTable);
		    const	arrWidth	= [...kTable.tHead.rows[0].cells].map(kHead => kHead.getBoundingClientRect().width);	// 먼저 모두 읽음 (쓰기 사이 읽기: 칸마다 다시 배치)

		    arrWidth.forEach((nWidth, nIndex) =>
		    {
		        if (nIndex !== nRest)
		        {
		            arrCol[nIndex].style.width = nWidth + "px";
		        }
		    });

		    SetFixed(kTable, true);
		}

		function ResetColumn(kTable)	// 칸 너비 자동으로 되돌림 (칸 경계 더블 클릭)
		{
		    for (const kCol of kTable.querySelectorAll("col"))
		    {
		        kCol.style.width = "";
		    }

		    SetFixed(kTable, false);
		}

		function SetFixed(kTable, bFixed)	// 칸 너비 고정 켜기·끄기: tbody 떼고 바꾼 뒤 다시 붙임 (붙은 채: 줄 수 제곱으로 느림)
		{
		    const	kBody	= kTable.tBodies[0];	// 결과 줄 (표 맨 끝)

		    kBody.remove();
		    kTable.classList.toggle("fixed", bFixed);
		    kTable.append(kBody);
		}

		function GetColumnMin(kHead)	// 칸 최소 너비: 제목 첫 글자 + 왼쪽 여백·테두리
		{
		    const	kStyle	= getComputedStyle(kHead);
		    const	kRange	= document.createRange();

		    kRange.setStart(kHead.firstChild, 0);
		    kRange.setEnd(kHead.firstChild, 1);

		    return Math.ceil(kRange.getBoundingClientRect().width + parseFloat(kStyle.paddingLeft) + parseFloat(kStyle.borderLeftWidth) + parseFloat(kStyle.borderRightWidth));
		}

		async function LoadMachine()	// 접속 머신 목록 조회 (바뀐 경우만 다시 그림 → 체크·열린 드롭 박스 유지)
		{
		    if (bMachineLoading)
		    {
		        return;
		    }

		    bMachineLoading = true;

		    try
		    {
		        const	kData		= await FetchJson("/api/client", { cache: "no-store" });
		        const	arrMachine	= [...new Set(kData.arrClient.map(kClient => kClient.sMachine).filter(sMachine => sMachine !== ""))].sort();
		        const	arrOption	= arrMachine.map(sMachine => [sMachine, sMachine]);	// [값, 표시]

		        for (const sCheck of setMachineCheck)	// 체크 머신 끊김: 체크 유지
		        {
		            if (!arrMachine.includes(sCheck))
		            {
		                arrOption.push([sCheck, `${sCheck} (접속 끊김)`]);
		            }
		        }

		        const	sSignature	= JSON.stringify(arrOption);

		        if (sSignature !== sMachineSignature)
		        {
		            arrMachineList		= arrOption.map(([sValue]) => sValue);
		            arrMachineOption	= arrOption;
		            sMachineSignature	= sSignature;

		            if (!IsQueryMode())	// 쿼리 모드: 쿼리 목록 그대로 (서치로 돌아올 때 다시 그림)
		            {
		                RenderMachine(arrOption);
		            }
		        }

		        nMachineCount = arrMachine.length;
		    }
		    catch (kError)	// 조회 실패: 기존 목록 유지
		    {
		        console.error(kError);
		    }
		    finally
		    {
		        bMachineLoading = false;
		    }

		    ShowStatus();
		}

		function GetMachines()	// 대상 머신 (체크 없음: 전체, 목록 순서)
		{
		    return setMachineCheck.size === 0 ? arrMachineList : arrMachineList.filter(sMachine => setMachineCheck.has(sMachine));
		}

		function GetMachineText()	// 머신 표시 글자 (전체 · 1대: 이름 · 여러 대: 첫 머신 외 N대)
		{
		    const	arrCheck	= arrMachineList.filter(sMachine => setMachineCheck.has(sMachine));

		    return arrCheck.length === 0 ? "전체" : (arrCheck.length === 1 ? arrCheck[0] : `${arrCheck[0]} 외 ${arrCheck.length - 1}대`);
		}

		function RenderMachine(arrOption, setCheck = setMachineCheck)	// 드롭 박스 목록 다시 그림: 왼쪽 체크 박스 + 이름 (끊긴 체크 머신: '(접속 끊김)', 글자는 textContent 로만, setCheck: 체크 (쿼리 모드: 쿼리칸 머신 조건))
		{
		    kMachineList.replaceChildren(...arrOption.map(([sValue, sLabel]) =>
		    {
		        const	kLabel	= document.createElement("label");
		        const	kCheck	= document.createElement("input");
		        const	kName	= document.createElement("span");

		        kCheck.type			= "checkbox";
		        kCheck.value		= sValue;
		        kCheck.checked		= setCheck.has(sValue);
		        kName.textContent	= sLabel;

		        kLabel.append(kCheck, kName);

		        return kLabel;
		    }));

		    ShowMachine();
		}

		function ShowMachine()	// 체크 상태 → 드롭 박스 글자·전체 체크 (체크 없음: 전체, 하위 체크: 선택, 쿼리 모드: 쿼리칸 머신 조건) + 상태 줄
		{
		    if (IsQueryMode())
		    {
		        const	bCheck	= GetQueryMachine()[0].length > 0;	// 쿼리칸 머신 조건 있음

		        kMachineBox.textContent	= bCheck ? "선택" : "전체";
		        kMachineAll.checked		= !bCheck;
		    }
		    else
		    {
		        kMachineBox.textContent	= arrMachineList.some(sMachine => setMachineCheck.has(sMachine)) ? "선택" : "전체";	// 머신 이름은 펼친 목록에서
		        kMachineAll.checked		= setMachineCheck.size === 0;
		    }

		    ShowStatus();
		}

		function OnMachineBox()	// 드롭 박스 클릭: 목록 펼침·접음 (펼칠 때 머신 다시 조회, 쿼리 모드: 색인 머신)
		{
		    kMachineMenu.hidden = !kMachineMenu.hidden;
		    kMachineBox.setAttribute("aria-expanded", String(!kMachineMenu.hidden));

		    if (!kMachineMenu.hidden)
		    {
		        if (IsQueryMode())
		        {
		            RenderQueryMachine();
		            LoadQueryMachine();
		        }
		        else
		        {
		            LoadMachine();
		        }
		    }
		}

		function OnMachineCheck(kEvent)	// 머신 체크 → 고른 머신 추가·제외 (모두 해제: 전체, 쿼리 모드: 쿼리칸 머신 조건 바꿈)
		{
		    if (IsQueryMode())
		    {
		        SetQueryMachine([...kMachineList.querySelectorAll("input:checked")].map(kCheck => kCheck.value));

		        return;
		    }

		    if (kEvent.target.checked)
		    {
		        setMachineCheck.add(kEvent.target.value);
		    }
		    else
		    {
		        setMachineCheck.delete(kEvent.target.value);
		    }

		    ShowMachine();
		}

		function OnMachineAll()	// 전체 체크 → 고른 머신 모두 해제 (전체 해제: 그대로 전체, 쿼리 모드: 쿼리칸 머신 조건 뺌 → 기본 빈칸)
		{
		    if (IsQueryMode())
		    {
		        SetQueryMachine([]);

		        return;
		    }

		    setMachineCheck.clear();

		    for (const kCheck of kMachineList.querySelectorAll("input"))
		    {
		        kCheck.checked = false;
		    }

		    ShowMachine();
		}

		function OnMachineOutside(kEvent)	// 드롭 박스 밖 클릭, Esc: 목록 접음
		{
		    if (kMachineMenu.hidden || (kEvent.type === "click" && kMachinePick.contains(kEvent.target)) || (kEvent.type === "keydown" && kEvent.key !== "Escape"))
		    {
		        return;
		    }

		    kMachineMenu.hidden = true;
		    kMachineBox.setAttribute("aria-expanded", "false");
		}

		function IsQueryMode()	// 명령 창 쿼리 모드 (M: 쿼리칸 머신 조건)
		{
		    return kFilterPanel.classList.contains("query");
		}

		async function LoadQueryMachine()	// 쿼리 머신 목록 조회 (색인된 머신, 실패: 기존 목록 유지) → 쿼리 모드: 다시 그림
		{
		    try
		    {
		        arrQueryMachine = (await FetchJson("/api/query/machine", { cache: "no-store" })).arrMachine;
		    }
		    catch (kError)
		    {
		        console.error(kError);
		    }

		    if (IsQueryMode())
		    {
		        RenderQueryMachine();
		    }
		}

		function RenderQueryMachine()	// 쿼리 모드 드롭 박스 목록: 색인·접속·쿼리칸 머신 (이름순, 체크: 쿼리칸 머신 조건)
		{
		    const [arrCheck]	= GetQueryMachine();
		    const	arrMachine	= [...new Set([...arrQueryMachine, ...arrMachineList, ...arrCheck])].sort();

		    RenderMachine(arrMachine.map(sMachine => [sMachine, sMachine]), new Set(arrCheck));
		}

		function RenderMachineMode()	// 쿼리 모드 들어감·나감: 드롭 박스 목록 바꿔 그림 (쿼리: 쿼리칸 머신 조건, 서치: 접속 머신 체크)
		{
		    if (IsQueryMode())
		    {
		        RenderQueryMachine();
		        LoadQueryMachine();

		        return;
		    }

		    RenderMachine(arrMachineOption);
		}

		function GetQueryMachine()	// 쿼리칸 앞 머신 조건 → [머신 목록, 나머지 쿼리] (없음: [[], 쿼리칸])
		{
		    const	sQuery	= kQueryKey.value;
		    const	kMatch	= sQuery.match(QUERY_MACHINE_REGEX);

		    if (kMatch == null)
		    {
		        return [[], sQuery.trim()];
		    }

		    return [(kMatch[1] ?? kMatch[2]).split(/\s+OR\s+/).map(sItem => sItem.slice("Machine:".length)), sQuery.slice(kMatch[0].length).trim()];
		}

		function SetQueryMachine(arrMachine)	// 쿼리칸 앞 머신 조건 바꿈 (없음: 뺌 → 전체 기본 빈칸, 1대: Machine:A, 여러 대: (Machine:A OR Machine:B), 나머지 쿼리: AND 로 이음)
		{
		    const [, sRest]	= GetQueryMachine();
		    const sMachine	= arrMachine.length === 0 ? "" : (arrMachine.length === 1 ? `Machine:${arrMachine[0]}` : `(${arrMachine.map(sItem => `Machine:${sItem}`).join(" OR ")})`);

		    kQueryKey.value = sMachine === "" ? sRest : (sRest === "" ? sMachine : `${sMachine} AND ${WrapQuery(sRest)}`);

		    Query.OnQueryInput();	// 저장 쿼리 선택 해제·M 맞춤
		}

		function WrapQuery(sQuery)	// 나머지 쿼리 괄호 (여러 단어: 왼쪽부터 규칙에도 머신 조건이 전체에 걸림, 한 단어·통째 괄호: 그대로)
		{
		    if (!/\s/.test(sQuery))
		    {
		        return sQuery;
		    }

		    let	nDepth	= 0;	// 괄호 깊이

		    for (let nIndex = 0; nIndex < sQuery.length - 1; ++nIndex)
		    {
		        nDepth += sQuery[nIndex] === "(" ? 1 : (sQuery[nIndex] === ")" ? -1 : 0);

		        if (nDepth <= 0)	// 끝 전에 괄호 밖: 통째 괄호 아님
		        {
		            return `(${sQuery})`;
		        }
		    }

		    return sQuery;
		}

		function SyncQueryMachine()	// 쿼리칸 바뀜: 쿼리 모드 드롭 박스 글자·체크 맞춤 (목록 펼침: 다시 그림)
		{
		    if (!IsQueryMode())
		    {
		        return;
		    }

		    if (!kMachineMenu.hidden)
		    {
		        RenderQueryMachine();

		        return;
		    }

		    ShowMachine();
		}


		function OpenWindow(sUrl, nWidth, nHeight, bCenterWindow = false)	// 새 창: 크기 지정 + 모니터 화면 중앙 (bCenterWindow: 브라우저 창 중앙, sOpenMode browser: 같은 크기·자리, tab: 새 탭)
		{
		    if (sOpenMode === "tab")	// Alt: 새 탭 (클릭 이벤트 끝난 뒤 열기)
		    {
		        setTimeout(() => window.open(sUrl, "_blank"));

		        return;
		    }

		    if (sOpenMode === "browser")	// Ctrl: 같은 바깥 크기·자리 (open 크기: 안쪽 → 열고 바깥 크기로 맞춤)
		    {
		        const	nOuterWidth		= window.outerWidth;	// 누른 때 바깥 크기·자리
		        const	nOuterHeight	= window.outerHeight;
		        const	nScreenX		= window.screenX;
		        const	nScreenY		= window.screenY;
		        const sInner		= `width=${window.innerWidth},height=${window.innerHeight},left=${nScreenX},top=${nScreenY}`;	// 안쪽 내용 크기 (같은 화면이면 바깥도 거의 같음)

		        setTimeout(() =>	// 클릭 이벤트 끝난 뒤 열기 (이벤트 안: Chrome 이 눌린 Ctrl 보고 뒤 탭으로 엶, 클릭 직후: 팝업 허용 유지)
		        {
		            const	kWindow	= window.open(sUrl, "_blank", sInner);

		            try
		            {
		                kWindow?.resizeTo(nOuterWidth, nOuterHeight);	// 바깥 크기 맞춤 (탭·주소 줄 차이)
		                kWindow?.moveTo(nScreenX, nScreenY);
		            }
		            catch (kError)	// 크기 바꾸기 막힘: 안쪽 크기 그대로
		            {
		                console.error(kError);
		            }
		        });

		        return;
		    }

		    const	nLeft	= Math.round(bCenterWindow ? window.screenX + Math.max(0, window.outerWidth - nWidth) / 2 : (screen.availLeft || 0) + Math.max(0, screen.availWidth - nWidth) / 2);
		    const	nTop	= Math.round(bCenterWindow ? window.screenY + Math.max(0, window.outerHeight - nHeight) / 2 : (screen.availTop || 0) + Math.max(0, screen.availHeight - nHeight) / 2);

		    window.open(sUrl, "_blank", `width=${nWidth},height=${nHeight},left=${nLeft},top=${nTop}`);
		}

		function GetOpenTarget(kEvent)	// Ctrl·Alt+클릭 새 창 대상 → { kTarget, sKind: fileview (파일 보기 제목)·self (원래 새 창)·file (파일명)·link·state (그 외 UI) } (null: 원래 동작)
		{
		    if (kEvent.button !== 0 || kEvent.ctrlKey === kEvent.altKey || kEvent.target.closest?.(`${OPEN_WORD}, dialog, ${OPEN_SKIP}`) != null)
		    {
		        return null;
		    }

		    const	kTarget	= kEvent.target.closest?.(OPEN_TARGET) ?? null;	// 누른 UI (제목란 안 버튼 포함: 그 버튼)

		    if (kTarget == null)	// UI 아님: 하위 창 제목란 (제목 글자·빈 곳)
		    {
		        const	kTitle	= kEvent.target.closest?.(OPEN_TITLE) ?? null;	// 누른 하위 창 제목란

		        if (kTitle == null)
		        {
		            return null;
		        }

		        if (kEvent.target.closest("#FileViewTitle") != null && File.kFileView != null)	// 파일 1번 창 파일 보기 제목: 보는 파일 (마지막 줄부터)
		        {
		            return { kTarget: kTitle, sKind: "fileview", kView: { sMachine: File.kFileView.sMachine, sPath: File.kFileView.sPath, nTarget: 0, sKey: "" } };
		        }

		        if (kEvent.target.closest("#ViewPanel > .filter") != null && Search.kView != null)	// 서치 2번 창 제목: 보는 파일 (그 줄 강조)
		        {
		            return { kTarget: kTitle, sKind: "fileview", kView: Search.kView };
		        }

		        return document.body.dataset.mode === "main" ? { kTarget: kTitle, sKind: "state" } : null;	// 지금 상태 첫 화면 (보는 탭·입력칸, 쿼리 결과 보던 중: 다시 쿼리, 새 창 모드: 원래 동작)
		    }

		    if (kTarget.matches(OPEN_SELF))
		    {
		        return { kTarget, sKind: "self" };
		    }

		    if (kTarget.matches("td.filename, #FileTree div.node.view"))
		    {
		        return { kTarget, sKind: "file" };
		    }

		    if (kTarget.matches("a[href]"))
		    {
		        return { kTarget, sKind: "link" };
		    }

		    return document.body.dataset.mode === "main" ? { kTarget, sKind: "state" } : null;	// 새 창 모드: 지금 상태 복원 없음 → 원래 동작
		}

		function OnOpenMouseDown(kEvent)	// Ctrl·Alt+누름 (새 창 대상): 포커스·드롭 박스 펼침·글자 선택 막음
		{
		    if (GetOpenTarget(kEvent) != null)
		    {
		        kEvent.preventDefault();
		    }
		}

		function OnOpenClick(kEvent)	// Ctrl+클릭: 새 창, Alt+클릭: 새 탭 (지금 창 그대로)
		{
		    const	kOpen	= GetOpenTarget(kEvent);	// 대상 (null: 원래 동작)

		    if (kOpen == null)
		    {
		        return;
		    }

		    if (kOpen.sKind === "self")	// 원래 새 창 여는 것 (필드 이름): 그 동작 그대로
		    {
		        sOpenMode = kEvent.altKey ? "tab" : "browser";

		        setTimeout(() => sOpenMode = "");	// 원래 클릭 처리 뒤 되돌림

		        return;
		    }

		    kEvent.preventDefault();
		    kEvent.stopPropagation();	// 원래 클릭 처리 안 함 (지금 창 그대로)

		    sOpenMode = kEvent.altKey ? "tab" : "browser";	// Ctrl: 새 창, Alt: 새 탭

		    try
		    {
		        if (kOpen.sKind === "fileview")	// 파일 보기 제목: 보는 파일
		        {
		            OpenWindow("/view?" + new URLSearchParams({ sMachine: kOpen.kView.sMachine, sPath: kOpen.kView.sPath, nTarget: String(kOpen.kView.nTarget ?? 0), sKey: kOpen.kView.sKey ?? "" }), 1000, 900, true);
		        }
		        else if (kOpen.sKind === "file")
		        {
		            OpenWindow(GetOpenFileUrl(kOpen.kTarget), 1000, 900, true);	// 파일 보기 (브라우저 창 중앙)
		        }
		        else if (kOpen.sKind === "link")
		        {
		            OpenWindow(kOpen.kTarget.href, 1600, 1000);
		        }
		        else
		        {
		            OpenWindow(GetStateUrl(kOpen.kTarget), 1600, 1000);
		        }
		    }
		    finally
		    {
		        sOpenMode = "";
		    }
		}

		function GetOpenFileUrl(kTarget)	// 파일명 → 파일 보기 주소 (결과 줄 File 칸: 그 줄 강조, 파일 트리: 마지막 줄부터)
		{
		    return "/view?" + new URLSearchParams({ sMachine: kTarget.dataset.machine, sPath: kTarget.dataset.path, nTarget: kTarget.dataset.line ?? "0", sKey: kTarget.dataset.key ?? "" });
		}

		function GetStateUrl(kTarget)	// 지금 상태 첫 화면 주소 (탭·머신 체크·입력칸·쿼리 결과 + 누른 UI id → 새 창 RestoreState)
		{
		    const	kControl	= kTarget.matches("label") ? kTarget.control : kTarget;	// 라벨: 그 입력칸
		    const	kParam		= new URLSearchParams({ sTab: document.querySelector("#TabBar button.cur")?.id ?? kQueryButton.id, sMachine: [...setMachineCheck].join(","), bQuery: Query.sQueryKey !== "" ? "1" : "0", sClick: kControl?.id ?? "" });

		    for (const sId of OPEN_STATE_INPUT)
		    {
		        kParam.set(sId, document.getElementById(sId).value);
		    }

		    return location.pathname + "?" + kParam;
		}

		function GetOpenerState()	// 연 창의 다른 탭 기간·← 단계 →·단계 결과 (복사본, 연 창 없음·접근 막힘: null)
		{
		    try
		    {
		        const	kOpener	= window.opener;	// 연 창 (같은 출처)

		        if (kOpener == null || kOpener.closed || kOpener.Query == null || kOpener.Search == null)
		        {
		            return null;
		        }

		        return structuredClone({ kOtherRange: kOpener.Query.kOtherRange, arrQueryHistory: kOpener.Query.arrHistory, arrQueryResult: kOpener.Query.arrHistory.map(kStep => kOpener.Query.mapStepResult?.get(kStep) ?? null), nQueryHistory: kOpener.Query.nHistory, arrSearchHistory: kOpener.Search.arrHistory, arrSearchResult: kOpener.Search.arrHistory.map(kStep => kOpener.Search.mapStepResult?.get(kStep) ?? null), nSearchHistory: kOpener.Search.nHistory });
		    }
		    catch (kError)	// 연 창 접근 불가
		    {
		        console.error(kError);

		        return null;
		    }
		}

		function GetPeriodPick()	// 지금 명령 창 기간 [S 날짜·시·분, E 날짜·시·분] (D 최근 기간: 지금 기준으로 다시 계산)
		{
		    if (kQueryRange.selectedIndex > 0)
		    {
		        Query.SetRecent();
		    }

		    return [kStart.value, kStartHour.value, kStartMinute.value, kEnd.value, kEndHour.value, kEndMinute.value, kStartSecond.value, kEndSecond.value];
		}

		function SetPeriodPick(arrPick)	// 명령 창 기간 넣기 (쿼리 D 해제 → S·E 그대로 쿼리)
		{
		    [kStart.value, kStartHour.value, kStartMinute.value, kEnd.value, kEndHour.value, kEndMinute.value, kStartSecond.value, kEndSecond.value] = arrPick;

		    kQueryRange.selectedIndex = 0;
		}

		function RestoreState(kParam)	// Ctrl·Alt+클릭 새 창 첫 화면 복원 (머신 → 탭 → 입력칸 → 연 창 기간·단계·결과 → 결과 그대로 (없음: 쿼리 다시, 서치 다시 검색 안 함 (실서버 부하)) → 누른 버튼·포커스)
		{
		    const	sTab	= kParam.get("sTab") ?? "";
		    const	kClick	= document.getElementById(kParam.get("sClick") ?? "");	// 누른 UI (없음: null)
		    const	bButton	= kClick?.matches("button") === true;

		    history.replaceState(null, "", location.pathname);	// 새로 고침: 처음 화면 (다시 누르지 않음)

		    for (const sMachine of (kParam.get("sMachine") ?? "").split(",").filter(sItem => arrMachineList.includes(sItem)))	// 접속 안 된 머신: 제외 (모두 없으면 전체)
		    {
		        setMachineCheck.add(sMachine);
		    }

		    for (const kCheck of kMachineList.querySelectorAll("input[type=checkbox]"))
		    {
		        kCheck.checked = setMachineCheck.has(kCheck.value);
		    }

		    ShowMachine();

		    if (sTab !== kQueryButton.id && document.querySelector(`#TabBar #${CSS.escape(sTab)}:not([hidden])`) != null)	// 쿼리 외 보이는 탭 (쿼리: 처음부터 보는 탭, 숨은 탭: 계정 메뉴·aMenu 밖 → 안 엶)
		    {
		        document.getElementById(sTab).click();
		    }

		    for (const sId of OPEN_STATE_INPUT)	// 탭 바꾼 뒤 (서치·쿼리 기간 따로 보관 → 바꿀 때 바뀜)
		    {
		        if (kParam.has(sId))
		        {
		            document.getElementById(sId).value = kParam.get(sId);
		        }
		    }

		    const	kOpener	= GetOpenerState();	// 연 창 다른 탭 기간·← 단계 → (못 가져옴: null → 입력칸만)

		    if (kOpener != null)
		    {
		        Query.kOtherRange	= kOpener.kOtherRange;	// 다른 탭 (서치 ↔ 쿼리) 기간: 탭 바꿀 때 꺼냄
		        Query.arrHistory	= kOpener.arrQueryHistory;
		        Query.nHistory	= kOpener.nQueryHistory;
		        Search.arrHistory	= kOpener.arrSearchHistory;
		        Search.nHistory	= kOpener.nSearchHistory;

		        kOpener.arrQueryResult.forEach((kResult, nIndex) =>	// 쿼리 단계 결과 (지금 단계·← →: 다시 쿼리 안 함)
		        {
		            if (kResult != null && Query.arrHistory[nIndex] != null)
		            {
		                Query.mapStepResult.set(Query.arrHistory[nIndex], kResult);
		            }
		        });

		        kOpener.arrSearchResult.forEach((kResult, nIndex) =>	// 서치 단계 결과 (지금 단계·← →: 다시 검색 안 함)
		        {
		            if (kResult != null && Search.arrHistory[nIndex] != null)
		            {
		                Search.SaveStepResult(Search.arrHistory[nIndex], kResult);
		            }
		        });

		        Query.ShowHistory();
		    }

		    if (kParam.get("bMove") === "1")	// Shift 옮겨 실행: 다른 탭도 같은 기간 (돌아가도 이 기간)
		    {
		        Query.kOtherRange = { sStart: kStart.value, sStartHour: kStartHour.value, sStartMinute: kStartMinute.value, sStartSecond: kStartSecond.value, sEnd: kEnd.value, sEndHour: kEndHour.value, sEndMinute: kEndMinute.value, sEndSecond: kEndSecond.value, nRange: 0 };
		    }

		    ShowStatus();

		    if (sTab === kQueryButton.id && kParam.get("bQuery") === "1")	// 쿼리 결과 보던 중: 그 결과 그대로 (결과 없음·버튼 아님: 같은 조건으로 다시)
		    {
		        const	kStep	= Query.arrHistory[Query.nHistory];	// 연 창 지금 단계 (없음: undefined)
		        const	kResult	= kStep === undefined ? undefined : Query.mapStepResult.get(kStep);	// 그 결과 (연 창에서 복사)

		        if (kResult?.kPage != null)
		        {
		            Query.ShowStep(kStep, kResult);
		        }
		        else if (!bButton && kStep !== undefined)
		        {
		            Query.GoHistory(0);
		        }
		        else if (!bButton)
		        {
		            Query.RunQuery();
		        }
		    }

		    const	kSearchResult	= Search.mapStepResult.get(Search.arrHistory[Search.nHistory]);	// 연 창 지금 서치 단계 결과 (없음: undefined)

		    if (sTab === kLogButton.id && kSearchResult !== undefined)	// 서치 결과 보던 중: 그 결과 그대로 (없음: 다시 검색 안 함)
		    {
		        Search.ShowStep(kSearchResult);
		    }

		    if (bButton && kClick.id !== sTab && !kClick.disabled)
		    {
		        kClick.click();
		    }
		    else if (!bButton)
		    {
		        kClick?.focus();
		    }
		}

		function GetShiftMove(kEvent)	// Shift+클릭 쿼리 ↔ 서치 옮겨 실행 대상 → { sKey, bToSearch, bRun (false: 명령·기간 복사만) } (첫 화면만, null: 원래 동작)
		{
		    if (kEvent.button !== 0 || !kEvent.shiftKey || (kEvent.ctrlKey && kEvent.altKey) || document.body.dataset.mode !== "main" || kEvent.target.closest?.("dialog") != null)
		    {
		        return null;
		    }

		    if (kEvent.target === kQueryKey || kEvent.target === kKey)	// 입력칸: 입력한 명령 (쿼리칸 → 서치, 검색어 → 쿼리)
		    {
		        return { sKey: kEvent.target.value.trim(), bToSearch: kEvent.target === kQueryKey, bRun: false };	// 입력칸: 명령·기간 복사만 (실행 안 함)
		    }

		    const	kRun	= kEvent.target.closest?.("#QueryRunButton, #SearchButton");	// 실행 버튼: 그 탭 입력한 명령 (쿼리 실행 → 서치, 서치 실행 → 쿼리)

		    if (kRun != null)
		    {
		        return kRun === kQueryRunButton ? { sKey: kQueryKey.value.trim(), bToSearch: true, bRun: true } : { sKey: kKey.value.trim(), bToSearch: false, bRun: true };
		    }

		    const	kField	= kEvent.target.closest?.("#QueryBody .queryfield span[data-field]");	// 펼친 줄 필드 값: 값만 서치

		    if (kField != null)
		    {
		        return { sKey: kField.dataset.value, bToSearch: true, bRun: true };
		    }

		    const	bQuery	= kEvent.target.closest?.(SHIFT_QUERY_WORD) != null;	// 쿼리 단어 (아님: 서치 단어)

		    if (!bQuery && kEvent.target.closest?.(Search.WORD_BOX) == null)
		    {
		        return null;
		    }

		    let	sWord	= bQuery ? Query.WordAt(kEvent.clientX, kEvent.clientY, "td.log, .querytext", true) : Query.WordAt(kEvent.clientX, kEvent.clientY, Search.WORD_BOX, false);	// 누른 단어 (Ctrl·Alt 단어 클릭과 같음)

		    if (!/[^-_]/.test(sWord))	// 단어 없음 (-·_ 만)
		    {
		        return null;
		    }

		    if (/^(and|or|not)$/i.test(sWord))	// 연산자 글자 (대소문자 무시 → 검색어로 못 넣음): 안 함
		    {
		        return null;
		    }

		    return { sKey: sWord, bToSearch: bQuery, bRun: true };
		}

		function OnShiftMouseDown(kEvent)	// Shift+누름 (쿼리 ↔ 서치 대상): 다른 탭 입력칸에 명령·기간 옮김 (Shift+Ctrl: 새 창, Shift+Alt: 새 탭), 포커스·선택·새 창·단어 추가 막음
		{
		    bShiftClick = false;

		    const	kMove	= GetShiftMove(kEvent);	// 대상 (null: 원래 동작)

		    if (kMove == null)
		    {
		        return;
		    }

		    kEvent.preventDefault();
		    kEvent.stopImmediatePropagation();	// Ctrl·Alt 새 창·단어 추가 안 함

		    bShiftClick = true;

		    if (kMove.sKey === "" || Search.bSearching || (!kMove.bToSearch && Query.bQuerying))	// 빈 명령·검색 중 (탭 잠김)·쿼리 중: 안 함
		    {
		        return;
		    }

		    const	arrPick	= GetPeriodPick();	// 지금 탭 기간 (옮긴 탭도 같은 기간, 쿼리 D: 지금 기준)

		    const	kInput	= kMove.bToSearch ? kKey : kQueryKey;	// 옮길 탭 입력칸

		    if (kEvent.ctrlKey || kEvent.altKey)	// 새 탭·새 창: RestoreState 가 그 탭 입력칸·기간 넣고 실행 (단계: 연 창에서 복사)
		    {
		        const	kParam	= new URLSearchParams({ sTab: kMove.bToSearch ? kLogButton.id : kQueryButton.id, sMachine: [...setMachineCheck].join(","), bQuery: "0", bMove: "1", sClick: kMove.bRun ? (kMove.bToSearch ? kSearchButton.id : kQueryRunButton.id) : kInput.id });

		        kParam.set(kInput.id, kMove.sKey);
		        [kStart.id, kStartHour.id, kStartMinute.id, kEnd.id, kEndHour.id, kEndMinute.id, kStartSecond.id, kEndSecond.id].forEach((sId, nIndex) => kParam.set(sId, arrPick[nIndex]));
		        kParam.set(kQueryRange.id, "");	// 쿼리 D 해제 → S·E 그대로

		        sOpenMode = kEvent.ctrlKey ? "browser" : "tab";	// Shift+Ctrl: 같은 크기·자리 새 창, Shift+Alt: 새 탭

		        try
		        {
		            OpenWindow(location.pathname + "?" + kParam, 1600, 1000);
		        }
		        finally
		        {
		            sOpenMode = "";
		        }

		        return;
		    }

		    if (kMove.bToSearch)	// 서치 탭: 검색어·지금 기간 넣고 서치 (입력칸 복사: 넣기만, 머신: 서치 것)
		    {
		        ShowPanel("log");
		        SetPeriodPick(arrPick);

		        kKey.value = kMove.sKey;
		    }
		    else	// 쿼리 탭: 쿼리칸·지금 기간 넣고 쿼리 (입력칸 복사: 넣기만, D 해제 → S·E 그대로)
		    {
		        Query.LoadQuery();
		        SetPeriodPick(arrPick);

		        kQueryKey.value = kMove.sKey;

		        Query.OnQueryInput();	// 선택한 저장 쿼리와 다름: 선택 해제
		    }

		    if (!kMove.bRun)	// 입력칸 복사: 그 입력칸 포커스 (마우스 뗀 뒤)
		    {
		        setTimeout(() => kInput.focus());

		        return;
		    }

		    if (kMove.bToSearch)
		    {
		        Search.SearchLog();
		    }
		    else
		    {
		        Query.RunQuery();
		    }
		}

		function OnShiftClick(kEvent)	// Shift+누름으로 옮겨 실행한 뒤 클릭: 막음 (줄 펼침·보기·새 창 안 함)
		{
		    if (bShiftClick)
		    {
		        bShiftClick = false;

		        kEvent.preventDefault();
		        kEvent.stopImmediatePropagation();
		    }
		}

		function OnTabKey(kEvent)	// Tab 키: 오른쪽 탭 선택 (맨 끝: 처음, 첫 화면만, 힌트 팝업: 오른쪽 힌트 탭, 입력칸·다른 팝업·다른 키 같이: 기본 포커스 이동)
		{
		    if (kEvent.key !== "Tab" || kEvent.shiftKey || kEvent.ctrlKey || kEvent.altKey || kEvent.metaKey || kEvent.target.closest?.("input, textarea, select, [contenteditable]") != null)
		    {
		        return;
		    }

		    const	bHelp	= kHelpDialog.open && document.querySelector("dialog[open]:not(#HelpDialog)") == null;	// 힌트 팝업만 열림: 힌트 탭 이동

		    if (!bHelp && (document.body.dataset.mode !== "main" || document.querySelector("dialog[open]") != null))
		    {
		        return;
		    }

		    const	arrTab	= [...document.querySelectorAll(bHelp ? "#HelpTabs button:not([hidden])" : "#TabBar button:not([hidden]):not(#AccountButton):not(#HelpButton)")];	// 탭 순서: 보이는 탭 화면 순서 (aMenu, 계정·힌트 제외, 힌트 팝업: 힌트 탭)
		    const	kNext	= arrTab[(arrTab.findIndex(kTab => kTab.classList.contains("cur")) + 1) % arrTab.length];	// 오른쪽 탭 (맨 끝: 처음)

		    kEvent.preventDefault();

		    if (!kNext.disabled)
		    {
		        kNext.click();
		    }
		}

		function OnResetKey(kEvent)	// F5: 지금 탭 리셋 (새로 고침 버튼과 같음, 첫 화면만, Ctrl·Shift·Alt 같이: 브라우저 그대로 (Ctrl + F5: 새로 고침), 팝업 열림: 안 함)
		{
		    if (kEvent.key !== "F5" || kEvent.ctrlKey || kEvent.shiftKey || kEvent.altKey || kEvent.metaKey || document.body.dataset.mode !== "main")
		    {
		        return;
		    }

		    kEvent.preventDefault();	// 브라우저 새로 고침 막음 (다른 탭 상태 유지)

		    if (document.querySelector("dialog[open]") != null)	// 팝업 열림
		    {
		        return;
		    }

		    OnTabRefresh();	// 새로 고침 버튼과 같음 (화살표 1바퀴)
		}

		function ResetTab()	// 지금 탭만 최초 입장 상태 (쿼리·서치: Query.ResetState, 덤프·파일: 처음부터 다시 조회, 운영·반영: 탭 리셋)
		{
		    const	sTab	= document.querySelector("#TabBar button.cur")?.id ?? "";	// 지금 탭 버튼

		    if (sTab === kQueryButton.id || sTab === kLogButton.id)	// 쿼리: 리셋 뒤 실행, 서치: 빈 첫 화면
		    {
		        Query.ResetState();
		    }
		    else if (sTab === kDumpButton.id)	// 덤프 탭에서 다시: 처음부터
		    {
		        Dump.LoadDump();
		    }
		    else if (sTab === kFileButton.id)	// 파일 탭에서 다시: 처음부터
		    {
		        File.LoadFileTree();
		    }
		    else if (sTab === kManagerButton.id)
		    {
		        Operation.ResetState();
		    }
		    else if (sTab === kProcessButton.id)
		    {
		        Process.ResetState();
		    }
		}

		function OnTabRefresh()	// 제목란 새로 고침 버튼: 지금 탭 리셋 (F5 와 같음, 화살표 1바퀴)
		{
		    const	kButton	= document.getElementById("TabRefresh");

		    kButton.classList.remove("turn");
		    void kButton.offsetWidth;	// 회전 처음부터 다시 (애니메이션 재시작)
		    kButton.classList.add("turn");

		    ResetTab();
		}

		function OnHelpKey(kEvent)	// ?: 힌트 팝업 (첫 화면만, 입력칸·팝업·다른 키 같이: 안 함)
		{
		    if (kEvent.key !== "?" || kEvent.ctrlKey || kEvent.altKey || kEvent.metaKey || document.body.dataset.mode !== "main" || document.querySelector("dialog[open]") != null || kEvent.target.closest?.("input, textarea, select, [contenteditable]") != null)
		    {
		        return;
		    }

		    kEvent.preventDefault();
		    ShowHelp();
		}

		const	HELP_WIDTH_PX	= 650;	// 힌트 팝업 너비 (px, 테두리 포함, 모든 탭 같음, 창 좁음: 화면 너비까지)
		const	HELP_HEIGHT_PX	= 750;	// 힌트 팝업 높이 (px, 테두리 포함, 표 영역이 남는 높이 채움, 모든 탭 같음)
		const	HELP_MIN_PX		= 120;	// 힌트 표 최소 높이 (px, 브라우저 창 작음)

		const	HELP_KEY	= "HMX.HelpTab";	// 마지막에 본 힌트 탭 localStorage 키 (없음: 공통)

		function ShowHelp()	// 힌트 팝업: 마지막에 본 힌트 탭 (처음·저장 못 읽음·숨은 탭: 공통)
		{
		    let	sHelp	= "common";	// 열 힌트 탭

		    try
		    {
		        sHelp = localStorage.getItem(HELP_KEY) ?? "common";
		    }
		    catch	// 저장소 막힘: 공통
		    {
		    }

		    const kButton = document.querySelector(`#HelpTabs button[data-help="${sHelp}"]`);	// 그 힌트 탭 버튼

		    kHelpDialog.showModal();

		    SetHelpTab(kButton != null && !kButton.hidden ? sHelp : "common");
		    FixHelpSize();
		}

		function FixHelpSize()	// 힌트 팝업 크기 고정 (모든 탭 같음): 좌우·상하 X px (표 영역: 남는 높이, 넘침: 표 안 스크롤, 브라우저 창 작음: 줄임, 최소 X px)
		{
		    const	kScroll	= document.getElementById("HelpScroll");

		    kHelpDialog.style.width	= `${HELP_WIDTH_PX}px`;	// 테두리 포함 (box-sizing: border-box)
		    kScroll.style.height	= "0px";

		    const	nHeight	= HELP_HEIGHT_PX - Math.ceil(kHelpDialog.getBoundingClientRect().height);	// 표 영역 높이 (팝업 높이 - 힌트 탭 버튼·닫기·여백)

		    kScroll.style.height	= `${Math.max(HELP_MIN_PX, nHeight)}px`;
		    kScroll.scrollTop		= 0;
		    kHelpDialog.scrollTop	= 0;

		    const	nOver	= kHelpDialog.scrollHeight - kHelpDialog.clientHeight;	// 브라우저 창 작음: 팝업 넘침 → 표 높이 줄임 (닫기 버튼 보이게)

		    if (nOver > 0)
		    {
		        kScroll.style.height = `${Math.max(HELP_MIN_PX, nHeight - nOver)}px`;
		    }
		}

		function SetHelpTab(sHelp)	// 힌트 탭 보기: 그 묶음 줄만, 맨 위부터 (크기: FixHelpSize 고정, 넘침: 표 안 스크롤, 닫기 버튼 autofocus → 아래로 스크롤된 것 되돌림, 포커스: 닫기 그대로)
		{
		    const	kScroll	= document.getElementById("HelpScroll");

		    document.getElementById("HelpTable").dataset.help = sHelp;

		    for (const kButton of document.querySelectorAll("#HelpTabs button"))
		    {
		        kButton.classList.toggle("cur", kButton.dataset.help === sHelp);
		    }

		    kScroll.scrollTop = 0;
		}

		let		sAccountLogin		= "";		// 지금 로그인 아이디 (목록 강조)
		let		arrAccountTab		= [];		// 계정 메뉴 고를 탭 (모든 탭)
		let		arrAccountApi		= [];		// 계정 외부 API 고를 권한 (aAPI: 쿼리·서치·로그)
		let		arrAccountList		= [];		// 계정 목록 { sID, aMenu, aAPI, bMain } (정렬 뒤 전체, 줄 dataset.index = 이 위치)
		const	ACCOUNT_PAGE		= 10;		// 계정 팝업 한 페이지 계정 수 (관리 계정 제외)
		let		nAccountPage		= 0;		// 계정 팝업 지금 페이지 (0부터, 열 때 0)
		let		kAccountEdit		= null;		// 수정·삭제할 계정 (추가: null)
		let		sAccountNewKey		= "";		// 수정 창 새 키 (적용 전, 없음: "")
		const	ACCOUNT_KEY_LENGTH	= 20;		// 계정 키 글자 수 (서버 Auth.m_nKeyLength 와 같음)
		let		bAccountDelete		= false;	// 삭제 창
		let		bAccountAdmin		= false;	// 추가·수정·삭제 가능 (관리 계정)

		async function ShowAccount()	// 계정 팝업: 제목 밑 계정 목록 표만 + 추가 (관리 계정만, 실패: 오류 글만)
		{
		    const	kText	= document.getElementById("AccountText");	// 오류 글 (설명 안 씀)
		    const	kAdd	= document.getElementById("AccountAdd");

		    kText.textContent	= "";
		    kText.hidden		= true;
		    kAdd.hidden			= true;
		    bAccountAdmin		= false;
		    nAccountPage		= 0;

		    ShowAccountList([]);

		    kAccountDialog.showModal();

		    try
		    {
		        const	kData	= await FetchJson("/api/account", { cache: "no-store" });

		        sAccountLogin		= kData.sID;
		        bAccountAdmin		= kData.bAdmin;
		        arrAccountTab		= kData.arrTab;
		        arrAccountApi		= kData.arrApi;
		        kAdd.hidden			= !kData.bAdmin;

		        ShowAccountList(kData.arrAccount);
		    }
		    catch (kError)
		    {
		        kText.textContent	= `오류: ${ErrorText(kError)}`;
		        kText.hidden		= false;
		    }
		}

		function ShowAccountList(arrAccount, nPage = nAccountPage)	// 계정 목록 그리기 (관리 계정: 맨 위 고정 (모든 쪽), 나머지 아이디 오름차순 ACCOUNT_PAGE 개씩 페이지 (번호 버튼, 1쪽: 숨김), 아이디 | 수정·삭제 (관리 계정 로그인만), 관리 계정 줄: "관리", 지금 로그인: 강조)
		{
		    arrAccountList = [...arrAccount].sort((kLeft, kRight) => Number(kRight.bMain) - Number(kLeft.bMain) || kLeft.sID.localeCompare(kRight.sID, undefined, { numeric: true }));	// 관리 계정 먼저, 그다음 오름차순
		    arrAccount = arrAccountList;

		    const	nMainCount	= arrAccount.filter(kAccount => kAccount.bMain).length;						// 고정 줄 (관리 계정)
		    const	nPageCount	= Math.max(1, Math.ceil((arrAccount.length - nMainCount) / ACCOUNT_PAGE));	// 나머지 페이지 수
		    const	kPager		= document.getElementById("AccountPager");

		    nAccountPage = Math.min(Math.max(0, nPage), nPageCount - 1);	// 범위 밖 (삭제로 줄어듦): 끝 페이지

		    kPager.replaceChildren(...Array.from({ length: nPageCount }, (_, nIndex) =>
		    {
		        const	kButton	= document.createElement("button");

		        kButton.type		= "button";
		        kButton.textContent	= String(nIndex + 1);
		        kButton.className	= nIndex === nAccountPage ? "cur" : "";
		        kButton.title		= `${nIndex * ACCOUNT_PAGE + 1} ~ ${Math.min(arrAccount.length - nMainCount, (nIndex + 1) * ACCOUNT_PAGE)}`;

		        kButton.addEventListener("click", () => ShowAccountList(arrAccountList, nIndex));

		        return kButton;
		    }));

		    kPager.style.visibility = nPageCount <= 1 ? "hidden" : "";	// 1쪽: 자리만 (팝업 크기 고정)

		    const	nFrom	= nMainCount + nAccountPage * ACCOUNT_PAGE;	// 이 페이지 첫 계정 위치 (관리 계정 뒤)
		    const arrRow	= [...arrAccount.slice(0, nMainCount), ...arrAccount.slice(nFrom, nFrom + ACCOUNT_PAGE)].map((kAccount, nOffset) =>
		    {
		        const	nIndex	= nOffset < nMainCount ? nOffset : nFrom + nOffset - nMainCount;	// 전체 목록 위치 (관리 계정: 앞, 나머지: 이 페이지)
		        const	kRow	= document.createElement("tr");
		        const	kID		= kRow.insertCell();	// 아이디
		        const	kAct	= kRow.insertCell();	// 수정·삭제

		        kRow.dataset.index	= String(nIndex);	// 목록 위치
		        kRow.className		= kAccount.sID === sAccountLogin ? "sel" : "";
		        kID.textContent		= kAccount.sID;
		        kAct.className		= "act";

		        if (kAccount.bMain)	// 관리 계정: 설정 파일에서만
		        {
		            kAct.textContent	= "관리";
		            kAct.title			= "관리 계정 (Server.json sID·sCode, 모든 메뉴, 비밀번호: 관리 비밀번호)";

		            return kRow;
		        }

		        for (const [sAction, sText] of bAccountAdmin ? [["edit", "수정"], ["delete", "삭제"]] : [])	// 관리 계정 아님: 버튼 없음
		        {
		            const	kButton	= document.createElement("button");

		            kButton.type			= "button";
		            kButton.textContent		= sText;
		            kButton.dataset.action	= sAction;

		            kAct.append(kButton);
		        }

		        return kRow;
		    });	// 없음: 빈 표

		    document.getElementById("AccountList").replaceChildren(...arrRow);
		}

		function OnAccountClick(kEvent)	// 계정 줄: 수정·삭제 버튼, 더블클릭: 수정 (버튼 더블클릭·관리 계정 줄·관리 계정 로그인 아님: 안 함)
		{
		    const	kRow		= kEvent.target.closest("tr[data-index]");						// 누른 줄
		    const	kAccount	= kRow == null ? undefined : arrAccountList[Number(kRow.dataset.index)];
		    const	sButton		= kEvent.target.closest("button[data-action]")?.dataset.action;	// 누른 버튼 (없음: 줄)
		    const	sAction		= kEvent.type === "dblclick" ? (sButton === undefined ? "edit" : "") : (sButton ?? "");

		    if (kAccount === undefined || kAccount.bMain || sAction === "" || !bAccountAdmin)
		    {
		        return;
		    }

		    OpenAccountEdit(kAccount, sAction === "delete");
		}

		function OpenAccountEdit(kAccount, bDelete = false)	// 계정 추가·수정·삭제 창 (kAccount: 수정·삭제할 계정, null: 추가, 관리 비밀번호 필수, 삭제: 아이디·관리 비밀번호만)
		{
		    const	kID		= document.getElementById("AccountEditID");
		    const	kPass	= document.getElementById("AccountEditPass");
		    const	kOk		= document.getElementById("AccountEditOk");

		    kAccountEdit	= kAccount;
		    bAccountDelete	= bDelete;

		    document.getElementById("AccountEditTitle").textContent = bDelete ? "계정 삭제" : (kAccount == null ? "계정 추가" : "계정 수정");

		    kAccountEditDialog.classList.toggle("delete", bDelete);
		    kAccountEditDialog.classList.toggle("edit", kAccount != null && !bDelete);	// 수정: 키·토큰·새 키 줄

		    ShowEditKey(kAccount);

		    kID.value			= kAccount?.sID ?? "";
		    kID.readOnly		= bDelete;
		    kPass.value			= "";
		    kPass.placeholder	= kAccount == null ? "" : "********";	// 수정: 기존 비번 있음 (빈칸 저장: 그대로)

		    document.getElementById("AccountEditMail").value	= kAccount?.sMail ?? "";	// 알람 메일 받는 주소
		    document.getElementById("AccountEditPhone").value	= kAccount?.sPhone ?? "";	// 전화 번호
		    kOk.textContent		= bDelete ? "삭제" : "저장";
		    kOk.className		= bDelete ? "danger" : "primary";

		    document.getElementById("AccountEditAdmin").value = "";
		    const funcCheck = (sTab, arrSel, bEmptyAll) =>	// 메뉴·외부 체크 1개 (추가: 없음, bEmptyAll: 빈 값 = 전체 (메뉴 aMenu), 외부 aAPI: 고른 것만)
		    {
		        const	kLabel	= document.createElement("label");
		        const	kCheck	= document.createElement("input");

		        kCheck.type		= "checkbox";
		        kCheck.value	= sTab;
		        kCheck.checked	= kAccount != null && ((bEmptyAll && arrSel.length === 0) || arrSel.includes(sTab));

		        kLabel.append(kCheck, sTab);

		        return kLabel;
		    };

		    document.getElementById("AccountEditMenu").replaceChildren(...arrAccountTab.map(sTab => funcCheck(sTab, kAccount?.aMenu ?? [], true)));	// 탭 (aMenu)
		    document.getElementById("AccountEditApi").replaceChildren(...arrAccountApi.map(sApi => funcCheck(sApi, kAccount?.aAPI ?? [], false)));	// 외부 API 권한 (aAPI: 쿼리·서치·로그)

		    kAccountEditDialog.showModal();

		    (bDelete ? document.getElementById("AccountEditAdmin") : kID).focus();
		}

		function ShowEditKey(kAccount)	// 수정 창 키·토큰 칸 (없음: 키 없음, 새 키 대기·적용 버튼: 지움)
		{
		    const	kKey	= document.getElementById("AccountEditKey");

		    sAccountNewKey		= "";
		    kKey.textContent	= kAccount?.sKey || "없음";
		    kKey.classList.remove("pending");

		    document.getElementById("AccountEditToken").textContent	= kAccount?.sToken || "";
		    document.getElementById("AccountEditKeyApply").disabled	= true;
		}

		function NewAccountKey()	// 수정 창 새 키 버튼: 새 키 만들어 보여줌 (저장 안 함 → 관리 비밀번호 입력 + 적용, 영문·숫자 X자리, crypto 무작위 치우침 없음)
		{
		    const	sChars	= "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";	// 62자
		    const	kKey	= document.getElementById("AccountEditKey");
		    let		sKey	= "";

		    while (sKey.length < ACCOUNT_KEY_LENGTH)
		    {
		        for (const nByte of crypto.getRandomValues(new Uint8Array(32)))
		        {
		            if (nByte < 248 && sKey.length < ACCOUNT_KEY_LENGTH)	// 62 × 4 = 248 미만만: 치우침 없음
		            {
		                sKey += sChars[nByte % sChars.length];
		            }
		        }
		    }

		    sAccountNewKey		= sKey;
		    kKey.textContent	= sKey;
		    kKey.classList.add("pending");	// 적용 전

		    document.getElementById("AccountEditKeyApply").disabled = false;
		    document.getElementById("AccountEditAdmin").focus();
		}

		async function ApplyAccountKey()	// 관리 비밀번호 옆 적용 버튼: 수정 중인 계정만 새 키 저장 → 서버 Account.json (저장 버튼 안 눌러도 바로, 비밀번호 빈칸: 그 칸으로, 예전 키·링크: 못 씀, 성공: 수정 창 키·목록 다시, 실패: 알림·새 키 그대로)
		{
		    const	kApply	= document.getElementById("AccountEditKeyApply");
		    const	kAdmin	= document.getElementById("AccountEditAdmin");
		    const	sID		= kAccountEdit?.sID ?? "";	// 수정 중인 계정

		    if (sID === "" || sAccountNewKey === "")
		    {
		        return;
		    }

		    if (kAdmin.value === "")	// 관리 비밀번호 필요
		    {
		        kAdmin.focus();

		        return;
		    }

		    kApply.disabled = true;

		    try
		    {
		        const	kData	= await FetchJson("/api/account/key", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ sID, sKey: sAccountNewKey, sAdmin: kAdmin.value }) });

		        if (kData.sError !== "")
		        {
		            throw new Error(kData.sError);
		        }

		        ShowAccountList(kData.arrAccount);

		        const	kAccount	= kData.arrAccount.find(kItem => kItem.sID === sID);	// 새 키 받은 계정

		        if (kAccount !== undefined)
		        {
		            kAccountEdit = kAccount;

		            ShowEditKey(kAccount);
		        }
		    }
		    catch (kError)
		    {
		        alert(`키 적용 실패: ${ErrorText(kError)}`);

		        kApply.disabled = false;	// 새 키 그대로 → 다시 적용
		    }
		}

		async function SaveAccountEdit()	// 계정 추가·수정·삭제 → 서버 Account.json (빈칸: 그 칸으로, 성공: 창 닫고 목록 다시, 실패: 알림·창 유지)
		{
		    const	kID		= document.getElementById("AccountEditID");
		    const	kAdmin	= document.getElementById("AccountEditAdmin");
		    const	kOk		= document.getElementById("AccountEditOk");

		    if (kID.value.trim() === "" || kAdmin.value === "")
		    {
		        (kID.value.trim() === "" ? kID : kAdmin).focus();

		        return;
		    }

		    const	kBody	= bAccountDelete ? { sID: kAccountEdit.sID, sAdmin: kAdmin.value } : { sOldID: kAccountEdit?.sID ?? "", sID: kID.value.trim(), sPass: document.getElementById("AccountEditPass").value, sMail: document.getElementById("AccountEditMail").value.trim(), sPhone: document.getElementById("AccountEditPhone").value.trim(), aMenu: [...document.querySelectorAll("#AccountEditMenu input:checked")].map(kCheck => kCheck.value), aAPI: [...document.querySelectorAll("#AccountEditApi input:checked")].map(kCheck => kCheck.value), sAdmin: kAdmin.value };	// 요청 본문

		    kOk.disabled = true;

		    try
		    {
		        const	kData	= await FetchJson(bAccountDelete ? "/api/account/delete" : "/api/account", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(kBody) });

		        if (kData.sError !== "")
		        {
		            throw new Error(kData.sError);
		        }

		        kAccountEditDialog.close();

		        ShowAccountList(kData.arrAccount);
		    }
		    catch (kError)
		    {
		        alert(`계정 ${bAccountDelete ? "삭제" : "저장"} 실패: ${ErrorText(kError)}`);
		    }

		    kOk.disabled = false;
		}

		function OnTableSelectAll(kEvent)	// Ctrl+A: 누른 표 전체 고름 (없음·안 보임: 맨 위 표, 팝업: 팝업 안 표, 입력칸: 브라우저 기본)
		{
		    if (!(kEvent.ctrlKey || kEvent.metaKey) || kEvent.altKey || kEvent.key.toLowerCase() !== "a" || kEvent.target.closest?.("input, textarea, select") != null)
		    {
		        return;
		    }

		    const	kDialog		= document.querySelector("dialog[open]");	// 열린 팝업 (있으면 그 안 표만)
		    const	funcShow	= (kItem) => kItem?.isConnected === true && kItem.tBodies[0] != null && kItem.getClientRects().length > 0 && (kDialog == null || kDialog.contains(kItem));	// 보이는 표
		    const	kTable		= funcShow(kSelectTable) ? kSelectTable : [...document.querySelectorAll("table")].filter(kItem => funcShow(kItem) && kItem.tBodies[0].rows.length > 0).sort((kLeft, kRight) => kLeft.getBoundingClientRect().top - kRight.getBoundingClientRect().top)[0];	// 고를 표 (지정 없음: 맨 위)
		    const	kBody		= kTable?.tBodies[0] ?? null;

		    if (kBody == null)
		    {
		        return;
		    }

		    const	kRange	= document.createRange();	// 표 전체 (칸 이름 + 줄)

		    kEvent.preventDefault();
		    kRange.selectNodeContents(kTable);

		    window.getSelection().removeAllRanges();
		    window.getSelection().addRange(kRange);
		}

		function ShowPanel(sPanel)	// 보이는 창 바꾸기 (log: 서치 1·2번 창, file: 파일 트리, dump: 덤프 표, manager: 운영 창, process: 반영 창 (file·dump·manager·process: 명령 창 숨김), query: 쿼리 창, 숨긴 창 내용 유지)
		{
		    const	bModeChange	= (sPanel === "query") !== kFilterPanel.classList.contains("query");	// 쿼리 모드 들어감·나감

		    if (bModeChange)	// 명령 창 기간·D 값 바꿔 끼움 (서치·쿼리 따로)
		    {
		        Query.SwapRange(sPanel === "query");
		    }

		    kFilterPanel.classList.toggle("query", sPanel === "query");	// 쿼리 모드: M (쿼리칸 머신 조건)·D·S·E, 쿼리칸

		    if (bModeChange)	// M 목록: 모드에 맞게 (쿼리: 색인 머신·쿼리칸 머신 조건, 서치: 접속 머신 체크), 모래시계: 그 탭 기억
		    {
		        RenderMachineMode();
		        Query.ShowBack();
		    }
		    kFilterPanel.hidden	= sPanel === "dump" || sPanel === "file" || sPanel === "manager" || sPanel === "process";
		    kFilterSplitter.hidden	= kFilterPanel.hidden;	// 명령 창·결과 창 사이 바

		    ShowTimer();	// 타이머 버튼: 지금 탭 타이머 (서치·쿼리 따로)
		    kResultPanel.hidden	= sPanel !== "log" && sPanel !== "query";
		    kSplitter.hidden	= sPanel !== "log";
		    kViewPanel.hidden	= sPanel !== "log";
		    kFilePanel.hidden	= sPanel !== "file";
		    kDumpPanel.hidden	= sPanel !== "dump";
		    kManagerPanel.hidden	= sPanel !== "manager";
		    kProcessPanel.hidden	= sPanel !== "process";
		    kQueryPanel.hidden	= sPanel !== "query";	// 쿼리 2번 창
		    kQuerySplitter.hidden	= sPanel !== "query";	// 쿼리 1·2번 창 사이 바
		    kResultPanel.classList.toggle("query", sPanel === "query");	// 쿼리 1번 창: 서치 1번 창 틀·← 단계 →·새 창 (검색 결과 내용은 유지)

		    Query.ShowHistory();	// ← 단계 →: 보는 탭 단계
		    Query.FitLogHeight();	// 쿼리 2번 창 로그 표 높이: 브라우저 창에 맞춤

		    kLogButton.classList.toggle("cur", sPanel === "log");
		    kFileButton.classList.toggle("cur", sPanel === "file");
		    kDumpButton.classList.toggle("cur", sPanel === "dump");
		    kManagerButton.classList.toggle("cur", sPanel === "manager");
		    kProcessButton.classList.toggle("cur", sPanel === "process");
		    kQueryButton.classList.toggle("cur", sPanel === "query");
		    SetTabZone(document.querySelector("#TabBar button.cur"));	// 탭 존 색 (운영 탭 Manager:sZone 등, 사이트 존과 다름: 제목 색 포함 페이지 전체 존 색만)

		    if (document.activeElement?.closest("#TabBar") != null && !document.activeElement.classList.contains("cur"))	// 이전 탭 버튼 포커스 테두리 제거
		    {
		        document.activeElement.blur();
		    }

		    ShowStatus();	// 설정 버튼 툴팁 (지금 탭 설정)
		    PlaceToolButtons();	// 기능 버튼 자리 (모드마다 보이는 버튼 다름)
		    Search.FitResultHeight();	// 서치 1번 창 높이 (쿼리 모드: 기준 높이)
		    Dump.FitDumpHeight();	// 덤프 1번 창 높이: 브라우저 창에 맞춤
		}

		function FitHeight(kScroll, kPanel)	// 상자 높이: 브라우저 창 아래 끝에 맞춤 (상자 아래 페이지 번호·여백까지 한 화면, 목록 많음: 상자 안 스크롤, 최소 Search.SPLIT_MIN px, 새 창 모드·숨김: 안 함)
		{
		    if (document.body.dataset.mode !== "main" || kPanel.hidden)
		    {
		        return;
		    }

		    const	kRect		= kScroll.getBoundingClientRect();
		    const	nFrame		= kRect.height - (parseFloat(getComputedStyle(kScroll).height) || 0);	// 상자 테두리 (height 는 안쪽만)
		    const	nBelow		= kPanel.getBoundingClientRect().bottom - kRect.bottom;					// 상자 아래 (페이지 번호·여백)
		    const	nPageGap	= parseFloat(getComputedStyle(document.body).paddingBottom) || 0;		// 페이지 아래 여백

		    kScroll.style.height = `${Math.max(Search.SPLIT_MIN, Math.floor(window.innerHeight - (kRect.top + window.scrollY) - nBelow - nPageGap - nFrame))}px`;
		}

		function SetResultTitle(kTitle, sText)	// 상자 제목 (쿼리·서치 결과 건수·시간 숫자만 존 색 span.count, 글자: textContent 로만)
		{
		    const	arrPart	= [];	// 글자·숫자 조각
		    let		nPos	= 0;	// 이어 붙일 위치

		    for (const kMatch of sText.matchAll(/((?:결과|중지): )([\d,]+)(?=건)|(시간: )([\d,]+)(?= ms)/g))
		    {
		        const	kCount	= document.createElement("span");	// 숫자

		        kCount.className	= "count";
		        kCount.textContent	= kMatch[2] ?? kMatch[4];

		        arrPart.push(sText.slice(nPos, kMatch.index) + (kMatch[1] ?? kMatch[3]), kCount);

		        nPos = kMatch.index + kMatch[0].length;
		    }

		    arrPart.push(sText.slice(nPos));

		    kTitle.replaceChildren(...arrPart);
		}

		function ErrorText(kError)	// 요청 오류 → 두 단어 (예: Server Off·Server Error, 서버 오류 글: 그대로)
		{
		    const	kStatus	= /HTTP (\d+)/.exec(kError?.message ?? "");	// HTTP 상태 (예: HTTP 500)

		    if (kError?.name === "AbortError")
		    {
		        return "User Stop";
		    }

		    if (kError?.name === "TimeoutError")
		    {
		        return "Time Out";
		    }

		    if (kError instanceof TypeError)	// fetch 접속 안 됨 (Failed to fetch 등)
		    {
		        return /fetch|network/i.test(kError.message) ? "Server Off" : "Script Error";
		    }

		    if (kError instanceof SyntaxError)	// 응답 JSON 아님
		    {
		        return "Bad Data";
		    }

		    if (kStatus != null)
		    {
		        const	nStatus	= Number(kStatus[1]);

		        return { 400: "Bad Request", 401: "Login Need", 403: "Access Denied", 404: "Not Found", 408: "Time Out", 500: "Server Error", 502: "Bad Gateway", 503: "Server Busy", 504: "Time Out" }[nStatus] ?? (nStatus >= 500 ? "Server Error" : "Bad Request");
		    }

		    return kError?.message || "Unknown Error";
		}

		function AskDelete(sTitle, sName, sDetail, funcYes)	// 삭제 확인 팝업 (덤프·저장 쿼리 공통, sName "": 안 보임, Enter: 취소 → 실수 삭제 방지)
		{
		    funcDeleteYes = funcYes;

		    document.getElementById("DeleteTitle").textContent	= sTitle;
		    document.getElementById("DeleteName").textContent	= sName;
		    kDeleteText.textContent								= sDetail;

		    kDeleteDialog.showModal();

		    document.getElementById("DeleteNo").focus();
		}

		function AskDownload(sName, sDetail, funcYes)	// 다운 확인 팝업 (File 칸·파일 트리·덤프 zip 공통)
		{
		    funcDownYes = funcYes;

		    document.getElementById("DownName").textContent	= sName;
		    document.getElementById("DownPath").textContent	= sDetail;

		    kDownDialog.showModal();

		    document.getElementById("DownYes").focus();	// Enter: 예
		}

		function GetFolderText(sFolder)	// 표 Folder 칸 글자: 마지막 두 폴더 (예: C:\NHNServices\Log1\Server → Log1\Server, 두 단계 이하: 그대로)
		{
		    const	arrPart	= sFolder.split("\\").filter(sPart => sPart !== "");

		    return arrPart.length > 2 ? arrPart.slice(-2).join("\\") : sFolder;
		}

		function GetLogFolder(sPath)	// 로그 파일 경로 → 대상 폴더 (파일명 뗌, 날짜 폴더 yyyyMMdd 뗌)
		{
		    const	sDir	= sPath.slice(0, Math.max(0, sPath.lastIndexOf("\\")));

		    return /\\\d{8}$/.test(sDir) ? sDir.slice(0, sDir.lastIndexOf("\\")) : sDir;
		}

		function AddFolderCell(kRow, sFolder)	// 결과 줄 Folder 칸 (서치·쿼리·덤프 공통, Machine 다음·File 앞): 마지막 두 폴더, 전체 폴더: 툴팁
		{
		    AddCell(kRow, GetFolderText(sFolder), "nowrap").title = sFolder;
		}

		function AddFileNameCell(kRow, sMachine, sPath, nLine = 0, sKey = "")	// 결과 줄 File 칸 (서치·쿼리 공통, 파일명 전체, Shift+클릭: 다운, Ctrl·Alt+클릭: 파일 보기, nLine 0: 마지막 줄부터, sKey: 강조 검색어)
		{
		    const	kCell	= AddCell(kRow, sPath.slice(sPath.lastIndexOf("\\") + 1), "nowrap filename");	// 파일명 (확장자 포함)

		    kCell.title				= sPath;
		    kCell.dataset.machine	= sMachine;
		    kCell.dataset.path		= sPath;
		    kCell.dataset.line		= String(nLine);
		    kCell.dataset.key		= sKey;
		}

		function GetShiftFile(kEvent)	// Shift+클릭 다운 대상 → { sMachine, sPath, sInfo } (File 칸·파일 트리 파일, 그 외: null)
		{
		    if (kEvent.button !== 0 || !kEvent.shiftKey || kEvent.ctrlKey || kEvent.altKey || kEvent.target.closest?.("dialog") != null)
		    {
		        return null;
		    }

		    const	kFile	= kEvent.target.closest?.("td.filename, #FileTree div.node[data-kind=file]") ?? null;	// 누른 파일

		    return kFile == null ? null : { sMachine: kFile.dataset.machine, sPath: kFile.dataset.path, sInfo: kFile.querySelector(".info")?.textContent ?? "" };
		}

		function OnShiftFileDown(kEvent)	// Shift+누름 파일: 글자 고르기 막음
		{
		    if (GetShiftFile(kEvent) != null)
		    {
		        kEvent.preventDefault();
		    }
		}

		function OnShiftFileClick(kEvent)	// Shift+클릭 파일: 확인 팝업 → 브라우저로 받기 (원래 클릭 막음)
		{
		    const	kFile	= GetShiftFile(kEvent);	// 다운할 파일 (null: 원래 동작)

		    if (kFile == null)
		    {
		        return;
		    }

		    kEvent.preventDefault();
		    kEvent.stopPropagation();

		    AskDownload(kFile.sPath.slice(kFile.sPath.lastIndexOf("\\") + 1), `${kFile.sMachine} : ${kFile.sPath}${kFile.sInfo === "" ? "" : ` (${kFile.sInfo})`}`, () => DownloadFile(kFile.sMachine, kFile.sPath));
		}

		function DownloadFile(sMachine, sPath)	// 파일 다운: UTF-8 변환 후 브라우저로 (서버가 조각마다 바로 전달, 상대 주소: ToolManager 경유 대응)
		{
		    const	kLink	= document.createElement("a");

		    kLink.href		= "api/files/download?" + new URLSearchParams({ sMachine, sPath });
		    kLink.download	= "";	// 파일명: 서버 응답 (원본 파일명)

		    kLink.click();
		}

		class Key	// 검색어 규칙 (서치·쿼리 공통, Client SearchNode 와 같음): AND·OR·NOT (대소문자 무시) 왼쪽부터, 괄호 안 먼저, 앞뒤 * 와일드카드 + 검색어 강조
		{
		    static	sSplitKey	= null;	// 마지막으로 나눈 검색어 (결과 줄마다 다시 해석하지 않음)
		    static	arrSplitKey	= [];	// 그 강조할 검색어

		    static GetOperator(sKey, nPos)	// nPos 의 연산자 [길이, 종류 & | !] (AND·OR·NOT 대소문자 무시 + 공백·여는 괄호, 아님: [0, ""])
		    {
		        for (const [sWord, sType] of [["AND", "&"], ["OR", "|"], ["NOT", "!"]])
		        {
		            const	cNext	= sKey[nPos + sWord.length];	// 연산자 뒤 글자

		            if (sKey.substr(nPos, sWord.length).toUpperCase() === sWord && cNext !== undefined && (/\s/.test(cNext) || cNext === "("))	// 대소문자 무시
		            {
		                return [sWord.length, sType];
		            }
		        }

		        return [0, ""];
		    }

		    static IsClose(sKey, nPos)	// nPos 의 ')' 가 닫는 괄호 (뒤에 공백·')' 만 이어진 뒤 끝·연산자, 아님: 검색어 글자)
		    {
		        let	nNext	= nPos + 1;	// 뒤 글자

		        while (nNext < sKey.length && (/\s/.test(sKey[nNext]) || sKey[nNext] === ")"))
		        {
		            ++nNext;
		        }

		        return nNext >= sKey.length || Key.GetOperator(sKey, nNext)[0] > 0;
		    }

		    static FindTermEnd(sKey, nStart, nDepth)	// 검색어 끝: 공백 뒤 연산자·괄호 안 닫는 괄호 앞 (검색어 안 짝 맞는 괄호: 검색어 글자)
		    {
		        let	nInner	= 0;	// 검색어 안에서 열린 괄호 수 (예: Init(x))

		        for (let nIndex = nStart; nIndex < sKey.length; ++nIndex)
		        {
		            const	cChar	= sKey[nIndex];

		            if (/\s/.test(cChar))	// 공백 뒤 연산자·닫는 괄호면 여기서 끝
		            {
		                let	nNext	= nIndex;	// 공백 다음 글자

		                while (nNext < sKey.length && /\s/.test(sKey[nNext]))
		                {
		                    ++nNext;
		                }

		                if (nNext < sKey.length && (Key.GetOperator(sKey, nNext)[0] > 0 || (nDepth > 0 && nInner === 0 && sKey[nNext] === ")" && Key.IsClose(sKey, nNext))))
		                {
		                    return nIndex;
		                }

		                nIndex = nNext - 1;

		                continue;
		            }

		            if (cChar === "(")
		            {
		                ++nInner;
		            }
		            else if (cChar === ")" && nInner > 0)
		            {
		                --nInner;
		            }
		            else if (cChar === ")" && nDepth > 0 && Key.IsClose(sKey, nIndex))	// 괄호 안 검색어 끝
		            {
		                return nIndex;
		            }
		        }

		        return sKey.length;
		    }

		    static Tokenize(sKey)	// 토큰 나누기: 검색어 T·AND &·OR |·NOT !·괄호 (null: 형식 오류)
		    {
		        const	arrToken	= [];	// { sType, sTerm }
		        let		nPos		= 0;	// 읽을 위치
		        let		nDepth		= 0;	// 열린 괄호 수
		        let		bOperand	= true;	// 검색어·여는 괄호 차례 (false: 연산자·닫는 괄호 차례)

		        while (true)
		        {
		            while (nPos < sKey.length && /\s/.test(sKey[nPos]))
		            {
		                ++nPos;
		            }

		            if (nPos >= sKey.length)
		            {
		                break;
		            }

		            if (bOperand && sKey[nPos] === "(")	// 여는 괄호
		            {
		                arrToken.push({ sType: "(", sTerm: "" });

		                ++nDepth;
		                ++nPos;

		                continue;
		            }

		            if (bOperand)	// 검색어: 다음 연산자·닫는 괄호 앞까지
		            {
		                const	nEnd	= Key.FindTermEnd(sKey, nPos, nDepth);	// 검색어 끝

		                arrToken.push({ sType: "T", sTerm: sKey.slice(nPos, nEnd).trim() });

		                nPos		= nEnd;
		                bOperand	= false;

		                continue;
		            }

		            if (sKey[nPos] === ")" && nDepth > 0)	// 닫는 괄호
		            {
		                arrToken.push({ sType: ")", sTerm: "" });

		                --nDepth;
		                ++nPos;

		                continue;
		            }

		            const [nOpLength, sType] = Key.GetOperator(sKey, nPos);	// 연산자 길이 (0: 아님)

		            if (nOpLength === 0)	// 연산자 자리에 다른 글자 (예: (A) B)
		            {
		                return null;
		            }

		            arrToken.push({ sType: sType, sTerm: "" });

		            nPos		+= nOpLength;
		            bOperand	= true;
		        }

		        return bOperand ? null : arrToken;	// 연산자·여는 괄호로 끝남: 형식 오류
		    }

		    static ParseExpr(arrToken, kState)	// 식: 항 (연산자 항)… 왼쪽부터 차례로 (null: 형식 오류, kState.nIndex: 읽을 토큰)
		    {
		        let	kLeft	= Key.ParseTerm(arrToken, kState);	// 지금까지 결과

		        while (kLeft != null && kState.nIndex < arrToken.length && arrToken[kState.nIndex].sType !== ")")
		        {
		            const	sOp	= arrToken[kState.nIndex].sType;	// 연산자

		            if (sOp !== "&" && sOp !== "|" && sOp !== "!")	// 연산자 자리에 다른 토큰
		            {
		                return null;
		            }

		            ++kState.nIndex;

		            const	kRight	= Key.ParseTerm(arrToken, kState);

		            kLeft = kRight == null ? null : { sOp: sOp, kLeft: kLeft, kRight: kRight };
		        }

		        return kLeft;
		    }

		    static ParseTerm(arrToken, kState)	// 항: 검색어 또는 (식) (닫는 괄호 없이 끝나면 끝에서 닫힘, null: 형식 오류)
		    {
		        if (kState.nIndex >= arrToken.length)
		        {
		            return null;
		        }

		        const	kToken	= arrToken[kState.nIndex++];

		        if (kToken.sType === "T")
		        {
		            return kToken.sTerm === "" ? null : { sOp: "T", sTerm: kToken.sTerm };
		        }

		        if (kToken.sType !== "(")
		        {
		            return null;
		        }

		        const	kInner	= Key.ParseExpr(arrToken, kState);	// 괄호 안

		        if (kState.nIndex < arrToken.length && arrToken[kState.nIndex].sType === ")")
		        {
		            ++kState.nIndex;
		        }

		        return kInner;
		    }

		    static Parse(sKey)	// 검색어 → 조건 트리 { sOp: T·&·|·!, sTerm, kLeft, kRight } (빈 검색어: null, 형식 오류: 전체를 검색어 하나로)
		    {
		        const	sTrim	= sKey.trim();

		        if (sTrim === "")
		        {
		            return null;
		        }

		        const	arrToken	= Key.Tokenize(sTrim);	// 토큰 (null: 형식 오류)
		        const	kState		= { nIndex: 0 };		// 읽을 토큰
		        const	kRoot		= arrToken == null ? null : Key.ParseExpr(arrToken, kState);

		        if (arrToken == null || kRoot == null || kState.nIndex !== arrToken.length)	// 형식 오류: 전체를 검색어 하나로
		        {
		            return { sOp: "T", sTerm: sTrim };
		        }

		        return kRoot;
		    }

		    static IsValid(sKey)	// 검색어 형식 맞음 (빈 괄호·연산자로 시작·끝·빈 검색어: false, 서버 SearchNode.IsValid 와 같음)
		    {
		        const	sTrim	= sKey.trim();

		        if (sTrim === "")
		        {
		            return false;
		        }

		        const	arrToken	= Key.Tokenize(sTrim);	// 토큰 (null: 형식 오류)
		        const	kState		= { nIndex: 0 };		// 읽을 토큰

		        return arrToken != null && Key.ParseExpr(arrToken, kState) != null && kState.nIndex === arrToken.length && !arrToken.some(kToken => kToken.sType === "T" && /^(AND|OR|NOT)(\s|$)|\s(AND|OR|NOT)$/i.test(kToken.sTerm.trim()));	// 검색어가 AND·OR·NOT (대소문자 무시) 로 시작·끝남 (예: "A AND"): 연산자 빠짐으로 봄
		    }

		    static SplitKey(sKey)	// 강조할 검색어: 조건 트리에서 찾는 쪽 검색어 모두 (NOT 오른쪽은 제외 조건 → 강조 안 함)
		    {
		        if (sKey === Key.sSplitKey)
		        {
		            return Key.arrSplitKey;
		        }

		        const	arrKey	= [];				// 강조할 검색어
		        const	kRoot	= Key.Parse(sKey);	// 조건 트리 (빈 검색어: null)

		        const funcCollect = (kNode, bFind) =>	// 검색어 모으기 (bFind: 찾는 쪽, NOT 오른쪽에서 뒤집힘)
		        {
		            if (kNode.sOp === "T")
		            {
		                if (bFind)
		                {
		                    arrKey.push(kNode.sTerm);
		                }

		                return;
		            }

		            funcCollect(kNode.kLeft, bFind);
		            funcCollect(kNode.kRight, kNode.sOp === "!" ? !bFind : bFind);
		        };

		        if (kRoot != null)
		        {
		            funcCollect(kRoot, true);
		        }

		        Key.sSplitKey		= sKey;
		        Key.arrSplitKey	= arrKey;

		        return arrKey;
		    }

		    static IsWild(sTerm)	// 와일드카드 검색어: 앞뒤 * 이고 * 뗀 글자 있음 (서버·클라 TryGetWild 와 같음)
		    {
		        const	sTrim	= sTerm.trim();

		        return (sTrim.startsWith("*") || sTrim.endsWith("*")) && sTrim.replace(/^\*+|\*+$/g, "") !== "";
		    }

		    static MakeRegex(sTerm)	// 검색어 → 찾기 정규식 (대소문자 무시, 와일드카드: * 뗀 글자 + 앞·뒤 * 없는 쪽 글자·숫자 경계, 서버·클라 IsWildMatch 와 같음, 그 외: 글자 그대로)
		    {
		        const	sTrim	= sTerm.trim();
		        const	bWild	= Key.IsWild(sTerm);
		        const	sText	= (bWild ? sTrim.replace(/^\*+|\*+$/g, "") : sTerm).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");	// 정규식 기호 그대로

		        return new RegExp(bWild ? `${sTrim.startsWith("*") ? "" : "(?<![\\p{L}\\p{Nd}])"}${sText}${sTrim.endsWith("*") ? "" : "(?![\\p{L}\\p{Nd}])"}` : sText, "giu");
		    }

		    static AppendHighlight(kParent, sText, sKey)	// 텍스트 추가 + 찾는 쪽 검색어 강조 (대소문자 무시, 와일드카드 ab*·*ab: 경계, 텍스트 노드로만 → HTML 해석 안 함)
		    {
		        if (sKey !== Key.sRegexKey)	// 검색어 정규식 (같은 검색어: 다시 안 만듦)
		        {
		            Key.sRegexKey	= sKey;
		            Key.arrRegex	= Key.SplitKey(sKey).map(sItem => Key.MakeRegex(sItem));
		        }

		        const	arrRegex	= Key.arrRegex;	// 강조할 검색어
		        let		nPos		= 0;			// 추가할 위치

		        while (arrRegex.length > 0)
		        {
		            let	nFind	= -1;	// 가장 앞에 있는 검색어 위치 (-1: 없음)
		            let	nLength	= 0;	// 그 검색어 길이 (같은 위치면 긴 것)

		            for (const kRegex of arrRegex)
		            {
		                kRegex.lastIndex = nPos;

		                const	kMatch	= kRegex.exec(sText);	// nPos 뒤 첫 맞음

		                if (kMatch != null && kMatch[0].length > 0 && (nFind === -1 || kMatch.index < nFind || (kMatch.index === nFind && kMatch[0].length > nLength)))
		                {
		                    nFind	= kMatch.index;
		                    nLength	= kMatch[0].length;
		                }
		            }

		            if (nFind === -1)	// 남은 검색어 없음
		            {
		                break;
		            }

		            const	kMark	= document.createElement("mark");

		            kParent.append(sText.slice(nPos, nFind));
		            kMark.textContent = sText.slice(nFind, nFind + nLength);
		            kParent.append(kMark);

		            nPos = nFind + nLength;
		        }

		        kParent.append(sText.slice(nPos));
		    }
		}
		""";
	}

	private static string IndexTail()	// 기본 페이지 공통 뒤: 이벤트 연결·첫 화면 스크립트
	{
		return """
		window.Query = Query;	// 필드 새 창에서 호출 (class 전역은 window 속성이 아님)
		window.Search = Search;	// Ctrl·Alt+클릭 새 창·새 탭이 연 창의 서치 단계 복사 (GetOpenerState)

		kStart.addEventListener("change", () => FixRange(true));
		kStartHour.addEventListener("change", () => FixRange(true));
		kEnd.addEventListener("change", () => FixRange(false));
		kEndHour.addEventListener("change", () => FixRange(false));
		kStartMinute.addEventListener("change", () => FixRange(true));	// 쿼리 모드: D 해제
		kEndMinute.addEventListener("change", () => FixRange(false));
		kStartSecond.addEventListener("change", () => FixRange(true));	// 초: 분과 같음
		kEndSecond.addEventListener("change", () => FixRange(false));

		for (const kPick of [kStart, kStartHour, kStartMinute, kStartSecond, kEnd, kEndHour, kEndMinute, kEndSecond])	// S·E 고르기 제한: 바뀜·누름 (포커스) 마다 다시 (코드로 바꾼 값 반영)
		{
		    kPick.addEventListener("change", LimitRange);
		    kPick.addEventListener("focus", LimitRange);
		}

		LimitRange();	// 처음 값 기준
		kQueryRange.addEventListener("change", Query.SetRecent);	// D 고름: S·E 를 지금까지로
		document.getElementById("ResetButton").addEventListener("click", Query.GoBack);	// 모래시계: 이전 실행으로 (서치·쿼리 따로 최근 X회)
		kTimerButton.addEventListener("click", OnTimerButton);	// 타이머 (서치·쿼리 따로): 초 입력 → 초 줄여 감 → 0: 반짝 + 실행 → 다시 (도는 중 누름: 중지)
		kQueryPrev.addEventListener("click", () => (kResultPanel.classList.contains("query") ? Query : Search).GoHistory(-1));	// ← 뒤로 (보는 1번 창 들어온 단계)
		kQueryNext.addEventListener("click", () => (kResultPanel.classList.contains("query") ? Query : Search).GoHistory(1));	// → 앞으로
		kQueryDepth.addEventListener("click", Query.EditHistory);	// 가운데 단계 누름: 숫자 입력 → 그 단계로
		kLensUp.addEventListener("click", (kEvent) => kEvent.detail <= 1 && Query.MoveLens(-1));	// 렌즈 ↑: 막대 큰 단위 (스텝 0: 기간 넓혀 쿼리, 더블 클릭 두 번째: 무시 → 한 단계만)
		kLensDown.addEventListener("click", (kEvent) => kEvent.detail <= 1 && Query.MoveLens(1));	// 렌즈 ↓: 막대 작은 단위 (초봉까지, 더블 클릭 두 번째: 무시 → 한 단계만)
		kQueryBody.addEventListener("click", Query.ToggleDetail);			// 쿼리 결과 줄 클릭: 아래로 펼침·접음

		window.addEventListener("resize", Query.FitLogHeight);	// 브라우저 창 크기: 쿼리 2번 창 로그 표 높이 다시 (맨 아래 페이지 번호까지 한 화면)
		window.addEventListener("resize", Search.FitViewHeight);	// 브라우저 창 크기: 서치 2번 창 높이 다시
		window.addEventListener("resize", Dump.FitDumpHeight);	// 브라우저 창 크기: 덤프 1번 창 높이 다시
		window.addEventListener("resize", PlaceToolButtons);	// 브라우저 창 크기: 기능 버튼 1줄 ↔ 2줄 실행·중단 오른쪽
		kQueryScroll.addEventListener("scroll", () =>	// 읽은 줄 끝 가까이: 다음 줄 이어서
		{
		    if (Query.IsNearRowEnd())
		    {
		        Query.LoadMore();
		    }
		});
		kQueryBody.addEventListener("click", Query.OnWordClick);			// 쿼리 결과 Log 단어 Ctrl·Alt+클릭: 쿼리칸에 단어

		kQueryBody.addEventListener("mousedown", (kEvent) =>	// 쿼리 결과 Log 단어·펼친 필드 값 Ctrl·Alt+클릭: 글자 고르기 막음 (단어 추가만)
		{
		    if ((kEvent.ctrlKey || kEvent.altKey) && kEvent.target.closest("td.log, .querytext, .queryfield span[data-field]") != null)
		    {
		        kEvent.preventDefault();
		    }
		});

		document.addEventListener("mousedown", OnShiftFileDown, true);	// Shift+누름 파일: 글자 고르기 막음
		document.addEventListener("click", OnShiftFileClick, true);		// Shift+클릭 파일: 다운 (원래 처리보다 먼저)
		document.addEventListener("mousedown", OnShiftMouseDown, true);	// Shift+누름: 쿼리 ↔ 서치 옮겨 실행 (Ctrl·Alt 새 창보다 먼저)
		document.addEventListener("click", OnShiftClick, true);			// 옮겨 실행한 뒤 클릭: 막음
		document.addEventListener("mousedown", OnOpenMouseDown, true);	// Ctrl·Alt+누름 (새 창 대상): 포커스·드롭 박스 막음 (원래 처리보다 먼저)
		document.addEventListener("click", OnOpenClick, true);			// Ctrl+클릭: 새 창, Alt+클릭: 새 탭 (원래 처리보다 먼저)
		document.addEventListener("pointerdown", (kEvent) => kSelectTable = kEvent.target.closest?.("table") ?? null, true);	// 누른 표 (표 밖: 없음)
		document.addEventListener("keydown", OnTableSelectAll);			// Ctrl+A: 누른 표 전체 (지정 없음: 맨 위 표)
		document.addEventListener("keydown", OnTabKey);					// Tab: 오른쪽 탭 (힌트 제외, 맨 끝: 처음, 힌트 팝업: 힌트 탭)
		document.addEventListener("keydown", OnHelpKey);				// ?: 힌트 팝업
		document.addEventListener("keydown", OnResetKey);				// F5: 지금 탭 리셋 (새로 고침 버튼과 같음)
		document.addEventListener("keyup", Query.OnModifierUp);	// Ctrl·Alt 뗌: MODIFIER_WAIT_MS 뒤 쿼리·서치
		document.addEventListener("keydown", Query.OnModifierDown);	// Ctrl·Alt 다시 누름: 기다리던 쿼리 취소 (이어서 더함)
		window.addEventListener("blur", Query.OnModifierUp);		// Ctrl·Alt 누른 채 창 벗어남: 같게

		kQueryFieldList.addEventListener("click", Query.OnFieldClick);	// 왼쪽 필드: 값 목록 새 창
		kQueryFieldBody.addEventListener("click", Query.OnFieldWindowClick);	// 필드 새 창 값: 연 창 쿼리에 AND 필드:값
		kQueryFieldBody.closest("table").tHead.addEventListener("click", Query.OnFieldSortClick);	// 필드 새 창 제목: 보이는 페이지 정렬

		kMachineBox.addEventListener("click", OnMachineBox);
		kMachineList.addEventListener("change", OnMachineCheck);
		kMachineAll.addEventListener("change", OnMachineAll);
		document.addEventListener("click", OnMachineOutside);
		document.addEventListener("keydown", OnMachineOutside);
		kSearchButton.addEventListener("click", () => Search.SearchLog());	// 서치 버튼: 서치만 (쿼리와 별개)
		kQueryRunButton.addEventListener("click", () => Query.RunQuery());	// 쿼리 버튼: 쿼리만 (서치와 별개)
		kQueryStopButton.addEventListener("click", Query.StopQuery);			// 쿼리 중단 버튼: 쿼리·통계 요청 끊음
		kQueryEditList.addEventListener("click", Query.OnSavedClick);		// 쿼리 설정 줄 버튼: 수정·삭제
		kQueryEditList.addEventListener("dblclick", Query.OnSavedClick);		// 쿼리 설정 제목 더블클릭: 바로 쿼리
		kQueryEditAdd.addEventListener("click", () => Query.bRule ? Query.OpenRule(null) : (Query.bAlarm ? Query.OpenAlarm(null) : Query.OpenSaved(null)));	// 쿼리 설정 추가: 빈 추가 창 (알람: 기본값)
		kAlarmHistoryList.addEventListener("click", Query.OnAlarmHistoryClick);		// 알람 기록 제목: 내용 창
		document.getElementById("AlarmHistoryClose").addEventListener("click", () => kAlarmHistoryDialog.close());
		document.getElementById("AlarmHistoryViewClose").addEventListener("click", () => kAlarmHistoryViewDialog.close());
		kAlarmHistoryViewBody.addEventListener("click", Query.RunAlarmLink);		// 알람 기록 내용 클릭: 그 기간·쿼리 실행
		kQueryEditScroll.addEventListener("dragstart", Query.OnEditDragStart);	// 쿼리 설정 줄 드래그: 순서 바꿈
		kQueryEditScroll.addEventListener("dragend", Query.OnEditDragEnd);

		kQueryEditScroll.addEventListener("dragover", Query.OnEditDragOver);	// 놓을 곳: 줄 상자 (밖으로: 표시 지움)
		kQueryEditScroll.addEventListener("drop", Query.OnEditDrop);
		kQueryEditScroll.addEventListener("dragleave", (kEvent) =>
		{
		    if (!kQueryEditScroll.contains(kEvent.relatedTarget))
		    {
		        Query.ClearDrop();
		    }
		});

		document.getElementById("QuerySaveOk").addEventListener("click", Query.SaveSavedOk);		// 쿼리 추가·수정 창 저장
		document.getElementById("QuerySaveCancel").addEventListener("click", () => kQuerySaveDialog.close());

		kQuerySaveDialog.addEventListener("keydown", (kEvent) =>	// 쿼리 추가·수정 창: 제목 Enter·쿼리 Ctrl+Enter 저장 (한글 조합 중 제외)
		{
		    if (kEvent.key === "Enter" && !kEvent.isComposing && kEvent.target.tagName !== "BUTTON" && (kEvent.target.tagName !== "TEXTAREA" || kEvent.ctrlKey))
		    {
		        kEvent.preventDefault();

		        if (kEvent.target === kAlarmMail)	// 알람 메일·SMS 입력칸: 추가
		        {
		            Query.AddAlarmMail();
		        }
		        else if (kEvent.target === kAlarmSMS)
		        {
		            Query.AddAlarmSMS();
		        }
		        else
		        {
		            Query.SaveSavedOk();
		        }
		    }
		});
		document.getElementById("FieldRuleOk").addEventListener("click", Query.SaveRuleOk);		// 필드 추가·수정 창 저장
		document.getElementById("FieldRuleCancel").addEventListener("click", () => kFieldRuleDialog.close());

		kFieldRuleDialog.addEventListener("keydown", (kEvent) =>	// 필드 추가·수정 창: 이름·고정값 Enter·정규식 Ctrl+Enter 저장 (한글 조합 중 제외)
		{
		    if (kEvent.key === "Enter" && !kEvent.isComposing && kEvent.target.tagName !== "BUTTON" && (kEvent.target.tagName !== "TEXTAREA" || kEvent.ctrlKey))
		    {
		        kEvent.preventDefault();

		        Query.SaveRuleOk();
		    }
		});
		document.getElementById("AlarmTest").addEventListener("click", Query.TestAlarm);		// 알람 테스트 발송
		document.getElementById("AlarmMailAdd").addEventListener("click", Query.AddAlarmMail);	// 알람 메일 추가
		document.getElementById("AlarmSMSAdd").addEventListener("click", Query.AddAlarmSMS);		// 알람 SMS 추가
		kQueryKey.addEventListener("input", Query.OnQueryInput);				// 쿼리칸 고침: 선택한 저장 쿼리와 달라지면 선택 해제
		kStopButton.addEventListener("click", Search.StopSearch);
		kSettingButton.addEventListener("click", OpenSetting);	// 설정 버튼 (서치·쿼리): 지금 탭 설정
		kSearchSavedButton.addEventListener("click", () => Query.OpenSetting(false, false, true));	// 저장 버튼 (서치): 저장 서치 목록 (쿼리 설정 창 공용)
		kKey.addEventListener("input", Search.OnKeyInput);	// 검색어칸 고침: 선택한 저장 서치와 달라지면 선택 해제
		kQuerySavedButton.addEventListener("click", () => Query.OpenSetting(false));	// 저장 버튼 (쿼리): 쿼리 설정 저장 탭
		kQueryAlarmButton.addEventListener("click", () => Query.OpenSetting(true));	// 알람 버튼 (쿼리): 쿼리 설정 알람 탭
		kQueryHistoryButton.addEventListener("click", Query.OpenAlarmHistory);		// 기록 버튼 (쿼리): 알람 기록 창 (전체, 최신 순)
		kQueryRuleButton.addEventListener("click", () => Query.OpenSetting(false, true));	// 필드 버튼 (쿼리): 쿼리 설정 필드 탭 (설정 aField)
		document.getElementById("SettingSave").addEventListener("click", SaveSetting);
		document.getElementById("SettingCancel").addEventListener("click", () => kSettingDialog.close());
		document.getElementById("QuerySettingCancel").addEventListener("click", () => kQuerySettingDialog.close());
		document.getElementById("MachineMaxHint").textContent	= `개 (1 ~ ${MACHINE_LIMIT.toLocaleString()})`;
		document.getElementById("ResultMaxHint").textContent	= `개 (1 ~ ${RESULT_LIMIT.toLocaleString()})`;
		document.getElementById("SearchLimitHint").textContent	= `초 (1 ~ ${LIMIT_SEC_MAX.toLocaleString()})`;
		document.getElementById("QueryLimitHint").textContent	= `초 (1 ~ ${LIMIT_SEC_MAX.toLocaleString()})`;
		kMachineMaxInput.max	= MACHINE_LIMIT;
		kResultMaxInput.max		= RESULT_LIMIT;
		kSearchLimitInput.max	= LIMIT_SEC_MAX;
		kQueryLimitInput.max	= LIMIT_SEC_MAX;

		document.getElementById("SettingDefault").addEventListener("click", () =>	// 기본값 표시 (저장 눌러야 적용)
		{
		    kMachineMaxInput.value	= MACHINE_DEFAULT;
		    kResultMaxInput.value	= RESULT_DEFAULT;
		    kSearchLimitInput.value	= LIMIT_SEC_DEFAULT;
		    kQueryLimitInput.value	= LIMIT_SEC_DEFAULT;
		    kSortSelect.value		= SORT_DEFAULT;
		});

		kSettingDialog.addEventListener("keydown", (kEvent) =>	// 설정 창 Enter: 저장 (한글 조합 중 제외)
		{
		    if (kEvent.key === "Enter" && !kEvent.isComposing && kEvent.target.tagName !== "BUTTON")
		    {
		        kEvent.preventDefault();
		        SaveSetting();
		    }
		});

		kKey.addEventListener("keydown", (kEvent) =>	// 검색어 Enter: 서치만 (한글 조합 중 Enter 제외)
		{
		    if (kEvent.key === "Enter" && !kEvent.isComposing)
		    {
		        Search.SearchLog();
		    }
		});

		kQueryKey.addEventListener("keydown", (kEvent) =>	// 쿼리 Enter: 쿼리만 (한글 조합 중 Enter 제외)
		{
		    if (kEvent.key === "Enter" && !kEvent.isComposing)
		    {
		        Query.RunQuery();
		    }
		});

		kViewPrev.addEventListener("click", () => ShiftPos(Search.VIEW_POS, -1));	// 포지션 « 위로 (포지션 범위 안, -1 -2 …)
		kViewNext.addEventListener("click", () => ShiftPos(Search.VIEW_POS, 1));	// 포지션 » 아래로 (포지션 범위 안, 1 2 …)
		kViewShift.addEventListener("click", () => EditPos(Search.VIEW_POS));		// 가운데 포지션 누름: 숫자 입력 → 그 포지션으로
		kFilePrev.addEventListener("click", () => ShiftPos(File.FILE_POS, -1));	// 파일 보기 포지션 « (포지션 0: 파일 맨 아래)
		kFileNext.addEventListener("click", () => ShiftPos(File.FILE_POS, 1));	// 파일 보기 포지션 »
		kFileShift.addEventListener("click", () => EditPos(File.FILE_POS));		// 파일 보기 가운데 포지션 누름: 숫자 입력

		kResultBody.addEventListener("click", Search.OnResultClick);
		kResultBody.addEventListener("click", Search.OnWordClick);	// 서치 1번 창 Log 단어 Ctrl·Alt+클릭: 검색어에 AND·OR·NOT 단어 (키 뗄 때 서치)
		kViewBody.addEventListener("click", Search.OnWordClick);		// 서치 2번 창 줄 단어: 같게

		for (const kBody of [kResultBody, kViewBody])
		{
		    kBody.addEventListener("mousedown", (kEvent) =>	// 서치 1·2번 창 단어 Ctrl·Alt+클릭: 글자 고르기 막음 (단어 추가만)
		    {
		        if (Search.IsWordClick(kEvent))
		        {
		            kEvent.preventDefault();
		        }
		    });
		}
		kDumpButton.addEventListener("click", Dump.LoadDump);
		kLogButton.addEventListener("click", () => ShowPanel("log"));	// 서치 버튼: 서치 1·2번 창으로 (내용 유지)
		kQueryButton.addEventListener("click", Query.LoadQuery);		// 쿼리 버튼: 쿼리 1번 창
		kFileButton.addEventListener("click", File.LoadFileTree);		// 파일 버튼: 파일 1번 창 폴더 트리
		kManagerButton.addEventListener("click", Operation.Open);		// 운영 버튼: 운영 창 (처음: 서버 점검·동접 조회)
		Operation.Init();												// 운영 창 이벤트 (새로 고침·패치·하위 탭·모니터·세로선)
		kProcessButton.addEventListener("click", Process.Open);		// 반영 버튼: 반영 창 (처음: 버전·목록 로딩 + 폴링, 다시 열기: 바로 갱신)
		Process.Init();												// 반영 창 이벤트 (줄 동작·메뉴·서랍·팝업·Esc·연결 끊김 잠금)
		kFileTree.addEventListener("click", File.OnFileTreeClick);
		document.getElementById("HelpButton").addEventListener("click", ShowHelp);	// 힌트 버튼: 힌트 팝업 (탭 그대로)
		document.getElementById("AccountButton").addEventListener("click", ShowAccount);	// 계정 버튼: 계정 팝업 (아이디·로그아웃)
		document.getElementById("AccountClose").addEventListener("click", () => kAccountDialog.close());
		document.getElementById("AccountAdd").addEventListener("click", () => OpenAccountEdit(null));	// 계정 추가 버튼: 추가 창
		document.getElementById("AccountList").addEventListener("click", OnAccountClick);			// 계정 줄 버튼: 수정·삭제
		document.getElementById("AccountList").addEventListener("dblclick", OnAccountClick);		// 계정 줄 더블클릭: 수정
		document.getElementById("AccountEditOk").addEventListener("click", SaveAccountEdit);
		document.getElementById("AccountEditKeyNew").addEventListener("click", NewAccountKey);		// 수정 창 새 키: 새 키 만들어 보여줌 (저장 안 함)
		document.getElementById("AccountEditKeyApply").addEventListener("click", ApplyAccountKey);	// 수정 창 적용: 관리 비밀번호 확인 → 수정 중인 계정만 새 키 저장
		document.getElementById("AccountEditCancel").addEventListener("click", () => kAccountEditDialog.close());
		kAccountEditDialog.addEventListener("keydown", (kEvent) =>	// 계정 창 입력칸 Enter: 저장·삭제 (한글 조합 중 제외)
		{
		    if (kEvent.key === "Enter" && !kEvent.isComposing && kEvent.target.matches("input"))
		    {
		        kEvent.preventDefault();

		        SaveAccountEdit();
		    }
		});
		document.getElementById("TabRefresh").addEventListener("click", OnTabRefresh);	// 새로 고침 버튼: 지금 탭 리셋 (F5)
		document.getElementById("HelpClose").addEventListener("click", () => kHelpDialog.close());
		document.getElementById("HelpTabs").addEventListener("click", (kEvent) =>	// 힌트 탭 버튼: 그 묶음 보기 + 마지막 본 탭 저장
		{
		    const	kButton	= kEvent.target.closest("button[data-help]");

		    if (kButton == null)
		    {
		        return;
		    }

		    SetHelpTab(kButton.dataset.help);

		    try
		    {
		        localStorage.setItem(HELP_KEY, kButton.dataset.help);	// 마지막에 본 탭 (다음에 열 때)
		    }
		    catch	// 저장소 막힘: 저장 안 함
		    {
		    }
		});		// 힌트 팝업 닫기

		document.getElementById("DownYes").addEventListener("click", () =>	// 다운 확인 예: 닫고 받기 (파일: 브라우저로, 덤프: zip 브라우저로)
		{
		    const	funcYes	= funcDownYes;	// 닫히면 지워지므로 먼저 보관

		    kDownDialog.close();

		    funcYes?.();
		});

		document.getElementById("DownNo").addEventListener("click", () => kDownDialog.close());	// 다운 확인 아니오: 닫기
		kDownDialog.addEventListener("close", () => funcDownYes = null);							// 닫힘 (예·아니오·Esc): 확인 중 아님

		document.getElementById("DeleteYes").addEventListener("click", () =>	// 삭제 확인 삭제: 닫고 삭제 (덤프 전체·덤프 하나·저장 쿼리)
		{
		    const	funcYes	= funcDeleteYes;	// 닫히면 지워지므로 먼저 보관

		    kDeleteDialog.close();

		    funcYes?.();
		});

		document.getElementById("DeleteNo").addEventListener("click", () => kDeleteDialog.close());	// 삭제 확인 취소: 닫기
		kDeleteDialog.addEventListener("close", () => funcDeleteYes = null);							// 닫힘 (삭제·취소·Esc): 확인 중 아님

		kFileViewScroll.addEventListener("scroll", () =>	// 파일 1번 창 오른쪽 보이는 줄이 읽은 끝 가까이·빈 높이: 이어서·그 위치로 (위·아래에 쌓임), 포지션 (스크롤 따라감)
		{
		    File.FillFileView();
		    SchedulePos(File.FILE_POS);
		});

		kFileSplitter.addEventListener("pointerdown", (kEvent) =>	// 파일 1번 창 세로선 좌우 드래그: 왼쪽 트리 폭 (최소 File.FILE_SPLIT_MIN)
		{
		    const	nStartX		= kEvent.clientX;	// 드래그 시작 위치
		    const	nWidthStart	= kFileScroll.getBoundingClientRect().width;	// 왼쪽 시작 폭
		    const	nWidthMax	= kFileSplitter.parentElement.getBoundingClientRect().width - kFileSplitter.getBoundingClientRect().width - File.FILE_SPLIT_MIN;	// 왼쪽 최대 폭 (오른쪽 최소 남김)

		    const	nTitleExtra	= kFileSplitter.getBoundingClientRect().width - parseFloat(getComputedStyle(kFileTitle.parentElement).columnGap);	// 머리줄 왼쪽 제목 칸 폭 (오른쪽 제목 = 오른쪽 상자 위치)

		    const funcMove = (kMove) =>	// 끈 만큼 왼쪽 폭 변경 (머리줄 왼쪽 제목 칸도 같이)
		    {
		        const	nWidth	= Math.min(nWidthMax, Math.max(File.FILE_SPLIT_MIN, nWidthStart + kMove.clientX - nStartX));	// 왼쪽 트리 폭

		        kFileScroll.style.width	= nWidth + "px";
		        kFileTitle.style.width	= (nWidth + nTitleExtra) + "px";
		    };

		    const funcUp = () =>	// 드래그 끝: 이벤트 해제
		    {
		        kFileSplitter.classList.remove("drag");
		        kFileSplitter.removeEventListener("pointermove", funcMove);
		        kFileSplitter.removeEventListener("pointerup", funcUp);
		        kFileSplitter.removeEventListener("pointercancel", funcUp);
		    };

		    kFileSplitter.setPointerCapture(kEvent.pointerId);	// 세로선 밖으로 나가도 계속 추적
		    kFileSplitter.classList.add("drag");
		    kFileSplitter.addEventListener("pointermove", funcMove);
		    kFileSplitter.addEventListener("pointerup", funcUp);
		    kFileSplitter.addEventListener("pointercancel", funcUp);

		    kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		});
		kDumpBody.addEventListener("click", Dump.OnDumpClick);
		kDumpDeleteAll.addEventListener("click", Dump.DeleteAllDump);

		const	kSplitter	= document.getElementById("Splitter");	// 서치 1·2번 창 사이 바

		kSplitter.addEventListener("pointerdown", (kEvent) =>	// 서치 1·2번 창 사이 바 드래그: 높이 합 유지 (최소 Search.SPLIT_MIN)
		{
		    const	nStartY		= kEvent.clientY;												// 드래그 시작 위치
		    const	nTopStart	= parseFloat(getComputedStyle(kResultScroll).height);			// 서치 1번 창 시작 높이
		    const	nTotal		= nTopStart + parseFloat(getComputedStyle(kViewScroll).height);	// 두 창 높이 합 (유지)

		    const funcMove = (kMove) =>	// 끈 만큼 서치 1번 창 높이 변경, 서치 2번 창은 나머지
		    {
		        const	nTop	= Math.min(Math.max(Search.SPLIT_MIN, nTopStart + kMove.clientY - nStartY), nTotal - Search.SPLIT_MIN);	// 서치 1번 창 높이

		        kResultScroll.style.height	= nTop + "px";
		        kViewScroll.style.height	= (nTotal - nTop) + "px";
		        Search.sResultHeight		= kResultScroll.style.height;	// 드래그한 높이 = 기준 높이

		        Search.FitViewHeight();
		    };

		    const funcUp = () =>	// 드래그 끝: 이벤트 해제
		    {
		        kSplitter.classList.remove("drag");
		        kSplitter.removeEventListener("pointermove", funcMove);
		        kSplitter.removeEventListener("pointerup", funcUp);
		        kSplitter.removeEventListener("pointercancel", funcUp);
		    };

		    kSplitter.setPointerCapture(kEvent.pointerId);	// 바 밖으로 나가도 계속 추적
		    kSplitter.classList.add("drag");
		    kSplitter.addEventListener("pointermove", funcMove);
		    kSplitter.addEventListener("pointerup", funcUp);
		    kSplitter.addEventListener("pointercancel", funcUp);

		    kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		});

		kQueryFieldSplitter.addEventListener("pointerdown", (kEvent) =>	// 필드·로그 사이 세로선 좌우 드래그: 왼쪽 필드 폭 (최소 Query.FIELD_SPLIT_MIN)
		{
		    const	nStartX		= kEvent.clientX;	// 드래그 시작 위치
		    const	nWidthStart	= kQueryField.getBoundingClientRect().width;	// 왼쪽 시작 폭
		    const	nWidthMax	= kQuerySplit.getBoundingClientRect().width - kQueryFieldSplitter.getBoundingClientRect().width - Query.FIELD_SPLIT_MIN;	// 왼쪽 최대 폭 (오른쪽 최소 남김)

		    const funcMove = (kMove) =>	// 끈 만큼 왼쪽 폭 변경 (세로선·오른쪽 로그는 CSS 변수로 따라옴)
		    {
		        const	nWidth	= Math.min(nWidthMax, Math.max(Query.FIELD_SPLIT_MIN, nWidthStart + kMove.clientX - nStartX));	// 왼쪽 필드 폭

		        kQuerySplit.style.setProperty("--fieldWidth", nWidth + "px");
		    };

		    const funcUp = () =>	// 드래그 끝: 이벤트 해제
		    {
		        kQueryFieldSplitter.classList.remove("drag");
		        kQueryFieldSplitter.removeEventListener("pointermove", funcMove);
		        kQueryFieldSplitter.removeEventListener("pointerup", funcUp);
		        kQueryFieldSplitter.removeEventListener("pointercancel", funcUp);
		    };

		    kQueryFieldSplitter.setPointerCapture(kEvent.pointerId);	// 세로선 밖으로 나가도 계속 추적
		    kQueryFieldSplitter.classList.add("drag");
		    kQueryFieldSplitter.addEventListener("pointermove", funcMove);
		    kQueryFieldSplitter.addEventListener("pointerup", funcUp);
		    kQueryFieldSplitter.addEventListener("pointercancel", funcUp);

		    kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		});

		kFilterSplitter.addEventListener("pointerdown", (kEvent) =>	// 명령 창·결과 창 사이 바 드래그: 두 창 높이 합 유지 (명령 창: 원래 높이 이상·늘면 아래 빈칸, 결과 창: 서치 결과·쿼리 막대 최소 이상, 아래 창: 나머지)
		{
		    const	bQuery			= kResultPanel.classList.contains("query");							// 쿼리 모드: 결과 창 = 막대
		    const	nStartY			= kEvent.clientY;													// 드래그 시작 위치
		    const	nFilterStart	= kFilterPanel.getBoundingClientRect().height;						// 명령 창 시작 높이
		    const	nFilterFrame	= nFilterStart - parseFloat(getComputedStyle(kFilterPanel).height);	// 명령 창 여백·테두리 (min-height: 안쪽만)
		    const	sMinHeight		= kFilterPanel.style.minHeight;										// 지금 늘린 높이

		    kFilterPanel.style.minHeight = "";

		    const	nFilterMin	= kFilterPanel.getBoundingClientRect().height;	// 명령 창 원래 높이
		    const	nBoxStart	= bQuery ? (parseFloat(getComputedStyle(kQueryChart).getPropertyValue("--chartHeight")) || 120) : parseFloat(getComputedStyle(kResultScroll).height);	// 결과 창 시작 높이
		    const	nBoxMin		= bQuery ? Query.CHART_MIN : Search.SPLIT_MIN;	// 결과 창 최소 높이

		    kFilterPanel.style.minHeight = sMinHeight;

		    const funcMove = (kMove) =>	// 끈 만큼 명령 창 높이 변경, 결과 창은 반대로
		    {
		        const	nFilter	= Math.min(Math.max(nFilterMin, nFilterStart + kMove.clientY - nStartY), nFilterStart + nBoxStart - nBoxMin);	// 명령 창 높이
		        const	nBox	= nBoxStart - (nFilter - nFilterStart);	// 결과 창 높이

		        kFilterPanel.style.minHeight = nFilter > nFilterMin ? (nFilter - nFilterFrame) + "px" : "";

		        if (bQuery)
		        {
		            kQueryChart.style.setProperty("--chartHeight", nBox + "px");

		            Query.FitLogHeight();
		        }
		        else
		        {
		            kResultScroll.style.height	= nBox + "px";
		            Search.sResultHeight		= kResultScroll.style.height;	// 끈 높이 = 기준 높이

		            Search.FitViewHeight();
		        }
		    };

		    const funcUp = () =>	// 드래그 끝: 이벤트 해제
		    {
		        kFilterSplitter.classList.remove("drag");
		        kFilterSplitter.removeEventListener("pointermove", funcMove);
		        kFilterSplitter.removeEventListener("pointerup", funcUp);
		        kFilterSplitter.removeEventListener("pointercancel", funcUp);
		    };

		    kFilterSplitter.setPointerCapture(kEvent.pointerId);	// 바 밖으로 나가도 계속 추적
		    kFilterSplitter.classList.add("drag");
		    kFilterSplitter.addEventListener("pointermove", funcMove);
		    kFilterSplitter.addEventListener("pointerup", funcUp);
		    kFilterSplitter.addEventListener("pointercancel", funcUp);

		    kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		});

		kQuerySplitter.addEventListener("pointerdown", (kEvent) =>	// 쿼리 1·2번 창 사이 바 드래그: 막대 + 로그 높이 합 유지 (최소 CHART_MIN·SPLIT_MIN)
		{
		    const	nStartY		= kEvent.clientY;																		// 드래그 시작 위치
		    const	nChartStart	= parseFloat(getComputedStyle(kQueryChart).getPropertyValue("--chartHeight")) || 120;	// 막대 시작 높이
		    const	nTotal		= nChartStart + parseFloat(getComputedStyle(kQueryScroll).height);						// 막대 + 로그 표 높이 합 (유지)

		    const funcMove = (kMove) =>	// 끈 만큼 막대 높이 변경, 로그 표는 나머지 (첫 화면: 브라우저 창 아래 끝까지)
		    {
		        const	nChart	= Math.min(Math.max(Query.CHART_MIN, nChartStart + kMove.clientY - nStartY), nTotal - Search.SPLIT_MIN);	// 막대 높이

		        kQueryChart.style.setProperty("--chartHeight", nChart + "px");
		        kQueryScroll.style.height = (nTotal - nChart) + "px";

		        Query.FitLogHeight();
		    };

		    const funcUp = () =>	// 드래그 끝: 이벤트 해제
		    {
		        kQuerySplitter.classList.remove("drag");
		        kQuerySplitter.removeEventListener("pointermove", funcMove);
		        kQuerySplitter.removeEventListener("pointerup", funcUp);
		        kQuerySplitter.removeEventListener("pointercancel", funcUp);
		    };

		    kQuerySplitter.setPointerCapture(kEvent.pointerId);	// 바 밖으로 나가도 계속 추적
		    kQuerySplitter.classList.add("drag");
		    kQuerySplitter.addEventListener("pointermove", funcMove);
		    kQuerySplitter.addEventListener("pointerup", funcUp);
		    kQuerySplitter.addEventListener("pointercancel", funcUp);

		    kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		});

		const	kViewHScroll		= document.getElementById("ViewHScroll");		// 서치 2번 창 아래 좌우 바 (창 아래쪽이 화면 밖일 때 화면 맨 아래에 표시)
		const	kViewHScrollSize	= document.getElementById("ViewHScrollSize");	// 좌우 바 안 폭 (좌우 이동 범위)

		kViewHScroll.addEventListener("scroll", () =>	// 좌우 바 이동 → 서치 2번 창 같이 이동
		{
		    kViewScroll.scrollLeft = kViewHScroll.scrollLeft;
		});
		kViewScroll.addEventListener("scroll", () =>	// 서치 2번 창 이동 → 좌우 바 위치, 이어 읽기 (FillView: 위·아래에 쌓임), 포지션 (스크롤 따라감)
		{
		    kViewHScroll.scrollLeft = kViewScroll.scrollLeft;

		    Search.FillView();
		    SchedulePos(Search.VIEW_POS);
		});
		window.addEventListener("scroll", Search.SyncViewHScroll);	// 페이지 스크롤: 창 아래쪽 보임 여부
		window.addEventListener("resize", Search.SyncViewHScroll);
		new ResizeObserver(Search.SyncViewHScroll).observe(kViewScroll);		// 서치 2번 창 크기 (바 드래그)
		new ResizeObserver(Search.SyncViewHScroll).observe(document.body);	// 위쪽 내용 변화로 서치 2번 창 위치 이동
		new MutationObserver(Search.SyncViewHScroll).observe(kViewBody, { childList: true });	// 파일·페이지 내용 바뀜

		function InitColumnGrip(kTable)	// 표 칸 너비 조절 (모든 표 공통): 칸 오른쪽 경계 손잡이 (마지막 칸 제외), 드래그·더블 클릭
		{
		    for (const kHead of [...kTable.tHead.rows[0].cells].slice(0, -1).filter(kItem => kItem.cellIndex !== GetRestColumn(kTable)))	// 마지막 칸·나머지 너비 칸 제외
		    {
		        const	kGrip	= document.createElement("span");

		        kGrip.className	= "colgrip";
		        kGrip.title		= "칸 너비";

		        kHead.append(kGrip);
		    }

		    kTable.tHead.addEventListener("pointerdown", (kEvent) =>	// 칸 경계 드래그: 중엔 세로선만, 놓을 때 한 번 적용 (큰 표 재배치 멈춤 방지), 최소 1글자, 차이: 나머지 칸 흡수
		    {
		        const	kGrip	= kEvent.target.closest(".colgrip");	// 잡은 손잡이

		        if (kGrip == null)
		        {
		            return;
		        }

		        const	kHead		= kGrip.parentElement;								// 조절할 칸 제목
		        const	kLastHead	= kHead.parentElement.cells[GetRestColumn(kTable)];	// 나머지 너비 칸 제목 (기본: 마지막 칸)
		        const	kCol		= kTable.querySelectorAll("col")[kHead.cellIndex];	// 조절할 칸
		        const	kHeadRect	= kHead.getBoundingClientRect();					// 조절할 칸 제목 위치 (세로선 기준)
		        const	kScrollRect	= kTable.parentElement.getBoundingClientRect();		// 표 스크롤 영역 (세로선 위치·높이)
		        const	nStartX		= kEvent.clientX;									// 드래그 시작 위치
		        const	nWidthStart	= kTable.classList.contains("fixed") ? parseFloat(kCol.style.width) : kHeadRect.width;	// 칸 시작 너비 (고정 전: 지금 자동 너비 = FixColumn 이 고정할 너비)
		        const	nWidthMin	= GetColumnMin(kHead);								// 칸 최소 너비 (1글자)
		        const	nWidthMax	= nWidthStart + Math.max(0, kLastHead.getBoundingClientRect().width - GetColumnMin(kLastHead));	// 칸 최대 너비 (마지막 칸이 1글자 남을 때까지)
		        let		nWidth		= nWidthStart;										// 놓을 때 적용할 너비

		        const funcMove = (kMove) =>	// 끈 만큼 세로선만 이동 (표는 그대로)
		        {
		            nWidth = Math.min(nWidthMax, Math.max(nWidthMin, nWidthStart + kMove.clientX - nStartX));

		            kColGuide.style.left = (kHeadRect.left + nWidth) + "px";
		        };

		        const funcUp = (kUp) =>	// 드래그 끝: 놓음 → 칸 너비 한 번 적용 (취소·안 움직임: 그대로)
		        {
		            kGrip.classList.remove("drag");
		            kGrip.removeEventListener("pointermove", funcMove);
		            kGrip.removeEventListener("pointerup", funcUp);
		            kGrip.removeEventListener("pointercancel", funcUp);

		            kColGuide.hidden = true;

		            if (kUp.type === "pointerup" && nWidth !== nWidthStart)
		            {
		                FixColumn(kTable);

		                kCol.style.width = nWidth + "px";
		            }
		        };

		        kColGuide.style.left	= (kHeadRect.left + nWidthStart) + "px";
		        kColGuide.style.top		= kScrollRect.top + "px";
		        kColGuide.style.height	= kScrollRect.height + "px";
		        kColGuide.hidden		= false;

		        kGrip.setPointerCapture(kEvent.pointerId);	// 손잡이 밖으로 나가도 계속 추적
		        kGrip.classList.add("drag");
		        kGrip.addEventListener("pointermove", funcMove);
		        kGrip.addEventListener("pointerup", funcUp);
		        kGrip.addEventListener("pointercancel", funcUp);

		        kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		    });

		    kTable.tHead.addEventListener("dblclick", (kEvent) =>	// 칸 경계 더블 클릭: 자동 너비로 되돌림
		    {
		        if (kEvent.target.closest(".colgrip") != null)
		        {
		            ResetColumn(kTable);
		        }
		    });
		}

		const	FOLD_KEY		= "HMX.Fold";	// 칸 접기 localStorage 키 ({ 표 id: [칸 번호] }, 표마다)
		const	FOLD_WIDTH_PX	= 20;			// 접은 칸 너비 (px, 모든 칸 고정)
		const	FOLD_NEAR_PX	= 2;			// 최소 폭 판정 여유 (px, 경계 드래그 최소 1글자 ≒ 접은 폭)
		const	hmapFold		= LoadFold();	// 접은 칸 (표 id → 칸 번호 목록)

		function LoadFold()	// 저장된 접은 칸 (없음·오류: 빈)
		{
		    try
		    {
		        const	kSaved	= JSON.parse(localStorage.getItem(FOLD_KEY) ?? "null");

		        if (kSaved != null && typeof kSaved === "object" && !Array.isArray(kSaved))
		        {
		            return kSaved;
		        }
		    }
		    catch (kError)	// 저장소 사용 불가·깨짐
		    {
		        console.error(kError);
		    }

		    return {};
		}

		function IsFoldColumn(kTable, nIndex)	// 접을 수 있는 칸: 나머지 너비 칸·마지막 칸 제외 (손잡이와 같음)
		{
		    return nIndex !== GetRestColumn(kTable) && nIndex < kTable.tHead.rows[0].cells.length - 1;
		}

		function GetNaturalWidth(kTable, nIndex)	// 칸 기본 너비: 칸 글자 최대 너비 (자동 너비와 같음, 고정 너비 표에서 칸마다 글자 폭 잼, colspan 줄 제외)
		{
		    let	nWidth	= 0;

		    for (const kRow of kTable.rows)
		    {
		        const	kCell	= kRow.cells[nIndex];

		        if (kCell != null && kCell.colSpan === 1)
		        {
		            nWidth = Math.max(nWidth, kCell.scrollWidth);
		        }
		    }

		    return Math.ceil(nWidth) + 1;	// + 칸 경계선
		}

		function ApplyFold(kTable)	// 접은 칸 그림: FOLD_WIDTH_PX 고정 (CSS, colspan 칸 제외), 고정 너비 표: col 너비도
		{
		    const	arrCol	= kTable.querySelectorAll("col");
		    const	arrFold	= (Array.isArray(hmapFold[kTable.id]) ? hmapFold[kTable.id] : []).filter(nIndex => Number.isInteger(nIndex) && nIndex >= 0 && IsFoldColumn(kTable, nIndex));
		    const	bFixed	= kTable.classList.contains("fixed");

		    document.getElementById(kTable.id + "Fold").textContent = arrFold.map(nIndex => `#${kTable.id} tr>:nth-child(${nIndex + 1}):not([colspan]){box-sizing:border-box;width:${FOLD_WIDTH_PX}px;min-width:${FOLD_WIDTH_PX}px;max-width:${FOLD_WIDTH_PX}px;padding-right:0;overflow:hidden;text-overflow:clip;white-space:nowrap}`).join("");

		    if (!bFixed)	// 자동 너비: CSS 만
		    {
		        return;
		    }

		    for (const nIndex of arrFold)	// 고정 너비 표: 접은 칸 col 너비도 (펼침 너비: ToggleFold)
		    {
		        if (arrCol[nIndex] != null)
		        {
		            arrCol[nIndex].style.width = FOLD_WIDTH_PX + "px";
		        }
		    }
		}

		function ToggleFold(kTable, nIndex)	// 칸 접기 ↔ 기본 크기 (표마다 저장): 접힌 칸·경계 드래그로 최소 폭 된 칸 → 기본 크기 (글자 최대 너비), 그 밖 → 접음
		{
		    const	arrFold	= Array.isArray(hmapFold[kTable.id]) ? hmapFold[kTable.id] : [];
		    const	kHead	= kTable.tHead.rows[0].cells[nIndex];
		    const	kCol	= kTable.querySelectorAll("col")[nIndex];
		    const	bFold	= !arrFold.includes(nIndex) && kHead.getBoundingClientRect().width > Math.max(FOLD_WIDTH_PX, GetColumnMin(kHead)) + FOLD_NEAR_PX;	// 접음 (이미 최소 폭: 펼침)

		    hmapFold[kTable.id] = bFold ? [...arrFold, nIndex] : arrFold.filter(nItem => nItem !== nIndex);

		    if (hmapFold[kTable.id].length === 0)
		    {
		        delete hmapFold[kTable.id];
		    }

		    try
		    {
		        localStorage.setItem(FOLD_KEY, JSON.stringify(hmapFold));
		    }
		    catch (kError)	// 저장소 사용 불가: 이번 창만
		    {
		        console.error(kError);
		    }

		    ApplyFold(kTable);

		    if (!bFold && kTable.classList.contains("fixed") && kCol != null)	// 고정 너비 표 펼침: 그 칸만 기본 너비 (다른 칸 드래그 너비 유지)
		    {
		        kCol.style.width = GetNaturalWidth(kTable, nIndex) + "px";
		    }
		}

		function InitColumnFold(kTable)	// 표 칸 접기 (모든 표 공통): 칸 이름 클릭 → 좌우 FOLD_WIDTH_PX 고정 ↔ 다시 클릭: 기본 크기, 표마다 저장
		{
		    const	kStyle	= document.createElement("style");	// 이 표 접은 칸 CSS

		    kStyle.id = kTable.id + "Fold";
		    document.head.append(kStyle);

		    for (const kHead of kTable.tHead.rows[0].cells)
		    {
		        kHead.classList.toggle("foldhead", IsFoldColumn(kTable, kHead.cellIndex));
		    }

		    kTable.tHead.addEventListener("click", (kEvent) =>	// 칸 이름 클릭 (경계 손잡이 제외)
		    {
		        const	kHead	= kEvent.target.closest("th");

		        if (kHead?.classList.contains("foldhead") && kEvent.target.closest(".colgrip") == null)
		        {
		            ToggleFold(kTable, kHead.cellIndex);
		        }
		    });

		    ApplyFold(kTable);
		}

		InitColumnGrip(kResultTable);	// 서치 1번 창 (마지막 Log 칸이 나머지)
		InitColumnGrip(kDumpTable);		// 덤프 1번 창 (마지막 Ac. 칸이 나머지, 맨 오른쪽 고정)
		InitColumnGrip(kQueryBody.closest("table"));		// 쿼리 2번 창 (마지막 Log 칸이 나머지)
		InitColumnGrip(kQueryFieldBody.closest("table"));	// 필드 값 목록 (마지막 Count 칸이 나머지)
		InitColumnFold(kResultTable);	// 칸 접기: 서치 1번 창
		InitColumnFold(kDumpTable);		// 덤프 1번 창
		InitColumnFold(kQueryBody.closest("table"));	// 쿼리 2번 창 (필드 값 목록: 칸 이름 클릭 = 정렬 → 제외)

		for (const sTable of ["ManagerServerTable", "ManagerDownTable", "ManagerMaxTable", "ManagerCcuTable"])	// 운영 표
		{
		    InitColumnFold(document.getElementById(sTable));
		}

		if (document.body.dataset.mode === "view")	// 새 창 (보기 모드): 쿼리의 파일·페이지만 보기
		{
		    const	kQuery	= new URLSearchParams(location.search);
		    const	sPath	= kQuery.get("sPath") ?? "";

		    document.title = `${sPath.slice(sPath.lastIndexOf("\\") + 1)} - ${document.title}`;	// 창 구분용: 파일명

		    const	nTarget	= Number(kQuery.get("nTarget") ?? 0);	// 강조할 줄 (0: 줄 모름 → 마지막 줄부터)

		    Search.OpenView(kQuery.get("sMachine") ?? "", sPath, nTarget, kQuery.get("sKey") ?? "", nTarget > 0 ? null : 0);	// 그 줄 위·아래부터 (스크롤: 이어서)
		}
		else if (document.body.dataset.mode === "field")	// 필드 값 목록 새 창 (필드 모드)
		{
		    Query.LoadFieldWindow(new URLSearchParams(location.search));	// Ctrl+A: 값 표 전체 (OnTableSelectAll: 맨 위 표)
		}
		else
		{
		    const	kOpenParam	= new URLSearchParams(location.search);	// Ctrl·Alt+클릭으로 열림: 지금 상태 (sTab 있음)

		    ApplyMenu();	// 탭 메뉴: 설정 aMenu (보일 탭·순서, 빠진 탭: 아래 시작 조회 안 함)

		    LoadLimit();
		    ShowStatus();

		    const	bClient			= [kQueryButton, kLogButton, kFileButton, kDumpButton].some(kButton => !kButton.hidden);	// 클라 쓰는 탭 있음 (없음: 서버 /api/client 없음)
		    const	kMachineLoad	= bClient ? LoadMachine() : Promise.resolve();

		    Search.FitResultHeight();	// 검색 전에도 서치 1번 창 최소 줄 수 높이

		    if (!kQueryButton.hidden)
		    {
		        Query.LoadQuery();		// 기본 탭: 쿼리 (D 1일)
		    }
		    else	// 쿼리 탭 꺼짐: 쿼리 조회 안 함, 서치 처음 기간만 (D 1일)
		    {
		        Query.SetVisitRange();
		    }

		    if (kOpenParam.has("sTab"))	// 머신 목록 받은 뒤 상태 복원 (머신 체크·파일 탭 머신)
		    {
		        kMachineLoad.then(() => RestoreState(kOpenParam));
		    }
		    else
		    {
		        const	kFirst	= document.querySelector("#TabBar button:not([hidden]):not(#AccountButton):not(#HelpButton)");	// 첫 탭: aMenu 첫 번째 (없음: 쿼리)

		        if (kFirst == null || kFirst === kQueryButton)
		        {
		            Query.RunQuery();	// 처음 입장: 전체 쿼리 자동 실행
		        }
		        else
		        {
		            kMachineLoad.then(() => kFirst.click());	// 첫 탭 (쿼리 아님: 쿼리 자동 실행 안 함, 머신 목록 받은 뒤 → 덤프·파일 머신 없음 경고 안 남)
		        }
		    }

		    if (bClient)
		    {
		        setInterval(LoadMachine, MACHINE_REFRESH_MS);	// 접속 머신 자동 갱신 (클라 재접속·끊김 반영, M 펼칠 때도 다시 조회)
		    }
		}
		</script>
		</body>
		</html>
		""";
	}
}

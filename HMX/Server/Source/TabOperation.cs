using StackExchange.Redis;	// Redis 해시 명령 (이 파일·Redis 만, 전역 using 안 함 → ClientInfo 등 이름 겹침 방지)

class TabOperation	// 운영 탭: 게임 서버 XLO 모니터 점검 (운영·로비·AuthKey·접속자·CDS)·서버 버전·패치 시작 시각·X분 정각 동접 기록 (Redis) API (페이지 쪽: ManagerHTML, ServerManager 이식)
{
	private readonly OperationMonitor	m_kMonitor;	// 게임 서버 점검

	public TabOperation()
	{
		m_kMonitor = new OperationMonitor();
	}

	public void MapRoute(WebApplication WebApp)	// 라우트 등록 (운영 탭)
	{
		WebApp.MapGet("/api/manager/server", OnServer);		// 서버 점검 후 목록 + 패치 시각
		WebApp.MapPost("/api/manager/version", OnVersion);	// 서버 버전 임시 설정 (?sKey=ip:port&sVersion=)
		WebApp.MapGet("/api/manager/monitor", OnMonitor);	// XLO 모니터 페이지 이동 (?sKey=ip:port)
		WebApp.MapGet("/api/manager/cds", OnCDS);			// 서버 1대 CDS 확인 (?sKey=ip:port)
		WebApp.MapPost("/api/manager/patch", OnPatch);		// 패치 시작 시각 설정 (?nPatchTime=unix, 없음: 현재)
		WebApp.MapGet("/api/manager/ccu", OnCCU);			// 최고 동접 + 동접 기록
		WebApp.MapGet("/api/manager/redis", OnRedis);		// Redis 연결 확인 (PING)
	}

	public void StartTimer(CancellationToken kStop)	// 동접 기록 타이머 시작 (서버 목록·Redis 설정 있을 때만, 웹 서버 종료 시 중지)
	{
		if (m_kMonitor.GetCount() == 0 || !Redis.IsSet())
		{
			Log.Write(INFO_, $"동접 기록: 안 함 (운영 서버 {m_kMonitor.GetCount()}대, Redis {(Redis.IsSet() ? "설정 있음" : "설정 없음")})");

			return;
		}

		_ = RunTimer(kStop);
	}

	private async Task RunTimer(CancellationToken kStop)	// X분 정각마다 전체 점검 → 동접 기록
	{
		while (!kStop.IsCancellationRequested)
		{
			DateTimeOffset	kNow	= GetNow();
			DateTimeOffset	kTick	= new DateTimeOffset(kNow.Year, kNow.Month, kNow.Day, kNow.Hour, kNow.Minute, 0, kNow.Offset).AddMinutes(OperationRedis.TICK_MINUTE - kNow.Minute % OperationRedis.TICK_MINUTE);	// 다음 기록 시각 (정각)

			try
			{
				await Task.Delay(kTick - kNow + TimeSpan.FromSeconds(1), kStop);	// 1초 여유 (타이머 오차로 정각 전 실행 방지)

				await m_kMonitor.Check();

				await OperationRedis.Record(m_kMonitor.GetList(), kTick.ToString("yyyy-MM-dd HH:mm"));
			}
			catch (OperationCanceledException) when (kStop.IsCancellationRequested)	// 서버 종료
			{
				return;
			}
			catch (Exception kException)	// Redis 오류 등: 다음 주기 재시도
			{
				Log.Write(ERROR, $"동접 기록 실패: {GetShortError(kException)}");
			}
		}
	}

	private async Task<IResult> OnServer()	// 서버 정보: 전체 점검 후 목록 + 패치 시작 시각 (Redis 오류여도 목록은 응답)
	{
		await m_kMonitor.Check();

		OperationServer[]	arrServer	= m_kMonitor.GetList();
		long				nPatchTime	= 0;
		string				sError		= "";

		try
		{
			nPatchTime = await OperationRedis.GetPatchTime();
		}
		catch (Exception kException)	// Redis 오류
		{
			sError = GetShortError(kException);
		}

		return Results.Ok(new
		{
			sZone		= GetZone(),
			nPatchTime	= nPatchTime,
			sPatchTime	= GetTimeText(nPatchTime),
			sCheckTime	= m_kMonitor.GetCheckTime(),
			nOperating	= arrServer.Count(kServer => kServer.bSuccess),
			sError		= sError,
			listServer	= arrServer,
		});
	}

	private IResult OnVersion(string sKey, string sVersion)	// 서버 버전 임시 설정 (메모리, 재시작 시 1.0)
	{
		sVersion = sVersion.Trim();

		if (sVersion == "" || sVersion.Length > 32)	// 파라미터 검사 및 조기 반환
		{
			return Results.BadRequest(new { sError = "버전: 1~32자" });
		}

		if (!m_kMonitor.SetVersion(sKey, sVersion))
		{
			return Results.NotFound(new { sError = $"없는 서버: {sKey}" });
		}

		return Results.Ok(new { sKey, sVersion });
	}

	private async Task<IResult> OnCDS(string sKey)	// 서버 1대 CDS 확인 (모니터 탭에서 CDS 등록·해제 후 변경 감지용)
	{
		OperationServer?	kServer	= await m_kMonitor.CheckOne(sKey);

		if (kServer == null)	// 설정에 없는 주소
		{
			return Results.NotFound(new { sError = $"없는 서버: {sKey}" });
		}

		return Results.Ok(new { kServer.sKey, kServer.bSuccess, kServer.bLobby, kServer.bCDS });
	}

	private IResult OnMonitor(string sKey)	// XLO 모니터 페이지 이동 (설정에 있는 서버만 → 임의 주소 이동 방지)
	{
		OperationServer?	kServer	= m_kMonitor.Find(sKey);

		if (kServer == null)
		{
			return Results.NotFound(new { sError = $"없는 서버: {sKey}" });
		}

		return Results.Redirect(OperationMonitor.GetMonitorUrl(kServer));
	}

	private async Task<IResult> OnPatch(long? nPatchTime)	// 패치 시작 시각 = nPatchTime (없음: 현재)
	{
		if (nPatchTime != null && GetTimeText(nPatchTime.Value) == "")	// 파라미터 검사 및 조기 반환 (0 이하·범위 밖)
		{
			return Results.BadRequest(new { sError = $"잘못된 unix 시각: {nPatchTime}" });
		}

		try
		{
			long	nTime	= await OperationRedis.SetPatchTime(nPatchTime ?? 0);

			Log.Write(INFO_, $"패치 시작 설정: {nTime} ({GetTimeText(nTime)})");

			return Results.Ok(new { nPatchTime = nTime, sPatchTime = GetTimeText(nTime) });
		}
		catch (Exception kException)	// Redis 오류
		{
			return GetErrorResult(kException);
		}
	}

	private async Task<IResult> OnCCU()	// 최고 동접 + 동접 기록 (Redis)
	{
		try
		{
			return Results.Ok(await OperationRedis.GetCCU());
		}
		catch (Exception kException)	// Redis 오류
		{
			return GetErrorResult(kException);
		}
	}

	private static async Task<IResult> OnRedis()	// Redis 연결 확인: PING 응답 시간 (실패: 503)
	{
		try
		{
			TimeSpan	kPing	= await Redis.GetDb().PingAsync();

			return Results.Ok(new { bConnect = true, nDB = Redis.GetDbNumber(), dPingMs = kPing.TotalMilliseconds, sTime = GetNowText() });
		}
		catch (Exception kException)	// 호스트 미설정·연결 실패·인증 실패·타임아웃 등
		{
			return Results.Json(new { bConnect = false, nDB = Redis.GetDbNumber(), sError = GetShortError(kException), sTime = GetNowText() }, statusCode: StatusCodes.Status503ServiceUnavailable);
		}
	}

	private static IResult GetErrorResult(Exception kException)	// 오류 응답 (503, sError: 페이지 표시)
	{
		return Results.Json(new { sError = GetShortError(kException) }, statusCode: StatusCodes.Status503ServiceUnavailable);
	}

	public static string GetZone()	// 운영 존: Manager:sZone (Manager.json, 서버 목록 ServerList 선택, 없음: 최상위 sZone)
	{
		return Config.GetString("Manager:sZone", Config.GetString("sZone"));
	}

	private static string GetShortError(Exception kException)	// 오류 메시지 (Redis 진단 정보 등 긴 메시지는 앞부분만)
	{
		string	sText	= kException.Message;

		return sText.Length > 200 ? sText.Substring(0, 200) + "..." : sText;
	}

	public static string GetTimeText(long nUnix)	// unix 초 → KST 시각 문자열 (0 이하·범위 밖: "")
	{
		if (nUnix <= 0 || nUnix > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
		{
			return "";
		}

		return DateTimeOffset.FromUnixTimeSeconds(nUnix).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss");
	}

	public static string ManagerHTML()	// 페이지 운영 탭 스크립트 (Web.IndexHtml 조립)
	{
		return """
		class Operation	// 운영 탭: 왼쪽 게임 서버 목록 (XLO 모니터 점검·패치 시각) + 오른쪽 동접·차트·모니터 (ServerManager 이식, this 없음 → 메서드를 이벤트 처리기로 바로 넘김, 데이터: textContent)
		{
		    static	GAME_NAME	= { Total: "Total", Baduki: "Baduki", Poker7: "7Poker", Holdem: "Holdem", Highlow: "Highlow", Blackjack: "Blackjack" };	// 최고 동접 게임명 (동접 기록 표 제목과 같게)
		    static	CCU_FIELD	= ["nBaduki", "nPoker7", "nHighlow", "nHoldem", "nBlackjack"];			// 동접 기록 표 칸 (Total 앞, 표 제목 순서)
		    static	CHART_SET	= [["baduki", "nBaduki", "#df0c24"], ["poker7", "nPoker7", "#006edb"], ["highlow", "nHighlow", "#b88700"], ["holdem", "nHoldem", "#30a147"], ["blackjack", "nBlackjack", "#894ceb"], ["total", "nTotal", "#808fa3"]];	// 차트 [라벨, 필드, 색] (Primer data 색)
		    static	CHART_URL	= "https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js";	// Chart.js (차트 처음 그릴 때 불러옴, 인터넷 필요)
		    static	SPLIT_MIN	= 120;																	// 왼쪽·오른쪽 최소 폭 (px, 세로선 드래그)

		    static	kServerTitle	= document.getElementById("ManagerServerTitle");	// 서버 목록 제목 (운영·전체 수)
		    static	kRefresh		= document.getElementById("ManagerRefresh");		// 서버 새로 고침 (전체 점검)
		    static	kProgress		= document.getElementById("ManagerProgress");		// 서버 점검 진행 팝업
		    static	kError			= document.getElementById("ManagerError");			// 서버 목록 오류
		    static	kServerBody		= document.getElementById("ManagerServerBody");		// 운영 서버 줄
		    static	kDownCount		= document.getElementById("ManagerDownCount");		// 미운영 수
		    static	kDownBody		= document.getElementById("ManagerDownBody");		// 미운영 서버 줄
		    static	kPatchTime		= document.getElementById("ManagerPatchTime");		// 패치 시간 (시각 (unix))
		    static	kPatchDialog	= document.getElementById("ManagerPatchDialog");	// 패치 시간 팝업
		    static	kPatchInput		= document.getElementById("ManagerPatchInput");		// 패치 시간 입력 (unix)
		    static	kSplitter		= document.getElementById("ManagerSplitter");		// 왼쪽·오른쪽 사이 세로선
		    static	kCcuButton		= document.getElementById("ManagerCcuButton");		// 동접 하위 탭
		    static	kChartButton	= document.getElementById("ManagerChartButton");	// 차트 하위 탭
		    static	kMonitorButton	= document.getElementById("ManagerMonitorButton");	// 모니터 하위 탭
		    static	kCcuPanel		= document.getElementById("ManagerCcuPanel");		// 동접 창
		    static	kCcuRefresh		= document.getElementById("ManagerCcuRefresh");		// 동접 새로 고침
		    static	kCcuProgress	= document.getElementById("ManagerCcuProgress");	// 동접 조회 진행 팝업
		    static	kCcuError		= document.getElementById("ManagerCcuError");		// 동접 오류
		    static	kMaxBody		= document.getElementById("ManagerMaxBody");		// 최고 동접 줄
		    static	kCcuBody		= document.getElementById("ManagerCcuBody");		// 동접 기록 줄
		    static	kChartPanel		= document.getElementById("ManagerChartPanel");		// 차트 창
		    static	kChartError		= document.getElementById("ManagerChartError");		// 차트 오류
		    static	kMonitorPanel	= document.getElementById("ManagerMonitorPanel");	// 모니터 창
		    static	kMonitorTitle	= document.getElementById("ManagerMonitorTitle");	// 모니터 창 제목 (서버 키)
		    static	kMonitorFrame	= document.getElementById("ManagerMonitorFrame");	// XLO 모니터 (서버 URL 클릭)

		    static	bOpened		= false;	// 처음 열기 끝 (서버 점검·동접 조회 1번)
		    static	kServerInfo	= null;		// 마지막 /api/manager/server 응답
		    static	arrCcu		= [];		// 마지막 동접 기록 (최신순)
		    static	kChart		= null;		// Chart.js 객체
		    static	kChartLoad	= null;		// Chart.js 불러오기 (Promise, 실패: 다음에 다시)
		    static	sMonitorKey	= "";		// 모니터 창에 연 서버 키 (ip:port)

		    static Open()	// 운영 탭 버튼: 운영 창 (처음: 서버 점검·동접 조회)
		    {
		        ShowPanel("manager");

		        if (!Operation.bOpened)
		        {
		            Operation.bOpened = true;

		            Operation.LoadServer();
		            Operation.LoadCcu();
		        }
		    }

		    static async Fetch(sUrl, kOption)	// 요청 → JSON (HTTP 오류: throw 서버 오류 글 sError, 없음: "HTTP 상태")
		    {
		        const	kResponse	= await fetch(sUrl, kOption);

		        if (!kResponse.ok)
		        {
		            const	kData	= await kResponse.json().catch(() => ({}));	// 서버 오류 글 (JSON 아님: 없음)

		            throw new Error(kData.sError || `HTTP ${kResponse.status}`);
		        }

		        return await kResponse.json();
		    }

		    static ShowError(kBox, sText)	// 오류 상자 (빈 문자열: 숨김)
		    {
		        kBox.textContent	= sText;
		        kBox.hidden			= sText === "";
		    }

		    static TurnButton(kButton)	// 새로 고침 아이콘 버튼: 화살표 1바퀴 (누를 때마다 처음부터)
		    {
		        kButton.classList.remove("turn");
		        void kButton.offsetWidth;	// 회전 처음부터 다시 (애니메이션 재시작)
		        kButton.classList.add("turn");
		    }

		    static StartProgress(kButton, kProgress, sText)	// 진행 표시 (상자 가운데 팝업 상태 · N초, X초 이상 걸릴 때만, 실행 버튼 꺼짐), 반환: 끝 함수
		    {
		        const	nBegin	= performance.now();	// 시작 (진행 초)

		        const funcShow = () =>	// 진행 초 갱신
		        {
		            kProgress.textContent = `${sText} · ${Math.floor((performance.now() - nBegin) / 1000)}초`;
		        };

		        kButton.disabled = true;

		        funcShow();

		        const	nTimer	= setInterval(funcShow, 1000);
		        const	nPopup	= setTimeout(() => { funcShow(); kProgress.hidden = false; }, PROGRESS_DELAY_MS);

		        return () =>
		        {
		            clearInterval(nTimer);
		            clearTimeout(nPopup);

		            kButton.disabled	= false;
		            kProgress.hidden	= true;
		        };
		    }

		    static GetZoneClass(sZone)	// 존 칩 클래스 (대소문자 무시, 포함 여부: Real 빨강 > Alpha 파랑 > Dev 초록, 그 외·없음: 회색)
		    {
		        const	sLower	= (sZone || "").toLowerCase();

		        return sLower.includes("real") ? "zone-real" : (sLower.includes("alpha") ? "zone-alpha" : (sLower.includes("dev") ? "zone-dev" : "off"));
		    }

		    static GetNameNumber(sName)	// 서버 이름의 첫 3자리 이상 숫자 (예: 101 바둑이 → 101, B2_201 → 201, 없음: null)
		    {
		        const	kMatch	= /\d{3,}/.exec(sName || "");

		        return kMatch != null ? Number(kMatch[0]) : null;
		    }

		    static GetNameGroup(sName)	// 이름 숫자 100 단위 (예: 101 → 1, 1101 → 11, 없음: null)
		    {
		        const	nNumber	= Operation.GetNameNumber(sName);

		        return nNumber === null ? null : Math.floor(nNumber / 100);
		    }

		    static CompareServer(kA, kB)	// 숫자 정렬: 3300번대 → 10000 이상 맨 아래 → 이름 숫자 (없음: 숫자 있는 서버 뒤) → 이름 → IP → 포트
		    {
		        const funcKey = (kServer) =>
		        {
		            const	nNumber	= Operation.GetNameNumber(kServer.sName) ?? Infinity;

		            return [nNumber >= 3300 && nNumber < 3400 ? 1 : (nNumber >= 10000 && nNumber !== Infinity ? 2 : 0), nNumber, kServer.sName, kServer.sIP, kServer.nPort];
		        };
		        const	arrA	= funcKey(kA);
		        const	arrB	= funcKey(kB);

		        for (let nIndex = 0; nIndex < arrA.length; ++nIndex)
		        {
		            if (arrA[nIndex] !== arrB[nIndex])
		            {
		                return arrA[nIndex] < arrB[nIndex] ? -1 : 1;
		            }
		        }

		        return 0;
		    }

		    static AddChip(kCell, sText, sClass)	// 칩 (Primer Label, textContent)
		    {
		        const	kChip	= document.createElement("span");

		        kChip.className		= `chip ${sClass}`;
		        kChip.textContent	= sText;

		        kCell.append(kChip);
		    }

		    static AddEmpty(kBody, sText, nColumn)	// 빈 표 한 줄
		    {
		        AddCell(kBody.insertRow(), sText, "empty").colSpan = nColumn;
		    }

		    static AddSpacer(kBody, arrServer, nIndex, nColumn)	// 앞 서버와 이름 숫자 100 단위가 다르면 빈 줄 (사이만 띄움)
		    {
		        if (nIndex === 0 || Operation.GetNameGroup(arrServer[nIndex].sName) === Operation.GetNameGroup(arrServer[nIndex - 1].sName))
		        {
		            return;
		        }

		        const	kRow	= kBody.insertRow();

		        kRow.className = "spacer";

		        AddCell(kRow, "", "").colSpan = nColumn;
		    }

		    static async LoadServer()	// 전체 점검 후 목록 (점검 대기 시간 = 가장 느린 서버)
		    {
		        if (Operation.kRefresh.disabled)	// 점검 중
		        {
		            return;
		        }

		        const	funcEnd	= Operation.StartProgress(Operation.kRefresh, Operation.kProgress, "서버 갱신 중");

		        try
		        {
		            Operation.kServerInfo = await Operation.Fetch("/api/manager/server");

		            Operation.RenderServer();
		        }
		        catch (kError)
		        {
		            Operation.ShowError(Operation.kError, `서버 점검 실패: ${ErrorText(kError)}`);
		        }
		        finally
		        {
		            funcEnd();
		        }
		    }

		    static RenderServer()	// 운영 서버 표 + 미운영 서버 목록 (숫자 정렬, 3300번대 → 10000 이상 맨 아래)
		    {
		        const	kData		= Operation.kServerInfo;
		        const	arrSorted	= [...kData.listServer].sort(Operation.CompareServer);
		        const	arrUp		= arrSorted.filter(kServer => kServer.bSuccess);
		        const	arrDown		= arrSorted.filter(kServer => !kServer.bSuccess);

		        Operation.kServerTitle.textContent	= `운영 서버 ${kData.nOperating} / 전체 ${kData.listServer.length}`;
		        Operation.kPatchTime.textContent	= kData.nPatchTime ? `${kData.sPatchTime.slice(5)} (${kData.nPatchTime})` : "-";	// MM-dd HH:mm:ss (unix)
		        Operation.kDownCount.textContent	= arrDown.length;

		        Operation.ShowError(Operation.kError, kData.sError ? `패치 시간 조회 실패: ${kData.sError}` : "");

		        Operation.kServerBody.replaceChildren();
		        Operation.kDownBody.replaceChildren();

		        if (arrUp.length === 0)
		        {
		            Operation.AddEmpty(Operation.kServerBody, "운영 서버 없음", 6);
		        }

		        arrUp.forEach((kServer, nIndex) =>
		        {
		            Operation.AddSpacer(Operation.kServerBody, arrUp, nIndex, 6);

		            const	kRow	= Operation.kServerBody.insertRow();
		            const	kTag	= document.createElement("span");	// 로비·룸
		            const	kLink	= document.createElement("a");		// 모니터 링크 (오른쪽 모니터 창)

		            Operation.AddChip(AddCell(kRow, "", ""), kServer.sZone, Operation.GetZoneClass(kServer.sZone));

		            kTag.className		= "tag";
		            kTag.textContent	= kServer.bLobby ? "Lobby" : "Room";

		            AddCell(kRow, kServer.sName, "").append(kTag);

		            kLink.className		= "mono";
		            kLink.href			= "api/manager/monitor?" + new URLSearchParams({ sKey: kServer.sKey });	// 상대 주소: ToolManager 경유 (/proxy/N/) 아래로 감 (루트 기준 /api 링크: 경유 스크립트 변환 안 됨 → ToolManager 주소 404, 빈 창)
		            kLink.target		= "ManagerMonitor";
		            kLink.dataset.key	= kServer.sKey;
		            kLink.textContent	= kServer.sIP;

		            AddCell(kRow, "", "").append(kLink);
		            AddCell(kRow, kServer.sStartTime, "mono");

		            Operation.AddChip(AddCell(kRow, "", ""), String(kServer.nAuth), "mono " + (kServer.bLobby && !kServer.bCDS ? "off" : (kServer.nAuth <= kData.nPatchTime ? "before" : "after")));	// AuthKey: 패치 전 빨강·후 초록·CDS 제외 로비 회색

		            const	kCds	= AddCell(kRow, "", "");

		            if (kServer.bLobby)
		            {
		                Operation.AddChip(kCds, kServer.bCDS ? "등록" : "제외", kServer.bCDS ? "cds" : "off");
		            }
		        });

		        if (arrDown.length === 0)
		        {
		            Operation.AddEmpty(Operation.kDownBody, "없음", 3);
		        }

		        arrDown.forEach((kServer, nIndex) =>
		        {
		            Operation.AddSpacer(Operation.kDownBody, arrDown, nIndex, 3);

		            const	kRow	= Operation.kDownBody.insertRow();

		            kRow.className = "down";

		            Operation.AddChip(AddCell(kRow, "", ""), kServer.sZone, "off");
		            AddCell(kRow, kServer.sName, "");
		            AddCell(kRow, kServer.sIP, "mono");
		        });
		    }

		    static OpenPatch()	// 패치 시간 팝업 (기본: 지금 unix, Enter: 확인, Esc·취소: 닫기)
		    {
		        Operation.kPatchInput.value = Math.floor(Date.now() / 1000);

		        Operation.kPatchDialog.showModal();
		        Operation.kPatchInput.select();
		    }

		    static async SavePatch()	// 패치 시간 = 입력 unix (이후 기동한 서버 AuthKey: 초록)
		    {
		        const	nPatchTime	= Number(Operation.kPatchInput.value.trim());

		        Operation.kPatchDialog.close();

		        if (!Number.isSafeInteger(nPatchTime) || nPatchTime <= 0)	// 양의 정수만
		        {
		            Operation.ShowError(Operation.kError, "패치 시작 실패: Bad Input");

		            return;
		        }

		        try
		        {
		            const	kData	= await Operation.Fetch("/api/manager/patch?" + new URLSearchParams({ nPatchTime }), { method: "POST" });

		            if (Operation.kServerInfo != null)
		            {
		                Operation.kServerInfo.nPatchTime = kData.nPatchTime;
		                Operation.kServerInfo.sPatchTime = kData.sPatchTime;

		                Operation.RenderServer();
		            }
		        }
		        catch (kError)
		        {
		            Operation.ShowError(Operation.kError, `패치 시작 실패: ${ErrorText(kError)}`);
		        }
		    }

		    static async CheckCds()	// 모니터 페이지 이동 (CDS 등록·해제 등) 뒤 그 서버 CDS 확인 → 바뀜: 서버 새로 고침
		    {
		        const	kServer	= Operation.kServerInfo?.listServer.find(kItem => kItem.sKey === Operation.sMonitorKey);

		        if (kServer == null || !kServer.bLobby || Operation.kRefresh.disabled)	// 로비만 (CDS: 로비 전용), 새로 고침 중: 생략
		        {
		            return;
		        }

		        try
		        {
		            const	kData	= await Operation.Fetch("/api/manager/cds?" + new URLSearchParams({ sKey: Operation.sMonitorKey }));

		            if (kData.bSuccess && kData.bCDS !== kServer.bCDS)
		            {
		                Operation.LoadServer();
		            }
		        }
		        catch (kError)
		        {
		            Operation.ShowError(Operation.kError, `CDS 확인 실패: ${ErrorText(kError)}`);
		        }
		    }

		    static async LoadCcu()	// 최고 동접 + 동접 기록 (차트: 그린 적 있음·보는 중만 다시)
		    {
		        if (Operation.kCcuRefresh.disabled)	// 조회 중
		        {
		            return;
		        }

		        const	funcEnd	= Operation.StartProgress(Operation.kCcuRefresh, Operation.kCcuProgress, "동접 조회 중");

		        try
		        {
		            const	kData	= await Operation.Fetch("/api/manager/ccu");

		            Operation.arrCcu = kData.listCCU;

		            Operation.ShowError(Operation.kCcuError, "");
		            Operation.RenderCcu(kData);

		            if (Operation.kChart != null || !Operation.kChartPanel.hidden)
		            {
		                Operation.DrawChart();
		            }
		        }
		        catch (kError)
		        {
		            Operation.ShowError(Operation.kCcuError, `동접 조회 실패: ${ErrorText(kError)}`);
		        }
		        finally
		        {
		            funcEnd();
		        }
		    }

		    static RenderCcu(kData)	// 최고 동접 표 + 동접 기록 표
		    {
		        Operation.kMaxBody.replaceChildren();
		        Operation.kCcuBody.replaceChildren();

		        for (const kMax of kData.listMax)
		        {
		            const	kRow	= Operation.kMaxBody.insertRow();

		            AddCell(kRow, Operation.GAME_NAME[kMax.sGame] ?? kMax.sGame, "");
		            AddCell(kRow, kMax.sTime, "mono");
		            AddCell(kRow, kMax.nCnt.toLocaleString(), "num");
		        }

		        if (Operation.arrCcu.length === 0)
		        {
		            Operation.AddEmpty(Operation.kCcuBody, "기록 없음", 7);
		        }

		        for (const kCcu of Operation.arrCcu)
		        {
		            const	kRow	= Operation.kCcuBody.insertRow();
		            const	kTotal	= document.createElement("b");

		            AddCell(kRow, kCcu.sLogTime, "mono");

		            for (const sField of Operation.CCU_FIELD)
		            {
		                AddCell(kRow, kCcu[sField].toLocaleString(), "num");
		            }

		            kTotal.textContent = kCcu.nTotal.toLocaleString();

		            AddCell(kRow, "", "num").append(kTotal);
		        }
		    }

		    static LoadChartJs()	// Chart.js 불러오기 (처음 1번, 실패: 다음에 다시)
		    {
		        if (window.Chart)
		        {
		            return Promise.resolve();
		        }

		        if (Operation.kChartLoad == null)
		        {
		            Operation.kChartLoad = new Promise((funcOk, funcFail) =>
		            {
		                const	kScript	= document.createElement("script");

		                kScript.src		= Operation.CHART_URL;
		                kScript.onload	= () => funcOk();
		                kScript.onerror	= () =>
		                {
		                    Operation.kChartLoad = null;

		                    kScript.remove();
		                    funcFail(new TypeError("Failed to fetch"));
		                };

		                document.head.append(kScript);
		            });
		        }

		        return Operation.kChartLoad;
		    }

		    static async DrawChart()	// 동접 차트 (시간순, 기본: 누적 막대, 다시 그림: 막대·선·누적 그대로)
		    {
		        try
		        {
		            await Operation.LoadChartJs();
		        }
		        catch (kError)
		        {
		            Operation.ShowError(Operation.kChartError, `Chart.js 로딩 실패: ${ErrorText(kError)}`);

		            return;
		        }

		        Operation.ShowError(Operation.kChartError, "");

		        const	arrAsc	= [...Operation.arrCcu].reverse();
		        const kData		=
		        {
		            labels:		arrAsc.map(kCcu => kCcu.sLogTime),
		            datasets:	Operation.CHART_SET.map(([sLabel, sField, sColor]) => ({ label: sLabel, data: arrAsc.map(kCcu => kCcu[sField]), backgroundColor: sColor + "80", borderColor: sColor, borderWidth: 1, pointRadius: 0 })),
		        };

		        if (Operation.kChart != null)
		        {
		            kData.datasets.forEach((kSet, nIndex) => kSet.type = Operation.kChart.data.datasets[nIndex].type);

		            Operation.kChart.data = kData;
		            Operation.kChart.update();

		            return;
		        }

		        Chart.defaults.color = getComputedStyle(document.documentElement).getPropertyValue("--fgColor-muted").trim();

		        Operation.kChart = new Chart(document.getElementById("ManagerChartCanvas"),
		        {
		            type:		"bar",
		            data:		kData,
		            options:
		            {
		                responsive: true, maintainAspectRatio: false, animation: false,
		                interaction:	{ mode: "index", intersect: false },
		                plugins:		{ legend: { position: "top" }, title: { display: true, text: "Concurrent User" } },
		                scales:			{ x: { stacked: true }, y: { stacked: true, beginAtZero: true } },
		            },
		        });
		    }

		    static ToggleStack()	// 누적 ↔ 개별
		    {
		        if (Operation.kChart == null)
		        {
		            return;
		        }

		        const	bStack	= !Operation.kChart.options.scales.y.stacked;

		        Operation.kChart.options.scales.x.stacked = bStack;
		        Operation.kChart.options.scales.y.stacked = bStack;

		        Operation.kChart.update();
		    }

		    static ToggleLine()	// 막대 ↔ 선
		    {
		        if (Operation.kChart == null)
		        {
		            return;
		        }

		        const	sType	= Operation.kChart.data.datasets[0].type === "line" ? "bar" : "line";

		        Operation.kChart.data.datasets.forEach(kSet => kSet.type = sType);
		        Operation.kChart.update();
		    }

		    static ShowOnly(bTotal)	// true: total 만, false: 전체
		    {
		        if (Operation.kChart == null)
		        {
		            return;
		        }

		        Operation.kChart.data.datasets.forEach((kSet, nIndex) => Operation.kChart.setDatasetVisibility(nIndex, !bTotal || kSet.label === "total"));
		        Operation.kChart.update();
		    }

		    static ResetState()	// F5 리셋: 최초 입장 상태 (하위 탭 동접 → 서버 점검·동접 조회 다시, 조회 중: 그대로)
		    {
		        Operation.bOpened = true;

		        Operation.ShowSide("ccu");
		        Operation.kRefresh.click();		// 서버 새로 고침 (꺼짐: 점검 중 → 안 함)
		        Operation.kCcuRefresh.click();	// 동접 새로 고침
		    }

		    static ShowSide(sPanel)	// 오른쪽 하위 창 바꾸기 (ccu: 동접, chart: 차트, monitor: 모니터, 숨긴 창 내용 유지)
		    {
		        Operation.kCcuPanel.hidden		= sPanel !== "ccu";
		        Operation.kChartPanel.hidden	= sPanel !== "chart";
		        Operation.kMonitorPanel.hidden	= sPanel !== "monitor";

		        Operation.kCcuButton.classList.toggle("cur", sPanel === "ccu");
		        Operation.kChartButton.classList.toggle("cur", sPanel === "chart");
		        Operation.kMonitorButton.classList.toggle("cur", sPanel === "monitor");

		        if (document.activeElement?.closest("#ManagerSideTab") != null && !document.activeElement.classList.contains("cur"))	// 이전 탭 버튼 포커스 테두리 제거
		        {
		            document.activeElement.blur();
		        }

		        if (sPanel === "chart")	// 처음: 그림 (Chart.js 불러옴), 숨김 중 그린 차트: 크기 맞춤
		        {
		            if (Operation.kChart == null)
		            {
		                Operation.DrawChart();
		            }
		            else
		            {
		                Operation.kChart.resize();
		            }
		        }
		    }

		    static OnServerClick(kEvent)	// 모니터 링크 (URL): 오른쪽 모니터 창에 표시
		    {
		        const	kLink	= kEvent.target.closest("a[data-key]");

		        if (kLink == null)
		        {
		            return;
		        }

		        Operation.sMonitorKey				= kLink.dataset.key;
		        Operation.kMonitorTitle.textContent	= `${kLink.dataset.key} - XLO 모니터`;

		        Operation.ShowSide("monitor");
		    }

		    static UpdateGutter()	// 긴 목록 상자: 내용이 상자 높이 넘음 (세로 바) 일 때만 바 자리
		    {
		        for (const kScroll of kManagerPanel.querySelectorAll(".scroll.tall"))
		        {
		            kScroll.classList.toggle("vbar", kScroll.scrollHeight > kScroll.clientHeight);
		        }
		    }

		    static OnSplitterDown(kEvent)	// 세로선 좌우 드래그: 왼쪽 폭 변경, 오른쪽은 나머지 (각각 최소 SPLIT_MIN px)
		    {
		        const	kSplitter	= Operation.kSplitter;
		        const	nStartX		= kEvent.clientX;	// 드래그 시작 위치
		        const	nWidthStart	= kSplitter.previousElementSibling.getBoundingClientRect().width;	// 왼쪽 시작 폭
		        const	nWidthMax	= kManagerPanel.getBoundingClientRect().width - kSplitter.getBoundingClientRect().width - Operation.SPLIT_MIN;	// 왼쪽 최대 폭 (오른쪽 최소 남김)

		        const funcMove = (kMove) =>	// 끈 만큼 왼쪽 폭 변경
		        {
		            kManagerPanel.style.setProperty("--managerWidth", Math.min(nWidthMax, Math.max(Operation.SPLIT_MIN, nWidthStart + kMove.clientX - nStartX)) + "px");
		        };

		        const funcUp = () =>	// 드래그 끝: 이벤트 해제
		        {
		            kSplitter.classList.remove("drag");
		            kSplitter.removeEventListener("pointermove", funcMove);
		            kSplitter.removeEventListener("pointerup", funcUp);
		            kSplitter.removeEventListener("pointercancel", funcUp);
		        };

		        kSplitter.setPointerCapture(kEvent.pointerId);	// 세로선 밖으로 나가도 계속 추적
		        kSplitter.classList.add("drag");
		        kSplitter.addEventListener("pointermove", funcMove);
		        kSplitter.addEventListener("pointerup", funcUp);
		        kSplitter.addEventListener("pointercancel", funcUp);

		        kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		    }

		    static Init()	// 이벤트 연결 (페이지 시작 1번)
		    {
		        Operation.kRefresh.addEventListener("click", () => { Operation.TurnButton(Operation.kRefresh); Operation.LoadServer(); });	// 서버 새로 고침: 화살표 1바퀴 + 전체 점검
		        document.getElementById("ManagerPatchButton").addEventListener("click", Operation.OpenPatch);	// 패치 시작: 패치 시간 팝업
		        document.getElementById("ManagerPatchOk").addEventListener("click", Operation.SavePatch);
		        document.getElementById("ManagerPatchCancel").addEventListener("click", () => Operation.kPatchDialog.close());

		        Operation.kPatchDialog.addEventListener("keydown", (kEvent) =>	// 패치 팝업 Enter: 확인 (한글 조합 중 제외, 버튼: 그 버튼)
		        {
		            if (kEvent.key === "Enter" && !kEvent.isComposing && kEvent.target.tagName !== "BUTTON")
		            {
		                kEvent.preventDefault();
		                Operation.SavePatch();
		            }
		        });

		        Operation.kCcuRefresh.addEventListener("click", () => { Operation.TurnButton(Operation.kCcuRefresh); Operation.LoadCcu(); });	// 동접 새로 고침: 화살표 1바퀴 + 표·차트
		        Operation.kCcuButton.addEventListener("click", () => Operation.ShowSide("ccu"));			// 동접 하위 탭
		        Operation.kChartButton.addEventListener("click", () => Operation.ShowSide("chart"));		// 차트 하위 탭
		        Operation.kMonitorButton.addEventListener("click", () => Operation.ShowSide("monitor"));	// 모니터 하위 탭
		        document.getElementById("ManagerChartStack").addEventListener("click", Operation.ToggleStack);
		        document.getElementById("ManagerChartLine").addEventListener("click", Operation.ToggleLine);
		        document.getElementById("ManagerChartTotal").addEventListener("click", () => Operation.ShowOnly(true));
		        document.getElementById("ManagerChartAll").addEventListener("click", () => Operation.ShowOnly(false));
		        Operation.kMonitorFrame.addEventListener("load", Operation.CheckCds);	// 모니터 페이지 로딩 끝마다 CDS 변경 확인
		        Operation.kServerBody.addEventListener("click", Operation.OnServerClick);	// 모니터 링크: 오른쪽 모니터 창
		        Operation.kSplitter.addEventListener("pointerdown", Operation.OnSplitterDown);

		        const	kGutterObserver	= new ResizeObserver(() => requestAnimationFrame(Operation.UpdateGutter));	// 목록·창 크기 바뀜: 바 자리 다시 (다음 그림 때)

		        for (const kScroll of kManagerPanel.querySelectorAll(".scroll.tall"))
		        {
		            kGutterObserver.observe(kScroll);

		            if (kScroll.firstElementChild != null)	// 표 (줄 늘고 줄어듦)
		            {
		                kGutterObserver.observe(kScroll.firstElementChild);
		            }
		        }
		    }
		}
		""";
	}
}

record OperationServer	// 게임 서버: 설정 Manager:ServerList:{존} 항목 + XLO 모니터 점검 결과
{
	public string	sKey		{ get; init; } = "";	// 서버 키 (ip:port)
	public string	sZone		{ get; init; } = "";	// 존 (예: Dev, Alpha, Real)
	public string	sName		{ get; init; } = "";	// 서버 이름 (게임 구분: 바둑이·7포커·홀덤·하이로우·럭키랜드 포함 여부)
	public string	sIP			{ get; init; } = "";	// IP
	public int		nPort		{ get; init; }			// XLO 모니터 포트
	public bool		bSuccess	{ get; init; }			// 운영중 (XLO 모니터 접근 성공)
	public bool		bLobby		{ get; init; }			// 로비 서버 (XLobby Monitor Page, 아니면 룸)
	public long		nAuth		{ get; init; }			// XLO 인증키 (서버 시작 시각 unix)
	public string	sStartTime	{ get; init; } = "";	// 서버 시작 시각 (KST)
	public bool		bCDS		{ get; init; }			// CDS 등록 여부 (로비만)
	public int		nUser		{ get; init; }			// 현재 접속자 수 (로비만)
	public string	sVersion	{ get; init; } = "";	// 버전 (메모리 보관, 재시작 시 1.0)
}

record OperationCCU(string sLogTime, int nTotal, int nBaduki, int nPoker7, int nHoldem, int nHighlow, int nBlackjack);	// 동접 기록 1건 (X분 단위)

class OperationMonitor	// 게임 서버 점검: XLO 모니터 페이지로 운영·로비·인증키·접속자·CDS 등록 확인
{
	private const int		m_nTimeoutMs	= 30000;					// XLO 모니터 요청 제한 기본값 (ms, 연결·응답, 내려간 서버 대기 상한, 설정 Manager:nTimeout 없음)
	private const string	DEFAULT_VERSION	= "1.0";					// 기본 버전
	private const string	LOBBY_TEXT		= "XLobby Monitor Page";	// 로비 서버 구분 문구 (RedirectHome)
	private const string	USER_TEXT		= "User Instance Count";	// 접속자 수 앞 문구 (RedirectHome)
	private const string	CDS_TEXT		= "<br>Registered<br>";		// CDS 등록 문구 (Operator)

	private readonly OperationServer[]						m_arrConfig;	// 설정 서버 목록 (정렬)
	private readonly HttpClient								m_kHttp;		// XLO 모니터 요청 (연결·응답 제한: Manager:nTimeout)
	private readonly ConcurrentDictionary<string, string>	m_hmapVersion;	// 서버 키 → 버전 (메모리, 재시작 시 초기화)
	private readonly object									m_lockCheck;	// 점검 시작 직렬화
	private Task?											m_kCheck;		// 진행 중·마지막 점검
	private volatile OperationServer[]						m_arrServer;	// 마지막 점검 결과 (정렬)
	private volatile string									m_sCheckTime;	// 마지막 점검 시각 (KST, 점검 전: "")

	public OperationMonitor()
	{
		TimeSpan	kTimeout	= TimeSpan.FromMilliseconds(Config.GetInt("Manager:nTimeout", m_nTimeoutMs));

		m_arrConfig		= LoadConfig();
		m_kHttp			= new HttpClient(new SocketsHttpHandler { ConnectTimeout = kTimeout }) { Timeout = kTimeout };
		m_hmapVersion	= new ConcurrentDictionary<string, string>();
		m_lockCheck		= new object();
		m_arrServer		= m_arrConfig;
		m_sCheckTime	= "";
	}

	public static void CheckConfig(IConfiguration kFile, List<string> listError)	// 시작 검사 (ConfigCheck): 파일의 Manager:ServerList 존마다 서버 (sIP·nPort 1 ~ 65535·주소 겹침)
	{
		foreach (IConfigurationSection kZone in kFile.GetSection("Manager:ServerList").GetChildren())
		{
			HashSet<string>	setKey	= new HashSet<string>(StringComparer.OrdinalIgnoreCase);	// 존 안 주소 겹침

			foreach (IConfigurationSection kSection in kZone["sIP"] != null ? new[] { kZone } : kZone.GetChildren())
			{
				string	sIP		= kSection["sIP"] ?? "";
				string	sPath	= kSection.Path.Replace(':', '.');

				if (sIP == "" || !int.TryParse(kSection["nPort"], out int nPort) || nPort <= 0 || nPort > 65535)
				{
					listError.Add($"{sPath}: sIP 없음·nPort 1 ~ 65535");

					continue;
				}

				if (!setKey.Add($"{sIP}:{nPort}"))
				{
					listError.Add($"{sPath}: 주소 겹침 ({sIP}:{nPort})");
				}
			}
		}
	}

	private static OperationServer[] LoadConfig()	// 설정 Manager:ServerList:{Manager:sZone} (Manager.json) → 서버 목록 (sIP 없음·nPort 오류·주소 중복은 제외, 정렬)
	{
		string	sGroup	= TabOperation.GetZone();	// 사용할 존 (Dev | Alpha | Real, Manager:sZone, 없음: sZone)

		if (sGroup == "")	// 존 미설정
		{
			Log.Write(WARN_, "운영 서버: 존 미설정 (Manager:sZone·sZone)");

			return Array.Empty<OperationServer>();
		}

		Dictionary<string, OperationServer>	hmapServer	= new Dictionary<string, OperationServer>();	// 서버 키 → 서버
		IConfigurationSection				kZone		= Config.GetSection(ConfigurationPath.Combine("Manager:ServerList", sGroup));	// 존 서버 (객체: 1대, 배열: 여러 대)

		foreach (IConfigurationSection kSection in kZone["sIP"] != null ? new[] { kZone } : kZone.GetChildren())
		{
			string	sIP	= kSection["sIP"] ?? "";

			if (sIP == "" || !int.TryParse(kSection["nPort"], out int nPort) || nPort <= 0 || nPort > 65535)	// 필수 값 오류
			{
				Log.Write(ERROR, $"운영 서버 설정 오류: {kSection.Path} (sIP, nPort 확인)");

				continue;
			}

			string sKey = $"{sIP}:{nPort}";

			if (hmapServer.ContainsKey(sKey))	// 같은 주소 중복
			{
				Log.Write(WARN_, $"운영 서버 설정 중복: {kSection.Path} ({sKey})");

				continue;
			}

			hmapServer[sKey] = new OperationServer
			{
				sKey	= sKey,
				sZone	= sGroup,
				sName	= kSection["sName"] ?? "",
				sIP		= sIP,
				nPort	= nPort,
			};
		}

		Log.Write(INFO_, $"운영 서버: {sGroup} {hmapServer.Count}대 (Manager.json)");

		return hmapServer.Values.OrderByDescending(kServer => kServer.sZone, StringComparer.Ordinal).ThenBy(kServer => kServer.sName, StringComparer.Ordinal).ThenBy(kServer => kServer.sIP, StringComparer.Ordinal).ThenBy(kServer => kServer.nPort).ToArray();	// 존 내림차순 → 이름 → IP → 포트
	}

	public int GetCount()	// 설정 서버 수
	{
		return m_arrConfig.Length;
	}

	public Task Check()	// 전체 서버 점검 (진행 중이면 그 점검 끝 대기 → 화면·타이머 동시 요청 시 중복 점검 방지)
	{
		lock (m_lockCheck)
		{
			if (m_kCheck == null || m_kCheck.IsCompleted)
			{
				m_kCheck = CheckAll();
			}

			return m_kCheck;
		}
	}

	private async Task CheckAll()	// 전체 서버 동시 점검 → 결과 교체
	{
		OperationServer[]	arrServer	= await Task.WhenAll(m_arrConfig.Select(CheckServer));	// 입력 순서 유지 (정렬 유지)

		m_arrServer		= arrServer;
		m_sCheckTime	= GetNowText();

		Log.Write(INFO_, $"운영 서버 점검: 전체 {arrServer.Length}, 운영중 {arrServer.Count(kServer => kServer.bSuccess)}");
	}

	private async Task<OperationServer> CheckServer(OperationServer kServer)	// XLO 모니터 점검: RedirectHome → 로비면 Operator 로 CDS 등록 확인 (실패: 내려감)
	{
		try
		{
			string	sHome	= await GetText(GetMonitorUrl(kServer));
			string	sAuth	= ParseAuth(sHome);
			bool	bLobby	= sHome.Contains(LOBBY_TEXT, StringComparison.Ordinal);

			long.TryParse(sAuth, out long nAuth);

			OperationServer kResult = kServer with
			{
				bSuccess	= true,
				bLobby		= bLobby,
				nAuth		= nAuth,
				sStartTime	= TabOperation.GetTimeText(nAuth),
				nUser		= bLobby ? ParseUser(sHome) : 0,
			};

			if (!bLobby)	// 룸 서버: CDS 확인 안 함
			{
				return kResult;
			}

			string sOperator = await GetText($"http://{kServer.sIP}:{kServer.nPort}/Operator?auth={Uri.EscapeDataString(sAuth)}");

			return kResult with { bCDS = sOperator.IndexOf(CDS_TEXT, StringComparison.Ordinal) > 0 };
		}
		catch (Exception kException)	// 연결 실패·제한 시간 등
		{
			Log.Write(WARN_, $"운영 서버 점검 실패: {kServer.sName} {kServer.sKey} ({kException.Message})");

			return kServer;
		}
	}

	private async Task<string> GetText(string sUrl)	// GET 응답 본문 (상태 코드 무관)
	{
		using HttpResponseMessage kResponse = await m_kHttp.GetAsync(sUrl);

		return await kResponse.Content.ReadAsStringAsync();
	}

	private static string ParseAuth(string sHome)	// 인증키: 첫 "auth=" 뒤 ' 전까지 (없음: "")
	{
		int	nStart	= sHome.IndexOf("auth=", StringComparison.Ordinal);

		if (nStart < 0)
		{
			return "";
		}

		nStart += "auth=".Length;

		int	nEnd	= sHome.IndexOf('\'', nStart);

		return nEnd < 0 ? "" : sHome.Substring(nStart, nEnd - nStart);
	}

	private static int ParseUser(string sHome)	// 접속자 수: USER_TEXT 뒤 X자 안의 <font color="#0000FF"> 277 </font> 숫자 (없음: 0)
	{
		int	nStart	= sHome.IndexOf(USER_TEXT, StringComparison.Ordinal);

		if (nStart < 0)
		{
			return 0;
		}

		nStart += USER_TEXT.Length;

		string	sTemp	= sHome.Substring(nStart, Math.Min(50, sHome.Length - nStart));
		int		nFont	= sTemp.IndexOf("FF", StringComparison.Ordinal);	// #0000FF"> 위치

		if (nFont < 0 || nFont + 4 > sTemp.Length)
		{
			return 0;
		}

		nFont += 4;	// FF"> 다음

		int	nEnd	= sTemp.IndexOf('<', nFont);

		if (nEnd < 0)
		{
			return 0;
		}

		return int.TryParse(sTemp.Substring(nFont, nEnd - nFont).Trim(), out int nUser) ? nUser : 0;
	}

	public OperationServer[] GetList()	// 마지막 점검 결과 (버전 반영)
	{
		return m_arrServer.Select(kServer => kServer with { sVersion = m_hmapVersion.TryGetValue(kServer.sKey, out string? sVersion) ? sVersion : DEFAULT_VERSION }).ToArray();
	}

	public string GetCheckTime()	// 마지막 점검 시각 (점검 전: "")
	{
		return m_sCheckTime;
	}

	public OperationServer? Find(string sKey)	// 설정 서버 조회 (없음: null)
	{
		return m_arrConfig.FirstOrDefault(kServer => kServer.sKey == sKey);
	}

	public async Task<OperationServer?> CheckOne(string sKey)	// 서버 1대 점검 (목록 결과는 그대로) (없는 서버: null)
	{
		OperationServer?	kServer	= Find(sKey);

		if (kServer == null)
		{
			return null;
		}

		return await CheckServer(kServer);
	}

	public bool SetVersion(string sKey, string sVersion)	// 버전 임시 설정 (메모리, 재시작 시 초기화) (false: 없는 서버)
	{
		if (Find(sKey) == null)
		{
			return false;
		}

		m_hmapVersion[sKey] = sVersion;

		return true;
	}

	public static string GetMonitorUrl(OperationServer kServer)	// XLO 모니터 주소 (auth = Manager:sCode 복호화 값, 없음·틀림: 비밀번호 입력 폼 → 운영만·인증키·접속자 없음)
	{
		return $"http://{kServer.sIP}:{kServer.nPort}/RedirectHome?auth={Uri.EscapeDataString(Config.GetString("Manager:sPass"))}";
	}
}

static class OperationRedis	// 운영 Redis 데이터 (키 앞: Redis:sManager + 운영 존 + ":", 예: HMX:Manager:Real:patch_start_dttm): patch_start_dttm (패치 시작), daily_날짜 (X분 단위 동접 해시), max_conn (게임별 최고 동접)
{
	public const int	TICK_MINUTE	= 5;	// 동접 기록 주기 (분, 정각 기준)

	private const int	SHOW_DAYS			= 3;	// 동접 기록 조회 기간 (일)
	private const int	DAILY_EXPIRE_DAYS	= 1000;	// daily 해시 만료 (일)

	private static readonly string[]	GAME_LIST	= { "Total", "Baduki", "Poker7", "Holdem", "Highlow", "Blackjack" };	// 게임 (동접 JSON 필드명, 소문자 = max_conn 필드 앞)

	private static readonly (string sWord, string sGame)[]	GAME_WORD	= { ("바둑이", "Baduki"), ("7포커", "Poker7"), ("홀덤", "Holdem"), ("하이로우", "Highlow"), ("럭키랜드", "Blackjack") };	// 서버 이름 포함 단어 → 게임 (앞에서부터 첫 일치)

	private static string GetPrefix()	// 관리 키 앞: 설정 Redis:sManager + 운영 존 (Manager:sZone, 없음: sZone) + ":" (반영 탭 sProcess 와 따로)
	{
		return Redis.GetZonePrefix("Redis:sManager", TabOperation.GetZone(), "HMX:Manager:");	// 기본: HMX:Manager:
	}

	public static async Task<long> GetPatchTime()	// 패치 시작 시각 (unix, 없음: 0)
	{
		string?	sValue	= await Redis.GetDb().StringGetAsync(GetPrefix() + "patch_start_dttm");

		return long.TryParse(sValue, out long nTime) ? nTime : 0;
	}

	public static async Task<long> SetPatchTime(long nTime)	// 패치 시작 시각 = nTime (0: 현재) (반환: unix)
	{
		if (nTime == 0)
		{
			nTime = GetNow().ToUnixTimeSeconds();
		}

		await Redis.GetDb().StringSetAsync(GetPrefix() + "patch_start_dttm", nTime);

		return nTime;
	}

	public static async Task Record(OperationServer[] arrServer, string sLogTime)	// 동접 기록: 운영중 로비 접속자 게임별 합산 → daily 해시 + 최고 동접 갱신
	{
		Dictionary<string, int>	hmapCCU	= GAME_LIST.ToDictionary(sGame => sGame, sGame => 0);	// 게임 → 접속자

		foreach (OperationServer kServer in arrServer)
		{
			if (!kServer.bSuccess || !kServer.bLobby)	// 운영중 로비만
			{
				continue;
			}

			string	sGame	= GetGame(kServer.sName);

			if (sGame != "")
			{
				hmapCCU[sGame] += kServer.nUser;
			}

			hmapCCU["Total"] += kServer.nUser;
		}

		IDatabase	kDb		= Redis.GetDb();
		string		sPrefix	= GetPrefix();
		string		sDaily	= sPrefix + "daily_" + sLogTime.Substring(0, 10);	// 날짜별 해시 (필드 = 기록 시각 yyyy-MM-dd HH:mm)
		string		sJson	= JsonSerializer.Serialize(hmapCCU);

		await kDb.HashSetAsync(sDaily, sLogTime, sJson);
		await kDb.KeyExpireAsync(sDaily, TimeSpan.FromDays(DAILY_EXPIRE_DAYS));

		Dictionary<string, string>	hmapMax	= ToMap(await kDb.HashGetAllAsync(sPrefix + "max_conn"));	// max_conn 필드 → 값

		foreach (KeyValuePair<string, int> kPair in hmapCCU)	// 최고 동접 갱신 (게임_time = 기록시각_게임, 게임_cnt = 접속자)
		{
			string	sGame	= kPair.Key.ToLowerInvariant();

			if (kPair.Value <= (int.TryParse(hmapMax.GetValueOrDefault(sGame + "_cnt"), out int nMax) ? nMax : 0))
			{
				continue;
			}

			await kDb.HashSetAsync(sPrefix + "max_conn", new HashEntry[] { new HashEntry(sGame + "_time", sLogTime + "_" + sGame), new HashEntry(sGame + "_cnt", kPair.Value) });
		}

		Log.Write(INFO_, $"동접 기록: {sLogTime} {sJson}");
	}

	public static async Task<object> GetCCU()	// 최고 동접 + 최근 SHOW_DAYS 일 동접 기록 (최신순)
	{
		IDatabase		kDb		= Redis.GetDb();
		string			sPrefix	= GetPrefix();
		DateTimeOffset	kNow	= GetNow();
		string			sFrom	= kNow.AddDays(-SHOW_DAYS).ToString("yyyy-MM-dd HH:mm");	// 조회 시작 시각

		List<OperationCCU>	listCCU	= new List<OperationCCU>();

		for (int nDay = SHOW_DAYS; nDay >= 0; --nDay)	// 날짜별 해시 (SHOW_DAYS 일 전 ~ 오늘)
		{
			foreach (HashEntry kEntry in await kDb.HashGetAllAsync(sPrefix + "daily_" + kNow.AddDays(-nDay).ToString("yyyy-MM-dd")))
			{
				OperationCCU?	kCCU	= ParseCCU(kEntry.Name.ToString(), kEntry.Value.ToString());

				if (kCCU == null || string.CompareOrdinal(kCCU.sLogTime, sFrom) < 0)	// 형식 오류·조회 기간 이전
				{
					continue;
				}

				listCCU.Add(kCCU);
			}
		}

		Dictionary<string, string>	hmapMax	= ToMap(await kDb.HashGetAllAsync(sPrefix + "max_conn"));	// max_conn 필드 → 값

		return new
		{
			listMax	= GAME_LIST.Select(sGame => new
			{
				sGame	= sGame,
				sTime	= hmapMax.GetValueOrDefault(sGame.ToLowerInvariant() + "_time", ""),
				nCnt	= int.TryParse(hmapMax.GetValueOrDefault(sGame.ToLowerInvariant() + "_cnt"), out int nValue) ? nValue : 0,
			}).ToArray(),
			listCCU	= listCCU.OrderByDescending(kItem => kItem.sLogTime, StringComparer.Ordinal).ToArray(),
		};
	}

	private static OperationCCU? ParseCCU(string sLogTime, string sJson)	// 동접 JSON ({"Total":0,"Baduki":0,...}) → 기록 (형식 오류: null)
	{
		try
		{
			Dictionary<string, int>?	hmapCCU	= JsonSerializer.Deserialize<Dictionary<string, int>>(sJson);

			if (hmapCCU == null)
			{
				return null;
			}

			return new OperationCCU(sLogTime, hmapCCU.GetValueOrDefault("Total"), hmapCCU.GetValueOrDefault("Baduki"), hmapCCU.GetValueOrDefault("Poker7"), hmapCCU.GetValueOrDefault("Holdem"), hmapCCU.GetValueOrDefault("Highlow"), hmapCCU.GetValueOrDefault("Blackjack"));
		}
		catch (JsonException)	// JSON 형식 오류
		{
			return null;
		}
	}

	private static string GetGame(string sName)	// 서버 이름 → 게임 (포함 단어 없음: "")
	{
		foreach ((string sWord, string sGame) in GAME_WORD)
		{
			if (sName.Contains(sWord, StringComparison.Ordinal))
			{
				return sGame;
			}
		}

		return "";
	}

	private static Dictionary<string, string> ToMap(HashEntry[] arrEntry)	// 해시 필드 → 값
	{
		return arrEntry.ToDictionary(kEntry => kEntry.Name.ToString(), kEntry => kEntry.Value.ToString());
	}
}

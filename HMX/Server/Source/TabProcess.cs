class TabProcess	// 반영 탭: 게임 서버 프로세스 시작·정지·다운 지시 (Redis 서버 목록 → 머신별 HMXClient 가 Git 갱신·실행·종료)·등록·편집·삭제·이름 변경·작업 로그·에러 (HMXServer 이식, API: Process, 페이지 쪽: ProcessHTML)
{
	private readonly Process	m_kProcess;	// 반영 API

	public TabProcess()
	{
		m_kProcess = new Process();
	}

	public void MapRoute(WebApplication WebApp)	// 라우트 등록 (반영 탭)
	{
		WebApp.MapMethods("/api/process", new[] { HttpMethods.Get, HttpMethods.Post }, m_kProcess.OnRequest);	// 반영 API (action 쿼리로 분기, HMXServer /api/hmx 와 같은 응답, 이름 변경: POST 본문 JSON)
	}

	public static string ProcessHTML()	// 페이지 반영 탭 스크립트 (Web.IndexHtml 조립)
	{
		return """
		class Process	// 반영 탭: 게임 서버 목록 (Redis) 시작·정지·다운·등록·편집·복제·삭제·이름 변경·작업 로그·에러 (HMXServer 이식, this 없음 → 메서드를 이벤트 처리기로 바로 넘김, innerHTML 값: Escape)
		{
		    static	POLL_MS			= 1000;					// 목록 갱신 주기 (ms, 반영 탭 보일 때만, 첫 로딩·순차 작업 중: 계속)
		    static	PAGE_SIZE		= 300;					// 로그·에러 한 페이지 (서버 PAGE_LIMIT 와 같음)
		    static	PAGE_UNIT		= 100;					// 목록 페이지 나눔: 대분류 번호 X 단위
		    static	TRY_COUNT		= 3;					// 시작·정지·다운 요청 시도 횟수
		    static	STALE_WAIT_MS	= 1500;					// 버전 불일치 배너 뒤 다시 읽기 (ms)
		    static	TOAST_MS		= 3500;					// 토스트 표시 (ms)
		    static	TOAST_FAIL_MS	= 6000;					// 실패 토스트 표시 (ms)
		    static	FLASH_MS		= 1700;					// 찾아간 줄 깜빡임 (ms)
		    static	LIST_MAX		= 5;					// 토스트 대상 이름 최대 (넘음: 외 N)
		    static	BLOCK_MAX		= 6;					// 확인 팝업 제외 목록 최대 (넘음: 외 N)
		    static	EXPAND_KEY		= "HMX.ProcessExpand";	// 펼친 중분류 localStorage 키
		    static	SUMMARY_KEY		= "HMX.ProcessSummary";	// 현황 펼침 localStorage 키 ("1": 펼침)
		    static	STATE_NAME		= { running: "Running", starting: "Starting", stopping: "Stopping", stopped: "Stopped", failed: "Failed", downloading: "Downloading" };	// 상태 배지 이름
		    static	OP				= { On: { sName: "시작", sIcon: "▶" }, Off: { sName: "정지", sIcon: "■" }, Down: { sName: "다운로드", sIcon: "↓" } };	// 작업 이름·아이콘 글자
		    static	LOG_TYPE		= { all: "전체", action: "조작", edit: "설정 변경", system: "상태 변경", login: "로그인", error: "경고·오류" };	// 로그 종류 칩
		    static	LOG_RANGE		= [["15m", "15분", 15], ["1h", "1시간", 60], ["6h", "6시간", 360], ["today", "오늘", 0], ["1d", "24시간", 1440], ["7d", "7일", 10080]];	// 로그 기간 칩 [키, 이름, 분 (오늘: 0시부터)]

		    static	kStale			= document.getElementById("ProcessStale");			// 버전 불일치 배너
		    static	kStaleText		= document.getElementById("ProcessStaleText");		// 배너 서버·화면 버전
		    static	kSummary		= document.getElementById("ProcessSummary");		// 현황·필터 (현황 버튼 펼침)
		    static	kSummaryButton	= document.getElementById("ProcessSummaryButton");	// 현황 버튼 (펼침: 존 색)
		    static	kCards			= document.getElementById("ProcessCards");			// 요약 카드 (대분류마다)
		    static	kSearch			= document.getElementById("ProcessSearch");			// 검색칸
		    static	kChips			= document.getElementById("ProcessChips");			// 상태 칩
		    static	kMachine		= document.getElementById("ProcessMachine");		// 머신 드롭 박스
		    static	kVersion		= document.getElementById("ProcessVersion");		// 설정 버전 Ver.N
		    static	kPager			= document.getElementById("ProcessPager");			// 페이지 버튼 (상자 머리 맨 앞, PAGE_UNIT 단위)
		    static	kTest			= document.getElementById("ProcessTest");			// TestOn·TestOff (설정 bTest)
		    static	kError			= document.getElementById("ProcessError");			// 목록 오류
		    static	kTables			= document.getElementById("ProcessTables");			// 서버 표 (대분류마다 상자)
		    static	kDrawer			= document.getElementById("ProcessDrawer");			// 상세 패널 (오른쪽 서랍)
		    static	kMenu			= document.getElementById("ProcessMenu");			// 줄 메뉴 (⋯)
		    static	kDialog			= document.getElementById("ProcessDialog");			// 공통 팝업 (확인·폼·이름 변경·로그·에러)
		    static	kToasts			= document.getElementById("ProcessToasts");			// 토스트 (다른 탭에서도 보임)
		    static	kDeleteYes		= document.getElementById("DeleteYes");				// 삭제 확인 삭제 (실행 중: 꺼짐)
		    static	kDeleteStop		= document.getElementById("ProcessDeleteStop");		// 삭제 확인 먼저 정지 (Running 만)

		    static	bOpened		= false;		// 처음 열기 끝 (첫 로딩·폴링 시작)
		    static	bStarted	= false;		// 첫 목록 끝 (빈 목록 안내: 불러오는 중 → 등록된 서버 없음)
		    static	bListError	= false;		// 목록 요청 실패 (작업 버튼 잠금)
		    static	kLoad		= null;			// 목록 요청 중 (Promise, 겹침 방지)
		    static	nVersion	= 0;			// 화면 설정 버전 (등록·수정·삭제·이름 변경 검증)
		    static	arrRow		= [];			// 서버 목록 [{ sField, sValue }] (서버 정렬 순서)
		    static	sRawList	= "";			// 직전 목록 응답 (바뀜만 다시 그림)
		    static	setJob		= new Set();	// 진행 중 순차 작업 { sTitle, sResult } (여러 대분류 겹침 가능, 끝: 뺌)
		    static	kStart		= null;			// 첫 로딩 (Promise, Ctrl·Alt+클릭 새 창 버튼: 끝난 뒤 실행)
		    static	sDrawer		= null;			// 상세 패널 서버 키
		    static	sSelect		= null;			// 선택 줄 서버 키
		    static	setExpand	= new Set();	// 펼친 중분류 ("g:대분류,중분류")
		    static	kFilter		= { sKey: "", sState: "all", sMachine: "" };	// 검색어·상태 칩·머신
		    static	sPage		= null;			// 지금 페이지: 대분류 번호 PAGE_UNIT 단위 ("0"·"1"·…, 번호 없음: "0", 최초·목록에 없음: 첫 페이지 = 0번)
		    static	funcModal	= null;			// 확인 팝업 결과 대기 (resolve)
		    static	kRename		= null;			// 이름 변경 대상 { sScope, arrList, funcKey }
		    static	sLogNew		= "";			// 로그 최신 커서 (▲ 더 최신)
		    static	sLogOld		= "";			// 로그 최과거 커서 (더 과거 ▼)
		    static	arrLog		= [];			// 지금 로그 페이지 줄
		    static	kLogState	= { sRange: "1h", sType: "all", sKey: "" };	// 로그 기간·종류·검색
		    static	nErrOffset	= 0;			// 에러 페이지 시작 (0: 최신)
		    static	arrErr		= [];			// 지금 에러 페이지
		    static	kErrState	= { sMachine: "", sLevel: "", sKey: "" };	// 에러 머신·레벨·검색

		    static Open()	// 반영 탭 버튼: 반영 창 (처음: 버전·목록 로딩 + 폴링, 다시 열기: 바로 갱신)
		    {
		        ShowPanel("process");

		        Process.CloseMenu();

		        if (Process.bOpened)
		        {
		            Process.Refresh(true);

		            return;
		        }

		        Process.bOpened = true;

		        Process.kStart = Process.Start();
		    }

		    static async Start()	// 첫 로딩: TestOn·TestOff + 버전 + 목록 → 펼침 (보관 없음: 문제 그룹만) → 폴링
		    {
		        Process.LoadCheck();

		        await Process.LoadVersion();
		        await Process.Refresh(true);

		        Process.bStarted = true;

		        if (!Process.LoadExpand())
		        {
		            Process.DefaultExpand();
		        }

		        Process.Render();

		        setInterval(Process.Refresh, Process.POLL_MS);
		    }

		    static Escape(value)	// HTML 이스케이프 (innerHTML 에 넣는 값)
		    {
		        return String(value ?? "").replace(/[&<>"']/g, (sChar) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[sChar]);
		    }

		    static Pad(nValue)	// 두 자리 (예: 07)
		    {
		        return String(nValue).padStart(2, "0");
		    }

		    static GetInfo(kRow)	// 값 파트: 상태,적용,PID,커밋,머신,다운폴더,깃헙폴더,Sync,Mode,실행파일+인자들
		    {
		        const	arrPart	= kRow.sValue.split(",");

		        return { sStatus: arrPart[0] || "", sApply: arrPart[1] || "", nPid: parseInt(arrPart[2], 10), sCommit: arrPart[3] || "", sMachine: arrPart[4] || "", sFolder: arrPart[5] || "", sRepo: arrPart[6] || "", sSync: arrPart[7] || "", sMode: arrPart[8] || "", sCmd: arrPart.slice(9).join(",") };
		    }

		    static BuildValue(sValue, sStatus, sApply)	// 상태,적용만 바꾼 값 (뒤 파트 유지)
		    {
		        const	sRest	= sValue.split(",").slice(2).join(",");

		        return `${sStatus},${sApply}${sRest !== "" ? "," + sRest : ""}`;
		    }

		    static GetState(kRow)	// 상태 (On·Down 재요청: 이전 실패 PID 보다 우선, PID 음수: 실패, Down,True: 정지)
		    {
		        const	kInfo	= Process.GetInfo(kRow);

		        if (kInfo.sApply === "False" && (kInfo.sStatus === "On" || kInfo.sStatus === "Down"))
		        {
		            return kInfo.sStatus === "On" ? "starting" : "downloading";
		        }

		        if (kInfo.nPid < 0)	// 실행 실패
		        {
		            return "failed";
		        }

		        if (kInfo.sStatus === "Off" && kInfo.sApply === "False")
		        {
		            return "stopping";
		        }

		        return kInfo.sStatus === "On" ? "running" : "stopped";
		    }

		    static IsTransit(sState)	// 진행 중 (Starting·Stopping·Downloading)
		    {
		        return sState === "starting" || sState === "stopping" || sState === "downloading";
		    }

		    static Badge(kRow)	// 상태 배지 (Primer Label + 점)
		    {
		        const	sState	= Process.GetState(kRow);

		        return `<span class="badge ${sState}">${Process.STATE_NAME[sState]}</span>`;
		    }

		    static GetGroup(sField)	// 중분류 키: 첫째·둘째 값
		    {
		        const	arrPart	= sField.split(",");

		        return arrPart.length >= 2 ? arrPart[0] + "," + arrPart[1] : sField;
		    }

		    static GetLabel(sField)	// 서버 이름: 셋째 값부터
		    {
		        const	arrPart	= sField.split(",");

		        return arrPart.length >= 3 ? arrPart.slice(2).join(",").trim() : sField;
		    }

		    static GetTarget(sField)	// 로그 대상: 키 파트 앞뒤 공백 뺌
		    {
		        return sField.split(",").map(sPart => sPart.trim()).join(",");
		    }

		    static GetGroupLabel(sGroup)	// 중분류 이름: 둘째 값 (없음: 기타)
		    {
		        return (sGroup.split(",")[1] || sGroup).trim() || "기타";
		    }

		    static FindRow(sField)	// 서버 (없음: undefined)
		    {
		        return Process.arrRow.find(kRow => kRow.sField === sField);
		    }

		    static GetModel()	// 대분류: 중분류 첫 숫자마다 (이름: 첫 그룹 첫째 값, 숫자 자연 정렬) → [{ sId, nNumber, sLabel, arrGroup: [{ sId, sLabel, arrRow }], arrRow }]
		    {
		        const	mapGroup	= new Map();	// 중분류 → 서버 (목록 순서)
		        const	arrSec		= [];
		        let		kSec		= null;

		        for (const kRow of Process.arrRow)
		        {
		            const	sGroup	= Process.GetGroup(kRow.sField);

		            if (!mapGroup.has(sGroup))
		            {
		                mapGroup.set(sGroup, []);
		            }

		            mapGroup.get(sGroup).push(kRow);
		        }

		        const	arrGroup	= [...mapGroup.keys()].sort((sA, sB) => sA === sB ? 0 : (sA === "" ? 1 : (sB === "" ? -1 : sA.localeCompare(sB, undefined, { numeric: true }))));	// 빈 그룹: 맨 뒤

		        for (const sGroup of arrGroup)
		        {
		            const	nNumber	= parseInt(sGroup, 10);
		            const	sId		= isNaN(nNumber) ? "x" : String(nNumber);

		            if (kSec == null || kSec.sId !== sId)
		            {
		                kSec = { sId, nNumber, sLabel: (sGroup.split(",")[0] || "").trim() || "기타", arrGroup: [], arrRow: [] };

		                arrSec.push(kSec);
		            }

		            kSec.arrGroup.push({ sId: sGroup, sLabel: Process.GetGroupLabel(sGroup), arrRow: mapGroup.get(sGroup) });
		            kSec.arrRow.push(...mapGroup.get(sGroup));
		        }

		        return arrSec;
		    }

		    static FindSec(sId)	// 대분류 (첫 숫자, 번호 없음: x)
		    {
		        return Process.GetModel().find(kSec => kSec.sId === sId);
		    }

		    static GetPage(kSec)	// 대분류 페이지: 번호 PAGE_UNIT 단위 (PAGE_UNIT 미만·번호 없음: 0)
		    {
		        return isNaN(kSec.nNumber) ? "0" : String(Math.floor(kSec.nNumber / Process.PAGE_UNIT));
		    }

		    static GetPages(arrSec)	// 페이지 목록: 서버 있는 PAGE_UNIT 단위만 (번호 순, 겹침 없음)
		    {
		        return [...new Set(arrSec.map(kSec => Process.GetPage(kSec)))].sort((sA, sB) => Number(sA) - Number(sB));
		    }

		    static GetCurPage(arrPage)	// 지금 페이지 (없음·목록에 없음: 첫 페이지, 페이지 없음: null)
		    {
		        return arrPage.includes(Process.sPage) ? Process.sPage : (arrPage[0] ?? null);
		    }

		    static GetGroupRows(sGroup)	// 중분류 서버
		    {
		        return Process.arrRow.filter(kRow => Process.GetGroup(kRow.sField) === sGroup);
		    }

		    static GetCount(arrList)	// 상태별 수 (transit: 진행 중)
		    {
		        const	kCount	= { total: arrList.length, transit: 0, running: 0, starting: 0, stopping: 0, stopped: 0, failed: 0, downloading: 0 };

		        for (const kRow of arrList)
		        {
		            const	sState	= Process.GetState(kRow);

		            ++kCount[sState];

		            if (Process.IsTransit(sState))
		            {
		                ++kCount.transit;
		            }
		        }

		        return kCount;
		    }

		    static GetMajority(arrList)	// 다수 커밋
		    {
		        const	kCount	= {};	// 커밋 → 서버 수 (객체: 같은 수 → 숫자 커밋 먼저, 원본과 같은 순서)

		        for (const kRow of arrList)
		        {
		            const	sCommit	= Process.GetInfo(kRow).sCommit;

		            kCount[sCommit] = (kCount[sCommit] ?? 0) + 1;
		        }

		        return Object.entries(kCount).sort((arrA, arrB) => arrB[1] - arrA[1])[0]?.[0] ?? "";
		    }

		    static GetDrift(arrList)	// 커밋 불일치 서버 (다수 커밋과 다름)
		    {
		        const	sMajor	= Process.GetMajority(arrList);

		        return arrList.filter(kRow => Process.GetInfo(kRow).sCommit !== sMajor);
		    }

		    static IsDrift(kRow)	// 같은 중분류 다수 커밋과 다름
		    {
		        return Process.GetDrift(Process.GetGroupRows(Process.GetGroup(kRow.sField))).includes(kRow);
		    }

		    static Api(sQuery)	// 반영 API → JSON (상대 주소: ToolManager 경유)
		    {
		        return FetchJson("api/process?" + sQuery);
		    }

		    static SetField(sField, sValue, sLog, bVersion)	// 값 저장 (bVersion: 버전 검증·증가)
		    {
		        return Process.Api("action=set&field=" + encodeURIComponent(sField) + "&value=" + encodeURIComponent(sValue) + "&log=" + sLog + (bVersion ? "&version=" + Process.nVersion : ""));
		    }

		    static WriteActLog(sKind, sTarget, sStatus)	// 시작·정지·다운 확인 기록 (서버 로그 1줄, 결과 안 기다림)
		    {
		        fetch("api/process?action=actlog&kind=" + encodeURIComponent(sKind) + "&target=" + encodeURIComponent(sTarget) + "&status=" + encodeURIComponent(sStatus)).catch(() => {});
		    }

		    static async LoadCheck()	// TestOn·TestOff 표시 (설정 bTest)
		    {
		        try
		        {
		            Process.kTest.hidden = (await Process.Api("action=check")).test !== true;
		        }
		        catch (kError)	// 확인 실패: 숨김 그대로
		        {
		            console.error(kError);
		        }
		    }

		    static async LoadVersion()	// 설정 버전 (실패: 그대로)
		    {
		        try
		        {
		            const	kResponse	= await fetch("api/process?action=version");

		            if (kResponse.ok)
		            {
		                Process.SetVersion(parseInt((await kResponse.text()).trim(), 10) || 0);
		            }
		        }
		        catch (kError)	// 접속 안 됨: 목록 요청이 오류 표시
		        {
		        }
		    }

		    static SetVersion(nVersion)	// 화면 설정 버전 Ver.N
		    {
		        Process.nVersion			= nVersion;
		        Process.kVersion.textContent	= "Ver." + nVersion;
		    }

		    static CheckStale(kData)	// 버전 불일치 (다른 곳 변경): 팝업 닫고 배너 (서버·화면 버전) → X초 뒤 다시 읽기 + 다시 시도 토스트 (true: 불일치)
		    {
		        if (kData?.stale !== true)
		        {
		            return false;
		        }

		        Process.CloseModal(false, true);

		        Process.kStaleText.textContent	= `— 서버 Ver.${kData.version} (화면 Ver.${Process.nVersion}). 최신 목록으로 새로고침합니다…`;
		        Process.kStale.hidden			= false;

		        setTimeout(() => Process.Reload("다른 곳에서 변경되어 새로고침했습니다. 다시 시도해주세요."), Process.STALE_WAIT_MS);

		        return true;
		    }

		    static async Reload(sToast)	// 반영 탭 데이터만 다시 읽기 (버전 + 목록, 펼침·필터 유지) → 토스트 (실패 글: 실패 색)
		    {
		        Process.SaveExpand();

		        Process.kStale.hidden	= true;
		        Process.sRawList		= "";	// 같은 응답도 다시 그림

		        await Process.LoadVersion();
		        await Process.Refresh(true);

		        if (sToast)
		        {
		            Process.Toast(sToast, /실패/.test(sToast) ? "fail" : "ok");
		        }
		    }

		    static ApplyLocal(sField, sValue)	// 내 변경 성공: 다음 갱신 전 바로 반영
		    {
		        const	kRow	= Process.FindRow(sField);

		        if (kRow != null)
		        {
		            kRow.sValue = sValue;

		            Process.Render();
		        }
		    }

		    static ParseList(sText)	// 목록 응답 "필드|값" 줄 → [{ sField, sValue }]
		    {
		        const	arrRow	= [];

		        for (const sLine of sText.trim().split("\n"))
		        {
		            const	nBar	= sLine.indexOf("|");

		            if (nBar > 0)
		            {
		                arrRow.push({ sField: sLine.substring(0, nBar), sValue: sLine.substring(nBar + 1).replace(/\r$/, "") });
		            }
		        }

		        return arrRow;
		    }

		    static async Refresh(bForce)	// 목록 갱신 (반영 탭 안 보임·브라우저 탭 숨김: 쉼 → 첫 로딩·순차 작업 중: 계속, 요청 중: 폴링 건너뜀·강제 끝 대기 후 다시)
		    {
		        if (!bForce && Process.bStarted && (kProcessPanel.hidden || document.hidden) && Process.setJob.size === 0)
		        {
		            return;
		        }

		        while (Process.kLoad != null)
		        {
		            if (!bForce)
		            {
		                return;
		            }

		            await Process.kLoad;
		        }

		        Process.kLoad = Process.LoadList();

		        try
		        {
		            await Process.kLoad;
		        }
		        finally
		        {
		            Process.kLoad = null;
		        }
		    }

		    static async LoadList()	// 목록 요청 → 바뀜: 다시 그림 (실패: 오류 상자 + 작업 버튼 잠금)
		    {
		        try
		        {
		            const	kResponse	= await fetch("api/process");

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const	sText	= await kResponse.text();

		            if (sText.startsWith("오류:"))	// Redis 오류 등: 서버 오류 글
		            {
		                throw new Error(sText.slice(3).trim());
		            }

		            Process.ShowError("");

		            if (sText !== Process.sRawList)
		            {
		                Process.sRawList	= sText;
		                Process.arrRow		= Process.ParseList(sText);

		                Process.Render();
		            }
		        }
		        catch (kError)
		        {
		            Process.ShowError(`목록 조회 실패: ${ErrorText(kError)}`);
		        }
		    }

		    static ShowError(sText)	// 목록 오류 상자 (빈 문자열: 숨김) + 작업 버튼 잠금
		    {
		        Process.kError.textContent	= sText;
		        Process.kError.hidden		= sText === "";
		        Process.bListError			= sText !== "";

		        Process.ShowLock();
		    }

		    static ShowLock()	// 작업 버튼 잠금 (흐림·누름 안 됨): HMX 연결 끊김 (제목 옆 글)·목록 오류
		    {
		        kProcessPanel.classList.toggle("offline", Process.bListError || !kOffText.hidden);
		    }

		    static Render()	// 요약 카드·상태 칩·페이지 버튼·표·상세 패널
		    {
		        Process.RenderCards();
		        Process.RenderChips();
		        Process.RenderPager();
		        Process.RenderTable();

		        if (Process.sDrawer != null)
		        {
		            Process.RenderDrawer();
		        }
		    }

		    static RenderCards()	// 요약 카드: 대분류마다 (이름 · Running 수 / 전체 · 태그)
		    {
		        Process.kCards.innerHTML = Process.GetModel().map(kSec =>
		        {
		            const	kCount	= Process.GetCount(kSec.arrRow);
		            const	nDrift	= kSec.arrGroup.reduce((nSum, kGroup) => nSum + Process.GetDrift(kGroup.arrRow).length, 0);
		            const sTag		= (kCount.failed ? `<span class="tag fail">✕ Failed ${kCount.failed}</span>` : "")
		                + (kCount.transit ? `<span class="tag start"><span class="spin"></span> 진행 중 ${kCount.transit}</span>` : "")
		                + (nDrift ? `<span class="tag warn" title="다른 커밋">⚠ 커밋 불일치 ${nDrift}</span>` : "")
		                + (!kCount.failed && !kCount.transit && !nDrift && kCount.running === kCount.total ? `<span class="tag run">정상</span>` : "")
		                + (kCount.running === 0 && !kCount.failed && !kCount.transit ? `<span class="tag off">전체 정지</span>` : "");

		            return `<button type="button" class="card" data-act="jump" data-id="${Process.Escape(kSec.sId)}"><span class="c-name">${Process.Escape(kSec.sLabel)}</span><span class="c-num">${kCount.running}<small> / ${kCount.total}</small></span><span class="c-tags">${sTag}</span></button>`;
		        }).join("");
		    }

		    static RenderChips()	// 상태 칩 (+ 개수, 선택: 존 색) + 머신 드롭 박스 (머신 바뀜만 다시)
		    {
		        const	kCount		= Process.GetCount(Process.arrRow);
		        const	arrChip		= [["all", "전체", kCount.total], ["running", "Running", kCount.running], ["transit", "진행 중", kCount.transit], ["stopped", "Stopped", kCount.stopped], ["failed", "Failed", kCount.failed], ["drift", "커밋 불일치", Process.arrRow.filter(kRow => Process.IsDrift(kRow)).length]];
		        const	arrMachine	= [...new Set(Process.arrRow.map(kRow => Process.GetInfo(kRow).sMachine).filter(Boolean))].sort();
		        const	sSignature	= arrMachine.join("|");

		        Process.kChips.innerHTML = arrChip.map(([sId, sName, nCount]) => `<button type="button" class="chip${Process.kFilter.sState === sId ? " cur" : ""}" data-act="state" data-id="${sId}">${sName} <span class="n">${nCount}</span></button>`).join("");

		        if (Process.kMachine.dataset.sig !== sSignature)
		        {
		            Process.kMachine.replaceChildren(new Option("모든 머신", ""), ...arrMachine.map(sMachine => new Option(sMachine, sMachine, false, sMachine === Process.kFilter.sMachine)));

		            Process.kMachine.dataset.sig = sSignature;
		        }
		    }

		    static RenderPager()	// 페이지 버튼 (상자 머리 맨 앞): 서버 있는 PAGE_UNIT 단위만 (PAGE_UNIT 미만·번호 없음: 0, 번호 / PAGE_UNIT), 지금 페이지 존 색, 1페이지도 보임 (서버 없음: 숨김), 필터 중: 모든 페이지 결과 → 버튼 잠금
		    {
		        const	arrSec	= Process.GetModel();
		        const	arrPage	= Process.GetPages(arrSec);
		        const	sCur	= Process.GetCurPage(arrPage);
		        const	bFilter	= Process.IsFiltering();

		        Process.kPager.hidden		= arrPage.length === 0;
		        Process.kPager.innerHTML	= arrPage.map(sPage => `<button type="button"${!bFilter && sPage === sCur ? ' class="cur"' : ""} data-act="page" data-id="${sPage}"${bFilter ? " disabled" : ""}>${sPage}</button>`).join("");
		    }

		    static IsMatch(kRow)	// 필터 맞음 (상태 칩·머신·검색어: 키·머신·커밋·실행 명령 부분 일치, 대소문자 무시)
		    {
		        const	sState	= Process.GetState(kRow);
		        const	kInfo	= Process.GetInfo(kRow);
		        const	sFilter	= Process.kFilter.sState;
		        const	sKey	= Process.kFilter.sKey.toLowerCase();

		        if (sFilter === "transit" ? !Process.IsTransit(sState) : (sFilter === "drift" ? !Process.IsDrift(kRow) : (sFilter !== "all" && sFilter !== sState)))
		        {
		            return false;
		        }

		        if (Process.kFilter.sMachine !== "" && kInfo.sMachine !== Process.kFilter.sMachine)
		        {
		            return false;
		        }

		        return sKey === "" || [kRow.sField, kInfo.sMachine, kInfo.sCommit, kInfo.sCmd].some(sText => sText.toLowerCase().includes(sKey));
		    }

		    static IsFiltering()	// 필터 중
		    {
		        return Process.kFilter.sKey !== "" || Process.kFilter.sState !== "all" || Process.kFilter.sMachine !== "";
		    }

		    static PairButtons(sScope, sId, arrList)	// 시작·정지 버튼 쌍: 지금 상태 쪽 불 (서버 진행 중: 잠금)
		    {
		        const	kCount	= Process.GetCount(arrList);
		        const	bAllOn	= kCount.running === kCount.total;
		        const	bAllOff	= kCount.stopped + kCount.failed === kCount.total;
		        const	sOn		= "on" + (bAllOn ? " lit" : (kCount.running > 0 ? " part" : "")) + (kCount.starting ? " pend" : "");
		        const	sOff	= "off" + (bAllOff ? " lit" : "") + (kCount.failed ? " fail" : "") + (kCount.stopping ? " pend" : "");
		        const	sLock	= sScope === "srv" && kCount.transit ? " disabled" : "";
		        const	sKey	= Process.Escape(sId);

		        return `<span class="pair"><button type="button" class="pbtn ${sOn} needs-conn" data-act="op" data-op="On" data-scope="${sScope}" data-id="${sKey}"${sLock} title="시작${bAllOn ? "(실행 중)" : ""}" aria-label="시작">▶</button>`
		            + `<button type="button" class="pbtn ${sOff} needs-conn" data-act="op" data-op="Off" data-scope="${sScope}" data-id="${sKey}"${sLock} title="정지${bAllOff ? "(정지됨)" : ""}" aria-label="정지">■</button></span>`;
		    }

		    static DownButton(sScope, sId, arrList)	// 다운 버튼 (다운 중: 보라 깜빡임, 서버 진행 중: 잠금)
		    {
		        const	kCount	= Process.GetCount(arrList);

		        return `<button type="button" class="pbtn down${kCount.downloading ? " pend" : ""} needs-conn" data-act="op" data-op="Down" data-scope="${sScope}" data-id="${Process.Escape(sId)}"${sScope === "srv" && kCount.transit ? " disabled" : ""} title="Git 갱신" aria-label="다운">↓</button>`;
		    }

		    static KebabButton(sScope, sId)	// 줄 메뉴 버튼 (⋯)
		    {
		        return `<button type="button" class="kebab" data-act="menu" data-scope="${sScope}" data-id="${Process.Escape(sId)}" title="더보기" aria-label="더보기">⋯</button>`;
		    }

		    static RenderTable()	// 서버 표: 지금 페이지 대분류마다 상자, 대분류 → 중분류 → 서버 줄 (필터 중: 모든 페이지 맞는 서버 그룹 모두 펼침)
		    {
		        const	bFilter	= Process.IsFiltering();
		        const	arrSec	= Process.GetModel();
		        const	arrPage	= Process.GetPages(arrSec);
		        const	sPage	= Process.GetCurPage(arrPage);	// 지금 페이지 (필터 중: 안 씀)
		        const sHead			= `<thead><tr><th class="c-btn"><button type="button" class="th-help" data-act="legend" title="작업 범례">작업 ⓘ</button></th><th class="c-name">이름</th><th class="c-state">상태</th><th class="c-machine">머신</th><th class="c-pid">PID</th><th class="c-commit">커밋</th><th class="c-act"></th></tr></thead>`;
		        let	sHtml			= "";

		        for (const kSec of arrSec)
		        {
		            if (bFilter ? !kSec.arrRow.some(kRow => Process.IsMatch(kRow)) : Process.GetPage(kSec) !== sPage)	// 필터 중: 맞는 서버 있음, 그 외: 지금 페이지만
		            {
		                continue;
		            }

		            const	bOpen	= bFilter || kSec.arrGroup.every(kGroup => Process.setExpand.has("g:" + kGroup.sId));	// 대분류 펼침 표시: 하위 중분류 모두 펼침
		            const	kCount	= Process.GetCount(kSec.arrRow);
		            const	sSecId	= Process.Escape(kSec.sId);
		            let sRows		= `<tr class="sec-row${bOpen ? " open" : ""}${kSec.nNumber >= 10000 ? " gray" : ""}" data-act="secToggle" data-id="${sSecId}" id="ProcessSec-${sSecId}"><td colspan="7"><div class="row-head">${Process.PairButtons("sec", kSec.sId, kSec.arrRow)}`
		                + `<span class="chev">▶</span><span class="sec-name">${Process.Escape(kSec.sLabel)}</span><span><b>${kCount.running}</b><span class="muted"> / ${kCount.total}</span></span>`
		                + (kCount.failed ? `<span class="tag fail">✕ Failed ${kCount.failed}</span>` : "") + (kCount.transit ? `<span class="tag start"><span class="spin"></span> 진행 중 ${kCount.transit}</span>` : "")
		                + `<span class="acts">${Process.DownButton("sec", kSec.sId, kSec.arrRow)}${Process.KebabButton("sec", kSec.sId)}</span></div></td></tr>`;

		            for (const kGroup of kSec.arrGroup)
		            {
		                const	arrShow	= kGroup.arrRow.filter(kRow => Process.IsMatch(kRow));

		                if (bFilter && arrShow.length === 0)
		                {
		                    continue;
		                }

		                const	bGroupOpen	= bFilter || Process.setExpand.has("g:" + kGroup.sId);
		                const	kGroupCount	= Process.GetCount(kGroup.arrRow);
		                const	nDrift		= Process.GetDrift(kGroup.arrRow).length;
		                const	sMajor		= Process.GetMajority(kGroup.arrRow);

		                sRows += `<tr class="grp-row${bGroupOpen ? " open" : ""}" data-act="toggle" data-id="g:${Process.Escape(kGroup.sId)}"><td colspan="7"><div class="row-head">${Process.PairButtons("grp", kGroup.sId, kGroup.arrRow)}`
		                    + `<span class="chev">▶</span><span class="grp-name">${Process.Escape(kGroup.sLabel)}</span><span class="muted">${kGroupCount.running}/${kGroupCount.total}</span>`
		                    + (kGroupCount.failed ? `<span class="tag fail">✕ ${kGroupCount.failed}</span>` : "") + (nDrift ? `<span class="tag warn" title="다수 커밋: ${Process.Escape(sMajor)}">⚠ 커밋 불일치 ${nDrift}</span>` : "")
		                    + `<span class="acts">${Process.DownButton("grp", kGroup.sId, kGroup.arrRow)}${Process.KebabButton("grp", kGroup.sId)}</span></div></td></tr>`;

		                if (!bGroupOpen)
		                {
		                    continue;
		                }

		                for (const kRow of arrShow)
		                {
		                    const	sState	= Process.GetState(kRow);
		                    const	kInfo	= Process.GetInfo(kRow);
		                    const	sKey	= Process.Escape(kRow.sField);
		                    const sPid		= kInfo.nPid > 0 ? String(kInfo.nPid) : `<span class="muted">${Process.Escape(isNaN(kInfo.nPid) ? "—" : kInfo.nPid)}</span>`;
		                    const sCommit	= kInfo.sCommit !== sMajor ? `<span class="commit drift" title="다수 커밋: ${Process.Escape(sMajor)}">⚠ ${Process.Escape(kInfo.sCommit)}</span>` : `<span class="commit">${Process.Escape(kInfo.sCommit)}</span>`;

		                    sRows += `<tr class="srv-row ${sState}${Process.sDrawer === kRow.sField ? " active" : ""}${Process.sSelect === kRow.sField ? " sel" : ""}" data-act="select" data-id="${sKey}" data-key="${sKey}">`
		                        + `<td class="c-btn">${Process.PairButtons("srv", kRow.sField, [kRow])}</td><td class="c-name" title="${sKey}">${Process.Escape(Process.GetLabel(kRow.sField))}</td><td class="c-state">${Process.Badge(kRow)}</td>`
		                        + `<td class="c-machine mono" title="${Process.Escape(kInfo.sMachine)}">${Process.Escape(kInfo.sMachine)}</td><td class="c-pid mono">${sPid}</td><td class="c-commit">${sCommit}</td>`
		                        + `<td class="c-act">${Process.DownButton("srv", kRow.sField, [kRow])} ${Process.KebabButton("srv", kRow.sField)}</td></tr>`;
		                }
		            }

		            sHtml += `<div class="scroll sec-block"><table class="fixed">${sHead}<tbody>${sRows}</tbody></table></div>`;
		        }

		        if (sHtml === "")	// 빈 상태: 가운데 흐린 안내
		        {
		            sHtml = Process.arrRow.length > 0 ? `<div class="scroll"><div class="empty">조건에 맞는 서버가 없습니다. <button type="button" data-act="clearFilter">필터 초기화</button></div></div>` : `<div class="scroll"><div class="empty">${Process.bStarted ? "등록된 서버가 없습니다." : "불러오는 중…"}</div></div>`;
		        }

		        Process.kTables.innerHTML = sHtml;
		    }

		    static CopyBox(sValue)	// 값 상자 + 복사 버튼
		    {
		        const	sText	= Process.Escape(sValue);

		        return `<div class="codebox"><span>${sText}</span><button type="button" class="copy" data-act="copy" data-id="${sText}" title="복사" aria-label="복사">⧉</button></div>`;
		    }

		    static RenderDrawer()	// 상세 패널: 머리 (이름·닫기) + 경로·배지·실패·커밋 불일치 안내 + 상태·Redis 키·실행 설정·원본 값 + 아래 작업 버튼 (서버 사라짐: 닫음)
		    {
		        const	kRow	= Process.FindRow(Process.sDrawer);

		        if (kRow == null)
		        {
		            Process.CloseDrawer();

		            return;
		        }

		        const	sState	= Process.GetState(kRow);
		        const	kInfo	= Process.GetInfo(kRow);
		        const	sGroup	= Process.GetGroup(kRow.sField);
		        const	sMajor	= Process.GetMajority(Process.GetGroupRows(sGroup));
		        const	bDrift	= kInfo.sCommit !== sMajor;
		        const	sKey	= Process.Escape(kRow.sField);
		        const	sTarget	= Process.Escape(Process.GetTarget(kRow.sField));

		        Process.kDrawer.innerHTML = `<div class="resulthead"><div class="title">${Process.Escape(Process.GetLabel(kRow.sField))}</div><div class="toolright"><button type="button" data-act="closeDrawer" title="닫기(Esc)">닫기</button></div></div>`
		            + `<div class="d-body"><div class="crumb">${Process.Escape(sGroup.split(",").map(sPart => sPart.trim()).join(" › "))}</div><div class="d-badge">${Process.Badge(kRow)}</div>`
		            + (sState === "failed" ? `<div class="notice err"><b>실행 실패</b> (PID ${Process.Escape(kInfo.nPid)}) — <a href="#" data-act="openErr" data-id="${sTarget}">에러 로그 보기</a></div>` : "")
		            + (bDrift ? `<div class="notice warn">⚠ 이 서버는 <b class="mono">${Process.Escape(kInfo.sCommit)}</b> 커밋으로 실행 중입니다. 같은 그룹 다수는 <b class="mono">${Process.Escape(sMajor)}</b> 입니다.</div>` : "")
		            + `<div class="d-sec"><h4>상태</h4><dl class="kv"><dt>상태</dt><dd>${Process.STATE_NAME[sState]} <span class="muted mono raw">(${Process.Escape(kInfo.sStatus)},${Process.Escape(kInfo.sApply)})</span></dd><dt>PID</dt><dd class="mono">${Process.Escape(kInfo.nPid)}</dd><dt>커밋</dt><dd><span class="commit${bDrift ? " drift" : ""}">${Process.Escape(kInfo.sCommit)}</span></dd></dl></div>`
		            + `<div class="d-sec"><h4>Redis 키</h4>${Process.CopyBox(kRow.sField)}</div>`
		            + `<div class="d-sec"><h4>실행 설정</h4><dl class="kv"><dt>머신</dt><dd class="mono">${Process.Escape(kInfo.sMachine)}</dd><dt>Git 갱신</dt><dd>${kInfo.sSync === "0" ? "갱신 안 함 (Sync=0)" : "시작할 때 Git 갱신 (Sync=1)"}</dd><dt>종료 방식</dt><dd>${kInfo.sMode === "1" ? "Graceful — 콘솔 + Ctrl+C 종료" : "Detached — 독립 실행"}</dd>`
		            + `<dt>다운 폴더</dt><dd>${Process.CopyBox(kInfo.sFolder)}</dd><dt>깃헙 폴더</dt><dd>${Process.CopyBox(kInfo.sRepo)}</dd><dt>실행 명령</dt><dd>${Process.CopyBox(kInfo.sCmd)}</dd></dl></div>`
		            + `<div class="d-sec"><details><summary>Redis 원본 값</summary>${Process.CopyBox(kRow.sValue)}</details></div></div>`
		            + `<div class="d-foot">${Process.PairButtons("srv", kRow.sField, [kRow])} ${Process.DownButton("srv", kRow.sField, [kRow])}<button type="button" class="needs-conn" data-act="edit" data-id="${sKey}">설정 편집</button><button type="button" class="needs-conn" data-act="clone" data-id="${sKey}">복제</button>`
		            + `<button type="button" data-act="openLog" data-id="${sTarget}">로그</button><span class="spacer"></span><button type="button" class="danger needs-conn" data-act="del" data-id="${sKey}">삭제</button></div>`;
		    }

		    static OpenDrawer(sField)	// 상세 패널 열기 (그 줄 왼쪽 파랑 선)
		    {
		        Process.sDrawer = sField;

		        Process.RenderDrawer();
		        Process.kDrawer.classList.add("show");
		        Process.RenderTable();
		    }

		    static async ResetState()	// F5 리셋: 최초 입장 상태 (줄 메뉴·상세 패널 닫기·필터 초기화·0번 탭 → TestOn 표시·버전·목록 다시, 그대로: 펼침·현황 보관)
		    {
		        Process.CloseMenu();
		        Process.CloseDrawer();
		        Process.SetPage(null);	// 첫 페이지 (0번)
		        Process.ClearFilter();

		        if (!Process.bOpened)	// 처음 열기 전: 처음 열기
		        {
		            Process.Open();

		            return;
		        }

		        Process.LoadCheck();

		        await Process.LoadVersion();
		        await Process.Refresh(true);
		    }

		    static CloseDrawer()	// 상세 패널 닫기
		    {
		        Process.sDrawer = null;

		        Process.kDrawer.classList.remove("show");
		        Process.RenderTable();
		    }

		    static OpenModal(sHtml, sClass = "")	// 공통 팝업 열기 (sClass: wide·xl, 앞 확인 대기: 취소, autofocus 칸 포커스)
		    {
		        Process.ResolveModal(false);

		        delete Process.kDialog.dataset.locked;

		        Process.kDialog.className	= sClass;
		        Process.kDialog.innerHTML	= sHtml;

		        if (!Process.kDialog.open)
		        {
		            Process.kDialog.showModal();
		        }

		        setTimeout(() => Process.kDialog.querySelector("[autofocus]")?.focus(), 30);

		        return Process.kDialog;
		    }

		    static ResolveModal(bResult)	// 확인 대기 끝
		    {
		        const	funcResolve	= Process.funcModal;

		        Process.funcModal = null;

		        funcResolve?.(bResult);
		    }

		    static CloseModal(bResult = false, bForce = false)	// 공통 팝업 닫기 (저장 중 잠김: 안 닫힘, bForce: 잠김 풀고 닫음)
		    {
		        if (bForce)
		        {
		            delete Process.kDialog.dataset.locked;
		        }

		        if (Process.kDialog.dataset.locked)
		        {
		            return;
		        }

		        if (Process.kDialog.open)
		        {
		            Process.kDialog.close();
		        }

		        Process.ResolveModal(bResult);
		    }

		    static ConfirmOp(sOp, sTitle, arrItem, arrBlock, sTypeName, arrStage)	// 실행 전 확인: 대상·그룹·제외 수, 실행 순서, 대상 목록 (그룹별), 제외 이유, sTypeName: 이름 입력 일치 → 실행 켜짐 (반환: Promise 확인 여부)
		    {
		        const	mapGroup	= new Map();	// 그룹 → 대상

		        for (const kRow of arrItem)
		        {
		            const	sGroup	= Process.GetGroup(kRow.sField);

		            if (!mapGroup.has(sGroup))
		            {
		                mapGroup.set(sGroup, []);
		            }

		            mapGroup.get(sGroup).push(kRow);
		        }

		        const	kOp	= Process.OP[sOp];
		        const sFlow		= arrStage != null && arrStage.length > 1 ? `<div class="flow"><span class="muted">실행 순서</span>${arrStage.map(kStage => `<span class="st">${Process.Escape(kStage.sName)} <span class="muted">${kStage.arrItem.length}</span></span>`).join(`<span class="ar">→</span>`)}<span class="muted">· 그룹이 모두 ${sOp === "On" ? "Running" : "Stopped"}이 되면 다음 그룹 진행${sOp === "On" ? ", 실패 시 중단" : ""}</span></div>` : "";
		        const sList		= [...mapGroup].map(([sGroup, arrList]) => `<div class="tg">${Process.Escape(Process.GetTarget(sGroup))} <span class="muted">${arrList.length}</span></div><div class="ti">${arrList.map(kRow => `<span>${Process.Escape(Process.GetLabel(kRow.sField))}</span>`).join("")}</div>`).join("");
		        const sBlock	= arrBlock.length > 0 ? `<div class="notice warn"><b>제외 ${arrBlock.length}개</b><ul>${arrBlock.slice(0, Process.BLOCK_MAX).map(([kRow, sWhy]) => `<li>${Process.Escape(Process.GetLabel(kRow.sField))} — ${Process.Escape(sWhy)}</li>`).join("")}${arrBlock.length > Process.BLOCK_MAX ? `<li>외 ${arrBlock.length - Process.BLOCK_MAX}개</li>` : ""}</ul></div>` : "";
		        const sType		= sTypeName ? `<div class="typecheck"><label for="ProcessTypeInput">실행하려면 <code>${Process.Escape(sTypeName)}</code> 을(를) 입력하세요</label><input class="mono" id="ProcessTypeInput" autocomplete="off" spellcheck="false" autocapitalize="off" autofocus></div>` : "";
		        const kDialog	= Process.OpenModal(`<div class="title">${kOp.sIcon} ${Process.Escape(sTitle)}</div><div class="dbody"><div class="impact"><div class="box"><b>${arrItem.length}</b><span>${kOp.sName} 대상</span></div><div class="box"><b>${mapGroup.size}</b><span>그룹</span></div>${arrBlock.length > 0 ? `<div class="box block"><b>${arrBlock.length}</b><span>제외</span></div>` : ""}</div>${sFlow}<div class="tlist">${sList}</div>${sBlock}${sType}</div>`
		            + `<div class="toolright"><button type="button" class="primary" id="ProcessOk" data-act="modalOk" title="실행(Enter)"${sTypeName ? " disabled" : " autofocus"}>${kOp.sIcon} ${arrItem.length}개 ${kOp.sName}</button><button type="button" data-act="modalCancel" title="취소(Esc)">취소</button></div>`);

		        if (sTypeName)	// 이름 입력: 일치 → 실행 켜짐, Enter: 일치 시 실행 (한글 조합 중 제외)
		        {
		            const	kInput	= kDialog.querySelector("#ProcessTypeInput");
		            const	kOk		= kDialog.querySelector("#ProcessOk");

		            kInput.addEventListener("input", () => kOk.disabled = kInput.value.trim() !== sTypeName);

		            kInput.addEventListener("keydown", (kEvent) =>
		            {
		                if (kEvent.key === "Enter" && !kEvent.isComposing && kInput.value.trim() === sTypeName)
		                {
		                    Process.CloseModal(true);
		                }
		            });
		        }

		        return new Promise((funcResolve) => Process.funcModal = funcResolve);
		    }

		    static GetTargets(sScope, sId)	// 작업 대상 (srv: 서버, grp: 중분류, sec: 대분류)
		    {
		        if (sScope === "srv")
		        {
		            return [Process.FindRow(sId)].filter(Boolean);
		        }

		        return sScope === "grp" ? Process.GetGroupRows(sId) : (Process.FindSec(sId)?.arrRow ?? []);
		    }

		    static GetScopeName(sScope, sId)	// 작업 대상 이름
		    {
		        if (sScope === "srv")
		        {
		            return Process.GetLabel(sId);
		        }

		        return sScope === "grp" ? Process.GetGroupLabel(sId) : (Process.FindSec(sId)?.sLabel ?? sId);
		    }

		    static async RunOp(sOp, sScope, sId)	// 시작·정지·다운: 대상 = 적용 대기 아님 + 지금 상태와 다름 (적용 대기: 제외), 확인 → 상태,False → HMXClient 처리, 대분류: 그룹 순차 (이름 입력 확인)
		    {
		        const	arrAll	= Process.GetTargets(sScope, sId);

		        if (arrAll.length === 0)
		        {
		            return;
		        }

		        const	sName	= Process.GetScopeName(sScope, sId);

		        if (sOp === "Down")
		        {
		            Process.RunDown(sScope, sId, arrAll, sName);

		            return;
		        }

		        if (sScope === "srv")	// 불 켜진 버튼·적용 대기: 안내만
		        {
		            const	kInfo	= Process.GetInfo(arrAll[0]);

		            if (kInfo.sStatus === sOp)
		            {
		                Process.Toast(`이미 선택된 값입니다.\n${sName} — ${sOp === "On" ? "이미 실행 중" : "이미 정지됨"}`, "fail");

		                return;
		            }

		            if (kInfo.sApply === "False")
		            {
		                Process.Toast("적용 대기 중입니다. 잠시 후 다시 시도하세요.", "warn");

		                return;
		            }
		        }

		        const	arrItem		= arrAll.filter(kRow => Process.GetInfo(kRow).sApply !== "False" && Process.GetInfo(kRow).sStatus !== sOp);
		        const	arrBlock	= arrAll.filter(kRow => Process.GetInfo(kRow).sApply === "False").map(kRow => [kRow, "적용 대기 중"]);

		        if (arrItem.length === 0)
		        {
		            Process.Toast(`이미 선택된 값입니다.\n변경할 항목이 없습니다 (${sOp === "On" ? "이미 모두 실행 중" : "이미 모두 정지"}).`, "fail");

		            return;
		        }

		        const	bSequence	= sScope === "sec";
		        const sTitle	= `${sName}${bSequence ? " 전체" : ""} ${Process.OP[sOp].sName}`;
		        let	arrStage	= [{ sName, arrItem }];

		        if (bSequence)	// 화면 순서 그룹 (정지: 아래 그룹부터)
		        {
		            const	arrOrder	= [];

		            for (const kSec of Process.GetModel())
		            {
		                for (const kGroup of kSec.arrGroup)
		                {
		                    if (arrItem.some(kRow => Process.GetGroup(kRow.sField) === kGroup.sId))
		                    {
		                        arrOrder.push(kGroup.sId);
		                    }
		                }
		            }

		            if (sOp === "Off")
		            {
		                arrOrder.reverse();
		            }

		            arrStage = arrOrder.map(sGroup => ({ sName: Process.GetGroupLabel(sGroup), arrItem: arrItem.filter(kRow => Process.GetGroup(kRow.sField) === sGroup) }));
		        }

		        if (!await Process.ConfirmOp(sOp, sTitle, arrItem, arrBlock, bSequence ? sName : null, arrStage))
		        {
		            return;
		        }

		        Process.WriteActLog({ srv: "개별", grp: "그룹", sec: "전체" }[sScope], sScope === "sec" ? arrItem[0].sField.split(",")[0].trim() : Process.GetTarget(sId), sOp);	// 클릭 기록 (전체: 첫째 파트, 그룹: 첫·둘째, 개별: 전체 키)

		        if (bSequence)
		        {
		            Process.RunJob(sTitle, sOp, arrStage);
		        }
		        else
		        {
		            await Process.ApplyAll(arrItem, sOp);
		        }
		    }

		    static async SetToggle(sField, sValue)	// 시작·정지·다운 값 저장 (실패: TRY_COUNT 번까지, 반환: 마지막 응답)
		    {
		        let	kData	= { success: false, error: "" };

		        for (let nTry = 0; nTry < Process.TRY_COUNT; ++nTry)
		        {
		            try
		            {
		                kData = await Process.SetField(sField, sValue, "toggle", false);

		                if (kData.success)
		                {
		                    break;
		                }
		            }
		            catch (kError)
		            {
		                kData = { success: false, error: ErrorText(kError) };
		            }
		        }

		        return kData;
		    }

		    static async ApplyAll(arrItem, sOp)	// 대상 모두 "상태,False" (뒤 파트 유지) → HMXClient 처리 후 True, 반환: 실패 목록 (토스트 X건 + 외 N건)
		    {
		        const	arrFail	= [];

		        await Promise.all(arrItem.map(async (kRow) =>
		        {
		            const	sValue	= Process.BuildValue(kRow.sValue, sOp, "False");
		            const	kData	= await Process.SetToggle(kRow.sField, sValue);

		            if (kData.success)
		            {
		                Process.ApplyLocal(kRow.sField, sValue);
		            }
		            else
		            {
		                arrFail.push(`${Process.GetLabel(kRow.sField)} → ${kData.error || "Unknown Error"}`);
		            }
		        }));

		        if (arrFail.length > 0)
		        {
		            Process.Toast(`${Process.OP[sOp].sName} 실패: ${arrFail.length}건\n${arrFail.slice(0, Process.LIST_MAX).join("\n")}${arrFail.length > Process.LIST_MAX ? `\n외 ${arrFail.length - Process.LIST_MAX}건` : ""}`, "fail");
		        }

		        return arrFail;
		    }

		    static WaitGroup(arrField, sOp)	// 그룹 모두 원하는 상태 대기 (POLL_MS 마다, 시작: 하나라도 Off,True 되돌아감 → 실패) → "ok"·"fail"
		    {
		        return new Promise((funcResolve) =>
		        {
		            const nTimer = setInterval(() =>
		            {
		                let	nDone	= 0;
		                let	bFail	= false;

		                for (const sField of arrField)
		                {
		                    const	kRow	= Process.FindRow(sField);

		                    if (kRow == null)
		                    {
		                        continue;
		                    }

		                    const	kInfo	= Process.GetInfo(kRow);
		                    const	sHead	= kInfo.sStatus + "," + kInfo.sApply;

		                    if (sOp === "On" && sHead === "Off,True")	// 실행 실패로 되돌아감
		                    {
		                        bFail = true;
		                    }
		                    else if (sHead === (sOp === "On" ? "On,True" : "Off,True"))
		                    {
		                        ++nDone;
		                    }
		                }

		                if (bFail || nDone === arrField.length)
		                {
		                    clearInterval(nTimer);

		                    funcResolve(bFail ? "fail" : "ok");
		                }
		            }, Process.POLL_MS);
		        });
		    }

		    static async RunJob(sTitle, sOp, arrStage)	// 대분류 전체 순차: 그룹 시작 때 다시 확인 → 적용 → 모두 Running·Stopped: 다음 그룹, 요청 실패·시작 실패: 중단 + 뒤 그룹 건너뜀 (진행 팝업 없음, 끝: 토스트)
		    {
		        const	kJob	= { sTitle, sResult: "" };

		        Process.setJob.add(kJob);

		        try
		        {
		            for (const kStage of arrStage)
		            {
		                const	arrLive	= kStage.arrItem.map(kRow => Process.FindRow(kRow.sField)).filter(kRow => kRow != null && Process.GetInfo(kRow).sApply !== "False" && Process.GetInfo(kRow).sStatus !== sOp);	// 그사이 바뀐 서버 뺌

		                if (arrLive.length === 0)
		                {
		                    continue;
		                }

		                const	arrFail	= await Process.ApplyAll(arrLive, sOp);
		                const	sWait	= arrFail.length > 0 ? "setfail" : await Process.WaitGroup(arrLive.map(kRow => kRow.sField), sOp);	// 요청 실패: 대기 안 함

		                if (sWait === "ok")
		                {
		                    continue;
		                }

		                kJob.sResult = "fail";

		                Process.Toast(`${sTitle} 중단 — ${kStage.sName}: ${sWait === "setfail" ? `요청 실패 (재시도 ${Process.TRY_COUNT}회)` : "실행 실패 (Off,True로 되돌아감)"}. 이후 그룹은 진행하지 않았습니다.`, "fail");

		                break;
		            }

		            if (kJob.sResult === "")
		            {
		                kJob.sResult = "ok";

		                Process.Toast(`${sTitle} 완료`, "ok");
		            }
		        }
		        finally
		        {
		            Process.setJob.delete(kJob);	// 끝·예외: 진행 중 아님
		        }
		    }

		    static async RunDown(sScope, sId, arrAll, sName)	// 다운 (Git 갱신만, 프로세스 미실행): 실행 중 (On) 있음 → 전체 안 함, 적용 대기: 제외, 대분류: 이름 입력 확인 → Down,False
		    {
		        const	arrRun	= arrAll.filter(kRow => Process.GetInfo(kRow).sStatus === "On");

		        if (arrRun.length > 0)
		        {
		            Process.Toast(`실행 중(On)인 항목은 다운로드할 수 없습니다. 먼저 Off 하세요.\n${arrRun.slice(0, Process.LIST_MAX).map(kRow => Process.GetLabel(kRow.sField)).join(", ")}${arrRun.length > Process.LIST_MAX ? ` 외 ${arrRun.length - Process.LIST_MAX}개` : ""}`, "fail");

		            return;
		        }

		        const	arrItem		= arrAll.filter(kRow => Process.GetInfo(kRow).sApply === "True");
		        const	arrBlock	= arrAll.filter(kRow => Process.GetInfo(kRow).sApply !== "True").map(kRow => [kRow, "적용 대기 중"]);

		        if (arrItem.length === 0)
		        {
		            Process.Toast("다운로드할 항목이 없습니다. (적용 대기 상태는 제외)", "warn");

		            return;
		        }

		        if (!await Process.ConfirmOp("Down", `${sName} 다운로드`, arrItem, arrBlock, sScope === "sec" ? sName : null, null))
		        {
		            return;
		        }

		        Process.WriteActLog({ srv: "개별", grp: "그룹", sec: "전체" }[sScope], sScope === "sec" ? sName : Process.GetTarget(sId), "Down");

		        await Process.ApplyAll(arrItem, "Down");
		    }

		    static async TestAll(bOn)	// TestOn·TestOff: 확인 → 모든 서버 바로 On,True·Off,True,0 (HMXClient 안 거침, 테스트용)
		    {
		        const arrItem = Process.arrRow.filter(kRow =>
		        {
		            const	arrPart	= kRow.sValue.split(",");

		            return bOn ? `${arrPart[0]},${arrPart[1]}` !== "On,True" : `${arrPart[0]},${arrPart[1]},${arrPart[2]}` !== "Off,True,0";
		        });

		        if (arrItem.length === 0)
		        {
		            Process.Toast("변경할 항목이 없습니다.", "warn");

		            return;
		        }

		        if (!await Process.ConfirmOp(bOn ? "On" : "Off", bOn ? "TestOn (즉시 On,True)" : "TestOff (즉시 Off,True,0)", arrItem, [], null, null))
		        {
		            return;
		        }

		        await Promise.all(arrItem.map(async (kRow) =>
		        {
		            const	arrPart	= kRow.sValue.split(",");

		            arrPart[0] = "Off";
		            arrPart[1] = "True";
		            arrPart[2] = "0";

		            const	sValue	= bOn ? Process.BuildValue(kRow.sValue, "On", "True") : arrPart.join(",");

		            try
		            {
		                if ((await Process.SetField(kRow.sField, sValue, "toggle", false)).success)
		                {
		                    Process.ApplyLocal(kRow.sField, sValue);
		                }
		            }
		            catch (kError)	// 다음 목록 갱신에 반영
		            {
		            }
		        }));
		    }

		    static OpenForm(sMode, kSrc = null)	// 서버 등록 (add)·설정 편집 (edit)·복제 (clone) 넓은 팝업 (검사 통과해야 닫힘 → 입력값 유지)
		    {
		        if (sMode !== "add" && kSrc == null)	// 사라진 서버
		        {
		            return;
		        }

		        const	bEdit		= sMode === "edit";
		        const	arrKey		= kSrc != null ? kSrc.sField.split(",") : ["", "", ""];
		        const	kInfo		= kSrc != null ? Process.GetInfo(kSrc) : { sMachine: "", sFolder: "", sRepo: "", sSync: "1", sMode: "0", sCmd: "" };
		        const	sState		= bEdit ? kSrc.sValue.split(",").slice(0, 4).join(",") : "Off,True,0,0";
		        const	bRunning	= bEdit && Process.GetState(kSrc) !== "stopped" && Process.GetState(kSrc) !== "failed";
		        const	sName		= arrKey.slice(2).join(",") + (sMode === "clone" ? "_copy" : "");
		        const	arrSec		= [...new Set(Process.arrRow.map(kRow => kRow.sField.split(",")[0]))];
		        const	arrGroup	= [...new Set(Process.arrRow.map(kRow => kRow.sField.split(",")[1]).filter(sPart => sPart !== undefined))];
		        const	mapMachine	= new Map();	// 머신 → 서버 수

		        for (const kRow of Process.arrRow)
		        {
		            const	sMachine	= Process.GetInfo(kRow).sMachine;

		            if (sMachine !== "")
		            {
		                mapMachine.set(sMachine, (mapMachine.get(sMachine) ?? 0) + 1);
		            }
		        }

		        const	arrMachine	= [...mapMachine].sort((arrA, arrB) => arrA[0].localeCompare(arrB[0]));	// [[머신, 서버 수]]
		        const funcOption	= (arrValue) => arrValue.map(sValue => `<option value="${Process.Escape(sValue)}">`).join("");
		        const kDialog		= Process.OpenModal(`<div class="title">${bEdit ? "서버 설정 편집" : (sMode === "clone" ? "서버 복제하여 등록" : "서버 등록")}</div><form id="ProcessForm" class="setting" autocomplete="off">`
		            + (bEdit ? "" : `<label for="ProcessFormClone">기존 서버에서 값 가져오기 <span class="opt">(선택)</span></label><div class="field"><select id="ProcessFormClone"><option value="">— 빈 양식으로 시작 —</option>${Process.arrRow.map(kRow => `<option${kSrc != null && kSrc.sField === kRow.sField ? " selected" : ""}>${Process.Escape(kRow.sField)}</option>`).join("")}</select></div>`)
		            + (bRunning ? `<div class="notice warn">실행 중인 서버입니다. 실행 설정 변경은 다음 시작 때 적용됩니다.</div>` : "")
		            + `<div class="form-sec">String (Redis 키) — 대분류,중분류,이름</div>`
		            + `<label for="ProcessFormSec">대분류</label><div class="field"><input id="ProcessFormSec" list="ProcessSecList" value="${Process.Escape(arrKey[0])}" title="대분류" spellcheck="false" autocapitalize="off"><datalist id="ProcessSecList">${funcOption(arrSec)}</datalist><span class="err">대분류를 입력하세요</span></div>`
		            + `<label for="ProcessFormGroup">중분류</label><div class="field"><input id="ProcessFormGroup" list="ProcessGroupList" value="${Process.Escape(arrKey[1] ?? "")}" title="중분류" spellcheck="false" autocapitalize="off"><datalist id="ProcessGroupList">${funcOption(arrGroup)}</datalist><span class="err">중분류를 입력하세요</span></div>`
		            + `<label for="ProcessFormName">이름</label><div class="field"><input id="ProcessFormName" value="${Process.Escape(sName)}" spellcheck="false" autocapitalize="off"${bEdit ? "" : " autofocus"}><span class="err">이름을 입력하세요</span></div>`
		            + `<div class="form-sec">실행 위치</div>`
		            + `<label for="ProcessFormMachine">머신이름 <span class="opt">(${arrMachine.length}대)</span></label><div class="field"><div class="combo"><input class="mono" id="ProcessFormMachine" value="${Process.Escape(kInfo.sMachine)}" spellcheck="false" autocapitalize="off"><button type="button" class="cb-btn" tabindex="-1" title="머신 목록" aria-label="머신 목록">▾</button><div class="cb-list" hidden></div></div><span class="err">머신이름을 입력하세요</span></div>`
		            + `<label for="ProcessFormFolder">다운폴더</label><div class="field"><input class="mono" id="ProcessFormFolder" value="${Process.Escape(kInfo.sFolder)}" title="다운 폴더" spellcheck="false" autocapitalize="off"><span class="err">드라이브 경로(C:\\…)로 입력하세요</span></div>`
		            + `<label for="ProcessFormRepo">깃헙폴더</label><div class="field"><input class="mono" id="ProcessFormRepo" value="${Process.Escape(kInfo.sRepo)}" spellcheck="false" autocapitalize="off"><span class="help">다운폴더 안의 저장소 폴더</span></div>`
		            + `<div class="form-sec">실행 방식</div>`
		            + `<label for="ProcessFormSync">Sync</label><div class="field"><label class="switch"><input type="checkbox" id="ProcessFormSync"${kInfo.sSync !== "0" ? " checked" : ""}> 시작할 때 Git 갱신</label><span class="help">끄면 현재 폴더 그대로 실행 (Sync=0)</span></div>`
		            + `<label>Mode</label><div class="field"><div class="seg"><label><input type="radio" name="ProcessFormMode" value="0"${kInfo.sMode !== "1" ? " checked" : ""}>0 · Detached 독립실행</label><label><input type="radio" name="ProcessFormMode" value="1"${kInfo.sMode === "1" ? " checked" : ""}>1 · Graceful 콘솔+Ctrl+C</label></div></div>`
		            + `<label for="ProcessFormCmd">실행파일+인자들</label><div class="field"><input class="mono" id="ProcessFormCmd" value="${Process.Escape(kInfo.sCmd)}" title="실행 명령" spellcheck="false" autocapitalize="off"><span class="err">실행파일을 입력하세요</span></div>`
		            + `<details class="adv"><summary>고급 — State 직접 편집 (상태,적용,PID,커밋해시)</summary><div class="muted">State는 HMXClient가 관리합니다. 꼬였을 때만 수정하세요.</div><label class="switch"><input type="checkbox" id="ProcessFormUnlock"> 직접 편집 허용</label><div class="field"><input class="mono" id="ProcessFormState" value="${Process.Escape(sState)}" spellcheck="false" autocapitalize="off" readonly><span class="err">State는 비울 수 없습니다</span></div></details>`
		            + `<div class="preview" id="ProcessFormPreview"></div></form>`
		            + `<div class="toolright"><button type="button" class="primary" data-act="saveForm" data-id="${bEdit ? Process.Escape(kSrc.sField) : ""}" title="저장(Enter)">${bEdit ? "저장" : "등록"}</button><button type="button" data-act="modalCancel" title="취소(Esc)">취소</button></div>`, "wide");
		        const	kForm		= kDialog.querySelector("#ProcessForm");
		        const	kPreview	= kDialog.querySelector("#ProcessFormPreview");

		        const funcPreview = () =>	// 미리보기: String·값
		        {
		            const	kRead	= Process.ReadForm(kDialog);

		            kPreview.innerHTML = `<div><b>String</b>${Process.Escape(kRead.sField)}</div><div><b>값</b>${Process.Escape(kRead.sFull)}</div>`;
		        };

		        kDialog.querySelector("#ProcessFormUnlock").addEventListener("change", (kEvent) => kDialog.querySelector("#ProcessFormState").readOnly = !kEvent.target.checked);
		        kDialog.querySelector("#ProcessFormClone")?.addEventListener("change", (kEvent) => { const kClone = Process.FindRow(kEvent.target.value); Process.OpenForm(kClone != null ? "clone" : "add", kClone ?? null); });	// 기존 서버 고름: 복제 양식

		        Process.BindCombo(kDialog.querySelector("#ProcessFormMachine"), arrMachine);

		        kForm.addEventListener("input", funcPreview);
		        kForm.addEventListener("submit", (kEvent) => kEvent.preventDefault());

		        kForm.addEventListener("keydown", (kEvent) =>	// Enter: 저장 (한글 조합 중 제외)
		        {
		            if (kEvent.key === "Enter" && !kEvent.isComposing)
		            {
		                kEvent.preventDefault();

		                Process.SaveForm(bEdit ? kSrc.sField : "");
		            }
		        });

		        funcPreview();
		    }

		    static BindCombo(kInput, arrItem)	// 콤보 입력 (머신이름): 직접 입력 + 누름·▾·↓ 전체 목록 (값 + 서버 수), 입력: 부분 일치, ↑↓ 이동·Enter 고름 (저장 대신)·Esc 목록만 닫기, 아래 공간 부족: 위로
		    {
		        const	kBox		= kInput.parentElement;
		        const	kList		= kBox.querySelector(".cb-list");
		        let		nIndex		= -1;	// 고른 줄 (-1: 없음)
		        const	funcRows	= () => kList.querySelectorAll("[data-v]");

		        const funcShow = (bAll) =>	// 목록 펼침 (bAll: 전체, 아님: 입력 부분 일치)
		        {
		            const	sNow	= kInput.value.trim();
		            const	sKey	= bAll ? "" : sNow.toLowerCase();
		            const	arrHit	= arrItem.filter(([sValue]) => sValue.toLowerCase().includes(sKey));

		            nIndex = arrHit.findIndex(([sValue]) => sValue === sNow);

		            kList.innerHTML	= arrHit.length > 0 ? arrHit.map(([sValue, nCount], nRow) => `<div class="${nRow === nIndex ? "on cur" : ""}" data-v="${Process.Escape(sValue)}"><span>${Process.Escape(sValue)}</span><small>${nCount}개</small></div>`).join("") : `<p class="cb-empty">일치하는 머신 없음 — 입력값 그대로 사용</p>`;
		            kList.hidden	= false;

		            const	kRect	= kInput.getBoundingClientRect();
		            const	nHeight	= kList.offsetHeight;
		            const	bUp		= kRect.bottom + 2 + nHeight > innerHeight && kRect.top - 2 - nHeight > 0;	// 아래 공간 부족: 위로

		            kList.style.top		= bUp ? "auto" : "calc(100% + 2px)";
		            kList.style.bottom	= bUp ? "calc(100% + 2px)" : "auto";

		            funcRows()[nIndex]?.scrollIntoView({ block: "nearest" });
		        };

		        const funcHide = () =>
		        {
		            kList.hidden	= true;
		            nIndex			= -1;
		        };

		        const funcPick = (sValue) =>	// 고름: 미리보기 갱신
		        {
		            kInput.value = sValue;

		            funcHide();
		            kInput.dispatchEvent(new Event("input", { bubbles: true }));
		        };

		        const funcMove = (nStep) =>	// ↑↓ 이동 (끝: 반대쪽)
		        {
		            const	arrRow	= funcRows();

		            if (arrRow.length === 0)
		            {
		                return;
		            }

		            nIndex = nIndex < 0 ? (nStep > 0 ? 0 : arrRow.length - 1) : (nIndex + nStep + arrRow.length) % arrRow.length;

		            arrRow.forEach((kRow, nRow) => kRow.classList.toggle("on", nRow === nIndex));
		            arrRow[nIndex].scrollIntoView({ block: "nearest" });
		        };

		        kBox.querySelector(".cb-btn").addEventListener("mousedown", (kEvent) =>	// ▾: 펼침·접음 (포커스 유지)
		        {
		            kEvent.preventDefault();

		            if (kList.hidden)
		            {
		                kInput.focus();
		                funcShow(true);
		            }
		            else
		            {
		                funcHide();
		            }
		        });

		        kList.addEventListener("mousedown", (kEvent) =>	// 목록 누름: 고름 (포커스 유지)
		        {
		            kEvent.preventDefault();

		            const	kItem	= kEvent.target.closest("[data-v]");

		            if (kItem != null)
		            {
		                funcPick(kItem.dataset.v);
		            }
		        });

		        kInput.addEventListener("click", () => kList.hidden && funcShow(true));
		        kInput.addEventListener("input", (kEvent) => kEvent.isTrusted && funcShow(false));
		        kInput.addEventListener("blur", funcHide);

		        kInput.addEventListener("keydown", (kEvent) =>
		        {
		            if (kEvent.isComposing)
		            {
		                return;
		            }

		            if (kEvent.key === "ArrowDown" || kEvent.key === "ArrowUp")
		            {
		                kEvent.preventDefault();

		                if (kList.hidden)
		                {
		                    funcShow(true);
		                }
		                else
		                {
		                    funcMove(kEvent.key === "ArrowDown" ? 1 : -1);
		                }
		            }
		            else if (!kList.hidden && kEvent.key === "Enter")	// 고른 줄 있음: 저장 대신 고름
		            {
		                if (nIndex >= 0)
		                {
		                    kEvent.preventDefault();
		                    kEvent.stopPropagation();

		                    funcPick(funcRows()[nIndex].dataset.v);
		                }
		                else
		                {
		                    funcHide();
		                }
		            }
		            else if (!kList.hidden && kEvent.key === "Escape")	// 팝업 대신 목록만 닫음
		            {
		                kEvent.preventDefault();
		                kEvent.stopPropagation();

		                funcHide();
		            }
		        });
		    }

		    static ReadForm(kDialog)	// 폼 값 → { arrKey, sField, arrValue, sState, sFull (State + 값) }
		    {
		        const	funcGet		= (sId) => kDialog.querySelector("#" + sId);
		        const	arrKey		= [funcGet("ProcessFormSec").value, funcGet("ProcessFormGroup").value, funcGet("ProcessFormName").value];
		        const	arrValue	= [funcGet("ProcessFormMachine").value.trim(), funcGet("ProcessFormFolder").value.trim(), funcGet("ProcessFormRepo").value.trim(), funcGet("ProcessFormSync").checked ? "1" : "0", kDialog.querySelector("input[name=ProcessFormMode]:checked").value, funcGet("ProcessFormCmd").value.trim()];
		        const	sValue		= arrValue.some(sPart => sPart !== "") ? arrValue.join(",") : "";
		        const	sState		= funcGet("ProcessFormState").value.trim();

		        return { arrKey, sField: arrKey.join(",").trim(), arrValue, sState, sFull: sState + (sValue !== "" ? "," + sValue : "") };
		    }

		    static async SaveForm(sOldField)	// 등록·수정 저장: 검사 (빈칸·같은 키·다운폴더 드라이브 경로·State) → 틀림: 빨강 + 첫 칸 포커스 (팝업 유지), 키 바뀜: 이름 변경 후 저장 → 성공: 그 중분류 펼침 + 다시 읽기 + 토스트
		    {
		        const	kDialog		= Process.kDialog;
		        const	kRead		= Process.ReadForm(kDialog);
		        const	bDuplicate	= kRead.sField !== sOldField && Process.FindRow(kRead.sField) != null;
		        let		bBad		= false;

		        const funcMark = (sId, bWrong, sText) =>	// 칸 오류 표시 (sText: 오류 글 바꿈)
		        {
		            const	kField	= kDialog.querySelector("#" + sId).closest(".field");

		            kField.classList.toggle("invalid", bWrong);

		            if (sText)
		            {
		                kField.querySelector(".err").textContent = sText;
		            }

		            return bWrong;
		        };

		        if (kDialog.dataset.locked)	// 저장 중 (Enter 연타)
		        {
		            return;
		        }

		        const	funcChar	= (sText) => /[:|]/.test(sText);	// 키 금지 글자 (: 존 구분·| 목록 구분)

		        bBad = funcMark("ProcessFormSec", kRead.arrKey[0].trim() === "" || funcChar(kRead.arrKey[0]), funcChar(kRead.arrKey[0]) ? "':'·'|' 사용 불가" : "대분류를 입력하세요") || bBad;
		        bBad = funcMark("ProcessFormGroup", kRead.arrKey[1].trim() === "" || funcChar(kRead.arrKey[1]), funcChar(kRead.arrKey[1]) ? "':'·'|' 사용 불가" : "중분류를 입력하세요") || bBad;
		        bBad = funcMark("ProcessFormName", kRead.arrKey[2].trim() === "" || bDuplicate || funcChar(kRead.arrKey[2]), kRead.arrKey[2].trim() === "" ? "이름을 입력하세요" : (funcChar(kRead.arrKey[2]) ? "':'·'|' 사용 불가" : "이미 존재하는 String 입니다: " + kRead.sField)) || bBad;
		        bBad = funcMark("ProcessFormMachine", kRead.arrValue[0] === "") || bBad;
		        bBad = funcMark("ProcessFormFolder", !/^[A-Za-z]:\\/.test(kRead.arrValue[1])) || bBad;
		        bBad = funcMark("ProcessFormCmd", kRead.arrValue[5] === "") || bBad;

		        if (funcMark("ProcessFormState", kRead.sState === ""))	// State 빈칸: 고급 펼침
		        {
		            bBad = true;

		            kDialog.querySelector("details.adv").open = true;
		        }

		        if (bBad)
		        {
		            kDialog.querySelector(".field.invalid input")?.focus();

		            return;
		        }

		        const	kSave	= kDialog.querySelector("[data-act=saveForm]");

		        kSave.disabled			= true;
		        kDialog.dataset.locked	= "saving";

		        try
		        {
		            if (sOldField !== "" && kRead.sField !== sOldField)	// 키 바뀜: 이름 변경 먼저 (버전 검증)
		            {
		                const	kRename	= await Process.Api("action=rename&field=" + encodeURIComponent(sOldField) + "&newfield=" + encodeURIComponent(kRead.sField) + "&version=" + Process.nVersion);

		                if (Process.CheckStale(kRename))
		                {
		                    return;
		                }

		                if (!kRename.success)
		                {
		                    throw new Error(kRename.error);
		                }

		                if (kRename.version)
		                {
		                    Process.SetVersion(kRename.version);
		                }
		            }

		            const	kData	= await Process.SetField(kRead.sField, kRead.sFull, sOldField !== "" ? "edit" : "add", true);

		            if (Process.CheckStale(kData))
		            {
		                return;
		            }

		            if (!kData.success)
		            {
		                throw new Error(kData.error);
		            }

		            Process.setExpand.add("g:" + Process.GetGroup(kRead.sField));

		            Process.CloseModal(true, true);
		            Process.Reload(`${sOldField !== "" ? "저장했습니다" : "등록했습니다"}: ${Process.GetTarget(kRead.sField)}`);
		        }
		        catch (kError)
		        {
		            delete kDialog.dataset.locked;

		            kSave.disabled = false;

		            Process.Toast(`${sOldField !== "" ? "저장" : "등록"} 실패: ${ErrorText(kError)}`, "fail");
		        }
		    }

		    static OpenDelete(sField)	// 서버 삭제 확인 (HMX 삭제 팝업, Enter: 취소), 실행 중·적용 대기: 삭제 꺼짐 (Running: 먼저 정지 버튼)
		    {
		        const	kRow	= Process.FindRow(sField);

		        if (kRow == null)
		        {
		            return;
		        }

		        const	sState	= Process.GetState(kRow);
		        const	kInfo	= Process.GetInfo(kRow);
		        const	bBlock	= kInfo.sStatus === "On" || Process.IsTransit(sState);

		        AskDelete("서버 삭제", Process.GetLabel(sField), bBlock ? `${Process.STATE_NAME[sState]} · 실행 중이거나 적용 대기 중인 서버는 삭제할 수 없습니다. 삭제하면 프로세스는 계속 떠 있는데 HMX에서 관리할 수 없게 됩니다. 먼저 정지하세요.` : `String: ${sField} · 머신: ${kInfo.sMachine} · 다운폴더: ${kInfo.sFolder} · HMX 목록(Redis 키)만 삭제됩니다. 머신의 폴더와 파일은 그대로 남습니다.`, () => Process.RunDelete(sField));

		        Process.kDeleteYes.disabled		= bBlock;
		        Process.kDeleteStop.hidden		= !(bBlock && sState === "running");
		        Process.kDeleteStop.dataset.id	= sField;
		    }

		    static async RunDelete(sField)	// 삭제 (버전 검증) → 다시 읽기 + 토스트
		    {
		        try
		        {
		            const	kData	= await Process.Api("action=del&field=" + encodeURIComponent(sField) + "&version=" + Process.nVersion);

		            if (Process.CheckStale(kData))
		            {
		                return;
		            }

		            if (!kData.success)
		            {
		                Process.Toast(`삭제 실패: ${kData.error || "Unknown Error"}`, "fail");

		                return;
		            }

		            Process.Reload(`삭제했습니다: ${Process.GetTarget(sField)}`);
		        }
		        catch (kError)
		        {
		            Process.Toast(`삭제 실패: ${ErrorText(kError)}`, "fail");
		        }
		    }

		    static OpenRename(sScope, sId)	// 대분류 (첫째 값)·중분류 (둘째 값, 앞 공백 유지) 이름 변경: 새 이름 + 하위 키 미리보기 (기존 → 새 키)
		    {
		        const	arrList	= sScope === "sec" ? (Process.FindSec(sId)?.arrRow ?? []) : Process.GetGroupRows(sId);
		        const	sNow	= sScope === "sec" ? (Process.FindSec(sId)?.sLabel ?? "") : Process.GetGroupLabel(sId);

		        const funcKey = (sField, sValue) =>	// 새 키
		        {
		            const	arrPart	= sField.split(",");

		            if (sScope === "sec")
		            {
		                arrPart[0] = sValue;
		            }
		            else if (arrPart.length >= 2)
		            {
		                arrPart[1] = arrPart[1].match(/^\s*/)[0] + sValue;
		            }

		            return arrPart.join(",");
		        };

		        Process.kRename = { sScope, arrList, funcKey };

		        const kDialog	= Process.OpenModal(`<div class="title">${sScope === "sec" ? "대분류" : "중분류"} 이름 변경</div><div class="setting"><label for="ProcessRenameInput">새 이름 <span class="opt">(String ${sScope === "sec" ? "첫번째" : "두번째"} 값)</span></label><div class="field"><input class="mono" id="ProcessRenameInput" value="${Process.Escape(sNow)}" autocomplete="off" spellcheck="false" autocapitalize="off" autofocus><span class="err">새 이름을 입력하세요</span></div></div>`
		            + `<div class="muted">하위 ${arrList.length}개 키가 함께 바뀝니다</div><div class="tlist mono" id="ProcessRenamePreview"></div><div class="toolright"><button type="button" class="primary" data-act="renameOk" title="키 변경(Enter)">키 변경</button><button type="button" data-act="modalCancel" title="취소(Esc)">취소</button></div>`);
		        const	kInput		= kDialog.querySelector("#ProcessRenameInput");
		        const	kPreview	= kDialog.querySelector("#ProcessRenamePreview");

		        const funcPreview = () =>	// 하위 키: 기존 → 새 키
		        {
		            const	sValue	= kInput.value.trim() || "?";

		            kPreview.innerHTML = arrList.map(kRow => `<div class="ti">${Process.Escape(kRow.sField)} <span class="muted">→</span> <b>${Process.Escape(funcKey(kRow.sField, sValue))}</b></div>`).join("");
		        };

		        kInput.addEventListener("input", funcPreview);

		        kInput.addEventListener("keydown", (kEvent) =>	// Enter: 키 변경 (한글 조합 중 제외)
		        {
		            if (kEvent.key === "Enter" && !kEvent.isComposing)
		            {
		                Process.RunRename();
		            }
		        });

		        funcPreview();
		    }

		    static async RunRename()	// 이름 변경: 한 번 요청·버전 검증으로 일괄 (중복 실행 막음) → 다시 읽기 + 토스트
		    {
		        const	kDialog	= Process.kDialog;
		        const	kInput	= kDialog.querySelector("#ProcessRenameInput");
		        const	sValue	= kInput.value.trim();

		        if (sValue === "" || /[:|]/.test(sValue))	// 빈 값·키 금지 글자 (: 존 구분·| 목록 구분)
		        {
		            kInput.closest(".field").classList.add("invalid");

		            return;
		        }

		        const { sScope, arrList, funcKey } = Process.kRename;
		        const	arrRename	= arrList.map(kRow => ({ sOld: kRow.sField, sNew: funcKey(kRow.sField, sValue) })).filter(kItem => kItem.sOld !== kItem.sNew);
		        let		sError		= "";

		        if (arrRename.length === 0)
		        {
		            Process.Toast("변경할 항목이 없습니다.", "warn");

		            return;
		        }

		        if (kDialog.dataset.locked)	// 요청 중 (Enter 연타)
		        {
		            return;
		        }

		        kDialog.querySelector("[data-act=renameOk]").disabled	= true;
		        kDialog.dataset.locked									= "saving";

		        try
		        {
		            const	kData	= await FetchJson("api/process?action=rename&version=" + Process.nVersion, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ arrField: arrRename.map(kItem => kItem.sOld), arrNewField: arrRename.map(kItem => kItem.sNew) }) });	// 키 쌍: 본문 (대분류 키 많음: 쿼리 길이 제한 넘음 → 404)

		            if (Process.CheckStale(kData))
		            {
		                return;
		            }

		            if (kData.version)
		            {
		                Process.SetVersion(kData.version);
		            }

		            if (!kData.success)
		            {
		                sError = kData.error || "Unknown Error";
		            }
		        }
		        catch (kError)
		        {
		            sError = ErrorText(kError);
		        }

		        if (sScope === "grp")
		        {
		            Process.setExpand.add("g:" + Process.GetGroup(arrRename[0].sNew));
		        }

		        Process.CloseModal(true, true);
		        Process.Reload(sError !== "" ? `이름 변경 실패: ${sError}` : `이름 변경 완료 — ${arrRename.length}개 키`);
		    }

		    static OpenLog(sKey)	// 작업 로그 팝업 (큰 팝업, 로그에서 찾기·상세 로그: 검색어 채움): 최신 순 X줄 단위, 기간 칩·직접 지정 (다시 열기: 1시간)·종류 칩·검색 (지금 페이지)
		    {
		        if (sKey !== undefined)
		        {
		            Process.kLogState.sKey = sKey || "";
		        }

		        if (Process.kLogState.sRange === "custom")
		        {
		            Process.kLogState.sRange = "1h";
		        }

		        const sHour		= Array.from({ length: 24 }, (_, nIndex) => `<option>${Process.Pad(nIndex)}</option>`).join("");	// 24시간제
		        const sMinute	= Array.from({ length: 60 }, (_, nIndex) => `<option>${Process.Pad(nIndex)}</option>`).join("");
		        const kDialog	= Process.OpenModal(`<div class="resulthead"><div class="title">작업 로그</div><span class="hint">반영 로그 · 최신순 ${Process.PAGE_SIZE}줄 단위</span></div>`
		            + `<div class="filter"><div class="chips">${Process.LOG_RANGE.map(([sId, sName]) => `<button type="button" class="chip${Process.kLogState.sRange === sId ? " cur" : ""}" data-act="logRange" data-id="${sId}">${sName}</button>`).join("")}</div>`
		            + `<label title="시작 시간(포함)"><input type="date" id="ProcessLogStartDate"><select class="hour" id="ProcessLogStartHour">${sHour}</select><select class="hour" id="ProcessLogStartMinute">${sMinute}</select><span class="rangeop">이상</span></label>`
		            + `<label title="종료 시간(포함)"><input type="date" id="ProcessLogEndDate"><select class="hour" id="ProcessLogEndHour">${sHour}</select><select class="hour" id="ProcessLogEndMinute">${sMinute}</select><span class="rangeop">이하</span></label>`
		            + `<button type="button" data-act="logCustom" title="기간 조회">조회</button></div>`
		            + `<div class="filter"><div class="chips" id="ProcessLogTypes"></div><input type="text" id="ProcessLogKey" title="로그 검색" value="${Process.Escape(Process.kLogState.sKey)}" autocomplete="off" spellcheck="false" autocapitalize="off"></div>`
		            + `<div class="scrollwrap"><div class="scroll" id="ProcessLogScroll"><table class="log"><thead><tr><th class="c-time">시간</th><th class="c-level">레벨</th><th class="c-type">종류</th><th>내용</th></tr></thead><tbody id="ProcessLogBody"></tbody></table></div><div class="progress" id="ProcessLogProgress" hidden></div></div>`
		            + `<div class="toolright"><span class="hint" id="ProcessLogCount"></span><button type="button" data-act="logPage" data-id="prev">▲ 더 최신</button><button type="button" data-act="logPage" data-id="next">더 과거 ▼</button><button type="button" data-act="modalCancel" autofocus>닫기</button></div>`, "xl");

		        kDialog.querySelector("#ProcessLogKey").addEventListener("input", (kEvent) => { Process.kLogState.sKey = kEvent.target.value; Process.RenderLog(); });

		        Process.SetLogRange(Process.kLogState.sRange);
		        Process.LoadLog("");
		    }

		    static SetLogRange(sRange)	// 기간 칩 → S·E (끝: 지금, 오늘: 0시부터)
		    {
		        const	kNow	= new Date();
		        const	nMinute	= Process.LOG_RANGE.find(([sId]) => sId === sRange)?.[2] ?? 60;
		        const	kStart	= sRange === "today" ? new Date(kNow.getFullYear(), kNow.getMonth(), kNow.getDate()) : new Date(kNow.getTime() - nMinute * 60000);

		        Process.SetLogPick("Start", kStart);
		        Process.SetLogPick("End", kNow);
		    }

		    static SetLogPick(sSide, kDate)	// 로그 S·E 칸 (날짜·시·분)
		    {
		        document.getElementById(`ProcessLog${sSide}Date`).value		= `${kDate.getFullYear()}-${Process.Pad(kDate.getMonth() + 1)}-${Process.Pad(kDate.getDate())}`;
		        document.getElementById(`ProcessLog${sSide}Hour`).value		= Process.Pad(kDate.getHours());
		        document.getElementById(`ProcessLog${sSide}Minute`).value	= Process.Pad(kDate.getMinutes());
		    }

		    static GetLogPick(sSide)	// 로그 S·E 값 (yyyy-MM-ddTHH:mm, 날짜 없음: "")
		    {
		        const sDate = document.getElementById(`ProcessLog${sSide}Date`)?.value ?? "";

		        return sDate === "" ? "" : `${sDate}T${document.getElementById(`ProcessLog${sSide}Hour`).value}:${document.getElementById(`ProcessLog${sSide}Minute`).value}`;
		    }

		    static OnLogRange(kItem)	// 기간 칩: S·E 맞춤 → 최신부터
		    {
		        Process.kLogState.sRange = kItem.dataset.id;

		        for (const kChip of Process.kDialog.querySelectorAll("[data-act=logRange]"))
		        {
		            kChip.classList.toggle("cur", kChip === kItem);
		        }

		        Process.SetLogRange(kItem.dataset.id);
		        Process.LoadLog("");
		    }

		    static OnLogCustom()	// 조회 (직접 지정): 기간 칩 해제 → 최신부터
		    {
		        Process.kLogState.sRange = "custom";

		        for (const kChip of Process.kDialog.querySelectorAll("[data-act=logRange]"))
		        {
		            kChip.classList.remove("cur");
		        }

		        Process.LoadLog("");
		    }

		    static StartProgress(kProgress, sText)	// 진행 표시 (상자 가운데 팝업 상태 · N초, X초 이상 걸릴 때만), 반환: 끝 함수
		    {
		        const	nBegin	= performance.now();

		        const funcShow = () =>	// 진행 초 갱신
		        {
		            kProgress.textContent = `${sText} · ${Math.floor((performance.now() - nBegin) / 1000)}초`;
		        };

		        funcShow();

		        const	nTimer	= setInterval(funcShow, 1000);
		        const	nPopup	= setTimeout(() => { funcShow(); kProgress.hidden = false; }, PROGRESS_DELAY_MS);

		        return () =>
		        {
		            clearInterval(nTimer);
		            clearTimeout(nPopup);

		            kProgress.hidden = true;
		        };
		    }

		    static async LoadLog(sDirection)	// 로그 조회 ("": 최신부터, next: 더 과거, prev: 더 최신, 없음: 토스트·안내)
		    {
		        const	sStart	= Process.GetLogPick("Start");
		        const	sEnd	= Process.GetLogPick("End");

		        if (sStart === "" || sEnd === "")
		        {
		            Process.Toast("시작과 끝 날짜를 선택하세요.", "warn");

		            return;
		        }

		        let	sUrl	= "api/process?action=log&start=" + encodeURIComponent(sStart) + "&end=" + encodeURIComponent(sEnd);

		        if (sDirection === "next" && Process.sLogOld !== "")
		        {
		            sUrl += "&dir=next&cursor=" + encodeURIComponent(Process.sLogOld);
		        }
		        else if (sDirection === "prev" && Process.sLogNew !== "")
		        {
		            sUrl += "&dir=prev&cursor=" + encodeURIComponent(Process.sLogNew);
		        }

		        const	funcEnd	= Process.StartProgress(document.getElementById("ProcessLogProgress"), "로그 조회 중");

		        try
		        {
		            const	kResponse	= await fetch(sUrl);

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const	sText	= await kResponse.text();

		            if (sText[0] !== "#")	// 메타 줄 없음: 결과 없음 (범위 오류 글)
		            {
		                if (sDirection === "next" || sDirection === "prev")
		                {
		                    Process.Toast(sDirection === "next" ? "더 과거 로그가 없습니다." : "더 최신 로그가 없습니다.", "warn");

		                    return;
		                }

		                Process.arrLog	= [];
		                Process.sLogNew	= "";
		                Process.sLogOld	= "";

		                Process.RenderLog(sText.trim() || "해당 구간 로그가 없습니다. 기간을 늘려보세요.");

		                return;
		            }

		            const	nLine		= sText.indexOf("\n");
		            const	arrCursor	= sText.substring(1, nLine).split("|");	// "#최신 커서|최과거 커서"

		            Process.sLogNew	= arrCursor[0] || "";
		            Process.sLogOld	= arrCursor[1] || "";
		            Process.arrLog	= sText.substring(nLine + 1).split("\n").filter(Boolean).map(sItem => Process.ParseLogLine(sItem));

		            Process.RenderLog();

		            const	kScroll	= document.getElementById("ProcessLogScroll");

		            if (kScroll != null)
		            {
		                kScroll.scrollTop = 0;
		            }
		        }
		        catch (kError)
		        {
		            Process.RenderLog(`로그 조회 실패: ${ErrorText(kError)}`);
		        }
		        finally
		        {
		            funcEnd();
		        }
		    }

		    static ParseLogLine(sLine)	// 로그 줄 → { sTime, sLevel, sType, sText } (예: [2026/10/02 11:39:38.707] [HMX_][INFO_] [v.28] 개별: 111_DEV,3_MPAgent,Minor,On → (계정@IP), 레벨 앞 sTitle 태그: 건너뜀, 예전: [INFO_] [HMX_][v.28])
		    {
		        const	kMatch	= sLine.match(/^\[([^\]]+)\] \S*?\[(\w+)\] (?:\[HMX_\])?(?:\[v\.(\d+)\])? ?(.*)$/);

		        if (kMatch == null)
		        {
		            return { sTime: "", sLevel: "", sType: "system", sText: sLine };
		        }

		        const	sText	= kMatch[4];
		        const	sLevel	= kMatch[2].replace(/_$/, "");
		        const	sType	= sLevel === "ERROR" || sLevel === "WARN" ? "error" : (/^(개별|그룹|전체|선택):/.test(sText) ? "action" : (/^(등록|수정|이름|삭제):/.test(sText) ? "edit" : (/^로그인/.test(sText) ? "login" : "system")));

		        return { sTime: kMatch[1], sLevel, sType, sText };
		    }

		    static RenderLog(sEmpty)	// 로그 표 (종류 칩 + 개수, 검색: 지금 페이지 안), 아래 이 페이지 N줄 중 M줄
		    {
		        const	kTypes	= document.getElementById("ProcessLogTypes");

		        if (kTypes == null)	// 팝업 닫힘
		        {
		            return;
		        }

		        const	sKey	= Process.kLogState.sKey.toLowerCase();
		        const	arrShow	= Process.arrLog.filter(kLine => (Process.kLogState.sType === "all" || kLine.sType === Process.kLogState.sType) && (sKey === "" || (kLine.sText + kLine.sTime).toLowerCase().includes(sKey)));

		        kTypes.innerHTML = Object.entries(Process.LOG_TYPE).map(([sId, sName]) => `<button type="button" class="chip${Process.kLogState.sType === sId ? " cur" : ""}" data-act="logType" data-id="${sId}">${sName} <span class="n">${sId === "all" ? Process.arrLog.length : Process.arrLog.filter(kLine => kLine.sType === sId).length}</span></button>`).join("");

		        document.getElementById("ProcessLogBody").innerHTML = arrShow.map(kLine => `<tr><td class="t">${Process.Escape(kLine.sTime)}</td><td><span class="ltype ${kLine.sLevel === "ERROR" || kLine.sLevel === "WARN" ? "error" : "login"}">${Process.Escape(kLine.sLevel)}</span></td><td><span class="ltype ${kLine.sType}">${Process.LOG_TYPE[kLine.sType]}</span></td><td class="msg">${Process.Escape(kLine.sText)}</td></tr>`).join("")
		            || `<tr><td colspan="4" class="empty">${Process.Escape(sEmpty || "조건에 맞는 로그가 없습니다.")}</td></tr>`;

		        document.getElementById("ProcessLogCount").textContent = `이 페이지 ${Process.arrLog.length}줄 중 ${arrShow.length}줄 표시`;
		    }

		    static OpenErr(sKey)	// 에러 팝업 (큰 팝업, 상세 에러 로그 보기: 검색어 채움): Redis 에러 리스트 최신 순 X건 단위, 같은 레벨·종류·대상·머신 묶음
		    {
		        if (sKey !== undefined)
		        {
		            Process.kErrState = { sMachine: "", sLevel: "", sKey: sKey || "" };
		        }

		        Process.nErrOffset = 0;

		        const kDialog = Process.OpenModal(`<div class="resulthead"><div class="title">에러</div><span class="hint">Redis 에러 리스트 · 같은 서버·같은 종류는 묶어서 표시 · ${Process.PAGE_SIZE}건 단위</span></div>`
		            + `<div class="filter"><select id="ProcessErrMachine" title="머신"><option value="">모든 머신</option></select><select id="ProcessErrLevel" title="레벨"><option value="">모든 레벨</option><option>ERROR</option><option>FATAL</option></select><input type="text" id="ProcessErrKey" title="에러 검색" value="${Process.Escape(Process.kErrState.sKey)}" autocomplete="off" spellcheck="false" autocapitalize="off"></div>`
		            + `<div class="scrollwrap"><div class="scroll" id="ProcessErrScroll"><div id="ProcessErrBody"></div></div><div class="progress" id="ProcessErrProgress" hidden></div></div>`
		            + `<div class="toolright"><span class="hint" id="ProcessErrCount"></span><button type="button" data-act="errPage" data-id="prev">▲ 더 최신</button><button type="button" data-act="errPage" data-id="next">더 과거 ▼</button><button type="button" data-act="modalCancel" autofocus>닫기</button></div>`, "xl");
		        const	kLevel	= kDialog.querySelector("#ProcessErrLevel");

		        kLevel.value = Process.kErrState.sLevel;

		        kDialog.querySelector("#ProcessErrMachine").addEventListener("change", (kEvent) => { Process.kErrState.sMachine = kEvent.target.value; Process.RenderErr(); });
		        kLevel.addEventListener("change", () => { Process.kErrState.sLevel = kLevel.value; Process.RenderErr(); });
		        kDialog.querySelector("#ProcessErrKey").addEventListener("input", (kEvent) => { Process.kErrState.sKey = kEvent.target.value; Process.RenderErr(); });

		        Process.LoadErr("");
		    }

		    static async LoadErr(sDirection)	// 에러 조회 ("": 최신 X건, next: 더 과거, prev: 더 최신)
		    {
		        if (sDirection === "prev" && Process.nErrOffset === 0)
		        {
		            Process.Toast("더 최신 에러가 없습니다.", "warn");

		            return;
		        }

		        const	nOffset	= sDirection === "next" ? Process.nErrOffset + Process.PAGE_SIZE : (sDirection === "prev" ? Math.max(0, Process.nErrOffset - Process.PAGE_SIZE) : 0);
		        const	funcEnd	= Process.StartProgress(document.getElementById("ProcessErrProgress"), "에러 조회 중");

		        try
		        {
		            const	kResponse	= await fetch("api/process?action=errlog&offset=" + nOffset);

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const	sText	= await kResponse.text();

		            if (sText.startsWith("{"))	// Redis 오류 등: JSON 오류 응답
		            {
		                throw new Error(JSON.parse(sText).error || "Unknown Error");
		            }

		            if (sText.trim() === "")
		            {
		                if (sDirection === "next")
		                {
		                    Process.Toast("더 과거 에러가 없습니다.", "warn");

		                    return;
		                }

		                Process.arrErr		= [];
		                Process.nErrOffset	= 0;

		                Process.RenderErr("에러 로그가 없습니다 ✓");

		                return;
		            }

		            Process.nErrOffset	= nOffset;
		            Process.arrErr		= sText.split("\n").filter(Boolean).map(sItem => Process.ParseErrLine(sItem));

		            document.getElementById("ProcessErrMachine")?.replaceChildren(new Option("모든 머신", ""), ...[...new Set(Process.arrErr.map(kErr => kErr.sMachine))].sort().map(sMachine => new Option(sMachine, sMachine, false, sMachine === Process.kErrState.sMachine)));

		            Process.RenderErr();

		            const	kScroll	= document.getElementById("ProcessErrScroll");

		            if (kScroll != null)
		            {
		                kScroll.scrollTop = 0;
		            }
		        }
		        catch (kError)
		        {
		            Process.RenderErr(`에러 조회 실패: ${ErrorText(kError)}`);
		        }
		        finally
		        {
		            funcEnd();
		        }
		    }

		    static ParseErrLine(sLine)	// 에러 줄 → { sMachine, sTime, sLevel, sKind, sTarget, sLine } (예: [머신] [2026/10/02 10:29:30.873] [HMX_][ERROR] Execute Error Crash: 999_,2_Game,Holdem_Lobby(경로)(명령)(0), 레벨 앞 클라 sTitle 태그: 건너뜀, 예전: [ERROR] [HMX_])
		    {
		        const	kMatch	= sLine.match(/^\[([^\]]+)\] \[([^\]]+)\] \S*?\[(\w+)\] (?:\[HMX_\] )?(.*)$/);

		        if (kMatch == null)
		        {
		            return { sMachine: "-", sTime: "", sLevel: "ERROR", sKind: sLine.slice(0, 60), sTarget: "", sLine };
		        }

		        const	sText	= kMatch[4];
		        const	nColon	= sText.indexOf(":");
		        const	sKind	= nColon > 0 ? sText.slice(0, nColon) : sText;
		        const	sRest	= nColon > 0 ? sText.slice(nColon + 1).trim() : "";

		        return { sMachine: kMatch[1], sTime: kMatch[2], sLevel: kMatch[3].replace(/_$/, ""), sKind, sTarget: sKind.includes("Crash") ? sRest.split("(")[0] : sRest.replace(/\s*\(-?\d+\)$/, ""), sLine };
		    }

		    static RenderErr(sEmpty)	// 에러 묶음 (레벨·종류·대상·× 건수·머신·첫 ~ 마지막 시간, 누름: 원문 펼침, 서버 보기·목록에 없음)
		    {
		        const	kBody	= document.getElementById("ProcessErrBody");

		        if (kBody == null)	// 팝업 닫힘
		        {
		            return;
		        }

		        const	sKey		= Process.kErrState.sKey.toLowerCase();
		        const	arrShow		= Process.arrErr.filter(kErr => (Process.kErrState.sMachine === "" || kErr.sMachine === Process.kErrState.sMachine) && (Process.kErrState.sLevel === "" || kErr.sLevel === Process.kErrState.sLevel) && (sKey === "" || kErr.sLine.toLowerCase().includes(sKey)));
		        const	mapGroup	= new Map();	// 묶음 키 → { 첫 에러, arrItem }

		        for (const kErr of arrShow)
		        {
		            const sId = `${kErr.sLevel}|${kErr.sKind}|${kErr.sTarget}|${kErr.sMachine}`;

		            if (!mapGroup.has(sId))
		            {
		                mapGroup.set(sId, { ...kErr, arrItem: [] });
		            }

		            mapGroup.get(sId).arrItem.push(kErr);
		        }

		        kBody.innerHTML = [...mapGroup.values()].map(kGroup =>
		        {
		            const	kRow	= Process.FindRow(kGroup.sTarget) ?? Process.arrRow.find(kItem => Process.GetTarget(kItem.sField) === Process.GetTarget(kGroup.sTarget));
		            const	sLast	= kGroup.arrItem[0].sTime;
		            const	sFirst	= kGroup.arrItem[kGroup.arrItem.length - 1].sTime;
		            const sGoto		= kRow != null ? `<button type="button" data-act="errGoto" data-id="${Process.Escape(kRow.sField)}">서버 보기</button>` : (kGroup.sTarget.includes(",") ? `<span class="tag off" title="없는 서버">목록에 없음</span>` : "");

		            return `<div class="eg"><div class="eg-head" data-act="errToggle"><span class="lv ${Process.Escape(kGroup.sLevel)}">${Process.Escape(kGroup.sLevel)}</span><div class="eg-title"><b>${Process.Escape(kGroup.sKind)}</b><span class="mono">${Process.Escape(kGroup.sTarget)}</span></div>`
		                + `<div class="eg-meta"><div><span class="eg-count">×${kGroup.arrItem.length}</span> · ${Process.Escape(kGroup.sMachine)}</div><div class="mono">${kGroup.arrItem.length > 1 ? Process.Escape(sFirst) + " ~ " : ""}${Process.Escape(sLast)}</div></div>${sGoto}</div>`
		                + `<div class="eg-raw">${kGroup.arrItem.map(kErr => Process.Escape(kErr.sLine)).join("\n")}</div></div>`;
		        }).join("") || `<div class="empty">${Process.Escape(sEmpty || "조건에 맞는 에러가 없습니다.")}</div>`;

		        document.getElementById("ProcessErrCount").textContent = Process.arrErr.length > 0 ? `${Process.nErrOffset + 1}~${Process.nErrOffset + Process.arrErr.length}번째 · ${mapGroup.size}개 묶음 / ${arrShow.length}건` : "";
		    }

		    static GotoServer(sField)	// 에러 서버 보기: 팝업 닫고 필터 초기화·중분류 펼침·상세 패널·줄 깜빡임
		    {
		        const	kRow	= Process.FindRow(sField);

		        Process.CloseModal();

		        if (kRow == null)
		        {
		            return;
		        }

		        Process.setExpand.add("g:" + Process.GetGroup(kRow.sField));
		        Process.SetSecPage(Process.GetModel().find(kSec => kSec.arrRow.includes(kRow)));	// 그 서버 페이지

		        Process.kFilter			= { sKey: "", sState: "all", sMachine: "" };
		        Process.kSearch.value	= "";
		        Process.kMachine.value	= "";

		        Process.SaveExpand();
		        Process.Render();
		        Process.OpenDrawer(kRow.sField);
		        Process.FlashRow(kRow.sField);
		    }

		    static OpenMenu(kButton, sScope, sId)	// 줄 메뉴 (⋯): 서버 → 설정 편집·복제하여 등록·상세 보기·로그에서 찾기 | 삭제, 대분류·중분류 → 이름 변경·로그에서 찾기 (누른 버튼 오른쪽 끝 맞춤, 아래 공간 부족: 위로)
		    {
		        const	arrItem	= sScope === "srv" ? [["edit", "설정 편집"], ["clone", "복제하여 등록"], ["detail", "상세 보기"], ["logOf", "로그에서 찾기"], null, ["del", "삭제", "danger"]] : [["rename", sScope === "sec" ? "대분류 이름 변경" : "중분류 이름 변경"], ["logOf", "로그에서 찾기"]];

		        Process.kMenu.innerHTML	= arrItem.map(arrMenu => arrMenu == null ? "<hr>" : `<button type="button" class="${arrMenu[2] ?? ""}" data-act="menuItem" data-cmd="${arrMenu[0]}" data-scope="${sScope}" data-id="${Process.Escape(sId)}">${arrMenu[1]}</button>`).join("");
		        Process.kMenu.hidden	= false;

		        const	kRect	= kButton.getBoundingClientRect();
		        const	nWidth	= Process.kMenu.offsetWidth;
		        const	nHeight	= Process.kMenu.offsetHeight;

		        Process.kMenu.style.left	= Math.max(8, Math.min(innerWidth - nWidth - 8, kRect.right - nWidth)) + "px";
		        Process.kMenu.style.top		= (kRect.bottom + nHeight + 8 > innerHeight ? kRect.top - nHeight - 4 : kRect.bottom + 4) + "px";
		    }

		    static CloseMenu()	// 줄 메뉴 닫기
		    {
		        Process.kMenu.hidden = true;
		    }

		    static OnMenuItem(kItem)	// 줄 메뉴 항목
		    {
		        const	sCmd	= kItem.dataset.cmd;
		        const	sScope	= kItem.dataset.scope;
		        const	sId		= kItem.dataset.id;

		        if (sCmd === "edit" || sCmd === "clone")
		        {
		            Process.OpenForm(sCmd, Process.FindRow(sId));
		        }
		        else if (sCmd === "detail")
		        {
		            Process.OpenDrawer(sId);
		        }
		        else if (sCmd === "del")
		        {
		            Process.OpenDelete(sId);
		        }
		        else if (sCmd === "rename")
		        {
		            Process.OpenRename(sScope, sId);
		        }
		        else if (sCmd === "logOf")
		        {
		            Process.OpenLog(sScope === "sec" ? (Process.FindSec(sId)?.sLabel ?? "") : Process.GetTarget(sId));
		        }
		    }

		    static Toast(sText, sTone = "info")	// 토스트 (오른쪽 위 쌓임, 왼쪽 색 줄: ok 초록·fail 빨강·warn 주황·info 파랑, X초 뒤 사라짐, 실패: 더 오래, 팝업 위: top layer)
		    {
		        const	kToast	= document.createElement("div");

		        kToast.className	= "toast " + sTone;
		        kToast.textContent	= sText;

		        Process.kToasts.append(kToast);

		        try	// 열린 팝업 위 (top layer 맨 위로 다시)
		        {
		            if (Process.kToasts.matches(":popover-open"))
		            {
		                Process.kToasts.hidePopover();
		            }

		            Process.kToasts.showPopover();
		        }
		        catch (kError)	// popover 미지원: 그대로
		        {
		        }

		        setTimeout(() =>
		        {
		            kToast.style.transition	= "opacity .3s";
		            kToast.style.opacity	= "0";

		            setTimeout(() =>
		            {
		                kToast.remove();

		                if (Process.kToasts.children.length === 0 && Process.kToasts.matches(":popover-open"))
		                {
		                    Process.kToasts.hidePopover();
		                }
		            }, 300);
		        }, sTone === "fail" ? Process.TOAST_FAIL_MS : Process.TOAST_MS);
		    }

		    static FlashRow(sField)	// 찾아간 서버 줄: 깜빡임 + 가운데 스크롤
		    {
		        const	kRow	= [...Process.kTables.querySelectorAll("tr.srv-row")].find(kItem => kItem.dataset.key === sField);

		        if (kRow == null)
		        {
		            return;
		        }

		        kRow.classList.add("flash");
		        kRow.scrollIntoView({ block: "center", behavior: "smooth" });

		        setTimeout(() => kRow.classList.remove("flash"), Process.FLASH_MS);
		    }

		    static Copy(sText)	// 복사 버튼: 클립보드 + 토스트
		    {
		        if (navigator.clipboard == null)	// http 외부 주소 등: 클립보드 막힘
		        {
		            Process.Toast("복사 실패", "fail");

		            return;
		        }

		        navigator.clipboard.writeText(sText).then(() => Process.Toast("복사했습니다", "ok"), () => Process.Toast("복사 실패", "fail"));
		    }

		    static SetPage(sPage)	// 지금 페이지 바꿈 (보관 안 함: 최초 입장·F5 리셋 → 0번)
		    {
		        Process.sPage = sPage;
		    }

		    static SetSecPage(kSec)	// 대분류가 보이는 페이지로 (없음: 그대로)
		    {
		        if (kSec != null)
		        {
		            Process.SetPage(Process.GetPage(kSec));
		        }
		    }

		    static JumpSec(sId)	// 요약 카드 누름: 그 대분류 페이지 (필터 중: 모든 페이지 그대로) → 대분류 줄 가운데
		    {
		        if (!Process.IsFiltering())
		        {
		            Process.SetSecPage(Process.FindSec(sId));
		            Process.Render();
		        }

		        document.getElementById("ProcessSec-" + sId)?.scrollIntoView({ behavior: "smooth", block: "center" });
		    }

		    static SaveExpand()	// 펼침 보관 (localStorage: 새로 고침 유지)
		    {
		        try
		        {
		            localStorage.setItem(Process.EXPAND_KEY, JSON.stringify([...Process.setExpand]));
		        }
		        catch (kError)	// 저장 불가: 이 창만
		        {
		        }
		    }

		    static LoadExpand()	// 보관 펼침 읽기 (false: 보관 없음)
		    {
		        try
		        {
		            const	arrExpand	= JSON.parse(localStorage.getItem(Process.EXPAND_KEY) ?? "null");

		            if (Array.isArray(arrExpand))
		            {
		                Process.setExpand = new Set(arrExpand);

		                return true;
		            }
		        }
		        catch (kError)	// 형식 오류: 처음처럼
		        {
		        }

		        return false;
		    }

		    static DefaultExpand()	// 처음 (보관 없음): 문제 그룹 (실패·진행 중·커밋 불일치) 만 펼침
		    {
		        for (const kSec of Process.GetModel())
		        {
		            for (const kGroup of kSec.arrGroup)
		            {
		                const	kCount	= Process.GetCount(kGroup.arrRow);

		                if (kCount.failed || kCount.transit || Process.GetDrift(kGroup.arrRow).length > 0)
		                {
		                    Process.setExpand.add("g:" + kGroup.sId);
		                }
		            }
		        }

		        Process.SaveExpand();
		    }

		    static SetSummary(bOpen)	// 현황·필터 펼침·접기 (현황 버튼 선택: 존 색, localStorage 보관)
		    {
		        Process.kSummary.hidden = !bOpen;

		        Process.kSummaryButton.classList.toggle("cur", bOpen);

		        try
		        {
		            localStorage.setItem(Process.SUMMARY_KEY, bOpen ? "1" : "0");
		        }
		        catch (kError)	// 저장 불가: 이 창만
		        {
		        }
		    }

		    static ToggleGroup(sId)	// 중분류 줄: 펼침 ↔ 접기
		    {
		        if (!Process.setExpand.delete(sId))
		        {
		            Process.setExpand.add(sId);
		        }

		        Process.SaveExpand();
		        Process.RenderTable();
		    }

		    static ToggleSec(sId)	// 대분류 줄: 하위 중분류 모두 펼침 ↔ 접기
		    {
		        const	arrId	= (Process.FindSec(sId)?.arrGroup ?? []).map(kGroup => "g:" + kGroup.sId);
		        const	bAll	= arrId.every(sGroup => Process.setExpand.has(sGroup));

		        for (const sGroup of arrId)
		        {
		            if (bAll)
		            {
		                Process.setExpand.delete(sGroup);
		            }
		            else
		            {
		                Process.setExpand.add(sGroup);
		            }
		        }

		        Process.SaveExpand();
		        Process.RenderTable();
		    }

		    static ExpandAll(bOpen)	// 펴기·접기: 모든 중분류
		    {
		        for (const kSec of Process.GetModel())
		        {
		            for (const kGroup of kSec.arrGroup)
		            {
		                if (bOpen)
		                {
		                    Process.setExpand.add("g:" + kGroup.sId);
		                }
		                else
		                {
		                    Process.setExpand.delete("g:" + kGroup.sId);
		                }
		            }
		        }

		        Process.SaveExpand();
		        Process.RenderTable();
		    }

		    static ClearFilter()	// 필터 초기화 (검색어·상태 칩·머신)
		    {
		        Process.kFilter			= { sKey: "", sState: "all", sMachine: "" };
		        Process.kSearch.value	= "";
		        Process.kMachine.value	= "";

		        Process.Render();
		    }

		    static SelectRow(kItem)	// 서버 줄 누름: 선택 (연한 파랑)
		    {
		        Process.sSelect = kItem.dataset.id;

		        for (const kRow of Process.kTables.querySelectorAll("tr.srv-row.sel"))
		        {
		            kRow.classList.remove("sel");
		        }

		        kItem.classList.add("sel");
		    }

		    static ACTION	=	// data-act → 동작 (누른 요소, 이벤트)
		    {
		        toggle:			(kItem) => Process.ToggleGroup(kItem.dataset.id),
		        secToggle:		(kItem) => Process.ToggleSec(kItem.dataset.id),
		        select:			(kItem) => Process.SelectRow(kItem),
		        closeDrawer:	() => Process.CloseDrawer(),
		        op:				(kItem) => !kItem.disabled && Process.RunOp(kItem.dataset.op, kItem.dataset.scope, kItem.dataset.id),
		        menu:			(kItem) => Process.OpenMenu(kItem, kItem.dataset.scope, kItem.dataset.id),
		        menuItem:		(kItem) => Process.OnMenuItem(kItem),
		        edit:			(kItem) => Process.OpenForm("edit", Process.FindRow(kItem.dataset.id)),
		        clone:			(kItem) => Process.OpenForm("clone", Process.FindRow(kItem.dataset.id)),
		        del:			(kItem) => Process.OpenDelete(kItem.dataset.id),
		        renameOk:		() => Process.RunRename(),
		        saveForm:		(kItem) => Process.SaveForm(kItem.dataset.id),
		        modalCancel:	() => Process.CloseModal(false),
		        modalOk:		() => Process.CloseModal(true),
		        openLog:		(kItem) => Process.OpenLog(kItem.dataset.id),
		        logRange:		(kItem) => Process.OnLogRange(kItem),
		        logCustom:		() => Process.OnLogCustom(),
		        logType:		(kItem) => { Process.kLogState.sType = kItem.dataset.id; Process.RenderLog(); },
		        logPage:		(kItem) => Process.LoadLog(kItem.dataset.id),
		        openErr:		(kItem, kEvent) => { kEvent.preventDefault(); Process.OpenErr(kItem.dataset.id); },
		        errPage:		(kItem) => Process.LoadErr(kItem.dataset.id),
		        errToggle:		(kItem) => kItem.closest(".eg").classList.toggle("open"),
		        errGoto:		(kItem) => Process.GotoServer(kItem.dataset.id),
		        state:			(kItem) => { Process.kFilter.sState = kItem.dataset.id; Process.Render(); },
		        clearFilter:	() => Process.ClearFilter(),
		        jump:			(kItem) => Process.JumpSec(kItem.dataset.id),
		        page:			(kItem) => { Process.SetPage(kItem.dataset.id); Process.Render(); },
		        copy:			(kItem) => Process.Copy(kItem.dataset.id),
		        legend:			() => ShowHelp(),	// 힌트 팝업 (맨 위부터, 범례: 실행 묶음)
		    };

		    static OnClick(kEvent)	// 반영 창 누름 (서랍·메뉴·팝업 포함): data-act 동작, 메뉴 밖: 메뉴 닫음
		    {
		        const	kItem	= kEvent.target.closest("[data-act]");

		        if (kItem == null)
		        {
		            if (kEvent.target.closest("#ProcessMenu") == null)
		            {
		                Process.CloseMenu();
		            }

		            return;
		        }

		        if (kItem.dataset.act !== "menu")
		        {
		            Process.CloseMenu();
		        }

		        Process.ACTION[kItem.dataset.act]?.(kItem, kEvent);
		    }

		    static OnOutside(kEvent)	// 반영 창 밖 누름: 줄 메뉴 닫음 (메뉴 열림만)
		    {
		        if (!Process.kMenu.hidden && !kProcessPanel.contains(kEvent.target))
		        {
		            Process.CloseMenu();
		        }
		    }

		    static OnKey(kEvent)	// 반영 탭 보일 때 Esc: 줄 메뉴 → 상세 패널 → 입력칸 포커스 해제 (앞에서 하나만, 팝업: 팝업이 닫음)
		    {
		        if (kEvent.key !== "Escape" || kProcessPanel.hidden || document.querySelector("dialog[open]") != null)
		        {
		            return;
		        }

		        if (!Process.kMenu.hidden)
		        {
		            Process.CloseMenu();
		        }
		        else if (Process.sDrawer != null)
		        {
		            Process.CloseDrawer();
		        }
		        else if (kProcessPanel.contains(document.activeElement) && /INPUT|TEXTAREA|SELECT/.test(document.activeElement.tagName))
		        {
		            document.activeElement.blur();
		        }
		    }

		    static Init()	// 이벤트 연결 (페이지 시작 1번)
		    {
		        kProcessPanel.addEventListener("click", Process.OnClick);	// 줄·서랍·메뉴·팝업 동작
		        document.addEventListener("click", Process.OnOutside);		// 창 밖 누름: 메뉴 닫음
		        document.addEventListener("keydown", Process.OnKey);		// Esc: 메뉴 → 상세 패널 (반영 탭 보일 때만)

		        Process.kSearch.addEventListener("input", () => { Process.kFilter.sKey = Process.kSearch.value.trim(); Process.Render(); });
		        Process.kMachine.addEventListener("change", () => { Process.kFilter.sMachine = Process.kMachine.value; Process.Render(); });
		        Process.kSummaryButton.addEventListener("click", () => Process.SetSummary(Process.kSummary.hidden));	// 현황: 펼침 ↔ 접기
		        document.getElementById("ProcessLogButton").addEventListener("click", () => Process.OpenLog());
		        document.getElementById("ProcessErrButton").addEventListener("click", () => Process.OpenErr());
		        document.getElementById("ProcessAddButton").addEventListener("click", async () => { await Process.kStart; Process.OpenForm("add"); });	// 첫 로딩 뒤 (새 창 복원: 머신·자동완성 목록)
		        document.getElementById("ProcessExpandButton").addEventListener("click", () => Process.ExpandAll(true));
		        document.getElementById("ProcessCollapseButton").addEventListener("click", () => Process.ExpandAll(false));
		        document.getElementById("ProcessTestOn").addEventListener("click", async () => { await Process.kStart; Process.TestAll(true); });	// 첫 로딩 뒤 (새 창 복원: 빈 목록 방지)
		        document.getElementById("ProcessTestOff").addEventListener("click", async () => { await Process.kStart; Process.TestAll(false); });
		        document.getElementById("ProcessStaleReload").addEventListener("click", () => Process.Reload(""));	// 배너 새로고침: 바로 다시 읽기

		        Process.kDialog.addEventListener("cancel", (kEvent) =>	// Esc: 닫기 (저장 중 잠김 제외)
		        {
		            kEvent.preventDefault();

		            Process.CloseModal(false);
		        });

		        Process.kDialog.addEventListener("close", () =>	// 닫힘 (Esc 바로 닫음 포함): 잠김 → 다시 열기, 확인 대기 → 취소
		        {
		            if (Process.kDialog.open)
		            {
		                return;
		            }

		            if (Process.kDialog.dataset.locked)
		            {
		                Process.kDialog.showModal();

		                return;
		            }

		            Process.ResolveModal(false);
		        });

		        Process.kDeleteStop.addEventListener("click", () =>	// 삭제 확인 먼저 정지: 닫고 정지 확인
		        {
		            const	sField	= Process.kDeleteStop.dataset.id;

		            kDeleteDialog.close();

		            Process.RunOp("Off", "srv", sField);
		        });

		        kDeleteDialog.addEventListener("close", () =>	// 삭제 확인 닫힘: 삭제 켬·먼저 정지 숨김 (다른 탭 삭제 확인 그대로)
		        {
		            Process.kDeleteYes.disabled	= false;
		            Process.kDeleteStop.hidden	= true;
		        });

		        new MutationObserver(Process.ShowLock).observe(kOffText, { attributes: true, attributeFilter: ["hidden"] });	// HMX 연결 끊김·다시 연결: 작업 버튼 잠금·해제

		        document.addEventListener("visibilitychange", () =>	// 브라우저 탭 돌아옴: 반영 탭 보이면 바로 갱신
		        {
		            if (!document.hidden && !kProcessPanel.hidden && Process.bOpened)
		            {
		                Process.Refresh(true);
		            }
		        });

		        window.addEventListener("beforeunload", (kEvent) =>	// 순차 작업 중 나감: 브라우저 경고 (나가면 뒤 그룹 진행 안 함)
		        {
		            if (Process.setJob.size > 0)
		            {
		                kEvent.preventDefault();
		                kEvent.returnValue = "";
		            }
		        });

		        try	// 현황: 지난 펼침 그대로 (처음: 접힘)
		        {
		            if (localStorage.getItem(Process.SUMMARY_KEY) === "1")
		            {
		                Process.SetSummary(true);
		            }
		        }
		        catch (kError)
		        {
		        }

		        Process.RenderTable();	// 첫 안내: 불러오는 중
		    }
		}
		""";
	}
}

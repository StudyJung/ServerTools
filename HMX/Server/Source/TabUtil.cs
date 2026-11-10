class TabUtil	// 유틸 탭: 1번 창 하위 탭 타임 (시간)·디프 (비교)·소트 (정렬) (서버 API 없음, 페이지 쪽: UtilHTML)
{
	public static string UtilHTML()	// 페이지 유틸 탭 스크립트 (Web.IndexHtml 조립)
	{
		return """
		class Util	// 유틸 탭: 1번 창 하위 탭 (타임·디프·소트, 반영 탭 페이지 탭과 같은 버튼, 내용: 하위 탭마다 상자)
		{
		    static	TIME_MAX	= 100;	// 타임 결과 최대 줄 (넘음: 맨 아래 (오래된) 줄부터 지움)
		    static	TIME_KEY	= "HMX.UtilTime";	// 타임 결과 localStorage 키 (UnixTime 초 배열, 새로 고침 유지)
		    static	DIFF_SPLIT_MIN	= 120;	// 디프 좌·우 창 최소 폭 (px)
		    static	mapDiffMeta		= { Left: null, Right: null };	// 비교 중 편집 칸 줄마다 { sKind: "" 같음·chg 바뀜·gap 빈 줄 (반대쪽에만 있는 줄 자리)·add 고쳐 늘어난 줄, sOrig: 비교한 글 (add: null), sOther: 반대쪽 글 (줄 안 다른 부분 노랑, 없음: null), nBlock: 차이 번호 (같음: -1) } (비교 안 함: null)
		    static	mapDiffLines	= { Left: null, Right: null };	// 비교 중 지난 줄 (고침 앞뒤 견줌: 바뀐 곳만 줄 정보 맞춤)
		    static	mapDiffWhole	= { Left: false, Right: false };	// 이번 고침: 모두 고름 → 지움·붙여넣기 (비교 모두 풂)
		    static	mapDiffHistory	= { Left: [], Right: [] };	// 비교 중 고침 전 [{ sKey: 글 지문, arrMeta }] (Ctrl+Z·Y 로 그 글: 그때 줄 색 그대로, 최근 X개·줄 정보 합 X줄까지, 글 사본 안 둠)
		    static	mapDiffKey		= { Left: "", Right: "" };	// 비교 중 지금 글 지문 (길이:해시)
		    static	mapSelBar		= new Map();	// 고른 빈 줄 표시 (창 → 그린 막대, 다음에 그것만 지움)
		    static	nSelFrame		= 0;	// 고른 빈 줄 다시 그림 예약 (한 화면 틀에 1번)
		    static	nSortFrame		= 0;	// 소트 좌 줄 번호·제목 다시 그림 예약 (한 화면 틀에 1번)
		    static	mapDiffSync		= { Left: false, Right: false };	// 반대쪽 따라 옮긴 스크롤 (되돌려 옮기지 않음)
		    static	kDiffRelease	= null;	// 모두 지움·통째로 바꿈으로 비교 푼 직전 (Ctrl+Z 로 그 글 돌아옴: 비교 다시 켬, 다른 고침·다시 비교: 버림)
		    static	mapDiffFile		= { Left: null, Right: null };	// 디프 좌·우 연 파일 { kHandle (덮어쓰기, 없음: null), sName, bBom, sEol } (파일 아님: null → 저장 꺼짐)
		    static	mapDiffName		= { Left: "", Right: "" };	// 디프 좌·우 내용 이름: 연 파일 이름·붙여넣기·직접 입력: 복사 (비움: 없음)
		    static	DIFF_HISTORY_MAX	= 100;	// 비교 중 고침 기억 (Ctrl+Z·Y 줄 색, 넘음: 가장 오래된 것 버림)
		    static	DIFF_HISTORY_LINE	= 500000;	// 비교 중 고침 기억 줄 정보 합 최대 (큰 글: 기억 줄임, 메모리)
		    static	DIFF_BACK_MAX	= 10;	// 디프 네비게이션 기억 (실행한 좌·우 입력, 넘음: 가장 오래된 것 버림)
		    static	arrDiffBack		= [];	// 디프 실행 기억 [{ sLeft, sRight, sLeftName, sRightName }] (브라우저 메모리만)
		    static	nDiffPos		= -1;	// 디프 네비게이션 지금 위치 (없음: -1)
		    static	arrDiffBlock	= [];	// 디프 차이 [[줄 맞춤 보기 줄 번호, …]] (줄 비교 이어진 바뀜 묶음, DataCompare 와 같음)
		    static	nDiffBlock		= -1;	// 지금 차이 (없음: -1)
		    static	bDiffView		= false;	// 비교 중 (편집 칸 그대로 + 빈 줄·색 덧입힘, 끔: 기본 편집 칸)
		    static	sDiffState		= "";	// 디프 결과: 다름·같음 (비교 안 함·고침: 빈 값)
		    static	MYERS_LIMIT		= 2000;	// 줄 비교 최대 편집 수 (넘음: 가운데 통째로 바뀜, DataCompare 와 같음)
		    static	UNIX_DIGIT	= 10;	// Unix 칸 자릿수 (초, 고정: 넘는 자리 입력 안 됨)
		    static	nDragIndex	= -1;	// 2번 창 끄는 줄 (arrTime 번호, 없음: -1)
		    static	sDateLast	= "";	// KST 날짜 마지막 맞는 값 (범위 밖: 되돌림)
		    static	SORT_BACK_MAX	= 10;	// 소트 네비게이션 기억 (실행한 입력, 넘음: 가장 오래된 것 버림)
		    static	arrSortBack	= [];	// 소트 실행 기억 [입력 글] (브라우저 메모리만)
		    static	nSortPos	= -1;	// 소트 네비게이션 지금 위치 (없음: -1)
		    static	arrSortLine	= [];	// 소트 결과 줄 (비주얼 스튜디오 기본 형식)
		    static	mapSortFold	= new Map();	// 접을 수 있는 줄 (여는 줄 → 닫는 줄)
		    static	setSortFold	= new Set();	// 접은 여는 줄
		    static	arrSortHide	= [];	// 줄마다 접은 바깥 수 (0: 보임)
		    static	sSortCount	= "";	// 소트 결과 제목: 줄 수 + 요소별 개수 요약 (빈 입력: 줄 0)
		    static	setSortError	= new Set();	// 소트 결과 JSON 틀린 줄 (연한 빨강)
		    static	kSortFile	= null;	// 소트 좌 창 연 파일 { kHandle (덮어쓰기, 없음: null), sName, bBom, sEol } (없음·다 지움: null)
		    static	sSortName	= "";	// 소트 좌 창 파일 이름 (제목, 없음·다 지움: "")
		    static	DAY_NAME	= ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];	// 타임 결과 요일 (영어 세 글자)
		    static	KST_HOUR	= 9;	// KST = UTC + X시간 (브라우저 시간대 무관)

		    static	kTabs		= document.getElementById("UtilTabs");			// 하위 탭 버튼 묶음
		    static	kNow		= document.getElementById("UtilNow");			// 타임 Time: 현재 시간 (KST, 1초마다, 입력 없음)
		    static	kDate		= document.getElementById("UtilDate");			// 타임 입력 (1번 창 하위 탭 오른쪽 같은 줄) KST 날짜
		    static	kHour		= document.getElementById("UtilHour");			// KST 시 (00~23)
		    static	kMinute		= document.getElementById("UtilMinute");		// KST 분 (00~59)
		    static	kSecond		= document.getElementById("UtilSecond");		// KST 초 (00~59)
		    static	kUnix		= document.getElementById("UtilUnix");			// UnixTime (초·밀리초)
		    static	kTitle		= document.getElementById("UtilTimeTitle");		// 2번 창 제목 (건수)
		    static	kScroll		= document.getElementById("UtilTimeScroll");	// 2번 창 상자
		    static	kResult		= document.getElementById("UtilTimeResult");	// 2번 창 (결과)
		    static	kDiffSplit	= document.getElementById("UtilDiffSplit");		// 디프 좌·우 창 묶음 (가운데 바: 좌 창 폭 --splitWidth)
		    static	kDiffSplitter	= document.getElementById("UtilDiffSplitter");	// 디프 좌·우 사이 바
		    static	kBody		= document.getElementById("UtilTimeBody");		// 2번 창 표 줄
		    static	sCur		= "time";	// 지금 하위 탭 (최초 입장: 타임)
		    static	bKstAuto	= true;		// KST 칸 안 고침: 실행·탭 열 때 현재 시간 (고침: 그 값)
		    static	bUnixAuto	= true;		// UnixTime 칸 안 고침: 실행·탭 열 때 현재 시간 (고침: 그 값)
		    static	arrTime		= [];		// 타임 결과 UnixTime 초 (최신: 맨 앞, 최대 TIME_MAX)

		    static Open()	// 유틸 탭 버튼: 유틸 창 (지금 하위 탭 그대로, 타임: 입력 현재 시간으로)
		    {
		        ShowPanel("util");
		        Util.Select(Util.sCur, true);
		    }

		    static Select(sName, bEnter = false)	// 하위 탭 고름: 그 버튼 존 색 + 그 상자만 보임 (타임 들어옴: 두 칸 현재 시간, bEnter: 유틸 탭 들어옴)
		    {
		        if (sName === "time" && (bEnter || Util.sCur !== "time"))	// 타임 올 때마다: 고친 값 버리고 현재 시간
		        {
		            Util.bKstAuto	= true;
		            Util.bUnixAuto	= true;

		            Util.FillNow();
		        }

		        Util.sCur = sName;

		        Util.ShowNow();

		        for (const kButton of Util.kTabs.querySelectorAll("button[data-util]"))
		        {
		            kButton.classList.toggle("cur", kButton.dataset.util === sName);
		        }

		        for (const kBox of kUtilPanel.querySelectorAll(".utilbox"))
		        {
		            kBox.hidden = kBox.dataset.util !== sName;
		        }

		        Util.FitTimeHeight();
		        Util.FitDiffHeight();
		        Util.FitSortHeight();
		    }

		    static ResetState()	// F5 리셋: 최초 입장 상태 (타임, 두 칸 현재 시간, 결과: 그대로 (브라우저 저장))
		    {
		        Util.bKstAuto	= true;
		        Util.bUnixAuto	= true;

		        Util.kDiffSplit.style.removeProperty("--splitWidth");	// 디프·소트 좌·우 반반
		        document.getElementById("UtilSortSplit").style.removeProperty("--splitWidth");
		        Util.ClearSort();
		        Util.ClearDiff();

		        Util.Select("time", true);
		        Util.RenderTime();
		    }

		    static GetNow()	// 현재 UnixTime (초)
		    {
		        return Math.floor(Date.now() / 1000);
		    }

		    static Pad(nValue)	// 두 자리 (예: 07)
		    {
		        return String(nValue).padStart(2, "0");
		    }

		    static Format(nUnix, nHour)	// UnixTime 초 → yyyy-MM-dd HH:mm:ss (nHour: UTC 와 시차, KST: KST_HOUR, UTC: 0)
		    {
		        const	kTime	= new Date((nUnix + nHour * 3600) * 1000);

		        return `${kTime.getUTCFullYear()}-${Util.Pad(kTime.getUTCMonth() + 1)}-${Util.Pad(kTime.getUTCDate())} ${Util.Pad(kTime.getUTCHours())}:${Util.Pad(kTime.getUTCMinutes())}:${Util.Pad(kTime.getUTCSeconds())}`;
		    }

		    static ShowNow()	// Time: 현재 시간 (KST, 타임 보일 때만 바꿈)
		    {
		        if (!kUtilPanel.hidden && Util.sCur === "time")
		        {
		            Util.kNow.textContent = Util.Format(Util.GetNow(), Util.KST_HOUR);
		        }
		    }

		    static TickNow()	// Time 1초마다 (초 바뀌는 때에 맞춤)
		    {
		        Util.ShowNow();

		        setTimeout(Util.TickNow, 1000 - Date.now() % 1000);
		    }

		    static OnNowFill()	// Time 새로 고침 버튼: 화살표 1바퀴 + KST·Unix 칸 현재 시간 (고친 값 버림)
		    {
		        Operation.TurnButton(document.getElementById("UtilNowFill"));	// 운영 탭 새로 고침과 같음

		        Util.bKstAuto	= true;
		        Util.bUnixAuto	= true;

		        Util.FillNow();
		        Util.ShowNow();
		    }

		    static FillNow()	// 안 고친 칸: 현재 시간 (KST 날짜·시·분·초, UnixTime 초)
		    {
		        const	nNow	= Util.GetNow();

		        if (Util.bKstAuto)
		        {
		            Util.SetKst(nNow);
		        }

		        if (Util.bUnixAuto)
		        {
		            Util.kUnix.value = String(nNow);
		        }
		    }

		    static SetKst(nUnix)	// UnixTime 초 → KST 칸 (날짜·시·분·초)
		    {
		        const	kTime	= new Date((nUnix + Util.KST_HOUR * 3600) * 1000);	// KST 시각 (UTC 값으로 읽음)

		        Util.kDate.value	= `${kTime.getUTCFullYear()}-${Util.Pad(kTime.getUTCMonth() + 1)}-${Util.Pad(kTime.getUTCDate())}`;
		        Util.kHour.value	= Util.Pad(kTime.getUTCHours());
		        Util.kMinute.value	= Util.Pad(kTime.getUTCMinutes());
		        Util.kSecond.value	= Util.Pad(kTime.getUTCSeconds());
		    }

		    static GetKst()	// KST 칸 → UnixTime 초 (날짜 없음·범위 밖 (Unix 0 ~ UNIX_DIGIT 자리 넘음): null)
		    {
		        if (Util.kDate.value === "" || Util.kDate.value < Util.kDate.min || Util.kDate.value > Util.kDate.max)
		        {
		            return null;
		        }

		        const	[nYear, nMonth, nDay]	= Util.kDate.value.split("-").map(Number);
		        const	nUtc	= Date.UTC(nYear, nMonth - 1, nDay, Number(Util.kHour.value), Number(Util.kMinute.value), Number(Util.kSecond.value));	// 입력을 UTC 로 본 밀리초

		        const	nUnix	= nUtc / 1000 - Util.KST_HOUR * 3600;

		        return nUnix < 0 || String(nUnix).length > Util.UNIX_DIGIT ? null : nUnix;	// 범위 밖: 없음
		    }

		    static GetUnix()	// Unix 칸 → UnixTime 초 (숫자 UNIX_DIGIT 자리 이하, 형식 아님: null)
		    {
		        const	sUnix	= Util.kUnix.value.trim();

		        if (!new RegExp(`^\\d{1,${Util.UNIX_DIGIT}}$`).test(sUnix))
		        {
		            return null;
		        }

		        return Number(sUnix);
		    }

		    static SyncUnix()	// KST 입력 끝 (날짜·시·분·초 바뀜·Enter): 그 시간 → Unix 칸 (날짜 없음: 그대로)
		    {
		        const	nUnix	= Util.GetKst();

		        if (nUnix == null)
		        {
		            return;
		        }

		        Util.kUnix.value	= String(nUnix);
		        Util.bUnixAuto		= false;
		    }

		    static SyncKst()	// Unix 입력 끝 (포커스 나감·Enter): 그 시간 → KST 칸 (형식 아님: 그대로)
		    {
		        const	nUnix	= Util.GetUnix();

		        if (nUnix == null)
		        {
		            return;
		        }

		        Util.SetKst(nUnix);

		        Util.bKstAuto = false;
		    }

		    static RunKst()	// KST 실행: 1번 창 KST 시간 → UnixTime → 2번 창 맨 위 (안 고친 칸: 누른 때 현재 시간, 고침: Unix 칸 같이)
		    {
		        Util.FillNow();

		        const	nUnix	= Util.GetKst();

		        if (nUnix == null)	// 날짜 없음: 안 넣음 (표시 없음)
		        {
		            return;
		        }

		        if (!Util.bKstAuto)
		        {
		            Util.SyncUnix();
		        }

		        Util.AddTime(nUnix);
		    }

		    static RunUnix()	// UnixTime 실행: 초 (UNIX_DIGIT 자리 이하) → 2번 창 맨 위 (안 고친 칸: 누른 때 현재 시간, 고침: KST 칸 같이)
		    {
		        Util.FillNow();

		        const	nUnix	= Util.GetUnix();

		        if (nUnix == null)	// 형식 아님: 안 넣음 (표시 없음)
		        {
		            return;
		        }

		        if (!Util.bUnixAuto)
		        {
		            Util.SyncKst();
		        }

		        Util.AddTime(nUnix);
		    }

		    static AddTime(nUnix)	// 결과 맨 위에 쌓음 (같은 시간도 그대로 쌓음, TIME_MAX 넘음: 맨 아래부터 지움) → 표 다시
		    {
		        Util.arrTime.unshift(nUnix);

		        if (Util.arrTime.length > Util.TIME_MAX)
		        {
		            Util.arrTime.length = Util.TIME_MAX;
		        }

		        Util.SaveTime();
		        Util.RenderTime();

		        Util.kScroll.scrollTop = 0;	// 새 줄 (맨 위) 보이게
		    }

		    static SaveTime()	// 타임 결과 브라우저 저장 (localStorage, 저장 불가: 이 창만)
		    {
		        try
		        {
		            localStorage.setItem(Util.TIME_KEY, JSON.stringify(Util.arrTime));
		        }
		        catch (kError)
		        {
		        }
		    }

		    static LoadTime()	// 저장한 타임 결과 읽기 (정수만, 최대 TIME_MAX, 없음·깨짐: 빈 결과)
		    {
		        try
		        {
		            const	arrSaved	= JSON.parse(localStorage.getItem(Util.TIME_KEY) ?? "[]");

		            Util.arrTime = Array.isArray(arrSaved) ? arrSaved.filter(Number.isInteger).slice(0, Util.TIME_MAX) : [];
		        }
		        catch (kError)
		        {
		            Util.arrTime = [];
		        }
		    }

		    static RenderTime()	// 2번 창: 제목 (건수) + 표 No.·KST·Unix·ISO 8601 (KST 시간대, 예: 2026-11-10T06:30:20+09:00)·UTC·Day (KST 요일, 예: Mon)·Ac. (No. 1: 맨 위, 새 결과: 맨 위, 줄 끌기: 순서 바꿈, Ac.: 삭제 아이콘, 없음: 빈 표)
		    {
		        const	kFragment	= document.createDocumentFragment();

		        Util.arrTime.forEach((nUnix, nIndex) =>
		        {
		            const	kRow	= document.createElement("tr");
		            const	kDelete	= document.createElement("button");	// 삭제 (그 줄만, 덤프 Ac. 와 같은 아이콘)

		            kRow.dataset.index		= nIndex;
		            kRow.draggable			= true;	// 끌기: 순서 바꿈
		            kDelete.type			= "button";
		            kDelete.className		= "qdel";
		            kDelete.dataset.action	= "delete";
		            kDelete.ariaLabel		= "삭제";
		            kDelete.title			= "삭제";

		            AddCell(kRow, String(nIndex + 1), "num");
		            AddCell(kRow, Util.Format(nUnix, Util.KST_HOUR), "nowrap");
		            AddCell(kRow, String(nUnix), "nowrap");
		            AddCell(kRow, Util.Format(nUnix, Util.KST_HOUR).replace(" ", "T") + "+09:00", "nowrap");	// ISO 8601 (KST 시간대)
		            AddCell(kRow, Util.Format(nUnix, 0), "nowrap");
		            AddCell(kRow, Util.DAY_NAME[new Date((nUnix + Util.KST_HOUR * 3600) * 1000).getUTCDay()], "nowrap");	// 요일 (KST)
		            AddCell(kRow, "", "fileact").append(kDelete);	// Ac.: 맨 오른쪽 고정

		            kFragment.append(kRow);
		        });

		        Util.kBody.replaceChildren(kFragment);

		        SetResultTitle(Util.kTitle, `결과: ${Util.arrTime.length.toLocaleString()}건`);	// 건수 숫자: 존 색
		    }

		    static OnTimeClick(kEvent)	// 2번 창 표 누름: Ac. 삭제 → 그 줄만 지움 (확인 없음, 브라우저 메모리)
		    {
		        const	kRow	= kEvent.target.closest("button[data-action=delete]")?.closest("tr[data-index]");

		        if (kRow == null)
		        {
		            return;
		        }

		        Util.arrTime.splice(Number(kRow.dataset.index), 1);

		        Util.SaveTime();
		        Util.RenderTime();
		    }

		    static OnTimeDragStart(kEvent)	// 2번 창 줄 끌기 시작: 그 줄 위치 기억 (쿼리 설정 줄 끌기와 같음)
		    {
		        const	kRow	= kEvent.target.closest?.("tr[data-index]");	// 끄는 줄

		        if (kRow == null)
		        {
		            return;
		        }

		        Util.nDragIndex = Number(kRow.dataset.index);

		        kEvent.dataTransfer.effectAllowed = "move";
		        kEvent.dataTransfer.setData("text/plain", kRow.cells[1].textContent);

		        kRow.classList.add("drag");
		    }

		    static GetTimeDrop(kEvent)	// 놓을 자리 (줄 위쪽 반: 그 줄 앞, 아래쪽 반·줄 밖: 뒤, 반환: { kRow, bAfter, nInsert }, 줄 없음: null)
		    {
		        const	kOver	= kEvent.target.closest?.("tr[data-index]");	// 마우스 아래 줄
		        const	kRow	= kOver ?? [...Util.kBody.querySelectorAll("tr[data-index]")].at(-1);	// 놓을 줄 (줄 밖: 마지막 줄)

		        if (kRow == null)
		        {
		            return null;
		        }

		        const	kRect	= kRow.getBoundingClientRect();
		        const	bAfter	= kOver == null || kEvent.clientY > kRect.top + kRect.height / 2;

		        return { kRow, bAfter, nInsert: Number(kRow.dataset.index) + (bAfter ? 1 : 0) };
		    }

		    static OnTimeDragOver(kEvent)	// 끄는 중: 놓을 자리 표시 (줄 앞·뒤 존 색 선)
		    {
		        if (Util.nDragIndex < 0)	// 결과 줄 끌기 아님
		        {
		            return;
		        }

		        kEvent.preventDefault();
		        kEvent.dataTransfer.dropEffect = "move";

		        Util.ClearTimeDrop();

		        const	kDrop	= Util.GetTimeDrop(kEvent);

		        kDrop?.kRow.classList.add(kDrop.bAfter ? "dropafter" : "dropbefore");
		    }

		    static ClearTimeDrop()	// 놓을 자리 표시 지움
		    {
		        Util.kBody.querySelectorAll(".dropbefore, .dropafter").forEach(kItem => kItem.classList.remove("dropbefore", "dropafter"));
		    }

		    static OnTimeDrop(kEvent)	// 놓음: 줄 → 그 자리 (No. 다시 1부터, 브라우저 저장)
		    {
		        const	nFrom	= Util.nDragIndex;	// 옮길 줄

		        if (nFrom < 0)
		        {
		            return;
		        }

		        kEvent.preventDefault();

		        const	kDrop	= Util.GetTimeDrop(kEvent);

		        Util.OnTimeDragEnd();

		        if (kDrop == null)
		        {
		            return;
		        }

		        const	nTo	= kDrop.nInsert > nFrom ? kDrop.nInsert - 1 : kDrop.nInsert;	// 옮길 자리

		        if (nTo === nFrom)
		        {
		            return;
		        }

		        Util.arrTime.splice(nTo, 0, ...Util.arrTime.splice(nFrom, 1));

		        Util.SaveTime();
		        Util.RenderTime();
		    }

		    static OnTimeDragEnd()	// 끌기 끝 (놓음·취소): 표시 지움
		    {
		        Util.nDragIndex = -1;

		        Util.ClearTimeDrop();

		        Util.kBody.querySelector("tr.drag")?.classList.remove("drag");
		    }

		    static FitTimeHeight()	// 2번 창 높이: 브라우저 창 아래 끝에 맞춤 (타임 보일 때만)
		    {
		        if (!kUtilPanel.hidden && Util.sCur === "time")
		        {
		            FitHeight(Util.kScroll, Util.kResult);
		        }
		    }

		    static FitDiffHeight()	// 디프 좌·우 창 높이: 브라우저 창 아래 끝에 맞춤 (디프 보일 때만)
		    {
		        if (!kUtilPanel.hidden && Util.sCur === "diff")
		        {
		            FitHeight(document.getElementById("UtilDiffLeftBody"), document.getElementById("UtilDiffLeft"));
		            FitHeight(document.getElementById("UtilDiffRightBody"), document.getElementById("UtilDiffRight"));
		        }
		    }

		    static Myers(aA, aB, nOff, n, m)	// 가운데 구간(앞뒤 같은 줄 제외) Myers 최소 편집 → [['=', a, b] | ['-', a] | ['+', b]] (원본 줄 번호), 편집 수가 MYERS_LIMIT 넘으면 null (DataCompare 와 같음)
		    {
		        const nMax = n + m;
		        const nO = nMax + 1;	// k(-nMax-1 ~ nMax+1) → 배열 위치
		        const aV = new Int32Array(2 * nMax + 3);
		        const aTrace = [];	// 단계 d 시작 시점 aV 중 k(-d-1 ~ d+1) 부분 (위치 k + d + 1)
		        let bDone = (nMax === 0);

		        for (let d = 0; d <= nMax && !bDone; d++)
		        {
		            if (d > Util.MYERS_LIMIT)
		            {
		                return null;
		            }

		            aTrace.push(aV.slice(nO - d - 1, nO + d + 2));

		            for (let k = -d; k <= d; k += 2)
		            {
		                let x = (k === -d || (k !== d && aV[nO + k - 1] < aV[nO + k + 1])) ? aV[nO + k + 1] : aV[nO + k - 1] + 1;
		                let y = x - k;

		                while (x < n && y < m && aA[nOff + x] === aB[nOff + y])	// 같은 줄은 대각선으로 진행
		                {
		                    x++;
		                    y++;
		                }

		                aV[nO + k] = x;

		                if (x >= n && y >= m)
		                {
		                    bDone = true;
		                    break;
		                }
		            }
		        }

		        const aOps = [];
		        let x = n;
		        let y = m;

		        for (let d = aTrace.length - 1; d >= 0; d--)	// 끝에서부터 되짚기
		        {
		            const aSnap = aTrace[d];
		            const k = x - y;
		            const nPrevK = (k === -d || (k !== d && aSnap[k + d] < aSnap[k + d + 2])) ? k + 1 : k - 1;
		            const nPrevX = aSnap[nPrevK + d + 1];
		            const nPrevY = nPrevX - nPrevK;

		            while (x > nPrevX && y > nPrevY)
		            {
		                aOps.push(['=', nOff + x - 1, nOff + y - 1]);
		                x--;
		                y--;
		            }

		            if (d > 0)
		            {
		                aOps.push((x === nPrevX) ? ['+', nOff + nPrevY] : ['-', nOff + nPrevX]);
		            }

		            x = nPrevX;
		            y = nPrevY;
		        }

		        return aOps.reverse();
		    }

		    static DiffLines(aA, aB)	// 줄 비교 → {aOps: [['=', a, b] | ['-', a] | ['+', b]], bRough: 차이가 너무 많아 가운데를 통째로 바뀜 처리했는지}
		    {
		        let nPre = 0;
		        let nSuf = 0;

		        while (nPre < aA.length && nPre < aB.length && aA[nPre] === aB[nPre])
		        {
		            nPre++;
		        }

		        while (nSuf < aA.length - nPre && nSuf < aB.length - nPre && aA[aA.length - 1 - nSuf] === aB[aB.length - 1 - nSuf])
		        {
		            nSuf++;
		        }

		        const n = aA.length - nPre - nSuf;
		        const m = aB.length - nPre - nSuf;
		        let aMid = Util.Myers(aA, aB, nPre, n, m);
		        const bRough = (aMid === null);

		        if (bRough)
		        {
		            aMid = [];

		            for (let i = 0; i < n; i++)
		            {
		                aMid.push(['-', nPre + i]);
		            }

		            for (let i = 0; i < m; i++)
		            {
		                aMid.push(['+', nPre + i]);
		            }
		        }

		        const aOps = [];

		        for (let i = 0; i < nPre; i++)
		        {
		            aOps.push(['=', i, i]);
		        }

		        for (const aOp of aMid)
		        {
		            aOps.push(aOp);
		        }

		        for (let i = 0; i < nSuf; i++)
		        {
		            aOps.push(['=', aA.length - nSuf + i, aB.length - nSuf + i]);
		        }

		        return { aOps, bRough };
		    }

		    static RunDiff()	// 디프 비교 (DataCompare 와 같은 형식, 보기 따로 없음: 편집 칸 그대로): 좌·우 줄 비교 (Myers) → 편집 칸에 반대쪽에만 있는 줄 자리 빈 줄 넣음 (회색·줄 번호 없음) + 줄 뒤 색 (바뀐 줄 하늘색, 줄 안 다른 부분 노랑), 차이: 이어진 바뀜 묶음, 한쪽 비움: 비교 안 함 (자동 비교 안 함, 디프 실행 버튼), 편집: 기본 편집 칸 그대로
		    {
		        const	sLeft	= Util.GetDiffText("Left");	// 회색 빈 줄 뺀 글
		        const	sRight	= Util.GetDiffText("Right");

		        Util.ResetDiffInfo();	// 앞 비교 지움 (빈 줄 뺌)

		        if (sLeft === "" || sRight === "")	// 한쪽 비움: 비교 안 함
		        {
		            return;
		        }

		        const	arrA		= sLeft.split("\n");
		        const	arrB		= sRight.split("\n");
		        const	arrRow		= Util.PairRows(Util.DiffLines(arrA, arrB).aOps);	// 좌·우 한 줄씩 (DataCompare 와 같음)
		        const	arrBlock	= [];	// 차이 (줄 맞춤 줄 번호 묶음)
		        const	mapLine		= { Left: [], Right: [] };	// 줄 맞춤 글 (빈 줄 넣음)
		        const	mapMeta		= { Left: [], Right: [] };

		        arrRow.forEach((kRow, nIndex) =>
		        {
		            if (kRow.bStart)
		            {
		                arrBlock.push([]);
		            }

		            if (kRow.sKind !== "=")
		            {
		                arrBlock.at(-1).push(nIndex);
		            }

		            for (const [sSide, nLine, arrText, nOther, arrOther] of [["Left", kRow.a, arrA, kRow.b, arrB], ["Right", kRow.b, arrB, kRow.a, arrA]])
		            {
		                const	sText	= nLine >= 0 ? arrText[nLine] : "";

		                mapLine[sSide].push(sText);
		                mapMeta[sSide].push({ sKind: nLine < 0 ? "gap" : (kRow.sKind === "=" ? "" : "chg"), sOrig: sText, sOther: kRow.sKind === "~" ? arrOther[nOther] : null, nBlock: kRow.sKind === "=" ? -1 : arrBlock.length - 1 });
		            }
		        });

		        for (const sSide of ["Left", "Right"])
		        {
		            const	kText		= document.getElementById(`UtilDiff${sSide}Text`);
		            const	sAligned	= mapLine[sSide].join("\n");

		            if (kText.value !== sAligned)	// 빈 줄 넣음 (같음: 그대로 → Ctrl+Z 유지)
		            {
		                kText.value = sAligned;
		            }

		            kText.scrollTop			= 0;
		            kText.scrollLeft		= 0;
		            Util.mapDiffMeta[sSide]		= mapMeta[sSide];
		            Util.mapDiffLines[sSide]	= mapLine[sSide];
		            Util.mapDiffHistory[sSide]	= [];
		            Util.mapDiffKey[sSide]		= Util.TextKey(sAligned);
		        }

		        Util.arrDiffBlock	= arrBlock;
		        Util.sDiffState		= arrBlock.length > 0 ? "다름" : "같음";
		        Util.bDiffView		= true;

		        Util.RenderLines("Left");
		        Util.RenderLines("Right");
		        Util.ShowDiffInfo();
		        Util.GoBlock(0);	// 첫 차이로 (없음: 맨 위)
		    }

		    static PairRows(aOps)	// 편집 목록 → 좌우 한 줄씩 [{ a, b, sKind, bStart }] (sKind: = 같음·~ 바뀜·- 기준에만·+ 대상에만, 줄 없음: -1, bStart: 차이 묶음 첫 줄, DataCompare 와 같음)
		    {
		        const	arrRow	= [];
		        let		nIndex	= 0;

		        while (nIndex < aOps.length)
		        {
		            if (aOps[nIndex][0] === "=")
		            {
		                arrRow.push({ a: aOps[nIndex][1], b: aOps[nIndex][2], sKind: "=", bStart: false });
		                nIndex++;

		                continue;
		            }

		            const	arrDel	= [];
		            const	arrAdd	= [];

		            while (nIndex < aOps.length && aOps[nIndex][0] !== "=")	// 이어진 차이 = 한 묶음, 지운 줄과 넣은 줄 순서대로 짝
		            {
		                (aOps[nIndex][0] === "-" ? arrDel : arrAdd).push(aOps[nIndex][1]);
		                nIndex++;
		            }

		            for (let nPair = 0; nPair < Math.max(arrDel.length, arrAdd.length); nPair++)
		            {
		                const	bA	= nPair < arrDel.length;
		                const	bB	= nPair < arrAdd.length;

		                arrRow.push({ a: bA ? arrDel[nPair] : -1, b: bB ? arrAdd[nPair] : -1, sKind: bA && bB ? "~" : (bA ? "-" : "+"), bStart: nPair === 0 });
		            }
		        }

		        return arrRow;
		    }

		    static OnDiffRun()	// 디프 실행 버튼 (저장 뒤도): 지금 좌·우 입력 기억 (지금 위치와 같음: 안 넣음, 과거에서 실행: 그 뒤 기억 버림, 넘음: 가장 오래된 것 버림) → 다시 비교 (고친 줄 초록 지움, 보기에서 고친 뒤: 보던 자리 차이로)
		    {
		        const	kState	= { sLeft: Util.GetDiffText("Left"), sRight: Util.GetDiffText("Right"), sLeftName: Util.mapDiffName.Left, sRightName: Util.mapDiffName.Right };
		        const	kNow	= Util.arrDiffBack[Util.nDiffPos];
		        const	nTop	= Util.bDiffView ? document.getElementById("UtilDiffLeftText").scrollTop : -1;	// 보던 자리 (비교 안 함: -1)

		        if (kNow == null || kNow.sLeft !== kState.sLeft || kNow.sRight !== kState.sRight)
		        {
		            Util.arrDiffBack.length = Util.nDiffPos + 1;	// 지금 위치 뒤 (→ 쪽) 기억 버림
		            Util.arrDiffBack.push(kState);

		            if (Util.arrDiffBack.length > Util.DIFF_BACK_MAX)
		            {
		                Util.arrDiffBack.shift();
		            }

		            Util.nDiffPos = Util.arrDiffBack.length - 1;
		        }

		        Util.ShowDiffBack();
		        Util.RunDiff();

		        if (nTop >= 0 && Util.bDiffView)	// 보던 자리 차이로 (그 아래 첫 차이, 없음: 끝 차이, 차이 없음: 보던 자리)
		        {
		            const	nHeight	= Util.LineHeight(document.getElementById("UtilDiffLeftText"));
		            const	nBlock	= Util.arrDiffBlock.findIndex(arrBlock => 4 + arrBlock[0] * nHeight >= nTop);	// 그 차이 첫 줄 위치 (편집 칸 위 여백 X px)

		            if (Util.arrDiffBlock.length > 0)
		            {
		                Util.GoBlock(nBlock >= 0 ? nBlock : Util.arrDiffBlock.length - 1);
		            }
		            else
		            {
		                document.getElementById("UtilDiffLeftText").scrollTop = nTop;
		            }
		        }
		    }

		    static async SaveDiffFile(sSide)	// 저장 버튼 (그 창 글 있음): 연 파일 글 → 연 파일에 덮어씀 (같은 앞 BOM·줄 끝, 덮어쓸 곳 없음: 저장 창 같은 이름), 파일 없음 (붙여넣기·직접 입력): 저장 창 → 저장할 폴더·이름 고름 (기본 기준·대상.txt, UTF-8·CRLF, 저장 뒤: 그 파일 연 것과 같음), 저장 창 없음 (http 주소): 다운 → 실행 (다시 비교)
		    {
		        const	sValue	= Util.GetDiffText(sSide);	// 그 창 글 (회색 빈 줄 뺌)

		        if (sValue === "" && !Util.IsDiffFile(sSide))	// 파일 없이 비움: 저장 안 함
		        {
		            return;
		        }

		        const	kOpen	= Util.mapDiffFile[sSide];
		        const	kFile	= kOpen != null && kOpen.sName === Util.mapDiffName[sSide] ? kOpen : { kHandle: null, sName: `${sSide === "Left" ? "기준" : "대상"}.txt`, bBom: false, sEol: "\r\n" };	// 연 파일 글: 그 파일, 그 외: 새 파일 (저장할 폴더·이름 고름)
		        const	sText	= (kFile.bBom ? "\uFEFF" : "") + sValue.replace(/\n/g, kFile.sEol);

		        try
		        {
		            let	kHandle	= kFile.kHandle;

		            if (kHandle == null && window.showSaveFilePicker != null)	// 덮어쓸 곳 없음 (새 파일·끌어 놓기 등): 저장 창 (폴더·이름 고름, 마지막 폴더 기억)
		            {
		                kHandle = await window.showSaveFilePicker({ id: "HMXUtilDiff", suggestedName: kFile.sName });
		            }

		            if (kHandle != null && (await kHandle.requestPermission?.({ mode: "readwrite" }) ?? "granted") === "granted")
		            {
		                const	kWrite	= await kHandle.createWritable();	// 파일 비우고 새로 씀 (앞 글 안 남김)

		                await kWrite.write(sText);
		                await kWrite.close();

		                kFile.kHandle				= kHandle;
		                kFile.sName					= kHandle.name;	// 고른 이름
		                Util.mapDiffFile[sSide]		= kFile;	// 저장한 파일: 연 것과 같음 (다음 저장: 덮어씀)
		                Util.mapDiffName[sSide]		= kHandle.name;

		                Util.RenderTitle(sSide);
		            }
		            else	// http 주소 등 저장 창·파일 쓰기 없음: 그 이름 다운 (브라우저 다운 폴더)
		            {
		                const	kLink	= document.createElement("a");

		                kLink.href		= URL.createObjectURL(new Blob([sText], { type: "text/plain" }));
		                kLink.download	= kFile.sName;

		                kLink.click();

		                setTimeout(() => URL.revokeObjectURL(kLink.href), 1000);
		            }

		            Process.Toast(`저장: ${kFile.sName}`, "ok");
		        }
		        catch (kError)	// 저장 창 닫음 (AbortError): 그대로, 그 밖: 실패 알림
		        {
		            if (kError?.name !== "AbortError")
		            {
		                console.error(kError);
		                Process.Toast(`저장 실패: ${kFile.sName} (${ErrorText(kError)})`, "fail");
		            }

		            return;
		        }

		        Util.OnDiffRun();
		    }

		    static GoDiff(nStep)	// 디프 네비게이션 ← (-1: 과거)·→ (+1: 미래)·가운데 숫자 (그 위치까지): 그 실행 좌·우 입력·이름 그대로 → 비교 (기억 지우지 않음: 위치만, 끝: 무시)
		    {
		        const	nPos	= Util.nDiffPos + nStep;

		        if (nPos < 0 || nPos >= Util.arrDiffBack.length)
		        {
		            return;
		        }

		        const	kState	= Util.arrDiffBack[nPos];

		        Util.nDiffPos = nPos;

		        Util.ResetDiffInfo();	// 앞 비교 풂

		        document.getElementById("UtilDiffLeftText").value	= kState.sLeft;
		        document.getElementById("UtilDiffRightText").value	= kState.sRight;
		        Util.mapDiffName.Left								= kState.sLeftName;
		        Util.mapDiffName.Right								= kState.sRightName;

		        Util.RenderLines("Left");
		        Util.RenderLines("Right");
		        Util.ShowDiffBack();
		        Util.RunDiff();
		    }

		    static ShowDiffBack()	// 디프 네비게이션 ← N →: 이전·다음 실행 있음 → 켬 (없음: 꺼짐), 가운데 지금 위치 (0: 마지막 실행, 과거: -1·-2…, 없음: 0)
		    {
		        const	nLast	= Util.arrDiffBack.length - 1;	// 마지막 실행 위치

		        document.getElementById("UtilDiffRunPrev").disabled		= Util.nDiffPos <= 0;
		        document.getElementById("UtilDiffRunNext").disabled		= Util.nDiffPos < 0 || Util.nDiffPos >= nLast;
		        document.getElementById("UtilDiffRunDepth").textContent	= String(Util.nDiffPos - nLast);
		    }

		    static ShowDiffInfo()	// 1번 창 제목: 다름·같음 · 차이 N곳 · 현재 i/N (DataCompare 와 같은 모양, 비교 안 함·고침: 비움) + 이전·다음 차이 버튼
		    {
		        const	nCount	= Util.arrDiffBlock.length;
		        const	sText	= Util.sDiffState === "" ? "" : `${Util.sDiffState} · 차이 ${nCount.toLocaleString()}곳${Util.nDiffBlock >= 0 ? ` · 현재 ${Util.nDiffBlock + 1}/${nCount}` : ""}`;

		        document.getElementById("UtilDiffInfo").textContent	= sText;
		        document.getElementById("UtilDiffPrev").disabled		= Util.nDiffBlock <= 0;
		        document.getElementById("UtilDiffNext").disabled		= Util.nDiffBlock >= nCount - 1;
		    }

		    static ResetDiffInfo()	// 비교 풂 (파일 열기·다시 비교·모두 지움·통째로 바꿈): 회색 빈 줄 뺀 글로 (고쳐 쓴 빈 줄: 남음) + 줄 번호·색 기본 + 차이·제목 비움
		    {
		        for (const sSide of ["Left", "Right"])
		        {
		            Util.mapDiffHistory[sSide]	= [];	// 되돌림 기억 버림 (메모리)
		            Util.mapDiffKey[sSide]		= "";

		            if (Util.mapDiffMeta[sSide] == null)
		            {
		                continue;
		            }

		            const	kText	= document.getElementById(`UtilDiff${sSide}Text`);
		            const	sReal	= Util.GetDiffText(sSide);

		            Util.mapDiffMeta[sSide]		= null;
		            Util.mapDiffLines[sSide]	= null;

		            if (kText.value !== sReal)	// 빈 줄 뺌 (같음: 그대로 → Ctrl+Z 유지)
		            {
		                kText.value = sReal;
		            }

		            document.getElementById(`UtilDiff${sSide}Ln`).replaceChildren();
		            document.getElementById(`UtilDiff${sSide}Back`).replaceChildren();

		            Util.RenderLines(sSide);
		        }

		        Util.arrDiffBlock	= [];
		        Util.nDiffBlock		= -1;
		        Util.sDiffState		= "";
		        Util.bDiffView		= false;
		        Util.kDiffRelease	= null;

		        Util.ShowDiffInfo();
		    }

		    static GoBlock(nBlock)	// 차이 이동 (이전·다음 차이, DataCompare 와 같음): 지금 차이 줄 번호 진한 파랑 + 좌 편집 칸 그 차이 첫 줄 세로 가운데 (우: 스크롤 같이, 끝: 무시)
		    {
		        if (nBlock < 0 || nBlock >= Util.arrDiffBlock.length)
		        {
		            return;
		        }

		        Util.nDiffBlock = nBlock;

		        Util.MarkBlock();

		        const	kText	= document.getElementById("UtilDiffLeftText");
		        const	nLine	= Util.mapDiffMeta.Left?.findIndex((kMeta) => kMeta.nBlock === nBlock) ?? -1;	// 지금 그 차이 첫 줄 (고쳐 옮겨도)

		        if (nLine >= 0)
		        {
		            const	nHeight	= Util.LineHeight(kText);

		            kText.scrollTop = 4 + nLine * nHeight - (kText.clientHeight - nHeight) / 2;	// 위 여백 X px
		        }

		        Util.ShowDiffInfo();
		    }

		    static MarkBlock()	// 지금 차이 줄 번호 진한 파랑 (좌·우, 회색 빈 줄도, DataCompare 와 같음, 없음: 모두 지움)
		    {
		        for (const sSide of ["Left", "Right"])
		        {
		            const	arrMeta	= Util.mapDiffMeta[sSide] ?? [];

		            [...document.getElementById(`UtilDiff${sSide}Ln`).children].forEach((kNum, nIndex) => kNum.classList.toggle("cur", Util.nDiffBlock >= 0 && arrMeta[nIndex]?.nBlock === Util.nDiffBlock));
		        }
		    }

		    static LineHeight(kText)	// 편집 칸 한 줄 높이 (px)
		    {
		        return parseFloat(getComputedStyle(kText).lineHeight) || 19.5;
		    }

		    static IsGapLine(kMeta, sLine)	// 회색 빈 줄 (반대쪽에만 있는 줄 자리, 고쳐 쓰지 않음: 실제 글 아님)
		    {
		        return kMeta?.sKind === "gap" && sLine === "";
		    }

		    static GetDiffText(sSide)	// 그 창 실제 글 (비교 중: 회색 빈 줄 뺌 → 저장·실행·제목 줄 수·스냅샷)
		    {
		        const	sValue	= document.getElementById(`UtilDiff${sSide}Text`).value;
		        const	arrMeta	= Util.mapDiffMeta[sSide];

		        return arrMeta == null ? sValue : sValue.split("\n").filter((sLine, nIndex) => !Util.IsGapLine(arrMeta[nIndex], sLine)).join("\n");
		    }

		    static IsDiffFile(sSide)	// 그 창 글이 연 파일 (찾기·끌어 놓기·저장한 파일, 이름 그대로: 통째로 바꿈·고침에도 유지, 다 지움: 끊음)
		    {
		        const	kFile	= Util.mapDiffFile[sSide];

		        return kFile != null && kFile.sName === Util.mapDiffName[sSide];
		    }

		    static KeepDiffName(sSide, sText, sName)	// 글 바뀐 뒤 이름: 다 지움 (파일 연결 끊음 → 새로 시작: 이름 없음·저장 꺼짐)·연 파일 (유지: 통째로 바꿈 Ctrl+A + Ctrl+V·고침)·그 외 (sName, 없음: 복사)
		    {
		        if (sText === "")
		        {
		            Util.mapDiffName[sSide]	= "";
		            Util.mapDiffFile[sSide]	= null;
		        }
		        else if (!Util.IsDiffFile(sSide))
		        {
		            Util.mapDiffName[sSide] = sName || "복사";
		        }
		    }

		    static DiffRow(arrMeta, nIndex, sLine)	// 비교 중 한 줄: 줄 번호 칸·줄 뒤 칸 { kNum, kLine, bGap } (번호 글자: 부르는 쪽)
		    {
		        const	kMeta	= arrMeta[nIndex];
		        const	bGap	= Util.IsGapLine(kMeta, sLine);
		        const	bEdit	= !bGap && sLine !== kMeta?.sOrig;	// 고친 줄·늘어난 줄
		        const	kNum	= document.createElement("div");
		        const	kLine	= document.createElement("div");

		        kNum.className	= [bEdit ? "edit" : "", Util.nDiffBlock >= 0 && kMeta?.nBlock === Util.nDiffBlock ? "cur" : ""].join(" ").trim();
		        kLine.className	= bGap ? "gap" : (bEdit ? "edit" : (kMeta.sKind === "chg" ? "chg" : ""));

		        Util.MarkText(kLine, sLine, bEdit ? null : kMeta.sOther);

		        return { kNum, kLine, bGap };
		    }

		    static RenderDiffSide(sSide, kRange = null)	// 비교 중 줄 번호·줄 뒤 색 (편집 칸 줄마다: 바뀐 줄 하늘색·회색 빈 줄 (줄 번호 없음)·고친 줄 연한 초록 (줄 번호 진한 초록·흰 숫자, 되돌림: 원래 색)·지금 차이 줄 번호 진한 파랑·줄 안 다른 부분 노랑), 줄 번호: 회색 빈 줄 빼고 매김, kRange { nStart, nOld, nNew }: 그 줄만 다시 (나머지 그대로, 줄 수 바뀜: 아래 줄 번호만 고침, null: 모두)
		    {
		        const	arrMeta	= Util.mapDiffMeta[sSide];
		        const	arrLine	= Util.mapDiffLines[sSide] ?? document.getElementById(`UtilDiff${sSide}Text`).value.split("\n");
		        const	kLn		= document.getElementById(`UtilDiff${sSide}Ln`);
		        const	kBack	= document.getElementById(`UtilDiff${sSide}Back`);
		        const	kLnFrag		= document.createDocumentFragment();
		        const	kBackFrag	= document.createDocumentFragment();
		        const	nWas		= kRange == null ? -1 : arrLine.length - kRange.nNew + kRange.nOld;	// 고치기 전 줄 수

		        if (kRange == null || kLn.children.length !== nWas || kBack.querySelectorAll(":scope > div").length !== nWas)	// 모두 (처음·되돌림·어긋남)
		        {
		            let	nNum	= 0;

		            arrLine.forEach((sLine, nIndex) =>
		            {
		                const	kRow	= Util.DiffRow(arrMeta, nIndex, sLine);

		                kRow.kNum.textContent = kRow.bGap ? "" : String(++nNum);

		                kLnFrag.append(kRow.kNum);
		                kBackFrag.append(kRow.kLine);
		            });

		            kLn.replaceChildren(kLnFrag);
		            kBack.replaceChildren(kBackFrag);

		            return;
		        }

		        const	{ nStart, nOld, nNew }	= kRange;
		        let		nNum	= 0;	// 고친 곳 앞 마지막 줄 번호
		        let		nOldNum	= 0;	// 지운 줄 중 번호 있는 줄 수

		        for (let nIndex = nStart - 1; nIndex >= 0; --nIndex)
		        {
		            if (kLn.children[nIndex].textContent !== "")
		            {
		                nNum = Number(kLn.children[nIndex].textContent);

		                break;
		            }
		        }

		        for (let nIndex = 0; nIndex < nOld; ++nIndex)	// 바뀐 줄 지움
		        {
		            nOldNum += kLn.children[nStart].textContent !== "" ? 1 : 0;

		            kLn.children[nStart].remove();
		            kBack.children[nStart].remove();
		        }

		        const	nFirst	= nNum;

		        for (let nIndex = nStart; nIndex < nStart + nNew; ++nIndex)	// 새 줄
		        {
		            const	kRow	= Util.DiffRow(arrMeta, nIndex, arrLine[nIndex]);

		            kRow.kNum.textContent = kRow.bGap ? "" : String(++nNum);

		            kLnFrag.append(kRow.kNum);
		            kBackFrag.append(kRow.kLine);
		        }

		        kLn.insertBefore(kLnFrag, kLn.children[nStart] ?? null);
		        kBack.insertBefore(kBackFrag, kBack.children[nStart] ?? null);	// 고른 빈 줄 막대 (끝): 그 앞

		        const	nDelta	= nNum - nFirst - nOldNum;	// 번호 있는 줄 늘고 줄어든 수

		        if (nDelta !== 0)	// 아래 줄 번호만 고침
		        {
		            for (let nIndex = nStart + nNew; nIndex < kLn.children.length; ++nIndex)
		            {
		                const	kNum	= kLn.children[nIndex];

		                if (kNum.textContent !== "")
		                {
		                    kNum.textContent = String(Number(kNum.textContent) + nDelta);
		                }
		            }
		        }
		    }

		    static MarkText(kLine, sText, sOther)	// 줄 뒤 글 (보이지 않음, 줄 안 다른 부분: 노랑 = sOther 와 앞뒤 같은 글자 뺀 가운데, DataCompare 와 같음)
		    {
		        if (sOther == null || sText === sOther)
		        {
		            kLine.textContent = sText;

		            return;
		        }

		        let	nPre	= 0;
		        let	nSuf	= 0;

		        while (nPre < sText.length && nPre < sOther.length && sText[nPre] === sOther[nPre])
		        {
		            nPre++;
		        }

		        while (nSuf < sText.length - nPre && nSuf < sOther.length - nPre && sText[sText.length - 1 - nSuf] === sOther[sOther.length - 1 - nSuf])
		        {
		            nSuf++;
		        }

		        const	kMark	= document.createElement("mark");

		        kMark.textContent = sText.slice(nPre, sText.length - nSuf);

		        kLine.append(sText.slice(0, nPre), kMark, sText.slice(sText.length - nSuf));
		    }

		    static TextKey(sText)	// 글 지문 (길이:FNV-1a 32비트 해시, 되돌림 찾기: 글 사본 안 둠)
		    {
		        let	nHash	= 0x811c9dc5;

		        for (let nIndex = 0; nIndex < sText.length; ++nIndex)
		        {
		            nHash ^= sText.charCodeAt(nIndex);
		            nHash = Math.imul(nHash, 0x01000193);
		        }

		        return `${sText.length}:${nHash >>> 0}`;
		    }

		    static UpdateDiffMeta(sSide, sInputType)	// 비교 중 고침: 앞뒤 같은 줄 빼고 바뀐 곳만 줄 정보 맞춤 (그 자리 줄: 앞 정보 순서대로, 늘어난 줄: 새 줄 (고침), 아래 줄: 색 따라감, 좌·우 줄 맞춤: 다시 실행 때), Ctrl+Z·Y 로 전에 있던 글: 그때 줄 정보 그대로, 반환: 다시 그릴 줄 { nStart, nOld, nNew } (null: 모두)
		    {
		        const	sValue		= document.getElementById(`UtilDiff${sSide}Text`).value;
		        const	sKey		= Util.TextKey(sValue);
		        const	arrOld		= Util.mapDiffLines[sSide];
		        const	arrNew		= sValue.split("\n");
		        const	arrMeta		= Util.mapDiffMeta[sSide];
		        const	arrHistory	= Util.mapDiffHistory[sSide];
		        let		nTotal		= 0;	// 기억 줄 정보 합

		        arrHistory.push({ sKey: Util.mapDiffKey[sSide], arrMeta });	// 고침 전 (되돌림용)
		        arrHistory.forEach((kItem) => { nTotal += kItem.arrMeta.length; });

		        while (arrHistory.length > Util.DIFF_HISTORY_MAX || (arrHistory.length > 1 && nTotal > Util.DIFF_HISTORY_LINE))	// 넘음: 오래된 것 버림
		        {
		            nTotal -= arrHistory.shift().arrMeta.length;
		        }

		        Util.mapDiffKey[sSide]		= sKey;
		        Util.mapDiffLines[sSide]	= arrNew;

		        if (sInputType?.startsWith("history"))	// Ctrl+Z·Y: 전에 있던 글 → 그때 줄 정보
		        {
		            const	kHit	= arrHistory.findLast((kItem) => kItem.sKey === sKey && kItem.arrMeta.length === arrNew.length);

		            if (kHit != null)
		            {
		                Util.mapDiffMeta[sSide] = kHit.arrMeta;

		                return null;
		            }
		        }

		        let	nPre	= 0;
		        let	nSuf	= 0;

		        while (nPre < arrOld.length && nPre < arrNew.length && arrOld[nPre] === arrNew[nPre])
		        {
		            nPre++;
		        }

		        while (nSuf < arrOld.length - nPre && nSuf < arrNew.length - nPre && arrOld[arrOld.length - 1 - nSuf] === arrNew[arrNew.length - 1 - nSuf])
		        {
		            nSuf++;
		        }

		        const	nOld	= arrOld.length - nPre - nSuf;
		        const	nNew	= arrNew.length - nPre - nSuf;
		        const	arrMid	= Array.from({ length: nNew }, (_, nIndex) => nIndex < nOld ? arrMeta[nPre + nIndex] : { sKind: "add", sOrig: null, sOther: null, nBlock: -1 });

		        Util.mapDiffMeta[sSide] = arrMeta.slice(0, nPre).concat(arrMid, arrMeta.slice(arrMeta.length - nSuf));

		        return { nStart: nPre, nOld, nNew };
		    }

		    static OnDiffBeforeInput(sSide, kEvent)	// 고침 앞: 비교 중 모두 고름 → 지움·붙여넣기 (비교 모두 풂)
		    {
		        const	kText	= kEvent.currentTarget;

		        Util.mapDiffWhole[sSide] = Util.mapDiffMeta[sSide] != null && kText.value !== "" && kText.selectionStart === 0 && kText.selectionEnd === kText.value.length && (kEvent.inputType.startsWith("delete") || kEvent.inputType === "insertFromPaste");
		    }

		    static OnDiffInput(sSide, kEvent)	// 편집 칸 고침 (비교 중도 기본 편집 그대로: 끌어 고름·여러 줄 지움·붙여넣기·Ctrl+Z·Y): 비교 중 → 바뀐 곳만 줄 색 맞춤, 모두 고름 → 지움·붙여넣기·다 지움: 비교 모두 풂 (Ctrl+Z: 그 전 글·비교 돌아옴, Ctrl+Y: 다시 풂) + 줄 번호·제목 (이름 없이 입력: 복사, 다 지움: 파일 연결 끊음)
		    {
		        const	kText		= kEvent.currentTarget;
		        const	kRelease	= Util.kDiffRelease;
		        const	sOther		= sSide === "Left" ? "Right" : "Left";
		        let		kRange		= null;	// 다시 그릴 줄 (null: 모두)

		        if (Util.mapDiffMeta[sSide] != null)
		        {
		            if (Util.mapDiffWhole[sSide] || kText.value === "" || (kRelease?.bRestored && kRelease.sSide === sSide && kEvent.inputType === "historyRedo" && kText.value === kRelease.sAfter))	// 모두 지움·통째로 바꿈 (Ctrl+Y 도)
		            {
		                Util.ReleaseDiff(sSide);
		            }
		            else
		            {
		                kRange = Util.UpdateDiffMeta(sSide, kEvent.inputType);

		                Util.kDiffRelease = null;	// 다른 고침: 되돌림 기억 버림
		            }
		        }
		        else if (kRelease != null && !kRelease.bRestored && kRelease.sSide === sSide && kEvent.inputType === "historyUndo" && kText.value === kRelease.sBefore && document.getElementById(`UtilDiff${sOther}Text`).value === kRelease.sOtherAfter)	// Ctrl+Z: 비교 푼 직전 글 → 비교 다시 켬
		        {
		            Util.RestoreDiff();
		        }
		        else
		        {
		            Util.kDiffRelease = null;
		        }

		        Util.mapDiffWhole[sSide] = false;

		        Util.KeepDiffName(sSide, Util.GetDiffText(sSide), Util.mapDiffName[sSide]);	// 연 파일: 그대로
		        Util.RenderLines(sSide, kRange);
		    }

		    static ReleaseDiff(sSide)	// 모두 고름 → 지움·붙여넣기·다 지움: 비교 모두 풂 (좌·우 회색 빈 줄 뺌·색·지금 차이·차이 이동), 직전 비교 기억 (Ctrl+Z: 다시 켬)
		    {
		        const	kOther		= document.getElementById(`UtilDiff${sSide === "Left" ? "Right" : "Left"}Text`);
		        const	kRelease	= { sSide, sBefore: Util.mapDiffLines[sSide].join("\n"), sAfter: document.getElementById(`UtilDiff${sSide}Text`).value, sOtherBefore: kOther.value, mapMeta: { ...Util.mapDiffMeta }, mapLines: { ...Util.mapDiffLines }, arrDiffBlock: Util.arrDiffBlock, nDiffBlock: Util.nDiffBlock, sDiffState: Util.sDiffState, bRestored: false };

		        Util.mapDiffMeta[sSide]		= null;	// 이 창: 바뀐 글 그대로 (뺄 빈 줄 없음)
		        Util.mapDiffLines[sSide]	= null;

		        document.getElementById(`UtilDiff${sSide}Ln`).replaceChildren();
		        document.getElementById(`UtilDiff${sSide}Back`).replaceChildren();

		        Util.ResetDiffInfo();	// 반대쪽: 회색 빈 줄 뺌 + 색 풂

		        kRelease.sOtherAfter	= kOther.value;
		        Util.kDiffRelease		= kRelease;
		    }

		    static RestoreDiff()	// 비교 푼 뒤 Ctrl+Z 로 그 직전 글: 비교 다시 켬 (좌·우 회색 빈 줄·색·지금 차이 그대로, Ctrl+Y: 다시 풂)
		    {
		        const	kRelease	= Util.kDiffRelease;
		        const	sOther		= kRelease.sSide === "Left" ? "Right" : "Left";
		        const	kOther		= document.getElementById(`UtilDiff${sOther}Text`);

		        if (kOther.value !== kRelease.sOtherBefore)
		        {
		            kOther.value = kRelease.sOtherBefore;
		        }

		        Util.mapDiffMeta	= { ...kRelease.mapMeta };
		        Util.mapDiffLines	= { ...kRelease.mapLines };
		        Util.arrDiffBlock	= kRelease.arrDiffBlock;
		        Util.nDiffBlock		= kRelease.nDiffBlock;
		        Util.sDiffState		= kRelease.sDiffState;
		        Util.bDiffView		= true;
		        kRelease.bRestored	= true;

		        for (const sSide of ["Left", "Right"])	// 지문: 지금 글 (되돌림 기억: 새로)
		        {
		            Util.mapDiffKey[sSide]		= Util.TextKey(document.getElementById(`UtilDiff${sSide}Text`).value);
		            Util.mapDiffHistory[sSide]	= [];
		        }

		        Util.KeepDiffName(sOther, Util.GetDiffText(sOther), Util.mapDiffName[sOther]);
		        Util.RenderLines(sOther);
		        Util.ShowDiffInfo();
		    }

		    static OnDiffCopy(sSide, kEvent)	// 비교 중 복사·잘라내기: 회색 빈 줄 빼고 (잘라내기: 고른 글 지움 → Ctrl+Z 됨)
		    {
		        const	kText	= kEvent.currentTarget;
		        const	arrMeta	= Util.mapDiffMeta[sSide];
		        const	nStart	= kText.selectionStart;
		        const	nEnd	= kText.selectionEnd;

		        if (arrMeta == null || nStart === nEnd)
		        {
		            return;
		        }

		        const	arrLine	= kText.value.split("\n");
		        const	nFirst	= kText.value.slice(0, nStart).split("\n").length - 1;	// 고름 첫 줄
		        const	arrPart	= kText.value.slice(nStart, nEnd).split("\n");
		        const	arrKeep	= arrPart.filter((_, nIndex) => !Util.IsGapLine(arrMeta[nFirst + nIndex], arrLine[nFirst + nIndex]));

		        if (arrKeep.length === arrPart.length)	// 회색 빈 줄 없음: 기본
		        {
		            return;
		        }

		        kEvent.preventDefault();
		        kEvent.clipboardData.setData("text/plain", arrKeep.join("\n"));

		        if (kEvent.type === "cut")
		        {
		            document.execCommand("delete");	// 고른 글 지움 (Ctrl+Z 됨)
		        }
		    }

		    static OnDiffScroll(sSide)	// 편집 칸 스크롤: 줄 뒤 색·줄 번호 같은 자리 + 비교 중: 반대쪽 세로 같이 (가로: 따로, 따라 옮긴 쪽: 되돌려 옮기지 않음)
		    {
		        const	kText	= document.getElementById(`UtilDiff${sSide}Text`);
		        const	sOther	= sSide === "Left" ? "Right" : "Left";
		        const	kOther	= document.getElementById(`UtilDiff${sOther}Text`);

		        Util.SyncBack(sSide);

		        if (Util.mapDiffSync[sSide])
		        {
		            Util.mapDiffSync[sSide] = false;

		            return;
		        }

		        if (Util.bDiffView && kOther.scrollTop !== kText.scrollTop)
		        {
		            const	nBefore	= kOther.scrollTop;

		            kOther.scrollTop = kText.scrollTop;

		            Util.mapDiffSync[sOther] = kOther.scrollTop !== nBefore;	// 옮김: 그쪽 스크롤 한 번 건너뜀
		        }
		    }

		    static RenderSelBar(kText, kLayer)	// 고른 빈 줄 표시 (끌기·Ctrl+A, 편집 칸: 빈 줄 고름 안 보임 → 줄 처음 한 칸 진한 파랑 = 고름 색, 포커스 없음: 숨김)
		    {
		        for (const kBar of Util.mapSelBar.get(kLayer) ?? [])	// 그린 막대만 지움 (줄 수 무관)
		        {
		            kBar.remove();
		        }

		        Util.mapSelBar.delete(kLayer);

		        const	nStart	= kText.selectionStart;
		        const	nEnd	= kText.selectionEnd;

		        if (nStart === nEnd)
		        {
		            return;
		        }

		        const	sValue	= kText.value;
		        const	nHeight	= Util.LineHeight(kText);
		        const	kFrag	= document.createDocumentFragment();
		        let		nLine	= 0;	// 지금 줄 번호
		        let		nPos	= sValue.indexOf("\n");

		        while (nPos >= 0 && nPos < nStart)	// 고름 처음 줄
		        {
		            nLine++;
		            nPos = sValue.indexOf("\n", nPos + 1);
		        }

		        while (nPos >= 0 && nPos < nEnd)	// 고른 줄바꿈: 빈 줄이면 표시
		        {
		            if (nPos === 0 || sValue[nPos - 1] === "\n")
		            {
		                const	kBar	= document.createElement("span");

		                kBar.className	= "selbar";
		                kBar.style.top	= `${4 + nLine * nHeight}px`;	// 위 여백 X px

		                kFrag.append(kBar);
		            }

		            nLine++;
		            nPos = sValue.indexOf("\n", nPos + 1);
		        }

		        Util.mapSelBar.set(kLayer, [...kFrag.children]);

		        kLayer.append(kFrag);
		    }

		    static OnSelBar()	// 고름 바뀜 (공통 selectionchange, 끄는 중 잦음: 한 화면 틀에 1번): 지금 편집 칸 고른 빈 줄 표시 (디프 좌·우·소트 좌)
		    {
		        if (Util.nSelFrame !== 0)
		        {
		            return;
		        }

		        Util.nSelFrame = requestAnimationFrame(() =>
		        {
		            const	kText	= document.activeElement;
		            const	kLayer	= { UtilDiffLeftText: "UtilDiffLeftBack", UtilDiffRightText: "UtilDiffRightBack", UtilSortLeftText: "UtilSortLeftBack" }[kText?.id];

		            Util.nSelFrame = 0;

		            if (kLayer != null)
		            {
		                Util.RenderSelBar(kText, document.getElementById(kLayer));
		            }
		        });
		    }

		    static RenderLines(sSide, kRange = null)	// 편집 칸 왼쪽 줄 번호 (DataCompare 줄 번호 칸 모양, 줄 수 바뀜만 늘림·줄임, 비교 중: 회색 빈 줄 빼고 매김 + 줄 뒤 색) + 고른 빈 줄 + 제목
		    {
		        if (Util.mapDiffMeta[sSide] != null)	// 비교 중
		        {
		            Util.RenderDiffSide(sSide, kRange);	// 고침: 바뀐 줄만
		            Util.SyncBack(sSide);
		            Util.RenderSelBar(document.getElementById(`UtilDiff${sSide}Text`), document.getElementById(`UtilDiff${sSide}Back`));
		            Util.RenderTitle(sSide);

		            return;
		        }

		        const	kLn		= document.getElementById(`UtilDiff${sSide}Ln`);
		        const	nCount	= document.getElementById(`UtilDiff${sSide}Text`).value.split("\n").length;	// 줄 수 (빈 칸: 1)
		        const	nNow	= kLn.children.length;

		        if (nCount > nNow)
		        {
		            const	kFragment	= document.createDocumentFragment();

		            for (let nIndex = nNow; nIndex < nCount; ++nIndex)
		            {
		                const	kLine	= document.createElement("div");

		                kLine.textContent = String(nIndex + 1);

		                kFragment.append(kLine);
		            }

		            kLn.append(kFragment);
		        }
		        else if (nCount < nNow)
		        {
		            kLn.replaceChildren(...[...kLn.children].slice(0, nCount));
		        }

		        Util.SyncBack(sSide);
		        Util.RenderSelBar(document.getElementById(`UtilDiff${sSide}Text`), document.getElementById(`UtilDiff${sSide}Back`));
		        Util.RenderTitle(sSide);
		    }

		    static RenderTitle(sSide)	// 디프 창 제목: 기준·대상 · 줄 N - 파일 이름 (DataCompare 처럼 좌 기준·우 대상) (소트 제목 줄 N 과 같음, 상자 머리 짧은 하이픈, 붙여넣기·직접 입력 (복사)·이름 없음: 줄 N 만, 비움: 줄 0)
		    {
		        const	sText	= Util.GetDiffText(sSide);	// 회색 빈 줄 뺌
		        const	nLine	= sText === "" ? 0 : sText.split("\n").length;
		        const	sName	= Util.mapDiffName[sSide];

		        document.getElementById(`UtilDiff${sSide}Title`).textContent = `${sSide === "Left" ? "기준" : "대상"} · 줄 ${nLine.toLocaleString()}${sName === "" || sName === "복사" ? "" : " - " + sName}`;
		        document.getElementById(`UtilDiff${sSide}Save`).disabled = sText === "" && !Util.IsDiffFile(sSide);	// 저장: 글 있음·연 파일 (빈 파일 열기: 덮어씀), 파일 없음: 저장할 폴더·이름 고름, 다 지움: 꺼짐 (파일 연결 끊음)
		    }

		    static SyncBack(sSide)	// 편집 칸 스크롤 → 뒤 색·줄 번호 같은 자리
		    {
		        const	kText	= document.getElementById(`UtilDiff${sSide}Text`);
		        const	kBack	= document.getElementById(`UtilDiff${sSide}Back`);

		        kBack.scrollTop		= kText.scrollTop;
		        kBack.scrollLeft	= kText.scrollLeft;
		        document.getElementById(`UtilDiff${sSide}Ln`).scrollTop = kText.scrollTop;	// 줄 번호: 세로만
		    }

		    static LoadDiffFile(sSide, kFile, kHandle = null)	// 파일 → 그 창 글 (UTF-8, 앞 BOM 뺌), 이름: 파일 이름 → 제목 (비교 안 함), 저장용: 덮어쓸 곳 (kHandle)·BOM·줄 끝
		    {
		        kFile.text().then((sText) =>
		        {
		            Util.ResetDiffInfo();	// 앞 비교 풂 (회색 빈 줄 뺌)

		            document.getElementById(`UtilDiff${sSide}Text`).value			= sText.replace(/^\uFEFF/, "");
		            Util.mapDiffName[sSide] = kFile.name;
		            Util.mapDiffFile[sSide] = { kHandle, sName: kFile.name, bBom: sText.startsWith("\uFEFF"), sEol: sText.includes("\r\n") ? "\r\n" : "\n" };	// 저장: 같은 BOM·줄 끝

		            Util.RenderLines(sSide);
		        });
		    }

		    static async OpenDiffFile(sSide)	// 찾기 버튼: 파일 고름 (마지막 폴더 기억: 지원 브라우저·localhost·https → 파일 선택 창 id, 그 외: 브라우저 기본 기억)
		    {
		        if (window.showOpenFilePicker == null)	// http 주소 등: 파일 칸
		        {
		            document.getElementById(`UtilDiff${sSide}File`).click();

		            return;
		        }

		        try
		        {
		            const	[kHandle]	= await window.showOpenFilePicker({ id: "HMXUtilDiff" });	// 같은 id: 마지막 폴더에서 열림

		            Util.LoadDiffFile(sSide, await kHandle.getFile(), kHandle);	// 저장: 이 파일에 덮어씀
		        }
		        catch (kError)	// 고르지 않음 (AbortError) 등: 그대로
		        {
		            if (kError?.name !== "AbortError")
		            {
		                console.error(kError);
		            }
		        }
		    }

		    static OnDiffDrop(sSide, kEvent)	// 창에 파일 끌어 놓기: 그 파일 열기 (글 끌어 놓기: 편집 칸 기본)
		    {
		        const	kBody	= document.getElementById(`UtilDiff${sSide}Body`);
		        const	bFile	= [...(kEvent.dataTransfer?.types ?? [])].includes("Files");

		        if (!bFile)
		        {
		            return;
		        }

		        kEvent.preventDefault();	// 브라우저가 파일 열기 대신

		        if (kEvent.type === "dragover")
		        {
		            kBody.classList.add("drop");

		            return;
		        }

		        kBody.classList.remove("drop");

		        if (kEvent.type === "drop" && kEvent.dataTransfer.files.length > 0)
		        {
		            const	kFile	= kEvent.dataTransfer.files[0];
		            const	pHandle	= kEvent.dataTransfer.items?.[0]?.getAsFileSystemHandle?.() ?? Promise.resolve(null);	// 덮어쓸 곳 (지원 브라우저·localhost·https, 놓는 순간만 받음)

		            pHandle.then((kHandle) => Util.LoadDiffFile(sSide, kFile, kHandle?.kind === "file" ? kHandle : null), () => Util.LoadDiffFile(sSide, kFile));
		        }
		    }

		    static ClearDiff()	// 디프 좌·우 창 비움 (글·제목·색, F5 리셋)
		    {
		        Util.ResetDiffInfo();	// 비교 풂

		        for (const sSide of ["Left", "Right"])
		        {
		            document.getElementById(`UtilDiff${sSide}Text`).value			= "";
		            Util.mapDiffName[sSide] = "";

		            Util.RenderLines(sSide);
		        }

		        Util.RunDiff();	// 비교 색 지움 (양쪽 비움)
		    }

		    static OnSplitterDown(kEvent)	// 좌·우 가운데 바 드래그 (디프·소트 공통): 좌 창 폭 (그 묶음 --splitWidth), 우 창: 나머지 (각각 최소 DIFF_SPLIT_MIN px, 운영 탭 세로선과 같음)
		    {
		        const	kSplitter	= kEvent.currentTarget;
		        const	kSplit		= kSplitter.parentElement;	// 좌·우 창 묶음
		        const	nStartX		= kEvent.clientX;	// 드래그 시작 위치
		        const	nWidthStart	= kSplitter.previousElementSibling.getBoundingClientRect().width;	// 좌 창 시작 폭
		        const	nWidthMax	= kSplit.getBoundingClientRect().width - kSplitter.getBoundingClientRect().width - Util.DIFF_SPLIT_MIN;	// 좌 창 최대 폭 (우 창 최소 남김)

		        const funcMove = (kMove) =>	// 끈 만큼 좌 창 폭
		        {
		            kSplit.style.setProperty("--splitWidth", Math.min(nWidthMax, Math.max(Util.DIFF_SPLIT_MIN, nWidthStart + kMove.clientX - nStartX)) + "px");
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
		    }

		    static OnEnter(kEvent, funcRun)	// 1번 창 입력 Enter: 그 줄 실행 (한글 조합 중 제외)
		    {
		        if (kEvent.key === "Enter" && !kEvent.isComposing)
		        {
		            kEvent.preventDefault();

		            funcRun();
		        }
		    }

		    static JsonTokens(sText)	// JSON 글 → 토큰 [{ sType: str·lit·punct·comment, sText, nLine: 앞 줄바꿈 수 }] (틀린 JSON 도 그대로 자름, 주석 //·/* */ 포함)
		    {
		        const	arrToken	= [];
		        let		nPos		= 0;
		        let		nLine		= 0;	// 토큰 앞 줄바꿈 수 (빈 줄 유지)

		        while (nPos < sText.length)
		        {
		            const	sChar	= sText[nPos];
		            let		nEnd	= nPos + 1;
		            let		sType	= "lit";

		            if (sChar === "\n")
		            {
		                nLine++;
		                nPos++;

		                continue;
		            }

		            if (/\s|\uFEFF/.test(sChar))
		            {
		                nPos++;

		                continue;
		            }

		            if (sChar === '"' || sChar === "'")	// 문자열 (\ 다음 글자 건너뜀, 줄 끝: 끊음)
		            {
		                while (nEnd < sText.length && sText[nEnd] !== sChar && sText[nEnd] !== "\n")
		                {
		                    nEnd += sText[nEnd] === "\\" ? 2 : 1;
		                }

		                nEnd	= Math.min(sText.length, nEnd + 1);
		                sType	= "str";
		            }
		            else if (sChar === "/" && sText[nPos + 1] === "/")	// 줄 주석
		            {
		                nEnd	= sText.indexOf("\n", nPos) < 0 ? sText.length : sText.indexOf("\n", nPos);
		                sType	= "comment";
		            }
		            else if (sChar === "/" && sText[nPos + 1] === "*")	// 묶음 주석
		            {
		                nEnd	= sText.indexOf("*/", nPos + 2) < 0 ? sText.length : sText.indexOf("*/", nPos + 2) + 2;
		                sType	= "comment";
		            }
		            else if ("{}[]:,".includes(sChar))
		            {
		                sType = "punct";
		            }
		            else	// 숫자·true·false·null·그 외 글자
		            {
		                while (nEnd < sText.length && !/[\s{}\[\]:,"'\/]/.test(sText[nEnd]))
		                {
		                    nEnd++;
		                }
		            }

		            arrToken.push({ sType, sText: sText.slice(nPos, nEnd), nLine });

		            nLine	= 0;
		            nPos	= nEnd;
		        }

		        return arrToken;
		    }

		    static JsonCount(arrToken, nLine)	// 요소별 개수 요약: 맨 앞 줄 수 + 있는 것만 객체·배열·키·문자열·숫자·불·null (예: 줄 36 · 객체 3 · 배열 1)
		    {
		        const	mapCount	= new Map([["객체", 0], ["배열", 0], ["키", 0], ["문자열", 0], ["숫자", 0], ["불", 0], ["null", 0]]);

		        arrToken.forEach((kToken, nIndex) =>
		        {
		            const	sKind	= kToken.sType === "punct" ? ({ "{": "객체", "[": "배열" })[kToken.sText]
		                            : kToken.sType === "str" ? (arrToken[nIndex + 1]?.sText === ":" ? "키" : "문자열")
		                            : kToken.sType !== "lit" ? undefined
		                            : kToken.sText === "true" || kToken.sText === "false" ? "불"
		                            : kToken.sText === "null" ? "null"
		                            : /^-?\d/.test(kToken.sText) ? "숫자" : undefined;

		            if (sKind != null)
		            {
		                mapCount.set(sKind, mapCount.get(sKind) + 1);
		            }
		        });

		        return [["줄", nLine], ...[...mapCount].filter(([, nCount]) => nCount > 0)].map(([sKind, nCount]) => `${sKind} ${nCount.toLocaleString()}`).join(" · ");
		    }

		    static JsonErrors(arrToken)	// JSON 문법 틀린 토큰 위치 (주석 빼고 차례로 검사, 틀림: 표시 후 이어서 검사 → 틀린 곳 모두, 큰따옴표 글·숫자·true·false·null 만 값, 끝 쉼표: 그 쉼표, 안 닫힘: 여는 괄호)
		    {
		        const	setError	= new Set();
		        const	arrIndex	= arrToken.map((_, nIndex) => nIndex).filter((nIndex) => arrToken[nIndex].sType !== "comment");
		        const	arrStack	= [];	// 열린 괄호 [{ sText, nIndex }]
		        let		sState		= "value";	// value (값: 처음·: 뒤)·item (배열 , 뒤)·valueClose ([ 뒤)·keyClose ({ 뒤)·key (객체 , 뒤)·colon (키 뒤)·next (값 뒤: , 또는 닫음)·end (끝)
		        let		nComma		= -1;	// 마지막 , (끝 쉼표 표시)

		        const funcDone	= () => { sState = arrStack.length > 0 ? "next" : "end"; };	// 값 끝
		        const funcStr	= (sText) => sText.length >= 2 && sText[0] === '"' && sText.at(-1) === '"';
		        const funcLit	= (sText) => /^-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?$/.test(sText) || ["true", "false", "null"].includes(sText);

		        arrIndex.forEach((nIndex, nAt) =>
		        {
		            const	kToken	= arrToken[nIndex];
		            const	sText	= kToken.sText;
		            const	bColon	= arrToken[arrIndex[nAt + 1]]?.sText === ":";	// 다음 : (키 자리)
		            const	kTop	= arrStack.at(-1);

		            if (kToken.sType !== "punct")	// 글·숫자 등
		            {
		                const	bValid	= kToken.sType === "str" ? funcStr(sText) : funcLit(sText);

		                if (sState === "keyClose" || sState === "key")	// 키 자리: 큰따옴표 글만
		                {
		                    if (kToken.sType !== "str" || !bValid)
		                    {
		                        setError.add(nIndex);
		                    }

		                    if (bColon)
		                    {
		                        sState = "colon";
		                    }
		                    else
		                    {
		                        funcDone();
		                    }
		                }
		                else if (sState === "value" || sState === "item" || sState === "valueClose")
		                {
		                    if (!bValid)
		                    {
		                        setError.add(nIndex);
		                    }

		                    funcDone();
		                }
		                else	// : 없음·, 없음·끝 뒤 글
		                {
		                    setError.add(nIndex);

		                    if (kTop?.sText === "{" && bColon)
		                    {
		                        sState = "colon";
		                    }
		                    else
		                    {
		                        funcDone();
		                    }
		                }

		                return;
		            }

		            if (sText === "{" || sText === "[")
		            {
		                if (!["value", "item", "valueClose"].includes(sState))
		                {
		                    setError.add(nIndex);
		                }

		                arrStack.push({ sText, nIndex });

		                sState = sText === "{" ? "keyClose" : "valueClose";

		                return;
		            }

		            if (sText === "}" || sText === "]")
		            {
		                const	sOpen	= sText === "}" ? "{" : "[";

		                if (kTop?.sText === sOpen)
		                {
		                    if (sState === "key" || sState === "item")	// 끝 쉼표
		                    {
		                        setError.add(nComma);
		                    }
		                    else if (sState !== "next" && sState !== (sOpen === "{" ? "keyClose" : "valueClose"))	// 값 없음 (: 뒤 등)
		                    {
		                        setError.add(nIndex);
		                    }

		                    arrStack.pop();
		                    funcDone();

		                    return;
		                }

		                setError.add(nIndex);	// 짝 안 맞음

		                const	nOpen	= arrStack.findLastIndex((kOpen) => kOpen.sText === sOpen);

		                if (nOpen >= 0)	// 안쪽 안 닫힌 괄호: 표시 후 그 짝까지 닫음
		                {
		                    arrStack.splice(nOpen).slice(1).forEach((kOpen) => setError.add(kOpen.nIndex));
		                    funcDone();
		                }

		                return;
		            }

		            if (sText === ",")
		            {
		                if (sState !== "next")
		                {
		                    setError.add(nIndex);
		                }

		                if (kTop != null)
		                {
		                    sState	= kTop.sText === "{" ? "key" : "item";
		                    nComma	= nIndex;
		                }

		                return;
		            }

		            if (sState !== "colon")	// :
		            {
		                setError.add(nIndex);
		            }

		            sState = "value";
		        });

		        arrStack.forEach((kOpen) => setError.add(kOpen.nIndex));	// 안 닫힘: 여는 괄호

		        if (arrIndex.length > 0 && sState !== "end" && sState !== "next")	// 덜 끝남 (: 뒤 값 없음 등): 마지막 토큰
		        {
		            setError.add(arrIndex.at(-1));
		        }

		        return setError;
		    }

		    static FormatJson(sText)	// JSON → 비주얼 스튜디오 기본 형식 { arrLine, mapFold } (2칸 들여쓰기, "키": 값, 객체·객체 든 배열: 줄마다, 값만 든 배열: 한 줄 [ a, b ], 빈 {}·[]: 한 줄, 빈 줄·키 순서·값 글자·주석: 입력 그대로, 틀린 JSON: 토큰 그대로 들여쓰기만)
		    {
		        const	arrToken	= Util.JsonTokens(sText);
		        const	arrClose	= new Array(arrToken.length).fill(-1);	// 여는 괄호 → 짝 닫는 괄호 토큰 위치
		        const	arrOpen		= [];
		        const	arrLine		= [];	// 결과 줄
		        const	mapFold		= new Map();	// 여는 줄 → 닫는 줄 (접기)
		        const	arrFold		= [];	// 열린 여러 줄 괄호의 여는 줄
		        let		sLine		= "";	// 지금 줄
		        let		nDepth		= 0;
		        const		arrAt		= new Array(arrToken.length).fill(0);	// 토큰 → 결과 줄 (틀린 줄 표시)

		        arrToken.forEach((kToken, nIndex) =>
		        {
		            if (kToken.sType !== "punct")
		            {
		                return;
		            }

		            if (kToken.sText === "{" || kToken.sText === "[")
		            {
		                arrOpen.push(nIndex);
		            }
		            else if ((kToken.sText === "}" || kToken.sText === "]") && arrOpen.length > 0)
		            {
		                arrClose[arrOpen.pop()] = nIndex;
		            }
		        });

		        const funcBreak = (nBlank) =>	// 줄 끝냄 + 빈 줄 X개 + 새 줄 들여쓰기
		        {
		            arrLine.push(sLine.trimEnd());

		            for (let nCount = 0; nCount < nBlank; ++nCount)
		            {
		                arrLine.push("");
		            }

		            sLine = "  ".repeat(nDepth);
		        };
		        const funcBlank	= (kToken) => Math.max(0, (kToken?.nLine ?? 1) - 1);	// 토큰 앞 빈 줄 수 (입력 그대로)
		        const funcStart	= () => sLine.trim() === "";	// 줄 처음 (들여쓰기만)
		        const funcAt	= (nFrom, nTo = nFrom) => arrAt.fill(arrLine.length, nFrom, nTo + 1);	// 그 토큰들: 지금 줄

		        for (let nIndex = 0; nIndex < arrToken.length; ++nIndex)
		        {
		            const	kToken	= arrToken[nIndex];
		            const	sToken	= kToken.sText;

		            if (kToken.sType === "comment")	// 주석: 입력에서 줄 처음: 새 줄, 아님: 같은 줄 뒤
		            {
		                if (kToken.nLine > 0 && !funcStart())
		                {
		                    funcBreak(funcBlank(kToken));
		                }
		                else if (!funcStart())
		                {
		                    sLine += " ";
		                }

		                funcAt(nIndex);
		                sLine += sToken;

		                if (sToken.startsWith("//"))
		                {
		                    funcBreak(0);
		                }

		                continue;
		            }

		            if (kToken.sType !== "punct")	// 문자열·숫자 등: 그대로
		            {
		                funcAt(nIndex);
		                sLine += sToken;

		                continue;
		            }

		            if (sToken === "{" || sToken === "[")
		            {
		                const	nClose	= arrClose[nIndex];

		                if (nClose === nIndex + 1)	// 빈 {}·[]
		                {
		                    funcAt(nIndex, nClose);
		                    sLine += sToken + arrToken[nClose].sText;
		                    nIndex = nClose;

		                    continue;
		                }

		                if (sToken === "[" && nClose > 0 && arrToken.slice(nIndex + 1, nClose).every(kItem => kItem.sType !== "comment" && kItem.sText !== "{" && kItem.sText !== "["))	// 값만 든 배열: 한 줄
		                {
		                    funcAt(nIndex, nClose);
		                    sLine += "[ " + arrToken.slice(nIndex + 1, nClose).map(kItem => kItem.sText === "," ? ", " : kItem.sText).join("").trimEnd() + " ]";
		                    nIndex = nClose;

		                    continue;
		                }

		                funcAt(nIndex);
		                sLine += sToken;
		                nDepth++;

		                arrFold.push(arrLine.length);	// 여는 줄 (접기)

		                if (arrToken[nIndex + 1]?.sType === "comment" && arrToken[nIndex + 1].nLine === 0)	// 같은 줄 주석: 그 줄 뒤 그대로
		                {
		                    sLine += " " + arrToken[++nIndex].sText;
		                }

		                funcBreak(funcBlank(arrToken[nIndex + 1]));

		                continue;
		            }

		            if (sToken === "}" || sToken === "]")
		            {
		                nDepth = Math.max(0, nDepth - 1);

		                if (funcStart())
		                {
		                    sLine = "  ".repeat(nDepth);
		                }
		                else
		                {
		                    funcBreak(funcBlank(kToken));
		                }

		                const	nOpenLine	= arrFold.pop();

		                if (nOpenLine != null && arrLine.length > nOpenLine)
		                {
		                    mapFold.set(nOpenLine, arrLine.length);	// 여는 줄 → 닫는 줄
		                }

		                funcAt(nIndex);
		                sLine += sToken;

		                continue;
		            }

		            if (sToken === ",")
		            {
		                funcAt(nIndex);
		                sLine += ",";

		                if (arrToken[nIndex + 1]?.sType === "comment" && arrToken[nIndex + 1].nLine === 0)	// 같은 줄 주석: 그 줄 뒤 그대로
		                {
		                    sLine += " " + arrToken[++nIndex].sText;
		                }

		                funcBreak(funcBlank(arrToken[nIndex + 1]));

		                continue;
		            }

		            funcAt(nIndex);
		            sLine += ": ";	// :
		        }

		        if (!funcStart() || arrLine.length === 0)
		        {
		            arrLine.push(sLine.trimEnd());
		        }

		        return { arrLine, mapFold, sCount: Util.JsonCount(arrToken, arrLine.length), setError: new Set([...Util.JsonErrors(arrToken)].map((nIndex) => arrAt[nIndex])) };	// setError: 틀린 줄
		    }

		    static RenderSortLines()	// 소트 좌 창: 줄 번호 (줄 수 바뀜만 다시) + 제목 (요소별 개수 요약)
		    {
		        const	kText	= document.getElementById("UtilSortLeftText");
		        const	kLn		= document.getElementById("UtilSortLeftLn");
		        const	nCount	= kText.value.split("\n").length;	// 줄 수 (빈 칸: 1)

		        if (kLn.dataset.count !== String(nCount))
		        {
		            kLn.textContent		= Array.from({ length: nCount }, (_, nIndex) => nIndex + 1).join("\n");
		            kLn.dataset.count	= String(nCount);
		        }

		        document.getElementById("UtilSortLeftTitle").textContent = `기준 · ${Util.JsonCount(Util.JsonTokens(kText.value), kText.value === "" ? 0 : nCount)}${Util.sSortName === "" ? "" : " - " + Util.sSortName}`;	// 기준 · 줄 수 + 요소별 개수 - 파일 이름 (비움: 줄 0)
		        document.getElementById("UtilSortLeftSave").disabled = kText.value === "" && Util.kSortFile == null;	// 저장: 글 있음·연 파일 (파일 없음: 저장할 폴더·이름 고름)

		        Util.SyncSort();
		        Util.RenderSelBar(kText, document.getElementById("UtilSortLeftBack"));
		    }

		    static SyncSort()	// 소트 좌 창 스크롤 → 줄 번호·고른 빈 줄 같은 자리
		    {
		        document.getElementById("UtilSortLeftBack").scrollTop	= document.getElementById("UtilSortLeftText").scrollTop;
		        document.getElementById("UtilSortLeftBack").scrollLeft	= document.getElementById("UtilSortLeftText").scrollLeft;
		        document.getElementById("UtilSortLeftLn").scrollTop = document.getElementById("UtilSortLeftText").scrollTop;
		    }

		    static RunSort()	// 소트 실행: 좌 창 JSON → 우 창 비주얼 스튜디오 기본 형식 (접기 다시: 모두 펼침, 빈 입력: 우 창 비움)
		    {
		        const	sText	= document.getElementById("UtilSortLeftText").value;
		        const	kResult	= sText.trim() === "" ? { arrLine: [], mapFold: new Map(), sCount: Util.JsonCount([], 0), setError: new Set() } : Util.FormatJson(sText);

		        Util.arrSortLine	= kResult.arrLine;
		        Util.mapSortFold	= kResult.mapFold;
		        Util.sSortCount		= kResult.sCount;
		        Util.setSortError	= kResult.setError;
		        Util.setSortFold	= new Set();
		        Util.arrSortHide	= new Array(kResult.arrLine.length).fill(0);

		        Util.RenderSort();
		    }

		    static RenderSort()	// 소트 우 창: 줄 번호 · 접기 표시 (여는 줄 ▾) · 줄 + 제목 (요소별 개수 요약)
		    {
		        const	kFragment	= document.createDocumentFragment();

		        Util.arrSortLine.forEach((sLine, nIndex) =>
		        {
		            const	kRow	= document.createElement("div");
		            const	kNum	= document.createElement("span");
		            const	kFold	= document.createElement("span");
		            const	kText	= document.createElement("span");

		            kRow.className		= "jl";
		            kRow.dataset.index	= String(nIndex);
		            kNum.className		= "jn";
		            kNum.textContent	= String(nIndex + 1);
		            kFold.className		= "jf";
		            kText.className		= "jt";
		            kText.textContent	= sLine;

		            if (sLine === "")	// 빈 줄: 줄바꿈 (복사해도 빈 줄 유지)
		            {
		                kText.append(document.createElement("br"));
		            }

		            if (Util.mapSortFold.has(nIndex))
		            {
		                kFold.textContent	= "▾";
		                kFold.title			= "접기";
		            }

		            if (Util.setSortError.has(nIndex))	// JSON 틀린 줄: 연한 빨강 (디프 바뀐 줄 하늘색처럼)
		            {
		                kRow.classList.add("err");
		            }

		            kRow.append(kNum, kFold, kText);
		            kFragment.append(kRow);
		        });

		        document.getElementById("UtilSortRightBody").replaceChildren(kFragment);
		        document.getElementById("UtilSortRightTitle").textContent = Util.sSortCount === "" ? "대상" : `대상 · ${Util.sSortCount}`;	// 대상 · 줄 수 + 요소별 개수
		        document.getElementById("UtilSortUnfold").disabled			= Util.mapSortFold.size === 0;	// 접을 곳 없음: 펴기·접기 꺼짐
		        document.getElementById("UtilSortFold").disabled				= Util.mapSortFold.size === 0;
		    }

		    static OnSortFold(kEvent)	// 우 창 접기 표시·접힌 … 누름: 그 { }·[ ] 접기·펴기
		    {
		        const	kRow	= kEvent.target.closest(".jf, .jdots")?.closest(".jl");

		        if (kRow != null)
		        {
		            const	nStart	= Number(kRow.dataset.index);

		            Util.FoldSort(nStart, !Util.setSortFold.has(nStart));
		        }
		    }

		    static FoldSort(nStart, bFold)	// 여는 줄 ~ 닫는 줄 접기 (여는 줄: "키": {…}, 로 한 줄)·펴기 (안쪽 접힘: 그대로)
		    {
		        const	nEnd	= Util.mapSortFold.get(nStart);
		        const	arrRow	= document.getElementById("UtilSortRightBody").children;

		        if (nEnd == null || bFold === Util.setSortFold.has(nStart) || arrRow[nStart] == null)
		        {
		            return;
		        }

		        let	bHideError	= false;	// 접는 안쪽 틀린 줄

		        for (let nIndex = nStart + 1; nIndex <= nEnd; ++nIndex)
		        {
		            Util.arrSortHide[nIndex] += bFold ? 1 : -1;
		            arrRow[nIndex].hidden = Util.arrSortHide[nIndex] > 0;
		            bHideError ||= Util.setSortError.has(nIndex);
		        }

		        const	kRow	= arrRow[nStart];
		        const	kFold	= kRow.querySelector(".jf");

		        kFold.textContent	= bFold ? "▸" : "▾";
		        kFold.title			= bFold ? "펼치기" : "접기";

		        kRow.classList.toggle("err", Util.setSortError.has(nStart) || (bFold && bHideError));	// 접힘: 안쪽 틀린 줄도 여는 줄 빨강

		        kRow.querySelector(".jtail")?.remove();

		        if (bFold)	// 접힘: … + 닫는 줄 (예: },)
		        {
		            const	kTail	= document.createElement("span");
		            const	kDots	= document.createElement("span");

		            kTail.className		= "jtail";
		            kDots.className		= "jdots";
		            kDots.textContent	= "…";
		            kDots.title			= "펼치기";

		            kTail.append(kDots, Util.arrSortLine[nEnd].trim());
		            kRow.querySelector(".jt").append(kTail);

		            Util.setSortFold.add(nStart);
		        }
		        else
		        {
		            Util.setSortFold.delete(nStart);
		        }
		    }

		    static FoldSortAll(bFold)	// 펴기 (bFold false): 접은 곳 모두 펼침, 접기 (true): 최상위 { }·[ ] 빼고 모두 접음 (안쪽도 접힘: 바깥 펼쳐도 안쪽 그대로)
		    {
		        if (!bFold)
		        {
		            for (const nStart of [...Util.setSortFold])
		            {
		                Util.FoldSort(nStart, false);
		            }

		            return;
		        }

		        const	arrEnd	= [];	// 바깥 접기 닫는 줄 (깊이)

		        for (const [nStart, nEnd] of [...Util.mapSortFold].sort((kA, kB) => kA[0] - kB[0]))
		        {
		            while (arrEnd.length > 0 && arrEnd.at(-1) < nStart)	// 끝난 바깥: 뺌
		            {
		                arrEnd.pop();
		            }

		            if (arrEnd.length > 0)	// 최상위 아님: 접음
		            {
		                Util.FoldSort(nStart, true);
		            }

		            arrEnd.push(nEnd);
		        }
		    }

		    static OnSortRun()	// 소트 실행 버튼: 지금 입력 기억 (지금 위치와 같음: 안 넣음, 과거에서 실행: 그 뒤 기억 버림, 넘음: 가장 오래된 것 버림) → 펼침
		    {
		        const	sText	= document.getElementById("UtilSortLeftText").value;

		        if (Util.arrSortBack[Util.nSortPos] !== sText)
		        {
		            Util.arrSortBack.length = Util.nSortPos + 1;	// 지금 위치 뒤 (→ 쪽) 기억 버림
		            Util.arrSortBack.push(sText);

		            if (Util.arrSortBack.length > Util.SORT_BACK_MAX)
		            {
		                Util.arrSortBack.shift();
		            }

		            Util.nSortPos = Util.arrSortBack.length - 1;
		        }

		        Util.ShowSortBack();
		        Util.RunSort();
		    }

		    static GoSort(nStep)	// 소트 네비게이션 ← (-1: 과거)·→ (+1: 미래)·가운데 숫자 (그 위치까지): 그 실행 입력 그대로 → 펼침 (기억 지우지 않음: 위치만, 끝: 무시)
		    {
		        const	nPos	= Util.nSortPos + nStep;

		        if (nPos < 0 || nPos >= Util.arrSortBack.length)
		        {
		            return;
		        }

		        Util.nSortPos = nPos;

		        document.getElementById("UtilSortLeftText").value = Util.arrSortBack[nPos];

		        Util.RenderSortLines();
		        Util.ShowSortBack();
		        Util.RunSort();
		    }

		    static ShowSortBack()	// 소트 네비게이션 ← N →: 이전·다음 실행 있음 → 켬 (없음: 꺼짐), 가운데 지금 위치 (0: 마지막 실행, 과거: -1·-2…, 없음: 0)
		    {
		        const	nLast	= Util.arrSortBack.length - 1;	// 마지막 실행 위치

		        document.getElementById("UtilSortRunPrev").disabled		= Util.nSortPos <= 0;
		        document.getElementById("UtilSortRunNext").disabled		= Util.nSortPos < 0 || Util.nSortPos >= nLast;
		        document.getElementById("UtilSortRunDepth").textContent	= String(Util.nSortPos - nLast);
		    }

		    static LoadSortFile(kFile, kHandle = null)	// 파일 → 소트 좌 창 글 (UTF-8, 앞 BOM 뺌), 이름 → 제목 (자동 펼침 안 함), 저장용: 덮어쓸 곳 (kHandle)·BOM·줄 끝
		    {
		        kFile.text().then((sText) =>
		        {
		            document.getElementById("UtilSortLeftText").value = sText.replace(/^\uFEFF/, "");

		            Util.sSortName	= kFile.name;
		            Util.kSortFile	= { kHandle, sName: kFile.name, bBom: sText.startsWith("\uFEFF"), sEol: sText.includes("\r\n") ? "\r\n" : "\n" };	// 저장: 같은 BOM·줄 끝

		            Util.RenderSortLines();
		        });
		    }

		    static async OpenSortFile()	// 소트 좌 찾기 버튼: 파일 고름 (마지막 폴더 기억: 지원 브라우저·localhost·https → 파일 선택 창 id, 그 외: 파일 칸)
		    {
		        if (window.showOpenFilePicker == null)	// http 주소 등: 파일 칸
		        {
		            document.getElementById("UtilSortLeftFile").click();

		            return;
		        }

		        try
		        {
		            const	[kHandle]	= await window.showOpenFilePicker({ id: "HMXUtilSort" });	// 같은 id: 마지막 폴더에서 열림

		            Util.LoadSortFile(await kHandle.getFile(), kHandle);	// 저장: 이 파일에 덮어씀
		        }
		        catch (kError)	// 고르지 않음 (AbortError) 등: 그대로
		        {
		            if (kError?.name !== "AbortError")
		            {
		                console.error(kError);
		            }
		        }
		    }

		    static OnSortDrop(kEvent)	// 소트 좌 창에 파일 끌어 놓기: 그 파일 열기 (글 끌어 놓기: 편집 칸 기본)
		    {
		        const	kBody	= document.getElementById("UtilSortLeftBody");

		        if (![...(kEvent.dataTransfer?.types ?? [])].includes("Files"))
		        {
		            return;
		        }

		        kEvent.preventDefault();	// 브라우저가 파일 열기 대신

		        if (kEvent.type === "dragover")
		        {
		            kBody.classList.add("drop");

		            return;
		        }

		        kBody.classList.remove("drop");

		        if (kEvent.type === "drop" && kEvent.dataTransfer.files.length > 0)
		        {
		            const	kFile	= kEvent.dataTransfer.files[0];
		            const	pHandle	= kEvent.dataTransfer.items?.[0]?.getAsFileSystemHandle?.() ?? Promise.resolve(null);	// 덮어쓸 곳 (지원 브라우저·localhost·https, 놓는 순간만 받음)

		            pHandle.then((kHandle) => Util.LoadSortFile(kFile, kHandle?.kind === "file" ? kHandle : null), () => Util.LoadSortFile(kFile));
		        }
		    }

		    static async SaveSortFile()	// 소트 좌 저장 (입력 글): 연 파일에 덮어씀 (같은 BOM·줄 끝, 덮어쓸 곳 없음: 저장 창), 파일 없음: 저장 창 → 고른 파일 비우고 새로 씀 (기본 기준.json, UTF-8·CRLF, 저장 뒤: 그 파일 연 것과 같음), 저장 창 없음 (http 주소): 다운
		    {
		        const	sValue	= document.getElementById("UtilSortLeftText").value;

		        if (sValue === "" && Util.kSortFile == null)	// 저장할 글 없음
		        {
		            return;
		        }

		        const	kFile	= Util.kSortFile ?? { kHandle: null, sName: Util.sSortName || "기준.json", bBom: false, sEol: "\r\n" };	// 연 파일: 그 파일, 그 외: 새 파일 (저장할 폴더·이름 고름)
		        const	sText	= (kFile.bBom ? "\uFEFF" : "") + sValue.replace(/\n/g, kFile.sEol);
		        let		sName	= kFile.sName;	// 저장한 이름

		        try
		        {
		            let	kHandle	= kFile.kHandle;

		            if (kHandle == null && window.showSaveFilePicker != null)	// 덮어쓸 곳 없음: 저장 창 (폴더·이름 고름, 마지막 폴더 기억)
		            {
		                kHandle = await window.showSaveFilePicker({ id: "HMXUtilSort", suggestedName: kFile.sName });
		            }

		            if (kHandle != null && (await kHandle.requestPermission?.({ mode: "readwrite" }) ?? "granted") === "granted")
		            {
		                const	kWrite	= await kHandle.createWritable();	// 파일 비우고 새로 씀 (앞 글 안 남김)

		                await kWrite.write(sText);
		                await kWrite.close();

		                sName			= kHandle.name;
		                Util.kSortFile	= { ...kFile, kHandle, sName };	// 저장한 파일 연 것과 같음 (다음 저장: 덮어씀)
		                Util.sSortName	= sName;

		                Util.RenderSortLines();
		            }
		            else	// http 주소 등 저장 창·파일 쓰기 없음: 그 이름 다운 (브라우저 다운 폴더)
		            {
		                const	kLink	= document.createElement("a");

		                kLink.href		= URL.createObjectURL(new Blob([sText], { type: "application/json" }));
		                kLink.download	= sName;

		                kLink.click();

		                setTimeout(() => URL.revokeObjectURL(kLink.href), 1000);
		            }

		            Process.Toast(`저장: ${sName}`, "ok");
		        }
		        catch (kError)	// 저장 창 닫음 (AbortError): 그대로, 그 밖: 실패 알림
		        {
		            if (kError?.name !== "AbortError")
		            {
		                console.error(kError);
		                Process.Toast(`저장 실패: ${sName} (${ErrorText(kError)})`, "fail");
		            }
		        }
		    }

		    static ClearSort()	// 소트 좌·우 창 비움 (F5 리셋, 파일 연결 끊음)
		    {
		        Util.kSortFile	= null;
		        Util.sSortName	= "";

		        document.getElementById("UtilSortLeftText").value = "";

		        Util.RenderSortLines();
		        Util.RunSort();
		    }

		    static FitSortHeight()	// 소트 좌·우 창 높이: 브라우저 창 아래 끝에 맞춤 (소트 보일 때만)
		    {
		        if (!kUtilPanel.hidden && Util.sCur === "sort")
		        {
		            FitHeight(document.getElementById("UtilSortLeftBody"), document.getElementById("UtilSortLeft"));
		            FitHeight(document.getElementById("UtilSortRightBody"), document.getElementById("UtilSortRight"));
		        }
		    }

		    static GetSnapshot()	// 지금 상태 스냅샷 (Ctrl·Alt+클릭 새 창·새 탭 복사, 공통): 하위 탭·타임 입력·디프 좌·우 글·이름·비교·지금 차이·스크롤·네비게이션 기억·좌 창 폭·소트 입력·펼침·접은 곳
		    {
		        const	kLeft	= document.getElementById("UtilDiffLeftText");
		        const	kRight	= document.getElementById("UtilDiffRightText");

		        return {
		            sCur: Util.sCur,
		            arrKst: [Util.kDate.value, Util.kHour.value, Util.kMinute.value, Util.kSecond.value], sUnix: Util.kUnix.value, bKstAuto: Util.bKstAuto, bUnixAuto: Util.bUnixAuto,
		            sLeft: Util.GetDiffText("Left"), sRight: Util.GetDiffText("Right"), sLeftName: Util.mapDiffName.Left, sRightName: Util.mapDiffName.Right,
		            bDiff: Util.sDiffState !== "", nBlock: Util.nDiffBlock, nLeftTop: kLeft.scrollTop, nRightTop: kRight.scrollTop,
		            arrDiffBack: Util.arrDiffBack, nDiffPos: Util.nDiffPos, sWidth: Util.kDiffSplit.style.getPropertyValue("--splitWidth"),
		            sSortText: document.getElementById("UtilSortLeftText").value, sSortName: Util.sSortName, bSort: Util.arrSortLine.length > 0, arrSortFold: [...Util.setSortFold], arrSortBack: Util.arrSortBack, nSortPos: Util.nSortPos,
		            sSortWidth: document.getElementById("UtilSortSplit").style.getPropertyValue("--splitWidth"), nSortLeftTop: document.getElementById("UtilSortLeftText").scrollTop, nSortRightTop: document.getElementById("UtilSortRightBody").scrollTop,
		        };
		    }

		    static SetSnapshot(kSnap)	// 연 창 스냅샷 그대로 (새 창 첫 화면, 유틸 탭 연 뒤): 타임 입력·디프 글·이름·네비게이션 기억·좌 창 폭 → 비교했음: 다시 비교 + 그 차이·스크롤
		    {
		        const	kLeft	= document.getElementById("UtilDiffLeftText");
		        const	kRight	= document.getElementById("UtilDiffRightText");

		        [Util.kDate.value, Util.kHour.value, Util.kMinute.value, Util.kSecond.value]	= kSnap.arrKst;
		        Util.kUnix.value															= kSnap.sUnix;
		        Util.bKstAuto																= kSnap.bKstAuto;
		        Util.bUnixAuto																= kSnap.bUnixAuto;

		        Util.ResetDiffInfo();	// 앞 비교 풂

		        kLeft.value				= kSnap.sLeft;
		        kRight.value			= kSnap.sRight;
		        Util.mapDiffName.Left	= kSnap.sLeftName;
		        Util.mapDiffName.Right	= kSnap.sRightName;
		        Util.arrDiffBack		= kSnap.arrDiffBack;
		        Util.nDiffPos			= kSnap.nDiffPos;

		        if (kSnap.sWidth !== "")
		        {
		            Util.kDiffSplit.style.setProperty("--splitWidth", kSnap.sWidth);
		        }

		        Util.RenderLines("Left");
		        Util.RenderLines("Right");
		        Util.ShowDiffBack();
		        Util.FitDiffHeight();

		        if (kSnap.bDiff)
		        {
		            Util.RunDiff();
		            Util.GoBlock(kSnap.nBlock);
		        }

		        kLeft.scrollTop		= kSnap.nLeftTop;	// 스크롤 (비교함: 빈 줄 넣은 자리)
		        kRight.scrollTop	= kSnap.nRightTop;

		        Util.SyncBack("Left");
		        Util.SyncBack("Right");

		        document.getElementById("UtilSortLeftText").value	= kSnap.sSortText;	// 소트: 입력·파일 이름·네비게이션 기억·좌 창 폭 → 펼쳤음: 다시 펼침 + 접은 곳·스크롤
		        Util.sSortName										= kSnap.sSortName ?? "";	// 덮어쓸 곳: 안 옮김 (저장: 저장 창)
		        Util.arrSortBack									= kSnap.arrSortBack;
		        Util.nSortPos										= kSnap.nSortPos;

		        if (kSnap.sSortWidth !== "")
		        {
		            document.getElementById("UtilSortSplit").style.setProperty("--splitWidth", kSnap.sSortWidth);
		        }

		        Util.RenderSortLines();
		        Util.ShowSortBack();
		        Util.FitSortHeight();

		        if (kSnap.bSort)
		        {
		            Util.RunSort();

		            for (const nStart of kSnap.arrSortFold)
		            {
		                Util.FoldSort(nStart, true);
		            }
		        }

		        document.getElementById("UtilSortLeftText").scrollTop	= kSnap.nSortLeftTop;
		        document.getElementById("UtilSortRightBody").scrollTop	= kSnap.nSortRightTop;

		        Util.SyncSort();
		    }

		    static Init()	// 이벤트 연결 + 시·분·초 목록 + 빈 결과 (페이지 시작 1번)
		    {
		        for (let nValue = 0; nValue < 60; ++nValue)	// 시 00~23, 분·초 00~59
		        {
		            const	sValue	= Util.Pad(nValue);

		            if (nValue < 24)
		            {
		                Util.kHour.add(new Option(sValue, sValue));
		            }

		            Util.kMinute.add(new Option(sValue, sValue));
		            Util.kSecond.add(new Option(sValue, sValue));
		        }

		        Util.kTabs.addEventListener("click", (kEvent) =>
		        {
		            const	kButton	= kEvent.target.closest("button[data-util]");

		            if (kButton != null)
		            {
		                Util.Select(kButton.dataset.util);
		            }
		        });

		        for (const kInput of [Util.kDate, Util.kHour, Util.kMinute, Util.kSecond])	// 고침: 그 값으로 실행 (현재 시간 안 넣음)
		        {
		            kInput.addEventListener("input", () => { Util.bKstAuto = false; });
		            kInput.addEventListener("change", Util.SyncUnix);	// 입력 끝: Unix 칸 같이
		        }

		        Util.kUnix.addEventListener("input", () =>	// 숫자만 UNIX_DIGIT 자리 (넘는 자리·글자: 입력 안 됨)
		        {
		            const	sUnix	= Util.kUnix.value.replace(/\D/g, "").slice(0, Util.UNIX_DIGIT);

		            if (Util.kUnix.value !== sUnix)
		            {
		                Util.kUnix.value = sUnix;
		            }

		            Util.bUnixAuto = false;
		        });
		        Util.kDate.addEventListener("focus", () => { Util.sDateLast = Util.kDate.value; });
		        Util.kDate.addEventListener("change", () =>	// 날짜 범위 밖 (Unix 0 ~ UNIX_DIGIT 자리): 앞 값으로 되돌림
		        {
		            if (Util.kDate.value !== "" && (Util.kDate.value < Util.kDate.min || Util.kDate.value > Util.kDate.max))
		            {
		                Util.kDate.value = Util.sDateLast;
		            }

		            Util.sDateLast = Util.kDate.value;
		        });
		        Util.kUnix.addEventListener("change", Util.SyncKst);	// 입력 끝 (포커스 나감): KST 칸 같이

		        document.getElementById("UtilNowFill").addEventListener("click", Util.OnNowFill);	// Time 새로 고침: KST·Unix 칸 현재 시간
		        document.getElementById("UtilUnixRun").addEventListener("click", Util.RunUnix);
		        document.querySelector("#UtilKstRow label").addEventListener("keydown", (kEvent) => Util.OnEnter(kEvent, Util.RunKst));	// KST·날짜·시·분·초 칸 (실행 버튼 Enter: 버튼 그대로)
		        Util.kUnix.addEventListener("keydown", (kEvent) => Util.OnEnter(kEvent, Util.RunUnix));
		        Util.kBody.addEventListener("click", Util.OnTimeClick);	// Ac. 삭제
		        Util.kScroll.addEventListener("dragstart", Util.OnTimeDragStart);	// 결과 줄 끌기: 순서 바꿈
		        Util.kScroll.addEventListener("dragend", Util.OnTimeDragEnd);
		        Util.kScroll.addEventListener("dragover", Util.OnTimeDragOver);	// 놓을 곳: 결과 상자 (밖으로: 표시 지움)
		        Util.kScroll.addEventListener("drop", Util.OnTimeDrop);
		        Util.kScroll.addEventListener("dragleave", (kEvent) =>
		        {
		            if (!Util.kScroll.contains(kEvent.relatedTarget))
		            {
		                Util.ClearTimeDrop();
		            }
		        });
		        window.addEventListener("resize", Util.FitTimeHeight);	// 브라우저 창 크기: 2번 창 높이 다시
		        window.addEventListener("resize", Util.FitDiffHeight);	// 디프 좌·우 창 높이 다시
		        Util.kDiffSplitter.addEventListener("pointerdown", Util.OnSplitterDown);	// 디프 가운데 바 드래그

		        document.getElementById("UtilDiffRun").addEventListener("click", Util.OnDiffRun);	// 디프 실행: 입력 기억 → 좌·우 비교
		        document.getElementById("UtilDiffRunPrev").addEventListener("click", () => Util.GoDiff(-1));	// 디프 네비게이션 ←
		        document.getElementById("UtilDiffRunNext").addEventListener("click", () => Util.GoDiff(1));	// 디프 네비게이션 →
		        document.getElementById("UtilDiffRunDepth").addEventListener("click", (kEvent) => EditDepth(kEvent.currentTarget, Util.arrDiffBack.length, Util.nDiffPos, (nStep) => Util.GoDiff(nStep), 1 - Util.arrDiffBack.length));	// 가운데 위치 누름: 숫자 입력 (마지막 0·과거 -1…) → 그 실행으로
		        document.getElementById("UtilDiffPrev").addEventListener("click", () => Util.GoBlock(Util.nDiffBlock - 1));	// 이전 차이
		        document.getElementById("UtilDiffNext").addEventListener("click", () => Util.GoBlock(Util.nDiffBlock + 1));	// 다음 차이
		        document.addEventListener("keydown", (kEvent) =>	// 디프 보임: Alt+↑·↓ 이전·다음 차이 (DataCompare 와 같음)
		        {
		            if (kEvent.altKey && (kEvent.key === "ArrowUp" || kEvent.key === "ArrowDown") && document.getElementById("UtilDiffTool").offsetParent !== null)
		            {
		                kEvent.preventDefault();

		                Util.GoBlock(Util.nDiffBlock + (kEvent.key === "ArrowUp" ? -1 : 1));
		            }
		        });

		        for (const sSide of ["Left", "Right"])	// 디프: 찾기·파일 끌어 놓기·편집 (Ctrl+A·C·X·V 등, 자동 비교 안 함)
		        {
		            const	kText	= document.getElementById(`UtilDiff${sSide}Text`);
		            const	kFile	= document.getElementById(`UtilDiff${sSide}File`);
		            const	kBody	= document.getElementById(`UtilDiff${sSide}Body`);

		            document.getElementById(`UtilDiff${sSide}Open`).addEventListener("click", () => Util.OpenDiffFile(sSide));
		            document.getElementById(`UtilDiff${sSide}Save`).addEventListener("click", () => Util.SaveDiffFile(sSide));	// 저장: 연 파일에 → 다시 비교
		            kFile.addEventListener("change", () =>	// 파일 칸 (http 주소)
		            {
		                if (kFile.files?.[0] != null)
		                {
		                    Util.LoadDiffFile(sSide, kFile.files[0]);
		                }

		                kFile.value = "";	// 같은 파일 다시 골라도 읽음
		            });
		            kText.addEventListener("paste", () => Util.KeepDiffName(sSide, "붙여넣기", "복사"));	// 붙여넣기: 이름 복사 (연 파일: 그대로)
		            kText.addEventListener("beforeinput", (kEvent) => Util.OnDiffBeforeInput(sSide, kEvent));	// 모두 고름 → 지움·붙여넣기: 비교 풂
		            kText.addEventListener("input", (kEvent) => Util.OnDiffInput(sSide, kEvent));	// 고침: 기본 편집 그대로 + 비교 중 색 맞춤
		            kText.addEventListener("copy", (kEvent) => Util.OnDiffCopy(sSide, kEvent));	// 복사·잘라내기: 회색 빈 줄 뺌
		            kText.addEventListener("cut", (kEvent) => Util.OnDiffCopy(sSide, kEvent));
		            kText.addEventListener("scroll", () => Util.OnDiffScroll(sSide));	// 줄 뒤 색·줄 번호 + 비교 중: 좌·우 세로 같이

		            Util.RenderLines(sSide);	// 처음: 1

		            for (const sType of ["dragover", "dragleave", "drop"])
		            {
		                kBody.addEventListener(sType, (kEvent) => Util.OnDiffDrop(sSide, kEvent));
		            }
		        }

		        document.addEventListener("selectionchange", Util.OnSelBar);	// 고른 빈 줄 표시 (디프 좌·우·소트 좌)

		        const	kSortText	= document.getElementById("UtilSortLeftText");	// 소트 입력 (붙여넣기·편집, 자동 펼침 안 함: 실행 버튼)

		        document.getElementById("UtilSortRun").addEventListener("click", Util.OnSortRun);	// 소트 실행: 입력 기억 → 펼침
		        document.getElementById("UtilSortRunPrev").addEventListener("click", () => Util.GoSort(-1));	// 소트 네비게이션 ←
		        document.getElementById("UtilSortRunNext").addEventListener("click", () => Util.GoSort(1));	// 소트 네비게이션 →
		        document.getElementById("UtilSortRunDepth").addEventListener("click", (kEvent) => EditDepth(kEvent.currentTarget, Util.arrSortBack.length, Util.nSortPos, (nStep) => Util.GoSort(nStep), 1 - Util.arrSortBack.length));	// 가운데 위치 누름: 숫자 입력 (마지막 0·과거 -1…) → 그 실행으로
		        document.getElementById("UtilSortUnfold").addEventListener("click", () => Util.FoldSortAll(false));	// 펴기: 모두 펼침
		        document.getElementById("UtilSortFold").addEventListener("click", () => Util.FoldSortAll(true));	// 접기: 최상위 빼고 모두 접음
		        document.getElementById("UtilSortRightBody").addEventListener("click", Util.OnSortFold);	// 우 창 ▾·…: 접기·펴기
		        document.getElementById("UtilSortSplitter").addEventListener("pointerdown", Util.OnSplitterDown);	// 소트 가운데 바 드래그
		        kSortText.addEventListener("input", () =>	// 고침: 줄 번호·제목 (다 지움: 파일 연결 끊음 → 새로 시작 + 우 창 비움, Ctrl+A + Ctrl+V: 유지)
		        {
		            if (kSortText.value === "")	// 다 지움: 파일 연결 끊음 + 우 창도 비움
		            {
		                Util.kSortFile	= null;
		                Util.sSortName	= "";

		                Util.RunSort();
		            }

		            if (Util.nSortFrame === 0)	// 줄 번호·제목 (JSON 전체 셈): 한 화면 틀에 1번
		            {
		                Util.nSortFrame = requestAnimationFrame(() =>
		                {
		                    Util.nSortFrame = 0;

		                    Util.RenderSortLines();
		                });
		            }
		        });
		        document.getElementById("UtilSortLeftOpen").addEventListener("click", Util.OpenSortFile);	// 좌 찾기: 파일 열기
		        document.getElementById("UtilSortLeftSave").addEventListener("click", Util.SaveSortFile);	// 좌 저장: 입력 글
		        document.getElementById("UtilSortLeftFile").addEventListener("change", (kEvent) =>	// 파일 칸 (http 주소)
		        {
		            if (kEvent.currentTarget.files?.[0] != null)
		            {
		                Util.LoadSortFile(kEvent.currentTarget.files[0]);
		            }

		            kEvent.currentTarget.value = "";	// 같은 파일 다시 골라도 읽음
		        });

		        for (const sType of ["dragover", "dragleave", "drop"])	// 좌 창 파일 끌어 놓기: 열기
		        {
		            document.getElementById("UtilSortLeftBody").addEventListener(sType, Util.OnSortDrop);
		        }

		        kSortText.addEventListener("scroll", Util.SyncSort);
		        window.addEventListener("resize", Util.FitSortHeight);	// 소트 좌·우 창 높이 다시

		        Util.RenderSortLines();
		        Util.RunSort();

		        Util.LoadTime();	// 저장한 타임 결과
		        Util.RenderTime();
		        Util.TickNow();	// Time 시계
		    }
		}
		""";
	}
}

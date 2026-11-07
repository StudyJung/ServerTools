class TabSearch	// 서치 탭: 로그 검색 (여러 머신 동시)·외부 API 서치·파일 보기·진행·중지 API + 보기 새 창 (페이지 쪽: SearchHTML)
{
	private const int	m_nKeyMax			= 200;	// 검색어 최대 글자
	private const int	m_nSearchMax		= 1000000;	// 머신당 검색 결과 최대 건수 상한 (= 페이지 MACHINE_LIMIT, 요청 nMax: 1 ~ 이 값)
	private const int	m_nSearchDefault	= 1000;	// 머신당 검색 결과 최대 건수 기본값 (요청 nMax 없음, = 페이지 MACHINE_DEFAULT)
	private const int	m_nSearchLimitSec	= 60;	// 서치 제한 시간 기본 (초, 요청 nLimit 없음, 클라 검색 마감, 페이지 설정 기본과 같음)
	private const int	m_nSearchLimitMax	= 3600;	// 서치 제한 시간 최대 (초)
	private const int	m_nSearchWaitSec	= 30;	// 검색 결과 대기 여유 (초, 제한 시간 + 이 값)
	private const int	m_nSearchConcurrent	= 2;	// 동시 서치 (전체, 세션마다 1건, 클라 동시 처리 수와 같음)
	private const int	m_nViewConcurrent	= 4;	// 동시 파일 보기 (전체, 세션마다 1건)
	private const int	m_nViewCount		= 500;	// 보기 기본 줄 수 (요청 nCount 없음)
	private const int	m_nViewCountMax		= 2000;	// 보기 최대 줄 수
	private const int	m_nExternalHour		= 1;		// 외부 API 서치 기본 기간 (시간, 시작 없음: 끝 시간만)

	private static readonly Gate		m_kGate		= new Gate(m_nSearchConcurrent);	// 서치 자리
	private static readonly Gate		m_kViewGate	= new Gate(m_nViewConcurrent);		// 파일 보기 자리
	private static readonly SavedList	m_kSaved	= new SavedList("aSearch", "저장 서치", "검색어", "arrSearch");	// 저장 서치 (설정 aSearch → Search.json)

	private readonly TCPServer		m_kListener;		// Client TCP 수신 (Main 이 넘김)

	public TabSearch(TCPServer kListener)
	{
		m_kListener = kListener;
	}

	public void MapRoute(WebApplication WebApp, bool bSearch)	// 라우트 등록 (서치 탭, bSearch false: 서치 탭 꺼짐 → 파일 보기만 (파일 탭 공용))
	{
		WebApp.MapGet("/view", OnViewPage);	// 파일 보기 새 창 (보기 모드, 파일명·보기 제목 Ctrl·Alt+클릭)
		WebApp.MapGet("/api/view", OnView);		// 파일 보기 (머신 1대)

		if (!bSearch)
		{
			return;
		}

		WebApp.MapPost("/api/search", OnSearch);	// 로그 검색 (여러 머신 동시, 본문 JSON)
		m_kSaved.MapRoute(WebApp, "/api/search/saved");	// 저장 서치 (목록·저장·삭제·순서, SavedList)
		WebApp.MapGet("/api/progress", OnProgress);	// 검색 진행 (sToken)
		WebApp.MapGet("/api/stop", OnStop);			// 검색 중지 (sToken)
		WebApp.MapMethods(API.SEARCH_PATH + "{**sToken}", new[] { HttpMethods.Get, HttpMethods.Post }, OnExternalSearch);	// 외부 API 서치 (경로 키/토큰: aAPI 서치 권한 계정, 쿼리 문자열·본문 JSON, /api/search 와 같은 응답)
	}

	private static IResult OnViewPage()	// 서치 2번 창 새 창 (보기 모드: 쿼리의 파일만 보기)
	{
		return Web.MakePage("view");
	}

	private IResult OnSearch(SearchParam kParam, HttpContext kContext)	// 로그 검색: 머신마다 클라에 Search → 끝난 순서대로 한 줄씩 {nIndex, sMachine, sError, kResult} (NDJSON, sToken: 진행·중지)
	{
		string[]	arrMachine	= kParam.arrMachine ?? Array.Empty<string>();	// 검색할 머신 (nIndex = 이 순서)
		string		sKey		= kParam.sKey ?? "";							// 검색어
		long		nStart		= kParam.nStart ?? 0;							// 시작 yyyyMMddHH
		long		nEnd		= kParam.nEnd ?? 0;								// 종료 yyyyMMddHH
		int			nMax		= Math.Clamp(kParam.nMax ?? m_nSearchDefault, 1, m_nSearchMax);	// 머신당 최대 (페이지 설정값, 없으면 기본값)
		int			nLimit		= Math.Clamp(kParam.nLimit ?? m_nSearchLimitSec, 1, m_nSearchLimitMax);	// 서치 제한 시간 (초, 페이지 설정값)

		if (arrMachine.Length > Define.m_nMachineMax)	// 머신 수 초과: 조기 반환
		{
			return Results.BadRequest($"머신 수 초과 (최대 {Define.m_nMachineMax})");
		}

		bool bValid = arrMachine.All(sMachine => !string.IsNullOrEmpty(sMachine)) && !string.IsNullOrWhiteSpace(sKey) && sKey.Length <= m_nKeyMax && IsDateHour(nStart, 23) && IsDateHour(nEnd, 24) && nStart <= nEnd;	// 검색 조건 (오류: 머신마다 sError)

		return Results.Stream(async (Stream kBody) =>
		{
			if (!bValid)	// 검색 조건 오류: 머신마다 오류 줄
			{
				for (int nIndex = 0; nIndex < arrMachine.Length; ++nIndex)
				{
					await API.WriteSearchLine(kBody, nIndex, arrMachine[nIndex], "검색 조건 오류", null);
				}

				return;
			}

			using IDisposable? kGate = await m_kGate.TryEnterAsync(kContext);	// 세션 1건 → 전체 X건 (기다리는 중 요청 끊김: null)

			if (kGate == null)
			{
				return;
			}

			ConcurrentDictionary<long, SearchTrack>? hmapTrack = m_kListener.BeginTrack(kParam.sToken);	// 진행 조회·중지 등록 (sToken 없음: null)

			using CancellationTokenRegistration kAbortReg = kContext.RequestAborted.Register(() => _ = m_kListener.Stop(kParam.sToken));	// 페이지 닫힘·새로고침·경유 끊김: 머신들 검색 중지 (게임 서버 검색이 끝까지 돌지 않게)

			try
			{
				Task<(int nIndex, byte[]? arrResult, string sError)>[] arrTask = arrMachine.Select((sMachine, nIndex) => RequestSearch(nIndex, sMachine, nStart, nEnd, sKey.Trim(), nMax, nLimit, hmapTrack)).ToArray();	// 모든 머신 동시 요청

				await foreach (Task<(int nIndex, byte[]? arrResult, string sError)> kTask in Task.WhenEach(arrTask))	// 끝난 순서대로 전송 (결과 바이트는 보내고 바로 해제)
				{
					(int nIndex, byte[]? arrResult, string sError) = await kTask;

					await API.WriteSearchLine(kBody, nIndex, arrMachine[nIndex], sError, arrResult);
				}
			}
			finally	// 페이지가 받기 중단해도 해제 (남은 요청은 끝나면 결과 버림)
			{
				m_kListener.EndTrack(kParam.sToken, hmapTrack);
			}
		}, "application/x-ndjson; charset=utf-8");
	}

	private async Task<IResult> OnExternalSearch(HttpRequest kRequest)	// 외부 API 서치 (/api/external/search/키/토큰/, aAPI 서치 권한): 쿼리 문자열 (있음) 또는 POST 본문 JSON {sKey (검색어, 필수), aMachine (머신: 반복·쉼표, 없음: 접속 머신 전체), nStart·nEnd (yyyyMMddHH, 끝 없음: 지금 시, 시작 없음: 끝 nHour - 1 시간 전), nHour (기본 X), nMax (머신당 최대)} → /api/search 와 같은 응답 (NDJSON)
	{
		Dictionary<string, List<string>> hmapParam = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);	// 입력 (키 대소문자 무시, 값 여러 개)

		try
		{
			if (kRequest.Query.Count > 0)	// 쿼리 문자열 (GET 링크)
			{
				foreach (string sName in kRequest.Query.Keys)
				{
					hmapParam[sName] = kRequest.Query[sName].Select(sValue => sValue ?? "").ToList();
				}
			}
			else if (HttpMethods.IsPost(kRequest.Method) && kRequest.ContentLength != 0)	// 본문 JSON (없음: 기본값)
			{
				using JsonDocument kDoc = await JsonDocument.ParseAsync(kRequest.Body, cancellationToken: kRequest.HttpContext.RequestAborted);

				if (kDoc.RootElement.ValueKind != JsonValueKind.Object)
				{
					return Results.Json(new { sError = "형식 오류" }, statusCode: StatusCodes.Status400BadRequest);
				}

				foreach (JsonProperty kProp in kDoc.RootElement.EnumerateObject())	// 배열: 값마다, 그 외: 글자로
				{
					hmapParam[kProp.Name] = kProp.Value.ValueKind == JsonValueKind.Array ? kProp.Value.EnumerateArray().Select(kItem => kItem.ValueKind == JsonValueKind.String ? kItem.GetString() ?? "" : kItem.GetRawText()).ToList() : new List<string> { kProp.Value.ValueKind == JsonValueKind.String ? kProp.Value.GetString() ?? "" : kProp.Value.GetRawText() };
				}
			}
		}
		catch (JsonException)	// 형식 오류
		{
			return Results.Json(new { sError = "형식 오류" }, statusCode: StatusCodes.Status400BadRequest);
		}
		catch (OperationCanceledException)	// 보낸 쪽 끊음
		{
			return Results.Empty;
		}

		string	funcText(string sName)	=> hmapParam.TryGetValue(sName, out List<string>? listValue) ? listValue.LastOrDefault() ?? "" : "";	// 값 1개 (마지막)
		long?	funcLong(string sName)	=> long.TryParse(funcText(sName), NumberStyles.None, CultureInfo.InvariantCulture, out long nValue) ? nValue : null;	// 정수 (없음·형식 오류: null)
		long	funcStamp(DateTime k)	=> long.Parse(k.ToString("yyyyMMddHH", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);	// 시각 → yyyyMMddHH

		string[]	arrMachine	= (hmapParam.GetValueOrDefault("aMachine") ?? hmapParam.GetValueOrDefault("arrMachine") ?? new List<string>()).SelectMany(sValue => sValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct().ToArray();	// 머신 (반복·쉼표)
		int			nHour		= (int)Math.Clamp(funcLong("nHour") ?? m_nExternalHour, 1, 24 * 31);											// 시작 없음: 끝 X - 1 시간 전 (최대 31일)
		long		nEnd		= funcLong("nEnd") ?? funcStamp(GetNow().DateTime);													// 끝 (포함, 없음: 지금 시)
		long		nStart		= funcLong("nStart") ?? (DateTime.TryParseExact(nEnd.ToString(CultureInfo.InvariantCulture), "yyyyMMddHH", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kEnd) ? funcStamp(kEnd.AddHours(1 - nHour)) : 0);	// 시작 (포함, 없음: 끝 X - 1 시간 전, 끝 형식 오류: 0 → 검색 조건 오류)

		return OnSearch(new SearchParam
		{
			arrMachine	= arrMachine.Length > 0 ? arrMachine : m_kListener.GetMachineList(),	// 없음: 접속 머신 전체
			nStart		= nStart,
			nEnd		= nEnd,
			sKey		= funcText("sKey"),
			nMax		= (int?)funcLong("nMax"),
			nLimit		= (int?)funcLong("nLimit"),
		}, kRequest.HttpContext);
	}

	private async Task<(int nIndex, byte[]? arrResult, string sError)> RequestSearch(int nIndex, string sMachine, long nStart, long nEnd, string sKey, int nMax, int nLimit, ConcurrentDictionary<long, SearchTrack>? hmapTrack)	// 머신 1대 검색 요청 (nIndex: 결과 순서, nLimit: 클라 검색 마감 초, hmapTrack: 진행 조회·중지)
	{
		Dictionary<string, object> hmapRequest = new Dictionary<string, object>	// Search 요청 (머신마다 따로: Request 가 nReqId 추가)
		{
			["sType"]	= "Search",
			["nStart"]	= nStart,
			["nEnd"]	= nEnd,
			["sKey"]	= sKey,
			["nMax"]	= nMax,
			["nLimit"]	= nLimit,
		};

		(byte[]? arrResult, string sError) = await m_kListener.Request(sMachine, hmapRequest, nLimit + m_nSearchWaitSec, hmapTrack);

		return (nIndex, arrResult, sError);
	}

	private async Task<IResult> OnView(string? sMachine, string? sPath, long? nStart, int? nCount, HttpContext kContext)	// 파일 보기 (머신 1대): 클라에 View → kResult 그대로 (오류: sError, 세션 1건 → 전체 X건)
	{
		if (string.IsNullOrEmpty(sMachine) || string.IsNullOrEmpty(sPath) || sPath.Length > 1000)
		{
			return API.MakeResult(sMachine, "보기 조건 오류", null);
		}

		Dictionary<string, object> hmapRequest = new Dictionary<string, object>	// View 요청 (경로 허용 검사는 클라에서)
		{
			["sType"]	= "View",
			["sPath"]	= sPath,
			["nStart"]	= nStart ?? 1,
			["nCount"]	= Math.Clamp(nCount ?? m_nViewCount, 1, m_nViewCountMax),
		};

		using IDisposable? kGate = await m_kViewGate.TryEnterAsync(kContext);

		if (kGate == null)	// 기다리는 중 요청 끊김
		{
			return Results.Empty;
		}

		(byte[]? arrResult, string sError) = await m_kListener.Request(sMachine, hmapRequest, Define.m_nViewWaitSec);

		return API.MakeResult(sMachine, sError, arrResult);
	}

	private IResult OnProgress(string? sToken)	// 검색 진행: 진행 중인 머신 합 (bFound false: 없음, nWait: 앞 검색 대기 머신 수, nFileTotal: 아는 머신만)
	{
		SearchTrack[] arrTrack = m_kListener.GetTrackList(sToken);	// 진행 중인 머신별 검색

		if (arrTrack.Length == 0)
		{
			return Results.Json(new { bFound = false });
		}

		return Results.Json(new
		{
			bFound		= true,
			nRunning	= arrTrack.Length,
			nWait		= arrTrack.Count(kTrack => kTrack.m_nFileTotal < 0),
			nFileTotal	= arrTrack.Sum(kTrack => Math.Max(0, kTrack.m_nFileTotal)),
			nFileDone	= arrTrack.Sum(kTrack => kTrack.m_nFileDone),
			nFound		= arrTrack.Sum(kTrack => kTrack.m_nFound),
		});
	}

	private async Task<IResult> OnStop(string? sToken)	// 검색 중지: 진행 중인 머신마다 Stop 전송 (클라는 그때까지 결과를 bStopped 로 보냄)
	{
		return Results.Json(new { bStopped = await m_kListener.Stop(sToken) });
	}

	private static bool IsDateHour(long nValue, int nHourMax)	// yyyyMMddHH 검사 (유효 날짜, 시간 0~nHourMax)
	{
		long nHour = nValue % 100;	// 시

		return nValue > 0 && nHour <= nHourMax && DateTime.TryParseExact((nValue / 100).ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
	}

	public static string SearchHTML()	// 페이지 서치 탭 스크립트 (Web.IndexHtml 조립)
	{
		return """
		class Search	// 서치 탭: 1번 창 검색 결과·2번 창 파일 보기 (this 없음 → 메서드를 이벤트 처리기로 바로 넘김)
		{
		    static PAGE_MAX			= 10000;	// 서치 1번 창 한 페이지 건수
		    static SPLIT_MIN		= 120;		// 서치 1·2번 창 최소 높이 (px, 가운데 바 드래그)
		    static FIT_HEIGHT_MIN	= 135;		// 서치 1번 창 기본·최소 높이 (px, 쿼리 1번 창 막대 기본 높이와 같음, CSS #ResultScroll 과 같음)
		    static FIT_ROW_MAX		= 10;		// 서치 1번 창 기본 줄 수 (넘음: 스크롤, 바 드래그 전)
		    static VIEW_COUNT		= 300;		// 서치 2번 창 한 번에 읽는 줄 수 (클릭한 줄 위·아래, 스크롤: 이어서)
		    static VIEW_MAX			= 20000;	// 서치 2번 창 최대 줄 수 (넘음: 반대쪽 지움 → 메모리 제한, 빈 높이로 위치 유지)
		    static VIEW_POS			= { kScroll: kViewScroll, kBody: kViewBody, kPrev: kViewPrev, kShift: kViewShift, kNext: kViewNext, sInput: "ViewShiftInput", nStep: Search.VIEW_COUNT, funcState: () => Search.kView };	// 서치 2번 창 포지션 버튼 (공통 ShowPos 등, 포지션 0: 클릭한 줄)
		    static WORD_BOX		= "#ResultBody td.log, #ViewBody .line";	// Ctrl·Alt+클릭 단어 칸 (서치 1번 창 Log 칸·2번 창 줄)
		    static bSearching		= false;	// 검색 중
		    static arrResult		= [];		// 검색 결과 전체 { sMachine, kLine } (화면에는 현재 페이지만 그림)
		    static arrReceived		= [];		// 검색 결과 받은 순서 (정렬 설정 바뀜: 이것으로 다시 정렬)
		    static sResultKey		= "";		// 결과의 검색어 (강조·보기용)
		    static nResultPage		= 0;		// 서치 1번 창 현재 페이지 (0부터)
		    static sResultHeight	= "";		// 서치 1번 창 기준 높이 ("": CSS 기본, 바 드래그: 그 높이) → 결과가 적으면 이보다 줄임
		    static nSelected		= -1;		// 선택한 결과 번호 (0부터, -1: 없음)
		    static arrHistory		= [];		// 들어온 단계 { sKey, arrPick } (0: 최초 검색, Ctrl·Alt 단어 검색마다 추가, ← →: 그 조건·마지막 결과, 결과 없음: 다시 검색)
		    static nHistory			= -1;		// 지금 단계 (← → 로 오감)
		    static LOG_TIME			= /^\[\d{4}\/\d{2}\/\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]/;	// 로그 줄 시작 시각 (고정 폭 → 문자열 비교 = 시간 비교, 결과 정렬)
		    static kView			= null;		// 보기 중인 파일 { sMachine, sPath, sKey, nTarget, nStart, nTotal }
		    static PROGRESS_MS		= 1000;		// 검색 진행 갱신 주기 (ms)
		    static RUN_NONE			= { nWait: 0, nFileTotal: 0, nFileDone: 0, nFound: 0 };	// 진행 중인 머신 합 없음 (/api/progress 응답 형식)
		    static sSearchToken		= "";		// 이번 검색 표시 (서버 진행 조회·중지용)
		    static bStopRequested	= false;	// 중지 요청됨
		    static arrSaved			= [];		// 저장 서치 { sKey, sQuery, nMinute } (쿼리 설정 창 공용: Query.OpenSetting(false, false, true))
		    static sSavedKey		= "";		// 고른 저장 서치 제목 (설정 목록 강조, "": 없음)
		    static bRunAfterStop	= false;	// 저장 서치 고름 (서치 중): 중지 끝난 뒤 다시 실행
		    static kProgressInfo	= null;		// 진행 표시 정보 { nCount, nDone, nFileDone, nFound, kRun, nBegin } (검색 중만, kRun: 진행 중인 머신 합)
		    static bProgressLoading	= false;	// 진행 조회 중 (겹침 방지)
		    static mapStepResult	= new WeakMap();	// 단계 → 마지막 결과 { arrReceived, sResultKey, sTitle } (← →·새 창 복사: 다시 검색 안 함)
		    static arrResultStep	= [];				// 결과 보관한 단계 (보관 순서, 모든 단계 합 RESULT_LIMIT 줄 넘음: 오래된 단계부터 버림)

		    static MakeResultRow(nIndex)	// 서치 1번 창 결과 한 줄 (arrResult[nIndex], 클릭은 kResultBody 한 곳에서 처리)
		    {
		        const kItem		= Search.arrResult[nIndex];
		        const kLine		= kItem.kLine;
		        const kRow		= document.createElement("tr");

		        kRow.dataset.index = nIndex;

		        AddCell(kRow, String(nIndex + 1), "num");
		        AddCell(kRow, kItem.sMachine, "nowrap");
		        AddFileNameCell(kRow, kItem.sMachine, kLine.sPath, kLine.nLine, Search.sResultKey);	// 파일명 (Ctrl·Alt+클릭: 그 줄 강조)
		        AddCell(kRow, String(kLine.nLine), "num");
		        Key.AppendHighlight(AddCell(kRow, "", "log"), kLine.sText, Search.sResultKey);

		        if (nIndex === Search.nSelected)
		        {
		            kRow.className = "sel";
		        }

		        return kRow;
		    }

		    static OnResultClick(kEvent)	// 서치 1번 창 결과 클릭: 2번 창에 그 줄 주변 보기 (표 하나에서 처리 → 큰 페이지 메모리·그리기 절감)
		    {
		        const kRow = kEvent.target.closest("tr[data-index]");	// 클릭한 줄

		        if (kRow == null || Search.IsWordClick(kEvent))	// Log 칸 Ctrl·Alt 클릭: 단어 추가만
		        {
		            return;
		        }

		        const nIndex	= Number(kRow.dataset.index);
		        const kItem		= Search.arrResult[nIndex];

		        kResultBody.querySelector("tr.sel")?.classList.remove("sel");
		        kRow.classList.add("sel");

		        Search.nSelected = nIndex;

		        Search.OpenView(kItem.sMachine, kItem.kLine.sPath, kItem.kLine.nLine, Search.sResultKey);
		    }

		    static IsWordClick(kEvent)	// 서치 1번 창 Log 칸·2번 창 줄 Ctrl·Alt 클릭 (첫 화면만, 새 창: 아님)
		    {
		        return (kEvent.ctrlKey || kEvent.altKey) && document.body.dataset.mode === "main" && kEvent.target.closest(Search.WORD_BOX) != null;
		    }

		    static OnWordClick(kEvent)	// 단어 Ctrl+클릭: AND 단어, Alt+클릭: OR, 둘 다: NOT (키 뗄 때 서치)
		    {
		        if (Search.IsWordClick(kEvent))
		        {
		            Query.ClickWord(kEvent, Search.WORD_BOX, false, kKey);	// 맨 앞 시간도 단어 (서치: 글자 그대로 찾음)
		        }
		    }

		    static ShowPager()	// 페이지 번호 버튼 (현재 페이지 강조, 결과 없음: 숨김)
		    {
		        const nPageCount	= Math.ceil(Search.arrResult.length / Search.PAGE_MAX);	// 전체 페이지 수
		        const kFragment		= document.createDocumentFragment();

		        for (let nPage = 0; nPage < nPageCount; ++nPage)
		        {
		            const kButton = document.createElement("button");

		            kButton.type		= "button";
		            kButton.textContent	= String(nPage + 1);
		            kButton.title		= `${(nPage * Search.PAGE_MAX + 1).toLocaleString()} ~ ${Math.min(Search.arrResult.length, (nPage + 1) * Search.PAGE_MAX).toLocaleString()}`;

		            if (nPage === Search.nResultPage)
		            {
		                kButton.className = "cur";
		            }

		            kButton.addEventListener("click", () =>
		            {
		                Search.ShowResultPage(nPage);
		            });

		            kFragment.append(kButton);
		        }

		        kResultPager.replaceChildren(kFragment);

		        kResultPager.hidden = nPageCount === 0;
		    }

		    static ShowResultPage(nPage)	// 서치 1번 창 nPage 페이지 그리기 (비우고 PAGE_MAX 건 새로 그림, 맨 위로)
		    {
		        const nPageCount	= Math.max(1, Math.ceil(Search.arrResult.length / Search.PAGE_MAX));
		        const kFragment		= document.createDocumentFragment();

		        Search.nResultPage = Math.min(Math.max(0, nPage), nPageCount - 1);

		        const nBegin	= Search.nResultPage * Search.PAGE_MAX;
		        const nEnd		= Math.min(Search.arrResult.length, nBegin + Search.PAGE_MAX);

		        for (let nIndex = nBegin; nIndex < nEnd; ++nIndex)
		        {
		            kFragment.append(Search.MakeResultRow(nIndex));
		        }

		        kResultBody.replaceChildren(kFragment);

		        kResultScroll.scrollTop		= 0;
		        kResultScroll.scrollLeft	= 0;

		        Search.ShowPager();
		        Search.FitResultHeight();
		    }

		    static FitViewHeight()	// 서치 2번 창 높이: 브라우저 창 아래 끝에 맞춤 (1번 창 높이·창 크기 바뀔 때)
		    {
		        FitHeight(kViewScroll, kViewPanel);
		    }

		    static FitResultHeight()	// 서치 1번 창 높이 (결과 적음: 결과 + 빈 줄 (최소 FIT_HEIGHT_MIN), 많음: FIT_ROW_MAX 줄, 바 드래그 후: 그 높이 이하, 검색 중·숨김·쿼리 모드: 기준 높이)
		    {
		        kResultScroll.style.height = Search.sResultHeight;

		        if (Search.bSearching || kResultPanel.hidden || kResultPanel.classList.contains("query"))	// 검색 중 (진행 표시 공간)·숨김·쿼리 모드
		        {
		            Search.FitViewHeight();

		            return;
		        }

		        const nHeight	= kResultScroll.offsetHeight - kResultScroll.clientTop * 2;	// 기준 높이 (테두리 제외, 가로 스크롤 바 포함)
		        const nBar		= nHeight - kResultScroll.clientHeight;						// 가로 스크롤 바
		        const arrRow	= kResultBody.rows;											// 지금 페이지 결과 줄
		        let nNeed		= 0;														// 맞춘 높이

		        if (arrRow.length >= Search.FIT_ROW_MAX)	// 결과 많음: FIT_ROW_MAX 줄까지 (줄마다 높이 달라도 그 줄 끝까지)
		        {
		            nNeed = Math.ceil(arrRow[Search.FIT_ROW_MAX - 1].getBoundingClientRect().bottom - kResultTable.getBoundingClientRect().top) + nBar;
		        }
		        else	// 결과 적음: 결과 + 빈 줄 1개 (최소 FIT_HEIGHT_MIN: 쿼리 1번 창과 같은 위치·크기)
		        {
		            const kRow = arrRow[0] ?? kResultTable.tHead.rows[0];	// 빈 줄 높이 기준 (결과 없음: 제목 줄)

		            nNeed = Math.max(Search.FIT_HEIGHT_MIN, Math.ceil(kResultTable.getBoundingClientRect().height + kRow.getBoundingClientRect().height) + nBar);
		        }

		        if (Search.sResultHeight === "" || nNeed < nHeight)	// 바 드래그 전: 맞춘 높이, 드래그 후: 그 높이 넘지 않음
		        {
		            kResultScroll.style.height = nNeed + "px";
		        }

		        Search.FitViewHeight();
		    }

		    static AppendResult(nFrom)	// 검색 중 추가된 결과 반영: 현재 페이지에 들어갈 줄만 추가 (스크롤 유지), 페이지 번호 갱신
		    {
		        const nBegin	= Search.nResultPage * Search.PAGE_MAX;
		        const nEnd		= Math.min(Search.arrResult.length, nBegin + Search.PAGE_MAX);
		        const kFragment	= document.createDocumentFragment();

		        for (let nIndex = Math.max(nFrom, nBegin); nIndex < nEnd; ++nIndex)
		        {
		            kFragment.append(Search.MakeResultRow(nIndex));
		        }

		        kResultBody.append(kFragment);

		        Search.ShowPager();
		    }

		    static SortResult()	// 서치 1번 창 결과 정렬 (설정 sSort, 받은 순서 기준, 선택한 결과 유지)
		    {
		        const kSelected = Search.nSelected >= 0 ? Search.arrResult[Search.nSelected] : null;	// 선택한 결과 (정렬 후 번호 다시 찾음)

		        Search.arrResult	= sSort === SORT_TIME ? Search.SortTime(Search.arrReceived) : Search.arrReceived;
		        Search.nSelected	= kSelected == null ? -1 : Search.arrResult.indexOf(kSelected);
		    }

		    static SortTime(arrItem)	// 시간 정렬 (새 배열): 시각 있는 줄 최신 위 (같음: 받은 순서), 없는 줄 맨 아래
		    {
		        const arrTime	= [];	// 시각 있는 줄 { sTime, kItem }
		        const arrOther	= [];	// 시각 없는 줄 (머신 목록 순서 그대로)

		        for (const kItem of arrItem)
		        {
		            const kMatch = Search.LOG_TIME.exec(kItem.kLine.sText);

		            if (kMatch != null)
		            {
		                arrTime.push({ sTime: kMatch[0], kItem: kItem });
		            }
		            else
		            {
		                arrOther.push(kItem);
		            }
		        }

		        arrTime.sort((kLeft, kRight) => kLeft.sTime < kRight.sTime ? 1 : (kLeft.sTime > kRight.sTime ? -1 : 0));	// 시각 내림차순 (안정 정렬 → 같은 시각: 파일 순서)

		        return arrTime.map(kTime => kTime.kItem).concat(arrOther);
		    }

		    static MakeToken()	// 검색 표시 (겹치지 않는 임의 문자열, http 접속에서도 동작)
		    {
		        return Date.now().toString(36) + Math.random().toString(36).slice(2);
		    }

		    static RenderProgress(kInfo)	// 서치 1번 창 진행 표시: 시간·완료 머신·파일·찾은 줄
		    {
		        const kRun			= kInfo.kRun;	// 진행 중인 머신 합 (마지막 조회)
		        const nSec			= Math.floor((performance.now() - kInfo.nBegin) / 1000);	// 검색 시간 (초)
		        const nFileDone		= kInfo.nFileDone + kRun.nFileDone;		// 검색한 파일 (끝난 머신 + 진행 중인 머신)
		        const nFileTotal	= kInfo.nFileDone + kRun.nFileTotal;	// 대상 파일 (진행 중인 머신은 아는 것만)
		        const sWait			= kRun.nWait > 0 ? ` · 다른 검색 대기 ${kRun.nWait}대` : "";	// 클라가 앞 검색 처리 중인 머신

		        kProgress.textContent	= `${Search.bStopRequested ? "중지하는 중" : "검색 중"} · ${nSec}초\n머신 ${kInfo.nDone}/${kInfo.nCount} 완료 · 파일 ${nFileDone.toLocaleString()} / ${nFileTotal.toLocaleString()}${sWait}\n찾은 줄: ${(kInfo.nFound + kRun.nFound).toLocaleString()}건`;
		        kSearchButton.textContent	= `${nSec}초`;	// 서치 실행 버튼: 진행 초 (끝: 실행)
		    }

		    static async ShowProgress()	// 검색 진행 조회 (PROGRESS_MS 마다, 진행 중인 머신 파일·찾은 줄 합)
		    {
		        if (Search.kProgressInfo == null)	// 검색 끝
		        {
		            return;
		        }

		        if (Search.bProgressLoading)	// 앞 조회 진행 중: 시간만 갱신
		        {
		            Search.RenderProgress(Search.kProgressInfo);

		            return;
		        }

		        const kInfo	= Search.kProgressInfo;	// 이번 검색
		        let kRun	= null;						// 진행 중인 머신 합 (null: 조회 실패 → 앞 값 유지)

		        Search.bProgressLoading = true;

		        try
		        {
		            const kData = await FetchJson("/api/progress?" + new URLSearchParams({ sToken: Search.sSearchToken }), { cache: "no-store", signal: AbortSignal.timeout(Search.PROGRESS_MS * 5) });	// 응답 없음: 끊고 다음 조회

		            kRun = kData.bFound ? kData : Search.RUN_NONE;
		        }
		        catch (kError)	// 조회 실패: 시간만 갱신
		        {
		            console.error(kError);
		        }
		        finally
		        {
		            Search.bProgressLoading = false;
		        }

		        if (kInfo === Search.kProgressInfo)	// 그 사이 검색이 끝나지 않음
		        {
		            kInfo.kRun = kRun ?? kInfo.kRun;

		            Search.RenderProgress(kInfo);
		        }
		    }

		    static async StopSearch()	// 검색 중지: 진행 중인 머신은 모두 그때까지 결과만 받음
		    {
		        if (!Search.bSearching || Search.bStopRequested)
		        {
		            return;
		        }

		        const sToken = Search.sSearchToken;	// 중지할 검색

		        Search.bStopRequested	= true;
		        kStopButton.disabled	= true;

		        if (Search.kProgressInfo != null)
		        {
		            Search.RenderProgress(Search.kProgressInfo);
		        }

		        try
		        {
		            const kData = await FetchJson("/api/stop?" + new URLSearchParams({ sToken }), { cache: "no-store" });

		            if (!kData.bStopped)	// 아직 중지할 머신 없음 (검색 막 시작 등)
		            {
		                throw new Error("Not Stopped");
		            }
		        }
		        catch (kError)	// 요청 실패·중지 안 됨: 같은 검색 중이면 중단 다시 누를 수 있게
		        {
		            console.error(kError);

		            if (Search.bSearching && Search.sSearchToken === sToken)
		            {
		                Search.bStopRequested	= false;
		                kStopButton.disabled	= false;
		            }
		        }
		    }

		    static OnKeyInput()	// 검색어칸 고침: 고른 저장 서치와 달라지면 선택 해제
		    {
		        const kSaved = Search.arrSaved.find(kItem => kItem.sKey === Search.sSavedKey);

		        if (kSaved !== undefined && kKey.value.trim() !== kSaved.sQuery)
		        {
		            Search.sSavedKey = "";
		        }
		    }

		    static IsInRange(kLine, nFrom, nTo)	// 결과 줄이 초 범위 안 (yyyyMMddHHmmss, S <= 줄 < E, 초 없는 줄: 그 분 0초, 시간 없는 줄: 그 파일 시간 정각)
		    {
		        const kMatch	= /^\[(\d{4})\/(\d{2})\/(\d{2}) (\d{2}):(\d{2})(?::(\d{2}))?/.exec(kLine.sText);	// 줄 앞 시간
		        const nSecond	= kMatch != null ? Number(kMatch.slice(1, 6).join("") + (kMatch[6] ?? "00")) : kLine.nDateHour * 10000;	// 줄 초

		        return nSecond >= nFrom && nSecond < nTo;
		    }

		    static async SearchLog(bChild = false, bReplay = false, bKeepRange = false)	// 로그 검색: 선택 머신 동시 → 끝난 머신 결과 누적 → 끝: 정렬 → 단계 남김 (bChild: 지금 단계 하위, bReplay: ← → 다시 실행, bKeepRange: 명령 창 기간 그대로 (타이머))
		    {
		        if (Search.bSearching)	// 검색 중
		        {
		            return;
		        }

		        if (!bChild && !bReplay && !bKeepRange && kQueryRange.selectedIndex > 0)	// D 최근 기간: 지금까지로 다시 (하위·← → 다시 실행·타이머: 그 기간 그대로)
		        {
		            Query.SetRecent();
		        }

		        const kFilter = GetFilter();

		        if (kFilter.sKey === "")	// 검색어 없음
		        {
		            alert("검색어를 입력해 주세요.");
		            kKey.focus();

		            return;
		        }

		        if (kStart.value === "" || kEnd.value === "")	// 기간 없음
		        {
		            alert("검색 기간을 입력해 주세요.");

		            return;
		        }

		        const arrMachine = kFilter.arrMachine;	// 검색할 머신 (목록 순서)

		        if (arrMachine.length === 0)	// 접속 머신 없음
		        {
		            alert("접속된 머신이 없습니다. M 을 눌러 다시 조회해 주세요.");

		            return;
		        }

		        if (!bReplay && !bKeepRange)	// 실행 버튼·Enter·Ctrl·Alt 놓기 (하위): 실행 기억 (모래시계, 타이머·← →·모래시계 실행: 안 넣음)
		        {
		            Query.PushBack(false);
		        }

		        Search.bSearching		= true;
		        kSearchButton.disabled	= true;
		        kSettingButton.disabled	= true;
		        kStopButton.disabled	= false;
		        kDumpButton.disabled	= true;
		        kFileButton.disabled	= true;
		        kQueryButton.disabled	= true;
		        Search.arrResult		= [];
		        Search.arrReceived	= [];
		        Search.sResultKey		= kFilter.sKey;
		        Search.nSelected		= -1;

		        if (!bChild && !bReplay)	// 실행 버튼·Enter: 앞 단계 모두 비움
		        {
		            Search.arrHistory	= [];
		            Search.nHistory	= -1;
		        }
		        else if (bChild)	// Ctrl·Alt 하위: 되돌아간 뒤의 단계 (버려질 단계) 비움
		        {
		            Search.arrHistory.splice(Search.nHistory + 1);
		        }

		        Search.sSearchToken	= Search.MakeToken();
		        Search.bStopRequested	= false;
		        Search.kProgressInfo	= { nCount: arrMachine.length, nDone: 0, nFileDone: 0, nFound: 0, kRun: Search.RUN_NONE, nBegin: performance.now() };

		        ShowPanel("log");	// 덤프·파일·쿼리 1번 창 보던 중: 서치 1·2번 창으로 (← → 끔: 검색 중)
		        Search.ShowResultPage(0);
		        Search.RenderProgress(Search.kProgressInfo);

		        const nProgressTimer = setInterval(Search.ShowProgress, Search.PROGRESS_MS);	// 진행 표시 갱신
		        const nProgressPopup = setTimeout(() =>	// 팝업: X초 이상 걸릴 때만 (정확히 X초)
		        {
		            if (Search.kProgressInfo != null)
		            {
		                Search.RenderProgress(Search.kProgressInfo);

		                kProgress.hidden = false;
		            }
		        }, PROGRESS_DELAY_MS);

		        const sScope		= kFilter.sMachine === "전체" ? "전체" : "선택";	// 제목 머신 범위 (전체·선택)
		        const nBegin		= performance.now();	// 검색 시작 시각

		        kResultTitle.textContent = `${sScope} - 서치 중: 머신 0/${arrMachine.length} 완료 · 0건`;	// 첫 머신 결과 전: 앞 검색 제목 지움
		        const nSearchMachineMax	= nMachineMax;		// 이번 검색 머신당 최대 (설정, 검색 중 변경 불가)
		        const nSearchResultMax	= nResultMax;		// 이번 검색 전체 최대 (설정)
		        const arrNotice		= [];					// 머신별 오류·일부 표시 안내
		        const arrOver		= [];					// 머신당 최대(nSearchMachineMax) 넘은 머신
		        const arrStopped	= [];					// 중지로 그때까지 결과만 받은 머신
		        const arrWait		= new Array(arrMachine.length);	// 받은 머신 결과 (앞 머신이 아직이면 보관, undefined: 아직 안 옴)
		        const kAbort		= new AbortController();	// 전체 최대 넘으면 결과 받기 중단
		        let nNext			= 0;					// 다음에 서치 1번 창에 넣을 머신 순서
		        let nFileCount		= 0;					// 검색한 파일 수
		        let bTotalOver		= false;				// 전체 최대(nSearchResultMax) 넘음
		        let nSkipMachine	= 0;					// 전체 최대 넘어 표시 안 한 머신 수

		        const funcAdd = (nIndex, kData) =>	// 머신 1대 결과를 서치 1번 창에 추가 (머신 순서대로 호출, 전체 최대 넘으면 bTotalOver)
		        {
		            const sMachine	= arrMachine[nIndex];
		            const kResult	= kData.kResult;

		            if (kData.sError !== "" || kResult == null)	// 미접속·시간 초과 등
		            {
		                arrNotice.push(`${sMachine}: ${kData.sError}`);

		                return;
		            }

		            const nFrom		= Search.arrResult.length;	// 이번 머신 결과 시작 번호
		            const nRoom		= nSearchResultMax - nFrom;	// 전체 최대까지 남은 건수
		            const arrLine	= kResult.arrLine.filter(kLine => Search.IsInRange(kLine, kFilter.nStartSec, kFilter.nEndSec));	// 초 범위 안 줄만 (S <= 줄 < E, 검색은 시간 파일 단위)

		            nFileCount += kResult.nFileCount;

		            for (const kLine of arrLine.slice(0, nRoom))
		            {
		                Search.arrResult.push({ sMachine: sMachine, kLine: kLine });
		            }

		            Search.AppendResult(nFrom);

		            if (kResult.sError !== "")
		            {
		                arrNotice.push(`${sMachine}: ${kResult.sError}`);
		            }
		            else if (kResult.bStopped)	// 중지: 그때까지 결과만 (안내는 검색 끝에 머신 수로)
		            {
		                arrStopped.push(sMachine);
		            }
		            else if (kResult.bTruncated)	// 머신당 최대 넘음 (클라가 nSearchMachineMax 건에서 중단, 안내는 검색 끝에 머신 수로)
		            {
		                arrOver.push(sMachine);
		            }

		            if (arrLine.length > nRoom || (Search.arrResult.length >= nSearchResultMax && (kResult.bTruncated || nIndex < arrMachine.length - 1)))	// 전체 최대 넘음: 이후 머신 표시 안 함
		            {
		                bTotalOver		= true;
		                nSkipMachine	= arrMachine.length - nIndex - 1;
		            }
		        };

		        const funcFlush = (bEnd) =>	// 앞 머신부터 차례로 추가 (앞 머신 아직: 대기, bEnd: 결과 없는 머신 = 요청 실패)
		        {
		            while (!bTotalOver && nNext < arrMachine.length)
		            {
		                const kData = arrWait[nNext];

		                if (kData === undefined && !bEnd)	// 앞 머신 아직
		                {
		                    break;
		                }

		                arrWait[nNext] = null;	// 추가 후 해제

		                if (kData === undefined)
		                {
		                    arrNotice.push(`${arrMachine[nNext]}: 요청 실패`);
		                }
		                else
		                {
		                    funcAdd(nNext, kData);
		                }

		                ++nNext;
		            }
		        };

		        const funcLine = (sLine) =>	// 서버 결과 한 줄 = 머신 1대 (끝난 순서): 보관 → 차례로 추가 → 전체 최대 넘음: 중지·받기 중단
		        {
		            if (bTotalOver)	// 전체 최대 넘음: 이후 결과 버림
		            {
		                return;
		            }

		            const kData = JSON.parse(sLine);

		            if (!(kData.nIndex >= 0 && kData.nIndex < arrMachine.length) || arrWait[kData.nIndex] !== undefined)	// 잘못된·중복 줄
		            {
		                return;
		            }

		            arrWait[kData.nIndex] = kData;

		            Search.kProgressInfo.nDone		+= 1;
		            Search.kProgressInfo.nFileDone	+= kData.kResult?.nFileCount ?? 0;
		            Search.kProgressInfo.nFound		+= kData.kResult?.arrLine.length ?? 0;

		            funcFlush(false);

		            kResultTitle.textContent = `${sScope} - 서치 중: 머신 ${Search.kProgressInfo.nDone}/${arrMachine.length} 완료 · ${Search.arrResult.length.toLocaleString()}건`;

		            if (bTotalOver)	// 전체 최대 넘음: 남은 머신 검색 중지 (게임 서버 부하), 결과 받기 중단
		            {
		                fetch("/api/stop?" + new URLSearchParams({ sToken: Search.sSearchToken }), { cache: "no-store" }).catch(kError => console.error(kError));
		                kAbort.abort();
		            }
		        };

		        try
		        {
		            const kResponse = await fetch("/api/search", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ arrMachine: arrMachine, nStart: kFilter.nStart, nEnd: kFilter.nEnd, sKey: kFilter.sKey, nMax: nSearchMachineMax, nLimit: nSearchLimitSec, sToken: Search.sSearchToken }), cache: "no-store", signal: kAbort.signal });	// 모든 머신 동시 검색 (한 줄 = 머신 1대, 끝난 순서, nLimit: 서치 제한 시간 설정)

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kReader	= kResponse.body.pipeThrough(new TextDecoderStream()).getReader();	// UTF-8 → 문자열 조각
		            let arrPart		= [];	// 받는 중인 줄 조각 (큰 줄을 조각마다 처음부터 다시 찾지 않음)

		            while (true)
		            {
		                const { value: sChunk, done: bDone } = await kReader.read();

		                if (bDone)
		                {
		                    break;
		                }

		                let nPos = 0;	// 조각에서 다음 줄 시작

		                for (let nFind = sChunk.indexOf("\n"); nFind !== -1; nFind = sChunk.indexOf("\n", nPos))
		                {
		                    arrPart.push(sChunk.slice(nPos, nFind));
		                    funcLine(arrPart.join(""));

		                    arrPart	= [];
		                    nPos	= nFind + 1;
		                }

		                arrPart.push(sChunk.slice(nPos));
		            }
		        }
		        catch (kError)	// 요청 실패 (받은 머신까지만, 나머지는 요청 실패)·전체 최대로 받기 중단
		        {
		            if (!kAbort.signal.aborted)
		            {
		                console.error(kError);
		            }
		        }

		        clearInterval(nProgressTimer);
		        clearTimeout(nProgressPopup);

		        funcFlush(true);	// 남은 머신 차례로 추가 (결과 없음: 요청 실패)

		        Search.arrReceived = Search.arrResult;	// 받은 순서 보관

		        Search.SortResult();		// 설정 정렬
		        Search.ShowResultPage(0);	// 정렬한 결과를 첫 페이지부터 다시 그림

		        if (arrOver.length > 0)	// 머신당 최대 넘은 머신 수 (머신 이름은 팝업에서)
		        {
		            arrNotice.push(`머신당 최대 ${nSearchMachineMax.toLocaleString()}건 초과(머신 ${arrOver.length}대)`);
		        }

		        if (bTotalOver)
		        {
		            arrNotice.push(`전체 최대 ${nSearchResultMax.toLocaleString()}건 초과로 중단`);
		        }

		        if (arrStopped.length > 0)	// 중지로 그때까지 결과만 받은 머신 수
		        {
		            arrNotice.push(`중지로 그때까지 결과만(머신 ${arrStopped.length}대)`);
		        }

		        SetResultTitle(kResultTitle, `${sScope} - ${Search.bStopRequested ? "서치 중지" : "서치 결과"}: ${Search.arrResult.length.toLocaleString()}건 (머신 ${arrMachine.length}대 · 파일 ${nFileCount}개) · 서치 시간: ${Math.round(performance.now() - nBegin)} ms${arrNotice.length > 0 ? " · " + arrNotice.join(" / ") : ""}`);	// 건수·시간 숫자: 존 색

		        Search.bSearching		= false;
		        kSearchButton.disabled	= false;
		        kSearchButton.textContent	= "실행";
		        kSettingButton.disabled	= false;
		        kStopButton.disabled	= true;
		        kDumpButton.disabled	= Dump.bDumpLoading;
		        kFileButton.disabled	= false;
		        kQueryButton.disabled	= false;
		        Search.kProgressInfo	= null;
		        kProgress.hidden		= true;

		        if (Search.bRunAfterStop)	// 저장 서치 고름 (서치 중 중지): 이 검색 마무리 뒤 다시 실행
		        {
		            Search.bRunAfterStop = false;

		            setTimeout(() => Search.SearchLog());
		        }

		        if (!bReplay)	// 들어온 단계 남김 (지금 단계 뒤 버림, ← → 다시 실행: 그대로)
		        {
		            Search.arrHistory.splice(Search.nHistory + 1);
		            Search.arrHistory.push({ sKey: kKey.value, arrPick: [kStart.value, kStartHour.value, kStartMinute.value, kEnd.value, kEndHour.value, kEndMinute.value, kStartSecond.value, kEndSecond.value], nRange: kQueryRange.selectedIndex });

		            Search.nHistory = Search.arrHistory.length - 1;
		        }

		        if (!Search.bStopRequested)	// 끝까지 검색: 이 단계 결과 보관 (← →·새 창: 그대로, 중지: 다음에 다시 검색)
		        {
		            Search.SaveStepResult(Search.arrHistory[Search.nHistory], { arrReceived: Search.arrReceived, sResultKey: Search.sResultKey, sTitle: kResultTitle.textContent });
		        }

		        Query.ShowHistory();
		        Search.FitResultHeight();	// 결과가 적으면 서치 2번 창 끌어올림

		        if (arrOver.length > 0 || bTotalOver)	// 최대 초과 안내 팝업 (확인으로 닫힘): 머신당 초과 머신 목록, 맨 아래 전체 초과
		        {
		            const arrText = [];	// 팝업 줄

		            if (arrOver.length > 0)
		            {
		                arrText.push(`머신당 최대 ${nSearchMachineMax.toLocaleString()}건을 넘어 일부만 표시한 머신 (${arrOver.length}대)`);
		                arrText.push(...arrOver.map(sMachine => `- ${sMachine}`));
		            }

		            if (bTotalOver)
		            {
		                if (arrText.length > 0)
		                {
		                    arrText.push("");
		                }

		                arrText.push(`전체 최대 ${nSearchResultMax.toLocaleString()}건을 넘어 ${nSearchResultMax.toLocaleString()}건까지만 표시${nSkipMachine > 0 ? ` (표시 안 한 머신 ${nSkipMachine}대)` : ""}`);
		            }

		            alert(arrText.join("\n"));
		        }
		    }

		    static async GoHistory(nDirection)	// ← (-1)·→ (1): 들어온 단계 오가며 그 조건·마지막 결과 그대로 (결과 없음: 다시 검색, 검색 중: 중지 후)
		    {
		        if (Search.bSearching)	// 검색 중: 중지 (그때까지 결과만) 뒤 이어서
		        {
		            await Search.StopSearch();

		            while (Search.bSearching)
		            {
		                await new Promise(funcDone => setTimeout(funcDone, 50));
		            }
		        }

		        const nIndex = Search.nHistory + nDirection;	// 갈 단계

		        if (nIndex < 0 || nIndex >= Search.arrHistory.length)
		        {
		            return;
		        }

		        const kStep = Search.arrHistory[nIndex];	// 그 단계

		        Search.nHistory = nIndex;

		        kKey.value = kStep.sKey;

		        [kStart.value, kStartHour.value, kStartMinute.value, kEnd.value, kEndHour.value, kEndMinute.value, kStartSecond.value, kEndSecond.value] = kStep.arrPick;

		        kQueryRange.selectedIndex = kStep.nRange ?? 0;	// D 도 그 단계로 (쿼리 ← → 와 같음, 예전 단계: 해제)

		        const kResult = Search.mapStepResult.get(kStep);	// 그 단계 마지막 결과

		        if (kResult !== undefined)	// 결과 있음: 다시 검색 안 함
		        {
		            Search.ShowStep(kResult);

		            return;
		        }

		        Query.ShowHistory();

		        await Search.SearchLog(false, true);	// 그 조건으로 다시 검색 (단계 그대로)
		    }

		    static SaveStepResult(kStep, kResult)	// 단계 결과 보관 (버린 단계: 뺌, 모든 단계 합 RESULT_LIMIT 줄 넘음: 오래된 단계부터 버림, 단계 없음: 안 함)
		    {
		        if (kStep == null)
		        {
		            return;
		        }

		        Search.mapStepResult.set(kStep, kResult);

		        Search.arrResultStep = Search.arrResultStep.filter(kItem => kItem !== kStep && Search.arrHistory.includes(kItem));
		        Search.arrResultStep.push(kStep);

		        let nKeep = 0;	// 새 단계부터 센 보관 줄 수

		        for (let nIndex = Search.arrResultStep.length - 1; nIndex >= 0; --nIndex)
		        {
		            nKeep += Search.mapStepResult.get(Search.arrResultStep[nIndex]).arrReceived.length;

		            if (nKeep > RESULT_LIMIT && nIndex < Search.arrResultStep.length - 1)	// 넘음: 이 단계부터 오래된 단계 버림 (방금 단계: 남김)
		            {
		                for (const kOld of Search.arrResultStep.splice(0, nIndex + 1))
		                {
		                    Search.mapStepResult.delete(kOld);
		                }

		                break;
		            }
		        }
		    }

		    static ShowStep(kResult)	// 단계 보관 결과 그대로 그리기 (다시 검색 안 함, 지금 정렬 설정으로)
		    {
		        Search.arrReceived	= kResult.arrReceived;
		        Search.sResultKey		= kResult.sResultKey;
		        Search.nSelected		= -1;

		        Search.SortResult();
		        Search.ShowResultPage(0);

		        SetResultTitle(kResultTitle, kResult.sTitle);	// 건수·시간 숫자: 존 색

		        Query.ShowHistory();
		        Search.FitResultHeight();	// 결과가 적으면 서치 2번 창 끌어올림
		    }

		    static ClearResult()	// 최초 입장 리셋: 서치 결과 메모리 (단계·보관 결과)·스텝·1번 창 결과·2번 창 보기·포지션 처음으로
		    {
		        Search.arrResult		= [];
		        Search.arrReceived	= [];
		        Search.sResultKey		= "";
		        Search.nSelected		= -1;
		        Search.arrHistory		= [];
		        Search.nHistory		= -1;
		        Search.mapStepResult	= new WeakMap();
		        Search.arrResultStep	= [];
		        Search.kView			= null;	// 늦게 온 보기 응답 버림
		        kResultTitle.textContent	= "";
		        kViewTitle.textContent		= "";

		        Search.ShowResultPage(0);
		        Search.ClearView();

		        ShowPos(Search.VIEW_POS);	// 포지션 0·« » 끔

		        Query.ShowHistory();
		        Search.FitResultHeight();
		    }

		    static ClearView()	// 서치 2번 창 비우기 (내용·가로·세로 스크롤·위·아래 빈 높이 초기화)
		    {
		        kViewBody.replaceChildren();

		        kViewBody.style.paddingTop		= "";
		        kViewBody.style.paddingBottom	= "";

		        kViewScroll.scrollTop	= 0;
		        kViewScroll.scrollLeft	= 0;
		    }

		    static OpenView(sMachine, sPath, nTarget, sKey, nStart = null)	// 서치 2번 창 열기 (기본: 클릭한 줄 위·아래 VIEW_COUNT 줄 + 강조, nStart: 그 줄부터·0: 마지막 줄들, 스크롤: 이어서)
		    {
		        Search.kView = { sMachine: sMachine, sPath: sPath, sKey: sKey, nTarget: nTarget, nAnchor: nTarget, nPos: nTarget, nFirst: 0, nLast: 0, nTotal: 0, bLoading: false };	// nAnchor: 포지션 0 줄 (클릭한 줄, 없음: 첫 읽기 뒤 마지막 줄), nPos: 처음 보일 기준 줄 (보이는 줄 가운데)

		        Search.ClearView();
		        ShowPos(Search.VIEW_POS);

		        if (nStart != null)	// 끝 (줄 모름)
		        {
		            Search.LoadView(nStart, Search.VIEW_COUNT, 0);

		            return;
		        }

		        Search.LoadViewPos();
		    }

		    static LoadViewPos()	// 서치 2번 창 기준 줄 (kView.nPos) 위·아래 VIEW_COUNT 줄씩 새로 읽기 (기준 줄: 보이는 줄 가운데)
		    {
		        const kView	= Search.kView;
		        const nFrom	= Math.max(1, kView.nPos - Search.VIEW_COUNT);

		        Search.LoadView(nFrom, kView.nPos - nFrom + 1 + Search.VIEW_COUNT, 0);
		    }

		    static FillView()	// 서치 2번 창 스크롤: 보이는 곳 줄 읽기 (FillLines, VIEW_COUNT 줄씩)
		    {
		        FillLines(kViewScroll, kViewBody, Search.kView, Search.VIEW_COUNT, Search.LoadView);
		    }

		    static async LoadView(nStart, nCount, nDirection)	// 서치 2번 창 nStart 줄부터 nCount 줄 읽기 (nStart 0: 마지막, nDirection 0: 새로·-1: 위에 붙임·1: 아래에 붙임·2: 그 위치로 바꿈, 그 사이 다른 파일: 버림)
		    {
		        const kTarget = Search.kView;	// 요청한 파일

		        if (kTarget == null || kTarget.bLoading)
		        {
		            return;
		        }

		        kTarget.bLoading	= true;
		        kViewPanel.hidden	= false;

		        if (nDirection === 0)
		        {
		            kViewTitle.textContent = `${kTarget.sMachine} : ${kTarget.sPath} (불러오는 중)`;
		        }

		        try
		        {
		            const kParam	= new URLSearchParams({ sMachine: kTarget.sMachine, sPath: kTarget.sPath, nStart: nStart, nCount: nCount });
		            const kData		= await FetchJson("/api/view?" + kParam, { cache: "no-store" });

		            if (kTarget !== Search.kView)	// 그 사이 다른 파일·처음·끝
		            {
		                return;
		            }

		            const kResult	= kData.kResult;
		            const sError	= kData.sError !== "" ? kData.sError : (kResult == null ? "결과 없음" : kResult.sError);

		            if (sError !== "")
		            {
		                if (nDirection === 0)	// 새로: 비움 (이어 읽기: 보던 내용 유지)
		                {
		                    Search.ClearView();
		                }

		                kTarget.bError			= true;	// 스크롤로 다시 읽지 않음 (파일을 다시 열면 처음부터)
		                kViewTitle.textContent	= `${kTarget.sMachine} : ${kTarget.sPath} (보기 실패: ${sError})`;

		                return;
		            }

		            const kFragment	= document.createDocumentFragment();	// 새 내용 (다 만든 뒤 한 번에 붙임)
		            const nCountRead	= kResult.arrText.length;				// 읽은 줄 수
		            let kPosLine		= null;									// 기준 줄 (처음: 클릭한 줄)

		            kResult.arrText.forEach((sText, nIndex) =>
		            {
		                const nLine	= kResult.nStart + nIndex;
		                const kLine	= document.createElement("span");
		                const kNo	= document.createElement("span");

		                kLine.className	= "line";
		                kNo.className	= "lno";
		                kNo.textContent	= String(nLine).padStart(7, " ") + " : ";

		                kLine.append(kNo);
		                Key.AppendHighlight(kLine, sText, kTarget.sKey);

		                if (nLine === kTarget.nTarget)	// 클릭한 줄: 강조 (이어 읽어도 그 줄 있으면 강조)
		                {
		                    kLine.classList.add("target");
		                }

		                if (nLine === kTarget.nPos)
		                {
		                    kPosLine = kLine;
		                }

		                kFragment.append(kLine);
		            });

		            kTarget.nTotal = kResult.nTotal;

		            ShowPos(Search.VIEW_POS);	// 포지션 « » 켬·끔 (전체 줄 수 앎)

		            if (nDirection < 0)	// 위에 붙임: 위 빈 높이가 그만큼 줄어 보던 위치 그대로 (읽을 줄 없음: 1줄까지 읽음)
		            {
		                kViewBody.prepend(kFragment);

		                kTarget.nFirst = nCountRead > 0 ? kResult.nStart : 1;

		                TrimLines(kViewBody, kTarget, Search.VIEW_MAX, false);	// 최대 넘음: 아래쪽 지움
		                SetLineSpace(kViewBody, kTarget);
		            }
		            else if (nDirection === 1)	// 아래에 붙임
		            {
		                kViewBody.append(kFragment);

		                kTarget.nLast += nCountRead;

		                TrimLines(kViewBody, kTarget, Search.VIEW_MAX, true);	// 최대 넘음: 위쪽 지움
		                SetLineSpace(kViewBody, kTarget);
		            }
		            else if (nDirection === 2)	// 멀리 이동: 그 위치 줄로 바꿈 (빈 높이로 위치 그대로)
		            {
		                kViewBody.replaceChildren(kFragment);

		                kTarget.nFirst	= nCountRead > 0 ? kResult.nStart : 1;
		                kTarget.nLast	= nCountRead > 0 ? kResult.nStart + nCountRead - 1 : 0;

		                SetLineSpace(kViewBody, kTarget);
		            }
		            else	// 새로
		            {
		                Search.ClearView();
		                kViewBody.append(kFragment);

		                kTarget.nFirst	= nCountRead > 0 ? kResult.nStart : 1;
		                kTarget.nLast	= nCountRead > 0 ? kResult.nStart + nCountRead - 1 : 0;
		                kTarget.nExtra	= 0;
		                kTarget.bError	= false;

		                SetLineSpace(kViewBody, kTarget);	// 위·아래 빈 높이 (스크롤 바: 파일 전체)

		                if (kTarget.nAnchor === 0)	// 줄 모름 (끝부터): 위치 0 = 마지막 줄
		                {
		                    kTarget.nAnchor	= kTarget.nLast;
		                    kTarget.nPos	= kTarget.nLast;
		                }

		                const bEnd = nStart === 0;	// 끝 (줄 모름): 맨 아래

		                if (kPosLine != null && !bEnd)	// 기준 줄: 보이는 줄 가운데 (줄 보기·보기 새 창 공통 GetCenterAbove, 가로: 맨 왼쪽)
		                {
		                    const nAboveMax	= GetCenterAbove(kViewScroll, kViewBody);	// 위에 보일 줄 수
		                    let kTop		= kPosLine;	// 맨 위에 보일 줄

		                    for (let nAbove = 0; nAbove < nAboveMax && kTop.previousElementSibling != null; ++nAbove)
		                    {
		                        kTop = kTop.previousElementSibling;
		                    }

		                    const nShort = kTop.offsetTop + kViewScroll.clientHeight - (kViewBody.offsetTop + kViewBody.offsetHeight);	// 그 줄을 위 끝에 두기에 모자란 높이 (파일 끝 가까움, 아래 빈 높이 포함)

		                    if (kTop.previousElementSibling != null && nShort > 0)	// 아래 여백 늘림 → 그 줄부터 보이고 앞 줄은 위로 스크롤
		                    {
		                        kTarget.nExtra = nShort;

		                        SetLineSpace(kViewBody, kTarget);
		                    }

		                    kViewScroll.scrollTop = kTop.previousElementSibling == null ? 0 : kTop.offsetTop;	// 그 줄을 창 위 끝에 (앞 줄 안 보임, 첫 줄: 맨 위)
		                }
		                else if (bEnd)	// 끝: 맨 아래
		                {
		                    kViewScroll.scrollTop = kViewScroll.scrollHeight;
		                }

		                SetPosStart(Search.VIEW_POS);	// 처음 연 포지션 (스크롤 전: 0 그대로)
		            }

		            const sRange = kTarget.nLast >= kTarget.nFirst && kTarget.nLast > 0 ? `${kTarget.nFirst}~${kTarget.nLast}` : "-";	// 쌓인 줄 범위

		            kViewTitle.textContent = `${kTarget.sMachine} : ${kTarget.sPath} (${sRange} / 전체 ${kResult.nTotal}줄)`;

		            ShowPos(Search.VIEW_POS);	// 포지션: 붙인 줄·스크롤 위치 기준

		            setTimeout(Search.FillView, 0);	// 읽는 사이 스크롤로 옮긴 곳 (읽는 중 끝난 뒤)
		        }
		        catch (kError)	// 요청 실패
		        {
		            if (kTarget === Search.kView)
		            {
		                if (nDirection === 0)	// 새로: 비움 (이어 읽기: 보던 내용 유지)
		                {
		                    Search.ClearView();
		                }

		                kTarget.bError			= true;	// 스크롤로 다시 읽지 않음 (파일을 다시 열면 처음부터)
		                kViewTitle.textContent	= `${kTarget.sMachine} : ${kTarget.sPath} (요청 실패: ${ErrorText(kError)})`;
		            }
		        }
		        finally
		        {
		            kTarget.bLoading = false;
		        }
		    }

		    static SyncViewHScroll()	// 서치 2번 창 아래 좌우 바: 좌우 넘침 + 창 아래쪽이 화면 밖일 때만 표시, 이동 범위·위치 맞춤
		    {
		        const kRect	= kViewScroll.getBoundingClientRect();
		        const bShow	= kViewScroll.offsetParent != null && kViewScroll.scrollWidth > kViewScroll.clientWidth && kRect.top < window.innerHeight && kRect.bottom > window.innerHeight;

		        kViewHScroll.hidden = !bShow;

		        if (bShow)
		        {
		            kViewHScrollSize.style.width	= (kViewScroll.scrollWidth - kViewScroll.clientWidth + kViewHScroll.clientWidth) + "px";	// 이동 범위 = 서치 2번 창과 같게
		            kViewHScroll.scrollLeft			= kViewScroll.scrollLeft;
		        }
		    }
		}
		""";
	}
}

class SearchTrack	// 머신 1대 검색 진행 (클라가 갱신, /api/progress: 머신 합 조회, /api/stop: 모두 중지)
{
	public readonly ClientInfo	m_kInfo;				// 검색 중인 클라
	public readonly long		m_nReqId;				// 요청 번호
	public long					m_nFileTotal	= -1;	// 대상 파일 수 (-1: 아직 모름·앞 검색 대기 중)
	public long					m_nFileDone;			// 검색한 파일 수
	public long					m_nFound;				// 찾은 줄 수
	public long					m_nElapsedMs;			// 검색 시간 (ms)

	public SearchTrack(ClientInfo kInfo, long nReqId)
	{
		m_kInfo		= kInfo;
		m_nReqId	= nReqId;
	}
}

class SearchNode	// 검색 조건 트리 (서치·쿼리 공통, Client SearchNode·페이지 Key 와 같은 규칙): AND·OR·NOT (대소문자 무시) 왼쪽부터, 괄호 안 먼저 (예: "A AND (B NOT C) OR D" = (A AND (B NOT C)) OR D)
//   연산자: 공백 + AND·OR·NOT (대소문자 무시) + 공백·여는 괄호 (단어 안: 검색어), 괄호: 검색어 자리 '(' 와 짝 ')' (검색어 안 괄호 Init(x): 그대로)
//   형식 오류 (연산자로 끝남·괄호 뒤 검색어 등): 전체를 검색어 하나로
{
	public readonly SEARCH_OP	m_eOp;		// 연산
	public readonly string		m_sTerm;	// 검색어 (SEARCH_OP_TERM 만, 그 외: "")
	public readonly SearchNode?	m_kLeft;	// 왼쪽 (연산만)
	public readonly SearchNode?	m_kRight;	// 오른쪽 (연산만)

	private SearchNode(SEARCH_OP eOp, string sTerm, SearchNode? kLeft, SearchNode? kRight)
	{
		m_eOp		= eOp;
		m_sTerm		= sTerm;
		m_kLeft		= kLeft;
		m_kRight	= kRight;
	}

	public static bool IsValid(string sKey)	// 검색어 형식 맞음 (빈 괄호·연산자로 시작·끝·빈 검색어: false, 페이지 Key.IsValid 와 같음)
	{
		string sTrim = sKey.Trim();

		if (sTrim == "")
		{
			return false;
		}

		List<(char cType, string sTerm)>?	listToken	= Tokenize(sTrim);	// 토큰 (null: 형식 오류)
		int									nIndex		= 0;				// 읽을 토큰

		return listToken != null && ParseExpr(listToken, ref nIndex) != null && nIndex == listToken.Count && !listToken.Any(kToken => kToken.cType == 'T' && Regex.IsMatch(kToken.sTerm.Trim(), @"^(AND|OR|NOT)(\s|$)|\s(AND|OR|NOT)$", RegexOptions.IgnoreCase));	// 검색어가 AND·OR·NOT (대소문자 무시) 로 시작·끝남 (예: "A AND"): 연산자 빠짐으로 봄
	}

	public static SearchNode? Parse(string sKey)	// 검색어 → 조건 트리 (빈 검색어: null, 형식 오류: 전체를 검색어 하나로)
	{
		string sTrim = sKey.Trim();

		if (sTrim == "")
		{
			return null;
		}

		List<(char cType, string sTerm)>?	listToken	= Tokenize(sTrim);	// 토큰 (null: 형식 오류)
		int									nIndex		= 0;				// 읽을 토큰
		SearchNode?							kRoot		= listToken == null ? null : ParseExpr(listToken, ref nIndex);

		if (listToken == null || kRoot == null || nIndex != listToken.Count)	// 형식 오류: 전체를 검색어 하나로
		{
			return new SearchNode(SEARCH_OP.SEARCH_OP_TERM, sTrim, null, null);
		}

		return kRoot;
	}

	private static SearchNode? ParseExpr(List<(char cType, string sTerm)> listToken, ref int nIndex)	// 식: 항 (연산자 항)… 왼쪽부터 차례로 (null: 형식 오류)
	{
		SearchNode? kLeft = ParseTerm(listToken, ref nIndex);	// 지금까지 결과

		while (kLeft != null && nIndex < listToken.Count && listToken[nIndex].cType != ')')
		{
			SEARCH_OP eOp = listToken[nIndex].cType switch	// 연산자
			{
				'&'	=> SEARCH_OP.SEARCH_OP_AND,
				'|'	=> SEARCH_OP.SEARCH_OP_OR,
				'!'	=> SEARCH_OP.SEARCH_OP_NOT,
				_	=> SEARCH_OP.SEARCH_OP_TERM,
			};

			if (eOp == SEARCH_OP.SEARCH_OP_TERM)	// 연산자 자리에 다른 토큰
			{
				return null;
			}

			++nIndex;

			SearchNode? kRight = ParseTerm(listToken, ref nIndex);

			kLeft = kRight == null ? null : new SearchNode(eOp, "", kLeft, kRight);
		}

		return kLeft;
	}

	private static SearchNode? ParseTerm(List<(char cType, string sTerm)> listToken, ref int nIndex)	// 항: 검색어 또는 (식) (닫는 괄호 없이 끝나면 끝에서 닫힘, null: 형식 오류)
	{
		if (nIndex >= listToken.Count)
		{
			return null;
		}

		(char cType, string sTerm) = listToken[nIndex++];

		if (cType == 'T')
		{
			return sTerm == "" ? null : new SearchNode(SEARCH_OP.SEARCH_OP_TERM, sTerm, null, null);
		}

		if (cType != '(')
		{
			return null;
		}

		SearchNode? kInner = ParseExpr(listToken, ref nIndex);	// 괄호 안

		if (nIndex < listToken.Count && listToken[nIndex].cType == ')')
		{
			++nIndex;
		}

		return kInner;
	}

	private static List<(char cType, string sTerm)>? Tokenize(string sKey)	// 토큰 나누기: 검색어 T·AND &·OR |·NOT !·괄호 (null: 형식 오류)
	{
		List<(char cType, string sTerm)>	listToken	= new List<(char cType, string sTerm)>();
		int									nPos		= 0;	// 읽을 위치
		int									nDepth		= 0;	// 열린 괄호 수
		bool								bOperand	= true;	// 검색어·여는 괄호 차례 (false: 연산자·닫는 괄호 차례)

		while (true)
		{
			while (nPos < sKey.Length && char.IsWhiteSpace(sKey[nPos]))
			{
				++nPos;
			}

			if (nPos >= sKey.Length)
			{
				break;
			}

			if (bOperand && sKey[nPos] == '(')	// 여는 괄호
			{
				listToken.Add(('(', ""));

				++nDepth;
				++nPos;

				continue;
			}

			if (bOperand)	// 검색어: 다음 연산자·닫는 괄호 앞까지
			{
				int nEnd = FindTermEnd(sKey, nPos, nDepth);	// 검색어 끝

				listToken.Add(('T', sKey[nPos..nEnd].Trim()));

				nPos		= nEnd;
				bOperand	= false;

				continue;
			}

			if (sKey[nPos] == ')' && nDepth > 0)	// 닫는 괄호
			{
				listToken.Add((')', ""));

				--nDepth;
				++nPos;

				continue;
			}

			int nOpLength = GetOperator(sKey, nPos, out char cType);	// 연산자 길이 (0: 아님)

			if (nOpLength == 0)	// 연산자 자리에 다른 글자 (예: (A) B)
			{
				return null;
			}

			listToken.Add((cType, ""));

			nPos		+= nOpLength;
			bOperand	= true;
		}

		return bOperand ? null : listToken;	// 연산자·여는 괄호로 끝남: 형식 오류
	}

	private static int FindTermEnd(string sKey, int nStart, int nDepth)	// 검색어 끝: 공백 뒤 연산자·괄호 안 닫는 괄호 앞 (검색어 안 짝 맞는 괄호: 검색어 글자)
	{
		int nInner = 0;	// 검색어 안에서 열린 괄호 수 (예: Init(x))

		for (int nIndex = nStart; nIndex < sKey.Length; ++nIndex)
		{
			char cChar = sKey[nIndex];

			if (char.IsWhiteSpace(cChar))	// 공백 뒤 연산자·닫는 괄호면 여기서 끝
			{
				int nNext = nIndex;	// 공백 다음 글자

				while (nNext < sKey.Length && char.IsWhiteSpace(sKey[nNext]))
				{
					++nNext;
				}

				if (nNext < sKey.Length && (GetOperator(sKey, nNext, out _) > 0 || (nDepth > 0 && nInner == 0 && sKey[nNext] == ')' && IsClose(sKey, nNext))))
				{
					return nIndex;
				}

				nIndex = nNext - 1;

				continue;
			}

			if (cChar == '(')
			{
				++nInner;
			}
			else if (cChar == ')' && nInner > 0)
			{
				--nInner;
			}
			else if (cChar == ')' && nDepth > 0 && IsClose(sKey, nIndex))	// 괄호 안 검색어 끝
			{
				return nIndex;
			}
		}

		return sKey.Length;
	}

	private static bool IsClose(string sKey, int nPos)	// nPos 의 ')' 가 닫는 괄호 (뒤에 공백·')' 만 이어진 뒤 끝·연산자, 아님: 검색어 글자)
	{
		int nNext = nPos + 1;	// 뒤 글자

		while (nNext < sKey.Length && (char.IsWhiteSpace(sKey[nNext]) || sKey[nNext] == ')'))
		{
			++nNext;
		}

		return nNext >= sKey.Length || GetOperator(sKey, nNext, out _) > 0;
	}

	private static int GetOperator(string sKey, int nPos, out char cType)	// nPos 의 연산자 길이 (AND·OR·NOT 대소문자 무시 + 공백·여는 괄호, 0: 아님), cType: & | !
	{
		foreach ((string sWord, char cOp) in new[] { ("AND", '&'), ("OR", '|'), ("NOT", '!') })	// 대소문자 무시
		{
			int nNext = nPos + sWord.Length;	// 연산자 뒤 글자

			if (string.Compare(sKey, nPos, sWord, 0, sWord.Length, StringComparison.OrdinalIgnoreCase) == 0 && nNext < sKey.Length && (char.IsWhiteSpace(sKey[nNext]) || sKey[nNext] == '('))
			{
				cType = cOp;

				return sWord.Length;
			}
		}

		cType = ' ';

		return 0;
	}
}

class TabFile	// 파일 탭: 클라 감시 폴더 보기·파일 다운 (조각마다 UTF-8 변환 후 브라우저로) API (페이지 쪽: FileHTML)
{
	private const int				m_nDownLineMax		= 4 * Define.m_nDumpChunk;		// 다운 줄 끝 기다림 최대 (바이트, 넘음: 줄 중간이라도 변환)
	private static readonly byte[]	m_arrUtf8Bom		= { 0xEF, 0xBB, 0xBF };			// 다운 앞 UTF-8 BOM (편집기 인코딩 판별)
	private static Encoding?		m_kCp949;											// CP949 (처음 쓸 때 생성 → Main 의 코드 페이지 등록 뒤)
	private const int				m_nDownConcurrent	= 2;							// 동시 파일 다운 (전체, 세션마다 1건)
	private static readonly Gate	m_kGate				= new Gate(m_nDownConcurrent);	// 파일 다운 자리
	private readonly TCPServer		m_kListener;										// Client TCP 수신 (Main 이 넘김)

	public TabFile(TCPServer kListener)
	{
		m_kListener = kListener;
	}

	public void MapRoute(WebApplication WebApp, bool bFile)	// 라우트 등록 (파일 탭, bFile false: 파일 탭 꺼짐 → 파일 다운만 (서치 탭 공용))
	{
		WebApp.MapGet("/api/files/download", OnFileDownload);	// 파일 다운 (머신 1대, 브라우저로 그대로)

		if (bFile)
		{
			WebApp.MapGet("/api/files", OnFiles);					// 폴더 보기 (머신 1대, 파일 1번 창 트리)
		}
	}

	private async Task OnFileDownload(string? sMachine, string? sPath, HttpResponse kResponse)	// 파일 다운 (머신 1대): 클라에 FileRead 조각 (Define.m_nDumpChunk) 마다 요청 → UTF-8 변환 후 브라우저로 (첨부, 압축 안 함, 첫 조각 받으면 바로 응답 → ToolManager 시간 제한 회피)
	//   첫 조각 오류: 404 + 오류 문구, 중간 오류: 연결 끊음, 크기: 첫 조각 때 (이후 커져도 그 크기까지)
	//   인코딩: UTF-8 BOM + 줄마다 UTF-8·CP949 판별 → CP949 줄만 UTF-8 (파일 보기와 같음, 크기 바뀜 → 길이 없이 전송), UTF-16 BOM: 그대로 (길이 있음)
	{
		if (string.IsNullOrEmpty(sMachine) || string.IsNullOrEmpty(sPath) || sPath.Length > 1000)
		{
			kResponse.StatusCode = StatusCodes.Status400BadRequest;

			await kResponse.WriteAsync("다운 조건 오류");

			return;
		}

		string											sAddr	= Web.GetAddr(kResponse.HttpContext);					// 요청자 IP
		CancellationToken								kAbort	= kResponse.HttpContext.RequestAborted;					// 브라우저 다운 취소
		using IDisposable?								kGate	= await m_kGate.TryEnterAsync(kResponse.HttpContext);	// 세션 1건 → 전체 X건 (기다리는 중 취소: null)

		if (kGate == null)
		{
			return;
		}

		(byte[]? arrData, long nSize, string sError)	kChunk	= await API.RequestRead(m_kListener, "FileRead", sMachine, sPath, 0, Define.m_nDumpChunk);	// 첫 조각 (파일 크기 확인)

		if (kChunk.arrData == null)	// 첫 조각 오류 (미접속·허용 안 된 경로·읽기 실패): 응답 전 → 오류 문구
		{
			Log.Write(WARN_, $"파일 다운 실패: {sMachine} {sPath} ({kChunk.sError}, {sAddr})");

			kResponse.StatusCode = StatusCodes.Status404NotFound;

			await kResponse.WriteAsync($"다운 실패: {kChunk.sError}");

			return;
		}

		Microsoft.Net.Http.Headers.ContentDispositionHeaderValue kDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");	// 첨부 파일 (한글 파일명은 filename* 로)

		kDisposition.SetHttpFileName(Path.GetFileName(sPath));

		bool	bUtf16	= kChunk.arrData.Length >= 2 && ((kChunk.arrData[0] == 0xFF && kChunk.arrData[1] == 0xFE) || (kChunk.arrData[0] == 0xFE && kChunk.arrData[1] == 0xFF));	// UTF-16 BOM: 그대로
		byte[]	arrRest	= Array.Empty<byte>();	// 줄 끝 안 온 바이트 (다음 조각과 이어서 변환)

		kResponse.ContentType					= "application/octet-stream";
		kResponse.ContentLength					= bUtf16 ? kChunk.nSize : null;	// 브라우저 진행률 표시 (UTF-8 변환: 크기 바뀜 → 길이 없음)
		kResponse.Headers.ContentDisposition	= kDisposition.ToString();
		kResponse.Headers.CacheControl			= "no-store";

		Log.Write(INFO_, $"파일 다운: {sMachine} {sPath} ({kChunk.nSize:N0} bytes{(bUtf16 ? ", UTF-16" : "")}, {sAddr})");

		long nSize		= kChunk.nSize;	// 보낼 전체 크기 (원본)
		long nOffset	= 0;			// 보낸 크기 (원본)

		try
		{
			if (!bUtf16)
			{
				await kResponse.Body.WriteAsync(m_arrUtf8Bom, kAbort);
			}

			while (true)
			{
				bool bFirst = nOffset == 0;	// 첫 조각 (원본 UTF-8 BOM 건너뜀)

				nOffset += kChunk.arrData.Length;

				if (bUtf16)
				{
					await kResponse.Body.WriteAsync(kChunk.arrData, kAbort);
				}
				else
				{
					byte[]	arrData	= arrRest.Length == 0 ? kChunk.arrData : [.. arrRest, .. kChunk.arrData];							// 남은 줄 + 이번 조각
					int		nStart	= bFirst && arrData.AsSpan().StartsWith(m_arrUtf8Bom) ? m_arrUtf8Bom.Length : 0;						// 원본 BOM 건너뜀
					int		nEnd	= nOffset >= nSize || arrData.Length >= m_nDownLineMax ? arrData.Length : Array.LastIndexOf(arrData, (byte)'\n') + 1;	// 변환 끝 (마지막 줄 끝, 파일 끝·아주 긴 줄: 전부)

					nEnd = Math.Max(nEnd, nStart);

					Gate.Yield(kAbort);	// 변환 CPU 양보

					await kResponse.Body.WriteAsync(ToUtf8(arrData.AsSpan(nStart, nEnd - nStart)), kAbort);

					arrRest = arrData[nEnd..];
				}

				if (nOffset >= nSize)	// 다 보냄
				{
					break;
				}

				kChunk = await API.RequestRead(m_kListener, "FileRead", sMachine, sPath, nOffset, (int)Math.Min(Define.m_nDumpChunk, nSize - nOffset));

				if (kChunk.arrData == null || kChunk.arrData.Length == 0)	// 중간 오류 (끊김·삭제·크기 줄어듦): 연결 끊음
				{
					Log.Write(WARN_, $"파일 다운 중단: {sMachine} {sPath} ({nOffset:N0}/{nSize:N0} bytes, {(kChunk.sError == "" ? "읽은 바이트 없음" : kChunk.sError)}, {sAddr})");

					kResponse.HttpContext.Abort();

					return;
				}
			}
		}
		catch (OperationCanceledException) when (kAbort.IsCancellationRequested)	// 브라우저가 다운 취소
		{
			Log.Write(INFO_, $"파일 다운 취소: {sMachine} {sPath} ({nOffset:N0}/{nSize:N0} bytes, {sAddr})");
		}
	}

	private static byte[] ToUtf8(ReadOnlySpan<byte> kBytes)	// 다운 조각 → UTF-8 (전체 UTF-8: 그대로, 아니면 줄마다 판별 → CP949 줄만 변환, Client 파일 보기와 같은 규칙)
	{
		if (System.Text.Unicode.Utf8.IsValid(kBytes))	// 대부분의 UTF-8·영문 로그
		{
			return kBytes.ToArray();
		}

		Encoding		kCp949	= m_kCp949 ??= Encoding.GetEncoding(949);
		MemoryStream	kOut	= new MemoryStream(kBytes.Length + kBytes.Length / 2);	// 한글 2바이트 → 3바이트
		int				nPos	= 0;													// 다음 줄 시작

		while (nPos < kBytes.Length)
		{
			int					nIndex	= kBytes.Slice(nPos).IndexOf((byte)'\n');	// 줄 끝 (상대)
			int					nNext	= nIndex < 0 ? kBytes.Length : nPos + nIndex + 1;
			ReadOnlySpan<byte>	kLine	= kBytes.Slice(nPos, nNext - nPos);

			if (System.Text.Unicode.Utf8.IsValid(kLine))	// UTF-8·영문 줄: 그대로
			{
				kOut.Write(kLine);
			}
			else	// CP949 줄: UTF-8 로
			{
				kOut.Write(Encoding.UTF8.GetBytes(kCp949.GetString(kLine)));
			}

			nPos = nNext;
		}

		return kOut.ToArray();
	}

	private async Task<IResult> OnFiles(string? sMachine, string? sPath)	// 폴더 보기 (머신 1대): 클라에 Files → kResult 그대로 (sPath "": 클라 aLogFolder 목록, 오류: sError, 경로 허용 검사: 클라)
	{
		if (string.IsNullOrEmpty(sMachine) || (sPath ?? "").Length > 1000)
		{
			return API.MakeResult(sMachine, "조회 조건 오류", null);
		}

		(byte[]? arrResult, string sError) = await m_kListener.Request(sMachine, new Dictionary<string, object> { ["sType"] = "Files", ["sPath"] = sPath ?? "" }, Define.m_nViewWaitSec);

		return API.MakeResult(sMachine, sError, arrResult);
	}

	public static string FileHTML()	// 페이지 파일 탭 스크립트 (Web.IndexHtml 조립)
	{
		return """
		class File	// 파일 탭: 1번 창 클라 감시 폴더 트리 (펼치기·파일 보기·다운, this 없음 → 메서드를 이벤트 처리기로 바로 넘김)
		{
		    static FILE_VIEW_COUNT	= 300;		// 파일 1번 창 파일 보기 한 번에 읽는 줄 수
		    static FILE_VIEW_EXT	= /\.(log|txt|json|ini|bat|xml|config|md|lua)$/i;	// 볼 수 있는 파일 (Client IsViewFile 과 같음, 트리: 이 형식만)
		    static FILE_DATE_NAME	= /(?<!\d)(19|20)\d{2}[-_.]?(0[1-9]|1[0-2])[-_.]?(0[1-9]|[12]\d|3[01])/;	// 년월일 들어 있는 이름 (예: XLobby_20261022_09.log, 앞에 숫자 이어지면 아님) → 트리 내림차순
		    static FILE_SPLIT_MIN	= 120;		// 파일 1번 창 좌우 최소 폭 (px, 세로선 드래그)
		    static FILE_VIEW_MAX	= 20000;	// 파일 1번 창 파일 보기 최대 줄 수 (넘음: 반대쪽 지움, 빈 높이로 위치 유지)
		    static kFileView		= null;		// 보는 파일 { sMachine, sPath, nFirst: 읽은 첫 줄 (0: 아직, 1: 처음까지 읽음), nLast: 읽은 마지막 줄, nTotal: 전체 줄, bLoading }
		    static bFileLoaded		= false;	// 트리 한 번 만듦 (다른 탭에서 돌아오면 그대로, 파일 탭에서 다시 누르면 새로)
		    static FILE_VIEW_HINT	= kFileViewTitle.textContent;	// 파일 1번 창 오른쪽 처음 안내 (보는 파일 없음)
		    static FILE_POS			= { kScroll: kFileViewScroll, kBody: kFileViewBody, kPrev: kFilePrev, kShift: kFileShift, kNext: kFileNext, sInput: "FileShiftInput", nStep: File.FILE_VIEW_COUNT, funcState: () => File.kFileView };	// 파일 보기 포지션 버튼 (공통 ShowPos 등, 포지션 0: 파일 맨 아래)

		    static MakeFileNode(sKind, sMachine, sPath, sName, sInfo)	// 트리 한 줄 <li> (sKind: machine·dir·file, sPath: 전체 경로 (머신: ""), sInfo: 이름 뒤 회색 글자)
		    {
		        const kItem		= document.createElement("li");
		        const kNode		= document.createElement("div");								// 누르는 줄
		        const kToggle	= document.createElement("span");	// [+]·[-] (파일: 비움 → 안 보이고 자리만, 이름 줄 맞춤)
		        const kLabel	= document.createElement("span");	// 아이콘 (머신·폴더만, CSS Octicons)·이름·정보
		        const kName		= document.createElement("span");

		        kNode.className			= sKind === "file" && File.FILE_VIEW_EXT.test(sName) ? "node view" : "node";	// view: 누르면 오른쪽에 내용
		        kNode.dataset.kind		= sKind;
		        kNode.dataset.machine	= sMachine;
		        kNode.dataset.path		= sPath;
		        kLabel.className		= "label";
		        kName.textContent		= sName;
		        kToggle.className		= "tog";
		        kToggle.textContent		= sKind === "file" ? "" : "+";

		        if (sKind !== "file")	// 머신·폴더 아이콘 (파일: 없음)
		        {
		            kLabel.append(Object.assign(document.createElement("span"), { className: "ticon", textContent: "📁" }));
		        }

		        kLabel.append(kName);

		        if (sInfo !== "")
		        {
		            const kInfo = document.createElement("span");

		            kInfo.className		= "info";
		            kInfo.textContent	= sInfo;

		            kLabel.append(kInfo);
		        }

		        kNode.append(kToggle, kLabel);
		        kItem.append(kNode);

		        return kItem;
		    }

		    static MakeFileMessage(sText)	// 파일 1번 창 트리 안내 한 줄 <li> (오류·빈 폴더·일부만)
		    {
		        const kItem = document.createElement("li");

		        kItem.className		= "msg";
		        kItem.textContent	= sText;

		        return kItem;
		    }

		    static SortTreeNames(arrItem, funcName)	// 트리 순서: 이름 오름차순 (클라 순서), 년월일 든 것만 그 자리들 안에서 내림차순
		    {
		        const arrDate	= arrItem.filter(kItem => File.FILE_DATE_NAME.test(funcName(kItem))).reverse();	// 년월일 들어 있는 것 (내림차순)
		        let nDate		= 0;																				// 다음에 채울 순서

		        return arrItem.map(kItem => File.FILE_DATE_NAME.test(funcName(kItem)) ? arrDate[nDate++] : kItem);
		    }

		    static async ToggleFileNode(kNode)	// 트리 펼치기·접기 (펼칠 때마다 클라에 폴더 보기 요청 → 갱신, 머신: aLogFolder 목록, 접음: 하위 지움)
		    {
		        const kItem		= kNode.parentElement;
		        const kToggle	= kNode.querySelector(".tog");
		        let kChild		= kItem.querySelector(":scope > ul");	// 하위 목록 (null: 접힘)

		        if (kNode.dataset.kind === "file" || kNode.dataset.loading === "1")	// 파일·읽는 중
		        {
		            return;
		        }

		        if (kChild != null)	// 펼침: 접기 (하위 목록 지움 → 다시 펼치면 새로 읽음)
		        {
		            kChild.remove();

		            kToggle.textContent = "+";

		            return;
		        }

		        const sMachine	= kNode.dataset.machine;
		        const sPath		= kNode.dataset.path;

		        kNode.dataset.loading	= "1";
		        kToggle.textContent		= "…";
		        kChild					= document.createElement("ul");

		        try
		        {
		            const kResponse = await fetch("/api/files?" + new URLSearchParams({ sMachine: sMachine, sPath: sPath }), { cache: "no-store" });

		            if (!kResponse.ok)	// 경유 실패 등 (JSON 아님)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData		= await kResponse.json();
		            const kResult	= kData.kResult;
		            const sError	= kData.sError !== "" ? kData.sError : (kResult == null ? "결과 없음" : kResult.sError);

		            if (sError !== "")
		            {
		                throw new Error(sError);
		            }

		            for (const sDir of File.SortTreeNames(kResult.arrDir, sItem => sItem))	// 폴더 먼저 (날짜 폴더: 최신부터)
		            {
		                kChild.append(File.MakeFileNode("dir", sMachine, sPath === "" ? sDir : `${sPath}\\${sDir}`, sDir, ""));
		            }

		            const arrFile = kResult.arrFile.filter(kFile => File.FILE_VIEW_EXT.test(kFile.sName));	// 볼 수 있는 형식 파일만

		            for (const kFile of File.SortTreeNames(arrFile, kItem => kItem.sName))	// 파일 (시간별 로그: 최신부터, 수정 시각·크기)
		            {
		                kChild.append(File.MakeFileNode("file", sMachine, sPath === "" ? kFile.sName : `${sPath}\\${kFile.sName}`, kFile.sName, `${kFile.sTime} · ${Math.ceil(kFile.nSize / 1024).toLocaleString()} KB`));
		            }

		            if (kResult.bTruncated)
		            {
		                kChild.append(File.MakeFileMessage("… 일부만 표시 (폴더당 최대 10,000개)"));
		            }
		            else if (kResult.arrDir.length + kResult.arrFile.length === 0)
		            {
		                kChild.append(File.MakeFileMessage("(비어 있음)"));
		            }
		            else if (kResult.arrDir.length + arrFile.length === 0)	// 파일은 있지만 모두 숨긴 형식
		            {
		                kChild.append(File.MakeFileMessage("(해당 없음)"));
		            }
		        }
		        catch (kError)	// 요청 실패·허용 안 된 경로·폴더 읽기 실패 (접었다 펼치면 다시 요청)
		        {
		            console.error(kError);

		            kChild.replaceChildren(File.MakeFileMessage(`폴더 보기 실패: ${ErrorText(kError)}`));
		        }

		        kItem.append(kChild);

		        kToggle.textContent = "−";

		        delete kNode.dataset.loading;
		    }

		    static LoadFileTree()	// 파일 버튼: 선택 머신 감시 폴더 트리 (다른 탭에서 돌아옴: 그대로, 파일 탭에서 다시 누름: 처음부터)
		    {
		        if (Search.bSearching)	// 검색 중
		        {
		            return;
		        }

		        if (File.bFileLoaded && !kFileButton.classList.contains("cur"))	// 다른 탭에서 돌아옴: 마지막 데이터 그대로
		        {
		            ShowPanel("file");

		            return;
		        }

		        const sMachine		= GetMachineText();	// 머신 표시 글자
		        const arrMachine	= GetMachines();		// 보여줄 머신 (목록 순서)

		        if (arrMachine.length === 0)	// 접속 머신 없음
		        {
		            alert("접속된 머신이 없습니다. M 을 눌러 다시 조회해 주세요.");

		            return;
		        }

		        const arrNode		= arrMachine.map(sItem => File.MakeFileNode("machine", sItem, "", sItem, ""));			// 맨 위: 머신 목록

		        kFileTree.replaceChildren(...arrNode);
		        File.ResetFileView();	// 오른쪽 파일 보기도 처음으로 (다른 탭에 다녀와도 초기화)

		        kFileScroll.scrollTop	= 0;
		        kFileScroll.scrollLeft	= 0;
		        kFileTitle.textContent	= `[${sMachine} : File] 머신 ${arrMachine.length}대`;
		        File.bFileLoaded		= true;

		        ShowPanel("file");
		    }

		    static OnFileTreeClick(kEvent)	// 트리 클릭 (트리 하나에서 처리): 머신·폴더 펼치기·접기, 보기 파일: 오른쪽에 내용 (Shift·Ctrl·Alt+클릭: OnShiftFileClick·OnOpenClick 이 먼저)
		    {
		        const kNode = kEvent.target.closest("div.node");	// 누른 줄

		        if (kNode == null)
		        {
		            return;
		        }

		        if (kNode.dataset.kind !== "file")
		        {
		            File.ToggleFileNode(kNode);

		            return;
		        }

		        if (kNode.classList.contains("view"))	// 보기 파일: 선택 표시 후 오른쪽에 마지막 FILE_VIEW_COUNT 줄
		        {
		            kFileTree.querySelector("div.node.sel")?.classList.remove("sel");
		            kNode.classList.add("sel");

		            File.OpenFileView(kNode.dataset.machine, kNode.dataset.path, kNode);
		        }
		    }

		    static ResetFileView()	// 오른쪽 파일 보기 비우기 (읽는 중 응답 버림, 처음 안내)
		    {
		        File.kFileView = null;

		        File.ClearFileView();

		        kFileViewTitle.textContent = File.FILE_VIEW_HINT;

		        ShowPos(File.FILE_POS);	// 포지션 버튼 끔
		    }

		    static ClearFileView()	// 파일 1번 창 오른쪽 파일 보기 내용·위·아래 빈 높이·스크롤 비우기
		    {
		        kFileViewBody.replaceChildren();

		        kFileViewBody.style.paddingTop		= "";
		        kFileViewBody.style.paddingBottom	= "";
		        kFileViewScroll.scrollTop			= 0;
		        kFileViewScroll.scrollLeft			= 0;
		    }

		    static OpenFileView(sMachine, sPath, kNode)	// 오른쪽 파일 보기 열기: 마지막 FILE_VIEW_COUNT 줄 → 맨 아래 (kNode: 트리 줄 → 수정 시각·크기 갱신)
		    {
		        File.kFileView = { sMachine: sMachine, sPath: sPath, kNode: kNode, nAnchor: 0, nFirst: 0, nLast: 0, nTotal: 0, bLoading: false, bError: false };	// nAnchor: 포지션 0 줄 (첫 읽기 뒤 마지막 줄)

		        File.ClearFileView();
		        ShowPos(File.FILE_POS);	// 포지션 버튼 끔 (읽은 뒤 켬)
		        File.LoadFileView(0, File.FILE_VIEW_COUNT, 0);
		    }

		    static FillFileView()	// 파일 1번 창 오른쪽 스크롤: 보이는 곳 줄 읽기 (FillLines, FILE_VIEW_COUNT 줄씩)
		    {
		        FillLines(kFileViewScroll, kFileViewBody, File.kFileView, File.FILE_VIEW_COUNT, File.LoadFileView);
		    }

		    static async LoadFileView(nStart, nCount, nDirection)	// 오른쪽 nStart 줄부터 nCount 줄 읽기 (nStart 0: 마지막, nDirection 0: 새로·-1: 위에 붙임·1: 아래에 붙임·2: 그 위치로 바꿈, 그 사이 다른 파일: 버림)
		    {
		        const kTarget = File.kFileView;	// 요청한 파일

		        if (kTarget == null || kTarget.bLoading)
		        {
		            return;
		        }

		        kTarget.bLoading = true;

		        if (nDirection === 0)
		        {
		            kFileViewTitle.textContent = `${kTarget.sMachine} : ${kTarget.sPath} (불러오는 중)`;
		        }

		        try
		        {
		            const kParam	= new URLSearchParams({ sMachine: kTarget.sMachine, sPath: kTarget.sPath, nStart: nStart, nCount: nCount });
		            const kResponse	= await fetch("/api/view?" + kParam, { cache: "no-store" });

		            if (!kResponse.ok)	// 경유 실패 등 (JSON 아님)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData = await kResponse.json();

		            if (kTarget !== File.kFileView)	// 그 사이 다른 파일을 엶
		            {
		                return;
		            }

		            const kResult	= kData.kResult;
		            const sError	= kData.sError !== "" ? kData.sError : (kResult == null ? "결과 없음" : kResult.sError);

		            if (sError !== "")
		            {
		                throw new Error(sError);
		            }

		            const kFragment		= document.createDocumentFragment();	// 붙일 줄
		            const nCountRead	= kResult.arrText.length;				// 읽은 줄 수

		            kResult.arrText.forEach((sText, nIndex) =>
		            {
		                const kLine	= document.createElement("span");
		                const kNo	= document.createElement("span");

		                kLine.className	= "line";
		                kNo.className	= "lno";
		                kNo.textContent	= String(kResult.nStart + nIndex).padStart(7, " ") + " : ";

		                kLine.append(kNo, sText);
		                kFragment.append(kLine);
		            });

		            kTarget.nTotal = kResult.nTotal;

		            if (nDirection < 0)	// 위에 붙임 (보던 위치 그대로)
		            {
		                kFileViewBody.prepend(kFragment);

		                kTarget.nFirst = nCountRead > 0 ? kResult.nStart : 1;

		                TrimLines(kFileViewBody, kTarget, File.FILE_VIEW_MAX, false);	// 최대 넘음: 아래쪽 지움
		            }
		            else if (nDirection === 1)	// 아래에 붙임
		            {
		                kFileViewBody.append(kFragment);

		                kTarget.nLast += nCountRead;

		                TrimLines(kFileViewBody, kTarget, File.FILE_VIEW_MAX, true);	// 최대 넘음: 위쪽 지움
		            }
		            else	// 새로·그 위치로 바꿈
		            {
		                kFileViewBody.replaceChildren(kFragment);

		                kTarget.nFirst	= nCountRead > 0 ? kResult.nStart : 1;
		                kTarget.nLast	= nCountRead > 0 ? kResult.nStart + nCountRead - 1 : 0;
		            }

		            SetLineSpace(kFileViewBody, kTarget);	// 위·아래 빈 높이 (스크롤 바: 파일 전체)

		            if (nDirection === 0)	// 새로: 맨 아래로, 트리의 수정 시각·크기 갱신
		            {
		                const kInfo = kTarget.kNode?.querySelector(".info");	// 트리 줄 정보 (트리를 다시 만들었으면 화면에 없음 → 무시)

		                kFileViewScroll.scrollTop = kFileViewScroll.scrollHeight;

		                kTarget.nAnchor = kTarget.nLast;	// 포지션 0 = 파일 맨 아래 (보던 그대로)

		                SetPosStart(File.FILE_POS);

		                if (kInfo != null && typeof kResult.sTime === "string" && kResult.sTime !== "")	// 예전 Client: sTime 없음 → 그대로
		                {
		                    kInfo.textContent = `${kResult.sTime} · ${Math.ceil(kResult.nSize / 1024).toLocaleString()} KB`;
		                }
		            }

		            kFileViewTitle.textContent = `${kTarget.sMachine} : ${kTarget.sPath} (${kTarget.nLast > 0 ? `${kTarget.nFirst}~${kTarget.nLast}` : "-"} / 전체 ${kTarget.nTotal.toLocaleString()}줄)`;

		            ShowPos(File.FILE_POS);	// 포지션: 붙인 줄·스크롤 위치 기준

		            setTimeout(File.FillFileView, 0);	// 읽는 사이 스크롤로 옮긴 곳 (읽는 중 끝난 뒤)
		        }
		        catch (kError)	// 요청 실패·허용 안 된 경로·읽기 실패: 더 읽지 않음 (다시 누름: 처음부터)
		        {
		            console.error(kError);

		            kTarget.bError = true;

		            if (kTarget === File.kFileView)
		            {
		                kFileViewTitle.textContent = `${kTarget.sMachine} : ${kTarget.sPath} (보기 실패: ${ErrorText(kError)})`;
		            }
		        }
		        finally
		        {
		            kTarget.bLoading = false;
		        }
		    }

		}
		""";
	}
}

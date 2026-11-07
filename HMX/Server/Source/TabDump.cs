class TabDump	// 덤프 탭: 덤프 목록 (여러 머신 동시)·다운 (Server Dump 폴더 zip)·zip 받기·삭제 API (페이지 쪽: DumpHTML)
{
	private const int	m_nDumpResultMs		= 60000;			// 끝난 다운 결과 보관 (ms, 지남: 버리고 zip 유무로 판단, 조회 안 한 것도 정리)
	private const int	m_nDownConcurrent	= 2;				// 동시 덤프 다운 (전체, 세션마다 1건)

	private static readonly Gate	m_kGate	= new Gate(m_nDownConcurrent);	// 덤프 다운 자리 (저장 작업 안에서 잡음)

	private readonly TCPServer		m_kListener;		// Client TCP 수신 (Main 이 넘김)

	private readonly ConcurrentDictionary<string, DumpSave>	m_hmapDumpSave = new ConcurrentDictionary<string, DumpSave>(StringComparer.OrdinalIgnoreCase);	// 다운 중·끝난 덤프 zip → 진행·결과 (같은 파일 동시 다운 방지, 끝난 것은 다음 조회에 알리고 해제)

	public TabDump(TCPServer kListener)
	{
		m_kListener = kListener;

		try	// 앞 실행에서 남은 받는 중 zip (서비스 중지·종료로 끝나지 못함) 정리
		{
			string sDumpDir = Path.Combine(GetBaseDir(), "Dump");

			if (Directory.Exists(sDumpDir))
			{
				foreach (string sTemp in Directory.EnumerateFiles(sDumpDir, "*.zip.tmp", SearchOption.AllDirectories))
				{
					File.Delete(sTemp);
				}
			}
		}
		catch (Exception kException)	// 사용 중·권한 없음: 다음 실행 때 다시
		{
			Log.Write(WARN_, $"덤프 임시 파일 정리 실패: {kException.Message}");
		}
	}

	public void MapRoute(WebApplication WebApp)	// 라우트 등록 (덤프 탭)
	{
		WebApp.MapPost("/api/dump", OnDump);					// 덤프 목록 (여러 머신 동시, 본문 JSON)
		WebApp.MapPost("/api/dump/download", OnDumpDownload);	// 덤프 다운 (머신 1대 → Server Dump 폴더 zip, 본문 JSON)
		WebApp.MapGet("/api/dump/zip", OnDumpZip);				// 다운한 덤프 zip 받기 (브라우저 로컬 저장)
		WebApp.MapPost("/api/dump/delete", OnDumpDelete);		// 덤프 삭제 (머신 1대, 본문 JSON)
	}

	private IResult OnDump(DumpParam kParam)	// 덤프 목록: 머신마다 클라에 Dump → 끝난 순서대로 한 줄씩 {nIndex, sMachine, sError, kResult} (NDJSON)
	{
		string[] arrMachine = kParam.arrMachine ?? Array.Empty<string>();	// 조회할 머신 (nIndex = 이 순서)

		if (arrMachine.Length > Define.m_nMachineMax)	// 머신 수 초과: 조기 반환
		{
			return Results.BadRequest($"머신 수 초과 (최대 {Define.m_nMachineMax})");
		}

		return Results.Stream(async (Stream kBody) =>
		{
			Task<(int nIndex, byte[]? arrResult, string sError)>[] arrTask = arrMachine.Select((sMachine, nIndex) => RequestDump(nIndex, sMachine)).ToArray();	// 모든 머신 동시 요청

			await foreach (Task<(int nIndex, byte[]? arrResult, string sError)> kTask in Task.WhenEach(arrTask))	// 끝난 순서대로 전송
			{
				(int nIndex, byte[]? arrResult, string sError) = await kTask;

				await API.WriteSearchLine(kBody, nIndex, arrMachine[nIndex], sError, arrResult);
			}
		}, "application/x-ndjson; charset=utf-8");
	}

	private async Task<(int nIndex, byte[]? arrResult, string sError)> RequestDump(int nIndex, string sMachine)	// 머신 1대 덤프 목록 요청 (nIndex: 결과 순서)
	{
		if (string.IsNullOrEmpty(sMachine))
		{
			return (nIndex, null, "조회 조건 오류");
		}

		(byte[]? arrResult, string sError) = await m_kListener.Request(sMachine, new Dictionary<string, object> { ["sType"] = "Dump" }, Define.m_nDumpWaitSec);

		return (nIndex, MarkDumpZip(sMachine, arrResult), sError);
	}

	private static byte[]? MarkDumpZip(string sMachine, byte[]? arrResult)	// 덤프 목록 결과에 Server zip 유무 추가 (arrFile[].bZip, 결과 없음·형식 오류: 그대로)
	{
		if (arrResult == null)
		{
			return null;
		}

		try
		{
			JsonNode? kRoot = JsonNode.Parse(arrResult);	// 목록은 최대 m_nDumpMax 건 (검색 결과와 달리 작음 → 다시 직렬화)

			if (kRoot?["arrFile"] is not JsonArray arrFile)	// 오류 결과 (목록 없음)
			{
				return arrResult;
			}

			foreach (JsonObject kFile in arrFile.OfType<JsonObject>())
			{
				string sZip = GetDumpZip(sMachine, kFile["sPath"]?.GetValue<string>() ?? "");	// 다운 때와 같은 zip 경로

				kFile["bZip"] = sZip != "" && File.Exists(sZip);
			}

			return JsonSerializer.SerializeToUtf8Bytes(kRoot, Define.m_kJsonOption);
		}
		catch (Exception)	// 형식 오류: 그대로 (bZip 없음 → 압축)
		{
			return arrResult;
		}
	}

	private IResult OnDumpDownload(DumpParam kParam, HttpRequest kRequest)	// 덤프 다운 시작·진행 조회: 바로 응답 (페이지 재호출 → ToolManager·IIS 요청 시간 제한 회피), 저장: SaveDump 가 따로 끝까지 (이미 있음: 바로 완료)
	{
		string sMachine	= kParam.sMachine ?? "";
		string sPath	= kParam.sPath ?? "";
		string sZip		= GetDumpZip(sMachine, sPath);	// 저장할 zip (조건 오류: "")

		if (sZip == "")
		{
			return MakeDumpSave(sMachine, "다운 조건 오류", sZip, null);
		}

		foreach (KeyValuePair<string, DumpSave> kPair in m_hmapDumpSave)	// 끝난 지 오래된 결과 정리 (조회 안 한 것 메모리 잔류 방지)
		{
			if (kPair.Value.m_bEnd && Environment.TickCount64 - kPair.Value.m_nEndTick > m_nDumpResultMs)
			{
				m_hmapDumpSave.TryRemove(kPair);
			}
		}

		if (m_hmapDumpSave.TryGetValue(sZip, out DumpSave? kSave))	// 다운 중·끝남
		{
			if (!kSave.m_bEnd)	// 다운 중: 진행
			{
				return MakeDumpSave(sMachine, "", sZip, kSave);
			}

			m_hmapDumpSave.TryRemove(new KeyValuePair<string, DumpSave>(sZip, kSave));	// 끝난 결과는 한 번만 알림

			if (Environment.TickCount64 - kSave.m_nEndTick <= m_nDumpResultMs)	// 방금 끝남: 결과 (오래된 결과는 버리고 아래에서 다시 판단)
			{
				return MakeDumpSave(sMachine, kSave.m_sError, sZip, kSave);
			}
		}

		if (File.Exists(sZip))	// 이미 받은 덤프: 바로 완료
		{
			return MakeDumpSave(sMachine, "", sZip, null);
		}

		kSave = new DumpSave();

		if (m_hmapDumpSave.TryAdd(sZip, kSave))	// 다운 시작 (같은 파일 동시 요청: 먼저 등록한 쪽만 저장, 나머지는 다음 조회에서 그 진행)
		{
			_ = SaveDump(sMachine, sPath, sZip, Web.GetAddr(kRequest.HttpContext), Gate.GetSession(kRequest.HttpContext), kSave);	// 페이지와 무관하게 끝까지 저장 (예외는 SaveDump 안에서 처리)

			if (kSave.m_bEnd)	// 바로 끝남 (미접속 등): 결과는 이번에 알림
			{
				m_hmapDumpSave.TryRemove(new KeyValuePair<string, DumpSave>(sZip, kSave));
			}
		}

		return MakeDumpSave(sMachine, "", sZip, kSave);
	}

	private async Task SaveDump(string sMachine, string sPath, string sZip, string sAddr, string sSession, DumpSave kSave)	// 덤프 조각 차례로 받아 .tmp zip 에 압축 → 다 받으면 sZip 으로 이름 변경 (실패: 임시 파일 삭제), 진행·결과: kSave, 세션 1건 → 전체 X건 (기다림: 진행 0)
	{
		string	sTemp	= sZip + ".tmp";				// 받는 중인 zip (완료 전에는 sZip 없음 → 이미 받음 판정 안 됨)
		long	nSize	= 0;							// 원본 크기 (첫 조각 때)
		long	nOffset	= 0;							// 받은 크기
		string	sError	= "";							// 오류 (없음: "")
		long	nBegin	= Stopwatch.GetTimestamp();

		try
		{
			using IDisposable kGate = await m_kGate.EnterAsync(sSession, CancellationToken.None);

			(byte[]? arrData, long nSize, string sError) kChunk = await API.RequestRead(m_kListener, "DumpRead", sMachine, sPath, 0, Define.m_nDumpChunk);	// 첫 조각 (파일 크기 확인)

			if (kChunk.arrData == null)	// 미접속·허용 안 된 경로·읽기 실패
			{
				throw new RelayException(kChunk.sError);
			}

			nSize			= kChunk.nSize;
			kSave.m_nSize	= nSize;

			Directory.CreateDirectory(Path.GetDirectoryName(sZip)!);	// Dump\머신 (없으면 생성)

			Log.Write(INFO_, $"덤프 다운 시작: {sMachine} {sPath} ({nSize:N0} bytes, {sAddr})");

			using (FileStream kFile = new FileStream(sTemp, FileMode.Create, FileAccess.Write, FileShare.None))
			using (ZipArchive kZip = new ZipArchive(kFile, ZipArchiveMode.Create))
			{
				using Stream kEntry = kZip.CreateEntry(Path.GetFileName(sPath), CompressionLevel.Optimal).Open();	// zip 안 덤프 (압축: Server, 게임 서버 CPU 안 씀)

				while (true)
				{
					Gate.Yield(CancellationToken.None);	// 압축 CPU 양보

					await kEntry.WriteAsync(kChunk.arrData);

					nOffset			+= kChunk.arrData.Length;
					kSave.m_nOffset	= nOffset;

					if (nOffset >= nSize)	// 다 받음 (첫 조각 때 크기까지만)
					{
						break;
					}

					kChunk = await API.RequestRead(m_kListener, "DumpRead", sMachine, sPath, nOffset, (int)Math.Min(Define.m_nDumpChunk, nSize - nOffset));

					if (kChunk.arrData == null || kChunk.arrData.Length == 0)	// 중간 오류 (끊김·삭제·크기 줄어듦)
					{
						throw new RelayException(kChunk.sError == "" ? "No Data" : kChunk.sError);
					}
				}
			}

			File.Move(sTemp, sZip, true);
		}
		catch (Exception kException)	// 조각 요청·파일 쓰기 실패
		{
			sError = GetErrorText(kException, $"덤프 다운 {sMachine} {sPath}");

			try
			{
				File.Delete(sTemp);
			}
			catch (Exception)	// 폴더 없음 등: 임시 파일 없음
			{
			}

			DeleteEmptyDumpDir(sZip);	// 이번에 만든 Dump\머신 폴더가 비면 삭제
		}

		Log.Write(sError == "" ? INFO_ : WARN_, $"덤프 다운 {(sError == "" ? "완료" : "실패")}: {sMachine} {sPath} → {sZip} ({nOffset:N0}/{nSize:N0} bytes, {(long)Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds}ms, {sAddr}){(sError == "" ? "" : $" ({sError})")}");

		kSave.m_sError		= sError;
		kSave.m_nEndTick	= Environment.TickCount64;
		kSave.m_bEnd		= true;	// 마지막에 (조회 쪽은 m_bEnd 를 본 뒤 오류·시각을 읽음), 해제는 다음 조회
	}

	private static IResult OnDumpZip(string? sMachine, string? sPath, HttpRequest kRequest)	// 다운한 덤프 zip 을 브라우저로 전송 (첨부 파일, Dump\머신\덤프 파일명.zip 만, 없음·다운 중: 404)
	{
		string sZip = GetDumpZip(sMachine ?? "", sPath ?? "");	// 다운한 zip (조건 오류: "")

		if (sZip == "" || !File.Exists(sZip))	// 조건 오류·아직 다운 안 함 (다운 중은 .tmp 만 있음)
		{
			return Results.NotFound("다운한 zip 없음");
		}

		Log.Write(INFO_, $"덤프 zip 받기: {sZip} ({Web.GetAddr(kRequest.HttpContext)})");

		return Results.File(sZip, "application/zip", Path.GetFileName(sZip));
	}

	private static string GetDumpZip(string sMachine, string sPath)	// 덤프 저장 zip: 기준 폴더 하위 Dump\머신\덤프 파일명.zip (머신·경로 오류: "")
	{
		string sName = Path.GetFileName(sPath);	// 덤프 파일명 (폴더 제외 → 기준 폴더 밖 경로 불가)

		if (sMachine == "" || sMachine.Length > 255 || sMachine.Trim('.') == "" || sMachine.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || sPath.Length > 1000 || sName.Length <= 4 || !sName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))	// 머신명이 폴더명으로 안 됨·덤프 아님
		{
			return "";
		}

		string sDumpDir	= Path.GetFullPath(Path.Combine(GetBaseDir(), "Dump"));								// 덤프 저장 폴더
		string sZip		= Path.GetFullPath(Path.Combine(sDumpDir, sMachine, Path.ChangeExtension(sName, ".zip")));	// 저장할 zip

		return string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(sZip)), sDumpDir, StringComparison.OrdinalIgnoreCase) ? sZip : "";	// Dump\머신\ 바로 아래만 (".. " 처럼 정리되면 밖이 되는 이름: 제외)
	}

	private static IResult MakeDumpSave(string sMachine, string sError, string sZip, DumpSave? kSave)	// 덤프 다운 응답 {sMachine, sError, sZip (전체 경로), bDone, bExist (이미 있음), nSize·nOffset (원본·받은 바이트), nZipSize} (kSave null: 다운 안 함)
	{
		if (sError == "" && kSave != null && kSave.m_bEnd)	// 다운 끝: 그 오류 (조회 사이에 끝남·바로 실패)
		{
			sError = kSave.m_sError;
		}

		bool bDone		= sError == "" && (kSave == null || kSave.m_bEnd);					// 완료 (이미 있음·다운 끝)
		long nZipSize	= bDone && File.Exists(sZip) ? new FileInfo(sZip).Length : 0;		// zip 크기 (완료 전·오류: 0)

		return Results.Json(new { sMachine, sError, sZip, bDone, bExist = bDone && kSave == null, nSize = kSave?.m_nSize ?? 0, nOffset = kSave?.m_nOffset ?? 0, nZipSize });
	}

	private async Task<IResult> OnDumpDelete(DumpParam kParam, HttpRequest kRequest)	// 덤프 삭제 (머신 1대): 클라에 DumpDelete → 성공: 다운 zip·빈 Dump\머신 폴더도 삭제 → {sMachine, sError, sZipError, kResult} (요청자 IP 로그)
	{
		string sMachine	= kParam.sMachine ?? "";
		string sPath	= kParam.sPath ?? "";

		if (sMachine == "" || sPath == "" || sPath.Length > 1000)
		{
			return API.MakeResult(sMachine, "삭제 조건 오류", null);
		}

		(byte[]? arrResult, string sError) = await m_kListener.Request(sMachine, new Dictionary<string, object> { ["sType"] = "DumpDelete", ["sPath"] = sPath }, Define.m_nDumpWaitSec);	// 경로 허용 검사는 클라에서

		string	sZip		= GetDumpZip(sMachine, sPath);	// 다운 zip (조건 오류: "")
		string	sZipError	= "";							// zip 삭제 오류 (없음·zip 없음: "")
		bool	bZipDeleted	= false;						// zip 삭제함
		bool	bDirDeleted	= false;						// 빈 Dump\머신 폴더 삭제함

		if (sError == "" && GetResultError(arrResult) == "" && sZip != "")	// 덤프 삭제 성공: 다운 zip·빈 폴더 삭제
		{
			try
			{
				if (File.Exists(sZip))
				{
					File.Delete(sZip);

					bZipDeleted = true;
				}
			}
			catch (Exception kException)	// 사용 중·권한 없음 (덤프는 이미 삭제됨)
			{
				sZipError = GetErrorText(kException, $"덤프 zip 삭제 {sZip}");
			}

			bDirDeleted = DeleteEmptyDumpDir(sZip);
		}

		Log.Write(NOTI_, $"덤프 삭제 요청: {sMachine} {sPath} ({Web.GetAddr(kRequest.HttpContext)}){(sError == "" ? "" : $" (오류: {sError})")}{(bZipDeleted ? $" (zip 삭제: {sZip})" : "")}{(sZipError == "" ? "" : $" (zip 삭제 실패: {sZip}, {sZipError})")}{(bDirDeleted ? $" (빈 폴더 삭제: {Path.GetDirectoryName(sZip)})" : "")}");

		return Results.Stream((Stream kBody) => API.WriteResult(kBody, new { sMachine, sError, sZipError }, arrResult), "application/json; charset=utf-8");
	}

	private static bool DeleteEmptyDumpDir(string sZip)	// zip 이 있던 Dump\머신 폴더 안에 파일·폴더가 없으면 삭제 (반환: 삭제함, 남은 파일 있음·실패: false)
	{
		string? sDir = Path.GetDirectoryName(sZip);	// Dump\머신

		try
		{
			if (sDir == null || !Directory.Exists(sDir) || Directory.EnumerateFileSystemEntries(sDir).Any())	// 폴더 없음·남은 파일 있음
			{
				return false;
			}

			Directory.Delete(sDir);	// 하위 포함 안 함 (그 사이 다른 다운이 파일을 만들면 실패 → 유지)

			return true;
		}
		catch (Exception)	// 다른 다운이 사용 중 등: 유지
		{
			return false;
		}
	}

	private static string GetResultError(byte[]? arrResult)	// 클라 결과의 sError (없음: "", 결과 없음·형식 오류: 오류 문구)
	{
		if (arrResult == null)
		{
			return "결과 없음";
		}

		try
		{
			using JsonDocument kDoc = JsonDocument.Parse(arrResult);

			return kDoc.RootElement.GetProperty("sError").GetString() ?? "";
		}
		catch (Exception)	// 결과 형식 오류
		{
			return "결과 형식 오류";
		}
	}

	public static string DumpHTML()	// 페이지 덤프 탭 스크립트 (Web.IndexHtml 조립)
	{
		return """
		class Dump	// 덤프 탭: 1번 창 덤프 파일 (목록·압축·zip 다운·삭제, this 없음 → 메서드를 이벤트 처리기로 바로 넘김)
		{
		    static arrDump			= [];		// 덤프 파일 { sMachine, sPath, nSize, sTime, bZip } (수정 시각 내림차순, 같으면 머신 목록 순서 → 경로 순)
		    static kDumpInfo		= null;		// 덤프 1번 창 제목 정보 { sCondition, nMachine, nMs, arrNotice }
		    static bDumpLoading		= false;	// 덤프 목록 조회·전체 삭제 중
		    static DUMP_POLL_MS		= 1000;		// 덤프 다운 진행 조회 주기 (ms)
		    static mapBusy			= new Map();	// 진행 중 덤프 (머신·경로 → 진행 팝업 글)
		    static nBusyPopup		= 0;			// 진행 팝업 지연 타이머 (X초 이상 걸릴 때만, 0: 없음)

		    static ShowDumpTitle()	// 덤프 1번 창 제목: 조건 · 덤프 수 (머신 수) · 조회 시간 · 머신별 오류
		    {
		        kDumpTitle.textContent = `${Dump.kDumpInfo.sCondition} 덤프 파일: ${Dump.arrDump.length.toLocaleString()}개 (머신 ${Dump.kDumpInfo.nMachine}대) · 조회 시간: ${Dump.kDumpInfo.nMs} ms${Dump.kDumpInfo.arrNotice.length > 0 ? " · " + Dump.kDumpInfo.arrNotice.join(" / ") : ""}`;
		    }

		    static RenderDump()	// 덤프 1번 창 그리기 (No.·Machine·File·Date·Size·Folder·Ac., 클릭: kDumpBody 한 곳에서 처리)
		    {
		        const kFragment = document.createDocumentFragment();

		        Dump.arrDump.forEach((kDump, nIndex) =>
		        {
		            const nSlash	= kDump.sPath.lastIndexOf("\\");	// 폴더·파일명 구분
		            const kRow		= document.createElement("tr");

		            kRow.dataset.index = nIndex;

		            AddCell(kRow, String(nIndex + 1), "num");
		            AddCell(kRow, kDump.sMachine, "nowrap");
		            AddCell(kRow, kDump.sPath.slice(nSlash + 1), "nowrap");
		            AddCell(kRow, kDump.sTime, "nowrap");	// 파일 날짜: 수정 시각 (KST)
		            AddCell(kRow, `${Math.ceil(kDump.nSize / 1024).toLocaleString()} KB`, "num").title = `${kDump.nSize.toLocaleString()} bytes`;	// 파일 크기: KB (탐색기처럼 올림), 마우스: 바이트
		            AddCell(kRow, kDump.sPath.slice(0, nSlash), "nowrap");	// 폴더: 전체 경로
		            const kAction	= AddCell(kRow, "", "fileact");			// Ac.: 다운(압축)·삭제 아이콘 (맨 오른쪽 고정)
		            const kDown		= document.createElement("button");	// 다운 (zip 없음: 압축, 흐림)
		            const kDelete	= document.createElement("button");	// 삭제

		            kDown.type				= "button";
		            kDown.className			= "qdown";
		            kDown.dataset.action	= "down";
		            kDown.ariaLabel			= "다운";
		            kDelete.type			= "button";
		            kDelete.className		= "qdel";
		            kDelete.dataset.action	= "delete";
		            kDelete.ariaLabel		= "삭제";
		            kDelete.title			= "삭제(dmp, zip)";
		            kAction.append(kDown, kDelete);

		            Dump.SetDumpButton(kDown, kDump.bZip, kDump.sBusy ?? null);	// zip 있음: 다운, 없음: 압축, 진행 중: 진행 글 (다시 그려도 유지)

		            kFragment.append(kRow);
		        });

		        kDumpBody.replaceChildren(kFragment);

		        kDumpDeleteAll.disabled = Dump.bDumpLoading || Dump.arrDump.length === 0;	// 파일 없음·조회·삭제 중: 비활성
		    }

		    static FitDumpHeight()	// 덤프 1번 창 높이: 브라우저 창 아래 끝에 맞춤 (처음·창 크기 바뀔 때)
		    {
		        FitHeight(kDumpScroll, kDumpPanel);
		    }

		    static async LoadDump()	// 덤프 버튼: 선택 머신 덤프 목록 동시 조회 (다른 탭에서 돌아옴: 마지막 목록, 덤프 탭에서 다시 누름: 다시 조회)
		    {
		        if (Search.bSearching)	// 검색 중
		        {
		            return;
		        }

		        if (Dump.kDumpInfo != null && !kDumpButton.classList.contains("cur"))	// 다른 탭에서 돌아옴: 마지막 데이터 그대로
		        {
		            ShowPanel("dump");

		            return;
		        }

		        if (Dump.bDumpLoading)	// 조회 중
		        {
		            return;
		        }

		        const sMachine		= GetMachineText();	// 머신 표시 글자
		        const arrMachine	= GetMachines();		// 조회할 머신 (목록 순서)

		        if (arrMachine.length === 0)	// 접속 머신 없음
		        {
		            alert("접속된 머신이 없습니다. M 을 눌러 다시 조회해 주세요.");

		            return;
		        }

		        Dump.bDumpLoading		= true;
		        kDumpButton.disabled	= true;
		        Dump.arrDump			= [];
		        Dump.kDumpInfo		= { sCondition: `[${sMachine} : Dump]`, nMachine: arrMachine.length, nMs: 0, arrNotice: [] };

		        ShowPanel("dump");
		        Dump.RenderDump();

		        kDumpTitle.textContent	= `${Dump.kDumpInfo.sCondition} 조회 중: 머신 ${arrMachine.length}대`;
		        kDumpScroll.scrollTop	= 0;

		        const nBegin	= performance.now();				// 조회 시작 시각
		        const arrData	= new Array(arrMachine.length);	// 머신별 결과 (서버는 끝난 순서로 줌)

		        try
		        {
		            const kResponse = await fetch("/api/dump", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ arrMachine: arrMachine }), cache: "no-store" });	// 한 줄 = 머신 1대

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            for (const sLine of (await kResponse.text()).split("\n"))
		            {
		                const kData = sLine === "" ? null : JSON.parse(sLine);

		                if (kData != null && kData.nIndex >= 0 && kData.nIndex < arrMachine.length)
		                {
		                    arrData[kData.nIndex] = kData;
		                }
		            }
		        }
		        catch (kError)	// 요청 실패 (받은 머신까지만, 나머지는 요청 실패)
		        {
		            console.error(kError);
		        }

		        arrMachine.forEach((sItem, nIndex) =>	// 머신 순서대로 추가
		        {
		            const kData		= arrData[nIndex];
		            const kResult	= kData?.kResult;
		            const sError	= kData == null ? "요청 실패" : (kData.sError !== "" ? kData.sError : (kResult == null ? "결과 없음" : kResult.sError));

		            if (sError !== "")
		            {
		                Dump.kDumpInfo.arrNotice.push(`${sItem}: ${sError}`);

		                return;
		            }

		            for (const kFile of kResult.arrFile)
		            {
		                Dump.arrDump.push({ sMachine: sItem, sPath: kFile.sPath, nSize: kFile.nSize, sTime: kFile.sTime, bZip: kFile.bZip === true });
		            }

		            if (kResult.bTruncated)	// 머신당 최대 건수 넘음
		            {
		                Dump.kDumpInfo.arrNotice.push(`${sItem}: 최대 건수 초과로 일부만`);
		            }
		        });

		        Dump.kDumpInfo.nMs = Math.round(performance.now() - nBegin);

		        Dump.arrDump.sort((kLeft, kRight) => kLeft.sTime < kRight.sTime ? 1 : (kLeft.sTime > kRight.sTime ? -1 : 0));	// 수정 시각 내림차순 (문자열 비교, 같음: 머신 목록·경로 순)

		        Dump.RenderDump();
		        Dump.ShowDumpTitle();

		        Dump.bDumpLoading		= false;
		        kDumpButton.disabled	= Search.bSearching;
		        kDumpDeleteAll.disabled	= Dump.arrDump.length === 0;	// 파일 없음: 비활성
		    }

		    static async SaveDumpZip(kDump)	// Server zip 을 끝까지 받은 뒤 로컬 저장 (받기 끝 시점 앎, false: zip 없음)
		    {
		        const kResponse = await fetch("/api/dump/zip?" + new URLSearchParams({ sMachine: kDump.sMachine, sPath: kDump.sPath }), { cache: "no-store" });

		        if (kResponse.status === 404)	// zip 없음 (삭제됨)
		        {
		            return false;
		        }

		        if (!kResponse.ok)
		        {
		            throw new Error(`zip 받기 실패 (HTTP ${kResponse.status})`);
		        }

		        const sUrl	= URL.createObjectURL(await kResponse.blob());	// 받은 zip
		        const kLink	= document.createElement("a");

		        kLink.href		= sUrl;
		        kLink.download	= kDump.sPath.slice(kDump.sPath.lastIndexOf("\\") + 1).replace(/\.[^.]*$/, "") + ".zip";	// 서버 zip 이름과 같음 (확장자만 .zip)

		        kLink.click();

		        setTimeout(() => URL.revokeObjectURL(sUrl), 60000);	// 브라우저 저장 시작 후 해제

		        return true;
		    }

		    static SetDumpButton(kButton, bZip, sBusy)	// Ac. 다운 아이콘 (zip 있음: 다운, 없음: 압축 (흐림), sBusy: 진행 중 (끔), null: 진행 없음)
		    {
		        kButton.title		= bZip ? "다운(zip)" : "압축(zip)";
		        kButton.disabled	= sBusy != null;

		        kButton.classList.toggle("nozip", !bZip);
		    }

		    static IsSameDump(kLeft, kRight)	// 같은 덤프 (머신·경로, 목록을 다시 조회해 객체가 바뀌어도 같음)
		    {
		        return kLeft.sMachine === kRight.sMachine && kLeft.sPath === kRight.sPath;
		    }

		    static SetDumpBusy(kDump, sBusy)	// 덤프 다운 진행 표시 (sBusy null: 끝): 덤프 창 가운데 팝업 + 같은 덤프 줄 다운 아이콘 끔 (다시 그려도 유지)
		    {
		        const sBusyKey = `${kDump.sMachine}\t${kDump.sPath}`;	// 진행 중 덤프 구분

		        if (sBusy == null)
		        {
		            Dump.mapBusy.delete(sBusyKey);
		        }
		        else
		        {
		            Dump.mapBusy.set(sBusyKey, `${sBusy}\n${kDump.sMachine} : ${kDump.sPath.slice(kDump.sPath.lastIndexOf("\\") + 1)}`);
		        }

		        kDumpProgress.textContent	= [...Dump.mapBusy.values()].join("\n\n");

		        if (Dump.mapBusy.size === 0)	// 진행 끝: 팝업 닫음
		        {
		            clearTimeout(Dump.nBusyPopup);

		            Dump.nBusyPopup		= 0;
		            kDumpProgress.hidden	= true;
		        }
		        else if (Dump.nBusyPopup === 0)	// 진행 시작: X초 이상 걸릴 때만 팝업
		        {
		            Dump.nBusyPopup = setTimeout(() => { kDumpProgress.hidden = Dump.mapBusy.size === 0; }, PROGRESS_DELAY_MS);
		        }

		        const nIndex	= Dump.arrDump.findIndex(kItem => Dump.IsSameDump(kItem, kDump));	// 지금 목록 줄 (없음: -1)
		        const kItem		= Dump.arrDump[nIndex];

		        kDump.sBusy = sBusy;

		        if (kItem === undefined)	// 목록에서 빠짐
		        {
		            return;
		        }

		        kItem.sBusy	= sBusy;
		        kItem.bZip	= kDump.bZip;

		        const kButton = kDumpBody.rows[nIndex]?.querySelector('button[data-action="down"]');	// 지금 줄의 다운 버튼

		        if (kButton == null)
		        {
		            return;
		        }

		        Dump.SetDumpButton(kButton, kItem.bZip, sBusy);
		    }

		    static async DownloadDump(kDump)	// 덤프 다운: Server zip 을 브라우저로 받기 (zip 없어짐: 압축)
		    {
		        Dump.SetDumpBusy(kDump, "다운 중");

		        try
		        {
		            if (!await Dump.SaveDumpZip(kDump))	// zip 없어짐 (삭제됨): 압축부터
		            {
		                kDump.bZip = false;

		                alert(`Server 에 zip 이 없습니다. 압축 후 다시 다운해 주세요.\n\n${kDump.sMachine}\n${kDump.sPath}`);
		            }
		        }
		        catch (kError)	// zip 받기 실패
		        {
		            console.error(kError);

		            alert(`다운 실패: ${ErrorText(kError)}\n\n${kDump.sMachine}\n${kDump.sPath}`);
		        }

		        Dump.SetDumpBusy(kDump, null);
		    }

		    static async CompressDump(kDump)	// 덤프 압축: Server 가 클라에서 받아 Dump\머신\덤프 파일명.zip 저장 (DUMP_POLL_MS 마다 진행 조회, 끝: 다운 버튼, 자동 받기 안 함)
		    {
		        const nBegin = performance.now();	// 압축 시작 (진행 초)

		        Dump.SetDumpBusy(kDump, "압축 중 · 0초");

		        try
		        {
		            while (true)	// 시작·진행 조회 (요청마다 바로 응답 → 큰 덤프도 ToolManager·IIS 요청 시간 제한에 안 걸림)
		            {
		                const kResponse = await fetch("/api/dump/download", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ sMachine: kDump.sMachine, sPath: kDump.sPath }), cache: "no-store" });

		                if (!kResponse.ok)	// 경유 실패 등 (JSON 아님)
		                {
		                    throw new Error(`HTTP ${kResponse.status}`);
		                }

		                const kData = await kResponse.json();	// {bDone, nOffset, nSize, sError}

		                if (kData.sError !== "")
		                {
		                    throw new Error(kData.sError);
		                }

		                if (kData.bDone)
		                {
		                    break;
		                }

		                const nSec = Math.floor((performance.now() - nBegin) / 1000);	// 압축 시간 (초)

		                Dump.SetDumpBusy(kDump, kData.nSize > 0 ? `압축 중 · ${nSec}초 · ${Math.floor(kData.nOffset * 100 / kData.nSize)}%` : `압축 중 · ${nSec}초`);	// zip 없어 Server 가 받아 압축하는 중

		                await new Promise(funcResolve => setTimeout(funcResolve, Dump.DUMP_POLL_MS));
		            }

		            kDump.bZip = true;	// Server 에 zip 있음 → 다운 버튼
		        }
		        catch (kError)	// 압축 실패·응답 끊김 (서버는 저장 계속 → 다시 누르면 진행 이어서 표시)
		        {
		            console.error(kError);

		            alert(`압축 실패: ${ErrorText(kError)}\n\n${kDump.sMachine}\n${kDump.sPath}`);
		        }

		        Dump.SetDumpBusy(kDump, null);
		    }

		    static DeleteDump(kDump, kButton)	// 덤프 삭제: 확인 팝업 → RunDeleteDump
		    {
		        AskDelete("덤프 삭제", kDump.sPath.slice(kDump.sPath.lastIndexOf("\\") + 1), `${kDump.sMachine} : ${kDump.sPath} · 되돌릴 수 없음 · Server 에 다운한 zip 도 삭제`, () => Dump.RunDeleteDump(kDump, kButton));
		    }

		    static async RunDeleteDump(kDump, kButton)	// 덤프 삭제 실행 → 덤프 1번 창에서 제거 (실패: 알림)
		    {
		        kButton.disabled = true;

		        try
		        {
		            const sZipError = await Dump.RequestDelete(kDump);	// Server zip 삭제 오류

		            Dump.arrDump = Dump.arrDump.filter(kItem => !Dump.IsSameDump(kItem, kDump));	// 그새 다시 조회해도 같은 덤프 뺌

		            if (!Dump.bDumpLoading)	// 다시 조회 중: 조회가 그림 (조회 중 제목 유지)
		            {
		                Dump.RenderDump();
		                Dump.ShowDumpTitle();
		            }

		            if (sZipError !== "")	// 덤프는 삭제, Server zip 삭제 실패
		            {
		                alert(`덤프는 삭제했지만 Server zip 삭제 실패: ${sZipError}\n\n${kDump.sMachine}\n${kDump.sPath}`);
		            }
		        }
		        catch (kError)	// 삭제 실패
		        {
		            console.error(kError);

		            kButton.disabled = false;

		            alert(`삭제 실패: ${ErrorText(kError)}\n\n${kDump.sMachine}\n${kDump.sPath}`);
		        }
		    }

		    static async RequestDelete(kDump)	// 클라에 덤프 삭제 요청 (성공: Server zip 도 삭제) → zip 삭제 오류 ("": 없음, 덤프 삭제 실패: throw)
		    {
		        const kData		= await FetchJson("/api/dump/delete", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ sMachine: kDump.sMachine, sPath: kDump.sPath }), cache: "no-store" });
		        const sError	= kData.sError !== "" ? kData.sError : (kData.kResult == null ? "결과 없음" : kData.kResult.sError);

		        if (sError !== "")
		        {
		            throw new Error(sError);
		        }

		        return kData.sZipError;
		    }

		    static DeleteAllDump()	// 전체 삭제 버튼: 확인 팝업 → RunDeleteAll
		    {
		        if (Dump.arrDump.length === 0 || Dump.bDumpLoading)	// 목록 없음·조회·삭제 중
		        {
		            return;
		        }

		        AskDelete("덤프 전체 삭제", `덤프 파일 ${Dump.arrDump.length.toLocaleString()}개`, "되돌릴 수 없음 · Server 에 다운한 zip 도 삭제", Dump.RunDeleteAll);
		    }

		    static async RunDeleteAll()	// 전체 삭제: 차례로 삭제 (클라 동시 처리 제한), 성공한 줄만 제거, 실패: 모아서 알림
		    {
		        const arrTarget	= [...Dump.arrDump];	// 삭제할 덤프 (시작 때 목록)
		        const arrFail	= [];					// 실패 문구

		        Dump.bDumpLoading		= true;
		        kDumpButton.disabled	= true;
		        kDumpDeleteAll.disabled	= true;
		        kDumpBody.inert			= true;	// 삭제 중 줄 버튼 막음

		        for (const [nIndex, kDump] of arrTarget.entries())
		        {
		            kDumpTitle.textContent = `${Dump.kDumpInfo.sCondition} 삭제 중: ${nIndex + 1} / ${arrTarget.length}`;

		            try
		            {
		                const sZipError = await Dump.RequestDelete(kDump);	// Server zip 삭제 오류

		                Dump.arrDump = Dump.arrDump.filter(kItem => !Dump.IsSameDump(kItem, kDump));

		                Dump.RenderDump();

		                if (sZipError !== "")	// 덤프는 삭제, Server zip 삭제 실패
		                {
		                    arrFail.push(`${kDump.sMachine} : ${kDump.sPath} (Server zip 삭제 실패: ${sZipError})`);
		                }
		            }
		            catch (kError)	// 삭제 실패: 다음 덤프 계속
		            {
		                console.error(kError);

		                arrFail.push(`${kDump.sMachine} : ${kDump.sPath} (${ErrorText(kError)})`);
		            }
		        }

		        Dump.bDumpLoading		= false;
		        kDumpButton.disabled	= Search.bSearching;
		        kDumpDeleteAll.disabled	= Dump.arrDump.length === 0;	// 모두 삭제: 비활성
		        kDumpBody.inert			= false;

		        Dump.ShowDumpTitle();

		        if (arrFail.length > 0)
		        {
		            alert(`삭제 실패: ${arrFail.length}개\n\n${arrFail.join("\n")}`);
		        }
		    }

		    static OnDumpClick(kEvent)	// 덤프 1번 창 버튼 (표 하나에서 처리): 다운·압축·삭제
		    {
		        const kButton = kEvent.target.closest("button[data-action]");	// 누른 버튼

		        if (kButton == null || kButton.disabled)
		        {
		            return;
		        }

		        const kDump = Dump.arrDump[Number(kButton.closest("tr").dataset.index)];

		        if (kButton.dataset.action === "delete")
		        {
		            Dump.DeleteDump(kDump, kButton);
		        }
		        else if (kDump.bZip)	// zip 있음: 확인 팝업 → 브라우저로 받기
		        {
		            const sName = kDump.sPath.slice(kDump.sPath.lastIndexOf("\\") + 1);	// 덤프 파일명

		            AskDownload(sName.replace(/\.[^.]*$/, "") + ".zip", `Server Dump 폴더 zip · 원본 ${kDump.sMachine} : ${kDump.sPath} (${kDump.sTime} · ${Math.ceil(kDump.nSize / 1024).toLocaleString()} KB)`, () => Dump.DownloadDump(kDump));
		        }
		        else	// zip 없음: Server 에 압축만
		        {
		            Dump.CompressDump(kDump);
		        }
		    }
		}
		""";
	}
}

class DumpSave	// 다운 중인 덤프 1개 (TabDump.SaveDump 가 갱신, 페이지가 /api/dump/download 로 진행 조회)
{
	public long				m_nSize;			// 원본 크기 (첫 조각 때, 0: 아직 모름)
	public long				m_nOffset;			// 받은 크기
	public string			m_sError	= "";	// 실패 오류 (없음: "")
	public long				m_nEndTick;			// 끝난 시각 (Environment.TickCount64)
	public volatile bool	m_bEnd;				// 끝남 (성공: zip 있음, 실패: m_sError) → 다음 조회에 결과 알린 뒤 해제
}

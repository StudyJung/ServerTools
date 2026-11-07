static class TabSearch	// 서치 탭: 로그 검색 (서치 대상 폴더 날짜 파일)·파일 보기·진행 알림
{
	private const int	m_nSearchMax	= 1000000;	// 검색 결과 최대 건수 (서버 nMax 상한)

	public const int	m_nSearchDefault	= 1000;	// 검색 결과 최대 건수 기본값 (nMax 없음, 서버와 같음)
	public const int	m_nSearchLimitSec	= 30;	// 검색 최대 시간 기본 (초, 서버 nLimit 없음, 초과 시 그때까지 결과만)

	private const int	m_nSearchLimitMax	= 100;			// 검색 최대 시간 상한 (초, 서버 nLimit)
	private const int	m_nSearchConcurrent	= 16;			// 동시 검색 안전 상한 (서버: 세션마다 1건씩 동시 → 사용자 수보다 넉넉히, 넘음: 대기 후 거절)
	private const int	m_nSearchTextMax	= 1000;			// 검색 결과 줄 최대 글자 (초과: "…")
	private const long	m_nSearchTextTotal	= 300000000;	// 검색 결과 글자 수 합 최대 (초과: 그때까지 결과만, 브라우저 문자열 한도·서버 m_nLineMax 안쪽)
	private const int	m_nViewCount		= 500;			// 보기 기본 줄 수
	private const int	m_nViewCountMax		= 2000;			// 보기 최대 줄 수
	private const int	m_nViewTextMax		= 10000;		// 보기 줄 최대 글자 (초과: "…")
	private const int	m_nSearchWaitSec	= 20;			// 앞 검색 처리 중일 때 대기 (초, 초과: 거절 → 밀린 검색 방지)
	private const int	m_nProgressMs		= 1000;			// 검색 진행 알림 주기 (ms)

	private static readonly SemaphoreSlim	m_kSearchLock	= new SemaphoreSlim(m_nSearchConcurrent, m_nSearchConcurrent);	// 동시 검색 X건 (한 검색이 오래 걸려도 다른 사용자 검색)

	public static readonly ConcurrentDictionary<long, SearchJob>	m_hmapJob	= new ConcurrentDictionary<long, SearchJob>();	// 진행 중인 검색 (요청 번호 → 진행·중지)
	public static string[]			m_arrBlockSearch	= Array.Empty<string>();	// aBlockSearch 막는 경로 (전체 경로·\이름\, 자신·하위: 서치 제외, 다른 탭: 그대로)
	public static volatile string[]	m_arrLogFolder		= Array.Empty<string>();	// 서치 대상 폴더 (aBlockSearch 제외, 탐색 시 새 배열로 교체 → 다른 스레드 읽기)

	public static void Init(string[] arrBlockSearch)	// 설정 → 막는 경로 (aBlockSearch, Main.Create)
	{
		m_arrBlockSearch = Tab.GetBlockRules(arrBlockSearch, "aBlockSearch");
	}

	public static async Task SendProgress(long nReqId, SearchJob kJob, LineWriter kWriter, CancellationToken kEnd, CancellationToken kStop)	// 검색 진행 알림 (kEnd: 대기만 끊음, 쓰는 중엔 안 끊음 → 줄 깨짐 방지)
	{
		try
		{
			while (true)
			{
				await Task.Delay(m_nProgressMs, kEnd);

				await kWriter.WriteJsonAsync(new { sType = "SearchProgress", nReqId, nFileTotal = kJob.m_nFileTotal, nFileDone = kJob.m_nFileDone, nFound = kJob.m_nFound, nElapsedMs = (long)Stopwatch.GetElapsedTime(kJob.m_nBegin).TotalMilliseconds }, Define.m_kJsonOption, kStop);
			}
		}
		catch (Exception)	// 검색 끝·연결 끊김
		{
		}
	}

	public static async Task<object> Search(long nReqId, long nStart, long nEnd, string sKey, int nMax, int nLimit, SearchJob kJob, CancellationToken kStop)	// 로그 검색: 대상 파일(시간·경로 순)에서 검색어 포함 줄 (nLimit: 마감 초, 없음·0 이하: 기본, kJob: 진행 기록·중지 요청)
	{
		long				nBegin		= kJob.m_nBegin;	// 검색 시작 타임스탬프
		List<SearchLine>	listLine	= new List<SearchLine>();
		int					nFileCount	= 0;				// 검색한 파일 수
		bool				bTruncated	= false;			// 최대 건수·시간 초과·중지로 중단
		bool				bStopped	= false;			// 중지 요청으로 중단
		string				sError		= "";
		int					nLimitSec	= nLimit > 0 ? Math.Min(nLimit, m_nSearchLimitMax) : m_nSearchLimitSec;	// 마감 (초)

		nMax = nMax <= 0 ? m_nSearchDefault : Math.Min(nMax, m_nSearchMax);	// 없음: 기본값, 그 외: 상한까지

		using CancellationTokenSource kCancel = CancellationTokenSource.CreateLinkedTokenSource(kStop, kJob.m_kCancel.Token);	// 연결 끊김·중지 요청

		try
		{
			if (!await m_kSearchLock.WaitAsync(TimeSpan.FromSeconds(m_nSearchWaitSec), kCancel.Token))	// 동시 검색 X건, 대기 초과: 거절 (밀린 검색 방지)
			{
				return new { sType = "SearchResult", nReqId, bTruncated = false, bStopped = false, nFileCount = 0, nElapsedMs = 0L, sError = "다른 검색 처리 중 (잠시 후 다시)", arrLine = listLine };
			}
		}
		catch (OperationCanceledException)	// 기다리는 중 중지·연결 끊김
		{
			return new { sType = "SearchResult", nReqId, bTruncated = true, bStopped = true, nFileCount = 0, nElapsedMs = 0L, sError = "", arrLine = listLine };
		}

		try
		{
			SearchNode?	kCond	= SearchNode.Parse(sKey);	// 검색 조건 (빈 검색어: null)

			if (kCond == null || nStart > nEnd)
			{
				sError = "검색 조건 오류";
			}
			else
			{
				SearchValues<string>	kKey		= SearchValues.Create(kCond.GetFirst(), StringComparison.OrdinalIgnoreCase);	// 블록에서 먼저 찾을 검색어 (맞는 줄에 꼭 있는 것, 벡터 검색)
				SearchNode?				kCheck		= kCond.m_eOp == SEARCH_OP.SEARCH_OP_TERM ? null : kCond;						// 줄마다 확인할 조건 (연산자 없음: null → 찾은 줄 바로 추가)
				long					nDeadline	= nBegin + nLimitSec * Stopwatch.Frequency;										// 검색 마감 타임스탬프

				List<(long nDateHour, string sPath)>	listTarget	= GetTargetFile(nStart, nEnd);
				Throttle	kThrottle	= new Throttle();	// 파일 바뀌어도 이어서 셈

				kJob.m_nFileTotal = listTarget.Count;

				foreach ((long nDateHour, string sPath) in listTarget)
				{
					if (kCancel.IsCancellationRequested)	// 중지 요청·연결 끊김
					{
						bTruncated	= true;
						bStopped	= true;

						break;
					}

					if (Stopwatch.GetTimestamp() > nDeadline)	// 시간 초과
					{
						bTruncated	= true;
						sError		= $"검색 시간 초과 ({nLimitSec}초, 그때까지 결과만)";

						break;
					}

					++nFileCount;

					try
					{
						SEARCH_STOP	eStop	= SearchFile(sPath, nDateHour, kKey, kCheck, listLine, nMax, nDeadline, kJob, kCancel.Token, kThrottle);

						if (eStop == SEARCH_STOP.SEARCH_STOP_CANCEL)
						{
							bTruncated	= true;
							bStopped	= true;

							break;
						}

						if (eStop == SEARCH_STOP.SEARCH_STOP_MAX)
						{
							bTruncated = true;

							break;
						}

						if (eStop == SEARCH_STOP.SEARCH_STOP_TIME)
						{
							bTruncated	= true;
							sError		= $"검색 시간 초과 ({nLimitSec}초, 그때까지 결과만)";

							break;
						}
					}
					catch (Exception kException)	// 파일 열기·읽기 실패: 다음 파일 계속
					{
						Log.Write(WARN_, $"서치 파일 오류: {sPath} ({kException.Message})");
					}

					kJob.m_nFileDone = nFileCount;
				}
			}
		}
		catch (Exception kException)	// 폴더 탐색 오류 등
		{
			sError = GetErrorText(kException, "로그 검색");
		}
		finally
		{
			m_kSearchLock.Release();
		}

		long	nElapsedMs	= (long)Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds;

		Log.Write(INFO_, $"서치: {nStart}~{nEnd} → {listLine.Count}건 / 파일 {nFileCount}개 ({nElapsedMs}ms){(bStopped ? " (중지)" : bTruncated ? " (일부)" : "")}");

		return new { sType = "SearchResult", nReqId, bTruncated, bStopped, nFileCount, nElapsedMs, sError, arrLine = listLine };
	}

	private static SEARCH_STOP SearchFile(string sPath, long nDateHour, SearchValues<string> kKey, SearchNode? kCheck, List<SearchLine> listLine, int nMax, long nDeadline, SearchJob kJob, CancellationToken kCancel, Throttle kThrottle)	// 파일 하나 검색: 블록에서 검색어 위치 찾고 맞는 줄만 문자열화 (kCheck: 줄 조건, null: 바로 추가, 줄 번호 = 앞 '\n' 수 + 1)
	{
		long		nLine	= 0;	// 지난 블록까지 '\n' 수
		SEARCH_STOP	eStop	= SEARCH_STOP.SEARCH_STOP_NONE;

		Tab.ForEachBlock(sPath, (ReadOnlySpan<char> kBlock) =>
		{
			int	nCounted	= 0;	// '\n' 을 센 위치
			int	nPos		= 0;

			while (nPos < kBlock.Length)
			{
				int	nFind	= kBlock.Slice(nPos).IndexOfAny(kKey);

				if (nFind < 0)
				{
					break;
				}

				nFind += nPos;

				int	nLineStart	= kBlock.Slice(0, nFind).LastIndexOf('\n') + 1;
				int	nLineEnd	= kBlock.Slice(nFind).IndexOf('\n');	// 줄 끝 ('\n' 위치, 없음: 블록 끝)

				nLineEnd	= nLineEnd < 0 ? kBlock.Length : nFind + nLineEnd;
				nLine		+= kBlock.Slice(nCounted, nLineStart - nCounted).Count('\n');
				nCounted	= nLineStart;

				if (kCheck != null && !kCheck.IsMatch(kBlock.Slice(nLineStart, nLineEnd - nLineStart)))	// 조건 안 맞음: 다음 줄
				{
					nPos = nLineEnd + 1;

					continue;
				}

				if (listLine.Count >= nMax || kJob.m_nTextChars >= m_nSearchTextTotal)	// 최대 건수·글자 수 합 초과: 중단 (더 있다는 뜻)
				{
					eStop = SEARCH_STOP.SEARCH_STOP_MAX;

					return false;
				}

				string	sText	= Tab.ToText(kBlock.Slice(nLineStart, nLineEnd - nLineStart), m_nSearchTextMax);

				listLine.Add(new SearchLine(sPath, nDateHour, nLine + 1, sText));	// 전체 경로 (aLogFolder 여러 개)

				kJob.m_nTextChars += sText.Length;

				nPos = nLineEnd + 1;
			}

			nLine += kBlock.Slice(nCounted).Count('\n');

			kJob.m_nFound = listLine.Count;

			if (kCancel.IsCancellationRequested)	// 중지 요청 (블록마다 확인)
			{
				eStop = SEARCH_STOP.SEARCH_STOP_CANCEL;

				return false;
			}

			if (Stopwatch.GetTimestamp() > nDeadline)	// 시간 초과 (블록마다 확인)
			{
				eStop = SEARCH_STOP.SEARCH_STOP_TIME;

				return false;
			}

			return true;
		}, kThrottle);

		return eStop;
	}

	public static object View(long nReqId, string sRelPath, long nStart, int nCount)	// 파일 보기: nStart 줄부터 nCount 줄 (nStart 0 이하: 마지막 nCount 줄), 보기 확장자만
	//   UTF-8·CP949: 바이트 '\n' 로 줄 위치 → 보낼 줄만 글자 변환 (CPU 절감), UTF-16: 처음부터 글자로 셈
	{
		List<string>	listText	= new List<string>();
		long			nTotal		= 0;					// 전체 줄 수
		long			nFirst		= Math.Max(1, nStart);	// 보낼 첫 줄 번호
		long			nSize		= 0;
		string			sTime		= "";					// 수정 시각 (KST)
		string			sError		= "";

		nCount = nCount <= 0 ? m_nViewCount : Math.Min(nCount, m_nViewCountMax);

		string?	sPath	= GetAllowedPath(sRelPath);	// 허용 경로 (그 외: null)

		if (sPath == null)
		{
			sError = "허용되지 않은 경로";
		}
		else
		{
			try
			{
				Throttle	kThrottle	= new Throttle();	// 줄 세기·읽기 이어서 셈

				(nTotal, long nOffset) = ScanLines(sPath, nStart > 0 ? nFirst : 0, kThrottle);	// 바이트로 줄 수·nFirst 줄 시작 위치 (마지막 페이지: 줄 수만, UTF-16: -1)

				if (nTotal < 0)	// UTF-16: 처음부터 글자로 셈
				{
					if (nStart <= 0)	// 마지막 페이지: 줄 수만 먼저 셈 (문자열 안 만듦)
					{
						nFirst = Math.Max(1, CountLines(sPath, kThrottle) - nCount + 1);
					}

					nTotal = ReadRange(sPath, nFirst, nCount, listText, kThrottle);
				}
				else
				{
					if (nStart <= 0)	// 마지막 페이지: 시작 줄 정한 뒤 그 위치 찾기 (바이트 세기 → 두 번째는 OS 캐시)
					{
						nFirst = Math.Max(1, nTotal - nCount + 1);

						(_, nOffset) = ScanLines(sPath, nFirst, kThrottle);
					}

					if (nOffset >= 0)	// 범위 안: 그 위치부터 nCount 줄만 글자로 (범위 밖: 빈 목록)
					{
						ReadLines(sPath, nOffset, nCount, listText, kThrottle);
					}
				}

				FileInfo	kInfo	= new FileInfo(sPath);

				nSize	= kInfo.Length;
				sTime	= new DateTimeOffset(kInfo.LastWriteTimeUtc, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
			}
			catch (Exception kException)	// 파일 열기·읽기 실패
			{
				sError = GetErrorText(kException, $"파일 보기 {sRelPath}");
			}
		}

		return new { sType = "ViewResult", nReqId, sPath = sRelPath, nStart = nFirst, nTotal, nSize, sTime, sError, arrText = listText };
	}

	private static (long nTotal, long nOffset) ScanLines(string sPath, long nFindLine, Throttle kThrottle)	// 바이트로 줄 세기: 전체 줄 수, nFindLine 줄 시작 바이트 위치 (nFindLine 0·범위 밖: -1)
	//   '\n' 바이트는 UTF-8·CP949 멀티바이트 글자 안에 없음 → 변환 없이 벡터 검색, UTF-16 BOM: (-1, -1)
	{
		using FileStream kStream = new FileStream(sPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.SequentialScan);	// 쓰는 중인 로그 허용, 순차 읽기 힌트

		Span<byte>	kBom	= stackalloc byte[3];
		int			nBom	= kStream.ReadAtLeast(kBom, 3, throwOnEndOfStream: false);

		if (nBom >= 2 && ((kBom[0] == 0xFF && kBom[1] == 0xFE) || (kBom[0] == 0xFE && kBom[1] == 0xFF)))	// UTF-16: 바이트로 못 셈
		{
			return (-1, -1);
		}

		long	nBase		= nBom == 3 && kBom[0] == 0xEF && kBom[1] == 0xBB && kBom[2] == 0xBF ? 3 : 0;	// 첫 줄 시작 (UTF-8 BOM 건너뜀)
		long	nNewLine	= 0;							// 센 '\n' 수
		long	nOffset		= nFindLine == 1 ? nBase : -1;	// 찾는 줄 시작 위치 (-1: 아직·없음)
		long	nPos		= nBase;						// 이번 블록 시작 위치
		bool	bOpen		= false;						// 마지막 바이트가 '\n' 아님 (끝 줄 있음)
		byte[]	arrBuffer	= ArrayPool<byte>.Shared.Rent(Define.m_nBlockBytes);

		try
		{
			kStream.Position = nBase;

			while (true)
			{
				int	nRead	= kStream.Read(arrBuffer, 0, arrBuffer.Length);

				if (nRead == 0)
				{
					break;
				}

				ReadOnlySpan<byte>	kBlock	= arrBuffer.AsSpan(0, nRead);
				int					nCount	= kBlock.Count((byte)'\n');

				if (nOffset < 0 && nFindLine > 1 && nNewLine + nCount >= nFindLine - 1)	// 찾는 줄 시작이 이 블록에 있음
				{
					int	nSkip	= 0;

					for (long nNeed = nFindLine - 1 - nNewLine; nNeed > 0; --nNeed)
					{
						nSkip += kBlock.Slice(nSkip).IndexOf((byte)'\n') + 1;
					}

					nOffset = nPos + nSkip;
				}

				nNewLine	+= nCount;
				nPos		+= nRead;
				bOpen		= kBlock[^1] != (byte)'\n';

				kThrottle.Check();
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(arrBuffer);
		}

		long	nTotal	= nNewLine + (bOpen ? 1 : 0);	// 끝 줄 '\n' 없음: 1 더함

		return (nTotal, nFindLine > nTotal ? -1 : nOffset);	// 범위 밖 (예: 끝 '\n' 뒤): -1
	}

	private static void ReadLines(string sPath, long nOffset, int nCount, List<string> listText, Throttle kThrottle)	// nOffset (줄 시작 바이트) 부터 nCount 줄 → listText (아주 긴 줄: 앞 조각만)
	{
		bool	bOpen	= false;	// 앞 블록이 줄 중간에서 끝남 (이어지는 조각은 버림)

		Tab.ForEachBlock(sPath, (ReadOnlySpan<char> kBlock) =>
		{
			ReadOnlySpan<char>	kRest	= kBlock;

			while (kRest.Length > 0 && listText.Count < nCount)
			{
				int	nIndex	= kRest.IndexOf('\n');

				if (!bOpen)
				{
					listText.Add(Tab.ToText(nIndex < 0 ? kRest : kRest.Slice(0, nIndex), m_nViewTextMax));
				}

				if (nIndex < 0)	// 줄이 다음 블록으로 이어짐
				{
					bOpen = true;

					break;
				}

				bOpen	= false;
				kRest	= kRest.Slice(nIndex + 1);
			}

			return listText.Count < nCount;	// 다 모음: 중단
		}, kThrottle, nOffset);
	}

	private static long CountLines(string sPath, Throttle kThrottle)	// 전체 줄 수 ('\n' 수 + 마지막 줄이 '\n' 으로 안 끝나면 1)
	{
		long	nLine	= 0;		// '\n' 수
		bool	bOpen	= false;	// 마지막 블록이 '\n' 으로 안 끝남

		Tab.ForEachBlock(sPath, (ReadOnlySpan<char> kBlock) =>
		{
			nLine	+= kBlock.Count('\n');
			bOpen	= kBlock[^1] != '\n';

			return true;
		}, kThrottle);

		return nLine + (bOpen ? 1 : 0);
	}

	private static long ReadRange(string sPath, long nFirst, int nCount, List<string> listText, Throttle kThrottle)	// nFirst 줄부터 nCount 줄을 listText 에 (범위 밖 줄은 세기만 함), 반환: 전체 줄 수
	{
		long	nLast	= nFirst + nCount - 1;	// 읽을 마지막 줄 번호
		long	nLine	= 0;					// 지난 '\n' 수
		long	nAdded	= 0;					// 마지막으로 넣은 줄 번호 (아주 긴 줄이 블록에 나뉘면 앞 조각만)
		bool	bOpen	= false;				// 마지막 블록이 '\n' 으로 안 끝남

		Tab.ForEachBlock(sPath, (ReadOnlySpan<char> kBlock) =>
		{
			int	nNewLine	= kBlock.Count('\n');

			if (nLine + nNewLine + 1 < nFirst || nLine + 1 > nLast)	// 블록 줄이 모두 범위 밖: 세기만
			{
				nLine	+= nNewLine;
				bOpen	= kBlock[^1] != '\n';

				return true;
			}

			ReadOnlySpan<char>	kRest	= kBlock;

			while (kRest.Length > 0)
			{
				int		nIndex	= kRest.IndexOf('\n');
				long	nNo		= nLine + 1;

				if (nNo >= nFirst && nNo <= nLast && nNo != nAdded)
				{
					listText.Add(Tab.ToText(nIndex < 0 ? kRest : kRest.Slice(0, nIndex), m_nViewTextMax));

					nAdded = nNo;
				}

				if (nIndex < 0)
				{
					break;
				}

				++nLine;

				kRest = kRest.Slice(nIndex + 1);
			}

			bOpen = kBlock[^1] != '\n';

			return true;
		}, kThrottle);

		return nLine + (bOpen ? 1 : 0);
	}

	private static List<(long nDateHour, string sPath)> GetTargetFile(long nStart, long nEnd)	// 검색 대상 파일: 쿼리 대상 폴더의 기간 안 날짜 폴더 *yyyyMMdd_HH.log (시간·경로 순)
	{
		List<(long nDateHour, string sPath)>	listFile	= new List<(long nDateHour, string sPath)>();
		long									nDateMin	= nStart / 100;
		long									nDateMax	= nEnd / 100;
		EnumerationOptions						kDirOption	= new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };	// 대상 폴더 바로 아래만
		EnumerationOptions						kFileOption	= new EnumerationOptions { IgnoreInaccessible = true };

		foreach (string sFolder in m_arrLogFolder)	// 없어진 폴더: 건너뜀
		{
			if (!Directory.Exists(sFolder))
			{
				continue;
			}

			FileSystemEnumerable<string> kDateDir = new FileSystemEnumerable<string>(sFolder, (ref FileSystemEntry kEntry) => kEntry.ToFullPath(), kDirOption)	// 기간 안 날짜 폴더
			{
				ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => kEntry.IsDirectory && Tab.TryGetDate(kEntry.FileName, out long nDate) && nDate >= nDateMin && nDate <= nDateMax,
			};

			foreach (string sDirPath in kDateDir)
			{
				FileSystemEnumerable<(long nDateHour, string sPath)> kFile = new FileSystemEnumerable<(long nDateHour, string sPath)>(sDirPath, (ref FileSystemEntry kEntry) => (Tab.TryGetLogDateHour(kEntry.FileName, out long nDateHour) ? nDateHour : 0, kEntry.ToFullPath()), kFileOption)
				{
					ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => !kEntry.IsDirectory && Tab.TryGetLogDateHour(kEntry.FileName, out long nDateHour) && nDateHour >= nStart && nDateHour <= nEnd,
				};

				listFile.AddRange(kFile);
			}
		}

		listFile.Sort((kLeft, kRight) => kLeft.nDateHour != kRight.nDateHour ? kLeft.nDateHour.CompareTo(kRight.nDateHour) : string.Compare(kLeft.sPath, kRight.sPath, StringComparison.OrdinalIgnoreCase));

		return listFile;
	}

	private static string? GetAllowedPath(string sPath)	// 보기 허용 경로: aLogFolder 하위 + 보기 확장자 + 존재 (그 외: null)
	{
		string?	sLog	= Tab.GetLogFolderPath(sPath);

		return sLog != null && IsViewFile(Path.GetFileName(sLog.AsSpan())) ? sLog : null;
	}

	private static bool IsViewFile(ReadOnlySpan<char> sName)	// 보기 확장자 (대소문자 무시, 페이지 File.FILE_VIEW_EXT 와 같음)
	{
		return sName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || sName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || sName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || sName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
			|| sName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || sName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || sName.EndsWith(".config", StringComparison.OrdinalIgnoreCase)
			|| sName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || sName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);
	}
}

class SearchJob	// 진행 중인 검색 (진행 알림·Stop 중지)
{
	public readonly CancellationTokenSource	m_kCancel		= new CancellationTokenSource();	// 중지 요청
	public readonly long					m_nBegin		= Stopwatch.GetTimestamp();			// 시작 타임스탬프
	public volatile int						m_nFileTotal	= -1;								// 대상 파일 수 (-1: 앞 검색 대기 중)
	public volatile int						m_nFileDone;										// 검색한 파일 수
	public volatile int						m_nFound;											// 찾은 줄 수
	public long								m_nTextChars;										// 찾은 줄 글자 수 합 (검색 스레드 전용)
}

class SearchNode	// 검색 조건 트리: AND·OR·NOT (대소문자 무시) 왼쪽부터, 괄호 먼저 (예: "A AND (B NOT C) OR D" = (A AND (B NOT C)) OR D, 페이지 Key.Parse 와 같은 규칙)
//   연산자: 공백 + AND·OR·NOT (대소문자 무시) + 공백·'(' (단어 안: 검색어)
//   와일드카드: 검색어 앞뒤 * (ab*: 단어 시작, *ab: 단어 끝, *ab*: 어디든 = ab, 경계: 글자·숫자 아님, 서버 쿼리와 같음), 괄호: 검색어 자리 '(' 와 짝 ')' (Init(x) 등 검색어 안 괄호: 그대로)
//   형식 오류 (연산자로 끝남·괄호 뒤 검색어 등): 전체를 검색어 하나로
{
	public readonly SEARCH_OP	m_eOp;		// 연산
	public readonly string		m_sTerm;	// 검색어 (SEARCH_OP_TERM 만, 그 외: "")
	public readonly SearchNode?	m_kLeft;	// 왼쪽 (연산만)
	public readonly SearchNode?	m_kRight;	// 오른쪽 (연산만)

	private readonly string	m_sWild;		// 와일드카드 * 뗀 글자 ("": 아님)
	private readonly bool	m_bWildHead;	// 와일드카드 앞 * (앞 아무 글자)
	private readonly bool	m_bWildTail;	// 와일드카드 뒤 * (뒤 아무 글자)

	private SearchNode(SEARCH_OP eOp, string sTerm, SearchNode? kLeft, SearchNode? kRight)
	{
		m_eOp		= eOp;
		m_sTerm		= sTerm;
		m_kLeft		= kLeft;
		m_kRight	= kRight;
		m_sWild		= eOp == SEARCH_OP.SEARCH_OP_TERM && TryGetWild(sTerm, out string sCore, out m_bWildHead, out m_bWildTail) ? sCore : "";
	}

	public static SearchNode? Parse(string sKey)	// 검색어 → 조건 트리 (빈 검색어: null, 형식 오류: 전체를 검색어 하나로)
	{
		string	sTrim	= sKey.Trim();

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

	public bool IsMatch(ReadOnlySpan<char> kLine)	// 줄이 조건에 맞음 (검색어: 대소문자 무시)
	{
		switch (m_eOp)
		{
			case SEARCH_OP.SEARCH_OP_AND:
			{
				return m_kLeft!.IsMatch(kLine) && m_kRight!.IsMatch(kLine);
			}

			case SEARCH_OP.SEARCH_OP_OR:
			{
				return m_kLeft!.IsMatch(kLine) || m_kRight!.IsMatch(kLine);
			}

			case SEARCH_OP.SEARCH_OP_NOT:
			{
				return m_kLeft!.IsMatch(kLine) && !m_kRight!.IsMatch(kLine);
			}

			default:
			{
				return m_sWild != "" ? IsWildMatch(kLine, m_sWild, m_bWildHead, m_bWildTail) : kLine.IndexOf(m_sTerm, StringComparison.OrdinalIgnoreCase) >= 0;	// 와일드카드: 경계 확인
			}
		}
	}

	public string[] GetFirst()	// 블록에서 먼저 찾을 검색어 (맞는 줄에 이 중 하나 꼭 있음: OR 양쪽 합, AND 최단 검색어가 긴 쪽, NOT 왼쪽)
	{
		switch (m_eOp)
		{
			case SEARCH_OP.SEARCH_OP_AND:
			{
				string[]	arrLeft		= m_kLeft!.GetFirst();
				string[]	arrRight	= m_kRight!.GetFirst();

				return arrLeft.Min(sTerm => sTerm.Length) >= arrRight.Min(sTerm => sTerm.Length) ? arrLeft : arrRight;
			}

			case SEARCH_OP.SEARCH_OP_OR:
			{
				return m_kLeft!.GetFirst().Concat(m_kRight!.GetFirst()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			}

			case SEARCH_OP.SEARCH_OP_NOT:
			{
				return m_kLeft!.GetFirst();
			}

			default:
			{
				return [m_sWild != "" ? m_sWild : m_sTerm];	// 와일드카드: * 뗀 글자
			}
		}
	}

	private static SearchNode? ParseExpr(List<(char cType, string sTerm)> listToken, ref int nIndex)	// 식: 항 (연산자 항)… 왼쪽부터 차례로 (null: 형식 오류)
	{
		SearchNode?	kLeft	= ParseTerm(listToken, ref nIndex);	// 지금까지 결과

		while (kLeft != null && nIndex < listToken.Count && listToken[nIndex].cType != ')')
		{
			SEARCH_OP eOp = listToken[nIndex].cType switch
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

			SearchNode?	kRight	= ParseTerm(listToken, ref nIndex);

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

		SearchNode?	kInner	= ParseExpr(listToken, ref nIndex);	// 괄호 안

		if (nIndex < listToken.Count && listToken[nIndex].cType == ')')
		{
			++nIndex;
		}

		return kInner;
	}

	private static List<(char cType, string sTerm)>? Tokenize(string sKey)	// 토큰 나누기: 검색어 T·AND &·OR |·NOT !·괄호 ( ) (null: 형식 오류 → 연산자 자리에 검색어, 연산자로 끝남)
	{
		List<(char cType, string sTerm)>	listToken	= new List<(char cType, string sTerm)>();
		int		nPos		= 0;	// 읽을 위치
		int		nDepth		= 0;	// 열린 괄호 수
		bool	bOperand	= true;	// 검색어·여는 괄호 차례 (false: 연산자·닫는 괄호 차례)

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

			if (bOperand && sKey[nPos] == '(')
			{
				listToken.Add(('(', ""));

				++nDepth;
				++nPos;

				continue;
			}

			if (bOperand)	// 검색어: 다음 연산자·닫는 괄호 앞까지
			{
				int	nEnd	= FindTermEnd(sKey, nPos, nDepth);

				listToken.Add(('T', sKey[nPos..nEnd].Trim()));

				nPos		= nEnd;
				bOperand	= false;

				continue;
			}

			if (sKey[nPos] == ')' && nDepth > 0)
			{
				listToken.Add((')', ""));

				--nDepth;
				++nPos;

				continue;
			}

			int	nOpLength	= GetOperator(sKey, nPos, out char cType);	// 연산자 길이 (0: 아님)

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

	private static int FindTermEnd(string sKey, int nStart, int nDepth)	// 검색어 끝 위치: 공백 뒤 연산자 또는 (괄호 안) 닫는 괄호 앞 (검색어 안 짝 괄호: 검색어 글자)
	{
		int	nInner	= 0;	// 검색어 안에서 열린 괄호 수 (예: Init(x))

		for (int nIndex = nStart; nIndex < sKey.Length; ++nIndex)
		{
			char	cChar	= sKey[nIndex];

			if (char.IsWhiteSpace(cChar))	// 공백 뒤 연산자·닫는 괄호면 여기서 끝
			{
				int	nNext	= nIndex;

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

	private static bool IsClose(string sKey, int nPos)	// nPos 의 ')' 가 닫는 괄호: 뒤에 공백·')' 만 이어진 뒤 끝 또는 연산자 (아니면 검색어 글자)
	{
		int	nNext	= nPos + 1;

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
			int	nNext	= nPos + sWord.Length;

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

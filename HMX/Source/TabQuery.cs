static class TabQuery	// 쿼리 탭: 로그 감시 (대상 폴더 탐색·최신 시간 로그 aLogType 줄 → 서버 전송)·받은 진행 (Count) 동기화·줄 필드 (서버 aField)
{
	private const int	m_nFieldKeyMax		= 32;	// aField 키 최대 글자 (서버와 같음)
	private const int	m_nFieldValueMax	= 260;	// aField 고정값 최대 글자 (서버 필드 값 최대와 같음)
	private const int	m_nFieldRegexMax	= 500;	// aField 정규식 최대 글자
	private const int	m_nFieldRegexMs		= 100;	// aField 정규식 한 줄 제한 (ms, 넘음: 값 없음)
	private const int	m_nLogWatchMs		= 1000;		// 로그 감시 주기 (ms)
	private const int	m_nNextCheckMs		= 2000;		// 다음 시간 파일 찾기 주기 (ms, 감시 파일 시간 경과 후)
	private const int	m_nLogScanMs		= 60000;	// 쿼리·서치 대상 폴더 탐색 주기 (ms)
	private const int	m_nCountWaitSec		= 10;		// 접속 때 서버 Count 답 대기 (초, 없음: 보정 없이 로그 전송)
	private const int	m_nLogTextMax		= 10000;	// 로그 감시 줄 최대 글자 (초과: "…")

	private static readonly Encoding				m_kUtf8			= new UTF8Encoding(false);	// UTF-8 (BOM 없음, 재사용)
	public static readonly TaskCompletionSource				m_kLogReady		= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);	// 쿼리 준비 끝 (준비 순서: 쿼리 → 서치)
	public static string[]							m_arrBlockQuery = Array.Empty<string>();	// aBlockQuery 막는 경로 (전체 경로·\이름\, 자신·하위: 쿼리 감시 제외, 다른 탭: 그대로)
	private static readonly List<string>						m_listLogTarget	= new List<string>();	// 찾은 대상 폴더 전체 (막기 전, 로그 감시 스레드 전용)
	private static readonly List<LogWatch>						m_listLogWatch	= new List<LogWatch>();	// 로그 감시 (대상 폴더마다, aBlockQuery 제외, 로그 감시 스레드 전용)
	private static readonly List<string>						m_listCountDel	= new List<string>();	// 사라진 감시 폴더 (서버 관리 제외 알릴 것, 로그 감시 스레드 전용)
	private static string										m_sLogScanError	= "";	// 마지막 대상 폴더 탐색 오류 (같은 오류 반복 출력 방지)
	private static string[]							m_arrLogType = Array.Empty<string>();	// 보낼 로그 종류 (두 번째 [] 안, 예: ERROR)
	private static volatile LogField[]							m_arrField		= Array.Empty<LogField>();	// 보낼 줄 필드 (서버 Count 답·얼라이브 답 aField, 접속마다 새로, 바꿀 때 배열 통째로)
	private static volatile string?							m_sServerField;	// 마지막 받은 서버 aField 글자 (같음: 다시 안 만듦, null: 이 접속 아직)
	private static bool										m_bCountLoaded;	// 시작 때 로그 감시 상태 읽음 (뒤에 생긴 대상: 바로 읽음, 로그 감시 스레드 전용)
	private static volatile string[]							m_arrCountFolder	= Array.Empty<string>();	// 로그 감시 대상 폴더 (접속 때 서버로, 탐색 시 새 배열로 교체 → 다른 스레드 읽기)
	public static readonly ConcurrentQueue<(long nGen, string[] arrFolder, Dictionary<string, (string sRel, long nLine)> hmapCount)>	m_queServerCount	= new ConcurrentQueue<(long nGen, string[] arrFolder, Dictionary<string, (string sRel, long nLine)> hmapCount)>();	// 서버 답 (접속 Count·발견 CountAdd: 접속 번호·등록 폴더·받은 진행, 로그 감시 스레드가 꺼내 등록·보정)
	public static volatile TaskCompletionSource<Dictionary<string, (string sRel, long nLine)>>?	m_kCountReply;	// 접속 때 Count 답 기다림 (수신 처리가 채움)
	public static long										m_nCountGen;		// 접속 번호 (Count 동기화마다 +1, 등록 기준: 접속 바뀜 → 모두 미등록)
	private static volatile bool								m_bCountServer;		// 서버 Count 지원 (이 접속 Count 답 받음, 예전 서버: false → 등록 없이 모두 전송)
	public static volatile LineWriter?						m_kLogWriter;	// 로그 감시 전송 (접속 중: Hello 뒤 설정, 끊김: null → 로그 감시 쉼)
	public static volatile bool								m_bLogPause;	// 서버 Pause 받음 (분당 상위 클라만, Resume·재접속: 해제, 수신 루프가 순서대로 바꿈)
	private static int								m_nLogSend;		// 설정 nLogSend
	private static int								m_nLogRead;		// 설정 nLogRead

	public static void Init(string[] arrBlockQuery, string[] arrLogType, int nLogSend, int nLogRead)	// 설정 → 막는 경로 (aBlockQuery)·보낼 로그 종류·전송·읽기 줄 수 (Main.Create)
	{
		m_arrBlockQuery	= Tab.GetBlockRules(arrBlockQuery, "aBlockQuery");
		m_arrLogType	= arrLogType;
		m_nLogSend		= nLogSend;
		m_nLogRead		= nLogRead;
	}

	public static async Task LogWatchLoop(CancellationToken kStop)	// 로그 감시: m_nLogWatchMs 마다 대상 폴더별 감시 파일 새 줄 검사 → aLogType 줄 전송 (aLogType 없음: 대상 탐색만)
	{
		bool bSend = m_arrLogType.Length > 0;

		Log.Write(INFO_, bSend ? $"쿼리 정보: aLogFolder {Tab.m_arrLogRoot.Length}개, aLogType {string.Join("·", m_arrLogType)}" : $"쿼리 안 함: aLogType 없음 (aLogFolder {Tab.m_arrLogRoot.Length}개, 서치 대상 탐색만)");

		ScanLogFolder();

		if (m_listLogWatch.Count == 0)
		{
			Log.Write(INFO_, m_listLogTarget.Count == 0 ? $"쿼리 대상 없음: aLogFolder 자신·하위 {Define.m_nLogScanDepth}단계 폴더 중 날짜 폴더(yyyyMMdd) 가진 폴더 없음 ({m_nLogScanMs / 1000}초마다 다시 탐색)" : $"쿼리 대상 없음: 대상 폴더 {m_listLogTarget.Count}개 모두 aBlockQuery ({m_nLogScanMs / 1000}초마다 다시 탐색)");
		}

		if (bSend)
		{
			LoadLogState();
		}

		m_kLogReady.TrySetResult();

		long nScanTick = Environment.TickCount64;

		try
		{
			while (!kStop.IsCancellationRequested)
			{
				if (Environment.TickCount64 - nScanTick >= m_nLogScanMs)	// 새 대상 폴더 탐색 (뒤에 생긴 폴더)
				{
					nScanTick = Environment.TickCount64;

					ScanLogFolder();
				}

				CloseMissingLog();	// 감시 파일 사라짐: 핸들 바로 닫음 (접속 여부 무관)

				LineWriter? kWriter = m_kLogWriter;	// 접속 안 됨·Pause: 이번 주기 쉼 (위치 유지)

				if (bSend && kWriter != null && !m_bLogPause)
				{
					long nGen = ApplyServerCount();	// 서버 등록·받은 진행으로 보정 (접속 Count·발견 CountAdd 답, 첫 검사 전), 반환: 이 접속 번호

					await RequestCountDel(kWriter, kStop);			// 사라진 폴더: 서버 관리 제외 알림
					await RequestCountAdd(kWriter, nGen, kStop);	// 새로 발견한 폴더: 서버 등록 요청 (답 오면 그 폴더도 전송)

					foreach (LogWatch kWatch in m_listLogWatch)
					{
						if (m_bCountServer && kWatch.m_nServerGen != nGen)	// 서버 등록 전 (새로 발견: 등록 답 기다림): 위치 유지
						{
							continue;
						}

						try
						{
							await CheckLog(kWatch, kWriter, m_nLogSend, m_nLogRead, kStop);

							kWatch.m_sError = "";
						}
						catch (Exception kException)	// 실패: 닫고 다음 주기 재시도 (위치 유지, 같은 오류 1회만 출력)
						{
							kWatch.Close();

							if (kStop.IsCancellationRequested)
							{
								break;
							}

							if (kWatch.m_sError != kException.Message)
							{
								kWatch.m_sError = kException.Message;

								Log.Write(WARN_, $"쿼리 오류: {kWatch.m_sPath ?? kWatch.m_sFolder} ({kException.Message})");
							}
						}
					}

					SaveLogState();	// 보낸 위치만
				}

				await Delay(m_nLogWatchMs, kStop);
			}
		}
		finally
		{
			foreach (LogWatch kWatch in m_listLogWatch)
			{
				kWatch.Close();
				kWatch.m_kCount.Close();
			}
		}
	}

	private static void ScanLogFolder()	// 대상 폴더 탐색: aLogFolder 자신·하위 중 바로 아래 날짜 폴더 가진 폴더 → 감시 추가 (기존 폴더 유지, 사라진 폴더: 감시 정리·서버 관리 제외, 새 폴더: 상태 있음 → 이어서, 없음: 최신 파일 끝부터)
	{
		bool bPruned = PruneLogFolder();	// 사라진 대상: 먼저 뺌

		try
		{
			EnumerationOptions kDirOption	= new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };																											// 바로 아래만 (날짜 폴더 확인)
			EnumerationOptions kScanOption	= new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint, RecurseSubdirectories = true, MaxRecursionDepth = Define.m_nLogScanDepth };	// 하위 폴더 (깊이 제한)

			foreach (string sRoot in Tab.m_arrLogRoot)
			{
				if (!Directory.Exists(sRoot))
				{
					continue;
				}

				FileSystemEnumerable<string> kSubDir = new FileSystemEnumerable<string>(sRoot, (ref FileSystemEntry kEntry) => kEntry.ToFullPath(), kScanOption)	// 하위 폴더 (날짜 폴더 자신·그 안은 제외)
				{
					ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => kEntry.IsDirectory && !Tab.TryGetDate(kEntry.FileName, out _),
					ShouldRecursePredicate	= (ref FileSystemEntry kEntry) => !Tab.TryGetDate(kEntry.FileName, out _),
				};

				foreach (string sFolder in kSubDir.Prepend(sRoot))
				{
					if (!HasDateDir(sFolder, kDirOption) || m_listLogTarget.Contains(sFolder, StringComparer.OrdinalIgnoreCase))
					{
						continue;
					}

					m_listLogTarget.Add(sFolder);
					m_listCountDel.RemoveAll(sGone => string.Equals(sGone, sFolder, StringComparison.OrdinalIgnoreCase));	// 다시 생김: 관리 제외 알림 취소

					if (Tab.IsBlocked(sFolder, m_arrBlockQuery))	// 쿼리 감시 제외 (서치: aBlockSearch 따로)
					{
						Log.Write(INFO_, $"쿼리 제외: {sFolder} (aBlockQuery)");
					}
					else
					{
						LogWatch kWatch = new LogWatch(sFolder);

						m_listLogWatch.Add(kWatch);

						Log.Write(INFO_, $"쿼리 대상: {sFolder}");

						if (m_bCountLoaded)	// 뒤에 생긴 대상: 상태 있음 → 이어서
						{
							LoadCount(kWatch, null);
						}
					}

					if (Tab.IsBlocked(sFolder, TabSearch.m_arrBlockSearch))
					{
						Log.Write(INFO_, $"서치 제외: {sFolder} (aBlockSearch)");
					}
				}
			}

			m_sLogScanError = "";
		}
		catch (Exception kException)	// 권한 없음·경로 오류 (같은 오류 1회만 출력)
		{
			if (m_sLogScanError != kException.Message)
			{
				m_sLogScanError = kException.Message;

				Log.Write(WARN_, $"쿼리 대상 탐색 오류: {kException.Message}");
			}
		}

		if (bPruned || m_arrCountFolder.Length != m_listLogWatch.Count)	// 로그 감시 대상 바뀜 (추가·사라짐): 접속 때 보낼 목록 새 배열
		{
			m_arrCountFolder = m_listLogWatch.Select(kWatch => kWatch.m_sFolder).ToArray();
		}

		string[] arrSearch = m_listLogTarget.Where(sFolder => !Tab.IsBlocked(sFolder, TabSearch.m_arrBlockSearch)).ToArray();	// 서치 대상 (aBlockSearch 제외)

		if (bPruned || TabSearch.m_arrLogFolder.Length != arrSearch.Length)	// 바뀜 (추가·사라짐): 서치 대상 새 배열
		{
			TabSearch.m_arrLogFolder = arrSearch;
		}
	}

	private static bool PruneLogFolder()	// 사라진 대상 폴더 (삭제·이름 변경): 감시 핸들·상태 파일 닫고 목록에서 뺌 → 서버 관리 제외 알림 대기 (상태 파일: 남김 → 다시 생기면 이어서), 반환: 뺀 것 있음
	{
		List<string> listGone = m_listLogTarget.Where(sFolder => !Directory.Exists(sFolder)).ToList();	// 사라진 대상 폴더

		foreach (string sFolder in listGone)
		{
			m_listLogTarget.Remove(sFolder);

			LogWatch? kWatch = m_listLogWatch.Find(kItem => string.Equals(kItem.m_sFolder, sFolder, StringComparison.OrdinalIgnoreCase));	// 그 폴더 감시 (aBlockQuery: 없음)

			if (kWatch == null)
			{
				continue;
			}

			kWatch.Close();
			kWatch.m_kCount.Close();

			m_listLogWatch.Remove(kWatch);
			m_listCountDel.Add(sFolder);

			Log.Write(INFO_, $"쿼리 대상 사라짐: {sFolder} (감시·상태 파일 닫음, 서버 관리 제외)");
		}

		return listGone.Count > 0;
	}

	private static void CloseMissingLog()	// 감시 파일 사라짐 (파일·폴더 삭제): 핸들 바로 닫음 (위치 유지, 폴더 삭제 막지 않음, 안 읽은 줄: 버림)
	{
		foreach (LogWatch kWatch in m_listLogWatch)
		{
			if (kWatch.m_kHandle != null && !File.Exists(kWatch.m_sPath))
			{
				kWatch.Close();

				Log.Write(INFO_, $"쿼리 파일 사라짐: {kWatch.m_sPath} (핸들 닫음)");
			}
		}
	}

	private static async Task RequestCountDel(LineWriter kWriter, CancellationToken kStop)	// 사라진 감시 폴더 → 서버 관리 제외 한 패킷 {"sType":"CountDel","arrFolder":[]} (답 없음, 예전 서버: 안 보냄, 보내기 실패: 다음 접속 Count 목록에 없음 → 서버가 정리)
	{
		if (m_listCountDel.Count == 0)
		{
			return;
		}

		string[] arrFolder = m_listCountDel.ToArray();

		m_listCountDel.Clear();

		if (!m_bCountServer)	// 예전 서버
		{
			return;
		}

		try
		{
			await kWriter.WriteAsync(JsonSerializer.Serialize(new { sType = "CountDel", arrFolder, sTime = GetNowText() }, Define.m_kJsonOption), kStop);
		}
		catch (Exception)	// 끊김: 다음 접속 Count 목록에 없음
		{
			return;
		}

		Log.Write(INFO_, $"쿼리 관리 제외 요청: 사라진 대상 폴더 {arrFolder.Length}개 ({string.Join(", ", arrFolder)})");
	}

	private static bool HasDateDir(string sFolder, EnumerationOptions kOption)	// 바로 아래 날짜 폴더(yyyyMMdd) 있음 (첫 개 찾으면 끝)
	{
		FileSystemEnumerable<byte> kDateDir = new FileSystemEnumerable<byte>(sFolder, (ref FileSystemEntry kEntry) => 0, kOption)
		{
			ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => kEntry.IsDirectory && Tab.TryGetDate(kEntry.FileName, out _),
		};

		return kDateDir.Any();
	}

	private static string GetOldStateFile()	// 예전 로그 감시 상태 파일: 기준 폴더 LMClient.data (더 예전: LMClient.txt, 시작 때 Data\Count 로 옮기고 지움)
	{
		return Path.Combine(GetBaseDir(), "LMClient.data");	// 예전 이름 고정 (프로젝트 이름 바뀜과 무관)
	}

	private static void LoadLogState()	// 로그 감시 상태 읽기: 대상 폴더마다 Count (없음: 예전 LMClient.data 줄) → 본 줄 다음부터, 감시 대상 아닌 폴더 상태: 지움, 예전 파일: 모두 옮기면 지움 (실패: 다음 시작 때 다시)
	{
		Dictionary<string, string>?	hmapOld	= LoadOldState();	// 예전 상태: 대상 폴더 → "날짜 폴더\파일\t줄 번호" (읽기 실패: null → 안 지움)
		bool						bMoved	= hmapOld != null;	// 모두 옮김

		foreach (LogWatch kWatch in m_listLogWatch)
		{
			bMoved &= LoadCount(kWatch, hmapOld?.GetValueOrDefault(kWatch.m_sFolder));
		}

		m_bCountLoaded = true;

		Count.DeleteUnused(GetMachine(), m_listLogWatch.Select(kWatch => kWatch.m_kCount.m_sFile));	// 감시 대상 아닌 폴더 (대상 밖·aBlockQuery) 상태

		string		sOld		= GetOldStateFile();
		string[]	arrOld		= [sOld, sOld + ".tmp", Path.ChangeExtension(sOld, ".txt")];	// 예전 파일 (LMClient.data·임시 파일·LMClient.txt)

		if (!bMoved || !arrOld.Any(File.Exists))
		{
			return;
		}

		try
		{
			foreach (string sFile in arrOld)
			{
				File.Delete(sFile);
			}

			Log.Write(INFO_, $"쿼리 상태 옮김: {sOld} → Data\\Count (예전 파일 지움)");
		}
		catch (Exception kException)	// 사용 중·권한 없음: 그대로 둠
		{
			Log.Write(WARN_, $"예전 쿼리 상태 지우기 실패: {sOld} ({kException.Message})");
		}
	}

	private static Dictionary<string, string>? LoadOldState()	// 예전 상태 파일 "전체 경로\t줄 번호" → 대상 폴더 → "날짜 폴더\파일\t줄 번호" (폴더마다 첫 줄, 파일 없음: 빈 목록, 읽기 실패: null)
	{
		Dictionary<string, string>	hmapOld	= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string						sFile	= GetOldStateFile();

		if (!File.Exists(sFile))
		{
			sFile = Path.ChangeExtension(sFile, ".txt");	// 더 예전 이름
		}

		try
		{
			if (!File.Exists(sFile))
			{
				return hmapOld;
			}

			foreach (string sState in File.ReadAllLines(sFile, m_kUtf8))
			{
				string[]	arrPart	= sState.Trim().Split('\t', 2);								// 전체 경로, 줄 번호
				string		sPath	= arrPart[0].Trim();										// 감시 파일 전체 경로
				string?		sDate	= Path.GetDirectoryName(sPath);								// 날짜 폴더
				string?		sFolder	= sDate == null ? null : Path.GetDirectoryName(sDate);		// 대상 폴더 (대상\날짜\파일)

				if (sFolder != null)
				{
					hmapOld.TryAdd(sFolder, $"{Path.GetFileName(sDate)}\\{Path.GetFileName(sPath)}\t{(arrPart.Length > 1 ? arrPart[1] : "")}");
				}
			}

			return hmapOld;
		}
		catch (Exception kException)	// 권한 없음·사용 중
		{
			Log.Write(WARN_, $"예전 쿼리 상태 읽기 실패: {sFile} ({kException.Message})");

			return null;
		}
	}

	private static bool LoadCount(LogWatch kWatch, string? sOld)	// 대상 폴더 상태 읽기 (Count, 없음: 예전 상태 줄 → 새 파일에 씀) → 본 줄 다음부터 (반환: 실패 false)
	//   줄 번호만 깨짐: 그 파일 마지막 줄까지 본 것으로 복구 (파일 없음: 다음 시간 파일 처음부터), 상태 없음·경로 깨짐: 최신 파일 끝부터
	{
		bool bDone = kWatch.m_kCount.Load(sOld, out string sRel, out long nLine);

		if (sRel == "")	// 상태 없음·경로 깨짐
		{
			return bDone;
		}

		SetLogPosition(kWatch, sRel, nLine);

		if (nLine < 0)	// 줄 번호만 깨짐: 그 파일 끝부터 (첫 저장 때 바른 줄로)
		{
			Log.Write(WARN_, $"쿼리 상태 복구: {kWatch.m_sPath} (줄 번호 오류 → 마지막 줄까지 본 것으로{(File.Exists(kWatch.m_sPath) ? "" : ", 파일 없음 → 다음 시간 파일부터")})");
		}
		else
		{
			Log.Write(INFO_, $"쿼리 시작: {kWatch.m_sPath} ({nLine}줄까지 봄{(File.Exists(kWatch.m_sPath) ? "" : ", 파일 없음 → 다음 시간 파일부터")})");
		}

		return bDone;
	}

	private static void SetLogPosition(LogWatch kWatch, string sRel, long nLine)	// 감시 위치 바꿈: 대상 폴더\날짜 폴더\파일 의 nLine 줄 다음부터 (-1: 그 파일 끝난 줄 끝, 열 때 정함)
	{
		kWatch.Close();

		kWatch.m_sPath		= Path.Join(kWatch.m_sFolder, sRel);
		kWatch.m_nOffset	= -1;		// 열 때 정함
		kWatch.m_nLine		= nLine;	// 본 줄 다음 (-1: 그 파일 끝난 줄 끝 = 마지막 줄까지 본 것으로, 파일 없음: 다음 시간 파일 처음부터)
	}

	private static long ApplyServerCount()	// 서버 답 (접속 Count·발견 CountAdd) → 답 폴더 등록 (이 접속 전송) + 받은 진행 있음: 그 위치부터 (서버 앞섬 → 건너뜀 (중복 방지), 뒤짐 → 다시 보냄 (유실 방지), 없음: 그대로 (지난 답도 없음 + 그 뒤 보냄: 서버 저장 전 끊김 → 그 뒤 처음 보낸 위치부터 다시)), 지난 접속 답: 버림 (반환: 이 접속 번호)
	{
		long nGen = Interlocked.Read(ref m_nCountGen);	// 이 접속 번호

		while (m_queServerCount.TryDequeue(out (long nGen, string[] arrFolder, Dictionary<string, (string sRel, long nLine)> hmapCount) kReply))
		{
			if (kReply.nGen != nGen)	// 지난 접속 답
			{
				continue;
			}

			HashSet<string> setFolder = kReply.arrFolder.ToHashSet(StringComparer.OrdinalIgnoreCase);	// 등록된 폴더

			foreach (LogWatch kWatch in m_listLogWatch.Where(kWatch => setFolder.Contains(kWatch.m_sFolder)))
			{
				kWatch.m_nServerGen = nGen;	// 등록: 이 접속 동안 전송

				string sNow = string.IsNullOrEmpty(kWatch.m_sPath) ? "" : Path.GetRelativePath(kWatch.m_sFolder, kWatch.m_sPath);	// 지금 위치 (날짜 폴더\파일)

				if (!kReply.hmapCount.TryGetValue(kWatch.m_sFolder, out (string sRel, long nLine) kServer) || kServer.nLine < 0 || Count.GetDateHour(kServer.sRel) < 0)	// 서버 받은 진행 없음·형식 오류
				{
					if (kWatch.m_bNoCount && kWatch.m_sNoCountRel != "")	// 지난 답도 없음 + 그 뒤 보냄: 서버 저장 전 끊김 (재시작 등) → 그 뒤 처음 보낸 위치부터 다시 (유실 방지)
					{
						Log.Write(NOTI_, $"쿼리 보정: {kWatch.m_sFolder} ({(sNow == "" ? "없음" : $"{sNow} {kWatch.m_nLine}줄")} → 서버 받은 진행 없음, 그 뒤 처음 보낸 {kWatch.m_sNoCountRel} {kWatch.m_nNoCountLine}줄 다음부터)");

						SetLogPosition(kWatch, kWatch.m_sNoCountRel, kWatch.m_nNoCountLine);

						kWatch.m_kCount.Save(kWatch.m_sNoCountRel, kWatch.m_nNoCountLine);
					}

					kWatch.m_bNoCount		= true;
					kWatch.m_sNoCountRel	= "";	// 다음 보낼 때 다시 기록

					continue;
				}

				kWatch.m_bNoCount		= false;	// 서버 받은 진행 있음: 이후 보정은 서버 진행으로
				kWatch.m_sNoCountRel	= "";

				if (string.Equals(sNow, kServer.sRel, StringComparison.OrdinalIgnoreCase) && kWatch.m_nLine == kServer.nLine)	// 같음
				{
					continue;
				}

				Log.Write(NOTI_, $"쿼리 보정: {kWatch.m_sFolder} ({(sNow == "" ? "없음" : $"{sNow} {kWatch.m_nLine}줄")} → 서버 {kServer.sRel} {kServer.nLine}줄)");

				SetLogPosition(kWatch, kServer.sRel, kServer.nLine);

				kWatch.m_kCount.Save(kServer.sRel, kServer.nLine);
			}
		}

		return nGen;
	}

	private static async Task RequestCountAdd(LineWriter kWriter, long nGen, CancellationToken kStop)	// 새로 발견한 대상 폴더 (이 접속 미등록·미요청) → 서버 등록 요청 한 패킷 {"sType":"CountAdd","arrFolder":[]} (답 → ApplyServerCount 가 등록, 예전 서버: 안 함, 보내기 실패: 다음 주기 다시)
	{
		if (!m_bCountServer)
		{
			return;
		}

		List<LogWatch> listNew = m_listLogWatch.Where(kWatch => kWatch.m_nServerGen != nGen && kWatch.m_nRequestGen != nGen).ToList();	// 등록 요청할 폴더

		if (listNew.Count == 0)
		{
			return;
		}

		string[] arrFolder = listNew.Select(kWatch => kWatch.m_sFolder).ToArray();

		try
		{
			await kWriter.WriteAsync(JsonSerializer.Serialize(new { sType = "CountAdd", arrFolder, sTime = GetNowText() }, Define.m_kJsonOption), kStop);
		}
		catch (Exception)	// 끊김: 다음 접속 Count 목록에 들어감
		{
			return;
		}

		foreach (LogWatch kWatch in listNew)
		{
			kWatch.m_nRequestGen = nGen;
		}

		Log.Write(INFO_, $"쿼리 등록 요청: 새 대상 폴더 {arrFolder.Length}개 ({string.Join(", ", arrFolder)})");
	}

	private static void SaveLogState()	// 로그 감시 상태 저장: 대상 폴더마다 바뀐 것만 Count (실패: 다음 주기 재시도)
	{
		foreach (LogWatch kWatch in m_listLogWatch)
		{
			if (string.IsNullOrEmpty(kWatch.m_sPath) || kWatch.m_nLine < 0)
			{
				continue;
			}

			kWatch.m_kCount.Save(Path.GetRelativePath(kWatch.m_sFolder, kWatch.m_sPath), kWatch.m_nLine);	// 날짜 폴더\파일, 줄 번호
		}
	}

	private static async Task<(int nSent, int nCheck)> CheckLog(LogWatch kWatch, LineWriter kWriter, int nSendMax, int nCheckMax, CancellationToken kStop)	// 감시 파일 새 줄 검사·전송 (다음 시간 파일: 지금 파일 끝까지 본 뒤 이동)
	{
		string	sPath	= string.IsNullOrEmpty(kWatch.m_sPath) ? GetLatestLog(kWatch.m_sFolder) : (NeedNextCheck(kWatch) ? GetNextLog(kWatch.m_sFolder, kWatch.m_sPath) : "");	// 옮겨 갈 파일 (처음·파일 없음: 최신 파일, 그 외: 다음 시간 파일, "": 없음)
		int		nSent	= 0;
		int		nCheck	= 0;

		if (sPath != "" && !string.Equals(sPath, kWatch.m_sPath, StringComparison.OrdinalIgnoreCase))	// 감시 파일 바뀜 (클라 시작·다음 시간 파일)
		{
			if (!string.IsNullOrEmpty(kWatch.m_sPath))	// 이전 파일: 남은 줄 모두 전송 (시간 무관, 최대 줄 수·전송 실패: 다음 주기 이어서, 열기 실패: 넘어감)
			{
				bool bOpen = File.Exists(kWatch.m_sPath);	// 없음: 바로 넘어감

				try
				{
					if (bOpen)
					{
						OpenLog(kWatch);
					}
				}
				catch (Exception kException)	// 이전 파일 삭제·열기 실패
				{
					bOpen = false;

					Log.Write(WARN_, $"쿼리 이전 파일 열기 실패: {kWatch.m_sPath} ({kException.Message}, 넘어감)");
				}

				if (bOpen)
				{
					(nSent, nCheck, bool bMore) = await SendLog(kWatch, true, kWriter, nSendMax, nCheckMax, kStop);

					if (bMore)	// 남은 줄 있음: 다음 주기 이어서
					{
						return (nSent, nCheck);
					}
				}
			}

			kWatch.Close();

			kWatch.m_nOffset	= kWatch.m_sPath == null ? -1 : 0;	// 클라 시작 (txt 없음): 열 때 끝난 줄 끝, 새 파일: 처음부터
			kWatch.m_nLine		= kWatch.m_sPath == null ? -1 : 0;
			kWatch.m_sPath		= sPath;

			Log.Write(INFO_, $"쿼리: {sPath}");
		}
		else if (kWatch.m_sPath == null)	// 클라 시작에 최신 파일 없음: 이후 생기는 파일은 처음부터
		{
			kWatch.m_sPath = "";

			Log.Write(INFO_, $"쿼리: {kWatch.m_sFolder} (최신 파일 없음)");
		}

		if (string.IsNullOrEmpty(kWatch.m_sPath) || nSent >= nSendMax || nCheck >= nCheckMax)	// 감시 파일 없음·다 씀
		{
			return (nSent, nCheck);
		}

		if (kWatch.m_kHandle == null && !File.Exists(kWatch.m_sPath))	// 감시 파일 사라짐: 열지 않음 (다음 시간 파일 기다림, 매 주기 예외 방지)
		{
			return (nSent, nCheck);
		}

		OpenLog(kWatch);

		(int nNewSent, int nNewCheck, _) = await SendLog(kWatch, false, kWriter, nSendMax - nSent, nCheckMax - nCheck, kStop);

		return (nSent + nNewSent, nCheck + nNewCheck);
	}

	private static async Task<(int nSent, int nCheck, bool bMore)> SendLog(LogWatch kWatch, bool bAll, LineWriter kWriter, int nSendMax, int nCheckMax, CancellationToken kStop)	// ReadLog 줄 전송 → 보낸 뒤에만 위치 반영 (전송 실패: 예외)
	{
		(List<string> listLine, List<long> listNo, long nOffset, long nLine, int nCheck, bool bMore) = ReadLog(kWatch, bAll, nSendMax, nCheckMax);

		if (listLine.Count > 0)
		{
			LogField[] arrField = m_arrField;	// 줄 필드 (이 건 동안 같은 것)

			string sLine = JsonSerializer.Serialize(new
			{
				sType	= "Log",
				sPath	= kWatch.m_sPath,
				sFolder	= kWatch.m_sFolder,	// 서버 쿼리 Folder 필드
				arrLine	= listLine,
				arrNo	= listNo,	// 줄 번호 (arrLine 과 같은 순서, 파일 첫 줄 1)
				arrField	= arrField.Length == 0 ? null : listLine.Select(sText => GetLineField(arrField, sText)).ToList(),	// 줄 필드 (arrLine 과 같은 순서)
				sTime	= GetNowText(),
			}, Define.m_kJsonOption);

			await kWriter.WriteAsync(sLine, kStop);

			if (kWatch.m_bNoCount && kWatch.m_sNoCountRel == "" && kWatch.m_nLine >= 0)	// 서버 받은 진행 없음 뒤 처음 보냄: 보내기 전 위치 기록 (다음 답도 없음 → 여기부터 다시)
			{
				kWatch.m_sNoCountRel	= Path.GetRelativePath(kWatch.m_sFolder, kWatch.m_sPath!);
				kWatch.m_nNoCountLine	= kWatch.m_nLine;
			}
		}

		kWatch.m_nOffset	= nOffset;
		kWatch.m_nLine		= nLine;

		return (listLine.Count, nCheck, bMore);
	}

	private static Dictionary<string, string> GetLineField(LogField[] arrField, string sLine)	// 한 줄 aField 값 {키: 값} (고정값: 그대로, 정규식 첫 () 값, 없음: 맞은 부분, 빈 값·시간 초과: 제외)
	{
		Dictionary<string, string> hmapField = new Dictionary<string, string>();	// 값 있는 키만

		foreach (LogField kField in arrField)
		{
			if (kField.kRegex == null)	// 고정값 (sValue)
			{
				hmapField[kField.sKey] = kField.sValue;

				continue;
			}

			try
			{
				Match	kMatch	= kField.kRegex.Match(sLine);
				string	sValue	= !kMatch.Success ? "" : (kMatch.Groups.Count > 1 && kMatch.Groups[1].Success ? kMatch.Groups[1].Value : kMatch.Value).Trim();

				if (sValue != "")
				{
					hmapField[kField.sKey] = sValue;
				}
			}
			catch (RegexMatchTimeoutException)	// 정규식 시간 넘음: 값 없음
			{
			}
		}

		return hmapField;
	}

	private static void OpenLog(LogWatch kWatch)	// 감시 파일 열기 (이미 열림: 그대로) + 처음 열 때 읽기 위치 (txt 본 줄 다음, txt 없음: 끝난 줄 끝)
	{
		if (kWatch.m_kHandle != null)
		{
			return;
		}

		kWatch.m_kHandle = File.OpenHandle(kWatch.m_sPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);	// 읽기 전용: 쓰기·삭제·이름 변경 허용 (다른 프로세스 영향 없음)

		if (kWatch.m_nOffset >= 0)	// 위치 정해짐 (새 파일·다시 열기)
		{
			return;
		}

		if (kWatch.m_nLine < 0)	// txt 없음: 끝난 줄 끝부터
		{
			kWatch.m_nOffset	= GetLastLineEnd(kWatch.m_kHandle);
			kWatch.m_nLine		= CountLineEnd(kWatch.m_kHandle, kWatch.m_nOffset);

			return;
		}

		kWatch.m_nOffset = FindLineStart(kWatch.m_kHandle, kWatch.m_nLine);	// txt 본 줄 다음

		if (kWatch.m_nOffset < 0)	// 줄 모자람 (다시 쓴 파일): 처음부터
		{
			kWatch.m_nOffset	= 0;
			kWatch.m_nLine		= 0;
		}
	}

	private static (List<string> listLine, List<long> listNo, long nOffset, long nLine, int nCheck, bool bMore) ReadLog(LogWatch kWatch, bool bAll, int nSendMax, int nCheckMax)	// 본 줄 다음 끝난 줄 검사 (시간이 지금 초 미만인 줄까지, bAll: 파일 끝까지, 최대 줄 수 차면 멈춤, 위치 반영은 SendLog)
	{
		SafeFileHandle	kHandle		= kWatch.m_kHandle!;
		long			nLength		= RandomAccess.GetLength(kHandle);
		DateTimeOffset	kNow		= GetNow();
		long			nCutoff		= bAll ? long.MaxValue : ((((kNow.Year * 100L + kNow.Month) * 100 + kNow.Day) * 100 + kNow.Hour) * 100 + kNow.Minute) * 100 + kNow.Second;	// 기준 시각 (yyyyMMddHHmmss, 이 시각 전 줄만)
		List<string>	listLine	= new List<string>();
		List<long>		listNo		= new List<long>();					// 보낼 줄 번호 (파일 첫 줄 1)
		bool			bStop		= false;							// 멈춤 (기준 시각 이후 줄·최대 줄 수)
		bool			bMore		= false;							// 최대 줄 수라 남은 줄 있음
		int				nCheck		= 0;
		long			nOffset		= kWatch.m_nOffset;					// 다음 읽기 위치
		long			nLine		= kWatch.m_nLine;					// 본 줄까지

		if (nLength < nOffset)	// 파일이 줄어듦 (다시 씀): 처음부터
		{
			nOffset	= 0;
			nLine	= 0;
		}

		if (nLength == nOffset || nSendMax <= 0 || nCheckMax <= 0)	// 새 내용 없음·최대 줄 수 다 씀
		{
			return (listLine, listNo, nOffset, nLine, nCheck, nLength != nOffset);
		}

		byte[] arrBytes = ArrayPool<byte>.Shared.Rent(Define.m_nBlockBytes);
		char[] arrChars = ArrayPool<char>.Shared.Rent(Define.m_nBlockBytes);	// 글자 수 ≤ 바이트 수

		try
		{
			while (!bStop && nOffset < nLength)
			{
				int nRead = RandomAccess.Read(kHandle, arrBytes.AsSpan(0, (int)Math.Min(Define.m_nBlockBytes, nLength - nOffset)), nOffset);

				if (nRead <= 0)
				{
					break;
				}

				ReadOnlySpan<byte>	kBlock	= arrBytes.AsSpan(0, nRead);
				bool				bEnd	= nOffset + nRead >= nLength;											// 파일 끝까지 읽음
				int					nBom	= nOffset == 0 && kBlock.StartsWith("\uFEFF"u8) ? 3 : 0;				// UTF-8 BOM 건너뜀
				int					nEnd	= nBom;																			// 볼 끝 (상대)
				long				nBlockLine	= 0;																// 이 블록에서 지난 줄 수 (줄 번호 = nLine + nBlockLine + 1)
				int					nIndex;																					// 줄 끝 (상대)

				while ((nIndex = kBlock.Slice(nEnd).IndexOf((byte)'\n')) >= 0)	// 끝난 줄마다 시간 확인 → aLogType 줄 모음
				{
					if (TryGetLogTime(kBlock.Slice(nEnd, nIndex), out long nTime) && nTime >= nCutoff)	// 기준 시각 이후 줄: 다음 주기
					{
						bStop = true;

						break;
					}

					AddLogLine(kBlock.Slice(nEnd, nIndex), arrChars, listLine, listNo, nLine + ++nBlockLine);

					nEnd += nIndex + 1;

					if (++nCheck >= nCheckMax || listLine.Count >= nSendMax)	// 최대 줄 수: 남은 줄은 다음 주기
					{
						bStop = true;
						bMore = nOffset + nEnd < nLength;

						break;
					}
				}

				if (!bStop && bAll && bEnd && nEnd < nRead)	// 파일 끝까지 모두 (끝나지 않은 마지막 줄 포함)
				{
					AddLogLine(kBlock.Slice(nEnd), arrChars, listLine, listNo, nLine + nBlockLine + 1);

					++nCheck;

					nEnd = nRead;
				}
				else if (!bStop && nEnd == nBom && !bEnd)	// 블록보다 긴 줄: 블록째 (앞 조각만 종류·시간 확인)
				{
					if (TryGetLogTime(kBlock.Slice(nBom), out long nTime) && nTime >= nCutoff)
					{
						bStop = true;
					}
					else
					{
						AddLogLine(kBlock.Slice(nBom), arrChars, listLine, listNo, nLine + 1);

						nEnd = nRead;

						if (++nCheck >= nCheckMax || listLine.Count >= nSendMax)	// 최대 줄 수
						{
							bStop = true;
							bMore = true;
						}
					}
				}

				if (nEnd == nBom)	// 볼 줄 없음 (끝나지 않은 줄·기준 시각 이후 줄)
				{
					break;
				}

				nOffset	+= nEnd;
				nLine	+= kBlock.Slice(0, nEnd).Count((byte)'\n');

				if (bEnd && nEnd < nRead)	// 파일 끝의 끝나지 않은 줄: 다음 주기
				{
					break;
				}
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(arrBytes);
			ArrayPool<char>.Shared.Return(arrChars);
		}

		return (listLine, listNo, nOffset, nLine, nCheck, bMore);
	}

	private static void AddLogLine(ReadOnlySpan<byte> kLine, char[] arrChars, List<string> listLine, List<long> listNo, long nNo)	// 한 줄 (바이트) → aLogType 줄이면 listLine·listNo 에 추가 (원문 시간 없음·값 틀림: 보내는 줄만 첫 [] 를 지금 시간으로 보정, 로그 파일 그대로)
	{
		ReadOnlySpan<char> kText = arrChars.AsSpan(0, Tab.Decode(kLine, arrChars));	// 글자 (글자 수 ≤ 바이트 수)

		if (IsLogType(kText))
		{
			listLine.Add(TryGetLogTime(kLine, out _) ? Tab.ToText(kText, m_nLogTextMax) : Tab.ToText(string.Concat(GetNow().ToString("[yyyy/MM/dd HH:mm:ss.fff]", CultureInfo.InvariantCulture), kText.Slice(kText.IndexOf(']') + 1)), m_nLogTextMax));	// 보정: 첫 [] → 지금 시간 (KST, 두 번째 [] 로그 종류 자리 그대로)
			listNo.Add(nNo);
		}
	}

	private static bool IsLogType(ReadOnlySpan<char> kLine)	// 두 번째 [] 안에 aLogType 포함 (대소문자 구분, 예: NOTI → [NOTI_])
	{
		int nTimeEnd = kLine.IndexOf(']');	// 첫 번째 [] 끝

		if (kLine.Length == 0 || kLine[0] != '[' || nTimeEnd < 0)
		{
			return false;
		}

		ReadOnlySpan<char>	kRest		= kLine.Slice(nTimeEnd + 1).TrimStart(' ');	// 두 번째 [] 부터
		int					nTypeEnd	= kRest.IndexOf(']');						// 두 번째 [] 끝

		if (kRest.Length == 0 || kRest[0] != '[' || nTypeEnd < 0)
		{
			return false;
		}

		foreach (string sType in m_arrLogType)
		{
			if (kRest.Slice(1, nTypeEnd - 1).Contains(sType, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private static bool TryGetLogTime(ReadOnlySpan<byte> kLine, out long nTime)	// 줄 앞 시간 ([yyyy/MM/dd HH:mm:ss.fff] → yyyyMMddHHmmss, 서버 원문 시간과 같은 형식, 형식 아님·없는 날짜·시각 (예: 13월·25시): false → 끝난 줄 판단 안 막음·보낼 때 지금 시간으로 보정)
	{
		nTime = 0;

		if (kLine.Length < 25 || kLine[0] != '[' || kLine[5] != '/' || kLine[8] != '/' || kLine[11] != ' ' || kLine[14] != ':' || kLine[17] != ':' || kLine[20] != '.' || kLine[24] != ']')
		{
			return false;
		}

		for (int nIndex = 1; nIndex < 24; ++nIndex)	// yyyy MM dd HH mm ss fff 숫자만 (구분자 건너뜀, nTime: 초까지)
		{
			if (nIndex == 5 || nIndex == 8 || nIndex == 11 || nIndex == 14 || nIndex == 17 || nIndex == 20)
			{
				continue;
			}

			int nDigit = kLine[nIndex] - '0';

			if ((uint)nDigit > 9)
			{
				return false;
			}

			if (nIndex < 20)
			{
				nTime = nTime * 10 + nDigit;
			}
		}

		long nYear		= nTime / 10000000000;		// 년
		long nMonth		= nTime / 100000000 % 100;	// 월
		long nDay		= nTime / 1000000 % 100;	// 일
		long nHour		= nTime / 10000 % 100;		// 시
		long nMinute	= nTime / 100 % 100;		// 분
		long nSecond	= nTime % 100;				// 초

		return nYear >= 1 && nMonth >= 1 && nMonth <= 12 && nDay >= 1 && nDay <= DateTime.DaysInMonth((int)nYear, (int)nMonth) && nHour <= 23 && nMinute <= 59 && nSecond <= 59;	// 있는 날짜·시각
	}

	private static long GetLastLineEnd(SafeFileHandle kHandle)	// 끝난 줄 끝 바이트 (마지막 '\n' 다음, 끝 블록 안에서 찾음)
	{
		long	nLength		= RandomAccess.GetLength(kHandle);
		int		nCount		= (int)Math.Min(nLength, Define.m_nBlockBytes);			// 끝에서 읽을 바이트
		long	nStart		= nLength - nCount;
		byte[]	arrBytes	= ArrayPool<byte>.Shared.Rent(Define.m_nBlockBytes);

		try
		{
			int nEnd = arrBytes.AsSpan(0, RandomAccess.Read(kHandle, arrBytes.AsSpan(0, nCount), nStart)).LastIndexOf((byte)'\n') + 1;	// 마지막 '\n' 다음 (상대, 0: 없음)

			return nEnd > 0 || nStart == 0 ? nStart + nEnd : nLength;	// 끝 블록에 줄 끝 없음 (아주 긴 줄): 파일 끝부터
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(arrBytes);
		}
	}

	private static long FindLineStart(SafeFileHandle kHandle, long nLine)	// nLine 번째 '\n' 다음 바이트 (nLine 0: 0, 줄 모자람: -1)
	{
		if (nLine <= 0)
		{
			return 0;
		}

		Throttle	kThrottle	= new Throttle();
		byte[]		arrBytes	= ArrayPool<byte>.Shared.Rent(Define.m_nBlockBytes);
		long		nCount		= 0;											// 지난 '\n' 수
		long		nPos		= 0;

		try
		{
			while (true)
			{
				int nRead = RandomAccess.Read(kHandle, arrBytes.AsSpan(0, Define.m_nBlockBytes), nPos);

				if (nRead <= 0)	// 파일 끝: 줄 모자람
				{
					return -1;
				}

				Span<byte>	kBlock		= arrBytes.AsSpan(0, nRead);
				int			nBlockCount	= kBlock.Count((byte)'\n');

				if (nCount + nBlockCount < nLine)	// 다음 블록에 있음
				{
					nCount	+= nBlockCount;
					nPos	+= nRead;

					kThrottle.Check();

					continue;
				}

				int nIndex = -1;	// 블록 안 '\n' 위치

				while (nCount < nLine)
				{
					nIndex += kBlock.Slice(nIndex + 1).IndexOf((byte)'\n') + 1;

					++nCount;
				}

				return nPos + nIndex + 1;
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(arrBytes);
		}
	}

	private static long CountLineEnd(SafeFileHandle kHandle, long nEnd)	// nEnd 앞 '\n' 수 (시작 줄 번호)
	{
		Throttle	kThrottle	= new Throttle();
		byte[]		arrBytes	= ArrayPool<byte>.Shared.Rent(Define.m_nBlockBytes);
		long		nCount		= 0;
		long		nPos		= 0;

		try
		{
			while (nPos < nEnd)
			{
				int nRead = RandomAccess.Read(kHandle, arrBytes.AsSpan(0, (int)Math.Min(Define.m_nBlockBytes, nEnd - nPos)), nPos);

				if (nRead <= 0)
				{
					break;
				}

				nCount	+= arrBytes.AsSpan(0, nRead).Count((byte)'\n');
				nPos	+= nRead;

				kThrottle.Check();
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(arrBytes);
		}

		return nCount;
	}

	private static string GetLatestLog(string sFolder)	// 최신 파일: 가장 늦은 날짜 폴더 안 *yyyyMMdd_HH.log 중 가장 늦은 시간 (같으면 수정 시각, 예: X_20261031_11 - 복사본.log 제외, 없음: "")
	{
		if (!Directory.Exists(sFolder))
		{
			return "";
		}

		EnumerationOptions	kOption		= new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
		long				nDateMax	= 0;
		string				sDateDir	= "";

		FileSystemEnumerable<(long nDate, string sPath)> kDateDir = new FileSystemEnumerable<(long nDate, string sPath)>(sFolder, (ref FileSystemEntry kEntry) => (Tab.TryGetDate(kEntry.FileName, out long nDate) ? nDate : 0, kEntry.ToFullPath()), kOption)
		{
			ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => kEntry.IsDirectory && Tab.TryGetDate(kEntry.FileName, out _),
		};

		foreach ((long nDate, string sPath) kDir in kDateDir)
		{
			if (kDir.nDate > nDateMax)
			{
				nDateMax = kDir.nDate;
				sDateDir = kDir.sPath;
			}
		}

		if (sDateDir == "")
		{
			return "";
		}

		(long nDateHour, long nTime, string sPath) kLatest = (-1, 0, "");	// 최신 파일 (파일명 날짜·시간, 수정 시각, 경로)

		FileSystemEnumerable<(long nDateHour, long nTime, string sPath)> kFile = new FileSystemEnumerable<(long nDateHour, long nTime, string sPath)>(sDateDir, (ref FileSystemEntry kEntry) => (Tab.TryGetLogDateHour(kEntry.FileName, out long nDateHour) ? nDateHour : 0, kEntry.LastWriteTimeUtc.UtcTicks, kEntry.ToFullPath()), kOption)
		{
			ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => !kEntry.IsDirectory && Tab.TryGetLogDateHour(kEntry.FileName, out _),
		};

		foreach ((long nDateHour, long nTime, string sPath) kItem in kFile)
		{
			if (kItem.nDateHour > kLatest.nDateHour || (kItem.nDateHour == kLatest.nDateHour && kItem.nTime > kLatest.nTime))
			{
				kLatest = kItem;
			}
		}

		return kLatest.sPath;
	}

	private static bool NeedNextCheck(LogWatch kWatch)	// 다음 시간 파일 찾기 여부: 감시 파일 시간 경과 후 m_nNextCheckMs 마다 1번 (그 전: 다음 파일 없음, 파일명 형식 아님: 매번)
	{
		if (!Tab.TryGetLogDateHour(Path.GetFileName(kWatch.m_sPath.AsSpan()), out long nCurrent))
		{
			return true;
		}

		DateTimeOffset	kNow		= GetNow();
		long			nNowHour	= ((kNow.Year * 100L + kNow.Month) * 100 + kNow.Day) * 100 + kNow.Hour;	// 지금 시간 (yyyyMMddHH)

		if (nNowHour <= nCurrent || Environment.TickCount64 - kWatch.m_nNextTick < m_nNextCheckMs)
		{
			return false;
		}

		kWatch.m_nNextTick = Environment.TickCount64;

		return true;
	}

	private static string GetNextLog(string sFolder, string sPath)	// 다음 시간 파일: sPath 시간보다 늦은 것 중 가장 이른 것 (날짜 폴더 바뀜 포함, 같은 시간: 경로 순, 없음: "")
	{
		if (!Tab.TryGetLogDateHour(Path.GetFileName(sPath.AsSpan()), out long nCurrent) || !Directory.Exists(sFolder))
		{
			return "";
		}

		EnumerationOptions				kOption	= new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
		(long nDateHour, string sPath)	kNext	= (long.MaxValue, "");	// 다음 파일 (파일명 날짜·시간, 경로)

		FileSystemEnumerable<string> kDateDir = new FileSystemEnumerable<string>(sFolder, (ref FileSystemEntry kEntry) => kEntry.ToFullPath(), kOption)	// sPath 날짜 이후 날짜 폴더
		{
			ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => kEntry.IsDirectory && Tab.TryGetDate(kEntry.FileName, out long nDate) && nDate >= nCurrent / 100,
		};

		foreach (string sDateDir in kDateDir)
		{
			FileSystemEnumerable<(long nDateHour, string sPath)> kFile = new FileSystemEnumerable<(long nDateHour, string sPath)>(sDateDir, (ref FileSystemEntry kEntry) => (Tab.TryGetLogDateHour(kEntry.FileName, out long nDateHour) ? nDateHour : 0, kEntry.ToFullPath()), kOption)
			{
				ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => !kEntry.IsDirectory && Tab.TryGetLogDateHour(kEntry.FileName, out long nDateHour) && nDateHour > nCurrent,
			};

			foreach ((long nDateHour, string sPath) kItem in kFile)
			{
				if (kItem.nDateHour < kNext.nDateHour || (kItem.nDateHour == kNext.nDateHour && string.Compare(kItem.sPath, kNext.sPath, StringComparison.OrdinalIgnoreCase) < 0))
				{
					kNext = kItem;
				}
			}
		}

		return kNext.sPath;
	}

	public static async Task SyncCount(LineWriter kWriter, CancellationToken kStop)	// 접속 때 1번: 로그 감시 대상 폴더 목록 → 서버 (서버: 목록 밖 Count 지움) → 답 (서버 받은 진행) X초 기다림 (없음: 예전 서버 → 등록 없이) → 로그 감시 스레드가 첫 검사 전 등록·보정
	{
		TaskCompletionSource<Dictionary<string, (string sRel, long nLine)>>	kReply		= new TaskCompletionSource<Dictionary<string, (string sRel, long nLine)>>(TaskCreationOptions.RunContinuationsAsynchronously);
		string[]															arrFolder	= m_arrLogType.Length == 0 ? Array.Empty<string>() : m_arrCountFolder;	// 관리 목록 (aLogType 없음: 빈 목록 → 서버 Count 모두 지움)
		long																nGen		= Interlocked.Increment(ref m_nCountGen);								// 이 접속 번호 (지난 접속 등록·답: 무효)

		m_bCountServer	= false;
		m_arrField		= Array.Empty<LogField>();	// 이 접속 줄 필드: Count 답 aField (답 없음·예전 서버: 없이)
		m_sServerField	= null;
		m_kCountReply	= kReply;

		try
		{
			await kWriter.WriteAsync(JsonSerializer.Serialize(new { sType = "Count", arrFolder, sTime = GetNowText() }, Define.m_kJsonOption), kStop);

			Dictionary<string, (string sRel, long nLine)> hmapCount = await kReply.Task.WaitAsync(TimeSpan.FromSeconds(m_nCountWaitSec), kStop);	// 서버 받은 진행

			m_bCountServer = true;

			if (m_arrLogType.Length > 0)	// 로그 감시 함: 로그 전송 시작 (m_kLogWriter) 전에 넣음 (안 함: 꺼낼 곳 없음 → 안 넣음)
			{
				m_queServerCount.Enqueue((nGen, arrFolder, hmapCount));
			}
		}
		catch (TimeoutException)	// 예전 서버·답 없음: 등록·보정 없이 모두 전송
		{
			Log.Write(WARN_, $"쿼리 보정 없음: 서버 Count 답 {m_nCountWaitSec}초 없음 (예전 서버)");
		}
		finally
		{
			m_kCountReply = null;
		}
	}

	public static string GetAliveLine()	// 얼라이브 한 줄 {"sType":"Alive","arrFolder":[""]} (지금 관리 목록, aLogType 없음: 빈 목록 → 서버: 다름만 맞춤·답 필드 정의)
	{
		string[] arrFolder = m_arrLogType.Length == 0 ? Array.Empty<string>() : m_arrCountFolder;	// 관리 목록 (Count 와 같음)

		return JsonSerializer.Serialize(new { sType = "Alive", arrFolder, sTime = GetNowText() }, Define.m_kJsonOption);
	}

	public static void ApplyServerField(JsonElement kRoot)	// 서버 aField (Count 답·얼라이브 답) → 보낼 줄 필드 (같은 값: 그대로 → 1분마다 로그 안 남김)
	{
		string sField = kRoot.TryGetProperty("aField", out JsonElement kFields) ? kFields.GetRawText() : "";	// 받은 aField 글자 (바뀜 판단)

		if (sField == m_sServerField)
		{
			return;
		}

		m_sServerField	= sField;
		m_arrField		= LoadField(kRoot);
	}

	public static void SetLogPause(bool bPause)	// 서버 밀림 Pause (분당 상위 클라만 옴) → 로그 감시 멈춤 (위치 유지) / Resume (모두에게 옴) → 멈춘 상태만 재개, 같은 상태: 무시
	{
		if (m_bLogPause == bPause)	// 이미 전송 중 Resume·이미 멈춤 Pause
		{
			return;
		}

		m_bLogPause = bPause;

		Log.Write(NOTI_, bPause ? "쿼리 멈춤: 서버 적재 밀림 (Pause, 분당 상위 클라)" : "쿼리 재개: 서버 적재 안정 (Resume)");
	}

	public static Dictionary<string, (string sRel, long nLine)> ReadServerCount(JsonElement kRoot)	// Count·CountAdd 답 arrCount [{sFolder, sRel, nLine}] → 대상 폴더 → 날짜 폴더\파일·줄 번호
	{
		Dictionary<string, (string sRel, long nLine)> hmapCount = new Dictionary<string, (string sRel, long nLine)>(StringComparer.OrdinalIgnoreCase);

		if (kRoot.TryGetProperty("arrCount", out JsonElement kCounts) && kCounts.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement kCount in kCounts.EnumerateArray())
			{
				hmapCount[GetText(kCount, "sFolder")] = (GetText(kCount, "sRel"), GetLong(kCount, "nLine"));
			}
		}

		return hmapCount;
	}

	public static LogField[] LoadField(JsonElement kRoot)	// 서버 Count 답 aField [{sKey, sRegex, sValue}] → 보낼 줄 필드 (키: 영문 시작·영문·숫자·_ X자 이하·기본 필드·중복 불가 (대소문자 무시), sValue 있음: 고정값 X자 이하 (정규식 안 씀), 없음: 정규식 X자 이하, 오류: 제외, aField 없음 (예전 서버): 빈 배열)
	{
		string[]		arrBase		= { "Machine", "Folder", "Type" };	// 서버 기본 필드 (쓸 수 없는 키)
		List<LogField>	listField	= new List<LogField>();

		if (!kRoot.TryGetProperty("aField", out JsonElement kFields) || kFields.ValueKind != JsonValueKind.Array)
		{
			Log.Write(INFO_, "줄 필드: 없음 (서버 Count 답 aField 없음)");

			return Array.Empty<LogField>();
		}

		foreach (JsonElement kSection in kFields.EnumerateArray().Where(kItem => kItem.ValueKind == JsonValueKind.Object))
		{
			string sKey		= GetFieldText(kSection, "sKey").Trim();
			string sRegex	= GetFieldText(kSection, "sRegex");
			string sValue	= GetFieldText(kSection, "sValue").Trim();	// 고정값 (있음: 정규식 안 씀, 모든 줄 이 값)

			if (sKey.Length > m_nFieldKeyMax || !Regex.IsMatch(sKey, "^[A-Za-z][A-Za-z0-9_]*$") || arrBase.Contains(sKey, StringComparer.OrdinalIgnoreCase) || listField.Any(kField => string.Equals(kField.sKey, sKey, StringComparison.OrdinalIgnoreCase)))
			{
				Log.Write(WARN_, $"서버 aField 오류: 키 형식·중복 ({sKey}, 영문 시작·영문·숫자·_ {m_nFieldKeyMax}자 이하·기본 필드 아님)");

				continue;
			}

			if (sValue != "")	// 고정값: 정규식 안 씀
			{
				if (sValue.Length > m_nFieldValueMax)
				{
					Log.Write(WARN_, $"서버 aField 오류: {sKey} 값 {m_nFieldValueMax}자 넘음");

					continue;
				}

				listField.Add(new LogField(sKey, null, sValue));

				continue;
			}

			if (sRegex == "" || sRegex.Length > m_nFieldRegexMax)
			{
				Log.Write(WARN_, $"서버 aField 오류: {sKey} 정규식 없음·{m_nFieldRegexMax}자 넘음");

				continue;
			}

			try
			{
				listField.Add(new LogField(sKey, new Regex(sRegex, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(m_nFieldRegexMs)), ""));
			}
			catch (ArgumentException kException)	// 정규식 형식 오류
			{
				Log.Write(WARN_, $"서버 aField 오류: {sKey} 정규식 오류 ({kException.Message})");
			}
		}

		Log.Write(INFO_, $"줄 필드: 서버 aField {listField.Count}개 ({string.Join(", ", listField.Select(kField => kField.sKey))})");

		return listField.ToArray();
	}

	private static string GetFieldText(JsonElement kSection, string sKey)	// aField 항목 문자열 값 (키 없음·문자열 아님: "")
	{
		return kSection.TryGetProperty(sKey, out JsonElement kValue) && kValue.ValueKind == JsonValueKind.String ? kValue.GetString() ?? "" : "";
	}
}

class LogWatch	// 대상 폴더 하나의 로그 감시: 감시 파일 읽기 전용 핸들 유지 (로그 감시 스레드 전용)
{
	public readonly string	m_sFolder;			// 대상 폴더 전체 경로 (끝 구분자 없음)
	public string?			m_sPath;			// 감시 파일 (null: 탐색 후 확인 전, "": 최신 파일 없음)
	public SafeFileHandle?	m_kHandle;			// 감시 파일 핸들 (읽기 전용, null: 안 열림)
	public long				m_nOffset;			// 다음 읽기 바이트 (-1: 열 때 m_nLine 기준으로 정함)
	public long				m_nLine;			// 마지막 본 줄 번호 (0: 없음, -1: 열 때 끝난 줄 끝으로)
	public long				m_nNextTick;		// 마지막 다음 시간 파일 찾기 시각 (TickCount64)
	public string			m_sError	= "";	// 마지막 오류 (같은 오류 반복 출력 방지)
	public readonly Count	m_kCount;			// 로그 감시 상태 (Data\Count\머신\대상 폴더\Count.data)
	public long				m_nServerGen	= -1;	// 서버 등록된 접속 번호 (지금 접속과 같음: 전송, 다름: 등록 전 → 위치 유지)
	public long				m_nRequestGen	= -1;	// 서버 등록 요청 (CountAdd) 보낸 접속 번호 (같은 접속 중복 요청 방지)
	public bool				m_bNoCount;				// 마지막 서버 답: 받은 진행 없음
	public string			m_sNoCountRel	= "";	// 진행 없음 뒤 처음 보낸 위치 "날짜 폴더\파일" ("": 그 뒤 안 보냄, 다음 답도 진행 없음 → 여기부터 다시)
	public long				m_nNoCountLine;			// 그 파일 본 줄 (다음 줄부터 보냄)

	public LogWatch(string sFolder)
	{
		m_sFolder	= sFolder;
		m_kCount	= new Count(GetMachine(), sFolder);	// 서버 Hello sMachine 과 같은 이름
	}

	public void Close()	// 핸들 닫기 (위치 유지)
	{
		m_kHandle?.Dispose();

		m_kHandle = null;
	}
}

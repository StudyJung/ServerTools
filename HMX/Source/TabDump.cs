static class TabDump	// 덤프 탭: *.dmp 감시 (파일 이벤트 → 서버)·목록·조각 읽기·삭제
{
	private const int	m_nPingSec			= 60;		// 감시 폴더 재확인 주기 (초)
	private const int	m_nFlushMs			= 1000;		// Changed 이벤트 묶음 전송 주기 (ms)
	private const int	m_nWatchCheckMs		= 5000;		// 덤프 감시 상태 확인 주기 (ms)
	private const string	m_sWatchFilter	= "*.dmp";	// 감시 대상 파일 (덤프만, 대소문자 무시)
	private const int	m_nDumpMax			= 10000;			// 덤프 목록 최대 건수 (초과: bTruncated)

	private static readonly ConcurrentDictionary<string, byte>	m_hmapChanged = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);	// Changed 이벤트 경로 (주기마다 경로당 1건 전송, 로그 연속 쓰기 폭주 방지)
	private static volatile bool								m_bWatchError;	// 감시 오류 발생 (WatchLoop 가 감시 다시 생성)
	public static readonly TaskCompletionSource				m_kDumpReady	= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);	// 덤프 준비 끝 (준비 순서: 덤프 → 파일)
	public static string[]							m_arrBlockDump = Array.Empty<string>();		// aBlockDump 막는 경로 (전체 경로·\이름\, 자신·하위: 덤프 감시·목록·다운·삭제 제외, 다른 탭: 그대로)

	public static void Init(string[] arrBlockDump)	// 설정 → 막는 경로 (aBlockDump, Main.Create)
	{
		m_arrBlockDump = Tab.GetBlockRules(arrBlockDump, "aBlockDump");
	}

	public static async Task WatchLoop(CancellationToken kStop)	// 덤프 감시: aLogFolder 하위 *.dmp (폴더 생김·삭제·감시 오류: 다시 생성)
	{
		while (!kStop.IsCancellationRequested)
		{
			List<FileSystemWatcher>	listWatcher	= new List<FileSystemWatcher>();
			string					sExist		= GetRootExist();
			bool					bRetry		= false;							// 감시 열기 실패 (대기 후 재시도)

			m_bWatchError = false;

			try
			{
				foreach (string sRoot in Tab.m_arrLogRoot)
				{
					if (!Directory.Exists(sRoot))	// 없음: 생기면 다시 생성
					{
						Log.Write(WARN_, $"덤프 폴더 없음: {sRoot} (생기면 감시)");

						continue;
					}

					FileSystemWatcher kWatcher = new FileSystemWatcher(sRoot, m_sWatchFilter)	// 이름 변경: 이전·새 이름 중 하나만 맞아도 알림
					{
						IncludeSubdirectories	= true,
						NotifyFilter			= NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,	// 파일만 (폴더 이벤트 제외)
						InternalBufferSize		= 64 * 1024,	// 최대 버퍼 (초과 시 이벤트 유실 → Error 이벤트 전송)
					};

					listWatcher.Add(kWatcher);

					kWatcher.Created	+= OnCreated;
					kWatcher.Changed	+= OnChanged;
					kWatcher.Deleted	+= OnDeleted;
					kWatcher.Renamed	+= OnRenamed;
					kWatcher.Error		+= OnError;

					kWatcher.EnableRaisingEvents = true;

					Log.Write(INFO_, $"덤프 시작: {sRoot} (하위 {Define.m_nLogScanDepth}단계, {m_sWatchFilter})");
				}

				m_kDumpReady.TrySetResult();

				while (!kStop.IsCancellationRequested && !m_bWatchError && GetRootExist() == sExist)
				{
					await Delay(m_nWatchCheckMs, kStop);
				}
			}
			catch (Exception kException)	// 감시 열기 실패 (폴더 삭제·권한·네트워크): 대기 후 재생성
			{
				Log.Write(WARN_, $"덤프 오류: {kException.Message} ({m_nPingSec}초 후 재시도)");

				m_kDumpReady.TrySetResult();

				bRetry = true;
			}
			finally
			{
				foreach (FileSystemWatcher kWatcher in listWatcher)
				{
					kWatcher.Dispose();
				}
			}

			if (bRetry)
			{
				await Delay(m_nPingSec * 1000, kStop);
			}
		}
	}

	private static string GetRootExist()	// aLogFolder 있음 상태 (순서대로 1·0, 덤프 감시 다시 생성 판단)
	{
		return string.Concat(Tab.m_arrLogRoot.Select(sRoot => Directory.Exists(sRoot) ? '1' : '0'));
	}

	private static void OnCreated(object kSender, FileSystemEventArgs kArgs)	// 생성: 즉시 전송
	{
		if (IsDumpWatch(kArgs.FullPath))
		{
			Send("Created", kArgs.FullPath);
		}
	}

	private static void OnChanged(object kSender, FileSystemEventArgs kArgs)	// 변경: 경로만 보관 → FlushLoop 묶음 전송
	{
		if (IsDumpWatch(kArgs.FullPath))
		{
			m_hmapChanged[kArgs.FullPath] = 0;
		}
	}

	private static void OnDeleted(object kSender, FileSystemEventArgs kArgs)	// 삭제: 즉시 전송
	{
		m_hmapChanged.TryRemove(kArgs.FullPath, out _);

		if (IsDumpWatch(kArgs.FullPath))
		{
			Send("Deleted", kArgs.FullPath);
		}
	}

	private static void OnRenamed(object kSender, RenamedEventArgs kArgs)	// 이름 변경: 즉시 전송 (이전·새 경로 중 하나라도 깊이 안)
	{
		m_hmapChanged.TryRemove(kArgs.OldFullPath, out _);

		if (IsDumpWatch(kArgs.FullPath) || IsDumpWatch(kArgs.OldFullPath))
		{
			Send("Renamed", kArgs.FullPath, kArgs.OldFullPath);
		}
	}

	private static void OnError(object kSender, ErrorEventArgs kArgs)	// 감시 오류 (버퍼 초과 등): 즉시 전송, 감시 다시 생성
	{
		m_bWatchError = true;

		Log.Write(WARN_, $"덤프 오류: {kArgs.GetException().Message} (다시 생성)");

		Send("Error", (kSender as FileSystemWatcher)?.Path ?? "", "", kArgs.GetException().Message);
	}

	private static bool IsDumpWatch(string sPath)	// 덤프 감시 대상: aLogFolder 하위 m_nLogScanDepth 단계 안 + aBlockDump 밖 (덤프 목록과 같은 기준)
	{
		if (Tab.IsBlocked(sPath, m_arrBlockDump))
		{
			return false;
		}

		foreach (string sRoot in Tab.m_arrLogRoot)
		{
			string sPrefix = Tab.GetRootPrefix(sRoot);

			if (sPath.Length > sPrefix.Length && sPath.StartsWith(sPrefix, StringComparison.OrdinalIgnoreCase) && sPath.AsSpan(sPrefix.Length).Count(Path.DirectorySeparatorChar) <= Define.m_nLogScanDepth)
			{
				return true;
			}
		}

		return false;
	}

	public static async Task FlushLoop(CancellationToken kStop)	// Changed 경로 묶음 전송 (주기마다 경로당 1건)
	{
		while (!kStop.IsCancellationRequested)
		{
			await Delay(m_nFlushMs, kStop);

			foreach (string sPath in m_hmapChanged.Keys)
			{
				if (m_hmapChanged.TryRemove(sPath, out _))
				{
					Send("Changed", sPath);
				}
			}
		}
	}

	private static void Send(string sEvent, string sPath, string sOldPath = "", string sMessage = "")	// 파일 이벤트 → 전송 대기 (끊긴 동안에도 보관)
	{
		long nSize = -1;	// 파일 크기 (폴더·삭제·조회 실패: -1)

		try
		{
			FileInfo kInfo = new FileInfo(sPath);

			if (kInfo.Exists)
			{
				nSize = kInfo.Length;
			}
		}
		catch (Exception)	// 경로 오류·권한 없음: 크기 -1
		{
		}

		string sLine = JsonSerializer.Serialize(new
		{
			sType		= "File",
			sEvent		= sEvent,
			sPath		= sPath,
			sOldPath	= sOldPath,
			nSize		= nSize,
			sMessage	= sMessage,
			sTime		= GetNowText(),
		});

		Main.Post(sLine);
	}

	private static bool IsDumpFile(ReadOnlySpan<char> sName)	// 덤프 확장자 (대소문자 무시)
	{
		return sName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);
	}

	public static object DumpList(long nReqId)	// 덤프 목록: aLogFolder 하위 *.dmp (경로 순, 겹친 경로 1번, 최대 m_nDumpMax 건, 없는 aLogFolder: 건너뜀)
	{
		List<DumpFile>	listFile	= new List<DumpFile>();
		bool			bTruncated	= false;				// 최대 건수 초과
		string			sError		= "";
		long			nBegin		= Stopwatch.GetTimestamp();

		try
		{
			Throttle			kThrottle	= new Throttle();
			EnumerationOptions	kOption		= new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = Define.m_nLogScanDepth, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
			HashSet<string>		hsetPath	= new HashSet<string>(StringComparer.OrdinalIgnoreCase);	// 담은 경로 (aLogFolder 겹침: 1번만)

			foreach (string sRoot in Tab.m_arrLogRoot)
			{
				if (bTruncated || !Directory.Exists(sRoot) || Tab.IsBlocked(sRoot, m_arrBlockDump))
				{
					continue;
				}

				FileSystemEnumerable<DumpFile> kFile = new FileSystemEnumerable<DumpFile>(sRoot, (ref FileSystemEntry kEntry) => new DumpFile(kEntry.ToFullPath(), kEntry.Length, kEntry.LastWriteTimeUtc.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), kOption)
				{
					ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => !kEntry.IsDirectory && IsDumpFile(kEntry.FileName),
					ShouldRecursePredicate	= (ref FileSystemEntry kEntry) =>	// 폴더마다 부하 조절, aBlockDump: 안 들어감
					{
						kThrottle.Check();

						return m_arrBlockDump.Length == 0 || !Tab.IsBlocked(kEntry.ToFullPath(), m_arrBlockDump);
					},
				};

				foreach (DumpFile kDump in kFile)
				{
					if (listFile.Count >= m_nDumpMax)	// 최대 건수 초과: 중단
					{
						bTruncated = true;

						break;
					}

					if (hsetPath.Add(kDump.sPath))
					{
						listFile.Add(kDump);
					}
				}
			}

			listFile.Sort((kLeft, kRight) => string.Compare(kLeft.sPath, kRight.sPath, StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception kException)	// 폴더 없음·탐색 오류
		{
			sError = GetErrorText(kException, "덤프 목록");
		}

		Log.Write(INFO_, $"덤프 목록: {listFile.Count}건 ({(long)Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds}ms){(bTruncated ? " (일부)" : "")}{(sError == "" ? "" : $" (오류: {sError})")}");

		return new { sType = "DumpResult", nReqId, bTruncated, sError, arrFile = listFile };
	}

	public static object DumpRead(long nReqId, string sPath, long nOffset, int nCount)	// 덤프 다운 조각
	{
		return Tab.ReadChunk("DumpReadResult", nReqId, sPath, GetDumpPath(sPath), nOffset, nCount);
	}

	public static object DumpDelete(long nReqId, string sPath)	// 덤프 삭제 (허용 경로만)
	{
		string	sError	= "";
		string?	sFull	= GetDumpPath(sPath);

		if (sFull == null)
		{
			sError = "허용되지 않은 경로";
		}
		else
		{
			try
			{
				File.Delete(sFull);
			}
			catch (Exception kException)	// 사용 중·권한 없음
			{
				sError = GetErrorText(kException, $"덤프 삭제 {sPath}");
			}
		}

		Log.Write(sError == "" ? NOTI_ : WARN_, $"덤프 삭제: {sPath}{(sError == "" ? "" : $" (오류: {sError})")}");

		return new { sType = "DumpDeleteResult", nReqId, sPath, sError };
	}

	private static string? GetDumpPath(string sPath)	// 덤프 허용 경로: aLogFolder 하위 *.dmp + 존재 + aBlockDump 밖 (그 외: null)
	{
		string? sFull = Tab.GetLogFolderPath(sPath);

		return sFull != null && IsDumpFile(sFull) && !Tab.IsBlocked(sFull, m_arrBlockDump) ? sFull : null;
	}
}

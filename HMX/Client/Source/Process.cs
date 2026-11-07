using StackExchange.Redis;	// Redis 명령 (이 파일·Redis·TabProcess 만, 전역 using 안 함 → 이름 겹침 방지)

static class Process	// 실행 API: 폴더\깃헙\실행파일 실행 여부 확인·실행·종료 (Win32 API, OLD HMXProcess 이식, 주기 처리: TabProcess)
{
	private const uint	PROCESS_QUERY_LIMITED_INFORMATION	= 0x00001000;	// 실행 파일 경로 조회 권한
	private const uint	DETACHED_PROCESS					= 0x00000008;	// 콘솔 없이 독립 실행 (클라 종료와 무관)
	private const uint	CREATE_NEW_CONSOLE					= 0x00000010;	// 새 콘솔 (Ctrl+C 전달용, 서비스는 세션 0 이라 창은 안 보임)
	private const uint	NORMAL_PRIORITY_CLASS				= 0x00000020;	// 보통 우선순위 (클라 BelowNormal 상속 방지)
	private const uint	CREATE_NEW_PROCESS_GROUP			= 0x00000200;	// PID = 프로세스 그룹 ID (GenerateConsoleCtrlEvent 대상)
	private const uint	CREATE_BREAKAWAY_FROM_JOB			= 0x01000000;	// 클라가 잡에 묶인 환경에서도 종속되지 않게
	private const uint	CTRL_C_EVENT						= 0;
	private const uint	ATTACH_PARENT_PROCESS				= 0xFFFFFFFF;

	private const int	m_nKillWaitMs	= 5000;		// 강제 종료 뒤 완전 종료 대기 (ms)
	private const int	m_nCtrlCWaitMs	= 10000;	// Ctrl+C 뒤 graceful 종료 대기 (ms, 초과: ERROR)

	private static readonly object	m_lockConsole	= new object();	// AttachConsole·FreeConsole 은 프로세스 전역 상태라 Ctrl+C 전송 직렬화

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct STARTUPINFO
	{
		public int		cb;
		public string?	lpReserved;
		public string?	lpDesktop;
		public string?	lpTitle;
		public int		dwX;
		public int		dwY;
		public int		dwXSize;
		public int		dwYSize;
		public int		dwXCountChars;
		public int		dwYCountChars;
		public int		dwFillAttribute;
		public int		dwFlags;
		public short	wShowWindow;
		public short	cbReserved2;
		public IntPtr	lpReserved2;
		public IntPtr	hStdInput;
		public IntPtr	hStdOutput;
		public IntPtr	hStdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PROCESS_INFORMATION
	{
		public IntPtr	hProcess;
		public IntPtr	hThread;
		public int		dwProcessId;
		public int		dwThreadId;
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr hObject);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool FreeConsole();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AttachConsole(uint dwProcessId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetConsoleWindow();

	public static int Check(string[] arrValue, int nPID)	// 폴더\깃헙\실행파일 이 실행 중이면 PID (미실행: 0, 값 오류·조회 실패: -1), nPID 있음: 그 PID 먼저 확인 (전체 검색 생략)
	{
		string	sFullPath	= GetFullPath(arrValue);

		if (sFullPath == "")	// 파라미터 검사 및 조기 반환
		{
			return -1;
		}

		if (nPID > 0 && string.Equals(GetImagePath(nPID), sFullPath, StringComparison.OrdinalIgnoreCase))	// 저장된 PID 가 같은 실행 파일: 그대로
		{
			return nPID;
		}

		System.Diagnostics.Process[]	arrProcess;	// 실행파일명이 같은 프로세스

		try
		{
			arrProcess = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GetExeName(arrValue[ProcessValue.EXECUTE])));
		}
		catch (Exception kException)	// 프로세스 목록 조회 실패
		{
			ProcessLog.Write(FATAL, $"프로세스 목록 조회 실패: {kException.Message}");

			return -1;
		}

		int	nFound	= 0;	// 전체 경로가 같은 첫 프로세스

		foreach (System.Diagnostics.Process kProcess in arrProcess)
		{
			if (nFound == 0 && string.Equals(GetImagePath(kProcess.Id), sFullPath, StringComparison.OrdinalIgnoreCase))
			{
				nFound = kProcess.Id;
			}

			kProcess.Dispose();
		}

		return nFound;
	}

	public static int Init(string sField, string[] arrValue)	// 폴더\깃헙 에서 실행파일+인자 실행 → PID (실패: -1), Mode 0: 독립 실행 (클라 종료와 무관하게 유지), 그 외: 새 콘솔 + 프로세스 그룹 (Ctrl+C 종료용)
	{
		string	sMode		= arrValue[ProcessValue.MODE];
		string	sExecute	= arrValue[ProcessValue.EXECUTE];

		if (GetFullPath(arrValue) == "")	// 파라미터 검사 및 조기 반환
		{
			return -1;
		}

		string	sWorkDir	= arrValue[ProcessValue.FOLDER] + "\\" + arrValue[ProcessValue.GITHUB];	// 실제 실행 위치 (sparse-checkout 폴더)
		string	sExeName	= GetExeName(sExecute);
		bool	bDetached	= sMode == "0";

		StringBuilder kCmdLine = new StringBuilder($"\"{sWorkDir}\\{sExeName}\"{sExecute.Substring(sExeName.Length)}");	// "폴더\깃헙\실행파일" + 인자 (경로 공백 대비 따옴표)

		STARTUPINFO			kStartup	= new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
		PROCESS_INFORMATION	kProcess	= default;
		uint				uFlags		= NORMAL_PRIORITY_CLASS | (bDetached ? DETACHED_PROCESS : (CREATE_NEW_CONSOLE | CREATE_NEW_PROCESS_GROUP));

		bool	bCreated	= bDetached && CreateProcessW(null, kCmdLine, IntPtr.Zero, IntPtr.Zero, false, uFlags | CREATE_BREAKAWAY_FROM_JOB, IntPtr.Zero, sWorkDir, ref kStartup, out kProcess);	// 독립 실행은 잡에서도 분리 시도

		if (!bCreated)	// 잡 분리 미허용 환경·Graceful: 플래그 그대로
		{
			bCreated = CreateProcessW(null, kCmdLine, IntPtr.Zero, IntPtr.Zero, false, uFlags, IntPtr.Zero, sWorkDir, ref kStartup, out kProcess);
		}

		if (!bCreated)	// 실행 실패 검사 및 조기 반환
		{
			ProcessLog.Write(FATAL, $"Process Init CreateProcess 실패: {sField}({kCmdLine})({Marshal.GetLastWin32Error()})");

			return -1;
		}

		CloseHandle(kProcess.hThread);	// 대기 없이 실행 상태로 두고 핸들만 정리
		CloseHandle(kProcess.hProcess);

		if (Check(arrValue, kProcess.dwProcessId) <= 0)
		{
			ProcessLog.Write(FATAL, $"Process Init CheckProcess 실패: {sField}({kCmdLine})({sExecute})({kProcess.dwProcessId})");

			return -1;
		}

		ProcessLog.Write(INFO_, $"Process Init: {sField}({sWorkDir})({sMode})({sExecute})({kProcess.dwProcessId})");

		return kProcess.dwProcessId;
	}

	public static int Fini(string sField, string[] arrValue)	// 폴더\깃헙\실행파일 종료 → 종료한 PID (이미 종료: 0, 실패: -1), Mode 0: 강제 종료 (X초 대기), 그 외: Ctrl+C graceful (X초 대기, 초과: ERROR)
	{
		string	sMode		= arrValue[ProcessValue.MODE];
		string	sExecute	= arrValue[ProcessValue.EXECUTE];
		string	sWorkDir	= arrValue[ProcessValue.FOLDER] + "\\" + arrValue[ProcessValue.GITHUB];	// 실제 실행 위치 (로그 표시)

		int	nPID	= Check(arrValue, ProcessValue.ToInt(arrValue[ProcessValue.PID]));	// 실행 중인 PID (저장된 PID 우선 확인)

		if (nPID <= 0)	// 확인 오류(-1)·이미 종료(0)
		{
			return nPID;
		}

		if (sMode == "0")	// Detached: 강제 종료
		{
			try
			{
				using System.Diagnostics.Process kProcess = System.Diagnostics.Process.GetProcessById(nPID);

				kProcess.Kill();
				kProcess.WaitForExit(m_nKillWaitMs);	// 완전 종료까지 대기
			}
			catch (Exception kException)	// 권한 없음 등
			{
				ProcessLog.Write(FATAL, $"Process Fini 종료 실패: {sField}({sWorkDir})({sExecute})({nPID}) {kException.Message}");

				return -1;
			}
		}
		else	// Graceful: Ctrl+C → 모듈 정리 후 종료
		{
			System.Diagnostics.Process?	kProcess	= null;	// 종료 완료 대기용

			try
			{
				kProcess = System.Diagnostics.Process.GetProcessById(nPID);
			}
			catch (Exception)	// 그 사이 종료됨
			{
			}

			if (!SendCtrlC(nPID, out string sError))
			{
				ProcessLog.Write(FATAL, $"Process Fini SendCtrlC 실패: {sField}({sWorkDir})({sExecute})({nPID}) {sError}");

				kProcess?.Dispose();

				return -1;
			}

			if (kProcess != null)	// graceful 종료 대기, 시간 초과: 최후수단 강제 종료 필요
			{
				if (!kProcess.WaitForExit(m_nCtrlCWaitMs))
				{
					ProcessLog.Write(ERROR, $"Process Fini graceful 실패 → 강제 종료 필요: {sField}({sWorkDir})({sExecute})({nPID})");
				}

				kProcess.Dispose();
			}
		}

		ProcessLog.Write(INFO_, $"Process Fini: {sField}({sWorkDir})({sMode})({sExecute})({nPID})");

		return nPID;
	}

	private static bool SendCtrlC(int nPID, out string sError)	// 대상 PID 의 콘솔에 붙어 그 프로세스 그룹에만 Ctrl+C 전송 (그룹 0 은 클라 자신도 받아 함께 종료되므로 PID 사용, 실패: sError 에 API·에러코드)
	{
		bool	bSent	= false;

		sError = "";

		lock (m_lockConsole)
		{
			bool	bHadConsole	= GetConsoleWindow() != IntPtr.Zero;	// 콘솔 모드 (작업 후 원래 콘솔로 복귀)

			FreeConsole();	// 다른 콘솔에 붙으려면 먼저 기존 콘솔 분리 (서비스는 콘솔이 없어 무해)

			if (!AttachConsole((uint)nPID))	// 대상이 콘솔 없이 실행됐으면 ERROR_INVALID_HANDLE(6): Ctrl+C 전달 경로 없음
			{
				sError = $"AttachConsole ERR({Marshal.GetLastWin32Error()})";
			}
			else
			{
				if (GenerateConsoleCtrlEvent(CTRL_C_EVENT, (uint)nPID))
				{
					bSent = true;
				}
				else
				{
					sError = $"GenerateConsoleCtrlEvent ERR({Marshal.GetLastWin32Error()})";
				}

				FreeConsole();	// 대상 콘솔 분리 (이벤트는 이미 전달됨)
			}

			if (bHadConsole && AttachConsole(ATTACH_PARENT_PROCESS))	// 원래(부모) 콘솔 복귀 → 콘솔 출력 다시 연결
			{
				Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
			}
		}

		return bSent;
	}

	private static string GetFullPath(string[] arrValue)	// 실행 파일 전체 경로 = 폴더\깃헙\실행파일 (폴더·깃헙·실행파일 없음·경로 오류: "")
	{
		string	sFolder		= arrValue[ProcessValue.FOLDER];
		string	sGitHub		= arrValue[ProcessValue.GITHUB];
		string	sExeName	= GetExeName(arrValue[ProcessValue.EXECUTE]);

		if (sFolder == "" || sGitHub == "" || sExeName == "")
		{
			return "";
		}

		try
		{
			return Path.GetFullPath(sFolder + "\\" + sGitHub + "\\" + sExeName);	// 중복·/ 구분자 정리 (실행 중 경로와 비교)
		}
		catch (Exception)	// 경로 형식 오류
		{
			return "";
		}
	}

	private static string GetExeName(string sExecute)	// 실행파일명 (첫 공백 전까지)
	{
		int	nSpace	= sExecute.IndexOf(' ');

		return nSpace < 0 ? sExecute : sExecute.Substring(0, nSpace);
	}

	private static string GetImagePath(int nPID)	// 프로세스 실행 파일 전체 경로 (접근 불가·종료: "")
	{
		IntPtr	hProcess	= OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, nPID);

		if (hProcess == IntPtr.Zero)
		{
			return "";
		}

		try
		{
			StringBuilder	kPath	= new StringBuilder(1024);
			int				nSize	= kPath.Capacity;

			return QueryFullProcessImageNameW(hProcess, 0, kPath, ref nSize) ? kPath.ToString() : "";
		}
		finally
		{
			CloseHandle(hProcess);
		}
	}
}

static class ProcessValue	// 값 파트 (콤마 구분, 서버 반영 탭 등록 형식: 상태,적용,PID,커밋,머신,폴더,깃헙,Sync,Mode,실행파일+인자, OLD HMXValue 이식)
{
	public const int	ONOFF	= 0;	// 상태 (On | Off | Down)
	public const int	CHECK	= 1;	// 적용 (True: 상태 반영 완료, False: 처리 대기)
	public const int	PID		= 2;	// 실행 PID (0: 미실행, 음수: 오류값)
	public const int	COMMIT	= 3;	// Git 커밋 (X자리)
	public const int	MACHINE	= 4;	// 머신 이름 (이 머신과 같은 항목만 처리)
	public const int	FOLDER	= 5;	// 폴더 (Git 클론 위치)
	public const int	GITHUB	= 6;	// 깃헙 (sparse-checkout 경로, 실행 위치 = 폴더\깃헙)
	public const int	SYNC	= 7;	// 1: 시작 시 Git 갱신 (기본), 0: 생략 (Down 의 명시적 다운로드는 값과 무관하게 수행)
	public const int	MODE	= 8;	// 0: Detached (독립 실행 + 강제 종료), 그 외: Graceful (새 콘솔 + Ctrl+C 종료)
	public const int	EXECUTE	= 9;	// 실행파일 + 인자 (첫 공백 전까지 실행파일)
	public const int	COUNT	= 10;	// 파트 수

	public static string[] Parse(string sValue)	// 값 → 파트 배열 (COUNT 개 고정, 없는 파트: "", 마지막 파트 (실행파일+인자): 남은 콤마 그대로 → 서버 화면 실행 명령과 같음)
	{
		string[]	arrPart		= sValue.Split(',', COUNT);
		string[]	arrValue	= new string[COUNT];

		for (int nIndex = 0; nIndex < COUNT; ++nIndex)
		{
			arrValue[nIndex] = nIndex < arrPart.Length ? arrPart[nIndex] : "";
		}

		return arrValue;
	}

	public static string Join(string[] arrValue)	// 파트 배열 → 값
	{
		return string.Join(",", arrValue);
	}

	public static int ToInt(string sValue)	// 정수 파트 (PID 등, 숫자 아님: 0)
	{
		return int.TryParse(sValue, out int nValue) ? nValue : 0;
	}

	public static string GetGroup(string sField)	// 그룹 키: 필드의 첫번째+두번째 파트 (예: "999, 0_HMX, 0_Common" → "999, 0_HMX", 파트 부족: 필드 그대로)
	{
		int	nFirst	= sField.IndexOf(',');

		if (nFirst < 0)
		{
			return sField;
		}

		int	nSecond	= sField.IndexOf(',', nFirst + 1);

		return nSecond < 0 ? sField : sField.Substring(0, nSecond);
	}
}

static class ProcessLog	// 실행 로그: 공통 Log (레벨 앞 sTitle 태그), ERROR·FATAL 은 Redis 에러 리스트에도 추가 ([머신] 붙임, 서버 반영 탭 조회 형식: [머신] [시각] [HMX_][레벨] 내용, OLD HMXLog 이식)
{
	private const int	m_nErrorExpireDay	= 30;		// 에러 리스트 유지 (일, 마지막 에러 기준)
	private const int	m_nFatalWaitMs		= 10000;	// FATAL 기록 뒤 대기 (ms, 반복 실패 완화)

	public static void Write(string sLevel, string sMessage)	// 로그 한 줄 (FATAL: 기록 후 X초 대기)
	{
		Log.Write(sLevel, sMessage);

		if (sLevel != ERROR && sLevel != FATAL)
		{
			return;
		}

		PushError($"[{GetNow().ToString("yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {GetTag()}[{sLevel}] {sMessage.Replace('\r', ' ').Replace('\n', ' ')}");	// 로그 줄과 같은 형식 (레벨 앞 sTitle 태그)

		if (sLevel == FATAL)
		{
			Thread.Sleep(m_nFatalWaitMs);
		}
	}

	private static void PushError(string sLine)	// Redis 에러 리스트 앞에 추가 (최신 index 0) + 마지막 에러부터 X일 유지 (실패 무시: 로그 재귀 방지)
	{
		if (!Redis.IsSet())	// Redis 미설정: 파일 로그만
		{
			return;
		}

		try
		{
			IDatabase	kDb		= Redis.GetDb();
			string		sKey	= Redis.GetErrorKey();

			kDb.ListLeftPush(sKey, $"[{GetMachine()}] {sLine}");
			kDb.KeyExpire(sKey, TimeSpan.FromDays(m_nErrorExpireDay));
		}
		catch (Exception)	// 연결 실패·타임아웃
		{
		}
	}
}

static class ProcessGit	// Git 배포 저장소 동기화: 폴더 = 클론 위치, 깃헙 = sparse-checkout 경로 (최신 커밋 1개만 shallow 유지, OLD HMXGit 이식)
{
	private const int		m_nCloneTimeoutMs	= 100000000;										// Git 클론 최대 대기 (ms, 초과: 강제 종료)
	private const int		m_nCommandTimeoutMs	= 1000000;											// 명령 최대 대기 (ms, 초과: 강제 종료)
	private const string	m_sDefaultUrl		= "https://github.nhnent.com/qpoker/HMX_ALPHA.git";	// 배포 저장소 기본값 (Git:sUrl 없음)
	private const string	m_sGitClone			= "git clone --filter=blob:none --sparse --depth=1 {0} . && git sparse-checkout init --cone && git sparse-checkout set {1}";	// 클론 ({0}: 저장소 주소, {1}: 깃헙 파트 = sparse-checkout 경로)
	private const string	m_sGitUpdate		= "git fetch --depth=1 --no-tags origin main && git reset --hard origin/main";	// 히스토리 없이 최신 커밋 1개만 받아 shallow 유지 (fetch 가 히스토리 전체를 deepen 해 느려지는 것 방지)
	private const string	m_sGitHash			= "git rev-parse --short=6 HEAD";					// 현재 커밋 해시
	private const int		m_nCommitLength		= 6;												// 커밋 해시 표시 길이

	public static async Task<bool> Update(string[] arrValue, CancellationToken kStop)	// On·False (실행 전 동기화, Sync 0: 생략) 또는 Down·False (다운로드만) 일 때만 폴더 동기화 → 커밋 기록 (할 일 없음: true, 실패: false)
	{
		string	sFolder	= arrValue[ProcessValue.FOLDER];
		bool	bOn		= arrValue[ProcessValue.ONOFF] == "On";
		bool	bDown	= arrValue[ProcessValue.ONOFF] == "Down";

		if ((!bOn && !bDown) || arrValue[ProcessValue.CHECK] != "False")	// 동기화 대상 아님 (폴더 검사보다 먼저: 폴더 빈 Off 항목 매 주기 오류 저장 방지)
		{
			return true;
		}

		if (sFolder == "")	// 파라미터 검사 및 조기 반환
		{
			return false;
		}

		if (bOn && arrValue[ProcessValue.SYNC] == "0")	// Sync 0: 시작 시점 동기화 생략 (Down 의 명시적 다운로드는 그대로)
		{
			ProcessLog.Write(INFO_, $"Git Update 생략(Sync=0): {sFolder}");

			return true;
		}

		ProcessLog.Write(INFO_, $"Git Update 시작: {sFolder}");

		try
		{
			Directory.CreateDirectory(sFolder);	// 폴더 없으면 생성 (중간 경로 포함)
		}
		catch (Exception kException)
		{
			ProcessLog.Write(FATAL, $"폴더 생성 실패: {sFolder} ({kException.Message})");

			return false;
		}

		int		nTimeoutMs	= m_nCommandTimeoutMs;
		string	sGitDir		= Path.Combine(sFolder, ".git");

		if (!Directory.Exists(sGitDir))	// 클론 (이미 클론된 폴더면 생략, 실패해도 아래 갱신에서 확인)
		{
			ProcessLog.Write(INFO_, $"Git Clone Init: {sFolder}");

			nTimeoutMs = m_nCloneTimeoutMs;

			await Run(sFolder, string.Format(m_sGitClone, GetRepoUrl(), arrValue[ProcessValue.GITHUB]), nTimeoutMs, kStop);

			ProcessLog.Write(INFO_, $"Git Clone Fini: {sFolder}");
		}

		string	sIndexLock	= Path.Combine(sGitDir, "index.lock");	// 이전 git 이 강제 종료(시간 초과·정지)되며 남긴 lock → "Another git process..."(128) 방지

		if (File.Exists(sIndexLock))
		{
			DeleteFile(sIndexLock);

			ProcessLog.Write(WARN_, $"stale lock 제거: {sIndexLock}");
		}

		DeleteFile(Path.Combine(sGitDir, "shallow.lock"));	// clone 중단 시 남는 lock (없으면 무시)

		(int nExit, string sOut, string sErr) = await Run(sFolder, m_sGitUpdate, nTimeoutMs, kStop);

		if (nExit != 0)
		{
			ProcessLog.Write(kStop.IsCancellationRequested ? NOTI_ : FATAL, $"Git Update 실패: {sFolder} ({nExit}) {GetLastLine(sErr + "\n" + sOut)}");	// 종료 요청 중단: NOTI (FATAL 대기 없음)

			return false;
		}

		(nExit, sOut, _) = await Run(sFolder, m_sGitHash, nTimeoutMs, kStop);

		string	sCommit	= sOut.Trim();	// 현재 커밋 해시

		if (nExit == 0 && sCommit != "")
		{
			arrValue[ProcessValue.COMMIT] = sCommit.Length > m_nCommitLength ? sCommit.Substring(0, m_nCommitLength) : sCommit;
		}

		return true;
	}

	private static async Task<(int nExit, string sOut, string sErr)> Run(string sFolder, string sCommand, int nTimeoutMs, CancellationToken kStop)	// 폴더에서 cmd 명령 실행 (창 없음, git 인증 입력 대기 차단) → 종료 코드·출력 (시간 초과·종료 요청: 명령 프로세스 트리 강제 종료 → -1)
	{
		ProcessStartInfo kInfo = new ProcessStartInfo("cmd.exe", "/c " + sCommand)
		{
			WorkingDirectory		= sFolder,
			UseShellExecute			= false,
			CreateNoWindow			= true,
			RedirectStandardOutput	= true,
			RedirectStandardError	= true,
			StandardOutputEncoding	= Encoding.UTF8,
			StandardErrorEncoding	= Encoding.UTF8,
		};

		kInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";	// git 이 인증 입력 대기로 멈추지 않게

		System.Diagnostics.Process?	kProcess;

		try
		{
			kProcess = System.Diagnostics.Process.Start(kInfo);
		}
		catch (Exception kException)	// 실행 실패
		{
			ProcessLog.Write(FATAL, $"명령 실행 실패: {Mask(sCommand)} ({kException.Message})");

			return (-1, "", "");
		}

		if (kProcess == null)
		{
			ProcessLog.Write(FATAL, $"명령 실행 실패: {Mask(sCommand)}");

			return (-1, "", "");
		}

		using (kProcess)
		{
			Task<string>	taskOut	= kProcess.StandardOutput.ReadToEndAsync();	// 출력 버퍼가 차서 멈추지 않게 동시 읽기
			Task<string>	taskErr	= kProcess.StandardError.ReadToEndAsync();

			using CancellationTokenSource kWait = CancellationTokenSource.CreateLinkedTokenSource(kStop);

			kWait.CancelAfter(nTimeoutMs);

			try
			{
				await kProcess.WaitForExitAsync(kWait.Token);
			}
			catch (OperationCanceledException)	// 시간 초과·종료 요청: 자식(git 등)까지 강제 종료
			{
				try
				{
					kProcess.Kill(entireProcessTree: true);
				}
				catch (Exception)	// 그 사이 종료됨
				{
				}

				bool	bStop	= kStop.IsCancellationRequested;

				ProcessLog.Write(bStop ? NOTI_ : FATAL, $"명령 실행 중단({(bStop ? "정지 요청" : "시간 초과")}): {Mask(sCommand)}");	// 정지 요청은 FATAL 대기 없이 바로 종료

				return (-1, "", "");
			}

			return (kProcess.ExitCode, await taskOut, await taskErr);
		}
	}

	private static string GetRepoUrl()	// 저장소 주소 (Git:sPass 토큰 있음: https://토큰@... )
	{
		string	sUrl	= Config.GetString("Git:sUrl", m_sDefaultUrl);
		string	sToken	= Config.GetString("Git:sPass");	// sCode 복호화 값
		int		nScheme	= sUrl.IndexOf("://", StringComparison.Ordinal);

		if (sToken == "" || nScheme < 0)	// 토큰 없음: git 자격 증명 사용
		{
			return sUrl;
		}

		return sUrl.Insert(nScheme + 3, Uri.EscapeDataString(sToken) + "@");
	}

	private static string Mask(string sText)	// 로그용: 저장소 토큰을 **** 로 (명령·git 오류 메시지)
	{
		string	sToken	= Config.GetString("Git:sPass");

		if (sToken == "")
		{
			return sText;
		}

		return sText.Replace(Uri.EscapeDataString(sToken), "****").Replace(sToken, "****");
	}

	private static string GetLastLine(string sText)	// 출력 마지막 줄 (오류 원인 표시용, 토큰 가림, 없음: "")
	{
		string	sLine	= sText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

		return Mask(sLine);
	}

	private static void DeleteFile(string sPath)	// 파일 삭제 (없음·실패 무시)
	{
		try
		{
			File.Delete(sPath);
		}
		catch (Exception)
		{
		}
	}
}

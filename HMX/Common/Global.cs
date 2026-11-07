static class Global	// 전역 함수 (Server·Client 공통, 프로젝트별 값: Main)
{
	public static int GetDefaultPort()	// 기본 포트 (Main.m_nDefaultPort: 서버 웹·클라 Server TCP)
	{
		return Main.m_nDefaultPort;
	}

	public static string GetMachine()	// 머신 이름 (설정 sMachine, 없음: 컴퓨터 이름 → 서버 Hello·받은 진행 머신 폴더)
	{
		string sMachine = Config.GetString("sMachine").Trim();

		return sMachine != "" ? sMachine : Environment.MachineName;
	}

	public static string GetProjectName()	// 프로젝트명 = 어셈블리 이름 (설정 json·로그 파일 이름)
	{
		return typeof(Global).Assembly.GetName().Name ?? "";
	}

	private static volatile string m_sMasterKey = Define.m_sSolution;	// 마스터 키 (메모리만, 서버: 시작 때 비움 → 로그인 사용: 오픈 뒤 첫 접속 1회 입력·안 씀: 솔루션명, 클라: 시작 때 비움 → 서버에서 받음)

	public static string GetMasterKey()	// 마스터 키 (sCode AES 키, 암호화: SELECT TO_BASE64(AES_ENCRYPT('Password', '마스터 키')), 없음: "" → 복호화 안 함)
	{
		return m_sMasterKey;
	}

	public static void SetMasterKey(string sKey)	// 마스터 키 바꿈 (메모리만, 파일 저장 안 함)
	{
		m_sMasterKey = sKey;
	}

	public static string GetMasterError([System.Runtime.CompilerServices.CallerLineNumber] int nLine = 0)	// 마스터 키 오류 문구 (사유 숨김: 부른 줄 번호만)
	{
		return $"마스터 에러 {nLine}";
	}

	public static string GetConfigFile()	// 설정 파일명
	{
		return GetProjectName() + ".json";
	}

	public static string GetConfigDir()	// 설정 json 폴더 = 기준 폴더\Json (설정 파일·따로 관리 파일)
	{
		return Path.Combine(GetBaseDir(), "Json");
	}

	public static string GetBaseDir()	// 기준 폴더 = 콘텐츠 루트 (서비스: exe 폴더, 그 외: 현재 폴더)
	{
		return WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : Directory.GetCurrentDirectory();
	}

	public static long GetAvailableMemory()	// 남은 물리 메모리 (바이트, 실패: 런타임 전체 메모리 절반)
	{
		MEMORYSTATUSEX kStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };

		if (OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref kStatus))
		{
			return (long)kStatus.ullAvailPhys;
		}

		return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX kStatus);

	[StructLayout(LayoutKind.Sequential)]
	private struct MEMORYSTATUSEX	// 윈도우 메모리 상태 (GlobalMemoryStatusEx)
	{
		public uint		dwLength;
		public uint		dwMemoryLoad;
		public ulong	ullTotalPhys;
		public ulong	ullAvailPhys;
		public ulong	ullTotalPageFile;
		public ulong	ullAvailPageFile;
		public ulong	ullTotalVirtual;
		public ulong	ullAvailVirtual;
		public ulong	ullAvailExtendedVirtual;
	}

	public static DateTimeOffset GetNow()	// 현재 시각 (KST, UTC+9)
	{
		return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
	}

	public static string GetNowText()	// 현재 시각 문자열 (KST)
	{
		return GetNow().ToString("yyyy-MM-dd HH:mm:ss");
	}

	public static void SetKeepAlive(Socket kSocket)	// TCP keepalive (끊긴 연결 감지)
	{
		try
		{
			kSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
			kSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
			kSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
			kSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);	// 윈도우 10 1703 이상
		}
		catch (Exception)	// 구버전 OS: 지원 옵션까지만 적용
		{
		}
	}

	public static string GetErrorText(Exception kException, string sWhere)	// 예외 → 페이지 표시용 두 단어 (상세: 로그, sWhere: 로그 앞 글, 예: 덤프 삭제D:\Dump.dmp)
	{
		Log.Write(WARN_, $"{sWhere} 실패: {kException.Message}");

		return kException switch
		{
			RelayException												=> kException.Message,	// 상대가 보낸 글 그대로
			OperationCanceledException									=> "User Stop",
			TimeoutException											=> "Time Out",
			UnauthorizedAccessException									=> "Access Denied",
			FileNotFoundException or DirectoryNotFoundException			=> "Not Found",
			PathTooLongException										=> "Path Long",
			JsonException or FormatException or InvalidDataException	=> "Bad Data",
			SocketException or ObjectDisposedException					=> Main.m_sPeerOff,
			IOException when kException.InnerException is SocketException	=> Main.m_sPeerOff,
			IOException when kException.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)	=> "File Busy",	// 공유·잠금 위반 (사용 중)
			IOException when kException.HResult is unchecked((int)0x80070070)	=> "Disk Full",
			IOException													=> "IO Error",
			_															=> Main.m_sSelfError,
		};
	}

	public static string GetText(JsonElement kRoot, string sKey)	// 문자열 값 (객체 아님·키 없음·문자열 아님: "")
	{
		if (kRoot.ValueKind != JsonValueKind.Object || !kRoot.TryGetProperty(sKey, out JsonElement kValue) || kValue.ValueKind != JsonValueKind.String)
		{
			return "";
		}

		return kValue.GetString() ?? "";
	}

	public static long GetLong(JsonElement kRoot, string sKey)	// 정수 값 (객체 아님·키 없음·정수 아님: -1)
	{
		if (kRoot.ValueKind != JsonValueKind.Object || !kRoot.TryGetProperty(sKey, out JsonElement kValue) || kValue.ValueKind != JsonValueKind.Number || !kValue.TryGetInt64(out long nValue))
		{
			return -1;
		}

		return nValue;
	}

	public static bool TryGetWild(string sTerm, out string sCore, out bool bHead, out bool bTail)	// 와일드카드 검색어: 앞뒤 * (ab*: 단어 시작, *ab: 단어 끝, *ab*: 어디든), sCore: * 뗀 글자 (* 없음·* 뿐: false)
	{
		string sTrim = sTerm.Trim();

		bHead	= sTrim.StartsWith('*');
		bTail	= sTrim.EndsWith('*');
		sCore	= sTrim.Trim('*');

		if ((bHead || bTail) && sCore != "")
		{
			return true;
		}

		sCore = "";

		return false;
	}

	public static bool IsWildMatch(ReadOnlySpan<char> kText, string sCore, bool bHead, bool bTail)	// 와일드카드 맞음 (대소문자 무시, 앞 * 없음: 앞 글자 없음·글자·숫자 아님, 뒤 * 없음: 뒤 같음 → '-'·'_' 도 경계 = 조각 색인과 같음)
	{
		for (int nFrom = 0; nFrom <= kText.Length - sCore.Length;)
		{
			int nAt = kText.Slice(nFrom).IndexOf(sCore, StringComparison.OrdinalIgnoreCase);	// 다음 위치

			if (nAt < 0)
			{
				return false;
			}

			nAt += nFrom;

			int nEnd = nAt + sCore.Length;	// 끝 (미포함)

			if ((bHead || nAt == 0 || !char.IsLetterOrDigit(kText[nAt - 1])) && (bTail || nEnd == kText.Length || !char.IsLetterOrDigit(kText[nEnd])))
			{
				return true;
			}

			nFrom = nAt + 1;
		}

		return false;
	}

	public static async Task Delay(int nMs, CancellationToken kStop)	// 대기 (종료 요청 시 예외 없이 반환)
	{
		try
		{
			await Task.Delay(nMs, kStop);
		}
		catch (OperationCanceledException)
		{
		}
	}
}

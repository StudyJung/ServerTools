static class Log	// 로그: log/YYYYMMDD/프로젝트명_YYYYMMDD_HH.log 시간별 (줄: [yyyy/MM/dd HH:mm:ss.fff] [sTitle_][레벨] 메시지, sTitle 없음: [레벨] 부터, 설정 sLog 레벨 이상만) (Server·Client 공통)
{
	public const string	DEBUG	= "DEBUG";	// 로그 레벨: 디버그
	public const string	INFO_	= "INFO_";	// 로그 레벨: 정보
	public const string	NOTI_	= "NOTI_";	// 로그 레벨: 알림
	public const string	WARN_	= "WARN_";	// 로그 레벨: 경고
	public const string	ERROR	= "ERROR";	// 로그 레벨: 오류
	public const string	FATAL	= "FATAL";	// 로그 레벨: 치명

	private static readonly string[]	m_arrLevel	= { DEBUG, INFO_, NOTI_, WARN_, ERROR, FATAL };	// 레벨 순서 (낮음 → 높음)

	public static readonly string[]	m_arrLogSetting	= { "Debug", "Info", "Warn" };	// 설정 sLog 값 (그 레벨 이상만 남김)

	private static readonly object	m_lockWrite		= new object();	// 같은 파일 동시 추가 방지
	private static readonly bool	m_bConsole		= !WindowsServiceHelpers.IsWindowsService();	// 콘솔 모드 (서비스 아님)
	private static readonly object	m_lockConsole	= new object();	// 콘솔 색 변경·출력·복원 묶음 (색 번짐 방지)

	public static string GetDir()	// 로그 폴더 (기준 폴더 하위 log)
	{
		return Path.Combine(GetBaseDir(), "log");
	}

	public static string GetFile(string sStamp)	// 시간별 로그 파일 (sStamp: yyyyMMdd_HH)
	{
		return Path.Combine(GetDir(), sStamp.Substring(0, 8), $"{GetProjectName()}_{sStamp}.log");
	}

	public static string GetTag()	// 레벨 앞 태그: [설정 sTitle_] (예: HMX → [시각] [HMX_][INFO_] 내용, 없음·설정 로딩 전: 안 붙음)
	{
		string	sTitle	= Config.GetString("sTitle").Trim();	// 이름 (제목과 같음)

		return sTitle == "" ? "" : $"[{sTitle}_]";
	}

	public static void Write(string sLevel, string sMessage, bool bFile = true)	// 로그 한 줄 추가 (콘솔 모드: 콘솔도, 파일: bLog true 일 때만, bFile false: 콘솔만, 설정 sLog 아래 레벨: 안 남김)
	{
		if (!IsLevel(sLevel))
		{
			return;
		}

		DateTimeOffset	kNow	= GetNow();
		string			sFile	= GetFile(kNow.ToString("yyyyMMdd_HH", CultureInfo.InvariantCulture));
		string sLine = $"[{kNow.ToString("yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {GetTag()}[{sLevel}] {sMessage.Replace('\r', ' ').Replace('\n', ' ')}";	// 레벨 앞 태그 (설정 sTitle), 줄바꿈 제거 (한 줄 = 로그 1건)

		if (m_bConsole)
		{
			WriteConsole(sLevel, sLine);
		}

		if (!bFile || !Config.GetBool("bLog"))	// 키 없음·설정 로딩 전: false
		{
			return;
		}

		try
		{
			lock (m_lockWrite)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(sFile)!);
				File.AppendAllText(sFile, sLine + "\n", new UTF8Encoding(false));
			}
		}
		catch (Exception kException)
		{
			Console.WriteLine($"로그 쓰기 실패: {kException.Message}");
		}
	}

	private static bool IsLevel(string sLevel)	// 남길 레벨 (설정 sLog: Debug: 모두, Info: INFO_ 이상 (DEBUG 안 남김), Warn: WARN_ 이상 (DEBUG·INFO_·NOTI_ 안 남김), ERROR·FATAL: 늘 남김, 콘솔·파일 같이, 대소문자 무시, 없음: Debug, 설정 다시 읽기: 바로 반영, 설정 로딩 전: 모두)
	{
		int	nMin	= Config.GetString("sLog").Trim().ToLowerInvariant() switch	// 남길 최소 레벨 순서
		{
			"info"	=> Array.IndexOf(m_arrLevel, INFO_),
			"warn"	=> Array.IndexOf(m_arrLevel, WARN_),
			_		=> 0,
		};
		int	nLevel	= Array.IndexOf(m_arrLevel, sLevel);	// 이 줄 레벨 순서 (모름: -1 → 남김)

		return nLevel < 0 || nLevel >= nMin;
	}

	private static void WriteConsole(string sLevel, string sLine)	// 콘솔 한 줄 출력 (레벨별 글자색)
	{
		ConsoleColor? eColor = sLevel switch	// null: 기본색
		{
			DEBUG			=> ConsoleColor.Green,
			NOTI_ or WARN_	=> ConsoleColor.Yellow,
			ERROR or FATAL	=> ConsoleColor.Red,
			_				=> null,
		};

		lock (m_lockConsole)
		{
			if (eColor == null)
			{
				Console.WriteLine(sLine);

				return;
			}

			Console.ForegroundColor = eColor.Value;
			Console.WriteLine(sLine);
			Console.ResetColor();
		}
	}
}

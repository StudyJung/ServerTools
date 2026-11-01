#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Worker
#:property PublishAot=false
#:property ServerGarbageCollection=false
#:package Microsoft.Extensions.Hosting.WindowsServices@10.0.10

// LMClient - 로그 감시 클라 (.NET 10 file-based app, 윈도우 서비스 지원)
// 실행: dotnet run --file LMClient.cs (같은 폴더 LMClient.csproj 때문에 --file 필수)
// 디버깅: LMClient.slnx (Visual Studio 2026) F5
// 구성: HTGlobal(전역 함수) → HTLog(로그) → HTAES(복호화) → HTConfig(설정) → HTTimer(타이머) → HTMain(진입점) → LogClient 등(로그 감시·TCP 전송·검색·보기)
// 설정 파일: 기준 폴더 LMClient.json (서비스: exe 폴더), HTConfig.GetString("sIP")·GetInt("nPort") 등 (키 없음: 기본값)
//   Base:bLog : 로그 파일 기록 (false·없음: 콘솔만)
//   Base:sZone : 존 (Dev·Alpha·Real 등, 서버 쿼리 Zone 필드, 없음: "")
//   sIP   : LMServer 아이피 (호스트명 가능)
//   nPort : LMServer TCP 포트 (LMServer.json 의 nPort)
//   aLogFolder : 기준 폴더 배열 (쿼리·서치·덤프·파일 공통, 필수, 하위 X단계까지)
//     쿼리·서치 대상: 기준 폴더 자신·하위 중 바로 아래 날짜 폴더(yyyyMMdd)를 가진 폴더 (예: Log1\Server·Log2·Log2\Client, 날짜 폴더 안 제외, X초마다 재탐색)
//       쿼리: 대상마다 최신 *yyyyMMdd_HH.log 1개 읽기 전용 열기, 서치: 대상 날짜 폴더의 같은 규칙 파일
//     덤프: 하위 *.dmp 감시·목록, 파일: 하위 폴더 보기·다운
//   aLogType   : 보낼 로그 종류 배열 (줄의 두 번째 [], 예: [2026/10/31 09:07:31.635] [ERROR] 내용 → ERROR)
//   aField     : 보낼 줄 필드 배열 [{sKey, sRegex}] (정규식 첫 () 값, 없음: 맞은 부분 → 서버 쿼리 필드, 키: 영문 시작·영문·숫자·_ X자 이하·Machine·Folder·Zone·Type 불가, 오류·중복: 제외)
//   nLogRead   : 파일마다 감시 주기당 최대 읽기 줄 수 (기본 1000, 남은 줄: 다음 주기)
//   nLogSend   : 파일마다 감시 주기당 최대 전송 줄 수 (aLogType 줄, 기본 100, 차면 그 줄까지만 본 것으로)
//   sCode : 로딩 시 HTAES 복호화 → 같은 위치 sPass 로 조회 (예: "DB:Dev:sCode" → GetString("DB:Dev:sPass"))
// 로그 감시 상태: 기준 폴더 LMClient.data (예전 LMClient.txt: 시작 때 이름 변경, 대상 폴더마다 한 줄 "전체 경로\t줄 번호", 전송 후·변경 시만 저장)
//   시작: 본 줄 다음부터 (파일 없음: 다음 시간 파일부터, 빠진 대상: 줄 삭제), txt 없음·새 대상: 최신 파일 끝부터
//   다음 시간 파일 (yyyyMMdd_HH, 날짜 바뀜 포함): 지금 파일 끝까지 본 뒤 이동 (시간 건너뜀 없음, 폴더 훑기는 감시 파일 시간 경과 후 X초마다만)
//   서버 접속 중에만 검사·전송 (끊김: 위치 유지, 재접속 시 이어서), 전송은 일방 (응답 확인 없음, 쓰기 실패: 다음 주기 재시도)
// 전송: TCP 한 줄 = JSON 1건 (UTF-8, '\n' 구분), 끊김: X초부터 2배씩 최대 X초 대기 후 재접속 (실패 로그: 처음 1회 + X회마다)
//   {"sType":"Hello","sMachine":"","sDir":"","arrField":[""],"nPid":0,"sTime":""}	접속 직후 1회 (sDir: aLogFolder 전체 경로, "|" 구분, arrField: aField 키 설정 순서 → 서버 필드 목록 순서)
//   {"sType":"File","sEvent":"Created|Changed|Deleted|Renamed|Error","sPath":"","sOldPath":"","nSize":0,"sMessage":"","sTime":""}
//   {"sType":"Log","sPath":"","sFolder":"","sZone":"","arrLine":[""],"arrNo":[0],"arrField":[{"키":"값"}],"sTime":""}	로그 감시 (sZone: Base:sZone, arrNo: 줄마다 파일 줄 번호 (첫 줄 1), arrField: 줄마다 aField 값 (값 있는 키만, aField 없음: null), sFolder: 대상 폴더): 감시 주기마다 시간이 지금 초 미만인 끝난 줄 최대 nLogRead 줄 중 aLogType 줄 최대 nLogSend 줄
//   {"sType":"Ping","sTime":""}	X초 무전송 시
// 알림 (서버 → 클라): {"sType":"Pause"} 서버 적재 밀림 → 로그 감시 멈춤 (위치 유지), {"sType":"Resume"} 재개, 재접속: 해제 (밀림 중이면 서버가 Hello 뒤 Pause 재전송)
// 요청 (서버 → 클라) / 결과 (클라 → 서버), nReqId 로 짝, sPath: 전체 경로
//   {"sType":"Search","nReqId":0,"nStart":2026102600,"nEnd":2026102624,"sKey":"","nMax":1000}
//     → {"sType":"SearchResult","nReqId":0,"bTruncated":false,"bStopped":false,"nFileCount":0,"nElapsedMs":0,"sError":"","arrLine":[{"sPath":"","nDateHour":0,"nLine":0,"sText":""}]}
//     → 검색 중 X초마다 {"sType":"SearchProgress","nReqId":0,"nFileTotal":0,"nFileDone":0,"nFound":0,"nElapsedMs":0}	(nFileTotal -1: 앞 검색 대기 중)
//   {"sType":"Stop","nReqId":0}	검색 중지 → 그때까지 결과 bStopped=true
//   {"sType":"View","nReqId":0,"sPath":"","nStart":1,"nCount":500}	(nStart 0: 마지막 nCount 줄)
//     → {"sType":"ViewResult","nReqId":0,"sPath":"","nStart":1,"nTotal":0,"sError":"","arrText":[""]}
//   {"sType":"Dump","nReqId":0}	덤프 목록 (aLogFolder 하위 *.dmp, 경로 순)
//     → {"sType":"DumpResult","nReqId":0,"bTruncated":false,"sError":"","arrFile":[{"sPath":"","nSize":0,"sTime":""}]}
//   {"sType":"DumpRead","nReqId":0,"sPath":"","nOffset":0,"nCount":4194304}	덤프 다운 조각 (최대 X MB, 서버가 zip 저장)
//     → {"sType":"DumpReadResult","nReqId":0,"sPath":"","nOffset":0,"nSize":0,"sError":"","arrData":"base64"}	(nSize: 지금 파일 크기)
//   {"sType":"DumpDelete","nReqId":0,"sPath":""}	덤프 삭제
//     → {"sType":"DumpDeleteResult","nReqId":0,"sPath":"","sError":""}
//   {"sType":"FileRead","nReqId":0,"sPath":"","nOffset":0,"nCount":4194304}	파일 다운 조각 (최대 X MB, 확장자 제한 없음)
//     → {"sType":"FileReadResult","nReqId":0,"sPath":"","nOffset":0,"nSize":0,"sError":"","arrData":"base64"}	(nSize: 지금 파일 크기)
//   {"sType":"Files","nReqId":0,"sPath":""}	폴더 보기 (바로 아래만, "": aLogFolder 목록 → arrDir)
//     → {"sType":"FilesResult","nReqId":0,"sPath":"","bTruncated":false,"sError":"","arrDir":[""],"arrFile":[{"sName":"","nSize":0,"sTime":""}]}
//   허용 경로: 덤프 aLogFolder 하위 *.dmp, 폴더 보기 aLogFolder 자신·하위 (그 외 거부), 동시 처리: 보기와 합쳐 m_nViewConcurrent 건
// 검색 대상: 쿼리 대상 폴더의 날짜 폴더(yyyyMMdd) 안 *yyyyMMdd_HH.log (예: Logs\XLobby\20261020\XLobby_20261020_11.log)
//   기간 nStart~nEnd (yyyyMMddHH, 양끝 포함, 24시 = 그날 23시까지), 검색어 포함 줄 (대소문자 무시, 대문자 AND·OR·NOT·괄호, 왼쪽부터, 예: "A AND (B NOT C) OR D", SearchNode)
//   머신당 최대 nMax 건·X초, 1건씩 처리 (게임 서버 부하 제한)
//   부하 조절: 검색·보기 Throttle (X ms 일하면 X ms 쉼)
//   보기 허용: aLogFolder 하위 *.log·*.txt·*.json·*.ini·*.bat·*.xml·*.config·*.md·*.lua (그 외 거부), 인코딩: 줄마다 UTF-8·CP949 판별 (UTF-8·UTF-16 BOM 지원)
// 로그: Base:bLog 가 true 일 때만 기준 폴더 Log/YYYYMMDD/LMClient_YYYYMMDD_HH.log 기록 → HTLog.Write(INFO_, "메시지") (레벨: DEBUG·INFO_·NOTI_·WARN_·ERROR·FATAL)
// 타이머: HTTimer.Start → 별도 스레드 X초마다 Batch (X분마다 Alive 로그), 종료 시 HTTimer.Stop
// 종료: Ctrl+C (서비스: 서비스 중지)

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32.SafeHandles;
using static HTGlobal;	// 전역 함수 클래스명 없이 호출
using static HTLog;		// 로그 레벨 클래스명 없이 호출

static class HTGlobal	// 전역 함수
{
	public static int GetDefaultPort()	// 기본 포트 (LMServer TCP)
	{
		return 9091;
	}

	public static string GetProjectName()	// 프로젝트명 = 어셈블리 이름 (AES 키, 암호화: SELECT TO_BASE64(AES_ENCRYPT('Password', 'ProjectName')))
	{
		return typeof(HTGlobal).Assembly.GetName().Name ?? "";
	}

	public static string GetConfigFile()	// 설정 파일명
	{
		return GetProjectName() + ".json";
	}

	public static string GetBaseDir()	// 기준 폴더 = 콘텐츠 루트 (서비스: exe 폴더, 그 외: 현재 폴더)
	{
		return WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : Directory.GetCurrentDirectory();
	}

	public static DateTimeOffset GetNow()	// 현재 시각 (KST, UTC+9)
	{
		return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
	}

	public static string GetNowText()	// 현재 시각 문자열 (KST)
	{
		return GetNow().ToString("yyyy-MM-dd HH:mm:ss");
	}

	public static void SetKeepAlive(Socket kSocket)	// TCP keepalive: X초 무통신 시 X초 간격 확인 (끊긴 연결 감지)
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

	public static string GetErrorText(Exception kException, string sWhere)	// 예외 → 페이지 표시용 짧은 오류 (예: File Busy), 상세는 로그 (sWhere: 로그 앞 글)
	{
		HTLog.Write(WARN_, $"{sWhere} 실패: {kException.Message}");

		return kException switch
		{
			RelayException												=> kException.Message,	// 상대가 보낸 글 그대로
			OperationCanceledException									=> "User Stop",
			TimeoutException											=> "Time Out",
			UnauthorizedAccessException									=> "Access Denied",
			FileNotFoundException or DirectoryNotFoundException			=> "Not Found",
			PathTooLongException										=> "Path Long",
			JsonException or FormatException or InvalidDataException	=> "Bad Data",
			SocketException or ObjectDisposedException					=> "Server Off",
			IOException when kException.InnerException is SocketException	=> "Server Off",
			IOException when kException.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)	=> "File Busy",	// 공유·잠금 위반 (사용 중)
			IOException when kException.HResult is unchecked((int)0x80070070)	=> "Disk Full",
			IOException													=> "IO Error",
			_															=> "Client Error",
		};
	}
}

static class HTLog	// 로그: Log/YYYYMMDD/프로젝트명_YYYYMMDD_HH.log 시간별 기록 (줄: [yyyy/MM/dd HH:mm:ss.fff] [레벨] 메시지)
{
	public const string DEBUG	= "DEBUG";	// 로그 레벨: 디버그
	public const string INFO_	= "INFO_";	// 로그 레벨: 정보
	public const string NOTI_	= "NOTI_";	// 로그 레벨: 알림
	public const string WARN_	= "WARN_";	// 로그 레벨: 경고
	public const string ERROR	= "ERROR";	// 로그 레벨: 오류
	public const string FATAL	= "FATAL";	// 로그 레벨: 치명

	private static readonly object	m_lockWrite	= new object();								// 같은 파일 동시 추가 방지
	private static readonly bool	m_bConsole	= !WindowsServiceHelpers.IsWindowsService();	// 콘솔 모드 (서비스 아님: 콘솔에도 출력)
	private static readonly object	m_lockConsole	= new object();							// 콘솔 색 변경·출력·복원 묶음 (다른 로그 줄에 색 번짐 방지)

	public static string GetDir()	// 로그 폴더 (기준 폴더 하위 Log)
	{
		return Path.Combine(GetBaseDir(), "Log");
	}

	public static string GetFile(string sStamp)	// 시간별 로그 파일 경로 (sStamp: yyyyMMdd_HH)
	{
		return Path.Combine(GetDir(), sStamp.Substring(0, 8), $"{GetProjectName()}_{sStamp}.log");
	}

	public static void Write(string sLevel, string sMessage)	// 로그 한 줄 추가 (콘솔 모드: 콘솔 출력, 파일: Base:bLog true 일 때만)
	{
		DateTimeOffset kNow = GetNow();
		string sFile = GetFile(kNow.ToString("yyyyMMdd_HH", CultureInfo.InvariantCulture));
		string sLine = $"[{kNow.ToString("yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] [{sLevel}] {sMessage.Replace('\r', ' ').Replace('\n', ' ')}";	// 줄바꿈 제거 (한 줄 = 로그 1건)

		if (m_bConsole)
		{
			WriteConsole(sLevel, sLine);
		}

		if (!HTConfig.GetBool("Base:bLog"))	// 파일 기록 안 함 (false·키 없음·설정 로딩 전)
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
		catch (Exception kException)	// 쓰기 실패 (권한 등)
		{
			Console.WriteLine($"로그 쓰기 실패: {kException.Message}");
		}
	}

	private static void WriteConsole(string sLevel, string sLine)	// 콘솔 한 줄 출력 (레벨별 글자색)
	{
		ConsoleColor? eColor = sLevel switch
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

static class HTAES	// MySQL AES 호환 복호화 (MySQL 기본 block_encryption_mode = aes-128-ecb, PKCS7 패딩)
{
	public static string? Decrypt(string sData)	// SELECT CAST(AES_DECRYPT(FROM_BASE64(sData), GetProjectName()) AS CHAR) (실패: null = MySQL NULL)
	{
		if (string.IsNullOrEmpty(sData))
		{
			return null;
		}

		byte[] arrData;

		try
		{
			arrData = Convert.FromBase64String(sData);
		}
		catch (FormatException)
		{
			return null;
		}

		if (arrData.Length == 0 || arrData.Length % 16 != 0)	// AES 블록(16바이트) 단위 아님
		{
			return null;
		}

		try
		{
			using Aes kAes = Aes.Create();

			kAes.Key = GetMySQLKey(GetProjectName());

			return Encoding.UTF8.GetString(kAes.DecryptEcb(arrData, PaddingMode.PKCS7));	// CAST AS CHAR (utf8mb4)
		}
		catch (CryptographicException)	// 키 불일치·패딩 오류
		{
			return null;
		}
	}

	private static byte[] GetMySQLKey(string sKey)	// MySQL 키 변환: 키 바이트를 16바이트(128bit) 버퍼에 순환 XOR
	{
		byte[] arrKey		= new byte[16];
		byte[] arrSource	= Encoding.UTF8.GetBytes(sKey);

		for (int nIndex = 0; nIndex < arrSource.Length; ++nIndex)
		{
			arrKey[nIndex % arrKey.Length] ^= arrSource[nIndex];
		}

		return arrKey;
	}
}

static class HTConfig	// 설정 파일 로딩 및 값 조회 (키 구분자 ':', 예: GetString("DB:Dev:sHost"))
{
	private static IConfigurationRoot m_kConfig = new ConfigurationBuilder().Build();	// 설정 값 (Load 전·파일 없음: 빈 설정)

	public static bool Load()	// 설정 파일 로딩 (기준 폴더, false: 형식 오류)
	{
		string sPath = Path.Combine(GetBaseDir(), GetConfigFile());

		if (!File.Exists(sPath) || new FileInfo(sPath).Length == 0)	// 없음·빈 파일: 빈 설정 (빈 파일은 JSON 파싱 오류)
		{
			HTLog.Write(WARN_, $"설정 파일: 없음 또는 빈 파일 ({sPath})");

			return true;
		}

		try
		{
			m_kConfig = new ConfigurationBuilder().AddJsonFile(sPath, optional: false, reloadOnChange: false).Build();
		}
		catch (Exception kException)	// JSON 형식 오류
		{
			HTLog.Write(ERROR, $"설정 오류: {sPath} ({kException.Message})");

			return false;
		}

		int nDecrypt = DecryptCode();

		HTLog.Write(INFO_, $"설정 파일: {sPath} (복호화: {nDecrypt}건)");

		return true;
	}

	private static int DecryptCode()	// sCode 값 복호화 → 같은 위치 sPass 로 메모리 보관 (반환: 등록 건수)
	{
		Dictionary<string, string> hmapPass = new Dictionary<string, string>();	// sPass 키 → 복호화 값 (열거 중 설정 변경 방지)

		foreach (KeyValuePair<string, string?> kPair in m_kConfig.AsEnumerable())
		{
			if (!string.Equals(ConfigurationPath.GetSectionKey(kPair.Key), "sCode", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(kPair.Value))
			{
				continue;
			}

			string? sPass = HTAES.Decrypt(kPair.Value);

			if (sPass == null)	// 복호화 실패: sPass 미등록, 값 출력 금지
			{
				HTLog.Write(ERROR, $"설정 복호화 실패: {kPair.Key}");

				continue;
			}

			string? sParent = ConfigurationPath.GetParentPath(kPair.Key);	// 상위 경로 (최상위 sCode: null)

			hmapPass[sParent == null ? "sPass" : ConfigurationPath.Combine(sParent, "sPass")] = sPass;
		}

		foreach (KeyValuePair<string, string> kPass in hmapPass)
		{
			m_kConfig[kPass.Key] = kPass.Value;
		}

		return hmapPass.Count;
	}

	public static string GetString(string sKey, string sDefault = "")	// 문자열 값 (키 없음: 기본값)
	{
		return m_kConfig[sKey] ?? sDefault;
	}

	public static int GetInt(string sKey, int nDefault = 0)	// 정수 값 (키 없음·변환 실패: 기본값)
	{
		return int.TryParse(m_kConfig[sKey], out int nValue) ? nValue : nDefault;
	}

	public static double GetDouble(string sKey, double dDefault = 0)	// 실수 값 (키 없음·변환 실패: 기본값, JSON 소수점 '.' 고정 → InvariantCulture)
	{
		return double.TryParse(m_kConfig[sKey], NumberStyles.Float, CultureInfo.InvariantCulture, out double dValue) ? dValue : dDefault;
	}

	public static bool GetBool(string sKey, bool bDefault = false)	// bool 값 (키 없음·변환 실패: 기본값)
	{
		return bool.TryParse(m_kConfig[sKey], out bool bValue) ? bValue : bDefault;
	}

	public static string[] GetArray(string sKey)	// 배열 값 (빈 값 제외, 키 없음: 빈 배열)
	{
		return m_kConfig.GetSection(sKey).GetChildren().Select(kChild => kChild.Value ?? "").Where(sValue => !string.IsNullOrWhiteSpace(sValue)).ToArray();
	}

	public static IEnumerable<IConfigurationSection> GetChildren(string sKey)	// 하위 섹션 (배열: 순서대로, 키 없음: 없음)
	{
		return m_kConfig.GetSection(sKey).GetChildren();
	}
}

static class HTTimer	// 타이머: 별도 스레드에서 X초마다 Batch 호출, X분마다 Alive 로그 (Start → Stop)
{
	private const int	m_nTickMs		= 1000;	// Batch 호출 간격 (ms)
	private const int	m_nAliveMin		= 10;	// Alive 로그 간격 (분)

	private static Thread?					m_kThread;							// 타이머 스레드 (Start 생성, Stop 해제)
	private static CancellationTokenSource	m_kStop		= new CancellationTokenSource();	// 중지 요청 (대기 중이면 즉시 깸)
	private static DateTimeOffset			m_kStartTime;						// 시작 시각 (Alive 로그 가동 시간)
	private static long						m_nAliveTick;						// 마지막 Alive 로그 시각 (Environment.TickCount64)

	public static void Start()	// 타이머 시작 (백그라운드 스레드: 프로세스 종료 시 함께 종료)
	{
		if (m_kThread != null)	// 중복 시작 방지
		{
			return;
		}

		m_kStop			= new CancellationTokenSource();
		m_kStartTime	= GetNow();
		m_nAliveTick	= Environment.TickCount64;
		m_kThread		= new Thread(Run) { IsBackground = true, Name = "HTTimer" };

		m_kThread.Start();

		HTLog.Write(INFO_, $"타이머: 시작 (Batch {m_nTickMs}ms, Alive {m_nAliveMin}분)");
	}

	public static void Stop()	// 타이머 중지: 중지 요청 → 진행 중인 Batch 완료 대기 (최대 X초)
	{
		if (m_kThread == null)
		{
			return;
		}

		m_kStop.Cancel();
		m_kThread.Join(5000);

		m_kThread = null;

		HTLog.Write(INFO_, "타이머: 중지");
	}

	private static void Run()	// 타이머 스레드: Batch → 남은 시간 대기 (Batch 소요 시간 제외)
	{
		while (!m_kStop.IsCancellationRequested)
		{
			long nBegin = Environment.TickCount64;

			try
			{
				Batch();
			}
			catch (Exception kException)	// 로그 후 계속 (스레드 미처리 예외 → 프로세스 종료 방지)
			{
				HTLog.Write(ERROR, $"타이머 오류: {kException.Message}");
			}

			long nWait = Math.Max(0, m_nTickMs - (Environment.TickCount64 - nBegin));	// 남은 대기 시간 (Batch 가 X초 넘으면 바로 다음 호출)

			m_kStop.Token.WaitHandle.WaitOne((int)nWait);
		}
	}

	private static void Batch()	// X초마다 호출 (타이머 스레드): X분마다 Alive 로그
	{
		if (Environment.TickCount64 - m_nAliveTick >= m_nAliveMin * 60 * 1000L)
		{
			m_nAliveTick = Environment.TickCount64;

			HTLog.Write(INFO_, $"Alive: 가동 {(GetNow() - m_kStartTime).ToString(@"d\.hh\:mm\:ss")}");
		}

		// 차후 작업할 코드 아래에 추가
	}
}

class HTMain	// 진입점
{
	private const int	m_nFieldKeyMax		= 32;	// aField 키 최대 글자 (서버와 같음)
	private const int	m_nFieldRegexMax	= 500;	// aField 정규식 최대 글자
	private const int	m_nFieldRegexMs		= 100;	// aField 정규식 한 줄 제한 (ms, 넘음: 값 없음)

	static int Main(string[] args)	// 기본 설정·설정 파일 로딩 후 클라 실행 (0: 정상 종료, 1: 설정 오류)
	{
		Console.OutputEncoding = Encoding.UTF8;	// 한글 콘솔 출력

		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);	// CP949 로그 파일 읽기

		if (!HTConfig.Load())
		{
			return 1;
		}

		string	sIP		= HTConfig.GetString("sIP");
		int		nPort	= HTConfig.GetInt("nPort", GetDefaultPort());

		string[]	arrLogFolder	= HTConfig.GetArray("aLogFolder");
		string[]	arrLogType		= HTConfig.GetArray("aLogType");
		int			nLogSend		= HTConfig.GetInt("nLogSend", 100);
		int			nLogRead		= HTConfig.GetInt("nLogRead", 1000);

		if (arrLogFolder.Length == 0 || arrLogFolder.Any(string.IsNullOrWhiteSpace))
		{
			HTLog.Write(ERROR, $"설정 오류: aLogFolder 없음·빈 항목 ({GetConfigFile()})");

			return 1;
		}

		if (!IPAddress.TryParse(sIP, out _) && Uri.CheckHostName(sIP) == UriHostNameType.Unknown)	// 아이피·호스트명 형식 아님
		{
			HTLog.Write(ERROR, $"설정 오류: sIP 형식 오류 ({sIP})");

			return 1;
		}

		if (nPort <= 0 || nPort > 65535)
		{
			HTLog.Write(ERROR, $"설정 오류: nPort 범위 오류 ({nPort}, 1~65535)");

			return 1;
		}

		if (nLogSend <= 0 || nLogRead <= 0)
		{
			HTLog.Write(ERROR, $"설정 오류: nLogSend·nLogRead 범위 오류 ({nLogSend}·{nLogRead}, 1 이상)");

			return 1;
		}

		LogClient kClient = new LogClient(sIP, nPort, arrLogFolder, arrLogType, LoadField(), nLogSend, nLogRead);

		HTTimer.Start();

		Execute();

		Service(args, kClient);

		HTTimer.Stop();

		return 0;
	}

	static void Execute()
	{
		HTLog.Write(NOTI_, "앱: 실행");	// 차후 작업할 코드 아래에 추가
	}

	static LogField[] LoadField()	// 설정 aField → 보낼 줄 필드 (키: 영문 시작·영문·숫자·_ X자 이하·기본 필드·중복 불가 (대소문자 무시), 정규식: X자 이하, 오류: 제외)
	{
		string[]		arrBase		= { "Machine", "Folder", "Zone", "Type" };	// 서버 기본 필드 (쓸 수 없는 키)
		List<LogField>	listField	= new List<LogField>();

		foreach (IConfigurationSection kSection in HTConfig.GetChildren("aField"))
		{
			string sKey		= (kSection["sKey"] ?? "").Trim();
			string sRegex	= kSection["sRegex"] ?? "";

			if (sKey.Length > m_nFieldKeyMax || !Regex.IsMatch(sKey, "^[A-Za-z][A-Za-z0-9_]*$") || arrBase.Contains(sKey, StringComparer.OrdinalIgnoreCase) || listField.Any(kField => string.Equals(kField.sKey, sKey, StringComparison.OrdinalIgnoreCase)))
			{
				HTLog.Write(WARN_, $"설정 오류: aField 키 형식·중복 ({sKey}, 영문 시작·영문·숫자·_ {m_nFieldKeyMax}자 이하·기본 필드 아님)");

				continue;
			}

			if (sRegex == "" || sRegex.Length > m_nFieldRegexMax)
			{
				HTLog.Write(WARN_, $"설정 오류: aField {sKey} 정규식 없음·{m_nFieldRegexMax}자 넘음");

				continue;
			}

			try
			{
				listField.Add(new LogField(sKey, new Regex(sRegex, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(m_nFieldRegexMs))));
			}
			catch (ArgumentException kException)	// 정규식 형식 오류
			{
				HTLog.Write(WARN_, $"설정 오류: aField {sKey} 정규식 오류 ({kException.Message})");
			}
		}

		return listField.ToArray();
	}

	static void Service(string[] args, LogClient pClient)	// 서비스 SCM 연동 + 호스트 실행 (서비스 중지·Ctrl+C 까지 대기)
	{
		try
		{
			using Process kProcess = Process.GetCurrentProcess();

			kProcess.PriorityClass = ProcessPriorityClass.BelowNormal;	// 게임 서버보다 낮은 CPU 우선순위 (검색 부하 양보)
		}
		catch (Exception)	// 권한 없음: 기본 우선순위로 계속
		{
		}

		HostApplicationBuilder kBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
		{
			Args			= args,
			ContentRootPath	= WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,	// 서비스 실행 시 기준 폴더 = exe 폴더 (기본 System32)
		});

		kBuilder.Services.AddWindowsService();	// 서비스 실행 시 SCM 연동 (콘솔 실행 영향 없음)
		kBuilder.Services.AddHostedService(_ => pClient);	// 호스트와 함께 시작·중지
		kBuilder.Logging.SetMinimumLevel(LogLevel.Warning);	// 기본 로그 최소화 (클라 로그는 콘솔 직접 출력)

		kBuilder.Build().Run();
	}
}

class LogClient : BackgroundService	// 로그 감시 클라: 덤프 감시·로그 aLogType 줄 → LMServer TCP 전송, 서버 요청(검색·보기 등) 처리
{
	private const int	m_nPingSec			= 60;		// 무전송 시 Ping 주기·감시 폴더 재확인 주기 (초)
	private const int	m_nRetryMinSec		= 2;		// 재접속 대기 최소 (초)
	private const int	m_nRetryMaxSec		= 5;		// 재접속 대기 최대 (초, 실패마다 2배)
	private const int	m_nRetryLogCount	= 12;		// 연속 접속 실패 로그 간격 (회, 처음 1회 + X회마다)
	private const int	m_nFlushMs			= 1000;		// Changed 이벤트 묶음 전송 주기 (ms)
	private const int	m_nLogWatchMs		= 1000;		// 로그 감시 주기 (ms)
	private const int	m_nNextCheckMs		= 2000;		// 다음 시간 파일 찾기 주기 (ms, 감시 파일 시간 경과 후)
	private const int	m_nLogScanMs		= 60000;	// 쿼리·서치 대상 폴더 탐색 주기 (ms)
	private const int	m_nLogScanDepth		= 4;		// aLogFolder 하위 폴더 깊이 (대상 탐색·덤프 감시·목록)
	private const int	m_nLogTextMax		= 10000;	// 로그 감시 줄 최대 글자 (초과: "…")
	private const int	m_nQueueMax			= 10000;	// 전송 대기 최대 건수 (초과 시 오래된 것부터 버림)
	private const int	m_nSearchMax		= 1000000;	// 검색 결과 최대 건수 (서버 nMax 상한)
	private const int	m_nSearchDefault	= 1000;	// 검색 결과 최대 건수 기본값 (nMax 없음, 서버와 같음)
	private const int	m_nSearchLimitSec	= 60;		// 검색 최대 시간 (초, 초과 시 그때까지 결과만)
	private const int	m_nSearchTextMax	= 1000;		// 검색 결과 줄 최대 글자 (초과: "…")
	private const long	m_nSearchTextTotal	= 300000000;	// 검색 결과 글자 수 합 최대 (초과: 그때까지 결과만, 브라우저 문자열 한도·서버 m_nLineMax 안쪽)
	private const int	m_nViewCount		= 500;		// 보기 기본 줄 수
	private const int	m_nViewCountMax		= 2000;		// 보기 최대 줄 수
	private const int	m_nViewTextMax		= 10000;	// 보기 줄 최대 글자 (초과: "…")
	private const int	m_nWatchCheckMs		= 5000;		// 덤프 감시 상태 확인 주기 (ms)
	private const string	m_sWatchFilter	= "*.dmp";	// 감시 대상 파일 (덤프만, 대소문자 무시)
	private const int	m_nBatchMax			= 200;		// 한 번에 묶어 보낼 최대 줄 수 (flush 1회)
	private const int	m_nSearchWaitSec	= 20;		// 앞 검색 처리 중일 때 대기 (초, 초과: 거절 → 밀린 검색 방지)
	private const int	m_nViewWaitSec		= 10;		// 보기 동시 처리 초과 시 대기 (초, 초과: 거절)
	private const int	m_nViewConcurrent	= 2;		// 보기 동시 처리 수
	private const int	m_nRequestLineMax	= 1024 * 1024;	// 서버 요청 한 줄 최대 바이트 (초과: 연결 종료)
	private const int	m_nProgressMs		= 1000;			// 검색 진행 알림 주기 (ms)
	private const int	m_nReadBufferSize	= 128 * 1024;	// UTF-16 파일 읽기 버퍼 (바이트)
	private const int	m_nBlockBytes		= 256 * 1024;	// 검색·보기 블록 기본 크기 (바이트, 풀에서 재사용)
	private const int	m_nLineBytesMax		= 8 * 1024 * 1024;	// 블록 최대 크기 (바이트, 이보다 긴 줄은 조각으로 처리)
	private const int	m_nDumpMax			= 10000;			// 덤프 목록 최대 건수 (초과: bTruncated)
	private const int	m_nDumpChunkMax		= 4 * 1024 * 1024;	// 덤프 읽기 조각 최대 바이트 (서버 m_nDumpChunk 와 같음)
	private const int	m_nFileListMax		= 10000;			// 폴더 보기 최대 개수 (폴더·파일 합, 초과: bTruncated)

	private static readonly Encoding				m_kUtf8			= new UTF8Encoding(false);	// UTF-8 (BOM 없음, 재사용)
	private static readonly JsonSerializerOptions	m_kJsonOption	= new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };	// 한글 등 \uXXXX 미변환 (크기 절감)

	private readonly string								m_sIP;			// LMServer 아이피
	private readonly int								m_nPort;		// LMServer TCP 포트
	private readonly Channel<string>					m_queSend;		// 전송 대기 (JSON 한 줄)
	private readonly ConcurrentDictionary<string, byte>	m_hmapChanged;	// Changed 이벤트 경로 (주기마다 경로당 1건 전송, 로그 연속 쓰기 폭주 방지)
	private readonly SemaphoreSlim						m_kSearchLock;	// 검색 1건씩 (게임 서버 부하 제한)
	private readonly SemaphoreSlim						m_kViewLock;	// 보기 동시 m_nViewConcurrent 건
	private readonly ConcurrentDictionary<long, SearchJob>	m_hmapJob = new ConcurrentDictionary<long, SearchJob>();	// 진행 중인 검색 (요청 번호 → 진행·중지)
	private volatile bool								m_bWatchError;	// 감시 오류 발생 (WatchLoop 가 감시 다시 생성)
	private readonly TaskCompletionSource				m_kDumpReady	= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);	// 덤프 준비 끝 (준비 순서: 덤프 → 파일)
	private readonly TaskCompletionSource				m_kLogReady		= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);	// 쿼리 준비 끝 (준비 순서: 쿼리 → 서치)
	private readonly string[]							m_arrLogRoot;	// aLogFolder 전체 경로 (끝 구분자 없음 (드라이브 루트 제외), 중복 제외)
	private readonly List<LogWatch>						m_listLogWatch	= new List<LogWatch>();	// 로그 감시 (대상 폴더마다, 로그 감시 스레드 전용)
	private volatile string[]							m_arrLogFolder	= Array.Empty<string>();	// 쿼리·서치 대상 폴더 (탐색 시 새 배열로 교체 → 다른 스레드 읽기)
	private string										m_sLogScanError	= "";	// 마지막 대상 폴더 탐색 오류 (같은 오류 반복 출력 방지)
	private readonly string[]							m_arrLogType;	// 보낼 로그 종류 (두 번째 [] 안, 예: ERROR)
	private readonly LogField[]							m_arrField;		// 보낼 줄 필드 (설정 aField)
	private string										m_sLogState		= "";	// 마지막 저장 로그 감시 상태 (LMClient.data 내용, 로그 감시 스레드 전용)
	private volatile LineWriter?						m_kLogWriter;	// 로그 감시 전송 (접속 중: Hello 뒤 설정, 끊김: null → 로그 감시 쉼)
	private volatile bool								m_bLogPause;	// 서버 Pause 받음 (Resume·재접속: 해제)
	private readonly int								m_nLogSend;		// 설정 nLogSend
	private readonly int								m_nLogRead;		// 설정 nLogRead

	public LogClient(string sIP, int nPort, string[] arrLogFolder, string[] arrLogType, LogField[] arrField, int nLogSend, int nLogRead)
	{
		m_sIP			= sIP;
		m_nPort			= nPort;
		m_arrLogRoot	= arrLogFolder.Select(GetFullFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		m_arrLogType	= arrLogType;
		m_arrField		= arrField;
		m_nLogSend		= nLogSend;
		m_nLogRead		= nLogRead;
		m_queSend		= Channel.CreateBounded<string>(new BoundedChannelOptions(m_nQueueMax) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
		m_hmapChanged	= new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
		m_kSearchLock	= new SemaphoreSlim(1, 1);
		m_kViewLock		= new SemaphoreSlim(m_nViewConcurrent, m_nViewConcurrent);
	}

	protected override async Task ExecuteAsync(CancellationToken kStop)	// 탭 순서로 준비 (쿼리 → 서치 → 덤프 → 파일) 뒤 서버 접속
	{
		HTLog.Write(INFO_, $"클라 시작: aLogFolder {string.Join(", ", m_arrLogRoot)} -> 서버 {m_sIP}:{m_nPort}");

		List<Task> listLoop = new List<Task>();

		try
		{
			listLoop.Add(LogWatchLoop(kStop));	// 쿼리 (준비 끝: m_kLogReady)

			await m_kLogReady.Task.WaitAsync(kStop);

			HTLog.Write(INFO_, $"서치 시작: 쿼리 대상 폴더 {m_arrLogFolder.Length}개 (날짜 폴더 *_yyyyMMdd_HH.log, 기본 {m_nSearchDefault:N0}건·최대 {m_nSearchLimitSec}초)");

			listLoop.Add(WatchLoop(kStop));	// 덤프 (준비 끝: m_kDumpReady)

			await m_kDumpReady.Task.WaitAsync(kStop);

			HTLog.Write(INFO_, $"파일 시작: aLogFolder {m_arrLogRoot.Length}개 (하위 폴더 보기·다운)");

			listLoop.Add(FlushLoop(kStop));
			listLoop.Add(ConnectLoop(kStop));	// 서버 접속: 준비 다 끝난 뒤
		}
		catch (OperationCanceledException) when (kStop.IsCancellationRequested)	// 준비 중 종료 요청
		{
		}

		await Task.WhenAll(listLoop);

		HTLog.Write(INFO_, "클라 종료");
	}

	private async Task WatchLoop(CancellationToken kStop)	// 덤프 감시: aLogFolder 하위 *.dmp (폴더 생김·삭제·감시 오류: 다시 생성)
	{
		while (!kStop.IsCancellationRequested)
		{
			List<FileSystemWatcher>	listWatcher	= new List<FileSystemWatcher>();
			string					sExist		= GetRootExist();
			bool					bRetry		= false;							// 감시 열기 실패 (대기 후 재시도)

			m_bWatchError = false;

			try
			{
				foreach (string sRoot in m_arrLogRoot)
				{
					if (!Directory.Exists(sRoot))	// 없음: 생기면 다시 생성
					{
						HTLog.Write(WARN_, $"덤프 폴더 없음: {sRoot} (생기면 감시)");

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

					HTLog.Write(INFO_, $"덤프 시작: {sRoot} (하위 {m_nLogScanDepth}단계, {m_sWatchFilter})");
				}

				m_kDumpReady.TrySetResult();

				while (!kStop.IsCancellationRequested && !m_bWatchError && GetRootExist() == sExist)
				{
					await Delay(m_nWatchCheckMs, kStop);
				}
			}
			catch (Exception kException)	// 감시 열기 실패 (폴더 삭제·권한·네트워크): 대기 후 재생성
			{
				HTLog.Write(WARN_, $"덤프 오류: {kException.Message} ({m_nPingSec}초 후 재시도)");

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

	private string GetRootExist()	// aLogFolder 있음 상태 (순서대로 1·0, 덤프 감시 다시 생성 판단)
	{
		return string.Concat(m_arrLogRoot.Select(sRoot => Directory.Exists(sRoot) ? '1' : '0'));
	}

	private void OnCreated(object kSender, FileSystemEventArgs kArgs)	// 생성: 즉시 전송
	{
		if (IsInDepth(kArgs.FullPath))
		{
			Send("Created", kArgs.FullPath);
		}
	}

	private void OnChanged(object kSender, FileSystemEventArgs kArgs)	// 변경: 경로만 보관 → FlushLoop 묶음 전송
	{
		if (IsInDepth(kArgs.FullPath))
		{
			m_hmapChanged[kArgs.FullPath] = 0;
		}
	}

	private void OnDeleted(object kSender, FileSystemEventArgs kArgs)	// 삭제: 즉시 전송
	{
		m_hmapChanged.TryRemove(kArgs.FullPath, out _);

		if (IsInDepth(kArgs.FullPath))
		{
			Send("Deleted", kArgs.FullPath);
		}
	}

	private void OnRenamed(object kSender, RenamedEventArgs kArgs)	// 이름 변경: 즉시 전송 (이전·새 경로 중 하나라도 깊이 안)
	{
		m_hmapChanged.TryRemove(kArgs.OldFullPath, out _);

		if (IsInDepth(kArgs.FullPath) || IsInDepth(kArgs.OldFullPath))
		{
			Send("Renamed", kArgs.FullPath, kArgs.OldFullPath);
		}
	}

	private void OnError(object kSender, ErrorEventArgs kArgs)	// 감시 오류 (버퍼 초과 등): 즉시 전송, 감시 다시 생성
	{
		m_bWatchError = true;

		HTLog.Write(WARN_, $"덤프 오류: {kArgs.GetException().Message} (다시 생성)");

		Send("Error", (kSender as FileSystemWatcher)?.Path ?? "", "", kArgs.GetException().Message);
	}

	private bool IsInDepth(string sPath)	// aLogFolder 하위 m_nLogScanDepth 단계 안 (덤프 목록과 같은 기준)
	{
		foreach (string sRoot in m_arrLogRoot)
		{
			string sPrefix = GetRootPrefix(sRoot);

			if (sPath.Length > sPrefix.Length && sPath.StartsWith(sPrefix, StringComparison.OrdinalIgnoreCase) && sPath.AsSpan(sPrefix.Length).Count(Path.DirectorySeparatorChar) <= m_nLogScanDepth)
			{
				return true;
			}
		}

		return false;
	}

	private static string GetRootPrefix(string sRoot)	// aLogFolder 끝 구분자 포함 (하위 경로 비교, 드라이브 루트: 그대로)
	{
		return Path.EndsInDirectorySeparator(sRoot) ? sRoot : sRoot + Path.DirectorySeparatorChar;
	}

	private async Task FlushLoop(CancellationToken kStop)	// Changed 경로 묶음 전송 (주기마다 경로당 1건)
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

	private void Send(string sEvent, string sPath, string sOldPath = "", string sMessage = "")	// 파일 이벤트 → 전송 대기 (끊긴 동안에도 보관)
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

		m_queSend.Writer.TryWrite(sLine);
	}

	private async Task LogWatchLoop(CancellationToken kStop)	// 로그 감시: m_nLogWatchMs 마다 대상 폴더별 감시 파일 새 줄 검사 → aLogType 줄 전송 (aLogType 없음: 대상 탐색만)
	{
		bool bSend = m_arrLogType.Length > 0;

		HTLog.Write(INFO_, bSend ? $"쿼리 정보: aLogFolder {m_arrLogRoot.Length}개, aLogType {string.Join("·", m_arrLogType)}" : $"쿼리 안 함: aLogType 없음 (aLogFolder {m_arrLogRoot.Length}개, 서치 대상 탐색만)");

		ScanLogFolder();

		if (m_listLogWatch.Count == 0)
		{
			HTLog.Write(INFO_, $"쿼리 대상 없음: aLogFolder 자신·하위 {m_nLogScanDepth}단계 폴더 중 날짜 폴더(yyyyMMdd) 가진 폴더 없음 ({m_nLogScanMs / 1000}초마다 다시 탐색)");
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

				LineWriter? kWriter = m_kLogWriter;	// 접속 안 됨·Pause: 이번 주기 쉼 (위치 유지)

				if (bSend && kWriter != null && !m_bLogPause)
				{
					foreach (LogWatch kWatch in m_listLogWatch)
					{
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

								HTLog.Write(WARN_, $"쿼리 오류: {kWatch.m_sPath ?? kWatch.m_sFolder} ({kException.Message})");
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
			}
		}
	}

	private void ScanLogFolder()	// 대상 폴더 탐색: aLogFolder 자신·하위 중 바로 아래 날짜 폴더 가진 폴더 → 감시 추가 (기존·없어진 폴더 유지, 새 폴더: 최신 파일 끝부터)
	{
		try
		{
			EnumerationOptions kDirOption	= new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };																											// 바로 아래만 (날짜 폴더 확인)
			EnumerationOptions kScanOption	= new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint, RecurseSubdirectories = true, MaxRecursionDepth = m_nLogScanDepth };	// 하위 폴더 (깊이 제한)

			foreach (string sRoot in m_arrLogRoot)
			{
				if (!Directory.Exists(sRoot))
				{
					continue;
				}

				FileSystemEnumerable<string> kSubDir = new FileSystemEnumerable<string>(sRoot, (ref FileSystemEntry kEntry) => kEntry.ToFullPath(), kScanOption)	// 하위 폴더 (날짜 폴더 자신·그 안은 제외)
				{
					ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => kEntry.IsDirectory && !TryGetDate(kEntry.FileName, out _),
					ShouldRecursePredicate	= (ref FileSystemEntry kEntry) => !TryGetDate(kEntry.FileName, out _),
				};

				foreach (string sFolder in kSubDir.Prepend(sRoot))
				{
					if (!HasDateDir(sFolder, kDirOption) || m_listLogWatch.Any(kWatch => string.Equals(kWatch.m_sFolder, sFolder, StringComparison.OrdinalIgnoreCase)))
					{
						continue;
					}

					m_listLogWatch.Add(new LogWatch(sFolder));

					HTLog.Write(INFO_, $"쿼리 대상: {sFolder}");
				}
			}

			m_sLogScanError = "";
		}
		catch (Exception kException)	// 권한 없음·경로 오류 (같은 오류 1회만 출력)
		{
			if (m_sLogScanError != kException.Message)
			{
				m_sLogScanError = kException.Message;

				HTLog.Write(WARN_, $"쿼리 대상 탐색 오류: {kException.Message}");
			}
		}

		if (m_arrLogFolder.Length != m_listLogWatch.Count)	// 추가됨: 서치 대상 새 배열 (추가만 → 개수로 비교)
		{
			m_arrLogFolder = m_listLogWatch.Select(kWatch => kWatch.m_sFolder).ToArray();
		}
	}

	private static bool HasDateDir(string sFolder, EnumerationOptions kOption)	// 바로 아래 날짜 폴더(yyyyMMdd) 있음 (첫 개 찾으면 끝)
	{
		FileSystemEnumerable<byte> kDateDir = new FileSystemEnumerable<byte>(sFolder, (ref FileSystemEntry kEntry) => 0, kOption)
		{
			ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => kEntry.IsDirectory && TryGetDate(kEntry.FileName, out _),
		};

		return kDateDir.Any();
	}

	private static string GetFullFolder(string sFolder)	// 폴더 전체 경로 (끝 구분자 없음, 형식 오류: 설정 그대로)
	{
		try
		{
			return Path.TrimEndingDirectorySeparator(Path.GetFullPath(sFolder));
		}
		catch (Exception)
		{
			return Path.TrimEndingDirectorySeparator(sFolder);
		}
	}

	private static string GetLogStateFile()	// 로그 감시 상태 파일: 기준 폴더 LMClient.data
	{
		return Path.Combine(GetBaseDir(), $"{GetProjectName()}.data");
	}

	private void LoadLogState()	// 로그 감시 상태 읽기: LMClient.data "전체 경로\t줄 번호" → 대상 폴더마다 본 줄 다음부터 (대상 밖·형식 오류·같은 폴더 중복 줄: 파일에서 뺌)
	{
		string sFile = GetLogStateFile();

		string sOld = Path.ChangeExtension(sFile, ".txt");	// 예전 이름 (LMClient.txt)

		if (!File.Exists(sFile) && File.Exists(sOld))	// 예전 이름만 있음: 새 이름으로
		{
			try
			{
				File.Move(sOld, sFile);

				HTLog.Write(INFO_, $"쿼리 상태 이름 변경: {sOld} → {sFile}");
			}
			catch (Exception kException)	// 권한 없음 등: 새 파일로 시작
			{
				HTLog.Write(WARN_, $"쿼리 상태 이름 변경 실패: {sOld} ({kException.Message})");
			}
		}

		if (!File.Exists(sFile))	// 없음: 빈 파일 생성 (이번 실행: 최신 파일 끝부터)
		{
			try
			{
				File.WriteAllText(sFile, "", m_kUtf8);

				HTLog.Write(INFO_, $"쿼리 상태 생성: {sFile}");
			}
			catch (Exception kException)	// 권한 없음 등: 보낸 뒤 저장 때 다시
			{
				HTLog.Write(WARN_, $"쿼리 상태 생성 실패: {sFile} ({kException.Message})");
			}

			return;
		}

		try
		{
			string[]		arrState	= File.ReadAllLines(sFile, m_kUtf8);
			List<string>	listKeep	= new List<string>();					// 남길 줄

			foreach (string sState in arrState)
			{
				string[]	arrPart	= sState.Split('\t');	// 전체 경로, 줄 번호
				long		nLine	= 0;					// 본 줄까지

				if (arrPart.Length != 2 || !long.TryParse(arrPart[1], NumberStyles.None, CultureInfo.InvariantCulture, out nLine) || !TryGetLogDateHour(Path.GetFileName(arrPart[0].AsSpan()), out _))	// 형식 오류·감시 파일 이름 아님
				{
					continue;
				}

				string?		sFolder	= Path.GetDirectoryName(Path.GetDirectoryName(arrPart[0]));	// 대상 폴더 (대상\날짜\파일, 전체 경로 비교)
				LogWatch?	kWatch	= sFolder == null ? null : m_listLogWatch.FirstOrDefault(kItem => kItem.m_sPath == null && string.Equals(kItem.m_sFolder, sFolder, StringComparison.OrdinalIgnoreCase));

				if (kWatch == null)
				{
					continue;
				}

				kWatch.m_sPath		= arrPart[0];
				kWatch.m_nOffset	= -1;	// 열 때 본 줄 다음 위치
				kWatch.m_nLine		= nLine;

				listKeep.Add(sState);

				HTLog.Write(INFO_, $"쿼리 시작: {arrPart[0]} ({nLine}줄까지 봄{(File.Exists(arrPart[0]) ? "" : ", 파일 없음 → 다음 시간 파일부터")})");
			}

			if (listKeep.Count < arrState.Length)	// 뺀 줄 있음: txt 다시 씀
			{
				File.WriteAllText(sFile + ".tmp", string.Concat(listKeep.Select(sState => sState + "\r\n")), m_kUtf8);	// 임시 파일 후 교체 (쓰는 중 종료 대비)
				File.Move(sFile + ".tmp", sFile, true);

				HTLog.Write(INFO_, $"쿼리 상태 정리: {sFile} (대상 폴더에 없는 줄 등 {arrState.Length - listKeep.Count}줄 뺌)");
			}
		}
		catch (Exception kException)	// 권한 없음·사용 중
		{
			HTLog.Write(WARN_, $"쿼리 상태 읽기 실패: {sFile} ({kException.Message})");
		}
	}

	private void SaveLogState()	// 로그 감시 상태 저장: LMClient.data (대상 폴더 순서, 변경 시만, 실패: 다음 주기 재시도)
	{
		string sState = string.Concat(m_listLogWatch.Where(kWatch => !string.IsNullOrEmpty(kWatch.m_sPath) && kWatch.m_nLine >= 0).Select(kWatch => $"{kWatch.m_sPath}\t{kWatch.m_nLine}\r\n"));

		if (sState == m_sLogState)
		{
			return;
		}

		string sFile = GetLogStateFile();

		try
		{
			File.WriteAllText(sFile + ".tmp", sState, m_kUtf8);	// 임시 파일 후 교체 (쓰는 중 종료: 앞 내용 유지)
			File.Move(sFile + ".tmp", sFile, true);

			m_sLogState = sState;	// 저장 성공 시만
		}
		catch (Exception kException)	// 권한 없음·사용 중
		{
			HTLog.Write(WARN_, $"쿼리 상태 저장 실패: {sFile} ({kException.Message})");
		}
	}

	private async Task<(int nSent, int nCheck)> CheckLog(LogWatch kWatch, LineWriter kWriter, int nSendMax, int nCheckMax, CancellationToken kStop)	// 감시 파일 새 줄 검사·전송 (다음 시간 파일: 지금 파일 끝까지 본 뒤 이동)
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

					HTLog.Write(WARN_, $"쿼리 이전 파일 열기 실패: {kWatch.m_sPath} ({kException.Message}, 넘어감)");
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

			HTLog.Write(INFO_, $"쿼리: {sPath}");
		}
		else if (kWatch.m_sPath == null)	// 클라 시작에 최신 파일 없음: 이후 생기는 파일은 처음부터
		{
			kWatch.m_sPath = "";

			HTLog.Write(INFO_, $"쿼리: {kWatch.m_sFolder} (최신 파일 없음)");
		}

		if (string.IsNullOrEmpty(kWatch.m_sPath) || nSent >= nSendMax || nCheck >= nCheckMax)	// 감시 파일 없음·다 씀
		{
			return (nSent, nCheck);
		}

		OpenLog(kWatch);

		(int nNewSent, int nNewCheck, _) = await SendLog(kWatch, false, kWriter, nSendMax - nSent, nCheckMax - nCheck, kStop);

		return (nSent + nNewSent, nCheck + nNewCheck);
	}

	private async Task<(int nSent, int nCheck, bool bMore)> SendLog(LogWatch kWatch, bool bAll, LineWriter kWriter, int nSendMax, int nCheckMax, CancellationToken kStop)	// ReadLog 줄 전송 → 보낸 뒤에만 위치 반영 (전송 실패: 예외)
	{
		(List<string> listLine, List<long> listNo, long nOffset, long nLine, int nCheck, bool bMore) = ReadLog(kWatch, bAll, nSendMax, nCheckMax);

		if (listLine.Count > 0)
		{
			string sLine = JsonSerializer.Serialize(new
			{
				sType	= "Log",
				sPath	= kWatch.m_sPath,
				sFolder	= kWatch.m_sFolder,	// 서버 쿼리 Folder 필드
				sZone	= HTConfig.GetString("Base:sZone"),	// 서버 쿼리 Zone 필드
				arrLine	= listLine,
				arrNo	= listNo,	// 줄 번호 (arrLine 과 같은 순서, 파일 첫 줄 1)
				arrField	= m_arrField.Length == 0 ? null : listLine.Select(GetLineField).ToList(),	// 줄 필드 (arrLine 과 같은 순서)
				sTime	= GetNowText(),
			}, m_kJsonOption);

			await kWriter.WriteAsync(sLine, kStop);
		}

		kWatch.m_nOffset	= nOffset;
		kWatch.m_nLine		= nLine;

		return (listLine.Count, nCheck, bMore);
	}

	private Dictionary<string, string> GetLineField(string sLine)	// 한 줄 aField 값 {키: 값} (정규식 첫 () 값, 없음: 맞은 부분, 빈 값·시간 초과: 제외)
	{
		Dictionary<string, string> hmapField = new Dictionary<string, string>();	// 값 있는 키만

		foreach (LogField kField in m_arrField)
		{
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

	private (List<string> listLine, List<long> listNo, long nOffset, long nLine, int nCheck, bool bMore) ReadLog(LogWatch kWatch, bool bAll, int nSendMax, int nCheckMax)	// 본 줄 다음 끝난 줄 검사 (시간이 지금 초 미만인 줄까지, bAll: 파일 끝까지, 최대 줄 수 차면 멈춤, 위치 반영은 SendLog)
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

		byte[] arrBytes = ArrayPool<byte>.Shared.Rent(m_nBlockBytes);
		char[] arrChars = ArrayPool<char>.Shared.Rent(m_nBlockBytes);	// 글자 수 ≤ 바이트 수

		try
		{
			while (!bStop && nOffset < nLength)
			{
				int nRead = RandomAccess.Read(kHandle, arrBytes.AsSpan(0, (int)Math.Min(m_nBlockBytes, nLength - nOffset)), nOffset);

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

	private void AddLogLine(ReadOnlySpan<byte> kLine, char[] arrChars, List<string> listLine, List<long> listNo, long nNo)	// 한 줄 (바이트) → aLogType 줄이면 listLine·listNo 에 추가
	{
		ReadOnlySpan<char> kText = arrChars.AsSpan(0, Decode(kLine, arrChars));	// 글자 (글자 수 ≤ 바이트 수)

		if (IsLogType(kText))
		{
			listLine.Add(ToText(kText, m_nLogTextMax));
			listNo.Add(nNo);
		}
	}

	private bool IsLogType(ReadOnlySpan<char> kLine)	// 두 번째 [] 안에 aLogType 포함 (대소문자 구분, 예: NOTI → [NOTI_])
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

	private static bool TryGetLogTime(ReadOnlySpan<byte> kLine, out long nTime)	// 줄 앞 시간 ([yyyy/MM/dd HH:mm:ss.fff] → yyyyMMddHHmmss, 형식 아님: false)
	{
		nTime = 0;

		if (kLine.Length < 20 || kLine[0] != '[' || kLine[5] != '/' || kLine[8] != '/' || kLine[11] != ' ' || kLine[14] != ':' || kLine[17] != ':')
		{
			return false;
		}

		for (int nIndex = 1; nIndex < 20; ++nIndex)	// yyyy MM dd HH mm ss 숫자만 (구분자 건너뜀)
		{
			if (nIndex == 5 || nIndex == 8 || nIndex == 11 || nIndex == 14 || nIndex == 17)
			{
				continue;
			}

			int nDigit = kLine[nIndex] - '0';

			if ((uint)nDigit > 9)
			{
				return false;
			}

			nTime = nTime * 10 + nDigit;
		}

		return true;
	}

	private static long GetLastLineEnd(SafeFileHandle kHandle)	// 끝난 줄 끝 바이트 (마지막 '\n' 다음, 끝 블록 안에서 찾음)
	{
		long	nLength		= RandomAccess.GetLength(kHandle);
		int		nCount		= (int)Math.Min(nLength, m_nBlockBytes);			// 끝에서 읽을 바이트
		long	nStart		= nLength - nCount;
		byte[]	arrBytes	= ArrayPool<byte>.Shared.Rent(m_nBlockBytes);

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
		byte[]		arrBytes	= ArrayPool<byte>.Shared.Rent(m_nBlockBytes);
		long		nCount		= 0;											// 지난 '\n' 수
		long		nPos		= 0;

		try
		{
			while (true)
			{
				int nRead = RandomAccess.Read(kHandle, arrBytes.AsSpan(0, m_nBlockBytes), nPos);

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
		byte[]		arrBytes	= ArrayPool<byte>.Shared.Rent(m_nBlockBytes);
		long		nCount		= 0;
		long		nPos		= 0;

		try
		{
			while (nPos < nEnd)
			{
				int nRead = RandomAccess.Read(kHandle, arrBytes.AsSpan(0, (int)Math.Min(m_nBlockBytes, nEnd - nPos)), nPos);

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

		FileSystemEnumerable<(long nDate, string sPath)> kDateDir = new FileSystemEnumerable<(long nDate, string sPath)>(sFolder, (ref FileSystemEntry kEntry) => (TryGetDate(kEntry.FileName, out long nDate) ? nDate : 0, kEntry.ToFullPath()), kOption)
		{
			ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => kEntry.IsDirectory && TryGetDate(kEntry.FileName, out _),
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

		FileSystemEnumerable<(long nDateHour, long nTime, string sPath)> kFile = new FileSystemEnumerable<(long nDateHour, long nTime, string sPath)>(sDateDir, (ref FileSystemEntry kEntry) => (TryGetLogDateHour(kEntry.FileName, out long nDateHour) ? nDateHour : 0, kEntry.LastWriteTimeUtc.UtcTicks, kEntry.ToFullPath()), kOption)
		{
			ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => !kEntry.IsDirectory && TryGetLogDateHour(kEntry.FileName, out _),
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
		if (!TryGetLogDateHour(Path.GetFileName(kWatch.m_sPath.AsSpan()), out long nCurrent))
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
		if (!TryGetLogDateHour(Path.GetFileName(sPath.AsSpan()), out long nCurrent) || !Directory.Exists(sFolder))
		{
			return "";
		}

		EnumerationOptions				kOption	= new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
		(long nDateHour, string sPath)	kNext	= (long.MaxValue, "");	// 다음 파일 (파일명 날짜·시간, 경로)

		FileSystemEnumerable<string> kDateDir = new FileSystemEnumerable<string>(sFolder, (ref FileSystemEntry kEntry) => kEntry.ToFullPath(), kOption)	// sPath 날짜 이후 날짜 폴더
		{
			ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => kEntry.IsDirectory && TryGetDate(kEntry.FileName, out long nDate) && nDate >= nCurrent / 100,
		};

		foreach (string sDateDir in kDateDir)
		{
			FileSystemEnumerable<(long nDateHour, string sPath)> kFile = new FileSystemEnumerable<(long nDateHour, string sPath)>(sDateDir, (ref FileSystemEntry kEntry) => (TryGetLogDateHour(kEntry.FileName, out long nDateHour) ? nDateHour : 0, kEntry.ToFullPath()), kOption)
			{
				ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => !kEntry.IsDirectory && TryGetLogDateHour(kEntry.FileName, out long nDateHour) && nDateHour > nCurrent,
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

	private async Task ConnectLoop(CancellationToken kStop)	// 접속 → 전송 (끊김: 대기 후 재접속, 실패마다 대기 2배)
	{
		int nRetrySec	= m_nRetryMinSec;	// 재접속 대기 (초)
		int nFail		= 0;				// 연속 접속 실패 수 (로그 줄이기)

		while (!kStop.IsCancellationRequested)
		{
			bool bConnected = false;	// 이번 시도에서 접속 성공

			try
			{
				using TcpClient kClient = new TcpClient();

				await kClient.ConnectAsync(m_sIP, m_nPort, kStop);

				SetKeepAlive(kClient.Client);	// 서버가 FIN 없이 사라진 경우 감지

				HTLog.Write(INFO_, $"서버 접속: {m_sIP}:{m_nPort}{(nFail > 0 ? $" (재접속 시도 {nFail}회 후)" : "")}");

				bConnected	= true;
				nRetrySec	= m_nRetryMinSec;
				nFail		= 0;

				await SendLoop(kClient, kStop);
			}
			catch (OperationCanceledException) when (kStop.IsCancellationRequested)
			{
				break;
			}
			catch (Exception kException)	// 접속 실패·끊김 (연속 실패: 처음 1회 + X회마다 출력)
			{
				if (bConnected || ++nFail == 1 || nFail % m_nRetryLogCount == 0)
				{
					HTLog.Write(WARN_, $"{(bConnected ? "서버 연결 끊김" : "서버 접속 실패")}: {m_sIP}:{m_nPort} ({kException.Message}, {nRetrySec}초 후 재접속{(nFail > 1 ? $", 연속 실패 {nFail}회" : "")})");
				}
			}

			await Delay(nRetrySec * 1000, kStop);

			nRetrySec = Math.Min(nRetrySec * 2, m_nRetryMaxSec);
		}
	}

	private async Task SendLoop(TcpClient kClient, CancellationToken kStop)	// Hello 후 대기 줄 묶음 전송, 무전송 시 Ping (서버 종료 감지: IOException)
	{
		using CancellationTokenSource kLink = CancellationTokenSource.CreateLinkedTokenSource(kStop);	// 종료 요청 또는 서버 종료 감지 시 취소

		NetworkStream	kStream		= kClient.GetStream();
		LineWriter		kWriter		= new LineWriter(kStream);				// 줄 전송 (이벤트·요청 결과 동시 전송 직렬화)
		Task			kReadTask	= ReadLoop(kStream, kWriter, kLink);	// 서버 수신 (0바이트 = 서버 종료)

		try
		{
			string sHello = JsonSerializer.Serialize(new
			{
				sType		= "Hello",
				sMachine	= Environment.MachineName,
				sDir		= string.Join("|", m_arrLogRoot),	// aLogFolder 목록 ("|" 구분)
				arrField	= m_arrField.Select(kField => kField.sKey).ToArray(),	// aField 키 (설정 순서: 서버 필드 목록 순서)
				nPid		= Environment.ProcessId,
				sTime		= GetNowText(),
			});

			await kWriter.WriteAsync(sHello, kLink.Token);

			m_bLogPause	= false;	// 새 접속: 멈춤 해제 (밀림 중이면 서버가 Pause 재전송)
			m_kLogWriter	= kWriter;	// 로그 감시 전송 시작 (Hello 뒤)

			List<string> listBatch = new List<string>(m_nBatchMax);	// 한 번에 보낼 줄 (재사용)

			while (true)
			{
				listBatch.Clear();

				while (listBatch.Count < m_nBatchMax && m_queSend.Reader.TryRead(out string? sQueued))	// 쌓인 줄은 대기 없이 묶음
				{
					listBatch.Add(sQueued);
				}

				if (listBatch.Count == 0)	// 쌓인 줄 없음: 새 줄 또는 Ping 시간까지 대기
				{
					using CancellationTokenSource kPing = CancellationTokenSource.CreateLinkedTokenSource(kLink.Token);

					kPing.CancelAfter(m_nPingSec * 1000);

					try
					{
						listBatch.Add(await m_queSend.Reader.ReadAsync(kPing.Token));
					}
					catch (OperationCanceledException) when (!kLink.IsCancellationRequested)	// 무전송 시간 초과: Ping
					{
						listBatch.Add(JsonSerializer.Serialize(new { sType = "Ping", sTime = GetNowText() }));
					}
				}

				await kWriter.WriteLinesAsync(listBatch, kLink.Token);
			}
		}
		catch (OperationCanceledException) when (!kStop.IsCancellationRequested)	// 서버 종료 감지 (ReadLoop)
		{
			throw new IOException("서버가 연결 종료");
		}
		finally
		{
			m_kLogWriter	= null;		// 로그 감시 전송 멈춤 (끊김)
			m_bLogPause		= false;

			kLink.Cancel();

			await kReadTask;
		}
	}

	private async Task ReadLoop(NetworkStream kStream, LineWriter kWriter, CancellationTokenSource kLink)	// 서버 수신: 요청마다 별도 처리, 서버 종료·줄 길이 초과·오류 시 kLink 취소
	{
		CancellationToken kToken = kLink.Token;	// 연결 단위 취소 (요청 처리에 전달)

		try
		{
			LineReader kReader = new LineReader(kStream, m_nRequestLineMax);

			while (await kReader.ReadLineAsync(kToken) is string sLine)
			{
				_ = Task.Run(() => OnRequest(sLine, kWriter, kToken));	// 요청 처리 중에도 수신 계속
			}
		}
		catch (Exception)	// 끊김·취소
		{
		}

		kLink.Cancel();
	}

	private async Task OnRequest(string sLine, LineWriter kWriter, CancellationToken kStop)	// 서버 요청 처리 → 결과 전송 (모르는 종류: 무시)
	{
		try
		{
			JsonElement kRoot;

			using (JsonDocument kDoc = JsonDocument.Parse(sLine))
			{
				kRoot = kDoc.RootElement.Clone();
			}

			string	sType	= GetText(kRoot, "sType");
			long	nReqId	= GetLong(kRoot, "nReqId");	// 요청 번호 (결과에 그대로)
			object	kResult;

			if (sType == "Pause" || sType == "Resume")	// 서버 적재 밀림: 로그 감시 멈춤 (위치 유지) / 재개
			{
				m_bLogPause = sType == "Pause";

				HTLog.Write(NOTI_, m_bLogPause ? "쿼리 멈춤: 서버 적재 밀림 (Pause)" : "쿼리 재개: 서버 적재 안정 (Resume)");

				return;
			}

			if (sType == "Stop")	// 검색 중지 (이미 끝났으면 무시)
			{
				if (m_hmapJob.TryGetValue(nReqId, out SearchJob? kStopJob))
				{
					kStopJob.m_kCancel.Cancel();
				}

				return;
			}

			if (sType == "Search")
			{
				SearchJob kJob = new SearchJob();

				m_hmapJob[nReqId] = kJob;

				using CancellationTokenSource	kProgressStop	= CancellationTokenSource.CreateLinkedTokenSource(kStop);	// 진행 알림 끝 (검색 끝·연결 끊김)
				Task							kProgress		= SendProgress(nReqId, kJob, kWriter, kProgressStop.Token, kStop);

				try
				{
					kResult = await Search(nReqId, GetLong(kRoot, "nStart"), GetLong(kRoot, "nEnd"), GetText(kRoot, "sKey"), (int)GetLong(kRoot, "nMax"), kJob, kStop);
				}
				finally
				{
					kProgressStop.Cancel();

					await kProgress;	// 진행 알림이 결과보다 뒤에 가지 않게

					m_hmapJob.TryRemove(new KeyValuePair<long, SearchJob>(nReqId, kJob));	// 내 것만 (같은 번호 새 검색 유지), m_kCancel 은 Dispose 안 함 (늦은 Stop 안전)
				}
			}
			else if (sType == "View")
			{
				if (!await m_kViewLock.WaitAsync(TimeSpan.FromSeconds(m_nViewWaitSec), kStop))	// 보기 동시 처리 초과: 거절
				{
					kResult = new { sType = "ViewResult", nReqId, sPath = GetText(kRoot, "sPath"), nStart = 0L, nTotal = 0L, sError = "보기 요청이 많음 (잠시 후 다시)", arrText = Array.Empty<string>() };
				}
				else
				{
					try
					{
						kResult = View(nReqId, GetText(kRoot, "sPath"), GetLong(kRoot, "nStart"), (int)GetLong(kRoot, "nCount"));
					}
					finally
					{
						m_kViewLock.Release();
					}
				}
			}
			else if (sType == "Dump" || sType == "DumpRead" || sType == "DumpDelete" || sType == "Files" || sType == "FileRead")	// 덤프·파일 요청 (보기와 같은 동시 처리 제한)
			{
				if (!await m_kViewLock.WaitAsync(TimeSpan.FromSeconds(m_nViewWaitSec), kStop))	// 동시 처리 초과: 거절
				{
					kResult = new { sType = sType + "Result", nReqId, sPath = GetText(kRoot, "sPath"), sError = "요청이 많음 (잠시 후 다시)" };	// 문구: 서버 m_sClientBusy 와 같음 (서버 조각 재요청 판단)
				}
				else
				{
					try
					{
						kResult = sType switch
						{
							"Dump"		=> DumpList(nReqId),
							"DumpRead"	=> DumpRead(nReqId, GetText(kRoot, "sPath"), GetLong(kRoot, "nOffset"), (int)GetLong(kRoot, "nCount")),
							"Files"		=> FileList(nReqId, GetText(kRoot, "sPath")),
							"FileRead"	=> FileRead(nReqId, GetText(kRoot, "sPath"), GetLong(kRoot, "nOffset"), (int)GetLong(kRoot, "nCount")),
							_			=> DumpDelete(nReqId, GetText(kRoot, "sPath")),
						};
					}
					finally
					{
						m_kViewLock.Release();
					}
				}
			}
			else
			{
				return;
			}

			try
			{
				await kWriter.WriteJsonAsync(kResult, m_kJsonOption, kStop);	// 결과를 큰 문자열로 만들지 않고 바로 전송
			}
			finally
			{
				(kResult as IDisposable)?.Dispose();	// 조각 읽기 결과: 빌린 버퍼 반환 (ChunkResult)
			}
		}
		catch (Exception kException) when (!kStop.IsCancellationRequested)	// 형식 오류·전송 실패
		{
			HTLog.Write(ERROR, $"요청 처리 오류: {kException.Message}");
		}
		catch (Exception)	// 연결 종료
		{
		}
	}

	private static async Task SendProgress(long nReqId, SearchJob kJob, LineWriter kWriter, CancellationToken kEnd, CancellationToken kStop)	// 검색 진행 알림 (kEnd: 대기만 끊음, 쓰는 중엔 안 끊음 → 줄 깨짐 방지)
	{
		try
		{
			while (true)
			{
				await Task.Delay(m_nProgressMs, kEnd);

				await kWriter.WriteJsonAsync(new { sType = "SearchProgress", nReqId, nFileTotal = kJob.m_nFileTotal, nFileDone = kJob.m_nFileDone, nFound = kJob.m_nFound, nElapsedMs = (long)Stopwatch.GetElapsedTime(kJob.m_nBegin).TotalMilliseconds }, m_kJsonOption, kStop);
			}
		}
		catch (Exception)	// 검색 끝·연결 끊김
		{
		}
	}

	private async Task<object> Search(long nReqId, long nStart, long nEnd, string sKey, int nMax, SearchJob kJob, CancellationToken kStop)	// 로그 검색: 대상 파일(시간·경로 순)에서 검색어 포함 줄 (kJob: 진행 기록·중지 요청)
	{
		long				nBegin		= kJob.m_nBegin;			// 검색 시작 타임스탬프
		List<SearchLine>	listLine	= new List<SearchLine>();
		int					nFileCount	= 0;						// 검색한 파일 수
		bool				bTruncated	= false;					// 최대 건수·시간 초과·중지로 중단
		bool				bStopped	= false;					// 중지 요청으로 중단
		string				sError		= "";

		nMax = nMax <= 0 ? m_nSearchDefault : Math.Min(nMax, m_nSearchMax);	// 없음: 기본값, 그 외: 상한까지

		using CancellationTokenSource kCancel = CancellationTokenSource.CreateLinkedTokenSource(kStop, kJob.m_kCancel.Token);	// 연결 끊김·중지 요청

		try
		{
			if (!await m_kSearchLock.WaitAsync(TimeSpan.FromSeconds(m_nSearchWaitSec), kCancel.Token))	// 검색 1건씩, 대기 초과: 거절 (밀린 검색 방지)
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
			SearchNode? kCond = SearchNode.Parse(sKey);	// 검색 조건 (빈 검색어: null)

			if (kCond == null || nStart > nEnd)
			{
				sError = "검색 조건 오류";
			}
			else
			{
				SearchValues<string>	kKey		= SearchValues.Create(kCond.GetFirst(), StringComparison.OrdinalIgnoreCase);		// 블록에서 먼저 찾을 검색어 (맞는 줄에 꼭 있는 것, 벡터 검색)
				SearchNode?				kCheck		= kCond.m_eOp == SEARCH_OP.SEARCH_OP_TERM ? null : kCond;						// 줄마다 확인할 조건 (연산자 없음: null → 찾은 줄 바로 추가)
				long					nDeadline	= nBegin + m_nSearchLimitSec * Stopwatch.Frequency;								// 검색 마감 타임스탬프

				List<(long nDateHour, string sPath)>	listTarget	= GetTargetFile(nStart, nEnd);
				Throttle								kThrottle	= new Throttle();				// 파일 바뀌어도 이어서 셈

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
						sError		= $"검색 시간 초과 ({m_nSearchLimitSec}초, 그때까지 결과만)";

						break;
					}

					++nFileCount;

					try
					{
						SEARCH_STOP eStop = SearchFile(sPath, nDateHour, kKey, kCheck, listLine, nMax, nDeadline, kJob, kCancel.Token, kThrottle);

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
							sError		= $"검색 시간 초과 ({m_nSearchLimitSec}초, 그때까지 결과만)";

							break;
						}
					}
					catch (Exception kException)	// 파일 열기·읽기 실패: 다음 파일 계속
					{
						HTLog.Write(WARN_, $"서치 파일 오류: {sPath} ({kException.Message})");
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

		long nElapsedMs = (long)Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds;

		HTLog.Write(INFO_, $"서치: {nStart}~{nEnd} → {listLine.Count}건 / 파일 {nFileCount}개 ({nElapsedMs}ms){(bStopped ? " (중지)" : bTruncated ? " (일부)" : "")}");

		return new { sType = "SearchResult", nReqId, bTruncated, bStopped, nFileCount, nElapsedMs, sError, arrLine = listLine };
	}

	private static SEARCH_STOP SearchFile(string sPath, long nDateHour, SearchValues<string> kKey, SearchNode? kCheck, List<SearchLine> listLine, int nMax, long nDeadline, SearchJob kJob, CancellationToken kCancel, Throttle kThrottle)	// 파일 하나 검색: 블록에서 검색어 위치 찾고 맞는 줄만 문자열화 (kCheck: 줄 조건, null: 바로 추가, 줄 번호 = 앞 '\n' 수 + 1)
	{
		long		nLine	= 0;							// 지난 블록까지 '\n' 수
		SEARCH_STOP	eStop	= SEARCH_STOP.SEARCH_STOP_NONE;

		ForEachBlock(sPath, (ReadOnlySpan<char> kBlock) =>
		{
			int nCounted	= 0;	// '\n' 을 센 위치
			int nPos		= 0;

			while (nPos < kBlock.Length)
			{
				int nFind = kBlock.Slice(nPos).IndexOfAny(kKey);

				if (nFind < 0)
				{
					break;
				}

				nFind += nPos;

				int nLineStart	= kBlock.Slice(0, nFind).LastIndexOf('\n') + 1;
				int nLineEnd	= kBlock.Slice(nFind).IndexOf('\n');				// 줄 끝 ('\n' 위치, 없음: 블록 끝)

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

				string sText = ToText(kBlock.Slice(nLineStart, nLineEnd - nLineStart), m_nSearchTextMax);

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

	private object View(long nReqId, string sRelPath, long nStart, int nCount)	// 파일 보기: nStart 줄부터 nCount 줄 (nStart 0 이하: 마지막 nCount 줄), 보기 확장자만
	//   UTF-8·CP949: 바이트 '\n' 로 줄 위치 → 보낼 줄만 글자 변환 (CPU 절감), UTF-16: 처음부터 글자로 셈
	{
		List<string>	listText	= new List<string>();
		long			nTotal		= 0;					// 전체 줄 수
		long			nFirst		= Math.Max(1, nStart);	// 보낼 첫 줄 번호
		long			nSize		= 0;
		string			sTime		= "";					// 수정 시각 (KST)
		string			sError		= "";

		nCount = nCount <= 0 ? m_nViewCount : Math.Min(nCount, m_nViewCountMax);

		string? sPath = GetAllowedPath(sRelPath);	// 허용 경로 (그 외: null)

		if (sPath == null)
		{
			sError = "허용되지 않은 경로";
		}
		else
		{
			try
			{
				Throttle kThrottle = new Throttle();	// 줄 세기·읽기 이어서 셈

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

				FileInfo kInfo = new FileInfo(sPath);

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
		byte[]	arrBuffer	= ArrayPool<byte>.Shared.Rent(m_nBlockBytes);

		try
		{
			kStream.Position = nBase;

			while (true)
			{
				int nRead = kStream.Read(arrBuffer, 0, arrBuffer.Length);

				if (nRead == 0)
				{
					break;
				}

				ReadOnlySpan<byte>	kBlock	= arrBuffer.AsSpan(0, nRead);
				int					nCount	= kBlock.Count((byte)'\n');

				if (nOffset < 0 && nFindLine > 1 && nNewLine + nCount >= nFindLine - 1)	// 찾는 줄 시작이 이 블록에 있음
				{
					int nSkip = 0;

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

		long nTotal = nNewLine + (bOpen ? 1 : 0);	// 끝 줄 '\n' 없음: 1 더함

		return (nTotal, nFindLine > nTotal ? -1 : nOffset);	// 범위 밖 (예: 끝 '\n' 뒤): -1
	}

	private static void ReadLines(string sPath, long nOffset, int nCount, List<string> listText, Throttle kThrottle)	// nOffset (줄 시작 바이트) 부터 nCount 줄 → listText (아주 긴 줄: 앞 조각만)
	{
		bool bOpen = false;	// 앞 블록이 줄 중간에서 끝남 (이어지는 조각은 버림)

		ForEachBlock(sPath, (ReadOnlySpan<char> kBlock) =>
		{
			ReadOnlySpan<char> kRest = kBlock;

			while (kRest.Length > 0 && listText.Count < nCount)
			{
				int nIndex = kRest.IndexOf('\n');

				if (!bOpen)
				{
					listText.Add(ToText(nIndex < 0 ? kRest : kRest.Slice(0, nIndex), m_nViewTextMax));
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
		long nLine = 0;		// '\n' 수
		bool bOpen = false;	// 마지막 블록이 '\n' 으로 안 끝남

		ForEachBlock(sPath, (ReadOnlySpan<char> kBlock) =>
		{
			nLine	+= kBlock.Count('\n');
			bOpen	= kBlock[^1] != '\n';

			return true;
		}, kThrottle);

		return nLine + (bOpen ? 1 : 0);
	}

	private static long ReadRange(string sPath, long nFirst, int nCount, List<string> listText, Throttle kThrottle)	// nFirst 줄부터 nCount 줄을 listText 에 (범위 밖 줄은 세기만 함), 반환: 전체 줄 수
	{
		long nLast	= nFirst + nCount - 1;	// 읽을 마지막 줄 번호
		long nLine	= 0;					// 지난 '\n' 수
		long nAdded	= 0;					// 마지막으로 넣은 줄 번호 (아주 긴 줄이 블록에 나뉘면 앞 조각만)
		bool bOpen	= false;				// 마지막 블록이 '\n' 으로 안 끝남

		ForEachBlock(sPath, (ReadOnlySpan<char> kBlock) =>
		{
			int nNewLine = kBlock.Count('\n');

			if (nLine + nNewLine + 1 < nFirst || nLine + 1 > nLast)	// 블록 줄이 모두 범위 밖: 세기만
			{
				nLine	+= nNewLine;
				bOpen	= kBlock[^1] != '\n';

				return true;
			}

			ReadOnlySpan<char> kRest = kBlock;

			while (kRest.Length > 0)
			{
				int		nIndex	= kRest.IndexOf('\n');
				long	nNo		= nLine + 1;

				if (nNo >= nFirst && nNo <= nLast && nNo != nAdded)
				{
					listText.Add(ToText(nIndex < 0 ? kRest : kRest.Slice(0, nIndex), m_nViewTextMax));

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

	private List<(long nDateHour, string sPath)> GetTargetFile(long nStart, long nEnd)	// 검색 대상 파일: 쿼리 대상 폴더의 기간 안 날짜 폴더 *yyyyMMdd_HH.log (시간·경로 순)
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
				ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => kEntry.IsDirectory && TryGetDate(kEntry.FileName, out long nDate) && nDate >= nDateMin && nDate <= nDateMax,
			};

			foreach (string sDirPath in kDateDir)
			{
				FileSystemEnumerable<(long nDateHour, string sPath)> kFile = new FileSystemEnumerable<(long nDateHour, string sPath)>(sDirPath, (ref FileSystemEntry kEntry) => (TryGetLogDateHour(kEntry.FileName, out long nDateHour) ? nDateHour : 0, kEntry.ToFullPath()), kFileOption)
				{
					ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => !kEntry.IsDirectory && TryGetLogDateHour(kEntry.FileName, out long nDateHour) && nDateHour >= nStart && nDateHour <= nEnd,
				};

				listFile.AddRange(kFile);
			}
		}

		listFile.Sort((kLeft, kRight) => kLeft.nDateHour != kRight.nDateHour ? kLeft.nDateHour.CompareTo(kRight.nDateHour) : string.Compare(kLeft.sPath, kRight.sPath, StringComparison.OrdinalIgnoreCase));

		return listFile;
	}

	private string? GetAllowedPath(string sPath)	// 보기 허용 경로: aLogFolder 하위 + 보기 확장자 + 존재 (그 외: null)
	{
		string? sLog = GetLogFolderPath(sPath);

		return sLog != null && IsViewFile(Path.GetFileName(sLog.AsSpan())) ? sLog : null;
	}

	private static bool HasLink(string sRoot, string sFull)	// sRoot 아래 sFull 까지 링크 (정션·심볼릭) 있음 (aLogFolder 밖 우회 방지, 확인 실패: 있음)
	{
		try
		{
			for (string? sPath = Path.TrimEndingDirectorySeparator(sFull); sPath != null && sPath.Length >= sRoot.Length; sPath = Path.GetDirectoryName(sPath))
			{
				FileSystemInfo kInfo = Directory.Exists(sPath) ? new DirectoryInfo(sPath) : new FileInfo(sPath);

				if (kInfo.LinkTarget != null)
				{
					return true;
				}
			}
		}
		catch (Exception)	// 권한 없음 등
		{
			return true;
		}

		return false;
	}

	private static bool IsViewFile(ReadOnlySpan<char> sName)	// 보기 확장자 (대소문자 무시, 페이지 HTFile.FILE_VIEW_EXT 와 같음)
	{
		return sName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || sName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || sName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || sName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
			|| sName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || sName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || sName.EndsWith(".config", StringComparison.OrdinalIgnoreCase)
			|| sName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || sName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsLogFile(ReadOnlySpan<char> sName)	// 검색 대상 확장자 (대소문자 무시)
	{
		return sName.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsDumpFile(ReadOnlySpan<char> sName)	// 덤프 확장자 (대소문자 무시)
	{
		return sName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);
	}

	private object DumpList(long nReqId)	// 덤프 목록: aLogFolder 하위 *.dmp (경로 순, 겹친 경로 1번, 최대 m_nDumpMax 건, 없는 aLogFolder: 건너뜀)
	{
		List<DumpFile>	listFile	= new List<DumpFile>();
		bool			bTruncated	= false;				// 최대 건수 초과
		string			sError		= "";
		long			nBegin		= Stopwatch.GetTimestamp();

		try
		{
			Throttle			kThrottle	= new Throttle();
			EnumerationOptions	kOption		= new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = m_nLogScanDepth, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
			HashSet<string>		hsetPath	= new HashSet<string>(StringComparer.OrdinalIgnoreCase);	// 담은 경로 (aLogFolder 겹침: 1번만)

			foreach (string sRoot in m_arrLogRoot)
			{
				if (bTruncated || !Directory.Exists(sRoot))
				{
					continue;
				}

				FileSystemEnumerable<DumpFile> kFile = new FileSystemEnumerable<DumpFile>(sRoot, (ref FileSystemEntry kEntry) => new DumpFile(kEntry.ToFullPath(), kEntry.Length, kEntry.LastWriteTimeUtc.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), kOption)
				{
					ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => !kEntry.IsDirectory && IsDumpFile(kEntry.FileName),
					ShouldRecursePredicate	= (ref FileSystemEntry kEntry) =>	// 폴더마다 부하 조절
					{
						kThrottle.Check();

						return true;
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

		HTLog.Write(INFO_, $"덤프 목록: {listFile.Count}건 ({(long)Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds}ms){(bTruncated ? " (일부)" : "")}{(sError == "" ? "" : $" (오류: {sError})")}");

		return new { sType = "DumpResult", nReqId, bTruncated, sError, arrFile = listFile };
	}

	private object DumpRead(long nReqId, string sPath, long nOffset, int nCount)	// 덤프 다운 조각
	{
		return ReadChunk("DumpReadResult", nReqId, sPath, GetDumpPath(sPath), nOffset, nCount);
	}

	private object FileRead(long nReqId, string sPath, long nOffset, int nCount)	// 파일 다운 조각 (aLogFolder 하위)
	{
		return ReadChunk("FileReadResult", nReqId, sPath, GetFilePath(sPath), nOffset, nCount);
	}

	private static ChunkResult ReadChunk(string sResultType, long nReqId, string sPath, string? sFull, long nOffset, int nCount)	// 파일 조각 읽기: nOffset 부터 최대 nCount 바이트 (sFull null: 거부, 풀 버퍼 → 전송 후 Dispose 로 반환)
	{
		byte[]?	arrBuffer	= null;	// 풀 버퍼 (앞 nLength 만 사용)
		int		nLength		= 0;
		long	nSize		= 0;
		string	sError		= "";

		if (sFull == null)
		{
			sError = "허용되지 않은 경로";
		}
		else if (nOffset < 0)
		{
			sError = "읽기 조건 오류";
		}
		else
		{
			try
			{
				using FileStream kStream = new FileStream(sFull, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);	// 쓰는 중인 파일 허용

				nSize		= kStream.Length;
				nLength		= (int)Math.Clamp(nSize - nOffset, 0, Math.Clamp(nCount, 1, m_nDumpChunkMax));
				arrBuffer	= ArrayPool<byte>.Shared.Rent(Math.Max(1, nLength));	// 풀 재사용 (조각마다 큰 배열 확보 방지)

				kStream.Position = nOffset;
				kStream.ReadExactly(arrBuffer, 0, nLength);
			}
			catch (Exception kException)	// 파일 열기·읽기 실패 (읽는 중 크기 줄어듦 포함)
			{
				if (arrBuffer != null)
				{
					ArrayPool<byte>.Shared.Return(arrBuffer);
				}

				arrBuffer	= null;
				nLength		= 0;
				sError		= GetErrorText(kException, $"파일 읽기 {sPath}");
			}
		}

		return new ChunkResult(sResultType, nReqId, sPath, nOffset, nSize, sError, arrBuffer, nLength);
	}

	private string? GetLogFolderPath(string sPath)	// aLogFolder 하위 파일 전체 경로: 존재 + 링크 경유 없음 (상대 경로·스트림 지정·aLogFolder 밖: null)
	{
		if (string.IsNullOrEmpty(sPath) || !Path.IsPathFullyQualified(sPath) || sPath.IndexOf(':', 2) >= 0)	// 상대 경로·스트림 지정 거부 (드라이브 ':' 다음 ':' 없음)
		{
			return null;
		}

		string sLog = Path.GetFullPath(sPath);

		foreach (string sRoot in m_arrLogRoot)
		{
			string sLogRoot = GetRootPrefix(sRoot);

			if (sLog.StartsWith(sLogRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(sLog) && !HasLink(sLogRoot, sLog))
			{
				return sLog;
			}
		}

		return null;
	}

	private string? GetFilePath(string sPath)	// 파일 다운 허용 경로: aLogFolder 하위 + 존재 (확장자 제한 없음)
	{
		return GetLogFolderPath(sPath);
	}

	private object DumpDelete(long nReqId, string sPath)	// 덤프 삭제 (허용 경로만)
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

		HTLog.Write(sError == "" ? NOTI_ : WARN_, $"덤프 삭제: {sPath}{(sError == "" ? "" : $" (오류: {sError})")}");

		return new { sType = "DumpDeleteResult", nReqId, sPath, sError };
	}

	private object FileList(long nReqId, string sRelPath)	// 폴더 보기: sRelPath 바로 아래 폴더·파일 (이름순, 최대 m_nFileListMax 개, 숨김·시스템·링크 제외), "": 있는 aLogFolder 목록
	{
		List<string>	listDir		= new List<string>();		// 폴더 이름 ("": aLogFolder 전체 경로)
		List<FileItem>	listFile	= new List<FileItem>();
		bool			bTruncated	= false;					// 최대 개수 초과
		string			sError		= "";

		if (sRelPath == "")	// 맨 위: aLogFolder 목록 (페이지가 그대로 경로로 씀 → 하위는 전체 경로)
		{
			listDir.AddRange(m_arrLogRoot.Where(Directory.Exists));

			return new { sType = "FilesResult", nReqId, sPath = sRelPath, bTruncated, sError, arrDir = listDir, arrFile = listFile };
		}

		string? sFull = GetFolderPath(sRelPath);

		if (sFull == null)
		{
			sError = "허용되지 않은 경로";
		}
		else
		{
			try
			{
				EnumerationOptions kOption = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };	// 바로 아래만 (링크 폴더 제외 → aLogFolder 밖으로 안 나감)

				FileSystemEnumerable<(bool bDir, string sName, long nSize, DateTimeOffset kTime)> kEntry = new FileSystemEnumerable<(bool bDir, string sName, long nSize, DateTimeOffset kTime)>(sFull, (ref FileSystemEntry kItem) => (kItem.IsDirectory, kItem.FileName.ToString(), kItem.IsDirectory ? 0 : kItem.Length, kItem.LastWriteTimeUtc), kOption);

				foreach ((bool bDir, string sName, long nSize, DateTimeOffset kTime) in kEntry)
				{
					if (listDir.Count + listFile.Count >= m_nFileListMax)	// 최대 개수 초과: 중단
					{
						bTruncated = true;

						break;
					}

					if (bDir)
					{
						listDir.Add(sName);
					}
					else
					{
						listFile.Add(new FileItem(sName, nSize, kTime.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
					}
				}

				listDir.Sort(StringComparer.OrdinalIgnoreCase);
				listFile.Sort((kLeft, kRight) => string.Compare(kLeft.sName, kRight.sName, StringComparison.OrdinalIgnoreCase));
			}
			catch (Exception kException)	// 폴더 읽기 실패 (권한·삭제됨)
			{
				sError = GetErrorText(kException, $"폴더 보기 {sRelPath}");
			}
		}

		return new { sType = "FilesResult", nReqId, sPath = sRelPath, bTruncated, sError, arrDir = listDir, arrFile = listFile };
	}

	private string? GetFolderPath(string sPath)	// 폴더 보기 허용 경로: aLogFolder 자신·하위 + 존재 (그 외: null)
	{
		if (!Path.IsPathFullyQualified(sPath) || sPath.IndexOf(':', 2) >= 0)	// 상대 경로·스트림 지정 거부
		{
			return null;
		}

		string sFull = GetRootPrefix(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sPath)));	// 끝 구분자 붙여 비교 (aLogFolder 자신 허용)

		foreach (string sRoot in m_arrLogRoot)
		{
			string sLogRoot = GetRootPrefix(sRoot);

			if (sFull.StartsWith(sLogRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(sFull) && !HasLink(sLogRoot, sFull))
			{
				return sFull;
			}
		}

		return null;
	}

	private string? GetDumpPath(string sPath)	// 덤프 허용 경로: aLogFolder 하위 *.dmp + 존재 (그 외: null)
	{
		string? sFull = GetLogFolderPath(sPath);

		return sFull != null && IsDumpFile(sFull) ? sFull : null;
	}

	private static bool TryGetDate(ReadOnlySpan<char> sName, out long nDate)	// 날짜 (yyyyMMdd, 유효 날짜)
	{
		nDate = 0;

		if (sName.Length != 8 || sName.ContainsAnyExceptInRange('0', '9') || !DateTime.TryParseExact(sName, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
		{
			return false;
		}

		nDate = long.Parse(sName, CultureInfo.InvariantCulture);

		return true;
	}

	private static bool TryGetDateHour(ReadOnlySpan<char> sName, out long nDateHour)	// 파일명 날짜·시간 (yyyyMMdd_HH → yyyyMMddHH, 앞뒤 숫자 없음, 여러 개: 마지막)
	{
		nDateHour = 0;

		for (int nIndex = sName.Length - 11; nIndex >= 0; --nIndex)	// 뒤에서부터 (마지막 것)
		{
			ReadOnlySpan<char> kPart = sName.Slice(nIndex, 11);	// yyyyMMdd_HH 후보

			if (kPart[8] != '_' || kPart.Slice(0, 8).ContainsAnyExceptInRange('0', '9') || kPart.Slice(9).ContainsAnyExceptInRange('0', '9'))
			{
				continue;
			}

			if ((nIndex > 0 && char.IsAsciiDigit(sName[nIndex - 1])) || (nIndex + 11 < sName.Length && char.IsAsciiDigit(sName[nIndex + 11])))	// 앞뒤에 숫자 이어짐
			{
				continue;
			}

			int nHour = (kPart[9] - '0') * 10 + (kPart[10] - '0');

			if (nHour > 23 || !TryGetDate(kPart.Slice(0, 8), out long nDate))	// 유효 시간·날짜 아님
			{
				return false;
			}

			nDateHour = nDate * 100 + nHour;

			return true;
		}

		return false;
	}

	private static bool TryGetLogDateHour(ReadOnlySpan<char> sName, out long nDateHour)	// 로그 감시 파일명 날짜·시간: 이름 끝 yyyyMMdd_HH.log (앞에 숫자 없음, 대소문자 무시, 예: X_20261031_11 - 복사본.log: false)
	{
		nDateHour = 0;

		if (sName.Length < 15 || !IsLogFile(sName) || (sName.Length > 15 && char.IsAsciiDigit(sName[sName.Length - 16])))	// yyyyMMdd_HH.log 보다 짧음·로그 아님·앞에 숫자 이어짐
		{
			return false;
		}

		return TryGetDateHour(sName.Slice(sName.Length - 15, 11), out nDateHour);
	}

	private delegate bool BlockHandler(ReadOnlySpan<char> kBlock);	// 블록 처리 (false: 중단)

	private static Encoding? m_kCp949;	// CP949 (처음 쓸 때 생성 → Main 의 코드 페이지 등록 뒤)

	private static void ForEachBlock(string sPath, BlockHandler funcBlock, Throttle kThrottle, long nOffset = 0)	// 파일 블록 읽기: 블록 = 완전한 줄들 (파일 끝·아주 긴 줄 조각 예외, nOffset: 시작 바이트, UTF-16: 항상 처음)
	//   인코딩: 줄마다 UTF-8·CP949 판별 (섞여도 안 깨짐), UTF-8 BOM 건너뜀, UTF-16 BOM: UTF-16
	{
		using FileStream kStream = new FileStream(sPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.SequentialScan);	// 버퍼 없이 블록 단위로 직접 읽음, 순차 읽기 힌트

		Span<byte>	kBom	= stackalloc byte[3];
		int			nBom	= kStream.ReadAtLeast(kBom, 3, throwOnEndOfStream: false);

		if (nBom >= 2 && ((kBom[0] == 0xFF && kBom[1] == 0xFE) || (kBom[0] == 0xFE && kBom[1] == 0xFF)))	// UTF-16 BOM: 글자 단위로 읽음
		{
			kStream.Position = 0;

			using StreamReader kReader = new StreamReader(kStream, Encoding.Unicode, true, m_nReadBufferSize);

			ForEachCharBlock(kReader, funcBlock, kThrottle);

			return;
		}

		kStream.Position = nOffset > 0 ? nOffset : (nBom == 3 && kBom[0] == 0xEF && kBom[1] == 0xBB && kBom[2] == 0xBF ? 3 : 0);	// 지정 위치 (줄 시작) 또는 처음 (UTF-8 BOM 건너뜀)

		byte[]	arrBytes	= ArrayPool<byte>.Shared.Rent(m_nBlockBytes);
		char[]	arrChars	= ArrayPool<char>.Shared.Rent(m_nBlockBytes);	// 글자 수 ≤ 바이트 수
		int		nLen		= 0;											// 바이트 블록 안 데이터 길이

		try
		{
			while (true)
			{
				int nRead = kStream.Read(arrBytes, nLen, arrBytes.Length - nLen);
				int nEnd;															// 넘길 블록 끝

				nLen += nRead;

				if (nLen == 0)	// 빈 파일·다 처리함
				{
					return;
				}

				if (nRead == 0)	// 파일 끝: 남은 전부
				{
					nEnd = nLen;
				}
				else
				{
					int nLast = arrBytes.AsSpan(0, nLen).LastIndexOf((byte)'\n');	// 마지막 줄 끝

					if (nLast >= 0)
					{
						nEnd = nLast + 1;
					}
					else if (nLen < arrBytes.Length)	// 줄 끝 없음: 더 읽음
					{
						continue;
					}
					else if (arrBytes.Length < m_nLineBytesMax)	// 버퍼보다 긴 줄: 버퍼 키움
					{
						byte[] arrNewBytes = ArrayPool<byte>.Shared.Rent(arrBytes.Length * 2);
						char[] arrNewChars = ArrayPool<char>.Shared.Rent(arrNewBytes.Length);

						arrBytes.AsSpan(0, nLen).CopyTo(arrNewBytes);
						ArrayPool<byte>.Shared.Return(arrBytes);
						ArrayPool<char>.Shared.Return(arrChars);

						arrBytes = arrNewBytes;
						arrChars = arrNewChars;

						continue;
					}
					else	// 아주 긴 줄: 조각으로 넘김 (줄 번호는 '\n' 기준이라 유지)
					{
						nEnd = nLen;
					}
				}

				int nChars = Decode(arrBytes.AsSpan(0, nEnd), arrChars);

				if (nChars > 0 && !funcBlock(arrChars.AsSpan(0, nChars)))
				{
					return;
				}

				kThrottle.Check();

				if (nRead == 0)
				{
					return;
				}

				arrBytes.AsSpan(nEnd, nLen - nEnd).CopyTo(arrBytes);	// 남은 (끝나지 않은) 줄을 앞으로

				nLen -= nEnd;
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(arrBytes);
			ArrayPool<char>.Shared.Return(arrChars);
		}
	}

	private static int Decode(ReadOnlySpan<byte> kBytes, Span<char> kChars)	// 바이트 블록 → 글자 (전체 UTF-8: 한 번에, 아니면 줄마다 판별 후 같은 인코딩 묶음 변환), 반환: 글자 수
	{
		if (Utf8.IsValid(kBytes))	// 대부분의 UTF-8·영문 로그
		{
			return Encoding.UTF8.GetChars(kBytes, kChars);
		}

		Encoding	kCp949		= m_kCp949 ??= Encoding.GetEncoding(949);
		int			nChars		= 0;
		int			nRunStart	= 0;		// 같은 인코딩 묶음 시작
		int			nPos		= 0;		// 다음 줄 시작
		bool		bRunUtf8	= false;	// 묶음 인코딩 (false: CP949)
		bool		bRunFixed	= false;	// 묶음 인코딩이 정해짐

		while (nPos < kBytes.Length)
		{
			int					nIndex	= kBytes.Slice(nPos).IndexOf((byte)'\n');				// 줄 끝 (상대)
			int					nNext	= nIndex < 0 ? kBytes.Length : nPos + nIndex + 1;
			ReadOnlySpan<byte>	kLine	= kBytes.Slice(nPos, nNext - nPos);

			if (!Ascii.IsValid(kLine))	// 영문뿐인 줄은 어느 쪽이든 같음
			{
				bool bUtf8 = Utf8.IsValid(kLine);

				if (bRunFixed && bUtf8 != bRunUtf8)	// 인코딩 바뀜: 앞 묶음 변환
				{
					nChars		+= (bRunUtf8 ? Encoding.UTF8 : kCp949).GetChars(kBytes.Slice(nRunStart, nPos - nRunStart), kChars.Slice(nChars));
					nRunStart	= nPos;
				}

				bRunUtf8	= bUtf8;
				bRunFixed	= true;
			}

			nPos = nNext;
		}

		return nChars + (bRunUtf8 ? Encoding.UTF8 : kCp949).GetChars(kBytes.Slice(nRunStart), kChars.Slice(nChars));
	}

	private static void ForEachCharBlock(StreamReader kReader, BlockHandler funcBlock, Throttle kThrottle)	// 글자 단위 블록 읽기 (UTF-16 파일용, 블록 규칙은 ForEachBlock 과 같음)
	{
		char[]	arrBuffer	= ArrayPool<char>.Shared.Rent(m_nBlockBytes);
		int		nLen		= 0;											// 버퍼 안 글자 수

		try
		{
			while (true)
			{
				int nRead = kReader.Read(arrBuffer, nLen, arrBuffer.Length - nLen);
				int nEnd;															// 넘길 블록 끝

				nLen += nRead;

				if (nLen == 0)
				{
					return;
				}

				if (nRead == 0)
				{
					nEnd = nLen;
				}
				else
				{
					int nLast = arrBuffer.AsSpan(0, nLen).LastIndexOf('\n');	// 마지막 줄 끝

					if (nLast >= 0)
					{
						nEnd = nLast + 1;
					}
					else if (nLen < arrBuffer.Length)
					{
						continue;
					}
					else if (arrBuffer.Length < m_nLineBytesMax)
					{
						char[] arrNew = ArrayPool<char>.Shared.Rent(arrBuffer.Length * 2);

						arrBuffer.AsSpan(0, nLen).CopyTo(arrNew);
						ArrayPool<char>.Shared.Return(arrBuffer);

						arrBuffer = arrNew;

						continue;
					}
					else
					{
						nEnd = nLen;
					}
				}

				if (!funcBlock(arrBuffer.AsSpan(0, nEnd)) || nRead == 0)
				{
					return;
				}

				kThrottle.Check();

				arrBuffer.AsSpan(nEnd, nLen - nEnd).CopyTo(arrBuffer);

				nLen -= nEnd;
			}
		}
		finally
		{
			ArrayPool<char>.Shared.Return(arrBuffer);
		}
	}

	private static string ToText(ReadOnlySpan<char> kLine, int nMax)	// 줄 → 문자열 (끝 '\r' 제거, nMax 초과: 잘라서 끝에 "…")
	{
		if (kLine.Length > 0 && kLine[^1] == '\r')
		{
			kLine = kLine.Slice(0, kLine.Length - 1);
		}

		return kLine.Length <= nMax ? kLine.ToString() : string.Concat(kLine.Slice(0, nMax), "…");
	}

	private static string GetText(JsonElement kRoot, string sKey)	// 문자열 값 (객체 아님·키 없음·문자열 아님: "")
	{
		if (kRoot.ValueKind != JsonValueKind.Object || !kRoot.TryGetProperty(sKey, out JsonElement kValue) || kValue.ValueKind != JsonValueKind.String)
		{
			return "";
		}

		return kValue.GetString() ?? "";
	}

	private static long GetLong(JsonElement kRoot, string sKey)	// 정수 값 (객체 아님·키 없음·정수 아님: -1)
	{
		if (kRoot.ValueKind != JsonValueKind.Object || !kRoot.TryGetProperty(sKey, out JsonElement kValue) || kValue.ValueKind != JsonValueKind.Number || !kValue.TryGetInt64(out long nValue))
		{
			return -1;
		}

		return nValue;
	}

	private static async Task Delay(int nMs, CancellationToken kStop)	// 대기 (종료 요청 시 예외 없이 반환)
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

enum SEARCH_STOP	// 파일 검색 중단 이유
{
	SEARCH_STOP_NONE	= 0,	// 끝까지 검색
	SEARCH_STOP_MAX		= 1,	// 최대 건수 초과
	SEARCH_STOP_TIME	= 2,	// 시간 초과
	SEARCH_STOP_CANCEL	= 3,	// 중지 요청
}

enum SEARCH_OP	// 검색 조건 연산 (SearchNode)
{
	SEARCH_OP_TERM	= 0,	// 검색어 (줄에 있음, 대소문자 무시)
	SEARCH_OP_AND	= 1,	// 왼쪽·오른쪽 모두
	SEARCH_OP_OR	= 2,	// 왼쪽·오른쪽 하나라도
	SEARCH_OP_NOT	= 3,	// 왼쪽이고 오른쪽 아님
}

class Throttle	// 부하 조절: m_nBusyMs 연속 작업 시 m_nRestMs 쉼 (블록마다 확인, 실서버 CPU·디스크 양보)
{
	private const int	m_nBusyMs	= 100;	// 연속 작업 시간 (ms)
	private const int	m_nRestMs	= 10;	// 쉬는 시간 (ms)

	private long		m_nSliceStart	= Stopwatch.GetTimestamp();	// 작업 시작 타임스탬프

	public void Check()	// m_nBusyMs 초과 시 쉼 (호출 스레드만)
	{
		if (Stopwatch.GetElapsedTime(m_nSliceStart).TotalMilliseconds < m_nBusyMs)
		{
			return;
		}

		Thread.Sleep(m_nRestMs);

		m_nSliceStart = Stopwatch.GetTimestamp();
	}
}

class SearchJob	// 진행 중인 검색 (진행 알림·Stop 중지)
{
	public readonly CancellationTokenSource	m_kCancel		= new CancellationTokenSource();	// 중지 요청
	public readonly long					m_nBegin		= Stopwatch.GetTimestamp();		// 시작 타임스탬프
	public volatile int						m_nFileTotal	= -1;							// 대상 파일 수 (-1: 앞 검색 대기 중)
	public volatile int						m_nFileDone;									// 검색한 파일 수
	public volatile int						m_nFound;										// 찾은 줄 수
	public long								m_nTextChars;									// 찾은 줄 글자 수 합 (검색 스레드 전용)
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

	public LogWatch(string sFolder)
	{
		m_sFolder = sFolder;
	}

	public void Close()	// 핸들 닫기 (위치 유지)
	{
		m_kHandle?.Dispose();

		m_kHandle = null;
	}
}

record struct SearchLine(string sPath, long nDateHour, long nLine, string sText);	// 검색 결과 한 줄 (속성 이름 그대로 JSON 전송)

record LogField(string sKey, Regex kRegex);	// 보낼 줄 필드 (설정 aField 키·정규식)

class SearchNode	// 검색 조건 트리: 대문자 AND·OR·NOT 왼쪽부터, 괄호 먼저 (예: "A AND (B NOT C) OR D" = (A AND (B NOT C)) OR D, 페이지 HTKey.Parse 와 같은 규칙)
//   연산자: 공백 + 대문자 AND·OR·NOT + 공백·'(' (소문자·단어 안: 검색어), 괄호: 검색어 자리 '(' 와 짝 ')' (Init(x) 등 검색어 안 괄호: 그대로)
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
				return kLine.IndexOf(m_sTerm, StringComparison.OrdinalIgnoreCase) >= 0;
			}
		}
	}

	public string[] GetFirst()	// 블록에서 먼저 찾을 검색어 (맞는 줄에 이 중 하나 꼭 있음: OR 양쪽 합, AND 최단 검색어가 긴 쪽, NOT 왼쪽)
	{
		switch (m_eOp)
		{
			case SEARCH_OP.SEARCH_OP_AND:
			{
				string[] arrLeft	= m_kLeft!.GetFirst();
				string[] arrRight	= m_kRight!.GetFirst();

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
				return [m_sTerm];
			}
		}
	}

	private static SearchNode? ParseExpr(List<(char cType, string sTerm)> listToken, ref int nIndex)	// 식: 항 (연산자 항)… 왼쪽부터 차례로 (null: 형식 오류)
	{
		SearchNode? kLeft = ParseTerm(listToken, ref nIndex);	// 지금까지 결과

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

	private static List<(char cType, string sTerm)>? Tokenize(string sKey)	// 토큰 나누기: 검색어 T·AND &·OR |·NOT !·괄호 ( ) (null: 형식 오류 → 연산자 자리에 검색어, 연산자로 끝남)
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

			if (bOperand && sKey[nPos] == '(')
			{
				listToken.Add(('(', ""));

				++nDepth;
				++nPos;

				continue;
			}

			if (bOperand)	// 검색어: 다음 연산자·닫는 괄호 앞까지
			{
				int nEnd = FindTermEnd(sKey, nPos, nDepth);

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

	private static int FindTermEnd(string sKey, int nStart, int nDepth)	// 검색어 끝 위치: 공백 뒤 연산자 또는 (괄호 안) 닫는 괄호 앞 (검색어 안 짝 괄호: 검색어 글자)
	{
		int nInner = 0;	// 검색어 안에서 열린 괄호 수 (예: Init(x))

		for (int nIndex = nStart; nIndex < sKey.Length; ++nIndex)
		{
			char cChar = sKey[nIndex];

			if (char.IsWhiteSpace(cChar))	// 공백 뒤 연산자·닫는 괄호면 여기서 끝
			{
				int nNext = nIndex;

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
		int nNext = nPos + 1;

		while (nNext < sKey.Length && (char.IsWhiteSpace(sKey[nNext]) || sKey[nNext] == ')'))
		{
			++nNext;
		}

		return nNext >= sKey.Length || GetOperator(sKey, nNext, out _) > 0;
	}

	private static int GetOperator(string sKey, int nPos, out char cType)	// nPos 의 연산자 길이 (대문자 AND·OR·NOT + 공백·여는 괄호, 0: 아님), cType: & | !
	{
		foreach ((string sWord, char cOp) in new[] { ("AND", '&'), ("OR", '|'), ("NOT", '!') })
		{
			int nNext = nPos + sWord.Length;

			if (string.CompareOrdinal(sKey, nPos, sWord, 0, sWord.Length) == 0 && nNext < sKey.Length && (char.IsWhiteSpace(sKey[nNext]) || sKey[nNext] == '('))
			{
				cType = cOp;

				return sWord.Length;
			}
		}

		cType = ' ';

		return 0;
	}
}

record struct DumpFile(string sPath, long nSize, string sTime);	// 덤프 목록 한 건 (속성 이름 그대로 JSON 전송)

record struct FileItem(string sName, long nSize, string sTime);	// 폴더 보기 파일 한 건 (속성 이름 그대로 JSON 전송)

class ChunkResult : IDisposable	// 조각 읽기 결과 (공개 속성 이름 그대로 JSON 전송, arrData: base64, 풀 버퍼 → 전송 후 Dispose)
{
	private byte[]? m_arrBuffer;	// 빌린 버퍼 (반환 후·없음: null)

	public string				sType	{ get; }	// DumpReadResult·FileReadResult
	public long					nReqId	{ get; }	// 요청 번호
	public string				sPath	{ get; }	// 요청 경로
	public long					nOffset	{ get; }	// 읽은 시작 위치
	public long					nSize	{ get; }	// 지금 파일 크기
	public string				sError	{ get; }	// 오류 (없음: "")
	public ReadOnlyMemory<byte>	arrData	{ get; }	// 읽은 바이트 (버퍼 앞 부분)

	public ChunkResult(string sResultType, long nRequestId, string sRequestPath, long nReadOffset, long nFileSize, string sReadError, byte[]? arrBuffer, int nLength)
	{
		m_arrBuffer	= arrBuffer;
		sType		= sResultType;
		nReqId		= nRequestId;
		sPath		= sRequestPath;
		nOffset		= nReadOffset;
		nSize		= nFileSize;
		sError		= sReadError;
		arrData		= arrBuffer == null ? ReadOnlyMemory<byte>.Empty : arrBuffer.AsMemory(0, nLength);
	}

	public void Dispose()	// 빌린 버퍼 반환 (두 번 불러도 한 번만)
	{
		if (m_arrBuffer != null)
		{
			ArrayPool<byte>.Shared.Return(m_arrBuffer);

			m_arrBuffer = null;
		}
	}
}

class LineWriter	// 줄 단위 전송 (여러 작업 동시 전송 직렬화)
{
	private static readonly byte[]	m_arrNewLine	= { (byte)'\n' };

	private readonly Stream			m_kStream;	// 전송 스트림 (JSON 직접 쓰기)
	private readonly StreamWriter	m_kWriter;	// UTF-8, '\n' 구분 (flush 는 WriteLinesAsync 끝에 1회)
	private readonly SemaphoreSlim	m_kLock;	// 전송 1건씩

	public LineWriter(Stream kStream)
	{
		m_kStream	= kStream;
		m_kWriter	= new StreamWriter(kStream, new UTF8Encoding(false), 16 * 1024) { NewLine = "\n", AutoFlush = false };
		m_kLock		= new SemaphoreSlim(1, 1);
	}

	public async Task WriteJsonAsync(object kValue, JsonSerializerOptions kOption, CancellationToken kStop)	// 객체 → JSON 한 줄 전송 (큰 결과도 문자열 없이 나눠 씀)
	{
		await m_kLock.WaitAsync(kStop);

		try
		{
			await JsonSerializer.SerializeAsync(m_kStream, kValue, kValue.GetType(), kOption, kStop);
			await m_kStream.WriteAsync(m_arrNewLine, kStop);
			await m_kStream.FlushAsync(kStop);
		}
		finally
		{
			m_kLock.Release();
		}
	}

	public Task WriteAsync(string sLine, CancellationToken kStop)	// 한 줄 전송
	{
		return WriteLinesAsync(new[] { sLine }, kStop);
	}

	public async Task WriteLinesAsync(IReadOnlyList<string> listLine, CancellationToken kStop)	// 여러 줄 전송 (끝에 flush 1회)
	{
		await m_kLock.WaitAsync(kStop);

		try
		{
			foreach (string sLine in listLine)
			{
				await m_kWriter.WriteLineAsync(sLine.AsMemory(), kStop);
			}

			await m_kWriter.FlushAsync(kStop);
		}
		finally
		{
			m_kLock.Release();
		}
	}
}

class LineReader	// 줄 단위 수신 ('\n' 구분, UTF-8, 한 줄 최대 초과: InvalidDataException)
{
	private readonly Stream	m_kStream;		// 수신 스트림
	private readonly int	m_nMaxBytes;	// 한 줄 최대 바이트
	private readonly byte[]	m_arrBuffer;	// 수신 버퍼
	private int				m_nStart;		// 버퍼 안 처리 안 한 시작
	private int				m_nEnd;			// 버퍼 안 데이터 끝
	private MemoryStream	m_kLine;		// 버퍼보다 긴 줄 누적 (큰 줄 처리 후 작게 다시 생성)

	public LineReader(Stream kStream, int nMaxBytes)
	{
		m_kStream	= kStream;
		m_nMaxBytes	= nMaxBytes;
		m_arrBuffer	= new byte[64 * 1024];
		m_kLine		= new MemoryStream();
	}

	public async Task<string?> ReadLineAsync(CancellationToken kStop)	// 한 줄 ('\r' 제거, 연결 종료: null)
	{
		while (true)
		{
			int nIndex = Array.IndexOf(m_arrBuffer, (byte)'\n', m_nStart, m_nEnd - m_nStart);

			if (nIndex >= 0)
			{
				string sLine = TakeLine(nIndex);

				m_nStart = nIndex + 1;

				return sLine;
			}

			if (m_nEnd > m_nStart)	// 줄 끝 없음: 남은 부분 누적
			{
				m_kLine.Write(m_arrBuffer, m_nStart, m_nEnd - m_nStart);
			}

			m_nStart	= 0;
			m_nEnd		= 0;

			if (m_kLine.Length > m_nMaxBytes)	// 한 줄 최대 초과: 비정상 상대 → 연결 종료
			{
				throw new InvalidDataException($"수신 줄 길이 초과 ({m_nMaxBytes} bytes)");
			}

			int nRead = await m_kStream.ReadAsync(m_arrBuffer.AsMemory(), kStop);

			if (nRead == 0)	// 연결 종료
			{
				return null;
			}

			m_nEnd = nRead;
		}
	}

	private string TakeLine(int nIndex)	// 누적분 + 버퍼의 줄 끝까지 → 문자열
	{
		int		nCount = nIndex - m_nStart;	// 버퍼 안 줄 길이
		string	sLine;

		if (m_kLine.Length == 0)
		{
			sLine = Encoding.UTF8.GetString(m_arrBuffer, m_nStart, nCount);
		}
		else
		{
			if (m_kLine.Length + nCount > m_nMaxBytes)
			{
				throw new InvalidDataException($"수신 줄 길이 초과 ({m_nMaxBytes} bytes)");
			}

			m_kLine.Write(m_arrBuffer, m_nStart, nCount);

			sLine = Encoding.UTF8.GetString(m_kLine.GetBuffer(), 0, (int)m_kLine.Length);

			m_kLine = m_kLine.Capacity > 1024 * 1024 ? new MemoryStream() : m_kLine;	// 큰 줄 버퍼는 버림 (메모리 반환)

			m_kLine.SetLength(0);
		}

		return sLine.EndsWith('\r') ? sLine.Substring(0, sLine.Length - 1) : sLine;
	}
}

class RelayException(string sMessage) : Exception(sMessage)	// 상대(클라·서버)가 보낸 오류 글 그대로 (GetErrorText 가 바꾸지 않음)
{
}

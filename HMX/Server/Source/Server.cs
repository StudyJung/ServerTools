#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
#:property ServerGarbageCollection=true
#:package Microsoft.Extensions.Hosting.WindowsServices@10.0.10
#:package StackExchange.Redis@2.13.17
#:include ../../Common/Global.cs
#:include ../../Common/Config.cs
#:include ../../Common/Log.cs
#:include ../../Common/AES.cs
#:include ../../Common/Count.cs
#:include ../../Common/Define.cs
#:include Timer.cs
#:include Auth.cs
#:include Web.cs
#:include API.cs
#:include TCPServer.cs
#:include Gate.cs
#:include SavedList.cs
#:include TabSearch.cs
#:include TabDump.cs
#:include TabFile.cs
#:include Redis.cs
#:include TabOperation.cs
#:include Process.cs
#:include TabProcess.cs
#:include TabQuery.cs
#:include Alarm.cs
#:include Stat.cs
#:include Send.cs
#:include AlarmLog.cs

// ReadMe.cs 반드시 참조

global using System.Buffers;
global using System.Buffers.Binary;
global using System.Collections.Concurrent;
global using System.Diagnostics;
global using System.Globalization;
global using System.IO.Compression;
global using System.Net;
global using System.Net.Sockets;
global using System.Runtime;
global using System.Runtime.InteropServices;
global using System.Security.Claims;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Encodings.Web;
global using System.Text.Json;
global using System.Text.Json.Nodes;
global using System.Text.RegularExpressions;
global using Microsoft.AspNetCore.Authentication;
global using Microsoft.AspNetCore.Authentication.Cookies;
global using Microsoft.AspNetCore.DataProtection;
global using Microsoft.AspNetCore.HttpOverrides;
global using Microsoft.Extensions.Hosting.WindowsServices;
global using static Global;	// 전역 함수 클래스명 없이 호출
global using static Log;		// 로그 레벨 클래스명 없이 호출

return Main.Run(args);	// 진입 (클래스 Main 안에 진입 함수 Main 불가 → Main.Run)

class Main	// 진입점 + 공통·기능 연결 (웹·TCP 수신·타이머 ↔ 공통 API·탭)
{
	public const int	m_nDefaultPort	= 800;				// 기본 웹 포트 (공통 Global.GetDefaultPort)
	public const string	m_sPeerOff		= "Client Off";	// 상대 (클라) 끊김 표시 (공통 Global.GetErrorText)
	public const string	m_sSelfError	= "Server Error";	// 그 밖 오류 표시 (공통 Global.GetErrorText)
	public const string	TAB_QUERY	= "쿼리";	// 탭 이름 (설정 aMenu 값 = 페이지 탭 버튼 이름)
	public const string	TAB_SEARCH	= "서치";
	public const string	TAB_FILE	= "파일";
	public const string	TAB_DUMP	= "덤프";
	public const string	TAB_MANAGER	= "운영";
	public const string	TAB_PROCESS	= "반영";
	public static readonly string[]	m_arrTab	= { TAB_QUERY, TAB_SEARCH, TAB_FILE, TAB_DUMP, TAB_MANAGER, TAB_PROCESS };	// 모든 탭 (페이지 순서)
	public static readonly (string sKey, string sFile)[]	m_arrConfigSplit	= { ("aQuery", "Query.json"), ("aSearch", "Search.json"), ("aAlarm", "Alarm.json"), ("aField", "Field.json"), ("Manager", "Manager.json"), ("aAccount", "Account.json"), ("sKey", "Account.json"), ("sRand", "Account.json"), ("sToken", "Account.json") };	// 따로 관리 설정 (웹에서 고침: 저장 쿼리·저장 서치·알람·필드 → 키별 json, 운영 탭 서버 목록, 계정·관리 계정 외부 API 토큰 sToken·키 sKey: Account.json, 공통 Config)

	private const int	m_nStartFailWaitSec	= 10;	// 시작 실패 시 콘솔 종료 대기 (초, 더블클릭 실행에서 사유 확인)
	private const int	m_nShutdownSec		= 300;	// 종료 정리 최대 대기 (초, 호스트 ShutdownTimeout → 서비스: 이 동안 중지 대기 상태 유지, 밀린 적재·색인 많을 때)

	private static int	m_nShutdown;				// 데이터 정리 함 (1번만)
	private static int	m_nRunStart;				// 기능 시작함 (1번만, 마스터 키 뒤)

	private static CancellationToken	m_kWebStop;	// 웹 서버 종료 (TCP 수신·탭 타이머 중지)
	private static string[]?	m_arrMenu;			// 켜진 탭 (설정 aMenu 순서, 처음 GetMenu 때 정함)

	private static TCPServer?		m_kListener;	// Client TCP 수신 (웹 사용 때만)
	private static API?				m_kAPI;			// 공통 API
	private static TabSearch?	m_kSearch;		// 서치 탭
	private static TabDump?	m_kDump;		// 덤프 탭
	private static TabFile?	m_kFile;		// 파일 탭
	private static TabQuery?	m_kQuery;		// 쿼리 탭
	private static TabOperation?	m_kManager;		// 운영 탭
	private static TabProcess?	m_kProcess;		// 반영 탭

	private const string	m_sServiceName			= "HMXServer";	// 서비스 이름 (표시 이름 같음, -install·-uninstall·-start·-stop)
	private const string	m_sServiceDescription	= "로그 쿼리·서치·덤프·파일·운영·반영 웹 서버 (HMX Server)";	// 서비스 설명
	private const int		m_nServiceStopSec		= 60;		// 서비스 시작·정지 대기 (초)

	static int Install(string[] arrArg)	// 서비스 등록 (자동 시작, 실행 인자: -install 뒤 인자 그대로, 예: 포트 호스트) → 설명 → 시작 (0: 등록 완료, 1: 실패)
	{
		string sExe = Environment.ProcessPath ?? "";	// 등록할 실행 파일 (현재 exe)

		if (!string.Equals(Path.GetFileNameWithoutExtension(sExe), GetProjectName(), StringComparison.OrdinalIgnoreCase))	// dotnet 등 다른 실행 파일: 조기 반환
		{
			Log.Write(ERROR, $"서비스 등록은 게시된 {GetProjectName()}.exe 로 실행 (현재: {sExe})");

			return 1;
		}

		string sBinPath = string.Join(' ', arrArg.Select(sArg => sArg.Contains(' ') ? $"\"{sArg}\"" : sArg).Prepend($"\"{sExe}\""));	// 실행 명령 (exe + 실행 인자)

		if (RunSc("create", m_sServiceName, "binPath=", sBinPath, "start=", "auto", "DisplayName=", m_sServiceName) != 0)	// 등록 실패 (이미 등록: -uninstall 후 다시)
		{
			return 1;
		}

		RunSc("description", m_sServiceName, m_sServiceDescription);

		Log.Write(NOTI_, $"서비스 등록 완료: {m_sServiceName} ({sBinPath})");

		if (StartService() != 0)	// 등록은 됨: -start 로 다시
		{
			Log.Write(WARN_, $"서비스 시작 안 됨 ({GetProjectName()}.exe -start 로 다시)");
		}

		return 0;
	}

	static int Uninstall()	// 서비스 정지 (X초 대기) → 제거 (0: 제거 완료, 1: 실패, 정지 실패: 제거는 계속 시도)
	{
		StopService();

		if (RunSc("delete", m_sServiceName) != 0)
		{
			return 1;
		}

		Log.Write(NOTI_, $"서비스 제거 완료: {m_sServiceName}");

		return 0;
	}

	static int StartService()	// 서비스 시작 (X초 대기, 0: 시작·이미 실행 중, 1: 실패: 미등록·권한 없음·대기 넘음)
	{
		if (!OperatingSystem.IsWindows())	// 윈도우 전용
		{
			Log.Write(ERROR, "서비스: 윈도우 전용");

			return 1;
		}

		try
		{
			using System.ServiceProcess.ServiceController kService = new System.ServiceProcess.ServiceController(m_sServiceName);

			if (kService.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)	// 시작 중: 기다리기만
			{
				kService.Start();
			}

			kService.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(m_nServiceStopSec));

			Log.Write(NOTI_, $"서비스 시작 완료: {m_sServiceName}");

			return 0;
		}
		catch (Exception kException)	// 미등록·권한 없음 (관리자 권한 필요)·대기 넘음
		{
			Log.Write(ERROR, $"서비스 시작 실패: {m_sServiceName} ({kException.InnerException?.Message ?? kException.Message})");

			return 1;
		}
	}

	static int StopService()	// 서비스 정지 (X초 대기, 0: 정지·이미 정지, 1: 실패: 미등록·권한 없음·대기 넘음)
	{
		if (!OperatingSystem.IsWindows())	// 윈도우 전용
		{
			Log.Write(ERROR, "서비스: 윈도우 전용");

			return 1;
		}

		try
		{
			using System.ServiceProcess.ServiceController kService = new System.ServiceProcess.ServiceController(m_sServiceName);

			if (kService.Status != System.ServiceProcess.ServiceControllerStatus.Stopped && kService.Status != System.ServiceProcess.ServiceControllerStatus.StopPending)	// 정지 중: 기다리기만
			{
				kService.Stop();
			}

			kService.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(m_nServiceStopSec));

			Log.Write(NOTI_, $"서비스 정지 완료: {m_sServiceName}");

			return 0;
		}
		catch (Exception kException)	// 미등록·권한 없음 (관리자 권한 필요)·대기 넘음
		{
			Log.Write(WARN_, $"서비스 정지 실패: {m_sServiceName} ({kException.InnerException?.Message ?? kException.Message})");

			return 1;
		}
	}

	static int RunSc(params string[] arrArg)	// sc.exe 실행 (출력: 콘솔 그대로) → 종료 코드 (5: 관리자 권한 필요)
	{
		ProcessStartInfo kInfo = new ProcessStartInfo("sc.exe") { UseShellExecute = false };

		foreach (string sArg in arrArg)
		{
			kInfo.ArgumentList.Add(sArg);
		}

		using System.Diagnostics.Process kProcess = System.Diagnostics.Process.Start(kInfo)!;

		kProcess.WaitForExit();

		if (kProcess.ExitCode == 5)	// ERROR_ACCESS_DENIED
		{
			Log.Write(ERROR, "관리자 권한으로 실행 필요");
		}

		return kProcess.ExitCode;
	}

	public static int Run(string[] args)	// 진입: 인자 검사 후 서버 실행 (0: 정상 종료, 1: 인자·설정 오류·시작 실패)
	{
		Console.OutputEncoding = Encoding.UTF8;	// 한글 콘솔 출력

		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);	// CP949 (파일 다운 UTF-8 변환)

		SetMasterKey("");	// 마스터 키 비움: 로그인 사용 → 서버 오픈 뒤 첫 접속 1회 입력 (그 전: sCode 복호화·Redis 연결·메일 인증 안 함)

		if (!Config.Load())	// 설정 파일 로딩 (형식 오류: 종료)
		{
			return 1;
		}

		if (!Config.GetBool("bWeb") || !Config.GetBool("bAuth"))	// 로그인 안 씀: 입력할 곳 없음 → 마스터 키 = 솔루션명
		{
			SetMasterKey(Define.m_sSolution);

			if (!Config.Load())
			{
				return 1;
			}
		}

		string sArg = args.Length > 0 ? args[0].ToLowerInvariant() : "";	// 1번 인자: 서비스 명령 (-install·-uninstall·-start·-stop, 그 외: 포트)

		if (sArg == "-install")
		{
			return Install(args[1..]);	// 뒤 인자: 실행 인자 (포트 호스트)
		}

		if (sArg == "-uninstall")
		{
			return Uninstall();
		}

		if (sArg == "-start")
		{
			return StartService();
		}

		if (sArg == "-stop")
		{
			return StopService();
		}

		string[] arrOff = m_arrTab.Except(GetMenu()).ToArray();	// 꺼진 탭 (aMenu 에 없음)

		Log.Write(arrOff.Length == 0 ? INFO_ : NOTI_, $"탭: {string.Join("·", GetMenu())}{(arrOff.Length == 0 ? "" : $" (꺼짐: {string.Join("·", arrOff)} → 서버 동작 안 함, 설정 aMenu)")}");

		MoveDataDir();	// 예전 File 폴더 → Data (색인·원문·알람 읽기 전)

		Web? kWeb = null;	// 웹 서버 (설정 bWeb 가 false·키 없음: null)

		if (Config.GetBool("bWeb"))	// bWeb true 일 때만
		{
			Log.Write(INFO_, "웹: 실행 (설정 bWeb 가 true)");

			int nPort = GetDefaultPort();	// 포트 (1번 인자)

			if (args.Length > 0 && (!int.TryParse(args[0], out nPort) || nPort <= 0 || nPort > 65535))
			{
				Log.Write(ERROR, $"포트 오류: {args[0]} (1~65535)");

				return 1;
			}

			string sHost = args.Length > 1 ? args[1] : "localhost";	// 호스트 (2번 인자)

			kWeb = new Web(nPort, sHost);

			Create();	// TCP 수신·공통 API·탭
		}

		if (kWeb == null)	// 웹 없음 (로그인 없음): 바로 기능 시작 (웹: Web.StartAsync → StartRun)
		{
			RunAll();
		}

		ThreadPool.GetMinThreads(out int nWorker, out int nIO);
		ThreadPool.SetMinThreads(Math.Max(nWorker, Environment.ProcessorCount * 2), nIO);	// 쿼리 병렬 (동시 X건 × 코어 절반) 중에도 웹·TCP 수신 스레드 여유

		try
		{
			Service(kWeb);	// 서비스 SCM 연동 + 호스트 실행 (웹 서버는 호스트와 함께 시작·중지)
		}
		catch (Exception kException)	// 호스트 시작 실패 (웹 포트 사용 중 등): 사유 기록 후 종료
		{
			Log.Write(FATAL, $"서버 시작 실패: {GetStartError(kException)}");

			if (!WindowsServiceHelpers.IsWindowsService() && Environment.UserInteractive)	// 콘솔 실행: 사유 볼 수 있게 대기 후 종료
			{
				Log.Write(INFO_, $"{m_nStartFailWaitSec}초 후 종료");

				Thread.Sleep(m_nStartFailWaitSec * 1000);
			}

			return 1;
		}
		finally	// 호스트 시작 실패 (포트 사용 중 등)·웹 없음 도 데이터 정리 (웹 중지 때 이미 함: 안 함)
		{
			Shutdown();
		}

		return 0;
	}

	public static void Shutdown()	// 데이터 정리 (1번만, 클라 수신 끝난 뒤): 타이머 중지 → 워커 남은 Log 모두 원문·받은 진행 (Count) 저장·닫음 → 남은 색인 세그먼트 → 알람 펜딩 저장
	{
		if (Interlocked.Exchange(ref m_nShutdown, 1) != 0)
		{
			return;
		}

		long nBegin = Stopwatch.GetTimestamp();	// 정리 시작

		Log.Write(INFO_, "종료 정리: 시작 (타이머 → 적재 원문·받은 진행 → 색인 → 알람)");

		Timer.Stop();		// 타이머 중지 (진행 중인 Batch 완료 대기)

		if (IsTab(TAB_QUERY))	// 쿼리 탭 꺼짐: 적재·알람 없음
		{
			TabQuery.Close();	// 워커 라이트 큐 모두 처리 (원문·Count 닫음) → 남은 색인 버퍼 모두 세그먼트 파일로 (타이머 중지 후)

			if (m_nRunStart != 0)	// 기능 시작 전 (마스터 키 입력 전): 알람 안 돎 → 펜딩 그대로 (빈 펜딩 덮어쓰기 방지)
			{
				Alarm.Stop();	// 알람 통계 중지·남은 발송·펜딩 저장 (적재 끝난 뒤)
			}
		}

		Log.Write(INFO_, $"종료 정리: 끝 ({Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds:N0} ms)");
	}

	public static string[] GetMenu()	// 켜진 탭: 설정 aMenu 중 아는 탭 이름 (그 순서, 없음·맞는 이름 없음: 전체 → 페이지 ApplyMenu 와 같은 기준)
	{
		if (m_arrMenu == null)
		{
			string[] arrMenu = Config.GetArray("aMenu").Select(sName => sName.Trim()).Where(sName => m_arrTab.Contains(sName)).Distinct().ToArray();

			m_arrMenu = arrMenu.Length == 0 ? m_arrTab : arrMenu;
		}

		return m_arrMenu;
	}

	public static bool IsTab(string sName)	// 탭 켜짐 (aMenu 에 있음, 빠진 탭: 서버 동작 안 함)
	{
		return GetMenu().Contains(sName);
	}

	static void Create()	// 웹 사용 때만: TCP 수신 (설정 nPort)·공통 API·탭 생성 (탭: TCP 수신으로 클라 요청, aMenu 빠진 탭: 안 만듦)
	{
		bool bSearch	= IsTab(TAB_SEARCH);
		bool bFile		= IsTab(TAB_FILE);

		if (IsTab(TAB_QUERY) || bSearch || bFile || IsTab(TAB_DUMP))	// 클라 쓰는 탭 있음: TCP 수신 (없음: 포트 안 엶)
		{
			m_kListener = new TCPServer(Config.GetInt("nPort"), IsTab(TAB_QUERY));
		}

		m_kAPI = new API(m_kListener);

		if (bSearch || bFile)	// 파일 보기·다운: 서치·파일 공용
		{
			m_kSearch	= new TabSearch(m_kListener!);
			m_kFile		= new TabFile(m_kListener!);
		}

		if (IsTab(TAB_DUMP))
		{
			m_kDump = new TabDump(m_kListener!);
		}

		if (IsTab(TAB_QUERY))
		{
			m_kQuery = new TabQuery();
		}

		if (IsTab(TAB_MANAGER))
		{
			m_kManager = new TabOperation();
		}

		if (IsTab(TAB_PROCESS))
		{
			m_kProcess = new TabProcess();
		}
	}

	public static void MapRoute(WebApplication WebApp, bool bAuth)	// 라우트 등록 (Web.StartAsync): 기본 페이지 → 공통 API → 탭 → 로그인
	{
		Web.MapRoute(WebApp);			// 기본 페이지
		m_kAPI!.MapRoute(WebApp);		// 공통 API (서버 정보·에코·접속 클라·로그 수신)
		m_kSearch?.MapRoute(WebApp, IsTab(TAB_SEARCH));	// 서치 탭 (파일 보기: 서치·파일 공용)
		m_kDump?.MapRoute(WebApp);		// 덤프 탭
		m_kFile?.MapRoute(WebApp, IsTab(TAB_FILE));	// 파일 탭 (파일 다운: 서치·파일 공용)
		m_kQuery?.MapRoute(WebApp);		// 쿼리 탭
		m_kManager?.MapRoute(WebApp);	// 운영 탭
		m_kProcess?.MapRoute(WebApp);	// 반영 탭

		Auth.MapRoute(WebApp, bAuth);	// 계정 API + 로그인·로그아웃 API (bAuth)
	}

	public static void StartRun(CancellationToken kStop)	// 웹 서버 시작 때 (Web.StartAsync): 중지 토큰 보관 → 마스터 키 입력 대기 아님: 바로 기능 시작, 대기: 첫 접속 마스터 키 뒤 (Auth.Unlock → RunAll, 그 전: 로그인만)
	{
		m_kWebStop = kStop;

		if (!Auth.IsWait())
		{
			RunAll();
		}
	}

	public static readonly Comparer<string> m_kNaturalOrder = Comparer<string>.Create(CompareNatural);	// 오름차순 (숫자: 크기 순, 페이지 localeCompare numeric 과 같게, 알람 받는 계정·Account.json 계정)

	public static int CompareNatural(string sLeft, string sRight)	// 자연 정렬 비교 (숫자 덩어리: 크기, 그 외 글자: 대소문자 무시, 예: ne9 < ne10)
	{
		int nLeft	= 0;	// 왼쪽 위치
		int nRight	= 0;	// 오른쪽 위치

		while (nLeft < sLeft.Length && nRight < sRight.Length)
		{
			if (char.IsAsciiDigit(sLeft[nLeft]) && char.IsAsciiDigit(sRight[nRight]))	// 숫자 덩어리: 앞 0 뺀 길이 → 글자 순
			{
				int nLeftEnd	= nLeft;
				int nRightEnd	= nRight;

				while (nLeftEnd < sLeft.Length && char.IsAsciiDigit(sLeft[nLeftEnd]))
				{
					++nLeftEnd;
				}

				while (nRightEnd < sRight.Length && char.IsAsciiDigit(sRight[nRightEnd]))
				{
					++nRightEnd;
				}

				ReadOnlySpan<char>	kLeft	= sLeft.AsSpan(nLeft, nLeftEnd - nLeft).TrimStart('0');
				ReadOnlySpan<char>	kRight	= sRight.AsSpan(nRight, nRightEnd - nRight).TrimStart('0');
				int					nNumber	= kLeft.Length != kRight.Length ? kLeft.Length.CompareTo(kRight.Length) : kLeft.CompareTo(kRight, StringComparison.Ordinal);

				if (nNumber != 0)
				{
					return nNumber;
				}

				nLeft	= nLeftEnd;
				nRight	= nRightEnd;

				continue;
			}

			int nChar = char.ToLowerInvariant(sLeft[nLeft]).CompareTo(char.ToLowerInvariant(sRight[nRight]));

			if (nChar != 0)
			{
				return nChar;
			}

			++nLeft;
			++nRight;
		}

		return (sLeft.Length - nLeft).CompareTo(sRight.Length - nRight);	// 앞이 같음: 짧은 쪽 먼저
	}

	public static void RunAll()	// 기능 시작 (1번만, 마스터 키 뒤): 필드 순서 → 타이머 → 알람 → TCP 수신 (클라 접속·로그) → 탭 타이머 (운영 탭 동접 기록)
	{
		if (Interlocked.Exchange(ref m_nRunStart, 1) != 0)
		{
			return;
		}

		if (IsTab(TAB_QUERY))	// 쿼리 탭 꺼짐: TabQuery 안 건드림 (적재 워커·설정 로딩 안 함)
		{
			TabQuery.InitField();	// 설정 aField 순서 → 필드 목록 순서 (알람 매칭 준비 전)
		}

		Timer.Start();	// 타이머 시작

		if (IsTab(TAB_QUERY))
		{
			Alarm.Start(TabQuery.GetAlarmList());	// 알람 통계·발송 시작
		}

		Execute();

		bool bListen = m_kListener?.Start(m_kWebStop) ?? false;	// TCP 수신 (클라 접속·로그, 웹 서버 종료 시 함께 중지, false: 설정 nPort 없음·클라 쓰는 탭 없음)

		Log.Write(INFO_, $"TCP 수신: {(bListen ? GetTCPPort().ToString() : "없음")}");

		m_kManager?.StartTimer(m_kWebStop);	// 운영 탭 동접 기록 (운영 탭 꺼짐: 안 함)
	}

	public static int GetTCPPort()	// TCP 수신 포트
	{
		return m_kListener?.GetPort() ?? 0;
	}

	public static Task WaitTCP()	// 모든 클라 수신 끝 대기 (Web.StopAsync, 웹 중지 뒤)
	{
		return m_kListener?.WaitStop() ?? Task.CompletedTask;
	}

	public static void OnMinute()	// 타이머 1분 (Timer, 스레드 풀): 클라 분당 교체·로그
	{
		TCPServer.WriteMinute();
	}

	public static void OnTick()	// 타이머 X초 (Timer 스레드): 색인 버퍼 → 세그먼트 (쿼리 탭 꺼짐: 안 함)
	{
		if (IsTab(TAB_QUERY))
		{
			TabQuery.Tick();
		}
	}

	static void MoveDataDir()	// 예전 데이터 폴더 (기준 폴더 하위 File) → Data 이름 변경 (Data 있음: 그대로 + 경고, 실패: 경고 후 새 Data 로 시작)
	{
		string sOld	= Path.Combine(GetBaseDir(), "File");	// 예전 이름
		string sNew	= Path.Combine(GetBaseDir(), "Data");

		if (!Directory.Exists(sOld))
		{
			return;
		}

		if (Directory.Exists(sNew))
		{
			Log.Write(WARN_, $"예전 데이터 폴더 남음: {sOld} (Data 있음 → 그대로, 직접 확인)");

			return;
		}

		try
		{
			Directory.Move(sOld, sNew);

			Log.Write(INFO_, $"데이터 폴더 이름 변경: {sOld} → {sNew}");
		}
		catch (Exception kException)	// 사용 중·권한 없음
		{
			Log.Write(WARN_, $"데이터 폴더 이름 변경 실패: {sOld} ({kException.Message}, 새 Data 로 시작)");
		}
	}

	static string GetStartError(Exception kException)	// 시작 실패 사유 (포트 사용 중: 이미 실행 중인 같은 exe PID 포함, 그 외: 예외 메시지)
	{
		bool bInUse = false;	// 포트 사용 중

		for (Exception? kItem = kException; kItem != null; kItem = kItem.InnerException)
		{
			if (kItem is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
			{
				bInUse = true;

				break;
			}
		}

		if (!bInUse)
		{
			return kException.Message;
		}

		string sRunning = string.Join("·", System.Diagnostics.Process.GetProcessesByName(System.Diagnostics.Process.GetCurrentProcess().ProcessName).Where(kProcess => kProcess.Id != Environment.ProcessId).Select(kProcess => kProcess.Id));	// 이미 실행 중인 같은 이름 프로세스

		return $"웹 포트 사용 중 ({kException.Message}{(sRunning == "" ? ", 다른 프로그램이 사용 중" : $", 이미 실행 중인 {System.Diagnostics.Process.GetCurrentProcess().ProcessName} PID {sRunning}")})";
	}

	static void Execute()
	{
		Log.Write(NOTI_, "앱: 실행");	// 차후 작업할 코드 아래에 추가
	}

	static void Service(Web? pWeb)	// 서비스 SCM 연동 + 호스트 실행 (웹 서버: 호스트와 함께 시작·중지, 서비스 중지·Ctrl+C 까지 대기)
	{
		HostApplicationBuilder kBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
		{
			ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,	// 서비스 실행 시 기준 폴더 = exe 폴더
		});

		kBuilder.Services.AddWindowsService();				// 서비스 실행 시 SCM 연동: 시작 응답·중지 요청 수신 (콘솔·dotnet run 실행은 영향 없음)
		kBuilder.Services.Configure<HostOptions>(kOption => kOption.ShutdownTimeout = TimeSpan.FromSeconds(m_nShutdownSec));	// 중지 대기: 웹 중지 → 클라 수신 끝 → 데이터 정리 (Web.StopAsync) 끝날 때까지 (서비스: 그동안 중지 대기 상태, 기본 30초면 정리 중 중지 처리됨)
		kBuilder.Logging.SetMinimumLevel(LogLevel.Warning);	// 기본 로그 최소화

		if (pWeb != null)	// 웹 서버: 호스트 시작·중지 시 함께 시작·중지 (서비스 중지 응답 전 웹 서버 먼저 중지)
		{
			kBuilder.Services.AddHostedService(_ => pWeb);
		}

		kBuilder.Build().Run();	// 시작 → 서비스 중지·Ctrl+C 대기 → 중지 → 해제 (SCM 시작·중지 응답 포함)
	}
}

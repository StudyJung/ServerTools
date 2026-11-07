#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Worker
#:property PublishAot=false
#:property ServerGarbageCollection=false
#:package Microsoft.Extensions.Hosting.WindowsServices@10.0.10
#:package StackExchange.Redis@2.13.17
#:include ../../Common/Global.cs
#:include ../../Common/Config.cs
#:include ../../Common/Log.cs
#:include ../../Common/AES.cs
#:include ../../Common/Count.cs
#:include ../../Common/Define.cs
#:include Timer.cs
#:include TCPClient.cs
#:include Tab.cs
#:include TabQuery.cs
#:include TabSearch.cs
#:include TabDump.cs
#:include TabFile.cs
#:include Redis.cs
#:include Process.cs
#:include TabProcess.cs
#:include Clean.cs

// ReadMe.cs 반드시 참조

global using System.Buffers;
global using System.Collections.Concurrent;
global using System.Diagnostics;
global using System.Globalization;
global using System.IO.Enumeration;
global using System.Net;
global using System.Net.Sockets;
global using System.Runtime.InteropServices;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Encodings.Web;
global using System.Text.Json;
global using System.Text.Json.Nodes;
global using System.Text.RegularExpressions;
global using System.Text.Unicode;
global using System.Threading.Channels;
global using Microsoft.Extensions.Hosting.WindowsServices;
global using Microsoft.Win32.SafeHandles;
global using static Global;	// 전역 함수 클래스명 없이 호출
global using static Log;		// 로그 레벨 클래스명 없이 호출

return Main.Run(args);	// 진입 (클래스 Main 안에 진입 함수 Main 불가 → Main.Run)

class Main : BackgroundService	// 진입점 + 서비스: 공통·기능 연결 (반영 탭 시작, 탭 순서 준비 → 서버 접속, 접속·끊김·Pause → 쿼리, 서버 요청 → 탭), 서비스 등록·제거
{
	public const int	m_nDefaultPort	= 9091;				// 기본 Server TCP 포트 (공통 Global.GetDefaultPort)
	public const string	m_sPeerOff		= "Server Off";	// 상대 (서버) 끊김 표시 (공통 Global.GetErrorText)
	public const string	m_sSelfError	= "Client Error";	// 그 밖 오류 표시 (공통 Global.GetErrorText)
	public static readonly (string sKey, string sFile)[]	m_arrConfigSplit	= Array.Empty<(string, string)>();	// 따로 관리 설정 (없음, 공통 Config)

	private const int	m_nViewWaitSec		= 10;		// 보기 동시 처리 초과 시 대기 (초, 초과: 거절)
	private const int	m_nViewConcurrent	= 2;		// 보기 동시 처리 수

	private const string	m_sServiceName			= "HMXClient";							// 서비스 이름 (표시 이름 같음, -install·-uninstall·-start·-stop)
	private const string	m_sServiceDescription	= "로그 감시·바이너리 배포 및 온오프 관리";	// 서비스 설명
	private const int		m_nServiceStopSec		= 60;									// 서비스 시작·정지 대기 (초, -uninstall·-start·-stop, 실행 기능 Git·종료 대기 포함)

	private static readonly SemaphoreSlim						m_kViewLock = new SemaphoreSlim(m_nViewConcurrent, m_nViewConcurrent);	// 보기 동시 m_nViewConcurrent 건

	public static int Run(string[] args)	// 진입: 기본 설정·설정 파일 로딩 후 인자별 실행: -install (서비스 등록·시작) | -uninstall (서비스 정지·제거) | -start·-stop (서비스 시작·정지) | 그 외 (클라 실행) (0: 정상 종료, 1: 설정 오류·등록 실패)
	{
		Console.OutputEncoding = Encoding.UTF8;	// 한글 콘솔 출력

		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);	// CP949 로그 파일 읽기

		SetMasterKey("");	// 마스터 키 비움: 서버에서 받음 (첫 접속부터 X초마다 요청, 메모리만, 그 전: sCode 복호화·기능 안 함)

		if (!Config.Load())
		{
			return 1;
		}

		string sArg = args.Length > 0 ? args[0].ToLowerInvariant() : "";	// 1번 인자

		if (sArg == "-install")
		{
			return Install();
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

		string	sIP		= Config.GetString("sIP");
		int		nPort	= Config.GetInt("nPort", GetDefaultPort());

		string[]	arrLogFolder	= Config.GetArray("aLogFolder");
		string[]	arrLogType		= Config.GetArray("aLogType");
		int			nLogSend		= Config.GetInt("nLogSend", 100);
		int			nLogRead		= Config.GetInt("nLogRead", 1000);

		if (arrLogFolder.Length == 0 || arrLogFolder.Any(string.IsNullOrWhiteSpace))
		{
			Log.Write(ERROR, $"설정 오류: aLogFolder 없음·빈 항목 ({GetConfigFile()})");

			return 1;
		}

		if (Uri.TryCreate(sIP, UriKind.Absolute, out Uri? kUri) && kUri.Host != "")	// 주소 형태 (예: http://호스트): 호스트만
		{
			sIP = kUri.Host;
		}

		if (!IPAddress.TryParse(sIP, out _) && Uri.CheckHostName(sIP) == UriHostNameType.Unknown)	// 아이피·호스트명 형식 아님
		{
			Log.Write(ERROR, $"설정 오류: sIP 형식 오류 ({sIP})");

			return 1;
		}

		if (nPort <= 0 || nPort > 65535)
		{
			Log.Write(ERROR, $"설정 오류: nPort 범위 오류 ({nPort}, 1~65535)");

			return 1;
		}

		if (nLogSend <= 0 || nLogRead <= 0)
		{
			Log.Write(ERROR, $"설정 오류: nLogSend·nLogRead 범위 오류 ({nLogSend}·{nLogRead}, 1 이상)");

			return 1;
		}

		if (GetMachine().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)	// 서버·클라 받은 진행 머신 폴더 이름
		{
			Log.Write(ERROR, $"설정 오류: sMachine 형식 오류 ({GetMachine()}, 폴더 이름에 못 쓰는 글자)");

			return 1;
		}

		if (Config.GetChildren("aField").Any())	// 예전 설정: 안 씀 (서버가 내려줌)
		{
			Log.Write(WARN_, $"설정 aField: 안 씀 (서버 Field.json aField → 접속 때 받음, {GetConfigFile()} 에서 지워도 됨)");
		}

		Create(sIP, nPort, arrLogFolder, Config.GetArray("aBlockQuery"), Config.GetArray("aBlockSearch"), Config.GetArray("aBlockFile"), Config.GetArray("aBlockDump"), arrLogType, nLogSend, nLogRead);	// 탭별 막는 경로 (없음: 막지 않음)

		Service(args, new Main());	// 기능 (타이머·Execute 포함): 마스터 키 받은 뒤 (ExecuteAsync)

		Timer.Stop();

		return 0;
	}

	static void Execute()
	{
		Log.Write(NOTI_, "앱: 실행");	// 차후 작업할 코드 아래에 추가
	}

	static void Create(string sIP, int nPort, string[] arrLogFolder, string[] arrBlockQuery, string[] arrBlockSearch, string[] arrBlockFile, string[] arrBlockDump, string[] arrLogType, int nLogSend, int nLogRead)	// 설정 → 탭 공통 (aLogFolder)·TCP·탭 (탭별 막는 경로)·반영 탭 (Redis)·날짜 폴더 삭제 (aDeleteFolder)
	{
		Tab.m_arrLogRoot = arrLogFolder.Select(Tab.GetFullFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

		TCPClient.Init(sIP, nPort, Tab.m_arrLogRoot);
		TabQuery.Init(arrBlockQuery, arrLogType, nLogSend, nLogRead);
		TabSearch.Init(arrBlockSearch);
		TabDump.Init(arrBlockDump);
		TabFile.Init(arrBlockFile);
		TabProcess.Init();
		Clean.Init();
	}

	static int Install()	// 서비스 등록 (자동 시작) → 설명 → 시작 (0: 등록 완료, 1: 실패)
	{
		string sExe = Environment.ProcessPath ?? "";	// 등록할 실행 파일 (현재 exe)

		if (!string.Equals(Path.GetFileNameWithoutExtension(sExe), GetProjectName(), StringComparison.OrdinalIgnoreCase))	// dotnet 등 다른 실행 파일: 조기 반환
		{
			Log.Write(ERROR, $"서비스 등록은 게시된 {GetProjectName()}.exe 로 실행 (현재: {sExe})");

			return 1;
		}

		if (RunSc("create", m_sServiceName, "binPath=", $"\"{sExe}\"", "start=", "auto", "DisplayName=", m_sServiceName) != 0)	// 등록 실패 (이미 등록: -uninstall 후 다시)
		{
			return 1;
		}

		RunSc("description", m_sServiceName, m_sServiceDescription);

		Log.Write(NOTI_, $"서비스 등록 완료: {m_sServiceName} ({sExe})");

		if (RunSc("start", m_sServiceName) != 0)
		{
			Log.Write(WARN_, $"서비스 시작 실패 (sc start {m_sServiceName} 로 수동 시작)");

			return 0;
		}

		Log.Write(NOTI_, "서비스 시작 완료");

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

	static void Service(string[] args, Main pMain)	// 서비스 SCM 연동 + 호스트 실행 (서비스 중지·Ctrl+C 까지 대기)
	{
		try
		{
			using System.Diagnostics.Process kProcess = System.Diagnostics.Process.GetCurrentProcess();

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
		kBuilder.Services.AddHostedService(_ => pMain);	// 호스트와 함께 시작·중지
		kBuilder.Logging.SetMinimumLevel(LogLevel.Warning);	// 기본 로그 최소화 (클라 로그는 콘솔 직접 출력)

		kBuilder.Build().Run();
	}

	protected override async Task ExecuteAsync(CancellationToken kStop)	// 반영 탭·날짜 폴더 삭제 시작 (따로), 탭 순서로 준비 (쿼리 → 서치 → 덤프 → 파일) 뒤 서버 접속
	{
		Log.Write(INFO_, $"클라 시작: aLogFolder {string.Join(", ", Tab.m_arrLogRoot)} -> 서버 {TCPClient.m_sIP}:{TCPClient.m_nPort}");

		foreach ((string sKey, string[] arrBlock) in new[] { ("aBlockQuery", TabQuery.m_arrBlockQuery), ("aBlockSearch", TabSearch.m_arrBlockSearch), ("aBlockFile", TabFile.m_arrBlockFile), ("aBlockDump", TabDump.m_arrBlockDump) }.Where(kItem => kItem.Item2.Length > 0))	// 탭별 막는 경로 (있는 것만)
		{
			Log.Write(INFO_, $"막는 경로: {sKey} {string.Join(", ", arrBlock)}");
		}

		string? sMasterKey = await TCPClient.KeyLoop(kStop);	// 마스터 키: 서버 첫 접속부터 X초마다 요청 → 받을 때까지 다른 기능 안 함

		if (sMasterKey == null)	// 받기 전 종료 요청
		{
			Log.Write(INFO_, "클라 종료");

			return;
		}

		SetMasterKey(sMasterKey);	// 메모리만 (파일 저장 안 함)

		if (!Config.Load())	// 받은 키로 sCode 복호화 → sPass 메모리 (형식 오류: 앞 설정 그대로)
		{
			Log.Write(ERROR, GetMasterError());
		}

		Timer.Start();

		Execute();

		List<Task> listLoop = new List<Task>();

		try
		{
			listLoop.Add(TabProcess.ProcessLoop(kStop));	// 실행 (Redis 목록, 로그 탭 준비·서버 접속과 무관, 꺼짐: 바로 끝)
			listLoop.Add(Clean.CleanLoop(kStop));			// 날짜 폴더 삭제 (aDeleteFolder, 로그 탭 준비·서버 접속과 무관, 항목 없음: 바로 끝)
			listLoop.Add(TabQuery.LogWatchLoop(kStop));	// 쿼리 (준비 끝: m_kLogReady)

			await TabQuery.m_kLogReady.Task.WaitAsync(kStop);

			Log.Write(INFO_, $"서치 시작: 서치 대상 폴더 {TabSearch.m_arrLogFolder.Length}개 (날짜 폴더 *_yyyyMMdd_HH.log, 기본 {TabSearch.m_nSearchDefault:N0}건·최대 {TabSearch.m_nSearchLimitSec}초)");

			listLoop.Add(TabDump.WatchLoop(kStop));	// 덤프 (준비 끝: m_kDumpReady)

			await TabDump.m_kDumpReady.Task.WaitAsync(kStop);

			Log.Write(INFO_, $"파일 시작: aLogFolder {Tab.m_arrLogRoot.Length}개 (하위 폴더 보기·다운)");

			listLoop.Add(TabDump.FlushLoop(kStop));
			listLoop.Add(TCPClient.ConnectLoop(kStop));	// 서버 접속: 준비 다 끝난 뒤
		}
		catch (OperationCanceledException) when (kStop.IsCancellationRequested)	// 준비 중 종료 요청
		{
		}

		await Task.WhenAll(listLoop);

		Log.Write(INFO_, "클라 종료");
	}

	public static async Task OnRequest(string sLine, LineWriter kWriter, CancellationToken kStop)	// 서버 요청 처리 → 결과 전송 (모르는 종류: 무시)
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

			if (sType == "Pause" || sType == "Resume")	// 서버 적재 밀림 (다른 모양 JSON: 보통은 ReadLoop 가 바로 처리)
			{
				OnPause(sType == "Pause");

				return;
			}

			if (sType == "Count")	// 접속 때 Count 답: 서버 받은 진행 {arrCount: [{sFolder, sRel, nLine}], aField: 줄 필드} (SyncCount 가 기다림, 늦은 답: 무시)
			{
				TaskCompletionSource<Dictionary<string, (string sRel, long nLine)>>? kReply = TabQuery.m_kCountReply;	// 기다리는 접속

				if (kReply != null)
				{
					TabQuery.ApplyServerField(kRoot);	// 줄 필드 먼저 (답 뒤 Log 전송 시작)

					kReply.TrySetResult(TabQuery.ReadServerCount(kRoot));
				}

				return;
			}

			if (sType == "Alive")	// 얼라이브 답 (1분마다)·서버 필드 정의 바뀜 (필드 버튼): {aField} → 바뀜만 다음 Log 부터 새 줄 필드 (받은 진행 없음: 보정 안 함)
			{
				TabQuery.ApplyServerField(kRoot);

				return;
			}

			if (sType == "CountAdd")	// 발견 등록 답: {arrFolder: 등록 폴더, arrCount: 받은 진행} → 로그 감시 스레드가 등록·보정 후 그 폴더도 전송
			{
				string[] arrFolder = kRoot.TryGetProperty("arrFolder", out JsonElement kFolders) && kFolders.ValueKind == JsonValueKind.Array ? kFolders.EnumerateArray().Where(kFolder => kFolder.ValueKind == JsonValueKind.String).Select(kFolder => kFolder.GetString() ?? "").ToArray() : Array.Empty<string>();	// 등록 폴더

				TabQuery.m_queServerCount.Enqueue((Interlocked.Read(ref TabQuery.m_nCountGen), arrFolder, TabQuery.ReadServerCount(kRoot)));	// 이 접속 답 (접속 바뀜: 꺼낼 때 버림)

				return;
			}

			if (sType == "Stop")	// 검색 중지 (이미 끝났으면 무시)
			{
				if (TabSearch.m_hmapJob.TryGetValue(nReqId, out SearchJob? kStopJob))
				{
					kStopJob.m_kCancel.Cancel();
				}

				return;
			}

			if (sType == "Search")
			{
				SearchJob kJob = new SearchJob();

				TabSearch.m_hmapJob[nReqId] = kJob;

				using CancellationTokenSource	kProgressStop	= CancellationTokenSource.CreateLinkedTokenSource(kStop);	// 진행 알림 끝 (검색 끝·연결 끊김)
				Task							kProgress		= TabSearch.SendProgress(nReqId, kJob, kWriter, kProgressStop.Token, kStop);

				try
				{
					kResult = await TabSearch.Search(nReqId, GetLong(kRoot, "nStart"), GetLong(kRoot, "nEnd"), GetText(kRoot, "sKey"), (int)GetLong(kRoot, "nMax"), (int)GetLong(kRoot, "nLimit"), kJob, kStop);
				}
				finally
				{
					kProgressStop.Cancel();

					await kProgress;	// 진행 알림이 결과보다 뒤에 가지 않게

					TabSearch.m_hmapJob.TryRemove(new KeyValuePair<long, SearchJob>(nReqId, kJob));	// 내 것만 (같은 번호 새 검색 유지), m_kCancel 은 Dispose 안 함 (늦은 Stop 안전)
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
						kResult = TabSearch.View(nReqId, GetText(kRoot, "sPath"), GetLong(kRoot, "nStart"), (int)GetLong(kRoot, "nCount"));
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
							"Dump"		=> TabDump.DumpList(nReqId),
							"DumpRead"	=> TabDump.DumpRead(nReqId, GetText(kRoot, "sPath"), GetLong(kRoot, "nOffset"), (int)GetLong(kRoot, "nCount")),
							"Files"		=> TabFile.FileList(nReqId, GetText(kRoot, "sPath")),
							"FileRead"	=> TabFile.FileRead(nReqId, GetText(kRoot, "sPath"), GetLong(kRoot, "nOffset"), (int)GetLong(kRoot, "nCount")),
							_			=> TabDump.DumpDelete(nReqId, GetText(kRoot, "sPath")),
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
				await kWriter.WriteJsonAsync(kResult, Define.m_kJsonOption, kStop);	// 결과를 큰 문자열로 만들지 않고 바로 전송
			}
			finally
			{
				(kResult as IDisposable)?.Dispose();	// 조각 읽기 결과: 빌린 버퍼 반환 (ChunkResult)
			}
		}
		catch (Exception kException) when (!kStop.IsCancellationRequested)	// 형식 오류·전송 실패
		{
			Log.Write(ERROR, $"요청 처리 오류: {kException.Message}");
		}
		catch (Exception)	// 연결 종료
		{
		}
	}

	public static async Task OnConnect(LineWriter kWriter, CancellationToken kStop)	// 접속 (TCPClient, Hello 뒤): 쿼리 관리 목록 → 서버 → 받은 진행 보정 → 로그 감시 전송 시작
	{
		await TabQuery.SyncCount(kWriter, kStop);	// 관리 목록 → 서버 (목록 밖 Count 지움) → 서버 받은 진행으로 보정 (그 뒤 로그 전송)

		TabQuery.m_bLogPause	= false;		// 새 접속: 멈춤 해제 (밀림 중이면 서버가 Pause 재전송)
		TabQuery.m_kLogWriter	= kWriter;	// 로그 감시 전송 시작 (Hello·Count 뒤)
	}

	public static void OnDisconnect()	// 끊김 (TCPClient): 로그 감시 전송 멈춤
	{
		TabQuery.m_kLogWriter	= null;		// 로그 감시 전송 멈춤 (끊김)
		TabQuery.m_bLogPause	= false;
	}

	public static string GetAliveLine()	// 얼라이브 한 줄 (TCPClient, X초마다): 쿼리 관리 목록
	{
		return TabQuery.GetAliveLine();
	}

	public static void OnPause(bool bPause)	// 서버 밀림 Pause·Resume (TCPClient 수신 순서대로): 쿼리 로그 감시 멈춤·재개
	{
		TabQuery.SetLogPause(bPause);
	}

	public static void Post(string sLine)	// 전송 대기 (탭 → TCPClient, 끊긴 동안에도 보관): 덤프 파일 이벤트
	{
		TCPClient.m_queSend.Writer.TryWrite(sLine);
	}
}

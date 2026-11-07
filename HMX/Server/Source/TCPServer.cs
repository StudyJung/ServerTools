class TCPServer	// Client TCP 수신: 한 줄 = JSON 1건 (Hello: 머신 등록·필드 순서, File: 콘솔 출력, Log: 원문 저장, Ping: 수신 시각 갱신, *Result: 요청 결과), 접속 목록 관리, 클라 요청(검색·보기)
{
	private readonly int	m_nPort;								// 수신 포트 (0: 사용 안 함)
	private readonly ConcurrentDictionary<long, ClientInfo>	m_hmapClient;	// 접속 클라 (접속 번호 → 정보)
	private long			m_nClientSeq;							// 접속 번호 발급
	private long			m_nReqSeq;								// 요청 번호 발급
	private Task			m_kAcceptTask	= Task.CompletedTask;	// 접속 대기 (종료 때 끝 대기)
	private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, SearchTrack>>	m_hmapTrack	= new ConcurrentDictionary<string, ConcurrentDictionary<long, SearchTrack>>();	// 진행 중인 검색 (페이지 sToken → 머신별 진행 (요청 번호 → 진행))

	private const int	m_nIdleSec		= 90;					// 클라 무수신 제한 (초, 클라 Ping 주기 × 3, 초과: 연결 종료)
	private const int	m_nSendWaitSec	= 10;					// 알림 한 줄 (Pause·Resume·Count 답) 전송 제한 (초, 넘음: 수신 안 하는 클라 → 연결 정리)
	private const int	m_nStopWaitMs	= 50;					// 종료 때 클라 수신 끝 확인 간격 (ms)
	private const int	m_nStopLogSec	= 10;					// 종료 대기 로그 간격 (초)
	private const int	m_nLineMax		= 1536 * 1024 * 1024;	// 수신 한 줄 최대 바이트 (클라가 글자 수 합 m_nSearchTextTotal 에서 끊음 → 한글 3바이트도 안쪽, 초과: 연결 종료)

	private static readonly (string sLine, string sNo, string sField)	m_kTcpKey	= ("arrLine", "arrNo", "arrField");	// TCP Log 줄·번호·필드 배열 키 (Client 프로토콜, 변경 금지)
	private static readonly (string sLine, string sNo, string sField)	m_kHttpKey	= ("aLine", "aNo", "aField");		// HTTP Log 줄·번호·필드 배열 키 (/api/external/log)

	private static TCPServer?	m_kInstance;		// 수신 1개 (TabQuery 적재 부하 → 분당 상위 클라 Pause·모든 클라 Resume, 타이머 → 클라 분당 교체)
	private static bool			m_bQuery	= true;	// 쿼리 탭 켜짐 (aMenu, 꺼짐: 접속 때 Pause·Log·받은 진행 안 받음 → TabQuery 안 건드림)

	public TCPServer(int nPort, bool bQuery)
	{
		m_nPort			= nPort;
		m_hmapClient	= new ConcurrentDictionary<long, ClientInfo>();
		m_kInstance		= this;
		m_bQuery		= bQuery;
	}

	public static void WriteMinute()	// 타이머 스레드 (1분마다): 클라마다 이번 분 → 지난 1분 원자 교체 + 분당 로그 합계 1줄 (머신별: /api/client)
	{
		if (m_kInstance == null)
		{
			return;
		}

		List<KeyValuePair<long, ClientInfo>>	listClient	= m_kInstance.m_hmapClient.OrderBy(kPair => kPair.Key).ToList();	// 접속 번호 순 스냅샷

		if (listClient.Count == 0)
		{
			return;
		}

		foreach (KeyValuePair<long, ClientInfo> kPair in listClient)
		{
			kPair.Value.TakeMinute();
		}

		long	nMsg	= listClient.Sum(kPair => Interlocked.Read(ref kPair.Value.m_nLastMinMsg));
		long	nLine	= listClient.Sum(kPair => Interlocked.Read(ref kPair.Value.m_nLastMinLine));
		long	nByte	= listClient.Sum(kPair => Interlocked.Read(ref kPair.Value.m_nLastMinByte));

		Log.Write(INFO_, $"클라 분당: 접속 {listClient.Count}개, 메시지 {nMsg:N0}건 · Log {nLine:N0}줄 · {nByte / 1048576.0:N1} MB (멈춤 {listClient.Count(kPair => Volatile.Read(ref kPair.Value.m_nPaused) != 0)}대)");
	}

	public static (int nPause, int nSending, int nTotal) PauseTop(int nPercent)	// 상한 넘침: 전송 중 (안 멈춘) 클라 중 분당 Log 줄 (지난 1분 + 이번 분) 많은 순 상위 X% 더 Pause (전송 중 수 기준 올림 → 남은 1대: 그것도, 반환: 이번에 멈춘 수·멈추기 전 전송 중 수·접속 수)
	{
		List<ClientInfo>	listClient	= m_kInstance?.m_hmapClient.Values.Where(kInfo => kInfo.m_kWriter != null).ToList() ?? new List<ClientInfo>();	// 접속 클라
		List<ClientInfo>	listSending	= listClient.Where(kInfo => Volatile.Read(ref kInfo.m_nPaused) == 0).ToList();	// 전송 중 (안 멈춘) 클라
		int					nTop		= (int)Math.Ceiling(listSending.Count * nPercent / 100.0);						// 이번에 멈출 수
		int					nPause		= 0;

		foreach (ClientInfo kInfo in listSending.Select(kInfo => (kInfo, nLoad: kInfo.GetLoad())).OrderByDescending(kItem => kItem.nLoad).Take(nTop).Select(kItem => kItem.kInfo))
		{
			if (Interlocked.Exchange(ref kInfo.m_nPaused, 1) == 0)	// 그 사이 다른 곳이 멈추지 않았을 때만 보냄
			{
				SendLine(kInfo, JsonSerializer.Serialize(new { sType = "Pause" }));

				++nPause;
			}
		}

		return (nPause, listSending.Count, listClient.Count);
	}

	public static int ResumeAll()	// 밀림 풀림: 모든 클라에 Resume (클라: 멈춘 상태만 재개, 이미 전송 중: 무시), 멈춤 표시 지움 (반환: 보낸 수)
	{
		int	nCount	= 0;	// 보낸 수

		foreach (ClientInfo kInfo in m_kInstance?.m_hmapClient.Values ?? Enumerable.Empty<ClientInfo>())
		{
			if (kInfo.m_kWriter == null)
			{
				continue;
			}

			Interlocked.Exchange(ref kInfo.m_nPaused, 0);

			SendLine(kInfo, JsonSerializer.Serialize(new { sType = "Resume" }));

			++nCount;
		}

		return nCount;
	}

	private static string GetAliveLine()	// 얼라이브 답 {"sType":"Alive","aField":[...],"sTime":""} (Count 답과 같은 aField, 받은 진행 없음 → 클라 보정 안 함)
	{
		return JsonSerializer.Serialize(new { sType = "Alive", aField = TabQuery.GetFieldRule(), sTime = GetNowText() }, Define.m_kJsonOption);
	}

	public static int SendAliveAll()	// 필드 정의 바뀜 (필드 버튼): 접속 클라 모두에 얼라이브 답 바로 (클라 1분 얼라이브 안 기다림 → 클라: 바뀜만 다음 Log 부터 새 정의, 예전 클라: 무시, 반환: 보낸 수)
	{
		string	sLine	= GetAliveLine();	// 얼라이브 답
		int		nCount	= 0;				// 보낸 수

		foreach (ClientInfo kInfo in m_kInstance?.m_hmapClient.Values ?? Enumerable.Empty<ClientInfo>())
		{
			if (kInfo.m_kWriter == null)
			{
				continue;
			}

			SendLine(kInfo, sLine);

			++nCount;
		}

		return nCount;
	}

	private static void SendLine(ClientInfo kInfo, string sLine)	// 클라 1대에 한 줄 (기다리지 않음, m_nSendWaitSec 초 안 보내짐 (수신 안 하는 클라)·전송 오류: 연결 정리 → 보낼 줄 대기 안 쌓임, 끊김은 ReadLoop 가 처리)
	{
		_ = SendLineAsync(kInfo, sLine);
	}

	private static async Task SendLineAsync(ClientInfo kInfo, string sLine)	// SendLine 전송 (시간 제한, 예외 밖으로 안 냄)
	{
		if (kInfo.m_kWriter == null)
		{
			return;
		}

		try
		{
			using CancellationTokenSource kTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(m_nSendWaitSec));	// 전송 제한 (앞 전송 대기 포함)

			await kInfo.m_kWriter.WriteAsync(sLine, kTimeout.Token);
		}
		catch (Exception)	// 시간 초과·끊김: 줄 일부 전송 가능 → 연결 정리 (깨진 줄 방지, 클라 재접속)
		{
			kInfo.m_kClient?.Close();
		}
	}

	public int GetPort()	// 수신 포트
	{
		return m_nPort;
	}

	public bool Start(CancellationToken kStop)	// 수신 시작: 모든 인터페이스 바인딩 (원격 Client 접속) (false: nPort 미설정·범위 오류·포트 사용 중)
	{
		if (m_nPort <= 0 || m_nPort > 65535)
		{
			return false;
		}

		TcpListener	kListener	= new TcpListener(IPAddress.Any, m_nPort);

		try
		{
			kListener.Start();
		}
		catch (SocketException kException)	// 포트 사용 중 등
		{
			Log.Write(ERROR, $"TCP 수신 실패: {m_nPort} ({kException.Message})");

			return false;
		}

		kStop.Register(kListener.Stop);	// 웹 서버 종료 시 수신 중지

		m_kAcceptTask = AcceptLoop(kListener, kStop);

		return true;
	}

	public async Task WaitStop()	// 종료 (수신 중지 뒤): 접속 대기 끝 → 모든 클라 수신 끝날 때까지 대기 (끊긴 클라 정리 표시까지 큐에 넣음 → 이후 Log 안 들어옴, 워커 정리 전에)
	{
		await m_kAcceptTask;

		long	nLogTick	= Environment.TickCount64;	// 마지막 대기 로그

		while (!m_hmapClient.IsEmpty)
		{
			if (Environment.TickCount64 - nLogTick >= m_nStopLogSec * 1000L)
			{
				nLogTick = Environment.TickCount64;

				Log.Write(INFO_, $"종료 정리 대기: 클라 수신 {m_hmapClient.Count}개");
			}

			await Task.Delay(m_nStopWaitMs);
		}
	}

	public string[] GetMachineList()	// 접속 머신 이름 (접속 순, 빈 이름·겹침 뺌, 외부 API 서치 머신 없음: 전체)
	{
		return m_hmapClient.OrderBy(kPair => kPair.Key).Select(kPair => kPair.Value.m_sMachine).Where(sMachine => !string.IsNullOrEmpty(sMachine)).Distinct().ToArray();
	}

	public List<object> GetClientList()	// 접속 클라 목록 (접속 순)
	{
		return m_hmapClient.OrderBy(kPair => kPair.Key).Select(kPair => (object)new
		{
			nId				= kPair.Key,
			sMachine		= kPair.Value.m_sMachine,
			sEndPoint		= kPair.Value.m_sEndPoint,
			sDir			= kPair.Value.m_sDir,
			sConnectTime	= kPair.Value.m_kConnectTime.ToString("yyyy-MM-dd HH:mm:ss"),
			sLastTime		= kPair.Value.m_kLastTime.ToString("yyyy-MM-dd HH:mm:ss"),
			nEventCount		= kPair.Value.m_nEventCount,
			sLastEvent		= kPair.Value.m_sLastEvent,
			nRecvMsg		= Interlocked.Read(ref kPair.Value.m_nRecvMsg),		// 받은 메시지 누적
			nRecvByte		= Interlocked.Read(ref kPair.Value.m_nRecvByte),	// 받은 바이트 누적
			nRecvLine		= Interlocked.Read(ref kPair.Value.m_nRecvLine),	// 받은 Log 줄 누적
			nMinMsg			= Interlocked.Read(ref kPair.Value.m_nLastMinMsg),	// 분당 메시지 (지난 1분)
			nMinByte		= Interlocked.Read(ref kPair.Value.m_nLastMinByte),	// 분당 바이트 (지난 1분)
			nMinLine		= Interlocked.Read(ref kPair.Value.m_nLastMinLine),	// 분당 Log 줄 (지난 1분)
			bPaused			= Volatile.Read(ref kPair.Value.m_nPaused) != 0,	// 밀림 Pause 받은 상태
		}).ToList();
	}

	public async Task<(byte[]? arrResult, string sError)> Request(string sMachine, Dictionary<string, object> hmapRequest, int nWaitSec, ConcurrentDictionary<long, SearchTrack>? hmapTrack = null)	// 머신에 요청 → 결과 대기 (같은 머신 여러 접속: 최근 접속, 미접속·시간 초과·끊김: sError, hmapTrack: 진행 조회·중지)
	{
		ClientInfo?	kInfo	= m_hmapClient.Where(kPair => kPair.Value.m_kWriter != null && string.Equals(kPair.Value.m_sMachine, sMachine, StringComparison.OrdinalIgnoreCase)).OrderByDescending(kPair => kPair.Key).Select(kPair => kPair.Value).FirstOrDefault();

		if (kInfo == null || kInfo.m_kWriter == null)	// 접속 안 된 머신
		{
			return (null, "접속 안 된 머신");
		}

		long							nReqId	= Interlocked.Increment(ref m_nReqSeq);	// 요청 번호
		TaskCompletionSource<byte[]>	kWait	= new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);	// 결과 대기 (클라 결과 JSON 바이트)

		kInfo.m_hmapPending[nReqId]	= kWait;
		hmapRequest["nReqId"]		= nReqId;

		SearchTrack?	kTrack	= hmapTrack == null ? null : new SearchTrack(kInfo, nReqId);	// 진행 조회·중지 (페이지 sToken 의 머신별 진행)

		if (kTrack != null)
		{
			hmapTrack![nReqId]			= kTrack;
			kInfo.m_hmapTrack[nReqId]	= kTrack;
		}

		using CancellationTokenSource kTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(nWaitSec));	// 전송 + 결과 대기 시간 제한

		bool	bSent	= false;	// 요청 전송 완료

		try
		{
			await kInfo.m_kWriter.WriteAsync(JsonSerializer.Serialize(hmapRequest), kTimeout.Token);

			bSent = true;

			byte[]	arrResult	= await kWait.Task.WaitAsync(kTimeout.Token);

			return (arrResult, "");
		}
		catch (OperationCanceledException) when (kTimeout.IsCancellationRequested)	// 응답 없음
		{
			if (!bSent)	// 클라가 수신을 안 해 전송이 막힘: 연결 정리 (ReadLoop 종료 → 클라 재접속)
			{
				kInfo.m_kClient?.Close();
			}

			return (null, $"응답 시간 초과 ({nWaitSec}초)");
		}
		catch (Exception kException)	// 전송 실패·연결 끊김
		{
			return (null, GetErrorText(kException, $"클라 요청 {sMachine}"));
		}
		finally
		{
			kInfo.m_hmapPending.TryRemove(nReqId, out _);

			if (kTrack != null)	// 진행 등록 해제
			{
				hmapTrack!.TryRemove(nReqId, out _);
				kInfo.m_hmapTrack.TryRemove(nReqId, out _);
			}
		}
	}

	public ConcurrentDictionary<long, SearchTrack>? BeginTrack(string? sToken)	// 검색 1건 진행 등록 → 머신별 진행 보관 (sToken 없음·64자 초과: null → 진행 조회·중지 안 됨)
	{
		if (sToken == null || sToken.Length > 64)
		{
			return null;
		}

		ConcurrentDictionary<long, SearchTrack>	hmapTrack	= new ConcurrentDictionary<long, SearchTrack>();	// 머신별 진행 (요청 번호 → 진행)

		m_hmapTrack[sToken] = hmapTrack;

		return hmapTrack;
	}

	public void EndTrack(string? sToken, ConcurrentDictionary<long, SearchTrack>? hmapTrack)	// 검색 1건 진행 해제 (같은 sToken 의 다음 검색 것은 남김)
	{
		if (sToken != null && hmapTrack != null)
		{
			m_hmapTrack.TryRemove(new KeyValuePair<string, ConcurrentDictionary<long, SearchTrack>>(sToken, hmapTrack));
		}
	}

	public SearchTrack[] GetTrackList(string? sToken)	// 진행 중인 머신별 검색 (없음: 빈 배열)
	{
		return sToken != null && m_hmapTrack.TryGetValue(sToken, out ConcurrentDictionary<long, SearchTrack>? hmapTrack) ? hmapTrack.Values.ToArray() : Array.Empty<SearchTrack>();
	}

	public async Task<bool> Stop(string? sToken)	// 검색 중지: 진행 중인 머신마다 Stop 전송 (false: 진행 중인 검색 없음·전송 모두 실패)
	{
		bool[]	arrSent	= await Task.WhenAll(GetTrackList(sToken).Select(StopTrack));	// 머신별 전송 결과

		return arrSent.Contains(true);
	}

	private static async Task<bool> StopTrack(SearchTrack kTrack)	// 머신 1대 검색 중지: 해당 클라에 Stop 전송 (false: 전송 실패)
	{
		if (kTrack.m_kInfo.m_kWriter == null)
		{
			return false;
		}

		try
		{
			using CancellationTokenSource kTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));	// 전송 제한

			await kTrack.m_kInfo.m_kWriter.WriteAsync(JsonSerializer.Serialize(new { sType = "Stop", nReqId = kTrack.m_nReqId }), kTimeout.Token);

			return true;
		}
		catch (Exception)	// 연결 끊김·전송 시간 초과: 줄 일부 전송 가능 → 연결 정리 (깨진 줄 방지, 클라 재접속)
		{
			kTrack.m_kInfo.m_kClient?.Close();

			return false;
		}
	}

	private async Task AcceptLoop(TcpListener kListener, CancellationToken kStop)	// 접속 대기 → 클라마다 ReadLoop 실행
	{
		while (!kStop.IsCancellationRequested)
		{
			TcpClient	kClient;	// 접속 클라

			try
			{
				kClient = await kListener.AcceptTcpClientAsync(kStop);
			}
			catch (Exception) when (kStop.IsCancellationRequested)	// 종료 요청
			{
				break;
			}
			catch (Exception kException)	// 일시 오류 (자원 부족 등): 잠깐 쉬고 계속 대기 (접속 대기가 끝나지 않게)
			{
				Log.Write(WARN_, $"TCP 접속 오류: {kException.Message}");

				try
				{
					await Task.Delay(1000, kStop);
				}
				catch (OperationCanceledException)	// 종료 요청
				{
					break;
				}

				continue;
			}

			IPAddress?	kRemote	= (kClient.Client.RemoteEndPoint as IPEndPoint)?.Address;	// 접속 IP
			string		sRemote	= kRemote == null ? "-" : (kRemote.IsIPv4MappedToIPv6 ? kRemote.MapToIPv4() : kRemote).ToString();

			if (!IPList.IsTcpAllow(sRemote))	// aWhiteTCP 에 있는 IP 만 (IP.json, 비었음: 모두 거절)
			{
				IPList.LogBlock(sRemote, "TCP");

				kClient.Dispose();

				continue;
			}

			_ = ReadLoop(kClient, kStop);
		}
	}

	private async Task ReadLoop(TcpClient kClient, CancellationToken kStop)	// 클라 한 명 수신: 줄 단위 처리 (끊김·m_nIdleSec 초 무수신·줄 길이 초과 시 종료 → 목록에서 제거)
	{
		long		nId		= Interlocked.Increment(ref m_nClientSeq);							// 접속 번호
		ClientInfo	kInfo	= new ClientInfo(kClient.Client.RemoteEndPoint?.ToString() ?? "");	// 접속 정보

		m_hmapClient[nId] = kInfo;

		Log.Write(INFO_, $"클라 접속: #{nId} {kInfo.m_sEndPoint} (접속 {m_hmapClient.Count}개)");

		try
		{
			using (kClient)
			{
				SetKeepAlive(kClient.Client);	// 클라가 FIN 없이 사라진 경우 감지

				NetworkStream	kStream	= kClient.GetStream();
				LineReader		kReader	= new LineReader(kStream, m_nLineMax);	// 줄 길이 제한 수신 (비정상 클라 메모리 고갈 방지)
				using CancellationTokenSource	kIdle	= CancellationTokenSource.CreateLinkedTokenSource(kStop);	// 무수신 시간 제한 (수신마다 다시 계산)

				kInfo.m_kClient	= kClient;
				kInfo.m_kWriter	= new LineWriter(kStream);	// 요청 전송 (검색·보기)

				kIdle.CancelAfter(TimeSpan.FromSeconds(m_nIdleSec));

				while (await kReader.ReadLineAsync(kIdle.Token) is ReadOnlyMemory<byte> kLine)
				{
					kIdle.CancelAfter(TimeSpan.FromSeconds(m_nIdleSec));

					kInfo.AddRecv(kLine.Length + 1);	// 클라별 받은 메시지·바이트 (원자)

					LogBatch?	kBatch	= OnMessage(nId, kInfo, kLine);	// Log 건: 적재 라이트 큐로 (가득: 비워질 때까지 기다림 → 이 클라 수신만 늦어짐)

					if (kBatch != null)
					{
						kInfo.AddLine(kBatch.listLine.Count);	// 클라별 받은 Log 줄 (원자)

						await TabQuery.EnqueueAsync(kBatch, kStop);

						kIdle.CancelAfter(TimeSpan.FromSeconds(m_nIdleSec));	// 큐 가득으로 기다린 동안은 무수신 시간에서 뺌
					}
				}
			}
		}
		catch (OperationCanceledException) when (!kStop.IsCancellationRequested)	// 무수신 시간 초과
		{
			Log.Write(WARN_, $"클라 무응답: #{nId} {kInfo.m_sMachine} ({m_nIdleSec}초 무수신, 연결 종료)");
		}
		catch (Exception kException) when (!kStop.IsCancellationRequested)	// 끊김 (클라 강제 종료 등)·줄 길이 초과
		{
			Log.Write(WARN_, $"클라 오류: #{nId} {kInfo.m_sMachine} ({kException.Message})");
		}
		catch (Exception)	// 종료 요청
		{
		}
		finally
		{
			foreach (TaskCompletionSource<byte[]> kWait in kInfo.m_hmapPending.Values)	// 결과 대기 중인 요청: 끊김 처리
			{
				kWait.TrySetException(new RelayException("Client Off"));	// 클라 연결 끊김
			}

			if (kInfo.m_setCount.Count > 0)	// 받은 진행 관리 끝: 이 머신 모두 메모리에서 뺌 (워커: 쌓인 Log 처리 뒤, 파일: 그대로 → 다음 접속 때 다시)
			{
				TabQuery.PruneCount(kInfo.m_sMachine, Array.Empty<string>());
			}

			m_hmapClient.TryRemove(nId, out _);	// 목록에서 뺌: 정리 표시까지 넣은 뒤 (종료 때 WaitStop 기준)

			Log.Write(INFO_, $"클라 종료: #{nId} {kInfo.m_sMachine} {kInfo.m_sEndPoint} (접속 {m_hmapClient.Count}개)");
		}
	}

	private static LogBatch? OnMessage(long nId, ClientInfo kInfo, ReadOnlyMemory<byte> kLine)	// 메시지 처리: 요청 결과: 바이트 그대로 전달, 작은 메시지만 파싱 (형식 오류: 출력 후 무시), 반환: Log 건 (그 외: null)
	{
		kInfo.m_kLastTime = GetNow();

		if (!TryGetHead(kLine.Span, out string sType, out long nReqId))	// JSON 형식 오류
		{
			Log.Write(WARN_, $"클라 형식 오류: #{nId} {kInfo.m_sEndPoint}");

			return null;
		}

		if (sType == "SearchProgress")	// 검색 진행 → 진행 기록
		{
			if (kInfo.m_hmapTrack.TryGetValue(nReqId, out SearchTrack? kTrack))
			{
				using JsonDocument kProgress = JsonDocument.Parse(kLine);

				kTrack.m_nFileTotal	= GetLong(kProgress.RootElement, "nFileTotal");
				kTrack.m_nFileDone	= GetLong(kProgress.RootElement, "nFileDone");
				kTrack.m_nFound		= GetLong(kProgress.RootElement, "nFound");
				kTrack.m_nElapsedMs	= GetLong(kProgress.RootElement, "nElapsedMs");
			}

			return null;
		}

		if (sType == "SearchResult" || sType == "ViewResult" || sType == "DumpResult" || sType == "DumpReadResult" || sType == "DumpDeleteResult" || sType == "FilesResult" || sType == "FileReadResult")	// 요청 결과 → 대기 중인 요청에 바이트 복사본 전달 (시간 초과 후 도착: 버림)
		{
			if (kInfo.m_hmapPending.TryRemove(nReqId, out TaskCompletionSource<byte[]>? kWait))
			{
				kWait.TrySetResult(kLine.ToArray());	// 수신 버퍼는 다음 줄에 재사용 → 복사
			}

			return null;
		}

		using JsonDocument kDoc = JsonDocument.Parse(kLine);	// 작은 메시지 (Hello·File·Log·Ping)

		return OnMessage(nId, kInfo, kDoc.RootElement);
	}

	private static bool TryGetHead(ReadOnlySpan<byte> kLine, out string sType, out long nReqId)	// JSON 형식 검사 (할당 없이 끝까지 읽음) + 최상위 sType·nReqId (없음: "", -1)
	{
		sType	= "";
		nReqId	= -1;

		try
		{
			Utf8JsonReader	kReader	= new Utf8JsonReader(kLine);	// 읽기 (형식 오류: JsonException)

			if (!kReader.Read() || kReader.TokenType != JsonTokenType.StartObject)	// 객체 아님
			{
				return false;
			}

			while (kReader.Read())
			{
				if (kReader.CurrentDepth != 1 || kReader.TokenType != JsonTokenType.PropertyName)	// 최상위 속성 이름만
				{
					continue;
				}

				if (kReader.ValueTextEquals("sType"u8))
				{
					kReader.Read();

					sType = kReader.TokenType == JsonTokenType.String ? kReader.GetString() ?? "" : "";
				}
				else if (kReader.ValueTextEquals("nReqId"u8))
				{
					kReader.Read();

					nReqId = kReader.TokenType == JsonTokenType.Number && kReader.TryGetInt64(out long nValue) ? nValue : -1;
				}
			}

			return true;
		}
		catch (JsonException)	// 형식 오류
		{
			return false;
		}
	}

	private static List<string> GetFolderList(JsonElement kRoot)	// 클라 Count·CountAdd 폴더 목록 (arrFolder 문자열, 빈 값 뺌)
	{
		List<string>	listFolder	= new List<string>();

		if (kRoot.TryGetProperty("arrFolder", out JsonElement kFolders) && kFolders.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement kFolder in kFolders.EnumerateArray())
			{
				if (kFolder.ValueKind == JsonValueKind.String && kFolder.GetString() is string sFolder && sFolder != "")
				{
					listFolder.Add(sFolder);
				}
			}
		}

		return listFolder;
	}

	private static List<object> GetCountReply(List<string> listFolder, List<Count> listCount)	// 폴더마다 받은 진행 (Data\Count 파일 읽기, 있는 폴더만) → 답 arrCount [{sFolder, sRel, nLine}]
	{
		List<object>	listReply	= new List<object>();

		for (int nIndex = 0; nIndex < listFolder.Count; ++nIndex)
		{
			if (listCount[nIndex].Load(null, out string sRel, out long nLine) && sRel != "" && nLine >= 0)
			{
				listReply.Add(new { sFolder = listFolder[nIndex], sRel, nLine });
			}
		}

		return listReply;
	}

	private static LogBatch? ReadLog(string sMachine, JsonElement kRoot, HashSet<string>? setCount, (string sLine, string sNo, string sField) kKey)	// Log 메시지 → Log 건 (TCP·HTTP 공통, setCount: 받은 진행 관리 폴더 (HTTP: 없음 → 관리 안 함), kKey: 배열 키 (TCP arr*, HTTP a*), 받은 줄 없음: null)
	{
		List<string>	listLine	= new List<string>();	// 받은 줄

		if (kRoot.TryGetProperty(kKey.sLine, out JsonElement kLines) && kLines.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement kLine in kLines.EnumerateArray())
			{
				if (kLine.ValueKind == JsonValueKind.String)
				{
					listLine.Add(kLine.GetString() ?? "");
				}
			}
		}

		List<long>	listNo	= new List<long>();	// 받은 줄 번호 (arrLine 과 같은 순서, 예전 클라: 없음)

		if (kRoot.TryGetProperty(kKey.sNo, out JsonElement kNos) && kNos.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement kNo in kNos.EnumerateArray())
			{
				listNo.Add(kNo.ValueKind == JsonValueKind.Number && kNo.TryGetInt64(out long nNo) ? nNo : 0);
			}
		}

		List<(string sKey, string sValue)[]>	listField	= new List<(string sKey, string sValue)[]>();	// 받은 줄 추가 필드 (arrLine 과 같은 순서, 설정 aField 키만, 예전 클라·aField 없음: 없음)

		if (kRoot.TryGetProperty(kKey.sField, out JsonElement kFields) && kFields.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement kField in kFields.EnumerateArray())
			{
				listField.Add(TabQuery.ReadLineField(kField));
			}
		}

		string	sFolder	= GetText(kRoot, "sFolder");	// 클라 대상 폴더 (예전 클라: 없음)

		return listLine.Count == 0 ? null : new LogBatch(sMachine, GetText(kRoot, "sPath"), sFolder, GetText(kRoot, "sZone"), listLine, listNo.Count == listLine.Count ? listNo : null, listField.Count == listLine.Count ? listField : null, setCount != null && setCount.Contains(sFolder));	// sZone: 예전 클라 Base:sZone (원문 파일 번호 구분만, 지금 클라: 없음 → 존은 aField 추가 필드), arrField: 설정 aField 값 (예전 클라: 없음), 받은 진행: 클라 Count 목록 폴더만
	}

	private static JsonObject GetLogQuery(IQueryCollection kQuery)	// 쿼리 문자열 → Log 메시지 (본문 JSON 과 같은 이름, aLine·aNo: 반복, 그 밖 키: 모든 줄 추가 필드)
	{
		JsonObject	kRoot	= new JsonObject();	// Log 메시지
		JsonObject	kField	= new JsonObject();	// 추가 필드 (모든 줄 같음)

		foreach (string sKey in kQuery.Keys)
		{
			string[]	arrValue	= kQuery[sKey].Select(sValue => sValue ?? "").ToArray();	// 같은 키 값들 (반복 순서)

			if (sKey == "sMachine" || sKey == "sPath" || sKey == "sFolder" || sKey == "sZone")
			{
				kRoot[sKey] = arrValue.LastOrDefault() ?? "";
			}
			else if (sKey == m_kHttpKey.sLine)
			{
				kRoot[sKey] = new JsonArray(arrValue.Select(sValue => (JsonNode?)sValue).ToArray());
			}
			else if (sKey == m_kHttpKey.sNo)
			{
				kRoot[sKey] = new JsonArray(arrValue.Select(sValue => (JsonNode?)(long.TryParse(sValue, out long nNo) ? nNo : 0)).ToArray());
			}
			else
			{
				kField[sKey] = arrValue.LastOrDefault() ?? "";
			}
		}

		if (kField.Count > 0 && kRoot[m_kHttpKey.sLine] is JsonArray kLines)
		{
			kRoot[m_kHttpKey.sField] = new JsonArray(kLines.Select(_ => (JsonNode?)kField.DeepClone()).ToArray());
		}

		return kRoot;
	}

	public static async Task<IResult> OnLog(HttpRequest kRequest)	// HTTP 로그 수신: 쿼리 문자열 (있음, GET 링크) 또는 본문 JSON {sMachine, sPath, sFolder, aLine, aNo, aField} (TCP Log 의 arr* → a* + sMachine, 추가 필드: 설정 aField 키·고정값만 → 그 밖: 400) → TCP 와 같은 처리 (ReadLog → 적재 큐, 원문 시간: 받은 시각), 받은 진행 관리 안 함
	{
		CancellationToken	kStop		= kRequest.HttpContext.RequestAborted;	// 요청 끊김
		string				sRecvTime	= GetNow().ToString("[yyyy/MM/dd HH:mm:ss.fff]", CultureInfo.InvariantCulture);	// 받은 시각 (KST, 이 요청 모든 줄 원문 시간)

		if (TabQuery.IsIngestPaused())	// 적재 밀림: TCP 우선 (HTTP 거절, 잠시 후 다시)
		{
			return Results.Json(new { sError = "적재 부하", nLine = 0 }, statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		try
		{
			LogBatch?	kBatch;	// Log 건

			using (JsonDocument kDoc = kRequest.Query.Count > 0 ? JsonDocument.Parse(GetLogQuery(kRequest.Query).ToJsonString()) : await JsonDocument.ParseAsync(kRequest.Body, cancellationToken: kStop))	// 쿼리 문자열 있음: 쿼리, 없음: 본문 JSON (적재 대기 전 해제)
			{
				JsonElement	kRoot		= kDoc.RootElement;
				string		sMachine	= kRoot.ValueKind == JsonValueKind.Object ? GetText(kRoot, "sMachine") : "";	// 보낸 머신 (TCP: Hello)

				if (sMachine == "")	// 객체 아님·머신 없음
				{
					return Results.Json(new { sError = "형식 오류", nLine = 0 }, statusCode: StatusCodes.Status400BadRequest);
				}

				if (!TabQuery.IsOriginPath(sMachine, GetText(kRoot, "sPath")))	// 머신·경로 오류 (전체 경로·.log 아님): 워커가 버림 → 미리 거절
				{
					return Results.Json(new { sError = "경로 오류", nLine = 0 }, statusCode: StatusCodes.Status400BadRequest);
				}

				string	sFieldError	= TabQuery.FindFieldError(kRoot, m_kHttpKey.sField);	// 정의에 안 맞는 추가 필드 (설정 aField 에 없는 키 (쿼리 문자열 그 밖 키 포함)·고정값 다름)

				if (sFieldError != "")	// 정해진 필드·고정값만 받음 (추가 필드 이름 늘어남 방지)
				{
					return Results.Json(new { sError = $"필드 오류 ({sFieldError})", nLine = 0 }, statusCode: StatusCodes.Status400BadRequest);
				}

				kBatch = ReadLog(sMachine, kRoot, null, m_kHttpKey);

				if (kBatch != null)	// 원문 시간 대신 받은 시각 (요청 1건 같은 시각, 보내는 쪽 시계·잘못된 시간 무시 → 과거 시간 폴더 안 늘림)
				{
					kBatch = kBatch with { sRecvTime = sRecvTime };
				}
			}

			if (kBatch != null && kBatch.listLine.Any(string.IsNullOrWhiteSpace))	// 빈 줄: 거절 (빈 원문 적재 방지)
			{
				return Results.Json(new { sError = "빈 줄", nLine = 0 }, statusCode: StatusCodes.Status400BadRequest);
			}

			if (kBatch == null)	// 받은 줄 없음
			{
				return Results.Json(new { sError = "", nLine = 0 });
			}

			await TabQuery.EnqueueAsync(kBatch, kStop);	// TCP 와 같은 적재 큐 (가득: 대기 → 응답 지연)

			return Results.Json(new { sError = "", nLine = kBatch.listLine.Count });
		}
		catch (JsonException)	// 형식 오류
		{
			return Results.Json(new { sError = "형식 오류", nLine = 0 }, statusCode: StatusCodes.Status400BadRequest);
		}
		catch (OperationCanceledException)	// 요청 끊김
		{
			return Results.Empty;
		}
	}

	private static LogBatch? OnMessage(long nId, ClientInfo kInfo, JsonElement kRoot)	// 작은 메시지 종류별 처리 (kRoot 는 호출 끝나면 해제됨, 반환: Log 건 → 적재 큐, 그 외: null)
	{
		string	sType	= GetText(kRoot, "sType");	// 메시지 종류 (Hello·Count·Alive·File·Log·Ping)

		if (sType == "Hello")	// 접속 직후 머신 등록 (예전 클라 arrField: 무시 → 필드 순서는 설정 aField)
		{
			kInfo.m_sMachine	= GetText(kRoot, "sMachine");
			kInfo.m_sDir		= GetText(kRoot, "sDir");

			Log.Write(INFO_, $"클라 등록: #{nId} {kInfo.m_sMachine}(폴더 {(kInfo.m_sDir == "" ? 0 : kInfo.m_sDir.Split('|').Length)}개)");	// sDir: 클라 aLogFolder 목록 ("|" 구분), 밀림 중 새 클라: Pause 안 보냄 (분당 상위 X% 만 멈춤)

			if (!m_bQuery && Interlocked.Exchange(ref kInfo.m_nPaused, 1) == 0)	// 쿼리 탭 꺼짐: 클라 로그 전송 멈춤 (위치 유지, Resume 없음)
			{
				SendLine(kInfo, JsonSerializer.Serialize(new { sType = "Pause" }));
			}

			return null;
		}

		if (sType == "MasterKey")	// 클라 마스터 키 요청 (클라: 첫 접속부터 X초마다, 받을 때까지, 메모리만) → 서버 메모리 마스터 키 (TCP 수신: 서버 마스터 키 뒤 엶)
		{
			string	sMasterKey	= GetMasterKey();

			SendLine(kInfo, JsonSerializer.Serialize(new { sType = "MasterKey", sKey = sMasterKey == "" ? "" : AES.Encrypt(sMasterKey, Define.m_sSolution) }));	// 솔루션명 암호화 (평문 안 보냄, 빈 키: 그대로)

			return null;
		}

		if (!m_bQuery && sType is "Count" or "Alive" or "CountAdd" or "CountDel" or "Log")	// 쿼리 탭 꺼짐: 받은 진행·필드·적재 안 함 (답 없음)
		{
			return null;
		}

		if (sType == "Count")	// 클라 관리 목록 (접속 때 1번, Hello 뒤, 한 패킷) → 목록 밖 이 머신 Count 지움 + 목록 폴더마다 받은 진행·설정 aField 한 패킷으로 답 (클라: aField 로 줄 필드, 그 위치부터 보정 후 로그 전송, 답에 없는 폴더: 클라 로직대로)
		{
			List<string>	listFolder	= GetFolderList(kRoot);	// 클라 로그 감시 대상 폴더
			List<Count>		listCount	= listFolder.Select(sFolder => new Count(kInfo.m_sMachine, sFolder)).ToList();	// 폴더마다 상태 (읽기만)

			kInfo.m_setCount.Clear();
			kInfo.m_setCount.UnionWith(listFolder);																				// 이 접속: 목록 폴더만 관리 (Log bCount)
			TabQuery.PruneCount(kInfo.m_sMachine, listFolder);																	// 메모리: 목록 밖 뺌 (워커, 앞 Log 처리 뒤)
			Count.DeleteUnused(kInfo.m_sMachine, listCount.Select(kCount => kCount.m_sFile).Where(sFile => sFile != ""));	// 파일: 목록 밖 지움

			List<object>	listReply	= GetCountReply(listFolder, listCount);	// 받은 진행 있는 폴더만

			SendLine(kInfo, JsonSerializer.Serialize(new { sType = "Count", arrCount = listReply, aField = TabQuery.GetFieldRule() }, Define.m_kJsonOption));

			Log.Write(INFO_, $"관리 목록: #{nId} {kInfo.m_sMachine} (폴더 {listFolder.Count}개, 서버 받은 진행 {listReply.Count}개 → 클라 보정)");

			return null;
		}

		if (sType == "Alive")	// 클라 얼라이브 (1분마다, Count 뒤 한 패킷 {"sType":"Alive","arrFolder":[""]}: 관리 목록) → 관리 폴더와 다름: Count 처럼 맞춤 (목록 밖: 메모리·파일 정리, 새 폴더: 관리) + 답: 필드 정의 (받은 진행 안 보냄 → 클라 보정 안 함)
		{
			List<string>	listFolder	= GetFolderList(kRoot);	// 클라 관리 목록 (지금)

			if (!kInfo.m_setCount.SetEquals(listFolder))
			{
				int	nAdd	= listFolder.Count(sFolder => !kInfo.m_setCount.Contains(sFolder));	// 새로 관리
				int	nDel	= kInfo.m_setCount.Count(sFolder => !listFolder.Contains(sFolder, StringComparer.OrdinalIgnoreCase));	// 관리 끝

				kInfo.m_setCount.Clear();
				kInfo.m_setCount.UnionWith(listFolder);																										// 이 접속: 목록 폴더만 관리 (Log bCount)
				TabQuery.PruneCount(kInfo.m_sMachine, listFolder);																							// 메모리: 목록 밖 뺌 (워커, 앞 Log 처리 뒤)
				Count.DeleteUnused(kInfo.m_sMachine, listFolder.Select(sFolder => new Count(kInfo.m_sMachine, sFolder).m_sFile).Where(sFile => sFile != ""));	// 파일: 목록 밖 지움

				Log.Write(INFO_, $"관리 맞춤: #{nId} {kInfo.m_sMachine} (얼라이브 폴더 {listFolder.Count}개, 새로 {nAdd}개·뺌 {nDel}개)");
			}

			SendLine(kInfo, GetAliveLine());

			return null;
		}

		if (sType == "CountAdd")	// 클라 새 폴더 등록 (접속 뒤 발견, 한 패킷) → 관리 목록에 더함 (지우기 없음) + 그 폴더 받은 진행 한 패킷으로 답 (클라: 답 받은 뒤 그 폴더도 로그 전송)
		{
			List<string>	listFolder	= GetFolderList(kRoot);	// 새로 발견한 폴더
			List<object>	listReply	= GetCountReply(listFolder, listFolder.Select(sFolder => new Count(kInfo.m_sMachine, sFolder)).ToList());	// 받은 진행 있는 폴더만 (보통 없음)

			kInfo.m_setCount.UnionWith(listFolder);	// 이 접속: 관리 폴더 추가 (Log bCount)

			SendLine(kInfo, JsonSerializer.Serialize(new { sType = "CountAdd", arrFolder = listFolder, arrCount = listReply }, Define.m_kJsonOption));

			Log.Write(INFO_, $"관리 추가: #{nId} {kInfo.m_sMachine} (폴더 {listFolder.Count}개 → 관리 {kInfo.m_setCount.Count}개, 서버 받은 진행 {listReply.Count}개)");

			return null;
		}

		if (sType == "CountDel")	// 클라 감시 폴더 사라짐 (폴더 삭제, 한 패킷) → 관리 목록에서 뺌 + 받은 진행 정리 (메모리·파일, Count 목록 밖과 같음, 답 없음)
		{
			List<string>	listFolder	= GetFolderList(kRoot);	// 사라진 폴더

			kInfo.m_setCount.ExceptWith(listFolder);																										// 이 접속: 관리 폴더 뺌 (Log bCount)
			TabQuery.PruneCount(kInfo.m_sMachine, kInfo.m_setCount);																						// 메모리: 목록 밖 뺌 (워커, 앞 Log 처리 뒤)
			Count.DeleteUnused(kInfo.m_sMachine, kInfo.m_setCount.Select(sFolder => new Count(kInfo.m_sMachine, sFolder).m_sFile).Where(sFile => sFile != ""));	// 파일: 목록 밖 지움

			Log.Write(INFO_, $"관리 제외: #{nId} {kInfo.m_sMachine} (사라진 폴더 {listFolder.Count}개 → 관리 {kInfo.m_setCount.Count}개, {string.Join(", ", listFolder)})");

			return null;
		}

		if (sType == "Log")	// 로그 감시 줄 (aLogType 줄만) → Log 건 (호출 쪽이 TabQuery 적재 큐에)
		{
			return ReadLog(kInfo.m_sMachine, kRoot, kInfo.m_setCount, m_kTcpKey);	// TCP·HTTP 공통 (TCP 키: arr*)
		}

		if (sType == "File")	// 파일 이벤트 (Changed: 로그 쓰기마다 → 출력 안 함 (폭주 방지), 건수·마지막 이벤트만 갱신)
		{
			string	sEvent		= GetText(kRoot, "sEvent");		// 이벤트 종류
			string	sOldPath	= GetText(kRoot, "sOldPath");	// 이름 변경 전 경로
			string	sMessage	= GetText(kRoot, "sMessage");	// 오류 메시지

			kInfo.m_nEventCount++;
			kInfo.m_sLastEvent = $"{sEvent} {GetText(kRoot, "sPath")}";

			if (sEvent == "Changed")
			{
				return null;
			}

			Log.Write(INFO_, $"[{kInfo.m_sMachine}] {kInfo.m_sLastEvent} (크기: {GetLong(kRoot, "nSize")}){(sOldPath == "" ? "" : $" (이전: {sOldPath})")}{(sMessage == "" ? "" : $" ({sMessage})")}");
		}

		return null;
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
}

class ClientInfo	// 접속 클라 정보 (ReadLoop 1개만 갱신, /api/client 조회)
{
	public readonly string			m_sEndPoint;			// 접속 주소 (아이피:포트)
	public readonly DateTimeOffset	m_kConnectTime;			// 접속 시각 (KST)
	public DateTimeOffset			m_kLastTime;			// 마지막 수신 시각 (KST)
	public string					m_sMachine		= "";	// 머신명 (Hello)
	public string					m_sDir			= "";	// 클라 aLogFolder 목록 (Hello, "|" 구분)
	public readonly HashSet<string>	m_setCount		= new HashSet<string>(StringComparer.OrdinalIgnoreCase);	// 받은 진행 관리 폴더 (클라 Count 목록, 수신 스레드만, 목록 밖·예전 클라: 관리 안 함)
	public long						m_nEventCount;			// 파일 이벤트 수
	public string					m_sLastEvent	= "";	// 마지막 파일 이벤트
	public LineWriter?				m_kWriter;				// 요청 전송 (접속 직후 설정)
	public TcpClient?				m_kClient;				// 연결 (요청 전송이 막히면 닫음)

	public long	m_nRecvMsg;		// 받은 메시지 누적 (모든 종류, Interlocked: 수신 스레드 더함 ↔ 웹·타이머 읽음)
	public long	m_nRecvByte;	// 받은 바이트 누적 (줄 + '\n')
	public long	m_nRecvLine;	// 받은 Log 줄 누적
	public long	m_nMinMsg;		// 이번 분 메시지 (1분마다 0 과 원자 교체 → m_nLastMinMsg)
	public long	m_nMinByte;		// 이번 분 바이트
	public long	m_nMinLine;		// 이번 분 Log 줄
	public long	m_nLastMinMsg;	// 지난 1분 메시지 (분당)
	public long	m_nLastMinByte;	// 지난 1분 바이트 (분당)
	public long	m_nLastMinLine;	// 지난 1분 Log 줄 (분당)
	public int	m_nPaused;		// 밀림 Pause 보냄 (1, Resume 보낼 때 0, Interlocked)

	public readonly ConcurrentDictionary<long, SearchTrack>	m_hmapTrack	= new ConcurrentDictionary<long, SearchTrack>();	// 진행 중인 검색 (요청 번호 → 진행)

	public readonly ConcurrentDictionary<long, TaskCompletionSource<byte[]>>	m_hmapPending	= new ConcurrentDictionary<long, TaskCompletionSource<byte[]>>();	// 결과 대기 중인 요청 (요청 번호 → 결과 JSON 바이트 대기)

	public ClientInfo(string sEndPoint)
	{
		m_sEndPoint		= sEndPoint;
		m_kConnectTime	= GetNow();
		m_kLastTime		= m_kConnectTime;
	}

	public void AddRecv(int nByte)	// 수신 스레드: 메시지 1건 (누적·이번 분 원자 더함)
	{
		Interlocked.Increment(ref m_nRecvMsg);
		Interlocked.Add(ref m_nRecvByte, nByte);
		Interlocked.Increment(ref m_nMinMsg);
		Interlocked.Add(ref m_nMinByte, nByte);
	}

	public void AddLine(int nLine)	// 수신 스레드: Log 줄 (누적·이번 분 원자 더함)
	{
		Interlocked.Add(ref m_nRecvLine, nLine);
		Interlocked.Add(ref m_nMinLine, nLine);
	}

	public void TakeMinute()	// 타이머 스레드 (1분마다): 이번 분 → 지난 1분 (0 과 원자 교체 → 교체 순간 수신도 다음 분에 남음)
	{
		Interlocked.Exchange(ref m_nLastMinMsg, Interlocked.Exchange(ref m_nMinMsg, 0));
		Interlocked.Exchange(ref m_nLastMinByte, Interlocked.Exchange(ref m_nMinByte, 0));
		Interlocked.Exchange(ref m_nLastMinLine, Interlocked.Exchange(ref m_nMinLine, 0));
	}

	public long GetLoad()	// 부하 순위 값: 지난 1분 + 이번 분 Log 줄 (막 접속: 이번 분만)
	{
		return Interlocked.Read(ref m_nLastMinLine) + Interlocked.Read(ref m_nMinLine);
	}
}

class LineWriter	// 줄 단위 전송 (여러 요청의 동시 전송 직렬화, 묶음 전송 시 flush 1회)
{
	private readonly StreamWriter	m_kWriter;	// UTF-8, '\n' 구분 (flush 는 WriteLinesAsync 끝에 1회)
	private readonly SemaphoreSlim	m_kLock;	// 전송 1건씩

	public LineWriter(Stream kStream)
	{
		m_kWriter	= new StreamWriter(kStream, new UTF8Encoding(false), 16 * 1024) { NewLine = "\n", AutoFlush = false };
		m_kLock		= new SemaphoreSlim(1, 1);
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

class LineReader	// 줄 단위 수신 ('\n' 구분, 한 줄 최대 바이트 제한 → 초과 시 InvalidDataException), 줄은 UTF-8 바이트 그대로 (문자열로 바꾸지 않음)
{
	private readonly Stream	m_kStream;		// 수신 스트림
	private readonly int	m_nMaxBytes;	// 한 줄 최대 바이트
	private readonly byte[]	m_arrBuffer;	// 수신 버퍼
	private int				m_nStart;		// 버퍼 안 처리 안 한 시작
	private int				m_nEnd;			// 버퍼 안 데이터 끝
	private MemoryStream	m_kLine;		// 버퍼보다 긴 줄 누적 (큰 줄 처리 후 작게 다시 생성)
	private bool			m_bLineUsed;	// 앞 줄이 m_kLine 을 사용 (다음 읽기 전에 비움)

	public LineReader(Stream kStream, int nMaxBytes)
	{
		m_kStream	= kStream;
		m_nMaxBytes	= nMaxBytes;
		m_arrBuffer	= new byte[64 * 1024];
		m_kLine		= new MemoryStream();
	}

	public async Task<ReadOnlyMemory<byte>?> ReadLineAsync(CancellationToken kStop)	// 한 줄 (끝 '\r' 제거, 연결 종료: null), 반환 메모리는 다음 ReadLineAsync 전까지만 유효
	{
		if (m_bLineUsed)	// 앞 줄 누적분 비움 (큰 줄 버퍼는 버려 메모리 반환)
		{
			m_kLine		= m_kLine.Capacity > 1024 * 1024 ? new MemoryStream() : m_kLine;
			m_bLineUsed	= false;

			m_kLine.SetLength(0);
		}

		while (true)
		{
			int	nIndex	= m_arrBuffer.AsSpan(m_nStart, m_nEnd - m_nStart).IndexOf((byte)'\n');	// 줄 끝 (처리 안 한 시작 기준)

			if (nIndex >= 0)
			{
				ReadOnlyMemory<byte>	kLine;	// 이번 줄

				if (m_kLine.Length == 0)	// 버퍼 안에 다 있음: 복사 없이
				{
					kLine = m_arrBuffer.AsMemory(m_nStart, nIndex);
				}
				else
				{
					if (m_kLine.Length + nIndex > m_nMaxBytes)
					{
						throw new InvalidDataException($"수신 줄 길이 초과 ({m_nMaxBytes} bytes)");
					}

					m_kLine.Write(m_arrBuffer, m_nStart, nIndex);

					kLine		= m_kLine.GetBuffer().AsMemory(0, (int)m_kLine.Length);
					m_bLineUsed	= true;
				}

				m_nStart += nIndex + 1;

				return kLine.Length > 0 && kLine.Span[^1] == (byte)'\r' ? kLine.Slice(0, kLine.Length - 1) : kLine;
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

			int	nRead	= await m_kStream.ReadAsync(m_arrBuffer.AsMemory(), kStop);

			if (nRead == 0)	// 연결 종료
			{
				return null;
			}

			m_nEnd = nRead;
		}
	}
}

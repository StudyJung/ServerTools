static class TCPClient	// Server TCP 접속: 접속·재접속, Hello 뒤 대기 줄 묶음 전송·얼라이브, 서버 수신 (요청·Pause·접속·끊김: Main 연결)
{
	private const int		m_nAliveSec			= 60;						// 얼라이브 주기 (초, 관리 목록 → 서버 폴더 맞춤·필드 정의 답, 무전송 Ping 대신 → 서버 무수신 제한 X초 안쪽)
	private const int		m_nRetryMinSec		= 2;						// 재접속 대기 최소 (초)
	private const int		m_nRetryMaxSec		= 5;						// 재접속 대기 최대 (초, 실패마다 2배)
	private const int		m_nRetryLogCount	= 12;						// 연속 접속 실패 로그 간격 (회, 처음 1회 + X회마다)
	private const int		m_nKeyRequestMs		= 1000;						// 마스터 키 요청 간격 (ms, 받을 때까지)
	private const string	m_sPauseLine		= "{\"sType\":\"Pause\"}";	// 서버 밀림 Pause 줄 (서버 직렬화 그대로 → 수신 루프가 바로 처리)
	private const string	m_sResumeLine		= "{\"sType\":\"Resume\"}";	// 서버 밀림 풀림 Resume 줄
	private const int		m_nQueueMax			= 10000;					// 전송 대기 최대 건수 (초과 시 오래된 것부터 버림)
	private const int		m_nBatchMax			= 200;						// 한 번에 묶어 보낼 최대 줄 수 (flush 1회)
	private const int		m_nRequestLineMax	= 1024 * 1024;				// 서버 요청 한 줄 최대 바이트 (초과: 연결 종료)

	public static string					m_sIP		= "";	// Server 아이피
	public static int						m_nPort;			// Server TCP 포트
	public static readonly Channel<string>	m_queSend	= Channel.CreateBounded<string>(new BoundedChannelOptions(m_nQueueMax) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });	// 전송 대기 (JSON 한 줄)

	private static string[]	m_arrLogRoot	= Array.Empty<string>();	// aLogFolder 전체 경로 (Hello sDir)

	public static void Init(string sIP, int nPort, string[] arrLogRoot)	// 설정 → 서버 주소·aLogFolder (Hello, Main.Create)
	{
		m_sIP			= sIP;
		m_nPort			= nPort;
		m_arrLogRoot	= arrLogRoot;
	}

	public static async Task<string?> KeyLoop(CancellationToken kStop)	// 마스터 키 받기 (서버 첫 접속부터 X초마다 요청, 접속 실패·끊김: X초 뒤 다시, 받음: 접속 닫고 키 (빈 값: 서버도 키 없음), 종료 요청: null)
	{
		string	sRequest	= JsonSerializer.Serialize(new { sType = "MasterKey", sMachine = GetMachine() });	// 요청 줄

		while (!kStop.IsCancellationRequested)
		{
			bool	bConnected	= false;	// 이번 시도 접속 성공 (요청: 접속 뒤)

			try
			{
				using TcpClient kClient = new TcpClient();

				await kClient.ConnectAsync(m_sIP, m_nPort, kStop);

				bConnected = true;

				NetworkStream	kStream		= kClient.GetStream();
				LineReader		kReader		= new LineReader(kStream, m_nRequestLineMax);
				LineWriter		kWriter		= new LineWriter(kStream);
				Task<string?>	kRead		= kReader.ReadLineAsync(kStop);	// 서버 답 대기
				long			nSendTick	= 0;	// 마지막 요청 (TickCount64)

				while (true)
				{
					if (Environment.TickCount64 - nSendTick >= m_nKeyRequestMs)	// X초마다 요청 (받을 때까지)
					{
						await kWriter.WriteAsync(sRequest, kStop);

						nSendTick = Environment.TickCount64;
					}

					if (await Task.WhenAny(kRead, Task.Delay((int)Math.Max(1, m_nKeyRequestMs - (Environment.TickCount64 - nSendTick)), kStop)) != kRead)	// 답 없음: 다음 요청
					{
						kStop.ThrowIfCancellationRequested();

						continue;
					}

					string	sLine	= await kRead ?? throw new IOException("서버가 연결 종료");

					using (JsonDocument kDoc = JsonDocument.Parse(sLine))
					{
						if (GetText(kDoc.RootElement, "sType") == "MasterKey")
						{
							return GetText(kDoc.RootElement, "sKey");
						}
					}

					kRead = kReader.ReadLineAsync(kStop);	// 다른 줄 (Pause 등): 무시
				}
			}
			catch (OperationCanceledException) when (kStop.IsCancellationRequested)
			{
				return null;
			}
			catch (Exception)	// 접속 뒤 끊김·형식 오류: 경고 (접속 실패: 서버 아직 안 뜸 → 로그 없음)
			{
				if (bConnected)
				{
					Log.Write(WARN_, GetMasterError());
				}
			}

			await Delay(m_nKeyRequestMs, kStop);
		}

		return null;
	}

	public static async Task ConnectLoop(CancellationToken kStop)	// 접속 → 전송 (끊김: 대기 후 재접속, 실패마다 대기 2배)
	{
		int	nRetrySec	= m_nRetryMinSec;	// 재접속 대기 (초)
		int	nFail		= 0;				// 연속 접속 실패 수 (로그 줄이기)

		while (!kStop.IsCancellationRequested)
		{
			bool	bConnected	= false;	// 이번 시도에서 접속 성공

			try
			{
				using TcpClient kClient = new TcpClient();

				await kClient.ConnectAsync(m_sIP, m_nPort, kStop);

				SetKeepAlive(kClient.Client);	// 서버가 FIN 없이 사라진 경우 감지

				Log.Write(INFO_, $"서버 접속: {m_sIP}:{m_nPort}{(nFail > 0 ? $" (재접속 시도 {nFail}회 후)" : "")}");

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
					Log.Write(WARN_, $"{(bConnected ? "서버 연결 끊김" : "서버 접속 실패")}: {m_sIP}:{m_nPort} ({kException.Message}, {nRetrySec}초 후 재접속{(nFail > 1 ? $", 연속 실패 {nFail}회" : "")})");
				}
			}

			await Delay(nRetrySec * 1000, kStop);

			nRetrySec = Math.Min(nRetrySec * 2, m_nRetryMaxSec);
		}
	}

	private static async Task SendLoop(TcpClient kClient, CancellationToken kStop)	// Hello·Count 후 대기 줄 묶음 전송, X초마다 얼라이브 (무전송: 얼라이브만, 서버 종료 감지: IOException)
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
				sMachine	= GetMachine(),
				sDir		= string.Join("|", m_arrLogRoot),	// aLogFolder 목록 ("|" 구분)
				nPid		= Environment.ProcessId,
				sTime		= GetNowText(),
			});

			await kWriter.WriteAsync(sHello, kLink.Token);

			await Main.OnConnect(kWriter, kLink.Token);	// 쿼리 관리 목록 → 서버 (목록 밖 Count 지움) → 서버 받은 진행 보정 → 로그 감시 전송 시작

			List<string>	listBatch	= new List<string>(m_nBatchMax);	// 한 번에 보낼 줄 (재사용)
			long			nAliveTick	= Environment.TickCount64;			// 마지막 얼라이브 (접속 직후: Count 가 같은 일)

			while (true)
			{
				listBatch.Clear();

				if (Environment.TickCount64 - nAliveTick >= m_nAliveSec * 1000L)	// X초마다 얼라이브 (전송 중에도)
				{
					nAliveTick = Environment.TickCount64;

					listBatch.Add(Main.GetAliveLine());
				}

				while (listBatch.Count < m_nBatchMax && m_queSend.Reader.TryRead(out string? sQueued))	// 쌓인 줄은 대기 없이 묶음
				{
					listBatch.Add(sQueued);
				}

				if (listBatch.Count == 0)	// 쌓인 줄 없음: 새 줄 또는 다음 얼라이브까지 대기
				{
					using CancellationTokenSource kWait = CancellationTokenSource.CreateLinkedTokenSource(kLink.Token);	// 다음 얼라이브까지

					kWait.CancelAfter((int)Math.Max(1, m_nAliveSec * 1000L - (Environment.TickCount64 - nAliveTick)));

					try
					{
						listBatch.Add(await m_queSend.Reader.ReadAsync(kWait.Token));
					}
					catch (OperationCanceledException) when (!kLink.IsCancellationRequested)	// 무전송: 얼라이브 (Ping 대신, 서버 무수신 끊김 방지)
					{
						nAliveTick = Environment.TickCount64;

						listBatch.Add(Main.GetAliveLine());
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
			Main.OnDisconnect();	// 로그 감시 전송 멈춤 (끊김)

			kLink.Cancel();

			await kReadTask;
		}
	}

	private static async Task ReadLoop(NetworkStream kStream, LineWriter kWriter, CancellationTokenSource kLink)	// 서버 수신: 요청마다 별도 처리, 서버 종료·줄 길이 초과·오류 시 kLink 취소
	{
		CancellationToken	kToken	= kLink.Token;	// 연결 단위 취소 (요청 처리에 전달)

		try
		{
			LineReader	kReader	= new LineReader(kStream, m_nRequestLineMax);

			while (await kReader.ReadLineAsync(kToken) is string sLine)
			{
				if (sLine == m_sPauseLine || sLine == m_sResumeLine)	// 밀림 Pause·Resume: 수신 순서대로 바로 (따로 처리하면 순서 뒤바뀌어 멈춘 채 남을 수 있음)
				{
					Main.OnPause(sLine == m_sPauseLine);

					continue;
				}

				_ = Task.Run(() => Main.OnRequest(sLine, kWriter, kToken));	// 요청 처리 중에도 수신 계속
			}
		}
		catch (Exception)	// 끊김·취소
		{
		}

		kLink.Cancel();
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
			int	nIndex	= Array.IndexOf(m_arrBuffer, (byte)'\n', m_nStart, m_nEnd - m_nStart);

			if (nIndex >= 0)
			{
				string	sLine	= TakeLine(nIndex);

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

			int	nRead	= await m_kStream.ReadAsync(m_arrBuffer.AsMemory(), kStop);

			if (nRead == 0)	// 연결 종료
			{
				return null;
			}

			m_nEnd = nRead;
		}
	}

	private string TakeLine(int nIndex)	// 누적분 + 버퍼의 줄 끝까지 → 문자열
	{
		int		nCount	= nIndex - m_nStart;	// 버퍼 안 줄 길이
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

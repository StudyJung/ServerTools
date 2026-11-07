static class Send	// 알람 발송 스레드 풀 X개: 공유 큐 (통계 스레드 Add) 를 한가한 스레드가 꺼내 채널별 발송 (메일·훅) → 알람 로그 + 실패: 결과 큐 (통계 스레드가 매분 꺼내 펜딩)
{
	private const int	m_nThreadCount	= 5;		// 발송 스레드 수
	private const int	m_nTimeoutMs	= 10000;	// 발송 1건 제한 (ms)
	private const int	m_nStopWaitMs	= 5000;		// 종료 때 남은 큐 처리 대기 (ms)

	public static readonly ConcurrentQueue<(AlarmJob kJob, string sError)>	m_kResult	= new ConcurrentQueue<(AlarmJob kJob, string sError)>();	// 실패 작업 (통계 스레드가 매분 꺼냄)

	private static BlockingCollection<AlarmJob>	m_kQueue	= new BlockingCollection<AlarmJob>();	// 발송 큐 (공유)
	private static Thread[]						m_arrThread	= Array.Empty<Thread>();				// 발송 스레드
	private static readonly HttpClient			m_kHttp		= new HttpClient { Timeout = TimeSpan.FromMilliseconds(m_nTimeoutMs) };	// 훅 (1개 공유)

	public static void Start()	// 발송 스레드 시작
	{
		if (m_arrThread.Length > 0)
		{
			return;
		}

		m_kQueue	= new BlockingCollection<AlarmJob>();
		m_arrThread	= Enumerable.Range(0, m_nThreadCount).Select(nIndex => new Thread(Run) { IsBackground = true, Name = $"Send{nIndex}" }).ToArray();

		foreach (Thread kThread in m_arrThread)
		{
			kThread.Start();
		}
	}

	public static List<AlarmJob> Stop()	// 종료: 새 작업 막고 남은 큐 X초 처리 → 못 보낸 작업·실패 작업 (펜딩 저장용)
	{
		m_kQueue.CompleteAdding();

		long	nEnd	= Environment.TickCount64 + m_nStopWaitMs;	// 대기 끝

		foreach (Thread kThread in m_arrThread)
		{
			kThread.Join((int)Math.Max(0, nEnd - Environment.TickCount64));
		}

		m_arrThread = Array.Empty<Thread>();

		List<AlarmJob>	listLeft	= new List<AlarmJob>();	// 못 보낸 작업

		while (m_kQueue.TryTake(out AlarmJob? kJob))
		{
			listLeft.Add(kJob);
		}

		while (m_kResult.TryDequeue(out (AlarmJob kJob, string sError) kResult))
		{
			listLeft.Add(kResult.kJob);
		}

		return listLeft;
	}

	public static void Add(AlarmJob kJob)	// 통계 스레드: 발송 큐 끝에 (한가한 스레드가 깨어 꺼냄)
	{
		m_kQueue.Add(kJob);
	}

	private static void Run()	// 발송 스레드: 큐 빌 때까지 꺼내 보냄, 비면 대기 (CPU 0)
	{
		foreach (AlarmJob kJob in m_kQueue.GetConsumingEnumerable())
		{
			string	sError	= "";	// 실패 사유 (성공: "")

			++kJob.nTry;

			try
			{
				SendJob(kJob);
			}
			catch (Exception kException)
			{
				sError = GetErrorText(kException, $"알람 발송 #{kJob.nID} {kJob.sChannel}");
			}

			string	sResult	= sError != "" ? "Failed" : (kJob.nRetryOf != 0 ? "Resent" : "Sent");	// 발송 결과

			AlarmLog.Write(kJob, sResult, sError);
			AlarmLog.WriteHistory(kJob, sResult, sError);	// 알람 기록 (날짜·제목·내용·결과, Data\Alarm\history.jsonl 최신 X건)

			if (sError != "")
			{
				m_kResult.Enqueue((kJob, sError));
			}
		}
	}

	public static int GetQueueCount()	// 못 보낸 발송 큐 수
	{
		return m_kQueue.Count;
	}

	public static string SendNow(AlarmJob kJob)	// 바로 보냄 (테스트 발송, 요청 스레드, 알람 기록, 반환: 실패 사유·성공 "")
	{
		string	sError	= "";	// 실패 사유 (성공: "")

		try
		{
			SendJob(kJob);
		}
		catch (Exception kException)
		{
			sError = GetErrorText(kException, $"알람 테스트 발송 #{kJob.nID} {kJob.sChannel}");
		}

		AlarmLog.WriteHistory(kJob, sError != "" ? "Failed" : "Sent", sError);	// 알람 기록 (제목: [테스트] 표시)

		return sError;
	}

	private static void SendJob(AlarmJob kJob)	// 채널별 발송 (클래스 Send 안: 메서드 이름 Send 불가) (실패: 예외)
	{
		if (kJob.sChannel == "Mail")
		{
			SendMail(kJob);
		}
		else
		{
			SendHook(kJob);
		}
	}

	public static string GetMailError()	// 메일 설정 확인 (Mail:sHost·보내는 주소 없음: "Mail Off", 있음: "")
	{
		return Config.GetString("Mail:sHost") == "" || GetMailFrom() == "" ? "Mail Off" : "";
	}

	private static string GetMailFrom()	// 보내는 주소 (Mail:sFrom, 없음: 로그인 계정 Mail:sID)
	{
		string	sFrom	= Config.GetString("Mail:sFrom");

		return sFrom != "" ? sFrom : Config.GetString("Mail:sID");
	}

	private static void SendMail(AlarmJob kJob)	// 메일 (SMTP, 설정 Mail:sHost·nPort·bSsl·sFrom (없음: sID), 인증: Mail:sID·sCode 있을 때만): 제목·내용 HTML (GetMailHtml), 받는 사람 모두
	{
		if (GetMailError() != "")
		{
			throw new RelayException(GetMailError());
		}

		if (Config.GetString("Mail:sID") != "" && Auth.IsWait())	// 인증 메일: 마스터 키 입력 대기 (비밀번호 없음)
		{
			throw new InvalidOperationException(GetMasterError());
		}

		using System.Net.Mail.SmtpClient	kClient	= new System.Net.Mail.SmtpClient(Config.GetString("Mail:sHost"), Config.GetInt("Mail:nPort", 25)) { EnableSsl = Config.GetBool("Mail:bSsl"), Timeout = m_nTimeoutMs };
		using System.Net.Mail.MailMessage	kMail	= new System.Net.Mail.MailMessage { From = new System.Net.Mail.MailAddress(GetMailFrom()), Subject = kJob.sTitle, Body = GetMailHtml(kJob), IsBodyHtml = true, BodyEncoding = Encoding.UTF8 };

		if (Config.GetString("Mail:sID") != "")
		{
			kClient.Credentials = new NetworkCredential(Config.GetString("Mail:sID"), Config.GetString("Mail:sPass"));
		}

		foreach (string sMail in kJob.arrTarget)
		{
			kMail.To.Add(sMail);
		}

		try
		{
			kClient.Send(kMail);
		}
		catch (System.Net.Mail.SmtpException kException)	// SMTP 거부·접속 실패: 상태 이름 (상세: 로그)
		{
			Log.Write(WARN_, $"알람 메일 SMTP: {kException.StatusCode} ({kException.InnerException?.Message ?? kException.Message})");

			throw new RelayException($"Mail {kException.StatusCode}");
		}
	}

	private static string GetLink(AlarmJob kJob)	// 보낼 쿼리 링크: 외부 API 쿼리 주소 (없음: 페이지 주소 sLink)
	{
		return kJob.sApiLink != "" ? kJob.sApiLink : kJob.sLink;
	}

	private static string GetText(AlarmJob kJob)	// 보낼 내용: 알람 내용 + 빈 줄 2개 + 쿼리 링크 (링크 없음: 내용만, 예전 펜딩: 끝 페이지 주소 뺀 뒤 붙임, 알람 기록: 링크 따로)
	{
		string	sText	= kJob.sLink != "" && kJob.sText.EndsWith(kJob.sLink, StringComparison.Ordinal) ? kJob.sText[..^kJob.sLink.Length].TrimEnd('\n') : kJob.sText;	// 알람 내용 (링크 없음)

		return GetLink(kJob) == "" ? sText : sText + "\n\n\n" + GetLink(kJob);
	}

	private static string GetMailHtml(AlarmJob kJob)	// 메일 HTML: 내용 줄 → 글자 바꿈 (<·& 등) + 줄 <br>, 구분 줄 → <hr>, 마지막 쿼리 주소 → 링크
	{
		StringBuilder	kHtml	= new StringBuilder();
		string[]		arrLine	= GetText(kJob).Split('\n');

		for (int nIndex = 0; nIndex < arrLine.Length; ++nIndex)
		{
			string	sLine	= arrLine[nIndex];

			if (sLine == Stat.MAIL_LINE)	// 구분 줄: 가로줄 (앞뒤 <br> 없음)
			{
				kHtml.Append("<hr>");

				continue;
			}

			kHtml.Append(sLine != "" && sLine == GetLink(kJob) ? $"<a href=\"{WebUtility.HtmlEncode(sLine)}\">{WebUtility.HtmlEncode(sLine)}</a>" : WebUtility.HtmlEncode(sLine));

			if (nIndex + 1 < arrLine.Length && arrLine[nIndex + 1] != Stat.MAIL_LINE)
			{
				kHtml.Append("<br>");
			}
		}

		return kHtml.ToString();
	}

	private static void SendHook(AlarmJob kJob)	// 훅: POST {"botName", "text": 메일 제목, "attachments": [{"text": 메일 내용 (구분 줄: 마크다운 가로줄, 마지막 쿼리 주소: 마크다운 링크)}]} (두레이·슬랙 수신 훅 형식, 2xx: 성공, 길이 있는 본문: chunked 못 받는 곳 대비)
	{
		string	sText	= string.Join('\n', GetText(kJob).Split('\n').Select(sLine => sLine == Stat.MAIL_LINE ? "\n---" : sLine));	// 구분 줄 → 빈 줄 + --- (빈 줄 없음: 윗줄이 제목으로 바뀜)

		string	sLink	= GetLink(kJob);	// 보낼 쿼리 링크

		sText = sLink != "" && sText.EndsWith(sLink, StringComparison.Ordinal) ? sText[..^sLink.Length] + $"[{sLink}]({sLink})" : sText;	// 본문 마지막 쿼리 주소: 누르면 외부 API 쿼리 (주소 괄호·대괄호: 인코딩됨)

		using StringContent kBody = new StringContent(JsonSerializer.Serialize(new { botName = Web.GetTitle(), text = kJob.sTitle, attachments = new[] { new { text = sText } } }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Encoding.UTF8, "application/json");	// 본문 (보낸 이: HMX 존, 제목·내용: 메일과 같음)

		try
		{
			using HttpResponseMessage kResponse = m_kHttp.PostAsync(kJob.arrTarget[0], kBody).GetAwaiter().GetResult();

			if (!kResponse.IsSuccessStatusCode)
			{
				throw new RelayException($"HTTP {(int)kResponse.StatusCode}");
			}
		}
		catch (TaskCanceledException)	// 제한 시간 넘음 (User Stop 아님)
		{
			throw new TimeoutException();
		}
		catch (HttpRequestException kException)	// 접속 안 됨·주소 오류 (상세: 로그)
		{
			Log.Write(WARN_, $"알람 훅: {kException.Message}");

			throw new RelayException("Hook Off");
		}
	}
}

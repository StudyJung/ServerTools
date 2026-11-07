static class Stat	// 알람 통계 스레드 1개: 매분 (분 경계) 워커 카운터 합산 → 알람별 nMinute 분 구간 (KST 자정부터 X분마다, 하루 안 넘김) 끝에 판정 (합 ≥ nCount, 구간이 억제와 안 겹침) → 채널마다 발송 작업 (분당 X건 한도, 넘음·실패: 펜딩 → 여유 생기면 재전송)
{
	private const int	m_nSendMax		= 100;	// 분당 발송 한도 (새 발송 + 재전송)
	private const int	m_nDayMinute	= 1440;	// 하루 (분, 알람 구간: KST 자정 안 넘김)

	public const string	MAIL_LINE	= "──────────────────────────────";	// 메일·훅 구분 줄 표시 (원문·필드 위·Type 아래, 내용·펜딩: 이 줄 그대로, 보낼 때: 메일 <hr>·훅 마크다운 ---)

	private const int	m_nPendingMax	= 10000;	// 펜딩 최대 (넘음: 오래된 것 포기)
	private const int	m_nPendingMin	= 60;		// 펜딩 유효 (분, 지남: 포기)
	private const int	m_nTryMax		= 3;		// 최대 보내기 횟수 (넘음: 포기)

	private static Thread?					m_kThread;											// 통계 스레드
	private static CancellationTokenSource	m_kStop			= new CancellationTokenSource();	// 중지 요청 (대기 즉시 깸)
	private static readonly Dictionary<int, (AlarmItem kAlarm, int nSum, long nDelayEnd, AlarmHit kLast)>	m_hmapState	= new Dictionary<int, (AlarmItem kAlarm, int nSum, long nDelayEnd, AlarmHit kLast)>();	// 알람 ID → 상태 (kLast: 구간 마지막 맞은 줄) (알람 바뀜: 처음부터, 이 스레드만)
	private static List<AlarmJob>			m_listPending	= new List<AlarmJob>();				// 펜딩 (오래된 순, 이 스레드만)
	private static readonly object			m_kSaveLock		= new object();						// pending.json 쓰기 1건씩

	public static void Start()	// 펜딩 읽기 → 통계 스레드 시작
	{
		if (m_kThread != null)
		{
			return;
		}

		m_listPending	= LoadPending();
		m_kStop			= new CancellationTokenSource();
		m_kThread		= new Thread(Run) { IsBackground = true, Name = "Stat" };

		m_kThread.Start();
	}

	public static void Stop()	// 통계 스레드 중지 (진행 중인 분 처리 완료 대기, 최대 X초)
	{
		if (m_kThread == null)
		{
			return;
		}

		m_kStop.Cancel();
		m_kThread.Join(5000);

		m_kThread = null;
	}

	private static void Run()	// 분 경계마다 처리 (예외: 출력 후 다음 분)
	{
		long	nMinute	= GetMinute() + 1;	// 다음 분 경계 (유닉스 분)

		while (!m_kStop.IsCancellationRequested)
		{
			long	nWait	= nMinute * 60000 - GetNow().ToUnixTimeMilliseconds();	// 분 경계까지 (ms)

			if (nWait > 0 && m_kStop.Token.WaitHandle.WaitOne((int)Math.Min(nWait, int.MaxValue)))
			{
				break;
			}

			try
			{
				Tick(nMinute);
			}
			catch (Exception kException)
			{
				Log.Write(ERROR, $"알람 통계 오류: {kException.Message}");
			}

			nMinute = Math.Max(nMinute + 1, GetMinute() + 1);	// 늦어짐: 지난 분 건너뜀
		}
	}

	private static long GetMinute()	// 지금 유닉스 분
	{
		return GetNow().ToUnixTimeMilliseconds() / 60000;
	}

	private static void Tick(long nMinute)	// 분 경계 nMinute (끝난 분: nMinute - 1): 합산 → 판정 → 발송·펜딩
	{
		Alarm.Refresh();

		Dictionary<int, int>		hmapCount	= new Dictionary<int, int>();		// 알람 ID → 끝난 분 건수 (수신 시각 기준)
		Dictionary<int, AlarmHit>	hmapLast	= new Dictionary<int, AlarmHit>();	// 알람 ID → 끝난 분 마지막 맞은 줄 (원문 시간 최신)

		foreach ((AlarmMatcher kMatcher, AlarmCount kCount) in TabQuery.TakeAlarmCount())
		{
			for (int nAlarm = 0; nAlarm < kCount.m_arrCount.Length; ++nAlarm)
			{
				if (kCount.m_arrCount[nAlarm] > 0)
				{
					int	nID	= kMatcher.m_arrAlarm[nAlarm].nID;

					hmapCount[nID] = hmapCount.GetValueOrDefault(nID) + kCount.m_arrCount[nAlarm];

					if (!hmapLast.TryGetValue(nID, out AlarmHit kLast) || string.CompareOrdinal(kCount.m_arrLast[nAlarm].kRecord.sTime, kLast.kRecord.sTime) >= 0)
					{
						hmapLast[nID] = kCount.m_arrLast[nAlarm];
					}
				}
			}
		}

		AlarmItem[]		arrAlarm	= Alarm.m_kMatcher?.m_arrAlarm ?? Array.Empty<AlarmItem>();	// 사용 알람
		List<AlarmJob>	listJob		= new List<AlarmJob>();	// 새 발송 작업
		bool			bChanged	= false;				// 펜딩 바뀜

		HashSet<int>	setID		= arrAlarm.Select(kAlarm => kAlarm.nID).ToHashSet();	// 사용 알람 ID
		long			nDayMinute	= ((nMinute + (long)GetNow().Offset.TotalMinutes) % m_nDayMinute + m_nDayMinute) % m_nDayMinute;	// 분 경계의 KST 하루 안 분 (0: 자정)
		long			nDayEnd		= nDayMinute == 0 ? m_nDayMinute : nDayMinute;			// 자정: 그날 끝 (하루 분)

		foreach (int nID in m_hmapState.Keys.Where(nID => !setID.Contains(nID)).ToList())	// 없어진·꺼진 알람
		{
			m_hmapState.Remove(nID);
		}

		foreach (AlarmItem kAlarm in arrAlarm)
		{
			(_, int nSum, long nDelayEnd, AlarmHit kLast) = m_hmapState.TryGetValue(kAlarm.nID, out (AlarmItem kAlarm, int nSum, long nDelayEnd, AlarmHit kLast) kState) && kState.kAlarm == kAlarm ? kState : (kAlarm, 0, 0L, default(AlarmHit));	// 알람 바뀜 (수정·켬): 처음부터

			nSum += hmapCount.GetValueOrDefault(kAlarm.nID);

			if (hmapLast.TryGetValue(kAlarm.nID, out AlarmHit kHit) && string.CompareOrdinal(kHit.kRecord.sTime, kLast.kRecord.sTime) >= 0)	// 구간 마지막 맞은 줄 (원문 시간 최신)
			{
				kLast = kHit;
			}

			if (nDayEnd % kAlarm.nMinute == 0 || nDayMinute == 0)	// 구간 끝: KST 자정부터 X분마다 + 자정 (하루 안 넘김, 하루에 안 나눠떨어지는 간격: 그날 마지막 구간 짧음)
			{
				int	nLength	= (int)(nDayEnd - (nDayEnd - 1) / kAlarm.nMinute * kAlarm.nMinute);	// 이 구간 길이 (분, 보통: 간격, 그날 마지막 짧은 구간: 자정까지)

				if (nSum >= kAlarm.nCount && nMinute - nLength >= nDelayEnd)	// 발생: 구간 시작이 억제 끝 뒤 (억제와 겹친 구간: 안 봄, 예: 간격 X분·억제 X분 → 발송 다음 구간 건너뜀 → 그다음 구간 판정)
				{
					listJob.AddRange(MakeJob(kAlarm, nMinute, nLength, nSum, kLast));

					nDelayEnd = nMinute + kAlarm.nDelay;
				}

				nSum	= 0;
				kLast	= default;
			}

			m_hmapState[kAlarm.nID] = (kAlarm, nSum, nDelayEnd, kLast);
		}

		while (Send.m_kResult.TryDequeue(out (AlarmJob kJob, string sError) kResult))	// 발송 실패 → 펜딩 (X번 넘음: 포기)
		{
			if (kResult.kJob.nTry >= m_nTryMax)
			{
				AlarmLog.Write(kResult.kJob, "Expired", $"{m_nTryMax}번 실패");
			}
			else
			{
				m_listPending.Add(kResult.kJob);
			}

			bChanged = true;
		}

		int	nQuota		= Math.Max(0, m_nSendMax - Send.GetQueueCount());	// 이번 분 남은 한도 (못 보낸 큐만큼 줄임: 발송 느림·실패 계속에도 큐 상한)
		int	nDeferred	= 0;	// 한도 넘어 펜딩

		foreach (AlarmJob kJob in listJob)
		{
			if (nQuota > 0)
			{
				--nQuota;

				Send.Add(kJob);
			}
			else
			{
				AlarmLog.Write(kJob, "Deferred", "분당 한도");

				m_listPending.Add(kJob);

				++nDeferred;
				bChanged = true;
			}
		}

		if (nDeferred > 0)
		{
			Log.Write(WARN_, $"알람 발송 한도 넘음: {nDeferred}건 펜딩 (분당 {m_nSendMax}건)");
		}

		foreach (AlarmJob kJob in m_listPending.Where(kJob => nMinute - kJob.nMinute > m_nPendingMin || !setID.Contains(kJob.nID)))	// 유효 시간 지남·알람 없어짐·꺼짐: 포기
		{
			AlarmLog.Write(kJob, "Expired", setID.Contains(kJob.nID) ? "유효 시간 지남" : "알람 없음·꺼짐");

			bChanged = true;
		}

		m_listPending.RemoveAll(kJob => nMinute - kJob.nMinute > m_nPendingMin || !setID.Contains(kJob.nID));

		while (m_listPending.Count > m_nPendingMax)	// 최대 넘음: 오래된 것부터 포기
		{
			AlarmLog.Write(m_listPending[0], "Expired", "펜딩 최대 넘음");

			m_listPending.RemoveAt(0);

			bChanged = true;
		}

		while (nQuota > 0 && m_listPending.Count > 0)	// 남은 한도: 펜딩 재전송 (오래된 것부터)
		{
			AlarmJob	kJob	= m_listPending[0];

			m_listPending.RemoveAt(0);

			kJob.nRetryOf	= kJob.nRetryOf == 0 ? kJob.nLogId : kJob.nRetryOf;
			kJob.nLogId		= AlarmLog.NewId();

			Send.Add(kJob);

			--nQuota;
			bChanged = true;
		}

		if (bChanged)
		{
			SavePending(Array.Empty<AlarmJob>());
		}
	}

	private static IEnumerable<AlarmJob> MakeJob(AlarmItem kAlarm, long nMinute, int nLength, int nSum, AlarmHit kLast)	// 알람 발생 → 받는 곳마다 작업 (메일: 받는 계정마다 1건 (링크: 그 계정 키/토큰), 훅 1건 (링크: 관리 계정 키/토큰), SMS: 나중에)
	{
		DateTimeOffset	kEnd	= DateTimeOffset.FromUnixTimeMilliseconds(nMinute * 60000).ToOffset(GetNow().Offset);	// 구간 끝 (KST)
		DateTimeOffset	kStart	= kEnd.AddMinutes(-nLength);	// 구간 시작 (nLength: 이 구간 길이, 자정에서 끊긴 구간: 짧음)
		string			sTitle	= $"{GetTitleTag()}:{kAlarm.sKey} - [{nSum}]";	// [HMX][존]:알람 제목 - [횟수]
		string	sLink	= Web.GetQueryLink(kAlarm.sQuery, kStart, kEnd);	// 쿼리 탭 바로 실행 주소
		string	sText	= GetMailText(kAlarm.sQuery, kStart, kEnd, nSum, kLast.kRecord.sText ?? "", GetHitField(kLast));

		foreach ((string sChannel, string[] arrTarget, string sPath) in GetChannel(kAlarm))
		{
			yield return new AlarmJob
			{
				nLogId		= AlarmLog.NewId(),
				nMinute		= nMinute,
				nID			= kAlarm.nID,
				sKey		= kAlarm.sKey,
				sQuery		= kAlarm.sQuery,
				sTime		= kEnd.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
				sWindow		= $"{kStart:HH:mm}~{kEnd:HH:mm}",
				nCount		= nSum,
				nLimit		= kAlarm.nCount,
				sChannel	= sChannel,
				arrTarget	= arrTarget,
				sTitle		= sTitle,
				sText		= sText,
				sLink		= sLink,
				sApiLink	= Web.GetApiQueryLink(kAlarm.sQuery, kStart, kEnd, sPath),	// 메일: 받는 계정 키/토큰 외부 API 쿼리 주소 (키 없음: "" → sLink), 훅: 없음 (링크 안 보냄)
			};
		}
	}

	public static IEnumerable<AlarmJob> MakeTestJob(AlarmItem kAlarm)	// 테스트 발송 작업 (제목: 알람 nCount 회·:[테스트], 내용: 실제 알람 형식, 구간: 보낸 시각 기준, 마지막 원문: 가상)
	{
		DateTimeOffset	kEnd	= GetNow();							// 보낸 시각 (KST)
		DateTimeOffset	kStart	= kEnd.AddMinutes(-kAlarm.nMinute);	// 구간 시작
		string			sTime	= kEnd.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
		string			sLine	= $"[{kEnd.ToString("yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] [ERROR] TEST";	// 가상 마지막 원문
		string	sField	= "Machine: TEST\nFolder: TEST\nFile: TEST\nType: ERROR\n" + MAIL_LINE;	// Type 아래 구분 줄 (실제 알람과 같게)	// 가상 필드
		string	sLink	= Web.GetQueryLink(kAlarm.sQuery, kStart, kEnd);						// 쿼리 탭 바로 실행 주소

		return GetChannel(kAlarm).Select(kChannel => new AlarmJob
		{
			nID			= kAlarm.nID,
			sKey		= kAlarm.sKey,
			sQuery		= kAlarm.sQuery,
			sTime		= sTime,
			nLimit		= kAlarm.nCount,
			sChannel	= kChannel.sChannel,
			arrTarget	= kChannel.arrTarget,
			sTitle		= $"{GetTitleTag()}:{kAlarm.sKey} - [{kAlarm.nCount}]:[테스트]",
			sWindow		= $"{kStart:HH:mm}~{kEnd:HH:mm}",
			nCount		= kAlarm.nCount,
			sText		= GetMailText(kAlarm.sQuery, kStart, kEnd, kAlarm.nCount, sLine, sField),
			sLink		= sLink,
			sApiLink	= Web.GetApiQueryLink(kAlarm.sQuery, kStart, kEnd, kChannel.sPath),	// 메일: 받는 계정 키/토큰 외부 API 쿼리 주소 (키 없음: "" → sLink), 훅: 없음 (링크 안 보냄)
		});
	}

	private static string GetMailText(string sQuery, DateTimeOffset kStart, DateTimeOffset kEnd, int nCount, string sLine, string sField)	// 메일·훅 내용: Query·Time (초까지)·Count → 구분 줄 → 마지막 원문 → 구분 줄 → 필드 (Type 아래 구분 줄 → 추가 필드) (없는 것: 뺌, 쿼리 링크: 메일·훅 보낼 때 끝에 붙임 (Send.GetText), 알람 기록: 따로)
	{
		StringBuilder	kText	= new StringBuilder();

		kText.Append($"Query: {sQuery}\nTime: {kStart:yyyy-MM-dd HH:mm:ss} - {kEnd:yyyy-MM-dd HH:mm:ss}\nCount: {nCount}회");	// 맨 위, 초까지, Count: Time 밑

		if (sLine != "")
		{
			kText.Append('\n').Append(MAIL_LINE).Append('\n').Append(sLine);	// 구분 줄 → 마지막 원문
		}

		if (sField != "")
		{
			kText.Append('\n').Append(MAIL_LINE).Append('\n').Append(sField);	// 구분 줄 (필드 위, 빈 줄 대신)
		}

		return kText.ToString();
	}

	private static string GetHitField(AlarmHit kHit)	// 마지막 맞은 줄 필드: Machine·Folder·File·Type → 구분 줄 (늘) → 추가 필드 (줄 없음: "")
	{
		if (kHit.kRecord.sText == null)
		{
			return "";
		}

		string			sFolder	= kHit.sFolder.TrimEnd('\\', '/');		// 대상 폴더
		string			sPath	= TabQuery.GetFilePath(kHit.nFileId);	// 클라 경로
		string			sFile	= sFolder != "" && sPath.Length > sFolder.Length && sPath.StartsWith(sFolder, StringComparison.OrdinalIgnoreCase) ? sPath[(sFolder.Length + 1)..] : sPath;	// Folder 아래 경로 (화면 File 칸과 같음)
		StringBuilder	kText	= new StringBuilder();

		kText.Append("Machine: ").Append(kHit.sMachine).Append("\nFolder: ").Append(kHit.sFolder).Append("\nFile: ").Append(sFile).Append("\nType: ").Append(kHit.kRecord.sType);

		kText.Append('\n').Append(MAIL_LINE);	// Type 아래 구분 줄 (추가 필드 없어도)

		foreach ((string sKey, string sValue) in kHit.kRecord.arrField)
		{
			kText.Append('\n').Append(sKey).Append(": ").Append(sValue);
		}

		return kText.ToString();
	}

	private static string GetTitleTag()	// 메일·훅 제목 앞: [HMX] 고정 + [존] (설정 sZone, 예: [HMX][Real], 존 없음: [HMX])
	{
		string	sZone	= Config.GetString("sZone").Trim();

		return sZone == "" ? "[HMX]" : $"[HMX][{sZone}]";
	}

	private static (string sChannel, string[] arrTarget, string sPath)[] GetChannel(AlarmItem kAlarm)	// 받는 곳 (메일: aMail 계정마다 1건 → 그 계정 sMail·키/토큰 (예전 메일 주소: 그대로·경로 ""), 메일 없는 계정·없는 계정·같은 주소: 뺌, 훅 1건 (관리 계정 키/토큰), SMS: 나중에)
	{
		List<(string sChannel, string[] arrTarget, string sPath)>	listChannel	= new List<(string sChannel, string[] arrTarget, string sPath)>();

		foreach (string sTarget in kAlarm.aMail)
		{
			(string sAddr, string sPath) = Auth.GetMailTarget(sTarget);

			if (sAddr != "" && !listChannel.Any(kChannel => string.Equals(kChannel.arrTarget[0], sAddr, StringComparison.OrdinalIgnoreCase)))	// 같은 주소: 1번
			{
				listChannel.Add(("Mail", new[] { sAddr }, sPath));
			}
		}

		if (kAlarm.sHook != "")
		{
			listChannel.Add(("Hook", new[] { kAlarm.sHook }, ""));	// 훅: 링크 없음 (키·토큰 안 만듦)
		}

		return listChannel.ToArray();
	}

	private static string GetPendingFile()	// 펜딩 파일 (기준 폴더 하위 Data\Alarm\pending.json)
	{
		return Path.Combine(GetBaseDir(), "Data", "Alarm", "pending.json");
	}

	private static List<AlarmJob> LoadPending()	// 펜딩 읽기 (없음·오류: 빈 목록, 오류: 출력)
	{
		string	sFile	= GetPendingFile();

		try
		{
			return File.Exists(sFile) ? JsonSerializer.Deserialize<List<AlarmJob>>(File.ReadAllText(sFile)) ?? new List<AlarmJob>() : new List<AlarmJob>();
		}
		catch (Exception kException)
		{
			Log.Write(WARN_, $"알람 펜딩 읽기 실패: {sFile} ({kException.Message})");

			return new List<AlarmJob>();
		}
	}

	public static void SavePending(IEnumerable<AlarmJob> listMore)	// 펜딩 저장 (임시 파일 후 교체, listMore: 종료 때 못 보낸 작업 함께)
	{
		string	sFile	= GetPendingFile();

		lock (m_kSaveLock)
		{
			try
			{
				List<AlarmJob>	listSave	= m_listPending.Concat(listMore).ToList();	// 저장할 작업

				Directory.CreateDirectory(Path.GetDirectoryName(sFile)!);
				File.WriteAllText(sFile + ".tmp", JsonSerializer.Serialize(listSave));
				File.Move(sFile + ".tmp", sFile, true);
			}
			catch (Exception kException)
			{
				Log.Write(WARN_, $"알람 펜딩 저장 실패: {sFile} ({kException.Message})");
			}
		}
	}
}

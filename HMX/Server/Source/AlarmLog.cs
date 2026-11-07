static class AlarmLog	// 알람 로그: 기준 폴더 하위 data\alarm\log\yyyyMMdd.jsonl 추가 전용 (1줄 = 발송 작업 결과 1건, 받는 곳 안 씀) + 조회 API, 알람 기록 (날짜·제목·내용·결과): data\alarm\history.jsonl 최신 X건 + 조회 API (전체, 최신 순)
{
	private const int	m_nPageSize		= 100;	// 조회 한 페이지 줄 수
	private const int	m_nDayMax		= 31;	// 조회 최대 기간 (일, 메모리 보호)
	private const int	m_nPageMax		= 100;	// 조회 최대 페이지 (보관 줄 수 상한)
	private const int	m_nHistoryMax	= 1000;	// 알람 기록 최대 건수 (최신만, 넘음: 오래된 것 지움)

	private static long				m_nLogId		= GetNow().ToUnixTimeMilliseconds() * 100;	// 줄 번호 (시작 시각 기준 → 재시작해도 겹침 없음)
	private static readonly object	m_kLock			= new object();	// 파일 추가 1건씩
	private static readonly object	m_kHistoryLock	= new object();	// 알람 기록 읽기·쓰기 1건씩
	private static List<string>?	m_listHistory;					// 알람 기록 줄 (오래된 순, 처음 쓰기·조회 때 파일에서 읽음)

	public static long NewId()	// 새 줄 번호
	{
		return Interlocked.Increment(ref m_nLogId);
	}

	public static void Write(AlarmJob kJob, string sResult, string sError)	// 결과 1줄 추가 (Sent·Failed·Deferred·Resent·Expired, 실패: 출력만)
	{
		string sLine = new JsonObject
		{
			["nLogId"]		= kJob.nLogId,
			["sTime"]		= kJob.sTime,
			["nID"]			= kJob.nID,
			["sKey"]		= kJob.sKey,
			["sQuery"]		= kJob.sQuery,
			["nCount"]		= kJob.nCount,
			["nLimit"]		= kJob.nLimit,
			["sWindow"]		= kJob.sWindow,
			["sChannel"]	= kJob.sChannel,
			["sResult"]		= sResult,
			["sError"]		= sError,
			["nRetryOf"]	= kJob.nRetryOf,
			["nTry"]		= kJob.nTry,
			["sDone"]		= GetNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
		}.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });	// 줄 (JSON)
		string	sFile	= Path.Combine(GetBaseDir(), "data", "alarm", "log", GetNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");

		lock (m_kLock)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(sFile)!);
				File.AppendAllText(sFile, sLine + "\n");
			}
			catch (Exception kException)
			{
				Log.Write(WARN_, $"알람 로그 쓰기 실패: {sFile} ({kException.Message})");
			}
		}
	}

	public static void WriteHistory(AlarmJob kJob, string sResult, string sError)	// 알람 기록 1줄 추가 (발송 시도마다: Sent·Resent·Failed, 테스트 포함, 받는 곳 안 씀) → 최신 X건만 파일 다시 씀 (실패: 출력만 → 발송 결과 영향 없음)
	{
		string sLine = new JsonObject
		{
			["sDone"]		= GetNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
			["nID"]			= kJob.nID,
			["sKey"]		= kJob.sKey,
			["sChannel"]	= kJob.sChannel,
			["sResult"]		= sResult,
			["sError"]		= sError,
			["nTry"]		= kJob.nTry,
			["sTitle"]		= kJob.sTitle,
			["sText"]		= kJob.sText,
			["sLink"]		= kJob.sLink,
		}.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });	// 줄 (JSON: 날짜·알람·채널·결과·사유·제목·내용·쿼리 링크, 내용 줄바꿈: \n)
		string	sFile	= GetHistoryFile();

		lock (m_kHistoryLock)
		{
			List<string>	listHistory	= LoadHistory();

			listHistory.Add(sLine);

			if (listHistory.Count > m_nHistoryMax)	// 넘음: 오래된 것 지움
			{
				listHistory.RemoveRange(0, listHistory.Count - m_nHistoryMax);
			}

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(sFile)!);
				File.WriteAllText(sFile + ".tmp", string.Concat(listHistory.Select(sItem => sItem + "\n")), new UTF8Encoding(false));
				File.Move(sFile + ".tmp", sFile, true);	// 쓰는 중 종료: 이전 파일 그대로
			}
			catch (Exception kException)
			{
				Log.Write(WARN_, $"알람 기록 쓰기 실패: {sFile} ({kException.Message})");
			}
		}
	}

	public static IResult OnHistory()	// 알람 기록 조회: 전체 (최신 X건) 최신 순 → {sError, arrHistory: [{sDone, nID, sKey, sChannel, sResult, sError, nTry, sTitle, sText, sLink}]}
	{
		string[]	arrLine;	// 기록 줄 (오래된 순)

		lock (m_kHistoryLock)
		{
			arrLine = LoadHistory().ToArray();
		}

		List<JsonNode>	listHistory	= new List<JsonNode>();	// 최신 순

		foreach (string sLine in arrLine.Reverse())
		{
			try
			{
				if (JsonNode.Parse(sLine) is JsonObject kHistory)
				{
					listHistory.Add(kHistory);
				}
			}
			catch (JsonException)	// 깨진 줄: 건너뜀
			{
			}
		}

		return Results.Json(new { sError = "", arrHistory = listHistory });
	}

	private static List<string> LoadHistory()	// 알람 기록 줄 (m_kHistoryLock 안, 처음: 파일에서 최신 X건 읽음, 없음·실패: 빈 목록)
	{
		if (m_listHistory != null)
		{
			return m_listHistory;
		}

		m_listHistory = new List<string>();

		string	sFile	= GetHistoryFile();

		try
		{
			if (File.Exists(sFile))
			{
				m_listHistory.AddRange(File.ReadLines(sFile, Encoding.UTF8).Where(sLine => sLine.Trim() != "").TakeLast(m_nHistoryMax));
			}
		}
		catch (Exception kException) when (kException is IOException or UnauthorizedAccessException)	// 사용 중·권한 없음: 빈 목록 (다음 쓰기: 새로)
		{
			Log.Write(WARN_, $"알람 기록 읽기 실패: {sFile} ({kException.Message})");
		}

		return m_listHistory;
	}

	private static string GetHistoryFile()	// 알람 기록 파일 (기준 폴더 하위 data\alarm\history.jsonl, 1줄 = 발송 시도 1건, 최신 X건)
	{
		return Path.Combine(GetBaseDir(), "data", "alarm", "history.jsonl");
	}

	public static IResult OnLog(long? nStart, long? nEnd, int? nID, string? sResult, int? nPage)	// 알람 로그 조회: 기간 (yyyyMMddHHmm, 기록 날짜 파일, 최대 X일)·알람 ID·결과 → 최신 순 페이지 {sError, nTotal, nPage, nPageSize, arrLog}
	{
		DateTime	kNow	= GetNow().DateTime;
		DateTime	kEnd	= nEnd != null && DateTime.TryParseExact(nEnd.ToString(), "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kEndValue) ? kEndValue : kNow;						// 끝 (없음: 지금)
		DateTime	kStart	= nStart != null && DateTime.TryParseExact(nStart.ToString(), "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kStartValue) ? kStartValue : kEnd.AddDays(-1);	// 시작 (없음: 1일 전)

		if (kStart > kEnd || (kEnd - kStart).TotalDays > m_nDayMax)
		{
			return Results.Json(new { sError = $"기간 오류 (최대 {m_nDayMax}일)", nTotal = 0, nPage = 0, nPageSize = m_nPageSize, arrLog = Array.Empty<object>() });
		}

		int				nPageValue	= Math.Clamp(nPage ?? 0, 0, m_nPageMax);	// 페이지 (0부터, 상한: 메모리)
		int				nKeep		= (nPageValue + 1) * m_nPageSize;			// 보관할 최신 줄 수 (이 페이지까지)
		Queue<JsonNode>	queueLog	= new Queue<JsonNode>();					// 맞는 줄 중 최신 nKeep 개 (오래된 순)
		int				nTotal		= 0;										// 맞는 줄 수

		for (DateTime kDay = kStart.Date; kDay <= kEnd.Date; kDay = kDay.AddDays(1))
		{
			string	sFile	= Path.Combine(GetBaseDir(), "data", "alarm", "log", kDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");

			if (!File.Exists(sFile))
			{
				continue;
			}

			using FileStream	kStream	= new FileStream(sFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);	// 쓰는 중에도 읽음 (쓰기 잠금 안 잡음)
			using StreamReader	kReader	= new StreamReader(kStream, Encoding.UTF8);

			for (string? sLine = kReader.ReadLine(); sLine != null; sLine = kReader.ReadLine())
			{
				try
				{
					JsonNode?	kLog	= JsonNode.Parse(sLine);
					string		sDone	= kLog?["sDone"]?.GetValue<string>() ?? "";	// 기록 시각

					if (kLog == null || !DateTime.TryParseExact(sDone, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kDone) || kDone < kStart || kDone >= kEnd.AddMinutes(1)
						|| (nID != null && kLog["nID"]?.GetValue<int>() != nID) || (!string.IsNullOrEmpty(sResult) && kLog["sResult"]?.GetValue<string>() != sResult))
					{
						continue;
					}

					++nTotal;

					queueLog.Enqueue(kLog);

					if (queueLog.Count > nKeep)	// 오래된 것 버림 (메모리 상한)
					{
						queueLog.Dequeue();
					}
				}
				catch (Exception kException) when (kException is JsonException or InvalidOperationException or FormatException)	// 깨진 줄 (쓰는 중 종료)·이상한 값: 건너뜀
				{
				}
			}
		}

		return Results.Json(new { sError = "", nTotal, nPage = nPageValue, nPageSize = m_nPageSize, arrLog = queueLog.Reverse().Skip(nPageValue * m_nPageSize).Take(m_nPageSize) });
	}
}

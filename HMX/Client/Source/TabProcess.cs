using StackExchange.Redis;	// Redis 키·값 (이 파일·Redis·Process 만, 전역 using 안 함 → 이름 겹침 방지)

static class TabProcess	// 반영 탭: X초마다 Redis 목록 중 이 머신 항목 → Ready (Git 동기화) → Start (그룹 순서: On 실행·Off 종료·Down 다운로드) → 바뀐 값 Redis 저장 (OLD HMXClient 이식, API: Process, 짝: 서버 반영 탭)
{
	private const int	m_nTickMs		= 1000;	// 주기 처리 간격 (ms)
	private const int	m_nReadyMax		= 8;	// Git 동기화 동시 최대 (1개 끝: 다음 1개)
	private const int	m_nAliveSec		= 100;	// 동작 확인 로그 주기 (초, 대략)
	private const int	m_nSaveWaitMs	= 1000;	// 값 저장 뒤 대기 (ms, On·True·PID 0 이하 각각)

	public static bool		m_bEnable;			// 실행 기능 켜짐 (Redis:sHost 있음, 없음: 꺼짐 → 로그 클라만)
	private const int		m_nMinThread		= 64;	// 스레드 풀 최소 작업 스레드 (실행 기능 켜짐)
	private static string	m_sMachine	= "";	// 이 머신 이름 (값 머신 파트와 대소문자 무시 비교)

	public static void Init()	// 설정 → 켜짐·머신 (Main.Create)
	{
		m_bEnable	= Redis.IsSet();
		m_sMachine	= GetMachine();

		if (!m_bEnable)
		{
			Log.Write(INFO_, "실행 기능: 꺼짐 (Redis:sHost 없음)");

			return;
		}

		ThreadPool.GetMinThreads(out int nWorker, out int nIO);
		ThreadPool.SetMinThreads(Math.Max(nWorker, m_nMinThread), nIO);	// 실행 기능 동기 대기 (종료 대기·FATAL X초) 동안 로그 클라 비동기 지연 방지
	}

	public static async Task ProcessLoop(CancellationToken kStop)	// 주기 처리 (서비스 중지·Ctrl+C 까지, 예외: 로그 후 다음 주기, 꺼짐: 바로 끝)
	{
		if (!m_bEnable)
		{
			return;
		}

		ProcessLog.Write(NOTI_, $"실행 시작: 목록 {Redis.GetPrefix()}* 중 머신 {m_sMachine} (Git 동시 {m_nReadyMax}개)");

		try
		{
			while (true)
			{
				await Task.Delay(m_nTickMs, kStop);	// 종료 요청 시 OperationCanceledException

				try
				{
					await Tick(kStop);
				}
				catch (Exception kException) when (!kStop.IsCancellationRequested)	// 예상 못한 오류: 다음 주기 계속
				{
					ProcessLog.Write(FATAL, $"Tick 오류: {kException.Message}");
				}
			}
		}
		catch (OperationCanceledException) when (kStop.IsCancellationRequested)	// 서비스 중지·Ctrl+C
		{
		}

		ProcessLog.Write(NOTI_, "실행 종료");
	}

	private static async Task Tick(CancellationToken kStop)	// 1회 처리: 목록 로딩 → Ready → Start
	{
		SortedDictionary<string, string[]> hmapList;	// 이 머신 항목 (필드 → 값 파트, 필드 이름순 = 그룹 순서)

		try
		{
			hmapList = await LoadList();
		}
		catch (Exception kException)	// 호스트 미설정·연결 실패·인증 실패·타임아웃 등 (FATAL: X초 대기 후 다음 주기 재시도)
		{
			ProcessLog.Write(FATAL, $"Tick Redis 조회 실패: {kException.Message}");

			return;
		}

		if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() % m_nAliveSec == 0)	// 대략 X초마다 동작 확인 로그
		{
			ProcessLog.Write(INFO_, $"Tick Alive: {Redis.GetPrefix()}*,{hmapList.Count}");
		}

		await Ready(hmapList, kStop);

		if (kStop.IsCancellationRequested)	// 종료 요청: 시작·정지 생략
		{
			return;
		}

		await Start(hmapList);
	}

	private static async Task<SortedDictionary<string, string[]>> LoadList()	// Redis {sPrefix}* 중 머신 파트가 이 머신인 항목 (String 아닌 키·빈 필드 제외)
	{
		string				sPrefix	= Redis.GetPrefix();
		HashSet<RedisKey>	hsetKey	= new HashSet<RedisKey>();	// 목록 키 (SCAN 은 중복 가능)

		await foreach (RedisKey kKey in Redis.GetServer().KeysAsync(Redis.GetDbNumber(), sPrefix + "*"))	// SCAN 기반 (KEYS 와 달리 Redis 블로킹 없음)
		{
			hsetKey.Add(kKey);
		}

		RedisKey[]		arrKey		= hsetKey.ToArray();
		RedisValue[]	arrValue	= arrKey.Length == 0 ? Array.Empty<RedisValue>() : await Redis.GetDb().StringGetAsync(arrKey);	// MGET 일괄 조회 (String 아닌 키·조회 사이 삭제된 키: null)

		SortedDictionary<string, string[]> hmapList = new SortedDictionary<string, string[]>(StringComparer.Ordinal);

		for (int nIndex = 0; nIndex < arrKey.Length; ++nIndex)
		{
			string sField = arrKey[nIndex].ToString().Substring(sPrefix.Length);	// 키에서 접두사 제거

			if (sField == "" || arrValue[nIndex].IsNull)	// 접두사만 있는 키·String 아닌 키·삭제된 키
			{
				continue;
			}

			string[] arrPart = ProcessValue.Parse(arrValue[nIndex].ToString());

			if (string.Equals(arrPart[ProcessValue.MACHINE], m_sMachine, StringComparison.OrdinalIgnoreCase))	// 이 머신 항목만
			{
				hmapList[sField] = arrPart;
			}
		}

		return hmapList;
	}

	private static async Task Ready(SortedDictionary<string, string[]> hmapList, CancellationToken kStop)	// Git 동기화: 최대 m_nReadyMax 개 동시 (1개 끝: 다음 1개), 실패 항목: Off·False·PID -1 저장 후 이번 Start 대상에서 제외
	{
		List<string> listFail = new List<string>();	// 동기화 실패 필드

		await Parallel.ForEachAsync(hmapList.ToArray(), new ParallelOptions { MaxDegreeOfParallelism = m_nReadyMax }, async (KeyValuePair<string, string[]> kPair, CancellationToken kUnused) =>
		{
			if (await ProcessGit.Update(kPair.Value, kStop))
			{
				return;
			}

			if (!kStop.IsCancellationRequested)	// 종료 요청 중단: 오류 저장 안 함 (재시작 뒤 다시 동기화)
			{
				await PassError(kPair.Key, kPair.Value, "-1");
			}

			lock (listFail)
			{
				listFail.Add(kPair.Key);
			}
		});

		foreach (string sField in listFail)
		{
			hmapList.Remove(sField);
		}
	}

	private static async Task Start(SortedDictionary<string, string[]> hmapList)	// 그룹 (필드 첫번째+두번째 파트) 순서대로, 그룹 안은 동시 (앞 그룹이 전부 끝나야 다음 그룹)
	{
		foreach (IGrouping<string, KeyValuePair<string, string[]>> kGroup in hmapList.GroupBy(kPair => ProcessValue.GetGroup(kPair.Key)))	// 그룹 첫 등장 순서 유지
		{
			await Task.WhenAll(kGroup.Select(kPair => Task.Run(async () =>	// 프로세스 확인·종료 대기가 동기 호출이라 스레드 풀에서 동시 처리
			{
				if (await Execute(kPair.Key, kPair.Value))	// 바뀐 값만 저장
				{
					await RedisUpdate(kPair.Key, kPair.Value);
				}
			})));
		}
	}

	private static async Task<bool> Execute(string sField, string[] arrValue)	// 상태 반영 (반환: 값이 바뀌어 저장 필요 여부)
	{
		string sOnOff	= arrValue[ProcessValue.ONOFF];
		string sCheck	= arrValue[ProcessValue.CHECK];
		string sPID		= arrValue[ProcessValue.PID];
		string sExecute	= arrValue[ProcessValue.EXECUTE];
		string sWorkDir	= arrValue[ProcessValue.FOLDER] + "\\" + arrValue[ProcessValue.GITHUB];	// 실제 실행 위치 (로그 표시)

		if (sOnOff == "Down")	// Down: Ready 의 Git 동기화만 하고 프로세스는 그대로
		{
			if (sCheck == "True")	// 이미 완료
			{
				return false;
			}

			arrValue[ProcessValue.CHECK] = "True";	// 다운로드 완료

			if (ProcessValue.ToInt(sPID) < 0)	// PID 오류값이면 0 (0·양수는 그대로)
			{
				arrValue[ProcessValue.PID] = "0";
			}

			ProcessLog.Write(INFO_, $"Execute Down: {sField}({sWorkDir})({sExecute})");

			return true;
		}

		if (sOnOff == "On")
		{
			int nPID = Process.Check(arrValue, ProcessValue.ToInt(sPID));

			if (nPID < 0)
			{
				ProcessLog.Write(FATAL, $"Execute CheckProcess nPID < 0: {sField}({sWorkDir})({sExecute})({nPID})");

				await PassError(sField, arrValue, "-3");

				return false;
			}

			arrValue[ProcessValue.PID] = nPID.ToString();

			if (sCheck == "True")	// On·True: 실행 중이어야 함
			{
				if (nPID > 0)	// 실행 중
				{
					return false;
				}

				ProcessLog.Write(ERROR, $"Execute Error Crash: {sField}({sWorkDir})({sExecute})({nPID})");

				arrValue[ProcessValue.CHECK]	= "False";	// 다음 주기에 다시 실행
				arrValue[ProcessValue.PID]		= "-10";

				return true;
			}

			if (nPID > 0)	// On·False 인데 이미 실행 중: 적용 완료로
			{
				arrValue[ProcessValue.CHECK] = "True";

				ProcessLog.Write(WARN_, $"Execute True nPID > 0: {sField}({sWorkDir})({sExecute})({nPID})");

				return true;
			}

			nPID = Process.Init(sField, arrValue);	// On·False: 실행

			if (nPID < 0)
			{
				ProcessLog.Write(ERROR, $"Execute Error ProcessInit nPID < 0: {sField}({sWorkDir})({sExecute})({nPID})");

				await PassError(sField, arrValue, "-11");

				return false;
			}

			arrValue[ProcessValue.CHECK]	= "True";
			arrValue[ProcessValue.PID]		= nPID.ToString();

			return true;
		}

		if (sCheck == "True")	// Off·True: 이미 종료
		{
			return false;
		}

		int nFini = Process.Fini(sField, arrValue);	// Off·False: 종료

		if (nFini < 0)
		{
			ProcessLog.Write(ERROR, $"Execute Error ProcessFini nPID < 0: {sField}({sWorkDir})({sExecute})({nFini})");

			await PassError(sField, arrValue, "-12", "True");

			return false;
		}

		arrValue[ProcessValue.CHECK] = "True";

		if (ProcessValue.ToInt(arrValue[ProcessValue.PID]) > 0)	// 종료 완료: PID 0 (오류값 음수는 그대로)
		{
			arrValue[ProcessValue.PID] = "0";
		}

		return true;
	}

	private static async Task PassError(string sField, string[] arrValue, string sError, string sCheck = "False")	// 오류 저장: Off·sCheck·PID 오류값
	{
		arrValue[ProcessValue.ONOFF]	= "Off";
		arrValue[ProcessValue.CHECK]	= sCheck;
		arrValue[ProcessValue.PID]		= sError;

		await RedisUpdate(sField, arrValue);
	}

	private static async Task RedisUpdate(string sField, string[] arrValue)	// 값 저장 ({sPrefix}{필드} = 파트를 콤마로 합친 값), 저장 후 On·True: X초·PID 0 이하: X초 대기
	{
		try
		{
			await Redis.GetDb().StringSetAsync(Redis.GetPrefix() + sField, ProcessValue.Join(arrValue));
		}
		catch (Exception kException)	// 연결 끊김·타임아웃 등
		{
			ProcessLog.Write(FATAL, $"RedisUpdate SET 실패: {sField} ({kException.Message})");
		}

		if (arrValue[ProcessValue.ONOFF] == "On" && arrValue[ProcessValue.CHECK] == "True")
		{
			await Task.Delay(m_nSaveWaitMs);
		}

		if (ProcessValue.ToInt(arrValue[ProcessValue.PID]) <= 0)
		{
			await Task.Delay(m_nSaveWaitMs);
		}
	}
}

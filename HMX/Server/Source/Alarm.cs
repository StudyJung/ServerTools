static class Alarm	// 알람 (적재 시 매칭 → 매분 판정 → 발송): 사용 알람 매칭 스냅샷 (aAlarm 저장 때 교체), 통계 스레드 (Stat)·발송 풀 (Send) 시작·중지
{
	public static volatile AlarmMatcher?	m_kMatcher;			// 매칭 스냅샷 (적재 스레드가 잠금 없이 읽음, 사용 알람 없음: null)
	public static volatile bool				m_bFieldChanged;	// 추가 필드 이름 늘어남 → 다음 분에 스냅샷 다시 (필드:값 판단)

	private static List<AlarmItem>	m_listAlarm	= new List<AlarmItem>();	// 지금 알람 (스냅샷 다시 만들 때)
	private static readonly object	m_kLock		= new object();				// 스냅샷 교체 1건씩

	public static void Start(List<AlarmItem> listAlarm)	// 시작: 스냅샷 → 펜딩 읽기·통계 스레드 → 발송 풀
	{
		SetAlarm(listAlarm);

		Log.Write(INFO_, $"알람: 사용 {listAlarm.Count(kAlarm => kAlarm.bEnable)}개 / 전체 {listAlarm.Count}개, 메일: {(Send.GetMailError() == "" ? "설정 있음" : "설정 없음 (Mail:sHost·sFrom 또는 sID)")}, SMS: 나중에");

		Stat.Start();
		Send.Start();
	}

	public static void Stop()	// 종료 (적재 끝난 뒤): 통계 스레드 중지 → 발송 풀 남은 큐 X초 처리 → 못 보낸 작업 펜딩 저장
	{
		Stat.Stop();
		Stat.SavePending(Send.Stop());
	}

	public static void SetAlarm(List<AlarmItem> listAlarm)	// 알람 목록 바뀜 (시작·저장): 사용 알람만 매칭 스냅샷 새로
	{
		lock (m_kLock)
		{
			AlarmItem[]	arrUse	= listAlarm.Where(kAlarm => kAlarm.bEnable).ToArray();	// 사용 알람

			m_listAlarm		= listAlarm;
			m_bFieldChanged	= false;

			try
			{
				m_kMatcher = arrUse.Length == 0 ? null : new AlarmMatcher(arrUse);
			}
			catch (Exception kException)	// 필드 목록 읽기 실패 등: 앞 스냅샷 유지 (서버·저장 계속)
			{
				Log.Write(ERROR, $"알람 매칭 준비 실패: {kException.Message}");
			}
		}
	}

	public static void Refresh()	// 통계 스레드 (매분): 추가 필드 늘어났으면 스냅샷 다시
	{
		if (m_bFieldChanged)
		{
			SetAlarm(m_listAlarm);
		}
	}
}

record struct AlarmHit(OriginRecord kRecord, int nFileId, string sMachine, string sFolder);	// 알람 맞은 줄 (원문 레코드·원문 파일 번호·머신·Folder, 메일 마지막 원문)

class AlarmCount	// 워커 알람 카운터 (스냅샷 1개, 버퍼 잠금): 알람별 맞은 줄 수·마지막 맞은 줄
{
	public readonly int[]		m_arrCount;	// 맞은 줄 수
	public readonly AlarmHit[]	m_arrLast;	// 마지막 맞은 줄 (원문 시간 최신, 없음: 기본값)

	public AlarmCount(int nAlarm)
	{
		m_arrCount	= new int[nAlarm];
		m_arrLast	= new AlarmHit[nAlarm];
	}
}

class AlarmMatcher	// 알람 매칭 스냅샷 (불변, 적재 스레드가 잠금 없이): 앵커 사전 (단어·필드 키 → 알람 위치) 으로 후보만 조건 평가, 앵커 없음 (*·NOT 만): 모든 줄 평가
{
	public readonly AlarmItem[]	m_arrAlarm;	// 사용 알람 (카운터 위치 = 배열 위치)
	public readonly bool		m_bField;	// 필드 조건 있음 (줄 필드 키 필요)

	private readonly AlarmNode[]				m_arrNode;		// 알람 조건
	private readonly Dictionary<ulong, int[]>	m_hmapWord;		// 앵커 단어 해시 (색인과 같은 GetHash) → 알람 위치
	private readonly Dictionary<string, int[]>	m_hmapField;	// 앵커 필드 키 (대소문자 무시) → 알람 위치
	private readonly int[]						m_arrAlways;	// 앵커 없음: 모든 줄 평가
	private readonly bool[]						m_arrMulti;		// 앵커 여러 개 (OR): 한 줄에 여러 번 후보 → 1번만 평가

	public AlarmMatcher(AlarmItem[] arrAlarm)
	{
		Dictionary<ulong, List<int>>	hmapWord	= new Dictionary<ulong, List<int>>();
		Dictionary<string, List<int>>	hmapField	= new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
		List<int>						listAlways	= new List<int>();

		m_arrAlarm	= arrAlarm;
		m_arrNode	= arrAlarm.Select(kAlarm => new AlarmNode(SearchNode.Parse(kAlarm.sQuery)!)).ToArray();
		m_bField	= m_arrNode.Any(kNode => kNode.HasField());
		m_arrMulti	= new bool[arrAlarm.Length];

		for (int nAlarm = 0; nAlarm < m_arrNode.Length; ++nAlarm)
		{
			HashSet<string>?	setAnchor	= m_arrNode[nAlarm].GetAnchor();	// "W:단어"·"F:필드 키" (null: 앵커 없음)

			if (setAnchor == null)
			{
				listAlways.Add(nAlarm);

				continue;
			}

			m_arrMulti[nAlarm] = setAnchor.Count > 1;

			foreach (string sAnchor in setAnchor)
			{
				string	sKey	= sAnchor[2..];	// 단어·필드 키

				if (sAnchor[0] == 'W')
				{
					if (!hmapWord.TryGetValue(TabQuery.GetHash(sKey), out List<int>? listWord))
					{
						hmapWord[TabQuery.GetHash(sKey)] = listWord = new List<int>();
					}

					listWord.Add(nAlarm);

					continue;
				}

				if (!hmapField.TryGetValue(sKey, out List<int>? listAlarm))
				{
					hmapField[sKey] = listAlarm = new List<int>();
				}

				listAlarm.Add(nAlarm);
			}
		}

		m_hmapWord	= hmapWord.ToDictionary(kPair => kPair.Key, kPair => kPair.Value.ToArray());
		m_hmapField	= hmapField.ToDictionary(kPair => kPair.Key, kPair => kPair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
		m_arrAlways	= listAlways.ToArray();
	}

	public void Match(HashSet<ulong> setWord, HashSet<string>? setField, ReadOnlySpan<char> kText, AlarmCount kCount, AlarmHit kHit)	// 줄 1개: 앵커 후보만 조건 평가 → 맞으면 카운터 +1·마지막 줄 (원문 시간 최신) (setWord: 색인 단어 해시, setField: 필드 키, kText: 앞 시간 뺀 원문, kHit: 이 줄)
	{
		HashSet<int>?	setDone	= null;	// 평가한 앵커 여러 개 알람 (줄당 1번, 앵커 1개: 단어·필드 키가 집합이라 1번뿐)

		foreach (int nAlarm in m_arrAlways)
		{
			Try(nAlarm, ref setDone, setWord, setField, kText, kCount, kHit);
		}

		if (m_hmapWord.Count > 0)
		{
			foreach (ulong nHash in setWord)
			{
				if (m_hmapWord.TryGetValue(nHash, out int[]? arrAlarm))
				{
					foreach (int nAlarm in arrAlarm)
					{
						Try(nAlarm, ref setDone, setWord, setField, kText, kCount, kHit);
					}
				}
			}
		}

		if (m_hmapField.Count > 0 && setField != null)
		{
			foreach (string sField in setField)
			{
				if (m_hmapField.TryGetValue(sField, out int[]? arrAlarm))
				{
					foreach (int nAlarm in arrAlarm)
					{
						Try(nAlarm, ref setDone, setWord, setField, kText, kCount, kHit);
					}
				}
			}
		}
	}

	private void Try(int nAlarm, ref HashSet<int>? setDone, HashSet<ulong> setWord, HashSet<string>? setField, ReadOnlySpan<char> kText, AlarmCount kCount, AlarmHit kHit)	// 알람 1개 조건 평가 (앵커 여러 개: 줄당 1번) → 맞으면 카운터 +1·마지막 줄
	{
		if (m_arrMulti[nAlarm] && !(setDone ??= new HashSet<int>()).Add(nAlarm))
		{
			return;
		}

		if (m_arrNode[nAlarm].Eval(setWord, setField, kText))
		{
			++kCount.m_arrCount[nAlarm];

			if (string.CompareOrdinal(kHit.kRecord.sTime, kCount.m_arrLast[nAlarm].kRecord.sTime) >= 0)	// 원문 시간 최신 (같음: 뒤 줄)
			{
				kCount.m_arrLast[nAlarm] = kHit;
			}
		}
	}
}

class AlarmNode	// 알람 조건 트리 (SearchNode 를 매칭용으로 미리 풂, 쿼리와 같은 규칙: * 전체·필드:값·단어 모두·IP·날짜·시간 원문 그대로)
{
	private readonly SEARCH_OP	m_eOp;			// 연산
	private readonly bool		m_bAll;			// 검색어 *
	private readonly string		m_sField;		// 필드 키 "필드\t값" ("": 필드 아님)
	private readonly string[]	m_arrWord;		// 단어 (모두 있어야 맞음, 없음: 안 맞음, 앵커 고르기용)
	private readonly ulong[]	m_arrHash;		// 단어 해시 (색인 줄 단어 해시 집합과 비교)
	private readonly string		m_sExact;		// 원문 그대로 확인 (IP·날짜·시간, "": 안 함)
	private readonly string		m_sWild;		// 와일드카드 * 뗀 글자 ("": 아님 → 단어 없이 원문 확인, 앵커 없음)
	private readonly bool		m_bWildHead;	// 와일드카드 앞 *
	private readonly bool		m_bWildTail;	// 와일드카드 뒤 *
	private readonly AlarmNode?	m_kLeft;		// 왼쪽 (연산만)
	private readonly AlarmNode?	m_kRight;		// 오른쪽 (연산만)

	public AlarmNode(SearchNode kNode)
	{
		m_eOp		= kNode.m_eOp;
		m_sField	= "";
		m_arrWord	= Array.Empty<string>();
		m_arrHash	= Array.Empty<ulong>();
		m_sExact	= "";
		m_sWild		= "";

		if (kNode.m_eOp != SEARCH_OP.SEARCH_OP_TERM)
		{
			m_kLeft		= new AlarmNode(kNode.m_kLeft!);
			m_kRight	= new AlarmNode(kNode.m_kRight!);

			return;
		}

		m_bAll = TabQuery.IsAll(kNode);

		if (!m_bAll && !TabQuery.TryGetFieldKey(kNode.m_sTerm, out m_sField))
		{
			if (TryGetWild(kNode.m_sTerm, out m_sWild, out m_bWildHead, out m_bWildTail))	// 와일드카드 (쿼리와 같음): 원문 확인만
			{
				return;
			}

			m_arrWord	= TabQuery.SplitTerm(kNode.m_sTerm).ToArray();
			m_arrHash	= m_arrWord.Select(sWord => TabQuery.GetHash(sWord)).ToArray();
			m_sExact	= TabQuery.IsExactTerm(kNode.m_sTerm) ? kNode.m_sTerm.Trim() : "";
		}
	}

	public bool HasField()	// 필드 조건 있음
	{
		return m_eOp == SEARCH_OP.SEARCH_OP_TERM ? m_sField != "" : m_kLeft!.HasField() || m_kRight!.HasField();
	}

	public HashSet<string>? GetAnchor()	// 앵커 ("W:단어"·"F:필드 키", null: 앵커 없음 → 모든 줄, 빈 집합: 안 맞음) — AND: 앵커 적은 쪽 (같음: 긴 단어), OR: 양쪽, NOT: 왼쪽
	{
		switch (m_eOp)
		{
			case SEARCH_OP.SEARCH_OP_AND:
			{
				HashSet<string>?	setLeft		= m_kLeft!.GetAnchor();
				HashSet<string>?	setRight	= m_kRight!.GetAnchor();

				if (setLeft == null || setRight == null)
				{
					return setLeft ?? setRight;
				}

				return setLeft.Count != setRight.Count ? (setLeft.Count < setRight.Count ? setLeft : setRight) : (setLeft.Sum(sAnchor => sAnchor.Length) >= setRight.Sum(sAnchor => sAnchor.Length) ? setLeft : setRight);
			}

			case SEARCH_OP.SEARCH_OP_OR:
			{
				HashSet<string>?	setLeft		= m_kLeft!.GetAnchor();
				HashSet<string>?	setRight	= m_kRight!.GetAnchor();

				if (setLeft == null || setRight == null)
				{
					return null;
				}

				setLeft.UnionWith(setRight);

				return setLeft;
			}

			case SEARCH_OP.SEARCH_OP_NOT:
			{
				return m_kLeft!.GetAnchor();
			}

			default:
			{
				if (m_bAll || m_sWild != "")	// * ·와일드카드: 모든 줄 확인
				{
					return null;
				}

				if (m_sField != "")
				{
					return new HashSet<string> { "F:" + m_sField };
				}

				return m_arrWord.Length == 0 ? new HashSet<string>() : new HashSet<string> { "W:" + m_arrWord.MaxBy(sWord => sWord.Length) };
			}
		}
	}

	public bool Eval(HashSet<ulong> setWord, HashSet<string>? setField, ReadOnlySpan<char> kText)	// 줄이 조건에 맞음 (setWord: 줄 단어 해시, kText: 앞 시간 뺀 원문)
	{
		switch (m_eOp)
		{
			case SEARCH_OP.SEARCH_OP_AND:
			{
				return m_kLeft!.Eval(setWord, setField, kText) && m_kRight!.Eval(setWord, setField, kText);
			}

			case SEARCH_OP.SEARCH_OP_OR:
			{
				return m_kLeft!.Eval(setWord, setField, kText) || m_kRight!.Eval(setWord, setField, kText);
			}

			case SEARCH_OP.SEARCH_OP_NOT:
			{
				return m_kLeft!.Eval(setWord, setField, kText) && !m_kRight!.Eval(setWord, setField, kText);
			}

			default:
			{
				if (m_bAll)
				{
					return true;
				}

				if (m_sField != "")
				{
					return setField != null && setField.Contains(m_sField);
				}

				if (m_sWild != "")	// 와일드카드: 원문 확인 (쿼리와 같음)
				{
					return IsWildMatch(kText, m_sWild, m_bWildHead, m_bWildTail);
				}

				if (m_arrHash.Length == 0)
				{
					return false;
				}

				foreach (ulong nHash in m_arrHash)
				{
					if (!setWord.Contains(nHash))
					{
						return false;
					}
				}

				return m_sExact == "" || kText.Contains(m_sExact.AsSpan(), StringComparison.OrdinalIgnoreCase);
			}
		}
	}
}

class AlarmJob	// 발송 작업 1건 (알람 발생 × 채널, 펜딩 pending.json 항목)
{
	public long		nLogId		{ get; set; }								// 알람 로그 줄 번호
	public long		nRetryOf	{ get; set; }								// 재전송: 처음 줄 번호 (0: 처음)
	public int		nTry		{ get; set; }								// 보낸 횟수 (재전송 때 +1)
	public long		nMinute		{ get; set; }								// 판정 분 (유닉스 분, 펜딩 유효 시간)
	public int		nID			{ get; set; }								// 알람 ID
	public string	sKey		{ get; set; } = "";							// 알람 제목
	public string	sQuery		{ get; set; } = "";							// 그 시점 조건
	public string	sTime		{ get; set; } = "";							// 판정 시각 (KST)
	public string	sWindow		{ get; set; } = "";							// 구간 (HH:mm~HH:mm)
	public int		nCount		{ get; set; }								// 구간 건수
	public int		nLimit		{ get; set; }								// 기준 건수
	public string	sChannel	{ get; set; } = "";							// Mail·Hook
	public string[]	arrTarget	{ get; set; } = Array.Empty<string>();		// 받는 곳 (메일 주소·훅 주소, 로그엔 안 씀)
	public string	sTitle		{ get; set; } = "";							// 제목
	public string	sText		{ get; set; } = "";							// 내용
	public string	sLink		{ get; set; } = "";							// 쿼리 탭 바로 실행 주소 (알람 기록: 안 보임·내용 클릭 쿼리, 메일·훅: sApiLink 없을 때만, 없음: 링크 없음)
	public string	sApiLink	{ get; set; } = "";							// 외부 API 쿼리 주소 (/api/external/query/키/토큰/?sType=&nStart=&nEnd=, 관리 계정 키·토큰, 브라우저 열기: 로그인 → 쿼리 탭 바로 실행, 메일·훅 본문 끝 링크, 없음: sLink)
}

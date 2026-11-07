class SavedList	// 저장 목록 (저장 쿼리 aQuery → Query.json·저장 서치 aSearch → Search.json): 항목 {sKey, sQuery, nMinute}, 설정 파일 읽기·저장·API (목록·저장·삭제·순서)
{
	public const int	m_nKeyMax		= 50;		// 제목 최대 글자 (알람 제목 공통)
	public const int	m_nQueryMax		= 1000;		// 쿼리·검색어 최대 글자 (알람 쿼리 공통)
	public const int	m_nMinuteMax	= 129600;	// 간격 최대 (분, 페이지 SAVED_MINUTE_MAX)
	public const int	m_nMinute		= 10;		// 간격 기본 (분, 페이지 SAVED_MINUTE)

	private readonly string												m_sKey;					// 설정 키 (aQuery·aSearch)
	private readonly string												m_sName;				// 로그 이름 (저장 쿼리·저장 서치)
	private readonly string												m_sWord;				// 항목 글 (쿼리·검색어)
	private readonly string												m_sResult;				// 응답 배열 키 (arrQuery·arrSearch)
	private readonly object												m_kLock	= new object();	// 바꾸기 1건씩
	private volatile List<(string sKey, string sQuery, int nMinute)>	m_listSaved;			// 목록 (바꿀 때 통째로)

	public SavedList(string sKey, string sName, string sWord, string sResult)
	{
		m_sKey		= sKey;
		m_sName		= sName;
		m_sWord		= sWord;
		m_sResult	= sResult;
		m_listSaved	= Load();
	}

	public void MapRoute(WebApplication WebApp, string sPath)	// sPath: /api/query/saved·/api/search/saved (+ /delete·/move)
	{
		WebApp.MapGet(sPath, OnList);
		WebApp.MapPost(sPath, OnSave);
		WebApp.MapPost(sPath + "/delete", OnDelete);
		WebApp.MapPost(sPath + "/move", OnMove);
	}

	public static List<T> MoveItem<T>(List<T> listItem, int nFrom, int nTo)	// nFrom 항목을 nTo 자리로 옮긴 새 목록 (범위 밖: 처음·끝)
	{
		List<T>	listMove	= new List<T>(listItem);
		T		kItem		= listMove[nFrom];

		listMove.RemoveAt(nFrom);
		listMove.Insert(Math.Clamp(nTo, 0, listMove.Count), kItem);

		return listMove;
	}

	private IResult OnList()	// 목록 (팝업 열 때마다: 설정 파일 다시 읽음)
	{
		Reload();

		return Result("");
	}

	private IResult OnSave(SavedParam kParam, HttpRequest kRequest)	// 저장: sOldKey 그 자리 수정 (제목 겹침: 오류), 없음: 같은 제목 바꿈·끝에 추가
	{
		string	sKey	= (kParam.sKey ?? "").Trim();
		string	sQuery	= (kParam.sQuery ?? "").Trim();
		string	sOldKey	= (kParam.sOldKey ?? "").Trim();
		int		nMinute	= kParam.nMinute ?? m_nMinute;
		string	sError	= sKey == "" || sKey.Length > m_nKeyMax ? $"제목 없음·{m_nKeyMax}자 넘음" : (sQuery == "" || sQuery.Length > m_nQueryMax ? $"{m_sWord} 없음·{m_nQueryMax}자 넘음" : (!SearchNode.IsValid(sQuery) ? $"{m_sWord} 형식 오류 (예: A AND (B NOT C) OR D)" : (nMinute < 1 || nMinute > m_nMinuteMax ? $"간격 범위 오류 (1 ~ {m_nMinuteMax}분)" : "")));

		if (sError == "")
		{
			lock (m_kLock)
			{
				List<(string sKey, string sQuery, int nMinute)>	listSaved	= new List<(string sKey, string sQuery, int nMinute)>(m_listSaved);
				int		nSame	= Find(listSaved, sKey);
				int		nOld	= sOldKey == "" ? -1 : Find(listSaved, sOldKey);
				string	sAction	= nOld >= 0 ? "수정" : (nSame >= 0 ? "바꿈" : "추가");

				if (sOldKey != "" && nOld < 0)
				{
					sError = "없는 제목";
				}
				else if (nOld >= 0 && nSame >= 0 && nSame != nOld)
				{
					sError = "이미 있는 제목";
				}
				else
				{
					if (nOld >= 0)
					{
						listSaved[nOld] = (sKey, sQuery, nMinute);
					}
					else if (nSame >= 0)
					{
						listSaved[nSame] = (listSaved[nSame].sKey, sQuery, nMinute);
					}
					else
					{
						listSaved.Add((sKey, sQuery, nMinute));
					}

					sError = Save(listSaved);
				}

				if (sError == "")
				{
					Log.Write(INFO_, $"{m_sName} {sAction}: {(nOld >= 0 && sOldKey != sKey ? sOldKey + " → " : "")}{sKey} ({sQuery}, {nMinute}분) 요청자 {Web.GetAddr(kRequest.HttpContext)}");
				}
			}
		}

		return Result(sError);
	}

	private IResult OnDelete(SavedParam kParam, HttpRequest kRequest)	// 삭제 (없는 제목: 오류)
	{
		string	sKey	= (kParam.sKey ?? "").Trim();
		string	sError;

		lock (m_kLock)
		{
			List<(string sKey, string sQuery, int nMinute)> listSaved = m_listSaved.Where(kSaved => !string.Equals(kSaved.sKey, sKey, StringComparison.OrdinalIgnoreCase)).ToList();

			sError = listSaved.Count == m_listSaved.Count ? "없는 제목" : Save(listSaved);

			if (sError == "")
			{
				Log.Write(INFO_, $"{m_sName} 삭제: {sKey} 요청자 {Web.GetAddr(kRequest.HttpContext)}");
			}
		}

		return Result(sError);
	}

	private IResult OnMove(SavedParam kParam, HttpRequest kRequest)	// 순서: 그 제목 → nTo 자리
	{
		string	sKey	= (kParam.sKey ?? "").Trim();
		string	sError;

		lock (m_kLock)
		{
			int nFrom = Find(m_listSaved, sKey);

			sError = nFrom < 0 ? "없는 제목" : Save(MoveItem(m_listSaved, nFrom, kParam.nTo ?? nFrom));

			if (sError == "")
			{
				Log.Write(INFO_, $"{m_sName} 순서: {sKey} → {(kParam.nTo ?? nFrom) + 1}번 요청자 {Web.GetAddr(kRequest.HttpContext)}");
			}
		}

		return Result(sError);
	}

	private IResult Result(string sError)	// 응답 {sError, arrQuery·arrSearch: [{sKey, sQuery, nMinute}]}
	{
		return Results.Json(new Dictionary<string, object> { ["sError"] = sError, [m_sResult] = m_listSaved.Select(kSaved => new { kSaved.sKey, kSaved.sQuery, kSaved.nMinute }).ToArray() });
	}

	private static int Find(List<(string sKey, string sQuery, int nMinute)> listSaved, string sKey)	// 제목 자리 (대소문자 무시, 없음: -1)
	{
		return listSaved.FindIndex(kSaved => string.Equals(kSaved.sKey, sKey, StringComparison.OrdinalIgnoreCase));
	}

	private string Save(List<(string sKey, string sQuery, int nMinute)> listSaved)	// 잠금 안: 설정 파일 저장 → 성공: 메모리 목록 바꿈 (반환: 오류, 없음: "")
	{
		JsonArray	kArray	= new JsonArray(listSaved.Select(kSaved => (JsonNode?)new JsonObject { ["sKey"] = kSaved.sKey, ["sQuery"] = kSaved.sQuery, ["nMinute"] = kSaved.nMinute }).ToArray());
		string		sError	= Config.SetArray(m_sKey, kArray);

		if (sError == "")
		{
			m_listSaved = listSaved;
		}

		return sError;
	}

	private List<(string sKey, string sQuery, int nMinute)> Load(IEnumerable<IConfigurationSection>? listSection = null, List<string>? listWarn = null)	// 설정 → 목록 (제목 없음·너무 김·같은 제목: 뺌, 간격 범위 밖: 기본, listWarn: 오류 글 모음, 없음: 바로 출력)
	{
		List<(string sKey, string sQuery, int nMinute)> listSaved = new List<(string sKey, string sQuery, int nMinute)>();

		foreach (IConfigurationSection kSection in listSection ?? Config.GetChildren(m_sKey))
		{
			string	sKey	= (kSection["sKey"] ?? "").Trim();
			string	sQuery	= (kSection["sQuery"] ?? "").Trim();
			int		nMinute	= int.TryParse(kSection["nMinute"], out int nValue) && nValue >= 1 && nValue <= m_nMinuteMax ? nValue : m_nMinute;

			if (sKey == "" || sKey.Length > m_nKeyMax || sQuery.Length > m_nQueryMax || Find(listSaved, sKey) >= 0)
			{
				string sWarn = $"{m_sName} 설정 오류: {m_sKey} {sKey}";

				if (listWarn != null)
				{
					listWarn.Add(sWarn);
				}
				else
				{
					Log.Write(WARN_, sWarn);
				}

				continue;
			}

			listSaved.Add((sKey, sQuery, nMinute));
		}

		return listSaved;
	}

	private void Reload()	// 설정 파일 다시 읽음 → 메모리와 다름: 바꿈 (json 직접 고침 반영, 없음·형식 오류: 그대로)
	{
		string				sPath	= Config.GetKeyFile(m_sKey);
		IConfigurationRoot	kConfig;

		try
		{
			if (!File.Exists(sPath))
			{
				return;
			}

			kConfig = new ConfigurationBuilder().AddJsonFile(sPath, optional: false, reloadOnChange: false).Build();
		}
		catch (Exception kException)	// JSON 형식 오류·사용 중
		{
			Log.Write(WARN_, $"설정 다시 읽기 실패: {sPath} ({kException.Message})");

			return;
		}

		List<string>									listWarn	= new List<string>();
		List<(string sKey, string sQuery, int nMinute)>	listSaved	= Load(kConfig.GetSection(m_sKey).GetChildren(), listWarn);

		lock (m_kLock)
		{
			if (listSaved.SequenceEqual(m_listSaved))
			{
				return;
			}

			m_listSaved = listSaved;
		}

		listWarn.ForEach(sWarn => Log.Write(WARN_, sWarn));

		Log.Write(INFO_, $"{m_sName} 다시 읽음: 설정 파일 {m_sKey} {listSaved.Count}개 (json 직접 고침)");
	}
}

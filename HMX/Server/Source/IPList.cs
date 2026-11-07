class IPList	// 웹 접속 IP 막기 (Json/IP.json, 모든 웹 요청 맨 앞): 블랙리스트 aBlacklist 맞음 → 막음 (우선), 화이트리스트 aWhitelist 있음 → 맞는 IP 만, 비었음·파일 없음: 모두 허용, 칸 범위 '*' (예: 192.168.*.*, *.*.*.*), 파일 바뀜: 재시작 없이 반영
{
	private const string	m_sFile		= "IP.json";	// 목록 파일 (Json 폴더)
	private const int		m_nCheckMs	= 2000;			// 파일 바뀜 확인 간격 (ms)
	private const int		m_nLogMs	= 60000;		// 같은 IP 막음 로그 간격 (ms)
	private const int		m_nLogIPMax	= 10000;		// 막음 로그 기억 IP 최대 (넘음: 비움)

	private sealed record Pattern(string sText, int[]? arrOctet, IPAddress? kExact, bool bAll);	// IP 패턴: 모두 ("*") · IPv4 칸 (-1: '*') · IPv6 그대로
	private sealed record Rule(Pattern[] arrWhite, Pattern[] arrBlack);							// 목록

	private static Rule				m_kRule			= new Rule(Array.Empty<Pattern>(), Array.Empty<Pattern>());	// 지금 목록 (교체만)
	private static long				m_nCheckTick	= -m_nCheckMs;			// 마지막 파일 확인 (TickCount64, 처음: 바로 확인 → 뺄셈 넘침 없음)
	private static DateTime			m_kWriteTime	= DateTime.MaxValue;	// 읽은 파일 수정 시각 (없음: MinValue, 처음: MaxValue)
	private static readonly object	m_kLoadLock		= new object();			// 다시 읽기 1건씩
	private static readonly ConcurrentDictionary<string, long>	m_hmapLog	= new ConcurrentDictionary<string, long>(StringComparer.Ordinal);	// IP → 마지막 막음 로그 (TickCount64)

	public static bool IsAllow(string sAddr)	// 접속 허용: 블랙리스트 맞음 → 막음, 화이트리스트 비었음 → 허용, 그 외: 화이트리스트 맞음만
	{
		Rule		kRule	= GetRule();
		IPAddress?	kAddr	= IPAddress.TryParse(sAddr, out IPAddress? kParse) ? (kParse.IsIPv4MappedToIPv6 ? kParse.MapToIPv4() : kParse) : null;	// 모름 ("-"): null

		if (kRule.arrBlack.Any(kPattern => IsMatch(kPattern, kAddr)))
		{
			return false;
		}

		return kRule.arrWhite.Length == 0 || kRule.arrWhite.Any(kPattern => IsMatch(kPattern, kAddr));
	}

	public static void LogBlock(string sAddr, string sPath)	// 막음 로그 (같은 IP: m_nLogMs 에 1번, 로그 넘침 방지)
	{
		long	nNow	= Environment.TickCount64;

		if (m_hmapLog.Count >= m_nLogIPMax && !m_hmapLog.ContainsKey(sAddr))
		{
			m_hmapLog.Clear();
		}

		if (m_hmapLog.TryGetValue(sAddr, out long nLast) && nNow - nLast < m_nLogMs)
		{
			return;
		}

		m_hmapLog[sAddr] = nNow;

		Log.Write(WARN_, $"IP 막음: {sAddr} {sPath}");
	}

	public static void Check()	// 시작 때 목록 읽기 (로그: 화이트·블랙 수)
	{
		GetRule();
	}

	private static Rule GetRule()	// 지금 목록 (m_nCheckMs 마다 파일 수정 시각 확인 → 바뀜: 다시 읽음)
	{
		long	nNow	= Environment.TickCount64;

		if (nNow - Volatile.Read(ref m_nCheckTick) < m_nCheckMs)
		{
			return Volatile.Read(ref m_kRule);
		}

		lock (m_kLoadLock)
		{
			if (nNow - m_nCheckTick < m_nCheckMs)	// 다른 요청이 먼저 확인
			{
				return m_kRule;
			}

			Volatile.Write(ref m_nCheckTick, nNow);

			string		sPath	= Path.Combine(GetConfigDir(), m_sFile);
			DateTime	kWrite	= File.Exists(sPath) ? File.GetLastWriteTimeUtc(sPath) : DateTime.MinValue;	// 없음: MinValue

			if (kWrite != m_kWriteTime)
			{
				Rule?	kRule	= Load(sPath, kWrite != DateTime.MinValue, out bool bRetry);

				if (!bRetry)	// 쓰는 중 (파일 잠김): 다음 확인 때 다시
				{
					m_kWriteTime = kWrite;
				}

				if (kRule != null)
				{
					Volatile.Write(ref m_kRule, kRule);
				}
			}

			return m_kRule;
		}
	}

	private static Rule? Load(string sPath, bool bExist, out bool bRetry)	// 목록 읽기 (없음: 빈 목록 = 모두 허용, 형식 오류: null → 앞 목록 그대로)
	{
		bRetry = false;

		if (!bExist)
		{
			Log.Write(INFO_, $"IP 목록 없음: {sPath} (모두 허용)");

			return new Rule(Array.Empty<Pattern>(), Array.Empty<Pattern>());
		}

		try
		{
			JsonObject	kRoot		= JsonNode.Parse(File.ReadAllText(sPath), null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject() ?? new JsonObject();
			Pattern[]	arrWhite	= ParseList(kRoot["aWhitelist"], "aWhitelist");
			Pattern[]	arrBlack	= ParseList(kRoot["aBlacklist"], "aBlacklist");

			Log.Write(INFO_, $"IP 목록: 화이트 {arrWhite.Length}·블랙 {arrBlack.Length} ({sPath}{(arrWhite.Length == 0 ? ", 화이트 비었음: 블랙 빼고 모두 허용" : "")})");

			return new Rule(arrWhite, arrBlack);
		}
		catch (IOException)	// 편집기 저장 중 잠김
		{
			bRetry = true;

			return null;
		}
		catch (Exception kException)	// 형식 오류
		{
			Log.Write(ERROR, $"IP 목록 형식 오류: {sPath} ({kException.Message}) → 앞 목록 그대로");

			return null;
		}
	}

	private static Pattern[] ParseList(JsonNode? kNode, string sKey)	// 목록 → 패턴 (틀린 값: 경고 후 뺌)
	{
		List<Pattern>	listPattern	= new List<Pattern>();

		if (kNode is not JsonArray kArray)
		{
			return listPattern.ToArray();
		}

		foreach (JsonNode? kItem in kArray)
		{
			string		sText		= (kItem is JsonValue kValue && kValue.TryGetValue(out string? sValue) ? sValue : "").Trim();
			Pattern?	kPattern	= ParsePattern(sText);

			if (kPattern == null)
			{
				Log.Write(WARN_, $"IP 목록 {sKey} 틀린 값 뺌: \"{sText}\" (예: 10.0.0.1, 192.168.*.*, *.*.*.*, ::1, *)");

				continue;
			}

			listPattern.Add(kPattern);
		}

		return listPattern.ToArray();
	}

	private static Pattern? ParsePattern(string sText)	// 패턴: "*" 모두, IPv4 네 칸 (숫자 0~255·'*'), IPv6 그대로 (틀림: null)
	{
		if (sText == "*")
		{
			return new Pattern(sText, null, null, true);
		}

		if (sText.Contains(':'))	// IPv6
		{
			return IPAddress.TryParse(sText, out IPAddress? kExact) ? new Pattern(sText, null, kExact, false) : null;
		}

		string[]	arrPart		= sText.Split('.');
		int[]		arrOctet	= new int[4];

		if (arrPart.Length != 4)
		{
			return null;
		}

		for (int nIndex = 0; nIndex < 4; ++nIndex)
		{
			if (arrPart[nIndex] == "*")
			{
				arrOctet[nIndex] = -1;
			}
			else if (arrPart[nIndex].Length > 0 && arrPart[nIndex].Length <= 3 && arrPart[nIndex].All(char.IsAsciiDigit) && int.Parse(arrPart[nIndex]) <= 255)
			{
				arrOctet[nIndex] = int.Parse(arrPart[nIndex]);
			}
			else
			{
				return null;
			}
		}

		return new Pattern(sText, arrOctet, null, false);
	}

	private static bool IsMatch(Pattern kPattern, IPAddress? kAddr)	// 패턴 맞음 (IP 모름: "*" 만)
	{
		if (kPattern.bAll)
		{
			return true;
		}

		if (kAddr == null)
		{
			return false;
		}

		if (kPattern.arrOctet != null)	// IPv4 칸
		{
			if (kAddr.AddressFamily != AddressFamily.InterNetwork)
			{
				return false;
			}

			byte[]	arrByte	= kAddr.GetAddressBytes();

			for (int nIndex = 0; nIndex < 4; ++nIndex)
			{
				if (kPattern.arrOctet[nIndex] >= 0 && kPattern.arrOctet[nIndex] != arrByte[nIndex])
				{
					return false;
				}
			}

			return true;
		}

		return kPattern.kExact != null && kPattern.kExact.Equals(kAddr);
	}
}

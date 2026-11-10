static class Config	// 설정 파일 로딩 및 값 조회 (키 구분자 ':', 예: GetString("DB:Dev:sHost"), 따로 관리 키: Main.m_arrConfigSplit 파일) (Server·Client 공통)
{
	private static IConfigurationRoot	m_kConfig	= new ConfigurationBuilder().Build();	// 설정 값 (Load 전·파일 없음: 빈 설정)
	private static readonly object		m_kSaveLock	= new object();							// 설정 파일 쓰기 1건씩
	private static bool					m_bLoaded;											// 한 번 로딩함 (예전 형식 경고: 처음만)
	private static int					m_nVersion;											// 설정 버전 (로딩마다 +1)

	public static int GetVersion()	// 설정 버전 (로딩마다 바뀜 → 설정으로 만든 메모리 맵 다시 만들 때)
	{
		return Volatile.Read(ref m_nVersion);
	}

	public static bool Load()	// 설정 파일 로딩 (false: 형식 오류): Json 폴더 기본 설정 파일 + 따로 관리 키 파일 (예: aQuery → Query.json), 예전 Base 묶음: 최상위로 풀어 읽음
	{
		string		sPath	= GetPath(GetConfigFile());
		JsonObject	kRoot;	// 합친 설정 (메모리)

		try
		{
			kRoot = ReadJson(sPath) ?? new JsonObject();	// 없음·빈 파일: 빈 설정
		}
		catch (Exception kException)	// JSON 형식 오류
		{
			Log.Write(ERROR, $"설정 오류: {sPath} ({kException.Message})");

			return false;
		}

		if (kRoot.Count == 0)
		{
			Log.Write(WARN_, $"설정 파일: 없음 또는 빈 파일 ({sPath})");
		}

		LiftBase(kRoot);

		foreach ((string sKey, string sFile) in Main.m_arrConfigSplit)
		{
			if (!LoadSplit(kRoot, sKey, sFile))
			{
				return false;
			}
		}

		IConfigurationRoot	kConfig	= new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(kRoot.ToJsonString()))).Build();	// 복호화 후 교체 (다시 로딩 중 읽는 쪽: 앞 설정)

		int	nDecrypt	= DecryptCode(kConfig);	// sCode → sPass

		m_kConfig	= kConfig;
		m_bLoaded	= true;

		Interlocked.Increment(ref m_nVersion);	// 바뀐 설정 표시 (설정으로 만든 메모리 맵: 다시 만듦)

		Log.Write(INFO_, $"설정 파일: {sPath} (따로 관리: {Main.m_arrConfigSplit.Length}개, 복호화: {nDecrypt}건)");

		return true;
	}

	private static void LiftBase(JsonObject kRoot)	// 예전 형식 Base 묶음 → 최상위 (최상위에 같은 키: 최상위 우선, 경고: 처음 로딩만)
	{
		if (kRoot["Base"] is not JsonObject kBase)
		{
			return;
		}

		kRoot.Remove("Base");

		foreach (KeyValuePair<string, JsonNode?> kPair in kBase.ToList())
		{
			kBase.Remove(kPair.Key);	// 부모 떼고 옮김

			if (!kRoot.ContainsKey(kPair.Key))
			{
				kRoot[kPair.Key] = kPair.Value;
			}
		}

		if (!m_bLoaded)
		{
			Log.Write(WARN_, $"설정 Base 묶음: 예전 형식 → 최상위로 읽음 ({GetConfigFile()} 의 Base 를 풀어 최상위로 옮기면 됨)");
		}
	}

	private static bool LoadSplit(JsonObject kRoot, string sKey, string sFile)	// 따로 관리 키: 파일 있음 → 그 값 (기본 설정 파일 같은 키: 무시), 파일 없음·기본 설정 파일에 있음 → 파일 만듦 (기본 설정 파일 그대로), false: 형식 오류
	{
		string		sPath	= GetPath(sFile);
		JsonObject?	kSplit;	// 따로 관리 파일 (없음: null)

		try
		{
			kSplit = ReadJson(sPath);
		}
		catch (Exception kException)	// JSON 형식 오류
		{
			Log.Write(ERROR, $"설정 오류: {sPath} ({kException.Message})");

			return false;
		}

		if (kSplit != null)
		{
			if (kRoot.ContainsKey(sKey) && !m_bLoaded)
			{
				Log.Write(WARN_, $"설정 {sKey}: {sFile} 사용 ({GetConfigFile()} 의 {sKey} 무시, 지워도 됨)");
			}

			if (kSplit[sKey] is JsonNode kValue)
			{
				kRoot[sKey] = kValue.DeepClone();
			}
			else
			{
				kRoot.Remove(sKey);	// 파일에 키 없음: 빈 값
			}

			return true;
		}

		if (kRoot[sKey] is JsonNode kOld)	// 예전 형식: 기본 설정 파일 값 → 따로 관리 파일 만듦
		{
			string	sError	= WriteJson(sPath, new JsonObject { [sKey] = kOld.DeepClone() });

			Log.Write(sError == "" ? NOTI_ : WARN_, sError == "" ? $"설정 {sKey}: {sFile} 로 옮김 ({GetConfigFile()} 의 {sKey} 지워도 됨)" : $"설정 {sKey}: {sFile} 만들기 실패 ({sError}, {GetConfigFile()} 값 사용)");
		}

		return true;
	}

	public static string GetKeyFile(string sKey)	// 키를 읽는 파일 경로 (따로 관리 키: 그 파일 있음 → 그 파일, 그 외: 기본 설정 파일)
	{
		string	sSplit	= GetSplitFile(sKey);

		return sSplit != "" && File.Exists(sSplit) ? sSplit : GetPath(GetConfigFile());
	}

	private static string GetPath(string sFile)	// 설정 파일 경로: Json 폴더 (Json 폴더에 없음·예전 위치 기준 폴더에 있음: 그 파일 그대로 읽고 씀, 경고: 처음 로딩만)
	{
		string	sPath	= Path.Combine(GetConfigDir(), sFile);
		string	sOld	= Path.Combine(GetBaseDir(), sFile);

		if (File.Exists(sPath) || !File.Exists(sOld))
		{
			return sPath;
		}

		if (!m_bLoaded)
		{
			Log.Write(WARN_, $"설정 파일: 예전 위치 사용 {sOld} ({GetConfigDir()} 폴더로 옮기면 됨)");
		}

		return sOld;
	}

	private static string GetSplitFile(string sKey)	// 따로 관리 키 파일 경로 (아님: "")
	{
		foreach ((string sSplitKey, string sFile) in Main.m_arrConfigSplit)
		{
			if (string.Equals(sSplitKey, sKey, StringComparison.OrdinalIgnoreCase))
			{
				return GetPath(sFile);
			}
		}

		return "";
	}

	private static JsonObject? ReadJson(string sPath)	// JSON 파일 → 객체 (없음·빈 파일: null, 형식 오류: 예외)
	{
		if (!File.Exists(sPath) || new FileInfo(sPath).Length == 0)
		{
			return null;
		}

		return JsonNode.Parse(FixPathEscape(File.ReadAllText(sPath)), null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject() ?? new JsonObject();
	}

	public static JsonObject ParseFile(string sPath)	// json 파일 → 객체 (설정 읽기와 같은 규칙: 주석·끝 쉼표·경로 \ 하나 허용, 없음·빈 파일: 빈 객체, 형식 오류: 예외 → 시작 검사)
	{
		return ReadJson(sPath) ?? new JsonObject();
	}

	public static string FixPathEscape(string sJson)	// 설정 JSON 글의 윈도 경로 문자열 ("X:\…"·"\\…" 시작): \ 하나도 허용 (서버·클라 공통) → \ 는 모두 글자로 보고 \\ 로 (제대로 쓴 \\ 는 \ 하나, 끝 \" 는 뒤가 구분자면 문자열 끝), 그 밖 문자열: 표준 그대로 (UNC 앞 \\·\\\\: \ 두 개, \" 뒤 구분자 아님: 글자 따옴표)
	{
		StringBuilder	kOut	= new StringBuilder(sJson.Length + 64);
		int				nPos	= 0;

		while (nPos < sJson.Length)
		{
			char	cChar	= sJson[nPos++];

			kOut.Append(cChar);

			if (cChar != '"')
			{
				continue;
			}

			if (!IsPathText(sJson, nPos))	// 보통 문자열: 이스케이프 그대로
			{
				while (nPos < sJson.Length)
				{
					cChar = sJson[nPos++];

					kOut.Append(cChar);

					if (cChar == '\\' && nPos < sJson.Length)
					{
						kOut.Append(sJson[nPos++]);
					}
					else if (cChar == '"')
					{
						break;
					}
				}

				continue;
			}

			if (sJson[nPos] == '\\')	// UNC "\\서버": 앞 \ 두 개 그대로 (\\\\ 표준·\\ 하나 표기 모두)
			{
				nPos += nPos + 3 < sJson.Length && sJson[nPos + 2] == '\\' && sJson[nPos + 3] == '\\' ? 4 : 2;

				kOut.Append("\\\\\\\\");
			}

			while (nPos < sJson.Length)	// 경로 문자열
			{
				cChar = sJson[nPos++];

				if (cChar == '"')	// 끝 (\" 는 아래 \ 에서 처리)
				{
					kOut.Append('"');

					break;
				}

				if (cChar == '\\')
				{
					if (nPos < sJson.Length && sJson[nPos] == '"' && !IsDelimiterAfter(sJson, nPos + 1))	// \" 뒤가 구분자 아님: 글자 따옴표 (구분자: \ 는 글자·" 는 끝)
					{
						kOut.Append("\\\"");
						++nPos;

						continue;
					}

					if (nPos < sJson.Length && sJson[nPos] == '\\')	// \\ 도 \ 하나
					{
						++nPos;
					}

					kOut.Append("\\\\");

					continue;
				}

				kOut.Append(cChar);
			}
		}

		return kOut.ToString();
	}

	private static bool IsPathText(string sJson, int nPos)	// 문자열 시작이 윈도 경로 (X:\ 또는 \\)
	{
		return (nPos + 2 < sJson.Length && char.IsAsciiLetter(sJson[nPos]) && sJson[nPos + 1] == ':' && sJson[nPos + 2] == '\\') || (nPos + 1 < sJson.Length && sJson[nPos] == '\\' && sJson[nPos + 1] == '\\');
	}

	private static bool IsDelimiterAfter(string sJson, int nPos)	// nPos 부터 빈칸 건너뛴 다음 글자가 JSON 구분자 (, ] } :)·끝
	{
		while (nPos < sJson.Length && char.IsWhiteSpace(sJson[nPos]))
		{
			++nPos;
		}

		return nPos >= sJson.Length || sJson[nPos] is ',' or ']' or '}' or ':';
	}

	private static string WriteJson(string sPath, JsonObject kRoot)	// JSON 파일 저장 (폴더 없음: 만듦, 임시 파일 후 교체 → 쓰는 중 실패: 원본 유지, 반환: 오류, 성공: "")
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(sPath)!);
			File.WriteAllText(sPath + ".tmp", kRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\r\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\r\n");
			File.Move(sPath + ".tmp", sPath, true);
		}
		catch (Exception kException)	// 사용 중·권한 없음
		{
			return GetErrorText(kException, $"설정 저장 {sPath}");
		}

		return "";
	}

	private static int DecryptCode(IConfigurationRoot kConfig)	// sCode 값 복호화 → 같은 위치 sPass 로 메모리 보관 (반환: 건수, 마스터 키 없음: 안 함)
	{
		Dictionary<string, string>	hmapPass	= new Dictionary<string, string>();	// sPass 키 → 값 (열거 중 변경 방지용 임시)

		if (GetMasterKey() == "")	// 마스터 키 없음 (서버 첫 접속 입력 전·클라 받기 전): sPass 없음
		{
			return 0;
		}

		foreach (KeyValuePair<string, string?> kPair in kConfig.AsEnumerable())
		{
			if (!string.Equals(ConfigurationPath.GetSectionKey(kPair.Key), "sCode", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(kPair.Value))
			{
				continue;
			}

			string?	sPass	= AES.Decrypt(kPair.Value);

			if (sPass == null)	// 실패: sPass 미등록, 값 미출력
			{
				Log.Write(ERROR, $"설정 복호화 실패: {kPair.Key}");

				continue;
			}

			string?	sParent	= ConfigurationPath.GetParentPath(kPair.Key);	// 최상위: null

			hmapPass[sParent == null ? "sPass" : ConfigurationPath.Combine(sParent, "sPass")] = sPass;
		}

		foreach (KeyValuePair<string, string> kPass in hmapPass)
		{
			kConfig[kPass.Key] = kPass.Value;
		}

		return hmapPass.Count;
	}

	public static string GetString(string sKey, string sDefault = "")	// 문자열 값 (키 없음: 기본값)
	{
		return m_kConfig[sKey] ?? sDefault;
	}

	public static int GetInt(string sKey, int nDefault = 0)	// 정수 값 (키 없음·변환 실패: 기본값)
	{
		return int.TryParse(m_kConfig[sKey], out int nValue) ? nValue : nDefault;
	}

	public static double GetDouble(string sKey, double dDefault = 0)	// 실수 값 (키 없음·변환 실패: 기본값, JSON 소수점 → InvariantCulture)
	{
		return double.TryParse(m_kConfig[sKey], NumberStyles.Float, CultureInfo.InvariantCulture, out double dValue) ? dValue : dDefault;
	}

	public static bool GetBool(string sKey, bool bDefault = false)	// bool 값 (키 없음·변환 실패: 기본값)
	{
		return bool.TryParse(m_kConfig[sKey], out bool bValue) ? bValue : bDefault;
	}

	public static string[] GetArray(string sKey)	// 배열 값 (빈 값 제외, 키 없음: 빈 배열)
	{
		return m_kConfig.GetSection(sKey).GetChildren().Select(kChild => kChild.Value ?? "").Where(sValue => !string.IsNullOrWhiteSpace(sValue)).ToArray();
	}

	public static IConfigurationSection GetSection(string sKey)	// 하위 섹션 (객체·배열 구분: 키 값으로, 없음: 빈 섹션)
	{
		return m_kConfig.GetSection(sKey);
	}

	public static IEnumerable<IConfigurationSection> GetChildren(string sKey)	// 하위 값들 (배열: 순서대로)
	{
		return m_kConfig.GetSection(sKey).GetChildren();
	}

	public static string SetArray(string sKey, JsonArray kArray)	// 최상위 배열 값 저장 후 다시 로딩 (따로 관리 키: 그 파일 통째로, 그 외: 기본 설정 파일의 그 키만·다른 키 유지, 반환: 오류, 성공: "")
	{
		return SetNode(sKey, kArray);
	}

	public static string SetValue(string sKey, string sValue)	// 최상위 문자열 값 저장 후 다시 로딩 (SetArray 와 같음, 예: 관리 계정 sKey → Account.json)
	{
		return SetNode(sKey, JsonValue.Create(sValue));
	}

	private static string SetNode(string sKey, JsonNode kNode)	// 최상위 값 저장 후 다시 로딩 (따로 관리 키: 그 파일, 그 외: 기본 설정 파일 → 그 키만 바꿈·다른 키 유지, 반환: 오류, 성공: "")
	{
		string	sSplit	= GetSplitFile(sKey);	// 따로 관리 파일 (아님: "")
		string	sPath	= sSplit != "" ? sSplit : GetPath(GetConfigFile());

		lock (m_kSaveLock)
		{
			JsonObject	kRoot;	// 저장할 파일 내용

			try
			{
				kRoot = ReadJson(sPath) ?? new JsonObject();	// 파일 없음·빈 파일: 새로 (같은 파일 다른 키: 유지, 예: Account.json aAccount·sKey)
			}
			catch (Exception kException)	// JSON 형식 오류
			{
				return GetErrorText(kException, $"설정 저장 {sPath} {sKey}");
			}

			kRoot[sKey] = kNode;

			string	sError	= WriteJson(sPath, kRoot);

			if (sError != "")
			{
				return sError;
			}

			return Load() ? "" : "설정 다시 로딩 실패";
		}
	}
}

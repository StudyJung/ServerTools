static class ConfigCheck	// 서버 시작 맨 앞: Json 폴더 모든 json 미리 읽기·검사 (형식·값 오류: 파일·필드·이유 ERROR 로그 → 서버 종료), 실행 중 다시 읽기: 기능마다 그대로 (경고·앞 값 유지)
{
	private static readonly string[]	m_arrBool	= { "bLog", "bWeb", "bAuth", "bHttps", "bTest" };	// bool 키 (true·false 만)
	private static readonly string[]	m_arrPort	= { "nPort", "Redis:nPort", "Mail:nPort" };			// 포트 키 (1 ~ 65535)
	private static readonly string[]	m_arrIPKey	= { "aWhiteTCP", "aBlackWeb" };						// IP.json 키 (이것만)

	public static bool CheckSyntax(List<string> listError)	// 1단계 (설정 로딩 전): 모든 json 형식 (설정 읽기와 같은 규칙: 주석·끝 쉼표·경로 \ 하나 허용) + IP.json 있음 (없음: 클라 TCP 모두 거절 → 오류)
	{
		if (!File.Exists(Path.Combine(GetConfigDir(), "IP.json")))
		{
			listError.Add("IP.json 없음 (aWhiteTCP: 클라 TCP 접속 허용 IP, aBlackWeb: 웹 막을 IP)");
		}

		foreach (string sPath in GetFiles())
		{
			try
			{
				Config.ParseFile(sPath);
			}
			catch (Exception kException)	// 형식 오류 (줄·위치: 예외 글)
			{
				listError.Add($"{Path.GetFileName(sPath)} 형식 오류 ({kException.Message})");
			}
		}

		return listError.Count == 0;
	}

	public static bool CheckValue(List<string> listError)	// 2단계 (설정 로딩 뒤: 계정·탭 서로 참조): 모든 json 값, 파일 안 키마다 그 기능 검사
	{
		foreach (string sPath in GetFiles())
		{
			string			sFile		= Path.GetFileName(sPath);
			List<string>	listFile	= new List<string>();	// 이 파일 오류
			JsonObject		kRoot;

			try
			{
				kRoot = Config.ParseFile(sPath);
			}
			catch	// 형식 오류: 1단계에서 걸림
			{
				continue;
			}

			IConfiguration	kFile	= new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(kRoot.ToJsonString()))).Build();	// 기능 검사용 (설정 읽기와 같은 값)

			CheckTop(kFile, listFile);
			CheckIP(sFile, kRoot, kFile, listFile);
			SavedList.CheckConfig(kFile, listFile);
			TabQuery.CheckConfig(kFile, listFile);
			Auth.CheckConfig(kFile, listFile);
			OperationMonitor.CheckConfig(kFile, listFile);

			listError.AddRange(listFile.Select(sError => $"{sFile} {sError}"));
		}

		return listError.Count == 0;
	}

	public static int Fail(List<string> listError)	// 오류 로그 → 종료 코드 1
	{
		foreach (string sError in listError)
		{
			Log.Write(ERROR, $"설정 오류: {sError}");
		}

		Log.Write(FATAL, $"설정 오류 {listError.Count}건 (Json 폴더 {GetConfigDir()}): 서버 종료 → 고친 뒤 다시 시작");

		return 1;
	}

	private static string[] GetFiles()	// Json 폴더 모든 json (이름 순)
	{
		string	sDir	= GetConfigDir();

		return Directory.Exists(sDir) ? Directory.GetFiles(sDir, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToArray() : Array.Empty<string>();
	}

	private static void CheckTop(IConfiguration kFile, List<string> listFile)	// 최상위·묶음 값: bool·포트·알람 링크 주소·탭 이름
	{
		foreach (string sKey in m_arrBool)
		{
			if (kFile[sKey] is string sValue && !bool.TryParse(sValue, out _))
			{
				listFile.Add($"{sKey}: true·false 만 (\"{sValue}\")");
			}
		}

		foreach (string sKey in m_arrPort)
		{
			if (kFile[sKey] is string sValue && !(int.TryParse(sValue, out int nPort) && nPort >= 1 && nPort <= 65535))
			{
				listFile.Add($"{sKey.Replace(':', '.')}: 1 ~ 65535 (\"{sValue}\")");
			}
		}

		if (kFile["sUrl"] is string sUrl && sUrl != "" && !(Uri.TryCreate(sUrl, UriKind.Absolute, out Uri? kUri) && (kUri.Scheme == Uri.UriSchemeHttp || kUri.Scheme == Uri.UriSchemeHttps)))
		{
			listFile.Add($"sUrl: http·https 주소 (\"{sUrl}\")");
		}

		foreach (string sName in kFile.GetSection("aMenu").GetChildren().Select(kItem => (kItem.Value ?? "").Trim()).Where(sName => !Main.m_arrTab.Contains(sName)))
		{
			listFile.Add($"aMenu: 없는 탭 \"{sName}\" ({string.Join("·", Main.m_arrTab)})");
		}
	}

	private static void CheckIP(string sFile, JsonObject kRoot, IConfiguration kFile, List<string> listFile)	// 접속 IP 목록: IP.json 은 aWhiteTCP (TCP)·aBlackWeb (웹) 만 (키 오타: 오류), 값: IP 패턴
	{
		if (string.Equals(sFile, "IP.json", StringComparison.OrdinalIgnoreCase))
		{
			foreach (string sKey in kRoot.Select(kPair => kPair.Key).Where(sKey => !m_arrIPKey.Contains(sKey)))
			{
				listFile.Add($"알 수 없는 키 \"{sKey}\" ({string.Join("·", m_arrIPKey)} 만)");
			}
		}

		foreach (string sKey in m_arrIPKey)
		{
			int	nIndex	= 0;

			foreach (IConfigurationSection kItem in kFile.GetSection(sKey).GetChildren())
			{
				string	sText	= (kItem.Value ?? kItem["sIP"] ?? "").Trim();	// 항목: {"sIP", "sID"}·예전 문자열

				if (kItem.Value == null && kItem.GetChildren().Any(kField => kField.Key is not ("sIP" or "sID")))
				{
					listFile.Add($"{sKey}[{nIndex}]: 항목 키는 sIP·sID 만");
				}

				if (!IPList.IsPattern(sText))
				{
					listFile.Add($"{sKey}[{nIndex}]: 틀린 sIP \"{sText}\" (예: 10.0.0.1, 192.168.*.*, *.*.*.*, ::1, *)");
				}

				++nIndex;
			}
		}
	}
}

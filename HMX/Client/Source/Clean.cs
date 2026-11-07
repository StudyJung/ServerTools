static class Clean	// 날짜 폴더 자동 삭제 (aDeleteFolder): 항목마다 매일 nHour 시 정각 sPath 자신·하위 날짜 폴더 (yyyyMMdd) 중 오늘 - nDay 일 이전 통째 삭제 (오늘: 안 지움, 겹친 하위 항목 경로: 그 항목 규칙만)
{
	private const int	m_nDayMin		= 1;		// 삭제 일수 최소·기본 (일, 최소: 어제 이전)
	private const int	m_nDayMax		= 36500;	// 삭제 일수 최대 (일, 날짜 계산 범위)
	private const int	m_nHourDefault	= 6;		// 삭제 시각 기본 (시, KST 정각)
	private const int	m_nWaitMaxMs	= 60000;	// 다음 삭제 시각까지 한 번 대기 최대 (ms, 시계 바뀜·절전 뒤 다시 계산)

	private static CleanItem[]	m_arrItem	= Array.Empty<CleanItem>();	// 삭제 항목 (빈 sPath·형식 오류·같은 경로 제외)

	public static void Init()	// 설정 aDeleteFolder → 삭제 항목 (빈 sPath: 꺼짐, 상대 경로·드라이브 루트·형식 오류: 경고 후 뺌, 같은 경로: 첫 항목만·경고, Main.Create)
	{
		List<(string sPath, int nDay, int nHour)>	listItem	= new List<(string sPath, int nDay, int nHour)>();

		foreach (IConfigurationSection kChild in Config.GetChildren("aDeleteFolder"))
		{
			string	sValue	= (kChild["sPath"] ?? "").Trim();

			if (sValue == "")	// 꺼짐 (예시 항목)
			{
				continue;
			}

			string?	sFull	= GetDeletePath(sValue);

			if (sFull == null)
			{
				Log.Write(WARN_, $"설정 aDeleteFolder 오류: {sValue} (전체 경로만, 드라이브 루트·와일드카드 불가, 예: C:\\NHNServices\\HMXServer\\Data → 뺌)");

				continue;
			}

			if (listItem.Any(kItem => string.Equals(kItem.sPath, sFull, StringComparison.OrdinalIgnoreCase)))	// 같은 경로 (대소문자 무시)
			{
				Log.Write(WARN_, $"설정 aDeleteFolder 중복: {sFull} (첫 항목만 → 뺌)");

				continue;
			}

			int	nDay	= int.TryParse(kChild["nDay"], out int nValue) && nValue >= m_nDayMin ? Math.Min(nValue, m_nDayMax) : m_nDayMin;	// 없음·최소 미만: 최소
			int	nHour	= int.TryParse(kChild["nHour"], out nValue) && nValue >= 0 && nValue <= 23 ? nValue : m_nHourDefault;				// 없음·범위 밖: 기본

			listItem.Add((sFull, nDay, nHour));
		}

		m_arrItem = listItem.Select(kItem => new CleanItem(kItem.sPath, kItem.nDay, kItem.nHour, listItem.Where(kSub => kSub.sPath.StartsWith(Tab.GetRootPrefix(kItem.sPath), StringComparison.OrdinalIgnoreCase)).Select(kSub => kSub.sPath).ToArray())).ToArray();	// 하위 항목: 이 경로 아래 다른 항목 (끝 구분자 붙여 비교 → 이름 앞부분만 같은 폴더 제외)
	}

	private static string? GetDeletePath(string sValue)	// 삭제 항목 전체 경로 (끝 구분자 없음, 상대 경로·드라이브 상대 (C:Log)·드라이브 루트·와일드카드·스트림 지정·형식 오류: null)
	{
		if (!Path.IsPathFullyQualified(sValue))
		{
			return null;
		}

		try
		{
			string	sFull	= Path.TrimEndingDirectorySeparator(Path.GetFullPath(sValue));
			string	sRoot	= Path.GetPathRoot(sFull) ?? "";	// 드라이브 루트 (C:\·\\서버\공유)

			if (sFull.Length <= sRoot.Length || sFull.Substring(sRoot.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(sName => sName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))	// 드라이브 루트·폴더 이름에 못 쓰는 글자 (*?:<>|")
			{
				return null;
			}

			return sFull;
		}
		catch (Exception)	// 형식 오류
		{
			return null;
		}
	}

	public static async Task CleanLoop(CancellationToken kStop)	// 항목마다 매일 nHour 시 정각 삭제 (시작 때 바로 안 함: 다음 그 시각부터, 예외: 로그 후 다음 날, 항목 없음: 바로 끝)
	{
		if (m_arrItem.Length == 0)
		{
			return;
		}

		DateTimeOffset[]	arrNext	= m_arrItem.Select(kItem => GetNextTime(kItem.nHour, GetNow())).ToArray();	// 항목마다 다음 삭제 시각 (KST)

		for (int nIndex = 0; nIndex < m_arrItem.Length; ++nIndex)
		{
			CleanItem	kItem	= m_arrItem[nIndex];

			Log.Write(INFO_, $"날짜 폴더 삭제 등록: {kItem.sPath} ({kItem.nDay}일 이전, 매일 {kItem.nHour}시, 하위 {Define.m_nLogScanDepth}단계{(kItem.arrSub.Length > 0 ? $", 하위 항목 제외: {string.Join(", ", kItem.arrSub)}" : "")}, 다음 {arrNext[nIndex]:yyyy-MM-dd HH:mm})");
		}

		while (!kStop.IsCancellationRequested)
		{
			DateTimeOffset	kNow	= GetNow();

			for (int nIndex = 0; nIndex < m_arrItem.Length && !kStop.IsCancellationRequested; ++nIndex)
			{
				if (arrNext[nIndex] > kNow)	// 아직 그 시각 전
				{
					continue;
				}

				try
				{
					Run(m_arrItem[nIndex], kStop);
				}
				catch (Exception kException)	// 예상 못한 오류: 다음 날 다시
				{
					Log.Write(ERROR, $"날짜 폴더 삭제 오류: {m_arrItem[nIndex].sPath} ({kException.Message})");
				}

				arrNext[nIndex] = GetNextTime(m_arrItem[nIndex].nHour, GetNow());
			}

			double	dWaitMs	= (arrNext.Min() - GetNow()).TotalMilliseconds;	// 가장 가까운 삭제 시각까지

			await Delay((int)Math.Clamp(Math.Ceiling(dWaitMs), 1, m_nWaitMaxMs), kStop);	// 최대 m_nWaitMaxMs 씩 (종료 요청: 바로 반환)
		}

		Log.Write(INFO_, "날짜 폴더 삭제 종료");
	}

	private static DateTimeOffset GetNextTime(int nHour, DateTimeOffset kNow)	// 다음 삭제 시각: kNow 뒤 첫 nHour 시 정각 (KST, 그 시각 지남: 다음 날)
	{
		DateTimeOffset	kTime	= new DateTimeOffset(kNow.Date.AddHours(nHour), kNow.Offset);	// 오늘 nHour 시

		return kTime > kNow ? kTime : kTime.AddDays(1);
	}

	private static long GetLimitDate(DateTimeOffset kNow, int nDay)	// 삭제 기준 날짜 (yyyyMMdd, 이하 삭제): 오늘 - nDay 일 (최소 어제 → 오늘 제외)
	{
		return long.Parse(kNow.AddDays(-Math.Clamp(nDay, m_nDayMin, m_nDayMax)).ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
	}

	private static void Run(CleanItem kItem, CancellationToken kStop)	// 1회 삭제: 기준 날짜 이하 날짜 폴더 통째 (폴더마다 부하 조절, 안에 폴더 링크·실패: 경고 후 다음 폴더, 끝: 합계 1줄)
	{
		long			nBegin		= Stopwatch.GetTimestamp();
		long			nLimit		= GetLimitDate(GetNow(), kItem.nDay);	// 삭제 기준 날짜 (yyyyMMdd)
		Throttle		kThrottle	= new Throttle();
		int				nDelete		= 0;	// 삭제 수
		int				nFail		= 0;	// 실패 수
		int				nSkip		= 0;	// 링크 건너뜀 수
		List<string>	listFolder;			// 삭제 대상

		if (!Directory.Exists(kItem.sPath))
		{
			Log.Write(WARN_, $"날짜 폴더 삭제 건너뜀: {kItem.sPath} (폴더 없음)");

			return;
		}

		if (Tab.HasLink(kItem.sPath, kItem.sPath))	// 항목 경로 자신이 링크 (정션·심볼릭): 안 들어감
		{
			Log.Write(WARN_, $"날짜 폴더 삭제 건너뜀: {kItem.sPath} (정션·심볼릭 링크, 실제 경로로 등록)");

			return;
		}

		try
		{
			listFolder = FindDateFolder(kItem, nLimit, kThrottle);
		}
		catch (Exception kException)	// 권한 없음·경로 오류
		{
			Log.Write(WARN_, $"날짜 폴더 삭제 탐색 오류: {kItem.sPath} ({kException.Message})");

			return;
		}

		foreach (string sFolder in listFolder)
		{
			if (kStop.IsCancellationRequested)	// 종료 요청: 남은 폴더 다음 날
			{
				break;
			}

			try
			{
				if (HasDirLink(sFolder, kThrottle))	// 안에 폴더 링크: 안 지움 (링크·대상 그대로)
				{
					++nSkip;

					Log.Write(WARN_, $"날짜 폴더 삭제 건너뜀: {sFolder} (안에 정션·심볼릭 링크 폴더)");
				}
				else
				{
					Directory.Delete(sFolder, true);

					++nDelete;

					Log.Write(INFO_, $"날짜 폴더 삭제: {sFolder}");
				}
			}
			catch (Exception kException)	// 사용 중·권한 없음: 다음 폴더 계속 (다음 날 다시)
			{
				++nFail;

				Log.Write(WARN_, $"날짜 폴더 삭제 실패: {sFolder} ({kException.Message})");
			}

			kThrottle.Check();
		}

		Log.Write(INFO_, $"날짜 폴더 삭제 끝: {kItem.sPath} ({nLimit} 이하, 대상 {listFolder.Count}개·삭제 {nDelete}개·실패 {nFail}개·링크 건너뜀 {nSkip}개, {(long)Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds}ms)");
	}

	private static List<string> FindDateFolder(CleanItem kItem, long nLimit, Throttle kThrottle)	// 삭제 대상: sPath 하위 X단계 날짜 폴더 중 nLimit 이하 (날짜 폴더 안·숨김·시스템·링크·하위 항목 경로: 안 들어감, 하위 항목 경로 담은 날짜 폴더: 뺌, 경로 순)
	{
		EnumerationOptions	kOption	= new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint, RecurseSubdirectories = true, MaxRecursionDepth = Define.m_nLogScanDepth };	// 하위 폴더 (깊이 제한, 링크 제외 → sPath 밖으로 안 나감)

		FileSystemEnumerable<string> kDateDir = new FileSystemEnumerable<string>(kItem.sPath, (ref FileSystemEntry kEntry) => kEntry.ToFullPath(), kOption)
		{
			ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => kEntry.IsDirectory && Tab.TryGetDate(kEntry.FileName, out long nDate) && nDate <= nLimit && !HasSub(kEntry.ToFullPath(), kItem.arrSub),	// 기준 날짜 이하 날짜 폴더 (하위 항목 경로 담음: 뺌)
			ShouldRecursePredicate	= (ref FileSystemEntry kEntry) =>	// 폴더마다 부하 조절, 날짜 폴더·하위 항목 경로: 안 들어감
			{
				kThrottle.Check();

				return !Tab.TryGetDate(kEntry.FileName, out _) && (kItem.arrSub.Length == 0 || !kItem.arrSub.Contains(kEntry.ToFullPath(), StringComparer.OrdinalIgnoreCase));
			},
		};

		List<string>	listFolder	= kDateDir.ToList();	// 다 찾은 뒤 삭제 (탐색 중 삭제 안 함)

		listFolder.Sort(StringComparer.OrdinalIgnoreCase);

		return listFolder;
	}

	private static bool HasDirLink(string sFolder, Throttle kThrottle)	// 폴더 안 폴더 링크 (정션·심볼릭·마운트) 있음 (링크 안: 안 들어감, 첫 개 찾으면 끝, 파일: 안 봄)
	{
		FileSystemEnumerable<byte> kLink = new FileSystemEnumerable<byte>(sFolder, (ref FileSystemEntry kEntry) => 0, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })	// 숨김·시스템 포함 (지울 폴더 전부)
		{
			ShouldIncludePredicate	= (ref FileSystemEntry kEntry) => kEntry.IsDirectory && (kEntry.Attributes & FileAttributes.ReparsePoint) != 0,
			ShouldRecursePredicate	= (ref FileSystemEntry kEntry) =>	// 폴더마다 부하 조절, 링크 안: 안 들어감
			{
				kThrottle.Check();

				return (kEntry.Attributes & FileAttributes.ReparsePoint) == 0;
			},
		};

		return kLink.Any();
	}

	private static bool HasSub(string sFolder, string[] arrSub)	// sFolder 자신·하위에 하위 항목 경로 있음 (대소문자 무시, 끝 구분자 붙여 비교)
	{
		string	sPrefix	= Tab.GetRootPrefix(sFolder);

		return arrSub.Any(sSub => Tab.GetRootPrefix(sSub).StartsWith(sPrefix, StringComparison.OrdinalIgnoreCase));
	}
}

record CleanItem(string sPath, int nDay, int nHour, string[] arrSub);	// 날짜 폴더 삭제 항목 (aDeleteFolder 1개, sPath: 전체 경로, arrSub: 이 경로 아래 다른 항목 경로 → 그 경로 자신·하위: 그 항목 규칙만)

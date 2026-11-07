class Count	// 로그 감시 상태 (대상 폴더 하나, Server·Client 같은 코드, 한 스레드 전용): 기준 폴더 하위 data\count\머신\대상 폴더 (드라이브 ':' 제거)\Count.data, 메모리 상태 + 파일 열어 둠
//   줄: "날짜 폴더\파일\t줄 번호" (예: 20261022\xrpc20261022_10.log	617), 최대 2줄 (윗줄: 이전, 아랫줄: 최신)
//   저장: 바뀜만, 1·2번째 줄 이어 씀, 셋째 줄부터: 윗줄 지우고 아랫줄 맨 위로 + 새 줄 둘째 줄 (쓰기 1번 → 남은 뒤 자름), 실패: 닫고 다음 저장 때 처음부터 정리
//     예) 10.log 1 / 11.log 2 → 11.log 617 들어옴 → 11.log 2 / 11.log 617
//   읽기: 아랫줄 기준, 줄바꿈 없는 마지막 줄 (쓰다 끊김) 버림, 바른 줄 중 맨 밑 줄이 윗줄보다 진행 (파일 날짜·시간, 줄 번호) 작음: 쓰다 끊김 → 윗줄
//     예) 10.log 617 / 10.log 6 → 617, 10.log 617 / 11.log 617: 정상 → 아랫줄
{
	private const int	m_nFileLineMax	= 2;	// 파일 줄 수 (참: 다음 저장 때 윗줄 지우고 아랫줄 맨 위로)

	private static readonly Encoding	m_kUtf8	= new UTF8Encoding(false);	// UTF-8 (BOM 없음)

	public readonly string	m_sFile;			// 상태 파일 ("": 머신·폴더 오류 → 안 읽고 안 씀)
	public string			m_sRel		= "";	// 마지막 저장 "날짜 폴더\파일" (없음: "")
	public long				m_nDateHour	= -1;	// 마지막 저장 파일 날짜·시간 (yyyyMMddHH, 진행 비교, 없음: -1)
	public long				m_nLine		= -1;	// 마지막 저장 줄 번호 (없음: -1)

	private FileStream?	m_kStream;		// 상태 파일 (쓰기로 열어 둠, null: 안 열림)
	private int			m_nFileLine;	// 파일 줄 수 (m_nFileLineMax: 참·모름 → 다음 저장 때 처음부터 정리)

	public Count(string sMachine, string sFolder)
	{
		m_sFile = GetFile(sMachine, sFolder);
	}

	public static string GetRoot(string sMachine)	// 머신 상태 폴더: 기준 폴더 하위 data\count\머신
	{
		return Path.Combine(GetBaseDir(), "data", "count", sMachine);
	}

	private static string GetFile(string sMachine, string sFolder)	// 상태 파일: data\count\머신\대상 폴더 (드라이브 ':' 제거, \\서버\공유 → 서버\공유)\Count.data (머신·폴더 오류·머신 폴더 밖: "")
	{
		try
		{
			if (sMachine.Trim('.') == "" || sMachine.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Path.IsPathFullyQualified(sFolder))
			{
				return "";
			}

			string	sRoot	= Path.GetFullPath(GetRoot(sMachine)) + Path.DirectorySeparatorChar;	// 머신 폴더 (끝 구분자 포함)
			string	sFile	= Path.GetFullPath(Path.Combine(sRoot, sFolder.Replace(":", "").TrimStart('\\', '/'), "Count.data"));

			return sFile.StartsWith(sRoot, StringComparison.OrdinalIgnoreCase) ? sFile : "";	// 머신 폴더 밖 (..\ 등): 거부
		}
		catch (Exception)	// 경로 형식 오류
		{
			return "";
		}
	}

	public static long GetDateHour(string sRel)	// "날짜 폴더\…yyyyMMdd_HH.log" → 파일 날짜·시간 yyyyMMddHH (날짜 폴더 yyyyMMdd·파일명 끝 yyyyMMdd_HH.log (앞에 숫자 없음) 아님: -1)
	{
		string	sDate	= Path.GetDirectoryName(sRel) ?? "";	// 날짜 폴더
		string	sName	= Path.GetFileName(sRel);				// 파일

		if (sDate.Length != 8 || !DateTime.TryParseExact(sDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || sName.Length < 15 || !sName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || (sName.Length > 15 && char.IsAsciiDigit(sName[^16])) || !DateTime.TryParseExact(sName.AsSpan(sName.Length - 15, 11), "yyyyMMdd_HH", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kTime))
		{
			return -1;
		}

		return ((kTime.Year * 100L + kTime.Month) * 100 + kTime.Day) * 100 + kTime.Hour;
	}

	public bool Load(string? sOld, out string sRel, out long nLine)	// 상태 읽기: 파일 (없음: sOld 줄 → 바로 새 파일에 씀) → 고른 줄 (sRel "": 없음·경로 깨짐, nLine -1: 줄 번호만 깨짐 → 경로만, 반환: 읽기·옮겨 쓰기 실패 false)
	{
		sRel	= "";
		nLine	= -1;

		if (m_sFile == "")
		{
			return false;
		}

		bool		bExist;		// 상태 파일 있음
		string[]	arrState;	// 끝난 줄 (줄바꿈 있음)

		try
		{
			bExist		= File.Exists(m_sFile);
			arrState	= sOld == null ? [] : [sOld];

			if (bExist)
			{
				using StreamReader kReader = new StreamReader(new FileStream(m_sFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), m_kUtf8);	// 다른 곳이 열어 써도 읽음

				arrState = kReader.ReadToEnd().Split('\n')[..^1];	// 마지막 조각: 줄바꿈 없음 (쓰다 끊김·빈 조각) → 뺌
			}
		}
		catch (Exception kException)	// 권한 없음·사용 중
		{
			Log.Write(WARN_, $"쿼리 상태 읽기 실패: {m_sFile} ({kException.Message})");

			m_nFileLine = m_nFileLineMax;	// 첫 저장 때 처음부터 정리

			return false;
		}

		m_nFileLine = bExist ? m_nFileLineMax : 0;	// 남은 파일: 다음 저장 때 정리 (고른 줄 맨 위 + 새 줄)

		(string sRel, long nDateHour, long nLine)?	kLast	= null;	// 바른 줄 중 맨 밑
		(string sRel, long nDateHour, long nLine)?	kPrev	= null;	// 그 윗줄 (바른 줄)

		for (int nIndex = arrState.Length - 1; nIndex >= 0 && kPrev == null; --nIndex)
		{
			string[]	arrPart		= arrState[nIndex].Trim().Split('\t');	// 경로, 줄 번호 (앞뒤 빈칸·\r 무시)
			string		sPart		= arrPart[0].Trim();					// 날짜 폴더\파일
			long		nDateHour	= GetDateHour(sPart);
			long		nPart		= arrPart.Length == 2 && long.TryParse(arrPart[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long nValue) ? nValue : -1;	// 줄 번호 (빈칸·숫자 아님·탭 없음·칸 더 있음: -1)

			if (nDateHour < 0)	// 경로 깨짐
			{
				continue;
			}

			if (nPart < 0)	// 줄 번호만 깨짐: 바른 줄 없을 때 경로만 (맨 밑 것)
			{
				sRel = sRel == "" ? sPart : sRel;
			}
			else if (kLast == null)
			{
				kLast = (sPart, nDateHour, nPart);
			}
			else
			{
				kPrev = (sPart, nDateHour, nPart);
			}
		}

		(string sRel, long nDateHour, long nLine)?	kState	= kLast;	// 고른 줄

		if (kLast != null && kPrev != null && (kLast.Value.nDateHour, kLast.Value.nLine).CompareTo((kPrev.Value.nDateHour, kPrev.Value.nLine)) < 0)	// 윗줄보다 진행 작음: 쓰다 끊김
		{
			Log.Write(WARN_, $"쿼리 상태 복구: {m_sFile} (맨 밑 줄 {kLast.Value.sRel} {kLast.Value.nLine}줄 < 윗줄 {kPrev.Value.sRel} {kPrev.Value.nLine}줄 → 쓰다 끊김, 윗줄로)");

			kState = kPrev;
		}

		if (kState == null)	// 바른 줄 없음 (sRel: 줄 번호만 깨진 경로·없음 "")
		{
			return true;
		}

		m_sRel		= sRel	= kState.Value.sRel;
		m_nDateHour	= kState.Value.nDateHour;
		m_nLine		= nLine	= kState.Value.nLine;

		return bExist || Write($"{m_sRel}\t{m_nLine}");	// 예전 상태에서 옮김: 바로 새 파일에 씀
	}

	public bool IsNewer(long nDateHour, long nLine)	// 저장한 진행 (파일 날짜·시간, 줄 번호) 보다 뒤 (저장 없음: 모두 뒤)
	{
		return (nDateHour, nLine).CompareTo((m_nDateHour, m_nLine)) > 0;
	}

	public bool Save(string sRel, long nLine)	// 상태 저장 (바뀜만, 반환: 실패 false → 다음 저장 때 다시)
	{
		if (sRel == m_sRel && nLine == m_nLine)
		{
			return true;
		}

		if (m_sFile == "" || !Write($"{sRel}\t{nLine}"))
		{
			return false;
		}

		m_sRel		= sRel;
		m_nDateHour	= GetDateHour(sRel);
		m_nLine		= nLine;

		return true;
	}

	private bool Write(string sState)	// 줄 쓰기: 안 참 → 맨 밑에 이어 씀, 참 (셋째 줄) → 윗줄 지우고 아랫줄 (마지막 저장 줄) 맨 위로 + 새 줄 둘째 줄 (쓰기 1번 → 남은 뒤 자름, 마지막 저장 줄 없음: 새 줄만)
	{
		try
		{
			if (m_kStream == null)	// 처음·닫힘: 폴더·파일 만들고 열어 둠 (다른 곳 읽기·지우기 허용)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(m_sFile)!);

				m_kStream = new FileStream(m_sFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read | FileShare.Delete);
			}

			if (m_nFileLine < m_nFileLineMax)	// 안 참: 맨 밑에 이어 씀
			{
				m_kStream.Position = m_kStream.Length;
				m_kStream.Write(m_kUtf8.GetBytes(sState + "\r\n"));
				m_kStream.Flush();

				++m_nFileLine;
			}
			else	// 셋째 줄: 윗줄 지우고 아랫줄 맨 위로 + 새 줄
			{
				byte[] arrText = m_kUtf8.GetBytes(m_sRel == "" ? sState + "\r\n" : $"{m_sRel}\t{m_nLine}\r\n{sState}\r\n");

				m_kStream.Position = 0;
				m_kStream.Write(arrText);
				m_kStream.Flush();
				m_kStream.SetLength(arrText.Length);

				m_nFileLine = m_sRel == "" ? 1 : 2;
			}

			return true;
		}
		catch (Exception kException)	// 권한 없음·디스크 오류: 닫고 다음 저장 때 처음부터 정리
		{
			Close();

			m_nFileLine = m_nFileLineMax;

			Log.Write(WARN_, $"쿼리 상태 저장 실패: {m_sFile} ({kException.Message})");

			return false;
		}
	}

	public void Close()	// 파일 닫기 (메모리 상태 유지, 다음 저장 때 다시 엶)
	{
		m_kStream?.Dispose();

		m_kStream = null;
	}

	public static void DeleteUnused(string sMachine, IEnumerable<string> listKeep)	// 머신 상태 폴더에서 남길 파일 (listKeep) 아닌 Count.data + 빈 폴더 지움 (파일마다 실패: 출력 후 그대로)
	{
		string	sRoot	= Path.GetFullPath(GetRoot(sMachine));	// 머신 상태 폴더

		if (sMachine.Trim('.') == "" || sMachine.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Directory.Exists(sRoot))	// 머신 이름 오류·상태 없음
		{
			return;
		}

		HashSet<string>	setKeep	= listKeep.ToHashSet(StringComparer.OrdinalIgnoreCase);
		int				nDelete	= 0;

		foreach (string sFile in Directory.EnumerateFiles(sRoot, "Count.data", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Where(sFile => !setKeep.Contains(sFile)).ToList())
		{
			try
			{
				File.Delete(sFile);

				++nDelete;

				for (string? sDir = Path.GetDirectoryName(sFile); sDir != null && sDir.Length > sRoot.Length && !Directory.EnumerateFileSystemEntries(sDir).Any(); sDir = Path.GetDirectoryName(sDir))	// 빈 폴더: 위로 올라가며 지움 (머신 폴더 자신 제외)
				{
					Directory.Delete(sDir);
				}
			}
			catch (Exception kException)	// 사용 중·권한 없음
			{
				Log.Write(WARN_, $"쿼리 상태 지우기 실패: {sFile} ({kException.Message})");
			}
		}

		if (nDelete > 0)
		{
			Log.Write(INFO_, $"쿼리 상태 정리: {sRoot} (관리 목록 밖 폴더 {nDelete}개 지움)");
		}
	}
}

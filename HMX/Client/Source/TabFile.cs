static class TabFile	// 파일 탭: aLogFolder 하위 폴더 보기·파일 조각 읽기 (다운)
{
	private const int	m_nFileListMax		= 10000;			// 폴더 보기 최대 개수 (폴더·파일 합, 초과: bTruncated)

	public static string[]							m_arrBlockFile = Array.Empty<string>();		// aBlockFile 막는 경로 (전체 경로·\이름\, 자신·하위: 파일 탭 폴더 보기·다운 제외, 다른 탭: 그대로)

	public static void Init(string[] arrBlockFile)	// 설정 → 막는 경로 (aBlockFile, Main.Create)
	{
		m_arrBlockFile = Tab.GetBlockRules(arrBlockFile, "aBlockFile");
	}

	public static object FileRead(long nReqId, string sPath, long nOffset, int nCount)	// 파일 다운 조각 (aLogFolder 하위)
	{
		return Tab.ReadChunk("FileReadResult", nReqId, sPath, GetFilePath(sPath), nOffset, nCount);
	}

	private static string? GetFilePath(string sPath)	// 파일 다운 허용 경로: aLogFolder 하위 + 존재 + aBlockFile 밖 (확장자 제한 없음)
	{
		string? sFull = Tab.GetLogFolderPath(sPath);

		return sFull != null && !Tab.IsBlocked(sFull, m_arrBlockFile) ? sFull : null;
	}

	public static object FileList(long nReqId, string sRelPath)	// 폴더 보기: sRelPath 바로 아래 폴더·파일 (이름순, 최대 m_nFileListMax 개, 숨김·시스템·링크 제외), "": 있는 aLogFolder 목록
	{
		List<string>	listDir		= new List<string>();		// 폴더 이름 ("": aLogFolder 전체 경로)
		List<FileItem>	listFile	= new List<FileItem>();
		bool			bTruncated	= false;					// 최대 개수 초과
		string			sError		= "";

		if (sRelPath == "")	// 맨 위: aLogFolder 목록 (페이지가 그대로 경로로 씀 → 하위는 전체 경로)
		{
			listDir.AddRange(Tab.m_arrLogRoot.Where(sRoot => Directory.Exists(sRoot) && !Tab.IsBlocked(sRoot, m_arrBlockFile)));	// aBlockFile: 뺌

			return new { sType = "FilesResult", nReqId, sPath = sRelPath, bTruncated, sError, arrDir = listDir, arrFile = listFile };
		}

		string? sFull = GetFolderPath(sRelPath);

		if (sFull == null)
		{
			sError = "허용되지 않은 경로";
		}
		else
		{
			try
			{
				EnumerationOptions kOption = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };	// 바로 아래만 (링크 폴더 제외 → aLogFolder 밖으로 안 나감)

				FileSystemEnumerable<(bool bDir, string sName, long nSize, DateTimeOffset kTime)> kEntry = new FileSystemEnumerable<(bool bDir, string sName, long nSize, DateTimeOffset kTime)>(sFull, (ref FileSystemEntry kItem) => (kItem.IsDirectory, kItem.FileName.ToString(), kItem.IsDirectory ? 0 : kItem.Length, kItem.LastWriteTimeUtc), kOption);

				foreach ((bool bDir, string sName, long nSize, DateTimeOffset kTime) in kEntry)
				{
					if (listDir.Count + listFile.Count >= m_nFileListMax)	// 최대 개수 초과: 중단
					{
						bTruncated = true;

						break;
					}

					if (bDir)
					{
						if (!Tab.IsBlocked(Path.Combine(sFull, sName), m_arrBlockFile))	// aBlockFile: 뺌
						{
							listDir.Add(sName);
						}
					}
					else
					{
						listFile.Add(new FileItem(sName, nSize, kTime.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
					}
				}

				listDir.Sort(StringComparer.OrdinalIgnoreCase);
				listFile.Sort((kLeft, kRight) => string.Compare(kLeft.sName, kRight.sName, StringComparison.OrdinalIgnoreCase));
			}
			catch (Exception kException)	// 폴더 읽기 실패 (권한·삭제됨)
			{
				sError = GetErrorText(kException, $"폴더 보기 {sRelPath}");
			}
		}

		return new { sType = "FilesResult", nReqId, sPath = sRelPath, bTruncated, sError, arrDir = listDir, arrFile = listFile };
	}

	private static string? GetFolderPath(string sPath)	// 폴더 보기 허용 경로: aLogFolder 자신·하위 + 존재 (그 외: null)
	{
		if (!Path.IsPathFullyQualified(sPath) || sPath.IndexOf(':', 2) >= 0)	// 상대 경로·스트림 지정 거부
		{
			return null;
		}

		string sFull = Tab.GetRootPrefix(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sPath)));	// 끝 구분자 붙여 비교 (aLogFolder 자신 허용)

		foreach (string sRoot in Tab.m_arrLogRoot)
		{
			string sLogRoot = Tab.GetRootPrefix(sRoot);

			if (sFull.StartsWith(sLogRoot, StringComparison.OrdinalIgnoreCase) && !Tab.IsBlocked(sFull, m_arrBlockFile) && Directory.Exists(sFull) && !Tab.HasLink(sLogRoot, sFull))	// aBlockFile 자신·하위: 거부
			{
				return sFull;
			}
		}

		return null;
	}
}

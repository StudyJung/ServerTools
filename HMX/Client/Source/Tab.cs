static class Tab	// 탭 공통: aLogFolder 기준 폴더·막는 폴더 규칙·경로 허용·파일 날짜·블록 읽기 (UTF-8·CP949·UTF-16)·조각 읽기
{
	public static string[]	m_arrLogRoot	= Array.Empty<string>();	// aLogFolder 전체 경로 (끝 구분자 없음 (드라이브 루트 제외), 중복 제외)

	private static Encoding?	m_kCp949;	// CP949 (처음 쓸 때 생성 → Main 의 코드 페이지 등록 뒤)

	public static string GetRootPrefix(string sRoot)	// aLogFolder 끝 구분자 포함 (하위 경로 비교, 드라이브 루트: 그대로)
	{
		return Path.EndsInDirectorySeparator(sRoot) ? sRoot : sRoot + Path.DirectorySeparatorChar;
	}

	public static string[] GetBlockRules(string[] arrBlock, string sKey)	// 막는 경로 값 → 규칙 목록 (전체 경로: 그대로, 폴더 이름·중간 경로: \이름\ 형태, 형식 오류: 경고 후 뺌, 중복 제외)
	{
		List<string>	listRule	= new List<string>();

		foreach (string sBlock in arrBlock)
		{
			string	sValue	= sBlock.Trim();

			if (Path.IsPathFullyQualified(sValue))	// 전체 경로 (예: C:\NHNServices\HMXServer)
			{
				listRule.Add(GetFullFolder(sValue));

				continue;
			}

			string[]	arrName	= sValue.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);	// 폴더 이름·중간 경로 (예: Test·Test\S·TEST1/5/5, 구분자 \·/, 앞뒤 구분자 무시)

			if (arrName.Length == 0 || arrName.Any(sName => sName is "." or ".." || sName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))	// 빈 값·상대 이동·드라이브 상대 (C:Log)·와일드카드
			{
				Log.Write(WARN_, $"설정 {sKey} 오류: {sValue} (전체 경로·폴더 이름·중간 경로만, 예: C:\\NHNServices\\HMXServer·Test·Test\\S → 뺌)");

				continue;
			}

			listRule.Add(Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, arrName) + Path.DirectorySeparatorChar);	// \이름\ (경로 안 이름 단위 비교)
		}

		return listRule.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	public static bool IsBlocked(string sPath, string[] arrBlock)	// 막는 경로 자신·하위 (대소문자 무시, 끝 구분자 붙여 비교 → 이름 앞부분만 같은 폴더 제외)
	//   전체 경로: 앞부분 일치, 폴더 이름·중간 경로 (\이름\): 경로 안 어디든 이름 단위 일치 (예: \Test\S\ → C:\A\Test\S·C:\A\Test\S\B 막음, C:\A\Test\S1·C:\A\MyTest\S: 안 막음)
	{
		if (arrBlock.Length == 0)
		{
			return false;
		}

		string	sFull	= GetRootPrefix(sPath);

		foreach (string sBlock in arrBlock)
		{
			if (Path.IsPathFullyQualified(sBlock) ? sFull.StartsWith(GetRootPrefix(sBlock), StringComparison.OrdinalIgnoreCase) : sFull.Contains(sBlock, StringComparison.OrdinalIgnoreCase))	// 전체 경로: 앞부분, 이름 규칙: 어디든
			{
				return true;
			}
		}

		return false;
	}

	public static string GetFullFolder(string sFolder)	// 폴더 전체 경로 (끝 구분자 없음, 형식 오류: 설정 그대로)
	{
		try
		{
			return Path.TrimEndingDirectorySeparator(Path.GetFullPath(sFolder));
		}
		catch (Exception)
		{
			return Path.TrimEndingDirectorySeparator(sFolder);
		}
	}

	public static bool HasLink(string sRoot, string sFull)	// sRoot 아래 sFull 까지 링크 (정션·심볼릭) 있음 (aLogFolder 밖 우회 방지, 확인 실패: 있음)
	{
		try
		{
			for (string? sPath = Path.TrimEndingDirectorySeparator(sFull); sPath != null && sPath.Length >= sRoot.Length; sPath = Path.GetDirectoryName(sPath))
			{
				FileSystemInfo	kInfo	= Directory.Exists(sPath) ? new DirectoryInfo(sPath) : new FileInfo(sPath);

				if (kInfo.LinkTarget != null)
				{
					return true;
				}
			}
		}
		catch (Exception)	// 권한 없음 등
		{
			return true;
		}

		return false;
	}

	private static bool IsLogFile(ReadOnlySpan<char> sName)	// 검색 대상 확장자 (대소문자 무시)
	{
		return sName.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
	}

	public static ChunkResult ReadChunk(string sResultType, long nReqId, string sPath, string? sFull, long nOffset, int nCount)	// 파일 조각 읽기: nOffset 부터 최대 nCount 바이트 (sFull null: 거부, 풀 버퍼 → 전송 후 Dispose 로 반환)
	{
		byte[]?	arrBuffer	= null;	// 풀 버퍼 (앞 nLength 만 사용)
		int		nLength		= 0;
		long	nSize		= 0;
		string	sError		= "";

		if (sFull == null)
		{
			sError = "허용되지 않은 경로";
		}
		else if (nOffset < 0)
		{
			sError = "읽기 조건 오류";
		}
		else
		{
			try
			{
				using FileStream kStream = new FileStream(sFull, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);	// 쓰는 중인 파일 허용

				nSize		= kStream.Length;
				nLength		= (int)Math.Clamp(nSize - nOffset, 0, Math.Clamp(nCount, 1, Define.m_nDumpChunk));
				arrBuffer	= ArrayPool<byte>.Shared.Rent(Math.Max(1, nLength));	// 풀 재사용 (조각마다 큰 배열 확보 방지)

				kStream.Position = nOffset;
				kStream.ReadExactly(arrBuffer, 0, nLength);
			}
			catch (Exception kException)	// 파일 열기·읽기 실패 (읽는 중 크기 줄어듦 포함)
			{
				if (arrBuffer != null)
				{
					ArrayPool<byte>.Shared.Return(arrBuffer);
				}

				arrBuffer	= null;
				nLength		= 0;
				sError		= GetErrorText(kException, $"파일 읽기 {sPath}");
			}
		}

		return new ChunkResult(sResultType, nReqId, sPath, nOffset, nSize, sError, arrBuffer, nLength);
	}

	public static string? GetLogFolderPath(string sPath)	// aLogFolder 하위 파일 전체 경로: 존재 + 링크 경유 없음 (상대 경로·스트림 지정·aLogFolder 밖: null)
	{
		if (string.IsNullOrEmpty(sPath) || !Path.IsPathFullyQualified(sPath) || sPath.IndexOf(':', 2) >= 0)	// 상대 경로·스트림 지정 거부 (드라이브 ':' 다음 ':' 없음)
		{
			return null;
		}

		string	sLog	= Path.GetFullPath(sPath);

		foreach (string sRoot in m_arrLogRoot)
		{
			string	sLogRoot	= GetRootPrefix(sRoot);

			if (sLog.StartsWith(sLogRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(sLog) && !HasLink(sLogRoot, sLog))
			{
				return sLog;
			}
		}

		return null;
	}

	public static bool TryGetDate(ReadOnlySpan<char> sName, out long nDate)	// 날짜 (yyyyMMdd, 유효 날짜)
	{
		nDate = 0;

		if (sName.Length != 8 || sName.ContainsAnyExceptInRange('0', '9') || !DateTime.TryParseExact(sName, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
		{
			return false;
		}

		nDate = long.Parse(sName, CultureInfo.InvariantCulture);

		return true;
	}

	private static bool TryGetDateHour(ReadOnlySpan<char> sName, out long nDateHour)	// 파일명 날짜·시간 (yyyyMMdd_HH → yyyyMMddHH, 앞뒤 숫자 없음, 여러 개: 마지막)
	{
		nDateHour = 0;

		for (int nIndex = sName.Length - 11; nIndex >= 0; --nIndex)	// 뒤에서부터 (마지막 것)
		{
			ReadOnlySpan<char>	kPart	= sName.Slice(nIndex, 11);	// yyyyMMdd_HH 후보

			if (kPart[8] != '_' || kPart.Slice(0, 8).ContainsAnyExceptInRange('0', '9') || kPart.Slice(9).ContainsAnyExceptInRange('0', '9'))
			{
				continue;
			}

			if ((nIndex > 0 && char.IsAsciiDigit(sName[nIndex - 1])) || (nIndex + 11 < sName.Length && char.IsAsciiDigit(sName[nIndex + 11])))	// 앞뒤에 숫자 이어짐
			{
				continue;
			}

			int	nHour	= (kPart[9] - '0') * 10 + (kPart[10] - '0');

			if (nHour > 23 || !TryGetDate(kPart.Slice(0, 8), out long nDate))	// 유효 시간·날짜 아님
			{
				return false;
			}

			nDateHour = nDate * 100 + nHour;

			return true;
		}

		return false;
	}

	public static bool TryGetLogDateHour(ReadOnlySpan<char> sName, out long nDateHour)	// 로그 감시 파일명 날짜·시간: 이름 끝 yyyyMMdd_HH.log (앞에 숫자 없음, 대소문자 무시, 예: X_20261031_11 - 복사본.log: false)
	{
		nDateHour = 0;

		if (sName.Length < 15 || !IsLogFile(sName) || (sName.Length > 15 && char.IsAsciiDigit(sName[sName.Length - 16])))	// yyyyMMdd_HH.log 보다 짧음·로그 아님·앞에 숫자 이어짐
		{
			return false;
		}

		return TryGetDateHour(sName.Slice(sName.Length - 15, 11), out nDateHour);
	}

	public delegate bool BlockHandler(ReadOnlySpan<char> kBlock);	// 블록 처리 (false: 중단)

	public static void ForEachBlock(string sPath, BlockHandler funcBlock, Throttle kThrottle, long nOffset = 0)	// 파일 블록 읽기: 블록 = 완전한 줄들 (파일 끝·아주 긴 줄 조각 예외, nOffset: 시작 바이트, UTF-16: 항상 처음)
	//   인코딩: 줄마다 UTF-8·CP949 판별 (섞여도 안 깨짐), UTF-8 BOM 건너뜀, UTF-16 BOM: UTF-16
	{
		using FileStream kStream = new FileStream(sPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.SequentialScan);	// 버퍼 없이 블록 단위로 직접 읽음, 순차 읽기 힌트

		Span<byte>	kBom	= stackalloc byte[3];
		int			nBom	= kStream.ReadAtLeast(kBom, 3, throwOnEndOfStream: false);

		if (nBom >= 2 && ((kBom[0] == 0xFF && kBom[1] == 0xFE) || (kBom[0] == 0xFE && kBom[1] == 0xFF)))	// UTF-16 BOM: 글자 단위로 읽음
		{
			kStream.Position = 0;

			using StreamReader kReader = new StreamReader(kStream, Encoding.Unicode, true, Define.m_nReadBufferSize);

			ForEachCharBlock(kReader, funcBlock, kThrottle);

			return;
		}

		kStream.Position = nOffset > 0 ? nOffset : (nBom == 3 && kBom[0] == 0xEF && kBom[1] == 0xBB && kBom[2] == 0xBF ? 3 : 0);	// 지정 위치 (줄 시작) 또는 처음 (UTF-8 BOM 건너뜀)

		byte[]	arrBytes	= ArrayPool<byte>.Shared.Rent(Define.m_nBlockBytes);
		char[]	arrChars	= ArrayPool<char>.Shared.Rent(Define.m_nBlockBytes);	// 글자 수 ≤ 바이트 수
		int		nLen		= 0;	// 바이트 블록 안 데이터 길이

		try
		{
			while (true)
			{
				int	nRead	= kStream.Read(arrBytes, nLen, arrBytes.Length - nLen);
				int	nEnd;	// 넘길 블록 끝

				nLen += nRead;

				if (nLen == 0)	// 빈 파일·다 처리함
				{
					return;
				}

				if (nRead == 0)	// 파일 끝: 남은 전부
				{
					nEnd = nLen;
				}
				else
				{
					int	nLast	= arrBytes.AsSpan(0, nLen).LastIndexOf((byte)'\n');	// 마지막 줄 끝

					if (nLast >= 0)
					{
						nEnd = nLast + 1;
					}
					else if (nLen < arrBytes.Length)	// 줄 끝 없음: 더 읽음
					{
						continue;
					}
					else if (arrBytes.Length < Define.m_nLineBytesMax)	// 버퍼보다 긴 줄: 버퍼 키움
					{
						byte[]	arrNewBytes	= ArrayPool<byte>.Shared.Rent(arrBytes.Length * 2);
						char[]	arrNewChars	= ArrayPool<char>.Shared.Rent(arrNewBytes.Length);

						arrBytes.AsSpan(0, nLen).CopyTo(arrNewBytes);
						ArrayPool<byte>.Shared.Return(arrBytes);
						ArrayPool<char>.Shared.Return(arrChars);

						arrBytes = arrNewBytes;
						arrChars = arrNewChars;

						continue;
					}
					else	// 아주 긴 줄: 조각으로 넘김 (줄 번호는 '\n' 기준이라 유지)
					{
						nEnd = nLen;
					}
				}

				int	nChars	= Decode(arrBytes.AsSpan(0, nEnd), arrChars);

				if (nChars > 0 && !funcBlock(arrChars.AsSpan(0, nChars)))
				{
					return;
				}

				kThrottle.Check();

				if (nRead == 0)
				{
					return;
				}

				arrBytes.AsSpan(nEnd, nLen - nEnd).CopyTo(arrBytes);	// 남은 (끝나지 않은) 줄을 앞으로

				nLen -= nEnd;
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(arrBytes);
			ArrayPool<char>.Shared.Return(arrChars);
		}
	}

	public static int Decode(ReadOnlySpan<byte> kBytes, Span<char> kChars)	// 바이트 블록 → 글자 (전체 UTF-8: 한 번에, 아니면 줄마다 판별 후 같은 인코딩 묶음 변환), 반환: 글자 수
	{
		if (Utf8.IsValid(kBytes))	// 대부분의 UTF-8·영문 로그
		{
			return Encoding.UTF8.GetChars(kBytes, kChars);
		}

		Encoding	kCp949		= m_kCp949 ??= Encoding.GetEncoding(949);
		int			nChars		= 0;
		int			nRunStart	= 0;		// 같은 인코딩 묶음 시작
		int			nPos		= 0;		// 다음 줄 시작
		bool		bRunUtf8	= false;	// 묶음 인코딩 (false: CP949)
		bool		bRunFixed	= false;	// 묶음 인코딩이 정해짐

		while (nPos < kBytes.Length)
		{
			int					nIndex	= kBytes.Slice(nPos).IndexOf((byte)'\n');	// 줄 끝 (상대)
			int					nNext	= nIndex < 0 ? kBytes.Length : nPos + nIndex + 1;
			ReadOnlySpan<byte>	kLine	= kBytes.Slice(nPos, nNext - nPos);

			if (!Ascii.IsValid(kLine))	// 영문뿐인 줄은 어느 쪽이든 같음
			{
				bool	bUtf8	= Utf8.IsValid(kLine);

				if (bRunFixed && bUtf8 != bRunUtf8)	// 인코딩 바뀜: 앞 묶음 변환
				{
					nChars		+= (bRunUtf8 ? Encoding.UTF8 : kCp949).GetChars(kBytes.Slice(nRunStart, nPos - nRunStart), kChars.Slice(nChars));
					nRunStart	= nPos;
				}

				bRunUtf8	= bUtf8;
				bRunFixed	= true;
			}

			nPos = nNext;
		}

		return nChars + (bRunUtf8 ? Encoding.UTF8 : kCp949).GetChars(kBytes.Slice(nRunStart), kChars.Slice(nChars));
	}

	private static void ForEachCharBlock(StreamReader kReader, BlockHandler funcBlock, Throttle kThrottle)	// 글자 단위 블록 읽기 (UTF-16 파일용, 블록 규칙은 ForEachBlock 과 같음)
	{
		char[]	arrBuffer	= ArrayPool<char>.Shared.Rent(Define.m_nBlockBytes);
		int		nLen		= 0;	// 버퍼 안 글자 수

		try
		{
			while (true)
			{
				int	nRead	= kReader.Read(arrBuffer, nLen, arrBuffer.Length - nLen);
				int	nEnd;	// 넘길 블록 끝

				nLen += nRead;

				if (nLen == 0)
				{
					return;
				}

				if (nRead == 0)
				{
					nEnd = nLen;
				}
				else
				{
					int	nLast	= arrBuffer.AsSpan(0, nLen).LastIndexOf('\n');	// 마지막 줄 끝

					if (nLast >= 0)
					{
						nEnd = nLast + 1;
					}
					else if (nLen < arrBuffer.Length)
					{
						continue;
					}
					else if (arrBuffer.Length < Define.m_nLineBytesMax)
					{
						char[]	arrNew	= ArrayPool<char>.Shared.Rent(arrBuffer.Length * 2);

						arrBuffer.AsSpan(0, nLen).CopyTo(arrNew);
						ArrayPool<char>.Shared.Return(arrBuffer);

						arrBuffer = arrNew;

						continue;
					}
					else
					{
						nEnd = nLen;
					}
				}

				if (!funcBlock(arrBuffer.AsSpan(0, nEnd)) || nRead == 0)
				{
					return;
				}

				kThrottle.Check();

				arrBuffer.AsSpan(nEnd, nLen - nEnd).CopyTo(arrBuffer);

				nLen -= nEnd;
			}
		}
		finally
		{
			ArrayPool<char>.Shared.Return(arrBuffer);
		}
	}

	public static string ToText(ReadOnlySpan<char> kLine, int nMax)	// 줄 → 문자열 (끝 '\r' 제거, nMax 초과: 잘라서 끝에 "…")
	{
		if (kLine.Length > 0 && kLine[^1] == '\r')
		{
			kLine = kLine.Slice(0, kLine.Length - 1);
		}

		return kLine.Length <= nMax ? kLine.ToString() : string.Concat(kLine.Slice(0, nMax), "…");
	}
}

class Throttle	// 부하 조절 (서치·보기·덤프·정리 공통): m_nBusyMs 연속 작업 시 m_nRestMs 쉼 (블록마다 확인, 실서버 CPU·디스크 양보, 서버 Gate.Yield 와 같은 값)
{
	private const int	m_nBusyMs	= 500;	// 연속 작업 시간 (ms)
	private const int	m_nRestMs	= 100;	// 쉬는 시간 (ms)

	private long	m_nSliceStart	= Stopwatch.GetTimestamp();	// 작업 시작 타임스탬프

	public void Check()	// m_nBusyMs 초과 시 쉼 (호출 스레드만)
	{
		if (Stopwatch.GetElapsedTime(m_nSliceStart).TotalMilliseconds < m_nBusyMs)
		{
			return;
		}

		Thread.Sleep(m_nRestMs);

		m_nSliceStart = Stopwatch.GetTimestamp();
	}
}

static class AES	// MySQL AES 호환 암호화·복호화 (MySQL 기본 aes-128-ecb, PKCS7 패딩) (Server·Client 공통)
{
    // SELECT TO_BASE64(AES_ENCRYPT('Password', 'ProjectName')); SELECT CAST(AES_DECRYPT(FROM_BASE64('코드'), 'ProjectName') AS CHAR);
    public static string? Decrypt(string sData, string? sKey = null)	// CAST(AES_DECRYPT(FROM_BASE64(sData), 마스터 키) AS CHAR) 호환 (sKey: 확인할 키, 없음: 마스터 키, 키 없음·실패: null)
	{
		sKey ??= GetMasterKey();

		if (string.IsNullOrEmpty(sData) || sKey == "")
		{
			return null;
		}

		byte[]	arrData;

		try
		{
			arrData = Convert.FromBase64String(sData);
		}
		catch (FormatException)
		{
			return null;
		}

		if (arrData.Length == 0 || arrData.Length % 16 != 0)	// AES 블록 단위 아님
		{
			return null;
		}

		try
		{
			using Aes kAes = Aes.Create();

			kAes.Key = GetMySQLKey(sKey);

			return Encoding.UTF8.GetString(kAes.DecryptEcb(arrData, PaddingMode.PKCS7));	// CAST AS CHAR (utf8mb4)
		}
		catch (CryptographicException)	// 키 불일치·패딩 오류
		{
			return null;
		}
	}

	public static string Encrypt(string sText, string? sKey = null)	// TO_BASE64(AES_ENCRYPT(sText, 키)) 호환 (Decrypt 반대, sKey: 쓸 키 (예: 외부 API 토큰 = sCode 를 sRand 키로), 없음: 마스터 키 → 설정 sCode 값, 키 없음: 예외)
	{
		sKey ??= GetMasterKey();

		if (sKey == "")
		{
			throw new InvalidOperationException(GetMasterError());
		}

		using Aes kAes = Aes.Create();

		kAes.Key = GetMySQLKey(sKey);

		return Convert.ToBase64String(kAes.EncryptEcb(Encoding.UTF8.GetBytes(sText), PaddingMode.PKCS7));
	}

	private static byte[] GetMySQLKey(string sKey)	// MySQL 키 변환: 16바이트 버퍼에 순환 XOR
	{
		byte[]	arrKey		= new byte[16];
		byte[]	arrSource	= Encoding.UTF8.GetBytes(sKey);

		for (int nIndex = 0; nIndex < arrSource.Length; ++nIndex)
		{
			arrKey[nIndex % arrKey.Length] ^= arrSource[nIndex];
		}

		return arrKey;
	}
}

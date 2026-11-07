static class Define	// 공유 상수 (Server·Client 공통, 프로젝트별 값: Main)
{
	public const string	m_sSolution	= "HMX";	// 솔루션명 (sCode AES 키, 프로젝트 이름 바뀌어도 그대로)

	public const int	m_nMachineMax	= 1000;				// 한 번에 검색할 최대 머신 수
	public const int	m_nViewWaitSec	= 30;				// 보기 결과 대기 (초)
	public const int	m_nDumpWaitSec	= 60;				// 덤프 목록·조각·삭제 결과 대기 (초)
	public const int	m_nDumpChunk	= 4 * 1024 * 1024;	// 덤프·파일 다운 조각 (바이트, 서버 요청 = 클라 읽기 최대)

	public const int	m_nLogScanDepth		= 4;				// aLogFolder 하위 폴더 깊이 (대상 탐색·덤프 감시·목록)
	public const int	m_nReadBufferSize	= 128 * 1024;		// UTF-16 파일 읽기 버퍼 (바이트)
	public const int	m_nBlockBytes		= 256 * 1024;		// 검색·보기 블록 기본 크기 (바이트, 풀에서 재사용)
	public const int	m_nLineBytesMax		= 8 * 1024 * 1024;	// 블록 최대 크기 (바이트, 이보다 긴 줄은 조각으로 처리)

	public static readonly JsonSerializerOptions	m_kJsonOption	= new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };	// 한글 등 \uXXXX 미변환 (크기 절감)
}

enum SEARCH_OP	// 검색 조건 연산 (SearchNode)
{
	SEARCH_OP_TERM	= 0,	// 검색어 (줄에 있음, 대소문자 무시)
	SEARCH_OP_AND	= 1,	// 왼쪽·오른쪽 모두
	SEARCH_OP_OR	= 2,	// 왼쪽·오른쪽 하나라도
	SEARCH_OP_NOT	= 3,	// 왼쪽이고 오른쪽 아님
}

enum SEARCH_STOP	// 파일 검색 중단 이유
{
	SEARCH_STOP_NONE	= 0,	// 끝까지 검색
	SEARCH_STOP_MAX		= 1,	// 최대 건수 초과
	SEARCH_STOP_TIME	= 2,	// 시간 초과
	SEARCH_STOP_CANCEL	= 3,	// 중지 요청
}

class RelayException(string sMessage) : Exception(sMessage)	// 상대(클라·서버)가 보낸 오류 글 그대로 (GetErrorText 가 바꾸지 않음)
{
}

record FieldRule(string sKey, string sRegex, string sValue);	// 줄 추가 필드 정의 (설정 aField 1개, sValue 있음: 고정값 → sRegex "", 클라 Count 답으로 내려줌)

record LogBatch(string sMachine, string sPath, string sFolder, string sZone, List<string> listLine, List<long>? listNo, List<(string sKey, string sValue)[]>? listField, bool bCount)	// 클라 Log 1건 (TCPServer 수신 → IngestWorker 라이트 큐, listNo·listField: 예전 클라 없음, bCount: 클라 Count 목록 폴더 → 받은 진행 관리)
{
	public HashSet<string>? setPrune { get; init; }	// 받은 진행 정리 표시 (Log 아님, 이 머신 목록 밖 폴더 뺌, 빈 목록: 모두 → 같은 큐라 앞 Log 처리 뒤)
	public string sRecvTime { get; init; } = "";		// 원문 시간 대신 쓸 받은 시각 ([yyyy/MM/dd HH:mm:ss.fff] KST, HTTP /api/external/log: 요청 받은 순간, "": 원문 앞 시간)
}

class SearchParam	// 로그 검색 요청 본문 (POST /api/search, JSON)
{
	public string[]?	arrMachine	{ get; set; }	// 검색할 머신 (결과 nIndex = 이 순서)
	public long?		nStart		{ get; set; }	// 시작 yyyyMMddHH
	public long?		nEnd		{ get; set; }	// 종료 yyyyMMddHH (24시 = 그날 23시 파일까지)
	public string?		sKey		{ get; set; }	// 검색어
	public int?			nMax		{ get; set; }	// 머신당 최대 건수 (페이지 설정값)
	public int?			nLimit		{ get; set; }	// 서치 제한 시간 (초, 페이지 설정값, 클라 검색 마감)
	public string?		sToken		{ get; set; }	// 진행 조회·중지용 (페이지가 만든 검색 표시)
}

class DumpParam	// 덤프 요청 본문 (POST /api/dump: arrMachine, /api/dump/download·delete: sMachine·sPath, JSON)
{
	public string[]?	arrMachine	{ get; set; }	// 조회할 머신 (결과 nIndex = 이 순서)
	public string?		sMachine	{ get; set; }	// 다운·삭제할 머신
	public string?		sPath		{ get; set; }	// 다운·삭제할 덤프 (클라 전체 경로)
}

class SavedParam	// 저장 쿼리·저장 서치 본문 (POST /api/query/saved·/api/search/saved: sKey·sQuery·nMinute·sOldKey, /delete: sKey, /move: sKey·nTo, JSON)
{
	public string?	sKey	{ get; set; }	// 제목
	public string?	sQuery	{ get; set; }	// 쿼리·검색어
	public string?	sOldKey	{ get; set; }	// 수정할 제목 (비움: 추가·같은 제목이면 쿼리만 바꿈)
	public int?		nMinute	{ get; set; }	// 간격 (분, 페이지 D)
	public int?		nTo		{ get; set; }	// 옮길 자리 (0부터)
}

class FieldParam	// 필드 정의 본문 (POST /api/field: sKey·sRegex·sValue·sOldKey, /api/field/delete: sKey, /api/field/move: sKey·nTo, JSON)
{
	public string?	sKey	{ get; set; }	// 이름
	public string?	sRegex	{ get; set; }	// 정규식 (첫 () 값)
	public string?	sValue	{ get; set; }	// 고정값 (있음: 정규식 안 씀)
	public string?	sOldKey	{ get; set; }	// 수정할 필드 이름 (비움: 추가)
	public int?		nTo		{ get; set; }	// 옮길 자리 (0부터)
}

class AlarmParam	// 알람 본문 (POST /api/alarm: nID (0: 추가)·나머지 값, /api/alarm/delete: nID, /api/alarm/move: nID·nTo, JSON)
{
	public int?			nID		{ get; set; }	// ID
	public string?		sKey	{ get; set; }	// 제목
	public string?		sQuery	{ get; set; }	// 쿼리
	public int?			nMinute	{ get; set; }	// 간격 (분)
	public int?			nCount	{ get; set; }	// 건수
	public int?			nDelay	{ get; set; }	// 억제 (분)
	public string[]?	aMail	{ get; set; }	// 메일
	public string?		sHook	{ get; set; }	// 훅 주소
	public string[]?	aSMS	{ get; set; }	// 전화번호
	public bool?		bEnable	{ get; set; }	// 사용
	public int?			nTo		{ get; set; }	// 옮길 자리 (0부터)
}

class AccountParam	// 계정 본문 (POST /api/account: sOldID (비움: 추가)·sID·sPass·sMail·sPhone·aMenu·aAPI·sAdmin, /api/account/delete: sID·sAdmin, /api/account/key: sID·sKey·sAdmin, JSON)
{
	public string?		sOldID	{ get; set; }	// 수정할 계정 아이디 (비움: 추가)
	public string?		sID		{ get; set; }	// 아이디
	public string?		sPass	{ get; set; }	// 비밀번호 (저장: AES.Encrypt → sCode, 수정 비움: 그대로)
	public string?		sMail	{ get; set; }	// 알람 메일 받는 주소 (없음: 메일 안 받음)
	public string?		sPhone	{ get; set; }	// 전화 번호 (숫자·-·+, 없음: 안 씀)
	public string[]?	aMenu	{ get; set; }	// 접근 탭 이름
	public string[]?	aAPI	{ get; set; }	// 외부 API 권한 (쿼리·서치·로그)
	public string?		sAdmin	{ get; set; }	// 관리 비밀번호 (관리 계정 비밀번호)
	public string?		sKey	{ get; set; }	// 외부 API 키 (/api/account/key: 수정 창 새 키, 없음: 서버가 만듦)
}

class AlarmItem	// 알람 1개 (설정 aAlarm 항목·API 응답 같은 이름, nMinute 분 동안 nCount 건 이상: 발생, 발생 뒤 nDelay 분: 억제)
{
	public int		nID		{ get; init; }							// ID (중복 없음, 추가: 최대 + 1)
	public string	sKey	{ get; init; } = "";					// 제목
	public string	sQuery	{ get; init; } = "";					// 쿼리 (검색어 규칙)
	public int		nMinute	{ get; init; }							// 간격 (분)
	public int		nCount	{ get; init; }							// 건수
	public int		nDelay	{ get; init; }							// 억제 (분, 0: 없음)
	public string[]	aMail	{ get; init; } = Array.Empty<string>();	// 받을 메일
	public string	sHook	{ get; init; } = "";					// 훅 주소 (없음: "")
	public string[]	aSMS	{ get; init; } = Array.Empty<string>();	// 받을 전화번호
	public bool		bEnable	{ get; init; }							// 사용
}

record struct SearchLine(string sPath, long nDateHour, long nLine, string sText);	// 검색 결과 한 줄 (속성 이름 그대로 JSON 전송)

record LogField(string sKey, Regex? kRegex, string sValue);	// 보낼 줄 필드 (설정 aField 키·정규식·고정값, kRegex null: 고정값 sValue)

record struct DumpFile(string sPath, long nSize, string sTime);	// 덤프 목록 한 건 (속성 이름 그대로 JSON 전송)

record struct FileItem(string sName, long nSize, string sTime);	// 폴더 보기 파일 한 건 (속성 이름 그대로 JSON 전송)

class ChunkResult : IDisposable	// 조각 읽기 결과 (공개 속성 이름 그대로 JSON 전송, arrData: base64, 풀 버퍼 → 전송 후 Dispose)
{
	private byte[]?	m_arrBuffer;	// 빌린 버퍼 (반환 후·없음: null)

	public string				sType	{ get; }	// DumpReadResult·FileReadResult
	public long					nReqId	{ get; }	// 요청 번호
	public string				sPath	{ get; }	// 요청 경로
	public long					nOffset	{ get; }	// 읽은 시작 위치
	public long					nSize	{ get; }	// 지금 파일 크기
	public string				sError	{ get; }	// 오류 (없음: "")
	public ReadOnlyMemory<byte>	arrData	{ get; }	// 읽은 바이트 (버퍼 앞 부분)

	public ChunkResult(string sResultType, long nRequestId, string sRequestPath, long nReadOffset, long nFileSize, string sReadError, byte[]? arrBuffer, int nLength)
	{
		m_arrBuffer	= arrBuffer;
		sType		= sResultType;
		nReqId		= nRequestId;
		sPath		= sRequestPath;
		nOffset		= nReadOffset;
		nSize		= nFileSize;
		sError		= sReadError;
		arrData		= arrBuffer == null ? ReadOnlyMemory<byte>.Empty : arrBuffer.AsMemory(0, nLength);
	}

	public void Dispose()	// 빌린 버퍼 반환 (두 번 불러도 한 번만)
	{
		if (m_arrBuffer != null)
		{
			ArrayPool<byte>.Shared.Return(m_arrBuffer);

			m_arrBuffer = null;
		}
	}
}

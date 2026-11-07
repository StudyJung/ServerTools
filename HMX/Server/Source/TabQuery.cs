class TabQuery	// 쿼리 탭: 클라 로그 감시 줄 → 레코드 원문 저장·단어·필드 세그먼트 역색인·쿼리·필드별 통계 (구조: Server.cs 머리, 페이지 쪽: QueryHTML)
{
	public const int						m_nIndexWordMax		= 100;				// 색인 단어 최대 글자 (초과: 색인 안 함, 워커 소문자 버퍼 크기)
	public const int						m_nFieldValueMax	= 260;				// 필드 값 최대 글자 (초과: 색인 안 함, Folder 경로 길이)
	private const int						m_nFieldKeyMax		= 32;				// 추가 필드 키 최대 글자 (설정 aField 키)
	private const int						m_nLineFieldMax		= 32;				// 줄마다 추가 필드 최대 수 (넘는 필드: 버림)
	private const int						m_nStatTop			= 20;				// 필드별 통계 값 최대 (건수 많은 순)
	private const int						m_nQueryPage		= 1000;				// 쿼리 결과 한 페이지 건수
	private const int						m_nExternalMinute	= 60;				// 외부 API 쿼리 기본 기간 (분, 시작 없음: 끝 X분 전, 페이지 D 기본과 같음)
	private const int						m_nCacheSec			= 60;				// 쿼리 결과 보관 (초, 같은 쿼리·기간 페이지 넘김은 다시 계산 안 함)
	private const int						m_nCacheMax			= 16;				// 쿼리 결과 보관 수 (여러 사용자·창, 넘으면 가장 오래된 것부터 뺌)
	private const long						m_nCacheHitMax		= 8000000;			// 쿼리 결과 보관분 맞는 줄 (줄당 8바이트) 합 최대 (넘음: 오래된 것부터 뺌 → 큰 쿼리 여러 개 메모리 점유 방지)
	private const int						m_nQueryLimitSec	= 60;				// 쿼리 제한 시간 기본 (초, 요청 nLimit 없음, 페이지 설정 기본과 같음)
	private const int						m_nQueryLimitMax	= 600;				// 쿼리 제한 시간 최대 (초)
	private const int						m_nQueryConcurrent	= 2;				// 동시 쿼리 (전체, 통계·렌즈 포함, 세션마다 1건)
	private static readonly int				m_nQueryParallel	= Math.Max(1, Environment.ProcessorCount / 2);	// 시간 폴더 병렬 수 (코어 절반: 적재·수신 여유)
	private static readonly Gate			m_kGate				= new Gate(m_nQueryConcurrent);	// 쿼리·통계·렌즈 자리 (세션 1건 → 전체)
	private const int						m_nQueryDayMax		= 1096;				// 쿼리·통계 기간 최대 (일, 3년, 시간 수만큼 배열·폴더 확인 → 잘못된 요청 하나의 서버 점유 방지, 페이지 QUERY_DAY_MAX 와 같음)
	private const int						m_nHitKeepHour		= 2;				// 기간 X시간 이하: 맞는 줄 결과에 보관 (분별 건수·페이지 다시 안 읽음, 긴 기간: 페이지 때 그 시간만 다시 읽음)
	private const int						m_nLensBarMax		= 86400;			// 렌즈 초·분봉 칸 최대 (페이지 LENS_BAR_MAX 와 같음, 24시간 초봉)
	private const int						m_nOriginLineMax	= 64 * 1024;		// 원문 한 줄 최대 읽기 (바이트)
	private const int						m_nLinePrefix		= 24;				// 원문 앞 읽기 (바이트, 레코드 "[줄 번호]:[" 부분)
	private const int						m_nOriginTailMax	= 16 * 1024;		// 원문 뒤 읽기 최대 (바이트, 레코드 "]:[Type]{추가 필드}" 부분)
	private const int						m_nFlushSec			= 1;				// 색인 버퍼 → 세그먼트 파일 간격 (초, 받은 줄이 쿼리에 보이기까지 최대 지연)
	private const int						m_nFlushDoc			= 100000;			// 색인 버퍼 줄 수 넘으면 다음 타이머에 바로 세그먼트 파일
	private const int						m_nMergeSeg			= 8;				// 시간 폴더 세그먼트 수 넘음: 합침 (쿼리 여는 파일 수 제한, 비슷한 크기끼리 → 매번 전체 재작성 방지)
	private const int						m_nMergeIdleMin		= 10;				// 마지막 세그먼트 쓰기 후 X분 지나면 1개로 합침 (지난 시간 마무리, 파일 크기 합 m_nMergeBytesMax 넘는 시간: 그대로)
	private const int						m_nQueueLineBytes	= 3072;				// 라이트 큐 줄 1개 메모리 추정 (바이트, 줄 문자열 UTF-16 + 줄 번호·추가 필드 값, 부하 테스트 실측 ~3 KB) → 큐 최대 줄 수
	private const int						m_nBufferDocBytes	= 512;				// 색인 버퍼 줄 1개 메모리 추정 (바이트, 줄 20 + 단어·필드 포스팅 아레나 + 사전 칸, 장문 포함 평균) → 버퍼 최대 줄 수
	private static readonly long			m_nMemoryLimit		= GetAvailableMemory() / 2;	// 적재 메모리 상한: 서버 시작 때 남은 물리 메모리의 절반 (라이트 큐 1/4·색인 버퍼 1/4·합침 1/2)
	private static readonly long			m_nMergeBytesMax	= Math.Max(m_nMemoryLimit / 2, 256L * 1048576);	// 합침 묶음 최대 바이트 (세그먼트 파일 크기 합 = 읽어 두는 메모리, 넘음: 세그먼트 여러 개 그대로)
	public static readonly long				m_nIndexBufferMax	= Math.Clamp(m_nMemoryLimit / 4 / m_nBufferDocBytes, m_nFlushDoc * 2L, 100000000L);	// 색인 버퍼 최대 줄 수 (모든 워커 합·세그먼트 쓰는 중 포함, 메모리 상한 1/4, 넘음: 클라 Pause·수신 스레드 대기 → TCP 역압, 절반 넘음: 나이 상관없이 Flush)
	private const int						m_nMergeTier		= 4;				// 합칠 세그먼트 묶음: 끝에서부터 줄 수가 가장 작은 것의 X배 이하인 것들 (비슷한 크기)
	public const int						m_nIngestQueueWarn	= 10000;			// 라이트 큐 밀림 (스왑 때 X줄 이상 → 부하 로그 + 모든 클라에 Pause, IngestWorker)
	public const int						m_nIngestQueueResume	= 1000;			// 밀림 풀림 (모든 워커 라이트 큐가 이 줄 수 미만 → 모든 클라에 Resume: 다시 읽기·전송)
	public const int						m_nIngestPausePercent	= 30;			// 상한 넘칠 때마다 멈출 클라 (전송 중 클라의 분당 Log 줄 많은 순 상위 X%, 올림 → 남은 1대도 멈춤)
	private const int						m_nIngestPauseStepMs	= 3000;			// 다음 X% 멈춤 최소 간격 (ms, 앞 멈춤 효과 볼 시간, 넘친 채 스왑마다 → 순식간에 모두 멈춤 방지)
	private const int						m_nIngestPauseMinMs	= 2000;				// Pause 뒤 Resume 까지 최소 (ms, Pause·Resume 깜빡임 방지)
	public static readonly int				m_nIngestQueueMax	= (int)Math.Clamp(m_nMemoryLimit / 4 / m_nQueueLineBytes, m_nIngestQueueWarn * 2, 100000000);	// 라이트 큐 최대 줄 수 (모든 워커 합, 메모리 상한 1/4, 넘음: 수신 스레드 대기 → TCP 역압)
	public const int						m_nIngestQueueWorkerMax	= m_nIngestQueueWarn * 2;	// 워커마다 라이트 큐 최대 줄 수 (넘음: 그 워커 담당 클라 수신 스레드 대기 → TCP 역압, 밀림이 한 워커에 쌓여 종료 처리·메모리 커지는 것 방지)
	public const int						m_nIngestStopMs		= 3000;				// 종료: 멈춤 요청 뒤 남은 라이트 큐 처리 상한 (ms, 넘음: 받은 진행 관리 폴더 Log 버림 → 클라가 서버 진행부터 다시 보냄, HTTP·관리 밖 폴더: 그대로 처리)
	private const int						m_nIngestWaitMs		= 20;				// 라이트 큐 가득: 수신 스레드 기다림 (ms)
	public const int						m_nIngestWarnSec	= 10;				// 부하 로그 간격 (초)
	public const int						m_nIngestIdleMs		= 100;				// 로직 스레드 신호 없을 때 깨어나 확인 (ms)
	public const int						m_nOriginIdleSec	= 60;				// 열어 둔 원문 파일 닫음 (마지막 쓰기 후 X초)
	public const int						m_nOriginOpenMax	= 256;				// 워커마다 열어 둘 원문 파일 최대 (넘으면 가장 오래 안 쓴 것부터 닫음)
	private const int						m_nFileIdKeepHour	= 24;				// 원문 파일 번호 최근 목록 세대 (시간, X~2X시간 안 쓴 파일: 메모리에서 뺌, 다시 받음: 새 번호 (정보 같음))
	private const int						m_nFileIdPreload	= 50000;			// 시작 때 최근 목록에 올릴 원문 파일 번호 (file.txt 끝에서부터)
	private const int						m_nFileInfoMax		= 20000;			// 원문 파일 번호 → 정보 최근 목록 최대 (넘음: 세대 바꿈 → 앞 세대 버림)
	private const int						m_nFileLineMax		= 16 * 1024;		// file.txt 한 줄 최대 읽기 (바이트)
	private const int						m_nSegBucket		= 256;				// 세그먼트 단어 사전 버킷 수 (해시 상위 바이트 → 단어 찾을 때 그 버킷 칸만 읽음)
	private const int						m_nSegEntry			= 20;				// 세그먼트 단어 사전 한 칸 바이트 (해시 8 + 포스팅 위치 4 + 건수 4 + 바이트 4)
	private const int						m_nSegField			= 14;				// 세그먼트 필드 사전 한 칸 머리 바이트 (포스팅 위치 4 + 건수 4 + 바이트 4 + 키 바이트 2, 뒤에 키 "필드\t값" UTF-8)
	private const int						m_nSegHeader		= 12 + (m_nSegBucket + 1) * 4;	// 세그먼트 머리 바이트 (버전 4 + 단어 수 4 + 필드 사전 바이트 4 + 버킷 시작 257 × 4)
	private const int						m_nSegVersion		= 2;				// 세그먼트 파일 버전 (다르면 쿼리 오류, 2: 필드 사전)
	private const int						m_nDocRecord		= 20;				// 줄 목록 (doc.bin) 한 줄 바이트 (분초 4 + 원문 파일 번호 4 + 원문 위치 8 + 원문 바이트 4)
	private const int						m_nHourMs			= 3600 * 1000;		// 1시간 (ms)
	private static readonly string[]		m_arrBaseField		= { "Machine", "Folder", "Type" };	// 기본 필드 (Machine·Folder: 원문 앞 받은 값, Type: 원문 뒤 원문 기준 파싱 = 첫 [] 안, 존 등: 설정 aField 추가 필드)
	private static readonly Regex			m_kFieldKey			= new Regex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);	// 추가 필드 키 형식 (영문 시작, 영문·숫자·_)
	private static readonly string[]		m_arrRangeFormat	= { "yyyyMMddHHmm", "yyyyMMddHHmmss" };	// 쿼리·통계 기간 형식 (분, 초별 막대 클릭: 초)
	private static readonly Regex			m_kExactTerm		= new Regex(@"^(\d{1,3}(\.\d{1,3}){3}(:\d{1,5})?|(\d{4}/)?\d{1,2}/\d{1,2}|\d{1,2}:\d{2}(:\d{2}(\.\d{1,7})?)?)$", RegexOptions.CultureInvariant);	// 원문 그대로 확인할 검색어: IP·IP:포트·날짜·시간 (페이지 WordAt 와 같은 형식)
	private static volatile string[]		m_arrFieldName		= Array.Empty<string>();	// 추가 필드 이름 (설정 aField 키, 설정 순서·처음 받은 표기, Data\Index\field.txt, 바꿀 때 배열 통째로)
	private static readonly ConcurrentDictionary<string, string>	m_hmapFieldName	= new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);	// 추가 필드 이름 (대소문자 무시) → 처음 표기 (읽기: 워커 잠금 없이, 쓰기: m_kIndexLock)
	private static volatile bool			m_bFieldLoaded;						// field.txt 읽음
	private const int						m_nFieldRegexMax	= 500;				// 추가 필드 정규식 최대 글자 (클라와 같음)
	private static volatile FieldRule[]		m_arrFieldRule		= LoadFieldRule();	// 줄 추가 필드 정의 (설정 aField, TCP: 클라 Count 답·얼라이브 답 (클라 1분마다, 바뀜: 바로)으로 내려줌, HTTP /api/external/log·TCP 받은 필드: 이 키만, 바꿀 때 배열 통째로)
	private static readonly SavedList		m_kSaved			= new SavedList("aQuery", "저장 쿼리", "쿼리", "arrQuery");	// 저장 쿼리 (설정 aQuery → Query.json)
	private static readonly object			m_kSavedLock		= new object();		// 알람·필드 정의 바꾸기 1건씩 (설정 파일 쓰기)
	private static volatile List<AlarmItem>	m_listAlarm			= LoadAlarm();		// 알람 (설정 aAlarm, 바꿀 때 목록 통째로)
	private const int						m_nAlarmMinuteMax	= 1440;				// 알람 간격 최대 (분, 하루: 구간은 KST 자정 안 넘김)
	private const int						m_nAlarmDelayMax	= 1440;				// 알람 억제 최대 (분)
	private static long						m_nAlarmTestTick	= -m_nAlarmTestMs;	// 마지막 테스트 발송 (TickCount64)
	private const int						m_nAlarmTestMs		= 5000;				// 테스트 발송 최소 간격 (ms)
	private const int						m_nAlarmListMax		= 20;				// 알람 메일·SMS 최대 개수
	private static readonly object			m_kIndexLock		= new object();		// 원문 파일 번호 목록 (file.txt)·추가 필드 이름 (field.txt) 잠금 (처음 읽기·새 파일·새 필드·색인 폴더 지움 때만, 있는 것 찾기: 잠금 없음)
	private static readonly object			m_kFlushLock		= new object();		// Flush 1번씩 (타이머 스레드·종료, 버퍼 떼기부터 세그먼트 다 쓸 때까지 → 종료 Flush: 타이머가 뗀 버퍼 끝까지 대기)
	private static readonly object			m_kMergeLock		= new object();		// 합칠 시간 목록 (m_hmapMerge) 잠금 (타이머 쓰기 ↔ 합침 스레드)
	private static readonly AutoResetEvent	m_kMergeSignal		= new AutoResetEvent(false);	// 합침 스레드 깨움 (세그먼트 쓴 뒤)
	private static Thread?					m_kMergeThread;						// 합침 스레드 (첫 Tick 때 시작, Close 때 끝)
	private static volatile bool			m_bMergeStop;						// 합침 스레드 멈춤 요청
	private static readonly ReaderWriterLockSlim	m_kSegmentLock	= new ReaderWriterLockSlim();	// 세그먼트 목록·열기 (읽기: 쿼리·통계) ↔ 옛 세그먼트 삭제 (쓰기: 합치기) → 열기 전 삭제 방지 (연 뒤: FileShare.Delete)
	private static readonly object			m_kCacheLock		= new object();		// 쿼리 결과 보관 잠금
	private static readonly IngestWorker[]	m_arrWorker			= Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount * 2 + Environment.ProcessorCount / 2)).Select(nIndex => new IngestWorker(nIndex)).ToArray();	// 적재 로직 스레드 (코어 수 × 2.5, 쓰기 대기 중 다른 워커 CPU 사용): 머신·대상 폴더 해시로 담당 고정 → 같은 폴더 순서대로 (받은 진행 포함), 다른 폴더 동시
	private static readonly int[]			m_arrBacklog		= new int[m_arrWorker.Length];	// 워커마다 마지막 스왑 때 밀린 줄 수 (ReportLoad)
	public static int						m_nIngestQueueLine;					// 모든 워커 라이트 큐 줄 수 합 (Interlocked, m_nIngestQueueMax 넘으면 TryAdd false)
	public static long						m_nIndexBufferLine;					// 모든 워커 색인 버퍼 줄 수 합 (Interlocked, 세그먼트 쓴 뒤 뺌, m_nIndexBufferMax 넘으면 TryAdd false·Pause)
	private static readonly object			m_kLoadLock			= new object();		// 부하 상태 바꾸기 1건씩
	private static volatile bool			m_bIngestPaused;					// 밀림으로 클라 전송 멈춤 중 (상한 넘칠 때마다 전송 중 클라 X% 더 Pause, 모든 워커 m_nIngestQueueResume 미만·색인 버퍼 절반 미만이면 모든 클라에 Resume)
	private static long						m_nIngestPauseTick;					// 마지막 Pause 보낸 시각 (TickCount64, m_nIngestPauseStepMs 지나야 다음 X%, m_nIngestPauseMinMs 지나야 Resume)
	private static readonly Dictionary<long, long>					m_hmapMerge		= new Dictionary<long, long>();									// 합칠 원문 시간 → 마지막 세그먼트 쓴 시각 (TickCount64, 0: 시작 때 발견, m_kMergeLock)
	private static readonly Dictionary<string, QueryResult>			m_hmapCache		= new Dictionary<string, QueryResult>(StringComparer.Ordinal);	// 쿼리 결과 보관 (기간·쿼리 → 결과, m_kCacheLock)
	private static readonly List<long>								m_listFileOffset	= new List<long>();	// 원문 파일 번호 → Data\Index\file.txt 줄 시작 바이트 (번호 = 위치 + 1, -1: 없음, 번호마다 8바이트만 → 정보: 최근 것만 메모리, m_kIndexLock)
	private static volatile ConcurrentDictionary<string, int>		m_hmapFileId		= new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);	// 최근 원문 파일 머신\t클라 경로\t존 → 번호 (이번 세대, 존 바뀜: 새 번호, 읽기: 워커 잠금 없이, 새 번호·세대 바꿈: m_kIndexLock)
	private static volatile ConcurrentDictionary<string, int>		m_hmapFileIdOld		= new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);	// 앞 세대 (찾음: 이번 세대로 옮김, m_nFileIdKeepHour 시간마다 세대 바꿈)
	private static long												m_nFileIdTick		= Environment.TickCount64;	// 원문 파일 번호 세대 바꾼 시각 (TickCount64, m_kIndexLock)
	private static volatile ConcurrentDictionary<int, (string sMachine, string sPath, string sFolder, string sZone)>	m_hmapFileInfo		= new ConcurrentDictionary<int, (string sMachine, string sPath, string sFolder, string sZone)>();	// 최근 원문 파일 번호 → 머신·클라 경로·Folder·Zone (이번 세대, 없음: file.txt 그 줄만 읽음, m_nFileInfoMax 넘음: 세대 바꿈)
	private static volatile ConcurrentDictionary<int, (string sMachine, string sPath, string sFolder, string sZone)>	m_hmapFileInfoOld	= new ConcurrentDictionary<int, (string sMachine, string sPath, string sFolder, string sZone)>();	// 앞 세대 (찾음: 이번 세대로 옮김)
	private static readonly ConcurrentDictionary<string, string>	m_hmapMachine		= new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);	// 색인된 머신 (대소문자 무시 → 처음 표기, 쿼리 M 목록, 쓰기: m_kIndexLock)
	private static volatile bool			m_bFileLoaded;						// file.txt 읽음 (다 읽은 뒤 참 → 잠금 없는 찾기는 그 뒤만)
	private static bool						m_bIndexScanned;					// 시작 때 색인 폴더 훑음 (.tmp 삭제, 세그먼트 2개 이상인 시간 → 합칠 시간)

	static TabQuery()	// 적재 메모리 상한 출력 (서버 시작 때 남은 메모리 기준)
	{
		Log.Write(INFO_, $"적재 메모리 상한: 남은 {GetAvailableMemory() / 1048576:N0} MB 의 절반 {m_nMemoryLimit / 1048576:N0} MB → 라이트 큐 {m_nIngestQueueMax:N0}줄 (워커마다 {m_nIngestQueueWorkerMax:N0}줄), 색인 버퍼 {m_nIndexBufferMax:N0}줄, 합침 묶음 {m_nMergeBytesMax / 1048576:N0} MB, 워커 {m_arrWorker.Length}개, 서버 GC {GCSettings.IsServerGC}");
	}

	public void MapRoute(WebApplication WebApp)	// 라우트 등록 (쿼리 탭)
	{
		WebApp.MapGet("/query/field", OnFieldPage);			// 필드 값 목록 새 창 (필드 모드)
		WebApp.MapGet("/api/query", OnQuery);				// 쿼리 (색인 단어·필드, 최신 순 페이지)
		WebApp.MapGet("/api/query/stat", OnStat);			// 필드별 통계 (쿼리·기간 맞는 줄의 필드 값마다 건수)
		WebApp.MapGet("/api/query/lens", OnLens);			// 렌즈 초·분봉 (쿼리·기간 맞는 줄의 초·분별 건수)
		WebApp.MapGet("/api/query/field", OnFieldList);		// 필드 목록 (기본 + 추가: 설정 aField 로 받은 이름)
		WebApp.MapGet("/api/query/machine", OnMachineList);	// 머신 목록 (색인된 머신, 쿼리 M)
		m_kSaved.MapRoute(WebApp, "/api/query/saved");		// 저장 쿼리 (목록·저장·삭제·순서, SavedList)
		WebApp.MapGet("/api/alarm", OnAlarmList);					// 알람 목록 (설정 aAlarm)
		WebApp.MapPost("/api/alarm", OnAlarmSave);					// 알람 추가·수정 (본문 JSON)
		WebApp.MapPost("/api/alarm/delete", OnAlarmDelete);			// 알람 삭제 (본문 JSON)
		WebApp.MapPost("/api/alarm/move", OnAlarmMove);				// 알람 순서 바꿈 (본문 JSON)
		WebApp.MapPost("/api/alarm/test", OnAlarmTest);				// 알람 테스트 발송 (본문 JSON, 저장 안 함)
		WebApp.MapGet("/api/alarm/log", AlarmLog.OnLog);			// 알람 로그 (기간·알람·결과, 최신 순)
		WebApp.MapGet("/api/alarm/history", AlarmLog.OnHistory);		// 알람 기록 (날짜·제목·내용·결과, 전체 최신 순)
		WebApp.MapGet("/api/alarm/account", () => Results.Json(new { arrAccount = Auth.GetAlarmAccounts() }));	// 알람 메일·SMS 받는 계정 고르기 목록 (계정 {sID, sMail})
		WebApp.MapGet("/api/field", OnFieldRuleList);				// 필드 정의 목록 (설정 aField)
		WebApp.MapPost("/api/field", OnFieldRuleSave);				// 필드 정의 추가·수정 (본문 JSON)
		WebApp.MapPost("/api/field/delete", OnFieldRuleDelete);		// 필드 정의 삭제 (본문 JSON)
		WebApp.MapPost("/api/field/move", OnFieldRuleMove);			// 필드 정의 순서 바꿈 (본문 JSON)
	}

	private static IResult OnFieldPage()	// 필드 값 목록 새 창 (필드 모드)
	{
		return Web.MakePage("field");
	}

	private static async Task<IResult> OnQuery(string? sKey, long? nStart, long? nEnd, int? nPage, int? nOffset, int? nCount, int? nLimit, HttpContext kContext)	// 쿼리 (응답: Server.cs 머리, 부하 제한: Gate·GetLimitSource·Yield, 요청 끊김: 응답 없음, 제한 시간 넘음: sError)
	{
		long			nBegin		= Stopwatch.GetTimestamp();
		string			sQuery		= (sKey ?? "").Trim();
		int				nLimitSec	= GetLimitSec(nLimit);
		int				nSkip		= Math.Clamp(nOffset ?? 0, 0, m_nQueryPage);					// 페이지 안 시작 (스크롤 이어 읽기)
		int				nTake		= Math.Clamp(nCount ?? m_nQueryPage, 0, m_nQueryPage - nSkip);	// 페이지 안 줄 수
		int				nCur		= 0;															// 지금 페이지 (범위 밖: 끝)
		QueryResult		kResult;
		List<object>	listLine	= new List<object>();											// 이 페이지 줄 (원문은 이것만 읽음)
		string			sError;

		try
		{
			using IDisposable				kGate	= await m_kGate.EnterAsync(kContext);
			using CancellationTokenSource	kLimit	= GetLimitSource(kContext, nLimitSec);

			kResult	= GetQueryResult(sQuery, nStart ?? 0, nEnd ?? 0, (nPage ?? 0) > 0 || nSkip > 0, kLimit.Token);	// 페이지 넘김·이어 읽기: 보관분
			nCur	= Math.Clamp(nPage ?? 0, 0, Math.Max(0, (kResult.m_nTotal + m_nQueryPage - 1) / m_nQueryPage - 1));
			sError	= kResult.m_sError;

			try
			{
				listLine = ReadPage(kResult, nCur * m_nQueryPage + nSkip, nTake, kLimit.Token);
			}
			catch (Exception kException) when (kException is not OperationCanceledException)	// 원문 사용 중·잘림: 건수·막대는 그대로
			{
				sError = GetErrorText(kException, "쿼리 원문 읽기");
			}
		}
		catch (OperationCanceledException)	// 요청 끊김: 응답 없음, 제한 시간: 오류
		{
			if (kContext.RequestAborted.IsCancellationRequested)
			{
				return Results.Empty;
			}

			kResult	= new QueryResult("");
			sError	= GetLimitError("쿼리", nLimitSec);
		}

		return Results.Json(new { sKey = sQuery, nStart = nStart ?? 0, nEnd = nEnd ?? 0, nTotal = kResult.m_nTotal, nPage = nCur, nPageSize = m_nQueryPage, nOffset = nSkip, nElapsedMs = (long)Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds, sError, arrHour = kResult.m_arrHour.Select((nHour, nIndex) => new { nHour, nCount = kResult.m_arrCount[nIndex] }), arrMinute = kResult.m_listMinute, arrSecond = kResult.m_listSecond, arrLine = listLine });
	}

	private static int GetLimitSec(int? nLimit)	// 제한 시간 (초, 없음: 기본)
	{
		return Math.Clamp(nLimit ?? m_nQueryLimitSec, 1, m_nQueryLimitMax);
	}

	private static CancellationTokenSource GetLimitSource(HttpContext kContext, int nLimitSec)	// 제한 시간 + 요청 끊김 토큰
	{
		CancellationTokenSource kLimit = CancellationTokenSource.CreateLinkedTokenSource(kContext.RequestAborted);

		kLimit.CancelAfter(nLimitSec * 1000);

		return kLimit;
	}

	private static string GetLimitError(string sWhat, int nLimitSec)	// 제한 시간 오류 글
	{
		return $"{sWhat} 제한 시간 초과 ({nLimitSec}초)";
	}

	public static async Task<IResult> OnExternalQuery(HttpRequest kRequest)	// 외부 API 쿼리 (/api/external/query/키/토큰/, 쿼리 권한 계정, 브라우저 GET: 키 계정 로그인 → 쿼리 탭 /?… 이동 (그 쿼리·기간 바로 실행), 그 외: JSON): 쿼리 문자열 (있음) 또는 POST 본문 JSON {sKey (없음: *), nStart·nEnd (yyyyMMddHHmm·yyyyMMddHHmmss, 시작 포함·끝 미포함, 끝 없음: 지금), nMinute (시작 없음: 끝 X분 전, 없음: X분), nPage, nOffset, nCount} → /api/query 와 같은 응답
	{
		CancellationToken			kStop		= kRequest.HttpContext.RequestAborted;									// 요청 끊김
		Dictionary<string, string>	hmapParam	= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);	// 입력 (키 대소문자 무시)

		try
		{
			if (kRequest.Query.Count > 0)	// 쿼리 문자열 (GET 링크)
			{
				foreach (string sName in kRequest.Query.Keys)
				{
					hmapParam[sName] = kRequest.Query[sName].LastOrDefault() ?? "";
				}
			}
			else if (HttpMethods.IsPost(kRequest.Method) && kRequest.ContentLength != 0)	// 본문 JSON (없음: 기본값)
			{
				using JsonDocument kDoc = await JsonDocument.ParseAsync(kRequest.Body, cancellationToken: kStop);

				if (kDoc.RootElement.ValueKind != JsonValueKind.Object)
				{
					return Results.Json(new { sError = "형식 오류" }, statusCode: StatusCodes.Status400BadRequest);
				}

				foreach (JsonProperty kProp in kDoc.RootElement.EnumerateObject())
				{
					hmapParam[kProp.Name] = kProp.Value.ValueKind == JsonValueKind.String ? kProp.Value.GetString() ?? "" : kProp.Value.GetRawText();	// 문자열·숫자 모두 글자로
				}
			}
		}
		catch (JsonException)	// 형식 오류
		{
			return Results.Json(new { sError = "형식 오류" }, statusCode: StatusCodes.Status400BadRequest);
		}
		catch (OperationCanceledException)	// 보낸 쪽 끊음
		{
			return Results.Empty;
		}

		long?	funcLong(string sName)	=> long.TryParse(hmapParam.GetValueOrDefault(sName), NumberStyles.None, CultureInfo.InvariantCulture, out long nValue) ? nValue : null;	// 정수 입력 (없음·형식 오류: null)
		long	funcStamp(DateTime k)	=> long.Parse(k.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);										// 시각 → yyyyMMddHHmmss

		string	sKey	= hmapParam.GetValueOrDefault("sKey", "").Trim();										// 쿼리 (없음: * 전체)
		int		nMinute	= (int)Math.Clamp(funcLong("nMinute") ?? m_nExternalMinute, 1, m_nQueryDayMax * 1440L);	// 시작 없음: 끝 X분 전
		long	nEnd	= funcLong("nEnd") ?? funcStamp(GetNow().DateTime);										// 끝 (미포함, 없음: 지금)
		long	nStart	= funcLong("nStart") ?? (DateTime.TryParseExact(nEnd.ToString(CultureInfo.InvariantCulture), m_arrRangeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kEnd) ? funcStamp(kEnd.AddMinutes(-nMinute)) : 0);	// 시작 (포함, 없음: 끝 X분 전, 끝 형식 오류: 0 → 기간 오류)

		if (HttpMethods.IsGet(kRequest.Method) && kRequest.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase)	// 브라우저 (메일·훅 링크 클릭): 키 계정 로그인 → 쿼리 탭에서 그 쿼리·기간 바로 실행
			&& DateTime.TryParseExact(nStart.ToString(CultureInfo.InvariantCulture), m_arrRangeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kFrom)
			&& DateTime.TryParseExact(nEnd.ToString(CultureInfo.InvariantCulture), m_arrRangeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kTo))
		{
			if (!await Auth.SignInByKey(kRequest.HttpContext))	// 접근 탭 없는 계정 (로그 수신만 등): 화면 로그인 못 함
			{
				return Results.Json(new { sError = "로그인할 수 없는 계정 (접근 탭 없음)" }, statusCode: StatusCodes.Status403Forbidden);
			}

			return Results.Redirect(Web.GetQueryPath(sKey, kFrom, kTo));
		}

		return await OnQuery(sKey == "" ? "*" : sKey, nStart, nEnd, (int?)funcLong("nPage"), (int?)funcLong("nOffset"), (int?)funcLong("nCount"), (int?)funcLong("nLimit"), kRequest.HttpContext);
	}

	private static async Task<IResult> OnLens(string? sKey, long? nStart, long? nEnd, int? nSec, int? nLimit, HttpContext kContext)	// 렌즈 초·분봉: 쿼리·기간 맞는 줄 → 칸 (nSec 1: 초, 그 외: 분) 마다 건수 (기간 첫 칸 정각부터 모든 칸 0 포함, m_nLensBarMax 칸 넘음: 오류, 원문 안 읽음, 보관 안 함, 부하 제한: OnQuery 와 같음)
	{
		long		nBegin		= Stopwatch.GetTimestamp();
		int			nCell		= nSec == 1 ? 1 : 60;												// 칸 (초)
		int			nLimitSec	= GetLimitSec(nLimit);
		QueryResult	kPlan		= new QueryResult("");												// 쿼리 조건 (보관 안 함)
		string		sError		= PrepareQuery(kPlan, (sKey ?? "").Trim(), nStart ?? 0, nEnd ?? 0);	// 조건 오류
		DateTime	kFirst		= kPlan.m_kFrom.Date.AddHours(kPlan.m_kFrom.Hour).AddMinutes(kPlan.m_kFrom.Minute).AddSeconds(nCell == 1 ? kPlan.m_kFrom.Second : 0);	// 기간 첫 칸 (정각 분·초)
		int[]		arrCount	= Array.Empty<int>();												// 칸마다 건수 (kFirst 부터)

		if (sError == "" && (kPlan.m_kTo - kFirst).TotalSeconds / nCell > m_nLensBarMax)
		{
			sError = $"기간 초과 ({(nCell == 1 ? "초봉" : "분봉")} 최대 {m_nLensBarMax:N0}칸)";
		}

		if (sError == "")
		{
			try
			{
				using IDisposable				kGate	= await m_kGate.EnterAsync(kContext);
				using CancellationTokenSource	kLimit	= GetLimitSource(kContext, nLimitSec);

				(sError, arrCount) = RunLens(kPlan, kFirst, nCell, kLimit.Token);
			}
			catch (OperationCanceledException)	// 요청 끊김: 응답 없음, 제한 시간: 오류
			{
				if (kContext.RequestAborted.IsCancellationRequested)
				{
					return Results.Empty;
				}

				sError		= GetLimitError("렌즈", nLimitSec);
				arrCount	= Array.Empty<int>();
			}
		}

		return Results.Json(new { sError, nElapsedMs = (long)Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds, arrTime = arrCount.Select((nCount, nIndex) => { DateTime kCell = kFirst.AddSeconds((long)nIndex * nCell); long nMinute = ToHour(kCell) * 100 + kCell.Minute; return new { nTime = nCell == 1 ? nMinute * 100 + kCell.Second : nMinute, nCount }; }) });	// nTime: 초봉 yyyyMMddHHmmss, 분봉 yyyyMMddHHmm
	}

	private static async Task<IResult> OnStat(string? sKey, long? nStart, long? nEnd, string? sField, int? nLimit, HttpContext kContext)	// 필드별 통계: 쿼리·기간 맞는 줄 ∩ 필드 값 포스팅 → 값마다 건수 (원문 안 읽음, sField: 그 필드 값 전체, 응답: Server.cs 머리, 부하 제한: OnQuery 와 같음)
	{
		long										nBegin		= Stopwatch.GetTimestamp();
		string										sQuery		= (sKey ?? "").Trim();
		int											nLimitSec	= GetLimitSec(nLimit);
		QueryResult									kPlan		= new QueryResult("");																// 쿼리 조건 (보관 안 함)
		Dictionary<string, (string sKey, int nCount)>	hmapCount	= new Dictionary<string, (string sKey, int nCount)>(StringComparer.OrdinalIgnoreCase);	// 필드 키 (필드\t값) → 첫 표기·건수
		string										sError		= PrepareQuery(kPlan, sQuery, nStart ?? 0, nEnd ?? 0);								// 조건 오류
		int											nTotal		= 0;																				// 맞는 줄 수

		if (sError == "")
		{
			try
			{
				using IDisposable				kGate	= await m_kGate.EnterAsync(kContext);
				using CancellationTokenSource	kLimit	= GetLimitSource(kContext, nLimitSec);

				(sError, nTotal) = RunStat(kPlan, hmapCount, kLimit.Token);
			}
			catch (OperationCanceledException)	// 요청 끊김: 응답 없음, 제한 시간: 오류
			{
				if (kContext.RequestAborted.IsCancellationRequested)
				{
					return Results.Empty;
				}

				sError = GetLimitError("통계", nLimitSec);

				hmapCount.Clear();
			}
		}

		Dictionary<string, List<(string sValue, int nCount)>> hmapValue = new Dictionary<string, List<(string sValue, int nCount)>>(StringComparer.OrdinalIgnoreCase);	// 필드 → 값·건수

		foreach ((string sFieldKey, int nCount) in hmapCount.Values)
		{
			int nTab = sFieldKey.IndexOf('\t');	// 필드·값 사이

			if (!hmapValue.TryGetValue(sFieldKey[..nTab], out List<(string sValue, int nCount)>? listValue))
			{
				hmapValue[sFieldKey[..nTab]] = listValue = new List<(string sValue, int nCount)>();
			}

			listValue.Add((sFieldKey[(nTab + 1)..], nCount));
		}

		List<object>	listField	= new List<object>();								// 필드마다 (기본 → 추가 순)
		bool			bOne		= !string.IsNullOrWhiteSpace(sField);				// 그 필드만 값 전체 (필드 새 창)

		foreach ((string sName, bool bBase) in GetFieldList())
		{
			if (bOne && !string.Equals(sName, sField!.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			List<(string sValue, int nCount)> listValue = hmapValue.GetValueOrDefault(sName) ?? new List<(string sValue, int nCount)>();

			listField.Add(new { sName, bBase, nValue = listValue.Count, arrValue = listValue.OrderByDescending(kValue => kValue.nCount).ThenBy(kValue => kValue.sValue, StringComparer.OrdinalIgnoreCase).Take(bOne ? int.MaxValue : m_nStatTop).Select(kValue => new { kValue.sValue, kValue.nCount }) });
		}

		return Results.Json(new { sKey = sQuery, nTotal, nElapsedMs = (long)Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds, sError, arrField = listField });
	}

	private static IResult OnFieldList()	// 필드 목록 → {arrField: [{sName, bBase (기본 필드)}]}
	{
		return Results.Json(new { arrField = GetFieldList().Select(kField => new { kField.sName, kField.bBase }) });
	}

	private static IResult OnMachineList()	// 쿼리 머신 목록 → {arrMachine: [""]} (원문 파일 번호 목록 file.txt 의 머신, 접속 안 된 머신 포함, 이름순, 번호 목록 안 훑음 → 잠금 짧게)
	{
		lock (m_kIndexLock)
		{
			LoadFileList();
		}

		string[] arrMachine = m_hmapMachine.Values.Order(StringComparer.OrdinalIgnoreCase).ToArray();	// 색인된 머신 (대소문자 무시 중복 뺌)

		return Results.Json(new { arrMachine });
	}

	private static FieldRule[] LoadFieldRule(IEnumerable<IConfigurationSection>? listSection = null, List<string>? listWarn = null)	// 설정 aField → 줄 추가 필드 정의 (키: 형식·m_nFieldKeyMax 자 이하·기본 필드·중복 (대소문자 무시) 아님·최대 m_nLineFieldMax 개, sValue 있음: m_nFieldValueMax 자 이하 (정규식 안 씀), 없음: 정규식 m_nFieldRegexMax 자 이하·형식 맞음, 오류: 오류 글 후 뺌, listSection 없음: 메모리 설정 (시작: 정의 출력), listWarn: 오류 글 모음 (없음: 바로 출력))
	{
		List<FieldRule> listRule = new List<FieldRule>();

		foreach (IConfigurationSection kSection in listSection ?? Config.GetChildren("aField"))
		{
			string sKey		= (kSection["sKey"] ?? "").Trim();
			string sRegex	= kSection["sRegex"] ?? "";
			string sValue	= (kSection["sValue"] ?? "").Trim();	// 고정값 (있음: 정규식 안 씀, 모든 줄 이 값)
			string sError	= CheckFieldRule(sKey, sRegex, sValue, listRule);

			if (sError != "")
			{
				AddWarn(listWarn, $"설정 오류: aField {sKey} ({sError})");

				continue;
			}

			listRule.Add(new FieldRule(sKey, sValue != "" ? "" : sRegex, sValue));
		}

		if (listSection == null)	// 시작
		{
			Log.Write(INFO_, $"쿼리 필드 정의: {(listRule.Count == 0 ? "없음" : string.Join(", ", listRule.Select(kRule => kRule.sKey)))} (설정 aField → 클라 Count 답, 받는 필드: 이 키만)");
		}

		return listRule.ToArray();
	}

	private static string CheckFieldRule(string sKey, string sRegex, string sValue, IEnumerable<FieldRule> listOther)	// 필드 정의 1개 검사 (설정 읽기·필드 버튼 저장 공통, listOther: 같이 둘 다른 정의, 반환: 오류, 없음: "")
	{
		if (sKey == "" || sKey.Length > m_nFieldKeyMax || !m_kFieldKey.IsMatch(sKey))
		{
			return $"이름 형식 오류 (영문 시작·영문·숫자·_ {m_nFieldKeyMax}자 이하)";
		}

		if (m_arrBaseField.Contains(sKey, StringComparer.OrdinalIgnoreCase))
		{
			return $"기본 필드 이름 ({string.Join("·", m_arrBaseField)}) 불가";
		}

		if (listOther.Any(kRule => string.Equals(kRule.sKey, sKey, StringComparison.OrdinalIgnoreCase)))
		{
			return "이미 있는 이름";
		}

		if (listOther.Count() >= m_nLineFieldMax)
		{
			return $"최대 {m_nLineFieldMax}개";
		}

		if (sValue != "")	// 고정값: 정규식 안 봄
		{
			return sValue.Length > m_nFieldValueMax ? $"고정값 {m_nFieldValueMax}자 넘음" : "";
		}

		return IsFieldRegex(sRegex) ? "" : $"정규식 없음·{m_nFieldRegexMax}자 넘음·형식 오류";
	}

	private static bool IsFieldRegex(string sRegex)	// 추가 필드 정규식 (있음·m_nFieldRegexMax 자 이하·형식 맞음)
	{
		if (sRegex == "" || sRegex.Length > m_nFieldRegexMax)
		{
			return false;
		}

		try
		{
			_ = new Regex(sRegex, RegexOptions.CultureInvariant);

			return true;
		}
		catch (ArgumentException)	// 정규식 형식 오류
		{
			return false;
		}
	}

	public static object[] GetFieldRule()	// 필드 정의 응답·클라 Count 답 aField [{sKey, sRegex, sValue}] (설정 순서)
	{
		return m_arrFieldRule.Select(kRule => (object)new { kRule.sKey, kRule.sRegex, kRule.sValue }).ToArray();
	}

	private static IResult OnFieldRuleList()	// 필드 정의 목록 → {arrField: [{sKey, sRegex, sValue}]} (설정 aField, 팝업 열 때마다: 설정 파일 다시 읽음)
	{
		ReloadFieldRule();

		return Results.Json(new { arrField = GetFieldRule() });
	}

	private static IResult OnFieldRuleSave(FieldParam kParam, HttpRequest kRequest)	// 필드 정의 추가·수정: 검사 → sOldKey: 그 자리 수정, 없음: 끝에 추가 (이름 겹침: 오류) → 설정 aField·바로 적용·접속 클라 알림 (요청자 IP 로그)
	{
		string sKey		= (kParam.sKey ?? "").Trim();		// 이름
		string sRegex	= kParam.sRegex ?? "";				// 정규식 (첫 () 값)
		string sValue	= (kParam.sValue ?? "").Trim();		// 고정값 (있음: 정규식 안 씀)
		string sOldKey	= (kParam.sOldKey ?? "").Trim();	// 수정할 필드 이름 (비움: 추가)
		string sError	= "";

		lock (m_kSavedLock)
		{
			List<FieldRule>	listRule	= new List<FieldRule>(m_arrFieldRule);																					// 바꿀 목록
			int				nOld		= sOldKey == "" ? -1 : listRule.FindIndex(kRule => string.Equals(kRule.sKey, sOldKey, StringComparison.OrdinalIgnoreCase));	// 수정할 자리

			sError = sOldKey != "" && nOld < 0 ? "없는 필드" : CheckFieldRule(sKey, sRegex, sValue, listRule.Where((_, nIndex) => nIndex != nOld).ToList());

			if (sError == "")
			{
				FieldRule kRule = new FieldRule(sKey, sValue != "" ? "" : sRegex, sValue);	// 고정값: 정규식 안 둠

				if (nOld >= 0)
				{
					listRule[nOld] = kRule;
				}
				else
				{
					listRule.Add(kRule);
				}

				sError = SaveFieldRule(listRule);
			}

			if (sError == "")
			{
				Log.Write(INFO_, $"필드 정의 {(nOld >= 0 ? "수정" : "추가")}: {(nOld >= 0 && sOldKey != sKey ? sOldKey + " → " : "")}{sKey} ({(sValue != "" ? $"고정값 {sValue}" : $"정규식 {sRegex}")}) 요청자 {Web.GetAddr(kRequest.HttpContext)}");
			}
		}

		return Results.Json(new { sError, arrField = GetFieldRule() });
	}

	private static IResult OnFieldRuleDelete(FieldParam kParam, HttpRequest kRequest)	// 필드 정의 삭제 → 설정 aField·바로 적용·접속 클라 알림 (없는 이름: 오류, 받아 둔 값·필드 목록 이름: 그대로, 요청자 IP 로그)
	{
		string sKey		= (kParam.sKey ?? "").Trim();	// 이름
		string sError	= "";

		lock (m_kSavedLock)
		{
			List<FieldRule> listRule = m_arrFieldRule.Where(kRule => !string.Equals(kRule.sKey, sKey, StringComparison.OrdinalIgnoreCase)).ToList();	// 지운 목록

			sError = listRule.Count == m_arrFieldRule.Length ? "없는 필드" : SaveFieldRule(listRule);

			if (sError == "")
			{
				Log.Write(INFO_, $"필드 정의 삭제: {sKey} 요청자 {Web.GetAddr(kRequest.HttpContext)}");
			}
		}

		return Results.Json(new { sError, arrField = GetFieldRule() });
	}

	private static IResult OnFieldRuleMove(FieldParam kParam, HttpRequest kRequest)	// 필드 정의 순서 바꿈: 그 이름 → nTo 자리 (필드 목록 순서도, 요청자 IP 로그)
	{
		string sKey		= (kParam.sKey ?? "").Trim();	// 이름
		string sError	= "";

		lock (m_kSavedLock)
		{
			List<FieldRule>	listRule	= new List<FieldRule>(m_arrFieldRule);
			int				nFrom		= listRule.FindIndex(kRule => string.Equals(kRule.sKey, sKey, StringComparison.OrdinalIgnoreCase));	// 옮길 자리

			sError = nFrom < 0 ? "없는 필드" : SaveFieldRule(SavedList.MoveItem(listRule, nFrom, kParam.nTo ?? nFrom));

			if (sError == "")
			{
				Log.Write(INFO_, $"필드 정의 순서: {sKey} → {(kParam.nTo ?? nFrom) + 1}번 요청자 {Web.GetAddr(kRequest.HttpContext)}");
			}
		}

		return Results.Json(new { sError, arrField = GetFieldRule() });
	}

	private static string SaveFieldRule(List<FieldRule> listRule)	// 필드 정의 잠금 안에서: 설정 aField 에 [{sKey, sRegex 또는 sValue}] 저장 → 성공: 메모리 정의 바꿈·필드 목록 순서·접속 클라에 새 정의 (반환: 오류, 없음: "")
	{
		JsonArray	kArray	= new JsonArray(listRule.Select(kRule => (JsonNode?)(kRule.sValue != "" ? new JsonObject { ["sKey"] = kRule.sKey, ["sValue"] = kRule.sValue } : new JsonObject { ["sKey"] = kRule.sKey, ["sRegex"] = kRule.sRegex })).ToArray());	// 설정 aField
		string		sError	= Config.SetArray("aField", kArray);

		if (sError == "")
		{
			m_arrFieldRule = listRule.ToArray();

			InitField();

			Log.Write(INFO_, $"필드 정의 알림: 접속 클라 {TCPServer.SendAliveAll()}대 (얼라이브 답 바로, 그 뒤: 클라 1분 얼라이브 답)");
		}

		return sError;
	}

	private static string CheckFieldValue(string sKey, string sValue)	// 받은 필드 1개 정의 검사 (키: 설정 aField 에 있음 (대소문자 무시), 고정값 정의: 값 같음 (대소문자 무시, 색인과 같음), 반환: 오류, 맞음: "")
	{
		FieldRule? kRule = m_arrFieldRule.FirstOrDefault(kItem => string.Equals(kItem.sKey, sKey, StringComparison.OrdinalIgnoreCase));	// 그 키 정의

		if (kRule == null)
		{
			return "설정 aField 에 없음";
		}

		return kRule.sValue == "" || string.Equals(kRule.sValue, sValue, StringComparison.OrdinalIgnoreCase) ? "" : $"고정값 {kRule.sValue} 아님";
	}

	public static string FindFieldError(JsonElement kRoot, string sFieldKey)	// Log 메시지 줄 추가 필드 중 정의에 안 맞는 첫 필드 (설정 aField 에 없는 키·고정값 다름, sFieldKey: 줄 필드 배열 키, 반환: "키: 오류", 없음: "", 긴 키: X자까지, 빈 키·빈 값: 검사 안 함 → ReadLineField 가 안 씀)
	{
		if (!kRoot.TryGetProperty(sFieldKey, out JsonElement kFields) || kFields.ValueKind != JsonValueKind.Array)
		{
			return "";
		}

		foreach (JsonElement kField in kFields.EnumerateArray().Where(kItem => kItem.ValueKind == JsonValueKind.Object))
		{
			foreach (JsonProperty kProperty in kField.EnumerateObject())
			{
				string sKey		= kProperty.Name.Trim();
				string sValue	= kProperty.Value.ValueKind == JsonValueKind.String ? (kProperty.Value.GetString() ?? "").Trim() : "";	// 문자열 아님: 빈 값

				if (sKey == "" || sValue == "")	// 빈 키·빈 값·문자열 아님: 안 씀 (오류 아님)
				{
					continue;
				}

				string sError = CheckFieldValue(sKey, sValue);

				if (sError != "")
				{
					return $"{(sKey.Length > m_nFieldKeyMax ? sKey[..m_nFieldKeyMax] + "…" : sKey)}: {sError}";
				}
			}
		}

		return "";
	}

	public static void InitField()	// 시작 (설정 읽은 뒤): 설정 aField 순서 → 추가 필드 목록 순서 (새 키: 등록, field.txt 실패: 출력 후 계속)
	{
		try
		{
			SetFieldOrder(m_arrFieldRule.Select(kRule => kRule.sKey).ToList());
		}
		catch (Exception kException) when (kException is IOException or UnauthorizedAccessException)
		{
			Log.Write(WARN_, $"쿼리 필드 순서 실패: {kException.Message}");
		}
	}

	public static List<AlarmItem> GetAlarmList()	// 알람 목록 (시작 때 Alarm)
	{
		return m_listAlarm;
	}

	public static List<(AlarmMatcher kMatcher, AlarmCount kCount)> TakeAlarmCount()	// 통계 스레드 (매분): 워커마다 버퍼 잠금 안에서 알람 카운터·마지막 줄 떼어 감 (워커: 새 카운터)
	{
		List<(AlarmMatcher kMatcher, AlarmCount kCount)> listCount = new List<(AlarmMatcher kMatcher, AlarmCount kCount)>();	// 스냅샷별 카운터

		foreach (IngestWorker kWorker in m_arrWorker)
		{
			lock (kWorker.m_kBufferLock)
			{
				listCount.AddRange(kWorker.m_listAlarmOld);
				kWorker.m_listAlarmOld.Clear();

				if (kWorker.m_kAlarmMatcher != null)
				{
					listCount.Add((kWorker.m_kAlarmMatcher, kWorker.m_kAlarmCount));

					kWorker.m_kAlarmCount = new AlarmCount(kWorker.m_kAlarmCount.m_arrCount.Length);
				}
			}
		}

		return listCount;
	}

	private static IResult OnAlarmList()	// 알람 목록 → {arrAlarm: [{nID, sKey, sQuery, nMinute, nCount, nDelay, aMail, sHook, aSMS, bEnable}]} (팝업 열 때마다: 설정 파일 다시 읽음)
	{
		ReloadAlarm();

		return Results.Json(new { arrAlarm = m_listAlarm });
	}

	private static IResult OnAlarmSave(AlarmParam kParam, HttpRequest kRequest)	// 알람 추가·수정: 검사 → nID 0: 끝에 추가 (ID: 최대 + 1), 그 외: 그 자리 → 설정 aAlarm (제목 겹침: 오류, 요청자 IP 로그)
	{
		int		nID		= kParam.nID ?? 0;					// 수정할 알람 (0: 추가)
		string	sError	= CheckAlarm(ToAlarm(kParam, nID));

		if (sError == "")
		{
			lock (m_kSavedLock)
			{
				List<AlarmItem>	listAlarm	= new List<AlarmItem>(m_listAlarm);	// 바꿀 목록
				int				nOld		= nID == 0 ? -1 : listAlarm.FindIndex(kAlarm => kAlarm.nID == nID);	// 수정할 자리
				int				nSame		= listAlarm.FindIndex(kAlarm => string.Equals(kAlarm.sKey, (kParam.sKey ?? "").Trim(), StringComparison.OrdinalIgnoreCase));	// 같은 제목

				if (nID != 0 && nOld < 0)
				{
					sError = "없는 알람";
				}
				else if (nSame >= 0 && nSame != nOld)
				{
					sError = "이미 있는 제목";
				}
				else
				{
					if (nOld < 0)	// 추가: 새 ID
					{
						nID = listAlarm.Count == 0 ? 1 : listAlarm.Max(kAlarm => kAlarm.nID) + 1;

						listAlarm.Add(ToAlarm(kParam, nID));
					}
					else
					{
						listAlarm[nOld] = ToAlarm(kParam, nID);
					}

					sError = SaveAlarm(listAlarm);
				}

				if (sError == "")
				{
					Log.Write(INFO_, $"알람 {(nOld < 0 ? "추가" : "수정")}: #{nID} {listAlarm.First(kAlarm => kAlarm.nID == nID).sKey} 요청자 {Web.GetAddr(kRequest.HttpContext)}");
				}
			}
		}

		return Results.Json(new { sError, nID, arrAlarm = m_listAlarm });
	}

	private static IResult OnAlarmDelete(AlarmParam kParam, HttpRequest kRequest)	// 알람 삭제 → 설정 aAlarm (없는 ID: 오류, 요청자 IP 로그)
	{
		int		nID		= kParam.nID ?? 0;	// 지울 알람
		string	sError	= "";

		lock (m_kSavedLock)
		{
			List<AlarmItem> listAlarm = m_listAlarm.Where(kAlarm => kAlarm.nID != nID).ToList();	// 지운 목록

			sError = listAlarm.Count == m_listAlarm.Count ? "없는 알람" : SaveAlarm(listAlarm);

			if (sError == "")
			{
				Log.Write(INFO_, $"알람 삭제: #{nID} 요청자 {Web.GetAddr(kRequest.HttpContext)}");
			}
		}

		return Results.Json(new { sError, arrAlarm = m_listAlarm });
	}

	private static IResult OnAlarmTest(AlarmParam kParam, HttpRequest kRequest)	// 알람 테스트 발송: 창 값 검사 → 메일·훅 바로 보냄 (요청 스레드, 저장·알람 로그·한도 없음) → {sError, arrResult: [{sChannel, sError}]} (요청자 IP 로그)
	{
		AlarmItem		kAlarm		= ToAlarm(kParam, kParam.nID ?? 0);							// 창 값
		string			sError		= CheckAlarm(kAlarm);										// 오류
		List<object>	listResult	= new List<object>();									// 채널별 결과

		if (sError == "" && kAlarm.aMail.Length == 0 && kAlarm.sHook == "")
		{
			sError = "메일·훅 없음";
		}

		long nTick = Environment.TickCount64;	// 지금 (반복 발송 방지)

		if (sError == "" && nTick - Interlocked.Read(ref m_nAlarmTestTick) < m_nAlarmTestMs)
		{
			sError = "잠시 후 다시";
		}

		if (sError == "")
		{
			Interlocked.Exchange(ref m_nAlarmTestTick, nTick);
		}

		if (sError == "")
		{
			foreach (AlarmJob kJob in Stat.MakeTestJob(kAlarm))
			{
				listResult.Add(new { kJob.sChannel, sError = Send.SendNow(kJob) });
			}

			Log.Write(NOTI_, $"알람 테스트 발송: #{kAlarm.nID} {kAlarm.sKey} ({string.Join(", ", listResult)}) 요청자 {Web.GetAddr(kRequest.HttpContext)}");
		}

		return Results.Json(new { sError, arrResult = listResult });
	}

	private static IResult OnAlarmMove(AlarmParam kParam, HttpRequest kRequest)	// 알람 순서 바꿈: 그 ID → nTo 자리 (요청자 IP 로그)
	{
		int		nID		= kParam.nID ?? 0;	// 옮길 알람
		string	sError	= "";

		lock (m_kSavedLock)
		{
			int nFrom = m_listAlarm.FindIndex(kAlarm => kAlarm.nID == nID);	// 옮길 자리

			sError = nFrom < 0 ? "없는 알람" : SaveAlarm(SavedList.MoveItem(m_listAlarm, nFrom, kParam.nTo ?? nFrom));

			if (sError == "")
			{
				Log.Write(INFO_, $"알람 순서: #{nID} → {(kParam.nTo ?? nFrom) + 1}번 요청자 {Web.GetAddr(kRequest.HttpContext)}");
			}
		}

		return Results.Json(new { sError, arrAlarm = m_listAlarm });
	}

	private static AlarmItem ToAlarm(AlarmParam kParam, int nID)	// 본문 → 알람 (앞뒤 빈칸·빈 항목 뺌, 메일·SMS 받는 계정: 오름차순)
	{
		return new AlarmItem
		{
			nID		= nID,
			sKey	= (kParam.sKey ?? "").Trim(),
			sQuery	= (kParam.sQuery ?? "").Trim(),
			nMinute	= kParam.nMinute ?? 0,
			nCount	= kParam.nCount ?? 0,
			nDelay	= kParam.nDelay ?? 0,
			aMail	= (kParam.aMail ?? Array.Empty<string>()).Select(sMail => (sMail ?? "").Trim()).Where(sMail => sMail != "").Order(Main.m_kNaturalOrder).ToArray(),
			sHook	= (kParam.sHook ?? "").Trim(),
			aSMS	= (kParam.aSMS ?? Array.Empty<string>()).Select(sSMS => (sSMS ?? "").Trim()).Where(sSMS => sSMS != "").Order(Main.m_kNaturalOrder).ToArray(),
			bEnable	= kParam.bEnable ?? false,
		};
	}

	private static string CheckAlarm(AlarmItem kAlarm)	// 알람 값 검사 (반환: 오류, 없음: "")
	{
		string sError = "";

		if (kAlarm.sKey == "" || kAlarm.sKey.Length > SavedList.m_nKeyMax)
		{
			sError = $"제목 없음·{SavedList.m_nKeyMax}자 넘음";
		}
		else if (kAlarm.sQuery == "" || kAlarm.sQuery.Length > SavedList.m_nQueryMax)
		{
			sError = $"쿼리 없음·{SavedList.m_nQueryMax}자 넘음";
		}
		else if (!SearchNode.IsValid(kAlarm.sQuery))
		{
			sError = "쿼리 형식 오류 (예: A AND (B NOT C) OR D)";
		}
		else if (kAlarm.nMinute < 1 || kAlarm.nMinute > m_nAlarmMinuteMax)
		{
			sError = $"간격 1 ~ {m_nAlarmMinuteMax}분";
		}
		else if (kAlarm.nCount < 1)
		{
			sError = "건수 1 이상";
		}
		else if (kAlarm.nDelay < 0 || kAlarm.nDelay > m_nAlarmDelayMax)
		{
			sError = $"억제 0 ~ {m_nAlarmDelayMax}분";
		}
		else if (kAlarm.aMail.Length > m_nAlarmListMax || kAlarm.aMail.Any(sMail => sMail.Contains('@') ? !Regex.IsMatch(sMail, @"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$") : Auth.GetMailTarget(sMail).sAddr == ""))	// 계정 아이디 (메일 있는 계정)·예전 메일 주소
		{
			sError = $"메일 받는 계정 오류 (없는 계정·메일 없음)·{m_nAlarmListMax}개 넘음";
		}
		else if (kAlarm.sHook != "" && !(Uri.TryCreate(kAlarm.sHook, UriKind.Absolute, out Uri? kUri) && (kUri.Scheme == Uri.UriSchemeHttp || kUri.Scheme == Uri.UriSchemeHttps)))
		{
			sError = "훅 주소 오류 (http·https)";
		}
		else if (kAlarm.aSMS.Length > m_nAlarmListMax || kAlarm.aSMS.Any(sSMS => Auth.IsAccount(sSMS) ? !Auth.HasPhone(sSMS) : !Regex.IsMatch(sSMS, @"^[A-Za-z0-9._-]{1,50}$")))	// SMS: 전화 번호 있는 계정 아이디 (예전: 아이디 형식, 전화번호 아님)
		{
			sError = $"문자 받는 계정 오류 (전화 번호 없음)·{m_nAlarmListMax}개 넘음";
		}

		return sError;
	}

	public static void RemoveAlarmAccount(string sID)	// 계정 삭제 때 (Auth.OnAccountDelete): 모든 알람 메일·SMS 받는 계정 (aMail·aSMS) 에서 그 아이디 뺌 → 설정 aAlarm 저장 (없음: 안 씀, 받는 곳 다 없어짐: 알람 그대로·발송 없음, 실패: 출력만)
	{
		bool funcHas(string[] arrItem) => arrItem.Contains(sID, StringComparer.OrdinalIgnoreCase);	// 그 아이디 있음
		string[] funcOut(string[] arrItem) => arrItem.Where(sItem => !string.Equals(sItem, sID, StringComparison.OrdinalIgnoreCase)).ToArray();	// 그 아이디 뺌

		lock (m_kSavedLock)
		{
			if (!m_listAlarm.Any(kAlarm => funcHas(kAlarm.aMail) || funcHas(kAlarm.aSMS)))
			{
				return;
			}

			List<AlarmItem> listAlarm = m_listAlarm.Select(kAlarm => !funcHas(kAlarm.aMail) && !funcHas(kAlarm.aSMS) ? kAlarm : new AlarmItem
			{
				nID		= kAlarm.nID,
				sKey	= kAlarm.sKey,
				sQuery	= kAlarm.sQuery,
				nMinute	= kAlarm.nMinute,
				nCount	= kAlarm.nCount,
				nDelay	= kAlarm.nDelay,
				aMail	= funcOut(kAlarm.aMail),
				sHook	= kAlarm.sHook,
				aSMS	= funcOut(kAlarm.aSMS),
				bEnable	= kAlarm.bEnable,
			}).ToList();	// 그 아이디 뺀 목록
			string sError = SaveAlarm(listAlarm);

			Log.Write(sError == "" ? INFO_ : WARN_, $"알람 받는 계정에서 뺌: {sID}{(sError == "" ? "" : $" 실패: {sError}")}");
		}
	}

	private static string SaveAlarm(List<AlarmItem> listAlarm)	// 알람 잠금 안에서: 설정 aAlarm 저장 → 성공: 메모리 목록 바꿈 (반환: 오류, 없음: "")
	{
		JsonArray	kArray	= new JsonArray(listAlarm.Select(kAlarm => (JsonNode?)new JsonObject
		{
			["nID"]		= kAlarm.nID,
			["sKey"]	= kAlarm.sKey,
			["sQuery"]	= kAlarm.sQuery,
			["nMinute"]	= kAlarm.nMinute,
			["nCount"]	= kAlarm.nCount,
			["nDelay"]	= kAlarm.nDelay,
			["aMail"]	= new JsonArray(kAlarm.aMail.Select(sMail => (JsonNode?)sMail).ToArray()),
			["sHook"]	= kAlarm.sHook,
			["aSMS"]	= new JsonArray(kAlarm.aSMS.Select(sSMS => (JsonNode?)sSMS).ToArray()),
			["bEnable"]	= kAlarm.bEnable,
		}).ToArray());	// 설정 aAlarm
		string		sError	= Config.SetArray("aAlarm", kArray);

		if (sError == "")
		{
			m_listAlarm = listAlarm;

			Alarm.SetAlarm(listAlarm);	// 매칭 스냅샷 교체 (다음 줄부터)
		}

		return sError;
	}

	private static List<AlarmItem> LoadAlarm(IEnumerable<IConfigurationSection>? listSection = null, List<string>? listWarn = null)	// 설정 aAlarm → 알람 (값 오류·ID 없음·중복: 오류 글 후 뺌, listSection 없음: 메모리 설정, listWarn: 오류 글 모음 (없음: 바로 출력))
	{
		List<AlarmItem> listAlarm = new List<AlarmItem>();

		foreach (IConfigurationSection kSection in listSection ?? Config.GetChildren("aAlarm"))
		{
			AlarmParam kParam = new AlarmParam
			{
				sKey	= kSection["sKey"],
				sQuery	= kSection["sQuery"],
				nMinute	= int.TryParse(kSection["nMinute"], out int nMinute) ? nMinute : null,
				nCount	= int.TryParse(kSection["nCount"], out int nCount) ? nCount : null,
				nDelay	= int.TryParse(kSection["nDelay"], out int nDelay) ? nDelay : null,
				aMail	= kSection.GetSection("aMail").GetChildren().Select(kItem => kItem.Value ?? "").ToArray(),
				sHook	= kSection["sHook"],
				aSMS	= kSection.GetSection("aSMS").GetChildren().Select(kItem => kItem.Value ?? "").ToArray(),
				bEnable	= bool.TryParse(kSection["bEnable"], out bool bEnable) && bEnable,
			};	// 설정 값
			AlarmItem	kAlarm	= ToAlarm(kParam, int.TryParse(kSection["nID"], out int nID) ? nID : 0);
			string		sError	= kAlarm.nID < 1 || listAlarm.Any(kItem => kItem.nID == kAlarm.nID) ? "ID 없음·중복" : CheckAlarm(kAlarm);

			if (sError != "")
			{
				AddWarn(listWarn, $"알람 설정 오류: aAlarm #{kAlarm.nID} {kAlarm.sKey} ({sError})");

				continue;
			}

			listAlarm.Add(kAlarm);
		}

		return listAlarm;
	}

	private static void AddWarn(List<string>? listWarn, string sWarn)	// 설정 오류 글: listWarn 있음 → 모음 (다시 읽기: 바뀔 때만 출력), 없음 → 바로 출력 (시작)
	{
		if (listWarn != null)
		{
			listWarn.Add(sWarn);

			return;
		}

		Log.Write(WARN_, sWarn);
	}

	private static IConfigurationRoot? ReadConfigFile(string sKey)	// 그 키 설정 파일 다시 읽기 (목록 팝업 열 때 json 직접 고침 반영, 따로 관리 파일: Query.json·Alarm.json·Field.json, 메모리 설정 Config 는 그대로, 없음·형식 오류: 출력 후 null → 메모리 목록 그대로)
	{
		string sPath = Config.GetKeyFile(sKey);

		try
		{
			return File.Exists(sPath) ? new ConfigurationBuilder().AddJsonFile(sPath, optional: false, reloadOnChange: false).Build() : null;
		}
		catch (Exception kException)	// JSON 형식 오류·사용 중
		{
			Log.Write(WARN_, $"설정 다시 읽기 실패: {sPath} ({kException.Message})");

			return null;
		}
	}

	private static void ReloadAlarm()	// 알람 팝업 열 때 (목록 요청): 설정 파일 aAlarm 다시 읽음 → 메모리와 다름: 바꿈·매칭 스냅샷 교체 (json 직접 고침, 오류 글: 그때만)
	{
		IConfigurationRoot? kConfig = ReadConfigFile("aAlarm");

		if (kConfig == null)
		{
			return;
		}

		List<string>	listWarn	= new List<string>();											// 오류 글
		List<AlarmItem>	listAlarm	= LoadAlarm(kConfig.GetSection("aAlarm").GetChildren(), listWarn);	// 파일 알람

		lock (m_kSavedLock)
		{
			if (JsonSerializer.Serialize(listAlarm) == JsonSerializer.Serialize(m_listAlarm))	// 값 같음 (AlarmItem: 참조 비교 안 씀)
			{
				return;
			}

			m_listAlarm = listAlarm;

			Alarm.SetAlarm(listAlarm);	// 매칭 스냅샷 교체 (다음 줄부터)
		}

		listWarn.ForEach(sWarn => Log.Write(WARN_, sWarn));

		Log.Write(INFO_, $"알람 다시 읽음: 설정 파일 aAlarm {listAlarm.Count}개 (json 직접 고침)");
	}

	private static void ReloadFieldRule()	// 필드 팝업 열 때 (목록 요청): 설정 파일 aField 다시 읽음 → 메모리와 다름: 바꿈·필드 목록 순서·접속 클라 얼라이브 답 (json 직접 고침, 오류 글: 그때만)
	{
		IConfigurationRoot? kConfig = ReadConfigFile("aField");

		if (kConfig == null)
		{
			return;
		}

		List<string>	listWarn	= new List<string>();											// 오류 글
		FieldRule[]		arrRule		= LoadFieldRule(kConfig.GetSection("aField").GetChildren(), listWarn);	// 파일 필드 정의
		int				nSent;																		// 알린 클라 수

		lock (m_kSavedLock)
		{
			if (arrRule.SequenceEqual(m_arrFieldRule))
			{
				return;
			}

			m_arrFieldRule = arrRule;

			InitField();

			nSent = TCPServer.SendAliveAll();
		}

		listWarn.ForEach(sWarn => Log.Write(WARN_, sWarn));

		Log.Write(INFO_, $"필드 정의 다시 읽음: 설정 파일 aField {string.Join(", ", arrRule.Select(kRule => kRule.sKey))} (json 직접 고침, 접속 클라 {nSent}대 알림)");
	}

	private static QueryResult GetQueryResult(string sQuery, long nStart, long nEnd, bool bCache, CancellationToken kToken)	// 쿼리 결과: bCache 이고 m_nCacheSec 초 안 같은 쿼리·기간이면 보관분, 아니면 새로 계산해 보관 (m_nCacheMax 개 넘음: 오래된 것 뺌, 중단: 예외 → 보관 안 함)
	{
		string sCacheKey = $"{nStart}\t{nEnd}\t{sQuery}";	// 보관 키 (기간·쿼리)

		lock (m_kCacheLock)
		{
			if (bCache && m_hmapCache.TryGetValue(sCacheKey, out QueryResult? kCached) && (DateTime.UtcNow - kCached.m_kTime).TotalSeconds < m_nCacheSec)
			{
				return kCached;
			}
		}

		QueryResult kResult = RunQuery(sQuery, nStart, nEnd, sCacheKey, kToken);	// 새로 계산 (중단: 예외)

		lock (m_kCacheLock)
		{
			PruneCache(kResult);

			m_hmapCache[sCacheKey] = kResult;
		}

		return kResult;
	}

	private static void PruneCache(QueryResult? kAdd)	// 쿼리 결과 보관 정리 (보관 잠금 안): 만료 (m_nCacheSec 초) 뺌 → 개수 m_nCacheMax·맞는 줄 합 m_nCacheHitMax (페이지 때 늘어난 것 포함) 넘음: 오래된 것부터 뺌 (kAdd: 넣을 결과 자리·몫 비움, null (타이머): 가장 새 것 1개 남김 → 상한 넘는 결과: 혼자 만료까지)
	{
		foreach (string sOld in m_hmapCache.Where(kPair => (DateTime.UtcNow - kPair.Value.m_kTime).TotalSeconds >= m_nCacheSec).Select(kPair => kPair.Key).ToList())
		{
			m_hmapCache.Remove(sOld);
		}

		int		nAddCount	= kAdd == null ? 0 : 1;											// 넣을 개수
		long	nAddHit		= kAdd == null ? 0 : Interlocked.Read(ref kAdd.m_nHitCount);		// 넣을 맞는 줄
		int		nKeep		= kAdd == null ? 1 : 0;											// 남길 개수

		while (m_hmapCache.Count > nKeep && (m_hmapCache.Count + nAddCount > m_nCacheMax || m_hmapCache.Values.Sum(kItem => Interlocked.Read(ref kItem.m_nHitCount)) + nAddHit > m_nCacheHitMax))
		{
			m_hmapCache.Remove(m_hmapCache.MinBy(kPair => kPair.Value.m_kTime).Key);
		}
	}

	private static string PrepareQuery(QueryResult kResult, string sQuery, long nStart, long nEnd)	// 쿼리 조건: 조건 트리·기간·읽을 단어 (해시)·필드 키 (반환: 오류, 없음: "")
	{
		SearchNode? kRoot = sQuery.Length > 1000 ? null : SearchNode.Parse(sQuery);	// 조건 트리 (없음·너무 김: null)

		if (kRoot == null)
		{
			return "쿼리 없음";
		}

		if (!TryGetMinuteRange(nStart, nEnd, out DateTime kFrom, out DateTime kTo))
		{
			return "기간 오류";
		}

		if ((kTo - kFrom).TotalDays > m_nQueryDayMax)
		{
			return "기간 초과 (최대 3년)";
		}

		if (GetWildScan(kRoot, false).bScan)	// 와일드카드만·OR 쪽 와일드카드: 기간 전체 원문 읽음 → 막음
		{
			return "와일드카드 1차 조건 필요 (다른 검색어와 AND, 예: test AND *ab*)";
		}

		HashSet<string> setWord		= new HashSet<string>(StringComparer.Ordinal);				// 쿼리 단어 모두 (읽을 포스팅)
		HashSet<string> setField	= new HashSet<string>(StringComparer.OrdinalIgnoreCase);	// 쿼리 필드 키 모두 (필드\t값)

		CollectTerm(kRoot, setWord, setField);

		kResult.m_kRoot	= kRoot;
		kResult.m_kFrom	= kFrom;
		kResult.m_kTo	= kTo;
		kResult.m_bAll	= HasAll(kRoot) || HasWild(kRoot);		// 와일드카드: 앞 조건 없으면 시간 전체 줄이 후보
		kResult.m_bExact	= HasExact(kRoot) || HasWild(kRoot);	// 와일드카드: 후보 줄 원문 확인

		kResult.m_listWord.AddRange(setWord.Select(sWord => (sWord, GetHash(sWord))));
		kResult.m_listField.AddRange(setField);

		return "";
	}

	private static QueryResult RunQuery(string sQuery, long nStart, long nEnd, string sCacheKey, CancellationToken kToken)	// 쿼리 계산: 시간 폴더마다 병렬로 시간별 건수 (단어·필드 1개·시간 전체: 사전 건수만, 그 외: 포스팅 병합), m_nHitKeepHour 이하: 맞는 줄 보관, 1시간 이하: 분별 건수
	{
		QueryResult kResult = new QueryResult(sCacheKey);

		kResult.m_sError = PrepareQuery(kResult, sQuery, nStart, nEnd);

		if (kResult.m_sError != "")
		{
			return kResult;
		}

		DateTime	kFrom		= kResult.m_kFrom;
		DateTime	kTo			= kResult.m_kTo;
		DateTime	kHourFirst	= kFrom.Date.AddHours(kFrom.Hour);																		// 기간 첫 시간 (정각)
		int			nHourCount	= (int)Math.Ceiling((kTo - kHourFirst).TotalHours);														// 기간 시간 수 (끝 미포함)
		bool		bSingle		= kResult.m_kRoot!.m_eOp == SEARCH_OP.SEARCH_OP_TERM && kResult.m_listWord.Count + kResult.m_listField.Count == 1 && !kResult.m_bExact && (kTo - kFrom).TotalMinutes > 60;	// 단어·필드 1개 쿼리 (시간 전체: 사전 건수만, 1시간 이하·IP·날짜·시간: 안 씀)
		bool		bAllOnly	= kResult.m_kRoot.m_eOp == SEARCH_OP.SEARCH_OP_TERM && IsAll(kResult.m_kRoot) && (kTo - kFrom).TotalMinutes > 60;	// * 만 (시간 전체: 줄 목록 줄 수만, 1시간 이하: 안 씀)

		kResult.m_arrHour	= new long[nHourCount];
		kResult.m_arrCount	= new int[nHourCount];

		Parallel.For(0, nHourCount, new ParallelOptions { MaxDegreeOfParallelism = m_nQueryParallel, CancellationToken = kToken }, nIndex =>	// 시간 폴더마다 병렬 (없는 시간: 0건, 중단: 남은 시간 안 읽음)
		{
			DateTime	kHour		= kHourFirst.AddHours(nIndex);					// 이 시간 (정각)
			long		nHour		= ToHour(kHour);								// yyyyMMddHH
			string		sHourDir	= GetHourDir(nHour);							// 시간 폴더

			(int nMsFrom, int nMsTo) = GetMsRange(kFrom, kTo, kHour);				// 이 시간 안 기간 (ms, 시작 포함·끝 미포함)

			kResult.m_arrHour[nIndex] = nHour;

			try
			{
				if (!Directory.Exists(sHourDir))	// 이 시간 색인 없음: 0건
				{
					return;
				}

				if ((bSingle || bAllOnly) && nMsFrom == 0 && nMsTo == m_nHourMs)	// 단어·필드 1개·시간 전체: 세그먼트 사전 건수 합만, * 만: 줄 목록 줄 수만 (포스팅 안 읽음)
				{
					kResult.m_arrCount[nIndex] = bAllOnly ? GetDocCount(sHourDir) : CountTerm(kResult, sHourDir);

					return;
				}

				if (nHourCount > m_nHitKeepHour)	// 긴 기간: 건수만 (시간 전체: 줄 목록 안 읽음, 페이지 때 그 시간만 다시 읽음)
				{
					kResult.m_arrCount[nIndex] = ReadHourDoc(kResult, sHourDir, nMsFrom, nMsTo, kToken).Length;

					return;
				}

				(int nMs, int nDoc)[] arrHit = ReadHourHit(kResult, sHourDir, nMsFrom, nMsTo, kToken);	// 맞는 줄 (분초·줄 번호)

				kResult.m_arrCount[nIndex] = arrHit.Length;
				kResult.m_hmapHit[nHour] = arrHit;	// 짧은 기간: 분별 건수·페이지에 다시 씀

				Interlocked.Add(ref kResult.m_nHitCount, arrHit.Length);
			}
			catch (Exception kException) when (kException is not OperationCanceledException)	// 사용 중·권한 없음·형식 오류 (첫 오류만, 중단·제한 시간: 그대로 나감)
			{
				lock (kResult)
				{
					if (kResult.m_sError == "")
					{
						kResult.m_sError = GetErrorText(kException, "쿼리 읽기");
					}
				}
			}
		});

		if (kResult.m_sError != "")
		{
			return kResult;
		}

		kResult.m_nTotal = kResult.m_arrCount.Sum();

		if ((kTo - kFrom).TotalMinutes <= 60)	// 분별 건수 (기간 1시간 이하만 → 맞는 줄 결과에 있음, 모든 분 0 포함)
		{
			Dictionary<long, int> hmapMinute = new Dictionary<long, int>();	// yyyyMMddHHmm → 건수

			for (int nIndex = 0; nIndex < nHourCount; ++nIndex)
			{
				if (kResult.m_hmapHit.TryGetValue(kResult.m_arrHour[nIndex], out (int nMs, int nDoc)[]? arrHit))
				{
					foreach ((int nMs, _) in arrHit)
					{
						long nMinute = kResult.m_arrHour[nIndex] * 100 + nMs / 60000;	// yyyyMMddHHmm

						hmapMinute[nMinute] = hmapMinute.GetValueOrDefault(nMinute) + 1;
					}
				}
			}

			for (DateTime kMinute = kFrom; kMinute < kTo; kMinute = kMinute.AddMinutes(1))
			{
				long nMinute = long.Parse(kMinute.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);	// 분

				kResult.m_listMinute.Add(new { nMinute, nCount = hmapMinute.GetValueOrDefault(nMinute) });
			}
		}

		if ((kTo - kFrom).TotalMinutes <= 1)	// 초별 건수 (기간 1분 이하만, 모든 초 0 포함)
		{
			Dictionary<long, int> hmapSecond = new Dictionary<long, int>();	// yyyyMMddHHmmss → 건수

			for (int nIndex = 0; nIndex < nHourCount; ++nIndex)
			{
				if (kResult.m_hmapHit.TryGetValue(kResult.m_arrHour[nIndex], out (int nMs, int nDoc)[]? arrHit))
				{
					foreach ((int nMs, _) in arrHit)
					{
						long nSecond = kResult.m_arrHour[nIndex] * 10000 + nMs / 60000 * 100 + nMs / 1000 % 60;	// yyyyMMddHHmmss

						hmapSecond[nSecond] = hmapSecond.GetValueOrDefault(nSecond) + 1;
					}
				}
			}

			for (DateTime kSecond = kFrom; kSecond < kTo; kSecond = kSecond.AddSeconds(1))
			{
				long nSecond = long.Parse(kSecond.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);	// 초

				kResult.m_listSecond.Add(new { nSecond, nCount = hmapSecond.GetValueOrDefault(nSecond) });
			}
		}

		return kResult;
	}

	private static (string sError, int[] arrCount) RunLens(QueryResult kPlan, DateTime kFirst, int nCell, CancellationToken kToken)	// 렌즈 초·분봉 계산: 기간 시간 폴더마다 병렬로 맞는 줄 분초 → nCell 초 칸마다 건수 (반환: 첫 오류·kFirst 부터 칸마다 건수, 중단: OperationCanceledException)
	{
		DateTime	kHourFirst	= kPlan.m_kFrom.Date.AddHours(kPlan.m_kFrom.Hour);									// 기간 첫 시간 (정각)
		int			nHourCount	= (int)Math.Ceiling((kPlan.m_kTo - kHourFirst).TotalHours);							// 기간 시간 수 (끝 미포함)
		int[]		arrCount	= new int[(int)Math.Ceiling((kPlan.m_kTo - kFirst).TotalSeconds / nCell)];			// 칸마다 건수
		object		kLock		= new object();																		// 오류 잠금
		string		sError		= "";																				// 첫 오류

		Parallel.For(0, nHourCount, new ParallelOptions { MaxDegreeOfParallelism = m_nQueryParallel, CancellationToken = kToken }, nIndex =>	// 시간 폴더마다 병렬 (시간마다 다른 칸 → 잠금 없음, 중단: 남은 시간 안 읽음)
		{
			DateTime	kHour		= kHourFirst.AddHours(nIndex);						// 이 시간 (정각)
			string		sHourDir	= GetHourDir(ToHour(kHour));						// 시간 폴더
			int			nBase		= (int)(kHour - kFirst).TotalSeconds;				// 이 시간 정각의 초 위치 (첫 시간: 0 이하)

			(int nMsFrom, int nMsTo) = GetMsRange(kPlan.m_kFrom, kPlan.m_kTo, kHour);

			try
			{
				if (!Directory.Exists(sHourDir))	// 이 시간 색인 없음: 0건
				{
					return;
				}

				foreach ((int nMs, _) in ReadHourHit(kPlan, sHourDir, nMsFrom, nMsTo, kToken))
				{
					++arrCount[(nBase + nMs / 1000) / nCell];
				}
			}
			catch (Exception kException) when (kException is not OperationCanceledException)	// 사용 중·권한 없음·형식 오류 (첫 오류만, 중단: 그대로 나감)
			{
				lock (kLock)
				{
					if (sError == "")
					{
						sError = GetErrorText(kException, "렌즈 읽기");
					}
				}
			}
		});

		return (sError, arrCount);
	}

	private static (string sError, int nTotal) RunStat(QueryResult kPlan, Dictionary<string, (string sKey, int nCount)> hmapCount, CancellationToken kToken)	// 통계 계산: 기간 시간 폴더마다 병렬로 맞는 줄 → 필드 값마다 맞는 줄 수 → hmapCount 에 더함 (반환: 첫 오류·맞는 줄 수, 중단: OperationCanceledException)
	{
		DateTime	kHourFirst	= kPlan.m_kFrom.Date.AddHours(kPlan.m_kFrom.Hour);					// 기간 첫 시간 (정각)
		int			nHourCount	= (int)Math.Ceiling((kPlan.m_kTo - kHourFirst).TotalHours);			// 기간 시간 수 (끝 미포함)
		object		kLock		= new object();														// 합치기 잠금
		string		sError		= "";																// 첫 오류
		int			nTotal		= 0;																// 맞는 줄 수

		Parallel.For(0, nHourCount, new ParallelOptions { MaxDegreeOfParallelism = m_nQueryParallel, CancellationToken = kToken }, nIndex =>	// 시간 폴더마다 병렬 (중단: 남은 시간 안 읽음)
		{
			DateTime	kHour		= kHourFirst.AddHours(nIndex);			// 이 시간 (정각)
			string		sHourDir	= GetHourDir(ToHour(kHour));			// 시간 폴더

			(int nMsFrom, int nMsTo) = GetMsRange(kPlan.m_kFrom, kPlan.m_kTo, kHour);

			try
			{
				if (!Directory.Exists(sHourDir))
				{
					return;
				}

				int[] arrDoc = ReadHourDoc(kPlan, sHourDir, nMsFrom, nMsTo, kToken);	// 맞는 줄 번호

				if (arrDoc.Length == 0)
				{
					return;
				}

				Dictionary<string, (string sKey, int nCount)> hmapHour = CountField(arrDoc, sHourDir, kToken);	// 이 시간 필드 값마다 건수

				lock (kLock)
				{
					nTotal += arrDoc.Length;

					foreach ((string sFieldKey, int nCount) in hmapHour.Values)
					{
						hmapCount[sFieldKey] = hmapCount.TryGetValue(sFieldKey, out (string sKey, int nCount) kOld) ? (kOld.sKey, kOld.nCount + nCount) : (sFieldKey, nCount);
					}
				}
			}
			catch (Exception kException) when (kException is not OperationCanceledException)	// 사용 중·권한 없음·형식 오류 (첫 오류만, 중단: 그대로 나감)
			{
				lock (kLock)
				{
					if (sError == "")
					{
						sError = GetErrorText(kException, "쿼리 통계");
					}
				}
			}
		});

		return (sError, nTotal);
	}

	private static Dictionary<string, (string sKey, int nCount)> CountField(int[] arrDoc, string sHourDir, CancellationToken kToken)	// 시간 폴더 하나: 세그먼트마다 필드 사전 → 필드 포스팅 영역 한 번에 읽어 값마다 맞는 줄 수 (0건 뺌, 맞는 줄: 표시 배열)
	{
		Dictionary<string, (string sKey, int nCount)> hmapCount = new Dictionary<string, (string sKey, int nCount)>(StringComparer.OrdinalIgnoreCase);	// 필드 키 → 첫 표기·건수
		bool[] arrMask = new bool[arrDoc[^1] + 1];	// 맞는 줄 표시 (줄 번호, arrDoc 오름차순·비지 않음)

		foreach (int nDoc in arrDoc)
		{
			arrMask[nDoc] = true;
		}

		ReadSegments(sHourDir, (kStream, kHead) =>
		{
			Gate.Yield(kToken);

			List<FieldEntry> listDict = ReadFieldDict(kStream, kHead);	// 필드 사전

			if (listDict.Count == 0)
			{
				return;
			}

			int		nFrom		= listDict.Min(kEntry => kEntry.nOffset);					// 필드 포스팅 영역 시작 (단어 포스팅 뒤)
			byte[]	arrBlob		= new byte[listDict.Max(kEntry => kEntry.nOffset + kEntry.nBytes) - nFrom];	// 필드 포스팅 영역

			kStream.Position = GetPostStart(kHead) + nFrom;
			kStream.ReadExactly(arrBlob);

			foreach (FieldEntry kEntry in listDict)
			{
				int nHit = 0;	// 맞는 줄 수

				Gate.Yield(kToken);

				foreach (int nDoc in DecodePosting(arrBlob.AsSpan(kEntry.nOffset - nFrom, kEntry.nBytes), kEntry.nCount))
				{
					if (nDoc < arrMask.Length && arrMask[nDoc])
					{
						++nHit;
					}
				}

				if (nHit > 0)
				{
					hmapCount[kEntry.sKey] = hmapCount.TryGetValue(kEntry.sKey, out (string sKey, int nCount) kOld) ? (kOld.sKey, kOld.nCount + nHit) : (kEntry.sKey, nHit);
				}
			}
		}, () => hmapCount.Clear());

		return hmapCount;
	}

	private static int CountTerm(QueryResult kResult, string sHourDir)	// 단어·필드 1개 쿼리·시간 전체: 세그먼트마다 단어·필드 사전의 건수만 더함 (포스팅 안 읽음)
	{
		int nCount = 0;	// 건수 합

		ReadSegments(sHourDir, (kStream, kHead) =>
		{
			if (kResult.m_listWord.Count == 1)	// 단어
			{
				if (FindTerm(kStream, kHead, kResult.m_listWord[0].nHash, out _, out int nTermCount, out _))
				{
					nCount += nTermCount;
				}

				return;
			}

			foreach (FieldEntry kEntry in ReadFieldDict(kStream, kHead))	// 필드
			{
				if (string.Equals(kEntry.sKey, kResult.m_listField[0], StringComparison.OrdinalIgnoreCase))
				{
					nCount += kEntry.nCount;

					break;
				}
			}
		}, () => nCount = 0);

		return nCount;
	}

	private static (int nMs, int nDoc)[] ReadHourHit(QueryResult kResult, string sHourDir, int nMsFrom, int nMsTo, CancellationToken kToken)	// 시간 폴더 하나 맞는 줄 (분초·줄 번호): 포스팅 조건 계산 → doc.bin 분초로 기간 안 (nMsFrom 포함·nMsTo 미포함)
	{
		int[] arrDoc = EvalHour(kResult, sHourDir, kToken);	// 맞는 줄 번호

		if (arrDoc.Length == 0)
		{
			return Array.Empty<(int nMs, int nDoc)>();
		}

		List<(int nMs, int nDoc)>	listHit		= new List<(int nMs, int nDoc)>(arrDoc.Length);	// 기간 안 줄

		if ((long)arrDoc.Length * m_nDocRecord * 8 < (long)GetDocCount(sHourDir) * m_nDocRecord)	// 맞는 줄이 전체의 1/8 미만: 줄 목록에서 그 자리만 읽음 (큰 시간 폴더 전체를 메모리에 안 올림)
		{
			using FileStream? kDoc = OpenDocTable(sHourDir);

			foreach (int nDoc in arrDoc)
			{
				Gate.Yield(kToken);

				if (ReadDocRecord(kDoc, nDoc, out int nMs, out _, out _, out _) && nMs >= nMsFrom && nMs < nMsTo)
				{
					listHit.Add((nMs, nDoc));
				}
			}

			return listHit.ToArray();
		}

		byte[] arrTable = ReadDocTable(sHourDir);	// 줄 목록 전체 (맞는 줄 많음: 한 번에)

		foreach (int nDoc in arrDoc)
		{
			Gate.Yield(kToken);

			if (TryGetDoc(arrTable, nDoc, out int nMs, out _, out _, out _) && nMs >= nMsFrom && nMs < nMsTo)
			{
				listHit.Add((nMs, nDoc));
			}
		}

		return listHit.ToArray();
	}

	private static int[] ReadHourDoc(QueryResult kPlan, string sHourDir, int nMsFrom, int nMsTo, CancellationToken kToken)	// 시간 폴더 하나 맞는 줄 번호 (오름차순, 시간 전체: 줄 목록 안 읽음, 기간 양끝 시간: 줄 목록 분초로 기간 안만)
	{
		int[] arrDoc = EvalHour(kPlan, sHourDir, kToken);	// 맞는 줄 번호

		if (arrDoc.Length == 0 || (nMsFrom == 0 && nMsTo == m_nHourMs))
		{
			return arrDoc;
		}

		byte[] arrTable = ReadDocTable(sHourDir);	// 줄 목록

		return arrDoc.Where(nDoc =>
		{
			Gate.Yield(kToken);

			return TryGetDoc(arrTable, nDoc, out int nMs, out _, out _, out _) && nMs >= nMsFrom && nMs < nMsTo;
		}).ToArray();
	}

	private static int[] EvalHour(QueryResult kResult, string sHourDir, CancellationToken kToken)	// 시간 폴더 하나 조건 계산 (IP·날짜·시간 검색어: 후보 줄 원문 글자 확인, 원문 파일: 이 시간 안에서만 열고 닫음)
	{
		Dictionary<int, FileStream> hmapStream = new Dictionary<int, FileStream>();	// 원문 파일 번호 → 연 원문 파일

		try
		{
			return EvalQuery(kResult.m_kRoot!, ReadHourPosting(kResult, sHourDir, kToken), kResult.m_bExact ? MakeTextReader(sHourDir, hmapStream, kToken) : null);
		}
		finally
		{
			foreach (FileStream kStream in hmapStream.Values)
			{
				kStream.Dispose();
			}
		}
	}

	private static Func<int, string?> MakeTextReader(string sHourDir, Dictionary<int, FileStream> hmapStream, CancellationToken kToken)	// 시간 폴더 줄 번호 → 원문에서 앞 시간 뺀 내용 (색인과 같은 범위, 줄 목록은 처음 쓸 때 1번 읽음, 없음: null)
	{
		byte[]? arrTable = null;	// 줄 목록

		return nDoc =>
		{
			Gate.Yield(kToken);

			arrTable ??= ReadDocTable(sHourDir);

			if (!TryGetDoc(arrTable, nDoc, out _, out int nFileId, out long nOffset, out int nLength))
			{
				return null;
			}

			(string sMachine, string sPath, _, _)	= GetFileInfo(nFileId);
			string?									sText	= ReadOrigin(hmapStream, nFileId, sMachine, sPath, nOffset, nLength, out _);

			return sText == null ? null : GetLogText(sText);
		};
	}

	private static Dictionary<string, int[]> ReadHourPosting(QueryResult kResult, string sHourDir, CancellationToken kToken)	// 쿼리 단어·필드마다 포스팅: 세그먼트마다 사전에서 찾아 세그먼트 순 이어 붙임 (범위 안 겹침 → 줄 번호 오름차순, 없음: 빈 배열)
	{
		Dictionary<string, List<int[]>> hmapPart = new Dictionary<string, List<int[]>>(StringComparer.OrdinalIgnoreCase);	// 단어·필드 키 → 세그먼트마다 포스팅

		foreach ((string sWord, _) in kResult.m_listWord)
		{
			hmapPart[sWord] = new List<int[]>();
		}

		foreach (string sFieldKey in kResult.m_listField)
		{
			hmapPart[sFieldKey] = new List<int[]>();
		}

		ReadSegments(sHourDir, (kStream, kHead) =>
		{
			foreach ((string sWord, ulong nHash) in kResult.m_listWord)
			{
				Gate.Yield(kToken);

				if (FindTerm(kStream, kHead, nHash, out int nOffset, out int nCount, out int nBytes))
				{
					hmapPart[sWord].Add(ReadPosting(kStream, kHead, nOffset, nCount, nBytes));
				}
			}

			if (kResult.m_listField.Count == 0)
			{
				return;
			}

			foreach (FieldEntry kEntry in ReadFieldDict(kStream, kHead))
			{
				Gate.Yield(kToken);

				if (hmapPart.TryGetValue(kEntry.sKey, out List<int[]>? listPart))	// 쿼리 필드 (필드 키에만 탭 → 단어와 안 겹침)
				{
					listPart.Add(ReadPosting(kStream, kHead, kEntry.nOffset, kEntry.nCount, kEntry.nBytes));
				}
			}
		}, () =>
		{
			foreach (List<int[]> listPart in hmapPart.Values)	// 다시 읽음: 모은 것 비움
			{
				listPart.Clear();
			}
		});

		Dictionary<string, int[]> hmapPost = hmapPart.ToDictionary(kPair => kPair.Key, kPair => Concat(kPair.Value), StringComparer.OrdinalIgnoreCase);	// 단어·필드 키 → 포스팅

		if (kResult.m_bAll)	// * (전체 로그): 이 시간 모든 줄 번호 (단어·필드 키와 안 겹침)
		{
			hmapPost["*"] = Enumerable.Range(0, GetDocCount(sHourDir)).ToArray();
		}

		return hmapPost;
	}

	private static void ReadSegments(string sHourDir, Action<FileStream, SegmentHead> funcSegment, Action funcReset)	// 시간 폴더 세그먼트마다 (줄 번호 순) 열어 머리 읽고 funcSegment (목록·열기: 세그먼트 읽기 잠금 안, 없는 파일: funcReset 뒤 1번 다시)
	{
		for (int nTry = 0; ; ++nTry)
		{
			List<FileStream> listStream = new List<FileStream>();	// 연 세그먼트 (줄 번호 순)

			try
			{
				m_kSegmentLock.EnterReadLock();

				try
				{
					foreach ((_, _, string sFile) in GetSegmentList(sHourDir))
					{
						listStream.Add(OpenSegment(sFile));
					}
				}
				finally
				{
					m_kSegmentLock.ExitReadLock();
				}

				foreach (FileStream kStream in listStream)
				{
					funcSegment(kStream, ReadSegmentHead(kStream));
				}

				return;
			}
			catch (FileNotFoundException) when (nTry == 0)	// 합치며 지운 세그먼트 (잠금 밖 삭제 등)
			{
				funcReset();
			}
			finally
			{
				foreach (FileStream kStream in listStream)
				{
					kStream.Dispose();
				}
			}
		}
	}

	private static void DeleteSegments(IEnumerable<string> listFile)	// 옛 세그먼트 삭제 (세그먼트 쓰기 잠금 안, 읽는 중·지우는 중: 다음 타이머에 다시)
	{
		m_kSegmentLock.EnterWriteLock();

		try
		{
			foreach (string sFile in listFile)
			{
				try
				{
					File.Delete(sFile);
				}
				catch (Exception kException) when (kException is IOException or UnauthorizedAccessException)	// 읽는 중·지우는 중
				{
				}
			}
		}
		finally
		{
			m_kSegmentLock.ExitWriteLock();
		}
	}

	private static List<(int nFirst, int nLast, string sFile)> GetSegmentList(string sHourDir, List<string>? listStale = null)	// 시간 폴더 세그먼트 (첫 줄 번호 순, 이름 "첫-끝.idx", 다른 범위 안에 든 옛 파일: 빼고 listStale 에, 폴더 없음: 빈 목록)
	{
		List<(int nFirst, int nLast, string sFile)>	listAll	= new List<(int nFirst, int nLast, string sFile)>();	// 모든 세그먼트
		List<(int nFirst, int nLast, string sFile)>	listSeg	= new List<(int nFirst, int nLast, string sFile)>();	// 읽을 세그먼트

		if (!Directory.Exists(sHourDir))
		{
			return listSeg;
		}

		foreach (string sFile in Directory.EnumerateFiles(sHourDir, "*.idx"))
		{
			string[] arrPart = Path.GetFileNameWithoutExtension(sFile).Split('-');	// 첫-끝

			if (arrPart.Length == 2 && int.TryParse(arrPart[0], NumberStyles.None, CultureInfo.InvariantCulture, out int nFirst) && int.TryParse(arrPart[1], NumberStyles.None, CultureInfo.InvariantCulture, out int nLast) && nFirst <= nLast)
			{
				listAll.Add((nFirst, nLast, sFile));
			}
		}

		listAll.Sort((kLeft, kRight) => kLeft.nFirst != kRight.nFirst ? kLeft.nFirst.CompareTo(kRight.nFirst) : kRight.nLast.CompareTo(kLeft.nLast));	// 첫 줄 번호 순 (같으면 넓은 것 먼저)

		foreach ((int nFirst, int nLast, string sFile) kSeg in listAll)
		{
			if (listAll.Any(kOther => kOther.sFile != kSeg.sFile && kOther.nFirst <= kSeg.nFirst && kSeg.nLast <= kOther.nLast && kOther.nLast - kOther.nFirst > kSeg.nLast - kSeg.nFirst))	// 더 넓은 세그먼트 범위 안: 합친 뒤 옛 파일
			{
				listStale?.Add(kSeg.sFile);
			}
			else
			{
				listSeg.Add(kSeg);
			}
		}

		return listSeg;
	}

	private static FileStream OpenSegment(string sFile)	// 세그먼트·줄 목록 읽기 열기 (쓰는 중·지우는 중 허용)
	{
		return new FileStream(sFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
	}

	private static SegmentHead ReadSegmentHead(FileStream kStream)	// 세그먼트 머리: 버전 확인 → 단어 수·필드 사전 바이트·버킷 시작 257 (형식 오류: 예외 → 쿼리 오류로 표시)
	{
		byte[] arrHeader = new byte[m_nSegHeader];

		if (kStream.Length < m_nSegHeader)
		{
			throw new InvalidDataException($"세그먼트 형식 오류: {kStream.Name}");
		}

		kStream.ReadExactly(arrHeader);

		if (BinaryPrimitives.ReadInt32LittleEndian(arrHeader) != m_nSegVersion)
		{
			throw new InvalidDataException($"세그먼트 버전 오류 (Data\\Index 지우고 다시 쌓기): {kStream.Name}");
		}

		int[] arrBucket = new int[m_nSegBucket + 1];	// 버킷 시작 (단어 사전 칸 번호, [256] = 단어 수)

		for (int nBucket = 0; nBucket <= m_nSegBucket; ++nBucket)
		{
			arrBucket[nBucket] = BinaryPrimitives.ReadInt32LittleEndian(arrHeader.AsSpan(12 + nBucket * 4));
		}

		return new SegmentHead(BinaryPrimitives.ReadInt32LittleEndian(arrHeader.AsSpan(4)), BinaryPrimitives.ReadInt32LittleEndian(arrHeader.AsSpan(8)), arrBucket);
	}

	private static long GetPostStart(SegmentHead kHead)	// 세그먼트 포스팅 영역 시작 (머리 + 단어 사전 + 필드 사전 뒤)
	{
		return m_nSegHeader + (long)kHead.nTermCount * m_nSegEntry + kHead.nFieldBytes;
	}

	private static bool FindTerm(FileStream kStream, SegmentHead kHead, ulong nHash, out int nOffset, out int nCount, out int nBytes)	// 단어 사전에서 해시 찾기: 버킷 (해시 상위 바이트) 칸들만 읽어 비교 (없음: false)
	{
		int nBucket	= (int)(nHash >> 56);
		int nStart	= kHead.arrBucket[nBucket];			// 버킷 첫 칸
		int nEnd	= kHead.arrBucket[nBucket + 1];		// 버킷 끝 칸 (미포함)

		nOffset	= 0;
		nCount	= 0;
		nBytes	= 0;

		if (nEnd <= nStart)
		{
			return false;
		}

		byte[] arrEntry = new byte[(nEnd - nStart) * m_nSegEntry];	// 버킷 칸들

		kStream.Position = m_nSegHeader + (long)nStart * m_nSegEntry;
		kStream.ReadExactly(arrEntry);

		for (int nIndex = 0; nIndex < nEnd - nStart; ++nIndex)
		{
			ReadOnlySpan<byte> kEntry = arrEntry.AsSpan(nIndex * m_nSegEntry, m_nSegEntry);	// 칸: 해시·포스팅 위치·건수·바이트

			if (BinaryPrimitives.ReadUInt64LittleEndian(kEntry) == nHash)
			{
				nOffset	= BinaryPrimitives.ReadInt32LittleEndian(kEntry[8..]);
				nCount	= BinaryPrimitives.ReadInt32LittleEndian(kEntry[12..]);
				nBytes	= BinaryPrimitives.ReadInt32LittleEndian(kEntry[16..]);

				return true;
			}
		}

		return false;
	}

	private static List<FieldEntry> ReadFieldDict(FileStream kStream, SegmentHead kHead)	// 필드 사전 전체 (단어 사전 뒤, 칸: 포스팅 위치·건수·바이트·키 "필드\t값", 한 번에 읽음, 없음: 빈 목록)
	{
		List<FieldEntry> listDict = new List<FieldEntry>();

		if (kHead.nFieldBytes == 0)
		{
			return listDict;
		}

		byte[] arrDict = new byte[kHead.nFieldBytes];

		kStream.Position = m_nSegHeader + (long)kHead.nTermCount * m_nSegEntry;
		kStream.ReadExactly(arrDict);

		for (int nPos = 0; nPos + m_nSegField <= arrDict.Length;)
		{
			ReadOnlySpan<byte>	kEntry		= arrDict.AsSpan(nPos);											// 칸
			int					nKeyBytes	= BinaryPrimitives.ReadUInt16LittleEndian(kEntry[12..]);			// 키 바이트

			listDict.Add(new FieldEntry(Encoding.UTF8.GetString(arrDict, nPos + m_nSegField, nKeyBytes), BinaryPrimitives.ReadInt32LittleEndian(kEntry), BinaryPrimitives.ReadInt32LittleEndian(kEntry[4..]), BinaryPrimitives.ReadInt32LittleEndian(kEntry[8..])));

			nPos += m_nSegField + nKeyBytes;
		}

		return listDict;
	}

	private static int[] ReadPosting(FileStream kStream, SegmentHead kHead, int nOffset, int nCount, int nBytes)	// 포스팅 읽기: 사전 뒤 포스팅 영역의 nOffset 부터 nBytes → 줄 번호 nCount 개 (오름차순)
	{
		byte[] arrBytes = new byte[nBytes];

		kStream.Position = GetPostStart(kHead) + nOffset;
		kStream.ReadExactly(arrBytes);

		return DecodePosting(arrBytes, nCount);
	}

	private static byte[] ReadDocTable(string sHourDir)	// 시간 폴더 줄 목록 (doc.bin) 전체 (쓰는 중 허용, 없음: 빈 배열)
	{
		string sFile = Path.Combine(sHourDir, "doc.bin");	// 줄 목록

		if (!File.Exists(sFile))
		{
			return Array.Empty<byte>();
		}

		using FileStream kStream = OpenSegment(sFile);

		byte[]	arrTable	= new byte[kStream.Length];
		int		nRead		= kStream.ReadAtLeast(arrTable, arrTable.Length, false);	// 읽은 바이트

		return nRead == arrTable.Length ? arrTable : arrTable[..nRead];
	}

	private static FileStream? OpenDocTable(string sHourDir)	// 시간 폴더 줄 목록 (doc.bin) 열기 (줄마다 그 자리만 읽기용, 쓰는 중 허용, 없음: null)
	{
		string sFile = Path.Combine(sHourDir, "doc.bin");	// 줄 목록

		return File.Exists(sFile) ? OpenSegment(sFile) : null;
	}

	private static bool ReadDocRecord(FileStream? kDoc, int nDoc, out int nMs, out int nFileId, out long nOffset, out int nLength)	// 연 줄 목록에서 줄 번호 1개의 분초·원문 파일 번호·위치·바이트 (그 자리만 읽음, 없음·목록 밖: false)
	{
		Span<byte>	kRecord	= stackalloc byte[m_nDocRecord];	// 한 줄
		long		nPos	= (long)nDoc * m_nDocRecord;		// 줄 위치

		nMs		= 0;
		nFileId	= 0;
		nOffset	= 0;
		nLength	= 0;

		if (kDoc == null || nDoc < 0 || nPos + m_nDocRecord > kDoc.Length || RandomAccess.Read(kDoc.SafeFileHandle, kRecord, nPos) != m_nDocRecord)
		{
			return false;
		}

		nMs		= BinaryPrimitives.ReadInt32LittleEndian(kRecord);
		nFileId	= BinaryPrimitives.ReadInt32LittleEndian(kRecord[4..]);
		nOffset	= BinaryPrimitives.ReadInt64LittleEndian(kRecord[8..]);
		nLength	= BinaryPrimitives.ReadInt32LittleEndian(kRecord[16..]);

		return true;
	}

	private static bool TryGetDoc(byte[] arrTable, int nDoc, out int nMs, out int nFileId, out long nOffset, out int nLength)	// 줄 목록에서 줄 번호의 분초·원문 파일 번호·원문 위치·원문 바이트 (목록 밖: false)
	{
		long nPos = (long)nDoc * m_nDocRecord;	// 줄 위치

		if (nDoc < 0 || nPos + m_nDocRecord > arrTable.Length)
		{
			nMs		= 0;
			nFileId	= 0;
			nOffset	= 0;
			nLength	= 0;

			return false;
		}

		ReadOnlySpan<byte> kRecord = arrTable.AsSpan((int)nPos, m_nDocRecord);	// 한 줄

		nMs		= BinaryPrimitives.ReadInt32LittleEndian(kRecord);
		nFileId	= BinaryPrimitives.ReadInt32LittleEndian(kRecord[4..]);
		nOffset	= BinaryPrimitives.ReadInt64LittleEndian(kRecord[8..]);
		nLength	= BinaryPrimitives.ReadInt32LittleEndian(kRecord[16..]);

		return true;
	}

	private static int[] EvalQuery(SearchNode kNode, Dictionary<string, int[]> hmapPost, Func<int, string?>? funcText, int[]? arrScope = null)	// 조건 → 맞는 줄 번호 (오름차순): 검색어: 필드·단어 포스팅 교집합 (IP·날짜·시간: 원문 글자 확인, 와일드카드: 후보 줄 원문 확인), AND 교집합, OR 합집합, NOT 차집합 (정렬 배열 병합), arrScope: 이 줄 안에서만 (null: 시간 전체 → 원문 확인 줄 줄임)
	{
		switch (kNode.m_eOp)
		{
			case SEARCH_OP.SEARCH_OP_AND:	// 앞 결과 안에서 뒤 계산 (한쪽만 와일드카드: 다른 쪽 먼저)
			{
				bool		bSwap	= HasWild(kNode.m_kLeft!) && !HasWild(kNode.m_kRight!);	// 왼쪽만 와일드카드
				int[]		arrFirst	= EvalQuery(bSwap ? kNode.m_kRight! : kNode.m_kLeft!, hmapPost, funcText, arrScope);

				return arrFirst.Length == 0 ? arrFirst : And(arrFirst, EvalQuery(bSwap ? kNode.m_kLeft! : kNode.m_kRight!, hmapPost, funcText, arrFirst));
			}

			case SEARCH_OP.SEARCH_OP_OR:
			{
				return Or(EvalQuery(kNode.m_kLeft!, hmapPost, funcText, arrScope), EvalQuery(kNode.m_kRight!, hmapPost, funcText, arrScope));
			}

			case SEARCH_OP.SEARCH_OP_NOT:	// 왼쪽 결과 안에서 오른쪽 계산
			{
				int[] arrLeft = EvalQuery(kNode.m_kLeft!, hmapPost, funcText, arrScope);

				return arrLeft.Length == 0 ? arrLeft : Not(arrLeft, EvalQuery(kNode.m_kRight!, hmapPost, funcText, arrLeft));
			}

			default:	// 검색어: * → 모든 줄, 필드:값 → 필드 포스팅, 와일드카드 → 후보 줄 원문 확인, 그 외 → 단어 포스팅 교집합 (짧은 것부터)
			{
				if (IsAll(kNode))
				{
					return arrScope ?? hmapPost.GetValueOrDefault("*") ?? Array.Empty<int>();
				}

				if (TryGetFieldKey(kNode.m_sTerm, out string sFieldKey))
				{
					int[] arrField = hmapPost.GetValueOrDefault(sFieldKey) ?? Array.Empty<int>();	// 필드 포스팅

					return arrScope == null ? arrField : And(arrField, arrScope);
				}

				if (TryGetWild(kNode.m_sTerm, out string sCore, out bool bHead, out bool bTail))	// 와일드카드 (ab*·*ab·*ab*): 후보 (앞 조건 결과, 없음: 시간 전체 줄) 원문 확인
				{
					int[] arrCand = arrScope ?? hmapPost.GetValueOrDefault("*") ?? Array.Empty<int>();	// 후보 줄

					return funcText == null ? Array.Empty<int>() : arrCand.Where(nDoc => funcText(nDoc) is string sText && IsWildMatch(sText, sCore, bHead, bTail)).ToArray();
				}

				List<int[]> listWord = SplitTerm(kNode.m_sTerm).Select(sWord => hmapPost.GetValueOrDefault(sWord) ?? Array.Empty<int>()).OrderBy(arrWord => arrWord.Length).ToList();	// 단어 포스팅 (짧은 순)

				if (listWord.Count == 0)	// 단어 없는 검색어 (기호뿐)
				{
					return Array.Empty<int>();
				}

				int[] arrHit = listWord[0];	// 맞는 줄

				for (int nIndex = 1; nIndex < listWord.Count && arrHit.Length > 0; ++nIndex)
				{
					arrHit = And(arrHit, listWord[nIndex]);
				}

				if (arrScope != null)	// 앞 조건 안 (원문 확인 줄 줄임)
				{
					arrHit = And(arrHit, arrScope);
				}

				if (funcText != null && IsExactTerm(kNode.m_sTerm))	// IP·날짜·시간: 후보 중 원문에 그 글자 그대로 있는 줄만
				{
					string sExact = kNode.m_sTerm.Trim();	// 찾을 글자

					arrHit = arrHit.Where(nDoc => funcText(nDoc)?.Contains(sExact, StringComparison.OrdinalIgnoreCase) == true).ToArray();
				}

				return arrHit;
			}
		}
	}

	private static int[] And(int[] arrLeft, int[] arrRight)	// 교집합 (두 정렬 배열을 앞에서부터 같이 훑음)
	{
		int[]	arrHit	= new int[Math.Min(arrLeft.Length, arrRight.Length)];
		int		nHit	= 0;

		for (int nLeft = 0, nRight = 0; nLeft < arrLeft.Length && nRight < arrRight.Length;)
		{
			if (arrLeft[nLeft] < arrRight[nRight])
			{
				++nLeft;
			}
			else if (arrLeft[nLeft] > arrRight[nRight])
			{
				++nRight;
			}
			else
			{
				arrHit[nHit++] = arrLeft[nLeft++];

				++nRight;
			}
		}

		Array.Resize(ref arrHit, nHit);

		return arrHit;
	}

	private static int[] Or(int[] arrLeft, int[] arrRight)	// 합집합 (같은 줄은 1번)
	{
		int[]	arrHit	= new int[arrLeft.Length + arrRight.Length];
		int		nHit	= 0;
		int		nLeft	= 0;
		int		nRight	= 0;

		while (nLeft < arrLeft.Length && nRight < arrRight.Length)
		{
			if (arrLeft[nLeft] < arrRight[nRight])
			{
				arrHit[nHit++] = arrLeft[nLeft++];
			}
			else if (arrLeft[nLeft] > arrRight[nRight])
			{
				arrHit[nHit++] = arrRight[nRight++];
			}
			else
			{
				arrHit[nHit++] = arrLeft[nLeft++];

				++nRight;
			}
		}

		while (nLeft < arrLeft.Length)
		{
			arrHit[nHit++] = arrLeft[nLeft++];
		}

		while (nRight < arrRight.Length)
		{
			arrHit[nHit++] = arrRight[nRight++];
		}

		Array.Resize(ref arrHit, nHit);

		return arrHit;
	}

	private static int[] Not(int[] arrLeft, int[] arrRight)	// 차집합 (왼쪽에서 오른쪽에 있는 줄 뺌)
	{
		int[]	arrHit	= new int[arrLeft.Length];
		int		nHit	= 0;
		int		nRight	= 0;

		foreach (int nDoc in arrLeft)
		{
			while (nRight < arrRight.Length && arrRight[nRight] < nDoc)
			{
				++nRight;
			}

			if (nRight >= arrRight.Length || arrRight[nRight] != nDoc)
			{
				arrHit[nHit++] = nDoc;
			}
		}

		Array.Resize(ref arrHit, nHit);

		return arrHit;
	}

	private static int[] Concat(List<int[]> listPart)	// 배열 이어 붙임 (1개: 그대로)
	{
		if (listPart.Count == 1)
		{
			return listPart[0];
		}

		int[]	arrAll	= new int[listPart.Sum(arrPart => arrPart.Length)];
		int		nPos	= 0;													// 붙일 위치

		foreach (int[] arrPart in listPart)
		{
			Array.Copy(arrPart, 0, arrAll, nPos, arrPart.Length);

			nPos += arrPart.Length;
		}

		return arrAll;
	}

	private static void CollectTerm(SearchNode kNode, HashSet<string> setWord, HashSet<string> setField)	// 조건 트리의 검색어 단어·필드 키 모두 (NOT 오른쪽 포함 → 읽을 포스팅)
	{
		if (kNode.m_eOp == SEARCH_OP.SEARCH_OP_TERM)
		{
			if (TryGetFieldKey(kNode.m_sTerm, out string sFieldKey))
			{
				setField.Add(sFieldKey);
			}
			else if (!IsWild(kNode))	// 와일드카드: 단어 포스팅 안 읽음 (원문 확인)
			{
				setWord.UnionWith(SplitTerm(kNode.m_sTerm));
			}

			return;
		}

		CollectTerm(kNode.m_kLeft!, setWord, setField);
		CollectTerm(kNode.m_kRight!, setWord, setField);
	}

	public static bool IsExactTerm(string sTerm)	// 원문 그대로 확인할 검색어: IP·IP:포트·날짜·시간 (단어로 나누면 흩어진 숫자도 맞음)
	{
		return m_kExactTerm.IsMatch(sTerm.Trim());
	}

	private static bool HasExact(SearchNode kNode)	// 조건 트리에 원문 그대로 확인할 검색어 있음 (NOT 오른쪽 포함)
	{
		return kNode.m_eOp == SEARCH_OP.SEARCH_OP_TERM ? IsExactTerm(kNode.m_sTerm) : HasExact(kNode.m_kLeft!) || HasExact(kNode.m_kRight!);
	}

	private static bool IsWild(SearchNode kNode)	// 검색어가 와일드카드 (앞뒤 *, * 뿐·필드:값 아님)
	{
		return kNode.m_eOp == SEARCH_OP.SEARCH_OP_TERM && !IsAll(kNode) && !TryGetFieldKey(kNode.m_sTerm, out _) && TryGetWild(kNode.m_sTerm, out _, out _, out _);
	}

	private static (bool bScan, bool bNarrow) GetWildScan(SearchNode kNode, bool bScoped)	// 1차 조건 없이 계산되는 와일드카드 (bScan: 있음 → 기간 전체 원문, bNarrow: 결과가 단어·필드로 좁음, bScoped: 앞 조건 결과 있음, EvalQuery 순서와 같음)
	{
		switch (kNode.m_eOp)
		{
			case SEARCH_OP.SEARCH_OP_AND:	// 한쪽만 와일드카드: 다른 쪽 먼저
			{
				bool						bSwap	= HasWild(kNode.m_kLeft!) && !HasWild(kNode.m_kRight!);
				(bool bScan, bool bNarrow)	kFirst	= GetWildScan(bSwap ? kNode.m_kRight! : kNode.m_kLeft!, bScoped);
				(bool bScan, bool bNarrow)	kSecond	= GetWildScan(bSwap ? kNode.m_kLeft! : kNode.m_kRight!, bScoped || kFirst.bNarrow);

				return (kFirst.bScan || kSecond.bScan, kFirst.bNarrow || kSecond.bNarrow);
			}

			case SEARCH_OP.SEARCH_OP_OR:
			{
				(bool bScan, bool bNarrow) kLeft	= GetWildScan(kNode.m_kLeft!, bScoped);
				(bool bScan, bool bNarrow) kRight	= GetWildScan(kNode.m_kRight!, bScoped);

				return (kLeft.bScan || kRight.bScan, kLeft.bNarrow && kRight.bNarrow);
			}

			case SEARCH_OP.SEARCH_OP_NOT:	// 오른쪽: 왼쪽 결과 안
			{
				(bool bScan, bool bNarrow) kLeft	= GetWildScan(kNode.m_kLeft!, bScoped);
				(bool bScan, bool bNarrow) kRight	= GetWildScan(kNode.m_kRight!, bScoped || kLeft.bNarrow);

				return (kLeft.bScan || kRight.bScan, kLeft.bNarrow);
			}

			default:	// * : 안 좁힘, 와일드카드: 앞 조건 있어야 함, 단어·필드: 좁힘
			{
				return IsAll(kNode) ? (false, bScoped) : (IsWild(kNode) ? (!bScoped, bScoped) : (false, true));
			}
		}
	}

	private static bool HasWild(SearchNode kNode)	// 조건 트리에 와일드카드 있음 (NOT 오른쪽 포함)
	{
		return kNode.m_eOp == SEARCH_OP.SEARCH_OP_TERM ? IsWild(kNode) : HasWild(kNode.m_kLeft!) || HasWild(kNode.m_kRight!);
	}

	public static bool IsAll(SearchNode kNode)	// 검색어가 * (전체 로그)
	{
		return kNode.m_eOp == SEARCH_OP.SEARCH_OP_TERM && kNode.m_sTerm.Trim() == "*";
	}

	private static bool HasAll(SearchNode kNode)	// 조건 트리에 * (전체 로그) 있음 (NOT 오른쪽 포함)
	{
		return kNode.m_eOp == SEARCH_OP.SEARCH_OP_TERM ? IsAll(kNode) : HasAll(kNode.m_kLeft!) || HasAll(kNode.m_kRight!);
	}

	private static int GetDocCount(string sHourDir)	// 시간 폴더 줄 수 (doc.bin 크기 / 한 줄 바이트, 없음: 0)
	{
		string sFile = Path.Combine(sHourDir, "doc.bin");	// 줄 목록

		return File.Exists(sFile) ? (int)(new FileInfo(sFile).Length / m_nDocRecord) : 0;
	}

	public static bool TryGetFieldKey(string sTerm, out string sFieldKey)	// 검색어가 필드:값 → 필드 키 "필드\t값" (이름·값 대소문자 무시, 값: 첫 ':' 뒤 전체·딱 맞는 값, 아님: false → 단어)
	{
		int		nColon	= sTerm.IndexOf(':');											// 필드 이름 끝
		string	sName	= nColon > 0 ? GetFieldName(sTerm[..nColon].Trim()) : "";		// 필드 이름 (아님: "")
		string	sValue	= nColon > 0 ? sTerm[(nColon + 1)..].Trim() : "";

		sFieldKey = sName != "" && sValue != "" ? sName + "\t" + sValue : "";

		return sFieldKey != "";
	}

	private static string GetFieldName(string sName)	// 필드 이름 (대소문자 무시) → 등록된 이름 (기본·추가 필드 아님: "")
	{
		foreach ((string sField, _) in GetFieldList())
		{
			if (string.Equals(sField, sName, StringComparison.OrdinalIgnoreCase))
			{
				return sField;
			}
		}

		return "";
	}

	private static List<(string sName, bool bBase)> GetFieldList()	// 필드 목록 (기본 Machine·Folder·Type → 추가 필드 설정 aField 순, bBase: 기본 필드)
	{
		if (!m_bFieldLoaded)	// 처음: field.txt 읽기
		{
			lock (m_kIndexLock)
			{
				LoadFieldName();
			}
		}

		List<(string sName, bool bBase)> listField = m_arrBaseField.Select(sName => (sName, true)).ToList();

		listField.AddRange(m_arrFieldName.Select(sName => (sName, false)));

		return listField;
	}

	private static List<object> ReadPage(QueryResult kResult, int nSkip, int nCount, CancellationToken kToken)	// 최신 순 nSkip 번째부터 nCount 줄: 그 시간 맞는 줄 (없음: 그 시간만 다시 읽음) → 원문 파일 번호·위치로 원문만 읽음 (없음: "(원문 없음)") + 줄 필드
	{
		List<object>							listLine	= new List<object>();										// 이 페이지 줄
		List<(long nHour, int nMs, int nDoc)>	listPick	= new List<(long nHour, int nMs, int nDoc)>(nCount);	// 고른 줄
		int										nPos		= 0;														// 최신부터 센 위치

		for (int nIndex = kResult.m_arrHour.Length - 1; nIndex >= 0 && listPick.Count < nCount; --nIndex)	// 최신 시간부터
		{
			int nHourCount = kResult.m_arrCount[nIndex];	// 이 시간 건수

			if (nPos + nHourCount <= nSkip)	// 이 시간 전부 앞 페이지
			{
				nPos += nHourCount;

				continue;
			}

			long					nHour	= kResult.m_arrHour[nIndex];
			(int nMs, int nDoc)[]	arrHit	= GetHourHit(kResult, nHour, kToken);	// 맞는 줄 (최신 순)

			for (int nHit = Math.Max(0, nSkip - nPos); nHit < arrHit.Length && listPick.Count < nCount; ++nHit)
			{
				listPick.Add((nHour, arrHit[nHit].nMs, arrHit[nHit].nDoc));
			}

			nPos += nHourCount;
		}

		Dictionary<int, FileStream>		hmapStream	= new Dictionary<int, FileStream>();	// 원문 파일 번호 → 연 원문 파일 (페이지 안에서 다시 씀)
		Dictionary<long, FileStream?>	hmapDoc		= new Dictionary<long, FileStream?>();	// 시간 → 연 줄 목록 (줄마다 그 자리만 읽음, 전체를 메모리에 안 올림, 없음: null)

		try
		{
			foreach ((long nHour, int nMs, int nDoc) in listPick)
			{
				Gate.Yield(kToken);

				if (!hmapDoc.TryGetValue(nHour, out FileStream? kDoc))	// 처음 보는 시간: 줄 목록 열기
				{
					hmapDoc[nHour] = kDoc = OpenDocTable(GetHourDir(nHour));
				}

				ReadDocRecord(kDoc, nDoc, out _, out int nFileId, out long nOffset, out int nLength);

				(string sMachine, string sPath, string sFolder, _)				= GetFileInfo(nFileId);
				string?											sText	= ReadOrigin(hmapStream, nFileId, sMachine, sPath, nOffset, nLength, out long nLine);	// 원문 (레코드의 [원문] 안, 받은 줄 그대로), 클라 파일 줄 번호 (예전 레코드: 0)
				DateTime										kTime	= FromHour(nHour).AddMilliseconds(nMs);									// 원문 시간
				(string sKey, string sValue)[]					arrField	= sText == null ? [] : ReadOriginField(hmapStream, nFileId, nOffset + nLength);	// 추가 필드 (원문 뒤, 설정 aField)

				listLine.Add(new { sTime = kTime.ToString("[yyyy/MM/dd HH:mm:ss.fff]", CultureInfo.InvariantCulture), sMachine, sPath, sFolder, sText = sText == null ? "(원문 없음)" : GetLogText(sText), sOrigin = sText ?? "(원문 없음)", nLine, arrField = GetLineField(sMachine, sFolder, GetLineType(sText ?? ""), arrField) });	// 표시: Log 칸·펼친 줄은 원문 그대로 (sOrigin), Line 칸: 클라 파일 줄 번호 (0: 모름)
			}
		}
		finally
		{
			foreach (FileStream kStream in hmapStream.Values)
			{
				kStream.Dispose();
			}

			foreach (FileStream? kDoc in hmapDoc.Values)
			{
				kDoc?.Dispose();
			}
		}

		return listLine;
	}

	private static List<object> GetLineField(string sMachine, string sFolder, string sType, (string sKey, string sValue)[] arrField)	// 줄 필드 [{sName, sValue}] (Machine·Folder·Type·추가 필드 순)
	{
		List<object> listField = new List<object> { new { sName = "Machine", sValue = sMachine }, new { sName = "Folder", sValue = sFolder }, new { sName = "Type", sValue = sType } };

		foreach ((string sKey, string sValue) in arrField)
		{
			listField.Add(new { sName = sKey, sValue });
		}

		return listField;
	}

	private static (int nMs, int nDoc)[] GetHourHit(QueryResult kResult, long nHour, CancellationToken kToken)	// 시간 하나 맞는 줄 최신 순 (결과에 둔 것: 처음 1번만 정렬, 없음: 그 시간만 다시 읽음 (잠금 밖, 동시 읽음: 먼저 넣은 것), 정렬: 잠금)
	{
		if (!kResult.m_hmapHit.TryGetValue(nHour, out (int nMs, int nDoc)[]? arrHit))	// 긴 기간: 이 시간만 다시 읽음
		{
			(int nMsFrom, int nMsTo) = GetMsRange(kResult.m_kFrom, kResult.m_kTo, FromHour(nHour));

			string					sHourDir	= GetHourDir(nHour);	// 시간 폴더
			(int nMs, int nDoc)[]	arrRead		= Directory.Exists(sHourDir) ? ReadHourHit(kResult, sHourDir, nMsFrom, nMsTo, kToken) : Array.Empty<(int nMs, int nDoc)>();

			arrHit = kResult.m_hmapHit.GetOrAdd(nHour, arrRead);

			if (ReferenceEquals(arrHit, arrRead))
			{
				Interlocked.Add(ref kResult.m_nHitCount, arrHit.Length);	// 보관분 늘어남 (상한 넘음: 타이머가 오래된 결과부터 뺌)
			}
		}

		lock (kResult)
		{
			if (kResult.m_setSorted.Add(nHour))	// 처음 보는 시간: 최신 순 정렬
			{
				Array.Sort(arrHit, (kLeft, kRight) => kRight.nMs != kLeft.nMs ? kRight.nMs.CompareTo(kLeft.nMs) : kRight.nDoc.CompareTo(kLeft.nDoc));
			}
		}

		return arrHit;
	}

	private static string? ReadOrigin(Dictionary<int, FileStream> hmapStream, int nFileId, string sMachine, string sPath, long nOffset, int nLength, out long nLine)	// 원문: 레코드 [원문] 안 nOffset 부터 nLength 바이트 (최대 m_nOriginLineMax, 없음·위치 밖: null), nLine: 원문 앞 [줄 번호] (예전 레코드: 0)
	{
		nLine = 0;

		if (!hmapStream.TryGetValue(nFileId, out FileStream? kStream))	// 처음 보는 원문 파일: 열기
		{
			string sFile = GetOriginPath(sMachine, sPath);	// 원문 파일

			if (sFile == "" || !File.Exists(sFile))
			{
				return null;
			}

			hmapStream[nFileId] = kStream = new FileStream(sFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);	// 원문 쓰는 중 허용
		}

		if (nOffset < 0 || nLength < 0 || nOffset + nLength > kStream.Length)	// 위치 밖 (원문 파일 다시 만듦 등)
		{
			return null;
		}

		int		nPrefix		= (int)Math.Min(nOffset, m_nLinePrefix);						// 원문 앞 (레코드 "[줄 번호]:[")
		byte[]	arrBytes	= new byte[nPrefix + Math.Min(m_nOriginLineMax, nLength)];	// 원문 앞 + 원문 (최대)

		kStream.Position = nOffset - nPrefix;
		kStream.ReadExactly(arrBytes);

		nLine = GetRecordLine(arrBytes.AsSpan(0, nPrefix));

		return Encoding.UTF8.GetString(arrBytes, nPrefix, arrBytes.Length - nPrefix);
	}

	private static long GetRecordLine(ReadOnlySpan<byte> kHead)	// 레코드 원문 앞 "…[123]:[" 의 줄 번호 (예전 레코드 "…[Folder]:[": 0)
	{
		if (!kHead.EndsWith("]:["u8))
		{
			return 0;
		}

		ReadOnlySpan<byte>	kBody	= kHead[..^3];								// "…[123"
		ReadOnlySpan<byte>	kDigit	= kBody[(kBody.LastIndexOf((byte)'[') + 1)..];	// "123" ('[' 없음: 전체 → 숫자 아님)

		return kDigit.Length is > 0 and <= 18 && !kDigit.ContainsAnyExceptInRange((byte)'0', (byte)'9') && long.TryParse(kDigit, out long nLine) ? nLine : 0;
	}

	private static (string sKey, string sValue)[] ReadOriginField(Dictionary<int, FileStream> hmapStream, int nFileId, long nEnd)	// 원문 뒤 "]:[Type]{추가 필드 JSON}" 의 추가 필드 (nEnd: 원문 끝, 예전 레코드·없음·m_nOriginTailMax 넘음: 빈 배열)
	{
		if (!hmapStream.TryGetValue(nFileId, out FileStream? kStream) || nEnd < 0 || nEnd >= kStream.Length)
		{
			return [];
		}

		byte[] arrBytes = new byte[(int)Math.Min(m_nOriginTailMax, kStream.Length - nEnd)];	// 원문 뒤 (최대)

		kStream.Position = nEnd;
		kStream.ReadExactly(arrBytes);

		ReadOnlySpan<byte>	kTail		= arrBytes;
		int					nLineEnd	= kTail.IndexOf("\r\n"u8);	// 레코드 끝

		if (nLineEnd < 0 || !kTail.StartsWith("]:["u8))
		{
			return [];
		}

		int nJson = kTail[3..nLineEnd].IndexOf((byte)']') + 4;	// Type [] 뒤 (추가 필드 JSON 시작)

		if (nJson < 4 || nJson >= nLineEnd || arrBytes[nJson] != (byte)'{')	// 추가 필드 없음·예전 레코드
		{
			return [];
		}

		try
		{
			using JsonDocument kDoc = JsonDocument.Parse(arrBytes.AsMemory(nJson, nLineEnd - nJson));

			return kDoc.RootElement.EnumerateObject().Where(kField => kField.Value.ValueKind == JsonValueKind.String).Select(kField => (kField.Name, kField.Value.GetString() ?? "")).ToArray();
		}
		catch (JsonException)	// 형식 오류: 없음
		{
			return [];
		}
	}

	public static async ValueTask EnqueueAsync(LogBatch kBatch, CancellationToken kStop)	// 수신 스레드: Log 1건 → 대상 폴더 해시 담당 워커 라이트 큐 (잠금: Add 순간, 큐·색인 버퍼 가득: 대기 → TCP 역압)
	{
		IngestWorker kWorker = m_arrWorker[(int)((uint)HashCode.Combine(kBatch.sMachine.ToUpperInvariant(), GetBatchFolder(kBatch).ToUpperInvariant()) % (uint)m_arrWorker.Length)];	// 담당 워커 (같은 머신·대상 폴더는 늘 같은 워커 → 원문·받은 진행 (Count) 순서 유지, 시간 파일 바뀜 포함)

		while (!kWorker.TryAdd(kBatch))
		{
			await Task.Delay(m_nIngestWaitMs, kStop);
		}
	}

	public static bool IsIngestPaused()	// 밀림으로 클라 전송 멈춤 중 (HTTP 수신: 거절 → TCP 우선)
	{
		return m_bIngestPaused;
	}

	public static void ReportLoad(int nWorker, int nBacklog)	// 워커 스왑마다: 밀린 줄 m_nIngestQueueWarn 이상·색인 버퍼 m_nIndexBufferMax 이상 (상한 넘침) 마다 (앞 멈춤 뒤 m_nIngestPauseStepMs 지남): 부하 로그 + 전송 중 클라 분당 Log 상위 X% 더 Pause, 멈춤 중 모든 워커 m_nIngestQueueResume 미만·색인 버퍼 절반 미만: 모든 클라에 Resume
	{
		m_arrBacklog[nWorker] = nBacklog;

		long nBuffer = Volatile.Read(ref m_nIndexBufferLine);	// 색인 버퍼 줄 수 (세그먼트 못 쓴 줄)

		if (nBacklog < m_nIngestQueueWarn && nBuffer < m_nIndexBufferMax && !m_bIngestPaused)	// 평소: 잠금 없이 끝
		{
			return;
		}

		lock (m_kLoadLock)
		{
			bool bLoad = nBacklog >= m_nIngestQueueWarn || nBuffer >= m_nIndexBufferMax;	// 상한 넘침 (라이트 큐·색인 버퍼)

			if (bLoad && (!m_bIngestPaused || Environment.TickCount64 - m_nIngestPauseTick >= m_nIngestPauseStepMs))	// 넘칠 때마다 (앞 멈춤 뒤 X ms 지남): 전송 중 클라 X% 더 멈춤
			{
				m_bIngestPaused		= true;
				m_nIngestPauseTick	= Environment.TickCount64;

				string sLoad = nBacklog >= m_nIngestQueueWarn ? $"워커 {nWorker} 라이트 큐 {nBacklog:N0}줄" : $"색인 버퍼 {nBuffer:N0}줄 (최대 {m_nIndexBufferMax:N0}줄, 세그먼트 쓰기 밀림)";	// 밀린 곳

				(int nPause, int nSending, int nTotal) = TCPServer.PauseTop(m_nIngestPausePercent);	// 전송 중 클라 분당 Log 줄 많은 순 상위 X% (남은 1대: 그것도)

				Log.Write(WARN_, $"적재 부하: {sLoad} 밀림 → 전송 중 클라 분당 Log 상위 {m_nIngestPausePercent}% 멈춤 요청 (Pause {nPause}대, 남은 전송 {nSending - nPause}대 / 접속 {nTotal}대)");
			}
			else if (m_bIngestPaused && Environment.TickCount64 - m_nIngestPauseTick >= m_nIngestPauseMinMs && nBuffer < m_nIndexBufferMax / 2 && m_arrBacklog.All(nQueue => nQueue < m_nIngestQueueResume))
			{
				m_bIngestPaused = false;

				Log.Write(NOTI_, $"적재 안정: 모든 워커 라이트 큐 {m_nIngestQueueResume:N0}줄 미만·색인 버퍼 {nBuffer:N0}줄 → 모든 클라 전송 재개 요청 (Resume, 접속 {TCPServer.ResumeAll()}대, 클라: 멈춘 상태만 재개)");
			}
		}
	}

	public static void SaveOrigin(IngestWorker kWorker, LogBatch kBatch)	// 로직 스레드: 받은 진행 (Count) 이후 줄만 → 원문 레코드 추가 (형식: Server.cs 머리, 1건 = 쓰기 1번, 줄마다 문자열·JSON 객체 안 만듦) + 시간별 색인 버퍼 (타이머가 m_nFlushSec 초마다 가져감) → 받은 진행 저장, 머신·경로 오류·실패: 출력 후 버림
	{
		string sFile = "";	// 원문 파일

		try
		{
			sFile = GetOriginPath(kBatch.sMachine, kBatch.sPath);

			if (sFile == "")
			{
				Log.Write(WARN_, $"원문 저장 거부: [{kBatch.sMachine}] {kBatch.sPath}");

				return;
			}

			string		sFolder		= GetBatchFolder(kBatch);																	// 대상 폴더 (예전 클라·이상한 값: 클라 경로에서)
			string		sZone		= kBatch.sZone.Trim().Length > m_nFieldValueMax ? "" : kBatch.sZone.Trim();				// 예전 클라 Base:sZone (원문 파일 번호 구분만, 필드 아님), 이상한 값: 없음
			Count?	kCount		= kBatch.bCount ? kWorker.GetCount(kBatch.sMachine, sFolder) : null;					// 머신·대상 폴더 받은 진행 (Data\Count, 클라 Count 목록 폴더만)
			string		sRel		= Path.GetRelativePath(sFolder, kBatch.sPath);										// 날짜 폴더\파일
			long		nDateHour	= kCount == null || kBatch.listNo == null ? -1 : Count.GetDateHour(sRel);			// 파일 날짜·시간 (관리 안 함·예전 클라·이름 규칙 다름: -1 → 확인 안 함)

			(List<string> listLine, List<long>? listNo, List<(string sKey, string sValue)[]>? listField) = SkipReceived(kBatch, kCount, nDateHour);	// 받은 진행 이하 줄 뺌 (클라 재전송)

			if (listLine.Count == 0)	// 모두 받은 줄
			{
				return;
			}

			List<OriginRecord>	listRecord	= kWorker.m_listRecord;						// 줄마다 레코드 (워커가 둔 목록 재사용)
			FileStream			kStream		= kWorker.GetOriginStream(sFile);			// 열어 둔 원문 파일 (끝에 이어 씀)
			MemoryStream		kBuffer		= kWorker.m_kWriteBuffer;					// 1건 전체를 모아 쓰기 1번
			Utf8JsonWriter		kJson		= kWorker.m_kJson;							// 추가 필드 JSON 쓰기 (버퍼 끝에 바로)
			long				nBase		= kStream.Position;							// 이번 건 시작 위치

			listRecord.Clear();
			kBuffer.SetLength(0);
			kJson.Reset(kBuffer);

			if (listField != null)	// 추가 필드 이름 (처음 받은 표기, 있음: 잠금 없이, 새 이름: 잠금 안에서 field.txt)
			{
				foreach ((string sKey, string sValue)[] arrLineField in listField)
				{
					for (int nIndex = 0; nIndex < arrLineField.Length; ++nIndex)
					{
						arrLineField[nIndex].sKey = RegisterFieldName(arrLineField[nIndex].sKey);
					}
				}
			}

			for (int nIndex = 0; nIndex < listLine.Count; ++nIndex)
			{
				string							sLine		= listLine[nIndex];																											// 받은 줄
				string							sTime		= kBatch.sRecvTime != "" ? kBatch.sRecvTime : (IsLogTime(sLine) ? sLine.Substring(0, 25) : GetNow().ToString("[yyyy/MM/dd HH:mm:ss.fff]", CultureInfo.InvariantCulture));	// 원문 시간 (HTTP: 받은 시각, 없음: 지금 KST)
				string							sType		= kWorker.InternType(GetLineType(sLine.AsSpan()));																			// Type (같은 글자: 워커가 둔 문자열 재사용)
				(string sKey, string sValue)[]	arrField	= listField == null ? [] : listField[nIndex];

				WriteUtf8(kBuffer, sTime);	// [Line]: 원문 바로 앞 (쿼리 때 원문 앞 몇 바이트만 읽어 줄 번호), 조각마다 바로 씀
				WriteUtf8(kBuffer, "[");
				WriteUtf8(kBuffer, kBatch.sMachine);
				WriteUtf8(kBuffer, "][");
				WriteUtf8(kBuffer, sFolder);
				WriteUtf8(kBuffer, "]");

				if (listNo != null)
				{
					WriteUtf8(kBuffer, "[");
					WriteNumber(kBuffer, listNo[nIndex]);
					WriteUtf8(kBuffer, "]");
				}

				WriteUtf8(kBuffer, ":[");

				long	nOffset	= nBase + kBuffer.Length;		// 원문 위치 (파일 기준)
				int		nLength	= WriteUtf8(kBuffer, sLine);	// 원문 그대로 (시간·Type 포함)

				listRecord.Add(new OriginRecord(sTime, sLine, nOffset, nLength, sType, arrField));

				WriteUtf8(kBuffer, "]:[");
				WriteUtf8(kBuffer, sType);
				WriteUtf8(kBuffer, "]");

				if (arrField.Length > 0)	// 추가 필드: JSON {키: 값} (쿼리 때 원문 뒤만 읽어 표시, 이스케이프: JsonObject.ToJsonString 과 같은 기본)
				{
					kJson.Reset();
					kJson.WriteStartObject();

					foreach ((string sKey, string sValue) in arrField)
					{
						kJson.WriteString(sKey, sValue);
					}

					kJson.WriteEndObject();
					kJson.Flush();
				}

				WriteUtf8(kBuffer, "\r\n");
			}

			kStream.Write(kBuffer.GetBuffer(), 0, (int)kBuffer.Length);
			kStream.Flush();	// 세그먼트가 가리키기 전에 파일에 있어야 함 (쿼리 ReadOrigin 은 따로 열어 읽음)

			int nFileId = GetFileId(kBatch.sMachine, kBatch.sPath, sFolder, sZone);	// 원문 파일 번호 (있음: 잠금 없이)

			lock (kWorker.m_kBufferLock)	// 타이머 Flush 가 버퍼를 통째로 바꿔 갈 때만 겹침
			{
				AddIndex(kWorker, nFileId, kBatch.sMachine, sFolder, listRecord);

				Interlocked.Add(ref m_nIndexBufferLine, listRecord.Count);	// 색인 버퍼 줄 수 (Flush 가 세그먼트 쓴 뒤 뺌)
			}

			if (kCount != null && nDateHour >= 0 && listNo != null && kCount.IsNewer(nDateHour, listNo.Max()))	// 받은 진행 갱신 (원문 쓴 뒤, 없음: 처음 받은 값)
			{
				kCount.Save(sRel, listNo.Max());
			}
		}
		catch (Exception kException)	// 경로 형식 오류·쓰기 실패
		{
			kWorker.CloseOriginStream(sFile);	// 다음 건에 다시 엶

			Log.Write(WARN_, $"원문 저장 실패: [{kBatch.sMachine}] {(sFile == "" ? kBatch.sPath : sFile)} ({kException.Message})");
		}
		finally
		{
			kWorker.ReleaseBuffer();	// 받은 줄 문자열·큰 건 버퍼 놓음
		}
	}

	public static void PruneCount(string sMachine, IEnumerable<string> listFolder)	// 클라 관리 목록 밖 이 머신 받은 진행: 모든 워커 라이트 큐에 정리 표시 (앞서 받은 Log 처리 뒤 메모리에서 뺌 → 처리 중 다시 생겨 남는 일 없음)
	{
		HashSet<string> setFolder = listFolder.ToHashSet(StringComparer.OrdinalIgnoreCase);

		foreach (IngestWorker kWorker in m_arrWorker)
		{
			kWorker.AddPrune(sMachine, setFolder);
		}
	}

	private static string GetBatchFolder(LogBatch kBatch)	// 대상 폴더 (클라 sFolder, 예전 클라·이상한 값: 클라 경로에서)
	{
		return kBatch.sFolder == "" || kBatch.sFolder.Length > 1000 ? GetFolder(kBatch.sPath) : kBatch.sFolder;
	}

	private static (List<string> listLine, List<long>? listNo, List<(string sKey, string sValue)[]>? listField) SkipReceived(LogBatch kBatch, Count? kCount, long nDateHour)	// 받은 진행 (파일 날짜·시간, 줄 번호) 이하 줄 뺌 (관리 안 함·예전 클라·이름 규칙 다름 (nDateHour -1)·줄 번호 없음 (0): 그대로, 뺌: 출력)
	{
		List<long>? listNo = kBatch.listNo;

		if (kCount == null || nDateHour < 0 || listNo == null)	// 확인 안 함
		{
			return (kBatch.listLine, listNo, kBatch.listField);
		}

		Count kHave = kCount;	// 받은 진행

		if (listNo.All(nNo => nNo <= 0 || kHave.IsNewer(nDateHour, nNo)))	// 받은 줄 없음
		{
			return (kBatch.listLine, listNo, kBatch.listField);
		}

		List<string>							listLine	= new List<string>();
		List<long>								listKeepNo	= new List<long>();
		List<(string sKey, string sValue)[]>?	listField	= kBatch.listField == null ? null : new List<(string sKey, string sValue)[]>();

		for (int nIndex = 0; nIndex < listNo.Count; ++nIndex)
		{
			if (listNo[nIndex] > 0 && !kCount.IsNewer(nDateHour, listNo[nIndex]))	// 받은 줄
			{
				continue;
			}

			listLine.Add(kBatch.listLine[nIndex]);
			listKeepNo.Add(listNo[nIndex]);
			listField?.Add(kBatch.listField![nIndex]);
		}

		Log.Write(INFO_, $"이미 받은 줄 뺌: [{kBatch.sMachine}] {kBatch.sPath} ({listNo.Count - listLine.Count}줄, 받은 진행 {kCount.m_sRel} {kCount.m_nLine}줄)");

		return (listLine, listKeepNo, listField);
	}

	private static int WriteUtf8(MemoryStream kBuffer, ReadOnlySpan<char> kText)	// 글자 → UTF-8 로 버퍼 끝에 (반환: 쓴 바이트)
	{
		int nLength = Encoding.UTF8.GetByteCount(kText);

		kBuffer.SetLength(kBuffer.Length + nLength);

		Encoding.UTF8.GetBytes(kText, kBuffer.GetBuffer().AsSpan((int)kBuffer.Length - nLength, nLength));
		kBuffer.Position = kBuffer.Length;

		return nLength;
	}

	private static void WriteNumber(MemoryStream kBuffer, long nValue)	// 숫자 → 10진 UTF-8 로 버퍼 끝에 (문자열 안 만듦)
	{
		Span<byte> kDigit = stackalloc byte[24];	// 자릿수 (부호 + 19)

		nValue.TryFormat(kDigit, out int nLength, default, CultureInfo.InvariantCulture);
		kBuffer.Write(kDigit[..nLength]);
	}

	private static void AddIndex(IngestWorker kWorker, int nFileId, string sMachine, string sFolder, List<OriginRecord> listRecord)	// 워커 색인 버퍼 (워커 버퍼 잠금 안): 레코드 → 원문 시간 (yyyyMMddHH) 버퍼에 줄 추가 + 단어 해시 (앞 시간 뺀 원문, 문자열 안 만듦)·필드 (값 있음) 마다 줄 번호 (같은 단어: 줄마다 1번)
	{
		Dictionary<long, IndexBuffer>	hmapBuffer	= kWorker.m_hmapBuffer;
		AlarmMatcher?					kMatcher	= Alarm.m_kMatcher;														// 알람 매칭 스냅샷 (없음: 매칭 안 함)
		AlarmCount?						kCount		= kMatcher == null ? null : kWorker.GetAlarmCount(kMatcher);	// 이 워커 알람 카운터·마지막 줄 (수신 시각 분)
		HashSet<ulong>					setHash		= kWorker.m_setHash;														// 줄 단어 해시 (줄마다 비움, 재사용)

		foreach (OriginRecord kRecord in listRecord)
		{
			(long nHour, int nMs) = GetLineTime(kRecord.sTime);	// 원문 시간·시간 안 ms

			if (!hmapBuffer.TryGetValue(nHour, out IndexBuffer? kBuffer))	// 처음 보는 시간: 버퍼 새로
			{
				hmapBuffer[nHour] = kBuffer = new IndexBuffer();
			}

			int nDoc = kBuffer.m_listDoc.Count;	// 버퍼 안 줄 번호

			kBuffer.m_listDoc.Add((nMs, nFileId, kRecord.nOffset, kRecord.nLength));

			ReadOnlySpan<char> kBody = GetLogText(kRecord.sText.AsSpan());	// 앞 시간 뺀 원문 (색인·알람 정확 일치)

			HashTerm(kBody, setHash, kWorker.m_arrLower);	// 색인 단어 해시 (앞뒤 '-'·'_' 뗀 형태·가운데 '-'·'_' 조각도, 알람 매칭도)

			foreach (ulong nHash in setHash)
			{
				kBuffer.AddWord(nHash, nDoc);
			}

			kBuffer.AddField("Machine", sMachine, nDoc);
			kBuffer.AddField("Folder", sFolder, nDoc);
			kBuffer.AddField("Type", kRecord.sType, nDoc);

			foreach ((string sKey, string sValue) in kRecord.arrField)
			{
				kBuffer.AddField(sKey, sValue, nDoc);
			}

			kMatcher?.Match(setHash, kMatcher.m_bField ? GetLineField(sMachine, sFolder, kRecord) : null, kBody, kCount!, new AlarmHit(kRecord, nFileId, sMachine, sFolder));
		}
	}

	private static HashSet<string> GetLineField(string sMachine, string sFolder, OriginRecord kRecord)	// 알람 매칭용 줄 필드 키 "필드\t값" (색인 AddField 와 같은 값만, 대소문자 무시)
	{
		HashSet<string> setField = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach ((string sName, string sValue) in kRecord.arrField.Prepend(("Type", kRecord.sType)).Prepend(("Folder", sFolder)).Prepend(("Machine", sMachine)))
		{
			if (sValue != "" && sValue.Length <= m_nFieldValueMax)
			{
				setField.Add(sName + "\t" + sValue);
			}
		}

		return setField;
	}

	private static ReadOnlySpan<char> GetLineType(ReadOnlySpan<char> kText)	// 원문 (받은 줄 그대로) 의 Type (앞 시간 뒤 첫 [] 안, 없음: 빈 것, 문자열 안 만듦)
	{
		ReadOnlySpan<char>	kBody		= GetLogText(kText);								// 앞 시간 뺀 내용 (시간 없음: 그대로)
		int					nTypeEnd	= kBody.StartsWith('[') ? kBody.IndexOf(']') : -1;	// Type [] 끝

		return nTypeEnd > 0 ? kBody[1..nTypeEnd].Trim() : ReadOnlySpan<char>.Empty;
	}

	private static string GetLineType(string sText)	// 원문의 Type 문자열 (쿼리 표시용)
	{
		return GetLineType(sText.AsSpan()).ToString();
	}

	public static (string sKey, string sValue)[] ReadLineField(JsonElement kField)	// 받은 줄 추가 필드 {키: 값} → [(키, 값)] (키: 설정 aField 키·형식·m_nFieldKeyMax 자 이하·기본 필드·중복 아님, 값: 있음·m_nFieldValueMax 자 이하·고정값 정의: 같은 값, 최대 m_nLineFieldMax 개, 아님: 뺌 → 예전 클라 자기 aField·다른 고정값: 버림)
	{
		List<(string sKey, string sValue)> listField = new List<(string sKey, string sValue)>();	// 받은 필드

		if (kField.ValueKind != JsonValueKind.Object)
		{
			return [];
		}

		foreach (JsonProperty kProperty in kField.EnumerateObject())
		{
			string sKey		= kProperty.Name.Trim();
			string sValue	= kProperty.Value.ValueKind == JsonValueKind.String ? (kProperty.Value.GetString() ?? "").Trim() : "";

			if (listField.Count >= m_nLineFieldMax)
			{
				break;
			}

			if (sValue == "" || sValue.Length > m_nFieldValueMax || sKey.Length > m_nFieldKeyMax || !m_kFieldKey.IsMatch(sKey) || m_arrBaseField.Contains(sKey, StringComparer.OrdinalIgnoreCase) || CheckFieldValue(sKey, sValue) != "" || listField.Any(kItem => string.Equals(kItem.sKey, sKey, StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}

			listField.Add((sKey, sValue));
		}

		return listField.ToArray();
	}

	private static string RegisterFieldName(string sKey)	// 받은 추가 필드 이름 → 처음 받은 표기 (있음: 잠금 없이, 처음 받음·아직 안 읽음: 잠금 안에서 읽고 등록)
	{
		if (m_bFieldLoaded && m_hmapFieldName.TryGetValue(sKey, out string? sName))
		{
			return sName;
		}

		lock (m_kIndexLock)
		{
			LoadFieldName();

			return AddFieldName(sKey);
		}
	}

	private static void CheckIndexReset()	// 타이머 (X초마다): 색인 폴더 지움 (file.txt·field.txt 없음) → 원문 파일 번호·추가 필드 이름 새로 (적재 건마다 파일 확인 안 함: 잠금·파일 확인 비용, 지운 뒤 X초 안 줄: 옛 번호·이름) + 원문 파일 번호 최근 목록 세대 바꿈
	{
		lock (m_kIndexLock)
		{
			if (m_bFileLoaded && m_listFileOffset.Count > 0 && !File.Exists(Path.Combine(GetIndexDir(), "file.txt")))	// 번호 새로
			{
				m_listFileOffset.Clear();
				m_listFileOffset.TrimExcess();
				m_hmapMachine.Clear();

				m_hmapFileId		= new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
				m_hmapFileIdOld		= new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
				m_hmapFileInfo		= new ConcurrentDictionary<int, (string sMachine, string sPath, string sFolder, string sZone)>();
				m_hmapFileInfoOld	= new ConcurrentDictionary<int, (string sMachine, string sPath, string sFolder, string sZone)>();
			}

			if (Environment.TickCount64 - m_nFileIdTick >= m_nFileIdKeepHour * 3600 * 1000L)	// 원문 파일 번호 세대 바꿈 (X시간 안 쓴 파일: 다음 바꿈 때 메모리에서 빠짐)
			{
				m_nFileIdTick	= Environment.TickCount64;
				m_hmapFileIdOld	= m_hmapFileId;
				m_hmapFileId	= new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			}

			if (m_bFieldLoaded && m_arrFieldName.Length > 0 && !File.Exists(Path.Combine(GetIndexDir(), "field.txt")))	// 이름 새로
			{
				m_hmapFieldName.Clear();

				m_arrFieldName = Array.Empty<string>();
			}
		}
	}

	private static void LoadFieldName()	// Data\Index\field.txt → 추가 필드 이름 (잠금 안에서, 처음 한 번, 한 줄에 1개, 다 읽은 뒤 m_bFieldLoaded → 잠금 없는 찾기는 그 뒤만)
	{
		string sFile = Path.Combine(GetIndexDir(), "field.txt");	// 추가 필드 이름 목록

		if (m_bFieldLoaded)
		{
			return;
		}

		List<string> listName = new List<string>();	// 읽은 이름 (순서대로)

		if (File.Exists(sFile))
		{
			foreach (string sEntry in File.ReadAllLines(sFile))
			{
				string sName = sEntry.Trim();

				if (sName.Length <= m_nFieldKeyMax && m_kFieldKey.IsMatch(sName) && !m_arrBaseField.Contains(sName, StringComparer.OrdinalIgnoreCase) && m_hmapFieldName.TryAdd(sName, sName))
				{
					listName.Add(sName);
				}
			}
		}

		m_arrFieldName	= listName.ToArray();
		m_bFieldLoaded	= true;
	}

	private static string AddFieldName(string sKey)	// 받은 추가 필드 이름 → 처음 받은 표기 (잠금 안에서, 처음 받음: 목록·Data\Index\field.txt 에 추가)
	{
		if (m_hmapFieldName.TryGetValue(sKey, out string? sName))
		{
			return sName;
		}

		string sFile = Path.Combine(GetIndexDir(), "field.txt");	// 추가 필드 이름 목록

		Directory.CreateDirectory(Path.GetDirectoryName(sFile)!);

		File.AppendAllText(sFile, sKey + "\n");

		m_arrFieldName			= [.. m_arrFieldName, sKey];
		m_hmapFieldName[sKey]	= sKey;	// 목록 뒤 (잠금 없는 찾기: 찾은 이름은 목록에도 있음)

		Log.Write(NOTI_, $"쿼리 필드 추가: {sKey}");

		Alarm.m_bFieldChanged = true;	// 알람 필드:값 판단 다시

		return sKey;
	}

	public static void SetFieldOrder(List<string> listKey)	// 설정 aField 키 순서 → 추가 필드 목록 순서 (설정 키: 앞에 그 순서, 나머지: 뒤에 원래 순서, 새 키: 등록, 바뀜: field.txt 다시 씀)
	{
		lock (m_kIndexLock)
		{
			LoadFieldName();	// 색인 폴더 지움: 새로

			List<string> listName = new List<string>();	// 새 순서

			foreach (string sKey in listKey.Take(m_nLineFieldMax))
			{
				if (sKey.Length <= m_nFieldKeyMax && m_kFieldKey.IsMatch(sKey) && !m_arrBaseField.Contains(sKey, StringComparer.OrdinalIgnoreCase))
				{
					string sName = AddFieldName(sKey);	// 처음 받은 표기 (새 키: 등록)

					if (!listName.Contains(sName))
					{
						listName.Add(sName);
					}
				}
			}

			listName.AddRange(m_arrFieldName.Where(sName => !listName.Contains(sName)));

			if (listName.SequenceEqual(m_arrFieldName))
			{
				return;
			}

			File.WriteAllText(Path.Combine(GetIndexDir(), "field.txt"), string.Concat(listName.Select(sName => sName + "\n")));

			m_arrFieldName = listName.ToArray();

			Log.Write(INFO_, $"쿼리 필드 순서: {string.Join(", ", listName)}");
		}
	}

	public static void Tick()	// 타이머 (X초마다, 타이머 스레드): 워커 버퍼 → 세그먼트 파일 (m_nFlushSec 초 지남·m_nFlushDoc 줄 넘은 시간), 색인 폴더 지움 확인, 합침 스레드 깨움 (합치기는 따로: 세그먼트 쓰기 안 밀림)
	{
		if (m_kMergeThread == null && !m_bMergeStop)	// 처음: 합침 스레드 시작
		{
			m_kMergeThread = new Thread(RunMerge) { IsBackground = true, Name = "IndexMerge" };

			m_kMergeThread.Start();
		}

		Flush(false);
		CheckIndexReset();

		lock (m_kCacheLock)	// 쿼리 결과 보관 만료·상한 (새 쿼리 없어도 놓음)
		{
			PruneCache(null);
		}

		m_kMergeSignal.Set();
	}

	private static void RunMerge()	// 합침 스레드: 신호 (또는 X초) 마다 합칠 시간 확인 → 합침 (예외: 출력 후 계속), 멈춤 요청: 하던 합침 끝내고 끝남
	{
		while (!m_bMergeStop)
		{
			m_kMergeSignal.WaitOne(m_nFlushSec * 1000);

			try
			{
				Merge();
			}
			catch (Exception kException)	// 디스크 오류 등
			{
				Log.Write(ERROR, $"색인 합침 스레드 오류: {kException.Message}");
			}
		}
	}

	public static void Close()	// 종료 (클라 수신 끝·타이머 중지 후): 워커 모두 멈춤 → 끝나길 대기 (라이트 큐 처리: 멈춤 요청 뒤 X초 상한·X초마다 로그, 원문·받은 진행 파일 닫음) → 남은 버퍼 모두 세그먼트 (타이머 Flush 진행 중: 그 Flush 끝 대기 → 뗀 버퍼까지) → 합침 스레드 끝 대기 (하던 합침까지)
	{
		foreach (IngestWorker kWorker in m_arrWorker)
		{
			kWorker.RequestStop();
		}

		foreach (IngestWorker kWorker in m_arrWorker)
		{
			kWorker.Join();
		}

		Flush(true);

		m_bMergeStop = true;

		m_kMergeSignal.Set();
		m_kMergeThread?.Join();

		m_kMergeThread = null;
	}

	private static void Flush(bool bAll)	// 워커 버퍼 → 세그먼트 (플러시 잠금: 떼기부터 다 쓸 때까지 → 종료 Flush 는 타이머가 뗀 버퍼 다 쓸 때까지 대기): 버퍼 잠금 안에서 쓸 버퍼만 떼고 (워커는 새 버퍼로), 시간마다 세그먼트 1개 (bAll·색인 버퍼 절반 넘음: 나이 상관없이 모두, 쓴 시간 버퍼: 바로 놓음, 실패: 출력 후 버림 → 원문 남음)
	{
		lock (m_kFlushLock)	// 타이머·종료 Flush 1번씩 (타이머 중지 대기 넘음: 뗀 버퍼 안 쓴 채 프로세스 끝남 → 색인 빠짐 방지)
		{
			Dictionary<long, List<IndexBuffer>>	hmapFlush	= new Dictionary<long, List<IndexBuffer>>();					// 시간 → 쓸 버퍼 (워커 순)
			bool								bFull		= bAll || Volatile.Read(ref m_nIndexBufferLine) >= m_nIndexBufferMax / 2;	// 모두 (종료·세그먼트 쓰기 밀림)

			foreach (IngestWorker kWorker in m_arrWorker)
			{
				lock (kWorker.m_kBufferLock)
				{
					foreach (KeyValuePair<long, IndexBuffer> kPair in kWorker.m_hmapBuffer.Where(kPair => bFull || Environment.TickCount64 - kPair.Value.m_nTick >= m_nFlushSec * 1000L || kPair.Value.m_listDoc.Count >= m_nFlushDoc).ToList())
					{
						if (!hmapFlush.TryGetValue(kPair.Key, out List<IndexBuffer>? listBuffer))
						{
							hmapFlush[kPair.Key] = listBuffer = new List<IndexBuffer>();
						}

						listBuffer.Add(kPair.Value);
						kWorker.m_hmapBuffer.Remove(kPair.Key);
					}
				}
			}

			foreach (long nHour in hmapFlush.Keys.Order().ToList())
			{
				List<IndexBuffer>	listBuffer	= hmapFlush[nHour];
				long				nDoc		= listBuffer.Sum(kBuffer => (long)kBuffer.m_listDoc.Count);	// 이 시간 줄 수

				hmapFlush.Remove(nHour);	// 쓴 시간 버퍼: 이번 Flush 끝까지 잡지 않음 (밀린 버퍼 큼: 메모리)

				try
				{
					WriteSegment(nHour, listBuffer);
				}
				catch (Exception kException)	// 사용 중·권한 없음
				{
					Log.Write(WARN_, $"색인 저장 실패: {nHour} ({kException.Message})");
				}
				finally
				{
					Interlocked.Add(ref m_nIndexBufferLine, -nDoc);	// 쓴·버린 줄 (색인 버퍼 줄 수)

					foreach (IndexBuffer kBuffer in listBuffer)	// 포스팅 아레나 블록 반납
					{
						kBuffer.m_kPool.Release();
					}
				}
			}
		}
	}

	private static void WriteSegment(long nHour, List<IndexBuffer> listBuffer)	// 플러시 잠금 안: 같은 시간 워커 버퍼들 → doc.bin 줄 추가 (줄 번호: 지금 줄 수부터) → 세그먼트 1개 "첫-끝.idx" (버퍼마다 해시 순·키 순 배열 → 버퍼 순 (= 줄 번호 오름차순) k-way 합침 → 포스팅 아레나에서 바로 이어 씀, 단어·값마다 배열 안 만듦) → m_nMergeSeg 개 넘음: 합침 스레드 깨움
	{
		string	sDir		= GetHourDir(nHour);								// 시간 폴더
		string	sDocFile	= Path.Combine(sDir, "doc.bin");					// 줄 목록

		Directory.CreateDirectory(sDir);

		int		nFirst		= File.Exists(sDocFile) ? (int)(new FileInfo(sDocFile).Length / m_nDocRecord) : 0;	// 첫 줄 번호 (지금 줄 수)
		int		nCount		= listBuffer.Sum(kBuffer => kBuffer.m_listDoc.Count);								// 추가할 줄 수
		int		nLast		= nFirst + nCount - 1;																// 끝 줄 번호
		byte[]	arrDoc		= new byte[nCount * m_nDocRecord];													// 추가할 줄 목록
		int[]	arrBase		= new int[listBuffer.Count];														// 버퍼마다 첫 줄 번호 (시간 안)
		int		nPos		= 0;																				// 쓴 줄 수

		for (int nBuffer = 0; nBuffer < listBuffer.Count; ++nBuffer)
		{
			IndexBuffer kBuffer = listBuffer[nBuffer];

			arrBase[nBuffer] = nFirst + nPos;

			for (int nIndex = 0; nIndex < kBuffer.m_listDoc.Count; ++nIndex)
			{
				(int nMs, int nFileId, long nOffset, int nLength) = kBuffer.m_listDoc[nIndex];

				Span<byte> kRecord = arrDoc.AsSpan((nPos + nIndex) * m_nDocRecord, m_nDocRecord);	// 한 줄

				BinaryPrimitives.WriteInt32LittleEndian(kRecord, nMs);
				BinaryPrimitives.WriteInt32LittleEndian(kRecord[4..], nFileId);
				BinaryPrimitives.WriteInt64LittleEndian(kRecord[8..], nOffset);
				BinaryPrimitives.WriteInt32LittleEndian(kRecord[16..], nLength);
			}

			nPos += kBuffer.m_listDoc.Count;
		}

		using (FileStream kStream = new FileStream(sDocFile, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete))	// 쿼리 읽는 중 허용 (세그먼트보다 먼저 써야 세그먼트가 가리키는 줄이 있음)
		{
			kStream.Write(arrDoc);
		}

		SegmentWriter						kWriter		= new SegmentWriter();																							// 세그먼트 파일 (단어 → 필드 순)
		KeyValuePair<ulong, Posting>[][]	arrWord		= listBuffer.Select(kBuffer => SortWord(kBuffer.m_hmapWord)).ToArray();											// 버퍼마다 단어 (해시 순)
		(string sKey, Posting kPost)[][]	arrField	= listBuffer.Select(kBuffer => SortField(kBuffer.m_hmapField)).ToArray();										// 버퍼마다 필드 키 "필드\t값" (키 순, 대소문자 무시)
		int									nBlobOffset	= 0;	// 쓰는 단어·필드 포스팅 시작
		int									nBlobCount	= 0;	// 쓰는 단어·필드 줄 수
		int									nBlobPrev	= 0;	// 앞 줄 번호 (버퍼 조각 이어 차이)

		MergeSorted(arrWord.Select(arrItem => arrItem.Length).ToArray(), (nBuffer, nIndex) => arrWord[nBuffer][nIndex].Key, Comparer<ulong>.Default,
			_ => (nBlobOffset, nBlobCount, nBlobPrev) = (kWriter.Begin(), 0, 0),
			(nBuffer, nIndex) =>
			{
				Posting kPost = arrWord[nBuffer][nIndex].Value;

				nBlobCount	+= kPost.nCount;
				nBlobPrev	= listBuffer[nBuffer].m_kPool.WritePosting(kWriter.m_kBlob, kPost, arrBase[nBuffer], nBlobPrev);
			},
			nHash => kWriter.EndTerm(nHash, nBlobCount, nBlobOffset));

		MergeSorted(arrField.Select(arrItem => arrItem.Length).ToArray(), (nBuffer, nIndex) => arrField[nBuffer][nIndex].sKey, StringComparer.OrdinalIgnoreCase,
			_ => (nBlobOffset, nBlobCount, nBlobPrev) = (kWriter.Begin(), 0, 0),
			(nBuffer, nIndex) =>
			{
				Posting kPost = arrField[nBuffer][nIndex].kPost;

				nBlobCount	+= kPost.nCount;
				nBlobPrev	= listBuffer[nBuffer].m_kPool.WritePosting(kWriter.m_kBlob, kPost, arrBase[nBuffer], nBlobPrev);
			},
			sKey => kWriter.EndField(sKey, nBlobCount, nBlobOffset));

		kWriter.Save(sDir, nFirst, nLast);

		lock (m_kMergeLock)
		{
			m_hmapMerge[nHour] = Environment.TickCount64;
		}

		if (GetSegmentList(sDir).Count > m_nMergeSeg)	// 세그먼트 많음: 합침 스레드 (비슷한 크기끼리)
		{
			m_kMergeSignal.Set();
		}
	}

	private static KeyValuePair<ulong, Posting>[] SortWord(Dictionary<ulong, Posting> hmapWord)	// 버퍼 단어 → 해시 순 배열
	{
		KeyValuePair<ulong, Posting>[] arrWord = hmapWord.ToArray();

		Array.Sort(arrWord, (kLeft, kRight) => kLeft.Key.CompareTo(kRight.Key));

		return arrWord;
	}

	private static (string sKey, Posting kPost)[] SortField(Dictionary<(string sName, string sValue), Posting> hmapField)	// 버퍼 필드 → 키 "필드\t값" 순 배열 (대소문자 무시, 키 문자열: 여기서만 만듦)
	{
		(string sKey, Posting kPost)[] arrField = hmapField.Select(kPair => (kPair.Key.sName + "\t" + kPair.Key.sValue, kPair.Value)).ToArray();

		Array.Sort(arrField, (kLeft, kRight) => string.Compare(kLeft.sKey, kRight.sKey, StringComparison.OrdinalIgnoreCase));

		return arrField;
	}

	private static void MergeSorted<TKey>(int[] arrLength, Func<int, int, TKey> funcKey, IComparer<TKey> kComparer, Action<TKey> funcBegin, Action<int, int> funcPart, Action<TKey> funcEnd)	// k-way 합침 (원천마다 키 순 배열, 키 funcKey(원천, 칸)): 키 작은 순 (같은 키: 원천 순) 으로 funcBegin(첫 원천 키) → 원천마다 funcPart(원천, 칸) → funcEnd(첫 원천 키)
	{
		int[]											arrCursor	= new int[arrLength.Length];	// 원천마다 다음 칸
		PriorityQueue<int, (TKey kKey, int nSource)>	kQueue		= new PriorityQueue<int, (TKey kKey, int nSource)>(Comparer<(TKey kKey, int nSource)>.Create((kLeft, kRight) =>
		{
			int nCompare = kComparer.Compare(kLeft.kKey, kRight.kKey);	// 키 → 원천 순

			return nCompare != 0 ? nCompare : kLeft.nSource.CompareTo(kRight.nSource);
		}));	// 원천 → (다음 키, 원천) 작은 순

		for (int nSource = 0; nSource < arrLength.Length; ++nSource)
		{
			if (arrLength[nSource] > 0)
			{
				kQueue.Enqueue(nSource, (funcKey(nSource, 0), nSource));
			}
		}

		while (kQueue.TryDequeue(out int nSource, out (TKey kKey, int nSource) kTop))
		{
			funcBegin(kTop.kKey);

			while (true)	// 같은 키: 원천 순으로 이어 씀 (한 원천 안 키는 중복 없음 → 다음 칸은 더 큰 키)
			{
				funcPart(nSource, arrCursor[nSource]);

				if (++arrCursor[nSource] < arrLength[nSource])	// 이 원천 다음 칸
				{
					kQueue.Enqueue(nSource, (funcKey(nSource, arrCursor[nSource]), nSource));
				}

				if (!kQueue.TryPeek(out int nNext, out (TKey kKey, int nSource) kNext) || kComparer.Compare(kNext.kKey, kTop.kKey) != 0)
				{
					break;
				}

				kQueue.Dequeue();

				nSource = nNext;
			}

			funcEnd(kTop.kKey);
		}
	}

	class SegmentWriter	// 세그먼트 파일 만들기 (플러시·합침 공통): 단어 (해시 오름차순)·필드 (키 순) 마다 Begin → m_kBlob 에 포스팅 이어 씀 → EndTerm·EndField → Save (.tmp 에 쓰고 .idx 로, 형식: Server.cs 머리, 포스팅: 단어 → 필드 순)
	{
		public readonly MemoryStream	m_kBlob		= new MemoryStream();			// 포스팅 (호출 쪽이 바로 씀)
		private readonly MemoryStream	m_kEntry	= new MemoryStream();			// 단어 사전 (칸 m_nSegEntry 바이트)
		private readonly MemoryStream	m_kField	= new MemoryStream();			// 필드 사전 (칸 머리 m_nSegField 바이트 + 키)
		private readonly int[]			m_arrBucket	= new int[m_nSegBucket + 1];	// 버킷 시작 ([b + 1] 에 건수 → Save 때 누적)
		private int						m_nTerm;									// 단어 수

		public int Begin()	// 포스팅 시작 위치
		{
			return (int)m_kBlob.Length;
		}

		public void EndTerm(ulong nHash, int nCount, int nOffset)	// 단어 칸 (해시·포스팅 위치·건수·바이트, 해시 오름차순으로 불러야 버킷마다 이어짐)
		{
			Span<byte> kEntry = stackalloc byte[m_nSegEntry];	// 칸

			BinaryPrimitives.WriteUInt64LittleEndian(kEntry, nHash);
			BinaryPrimitives.WriteInt32LittleEndian(kEntry[8..], nOffset);
			BinaryPrimitives.WriteInt32LittleEndian(kEntry[12..], nCount);
			BinaryPrimitives.WriteInt32LittleEndian(kEntry[16..], (int)m_kBlob.Length - nOffset);

			m_kEntry.Write(kEntry);

			++m_arrBucket[(int)(nHash >> 56) + 1];
			++m_nTerm;
		}

		public void EndField(string sKey, int nCount, int nOffset)	// 필드 칸 (포스팅 위치·건수·바이트·키 바이트 + 키 "필드\t값" UTF-8)
		{
			byte[]		arrKey	= Encoding.UTF8.GetBytes(sKey);		// 키
			Span<byte>	kHead	= stackalloc byte[m_nSegField];		// 칸 머리

			BinaryPrimitives.WriteInt32LittleEndian(kHead, nOffset);
			BinaryPrimitives.WriteInt32LittleEndian(kHead[4..], nCount);
			BinaryPrimitives.WriteInt32LittleEndian(kHead[8..], (int)m_kBlob.Length - nOffset);
			BinaryPrimitives.WriteUInt16LittleEndian(kHead[12..], (ushort)arrKey.Length);

			m_kField.Write(kHead);
			m_kField.Write(arrKey);
		}

		public void Save(string sDir, int nFirst, int nLast)	// 세그먼트 파일 "첫-끝.idx" (.tmp 에 쓰고 이름 바꿈)
		{
			byte[] arrHeader = new byte[m_nSegHeader];	// 머리

			for (int nBucket = 1; nBucket <= m_nSegBucket; ++nBucket)	// 건수 → 시작 칸 번호 (누적)
			{
				m_arrBucket[nBucket] += m_arrBucket[nBucket - 1];
			}

			BinaryPrimitives.WriteInt32LittleEndian(arrHeader, m_nSegVersion);
			BinaryPrimitives.WriteInt32LittleEndian(arrHeader.AsSpan(4), m_nTerm);
			BinaryPrimitives.WriteInt32LittleEndian(arrHeader.AsSpan(8), (int)m_kField.Length);

			for (int nBucket = 0; nBucket <= m_nSegBucket; ++nBucket)
			{
				BinaryPrimitives.WriteInt32LittleEndian(arrHeader.AsSpan(12 + nBucket * 4), m_arrBucket[nBucket]);
			}

			string sTemp = Path.Combine(sDir, $"{nFirst}-{nLast}.tmp");	// 쓰는 중 파일 (다 쓰고 .idx 로)

			using (FileStream kStream = new FileStream(sTemp, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				kStream.Write(arrHeader);
				kStream.Write(m_kEntry.GetBuffer(), 0, (int)m_kEntry.Length);
				kStream.Write(m_kField.GetBuffer(), 0, (int)m_kField.Length);
				kStream.Write(m_kBlob.GetBuffer(), 0, (int)m_kBlob.Length);
			}

			File.Move(sTemp, Path.ChangeExtension(sTemp, ".idx"), true);
		}
	}

	private static void Merge()	// 합침 스레드: 합칠 시간마다 세그먼트 2개 이상 (m_nMergeSeg 개 넘음·m_nMergeIdleMin 분 지남) → 합침·옛 파일 삭제, 1개뿐·지남: 목록에서 뺌 (시작 때 1번: ScanIndex, 멈춤 요청: 하던 시간까지)
	{
		if (!m_bIndexScanned)	// 시작 때 1번
		{
			m_bIndexScanned = true;

			ScanIndex();
		}

		List<long> listHour;	// 합칠 시간

		lock (m_kMergeLock)
		{
			listHour = m_hmapMerge.Keys.ToList();
		}

		foreach (long nHour in listHour)
		{
			long nTick;	// 마지막 세그먼트 쓴 시각

			lock (m_kMergeLock)
			{
				if (m_bMergeStop || !m_hmapMerge.TryGetValue(nHour, out nTick))
				{
					return;
				}
			}

			string			sDir		= GetHourDir(nHour);												// 시간 폴더
			List<string>	listStale	= new List<string>();												// 옛 파일
			int				nCount		= GetSegmentList(sDir, listStale).Count;							// 세그먼트 수
			bool			bIdle		= Environment.TickCount64 - nTick >= m_nMergeIdleMin * 60 * 1000L;	// 마지막 쓰기 후 지남

			if (listStale.Count > 0)	// 옛 파일 삭제 (세그먼트 읽기·쓰기 잠금, 읽는 중: 다음에 다시)
			{
				DeleteSegments(listStale);
			}

			if (nCount >= 2 && (nCount > m_nMergeSeg || bIdle))
			{
				try
				{
					if (!MergeHour(nHour, bIdle) && bIdle && !MergeHour(nHour, false))	// 지난 시간 모두 합침 상한 넘음: 비슷한 크기끼리 → 그것도 못 함: 마무리 (세그먼트 그대로, 매번 다시 훑지 않음)
					{
						RemoveMerge(nHour, nTick);
					}
				}
				catch (Exception kException)	// 사용 중·권한 없음·형식 오류
				{
					Log.Write(WARN_, $"색인 합침 실패: {nHour} ({kException.Message}{(bIdle ? ", 지난 시간: 목록에서 뺌 → 새 세그먼트·재시작 때 다시" : "")})");

					if (bIdle)	// 지난 시간: 같은 실패 매번 되풀이 방지
					{
						RemoveMerge(nHour, nTick);
					}
				}
			}
			else if (nCount <= 1 && bIdle && listStale.Count == 0)	// 마무리됨 (그새 세그먼트 또 씀: 그대로)
			{
				RemoveMerge(nHour, nTick);
			}
		}
	}

	private static void RemoveMerge(long nHour, long nTick)	// 합칠 시간 목록에서 뺌 (그새 세그먼트 또 씀: 그대로)
	{
		lock (m_kMergeLock)
		{
			if (m_hmapMerge.TryGetValue(nHour, out long nNow) && nNow == nTick)
			{
				m_hmapMerge.Remove(nHour);
			}
		}
	}

	private static void ScanIndex()	// 시작 때 1번 (합침 스레드): 시간 폴더마다 .tmp 삭제, 세그먼트 2개 이상·옛 파일 → 합칠 시간
	{
		string sDir = GetIndexDir();	// 색인 폴더

		if (!Directory.Exists(sDir))
		{
			return;
		}

		foreach (string sHourDir in Directory.EnumerateDirectories(sDir))
		{
			string sName = Path.GetFileName(sHourDir);	// yyyyMMddHH

			if (sName.Length != 10 || !long.TryParse(sName, NumberStyles.None, CultureInfo.InvariantCulture, out long nHour))	// 시간 폴더 아님
			{
				continue;
			}

			foreach (string sTemp in Directory.EnumerateFiles(sHourDir, "*.tmp"))
			{
				try
				{
					File.Delete(sTemp);
				}
				catch (Exception kException) when (kException is IOException or UnauthorizedAccessException)	// 사용 중: 다음 실행 때 다시
				{
				}
			}

			List<string> listStale = new List<string>();	// 옛 파일

			if (GetSegmentList(sHourDir, listStale).Count >= 2 || listStale.Count > 0)
			{
				lock (m_kMergeLock)
				{
					m_hmapMerge[nHour] = 0;
				}
			}
		}
	}

	private static bool MergeHour(long nHour, bool bAll)	// 합침 스레드: 합칠 묶음 (PickMergeGroup) 세그먼트마다 사전·포스팅 바이트 통째로 읽어 (디코드 안 함) 해시 순·키 순 k-way 합침 → 포스팅 조각 바이트 이어 붙여 세그먼트 1개 "첫-끝.idx" (조각 첫 varint 만 앞 조각 마지막 줄 번호 기준, 줄 번호 배열 안 만듦 → 메모리 = 파일 크기 합) → 옛 파일 삭제 (읽는 중: 다음에), 반환: 합침 (묶음 없음·상한 넘음: false)
	{
		string										sDir		= GetHourDir(nHour);											// 시간 폴더
		List<(int nFirst, int nLast, string sFile)>	listSeg		= PickMergeGroup(GetSegmentList(sDir), bAll);					// 합칠 세그먼트 (이어진 묶음, 줄 번호 순)
		long										nBegin		= Stopwatch.GetTimestamp();

		if (listSeg.Count < 2)
		{
			return false;
		}

		byte[][]		arrEntry	= new byte[listSeg.Count][];		// 세그먼트마다 단어 사전 전체 (해시 순)
		byte[][]		arrBlob		= new byte[listSeg.Count][];		// 세그먼트마다 포스팅 전체
		FieldEntry[][]	arrDict		= new FieldEntry[listSeg.Count][];	// 세그먼트마다 필드 사전 (키 순, 대소문자 무시)

		for (int nSeg = 0; nSeg < listSeg.Count; ++nSeg)
		{
			using FileStream kStream = OpenSegment(listSeg[nSeg].sFile);

			SegmentHead kHead = ReadSegmentHead(kStream);

			arrEntry[nSeg]	= new byte[kHead.nTermCount * m_nSegEntry];
			arrDict[nSeg]	= ReadFieldDict(kStream, kHead).ToArray();
			arrBlob[nSeg]	= new byte[kStream.Length - GetPostStart(kHead)];

			Array.Sort(arrDict[nSeg], (kLeft, kRight) => string.Compare(kLeft.sKey, kRight.sKey, StringComparison.OrdinalIgnoreCase));	// 키 순 (예전 세그먼트: 대소문자 구분 순 → 다시)

			kStream.Position = m_nSegHeader;
			kStream.ReadExactly(arrEntry[nSeg]);

			kStream.Position = GetPostStart(kHead);
			kStream.ReadExactly(arrBlob[nSeg]);
		}

		SegmentWriter	kWriter	= new SegmentWriter();	// 세그먼트 파일 (단어 → 필드 순)
		int				nOffset	= 0;					// 쓰는 단어·필드 포스팅 시작
		int				nTotal	= 0;					// 쓰는 단어·필드 줄 수
		int				nPrev	= 0;					// 앞 조각 마지막 줄 번호
		int				nWord	= 0;					// 단어 수 (로그)
		int				nKey	= 0;					// 필드 값 수 (로그)

		MergeSorted(arrEntry.Select(arrItem => arrItem.Length / m_nSegEntry).ToArray(), (nSeg, nIndex) => BinaryPrimitives.ReadUInt64LittleEndian(arrEntry[nSeg].AsSpan(nIndex * m_nSegEntry)), Comparer<ulong>.Default,
			_ => (nOffset, nTotal, nPrev) = (kWriter.Begin(), 0, 0),
			(nSeg, nIndex) =>
			{
				ReadOnlySpan<byte>	kEntry	= arrEntry[nSeg].AsSpan(nIndex * m_nSegEntry, m_nSegEntry);	// 칸: 해시·포스팅 위치·건수·바이트
				int					nCount	= BinaryPrimitives.ReadInt32LittleEndian(kEntry[12..]);

				nTotal	+= nCount;
				nPrev	= CopyPosting(kWriter.m_kBlob, arrBlob[nSeg].AsSpan(BinaryPrimitives.ReadInt32LittleEndian(kEntry[8..]), BinaryPrimitives.ReadInt32LittleEndian(kEntry[16..])), nCount, nPrev);
			},
			nHash =>
			{
				kWriter.EndTerm(nHash, nTotal, nOffset);

				++nWord;
			});

		MergeSorted(arrDict.Select(arrItem => arrItem.Length).ToArray(), (nSeg, nIndex) => arrDict[nSeg][nIndex].sKey, StringComparer.OrdinalIgnoreCase,
			_ => (nOffset, nTotal, nPrev) = (kWriter.Begin(), 0, 0),
			(nSeg, nIndex) =>
			{
				FieldEntry kEntry = arrDict[nSeg][nIndex];

				nTotal	+= kEntry.nCount;
				nPrev	= CopyPosting(kWriter.m_kBlob, arrBlob[nSeg].AsSpan(kEntry.nOffset, kEntry.nBytes), kEntry.nCount, nPrev);
			},
			sKey =>
			{
				kWriter.EndField(sKey, nTotal, nOffset);	// 처음 (오래된) 세그먼트 표기

				++nKey;
			});

		kWriter.Save(sDir, listSeg[0].nFirst, listSeg.Max(kSeg => kSeg.nLast));

		DeleteSegments(listSeg.Select(kSeg => kSeg.sFile));	// 옛 파일 삭제

		Log.Write(DEBUG, $"색인 합침: {nHour} ({listSeg.Count}개 → 1개, {listSeg[0].nFirst}~{listSeg.Max(kSeg => kSeg.nLast)} 줄, {nWord:N0} 단어, {nKey:N0} 필드 값, {arrBlob.Sum(arrItem => (long)arrItem.Length) / 1048576.0:N1} MB, {Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds:N0} ms)");

		return true;
	}

	private static List<(int nFirst, int nLast, string sFile)> PickMergeGroup(List<(int nFirst, int nLast, string sFile)> listSeg, bool bAll)	// 합칠 세그먼트 묶음 (이어진 것만): bAll → 모두 (파일 크기 합 m_nMergeBytesMax 넘음: 빈 목록), 아님 → 끝에서부터 가장 작은 것의 m_nMergeTier 배 이하 (2개 미만: 끝 m_nMergeSeg / 2 개), 묶음 크기 상한 (읽어 두는 메모리): 앞 (오래된) 것부터 뺌
	{
		long[] arrBytes = listSeg.Select(kSeg => new FileInfo(kSeg.sFile).Length).ToArray();	// 파일 크기

		long Bytes(int nFrom)	// nFrom 부터 끝까지 파일 크기 합
		{
			return arrBytes.Skip(nFrom).Sum();
		}

		if (bAll)
		{
			return listSeg.Count >= 2 && Bytes(0) <= m_nMergeBytesMax ? listSeg : new List<(int nFirst, int nLast, string sFile)>();
		}

		int nStart	= listSeg.Count - 1;																	// 묶음 시작 (끝에서부터)
		int nMin	= listSeg[nStart].nLast - listSeg[nStart].nFirst + 1;									// 묶음 안 가장 작은 줄 수

		while (nStart > 0 && listSeg[nStart - 1].nLast - listSeg[nStart - 1].nFirst + 1 <= (long)nMin * m_nMergeTier)	// 앞 것도 비슷한 크기: 묶음에
		{
			--nStart;

			nMin = Math.Min(nMin, listSeg[nStart].nLast - listSeg[nStart].nFirst + 1);
		}

		if (listSeg.Count - nStart < 2)	// 비슷한 크기 없음: 끝 몇 개라도 (세그먼트 수 제한)
		{
			nStart = Math.Max(0, listSeg.Count - Math.Max(2, m_nMergeSeg / 2));
		}

		while (listSeg.Count - nStart > 2 && Bytes(nStart) > m_nMergeBytesMax)	// 묶음 크기 상한 (앞 (오래된) 것부터 뺌, 2개 남김)
		{
			++nStart;
		}

		return Bytes(nStart) > m_nMergeBytesMax ? new List<(int nFirst, int nLast, string sFile)>() : listSeg.GetRange(nStart, listSeg.Count - nStart);	// 2개도 상한 넘음: 안 합침 (세그먼트 그대로)
	}

	public static void WriteVarint(Stream kStream, uint nValue)	// 7비트씩 (작은 값 1바이트, 이어짐 비트 0x80)
	{
		while (nValue >= 0x80)
		{
			kStream.WriteByte((byte)(nValue | 0x80));

			nValue >>= 7;
		}

		kStream.WriteByte((byte)nValue);
	}

	private static uint ReadVarint(ReadOnlySpan<byte> kBytes, ref int nPos)	// varint 1개 (7비트씩, 이어짐 비트 0x80), nPos: 다음 위치
	{
		uint	nValue	= 0;
		int		nShift	= 0;

		while (true)
		{
			byte nByte = kBytes[nPos++];

			nValue |= (uint)(nByte & 0x7F) << nShift;

			if ((nByte & 0x80) == 0)
			{
				return nValue;
			}

			nShift += 7;
		}
	}

	private static int CopyPosting(MemoryStream kBlob, ReadOnlySpan<byte> kPart, int nCount, int nPrev)	// 세그먼트 포스팅 조각 이어 쓰기: 첫 줄 번호만 앞 조각 마지막 줄 번호 (nPrev) 기준 차이로 다시 쓰고 나머지 바이트 그대로 (반환: 이 조각 마지막 줄 번호, 줄 번호 배열 안 만듦)
	{
		int nPos	= 0;								// 읽을 위치
		int nDoc	= (int)ReadVarint(kPart, ref nPos);	// 첫 줄 번호 (조각 안: 0 기준)

		WriteVarint(kBlob, (uint)(nDoc - nPrev));
		kBlob.Write(kPart[nPos..]);

		for (int nIndex = 1; nIndex < nCount; ++nIndex)	// 마지막 줄 번호 (차이 더함)
		{
			nDoc += (int)ReadVarint(kPart, ref nPos);
		}

		return nDoc;
	}

	private static int[] DecodePosting(ReadOnlySpan<byte> kBytes, int nCount)	// 포스팅 (줄 번호 차이 varint) → 줄 번호 오름차순
	{
		int[]	arrDoc	= new int[nCount];	// 줄 번호
		int		nPos	= 0;				// 읽을 위치
		int		nDoc	= 0;				// 앞 줄 번호

		for (int nIndex = 0; nIndex < nCount; ++nIndex)
		{
			uint	nValue	= 0;	// 차이
			int		nShift	= 0;

			while (true)
			{
				byte nByte = kBytes[nPos++];

				nValue |= (uint)(nByte & 0x7F) << nShift;

				if ((nByte & 0x80) == 0)
				{
					break;
				}

				nShift += 7;
			}

			nDoc			+= (int)nValue;
			arrDoc[nIndex]	= nDoc;
		}

		return arrDoc;
	}

	public static ulong GetHash(ReadOnlySpan<char> kWord)	// 단어 (소문자) → 64비트 해시 (FNV-1a, 세그먼트 단어 사전 키: 단어 글자는 안 둠, 상위 바이트 = 버킷, 색인 HashTerm·쿼리·알람 같은 값)
	{
		ulong nHash = 14695981039346656037;	// FNV-1a 64

		foreach (char cChar in kWord)
		{
			nHash = (nHash ^ cChar) * 1099511628211UL;
		}

		return nHash;
	}

	private static (long nHour, int nMs) GetLineTime(string sTime)	// 파일 쓰는 시간 [yyyy/MM/dd HH:mm:ss.fff] → 원문 시간 (yyyyMMddHH)·시간 안 ms (분·초·ms, 문자열 안 만듦)
	{
		long	nHour	= int.Parse(sTime.AsSpan(1, 4), CultureInfo.InvariantCulture) * 1000000L + int.Parse(sTime.AsSpan(6, 2), CultureInfo.InvariantCulture) * 10000 + int.Parse(sTime.AsSpan(9, 2), CultureInfo.InvariantCulture) * 100 + int.Parse(sTime.AsSpan(12, 2), CultureInfo.InvariantCulture);	// 시간 폴더 (yyyyMMddHH)
		int		nMs		= int.Parse(sTime.AsSpan(15, 2), CultureInfo.InvariantCulture) * 60000 + int.Parse(sTime.AsSpan(18, 2), CultureInfo.InvariantCulture) * 1000 + int.Parse(sTime.AsSpan(21, 3), CultureInfo.InvariantCulture);	// 시간 안 ms

		return (nHour, nMs);
	}

	private static (int nMsFrom, int nMsTo) GetMsRange(DateTime kFrom, DateTime kTo, DateTime kHour)	// 기간이 이 시간 (정각) 안에서 차지하는 ms (시작 포함·끝 미포함, 시간 전체: 0 ~ 1시간)
	{
		return ((int)Math.Max(0, (kFrom - kHour).TotalMilliseconds), (int)Math.Min(m_nHourMs, (kTo - kHour).TotalMilliseconds));
	}

	private static long ToHour(DateTime kTime)	// 시각 → yyyyMMddHH
	{
		return long.Parse(kTime.ToString("yyyyMMddHH", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
	}

	private static DateTime FromHour(long nHour)	// yyyyMMddHH → 시각 (정각)
	{
		return DateTime.ParseExact(nHour.ToString(CultureInfo.InvariantCulture), "yyyyMMddHH", CultureInfo.InvariantCulture);
	}

	private static string GetIndexDir()	// 색인 폴더 (기준 폴더 하위 Data\Index)
	{
		return Path.Combine(GetBaseDir(), "Data", "Index");
	}

	private static string GetHourDir(long nHour)	// 시간 폴더 (색인 폴더 하위 yyyyMMddHH)
	{
		return Path.Combine(GetIndexDir(), nHour.ToString(CultureInfo.InvariantCulture));
	}

	private static string GetFolder(string sPath)	// Folder 못 받음 (예전 클라): 클라 경로에서 (aLogFolder\yyyyMMdd\파일 → aLogFolder, 날짜 폴더 아님: 파일 폴더)
	{
		string sDir		= Path.GetDirectoryName(sPath) ?? "";	// 파일 폴더
		string sName	= Path.GetFileName(sDir);				// 파일 폴더 이름

		return sName.Length == 8 && sName.All(char.IsAsciiDigit) ? Path.GetDirectoryName(sDir) ?? sDir : sDir;
	}

	private static int GetFileId(string sMachine, string sPath, string sFolder, string sZone)	// 원문 파일 번호 (머신·클라 경로·존마다 1부터, 최근 것: 잠금 없이, 새 파일·오래 안 쓴 파일 (최근 목록 밖)·아직 안 읽음: 잠금 안에서 file.txt 에 추가 → 줄 목록엔 번호만, 색인 폴더 지움: 타이머가 번호 새로)
	{
		string sKey = sMachine + "\t" + sPath + "\t" + sZone;	// 머신·클라 경로·존

		if (m_bFileLoaded && TryGetRecent(m_hmapFileId, m_hmapFileIdOld, sKey, out int nId))
		{
			return nId;
		}

		lock (m_kIndexLock)
		{
			LoadFileList();

			if (TryGetRecent(m_hmapFileId, m_hmapFileIdOld, sKey, out nId))
			{
				return nId;
			}

			string sFile = Path.Combine(GetIndexDir(), "file.txt");	// 원문 파일 번호 목록

			nId = m_listFileOffset.Count + 1;

			Directory.CreateDirectory(Path.GetDirectoryName(sFile)!);

			long nOffset = File.Exists(sFile) ? new FileInfo(sFile).Length : 0;	// 이 줄 시작 (파일 끝)

			File.AppendAllText(sFile, $"{nId},{ToCsv(sMachine, false)},{ToCsv(sPath, false)},{ToCsv(sFolder, false)},{ToCsv(sZone, false)}\n");

			m_listFileOffset.Add(nOffset);
			m_hmapFileId[sKey] = nId;

			if (sMachine != "")
			{
				m_hmapMachine.TryAdd(sMachine, sMachine);
			}

			AddFileInfo(nId, (sMachine, sPath, sFolder, sZone));

			return nId;
		}
	}

	private static void LoadFileList()	// Data\Index\file.txt → 원문 파일 번호마다 줄 위치·머신 목록 + 끝에서부터 m_nFileIdPreload 개 최근 목록 (처음 한 번, 잠금 안에서, 줄 정보는 안 둠, 다 읽은 뒤 m_bFileLoaded → 잠금 없는 찾기는 그 뒤만)
	{
		if (m_bFileLoaded)
		{
			return;
		}

		string							sFile		= Path.Combine(GetIndexDir(), "file.txt");	// 원문 파일 번호 목록
		Queue<(string sKey, int nId)>	queueRecent	= new Queue<(string sKey, int nId)>();		// 끝 X개 (최근 목록에 올림)

		if (File.Exists(sFile))
		{
			ScanFileList(sFile, (nOffset, sEntry) =>
			{
				if (!TryParseFileLine(sEntry, out int nId, out (string sMachine, string sPath, string sFolder, string sZone) kInfo))	// 형식 오류
				{
					return;
				}

				while (m_listFileOffset.Count < nId)
				{
					m_listFileOffset.Add(-1);
				}

				m_listFileOffset[nId - 1] = nOffset;

				if (kInfo.sMachine != "")
				{
					m_hmapMachine.TryAdd(kInfo.sMachine, kInfo.sMachine);
				}

				queueRecent.Enqueue((kInfo.sMachine + "\t" + kInfo.sPath + "\t" + kInfo.sZone, nId));

				if (queueRecent.Count > m_nFileIdPreload)
				{
					queueRecent.Dequeue();
				}
			});
		}

		foreach ((string sKey, int nId) in queueRecent)	// 같은 키: 뒤 번호
		{
			m_hmapFileId[sKey] = nId;
		}

		m_nFileIdTick	= Environment.TickCount64;
		m_bFileLoaded	= true;
	}

	private static void ScanFileList(string sFile, Action<long, string> funcLine)	// file.txt 줄마다 funcLine(줄 시작 바이트, 줄 글자) (X 바이트씩 읽음 → 파일 전체를 메모리에 안 올림, 끝 줄바꿈 없는 줄 포함)
	{
		using FileStream kStream = new FileStream(sFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);	// 추가 쓰는 중에도 읽음

		byte[]			arrBuffer	= new byte[64 * 1024];	// 읽기 버퍼
		MemoryStream	kLine		= new MemoryStream();	// 지금 줄 (버퍼 넘어 이어짐)
		long			nBase		= 0;					// 버퍼 첫 바이트 위치
		long			nStart		= 0;					// 지금 줄 시작 위치
		int				nRead;								// 읽은 바이트

		while ((nRead = kStream.Read(arrBuffer)) > 0)
		{
			int nPos = 0;	// 버퍼 안 다음 줄 시작

			for (int nFind = Array.IndexOf(arrBuffer, (byte)'\n', 0, nRead); nFind >= 0; nFind = Array.IndexOf(arrBuffer, (byte)'\n', nPos, nRead - nPos))
			{
				kLine.Write(arrBuffer, nPos, nFind - nPos);

				funcLine(nStart, DecodeFileLine(kLine.GetBuffer(), (int)kLine.Length));

				kLine.SetLength(0);

				nPos	= nFind + 1;
				nStart	= nBase + nPos;
			}

			kLine.Write(arrBuffer, nPos, nRead - nPos);

			nBase += nRead;
		}

		if (kLine.Length > 0)	// 끝 줄 (줄바꿈 없음)
		{
			funcLine(nStart, DecodeFileLine(kLine.GetBuffer(), (int)kLine.Length));
		}
	}

	private static string ReadFileLine(long nOffset)	// file.txt nOffset 부터 한 줄 (최대 m_nFileLineMax 바이트)
	{
		using FileStream kStream = new FileStream(Path.Combine(GetIndexDir(), "file.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);	// 추가 쓰는 중에도 읽음

		byte[] arrLine = new byte[(int)Math.Clamp(kStream.Length - nOffset, 0, m_nFileLineMax)];	// 줄 (최대)

		kStream.Position = nOffset;
		kStream.ReadExactly(arrLine);

		int nEnd = Array.IndexOf(arrLine, (byte)'\n');	// 줄 끝 (없음: 끝까지)

		return DecodeFileLine(arrLine, nEnd < 0 ? arrLine.Length : nEnd);
	}

	private static string DecodeFileLine(byte[] arrLine, int nLength)	// file.txt 줄 바이트 → 글자 (UTF-8 BOM·끝 \r 뗌)
	{
		return Encoding.UTF8.GetString(arrLine, 0, nLength).TrimStart((char)0xFEFF).TrimEnd('\r');
	}

	private static bool TryParseFileLine(string sEntry, out int nId, out (string sMachine, string sPath, string sFolder, string sZone) kInfo)	// file.txt 한 줄 "번호,머신,클라 경로,Folder,Zone" (Folder·Zone 칸 없음: "", 형식 오류: false)
	{
		int nPos = 0;	// 읽을 위치

		nId		= 0;
		kInfo	= ("", "", "", "");

		if (!ReadCsv(sEntry, ref nPos, out string sId) || !int.TryParse(sId, NumberStyles.None, CultureInfo.InvariantCulture, out nId) || nId <= 0 || !ReadCsv(sEntry, ref nPos, out string sMachine) || !ReadCsv(sEntry, ref nPos, out string sPath))
		{
			return false;
		}

		if (!ReadCsv(sEntry, ref nPos, out string sFolder))	// Folder 칸 없음
		{
			sFolder = "";
		}

		if (!ReadCsv(sEntry, ref nPos, out string sZone))	// Zone 칸 없음 (예전 번호)
		{
			sZone = "";
		}

		kInfo = (sMachine, sPath, sFolder, sZone);

		return true;
	}

	private static bool TryGetRecent<TKey, TValue>(ConcurrentDictionary<TKey, TValue> hmapNew, ConcurrentDictionary<TKey, TValue> hmapOld, TKey kKey, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TValue kValue) where TKey : notnull	// 최근 목록 찾기 (이번 세대, 앞 세대: 이번 세대로 옮김, 잠금 없음)
	{
		if (hmapNew.TryGetValue(kKey, out kValue))
		{
			return true;
		}

		if (!hmapOld.TryGetValue(kKey, out kValue))
		{
			return false;
		}

		hmapNew.TryAdd(kKey, kValue);

		return true;
	}

	private static void AddFileInfo(int nFileId, (string sMachine, string sPath, string sFolder, string sZone) kInfo)	// 원문 파일 번호 → 정보 최근 목록에 (잠금 안에서, m_nFileInfoMax 넘음: 세대 바꿈 → 앞 세대 버림)
	{
		if (m_hmapFileInfo.Count >= m_nFileInfoMax)
		{
			m_hmapFileInfoOld	= m_hmapFileInfo;
			m_hmapFileInfo		= new ConcurrentDictionary<int, (string sMachine, string sPath, string sFolder, string sZone)>();
		}

		m_hmapFileInfo[nFileId] = kInfo;
	}

	public static string GetFilePath(int nFileId)	// 원문 파일 번호 → 클라 경로 (알람 메일, 없음: "")
	{
		return GetFileInfo(nFileId).sPath;
	}

	private static (string sMachine, string sPath, string sFolder, string sZone) GetFileInfo(int nFileId)	// 원문 파일 번호 → 머신·클라 경로·Folder·Zone (최근 것: 잠금 없이, 아님: 잠금 안에서 file.txt 그 줄만 읽음, 없음·읽기 실패: "", "", "", "")
	{
		if (m_bFileLoaded && TryGetRecent(m_hmapFileInfo, m_hmapFileInfoOld, nFileId, out (string sMachine, string sPath, string sFolder, string sZone) kInfo))
		{
			return kInfo;
		}

		lock (m_kIndexLock)
		{
			LoadFileList();

			if (TryGetRecent(m_hmapFileInfo, m_hmapFileInfoOld, nFileId, out kInfo))
			{
				return kInfo;
			}

			if (nFileId <= 0 || nFileId > m_listFileOffset.Count || m_listFileOffset[nFileId - 1] < 0)	// 없는 번호
			{
				return ("", "", "", "");
			}

			try
			{
				if (!TryParseFileLine(ReadFileLine(m_listFileOffset[nFileId - 1]), out int nId, out kInfo) || nId != nFileId)	// 형식 오류·다른 줄 (file.txt 바뀜)
				{
					return ("", "", "", "");
				}
			}
			catch (Exception kException) when (kException is IOException or UnauthorizedAccessException)	// 사용 중·지워짐: 없음 (다음에 다시)
			{
				return ("", "", "", "");
			}

			AddFileInfo(nFileId, kInfo);

			return kInfo;
		}
	}

	private static string GetLogText(string sLine)	// 원문에서 앞 시간 뺀 내용 (표시용, 시간 없음: 그대로)
	{
		return IsLogTime(sLine) ? sLine.Substring(25).TrimStart() : sLine;
	}

	private static ReadOnlySpan<char> GetLogText(ReadOnlySpan<char> kLine)	// 원문에서 앞 시간 뺀 내용 (단어 나눔·Type·알람용, 시간 없음: 그대로, 문자열 안 만듦)
	{
		return IsLogTime(kLine) ? kLine[25..].TrimStart() : kLine;
	}

	private static bool IsLogTime(ReadOnlySpan<char> kLine)	// 줄 앞이 시간 [yyyy/MM/dd HH:mm:ss.fff] (25글자, 숫자·구분자 자리 확인)
	{
		const string sForm = "[0000/00/00 00:00:00.000]";	// 형식 (0: 숫자)

		if (kLine.Length < sForm.Length)
		{
			return false;
		}

		for (int nIndex = 0; nIndex < sForm.Length; ++nIndex)
		{
			if (sForm[nIndex] == '0' ? !char.IsAsciiDigit(kLine[nIndex]) : kLine[nIndex] != sForm[nIndex])
			{
				return false;
			}
		}

		return true;
	}

	private static string ToCsv(string sValue, bool bQuote)	// CSV 칸 (bQuote·컴마·따옴표 있음: 따옴표로 감싸고 안의 " 는 "")
	{
		return bQuote || sValue.Contains(',') || sValue.Contains('"') ? "\"" + sValue.Replace("\"", "\"\"") + "\"" : sValue;
	}

	private static bool ReadCsv(string sEntry, ref int nPos, out string sField)	// CSV 칸 하나 (따옴표 칸: "" → ", nPos: 다음 칸 시작 = 컴마 다음, 줄 끝: 길이 + 1, 형식 오류: false)
	{
		sField = "";

		if (nPos > sEntry.Length)	// 칸 모자람
		{
			return false;
		}

		if (nPos == sEntry.Length || sEntry[nPos] != '"')	// 따옴표 없는 칸: 다음 컴마까지
		{
			int nEnd = sEntry.IndexOf(',', nPos);	// 칸 끝

			nEnd	= nEnd < 0 ? sEntry.Length : nEnd;
			sField	= sEntry.Substring(nPos, nEnd - nPos);
			nPos	= nEnd + 1;

			return true;
		}

		StringBuilder	kField	= new StringBuilder();	// 따옴표 칸
		int				nIndex	= nPos + 1;				// 읽을 위치

		while (true)
		{
			int nQuote = sEntry.IndexOf('"', nIndex);	// 다음 따옴표

			if (nQuote < 0)	// 닫는 따옴표 없음
			{
				return false;
			}

			kField.Append(sEntry, nIndex, nQuote - nIndex);

			if (nQuote + 1 < sEntry.Length && sEntry[nQuote + 1] == '"')	// "" → "
			{
				kField.Append('"');

				nIndex = nQuote + 2;

				continue;
			}

			nPos = nQuote + 1;

			break;
		}

		if (nPos < sEntry.Length && sEntry[nPos] != ',')	// 닫는 따옴표 뒤 컴마 아님
		{
			return false;
		}

		sField	= kField.ToString();
		nPos	= nPos + 1;

		return true;
	}

	public static HashSet<string> SplitTerm(string sText, bool bIndex = false)	// 단어 나눔 (색인·쿼리 공통): 글자·숫자·'-'·'_' 이어진 것, 소문자, bIndex: 앞뒤 '-'·'_' 뗀 형태·가운데 '-'·'_' 조각도 (예: [NOTI_] → noti_·noti, sk_hidden_b7 → sk_hidden_b7·sk·hidden·b7), '-'·'_' 뿐·중복·m_nIndexWordMax 넘음: 제외
	{
		HashSet<string>	setWord	= new HashSet<string>(StringComparer.Ordinal);	// 단어 (소문자)
		int				nBegin	= 0;												// 조각 시작

		for (int nIndex = 0; nIndex <= sText.Length; ++nIndex)
		{
			if (nIndex < sText.Length && IsWordChar(sText[nIndex]))	// 단어 글자: 조각 이어짐
			{
				continue;
			}

			int nLength = nIndex - nBegin;	// 조각 길이

			if (nLength > 0 && nLength <= m_nIndexWordMax && sText.AsSpan(nBegin, nLength).ContainsAnyExcept('-', '_'))	// '-'·'_' 뿐 (구분선 등)·너무 김: 색인 안 함
			{
				string sWord = sText.Substring(nBegin, nLength).ToLowerInvariant();	// 단어 (앞뒤 그대로)

				setWord.Add(sWord);

				if (bIndex)	// 색인: 앞뒤 '-'·'_' 뗀 형태 + 가운데 '-'·'_' 조각 (중복: 하나)
				{
					string sTrim = sWord.Trim('-', '_');	// 앞뒤 '-'·'_' 뗀 형태

					setWord.Add(sTrim);
					setWord.UnionWith(sTrim.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries));
				}
			}

			nBegin = nIndex + 1;
		}

		return setWord;
	}

	private static void HashTerm(ReadOnlySpan<char> kText, HashSet<ulong> setHash, char[] arrLower)	// 색인 단어 해시 (SplitTerm 과 같은 규칙: 글자·숫자·'-'·'_' 이어진 것, 소문자, 앞뒤 '-'·'_' 뗀 형태·가운데 '-'·'_' 조각도, '-'·'_' 뿐·m_nIndexWordMax 넘음: 제외) → 단어 문자열 안 만들고 GetHash(SplitTerm 단어) 와 같은 값 (arrLower: 소문자 버퍼 m_nIndexWordMax 글자)
	{
		int nBegin = 0;	// 조각 시작

		setHash.Clear();

		for (int nIndex = 0; nIndex <= kText.Length; ++nIndex)
		{
			if (nIndex < kText.Length && IsWordChar(kText[nIndex]))	// 단어 글자: 조각 이어짐
			{
				continue;
			}

			int nLength = nIndex - nBegin;	// 조각 길이

			if (nLength > 0 && nLength <= m_nIndexWordMax && kText.Slice(nBegin, nLength).ContainsAnyExcept('-', '_'))	// '-'·'_' 뿐 (구분선 등)·너무 김: 색인 안 함
			{
				ReadOnlySpan<char>	kWord	= arrLower.AsSpan(0, kText.Slice(nBegin, nLength).ToLowerInvariant(arrLower));	// 단어 소문자 (앞뒤 그대로, 길이 같음)
				ReadOnlySpan<char>	kTrim	= kWord.Trim("-_");																// 앞뒤 '-'·'_' 뗀 형태

				setHash.Add(GetHash(kWord));

				if (kTrim.Length != kWord.Length)	// 다를 때만
				{
					setHash.Add(GetHash(kTrim));
				}

				if (kTrim.ContainsAny('-', '_'))	// 가운데 '-'·'_': 조각도 (예: sk_hidden_b7 → sk·hidden·b7, 쿼리 sk 로 찾음)
				{
					foreach (Range kRange in kTrim.SplitAny("-_"))
					{
						ReadOnlySpan<char> kPart = kTrim[kRange];	// 조각 (연속 '-'·'_' 사이: 빈 조각)

						if (!kPart.IsEmpty)
						{
							setHash.Add(GetHash(kPart));
						}
					}
				}
			}

			nBegin = nIndex + 1;
		}
	}

	private static bool IsWordChar(char cChar)	// 단어 글자: 글자·숫자·'-'·'_' (그 외 기호·빈칸: 단어 나눔)
	{
		return char.IsLetterOrDigit(cChar) || cChar == '-' || cChar == '_';
	}

	private static bool TryGetMinuteRange(long nStart, long nEnd, out DateTime kFrom, out DateTime kTo)	// 기간 yyyyMMddHHmm·yyyyMMddHHmmss (초별 막대 클릭: 1초) → 시작 (포함)·끝 (미포함) (시작 >= 끝·형식 오류: false)
	{
		kTo = default;

		return DateTime.TryParseExact(nStart.ToString(CultureInfo.InvariantCulture), m_arrRangeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out kFrom)
			&& DateTime.TryParseExact(nEnd.ToString(CultureInfo.InvariantCulture), m_arrRangeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out kTo)
			&& kFrom < kTo;
	}

	public static bool IsOriginPath(string sMachine, string sPath)	// 원문 저장 가능 (HTTP 수신 미리 확인, 머신·경로 오류·형식 오류: false)
	{
		try
		{
			return GetOriginPath(sMachine, sPath) != "";
		}
		catch (Exception)	// 경로 형식 오류
		{
			return false;
		}
	}

	private static string GetOriginPath(string sMachine, string sPath)	// 원문 저장 파일: 기준 폴더 하위 Data\Origin\머신\클라 전체 경로 (드라이브 ':' 제거, 머신·경로 오류: "")
	{
		if (sMachine == "" || sMachine.Length > 255 || sMachine.Trim('.') == "" || sMachine.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || sPath.Length > 1000 || !Path.IsPathFullyQualified(sPath) || !sPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))	// 머신명이 폴더명으로 안 됨·전체 경로 아님·로그 아님
		{
			return "";
		}

		string sRoot = Path.GetFullPath(Path.Combine(GetBaseDir(), "Data", "Origin", sMachine)) + Path.DirectorySeparatorChar;	// 머신 폴더 (끝 구분자 포함)
		string sFile = Path.GetFullPath(Path.Combine(sRoot, sPath.Replace(":", "").TrimStart('\\', '/')));						// 저장 파일 (D:\Logs → D\Logs, \\서버\공유 → 서버\공유)

		return sFile.StartsWith(sRoot, StringComparison.OrdinalIgnoreCase) ? sFile : "";	// 머신 폴더 밖 (..\ 등): 거부
	}
}

static class QueryPage	// 페이지 쿼리 탭 스크립트 (상태 없음 → 페이지 조립이 TabQuery 정적 초기화 (적재 워커·설정 로딩) 안 일으킴, aMenu 쿼리 꺼짐 대비)
{
	public static string QueryHTML()	// 페이지 쿼리 탭 스크립트 (Web.IndexHtml 조립)
	{
		return """
		class Query	// 쿼리 탭 (this 없음 → 메서드를 이벤트 처리기로 바로 넘김)
		{
		    static PAGE_BLOCK	= 10;		// 페이지 번호 한 번에 보이는 수 (‹ › 로 앞뒤 묶음)
		    static CHART_MIN	= 60;		// 쿼리 1번 창 막대 최소 높이 (px, 1·2번 창 사이 바 드래그)
		    static sQueryKey	= "";		// 지금 결과의 쿼리
		    static nStart		= 0;		// 지금 결과의 기간 시작 (yyyyMMddHHmm, 1초 기간: yyyyMMddHHmmss, 포함)
		    static nEnd			= 0;		// 지금 결과의 기간 끝 (yyyyMMddHHmm, 1초 기간: yyyyMMddHHmmss, 미포함)
		    static LENS_WIDE	= [null, 60, 24, 1, 12];	// 렌즈 스텝 0 ↑ 넓힐 기간 (기준 포함 지난 X: 분봉 60분·시봉 24시간·일봉 1개월 (그 달)·월봉 12개월, 초: 없음)
		    static LENS_NAME	= ["초", "분", "시", "일", "월"];	// 막대 단위 (0 초 ~ 4 월, 렌즈 스텝 0: 기간 기본 단위, ↓: 한 단위 작게)
		    static LENS_BAR_MAX	= 86400;	// 렌즈 막대 최대 수 (넘는 단위: ↓ 못 감, 서버 m_nLensBarMax 와 같음, 24시간 이하: 초봉까지)
		    static nLens		= 0;		// 렌즈 스텝 (0: 기간 기본 단위, ↓ 마다 +1 (화면 숫자 -1), 새 결과: 0, 단계 이동: 그 단계 마지막 스텝)
		    static bLensLoading	= false;	// 렌즈 초·분봉 건수 가져오는 중
		    static nBarUnit		= 2;		// 지금 막대 단위 (LENS_NAME 위치, 마우스 올림·누름·렌즈 툴팁)
		    static kChartData	= null;		// 지금 막대 건수 { arrHour, arrMinute, arrSecond, arrLens: [초봉, 분봉] } (렌즈 다시 그림, arrLens: 기본 단위보다 작은 초·분봉 [{nTime, nCount}], 따로 가져옴)
		    static kFrom		= null;		// 지금 결과의 기간 시작 (Date)
		    static kTo			= null;		// 지금 결과의 기간 끝 (Date, 미포함)
		    static arrHistory	= [];		// 들어온 단계 { sKey, kFrom, kTo, nRange, nLens, arrLens, bWiden, nLensBase } (0: 최초 쿼리, 막대·단어·필드 값 클릭·렌즈 넓힘마다 추가, 렌즈 스텝·초·분봉·넓힘 표시: 스냅샷)
		    static arrLine		= [];		// 지금 페이지 결과 줄 { sTime, sMachine, sPath, sText } (줄 클릭 펼침용)
		    static nHistory		= -1;		// 지금 단계 (← → 로 오감)
		    static kOtherRange	= null;		// 안 보이는 쪽 명령 창 기간 (쿼리 모드: 서치 기간, 그 외: 쿼리 기간, null: 쿼리 처음 → D 최근 기간)
		    static arrBar		= [];		// 막대 { nFirst, nLast, nCount, nKey } (마우스 올림 표시용)
		    static nPage		= 0;		// 지금 페이지 (0부터)
		    static nPageSize	= 1000;		// 한 페이지 건수 (서버가 알려 줌, 페이지 번호 기준)
		    static CHUNK		= 100;		// 한 번에 읽어 그리는 줄 수 (스크롤: 이어서)
		    static nRowEnd		= -1;		// 이 페이지 끝으로 확인한 줄 수 (요청보다 덜 옴: 읽은 데까지, -1: 모름 → 빈 높이·이어 읽기 여기까지)
		    static nChunkSeq	= 0;		// 페이지 읽기 번호 (새 쿼리·페이지: 늘어남 → 늦게 온 이어 읽기 버림)
		    static bChunkLoading	= false;	// 이어 읽는 중
		    static nTotal		= 0;		// 전체 건수
		    static bQuerying	= false;	// 쿼리 중 (중복 요청 막음)
		    static bResetting	= false;	// 리셋·모래시계 중 (다시 누름 무시)
		    static arrBackQuery	= [];		// 쿼리 실행 기억 (모래시계, 최근 X회, 브라우저 메모리만, 맨 뒤: 지금 실행)
		    static arrBackSearch	= [];		// 서치 실행 기억 (위와 같음)
		    static bWordAdded	= false;	// Ctrl·Alt 누른 채 단어를 쿼리칸·검색어에 더함 (키 뗄 때 쿼리·서치)
		    static kWordInput	= null;		// 단어를 더한 칸 (쿼리칸: 쿼리, 검색어: 서치)
		    static nModifierTimer	= 0;	// Ctrl·Alt 뗀 뒤 쿼리 기다림 (0: 없음)
		    static MODIFIER_WAIT_MS	= 1000;	// Ctrl·Alt 뗀 뒤 쿼리·서치까지 (ms, 그 사이 다시 누르면 이어서 더함)
		    static arrField		= [];		// 왼쪽 필드 { sName, bBase, nValue, arrValue: [{ sValue, nCount }] } (bBase: 기본 필드, 통계 전: nValue 없음)
		    static nStatSeq		= 0;		// 통계 요청 번호 (늦게 온 앞 요청 결과 버림)
		    static bStatLoading	= false;	// 통계 가져오는 중 (값 수: …)
		    static kQueryAbort	= null;		// 쿼리 요청 중단 (중단 버튼, null: 쿼리 중 아님)
		    static kQueryTask	= null;		// 진행 중인 쿼리 (LoadPage, ← → 가 중단하고 끝나길 기다림)
		    static mapStepResult	= new WeakMap();	// 단계 → 마지막 본 결과 { kPage: 페이지 응답 (처음 CHUNK 줄), arrField: 필드 통계 } (← →·새 창 복사: 다시 쿼리 안 함, 버린 단계: 같이 없어짐)
		    static bEntered		= false;	// 쿼리 탭 들어온 적 있음 (처음만 D 1일)
		    static arrSaved		= [];		// 저장 쿼리 { sKey, sQuery, nMinute } (쿼리 설정)
		    static WORD_REGEX	= /[\p{L}\p{Nd}_-]+/gu;	// 단어 (서버 SplitTerm 과 같음: 글자·숫자·-·_ 이어진 것)
		    static sMarkKey		= null;		// 강조 단어를 만든 쿼리 (같은 쿼리면 다시 안 만듦)
		    static setMarkWord	= new Set();	// 강조할 쿼리 단어 (소문자)
		    static arrMarkExact	= [];		// 원문 그대로 강조할 쿼리 글자 (IP·IP:포트·날짜·시간, 단어로 안 나눔)
		    static arrMarkWild	= [];		// 와일드카드 강조 정규식 (ab*·*ab·*ab*, Key.MakeRegex)
		    static EXACT_REGEX	= /^(\d{1,3}(\.\d{1,3}){3}(:\d{1,5})?|(\d{4}\/)?\d{1,2}\/\d{1,2}|\d{1,2}:\d{2}(:\d{2}(\.\d{1,7})?)?)$/;	// 원문 그대로 확인할 검색어 (서버 m_kExactTerm 과 같음)
		    static sSavedKey	= "";		// 쿼리 설정에서 선택한 저장 쿼리 제목 (설정 목록 강조, "": 고르지 않음)
		    static sEditKey		= "";		// 쿼리 추가·수정 창에서 수정할 저장 쿼리 제목 ("": 추가)
		    static bAlarm		= false;	// 쿼리 설정 알람 탭
		    static bRule		= false;	// 쿼리 설정 필드 탭 (설정 aField, 알람 탭과 같이 안 켜짐)
		    static bSearch		= false;	// 설정 창: 저장 서치 (서치 탭 저장 버튼, 저장 쿼리 창·기능 공용, 목록·선택: Search.arrSaved·sSavedKey)
		    static arrRule		= [];		// 필드 정의 { sKey, sRegex, sValue } (sValue 있음: 고정값)
		    static sEditRule	= "";		// 필드 추가·수정 창에서 수정할 필드 이름 ("": 추가)
		    static RULE_KEY_MAX		= 32;	// 필드 이름 최대 글자 (서버와 같음)
		    static RULE_VALUE_MAX	= 260;	// 필드 고정값 최대 글자 (서버와 같음)
		    static RULE_REGEX_MAX	= 500;	// 필드 정규식 최대 글자 (서버와 같음)
		    static RULE_BASE		= ["machine", "folder", "type"];	// 쓸 수 없는 이름 (기본 필드, 소문자)
		    static arrAlarm		= [];		// 알람 { nID, sKey, sQuery, nMinute, nCount, nDelay, aMail, sHook, aSMS, bEnable }
		    static nEditID		= 0;		// 알람 추가·수정 창에서 수정할 알람 (0: 추가)
		    static nDragIndex	= -1;		// 쿼리 설정 드래그 중인 줄 위치 (-1: 없음)
		    static SAVED_MINUTE	= 10;		// 쿼리 추가·수정 기본 간격 (분, 서버와 같음)
		    static SAVED_MINUTE_MAX	= 129600;	// 쿼리 간격 최대 (분, 서버와 같음)
		    static ALARM_MINUTE	= 1;		// 알람 추가 기본 간격 (분)
		    static ALARM_COUNT	= 10;		// 알람 추가 기본 건수
		    static ALARM_DELAY	= 1;		// 알람 추가 기본 억제 (분)
		    static ALARM_MINUTE_MAX	= 1440;	// 알람 간격 최대 (분, 서버와 같음)
		    static ALARM_DELAY_MAX	= 1440;	// 알람 억제 최대 (분, 서버와 같음)
		    static ALARM_LIST_MAX	= 20;	// 알람 메일·SMS 최대 개수 (서버와 같음)
		    static arrAlarmMail	= [];		// 알람 추가·수정 창 메일 받는 계정 목록 (계정 아이디, 예전: 메일 주소)
		    static arrAlarmSMS	= [];		// 알람 추가·수정 창 SMS 받는 계정 목록 (계정 아이디)
		    static arrAlarmHistory	= [];	// 알람 기록 창 줄 (최신 순, 제목 클릭: 내용)
		    static sAlarmHistoryLink	= "";	// 알람 기록 내용 창 쿼리 링크 (내용 클릭: 그 기간·쿼리, 없음: "")
		    static kStatAbort	= null;		// 통계 요청 중단 (중단 버튼·다음 통계, null: 통계 중 아님)
		    static RANGE_PAD	= 1;		// D 기간 여유 (초, E: 기준 + X초 → 지금 초 줄 포함, S: E - D (같은 초), 서치·쿼리 공통)
		    static RESET_WAIT_MS	= 50;	// 리셋: 쿼리·서치 끝 확인 간격 (ms)
		    static RESET_STOP_MS	= 1000;	// 리셋: 서치 중지 다시 요청 간격 (ms, 막 시작한 검색: 중지 안 됨 → 다시)
		    static BACK_MAX		= 10;	// 모래시계 실행 기억 (탭마다 최근 X회)
		    static FIELD_PAGE	= 100;		// 필드 새 창 한 페이지 값 수
		    static FIELD_SPLIT_MIN	= 120;	// 쿼리 2번 창 왼쪽 필드·오른쪽 로그 최소 폭 (px, 세로선 드래그)
		    static sFieldName	= "";		// 필드 새 창 필드
		    static arrFieldValue	= [];	// 필드 새 창 값 전체 { sValue, nCount } (건수 많은 순)
		    static nFieldTotal	= 0;		// 필드 새 창 맞는 줄 수 (값 비율)
		    static nFieldPage	= 0;		// 필드 새 창 지금 페이지 (0부터)
		    static nFieldSort	= -1;		// 필드 새 창 정렬 칸 (-1: 원래 순서)
		    static bFieldAsc	= false;	// 필드 새 창 정렬 방향 (true: 오름차순, false: 내림차순)

		    static LoadQuery()	// 쿼리 탭 버튼: 쿼리 1·2번 창 (처음: D 1일, 다른 탭에서 돌아옴: 마지막 결과)
		    {
		        if (Search.bSearching)	// 검색 중
		        {
		            return;
		        }

		        ShowPanel("query");

		        if (!Query.bEntered)	// 처음 들어옴 (기간: SwapRange 처음 → D 1일·세션 최초 방문 기준, 서치와 같음)
		        {
		            Query.bEntered = true;

		            Query.LoadSaved();	// 쿼리 설정 저장 쿼리 목록
		            Query.LoadField();	// 왼쪽 필드 이름 (통계 실패 대비)
		        }
		    }

		    static RunQuery()	// 쿼리 버튼·Enter: 입력한 쿼리 (없음: *)·명령 창 기간 (S 포함 ~ E 미포함, D: 지금 기준) 첫 페이지
		    {
		        if (Query.bQuerying)	// 쿼리 중 (쿼리칸 Enter 등): 무시
		        {
		            return;
		        }

		        const sKey = kQueryKey.value.trim() || "*";	// 쿼리 (없음: * 전체 로그, 쿼리칸은 비운 그대로)

		        if (kQueryRange.selectedIndex > 0)	// D 최근 기간: 지금까지로 다시
		        {
		            Query.SetRecent();
		        }

		        Query.PushBack(true);	// 실행 기억 (모래시계)

		        const kFrom	= Query.GetPicker(kStart, kStartHour, kStartMinute);	// 시작 (포함)
		        const kTo	= Query.GetPicker(kEnd, kEndHour, kEndMinute);		// 끝 (미포함)

		        Query.Query(sKey, kFrom, kTo, true, true);	// 최초 단계부터 새로
		    }

		    static GetState()	// 지금 결과 상태 (새 쿼리 중단·실패: 이 상태로 되돌림)
		    {
		        return { sQueryKey: Query.sQueryKey, nStart: Query.nStart, nEnd: Query.nEnd, kFrom: Query.kFrom, kTo: Query.kTo, nLens: Query.nLens, arrHistory: Query.arrHistory.slice(), nHistory: Query.nHistory };
		    }

		    static Query(sKey, kFrom, kTo, bHistory, bReset = false, kPrev = Query.GetState())	// 쿼리 kFrom (포함) ~ kTo (미포함) 첫 페이지 (시작 = 종료: 그 1분, bHistory: 단계 남김, bReset: 단계 처음부터, kPrev: 중단·실패 때 되돌릴 상태)
		    {
		        const kPickTo = kTo;	// 고른 끝 (들어온 단계: S·E 그대로 다시 보이게)

		        if (Query.bQuerying)
		        {
		            return;
		        }

		        if (kFrom != null && kTo != null && kFrom.getTime() === kTo.getTime())	// 시작 = 종료: 그 1분
		        {
		            kTo = new Date(kFrom.getTime() + 60 * 1000);
		        }

		        if (kFrom == null || kTo == null || kFrom > kTo)	// 기간 오류
		        {
		            alert("기간을 확인해 주세요. (S ≤ E)");

		            return;
		        }

		        if (bReset)	// 쿼리 버튼: 최초 단계부터 새로 (기간 확인 뒤, 앞 단계 모두 비움)
		        {
		            Query.arrHistory	= [];
		            Query.nHistory	= -1;
		        }

		        Query.SetResultRange(sKey, kFrom, kTo);
		        SyncQueryMachine();	// M: 쿼리칸 머신 조건 (저장 쿼리·단계·필드 값으로 바뀐 쿼리칸)

		        if (bHistory)	// 들어온 단계 남김 (지금 단계 뒤는 버림)
		        {
		            Query.arrHistory.splice(Query.nHistory + 1);
		            Query.arrHistory.push({ sKey, kFrom, kTo: kPickTo, nRange: kQueryRange.selectedIndex });

		            Query.nHistory = Query.arrHistory.length - 1;
		        }

		        Query.ShowHistory();

		        const kStep = Query.arrHistory[Query.nHistory];	// 이 결과 단계 (결과 보관)

		        Query.kQueryTask = Query.LoadPage(0, kPrev, kStep);
		        Query.LoadStat(kStep);	// 왼쪽 필드별 통계 (따로 가져옴 → 결과 먼저)
		    }

		    static SetResultRange(sKey, kFrom, kTo)	// 지금 결과 쿼리·기간 (kTo: 미포함, 초 있음: yyyyMMddHHmmss)
		    {
		        const bSecond = kFrom.getSeconds() !== 0 || kTo.getSeconds() !== 0;	// 초 기간 (초별 막대 클릭: 1초)

		        Query.sQueryKey	= sKey;
		        Query.nStart		= bSecond ? Query.ToSecond(kFrom) : Query.ToMinute(kFrom);
		        Query.nEnd		= bSecond ? Query.ToSecond(kTo) : Query.ToMinute(kTo);
		        Query.kFrom		= kFrom;
		        Query.kTo			= kTo;
		        Query.nLens		= 0;	// 렌즈: 기간 기본 단위
		    }

		    static ClearResult()	// 최초 입장 리셋: 쿼리 결과 메모리 (단계·보관 결과·막대·렌즈 스냅샷·필드 통계)·스텝·결과 화면 처음으로 (통계 중: 끊음)
		    {
		        Query.kStatAbort?.abort();	// 통계 끊음
		        Query.nStatSeq++;				// 늦게 온 통계 버림
		        Query.nChunkSeq++;			// 늦게 온 이어 읽기 버림

		        Query.bStatLoading	= false;
		        Query.sQueryKey		= "";	// 쿼리 전 (필드 클릭: 값 목록 안 엶)
		        Query.arrHistory		= [];
		        Query.nHistory		= -1;
		        Query.mapStepResult	= new WeakMap();
		        Query.kChartData		= null;
		        Query.nLens			= 0;
		        Query.arrBar			= [];
		        Query.nTotal			= 0;
		        Query.nPage			= 0;
		        kQueryChart.hidden		= true;
		        kQueryTitle.textContent	= "";
		        kQueryLogTitle.textContent	= "";

		        Query.ShowRows([]);
		        Query.ShowPager();
		        Query.ShowHistory();
		        Query.ShowField(Query.arrField.map(kField => ({ sName: kField.sName, bBase: kField.bBase, nValue: kField.bBase ? undefined : 0 })));	// 필드 통계 처음 (기본 필드: -, 추가 필드: 숨김, 이름: 그대로 → 필드:값 강조)
		    }

		    static SaveStepResult(kStep, kResult)	// 단계 결과 보관 (페이지·필드 통계 따로 옴: 합침, 단계 없음: 안 함)
		    {
		        if (kStep != null)
		        {
		            Query.mapStepResult.set(kStep, { ...Query.mapStepResult.get(kStep), ...kResult });
		        }
		    }

		    static ShowStep(kStep, kResult)	// 단계 보관 결과 그대로 그리기 (다시 쿼리 안 함, 필드 통계 없음: 통계만 다시)
		    {
		        const kTo = kStep.kFrom.getTime() === kStep.kTo.getTime() ? new Date(kStep.kFrom.getTime() + 60 * 1000) : kStep.kTo;	// 시작 = 종료: 그 1분 (Query 와 같음)

		        Query.SetResultRange(kStep.sKey, kStep.kFrom, kTo);

		        Query.nLens = kStep.nLens ?? 0;	// 렌즈: 그 단계 마지막 스텝 (스냅샷)
		        Query.nChunkSeq++;	// 앞 페이지 이어 읽기 응답 버림
		        Query.ShowHistory();
		        Query.ShowPage({ ...kResult.kPage, arrLine: kResult.kPage.arrLine.slice() });

		        if (kResult.arrField == null)
		        {
		            Query.LoadStat(kStep);

		            return;
		        }

		        Query.kStatAbort?.abort();	// 앞 단계 통계 끊음
		        Query.nStatSeq++;				// 늦게 온 앞 통계 버림
		        Query.bStatLoading = false;

		        Query.ShowField(kResult.arrField);
		    }

		    static ShowHistory()	// ← → 켬·끔, 가운데 지금 단계 (보는 1번 창 단계, 0: 최초, 검색 중: 끔)
		    {
		        const kOwner = kResultPanel.classList.contains("query") ? Query : Search;	// 보는 1번 창 단계

		        kQueryPrev.disabled		= kOwner.nHistory <= 0 || Search.bSearching;
		        kQueryNext.disabled		= kOwner.nHistory >= kOwner.arrHistory.length - 1 || Search.bSearching;
		        kQueryDepth.textContent	= String(Math.max(0, kOwner.nHistory));
		    }

		    static EditHistory()	// 가운데 단계 누름: 숫자 입력 → Enter: 그 단계로 (넘음: 끝 단계), Esc·밖 누름: 취소 (단계 1개뿐·검색 중: 안 함)
		    {
		        const kOwner = kResultPanel.classList.contains("query") ? Query : Search;	// 보는 1번 창 단계

		        if (kOwner.arrHistory.length <= 1 || Search.bSearching || document.getElementById("QueryDepthInput") != null)
		        {
		            return;
		        }

		        const kInput	= document.createElement("input");	// 단계 입력 (단계 버튼 자리, 같은 크기)
		        let bDone		= false;							// 끝냄 (Enter·Esc·밖 누름 중 한 번만)

		        kInput.type			= "text";
		        kInput.inputMode	= "numeric";
		        kInput.id			= "QueryDepthInput";
		        kInput.value		= kQueryDepth.textContent;
		        kInput.style.width	= `${Math.max(kQueryDepth.offsetWidth, 44)}px`;
		        kInput.style.height	= `${kQueryDepth.offsetHeight}px`;

		        const funcClose = (bGo) =>	// 입력 닫고 단계 버튼 다시 (bGo: 그 단계로)
		        {
		            if (bDone)
		            {
		                return;
		            }

		            bDone = true;

		            const nTarget = Number.parseInt(kInput.value, 10);	// 입력한 단계

		            kInput.remove();

		            kQueryDepth.style.display = "";

		            if (bGo && Number.isFinite(nTarget))
		            {
		                const nIndex = Math.min(Math.max(nTarget, 0), kOwner.arrHistory.length - 1);	// 갈 단계 (넘음: 끝)

		                if (nIndex !== kOwner.nHistory)
		                {
		                    kOwner.GoHistory(nIndex - kOwner.nHistory);
		                }
		            }
		        };

		        kInput.addEventListener("keydown", (kEvent) =>	// Enter: 그 단계로, Esc: 취소
		        {
		            if (kEvent.key === "Enter" && !kEvent.isComposing)
		            {
		                kEvent.preventDefault();

		                funcClose(true);
		            }
		            else if (kEvent.key === "Escape")
		            {
		                kEvent.preventDefault();

		                funcClose(false);
		            }
		        });

		        kInput.addEventListener("blur", () => funcClose(false));	// 밖 누름: 취소

		        kQueryDepth.style.display = "none";
		        kQueryDepth.before(kInput);

		        kInput.focus();
		        kInput.select();
		    }

		    static async GoHistory(nDirection)	// ← (-1)·→ (1): 들어온 단계 오가며 그 조건 (쿼리·S·E·D)·마지막 본 결과 그대로 (결과 없음: 다시 쿼리, 쿼리 중: 중단 후)
		    {
		        if (Query.bQuerying)	// 쿼리 중: 중단 (앞 결과 상태로 돌아감) 뒤 이어서
		        {
		            Query.StopQuery();

		            await Query.kQueryTask;
		        }

		        const nIndex = Query.nHistory + nDirection;	// 갈 단계

		        if (Query.bQuerying || nIndex < 0 || nIndex >= Query.arrHistory.length)
		        {
		            return;
		        }

		        const kStep = Query.arrHistory[nIndex];	// 그 단계 { sKey, kFrom, kTo, nRange }
		        const kPrev = Query.GetState();			// 중단·실패 때 되돌릴 상태 (단계 바꾸기 전)

		        Query.nHistory = nIndex;

		        Query.SetPicker(kStart, kStartHour, kStartMinute, kStep.kFrom);
		        Query.SetPicker(kEnd, kEndHour, kEndMinute, kStep.kTo);

		        kQueryRange.selectedIndex	= kStep.nRange;
		        kQueryKey.value				= kStep.sKey === "*" ? "" : kStep.sKey;	// 필드 값 클릭으로 바뀐 쿼리도 그 단계로 (* 전체: 비움)

		        Query.OnQueryInput();	// 선택한 저장 쿼리와 달라지면 선택 해제

		        const kResult = Query.mapStepResult.get(kStep);	// 그 단계 마지막 본 결과

		        if (kResult?.kPage != null)	// 결과 있음: 다시 쿼리 안 함
		        {
		            Query.ShowStep(kStep, kResult);

		            return;
		        }

		        Query.Query(kStep.sKey, kStep.kFrom, kStep.kTo, false, false, kPrev);
		    }

		    static SwapRange(bEnter)	// 쿼리 모드 들어감·나감: 명령 창 기간·D 서치·쿼리 따로 보관·꺼냄 (처음: 쿼리·서치 같은 기본 = D 1일, E = 세션 최초 방문 시각 (초) + X초, S = E - 1일, 탭 바꿈: 그 탭 값 그대로)
		    {
		        const kOther = Query.kOtherRange;	// 꺼낼 값

		        if (kOther == null)	// 처음: 기본 기간 (보관할 서치 값도 같음)
		        {
		            Query.SetVisitRange();
		        }

		        Query.kOtherRange = { sStart: kStart.value, sStartHour: kStartHour.value, sStartMinute: kStartMinute.value, sStartSecond: kStartSecond.value, sEnd: kEnd.value, sEndHour: kEndHour.value, sEndMinute: kEndMinute.value, sEndSecond: kEndSecond.value, nRange: kQueryRange.selectedIndex };

		        if (kOther == null)
		        {
		            return;
		        }

		        kStart.value				= kOther.sStart;
		        kStartHour.value			= kOther.sStartHour;
		        kStartMinute.value			= kOther.sStartMinute;
		        kStartSecond.value			= kOther.sStartSecond ?? "00";
		        kEnd.value					= kOther.sEnd;
		        kEndHour.value				= kOther.sEndHour;
		        kEndMinute.value			= kOther.sEndMinute;
		        kEndSecond.value			= kOther.sEndSecond ?? "00";
		        kQueryRange.selectedIndex	= kOther.nRange;
		    }

		    static SetVisitRange()	// 기본 기간: D 1일, E = 세션 최초 방문 시각 (초) + X초, S = E - 1일 (쿼리·서치 같음)
		    {
		        kQueryRange.value = "1440";

		        Query.SetPadRange(Query.GetVisitTime(), Number(kQueryRange.value));
		    }

		    static SetPadRange(kBase, nMinute)	// S·E = 기준 + 여유 - nMinute 분 ~ 기준 + 여유 (S = E - D, 같은 초, 기준: 초 단위, 여유 RANGE_PAD 초, 서치·쿼리 공통)
		    {
		        const nEnd = kBase.getTime() + Query.RANGE_PAD * 1000;	// E (기준 + 여유)

		        Query.SetPicker(kStart, kStartHour, kStartMinute, new Date(nEnd - nMinute * 60 * 1000));
		        Query.SetPicker(kEnd, kEndHour, kEndMinute, new Date(nEnd));
		    }

		    static GetVisitTime()	// 세션 최초 방문 시각 (초 단위, 브라우저 탭 sessionStorage: 새로 고침·새 창 같은 시각, 저장소 안 됨: 지금)
		    {
		        let nTime = 0;	// 저장한 시각 (ms)

		        try
		        {
		            nTime = Number(sessionStorage.getItem("HMXVisitTime") ?? 0);
		        }
		        catch (kError)	// 저장소 사용 불가
		        {
		            console.error(kError);
		        }

		        if (!Number.isFinite(nTime) || nTime <= 0)	// 처음 방문
		        {
		            const kNow = new Date();

		            kNow.setMilliseconds(0);

		            nTime = kNow.getTime();

		            try
		            {
		                sessionStorage.setItem("HMXVisitTime", String(nTime));
		            }
		            catch (kError)
		            {
		                console.error(kError);
		            }
		        }

		        return new Date(nTime);
		    }

		    static SetRange(nMinute)	// 기간: 지금 기준 nMinute 분 (D 에 있음: D 고름 → 쿼리 때 S·E, 없음: D 해제·S·E 직접 (여유 X초 포함))
		    {
		        kQueryRange.value = String(nMinute);

		        if (kQueryRange.selectedIndex > 0)
		        {
		            return;
		        }

		        kQueryRange.selectedIndex = 0;	// D 에 없음: 기타 (D 해제)

		        const kTo = new Date();	// 지금 (초 단위)

		        kTo.setMilliseconds(0);

		        Query.SetPadRange(kTo, nMinute);
		    }

		    static SetRecent()	// D 최근 기간 → E = 지금 (초 단위) + X초, S = E - D
		    {
		        if (kQueryRange.selectedIndex <= 0)	// D 해제 (S·E 직접): 기본 60분
		        {
		            kQueryRange.value = "60";
		        }

		        const kTo	= new Date();	// 지금 (초 단위)

		        kTo.setMilliseconds(0);

		        Query.SetPadRange(kTo, Number(kQueryRange.value));	// D 값: 분
		    }

		    static async StopRun(bQuery)	// 지금 탭 실행 중: 중단 뒤 끝날 때까지 (리셋·모래시계)
		    {
		        if (bQuery)	// 쿼리 중: 중단 (앞 결과 상태로 돌아감) 뒤 끝날 때까지 (페이지 버튼 쿼리 포함, 그 사이 새 쿼리: 다시 중단)
		        {
		            while (Query.bQuerying)
		            {
		                Query.StopQuery();

		                await new Promise(funcDone => setTimeout(funcDone, Query.RESET_WAIT_MS));
		            }

		            return;
		        }

		        let nStopTick = -Query.RESET_STOP_MS;	// 검색 중: 중지 (그때까지 결과만) 뒤 끝날 때까지 (중지 안 됨 (막 시작 등): X ms 마다 다시), 마지막 중지 요청 (ms)

		        while (Search.bSearching)
		        {
		            if (performance.now() - nStopTick >= Query.RESET_STOP_MS)
		            {
		                nStopTick = performance.now();

		                await Search.StopSearch();	// 이미 중지 요청: 그대로
		            }

		            await new Promise(funcDone => setTimeout(funcDone, Query.RESET_WAIT_MS));
		        }
		    }

		    static GetBack(bQuery)	// 그 탭 실행 기억 (쿼리·서치 따로, 맨 뒤: 지금 실행)
		    {
		        return bQuery ? Query.arrBackQuery : Query.arrBackSearch;
		    }

		    static PushBack(bQuery)	// 실행 버튼·Enter·Ctrl·Alt 놓기·필드 값 클릭 (자동 입력·실행): 지금 M·D·S·E·입력칸 기억 (앞 기억과 같음: 안 넣음, 최근 X회 넘음: 오래된 것 지움)
		    {
		        const arrBack	= Query.GetBack(bQuery);
		        const kState	= {
		            sKey:			(bQuery ? kQueryKey : kKey).value,	// 입력칸 그대로 (쿼리: 머신 조건 포함)
		            arrMachine:		bQuery ? [] : [...setMachineCheck].sort(),	// 서치 M 체크 (비면: 전체, 쿼리: 쿼리칸 머신 조건)
		            nRange:			kQueryRange.selectedIndex,
		            sStart:			kStart.value,
		            sStartHour:		kStartHour.value,
		            sStartMinute:	kStartMinute.value,
		            sStartSecond:	kStartSecond.value,
		            sEnd:			kEnd.value,
		            sEndHour:		kEndHour.value,
		            sEndMinute:		kEndMinute.value,
		            sEndSecond:		kEndSecond.value,
		        };

		        if (arrBack.length === 0 || JSON.stringify(arrBack.at(-1)) !== JSON.stringify(kState))
		        {
		            arrBack.push(kState);

		            if (arrBack.length > Query.BACK_MAX)
		            {
		                arrBack.shift();
		            }
		        }

		        Query.ShowBack();
		    }

		    static ShowBack()	// 모래시계 버튼: 지금 탭 돌아갈 실행 있음 → 켬 (없음: 꺼짐)
		    {
		        const nBack		= Query.GetBack(IsQueryMode()).length - 1;	// 돌아갈 수 있는 횟수 (지금 실행 뺌)
		        const kButton	= document.getElementById("ResetButton");

		        kButton.disabled	= nBack <= 0;
		        kButton.title		= `이전 실행(최대 ${Query.BACK_MAX}회)`;
		    }

		    static async GoBack()	// 모래시계 버튼: 이전 실행으로 → 지금 실행 기억 지움 (돌아간 뒤 기억 지움) → 앞 기억 M·D·S·E·입력칸 그대로 → 그 S·E 그대로 실행 (D 다시 계산 안 함, 기억 안 넣음)
		    //   실행 중: 중단 뒤, 지금 탭 타이머: 끔, 돌아갈 기억 없음·누르는 중 다시 누름: 무시
		    {
		        const bQuery	= IsQueryMode();	// 쿼리 탭 (아님: 서치 탭)
		        const arrBack	= Query.GetBack(bQuery);

		        if (Query.bResetting || arrBack.length < 2)
		        {
		            return;
		        }

		        const kItem		= GetTimer();							// 지금 탭 타이머
		        const kButton	= document.getElementById("ResetButton");	// 모래시계 뒤집기

		        Query.bResetting = true;

		        kButton.classList.remove("flip");
		        void kButton.offsetWidth;	// 뒤집기 처음부터 다시 (애니메이션 재시작)
		        kButton.classList.add("flip");

		        try
		        {
		            if (kItem != null && kItem.nId !== 0)	// 지금 탭 타이머 먼저 끔 (끝 기다리는 사이 타이머 실행 방지)
		            {
		                StopTimer(kItem);
		                ShowTimer();
		            }

		            await Query.StopRun(bQuery);

		            arrBack.pop();	// 지금 실행 기억 지움

		            const kState = arrBack.at(-1);	// 돌아갈 실행

		            kStart.value				= kState.sStart;
		            kStartHour.value			= kState.sStartHour;
		            kStartMinute.value			= kState.sStartMinute;
		            kStartSecond.value			= kState.sStartSecond;
		            kEnd.value					= kState.sEnd;
		            kEndHour.value				= kState.sEndHour;
		            kEndMinute.value			= kState.sEndMinute;
		            kEndSecond.value			= kState.sEndSecond;
		            kQueryRange.selectedIndex	= kState.nRange;

		            LimitRange();	// S·E 고르기 제한 다시

		            if (bQuery)	// 쿼리칸 (머신 조건 포함) → M 체크·저장 쿼리 선택 맞춤 → 그 기간 그대로 실행 (쿼리 버튼과 같음, D 다시 계산 안 함)
		            {
		                kQueryKey.value = kState.sKey;

		                Query.OnQueryInput();
		                Query.Query(kState.sKey.trim() || "*", Query.GetPicker(kStart, kStartHour, kStartMinute), Query.GetPicker(kEnd, kEndHour, kEndMinute), true, true);
		            }
		            else	// 검색어·M 체크 → 그 기간 그대로 실행 (서치 버튼과 같음, D 다시 계산 안 함)
		            {
		                kKey.value = kState.sKey;

		                setMachineCheck.clear();

		                for (const sMachine of kState.arrMachine)
		                {
		                    setMachineCheck.add(sMachine);
		                }

		                for (const kCheck of kMachineList.querySelectorAll("input[type=checkbox]"))
		                {
		                    kCheck.checked = setMachineCheck.has(kCheck.value);
		                }

		                ShowMachine();

		                Search.SearchLog(false, false, true);
		            }
		        }
		        finally
		        {
		            Query.bResetting = false;

		            Query.ShowBack();
		        }
		    }

		    static async ResetState()	// F5·새로 고침 버튼: 최초 입장 리셋 → 지금 탭 최초 입장 상태로 (M 전체·D 1일·S·E 세션 최초 방문 시각 기준·지금 탭 타이머 끔·기본 X초·입력칸 비움·결과 메모리·스텝·포지션) → 쿼리 탭: 최초 입장처럼 실행, 서치 탭: 실행 안 함
		    //   + 지금 탭 입력칸 비움·결과 메모리 (단계·보관 결과·렌즈 스냅샷)·스텝·포지션 처음으로 (실행 중: 중단 뒤), 그대로: 직접 저장한 설정 (저장 쿼리·알람·서치 설정: 머신당·전체 최대 건수·정렬)
		    {
		        if (Query.bResetting)	// 리셋 중 다시 누름: 무시 (앞 리셋이 시작한 쿼리·통계 지움 방지)
		        {
		            return;
		        }

		        const kItem		= GetTimer();		// 지금 탭 타이머
		        const bQuery	= IsQueryMode();	// 쿼리 탭 (아님: 서치 탭)

		        Query.bResetting = true;

		        try
		        {
		            if (kItem != null)	// 지금 탭 타이머 먼저 끔·기본 X초 (끝 기다리는 사이 타이머 실행 방지)
		            {
		                if (kItem.nId !== 0)
		                {
		                    StopTimer(kItem);
		                }

		                kItem.nSec = TIMER_SEC_DEFAULT;

		                ShowTimer();
		            }

		            await Query.StopRun(bQuery);

		            OnMachineAll();				// M 전체 (서치: 체크 해제, 쿼리: 쿼리칸 머신 조건 뺌)
		            Query.SetVisitRange();	// D 1일, E = 최초 방문 시각 + X초, S = E - 1일

		            if (bQuery)	// 입력칸 비움 + 결과 메모리·스텝·렌즈·포지션 처음으로
		            {
		                kQueryKey.value = "";

		                Query.ClearResult();
		                Query.RunQuery();	// 처음 입장처럼 실행 (D 1일 → 실행 때 지금 기준, 쿼리칸 빈칸: 전체)
		            }
		            else
		            {
		                kKey.value = "";

		                Search.ClearResult();
		            }
		        }
		        finally
		        {
		            Query.bResetting = false;
		        }
		    }

		    static SetPicker(kDate, kHour, kMinute, kTime)	// 날짜·시·분·초 선택에 시각 넣기 (초: 분 선택 짝 S·E 초)
		    {
		        const funcPad = (nValue) => String(nValue).padStart(2, "0");
		        const kSecond = Query.GetSecondPick(kMinute);

		        kDate.value		= `${kTime.getFullYear()}-${funcPad(kTime.getMonth() + 1)}-${funcPad(kTime.getDate())}`;
		        kHour.value		= funcPad(kTime.getHours());
		        kMinute.value	= funcPad(kTime.getMinutes());

		        if (kSecond != null)
		        {
		            kSecond.value = funcPad(kTime.getSeconds());
		        }

		        LimitRange();	// S·E 고르기 제한 다시
		    }

		    static GetPicker(kDate, kHour, kMinute)	// 날짜·시·분·초 선택 → 시각 (시 24: 다음 날 0시 + 분·초, 날짜 없음: null, 초: 분 선택 짝 S·E 초)
		    {
		        if (kDate.value === "")
		        {
		            return null;
		        }

		        const [nYear, nMonth, nDay] = kDate.value.split("-").map(Number);
		        const kSecond = Query.GetSecondPick(kMinute);

		        return new Date(nYear, nMonth - 1, nDay, Number(kHour.value), Number(kMinute.value), kSecond != null ? Number(kSecond.value) : 0);
		    }

		    static GetSecondPick(kMinute)	// 분 선택 짝 초 선택 (S 분 → S 초, E 분 → E 초, 그 외: null)
		    {
		        return kMinute === kStartMinute ? kStartSecond : (kMinute === kEndMinute ? kEndSecond : null);
		    }

		    static ToMinute(kTime)	// 시각 → yyyyMMddHHmm
		    {
		        const funcPad = (nValue) => String(nValue).padStart(2, "0");

		        return Number(`${kTime.getFullYear()}${funcPad(kTime.getMonth() + 1)}${funcPad(kTime.getDate())}${funcPad(kTime.getHours())}${funcPad(kTime.getMinutes())}`);
		    }

		    static ToSecond(kTime)	// 시각 → yyyyMMddHHmmss (초별 막대 클릭: 1초 기간)
		    {
		        return Query.ToMinute(kTime) * 100 + kTime.getSeconds();
		    }

		    static FormatHour(nHour)	// yyyyMMddHH → yyyy-MM-dd HH시
		    {
		        const sText = String(nHour);

		        return `${sText.slice(0, 4)}-${sText.slice(4, 6)}-${sText.slice(6, 8)} ${sText.slice(8)}시`;
		    }

		    static FormatMinute(nMinute)	// yyyyMMddHHmm → yyyy-MM-dd HH:mm (yyyyMMddHHmmss: HH:mm:ss)
		    {
		        const sText = String(nMinute);

		        return `${sText.slice(0, 4)}-${sText.slice(4, 6)}-${sText.slice(6, 8)} ${sText.slice(8, 10)}:${sText.slice(10, 12)}${sText.length > 12 ? `:${sText.slice(12)}` : ""}`;
		    }

		    static FormatSpan(nStart, nEnd)	// 기간 길이 (yyyyMMddHHmm·yyyyMMddHHmmss 시작·끝 → 막대 단위: 1분 이하 N초, 1시간 이하 N분, 24시간 이하 N시간 N분, 넘음: N일 N시간 N분, 0 인 단위 뺌)
		    {
		        const funcDate	= (nTime) => { const sText = String(nTime); return new Date(Number(sText.slice(0, 4)), Number(sText.slice(4, 6)) - 1, Number(sText.slice(6, 8)), Number(sText.slice(8, 10)), Number(sText.slice(10, 12)), Number(sText.slice(12, 14) || 0)); };	// yyyyMMddHHmm(ss) → 시각
		        const nSecond	= Math.max(0, Math.round((funcDate(nEnd) - funcDate(nStart)) / 1000));	// 기간 (초)
		        const nMinute	= Math.round(nSecond / 60);	// 기간 (분)

		        if (nSecond <= 60)	// 초별 (1분, 초별 막대 클릭: 1초)
		        {
		            return `${nSecond}초`;
		        }

		        if (nMinute <= 60)	// 분별
		        {
		            return `${nMinute}분`;
		        }

		        const arrPart	= [];
		        const nHour		= nMinute > 1440 ? Math.floor(nMinute % 1440 / 60) : Math.floor(nMinute / 60);	// 시간 (시간별: 24시간까지)

		        if (nMinute > 1440)	// 날짜별
		        {
		            arrPart.push(`${Math.floor(nMinute / 1440)}일`);
		        }

		        if (nHour > 0)
		        {
		            arrPart.push(`${nHour}시간`);
		        }

		        if (nMinute % 60 > 0)
		        {
		            arrPart.push(`${nMinute % 60}분`);
		        }

		        return arrPart.join(" ");
		    }

		    static async LoadPage(nPage, kPrev = null, kStep = Query.arrHistory[Query.nHistory])	// 쿼리 결과 nPage 페이지 (최신 순 nPageSize 건씩, 진행 초 표시, 중단: 앞 결과 그대로, kPrev: 중단·실패 때 되돌릴 상태, kStep: 결과 보관 단계)
		    {
		        if (Query.bQuerying)	// 쿼리 중
		        {
		            return;
		        }

		        const nBegin	= performance.now();		// 시작 (진행 초)
		        const kAbort	= new AbortController();	// 중단 (중단 버튼 → 서버도 남은 시간 폴더 안 읽음)

		        const funcShow = () =>	// 진행 표시 (1초마다): 쿼리 중 · N초
		        {
		            const nSec = Math.floor((performance.now() - nBegin) / 1000);	// 쿼리 시간 (초)

		            kQueryTitle.textContent		= `${Query.FormatSpan(Query.nStart, Query.nEnd)} - 쿼리 중 · ${nSec}초`;
		            kQueryRunButton.textContent	= `${nSec}초`;	// 쿼리 실행 버튼: 진행 초 (끝: 실행)
		            kQueryProgress.textContent	= `쿼리 중 · ${nSec}초\n"${Query.sQueryKey}"\n${Query.FormatMinute(Query.nStart)} ~ ${Query.FormatMinute(Query.nEnd)}`;
		        };

		        Query.bQuerying			= true;
		        Query.nChunkSeq++;	// 앞 페이지 이어 읽기 응답 버림
		        Query.kQueryAbort			= kAbort;
		        kQueryRunButton.disabled	= true;
		        kQueryStopButton.disabled	= false;

		        funcShow();

		        const nTimer = setInterval(funcShow, 1000);	// 진행 초 갱신
		        const nPopup = setTimeout(() => { funcShow(); kQueryProgress.hidden = false; }, PROGRESS_DELAY_MS);	// 팝업: X초 이상 걸릴 때만 (정확히 X초)

		        kQueryProgress.hidden = true;	// 앞 팝업 (렌즈) 닫음 → X초 뒤 다시

		        try
		        {
		            const kResponse = await fetch("/api/query?" + new URLSearchParams({ sKey: Query.sQueryKey, nStart: String(Query.nStart), nEnd: String(Query.nEnd), nPage: String(nPage), nOffset: "0", nCount: String(Query.CHUNK), nLimit: String(nQueryLimitSec) }), { signal: kAbort.signal });	// 페이지 처음 CHUNK 건 (나머지: 아래로 내리면 LoadMore)

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData = await kResponse.json();

		            Query.ShowPage(kData);

		            if (kData.sError === "")	// 이 단계 마지막 본 결과 (← →·새 창: 그대로)
		            {
		                Query.SaveStepResult(kStep, { kPage: kData });
		            }
		        }
		        catch (kError)	// 중단·요청 실패 (화면은 앞 결과 그대로, 새 쿼리: 상태도 앞 결과로)
		        {
		            kQueryTitle.textContent = kError.name === "AbortError" ? `${Query.FormatSpan(Query.nStart, Query.nEnd)} - 쿼리 중단 (${Math.floor((performance.now() - nBegin) / 1000)}초)` : `${Query.FormatSpan(Query.nStart, Query.nEnd)} - 쿼리 실패: ${ErrorText(kError)}`;

		            if (kPrev != null)	// 새 쿼리: 앞 결과 상태로 (페이지·막대·클릭이 보이는 결과 기준)
		            {
		                Query.kStatAbort?.abort();	// 새 쿼리 통계 끊음 (왼쪽 값 수: -)

		                Object.assign(Query, kPrev);

		                Query.ShowHistory();
		            }
		        }
		        finally
		        {
		            clearInterval(nTimer);
		            clearTimeout(nPopup);

		            Query.bQuerying			= false;
		            Query.kQueryAbort			= null;
		            kQueryRunButton.disabled	= false;
		            kQueryRunButton.textContent	= "실행";
		            kQueryStopButton.disabled	= Query.kStatAbort == null;	// 통계 아직 가져오는 중: 중단 가능
		            kQueryProgress.hidden		= true;
		        }
		    }

		    static ShowPage(kData)	// 쿼리 결과 페이지 그리기 (막대·처음 줄·페이지 번호·제목, 쿼리 응답·단계 보관 결과)
		    {
		        Query.nPage		= kData.nPage;
		        Query.nPageSize	= kData.nPageSize;
		        Query.nTotal		= kData.nTotal;

		        Query.nRowEnd		= kData.arrLine.length < Query.CHUNK ? kData.arrLine.length : -1;	// 처음 CHUNK 건보다 덜 옴: 페이지 끝

		        const kStep = Query.arrHistory[Query.nHistory];	// 지금 단계 (렌즈 초·분봉 스냅샷 보관)

		        if (kStep != null)
		        {
		            kStep.arrLens ??= [null, null];
		        }

		        Query.kChartData = { arrHour: kData.arrHour, arrMinute: kData.arrMinute, arrSecond: kData.arrSecond ?? [], arrLens: kStep?.arrLens ?? (Query.nLens > 0 ? Query.kChartData.arrLens : [null, null]) };	// 초·분봉: 단계 보관분 (페이지 넘김·단계 이동: 다시 안 가져옴)

		        Query.ShowChart();
		        Query.ShowRows(kData.arrLine);
		        Query.ShowPager();

		        kQueryLogTitle.textContent	= kData.nTotal === 0 ? "로그 없음" : `로그 ${(kData.nPage * kData.nPageSize + 1).toLocaleString()} ~ ${Math.min(kData.nTotal, (kData.nPage + 1) * kData.nPageSize).toLocaleString()} / ${kData.nTotal.toLocaleString()}건 (최신 순)`;

		        SetResultTitle(kQueryTitle, kData.sError !== "" ? `${Query.FormatSpan(kData.nStart, kData.nEnd)} - 쿼리 실패: ${kData.sError}` : `${Query.FormatSpan(kData.nStart, kData.nEnd)} - 쿼리 결과: ${kData.nTotal.toLocaleString()}건 · 쿼리 시간: ${kData.nElapsedMs} ms`);	// 기간 길이만 (쿼리·S·E 는 입력칸), 건수·시간 숫자: 존 색
		    }

		    static StopQuery()	// 중단 버튼: 쿼리·통계 요청 끊음 (서버도 남은 시간 폴더 안 읽음, 화면은 앞 결과 그대로)
		    {
		        Query.kQueryAbort?.abort();
		        Query.kStatAbort?.abort();
		    }

		    static GetSavedPath(sTail = "")	// 저장 쿼리·저장 서치 API 경로 (지금 설정 창 기능, sTail: /delete·/move)
		    {
		        return (Query.bSearch ? "/api/search/saved" : "/api/query/saved") + sTail;
		    }

		    static GetSavedName()	// 저장 쿼리·저장 서치 이름 (알림·삭제 확인)
		    {
		        return Query.bSearch ? "저장 서치" : "저장 쿼리";
		    }

		    static GetSavedList(kData)	// 응답 목록 (arrQuery·arrSearch)
		    {
		        return Query.bSearch ? kData.arrSearch : kData.arrQuery;
		    }

		    static async LoadSaved(sShowKey = "", bSearch = false)	// 저장 쿼리·저장 서치 목록 가져와 설정 창 그리기 (서버: 설정 파일 다시 읽음, sShowKey: 그 제목 줄 보이게, 실패: 빈 목록)
		    {
		        try
		        {
		            const kResponse = await fetch(bSearch ? "/api/search/saved" : "/api/query/saved");

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData = await kResponse.json();

		            Query.ShowSaved(bSearch ? kData.arrSearch : kData.arrQuery, sShowKey, bSearch);
		        }
		        catch (kError)	// 요청 실패
		        {
		            Query.ShowSaved([], "", bSearch);
		        }
		    }

		    static async LoadAlarm()	// 알람 목록 가져와 쿼리 설정 알람 탭 그리기 (실패: 빈 목록)
		    {
		        try
		        {
		            const kResponse = await fetch("/api/alarm");

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            Query.ShowAlarm((await kResponse.json()).arrAlarm);
		        }
		        catch (kError)	// 요청 실패
		        {
		            Query.ShowAlarm([]);
		        }
		    }

		    static async OpenAlarmHistory()	// 알람 기록 창 열기 (기록 버튼, 열 때마다 새로) → 전체 줄 최신 순 (실패: 알림)
		    {
		        try
		        {
		            const kResponse = await fetch("/api/alarm/history");

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            Query.ShowAlarmHistory(await kResponse.json());
		        }
		        catch (kError)	// 요청 실패
		        {
		            alert(`알람 기록 실패: ${ErrorText(kError)}`);

		            return;
		        }

		        if (!kAlarmHistoryDialog.open)
		        {
		            kAlarmHistoryDialog.showModal();
		        }
		    }

		    static ShowAlarmHistory(kData)	// 알람 기록 그리기: 전체 줄 (날짜·제목·채널·결과, 실패: 빨강·툴팁 사유, 제목 클릭: 내용 창, 넘침: 스크롤), 없음: 기록 없음
		    {
		        Query.arrAlarmHistory = kData.arrHistory;

		        const arrRow = kData.arrHistory.map((kHistory, nIndex) =>
		        {
		            const kRow		= document.createElement("tr");
		            const kTime		= kRow.insertCell();	// 날짜 (yyyy-MM-dd HH:mm:ss)
		            const kKey		= kRow.insertCell();	// 제목
		            const kChannel	= kRow.insertCell();	// 채널
		            const kResult	= kRow.insertCell();	// 결과

		            kRow.dataset.index		= String(nIndex);	// 목록 위치
		            kTime.textContent		= kHistory.sDone;
		            kKey.textContent		= kHistory.sTitle;
		            kKey.className			= "key";
		            kChannel.textContent	= Query.GetChannelName(kHistory.sChannel);
		            kResult.textContent		= Query.GetResultName(kHistory.sResult);
		            kResult.title			= kHistory.sError;
		            kResult.className		= kHistory.sResult === "Failed" ? "fail" : "";

		            return kRow;
		        });

		        if (arrRow.length === 0)	// 기록 없음
		        {
		            const kRow	= document.createElement("tr");
		            const kCell	= kRow.insertCell();

		            kCell.colSpan		= 4;
		            kCell.className		= "empty";
		            kCell.textContent	= "기록 없음";

		            arrRow.push(kRow);
		        }

		        kAlarmHistoryList.replaceChildren(...arrRow);

		        kAlarmHistoryScroll.scrollTop = 0;
		    }

		    static OnAlarmHistoryClick(kEvent)	// 알람 기록 제목 클릭: 내용 창 (제목·시각·채널·결과·사유·내용)
		    {
		        const kHistory = Query.arrAlarmHistory[Number(kEvent.target.closest("td.key")?.parentElement.dataset.index ?? -1)];	// 누른 줄 (제목 아님: 없음)

		        if (kHistory == null)
		        {
		            return;
		        }

		        const arrLine	= kHistory.sText.split("\n");	// 내용 줄
		        const funcBar	= (sLine) => /^─+$/.test(sLine ?? "");	// 구분 줄 (메일: <hr>)

		        Query.sAlarmHistoryLink = kHistory.sLink ?? "";	// 내용 클릭 쿼리 링크 (안 보임)

		        if (/^https?:\/\//.test(arrLine.at(-1)))	// 예전 기록: 내용 끝 주소 줄 → 링크로만 (줄·앞 빈 줄 안 보임)
		        {
		            Query.sAlarmHistoryLink ||= arrLine.at(-1);

		            arrLine.pop();

		            while (arrLine.length > 1 && arrLine.at(-1) === "")
		            {
		                arrLine.pop();
		            }
		        }

		        kAlarmHistoryViewTitle.textContent	= kHistory.sTitle;
		        kAlarmHistoryViewInfo.textContent	= [kHistory.sDone, Query.GetChannelName(kHistory.sChannel), Query.GetResultName(kHistory.sResult) + (kHistory.sError !== "" ? ` (${kHistory.sError})` : "")].join(" · ");

		        kAlarmHistoryViewBody.classList.toggle("link", Query.sAlarmHistoryLink !== "");
		        kAlarmHistoryViewBody.replaceChildren(...arrLine.flatMap((sLine, nIndex) =>	// 내용 (구분 줄: 가로줄 끝까지)
		        {
		            const kLine = funcBar(sLine) ? document.createElement("hr") : document.createTextNode(sLine);

		            return nIndex === 0 || funcBar(sLine) || funcBar(arrLine[nIndex - 1]) ? [kLine] : ["\n", kLine];	// 가로줄 앞뒤: 줄바꿈 없음
		        }));

		        kAlarmHistoryViewDialog.showModal();

		        kAlarmHistoryViewBody.scrollTop = 0;
		    }

		    static async RunAlarmLink()	// 알람 기록 내용 클릭: 창 닫고 링크 기간 (S·E, D 해제)·쿼리 넣고 바로 쿼리 (쿼리 중: 중단 후, 초: 00, 링크 없음·글자 고르는 중: 무시)
		    {
		        if (Query.sAlarmHistoryLink === "" || window.getSelection().toString() !== "")	// 끌어서 고르기 (복사): 그대로
		        {
		            return;
		        }

		        const kParam = new URL(Query.sAlarmHistoryLink).searchParams;	// 링크 입력칸 (쿼리·S·E·D)

		        kAlarmHistoryViewDialog.close();
		        kAlarmHistoryDialog.close();

		        for (const sId of OPEN_STATE_INPUT.filter(sItem => sItem !== "SearchKey" && kParam.has(sItem)))
		        {
		            document.getElementById(sId).value = kParam.get(sId);
		        }

		        kStartSecond.value	= kParam.get("StartSecond") ?? "00";	// 링크: 분 단위
		        kEndSecond.value	= kParam.get("EndSecond") ?? "00";
		        Query.sSavedKey		= "";

		        ShowStatus();

		        if (Query.bQuerying)	// 쿼리 중: 중단 (앞 결과 상태로 돌아감) 뒤 이어서
		        {
		            Query.StopQuery();

		            await Query.kQueryTask;
		        }

		        Query.RunQuery();
		    }

		    static GetChannelName(sChannel)	// 채널 표시 (Mail: 메일, Hook: 훅)
		    {
		        return { Mail: "메일", Hook: "훅" }[sChannel] ?? sChannel;
		    }

		    static GetResultName(sResult)	// 결과 표시 (Sent: 발송, Resent: 재발송, Failed: 실패)
		    {
		        return { Sent: "발송", Resent: "재발송", Failed: "실패" }[sResult] ?? sResult;
		    }

		    static ShowSaved(arrItem, sShowKey = "", bSearch = false)	// 저장 쿼리·저장 서치 목록 바꿈 (bSearch: Search 에 둠, sShowKey: 그 제목 줄 보이게, 선택한 제목 없어짐: 선택 해제) → 그 기능 창이면 다시 그림
		    {
		        const kOwner = bSearch ? Search : Query;	// 목록·선택 제목 둘 곳

		        kOwner.arrSaved = arrItem;

		        if (!arrItem.some(kSaved => kSaved.sKey === kOwner.sSavedKey))
		        {
		            kOwner.sSavedKey = "";
		        }

		        if (!Query.bAlarm && !Query.bRule && Query.bSearch === bSearch)
		        {
		            Query.ShowEditList(arrItem.findIndex(kSaved => kSaved.sKey === sShowKey));
		        }
		    }

		    static ShowAlarm(arrAlarm, nShowID = 0)	// 알람 목록 바꿈 (nShowID: 그 알람 줄 보이게) → 알람 탭이면 다시 그림
		    {
		        Query.arrAlarm = arrAlarm;

		        if (Query.bAlarm)
		        {
		            Query.ShowEditList(arrAlarm.findIndex(kAlarm => kAlarm.nID === nShowID));
		        }
		    }

		    static ShowRule(arrField, sShowKey = "")	// 필드 정의 목록 바꿈 (sShowKey: 그 이름 줄 보이게) → 필드 탭이면 다시 그림
		    {
		        Query.arrRule = arrField;

		        if (Query.bRule)
		        {
		            Query.ShowEditList(arrField.findIndex(kRule => kRule.sKey === sShowKey));
		        }
		    }

		    static async LoadRule()	// 필드 정의 목록 가져와 쿼리 설정 필드 탭 그리기 (실패: 빈 목록)
		    {
		        try
		        {
		            const kResponse = await fetch("/api/field");

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            Query.ShowRule((await kResponse.json()).arrField);
		        }
		        catch (kError)	// 요청 실패
		        {
		            Query.ShowRule([]);
		        }
		    }

		    static GetRuleText(kRule)	// 필드 정의 설명 (고정값: "고정값 값", 정규식: "정규식 식")
		    {
		        return kRule.sValue !== "" ? `고정값 ${kRule.sValue}` : `정규식 ${kRule.sRegex}`;
		    }

		    static GetEditItems()	// 쿼리 설정 지금 탭 목록 (저장 쿼리·알람·필드 정의)
		    {
		        return Query.bRule ? Query.arrRule : (Query.bAlarm ? Query.arrAlarm : (Query.bSearch ? Search.arrSaved : Query.arrSaved));
		    }

		    static ShowSettingTab(bAlarm, bRule = false, bSearch = false)	// 쿼리 설정 창 기능: 저장 (저장 쿼리·저장 서치)·알람·필드 (필드 정의, 누른 버튼 하나만, 제목: 버튼 이름, 같은 줄·추가 틀, 맨 위부터)
		    {
		        Query.bAlarm	= bAlarm && !bRule && !bSearch;
		        Query.bRule	= bRule && !bSearch;
		        Query.bSearch	= bSearch;

		        kQuerySettingTitle.textContent = bRule ? "필드" : (bAlarm ? "알람" : "저장");

		        kQueryEditAdd.title = bSearch ? "서치 추가" : (bRule ? "필드 추가" : (bAlarm ? "알람 추가" : "쿼리 추가"));
		        kQueryEditScroll.scrollTop = 0;

		        Query.ShowEditList();
		    }

		    static ShowEditList(nShow = -1)	// 쿼리 설정 지금 탭 줄 그리기 (전체, nShow: 그 줄 보이게, 더블클릭: 실행 (필드: 수정), 드래그: 순서, 쿼리: 선택 강조, 알람: 꺼짐 흐리게, 필드: 이름만 (정규식·고정값: 툴팁))
		    {
		        const arrItem = Query.GetEditItems();

		        const arrRow = arrItem.map((kItem, nIndex) =>
		        {
		            const kRow	= document.createElement("tr");
		            const kKey	= kRow.insertCell();	// 제목
		            const kAct	= kRow.insertCell();	// 수정·삭제

		            kRow.dataset.index	= String(nIndex);	// 목록 위치
		            kRow.draggable		= true;
		            kRow.className		= Query.bRule ? "" : (Query.bAlarm ? (kItem.bEnable ? "" : "off") : (kItem.sKey === (Query.bSearch ? Search.sSavedKey : Query.sSavedKey) ? "sel" : ""));
		            kKey.textContent	= kItem.sKey;
		            kKey.title			= Query.bRule ? Query.GetRuleText(kItem) : kItem.sQuery;	// 쿼리 (필드: 정규식·고정값)
		            kAct.className		= "act";

		            for (const [sAction, sText] of [["edit", "수정"], ["delete", "삭제"]])
		            {
		                const kButton = document.createElement("button");

		                kButton.type			= "button";
		                kButton.textContent		= sText;
		                kButton.dataset.action	= sAction;

		                kAct.append(kButton);
		            }

		            return kRow;
		        });	// 없음: 빈 상자

		        kQueryEditList.replaceChildren(...arrRow);

		        kQueryEditList.rows[nShow]?.scrollIntoView({ block: "nearest" });
		    }

		    static OnSavedClick(kEvent)	// 쿼리 설정 줄: 더블클릭 → 쿼리칸에 넣고 실행 (버튼 더블클릭 제외), 버튼 → 수정·삭제
		    {
		        const kRow		= kEvent.target.closest("tr[data-index]");	// 누른 줄
		        const kItem		= kRow == null ? undefined : Query.GetEditItems()[Number(kRow.dataset.index)];
		        const sAction	= kEvent.target.closest("button[data-action]")?.dataset.action;	// 누른 버튼 (없음: 제목)

		        if (kItem === undefined)
		        {
		            return;
		        }

		        if (kEvent.type === "dblclick")
		        {
		            if (sAction === undefined && Query.bRule)	// 필드: 실행 없음 → 수정
		            {
		                Query.OpenRule(kItem);
		            }
		            else if (sAction === undefined)
		            {
		                Query.ChooseSaved(kItem);
		            }
		        }
		        else if (sAction === "edit")
		        {
		            if (Query.bRule)
		            {
		                Query.OpenRule(kItem);
		            }
		            else if (Query.bAlarm)
		            {
		                Query.OpenAlarm(kItem);
		            }
		            else
		            {
		                Query.OpenSaved(kItem);
		            }
		        }
		        else if (sAction === "delete")
		        {
		            if (Query.bRule)
		            {
		                Query.DeleteRule(kItem);
		            }
		            else if (Query.bAlarm)
		            {
		                Query.DeleteAlarm(kItem);
		            }
		            else
		            {
		                Query.DeleteSaved(kItem);
		            }
		        }
		    }

		    static OnEditDragStart(kEvent)	// 쿼리 설정 줄 드래그 시작: 그 줄 위치 기억
		    {
		        const kRow = kEvent.target.closest?.("tr[data-index]");	// 끄는 줄

		        if (kRow == null)
		        {
		            return;
		        }

		        Query.nDragIndex = Number(kRow.dataset.index);

		        kEvent.dataTransfer.effectAllowed = "move";
		        kEvent.dataTransfer.setData("text/plain", kRow.cells[0].textContent);

		        kRow.classList.add("drag");
		    }

		    static GetDropIndex(kEvent)	// 쿼리 설정 놓을 자리 (줄 위쪽 반: 그 줄 앞, 아래쪽 반·줄 밖: 뒤, 반환: { kRow, bAfter, nInsert }, 줄 없음: null)
		    {
		        const kOver	= kEvent.target.closest?.("tr[data-index]");									// 마우스 아래 줄
		        const kRow	= kOver ?? [...kQueryEditList.querySelectorAll("tr[data-index]")].at(-1);	// 놓을 줄 (줄 밖: 마지막 줄)

		        if (kRow == null)
		        {
		            return null;
		        }

		        const kRect		= kRow.getBoundingClientRect();
		        const bAfter	= kOver == null || kEvent.clientY > kRect.top + kRect.height / 2;

		        return { kRow, bAfter, nInsert: Number(kRow.dataset.index) + (bAfter ? 1 : 0) };
		    }

		    static OnEditDragOver(kEvent)	// 쿼리 설정 드래그 중: 놓을 자리 표시 (줄 앞·뒤 존 색 선)
		    {
		        if (Query.nDragIndex < 0)	// 설정 줄 드래그 아님
		        {
		            return;
		        }

		        kEvent.preventDefault();
		        kEvent.dataTransfer.dropEffect = "move";

		        Query.ClearDrop();

		        const kDrop = Query.GetDropIndex(kEvent);

		        kDrop?.kRow.classList.add(kDrop.bAfter ? "dropafter" : "dropbefore");
		    }

		    static ClearDrop()	// 쿼리 설정 놓을 자리 표시 지움
		    {
		        document.querySelectorAll("#QueryEditList .dropbefore, #QueryEditList .dropafter").forEach(kItem => kItem.classList.remove("dropbefore", "dropafter"));
		    }

		    static OnEditDrop(kEvent)	// 쿼리 설정 놓음: 줄 → 그 자리
		    {
		        const nFrom = Query.nDragIndex;	// 옮길 줄

		        if (nFrom < 0)
		        {
		            return;
		        }

		        kEvent.preventDefault();

		        const kDrop = Query.GetDropIndex(kEvent);	// 줄 자리

		        Query.OnEditDragEnd();

		        if (kDrop != null)
		        {
		            Query.MoveEdit(nFrom, kDrop.nInsert > nFrom ? kDrop.nInsert - 1 : kDrop.nInsert);
		        }
		    }

		    static OnEditDragEnd()	// 쿼리 설정 드래그 끝 (놓음·취소): 표시 지움
		    {
		        Query.nDragIndex = -1;

		        Query.ClearDrop();

		        kQueryEditList.querySelector("tr.drag")?.classList.remove("drag");
		    }

		    static async MoveEdit(nFrom, nTo)	// 쿼리 설정 줄 nFrom → nTo 자리 (서버 목록·설정 저장, 같은 자리: 안 함) → 옮긴 줄 보이게
		    {
		        const kItem = Query.GetEditItems()[nFrom];	// 옮길 저장 쿼리·알람

		        if (kItem === undefined || nFrom === nTo)
		        {
		            return;
		        }

		        if (Query.bRule)
		        {
		            const kData = await Query.PostSaved("/api/field/move", { sKey: kItem.sKey, nTo }, "필드");

		            if (kData != null)
		            {
		                Query.ShowRule(kData.arrField, kItem.sKey);
		            }
		        }
		        else if (Query.bAlarm)
		        {
		            const kData = await Query.PostSaved("/api/alarm/move", { nID: kItem.nID, nTo }, "알람");

		            if (kData != null)
		            {
		                Query.ShowAlarm(kData.arrAlarm, kItem.nID);
		            }
		        }
		        else
		        {
		            const kData = await Query.PostSaved(Query.GetSavedPath("/move"), { sKey: kItem.sKey, nTo }, Query.GetSavedName());

		            if (kData != null)
		            {
		                Query.ShowSaved(Query.GetSavedList(kData), kItem.sKey, Query.bSearch);
		            }
		        }
		    }

		    static OnQueryInput()	// 쿼리칸 고침: 선택한 저장 쿼리와 달라지면 선택 해제, M 글자·체크 맞춤 (쿼리칸 머신 조건)
		    {
		        const kSaved = Query.arrSaved.find(kItem => kItem.sKey === Query.sSavedKey);

		        if (kSaved !== undefined && kQueryKey.value.trim() !== kSaved.sQuery)
		        {
		            Query.sSavedKey = "";
		        }

		        SyncQueryMachine();
		    }

		    static async ChooseSaved(kItem)	// 쿼리 설정 제목 더블클릭: 창 닫고 쿼리칸·기간 넣고 바로 쿼리 (쿼리 중: 중단 후), 저장 서치: 검색어칸·기간 → 서치 (서치 중: 중지 → 끝난 뒤 다시)
		    {
		        if (Query.bSearch)
		        {
		            Search.sSavedKey	= kItem.sKey;
		            kKey.value			= kItem.sQuery;

		            kQuerySettingDialog.close();

		            Query.SetRange(kItem.nMinute);

		            if (Search.bSearching)
		            {
		                Search.bRunAfterStop = true;

		                Search.StopSearch();

		                return;
		            }

		            Search.SearchLog();

		            return;
		        }

		        Query.sSavedKey	= Query.bAlarm ? "" : kItem.sKey;
		        kQueryKey.value		= kItem.sQuery;

		        kQuerySettingDialog.close();

		        Query.SetRange(kItem.nMinute);

		        if (Query.bQuerying)	// 쿼리 중: 중단 (앞 결과 상태로 돌아감) 뒤 이어서
		        {
		            Query.StopQuery();

		            await Query.kQueryTask;
		        }

		        Query.RunQuery();
		    }

		    static OpenSaved(kSaved)	// 쿼리 추가·수정 창 열기 (kSaved: 수정할 저장 쿼리, null: 추가 → 빈칸·기본 간격)
		    {
		        Query.sEditKey			= kSaved?.sKey ?? "";
		        kQuerySaveTitle.textContent	= `${Query.bSearch ? "서치" : "쿼리"} ${kSaved == null ? "추가" : "수정"}`;
		        kQuerySaveQueryLabel.textContent	= Query.bSearch ? "입력" : "쿼리";
		        kQuerySaveKey.value			= kSaved?.sKey ?? "";
		        kQuerySaveQuery.value		= kSaved?.sQuery ?? "";
		        kQuerySaveMinute.value		= String(kSaved?.nMinute ?? Query.SAVED_MINUTE);	// 간격 (추가: 기본)

		        kQuerySaveDialog.classList.remove("alarm");
		        kQuerySaveDialog.showModal();
		    }

		    static OpenAlarm(kAlarm)	// 알람 추가·수정 창 열기 (쿼리 추가 창 + 알람 칸, kAlarm: 수정할 알람 → 그 값, null: 추가 → 기본값)
		    {
		        Query.nEditID				= kAlarm?.nID ?? 0;
		        kQuerySaveTitle.textContent	= kAlarm == null ? "알람 추가" : "알람 수정";
		        kQuerySaveQueryLabel.textContent	= "쿼리";	// 서치 저장 창 뒤 "검색어" 되돌림
		        kQuerySaveKey.value			= kAlarm?.sKey ?? "";
		        kQuerySaveQuery.value		= kAlarm?.sQuery ?? "";
		        kAlarmMinute.value			= String(kAlarm?.nMinute ?? Query.ALARM_MINUTE);
		        kAlarmCount.value			= String(kAlarm?.nCount ?? Query.ALARM_COUNT);
		        kAlarmDelay.value			= String(kAlarm?.nDelay ?? Query.ALARM_DELAY);
		        kAlarmHook.value			= kAlarm?.sHook ?? "";
		        Query.arrAlarmMail		= [...(kAlarm?.aMail ?? [])];
		        Query.arrAlarmSMS			= [...(kAlarm?.aSMS ?? [])];
		        kAlarmEnable.checked		= kAlarm?.bEnable ?? true;

		        Query.ShowAlarmList(kAlarmMailList, Query.arrAlarmMail);
		        Query.ShowAlarmList(kAlarmSMSList, Query.arrAlarmSMS);
		        Query.LoadAlarmAccount();	// 메일 받는 계정 고르기 (열 때마다 새로)

		        kQuerySaveDialog.classList.add("alarm");
		        kQuerySaveDialog.showModal();
		    }

		    static CheckSaved(kKeyInput, kQueryInput)	// 추가·수정 입력 검사 (제목·쿼리 필요, 쿼리: 검색어 규칙, 줄바꿈 → 빈칸, 반환: { sKey, sQuery }, 오류: 알림 → null)
		    {
		        const sKey		= kKeyInput.value.trim();									// 제목
		        const sQuery	= kQueryInput.value.replace(/\s*[\r\n]+\s*/g, " ").trim();	// 쿼리 (줄바꿈: 빈칸)

		        if (sKey === "")
		        {
		            alert("제목을 입력해 주세요.");
		            kKeyInput.focus();

		            return null;
		        }

		        const sWord = Query.bSearch ? "검색어" : "쿼리";	// 알림 이름

		        if (sQuery === "")
		        {
		            alert(`${sWord}를 입력해 주세요.`);
		            kQueryInput.focus();

		            return null;
		        }

		        if (!Key.IsValid(sQuery))
		        {
		            alert(`${sWord} 형식을 확인해 주세요.\nAND·OR·NOT (대소문자 무시) 앞뒤에 검색어, 괄호 안에 검색어, 앞뒤 * 와일드카드 (예: A AND (B NOT C) OR D, ab*·*ab·*ab*)`);
		            kQueryInput.focus();

		            return null;
		        }

		        return { sKey, sQuery };
		    }

		    static CheckAlarm()	// 알람 칸 검사: 간격·건수·억제 정수 범위, 훅 http·https, 메일·SMS 입력칸에 남은 값: 추가 (반환: 알람 값, 오류: 알림·그 칸으로 → null)
		    {
		        const kAlarm	= { nMinute: Number(kAlarmMinute.value), nCount: Number(kAlarmCount.value), nDelay: Number(kAlarmDelay.value), sHook: kAlarmHook.value.trim(), bEnable: kAlarmEnable.checked };
		        const arrCheck	=	// [틀림, 칸, 알림]
		        [
		            [!Number.isInteger(kAlarm.nMinute) || kAlarm.nMinute < 1 || kAlarm.nMinute > Query.ALARM_MINUTE_MAX, kAlarmMinute, `간격을 1 ~ ${Query.ALARM_MINUTE_MAX} 분으로 입력해 주세요.`],
		            [!Number.isInteger(kAlarm.nCount) || kAlarm.nCount < 1, kAlarmCount, "건수를 1 이상으로 입력해 주세요."],
		            [!Number.isInteger(kAlarm.nDelay) || kAlarm.nDelay < 0 || kAlarm.nDelay > Query.ALARM_DELAY_MAX, kAlarmDelay, `억제를 0 ~ ${Query.ALARM_DELAY_MAX} 분으로 입력해 주세요.`],
		            [kAlarm.sHook !== "" && !/^https?:\/\/\S+$/i.test(kAlarm.sHook), kAlarmHook, "훅 주소를 http·https 로 입력해 주세요."],
		        ];
		        const arrWrong	= arrCheck.find(arrItem => arrItem[0]);	// 처음 틀린 칸

		        if (arrWrong !== undefined)
		        {
		            alert(arrWrong[2]);
		            arrWrong[1].focus();

		            return null;
		        }

		        if (!Query.AddAlarmMail() || !Query.AddAlarmSMS())	// 입력칸에 남은 메일·SMS: 추가 (오류: 알림)
		        {
		            return null;
		        }

		        return { ...kAlarm, aMail: [...Query.arrAlarmMail], aSMS: [...Query.arrAlarmSMS] };
		    }

		    static AddAlarmMail()	// 알람 메일 추가 버튼·Enter: 고른 계정 → 목록 끝에 (반환: 성공·안 고름 true, 넘음: false)
		    {
		        return Query.AddAlarmAccount(kAlarmMail, kAlarmMailList, Query.arrAlarmMail, "메일");
		    }

		    static AddAlarmAccount(kSelect, kList, arrItem, sName)	// 알람 메일·SMS 고른 계정 → 목록 끝에 (겹침: 안 넣음, 최대 개수, 고르기 비움, 반환: 성공·안 고름 true, 넘음: 알림 → false)
		    {
		        const sItem = kSelect.value;	// 고른 계정 아이디

		        if (sItem === "")
		        {
		            return true;
		        }

		        if (!arrItem.includes(sItem))
		        {
		            if (arrItem.length >= Query.ALARM_LIST_MAX)
		            {
		                alert(`${sName} 받는 계정은 최대 ${Query.ALARM_LIST_MAX}개입니다.`);
		                kSelect.focus();

		                return false;
		            }

		            arrItem.push(sItem);

		            Query.ShowAlarmList(kList, arrItem);
		        }

		        kSelect.value = "";

		        return true;
		    }

		    static async LoadAlarmAccount()	// 알람 메일·SMS 받는 계정 고르기 채움 (메일: 메일 있는 계정 아이디, SMS: 전화 번호 있는 계정 아이디, 실패: 빈 목록)
		    {
		        let arrAccount = [];	// [{sID, sMail}]

		        try
		        {
		            const kResponse = await fetch("/api/alarm/account");

		            if (kResponse.ok)
		            {
		                arrAccount = (await kResponse.json()).arrAccount;
		            }
		        }
		        catch (kError)	// 요청 실패: 빈 목록
		        {
		        }

		        arrAccount.sort((kLeft, kRight) => kLeft.sID.localeCompare(kRight.sID, undefined, { numeric: true }));	// 아이디 오름차순

		        kAlarmMail.replaceChildren(new Option("", ""), ...arrAccount.filter(kAccount => kAccount.sMail !== "").map(kAccount => new Option(kAccount.sID, kAccount.sID)));
		        kAlarmSMS.replaceChildren(new Option("", ""), ...arrAccount.filter(kAccount => kAccount.sPhone !== "").map(kAccount => new Option(kAccount.sID, kAccount.sID)));	// 전화 번호 있는 계정만
		    }

		    static AddAlarmSMS()	// 알람 SMS 추가 버튼·Enter: 고른 계정 → 목록 끝에 (반환: 성공·안 고름 true, 넘음: false)
		    {
		        return Query.AddAlarmAccount(kAlarmSMS, kAlarmSMSList, Query.arrAlarmSMS, "문자");
		    }

		    static ShowAlarmList(kList, arrItem)	// 알람 메일·SMS 받는 계정 그리기 (고르기 밑, 오름차순 한 줄 둥근 네모 "ne11 X", X 클릭: 뺌, 없음: 숨김)
		    {
		        arrItem.sort((sLeft, sRight) => sLeft.localeCompare(sRight, undefined, { numeric: true }));	// 오름차순 (숫자: 크기 순)

		        kList.replaceChildren(...arrItem.map((sItem, nIndex) =>
		        {
		            const kItem		= document.createElement("span");	// 계정 (둥근 네모)
		            const kDelete	= document.createElement("button");	// 계정 옆 X: 뺌

		            kItem.className		= "alarmitem";
		            kDelete.type		= "button";
		            kDelete.className	= "alarmdel";
		            kDelete.textContent	= "×";	// 오른쪽

		            kDelete.addEventListener("click", () =>
		            {
		                arrItem.splice(nIndex, 1);

		                Query.ShowAlarmList(kList, arrItem);
		            });

		            kItem.append(sItem, kDelete);

		            return kItem;
		        }));
		    }

		    static async SaveSavedOk()	// 쿼리 추가·수정 저장 → 서버 aQuery (추가: 같은 제목은 쿼리 바꿈, 수정: 그 자리, 겹침: 오류, 실패: 창 유지)
		    {
		        if (kQuerySaveDialog.classList.contains("alarm"))
		        {
		            Query.SaveAlarmOk();

		            return;
		        }

		        const sOldKey	= Query.sEditKey;	// 수정할 제목 ("": 추가)
		        const kInput	= Query.CheckSaved(kQuerySaveKey, kQuerySaveQuery);	// 제목·쿼리 (오류: null)
		        const nMinute	= Number(kQuerySaveMinute.value);	// 간격 (분)

		        if (kInput != null && (!Number.isInteger(nMinute) || nMinute < 1 || nMinute > Query.SAVED_MINUTE_MAX))
		        {
		            alert(`간격을 1 ~ ${Query.SAVED_MINUTE_MAX} 분으로 입력해 주세요.`);
		            kQuerySaveMinute.focus();

		            return;
		        }

		        const kData		= kInput == null ? null : await Query.PostSaved(Query.GetSavedPath(), { ...kInput, nMinute, sOldKey }, Query.GetSavedName());	// 응답 (실패: null)

		        if (kData != null)
		        {
		            const kOwner = Query.bSearch ? Search : Query;	// 선택 제목 둔 곳

		            if (sOldKey !== "" && kOwner.sSavedKey === sOldKey)	// 선택한 제목 수정: 새 제목으로
		            {
		                kOwner.sSavedKey = kInput.sKey;
		            }

		            kQuerySaveDialog.close();

		            Query.ShowSaved(Query.GetSavedList(kData), kInput.sKey, Query.bSearch);	// 추가·수정한 제목 줄 보이게
		        }
		    }

		    static async TestAlarm()	// 알람 테스트 발송: 창 값 검사 → 서버가 메일·훅 바로 보냄 (저장·알람 로그 없음) → 채널별 결과 알림
		    {
		        const kInput	= Query.CheckSaved(kQuerySaveKey, kQuerySaveQuery);	// 제목·쿼리 (오류: null)
		        const kAlarm	= kInput == null ? null : Query.CheckAlarm();			// 알람 칸 (오류: null)
		        const kButton	= document.getElementById("AlarmTest");

		        if (kAlarm == null)
		        {
		            return;
		        }

		        if (kAlarm.aMail.length === 0 && kAlarm.sHook === "")
		        {
		            alert("메일·훅을 입력해 주세요.");
		            kAlarmMail.focus();

		            return;
		        }

		        kButton.disabled = true;

		        const kData = await Query.PostSaved("/api/alarm/test", { ...kInput, ...kAlarm, nID: Query.nEditID }, "테스트 발송");	// 응답 (실패: null)

		        kButton.disabled = false;

		        if (kData != null)
		        {
		            alert("테스트 발송\n" + kData.arrResult.map(kResult => `${kResult.sChannel === "Mail" ? "메일" : "훅"}: ${kResult.sError === "" ? "성공" : `실패 (${kResult.sError})`}`).join("\n"));
		        }
		    }

		    static async SaveAlarmOk()	// 알람 추가·수정 저장 → 서버 aAlarm (추가: 끝에, 수정: 그 자리, 제목 겹침: 오류, 실패: 창 유지)
		    {
		        const kInput	= Query.CheckSaved(kQuerySaveKey, kQuerySaveQuery);	// 제목·쿼리 (오류: null)
		        const kAlarm	= kInput == null ? null : Query.CheckAlarm();			// 알람 칸 (오류: null)
		        const kData		= kAlarm == null ? null : await Query.PostSaved("/api/alarm", { ...kInput, ...kAlarm, nID: Query.nEditID }, "알람");	// 응답 (실패: null)

		        if (kData != null)
		        {
		            kQuerySaveDialog.close();

		            Query.ShowAlarm(kData.arrAlarm, kData.nID);	// 추가·수정한 알람 줄 보이게
		        }
		    }

		    static DeleteSaved(kSaved)	// 쿼리 설정 삭제: 확인 팝업 → RunDeleteSaved
		    {
		        AskDelete(`${Query.GetSavedName()} 삭제`, kSaved.sKey, kSaved.sQuery, () => Query.RunDeleteSaved(kSaved));
		    }

		    static async RunDeleteSaved(kSaved)	// 쿼리 설정 삭제 실행 → 목록 다시 (선택한 제목: 선택 해제)
		    {
		        const kData = await Query.PostSaved(Query.GetSavedPath("/delete"), { sKey: kSaved.sKey }, Query.GetSavedName());	// 응답 (실패: null)

		        if (kData != null)
		        {
		            Query.ShowSaved(Query.GetSavedList(kData), "", Query.bSearch);
		        }
		    }

		    static DeleteAlarm(kAlarm)	// 알람 삭제: 확인 팝업 → RunDeleteAlarm
		    {
		        AskDelete("알람 삭제", kAlarm.sKey, kAlarm.sQuery, () => Query.RunDeleteAlarm(kAlarm));
		    }

		    static async RunDeleteAlarm(kAlarm)	// 알람 삭제: 그 ID 지움 → 목록 다시
		    {
		        const kData = await Query.PostSaved("/api/alarm/delete", { nID: kAlarm.nID }, "알람");	// 응답 (실패: null)

		        if (kData != null)
		        {
		            Query.ShowAlarm(kData.arrAlarm);
		        }
		    }

		    static OpenRule(kRule)	// 필드 추가·수정 창 열기 (kRule: 수정할 필드 정의 → 그 값, null: 추가 → 빈칸)
		    {
		        Query.sEditRule			= kRule?.sKey ?? "";
		        kFieldRuleTitle.textContent	= kRule == null ? "필드 추가" : "필드 수정";
		        kFieldRuleKey.value			= kRule?.sKey ?? "";
		        kFieldRuleRegex.value		= kRule?.sRegex ?? "";
		        kFieldRuleValue.value		= kRule?.sValue ?? "";

		        kFieldRuleDialog.showModal();
		    }

		    static CheckRule()	// 필드 추가·수정 입력 검사 (이름: 영문 시작·영문·숫자·_ X자 이하·기본 필드 아님, 고정값 또는 정규식 필요 (고정값 있음: 정규식 안 씀), 정규식 형식: 서버, 반환: { sKey, sRegex, sValue }, 오류: 알림·그 칸으로 → null)
		    {
		        const sKey		= kFieldRuleKey.value.trim();								// 이름
		        const sValue	= kFieldRuleValue.value.trim();								// 고정값
		        const sRegex	= sValue !== "" ? "" : kFieldRuleRegex.value.replace(/[\r\n]+/g, "");	// 정규식 (줄바꿈 뺌, 고정값 있음: 안 씀)
		        const arrCheck	=	// [틀림, 칸, 알림]
		        [
		            [sKey.length > Query.RULE_KEY_MAX || !/^[A-Za-z][A-Za-z0-9_]*$/.test(sKey), kFieldRuleKey, `이름을 영문 시작·영문·숫자·_ ${Query.RULE_KEY_MAX}자 이하로 입력해 주세요.`],
		            [Query.RULE_BASE.includes(sKey.toLowerCase()), kFieldRuleKey, "기본 필드 (Machine·Folder·Type) 이름은 쓸 수 없습니다."],
		            [sValue.length > Query.RULE_VALUE_MAX, kFieldRuleValue, `고정값을 ${Query.RULE_VALUE_MAX}자 이하로 입력해 주세요.`],
		            [sValue === "" && (sRegex === "" || sRegex.length > Query.RULE_REGEX_MAX), kFieldRuleRegex, `정규식 (${Query.RULE_REGEX_MAX}자 이하) 또는 고정값을 입력해 주세요.`],
		        ];
		        const arrWrong	= arrCheck.find(arrItem => arrItem[0]);	// 처음 틀린 칸

		        if (arrWrong !== undefined)
		        {
		            alert(arrWrong[2]);
		            arrWrong[1].focus();

		            return null;
		        }

		        return { sKey, sRegex, sValue };
		    }

		    static async SaveRuleOk()	// 필드 추가·수정 저장 → 서버 aField (추가: 끝에, 수정: 그 자리, 이름 겹침·정규식 형식 오류: 서버 오류, 실패: 창 유지, 성공: 바로 적용·접속 클라 알림)
		    {
		        const kRule	= Query.CheckRule();	// 입력 (오류: null)
		        const kData	= kRule == null ? null : await Query.PostSaved("/api/field", { ...kRule, sOldKey: Query.sEditRule }, "필드");	// 응답 (실패: null)

		        if (kData != null)
		        {
		            kFieldRuleDialog.close();

		            Query.ShowRule(kData.arrField, kRule.sKey);	// 추가·수정한 필드 줄 보이게
		        }
		    }

		    static DeleteRule(kRule)	// 필드 정의 삭제: 확인 팝업 → RunDeleteRule
		    {
		        AskDelete("필드 삭제", kRule.sKey, `${Query.GetRuleText(kRule)} · 새 줄부터 안 받음 (받아 둔 값: 그대로)`, () => Query.RunDeleteRule(kRule));
		    }

		    static async RunDeleteRule(kRule)	// 필드 정의 삭제: 그 이름 지움 → 목록 다시
		    {
		        const kData = await Query.PostSaved("/api/field/delete", { sKey: kRule.sKey }, "필드");	// 응답 (실패: null)

		        if (kData != null)
		        {
		            Query.ShowRule(kData.arrField);
		        }
		    }

		    static async PostSaved(sUrl, kBody, sName)	// 쿼리 설정 추가·수정·삭제·순서 요청 (반환: { arrQuery·arrAlarm·arrField }, 실패: 알림 후 null)
		    {
		        try
		        {
		            const kResponse = await fetch(sUrl, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(kBody) });

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData = await kResponse.json();

		            if (kData.sError !== "")
		            {
		                throw new Error(kData.sError);
		            }

		            return kData;
		        }
		        catch (kError)	// 요청 실패·서버 오류
		        {
		            alert(`${sName} 실패: ${ErrorText(kError)}`);

		            return null;
		        }
		    }

		    static ShowChart()	// 건수 막대 (kChartData, 0건 포함, 단위: 기간 기본 단위에서 렌즈 스텝만큼 작게 (초·분·시·일·주·월), 1초: 초별 막대 1개 (안 눌림), 마우스: 시각·건수, 시간 없음: 숨김)
		    {
		        const kData = Query.kChartData;

		        kQueryChart.hidden = kData.arrHour.length === 0;

		        if (kData.arrHour.length === 0)
		        {
		            Query.arrBar = [];

		            kQueryChart.replaceChildren();

		            return;
		        }

		        const nBase		= Query.GetLensBase();	// 기간 기본 단위
		        const nLevel	= nBase - Query.nLens;	// 지금 단위 (0 초 · 1 분 · 2 시 · 3 일 · 4 월)
		        const arrItem	= nLevel < nBase && nLevel <= 1 ? (kData.arrLens[nLevel] ?? []) : (nLevel === 0 ? kData.arrSecond.map(kSecond => ({ nTime: kSecond.nSecond, nCount: kSecond.nCount })) : (nLevel === 1 ? kData.arrMinute.map(kMinute => ({ nTime: kMinute.nMinute, nCount: kMinute.nCount })) : kData.arrHour.map(kHour => ({ nTime: kHour.nHour, nCount: kHour.nCount }))));	// 초·분·시간별 건수 (렌즈 초·분봉: 따로 가져온 건수)
		        const funcKey	= [null, null, null, (nTime) => Math.floor(nTime / 100), (nTime) => Math.floor(nTime / 10000)][nLevel];	// 막대 묶음 (일: yyyyMMdd, 월: yyyyMM, 초·분·시: 그대로)
		        const arrBar	= [];	// 막대 { nFirst, nLast, nCount, nKey } (첫·끝 시각 yyyyMMddHH (분별: yyyyMMddHHmm, 초별: yyyyMMddHHmmss), 건수 합, 묶음)

		        for (const kItem of arrItem)	// 일·월: 같은 묶음 시간끼리 합침
		        {
		            const kLast	= arrBar[arrBar.length - 1];							// 앞 막대
		            const nKey	= funcKey == null ? kItem.nTime : funcKey(kItem.nTime);	// 묶음

		            if (kLast !== undefined && kLast.nKey === nKey)
		            {
		                kLast.nLast		= kItem.nTime;
		                kLast.nCount	+= kItem.nCount;
		            }
		            else
		            {
		                arrBar.push({ nFirst: kItem.nTime, nLast: kItem.nTime, nCount: kItem.nCount, nKey });
		            }
		        }

		        Query.arrBar		= arrBar;
		        Query.nBarUnit	= nLevel;

		        const nMax		= arrBar.reduce((nValue, kItem) => Math.max(nValue, kItem.nCount), 1);					// 가장 큰 건수 (최소 1)
		        const nWant		= nMax * 1.5;																			// 눈금 간격 기준 (가장 큰 건수 1.5배)
		        const nUnit		= 10 ** Math.floor(Math.log10(nWant / 4));												// 눈금 단위 자릿수
		        const nStep		= Math.max(1, [1, 2, 5, 10].map(nItem => nItem * nUnit).find(nItem => nItem >= nWant / 4));	// 눈금 간격 (1·2·5 × 10^n)
		        const nTop		= (Math.floor(nMax / nStep) + 1) * nStep;												// y축 최대: 가장 큰 막대 바로 위 눈금 한 줄 (예: 2건 → 3, 6건 → 10)
		        const arrEvery	= [[1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600], [1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440, 2880, 4320, 10080, 20160], [1, 2, 3, 6, 12, 24, 48, 72, 168, 336, 720]][Math.min(nLevel, 2)];	// 초·분·시별 라벨 간격 후보 (초·분·시간 수)
		        const nLabelMax	= Math.max(4, Math.floor(kQueryChart.clientWidth / 48) || 30);	// 라벨 최대 수 (라벨 하나 X px, 안 보임: X개)
		        const nEvery	= nLevel <= 2 && arrBar.length > 60 ? (arrEvery.find(nItem => arrBar.length / nItem <= nLabelMax) ?? arrEvery[arrEvery.length - 1]) : 1;	// 막대 많음 (렌즈 초·분·시봉): 라벨 X칸마다 (정각·자정 기준)
		        let sLabelMonth	= "";	// 앞 라벨의 월 (날짜별: 월 바뀐 라벨만 월/일)
		        const sGap		= arrBar.length > 150 ? "0" : (arrBar.length > 60 ? "1px" : "");	// 막대 사이 (막대 많음: 줄여서 창 너비 안에)
		        const kAxisY	= document.createElement("div");	// y축 눈금 글자
		        const kPlot		= document.createElement("div");	// 막대 영역 (눈금선 + 막대)
		        const kBars		= document.createElement("div");
		        const kAxisX	= document.createElement("div");	// x축 시간 글자
		        const kTip		= document.createElement("div");	// 마우스 올림 표시

		        kAxisY.className	= "charty";
		        kAxisY.append(Object.assign(document.createElement("span"), { textContent: Math.max(nTop, 1000000).toLocaleString(), style: "position:static;visibility:hidden;transform:none" }));	// y축 너비: 100만 단위 (1,000,000) 까지 자리 확보 (넘으면 가장 긴 눈금 글자)
		        kPlot.className		= "chartplot";
		        kBars.className		= "chartbars";
		        kBars.style.gap		= sGap;
		        kAxisX.className	= "chartx";
		        kAxisX.style.gap	= sGap;
		        kAxisX.style.fontSize	= arrBar.length > 45 ? "0.625rem" : "";	// 막대 많음 (60·90일): 날짜 글자 작게 (모두 표시)
		        kTip.className		= "charttip";
		        kTip.hidden			= true;

		        for (let nTick = 0; nTick <= nTop; nTick += nStep)	// 눈금 글자·선
		        {
		            const kLabel	= document.createElement("span");
		            const kGrid		= document.createElement("div");

		            kLabel.textContent	= nTick.toLocaleString();
		            kLabel.style.bottom	= `${nTick / nTop * 100}%`;
		            kGrid.className		= "chartgrid";
		            kGrid.style.bottom	= `${nTick / nTop * 100}%`;

		            kAxisY.append(kLabel);

		            if (nTick > 0)	// 0 은 바닥선
		            {
		                kPlot.append(kGrid);
		            }
		        }

		        arrBar.forEach((kItem, nIndex) =>
		        {
		            const kColumn	= document.createElement("div");	// 막대 칸 (마우스 대상: 막대보다 큼)
		            const kBar		= document.createElement("div");
		            const kLabel	= document.createElement("span");
		            const sTime		= String(kItem.nFirst);
		            const sMonth	= sTime.slice(4, 6);			// 월
		            const nDay		= Number(sTime.slice(6, 8));	// 일

		            kColumn.dataset.index	= nIndex;
		            kBar.style.height		= `${kItem.nCount / nTop * 100}%`;

		            if (nLevel <= 2)	// 초별: 초 (첫도 숫자만, 렌즈: 중간 정분만 시:분), 분별: 분 (첫도 숫자만, 중간 정시만 시:분), 시간별: 시, 렌즈: 자정만 월/일·nEvery 칸마다
		            {
		                const sClock = sTime.slice(8);	// 시분초 (초별: HHmmss, 분별: HHmm, 시간별: HH)

		                if (Math.floor(Date.UTC(Number(sTime.slice(0, 4)), Number(sMonth) - 1, nDay, Number(sClock.slice(0, 2)), Number(sClock.slice(2, 4) || 0), Number(sClock.slice(4) || 0)) / [1000, 60000, 3600000][nLevel]) % nEvery === 0)	// 라벨 칸 (정각·자정 기준)
		                {
		                    kLabel.textContent = Query.nLens > 0 && Number(sClock) === 0 ? `${Number(sMonth)}/${nDay}` : (nLevel === 2 ? String(Number(sClock)) : (nLevel === 1 ? ((nIndex > 0 || Query.nLens > 0) && sClock.slice(2) === "00" ? `${Number(sClock.slice(0, 2))}:00` : String(Number(sClock.slice(2)))) : (Query.nLens > 0 && sClock.slice(4) === "00" ? `${Number(sClock.slice(0, 2))}:${sClock.slice(2, 4)}` : String(Number(sClock.slice(4))))));	// 렌즈: 첫 라벨도 시:분

		                    kLabel.classList.toggle("month", kLabel.textContent.includes("/"));	// 월/일 라벨: 존 색
		                }
		            }
		            else if (nLevel === 4)	// 월별: 첫·해 바뀜만 년/월·그 외 월
		            {
		                kLabel.textContent = nIndex === 0 || sMonth === "01" ? `${sTime.slice(0, 4)}/${Number(sMonth)}` : `${Number(sMonth)}월`;

		                kLabel.classList.toggle("month", nIndex === 0 || sMonth === "01");	// 년/월 라벨: 존 색
		            }
		            else if (nIndex === 0 || (arrBar.length > 200 ? nDay === 1 : (arrBar.length <= 50 || [1, 5, 10, 15, 20, 25].includes(nDay))))	// 날짜별: 첫·월 바뀜만 월/일·그 외 일 (50개 넘음: 1·5·10·15·20·25일, 200개 넘음: 1일)
		            {
		                kLabel.textContent = sMonth !== sLabelMonth ? `${Number(sMonth)}/${nDay}` : String(nDay);

		                kLabel.classList.toggle("month", sMonth !== sLabelMonth);	// 월/일 라벨: 존 색

		                sLabelMonth = sMonth;
		            }

		            kColumn.append(kBar);
		            kBars.append(kColumn);
		            kAxisX.append(kLabel);
		        });

		        kBars.addEventListener("mousemove", (kEvent) =>
		        {
		            const kColumn = kEvent.target.closest("[data-index]");	// 마우스 아래 막대 칸

		            if (kColumn == null)
		            {
		                return;
		            }

		            const kItem		= Query.arrBar[Number(kColumn.dataset.index)];
		            const kChart	= kQueryChart.getBoundingClientRect();
		            const kBox		= kColumn.getBoundingClientRect();
		            const kBar		= kColumn.firstChild.getBoundingClientRect();
		            const nLevel	= Query.nBarUnit;
		            const sWhen		= nLevel === 0 ? `${Query.FormatMinute(Math.floor(kItem.nFirst / 100))}:${String(kItem.nFirst % 100).padStart(2, "0")}` : (nLevel === 1 ? Query.FormatMinute(kItem.nFirst) : (nLevel === 2 ? Query.FormatHour(kItem.nFirst) : (nLevel === 3 ? Query.FormatHour(kItem.nFirst).slice(0, 10) : Query.FormatHour(kItem.nFirst).slice(0, 7))));	// 초별: yyyy-MM-dd HH:mm:ss, 분별: yyyy-MM-dd HH:mm, 시간별: yyyy-MM-dd HH시, 날짜별: yyyy-MM-dd, 월별: yyyy-MM

		            kTip.textContent	= `${sWhen} · ${kItem.nCount.toLocaleString()}건`;
		            kTip.style.top		= `${Math.min(kBar.top, kBox.bottom) - kChart.top}px`;
		            kTip.hidden			= false;
		            kTip.style.left		= `${Math.min(Math.max(kBox.left + kBox.width / 2 - kChart.left, kTip.offsetWidth / 2), kChart.width - kTip.offsetWidth / 2)}px`;	// 막대 가운데 (양끝에서 잘리지 않게 창 안으로)
		        });

		        kBars.addEventListener("mouseleave", () =>
		        {
		            kTip.hidden = true;
		        });

		        kBars.addEventListener("click", (kEvent) =>	// 막대 클릭 (단계 남김): 월별 → 그 달 일별, 날짜별 → 그 날 시간별, 시간별 → 그 시간 분별, 분별 → 그 1분 초별, 초별 → 그 1초 (막대 1개·결과), 막대 하나 (1초·1분): 안 눌림
		        {
		            const kColumn	= kEvent.target.closest("[data-index]");	// 누른 막대 칸
		            const nLevel	= Query.nBarUnit;

		            if (kColumn == null || Query.bQuerying || Query.bLensLoading || (nLevel <= 1 && Query.arrBar.length === 1))
		            {
		                return;
		            }

		            const kItem	= Query.arrBar[Number(kColumn.dataset.index)];
		            const sTime	= String(nLevel === 4 ? kItem.nKey : kItem.nFirst);	// yyyyMMddHH (날짜별: 그 날 첫 시간, 분별: yyyyMMddHHmm, 초별: yyyyMMddHHmmss, 월별: yyyyMM)
		            const kFrom	= new Date(Number(sTime.slice(0, 4)), Number(sTime.slice(4, 6)) - 1, nLevel === 4 ? 1 : Number(sTime.slice(6, 8)), nLevel >= 3 ? 0 : Number(sTime.slice(8, 10)), nLevel <= 1 ? Number(sTime.slice(10, 12)) : 0, nLevel === 0 ? Number(sTime.slice(12, 14)) : 0);	// 그 달 1일·그 날 0시·그 시간 정각·그 분·그 초
		            const kTo	= nLevel === 0 ? new Date(kFrom.getTime() + 1000) : (nLevel === 1 ? kFrom : (nLevel === 4 ? new Date(kFrom.getFullYear(), kFrom.getMonth() + 1, 1) : new Date(kFrom.getTime() + (nLevel === 3 ? 24 : 1) * 3600 * 1000)));	// 초별: 다음 초 (그 1초), 분별: 시작과 같음 (그 1분), 다음 달 1일·다음 날 0시·다음 정각

		            Query.SetPicker(kStart, kStartHour, kStartMinute, kFrom);
		            Query.SetPicker(kEnd, kEndHour, kEndMinute, kTo);	// 초별: 그 1초 (S 포함 ~ E 미포함, 초까지 표시)

		            kQueryRange.selectedIndex = 0;

		            Query.Query(Query.sQueryKey, kFrom, kTo, true);
		        });

		        kBars.classList.toggle("click", arrBar.length > 1 || nLevel >= 2);	// 누를 수 있음 (막대 하나인 초별 (1초)·분별: 없음)

		        kPlot.append(kBars);
		        kQueryChart.replaceChildren(kQueryLens, kAxisY, kPlot, kAxisX, kTip);

		        Query.ShowLens();
		    }

		    static GetLensBase()	// 기간 기본 막대 단위 (렌즈 스텝 0: 1분 이하 초, 1시간 이하 분, 24시간 이하 시, 90일 미만 일, 넘음: 월)
		    {
		        const nSpan	= Query.kTo - Query.kFrom;	// 기간 (ms)
		        const nDay	= 24 * 3600 * 1000;

		        return nSpan <= 60 * 1000 ? 0 : (nSpan <= 3600 * 1000 ? 1 : (nSpan <= nDay ? 2 : (nSpan < 90 * nDay ? 3 : 4)));
		    }

		    static GetLensMax()	// 렌즈 스텝 최대 (초봉까지, 막대 LENS_BAR_MAX 개 넘는 단위: 못 감)
		    {
		        const nBase		= Query.GetLensBase();
		        const kMinute	= new Date(Query.kFrom.getTime());	// 기간 첫 분 (정각 분, 서버 분봉과 같음)

		        kMinute.setSeconds(0, 0);

		        const arrCount	= [Math.ceil((Query.kTo - Query.kFrom) / 1000), Math.ceil((Query.kTo - kMinute) / 60000), Query.kChartData.arrHour.length, 0, 0];	// 단위별 막대 수 (초·분·시만, 일·월: 상한 안)
		        let nMax		= 0;

		        while (nBase - nMax > 0 && arrCount[nBase - nMax - 1] <= Query.LENS_BAR_MAX)
		        {
		            ++nMax;
		        }

		        return nMax;
		    }

		    static GetLensStep(nDirection)	// 렌즈 스텝 0 에서 오갈 넓힌 단계 (↓ (1): 넓히기 전 단계, ↑ (-1): 넓혀 둔 다음 단계, 없음: -1)
		    {
		        const nIndex = Query.nHistory + (nDirection > 0 ? -1 : 1);	// 오갈 단계

		        return Query.nLens === 0 && Query.arrHistory[nDirection > 0 ? Query.nHistory : nIndex]?.bWiden === true && nIndex >= 0 ? nIndex : -1;
		    }

		    static ShowLens()	// 렌즈 ↑·↓ 켬·끔, 가운데 스텝 (툴팁: 지금 단위)
		    {
		        kLensUp.disabled		= Query.bLensLoading || (Query.nLens <= 0 && Query.GetLensBase() >= Query.LENS_WIDE.length - 1 && Query.GetLensStep(-1) < 0);	// 스텝 0: 넓힐 기간·넓혀 둔 단계 있음 (월봉: 끝)
		        kLensDown.disabled		= Query.bLensLoading || (Query.nLens >= Query.GetLensMax() && Query.GetLensStep(1) < 0);		// 끝 단위: 넓히기 전 단계 있음
		        kLensStep.textContent	= String((Query.arrHistory[Query.nHistory]?.nLensBase ?? 0) - Query.nLens);	// 렌즈 숫자 = 넓힌 깊이 (↑ 넓힘마다 +1) - 스텝 (↓ 마다 -1)
		        kLensStep.title			= `${Query.LENS_NAME[Query.nBarUnit]}봉`;
		    }

		    static async MoveLens(nDirection)	// 렌즈 ↑ (-1)·↓ (1): 막대 한 단위 크게·작게 (스텝 0 ~ 초봉, 스텝 0: 넓힌 단계 오감 (스냅샷) → 없으면 ↑ 기간 넓혀 쿼리, 기본 단위보다 작은 초·분봉: 건수 서버에서, 쿼리 중·가져오는 중: 안 함)
		    {
		        const nLens		= Query.nLens + nDirection;		// 갈 스텝
		        const nLevel	= Query.GetLensBase() - nLens;	// 갈 단위
		        const nStep		= Query.GetLensStep(nDirection);	// 오갈 넓힌 단계

		        if (Query.bQuerying || Query.bLensLoading || Query.kChartData == null)
		        {
		            return;
		        }

		        if (nStep >= 0)	// 스텝 0: 넓히기 전·넓혀 둔 단계 보관 결과 그대로 (다시 쿼리 안 함)
		        {
		            Query.GoHistory(nStep - Query.nHistory);

		            return;
		        }

		        if (nLens < 0)	// 스텝 0 ↑: 한 단위 큰 기간으로 넓혀 쿼리
		        {
		            Query.WidenLens(nLevel);

		            return;
		        }

		        if (nLens > Query.GetLensMax() || (nLevel <= 1 && nLens > 0 && Query.kChartData.arrLens[nLevel] == null && !await Query.LoadLens(nLevel)))	// 끝 단위·초·분봉 처음: 건수 가져옴 (실패·결과 바뀜: 그대로)
		        {
		            return;
		        }

		        Query.nLens = nLens;

		        if (Query.arrHistory[Query.nHistory] != null)	// 그 단계 스텝 보관 (단계 오감: 그대로)
		        {
		            Query.arrHistory[Query.nHistory].nLens = nLens;
		        }

		        Query.ShowChart();
		    }

		    static WidenLens(nLevel)	// 렌즈 스텝 0 ↑: 기준 시각 (지금 기간 마지막) 포함 지난 LENS_WIDE (분봉 60분·시봉 24시간·일봉 그 달 (월봉 막대 클릭과 같음)·월봉 12개월) 쿼리 (S·E 맞춤·D 해제·단계 남김, 월봉 위: 안 함)
		    {
		        const nCount	= Query.LENS_WIDE[nLevel];	// 분·시간·달 수

		        if (nCount == null)
		        {
		            return;
		        }

		        const kBase		= new Date(Query.kTo.getTime() - 1);	// 기준 시각 (기간 마지막, 끝 미포함)
		        const kTo		= [null, new Date(kBase.getFullYear(), kBase.getMonth(), kBase.getDate(), kBase.getHours(), kBase.getMinutes() + 1), new Date(kBase.getFullYear(), kBase.getMonth(), kBase.getDate(), kBase.getHours() + 1), new Date(kBase.getFullYear(), kBase.getMonth() + 1, 1), new Date(kBase.getFullYear(), kBase.getMonth() + 1, 1)][nLevel];	// 기준 분·시간 다음 칸 처음·기준 달 다음 달 1일 (미포함)
		        const kFrom		= [null, new Date(kTo.getTime() - nCount * 60000), new Date(kTo.getTime() - nCount * 3600000), new Date(kTo.getFullYear(), kTo.getMonth() - nCount, 1), new Date(kTo.getFullYear(), kTo.getMonth() - nCount, 1)][nLevel];	// X 분·시간·달 앞 (일봉: 그 달 1일, 월봉: 11개월 전 1일)

		        Query.SetPicker(kStart, kStartHour, kStartMinute, kFrom);
		        Query.SetPicker(kEnd, kEndHour, kEndMinute, kTo);

		        kQueryRange.selectedIndex = 0;

		        const nHistory	= Query.nHistory;								// 넓히기 전 단계
		        const nBase		= Query.arrHistory[nHistory]?.nLensBase ?? 0;	// 넓히기 전 렌즈 숫자 (스텝 0)

		        Query.Query(Query.sQueryKey, kFrom, kTo, true);

		        if (Query.nHistory === nHistory + 1)	// 넓힌 단계 표시 (렌즈 ↓: 넓히기 전 단계 스냅샷으로, 렌즈 숫자: 넓히기 전 + 1)
		        {
		            Query.arrHistory[Query.nHistory].bWiden		= true;
		            Query.arrHistory[Query.nHistory].nLensBase	= nBase + 1;
		        }
		    }

		    static async LoadLens(nLevel)	// 렌즈 초봉 (0)·분봉 (1) 건수 (지금 결과 쿼리·기간, 성공: kChartData.arrLens[nLevel], 실패·쿼리 시작·결과 바뀜: false)
		    {
		        const kData		= Query.kChartData;							// 가져올 결과 (끝났을 때 같은지 확인)
		        const sName		= `${Query.LENS_NAME[nLevel]}봉`;				// 초봉·분봉

		        Query.bLensLoading		= true;
		        kQueryProgress.textContent	= `${sName} 중\n"${Query.sQueryKey}"\n${Query.FormatMinute(Query.nStart)} ~ ${Query.FormatMinute(Query.nEnd)}`;
		        Query.ShowLens();

		        const nPopup = setTimeout(() => { kQueryProgress.hidden = false; }, PROGRESS_DELAY_MS);	// 팝업: X초 이상 걸릴 때만

		        try
		        {
		            const kResponse = await fetch("/api/query/lens?" + new URLSearchParams({ sKey: Query.sQueryKey, nStart: String(Query.nStart), nEnd: String(Query.nEnd), nSec: nLevel === 0 ? "1" : "60", nLimit: String(nQueryLimitSec) }));

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kResult = await kResponse.json();

		            if (kResult.sError !== "")
		            {
		                throw new Error(kResult.sError);
		            }

		            if (kData !== Query.kChartData || Query.bQuerying)	// 그 사이 쿼리 시작·결과 바뀜: 버림
		            {
		                return false;
		            }

		            kData.arrLens[nLevel] = kResult.arrTime;

		            return true;
		        }
		        catch (kError)	// 요청 실패·서버 오류 (막대는 그대로)
		        {
		            if (kData === Query.kChartData && !Query.bQuerying)
		            {
		                kQueryTitle.textContent = `${Query.FormatSpan(Query.nStart, Query.nEnd)} - ${sName} 실패: ${ErrorText(kError)}`;
		            }

		            return false;
		        }
		        finally
		        {
		            Query.bLensLoading = false;

		            clearTimeout(nPopup);

		            if (!Query.bQuerying)	// 쿼리 진행 표시 중: 그대로
		            {
		                kQueryProgress.hidden = true;
		            }

		            Query.ShowLens();
		        }
		    }

		    static GetMarkWords(sKey)	// 강조할 쿼리 단어 (소문자, 찾는 쪽 검색어마다: * 뺌, 필드:값 → 값, IP·날짜·시간 → arrMarkExact 글자 그대로, 그 외: 서버 단어 규칙으로 나눔)
		    {
		        if (sKey === Query.sMarkKey)
		        {
		            return Query.setMarkWord;
		        }

		        const setWord	= new Set();	// 강조할 단어
		        const arrExact	= [];			// 원문 그대로 강조할 글자
		        const arrWild	= [];			// 와일드카드 정규식
		        const arrName	= ["machine", "folder", "type", ...Query.arrField.map(kField => kField.sName.toLowerCase())];	// 필드 이름 (필드:값 구분)

		        for (const sTerm of Key.SplitKey(sKey))
		        {
		            const kField	= /^([A-Za-z][A-Za-z0-9_]*):(.+)$/.exec(sTerm.trim());	// 필드:값 후보
		            const sText		= kField != null && arrName.includes(kField[1].toLowerCase()) ? kField[2] : sTerm;	// 단어로 나눌 글자

		            if (sTerm.trim() === "*")	// 전체 로그
		            {
		                continue;
		            }

		            if (sText === sTerm && Key.IsWild(sTerm))	// 와일드카드 (필드:값 아님): * 뗀 글자 + 경계 (서버 IsWildMatch 와 같음)
		            {
		                arrWild.push(Key.MakeRegex(sTerm));

		                continue;
		            }

		            if (Query.EXACT_REGEX.test(sText.trim()))	// IP·날짜·시간: 그 글자 덩어리만 (서버 원문 확인과 같음, 예: 1/5 → 1·5 따로 안 칠함)
		            {
		                arrExact.push(sText.trim());

		                continue;
		            }

		            for (const kMatch of sText.matchAll(Query.WORD_REGEX))
		            {
		                if (/[^-_]/.test(kMatch[0]))
		                {
		                    setWord.add(kMatch[0].toLowerCase());
		                }
		            }
		        }

		        Query.sMarkKey		= sKey;
		        Query.setMarkWord	= setWord;
		        Query.arrMarkExact	= arrExact;
		        Query.arrMarkWild	= arrWild;

		        return setWord;
		    }

		    static AppendMark(kParent, sText)	// 텍스트 추가 + 쿼리 단어 강조 (단어 단위·대소문자 무시, 앞뒤 -·_ 뗀 형태·가운데 -·_ 조각 포함, IP·날짜·시간: 그 글자 덩어리만, 와일드카드: * 뗀 글자, 앞 시간 제외 → 서버 색인·원문 확인과 같음, 텍스트 노드로만)
		    {
		        const setWord	= Query.GetMarkWords(Query.sQueryKey);	// 강조할 단어
		        const kTime		= /^\[\d{4}\/\d{2}\/\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]/.exec(sText);	// 앞 시간 (원문 그대로일 때)
		        let nPos		= kTime != null ? kTime[0].length : 0;	// 추가할 위치

		        kParent.append(sText.slice(0, nPos));

		        if (setWord.size > 0 || Query.arrMarkExact.length > 0 || Query.arrMarkWild.length > 0)
		        {
		            const nBase		= nPos;	// 찾기 시작 (앞 시간 뒤)
		            const arrMark	= [];	// 칠할 구간 [시작, 끝)
		            const funcFree	= (nFrom, nTo) => arrMark.every(([nA, nB]) => nTo <= nA || nFrom >= nB);	// 앞 구간과 안 겹침

		            for (const sExact of Query.arrMarkExact)	// IP·날짜·시간: 원문 그 글자 그대로 (숫자·기호뿐 → 대소문자 무관)
		            {
		                for (let nAt = sText.indexOf(sExact, nBase); nAt >= 0; nAt = sText.indexOf(sExact, nAt + sExact.length))
		                {
		                    if (funcFree(nAt, nAt + sExact.length))
		                    {
		                        arrMark.push([nAt, nAt + sExact.length]);
		                    }
		                }
		            }

		            for (const kRegex of Query.arrMarkWild)	// 와일드카드: * 뗀 글자 (앞뒤 * 없는 쪽 경계)
		            {
		                kRegex.lastIndex = nBase;

		                for (let kMatch = kRegex.exec(sText); kMatch != null && kMatch[0].length > 0; kMatch = kRegex.exec(sText))
		                {
		                    if (funcFree(kMatch.index, kMatch.index + kMatch[0].length))
		                    {
		                        arrMark.push([kMatch.index, kMatch.index + kMatch[0].length]);
		                    }
		                }
		            }

		            for (const kMatch of sText.slice(nBase).matchAll(Query.WORD_REGEX))
		            {
		                const sWord	= kMatch[0].toLowerCase();
		                const nAt	= nBase + kMatch.index;	// 줄 안 위치

		                if (setWord.has(sWord) || setWord.has(sWord.replace(/^[-_]+|[-_]+$/g, "")))	// 단어 통째
		                {
		                    if (funcFree(nAt, nAt + kMatch[0].length))
		                    {
		                        arrMark.push([nAt, nAt + kMatch[0].length]);
		                    }

		                    continue;
		                }

		                for (const kPart of kMatch[0].matchAll(/[^-_]+/g))	// 가운데 -·_ 조각 (예: sk_hidden_b7 의 sk)
		                {
		                    const nPartAt = nAt + kPart.index;	// 조각 위치

		                    if (kPart[0].length !== kMatch[0].length && setWord.has(kPart[0].toLowerCase()) && funcFree(nPartAt, nPartAt + kPart[0].length))	// 조각 (통째와 다름)·맞음·안 겹침
		                    {
		                        arrMark.push([nPartAt, nPartAt + kPart[0].length]);
		                    }
		                }
		            }

		            arrMark.sort((kA, kB) => kA[0] - kB[0]);

		            for (const [nFrom, nTo] of arrMark)
		            {
		                kParent.append(sText.slice(nPos, nFrom), Object.assign(document.createElement("mark"), { textContent: sText.slice(nFrom, nTo) }));

		                nPos = nTo;
		            }
		        }

		        kParent.append(sText.slice(nPos));
		    }

		    static ShowRows(arrLine, bAppend = false)	// 결과 줄 그리기 (No.·Machine·File·Line·Log, Log: 쿼리 단어 강조, 줄 클릭: 펼침, bAppend: 이어 읽은 줄 붙임)
		    {
		        const kFragment	= document.createDocumentFragment();
		        const nFirst	= bAppend ? Query.arrLine.length : 0;	// 이번 줄 시작 위치 (페이지 안)

		        Query.arrLine = bAppend ? Query.arrLine.concat(arrLine) : arrLine;

		        arrLine.forEach((kLine, nOffset) =>
		        {
		            const kRow		= document.createElement("tr");
		            const nIndex	= nFirst + nOffset;	// 페이지 안 위치 (arrLine·펼침)

		            kRow.className		= "row";
		            kRow.dataset.index	= nIndex;

		            AddCell(kRow, (Query.nPage * Query.nPageSize + nIndex + 1).toLocaleString(), "num");	// 번호 (최신부터 1)
		            AddCell(kRow, kLine.sMachine, "nowrap");												// 머신
		            AddFileNameCell(kRow, kLine.sMachine, kLine.sPath, kLine.nLine ?? 0, Query.sQueryKey === "*" ? "" : Query.sQueryKey);	// 파일명 (Ctrl·Alt+클릭: 그 줄 강조, 줄 모름: 마지막 줄부터)
		            AddCell(kRow, kLine.nLine > 0 ? String(kLine.nLine) : "", "num");						// 클라 파일 줄 번호 (모름: 비움)
		            Query.AppendMark(AddCell(kRow, "", "log"), kLine.sOrigin ?? kLine.sText);		// 로그: 원문 그대로 (시간·Type 포함, 쿼리 단어 강조)

		            kFragment.append(kRow);
		        });

		        if (bAppend)	// 이어 읽음: 아래에 붙임 (보던 위치 그대로)
		        {
		            kQueryBody.append(kFragment);
		            Query.SetRowSpace();

		            return;
		        }

		        kQueryBody.replaceChildren(kFragment);
		        Query.SetRowSpace();

		        kQueryScroll.scrollTop	= 0;
		        kQueryScroll.scrollLeft	= 0;
		    }

		    static GetPageRows()	// 이 페이지 전체 줄 수 (건수 기준, 서버가 요청보다 덜 줘서 끝 확인: 읽은 데까지)
		    {
		        const nPageRows = Math.max(0, Math.min(Query.nPageSize, Query.nTotal - Query.nPage * Query.nPageSize));	// 건수 기준

		        return Query.nRowEnd >= 0 ? Math.min(nPageRows, Query.nRowEnd) : nPageRows;
		    }

		    static SetRowSpace()	// 쿼리 2번 창 표 아래 빈 높이: 안 읽은 줄 수 × 줄 높이 → 스크롤 바 처음부터 페이지 전체 크기 (끝 확인: 빈 높이 없음)
		    {
		        const nPageRows	= Query.GetPageRows();	// 이 페이지 전체 줄 수 (끝 확인: 읽은 데까지)
		        const nRowH		= kQueryBody.querySelector("tr.row")?.getBoundingClientRect().height ?? 0;					// 한 줄 높이 (px 소수까지, 숨김: 0)

		        kQueryBody.parentElement.style.marginBottom = `${Math.max(0, nPageRows - Query.arrLine.length) * nRowH}px`;
		    }

		    static IsNearRowEnd()	// 쿼리 2번 창 보이는 곳 아래 끝이 읽은 줄 끝에서 300px 안 (빈 높이로 내려감 포함)
		    {
		        return kQueryBody.getBoundingClientRect().bottom - kQueryScroll.getBoundingClientRect().bottom <= 300;
		    }

		    static async LoadMore()	// 아래 끝 가까이 스크롤: 다음 CHUNK 건 이어 읽어 붙임 (페이지 끝·쿼리 중·읽는 중: 무시, 그 사이 새 쿼리·페이지: 버림)
		    {
		        const nPageRows = Query.GetPageRows();	// 이 페이지 전체 줄 수 (끝 확인: 읽은 데까지)

		        if (Query.bQuerying || Query.bChunkLoading || Query.arrLine.length >= nPageRows || Query.sQueryKey === "")
		        {
		            return;
		        }

		        const nSeq		= Query.nChunkSeq;																				// 이번 페이지 (새 쿼리·페이지: 바뀜)
		        const nRowH		= kQueryBody.querySelector("tr.row")?.getBoundingClientRect().height || 1;					// 한 줄 높이
		        const nGap		= Math.max(0, kQueryScroll.getBoundingClientRect().bottom - kQueryBody.getBoundingClientRect().bottom);	// 빈 높이로 내려간 만큼
		        const nCount	= Math.min(nPageRows - Query.arrLine.length, Math.ceil(nGap / nRowH) + Query.CHUNK);			// 읽을 줄 수
		        let bDone		= false;																							// 붙임 (이어서 더 읽을지 확인)

		        Query.bChunkLoading = true;

		        try
		        {
		            const kResponse = await fetch("/api/query?" + new URLSearchParams({ sKey: Query.sQueryKey, nStart: String(Query.nStart), nEnd: String(Query.nEnd), nPage: String(Query.nPage), nOffset: String(Query.arrLine.length), nCount: String(nCount), nLimit: String(nQueryLimitSec) }));

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData = await kResponse.json();

		            if (nSeq === Query.nChunkSeq && kData.sError === "")
		            {
		                if (kData.arrLine.length < nCount)	// 요청보다 덜 옴: 페이지 끝 (남은 빈 높이 없앰)
		                {
		                    Query.nRowEnd = Query.arrLine.length + kData.arrLine.length;
		                }

		                Query.ShowRows(kData.arrLine, true);

		                bDone = kData.arrLine.length > 0;
		            }
		        }
		        catch (kError)	// 요청 실패: 다음 스크롤 때 다시
		        {
		            console.error(kError);
		        }
		        finally
		        {
		            Query.bChunkLoading = false;
		        }

		        if (bDone && Query.IsNearRowEnd())	// 읽는 사이 더 내려감: 이어서
		        {
		            Query.LoadMore();
		        }
		    }

		    static ToggleDetail(kEvent)	// 결과 줄 클릭: 아래로 펼쳐 필드·원문 전체 (다시 클릭: 접음, File 칸 Shift·Ctrl·Alt·Log 칸 글자 고르기·더블클릭 제외, 표 하나에서 처리)
		    {
		        const kRow		= kEvent.target.closest("tr.row");	// 누른 결과 줄 (펼친 내용 클릭: 없음)
		        const kClick	= kEvent.target.closest("td");		// 누른 칸

		        if (kRow == null || kClick == null)
		        {
		            return;
		        }

		        if (kClick.classList.contains("log") && (kEvent.ctrlKey || kEvent.altKey || kEvent.shiftKey || kEvent.detail > 1 || window.getSelection().toString() !== ""))	// Log 칸: 단어 쿼리·AND 추가·더블클릭·끌어서 고르기는 그대로
		        {
		            return;
		        }

		        if (kRow.classList.contains("open"))	// 펼침: 접음
		        {
		            kRow.nextElementSibling?.remove();
		            kRow.classList.remove("open");

		            return;
		        }

		        const nIndex	= Number(kRow.dataset.index);
		        const kLine		= Query.arrLine[nIndex];
		        const kDetail	= document.createElement("tr");	// 펼친 줄
		        const kCell		= kDetail.insertCell();
		        const kField	= document.createElement("div");	// 필드 한 줄 (Machine·Folder·File·Type·추가 필드, 값 없음 뺌)
		        const kText		= document.createElement("div");	// 원문 그대로 전체 (시간·Type 포함, 줄바꿈)
		        const sFolder	= kLine.sFolder ?? "";
		        const sFile		= sFolder !== "" && kLine.sPath.toLowerCase().startsWith(sFolder.toLowerCase() + "\\") ? kLine.sPath.slice(sFolder.length + 1) : kLine.sPath;	// Folder 아래 경로 (Folder 밖: 전체 경로)
		        const arrItem	= (kLine.arrField ?? []).filter(kItem => kItem.sValue !== "");	// 값 있는 필드
		        const nFolder	= arrItem.findIndex(kItem => kItem.sName === "Folder");		// File 넣을 자리 (Folder 뒤, 없음: Machine 뒤)

		        arrItem.splice(nFolder >= 0 ? nFolder + 1 : Math.min(1, arrItem.length), 0, { sName: "File", sValue: sFile });

		        kDetail.className	= "detail";
		        kCell.colSpan		= 5;
		        kField.className	= "querymeta queryfield";	// 항목 (이름: 값) 단위로 줄바꿈

		        kField.append(...arrItem.map(kItem =>	// 이름: 값 (· 구분: CSS)
		        {
		            const kSpan = Object.assign(document.createElement("span"), { textContent: `${kItem.sName}: ${kItem.sValue}` });

		            if (kItem.sName !== "File")	// 쿼리 필드 (File 은 필드 아님): Ctrl·Alt+클릭 → 쿼리칸에 필드:값
		            {
		                kSpan.dataset.field = kItem.sName;
		                kSpan.dataset.value = kItem.sValue;
		            }

		            return kSpan;
		        }));
		        kText.className		= "querytext";
		        Query.AppendMark(kText, kLine.sOrigin ?? kLine.sText);	// 원문 그대로 (시간·Type 포함, 쿼리 단어 강조)

		        kCell.append(kField, kText);
		        kRow.after(kDetail);
		        kRow.classList.add("open");
		    }

		    static ShowPager()	// 페이지 번호 (PAGE_BLOCK 개씩, ‹ › 앞뒤 묶음, 현재 페이지 강조, 결과 없음: 숨김)
		    {
		        const nPageCount	= Math.ceil(Query.nTotal / Query.nPageSize);								// 전체 페이지 수
		        const nFirst		= Math.floor(Query.nPage / Query.PAGE_BLOCK) * Query.PAGE_BLOCK;		// 이 묶음 첫 페이지
		        const nLast			= Math.min(nPageCount, nFirst + Query.PAGE_BLOCK);						// 이 묶음 끝 (미포함)
		        const kFragment		= document.createDocumentFragment();

		        const funcAdd = (sText, nPage, sTitle) =>	// 페이지 버튼 1개
		        {
		            const kButton = document.createElement("button");

		            kButton.type		= "button";
		            kButton.textContent	= sText;
		            kButton.title		= sTitle;

		            if (sText === String(Query.nPage + 1))
		            {
		                kButton.className = "cur";
		            }

		            kButton.addEventListener("click", () => Query.LoadPage(nPage));

		            kFragment.append(kButton);
		        };

		        if (nFirst > 0)
		        {
		            funcAdd("‹", nFirst - 1, "이전 페이지 묶음");
		        }

		        for (let nPage = nFirst; nPage < nLast; ++nPage)
		        {
		            funcAdd(String(nPage + 1), nPage, `${(nPage * Query.nPageSize + 1).toLocaleString()} ~ ${Math.min(Query.nTotal, (nPage + 1) * Query.nPageSize).toLocaleString()}`);
		        }

		        if (nLast < nPageCount)
		        {
		            funcAdd("›", nLast, "다음 페이지 묶음");
		        }

		        kQueryPager.replaceChildren(kFragment);

		        kQueryPager.hidden = nPageCount === 0;

		        Query.FitLogHeight();	// 페이지 번호 보임·숨김: 로그 표 높이 다시
		    }

		    static FitLogHeight()	// 쿼리 2번 창 로그 표 높이: 브라우저 창 아래 끝에 맞춤 (페이지 번호까지 한 화면, 최소 Search.SPLIT_MIN, 첫 화면만)
		    {
		        FitHeight(kQueryScroll, kQueryPanel);
		    }

		    static OpenSetting(bAlarm = false, bRule = false, bSearch = false)	// 쿼리 설정 창 열기 (누른 버튼 기능만, 저장 버튼: 제목 저장·저장 쿼리 목록·선택한 제목 줄 보이게, 알람 버튼: 제목 알람·알람 목록, 필드 버튼: 제목 필드·필드 정의 목록, bSearch: 서치 탭 저장 버튼 → 저장 서치 목록)
		    {
		        Query.ShowSettingTab(bAlarm, bRule, bSearch);

		        if (!bSearch)
		        {
		            Query.LoadAlarm();	// 알람 목록 (열 때마다 새로, 서버: 설정 파일 다시 읽음)
		            Query.LoadRule();		// 필드 정의 목록 (열 때마다 새로, 서버: 설정 파일 다시 읽음)
		        }

		        kQuerySettingDialog.showModal();

		        kQuerySettingDialog.scrollTop = 0;	// 맨 위부터 (닫기 버튼 autofocus → 아래로 스크롤된 것 되돌림, 포커스: 닫기 그대로)

		        const kOwner = bSearch ? Search : Query;	// 목록·선택 제목 둔 곳

		        Query.ShowSaved(kOwner.arrSaved, kOwner.sSavedKey, bSearch);	// 선택한 제목 줄 보이게 (창 열린 뒤, 먼저 둔 목록)
		        Query.LoadSaved(kOwner.sSavedKey, bSearch);					// 목록 (열 때마다 새로, 서버: 설정 파일 다시 읽음)
		    }

		    static async LoadField()	// 왼쪽 필드 이름만 (통계 전·통계 실패 대비: 값 수 -, 그새 통계로 필드 그림: 안 그림)
		    {
		        try
		        {
		            const kResponse = await fetch("/api/query/field");

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData = await kResponse.json();

		            if (Query.arrField.length === 0)
		            {
		                Query.ShowField(kData.arrField);
		            }
		        }
		        catch (kError)	// 요청 실패: 다음 쿼리 통계 때 그림
		        {
		        }
		    }

		    static async LoadStat(kStep = Query.arrHistory[Query.nHistory])	// 왼쪽 필드별 통계 (쿼리마다 1번, 앞 요청 끊음·늦게 온 결과 버림, 중단 버튼: 끊음, kStep: 결과 보관 단계)
		    {
		        const nSeq		= ++Query.nStatSeq;		// 이 요청 번호
		        const kAbort	= new AbortController();	// 중단 (다음 통계·중단 버튼)

		        Query.kStatAbort?.abort();	// 앞 통계 끊음 (서버 계산도 멈춤)

		        Query.kStatAbort		= kAbort;
		        Query.bStatLoading	= true;

		        Query.ShowField(Query.arrField.map(kField => ({ sName: kField.sName, bBase: kField.bBase })));	// 값 수: …

		        try
		        {
		            const kResponse = await fetch("/api/query/stat?" + new URLSearchParams({ sKey: Query.sQueryKey, nStart: String(Query.nStart), nEnd: String(Query.nEnd), nLimit: String(nQueryLimitSec) }), { signal: kAbort.signal });

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData = await kResponse.json();

		            if (nSeq !== Query.nStatSeq)	// 뒤 요청 있음: 버림
		            {
		                return;
		            }

		            Query.bStatLoading = false;

		            Query.ShowField(kData.arrField);

		            if (kData.sError === "")	// 이 단계 필드 통계 (← →·새 창: 그대로)
		            {
		                Query.SaveStepResult(kStep, { arrField: kData.arrField });
		            }
		        }
		        catch (kError)	// 중단·요청 실패: 값 수 -
		        {
		            if (nSeq === Query.nStatSeq)
		            {
		                Query.bStatLoading = false;

		                Query.ShowField(Query.arrField.map(kField => ({ sName: kField.sName, bBase: kField.bBase })));
		            }
		        }
		        finally
		        {
		            if (Query.kStatAbort === kAbort)
		            {
		                Query.kStatAbort = null;

		                if (!Query.bQuerying)	// 쿼리도 끝남: 중단 끔
		                {
		                    kQueryStopButton.disabled = true;
		                }
		            }
		        }
		    }

		    static ShowField(arrField)	// 왼쪽 필드 그리기: 이름·값 수 (99 넘음: 99+, 가져오는 중: …, 통계 전: -, 추가 필드 0: 뺌), 클릭: 값 목록 새 창
		    {
		        const kFragment	= document.createDocumentFragment();
		        const arrShow	= arrField.filter(kField => kField.bBase || Query.bStatLoading || kField.nValue !== 0);	// 그릴 필드 (추가 필드: 값 0 이면 뺌)

		        Query.arrField	= arrField;
		        Query.sMarkKey	= null;	// 필드 이름 바뀜: 강조 단어 다시 만듦

		        for (const [nIndex, kField] of arrShow.entries())
		        {
		            const kItem	= document.createElement("li");
		            const kName	= Query.MakeFieldButton(kField.sName, Query.bStatLoading ? "…" : (kField.nValue === undefined ? "-" : (kField.nValue > 99 ? "99+" : String(kField.nValue))));

		            kItem.classList.toggle("baseend", kField.bBase && arrShow[nIndex + 1]?.bBase !== true);	// 기본 필드 끝: 아래 구분선
		            kName.className		= "fieldname";
		            kName.dataset.field	= kField.sName;
		            kName.title			= `${kField.sName}(${kField.bBase ? "기본 필드" : "설정 aField"})`;

		            kItem.append(kName);
		            kFragment.append(kItem);
		        }

		        kQueryFieldList.replaceChildren(kFragment);
		    }

		    static MakeFieldButton(sText, sCount)	// 왼쪽 필드 버튼 (왼쪽 글자 + 오른쪽 건수)
		    {
		        const kButton = document.createElement("button");

		        kButton.type = "button";

		        kButton.append(Object.assign(document.createElement("span"), { textContent: sText }), Object.assign(document.createElement("span"), { className: "count", textContent: sCount }));

		        return kButton;
		    }

		    static OnFieldClick(kEvent)	// 왼쪽 필드 클릭: 그 필드 값 목록 새 창 (지금 쿼리·기간, 쿼리 전: 없음)
		    {
		        const kButton = kEvent.target.closest("button[data-field]");	// 누른 필드

		        if (kButton == null || Query.sQueryKey === "")
		        {
		            return;
		        }

		        OpenWindow("/query/field?" + new URLSearchParams({ sKey: Query.sQueryKey, nStart: String(Query.nStart), nEnd: String(Query.nEnd), sField: kButton.dataset.field }), 640, 800, true);	// 지금 브라우저 창 중앙
		    }

		    static FilterField(sField, sValue)	// 필드 새 창 값 클릭 (새 창이 window.opener.Query 로 호출): 지금 기간 그대로 필드:값 쿼리 (쿼리 전·쿼리 중: false)
		    {
		        return Query.FilterKey(`${sField}:${sValue}`);	// 그 필드 값만 (지금 쿼리는 바꿈)
		    }

		    static OnWordClick(kEvent)	// 쿼리 결과 단어·펼친 필드 값: Ctrl+클릭 → AND 단어 (필드: 필드:값), Alt+클릭 → OR, 둘 다 → NOT (키 뗄 때 쿼리)
		    {
		        const kItem = kEvent.target.closest(".queryfield span[data-field]");	// 펼친 줄 필드 값 (Machine·Folder·Type·추가 필드)

		        if (kItem != null && (kEvent.ctrlKey || kEvent.altKey))
		        {
		            kEvent.preventDefault();

		            Query.AppendWord(`${kItem.dataset.field}:${kItem.dataset.value}`, kEvent.ctrlKey && kEvent.altKey ? "NOT" : (kEvent.altKey ? "OR" : "AND"), kQueryKey);	// 다시 클릭: 뺌

		            return;
		        }

		        Query.ClickWord(kEvent, "td.log, .querytext", true, kQueryKey);
		    }

		    static ClickWord(kEvent, sBox, bLead, kInput)	// sBox 칸 단어 Ctrl·Alt+클릭 → kInput 끝에 AND·OR·NOT 단어 (bLead: 맨 앞 시간 무시)
		    {
		        if (!(kEvent.ctrlKey || kEvent.altKey) || kEvent.target.closest(sBox) == null)
		        {
		            return;
		        }

		        let sWord = Query.WordAt(kEvent.clientX, kEvent.clientY, sBox, bLead);	// 누른 단어

		        if (!/[^-_]/.test(sWord))	// 단어 없음 (-·_ 만)
		        {
		            return;
		        }

		        if (/^(and|or|not)$/i.test(sWord))	// 연산자 글자 (대소문자 무시 → 검색어로 못 넣음): 안 함
		        {
		            return;
		        }

		        kEvent.preventDefault();

		        Query.AppendWord(sWord, kEvent.ctrlKey && kEvent.altKey ? "NOT" : (kEvent.altKey ? "OR" : "AND"), kInput);	// Ctrl: AND, Alt: OR, 둘 다: NOT
		    }

		    static AppendWord(sWord, sOp, kInput = kQueryKey)	// kInput 끝에 sOp 단어 (비움: 단어만, NOT: 쿼리칸 * NOT 단어·검색어 안 더함, 이미 있음: 뺌, 실행: OnModifierUp)
		    {
		        const sKey		= kInput.value.trim();					// 지금 칸
		        const sRemoved	= Query.RemoveWord(sKey, sWord);	// 이미 있는 단어: 뺀 식 (없음: null)

		        if (sRemoved === null && sKey === "" && sOp === "NOT" && kInput !== kQueryKey)	// 검색어: 전체 (*) 없음
		        {
		            return;
		        }

		        kInput.value			= sRemoved ?? (sKey !== "" ? `${sKey} ${sOp} ${sWord}` : (sOp === "NOT" ? `* NOT ${sWord}` : sWord));	// 비움 + NOT: 그 단어 없는 전체
		        kInput.scrollLeft		= kInput.scrollWidth;	// 끝 (추가한 단어) 보이게
		        Query.kWordInput		= kInput;
		        Query.bWordAdded		= true;

		        if (kInput === kQueryKey)
		        {
		            Query.OnQueryInput();	// 선택한 저장 쿼리와 달라지면 선택 해제
		        }
		    }

		    static RemoveWord(sKey, sWord)	// 식에서 단어 빼기 (대소문자 무시, 앞 연산자 함께, 빈 괄호·단어 하나 괄호·남은 연산자 정리, 예: A AND B OR C 에서 B → A OR C, 없음: null)
		    {
		        const arrToken	= [];	// 낱말 (단어·연산자·괄호)
		        const funcOp	= (nIndex) => /^(AND|OR|NOT)$/i.test(arrToken[nIndex] ?? "");	// 연산자 자리 (대소문자 무시)
		        const funcWord	= (nIndex) => nIndex >= 0 && nIndex < arrToken.length && !funcOp(nIndex) && arrToken[nIndex] !== "(" && arrToken[nIndex] !== ")";	// 단어 자리

		        for (let sItem of sKey.split(/\s+/).filter(sItem => sItem !== ""))	// 앞 '('·뒤 ')' 떼기 (단어 안 짝 맞는 괄호는 그대로, 예: f(x))
		        {
		            let nClose = 0;	// 뗀 ')'

		            while (sItem.startsWith("("))
		            {
		                arrToken.push("(");
		                sItem = sItem.slice(1);
		            }

		            while (sItem.endsWith(")") && sItem.split("(").length < sItem.split(")").length)
		            {
		                sItem = sItem.slice(0, -1);
		                ++nClose;
		            }

		            if (sItem !== "")
		            {
		                arrToken.push(sItem);
		            }

		            arrToken.push(...Array(nClose).fill(")"));
		        }

		        const nWord = arrToken.findIndex((sItem, nIndex) => funcWord(nIndex) && sItem.toLowerCase() === sWord.toLowerCase());	// 뺄 단어

		        if (nWord < 0)
		        {
		            return null;
		        }

		        arrToken.splice(funcOp(nWord - 1) ? nWord - 1 : nWord, funcOp(nWord - 1) || funcOp(nWord + 1) ? 2 : 1);

		        for (let bChanged = true; bChanged; )	// 정리: 바뀐 게 없을 때까지
		        {
		            bChanged = false;

		            for (let nIndex = 0; nIndex < arrToken.length && !bChanged; ++nIndex)
		            {
		                if (arrToken[nIndex] === "(" && arrToken[nIndex + 1] === ")")	// 빈 괄호
		                {
		                    arrToken.splice(nIndex, 2);
		                    bChanged = true;
		                }
		                else if (arrToken[nIndex] === "(" && funcWord(nIndex + 1) && arrToken[nIndex + 2] === ")")	// 단어 하나 괄호
		                {
		                    arrToken.splice(nIndex + 2, 1);
		                    arrToken.splice(nIndex, 1);
		                    bChanged = true;
		                }
		                else if (arrToken[nIndex] === "(" && arrToken[nIndex + 1] === "(" && arrToken[Query.FindClose(arrToken, nIndex + 1) + 1] === ")")	// 겹 괄호 ((A AND B))
		                {
		                    arrToken.splice(Query.FindClose(arrToken, nIndex + 1) + 1, 1);
		                    arrToken.splice(nIndex, 1);
		                    bChanged = true;
		                }
		                else if (funcOp(nIndex) && (nIndex === 0 || nIndex === arrToken.length - 1 || arrToken[nIndex - 1] === "(" || arrToken[nIndex + 1] === ")" || funcOp(nIndex + 1)))	// 남은 연산자 (맨 앞·끝·괄호 안 끝·연산자 연속)
		                {
		                    arrToken.splice(nIndex, 1);
		                    bChanged = true;
		                }
		            }
		        }

		        return arrToken.reduce((sResult, sItem, nIndex) => sResult + (nIndex === 0 || sItem === ")" || arrToken[nIndex - 1] === "(" ? "" : " ") + sItem, "");
		    }

		    static FindClose(arrToken, nOpen)	// nOpen 의 '(' 와 짝인 ')' 위치 (없음: -2)
		    {
		        for (let nIndex = nOpen, nDepth = 0; nIndex < arrToken.length; ++nIndex)
		        {
		            nDepth += arrToken[nIndex] === "(" ? 1 : (arrToken[nIndex] === ")" ? -1 : 0);

		            if (nDepth === 0)
		            {
		                return nIndex;
		            }
		        }

		        return -2;
		    }

		    static OnModifierUp(kEvent)	// Ctrl·Alt 뗌 (창 벗어남 포함): 단어 더했으면 둘 다 뗀 뒤 MODIFIER_WAIT_MS 뒤 쿼리·서치 (다 뺌: 안 함, 그 사이 다시 누름: 이어서 더함, 지금 단계 하위)
		    {
		        if (!Query.bWordAdded || (kEvent.type === "keyup" && kEvent.key !== "Control" && kEvent.key !== "Alt"))
		        {
		            return;
		        }

		        if (kEvent.type === "keyup")
		        {
		            kEvent.preventDefault();	// Alt 뗌: 브라우저 메뉴로 안 감

		            if (kEvent.ctrlKey || kEvent.altKey)	// 다른 키 아직 누름 (Ctrl·Alt 바꾸는 중): 다 뗄 때까지 기다림
		            {
		                return;
		            }
		        }

		        clearTimeout(Query.nModifierTimer);

		        Query.nModifierTimer = setTimeout(() =>
		        {
		            Query.nModifierTimer	= 0;
		            Query.bWordAdded		= false;

		            if (Query.kWordInput === kKey)	// 검색어: 서치 (지금 단계 하위)
		            {
		                if (kKey.value.trim() !== "")
		                {
		                    Search.SearchLog(true);
		                }
		            }
		            else if (kQueryKey.value.trim() !== "")	// 쿼리칸: 쿼리 (다 뺌: 안 함)
		            {
		                Query.FilterKey(kQueryKey.value.trim());
		            }
		        }, Query.MODIFIER_WAIT_MS);
		    }

		    static OnModifierDown(kEvent)	// Ctrl·Alt 누름: 기다리던 쿼리 취소 (Ctrl → Alt 로 빨리 바꿔도 이어서 더함)
		    {
		        if (kEvent.key !== "Control" && kEvent.key !== "Alt")
		        {
		            return;
		        }

		        if (Query.nModifierTimer !== 0)
		        {
		            clearTimeout(Query.nModifierTimer);

		            Query.nModifierTimer = 0;
		        }
		    }

		    static WordAt(nX, nY, sBox = "td.log, .querytext", bLead = true)	// 화면 위치 글자의 단어 (서버 SplitTerm 과 같음, IP·IP:포트·날짜·시간: 통째로, bLead: 맨 앞 시간 무시, 없음: "")
		    {
		        const kCaret	= document.caretPositionFromPoint?.(nX, nY) ?? null;					// 누른 글자 위치 (표준)
		        const kRange	= kCaret == null ? document.caretRangeFromPoint?.(nX, nY) ?? null : null;	// 누른 글자 위치 (예전 Chrome)
		        const kNode		= kCaret?.offsetNode ?? kRange?.startContainer;
		        const kBox		= kNode?.parentElement?.closest(sBox);	// 누른 칸 (강조 mark 로 나뉜 글자도 칸 전체로)

		        if (kNode?.nodeType !== Node.TEXT_NODE || kBox == null || kNode.parentElement.closest(".lno") != null)	// 서치 2번 창 줄 번호: 무시
		        {
		            return "";
		        }

		        const kBefore = document.createRange();	// 칸 처음 ~ 누른 위치

		        kBefore.setStart(kBox, 0);
		        kBefore.setEnd(kNode, kCaret?.offset ?? kRange.startOffset);

		        const sText	= kBox.textContent;				// 칸 전체 글자
		        const nPos	= kBefore.toString().length;	// 누른 위치 (칸 기준)
		        const kLead	= /^\[\d{4}\/\d{2}\/\d{2} \d{2}:\d{2}:\d{2}(\.\d+)?\]/.exec(sText);	// 줄 맨 앞 시간 (색인에서 빼는 부분)

		        if (bLead && kLead != null && nPos < kLead[0].length)	// 맨 앞 시간: 무시 (쿼리해도 못 찾음, 시간은 D·S·E·막대로)
		        {
		            return "";
		        }

		        const sIp	= Query.GetSpan(sText, nPos, /[0-9.:]/).replace(/[.:]+$/, "");	// 숫자·점·콜론 이어진 것 (끝 . : 뺌)

		        if (/^\d{1,3}(\.\d{1,3}){3}(:\d{1,5})?$/.test(sIp) || /^\d{1,2}:\d{2}(:\d{2}(\.\d{1,7})?)?$/.test(sIp))	// IP·IP:포트·시간: 통째로
		        {
		            return sIp;
		        }

		        const sDate = Query.GetSpan(sText, nPos, /[0-9/]/).replace(/\/+$/, "");	// 숫자·슬래시 이어진 것 (끝 / 뺌)

		        if (/^(\d{4}\/)?\d{1,2}\/\d{1,2}$/.test(sDate))	// 날짜 (yyyy/MM/dd·MM/dd): 통째로 (yyyy-MM-dd 는 단어로 통째)
		        {
		            return sDate;
		        }

		        return Query.GetSpan(sText, nPos, /[\p{L}\p{Nd}_-]/u);
		    }

		    static GetSpan(sText, nPos, regexChar)	// nPos 앞뒤로 regexChar 글자 이어진 것
		    {
		        let nStart	= nPos;
		        let nEnd	= nPos;

		        while (nStart > 0 && regexChar.test(sText[nStart - 1]))
		        {
		            --nStart;
		        }

		        while (nEnd < sText.length && regexChar.test(sText[nEnd]))
		        {
		            ++nEnd;
		        }

		        return sText.slice(nStart, nEnd);
		    }

		    static FilterKey(sKey)	// 지금 기간 그대로 sKey 만 쿼리 (단계 남김, 쿼리 전·쿼리 중: false)
		    {
		        const kStep = Query.arrHistory[Query.nHistory];	// 지금 단계 (기간)

		        if (kStep === undefined || Query.bQuerying)
		        {
		            return false;
		        }

		        if (!kQueryButton.classList.contains("cur"))	// 다른 탭 보는 중: 쿼리 탭으로
		        {
		            ShowPanel("query");
		        }

		        Query.SetPicker(kStart, kStartHour, kStartMinute, kStep.kFrom);	// S·E·D 도 그 단계로 (← → 와 같음)
		        Query.SetPicker(kEnd, kEndHour, kEndMinute, kStep.kTo);

		        kQueryRange.selectedIndex	= kStep.nRange;
		        kQueryKey.value				= sKey;

		        Query.OnQueryInput();	// 선택한 저장 쿼리와 달라지면 선택 해제
		        Query.PushBack(true);	// 실행 기억 (모래시계, Ctrl·Alt 놓기·필드 값 클릭 자동 실행)
		        Query.Query(sKey, kStep.kFrom, kStep.kTo, true);

		        return true;
		    }

		    static async LoadFieldWindow(kQuery)	// 필드 새 창: 주소의 쿼리·기간·필드로 값 전체 (건수 많은 순) 가져와 첫 페이지
		    {
		        const sKey		= kQuery.get("sKey") ?? "";
		        const nStart	= Number(kQuery.get("nStart") ?? 0);
		        const nEnd		= Number(kQuery.get("nEnd") ?? 0);
		        const sRange	= `"${sKey}" ${Query.FormatMinute(nStart)} ~ ${Query.FormatMinute(nEnd)}`;	// 쿼리·기간

		        Query.sFieldName				= kQuery.get("sField") ?? "";
		        kQueryFieldPanel.hidden			= false;
		        kQueryFieldColumn.firstChild.nodeValue	= Query.sFieldName;	// 칸 이름 글자만 (칸 너비 손잡이 유지)
		        kQueryFieldTitle.textContent	= `${Query.sFieldName} 가져오는 중…`;
		        document.title					= `${Query.sFieldName} · ${sRange} - ${document.title}`;	// 창 구분용

		        try
		        {
		            const kResponse = await fetch("/api/query/stat?" + new URLSearchParams({ sKey, nStart: String(nStart), nEnd: String(nEnd), sField: Query.sFieldName, nLimit: String(nQueryLimitSec) }));

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData		= await kResponse.json();
		            const kField	= kData.arrField[0] ?? { nValue: 0, arrValue: [] };	// 그 필드 (없는 필드: 빈 목록)

		            Query.arrFieldValue	= kField.arrValue;
		            Query.nFieldTotal		= kData.nTotal;

		            kQueryFieldTitle.textContent = kData.sError !== "" ? `${Query.sFieldName} 값 목록 실패: ${kData.sError}` : `${Query.sFieldName} · 전체 ${kData.nTotal.toLocaleString()}건 · 값 ${kField.nValue.toLocaleString()}개 (${sRange})`;

		            Query.ShowFieldPage(0);
		            Query.FitFieldWindow();
		        }
		        catch (kError)	// 요청 실패
		        {
		            kQueryFieldTitle.textContent = `${Query.sFieldName} 가져오기 실패: ${ErrorText(kError)}`;
		        }
		    }

		    static ShowFieldPage(nPage)	// 필드 새 창 nPage 페이지 (FIELD_PAGE 개씩, 페이지 번호 PAGE_BLOCK 개씩, 한 페이지: 숨김)
		    {
		        const nPageCount	= Math.max(1, Math.ceil(Query.arrFieldValue.length / Query.FIELD_PAGE));	// 전체 페이지 수
		        const kRows			= document.createDocumentFragment();
		        const kPager		= document.createDocumentFragment();

		        Query.nFieldPage = Math.min(Math.max(0, nPage), nPageCount - 1);

		        const nFirst	= Query.nFieldPage * Query.FIELD_PAGE;								// 이 페이지 첫 값
		        const nBlock	= Math.floor(Query.nFieldPage / Query.PAGE_BLOCK) * Query.PAGE_BLOCK;	// 이 묶음 첫 페이지
		        const nLast		= Math.min(nPageCount, nBlock + Query.PAGE_BLOCK);						// 이 묶음 끝 (미포함)

		        Query.arrFieldValue.slice(nFirst, nFirst + Query.FIELD_PAGE).forEach((kValue, nIndex) =>
		        {
		            const kRow = document.createElement("tr");

		            kRow.className		= "row";
		            kRow.dataset.value	= kValue.sValue;
		            kRow.dataset.no		= String(nFirst + nIndex);	// 원래 순서
		            kRow.title			= `${(kValue.nCount * 100 / Math.max(1, Query.nFieldTotal)).toFixed(1)}% · ${Query.sFieldName}:${kValue.sValue}`;

		            AddCell(kRow, (nFirst + nIndex + 1).toLocaleString(), "num");
		            AddCell(kRow, kValue.sValue, "log");
		            AddCell(kRow, String(kValue.nCount), "nowrap");	// Count: 왼쪽, 숫자만 (쉼표 없음)

		            kRows.append(kRow);
		        });

		        const funcAdd = (sText, nTarget, sTitle) =>	// 페이지 버튼 1개
		        {
		            const kButton = document.createElement("button");

		            kButton.type		= "button";
		            kButton.textContent	= sText;
		            kButton.title		= sTitle;

		            if (sText === String(Query.nFieldPage + 1))
		            {
		                kButton.className = "cur";
		            }

		            kButton.addEventListener("click", () => Query.ShowFieldPage(nTarget));

		            kPager.append(kButton);
		        };

		        if (nBlock > 0)
		        {
		            funcAdd("‹", nBlock - 1, "이전 페이지 묶음");
		        }

		        for (let nTarget = nBlock; nTarget < nLast; ++nTarget)
		        {
		            funcAdd(String(nTarget + 1), nTarget, `${(nTarget * Query.FIELD_PAGE + 1).toLocaleString()} ~ ${Math.min(Query.arrFieldValue.length, (nTarget + 1) * Query.FIELD_PAGE).toLocaleString()}`);
		        }

		        if (nLast < nPageCount)
		        {
		            funcAdd("›", nLast, "다음 페이지 묶음");
		        }

		        kQueryFieldBody.replaceChildren(kRows);
		        kQueryFieldPager.replaceChildren(kPager);
		        Query.SortFieldRows();	// 정렬 중: 새 페이지도 같은 정렬

		        kQueryFieldPager.hidden		= nPageCount <= 1;
		        kQueryFieldScroll.scrollTop	= 0;
		    }

		    static OnFieldSortClick(kEvent)	// 필드 새 창 제목 클릭: 그 칸 기준 보이는 페이지만 정렬 (내림차순 → 오름차순 → 원래 순서, 칸 너비 손잡이: 무시)
		    {
		        const kHead = kEvent.target.closest("th");

		        if (kHead == null || kEvent.target.closest(".colgrip") != null)
		        {
		            return;
		        }

		        if (Query.nFieldSort !== kHead.cellIndex)	// 다른 칸: 내림차순부터
		        {
		            Query.nFieldSort	= kHead.cellIndex;
		            Query.bFieldAsc		= false;
		        }
		        else if (!Query.bFieldAsc)	// 내림차순 → 오름차순
		        {
		            Query.bFieldAsc = true;
		        }
		        else	// 오름차순 → 원래 순서
		        {
		            Query.nFieldSort = -1;
		        }

		        Query.SortFieldRows();
		    }

		    static SortFieldRows()	// 필드 새 창 보이는 줄 정렬 (칸 값 모두 숫자: 숫자, 아님: 글자 속 숫자 크기 순, 같음: 원래 순서) + 제목 ▲·▼ 표시
		    {
		        const nColumn		= Query.nFieldSort;
		        const nSign			= Query.bFieldAsc ? 1 : -1;
		        const arrRow		= [...kQueryFieldBody.rows];
		        const funcNo		= (kRow) => Number(kRow.dataset.no);	// 원래 순서
		        const funcText		= (kRow) => kRow.cells[nColumn].textContent;	// 그 칸 글자
		        const funcNumber	= (sText) => sText.trim() === "" ? NaN : Number(sText.replaceAll(",", ""));	// 숫자 (아님: NaN, No.: 쉼표 뺌)

		        if (nColumn < 0)	// 원래 순서
		        {
		            arrRow.sort((kA, kB) => funcNo(kA) - funcNo(kB));
		        }
		        else if (arrRow.every(kRow => !Number.isNaN(funcNumber(funcText(kRow)))))	// 모두 숫자: 숫자 비교
		        {
		            arrRow.sort((kA, kB) => (funcNumber(funcText(kA)) - funcNumber(funcText(kB))) * nSign || funcNo(kA) - funcNo(kB));
		        }
		        else	// 글자 비교 (WA9 < WA10)
		        {
		            const kCollator = new Intl.Collator(undefined, { numeric: true, sensitivity: "base" });

		            arrRow.sort((kA, kB) => kCollator.compare(funcText(kA), funcText(kB)) * nSign || funcNo(kA) - funcNo(kB));
		        }

		        kQueryFieldBody.append(...arrRow);	// 같은 줄 순서만 바꿈

		        for (const kHead of kQueryFieldBody.closest("table").tHead.rows[0].cells)	// 정렬 칸 제목 오른쪽: ▲ 오름차순, ▼ 내림차순
		        {
		            kHead.querySelector(".sortmark").textContent = kHead.cellIndex !== nColumn ? "" : Query.bFieldAsc ? "▲" : "▼";
		        }
		    }

		    static FitFieldWindow()	// 필드 새 창 너비: 전체 값 중 가장 긴 줄에 맞춤 (넓히기만, 화면 너비까지, 가운데 유지, 새 탭·크기 막힘: 그대로)
		    {
		        const kCell = kQueryFieldBody.querySelector("td.log");	// 값 칸 (글꼴 기준, 없음: 빈 목록)

		        if (kCell == null)
		        {
		            return;
		        }

		        const kTable	= kQueryFieldBody.parentElement;									// 값 표
		        const kContext	= document.createElement("canvas").getContext("2d");				// 글자 폭 재기
		        const nFirst	= Query.nFieldPage * Query.FIELD_PAGE;							// 이 페이지 첫 값

		        kContext.font = getComputedStyle(kCell).font;

		        const funcMax = (arrValue) => arrValue.reduce((nMax, kValue) => Math.max(nMax, kContext.measureText(kValue.sValue).width), 0);	// 가장 긴 값 폭

		        kTable.style.width = "max-content";	// 칸 원래 폭 (잠깐)

		        const nTable = kTable.offsetWidth;	// 이 페이지 표 원래 폭

		        kTable.style.width = "";

		        const nGrow		= Math.ceil(nTable + funcMax(Query.arrFieldValue) - funcMax(Query.arrFieldValue.slice(nFirst, nFirst + Query.FIELD_PAGE)) - kQueryFieldScroll.clientWidth);	// 더 넓힐 폭 (다른 페이지 더 긴 값 포함)
		        const nOuter	= window.outerWidth;															// 지금 바깥 폭
		        const nWidth	= Math.min(screen.availWidth, nOuter + nGrow);									// 새 바깥 폭 (화면까지)
		        const nLeft		= screen.availLeft || 0;														// 화면 왼쪽

		        if (nWidth <= nOuter)
		        {
		            return;
		        }

		        try
		        {
		            window.resizeTo(nWidth, window.outerHeight);
		            window.moveTo(Math.max(nLeft, Math.min(window.screenX - Math.round((nWidth - nOuter) / 2), nLeft + screen.availWidth - nWidth)), window.screenY);	// 넓힌 만큼 왼쪽으로 (가운데 유지, 화면 안)
		        }
		        catch (kError)	// 크기 바꾸기 막힘: 그대로
		        {
		            console.error(kError);
		        }
		    }

		    static OnFieldWindowClick(kEvent)	// 필드 새 창 값 클릭: 연 창에서 필드:값 쿼리 후 이 창 닫음 (연 창 닫힘·접근 불가·쿼리 중: 그대로)
		    {
		        const kRow = kEvent.target.closest("tr.row");	// 누른 값

		        if (kRow == null)
		        {
		            return;
		        }

		        try
		        {
		            if (window.opener?.Query?.FilterField?.(Query.sFieldName, kRow.dataset.value) === true)
		            {
		                window.opener.focus();
		                window.close();
		            }
		        }
		        catch (kError)	// 연 창 접근 불가
		        {
		            console.error(kError);
		        }
		    }
		}
		""";
	}
}

class QueryResult	// 쿼리 결과 (TabQuery: 페이지 넘김용 보관, 시간별 건수 + 짧은 기간만 맞는 줄 → 메모리 작음, 통계: 조건만)
{
	public readonly string										m_sKey;												// 기간·쿼리
	public readonly DateTime									m_kTime			= DateTime.UtcNow;							// 계산 시각
	public readonly List<(string sWord, ulong nHash)>			m_listWord		= new List<(string sWord, ulong nHash)>();	// 쿼리 단어·해시 (읽을 포스팅)
	public readonly List<string>								m_listField		= new List<string>();						// 쿼리 필드 키 "필드\t값" (읽을 포스팅)
	public readonly ConcurrentDictionary<long, (int nMs, int nDoc)[]>	m_hmapHit	= new ConcurrentDictionary<long, (int nMs, int nDoc)[]>();	// 원문 시간 → 맞는 줄 (분초·줄 번호, 짧은 기간은 계산 때, 긴 기간은 페이지 때 그 시간만)
	public readonly HashSet<long>								m_setSorted		= new HashSet<long>();						// 최신 순 정렬한 원문 시간 (페이지 읽을 때 처음 1번만)
	public long													m_nHitCount;											// m_hmapHit 맞는 줄 합 (Interlocked, 보관 상한 계산, 페이지 때 늘어난 것 포함)
	public readonly List<object>								m_listMinute	= new List<object>();						// 분별 건수 {nMinute, nCount} (기간 1시간 이하만)
	public readonly List<object>								m_listSecond	= new List<object>();						// 초별 건수 {nSecond, nCount} (기간 1분 이하만)
	public SearchNode?											m_kRoot;												// 조건 트리
	public bool													m_bAll;													// 조건에 * (전체 로그)·와일드카드 있음 → 시간마다 모든 줄 번호
	public bool													m_bExact;												// 조건에 IP·날짜·시간·와일드카드 검색어 있음 → 후보 줄 원문 글자 확인 (사전 건수 빠른 길 안 씀)
	public DateTime												m_kFrom;												// 기간 시작 (포함)
	public DateTime												m_kTo;													// 기간 끝 (미포함)
	public long[]												m_arrHour		= Array.Empty<long>();						// 기간 시간 (yyyyMMddHH, 시간 순)
	public int[]												m_arrCount		= Array.Empty<int>();						// 시간별 건수 (m_arrHour 와 같은 순서)
	public int													m_nTotal;												// 전체 건수
	public string												m_sError		= "";										// 오류

	public QueryResult(string sKey)
	{
		m_sKey = sKey;
	}
}

class IngestWorker	// 적재 로직 스레드 1개 (TabQuery): 라이트 큐 (수신 스레드 Add) ↔ 리드 큐 스왑 (잠금: 스왑 순간) → 잠금 없이 처리 (원문 쓰기·색인 버퍼), 원문 파일 열어 둠, 줄마다 쓰는 버퍼·집합은 재사용 (할당 적게)
{
	private const int							m_nTypeMax		= 256;															// Type 문자열 재사용 최대 종류
	private const int							m_nWriteBufferKeep	= 1024 * 1024;												// Log 1건 원문 버퍼 평소 크기 (바이트, 큰 건으로 넘음: 처리 뒤 이 크기로 줄임)
	private const int							m_nRecordKeep		= 1024;														// Log 1건 레코드 목록 평소 칸 수 (넘음: 처리 뒤 줄임)

	private readonly object						m_kQueueLock	= new object();													// 라이트 큐 Add·스왑 잠금 (짧게)
	private List<LogBatch>						m_listWrite		= new List<LogBatch>();											// 라이트 큐 (수신 스레드 Add)
	private List<LogBatch>						m_listRead		= new List<LogBatch>();											// 리드 큐 (로직 스레드만, 스왑으로 바뀜)
	private int									m_nQueueLine;																	// 라이트 큐 줄 수 (수신 스레드 Interlocked, 워커·전체 최대 넘음: 역압)
	private readonly AutoResetEvent				m_kSignal		= new AutoResetEvent(false);									// 라이트 큐에 들어옴 → 로직 스레드 깨움
	private readonly Thread						m_kThread;																		// 로직 스레드
	private volatile bool						m_bStop;																		// 멈춤 요청 (남은 큐 처리 뒤 끝, m_nIngestStopMs 넘음: 받은 진행 관리 폴더 Log 버림)
	private long								m_nStopTick;																	// 멈춤 요청 시각 (TickCount64, 처리 상한 기준)
	private long								m_nWarnTick;																	// 마지막 부하 로그 시각 (TickCount64)
	private long								m_nIdleTick		= Environment.TickCount64;										// 마지막 원문 파일 정리 시각
	private readonly Dictionary<string, (FileStream kStream, long nTick)>	m_hmapStream	= new Dictionary<string, (FileStream kStream, long nTick)>(StringComparer.OrdinalIgnoreCase);	// 열어 둔 원문 파일 (경로 → 스트림·마지막 쓰기 시각, 로직 스레드만)
	private readonly Dictionary<string, Dictionary<string, Count>>	m_hmapCount	= new Dictionary<string, Dictionary<string, Count>>(StringComparer.OrdinalIgnoreCase);	// 머신 → 대상 폴더 → 받은 진행 (Count, 머신·폴더 해시 → 이 워커 담당분, 로직 스레드만, 클라 Count 목록 뒤 ~ 접속 종료까지 메모리 → 목록 밖·접속 종료: 닫고 뺌, 서버 종료: 닫음)
	public readonly object						m_kBufferLock	= new object();													// 색인 버퍼 잠금 (로직 스레드 Add ↔ 타이머 Flush 가 통째로 가져감)
	public Dictionary<long, IndexBuffer>		m_hmapBuffer	= new Dictionary<long, IndexBuffer>();							// 원문 시간 (yyyyMMddHH) → 색인 버퍼 (세그먼트 파일 전)
	public readonly MemoryStream				m_kWriteBuffer	= new MemoryStream();											// Log 1건 원문 레코드 모아 쓰기 (로직 스레드만)
	public readonly Utf8JsonWriter				m_kJson;																		// 원문 레코드 추가 필드 JSON 쓰기 (m_kWriteBuffer 끝에, 재사용)
	public readonly List<OriginRecord>			m_listRecord	= new List<OriginRecord>();										// Log 1건 줄마다 레코드 (재사용, 로직 스레드만)
	public readonly HashSet<ulong>				m_setHash		= new HashSet<ulong>();											// 줄 단어 해시 (줄마다 비움, 재사용)
	public readonly char[]						m_arrLower		= new char[TabQuery.m_nIndexWordMax];							// 단어 소문자 버퍼
	private readonly Dictionary<string, string>	m_hmapType		= new Dictionary<string, string>(StringComparer.Ordinal);		// Type 문자열 (같은 글자 재사용, 최대 m_nTypeMax 종류)
	public readonly int							m_nIndex;																		// 워커 번호 (로그)
	public AlarmMatcher?						m_kAlarmMatcher;													// 알람 카운터 기준 스냅샷 (버퍼 잠금)
	public AlarmCount							m_kAlarmCount	= new AlarmCount(0);								// 이번 분 알람별 맞은 줄 수·마지막 줄 (버퍼 잠금, 통계 스레드가 매분 가져감)
	public readonly List<(AlarmMatcher kMatcher, AlarmCount kCount)>	m_listAlarmOld	= new List<(AlarmMatcher kMatcher, AlarmCount kCount)>();	// 분 중간 스냅샷 바뀜: 앞 카운터 (통계 스레드가 같이 가져감)

	public IngestWorker(int nIndex)
	{
		m_nIndex	= nIndex;
		m_kJson		= new Utf8JsonWriter(m_kWriteBuffer);
		m_kThread	= new Thread(Run) { IsBackground = true, Name = $"Ingest{nIndex}" };

		m_kThread.Start();
	}

	public string InternType(ReadOnlySpan<char> kType)	// Type 글자 → 문자열 (같은 글자: 둔 것 재사용 → 줄마다 안 만듦, 종류 m_nTypeMax 넘음: 새로 만듦)
	{
		if (kType.Length == 0)
		{
			return "";
		}

		if (m_hmapType.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(kType, out string? sType))
		{
			return sType;
		}

		sType = kType.ToString();

		if (m_hmapType.Count < m_nTypeMax)
		{
			m_hmapType[sType] = sType;
		}

		return sType;
	}

	public void ReleaseBuffer()	// Log 1건 처리 뒤 (로직 스레드): 레코드 목록 비움 (받은 줄 문자열 안 잡음) + 큰 건으로 커진 원문 버퍼·목록 평소 크기로 (워커마다 최고치 유지 방지)
	{
		m_listRecord.Clear();

		if (m_listRecord.Capacity > m_nRecordKeep)
		{
			m_listRecord.Capacity = m_nRecordKeep;
		}

		if (m_kWriteBuffer.Capacity > m_nWriteBufferKeep)
		{
			m_kWriteBuffer.SetLength(0);

			m_kWriteBuffer.Capacity = m_nWriteBufferKeep;
		}
	}

	public AlarmCount GetAlarmCount(AlarmMatcher kMatcher)	// 버퍼 잠금 안: 이 스냅샷 카운터 (스냅샷 바뀜: 앞 카운터 넘기고 새로)
	{
		if (m_kAlarmMatcher != kMatcher)
		{
			if (m_kAlarmMatcher != null)
			{
				m_listAlarmOld.Add((m_kAlarmMatcher, m_kAlarmCount));
			}

			m_kAlarmMatcher	= kMatcher;
			m_kAlarmCount	= new AlarmCount(kMatcher.m_arrAlarm.Length);
		}

		return m_kAlarmCount;
	}

	public bool TryAdd(LogBatch kBatch)	// 수신 스레드: 라이트 큐 끝에 (이 워커 최대 줄 수·모든 워커 합 최대 줄 수·색인 버퍼 최대 줄 수 넘음: false → 호출 쪽이 기다렸다 다시)
	{
		if (Volatile.Read(ref m_nQueueLine) >= TabQuery.m_nIngestQueueWorkerMax || Volatile.Read(ref TabQuery.m_nIngestQueueLine) >= TabQuery.m_nIngestQueueMax || Volatile.Read(ref TabQuery.m_nIndexBufferLine) >= TabQuery.m_nIndexBufferMax)
		{
			return false;
		}

		lock (m_kQueueLock)
		{
			m_listWrite.Add(kBatch);
		}

		Interlocked.Add(ref m_nQueueLine, kBatch.listLine.Count);
		Interlocked.Add(ref TabQuery.m_nIngestQueueLine, kBatch.listLine.Count);
		m_kSignal.Set();

		return true;
	}

	public void RequestStop()	// 종료 요청: 남은 큐 처리 (m_nIngestStopMs 상한) 하고 원문 파일 닫은 뒤 스레드 끝남
	{
		m_nStopTick	= Environment.TickCount64;
		m_bStop		= true;

		m_kSignal.Set();
	}

	public void Join()	// 종료 기다림 (남은 라이트 큐 다 처리할 때까지, X초마다 남은 줄 로그)
	{
		while (!m_kThread.Join(TabQuery.m_nIngestWarnSec * 1000))
		{
			Log.Write(INFO_, $"종료 정리 대기: 워커 {m_nIndex} 라이트 큐 {Volatile.Read(ref m_nQueueLine):N0}줄");
		}
	}

	private bool HasWrite()	// 라이트 큐 남음 (Log·정리 표시)
	{
		lock (m_kQueueLock)
		{
			return m_listWrite.Count > 0;
		}
	}

	private void Run()	// 로직 스레드: 신호 (또는 m_nIngestIdleMs) 마다 스왑 → 처리, 멈춤 요청: 남은 것 처리 뒤 원문 파일 닫음 (예외: 출력 후 계속)
	{
		while (!m_bStop)
		{
			try
			{
				m_kSignal.WaitOne(TabQuery.m_nIngestIdleMs);
				Drain();
			}
			catch (Exception kException)	// 메모리 부족·디스크 오류 등
			{
				Log.Write(ERROR, $"적재 워커 {m_nIndex} 오류: {kException.Message}");

				Thread.Sleep(1000);
			}
		}

		try
		{
			do	// 남은 라이트 큐 빌 때까지 (Log 원문·받은 진행 저장, 정리 표시, 멈춤 요청 뒤 X초 넘음: 받은 진행 관리 폴더 Log 버림)
			{
				Drain();
			}
			while (HasWrite());
		}
		catch (Exception kException)	// 종료 중 오류
		{
			Log.Write(ERROR, $"적재 워커 {m_nIndex} 종료 오류: {kException.Message}");
		}

		CloseOriginStream(null);

		foreach (Count kCount in m_hmapCount.Values.SelectMany(hmapFolder => hmapFolder.Values))
		{
			kCount.Close();
		}
	}

	private void Drain()	// 큐 스왑 (잠금: 포인터 바꿈만) → 리드 큐 처리 (잠금 없음, Log·받은 진행 정리 표시 순서대로), 밀림: 부하 로그, 오래 안 쓴 원문 파일 닫음
	{
		List<LogBatch> listRead;	// 이번에 처리할 큐

		lock (m_kQueueLock)
		{
			if (m_listWrite.Count == 0)
			{
				listRead = m_listRead;
			}
			else
			{
				(m_listRead, m_listWrite) = (m_listWrite, m_listRead);

				listRead = m_listRead;
			}
		}

		int nLine = listRead.Sum(kBatch => kBatch.listLine.Count);	// 이번 (밀린) 줄 수

		TabQuery.ReportLoad(m_nIndex, nLine);	// 밀림 → 클라 Pause, 풀림 → Resume

		if (listRead.Count > 0)
		{
			int		nBatch	= listRead.Count;			// 이번 건수
			int		nDrop	= 0;						// 종료 상한 넘어 버린 줄
			long	nBegin	= Stopwatch.GetTimestamp();	// 처리 시작

			Interlocked.Add(ref m_nQueueLine, -nLine);
			Interlocked.Add(ref TabQuery.m_nIngestQueueLine, -nLine);

			foreach (LogBatch kBatch in listRead)
			{
				if (kBatch.setPrune != null)	// 받은 진행 정리 표시 (앞 Log 처리 뒤)
				{
					PruneCount(kBatch.sMachine, kBatch.setPrune);

					continue;
				}

				if (m_bStop && kBatch.bCount && Environment.TickCount64 - m_nStopTick >= TabQuery.m_nIngestStopMs)	// 종료 처리 상한 넘음: 클라 재전송으로 복구되는 Log (받은 진행 관리 폴더) 는 버림 (HTTP·관리 밖: 처리)
				{
					nDrop += kBatch.listLine.Count;

					continue;
				}

				TabQuery.SaveOrigin(this, kBatch);
			}

			listRead.Clear();

			if (nDrop > 0)
			{
				Log.Write(WARN_, $"종료 정리 상한: 워커 {m_nIndex} 남은 Log {nDrop:N0}줄 버림 ({TabQuery.m_nIngestStopMs / 1000}초 넘음, 받은 진행 관리 폴더 → 클라가 서버 진행부터 다시 보냄)");
			}

			if (nLine >= TabQuery.m_nIngestQueueWarn && Environment.TickCount64 - m_nWarnTick >= TabQuery.m_nIngestWarnSec * 1000L)	// 밀림: 부하 로그 (간격 제한)
			{
				m_nWarnTick = Environment.TickCount64;

				Log.Write(WARN_, $"적재 부하: 워커 {m_nIndex} 라이트 큐 {nLine:N0}줄 ({nBatch:N0}건) 한 번에 처리 ({Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds:N0} ms, 원문 파일 {m_hmapStream.Count}개 열림)");
			}
		}

		if (Environment.TickCount64 - m_nIdleTick >= TabQuery.m_nOriginIdleSec * 1000L / 2)	// 오래 안 쓴 원문 파일 닫음 (주기 확인)
		{
			m_nIdleTick = Environment.TickCount64;

			foreach (string sFile in m_hmapStream.Where(kPair => Environment.TickCount64 - kPair.Value.nTick >= TabQuery.m_nOriginIdleSec * 1000L).Select(kPair => kPair.Key).ToList())
			{
				CloseOriginStream(sFile);
			}
		}
	}

	public void AddPrune(string sMachine, HashSet<string> setFolder)	// 수신 스레드: 받은 진행 정리 표시를 라이트 큐 끝에 (클라 관리 목록 setFolder 밖, 접속 종료: 빈 목록 → 모두, 앞 Log 처리 뒤 로직 스레드가 닫고 뺌, 큐 가득: 무시하고 넣음)
	{
		lock (m_kQueueLock)
		{
			m_listWrite.Add(new LogBatch(sMachine, "", "", "", new List<string>(), null, null, false) { setPrune = setFolder });
		}

		m_kSignal.Set();
	}

	private void PruneCount(string sMachine, HashSet<string> setFolder)	// 로직 스레드: 이 머신 받은 진행 중 목록 밖 → 닫고 메모리에서 뺌 (파일: 그대로, 폴더 없음: 머신도 뺌)
	{
		if (!m_hmapCount.TryGetValue(sMachine, out Dictionary<string, Count>? hmapFolder))	// 이 워커에 그 머신 없음
		{
			return;
		}

		foreach (string sFolder in hmapFolder.Keys.Where(sFolder => !setFolder.Contains(sFolder)).ToList())
		{
			hmapFolder.Remove(sFolder, out Count? kCount);

			kCount?.Close();
		}

		if (hmapFolder.Count == 0)
		{
			m_hmapCount.Remove(sMachine);
		}
	}

	public Count GetCount(string sMachine, string sFolder)	// 머신 → 대상 폴더 받은 진행 (처음: Data\Count 읽음 → 접속 종료까지 메모리, 머신·폴더 오류: 파일 없이 메모리만)
	{
		if (!m_hmapCount.TryGetValue(sMachine, out Dictionary<string, Count>? hmapFolder))
		{
			m_hmapCount[sMachine] = hmapFolder = new Dictionary<string, Count>(StringComparer.OrdinalIgnoreCase);
		}

		if (!hmapFolder.TryGetValue(sFolder, out Count? kCount))
		{
			kCount = new Count(sMachine, sFolder);

			kCount.Load(null, out _, out _);

			hmapFolder[sFolder] = kCount;
		}

		return kCount;
	}

	public FileStream GetOriginStream(string sFile)	// 열어 둔 원문 파일 (없음: 이어 쓰기로 엶·폴더 만듦, 최대 수 넘음: 가장 오래 안 쓴 것 닫음, 쿼리 읽기 허용)
	{
		if (m_hmapStream.TryGetValue(sFile, out (FileStream kStream, long nTick) kOpen))
		{
			m_hmapStream[sFile] = (kOpen.kStream, Environment.TickCount64);

			return kOpen.kStream;
		}

		if (m_hmapStream.Count >= TabQuery.m_nOriginOpenMax)	// 너무 많이 열림: 가장 오래 안 쓴 것 닫음
		{
			CloseOriginStream(m_hmapStream.MinBy(kPair => kPair.Value.nTick).Key);
		}

		Directory.CreateDirectory(Path.GetDirectoryName(sFile)!);

		FileStream kStream = new FileStream(sFile, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete, 1, FileOptions.None);	// 버퍼 없음 (1건을 모아 쓰기 1번, Position = 파일 끝)

		m_hmapStream[sFile] = (kStream, Environment.TickCount64);

		return kStream;
	}

	public void CloseOriginStream(string? sFile)	// 열어 둔 원문 파일 닫음 (null: 모두, 없음: 무시)
	{
		foreach (string sKey in sFile == null ? m_hmapStream.Keys.ToList() : new List<string> { sFile })
		{
			if (m_hmapStream.Remove(sKey, out (FileStream kStream, long nTick) kOpen))
			{
				try
				{
					kOpen.kStream.Dispose();
				}
				catch (Exception)	// 디스크 오류: 무시
				{
				}
			}
		}
	}
}

class IndexBuffer	// 원문 시간 (yyyyMMddHH) 하나의 색인 버퍼 (워커가 채우고 타이머가 통째로 가져가 세그먼트로): 단어는 해시만 (세그먼트도 해시만 둠), 포스팅은 아레나 (단어·필드 값마다 List 객체 없음)
{
	public readonly List<(int nMs, int nFileId, long nOffset, int nLength)>	m_listDoc	= new List<(int nMs, int nFileId, long nOffset, int nLength)>();	// 줄 (버퍼 안 줄 번호 순: 분초·원문 파일 번호·원문 위치·원문 바이트)
	public readonly Dictionary<ulong, Posting>								m_hmapWord	= new Dictionary<ulong, Posting>();									// 단어 해시 → 포스팅 (버퍼 안 줄 번호 오름차순)
	public readonly Dictionary<(string sName, string sValue), Posting>		m_hmapField	= new Dictionary<(string sName, string sValue), Posting>(FieldKeyComparer.m_kInstance);	// 필드 (이름, 값) → 포스팅 (값 대소문자 무시 → 처음 표기, 키 "필드\t값" 문자열: 세그먼트 쓸 때만)
	public readonly PostingPool												m_kPool		= new PostingPool();												// 포스팅 아레나 (세그먼트 쓴 뒤 블록 반납)
	public readonly long													m_nTick		= Environment.TickCount64;											// 처음 쌓인 시각 (m_nFlushSec 초 지나면 세그먼트 파일)

	public void AddWord(ulong nHash, int nDoc)	// 단어 해시에 줄 번호 (처음: 포스팅 새로)
	{
		ref Posting kPost = ref CollectionsMarshal.GetValueRefOrAddDefault(m_hmapWord, nHash, out _);

		m_kPool.Add(ref kPost, nDoc);
	}

	public void AddField(string sName, string sValue, int nDoc)	// 필드 (이름, 값) 에 줄 번호 (값 없음·m_nFieldValueMax 넘음: 안 함)
	{
		if (sValue == "" || sValue.Length > TabQuery.m_nFieldValueMax)
		{
			return;
		}

		ref Posting kPost = ref CollectionsMarshal.GetValueRefOrAddDefault(m_hmapField, (sName, sValue), out _);

		m_kPool.Add(ref kPost, nDoc);
	}
}

class FieldKeyComparer : IEqualityComparer<(string sName, string sValue)>	// 필드 (이름, 값) 비교: 둘 다 대소문자 무시 (키 "필드\t값" OrdinalIgnoreCase 와 같음)
{
	public static readonly FieldKeyComparer m_kInstance = new FieldKeyComparer();

	public bool Equals((string sName, string sValue) kLeft, (string sName, string sValue) kRight)
	{
		return string.Equals(kLeft.sName, kRight.sName, StringComparison.OrdinalIgnoreCase) && string.Equals(kLeft.sValue, kRight.sValue, StringComparison.OrdinalIgnoreCase);
	}

	public int GetHashCode((string sName, string sValue) kKey)
	{
		return HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(kKey.sName), StringComparer.OrdinalIgnoreCase.GetHashCode(kKey.sValue));
	}
}

struct Posting	// 포스팅 (PostingPool 조각 연결): 첫·끝 조각 위치, 줄 수, 끝 조각 남은 칸 (0: 다음 추가 때 새 조각)
{
	public int nHead;
	public int nTail;
	public int nCount;
	public int nFree;
}

class PostingPool	// 포스팅 아레나 (색인 버퍼 1개): 줄 번호 조각 [다음 조각 위치, 칸 수, 줄 번호…] (칸 수 1 → 2 → 4 … → m_nChunkMax) 을 int 블록 (ArrayPool) 에 이어 담음 → 단어·필드 값마다 List·배열 객체 없음, 세그먼트 쓴 뒤 블록 반납
{
	private const int	m_nBlockBits	= 16;						// 블록 int 수 = 2^16 (256 KB)
	private const int	m_nBlockSize	= 1 << m_nBlockBits;
	private const int	m_nChunkMax		= 32;						// 조각 최대 칸 수

	private readonly List<int[]>	m_listBlock	= new List<int[]>();	// 블록 (위치 = 블록 번호 << m_nBlockBits | 블록 안 위치, 조각은 블록 안에서만)
	private int						m_nUsed		= m_nBlockSize;			// 현재 블록 쓴 int 수 (처음: 블록 없음)

	public void Add(ref Posting kPost, int nDoc)	// 줄 번호 추가 (끝 조각 참·처음: 새 조각, 앞 조각 칸 수 2배)
	{
		if (kPost.nFree == 0)
		{
			int nSize = kPost.nCount == 0 ? 1 : Math.Min(Get(kPost.nTail + 1) * 2, m_nChunkMax);	// 새 조각 칸 수
			int nAddr = Alloc(nSize + 2);															// 새 조각 위치

			Set(nAddr, 0);
			Set(nAddr + 1, nSize);

			if (kPost.nCount == 0)
			{
				kPost.nHead = nAddr;
			}
			else
			{
				Set(kPost.nTail, nAddr);	// 앞 조각 → 새 조각
			}

			kPost.nTail	= nAddr;
			kPost.nFree	= nSize;
		}

		Set(kPost.nTail + 2 + Get(kPost.nTail + 1) - kPost.nFree, nDoc);

		--kPost.nFree;
		++kPost.nCount;
	}

	public int WritePosting(Stream kBlob, in Posting kPost, int nBase, int nPrev)	// 포스팅 → 줄 번호 (+ nBase) 오름차순 차이 varint 로 이어 씀 (nPrev: 앞 줄 번호, 반환: 마지막 줄 번호)
	{
		int nLeft = kPost.nCount;	// 남은 줄 수

		for (int nAddr = kPost.nHead; nLeft > 0; nAddr = Get(nAddr))	// 조각마다
		{
			int nTake = Math.Min(Get(nAddr + 1), nLeft);	// 이 조각 줄 수

			for (int nIndex = 0; nIndex < nTake; ++nIndex)
			{
				int nDoc = Get(nAddr + 2 + nIndex) + nBase;	// 줄 번호 (시간 안)

				TabQuery.WriteVarint(kBlob, (uint)(nDoc - nPrev));

				nPrev = nDoc;
			}

			nLeft -= nTake;
		}

		return nPrev;
	}

	public void Release()	// 블록 반납 (세그먼트 쓴 뒤, 그 뒤 읽기 없음)
	{
		foreach (int[] arrBlock in m_listBlock)
		{
			ArrayPool<int>.Shared.Return(arrBlock);
		}

		m_listBlock.Clear();

		m_nUsed = m_nBlockSize;
	}

	private int Alloc(int nSize)	// 조각 자리 (블록 남은 칸 모자람: 새 블록)
	{
		if (m_nUsed + nSize > m_nBlockSize)
		{
			m_listBlock.Add(ArrayPool<int>.Shared.Rent(m_nBlockSize));

			m_nUsed = 0;
		}

		int nAddr = ((m_listBlock.Count - 1) << m_nBlockBits) | m_nUsed;	// 조각 위치

		m_nUsed += nSize;

		return nAddr;
	}

	private int Get(int nAddr)
	{
		return m_listBlock[nAddr >> m_nBlockBits][nAddr & (m_nBlockSize - 1)];
	}

	private void Set(int nAddr, int nValue)
	{
		m_listBlock[nAddr >> m_nBlockBits][nAddr & (m_nBlockSize - 1)] = nValue;
	}
}

record struct SegmentHead(int nTermCount, int nFieldBytes, int[] arrBucket);	// 세그먼트 머리 (단어 수, 필드 사전 바이트, 버킷 시작 257: 버킷 b 의 단어 사전 칸 = [b] ~ [b + 1] 미포함)

record struct FieldEntry(string sKey, int nOffset, int nCount, int nBytes);	// 세그먼트 필드 사전 칸 (키 "필드\t값", 포스팅 위치·건수·바이트)

record struct OriginRecord(string sTime, string sText, long nOffset, int nLength, string sType, (string sKey, string sValue)[] arrField);	// 원문 레코드 1줄 (원문 시간, 원문 (받은 줄 그대로), 원문 바이트 위치·길이, Type, 추가 필드 (설정 aField))

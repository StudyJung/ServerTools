# Client

> 게임 서버 머신 로그 감시·실행 에이전트 — 최신 로그의 지정 레벨 줄을 Server 로 TCP 전송, 서버 요청 (검색·보기·덤프·파일) 처리, Redis 지시로 Git 배포·프로세스 온오프 (OLD HMXClient 이식)

## 개요
- 유형: 콘솔 / 윈도우 서비스 (Worker SDK, BackgroundService), .NET 10 file-based app
- 프레임워크: net10.0, Microsoft.Extensions.Hosting.WindowsServices 10.0.10, StackExchange.Redis 2.13.17, win-x64 프레임워크 종속 단일 exe
- 역할: 게임 서버 머신마다 설치 → aLogFolder 하위 로그 감시·전송, `*.dmp` 감시, Server 원격 요청 처리, 실행 기능 (Redis 목록 중 이 머신 항목 Git 동기화·실행·종료)
- 부하 정책: 프로세스 우선순위 BelowNormal, 검색 동시 2건 (서버 Gate 와 같음)·보기 동시 2건, Throttle (500ms 작업마다 100ms 쉼, 서버 Gate.Yield 와 같음)

## 구조
| 경로 | 설명 |
|---|---|
| Source/Client.cs | 진입 소스: `#:` 지시문 (`#:include` 로 같은 폴더 클래스별 .cs·../../Common 포함)·`// Source.md 반드시 참조`·global using·진입 `return Main.Run(args);`·Main |
| Source/Source.md | 명세 (마크다운: 실행·설정·TCP·탭 동작, Client.cs 머리에서 옮김) |
| Source/{클래스}.cs | 클래스별 소스 (코드 구성 참고) |
| ../Common/*.cs | 서버와 같이 쓰는 공통 클래스 → [Common.md](../Common/Common.md) |
| Client.csproj | Worker SDK, `..\Common\*.cs` 링크 포함, 빌드 후 단일 exe 게시, Json\Client.json 출력 복사 안 함·Json 폴더 보존 |
| Json/Client.json | 설정 (예전 위치 Client 폴더에만 있음: 그 파일 + 경고) |
| On.bat / Off.bat | 콘솔 실행·종료 |
| .gitignore | .vs·.user·bin·obj·log·Properties 제외 |
| dotnet-runtime-10.0.13-win-x64.exe | .NET 런타임 설치 파일 (분석 제외) |
| bin/, obj/ | 게시 출력 (bin\Release\net10.0\Client.exe)·빌드 중간 출력 |
| (실행 시) Log/, Data\Count/ | 시간별 로그, 전송 진행 상태 |

## 코드 구성
| 파일 | 클래스 | 역할 |
|---|---|---|
| Client.cs | Main (BackgroundService) | 진입점·서비스: 인자 (-install·-uninstall: 서비스 HMXClient 등록·제거, sc.exe, -start·-stop: 시작·정지), 설정 검증 (aLogFolder·sIP·nPort·nLogSend·nLogRead·sMachine), Create (설정 → 탭 공통·TCP·탭 Init·반영 탭 Init·날짜 폴더 삭제 Init), 반영 탭·날짜 폴더 삭제 시작 (따로) + 탭 순서 준비 (쿼리 → 서치 → 덤프 → 파일) 뒤 접속, 공통·기능 연결 (OnRequest: 서버 요청 → 탭, OnConnect·OnDisconnect·GetAliveLine·OnPause: TCP ↔ 쿼리, Post: 덤프 이벤트 → 전송 큐) |
| ../Common | Global·Config·Log·AES·Count·Define | 공통 (전역 함수·설정·시간별 로그·AES 복호화·받은 진행 Data\Count\머신\폴더\Count.data, 상수·데이터 정의: SEARCH_STOP·SEARCH_OP·SearchLine·LogField·DumpFile·FileItem·ChunkResult·RelayException 등) |
| Client.cs (Main 상수) | m_nDefaultPort·m_sPeerOff·m_sSelfError | 프로젝트별 값 (기본 Server TCP 포트 9091·오류 문구 Server Off·Client Error → 공통 Global) |
| Timer.cs | Timer | 타이머·Alive 로그 |
| TCPClient.cs | TCPClient·LineWriter·LineReader | Server TCP: 마스터 키 받기 (KeyLoop: 첫 접속부터 X초마다, 받을 때까지), 접속·재접속 (ConnectLoop), Hello 뒤 묶음 전송·얼라이브 (SendLoop), 수신 (ReadLoop), 줄 단위 송수신 (요청 줄 최대 1MB) |
| Tab.cs | Tab·Throttle | 탭 공통: aLogFolder 기준 폴더·막는 폴더 규칙·경로 허용 (링크 거부)·파일 날짜·블록 읽기 (UTF-8·CP949·UTF-16)·조각 읽기, 부하 조절 |
| TabQuery.cs | TabQuery·LogWatch | 쿼리 탭: 대상 탐색·로그 감시 (LogWatchLoop)·Count 동기화·서버 aField 줄 필드·Pause, 대상 폴더별 감시 파일 핸들 |
| TabSearch.cs | TabSearch·SearchJob·SearchNode | 서치 탭: 검색·진행 알림·파일 보기, 검색어 트리 (AND·OR·NOT (대소문자 무시)·괄호·앞뒤 * 와일드카드, 서버·페이지와 같은 규칙) |
| TabDump.cs | TabDump | 덤프 탭: `*.dmp` 감시 (WatchLoop·FlushLoop)·목록·조각 읽기·삭제 |
| TabFile.cs | TabFile | 파일 탭: 폴더 보기·파일 조각 읽기 |
| Redis.cs | Redis | Redis 연결 (최초 사용 시 1회·자동 재연결, 서버 Redis 와 같은 모양): 존 키 앞 (GetZonePrefix: 설정 값 + 존 + `:`)·목록 키 앞 (sPrefix)·에러 리스트 키 (sError) |
| Process.cs | Process·ProcessValue·ProcessLog·ProcessGit | 실행 API (OLD HMXProcess·HMXValue·HMXLog·HMXGit): Win32 실행·확인·종료 (Ctrl+C·강제), 값 파트·그룹, 실행 로그·Redis 에러 리스트, Git sparse 클론·shallow 갱신 |
| TabProcess.cs | TabProcess | 반영 탭 (OLD HMXClient BackgroundService): 1초 주기 Tick → LoadList → Ready → Start → RedisUpdate |
| Clean.cs | Clean·CleanItem | 날짜 폴더 자동 삭제 (aDeleteFolder): 항목마다 매일 nHour 시 정각 (CleanLoop) sPath 하위 yyyyMMdd 폴더 중 오늘 - nDay 일 이전 통째 삭제, 겹친 하위 항목 경로: 그 항목 규칙 |

## 주요 기능
- 대상 탐색: aLogFolder 하위 4단계 안에서 바로 아래 날짜 폴더(yyyyMMdd) 가진 폴더 = 대상 (60초마다 재탐색, 사라진 폴더: 감시·상태 파일 닫고 목록에서 뺌 + 서버 CountDel, 감시 파일 사라짐: 핸들 바로 닫음)
- 로그 감시: 대상마다 최신 `*yyyyMMdd_HH.log` 1개 읽기 전용 감시, 1초마다 새 줄 → 두 번째 `[]` 레벨이 aLogType 인 줄만 전송 (주기당 nLogRead 읽기·nLogSend 전송, 줄 최대 1만 자), 다음 시간 파일 자동 이동
- 원문 시간 보정: 첫 `[]` 가 `[yyyy/MM/dd HH:mm:ss.fff]` 형식·있는 날짜·시각이 아니면 (시간 없음·13월·25시 등) 보내는 줄만 첫 `[]` 를 지금 시간 (KST) 으로 바꿔 보냄 (로그 파일 그대로, 끝난 줄 판단도 안 막음)
- 필드 추출: 서버 aField (Server.json, 접속 때 Count 답·1분 Alive 답·필드 버튼 저장 때 받음 → 바뀜만 다음 Log 부터, 클라 설정 없음) 정규식 첫 그룹 → 줄마다 {키: 값} (정규식 100ms 제한, sValue 있음: 정규식 안 씀·모든 줄 고정값 (존 등), 키 예약어 Machine·Folder·Type 불가, 예전 서버: 필드 없이)
- 진행 보정: 접속 시 Count 동기화 → 서버 받은 진행 기준 건너뜀·재전송 (중복·누락 방지), 끊김 중 위치 유지, 예전 LMClient.data 자동 이전
- 흐름 제어: 서버 Pause·Resume 수신 → 감시 멈춤·재개, 전송 큐 최대 1만 건 (초과: 오래된 것부터 버림)
- 원격 검색: 기간 (yyyyMMddHH) 로그 파일에서 검색어 포함 줄, 최대 nMax (기본 1,000)·nLimit 초 (서버 서치 제한 시간 설정, 없음: 60초, 상한 600), 1초마다 SearchProgress, Stop 지원
- 인코딩: 줄마다 UTF-8·CP949 판별, UTF-8·UTF-16 BOM 지원
- 파일 보기: *.log·txt·json·ini·bat·xml·config·md·lua, 기본 500·최대 2,000줄
- 덤프: FileSystemWatcher `*.dmp` 이벤트 전송 (Changed: 1초 묶음), 목록 (최대 1만)·4MB 조각 다운·삭제
- 파일 탭: aLogFolder 폴더 보기 (최대 1만)·파일 다운 (확장자 제한 없음, 4MB 조각)
- 접근 제한: aLogFolder 밖·정션·심볼릭 링크 경유 거부, 탭별 aBlock* 경로 (전체 경로·폴더 이름·중간 경로, 자신·하위) 제외
- 연결 유지: 재접속 2초부터 2배·최대 5초, 60초마다 Alive (관리 목록, 무전송 Ping 대신), TCP keepalive

## 실행 기능 (TabProcess, 짝: 서버 반영 탭)
- 켜짐: Redis:sHost 있음 (없음: 꺼짐 로그 1줄, 로그 감시만)
- 키: 설정 값 + 존 (sZone) + `:` (예: 목록 `HMX:Process:Dev:대분류,중분류,이름`, 에러 리스트 `HMX:Error:Dev:`, 존 없음: 설정 값만, 서버와 같음)
- 주기: 1초마다 Redis 목록 키 `*` (SCAN·MGET) 중 값 머신 파트 = 이 머신 (sMachine, 없음: 컴퓨터 이름, 대소문자 무시)
- 값: `상태,적용,PID,커밋,머신,폴더,깃헙,Sync,Mode,실행파일+인자` (서버 등록 형식, 실행 위치 `폴더\깃헙`)
- Ready: Git 동기화 최대 8개 동시 (On·False: Sync 0 이면 생략, Down·False: 다운로드), `.git` 없음 → sparse 클론, 있음 → shallow 갱신 (fetch depth 1 + reset --hard), 실패: Off·False·PID -1
- Start: 그룹 (필드 1·2번째 파트) 순서, 그룹 안 동시 — On: 실행 (Mode 0 독립 실행·1 새 콘솔), Off: 종료 (Mode 0 강제·1 Ctrl+C), Down: Git 만
- PID 오류값: -1 Git 실패·-3 확인 실패·-10 크래시 (다음 주기 재실행)·-11 실행 실패·-12 종료 실패
- 로그: 공통 로그 (레벨 앞 `[sTitle_]` 태그), ERROR·FATAL → Redis 에러 리스트에 `[머신] [시각] [HMX_][레벨] 내용` (30일 유지, 서버 반영 탭 에러 조회), FATAL 10초 대기 (Redis 장애 재시도 완화), 약 100초마다 Tick Alive, 저장소 토큰 `****`
- 실행 프로세스 우선순위: 보통 (클라 BelowNormal 상속 안 함)

## 설정 (Json/Client.json)
| 키 | 설명 |
|---|---|
| bLog | 파일 로그 기록 (false: 콘솔만, 최상위, 예전 Base 묶음: 읽을 때 풀어 씀) |
| sIP | Server 주소 (IP·호스트명) |
| nPort | Server TCP 포트 (현재 801, 키 없음: 9091) |
| sMachine | 서버에 보낼 머신 이름 (키 없음: 컴퓨터 이름, 한 PC 여러 클라 테스트: 클라마다 다르게, 예: Client1) |
| nLogRead / nLogSend | 파일당 주기별 최대 읽기 줄 (1000) / 전송 줄 (100) |
| aLogType | 전송 로그 레벨 (NOTI·ERROR·FATAL) |
| aLogFolder | 기준 폴더 배열 (필수, 예: C:\NHNServices, 경로는 `\` 하나로 써도 됨: 공통 Config.FixPathEscape) |
| aBlockQuery·aBlockSearch·aBlockFile·aBlockDump | 탭별 막는 경로 (전체 경로: 그 경로 자신·하위, 폴더 이름·중간 경로 (예: test·Test\S·TEST1/5/5): 경로 안 어디든 이름 단위 정확히 일치하는 폴더 자신·하위 (test1·MyTest: 안 막음), 대소문자 무시, 형식 오류 (빈 값·.·..·C:Log·와일드카드): 경고 후 뺌, 현재: C:\NHNServices\HMXServer\Data) |
| aDeleteFolder | 날짜 폴더 자동 삭제 `[{sPath, nDay, nHour}]` (기본: 빈 sPath 1개 = 꺼짐, aLogFolder 와 무관, 바꿈: 재시작): sPath 전체 경로 자신·하위 4단계 날짜 폴더(yyyyMMdd) 중 오늘 - nDay 일 이전 통째 삭제 (nDay 최소·기본 1: 어제 이전, 오늘: 안 지움), 매일 nHour 시 정각 (0~23, 기본 6, KST, 시작 때 바로 안 함), 날짜 폴더 안·숨김·시스템·정션·심볼릭 링크: 안 들어감·안 지움 (안에 링크 폴더 있는 날짜 폴더: 경고 후 안 지움), 상대 경로·드라이브 루트·형식 오류: 경고 후 뺌, 같은 경로: 첫 항목만·경고, 겹친 항목: 하위 항목 경로 자신·하위는 그 항목 규칙만 (예: C:\NHNServices\HMXServer\Data + C:\NHNServices\HMXServer\Data\Test → Test: Test 항목, Data\Test2: Data 규칙, 대소문자 무시), 폴더마다 Throttle, 실패 (사용 중): 경고 후 다음 폴더 |
| (aField) | 클라 설정 없음 → Server.json aField 로 옮김 (남은 예전 설정: 경고 후 무시) |
| Redis:sHost·nPort·nDB·sUser·sCode | 실행 기능 Redis 접속 (서버 Server.json Redis 와 같은 서버, sCode → sPass, sHost 없음: 실행 기능 꺼짐) |
| sTitle | 이름 → 로그 줄 레벨 앞 `[이름_]` 태그 (예: HMX → `[시각] [HMX_][INFO_] 내용`, 실행 에러 리스트 줄도 같음, 없음: 태그 없음) |
| sZone | 존 (Dev, 서버 Server.json sZone 과 같게 → 실행 기능 Redis 키 존, 없음: 설정 값만) |
| Redis:sPrefix / sError | 목록 키 앞 (HMX:Process:, 서버 Redis:sProcess 와 같게) / 에러 리스트 키 앞 (HMX:Error:, 서버 Redis:sError 와 같게), 실제 키: 값 + 존 + `:` |
| Git:sUrl / sCode | 배포 저장소 (HMX_ALPHA) / 저장소 토큰 (→ sPass, 없음: git 자격 증명) |
| *:sCode | (있으면) AES 복호화 → 같은 위치 sPass (Redis:sCode·Git:sCode, 키: 서버에서 받은 마스터 키, 메모리만, 받기 전: 기능 안 함) |

## 프로토콜 (TCP, 한 줄 = JSON 1건, UTF-8 `\n` 구분)
| 방향 | sType | 설명 |
|---|---|---|
| 클라→서버 | Hello | 접속 직후 머신·aLogFolder·PID |
| 클라→서버 | Count / CountAdd / CountDel | 감시 대상 폴더 목록 (진행 동기화, 새 폴더 추가, 사라진 폴더 관리 제외) |
| 클라→서버 | Log | 줄·줄 번호·필드 묶음 (sPath·sFolder, 존: arrField) |
| 클라→서버 | File | 덤프 이벤트 (Created·Changed·Deleted·Renamed·Error) |
| 클라→서버 | Alive | 60초마다 (전송 중에도, 무전송 Ping 대신) 지금 관리 목록 → 서버: 다름만 맞춤 + 답 필드 정의 |
| 서버→클라 | Pause·Resume·Count·CountAdd·Alive | 적재 밀림 제어, 받은 진행 답 (Count 답: aField 줄 필드 정의 포함), Alive: 얼라이브 답·필드 버튼 저장 바로 (aField, 바뀜만 다음 Log 부터 적용) |
| 서버→클라 | Search·Stop·View·Dump·DumpRead·DumpDelete·Files·FileRead | 요청 (nReqId 짝) → *Result·SearchProgress 응답 |

## 실행·배포
- On.bat: 창 제목 Client_Console 종료 → `dotnet run --file Source\Client.cs` 최소화 실행 (`#:include` 파일 함께 빌드), 출력 log\out.log·err.log
- Off.bat: 창 제목 Client_Console 프로세스 종료
- 빌드: Build 후 PublishExe → bin\Release\net10.0\Client.exe (Json\Client.json 직접 복사)
- 서비스: 관리자 cmd `Client.exe -install` (서비스 HMXClient 등록·자동 시작, 이미 등록: -uninstall 후 다시) / `Client.exe -uninstall` (정지·제거) / `Client.exe -start`·`-stop` (시작·정지, 최대 60초 대기), SCM 연동 (기준 폴더 = exe 폴더)
- 디버깅: 상위 HMX.slnx F5 
- 필요: .NET 10 런타임 (동봉 설치 파일)

## 연관
- 상위: [HMX.md](../HMX.md)
- 서버: [Server.md](../Server/Server.md) — TCP 접속 대상, SearchNode 같은 규칙
- 공통: [Common.md](../Common/Common.md) — Global·Config·Log·AES·Count·Define 같이 씀

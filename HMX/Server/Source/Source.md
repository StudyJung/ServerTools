# HMX·HTB Source (HMX Server\Source\Source.md = HTB\Source\Source.md, 같은 한 문서)

# HMX Server
## 로그 관리 웹 서버 (.NET 10 file-based app)
   ### 실행: dotnet run --file Source\Server.cs [포트] [호스트=localhost]  (Server 폴더에서, 상위 폴더 Server.csproj 와 따로: --file 필수)
   ### 서비스 (관리자 cmd, 게시된 Server.exe): -install [포트] [호스트] (서비스 HMXServer 등록·자동 시작·시작, 뒤 인자: 실행 인자 그대로) | -uninstall (정지·제거) | -start | -stop (시작·정지 X초 대기)
      예) dotnet run --file Source\Server.cs                -> http://localhost:포트 (외부 접속 불가)
      예) dotnet run --file Source\Server.cs 9000 0.0.0.0   -> 모든 인터페이스 9000 포트 (방화벽 허용 필요)
   ### 디버깅: HMX.slnx (Visual Studio 2026) F5
   ### 구성: Server.cs (Main: 진입점·공통·기능 연결) + #:include → 공통 ../../Common (Global 전역 함수·Config 설정·Log 로그·AES 복호화·Count 받은 진행·Define 상수·데이터 정의, Client 와 같이 씀, 프로젝트별 값: Main) → Timer(타이머) → Auth(로그인) → Web(웹 서버·기본 페이지) → API(공통 API·클라 요청 응답) → TCPServer(Client TCP 수신) → TabSearch(서치 탭·SearchNode 검색어 규칙)·TabDump(덤프 탭)·TabFile(파일 탭)·TabOperation(운영 탭: 게임 서버 점검·패치·동접, Redis)·TabProcess(반영 탭: 게임 서버 프로세스 시작·정지·다운, API Process, Redis)·TabUtil(유틸 탭: 하위 탭 타임·디프·소트, API 없음)·TabQuery(쿼리 탭: 적재·색인·쿼리) (탭 API + 페이지 스크립트 SearchHTML·DumpHTML·FileHTML·ManagerHTML·ProcessHTML·QueryHTML) → Alarm·Stat·Send·AlarmLog(알람)
   ### 기본 페이지: Web.IndexHtml() (공통 앞 + 탭 스크립트 SearchHTML·DumpHTML·FileHTML·ManagerHTML·ProcessHTML·QueryHTML + 공통 뒤)
   ### 정적 파일: 기준 폴더 wwwroot (있으면 자동 서비스, index.html 기본 문서)
   ### 데이터 폴더: 기준 폴더 하위 data (index·origin·alarm·count·프로젝트명.txt 실행 설정 버전, 예전 File: 시작 때 data 로 이름 변경, 둘 다 있음: 그대로 + 경고)
   ### 폴더 이름: 프로그램이 만드는 폴더: 이름 모두 소문자 (기준 폴더 하위 log·dump·data, data 하위 alarm·index·origin·count, 새 폴더도 소문자, 머신·대상 경로·시간 등 값 이름: 그대로), 대문자 폴더 있음 (예전 Log·Data·Dump 등): 시작 때 소문자로 이름만 바꿈 (공통 Global.FixDirCase, 설정 로딩 뒤·폴더 열기 전, 상위 먼저, 내용 그대로, 실패 (사용 중·권한): 경고 → 그대로 씀 (Windows 대소문자 무시)), 배포·소스 폴더 (Json·Source·Tool 등): 그대로

## 설정 파일: 기준 폴더 (exe 폴더·dotnet run: 실행 폴더) 하위 Json 폴더 프로젝트명.json (모든 설정 json: Json 폴더, 예전 위치 기준 폴더에만 있음: 그 파일 + 경고) → Config.GetString("DB:Dev:sHost")·GetInt("DB:Dev:nPort") (키 없음: 기본값)
   ### 시작 검사: 서버 시작 맨 앞 (ConfigCheck): Json 폴더 모든 *.json 미리 읽기·검사 → 1단계 형식 (설정 로딩 전, 주석·끝 쉼표·경로 \ 하나 허용), 2단계 값 (설정 로딩 뒤, 파일 안 키마다: bLog·bWeb·bAuth·bHttps·bTest true·false, sLog Debug·Info·Warn, nPort·Redis.nPort·Mail.nPort 1 ~ 65535, sUrl http·https, aMenu 탭 이름, aQuery·aSearch 제목·내용·형식·간격·겹침, aField 이름·정규식, aAlarm 값 (CheckAlarm), aAccount 아이디·겹침·sCode·메뉴·외부 권한·메일·전화, Manager.ServerList sIP·nPort·겹침, IP.json aWhiteTCP·aBlackWeb 만·IP 패턴·IP.json 없음: 오류) → 오류 하나라도: 파일·필드·이유 ERROR 로그 (전부) + FATAL → 서버 종료 (고친 뒤 다시 시작), 실행 중 다시 읽기 (설정 팝업·IP.json 바뀜): 기능마다 그대로 (경고·앞 값 유지)
   ### sCode 키: 로딩 시 AES 복호화 → 같은 위치 sPass (예: "DB:Dev:sCode" → GetString("DB:Dev:sPass"))
   ### 경로 값 (공통 Config.FixPathEscape, 서버·클라 모든 설정 json): "X:\…"·"\\…" 로 시작하는 문자열은 \ 하나로 써도 됨 (예: "D:\SRC\Log", 표준 "D:\\SRC\\Log" 도 같음, UNC: "\\서버\공유"·표준 "\\\\서버\\공유" 같음, 끝 \ 뒤 " 는 문자열 끝, \" 뒤 구분자 아님: 글자 따옴표), 웹에서 저장하면 표준 \\ 로 다시 씀, 그 밖 문자열 (정규식 등): 표준 JSON 그대로
      AES 키: 마스터 키 (메모리만, 파일·코드 저장 안 함), bWeb·bAuth: 서버 오픈 뒤 첫 접속 1회 로그인 페이지에서 관리 계정 아이디·비밀번호·마스터 키 같이 받음 (그 전: 관리 계정 말고는 로그인 처리 안 함 → 공통 오류, 실패 셈 안 함)
         → 그 계정 sCode 를 그 키로 복호화 = 비밀번호: 마스터 키 메모리 + 설정 다시 로딩 (모든 sCode → sPass 메모리), 불일치: X초 지연
         그 전: 첫 접속 계정 + 마스터 키만 (웹 로그인만) → 복호화·계정 로그인·Redis 연결·메일 인증 안 함, 기능 (타이머·알람·TCP 수신 (클라 접속·로그)·운영 탭 동접 기록): 입력 뒤 시작 (Main.RunAll), 외부 API (/api/external): 503 마스터 키 입력 전 (보낸 쪽 다시, TCP 수신: 그대로)
         로그인 안 씀 (bWeb·bAuth false): 솔루션명 (Define.m_sSolution: 프로젝트)
         클라: TCP {"sType":"MasterKey"} 요청 → 서버 메모리 키 답 {"sType":"MasterKey","sKey"} (sKey: 솔루션명 AES 암호화, 클라: 솔루션명 복호화, 빈 키: 그대로) (TCP 수신: 마스터 키 뒤 엶)
   ### 최상위: bLog(파일 로그), sLog(남길 로그 레벨: Debug: 모두, Info: INFO_ 이상 (DEBUG 안 남김), Warn: NOTI_ 이상 (DEBUG·INFO_ 안 남김, NOTI_ 알림은 남김), ERROR·FATAL: 늘 남김, 콘솔·파일 같이, 대소문자 무시, 없음: Debug, 설정 다시 읽기: 바로 반영), bWeb(웹 서버), bAuth(웹 로그인), bHttps(IIS HTTPS 운영: 로그인 쿠키 Secure·HSTS·알람 링크 https, 아래 "## 로그인"), bTest(반영 탭 TestOn·TestOff: 모든 서버 바로 On,True·Off,True,0, HMXClient 안 거침, 테스트용), sID·sCode(관리 계정: 로그인·모든 메뉴·계정 수정 비밀번호), sTitle(이름: 제목 앞·로그 줄 레벨 앞 [이름_] 태그, 예: 프로젝트 → 제목 프로젝트 Dev·로그 [시각] [HMX_][INFO_] 내용, 없음: 제목 프로젝트·로그 태그 없음), sZone(Dev·Alpha·Real → 제목·색), sUrl(알람 쿼리 링크 주소 (메일 본문 끝, 알람 기록 내용 클릭, 훅: 링크 없음), 없음: 웹 바인딩 주소, localhost·모든 주소: 이 머신 내부 IP (사설 10·172.16~31·192.168 우선), 포트: exe 실행 바인딩 포트, IIS 실행 첫 요청 포트 (사이트 포트, 바인딩 포트: IIS 내부, 기본 포트 80·443: 생략), bHttps: https)
   ### aMenu: 보일 탭 (예: [ "쿼리", "서치" ] → 두 탭만 그 순서대로, 탭 버튼 이름, 계정·힌트: 늘 끝, 없음·맞는 이름 없음: 전체 (유틸·쿼리·서치·파일·덤프·운영·반영), 첫 화면: 첫 탭 (유틸: 하위 탭 타임, 쿼리: 전체 쿼리 자동 실행, 첫 탭 쿼리 아님: 쿼리 탭 처음 누를 때 전체 쿼리 자동 실행 1번), Tab 키: 보이는 탭만)
      빠진 탭: 서버·페이지 동작 안 함 (긴급 내림, Main.IsTab): 탭 생성·API·스레드·타이머 안 함, 페이지: 버튼 숨김·시작 조회 안 함
         쿼리: 적재 워커·색인·알람·필드 (TabQuery 안 건드림, 페이지 스크립트: QueryPage), 클라 Log·받은 진행 (Count) 안 받음 (접속 때 Pause → 클라 로그 전송 멈춤, 위치 유지), /api/external/log 없음
         쿼리·서치·파일·덤프 모두 빠짐: TCP 수신 포트 안 엶 (/api/client 없음), 파일 보기·다운 (/view·/api/view·/api/files/download): 서치·파일 공용 (둘 중 하나 있음: 엶)
         운영: 서버 목록·동접 기록 타이머, 반영: Redis 프로세스 목록
   ### 따로 관리 (웹에서 고침, Main.m_arrConfigSplit): aQuery → Query.json·aSearch → Search.json·aAlarm → Alarm.json·aField → Field.json·Manager → Manager.json (운영 탭 서버 목록, 웹에서 안 고침)·aAccount → Account.json (로그인 계정, 계정 팝업에서 고침) (파일: {"키": [...]}, 웹 저장: 그 파일 통째로, 예전 형식 프로젝트명.json 에 있음·파일 없음: 로딩 때 파일 만듦, 예전 Base 묶음: 최상위로 풀어 읽음)
   ### 접속 IP 목록 (IPList, 손으로 고침): Json/IP.json {"aWhiteTCP": [{"sIP", "sID"}], "aBlackWeb": [{"sIP", "sID"}]} (항목: sIP = IP 패턴, sID = aWhiteTCP: 구분용 메모 (검사 안 함)·aBlackWeb: 막힌 계정 (자동 등록, 계정 비밀번호 바뀜: 그 IP 풀림)·수동: "" (IP 만), 예전 "IP" 문자열도 됨): aWhiteTCP = 클라 TCP 접속 허용 IP (있는 IP 만 접속, 비었음·파일 없음: 모두 거절 → 경고 로그, 바뀜: 새 접속부터), aBlackWeb = 웹 막을 IP (모든 웹 요청 맨 앞: 페이지·API·외부 API·로그인 → 403 Access Denied), 서로 따로 (aWhiteTCP: 웹 대상 아님, aBlackWeb: TCP 대상 아님), 값: IPv4 네 칸 숫자·'*' (예: 10.0.0.1, 192.168.*.*, 127.*.*.*, *.*.*.*), IPv6 그대로 (예: ::1), "*": 모두, 틀린 값: 시작 때 오류 → 종료 (실행 중: 경고 후 뺌), 형식 오류: 앞 목록 그대로, 파일 바뀜: 2초 안 반영 (재시작 안 함), 막음 로그 (같은 IP 1분에 1번, 외부 API 토큰 ****), 접속 IP: 웹 Web.GetAddr (IIS: X-Forwarded-For)·TCP 상대 주소, 자동 등록: 로그인 연속 실패 5번 → aBlackWeb 끝에 {sIP, sID: 그동안 시도한 아이디 (',' 구분)} 추가 (서버가 파일 다시 씀: 주석 사라짐)
   ### aAccount: 로그인 계정 [{sID, sCode (비밀번호 AES.Encrypt, 로딩: 복호화 → sPass), aMenu (접근 탭 이름, 없음: 켜진 탭 전체), aAPI (외부 API 권한: 쿼리 (/api/external/query)·서치 (/api/external/search)·로그 (/api/external/log), 없음: 외부 API 못 씀, 예전 aMenu 의 로그: 로그 권한)}] (관리 계정 sID 와 겹침·sID·sCode 없음·복호화 실패: 뺌)
   ### aQuery: 저장 쿼리 [{sKey, sQuery, nMinute}], aSearch: 저장 서치 (같은 형식, SavedList 공용), aAlarm: 알람 [{nID, sKey, sQuery, nMinute, nCount, nDelay, aMail, sHook, aSMS (받을 아이디, 영문·숫자·._- X자 이하), bEnable}]
   ### aField: 줄 추가 필드 정의 [{sKey, sRegex 또는 sValue}] (sValue 있음: 정규식 안 씀·모든 줄 그 값 (예: 존 {"sKey":"zone","sValue":"Dev"}), sRegex: 첫 () 값, 없음: 맞은 부분, 키: 영문 시작·영문·숫자·_ X자 이하·Machine·Folder·Type 불가, 최대 X개, 오류·중복: 출력 후 뺌, 바꿈: 쿼리 필드 버튼 → 바로 적용 (Field.json 직접 고침: 필드 팝업 열 때 반영))
      → TCP: 클라 Count 답·클라 얼라이브 (1분마다) 답 {"sType":"Alive","aField":[...],"sTime":""} 으로 내려줌 + 바뀜: 접속 클라 모두에 얼라이브 답 바로 (Count 답과 같은 aField, 클라: 바뀜만 적용, 클라가 값 뽑아 보냄, 이 키·고정값만 받음: 그 밖 버림), HTTP /api/external/log: 이 키·고정값만 받음 (그 밖: 400), 설정 순서 = 필드 목록 순서
   ### Mail: sHost·nPort (기본 25, bSsl: STARTTLS 587, 암시적 SSL 465 미지원)·bSsl·sFrom (없음: sID)·sID·sCode (알람 메일 SMTP, sHost 없음·sFrom·sID 모두 없음: 메일 실패 → 펜딩·재시도 후 포기, sID 있음: 인증)
   ### Redis: sHost·nPort (기본 6379)·nDB·sUser·sCode (복호화 → sPass)·키 앞 (실제 키 = 값 + 존 + ":"): sManager (운영 탭 패치 시각·동접 기록, 존: Manager:sZone 없음 sZone, 예: HMX:Manager: → HMX:Manager:Real:patch_start_dttm, sHost 없음: 동접 기록 안 함)·sProcess (반영 탭 서버 목록, 존: sZone, 기본 HMX:Process: → HMX:Process:Dev:)·sError (반영 탭 에러 리스트, 존: sZone, 기본 HMX:Error: → HMX:Error:Dev:) (HMXClient 와 같게)
   ### Manager (Manager.json): sZone (운영 존: ServerList 선택, 없음: 최상위 sZone)·nTimeout (XLO 모니터 요청 제한 ms, 기본 30000)·sCode (XLO 모니터 비밀번호, AES 키 솔루션명 → sPass, 목록 응답에 안 넣음)·ServerList:{존} [{sName, sIP, nPort}] (운영 탭 점검 대상: Manager:sZone 존만, XLO 모니터 요청 제한: nTimeout)

## TCP 수신: 프로젝트명.json 의 nPort (모든 인터페이스, 없음: 미사용, aMenu 쿼리·서치·파일·덤프 모두 빠짐: 안 엶) ← Client (한 줄 = JSON 1건: Hello·Count·Alive·File·Log·Ping)
   ### 받은 진행 (Count, 클라와 같은 코드): 머신 → 대상 폴더마다 기준 폴더 하위 data\count\머신\대상 폴더 (드라이브 ':' 제거)\Count.data (최대 2줄, 아랫줄 최신, 줄: "날짜 폴더\파일\t줄 번호")
      #### 클라 Count (접속 때 Hello 뒤 1번, 한 패킷 {"sType":"Count","arrFolder":[""]}: 클라 관리 목록) → 목록 밖 이 머신 Count 지움 (메모리·파일) → 목록 폴더마다 받은 진행 한 패킷 {"sType":"Count","arrCount":[{"sFolder":"","sRel":"","nLine":0}],"aField":[{"sKey":"","sRegex":"","sValue":""}]} (없는 폴더: 뺌, aField: 설정 aField)
         클라: aField → 보낼 줄 필드 (이 접속 동안), 답 폴더 → 그 위치부터 보정 (서버 앞섬: 건너뜀, 뒤짐: 다시 보냄), 답 없는 폴더 → 클라 로직대로 (클라 Count 줄 다음·없음: 최신 파일 끝) → 그 뒤 Log 전송
      #### 클라 CountDel (감시 폴더 사라짐, 한 패킷 {"sType":"CountDel","arrFolder":[""]}) → 관리 목록에서 뺌 → 그 폴더 Count 지움 (메모리·파일, 답 없음)
      #### 클라 Alive (1분마다, 한 패킷 {"sType":"Alive","arrFolder":[""]}: 지금 관리 목록) → 관리 폴더와 다름: Count 처럼 맞춤 (목록 밖 Count 지움·새 폴더 관리) → 답 {"sType":"Alive","aField":[...]} (설정 aField, 받은 진행 없음 → 클라 보정 안 함, 클라: 바뀜만 다음 Log 부터 새 필드)
      #### 관리: 클라가 Count 목록 보낸 머신의 목록 폴더만 (예전 클라·목록 밖: 그대로 받음), Count 목록 뒤 ~ 접속 종료까지 워커 메모리 (머신 → 대상 폴더, 처음: 파일 읽음), 접속 종료: 쌓인 Log 처리 뒤 메모리에서 뺌 (파일: 그대로)
      #### Log: 받은 진행 (파일 날짜·시간, 줄 번호) 이하 줄 (클라 재전송): 뺌 (로그) → 원문 쓴 뒤 진행 저장 (없음: 처음 받은 값)
   ### HTTP 수신 (외부 API, 목록: API.m_arrExternal): GET·POST /api/external/log/키/토큰/?sMachine=&sPath=&sFolder=&aLine=&aNo=&추가 필드=값 (쿼리 문자열 있음: 쿼리 (aLine·aNo: 반복, 그 밖 키: 모든 줄 추가 필드), 없음: 본문 JSON {sMachine, sPath, sFolder, aLine, aNo, aField}, 추가 필드: 설정 aField 키·고정값만 (그 밖: 400 필드 오류)) (TCP Log 의 arrLine·arrNo·arrField → aLine·aNo·aField + sMachine, 로그인 대신 토큰 = 설정 sCode (끝 '/' 허용, 불일치: X초 지연 후 401, bAuth false: 확인 안 함), 요청 로그: 토큰 ****) → TCP 와 같은 처리 (ReadLog → 적재 큐, 받은 진행 관리 안 함, 원문 시간: 요청 받은 시각 (KST, 요청 1건 같은 시각, 원문은 그대로 → 쿼리 시간·기간·막대만 받은 시각)) → {sError, nLine} (형식 오류·머신 없음·경로 오류 (전체 경로·.log 아님)·빈 줄: 400, 적재 밀림 (클라 Pause 중): 503 → TCP 우선)
   ### 외부 API 쿼리 (TabQuery.OnExternalQuery): GET·POST /api/external/query/키/토큰/?sType=&nStart=&nEnd=&nMinute=&nPage=  (쿼리 권한 계정, 브라우저 GET (text/html): 키 계정 로그인 → 쿼리 탭 그 쿼리·기간 바로 실행 (접근 탭 없음: 403), 그 외: JSON, 쿼리 문자열 없음: 본문 JSON 같은 키, sType (로그 타입 등 쿼리) 없음: *, 끝 없음: 지금, 시작 없음: 끝 X분 전) → /api/query 와 같은 응답, 알람 메일 본문 끝 링크 (Web.GetApiQueryLink: 받는 계정 토큰, 훅: 링크 없음, 기간 yyyyMMddHHmmss), 토큰: 그 계정 sCode 를 sRand 키로 암호화 (Auth.GetToken = TO_BASE64(AES_ENCRYPT(sCode, sRand)), 저장 안 함)
   ### 로그 감시 원문 (TabQuery): 클라 Log (aLogFolder 최신 로그의 aLogType 줄, sFolder: 대상 폴더 전체 경로) → 기준 폴더 하위 data\origin\머신\클라 경로 (드라이브 ':' 제거) 에 줄마다 레코드 1줄 (원문 저장 1번, 색인: 바이트 위치·길이로 참조)
      #### 레코드: [원문 시간][Machine][Folder][Line]:[원문]:[Type]{추가 필드} (원문 시간: 원문 앞 첫 [시간]·없음: 받은 시간 KST·HTTP /api/external/log: 요청 받은 시각 (원문 앞 시간 무시), 원문: 받은 줄 그대로, Line: 클라 파일 줄 번호 (예전 레코드: 없음), Type: 시간 뒤 첫 [] 안, 추가 필드: 설정 aField [{sKey, sRegex}] (클라가 Count 답으로 받음) 첫 () 값 → JSON {키: 값} (이름: data\index\field.txt, 설정 aField 순), 단어 색인: 원문에서 앞 시간 뺀 내용)
         예) [2026/10/31 09:07:31.635][GAME01][D:\Logs\XLobby][1523]:[[2026/10/31 09:07:31.635] [ERROR] [TID:11700] 실패 sno(123)]:[ERROR][123]
   ### 로그 감시 적재 (TabQuery, 멀티스레드 + 스왑 큐): 수신 스레드 → 머신·대상 폴더 해시 워커의 라이트 큐 (잠금: Add 순간) → 워커 (코어 수 × 2.5) 가 신호마다 리드·라이트 큐 스왑 (잠금: 스왑 순간) 후 리드 큐 잠금 없이 처리 → 같은 폴더 (시간 파일 바뀜 포함): 한 워커 순서대로, 다른 폴더: 동시 (원문 파일 번호·추가 필드 이름: 있음 → 잠금 없이, 새 것만 잠금, 색인 폴더 지움 확인: 타이머)
      #### 라이트 큐 밀림 (X줄 이상)·색인 버퍼 밀림 (최대 줄 수 이상, 세그먼트 쓰기가 못 따라감): 부하 로그 (워커마다 X초에 1번) + 상한 넘칠 때마다 (앞 멈춤 뒤 X ms 지남) 전송 중 클라의 분당 Log 줄 (지난 1분 + 이번 분) 많은 순 상위 X% 더 {"sType":"Pause"} (전송 중 수 기준 올림 → 남은 1대도 멈춤, 이미 멈춤: 다시 안 보냄, 읽기·전송 멈춤, 위치 유지) → 모든 워커 X줄 미만·색인 버퍼 절반 미만: 모든 클라에 {"sType":"Resume"} (클라: 멈춘 상태만 재개·전송 중: 무시, 쌓인 줄: X초마다 최대 nLogSend), 새 클라: Pause 안 보냄
      #### 클라별 카운터 (ClientInfo, Interlocked): 받은 메시지·바이트·Log 줄 누적 + 이번 분 → 타이머가 1분마다 0 과 원자 교체 → 지난 1분 (분당, 상위 X% 고르기), 1분마다 로그 합계 1줄 (머신별: /api/client)
      #### 적재 메모리 상한 = 시작 때 남은 물리 메모리 절반 (시작 로그): 라이트 큐 최대 줄 수 (상한 1/4 ÷ 줄당 추정)·색인 버퍼 최대 줄 수 (상한 1/4 ÷ 줄당 추정, 세그먼트 쓰는 중 포함)·워커마다 라이트 큐 최대 줄 수 (밀림 기준 × X) 넘음: 수신 스레드 대기 (TCP 역압, Pause 무시 클라 포함, 워커별: 그 워커 담당 클라만), 합침 묶음 최대 바이트 (상한 1/2, 세그먼트 파일 크기 합), 워커 예외: 출력 후 계속
   ### 로그 감시 색인 (TabQuery, 세그먼트 역색인): 원문 단어 + 필드 값 → 워커마다 원문 시간별 메모리 버퍼 (단어: 64비트 해시만 (문자열 안 만듦), 포스팅: int 블록 아레나 (단어·값마다 객체 없음)) → 타이머가 X초마다 버퍼 교체 (잠금: 교체 순간, 색인 버퍼 절반 넘음: 나이 상관없이 모두) 후 시간마다 1개로 (쓴 시간 버퍼: 바로 놓음) → 기준 폴더 하위 data\index\원문 시간(yyyyMMddHH)\
      #### 단어: 글자·숫자·'-'·'_' 연속 (그 외 기호·빈칸에서 나눔, 앞뒤 '-'·'_' 뗀 형태·가운데 '-'·'_' 조각도 색인, '-'·'_' 뿐: 제외, 소문자 → 대소문자 무시·딱 맞는 단어만, 예: [NOTI_] → noti_·noti, sk_hidden_b7 → sk_hidden_b7·sk·hidden·b7 (조각: 새로 받은 줄부터))
      #### 와일드카드: 검색어 앞뒤 * (ab*: 단어 시작, *ab: 단어 끝, *ab*: 어디든, 대소문자 무시, 경계: 글자·숫자 아님 ('-'·'_' 포함 = 조각과 같음)) → 색인 안 씀, 후보 줄 원문 확인 (후보: 같은 AND·NOT 왼쪽 결과), 1차 조건 필수 (와일드카드만·OR 쪽: 쿼리 오류 "와일드카드 1차 조건 필요", 알람: 줄마다 확인 → 없어도 됨), 필드:값 안 * : 안 됨, 알람·서치 탭 (클라) 도 같은 규칙
      #### 필드: Machine·Folder·Type·추가 필드 (존: 설정 aField 예: zone) 값 통째로 (대소문자 무시·딱 맞는 값, 값 없음: 색인 안 함)
      #### file.txt                      : 원문 파일 번호 목록 "번호,머신,클라 경로,Folder,Zone" (긴 경로 1번만, 줄 목록: 번호만, Zone: 예전 클라 sZone → 파일 번호 구분만·필드 아님)
      #### 원문 시간\doc.bin             : 줄 목록 (줄 번호 = 순서, 줄당 20바이트: 분초(ms)·원문 파일 번호·원문 위치·원문 바이트)
      #### 원문 시간\첫 줄 번호-끝 줄 번호.idx : 세그먼트 (머리: 버전·단어 수·필드 사전 바이트·버킷 시작 257 + 단어 사전: 64비트 해시 순 칸 (해시·포스팅 위치·건수·바이트), 해시 상위 바이트 256 버킷 + 필드 사전: "필드\t값" 순 칸 (포스팅 위치·건수·바이트·키) + 포스팅: 줄 번호 오름차순 차이 varint)
         세그먼트 쓰기: 버퍼마다 해시 순 배열 → 버퍼 순 (= 줄 번호 순) k-way 합침 → 포스팅 이어 쓰기 (단어·값마다 List·int[] 안 만듦)
         합침 (합침 스레드 따로: 세그먼트 쓰기 안 밀림): X개 넘음: 최신부터 비슷한 크기끼리 (묶음 파일 크기 X 이하, 메모리 상한), 마지막 쓰기 후 X분: 1개로 (크기 X 넘는 시간: 그대로), 스트리밍: 세그먼트 사전 해시 순 k-way 합침 → 포스팅 바이트 그대로 이어 붙임 (조각 첫 varint 만 앞 조각 마지막 줄 번호 기준으로 다시 → 디코드·메모리 거의 없음), 옛 파일: 더 넓은 범위에 포함 → 읽는 쪽이 뺌 (합치는 중 쿼리 가능), 옛 파일 삭제: 세그먼트 읽기·쓰기 잠금, 종료: 라이트 큐 X초분까지 처리 (넘음: 버림 → 받은 진행 관리 폴더는 클라 재전송으로 복구)·버퍼 모두 파일로 (강제 종료: 최대 X초분 색인만 손실)

## 웹 API
   ### 쿼리·알람 (TabQuery·Alarm·AlarmLog)
      #### 쿼리 부하 제한 (TabQuery, 쿼리·렌즈·통계 공통): 세션 (로그인 계정, 없음: IP) 마다 1건씩 동시 (전체 제한 없음, 같은 세션 둘째: 대기), 제한 시간 nLimit 초 (기본 30·최대 100, 페이지 설정, 넘음: sError "제한 시간 초과"), 시간 폴더 병렬 = 코어 절반, 양보 Yield: 스레드마다 연속 X ms 넘음 → X ms 쉼 + 취소 확인 (세그먼트·줄 단위)
      #### 쿼리 (TabQuery): /api/query?sKey=&nStart=&nEnd=&nPage=&nOffset=&nCount=&nLimit=  (기간 yyyyMMddHHmm·초별 막대 클릭 1초: yyyyMMddHHmmss (통계·필드 값 목록도 같음), 시작 포함·끝 미포함, nOffset·nCount: 페이지 안 이어 읽기) → 시간 폴더마다 병렬로 포스팅만 읽어 AND·OR·NOT 정렬 병합 (sKey: SearchNode 규칙, 필드:값 → 그 필드 값 줄, * → 전체, 단어·필드 1개·시간 전체: 사전 건수 합) → 시간별·분별 건수 + 최신 순 X건 페이지만 원문 읽음 (같은 쿼리·기간: X초 보관) {sKey, nStart, nEnd, nTotal, nPage, nPageSize, nElapsedMs, sError, arrHour: [{nHour, nCount}], arrMinute: [{nMinute, nCount}] (기간 1시간 이하), arrSecond: [{nSecond, nCount}] (기간 1분 이하), arrLine: [{sTime, sMachine, sPath, sFolder, sText (시간 뒤 원문), sOrigin (원문 그대로), nLine, arrField: [{sName, sValue}]}]}
      #### 렌즈 초·분봉 (TabQuery): /api/query/lens?sKey=&nStart=&nEnd=&nSec=  (기간: 쿼리와 같음, nSec 1: 초봉·그 외: 분봉, 최대 X칸) → 시간 폴더마다 병렬로 맞는 줄 분초 → {sError, nElapsedMs, arrTime: [{nTime, nCount}] (초봉 yyyyMMddHHmmss·분봉 yyyyMMddHHmm, 기간 첫 칸 정각부터 모든 칸 0 포함)} (보관 안 함)
      #### 필드별 통계 (TabQuery): /api/query/stat?sKey=&nStart=&nEnd=&sField=  → 쿼리·기간 줄 ∩ 필드 값 포스팅 → {sKey, nTotal, nElapsedMs, sError, arrField: [{sName, bBase, nValue, arrValue: [{sValue, nCount}] (건수 순 X개, sField: 그 필드 전체)}]}
      #### 필드 값 목록 새 창 (TabQuery): /query/field?sKey=&nStart=&nEnd=&sField=  → 필드 모드 페이지 (값 X개씩 페이지, 창 너비: 전체 값 중 가장 긴 줄에 맞춰 넓힘, Count: 숫자만, 값 클릭: 연 창에서 필드:값 쿼리 후 이 창 닫음, 제목 클릭: 그 칸 기준 보이는 페이지만 내림차순 ▼ → 오름차순 ▲ → 원래 순서 (페이지 넘김: 같은 정렬))
      #### 필드 목록 (TabQuery): /api/query/field → {arrField: [{sName, bBase}]} (기본 필드 + 설정 aField 추가 필드)
      #### 머신 목록 (TabQuery): /api/query/machine → {arrMachine} (data\index\file.txt 의 머신, 접속 안 된 머신 포함 → 쿼리 M 목록: 접속 머신과 합침)
      #### 저장 쿼리 (SavedList): /api/query/saved → {sError, arrQuery: [{sKey, sQuery, nMinute}]}, POST /api/query/saved {sKey, sQuery, nMinute, sOldKey} (같은 이름: 교체, sOldKey: 그 자리 수정)·/api/query/saved/delete {sKey}·/api/query/saved/move {sKey, nTo} → Query.json 의 aQuery (요청자 IP 로그), 저장 서치: /api/search/saved (같은 형식, arrSearch, Search.json 의 aSearch)
      #### 알람 목록 (TabQuery): /api/alarm → {arrAlarm}, POST /api/alarm {nID (0: 추가), 값}·/api/alarm/delete {nID}·/api/alarm/move {nID, nTo}·/api/alarm/test {값} (메일·훅 바로, 저장 안 함) → 프로젝트명.json 의 aAlarm (요청자 IP 로그)
      #### 필드 정의 (TabQuery): /api/field → {arrField: [{sKey, sRegex, sValue}]}, POST /api/field {sKey, sRegex, sValue, sOldKey} (sOldKey: 그 자리 수정, 없음: 끝에 추가, 이름 겹침: 오류)·/api/field/delete {sKey}·/api/field/move {sKey, nTo} → 프로젝트명.json 의 aField → 바로 적용·접속 클라 알림 (요청자 IP 로그)
      #### 알람 (Alarm): 적재 때 매칭 (앵커 단어·필드로 후보만 조건 평가, 쿼리와 같은 규칙, 워커 카운터) → Stat 매분 합산 (수신 시각)·nMinute 분 구간 끝 (KST 자정부터 X분마다, 하루 안 넘김, 최대 1440) 판정·nDelay 분 억제 (발송 뒤 X분: 그 시간과 겹친 구간 판정 안 함 → 그 뒤 구간부터)·분당 X건 한도
         → Send X개 스레드 메일·훅 POST {botName, text: 제목, attachments: [{text: 내용}]} (제목·내용: 메일과 같음, 본문 끝 쿼리 링크: 메일만 (훅: 링크 없음 → 채널 누구나 눌러 키 계정 로그인 막음), SMS: 나중에) → data\alarm\log\yyyyMMdd.jsonl (Sent·Failed·Deferred·Resent·Expired) + 알람 기록 data\alarm\history.jsonl (날짜·제목·내용·결과, 발송 시도마다·테스트 포함, 최신 X건), 한도 넘음·실패: data\alarm\pending.json → 다음 분 재전송 (X번·X분 넘음: 포기)
      #### 알람 로그 (AlarmLog): /api/alarm/log?nStart=&nEnd=&nID=&sResult=&nPage=  (기간 yyyyMMddHHmm, 최대 X일) → {sError, nTotal, nPage, nPageSize, arrLog} (최신 순)
      #### 알람 기록 (AlarmLog): /api/alarm/history → {sError, arrHistory: [{sDone, nID, sKey, sChannel, sResult, sError, nTry, sTitle, sText}]} (전체 최신 순) → 쿼리 기록 버튼 (알람 버튼 오른쪽) 창: 날짜·제목·채널·결과 (스크롤), 제목 클릭: 내용 창 (링크 안 보임, 내용 클릭: 그 알람 기간·쿼리 실행)
   ### 운영 (TabOperation, ServerManager 이식): /api/manager/server (전체 점검 → 목록·패치 시각)·/version (POST 버전 임시)·/monitor (XLO 모니터 이동)·/cds (1대 CDS)·/patch (POST 패치 시각, Redis)·/ccu (최고 동접·최근 X일 기록, Redis)·/redis (PING)
      #### 동접 기록: X분 정각마다 전체 점검 → 운영 로비 접속자 게임별 합 → Redis daily_날짜 해시·max_conn (서버 목록·Redis 있을 때만)
   ### 실행 (TabProcess·Process, HMXServer 이식, 요청·응답 형식 같음): /api/process (목록: 공통 Redis 서버 목록 키 (Redis:sProcess + 존) 이름순 "필드|값" 줄, Redis 설정 없음: 오류)·?action=check (TestOn 표시)·version (설정 버전)·set (값 저장: 등록·수정 version 검증·+1, On·Off·Down: 값 앞 상태,False)·rename (이름 일괄 변경, POST 본문 JSON arrField·arrNewField: 키 많음 → 쿼리 길이 제한 피함)·del (삭제, 삭제 전 값 로그)·errlog (Redis 에러 리스트 (Redis:sError + 존) X개씩)·log (서버 로그 [HMX_]·로그인 줄 커서 조회)·actlog (시작·정지·다운 클릭 기록)
      #### 서버 목록 키: Redis:sProcess + 존 + ":" + 대분류,중분류,이름 (예: HMX:Process:Dev:111_DEV,1_Center,Common) · 값: 상태,적용,PID,커밋,머신,다운폴더,깃헙폴더,Sync,Mode,실행파일+인자들 (상태 On·Off·Down, 적용 False: 머신별 HMXClient 처리 대기 → 처리 뒤 True, PID 음수: 실패)
      #### 설정 버전: 기준 폴더 하위 data\프로젝트명.txt (예: data\Server.txt, 등록·수정·삭제·이름 변경 성공: +1, 요청 버전 다름: stale 응답 → 화면 새로 고침 안내, 버전 검증~증가: 직렬화)
      #### 반영 로그: 공통 Log 파일에 레벨 뒤 [v.버전] 표시 (예: [시각] [HMX_][INFO_] [v.4] 수정: …, 등록·수정·이름·삭제·액션·클릭·버전 불일치·오류, 끝: 아이디@IP), 로그 조회: [v.버전] 줄·로그인 줄·예전 [HMX_] 태그 줄만, Redis 오류: X초 지연 후 오류 응답
   ### 클라 요청 (TabSearch·TabDump·TabFile: 머신마다 Client 에 요청)
      #### 부하 자리 (Gate, 기능마다 따로: 세션마다 1건씩 동시, 전체 제한 없음 (Gate nAll 0, 클라 안전 상한: 서치·보기 16), 같은 세션 둘째: 대기, 덤프 다운: 저장 작업 안에서 잡음 → 기다리는 동안 진행 0) + 조각마다 Gate.Yield (압축·변환 CPU 양보), 클라: 검색 동시 2건·Throttle 0.5초 → 0.1초
      #### 접속 클라 목록: /api/client (클라마다 받은 메시지·바이트·Log 줄 누적 nRecv*, 분당 (지난 1분) nMin*, 밀림 멈춤 bPaused)
      #### 로그 검색 (TabSearch, 여러 머신): POST /api/search {arrMachine, nStart, nEnd, sKey, nMax, nLimit, sToken}  (sKey: AND·OR·NOT·괄호·앞뒤 * 와일드카드, 클라 SearchNode 처리, 예: A AND (B NOT C) OR D, nLimit: 서치 제한 시간 초 (기본 30·최대 100, 클라 검색 마감, 서버 대기 = + 여유 X초))  → 머신마다 클라에 Search, 끝난 순서로 한 줄씩 {nIndex, sMachine, sError, kResult} (NDJSON)
      #### 파일 보기 (TabSearch, 머신 1대): /api/view?sMachine=&sPath=&nStart=&nCount=  → 클라에 View (nStart 0: 마지막)
      #### 검색 진행·중지 (TabSearch): /api/progress?sToken= (진행 머신들의 진행 합), /api/stop?sToken= (머신마다 Stop)
      #### 덤프 목록 (TabDump, 여러 머신): POST /api/dump {arrMachine}  → 머신마다 클라에 Dump, 끝난 순서로 한 줄씩 {nIndex, sMachine, sError, kResult} (NDJSON, kResult.arrFile[].bZip: Server dump 폴더 zip 있음)
      #### 덤프 다운 (TabDump, 머신 1대): POST /api/dump/download {sMachine, sPath}  → 클라에 DumpRead 조각마다 요청 → 기준 폴더 하위 dump\머신\덤프 파일명.zip 압축 저장 (바로 응답, 페이지가 X초마다 재호출: {bDone, nOffset, nSize})
      #### 덤프 zip 받기 (TabDump): /api/dump/zip?sMachine=&sPath=  → dump\머신\덤프 파일명.zip 첨부 전송 (없음: 404)
      #### 덤프 삭제 (TabDump, 머신 1대): POST /api/dump/delete {sMachine, sPath}  → 클라에 DumpDelete, 성공: 다운 zip 도 삭제 (빈 dump\머신 폴더 삭제, 요청자 IP 로그)
      #### 폴더 보기 (TabFile, 머신 1대): /api/files?sMachine=&sPath=  → 클라에 Files (sPath "": 클라 aLogFolder 목록, 결과: 바로 아래 폴더·파일)
      #### 파일 다운 (TabFile, 머신 1대): /api/files/download?sMachine=&sPath=  → 클라에 FileRead 조각마다 요청 → UTF-8 BOM + 줄마다 UTF-8·CP949 판별 → CP949 줄만 UTF-8 → 브라우저로 (첨부, 압축 안 함, UTF-16 BOM: 그대로)

## 로그 (설정 sLog 레벨 이상만, 콘솔·파일 같이): bLog true 일 때만 기준 폴더 하위 log/YYYYMMDD/Server_YYYYMMDD_HH.log 시간별 → Log.Write(INFO_, "메시지") (레벨: DEBUG·INFO_·NOTI_·WARN_·ERROR·FATAL, 줄: [시각] [sTitle_][레벨] 메시지, sTitle 없음: 태그 없음)
## 타이머: Timer.Start → 별도 스레드 X초마다 Timer.Batch (X분마다 Alive 로그, TabQuery.Tick: 색인 버퍼 → 세그먼트, 색인 폴더 지움 확인, 합침 스레드 깨움), 합치기: 합침 스레드 따로 (세그먼트 쓰기 안 밀림), 클라 분당 교체: 1분 타이머 따로 (스레드 풀, 색인 Flush 로 안 밀림)
## 종료 (서비스 중지·Ctrl+C, 호스트 ShutdownTimeout X초 동안 중지 대기): Web.StopAsync → 웹 중지 (클라 접속·수신 중지, 연결 끊음) → 모든 클라 수신 끝 대기 (끊긴 클라 정리 표시까지) → Main.Shutdown (1번만)
   ### Main.Shutdown: Timer.Stop → TabQuery.Close (워커 라이트 큐 처리: 원문·받은 진행 저장·닫음, 멈춤 요청 뒤 X초 상한 → 넘는 Log 버림 (받은 진행 관리 폴더: 클라 재전송, HTTP·관리 밖: 그대로 처리)·X초마다 로그 → 남은 색인 버퍼 세그먼트 (타이머 중지 대기 넘음: 타이머 Flush 가 뗀 버퍼 다 쓸 때까지 플러시 잠금 대기 → 색인 안 빠짐) → 합침 스레드 끝 대기) → Alarm.Stop (펜딩 저장), 웹 없음·시작 실패: Main 끝에서
   ### 강제 종료 (프로세스 kill·IIS HttpPlatformHandler 종료 방식 미확인): 큐에 남은 Log·색인 버퍼 사라짐 → 받은 진행 (Count) 은 원문 쓴 뒤만 저장 → 다음 접속 때 클라가 서버 진행부터 다시 보냄 (Count 관리 폴더)
## 웹 서버: bWeb true 일 때만 Web 을 Main.Service 호스트에 등록 (false·키 없음: 호스트만 대기, 서비스 중지·Ctrl+C: 종료)
## 로그인: bAuth true 일 때만 (false·키 없음: 모두 허용), 계정: 관리 계정 (sID·sCode, 켜진 탭 전체) + aAccount (계정 aMenu: 페이지 탭 (__MENU__)·탭 API (Auth.UseMenu: 라우트 → 탭, 없음: 403, /api/client: 쿼리·서치·파일·덤프 중 하나, 공통: /·/api/info·/api/echo·/api/account·로그인), 접근 메뉴 없음: 로그인 실패), 로그아웃: 계정 버튼 → POST /api/logout (인증 쿠키 삭제, 이 브라우저만), 미인증 → 로그인 페이지 → POST /api/login 이 sID·sCode(복호화 → sPass) 일치: 세션 쿠키 발급 (브라우저 종료까지 유지, 미인증 API: 401, Client TCP 제외, 외부 API /api/external (목록: API.m_arrExternal, 로그 수신: /api/external/log/키/토큰, 쿼리: /api/external/query/키/토큰): 키 (계정 sKey, 메모리 키 맵) 계정 + 그 API 권한 (관리 계정·aAccount aMenu 권한 메뉴, 로그 수신: 로그) + 토큰을 그 계정 sRand 키로 복호화 = sCode 만 통과, 키: 영문·숫자 20자리 무작위 (Account.json 계정 sCode 아래 sKey·sRand·sToken, 관리 계정: 최상위 sKey·sRand·sToken, 관리 계정 팝업 계정 줄 클릭: 키·토큰, 계정 수정 → 새 키 → 관리 비밀번호 + 적용: 그때만 저장 (없음: 외부 API 못 씀)) (로그인 무관, 목록에 없음: 401))
   ### 인증 쿠키 암호화 키: 메모리만 (서버 재시작: 모두 재로그인)
   ### 로그인 연속 실패 (Auth.CountFail·RandomPass·IPList.AddBlack): 같은 IP 웹 로그인 연속 실패 (아이디·비밀번호 불일치만, 접근 메뉴 없음·외부 API 토큰: 안 셈, 마스터 키 전: 관리 계정만 처리·그 외 공통 오류 (셈 안 함), 성공: 다시 셈) 3번 (m_nLoginFailMax) → 그동안 시도한 계정 (aAccount, 대소문자까지 같은 아이디만, 관리 계정·없는 아이디 제외) 비밀번호 최대 글자 (100자) 랜덤 (계정마다 1번, Account.json sCode·sToken → 그 계정 쿠키·외부 API 토큰 무효, 풀기: 관리 계정이 수정 창에서 새 비밀번호), 5번 (m_nLoginBlockMax) → 그 IP 를 Json/IP.json aBlackWeb 에 자동 등록 (루프백·모름: 안 함 → 경고 로그, 풀기: IP.json 에서 지움), 관리 계정이 계정 비밀번호 바꿈: 그 아이디를 시도한 IP 연속 실패 지움 + aBlackWeb 에서 sID 에 그 아이디 있는 IP 풀기 (수동 sID 빈칸: 그대로), 경고 로그 (IP·바꾼 아이디·등록)
   ### HTTPS 운영 (Web.m_bHttps): bHttps true + IIS 실행 (HttpPlatformHandler, 콘솔·서비스: 해당 없음 → http 그대로): 로그인 쿠키 Secure (https 로만), 모든 응답 HSTS 헤더 (max-age 1년), 알람 링크 https (sUrl 없음: 내부 IP + 첫 요청 포트, 기본 포트 443 생략), IIS 바인딩: https 만 (http 바인딩 없음, 인증서), IIS → Server.exe: localhost http
## 제목: 프로젝트 + sZone (예: 프로젝트 Alpha), 색: Dev 초록·Alpha 파랑·Real 빨강 (/api/info 의 sTitle·sTitleColor)
## 탭 존: 탭 버튼 data-zone·data-zonecolor (운영 탭: Manager:sZone) 사이트 존과 다름 → 그 탭 켜짐: 존 색만 탭 존 (제목 색 포함 페이지 전체, --zone·--split, :root 존 파생 변수 다시 계산), 다른 탭: 사이트 존 색 (제목 글자·탭 아이콘: 사이트 존 그대로)
## 연결 끊김 (페이지): 요청 접속 실패 (경유 서버 502 포함, 대기 중: 접속 머신 목록 X초 갱신 요청으로 감지) → 제목 옆 빨강 글 "서버 연결 끊김 - 재시도 N초" (마지막 응답 뒤 경과 초, 오류 글: 툴팁) + X초마다 /api/echo 재시도 (앞 확인 응답 대기 중: 건너뜀), 응답: 해제·접속 머신 목록 다시 (쿠키 무효: 401 → 로그인 페이지)
## ToolManager 경유 (/proxy/{번호}/): 스크립트에서 넣는 링크·이동 주소 (a.href·iframe.src·location 등) 상대 주소 (예: 운영 탭 모니터 api/manager/monitor, 파일 다운 api/files/download), 루트 기준 /...: fetch·XMLHttpRequest·window.open·HTML 글자만 경유 주소로 바뀜 (규칙: HTB Source\Source.md)
## 종료: Ctrl+C

# HTB
## C# 파일 한 장으로 띄우는 웹 서버 (.NET 10 file-based app)
   ### 실행: dotnet run --file Source\HTB.cs [포트] [호스트=localhost]  (HTB 폴더에서, 상위 폴더 HTB.csproj 와 따로: --file 필수)
   ### 서비스 (관리자 cmd, 게시된 HTB.exe): -install [포트] [호스트] (서비스 HTB 등록·자동 시작·시작, 뒤 인자: 실행 인자 그대로) | -uninstall (정지·제거) | -start | -stop (시작·정지 X초 대기)
      예) dotnet run --file Source\HTB.cs                -> http://localhost:포트 (외부 접속 미허용)
      예) dotnet run --file Source\HTB.cs 9000 0.0.0.0   -> 모든 인터페이스 9000 포트 (외부 접속 시 방화벽 허용 필요)
   ### 디버깅: HTB.slnx (Visual Studio 2026) 열고 F5
   ### 구성: Global(전역 함수·폴더 소문자) → Log(로그) → AES(복호화) → Config(설정) → IPList(웹 막을 IP) → ConfigCheck(설정 시작 검사) → Timer(타이머) → Web(웹 서버·로그인) → Main(진입점)
   ### 런타임: 설치 불필요 (게시 HTB.exe 에 .NET·ASP.NET Core 런타임 포함 self-contained, 보안 패치: 다시 빌드 후 exe 교체, dotnet run --file 실행만 .NET SDK 필요)
   ### 폴더 이름: 프로그램이 만드는 폴더 소문자 (기준 폴더 하위 log, 예전 대문자 Log: 시작 때 소문자로 이름만 바꿈 Global.FixDirCase, HMX 와 같음)
   ### 기본 페이지: wwwroot/index.html (wwwroot 없으면 HTB.cs 의 IndexHtml())
   ### Template: 기본 페이지의 Template 버튼 → TemplateHtml() 화면 요소 모음 (wwwroot/index.html 은 /api/template 으로 불러와 같은 동작, 다른 툴 참조용, 필요 없으면 TemplateHtml()·OnTemplate()·/api/template·OnSample()·/api/sample·Template 버튼 삭제)
   ### 정적 파일: 기준 폴더에 wwwroot 폴더가 있으면 자동 서비스 (index.html 기본 문서)

## 설정 파일: 기준 폴더 하위 Json 폴더의 프로젝트명.json (Json\HTB.json, 키: 최상위) → Config.GetString("DB:Dev:sHost"), GetInt("DB:Dev:nPort") (키 없으면 기본값)
   ### sCode 키는 로딩 시 AES 복호화 → 같은 위치 sPass 로 조회 (예: "DB:Dev:sCode" → GetString("DB:Dev:sPass"))
   ### AES 키: 마스터 키 (Global.GetMasterKey, 메모리만, 파일·코드 저장 안 함, HMX 와 같음), bWeb·bAuth: 서버 오픈 뒤 첫 접속 1회 로그인 페이지에서 아이디·비밀번호·마스터 키 같이 받음 (Web.Unlock)
      → 설정 sCode 를 그 키로 복호화 = 비밀번호: 마스터 키 메모리 + 설정 다시 로딩 (모든 sCode → sPass), 불일치: X초 지연·로그 "마스터 에러 줄 번호" (사유 숨김, 응답: 일반 로그인 실패 문구)
      그 전: 복호화 안 함 (sPass 없음 → 쿠키 인증 안 됨), 로그인 안 씀 (bWeb·bAuth false): 프로젝트명 (Main.Run 시작 때), 서버 재시작: 다시 입력 (DataCompare·DataFinder 같음)
   ### 경로 값 (`X:\…`·`\\…` 시작): `\` 하나로 써도 됨 (Config.FixPathEscape, 주석·끝 쉼표 허용, HMX 기준 + UNC `\\서버` 하나 표기·`\"` 글자 따옴표 보정)
   ### 최상위: bLog(파일 로그), sLog(남길 최소 레벨: Debug·Info·Warn, 없음: Debug), bWeb(웹 서버), bAuth(웹 로그인), bHttps(IIS HTTPS 운영: 로그인 쿠키 Secure·HSTS 헤더 1년, IIS 실행만·https 바인딩만, 콘솔·서비스: 그대로), sID·sCode(로그인 계정), sTitle(이름: 제목 앞·로그 줄 레벨 앞 [이름_] 태그, 예: HTB → 제목 HTB Dev·로그 [시각] [HTB_][INFO_] 내용, 없음: 제목 프로젝트명·로그 태그 없음), sZone(Dev·Alpha·Real → 제목·색)
   ### aMenu: 탭 메뉴 (아래 탭 메뉴 규칙, HTB 탭 없음 → Web.m_arrTab 빈 배열: 틀만, 탭 생기면 이름 추가 → 빠진 탭: 서버·페이지 동작 안 함)
   ### IP.json: 웹 막을 IP {"aBlackWeb": [{"sIP", "sID"}]} (sIP: IPv4 네 칸 숫자·'*' (예: 10.0.0.1, 192.168.*.*), IPv6 그대로 (예: ::1), "*": 모두, sID: 자동 등록 계정·수동: "", 예전 "IP" 문자열도 됨)
      #### 모든 웹 요청 맨 앞 (Web.UseIPList): 맞음 → 403 Access Denied (페이지·API·로그인), 막음 로그 (같은 IP 1분에 1번), 접속 IP: Web.GetAddr (IIS: X-Forwarded-For)
      #### 파일 없음: 막을 IP 없음, 파일 바뀜: X초 안 반영 (재시작 안 함), 형식 오류: 앞 목록 그대로, 자동 등록: 로그인 연속 실패 X번 → aBlackWeb 끝에 {sIP, sID: 시도한 아이디 ',' 구분} (루프백·모름 제외, 서버가 다시 씀: 주석 사라짐)
   ### 설정 시작 검사 (ConfigCheck, HMX 와 같음): 시작 맨 앞 Json 폴더 모든 *.json → 1단계 형식 (설정 로딩 전) → 2단계 값 (설정 로딩 뒤: bLog·bWeb·bAuth·bHttps true·false, sLog Debug·Info·Warn, aMenu 탭 이름, IP.json aBlackWeb 만·IP 패턴) → 오류 하나라도: 파일·필드·이유 ERROR 로그 (전부) + FATAL → 종료 (고친 뒤 다시 시작)

## 로그: 프로젝트명.json 의 bLog 가 true 일 때만 기준 폴더 하위 log/YYYYMMDD/HTB_YYYYMMDD_HH.log 시간별 기록 → Log.Write(INFO_, "메시지") (레벨: Log 의 DEBUG·INFO_·NOTI_·WARN_·ERROR·FATAL, 줄: [시각] [sTitle_][레벨] 메시지 (sTitle 없음: 태그 없음), 설정 sLog 레벨 이상만 (콘솔·파일 같이), Write(..., false): 콘솔만)
   ### 요청 로그 (Web.UseRequestLog, 콘솔만): [시각] 메서드 경로 -> 상태코드 (소요ms), 페이지 연결 확인 /api/echo?ping (X초마다): DEBUG (설정 sLog Info 이상: 안 남김)
## 타이머: Timer.Start → 별도 스레드에서 X초마다 Timer.Batch 호출 (X분마다 Alive 로그), 종료 시 Timer.Stop
## 웹 서버: 프로젝트명.json 의 bWeb 가 true 일 때만 Web 을 Main.Service 호스트에 등록하여 실행 (false·키 없음: 호스트만 실행하여 대기, 서비스 중지·Ctrl+C 시 종료)
## 로그인: 프로젝트명.json 의 bAuth 가 true 일 때만 (false·키 없음: 모든 접속 허용), 미인증 페이지 접속 시 로그인 페이지(아이디·비밀번호 입력, 마스터 키 입력 전: 마스터 키 칸 추가 → 비밀번호 Enter: 마스터 키로) → POST /api/login (id·pass·key) 이 프로젝트명.json 의 sID·sCode(복호화 → sPass) 와 일치 시 인증 쿠키 발급 → 이후 쿠키로만 인증 (로그인 검사 1회, 세션 쿠키: 브라우저 종료 전까지 유지, 유휴에도 유지 / 미인증: 페이지는 로그인 페이지·그 외 401 차단, 재로그인: 브라우저 새로 실행·서버 재시작 (마스터 키 다시 입력)·계정 변경)
   ### 인증 쿠키 암호화 키: 메모리 (파일 미생성, 서버 재시작 시 새 키 → 모두 재로그인)
   ### 로그인 연속 실패 (Web.CountFail, HMX 와 같음): 같은 IP 아이디·비밀번호 불일치 (계정 미설정·마스터 키 전: 안 셈) X번 → IP.json aBlackWeb 자동 등록 (IPList.AddBlack), 성공: 실패 기억 지움, 실패 응답 X초 지연
   ### HTTPS 운영 (Web.m_bHttps): bHttps true + IIS 실행 (HttpPlatformHandler, HTTP_PLATFORM_PORT): 로그인 쿠키 Secure (https 로만)·모든 응답 HSTS 헤더 (Web.UseHsts)
## 제목: 프로젝트명 + 프로젝트명.json 의 sZone (예: HTB Dev), 색: Dev 초록·Alpha 파랑·Real 빨강 (wwwroot 페이지는 /api/info 의 sTitle·sTitleColor 사용)
## 연결 끊김 (페이지): 요청 접속 실패 (경유 서버 502 포함, 대기 중: X초마다 /api/echo 확인) → 제목 옆 빨강 글 "서버 연결 끊김 - 재시도 N초" (마지막 응답 뒤 경과 초, 오류 글: 툴팁) + X초마다 /api/echo 재시도 (앞 확인 응답 대기 중: 건너뜀), 응답: 해제 (쿠키 무효: 401 → 로그인 페이지)
## ToolManager 경유 (/proxy/{번호}/...) 주소 규칙 (경유 표시 툴 필수, 어기면 ToolManager 주소로 가서 404·빈 창)
   ### 경유 주소로 바뀜: 응답 HTML 글자 href·src·action="/..." (스크립트 안 문자열 포함), fetch·XMLHttpRequest·window.open("/...") (ToolManager 경유 스크립트), 같은 사이트 리다이렉트 Location
   ### 안 바뀜: 스크립트 속성 대입 a.href·iframe.src·form.action = "/...", location.href·assign·replace("/..."), history.pushState·replaceState("/..."), location.origin·new URL 로 만든 절대 주소, EventSource·WebSocket("/...")
   ### 스크립트에서 넣는 링크·이동 주소: 상대 주소 (예: kEchoLink.href = "api/echo?..." → 직접: /api/echo, 경유: /proxy/{번호}/api/echo, HMX 관리 탭 모니터·파일 다운 같음), 지금 페이지 주소 유지: location.pathname 기준, blob·data 주소: 그대로
## 종료: Ctrl+C

# **UI/UX Guidelines (모든 툴 공통 가이드, 기준 구현: HMX)**
   HMX Server\Source\Source.md = HTB\Source\Source.md 같은 한 문서 (고칠 때: 양쪽 같이 똑같이, 툴 고유: 위 # HMX Server·# HTB·아래 ## HTB 전용)
   새 툴·기존 툴 고침: 이 가이드대로 (툴마다 다르게 안 함), 예·값·이름: HMX 기준 (HMX 기능 이름: 그 자리 기준 예, 없는 요소: 뺌, 순서·모양: 그대로)
   가이드 고칠 때: HMX 먼저 고침 → 이 가이드 같이 고침 (함수·상수 이름: HMX Web.cs·Tab*.cs 기준)
   UX/UI 수정 때마다 (AI 포함, 필수): 그 내용을 공통 기능으로 이 가이드 해당 섹션에 같이 정리 (HMX 기준 예·값·함수 이름, 흩어진 항목: 한 자리로 모음, 바뀐·없어진 내용: 고침·지움, 툴 고유처럼 쓰지 않음)

## 디자인 기준
   ### GitHub Primer 그대로 (@primer/primitives 토큰, Octicons), 자체 색 추가 금지 (존 색만), 버튼 선택: 검정 (탭 전환 버튼: 존 색), 중요 버튼: 일반 버튼 모양 + 글자만 존 색
*frontend-design 스킬로 초기 제작*

## 존 색 (GetTitleColor → CSS --zone, Dev 초록·Alpha 파랑·Real 빨강, 존 없음: 검정·기본)
   ### 제목 글자·테두리, 로그인 제목·버튼, 탭 전환 버튼 선택 (흰 글자), 계정·힌트 버튼 글자, 지금 페이지 번호, 입력칸·포커스 테두리
   ### 중요 버튼 (실행 등): 일반 버튼과 같음 (흰 바탕·기본 테두리·마우스 올림·누름 회색 바탕) + 글자만 존 색 (꺼짐: 회색 글자, 존 없음: 기본 글자)
   ### 위험 버튼 (중단·삭제·삭제 아이콘): 글자 존 색, 마우스 올림: 존 색 바탕 + 흰 글자, 꺼짐: 반투명 (존 없음: 빨강)
   ### 창 사이 바·칸 경계 마우스 올림·끌기: SM 존 색 (--split, 제목 색과 별개)
   ### 탭 아이콘: 도구 한 글자 굵은 흰 글자 + 존 색 바탕 (HMX X·SM S·HTB B·ToolManager T·DataCompare C·DataFinder F)
   ### 막대 차트: 존 색 50% 밝게, 날짜별 x축 월 글자
   ### 상자 제목 숫자: 쿼리·서치 결과 건수·시간 숫자만 (예: 서치 결과: 7,037건 · 서치 시간: 852 ms, 머신·파일 수·안내 숫자: 기본 글자, 공통 SetResultTitle)

## 화면 틀 (제목란·탭·계정·힌트)
   ### 제목란 배치 (왼쪽 → 오른쪽): 새로 고침 버튼 → 제목 (프로젝트 + 존) → 연결 끊김 글 / 오른쪽 끝 묶음: 탭 전환 버튼 → 계정 → 힌트
   ### 탭 전환 버튼: 제목란 오른쪽, 선택 탭 존 색, 처음: 맨 앞 탭 (HMX 기본: 유틸), 다른 탭에서 돌아옴: 마지막 상태, 이전 탭 버튼 포커스 테두리 없음
   ### 유틸 탭 (HMX, TabUtil.UtilHTML class Util, 쿼리 앞): 제목란 아래 #UtilPanel (명령 창 없음) = 1번 창 #UtilTabPanel (쿼리·서치 명령 창처럼 제목 없이 흰 배경 한 칸, 회색 머리 없음): 왼쪽 하위 탭 타임 (시간)·디프 (비교)·소트 (정렬) → 바로 오른쪽 같은 줄에 그 하위 탭 도구 (.utilbox: 타임 Time·KST·Unix, 디프 정보·버튼, 소트 버튼, 버튼: 오른쪽 끝, 좁음: 줄바꿈) → 아래 하위 탭 창
      하위 탭 버튼: 반영 탭 페이지 탭과 같은 모양·크기 (다른 버튼과 같음, 지금 탭: 존 색), 누름: 그 하위 탭 상자만 (디프·소트 내용: 차후), 최초 입장·F5 리셋: 타임, 다른 탭에서 돌아옴: 마지막 하위 탭
      타임 입력 #UtilTimeInput (1번 창 하위 탭 오른쪽 같은 줄, 따로 창 없음): 1줄 "Time" + 현재 시간 (KST yyyy-MM-dd HH:mm:ss, 1초마다 (초 바뀌는 때), 입력 없음, 타임 보일 때만 바꿈)
         "KST" + 날짜·시·분·초 (Enter: 변환, 실행 버튼 없음, 입력 끝 (바뀜): 그 시간 → Unix 칸, 날짜 범위: Unix 10자리 안 (1970-01-01 ~ 2286-11-20, 밖: 앞 값으로 되돌림), 시·분·초 넘는 값: 입력 안 됨), "Unix" + 칸 (초, 숫자만 10자리 고정: 넘는 자리·글자 입력 안 됨, 밀리초 붙여넣기: 앞 10자리 = 초, 입력 끝 (포커스 나감·Enter): 그 시간 → KST 칸)
         새로 고침 아이콘 (KST 앞, 누름: 화살표 1바퀴 + KST·Unix 칸 현재 시간), 실행 버튼 (Unix 칸 바로 뒤, 엔터 아이콘, Unix 변환, Enter 도)
         날짜 없음·형식 아님: 안 넣음 (표시 없음)
         배치: 한 줄에 묶음 셋 (모두 오른쪽 끝에 붙음: Time: 글자·시간 | KST: 새로 고침·글자·날짜·시·분·초 | Unix: 글자·칸·실행), 묶음 사이 좁게 (디프 버튼 줄과 같음), 좁음: 묶음 단위로 줄바꿈 (묶음 안: 안 나눔)
         처음·탭 열 때: 두 칸 현재 시간, 안 고친 칸: 실행 누른 때 현재 시간 (고침: 그 값, F5 리셋: 다시 현재 시간), KST·UTC: UTC + 시차로 계산 (브라우저 시간대 무관)
      타임 2번 창 #UtilTimeResult: 제목 "결과: N건" + 표 하나 No.·KST·Unix·ISO 8601 (KST 시간대, 예: 2026-11-10T06:30:20+09:00)·UTC·Day (KST 요일 영어 세 글자, 예: Mon)·Ac. (Ac.: 삭제 아이콘 → 그 줄만 지움 (확인 없음), 두 실행 모두 같은 표, 새 결과: 맨 위·No. 1, 같은 시간도 그대로 쌓음, 최대 100줄 넘음: 맨 아래 (오래된) 줄부터 지움, 브라우저 저장 (localStorage HMX.UtilTime, 새로 고침 유지), F5 리셋: 그대로)
         상자 높이: 브라우저 창 아래 끝에 맞춤, 칸 경계 드래그·칸 접기 (모든 표 공통), 빈 결과: 빈 표 그대로 ("결과 없음" 줄 없음)
      디프 (비교, 좌·우 문서 비교): 1번 창 하위 탭 오른쪽 같은 줄 = 왼쪽 제목 #UtilDiffInfo | 오른쪽 네비게이션 ← N → · 이전 차이 · 다음 차이 · 실행 버튼 #UtilDiffRun (엔터 아이콘, 누름: 네비게이션 기억 (새 입력: 새로, 그 외: 지금 기억 글만) → 비교)
         제목: "다름·같음 · 차이 N곳 · 현재 i/N" (DataCompare 와 같은 모양, 비교 안 함·한쪽 비움·비교 풂: 비움), 차이: 줄 비교 이어진 바뀜 묶음 (DataCompare 와 같음), 비교: 첫 차이로
         이전 차이·다음 차이 (DataCompare 와 같은 ∧·∨ 아이콘만, 글자 없음, 정사각, 툴팁: 이름·Alt+↑·↓): 그 차이 좌·우 줄 세로 가운데 + 그 줄 번호 칸 진한 파랑 (DataCompare 지금 차이와 같음: 진한 파랑 바탕·흰 숫자, 회색 빈 줄도, 공통 문서 비교 UI), 더 갈 차이 없음: 꺼짐
         네비게이션 (명령 창과 같음): 새 입력 (좌·우 리셋 (다 지움·F5) 뒤 입력·파일 열기 (찾기·끌어 놓기)) 마다 다음 실행에 1개 (그 입력 고침·실행·저장: 새로 안 넣음 → 지금 기억 글만 마지막 실행 글로), 최근 10개 (넘음: 가장 오래된 것 버림, 지금 기억과 같은 글: 안 넣음, 좌·우 다 비움: 안 함, 브라우저 메모리만), ←: 한 단계 과거·→: 한 단계 미래 (마지막 실행까지)·가운데 숫자 (0: 마지막 실행, 과거: -1·-2…) 누름: 입력 → Enter: 그 위치 → 그 입력·이름 그대로 비교, 과거에서 새 입력 실행: 그 뒤 기억 버림
         → 아래 좌 창 #UtilDiffLeft · 가운데 바 #UtilDiffSplitter · 우 창 #UtilDiffRight (운영 탭 좌우 창과 같은 구조)
         좌·우 창: 위 제목란 (다른 상자 머리와 같은 높이, 왼쪽 제목: "기준·대상 · 줄 N - 파일 이름" (DataCompare 처럼 좌 기준·우 대상, 줄 N: 소트 제목과 같음, 상자 머리 짧은 하이픈, 예: 기준 · 줄 11 - Huns.txt, 붙여넣기·직접 입력: 기준·대상 · 줄 N, 비움: 줄 0, 입력마다 갱신), 오른쪽 끝 찾기 버튼 (폴더 아이콘): 파일 고름 (마지막 폴더 기억: 지원 브라우저·localhost·https → 파일 선택 창 id HMXUtilDiff, 그 외: 브라우저 기본)) + 편집 칸 (제목란 바로 밑 ~ 창 끝, 안쪽 상자 테두리 없음)
         편집 칸 (고정폭 글꼴·줄바꿈 없음·맞춤법 검사 없음, 왼쪽 줄 번호 칸: DataCompare 줄 번호 모양 (폭 4.5em·오른쪽 맞춤·흐린 글자·연한 바탕·오른쪽 선), 글자: DataCompare 차이 보기와 같음 (0.8125rem 고정폭), 세로 스크롤 같이): 찾기·파일 끌어 놓기 (창에 놓음: 그 파일, UTF-8·앞 BOM 뺌)·직접 입력·Ctrl+A·C·X·V (입력 바뀜: 자동 비교 안 함)
         비교 (실행 버튼, Util.RunDiff, DataCompare 와 같은 형식, 보기 따로 없음: 편집 칸 그대로): 좌·우 줄 비교 → 편집 칸에 반대쪽에만 있는 줄 자리 빈 줄 넣음 (회색·줄 번호 없음) + 편집 칸 뒤 줄 색 덧입힘 (#UtilDiff{Left|Right}Back: 바뀐 줄 하늘색, 줄 안 다른 부분 노랑, 지금 차이 줄 번호 진한 파랑 (회색 빈 줄도)), 세로 스크롤 좌·우 같이·가로 따로, 줄 번호 왼쪽 고정
            편집 (비교 중도 기본 편집 칸 그대로: 끌어 고름·여러 줄 지움·Enter·붙여넣기·Ctrl+Z·Y): 바뀐 곳만 줄 색 맞춤 (앞뒤 같은 줄 빼고 그 자리 줄: 앞 색 순서대로, 늘어난 줄: 새 줄, 아래 줄: 색 따라감, 좌·우 줄 맞춤: 다시 실행 때), Ctrl+Z·Y: 그때 줄 색 그대로 (고침 전 줄 정보 기억: 최근 X개·줄 정보 합 X줄까지, 글 사본 안 둠 = 길이·해시 지문, 비교 풂: 기억 버림), 색 기억 없는 글 (실행·저장 전 글 등): 그 글로 다시 비교 (실행과 같음, 보던 자리·고치던 창 커서 줄 그대로, 네비게이션 기억 안 바꿈), 다시 그림: 바뀐 줄만 (줄 수 바뀜: 아래 줄 번호만 고침), 고친 줄: 줄 번호 진한 초록 + 흰 숫자·줄 뒤 연한 초록 (되돌림: 원래 색), 줄 지움 (회색 빈 줄만 지움 빼고): 지운 자리 다음 줄 (없음: 앞 줄) 고친 줄 색 + 지운 차이 이어받음 (이 창에 그 차이 줄 없음) + 반대쪽 그 자리 회색 빈 줄 뺌 (줄 맞춤 유지, 그 차이 줄 없어짐: 그 자리 줄 하늘색 + 차이 이어받음 → 이동·지금 차이 표시), Ctrl+Z·Y: 반대쪽 회색 빈 줄도 그때대로 (반대쪽 실제 글 같음·지문 맞음만), 회색 빈 줄에 씀: 그 자리 새 줄 (줄 번호 붙음), 줄 번호: 회색 빈 줄 빼고 바로 다시 매김, 복사·잘라내기: 회색 빈 줄 뺌, 저장·실행·제목 줄 수·스냅샷: 회색 빈 줄 뺀 글, 모두 고름 (Ctrl+A) → Backspace·Delete: 그 창 글 모두 지움 (파일 연결 끊음 → 새로 시작)·Ctrl+V: 붙여넣은 글로 통째로 바꿈 (연 파일: 그대로 유지 → 제목 이름·저장: 그 파일에 덮어씀, 파일 없음: 이름 복사), 둘 다·다 지움: 비교 모두 풂 (좌·우 회색 빈 줄 뺌·색·지금 차이·차이 이동 → 실행: 다시 비교, Ctrl+Z: 그 전 글·비교 그대로 돌아옴, Ctrl+Y: 다시 풂), 고른 빈 줄: 줄 처음 한 칸 진한 파랑 (끌기·Ctrl+A, 고름 바뀜: 한 화면 틀에 1번·그린 막대만 지움, 편집 칸 고름 색 (진한 파랑·흰 글자)과 같음, 포커스 없음: 숨김, 소트 좌 창도), 실행·저장: 다시 비교 (초록 지움, 보던 자리 아래 첫 차이로), 파일 열기·끌어 놓기: 비교 풂 (회색 빈 줄 뺌), 저장 버튼 (공통 savebtn, 찾기 버튼 오른쪽): 그 창 글 있음·연 파일일 때 켬 (연 파일: 통째로 바꿈 (Ctrl+A + Ctrl+V)·고침에도 유지, 다 지움 (편집 칸·보기·Ctrl+A): 파일 연결 끊음 → 새로 시작 (이름 없음·저장 꺼짐)), 누름: 연 파일 글 → 연 파일에 덮어씀 (찾기·끌어 놓기 파일, 같은 앞 BOM·줄 끝 CRLF·LF, 덮어쓸 곳 없음: 저장 창 같은 이름), 파일 없음 (붙여넣기·직접 입력) → 저장 창: 저장할 폴더·이름 고름 (고른 파일: 비우고 새로 씀, 기본 이름 기준.txt·대상.txt, UTF-8·CRLF, 마지막 폴더 기억, 저장 뒤: 그 파일 연 것과 같음 → 제목 이름·다음 저장 덮어쓰기), 쓰기 허용 창 (변경 내용 저장) 취소: 저장 안 함 (다운·토스트·다시 비교 없음), http 주소 등 저장 창 없음: 그 이름 다운 (브라우저 다운 폴더) → 토스트 → 실행 (다시 비교), 한쪽 비움: 비교 안 함 (편집 칸 그대로), 끌어 놓는 중: 창 존 색 테두리 (높이: 브라우저 창 아래 끝에 맞춤), 처음: 반반, 가운데 바 끌기: 좌 창 폭 (각각 최소 X px), 좌·우 창 높이 따로 맞춤 (서로 늘리지 않음: 위 맞춤), 좁은 화면도 좌우 그대로, F5 리셋: 반반·좌·우 글·제목 비움
      Tab 키: 다음 탭 (맨 끝: 처음, 힌트 팝업: 다음 힌트 탭, 입력칸·다른 팝업 안: 기본 포커스 이동)
      소트 (JSON 펼치기, 디프와 같은 틀): 1번 창 하위 탭 오른쪽 같은 줄 = 오른쪽 네비게이션 ← N → · 펴기 · 접기 · 실행 버튼 #UtilSortRun (엔터 아이콘, 누름: 네비게이션 기억 (새 입력: 새로, 그 외: 지금 기억 글만) → 펼침, 자동 펼침 안 함)
         → 아래 좌 창 #UtilSortLeft · 가운데 바 #UtilSortSplitter (끌기: 좌 창 폭, 디프와 같은 공통 OnSplitterDown·.utilsplit) · 우 창 #UtilSortRight, 좌 창 제목 오른쪽: 찾기·저장 버튼 (디프와 같은 openbtn·savebtn) + 파일 끌어 놓기, 창 높이: 브라우저 창 아래 끝
         좌 창: JSON 편집 칸 (줄 번호, 붙여넣기·직접 입력·Ctrl+A·C·X·V, 어떤 형식이든: 한 줄 압축·다른 들여쓰기·주석 // /* */), 제목: 기준 · 줄 수 + 요소별 개수 요약 - 파일 이름 (입력마다: 한 화면 틀에 1번, 비움: 줄 0), 찾기·끌어 놓기: 파일 → 좌 창 글 (UTF-8, 앞 BOM 뺌, 자동 펼침 안 함, 마지막 폴더 기억), 저장: 연 파일에 덮어씀 (같은 BOM·줄 끝), 파일 없음: 저장 창 → 고른 파일 비우고 새로 씀 (기본 기준.json, UTF-8·CRLF, 저장 뒤: 그 파일 연 것과 같음), http 주소 등 저장 창 없음: 다운, 다 지움: 파일 연결 끊음 → 새로 시작 + 우 창도 비움 (Ctrl+A + Ctrl+V: 유지), 새 창 스냅샷: 파일 이름만 (저장: 저장 창)
         우 창: 비주얼 스튜디오 2026 JSON 기본 열리는 형식 (2칸 들여쓰기, "키": 값, 객체·객체 든 배열: 줄마다, 값만 든 배열: 한 줄 [ a, b ], 빈 {}·[]: 한 줄, 빈 줄·키 순서·값 글자·주석: 입력 그대로, 틀린 JSON: 토큰 그대로 들여쓰기만), 틀린 JSON: 실행 때 틀린 줄 배경 연한 빨강 (디프 바뀐 줄 하늘색처럼, 틀린 곳 모두: 큰따옴표 글·숫자·true·false·null 만 값, 끝 쉼표: 그 쉼표 줄, 안 닫힘: 여는 줄, 접힘: 안쪽 틀림 → 여는 줄 빨강, 주석: 허용)
            빈 줄: 복사해도 빈 줄 유지 (Ctrl+A·C → 붙여넣기 줄 그대로), 줄 번호 + 접기 표시 (여러 줄 { }·[ ] 여는 줄 ▾, 누름: 닫는 줄까지 접음 → "키": {…}, 한 줄 (▸·… 누름: 펼침, 안쪽 접힘: 그대로)), 제목: 대상 · 줄 수 + 요소별 개수 요약 (객체·배열·키·문자열·숫자·불·null 중 있는 것만, 예: 대상 · 줄 36 · 객체 3 · 배열 1 · 키 27)
         펴기·접기 (아이콘만, 정사각, 툴팁: 이름, 접을 곳 없음: 꺼짐): 펴기 (바깥 화살표): 접은 { }·[ ] 모두 펼침, 접기 (안쪽 화살표): 최상위 { }·[ ] 빼고 모두 접음 (안쪽도: 바깥 펼쳐도 안쪽 접힌 그대로)
         네비게이션 (디프와 같음): 새 입력 (리셋 (다 지움·F5) 뒤 입력·파일 열기) 마다 다음 실행에 1개 (그 입력 고침·실행: 지금 기억 글만), 최근 10개 (넘음: 가장 오래된 것 버림, 지금 기억과 같은 글: 안 넣음, 비움: 안 함, 브라우저 메모리만), 0: 마지막 실행, ← -1·→ +1·가운데 숫자 입력 → 그 입력 그대로 펼침, F5 리셋: 좌·우 비움
   ### 새로 고침 버튼 (Octicons sync, 정사각, 아이콘 회색): 제목란 맨 왼쪽 (제목 앞), 누름: 지금 탭 리셋 (F5 와 같음: 최초 입장 상태, 화살표 1바퀴 (F5 도), 탭 없는 툴: 전체 새로 고침)
      F5·새로 고침 버튼: 최초 입장 리셋 (지금 탭을 최초 입장 상태로, 탭마다 동작 다름)
         명령 창 1줄 왼쪽 인스턴스 값 (M 전체·D 1일·S·E 세션 최초 방문 시각 기준·지금 탭 타이머 끔·기본 X초) + 검색칸·쿼리칸 비움 + 결과 메모리 (단계·보관 결과·막대·렌즈 스냅샷·필드 통계) 버림 + 스텝 ← 0 →·포지션 « 0 » (서치 2번 창 보기 비움), 실행 중: 중단 뒤 끝날 때까지 (서치 중지 안 됨: X초마다 다시), 타이머: 먼저 끔, 리셋 중 다시 누름: 무시
         쿼리 탭: 최초 입장처럼 실행까지, 서치 탭: 빈 첫 화면 (실행 안 함), 그대로: 직접 저장한 설정 (저장 쿼리·알람·서치 설정: 머신당·전체 최대 건수·정렬)
   ### 새로 고침 아이콘 버튼 (Octicons sync, 정사각, 있는 툴만): 누름 → 화살표 1바퀴 회전 (X초, 누를 때마다 처음부터, 꺼져도 끝까지)
      상자 탭 줄 (예: 운영 오른쪽 동접·차트·모니터): 탭 버튼 모두 왼쪽부터 → 새로 고침 아이콘: 오른쪽 끝 (margin-left:auto, 탭 사이 안 둠)
      상자 탭 버튼 (운영 동접·차트·모니터·유틸 타임·디프·소트·반영 페이지 탭): 같은 모양·크기 (높이 28px·글자 0.75rem·테두리·바탕·그림자, 지금 탭: 존 색), 탭 줄: 다른 상자 머리와 같은 높이·바탕 (위아래 8px·연한 바탕·아래 선)
   ### 힌트 버튼: 탭 전환 버튼 오른쪽 끝, 글자 존 색, 누름: 힌트 팝업 (위 힌트 탭: 공통·검색·쿼리·서치·운영·반영, 처음: 공통, 그 뒤: 마지막에 본 탭 (localStorage HMX.HelpTab), 탭마다 단축키 | 명령·검색어 | 검색 규칙 묶음, aMenu 로 숨은 탭: 힌트 탭·줄 숨김 (ApplyMenu, data-tab: 쿼리 단축키 쿼리·서치 둘 다, 서치 단축키 서치만)) 제목 줄, 비슷한 것끼리 구분선, 닫기·Esc, 열림: 맨 위부터 (닫기 버튼 포커스, 공통 ShowHelp), 크기 고정 좌우·상하 X px (모든 탭 같음, 넘침: 표 안 스크롤, 브라우저 창 작음: 표 높이 줄임))
   ### 계정 관리 (공통, 다른 툴 재사용 단위: Auth.cs 통째 + Web.cs 의 AccountDialog·AccountEditDialog 마크업·CSS·ShowAccount*·OnAccountClick 스크립트 + 설정 aAccount → Account.json (m_arrConfigSplit 의 aAccount·sKey·sRand·sToken), 로그인·권한 규칙: 위 "## 로그인")
      #### 계정 버튼: 힌트 버튼 왼쪽, 글자 존 색, 관리 계정만 보임 (다른 계정: 숨김·/api/account 403, 로그인 안 씀: 보임), 관리 계정 로그인: 모든 메뉴 보임 (body data-ui="account" UI 숨김 CSS: 남겨 둠, 지금 안 씀), 누름: 계정 팝업
      #### 계정 팝업 (#AccountDialog): 크기 고정 400 × 550px (세로 flex: 제목 → 목록 상자 (내용 높이, 넘침: 상자 안 스크롤) → 페이지 번호 (목록 바로 아래) → 버튼 (맨 아래 고정, 남는 높이는 그 사이)), 목록: 관리 계정 맨 위 고정 (모든 쪽, "관리" 글자) + 나머지 아이디 오름차순 10개씩, 줄: 아이디 | 수정·삭제 (관리 계정 로그인만 버튼, 다른 계정: 목록만·서버도 막음), 지금 로그인 줄 강조, 더블클릭: 수정, 페이지 번호 버튼: 상자 아래 (1쪽: 자리만 두고 안 보임 → 크기 고정, 열 때 1쪽, 추가·수정·삭제 뒤 그 쪽 유지), 오른쪽 아래 추가 (관리 계정만) · 닫기 (autofocus), 로그아웃: 계정 버튼 → 새로 고침: 로그인 페이지 (bAuth false: 로그인 안 함·로그아웃 숨김)
      #### 계정 추가·수정·삭제 창 (#AccountEditDialog, 폭 480px): 아이디 · 비번 (수정 빈칸: 그대로) · 메일 · 전화 · 메뉴 (탭 체크, 없음: 전체) · 외부 (외부 API 권한 체크: 쿼리·서치·로그, 없음: 못 씀) · 키·토큰 (수정만: 새 키 버튼 → 관리 비번 + 적용 때만 저장) + 관리 비번 (관리 계정 비밀번호, 불일치: X초 지연) → 저장·취소 (삭제: 대상 + 관리 비번만), 저장·삭제 뒤: 목록 다시 (그 쪽 유지)
      #### 계정 API: GET /api/account → {sError, bAuth, sID (지금 로그인), bAdmin, arrTab, arrApi, arrAccount [{sID, sMail, sPhone, aMenu, aAPI, bMain, sKey, sToken}]} (관리 계정만, 비밀번호 없음), POST /api/account {sOldID ("": 추가), sID, sPass, sMail, sPhone, aMenu, aAPI, sAdmin}, POST /api/account/delete {sID, sAdmin}, POST /api/account/key {sID, sKey, sAdmin}, POST /api/logout
      #### 로그인 연속 실패 (공통): 같은 IP 웹 로그인 연속 실패 (아이디·비밀번호 불일치만, 접근 메뉴 없음·외부 API 토큰: 안 셈, 마스터 키 전: 관리 계정만 처리·그 외 공통 오류 (셈 안 함), 성공: 다시 셈) 3번 (m_nLoginFailMax) → 그동안 시도한 계정 (aAccount, 대소문자까지 같은 아이디만, 관리 계정·없는 아이디 제외) 비밀번호 최대 글자 (100자) 랜덤 (계정마다 1번, Account.json sCode·sToken → 그 계정 쿠키·외부 API 토큰 무효, 풀기: 관리 계정이 수정 창에서 새 비밀번호), 5번 (m_nLoginBlockMax) → 그 IP 를 Json/IP.json aBlackWeb 에 자동 등록 (루프백·모름: 안 함 → 경고 로그, 풀기: IP.json 에서 지움), 관리 계정이 계정 비밀번호 바꿈: 그 아이디를 시도한 IP 연속 실패 지움 + aBlackWeb 에서 sID 에 그 아이디 있는 IP 풀기 (수동 sID 빈칸: 그대로), 경고 로그 (IP·바꾼 아이디·등록)
      #### HTTPS 운영 (공통, bHttps): bHttps true + IIS 실행 (HttpPlatformHandler, 콘솔·서비스: 해당 없음 → http 그대로): 로그인 쿠키 Secure (https 로만), 모든 응답 HSTS 헤더 (max-age 1년), 알람 링크 https (sUrl 없음: 내부 IP + 첫 요청 포트, 기본 포트 443 생략), IIS 바인딩: https 만 (http 바인딩 없음, 인증서), IIS → Server.exe: localhost http
      #### 접속 IP 목록 (공통, IPList.cs + Web.UseIPList·TCPServer.AcceptLoop): Json/IP.json {"aWhiteTCP": [{"sIP", "sID"}], "aBlackWeb": [{"sIP", "sID"}]} (항목: sIP = IP 패턴, sID = aWhiteTCP: 구분용 메모 (검사 안 함)·aBlackWeb: 막힌 계정 (자동 등록, 계정 비밀번호 바뀜: 그 IP 풀림)·수동: "" (IP 만), 예전 "IP" 문자열도 됨): aWhiteTCP = 클라 TCP 접속 허용 IP (있는 IP 만 접속, 비었음·파일 없음: 모두 거절 → 경고 로그, 바뀜: 새 접속부터), aBlackWeb = 웹 막을 IP (모든 웹 요청 맨 앞: 페이지·API·외부 API·로그인 → 403 Access Denied), 서로 따로 (aWhiteTCP: 웹 대상 아님, aBlackWeb: TCP 대상 아님), 값: IPv4 네 칸 숫자·'*' (예: 10.0.0.1, 192.168.*.*, 127.*.*.*, *.*.*.*), IPv6 그대로 (예: ::1), "*": 모두, 틀린 값: 시작 때 오류 → 종료 (실행 중: 경고 후 뺌), 형식 오류: 앞 목록 그대로, 파일 바뀜: 2초 안 반영 (재시작 안 함), 막음 로그 (같은 IP 1분에 1번, 외부 API 토큰 ****), 접속 IP: 웹 Web.GetAddr (IIS: X-Forwarded-For)·TCP 상대 주소, 자동 등록: 로그인 연속 실패 5번 → aBlackWeb 끝에 {sIP, sID: 그동안 시도한 아이디 (',' 구분)} 추가 (서버가 파일 다시 씀: 주석 사라짐)
      #### 다른 툴 적용: 탭 없는 툴은 메뉴·외부 칸 빼고 아이디·비번·메일·전화만 (서버 검사 그대로), 제목란 계정 버튼 자리·존 색·팝업 크기 같게 (툴마다 다르게 안 함)

## 명령 창 (기준: HMX 서치·쿼리 명령 창, 다른 툴: 같은 자리·순서)
   ### 명령 창 2줄 고정: 1줄 네비게이션·M·D·S·"이상"·E·"미만"·기능 버튼 (논리 단위: 타이머부터 설정까지 오른쪽 끝 묶음 #FilterTool, 실행·중단 위, 쿼리 모드 타이머·알람·기록·필드·저장·설정 / 서치 모드 타이머·저장·설정, 모드마다 보이는 것만 다름, 창 좁아 1줄에서 내려가면 입력칸 줄 실행·중단 오른쪽으로 옮김, 그래서 입력칸이 E 줄 (종료일 미만) 보다 좁아지면 기능 버튼만 한 줄 (입력칸 위, 오른쪽 끝 → 4줄: M·D·S / E / 기능 버튼 / 입력칸·실행·중단) (PlaceToolButtons: 창 크기·탭 바뀔 때 다시 판단), 다른 툴도 같은 자리)
      실행 버튼: 엔터 아이콘 ↵ (정사각, 아래 "실행·중단 버튼"), 타이머 버튼: 숫자 네 자리 폭 (숫자 폭 같음)
   ### 줄 내려감 공통 규칙 (명령 창 모든 요소: M·D·S·E·기능 버튼): 줄 안 간격 평소 12px (묶음 안 8px), 창이 좁아 요소가 아래 줄로 내려가야 하면 먼저 간격을 TOOL_GAP (6px) 까지 줄여 버팀 (.filter.tight: 줄 전체 한 번에), 그래도 안 들어가면 내려감 → 기능 버튼도 "미만" 과 6px 까지 (흐름 밖 고정), 넓어지면 바로 원래 간격·자리 (PlaceToolButtons 가 매번 원래대로 두고 다시 잼)
   ### 2줄: 검색칸 (나머지 폭 꽉)·실행·중단 (톱니 설정 버튼 없음, 설정: 기능 버튼 맨 오른쪽 → 아래 "설정 팝업")
   ### 명령 창 M → D → S·E: 서치·쿼리 같은 순서, D 고름: E = 지금 (초) + X초, S = E - D (S·E 같은 초, 여유 RANGE_PAD: 지금 초 줄 포함, 실행 때 지금 기준 다시, 하위·← → 다시 실행: 그 단계 기간), S·E 직접 고침: D 해제 (D 표시: 기타), S·E 고르기 제한: S 는 E 보다 늦게·E 는 S 보다 이르게·쿼리 S 는 E 기준 최대 기간 (3년) 보다 이르게 못 고름 (날짜: 달력 흐림, 시·분·초: 같은 날짜·시·분일 때 넘는 값 꺼짐 → 달력처럼 아주 연한 회색, 고를 수 있는 값: 검정, E 24시: 그날 전부), 서치·쿼리 기간·D 따로 (처음: 쿼리·서치 같은 기본 = D 1일, E = 세션 최초 방문 시각 (초) + X초, S = E - 1일 (sessionStorage: 새로 고침·새 창 같음), 탭 바꿈: 그 탭 값 그대로)
      D 목록: 10분·30분·60분·3·6·9시간·1·3·7·15·30·60일
      M·D·S·E: 글자 없음 (입력 UI 만, 이름: 마우스 올림 title)
      S·E: 날짜 (폭: 날짜 글자 + 달력, 사이 좁게)·시·분·초 (00~59), S 뒤 흐린 글자 "이상"·E 뒤 "미만" (S <= Data < E: S 포함, E 미포함), 서치·쿼리 모두 초까지 (D: 지금 초 기준)
   ### 타이머 버튼 (기능 버튼 맨 앞: 쿼리 알람·서치 저장 왼쪽, 서치·쿼리 따로: 탭마다 주기·도는 중 따로, 버튼: 지금 탭 타이머): 숫자만 ("초" 글자 없음), 주기 초 표시 (기본 X초)
      누름 → 초 입력 (정수, 최소 X초 ~ 최대 X초, 포지션 입력과 같음) → Enter: 시작, Esc·밖: 취소
      도는 중: 남은 초 (1초마다 줄어듦, 시각 기준), 글자 존 색, 0: 그 탭 보는 중 → 존 색 1회 반짝 + 그 탭 실행 (실행 중: 건너뜀, 다른 탭·덤프·파일 보는 중: 건너뜀) → 다시, 누름: 중지 (주기 초 표시)
      시작 (Enter): S·E 를 지금 + 간격으로 맞춤 (E = 지금 + X초, S = E - 간격, 간격: D 있음 D, 없음 지금 S·E 길이) → 바로 1회 실행 (반짝) → 타이머
      실행 기간: 실행마다 S·E 를 지난 시간 (마지막 실행부터, 건너뛴 주기 포함) 만큼 함께 밀고 실행 → 앞 기간과 빈틈없이 이어짐 (D 다시 계산 안 함)
      쿼리·서치: 초까지 (쿼리 yyyyMMddHHmmss, 서치 줄 시각 초), 명령 창 표시: 초까지, 사용자가 S·E·D 바꿈: 그 값부터, 서치 검색어 없음: 건너뜀
   ### 네비게이션 버튼 ← N → (맨 앞 M 왼쪽, 스텝 버튼과 같은 붙은 버튼 묶음 btngroup, 화살표: 직선 (스텝 버튼: 휘어진 모양), 가운데 숫자 = 지금 위치 (0: 마지막 실행, 과거로 갈수록 -1·-2…, 없음: 0), 더 갈 실행 없음: 꺼짐): 서치·쿼리 따로
      기억: 실행 버튼 직접 누름·Enter 만 M·D·S·E·입력칸 (처음 입장·F5 리셋·저장 쿼리·Shift 옮겨 실행·Ctrl·Alt 놓기·필드 값 클릭·막대 누름·렌즈: 안 넣음, 이것들은 스텝 버튼 (상자 머리) 쪽), 탭마다 최근 X회 (최대 10, 넘음: 가장 오래된 것 지움, 브라우저 메모리만: 새로 고침 지움), 지금 위치와 같음: 안 넣음, 타이머·← →: 안 넣음
      ←: 한 단계 과거 실행·→: 한 단계 미래 (마지막 실행까지) → 그 기억 M·D·S·E·입력칸 그대로 → 그 S·E 그대로 실행 (D 다시 계산 안 함, 기억 지우지 않음: 위치만), 과거에서 새로 실행: 그 뒤 (→ 쪽) 기억 버리고 붙임
      가운데 숫자 누름: 입력 (예: -3) → Enter: 그 위치 실행 (넘음: 끝, 같은 위치: 안 함, Esc·밖 누름: 취소, 기억 1개 이하: 안 함)
      실행 중: 중단 뒤, 지금 탭 타이머: 끔, 누르는 중 다시 누름: 무시
   ### Enter: 실행 (한글 조합 중 제외), 입력칸 설명 (placeholder) 안 씀, 설정 값: 브라우저 localStorage (기본값 버튼)
   ### 명령 입력칸 (검색어·쿼리): 브라우저 자동완성·맞춤법 검사 끔 (글자마다 처리 → 입력 느림), 입력 이벤트: 가벼운 처리만 (무거운 일: 실행 때)
   ### 쿼리 M: 색인·접속 머신 체크 → 쿼리칸 앞 머신 조건 (1대: Machine:A, 여러 대: (Machine:A OR Machine:B), 나머지 쿼리: AND 로 이음 (여러 단어: 괄호)), 전체: 조건 뺌 (기본 빈칸), 쿼리칸 고침: M 체크 맞춤
   ### 쿼리 ↔ 서치 Shift+클릭: 다른 탭 입력칸에 명령·지금 기간 옮김 (D: 지금 기준 S·E)
      입력칸: 복사만, 실행 버튼·단어·필드 값: 복사 + 실행, Shift+Ctrl·Alt: 새 창·새 탭 (단계도), 빈 명령·검색 중: 안 함
   ### 쿼리 탭 처음 입장: D 1일 전체 쿼리 자동 실행 (새 창 상태 복원: 복원대로)

## 상자 (결과 창)
   ### 창 기본 (공통, 특별히 지정하지 않으면): 창 = 제목란 (연한 회색 상자 머리: 위아래 8px·연한 바탕·아래 선, 버튼 있는 머리와 같은 높이) + 내용, 내용 안 하위 창도 같음 (예: 결과: N건 제목란 + 표 상자), 제목 글자 지정 없음: 제목란만
   ### 창 안 상자·기본 문서 UI (공통): 안쪽 상자 (흰 네모·테두리): 안에 표가 있을 때만 (표 상자), 표 아닌 내용 (트리·문서·코드 보기·편집 칸·JSON 보기 등): 제목란 바로 아래 ~ 창 끝 상자 없음 (디프처럼)
      기본 문서 UI (문서·코드 보기, 예: HMX 디프·소트·파일 보기·서치 줄 보기): 줄 번호 왼쪽 회색 칸 (회색 바탕·흐린 숫자·오른쪽 선, 숫자만, 가로 스크롤해도 고정, 복사 안 됨) + 고정폭 글
      문서 비교 UI (공통: HTB·HMX·DataCompare 같음, 좌 기준 → 우 대상, 예: HMX 디프·DataCompare 차이 보기): 줄 비교 (앞뒤 같은 줄 뺀 가운데 Myers 최소 편집, 편집 X개 넘음: 가운데 통째로 바뀜) → 좌·우 줄 맞춤 보기 (좌·우 같은 줄 수, 세로 스크롤 같이·가로 따로, 줄 번호 칸 왼쪽 고정), 색 (Primer 토큰, :root 정의, 세 툴 같은 값): 바뀐 줄·한쪽에만 있는 줄 하늘색 (--bgColor-accent-muted #ddf4ff, 줄 번호 칸 오른쪽 선부터 칸 끝까지: 흰 틈 없음) · 줄 안 다른 부분 노랑 (mark, --highlight-neutral-bgColor #fff8c5, 앞뒤 같은 글자 뺀 가운데) · 반대쪽에만 있는 줄의 빈 자리 회색 (--bgColor-neutral-muted #818b981f, 줄 번호 없음) · 지금 차이 줄 번호 칸 진한 파랑 + 흰 숫자 (--bgColor-accent-emphasis #0969da · --fgColor-onEmphasis #ffffff, 그 묶음 좌·우 모든 줄, 빈 자리: 번호 없이 파랑, 막대 없음) · 남은 CR 빨간 ␍ (--fgColor-danger) · 고칠 수 있는 비교 (HMX 디프): 고친 줄 줄 번호 칸 진한 초록 + 흰 숫자 (--bgColor-success-emphasis #1f883d) · 글 칸 연한 초록 (--bgColor-success-muted #dafbe1), 줄 지움: 지운 자리 다음 줄 연한 초록 + 반대쪽 그 자리 빈 줄 뺌 (그 차이 줄 없어짐: 그 자리 줄 파랑), 저장·실행: 다시 비교 → 지움, 이전·다음 차이 (∧·∨ 아이콘·Alt+↑·↓): 그 묶음 세로 가운데 (처음·끝: 버튼 꺼짐), 정보: 상태 · 차이 N곳 · 현재 i/N
   ### 좌우 창 (공통, 예: HMX 파일·디프·운영): 이름만 있는 맨 위 창 없음 (맨 위 창: 하위 탭·버튼 있을 때만, 예: HMX 디프·DataCompare 차이 보기) → 좌·우 창 각각 제목란 (그 창 내용, 예: 좌 파일 머신 N대·우 파일 경로·줄 + 포지션 « N ») + 내용, 가운데 세로선 (사이드 바, 끌기: 좌 창 폭, 각각 최소 X px)
      사이드 바 고정 (공통): 브라우저 폭 좁아도 좌·우 창 나란히 그대로 (아래로 내려가지 않음, 사이드 바 숨기지 않음, 넘침: 그 창 안 가로 스크롤)
   ### 상자 머리: 왼쪽 제목 (짧게: 범위 - 종류: 건수·시간만, 예: 60초 - 쿼리 결과, 전체 - 서치 결과), 오른쪽 스텝 버튼 ↶ 단계 ↷ (같은 종류: 같은 자리·모양)
      스텝 버튼 ↶ 단계 ↷ (휘어진 화살표): 가운데 숫자 = 지금 단계 (0: 최초, 제한 없음), 숫자 누름: 입력 → Enter: 그 단계 (Esc: 취소, 공통 EditDepth: 스텝·네비게이션 같이, 네비게이션 숫자: 마지막 0·과거 -1…)
      단계 이동: 그 단계 조건 (입력칸·기간) 복원 + 마지막 본 결과 그대로 (없음: 다시 실행, 실행 중: 중단 후), 조건·결과: 브라우저 메모리 (새로 고침: 사라짐, 서치 결과: 모든 단계 합 최대 건수 상한까지)
      새로 실행: 앞 단계 비움, 되돌아간 뒤 하위 실행: 뒤 단계 비움
   ### 버튼 구분 (모양·기능 따로, 공통): 스텝 버튼 ↶ N ↷ (상자 머리, 들어온 단계, 휘어진 화살표: 큰 원호 + 왼쪽 화살촉, ← 반시계·→ 시계 방향 15° 돌림 (되돌리기·다시 하기)), 네비게이션 버튼 ← N → (명령 창 맨 앞·디프 실행 앞, 실행 버튼 직접 누름·Enter 기억, 직선 화살표), 페이지 버튼 (상자 맨 아래 번호, 결과 페이지), 포지션 버튼 « N » (줄 보기·파일 보기 머리, 읽어 쌓인 줄 안 위치)
      스텝·포지션·렌즈: 붙은 버튼 묶음 (btngroup·chartlens, 사이 틈 없음·경계선 1줄, 바깥 모서리만 둥글게, 가운데 숫자 입력칸도 같은 자리)
   ### 페이지 버튼 (페이지 번호): 상자 맨 아래 가운데, 작게 (높이 20px, 위아래 1px), 지금 페이지 존 색
   ### 상자 높이: 처음·브라우저 창 크기 바뀜: 맨 아래 상자를 창 아래 끝에 맞춤 (페이지 번호까지 한 화면, 페이지 스크롤 없음, 최소 높이)
      목록 많음: 상자 안 스크롤, 위아래 창: 아래 창이 나머지 (사이 바 드래그·위 창 높이 바뀜: 다시 맞춤), 쿼리 2번·서치 2번·덤프·파일 같음, 쿼리·서치 처음 창 위치·크기 같음
      필드 값 새 창: 창 아래 끝까지가 최대, 목록 적음: 목록만큼
   ### 스크롤 바: 처음부터 전체 크기 (안 읽은 줄: 빈 높이 → 끝에서 안 늘어남), 끝 확인 (서버가 덜 줌): 읽은 데까지로 보정
      가까움: 이어 읽기, 멂 (끌기): 그 위치, 최대 줄 넘음: 반대쪽 지움
      모양: GitHub (Chrome 기본) 기준, 세모: 상자 끝·막대와 살짝 띄움, 세모·막대 같은 폭·가운데 맞춤, 막대: 회색 얇은 알약, 표 상자: 칸 이름 줄 밑에서 시작
   ### 창 사이 바: 위아래·좌우 창 사이, 끌어서 크기 (위아래: 합 그대로, 좌우: 왼쪽 폭), 창마다 최소 크기, 마우스 올림·끌기: 존 색, 명령 창·결과 창 사이도 같음
   ### 진행 표시: 상자 가운데 팝업 (로딩 인디케이터 형식 모든 팝업, 상태·경과 초, X초 이상 걸릴 때만 → 빨리 끝남: 안 띄움, 공통 PROGRESS_DELAY_MS), 실행 버튼 경과 초, 중단 버튼 (서버도 멈춤)
   ### 줄 보기 기준 줄 (클릭·검색 줄): 보이는 줄 수의 가운데 (공통 GetCenterAbove: 줄 보기·보기 새 창, 파일 끝 가까움: 아래 여백으로 가운데 유지)
   ### 포지션 버튼 « 포지션 » (공통 ShowPos·ShiftPos·GoPos·EditPos): 서치 2번 창 줄 보기 머리·파일 탭 머리 오른쪽 (파일 보기, 제목 그대로)
      포지션: 기준 줄 (보이는 줄 가운데) 을 읽는 줄 수 단위로, 0: 처음 연 위치 (서치: 클릭한 줄, 파일: 파일 맨 아래 → 위로 -1 -2 …), 스크롤 따라 바뀜
      포지션 범위: 읽어 쌓인 줄 끝까지 (끝 포지션: 끝 줄로 맞춤, 예: 위로 -11 까지 쌓임: -11 = 맨 위), 맨 위·맨 아래: 끝 포지션, 옮긴 뒤 스크롤 전: 옮긴 포지션
      «·»: 지금 포지션 앞·뒤로 스크롤 (범위 끝: 끔), 읽기: 스크롤만 (위·아래에 쌓임, 최대 줄 넘음: 반대쪽 지움)
      가운데 숫자 누름: 입력 (- 포함, 범위로 맞춤) → Enter: 그 포지션, Esc·밖 누름: 취소, 툴팁: 포지션 범위
   ### 파일·원문 보기 글자: 검정
   ### 필드 목록: 기본 필드 밑 연한 구분선, 값 0 인 추가 필드 안 보임

## 표 (모든 표 공통, 기준: HMX 서치·쿼리·덤프 결과 표)
   ### 표 목록 (칸 순서): 서치 결과 ResultTable·쿼리 결과 QueryTable: No.·Machine·Folder·File·Line·Log, 덤프 DumpTable: No.·Machine·Folder·File·Date·Size·Ac., 유틸 타임 UtilTimeTable: No.·KST·Unix·ISO 8601·UTC·Day·Ac.
      필드 값 목록 QueryFieldTable: No.·Value·Count, 운영: 서버 (Zone·Server·URL·StartTime·AuthKey·CDS)·미운영 (Zone·Server·URL)·최대 동접 (Game·Time·Count)·동접 기록 (LogTime·게임별·Total)
      팝업 목록 (칸 이름 줄 없음): 저장·알람·필드 QueryEditTable·기록 AlarmHistoryTable·계정 AccountTable·힌트 HelpTable
   ### 칸 순서 규칙: 번호 → 대상 (Machine → Folder → File) → 위치·값 (Line·Date·Size) → 내용 (Log) → 줄 동작 (Ac.), 칸 이름: 영문 (새 표도 같은 이름·순서)
   ### 모양: 상자 (.scroll: 테두리·둥근 모서리·넘침 스크롤) 안 표 폭 100%, 글자 12px (0.75rem), 칸 여백 4px 8px·왼쪽 맞춤·위 맞춤
      칸 이름 줄: 굵게 (600)·흐린 글자·연한 바탕·스크롤해도 위 고정 (sticky), 숫자 칸 (td.num): 오른쪽 맞춤, 줄바꿈 없음 (td.nowrap), Log 칸 (td.log): 고정폭 글꼴·줄바꿈 없음
      칸 이름 줄: 상자 위에 딱 붙음 (스크롤 틈 없음), 구분선 늘 보임 (경계 손잡이: 옆 2px 존 색), 상자와 겹치는 바깥 선 숨김
   ### 칸 글자 (공통 AddCell·AddFolderCell·AddFileNameCell): No.: 1부터 (쿼리: 최신부터, 페이지 넘어도 이어서), Machine: 머신 이름, Line: 줄 번호, Log: 원문 (검색어 강조), Date: 수정 시각 (KST), Size: KB (탐색기처럼 올림, 툴팁: 바이트)
   ### 파일 (표 File 칸·파일 트리): 아이콘 없음, Shift+클릭: 다운, Ctrl·Alt+클릭: 파일 보기 새 창·새 탭, 클릭: 지금 창 (펼침·보기)
      결과 표 Folder 칸 (서치·쿼리·덤프 공통, AddFolderCell, 칸 순서 No.·Machine·Folder·File…): 대상 폴더 (로그: 날짜 폴더 yyyyMMdd 뺀 폴더 (쿼리 sFolder, 서치: 경로에서), 덤프: 파일 폴더) 의 마지막 두 단계만 (C:\NHNServices\Log1\Server → Log1\Server, 두 단계 이하: 그대로), 전체 폴더: 툴팁
      결과 표 File 칸 글자 (서치·쿼리 공통, AddFileNameCell): 파일명 전체 (확장자 포함, 경로 뗌, 전체 경로: 툴팁)
      Ac. 칸: 줄 동작 있는 표만 (덤프: 다운·압축·삭제, 유틸 타임: 삭제 → 그 줄만), 삭제 아이콘: 덤프와 같음 (qdel), 맨 오른쪽 고정·나머지 너비 (다른 칸: 글자 최대 너비), 아이콘만
   ### 칸 너비: 기본 = 칸마다 글자 최대 너비 (col width 1px), 나머지 너비 칸 하나 (기본: 마지막 칸 (서치·쿼리: Log, 덤프: Ac.), 다른 칸: data-rest 로 지정, GetRestColumn)
      고정 너비 (table.fixed): 처음 경계 드래그 놓을 때 지금 자동 너비 → px 고정 (FixColumn, 넘침: 잘림, Log: …), 더블 클릭: 자동 너비로 (ResetColumn)
   ### 칸 경계 드래그 (모든 표 공통, InitColumnGrip): 칸 오른쪽 경계 손잡이 (6px, 마우스 올림·끌기: 2px SM 존 색), 끄는 중: 세로선만 → 놓을 때 한 번 적용 (큰 표 멈춤 방지)
      최소 1글자 (제목 첫 글자), 최대: 나머지 너비 칸이 1글자 남을 때까지 (차이: 나머지 너비 칸이 흡수), 더블 클릭: 자동 너비, 손잡이 없음: 나머지 너비 칸·마지막 칸
   ### 칸 접기 (모든 표 공통, InitColumnFold: 서치·쿼리·덤프·운영 표): 칸 이름 클릭 → 그 칸 좌우 너비 20px 고정 (FOLD_WIDTH_PX, 모든 칸 같음, 칸 글자 잘림) ↔ 다시 클릭: 기본 크기, 표마다 브라우저 저장 (localStorage HMX.Fold: { 표 id: [칸 번호] })
      기본 크기: 그 칸 글자 최대 너비 (자동 너비와 같음, 경계 드래그 너비 아님, 고정 너비 표: 그 칸만 다시 잼 GetNaturalWidth·다른 칸 드래그 너비 유지), 경계 드래그로 최소 폭 (접은 폭 ≒ 1글자) 된 칸: 클릭 → 기본 크기 (접힘으로 봄)
      칸 접기 제외: 나머지 너비 칸·마지막 칸 (경계 손잡이와 같음), 칸 이름 클릭이 정렬인 표 (필드 값 목록), 펼친 줄·빈 줄 (colspan 칸), 경계 더블 클릭 (자동 너비): 접은 칸 유지
   ### 결과 표 (서치·쿼리) 세로 칸 경계선: 칸 border 대신 각 칸 왼쪽 안쪽 box-shadow inset 1px (배율 125% 등 소수 픽셀 위치에서 border-collapse 경계선이 안 그려지거나 오른쪽 선이 옆 칸 배경에 덮이는 Chromium 문제 회피: 자기 칸 배경 위에 자기 선 → 옆 칸 무관, 제목줄: 아래선과 같이, 첫 칸: 없음 → 상자 테두리와 겹침 방지)
   ### 줄 보기 (서치 결과, 줄 클릭: 2번 창 그 줄 주변 + 강조): 이미 보고 있는 줄 다시 누름 (더블 클릭·칸 고르기 포함): 다시 안 읽음 (2번 창 그대로)
   ### 줄 펼침 (쿼리 결과, 줄 클릭 ↔ 다시 클릭: 접음, Log 칸 단어 Ctrl·Alt·Shift 클릭·더블 클릭·끌어 고르기: 그대로): 아래로 펼침, 표 칸 전체 한 칸 (colspan)
      1줄: 기본 필드 Type·Machine·Folder·File, 2줄: 확장 필드 (받은 순서, 없음: 줄 없음), 마지막: 원문 그대로 (줄바꿈, 쿼리 단어 강조), 값 없는 필드: 뺌
      필드: 이름: 값 (· 구분, 넘침: 항목 단위 줄바꿈), 필드 값 Ctrl·Alt+클릭: 쿼리칸에 필드:값 (File: 필드 아님), 연한 바탕·가로 스크롤해도 보이는 폭에 고정
   ### 빈 결과: 지정한 표만 줄 하나 "결과 없음" (서치·쿼리·덤프 등, 기록 목록: "기록 없음"), 표 칸 전체 (colspan), 칸 접기·세로선 대상 아님, 지정 없는 표: 빈 표 그대로 (예: 유틸 타임)
   ### 줄 수: 쿼리 결과 페이지 1,000줄, 필드 값 목록 페이지 100개 (페이지 버튼: 상자 맨 아래)
   ### Ctrl+A (공통 OnTableSelectAll): 누른 영역만 고름 → 바로 복사 (가장 안쪽 영역: 하위 창 (합친 칸, 예: 펼친 줄 상세): 그 안만, 표: 칸 이름 + 줄, 문서·코드 보기 (줄 보기·파일 보기·소트 결과 등): 그 글만, 줄 번호·접기 표시: 안 들어감, 입력칸: 그 칸만), 누른 곳 없음: 맨 위 표, 팝업·차이 보기 열림: 그 안
   ### 줄 번호 보기 (공통, 예: HMX 디프·소트·파일 보기·서치 줄 보기, DataCompare 차이 보기): 끌어 고르기·Ctrl+A (그 보기 줄 모두) → 복사: 원래 글 그대로 (줄 번호·반대쪽에만 있는 회색 빈 줄·␍ 표시: 뺌, 빈 줄: 그대로, 줄 번호 표 table.lines: 공통 OnLineCopy)
   ### 정렬 표 (필드 값 목록): 칸 이름 클릭 → 내림차순 ▼ → 오름차순 ▲ → 원래 순서 (보이는 페이지만), 칸 접기 없음
   ### 팝업 목록 표 (저장·알람·필드 QueryEditTable): 칸 이름 줄 없음, 줄 아래 연한 선, 제목 칸: 나머지 너비 (넘침: …, 제목 표시 최대 40자 + "...", 전체: 툴팁, EDIT_KEY_SHOW)
      버튼 칸 (수정·삭제): 오른쪽, 드래그: 순서 (놓을 자리 2px 존 색 선), 꺼진 알람: 흐린 글자, 더블 클릭: 실행

## 팝업 (dialog)
   ### 자리: 가로 가운데·세로 화면 위 30% 기준 (모든 팝업 같음), 바깥: 반투명 덮개, Esc: 닫기, 버튼: 오른쪽 아래 (저장·확인 → 취소·닫기), 열림: 첫 입력칸·닫기 버튼 포커스
   ### 크기 (폭 고정: 내용 따라 안 바뀜): 목록 팝업 400px (저장·알람·필드·기록), 계정 400 × 550px, 계정 수정 480px, 설정: 내용 폭 (max-content)
      추가·수정 창 (쿼리·알람·필드): 최소 600px, 기록 내용: 600px, 확인 (삭제·다운·패치): 320 ~ 600px, 힌트: 650 × 750px (넘침: 표 안 스크롤), 반영 등록: 520px
   ### 제목 길이: 저장·알람 제목 입력 최대 100자 (서버 같은 상한), 목록 표시 40자 + "..." (전체: 툴팁)
   ### 확인 팝업: 삭제 (대상 굵게 + 상세, 삭제·취소, Enter: 취소), 다운 (파일명 굵게 + 상세, 예·아니오), Esc: 닫기
   ### 저장 목록 팝업 (기능 버튼: 쿼리 모드 저장·알람·필드, 서치 모드 저장, 톱니 없음): 제목: 누른 버튼 이름 (저장·알람·필드, 탭 없음 → 그 기능만), 제목 줄 + 수정·삭제 (전체 목록, 높이 고정·넘침: 스크롤), 아래 추가·닫기
      더블 클릭: 내용·간격 넣고 실행 (실행 중: 중단 후), 드래그: 순서 (설정 파일 저장)
   ### 쿼리 설정 창 (서치 탭 저장 버튼도 같은 창: 제목 저장·저장 서치 aSearch, 추가 창 "서치 추가"·"입력" 칸, 더블클릭: 검색어·기간 넣고 서치 (서치 중: 중지 → 끝난 뒤 다시), 서버 API /api/search/saved): 누른 버튼 기능 하나만 (저장 버튼: 제목 저장·저장 쿼리 aQuery, 알람 버튼: 제목 알람·알람 aAlarm (서버가 매분 판정·발송), 필드 버튼: 제목 필드·필드 정의 aField (줄: 이름만, 정규식·고정값: 툴팁, 더블클릭: 수정, 저장: 바로 적용·접속 클라 알림), 창 안 바꾸기 없음), 필드 추가 창: 이름·정규식·고정값 (고정값 있음: 정규식 안 씀), 쿼리 추가 창: 제목·쿼리·간격 (분 직접 입력), 알람 추가 창: 제목·쿼리·간격·건수·억제 (한 줄)·메일·SMS (추가 버튼·Enter → 밑 목록, 줄마다 삭제)·훅·사용, 테스트 버튼: 창 값으로 메일·훅 바로 발송 (결과 알림)
   ### 설정 팝업 (기능 버튼 맨 오른쪽 "설정", 모든 툴 공통): 제목 "설정", 지금 탭 항목만 (HMX 서치: 한 머신당 최대·모든 머신 최대·서치 제한 시간·결과 정렬 (시간·머신), 쿼리: 쿼리 제한 시간)
      줄: 이름 → 입력칸 (폭 110px 고정) → 단위·범위 힌트만 (예: 초 (1 ~ 100), 기본값·설명 안 씀), 이름·입력칸 사이 한 칸 (12px), 팝업 폭: 내용 폭, 설명 줄 없음
      오른쪽 위 기본 버튼 (기본값 표시, 저장 눌러야 적용), 아래 저장·취소, 범위 넘음: 범위로 맞춤, 제한 시간: 기본 30초·최대 100초 (IIS 요청 제한 5분 안), 브라우저 localStorage 한 키 (HMX.Limit), 서치 중: 잠김
   ### 설정 팝업 다시 읽기 (모든 툴 공통): 설정 파일 (json 등) 에 저장하는 설정 팝업은 열 때마다 서버가 파일에서 그 항목만 다시 읽음 → 메모리와 다름: 바꾸고 바로 적용 (json 직접 고침 반영, 재시작 안 함, 오류 항목: 바뀔 때만 출력), 파일 없음·형식 오류: 출력 후 메모리 그대로
      저장·수정·삭제·순서: 설정 파일에 씀 (그 키만, 다른 키 유지), 브라우저 localStorage 설정: 대상 아님

## 입력·클릭·단축키
   ### 표 칸 더블 클릭 (공통 OnDateDblClick, 모든 표): 그 칸 첫 글자 ~ 끝 글자 고름 → Ctrl+C 복사 (줄 끌기 표도: 공통 CSS tr[draggable] td 글자 고름 허용, 끌기 그대로, 버튼·입력칸: 브라우저 그대로)
      합친 칸 (하위 줄·상세 등 colspan)·표 밖 글: 단어 단위 (브라우저 기본), 날짜·시각·IP (포트 포함, 예: 155.193.37.233:11207): 한 단어로 고름 + 복사 (아래 날짜 더블 클릭)
   ### 날짜 더블 클릭 (공통 OnDateDblClick, 모든 화면 글): 날짜·시각 (yyyy-MM-dd·yyyy/MM/dd·yyyy.MM.dd + 시:분[:초[.소수][Z·±HH:MM]], 예: 2026-11-10 06:06:59·2026-11-10T06:06:59Z) 전체 고름 + 복사 (한 번에), 입력칸·날짜 아님: 브라우저 그대로
   ### 시·분·초 직접 입력 (공통 InitHourInput, 모든 select.hour: HMX 쿼리·서치 S·E·유틸 타임 KST·HTB Template 현재·S·E 등, 페이지 시작 1번): 선택 위에 숫자 칸 겹침 → 숫자 쓰고 지움 (들어옴: 지금 값 전체 고름 → 바로 덮어쓰기, 입력 중 숫자 그대로 보임), 없는·막힌 값이 되는 숫자: 입력 안 됨 (앞 글자 그대로, 예: 시 30·분 60)
      두 자리 맞음: 바로 반영, Enter·포커스 나감: 한 자리 0 붙임 (예 5 → 05)·없는·막힌 값: 되돌림, Esc: 되돌림, ↑: 다음 값·↓: 앞 값 (막힌 값 건너뜀), 목록: 오른쪽 화살표 누름·Alt+↓ 만
      값 읽기·막힌 값·input·change: 선택 그대로 (코드가 값 바꿈: 숫자 칸도, 직접 고친 값: 직접 고른 것과 같게 input·change → 기간 맞춤 등)
   ### 스텝·네비게이션 숫자 입력 (공통 EditDepth): 가운데 숫자 누름 → 같은 자리·크기 입력칸 → Enter: 그 위치 (넘음: 끝, 같은 위치: 안 함), Esc·밖 누름: 취소, 1개 이하: 안 함 (숫자: 스텝 0 최초 ~, 네비게이션 0 마지막·과거 -1…)
   ### 실행·중단 버튼 (공통 enterbtn·stopbtn): 실행: 한 줄 화살표 엔터 아이콘 (↵), 중단: 빈 네모 (□), 둘 다 존 색 (primary, 중단도 빨강 아님), 정사각 28px, 글자 없음 (이름: title·aria-label)
      실행 중 경과 초: 글자 (N초) → 끝: 다시 아이콘 (글자 비움), 아이콘: CSS mask (currentColor)
   ### 찾기 버튼 (공통 openbtn): 파일·폴더 찾기 = 폴더 아이콘 (파일 트리 폴더와 같은 Octicons·색), 정사각 28px, 글자 없음 (이름: title·aria-label)
   ### 저장 버튼 (공통 savebtn): 파일 저장 = 다운 아이콘 (Octicons download, 찾기 버튼과 같은 색·크기, 꺼짐: 회색), 정사각 28px, 글자 없음 (이름: title·aria-label, 툴팁: "파일 저장"), 찾기 버튼 오른쪽
   ### 단어 Ctrl·Alt·Ctrl+Alt + 클릭 (결과·원문): 입력칸 끝에 AND·OR·NOT 추가, 같은 단어 다시: 빼기, 키 다 뗀 뒤 X초: 실행 (지금 단계의 하위)
      펼친 줄 필드 값: 필드:값, IP·IP:포트·날짜·시간: 한 단어 (쿼리: 원문 그대로 있는 줄만)
   ### UI Ctrl·Alt + 클릭 (버튼·입력칸·드롭 박스·파일명·상자 제목란): Ctrl 새 창 · Alt 새 탭, 지금 창 그대로
      새 창·새 탭: 지금 상태 스냅샷 복사 (공통, 연 창에서 그대로 가져옴): 탭·하위 탭 (유틸 타임·디프·소트, 운영 동접·차트·모니터, 반영 페이지)·머신·입력칸·서치·쿼리 기간·← 단계 →·유틸 (타임 입력·디프 좌·우 글·이름·비교·지금 차이·스크롤·네비게이션 기억·좌 창 폭·소트 입력·펼침·접은 곳·스크롤) + 누른 버튼 실행·입력칸 포커스, 쿼리·서치 결과: 그대로 복사 (결과 없음: 쿼리는 그 단계 다시 쿼리, 서치는 다시 검색 안 함)
      파일명·보기 제목: 파일 보기 (결과 줄: 그 줄 강조), 필드 이름: 값 목록
      Ctrl 새 창 크기·자리: 누른 브라우저와 같음, 여는 때: 클릭 이벤트 끝난 뒤 (Chrome 이 Ctrl 을 보고 뒤 탭으로 여는 것 막음)
   ### 단축키 = 힌트 표 (단축키 → 명령, 단축키 추가·변경: 힌트 팝업 HelpTable 과 이 목록 같이)
      [공통]
         F5 → 지금 탭 리셋 (최초 입장 상태, 브라우저 새로 고침: Ctrl + F5)
         ----
         ? → 힌트
         Esc → 팝업 닫기
         Enter → 실행
         ----
         Tab → 오른쪽 탭 (맨 끝: 처음)
         ----
         Ctrl + 단어 클릭 → 입력 + AND
         Alt + 단어 클릭 → 입력 + OR
         ----
         Ctrl + Alt + 단어 클릭 → 입력 + NOT
         Ctrl · Alt + 같은 단어 클릭 → 입력 제외
         Ctrl · Alt + 필드:값 클릭 → 입력 + 필드:값
         Ctrl · Alt · Ctrl + Alt 놓기 → 입력 1초 후 실행
         Ctrl + A → 누른 표 전체 (칸 이름 포함, 없음: 맨 위 표)
         ----
         Ctrl + UI 클릭 → 새 창에서 실행
         Alt + UI 클릭 → 새 탭에서 실행
         ----
         Step 버튼 클릭 → 숫자 입력 + Enter: 해당 Step 이동
         Pos 버튼 클릭 → 숫자 입력 + Enter: 해당 Pos 이동
         Timer 버튼 클릭 → 초 입력 + Enter: 초마다 실행 반복 (서치·쿼리 따로, 도는 중 클릭: 중지)
         네비게이션 ← N → 클릭 → 한 단계 과거·미래 (가운데 숫자: 그 위치) 실행으로 (M·D·S·E·입력 + 실행, 서치·쿼리 따로 최근 10회, 기억 그대로·위치만)
         ----
         Ctrl + 파일 클릭 → 뷰어
         Shift + 파일 클릭 → 다운
      [쿼리]
         Shift + 입력 클릭 → 서치로 입력·기간 복사
         Shift + 단어·실행 클릭 → 서치로 입력·기간 복사 + 실행
         Shift + Ctrl + 입력·단어·실행 클릭 → 새 창에서 위와 같게
         Shift + Alt + 입력·단어·실행 클릭 → 새 탭에서 위와 같게
      [서치]
         Shift + 입력 클릭 → 쿼리로 입력·기간 복사
         Shift + 단어·실행 클릭 → 쿼리로 입력·기간 복사 + 실행
         Shift + Ctrl + 입력·단어·실행 클릭 → 새 창에서 위와 같게
         Shift + Alt + 입력·단어·실행 클릭 → 새 탭에서 위와 같게
      [관리]
         URL 클릭 → 오른쪽 모니터 창에서 XLO 모니터
      [실행]
         Esc → 줄 메뉴 → 상세 패널 닫기 → 입력칸 포커스 해제
         ↑ · ↓ (머신이름) → 목록 이동 (Enter: 고름, Esc: 목록 닫기)
         ----
         Running → 실행 중 (On,True)
         Starting → 시작 요청 · HMXClient 처리 대기 (On,False)
         Stopping → 정지 요청 · 종료 대기 (Off,False)
         Stopped → 정지 (Off,True · Down,True)
         Failed → 실행 실패 · 에러 로그 확인 (PID 음수)
         Downloading → Git 갱신 중 · 프로세스 미실행 (Down,False)
         ----
         ⚠ 커밋 → 커밋 불일치: 같은 중분류 다수와 다른 커밋
         ----
         ▶ 시작 → On · 대분류: 위 그룹부터 (그룹 모두 Running → 다음, 실패: 중단)
         ■ 정지 → Off · 대분류: 아래 그룹부터
         ↓ 다운 → Down · Git 갱신만 (대상에 실행 중 있음: 전체 안 함)
         ----
         ▶ 초록 → 전부 실행 중 (누름: 이미 선택된 값)
         ▶ 연노랑 → 일부 실행 중 (대분류·중분류)
         ■ 흰 바탕 → 전부 정지 (누름: 이미 선택된 값)
         ■ 빨강 → 실행 실패 (PID 음수) 포함
         ▶ · ■ 주황 깜빡임 → 적용 대기 (깜빡임, 상태,False)
         ↓ 보라 깜빡임 → 다운 중 (깜빡임, Down,False)

## 글·표시
   ### 오류: "작업 실패: 두 단어" (Server Off·Time Out·Login Need·User Stop 등, 원문 안 씀, 서버 오류 글: 그대로)
   ### 오류·경고·결과·진행 표시: 작업자가 지정한 곳·글만 (지정 없음: 표시 안 함, 임의로 코딩 안 함), 그 표시만을 위해 없는 UI 를 만들거나 늘리지 않음
      예: 입력 줄 아래 오류 줄·안내 글, 빈 표 "결과 없음" 줄, 건수·진행 글 (지정한 글만: 예 유틸 타임 제목 "결과: N건")
   ### 툴팁 (모든 툴 공통): 핵심 명사 1~2개만 (예: 타임·디프·소트 → 시간 변환·파일 비교·제이슨 정렬·알람 → 계정 알림·기록 → 알람 기록·필드 → 필드 관리·저장 → 쿼리 관리·서치 관리·설정 → 설정 관리·D → 기간·스텝·네비게이션 가운데 숫자 → 스텝·네비게이션·← → → 뒤로·앞으로), 정말 중요한 추가 사항만 괄호 () 에 한두 단어 (단축키·포함 여부·단위·시간대·고름, 예: 실행(Enter)·닫기(Esc)·시작 시간(포함)·종료 시간(미포함)·머신(전체, 체크)·UnixTime(초)·요일(KST)·에러 로그(Redis)·키 생성(적용 필수)), 부연 설명·예시·기능 나열·경로 안 붙임 (설명: 힌트 팝업), 값 툴팁: 값 그대로 (예: 1,024 bytes·포지션 1 ~ 100·시봉), 아이콘만 버튼: 이름 (예: 파일 저장·파일 찾기·새로 고침(F5)), 상태 요약 안 넣음 (예: 설정 → 설정 관리, 값: 설정 팝업), 동작 설명 안 씀

## 브라우저 저장 (설정 파일 아님, 브라우저마다 따로)
   ### localStorage (키: 툴 이름 + "." + 기능, 다른 툴: 그 툴 이름 접두사): HMX.Limit (설정 팝업 값), HMX.Fold (표 칸 접기 { 표 id: [칸 번호] }), HMX.UtilTime (유틸 타임 결과 UnixTime 초 배열, 최대 100), HMX.HelpTab (마지막 본 힌트 탭), HMX.ProcessSummary (반영 현황 펼침), HMX.ProcessExpand (반영 펼친 그룹)
   ### sessionStorage: HMXVisitTime (세션 최초 방문 시각: S·E 기본, 새로 고침·새 창 같음)
   ### 브라우저 메모리만 (새로 고침: 사라짐): 단계 ← →·보관 결과·네비게이션 기억
   ### 읽기·쓰기 실패 (사용 불가·깨짐): 기본값으로, 오류는 콘솔만 (화면 동작 그대로)

## 폴더 (모든 툴 공통, 서버·클라)
   ### 이름: 프로그램이 만드는 폴더: 이름 모두 소문자 (기준 폴더 하위 log·dump·data, data 하위 alarm·index·origin·count, 새 폴더도 소문자, 머신·대상 경로·시간 등 값 이름: 그대로), 대문자 폴더 있음 (예전 Log·Data·Dump 등): 시작 때 소문자로 이름만 바꿈 (공통 Global.FixDirCase, 설정 로딩 뒤·폴더 열기 전, 상위 먼저, 내용 그대로, 실패 (사용 중·권한): 경고 → 그대로 씀 (Windows 대소문자 무시)), 배포·소스 폴더 (Json·Source·Tool 등): 그대로

## 차트 (기준: HMX 쿼리 막대)
   ### 쿼리 막대 클릭: 날짜별 → 시간별 → 분별 → 초별 → 1초 (1초: 끝, 그 초 막대 1개 + 그 초 결과, S·E: 그 분), 월별: 그 달 (일봉)
   ### 쿼리 막대 렌즈 (Lens 버튼, 막대 왼쪽 위아래 ↑·스텝·↓, 같은 폭 붙은 버튼): 스텝 0 = 기간 기본 단위 (1분 이하 초봉, 1시간 이하 분봉, 24시간 이하 시봉, 90일 미만 일봉, 넘음: 월봉)
      ↓: 한 단위 작게 (렌즈 숫자 -1, 월 → 일 → 시 → 분 → 초, 초봉: 마지막, 막대 X개 넘는 단위: 꺼짐), ↑: 한 단위 크게 (렌즈 숫자 +1, 같은 기간, 스텝 0: 기준 시각 (지금 기간 마지막) 단위 포함 지난 분봉 60분·시봉 24시간·일봉 그 달 (월봉 막대 클릭과 같음)·월봉 12개월로 기간 넓혀 쿼리 (S·E 맞춤·D 해제·단계 남김, 월봉: 끝)), 더블 클릭: 한 단계만 (두 번째 클릭 무시)
      렌즈 숫자: 넓힐 때마다 +1·↓ -1 (예: 0 → ↑ 1 → ↑ 2 → ↓ 1 → ↓ 0 → ↓ -1 분봉, 내부 스텝·넓힌 깊이 nLensBase)
      스냅샷: 넓힌 단계 스텝 0 ↓ = 넓히기 전 단계, 스텝 0 ↑ = 넓혀 둔 다음 단계 (보관 결과 그대로, 다시 쿼리 안 함), 단계마다 렌즈 스텝·초·분봉 보관 (← →·렌즈 오감: 그대로, 다시 안 가져옴), 스텝 툴팁: 지금 단위 (예: 시봉), 새 결과: 0, 단계 이동: 그 단계 마지막 스텝, 페이지 넘김: 그대로
      기본 단위보다 작은 초·분봉: /api/query/lens 따로 가져옴 (진행 팝업, 실패: 제목 "초봉·분봉 실패"), x축 라벨: 막대 많음 → 정각·자정 기준 X칸마다 (렌즈 자정: 월/일)
      실행 (쿼리 버튼·Enter) 때 계산 중인 렌즈: 끊음 (서버 계산도 멈춤 → 계정 자리 비움 → 새 쿼리 바로, 실패 글 안 씀), 이미 끝난 렌즈: 해당 없음
   ### 쿼리 결과 제목 기간: 막대 단위 (1초: 1초, 초별: 60초, 분별: 60분, 시간별: 24시간, 날짜별: N일), 초별·분별 x축 첫 라벨: 숫자만 (예: 0)

## 목록 관리 화면 (기준: HMX 반영 탭, HMXServer 이식, 충돌: 위 공통 기준 → 탭 구조·버튼·존 색·진행 표시·오류 글·연결 끊김·단축키·dialog)
   ### 구성: 탭 버튼 반영 (운영 뒤) → 반영 창 #ProcessPanel (스크립트 TabProcess.ProcessHTML class Process, 마크업·CSS Web.cs, id·localStorage: Process 접두사, CSS: 반영 범위만) = 버전 불일치 배너 → 현황 (요약 카드·필터 줄) → 서버 목록 상자 + 상세 패널·줄 메뉴·공통 팝업, 토스트: 창 밖 (다른 탭에서도 보임)
      상자 머리: 페이지 탭 | TestOn·TestOff (bTest)·Ver.N (설정 버전, 현황 왼쪽)·현황·로그·에러·등록·펴기·접기
   ### 요청: 상대 주소 api/process?action=... (ToolManager 경유), 인증: HMX 공통 (401 → 새로고침 → 로그인 페이지, 반영 탭 로그인 없음)
   ### 갱신: 처음 반영 탭 열 때 버전·목록 로딩 + X초 폴링 (반영 탭 보일 때만, 첫 로딩·순차 작업 중: 계속), 탭 다시 열기·브라우저 탭 돌아옴: 바로, 응답 바뀜만 다시 그림, 내 변경 성공: 바로 반영
   ### 연결 끊김: HMX 제목 줄 글 그대로 + 작업 버튼 잠금 (흐림·누름 안 됨)·표 흐림, 목록 오류 (Redis 등): 상자 오류 글 (목록 조회 실패: 두 단어·서버 오류 글) + 잠금, 다시 연결: 자동 해제
   ### 서버 목록: Redis 키 = 대분류,중분류,이름 · 값 = 상태,적용,PID,커밋,머신,다운폴더,깃헙폴더,Sync,Mode,실행파일+인자들, 대분류 = 중분류 첫 숫자마다 (이름: 첫 그룹 첫째 값), 숫자 자연 정렬
   ### 현황 버튼: 요약 카드·필터 줄 펼침 ↔ 접기 (선택: 존 색, 처음: 접힘, localStorage HMX.ProcessSummary)
      요약 카드: 대분류마다 (이름 · Running 수 / 전체 · 태그 ✕ Failed·진행 중·⚠ 커밋 불일치·정상·전체 정지), 누름: 그 대분류 페이지 → 대분류 줄로 스크롤
      필터 줄: 검색칸 (키·머신·커밋·실행 명령 부분 일치) · 상태 칩 (전체·Running·진행 중·Stopped·Failed·커밋 불일치 + 개수, 선택: 존 색) · 머신, 필터 중: 맞는 그룹 펼침, 결과 없음: 필터 초기화
   ### 페이지 탭 (상자 머리 맨 앞, 번호 버튼: 오른쪽 버튼과 같은 크기 (높이 28px·글자 0.75rem)·테두리·바탕·그림자): 대분류 번호 100 단위 (100 미만·번호 없음: 0, 100번대: 1, 8800번대: 88), 서버 있는 탭만 번호 순, 누름: 그 탭 목록만 (지금 탭: 존 색, 탭 1개도 보임, 서버 없음: 숨김)
      최초 입장·F5 리셋: 0번 (없음: 첫 탭, 보관 안 함), 필터 중: 모든 탭 맞는 서버 (탭 잠금), 요약 카드·에러 서버 보기: 그 서버 탭
   ### 표: 지금 탭 대분류마다 상자 (같은 간격), 머리줄 작업 ⓘ (힌트 팝업)·이름·상태·머신·PID·커밋, 대분류 줄 연한 존 색 (X만 이상: 진한 회색)·중분류 줄 중립 회색, 서버 줄 선택: 연한 파랑, 상세 패널 서버: 왼쪽 파랑 선
   ### 펼침: 중분류 단위 (대분류 줄: 하위 모두)·펴기·접기, localStorage HMX.ProcessExpand (보관 없음: 문제 그룹 (실패·진행 중·커밋 불일치) 만)
   ### 작업 버튼 (▶ 시작·■ 정지 쌍 + ↓ 다운): 대분류·중분류·서버 줄·상세 패널, 지금 상태 쪽 불 (힌트 표 [실행]), 불 켜진 버튼: 이미 선택된 값 토스트, 서버 진행 중: 잠금
      시작·정지: 대상 = 적용 대기 아님 + 지금 상태와 다름 (적용 대기: 제외), 확인 → 상태,False (뒤 값 유지), 요청 실패: X회 재시도 후 실패 토스트 (X건 + 외 N건)
      대분류 전체: 이름 입력 일치 → 순차 (시작: 위 그룹부터·정지: 아래 그룹부터, 그룹 모두 Running·Stopped → 다음, Off,True 되돌아감·요청 실패: 중단 + 뒤 그룹 건너뜀), 끝: 토스트, 진행 중 페이지 나감: 브라우저 경고
      다운 (Git 갱신만): 대상에 실행 중 (On) 있음: 전체 안 함, 적용 대기: 제외, 대분류: 이름 입력 → Down,False, 확인마다 작업 기록 (actlog)
   ### TestOn·TestOff (bTest true 만): 확인 → 모든 서버 바로 On,True·Off,True,0
   ### 확인 팝업: 대상·그룹·제외 수, 실행 순서 (대분류), 대상 목록 (그룹별), 제외 이유 (X개 + 외 N개), Enter: 실행 (이름 입력: 일치 시)
   ### 줄 메뉴 (⋯): 서버 → 설정 편집·복제하여 등록·상세 보기·로그에서 찾기 | 삭제, 대분류·중분류 → 이름 변경·로그에서 찾기, 바깥 누름·Esc: 닫기
   ### 상세 패널 (오른쪽 서랍): 경로·배지·실패 안내 (에러 로그 보기)·커밋 불일치 안내, 상태·Redis 키·실행 설정·원본 값 (접힘)·복사 버튼, 아래 시작·정지·다운·설정 편집·복제·로그 … 삭제, Esc: 닫기, 서버 사라짐: 닫힘
   ### 등록·편집·복제 (넓은 팝업): 기존 서버 값 가져오기, String (대분류·중분류 자동완성·이름)·실행 위치 (머신이름 콤보·다운폴더·깃헙폴더)·실행 방식 (Sync·Mode·실행파일+인자들)·고급 State, 미리보기
      검사: 저장 때만 (빈칸·같은 키·드라이브 경로·State), 틀린 칸 빨강 + 첫 칸 포커스, 저장 중: 잠금, 편집 키 바뀜: 이름 변경 후 저장
   ### 삭제: HMX 삭제 확인 팝업 (Enter: 취소), 실행 중·적용 대기: 삭제 꺼짐 + Running: 먼저 정지 버튼 (닫힘: 원래대로)
   ### 이름 변경 (대분류 첫째 값·중분류 둘째 값, 앞 공백 유지): 하위 키 미리보기, 한 번 요청으로 일괄
   ### 버전 검증 (등록·수정·삭제·이름 변경): 다름 → 팝업 닫고 노랑 배너 (서버·화면 버전 + 새로고침) → X초 뒤 다시 읽기 + 다시 시도 토스트
   ### 다시 읽기 (저장·삭제·이름 변경·버전 불일치): 반영 탭 데이터만 (버전 + 목록, 펼침·필터 유지) → 토스트, 페이지 새로 고침·다른 탭: 그대로
   ### 로그 팝업 (큰 팝업, 로그 버튼·로그에서 찾기·상세 로그: 검색어): 반영 로그 최신 순 X줄 단위, 기간 칩 (15분·1시간·6시간·오늘·24시간·7일, 기본 1시간)·직접 지정 (날짜·시·분 + 조회), 종류 칩 + 개수, 검색 (지금 페이지), ▲ 더 최신·더 과거 ▼
   ### 에러 팝업 (큰 팝업, 에러 버튼·에러 로그 보기): Redis 에러 리스트 최신 순 X건 단위, 머신·레벨·검색, 같은 레벨·종류·대상·머신 묶음 (누름: 원문), 서버 보기: 필터 초기화·펼침·상세 패널·줄 깜빡임
   ### 토스트: 오른쪽 위 쌓임 (왼쪽 색 줄: 성공·실패·주의·안내), X초 뒤 사라짐 (실패: 더 오래), 팝업 위, 오류 글: 작업 실패: 두 단어·서버 오류 글
   ### 단축키: HMX 공통 + 반영 탭 보일 때 Esc (줄 메뉴 → 상세 패널 → 입력칸 포커스 해제)·머신이름 콤보 ↑↓·Enter·Esc, 줄 동작 (표 줄·상세 패널·줄 메뉴) Ctrl·Alt + 클릭: 원래 동작 (OPEN_SKIP)

## HTB 전용
   ### 기본 페이지 (IndexHtml): 제목 + 힌트 버튼, Template 버튼 (누름: 화면 요소 모음 펼치기·접기, 펼침: 선택 검정), wwwroot/index.html: 제목 + 힌트 버튼·팝업 (IndexHtml 과 같음, 단축키·범례 변경: 같이) + /api/template 불러와 같은 동작
   ### Template: 다른 툴 제작 참고용 화면 요소 모음 (목록·값: 예시), 창 번호: 주석과 같음
      스크립트: 즉시 실행 함수 안 (전역 이름 안 남김 → wwwroot/index.html 에 붙여도 겹침 없음), CSS: wwwroot 기본 CSS 위에서도 같게 (공통 규칙 중복)
   ### 명령 상자: 현재 날짜·시 (서버 KST, 시: 00~23)·상태 ON·OFF (드롭 박스 + 세그먼트 버튼)
      실행·중단·새로 고침 아이콘·팝업 (Test: 예·아니오)·설정 글자 버튼 (실행 시간 X~X초·페이지 줄 수 X~X줄, 기본 X·X, 저장: 6번 창 다시 표시)
      /api/info 링크 버튼 + 존 칩, /api/echo 링크 + 입력칸 (최대 X글자, 입력: 링크 주소 맞춤, Enter: 호출) + 모든 메서드 칩, 정적 파일 안내
   ### 새로 고침: /api/info 다시 → 현재 날짜·시·존 칩·창 사이 바 색·1번 창 (선택 줄 유지, 처음: 첫 줄)
   ### 칸 접기 (InitColumnFold, 위 공통 표 규칙): 1번·6번·8번 창 표 (칸 이름 클릭 → 접기·펼치기, 표마다 브라우저 저장 HTB.Template.Fold, 나머지 너비·마지막 칸 제외)
   ### 1번 창 서버 정보: /api/info 앞 X줄 (No.·Key·Value, 처음 보이는 X줄: 상자 높이, 표 넓음: 가로 스크롤), 줄 누름: 선택 → 2번 창
   ### 2번 창 값 보기: 제목 "번호. 키" + 값, 처음·이전·다음·끝: 선택 줄 이동 (1번 창 선택 줄 보이게 스크롤, 칸 이름 줄에 안 가림)
   ### 1번·2번 창 사이 바: 위로 1번 줄고 2번 늘고 (창마다 최소 X px)
   ### 4번 창 파일 트리 예시 (HMX 파일 탭과 같은 좌우 창, 이름만 있는 맨 위 창 없음): 좌 창 "[예시] 트리" 제목란 + 트리 · 가운데 세로선 · 우 창 제목란 (파일 경로 + 포지션 « N ») + 파일 보기 (창마다 최소 X px), 우 제목 처음: 보기 확장자 (log·txt·json·ini·bat·xml·config·md·lua, HMX 와 같음)
      파일 보기: 줄 번호 + 글, 열면 맨 아래 (포지션 0), 오른쪽 제목 오른쪽 포지션 « N » (X줄 단위, HMX 파일 탭과 같은 자리), 긴 예시 파일 (X줄) 로 포지션 이동
   ### 5번 창 비교 결과 예시 (DataCompare 형식): 제목: 테이블명 + 같음·다름·기준 없음·대상 없음 건수, event_id 칸 고정 폭 (넘침: … + 툴팁 전체 이름), 컬럼마다 결과 칩
      색: 같음 회색·다름 빨강·기준 없음·대상 없음 주황 (굵게), 순서: 앞 컬럼 다름 → 뒤 컬럼 다름 → 같음 → 기준 없음 → 대상 없음 (같은 순위: 원래 순서)
      다른 줄 누름: 아래에 id · 컬럼별 결과 (마우스 올림: 연파랑)
   ### 6번 창 서버 목록 예시: Zone 칩·Server·State 칩·Users, 운영 서버만 페이지 (줄 수: 설정), 서버 번호 100 단위 바뀜: 사이 살짝 띄움, 미운영: 접기 (건수, 회색 줄·회색 칩)
      실행: 설정 시간 뒤 첫 페이지 (실제 툴: API 호출 + 중단 신호), 중단: "실행 실패: User Stop", 오류 버튼: 예시 오류 상자 ("조회 실패: Server Error") 보이기·숨기기
   ### 7번 창 탭: 칩 (존 Dev·Alpha·Real, 서버 패치 전 빨강·패치 후 초록·미운영 회색 (툴팁: 패치 전·후), 기타 등록 초록·다름 빨강 테두리·없음 진회색 바탕 + 흰 글자)·강조 (검색어 입력마다 다시 표시)·범례 (HMX 힌트 팝업 범례: 상태 배지·커밋 불일치·작업 버튼·버튼 불빛 | 뜻 | 값)
   ### 5번·6번·7번 창: 나란히 (좁은 화면: 한 줄씩)
   ### 8번 창 기간·타이머 명령 창 (HMX 공통, 5번·6번·7번 창 아래 한 줄 전체): HMX 명령 창과 같은 모양·이름·동작, /api/sample 호출
      명령 창 2줄 고정: 1줄 = 네비게이션·D·S·"<= Data <"·E·타이머 + 오른쪽 끝 저장 (팝업 제목 저장, 예시 목록 → 누름: 검색칸 + 실행), 2줄 = 검색칸 (나머지 폭 꽉, Enter: 실행)·실행·중단
      D: X분 ~ X일 (맨 앞 숨긴 기타), D 고름: E = 지금 (초) + X초, S = E - D, S·E 직접 고침: D 해제 (기타), 처음: D 1일 + E = 페이지 처음 연 시각 + X초 → 실행
      실행: D 있음 → 지금까지로 다시 → S·E (초까지 yyyyMMddHHmmss)·검색어로 조회, 조회 중: 실행 꺼짐·중단 켬, 중단·실패: "조회 실패: 두 단어" + 이전 결과 유지
      네비게이션 ← N → (명령 창 맨 앞, HMX 와 같음): 실행 버튼 직접 누름·Enter 만 D·S·E·검색칸 기억 (0: 마지막 실행, 최근 10회, 처음·저장 목록·타이머·막대·렌즈: 안 넣음), ← -1·→ +1·가운데 숫자 입력 → 그 조건 그대로 조회 (D 다시 계산 안 함, 조회 중: 중단 뒤, 타이머: 끔)
      타이머: 기본 X초 (X ~ X초), 시작: S·E 지금 + 간격 → 바로 1회 → 0마다 지난 시간만큼 밀고 실행
      스텝 ← N → (결과 머리 오른쪽): 실행 (버튼·Enter·저장 목록·타이머·막대 누름·렌즈 넓힘) 성공마다 단계 1개 (검색어·S·E·D·결과·렌즈 스냅샷), ← →·숫자 입력: 그 단계 조건 복원 + 결과 그대로 (다시 조회 안 함)
      막대 (명령 창 아래, 오류 상자 다음): 막대 차트 + 렌즈 (위 공통 규칙, /api/sample 시간·분·초별 건수, 렌즈 초·분봉: /api/sample/lens)
      결과: 제목 "조회 결과: X건 (최신 X건) · 시간: X ms" (숫자 존 색), 긴 목록 상자 (처음 결과 X줄 + 바로 아래 크기 조절 바 (끌기: 최대 높이, 최소 X px, 마우스 올림·끌기: SM 존 색), 세로 바 있을 때만 바 자리), No.·Time·Text (검색어 강조), 줄 누름: 선택, Ctrl·Alt+클릭: 그 줄 1초 API 새 창·새 탭
   ### /api/sample: 지금부터 과거 X일 X초 간격 예시 줄 중 S <= 시각 < E · 문자열에 sKey 포함 (대소문자 무시, 빈칸: 전체) 줄 최신 순 최대 X건 + 막대 건수 → { nTotal, nElapsedMs, sError, arrLine: [{ sTime, sText }], arrHour: [{ nHour, nCount }] (0건 포함), arrMinute: [{ nMinute, nCount }] (기간 1시간 이하), arrSecond: [{ nSecond, nCount }] (기간 1분 이하) }
   ### /api/sample/lens: 조회와 같은 줄 → nSec 1: 초·그 외: 분 칸마다 건수 (기간 첫 칸 정각부터 0건 포함, 최대 X칸 넘음: sError "Over Range") → { sError, nElapsedMs, arrTime: [{ nTime, nCount }] } (HMX /api/query/lens 와 같음)
      기간: nStart·nEnd = yyyyMMddHHmm·yyyyMMddHHmmss (TryGetMinuteRange, 형식 오류·시작 > 끝: sError "Bad Range", 최대 X일 넘음: "Over Range")

# Client
## Client - 로그 감시·실행 (배포·온오프) 클라 (.NET 10 file-based app, 윈도우 서비스 지원)
## 실행: dotnet run --file Source\Client.cs (Client 폴더에서, 상위 폴더 Client.csproj 와 따로: --file 필수)
## 인자: -install (서비스 HMXClient 등록·시작, 관리자 권한, 게시된 Client.exe 로 실행) | -start·-stop (서비스 시작·정지, X초 대기, 관리자 권한) | -uninstall (서비스 정지·제거, 관리자 권한) | 그 외·없음 (클라 실행: 콘솔 Ctrl+C 종료, 서비스로 실행되면 SCM 연동)
## 디버깅: HMX.slnx (Visual Studio 2026) F5
## 구성: Client.cs (Main: 진입점·서비스 등록·공통·기능 연결) + #:include → 공통 ../../Common (Global 전역 함수·Config 설정·Log 로그·AES 복호화·Count 받은 진행·Define 상수·데이터 정의, Server 와 같이 씀, 프로젝트별 값: Main) → Timer(타이머) → TCPClient(Server TCP 접속) → Tab(탭 공통: 경로·블록 읽기) → TabQuery(쿼리 탭: 로그 감시)·TabSearch(서치 탭·SearchNode)·TabDump(덤프 탭)·TabFile(파일 탭) → Redis(Redis 연결) → Process(실행 API: 실행·확인·종료, ProcessValue 값 파트·ProcessLog 실행 로그·ProcessGit Git 동기화) → TabProcess(반영 탭: 주기 처리) → Clean(날짜 폴더 자동 삭제: aDeleteFolder)
## 설정 파일: 기준 폴더 하위 Json\Client.json (서비스: exe 폴더, 예전 위치 기준 폴더에만 있음: 그 파일 + 경고), Config.GetString("sIP")·GetInt("nPort") 등 (키 없음: 기본값)
   ### bLog : 로그 파일 기록 (false·없음: 콘솔만)
   ### sIP   : Server 아이피 (호스트명 가능)
   ### nPort : Server TCP 포트 (Server.json 의 nPort)
   ### sMachine : 서버에 보낼 머신 이름 (없음: 컴퓨터 이름, 한 PC 여러 클라: 클라마다 다르게 → 서버 머신별 관리 목록·받은 진행 안 겹침)
   ### aLogFolder : 기준 폴더 배열 (쿼리·서치·덤프·파일 공통, 필수, 하위 X단계까지)
      #### 쿼리·서치 대상: 기준 폴더 자신·하위 중 바로 아래 날짜 폴더(yyyyMMdd)를 가진 폴더 (예: Log1\Server·Log2·Log2\Client, 날짜 폴더 안 제외, X초마다 재탐색)
         ##### 쿼리: 대상마다 최신 *yyyyMMdd_HH.log 1개 읽기 전용 열기, 서치: 대상 날짜 폴더의 같은 규칙 파일
      #### 덤프: 하위 *.dmp 감시·목록, 파일: 하위 폴더 보기·다운
   ### aBlockQuery·aBlockSearch·aBlockFile·aBlockDump : 탭별 막는 경로 배열 (전체 경로·폴더 이름·중간 경로, 그 탭에서만 뺌, 다른 탭: 그대로, 없음: 막지 않음, 바꿈: 재시작)
      #### 전체 경로: 그 경로 자신·하위 (예: C:\\NHNServices\\Agent → 앞부분만 같은 C:\\NHNServices\\AgentX: 안 막음)
      #### 폴더 이름·중간 경로: 경로 안 어디든 같은 이름 폴더 자신·하위 (이름 단위 정확히 일치, 예: test → C:\A\test\B 막음, C:\A\test1·MyTest: 안 막음, Test\\S·TEST1/5/5 → 이어진 폴더 이름 일치, 구분자 \·/)
      #### 대소문자 무시, 형식 오류 (빈 값·.·..·C:Log·와일드카드 *?): 경고 후 뺌
      #### aBlockQuery 로 빠진 폴더: 시작 때 로그 감시 상태 (Data\Count) 도 지움
      #### aBlockQuery: 쿼리 감시·전송, aBlockSearch: 서치 대상, aBlockFile: 파일 탭 폴더 보기·다운 (보기: 그대로), aBlockDump: 덤프 감시·목록·다운·삭제
   ### aDeleteFolder : 날짜 폴더 자동 삭제 배열 [{sPath, nDay, nHour}] (aLogFolder 와 무관, 없음·빈 sPath: 꺼짐, 바꿈: 재시작)
      #### sPath : 전체 경로 → 자신·하위 X단계 날짜 폴더(yyyyMMdd, 있는 날짜) 찾음 (날짜 폴더 안·숨김·시스템·정션·심볼릭 링크: 안 들어감·안 지움, 안에 링크 폴더 있는 날짜 폴더: 경고 후 안 지움), 상대 경로·드라이브 루트·형식 오류: 경고 후 뺌, 같은 경로: 첫 항목만·경고
      #### nDay  : 삭제 일수 (최소·기본 X일) → 날짜 ≤ 오늘 - nDay 일 폴더 통째 삭제 (오늘: 안 지움, 최소: 어제 이전)
      #### nHour : 매일 삭제 시각 (0~23, 기본 X시, KST 정각, 시작 때 바로 안 함: 다음 그 시각부터)
      #### 겹친 항목: 하위 항목 경로 자신·하위는 그 항목 규칙만 (예: C:\\NHNServices\\HMXServer\\Data + C:\\NHNServices\\HMXServer\\Data\\Test → Test: Test 항목 nDay·nHour, Data 항목: Test 안 안 들어감, Data\\Test2: Data 규칙, 대소문자 무시)
      #### 폴더마다 Throttle, 삭제 실패 (사용 중 등): 경고 후 다음 폴더 (다음 날 다시), 로그: 지운 폴더 1줄씩 + 항목마다 합계 1줄
   ### aLogType   : 보낼 로그 종류 배열 (줄의 두 번째 [], 예: [2026/10/31 09:07:31.635] [ERROR] 내용 → ERROR)
      #### 첫 [] 원문 시간 없음·형식 다름·없는 날짜·시각 (예: 13월·25시): 보내는 줄만 첫 [] 를 지금 시간 (KST, [yyyy/MM/dd HH:mm:ss.fff]) 으로 바꿔 보냄 (로그 파일 그대로, 끝난 줄 판단: 안 막음)
   ### aField     : 클라 설정 없음 (서버 Field.json aField → 접속 때 Count 답으로 받음, 남은 예전 설정: 무시)
      #### 보낼 줄 필드 [{sKey, sRegex 또는 sValue}] (sValue 있음: 정규식 안 씀·모든 줄 그 값 (예: 존 {"sKey":"zone","sValue":"Dev"}), sRegex: 첫 () 값, 없음: 맞은 부분 → 서버 쿼리 필드, 키: 영문 시작·영문·숫자·_ X자 이하·Machine·Folder·Type 불가, 값: X자 이하, 오류·중복: 제외, 예전 서버 (답에 없음): 필드 없이)
   ### nLogRead   : 파일마다 감시 주기당 최대 읽기 줄 수 (기본 1000, 남은 줄: 다음 주기)
   ### nLogSend   : 파일마다 감시 주기당 최대 전송 줄 수 (aLogType 줄, 기본 100, 차면 그 줄까지만 본 것으로)
   ### sCode : 로딩 시 AES 복호화 → 같은 위치 sPass 로 조회 (예: "DB:Dev:sCode" → GetString("DB:Dev:sPass"))
      #### AES 키: 마스터 키 (메모리만, 서버에서 받음: 첫 접속부터 X초마다 MasterKey 요청 → 받을 때까지 다른 기능 안 함 → 받으면 설정 다시 로딩)
   ### sTitle : 이름 → 로그 줄 레벨 앞 [이름_] 태그 (예: 프로젝트 → [시각] [HMX_][INFO_] 내용, 실행 에러 리스트 줄도 같음, 없음: 태그 없음)
   ### sZone : 존 (예: Dev, 서버 Server.json sZone 과 같게 → 실행 기능 Redis 키 존, 없음: 설정 값만)
   ### Redis : 실행 기능 Redis (서버 Server.json Redis 와 같은 서버, sHost 없음: 실행 기능 꺼짐 → 로그 감시만)
      #### sHost·nPort·nDB·sUser·sCode (→ sPass) : 접속 (비밀번호: 출력 안 함)
      #### sPrefix : 목록 키 앞 (기본 HMX:Process:, 서버 Redis:sProcess 와 같게), sError : 에러 리스트 키 앞 (기본 HMX:Error:, 서버 Redis:sError 와 같게)
      #### 실제 키: 값 + 존 + ":" (예: 목록 HMX:Process:Dev:대분류,중분류,이름, 에러 리스트 HMX:Error:Dev:, 존 없음: 값만)
   ### Git : sUrl 배포 저장소 (기본 HMX_ALPHA), sCode (→ sPass, 저장소 토큰: https://토큰@... 접속, 없음: git 자격 증명, 로그: ****)
## 실행 기능 (TabProcess, OLD HMXClient 이식, 짝: 서버 반영 탭 Process): X초마다 Redis 목록 키 (sPrefix + 존 + ":")* 중 값 머신 파트 = 이 머신 (GetMachine, 대소문자 무시) 항목 → Ready (Git 동기화, 최대 X개 동시) → Start (그룹 순서, 그룹 안 동시: On 실행·Off 종료·Down 다운로드) → 바뀐 값 Redis 저장
   ### 값: 상태(On|Off|Down),적용(True|False),PID,커밋,머신,폴더,깃헙(sparse-checkout 경로),Sync(1: 시작 시 Git 갱신, 0: 생략),Mode(0: 독립 실행·강제 종료, 1: 새 콘솔·Ctrl+C 종료),실행파일+인자 (실행 위치: 폴더\깃헙)
   ### 그룹: 필드 (키에서 목록 키 앞 뺀 이름) 첫번째+두번째 파트 (예: "999, 0_HMX, 0_Common" → "999, 0_HMX"), 앞 그룹이 전부 끝나야 다음 그룹
   ### Git: .git 없음 → sparse 클론 (blob 필터·depth 1), 있음 → shallow 갱신 (fetch depth 1 + reset --hard, 명령·클론 시간 제한, 남은 lock 제거)
   ### PID 오류값: -1 Git 동기화 실패, -3 프로세스 확인 실패, -10 크래시 (실행 중 종료 → 다음 주기 재실행), -11 실행 실패, -12 종료 실패
   ### 로그: 공통 로그 (레벨 앞 sTitle 태그), ERROR·FATAL: Redis 에러 리스트 (sError + 존 + ":") 에도 [머신] 붙여 추가 (X일 유지, 서버 반영 탭 조회), FATAL: X초 대기 (Redis 장애: 대기 후 다음 주기 재시도, 대략 X초마다 Tick Alive)
## 로그 감시 상태 (Count, 서버와 같은 코드): 대상 폴더마다 기준 폴더 하위 Data\Count\머신\대상 폴더 (드라이브 ':' 제거)\Count.data, 줄 "날짜 폴더\파일\t줄 번호" (예: Data\Count\GAME01\C\NHNServices\CenterServer\Count.data → 20261022\xrpc20261022_10.log	617)
   ### 저장: 전송 후·변경 시만 (주기마다 대상당 최대 1번, 파일 열어 둠), 최대 2줄 (윗줄: 이전, 아랫줄: 최신): 1·2번째 줄 이어 씀, 셋째 줄부터: 윗줄 지우고 아랫줄 맨 위로 + 새 줄 둘째 줄 (쓰기 1번 → 남은 뒤 자름)
      예) 10.log 1 / 11.log 2 → 11.log 617 들어옴 → 11.log 2 / 11.log 617
   ### 읽기: 아랫줄 기준, 줄바꿈 없는 마지막 줄 (쓰다 끊김) 버림, 바른 줄 중 맨 밑 줄이 윗줄보다 진행 (파일 날짜·시간, 줄 번호) 작음: 쓰다 끊김 → 윗줄 (예: 10.log 617 / 10.log 6 → 617, 10.log 617 / 11.log 617: 정상 → 아랫줄)
   ### 접속 (Hello 뒤): 관리 목록 (대상 폴더) 한 패킷 → 서버: 목록 밖 서버 Count 지움 + 받은 진행 한 패킷 답 → 답 폴더: 서버 위치부터 보정 (서버 앞섬: 건너뜀, 뒤짐: 다시 보냄), 답 없는 폴더: 아래 시작 규칙대로 → 그 뒤 Log 전송 (X초 답 없음: 보정 없이)
   ### 시작: 본 줄 다음부터 (파일 없음: 다음 시간 파일부터), 상태 없음·새 대상: 최신 파일 끝부터, 뒤에 생긴 대상: 상태 있음 → 이어서, 감시 대상 아닌 폴더 (대상 밖·aBlockQuery) 상태: 지움 (빈 폴더 포함)
   ### 예전 LMClient.data (LMClient.txt) "전체 경로\t줄 번호": 상태 없는 대상만 옮겨 받고 지움
   ### 줄 번호 깨짐 (빈칸·숫자 아님·탭 없음, 바른 줄 없음): 경로 맞으면 그 파일 마지막 줄까지 본 것으로 복구 (파일 없음: 다음 시간 파일 처음부터), 경로 깨짐: 최신 파일 끝부터
   ### 다음 시간 파일 (yyyyMMdd_HH, 날짜 바뀜 포함): 지금 파일 끝까지 본 뒤 이동 (시간 건너뜀 없음, 폴더 훑기는 감시 파일 시간 경과 후 X초마다만)
   ### 서버 접속 중에만 검사·전송 (끊김: 위치 유지, 재접속 시 서버 받은 진행으로 보정 후 이어서), 전송은 일방 (응답 확인 없음, 쓰기 실패: 다음 주기 재시도)
## 전송: TCP 한 줄 = JSON 1건 (UTF-8, '\n' 구분), 끊김: X초부터 2배씩 최대 X초 대기 후 재접속 (실패 로그: 처음 1회 + X회마다)
   ### {"sType":"MasterKey","sMachine":""}	클라 시작 뒤 첫 접속부터 X초마다 (받을 때까지, 따로 접속 → 받으면 닫음) → 서버 {"sType":"MasterKey","sKey":""}
   ### {"sType":"Hello","sMachine":"","sDir":"","nPid":0,"sTime":""}	접속 직후 1회 (sDir: aLogFolder 전체 경로, "|" 구분)
   ### {"sType":"Count","arrFolder":[""],"sTime":""}	Hello 뒤 1회, 관리 목록 한 패킷 (로그 감시 대상 폴더, aLogType 없음: 빈 목록 → 서버 Count 모두 지움)
   ### {"sType":"CountDel","arrFolder":[""],"sTime":""}	감시 폴더 사라짐 (탐색 때 발견, 한 패킷, 답 없음) → 서버 관리 목록에서 뺌·그 폴더 Count 지움 (클라: 감시·상태 파일 닫고 목록에서 뺌, 상태 파일 남김 → 다시 생기면 이어서)
   ### {"sType":"File","sEvent":"Created|Changed|Deleted|Renamed|Error","sPath":"","sOldPath":"","nSize":0,"sMessage":"","sTime":""}
   ### {"sType":"Log","sPath":"","sFolder":"","arrLine":[""],"arrNo":[0],"arrField":[{"키":"값"}],"sTime":""}	로그 감시 (arrNo: 줄마다 파일 줄 번호 (첫 줄 1), arrField: 줄마다 aField 값 (값 있는 키만, sValue: 고정값, aField 없음: null), sFolder: 대상 폴더): 감시 주기마다 시간이 지금 초 미만인 끝난 줄 최대 nLogRead 줄 중 aLogType 줄 최대 nLogSend 줄
   ### {"sType":"Alive","arrFolder":[""],"sTime":""}	X초마다 (전송 중에도, 무전송 Ping 대신): 지금 관리 목록 → 서버: 관리 폴더와 다름만 맞춤 + 답 필드 정의 (예전 서버: 무시, 받은 것으로 침)
## 알림 (서버 → 클라): {"sType":"Pause"} 서버 적재 밀림 (분당 Log 상위 X% 클라에만) → 로그 감시 멈춤 (위치 유지), {"sType":"Resume"} (모든 클라) → 멈춘 상태만 재개·전송 중: 무시, 재접속: 해제
   ### Pause·Resume: 수신 루프가 받은 순서대로 바로 처리 (요청처럼 따로 처리 안 함 → 순서 뒤바뀜 없음)
   ### {"sType":"Count","arrCount":[{"sFolder":"","sRel":"","nLine":0}],"aField":[{"sKey":"","sRegex":"","sValue":""}]} Count 답 한 패킷 (서버 받은 진행 있는 폴더만, aField: 서버 설정 → 이 접속 보낼 줄 필드) → 보정 뒤 Log 전송 시작
   ### {"sType":"Alive","aField":[{"sKey":"","sRegex":"","sValue":""}],"sTime":""} 얼라이브 답·서버 필드 정의 바뀜 (쿼리 필드 버튼, 바로) → 바뀜만 다음 Log 부터 새 줄 필드 (받은 진행 없음: 보정 안 함)
## 요청 (서버 → 클라) / 결과 (클라 → 서버), nReqId 로 짝, sPath: 전체 경로
   ### {"sType":"Search","nReqId":0,"nStart":2026102600,"nEnd":2026102624,"sKey":"","nMax":1000,"nLimit":60}	(nLimit: 마감 초, 없음: 60)
      #### → {"sType":"SearchResult","nReqId":0,"bTruncated":false,"bStopped":false,"nFileCount":0,"nElapsedMs":0,"sError":"","arrLine":[{"sPath":"","nDateHour":0,"nLine":0,"sText":""}]}
      #### → 검색 중 X초마다 {"sType":"SearchProgress","nReqId":0,"nFileTotal":0,"nFileDone":0,"nFound":0,"nElapsedMs":0}	(nFileTotal -1: 앞 검색 대기 중)
   ### {"sType":"Stop","nReqId":0}	검색 중지 → 그때까지 결과 bStopped=true
   ### {"sType":"View","nReqId":0,"sPath":"","nStart":1,"nCount":500}	(nStart 0: 마지막 nCount 줄)
      #### → {"sType":"ViewResult","nReqId":0,"sPath":"","nStart":1,"nTotal":0,"sError":"","arrText":[""]}
   ### {"sType":"Dump","nReqId":0}	덤프 목록 (aLogFolder 하위 *.dmp, 경로 순)
      #### → {"sType":"DumpResult","nReqId":0,"bTruncated":false,"sError":"","arrFile":[{"sPath":"","nSize":0,"sTime":""}]}
   ### {"sType":"DumpRead","nReqId":0,"sPath":"","nOffset":0,"nCount":4194304}	덤프 다운 조각 (최대 X MB, 서버가 zip 저장)
      #### → {"sType":"DumpReadResult","nReqId":0,"sPath":"","nOffset":0,"nSize":0,"sError":"","arrData":"base64"}	(nSize: 지금 파일 크기)
   ### {"sType":"DumpDelete","nReqId":0,"sPath":""}	덤프 삭제
      #### → {"sType":"DumpDeleteResult","nReqId":0,"sPath":"","sError":""}
   ### {"sType":"FileRead","nReqId":0,"sPath":"","nOffset":0,"nCount":4194304}	파일 다운 조각 (최대 X MB, 확장자 제한 없음)
      #### → {"sType":"FileReadResult","nReqId":0,"sPath":"","nOffset":0,"nSize":0,"sError":"","arrData":"base64"}	(nSize: 지금 파일 크기)
   ### {"sType":"Files","nReqId":0,"sPath":""}	폴더 보기 (바로 아래만, "": aLogFolder 목록 → arrDir)
      #### → {"sType":"FilesResult","nReqId":0,"sPath":"","bTruncated":false,"sError":"","arrDir":[""],"arrFile":[{"sName":"","nSize":0,"sTime":""}]}
   ### 허용 경로: 덤프 aLogFolder 하위 *.dmp (aBlockDump 밖), 폴더 보기·파일 다운 aLogFolder 자신·하위 (aBlockFile 밖) (그 외 거부), 동시 처리: 보기와 합쳐 m_nViewConcurrent 건
## 검색 대상: 쿼리 대상 폴더의 날짜 폴더(yyyyMMdd) 안 *yyyyMMdd_HH.log (예: Logs\XLobby\20261020\XLobby_20261020_11.log)
   ### 기간 nStart~nEnd (yyyyMMddHH, 양끝 포함, 24시 = 그날 23시까지), 검색어 포함 줄 (대소문자 무시, AND·OR·NOT (대소문자 무시)·괄호·앞뒤 * 와일드카드 (ab*·*ab·*ab*), 왼쪽부터, 예: "A AND (B NOT C) OR D", SearchNode)
   ### 머신당 최대 nMax 건·X초, 1건씩 처리 (게임 서버 부하 제한)
   ### 부하 조절: 검색·보기 Throttle (X ms 일하면 X ms 쉼)
   ### 보기 허용: aLogFolder 하위 *.log·*.txt·*.json·*.ini·*.bat·*.xml·*.config·*.md·*.lua (그 외 거부), 인코딩: 줄마다 UTF-8·CP949 판별 (UTF-8·UTF-16 BOM 지원)
## 로그: bLog 가 true 일 때만 기준 폴더 Log/YYYYMMDD/Client_YYYYMMDD_HH.log 기록 → Log.Write(INFO_, "메시지") (레벨: DEBUG·INFO_·NOTI_·WARN_·ERROR·FATAL)
## 타이머: Timer.Start → 별도 스레드 X초마다 Batch (X분마다 Alive 로그), 종료 시 Timer.Stop
## 종료: Ctrl+C (서비스: 서비스 중지)

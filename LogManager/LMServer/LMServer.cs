#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
#:property ServerGarbageCollection=false
#:package Microsoft.Extensions.Hosting.WindowsServices@10.0.10

// LMServer: 로그 관리 웹 서버 (.NET 10 file-based app)
// 실행: dotnet run --file LMServer.cs [포트] [호스트=localhost]  (같은 폴더 LMServer.csproj 때문에 --file 필수)
//   예) dotnet run --file LMServer.cs                -> http://localhost:포트 (외부 접속 불가)
//   예) dotnet run --file LMServer.cs 9000 0.0.0.0   -> 모든 인터페이스 9000 포트 (방화벽 허용 필요)
// 디버깅: LMServer.slnx (Visual Studio 2026) F5
// 구성: HTGlobal(전역 함수) → HTLog(로그) → HTAES(복호화) → HTConfig(설정) → HTTimer(타이머) → HTWeb(웹 서버·로그인) → HTSearch(서치 탭) → HTDump(덤프 탭) → HTFile(파일 탭) → HTQuery(쿼리 탭) → HTAlarm·HTStat·HTSend·HTAlarmLog(알람) → HTMain(진입점) → ClientListener 등(LMClient TCP 수신), SearchNode(검색어 규칙, 서치·쿼리 공통)
// 기본 페이지: IndexHtml()
// 정적 파일: 같은 폴더 wwwroot (있으면 자동 서비스, index.html 기본 문서)
// 설정 파일: 같은 폴더 프로젝트명.json → HTConfig.GetString("DB:Dev:sHost")·GetInt("DB:Dev:nPort") (키 없음: 기본값)
//   sCode 키: 로딩 시 HTAES 복호화 → 같은 위치 sPass (예: "DB:Dev:sCode" → GetString("DB:Dev:sPass"))
//   Base: bLog(파일 로그), bWeb(웹 서버), bAuth(웹 로그인), sID·sCode(로그인 계정), sZone(Dev·Alpha·Real → 제목·색), sUrl(알람 메일 쿼리 링크 주소, 없음: 웹 바인딩 주소)
//   aQuery: 저장 쿼리 [{sKey, sQuery, nMinute}], aAlarm: 알람 [{nID, sKey, sQuery, nMinute, nCount, nDelay, aMail, sHook, aSMS, bEnable}]
//   Mail: sHost·nPort (기본 25)·bSsl·sFrom (없음: sID)·sID·sCode (알람 메일 SMTP, sHost 없음·sFrom·sID 모두 없음: 메일 실패 → 펜딩·재시도 후 포기, sID 있음: 인증)
// TCP 수신: 프로젝트명.json 의 nPort (모든 인터페이스, 없음: 미사용) ← LMClient (한 줄 = JSON 1건: Hello·File·Log·Ping)
//   로그 감시 원문 (HTQuery): 클라 Log (aLogFolder 최신 로그의 aLogType 줄, sFolder: 대상 폴더 전체 경로) → 기준 폴더 하위 File\Origin\머신\클라 경로 (드라이브 ':' 제거) 에 줄마다 레코드 1줄 (원문 저장 1번, 색인: 바이트 위치·길이로 참조)
//     레코드: [원문 시간][Machine][Folder][Line]:[원문]:[Type]{추가 필드} (원문 시간: 원문 앞 첫 [시간]·없음: 받은 시간 KST, 원문: 받은 줄 그대로, Line: 클라 파일 줄 번호 (예전 레코드: 없음), Type: 시간 뒤 첫 [] 안, 추가 필드: 클라 aField [{sKey, sRegex}] 첫 () 값 → JSON {키: 값} (이름: File\Index\field.txt 받은 순), 단어 색인: 원문에서 앞 시간 뺀 내용)
//       예) [2026/10/31 09:07:31.635][GAME01][D:\Logs\XLobby][1523]:[[2026/10/31 09:07:31.635] [ERROR] [TID:11700] 실패 sno(123)]:[ERROR][123]
//   로그 감시 적재 (HTQuery, 멀티스레드 + 스왑 큐): 수신 스레드 → 파일 (머신·경로) 해시 워커의 라이트 큐 (잠금: Add 순간) → 워커 (코어 수 × 2.5) 가 신호마다 리드·라이트 큐 스왑 (잠금: 스왑 순간) 후 리드 큐 잠금 없이 처리 → 같은 파일: 한 워커 순서대로, 다른 파일: 동시
//     라이트 큐 밀림 (X줄 이상): 부하 로그 (워커마다 X초에 1번) + 모든 클라에 {"sType":"Pause"} (읽기·전송 멈춤, 위치 유지) → 모든 워커 X줄 미만: {"sType":"Resume"} (쌓인 줄: X초마다 최대 nLogSend), 새 클라: 멈춤 중이면 Pause 먼저
//     적재 메모리 상한 = 시작 때 남은 물리 메모리 절반 (시작 로그): 라이트 큐 최대 줄 수 (상한 1/4 ÷ 줄당 추정) 넘음: 수신 스레드 대기 (TCP 역압), 합침 묶음 최대 줄 수 (상한 1/2 ÷ 줄당 추정), 워커 예외: 출력 후 계속
//   로그 감시 색인 (HTQuery, 세그먼트 역색인): 원문 단어 + 필드 값 → 워커마다 원문 시간별 메모리 버퍼 → 타이머가 X초마다 버퍼 교체 (잠금: 교체 순간) 후 시간마다 1개로 → 기준 폴더 하위 File\Index\원문 시간(yyyyMMddHH)\
//     단어: 글자·숫자·'-'·'_' 연속 (그 외 기호·빈칸에서 나눔, 앞뒤 '-'·'_' 뗀 형태도 색인, '-'·'_' 뿐: 제외, 소문자 → 대소문자 무시·딱 맞는 단어만, 예: [NOTI_] → noti_·noti)
//     필드: Machine·Folder·Zone·Type·추가 필드 값 통째로 (대소문자 무시·딱 맞는 값, 값 없음: 색인 안 함)
//     file.txt                      : 원문 파일 번호 목록 "번호,머신,클라 경로,Folder,Zone" (긴 경로 1번만, 줄 목록: 번호만)
//     원문 시간\doc.bin             : 줄 목록 (줄 번호 = 순서, 줄당 20바이트: 분초(ms)·원문 파일 번호·원문 위치·원문 바이트)
//     원문 시간\첫 줄 번호-끝 줄 번호.idx : 세그먼트 (머리: 버전·단어 수·필드 사전 바이트·버킷 시작 257 + 단어 사전: 64비트 해시 순 칸 (해시·포스팅 위치·건수·바이트), 해시 상위 바이트 256 버킷 + 필드 사전: "필드\t값" 순 칸 (포스팅 위치·건수·바이트·키) + 포스팅: 줄 번호 오름차순 차이 varint)
//       X개 넘음: 최신부터 비슷한 크기끼리 합침 (묶음 줄 수 X 이하, 메모리 상한), 마지막 쓰기 후 X분: 1개로 합침 (줄 수 X 넘는 시간: 그대로), 옛 파일: 더 넓은 범위에 포함 → 읽는 쪽이 뺌 (합치는 중 쿼리 가능), 옛 파일 삭제: 세그먼트 읽기·쓰기 잠금, 종료: 큐·버퍼 모두 파일로 (강제 종료: 최대 X초분 색인만 손실)
//   쿼리 (HTQuery): /api/query?sKey=&nStart=&nEnd=&nPage=&nOffset=&nCount=  (기간 yyyyMMddHHmm, 시작 포함·끝 미포함, nOffset·nCount: 페이지 안 이어 읽기) → 시간 폴더마다 병렬로 포스팅만 읽어 AND·OR·NOT 정렬 병합 (sKey: SearchNode 규칙, 필드:값 → 그 필드 값 줄, * → 전체, 단어·필드 1개·시간 전체: 사전 건수 합) → 시간별·분별 건수 + 최신 순 X건 페이지만 원문 읽음 (같은 쿼리·기간: X초 보관) {sKey, nStart, nEnd, nTotal, nPage, nPageSize, nElapsedMs, sError, arrHour: [{nHour, nCount}], arrMinute: [{nMinute, nCount}] (기간 1시간 이하), arrSecond: [{nSecond, nCount}] (기간 1분 이하), arrLine: [{sTime, sMachine, sPath, sFolder, sText (시간 뒤 원문), sOrigin (원문 그대로), nLine, arrField: [{sName, sValue}]}]}
//   필드별 통계 (HTQuery): /api/query/stat?sKey=&nStart=&nEnd=&sField=  → 쿼리·기간 줄 ∩ 필드 값 포스팅 → {sKey, nTotal, nElapsedMs, sError, arrField: [{sName, bBase, nValue, arrValue: [{sValue, nCount}] (건수 순 X개, sField: 그 필드 전체)}]}
//   필드 값 목록 새 창 (HTQuery): /query/field?sKey=&nStart=&nEnd=&sField=  → 필드 모드 페이지 (값 X개씩 페이지, 값 클릭: 연 창에서 필드:값 쿼리 후 이 창 닫음)
//   필드 목록 (HTQuery): /api/query/field → {arrField: [{sName, bBase}]} (기본 필드 + 클라 aField 추가 필드)
//   저장 쿼리 (HTQuery): /api/query/saved → {arrQuery: [{sKey, sQuery, nMinute}]}, POST /api/query/saved {sKey, sQuery, nMinute, sOldKey} (같은 이름: 교체, sOldKey: 그 자리 수정)·/api/query/saved/delete {sKey}·/api/query/saved/move {sKey, nTo} → 프로젝트명.json 의 aQuery (요청자 IP 로그)
//   알람 목록 (HTQuery): /api/alarm → {arrAlarm}, POST /api/alarm {nID (0: 추가), 값}·/api/alarm/delete {nID}·/api/alarm/move {nID, nTo}·/api/alarm/test {값} (메일·훅 바로, 저장 안 함) → 프로젝트명.json 의 aAlarm (요청자 IP 로그)
//   알람 (HTAlarm): 적재 때 매칭 (앵커 단어·필드로 후보만 조건 평가, 쿼리와 같은 규칙, 워커 카운터) → HTStat 매분 합산 (수신 시각)·nMinute 분 구간 끝 (시계 기준) 판정·nDelay 분 억제·분당 X건 한도
//     → HTSend X개 스레드 메일·훅 POST {sTitle, sText} (SMS: 나중에) → File\Alarm\log\yyyyMMdd.jsonl (Sent·Failed·Deferred·Resent·Expired), 한도 넘음·실패: File\Alarm\pending.json → 다음 분 재전송 (X번·X분 넘음: 포기)
//   알람 로그 (HTAlarmLog): /api/alarm/log?nStart=&nEnd=&nID=&sResult=&nPage=  (기간 yyyyMMddHHmm, 최대 X일) → {sError, nTotal, nPage, nPageSize, arrLog} (최신 순)
//   접속 클라 목록: /api/client
//   로그 검색 (HTSearch, 여러 머신): POST /api/search {arrMachine, nStart, nEnd, sKey, nMax, sToken}  (sKey: AND·OR·NOT·괄호, 클라 SearchNode 처리, 예: A AND (B NOT C) OR D)  → 머신마다 클라에 Search, 끝난 순서로 한 줄씩 {nIndex, sMachine, sError, kResult} (NDJSON)
//   파일 보기 (HTSearch, 머신 1대): /api/view?sMachine=&sPath=&nStart=&nCount=  → 클라에 View (nStart 0: 마지막)
//   검색 진행·중지 (HTSearch): /api/progress?sToken= (진행 머신들의 진행 합), /api/stop?sToken= (머신마다 Stop)
//   덤프 목록 (HTDump, 여러 머신): POST /api/dump {arrMachine}  → 머신마다 클라에 Dump, 끝난 순서로 한 줄씩 {nIndex, sMachine, sError, kResult} (NDJSON, kResult.arrFile[].bZip: LMServer Dump 폴더 zip 있음)
//   덤프 다운 (HTDump, 머신 1대): POST /api/dump/download {sMachine, sPath}  → 클라에 DumpRead 조각마다 요청 → 기준 폴더 하위 Dump\머신\덤프 파일명.zip 압축 저장 (바로 응답, 페이지가 X초마다 재호출: {bDone, nOffset, nSize})
//   덤프 zip 받기 (HTDump): /api/dump/zip?sMachine=&sPath=  → Dump\머신\덤프 파일명.zip 첨부 전송 (없음: 404)
//   폴더 보기 (HTFile, 머신 1대): /api/files?sMachine=&sPath=  → 클라에 Files (sPath "": 클라 aLogFolder 목록, 결과: 바로 아래 폴더·파일)
//   파일 다운 (HTFile, 머신 1대): /api/files/download?sMachine=&sPath=  → 클라에 FileRead 조각마다 요청 → 브라우저로 그대로 (첨부, 압축 안 함)
//   덤프 삭제 (HTDump, 머신 1대): POST /api/dump/delete {sMachine, sPath}  → 클라에 DumpDelete, 성공: 다운 zip 도 삭제 (빈 Dump\머신 폴더 삭제, 요청자 IP 로그)
// 로그: Base:bLog true 일 때만 기준 폴더 하위 Log/YYYYMMDD/LMServer_YYYYMMDD_HH.log 시간별 → HTLog.Write(INFO_, "메시지") (레벨: DEBUG·INFO_·NOTI_·WARN_·ERROR·FATAL)
// 타이머: HTTimer.Start → 별도 스레드 X초마다 HTTimer.Batch (X분마다 Alive 로그, HTQuery.Tick: 색인 버퍼 → 세그먼트·합치기), 종료: HTTimer.Stop → HTQuery.Close (남은 버퍼 파일로)
// 웹 서버: Base:bWeb true 일 때만 HTWeb 을 HTMain.Service 호스트에 등록 (false·키 없음: 호스트만 대기, 서비스 중지·Ctrl+C: 종료)
// 로그인: Base:bAuth true 일 때만 (false·키 없음: 모두 허용), 미인증 → 로그인 페이지 → POST /api/login 이 Base:sID·Base:sCode(복호화 → Base:sPass) 일치: 세션 쿠키 발급 (브라우저 종료까지 유지, 미인증 API: 401, LMClient TCP 제외)
//   인증 쿠키 암호화 키: 메모리만 (서버 재시작: 모두 재로그인)
// 제목: LM + Base:sZone (예: LM Alpha), 색: Dev 초록·Alpha 파랑·Real 빨강 (/api/info 의 sTitle·sTitleColor)
// 종료: Ctrl+C

/* 
**UI/UX Guidelines**

# GitHub Primer 그대로 (@primer/primitives 토큰, Octicons), 자체 색 추가 금지 (존 색만), 버튼 선택: 검정 (탭 전환 버튼: 존 색), 중요 버튼: 존 색
*frontend-design 스킬로 초기 제작*

# 공통 기능
   ## 탭 전환 버튼: 제목란 오른쪽, 선택 탭 존 색, 처음: 맨 앞 탭, 다른 탭에서 돌아옴: 마지막 상태, 이전 탭 버튼 포커스 테두리 없음
      Tab 키: 다음 탭 (맨 끝: 처음, 입력칸·팝업 안: 기본 포커스 이동)
   ## 힌트 버튼: 탭 전환 버튼 오른쪽 끝, 글자 존 색, 누름: 힌트 팝업 (단축키 | 명령 표, 닫기·Esc)
   ## 단어 Ctrl·Alt·Ctrl+Alt + 클릭 (결과·원문): 입력칸 끝에 AND·OR·NOT 추가, 같은 단어 다시: 빼기, 키 다 뗀 뒤 X초: 실행 (지금 단계의 하위)
      펼친 줄 필드 값: 필드:값, IP·IP:포트·날짜·시간: 한 단어 (쿼리: 원문 그대로 있는 줄만)
   ## UI Ctrl·Alt + 클릭 (버튼·입력칸·드롭 박스·파일명·상자 제목란): Ctrl 새 창 · Alt 새 탭, 지금 창 그대로
      새 창: 지금 상태 (탭·머신·입력칸·서치·쿼리 기간·← 단계 →) + 누른 버튼 실행·입력칸 포커스, 쿼리·서치 결과: 그대로 복사 (결과 없음: 쿼리는 그 단계 다시 쿼리, 서치는 다시 검색 안 함)
      파일명·보기 제목: 파일 보기 (결과 줄: 그 줄 강조), 필드 이름: 값 목록
      Ctrl 새 창 크기·자리: 누른 브라우저와 같음, 여는 때: 클릭 이벤트 끝난 뒤 (Chrome 이 Ctrl 을 보고 뒤 탭으로 여는 것 막음)
   ## 파일 (표 File 칸·파일 트리): 아이콘 없음, Shift+클릭: 다운, Ctrl·Alt+클릭: 파일 보기 새 창·새 탭, 클릭: 지금 창 (펼침·보기)
   ## 표 Ctrl+A: 누른 표 (없음: 맨 위 표) 의 칸 이름 + 줄 (입력칸: 기본)
   ## 상자 머리: 왼쪽 제목 (짧게: 범위 - 종류: 건수·시간만, 예: 60초 - 쿼리 결과, 전체 - 서치 결과), 오른쪽 ← 단계 → (같은 종류: 같은 자리·모양)
      ← 단계 →: 가운데 숫자 = 지금 단계 (0: 최초, 제한 없음), 숫자 누름: 입력 → Enter: 그 단계 (Esc: 취소)
      단계 이동: 그 단계 조건 (입력칸·기간) 복원 + 마지막 본 결과 그대로 (없음: 다시 실행, 실행 중: 중단 후), 조건·결과: 브라우저 메모리 (새로 고침: 사라짐, 서치 결과: 모든 단계 합 최대 건수 상한까지)
      새로 실행: 앞 단계 비움, 되돌아간 뒤 하위 실행: 뒤 단계 비움
   ## 줄 보기 머리 (서치 2번 창): 제목 · ← 위치 → (0: 처음 연 위치, ←·→: 읽는 줄 수만큼 위·아래)
   ## 페이지 번호: 상자 맨 아래 가운데, 작게 (높이 20px, 위아래 1px), 지금 페이지 존 색
   ## 상자 높이: 처음·브라우저 창 크기 바뀜: 맨 아래 상자를 창 아래 끝에 맞춤 (페이지 번호까지 한 화면, 페이지 스크롤 없음, 최소 높이)
      목록 많음: 상자 안 스크롤, 위아래 창: 아래 창이 나머지 (사이 바 드래그·위 창 높이 바뀜: 다시 맞춤), 쿼리 2번·서치 2번·덤프·파일 같음, 쿼리·서치 처음 창 위치·크기 같음
      필드 값 새 창: 창 아래 끝까지가 최대, 목록 적음: 목록만큼
   ## 스크롤 바: 처음부터 전체 크기 (안 읽은 줄: 빈 높이 → 끝에서 안 늘어남), 끝 확인 (서버가 덜 줌): 읽은 데까지로 보정
      가까움: 이어 읽기, 멂 (끌기): 그 위치, 최대 줄 넘음: 반대쪽 지움
      모양: GitHub (Chrome 기본) 기준, 세모: 상자 끝·막대와 살짝 띄움, 세모·막대 같은 폭·가운데 맞춤, 막대: 회색 얇은 알약, 표 상자: 칸 이름 줄 밑에서 시작
   ## 표: 칸 경계 드래그 너비 (최소 1글자, 나머지 너비 칸 하나, 더블 클릭: 자동), 줄 클릭: 아래로 펼침 (필드 + 원문 줄바꿈)
      칸 이름 줄: 상자 위에 딱 붙음 (스크롤 틈 없음), 구분선 늘 보임 (경계 손잡이: 옆 2px 존 색), 상자와 겹치는 바깥 선 숨김
      Ac. 칸: 줄 동작 있는 표만 (덤프: 다운·압축·삭제), 맨 오른쪽 고정, 아이콘만
   ## 창 사이 바: 위아래·좌우 창 사이, 끌어서 크기 (위아래: 합 그대로, 좌우: 왼쪽 폭), 창마다 최소 크기, 마우스 올림·끌기: 존 색, 명령 창·결과 창 사이도 같음
   ## 파일·원문 보기 글자: 검정
   ## 필드 목록: 기본 필드 밑 연한 구분선, 값 0 인 추가 필드 안 보임
   ## 진행 표시: 상자 가운데 팝업 (상태·경과 초), 실행 버튼 경과 초, 중단 버튼 (서버도 멈춤)
   ## 오류: "작업 실패: 두 단어" (Server Off·Time Out·Login Need·User Stop 등, 원문 안 씀, 서버 오류 글: 그대로)
   ## 확인 팝업: 삭제 (대상 굵게 + 상세, 삭제·취소, Enter: 취소), 다운 (파일명 굵게 + 상세, 예·아니오), Esc: 닫기
   ## 저장 목록 팝업 (입력칸 오른쪽 톱니): 제목란 탭 (탭 전환 버튼과 같음), 제목 줄 + 수정·삭제 (전체 목록, 높이 고정·넘침: 스크롤), 아래 추가·닫기
      더블 클릭: 내용·간격 넣고 실행 (실행 중: 중단 후), 드래그: 순서 (설정 파일 저장)
   ## Enter: 실행 (한글 조합 중 제외), 입력칸 설명 (placeholder) 안 씀, 설정 값: 브라우저 localStorage (기본값 버튼)
   ## 툴팁: 명사·값만 짧게 (예: 전체 삭제(dmp, zip), 2026-11-01 00시 · 0건), 동작 설명 안 씀

# 존 색 (GetTitleColor → CSS --zone, Dev 초록·Alpha 파랑·Real 빨강, 존 없음: 검정·기본)
   ## 제목 글자·테두리, 로그인 제목·버튼, 탭 전환 버튼 선택 (흰 글자), 힌트 버튼 글자, 지금 페이지 번호, 입력칸·포커스 테두리
   ## 중요 버튼: 바탕 + 흰 글자 (마우스 올림·누름·꺼짐: 존 색에서 계산)
   ## 위험 버튼 (중단·삭제·삭제 아이콘): 글자 존 색, 마우스 올림: 존 색 바탕 + 흰 글자, 꺼짐: 반투명 (존 없음: 빨강)
   ## 창 사이 바·칸 경계 마우스 올림·끌기: SM 존 색 (--split, 제목 색과 별개)
   ## 탭 아이콘: 약칭 굵은 흰 글자 + 존 색 바탕
   ## 막대 차트: 존 색 50% 밝게, 날짜별 x축 월 글자

# LogManager 전용
   ## 쿼리 ↔ 서치 Shift+클릭: 다른 탭 입력칸에 명령·지금 기간 옮김 (쿼리 D: 지금 기준 S·E)
      입력칸: 복사만, 실행 버튼·단어·필드 값: 복사 + 실행, Shift+Ctrl·Alt: 새 창·새 탭 (단계도), 빈 명령·검색 중: 안 함
   ## 쿼리 탭 처음 입장: D 1일 전체 쿼리 자동 실행 (새 창 상태 복원: 복원대로)
   ## 쿼리 막대 클릭: 날짜별 → 시간별 → 분별 → 초별 (초별: 끝)
   ## 쿼리 결과 제목 기간: 막대 단위 (초별: 60초, 분별: 60분, 시간별: 24시간, 날짜별: N일), 초별·분별 x축 첫 라벨: 숫자만 (예: 0)
   ## 쿼리 설정: 쿼리 (저장 쿼리, aQuery)·알람 (aAlarm, 서버가 매분 판정·발송) 탭, 쿼리 추가 창: 제목·쿼리·간격 (분 직접 입력), 알람 추가 창: 제목·쿼리·간격·건수·억제 (한 줄)·메일·SMS (추가 버튼·Enter → 밑 목록, 줄마다 삭제)·훅·사용, 테스트 버튼: 창 값으로 메일·훅 바로 발송 (결과 알림)
*/

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting.WindowsServices;
using static HTGlobal;	// 전역 함수 클래스명 없이 호출
using static HTLog;		// 로그 레벨 클래스명 없이 호출

static class HTGlobal	// 전역 함수
{
	public static int GetDefaultPort()	// 기본 포트
	{
		return 800;
	}

	public static string GetProjectName()	// 프로젝트명 = 어셈블리 이름 (AES 키, 코드 생성: SELECT TO_BASE64(AES_ENCRYPT('Password', 'ProjectName')))
	{
		return typeof(HTGlobal).Assembly.GetName().Name ?? "";
	}

	public static string GetConfigFile()	// 설정 파일명
	{
		return GetProjectName() + ".json";
	}

	public static string GetBaseDir()	// 기준 폴더 = 콘텐츠 루트 (서비스: exe 폴더, 그 외: 현재 폴더)
	{
		return WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : Directory.GetCurrentDirectory();
	}

	public static long GetAvailableMemory()	// 남은 물리 메모리 (바이트, 실패: 런타임 전체 메모리 절반)
	{
		MEMORYSTATUSEX kStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };

		if (OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref kStatus))
		{
			return (long)kStatus.ullAvailPhys;
		}

		return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX kStatus);

	[StructLayout(LayoutKind.Sequential)]
	private struct MEMORYSTATUSEX	// 윈도우 메모리 상태 (GlobalMemoryStatusEx)
	{
		public uint		dwLength;
		public uint		dwMemoryLoad;
		public ulong	ullTotalPhys;
		public ulong	ullAvailPhys;
		public ulong	ullTotalPageFile;
		public ulong	ullAvailPageFile;
		public ulong	ullTotalVirtual;
		public ulong	ullAvailVirtual;
		public ulong	ullAvailExtendedVirtual;
	}

	public static DateTimeOffset GetNow()	// 현재 시각 (KST, UTC+9)
	{
		return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
	}

	public static string GetNowText()	// 현재 시각 문자열 (KST)
	{
		return GetNow().ToString("yyyy-MM-dd HH:mm:ss");
	}

	public static void SetKeepAlive(Socket kSocket)	// TCP keepalive (끊긴 연결 감지)
	{
		try
		{
			kSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
			kSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
			kSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
			kSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);	// 윈도우 10 1703 이상
		}
		catch (Exception)	// 구버전 OS: 지원 옵션까지만 적용
		{
		}
	}

	public static string GetErrorText(Exception kException, string sWhere)	// 예외 → 페이지 표시용 두 단어 (상세: 로그, sWhere: 로그 앞 글, 예: 덤프 삭제D:\Dump.dmp)
	{
		HTLog.Write(WARN_, $"{sWhere} 실패: {kException.Message}");

		return kException switch
		{
			RelayException												=> kException.Message,	// 상대가 보낸 글 그대로
			OperationCanceledException									=> "User Stop",
			TimeoutException											=> "Time Out",
			UnauthorizedAccessException									=> "Access Denied",
			FileNotFoundException or DirectoryNotFoundException			=> "Not Found",
			PathTooLongException										=> "Path Long",
			JsonException or FormatException or InvalidDataException	=> "Bad Data",
			SocketException or ObjectDisposedException					=> "Client Off",
			IOException when kException.InnerException is SocketException	=> "Client Off",
			IOException when kException.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)	=> "File Busy",	// 공유·잠금 위반 (사용 중)
			IOException when kException.HResult is unchecked((int)0x80070070)	=> "Disk Full",
			IOException													=> "IO Error",
			_															=> "Server Error",
		};
	}
}

static class HTLog	// 로그: Log/YYYYMMDD/프로젝트명_YYYYMMDD_HH.log 시간별 (줄: [yyyy/MM/dd HH:mm:ss.fff] [레벨] 메시지)
{
	public const string DEBUG	= "DEBUG";	// 로그 레벨: 디버그
	public const string INFO_	= "INFO_";	// 로그 레벨: 정보
	public const string NOTI_	= "NOTI_";	// 로그 레벨: 알림
	public const string WARN_	= "WARN_";	// 로그 레벨: 경고
	public const string ERROR	= "ERROR";	// 로그 레벨: 오류
	public const string FATAL	= "FATAL";	// 로그 레벨: 치명

	private static readonly object	m_lockWrite	= new object();								// 같은 파일 동시 추가 방지
	private static readonly bool	m_bConsole	= !WindowsServiceHelpers.IsWindowsService();	// 콘솔 모드 (서비스 아님)
	private static readonly object	m_lockConsole	= new object();							// 콘솔 색 변경·출력·복원 묶음 (색 번짐 방지)

	public static string GetDir()	// 로그 폴더 (기준 폴더 하위 Log)
	{
		return Path.Combine(GetBaseDir(), "Log");
	}

	public static string GetFile(string sStamp)	// 시간별 로그 파일 (sStamp: yyyyMMdd_HH)
	{
		return Path.Combine(GetDir(), sStamp.Substring(0, 8), $"{GetProjectName()}_{sStamp}.log");
	}

	public static void Write(string sLevel, string sMessage)	// 로그 한 줄 추가 (콘솔 모드: 콘솔도, 파일: Base:bLog true 일 때만)
	{
		DateTimeOffset kNow = GetNow();
		string sFile = GetFile(kNow.ToString("yyyyMMdd_HH", CultureInfo.InvariantCulture));
		string sLine = $"[{kNow.ToString("yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] [{sLevel}] {sMessage.Replace('\r', ' ').Replace('\n', ' ')}";	// 줄바꿈 제거 (한 줄 = 로그 1건)

		if (m_bConsole)
		{
			WriteConsole(sLevel, sLine);
		}

		if (!HTConfig.GetBool("Base:bLog"))	// 키 없음·설정 로딩 전: false
		{
			return;
		}

		try
		{
			lock (m_lockWrite)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(sFile)!);
				File.AppendAllText(sFile, sLine + "\n", new UTF8Encoding(false));
			}
		}
		catch (Exception kException)
		{
			Console.WriteLine($"로그 쓰기 실패: {kException.Message}");
		}
	}

	private static void WriteConsole(string sLevel, string sLine)	// 콘솔 한 줄 출력 (레벨별 글자색)
	{
		ConsoleColor? eColor = sLevel switch	// null: 기본색
		{
			DEBUG			=> ConsoleColor.Green,
			NOTI_ or WARN_	=> ConsoleColor.Yellow,
			ERROR or FATAL	=> ConsoleColor.Red,
			_				=> null,
		};

		lock (m_lockConsole)
		{
			if (eColor == null)
			{
				Console.WriteLine(sLine);

				return;
			}

			Console.ForegroundColor = eColor.Value;
			Console.WriteLine(sLine);
			Console.ResetColor();
		}
	}
}

static class HTAES	// MySQL AES 호환 복호화 (MySQL 기본 aes-128-ecb, PKCS7 패딩)
{
	public static string? Decrypt(string sData)	// CAST(AES_DECRYPT(FROM_BASE64(sData), 프로젝트명) AS CHAR) 호환 (실패: null)
	{
		if (string.IsNullOrEmpty(sData))
		{
			return null;
		}

		byte[] arrData;

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

			kAes.Key = GetMySQLKey(GetProjectName());

			return Encoding.UTF8.GetString(kAes.DecryptEcb(arrData, PaddingMode.PKCS7));	// CAST AS CHAR (utf8mb4)
		}
		catch (CryptographicException)	// 키 불일치·패딩 오류
		{
			return null;
		}
	}

	private static byte[] GetMySQLKey(string sKey)	// MySQL 키 변환: 16바이트 버퍼에 순환 XOR
	{
		byte[] arrKey		= new byte[16];
		byte[] arrSource	= Encoding.UTF8.GetBytes(sKey);

		for (int nIndex = 0; nIndex < arrSource.Length; ++nIndex)
		{
			arrKey[nIndex % arrKey.Length] ^= arrSource[nIndex];
		}

		return arrKey;
	}
}

static class HTConfig	// 설정 파일 로딩 및 값 조회 (키 구분자 ':', 예: GetString("DB:Dev:sHost"))
{
	private static IConfigurationRoot m_kConfig = new ConfigurationBuilder().Build();	// 설정 값 (Load 전·파일 없음: 빈 설정)
	private static readonly object m_kSaveLock = new object();							// 설정 파일 쓰기 1건씩

	public static bool Load()	// 설정 파일 로딩 (false: 형식 오류)
	{
		string sPath = Path.Combine(GetBaseDir(), GetConfigFile());

		if (!File.Exists(sPath) || new FileInfo(sPath).Length == 0)	// 없음·빈 파일: 빈 설정 (빈 파일은 JSON 파싱 오류)
		{
			HTLog.Write(WARN_, $"설정 파일: 없음 또는 빈 파일 ({sPath})");

			return true;
		}

		IConfigurationRoot kConfig;	// 복호화 후 교체 (다시 로딩 중 읽는 쪽: 앞 설정)

		try
		{
			kConfig = new ConfigurationBuilder().AddJsonFile(sPath, optional: false, reloadOnChange: false).Build();
		}
		catch (Exception kException)	// JSON 형식 오류
		{
			HTLog.Write(ERROR, $"설정 오류: {sPath} ({kException.Message})");

			return false;
		}

		int nDecrypt = DecryptCode(kConfig);	// sCode → sPass

		m_kConfig = kConfig;

		HTLog.Write(INFO_, $"설정 파일: {sPath} (복호화: {nDecrypt}건)");

		return true;
	}

	private static int DecryptCode(IConfigurationRoot kConfig)	// sCode 값 복호화 → 같은 위치 sPass 로 메모리 보관 (반환: 건수)
	{
		Dictionary<string, string> hmapPass = new Dictionary<string, string>();	// sPass 키 → 값 (열거 중 변경 방지용 임시)

		foreach (KeyValuePair<string, string?> kPair in kConfig.AsEnumerable())
		{
			if (!string.Equals(ConfigurationPath.GetSectionKey(kPair.Key), "sCode", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(kPair.Value))
			{
				continue;
			}

			string? sPass = HTAES.Decrypt(kPair.Value);

			if (sPass == null)	// 실패: sPass 미등록, 값 미출력
			{
				HTLog.Write(ERROR, $"설정 복호화 실패: {kPair.Key}");

				continue;
			}

			string? sParent = ConfigurationPath.GetParentPath(kPair.Key);	// 최상위: null

			hmapPass[sParent == null ? "sPass" : ConfigurationPath.Combine(sParent, "sPass")] = sPass;
		}

		foreach (KeyValuePair<string, string> kPass in hmapPass)
		{
			kConfig[kPass.Key] = kPass.Value;
		}

		return hmapPass.Count;
	}

	public static string GetString(string sKey, string sDefault = "")	// 문자열 값 (키 없음: 기본값)
	{
		return m_kConfig[sKey] ?? sDefault;
	}

	public static int GetInt(string sKey, int nDefault = 0)	// 정수 값 (키 없음·변환 실패: 기본값)
	{
		return int.TryParse(m_kConfig[sKey], out int nValue) ? nValue : nDefault;
	}

	public static double GetDouble(string sKey, double dDefault = 0)	// 실수 값 (키 없음·변환 실패: 기본값, JSON 소수점 → InvariantCulture)
	{
		return double.TryParse(m_kConfig[sKey], NumberStyles.Float, CultureInfo.InvariantCulture, out double dValue) ? dValue : dDefault;
	}

	public static bool GetBool(string sKey, bool bDefault = false)	// bool 값 (키 없음·변환 실패: 기본값)
	{
		return bool.TryParse(m_kConfig[sKey], out bool bValue) ? bValue : bDefault;
	}

	public static IEnumerable<IConfigurationSection> GetChildren(string sKey)	// 하위 값들 (배열: 순서대로)
	{
		return m_kConfig.GetSection(sKey).GetChildren();
	}

	public static string SetArray(string sKey, JsonArray kArray)	// 최상위 배열 값 저장 후 다시 로딩 (다른 키 유지, 반환: 오류, 성공: "")
	{
		string sPath = Path.Combine(GetBaseDir(), GetConfigFile());

		lock (m_kSaveLock)
		{
			try
			{
				JsonObject kRoot = File.Exists(sPath) && new FileInfo(sPath).Length > 0 ? JsonNode.Parse(File.ReadAllText(sPath), null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject() ?? new JsonObject() : new JsonObject();	// 설정 (없음·빈 파일: 새로)

				kRoot[sKey] = kArray;

				File.WriteAllText(sPath + ".tmp", kRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\r\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\r\n");	// 임시 파일 후 교체 (쓰는 중 실패: 원본 유지)
				File.Move(sPath + ".tmp", sPath, true);
			}
			catch (Exception kException)	// JSON 형식 오류·사용 중·권한 없음
			{
				return GetErrorText(kException, $"설정 저장 {sPath} {sKey}");
			}

			return Load() ? "" : "설정 다시 로딩 실패";
		}
	}
}

static class HTTimer	// 타이머: 별도 스레드에서 X초마다 Batch, X분마다 Alive 로그
{
	private const int	m_nTickMs		= 1000;	// Batch 호출 간격 (ms)
	private const int	m_nAliveMin		= 1;	// Alive 로그 간격 (분)

	private static Thread?					m_kThread;							// 타이머 스레드
	private static CancellationTokenSource	m_kStop		= new CancellationTokenSource();	// 중지 요청 (대기 즉시 깸)
	private static DateTimeOffset			m_kStartTime;						// 시작 시각 (KST, 가동 시간)
	private static long						m_nAliveTick;						// 마지막 Alive 로그 (TickCount64)

	public static void Start()	// 타이머 시작 (백그라운드 스레드)
	{
		if (m_kThread != null)	// 중복 시작 방지
		{
			return;
		}

		m_kStop			= new CancellationTokenSource();
		m_kStartTime	= GetNow();
		m_nAliveTick	= Environment.TickCount64;
		m_kThread		= new Thread(Run) { IsBackground = true, Name = "HTTimer" };

		m_kThread.Start();

		HTLog.Write(INFO_, $"타이머: 시작 (Batch {m_nTickMs}ms, Alive {m_nAliveMin}분)");
	}

	public static void Stop()	// 타이머 중지 (진행 중 Batch 완료 대기, 최대 X초)
	{
		if (m_kThread == null)
		{
			return;
		}

		m_kStop.Cancel();
		m_kThread.Join(5000);

		m_kThread = null;

		HTLog.Write(INFO_, "타이머: 중지");
	}

	private static void Run()	// 타이머 스레드: Batch → 남은 시간 대기
	{
		while (!m_kStop.IsCancellationRequested)
		{
			long nBegin = Environment.TickCount64;

			try
			{
				Batch();
			}
			catch (Exception kException)	// 로그 후 계속 (스레드 예외 → 프로세스 종료 방지)
			{
				HTLog.Write(ERROR, $"타이머 오류: {kException.Message}");
			}

			long nWait = Math.Max(0, m_nTickMs - (Environment.TickCount64 - nBegin));	// Batch 소요 시간 제외

			m_kStop.Token.WaitHandle.WaitOne((int)nWait);
		}
	}

	private static void Batch()	// X초마다: Alive 로그, 색인 버퍼 → 세그먼트 (HTQuery.Tick)
	{
		if (Environment.TickCount64 - m_nAliveTick >= m_nAliveMin * 60 * 1000L)
		{
			m_nAliveTick = Environment.TickCount64;

			HTLog.Write(INFO_, $"Alive: 가동 {(GetNow() - m_kStartTime).ToString(@"d\.hh\:mm\:ss")}");
		}

		HTQuery.Tick();
	}
}

class HTWeb : IHostedService	// 웹 서버 (Kestrel + Minimal API, 로그인 쿠키, HTMain.Service 호스트와 함께 시작·중지) + LMClient TCP 수신
{
	private const string	CLAIM_STAMP		= "sStamp";		// 인증 쿠키 항목: 계정 해시 (계정 변경: 기존 쿠키 무효)
	private const string	LOGIN_PATH		= "/api/login";	// 로그인 API (미인증 허용)

	private const int	m_nAuthDay			= 365;	// 인증 유지 (일, 절반 지나 접속 시 연장)
	public const int	m_nMachineMax		= 1000;	// 한 번에 검색할 최대 머신 수
	public const int	m_nViewWaitSec		= 30;	// 보기 결과 대기 (초)
	public const int	m_nDumpWaitSec		= 60;	// 덤프 목록·조각·삭제 결과 대기 (초)
	public const int	m_nDumpChunk		= 4 * 1024 * 1024;	// 덤프 다운 조각 (바이트, 클라 m_nDumpChunkMax 와 같음)
	private const int	m_nReadRetry		= 5;				// 클라 바쁨 거절 시 재요청 횟수
	private const int	m_nReadRetryMs		= 1000;				// 재요청 전 대기 (ms)
	private const string	m_sClientBusy	= "요청이 많음 (잠시 후 다시)";	// 클라 바쁨 거절 문구 (LMClient 동시 처리 초과 sError 와 같음)

	private static readonly byte[]	m_arrResultKey	= Encoding.UTF8.GetBytes(",\"kResult\":");	// 응답 조립: kResult 키
	private static readonly byte[]	m_arrNull		= Encoding.UTF8.GetBytes("null");			// 응답 조립: 결과 없음
	private static readonly byte[]	m_arrClose		= Encoding.UTF8.GetBytes("}");				// 응답 조립: 끝
	private static readonly byte[]	m_arrNewLine	= Encoding.UTF8.GetBytes("\n");				// 검색 응답: 머신 1대 결과 끝

	public static readonly JsonSerializerOptions m_kJsonOption = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };	// 한글 등 \uXXXX 미변환 (크기 절감, fetch 전용 → HTML 직접 삽입 안 함)

	private readonly string				m_sUrl;				// 바인딩 주소
	public static string				m_sLinkUrl	= "";	// 알람 메일 쿼리 링크 주소 (설정 Base:sUrl, 없음: 바인딩 주소, 웹 없음: "")
	private readonly DateTimeOffset		m_kStartTime;		// 서버 시작 시각 (KST)
	private WebApplication?				m_kWebApp;			// 웹 앱
	private readonly ClientListener		m_kListener;		// LMClient TCP 수신
	private readonly HTSearch			m_kSearch;			// 서치 탭
	private readonly HTDump				m_kDump;			// 덤프 탭
	private readonly HTFile				m_kFile;			// 파일 탭
	private readonly HTQuery			m_kQuery;			// 쿼리 탭

	public HTWeb(int nPort, string sHost)
	{
		m_sUrl				= $"http://{sHost}:{nPort}";
		m_sLinkUrl			= HTConfig.GetString("Base:sUrl") != "" ? HTConfig.GetString("Base:sUrl").TrimEnd('/') : m_sUrl;
		m_kStartTime		= GetNow();
		m_kListener			= new ClientListener(HTConfig.GetInt("nPort"));
		m_kSearch			= new HTSearch(m_kListener);
		m_kDump				= new HTDump(m_kListener);
		m_kFile				= new HTFile(m_kListener);
		m_kQuery			= new HTQuery();
	}

	public async Task StartAsync(CancellationToken kToken)	// 앱 생성 → 미들웨어 → 라우트 → 시작 (비차단)
	{
		WebApplicationBuilder kBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,	// 서비스: exe 폴더 (기본 System32 → wwwroot 못 찾음)
		});

		kBuilder.WebHost.UseUrls(m_sUrl);
		kBuilder.Logging.SetMinimumLevel(LogLevel.Warning);	// 기본 로그 최소화 (요청 로그: UseRequestLog)

		bool bAuth = HTConfig.GetBool("Base:bAuth");	// false·키 없음: 모두 허용

		if (bAuth)
		{
			kBuilder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();	// 쿠키 암호화 키: 메모리 (서버 재시작: 기존 쿠키 무효)
			kBuilder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(kOption =>
			{
				kOption.ExpireTimeSpan		= TimeSpan.FromDays(m_nAuthDay);
				kOption.SlidingExpiration	= true;								// 절반 지나 접속 시 연장
				kOption.Cookie.Name			= GetProjectName() + HTConfig.GetString("Base:sZone") + ".Auth";
				kOption.Cookie.HttpOnly		= true;
				kOption.Cookie.IsEssential	= true;
				kOption.Cookie.SameSite		= SameSiteMode.Lax;		// 다른 사이트 링크 GET: 전송 (메신저 링크 재로그인 방지), POST: 미전송 (CSRF 완화)
			});
		}

		WebApplication WebApp = kBuilder.Build();

		WebApp.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor });	// IIS 실행: X-Forwarded-For → 실제 IP (신뢰 프록시: 루프백만 → 헤더 위조 무시)

		UseRequestLog(WebApp);

		if (bAuth)	// 미인증 차단 (정적 파일·라우트보다 먼저)
		{
			WebApp.UseAuthentication();	// 절반 지난 쿠키 연장

			UseLogin(WebApp);
		}

		bool bStatic = UseStaticFile(WebApp);

		WebApp.UseRouting();

		MapRoute(WebApp, bAuth);

		bool bListen = m_kListener.Start(WebApp.Lifetime.ApplicationStopping);	// 웹 서버 종료 시 함께 중지

		if (!bAuth)
		{
			HTLog.Write(NOTI_, "로그인: 안함 (설정 Base:bAuth 가 false)");
		}
		else if (HTConfig.GetString("Base:sID") == "" || HTConfig.GetString("Base:sPass") == "")
		{
			HTLog.Write(WARN_, "로그인 계정 미설정 (Base:sID·Base:sCode): 모든 로그인 차단");
		}
		else
		{
			HTLog.Write(INFO_, "로그인: 사용 (설정 Base:bAuth 가 true)");
		}

		HTLog.Write(INFO_, $"웹 서버 시작: {m_sUrl}  (정적 파일: {(bStatic ? "wwwroot" : "없음")}, TCP 수신: {(bListen ? m_kListener.GetPort().ToString() : "없음")})");

		m_kWebApp = WebApp;

		await WebApp.StartAsync(kToken);
	}

	public async Task StopAsync(CancellationToken kToken)	// 웹 서버 중지 → 해제 (서비스 중지·Ctrl+C)
	{
		if (m_kWebApp == null)
		{
			return;
		}

		await m_kWebApp.StopAsync(kToken);
		await m_kWebApp.DisposeAsync();

		m_kWebApp = null;
	}

	private static void UseRequestLog(WebApplication WebApp)	// 요청 로그: [KST 시각] 메서드 경로 -> 상태코드 (소요ms)
	{
		WebApp.Use(async (HttpContext kContext, RequestDelegate funcNext) =>
		{
			long nBegin = Stopwatch.GetTimestamp();

			await funcNext(kContext);

			Console.WriteLine($"[{GetNowText()}] {kContext.Request.Method} {kContext.Request.Path}{kContext.Request.QueryString} -> {kContext.Response.StatusCode} ({Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds:F1}ms)");
		});
	}

	private static void UseLogin(WebApplication WebApp)	// 로그인 검사: 쿠키 없음·무효 차단 (페이지 요청: 로그인 페이지, 그 외: 401 JSON, 로그인 API: 통과)
	{
		WebApp.Use(async (HttpContext kContext, RequestDelegate funcNext) =>
		{
			if (IsLogin(kContext) || string.Equals(kContext.Request.Path.Value, LOGIN_PATH, StringComparison.OrdinalIgnoreCase))
			{
				await funcNext(kContext);

				return;
			}

			kContext.Response.StatusCode			= StatusCodes.Status401Unauthorized;
			kContext.Response.Headers.CacheControl	= "no-store";	// 로그인 페이지 캐시 방지 (로그인 후 새로고침: 요청 페이지)

			if (HttpMethods.IsGet(kContext.Request.Method) && kContext.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))	// 브라우저 페이지 요청 (주소창·링크)
			{
				kContext.Response.ContentType = "text/html; charset=utf-8";

				await kContext.Response.WriteAsync(LoginHtml());

				return;
			}

			await kContext.Response.WriteAsJsonAsync(new { success = false, error = "인증이 필요합니다." });	// API·정적 파일 등
		});
	}

	private static bool IsLogin(HttpContext kContext)	// 인증 쿠키 유효: 발급 당시 계정 표시 = 현재 계정 (계정 변경: false)
	{
		string sStamp = GetLoginStamp();

		return sStamp != "" && IsSame(kContext.User.FindFirst(CLAIM_STAMP)?.Value ?? "", sStamp);
	}

	private static string GetLoginStamp()	// 계정 표시: 설정 Base:sID·Base:sPass 해시 (계정 미설정: "")
	{
		string sConfigID = HTConfig.GetString("Base:sID");
		string sConfigPass = HTConfig.GetString("Base:sPass");

		if (sConfigID == "" || sConfigPass == "")
		{
			return "";
		}

		return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(sConfigID + "\n" + sConfigPass)));
	}

	private static async Task<IResult> OnLogin(HttpRequest kRequest)	// 로그인: POST 폼 id·pass 가 Base:sID·Base:sPass 일치 시 인증 쿠키 발급
	{
		HttpContext kContext = kRequest.HttpContext;	// HttpContext 인자: RequestDelegate 로 바인딩되어 IResult 버려짐 → HttpRequest 인자
		IFormCollection? kForm = kRequest.HasFormContentType ? await kRequest.ReadFormAsync() : null;	// 폼 아님: 불일치 처리
		string sID = kForm?["id"].LastOrDefault() ?? "";
		string sPass = kForm?["pass"].LastOrDefault() ?? "";
		string sAddr = GetAddr(kContext);
		string sConfigID = HTConfig.GetString("Base:sID");
		string sConfigPass = HTConfig.GetString("Base:sPass");

		if (sConfigID == "" || sConfigPass == "" || !IsSame(sID, sConfigID) || !IsSame(sPass, sConfigPass))	// 계정 미설정·불일치 (비밀번호 로그 미기록)
		{
			HTLog.Write(WARN_, $"로그인 실패: {sID} ({sAddr})");

			await Task.Delay(1000);	// 실패 응답 지연 (무차별 대입 완화)

			return Results.Json(new { success = false, error = "아이디 또는 비밀번호가 올바르지 않습니다." });
		}

		ClaimsIdentity kIdentity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, sID), new Claim(CLAIM_STAMP, GetLoginStamp()) }, CookieAuthenticationDefaults.AuthenticationScheme);	// 아이디 (로그용) + 계정 표시

		await kContext.SignInAsync(new ClaimsPrincipal(kIdentity));	// 세션 쿠키 (브라우저 종료 시 삭제)

		HTLog.Write(INFO_, $"로그인 성공: {sID} ({sAddr})");

		return Results.Json(new { success = true });
	}

	private static bool UseStaticFile(WebApplication WebApp)	// 정적 파일: wwwroot 있을 때만 (index.html: "/" 보다 우선)
	{
		if (!Directory.Exists(Path.Combine(WebApp.Environment.ContentRootPath, "wwwroot")))
		{
			return false;
		}

		WebApp.UseDefaultFiles();
		WebApp.UseStaticFiles(new StaticFileOptions
		{
			OnPrepareResponse = kFile => kFile.Context.Response.Headers.CacheControl = "no-cache",	// 매 요청 재검증 (캐시 페이지가 로그인 검사 없이 표시 방지)
		});

		return true;
	}

	private void MapRoute(WebApplication WebApp, bool bAuth)	// 라우트 등록
	{
		WebApp.MapGet("/", OnHome);			// 기본 페이지
		WebApp.MapGet("/api/info", OnInfo);	// 서버 정보
		WebApp.Map("/api/echo", OnEcho);		// ? 뒤 문구 그대로 반환 (모든 메서드)
		WebApp.MapGet("/api/client", OnClient);	// 접속 클라 목록
		m_kSearch.MapRoute(WebApp);				// 서치 탭
		m_kDump.MapRoute(WebApp);				// 덤프 탭
		m_kFile.MapRoute(WebApp);				// 파일 탭
		m_kQuery.MapRoute(WebApp);				// 쿼리 탭

		if (bAuth)
		{
			WebApp.MapPost(LOGIN_PATH, OnLogin);
		}
	}

	private static IResult OnHome()	// 기본 페이지
	{
		return MakePage("main");
	}

	public static IResult MakePage(string sMode)	// 페이지 HTML (sMode: main 기본, view 파일 보기 새 창, field 필드 값 목록 새 창)
	{
		string sHtml = IndexHtml();

		sHtml = sHtml.Replace("__YESTERDAY__", GetNow().AddDays(-1).ToString("yyyy-MM-dd"));	// 시작일 기본값 = 어제 (KST)
		sHtml = sHtml.Replace("__TODAY__", GetNow().ToString("yyyy-MM-dd"));	// 종료일 기본값 = 오늘 (KST)
		sHtml = sHtml.Replace("__TITLE__", WebUtility.HtmlEncode(GetTitle()));
		sHtml = sHtml.Replace("__ZONE_COLOR__", GetTitleColor());
		sHtml = sHtml.Replace("__FAVICON__", GetFavicon());	// 탭 아이콘 (LM + 존 색)
		sHtml = sHtml.Replace("__MODE__", sMode);

		return Results.Content(sHtml, "text/html; charset=utf-8");
	}

	public static string GetQueryLink(string sQuery, DateTimeOffset kStart, DateTimeOffset kEnd)	// 쿼리 탭 바로 실행 주소 (새 창 복원 형식: 쿼리·S·E, D 해제, 웹 없음: "")
	{
		if (m_sLinkUrl == "")
		{
			return "";
		}

		Dictionary<string, string> hmapParam = new Dictionary<string, string>	// 페이지 RestoreState 입력칸 (OPEN_STATE_INPUT)
		{
			["sTab"]			= "QueryButton",
			["bQuery"]		= "1",
			["QueryRange"]	= "",
			["StartDate"]		= kStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
			["StartHour"]		= kStart.ToString("HH", CultureInfo.InvariantCulture),
			["StartMinute"]	= kStart.ToString("mm", CultureInfo.InvariantCulture),
			["EndDate"]		= kEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
			["EndHour"]		= kEnd.ToString("HH", CultureInfo.InvariantCulture),
			["EndMinute"]		= kEnd.ToString("mm", CultureInfo.InvariantCulture),
			["QueryKey"]		= sQuery,
		};

		return $"{m_sLinkUrl}/?{string.Join("&", hmapParam.Select(kPair => $"{kPair.Key}={Uri.EscapeDataString(kPair.Value)}"))}";
	}

	public static string GetTitle()	// 제목: LM + 설정 Base:sZone (예: LM Alpha, sZone 없음: LM)
	{
		return $"LM {HTConfig.GetString("Base:sZone")}".Trim();
	}

	private static string GetTitleColor()	// 제목 색: Base:sZone 별 (그 외: 기본 글자색)
	{
		switch (HTConfig.GetString("Base:sZone").ToLowerInvariant())
		{
			case "dev":
				return "#1a7f37";
			case "alpha":
				return "#2f6fde";
			case "real":
				return "#d1242f";
			default:
				return "inherit";
		}
	}

	private static string GetFavicon()	// 탭 아이콘 (SVG data URI): 제목 앞 이름 최대 3자 흰 글자 + 존 색 바탕
	{
		string sName	= GetTitle().Split(' ')[0];
		string sText	= System.Net.WebUtility.HtmlEncode((sName.Length > 3) ? sName[..3] : sName);
		string sColor	= GetTitleColor().StartsWith('#') ? GetTitleColor() : "#25292e";	// 존 없음: 검정
		string sFit		= (sText.Length > 1) ? $" textLength='{((sText.Length > 2) ? 30 : 29)}' lengthAdjust='spacingAndGlyphs'" : "";	// 칸 폭 꽉 채움 (16px 탭 가독성)
		string sSvg		= $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'><rect width='32' height='32' rx='6' fill='{sColor}'/><text x='16' y='24.5' text-anchor='middle' font-family='Arial,Segoe UI,sans-serif' font-size='24' font-weight='900' fill='#fff'{sFit}>{sText}</text></svg>";

		return "data:image/svg+xml," + Uri.EscapeDataString(sSvg);	// 글자 y 24.5: 대문자 기준선 → 세로 중앙
	}

	private object OnInfo()	// 서버 정보
	{
		return new
		{
			sMachine	= Environment.MachineName,
			sOs			= RuntimeInformation.OSDescription,
			sRuntime	= RuntimeInformation.FrameworkDescription,
			nPid		= Environment.ProcessId,
			sStartTime	= m_kStartTime.ToString("yyyy-MM-dd HH:mm:ss"),
			sUptime		= (GetNow() - m_kStartTime).ToString(@"d\.hh\:mm\:ss"),
			sTime		= GetNowText(),
			sTitle		= GetTitle(),		// 제목 (예: LM Alpha)
			sTitleColor	= GetTitleColor(),	// 제목 색 (sZone 별)
		};
	}

	private object OnClient()	// 접속 클라 목록
	{
		return new
		{
			nPort		= m_kListener.GetPort(),
			arrClient	= m_kListener.GetClientList(),
			sTime		= GetNowText(),
		};
	}

	public static async Task<(byte[]? arrData, long nSize, string sError)> RequestRead(ClientListener kListener, string sType, string sMachine, string sPath, long nOffset, int nCount)	// 조각 1개 요청 (sType: DumpRead·FileRead, 클라 바쁨 거절: 재요청 → 다운 중간 끊김 방지)
	{
		for (int nTry = 0; ; ++nTry)
		{
			(byte[]? arrData, long nSize, string sError) kChunk = await RequestReadOnce(kListener, sType, sMachine, sPath, nOffset, nCount);

			if (kChunk.arrData != null || kChunk.sError != m_sClientBusy || nTry >= m_nReadRetry)	// 받음·다른 오류·재요청 다 씀
			{
				return kChunk;
			}

			await Task.Delay(m_nReadRetryMs);
		}
	}

	private static async Task<(byte[]? arrData, long nSize, string sError)> RequestReadOnce(ClientListener kListener, string sType, string sMachine, string sPath, long nOffset, int nCount)	// 조각 요청 1번 → base64 해제 (오류: arrData null)
	{
		Dictionary<string, object> hmapRequest = new Dictionary<string, object>	// 경로 허용 검사: 클라
		{
			["sType"]	= sType,
			["sPath"]	= sPath,
			["nOffset"]	= nOffset,
			["nCount"]	= nCount,
		};

		(byte[]? arrResult, string sError) = await kListener.Request(sMachine, hmapRequest, m_nDumpWaitSec);

		if (arrResult == null)	// 미접속·시간 초과·끊김
		{
			return (null, 0, sError);
		}

		try
		{
			using JsonDocument kDoc = JsonDocument.Parse(arrResult);

			JsonElement	kRoot			= kDoc.RootElement;
			string		sResultError	= kRoot.GetProperty("sError").GetString() ?? "";	// 클라 오류 (허용 안 된 경로·읽기 실패)

			if (sResultError != "")
			{
				return (null, 0, sResultError);
			}

			return (kRoot.GetProperty("arrData").GetBytesFromBase64(), kRoot.GetProperty("nSize").GetInt64(), "");
		}
		catch (Exception)
		{
			return (null, 0, "결과 형식 오류");
		}
	}

	public static IResult MakeResult(string? sMachine, string sError, byte[]? arrResult)	// 응답 {sMachine, sError, kResult} (보기)
	{
		return Results.Stream((Stream kBody) => WriteResult(kBody, new { sMachine, sError }, arrResult), "application/json; charset=utf-8");
	}

	public static async Task WriteSearchLine(Stream kBody, int nIndex, string sMachine, string sError, byte[]? arrResult)	// 검색 결과 한 줄 {nIndex, sMachine, sError, kResult} + '\n' (바로 flush → 끝난 머신부터)
	{
		await WriteResult(kBody, new { nIndex, sMachine, sError }, arrResult);
		await kBody.WriteAsync(m_arrNewLine);
		await kBody.FlushAsync();
	}

	public static async Task WriteResult(Stream kBody, object kHead, byte[]? arrResult)	// {kHead…, "kResult": 클라 결과 바이트 그대로} (큰 결과 재파싱·직렬화 안 함)
	{
		byte[] arrHead = JsonSerializer.SerializeToUtf8Bytes(kHead, m_kJsonOption);

		await kBody.WriteAsync(arrHead.AsMemory(0, arrHead.Length - 1));	// 끝 '}' 빼고
		await kBody.WriteAsync(m_arrResultKey);
		await kBody.WriteAsync(arrResult ?? m_arrNull);
		await kBody.WriteAsync(m_arrClose);
	}

	public static string GetAddr(HttpContext kContext)	// 접속 IP (IIS: X-Forwarded-For 반영, IPv4 매핑 → IPv4)
	{
		IPAddress? kAddr = kContext.Connection.RemoteIpAddress;

		if (kAddr == null)
		{
			return "-";
		}

		return (kAddr.IsIPv4MappedToIPv6 ? kAddr.MapToIPv4() : kAddr).ToString();
	}

	private static bool IsSame(string sLeft, string sRight)	// 문자열 일치 (비교 시간 일정: 타이밍 공격 완화)
	{
		return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sLeft), Encoding.UTF8.GetBytes(sRight));
	}

	private static IResult OnEcho(HttpRequest kRequest)	// ? 뒤 문구 그대로 반환 (%XX 복원)
	{
		string sQuery = kRequest.QueryString.HasValue ? kRequest.QueryString.Value!.Substring(1) : "";	// '?' 제외

		return Results.Text(Uri.UnescapeDataString(sQuery), "text/plain; charset=utf-8");
	}

	private static string LoginHtml()	// 로그인 페이지 HTML (로그인 주소 = form action: ToolManager 경유 주소 변환 대응, 성공: 새로고침 → 요청 페이지)
	{
		string sTitle = System.Net.WebUtility.HtmlEncode(GetTitle());

		return $$"""
		<!doctype html>
		<html lang="ko">
		<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{{sTitle}} 로그인</title><link rel="icon" href="{{GetFavicon()}}">
		<style>
		:root{--bgColor-default:#ffffff;--bgColor-muted:#f6f8fa;--bgColor-emphasis:#25292e;--fgColor-default:#1f2328;--fgColor-danger:#d1242f;--fgColor-onEmphasis:#ffffff;--borderColor-default:#d1d9e0;--borderColor-muted:#d1d9e0b3;
		--button-primary-fgColor-rest:#ffffff;--button-primary-borderColor-rest:#1f232826;--shadow-inset:inset 0 1px 0 0 #1f23280a;--shadow-resting-small:0 1px 1px 0 #1f23280a,0 1px 2px 0 #1f232808;--focus-outlineColor:var(--zone,var(--bgColor-emphasis));--borderRadius-medium:0.375rem;
		--fontStack-sansSerif:"Mona Sans VF",-apple-system,BlinkMacSystemFont,"Segoe UI","Noto Sans Backtick Fix","Noto Sans",Helvetica,Arial,sans-serif,"Apple Color Emoji","Segoe UI Emoji";--zone:{{GetTitleColor()}};color-scheme:light}
		body{margin:0;padding:16px;background:var(--bgColor-default);color:var(--fgColor-default);font:400 0.875rem/1.5 var(--fontStack-sansSerif)}
		.panel{position:relative;top:30vh;transform:translateY(-30%);max-width:320px;margin:0 auto;padding:16px;border:1px solid var(--borderColor-muted);border-radius:var(--borderRadius-medium);background:var(--bgColor-muted)}
		h1{margin:0 0 16px;font-size:1.25rem;font-weight:600}label{display:block;margin:0 0 4px;font-weight:600}
		input{box-sizing:border-box;width:100%;height:32px;margin:0 0 16px;padding:0 12px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--bgColor-default);box-shadow:var(--shadow-inset);color:var(--fgColor-default);font:inherit}
		input:focus{border-color:var(--focus-outlineColor);outline:none;box-shadow:inset 0 0 0 1px var(--focus-outlineColor)}
		button{box-sizing:border-box;width:100%;height:32px;padding:0 12px;border:1px solid var(--button-primary-borderColor-rest);border-radius:var(--borderRadius-medium);background:var(--zone,var(--bgColor-emphasis));box-shadow:var(--shadow-resting-small);color:var(--button-primary-fgColor-rest);font:500 0.875rem/1 var(--fontStack-sansSerif);cursor:pointer}
		button:focus-visible{outline:2px solid var(--focus-outlineColor);outline-offset:-2px;box-shadow:inset 0 0 0 3px var(--fgColor-onEmphasis)}button:disabled{opacity:.6;cursor:default}
		.err{min-height:20px;margin:0 0 8px;color:var(--fgColor-danger)}
		</style>
		</head>
		<body>
		<form class="panel" id="login" method="post" action="{{LOGIN_PATH}}">
		<h1 style="color:{{GetTitleColor()}}">{{sTitle}}</h1>
		<label for="id">아이디</label><input id="id" autocomplete="username" autofocus>
		<label for="pass">비밀번호</label><input id="pass" type="password" autocomplete="current-password">
		<p class="err" id="err"></p>
		<button id="btn">로그인</button>
		</form>
		<script>
		document.getElementById('login').addEventListener('submit', async kEvent => {
			kEvent.preventDefault();

			const kId = document.getElementById('id'), kPass = document.getElementById('pass'), kErr = document.getElementById('err'), kBtn = document.getElementById('btn');

			if (kPass.value === '') {	// 아이디 Enter: 비밀번호로 이동
				kPass.focus();

				return;
			}

			kBtn.disabled = true;

			try {
				const kRes = await fetch(kEvent.target.action, { method: 'POST', body: new URLSearchParams({ id: kId.value.trim(), pass: kPass.value }) });
				const kData = await kRes.json();

				if (kData.success) {	// 새로고침 → 요청 페이지
					location.reload();

					return;
				}

				kErr.textContent = kData.error || '로그인 실패';
				kPass.select();
			} catch (kError) {
				kErr.textContent = '오류: ' + kError;
			}

			kBtn.disabled = false;
		});
		</script>
		</body>
		</html>
		""";
	}

	private static string IndexHtml()    // 기본 페이지 HTML (데이터: textContent 로만 표시)
	{
		return """
		<!doctype html>
		<html lang="ko">
		<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>__TITLE__</title><link rel="icon" href="__FAVICON__">
		<style>
		:root{--bgColor-default:#ffffff;--bgColor-muted:#f6f8fa;--bgColor-accent-muted:#ddf4ff;--bgColor-accent-emphasis:#0969da;--fgColor-default:#1f2328;--fgColor-muted:#59636e;--fgColor-accent:#0969da;--fgColor-success:#1a7f37;--fgColor-danger:#d1242f;--fgColor-disabled:#818b98;--bgColor-emphasis:#25292e;--bgColor-neutral-emphasis:#59636e;--borderColor-emphasis:#818b98;--chart-barColor:#afb8c1;--fgColor-onEmphasis:#ffffff;
		--borderColor-default:#d1d9e0;--borderColor-disabled:#818b981a;--borderColor-accent-emphasis:#0969da;--button-default-fgColor-rest:#25292e;--button-default-bgColor-rest:#f6f8fa;--button-default-bgColor-hover:#eff2f5;--button-default-bgColor-active:#e6eaef;--button-default-bgColor-selected:#e6eaef;--button-default-bgColor-disabled:#eff2f5;--button-default-shadow-resting:0 1px 0 0 #1f23280a;
		--button-primary-fgColor-rest:var(--zone-primary-fg,var(--button-default-fgColor-rest));--button-primary-fgColor-disabled:var(--zone-primary-fg-disabled,var(--fgColor-disabled));--zone-primary-hover:color-mix(in srgb,var(--zone) 90%,#000);--zone-primary-active:color-mix(in srgb,var(--zone) 80%,#000);--zone-primary-disabled:color-mix(in srgb,var(--zone) 45%,#fff);--zone-primary-fg:color-mix(in srgb,var(--zone) 0%,#fff);--zone-primary-fg-disabled:color-mix(in srgb,var(--zone) 0%,#ffffffcc);--zone-primary-border:color-mix(in srgb,var(--zone) 0%,#1f232826);--button-primary-bgColor-rest:var(--zone,var(--button-default-bgColor-rest));--button-primary-bgColor-hover:var(--zone-primary-hover,var(--button-default-bgColor-hover));--button-primary-bgColor-active:var(--zone-primary-active,var(--button-default-bgColor-active));--button-primary-bgColor-disabled:var(--zone-primary-disabled,var(--button-default-bgColor-disabled));--button-primary-borderColor-rest:var(--zone-primary-border,var(--borderColor-default));--button-primary-shadow-selected:inset 0 1px 0 0 #002d114d;
		--button-danger-fgColor-rest:var(--zone,#d1242f);--button-danger-fgColor-hover:#ffffff;--button-danger-fgColor-disabled:color-mix(in srgb,var(--zone,#d1242f) 50%,transparent);--button-danger-bgColor-hover:var(--zone,#cf222e);--borderColor-muted:#d1d9e0b3;--control-transparent-bgColor-hover:#818b981a;--control-transparent-bgColor-selected:#818b9826;--treeViewItem-leadingVisual-iconColor-rest:#54aeff;--highlight-neutral-bgColor:#fff8c5;
		--overlay-bgColor:#ffffff;--overlay-backdrop-bgColor:#c8d1da66;--shadow-inset:inset 0 1px 0 0 #1f23280a;--shadow-resting-small:0 1px 1px 0 #1f23280a,0 1px 2px 0 #1f232808;--shadow-floating-small:0 0 0 1px #d1d9e040,0 6px 12px -3px #25292e0a,0 6px 18px 0 #25292e1f;--focus-outlineColor:var(--zone,var(--bgColor-emphasis));--borderRadius-medium:0.375rem;--borderRadius-large:0.75rem;
		--fontStack-sansSerif:"Mona Sans VF",-apple-system,BlinkMacSystemFont,"Segoe UI","Noto Sans Backtick Fix","Noto Sans",Helvetica,Arial,sans-serif,"Apple Color Emoji","Segoe UI Emoji";--fontStack-monospace:ui-monospace,SFMono-Regular,SF Mono,Menlo,Consolas,Liberation Mono,monospace;--zone:__ZONE_COLOR__;--split:var(--bgColor-emphasis);color-scheme:light}
		body{margin:0;padding:16px;min-width:560px;background:var(--bgColor-default);color:var(--fgColor-default);font:400 0.875rem/1.5 var(--fontStack-sansSerif)}
		h1{margin:0 0 16px;padding:8px 16px;border:1px solid var(--zone,var(--borderColor-default));border-radius:var(--borderRadius-medium);background:var(--bgColor-default);font-size:1.25rem;font-weight:600}.panel{padding:16px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--bgColor-default)}
		p{margin:12px 0 0;color:var(--fgColor-muted)}
		#ResultPanel.query #ResultTitle,#ResultPanel.query #ResultPager,#ResultPanel.query .scrollwrap,#ResultPanel:not(.query) #QueryTitle,#ResultPanel:not(.query) #QueryChart{display:none}#QueryScroll{height:480px;container-type:inline-size}#QueryPrev,#QueryNext,#QueryDepth,#ViewPrev,#ViewNext,#ViewShift{width:28px;padding:0}#QueryDepth,#ViewShift{width:auto;min-width:28px;padding:0 6px}#ViewShift{pointer-events:none}#QueryDepth,#ViewShift{font-variant-numeric:tabular-nums}#QueryDepth{cursor:text}#QueryDepthInput{box-sizing:border-box;padding:0 4px;text-align:center;font-variant-numeric:tabular-nums}#QueryPrev:focus-visible,#QueryNext:focus-visible,#QueryDepth:focus-visible,#ViewPrev:focus-visible,#ViewNext:focus-visible,#ViewShift:focus-visible{outline:none;box-shadow:var(--shadow-resting-small)}.chartbars.click>div{cursor:pointer}#FilterPanel:not(.query) .queryonly,#FilterPanel.query .searchonly,#FilterPanel.query .pickwrap{display:none}#ResultPanel{position:relative}#QueryRange{min-width:0;width:64px}.pickkey{display:inline-block;width:14px;text-align:center}#QueryRunButton,#SearchButton{min-width:52px}#QueryTable col:not(:last-child){width:1px}#QueryTable th{white-space:nowrap}#QueryBody tr.row>td:not(.log){cursor:pointer}td.fileact{white-space:nowrap}.qdown{display:inline-flex;vertical-align:-3px;cursor:pointer}.qdel{flex:none;display:inline-flex;vertical-align:-3px;width:16px;height:16px;margin-left:4px;padding:0;border:0;background:transparent;box-shadow:none;color:var(--button-danger-fgColor-rest);font-size:0;cursor:pointer;--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M11 1.75V3h2.25a.75.75 0 0 1 0 1.5H2.75a.75.75 0 0 1 0-1.5H5V1.75C5 .784 5.784 0 6.75 0h2.5C10.216 0 11 .784 11 1.75ZM4.496 6.675l.66 6.6a.25.25 0 0 0 .249.225h5.19a.25.25 0 0 0 .249-.225l.66-6.6a.75.75 0 0 1 1.492.149l-.66 6.6A1.748 1.748 0 0 1 10.595 15h-5.19a1.75 1.75 0 0 1-1.741-1.575l-.66-6.6a.75.75 0 1 1 1.492-.15ZM6.5 1.75V3h3V1.75a.25.25 0 0 0-.25-.25h-2.5a.25.25 0 0 0-.25.25Z'/%3E%3C/svg%3E")}.qdel::before{content:"";flex:none;width:16px;height:16px;background:currentColor;-webkit-mask:var(--icon) center/16px no-repeat;mask:var(--icon) center/16px no-repeat}.qdel:hover:not(:disabled){background:var(--button-danger-bgColor-hover);color:var(--button-danger-fgColor-hover)}.qdown.nozip{color:var(--fgColor-muted)}.qdown:disabled,.qdel:disabled{opacity:0.4;cursor:default}#DumpPanel{position:relative}#QueryBody tr.open{background:var(--bgColor-accent-muted)}#QueryBody tr.detail{cursor:default;background:var(--bgColor-muted)}#QueryBody tr.detail>td{overflow:visible;white-space:normal}#QueryBody tr.detail>td>div{position:sticky;left:9px;box-sizing:border-box;width:calc(100cqw - 18px)}.querymeta,.querytext{font-family:var(--fontStack-monospace);word-break:break-all}.querymeta{padding:4px 0 0;color:var(--fgColor-muted)}.queryfield{display:flex;flex-wrap:wrap;column-gap:6px}.queryfield>span{min-width:0}.queryfield>span:not(:last-child)::after{content:" ·"}.querytext{padding:4px 0;white-space:pre-wrap}
		#QueryChart{position:relative;display:grid;grid-template-columns:auto minmax(0,1fr);column-gap:8px;margin-top:8px;color:var(--fgColor-muted);font-size:0.75rem;line-height:1}.charty{position:relative;height:var(--chartHeight,120px);min-width:24px}.charty span{position:absolute;right:0;transform:translateY(50%);white-space:nowrap}.chartplot{position:relative;height:var(--chartHeight,120px);border-bottom:1px solid var(--borderColor-default)}.chartgrid{position:absolute;left:0;right:0;border-top:1px solid var(--borderColor-muted)}
		.chartbars,.chartx{display:flex;gap:2px}.chartbars{position:absolute;inset:0;align-items:flex-end}.chartbars>div{flex:1;min-width:0;height:100%;display:flex;align-items:flex-end}.chartbars>div:hover{background:var(--control-transparent-bgColor-hover)}.chartbars>div>div{width:100%;border-radius:4px 4px 0 0;background:color-mix(in srgb,var(--zone,var(--chart-barColor)) 50%,#fff)}.chartx{grid-column:2;height:12px;margin-top:4px;overflow:hidden}.chartx span{flex:1;min-width:0;white-space:nowrap;text-align:center}.chartx span.month{color:var(--zone,var(--fgColor-default));font-weight:600}
		.charttip{position:absolute;z-index:3;padding:4px 8px;border-radius:var(--borderRadius-medium);background:var(--overlay-bgColor);box-shadow:var(--shadow-floating-small);color:var(--fgColor-default);font-weight:600;white-space:nowrap;pointer-events:none;transform:translate(-50%,calc(-100% - 4px))}
		.querysplit{position:relative;margin-top:8px;padding-left:calc(var(--fieldWidth,120px) + 14px)}#QueryFieldSplitter{position:absolute;top:0;bottom:0;left:var(--fieldWidth,120px)}.querysplit>.scroll{margin-top:0}#QueryField{position:absolute;top:0;bottom:0;left:0;width:var(--fieldWidth,120px);box-sizing:border-box;overflow-y:auto;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);font-size:0.75rem}.fieldhead{position:sticky;top:0;z-index:1;display:flex;align-items:center;justify-content:space-between;gap:8px;padding:6px 8px;border-bottom:1px solid var(--borderColor-muted);background:var(--bgColor-default);font-weight:600}#QueryFieldList,#QueryFieldList ul{margin:0;padding:0;list-style:none}#QueryFieldList button{display:flex;align-items:flex-start;justify-content:space-between;gap:8px;width:100%;height:auto;min-height:28px;padding:6px 8px;border:0;border-radius:0;background:transparent;box-shadow:none;color:var(--fgColor-default);font-size:inherit;font-weight:400;text-align:left;white-space:normal}#QueryFieldList button:hover:not(:disabled){background:var(--control-transparent-bgColor-hover)}#QueryFieldList .fieldname{font-weight:600}#QueryFieldList li.baseend{border-bottom:1px solid var(--borderColor-muted)}#QueryFieldList button>span:first-child{flex:1;min-width:0;white-space:normal;word-break:break-all;overflow-wrap:anywhere}#QueryFieldList .count{flex:none;color:var(--fgColor-muted);font-variant-numeric:tabular-nums}
		#QuerySaveDialog{min-width:600px}#QuerySaveDialog .setting{grid-template-columns:max-content minmax(0,1fr);align-items:start}#QuerySaveDialog .setting label{line-height:28px}#QuerySaveDialog .setting input{width:100%}.savedlist{max-height:360px;overflow-y:auto;margin:16px 0}#QueryEditTable td{border:0;border-bottom:1px solid var(--borderColor-muted);padding:6px 4px;vertical-align:middle}#QueryEditTable td:first-child{max-width:0;width:100%;text-overflow:ellipsis;cursor:grab}#QueryEditTable td.act{width:1px}#QueryEditTable tr.off td:first-child{color:var(--fgColor-muted)}#QueryEditTable tr.drag{opacity:.5}#QueryEditTable tr.dropbefore td{box-shadow:inset 0 2px 0 var(--zone,var(--bgColor-emphasis))}#QueryEditTable tr.dropafter td{box-shadow:inset 0 -2px 0 var(--zone,var(--bgColor-emphasis))}#QuerySaveDialog:not(.alarm) .alarmrow,#QuerySaveDialog.alarm .queryrow{display:none}#QuerySaveDialog.alarm #QuerySaveQuery{height:96px}#QuerySaveDialog span.alarmrow,#QuerySaveDialog span.queryrow{display:flex;align-items:center;gap:8px;line-height:28px}#QuerySaveDialog label.delay{margin-left:16px}#AlarmTest{margin-right:auto}#QuerySaveDialog span.alarmadd{flex-direction:column;align-items:stretch;gap:4px}.alarminput{display:flex;align-items:center;gap:8px}.alarmlist{display:flex;flex-direction:column;max-height:125px;overflow-y:auto}.alarmlist:empty{display:none}.alarmitem{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:2px 4px 2px 8px;border-bottom:1px solid var(--borderColor-muted);line-height:20px;word-break:break-all}.alarmitem button{flex:none;height:20px;padding:0 8px;font-size:0.75rem}#QuerySaveDialog .setting span.alarmrow input[type="text"]{flex:1;width:auto}#QuerySaveDialog .setting input[type="number"]{width:80px}#QuerySaveDialog .setting input[type="checkbox"]{width:auto;margin:0}#QuerySaveQuery{box-sizing:border-box;width:100%;padding:6px 8px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--bgColor-default);box-shadow:var(--shadow-inset);color:var(--fgColor-default);font-family:var(--fontStack-monospace);font-size:0.75rem;line-height:1.5;resize:vertical}#QuerySaveDialog .toolright{justify-content:flex-end}#QueryFieldDialog{min-width:360px}#QueryFieldDialog .setting{grid-template-columns:auto 280px}#QueryFieldDialog .setting input{width:100%}#QueryFieldDialog .toolright{justify-content:flex-end}
		h1{display:flex;flex-wrap:wrap;align-items:center;gap:4px 12px}#TabBar{display:flex;flex-wrap:wrap;align-items:center;gap:4px 12px;margin-left:auto}body:not([data-mode="main"]) #TabBar{display:none}
		.panel+.panel{margin-top:16px}.filter{display:flex;flex-wrap:wrap;align-items:center;gap:8px 12px}.filter label{display:flex;flex-wrap:wrap;align-items:center;gap:8px}
		select,input{box-sizing:border-box;height:28px;padding:0 8px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background-color:var(--bgColor-default);box-shadow:var(--shadow-inset);color:var(--fgColor-default);font:inherit;font-size:0.75rem}select{min-width:160px;padding-right:24px;background:var(--bgColor-default) url("data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iMTYiIGhlaWdodD0iMTYiIHZpZXdCb3g9IjAgMCAxNiAxNiIgZmlsbD0iIzU4NjA2OSIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj48cGF0aCBkPSJNNC40MjcgOS40MjdsMy4zOTYgMy4zOTZhLjI1MS4yNTEgMCAwMC4zNTQgMGwzLjM5Ni0zLjM5NkEuMjUuMjUgMCAwMDExLjM5NiA5SDQuNjA0YS4yNS4yNSAwIDAwLS4xNzcuNDI3ek00LjQyMyA2LjQ3TDcuODIgMy4wNzJhLjI1LjI1IDAgMDEuMzU0IDBMMTEuNTcgNi40N2EuMjUuMjUgMCAwMS0uMTc3LjQyN0g0LjZhLjI1LjI1IDAgMDEtLjE3Ny0uNDI3eiIgLz48L3N2Zz4=") no-repeat right 4px center/16px;appearance:none}select.hour{min-width:0}.pickwrap{display:flex;align-items:center;gap:8px}#MachinePick{position:relative}#MachineBox{display:block;width:64px;overflow:hidden;text-overflow:ellipsis;line-height:26px;text-align:left;padding:0 24px 0 8px;background:var(--bgColor-default) url("data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iMTYiIGhlaWdodD0iMTYiIHZpZXdCb3g9IjAgMCAxNiAxNiIgZmlsbD0iIzU4NjA2OSIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj48cGF0aCBkPSJNNC40MjcgOS40MjdsMy4zOTYgMy4zOTZhLjI1MS4yNTEgMCAwMC4zNTQgMGwzLjM5Ni0zLjM5NkEuMjUuMjUgMCAwMDExLjM5NiA5SDQuNjA0YS4yNS4yNSAwIDAwLS4xNzcuNDI3ek00LjQyMyA2LjQ3TDcuODIgMy4wNzJhLjI1LjI1IDAgMDEuMzU0IDBMMTEuNTcgNi40N2EuMjUuMjUgMCAwMS0uMTc3LjQyN0g0LjZhLjI1LjI1IDAgMDEtLjE3Ny0uNDI3eiIgLz48L3N2Zz4=") no-repeat right 4px center/16px;box-shadow:var(--shadow-inset);color:var(--fgColor-default);font-weight:400}#MachineMenu{position:absolute;top:calc(100% + 4px);left:0;z-index:20;width:max-content;min-width:100%;max-height:min(480px,70vh);overflow:auto;padding:4px 0;border-radius:var(--borderRadius-large);background:var(--overlay-bgColor);box-shadow:var(--shadow-floating-small)}#MachineMenu label{display:flex;align-items:center;gap:8px;padding:4px 12px;font-size:0.75rem;white-space:nowrap;cursor:pointer}#MachineMenu label:hover{background:var(--control-transparent-bgColor-hover)}#MachineMenu label.all{border-bottom:1px solid var(--borderColor-muted);font-weight:600}#MachineMenu input{flex:none;width:auto;height:auto;margin:0;padding:0;box-shadow:none}
		select:focus,input:focus{border-color:var(--focus-outlineColor);outline:none;box-shadow:inset 0 0 0 1px var(--focus-outlineColor)}:focus-visible{outline:2px solid var(--focus-outlineColor);outline-offset:-2px}.octicon{flex:none;fill:var(--fgColor-muted)}#SettingButton{width:28px;padding:0}
		button{box-sizing:border-box;display:inline-flex;align-items:center;justify-content:center;gap:4px;height:28px;padding:0 8px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium);background:var(--button-default-bgColor-rest);box-shadow:var(--button-default-shadow-resting);color:var(--button-default-fgColor-rest);font:500 0.75rem/1 var(--fontStack-sansSerif);white-space:nowrap;cursor:pointer;user-select:none}
		button:hover:not(:disabled){background:var(--button-default-bgColor-hover)}button:active:not(:disabled){background:var(--button-default-bgColor-active)}button:disabled{border-color:var(--borderColor-disabled);background:var(--button-default-bgColor-disabled);box-shadow:none;color:var(--fgColor-disabled);cursor:not-allowed}.filter+.filter{margin-top:10px}#SearchKey,#QueryKey{flex:1;min-width:135px}.searchbox{display:flex;align-items:center;gap:8px;flex:1}
		button.primary{border-color:var(--button-primary-borderColor-rest);background:var(--button-primary-bgColor-rest);box-shadow:var(--shadow-resting-small);color:var(--button-primary-fgColor-rest)}button.primary:hover:not(:disabled){background:var(--button-primary-bgColor-hover)}
		button.primary:active:not(:disabled){border-color:var(--button-primary-borderColor-rest);background:var(--button-primary-bgColor-active);box-shadow:var(--button-primary-shadow-selected);color:var(--button-primary-fgColor-rest)}button.primary:disabled{border-color:var(--button-primary-bgColor-disabled);background:var(--button-primary-bgColor-disabled);box-shadow:none;color:var(--button-primary-fgColor-disabled)}
		button.danger,td.act button[data-action="delete"]{color:var(--button-danger-fgColor-rest)}button.danger:hover:not(:disabled),td.act button[data-action="delete"]:hover:not(:disabled){border-color:var(--button-primary-borderColor-rest);background:var(--button-danger-bgColor-hover);box-shadow:var(--shadow-resting-small);color:var(--button-danger-fgColor-hover)}button.danger:disabled,td.act button[data-action="delete"]:disabled{color:var(--button-danger-fgColor-disabled)}
		.scrollwrap{position:relative}.progress{position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);z-index:2;padding:12px 24px;border-radius:var(--borderRadius-large);background:var(--overlay-bgColor);box-shadow:var(--shadow-floating-small);font-weight:600;line-height:1.7;text-align:center;white-space:pre-line;pointer-events:none}
		.title{font-weight:600;word-break:break-all}.scroll{position:relative;overflow:auto;margin-top:8px;border:1px solid var(--borderColor-default);border-radius:var(--borderRadius-medium)}.hscroll{position:sticky;bottom:0;z-index:2;overflow-x:auto;overflow-y:hidden;margin-top:-16px;border:1px solid var(--borderColor-default);border-radius:0 0 var(--borderRadius-medium) var(--borderRadius-medium);background:var(--bgColor-default)}.hscroll>div{height:1px}#ResultScroll{height:135px}#ViewScroll{height:480px}
		.scroll::-webkit-scrollbar,.hscroll::-webkit-scrollbar{width:15px;height:15px}.scroll::-webkit-scrollbar-track,.hscroll::-webkit-scrollbar-track,.scroll::-webkit-scrollbar-corner{background:transparent}.scroll::-webkit-scrollbar-thumb,.hscroll::-webkit-scrollbar-thumb{border:0 solid transparent;background:var(--borderColor-emphasis) padding-box}.scroll::-webkit-scrollbar-thumb:vertical{border-width:0 4px;border-radius:7.5px/3.5px}.scroll::-webkit-scrollbar-thumb:horizontal,.hscroll::-webkit-scrollbar-thumb:horizontal{border-width:4px 0;border-radius:3.5px/7.5px}.scroll::-webkit-scrollbar-thumb:hover,.scroll::-webkit-scrollbar-thumb:active,.hscroll::-webkit-scrollbar-thumb:hover,.hscroll::-webkit-scrollbar-thumb:active{background-color:var(--fgColor-muted)}
		.scroll::-webkit-scrollbar-button:single-button,.hscroll::-webkit-scrollbar-button:single-button{display:block;background:no-repeat}.scroll::-webkit-scrollbar-button:single-button:vertical{width:15px;height:13px;background-size:7px 5px}.scroll::-webkit-scrollbar-button:single-button:horizontal,.hscroll::-webkit-scrollbar-button:single-button:horizontal{width:13px;height:15px;background-size:5px 7px}
		.scroll::-webkit-scrollbar-button:single-button:vertical:decrement{background-position:4px 4px;background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 7 5'%3E%3Cpath fill='%23818b98' stroke='%23818b98' stroke-linejoin='round' d='M3.5 .5l3 4h-6z'/%3E%3C/svg%3E")}.scroll::-webkit-scrollbar-button:single-button:vertical:increment{background-position:4px 4px;background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 7 5'%3E%3Cpath fill='%23818b98' stroke='%23818b98' stroke-linejoin='round' d='M.5 .5h6l-3 4z'/%3E%3C/svg%3E")}.scroll:has(>table)::-webkit-scrollbar-button:single-button:vertical:decrement{height:39px;border-top-right-radius:5px;background:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 7 5'%3E%3Cpath fill='%23818b98' stroke='%23818b98' stroke-linejoin='round' d='M3.5 .5l3 4h-6z'/%3E%3C/svg%3E") 4px 30px/7px 5px no-repeat,linear-gradient(var(--bgColor-muted) 25px,var(--borderColor-default) 25px 26px,transparent 26px)}
		.scroll::-webkit-scrollbar-button:single-button:horizontal:decrement,.hscroll::-webkit-scrollbar-button:single-button:horizontal:decrement{background-position:4px 4px;background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 5 7'%3E%3Cpath fill='%23818b98' stroke='%23818b98' stroke-linejoin='round' d='M.5 3.5l4-3v6z'/%3E%3C/svg%3E")}.scroll::-webkit-scrollbar-button:single-button:horizontal:increment,.hscroll::-webkit-scrollbar-button:single-button:horizontal:increment{background-position:4px 4px;background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 5 7'%3E%3Cpath fill='%23818b98' stroke='%23818b98' stroke-linejoin='round' d='M.5 .5l4 3-4 3z'/%3E%3C/svg%3E")}
		.panel>.resulthead:first-child,#ViewPanel>.filter:first-child{margin:-16px -16px 8px;padding:8px 16px;border-bottom:1px solid var(--borderColor-default);border-radius:calc(var(--borderRadius-medium) - 1px) calc(var(--borderRadius-medium) - 1px) 0 0;background:var(--bgColor-muted)}.resulthead .toolright button,#ViewPanel>.filter .toolright button{height:28px;padding:0 8px;gap:4px;font-size:0.75rem}
		.splitter{display:flex;align-items:center;height:14px;cursor:ns-resize;touch-action:none}.splitter::before{content:"";flex:1;height:4px;border-radius:2px;background:var(--borderColor-default)}.splitter:hover::before,.splitter.drag::before{background:var(--split)}.splitter+.panel{margin-top:0}
		body[data-mode="view"] #FilterPanel,body[data-mode="view"] #FilterSplitter,body[data-mode="view"] #ResultPanel,body[data-mode="view"] #Splitter{display:none}body[data-mode="view"] #ViewPanel{margin-top:0}body[data-mode="view"] #ViewScroll{height:calc(100vh - 161px)}
		table{border-collapse:collapse;width:100%;font-size:0.75rem}.scroll>table{border-style:hidden hidden none hidden}.scroll>table th{border-bottom-style:hidden}th,td{border:1px solid var(--borderColor-default);padding:4px 8px;text-align:left;vertical-align:top}th{position:sticky;top:0;z-index:1;background:var(--bgColor-muted);box-shadow:inset 0 -1px 0 var(--borderColor-default);color:var(--fgColor-muted);font-weight:600}
		td.num{text-align:right;white-space:nowrap}td.nowrap{white-space:nowrap}td.log{font-family:var(--fontStack-monospace);white-space:nowrap}
		.colgrip{position:absolute;top:0;right:0;bottom:0;width:6px;cursor:col-resize;touch-action:none}.colgrip::after{content:"";position:absolute;top:0;right:0;bottom:0;width:2px}.colgrip:hover::after,.colgrip.drag::after{background:var(--split)}.colguide{position:fixed;z-index:10;width:2px;margin-left:-1px;background:var(--split);pointer-events:none}
		table.fixed{table-layout:fixed}table.fixed th,table.fixed td{overflow:hidden;white-space:nowrap}table.fixed td.log{text-overflow:ellipsis}
		html{overflow-y:scroll}#TabBar button.cur,#TabBar button.cur:hover:not(:disabled),#QuerySettingTab button.cur,#QuerySettingTab button.cur:hover:not(:disabled){border-color:var(--zone,var(--bgColor-emphasis));background:var(--zone,var(--bgColor-emphasis));box-shadow:none;color:var(--fgColor-onEmphasis)}#HelpButton,#HelpButton:hover:not(:disabled){color:var(--zone,var(--fgColor-default))}#DumpScroll{height:calc(100vh - 286px);min-height:200px}
		#ViewBody,#FileViewBody,#ViewBody .lno,#FileViewBody .lno{color:#000}#ViewScroll{overflow-anchor:none}
		body[data-mode="field"] #FilterPanel,body[data-mode="field"] #FilterSplitter,body[data-mode="field"] #ResultPanel,body[data-mode="field"] #Splitter,body[data-mode="field"] #ViewPanel{display:none}body[data-mode="field"] #QueryFieldPanel{margin-top:0}body[data-mode="field"] #QueryFieldScroll{max-height:calc(100vh - 197px)}#QueryFieldTable col:not(:last-child){width:1px}#QueryFieldTable th{white-space:nowrap}#QueryFieldTable td.log{white-space:nowrap}#QueryFieldBody tr.row{cursor:pointer}#QueryFieldPager,#ResultPager,#QueryPager,#QueryFieldPager{justify-content:center;margin-top:8px}#ResultPager,#QueryPager,#QueryFieldPager{gap:2px;margin:1px 0 -15px}#ResultPager button,#QueryPager button,#QueryFieldPager button{height:20px;min-width:20px;padding:0 6px;font-size:0.75rem}
		#FilterPanel[hidden]~#DumpPanel #DumpScroll,#FilterPanel[hidden]~#FilePanel .filesplit{height:calc(100vh - 161px)}
		.filesplit{display:flex;margin-top:8px;height:calc(100vh - 286px);min-height:200px}#FileScroll{flex:none;width:26%;min-width:120px;margin-top:0}.fileview{flex:1;min-width:120px;display:flex;flex-direction:column}#FileViewScroll{flex:1;margin-top:0;overflow-anchor:none}
		#FileTitle{flex:none;width:calc(26% + 2px)}#FileViewTitle{flex:1;min-width:0}
		.vsplitter{flex:none;display:flex;justify-content:center;width:14px;cursor:ew-resize;touch-action:none}.vsplitter::before{content:"";width:4px;border-radius:2px;background:var(--borderColor-default)}.vsplitter:hover::before,.vsplitter.drag::before{background:var(--split)}
		.tree{margin:0;padding:8px;list-style:none}.tree ul{margin:0;padding-left:16px;list-style:none}.tree .node{position:relative;display:flex;align-items:center;gap:4px;min-height:2rem;padding:0 8px;border-radius:var(--borderRadius-medium);cursor:pointer;white-space:nowrap}.tree .node[data-kind="file"]{cursor:default}.tree .node.view{cursor:pointer}
		.tree .node:hover{background:var(--control-transparent-bgColor-hover)}.tree .node.sel{background:var(--control-transparent-bgColor-selected)}.tree .node.sel::after{content:"";position:absolute;top:calc(50% - 12px);left:-8px;width:4px;height:24px;border-radius:var(--borderRadius-medium);background:var(--borderColor-accent-emphasis)}
		.tree .label{display:flex;align-items:center;gap:8px}.tree .tog,.tree .ticon,.qdown::before{flex:none;width:16px;height:16px;font-size:0;background:currentColor;-webkit-mask:var(--icon) center/16px no-repeat;mask:var(--icon) center/16px no-repeat}.tree .tog:empty{visibility:hidden}.tree .info{margin-left:8px;color:var(--fgColor-muted);font-size:0.75rem}.tree .msg{padding:0 8px 0 28px;line-height:2rem;color:var(--fgColor-muted)}
		.tree .tog{color:var(--fgColor-muted);--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M6.22 3.22a.75.75 0 0 1 1.06 0l4.25 4.25a.75.75 0 0 1 0 1.06l-4.25 4.25a.751.751 0 0 1-1.042-.018.751.751 0 0 1-.018-1.042L9.94 8 6.22 4.28a.75.75 0 0 1 0-1.06Z'/%3E%3C/svg%3E")}
		.tree li:has(>ul)>.node>.tog{--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M12.78 5.22a.749.749 0 0 1 0 1.06l-4.25 4.25a.749.749 0 0 1-1.06 0L3.22 6.28a.749.749 0 1 1 1.06-1.06L8 8.939l3.72-3.719a.749.749 0 0 1 1.06 0Z'/%3E%3C/svg%3E")}
		.tree .ticon{color:var(--treeViewItem-leadingVisual-iconColor-rest);--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M1.75 1A1.75 1.75 0 0 0 0 2.75v10.5C0 14.216.784 15 1.75 15h12.5A1.75 1.75 0 0 0 16 13.25v-8.5A1.75 1.75 0 0 0 14.25 3H7.5a.25.25 0 0 1-.2-.1l-.9-1.2C6.07 1.26 5.55 1 5 1H1.75Z'/%3E%3C/svg%3E")}
		
		.qdown{flex:none;width:16px;height:16px;padding:0;border:0;background:transparent;box-shadow:none;color:var(--fgColor-accent);font-size:0;--icon:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M2.75 14A1.75 1.75 0 0 1 1 12.25v-2.5a.75.75 0 0 1 1.5 0v2.5c0 .138.112.25.25.25h10.5a.25.25 0 0 0 .25-.25v-2.5a.75.75 0 0 1 1.5 0v2.5A1.75 1.75 0 0 1 13.25 14Z'/%3E%3Cpath d='M7.25 7.689V2a.75.75 0 0 1 1.5 0v5.689l1.97-1.969a.749.749 0 1 1 1.06 1.06l-3.25 3.25a.749.749 0 0 1-1.06 0L4.22 6.78a.749.749 0 1 1 1.06-1.06l1.97 1.969Z'/%3E%3C/svg%3E")}.qdown::before{content:""}.qdown:hover:not(:disabled){background:var(--control-transparent-bgColor-hover)}
		#ResultTable col:not(:last-child),#DumpTable col:not(:nth-last-child(2)){width:1px}#DumpTable tr>:last-child{position:sticky;right:0;background:var(--bgColor-default);box-shadow:inset 1px 0 0 var(--borderColor-default)}#DumpTable tbody tr:hover>:last-child{background:var(--bgColor-muted)}#DumpTable th:last-child{z-index:2;background:var(--bgColor-muted);box-shadow:inset 1px 0 0 var(--borderColor-default),inset 0 -1px 0 var(--borderColor-default)}#ResultTable th,#DumpTable th{white-space:nowrap}#DumpBody tr{cursor:default}td.act{white-space:nowrap}td.act button{height:20px;padding:0 8px;font-size:0.75rem}td.act button+button{margin-left:4px}
		dialog{top:30%;bottom:auto;margin:0 auto;transform:translateY(-30%);padding:16px;border:0;border-radius:var(--borderRadius-large);background:var(--overlay-bgColor);color:var(--fgColor-default);box-shadow:var(--shadow-floating-small)}dialog::backdrop{background:var(--overlay-backdrop-bgColor)}
		.setting{display:grid;grid-template-columns:auto 110px auto;align-items:center;gap:8px 12px;margin:16px 0}.setting select{width:100%;min-width:0}.hint{color:var(--fgColor-muted)}#SettingDialog .toolright{justify-content:flex-end}#QuerySettingDialog{width:600px}#QuerySettingTab{display:flex;gap:8px}#QueryEditScroll{height:495px;max-height:none}#QuerySettingDialog .toolright{justify-content:flex-end}#DownDialog{min-width:320px;max-width:600px}#DownDialog .toolright{justify-content:flex-end}#DownName{margin:12px 0 4px;color:var(--fgColor-default);font-weight:600;word-break:break-all}#DownPath{margin:0 0 16px;word-break:break-all}#DeleteDialog{min-width:320px;max-width:600px}#DeleteDialog .toolright{justify-content:flex-end}#DeleteText{margin:12px 0 16px;color:var(--fgColor-default);word-break:break-all}#DeleteName{margin:12px 0 4px;color:var(--fgColor-default);font-weight:600;word-break:break-all}#DeleteName:empty{display:none}#DeleteName:not(:empty)+#DeleteText{margin-top:0}#HelpDialog{width:max-content;max-width:calc(100vw - 32px)}#HelpTable{width:auto;margin-bottom:12px;border-collapse:collapse;font-size:0.875rem}#HelpTable th{position:static}#HelpTable td:first-child{font-weight:600;white-space:nowrap}#HelpDialog .toolright{justify-content:flex-end}
		tbody tr{cursor:pointer}tbody tr:hover{background:var(--bgColor-muted)}tbody tr.sel{background:var(--bgColor-accent-muted)}mark{background:var(--highlight-neutral-bgColor);color:inherit;padding:0}
		[hidden]{display:none!important}.resulthead{display:flex;flex-wrap:wrap;align-items:center;gap:6px 12px}.toolright{display:flex;flex-wrap:wrap;align-items:center;gap:4px 8px;margin-left:auto}.pager{display:flex;flex-wrap:wrap;align-items:center;gap:4px}.pager button{min-width:28px;padding:0 8px;border-color:transparent;background:transparent;box-shadow:none;color:var(--fgColor-default);font-weight:400}.pager button:hover:not(:disabled){border-color:var(--borderColor-default);background:transparent}
		.pager button.cur,.pager button.cur:hover:not(:disabled){border-color:transparent;background:var(--zone,var(--bgColor-emphasis));box-shadow:none;color:var(--fgColor-onEmphasis)}
		pre{margin:0;padding:8px;font:400 0.8125rem/1.5 var(--fontStack-monospace)}.line{display:block;white-space:pre}.line.target{background:var(--bgColor-accent-muted)}.lno{color:var(--fgColor-muted);user-select:none}
		</style>
		</head>
		<body data-mode="__MODE__">
		<h1 style="color:__ZONE_COLOR__"><span id="TitleText">__TITLE__</span><span id="TabBar"><button type="button" id="QueryButton" class="cur">쿼리</button><button type="button" id="LogButton">서치</button><button type="button" id="DumpButton">덤프</button><button type="button" id="FileButton">파일</button><button type="button" id="HelpButton" title="단축키">힌트</button></span></h1>
		<section class="panel" id="FilterPanel">
		<div class="filter">
		<span class="pickwrap" title="Machine"><span class="pickkey">M</span><span id="MachinePick"><button type="button" id="MachineBox" title="대상 머신(전체, 체크)" aria-haspopup="true" aria-expanded="false">전체</button><div id="MachineMenu" hidden><label class="all"><input type="checkbox" id="MachineAll" checked><span>전체</span></label><div id="MachineList"></div></div></span></span>
		<label class="queryonly" title="Duration(최근 기간)"><span class="pickkey">D</span><select id="QueryRange"><option value="10">10분</option><option value="30">30분</option><option value="60" selected>60분</option><option value="120">2시간</option><option value="240">4시간</option><option value="480">8시간</option><option value="1440">1일</option><option value="4320">3일</option><option value="10080">7일</option><option value="43200">30일</option><option value="86400">60일</option><option value="129600">90일</option></select></label>
		<label title="Start(날짜, 시, 분)">S <input type="date" id="StartDate" value="__YESTERDAY__"><select id="StartHour" class="hour"></select><select id="StartMinute" class="hour"></select></label>
		<label title="End(날짜, 시, 분, 미포함)">E <input type="date" id="EndDate" value="__TODAY__"><select id="EndHour" class="hour"></select><select id="EndMinute" class="hour"></select></label>
		<div class="searchbox">
		<input type="text" id="SearchKey" class="searchonly" title="검색어(AND, OR, NOT, 괄호)">
		<input type="text" id="QueryKey" class="queryonly" title="쿼리(단어, 필드:값, AND, OR, NOT, 괄호)">
		<button type="button" id="SettingButton" title="검색 설정" aria-label="설정"><svg class="octicon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M8 0a8.2 8.2 0 0 1 .701.031C9.444.095 9.99.645 10.16 1.29l.288 1.107c.018.066.079.158.212.224.231.114.454.243.668.386.123.082.233.09.299.071l1.103-.303c.644-.176 1.392.021 1.82.63.27.385.506.792.704 1.218.315.675.111 1.422-.364 1.891l-.814.806c-.049.048-.098.147-.088.294.016.257.016.515 0 .772-.01.147.038.246.088.294l.814.806c.475.469.679 1.216.364 1.891a7.977 7.977 0 0 1-.704 1.217c-.428.61-1.176.807-1.82.63l-1.102-.302c-.067-.019-.177-.011-.3.071a5.909 5.909 0 0 1-.668.386c-.133.066-.194.158-.211.224l-.29 1.106c-.168.646-.715 1.196-1.458 1.26a8.006 8.006 0 0 1-1.402 0c-.743-.064-1.289-.614-1.458-1.26l-.289-1.106c-.018-.066-.079-.158-.212-.224a5.738 5.738 0 0 1-.668-.386c-.123-.082-.233-.09-.299-.071l-1.103.303c-.644.176-1.392-.021-1.82-.63a8.12 8.12 0 0 1-.704-1.218c-.315-.675-.111-1.422.363-1.891l.815-.806c.05-.048.098-.147.088-.294a6.214 6.214 0 0 1 0-.772c.01-.147-.038-.246-.088-.294l-.815-.806C.635 6.045.431 5.298.746 4.623a7.92 7.92 0 0 1 .704-1.217c.428-.61 1.176-.807 1.82-.63l1.102.302c.067.019.177.011.3-.071.214-.143.437-.272.668-.386.133-.066.194-.158.211-.224l.29-1.106C6.009.645 6.556.095 7.299.03 7.53.01 7.764 0 8 0Zm-.571 1.525c-.036.003-.108.036-.137.146l-.289 1.105c-.147.561-.549.967-.998 1.189-.173.086-.34.183-.5.29-.417.278-.97.423-1.529.27l-1.103-.303c-.109-.03-.175.016-.195.045-.22.312-.412.644-.573.99-.014.031-.021.11.059.19l.815.806c.411.406.562.957.53 1.456a4.709 4.709 0 0 0 0 .582c.032.499-.119 1.05-.53 1.456l-.815.806c-.081.08-.073.159-.059.19.162.346.353.677.573.989.02.03.085.076.195.046l1.102-.303c.56-.153 1.113-.008 1.53.27.161.107.328.204.501.29.447.222.85.629.997 1.189l.289 1.105c.029.109.101.143.137.146a6.6 6.6 0 0 0 1.142 0c.036-.003.108-.036.137-.146l.289-1.105c.147-.561.549-.967.998-1.189.173-.086.34-.183.5-.29.417-.278.97-.423 1.529-.27l1.103.303c.109.029.175-.016.195-.045.22-.313.411-.644.573-.99.014-.031.021-.11-.059-.19l-.815-.806c-.411-.406-.562-.957-.53-1.456a4.709 4.709 0 0 0 0-.582c-.032-.499.119-1.05.53-1.456l.815-.806c.081-.08.073-.159.059-.19a6.464 6.464 0 0 0-.573-.989c-.02-.03-.085-.076-.195-.046l-1.102.303c-.56.153-1.113.008-1.53-.27a4.44 4.44 0 0 0-.501-.29c-.447-.222-.85-.629-.997-1.189l-.289-1.105c-.029-.11-.101-.143-.137-.146a6.6 6.6 0 0 0-1.142 0ZM11 8a3 3 0 1 1-6 0 3 3 0 0 1 6 0ZM9.5 8a1.5 1.5 0 1 0-3.001.001A1.5 1.5 0 0 0 9.5 8Z"/></svg></button>
		<button type="button" id="SearchButton" class="primary searchonly" title="서치 실행(Enter)">실행</button>
		<button type="button" id="QueryRunButton" class="primary queryonly" title="쿼리 실행(Enter)">실행</button>
		<button type="button" id="QueryStopButton" class="danger queryonly" title="쿼리 중단" disabled>중단</button>
		<button type="button" id="StopButton" class="danger searchonly" title="검색 중단" disabled>중단</button>
		</div>
		</div>
		</section>
		<div class="splitter" id="FilterSplitter" title="위아래 크기 조절"></div>
		<section class="panel" id="ResultPanel">
		<div class="resulthead">
		<div class="title" id="ResultTitle"></div>
		<div class="title" id="QueryTitle"></div>
		<div class="toolright">
		<button type="button" id="QueryPrev" title="뒤로(이전 단계)" aria-label="뒤로" disabled><svg class="octicon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M7.78 12.53a.75.75 0 0 1-1.06 0L2.47 8.28a.75.75 0 0 1 0-1.06l4.25-4.25a.751.751 0 0 1 1.042.018.751.751 0 0 1 .018 1.042L4.81 7h7.44a.75.75 0 0 1 0 1.5H4.81l2.97 2.97a.75.75 0 0 1 0 1.06Z"/></svg></button>
		<button type="button" id="QueryDepth" tabindex="-1" title="단계 입력" aria-label="단계">0</button>
		<button type="button" id="QueryNext" title="앞으로(다음 단계)" aria-label="앞으로" disabled><svg class="octicon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M8.22 2.97a.75.75 0 0 1 1.06 0l4.25 4.25a.75.75 0 0 1 0 1.06l-4.25 4.25a.751.751 0 0 1-1.042-.018.751.751 0 0 1-.018-1.042l2.97-2.97H3.75a.75.75 0 0 1 0-1.5h7.44L8.22 4.03a.75.75 0 0 1 0-1.06Z"/></svg></button>
		</div>
		</div>
		<div id="QueryChart" hidden></div>
		<div class="progress" id="QueryProgress" hidden></div>
		<div class="scrollwrap">
		<div class="scroll" id="ResultScroll">
		<table id="ResultTable">
		<colgroup><col><col><col><col><col></colgroup>
		<thead><tr><th>No.</th><th>Machine</th><th>File</th><th>Line</th><th>Log</th></tr></thead>
		<tbody id="ResultBody"></tbody>
		</table>
		</div>
		<div class="progress" id="SearchProgress" hidden></div>
		</div>
		<div class="pager" id="ResultPager" hidden></div>
		</section>
		<div class="splitter" id="QuerySplitter" title="위아래 크기 조절" hidden></div>
		<section class="panel" id="QueryPanel" hidden>
		<div class="resulthead">
		<div class="title" id="QueryLogTitle"></div>
		<div class="toolright">
		</div>
		</div>
		<div class="querysplit" id="QuerySplit">
		<aside id="QueryField">
		<div class="fieldhead"><span>필드</span></div>
		<ul id="QueryFieldList"></ul>
		</aside>
		<div class="vsplitter" id="QueryFieldSplitter" title="좌우 폭 조절"></div>
		<div class="scroll" id="QueryScroll">
		<table id="QueryTable">
		<colgroup><col><col><col><col><col></colgroup>
		<thead><tr><th>No.</th><th>Machine</th><th>File</th><th title="파일 줄 번호">Line</th><th title="단어 조건(AND, OR, NOT)">Log</th></tr></thead>
		<tbody id="QueryBody"></tbody>
		</table>
		</div>
		</div>
		<div class="pager" id="QueryPager" hidden></div>
		</section>
		<section class="panel" id="QueryFieldPanel" hidden>
		<div class="resulthead">
		<div class="title" id="QueryFieldTitle"></div>
		</div>
		<div class="scroll" id="QueryFieldScroll">
		<table id="QueryFieldTable">
		<colgroup><col><col><col></colgroup>
		<thead><tr><th>No.</th><th id="QueryFieldColumn">Value</th><th>Count</th></tr></thead>
		<tbody id="QueryFieldBody"></tbody>
		</table>
		</div>
		<div class="pager" id="QueryFieldPager" hidden></div>
		</section>
		<div class="splitter" id="Splitter" title="위아래 크기 조절"></div>
		<section class="panel" id="ViewPanel">
		<div class="filter">
		<span class="title" id="ViewTitle"></span>
		<div class="toolright">
		<button type="button" id="ViewPrev" title="위로(줄 단위 이동)" aria-label="위로" disabled><svg class="octicon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M7.78 12.53a.75.75 0 0 1-1.06 0L2.47 8.28a.75.75 0 0 1 0-1.06l4.25-4.25a.751.751 0 0 1 1.042.018.751.751 0 0 1 .018 1.042L4.81 7h7.44a.75.75 0 0 1 0 1.5H4.81l2.97 2.97a.75.75 0 0 1 0 1.06Z"/></svg></button>
		<button type="button" id="ViewShift" tabindex="-1" aria-label="위치">0</button>
		<button type="button" id="ViewNext" title="아래로(줄 단위 이동)" aria-label="아래로" disabled><svg class="octicon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M8.22 2.97a.75.75 0 0 1 1.06 0l4.25 4.25a.75.75 0 0 1 0 1.06l-4.25 4.25a.751.751 0 0 1-1.042-.018.751.751 0 0 1-.018-1.042l2.97-2.97H3.75a.75.75 0 0 1 0-1.5h7.44L8.22 4.03a.75.75 0 0 1 0-1.06Z"/></svg></button>
		</div>
		</div>
		<div class="scroll" id="ViewScroll"><pre id="ViewBody"></pre></div>
		<div class="hscroll" id="ViewHScroll" hidden><div id="ViewHScrollSize"></div></div>
		</section>
		<section class="panel" id="DumpPanel" hidden>
		<div class="resulthead">
		<div class="title" id="DumpTitle"></div>
		<div class="toolright">
		<button type="button" id="DumpDeleteAll" class="danger" title="전체 삭제(dmp, zip)" disabled>삭제</button>
		</div>
		</div>
		<div class="scroll" id="DumpScroll">
		<table id="DumpTable" data-rest="5">
		<colgroup><col><col><col><col><col><col><col></colgroup>
		<thead><tr><th>No.</th><th>Machine</th><th>File</th><th>Date</th><th>Size</th><th>Folder</th><th title="다운, 압축, 삭제(dmp, zip)">Ac.</th></tr></thead>
		<tbody id="DumpBody"></tbody>
		</table>
		</div>
		<div class="progress" id="DumpProgress" hidden></div>
		</section>
		<section class="panel" id="FilePanel" hidden>
		<div class="resulthead">
		<div class="title" id="FileTitle"></div>
		<div class="title" id="FileViewTitle">*.log·*.txt·*.json·*.ini·*.bat·*.xml·*.config·*.md·*.lua</div>
		<div class="toolright">
		</div>
		</div>
		<div class="filesplit">
		<div class="scroll" id="FileScroll"><ul class="tree" id="FileTree"></ul></div>
		<div class="vsplitter" id="FileSplitter" title="좌우 폭 조절"></div>
		<div class="fileview">
		<div class="scroll" id="FileViewScroll"><pre id="FileViewBody"></pre></div>
		</div>
		</div>
		</section>
		<div class="colguide" id="ColGuide" hidden></div>
		<dialog id="SettingDialog">
		<div class="resulthead">
		<div class="title">검색 설정</div>
		<div class="toolright">
		<button type="button" id="SettingDefault" title="기본값">기본</button>
		</div>
		</div>
		<div class="setting">
		<label for="MachineMaxInput">한 머신당 최대</label><input type="number" id="MachineMaxInput" min="1" step="1" autofocus><span class="hint" id="MachineMaxHint"></span>
		<label for="ResultMaxInput">모든 머신 최대</label><input type="number" id="ResultMaxInput" min="1" step="1"><span class="hint" id="ResultMaxHint"></span>
		<label for="SortSelect">결과 정렬</label><select id="SortSelect"><option value="time">시간 정렬</option><option value="base">머신 정렬</option></select><span class="hint" id="SortHint"></span>
		</div>
		<div class="toolright">
		<button type="button" id="SettingSave" class="primary">저장</button>
		<button type="button" id="SettingCancel">취소</button>
		</div>
		</dialog>
		<dialog id="QuerySettingDialog">
		<div class="title" id="QuerySettingTab"><button type="button" id="QuerySettingQuery" class="cur">쿼리</button><button type="button" id="QuerySettingAlarm">알람</button></div>
		<div class="savedlist" id="QueryEditScroll"><table id="QueryEditTable"><tbody id="QueryEditList"></tbody></table></div>
		<div class="toolright">
		<button type="button" id="QueryEditAdd" title="쿼리 추가">추가</button>
		<button type="button" id="QuerySettingCancel" autofocus>닫기</button>
		</div>
		</dialog>
		<dialog id="QuerySaveDialog">
		<div class="title" id="QuerySaveTitle">쿼리 추가</div>
		<div class="setting">
		<label for="QuerySaveKey">제목</label><input type="text" id="QuerySaveKey" maxlength="50" autocomplete="off" autofocus>
		<label for="QuerySaveQuery">쿼리</label><textarea id="QuerySaveQuery" rows="10" maxlength="1000" spellcheck="false"></textarea>
		<label for="QuerySaveMinute" class="queryrow">간격</label><span class="queryrow"><input type="number" id="QuerySaveMinute" min="1" max="129600" step="1"> 분</span>
		<label for="AlarmMinute" class="alarmrow">간격</label><span class="alarmrow"><input type="number" id="AlarmMinute" min="1" max="60" step="1"> 분 동안 <input type="number" id="AlarmCount" min="1" step="1"> 건 이상 <label for="AlarmDelay" class="delay">억제</label><input type="number" id="AlarmDelay" min="0" max="1440" step="1"> 분</span>
		<label for="AlarmMail" class="alarmrow">메일</label><span class="alarmrow alarmadd"><span class="alarminput"><input type="text" id="AlarmMail" maxlength="254" autocomplete="off"><button type="button" id="AlarmMailAdd">추가</button></span><span class="alarmlist" id="AlarmMailList"></span></span>
		<label for="AlarmSMS" class="alarmrow">SMS</label><span class="alarmrow alarmadd"><span class="alarminput"><input type="text" id="AlarmSMS" maxlength="20" autocomplete="off"><button type="button" id="AlarmSMSAdd">추가</button></span><span class="alarmlist" id="AlarmSMSList"></span></span>
		<label for="AlarmHook" class="alarmrow">훅</label><input type="text" id="AlarmHook" class="alarmrow" maxlength="1000" autocomplete="off">
		<label for="AlarmEnable" class="alarmrow">사용</label><span class="alarmrow"><input type="checkbox" id="AlarmEnable"></span>
		</div>
		<div class="toolright">
		<button type="button" id="AlarmTest" class="alarmrow" title="메일·훅 바로 발송">테스트</button>
		<button type="button" id="QuerySaveOk" class="primary">저장</button>
		<button type="button" id="QuerySaveCancel">취소</button>
		</div>
		</dialog>
		<dialog id="DownDialog">
		<div class="title">파일 다운</div>
		<p id="DownName"></p>
		<p id="DownPath"></p>
		<div class="toolright">
		<button type="button" id="DownYes" class="primary">예</button>
		<button type="button" id="DownNo">아니오</button>
		</div>
		</dialog>
		<dialog id="DeleteDialog">
		<div class="title" id="DeleteTitle">덤프 전체 삭제</div>
		<p id="DeleteName"></p>
		<p id="DeleteText"></p>
		<div class="toolright">
		<button type="button" id="DeleteYes" class="danger">삭제</button>
		<button type="button" id="DeleteNo">취소</button>
		</div>
		</dialog>
		<dialog id="HelpDialog">
		<table id="HelpTable">
		<thead><tr><th>단축키</th><th>명령</th></tr></thead>
		<tbody>
		<tr><td>Ctrl + 단어 클릭</td><td>입력 + AND</td></tr>
		<tr><td>Alt + 단어 클릭</td><td>입력 + OR</td></tr>
		<tr><td>Ctrl + Alt + 단어 클릭</td><td>입력 + NOT</td></tr>
		<tr><td>Ctrl · Alt + 같은 단어 클릭</td><td>입력 제외</td></tr>
		<tr><td>Ctrl · Alt + 필드:값 클릭</td><td>입력 + 필드:값</td></tr>
		<tr><td>Ctrl · Alt · Ctrl + Alt 놓기</td><td>입력 존재시 1초 뒤 실행</td></tr>
		<tr><td>Shift + 입력 클릭</td><td>쿼리 ↔ 서치 입력·기간 복사</td></tr>
		<tr><td>Shift + 단어·실행 클릭</td><td>쿼리 ↔ 서치 입력·기간 복사 + 실행</td></tr>
		<tr><td>Shift + Ctrl + 입력·단어·실행 클릭</td><td>새 창에서 위와 같게</td></tr>
		<tr><td>Shift + Alt + 입력·단어·실행 클릭</td><td>새 탭에서 위와 같게</td></tr>
		<tr><td>Ctrl + UI 클릭</td><td>새 창에서 실행</td></tr>
		<tr><td>Alt + UI 클릭</td><td>새 탭에서 실행</td></tr>
		<tr><td>Shift + 파일 클릭</td><td>다운</td></tr>
		<tr><td>Ctrl + A</td><td>누른 표 전체 (칸 이름 포함, 없음: 맨 위 표)</td></tr>
		<tr><td>Step 버튼 클릭</td><td>숫자 입력 + Enter: 해당 Step 이동</td></tr>
		<tr><td>Tab</td><td>오른쪽 탭 (맨 끝: 처음)</td></tr>
		</tbody>
		</table>
		<div class="toolright">
		<button type="button" id="HelpClose" autofocus>닫기</button>
		</div>
		</dialog>
		<script>
		const funcFetch = window.fetch.bind(window);	// 원래 fetch

		window.fetch = async (...arrArg) =>	// 401 (쿠키 만료·계정 변경·캐시 페이지) → 새로고침 → 로그인 페이지
		{
		    const kResponse = await funcFetch(...arrArg);

		    if (kResponse.status === 401)
		    {
		        location.reload();
		    }

		    return kResponse;
		};

		const sZoneName = document.getElementById("TitleText").textContent.split(" ").slice(1).join(" ").toLowerCase();	// 존: 제목 LM 뒤 (예: LM Alpha → alpha)

		document.documentElement.style.setProperty("--split", sZoneName.includes("real") ? "#c42b2b" : (sZoneName.includes("alpha") ? "#2f6fde" : (sZoneName.includes("dev") ? "#1b7a3b" : "var(--bgColor-emphasis)")));	// 창 사이 바·칸 경계 = SM 존 색 (제목 색과 별개)

		const kFilterPanel	        = document.getElementById("FilterPanel");	// 명령 창: 검색 조건 (덤프·파일 모드 숨김)
		const kMachinePick	        = document.getElementById("MachinePick");	// 머신 드롭 박스 묶음 (밖 클릭: 목록 닫음)
		const kMachineBox	        = document.getElementById("MachineBox");		// 머신 드롭 박스 (전체·고른 머신 표시)
		const kMachineMenu	        = document.getElementById("MachineMenu");	// 펼친 목록 (왼쪽 체크 박스)
		const kMachineList	        = document.getElementById("MachineList");
		const kMachineAll	        = document.getElementById("MachineAll");		// 전체 (체크: 모든 머신)
		const kStart		        = document.getElementById("StartDate");		// 시작일 (기본: 어제 KST)
		const kStartHour	        = document.getElementById("StartHour");		// 시작 시 (00~23, 기본 00)
		const kEnd			        = document.getElementById("EndDate");		// 종료일 (기본: 오늘 KST)
		const kEndHour		        = document.getElementById("EndHour");		// 종료 시 (00~24, 기본 24 = 그날 23시 파일까지)
		const kStartMinute	        = document.getElementById("StartMinute");	// 시작 분 (00~59, 서치·쿼리)
		const kEndMinute	        = document.getElementById("EndMinute");		// 종료 분 (00~59, 서치·쿼리)
		const kQueryRange	        = document.getElementById("QueryRange");	// D: 쿼리 최근 기간 (분)
		const kQuerySaveDialog	    = document.getElementById("QuerySaveDialog");	// 쿼리·알람 추가·수정 창 (알람: class alarm → 알람 칸)
		const kQuerySaveKey	        = document.getElementById("QuerySaveKey");		// 쿼리 추가·수정 제목
		const kQuerySaveQuery	    = document.getElementById("QuerySaveQuery");	// 쿼리 추가·수정 쿼리 (줄바꿈: 빈칸으로 저장)
		const kQuerySaveTitle	    = document.getElementById("QuerySaveTitle");	// 쿼리 추가·수정 창 제목
		const kQuerySaveMinute	    = document.getElementById("QuerySaveMinute");	// 쿼리 추가·수정 간격 (분)
		const kAlarmMinute	        = document.getElementById("AlarmMinute");		// 알람 간격 (분)
		const kAlarmCount	        = document.getElementById("AlarmCount");		// 알람 건수
		const kAlarmDelay	        = document.getElementById("AlarmDelay");		// 알람 억제 (분)
		const kAlarmMail	        = document.getElementById("AlarmMail");			// 알람 메일 입력 (추가 → 밑 목록)
		const kAlarmMailList	    = document.getElementById("AlarmMailList");		// 알람 메일 목록
		const kAlarmHook	        = document.getElementById("AlarmHook");			// 알람 훅 주소
		const kAlarmSMS		        = document.getElementById("AlarmSMS");			// 알람 SMS 입력 (추가 → 밑 목록)
		const kAlarmSMSList	        = document.getElementById("AlarmSMSList");		// 알람 SMS 목록
		const kAlarmEnable	        = document.getElementById("AlarmEnable");		// 알람 사용
		const kQuerySettingQuery    = document.getElementById("QuerySettingQuery");	// 설정 창 쿼리 탭
		const kQuerySettingAlarm    = document.getElementById("QuerySettingAlarm");	// 설정 창 알람 탭
		const kQueryEditAdd		    = document.getElementById("QueryEditAdd");		// 설정 창 추가 버튼
		const kQueryEditScroll	    = document.getElementById("QueryEditScroll");	// 설정 창 줄 상자 (높이 고정, 넘침: 스크롤)
		const kQueryEditList	    = document.getElementById("QueryEditList");		// 설정 창 줄 (저장 쿼리·알람)
		const kSettingButton	    = document.getElementById("SettingButton");	// 설정 버튼 (쿼리 모드: 쿼리 설정)
		const kSettingDialog	    = document.getElementById("SettingDialog");	// 검색 설정 창
		const kQuerySettingDialog	= document.getElementById("QuerySettingDialog");	// 쿼리 설정 창
		const kDownDialog	        = document.getElementById("DownDialog");	// 다운 확인 팝업 (File 칸·파일 트리·덤프 zip 공통)
		let funcDownYes		        = null;		// 다운 확인 예 동작 (null: 확인 중 아님)
		let funcDeleteYes		    = null;		// 삭제 확인 동작 (null: 확인 중 아님)
		const kMachineMaxInput	    = document.getElementById("MachineMaxInput");
		const kResultMaxInput	    = document.getElementById("ResultMaxInput");
		const kSortSelect		    = document.getElementById("SortSelect");	// 설정 창 결과 정렬
		const kSortHint			    = document.getElementById("SortHint");		// 결과 정렬 설명
		const kColGuide			    = document.getElementById("ColGuide");		// 칸 너비 드래그 위치 세로선 (표는 놓을 때 한 번만 배치)
		const kKey			        = document.getElementById("SearchKey");		// 검색어 (서치만)
		const kSearchButton	        = document.getElementById("SearchButton");	// 검색 버튼
		const kQueryKey		        = document.getElementById("QueryKey");		// 쿼리 입력칸
		const kQueryRunButton	    = document.getElementById("QueryRunButton");	// 쿼리 버튼
		const kQueryStopButton	    = document.getElementById("QueryStopButton");	// 쿼리 중단 버튼
		const kQueryProgress	    = document.getElementById("QueryProgress");	// 쿼리 진행 표시
		const kStopButton	        = document.getElementById("StopButton");	// 검색 중지 버튼
		const kProgress		        = document.getElementById("SearchProgress");	// 검색 진행 표시
		const kResultPanel	        = document.getElementById("ResultPanel");	// 서치 1번 창: 검색 결과
		const kResultTitle	        = document.getElementById("ResultTitle");
		const kResultPager	        = document.getElementById("ResultPager");	// 페이지 번호
		const kResultScroll	        = document.getElementById("ResultScroll");
		const kResultTable	        = document.getElementById("ResultTable");	// 서치 1번 창 표
		const kResultBody	        = document.getElementById("ResultBody");
		const kQueryTitle	        = document.getElementById("QueryTitle");	// 쿼리 1번 창 제목
		const kQueryPager	        = document.getElementById("QueryPager");	// 쿼리 1번 창 페이지 번호
		const kQueryBody	        = document.getElementById("QueryBody");		// 쿼리 1번 창 결과 줄
		const kQueryChart	        = document.getElementById("QueryChart");	// 쿼리 1번 창 시간별 건수 막대
		const kQueryPrev	        = document.getElementById("QueryPrev");		// ← 이전 단계
		const kQueryDepth	        = document.getElementById("QueryDepth");	// 지금 단계 (0: 최초, 누름: 숫자 입력)
		const kQueryNext	        = document.getElementById("QueryNext");		// → 다음 단계
		const kQueryPanel	        = document.getElementById("QueryPanel");	// 쿼리 2번 창: 로그
		const kQueryLogTitle	    = document.getElementById("QueryLogTitle");	// 쿼리 2번 창 제목
		const kQueryScroll	        = document.getElementById("QueryScroll");
		const kQuerySplitter	        = document.getElementById("QuerySplitter");	// 쿼리 1·2번 창 사이 바
		const kFilterSplitter	        = document.getElementById("FilterSplitter");	// 명령 창·결과 창 사이 바 (쿼리·서치)
		const kQuerySplit	        = document.getElementById("QuerySplit");		// 쿼리 2번 창 필드 + 로그 (--fieldWidth: 필드 폭)
		const kQueryField	        = document.getElementById("QueryField");		// 쿼리 2번 창 필드 상자
		const kQueryFieldSplitter	= document.getElementById("QueryFieldSplitter");	// 필드·로그 사이 세로선
		const kQueryFieldList	    = document.getElementById("QueryFieldList");	// 쿼리 2번 창 필드 목록
		const kQueryFieldPanel	    = document.getElementById("QueryFieldPanel");	// 필드 값 목록 새 창 (필드 모드만)
		const kQueryFieldTitle	    = document.getElementById("QueryFieldTitle");	// 필드 새 창 제목
		const kQueryFieldColumn	    = document.getElementById("QueryFieldColumn");	// 필드 새 창 값 칸 이름
		const kQueryFieldScroll	    = document.getElementById("QueryFieldScroll");
		const kQueryFieldBody	    = document.getElementById("QueryFieldBody");	// 필드 새 창 값 줄
		const kQueryFieldPager	    = document.getElementById("QueryFieldPager");	// 필드 새 창 아래 페이지 번호
		const kViewPanel	        = document.getElementById("ViewPanel");		// 서치 2번 창: 파일 보기
		const kViewTitle	        = document.getElementById("ViewTitle");
		const kViewScroll	        = document.getElementById("ViewScroll");
		const kViewBody		        = document.getElementById("ViewBody");
		const kViewPrev		        = document.getElementById("ViewPrev");		// 서치 2번 창 ← 위로 VIEW_COUNT 줄
		const kViewShift	        = document.getElementById("ViewShift");		// 서치 2번 창 위치 (0: 처음 연 위치)
		const kViewNext		        = document.getElementById("ViewNext");		// 서치 2번 창 → 아래로 VIEW_COUNT 줄
		const kLogButton	        = document.getElementById("LogButton");		// 서치 탭 버튼
		const kDumpButton	        = document.getElementById("DumpButton");	// 덤프 탭 버튼
		const kDumpPanel	        = document.getElementById("DumpPanel");		// 덤프 1번 창: 덤프 파일
		const kDumpTitle	        = document.getElementById("DumpTitle");
		const kDumpScroll	        = document.getElementById("DumpScroll");
		const kDumpTable	        = document.getElementById("DumpTable");		// 덤프 1번 창 표
		const kDumpBody		        = document.getElementById("DumpBody");
		const kDumpProgress	        = document.getElementById("DumpProgress");	// 덤프 진행 팝업
		const kDumpDeleteAll	    = document.getElementById("DumpDeleteAll");	// 덤프 전체 삭제 버튼
		const kDeleteDialog	        = document.getElementById("DeleteDialog");	// 삭제 확인 팝업
		const kDeleteText	        = document.getElementById("DeleteText");
		const kFileButton	        = document.getElementById("FileButton");	// 파일 탭 버튼
		const kFilePanel	        = document.getElementById("FilePanel");		// 파일 1번 창: 클라 감시 폴더 트리
		const kFileTitle	        = document.getElementById("FileTitle");
		const kFileTree		        = document.getElementById("FileTree");
		const kFileScroll	        = document.getElementById("FileScroll");		// 파일 1번 창 왼쪽: 트리
		const kFileSplitter	        = document.getElementById("FileSplitter");	// 파일 1번 창 좌우 사이 세로선
		const kFileViewTitle	    = document.getElementById("FileViewTitle");	// 파일 1번 창 오른쪽: 파일 보기
		const kFileViewScroll	    = document.getElementById("FileViewScroll");
		const kFileViewBody	        = document.getElementById("FileViewBody");
		const kQueryButton	        = document.getElementById("QueryButton");	// 쿼리 탭 버튼
		const kHelpDialog	        = document.getElementById("HelpDialog");	// 힌트 팝업
		const MACHINE_LIMIT	        = 1000000;	// 머신당 최대 건수 상한 (서버·클라 m_nSearchMax 와 같음)
		const RESULT_LIMIT	        = 1000000;	// 전체 최대 건수 상한
		const MACHINE_DEFAULT	    = 1000;	// 머신당 최대 건수 기본값 (서버·클라 m_nSearchDefault 와 같음)
		const RESULT_DEFAULT	    = 10000;	// 전체 최대 건수 기본값 (모든 머신 합)
		const LIMIT_KEY		        = "LogManager.Limit";	// 검색 설정 localStorage 키
		let nMachineMax		        = MACHINE_DEFAULT;	// 머신당 최대 건수 (설정)
		let nResultMax		        = RESULT_DEFAULT;	// 전체 최대 건수 (설정, 넘음: 이후 머신 검색 안 함)
		const SORT_TIME		        = "time";	// 시간 정렬 (HTSearch.SortTime)
		const SORT_BASE		        = "base";	// 머신 정렬 (받은 순서)
		const SORT_DEFAULT	        = SORT_TIME;	// 결과 정렬 기본값
		const SORT_INFO		        = { [SORT_TIME]: { sText: "시간 정렬", sHint: "최근 시간 위(시간이 없다면 머신별)" }, [SORT_BASE]: { sText: "머신 정렬", sHint: "머신 목록 → 파일(줄)" } };	// 결과 정렬 표시·설명
		let sSort			        = SORT_DEFAULT;	// 결과 정렬 (설정)
		const MACHINE_REFRESH_MS    = 10000;	// 접속 머신 목록 자동 갱신 주기 (ms)
		let bMachineLoading		    = false;	// 접속 머신 조회 중 (겹침 방지)
		let sMachineSignature	    = "";		// 마지막 그린 머신 목록 (바뀐 경우만 다시 그림)
		let arrMachineList	        = [];		// 드롭 박스 머신 (이름순 + 끊긴 체크 머신)
		const setMachineCheck	    = new Set();	// 체크한 머신 (비면: 전체)
		let nMachineCount	        = 0;		// 접속 머신 수
		const OPEN_WORD		        = "#ResultBody td.log, #ViewBody .line, #QueryBody td.log, #QueryBody .querytext, #QueryBody .queryfield span[data-field]";	// Ctrl·Alt+클릭 단어 칸 (입력칸에 단어 추가)
		const OPEN_TARGET	        = "a[href], button, input, select, textarea, label, td.filename, #FileTree div.node";	// Ctrl·Alt+클릭 새 창 대상
		const OPEN_SELF		        = ".fieldname";	// 원래 새 창 여는 것 (필드 이름): 그대로
		const OPEN_SKIP		        = "td.act, td.fileact";	// 줄 동작 버튼 (덤프 Ac.): 원래 동작
		const OPEN_STATE_INPUT	    = ["QueryRange", "StartDate", "StartHour", "StartMinute", "EndDate", "EndHour", "EndMinute", "SearchKey", "QueryKey"];	// 새 창에 넘길 입력칸
		let sOpenMode		        = "";		// OpenWindow 방식 ("": 크기 지정 새 창, browser: 같은 크기·자리 새 창 (Ctrl), tab: 새 탭 (Alt))
		const OPEN_TITLE	        = ".panel .resulthead, #ViewPanel > .filter";	// Ctrl·Alt+클릭 하위 창 제목란
		let kSelectTable	        = null;		// 마지막 누른 표 (Ctrl+A 대상, 없음: 맨 위 표)
		const SHIFT_QUERY_WORD	    = "#QueryBody td.log, #QueryBody .querytext";	// Shift+클릭 쿼리 단어 칸 (→ 서치)
		let bShiftClick		        = false;	// Shift+누름 실행함: 뒤따르는 클릭 막음

		for (let nHour = 0; nHour <= 24; ++nHour)	// 시 선택: 시작 00~23, 종료 00~24
		{
		    const sHour = String(nHour).padStart(2, "0");

		    if (nHour < 24)
		    {
		        kStartHour.add(new Option(sHour, sHour));
		    }

		    kEndHour.add(new Option(sHour, sHour));
		}

		kStartHour.value	= "00";	// 기본: 어제 0시
		kEndHour.value		= "24";	// 기본: 오늘 24시

		for (let nMinute = 0; nMinute < 60; ++nMinute)	// 분 선택: 00~59
		{
		    const sMinute = String(nMinute).padStart(2, "0");

		    kStartMinute.add(new Option(sMinute, sMinute));
		    kEndMinute.add(new Option(sMinute, sMinute));
		}

		function GetFilter()	// 선택 값 (nStart·nEnd: yyyyMMddHH 끝 포함, nStartMin·nEndMin: yyyyMMddHHmm 시작 포함·끝 미포함)
		{
		    const nStart	= Number(kStart.value.replaceAll("-", "")) * 100 + Number(kStartHour.value);
		    const nEnd		= Number(kEnd.value.replaceAll("-", "")) * 100 + Number(kEndHour.value);

		    return {
		        sMachine:	GetMachineText(),
		        arrMachine:	GetMachines(),
		        nStart:		nStart,
		        nEnd:		nEnd,
		        nStartMin:	nStart * 100 + Number(kStartMinute.value),
		        nEndMin:	nEnd * 100 + (kEndHour.value === "24" ? 0 : Number(kEndMinute.value)),
		        sKey:		kKey.value.trim(),
		    };
		}

		async function FetchJson(sUrl, kOption)	// 요청 → JSON (HTTP 오류: throw "HTTP 상태", 경유 서버 오류 페이지 → Bad Data 대신 상태)
		{
		    const kResponse = await fetch(sUrl, kOption);

		    if (!kResponse.ok)
		    {
		        throw new Error(`HTTP ${kResponse.status}`);
		    }

		    return await kResponse.json();
		}

		function ShowStatus()	// 설정 버튼 툴팁 (모드별 설정·상태 요약)
		{
		    kSettingButton.title = kQueryButton.classList.contains("cur") ? "쿼리 설정(저장 쿼리·알람)" :`검색 설정(접속 ${nMachineCount}, 머신당 ${nMachineMax}, 최대 ${nResultMax}, ${sSort === SORT_TIME ? "시간" : "머신"} 정렬)`;
		}

		function ClampLimit(value, nDefault, nLimit)	// 최대 건수 값 정리 (정수 1 ~ nLimit, 그 외: nDefault)
		{
		    const nValue = Math.floor(Number(value));

		    if (value === "" || value == null || !Number.isFinite(nValue))
		    {
		        return nDefault;
		    }

		    return Math.min(Math.max(1, nValue), nLimit);
		}

		function GetSort(sValue)	// 결과 정렬 값 정리 (SORT_INFO 에 없음: SORT_DEFAULT)
		{
		    return Object.hasOwn(SORT_INFO, sValue) ? sValue : SORT_DEFAULT;
		}

		function ShowSortHint()	// 설정 창 결과 정렬 설명 (선택한 정렬)
		{
		    kSortHint.textContent = SORT_INFO[GetSort(kSortSelect.value)].sHint;
		}

		function LoadLimit()	// 저장된 검색 설정 읽기 (없음·오류: 기본값)
		{
		    try
		    {
		        const kSaved = JSON.parse(localStorage.getItem(LIMIT_KEY) ?? "null");

		        if (kSaved != null)
		        {
		            nMachineMax	= ClampLimit(kSaved.nMachineMax, MACHINE_DEFAULT, MACHINE_LIMIT);
		            nResultMax	= ClampLimit(kSaved.nResultMax, RESULT_DEFAULT, RESULT_LIMIT);
		            sSort		= GetSort(kSaved.sSort);	// 정렬 저장 전 설정: 기본값
		        }
		    }
		    catch (kError)	// 저장소 사용 불가·형식 오류
		    {
		        console.error(kError);
		    }
		}

		function OpenSetting()	// 설정 창 열기 (지금 값 표시, 검색 중 제외)
		{
		    if (HTSearch.bSearching)
		    {
		        return;
		    }

		    kMachineMaxInput.value	= nMachineMax;
		    kResultMaxInput.value	= nResultMax;
		    kSortSelect.value		= sSort;

		    kSettingDialog.style.width	= "";								// 크기 다시 계산
		    kSortHint.textContent		= SORT_INFO[SORT_DEFAULT].sHint;	// 기본 정렬 설명 기준 크기

		    kSettingDialog.showModal();

		    kSettingDialog.style.width	= getComputedStyle(kSettingDialog).width;	// 팝업 크기 고정 (정렬 바꿔도 안 줄어듦)

		    ShowSortHint();
		}

		function SaveSetting()	// 설정 저장 (저장 실패: 이 창만 적용, 정렬 바뀜: 지금 결과 다시 정렬)
		{
		    const sSortBefore = sSort;

		    nMachineMax	= ClampLimit(kMachineMaxInput.value, MACHINE_DEFAULT, MACHINE_LIMIT);
		    nResultMax	= ClampLimit(kResultMaxInput.value, RESULT_DEFAULT, RESULT_LIMIT);
		    sSort		= GetSort(kSortSelect.value);

		    try
		    {
		        localStorage.setItem(LIMIT_KEY, JSON.stringify({ nMachineMax: nMachineMax, nResultMax: nResultMax, sSort: sSort }));
		    }
		    catch (kError)	// 저장소 사용 불가
		    {
		        console.error(kError);
		    }

		    kSettingDialog.close();

		    ShowStatus();

		    if (sSort !== sSortBefore && HTSearch.arrReceived.length > 0)	// 정렬 바뀜: 다시 정렬 (다시 검색 안 함)
		    {
		        HTSearch.SortResult();
		        HTSearch.ShowResultPage(0);
		    }
		}

		function FixRange(bStartChanged)	// 시작 > 종료: 바꾼 쪽 기준 반대쪽 맞춤 (쿼리 모드: D 해제만)
		{
		    if (kQueryButton.classList.contains("cur"))	// 쿼리 모드: S·E 직접 고침 → D 해제
		    {
		        kQueryRange.selectedIndex = -1;

		        return;
		    }

		    const kFilter = GetFilter();

		    if (kFilter.nStartMin > kFilter.nEndMin)	// 분까지 비교
		    {
		        if (bStartChanged)
		        {
		            kEnd.value			= kStart.value;
		            kEndHour.value		= kStartHour.value;
		            kEndMinute.value	= kStartMinute.value;
		        }
		        else
		        {
		            kStart.value		= kEnd.value;
		            kStartHour.value	= kEndHour.value === "24" ? "23" : kEndHour.value;
		            kStartMinute.value	= kEndHour.value === "24" ? "59" : kEndMinute.value;
		        }
		    }

		    ShowStatus();
		}

		function AddCell(kRow, sText, sClass)	// 표 칸 추가 (textContent)
		{
		    const kCell = kRow.insertCell();

		    kCell.textContent = sText;

		    if (sClass !== "")
		    {
		        kCell.className = sClass;
		    }

		    return kCell;
		}

		const LINE_PAD	= 8;	// 줄 보기 pre 기본 위·아래 여백 (px, CSS pre padding)
		const LINE_NEAR	= 20;	// 줄 보기 이어 읽기 시작 (읽은 끝에서 X줄 안)

		function GetLineHeight(kBody)	// 줄 보기 한 줄 높이 (소수까지: offsetHeight 반올림 → 줄 많으면 위치 어긋남, 줄 없음: 0)
		{
		    return kBody.querySelector(".line")?.getBoundingClientRect().height ?? 0;
		}

		function SetLineSpace(kBody, kState)	// 줄 보기 위·아래 빈 높이: 안 읽은 줄 수 × 줄 높이 → 스크롤 바 처음부터 전체 크기 (kState.nExtra: 짧은 파일 아래 여백)
		{
		    const nLineH = GetLineHeight(kBody);

		    kBody.style.paddingTop		= nLineH > 0 && kState.nFirst > 1 ? `${LINE_PAD + (kState.nFirst - 1) * nLineH}px` : "";
		    kBody.style.paddingBottom	= nLineH > 0 ? `${LINE_PAD + Math.max(0, kState.nTotal - kState.nLast) * nLineH + (kState.nExtra ?? 0)}px` : "";
		}

		function GetLineRange(kScroll, kBody)	// 줄 보기 보이는 줄 번호 [첫, 끝] (빈 높이 포함, 줄 없음: null)
		{
		    const nLineH = GetLineHeight(kBody);

		    if (nLineH === 0)
		    {
		        return null;
		    }

		    const nTop = kScroll.scrollTop - kBody.offsetTop - LINE_PAD;	// 첫 줄 위치 기준

		    return [Math.max(1, Math.floor(nTop / nLineH) + 1), Math.max(1, Math.ceil((nTop + kScroll.clientHeight) / nLineH))];
		}

		function TrimLines(kBody, kState, nMax, bTop)	// 줄 보기 최대 줄 수 넘음: 반대쪽 줄 지움 (bTop: 위쪽, 빈 높이로 바뀌어 위치 유지)
		{
		    for (let nOver = kState.nLast - kState.nFirst + 1 - nMax; nOver > 0; --nOver)
		    {
		        if (bTop)
		        {
		            kBody.firstElementChild?.remove();
		            ++kState.nFirst;
		        }
		        else
		        {
		            kBody.lastElementChild?.remove();
		            --kState.nLast;
		        }
		    }
		}

		function FillLines(kScroll, kBody, kState, nCount, funcLoad)	// 줄 보기 스크롤: 빈 높이 근처면 funcLoad(시작 줄, 줄 수, 방향) (가까움: 위 -1·아래 1 이어 읽기, 멂: 그 위치로 바꿈 2)
		{
		    const arrRange = GetLineRange(kScroll, kBody);

		    if (kState == null || kState.bLoading || kState.bError || kState.nFirst === 0 || kState.nTotal === 0 || arrRange == null)
		    {
		        return;
		    }

		    const [nTop, nBottom] = arrRange;

		    if (nBottom < kState.nFirst - nCount || nTop > kState.nLast + nCount)	// 멂: 그 위치로 바꿈
		    {
		        const nStart = Math.max(1, nTop - nCount);

		        funcLoad(nStart, Math.min(kState.nTotal, nBottom + nCount) - nStart + 1, 2);
		    }
		    else if (kState.nFirst > 1 && nTop - LINE_NEAR < kState.nFirst)	// 위 가까움: 앞 줄
		    {
		        const nStart = Math.max(1, Math.min(kState.nFirst - nCount, nTop - LINE_NEAR));

		        funcLoad(nStart, kState.nFirst - nStart, -1);
		    }
		    else if (kState.nLast < kState.nTotal && nBottom + LINE_NEAR > kState.nLast)	// 아래 가까움: 뒤 줄
		    {
		        funcLoad(kState.nLast + 1, Math.max(nCount, nBottom + LINE_NEAR - kState.nLast), 1);
		    }
		}

		function GetRestColumn(kTable)	// 나머지 너비 칸 번호 (data-rest, 없음: 마지막 칸)
		{
		    return Number(kTable.dataset.rest ?? kTable.tHead.rows[0].cells.length - 1);
		}

		function FixColumn(kTable)	// 칸 너비 고정 (처음 드래그 놓을 때 자동 너비 → px, 나머지 너비 칸: 표 오른쪽 끝 고정)
		{
		    if (kTable.classList.contains("fixed"))
		    {
		        return;
		    }

		    const arrCol	= kTable.querySelectorAll("col");
		    const nRest		= GetRestColumn(kTable);
		    const arrWidth	= [...kTable.tHead.rows[0].cells].map(kHead => kHead.getBoundingClientRect().width);	// 먼저 모두 읽음 (쓰기 사이 읽기: 칸마다 다시 배치)

		    arrWidth.forEach((nWidth, nIndex) =>
		    {
		        if (nIndex !== nRest)
		        {
		            arrCol[nIndex].style.width = nWidth + "px";
		        }
		    });

		    SetFixed(kTable, true);
		}

		function ResetColumn(kTable)	// 칸 너비 자동으로 되돌림 (칸 경계 더블 클릭)
		{
		    for (const kCol of kTable.querySelectorAll("col"))
		    {
		        kCol.style.width = "";
		    }

		    SetFixed(kTable, false);
		}

		function SetFixed(kTable, bFixed)	// 칸 너비 고정 켜기·끄기: tbody 떼고 바꾼 뒤 다시 붙임 (붙은 채: 줄 수 제곱으로 느림)
		{
		    const kBody = kTable.tBodies[0];	// 결과 줄 (표 맨 끝)

		    kBody.remove();
		    kTable.classList.toggle("fixed", bFixed);
		    kTable.append(kBody);
		}

		function GetColumnMin(kHead)	// 칸 최소 너비: 제목 첫 글자 + 왼쪽 여백·테두리
		{
		    const kStyle	= getComputedStyle(kHead);
		    const kRange	= document.createRange();

		    kRange.setStart(kHead.firstChild, 0);
		    kRange.setEnd(kHead.firstChild, 1);

		    return Math.ceil(kRange.getBoundingClientRect().width + parseFloat(kStyle.paddingLeft) + parseFloat(kStyle.borderLeftWidth) + parseFloat(kStyle.borderRightWidth));
		}

		async function LoadMachine()	// 접속 머신 목록 조회 (바뀐 경우만 다시 그림 → 체크·열린 드롭 박스 유지)
		{
		    if (bMachineLoading)
		    {
		        return;
		    }

		    bMachineLoading = true;

		    try
		    {
		        const kData			= await FetchJson("/api/client", { cache: "no-store" });
		        const arrMachine	= [...new Set(kData.arrClient.map(kClient => kClient.sMachine).filter(sMachine => sMachine !== ""))].sort();
		        const arrOption		= arrMachine.map(sMachine => [sMachine, sMachine]);	// [값, 표시]

		        for (const sCheck of setMachineCheck)	// 체크 머신 끊김: 체크 유지
		        {
		            if (!arrMachine.includes(sCheck))
		            {
		                arrOption.push([sCheck, `${sCheck} (접속 끊김)`]);
		            }
		        }

		        const sSignature = JSON.stringify(arrOption);

		        if (sSignature !== sMachineSignature)
		        {
		            arrMachineList		= arrOption.map(([sValue]) => sValue);
		            sMachineSignature	= sSignature;

		            RenderMachine(arrOption);
		        }

		        nMachineCount = arrMachine.length;
		    }
		    catch (kError)	// 조회 실패: 기존 목록 유지
		    {
		        console.error(kError);
		    }
		    finally
		    {
		        bMachineLoading = false;
		    }

		    ShowStatus();
		}

		function GetMachines()	// 대상 머신 (체크 없음: 전체, 목록 순서)
		{
		    return setMachineCheck.size === 0 ? arrMachineList : arrMachineList.filter(sMachine => setMachineCheck.has(sMachine));
		}

		function GetMachineText()	// 머신 표시 글자 (전체 · 1대: 이름 · 여러 대: 첫 머신 외 N대)
		{
		    const arrCheck = arrMachineList.filter(sMachine => setMachineCheck.has(sMachine));

		    return arrCheck.length === 0 ? "전체" : (arrCheck.length === 1 ? arrCheck[0] : `${arrCheck[0]} 외 ${arrCheck.length - 1}대`);
		}

		function RenderMachine(arrOption)	// 드롭 박스 목록 다시 그림: 왼쪽 체크 박스 + 이름 (끊긴 체크 머신: '(접속 끊김)', 글자는 textContent 로만)
		{
		    kMachineList.replaceChildren(...arrOption.map(([sValue, sLabel]) =>
		    {
		        const kLabel	= document.createElement("label");
		        const kCheck	= document.createElement("input");
		        const kName		= document.createElement("span");

		        kCheck.type			= "checkbox";
		        kCheck.value		= sValue;
		        kCheck.checked		= setMachineCheck.has(sValue);
		        kName.textContent	= sLabel;

		        kLabel.append(kCheck, kName);

		        return kLabel;
		    }));

		    ShowMachine();
		}

		function ShowMachine()	// 체크 상태 → 드롭 박스 글자·전체 체크 (체크 없음: 전체, 하위 체크: 선택) + 상태 줄
		{
		    kMachineBox.textContent	= arrMachineList.some(sMachine => setMachineCheck.has(sMachine)) ? "선택" : "전체";	// 머신 이름은 펼친 목록에서
		    kMachineAll.checked		= setMachineCheck.size === 0;

		    ShowStatus();
		}

		function OnMachineBox()	// 드롭 박스 클릭: 목록 펼침·접음 (펼칠 때 접속 머신 다시 조회)
		{
		    kMachineMenu.hidden = !kMachineMenu.hidden;
		    kMachineBox.setAttribute("aria-expanded", String(!kMachineMenu.hidden));

		    if (!kMachineMenu.hidden)
		    {
		        LoadMachine();
		    }
		}

		function OnMachineCheck(kEvent)	// 머신 체크 → 고른 머신 추가·제외 (모두 해제: 전체)
		{
		    if (kEvent.target.checked)
		    {
		        setMachineCheck.add(kEvent.target.value);
		    }
		    else
		    {
		        setMachineCheck.delete(kEvent.target.value);
		    }

		    ShowMachine();
		}

		function OnMachineAll()	// 전체 체크 → 고른 머신 모두 해제 (전체 해제: 그대로 전체)
		{
		    setMachineCheck.clear();

		    for (const kCheck of kMachineList.querySelectorAll("input"))
		    {
		        kCheck.checked = false;
		    }

		    ShowMachine();
		}

		function OnMachineOutside(kEvent)	// 드롭 박스 밖 클릭, Esc: 목록 접음
		{
		    if (kMachineMenu.hidden || (kEvent.type === "click" && kMachinePick.contains(kEvent.target)) || (kEvent.type === "keydown" && kEvent.key !== "Escape"))
		    {
		        return;
		    }

		    kMachineMenu.hidden = true;
		    kMachineBox.setAttribute("aria-expanded", "false");
		}


		function OpenWindow(sUrl, nWidth, nHeight, bCenterWindow = false)	// 새 창: 크기 지정 + 모니터 화면 중앙 (bCenterWindow: 브라우저 창 중앙, sOpenMode browser: 같은 크기·자리, tab: 새 탭)
		{
		    if (sOpenMode === "tab")	// Alt: 새 탭 (클릭 이벤트 끝난 뒤 열기)
		    {
		        setTimeout(() => window.open(sUrl, "_blank"));

		        return;
		    }

		    if (sOpenMode === "browser")	// Ctrl: 같은 바깥 크기·자리 (open 크기: 안쪽 → 열고 바깥 크기로 맞춤)
		    {
		        const nOuterWidth	= window.outerWidth;	// 누른 때 바깥 크기·자리
		        const nOuterHeight	= window.outerHeight;
		        const nScreenX		= window.screenX;
		        const nScreenY		= window.screenY;
		        const sInner		= `width=${window.innerWidth},height=${window.innerHeight},left=${nScreenX},top=${nScreenY}`;	// 안쪽 내용 크기 (같은 화면이면 바깥도 거의 같음)

		        setTimeout(() =>	// 클릭 이벤트 끝난 뒤 열기 (이벤트 안: Chrome 이 눌린 Ctrl 보고 뒤 탭으로 엶, 클릭 직후: 팝업 허용 유지)
		        {
		            const kWindow = window.open(sUrl, "_blank", sInner);

		            try
		            {
		                kWindow?.resizeTo(nOuterWidth, nOuterHeight);	// 바깥 크기 맞춤 (탭·주소 줄 차이)
		                kWindow?.moveTo(nScreenX, nScreenY);
		            }
		            catch (kError)	// 크기 바꾸기 막힘: 안쪽 크기 그대로
		            {
		                console.error(kError);
		            }
		        });

		        return;
		    }

		    const nLeft	= Math.round(bCenterWindow ? window.screenX + Math.max(0, window.outerWidth - nWidth) / 2 : (screen.availLeft || 0) + Math.max(0, screen.availWidth - nWidth) / 2);
		    const nTop	= Math.round(bCenterWindow ? window.screenY + Math.max(0, window.outerHeight - nHeight) / 2 : (screen.availTop || 0) + Math.max(0, screen.availHeight - nHeight) / 2);

		    window.open(sUrl, "_blank", `width=${nWidth},height=${nHeight},left=${nLeft},top=${nTop}`);
		}

		function GetOpenTarget(kEvent)	// Ctrl·Alt+클릭 새 창 대상 → { kTarget, sKind: fileview (파일 보기 제목)·self (원래 새 창)·file (파일명)·link·state (그 외 UI) } (null: 원래 동작)
		{
		    if (kEvent.button !== 0 || kEvent.ctrlKey === kEvent.altKey || kEvent.target.closest?.(`${OPEN_WORD}, dialog, ${OPEN_SKIP}`) != null)
		    {
		        return null;
		    }

		    const kTarget = kEvent.target.closest?.(OPEN_TARGET) ?? null;	// 누른 UI (제목란 안 버튼 포함: 그 버튼)

		    if (kTarget == null)	// UI 아님: 하위 창 제목란 (제목 글자·빈 곳)
		    {
		        const kTitle = kEvent.target.closest?.(OPEN_TITLE) ?? null;	// 누른 하위 창 제목란

		        if (kTitle == null)
		        {
		            return null;
		        }

		        if (kEvent.target.closest("#FileViewTitle") != null && HTFile.kFileView != null)	// 파일 1번 창 파일 보기 제목: 보는 파일 (마지막 줄부터)
		        {
		            return { kTarget: kTitle, sKind: "fileview", kView: { sMachine: HTFile.kFileView.sMachine, sPath: HTFile.kFileView.sPath, nTarget: 0, sKey: "" } };
		        }

		        if (kEvent.target.closest("#ViewPanel > .filter") != null && HTSearch.kView != null)	// 서치 2번 창 제목: 보는 파일 (그 줄 강조)
		        {
		            return { kTarget: kTitle, sKind: "fileview", kView: HTSearch.kView };
		        }

		        return document.body.dataset.mode === "main" ? { kTarget: kTitle, sKind: "state" } : null;	// 지금 상태 첫 화면 (보는 탭·입력칸, 쿼리 결과 보던 중: 다시 쿼리, 새 창 모드: 원래 동작)
		    }

		    if (kTarget.matches(OPEN_SELF))
		    {
		        return { kTarget, sKind: "self" };
		    }

		    if (kTarget.matches("td.filename, #FileTree div.node.view"))
		    {
		        return { kTarget, sKind: "file" };
		    }

		    if (kTarget.matches("a[href]"))
		    {
		        return { kTarget, sKind: "link" };
		    }

		    return document.body.dataset.mode === "main" ? { kTarget, sKind: "state" } : null;	// 새 창 모드: 지금 상태 복원 없음 → 원래 동작
		}

		function OnOpenMouseDown(kEvent)	// Ctrl·Alt+누름 (새 창 대상): 포커스·드롭 박스 펼침·글자 선택 막음
		{
		    if (GetOpenTarget(kEvent) != null)
		    {
		        kEvent.preventDefault();
		    }
		}

		function OnOpenClick(kEvent)	// Ctrl+클릭: 새 창, Alt+클릭: 새 탭 (지금 창 그대로)
		{
		    const kOpen = GetOpenTarget(kEvent);	// 대상 (null: 원래 동작)

		    if (kOpen == null)
		    {
		        return;
		    }

		    if (kOpen.sKind === "self")	// 원래 새 창 여는 것 (필드 이름): 그 동작 그대로
		    {
		        sOpenMode = kEvent.altKey ? "tab" : "browser";

		        setTimeout(() => sOpenMode = "");	// 원래 클릭 처리 뒤 되돌림

		        return;
		    }

		    kEvent.preventDefault();
		    kEvent.stopPropagation();	// 원래 클릭 처리 안 함 (지금 창 그대로)

		    sOpenMode = kEvent.altKey ? "tab" : "browser";	// Ctrl: 새 창, Alt: 새 탭

		    try
		    {
		        if (kOpen.sKind === "fileview")	// 파일 보기 제목: 보는 파일
		        {
		            OpenWindow("/view?" + new URLSearchParams({ sMachine: kOpen.kView.sMachine, sPath: kOpen.kView.sPath, nTarget: String(kOpen.kView.nTarget ?? 0), sKey: kOpen.kView.sKey ?? "" }), 1000, 900, true);
		        }
		        else if (kOpen.sKind === "file")
		        {
		            OpenWindow(GetOpenFileUrl(kOpen.kTarget), 1000, 900, true);	// 파일 보기 (브라우저 창 중앙)
		        }
		        else if (kOpen.sKind === "link")
		        {
		            OpenWindow(kOpen.kTarget.href, 1600, 1000);
		        }
		        else
		        {
		            OpenWindow(GetStateUrl(kOpen.kTarget), 1600, 1000);
		        }
		    }
		    finally
		    {
		        sOpenMode = "";
		    }
		}

		function GetOpenFileUrl(kTarget)	// 파일명 → 파일 보기 주소 (결과 줄 File 칸: 그 줄 강조, 파일 트리: 마지막 줄부터)
		{
		    return "/view?" + new URLSearchParams({ sMachine: kTarget.dataset.machine, sPath: kTarget.dataset.path, nTarget: kTarget.dataset.line ?? "0", sKey: kTarget.dataset.key ?? "" });
		}

		function GetStateUrl(kTarget)	// 지금 상태 첫 화면 주소 (탭·머신 체크·입력칸·쿼리 결과 + 누른 UI id → 새 창 RestoreState)
		{
		    const kControl	= kTarget.matches("label") ? kTarget.control : kTarget;	// 라벨: 그 입력칸
		    const kParam	= new URLSearchParams({ sTab: document.querySelector("#TabBar button.cur")?.id ?? kQueryButton.id, sMachine: [...setMachineCheck].join(","), bQuery: HTQuery.sQueryKey !== "" ? "1" : "0", sClick: kControl?.id ?? "" });

		    for (const sId of OPEN_STATE_INPUT)
		    {
		        kParam.set(sId, document.getElementById(sId).value);
		    }

		    return location.pathname + "?" + kParam;
		}

		function GetOpenerState()	// 연 창의 다른 탭 기간·← 단계 →·단계 결과 (복사본, 연 창 없음·접근 막힘: null)
		{
		    try
		    {
		        const kOpener = window.opener;	// 연 창 (같은 출처)

		        if (kOpener == null || kOpener.closed || kOpener.HTQuery == null || kOpener.HTSearch == null)
		        {
		            return null;
		        }

		        return structuredClone({ kOtherRange: kOpener.HTQuery.kOtherRange, arrQueryHistory: kOpener.HTQuery.arrHistory, arrQueryResult: kOpener.HTQuery.arrHistory.map(kStep => kOpener.HTQuery.mapStepResult?.get(kStep) ?? null), nQueryHistory: kOpener.HTQuery.nHistory, arrSearchHistory: kOpener.HTSearch.arrHistory, arrSearchResult: kOpener.HTSearch.arrHistory.map(kStep => kOpener.HTSearch.mapStepResult?.get(kStep) ?? null), nSearchHistory: kOpener.HTSearch.nHistory });
		    }
		    catch (kError)	// 연 창 접근 불가
		    {
		        console.error(kError);

		        return null;
		    }
		}

		function GetPeriodPick()	// 지금 명령 창 기간 [S 날짜·시·분, E 날짜·시·분] (쿼리 D 최근 기간: 지금 기준으로 다시 계산)
		{
		    if (kQueryButton.classList.contains("cur") && kQueryRange.selectedIndex >= 0)
		    {
		        HTQuery.SetRecent();
		    }

		    return [kStart.value, kStartHour.value, kStartMinute.value, kEnd.value, kEndHour.value, kEndMinute.value];
		}

		function SetPeriodPick(arrPick)	// 명령 창 기간 넣기 (쿼리 D 해제 → S·E 그대로 쿼리)
		{
		    [kStart.value, kStartHour.value, kStartMinute.value, kEnd.value, kEndHour.value, kEndMinute.value] = arrPick;

		    kQueryRange.selectedIndex = -1;
		}

		function RestoreState(kParam)	// Ctrl·Alt+클릭 새 창 첫 화면 복원 (머신 → 탭 → 입력칸 → 연 창 기간·단계·결과 → 결과 그대로 (없음: 쿼리 다시, 서치 다시 검색 안 함 (실서버 부하)) → 누른 버튼·포커스)
		{
		    const sTab		= kParam.get("sTab") ?? "";
		    const kClick	= document.getElementById(kParam.get("sClick") ?? "");	// 누른 UI (없음: null)
		    const bButton	= kClick?.matches("button") === true;

		    history.replaceState(null, "", location.pathname);	// 새로 고침: 처음 화면 (다시 누르지 않음)

		    for (const sMachine of (kParam.get("sMachine") ?? "").split(",").filter(sItem => arrMachineList.includes(sItem)))	// 접속 안 된 머신: 제외 (모두 없으면 전체)
		    {
		        setMachineCheck.add(sMachine);
		    }

		    for (const kCheck of kMachineList.querySelectorAll("input[type=checkbox]"))
		    {
		        kCheck.checked = setMachineCheck.has(kCheck.value);
		    }

		    ShowMachine();

		    if (sTab !== kQueryButton.id && document.querySelector(`#TabBar #${CSS.escape(sTab)}`) != null)	// 쿼리 외 탭 (쿼리: 처음부터 보는 탭)
		    {
		        document.getElementById(sTab).click();
		    }

		    for (const sId of OPEN_STATE_INPUT)	// 탭 바꾼 뒤 (서치·쿼리 기간 따로 보관 → 바꿀 때 바뀜)
		    {
		        if (kParam.has(sId))
		        {
		            document.getElementById(sId).value = kParam.get(sId);
		        }
		    }

		    const kOpener = GetOpenerState();	// 연 창 다른 탭 기간·← 단계 → (못 가져옴: null → 입력칸만)

		    if (kOpener != null)
		    {
		        HTQuery.kOtherRange	= kOpener.kOtherRange;	// 다른 탭 (서치 ↔ 쿼리) 기간: 탭 바꿀 때 꺼냄
		        HTQuery.arrHistory	= kOpener.arrQueryHistory;
		        HTQuery.nHistory	= kOpener.nQueryHistory;
		        HTSearch.arrHistory	= kOpener.arrSearchHistory;
		        HTSearch.nHistory	= kOpener.nSearchHistory;

		        kOpener.arrQueryResult.forEach((kResult, nIndex) =>	// 쿼리 단계 결과 (지금 단계·← →: 다시 쿼리 안 함)
		        {
		            if (kResult != null && HTQuery.arrHistory[nIndex] != null)
		            {
		                HTQuery.mapStepResult.set(HTQuery.arrHistory[nIndex], kResult);
		            }
		        });

		        kOpener.arrSearchResult.forEach((kResult, nIndex) =>	// 서치 단계 결과 (지금 단계·← →: 다시 검색 안 함)
		        {
		            if (kResult != null && HTSearch.arrHistory[nIndex] != null)
		            {
		                HTSearch.SaveStepResult(HTSearch.arrHistory[nIndex], kResult);
		            }
		        });

		        HTQuery.ShowHistory();
		    }

		    if (kParam.get("bMove") === "1")	// Shift 옮겨 실행: 다른 탭도 같은 기간 (돌아가도 이 기간)
		    {
		        HTQuery.kOtherRange = { sStart: kStart.value, sStartHour: kStartHour.value, sStartMinute: kStartMinute.value, sEnd: kEnd.value, sEndHour: kEndHour.value, sEndMinute: kEndMinute.value, nRange: -1 };
		    }

		    ShowStatus();

		    if (sTab === kQueryButton.id && kParam.get("bQuery") === "1")	// 쿼리 결과 보던 중: 그 결과 그대로 (결과 없음·버튼 아님: 같은 조건으로 다시)
		    {
		        const kStep		= HTQuery.arrHistory[HTQuery.nHistory];									// 연 창 지금 단계 (없음: undefined)
		        const kResult	= kStep === undefined ? undefined : HTQuery.mapStepResult.get(kStep);	// 그 결과 (연 창에서 복사)

		        if (kResult?.kPage != null)
		        {
		            HTQuery.ShowStep(kStep, kResult);
		        }
		        else if (!bButton && kStep !== undefined)
		        {
		            HTQuery.GoHistory(0);
		        }
		        else if (!bButton)
		        {
		            HTQuery.RunQuery();
		        }
		    }

		    const kSearchResult = HTSearch.mapStepResult.get(HTSearch.arrHistory[HTSearch.nHistory]);	// 연 창 지금 서치 단계 결과 (없음: undefined)

		    if (sTab === kLogButton.id && kSearchResult !== undefined)	// 서치 결과 보던 중: 그 결과 그대로 (없음: 다시 검색 안 함)
		    {
		        HTSearch.ShowStep(kSearchResult);
		    }

		    if (bButton && kClick.id !== sTab && !kClick.disabled)
		    {
		        kClick.click();
		    }
		    else if (!bButton)
		    {
		        kClick?.focus();
		    }
		}

		function GetShiftMove(kEvent)	// Shift+클릭 쿼리 ↔ 서치 옮겨 실행 대상 → { sKey, bToSearch, bRun (false: 명령·기간 복사만) } (첫 화면만, null: 원래 동작)
		{
		    if (kEvent.button !== 0 || !kEvent.shiftKey || (kEvent.ctrlKey && kEvent.altKey) || document.body.dataset.mode !== "main" || kEvent.target.closest?.("dialog") != null)
		    {
		        return null;
		    }

		    if (kEvent.target === kQueryKey || kEvent.target === kKey)	// 입력칸: 입력한 명령 (쿼리칸 → 서치, 검색어 → 쿼리)
		    {
		        return { sKey: kEvent.target.value.trim(), bToSearch: kEvent.target === kQueryKey, bRun: false };	// 입력칸: 명령·기간 복사만 (실행 안 함)
		    }

		    const kRun = kEvent.target.closest?.("#QueryRunButton, #SearchButton");	// 실행 버튼: 그 탭 입력한 명령 (쿼리 실행 → 서치, 서치 실행 → 쿼리)

		    if (kRun != null)
		    {
		        return kRun === kQueryRunButton ? { sKey: kQueryKey.value.trim(), bToSearch: true, bRun: true } : { sKey: kKey.value.trim(), bToSearch: false, bRun: true };
		    }

		    const kField = kEvent.target.closest?.("#QueryBody .queryfield span[data-field]");	// 펼친 줄 필드 값: 값만 서치

		    if (kField != null)
		    {
		        return { sKey: kField.dataset.value, bToSearch: true, bRun: true };
		    }

		    const bQuery = kEvent.target.closest?.(SHIFT_QUERY_WORD) != null;	// 쿼리 단어 (아님: 서치 단어)

		    if (!bQuery && kEvent.target.closest?.(HTSearch.WORD_BOX) == null)
		    {
		        return null;
		    }

		    let sWord = bQuery ? HTQuery.WordAt(kEvent.clientX, kEvent.clientY, "td.log, .querytext", true) : HTQuery.WordAt(kEvent.clientX, kEvent.clientY, HTSearch.WORD_BOX, false);	// 누른 단어 (Ctrl·Alt 단어 클릭과 같음)

		    if (!/[^-_]/.test(sWord))	// 단어 없음 (-·_ 만)
		    {
		        return null;
		    }

		    if (/^(AND|OR|NOT)$/.test(sWord))	// 연산자 글자: 소문자 단어로
		    {
		        sWord = sWord.toLowerCase();
		    }

		    return { sKey: sWord, bToSearch: bQuery, bRun: true };
		}

		function OnShiftMouseDown(kEvent)	// Shift+누름 (쿼리 ↔ 서치 대상): 다른 탭 입력칸에 명령·기간 옮김 (Shift+Ctrl: 새 창, Shift+Alt: 새 탭), 포커스·선택·새 창·단어 추가 막음
		{
		    bShiftClick = false;

		    const kMove = GetShiftMove(kEvent);	// 대상 (null: 원래 동작)

		    if (kMove == null)
		    {
		        return;
		    }

		    kEvent.preventDefault();
		    kEvent.stopImmediatePropagation();	// Ctrl·Alt 새 창·단어 추가 안 함

		    bShiftClick = true;

		    if (kMove.sKey === "" || HTSearch.bSearching || (!kMove.bToSearch && HTQuery.bQuerying))	// 빈 명령·검색 중 (탭 잠김)·쿼리 중: 안 함
		    {
		        return;
		    }

		    const arrPick = GetPeriodPick();	// 지금 탭 기간 (옮긴 탭도 같은 기간, 쿼리 D: 지금 기준)

		    const kInput = kMove.bToSearch ? kKey : kQueryKey;	// 옮길 탭 입력칸

		    if (kEvent.ctrlKey || kEvent.altKey)	// 새 탭·새 창: RestoreState 가 그 탭 입력칸·기간 넣고 실행 (단계: 연 창에서 복사)
		    {
		        const kParam = new URLSearchParams({ sTab: kMove.bToSearch ? kLogButton.id : kQueryButton.id, sMachine: [...setMachineCheck].join(","), bQuery: "0", bMove: "1", sClick: kMove.bRun ? (kMove.bToSearch ? kSearchButton.id : kQueryRunButton.id) : kInput.id });

		        kParam.set(kInput.id, kMove.sKey);
		        [kStart.id, kStartHour.id, kStartMinute.id, kEnd.id, kEndHour.id, kEndMinute.id].forEach((sId, nIndex) => kParam.set(sId, arrPick[nIndex]));
		        kParam.set(kQueryRange.id, "");	// 쿼리 D 해제 → S·E 그대로

		        sOpenMode = kEvent.ctrlKey ? "browser" : "tab";	// Shift+Ctrl: 같은 크기·자리 새 창, Shift+Alt: 새 탭

		        try
		        {
		            OpenWindow(location.pathname + "?" + kParam, 1600, 1000);
		        }
		        finally
		        {
		            sOpenMode = "";
		        }

		        return;
		    }

		    if (kMove.bToSearch)	// 서치 탭: 검색어·지금 기간 넣고 서치 (입력칸 복사: 넣기만, 머신: 서치 것)
		    {
		        ShowPanel("log");
		        SetPeriodPick(arrPick);

		        kKey.value = kMove.sKey;
		    }
		    else	// 쿼리 탭: 쿼리칸·지금 기간 넣고 쿼리 (입력칸 복사: 넣기만, D 해제 → S·E 그대로)
		    {
		        HTQuery.LoadQuery();
		        SetPeriodPick(arrPick);

		        kQueryKey.value = kMove.sKey;

		        HTQuery.OnQueryInput();	// 선택한 저장 쿼리와 다름: 선택 해제
		    }

		    if (!kMove.bRun)	// 입력칸 복사: 그 입력칸 포커스 (마우스 뗀 뒤)
		    {
		        setTimeout(() => kInput.focus());

		        return;
		    }

		    if (kMove.bToSearch)
		    {
		        HTSearch.SearchLog();
		    }
		    else
		    {
		        HTQuery.RunQuery();
		    }
		}

		function OnShiftClick(kEvent)	// Shift+누름으로 옮겨 실행한 뒤 클릭: 막음 (줄 펼침·보기·새 창 안 함)
		{
		    if (bShiftClick)
		    {
		        bShiftClick = false;

		        kEvent.preventDefault();
		        kEvent.stopImmediatePropagation();
		    }
		}

		function OnTabKey(kEvent)	// Tab 키: 오른쪽 탭 선택 (맨 끝: 처음, 첫 화면만, 입력칸·팝업·다른 키 같이: 기본 포커스 이동)
		{
		    if (kEvent.key !== "Tab" || kEvent.shiftKey || kEvent.ctrlKey || kEvent.altKey || kEvent.metaKey || document.body.dataset.mode !== "main" || document.querySelector("dialog[open]") != null || kEvent.target.closest?.("input, textarea, select, [contenteditable]") != null)
		    {
		        return;
		    }

		    const arrTab	= [kQueryButton, kLogButton, kDumpButton, kFileButton];						// 탭 순서 (힌트 제외)
		    const kNext		= arrTab[(arrTab.findIndex(kTab => kTab.classList.contains("cur")) + 1) % arrTab.length];	// 오른쪽 탭 (맨 끝: 처음)

		    kEvent.preventDefault();

		    if (!kNext.disabled)
		    {
		        kNext.click();
		    }
		}

		function OnTableSelectAll(kEvent)	// Ctrl+A: 누른 표 전체 고름 (없음·안 보임: 맨 위 표, 팝업: 팝업 안 표, 입력칸: 브라우저 기본)
		{
		    if (!(kEvent.ctrlKey || kEvent.metaKey) || kEvent.altKey || kEvent.key.toLowerCase() !== "a" || kEvent.target.closest?.("input, textarea, select") != null)
		    {
		        return;
		    }

		    const kDialog	= document.querySelector("dialog[open]");	// 열린 팝업 (있으면 그 안 표만)
		    const funcShow	= (kItem) => kItem?.isConnected === true && kItem.tBodies[0] != null && kItem.getClientRects().length > 0 && (kDialog == null || kDialog.contains(kItem));	// 보이는 표
		    const kTable	= funcShow(kSelectTable) ? kSelectTable : [...document.querySelectorAll("table")].filter(kItem => funcShow(kItem) && kItem.tBodies[0].rows.length > 0).sort((kLeft, kRight) => kLeft.getBoundingClientRect().top - kRight.getBoundingClientRect().top)[0];	// 고를 표 (지정 없음: 맨 위)
		    const kBody		= kTable?.tBodies[0] ?? null;

		    if (kBody == null)
		    {
		        return;
		    }

		    const kRange = document.createRange();	// 표 전체 (칸 이름 + 줄)

		    kEvent.preventDefault();
		    kRange.selectNodeContents(kTable);

		    window.getSelection().removeAllRanges();
		    window.getSelection().addRange(kRange);
		}

		function ShowPanel(sPanel)	// 보이는 창 바꾸기 (log: 서치 1·2번 창, file: 파일 트리, dump: 덤프 표 (file·dump: 명령 창 숨김), query: 쿼리 창, 숨긴 창 내용 유지)
		{
		    if ((sPanel === "query") !== kFilterPanel.classList.contains("query"))	// 쿼리 모드 들어감·나감: 명령 창 기간 값 바꿔 끼움 (서치·쿼리 따로)
		    {
		        HTQuery.SwapRange(sPanel === "query");
		    }

		    kFilterPanel.classList.toggle("query", sPanel === "query");	// 쿼리 모드: M 대신 D, S·E 분까지
		    kFilterPanel.hidden	= sPanel === "dump" || sPanel === "file";
		    kFilterSplitter.hidden	= kFilterPanel.hidden;	// 명령 창·결과 창 사이 바
		    kResultPanel.hidden	= sPanel !== "log" && sPanel !== "query";
		    kSplitter.hidden	= sPanel !== "log";
		    kViewPanel.hidden	= sPanel !== "log";
		    kFilePanel.hidden	= sPanel !== "file";
		    kDumpPanel.hidden	= sPanel !== "dump";
		    kQueryPanel.hidden	= sPanel !== "query";	// 쿼리 2번 창
		    kQuerySplitter.hidden	= sPanel !== "query";	// 쿼리 1·2번 창 사이 바
		    kResultPanel.classList.toggle("query", sPanel === "query");	// 쿼리 1번 창: 서치 1번 창 틀·← 단계 →·새 창 (검색 결과 내용은 유지)

		    HTQuery.ShowHistory();	// ← 단계 →: 보는 탭 단계
		    HTQuery.FitLogHeight();	// 쿼리 2번 창 로그 표 높이: 브라우저 창에 맞춤

		    kLogButton.classList.toggle("cur", sPanel === "log");
		    kFileButton.classList.toggle("cur", sPanel === "file");
		    kDumpButton.classList.toggle("cur", sPanel === "dump");
		    kQueryButton.classList.toggle("cur", sPanel === "query");

		    if (document.activeElement?.closest("#TabBar") != null && !document.activeElement.classList.contains("cur"))	// 이전 탭 버튼 포커스 테두리 제거
		    {
		        document.activeElement.blur();
		    }

		    ShowStatus();	// 설정 버튼 툴팁 (쿼리 모드: 쿼리 설정)
		    HTSearch.FitResultHeight();	// 서치 1번 창 높이 (쿼리 모드: 기준 높이)
		    HTDump.FitDumpHeight();	// 덤프 1번 창 높이: 브라우저 창에 맞춤
		}

		function OnSettingButton()	// 설정 버튼 (쿼리 모드: 쿼리 설정, 그 외: 검색 설정)
		{
		    if (kQueryButton.classList.contains("cur"))	// 쿼리 모드
		    {
		        HTQuery.OpenSetting();

		        return;
		    }

		    OpenSetting();
		}

		function FitHeight(kScroll, kPanel)	// 상자 높이: 브라우저 창 아래 끝에 맞춤 (상자 아래 페이지 번호·여백까지 한 화면, 목록 많음: 상자 안 스크롤, 최소 HTSearch.SPLIT_MIN px, 새 창 모드·숨김: 안 함)
		{
		    if (document.body.dataset.mode !== "main" || kPanel.hidden)
		    {
		        return;
		    }

		    const kRect		= kScroll.getBoundingClientRect();
		    const nFrame	= kRect.height - (parseFloat(getComputedStyle(kScroll).height) || 0);	// 상자 테두리 (height 는 안쪽만)
		    const nBelow	= kPanel.getBoundingClientRect().bottom - kRect.bottom;					// 상자 아래 (페이지 번호·여백)
		    const nPageGap	= parseFloat(getComputedStyle(document.body).paddingBottom) || 0;			// 페이지 아래 여백

		    kScroll.style.height = `${Math.max(HTSearch.SPLIT_MIN, Math.floor(window.innerHeight - (kRect.top + window.scrollY) - nBelow - nPageGap - nFrame))}px`;
		}

		function ErrorText(kError)	// 요청 오류 → 두 단어 (예: Server Off·Server Error, 서버 오류 글: 그대로)
		{
		    const kStatus = /HTTP (\d+)/.exec(kError?.message ?? "");	// HTTP 상태 (예: HTTP 500)

		    if (kError?.name === "AbortError")
		    {
		        return "User Stop";
		    }

		    if (kError?.name === "TimeoutError")
		    {
		        return "Time Out";
		    }

		    if (kError instanceof TypeError)	// fetch 접속 안 됨 (Failed to fetch 등)
		    {
		        return /fetch|network/i.test(kError.message) ? "Server Off" : "Script Error";
		    }

		    if (kError instanceof SyntaxError)	// 응답 JSON 아님
		    {
		        return "Bad Data";
		    }

		    if (kStatus != null)
		    {
		        const nStatus = Number(kStatus[1]);

		        return { 400: "Bad Request", 401: "Login Need", 403: "Access Denied", 404: "Not Found", 408: "Time Out", 500: "Server Error", 502: "Bad Gateway", 503: "Server Busy", 504: "Time Out" }[nStatus] ?? (nStatus >= 500 ? "Server Error" : "Bad Request");
		    }

		    return kError?.message || "Unknown Error";
		}

		function AskDelete(sTitle, sName, sDetail, funcYes)	// 삭제 확인 팝업 (덤프·저장 쿼리 공통, sName "": 안 보임, Enter: 취소 → 실수 삭제 방지)
		{
		    funcDeleteYes = funcYes;

		    document.getElementById("DeleteTitle").textContent	= sTitle;
		    document.getElementById("DeleteName").textContent	= sName;
		    kDeleteText.textContent								= sDetail;

		    kDeleteDialog.showModal();

		    document.getElementById("DeleteNo").focus();
		}

		function AskDownload(sName, sDetail, funcYes)	// 다운 확인 팝업 (File 칸·파일 트리·덤프 zip 공통)
		{
		    funcDownYes = funcYes;

		    document.getElementById("DownName").textContent	= sName;
		    document.getElementById("DownPath").textContent	= sDetail;

		    kDownDialog.showModal();

		    document.getElementById("DownYes").focus();	// Enter: 예
		}

		function AddFileNameCell(kRow, sMachine, sPath, nLine = 0, sKey = "")	// 결과 줄 File 칸 (서치·쿼리 공통, Shift+클릭: 다운, Ctrl·Alt+클릭: 파일 보기, nLine 0: 마지막 줄부터, sKey: 강조 검색어)
		{
		    const kCell = AddCell(kRow, sPath.slice(sPath.lastIndexOf("\\") + 1), "nowrap filename");

		    kCell.title				= sPath;
		    kCell.dataset.machine	= sMachine;
		    kCell.dataset.path		= sPath;
		    kCell.dataset.line		= String(nLine);
		    kCell.dataset.key		= sKey;
		}

		function GetShiftFile(kEvent)	// Shift+클릭 다운 대상 → { sMachine, sPath, sInfo } (File 칸·파일 트리 파일, 그 외: null)
		{
		    if (kEvent.button !== 0 || !kEvent.shiftKey || kEvent.ctrlKey || kEvent.altKey || kEvent.target.closest?.("dialog") != null)
		    {
		        return null;
		    }

		    const kFile = kEvent.target.closest?.("td.filename, #FileTree div.node[data-kind=file]") ?? null;	// 누른 파일

		    return kFile == null ? null : { sMachine: kFile.dataset.machine, sPath: kFile.dataset.path, sInfo: kFile.querySelector(".info")?.textContent ?? "" };
		}

		function OnShiftFileDown(kEvent)	// Shift+누름 파일: 글자 고르기 막음
		{
		    if (GetShiftFile(kEvent) != null)
		    {
		        kEvent.preventDefault();
		    }
		}

		function OnShiftFileClick(kEvent)	// Shift+클릭 파일: 확인 팝업 → 브라우저로 받기 (원래 클릭 막음)
		{
		    const kFile = GetShiftFile(kEvent);	// 다운할 파일 (null: 원래 동작)

		    if (kFile == null)
		    {
		        return;
		    }

		    kEvent.preventDefault();
		    kEvent.stopPropagation();

		    AskDownload(kFile.sPath.slice(kFile.sPath.lastIndexOf("\\") + 1), `${kFile.sMachine} : ${kFile.sPath}${kFile.sInfo === "" ? "" : ` (${kFile.sInfo})`}`, () => DownloadFile(kFile.sMachine, kFile.sPath));
		}

		function DownloadFile(sMachine, sPath)	// 파일 다운: 원본 그대로 브라우저로 (서버가 조각마다 바로 전달 → 진행률 표시, 상대 주소: ToolManager 경유 대응)
		{
		    const kLink = document.createElement("a");

		    kLink.href		= "api/files/download?" + new URLSearchParams({ sMachine, sPath });
		    kLink.download	= "";	// 파일명: 서버 응답 (원본 파일명)

		    kLink.click();
		}

		class HTKey	// 검색어 규칙 (서치·쿼리 공통, LMClient SearchNode 와 같음): 대문자 AND·OR·NOT 왼쪽부터, 괄호 안 먼저 + 검색어 강조
		{
		    static sSplitKey		= null;		// 마지막으로 나눈 검색어 (결과 줄마다 다시 해석하지 않음)
		    static arrSplitKey		= [];		// 그 강조할 검색어

		    static GetOperator(sKey, nPos)	// nPos 의 연산자 [길이, 종류 & | !] (대문자 AND·OR·NOT + 공백·여는 괄호, 아님: [0, ""])
		    {
		        for (const [sWord, sType] of [["AND", "&"], ["OR", "|"], ["NOT", "!"]])
		        {
		            const cNext = sKey[nPos + sWord.length];	// 연산자 뒤 글자

		            if (sKey.startsWith(sWord, nPos) && cNext !== undefined && (/\s/.test(cNext) || cNext === "("))
		            {
		                return [sWord.length, sType];
		            }
		        }

		        return [0, ""];
		    }

		    static IsClose(sKey, nPos)	// nPos 의 ')' 가 닫는 괄호 (뒤에 공백·')' 만 이어진 뒤 끝·연산자, 아님: 검색어 글자)
		    {
		        let nNext = nPos + 1;	// 뒤 글자

		        while (nNext < sKey.length && (/\s/.test(sKey[nNext]) || sKey[nNext] === ")"))
		        {
		            ++nNext;
		        }

		        return nNext >= sKey.length || HTKey.GetOperator(sKey, nNext)[0] > 0;
		    }

		    static FindTermEnd(sKey, nStart, nDepth)	// 검색어 끝: 공백 뒤 연산자·괄호 안 닫는 괄호 앞 (검색어 안 짝 맞는 괄호: 검색어 글자)
		    {
		        let nInner = 0;	// 검색어 안에서 열린 괄호 수 (예: Init(x))

		        for (let nIndex = nStart; nIndex < sKey.length; ++nIndex)
		        {
		            const cChar = sKey[nIndex];

		            if (/\s/.test(cChar))	// 공백 뒤 연산자·닫는 괄호면 여기서 끝
		            {
		                let nNext = nIndex;	// 공백 다음 글자

		                while (nNext < sKey.length && /\s/.test(sKey[nNext]))
		                {
		                    ++nNext;
		                }

		                if (nNext < sKey.length && (HTKey.GetOperator(sKey, nNext)[0] > 0 || (nDepth > 0 && nInner === 0 && sKey[nNext] === ")" && HTKey.IsClose(sKey, nNext))))
		                {
		                    return nIndex;
		                }

		                nIndex = nNext - 1;

		                continue;
		            }

		            if (cChar === "(")
		            {
		                ++nInner;
		            }
		            else if (cChar === ")" && nInner > 0)
		            {
		                --nInner;
		            }
		            else if (cChar === ")" && nDepth > 0 && HTKey.IsClose(sKey, nIndex))	// 괄호 안 검색어 끝
		            {
		                return nIndex;
		            }
		        }

		        return sKey.length;
		    }

		    static Tokenize(sKey)	// 토큰 나누기: 검색어 T·AND &·OR |·NOT !·괄호 (null: 형식 오류)
		    {
		        const arrToken	= [];	// { sType, sTerm }
		        let nPos		= 0;	// 읽을 위치
		        let nDepth		= 0;	// 열린 괄호 수
		        let bOperand	= true;	// 검색어·여는 괄호 차례 (false: 연산자·닫는 괄호 차례)

		        while (true)
		        {
		            while (nPos < sKey.length && /\s/.test(sKey[nPos]))
		            {
		                ++nPos;
		            }

		            if (nPos >= sKey.length)
		            {
		                break;
		            }

		            if (bOperand && sKey[nPos] === "(")	// 여는 괄호
		            {
		                arrToken.push({ sType: "(", sTerm: "" });

		                ++nDepth;
		                ++nPos;

		                continue;
		            }

		            if (bOperand)	// 검색어: 다음 연산자·닫는 괄호 앞까지
		            {
		                const nEnd = HTKey.FindTermEnd(sKey, nPos, nDepth);	// 검색어 끝

		                arrToken.push({ sType: "T", sTerm: sKey.slice(nPos, nEnd).trim() });

		                nPos		= nEnd;
		                bOperand	= false;

		                continue;
		            }

		            if (sKey[nPos] === ")" && nDepth > 0)	// 닫는 괄호
		            {
		                arrToken.push({ sType: ")", sTerm: "" });

		                --nDepth;
		                ++nPos;

		                continue;
		            }

		            const [nOpLength, sType] = HTKey.GetOperator(sKey, nPos);	// 연산자 길이 (0: 아님)

		            if (nOpLength === 0)	// 연산자 자리에 다른 글자 (예: (A) B)
		            {
		                return null;
		            }

		            arrToken.push({ sType: sType, sTerm: "" });

		            nPos		+= nOpLength;
		            bOperand	= true;
		        }

		        return bOperand ? null : arrToken;	// 연산자·여는 괄호로 끝남: 형식 오류
		    }

		    static ParseExpr(arrToken, kState)	// 식: 항 (연산자 항)… 왼쪽부터 차례로 (null: 형식 오류, kState.nIndex: 읽을 토큰)
		    {
		        let kLeft = HTKey.ParseTerm(arrToken, kState);	// 지금까지 결과

		        while (kLeft != null && kState.nIndex < arrToken.length && arrToken[kState.nIndex].sType !== ")")
		        {
		            const sOp = arrToken[kState.nIndex].sType;	// 연산자

		            if (sOp !== "&" && sOp !== "|" && sOp !== "!")	// 연산자 자리에 다른 토큰
		            {
		                return null;
		            }

		            ++kState.nIndex;

		            const kRight = HTKey.ParseTerm(arrToken, kState);

		            kLeft = kRight == null ? null : { sOp: sOp, kLeft: kLeft, kRight: kRight };
		        }

		        return kLeft;
		    }

		    static ParseTerm(arrToken, kState)	// 항: 검색어 또는 (식) (닫는 괄호 없이 끝나면 끝에서 닫힘, null: 형식 오류)
		    {
		        if (kState.nIndex >= arrToken.length)
		        {
		            return null;
		        }

		        const kToken = arrToken[kState.nIndex++];

		        if (kToken.sType === "T")
		        {
		            return kToken.sTerm === "" ? null : { sOp: "T", sTerm: kToken.sTerm };
		        }

		        if (kToken.sType !== "(")
		        {
		            return null;
		        }

		        const kInner = HTKey.ParseExpr(arrToken, kState);	// 괄호 안

		        if (kState.nIndex < arrToken.length && arrToken[kState.nIndex].sType === ")")
		        {
		            ++kState.nIndex;
		        }

		        return kInner;
		    }

		    static Parse(sKey)	// 검색어 → 조건 트리 { sOp: T·&·|·!, sTerm, kLeft, kRight } (빈 검색어: null, 형식 오류: 전체를 검색어 하나로)
		    {
		        const sTrim = sKey.trim();

		        if (sTrim === "")
		        {
		            return null;
		        }

		        const arrToken	= HTKey.Tokenize(sTrim);	// 토큰 (null: 형식 오류)
		        const kState	= { nIndex: 0 };				// 읽을 토큰
		        const kRoot		= arrToken == null ? null : HTKey.ParseExpr(arrToken, kState);

		        if (arrToken == null || kRoot == null || kState.nIndex !== arrToken.length)	// 형식 오류: 전체를 검색어 하나로
		        {
		            return { sOp: "T", sTerm: sTrim };
		        }

		        return kRoot;
		    }

		    static IsValid(sKey)	// 검색어 형식 맞음 (빈 괄호·연산자로 시작·끝·빈 검색어: false, 서버 SearchNode.IsValid 와 같음)
		    {
		        const sTrim = sKey.trim();

		        if (sTrim === "")
		        {
		            return false;
		        }

		        const arrToken	= HTKey.Tokenize(sTrim);	// 토큰 (null: 형식 오류)
		        const kState	= { nIndex: 0 };				// 읽을 토큰

		        return arrToken != null && HTKey.ParseExpr(arrToken, kState) != null && kState.nIndex === arrToken.length && !arrToken.some(kToken => kToken.sType === "T" && /^(AND|OR|NOT)(\s|$)|\s(AND|OR|NOT)$/.test(kToken.sTerm.trim()));	// 검색어가 대문자 AND·OR·NOT 로 시작·끝남 (예: "A AND"): 연산자 빠짐으로 봄
		    }

		    static SplitKey(sKey)	// 강조할 검색어: 조건 트리에서 찾는 쪽 검색어 모두 (NOT 오른쪽은 제외 조건 → 강조 안 함)
		    {
		        if (sKey === HTKey.sSplitKey)
		        {
		            return HTKey.arrSplitKey;
		        }

		        const arrKey	= [];						// 강조할 검색어
		        const kRoot		= HTKey.Parse(sKey);	// 조건 트리 (빈 검색어: null)

		        const funcCollect = (kNode, bFind) =>	// 검색어 모으기 (bFind: 찾는 쪽, NOT 오른쪽에서 뒤집힘)
		        {
		            if (kNode.sOp === "T")
		            {
		                if (bFind)
		                {
		                    arrKey.push(kNode.sTerm);
		                }

		                return;
		            }

		            funcCollect(kNode.kLeft, bFind);
		            funcCollect(kNode.kRight, kNode.sOp === "!" ? !bFind : bFind);
		        };

		        if (kRoot != null)
		        {
		            funcCollect(kRoot, true);
		        }

		        HTKey.sSplitKey		= sKey;
		        HTKey.arrSplitKey	= arrKey;

		        return arrKey;
		    }

		    static AppendHighlight(kParent, sText, sKey)	// 텍스트 추가 + 찾는 쪽 검색어 강조 (대소문자 무시, 텍스트 노드로만 → HTML 해석 안 함)
		    {
		        const sLower	= sText.toLowerCase();
		        const arrKey	= HTKey.SplitKey(sKey).map(sItem => sItem.toLowerCase());	// 강조할 검색어
		        let nPos		= 0;	// 추가할 위치

		        while (arrKey.length > 0)
		        {
		            let nFind	= -1;	// 가장 앞에 있는 검색어 위치 (-1: 없음)
		            let nLength	= 0;	// 그 검색어 길이 (같은 위치면 긴 것)

		            for (const sKeyLower of arrKey)
		            {
		                const nAt = sLower.indexOf(sKeyLower, nPos);

		                if (nAt !== -1 && (nFind === -1 || nAt < nFind || (nAt === nFind && sKeyLower.length > nLength)))
		                {
		                    nFind	= nAt;
		                    nLength	= sKeyLower.length;
		                }
		            }

		            if (nFind === -1)	// 남은 검색어 없음
		            {
		                break;
		            }

		            const kMark = document.createElement("mark");

		            kParent.append(sText.slice(nPos, nFind));
		            kMark.textContent = sText.slice(nFind, nFind + nLength);
		            kParent.append(kMark);

		            nPos = nFind + nLength;
		        }

		        kParent.append(sText.slice(nPos));
		    }
		}

		class HTSearch	// 서치 탭: 1번 창 검색 결과·2번 창 파일 보기 (this 없음 → 메서드를 이벤트 처리기로 바로 넘김)
		{
		    static PAGE_MAX			= 10000;	// 서치 1번 창 한 페이지 건수
		    static SPLIT_MIN		= 120;		// 서치 1·2번 창 최소 높이 (px, 가운데 바 드래그)
		    static FIT_HEIGHT_MIN	= 135;		// 서치 1번 창 기본·최소 높이 (px, 쿼리 1번 창 막대 기본 높이와 같음, CSS #ResultScroll 과 같음)
		    static FIT_ROW_MAX		= 10;		// 서치 1번 창 기본 줄 수 (넘음: 스크롤, 바 드래그 전)
		    static VIEW_COUNT		= 300;		// 서치 2번 창 한 번에 읽는 줄 수 (클릭한 줄 위·아래, 스크롤: 이어서)
		    static VIEW_MAX			= 20000;	// 서치 2번 창 최대 줄 수 (넘음: 반대쪽 지움 → 메모리 제한, 빈 높이로 위치 유지)
		    static VIEW_ABOVE		= 4;		// 서치 2번 창 클릭한 줄 위 줄 수
		    static VIEW_ABOVE_WINDOW	= 9;	// 보기 새 창 클릭한 줄 위 줄 수 (강조)
		    static WORD_BOX		= "#ResultBody td.log, #ViewBody .line";	// Ctrl·Alt+클릭 단어 칸 (서치 1번 창 Log 칸·2번 창 줄)
		    static bSearching		= false;	// 검색 중
		    static arrResult		= [];		// 검색 결과 전체 { sMachine, kLine } (화면에는 현재 페이지만 그림)
		    static arrReceived		= [];		// 검색 결과 받은 순서 (정렬 설정 바뀜: 이것으로 다시 정렬)
		    static sResultKey		= "";		// 결과의 검색어 (강조·보기용)
		    static nResultPage		= 0;		// 서치 1번 창 현재 페이지 (0부터)
		    static sResultHeight	= "";		// 서치 1번 창 기준 높이 ("": CSS 기본, 바 드래그: 그 높이) → 결과가 적으면 이보다 줄임
		    static nSelected		= -1;		// 선택한 결과 번호 (0부터, -1: 없음)
		    static arrHistory		= [];		// 들어온 단계 { sKey, arrPick } (0: 최초 검색, Ctrl·Alt 단어 검색마다 추가, ← →: 그 조건·마지막 결과, 결과 없음: 다시 검색)
		    static nHistory			= -1;		// 지금 단계 (← → 로 오감)
		    static LOG_TIME			= /^\[\d{4}\/\d{2}\/\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]/;	// 로그 줄 시작 시각 (고정 폭 → 문자열 비교 = 시간 비교, 결과 정렬)
		    static kView			= null;		// 보기 중인 파일 { sMachine, sPath, sKey, nTarget, nStart, nTotal }
		    static PROGRESS_MS		= 1000;		// 검색 진행 갱신 주기 (ms)
		    static RUN_NONE			= { nWait: 0, nFileTotal: 0, nFileDone: 0, nFound: 0 };	// 진행 중인 머신 합 없음 (/api/progress 응답 형식)
		    static sSearchToken		= "";		// 이번 검색 표시 (서버 진행 조회·중지용)
		    static bStopRequested	= false;	// 중지 요청됨
		    static kProgressInfo	= null;		// 진행 표시 정보 { nCount, nDone, nFileDone, nFound, kRun, nBegin } (검색 중만, kRun: 진행 중인 머신 합)
		    static bProgressLoading	= false;	// 진행 조회 중 (겹침 방지)
		    static mapStepResult	= new WeakMap();	// 단계 → 마지막 결과 { arrReceived, sResultKey, sTitle } (← →·새 창 복사: 다시 검색 안 함)
		    static arrResultStep	= [];				// 결과 보관한 단계 (보관 순서, 모든 단계 합 RESULT_LIMIT 줄 넘음: 오래된 단계부터 버림)

		    static MakeResultRow(nIndex)	// 서치 1번 창 결과 한 줄 (arrResult[nIndex], 클릭은 kResultBody 한 곳에서 처리)
		    {
		        const kItem		= HTSearch.arrResult[nIndex];
		        const kLine		= kItem.kLine;
		        const kRow		= document.createElement("tr");

		        kRow.dataset.index = nIndex;

		        AddCell(kRow, String(nIndex + 1), "num");
		        AddCell(kRow, kItem.sMachine, "nowrap");
		        AddFileNameCell(kRow, kItem.sMachine, kLine.sPath, kLine.nLine, HTSearch.sResultKey);	// 파일명 (Ctrl·Alt+클릭: 그 줄 강조)
		        AddCell(kRow, String(kLine.nLine), "num");
		        HTKey.AppendHighlight(AddCell(kRow, "", "log"), kLine.sText, HTSearch.sResultKey);

		        if (nIndex === HTSearch.nSelected)
		        {
		            kRow.className = "sel";
		        }

		        return kRow;
		    }

		    static OnResultClick(kEvent)	// 서치 1번 창 결과 클릭: 2번 창에 그 줄 주변 보기 (표 하나에서 처리 → 큰 페이지 메모리·그리기 절감)
		    {
		        const kRow = kEvent.target.closest("tr[data-index]");	// 클릭한 줄

		        if (kRow == null || HTSearch.IsWordClick(kEvent))	// Log 칸 Ctrl·Alt 클릭: 단어 추가만
		        {
		            return;
		        }

		        const nIndex	= Number(kRow.dataset.index);
		        const kItem		= HTSearch.arrResult[nIndex];

		        kResultBody.querySelector("tr.sel")?.classList.remove("sel");
		        kRow.classList.add("sel");

		        HTSearch.nSelected = nIndex;

		        HTSearch.OpenView(kItem.sMachine, kItem.kLine.sPath, kItem.kLine.nLine, HTSearch.sResultKey);
		    }

		    static IsWordClick(kEvent)	// 서치 1번 창 Log 칸·2번 창 줄 Ctrl·Alt 클릭 (첫 화면만, 새 창: 아님)
		    {
		        return (kEvent.ctrlKey || kEvent.altKey) && document.body.dataset.mode === "main" && kEvent.target.closest(HTSearch.WORD_BOX) != null;
		    }

		    static OnWordClick(kEvent)	// 단어 Ctrl+클릭: AND 단어, Alt+클릭: OR, 둘 다: NOT (키 뗄 때 서치)
		    {
		        if (HTSearch.IsWordClick(kEvent))
		        {
		            HTQuery.ClickWord(kEvent, HTSearch.WORD_BOX, false, kKey);	// 맨 앞 시간도 단어 (서치: 글자 그대로 찾음)
		        }
		    }

		    static ShowPager()	// 페이지 번호 버튼 (현재 페이지 강조, 결과 없음: 숨김)
		    {
		        const nPageCount	= Math.ceil(HTSearch.arrResult.length / HTSearch.PAGE_MAX);	// 전체 페이지 수
		        const kFragment		= document.createDocumentFragment();

		        for (let nPage = 0; nPage < nPageCount; ++nPage)
		        {
		            const kButton = document.createElement("button");

		            kButton.type		= "button";
		            kButton.textContent	= String(nPage + 1);
		            kButton.title		= `${(nPage * HTSearch.PAGE_MAX + 1).toLocaleString()} ~ ${Math.min(HTSearch.arrResult.length, (nPage + 1) * HTSearch.PAGE_MAX).toLocaleString()}`;

		            if (nPage === HTSearch.nResultPage)
		            {
		                kButton.className = "cur";
		            }

		            kButton.addEventListener("click", () =>
		            {
		                HTSearch.ShowResultPage(nPage);
		            });

		            kFragment.append(kButton);
		        }

		        kResultPager.replaceChildren(kFragment);

		        kResultPager.hidden = nPageCount === 0;
		    }

		    static ShowResultPage(nPage)	// 서치 1번 창 nPage 페이지 그리기 (비우고 PAGE_MAX 건 새로 그림, 맨 위로)
		    {
		        const nPageCount	= Math.max(1, Math.ceil(HTSearch.arrResult.length / HTSearch.PAGE_MAX));
		        const kFragment		= document.createDocumentFragment();

		        HTSearch.nResultPage = Math.min(Math.max(0, nPage), nPageCount - 1);

		        const nBegin	= HTSearch.nResultPage * HTSearch.PAGE_MAX;
		        const nEnd		= Math.min(HTSearch.arrResult.length, nBegin + HTSearch.PAGE_MAX);

		        for (let nIndex = nBegin; nIndex < nEnd; ++nIndex)
		        {
		            kFragment.append(HTSearch.MakeResultRow(nIndex));
		        }

		        kResultBody.replaceChildren(kFragment);

		        kResultScroll.scrollTop		= 0;
		        kResultScroll.scrollLeft	= 0;

		        HTSearch.ShowPager();
		        HTSearch.FitResultHeight();
		    }

		    static FitViewHeight()	// 서치 2번 창 높이: 브라우저 창 아래 끝에 맞춤 (1번 창 높이·창 크기 바뀔 때)
		    {
		        FitHeight(kViewScroll, kViewPanel);
		    }

		    static FitResultHeight()	// 서치 1번 창 높이 (결과 적음: 결과 + 빈 줄 (최소 FIT_HEIGHT_MIN), 많음: FIT_ROW_MAX 줄, 바 드래그 후: 그 높이 이하, 검색 중·숨김·쿼리 모드: 기준 높이)
		    {
		        kResultScroll.style.height = HTSearch.sResultHeight;

		        if (HTSearch.bSearching || kResultPanel.hidden || kResultPanel.classList.contains("query"))	// 검색 중 (진행 표시 공간)·숨김·쿼리 모드
		        {
		            HTSearch.FitViewHeight();

		            return;
		        }

		        const nHeight	= kResultScroll.offsetHeight - kResultScroll.clientTop * 2;	// 기준 높이 (테두리 제외, 가로 스크롤 바 포함)
		        const nBar		= nHeight - kResultScroll.clientHeight;						// 가로 스크롤 바
		        const arrRow	= kResultBody.rows;											// 지금 페이지 결과 줄
		        let nNeed		= 0;														// 맞춘 높이

		        if (arrRow.length >= HTSearch.FIT_ROW_MAX)	// 결과 많음: FIT_ROW_MAX 줄까지 (줄마다 높이 달라도 그 줄 끝까지)
		        {
		            nNeed = Math.ceil(arrRow[HTSearch.FIT_ROW_MAX - 1].getBoundingClientRect().bottom - kResultTable.getBoundingClientRect().top) + nBar;
		        }
		        else	// 결과 적음: 결과 + 빈 줄 1개 (최소 FIT_HEIGHT_MIN: 쿼리 1번 창과 같은 위치·크기)
		        {
		            const kRow = arrRow[0] ?? kResultTable.tHead.rows[0];	// 빈 줄 높이 기준 (결과 없음: 제목 줄)

		            nNeed = Math.max(HTSearch.FIT_HEIGHT_MIN, Math.ceil(kResultTable.getBoundingClientRect().height + kRow.getBoundingClientRect().height) + nBar);
		        }

		        if (HTSearch.sResultHeight === "" || nNeed < nHeight)	// 바 드래그 전: 맞춘 높이, 드래그 후: 그 높이 넘지 않음
		        {
		            kResultScroll.style.height = nNeed + "px";
		        }

		        HTSearch.FitViewHeight();
		    }

		    static AppendResult(nFrom)	// 검색 중 추가된 결과 반영: 현재 페이지에 들어갈 줄만 추가 (스크롤 유지), 페이지 번호 갱신
		    {
		        const nBegin	= HTSearch.nResultPage * HTSearch.PAGE_MAX;
		        const nEnd		= Math.min(HTSearch.arrResult.length, nBegin + HTSearch.PAGE_MAX);
		        const kFragment	= document.createDocumentFragment();

		        for (let nIndex = Math.max(nFrom, nBegin); nIndex < nEnd; ++nIndex)
		        {
		            kFragment.append(HTSearch.MakeResultRow(nIndex));
		        }

		        kResultBody.append(kFragment);

		        HTSearch.ShowPager();
		    }

		    static SortResult()	// 서치 1번 창 결과 정렬 (설정 sSort, 받은 순서 기준, 선택한 결과 유지)
		    {
		        const kSelected = HTSearch.nSelected >= 0 ? HTSearch.arrResult[HTSearch.nSelected] : null;	// 선택한 결과 (정렬 후 번호 다시 찾음)

		        HTSearch.arrResult	= sSort === SORT_TIME ? HTSearch.SortTime(HTSearch.arrReceived) : HTSearch.arrReceived;
		        HTSearch.nSelected	= kSelected == null ? -1 : HTSearch.arrResult.indexOf(kSelected);
		    }

		    static SortTime(arrItem)	// 시간 정렬 (새 배열): 시각 있는 줄 최신 위 (같음: 받은 순서), 없는 줄 맨 아래
		    {
		        const arrTime	= [];	// 시각 있는 줄 { sTime, kItem }
		        const arrOther	= [];	// 시각 없는 줄 (머신 목록 순서 그대로)

		        for (const kItem of arrItem)
		        {
		            const kMatch = HTSearch.LOG_TIME.exec(kItem.kLine.sText);

		            if (kMatch != null)
		            {
		                arrTime.push({ sTime: kMatch[0], kItem: kItem });
		            }
		            else
		            {
		                arrOther.push(kItem);
		            }
		        }

		        arrTime.sort((kLeft, kRight) => kLeft.sTime < kRight.sTime ? 1 : (kLeft.sTime > kRight.sTime ? -1 : 0));	// 시각 내림차순 (안정 정렬 → 같은 시각: 파일 순서)

		        return arrTime.map(kTime => kTime.kItem).concat(arrOther);
		    }

		    static MakeToken()	// 검색 표시 (겹치지 않는 임의 문자열, http 접속에서도 동작)
		    {
		        return Date.now().toString(36) + Math.random().toString(36).slice(2);
		    }

		    static RenderProgress(kInfo)	// 서치 1번 창 진행 표시: 시간·완료 머신·파일·찾은 줄
		    {
		        const kRun			= kInfo.kRun;	// 진행 중인 머신 합 (마지막 조회)
		        const nSec			= Math.floor((performance.now() - kInfo.nBegin) / 1000);	// 검색 시간 (초)
		        const nFileDone		= kInfo.nFileDone + kRun.nFileDone;		// 검색한 파일 (끝난 머신 + 진행 중인 머신)
		        const nFileTotal	= kInfo.nFileDone + kRun.nFileTotal;	// 대상 파일 (진행 중인 머신은 아는 것만)
		        const sWait			= kRun.nWait > 0 ? ` · 다른 검색 대기 ${kRun.nWait}대` : "";	// 클라가 앞 검색 처리 중인 머신

		        kProgress.textContent	= `${HTSearch.bStopRequested ? "중지하는 중" : "검색 중"} · ${nSec}초\n머신 ${kInfo.nDone}/${kInfo.nCount} 완료 · 파일 ${nFileDone.toLocaleString()} / ${nFileTotal.toLocaleString()}${sWait}\n찾은 줄: ${(kInfo.nFound + kRun.nFound).toLocaleString()}건`;
		        kProgress.hidden		= false;
		        kSearchButton.textContent	= `${nSec}초`;	// 서치 실행 버튼: 진행 초 (끝: 실행)
		    }

		    static async ShowProgress()	// 검색 진행 조회 (PROGRESS_MS 마다, 진행 중인 머신 파일·찾은 줄 합)
		    {
		        if (HTSearch.kProgressInfo == null)	// 검색 끝
		        {
		            return;
		        }

		        if (HTSearch.bProgressLoading)	// 앞 조회 진행 중: 시간만 갱신
		        {
		            HTSearch.RenderProgress(HTSearch.kProgressInfo);

		            return;
		        }

		        const kInfo	= HTSearch.kProgressInfo;	// 이번 검색
		        let kRun	= null;						// 진행 중인 머신 합 (null: 조회 실패 → 앞 값 유지)

		        HTSearch.bProgressLoading = true;

		        try
		        {
		            const kData = await FetchJson("/api/progress?" + new URLSearchParams({ sToken: HTSearch.sSearchToken }), { cache: "no-store", signal: AbortSignal.timeout(HTSearch.PROGRESS_MS * 5) });	// 응답 없음: 끊고 다음 조회

		            kRun = kData.bFound ? kData : HTSearch.RUN_NONE;
		        }
		        catch (kError)	// 조회 실패: 시간만 갱신
		        {
		            console.error(kError);
		        }
		        finally
		        {
		            HTSearch.bProgressLoading = false;
		        }

		        if (kInfo === HTSearch.kProgressInfo)	// 그 사이 검색이 끝나지 않음
		        {
		            kInfo.kRun = kRun ?? kInfo.kRun;

		            HTSearch.RenderProgress(kInfo);
		        }
		    }

		    static async StopSearch()	// 검색 중지: 진행 중인 머신은 모두 그때까지 결과만 받음
		    {
		        if (!HTSearch.bSearching || HTSearch.bStopRequested)
		        {
		            return;
		        }

		        const sToken = HTSearch.sSearchToken;	// 중지할 검색

		        HTSearch.bStopRequested	= true;
		        kStopButton.disabled	= true;

		        if (HTSearch.kProgressInfo != null)
		        {
		            HTSearch.RenderProgress(HTSearch.kProgressInfo);
		        }

		        try
		        {
		            const kData = await FetchJson("/api/stop?" + new URLSearchParams({ sToken }), { cache: "no-store" });

		            if (!kData.bStopped)	// 아직 중지할 머신 없음 (검색 막 시작 등)
		            {
		                throw new Error("Not Stopped");
		            }
		        }
		        catch (kError)	// 요청 실패·중지 안 됨: 같은 검색 중이면 중단 다시 누를 수 있게
		        {
		            console.error(kError);

		            if (HTSearch.bSearching && HTSearch.sSearchToken === sToken)
		            {
		                HTSearch.bStopRequested	= false;
		                kStopButton.disabled	= false;
		            }
		        }
		    }

		    static IsInMinute(kLine, nFrom, nTo)	// 결과 줄이 분 범위 안 (yyyyMMddHHmm, 시작 포함·끝 미포함, 시간 없는 줄: 그 파일 시간 정각)
		    {
		        const kMatch	= /^\[(\d{4})\/(\d{2})\/(\d{2}) (\d{2}):(\d{2})/.exec(kLine.sText);	// 줄 앞 시간
		        const nMinute	= kMatch != null ? Number(kMatch.slice(1).join("")) : kLine.nDateHour * 100;	// 줄 분

		        return nMinute >= nFrom && nMinute < nTo;
		    }

		    static async SearchLog(bChild = false, bReplay = false)	// 로그 검색: 선택 머신 동시 → 끝난 머신 결과 누적 → 끝: 정렬 → 단계 남김 (bChild: 지금 단계 하위, bReplay: ← → 다시 실행)
		    {
		        if (HTSearch.bSearching)	// 검색 중
		        {
		            return;
		        }

		        const kFilter = GetFilter();

		        if (kFilter.sKey === "")	// 검색어 없음
		        {
		            alert("검색어를 입력해 주세요.");
		            kKey.focus();

		            return;
		        }

		        if (kStart.value === "" || kEnd.value === "")	// 기간 없음
		        {
		            alert("검색 기간을 입력해 주세요.");

		            return;
		        }

		        const arrMachine = kFilter.arrMachine;	// 검색할 머신 (목록 순서)

		        if (arrMachine.length === 0)	// 접속 머신 없음
		        {
		            alert("접속된 머신이 없습니다. M 을 눌러 다시 조회해 주세요.");

		            return;
		        }

		        HTSearch.bSearching		= true;
		        kSearchButton.disabled	= true;
		        kSettingButton.disabled	= true;
		        kStopButton.disabled	= false;
		        kDumpButton.disabled	= true;
		        kFileButton.disabled	= true;
		        kQueryButton.disabled	= true;
		        HTSearch.arrResult		= [];
		        HTSearch.arrReceived	= [];
		        HTSearch.sResultKey		= kFilter.sKey;
		        HTSearch.nSelected		= -1;

		        if (!bChild && !bReplay)	// 실행 버튼·Enter: 앞 단계 모두 비움
		        {
		            HTSearch.arrHistory	= [];
		            HTSearch.nHistory	= -1;
		        }
		        else if (bChild)	// Ctrl·Alt 하위: 되돌아간 뒤의 단계 (버려질 단계) 비움
		        {
		            HTSearch.arrHistory.splice(HTSearch.nHistory + 1);
		        }

		        HTSearch.sSearchToken	= HTSearch.MakeToken();
		        HTSearch.bStopRequested	= false;
		        HTSearch.kProgressInfo	= { nCount: arrMachine.length, nDone: 0, nFileDone: 0, nFound: 0, kRun: HTSearch.RUN_NONE, nBegin: performance.now() };

		        ShowPanel("log");	// 덤프·파일·쿼리 1번 창 보던 중: 서치 1·2번 창으로 (← → 끔: 검색 중)
		        HTSearch.ShowResultPage(0);
		        HTSearch.RenderProgress(HTSearch.kProgressInfo);

		        const nProgressTimer = setInterval(HTSearch.ShowProgress, HTSearch.PROGRESS_MS);	// 진행 표시 갱신

		        const sScope		= kFilter.sMachine === "전체" ? "전체" : "선택";	// 제목 머신 범위 (전체·선택)
		        const nBegin		= performance.now();	// 검색 시작 시각

		        kResultTitle.textContent = `${sScope} - 서치 중: 머신 0/${arrMachine.length} 완료 · 0건`;	// 첫 머신 결과 전: 앞 검색 제목 지움
		        const nSearchMachineMax	= nMachineMax;		// 이번 검색 머신당 최대 (설정, 검색 중 변경 불가)
		        const nSearchResultMax	= nResultMax;		// 이번 검색 전체 최대 (설정)
		        const arrNotice		= [];					// 머신별 오류·일부 표시 안내
		        const arrOver		= [];					// 머신당 최대(nSearchMachineMax) 넘은 머신
		        const arrStopped	= [];					// 중지로 그때까지 결과만 받은 머신
		        const arrWait		= new Array(arrMachine.length);	// 받은 머신 결과 (앞 머신이 아직이면 보관, undefined: 아직 안 옴)
		        const kAbort		= new AbortController();	// 전체 최대 넘으면 결과 받기 중단
		        let nNext			= 0;					// 다음에 서치 1번 창에 넣을 머신 순서
		        let nFileCount		= 0;					// 검색한 파일 수
		        let bTotalOver		= false;				// 전체 최대(nSearchResultMax) 넘음
		        let nSkipMachine	= 0;					// 전체 최대 넘어 표시 안 한 머신 수

		        const funcAdd = (nIndex, kData) =>	// 머신 1대 결과를 서치 1번 창에 추가 (머신 순서대로 호출, 전체 최대 넘으면 bTotalOver)
		        {
		            const sMachine	= arrMachine[nIndex];
		            const kResult	= kData.kResult;

		            if (kData.sError !== "" || kResult == null)	// 미접속·시간 초과 등
		            {
		                arrNotice.push(`${sMachine}: ${kData.sError}`);

		                return;
		            }

		            const nFrom		= HTSearch.arrResult.length;	// 이번 머신 결과 시작 번호
		            const nRoom		= nSearchResultMax - nFrom;	// 전체 최대까지 남은 건수
		            const arrLine	= kResult.arrLine.filter(kLine => HTSearch.IsInMinute(kLine, kFilter.nStartMin, kFilter.nEndMin));	// 분 범위 안 줄만 (검색은 시간 파일 단위)

		            nFileCount += kResult.nFileCount;

		            for (const kLine of arrLine.slice(0, nRoom))
		            {
		                HTSearch.arrResult.push({ sMachine: sMachine, kLine: kLine });
		            }

		            HTSearch.AppendResult(nFrom);

		            if (kResult.sError !== "")
		            {
		                arrNotice.push(`${sMachine}: ${kResult.sError}`);
		            }
		            else if (kResult.bStopped)	// 중지: 그때까지 결과만 (안내는 검색 끝에 머신 수로)
		            {
		                arrStopped.push(sMachine);
		            }
		            else if (kResult.bTruncated)	// 머신당 최대 넘음 (클라가 nSearchMachineMax 건에서 중단, 안내는 검색 끝에 머신 수로)
		            {
		                arrOver.push(sMachine);
		            }

		            if (arrLine.length > nRoom || (HTSearch.arrResult.length >= nSearchResultMax && (kResult.bTruncated || nIndex < arrMachine.length - 1)))	// 전체 최대 넘음: 이후 머신 표시 안 함
		            {
		                bTotalOver		= true;
		                nSkipMachine	= arrMachine.length - nIndex - 1;
		            }
		        };

		        const funcFlush = (bEnd) =>	// 앞 머신부터 차례로 추가 (앞 머신 아직: 대기, bEnd: 결과 없는 머신 = 요청 실패)
		        {
		            while (!bTotalOver && nNext < arrMachine.length)
		            {
		                const kData = arrWait[nNext];

		                if (kData === undefined && !bEnd)	// 앞 머신 아직
		                {
		                    break;
		                }

		                arrWait[nNext] = null;	// 추가 후 해제

		                if (kData === undefined)
		                {
		                    arrNotice.push(`${arrMachine[nNext]}: 요청 실패`);
		                }
		                else
		                {
		                    funcAdd(nNext, kData);
		                }

		                ++nNext;
		            }
		        };

		        const funcLine = (sLine) =>	// 서버 결과 한 줄 = 머신 1대 (끝난 순서): 보관 → 차례로 추가 → 전체 최대 넘음: 중지·받기 중단
		        {
		            if (bTotalOver)	// 전체 최대 넘음: 이후 결과 버림
		            {
		                return;
		            }

		            const kData = JSON.parse(sLine);

		            if (!(kData.nIndex >= 0 && kData.nIndex < arrMachine.length) || arrWait[kData.nIndex] !== undefined)	// 잘못된·중복 줄
		            {
		                return;
		            }

		            arrWait[kData.nIndex] = kData;

		            HTSearch.kProgressInfo.nDone		+= 1;
		            HTSearch.kProgressInfo.nFileDone	+= kData.kResult?.nFileCount ?? 0;
		            HTSearch.kProgressInfo.nFound		+= kData.kResult?.arrLine.length ?? 0;

		            funcFlush(false);

		            kResultTitle.textContent = `${sScope} - 서치 중: 머신 ${HTSearch.kProgressInfo.nDone}/${arrMachine.length} 완료 · ${HTSearch.arrResult.length.toLocaleString()}건`;

		            if (bTotalOver)	// 전체 최대 넘음: 남은 머신 검색 중지 (게임 서버 부하), 결과 받기 중단
		            {
		                fetch("/api/stop?" + new URLSearchParams({ sToken: HTSearch.sSearchToken }), { cache: "no-store" }).catch(kError => console.error(kError));
		                kAbort.abort();
		            }
		        };

		        try
		        {
		            const kResponse = await fetch("/api/search", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ arrMachine: arrMachine, nStart: kFilter.nStart, nEnd: kFilter.nEnd, sKey: kFilter.sKey, nMax: nSearchMachineMax, sToken: HTSearch.sSearchToken }), cache: "no-store", signal: kAbort.signal });	// 모든 머신 동시 검색 (한 줄 = 머신 1대, 끝난 순서)

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kReader	= kResponse.body.pipeThrough(new TextDecoderStream()).getReader();	// UTF-8 → 문자열 조각
		            let arrPart		= [];	// 받는 중인 줄 조각 (큰 줄을 조각마다 처음부터 다시 찾지 않음)

		            while (true)
		            {
		                const { value: sChunk, done: bDone } = await kReader.read();

		                if (bDone)
		                {
		                    break;
		                }

		                let nPos = 0;	// 조각에서 다음 줄 시작

		                for (let nFind = sChunk.indexOf("\n"); nFind !== -1; nFind = sChunk.indexOf("\n", nPos))
		                {
		                    arrPart.push(sChunk.slice(nPos, nFind));
		                    funcLine(arrPart.join(""));

		                    arrPart	= [];
		                    nPos	= nFind + 1;
		                }

		                arrPart.push(sChunk.slice(nPos));
		            }
		        }
		        catch (kError)	// 요청 실패 (받은 머신까지만, 나머지는 요청 실패)·전체 최대로 받기 중단
		        {
		            if (!kAbort.signal.aborted)
		            {
		                console.error(kError);
		            }
		        }

		        clearInterval(nProgressTimer);

		        funcFlush(true);	// 남은 머신 차례로 추가 (결과 없음: 요청 실패)

		        HTSearch.arrReceived = HTSearch.arrResult;	// 받은 순서 보관

		        HTSearch.SortResult();		// 설정 정렬
		        HTSearch.ShowResultPage(0);	// 정렬한 결과를 첫 페이지부터 다시 그림

		        if (arrOver.length > 0)	// 머신당 최대 넘은 머신 수 (머신 이름은 팝업에서)
		        {
		            arrNotice.push(`머신당 최대 ${nSearchMachineMax.toLocaleString()}건 초과(머신 ${arrOver.length}대)`);
		        }

		        if (bTotalOver)
		        {
		            arrNotice.push(`전체 최대 ${nSearchResultMax.toLocaleString()}건 초과로 중단`);
		        }

		        if (arrStopped.length > 0)	// 중지로 그때까지 결과만 받은 머신 수
		        {
		            arrNotice.push(`중지로 그때까지 결과만(머신 ${arrStopped.length}대)`);
		        }

		        kResultTitle.textContent = `${sScope} - ${HTSearch.bStopRequested ? "서치 중지" : "서치 결과"}: ${HTSearch.arrResult.length.toLocaleString()}건 (머신 ${arrMachine.length}대 · 파일 ${nFileCount}개) · 서치 시간: ${Math.round(performance.now() - nBegin)} ms${arrNotice.length > 0 ? " · " + arrNotice.join(" / ") : ""}`;

		        HTSearch.bSearching		= false;
		        kSearchButton.disabled	= false;
		        kSearchButton.textContent	= "실행";
		        kSettingButton.disabled	= false;
		        kStopButton.disabled	= true;
		        kDumpButton.disabled	= HTDump.bDumpLoading;
		        kFileButton.disabled	= false;
		        kQueryButton.disabled	= false;
		        HTSearch.kProgressInfo	= null;
		        kProgress.hidden		= true;

		        if (!bReplay)	// 들어온 단계 남김 (지금 단계 뒤 버림, ← → 다시 실행: 그대로)
		        {
		            HTSearch.arrHistory.splice(HTSearch.nHistory + 1);
		            HTSearch.arrHistory.push({ sKey: kKey.value, arrPick: [kStart.value, kStartHour.value, kStartMinute.value, kEnd.value, kEndHour.value, kEndMinute.value] });

		            HTSearch.nHistory = HTSearch.arrHistory.length - 1;
		        }

		        if (!HTSearch.bStopRequested)	// 끝까지 검색: 이 단계 결과 보관 (← →·새 창: 그대로, 중지: 다음에 다시 검색)
		        {
		            HTSearch.SaveStepResult(HTSearch.arrHistory[HTSearch.nHistory], { arrReceived: HTSearch.arrReceived, sResultKey: HTSearch.sResultKey, sTitle: kResultTitle.textContent });
		        }

		        HTQuery.ShowHistory();
		        HTSearch.FitResultHeight();	// 결과가 적으면 서치 2번 창 끌어올림

		        if (arrOver.length > 0 || bTotalOver)	// 최대 초과 안내 팝업 (확인으로 닫힘): 머신당 초과 머신 목록, 맨 아래 전체 초과
		        {
		            const arrText = [];	// 팝업 줄

		            if (arrOver.length > 0)
		            {
		                arrText.push(`머신당 최대 ${nSearchMachineMax.toLocaleString()}건을 넘어 일부만 표시한 머신 (${arrOver.length}대)`);
		                arrText.push(...arrOver.map(sMachine => `- ${sMachine}`));
		            }

		            if (bTotalOver)
		            {
		                if (arrText.length > 0)
		                {
		                    arrText.push("");
		                }

		                arrText.push(`전체 최대 ${nSearchResultMax.toLocaleString()}건을 넘어 ${nSearchResultMax.toLocaleString()}건까지만 표시${nSkipMachine > 0 ? ` (표시 안 한 머신 ${nSkipMachine}대)` : ""}`);
		            }

		            alert(arrText.join("\n"));
		        }
		    }

		    static async GoHistory(nDirection)	// ← (-1)·→ (1): 들어온 단계 오가며 그 조건·마지막 결과 그대로 (결과 없음: 다시 검색, 검색 중: 중지 후)
		    {
		        if (HTSearch.bSearching)	// 검색 중: 중지 (그때까지 결과만) 뒤 이어서
		        {
		            await HTSearch.StopSearch();

		            while (HTSearch.bSearching)
		            {
		                await new Promise(funcDone => setTimeout(funcDone, 50));
		            }
		        }

		        const nIndex = HTSearch.nHistory + nDirection;	// 갈 단계

		        if (nIndex < 0 || nIndex >= HTSearch.arrHistory.length)
		        {
		            return;
		        }

		        const kStep = HTSearch.arrHistory[nIndex];	// 그 단계

		        HTSearch.nHistory = nIndex;

		        kKey.value = kStep.sKey;

		        [kStart.value, kStartHour.value, kStartMinute.value, kEnd.value, kEndHour.value, kEndMinute.value] = kStep.arrPick;

		        const kResult = HTSearch.mapStepResult.get(kStep);	// 그 단계 마지막 결과

		        if (kResult !== undefined)	// 결과 있음: 다시 검색 안 함
		        {
		            HTSearch.ShowStep(kResult);

		            return;
		        }

		        HTQuery.ShowHistory();

		        await HTSearch.SearchLog(false, true);	// 그 조건으로 다시 검색 (단계 그대로)
		    }

		    static SaveStepResult(kStep, kResult)	// 단계 결과 보관 (버린 단계: 뺌, 모든 단계 합 RESULT_LIMIT 줄 넘음: 오래된 단계부터 버림, 단계 없음: 안 함)
		    {
		        if (kStep == null)
		        {
		            return;
		        }

		        HTSearch.mapStepResult.set(kStep, kResult);

		        HTSearch.arrResultStep = HTSearch.arrResultStep.filter(kItem => kItem !== kStep && HTSearch.arrHistory.includes(kItem));
		        HTSearch.arrResultStep.push(kStep);

		        let nKeep = 0;	// 새 단계부터 센 보관 줄 수

		        for (let nIndex = HTSearch.arrResultStep.length - 1; nIndex >= 0; --nIndex)
		        {
		            nKeep += HTSearch.mapStepResult.get(HTSearch.arrResultStep[nIndex]).arrReceived.length;

		            if (nKeep > RESULT_LIMIT && nIndex < HTSearch.arrResultStep.length - 1)	// 넘음: 이 단계부터 오래된 단계 버림 (방금 단계: 남김)
		            {
		                for (const kOld of HTSearch.arrResultStep.splice(0, nIndex + 1))
		                {
		                    HTSearch.mapStepResult.delete(kOld);
		                }

		                break;
		            }
		        }
		    }

		    static ShowStep(kResult)	// 단계 보관 결과 그대로 그리기 (다시 검색 안 함, 지금 정렬 설정으로)
		    {
		        HTSearch.arrReceived	= kResult.arrReceived;
		        HTSearch.sResultKey		= kResult.sResultKey;
		        HTSearch.nSelected		= -1;

		        HTSearch.SortResult();
		        HTSearch.ShowResultPage(0);

		        kResultTitle.textContent = kResult.sTitle;

		        HTQuery.ShowHistory();
		        HTSearch.FitResultHeight();	// 결과가 적으면 서치 2번 창 끌어올림
		    }

		    static ClearView()	// 서치 2번 창 비우기 (내용·가로·세로 스크롤·위·아래 빈 높이 초기화)
		    {
		        kViewBody.replaceChildren();

		        kViewBody.style.paddingTop		= "";
		        kViewBody.style.paddingBottom	= "";

		        kViewScroll.scrollTop	= 0;
		        kViewScroll.scrollLeft	= 0;
		    }

		    static OpenView(sMachine, sPath, nTarget, sKey, nStart = null)	// 서치 2번 창 열기 (기본: 클릭한 줄 위·아래 VIEW_COUNT 줄 + 강조, nStart: 그 줄부터·0: 마지막 줄들, 스크롤: 이어서)
		    {
		        HTSearch.kView = { sMachine: sMachine, sPath: sPath, sKey: sKey, nTarget: nTarget, nAnchor: nTarget, nPos: nTarget, nShift: 0, nFirst: 0, nLast: 0, nTotal: 0, bLoading: false };	// nAnchor: 위치 0 줄 (클릭한 줄, 없음: 첫 읽기 뒤 마지막 줄), nPos: 지금 보이는 기준 줄, nShift: ← → 위치

		        HTSearch.ClearView();
		        HTSearch.ShowViewShift();

		        if (nStart != null)	// 끝 (줄 모름)
		        {
		            HTSearch.LoadView(nStart, HTSearch.VIEW_COUNT, 0);

		            return;
		        }

		        HTSearch.LoadViewPos();
		    }

		    static LoadViewPos()	// 서치 2번 창 기준 줄 (kView.nPos) 위·아래 VIEW_COUNT 줄씩 새로 읽기 (기준 줄 위 VIEW_ABOVE 줄부터 보이게)
		    {
		        const kView	= HTSearch.kView;
		        const nFrom	= Math.max(1, kView.nPos - HTSearch.VIEW_COUNT);

		        HTSearch.LoadView(nFrom, kView.nPos - nFrom + 1 + HTSearch.VIEW_COUNT, 0);
		    }

		    static ShiftView(nDirection)	// 서치 2번 창 ← (-1)·→ (1): VIEW_COUNT 줄 위·아래로 (첫 줄·끝 줄에서 멈춤, 읽는 중: 무시)
		    {
		        const kView = HTSearch.kView;

		        if (kView == null || kView.bLoading || kView.nTotal === 0 || (nDirection < 0 && kView.nPos <= 1) || (nDirection > 0 && kView.nPos >= kView.nTotal))
		        {
		            return;
		        }

		        kView.nShift	+= nDirection;
		        kView.nPos		= Math.min(Math.max(1, kView.nAnchor + kView.nShift * HTSearch.VIEW_COUNT), kView.nTotal);

		        HTSearch.ShowViewShift();
		        HTSearch.ClearView();
		        HTSearch.LoadViewPos();
		    }

		    static ShowViewShift()	// 서치 2번 창 ← → 켬·끔 (첫 줄·끝 줄: 끔), 가운데 위치 (0: 처음 연 위치)
		    {
		        const kView = HTSearch.kView;

		        kViewPrev.disabled		= kView == null || kView.nTotal === 0 || kView.nPos <= 1;
		        kViewNext.disabled		= kView == null || kView.nTotal === 0 || kView.nPos >= kView.nTotal;
		        kViewShift.textContent	= String(kView?.nShift ?? 0);
		    }

		    static FillView()	// 서치 2번 창 스크롤: 보이는 곳 줄 읽기 (FillLines, VIEW_COUNT 줄씩)
		    {
		        FillLines(kViewScroll, kViewBody, HTSearch.kView, HTSearch.VIEW_COUNT, HTSearch.LoadView);
		    }

		    static async LoadView(nStart, nCount, nDirection)	// 서치 2번 창 nStart 줄부터 nCount 줄 읽기 (nStart 0: 마지막, nDirection 0: 새로·-1: 위에 붙임·1: 아래에 붙임·2: 그 위치로 바꿈, 그 사이 다른 파일: 버림)
		    {
		        const kTarget = HTSearch.kView;	// 요청한 파일

		        if (kTarget == null || kTarget.bLoading)
		        {
		            return;
		        }

		        kTarget.bLoading	= true;
		        kViewPanel.hidden	= false;

		        if (nDirection === 0)
		        {
		            kViewTitle.textContent = `${kTarget.sMachine} : ${kTarget.sPath} (불러오는 중)`;
		        }

		        try
		        {
		            const kParam	= new URLSearchParams({ sMachine: kTarget.sMachine, sPath: kTarget.sPath, nStart: nStart, nCount: nCount });
		            const kData		= await FetchJson("/api/view?" + kParam, { cache: "no-store" });

		            if (kTarget !== HTSearch.kView)	// 그 사이 다른 파일·처음·끝
		            {
		                return;
		            }

		            const kResult	= kData.kResult;
		            const sError	= kData.sError !== "" ? kData.sError : (kResult == null ? "결과 없음" : kResult.sError);

		            if (sError !== "")
		            {
		                if (nDirection === 0)	// 새로: 비움 (이어 읽기: 보던 내용 유지)
		                {
		                    HTSearch.ClearView();
		                }

		                kTarget.bError			= true;	// 스크롤로 다시 읽지 않음 (파일을 다시 열면 처음부터)
		                kViewTitle.textContent	= `${kTarget.sMachine} : ${kTarget.sPath} (보기 실패: ${sError})`;

		                return;
		            }

		            const kFragment	= document.createDocumentFragment();	// 새 내용 (다 만든 뒤 한 번에 붙임)
		            const nCountRead	= kResult.arrText.length;				// 읽은 줄 수
		            let kPosLine		= null;									// 기준 줄 (처음: 클릭한 줄, ← →: 옮긴 줄)

		            kResult.arrText.forEach((sText, nIndex) =>
		            {
		                const nLine	= kResult.nStart + nIndex;
		                const kLine	= document.createElement("span");
		                const kNo	= document.createElement("span");

		                kLine.className	= "line";
		                kNo.className	= "lno";
		                kNo.textContent	= String(nLine).padStart(7, " ") + " : ";

		                kLine.append(kNo);
		                HTKey.AppendHighlight(kLine, sText, kTarget.sKey);

		                if (nLine === kTarget.nTarget)	// 클릭한 줄: 강조 (← → 로 옮겨도 그 줄 있으면 강조)
		                {
		                    kLine.classList.add("target");
		                }

		                if (nLine === kTarget.nPos)
		                {
		                    kPosLine = kLine;
		                }

		                kFragment.append(kLine);
		            });

		            kTarget.nTotal = kResult.nTotal;

		            HTSearch.ShowViewShift();	// ← → 켬·끔 (전체 줄 수 앎)

		            if (nDirection < 0)	// 위에 붙임: 위 빈 높이가 그만큼 줄어 보던 위치 그대로 (읽을 줄 없음: 1줄까지 읽음)
		            {
		                kViewBody.prepend(kFragment);

		                kTarget.nFirst = nCountRead > 0 ? kResult.nStart : 1;

		                TrimLines(kViewBody, kTarget, HTSearch.VIEW_MAX, false);	// 최대 넘음: 아래쪽 지움
		                SetLineSpace(kViewBody, kTarget);
		            }
		            else if (nDirection === 1)	// 아래에 붙임
		            {
		                kViewBody.append(kFragment);

		                kTarget.nLast += nCountRead;

		                TrimLines(kViewBody, kTarget, HTSearch.VIEW_MAX, true);	// 최대 넘음: 위쪽 지움
		                SetLineSpace(kViewBody, kTarget);
		            }
		            else if (nDirection === 2)	// 멀리 이동: 그 위치 줄로 바꿈 (빈 높이로 위치 그대로)
		            {
		                kViewBody.replaceChildren(kFragment);

		                kTarget.nFirst	= nCountRead > 0 ? kResult.nStart : 1;
		                kTarget.nLast	= nCountRead > 0 ? kResult.nStart + nCountRead - 1 : 0;

		                SetLineSpace(kViewBody, kTarget);
		            }
		            else	// 새로
		            {
		                HTSearch.ClearView();
		                kViewBody.append(kFragment);

		                kTarget.nFirst	= nCountRead > 0 ? kResult.nStart : 1;
		                kTarget.nLast	= nCountRead > 0 ? kResult.nStart + nCountRead - 1 : 0;
		                kTarget.nExtra	= 0;
		                kTarget.bError	= false;

		                SetLineSpace(kViewBody, kTarget);	// 위·아래 빈 높이 (스크롤 바: 파일 전체)

		                if (kTarget.nAnchor === 0)	// 줄 모름 (끝부터): 위치 0 = 마지막 줄
		                {
		                    kTarget.nAnchor	= kTarget.nLast;
		                    kTarget.nPos	= kTarget.nLast;
		                }

		                const bEnd = nStart === 0 || (kTarget.nShift > 0 && kTarget.nPos >= kTarget.nTotal);	// 끝 (줄 모름·→ 로 끝 줄까지): 맨 아래

		                if (kPosLine != null && !bEnd)	// 기준 줄 위 VIEW_ABOVE 줄부터 보이게 (보기 새 창: VIEW_ABOVE_WINDOW, 가로: 맨 왼쪽)
		                {
		                    const nAboveMax	= document.body.dataset.mode === "view" ? HTSearch.VIEW_ABOVE_WINDOW : HTSearch.VIEW_ABOVE;	// 위에 보일 줄 수
		                    let kTop		= kPosLine;	// 맨 위에 보일 줄

		                    for (let nAbove = 0; nAbove < nAboveMax && kTop.previousElementSibling != null; ++nAbove)
		                    {
		                        kTop = kTop.previousElementSibling;
		                    }

		                    const nShort = kTop.offsetTop + kViewScroll.clientHeight - (kViewBody.offsetTop + kViewBody.offsetHeight);	// 그 줄을 위 끝에 두기에 모자란 높이 (파일 끝 가까움, 아래 빈 높이 포함)

		                    if (kTop.previousElementSibling != null && nShort > 0)	// 아래 여백 늘림 → 그 줄부터 보이고 앞 줄은 위로 스크롤
		                    {
		                        kTarget.nExtra = nShort;

		                        SetLineSpace(kViewBody, kTarget);
		                    }

		                    kViewScroll.scrollTop = kTop.previousElementSibling == null ? 0 : kTop.offsetTop;	// 그 줄을 창 위 끝에 (앞 줄 안 보임, 첫 줄: 맨 위)
		                }
		                else if (bEnd)	// 끝: 맨 아래
		                {
		                    kViewScroll.scrollTop = kViewScroll.scrollHeight;
		                }
		            }

		            const sRange = kTarget.nLast >= kTarget.nFirst && kTarget.nLast > 0 ? `${kTarget.nFirst}~${kTarget.nLast}` : "-";	// 쌓인 줄 범위

		            kViewTitle.textContent = `${kTarget.sMachine} : ${kTarget.sPath} (${sRange} / 전체 ${kResult.nTotal}줄)`;

		            setTimeout(HTSearch.FillView, 0);	// 읽는 사이 스크롤로 옮긴 곳 (읽는 중 끝난 뒤)
		        }
		        catch (kError)	// 요청 실패
		        {
		            if (kTarget === HTSearch.kView)
		            {
		                if (nDirection === 0)	// 새로: 비움 (이어 읽기: 보던 내용 유지)
		                {
		                    HTSearch.ClearView();
		                }

		                kTarget.bError			= true;	// 스크롤로 다시 읽지 않음 (파일을 다시 열면 처음부터)
		                kViewTitle.textContent	= `${kTarget.sMachine} : ${kTarget.sPath} (요청 실패: ${ErrorText(kError)})`;
		            }
		        }
		        finally
		        {
		            kTarget.bLoading = false;
		        }
		    }

		    static SyncViewHScroll()	// 서치 2번 창 아래 좌우 바: 좌우 넘침 + 창 아래쪽이 화면 밖일 때만 표시, 이동 범위·위치 맞춤
		    {
		        const kRect	= kViewScroll.getBoundingClientRect();
		        const bShow	= kViewScroll.offsetParent != null && kViewScroll.scrollWidth > kViewScroll.clientWidth && kRect.top < window.innerHeight && kRect.bottom > window.innerHeight;

		        kViewHScroll.hidden = !bShow;

		        if (bShow)
		        {
		            kViewHScrollSize.style.width	= (kViewScroll.scrollWidth - kViewScroll.clientWidth + kViewHScroll.clientWidth) + "px";	// 이동 범위 = 서치 2번 창과 같게
		            kViewHScroll.scrollLeft			= kViewScroll.scrollLeft;
		        }
		    }
		}


		class HTDump	// 덤프 탭: 1번 창 덤프 파일 (목록·압축·zip 다운·삭제, this 없음 → 메서드를 이벤트 처리기로 바로 넘김)
		{
		    static arrDump			= [];		// 덤프 파일 { sMachine, sPath, nSize, sTime, bZip } (수정 시각 내림차순, 같으면 머신 목록 순서 → 경로 순)
		    static kDumpInfo		= null;		// 덤프 1번 창 제목 정보 { sCondition, nMachine, nMs, arrNotice }
		    static bDumpLoading		= false;	// 덤프 목록 조회·전체 삭제 중
		    static DUMP_POLL_MS		= 1000;		// 덤프 다운 진행 조회 주기 (ms)
		    static mapBusy			= new Map();	// 진행 중 덤프 (머신·경로 → 진행 팝업 글)

		    static ShowDumpTitle()	// 덤프 1번 창 제목: 조건 · 덤프 수 (머신 수) · 조회 시간 · 머신별 오류
		    {
		        kDumpTitle.textContent = `${HTDump.kDumpInfo.sCondition} 덤프 파일: ${HTDump.arrDump.length.toLocaleString()}개 (머신 ${HTDump.kDumpInfo.nMachine}대) · 조회 시간: ${HTDump.kDumpInfo.nMs} ms${HTDump.kDumpInfo.arrNotice.length > 0 ? " · " + HTDump.kDumpInfo.arrNotice.join(" / ") : ""}`;
		    }

		    static RenderDump()	// 덤프 1번 창 그리기 (No.·Machine·File·Date·Size·Folder·Ac., 클릭: kDumpBody 한 곳에서 처리)
		    {
		        const kFragment = document.createDocumentFragment();

		        HTDump.arrDump.forEach((kDump, nIndex) =>
		        {
		            const nSlash	= kDump.sPath.lastIndexOf("\\");	// 폴더·파일명 구분
		            const kRow		= document.createElement("tr");

		            kRow.dataset.index = nIndex;

		            AddCell(kRow, String(nIndex + 1), "num");
		            AddCell(kRow, kDump.sMachine, "nowrap");
		            AddCell(kRow, kDump.sPath.slice(nSlash + 1), "nowrap");
		            AddCell(kRow, kDump.sTime, "nowrap");	// 파일 날짜: 수정 시각 (KST)
		            AddCell(kRow, `${Math.ceil(kDump.nSize / 1024).toLocaleString()} KB`, "num").title = `${kDump.nSize.toLocaleString()} bytes`;	// 파일 크기: KB (탐색기처럼 올림), 마우스: 바이트
		            AddCell(kRow, kDump.sPath.slice(0, nSlash), "nowrap");	// 폴더: 전체 경로
		            const kAction	= AddCell(kRow, "", "fileact");			// Ac.: 다운(압축)·삭제 아이콘 (맨 오른쪽 고정)
		            const kDown		= document.createElement("button");	// 다운 (zip 없음: 압축, 흐림)
		            const kDelete	= document.createElement("button");	// 삭제

		            kDown.type				= "button";
		            kDown.className			= "qdown";
		            kDown.dataset.action	= "down";
		            kDown.ariaLabel			= "다운";
		            kDelete.type			= "button";
		            kDelete.className		= "qdel";
		            kDelete.dataset.action	= "delete";
		            kDelete.ariaLabel		= "삭제";
		            kDelete.title			= "삭제(dmp, zip)";
		            kAction.append(kDown, kDelete);

		            HTDump.SetDumpButton(kDown, kDump.bZip, kDump.sBusy ?? null);	// zip 있음: 다운, 없음: 압축, 진행 중: 진행 글 (다시 그려도 유지)

		            kFragment.append(kRow);
		        });

		        kDumpBody.replaceChildren(kFragment);

		        kDumpDeleteAll.disabled = HTDump.bDumpLoading || HTDump.arrDump.length === 0;	// 파일 없음·조회·삭제 중: 비활성
		    }

		    static FitDumpHeight()	// 덤프 1번 창 높이: 브라우저 창 아래 끝에 맞춤 (처음·창 크기 바뀔 때)
		    {
		        FitHeight(kDumpScroll, kDumpPanel);
		    }

		    static async LoadDump()	// 덤프 버튼: 선택 머신 덤프 목록 동시 조회 (다른 탭에서 돌아옴: 마지막 목록, 덤프 탭에서 다시 누름: 다시 조회)
		    {
		        if (HTSearch.bSearching)	// 검색 중
		        {
		            return;
		        }

		        if (HTDump.kDumpInfo != null && !kDumpButton.classList.contains("cur"))	// 다른 탭에서 돌아옴: 마지막 데이터 그대로
		        {
		            ShowPanel("dump");

		            return;
		        }

		        if (HTDump.bDumpLoading)	// 조회 중
		        {
		            return;
		        }

		        const sMachine		= GetMachineText();	// 머신 표시 글자
		        const arrMachine	= GetMachines();		// 조회할 머신 (목록 순서)

		        if (arrMachine.length === 0)	// 접속 머신 없음
		        {
		            alert("접속된 머신이 없습니다. M 을 눌러 다시 조회해 주세요.");

		            return;
		        }

		        HTDump.bDumpLoading		= true;
		        kDumpButton.disabled	= true;
		        HTDump.arrDump			= [];
		        HTDump.kDumpInfo		= { sCondition: `[${sMachine} : Dump]`, nMachine: arrMachine.length, nMs: 0, arrNotice: [] };

		        ShowPanel("dump");
		        HTDump.RenderDump();

		        kDumpTitle.textContent	= `${HTDump.kDumpInfo.sCondition} 조회 중: 머신 ${arrMachine.length}대`;
		        kDumpScroll.scrollTop	= 0;

		        const nBegin	= performance.now();				// 조회 시작 시각
		        const arrData	= new Array(arrMachine.length);	// 머신별 결과 (서버는 끝난 순서로 줌)

		        try
		        {
		            const kResponse = await fetch("/api/dump", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ arrMachine: arrMachine }), cache: "no-store" });	// 한 줄 = 머신 1대

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            for (const sLine of (await kResponse.text()).split("\n"))
		            {
		                const kData = sLine === "" ? null : JSON.parse(sLine);

		                if (kData != null && kData.nIndex >= 0 && kData.nIndex < arrMachine.length)
		                {
		                    arrData[kData.nIndex] = kData;
		                }
		            }
		        }
		        catch (kError)	// 요청 실패 (받은 머신까지만, 나머지는 요청 실패)
		        {
		            console.error(kError);
		        }

		        arrMachine.forEach((sItem, nIndex) =>	// 머신 순서대로 추가
		        {
		            const kData		= arrData[nIndex];
		            const kResult	= kData?.kResult;
		            const sError	= kData == null ? "요청 실패" : (kData.sError !== "" ? kData.sError : (kResult == null ? "결과 없음" : kResult.sError));

		            if (sError !== "")
		            {
		                HTDump.kDumpInfo.arrNotice.push(`${sItem}: ${sError}`);

		                return;
		            }

		            for (const kFile of kResult.arrFile)
		            {
		                HTDump.arrDump.push({ sMachine: sItem, sPath: kFile.sPath, nSize: kFile.nSize, sTime: kFile.sTime, bZip: kFile.bZip === true });
		            }

		            if (kResult.bTruncated)	// 머신당 최대 건수 넘음
		            {
		                HTDump.kDumpInfo.arrNotice.push(`${sItem}: 최대 건수 초과로 일부만`);
		            }
		        });

		        HTDump.kDumpInfo.nMs = Math.round(performance.now() - nBegin);

		        HTDump.arrDump.sort((kLeft, kRight) => kLeft.sTime < kRight.sTime ? 1 : (kLeft.sTime > kRight.sTime ? -1 : 0));	// 수정 시각 내림차순 (문자열 비교, 같음: 머신 목록·경로 순)

		        HTDump.RenderDump();
		        HTDump.ShowDumpTitle();

		        HTDump.bDumpLoading		= false;
		        kDumpButton.disabled	= HTSearch.bSearching;
		        kDumpDeleteAll.disabled	= HTDump.arrDump.length === 0;	// 파일 없음: 비활성
		    }

		    static async SaveDumpZip(kDump)	// LMServer zip 을 끝까지 받은 뒤 로컬 저장 (받기 끝 시점 앎, false: zip 없음)
		    {
		        const kResponse = await fetch("/api/dump/zip?" + new URLSearchParams({ sMachine: kDump.sMachine, sPath: kDump.sPath }), { cache: "no-store" });

		        if (kResponse.status === 404)	// zip 없음 (삭제됨)
		        {
		            return false;
		        }

		        if (!kResponse.ok)
		        {
		            throw new Error(`zip 받기 실패 (HTTP ${kResponse.status})`);
		        }

		        const sUrl	= URL.createObjectURL(await kResponse.blob());	// 받은 zip
		        const kLink	= document.createElement("a");

		        kLink.href		= sUrl;
		        kLink.download	= kDump.sPath.slice(kDump.sPath.lastIndexOf("\\") + 1).replace(/\.[^.]*$/, "") + ".zip";	// 서버 zip 이름과 같음 (확장자만 .zip)

		        kLink.click();

		        setTimeout(() => URL.revokeObjectURL(sUrl), 60000);	// 브라우저 저장 시작 후 해제

		        return true;
		    }

		    static SetDumpButton(kButton, bZip, sBusy)	// Ac. 다운 아이콘 (zip 있음: 다운, 없음: 압축 (흐림), sBusy: 진행 중 (끔), null: 진행 없음)
		    {
		        kButton.title		= bZip ? "다운(zip)" : "압축(zip)";
		        kButton.disabled	= sBusy != null;

		        kButton.classList.toggle("nozip", !bZip);
		    }

		    static IsSameDump(kLeft, kRight)	// 같은 덤프 (머신·경로, 목록을 다시 조회해 객체가 바뀌어도 같음)
		    {
		        return kLeft.sMachine === kRight.sMachine && kLeft.sPath === kRight.sPath;
		    }

		    static SetDumpBusy(kDump, sBusy)	// 덤프 다운 진행 표시 (sBusy null: 끝): 덤프 창 가운데 팝업 + 같은 덤프 줄 다운 아이콘 끔 (다시 그려도 유지)
		    {
		        const sBusyKey = `${kDump.sMachine}\t${kDump.sPath}`;	// 진행 중 덤프 구분

		        if (sBusy == null)
		        {
		            HTDump.mapBusy.delete(sBusyKey);
		        }
		        else
		        {
		            HTDump.mapBusy.set(sBusyKey, `${sBusy}\n${kDump.sMachine} : ${kDump.sPath.slice(kDump.sPath.lastIndexOf("\\") + 1)}`);
		        }

		        kDumpProgress.textContent	= [...HTDump.mapBusy.values()].join("\n\n");
		        kDumpProgress.hidden		= HTDump.mapBusy.size === 0;

		        const nIndex	= HTDump.arrDump.findIndex(kItem => HTDump.IsSameDump(kItem, kDump));	// 지금 목록 줄 (없음: -1)
		        const kItem		= HTDump.arrDump[nIndex];

		        kDump.sBusy = sBusy;

		        if (kItem === undefined)	// 목록에서 빠짐
		        {
		            return;
		        }

		        kItem.sBusy	= sBusy;
		        kItem.bZip	= kDump.bZip;

		        const kButton = kDumpBody.rows[nIndex]?.querySelector('button[data-action="down"]');	// 지금 줄의 다운 버튼

		        if (kButton == null)
		        {
		            return;
		        }

		        HTDump.SetDumpButton(kButton, kItem.bZip, sBusy);
		    }

		    static async DownloadDump(kDump)	// 덤프 다운: LMServer zip 을 브라우저로 받기 (zip 없어짐: 압축)
		    {
		        HTDump.SetDumpBusy(kDump, "다운 중");

		        try
		        {
		            if (!await HTDump.SaveDumpZip(kDump))	// zip 없어짐 (삭제됨): 압축부터
		            {
		                kDump.bZip = false;

		                alert(`LMServer 에 zip 이 없습니다. 압축 후 다시 다운해 주세요.\n\n${kDump.sMachine}\n${kDump.sPath}`);
		            }
		        }
		        catch (kError)	// zip 받기 실패
		        {
		            console.error(kError);

		            alert(`다운 실패: ${ErrorText(kError)}\n\n${kDump.sMachine}\n${kDump.sPath}`);
		        }

		        HTDump.SetDumpBusy(kDump, null);
		    }

		    static async CompressDump(kDump)	// 덤프 압축: LMServer 가 클라에서 받아 Dump\머신\덤프 파일명.zip 저장 (DUMP_POLL_MS 마다 진행 조회, 끝: 다운 버튼, 자동 받기 안 함)
		    {
		        const nBegin = performance.now();	// 압축 시작 (진행 초)

		        HTDump.SetDumpBusy(kDump, "압축 중 · 0초");

		        try
		        {
		            while (true)	// 시작·진행 조회 (요청마다 바로 응답 → 큰 덤프도 ToolManager·IIS 요청 시간 제한에 안 걸림)
		            {
		                const kResponse = await fetch("/api/dump/download", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ sMachine: kDump.sMachine, sPath: kDump.sPath }), cache: "no-store" });

		                if (!kResponse.ok)	// 경유 실패 등 (JSON 아님)
		                {
		                    throw new Error(`HTTP ${kResponse.status}`);
		                }

		                const kData = await kResponse.json();	// {bDone, nOffset, nSize, sError}

		                if (kData.sError !== "")
		                {
		                    throw new Error(kData.sError);
		                }

		                if (kData.bDone)
		                {
		                    break;
		                }

		                const nSec = Math.floor((performance.now() - nBegin) / 1000);	// 압축 시간 (초)

		                HTDump.SetDumpBusy(kDump, kData.nSize > 0 ? `압축 중 · ${nSec}초 · ${Math.floor(kData.nOffset * 100 / kData.nSize)}%` : `압축 중 · ${nSec}초`);	// zip 없어 LMServer 가 받아 압축하는 중

		                await new Promise(funcResolve => setTimeout(funcResolve, HTDump.DUMP_POLL_MS));
		            }

		            kDump.bZip = true;	// LMServer 에 zip 있음 → 다운 버튼
		        }
		        catch (kError)	// 압축 실패·응답 끊김 (서버는 저장 계속 → 다시 누르면 진행 이어서 표시)
		        {
		            console.error(kError);

		            alert(`압축 실패: ${ErrorText(kError)}\n\n${kDump.sMachine}\n${kDump.sPath}`);
		        }

		        HTDump.SetDumpBusy(kDump, null);
		    }

		    static DeleteDump(kDump, kButton)	// 덤프 삭제: 확인 팝업 → RunDeleteDump
		    {
		        AskDelete("덤프 삭제", kDump.sPath.slice(kDump.sPath.lastIndexOf("\\") + 1), `${kDump.sMachine} : ${kDump.sPath} · 되돌릴 수 없음 · LMServer 에 다운한 zip 도 삭제`, () => HTDump.RunDeleteDump(kDump, kButton));
		    }

		    static async RunDeleteDump(kDump, kButton)	// 덤프 삭제 실행 → 덤프 1번 창에서 제거 (실패: 알림)
		    {
		        kButton.disabled = true;

		        try
		        {
		            const sZipError = await HTDump.RequestDelete(kDump);	// LMServer zip 삭제 오류

		            HTDump.arrDump = HTDump.arrDump.filter(kItem => !HTDump.IsSameDump(kItem, kDump));	// 그새 다시 조회해도 같은 덤프 뺌

		            if (!HTDump.bDumpLoading)	// 다시 조회 중: 조회가 그림 (조회 중 제목 유지)
		            {
		                HTDump.RenderDump();
		                HTDump.ShowDumpTitle();
		            }

		            if (sZipError !== "")	// 덤프는 삭제, LMServer zip 삭제 실패
		            {
		                alert(`덤프는 삭제했지만 LMServer zip 삭제 실패: ${sZipError}\n\n${kDump.sMachine}\n${kDump.sPath}`);
		            }
		        }
		        catch (kError)	// 삭제 실패
		        {
		            console.error(kError);

		            kButton.disabled = false;

		            alert(`삭제 실패: ${ErrorText(kError)}\n\n${kDump.sMachine}\n${kDump.sPath}`);
		        }
		    }

		    static async RequestDelete(kDump)	// 클라에 덤프 삭제 요청 (성공: LMServer zip 도 삭제) → zip 삭제 오류 ("": 없음, 덤프 삭제 실패: throw)
		    {
		        const kData		= await FetchJson("/api/dump/delete", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ sMachine: kDump.sMachine, sPath: kDump.sPath }), cache: "no-store" });
		        const sError	= kData.sError !== "" ? kData.sError : (kData.kResult == null ? "결과 없음" : kData.kResult.sError);

		        if (sError !== "")
		        {
		            throw new Error(sError);
		        }

		        return kData.sZipError;
		    }

		    static DeleteAllDump()	// 전체 삭제 버튼: 확인 팝업 → RunDeleteAll
		    {
		        if (HTDump.arrDump.length === 0 || HTDump.bDumpLoading)	// 목록 없음·조회·삭제 중
		        {
		            return;
		        }

		        AskDelete("덤프 전체 삭제", `덤프 파일 ${HTDump.arrDump.length.toLocaleString()}개`, "되돌릴 수 없음 · LMServer 에 다운한 zip 도 삭제", HTDump.RunDeleteAll);
		    }

		    static async RunDeleteAll()	// 전체 삭제: 차례로 삭제 (클라 동시 처리 제한), 성공한 줄만 제거, 실패: 모아서 알림
		    {
		        const arrTarget	= [...HTDump.arrDump];	// 삭제할 덤프 (시작 때 목록)
		        const arrFail	= [];					// 실패 문구

		        HTDump.bDumpLoading		= true;
		        kDumpButton.disabled	= true;
		        kDumpDeleteAll.disabled	= true;
		        kDumpBody.inert			= true;	// 삭제 중 줄 버튼 막음

		        for (const [nIndex, kDump] of arrTarget.entries())
		        {
		            kDumpTitle.textContent = `${HTDump.kDumpInfo.sCondition} 삭제 중: ${nIndex + 1} / ${arrTarget.length}`;

		            try
		            {
		                const sZipError = await HTDump.RequestDelete(kDump);	// LMServer zip 삭제 오류

		                HTDump.arrDump = HTDump.arrDump.filter(kItem => !HTDump.IsSameDump(kItem, kDump));

		                HTDump.RenderDump();

		                if (sZipError !== "")	// 덤프는 삭제, LMServer zip 삭제 실패
		                {
		                    arrFail.push(`${kDump.sMachine} : ${kDump.sPath} (LMServer zip 삭제 실패: ${sZipError})`);
		                }
		            }
		            catch (kError)	// 삭제 실패: 다음 덤프 계속
		            {
		                console.error(kError);

		                arrFail.push(`${kDump.sMachine} : ${kDump.sPath} (${ErrorText(kError)})`);
		            }
		        }

		        HTDump.bDumpLoading		= false;
		        kDumpButton.disabled	= HTSearch.bSearching;
		        kDumpDeleteAll.disabled	= HTDump.arrDump.length === 0;	// 모두 삭제: 비활성
		        kDumpBody.inert			= false;

		        HTDump.ShowDumpTitle();

		        if (arrFail.length > 0)
		        {
		            alert(`삭제 실패: ${arrFail.length}개\n\n${arrFail.join("\n")}`);
		        }
		    }

		    static OnDumpClick(kEvent)	// 덤프 1번 창 버튼 (표 하나에서 처리): 다운·압축·삭제
		    {
		        const kButton = kEvent.target.closest("button[data-action]");	// 누른 버튼

		        if (kButton == null || kButton.disabled)
		        {
		            return;
		        }

		        const kDump = HTDump.arrDump[Number(kButton.closest("tr").dataset.index)];

		        if (kButton.dataset.action === "delete")
		        {
		            HTDump.DeleteDump(kDump, kButton);
		        }
		        else if (kDump.bZip)	// zip 있음: 확인 팝업 → 브라우저로 받기
		        {
		            const sName = kDump.sPath.slice(kDump.sPath.lastIndexOf("\\") + 1);	// 덤프 파일명

		            AskDownload(sName.replace(/\.[^.]*$/, "") + ".zip", `LMServer Dump 폴더 zip · 원본 ${kDump.sMachine} : ${kDump.sPath} (${kDump.sTime} · ${Math.ceil(kDump.nSize / 1024).toLocaleString()} KB)`, () => HTDump.DownloadDump(kDump));
		        }
		        else	// zip 없음: LMServer 에 압축만
		        {
		            HTDump.CompressDump(kDump);
		        }
		    }
		}


		class HTFile	// 파일 탭: 1번 창 클라 감시 폴더 트리 (펼치기·파일 보기·다운, this 없음 → 메서드를 이벤트 처리기로 바로 넘김)
		{
		    static FILE_VIEW_COUNT	= 300;		// 파일 1번 창 파일 보기 한 번에 읽는 줄 수
		    static FILE_VIEW_EXT	= /\.(log|txt|json|ini|bat|xml|config|md|lua)$/i;	// 볼 수 있는 파일 (LMClient IsViewFile 과 같음, 트리: 이 형식만)
		    static FILE_DATE_NAME	= /(?<!\d)(19|20)\d{2}[-_.]?(0[1-9]|1[0-2])[-_.]?(0[1-9]|[12]\d|3[01])/;	// 년월일 들어 있는 이름 (예: XLobby_20261022_09.log, 앞에 숫자 이어지면 아님) → 트리 내림차순
		    static FILE_SPLIT_MIN	= 120;		// 파일 1번 창 좌우 최소 폭 (px, 세로선 드래그)
		    static FILE_VIEW_MAX	= 20000;	// 파일 1번 창 파일 보기 최대 줄 수 (넘음: 반대쪽 지움, 빈 높이로 위치 유지)
		    static kFileView		= null;		// 보는 파일 { sMachine, sPath, nFirst: 읽은 첫 줄 (0: 아직, 1: 처음까지 읽음), nLast: 읽은 마지막 줄, nTotal: 전체 줄, bLoading }
		    static bFileLoaded		= false;	// 트리 한 번 만듦 (다른 탭에서 돌아오면 그대로, 파일 탭에서 다시 누르면 새로)
		    static FILE_VIEW_HINT	= kFileViewTitle.textContent;	// 파일 1번 창 오른쪽 처음 안내 (보는 파일 없음)

		    static MakeFileNode(sKind, sMachine, sPath, sName, sInfo)	// 트리 한 줄 <li> (sKind: machine·dir·file, sPath: 전체 경로 (머신: ""), sInfo: 이름 뒤 회색 글자)
		    {
		        const kItem		= document.createElement("li");
		        const kNode		= document.createElement("div");								// 누르는 줄
		        const kToggle	= document.createElement("span");	// [+]·[-] (파일: 비움 → 안 보이고 자리만, 이름 줄 맞춤)
		        const kLabel	= document.createElement("span");	// 아이콘 (머신·폴더만, CSS Octicons)·이름·정보
		        const kName		= document.createElement("span");

		        kNode.className			= sKind === "file" && HTFile.FILE_VIEW_EXT.test(sName) ? "node view" : "node";	// view: 누르면 오른쪽에 내용
		        kNode.dataset.kind		= sKind;
		        kNode.dataset.machine	= sMachine;
		        kNode.dataset.path		= sPath;
		        kLabel.className		= "label";
		        kName.textContent		= sName;
		        kToggle.className		= "tog";
		        kToggle.textContent		= sKind === "file" ? "" : "+";

		        if (sKind !== "file")	// 머신·폴더 아이콘 (파일: 없음)
		        {
		            kLabel.append(Object.assign(document.createElement("span"), { className: "ticon", textContent: "📁" }));
		        }

		        kLabel.append(kName);

		        if (sInfo !== "")
		        {
		            const kInfo = document.createElement("span");

		            kInfo.className		= "info";
		            kInfo.textContent	= sInfo;

		            kLabel.append(kInfo);
		        }

		        kNode.append(kToggle, kLabel);
		        kItem.append(kNode);

		        return kItem;
		    }

		    static MakeFileMessage(sText)	// 파일 1번 창 트리 안내 한 줄 <li> (오류·빈 폴더·일부만)
		    {
		        const kItem = document.createElement("li");

		        kItem.className		= "msg";
		        kItem.textContent	= sText;

		        return kItem;
		    }

		    static SortTreeNames(arrItem, funcName)	// 트리 순서: 이름 오름차순 (클라 순서), 년월일 든 것만 그 자리들 안에서 내림차순
		    {
		        const arrDate	= arrItem.filter(kItem => HTFile.FILE_DATE_NAME.test(funcName(kItem))).reverse();	// 년월일 들어 있는 것 (내림차순)
		        let nDate		= 0;																				// 다음에 채울 순서

		        return arrItem.map(kItem => HTFile.FILE_DATE_NAME.test(funcName(kItem)) ? arrDate[nDate++] : kItem);
		    }

		    static async ToggleFileNode(kNode)	// 트리 펼치기·접기 (펼칠 때마다 클라에 폴더 보기 요청 → 갱신, 머신: aLogFolder 목록, 접음: 하위 지움)
		    {
		        const kItem		= kNode.parentElement;
		        const kToggle	= kNode.querySelector(".tog");
		        let kChild		= kItem.querySelector(":scope > ul");	// 하위 목록 (null: 접힘)

		        if (kNode.dataset.kind === "file" || kNode.dataset.loading === "1")	// 파일·읽는 중
		        {
		            return;
		        }

		        if (kChild != null)	// 펼침: 접기 (하위 목록 지움 → 다시 펼치면 새로 읽음)
		        {
		            kChild.remove();

		            kToggle.textContent = "+";

		            return;
		        }

		        const sMachine	= kNode.dataset.machine;
		        const sPath		= kNode.dataset.path;

		        kNode.dataset.loading	= "1";
		        kToggle.textContent		= "…";
		        kChild					= document.createElement("ul");

		        try
		        {
		            const kResponse = await fetch("/api/files?" + new URLSearchParams({ sMachine: sMachine, sPath: sPath }), { cache: "no-store" });

		            if (!kResponse.ok)	// 경유 실패 등 (JSON 아님)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData		= await kResponse.json();
		            const kResult	= kData.kResult;
		            const sError	= kData.sError !== "" ? kData.sError : (kResult == null ? "결과 없음" : kResult.sError);

		            if (sError !== "")
		            {
		                throw new Error(sError);
		            }

		            for (const sDir of HTFile.SortTreeNames(kResult.arrDir, sItem => sItem))	// 폴더 먼저 (날짜 폴더: 최신부터)
		            {
		                kChild.append(HTFile.MakeFileNode("dir", sMachine, sPath === "" ? sDir : `${sPath}\\${sDir}`, sDir, ""));
		            }

		            const arrFile = kResult.arrFile.filter(kFile => HTFile.FILE_VIEW_EXT.test(kFile.sName));	// 볼 수 있는 형식 파일만

		            for (const kFile of HTFile.SortTreeNames(arrFile, kItem => kItem.sName))	// 파일 (시간별 로그: 최신부터, 수정 시각·크기)
		            {
		                kChild.append(HTFile.MakeFileNode("file", sMachine, sPath === "" ? kFile.sName : `${sPath}\\${kFile.sName}`, kFile.sName, `${kFile.sTime} · ${Math.ceil(kFile.nSize / 1024).toLocaleString()} KB`));
		            }

		            if (kResult.bTruncated)
		            {
		                kChild.append(HTFile.MakeFileMessage("… 일부만 표시 (폴더당 최대 10,000개)"));
		            }
		            else if (kResult.arrDir.length + kResult.arrFile.length === 0)
		            {
		                kChild.append(HTFile.MakeFileMessage("(비어 있음)"));
		            }
		            else if (kResult.arrDir.length + arrFile.length === 0)	// 파일은 있지만 모두 숨긴 형식
		            {
		                kChild.append(HTFile.MakeFileMessage("(해당 없음)"));
		            }
		        }
		        catch (kError)	// 요청 실패·허용 안 된 경로·폴더 읽기 실패 (접었다 펼치면 다시 요청)
		        {
		            console.error(kError);

		            kChild.replaceChildren(HTFile.MakeFileMessage(`폴더 보기 실패: ${ErrorText(kError)}`));
		        }

		        kItem.append(kChild);

		        kToggle.textContent = "−";

		        delete kNode.dataset.loading;
		    }

		    static LoadFileTree()	// 파일 버튼: 선택 머신 감시 폴더 트리 (다른 탭에서 돌아옴: 그대로, 파일 탭에서 다시 누름: 처음부터)
		    {
		        if (HTSearch.bSearching)	// 검색 중
		        {
		            return;
		        }

		        if (HTFile.bFileLoaded && !kFileButton.classList.contains("cur"))	// 다른 탭에서 돌아옴: 마지막 데이터 그대로
		        {
		            ShowPanel("file");

		            return;
		        }

		        const sMachine		= GetMachineText();	// 머신 표시 글자
		        const arrMachine	= GetMachines();		// 보여줄 머신 (목록 순서)

		        if (arrMachine.length === 0)	// 접속 머신 없음
		        {
		            alert("접속된 머신이 없습니다. M 을 눌러 다시 조회해 주세요.");

		            return;
		        }

		        const arrNode		= arrMachine.map(sItem => HTFile.MakeFileNode("machine", sItem, "", sItem, ""));			// 맨 위: 머신 목록

		        kFileTree.replaceChildren(...arrNode);
		        HTFile.ResetFileView();	// 오른쪽 파일 보기도 처음으로 (다른 탭에 다녀와도 초기화)

		        kFileScroll.scrollTop	= 0;
		        kFileScroll.scrollLeft	= 0;
		        kFileTitle.textContent	= `[${sMachine} : File] 머신 ${arrMachine.length}대`;
		        HTFile.bFileLoaded		= true;

		        ShowPanel("file");
		    }

		    static OnFileTreeClick(kEvent)	// 트리 클릭 (트리 하나에서 처리): 머신·폴더 펼치기·접기, 보기 파일: 오른쪽에 내용 (Shift·Ctrl·Alt+클릭: OnShiftFileClick·OnOpenClick 이 먼저)
		    {
		        const kNode = kEvent.target.closest("div.node");	// 누른 줄

		        if (kNode == null)
		        {
		            return;
		        }

		        if (kNode.dataset.kind !== "file")
		        {
		            HTFile.ToggleFileNode(kNode);

		            return;
		        }

		        if (kNode.classList.contains("view"))	// 보기 파일: 선택 표시 후 오른쪽에 마지막 FILE_VIEW_COUNT 줄
		        {
		            kFileTree.querySelector("div.node.sel")?.classList.remove("sel");
		            kNode.classList.add("sel");

		            HTFile.OpenFileView(kNode.dataset.machine, kNode.dataset.path, kNode);
		        }
		    }

		    static ResetFileView()	// 오른쪽 파일 보기 비우기 (읽는 중 응답 버림, 처음 안내)
		    {
		        HTFile.kFileView = null;

		        HTFile.ClearFileView();

		        kFileViewTitle.textContent = HTFile.FILE_VIEW_HINT;
		    }

		    static ClearFileView()	// 파일 1번 창 오른쪽 파일 보기 내용·위·아래 빈 높이·스크롤 비우기
		    {
		        kFileViewBody.replaceChildren();

		        kFileViewBody.style.paddingTop		= "";
		        kFileViewBody.style.paddingBottom	= "";
		        kFileViewScroll.scrollTop			= 0;
		        kFileViewScroll.scrollLeft			= 0;
		    }

		    static OpenFileView(sMachine, sPath, kNode)	// 오른쪽 파일 보기 열기: 마지막 FILE_VIEW_COUNT 줄 → 맨 아래 (kNode: 트리 줄 → 수정 시각·크기 갱신)
		    {
		        HTFile.kFileView = { sMachine: sMachine, sPath: sPath, kNode: kNode, nFirst: 0, nLast: 0, nTotal: 0, bLoading: false, bError: false };

		        HTFile.ClearFileView();
		        HTFile.LoadFileView(0, HTFile.FILE_VIEW_COUNT, 0);
		    }

		    static FillFileView()	// 파일 1번 창 오른쪽 스크롤: 보이는 곳 줄 읽기 (FillLines, FILE_VIEW_COUNT 줄씩)
		    {
		        FillLines(kFileViewScroll, kFileViewBody, HTFile.kFileView, HTFile.FILE_VIEW_COUNT, HTFile.LoadFileView);
		    }

		    static async LoadFileView(nStart, nCount, nDirection)	// 오른쪽 nStart 줄부터 nCount 줄 읽기 (nStart 0: 마지막, nDirection 0: 새로·-1: 위에 붙임·1: 아래에 붙임·2: 그 위치로 바꿈, 그 사이 다른 파일: 버림)
		    {
		        const kTarget = HTFile.kFileView;	// 요청한 파일

		        if (kTarget == null || kTarget.bLoading)
		        {
		            return;
		        }

		        kTarget.bLoading = true;

		        if (nDirection === 0)
		        {
		            kFileViewTitle.textContent = `${kTarget.sMachine} : ${kTarget.sPath} (불러오는 중)`;
		        }

		        try
		        {
		            const kParam	= new URLSearchParams({ sMachine: kTarget.sMachine, sPath: kTarget.sPath, nStart: nStart, nCount: nCount });
		            const kResponse	= await fetch("/api/view?" + kParam, { cache: "no-store" });

		            if (!kResponse.ok)	// 경유 실패 등 (JSON 아님)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData = await kResponse.json();

		            if (kTarget !== HTFile.kFileView)	// 그 사이 다른 파일을 엶
		            {
		                return;
		            }

		            const kResult	= kData.kResult;
		            const sError	= kData.sError !== "" ? kData.sError : (kResult == null ? "결과 없음" : kResult.sError);

		            if (sError !== "")
		            {
		                throw new Error(sError);
		            }

		            const kFragment		= document.createDocumentFragment();	// 붙일 줄
		            const nCountRead	= kResult.arrText.length;				// 읽은 줄 수

		            kResult.arrText.forEach((sText, nIndex) =>
		            {
		                const kLine	= document.createElement("span");
		                const kNo	= document.createElement("span");

		                kLine.className	= "line";
		                kNo.className	= "lno";
		                kNo.textContent	= String(kResult.nStart + nIndex).padStart(7, " ") + " : ";

		                kLine.append(kNo, sText);
		                kFragment.append(kLine);
		            });

		            kTarget.nTotal = kResult.nTotal;

		            if (nDirection < 0)	// 위에 붙임 (보던 위치 그대로)
		            {
		                kFileViewBody.prepend(kFragment);

		                kTarget.nFirst = nCountRead > 0 ? kResult.nStart : 1;

		                TrimLines(kFileViewBody, kTarget, HTFile.FILE_VIEW_MAX, false);	// 최대 넘음: 아래쪽 지움
		            }
		            else if (nDirection === 1)	// 아래에 붙임
		            {
		                kFileViewBody.append(kFragment);

		                kTarget.nLast += nCountRead;

		                TrimLines(kFileViewBody, kTarget, HTFile.FILE_VIEW_MAX, true);	// 최대 넘음: 위쪽 지움
		            }
		            else	// 새로·그 위치로 바꿈
		            {
		                kFileViewBody.replaceChildren(kFragment);

		                kTarget.nFirst	= nCountRead > 0 ? kResult.nStart : 1;
		                kTarget.nLast	= nCountRead > 0 ? kResult.nStart + nCountRead - 1 : 0;
		            }

		            SetLineSpace(kFileViewBody, kTarget);	// 위·아래 빈 높이 (스크롤 바: 파일 전체)

		            if (nDirection === 0)	// 새로: 맨 아래로, 트리의 수정 시각·크기 갱신
		            {
		                const kInfo = kTarget.kNode?.querySelector(".info");	// 트리 줄 정보 (트리를 다시 만들었으면 화면에 없음 → 무시)

		                kFileViewScroll.scrollTop = kFileViewScroll.scrollHeight;

		                if (kInfo != null && typeof kResult.sTime === "string" && kResult.sTime !== "")	// 예전 LMClient: sTime 없음 → 그대로
		                {
		                    kInfo.textContent = `${kResult.sTime} · ${Math.ceil(kResult.nSize / 1024).toLocaleString()} KB`;
		                }
		            }

		            kFileViewTitle.textContent = `${kTarget.sMachine} : ${kTarget.sPath} (${kTarget.nLast > 0 ? `${kTarget.nFirst}~${kTarget.nLast}` : "-"} / 전체 ${kTarget.nTotal.toLocaleString()}줄)`;

		            setTimeout(HTFile.FillFileView, 0);	// 읽는 사이 스크롤로 옮긴 곳 (읽는 중 끝난 뒤)
		        }
		        catch (kError)	// 요청 실패·허용 안 된 경로·읽기 실패: 더 읽지 않음 (다시 누름: 처음부터)
		        {
		            console.error(kError);

		            kTarget.bError = true;

		            if (kTarget === HTFile.kFileView)
		            {
		                kFileViewTitle.textContent = `${kTarget.sMachine} : ${kTarget.sPath} (보기 실패: ${ErrorText(kError)})`;
		            }
		        }
		        finally
		        {
		            kTarget.bLoading = false;
		        }
		    }

		}


		class HTQuery	// 쿼리 탭 (this 없음 → 메서드를 이벤트 처리기로 바로 넘김)
		{
		    static PAGE_BLOCK	= 10;		// 페이지 번호 한 번에 보이는 수 (‹ › 로 앞뒤 묶음)
		    static CHART_MIN	= 60;		// 쿼리 1번 창 막대 최소 높이 (px, 1·2번 창 사이 바 드래그)
		    static sQueryKey	= "";		// 지금 결과의 쿼리
		    static nStart		= 0;		// 지금 결과의 기간 시작 (yyyyMMddHHmm, 포함)
		    static nEnd			= 0;		// 지금 결과의 기간 끝 (yyyyMMddHHmm, 미포함)
		    static bDay			= false;	// 지금 결과의 기간이 24시간 넘음 (막대 날짜별)
		    static bMinute		= false;	// 지금 결과의 기간이 1시간 미만 (막대 분별)
		    static bMinuteBar	= false;	// 지금 막대가 분별 (마우스 올림 표시용)
		    static bSecondBar	= false;	// 지금 막대가 초별 (1분 기간, 더 안 눌림)
		    static kFrom		= null;		// 지금 결과의 기간 시작 (Date)
		    static kTo			= null;		// 지금 결과의 기간 끝 (Date, 미포함)
		    static arrHistory	= [];		// 들어온 단계 { sKey, kFrom, kTo, nRange } (0: 최초 쿼리, 막대·단어·필드 값 클릭마다 추가)
		    static arrLine		= [];		// 지금 페이지 결과 줄 { sTime, sMachine, sPath, sText } (줄 클릭 펼침용)
		    static nHistory		= -1;		// 지금 단계 (← → 로 오감)
		    static kOtherRange	= null;		// 안 보이는 쪽 명령 창 기간 (쿼리 모드: 서치 기간, 그 외: 쿼리 기간, null: 쿼리 처음 → D 최근 기간)
		    static arrBar		= [];		// 막대 { nFirst, nLast, nCount } (마우스 올림 표시용)
		    static bDayBar		= false;	// 날짜별 막대 (기간 24시간 넘음, false: 시간별)
		    static nPage		= 0;		// 지금 페이지 (0부터)
		    static nPageSize	= 1000;		// 한 페이지 건수 (서버가 알려 줌, 페이지 번호 기준)
		    static CHUNK		= 100;		// 한 번에 읽어 그리는 줄 수 (스크롤: 이어서)
		    static nRowEnd		= -1;		// 이 페이지 끝으로 확인한 줄 수 (요청보다 덜 옴: 읽은 데까지, -1: 모름 → 빈 높이·이어 읽기 여기까지)
		    static nChunkSeq	= 0;		// 페이지 읽기 번호 (새 쿼리·페이지: 늘어남 → 늦게 온 이어 읽기 버림)
		    static bChunkLoading	= false;	// 이어 읽는 중
		    static nTotal		= 0;		// 전체 건수
		    static bQuerying	= false;	// 쿼리 중 (중복 요청 막음)
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
		    static sSavedKey	= "";		// 쿼리 설정에서 선택한 저장 쿼리 제목 (설정 목록 강조, "": 고르지 않음)
		    static sEditKey		= "";		// 쿼리 추가·수정 창에서 수정할 저장 쿼리 제목 ("": 추가)
		    static bAlarm		= false;	// 쿼리 설정 알람 탭
		    static arrAlarm		= [];		// 알람 { nID, sKey, sQuery, nMinute, nCount, nDelay, aMail, sHook, aSMS, bEnable }
		    static nEditID		= 0;		// 알람 추가·수정 창에서 수정할 알람 (0: 추가)
		    static nDragIndex	= -1;		// 쿼리 설정 드래그 중인 줄 위치 (-1: 없음)
		    static SAVED_MINUTE	= 10;		// 쿼리 추가·수정 기본 간격 (분, 서버와 같음)
		    static SAVED_MINUTE_MAX	= 129600;	// 쿼리 간격 최대 (분, 서버와 같음)
		    static ALARM_MINUTE	= 1;		// 알람 추가 기본 간격 (분)
		    static ALARM_COUNT	= 10;		// 알람 추가 기본 건수
		    static ALARM_DELAY	= 1;		// 알람 추가 기본 억제 (분)
		    static ALARM_MINUTE_MAX	= 60;	// 알람 간격 최대 (분, 서버와 같음)
		    static ALARM_DELAY_MAX	= 1440;	// 알람 억제 최대 (분, 서버와 같음)
		    static ALARM_LIST_MAX	= 20;	// 알람 메일·SMS 최대 개수 (서버와 같음)
		    static MAIL_REGEX		= /^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$/;	// 메일 형식 (서버와 같음)
		    static SMS_REGEX		= /^\+?[0-9-]{8,20}$/;					// SMS 번호 형식 (서버와 같음)
		    static arrAlarmMail	= [];		// 알람 추가·수정 창 메일 목록
		    static arrAlarmSMS	= [];		// 알람 추가·수정 창 SMS 목록
		    static kStatAbort	= null;		// 통계 요청 중단 (중단 버튼·다음 통계, null: 통계 중 아님)
		    static FIELD_PAGE	= 100;		// 필드 새 창 한 페이지 값 수
		    static FIELD_SPLIT_MIN	= 120;	// 쿼리 2번 창 왼쪽 필드·오른쪽 로그 최소 폭 (px, 세로선 드래그)
		    static sFieldName	= "";		// 필드 새 창 필드
		    static arrFieldValue	= [];	// 필드 새 창 값 전체 { sValue, nCount } (건수 많은 순)
		    static nFieldTotal	= 0;		// 필드 새 창 맞는 줄 수 (값 비율)
		    static nFieldPage	= 0;		// 필드 새 창 지금 페이지 (0부터)

		    static LoadQuery()	// 쿼리 탭 버튼: 쿼리 1·2번 창 (처음: D 1일, 다른 탭에서 돌아옴: 마지막 결과)
		    {
		        if (HTSearch.bSearching)	// 검색 중
		        {
		            return;
		        }

		        ShowPanel("query");

		        if (!HTQuery.bEntered)	// 처음 들어옴: D 1일
		        {
		            HTQuery.bEntered	= true;
		            kQueryRange.value	= "1440";

		            HTQuery.SetRecent();	// S·E: 지금 기준 1일
		            HTQuery.LoadSaved();	// 쿼리 설정 저장 쿼리 목록
		            HTQuery.LoadField();	// 왼쪽 필드 이름 (통계 실패 대비)
		        }
		    }

		    static RunQuery()	// 쿼리 버튼·Enter: 입력한 쿼리 (없음: *)·명령 창 기간 (S 포함 ~ E 미포함, D: 지금 기준) 첫 페이지
		    {
		        if (HTQuery.bQuerying)	// 쿼리 중 (쿼리칸 Enter 등): 무시
		        {
		            return;
		        }

		        const sKey = kQueryKey.value.trim() || "*";	// 쿼리 (없음: * 전체 로그, 쿼리칸은 비운 그대로)

		        if (kQueryRange.selectedIndex >= 0)	// D 최근 기간: 지금까지로 다시
		        {
		            HTQuery.SetRecent();
		        }

		        const kFrom	= HTQuery.GetPicker(kStart, kStartHour, kStartMinute);	// 시작 (포함)
		        const kTo	= HTQuery.GetPicker(kEnd, kEndHour, kEndMinute);		// 끝 (미포함)

		        HTQuery.Query(sKey, kFrom, kTo, true, true);	// 최초 단계부터 새로
		    }

		    static GetState()	// 지금 결과 상태 (새 쿼리 중단·실패: 이 상태로 되돌림)
		    {
		        return { sQueryKey: HTQuery.sQueryKey, nStart: HTQuery.nStart, nEnd: HTQuery.nEnd, kFrom: HTQuery.kFrom, kTo: HTQuery.kTo, bDay: HTQuery.bDay, bMinute: HTQuery.bMinute, arrHistory: HTQuery.arrHistory.slice(), nHistory: HTQuery.nHistory };
		    }

		    static Query(sKey, kFrom, kTo, bHistory, bReset = false, kPrev = HTQuery.GetState())	// 쿼리 kFrom (포함) ~ kTo (미포함) 첫 페이지 (시작 = 종료: 그 1분, bHistory: 단계 남김, bReset: 단계 처음부터, kPrev: 중단·실패 때 되돌릴 상태)
		    {
		        const kPickTo = kTo;	// 고른 끝 (들어온 단계: S·E 그대로 다시 보이게)

		        if (HTQuery.bQuerying)
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
		            HTQuery.arrHistory	= [];
		            HTQuery.nHistory	= -1;
		        }

		        HTQuery.SetResultRange(sKey, kFrom, kTo);

		        if (bHistory)	// 들어온 단계 남김 (지금 단계 뒤는 버림)
		        {
		            HTQuery.arrHistory.splice(HTQuery.nHistory + 1);
		            HTQuery.arrHistory.push({ sKey, kFrom, kTo: kPickTo, nRange: kQueryRange.selectedIndex });

		            HTQuery.nHistory = HTQuery.arrHistory.length - 1;
		        }

		        HTQuery.ShowHistory();

		        const kStep = HTQuery.arrHistory[HTQuery.nHistory];	// 이 결과 단계 (결과 보관)

		        HTQuery.kQueryTask = HTQuery.LoadPage(0, kPrev, kStep);
		        HTQuery.LoadStat(kStep);	// 왼쪽 필드별 통계 (따로 가져옴 → 결과 먼저)
		    }

		    static SetResultRange(sKey, kFrom, kTo)	// 지금 결과 쿼리·기간 (kTo: 미포함)
		    {
		        HTQuery.sQueryKey	= sKey;
		        HTQuery.nStart		= HTQuery.ToMinute(kFrom);
		        HTQuery.nEnd		= HTQuery.ToMinute(kTo);
		        HTQuery.kFrom		= kFrom;
		        HTQuery.kTo			= kTo;
		        HTQuery.bDay		= kTo - kFrom > 24 * 3600 * 1000;	// 24시간 넘음: 날짜별
		        HTQuery.bMinute		= kTo - kFrom <= 3600 * 1000;		// 1시간 이하: 분별 (시간별 막대 클릭 = 1시간)
		    }

		    static SaveStepResult(kStep, kResult)	// 단계 결과 보관 (페이지·필드 통계 따로 옴: 합침, 단계 없음: 안 함)
		    {
		        if (kStep != null)
		        {
		            HTQuery.mapStepResult.set(kStep, { ...HTQuery.mapStepResult.get(kStep), ...kResult });
		        }
		    }

		    static ShowStep(kStep, kResult)	// 단계 보관 결과 그대로 그리기 (다시 쿼리 안 함, 필드 통계 없음: 통계만 다시)
		    {
		        const kTo = kStep.kFrom.getTime() === kStep.kTo.getTime() ? new Date(kStep.kFrom.getTime() + 60 * 1000) : kStep.kTo;	// 시작 = 종료: 그 1분 (Query 와 같음)

		        HTQuery.SetResultRange(kStep.sKey, kStep.kFrom, kTo);
		        HTQuery.nChunkSeq++;	// 앞 페이지 이어 읽기 응답 버림
		        HTQuery.ShowHistory();
		        HTQuery.ShowPage({ ...kResult.kPage, arrLine: kResult.kPage.arrLine.slice() });

		        if (kResult.arrField == null)
		        {
		            HTQuery.LoadStat(kStep);

		            return;
		        }

		        HTQuery.kStatAbort?.abort();	// 앞 단계 통계 끊음
		        HTQuery.nStatSeq++;				// 늦게 온 앞 통계 버림
		        HTQuery.bStatLoading = false;

		        HTQuery.ShowField(kResult.arrField);
		    }

		    static ShowHistory()	// ← → 켬·끔, 가운데 지금 단계 (보는 1번 창 단계, 0: 최초, 검색 중: 끔)
		    {
		        const kOwner = kResultPanel.classList.contains("query") ? HTQuery : HTSearch;	// 보는 1번 창 단계

		        kQueryPrev.disabled		= kOwner.nHistory <= 0 || HTSearch.bSearching;
		        kQueryNext.disabled		= kOwner.nHistory >= kOwner.arrHistory.length - 1 || HTSearch.bSearching;
		        kQueryDepth.textContent	= String(Math.max(0, kOwner.nHistory));
		    }

		    static EditHistory()	// 가운데 단계 누름: 숫자 입력 → Enter: 그 단계로 (넘음: 끝 단계), Esc·밖 누름: 취소 (단계 1개뿐·검색 중: 안 함)
		    {
		        const kOwner = kResultPanel.classList.contains("query") ? HTQuery : HTSearch;	// 보는 1번 창 단계

		        if (kOwner.arrHistory.length <= 1 || HTSearch.bSearching || document.getElementById("QueryDepthInput") != null)
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
		        if (HTQuery.bQuerying)	// 쿼리 중: 중단 (앞 결과 상태로 돌아감) 뒤 이어서
		        {
		            HTQuery.StopQuery();

		            await HTQuery.kQueryTask;
		        }

		        const nIndex = HTQuery.nHistory + nDirection;	// 갈 단계

		        if (HTQuery.bQuerying || nIndex < 0 || nIndex >= HTQuery.arrHistory.length)
		        {
		            return;
		        }

		        const kStep = HTQuery.arrHistory[nIndex];	// 그 단계 { sKey, kFrom, kTo, nRange }
		        const kPrev = HTQuery.GetState();			// 중단·실패 때 되돌릴 상태 (단계 바꾸기 전)

		        HTQuery.nHistory = nIndex;

		        HTQuery.SetPicker(kStart, kStartHour, kStartMinute, kStep.kFrom);
		        HTQuery.SetPicker(kEnd, kEndHour, kEndMinute, kStep.kTo);

		        kQueryRange.selectedIndex	= kStep.nRange;
		        kQueryKey.value				= kStep.sKey === "*" ? "" : kStep.sKey;	// 필드 값 클릭으로 바뀐 쿼리도 그 단계로 (* 전체: 비움)

		        HTQuery.OnQueryInput();	// 선택한 저장 쿼리와 달라지면 선택 해제

		        const kResult = HTQuery.mapStepResult.get(kStep);	// 그 단계 마지막 본 결과

		        if (kResult?.kPage != null)	// 결과 있음: 다시 쿼리 안 함
		        {
		            HTQuery.ShowStep(kStep, kResult);

		            return;
		        }

		        HTQuery.Query(kStep.sKey, kStep.kFrom, kStep.kTo, false, false, kPrev);
		    }

		    static SwapRange(bEnter)	// 쿼리 모드 들어감·나감: 명령 창 기간 서치·쿼리 따로 보관·꺼냄 (쿼리 처음: D 최근 기간)
		    {
		        const kOther = HTQuery.kOtherRange;	// 꺼낼 값

		        HTQuery.kOtherRange = { sStart: kStart.value, sStartHour: kStartHour.value, sStartMinute: kStartMinute.value, sEnd: kEnd.value, sEndHour: kEndHour.value, sEndMinute: kEndMinute.value, nRange: kQueryRange.selectedIndex };

		        if (kOther == null)	// 쿼리 처음: D 최근 기간
		        {
		            HTQuery.SetRecent();

		            return;
		        }

		        kStart.value				= kOther.sStart;
		        kStartHour.value			= kOther.sStartHour;
		        kStartMinute.value			= kOther.sStartMinute;
		        kEnd.value					= kOther.sEnd;
		        kEndHour.value				= kOther.sEndHour;
		        kEndMinute.value			= kOther.sEndMinute;
		        kQueryRange.selectedIndex	= kOther.nRange;
		    }

		    static SetRange(nMinute)	// 기간: 지금 기준 nMinute 분 (D 에 있음: D 고름 → 쿼리 때 S·E, 없음: D 해제·S·E 직접)
		    {
		        kQueryRange.value = String(nMinute);

		        if (kQueryRange.selectedIndex >= 0)
		        {
		            return;
		        }

		        const kTo = new Date();	// 지금 (분 단위)

		        kTo.setSeconds(0, 0);

		        HTQuery.SetPicker(kStart, kStartHour, kStartMinute, new Date(kTo.getTime() - nMinute * 60 * 1000));
		        HTQuery.SetPicker(kEnd, kEndHour, kEndMinute, kTo);
		    }

		    static SetRecent()	// D 최근 기간 → S·E (지금 (분 단위) - D ~ 지금)
		    {
		        if (kQueryRange.selectedIndex < 0)	// D 해제 (S·E 직접): 기본 60분
		        {
		            kQueryRange.value = "60";
		        }

		        const kTo	= new Date();	// 지금 (분 단위)

		        kTo.setSeconds(0, 0);

		        HTQuery.SetPicker(kStart, kStartHour, kStartMinute, new Date(kTo.getTime() - Number(kQueryRange.value) * 60 * 1000));	// D 값: 분
		        HTQuery.SetPicker(kEnd, kEndHour, kEndMinute, kTo);
		    }

		    static SetPicker(kDate, kHour, kMinute, kTime)	// 날짜·시·분 선택에 시각 넣기
		    {
		        const funcPad = (nValue) => String(nValue).padStart(2, "0");

		        kDate.value		= `${kTime.getFullYear()}-${funcPad(kTime.getMonth() + 1)}-${funcPad(kTime.getDate())}`;
		        kHour.value		= funcPad(kTime.getHours());
		        kMinute.value	= funcPad(kTime.getMinutes());
		    }

		    static GetPicker(kDate, kHour, kMinute)	// 날짜·시·분 선택 → 시각 (시 24: 다음 날 0시 + 분, 날짜 없음: null)
		    {
		        if (kDate.value === "")
		        {
		            return null;
		        }

		        const [nYear, nMonth, nDay] = kDate.value.split("-").map(Number);

		        return new Date(nYear, nMonth - 1, nDay, Number(kHour.value), Number(kMinute.value));
		    }

		    static ToMinute(kTime)	// 시각 → yyyyMMddHHmm
		    {
		        const funcPad = (nValue) => String(nValue).padStart(2, "0");

		        return Number(`${kTime.getFullYear()}${funcPad(kTime.getMonth() + 1)}${funcPad(kTime.getDate())}${funcPad(kTime.getHours())}${funcPad(kTime.getMinutes())}`);
		    }

		    static FormatHour(nHour)	// yyyyMMddHH → yyyy-MM-dd HH시
		    {
		        const sText = String(nHour);

		        return `${sText.slice(0, 4)}-${sText.slice(4, 6)}-${sText.slice(6, 8)} ${sText.slice(8)}시`;
		    }

		    static FormatMinute(nMinute)	// yyyyMMddHHmm → yyyy-MM-dd HH:mm
		    {
		        const sText = String(nMinute);

		        return `${sText.slice(0, 4)}-${sText.slice(4, 6)}-${sText.slice(6, 8)} ${sText.slice(8, 10)}:${sText.slice(10)}`;
		    }

		    static FormatSpan(nStart, nEnd)	// 기간 길이 (yyyyMMddHHmm 시작·끝 → 막대 단위: 1분 이하 N초, 1시간 이하 N분, 24시간 이하 N시간 N분, 넘음: N일 N시간 N분, 0 인 단위 뺌)
		    {
		        const funcDate	= (nMinute) => { const sText = String(nMinute); return new Date(Number(sText.slice(0, 4)), Number(sText.slice(4, 6)) - 1, Number(sText.slice(6, 8)), Number(sText.slice(8, 10)), Number(sText.slice(10, 12))); };	// yyyyMMddHHmm → 시각
		        const nMinute	= Math.max(0, Math.round((funcDate(nEnd) - funcDate(nStart)) / 60000));	// 기간 (분)

		        if (nMinute <= 1)	// 초별
		        {
		            return `${nMinute * 60}초`;
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

		    static async LoadPage(nPage, kPrev = null, kStep = HTQuery.arrHistory[HTQuery.nHistory])	// 쿼리 결과 nPage 페이지 (최신 순 nPageSize 건씩, 진행 초 표시, 중단: 앞 결과 그대로, kPrev: 중단·실패 때 되돌릴 상태, kStep: 결과 보관 단계)
		    {
		        if (HTQuery.bQuerying)	// 쿼리 중
		        {
		            return;
		        }

		        const nBegin	= performance.now();		// 시작 (진행 초)
		        const kAbort	= new AbortController();	// 중단 (중단 버튼 → 서버도 남은 시간 폴더 안 읽음)

		        const funcShow = () =>	// 진행 표시 (1초마다): 쿼리 중 · N초
		        {
		            const nSec = Math.floor((performance.now() - nBegin) / 1000);	// 쿼리 시간 (초)

		            kQueryTitle.textContent		= `${HTQuery.FormatSpan(HTQuery.nStart, HTQuery.nEnd)} - 쿼리 중 · ${nSec}초`;
		            kQueryRunButton.textContent	= `${nSec}초`;	// 쿼리 실행 버튼: 진행 초 (끝: 실행)
		            kQueryProgress.textContent	= `쿼리 중 · ${nSec}초\n"${HTQuery.sQueryKey}"\n${HTQuery.FormatMinute(HTQuery.nStart)} ~ ${HTQuery.FormatMinute(HTQuery.nEnd)}`;
		        };

		        HTQuery.bQuerying			= true;
		        HTQuery.nChunkSeq++;	// 앞 페이지 이어 읽기 응답 버림
		        HTQuery.kQueryAbort			= kAbort;
		        kQueryRunButton.disabled	= true;
		        kQueryStopButton.disabled	= false;

		        funcShow();

		        kQueryProgress.hidden = false;

		        const nTimer = setInterval(funcShow, 1000);	// 진행 초 갱신

		        try
		        {
		            const kResponse = await fetch("/api/query?" + new URLSearchParams({ sKey: HTQuery.sQueryKey, nStart: String(HTQuery.nStart), nEnd: String(HTQuery.nEnd), nPage: String(nPage), nOffset: "0", nCount: String(HTQuery.CHUNK) }), { signal: kAbort.signal });	// 페이지 처음 CHUNK 건 (나머지: 아래로 내리면 LoadMore)

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData = await kResponse.json();

		            HTQuery.ShowPage(kData);

		            if (kData.sError === "")	// 이 단계 마지막 본 결과 (← →·새 창: 그대로)
		            {
		                HTQuery.SaveStepResult(kStep, { kPage: kData });
		            }
		        }
		        catch (kError)	// 중단·요청 실패 (화면은 앞 결과 그대로, 새 쿼리: 상태도 앞 결과로)
		        {
		            kQueryTitle.textContent = kError.name === "AbortError" ? `${HTQuery.FormatSpan(HTQuery.nStart, HTQuery.nEnd)} - 쿼리 중단 (${Math.floor((performance.now() - nBegin) / 1000)}초)` : `${HTQuery.FormatSpan(HTQuery.nStart, HTQuery.nEnd)} - 쿼리 실패: ${ErrorText(kError)}`;

		            if (kPrev != null)	// 새 쿼리: 앞 결과 상태로 (페이지·막대·클릭이 보이는 결과 기준)
		            {
		                HTQuery.kStatAbort?.abort();	// 새 쿼리 통계 끊음 (왼쪽 값 수: -)

		                Object.assign(HTQuery, kPrev);

		                HTQuery.ShowHistory();
		            }
		        }
		        finally
		        {
		            clearInterval(nTimer);

		            HTQuery.bQuerying			= false;
		            HTQuery.kQueryAbort			= null;
		            kQueryRunButton.disabled	= false;
		            kQueryRunButton.textContent	= "실행";
		            kQueryStopButton.disabled	= HTQuery.kStatAbort == null;	// 통계 아직 가져오는 중: 중단 가능
		            kQueryProgress.hidden		= true;
		        }
		    }

		    static ShowPage(kData)	// 쿼리 결과 페이지 그리기 (막대·처음 줄·페이지 번호·제목, 쿼리 응답·단계 보관 결과)
		    {
		        HTQuery.nPage		= kData.nPage;
		        HTQuery.nPageSize	= kData.nPageSize;
		        HTQuery.nTotal		= kData.nTotal;

		        HTQuery.nRowEnd		= kData.arrLine.length < HTQuery.CHUNK ? kData.arrLine.length : -1;	// 처음 CHUNK 건보다 덜 옴: 페이지 끝

		        HTQuery.ShowChart(kData.arrHour, kData.arrMinute, kData.arrSecond ?? []);
		        HTQuery.ShowRows(kData.arrLine);
		        HTQuery.ShowPager();

		        kQueryLogTitle.textContent	= kData.nTotal === 0 ? "로그 없음" : `로그 ${(kData.nPage * kData.nPageSize + 1).toLocaleString()} ~ ${Math.min(kData.nTotal, (kData.nPage + 1) * kData.nPageSize).toLocaleString()} / ${kData.nTotal.toLocaleString()}건 (최신 순)`;

		        kQueryTitle.textContent = kData.sError !== "" ? `${HTQuery.FormatSpan(kData.nStart, kData.nEnd)} - 쿼리 실패: ${kData.sError}` : `${HTQuery.FormatSpan(kData.nStart, kData.nEnd)} - 쿼리 결과: ${kData.nTotal.toLocaleString()}건 · 쿼리 시간: ${kData.nElapsedMs} ms`;	// 기간 길이만 (쿼리·S·E 는 입력칸)
		    }

		    static StopQuery()	// 중단 버튼: 쿼리·통계 요청 끊음 (서버도 남은 시간 폴더 안 읽음, 화면은 앞 결과 그대로)
		    {
		        HTQuery.kQueryAbort?.abort();
		        HTQuery.kStatAbort?.abort();
		    }

		    static async LoadSaved()	// 저장 쿼리 목록 가져와 쿼리 설정 목록 그리기 (실패: 빈 목록)
		    {
		        try
		        {
		            const kResponse = await fetch("/api/query/saved");

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            HTQuery.ShowSaved((await kResponse.json()).arrQuery);
		        }
		        catch (kError)	// 요청 실패
		        {
		            HTQuery.ShowSaved([]);
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

		            HTQuery.ShowAlarm((await kResponse.json()).arrAlarm);
		        }
		        catch (kError)	// 요청 실패
		        {
		            HTQuery.ShowAlarm([]);
		        }
		    }

		    static ShowSaved(arrQuery, sShowKey = "")	// 저장 쿼리 목록 바꿈 (sShowKey: 그 제목 줄 보이게, 선택한 제목 없어짐: 선택 해제) → 쿼리 탭이면 다시 그림
		    {
		        HTQuery.arrSaved = arrQuery;

		        if (!arrQuery.some(kSaved => kSaved.sKey === HTQuery.sSavedKey))
		        {
		            HTQuery.sSavedKey = "";
		        }

		        if (!HTQuery.bAlarm)
		        {
		            HTQuery.ShowEditList(arrQuery.findIndex(kSaved => kSaved.sKey === sShowKey));
		        }
		    }

		    static ShowAlarm(arrAlarm, nShowID = 0)	// 알람 목록 바꿈 (nShowID: 그 알람 줄 보이게) → 알람 탭이면 다시 그림
		    {
		        HTQuery.arrAlarm = arrAlarm;

		        if (HTQuery.bAlarm)
		        {
		            HTQuery.ShowEditList(arrAlarm.findIndex(kAlarm => kAlarm.nID === nShowID));
		        }
		    }

		    static GetEditItems()	// 쿼리 설정 지금 탭 목록 (저장 쿼리·알람)
		    {
		        return HTQuery.bAlarm ? HTQuery.arrAlarm : HTQuery.arrSaved;
		    }

		    static ShowSettingTab(bAlarm)	// 쿼리 설정 탭: 쿼리 (저장 쿼리)·알람 (같은 줄·추가 틀, 바꿈: 맨 위부터)
		    {
		        HTQuery.bAlarm = bAlarm;

		        kQuerySettingQuery.classList.toggle("cur", !bAlarm);
		        kQuerySettingAlarm.classList.toggle("cur", bAlarm);

		        kQueryEditAdd.title = bAlarm ? "알람 추가" : "쿼리 추가";
		        kQueryEditScroll.scrollTop = 0;

		        HTQuery.ShowEditList();
		    }

		    static ShowEditList(nShow = -1)	// 쿼리 설정 지금 탭 줄 그리기 (전체, nShow: 그 줄 보이게, 더블클릭: 실행, 드래그: 순서, 쿼리: 선택 강조, 알람: 꺼짐 흐리게)
		    {
		        const arrItem = HTQuery.GetEditItems();

		        const arrRow = arrItem.map((kItem, nIndex) =>
		        {
		            const kRow	= document.createElement("tr");
		            const kKey	= kRow.insertCell();	// 제목
		            const kAct	= kRow.insertCell();	// 수정·삭제

		            kRow.dataset.index	= String(nIndex);	// 목록 위치
		            kRow.draggable		= true;
		            kRow.className		= HTQuery.bAlarm ? (kItem.bEnable ? "" : "off") : (kItem.sKey === HTQuery.sSavedKey ? "sel" : "");
		            kKey.textContent	= kItem.sKey;
		            kKey.title			= kItem.sQuery;	// 쿼리
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
		        const kItem		= kRow == null ? undefined : HTQuery.GetEditItems()[Number(kRow.dataset.index)];
		        const sAction	= kEvent.target.closest("button[data-action]")?.dataset.action;	// 누른 버튼 (없음: 제목)

		        if (kItem === undefined)
		        {
		            return;
		        }

		        if (kEvent.type === "dblclick")
		        {
		            if (sAction === undefined)
		            {
		                HTQuery.ChooseSaved(kItem);
		            }
		        }
		        else if (sAction === "edit")
		        {
		            if (HTQuery.bAlarm)
		            {
		                HTQuery.OpenAlarm(kItem);
		            }
		            else
		            {
		                HTQuery.OpenSaved(kItem);
		            }
		        }
		        else if (sAction === "delete")
		        {
		            if (HTQuery.bAlarm)
		            {
		                HTQuery.DeleteAlarm(kItem);
		            }
		            else
		            {
		                HTQuery.DeleteSaved(kItem);
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

		        HTQuery.nDragIndex = Number(kRow.dataset.index);

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
		        if (HTQuery.nDragIndex < 0)	// 설정 줄 드래그 아님
		        {
		            return;
		        }

		        kEvent.preventDefault();
		        kEvent.dataTransfer.dropEffect = "move";

		        HTQuery.ClearDrop();

		        const kDrop = HTQuery.GetDropIndex(kEvent);

		        kDrop?.kRow.classList.add(kDrop.bAfter ? "dropafter" : "dropbefore");
		    }

		    static ClearDrop()	// 쿼리 설정 놓을 자리 표시 지움
		    {
		        document.querySelectorAll("#QueryEditList .dropbefore, #QueryEditList .dropafter").forEach(kItem => kItem.classList.remove("dropbefore", "dropafter"));
		    }

		    static OnEditDrop(kEvent)	// 쿼리 설정 놓음: 줄 → 그 자리
		    {
		        const nFrom = HTQuery.nDragIndex;	// 옮길 줄

		        if (nFrom < 0)
		        {
		            return;
		        }

		        kEvent.preventDefault();

		        const kDrop = HTQuery.GetDropIndex(kEvent);	// 줄 자리

		        HTQuery.OnEditDragEnd();

		        if (kDrop != null)
		        {
		            HTQuery.MoveEdit(nFrom, kDrop.nInsert > nFrom ? kDrop.nInsert - 1 : kDrop.nInsert);
		        }
		    }

		    static OnEditDragEnd()	// 쿼리 설정 드래그 끝 (놓음·취소): 표시 지움
		    {
		        HTQuery.nDragIndex = -1;

		        HTQuery.ClearDrop();

		        kQueryEditList.querySelector("tr.drag")?.classList.remove("drag");
		    }

		    static async MoveEdit(nFrom, nTo)	// 쿼리 설정 줄 nFrom → nTo 자리 (서버 목록·설정 저장, 같은 자리: 안 함) → 옮긴 줄 보이게
		    {
		        const kItem = HTQuery.GetEditItems()[nFrom];	// 옮길 저장 쿼리·알람

		        if (kItem === undefined || nFrom === nTo)
		        {
		            return;
		        }

		        if (HTQuery.bAlarm)
		        {
		            const kData = await HTQuery.PostSaved("/api/alarm/move", { nID: kItem.nID, nTo }, "알람");

		            if (kData != null)
		            {
		                HTQuery.ShowAlarm(kData.arrAlarm, kItem.nID);
		            }
		        }
		        else
		        {
		            const kData = await HTQuery.PostSaved("/api/query/saved/move", { sKey: kItem.sKey, nTo }, "저장 쿼리");

		            if (kData != null)
		            {
		                HTQuery.ShowSaved(kData.arrQuery, kItem.sKey);
		            }
		        }
		    }

		    static OnQueryInput()	// 쿼리칸 고침: 선택한 저장 쿼리와 달라지면 선택 해제
		    {
		        const kSaved = HTQuery.arrSaved.find(kItem => kItem.sKey === HTQuery.sSavedKey);

		        if (kSaved !== undefined && kQueryKey.value.trim() !== kSaved.sQuery)
		        {
		            HTQuery.sSavedKey = "";
		        }
		    }

		    static async ChooseSaved(kItem)	// 쿼리 설정 제목 더블클릭: 창 닫고 쿼리칸·기간 넣고 바로 쿼리 (쿼리 중: 중단 후)
		    {
		        HTQuery.sSavedKey	= HTQuery.bAlarm ? "" : kItem.sKey;
		        kQueryKey.value		= kItem.sQuery;

		        kQuerySettingDialog.close();

		        HTQuery.SetRange(kItem.nMinute);

		        if (HTQuery.bQuerying)	// 쿼리 중: 중단 (앞 결과 상태로 돌아감) 뒤 이어서
		        {
		            HTQuery.StopQuery();

		            await HTQuery.kQueryTask;
		        }

		        HTQuery.RunQuery();
		    }

		    static OpenSaved(kSaved)	// 쿼리 추가·수정 창 열기 (kSaved: 수정할 저장 쿼리, null: 추가 → 빈칸·기본 간격)
		    {
		        HTQuery.sEditKey			= kSaved?.sKey ?? "";
		        kQuerySaveTitle.textContent	= kSaved == null ? "쿼리 추가" : "쿼리 수정";
		        kQuerySaveKey.value			= kSaved?.sKey ?? "";
		        kQuerySaveQuery.value		= kSaved?.sQuery ?? "";
		        kQuerySaveMinute.value		= String(kSaved?.nMinute ?? HTQuery.SAVED_MINUTE);	// 간격 (추가: 기본)

		        kQuerySaveDialog.classList.remove("alarm");
		        kQuerySaveDialog.showModal();
		    }

		    static OpenAlarm(kAlarm)	// 알람 추가·수정 창 열기 (쿼리 추가 창 + 알람 칸, kAlarm: 수정할 알람 → 그 값, null: 추가 → 기본값)
		    {
		        HTQuery.nEditID				= kAlarm?.nID ?? 0;
		        kQuerySaveTitle.textContent	= kAlarm == null ? "알람 추가" : "알람 수정";
		        kQuerySaveKey.value			= kAlarm?.sKey ?? "";
		        kQuerySaveQuery.value		= kAlarm?.sQuery ?? "";
		        kAlarmMinute.value			= String(kAlarm?.nMinute ?? HTQuery.ALARM_MINUTE);
		        kAlarmCount.value			= String(kAlarm?.nCount ?? HTQuery.ALARM_COUNT);
		        kAlarmDelay.value			= String(kAlarm?.nDelay ?? HTQuery.ALARM_DELAY);
		        kAlarmMail.value			= "";
		        kAlarmSMS.value				= "";
		        kAlarmHook.value			= kAlarm?.sHook ?? "";
		        HTQuery.arrAlarmMail		= [...(kAlarm?.aMail ?? [])];
		        HTQuery.arrAlarmSMS			= [...(kAlarm?.aSMS ?? [])];
		        kAlarmEnable.checked		= kAlarm?.bEnable ?? true;

		        HTQuery.ShowAlarmList(kAlarmMailList, HTQuery.arrAlarmMail);
		        HTQuery.ShowAlarmList(kAlarmSMSList, HTQuery.arrAlarmSMS);

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

		        if (sQuery === "")
		        {
		            alert("쿼리를 입력해 주세요.");
		            kQueryInput.focus();

		            return null;
		        }

		        if (!HTKey.IsValid(sQuery))
		        {
		            alert("쿼리 형식을 확인해 주세요.\n대문자 AND·OR·NOT 앞뒤에 검색어, 괄호 안에 검색어 (예: A AND (B NOT C) OR D)");
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
		            [!Number.isInteger(kAlarm.nMinute) || kAlarm.nMinute < 1 || kAlarm.nMinute > HTQuery.ALARM_MINUTE_MAX, kAlarmMinute, `간격을 1 ~ ${HTQuery.ALARM_MINUTE_MAX} 분으로 입력해 주세요.`],
		            [!Number.isInteger(kAlarm.nCount) || kAlarm.nCount < 1, kAlarmCount, "건수를 1 이상으로 입력해 주세요."],
		            [!Number.isInteger(kAlarm.nDelay) || kAlarm.nDelay < 0 || kAlarm.nDelay > HTQuery.ALARM_DELAY_MAX, kAlarmDelay, `억제를 0 ~ ${HTQuery.ALARM_DELAY_MAX} 분으로 입력해 주세요.`],
		            [kAlarm.sHook !== "" && !/^https?:\/\/\S+$/i.test(kAlarm.sHook), kAlarmHook, "훅 주소를 http·https 로 입력해 주세요."],
		        ];
		        const arrWrong	= arrCheck.find(arrItem => arrItem[0]);	// 처음 틀린 칸

		        if (arrWrong !== undefined)
		        {
		            alert(arrWrong[2]);
		            arrWrong[1].focus();

		            return null;
		        }

		        if (!HTQuery.AddAlarmMail() || !HTQuery.AddAlarmSMS())	// 입력칸에 남은 메일·SMS: 추가 (오류: 알림)
		        {
		            return null;
		        }

		        return { ...kAlarm, aMail: [...HTQuery.arrAlarmMail], aSMS: [...HTQuery.arrAlarmSMS] };
		    }

		    static AddAlarmMail()	// 알람 메일 추가 버튼·Enter (반환: 성공·빈칸 true)
		    {
		        return HTQuery.AddAlarmItem(kAlarmMail, kAlarmMailList, HTQuery.arrAlarmMail, HTQuery.MAIL_REGEX, `메일 형식·개수 (최대 ${HTQuery.ALARM_LIST_MAX}) 를 확인해 주세요.`);
		    }

		    static AddAlarmSMS()	// 알람 SMS 추가 버튼·Enter (반환: 성공·빈칸 true)
		    {
		        return HTQuery.AddAlarmItem(kAlarmSMS, kAlarmSMSList, HTQuery.arrAlarmSMS, HTQuery.SMS_REGEX, `SMS 번호 형식·개수 (숫자·-, 최대 ${HTQuery.ALARM_LIST_MAX}) 를 확인해 주세요.`);
		    }

		    static AddAlarmItem(kInput, kList, arrItem, kRegex, sError)	// 알람 메일·SMS 입력 → 목록 끝에 (형식·최대 개수 검사, 겹침: 안 넣음, 입력칸 비움, 반환: 성공·빈칸 true, 오류: 알림·입력칸으로 → false)
		    {
		        const sItem = kInput.value.trim();	// 넣을 값

		        if (sItem === "")
		        {
		            return true;
		        }

		        const bSame = arrItem.some(sOld => sOld.toLowerCase() === sItem.toLowerCase());	// 이미 있음

		        if (!kRegex.test(sItem) || (!bSame && arrItem.length >= HTQuery.ALARM_LIST_MAX))
		        {
		            alert(sError);
		            kInput.focus();

		            return false;
		        }

		        if (!bSame)
		        {
		            arrItem.push(sItem);

		            HTQuery.ShowAlarmList(kList, arrItem);
		        }

		        kInput.value = "";

		        return true;
		    }

		    static ShowAlarmList(kList, arrItem)	// 알람 메일·SMS 목록 그리기 (입력칸 밑, 줄마다 삭제, 없음: 숨김)
		    {
		        kList.replaceChildren(...arrItem.map((sItem, nIndex) =>
		        {
		            const kItem		= document.createElement("span");
		            const kText		= document.createElement("span");
		            const kButton	= document.createElement("button");

		            kItem.className		= "alarmitem";
		            kText.textContent	= sItem;
		            kButton.type		= "button";
		            kButton.className	= "danger";
		            kButton.textContent	= "삭제";

		            kButton.addEventListener("click", () =>
		            {
		                arrItem.splice(nIndex, 1);

		                HTQuery.ShowAlarmList(kList, arrItem);
		            });

		            kItem.append(kText, kButton);

		            return kItem;
		        }));
		    }

		    static async SaveSavedOk()	// 쿼리 추가·수정 저장 → 서버 aQuery (추가: 같은 제목은 쿼리 바꿈, 수정: 그 자리, 겹침: 오류, 실패: 창 유지)
		    {
		        if (kQuerySaveDialog.classList.contains("alarm"))
		        {
		            HTQuery.SaveAlarmOk();

		            return;
		        }

		        const sOldKey	= HTQuery.sEditKey;	// 수정할 제목 ("": 추가)
		        const kInput	= HTQuery.CheckSaved(kQuerySaveKey, kQuerySaveQuery);	// 제목·쿼리 (오류: null)
		        const nMinute	= Number(kQuerySaveMinute.value);	// 간격 (분)

		        if (kInput != null && (!Number.isInteger(nMinute) || nMinute < 1 || nMinute > HTQuery.SAVED_MINUTE_MAX))
		        {
		            alert(`간격을 1 ~ ${HTQuery.SAVED_MINUTE_MAX} 분으로 입력해 주세요.`);
		            kQuerySaveMinute.focus();

		            return;
		        }

		        const kData		= kInput == null ? null : await HTQuery.PostSaved("/api/query/saved", { ...kInput, nMinute, sOldKey }, "저장 쿼리");	// 응답 (실패: null)

		        if (kData != null)
		        {
		            if (sOldKey !== "" && HTQuery.sSavedKey === sOldKey)	// 선택한 제목 수정: 새 제목으로
		            {
		                HTQuery.sSavedKey = kInput.sKey;
		            }

		            kQuerySaveDialog.close();

		            HTQuery.ShowSaved(kData.arrQuery, kInput.sKey);	// 추가·수정한 제목 줄 보이게
		        }
		    }

		    static async TestAlarm()	// 알람 테스트 발송: 창 값 검사 → 서버가 메일·훅 바로 보냄 (저장·알람 로그 없음) → 채널별 결과 알림
		    {
		        const kInput	= HTQuery.CheckSaved(kQuerySaveKey, kQuerySaveQuery);	// 제목·쿼리 (오류: null)
		        const kAlarm	= kInput == null ? null : HTQuery.CheckAlarm();			// 알람 칸 (오류: null)
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

		        const kData = await HTQuery.PostSaved("/api/alarm/test", { ...kInput, ...kAlarm, nID: HTQuery.nEditID }, "테스트 발송");	// 응답 (실패: null)

		        kButton.disabled = false;

		        if (kData != null)
		        {
		            alert("테스트 발송\n" + kData.arrResult.map(kResult => `${kResult.sChannel === "Mail" ? "메일" : "훅"}: ${kResult.sError === "" ? "성공" : `실패 (${kResult.sError})`}`).join("\n"));
		        }
		    }

		    static async SaveAlarmOk()	// 알람 추가·수정 저장 → 서버 aAlarm (추가: 끝에, 수정: 그 자리, 제목 겹침: 오류, 실패: 창 유지)
		    {
		        const kInput	= HTQuery.CheckSaved(kQuerySaveKey, kQuerySaveQuery);	// 제목·쿼리 (오류: null)
		        const kAlarm	= kInput == null ? null : HTQuery.CheckAlarm();			// 알람 칸 (오류: null)
		        const kData		= kAlarm == null ? null : await HTQuery.PostSaved("/api/alarm", { ...kInput, ...kAlarm, nID: HTQuery.nEditID }, "알람");	// 응답 (실패: null)

		        if (kData != null)
		        {
		            kQuerySaveDialog.close();

		            HTQuery.ShowAlarm(kData.arrAlarm, kData.nID);	// 추가·수정한 알람 줄 보이게
		        }
		    }

		    static DeleteSaved(kSaved)	// 쿼리 설정 삭제: 확인 팝업 → RunDeleteSaved
		    {
		        AskDelete("저장 쿼리 삭제", kSaved.sKey, kSaved.sQuery, () => HTQuery.RunDeleteSaved(kSaved));
		    }

		    static async RunDeleteSaved(kSaved)	// 쿼리 설정 삭제 실행 → 목록 다시 (선택한 제목: 선택 해제)
		    {
		        const kData = await HTQuery.PostSaved("/api/query/saved/delete", { sKey: kSaved.sKey }, "저장 쿼리");	// 응답 (실패: null)

		        if (kData != null)
		        {
		            HTQuery.ShowSaved(kData.arrQuery);
		        }
		    }

		    static DeleteAlarm(kAlarm)	// 알람 삭제: 확인 팝업 → RunDeleteAlarm
		    {
		        AskDelete("알람 삭제", kAlarm.sKey, kAlarm.sQuery, () => HTQuery.RunDeleteAlarm(kAlarm));
		    }

		    static async RunDeleteAlarm(kAlarm)	// 알람 삭제: 그 ID 지움 → 목록 다시
		    {
		        const kData = await HTQuery.PostSaved("/api/alarm/delete", { nID: kAlarm.nID }, "알람");	// 응답 (실패: null)

		        if (kData != null)
		        {
		            HTQuery.ShowAlarm(kData.arrAlarm);
		        }
		    }

		    static async PostSaved(sUrl, kBody, sName)	// 쿼리 설정 추가·수정·삭제·순서 요청 (반환: { arrQuery·arrAlarm }, 실패: 알림 후 null)
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

		    static ShowChart(arrHour, arrMinute, arrSecond = [])	// 건수 막대 (0건 시간 포함, 1분: 초별 (안 눌림), 1시간 미만: 분별, 24시간 이하: 시간별, 넘음: 날짜별, 마우스: 시각·건수, 시간 없음: 숨김)
		    {
		        const bSecond = arrSecond.length > 0;							// 초별 (기간 1분 이하: 분별 막대 클릭)
		        const bMinute = !bSecond && HTQuery.bMinute && arrMinute.length > 0;	// 분별 (기간 1시간 미만)

		        kQueryChart.hidden = arrHour.length === 0;

		        if (arrHour.length === 0)
		        {
		            HTQuery.arrBar = [];

		            kQueryChart.replaceChildren();

		            return;
		        }

		        const bDay		= HTQuery.bDay;			// 날짜별 (기간 24시간 넘음)
		        const arrBar	= [];					// 막대 { nFirst, nLast, nCount } (첫·끝 시간 yyyyMMddHH (분별: yyyyMMddHHmm), 건수 합)

		        for (const kItem of bSecond ? arrSecond.map(kSecond => ({ nHour: kSecond.nSecond, nCount: kSecond.nCount })) : (bMinute ? arrMinute.map(kMinute => ({ nHour: kMinute.nMinute, nCount: kMinute.nCount })) : arrHour))	// 날짜별: 같은 날짜 (yyyyMMdd) 시간끼리 합침
		        {
		            const kLast = arrBar[arrBar.length - 1];	// 앞 막대

		            if (bDay && kLast !== undefined && Math.floor(kLast.nFirst / 100) === Math.floor(kItem.nHour / 100))
		            {
		                kLast.nLast		= kItem.nHour;
		                kLast.nCount	+= kItem.nCount;
		            }
		            else
		            {
		                arrBar.push({ nFirst: kItem.nHour, nLast: kItem.nHour, nCount: kItem.nCount });
		            }
		        }

		        HTQuery.arrBar		= arrBar;
		        HTQuery.bDayBar		= bDay;
		        HTQuery.bMinuteBar	= bMinute;
		        HTQuery.bSecondBar	= bSecond;

		        const nMax		= arrBar.reduce((nValue, kItem) => Math.max(nValue, kItem.nCount), 1);					// 가장 큰 건수 (최소 1)
		        const nWant		= nMax * 1.5;																			// 바라는 y축 최대 (가장 큰 막대가 높이 2/3 정도)
		        const nUnit		= 10 ** Math.floor(Math.log10(nWant / 4));												// 눈금 단위 자릿수
		        const nStep		= Math.max(1, [1, 2, 5, 10].map(nItem => nItem * nUnit).find(nItem => nItem >= nWant / 4));	// 눈금 간격 (1·2·5 × 10^n)
		        const nTop		= Math.ceil(nWant / nStep) * nStep;														// y축 최대 (예: 2건 → 3)
		        const bFive		= bDay && arrBar.length > 50;	// x축 날짜 라벨 5일에 한 번 (50일 넘음: 첫 날·1·5·10·15·20·25일만)
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
		            const sHour		= String(kItem.nFirst);

		            kColumn.dataset.index	= nIndex;
		            kBar.style.height		= `${kItem.nCount / nTop * 100}%`;
		            kLabel.textContent		= bSecond ? String(Number(sHour.slice(12))) : bFive && nIndex > 0 && ![1, 5, 10, 15, 20, 25].includes(Number(sHour.slice(6, 8))) ? "" : (bMinute ? (nIndex > 0 && sHour.slice(10) === "00" ? `${Number(sHour.slice(8, 10))}:${sHour.slice(10)}` : String(Number(sHour.slice(10)))) : (!bDay ? String(Number(sHour.slice(8))) : (sHour.slice(4, 6) !== sLabelMonth ? `${Number(sHour.slice(4, 6))}/${Number(sHour.slice(6, 8))}` : String(Number(sHour.slice(6, 8))))));	// 초별: 초 (첫도 숫자만), 분별: 분 (첫도 숫자만, 중간 정시만 시:분), 시간별: 시, 날짜별: 첫·월 바뀜만 월/일·그 외 일

		            if (bDay && kLabel.textContent !== "")
		            {
		                kLabel.classList.toggle("month", sHour.slice(4, 6) !== sLabelMonth);	// 월/일 라벨: 존 색

		                sLabelMonth = sHour.slice(4, 6);
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

		            const kItem		= HTQuery.arrBar[Number(kColumn.dataset.index)];
		            const kChart	= kQueryChart.getBoundingClientRect();
		            const kBox		= kColumn.getBoundingClientRect();
		            const kBar		= kColumn.firstChild.getBoundingClientRect();

		            kTip.textContent	= HTQuery.bSecondBar ? `${HTQuery.FormatMinute(Math.floor(kItem.nFirst / 100))}:${String(kItem.nFirst % 100).padStart(2, "0")} · ${kItem.nCount.toLocaleString()}건` : HTQuery.bMinuteBar ? `${HTQuery.FormatMinute(kItem.nFirst)} · ${kItem.nCount.toLocaleString()}건` : (HTQuery.bDayBar ? `${HTQuery.FormatHour(kItem.nFirst).slice(0, 10)} · ${kItem.nCount.toLocaleString()}건` : `${HTQuery.FormatHour(kItem.nFirst)} · ${kItem.nCount.toLocaleString()}건`);	// 초별: yyyy-MM-dd HH:mm:ss (안 눌림), 분별: yyyy-MM-dd HH:mm, 날짜별: yyyy-MM-dd, 시간별: yyyy-MM-dd HH시
		            kTip.style.top		= `${Math.min(kBar.top, kBox.bottom) - kChart.top}px`;
		            kTip.hidden			= false;
		            kTip.style.left		= `${Math.min(Math.max(kBox.left + kBox.width / 2 - kChart.left, kTip.offsetWidth / 2), kChart.width - kTip.offsetWidth / 2)}px`;	// 막대 가운데 (양끝에서 잘리지 않게 창 안으로)
		        });

		        kBars.addEventListener("mouseleave", () =>
		        {
		            kTip.hidden = true;
		        });

		        kBars.addEventListener("click", (kEvent) =>	// 막대 클릭 (단계 남김): 날짜별 → 그 날 시간별, 시간별 → 그 시간 분별, 분별 → 그 1분 초별, 초별: 안 눌림
		        {
		            const kColumn = kEvent.target.closest("[data-index]");	// 누른 막대 칸

		            if (kColumn == null || HTQuery.bQuerying || HTQuery.bSecondBar || (HTQuery.bMinuteBar && HTQuery.arrBar.length === 1))
		            {
		                return;
		            }

		            const sTime	= String(HTQuery.arrBar[Number(kColumn.dataset.index)].nFirst);	// yyyyMMddHH (날짜별: 그 날 첫 시간, 분별: yyyyMMddHHmm)
		            const kFrom	= new Date(Number(sTime.slice(0, 4)), Number(sTime.slice(4, 6)) - 1, Number(sTime.slice(6, 8)), HTQuery.bDayBar ? 0 : Number(sTime.slice(8, 10)), HTQuery.bMinuteBar ? Number(sTime.slice(10, 12)) : 0);	// 그 날 0시·그 시간 정각·그 분
		            const kTo	= HTQuery.bMinuteBar ? kFrom : new Date(kFrom.getTime() + (HTQuery.bDayBar ? 24 : 1) * 3600 * 1000);	// 다음 날 0시·다음 정각·분별: 시작과 같음 (그 1분)

		            HTQuery.SetPicker(kStart, kStartHour, kStartMinute, kFrom);
		            HTQuery.SetPicker(kEnd, kEndHour, kEndMinute, kTo);

		            kQueryRange.selectedIndex = -1;

		            HTQuery.Query(HTQuery.sQueryKey, kFrom, kTo, true);
		        });

		        kBars.classList.toggle("click", !bSecond && (arrBar.length > 1 || !bMinute));	// 누를 수 있음 (초별·1분 막대 하나: 없음)

		        kPlot.append(kBars);
		        kQueryChart.replaceChildren(kAxisY, kPlot, kAxisX, kTip);
		    }

		    static GetMarkWords(sKey)	// 강조할 쿼리 단어 (소문자, 찾는 쪽 검색어마다: * 뺌, 필드:값 → 값, 서버 단어 규칙으로 나눔)
		    {
		        if (sKey === HTQuery.sMarkKey)
		        {
		            return HTQuery.setMarkWord;
		        }

		        const setWord	= new Set();	// 강조할 단어
		        const arrName	= ["machine", "folder", "type", ...HTQuery.arrField.map(kField => kField.sName.toLowerCase())];	// 필드 이름 (필드:값 구분)

		        for (const sTerm of HTKey.SplitKey(sKey))
		        {
		            const kField	= /^([A-Za-z][A-Za-z0-9_]*):(.+)$/.exec(sTerm.trim());	// 필드:값 후보
		            const sText		= kField != null && arrName.includes(kField[1].toLowerCase()) ? kField[2] : sTerm;	// 단어로 나눌 글자

		            if (sTerm.trim() === "*")	// 전체 로그
		            {
		                continue;
		            }

		            for (const kMatch of sText.matchAll(HTQuery.WORD_REGEX))
		            {
		                if (/[^-_]/.test(kMatch[0]))
		                {
		                    setWord.add(kMatch[0].toLowerCase());
		                }
		            }
		        }

		        HTQuery.sMarkKey	= sKey;
		        HTQuery.setMarkWord	= setWord;

		        return setWord;
		    }

		    static AppendMark(kParent, sText)	// 텍스트 추가 + 쿼리 단어 강조 (단어 단위·대소문자 무시, 앞뒤 -·_ 뗀 형태 포함, 앞 시간 제외 → 서버 색인과 같음, 텍스트 노드로만)
		    {
		        const setWord	= HTQuery.GetMarkWords(HTQuery.sQueryKey);	// 강조할 단어
		        const kTime		= /^\[\d{4}\/\d{2}\/\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]/.exec(sText);	// 앞 시간 (원문 그대로일 때)
		        let nPos		= kTime != null ? kTime[0].length : 0;	// 추가할 위치

		        kParent.append(sText.slice(0, nPos));

		        if (setWord.size > 0)
		        {
		            const nBase = nPos;	// 단어 찾기 시작 (앞 시간 뒤)

		            for (const kMatch of sText.slice(nBase).matchAll(HTQuery.WORD_REGEX))
		            {
		                const sWord	= kMatch[0].toLowerCase();
		                const nAt	= nBase + kMatch.index;	// 줄 안 위치

		                if (!setWord.has(sWord) && !setWord.has(sWord.replace(/^[-_]+|[-_]+$/g, "")))
		                {
		                    continue;
		                }

		                kParent.append(sText.slice(nPos, nAt), Object.assign(document.createElement("mark"), { textContent: kMatch[0] }));

		                nPos = nAt + kMatch[0].length;
		            }
		        }

		        kParent.append(sText.slice(nPos));
		    }

		    static ShowRows(arrLine, bAppend = false)	// 결과 줄 그리기 (No.·Machine·File·Line·Log, Log: 쿼리 단어 강조, 줄 클릭: 펼침, bAppend: 이어 읽은 줄 붙임)
		    {
		        const kFragment	= document.createDocumentFragment();
		        const nFirst	= bAppend ? HTQuery.arrLine.length : 0;	// 이번 줄 시작 위치 (페이지 안)

		        HTQuery.arrLine = bAppend ? HTQuery.arrLine.concat(arrLine) : arrLine;

		        arrLine.forEach((kLine, nOffset) =>
		        {
		            const kRow		= document.createElement("tr");
		            const nIndex	= nFirst + nOffset;	// 페이지 안 위치 (arrLine·펼침)

		            kRow.className		= "row";
		            kRow.dataset.index	= nIndex;

		            AddCell(kRow, (HTQuery.nPage * HTQuery.nPageSize + nIndex + 1).toLocaleString(), "num");	// 번호 (최신부터 1)
		            AddCell(kRow, kLine.sMachine, "nowrap");												// 머신
		            AddFileNameCell(kRow, kLine.sMachine, kLine.sPath, kLine.nLine ?? 0, HTQuery.sQueryKey === "*" ? "" : HTQuery.sQueryKey);	// 파일명 (Ctrl·Alt+클릭: 그 줄 강조, 줄 모름: 마지막 줄부터)
		            AddCell(kRow, kLine.nLine > 0 ? String(kLine.nLine) : "", "num");						// 클라 파일 줄 번호 (모름: 비움)
		            HTQuery.AppendMark(AddCell(kRow, "", "log"), kLine.sOrigin ?? kLine.sText);		// 로그: 원문 그대로 (시간·Type 포함, 쿼리 단어 강조)

		            kFragment.append(kRow);
		        });

		        if (bAppend)	// 이어 읽음: 아래에 붙임 (보던 위치 그대로)
		        {
		            kQueryBody.append(kFragment);
		            HTQuery.SetRowSpace();

		            return;
		        }

		        kQueryBody.replaceChildren(kFragment);
		        HTQuery.SetRowSpace();

		        kQueryScroll.scrollTop	= 0;
		        kQueryScroll.scrollLeft	= 0;
		    }

		    static GetPageRows()	// 이 페이지 전체 줄 수 (건수 기준, 서버가 요청보다 덜 줘서 끝 확인: 읽은 데까지)
		    {
		        const nPageRows = Math.max(0, Math.min(HTQuery.nPageSize, HTQuery.nTotal - HTQuery.nPage * HTQuery.nPageSize));	// 건수 기준

		        return HTQuery.nRowEnd >= 0 ? Math.min(nPageRows, HTQuery.nRowEnd) : nPageRows;
		    }

		    static SetRowSpace()	// 쿼리 2번 창 표 아래 빈 높이: 안 읽은 줄 수 × 줄 높이 → 스크롤 바 처음부터 페이지 전체 크기 (끝 확인: 빈 높이 없음)
		    {
		        const nPageRows	= HTQuery.GetPageRows();	// 이 페이지 전체 줄 수 (끝 확인: 읽은 데까지)
		        const nRowH		= kQueryBody.querySelector("tr.row")?.getBoundingClientRect().height ?? 0;					// 한 줄 높이 (px 소수까지, 숨김: 0)

		        kQueryBody.parentElement.style.marginBottom = `${Math.max(0, nPageRows - HTQuery.arrLine.length) * nRowH}px`;
		    }

		    static IsNearRowEnd()	// 쿼리 2번 창 보이는 곳 아래 끝이 읽은 줄 끝에서 300px 안 (빈 높이로 내려감 포함)
		    {
		        return kQueryBody.getBoundingClientRect().bottom - kQueryScroll.getBoundingClientRect().bottom <= 300;
		    }

		    static async LoadMore()	// 아래 끝 가까이 스크롤: 다음 CHUNK 건 이어 읽어 붙임 (페이지 끝·쿼리 중·읽는 중: 무시, 그 사이 새 쿼리·페이지: 버림)
		    {
		        const nPageRows = HTQuery.GetPageRows();	// 이 페이지 전체 줄 수 (끝 확인: 읽은 데까지)

		        if (HTQuery.bQuerying || HTQuery.bChunkLoading || HTQuery.arrLine.length >= nPageRows || HTQuery.sQueryKey === "")
		        {
		            return;
		        }

		        const nSeq		= HTQuery.nChunkSeq;																				// 이번 페이지 (새 쿼리·페이지: 바뀜)
		        const nRowH		= kQueryBody.querySelector("tr.row")?.getBoundingClientRect().height || 1;					// 한 줄 높이
		        const nGap		= Math.max(0, kQueryScroll.getBoundingClientRect().bottom - kQueryBody.getBoundingClientRect().bottom);	// 빈 높이로 내려간 만큼
		        const nCount	= Math.min(nPageRows - HTQuery.arrLine.length, Math.ceil(nGap / nRowH) + HTQuery.CHUNK);			// 읽을 줄 수
		        let bDone		= false;																							// 붙임 (이어서 더 읽을지 확인)

		        HTQuery.bChunkLoading = true;

		        try
		        {
		            const kResponse = await fetch("/api/query?" + new URLSearchParams({ sKey: HTQuery.sQueryKey, nStart: String(HTQuery.nStart), nEnd: String(HTQuery.nEnd), nPage: String(HTQuery.nPage), nOffset: String(HTQuery.arrLine.length), nCount: String(nCount) }));

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData = await kResponse.json();

		            if (nSeq === HTQuery.nChunkSeq && kData.sError === "")
		            {
		                if (kData.arrLine.length < nCount)	// 요청보다 덜 옴: 페이지 끝 (남은 빈 높이 없앰)
		                {
		                    HTQuery.nRowEnd = HTQuery.arrLine.length + kData.arrLine.length;
		                }

		                HTQuery.ShowRows(kData.arrLine, true);

		                bDone = kData.arrLine.length > 0;
		            }
		        }
		        catch (kError)	// 요청 실패: 다음 스크롤 때 다시
		        {
		            console.error(kError);
		        }
		        finally
		        {
		            HTQuery.bChunkLoading = false;
		        }

		        if (bDone && HTQuery.IsNearRowEnd())	// 읽는 사이 더 내려감: 이어서
		        {
		            HTQuery.LoadMore();
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
		        const kLine		= HTQuery.arrLine[nIndex];
		        const kDetail	= document.createElement("tr");	// 펼친 줄
		        const kCell		= kDetail.insertCell();
		        const kField	= document.createElement("div");	// 필드 한 줄 (Machine·Folder·File·Zone·Type·추가 필드, 값 없음 뺌)
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
		        HTQuery.AppendMark(kText, kLine.sOrigin ?? kLine.sText);	// 원문 그대로 (시간·Type 포함, 쿼리 단어 강조)

		        kCell.append(kField, kText);
		        kRow.after(kDetail);
		        kRow.classList.add("open");
		    }

		    static ShowPager()	// 페이지 번호 (PAGE_BLOCK 개씩, ‹ › 앞뒤 묶음, 현재 페이지 강조, 결과 없음: 숨김)
		    {
		        const nPageCount	= Math.ceil(HTQuery.nTotal / HTQuery.nPageSize);								// 전체 페이지 수
		        const nFirst		= Math.floor(HTQuery.nPage / HTQuery.PAGE_BLOCK) * HTQuery.PAGE_BLOCK;		// 이 묶음 첫 페이지
		        const nLast			= Math.min(nPageCount, nFirst + HTQuery.PAGE_BLOCK);						// 이 묶음 끝 (미포함)
		        const kFragment		= document.createDocumentFragment();

		        const funcAdd = (sText, nPage, sTitle) =>	// 페이지 버튼 1개
		        {
		            const kButton = document.createElement("button");

		            kButton.type		= "button";
		            kButton.textContent	= sText;
		            kButton.title		= sTitle;

		            if (sText === String(HTQuery.nPage + 1))
		            {
		                kButton.className = "cur";
		            }

		            kButton.addEventListener("click", () => HTQuery.LoadPage(nPage));

		            kFragment.append(kButton);
		        };

		        if (nFirst > 0)
		        {
		            funcAdd("‹", nFirst - 1, "이전 페이지 묶음");
		        }

		        for (let nPage = nFirst; nPage < nLast; ++nPage)
		        {
		            funcAdd(String(nPage + 1), nPage, `${(nPage * HTQuery.nPageSize + 1).toLocaleString()} ~ ${Math.min(HTQuery.nTotal, (nPage + 1) * HTQuery.nPageSize).toLocaleString()}`);
		        }

		        if (nLast < nPageCount)
		        {
		            funcAdd("›", nLast, "다음 페이지 묶음");
		        }

		        kQueryPager.replaceChildren(kFragment);

		        kQueryPager.hidden = nPageCount === 0;

		        HTQuery.FitLogHeight();	// 페이지 번호 보임·숨김: 로그 표 높이 다시
		    }

		    static FitLogHeight()	// 쿼리 2번 창 로그 표 높이: 브라우저 창 아래 끝에 맞춤 (페이지 번호까지 한 화면, 최소 HTSearch.SPLIT_MIN, 첫 화면만)
		    {
		        FitHeight(kQueryScroll, kQueryPanel);
		    }

		    static OpenSetting()	// 쿼리 설정 창 열기 (쿼리·알람 탭, 처음: 쿼리 탭·선택한 제목 줄 보이게)
		    {
		        HTQuery.ShowSettingTab(false);
		        HTQuery.LoadAlarm();	// 알람 목록 (열 때마다 새로)

		        kQuerySettingDialog.showModal();

		        HTQuery.ShowSaved(HTQuery.arrSaved, HTQuery.sSavedKey);	// 선택한 제목 줄 보이게 (창 열린 뒤)
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

		            if (HTQuery.arrField.length === 0)
		            {
		                HTQuery.ShowField(kData.arrField);
		            }
		        }
		        catch (kError)	// 요청 실패: 다음 쿼리 통계 때 그림
		        {
		        }
		    }

		    static async LoadStat(kStep = HTQuery.arrHistory[HTQuery.nHistory])	// 왼쪽 필드별 통계 (쿼리마다 1번, 앞 요청 끊음·늦게 온 결과 버림, 중단 버튼: 끊음, kStep: 결과 보관 단계)
		    {
		        const nSeq		= ++HTQuery.nStatSeq;		// 이 요청 번호
		        const kAbort	= new AbortController();	// 중단 (다음 통계·중단 버튼)

		        HTQuery.kStatAbort?.abort();	// 앞 통계 끊음 (서버 계산도 멈춤)

		        HTQuery.kStatAbort		= kAbort;
		        HTQuery.bStatLoading	= true;

		        HTQuery.ShowField(HTQuery.arrField.map(kField => ({ sName: kField.sName, bBase: kField.bBase })));	// 값 수: …

		        try
		        {
		            const kResponse = await fetch("/api/query/stat?" + new URLSearchParams({ sKey: HTQuery.sQueryKey, nStart: String(HTQuery.nStart), nEnd: String(HTQuery.nEnd) }), { signal: kAbort.signal });

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData = await kResponse.json();

		            if (nSeq !== HTQuery.nStatSeq)	// 뒤 요청 있음: 버림
		            {
		                return;
		            }

		            HTQuery.bStatLoading = false;

		            HTQuery.ShowField(kData.arrField);

		            if (kData.sError === "")	// 이 단계 필드 통계 (← →·새 창: 그대로)
		            {
		                HTQuery.SaveStepResult(kStep, { arrField: kData.arrField });
		            }
		        }
		        catch (kError)	// 중단·요청 실패: 값 수 -
		        {
		            if (nSeq === HTQuery.nStatSeq)
		            {
		                HTQuery.bStatLoading = false;

		                HTQuery.ShowField(HTQuery.arrField.map(kField => ({ sName: kField.sName, bBase: kField.bBase })));
		            }
		        }
		        finally
		        {
		            if (HTQuery.kStatAbort === kAbort)
		            {
		                HTQuery.kStatAbort = null;

		                if (!HTQuery.bQuerying)	// 쿼리도 끝남: 중단 끔
		                {
		                    kQueryStopButton.disabled = true;
		                }
		            }
		        }
		    }

		    static ShowField(arrField)	// 왼쪽 필드 그리기: 이름·값 수 (99 넘음: 99+, 가져오는 중: …, 통계 전: -, 추가 필드 0: 뺌), 클릭: 값 목록 새 창
		    {
		        const kFragment	= document.createDocumentFragment();
		        const arrShow	= arrField.filter(kField => kField.bBase || HTQuery.bStatLoading || kField.nValue !== 0);	// 그릴 필드 (추가 필드: 값 0 이면 뺌)

		        HTQuery.arrField	= arrField;
		        HTQuery.sMarkKey	= null;	// 필드 이름 바뀜: 강조 단어 다시 만듦

		        for (const [nIndex, kField] of arrShow.entries())
		        {
		            const kItem	= document.createElement("li");
		            const kName	= HTQuery.MakeFieldButton(kField.sName, HTQuery.bStatLoading ? "…" : (kField.nValue === undefined ? "-" : (kField.nValue > 99 ? "99+" : String(kField.nValue))));

		            kItem.classList.toggle("baseend", kField.bBase && arrShow[nIndex + 1]?.bBase !== true);	// 기본 필드 끝: 아래 구분선
		            kName.className		= "fieldname";
		            kName.dataset.field	= kField.sName;
		            kName.title			= `${kField.sName}(${kField.bBase ? "기본 필드" : "클라 aField"})`;

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

		        if (kButton == null || HTQuery.sQueryKey === "")
		        {
		            return;
		        }

		        OpenWindow("/query/field?" + new URLSearchParams({ sKey: HTQuery.sQueryKey, nStart: String(HTQuery.nStart), nEnd: String(HTQuery.nEnd), sField: kButton.dataset.field }), 640, 800, true);	// 지금 브라우저 창 중앙
		    }

		    static FilterField(sField, sValue)	// 필드 새 창 값 클릭 (새 창이 window.opener.HTQuery 로 호출): 지금 기간 그대로 필드:값 쿼리 (쿼리 전·쿼리 중: false)
		    {
		        return HTQuery.FilterKey(`${sField}:${sValue}`);	// 그 필드 값만 (지금 쿼리는 바꿈)
		    }

		    static OnWordClick(kEvent)	// 쿼리 결과 단어·펼친 필드 값: Ctrl+클릭 → AND 단어 (필드: 필드:값), Alt+클릭 → OR, 둘 다 → NOT (키 뗄 때 쿼리)
		    {
		        const kItem = kEvent.target.closest(".queryfield span[data-field]");	// 펼친 줄 필드 값 (Machine·Folder·Zone·Type·추가 필드)

		        if (kItem != null && (kEvent.ctrlKey || kEvent.altKey))
		        {
		            kEvent.preventDefault();

		            HTQuery.AppendWord(`${kItem.dataset.field}:${kItem.dataset.value}`, kEvent.ctrlKey && kEvent.altKey ? "NOT" : (kEvent.altKey ? "OR" : "AND"), kQueryKey);	// 다시 클릭: 뺌

		            return;
		        }

		        HTQuery.ClickWord(kEvent, "td.log, .querytext", true, kQueryKey);
		    }

		    static ClickWord(kEvent, sBox, bLead, kInput)	// sBox 칸 단어 Ctrl·Alt+클릭 → kInput 끝에 AND·OR·NOT 단어 (bLead: 맨 앞 시간 무시)
		    {
		        if (!(kEvent.ctrlKey || kEvent.altKey) || kEvent.target.closest(sBox) == null)
		        {
		            return;
		        }

		        let sWord = HTQuery.WordAt(kEvent.clientX, kEvent.clientY, sBox, bLead);	// 누른 단어

		        if (!/[^-_]/.test(sWord))	// 단어 없음 (-·_ 만)
		        {
		            return;
		        }

		        if (/^(AND|OR|NOT)$/.test(sWord))	// 연산자 글자: 소문자 단어로 (대소문자 무시)
		        {
		            sWord = sWord.toLowerCase();
		        }

		        kEvent.preventDefault();

		        HTQuery.AppendWord(sWord, kEvent.ctrlKey && kEvent.altKey ? "NOT" : (kEvent.altKey ? "OR" : "AND"), kInput);	// Ctrl: AND, Alt: OR, 둘 다: NOT
		    }

		    static AppendWord(sWord, sOp, kInput = kQueryKey)	// kInput 끝에 sOp 단어 (비움: 단어만, NOT: 쿼리칸 * NOT 단어·검색어 안 더함, 이미 있음: 뺌, 실행: OnModifierUp)
		    {
		        const sKey		= kInput.value.trim();					// 지금 칸
		        const sRemoved	= HTQuery.RemoveWord(sKey, sWord);	// 이미 있는 단어: 뺀 식 (없음: null)

		        if (sRemoved === null && sKey === "" && sOp === "NOT" && kInput !== kQueryKey)	// 검색어: 전체 (*) 없음
		        {
		            return;
		        }

		        kInput.value			= sRemoved ?? (sKey !== "" ? `${sKey} ${sOp} ${sWord}` : (sOp === "NOT" ? `* NOT ${sWord}` : sWord));	// 비움 + NOT: 그 단어 없는 전체
		        kInput.scrollLeft		= kInput.scrollWidth;	// 끝 (추가한 단어) 보이게
		        HTQuery.kWordInput		= kInput;
		        HTQuery.bWordAdded		= true;

		        if (kInput === kQueryKey)
		        {
		            HTQuery.OnQueryInput();	// 선택한 저장 쿼리와 달라지면 선택 해제
		        }
		    }

		    static RemoveWord(sKey, sWord)	// 식에서 단어 빼기 (대소문자 무시, 앞 연산자 함께, 빈 괄호·단어 하나 괄호·남은 연산자 정리, 예: A AND B OR C 에서 B → A OR C, 없음: null)
		    {
		        const arrToken	= [];	// 낱말 (단어·연산자·괄호)
		        const funcOp	= (nIndex) => /^(AND|OR|NOT)$/.test(arrToken[nIndex] ?? "");	// 연산자 자리
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
		                else if (arrToken[nIndex] === "(" && arrToken[nIndex + 1] === "(" && arrToken[HTQuery.FindClose(arrToken, nIndex + 1) + 1] === ")")	// 겹 괄호 ((A AND B))
		                {
		                    arrToken.splice(HTQuery.FindClose(arrToken, nIndex + 1) + 1, 1);
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
		        if (!HTQuery.bWordAdded || (kEvent.type === "keyup" && kEvent.key !== "Control" && kEvent.key !== "Alt"))
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

		        clearTimeout(HTQuery.nModifierTimer);

		        HTQuery.nModifierTimer = setTimeout(() =>
		        {
		            HTQuery.nModifierTimer	= 0;
		            HTQuery.bWordAdded		= false;

		            if (HTQuery.kWordInput === kKey)	// 검색어: 서치 (지금 단계 하위)
		            {
		                if (kKey.value.trim() !== "")
		                {
		                    HTSearch.SearchLog(true);
		                }
		            }
		            else if (kQueryKey.value.trim() !== "")	// 쿼리칸: 쿼리 (다 뺌: 안 함)
		            {
		                HTQuery.FilterKey(kQueryKey.value.trim());
		            }
		        }, HTQuery.MODIFIER_WAIT_MS);
		    }

		    static OnModifierDown(kEvent)	// Ctrl·Alt 누름: 기다리던 쿼리 취소 (Ctrl → Alt 로 빨리 바꿔도 이어서 더함)
		    {
		        if (kEvent.key !== "Control" && kEvent.key !== "Alt")
		        {
		            return;
		        }

		        if (HTQuery.nModifierTimer !== 0)
		        {
		            clearTimeout(HTQuery.nModifierTimer);

		            HTQuery.nModifierTimer = 0;
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

		        const sIp	= HTQuery.GetSpan(sText, nPos, /[0-9.:]/).replace(/[.:]+$/, "");	// 숫자·점·콜론 이어진 것 (끝 . : 뺌)

		        if (/^\d{1,3}(\.\d{1,3}){3}(:\d{1,5})?$/.test(sIp) || /^\d{1,2}:\d{2}(:\d{2}(\.\d{1,7})?)?$/.test(sIp))	// IP·IP:포트·시간: 통째로
		        {
		            return sIp;
		        }

		        const sDate = HTQuery.GetSpan(sText, nPos, /[0-9/]/).replace(/\/+$/, "");	// 숫자·슬래시 이어진 것 (끝 / 뺌)

		        if (/^(\d{4}\/)?\d{1,2}\/\d{1,2}$/.test(sDate))	// 날짜 (yyyy/MM/dd·MM/dd): 통째로 (yyyy-MM-dd 는 단어로 통째)
		        {
		            return sDate;
		        }

		        return HTQuery.GetSpan(sText, nPos, /[\p{L}\p{Nd}_-]/u);
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
		        const kStep = HTQuery.arrHistory[HTQuery.nHistory];	// 지금 단계 (기간)

		        if (kStep === undefined || HTQuery.bQuerying)
		        {
		            return false;
		        }

		        if (!kQueryButton.classList.contains("cur"))	// 다른 탭 보는 중: 쿼리 탭으로
		        {
		            ShowPanel("query");
		        }

		        HTQuery.SetPicker(kStart, kStartHour, kStartMinute, kStep.kFrom);	// S·E·D 도 그 단계로 (← → 와 같음)
		        HTQuery.SetPicker(kEnd, kEndHour, kEndMinute, kStep.kTo);

		        kQueryRange.selectedIndex	= kStep.nRange;
		        kQueryKey.value				= sKey;

		        HTQuery.OnQueryInput();	// 선택한 저장 쿼리와 달라지면 선택 해제
		        HTQuery.Query(sKey, kStep.kFrom, kStep.kTo, true);

		        return true;
		    }

		    static async LoadFieldWindow(kQuery)	// 필드 새 창: 주소의 쿼리·기간·필드로 값 전체 (건수 많은 순) 가져와 첫 페이지
		    {
		        const sKey		= kQuery.get("sKey") ?? "";
		        const nStart	= Number(kQuery.get("nStart") ?? 0);
		        const nEnd		= Number(kQuery.get("nEnd") ?? 0);
		        const sRange	= `"${sKey}" ${HTQuery.FormatMinute(nStart)} ~ ${HTQuery.FormatMinute(nEnd)}`;	// 쿼리·기간

		        HTQuery.sFieldName				= kQuery.get("sField") ?? "";
		        kQueryFieldPanel.hidden			= false;
		        kQueryFieldColumn.firstChild.nodeValue	= HTQuery.sFieldName;	// 칸 이름 글자만 (칸 너비 손잡이 유지)
		        kQueryFieldTitle.textContent	= `${HTQuery.sFieldName} 가져오는 중…`;
		        document.title					= `${HTQuery.sFieldName} · ${sRange} - ${document.title}`;	// 창 구분용

		        try
		        {
		            const kResponse = await fetch("/api/query/stat?" + new URLSearchParams({ sKey, nStart: String(nStart), nEnd: String(nEnd), sField: HTQuery.sFieldName }));

		            if (!kResponse.ok)
		            {
		                throw new Error(`HTTP ${kResponse.status}`);
		            }

		            const kData		= await kResponse.json();
		            const kField	= kData.arrField[0] ?? { nValue: 0, arrValue: [] };	// 그 필드 (없는 필드: 빈 목록)

		            HTQuery.arrFieldValue	= kField.arrValue;
		            HTQuery.nFieldTotal		= kData.nTotal;

		            kQueryFieldTitle.textContent = kData.sError !== "" ? `${HTQuery.sFieldName} 값 목록 실패: ${kData.sError}` : `${HTQuery.sFieldName} · 전체 ${kData.nTotal.toLocaleString()}건 · 값 ${kField.nValue.toLocaleString()}개 (${sRange})`;

		            HTQuery.ShowFieldPage(0);
		        }
		        catch (kError)	// 요청 실패
		        {
		            kQueryFieldTitle.textContent = `${HTQuery.sFieldName} 가져오기 실패: ${ErrorText(kError)}`;
		        }
		    }

		    static ShowFieldPage(nPage)	// 필드 새 창 nPage 페이지 (FIELD_PAGE 개씩, 페이지 번호 PAGE_BLOCK 개씩, 한 페이지: 숨김)
		    {
		        const nPageCount	= Math.max(1, Math.ceil(HTQuery.arrFieldValue.length / HTQuery.FIELD_PAGE));	// 전체 페이지 수
		        const kRows			= document.createDocumentFragment();
		        const kPager		= document.createDocumentFragment();

		        HTQuery.nFieldPage = Math.min(Math.max(0, nPage), nPageCount - 1);

		        const nFirst	= HTQuery.nFieldPage * HTQuery.FIELD_PAGE;								// 이 페이지 첫 값
		        const nBlock	= Math.floor(HTQuery.nFieldPage / HTQuery.PAGE_BLOCK) * HTQuery.PAGE_BLOCK;	// 이 묶음 첫 페이지
		        const nLast		= Math.min(nPageCount, nBlock + HTQuery.PAGE_BLOCK);						// 이 묶음 끝 (미포함)

		        HTQuery.arrFieldValue.slice(nFirst, nFirst + HTQuery.FIELD_PAGE).forEach((kValue, nIndex) =>
		        {
		            const kRow = document.createElement("tr");

		            kRow.className		= "row";
		            kRow.dataset.value	= kValue.sValue;
		            kRow.title			= `${(kValue.nCount * 100 / Math.max(1, HTQuery.nFieldTotal)).toFixed(1)}% · ${HTQuery.sFieldName}:${kValue.sValue}`;

		            AddCell(kRow, (nFirst + nIndex + 1).toLocaleString(), "num");
		            AddCell(kRow, kValue.sValue, "log");
		            AddCell(kRow, kValue.nCount.toLocaleString(), "nowrap");	// Count: 왼쪽

		            kRows.append(kRow);
		        });

		        const funcAdd = (sText, nTarget, sTitle) =>	// 페이지 버튼 1개
		        {
		            const kButton = document.createElement("button");

		            kButton.type		= "button";
		            kButton.textContent	= sText;
		            kButton.title		= sTitle;

		            if (sText === String(HTQuery.nFieldPage + 1))
		            {
		                kButton.className = "cur";
		            }

		            kButton.addEventListener("click", () => HTQuery.ShowFieldPage(nTarget));

		            kPager.append(kButton);
		        };

		        if (nBlock > 0)
		        {
		            funcAdd("‹", nBlock - 1, "이전 페이지 묶음");
		        }

		        for (let nTarget = nBlock; nTarget < nLast; ++nTarget)
		        {
		            funcAdd(String(nTarget + 1), nTarget, `${(nTarget * HTQuery.FIELD_PAGE + 1).toLocaleString()} ~ ${Math.min(HTQuery.arrFieldValue.length, (nTarget + 1) * HTQuery.FIELD_PAGE).toLocaleString()}`);
		        }

		        if (nLast < nPageCount)
		        {
		            funcAdd("›", nLast, "다음 페이지 묶음");
		        }

		        kQueryFieldBody.replaceChildren(kRows);
		        kQueryFieldPager.replaceChildren(kPager);

		        kQueryFieldPager.hidden		= nPageCount <= 1;
		        kQueryFieldScroll.scrollTop	= 0;
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
		            if (window.opener?.HTQuery?.FilterField?.(HTQuery.sFieldName, kRow.dataset.value) === true)
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

		window.HTQuery = HTQuery;	// 필드 새 창에서 호출 (class 전역은 window 속성이 아님)
		window.HTSearch = HTSearch;	// Ctrl·Alt+클릭 새 창·새 탭이 연 창의 서치 단계 복사 (GetOpenerState)

		kStart.addEventListener("change", () => FixRange(true));
		kStartHour.addEventListener("change", () => FixRange(true));
		kEnd.addEventListener("change", () => FixRange(false));
		kEndHour.addEventListener("change", () => FixRange(false));
		kStartMinute.addEventListener("change", () => FixRange(true));	// 쿼리 모드: D 해제
		kEndMinute.addEventListener("change", () => FixRange(false));
		kQueryRange.addEventListener("change", HTQuery.SetRecent);	// D 고름: S·E 를 지금까지로
		kQueryPrev.addEventListener("click", () => (kResultPanel.classList.contains("query") ? HTQuery : HTSearch).GoHistory(-1));	// ← 뒤로 (보는 1번 창 들어온 단계)
		kQueryNext.addEventListener("click", () => (kResultPanel.classList.contains("query") ? HTQuery : HTSearch).GoHistory(1));	// → 앞으로
		kQueryDepth.addEventListener("click", HTQuery.EditHistory);	// 가운데 단계 누름: 숫자 입력 → 그 단계로
		kQueryBody.addEventListener("click", HTQuery.ToggleDetail);			// 쿼리 결과 줄 클릭: 아래로 펼침·접음

		window.addEventListener("resize", HTQuery.FitLogHeight);	// 브라우저 창 크기: 쿼리 2번 창 로그 표 높이 다시 (맨 아래 페이지 번호까지 한 화면)
		window.addEventListener("resize", HTSearch.FitViewHeight);	// 브라우저 창 크기: 서치 2번 창 높이 다시
		window.addEventListener("resize", HTDump.FitDumpHeight);	// 브라우저 창 크기: 덤프 1번 창 높이 다시
		kQueryScroll.addEventListener("scroll", () =>	// 읽은 줄 끝 가까이: 다음 줄 이어서
		{
		    if (HTQuery.IsNearRowEnd())
		    {
		        HTQuery.LoadMore();
		    }
		});
		kQueryBody.addEventListener("click", HTQuery.OnWordClick);			// 쿼리 결과 Log 단어 Ctrl·Alt+클릭: 쿼리칸에 단어

		kQueryBody.addEventListener("mousedown", (kEvent) =>	// 쿼리 결과 Log 단어·펼친 필드 값 Ctrl·Alt+클릭: 글자 고르기 막음 (단어 추가만)
		{
		    if ((kEvent.ctrlKey || kEvent.altKey) && kEvent.target.closest("td.log, .querytext, .queryfield span[data-field]") != null)
		    {
		        kEvent.preventDefault();
		    }
		});

		document.addEventListener("mousedown", OnShiftFileDown, true);	// Shift+누름 파일: 글자 고르기 막음
		document.addEventListener("click", OnShiftFileClick, true);		// Shift+클릭 파일: 다운 (원래 처리보다 먼저)
		document.addEventListener("mousedown", OnShiftMouseDown, true);	// Shift+누름: 쿼리 ↔ 서치 옮겨 실행 (Ctrl·Alt 새 창보다 먼저)
		document.addEventListener("click", OnShiftClick, true);			// 옮겨 실행한 뒤 클릭: 막음
		document.addEventListener("mousedown", OnOpenMouseDown, true);	// Ctrl·Alt+누름 (새 창 대상): 포커스·드롭 박스 막음 (원래 처리보다 먼저)
		document.addEventListener("click", OnOpenClick, true);			// Ctrl+클릭: 새 창, Alt+클릭: 새 탭 (원래 처리보다 먼저)
		document.addEventListener("pointerdown", (kEvent) => kSelectTable = kEvent.target.closest?.("table") ?? null, true);	// 누른 표 (표 밖: 없음)
		document.addEventListener("keydown", OnTableSelectAll);			// Ctrl+A: 누른 표 전체 (지정 없음: 맨 위 표)
		document.addEventListener("keydown", OnTabKey);					// Tab: 오른쪽 탭 (힌트 제외, 맨 끝: 처음)
		document.addEventListener("keyup", HTQuery.OnModifierUp);	// Ctrl·Alt 뗌: MODIFIER_WAIT_MS 뒤 쿼리·서치
		document.addEventListener("keydown", HTQuery.OnModifierDown);	// Ctrl·Alt 다시 누름: 기다리던 쿼리 취소 (이어서 더함)
		window.addEventListener("blur", HTQuery.OnModifierUp);		// Ctrl·Alt 누른 채 창 벗어남: 같게

		kQueryFieldList.addEventListener("click", HTQuery.OnFieldClick);	// 왼쪽 필드: 값 목록 새 창
		kQueryFieldBody.addEventListener("click", HTQuery.OnFieldWindowClick);	// 필드 새 창 값: 연 창 쿼리에 AND 필드:값

		kMachineBox.addEventListener("click", OnMachineBox);
		kMachineList.addEventListener("change", OnMachineCheck);
		kMachineAll.addEventListener("change", OnMachineAll);
		document.addEventListener("click", OnMachineOutside);
		document.addEventListener("keydown", OnMachineOutside);
		kSearchButton.addEventListener("click", () => HTSearch.SearchLog());	// 서치 버튼: 서치만 (쿼리와 별개)
		kQueryRunButton.addEventListener("click", () => HTQuery.RunQuery());	// 쿼리 버튼: 쿼리만 (서치와 별개)
		kQueryStopButton.addEventListener("click", HTQuery.StopQuery);			// 쿼리 중단 버튼: 쿼리·통계 요청 끊음
		kQueryEditList.addEventListener("click", HTQuery.OnSavedClick);		// 쿼리 설정 줄 버튼: 수정·삭제
		kQueryEditList.addEventListener("dblclick", HTQuery.OnSavedClick);		// 쿼리 설정 제목 더블클릭: 바로 쿼리
		kQueryEditAdd.addEventListener("click", () => HTQuery.bAlarm ? HTQuery.OpenAlarm(null) : HTQuery.OpenSaved(null));	// 쿼리 설정 추가: 빈 추가 창 (알람: 기본값)
		kQuerySettingQuery.addEventListener("click", () => HTQuery.ShowSettingTab(false));	// 쿼리 설정 쿼리 탭
		kQuerySettingAlarm.addEventListener("click", () => HTQuery.ShowSettingTab(true));	// 쿼리 설정 알람 탭
		kQueryEditScroll.addEventListener("dragstart", HTQuery.OnEditDragStart);	// 쿼리 설정 줄 드래그: 순서 바꿈
		kQueryEditScroll.addEventListener("dragend", HTQuery.OnEditDragEnd);

		kQueryEditScroll.addEventListener("dragover", HTQuery.OnEditDragOver);	// 놓을 곳: 줄 상자 (밖으로: 표시 지움)
		kQueryEditScroll.addEventListener("drop", HTQuery.OnEditDrop);
		kQueryEditScroll.addEventListener("dragleave", (kEvent) =>
		{
		    if (!kQueryEditScroll.contains(kEvent.relatedTarget))
		    {
		        HTQuery.ClearDrop();
		    }
		});

		document.getElementById("QuerySaveOk").addEventListener("click", HTQuery.SaveSavedOk);		// 쿼리 추가·수정 창 저장
		document.getElementById("QuerySaveCancel").addEventListener("click", () => kQuerySaveDialog.close());

		kQuerySaveDialog.addEventListener("keydown", (kEvent) =>	// 쿼리 추가·수정 창: 제목 Enter·쿼리 Ctrl+Enter 저장 (한글 조합 중 제외)
		{
		    if (kEvent.key === "Enter" && !kEvent.isComposing && kEvent.target.tagName !== "BUTTON" && (kEvent.target.tagName !== "TEXTAREA" || kEvent.ctrlKey))
		    {
		        kEvent.preventDefault();

		        if (kEvent.target === kAlarmMail)	// 알람 메일·SMS 입력칸: 추가
		        {
		            HTQuery.AddAlarmMail();
		        }
		        else if (kEvent.target === kAlarmSMS)
		        {
		            HTQuery.AddAlarmSMS();
		        }
		        else
		        {
		            HTQuery.SaveSavedOk();
		        }
		    }
		});
		document.getElementById("AlarmTest").addEventListener("click", HTQuery.TestAlarm);		// 알람 테스트 발송
		document.getElementById("AlarmMailAdd").addEventListener("click", HTQuery.AddAlarmMail);	// 알람 메일 추가
		document.getElementById("AlarmSMSAdd").addEventListener("click", HTQuery.AddAlarmSMS);		// 알람 SMS 추가
		kQueryKey.addEventListener("input", HTQuery.OnQueryInput);				// 쿼리칸 고침: 선택한 저장 쿼리와 달라지면 선택 해제
		kStopButton.addEventListener("click", HTSearch.StopSearch);
		kSettingButton.addEventListener("click", OnSettingButton);	// 설정 버튼 (쿼리 모드: 쿼리 설정)
		document.getElementById("SettingSave").addEventListener("click", SaveSetting);
		document.getElementById("SettingCancel").addEventListener("click", () => kSettingDialog.close());
		document.getElementById("QuerySettingCancel").addEventListener("click", () => kQuerySettingDialog.close());
		document.getElementById("MachineMaxHint").textContent	= `개 (1 ~ ${MACHINE_LIMIT.toLocaleString()}, 기본 ${MACHINE_DEFAULT.toLocaleString()})`;
		document.getElementById("ResultMaxHint").textContent	= `개 (1 ~ ${RESULT_LIMIT.toLocaleString()}, 기본 ${RESULT_DEFAULT.toLocaleString()})`;
		kMachineMaxInput.max	= MACHINE_LIMIT;
		kResultMaxInput.max		= RESULT_LIMIT;

		document.getElementById("SettingDefault").addEventListener("click", () =>	// 기본값 표시 (저장 눌러야 적용)
		{
		    kMachineMaxInput.value	= MACHINE_DEFAULT;
		    kResultMaxInput.value	= RESULT_DEFAULT;
		    kSortSelect.value		= SORT_DEFAULT;

		    ShowSortHint();
		});

		kSortSelect.addEventListener("change", ShowSortHint);	// 결과 정렬 선택: 설명 갱신 (저장 눌러야 적용)

		kSettingDialog.addEventListener("keydown", (kEvent) =>	// 설정 창 Enter: 저장 (한글 조합 중 제외)
		{
		    if (kEvent.key === "Enter" && !kEvent.isComposing && kEvent.target.tagName !== "BUTTON")
		    {
		        kEvent.preventDefault();
		        SaveSetting();
		    }
		});

		kKey.addEventListener("keydown", (kEvent) =>	// 검색어 Enter: 서치만 (한글 조합 중 Enter 제외)
		{
		    if (kEvent.key === "Enter" && !kEvent.isComposing)
		    {
		        HTSearch.SearchLog();
		    }
		});

		kQueryKey.addEventListener("keydown", (kEvent) =>	// 쿼리 Enter: 쿼리만 (한글 조합 중 Enter 제외)
		{
		    if (kEvent.key === "Enter" && !kEvent.isComposing)
		    {
		        HTQuery.RunQuery();
		    }
		});

		kViewPrev.addEventListener("click", () => HTSearch.ShiftView(-1));	// ← 위로 VIEW_COUNT 줄 (위치 -1 -2 …)
		kViewNext.addEventListener("click", () => HTSearch.ShiftView(1));	// → 아래로 VIEW_COUNT 줄 (위치 1 2 …)

		kResultBody.addEventListener("click", HTSearch.OnResultClick);
		kResultBody.addEventListener("click", HTSearch.OnWordClick);	// 서치 1번 창 Log 단어 Ctrl·Alt+클릭: 검색어에 AND·OR·NOT 단어 (키 뗄 때 서치)
		kViewBody.addEventListener("click", HTSearch.OnWordClick);		// 서치 2번 창 줄 단어: 같게

		for (const kBody of [kResultBody, kViewBody])
		{
		    kBody.addEventListener("mousedown", (kEvent) =>	// 서치 1·2번 창 단어 Ctrl·Alt+클릭: 글자 고르기 막음 (단어 추가만)
		    {
		        if (HTSearch.IsWordClick(kEvent))
		        {
		            kEvent.preventDefault();
		        }
		    });
		}
		kDumpButton.addEventListener("click", HTDump.LoadDump);
		kLogButton.addEventListener("click", () => ShowPanel("log"));	// 서치 버튼: 서치 1·2번 창으로 (내용 유지)
		kQueryButton.addEventListener("click", HTQuery.LoadQuery);		// 쿼리 버튼: 쿼리 1번 창
		kFileButton.addEventListener("click", HTFile.LoadFileTree);		// 파일 버튼: 파일 1번 창 폴더 트리
		kFileTree.addEventListener("click", HTFile.OnFileTreeClick);
		document.getElementById("HelpButton").addEventListener("click", () => kHelpDialog.showModal());	// 힌트 버튼: 힌트 팝업 (탭 그대로)
		document.getElementById("HelpClose").addEventListener("click", () => kHelpDialog.close());		// 힌트 팝업 닫기

		document.getElementById("DownYes").addEventListener("click", () =>	// 다운 확인 예: 닫고 받기 (파일: 브라우저로, 덤프: zip 브라우저로)
		{
		    const funcYes = funcDownYes;	// 닫히면 지워지므로 먼저 보관

		    kDownDialog.close();

		    funcYes?.();
		});

		document.getElementById("DownNo").addEventListener("click", () => kDownDialog.close());	// 다운 확인 아니오: 닫기
		kDownDialog.addEventListener("close", () => funcDownYes = null);							// 닫힘 (예·아니오·Esc): 확인 중 아님

		document.getElementById("DeleteYes").addEventListener("click", () =>	// 삭제 확인 삭제: 닫고 삭제 (덤프 전체·덤프 하나·저장 쿼리)
		{
		    const funcYes = funcDeleteYes;	// 닫히면 지워지므로 먼저 보관

		    kDeleteDialog.close();

		    funcYes?.();
		});

		document.getElementById("DeleteNo").addEventListener("click", () => kDeleteDialog.close());	// 삭제 확인 취소: 닫기
		kDeleteDialog.addEventListener("close", () => funcDeleteYes = null);							// 닫힘 (삭제·취소·Esc): 확인 중 아님

		kFileViewScroll.addEventListener("scroll", HTFile.FillFileView);	// 파일 1번 창 오른쪽 보이는 줄이 읽은 끝 가까이·빈 높이: 이어서·그 위치로

		kFileSplitter.addEventListener("pointerdown", (kEvent) =>	// 파일 1번 창 세로선 좌우 드래그: 왼쪽 트리 폭 (최소 HTFile.FILE_SPLIT_MIN)
		{
		    const nStartX		= kEvent.clientX;													// 드래그 시작 위치
		    const nWidthStart	= kFileScroll.getBoundingClientRect().width;						// 왼쪽 시작 폭
		    const nWidthMax		= kFileSplitter.parentElement.getBoundingClientRect().width - kFileSplitter.getBoundingClientRect().width - HTFile.FILE_SPLIT_MIN;	// 왼쪽 최대 폭 (오른쪽 최소 남김)

		    const nTitleExtra	= kFileSplitter.getBoundingClientRect().width - parseFloat(getComputedStyle(kFileTitle.parentElement).columnGap);	// 머리줄 왼쪽 제목 칸 폭 (오른쪽 제목 = 오른쪽 상자 위치)

		    const funcMove = (kMove) =>	// 끈 만큼 왼쪽 폭 변경 (머리줄 왼쪽 제목 칸도 같이)
		    {
		        const nWidth = Math.min(nWidthMax, Math.max(HTFile.FILE_SPLIT_MIN, nWidthStart + kMove.clientX - nStartX));	// 왼쪽 트리 폭

		        kFileScroll.style.width	= nWidth + "px";
		        kFileTitle.style.width	= (nWidth + nTitleExtra) + "px";
		    };

		    const funcUp = () =>	// 드래그 끝: 이벤트 해제
		    {
		        kFileSplitter.classList.remove("drag");
		        kFileSplitter.removeEventListener("pointermove", funcMove);
		        kFileSplitter.removeEventListener("pointerup", funcUp);
		        kFileSplitter.removeEventListener("pointercancel", funcUp);
		    };

		    kFileSplitter.setPointerCapture(kEvent.pointerId);	// 세로선 밖으로 나가도 계속 추적
		    kFileSplitter.classList.add("drag");
		    kFileSplitter.addEventListener("pointermove", funcMove);
		    kFileSplitter.addEventListener("pointerup", funcUp);
		    kFileSplitter.addEventListener("pointercancel", funcUp);

		    kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		});
		kDumpBody.addEventListener("click", HTDump.OnDumpClick);
		kDumpDeleteAll.addEventListener("click", HTDump.DeleteAllDump);

		const kSplitter = document.getElementById("Splitter");	// 서치 1·2번 창 사이 바

		kSplitter.addEventListener("pointerdown", (kEvent) =>	// 서치 1·2번 창 사이 바 드래그: 높이 합 유지 (최소 HTSearch.SPLIT_MIN)
		{
		    const nStartY		= kEvent.clientY;										// 드래그 시작 위치
		    const nTopStart		= parseFloat(getComputedStyle(kResultScroll).height);	// 서치 1번 창 시작 높이
		    const nTotal		= nTopStart + parseFloat(getComputedStyle(kViewScroll).height);	// 두 창 높이 합 (유지)

		    const funcMove = (kMove) =>	// 끈 만큼 서치 1번 창 높이 변경, 서치 2번 창은 나머지
		    {
		        const nTop = Math.min(Math.max(HTSearch.SPLIT_MIN, nTopStart + kMove.clientY - nStartY), nTotal - HTSearch.SPLIT_MIN);	// 서치 1번 창 높이

		        kResultScroll.style.height	= nTop + "px";
		        kViewScroll.style.height	= (nTotal - nTop) + "px";
		        HTSearch.sResultHeight		= kResultScroll.style.height;	// 드래그한 높이 = 기준 높이

		        HTSearch.FitViewHeight();
		    };

		    const funcUp = () =>	// 드래그 끝: 이벤트 해제
		    {
		        kSplitter.classList.remove("drag");
		        kSplitter.removeEventListener("pointermove", funcMove);
		        kSplitter.removeEventListener("pointerup", funcUp);
		        kSplitter.removeEventListener("pointercancel", funcUp);
		    };

		    kSplitter.setPointerCapture(kEvent.pointerId);	// 바 밖으로 나가도 계속 추적
		    kSplitter.classList.add("drag");
		    kSplitter.addEventListener("pointermove", funcMove);
		    kSplitter.addEventListener("pointerup", funcUp);
		    kSplitter.addEventListener("pointercancel", funcUp);

		    kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		});

		kQueryFieldSplitter.addEventListener("pointerdown", (kEvent) =>	// 필드·로그 사이 세로선 좌우 드래그: 왼쪽 필드 폭 (최소 HTQuery.FIELD_SPLIT_MIN)
		{
		    const nStartX		= kEvent.clientX;																	// 드래그 시작 위치
		    const nWidthStart	= kQueryField.getBoundingClientRect().width;										// 왼쪽 시작 폭
		    const nWidthMax		= kQuerySplit.getBoundingClientRect().width - kQueryFieldSplitter.getBoundingClientRect().width - HTQuery.FIELD_SPLIT_MIN;	// 왼쪽 최대 폭 (오른쪽 최소 남김)

		    const funcMove = (kMove) =>	// 끈 만큼 왼쪽 폭 변경 (세로선·오른쪽 로그는 CSS 변수로 따라옴)
		    {
		        const nWidth = Math.min(nWidthMax, Math.max(HTQuery.FIELD_SPLIT_MIN, nWidthStart + kMove.clientX - nStartX));	// 왼쪽 필드 폭

		        kQuerySplit.style.setProperty("--fieldWidth", nWidth + "px");
		    };

		    const funcUp = () =>	// 드래그 끝: 이벤트 해제
		    {
		        kQueryFieldSplitter.classList.remove("drag");
		        kQueryFieldSplitter.removeEventListener("pointermove", funcMove);
		        kQueryFieldSplitter.removeEventListener("pointerup", funcUp);
		        kQueryFieldSplitter.removeEventListener("pointercancel", funcUp);
		    };

		    kQueryFieldSplitter.setPointerCapture(kEvent.pointerId);	// 세로선 밖으로 나가도 계속 추적
		    kQueryFieldSplitter.classList.add("drag");
		    kQueryFieldSplitter.addEventListener("pointermove", funcMove);
		    kQueryFieldSplitter.addEventListener("pointerup", funcUp);
		    kQueryFieldSplitter.addEventListener("pointercancel", funcUp);

		    kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		});

		kFilterSplitter.addEventListener("pointerdown", (kEvent) =>	// 명령 창·결과 창 사이 바 드래그: 두 창 높이 합 유지 (명령 창: 원래 높이 이상·늘면 아래 빈칸, 결과 창: 서치 결과·쿼리 막대 최소 이상, 아래 창: 나머지)
		{
		    const bQuery		= kResultPanel.classList.contains("query");									// 쿼리 모드: 결과 창 = 막대
		    const nStartY		= kEvent.clientY;														// 드래그 시작 위치
		    const nFilterStart	= kFilterPanel.getBoundingClientRect().height;							// 명령 창 시작 높이
		    const nFilterFrame	= nFilterStart - parseFloat(getComputedStyle(kFilterPanel).height);		// 명령 창 여백·테두리 (min-height: 안쪽만)
		    const sMinHeight	= kFilterPanel.style.minHeight;											// 지금 늘린 높이

		    kFilterPanel.style.minHeight = "";

		    const nFilterMin	= kFilterPanel.getBoundingClientRect().height;							// 명령 창 원래 높이
		    const nBoxStart		= bQuery ? (parseFloat(getComputedStyle(kQueryChart).getPropertyValue("--chartHeight")) || 120) : parseFloat(getComputedStyle(kResultScroll).height);	// 결과 창 시작 높이
		    const nBoxMin		= bQuery ? HTQuery.CHART_MIN : HTSearch.SPLIT_MIN;						// 결과 창 최소 높이

		    kFilterPanel.style.minHeight = sMinHeight;

		    const funcMove = (kMove) =>	// 끈 만큼 명령 창 높이 변경, 결과 창은 반대로
		    {
		        const nFilter	= Math.min(Math.max(nFilterMin, nFilterStart + kMove.clientY - nStartY), nFilterStart + nBoxStart - nBoxMin);	// 명령 창 높이
		        const nBox		= nBoxStart - (nFilter - nFilterStart);												// 결과 창 높이

		        kFilterPanel.style.minHeight = nFilter > nFilterMin ? (nFilter - nFilterFrame) + "px" : "";

		        if (bQuery)
		        {
		            kQueryChart.style.setProperty("--chartHeight", nBox + "px");

		            HTQuery.FitLogHeight();
		        }
		        else
		        {
		            kResultScroll.style.height	= nBox + "px";
		            HTSearch.sResultHeight		= kResultScroll.style.height;	// 끈 높이 = 기준 높이

		            HTSearch.FitViewHeight();
		        }
		    };

		    const funcUp = () =>	// 드래그 끝: 이벤트 해제
		    {
		        kFilterSplitter.classList.remove("drag");
		        kFilterSplitter.removeEventListener("pointermove", funcMove);
		        kFilterSplitter.removeEventListener("pointerup", funcUp);
		        kFilterSplitter.removeEventListener("pointercancel", funcUp);
		    };

		    kFilterSplitter.setPointerCapture(kEvent.pointerId);	// 바 밖으로 나가도 계속 추적
		    kFilterSplitter.classList.add("drag");
		    kFilterSplitter.addEventListener("pointermove", funcMove);
		    kFilterSplitter.addEventListener("pointerup", funcUp);
		    kFilterSplitter.addEventListener("pointercancel", funcUp);

		    kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		});

		kQuerySplitter.addEventListener("pointerdown", (kEvent) =>	// 쿼리 1·2번 창 사이 바 드래그: 막대 + 로그 높이 합 유지 (최소 CHART_MIN·SPLIT_MIN)
		{
		    const nStartY		= kEvent.clientY;																	// 드래그 시작 위치
		    const nChartStart	= parseFloat(getComputedStyle(kQueryChart).getPropertyValue("--chartHeight")) || 120;	// 막대 시작 높이
		    const nTotal		= nChartStart + parseFloat(getComputedStyle(kQueryScroll).height);					// 막대 + 로그 표 높이 합 (유지)

		    const funcMove = (kMove) =>	// 끈 만큼 막대 높이 변경, 로그 표는 나머지 (첫 화면: 브라우저 창 아래 끝까지)
		    {
		        const nChart = Math.min(Math.max(HTQuery.CHART_MIN, nChartStart + kMove.clientY - nStartY), nTotal - HTSearch.SPLIT_MIN);	// 막대 높이

		        kQueryChart.style.setProperty("--chartHeight", nChart + "px");
		        kQueryScroll.style.height = (nTotal - nChart) + "px";

		        HTQuery.FitLogHeight();
		    };

		    const funcUp = () =>	// 드래그 끝: 이벤트 해제
		    {
		        kQuerySplitter.classList.remove("drag");
		        kQuerySplitter.removeEventListener("pointermove", funcMove);
		        kQuerySplitter.removeEventListener("pointerup", funcUp);
		        kQuerySplitter.removeEventListener("pointercancel", funcUp);
		    };

		    kQuerySplitter.setPointerCapture(kEvent.pointerId);	// 바 밖으로 나가도 계속 추적
		    kQuerySplitter.classList.add("drag");
		    kQuerySplitter.addEventListener("pointermove", funcMove);
		    kQuerySplitter.addEventListener("pointerup", funcUp);
		    kQuerySplitter.addEventListener("pointercancel", funcUp);

		    kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		});

		const kViewHScroll		= document.getElementById("ViewHScroll");		// 서치 2번 창 아래 좌우 바 (창 아래쪽이 화면 밖일 때 화면 맨 아래에 표시)
		const kViewHScrollSize	= document.getElementById("ViewHScrollSize");	// 좌우 바 안 폭 (좌우 이동 범위)

		kViewHScroll.addEventListener("scroll", () =>	// 좌우 바 이동 → 서치 2번 창 같이 이동
		{
		    kViewScroll.scrollLeft = kViewHScroll.scrollLeft;
		});
		kViewScroll.addEventListener("scroll", () =>	// 서치 2번 창 이동 → 좌우 바 위치, 이어 읽기 (FillView)
		{
		    kViewHScroll.scrollLeft = kViewScroll.scrollLeft;

		    HTSearch.FillView();
		});
		window.addEventListener("scroll", HTSearch.SyncViewHScroll);	// 페이지 스크롤: 창 아래쪽 보임 여부
		window.addEventListener("resize", HTSearch.SyncViewHScroll);
		new ResizeObserver(HTSearch.SyncViewHScroll).observe(kViewScroll);		// 서치 2번 창 크기 (바 드래그)
		new ResizeObserver(HTSearch.SyncViewHScroll).observe(document.body);	// 위쪽 내용 변화로 서치 2번 창 위치 이동
		new MutationObserver(HTSearch.SyncViewHScroll).observe(kViewBody, { childList: true });	// 파일·페이지 내용 바뀜

		function InitColumnGrip(kTable)	// 표 칸 너비 조절 (모든 표 공통): 칸 오른쪽 경계 손잡이 (마지막 칸 제외), 드래그·더블 클릭
		{
		    for (const kHead of [...kTable.tHead.rows[0].cells].slice(0, -1).filter(kItem => kItem.cellIndex !== GetRestColumn(kTable)))	// 마지막 칸·나머지 너비 칸 제외
		    {
		        const kGrip = document.createElement("span");

		        kGrip.className	= "colgrip";
		        kGrip.title		= "칸 너비";

		        kHead.append(kGrip);
		    }

		    kTable.tHead.addEventListener("pointerdown", (kEvent) =>	// 칸 경계 드래그: 중엔 세로선만, 놓을 때 한 번 적용 (큰 표 재배치 멈춤 방지), 최소 1글자, 차이: 나머지 칸 흡수
		    {
		        const kGrip = kEvent.target.closest(".colgrip");	// 잡은 손잡이

		        if (kGrip == null)
		        {
		            return;
		        }

		        const kHead			= kGrip.parentElement;								// 조절할 칸 제목
		        const kLastHead		= kHead.parentElement.cells[GetRestColumn(kTable)];	// 나머지 너비 칸 제목 (기본: 마지막 칸)
		        const kCol			= kTable.querySelectorAll("col")[kHead.cellIndex];	// 조절할 칸
		        const kHeadRect		= kHead.getBoundingClientRect();					// 조절할 칸 제목 위치 (세로선 기준)
		        const kScrollRect	= kTable.parentElement.getBoundingClientRect();		// 표 스크롤 영역 (세로선 위치·높이)
		        const nStartX		= kEvent.clientX;									// 드래그 시작 위치
		        const nWidthStart	= kTable.classList.contains("fixed") ? parseFloat(kCol.style.width) : kHeadRect.width;	// 칸 시작 너비 (고정 전: 지금 자동 너비 = FixColumn 이 고정할 너비)
		        const nWidthMin		= GetColumnMin(kHead);								// 칸 최소 너비 (1글자)
		        const nWidthMax		= nWidthStart + Math.max(0, kLastHead.getBoundingClientRect().width - GetColumnMin(kLastHead));	// 칸 최대 너비 (마지막 칸이 1글자 남을 때까지)
		        let nWidth			= nWidthStart;										// 놓을 때 적용할 너비

		        const funcMove = (kMove) =>	// 끈 만큼 세로선만 이동 (표는 그대로)
		        {
		            nWidth = Math.min(nWidthMax, Math.max(nWidthMin, nWidthStart + kMove.clientX - nStartX));

		            kColGuide.style.left = (kHeadRect.left + nWidth) + "px";
		        };

		        const funcUp = (kUp) =>	// 드래그 끝: 놓음 → 칸 너비 한 번 적용 (취소·안 움직임: 그대로)
		        {
		            kGrip.classList.remove("drag");
		            kGrip.removeEventListener("pointermove", funcMove);
		            kGrip.removeEventListener("pointerup", funcUp);
		            kGrip.removeEventListener("pointercancel", funcUp);

		            kColGuide.hidden = true;

		            if (kUp.type === "pointerup" && nWidth !== nWidthStart)
		            {
		                FixColumn(kTable);

		                kCol.style.width = nWidth + "px";
		            }
		        };

		        kColGuide.style.left	= (kHeadRect.left + nWidthStart) + "px";
		        kColGuide.style.top		= kScrollRect.top + "px";
		        kColGuide.style.height	= kScrollRect.height + "px";
		        kColGuide.hidden		= false;

		        kGrip.setPointerCapture(kEvent.pointerId);	// 손잡이 밖으로 나가도 계속 추적
		        kGrip.classList.add("drag");
		        kGrip.addEventListener("pointermove", funcMove);
		        kGrip.addEventListener("pointerup", funcUp);
		        kGrip.addEventListener("pointercancel", funcUp);

		        kEvent.preventDefault();	// 드래그 중 글자 선택 방지
		    });

		    kTable.tHead.addEventListener("dblclick", (kEvent) =>	// 칸 경계 더블 클릭: 자동 너비로 되돌림
		    {
		        if (kEvent.target.closest(".colgrip") != null)
		        {
		            ResetColumn(kTable);
		        }
		    });
		}

		InitColumnGrip(kResultTable);	// 서치 1번 창 (마지막 Log 칸이 나머지)
		InitColumnGrip(kDumpTable);		// 덤프 1번 창 (Folder 칸이 나머지, Ac. 맨 오른쪽 고정)
		InitColumnGrip(kQueryBody.closest("table"));		// 쿼리 2번 창 (마지막 Log 칸이 나머지)
		InitColumnGrip(kQueryFieldBody.closest("table"));	// 필드 값 목록 (마지막 Count 칸이 나머지)

		if (document.body.dataset.mode === "view")	// 새 창 (보기 모드): 쿼리의 파일·페이지만 보기
		{
		    const kQuery	= new URLSearchParams(location.search);
		    const sPath		= kQuery.get("sPath") ?? "";

		    document.title = `${sPath.slice(sPath.lastIndexOf("\\") + 1)} - ${document.title}`;	// 창 구분용: 파일명

		    const nTarget	= Number(kQuery.get("nTarget") ?? 0);	// 강조할 줄 (0: 줄 모름 → 마지막 줄부터)

		    HTSearch.OpenView(kQuery.get("sMachine") ?? "", sPath, nTarget, kQuery.get("sKey") ?? "", nTarget > 0 ? null : 0);	// 그 줄 위·아래부터 (스크롤: 이어서)
		}
		else if (document.body.dataset.mode === "field")	// 필드 값 목록 새 창 (필드 모드)
		{
		    HTQuery.LoadFieldWindow(new URLSearchParams(location.search));	// Ctrl+A: 값 표 전체 (OnTableSelectAll: 맨 위 표)
		}
		else
		{
		    const kOpenParam	= new URLSearchParams(location.search);	// Ctrl·Alt+클릭으로 열림: 지금 상태 (sTab 있음)

		    LoadLimit();
		    ShowStatus();

		    const kMachineLoad	= LoadMachine();

		    HTSearch.FitResultHeight();	// 검색 전에도 서치 1번 창 최소 줄 수 높이
		    HTQuery.LoadQuery();		// 기본 탭: 쿼리 (D 1일)

		    if (kOpenParam.has("sTab"))	// 머신 목록 받은 뒤 상태 복원 (머신 체크·파일 탭 머신)
		    {
		        kMachineLoad.then(() => RestoreState(kOpenParam));
		    }
		    else
		    {
		        HTQuery.RunQuery();	// 처음 입장: 전체 쿼리 자동 실행
		    }

		    setInterval(LoadMachine, MACHINE_REFRESH_MS);	// 접속 머신 자동 갱신 (클라 재접속·끊김 반영, M 펼칠 때도 다시 조회)
		}
		</script>
		</body>
		</html>
		""";
	}
}

class HTSearch	// 서치 탭: 로그 검색 (여러 머신 동시)·파일 보기·진행·중지 API + 보기 새 창 (페이지 쪽: IndexHtml 의 HTSearch)
{
	private const int	m_nKeyMax			= 200;	// 검색어 최대 글자
	private const int	m_nSearchMax		= 1000000;	// 머신당 검색 결과 최대 건수 상한 (= 페이지 MACHINE_LIMIT, 요청 nMax: 1 ~ 이 값)
	private const int	m_nSearchDefault	= 1000;	// 머신당 검색 결과 최대 건수 기본값 (요청 nMax 없음, = 페이지 MACHINE_DEFAULT)
	private const int	m_nSearchWaitSec	= 90;	// 검색 결과 대기 (초, 클라 검색 제한 + 여유)
	private const int	m_nViewCount		= 500;	// 보기 기본 줄 수 (요청 nCount 없음)
	private const int	m_nViewCountMax		= 2000;	// 보기 최대 줄 수

	private readonly ClientListener		m_kListener;		// LMClient TCP 수신 (HTWeb 이 넘김)

	public HTSearch(ClientListener kListener)
	{
		m_kListener = kListener;
	}

	public void MapRoute(WebApplication WebApp)	// 라우트 등록 (서치 탭)
	{
		WebApp.MapGet("/view", OnViewPage);	// 파일 보기 새 창 (보기 모드, 파일명·보기 제목 Ctrl·Alt+클릭)
		WebApp.MapPost("/api/search", OnSearch);	// 로그 검색 (여러 머신 동시, 본문 JSON)
		WebApp.MapGet("/api/view", OnView);		// 파일 보기 (머신 1대)
		WebApp.MapGet("/api/progress", OnProgress);	// 검색 진행 (sToken)
		WebApp.MapGet("/api/stop", OnStop);			// 검색 중지 (sToken)
	}

	private static IResult OnViewPage()	// 서치 2번 창 새 창 (보기 모드: 쿼리의 파일만 보기)
	{
		return HTWeb.MakePage("view");
	}

	private IResult OnSearch(SearchParam kParam, HttpContext kContext)	// 로그 검색: 머신마다 클라에 Search → 끝난 순서대로 한 줄씩 {nIndex, sMachine, sError, kResult} (NDJSON, sToken: 진행·중지)
	{
		string[]	arrMachine	= kParam.arrMachine ?? Array.Empty<string>();	// 검색할 머신 (nIndex = 이 순서)
		string		sKey		= kParam.sKey ?? "";							// 검색어
		long		nStart		= kParam.nStart ?? 0;							// 시작 yyyyMMddHH
		long		nEnd		= kParam.nEnd ?? 0;								// 종료 yyyyMMddHH
		int			nMax		= Math.Clamp(kParam.nMax ?? m_nSearchDefault, 1, m_nSearchMax);	// 머신당 최대 (페이지 설정값, 없으면 기본값)

		if (arrMachine.Length > HTWeb.m_nMachineMax)	// 머신 수 초과: 조기 반환
		{
			return Results.BadRequest($"머신 수 초과 (최대 {HTWeb.m_nMachineMax})");
		}

		bool bValid = arrMachine.All(sMachine => !string.IsNullOrEmpty(sMachine)) && !string.IsNullOrWhiteSpace(sKey) && sKey.Length <= m_nKeyMax && IsDateHour(nStart, 23) && IsDateHour(nEnd, 24) && nStart <= nEnd;	// 검색 조건 (오류: 머신마다 sError)

		return Results.Stream(async (Stream kBody) =>
		{
			if (!bValid)	// 검색 조건 오류: 머신마다 오류 줄
			{
				for (int nIndex = 0; nIndex < arrMachine.Length; ++nIndex)
				{
					await HTWeb.WriteSearchLine(kBody, nIndex, arrMachine[nIndex], "검색 조건 오류", null);
				}

				return;
			}

			ConcurrentDictionary<long, SearchTrack>? hmapTrack = m_kListener.BeginTrack(kParam.sToken);	// 진행 조회·중지 등록 (sToken 없음: null)

			using CancellationTokenRegistration kAbortReg = kContext.RequestAborted.Register(() => _ = m_kListener.Stop(kParam.sToken));	// 페이지 닫힘·새로고침·경유 끊김: 머신들 검색 중지 (게임 서버 검색이 끝까지 돌지 않게)

			try
			{
				Task<(int nIndex, byte[]? arrResult, string sError)>[] arrTask = arrMachine.Select((sMachine, nIndex) => RequestSearch(nIndex, sMachine, nStart, nEnd, sKey.Trim(), nMax, hmapTrack)).ToArray();	// 모든 머신 동시 요청

				await foreach (Task<(int nIndex, byte[]? arrResult, string sError)> kTask in Task.WhenEach(arrTask))	// 끝난 순서대로 전송 (결과 바이트는 보내고 바로 해제)
				{
					(int nIndex, byte[]? arrResult, string sError) = await kTask;

					await HTWeb.WriteSearchLine(kBody, nIndex, arrMachine[nIndex], sError, arrResult);
				}
			}
			finally	// 페이지가 받기 중단해도 해제 (남은 요청은 끝나면 결과 버림)
			{
				m_kListener.EndTrack(kParam.sToken, hmapTrack);
			}
		}, "application/x-ndjson; charset=utf-8");
	}

	private async Task<(int nIndex, byte[]? arrResult, string sError)> RequestSearch(int nIndex, string sMachine, long nStart, long nEnd, string sKey, int nMax, ConcurrentDictionary<long, SearchTrack>? hmapTrack)	// 머신 1대 검색 요청 (nIndex: 결과 순서, hmapTrack: 진행 조회·중지)
	{
		Dictionary<string, object> hmapRequest = new Dictionary<string, object>	// Search 요청 (머신마다 따로: Request 가 nReqId 추가)
		{
			["sType"]	= "Search",
			["nStart"]	= nStart,
			["nEnd"]	= nEnd,
			["sKey"]	= sKey,
			["nMax"]	= nMax,
		};

		(byte[]? arrResult, string sError) = await m_kListener.Request(sMachine, hmapRequest, m_nSearchWaitSec, hmapTrack);

		return (nIndex, arrResult, sError);
	}

	private async Task<IResult> OnView(string? sMachine, string? sPath, long? nStart, int? nCount)	// 파일 보기 (머신 1대): 클라에 View → kResult 그대로 (오류: sError)
	{
		if (string.IsNullOrEmpty(sMachine) || string.IsNullOrEmpty(sPath) || sPath.Length > 1000)
		{
			return HTWeb.MakeResult(sMachine, "보기 조건 오류", null);
		}

		Dictionary<string, object> hmapRequest = new Dictionary<string, object>	// View 요청 (경로 허용 검사는 클라에서)
		{
			["sType"]	= "View",
			["sPath"]	= sPath,
			["nStart"]	= nStart ?? 1,
			["nCount"]	= Math.Clamp(nCount ?? m_nViewCount, 1, m_nViewCountMax),
		};

		(byte[]? arrResult, string sError) = await m_kListener.Request(sMachine, hmapRequest, HTWeb.m_nViewWaitSec);

		return HTWeb.MakeResult(sMachine, sError, arrResult);
	}

	private IResult OnProgress(string? sToken)	// 검색 진행: 진행 중인 머신 합 (bFound false: 없음, nWait: 앞 검색 대기 머신 수, nFileTotal: 아는 머신만)
	{
		SearchTrack[] arrTrack = m_kListener.GetTrackList(sToken);	// 진행 중인 머신별 검색

		if (arrTrack.Length == 0)
		{
			return Results.Json(new { bFound = false });
		}

		return Results.Json(new
		{
			bFound		= true,
			nRunning	= arrTrack.Length,
			nWait		= arrTrack.Count(kTrack => kTrack.m_nFileTotal < 0),
			nFileTotal	= arrTrack.Sum(kTrack => Math.Max(0, kTrack.m_nFileTotal)),
			nFileDone	= arrTrack.Sum(kTrack => kTrack.m_nFileDone),
			nFound		= arrTrack.Sum(kTrack => kTrack.m_nFound),
		});
	}

	private async Task<IResult> OnStop(string? sToken)	// 검색 중지: 진행 중인 머신마다 Stop 전송 (클라는 그때까지 결과를 bStopped 로 보냄)
	{
		return Results.Json(new { bStopped = await m_kListener.Stop(sToken) });
	}

	private static bool IsDateHour(long nValue, int nHourMax)	// yyyyMMddHH 검사 (유효 날짜, 시간 0~nHourMax)
	{
		long nHour = nValue % 100;	// 시

		return nValue > 0 && nHour <= nHourMax && DateTime.TryParseExact((nValue / 100).ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
	}
}

class HTQuery	// 쿼리 탭: 클라 로그 감시 줄 → 레코드 원문 저장·단어·필드 세그먼트 역색인·쿼리·필드별 통계 (구조: 파일 머리, 페이지 쪽: IndexHtml 의 HTQuery)
{
	private const int						m_nIndexWordMax		= 100;				// 색인 단어 최대 글자 (초과: 색인 안 함)
	private const int						m_nFieldValueMax	= 260;				// 필드 값 최대 글자 (초과: 색인 안 함, Folder 경로 길이)
	private const int						m_nFieldKeyMax		= 32;				// 추가 필드 키 최대 글자 (클라 aField 키)
	private const int						m_nLineFieldMax		= 32;				// 줄마다 추가 필드 최대 수 (넘는 필드: 버림)
	private const int						m_nStatTop			= 20;				// 필드별 통계 값 최대 (건수 많은 순)
	private const int						m_nQueryPage		= 1000;				// 쿼리 결과 한 페이지 건수
	private const int						m_nCacheSec			= 60;				// 쿼리 결과 보관 (초, 같은 쿼리·기간 페이지 넘김은 다시 계산 안 함)
	private const int						m_nCacheMax			= 16;				// 쿼리 결과 보관 수 (여러 사용자·창, 넘으면 가장 오래된 것부터 뺌)
	private const long						m_nCacheHitMax		= 8000000;			// 쿼리 결과 보관분 맞는 줄 (줄당 8바이트) 합 최대 (넘음: 오래된 것부터 뺌 → 큰 쿼리 여러 개 메모리 점유 방지)
	private const int						m_nQueryDayMax		= 731;				// 쿼리·통계 기간 최대 (일, 시간 수만큼 배열·폴더 확인 → 잘못된 요청 하나의 서버 점유 방지)
	private const int						m_nHitKeepHour		= 2;				// 기간 X시간 이하: 맞는 줄 결과에 보관 (분별 건수·페이지 다시 안 읽음, 긴 기간: 페이지 때 그 시간만 다시 읽음)
	private const int						m_nOriginLineMax	= 64 * 1024;		// 원문 한 줄 최대 읽기 (바이트)
	private const int						m_nLinePrefix		= 24;				// 원문 앞 읽기 (바이트, 레코드 "[줄 번호]:[" 부분)
	private const int						m_nOriginTailMax	= 16 * 1024;		// 원문 뒤 읽기 최대 (바이트, 레코드 "]:[Type]{추가 필드}" 부분)
	private const int						m_nFlushSec			= 10;				// 색인 버퍼 → 세그먼트 파일 간격 (초, 받은 줄이 쿼리에 보이기까지 최대 지연)
	private const int						m_nFlushDoc			= 100000;			// 색인 버퍼 줄 수 넘으면 다음 타이머에 바로 세그먼트 파일
	private const int						m_nMergeSeg			= 8;				// 시간 폴더 세그먼트 수 넘음: 합침 (쿼리 여는 파일 수 제한, 비슷한 크기끼리 → 매번 전체 재작성 방지)
	private const int						m_nMergeIdleMin		= 10;				// 마지막 세그먼트 쓰기 후 X분 지나면 1개로 합침 (지난 시간 마무리, m_nMergeDocMax 줄 넘는 시간: 그대로)
	private const int						m_nMergeDocBytes	= 600;				// 합침 때 줄 1개 메모리 추정 (바이트, 장문 포함 평균) → 묶음 최대 줄 수
	private const int						m_nQueueLineBytes	= 1024;				// 라이트 큐 줄 1개 메모리 추정 (바이트, 장문 포함 평균) → 큐 최대 줄 수
	private static readonly long			m_nMemoryLimit		= GetAvailableMemory() / 2;	// 적재 메모리 상한: 서버 시작 때 남은 물리 메모리의 절반 (라이트 큐 1/4·합침 1/2·나머지 버퍼)
	private static readonly int				m_nMergeDocMax		= (int)Math.Clamp(m_nMemoryLimit / 2 / m_nMergeDocBytes, 200000, 100000000);	// 합침 묶음 최대 줄 수 (넘음: 세그먼트 여러 개 그대로)
	private const int						m_nMergeTier		= 4;				// 합칠 세그먼트 묶음: 끝에서부터 줄 수가 가장 작은 것의 X배 이하인 것들 (비슷한 크기)
	public const int						m_nIngestQueueWarn	= 10000;			// 라이트 큐 밀림 (스왑 때 X줄 이상 → 부하 로그 + 모든 클라에 Pause, IngestWorker)
	public const int						m_nIngestQueueResume	= 1000;			// 밀림 풀림 (모든 워커 라이트 큐가 이 줄 수 미만 → 모든 클라에 Resume: 다시 읽기·전송)
	private const int						m_nIngestPauseMinMs	= 2000;				// Pause 뒤 Resume 까지 최소 (ms, Pause·Resume 깜빡임 방지)
	public static readonly int				m_nIngestQueueMax	= (int)Math.Clamp(m_nMemoryLimit / 4 / m_nQueueLineBytes, m_nIngestQueueWarn * 2, 100000000);	// 라이트 큐 최대 줄 수 (모든 워커 합, 메모리 상한 1/4, 넘음: 수신 스레드 대기 → TCP 역압)
	private const int						m_nIngestWaitMs		= 20;				// 라이트 큐 가득: 수신 스레드 기다림 (ms)
	public const int						m_nIngestWarnSec	= 10;				// 부하 로그 간격 (초)
	public const int						m_nIngestIdleMs		= 100;				// 로직 스레드 신호 없을 때 깨어나 확인 (ms)
	public const int						m_nOriginIdleSec	= 60;				// 열어 둔 원문 파일 닫음 (마지막 쓰기 후 X초)
	public const int						m_nOriginOpenMax	= 256;				// 워커마다 열어 둘 원문 파일 최대 (넘으면 가장 오래 안 쓴 것부터 닫음)
	private const int						m_nSegBucket		= 256;				// 세그먼트 단어 사전 버킷 수 (해시 상위 바이트 → 단어 찾을 때 그 버킷 칸만 읽음)
	private const int						m_nSegEntry			= 20;				// 세그먼트 단어 사전 한 칸 바이트 (해시 8 + 포스팅 위치 4 + 건수 4 + 바이트 4)
	private const int						m_nSegField			= 14;				// 세그먼트 필드 사전 한 칸 머리 바이트 (포스팅 위치 4 + 건수 4 + 바이트 4 + 키 바이트 2, 뒤에 키 "필드\t값" UTF-8)
	private const int						m_nSegHeader		= 12 + (m_nSegBucket + 1) * 4;	// 세그먼트 머리 바이트 (버전 4 + 단어 수 4 + 필드 사전 바이트 4 + 버킷 시작 257 × 4)
	private const int						m_nSegVersion		= 2;				// 세그먼트 파일 버전 (다르면 쿼리 오류, 2: 필드 사전)
	private const int						m_nDocRecord		= 20;				// 줄 목록 (doc.bin) 한 줄 바이트 (분초 4 + 원문 파일 번호 4 + 원문 위치 8 + 원문 바이트 4)
	private const int						m_nHourMs			= 3600 * 1000;		// 1시간 (ms)
	private static readonly string[]		m_arrBaseField		= { "Machine", "Folder", "Zone", "Type" };	// 기본 필드 (Machine·Folder: 원문 앞 받은 값, Zone: 클라 Base:sZone = 원문 파일 번호, Type: 원문 뒤 원문 기준 파싱 = 첫 [] 안)
	private static readonly Regex			m_kFieldKey			= new Regex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);	// 추가 필드 키 형식 (영문 시작, 영문·숫자·_)
	private static readonly Regex			m_kExactTerm		= new Regex(@"^(\d{1,3}(\.\d{1,3}){3}(:\d{1,5})?|(\d{4}/)?\d{1,2}/\d{1,2}|\d{1,2}:\d{2}(:\d{2}(\.\d{1,7})?)?)$", RegexOptions.CultureInvariant);	// 원문 그대로 확인할 검색어: IP·IP:포트·날짜·시간 (페이지 WordAt 와 같은 형식)
	private static volatile string[]		m_arrFieldName		= Array.Empty<string>();	// 추가 필드 이름 (클라 aField 키, 클라 설정 순서 (Hello)·처음 받은 표기, File\Index\field.txt, 바꿀 때 배열 통째로)
	private static readonly Dictionary<string, string>	m_hmapFieldName	= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);	// 추가 필드 이름 (대소문자 무시) → 처음 표기 (m_kIndexLock)
	private static volatile bool			m_bFieldLoaded;						// field.txt 읽음
	private static volatile List<(string sKey, string sQuery, int nMinute)>	m_listSaved	= LoadSaved();	// 저장 쿼리 (설정 aQuery, 바꿀 때 목록 통째로)
	private static readonly object			m_kSavedLock		= new object();		// 저장 쿼리·알람 바꾸기 1건씩 (설정 파일 쓰기)
	private const int						m_nSavedKeyMax		= 50;				// 저장 쿼리·알람 제목 최대 글자
	private const int						m_nSavedQueryMax	= 1000;				// 저장 쿼리·알람 쿼리 최대 글자 (쿼리와 같음)
	private const int						m_nSavedMinuteMax	= 129600;			// 저장 쿼리 간격 최대 (분, 페이지 SAVED_MINUTE_MAX 와 같음)
	private const int						m_nSavedMinute		= 10;				// 저장 쿼리 간격 없음·오류: 기본 (분, 페이지 SAVED_MINUTE 와 같음)
	private static volatile List<AlarmItem>	m_listAlarm			= LoadAlarm();		// 알람 (설정 aAlarm, 바꿀 때 목록 통째로)
	private const int						m_nAlarmMinuteMax	= 60;				// 알람 간격 최대 (분)
	private const int						m_nAlarmDelayMax	= 1440;				// 알람 억제 최대 (분)
	private static long						m_nAlarmTestTick	= -m_nAlarmTestMs;	// 마지막 테스트 발송 (TickCount64)
	private const int						m_nAlarmTestMs		= 5000;				// 테스트 발송 최소 간격 (ms)
	private const int						m_nAlarmListMax		= 20;				// 알람 메일·SMS 최대 개수
	private static readonly object			m_kIndexLock		= new object();		// 원문 파일 번호 목록 (file.txt)·추가 필드 이름 (field.txt) 잠금 (새 파일·새 필드 때만)
	private static readonly object			m_kFlushLock		= new object();		// 세그먼트 파일 쓰기·합치기 1건씩 (타이머 스레드·종료)
	private static readonly ReaderWriterLockSlim	m_kSegmentLock	= new ReaderWriterLockSlim();	// 세그먼트 목록·열기 (읽기: 쿼리·통계) ↔ 옛 세그먼트 삭제 (쓰기: 합치기) → 열기 전 삭제 방지 (연 뒤: FileShare.Delete)
	private static readonly object			m_kCacheLock		= new object();		// 쿼리 결과 보관 잠금
	private static readonly IngestWorker[]	m_arrWorker			= Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount * 2 + Environment.ProcessorCount / 2)).Select(nIndex => new IngestWorker(nIndex)).ToArray();	// 적재 로직 스레드 (코어 수 × 2.5, 쓰기 대기 중 다른 워커 CPU 사용): 파일 (머신·경로) 해시로 담당 고정 → 같은 파일 순서대로, 다른 파일 동시
	private static readonly int[]			m_arrBacklog		= new int[m_arrWorker.Length];	// 워커마다 마지막 스왑 때 밀린 줄 수 (ReportLoad)
	public static int						m_nIngestQueueLine;					// 모든 워커 라이트 큐 줄 수 합 (Interlocked, m_nIngestQueueMax 넘으면 TryAdd false)
	private static readonly object			m_kLoadLock			= new object();		// 부하 상태 바꾸기 1건씩
	private static volatile bool			m_bIngestPaused;					// 밀림으로 클라 전송 멈춤 중 (Pause 보냄, 모든 워커 m_nIngestQueueResume 미만이면 Resume)
	private static long						m_nIngestPauseTick;					// Pause 보낸 시각 (TickCount64, m_nIngestPauseMinMs 지나야 Resume)
	private static readonly Dictionary<long, long>					m_hmapMerge		= new Dictionary<long, long>();									// 합칠 원문 시간 → 마지막 세그먼트 쓴 시각 (TickCount64, 0: 시작 때 발견, m_kFlushLock)
	private static readonly Dictionary<string, QueryResult>			m_hmapCache		= new Dictionary<string, QueryResult>(StringComparer.Ordinal);	// 쿼리 결과 보관 (기간·쿼리 → 결과, m_kCacheLock)
	private static readonly List<(string sMachine, string sPath, string sFolder, string sZone)>	m_listFile	= new List<(string sMachine, string sPath, string sFolder, string sZone)>();	// 원문 파일 번호 → 머신·클라 경로·Folder·Zone (번호 = 위치 + 1, File\Index\file.txt)
	private static readonly Dictionary<string, int>					m_hmapFileId	= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);	// 머신\t클라 경로\t존 → 원문 파일 번호 (존 바뀜: 새 번호)
	private static bool						m_bFileLoaded;						// file.txt 읽음
	private static bool						m_bIndexScanned;					// 시작 때 색인 폴더 훑음 (.tmp 삭제, 세그먼트 2개 이상인 시간 → 합칠 시간)

	static HTQuery()	// 적재 메모리 상한 출력 (서버 시작 때 남은 메모리 기준)
	{
		HTLog.Write(INFO_, $"적재 메모리 상한: 남은 {GetAvailableMemory() / 1048576:N0} MB 의 절반 {m_nMemoryLimit / 1048576:N0} MB → 라이트 큐 {m_nIngestQueueMax:N0}줄, 합침 묶음 {m_nMergeDocMax:N0}줄, 워커 {m_arrWorker.Length}개");
	}

	public void MapRoute(WebApplication WebApp)	// 라우트 등록 (쿼리 탭)
	{
		WebApp.MapGet("/query/field", OnFieldPage);			// 필드 값 목록 새 창 (필드 모드)
		WebApp.MapGet("/api/query", OnQuery);				// 쿼리 (색인 단어·필드, 최신 순 페이지)
		WebApp.MapGet("/api/query/stat", OnStat);			// 필드별 통계 (쿼리·기간 맞는 줄의 필드 값마다 건수)
		WebApp.MapGet("/api/query/field", OnFieldList);		// 필드 목록 (기본 + 추가: 클라 aField 로 받은 이름)
		WebApp.MapGet("/api/query/saved", OnSavedList);		// 저장 쿼리 목록 (설정 aQuery)
		WebApp.MapPost("/api/query/saved", OnSavedSave);	// 저장 쿼리 저장 (같은 이름: 바꿈, 본문 JSON)
		WebApp.MapPost("/api/query/saved/delete", OnSavedDelete);	// 저장 쿼리 삭제 (본문 JSON)
		WebApp.MapPost("/api/query/saved/move", OnSavedMove);		// 저장 쿼리 순서 바꿈 (본문 JSON)
		WebApp.MapGet("/api/alarm", OnAlarmList);					// 알람 목록 (설정 aAlarm)
		WebApp.MapPost("/api/alarm", OnAlarmSave);					// 알람 추가·수정 (본문 JSON)
		WebApp.MapPost("/api/alarm/delete", OnAlarmDelete);			// 알람 삭제 (본문 JSON)
		WebApp.MapPost("/api/alarm/move", OnAlarmMove);				// 알람 순서 바꿈 (본문 JSON)
		WebApp.MapPost("/api/alarm/test", OnAlarmTest);				// 알람 테스트 발송 (본문 JSON, 저장 안 함)
		WebApp.MapGet("/api/alarm/log", HTAlarmLog.OnLog);			// 알람 로그 (기간·알람·결과, 최신 순)
	}

	private static IResult OnFieldPage()	// 필드 값 목록 새 창 (필드 모드)
	{
		return HTWeb.MakePage("field");
	}

	private static IResult OnQuery(string? sKey, long? nStart, long? nEnd, int? nPage, int? nOffset, int? nCount, CancellationToken kToken)	// 쿼리: 시간 폴더마다 병렬로 포스팅만 읽어 AND·OR·NOT 정렬 병합 → 시간별·분별 건수 + nPage 페이지 원문 (m_nCacheSec 초 보관, 응답: 파일 머리, 요청 끊김: 보관 안 함)
	{
		try
		{
			long			nBegin		= Stopwatch.GetTimestamp();
			string			sQuery		= (sKey ?? "").Trim();
			int				nSkip		= Math.Clamp(nOffset ?? 0, 0, m_nQueryPage);													// 페이지 안 시작 (페이지가 스크롤로 이어 읽음)
			int				nTake		= Math.Clamp(nCount ?? m_nQueryPage, 0, m_nQueryPage - nSkip);									// 페이지 안 줄 수
			QueryResult		kResult		= GetQueryResult(sQuery, nStart ?? 0, nEnd ?? 0, (nPage ?? 0) > 0 || nSkip > 0, kToken);		// 쿼리 결과 (페이지 넘김·이어 읽기: 보관분)
			int				nCur		= Math.Clamp(nPage ?? 0, 0, Math.Max(0, (kResult.m_nTotal + m_nQueryPage - 1) / m_nQueryPage - 1));	// 지금 페이지 (범위 밖: 끝)
			string			sError		= kResult.m_sError;
			List<object>	listLine;																									// 이 페이지 줄 (원문은 이것만 읽음)

			try
			{
				listLine = ReadPage(kResult, nCur * m_nQueryPage + nSkip, nTake);
			}
			catch (Exception kException) when (kException is not OperationCanceledException)	// 원문 사용 중·잘림 등: 건수·막대는 그대로, 줄 없음
			{
				listLine	= new List<object>();
				sError		= GetErrorText(kException, "쿼리 원문 읽기");
			}

			return Results.Json(new { sKey = sQuery, nStart = nStart ?? 0, nEnd = nEnd ?? 0, nTotal = kResult.m_nTotal, nPage = nCur, nPageSize = m_nQueryPage, nOffset = nSkip, nElapsedMs = (long)Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds, sError, arrHour = kResult.m_arrHour.Select((nHour, nIndex) => new { nHour, nCount = kResult.m_arrCount[nIndex] }), arrMinute = kResult.m_listMinute, arrSecond = kResult.m_listSecond, arrLine = listLine });
		}
		catch (OperationCanceledException)	// 페이지가 요청 끊음 (중단): 응답 안 받음
		{
			return Results.Empty;
		}
	}

	private static IResult OnStat(string? sKey, long? nStart, long? nEnd, string? sField, CancellationToken kToken)	// 필드별 통계: 쿼리·기간 맞는 줄 ∩ 필드 값 포스팅 → 값마다 건수 (원문 안 읽음, sField: 그 필드 값 전체, 응답: 파일 머리)
	{
		long										nBegin		= Stopwatch.GetTimestamp();
		string										sQuery		= (sKey ?? "").Trim();
		QueryResult									kPlan		= new QueryResult("");																// 쿼리 조건 (보관 안 함)
		Dictionary<string, (string sKey, int nCount)>	hmapCount	= new Dictionary<string, (string sKey, int nCount)>(StringComparer.OrdinalIgnoreCase);	// 필드 키 (필드\t값) → 첫 표기·건수
		string										sError		= PrepareQuery(kPlan, sQuery, nStart ?? 0, nEnd ?? 0);								// 조건 오류
		int											nTotal		= 0;																				// 맞는 줄 수

		if (sError == "")
		{
			try
			{
				(sError, nTotal) = RunStat(kPlan, hmapCount, kToken);
			}
			catch (OperationCanceledException)	// 페이지가 요청 끊음 (중단·다음 쿼리): 응답 안 받음
			{
				return Results.Empty;
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

	private static IResult OnSavedList()	// 저장 쿼리 목록 → {arrQuery: [{sKey, sQuery, nMinute}]}
	{
		return Results.Json(new { arrQuery = GetSavedList() });
	}

	private static IEnumerable<object> GetSavedList()	// 저장 쿼리 응답 [{sKey, sQuery, nMinute}]
	{
		return m_listSaved.Select(kSaved => new { kSaved.sKey, kSaved.sQuery, kSaved.nMinute });
	}

	private static IResult OnSavedSave(SavedParam kParam, HttpRequest kRequest)	// 저장 쿼리 저장: 검사 → sOldKey: 그 자리 수정 (제목 겹침: 오류), 없음: 같은 제목 바꿈·끝에 추가 → 설정 aQuery (요청자 IP 로그)
	{
		string sKey		= (kParam.sKey ?? "").Trim();		// 제목
		string sQuery	= (kParam.sQuery ?? "").Trim();		// 쿼리 (비움: 저장 안 함)
		string sOldKey	= (kParam.sOldKey ?? "").Trim();	// 수정할 저장 쿼리 제목 (비움: 추가)
		int nMinute		= kParam.nMinute ?? m_nSavedMinute;	// 간격 (분)
		string sError	= sKey == "" || sKey.Length > m_nSavedKeyMax ? $"제목 없음·{m_nSavedKeyMax}자 넘음" : (sQuery == "" || sQuery.Length > m_nSavedQueryMax ? $"쿼리 없음·{m_nSavedQueryMax}자 넘음" : (!SearchNode.IsValid(sQuery) ? "쿼리 형식 오류 (예: A AND (B NOT C) OR D)" : (nMinute < 1 || nMinute > m_nSavedMinuteMax ? $"간격 1 ~ {m_nSavedMinuteMax}분" : "")));	// 오류 (제목·쿼리 모두 필요, 쿼리는 검색어 규칙 형식)

		if (sError == "")
		{
			lock (m_kSavedLock)
			{
				List<(string sKey, string sQuery, int nMinute)>	listSaved	= new List<(string sKey, string sQuery, int nMinute)>(m_listSaved);	// 바꿀 목록
				int									nSame		= listSaved.FindIndex(kSaved => string.Equals(kSaved.sKey, sKey, StringComparison.OrdinalIgnoreCase));		// 같은 제목
				int									nOld		= sOldKey == "" ? -1 : listSaved.FindIndex(kSaved => string.Equals(kSaved.sKey, sOldKey, StringComparison.OrdinalIgnoreCase));	// 수정할 자리
				string								sAction		= nOld >= 0 ? "수정" : (nSame >= 0 ? "바꿈" : "추가");	// 로그

				if (sOldKey != "" && nOld < 0)
				{
					sError = "없는 제목";
				}
				else if (nOld >= 0 && nSame >= 0 && nSame != nOld)
				{
					sError = "이미 있는 제목";
				}
				else
				{
					if (nOld >= 0)
					{
						listSaved[nOld] = (sKey, sQuery, nMinute);
					}
					else if (nSame >= 0)
					{
						listSaved[nSame] = (listSaved[nSame].sKey, sQuery, nMinute);
					}
					else
					{
						listSaved.Add((sKey, sQuery, nMinute));
					}

					sError = SaveSaved(listSaved);
				}

				if (sError == "")
				{
					HTLog.Write(NOTI_, $"저장 쿼리 {sAction}: {(nOld >= 0 && sOldKey != sKey ? sOldKey + " → " : "")}{sKey} ({sQuery}, {nMinute}분) 요청자 {HTWeb.GetAddr(kRequest.HttpContext)}");
				}
			}
		}

		return Results.Json(new { sError, arrQuery = GetSavedList() });
	}

	private static IResult OnSavedDelete(SavedParam kParam, HttpRequest kRequest)	// 저장 쿼리 삭제 → 설정 aQuery (없는 이름: 오류, 요청자 IP 로그)
	{
		string sKey		= (kParam.sKey ?? "").Trim();	// 이름
		string sError	= "";

		lock (m_kSavedLock)
		{
			List<(string sKey, string sQuery, int nMinute)> listSaved = m_listSaved.Where(kSaved => !string.Equals(kSaved.sKey, sKey, StringComparison.OrdinalIgnoreCase)).ToList();	// 지운 목록

			sError = listSaved.Count == m_listSaved.Count ? "없는 제목" : SaveSaved(listSaved);

			if (sError == "")
			{
				HTLog.Write(NOTI_, $"저장 쿼리 삭제: {sKey} 요청자 {HTWeb.GetAddr(kRequest.HttpContext)}");
			}
		}

		return Results.Json(new { sError, arrQuery = GetSavedList() });
	}

	private static string SaveSaved(List<(string sKey, string sQuery, int nMinute)> listSaved)	// 저장 쿼리 잠금 안에서: 설정 aQuery 에 [{sKey, sQuery, nMinute}] 저장 → 성공: 메모리 목록 바꿈 (반환: 오류, 없음: "")
	{
		JsonArray	kArray	= new JsonArray(listSaved.Select(kSaved => (JsonNode?)new JsonObject { ["sKey"] = kSaved.sKey, ["sQuery"] = kSaved.sQuery, ["nMinute"] = kSaved.nMinute }).ToArray());	// 설정 aQuery
		string		sError	= HTConfig.SetArray("aQuery", kArray);

		if (sError == "")
		{
			m_listSaved = listSaved;
		}

		return sError;
	}

	private static List<(string sKey, string sQuery, int nMinute)> LoadSaved()	// 설정 aQuery → 저장 쿼리 (이름 없음·너무 김·같은 이름: 출력 후 뺌, 간격 없음·범위 밖: 기본)
	{
		List<(string sKey, string sQuery, int nMinute)> listSaved = new List<(string sKey, string sQuery, int nMinute)>();	// 저장 쿼리

		foreach (IConfigurationSection kSection in HTConfig.GetChildren("aQuery"))
		{
			string sKey		= (kSection["sKey"] ?? "").Trim();
			string sQuery	= (kSection["sQuery"] ?? "").Trim();
			int nMinute		= int.TryParse(kSection["nMinute"], out int nValue) && nValue >= 1 && nValue <= m_nSavedMinuteMax ? nValue : m_nSavedMinute;	// 간격 (분)

			if (sKey == "" || sKey.Length > m_nSavedKeyMax || sQuery.Length > m_nSavedQueryMax || listSaved.Any(kSaved => string.Equals(kSaved.sKey, sKey, StringComparison.OrdinalIgnoreCase)))
			{
				HTLog.Write(WARN_, $"저장 쿼리 설정 오류: aQuery {sKey}");

				continue;
			}

			listSaved.Add((sKey, sQuery, nMinute));
		}

		return listSaved;
	}

	private static IResult OnSavedMove(SavedParam kParam, HttpRequest kRequest)	// 저장 쿼리 순서 바꿈: 그 제목 → nTo 자리 (요청자 IP 로그)
	{
		string sKey		= (kParam.sKey ?? "").Trim();	// 제목
		string sError	= "";

		lock (m_kSavedLock)
		{
			int nFrom = m_listSaved.FindIndex(kSaved => string.Equals(kSaved.sKey, sKey, StringComparison.OrdinalIgnoreCase));	// 옮길 자리

			sError = nFrom < 0 ? "없는 제목" : SaveSaved(MoveItem(m_listSaved, nFrom, kParam.nTo ?? nFrom));

			if (sError == "")
			{
				HTLog.Write(NOTI_, $"저장 쿼리 순서: {sKey} → {(kParam.nTo ?? nFrom) + 1}번 요청자 {HTWeb.GetAddr(kRequest.HttpContext)}");
			}
		}

		return Results.Json(new { sError, arrQuery = GetSavedList() });
	}

	private static List<T> MoveItem<T>(List<T> listItem, int nFrom, int nTo)	// nFrom 항목을 nTo 자리로 옮긴 새 목록 (nTo 범위 밖: 처음·끝)
	{
		List<T>	listMove	= new List<T>(listItem);
		T		kItem		= listMove[nFrom];

		listMove.RemoveAt(nFrom);
		listMove.Insert(Math.Clamp(nTo, 0, listMove.Count), kItem);

		return listMove;
	}

	public static List<AlarmItem> GetAlarmList()	// 알람 목록 (시작 때 HTAlarm)
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

	private static IResult OnAlarmList()	// 알람 목록 → {arrAlarm: [{nID, sKey, sQuery, nMinute, nCount, nDelay, aMail, sHook, aSMS, bEnable}]}
	{
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
					HTLog.Write(NOTI_, $"알람 {(nOld < 0 ? "추가" : "수정")}: #{nID} {listAlarm.First(kAlarm => kAlarm.nID == nID).sKey} 요청자 {HTWeb.GetAddr(kRequest.HttpContext)}");
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
				HTLog.Write(NOTI_, $"알람 삭제: #{nID} 요청자 {HTWeb.GetAddr(kRequest.HttpContext)}");
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
			foreach (AlarmJob kJob in HTStat.MakeTestJob(kAlarm))
			{
				listResult.Add(new { kJob.sChannel, sError = HTSend.SendNow(kJob) });
			}

			HTLog.Write(NOTI_, $"알람 테스트 발송: #{kAlarm.nID} {kAlarm.sKey} ({string.Join(", ", listResult)}) 요청자 {HTWeb.GetAddr(kRequest.HttpContext)}");
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

			sError = nFrom < 0 ? "없는 알람" : SaveAlarm(MoveItem(m_listAlarm, nFrom, kParam.nTo ?? nFrom));

			if (sError == "")
			{
				HTLog.Write(NOTI_, $"알람 순서: #{nID} → {(kParam.nTo ?? nFrom) + 1}번 요청자 {HTWeb.GetAddr(kRequest.HttpContext)}");
			}
		}

		return Results.Json(new { sError, arrAlarm = m_listAlarm });
	}

	private static AlarmItem ToAlarm(AlarmParam kParam, int nID)	// 본문 → 알람 (앞뒤 빈칸·빈 항목 뺌)
	{
		return new AlarmItem
		{
			nID		= nID,
			sKey	= (kParam.sKey ?? "").Trim(),
			sQuery	= (kParam.sQuery ?? "").Trim(),
			nMinute	= kParam.nMinute ?? 0,
			nCount	= kParam.nCount ?? 0,
			nDelay	= kParam.nDelay ?? 0,
			aMail	= (kParam.aMail ?? Array.Empty<string>()).Select(sMail => (sMail ?? "").Trim()).Where(sMail => sMail != "").ToArray(),
			sHook	= (kParam.sHook ?? "").Trim(),
			aSMS	= (kParam.aSMS ?? Array.Empty<string>()).Select(sSMS => (sSMS ?? "").Trim()).Where(sSMS => sSMS != "").ToArray(),
			bEnable	= kParam.bEnable ?? false,
		};
	}

	private static string CheckAlarm(AlarmItem kAlarm)	// 알람 값 검사 (반환: 오류, 없음: "")
	{
		string sError = "";

		if (kAlarm.sKey == "" || kAlarm.sKey.Length > m_nSavedKeyMax)
		{
			sError = $"제목 없음·{m_nSavedKeyMax}자 넘음";
		}
		else if (kAlarm.sQuery == "" || kAlarm.sQuery.Length > m_nSavedQueryMax)
		{
			sError = $"쿼리 없음·{m_nSavedQueryMax}자 넘음";
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
		else if (kAlarm.aMail.Length > m_nAlarmListMax || kAlarm.aMail.Any(sMail => !Regex.IsMatch(sMail, @"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$")))
		{
			sError = $"메일 형식 오류·{m_nAlarmListMax}개 넘음";
		}
		else if (kAlarm.sHook != "" && !(Uri.TryCreate(kAlarm.sHook, UriKind.Absolute, out Uri? kUri) && (kUri.Scheme == Uri.UriSchemeHttp || kUri.Scheme == Uri.UriSchemeHttps)))
		{
			sError = "훅 주소 오류 (http·https)";
		}
		else if (kAlarm.aSMS.Length > m_nAlarmListMax || kAlarm.aSMS.Any(sSMS => !Regex.IsMatch(sSMS, @"^\+?[0-9-]{8,20}$")))
		{
			sError = $"SMS 번호 형식 오류·{m_nAlarmListMax}개 넘음";
		}

		return sError;
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
		string		sError	= HTConfig.SetArray("aAlarm", kArray);

		if (sError == "")
		{
			m_listAlarm = listAlarm;

			HTAlarm.SetAlarm(listAlarm);	// 매칭 스냅샷 교체 (다음 줄부터)
		}

		return sError;
	}

	private static List<AlarmItem> LoadAlarm()	// 설정 aAlarm → 알람 (값 오류·ID 없음·중복: 출력 후 뺌)
	{
		List<AlarmItem> listAlarm = new List<AlarmItem>();

		foreach (IConfigurationSection kSection in HTConfig.GetChildren("aAlarm"))
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
				HTLog.Write(WARN_, $"알람 설정 오류: aAlarm #{kAlarm.nID} {kAlarm.sKey} ({sError})");

				continue;
			}

			listAlarm.Add(kAlarm);
		}

		return listAlarm;
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
			foreach (string sOld in m_hmapCache.Where(kPair => (DateTime.UtcNow - kPair.Value.m_kTime).TotalSeconds >= m_nCacheSec).Select(kPair => kPair.Key).ToList())	// 오래된 보관분 정리
			{
				m_hmapCache.Remove(sOld);
			}

			while (m_hmapCache.Count > 0 && (m_hmapCache.Count >= m_nCacheMax || m_hmapCache.Values.Sum(kItem => kItem.m_hmapHit.Values.Sum(arrHit => (long)arrHit.Length)) + kResult.m_hmapHit.Values.Sum(arrHit => (long)arrHit.Length) > m_nCacheHitMax))	// 가득 참·맞는 줄 메모리 넘음: 가장 오래된 것부터 뺌
			{
				m_hmapCache.Remove(m_hmapCache.MinBy(kPair => kPair.Value.m_kTime).Key);
			}

			m_hmapCache[sCacheKey] = kResult;
		}

		return kResult;
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
			return "기간 초과 (최대 2년)";
		}

		HashSet<string> setWord		= new HashSet<string>(StringComparer.Ordinal);				// 쿼리 단어 모두 (읽을 포스팅)
		HashSet<string> setField	= new HashSet<string>(StringComparer.OrdinalIgnoreCase);	// 쿼리 필드 키 모두 (필드\t값)

		CollectTerm(kRoot, setWord, setField);

		kResult.m_kRoot	= kRoot;
		kResult.m_kFrom	= kFrom;
		kResult.m_kTo	= kTo;
		kResult.m_bAll	= HasAll(kRoot);
		kResult.m_bExact	= HasExact(kRoot);

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

		Parallel.For(0, nHourCount, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = kToken }, nIndex =>	// 시간 폴더마다 병렬 (없는 시간: 0건, 중단: 남은 시간 안 읽음)
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
					kResult.m_arrCount[nIndex] = ReadHourDoc(kResult, sHourDir, nMsFrom, nMsTo).Length;

					return;
				}

				(int nMs, int nDoc)[] arrHit = ReadHourHit(kResult, sHourDir, nMsFrom, nMsTo);	// 맞는 줄 (분초·줄 번호)

				kResult.m_arrCount[nIndex] = arrHit.Length;
				kResult.m_hmapHit[nHour] = arrHit;	// 짧은 기간: 분별 건수·페이지에 다시 씀
			}
			catch (Exception kException)	// 사용 중·권한 없음·형식 오류 (첫 오류만)
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

	private static (string sError, int nTotal) RunStat(QueryResult kPlan, Dictionary<string, (string sKey, int nCount)> hmapCount, CancellationToken kToken)	// 통계 계산: 기간 시간 폴더마다 병렬로 맞는 줄 → 필드 값마다 맞는 줄 수 → hmapCount 에 더함 (반환: 첫 오류·맞는 줄 수, 중단: OperationCanceledException)
	{
		DateTime	kHourFirst	= kPlan.m_kFrom.Date.AddHours(kPlan.m_kFrom.Hour);					// 기간 첫 시간 (정각)
		int			nHourCount	= (int)Math.Ceiling((kPlan.m_kTo - kHourFirst).TotalHours);			// 기간 시간 수 (끝 미포함)
		object		kLock		= new object();														// 합치기 잠금
		string		sError		= "";																// 첫 오류
		int			nTotal		= 0;																// 맞는 줄 수

		Parallel.For(0, nHourCount, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = kToken }, nIndex =>	// 시간 폴더마다 병렬 (중단: 남은 시간 안 읽음)
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

				int[] arrDoc = ReadHourDoc(kPlan, sHourDir, nMsFrom, nMsTo);	// 맞는 줄 번호

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
			if (kToken.IsCancellationRequested)	// 중단: 남은 세그먼트 안 읽음 (Parallel.For 가 중단으로 끝냄)
			{
				return;
			}

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

	private static (int nMs, int nDoc)[] ReadHourHit(QueryResult kResult, string sHourDir, int nMsFrom, int nMsTo)	// 시간 폴더 하나 맞는 줄 (분초·줄 번호): 포스팅 조건 계산 → doc.bin 분초로 기간 안 (nMsFrom 포함·nMsTo 미포함)
	{
		int[] arrDoc = EvalHour(kResult, sHourDir);	// 맞는 줄 번호

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
			if (TryGetDoc(arrTable, nDoc, out int nMs, out _, out _, out _) && nMs >= nMsFrom && nMs < nMsTo)
			{
				listHit.Add((nMs, nDoc));
			}
		}

		return listHit.ToArray();
	}

	private static int[] ReadHourDoc(QueryResult kPlan, string sHourDir, int nMsFrom, int nMsTo)	// 시간 폴더 하나 맞는 줄 번호 (오름차순, 시간 전체: 줄 목록 안 읽음, 기간 양끝 시간: 줄 목록 분초로 기간 안만)
	{
		int[] arrDoc = EvalHour(kPlan, sHourDir);	// 맞는 줄 번호

		if (arrDoc.Length == 0 || (nMsFrom == 0 && nMsTo == m_nHourMs))
		{
			return arrDoc;
		}

		byte[] arrTable = ReadDocTable(sHourDir);	// 줄 목록

		return arrDoc.Where(nDoc => TryGetDoc(arrTable, nDoc, out int nMs, out _, out _, out _) && nMs >= nMsFrom && nMs < nMsTo).ToArray();
	}

	private static int[] EvalHour(QueryResult kResult, string sHourDir)	// 시간 폴더 하나 조건 계산 (IP·날짜·시간 검색어: 후보 줄 원문 글자 확인, 원문 파일: 이 시간 안에서만 열고 닫음)
	{
		Dictionary<int, FileStream> hmapStream = new Dictionary<int, FileStream>();	// 원문 파일 번호 → 연 원문 파일

		try
		{
			return EvalQuery(kResult.m_kRoot!, ReadHourPosting(kResult, sHourDir), kResult.m_bExact ? MakeTextReader(sHourDir, hmapStream) : null);
		}
		finally
		{
			foreach (FileStream kStream in hmapStream.Values)
			{
				kStream.Dispose();
			}
		}
	}

	private static Func<int, string?> MakeTextReader(string sHourDir, Dictionary<int, FileStream> hmapStream)	// 시간 폴더 줄 번호 → 원문에서 앞 시간 뺀 내용 (색인과 같은 범위, 줄 목록은 처음 쓸 때 1번 읽음, 없음: null)
	{
		byte[]? arrTable = null;	// 줄 목록

		return nDoc =>
		{
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

	private static Dictionary<string, int[]> ReadHourPosting(QueryResult kResult, string sHourDir)	// 쿼리 단어·필드마다 포스팅: 세그먼트마다 사전에서 찾아 세그먼트 순 이어 붙임 (범위 안 겹침 → 줄 번호 오름차순, 없음: 빈 배열)
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
			throw new InvalidDataException($"세그먼트 버전 오류 (File\\Index 지우고 다시 쌓기): {kStream.Name}");
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

	private static int[] EvalQuery(SearchNode kNode, Dictionary<string, int[]> hmapPost, Func<int, string?>? funcText)	// 조건 → 맞는 줄 번호 (오름차순): 검색어: 필드·단어 포스팅 교집합 (IP·날짜·시간: 원문 글자 확인), AND 교집합, OR 합집합, NOT 차집합 (정렬 배열 병합)
	{
		switch (kNode.m_eOp)
		{
			case SEARCH_OP.SEARCH_OP_AND:
			{
				return And(EvalQuery(kNode.m_kLeft!, hmapPost, funcText), EvalQuery(kNode.m_kRight!, hmapPost, funcText));
			}

			case SEARCH_OP.SEARCH_OP_OR:
			{
				return Or(EvalQuery(kNode.m_kLeft!, hmapPost, funcText), EvalQuery(kNode.m_kRight!, hmapPost, funcText));
			}

			case SEARCH_OP.SEARCH_OP_NOT:
			{
				return Not(EvalQuery(kNode.m_kLeft!, hmapPost, funcText), EvalQuery(kNode.m_kRight!, hmapPost, funcText));
			}

			default:	// 검색어: * → 모든 줄, 필드:값 → 필드 포스팅, 그 외 → 단어 포스팅 교집합 (짧은 것부터)
			{
				if (IsAll(kNode))
				{
					return hmapPost.GetValueOrDefault("*") ?? Array.Empty<int>();
				}

				if (TryGetFieldKey(kNode.m_sTerm, out string sFieldKey))
				{
					return hmapPost.GetValueOrDefault(sFieldKey) ?? Array.Empty<int>();
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
			else
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

	private static List<(string sName, bool bBase)> GetFieldList()	// 필드 목록 (기본 Machine·Folder·Zone·Type → 추가 필드 클라 aField 순, bBase: 기본 필드)
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

	private static List<object> ReadPage(QueryResult kResult, int nSkip, int nCount)	// 최신 순 nSkip 번째부터 nCount 줄: 그 시간 맞는 줄 (없음: 그 시간만 다시 읽음) → 원문 파일 번호·위치로 원문만 읽음 (없음: "(원문 없음)") + 줄 필드
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
			(int nMs, int nDoc)[]	arrHit	= GetHourHit(kResult, nHour);	// 맞는 줄 (최신 순)

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
				if (!hmapDoc.TryGetValue(nHour, out FileStream? kDoc))	// 처음 보는 시간: 줄 목록 열기
				{
					hmapDoc[nHour] = kDoc = OpenDocTable(GetHourDir(nHour));
				}

				ReadDocRecord(kDoc, nDoc, out _, out int nFileId, out long nOffset, out int nLength);

				(string sMachine, string sPath, string sFolder, string sZone)	= GetFileInfo(nFileId);
				string?											sText	= ReadOrigin(hmapStream, nFileId, sMachine, sPath, nOffset, nLength, out long nLine);	// 원문 (레코드의 [원문] 안, 받은 줄 그대로), 클라 파일 줄 번호 (예전 레코드: 0)
				DateTime										kTime	= FromHour(nHour).AddMilliseconds(nMs);									// 원문 시간
				(string sKey, string sValue)[]					arrField	= sText == null ? [] : ReadOriginField(hmapStream, nFileId, nOffset + nLength);	// 추가 필드 (원문 뒤, 클라 aField)

				listLine.Add(new { sTime = kTime.ToString("[yyyy/MM/dd HH:mm:ss.fff]", CultureInfo.InvariantCulture), sMachine, sPath, sFolder, sText = sText == null ? "(원문 없음)" : GetLogText(sText), sOrigin = sText ?? "(원문 없음)", nLine, arrField = GetLineField(sMachine, sFolder, sZone, GetLineType(sText ?? ""), arrField) });	// 표시: Log 칸·펼친 줄은 원문 그대로 (sOrigin), Line 칸: 클라 파일 줄 번호 (0: 모름)
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

	private static List<object> GetLineField(string sMachine, string sFolder, string sZone, string sType, (string sKey, string sValue)[] arrField)	// 줄 필드 [{sName, sValue}] (Machine·Folder·Zone·Type·추가 필드 순)
	{
		List<object> listField = new List<object> { new { sName = "Machine", sValue = sMachine }, new { sName = "Folder", sValue = sFolder }, new { sName = "Zone", sValue = sZone }, new { sName = "Type", sValue = sType } };

		foreach ((string sKey, string sValue) in arrField)
		{
			listField.Add(new { sName = sKey, sValue });
		}

		return listField;
	}

	private static (int nMs, int nDoc)[] GetHourHit(QueryResult kResult, long nHour)	// 시간 하나 맞는 줄 최신 순 (결과에 둔 것: 처음 1번만 정렬, 없음: 그 시간만 다시 읽음, 동시 정렬 방지 잠금)
	{
		lock (kResult)
		{
			if (!kResult.m_hmapHit.TryGetValue(nHour, out (int nMs, int nDoc)[]? arrHit))	// 긴 기간: 이 시간만 다시 읽음
			{
				(int nMsFrom, int nMsTo) = GetMsRange(kResult.m_kFrom, kResult.m_kTo, FromHour(nHour));

				string sHourDir = GetHourDir(nHour);	// 시간 폴더

				arrHit = Directory.Exists(sHourDir) ? ReadHourHit(kResult, sHourDir, nMsFrom, nMsTo) : Array.Empty<(int nMs, int nDoc)>();

				kResult.m_hmapHit[nHour] = arrHit;
			}

			if (kResult.m_setSorted.Add(nHour))	// 처음 보는 시간: 최신 순 정렬
			{
				Array.Sort(arrHit, (kLeft, kRight) => kRight.nMs != kLeft.nMs ? kRight.nMs.CompareTo(kLeft.nMs) : kRight.nDoc.CompareTo(kLeft.nDoc));
			}

			return arrHit;
		}
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

	public static async ValueTask EnqueueAsync(LogBatch kBatch, CancellationToken kStop)	// 수신 스레드: Log 1건 → 파일 해시 담당 워커 라이트 큐 (잠금: Add 순간, 큐 가득: 대기 → TCP 역압)
	{
		IngestWorker kWorker = m_arrWorker[(int)((uint)HashCode.Combine(kBatch.sMachine.ToUpperInvariant(), kBatch.sPath.ToUpperInvariant()) % (uint)m_arrWorker.Length)];	// 담당 워커 (같은 파일은 늘 같은 워커 → 원문 순서 유지)

		while (!kWorker.TryAdd(kBatch))
		{
			await Task.Delay(m_nIngestWaitMs, kStop);
		}
	}

	public static bool IsIngestPaused()	// 밀림으로 클라 전송 멈춤 중 (새로 접속한 클라에 Pause 보낼지)
	{
		return m_bIngestPaused;
	}

	public static void ReportLoad(int nWorker, int nBacklog)	// 워커 스왑마다: 밀린 줄 m_nIngestQueueWarn 이상: 부하 로그 + 모든 클라에 Pause, 멈춤 중 모든 워커 m_nIngestQueueResume 미만: Resume
	{
		m_arrBacklog[nWorker] = nBacklog;

		if (nBacklog < m_nIngestQueueWarn && !m_bIngestPaused)	// 평소: 잠금 없이 끝
		{
			return;
		}

		lock (m_kLoadLock)
		{
			if (!m_bIngestPaused && nBacklog >= m_nIngestQueueWarn)
			{
				m_bIngestPaused		= true;
				m_nIngestPauseTick	= Environment.TickCount64;

				HTLog.Write(WARN_, $"적재 부하: 워커 {nWorker} 라이트 큐 {nBacklog:N0}줄 밀림 → 클라 전송 멈춤 요청 (Pause, 접속 {ClientListener.Broadcast(JsonSerializer.Serialize(new { sType = "Pause" }))}대)");
			}
			else if (m_bIngestPaused && Environment.TickCount64 - m_nIngestPauseTick >= m_nIngestPauseMinMs && m_arrBacklog.All(nQueue => nQueue < m_nIngestQueueResume))
			{
				m_bIngestPaused = false;

				HTLog.Write(NOTI_, $"적재 안정: 모든 워커 라이트 큐 {m_nIngestQueueResume:N0}줄 미만 → 클라 전송 재개 요청 (Resume, 접속 {ClientListener.Broadcast(JsonSerializer.Serialize(new { sType = "Resume" }))}대)");
			}
		}
	}

	public static void SaveOrigin(IngestWorker kWorker, LogBatch kBatch)	// 로직 스레드: 원문 레코드 추가 (형식: 파일 머리, 1건 = 쓰기 1번) + 시간별 색인 버퍼 (타이머가 m_nFlushSec 초마다 가져감), 머신·경로 오류·실패: 출력 후 버림
	{
		string sFile = "";	// 원문 파일

		try
		{
			sFile = GetOriginPath(kBatch.sMachine, kBatch.sPath);

			if (sFile == "")
			{
				HTLog.Write(WARN_, $"원문 저장 거부: [{kBatch.sMachine}] {kBatch.sPath}");

				return;
			}

			string sFolder	= kBatch.sFolder == "" || kBatch.sFolder.Length > 1000 ? GetFolder(kBatch.sPath) : kBatch.sFolder;	// 예전 클라·이상한 값: 클라 경로에서
			string sZone	= kBatch.sZone.Trim().Length > m_nFieldValueMax ? "" : kBatch.sZone.Trim();							// 이상한 값: 없음

			List<string>							listLine	= kBatch.listLine;
			List<long>?								listNo		= kBatch.listNo;
			List<(string sKey, string sValue)[]>?	listField	= kBatch.listField;
			List<OriginRecord>						listRecord	= new List<OriginRecord>(listLine.Count);	// 줄마다 레코드
			FileStream								kStream		= kWorker.GetOriginStream(sFile);			// 열어 둔 원문 파일 (끝에 이어 씀)
			MemoryStream							kBuffer		= kWorker.m_kWriteBuffer;					// 1건 전체를 모아 쓰기 1번
			long									nBase		= kStream.Position;							// 이번 건 시작 위치

			kBuffer.SetLength(0);

			if (listField != null)	// 추가 필드 이름 (처음 받은 표기, 새 이름: field.txt)
			{
				lock (m_kIndexLock)
				{
					LoadFieldName();	// 색인 폴더 지움: 새로

					foreach ((string sKey, string sValue)[] arrLineField in listField)
					{
						for (int nIndex = 0; nIndex < arrLineField.Length; ++nIndex)
						{
							arrLineField[nIndex].sKey = AddFieldName(arrLineField[nIndex].sKey);
						}
					}
				}
			}

			for (int nIndex = 0; nIndex < listLine.Count; ++nIndex)
			{
				string							sLine		= listLine[nIndex];																											// 받은 줄
				string							sTime		= IsLogTime(sLine) ? sLine.Substring(0, 25) : GetNow().ToString("[yyyy/MM/dd HH:mm:ss.fff]", CultureInfo.InvariantCulture);	// 원문 시간 (없음: 지금 KST)
				string							sType		= GetLineType(sLine);
				(string sKey, string sValue)[]	arrField	= listField == null ? [] : listField[nIndex];

				WriteUtf8(kBuffer, $"{sTime}[{kBatch.sMachine}][{sFolder}]{(listNo == null ? "" : $"[{listNo[nIndex]}]")}:[");	// [Line]: 원문 바로 앞 (쿼리 때 원문 앞 몇 바이트만 읽어 줄 번호)

				long	nOffset	= nBase + kBuffer.Length;		// 원문 위치 (파일 기준)
				int		nLength	= WriteUtf8(kBuffer, sLine);	// 원문 그대로 (시간·Type 포함)

				listRecord.Add(new OriginRecord(sTime, sLine, nOffset, nLength, sType, arrField));

				WriteUtf8(kBuffer, $"]:[{sType}]{(arrField.Length == 0 ? "" : new JsonObject(arrField.Select(kField => KeyValuePair.Create(kField.sKey, (JsonNode?)kField.sValue))).ToJsonString())}\r\n");	// 추가 필드: JSON (쿼리 때 원문 뒤만 읽어 표시)
			}

			kStream.Write(kBuffer.GetBuffer(), 0, (int)kBuffer.Length);
			kStream.Flush();	// 세그먼트가 가리키기 전에 파일에 있어야 함 (쿼리 ReadOrigin 은 따로 열어 읽음)

			int nFileId;	// 원문 파일 번호

			lock (m_kIndexLock)
			{
				nFileId = GetFileId(kBatch.sMachine, kBatch.sPath, sFolder, sZone);
			}

			lock (kWorker.m_kBufferLock)	// 타이머 Flush 가 버퍼를 통째로 바꿔 갈 때만 겹침
			{
				AddIndex(kWorker, nFileId, kBatch.sMachine, sFolder, sZone, listRecord);
			}
		}
		catch (Exception kException)	// 경로 형식 오류·쓰기 실패
		{
			kWorker.CloseOriginStream(sFile);	// 다음 건에 다시 엶

			HTLog.Write(WARN_, $"원문 저장 실패: [{kBatch.sMachine}] {(sFile == "" ? kBatch.sPath : sFile)} ({kException.Message})");
		}
	}

	private static int WriteUtf8(MemoryStream kBuffer, string sText)	// 문자열 → UTF-8 로 버퍼 끝에 (반환: 쓴 바이트)
	{
		int nLength = Encoding.UTF8.GetByteCount(sText);

		kBuffer.SetLength(kBuffer.Length + nLength);

		Encoding.UTF8.GetBytes(sText, kBuffer.GetBuffer().AsSpan((int)kBuffer.Length - nLength, nLength));
		kBuffer.Position = kBuffer.Length;

		return nLength;
	}

	private static void AddIndex(IngestWorker kWorker, int nFileId, string sMachine, string sFolder, string sZone, List<OriginRecord> listRecord)	// 워커 색인 버퍼 (워커 버퍼 잠금 안): 레코드 → 원문 시간 (yyyyMMddHH) 버퍼에 줄 추가 + 단어 (앞 시간 뺀 원문)·필드 (값 있음) 마다 줄 번호 (같은 단어: 줄마다 1번)
	{
		Dictionary<long, IndexBuffer>	hmapBuffer	= kWorker.m_hmapBuffer;
		AlarmMatcher?					kMatcher	= HTAlarm.m_kMatcher;														// 알람 매칭 스냅샷 (없음: 매칭 안 함)
		AlarmCount?						kCount		= kMatcher == null ? null : kWorker.GetAlarmCount(kMatcher);	// 이 워커 알람 카운터·마지막 줄 (수신 시각 분)

		foreach (OriginRecord kRecord in listRecord)
		{
			(long nHour, int nMs) = GetLineTime(kRecord.sTime);	// 원문 시간·시간 안 ms

			if (!hmapBuffer.TryGetValue(nHour, out IndexBuffer? kBuffer))	// 처음 보는 시간: 버퍼 새로
			{
				hmapBuffer[nHour] = kBuffer = new IndexBuffer();
			}

			int nDoc = kBuffer.m_listDoc.Count;	// 버퍼 안 줄 번호

			kBuffer.m_listDoc.Add((nMs, nFileId, kRecord.nOffset, kRecord.nLength));

			string			sBody	= GetLogText(kRecord.sText);	// 앞 시간 뺀 원문 (색인·알람 정확 일치)
			HashSet<string>	setWord	= SplitTerm(sBody, true);		// 색인 단어 (알람 매칭도)

			foreach (string sWord in setWord)	// 색인: 원문에서 앞 시간 뺀 단어 (앞뒤 '-'·'_' 뗀 형태도)
			{
				if (!kBuffer.m_hmapWord.TryGetValue(sWord, out List<int>? listDoc))
				{
					kBuffer.m_hmapWord[sWord] = listDoc = new List<int>();
				}

				listDoc.Add(nDoc);
			}

			AddField(kBuffer, "Machine", sMachine, nDoc);
			AddField(kBuffer, "Folder", sFolder, nDoc);
			AddField(kBuffer, "Zone", sZone, nDoc);
			AddField(kBuffer, "Type", kRecord.sType, nDoc);

			foreach ((string sKey, string sValue) in kRecord.arrField)
			{
				AddField(kBuffer, sKey, sValue, nDoc);
			}

			kMatcher?.Match(setWord, kMatcher.m_bField ? GetLineField(sMachine, sFolder, sZone, kRecord) : null, sBody, kCount!, new AlarmHit(kRecord, nFileId, sMachine, sFolder, sZone));
		}
	}

	private static HashSet<string> GetLineField(string sMachine, string sFolder, string sZone, OriginRecord kRecord)	// 알람 매칭용 줄 필드 키 "필드\t값" (색인 AddField 와 같은 값만, 대소문자 무시)
	{
		HashSet<string> setField = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach ((string sName, string sValue) in kRecord.arrField.Prepend(("Type", kRecord.sType)).Prepend(("Zone", sZone)).Prepend(("Folder", sFolder)).Prepend(("Machine", sMachine)))
		{
			if (sValue != "" && sValue.Length <= m_nFieldValueMax)
			{
				setField.Add(sName + "\t" + sValue);
			}
		}

		return setField;
	}

	private static void AddField(IndexBuffer kBuffer, string sName, string sValue, int nDoc)	// 필드 색인 버퍼: 키 "필드\t값" 에 줄 번호 (값 없음·m_nFieldValueMax 넘음: 안 함, 값 대소문자 무시 → 처음 표기로)
	{
		if (sValue == "" || sValue.Length > m_nFieldValueMax)
		{
			return;
		}

		string sFieldKey = sName + "\t" + sValue;

		if (!kBuffer.m_hmapField.TryGetValue(sFieldKey, out List<int>? listDoc))
		{
			kBuffer.m_hmapField[sFieldKey] = listDoc = new List<int>();
		}

		listDoc.Add(nDoc);
	}

	private static string GetLineType(string sText)	// 원문 (받은 줄 그대로) 의 Type (앞 시간 뒤 첫 [] 안, 없음: "")
	{
		string	sBody		= GetLogText(sText);								// 앞 시간 뺀 내용 (시간 없음: 그대로)
		int		nTypeEnd	= sBody.StartsWith('[') ? sBody.IndexOf(']') : -1;	// Type [] 끝

		return nTypeEnd > 0 ? sBody[1..nTypeEnd].Trim() : "";
	}

	public static (string sKey, string sValue)[] ReadLineField(JsonElement kField)	// 받은 줄 추가 필드 {키: 값} → [(키, 값)] (키: 형식·m_nFieldKeyMax 자 이하·기본 필드·중복 아님, 값: 있음·m_nFieldValueMax 자 이하, 최대 m_nLineFieldMax 개, 아님: 뺌)
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

			if (sValue == "" || sValue.Length > m_nFieldValueMax || sKey.Length > m_nFieldKeyMax || !m_kFieldKey.IsMatch(sKey) || m_arrBaseField.Contains(sKey, StringComparer.OrdinalIgnoreCase) || listField.Any(kItem => string.Equals(kItem.sKey, sKey, StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}

			listField.Add((sKey, sValue));
		}

		return listField.ToArray();
	}

	private static void LoadFieldName()	// File\Index\field.txt → 추가 필드 이름 (잠금 안에서, 처음 한 번, 한 줄에 1개, 색인 폴더 지움: 비움)
	{
		string sFile = Path.Combine(GetIndexDir(), "field.txt");	// 추가 필드 이름 목록

		if (m_bFieldLoaded)
		{
			if (m_arrFieldName.Length > 0 && !File.Exists(sFile))	// 색인 폴더 지움: 이름 새로
			{
				m_hmapFieldName.Clear();

				m_arrFieldName = Array.Empty<string>();
			}

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

	private static string AddFieldName(string sKey)	// 받은 추가 필드 이름 → 처음 받은 표기 (잠금 안에서, 처음 받음: 목록·File\Index\field.txt 에 추가)
	{
		if (m_hmapFieldName.TryGetValue(sKey, out string? sName))
		{
			return sName;
		}

		string sFile = Path.Combine(GetIndexDir(), "field.txt");	// 추가 필드 이름 목록

		Directory.CreateDirectory(Path.GetDirectoryName(sFile)!);

		File.AppendAllText(sFile, sKey + "\n");

		m_hmapFieldName[sKey]	= sKey;
		m_arrFieldName			= [.. m_arrFieldName, sKey];

		HTLog.Write(NOTI_, $"쿼리 필드 추가: {sKey}");

		HTAlarm.m_bFieldChanged = true;	// 알람 필드:값 판단 다시

		return sKey;
	}

	public static void SetFieldOrder(List<string> listKey)	// 클라 aField 키 순서 → 추가 필드 목록 순서 (클라 키: 앞에 그 순서, 나머지: 뒤에 원래 순서, 새 키: 등록, 바뀜: field.txt 다시 씀)
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

			HTLog.Write(INFO_, $"쿼리 필드 순서: {string.Join(", ", listName)}");
		}
	}

	public static void Tick()	// 타이머 (X초마다, 타이머 스레드): 워커 버퍼 → 세그먼트 파일 (m_nFlushSec 초 지남·m_nFlushDoc 줄 넘은 시간), 세그먼트 합치기
	{
		Flush(false);
		Merge();
	}

	public static void Close()	// 종료 (타이머 중지 후): 워커 모두 멈춤 → 끝나길 대기 (라이트 큐 처리·원문 파일 닫음) → 남은 버퍼 모두 세그먼트
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
	}

	private static void Flush(bool bAll)	// 워커 버퍼 → 세그먼트: 버퍼 잠금 안에서 쓸 버퍼만 떼고 (워커는 새 버퍼로), 잠금 밖에서 시간마다 세그먼트 1개 (bAll: 모두, 실패: 출력 후 버림 → 원문 남음)
	{
		Dictionary<long, List<IndexBuffer>> hmapFlush = new Dictionary<long, List<IndexBuffer>>();	// 시간 → 쓸 버퍼 (워커 순)

		foreach (IngestWorker kWorker in m_arrWorker)
		{
			lock (kWorker.m_kBufferLock)
			{
				foreach (KeyValuePair<long, IndexBuffer> kPair in kWorker.m_hmapBuffer.Where(kPair => bAll || Environment.TickCount64 - kPair.Value.m_nTick >= m_nFlushSec * 1000L || kPair.Value.m_listDoc.Count >= m_nFlushDoc).ToList())
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

		foreach ((long nHour, List<IndexBuffer> listBuffer) in hmapFlush.OrderBy(kPair => kPair.Key))
		{
			lock (m_kFlushLock)
			{
				try
				{
					WriteSegment(nHour, listBuffer);
				}
				catch (Exception kException)	// 사용 중·권한 없음
				{
					HTLog.Write(WARN_, $"색인 저장 실패: {nHour} ({kException.Message})");
				}
			}
		}
	}

	private static void WriteSegment(long nHour, List<IndexBuffer> listBuffer)	// 플러시 잠금 안: 같은 시간 워커 버퍼들 → doc.bin 줄 추가 (줄 번호: 지금 줄 수부터) → 세그먼트 1개 "첫-끝.idx" (버퍼 순 = 줄 번호 오름차순) → m_nMergeSeg 개 넘음: 합침
	{
		string	sDir		= GetHourDir(nHour);								// 시간 폴더
		string	sDocFile	= Path.Combine(sDir, "doc.bin");					// 줄 목록

		Directory.CreateDirectory(sDir);

		int		nFirst		= File.Exists(sDocFile) ? (int)(new FileInfo(sDocFile).Length / m_nDocRecord) : 0;	// 첫 줄 번호 (지금 줄 수)
		int		nCount		= listBuffer.Sum(kBuffer => kBuffer.m_listDoc.Count);								// 추가할 줄 수
		int		nLast		= nFirst + nCount - 1;																// 끝 줄 번호
		byte[]	arrDoc		= new byte[nCount * m_nDocRecord];													// 추가할 줄 목록
		int		nPos		= 0;																				// 쓴 줄 수

		Dictionary<ulong, List<int[]>>	hmapTerm	= new Dictionary<ulong, List<int[]>>();										// 단어 해시 → 버퍼마다 포스팅 (버퍼 순 = 줄 번호 오름차순)
		Dictionary<string, List<int[]>>	hmapField	= new Dictionary<string, List<int[]>>(StringComparer.OrdinalIgnoreCase);	// 필드 키 → 버퍼마다 포스팅

		foreach (IndexBuffer kBuffer in listBuffer)
		{
			int nBase = nFirst + nPos;	// 이 버퍼 첫 줄 번호 (시간 안)

			for (int nIndex = 0; nIndex < kBuffer.m_listDoc.Count; ++nIndex)
			{
				(int nMs, int nFileId, long nOffset, int nLength) = kBuffer.m_listDoc[nIndex];

				Span<byte> kRecord = arrDoc.AsSpan((nPos + nIndex) * m_nDocRecord, m_nDocRecord);	// 한 줄

				BinaryPrimitives.WriteInt32LittleEndian(kRecord, nMs);
				BinaryPrimitives.WriteInt32LittleEndian(kRecord[4..], nFileId);
				BinaryPrimitives.WriteInt64LittleEndian(kRecord[8..], nOffset);
				BinaryPrimitives.WriteInt32LittleEndian(kRecord[16..], nLength);
			}

			foreach (KeyValuePair<string, List<int>> kPair in kBuffer.m_hmapWord)
			{
				AddPart(hmapTerm, GetHash(kPair.Key), ToDocId(kPair.Value, nBase));
			}

			foreach (KeyValuePair<string, List<int>> kPair in kBuffer.m_hmapField)
			{
				AddPart(hmapField, kPair.Key, ToDocId(kPair.Value, nBase));
			}

			nPos += kBuffer.m_listDoc.Count;
		}

		using (FileStream kStream = new FileStream(sDocFile, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete))	// 쿼리 읽는 중 허용 (세그먼트보다 먼저 써야 세그먼트가 가리키는 줄이 있음)
		{
			kStream.Write(arrDoc);
		}

		WriteSegmentFile(sDir, nFirst, nLast, hmapTerm.Select(kPair => (nHash: kPair.Key, arrDoc: Concat(kPair.Value))).ToList(), hmapField.Select(kPair => (sKey: kPair.Key, arrDoc: Concat(kPair.Value))).ToList());

		m_hmapMerge[nHour] = Environment.TickCount64;

		if (GetSegmentList(sDir).Count > m_nMergeSeg)	// 세그먼트 많음: 비슷한 크기끼리 합침
		{
			MergeHour(nHour, false);
		}
	}

	private static void AddPart<TKey>(Dictionary<TKey, List<int[]>> hmapPart, TKey kKey, int[] arrDoc) where TKey : notnull	// 키마다 포스팅 조각 추가 (조각 순 = 줄 번호 오름차순)
	{
		if (!hmapPart.TryGetValue(kKey, out List<int[]>? listPart))
		{
			hmapPart[kKey] = listPart = new List<int[]>();
		}

		listPart.Add(arrDoc);
	}

	private static int[] ToDocId(List<int> listDoc, int nFirst)	// 버퍼 안 줄 번호 → 시간 안 줄 번호 (+ 첫 줄 번호)
	{
		int[] arrPost = listDoc.ToArray();	// 포스팅

		for (int nIndex = 0; nIndex < arrPost.Length; ++nIndex)
		{
			arrPost[nIndex] += nFirst;
		}

		return arrPost;
	}

	private static void WriteSegmentFile(string sDir, int nFirst, int nLast, List<(ulong nHash, int[] arrDoc)> listTerm, List<(string sKey, int[] arrDoc)> listField)	// 세그먼트 파일 (.tmp 에 쓰고 이름 바꿈, 형식: 파일 머리, 포스팅: 단어 → 필드 순)
	{
		listTerm.Sort((kLeft, kRight) => kLeft.nHash.CompareTo(kRight.nHash));					// 해시 순 (버킷 = 상위 바이트 → 버킷마다 이어짐)
		listField.Sort((kLeft, kRight) => string.CompareOrdinal(kLeft.sKey, kRight.sKey));		// 키 순

		byte[]			arrHeader	= new byte[m_nSegHeader];
		byte[]			arrEntry	= new byte[listTerm.Count * m_nSegEntry];	// 단어 사전
		int[]			arrBucket	= new int[m_nSegBucket + 1];				// 버킷 시작 ([b + 1] 에 건수 → 누적)
		MemoryStream	kField		= new MemoryStream();						// 필드 사전
		MemoryStream	kBlob		= new MemoryStream();						// 포스팅

		for (int nIndex = 0; nIndex < listTerm.Count; ++nIndex)
		{
			(ulong nHash, int[] arrDoc) = listTerm[nIndex];

			int nOffset = (int)kBlob.Length;	// 포스팅 위치

			WritePosting(kBlob, arrDoc);

			Span<byte> kEntry = arrEntry.AsSpan(nIndex * m_nSegEntry, m_nSegEntry);	// 칸

			BinaryPrimitives.WriteUInt64LittleEndian(kEntry, nHash);
			BinaryPrimitives.WriteInt32LittleEndian(kEntry[8..], nOffset);
			BinaryPrimitives.WriteInt32LittleEndian(kEntry[12..], arrDoc.Length);
			BinaryPrimitives.WriteInt32LittleEndian(kEntry[16..], (int)kBlob.Length - nOffset);

			++arrBucket[(int)(nHash >> 56) + 1];
		}

		foreach ((string sKey, int[] arrDoc) in listField)
		{
			int		nOffset	= (int)kBlob.Length;					// 포스팅 위치
			byte[]	arrKey	= Encoding.UTF8.GetBytes(sKey);			// 키
			byte[]	arrHead	= new byte[m_nSegField];				// 칸 머리

			WritePosting(kBlob, arrDoc);

			BinaryPrimitives.WriteInt32LittleEndian(arrHead, nOffset);
			BinaryPrimitives.WriteInt32LittleEndian(arrHead.AsSpan(4), arrDoc.Length);
			BinaryPrimitives.WriteInt32LittleEndian(arrHead.AsSpan(8), (int)kBlob.Length - nOffset);
			BinaryPrimitives.WriteUInt16LittleEndian(arrHead.AsSpan(12), (ushort)arrKey.Length);

			kField.Write(arrHead);
			kField.Write(arrKey);
		}

		for (int nBucket = 1; nBucket <= m_nSegBucket; ++nBucket)	// 건수 → 시작 칸 번호 (누적)
		{
			arrBucket[nBucket] += arrBucket[nBucket - 1];
		}

		BinaryPrimitives.WriteInt32LittleEndian(arrHeader, m_nSegVersion);
		BinaryPrimitives.WriteInt32LittleEndian(arrHeader.AsSpan(4), listTerm.Count);
		BinaryPrimitives.WriteInt32LittleEndian(arrHeader.AsSpan(8), (int)kField.Length);

		for (int nBucket = 0; nBucket <= m_nSegBucket; ++nBucket)
		{
			BinaryPrimitives.WriteInt32LittleEndian(arrHeader.AsSpan(12 + nBucket * 4), arrBucket[nBucket]);
		}

		string sTemp = Path.Combine(sDir, $"{nFirst}-{nLast}.tmp");	// 쓰는 중 파일 (다 쓰고 .idx 로)

		using (FileStream kStream = new FileStream(sTemp, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			kStream.Write(arrHeader);
			kStream.Write(arrEntry);
			kStream.Write(kField.GetBuffer(), 0, (int)kField.Length);
			kStream.Write(kBlob.GetBuffer(), 0, (int)kBlob.Length);
		}

		File.Move(sTemp, Path.ChangeExtension(sTemp, ".idx"), true);
	}

	private static void WritePosting(Stream kStream, int[] arrDoc)	// 포스팅: 줄 번호 (오름차순) 차이 varint
	{
		int nPrev = 0;	// 앞 줄 번호 (차이 저장)

		foreach (int nDoc in arrDoc)
		{
			WriteVarint(kStream, (uint)(nDoc - nPrev));

			nPrev = nDoc;
		}
	}

	private static void Merge()	// 타이머: 합칠 시간마다 세그먼트 2개 이상 (m_nMergeSeg 개 넘음·m_nMergeIdleMin 분 지남) → 합침·옛 파일 삭제, 1개뿐·지남: 목록에서 뺌 (시작 때 1번: ScanIndex)
	{
		lock (m_kFlushLock)
		{
			if (!m_bIndexScanned)	// 시작 때 1번
			{
				m_bIndexScanned = true;

				ScanIndex();
			}

			foreach (long nHour in m_hmapMerge.Keys.ToList())
			{
				string			sDir		= GetHourDir(nHour);												// 시간 폴더
				List<string>	listStale	= new List<string>();												// 옛 파일
				int				nCount		= GetSegmentList(sDir, listStale).Count;							// 세그먼트 수
				bool			bIdle		= Environment.TickCount64 - m_hmapMerge[nHour] >= m_nMergeIdleMin * 60 * 1000L;	// 마지막 쓰기 후 지남

				if (listStale.Count > 0)	// 옛 파일 삭제 (세그먼트 쓰기 잠금 안, 읽는 중: 다음 타이머에 다시)
				{
					DeleteSegments(listStale);
				}

				if (nCount >= 2 && (nCount > m_nMergeSeg || bIdle))
				{
					try
					{
						MergeHour(nHour, bIdle);
					}
					catch (Exception kException)	// 사용 중·권한 없음·형식 오류
					{
						HTLog.Write(WARN_, $"색인 합침 실패: {nHour} ({kException.Message})");
					}
				}
				else if (nCount <= 1 && bIdle && listStale.Count == 0)	// 마무리됨
				{
					m_hmapMerge.Remove(nHour);
				}
			}
		}
	}

	private static void ScanIndex()	// 시작 때 1번 (플러시 잠금 안): 시간 폴더마다 .tmp 삭제, 세그먼트 2개 이상·옛 파일 → 합칠 시간
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
				m_hmapMerge[nHour] = 0;
			}
		}
	}

	private static void MergeHour(long nHour, bool bAll)	// 플러시 잠금 안: 합칠 묶음 (PickMergeGroup) 읽어 단어·필드마다 포스팅 이어 붙여 세그먼트 1개 "첫-끝.idx" → 옛 파일 삭제 (읽는 중: 다음 타이머에)
	{
		string										sDir		= GetHourDir(nHour);											// 시간 폴더
		List<(int nFirst, int nLast, string sFile)>	listSeg		= PickMergeGroup(GetSegmentList(sDir), bAll);					// 합칠 세그먼트 (이어진 묶음)
		long										nBegin		= Stopwatch.GetTimestamp();

		if (listSeg.Count < 2)
		{
			return;
		}

		Dictionary<ulong, List<int[]>>	hmapTerm	= new Dictionary<ulong, List<int[]>>();										// 단어 해시 → 세그먼트마다 포스팅
		Dictionary<string, List<int[]>>	hmapField	= new Dictionary<string, List<int[]>>(StringComparer.OrdinalIgnoreCase);	// 필드 키 → 세그먼트마다 포스팅 (처음 표기)

		foreach ((_, _, string sFile) in listSeg)
		{
			using FileStream kStream = OpenSegment(sFile);

			SegmentHead			kHead		= ReadSegmentHead(kStream);
			byte[]				arrEntry	= new byte[kHead.nTermCount * m_nSegEntry];	// 단어 사전 전체
			List<FieldEntry>	listDict	= ReadFieldDict(kStream, kHead);			// 필드 사전 전체
			byte[]				arrBlob		= new byte[kStream.Length - GetPostStart(kHead)];	// 포스팅 전체

			kStream.Position = m_nSegHeader;
			kStream.ReadExactly(arrEntry);

			kStream.Position = GetPostStart(kHead);
			kStream.ReadExactly(arrBlob);

			for (int nIndex = 0; nIndex < kHead.nTermCount; ++nIndex)
			{
				ReadOnlySpan<byte>	kEntry	= arrEntry.AsSpan(nIndex * m_nSegEntry, m_nSegEntry);	// 칸
				ulong				nHash	= BinaryPrimitives.ReadUInt64LittleEndian(kEntry);
				int					nOffset	= BinaryPrimitives.ReadInt32LittleEndian(kEntry[8..]);
				int					nCount	= BinaryPrimitives.ReadInt32LittleEndian(kEntry[12..]);
				int					nBytes	= BinaryPrimitives.ReadInt32LittleEndian(kEntry[16..]);

				if (!hmapTerm.TryGetValue(nHash, out List<int[]>? listPart))
				{
					hmapTerm[nHash] = listPart = new List<int[]>();
				}

				listPart.Add(DecodePosting(arrBlob.AsSpan(nOffset, nBytes), nCount));
			}

			foreach (FieldEntry kEntry in listDict)
			{
				if (!hmapField.TryGetValue(kEntry.sKey, out List<int[]>? listPart))
				{
					hmapField[kEntry.sKey] = listPart = new List<int[]>();
				}

				listPart.Add(DecodePosting(arrBlob.AsSpan(kEntry.nOffset, kEntry.nBytes), kEntry.nCount));
			}
		}

		WriteSegmentFile(sDir, listSeg[0].nFirst, listSeg.Max(kSeg => kSeg.nLast), hmapTerm.Select(kPair => (nHash: kPair.Key, arrDoc: Concat(kPair.Value))).ToList(), hmapField.Select(kPair => (sKey: kPair.Key, arrDoc: Concat(kPair.Value))).ToList());

		DeleteSegments(listSeg.Select(kSeg => kSeg.sFile));	// 옛 파일 삭제

		HTLog.Write(DEBUG, $"색인 합침: {nHour} ({listSeg.Count}개 → 1개, {listSeg[0].nFirst}~{listSeg.Max(kSeg => kSeg.nLast)} 줄, {hmapTerm.Count:N0} 단어, {hmapField.Count:N0} 필드 값, {Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds:N0} ms)");
	}

	private static List<(int nFirst, int nLast, string sFile)> PickMergeGroup(List<(int nFirst, int nLast, string sFile)> listSeg, bool bAll)	// 합칠 세그먼트 묶음 (이어진 것만): bAll → 모두 (m_nMergeDocMax 넘음: 빈 목록), 아님 → 끝에서부터 가장 작은 것의 m_nMergeTier 배 이하 (2개 미만: 끝 m_nMergeSeg / 2 개)
	{
		if (bAll)
		{
			return listSeg.Count >= 2 && listSeg.Sum(kSeg => (long)(kSeg.nLast - kSeg.nFirst + 1)) <= m_nMergeDocMax ? listSeg : new List<(int nFirst, int nLast, string sFile)>();
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

		while (listSeg.Count - nStart > 2 && listSeg.Skip(nStart).Sum(kSeg => (long)(kSeg.nLast - kSeg.nFirst + 1)) > m_nMergeDocMax)	// 묶음 줄 수 상한 (메모리 합침 → 앞 (오래된) 것부터 뺌, 2개 남김)
		{
			++nStart;
		}

		return listSeg.Skip(nStart).Sum(kSeg => (long)(kSeg.nLast - kSeg.nFirst + 1)) > m_nMergeDocMax ? new List<(int nFirst, int nLast, string sFile)>() : listSeg.GetRange(nStart, listSeg.Count - nStart);	// 2개도 상한 넘음: 안 합침 (세그먼트 그대로)
	}

	private static void WriteVarint(Stream kStream, uint nValue)	// 7비트씩 (작은 값 1바이트, 이어짐 비트 0x80)
	{
		while (nValue >= 0x80)
		{
			kStream.WriteByte((byte)(nValue | 0x80));

			nValue >>= 7;
		}

		kStream.WriteByte((byte)nValue);
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

	private static ulong GetHash(string sWord)	// 단어 → 64비트 해시 (FNV-1a, 세그먼트 단어 사전 키: 단어 글자는 안 둠, 상위 바이트 = 버킷)
	{
		ulong nHash = 14695981039346656037;	// FNV-1a 64

		foreach (char cChar in sWord)
		{
			nHash = (nHash ^ cChar) * 1099511628211UL;
		}

		return nHash;
	}

	private static (long nHour, int nMs) GetLineTime(string sTime)	// 파일 쓰는 시간 [yyyy/MM/dd HH:mm:ss.fff] → 원문 시간 (yyyyMMddHH)·시간 안 ms (분·초·ms)
	{
		long	nHour	= long.Parse(string.Concat(sTime.AsSpan(1, 4), sTime.AsSpan(6, 2), sTime.AsSpan(9, 2), sTime.AsSpan(12, 2)), CultureInfo.InvariantCulture);	// 시간 폴더 (yyyyMMddHH)
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

	private static string GetIndexDir()	// 색인 폴더 (기준 폴더 하위 File\Index)
	{
		return Path.Combine(GetBaseDir(), "File", "Index");
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

	private static int GetFileId(string sMachine, string sPath, string sFolder, string sZone)	// 원문 파일 번호 (잠금 안, 머신·클라 경로·존마다 1부터, 새 파일: file.txt 에 추가 → 줄 목록엔 번호만)
	{
		string sFile = Path.Combine(GetIndexDir(), "file.txt");	// 원문 파일 번호 목록

		if (m_bFileLoaded && !File.Exists(sFile))	// 색인 폴더 지움: 번호 새로
		{
			m_listFile.Clear();
			m_hmapFileId.Clear();
		}

		LoadFileList();

		string sKey = sMachine + "\t" + sPath + "\t" + sZone;	// 머신·클라 경로·존

		if (m_hmapFileId.TryGetValue(sKey, out int nId))
		{
			return nId;
		}

		nId = m_listFile.Count + 1;

		Directory.CreateDirectory(Path.GetDirectoryName(sFile)!);

		File.AppendAllText(sFile, $"{nId},{ToCsv(sMachine, false)},{ToCsv(sPath, false)},{ToCsv(sFolder, false)},{ToCsv(sZone, false)}\n");

		m_listFile.Add((sMachine, sPath, sFolder, sZone));
		m_hmapFileId[sKey] = nId;

		return nId;
	}

	private static void LoadFileList()	// File\Index\file.txt → 원문 파일 번호 목록 (처음 한 번, 잠금 안에서, Folder·Zone 칸 없음: "")
	{
		if (m_bFileLoaded)
		{
			return;
		}

		m_bFileLoaded = true;

		string sFile = Path.Combine(GetIndexDir(), "file.txt");	// 원문 파일 번호 목록

		if (!File.Exists(sFile))
		{
			return;
		}

		foreach (string sEntry in File.ReadAllLines(sFile))	// 번호,머신,클라 경로,Folder,Zone
		{
			int nPos = 0;	// 읽을 위치

			if (!ReadCsv(sEntry, ref nPos, out string sId) || !int.TryParse(sId, NumberStyles.None, CultureInfo.InvariantCulture, out int nId) || nId <= 0 || !ReadCsv(sEntry, ref nPos, out string sMachine) || !ReadCsv(sEntry, ref nPos, out string sPath))	// 형식 오류
			{
				continue;
			}

			if (!ReadCsv(sEntry, ref nPos, out string sFolder))	// Folder 칸 없음
			{
				sFolder = "";
			}

			if (!ReadCsv(sEntry, ref nPos, out string sZone))	// Zone 칸 없음 (예전 번호)
			{
				sZone = "";
			}

			while (m_listFile.Count < nId)
			{
				m_listFile.Add(("", "", "", ""));
			}

			m_listFile[nId - 1]										= (sMachine, sPath, sFolder, sZone);
			m_hmapFileId[sMachine + "\t" + sPath + "\t" + sZone]	= nId;
		}
	}

	public static string GetFilePath(int nFileId)	// 원문 파일 번호 → 클라 경로 (알람 메일, 없음: "")
	{
		return GetFileInfo(nFileId).sPath;
	}

	private static (string sMachine, string sPath, string sFolder, string sZone) GetFileInfo(int nFileId)	// 원문 파일 번호 → 머신·클라 경로·Folder·Zone (없음: "", "", "", "")
	{
		lock (m_kIndexLock)
		{
			LoadFileList();

			return nFileId > 0 && nFileId <= m_listFile.Count ? m_listFile[nFileId - 1] : ("", "", "", "");
		}
	}

	private static string GetLogText(string sLine)	// 원문에서 앞 시간 뺀 내용 (단어 나눔·표시용, 시간 없음: 그대로)
	{
		return IsLogTime(sLine) ? sLine.Substring(25).TrimStart() : sLine;
	}

	private static bool IsLogTime(string sLine)	// 줄 앞이 시간 [yyyy/MM/dd HH:mm:ss.fff] (25글자, 숫자·구분자 자리 확인)
	{
		const string sForm = "[0000/00/00 00:00:00.000]";	// 형식 (0: 숫자)

		if (sLine.Length < sForm.Length)
		{
			return false;
		}

		for (int nIndex = 0; nIndex < sForm.Length; ++nIndex)
		{
			if (sForm[nIndex] == '0' ? !char.IsAsciiDigit(sLine[nIndex]) : sLine[nIndex] != sForm[nIndex])
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

	public static HashSet<string> SplitTerm(string sText, bool bIndex = false)	// 단어 나눔 (색인·쿼리 공통): 글자·숫자·'-'·'_' 이어진 것, 소문자, bIndex: 앞뒤 '-'·'_' 뗀 형태도 (예: [NOTI_] → noti_·noti), '-'·'_' 뿐·중복·m_nIndexWordMax 넘음: 제외
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

				if (bIndex)	// 색인: 앞뒤 '-'·'_' 뗀 형태도 (다를 때만)
				{
					setWord.Add(sWord.Trim('-', '_'));
				}
			}

			nBegin = nIndex + 1;
		}

		return setWord;
	}

	private static bool IsWordChar(char cChar)	// 단어 글자: 글자·숫자·'-'·'_' (그 외 기호·빈칸: 단어 나눔)
	{
		return char.IsLetterOrDigit(cChar) || cChar == '-' || cChar == '_';
	}

	private static bool TryGetMinuteRange(long nStart, long nEnd, out DateTime kFrom, out DateTime kTo)	// 기간 yyyyMMddHHmm → 시작 (포함)·끝 (미포함) (시작 >= 끝·형식 오류: false)
	{
		kTo = default;

		return DateTime.TryParseExact(nStart.ToString(CultureInfo.InvariantCulture), "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out kFrom)
			&& DateTime.TryParseExact(nEnd.ToString(CultureInfo.InvariantCulture), "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out kTo)
			&& kFrom < kTo;
	}

	private static string GetOriginPath(string sMachine, string sPath)	// 원문 저장 파일: 기준 폴더 하위 File\Origin\머신\클라 전체 경로 (드라이브 ':' 제거, 머신·경로 오류: "")
	{
		if (sMachine == "" || sMachine.Length > 255 || sMachine.Trim('.') == "" || sMachine.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || sPath.Length > 1000 || !Path.IsPathFullyQualified(sPath) || !sPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))	// 머신명이 폴더명으로 안 됨·전체 경로 아님·로그 아님
		{
			return "";
		}

		string sRoot = Path.GetFullPath(Path.Combine(GetBaseDir(), "File", "Origin", sMachine)) + Path.DirectorySeparatorChar;	// 머신 폴더 (끝 구분자 포함)
		string sFile = Path.GetFullPath(Path.Combine(sRoot, sPath.Replace(":", "").TrimStart('\\', '/')));						// 저장 파일 (D:\Logs → D\Logs, \\서버\공유 → 서버\공유)

		return sFile.StartsWith(sRoot, StringComparison.OrdinalIgnoreCase) ? sFile : "";	// 머신 폴더 밖 (..\ 등): 거부
	}
}

class QueryResult	// 쿼리 결과 (HTQuery: 페이지 넘김용 보관, 시간별 건수 + 짧은 기간만 맞는 줄 → 메모리 작음, 통계: 조건만)
{
	public readonly string										m_sKey;												// 기간·쿼리
	public readonly DateTime									m_kTime			= DateTime.UtcNow;							// 계산 시각
	public readonly List<(string sWord, ulong nHash)>			m_listWord		= new List<(string sWord, ulong nHash)>();	// 쿼리 단어·해시 (읽을 포스팅)
	public readonly List<string>								m_listField		= new List<string>();						// 쿼리 필드 키 "필드\t값" (읽을 포스팅)
	public readonly ConcurrentDictionary<long, (int nMs, int nDoc)[]>	m_hmapHit	= new ConcurrentDictionary<long, (int nMs, int nDoc)[]>();	// 원문 시간 → 맞는 줄 (분초·줄 번호, 짧은 기간은 계산 때, 긴 기간은 페이지 때 그 시간만)
	public readonly HashSet<long>								m_setSorted		= new HashSet<long>();						// 최신 순 정렬한 원문 시간 (페이지 읽을 때 처음 1번만)
	public readonly List<object>								m_listMinute	= new List<object>();						// 분별 건수 {nMinute, nCount} (기간 1시간 이하만)
	public readonly List<object>								m_listSecond	= new List<object>();						// 초별 건수 {nSecond, nCount} (기간 1분 이하만)
	public SearchNode?											m_kRoot;												// 조건 트리
	public bool													m_bAll;													// 조건에 * (전체 로그) 있음 → 시간마다 모든 줄 번호
	public bool													m_bExact;												// 조건에 IP·날짜·시간 검색어 있음 → 후보 줄 원문 글자 확인 (사전 건수 빠른 길 안 씀)
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

record LogBatch(string sMachine, string sPath, string sFolder, string sZone, List<string> listLine, List<long>? listNo, List<(string sKey, string sValue)[]>? listField);	// 클라 Log 1건 (ClientListener 수신 → IngestWorker 라이트 큐, listNo·listField: 예전 클라 없음)

static class HTAlarm	// 알람 (적재 시 매칭 → 매분 판정 → 발송): 사용 알람 매칭 스냅샷 (aAlarm 저장 때 교체), 통계 스레드 (HTStat)·발송 풀 (HTSend) 시작·중지
{
	public static volatile AlarmMatcher?	m_kMatcher;			// 매칭 스냅샷 (적재 스레드가 잠금 없이 읽음, 사용 알람 없음: null)
	public static volatile bool				m_bFieldChanged;	// 추가 필드 이름 늘어남 → 다음 분에 스냅샷 다시 (필드:값 판단)
	private static List<AlarmItem>			m_listAlarm		= new List<AlarmItem>();	// 지금 알람 (스냅샷 다시 만들 때)
	private static readonly object			m_kLock			= new object();				// 스냅샷 교체 1건씩

	public static void Start(List<AlarmItem> listAlarm)	// 시작: 스냅샷 → 펜딩 읽기·통계 스레드 → 발송 풀
	{
		SetAlarm(listAlarm);

		HTLog.Write(INFO_, $"알람: 사용 {listAlarm.Count(kAlarm => kAlarm.bEnable)}개 / 전체 {listAlarm.Count}개, 메일: {(HTSend.GetMailError() == "" ? "설정 있음" : "설정 없음 (Mail:sHost·sFrom 또는 sID)")}, SMS: 나중에");

		HTStat.Start();
		HTSend.Start();
	}

	public static void Stop()	// 종료 (적재 끝난 뒤): 통계 스레드 중지 → 발송 풀 남은 큐 X초 처리 → 못 보낸 작업 펜딩 저장
	{
		HTStat.Stop();
		HTStat.SavePending(HTSend.Stop());
	}

	public static void SetAlarm(List<AlarmItem> listAlarm)	// 알람 목록 바뀜 (시작·저장): 사용 알람만 매칭 스냅샷 새로
	{
		lock (m_kLock)
		{
			AlarmItem[] arrUse = listAlarm.Where(kAlarm => kAlarm.bEnable).ToArray();	// 사용 알람

			m_listAlarm		= listAlarm;
			m_bFieldChanged	= false;

			try
			{
				m_kMatcher = arrUse.Length == 0 ? null : new AlarmMatcher(arrUse);
			}
			catch (Exception kException)	// 필드 목록 읽기 실패 등: 앞 스냅샷 유지 (서버·저장 계속)
			{
				HTLog.Write(ERROR, $"알람 매칭 준비 실패: {kException.Message}");
			}
		}
	}

	public static void Refresh()	// 통계 스레드 (매분): 추가 필드 늘어났으면 스냅샷 다시
	{
		if (m_bFieldChanged)
		{
			SetAlarm(m_listAlarm);
		}
	}
}

record struct AlarmHit(OriginRecord kRecord, int nFileId, string sMachine, string sFolder, string sZone);	// 알람 맞은 줄 (원문 레코드·원문 파일 번호·머신·Folder·Zone, 메일 마지막 원문)

class AlarmCount	// 워커 알람 카운터 (스냅샷 1개, 버퍼 잠금): 알람별 맞은 줄 수·마지막 맞은 줄
{
	public readonly int[]		m_arrCount;	// 맞은 줄 수
	public readonly AlarmHit[]	m_arrLast;	// 마지막 맞은 줄 (원문 시간 최신, 없음: 기본값)

	public AlarmCount(int nAlarm)
	{
		m_arrCount	= new int[nAlarm];
		m_arrLast	= new AlarmHit[nAlarm];
	}
}

class AlarmMatcher	// 알람 매칭 스냅샷 (불변, 적재 스레드가 잠금 없이): 앵커 사전 (단어·필드 키 → 알람 위치) 으로 후보만 조건 평가, 앵커 없음 (*·NOT 만): 모든 줄 평가
{
	public readonly AlarmItem[]					m_arrAlarm;		// 사용 알람 (카운터 위치 = 배열 위치)
	public readonly bool						m_bField;		// 필드 조건 있음 (줄 필드 키 필요)
	private readonly AlarmNode[]				m_arrNode;		// 알람 조건
	private readonly Dictionary<string, int[]>	m_hmapWord;		// 앵커 단어 → 알람 위치
	private readonly Dictionary<string, int[]>	m_hmapField;	// 앵커 필드 키 (대소문자 무시) → 알람 위치
	private readonly int[]						m_arrAlways;	// 앵커 없음: 모든 줄 평가
	private readonly bool[]						m_arrMulti;		// 앵커 여러 개 (OR): 한 줄에 여러 번 후보 → 1번만 평가

	public AlarmMatcher(AlarmItem[] arrAlarm)
	{
		Dictionary<string, List<int>>	hmapWord	= new Dictionary<string, List<int>>(StringComparer.Ordinal);
		Dictionary<string, List<int>>	hmapField	= new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
		List<int>						listAlways	= new List<int>();

		m_arrAlarm	= arrAlarm;
		m_arrNode	= arrAlarm.Select(kAlarm => new AlarmNode(SearchNode.Parse(kAlarm.sQuery)!)).ToArray();
		m_bField	= m_arrNode.Any(kNode => kNode.HasField());
		m_arrMulti	= new bool[arrAlarm.Length];

		for (int nAlarm = 0; nAlarm < m_arrNode.Length; ++nAlarm)
		{
			HashSet<string>? setAnchor = m_arrNode[nAlarm].GetAnchor();	// "W:단어"·"F:필드 키" (null: 앵커 없음)

			if (setAnchor == null)
			{
				listAlways.Add(nAlarm);

				continue;
			}

			m_arrMulti[nAlarm] = setAnchor.Count > 1;

			foreach (string sAnchor in setAnchor)
			{
				Dictionary<string, List<int>>	hmapAnchor	= sAnchor[0] == 'W' ? hmapWord : hmapField;
				string							sKey		= sAnchor[2..];

				if (!hmapAnchor.TryGetValue(sKey, out List<int>? listAlarm))
				{
					hmapAnchor[sKey] = listAlarm = new List<int>();
				}

				listAlarm.Add(nAlarm);
			}
		}

		m_hmapWord	= hmapWord.ToDictionary(kPair => kPair.Key, kPair => kPair.Value.ToArray(), StringComparer.Ordinal);
		m_hmapField	= hmapField.ToDictionary(kPair => kPair.Key, kPair => kPair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
		m_arrAlways	= listAlways.ToArray();
	}

	public void Match(HashSet<string> setWord, HashSet<string>? setField, string sText, AlarmCount kCount, AlarmHit kHit)	// 줄 1개: 앵커 후보만 조건 평가 → 맞으면 카운터 +1·마지막 줄 (원문 시간 최신) (setWord: 색인 단어, setField: 필드 키, sText: 앞 시간 뺀 원문, kHit: 이 줄)
	{
		HashSet<int>? setDone = null;	// 평가한 앵커 여러 개 알람 (줄당 1번, 앵커 1개: 단어·필드 키가 집합이라 1번뿐)

		void Try(int nAlarm)
		{
			if (m_arrMulti[nAlarm] && !(setDone ??= new HashSet<int>()).Add(nAlarm))
			{
				return;
			}

			if (m_arrNode[nAlarm].Eval(setWord, setField, sText))
			{
				++kCount.m_arrCount[nAlarm];

				if (string.CompareOrdinal(kHit.kRecord.sTime, kCount.m_arrLast[nAlarm].kRecord.sTime) >= 0)	// 원문 시간 최신 (같음: 뒤 줄)
				{
					kCount.m_arrLast[nAlarm] = kHit;
				}
			}
		}

		foreach (int nAlarm in m_arrAlways)
		{
			Try(nAlarm);
		}

		if (m_hmapWord.Count > 0)
		{
			foreach (string sWord in setWord)
			{
				if (m_hmapWord.TryGetValue(sWord, out int[]? arrAlarm))
				{
					foreach (int nAlarm in arrAlarm)
					{
						Try(nAlarm);
					}
				}
			}
		}

		if (m_hmapField.Count > 0 && setField != null)
		{
			foreach (string sField in setField)
			{
				if (m_hmapField.TryGetValue(sField, out int[]? arrAlarm))
				{
					foreach (int nAlarm in arrAlarm)
					{
						Try(nAlarm);
					}
				}
			}
		}
	}
}

class AlarmNode	// 알람 조건 트리 (SearchNode 를 매칭용으로 미리 풂, 쿼리와 같은 규칙: * 전체·필드:값·단어 모두·IP·날짜·시간 원문 그대로)
{
	private readonly SEARCH_OP	m_eOp;			// 연산
	private readonly bool		m_bAll;			// 검색어 *
	private readonly string		m_sField;		// 필드 키 "필드\t값" ("": 필드 아님)
	private readonly string[]	m_arrWord;		// 단어 (모두 있어야 맞음, 없음: 안 맞음)
	private readonly string		m_sExact;		// 원문 그대로 확인 (IP·날짜·시간, "": 안 함)
	private readonly AlarmNode?	m_kLeft;		// 왼쪽 (연산만)
	private readonly AlarmNode?	m_kRight;		// 오른쪽 (연산만)

	public AlarmNode(SearchNode kNode)
	{
		m_eOp		= kNode.m_eOp;
		m_sField	= "";
		m_arrWord	= Array.Empty<string>();
		m_sExact	= "";

		if (kNode.m_eOp != SEARCH_OP.SEARCH_OP_TERM)
		{
			m_kLeft		= new AlarmNode(kNode.m_kLeft!);
			m_kRight	= new AlarmNode(kNode.m_kRight!);

			return;
		}

		m_bAll = HTQuery.IsAll(kNode);

		if (!m_bAll && !HTQuery.TryGetFieldKey(kNode.m_sTerm, out m_sField))
		{
			m_arrWord	= HTQuery.SplitTerm(kNode.m_sTerm).ToArray();
			m_sExact	= HTQuery.IsExactTerm(kNode.m_sTerm) ? kNode.m_sTerm.Trim() : "";
		}
	}

	public bool HasField()	// 필드 조건 있음
	{
		return m_eOp == SEARCH_OP.SEARCH_OP_TERM ? m_sField != "" : m_kLeft!.HasField() || m_kRight!.HasField();
	}

	public HashSet<string>? GetAnchor()	// 앵커 ("W:단어"·"F:필드 키", null: 앵커 없음 → 모든 줄, 빈 집합: 안 맞음) — AND: 앵커 적은 쪽 (같음: 긴 단어), OR: 양쪽, NOT: 왼쪽
	{
		switch (m_eOp)
		{
			case SEARCH_OP.SEARCH_OP_AND:
			{
				HashSet<string>? setLeft	= m_kLeft!.GetAnchor();
				HashSet<string>? setRight	= m_kRight!.GetAnchor();

				if (setLeft == null || setRight == null)
				{
					return setLeft ?? setRight;
				}

				return setLeft.Count != setRight.Count ? (setLeft.Count < setRight.Count ? setLeft : setRight) : (setLeft.Sum(sAnchor => sAnchor.Length) >= setRight.Sum(sAnchor => sAnchor.Length) ? setLeft : setRight);
			}

			case SEARCH_OP.SEARCH_OP_OR:
			{
				HashSet<string>? setLeft	= m_kLeft!.GetAnchor();
				HashSet<string>? setRight	= m_kRight!.GetAnchor();

				if (setLeft == null || setRight == null)
				{
					return null;
				}

				setLeft.UnionWith(setRight);

				return setLeft;
			}

			case SEARCH_OP.SEARCH_OP_NOT:
			{
				return m_kLeft!.GetAnchor();
			}

			default:
			{
				if (m_bAll)
				{
					return null;
				}

				if (m_sField != "")
				{
					return new HashSet<string> { "F:" + m_sField };
				}

				return m_arrWord.Length == 0 ? new HashSet<string>() : new HashSet<string> { "W:" + m_arrWord.MaxBy(sWord => sWord.Length) };
			}
		}
	}

	public bool Eval(HashSet<string> setWord, HashSet<string>? setField, string sText)	// 줄이 조건에 맞음
	{
		switch (m_eOp)
		{
			case SEARCH_OP.SEARCH_OP_AND:
			{
				return m_kLeft!.Eval(setWord, setField, sText) && m_kRight!.Eval(setWord, setField, sText);
			}

			case SEARCH_OP.SEARCH_OP_OR:
			{
				return m_kLeft!.Eval(setWord, setField, sText) || m_kRight!.Eval(setWord, setField, sText);
			}

			case SEARCH_OP.SEARCH_OP_NOT:
			{
				return m_kLeft!.Eval(setWord, setField, sText) && !m_kRight!.Eval(setWord, setField, sText);
			}

			default:
			{
				if (m_bAll)
				{
					return true;
				}

				if (m_sField != "")
				{
					return setField != null && setField.Contains(m_sField);
				}

				if (m_arrWord.Length == 0)
				{
					return false;
				}

				foreach (string sWord in m_arrWord)
				{
					if (!setWord.Contains(sWord))
					{
						return false;
					}
				}

				return m_sExact == "" || sText.Contains(m_sExact, StringComparison.OrdinalIgnoreCase);
			}
		}
	}
}

class AlarmJob	// 발송 작업 1건 (알람 발생 × 채널, 펜딩 pending.json 항목)
{
	public long		nLogId		{ get; set; }								// 알람 로그 줄 번호
	public long		nRetryOf	{ get; set; }								// 재전송: 처음 줄 번호 (0: 처음)
	public int		nTry		{ get; set; }								// 보낸 횟수 (재전송 때 +1)
	public long		nMinute		{ get; set; }								// 판정 분 (유닉스 분, 펜딩 유효 시간)
	public int		nID			{ get; set; }								// 알람 ID
	public string	sKey		{ get; set; } = "";							// 알람 제목
	public string	sQuery		{ get; set; } = "";							// 그 시점 조건
	public string	sTime		{ get; set; } = "";							// 판정 시각 (KST)
	public string	sWindow		{ get; set; } = "";							// 구간 (HH:mm~HH:mm)
	public int		nCount		{ get; set; }								// 구간 건수
	public int		nLimit		{ get; set; }								// 기준 건수
	public string	sChannel	{ get; set; } = "";							// Mail·Hook
	public string[]	arrTarget	{ get; set; } = Array.Empty<string>();		// 받는 곳 (메일 주소·훅 주소, 로그엔 안 씀)
	public string	sTitle		{ get; set; } = "";							// 제목
	public string	sText		{ get; set; } = "";							// 내용
}

static class HTStat	// 알람 통계 스레드 1개: 매분 (분 경계) 워커 카운터 합산 → 알람별 nMinute 분 구간 (시계 기준) 끝에 판정 (합 ≥ nCount, 억제 중 아님) → 채널마다 발송 작업 (분당 X건 한도, 넘음·실패: 펜딩 → 여유 생기면 재전송)
{
	private const int		m_nSendMax		= 100;		// 분당 발송 한도 (새 발송 + 재전송)
	private const int		m_nPendingMax	= 10000;	// 펜딩 최대 (넘음: 오래된 것 포기)
	private const int		m_nPendingMin	= 60;		// 펜딩 유효 (분, 지남: 포기)
	private const int		m_nTryMax		= 3;		// 최대 보내기 횟수 (넘음: 포기)

	private static Thread?											m_kThread;										// 통계 스레드
	private static CancellationTokenSource							m_kStop		= new CancellationTokenSource();	// 중지 요청 (대기 즉시 깸)
	private static readonly Dictionary<int, (AlarmItem kAlarm, int nSum, long nDelayEnd, AlarmHit kLast)>	m_hmapState	= new Dictionary<int, (AlarmItem kAlarm, int nSum, long nDelayEnd, AlarmHit kLast)>();	// 알람 ID → 상태 (kLast: 구간 마지막 맞은 줄) (알람 바뀜: 처음부터, 이 스레드만)
	private static List<AlarmJob>									m_listPending	= new List<AlarmJob>();			// 펜딩 (오래된 순, 이 스레드만)
	private static readonly object									m_kSaveLock	= new object();						// pending.json 쓰기 1건씩

	public static void Start()	// 펜딩 읽기 → 통계 스레드 시작
	{
		if (m_kThread != null)
		{
			return;
		}

		m_listPending	= LoadPending();
		m_kStop			= new CancellationTokenSource();
		m_kThread		= new Thread(Run) { IsBackground = true, Name = "HTStat" };

		m_kThread.Start();
	}

	public static void Stop()	// 통계 스레드 중지 (진행 중인 분 처리 완료 대기, 최대 X초)
	{
		if (m_kThread == null)
		{
			return;
		}

		m_kStop.Cancel();
		m_kThread.Join(5000);

		m_kThread = null;
	}

	private static void Run()	// 분 경계마다 처리 (예외: 출력 후 다음 분)
	{
		long nMinute = GetMinute() + 1;	// 다음 분 경계 (유닉스 분)

		while (!m_kStop.IsCancellationRequested)
		{
			long nWait = nMinute * 60000 - GetNow().ToUnixTimeMilliseconds();	// 분 경계까지 (ms)

			if (nWait > 0 && m_kStop.Token.WaitHandle.WaitOne((int)Math.Min(nWait, int.MaxValue)))
			{
				break;
			}

			try
			{
				Tick(nMinute);
			}
			catch (Exception kException)
			{
				HTLog.Write(ERROR, $"알람 통계 오류: {kException.Message}");
			}

			nMinute = Math.Max(nMinute + 1, GetMinute() + 1);	// 늦어짐: 지난 분 건너뜀
		}
	}

	private static long GetMinute()	// 지금 유닉스 분
	{
		return GetNow().ToUnixTimeMilliseconds() / 60000;
	}

	private static void Tick(long nMinute)	// 분 경계 nMinute (끝난 분: nMinute - 1): 합산 → 판정 → 발송·펜딩
	{
		HTAlarm.Refresh();

		Dictionary<int, int>		hmapCount	= new Dictionary<int, int>();		// 알람 ID → 끝난 분 건수 (수신 시각 기준)
		Dictionary<int, AlarmHit>	hmapLast	= new Dictionary<int, AlarmHit>();	// 알람 ID → 끝난 분 마지막 맞은 줄 (원문 시간 최신)

		foreach ((AlarmMatcher kMatcher, AlarmCount kCount) in HTQuery.TakeAlarmCount())
		{
			for (int nAlarm = 0; nAlarm < kCount.m_arrCount.Length; ++nAlarm)
			{
				if (kCount.m_arrCount[nAlarm] > 0)
				{
					int nID = kMatcher.m_arrAlarm[nAlarm].nID;

					hmapCount[nID] = hmapCount.GetValueOrDefault(nID) + kCount.m_arrCount[nAlarm];

					if (!hmapLast.TryGetValue(nID, out AlarmHit kLast) || string.CompareOrdinal(kCount.m_arrLast[nAlarm].kRecord.sTime, kLast.kRecord.sTime) >= 0)
					{
						hmapLast[nID] = kCount.m_arrLast[nAlarm];
					}
				}
			}
		}

		AlarmItem[]		arrAlarm	= HTAlarm.m_kMatcher?.m_arrAlarm ?? Array.Empty<AlarmItem>();	// 사용 알람
		List<AlarmJob>	listJob		= new List<AlarmJob>();											// 새 발송 작업
		bool			bChanged	= false;														// 펜딩 바뀜

		HashSet<int> setID = arrAlarm.Select(kAlarm => kAlarm.nID).ToHashSet();	// 사용 알람 ID

		foreach (int nID in m_hmapState.Keys.Where(nID => !setID.Contains(nID)).ToList())	// 없어진·꺼진 알람
		{
			m_hmapState.Remove(nID);
		}

		foreach (AlarmItem kAlarm in arrAlarm)
		{
			(_, int nSum, long nDelayEnd, AlarmHit kLast) = m_hmapState.TryGetValue(kAlarm.nID, out (AlarmItem kAlarm, int nSum, long nDelayEnd, AlarmHit kLast) kState) && kState.kAlarm == kAlarm ? kState : (kAlarm, 0, 0L, default(AlarmHit));	// 알람 바뀜 (수정·켬): 처음부터

			nSum += hmapCount.GetValueOrDefault(kAlarm.nID);

			if (hmapLast.TryGetValue(kAlarm.nID, out AlarmHit kHit) && string.CompareOrdinal(kHit.kRecord.sTime, kLast.kRecord.sTime) >= 0)	// 구간 마지막 맞은 줄 (원문 시간 최신)
			{
				kLast = kHit;
			}

			if (nMinute % kAlarm.nMinute == 0)	// 구간 끝 (시계 기준)
			{
				if (nSum >= kAlarm.nCount && nMinute >= nDelayEnd)	// 발생 (억제 중 아님)
				{
					listJob.AddRange(MakeJob(kAlarm, nMinute, nSum, kLast));

					nDelayEnd = nMinute + kAlarm.nDelay;
				}

				nSum	= 0;
				kLast	= default;
			}

			m_hmapState[kAlarm.nID] = (kAlarm, nSum, nDelayEnd, kLast);
		}

		while (HTSend.m_kResult.TryDequeue(out (AlarmJob kJob, string sError) kResult))	// 발송 실패 → 펜딩 (X번 넘음: 포기)
		{
			if (kResult.kJob.nTry >= m_nTryMax)
			{
				HTAlarmLog.Write(kResult.kJob, "Expired", $"{m_nTryMax}번 실패");
			}
			else
			{
				m_listPending.Add(kResult.kJob);
			}

			bChanged = true;
		}

		int nQuota		= Math.Max(0, m_nSendMax - HTSend.GetQueueCount());	// 이번 분 남은 한도 (못 보낸 큐만큼 줄임: 발송 느림·실패 계속에도 큐 상한)
		int nDeferred	= 0;			// 한도 넘어 펜딩

		foreach (AlarmJob kJob in listJob)
		{
			if (nQuota > 0)
			{
				--nQuota;

				HTSend.Add(kJob);
			}
			else
			{
				HTAlarmLog.Write(kJob, "Deferred", "분당 한도");

				m_listPending.Add(kJob);

				++nDeferred;
				bChanged = true;
			}
		}

		if (nDeferred > 0)
		{
			HTLog.Write(WARN_, $"알람 발송 한도 넘음: {nDeferred}건 펜딩 (분당 {m_nSendMax}건)");
		}

		foreach (AlarmJob kJob in m_listPending.Where(kJob => nMinute - kJob.nMinute > m_nPendingMin || !setID.Contains(kJob.nID)))	// 유효 시간 지남·알람 없어짐·꺼짐: 포기
		{
			HTAlarmLog.Write(kJob, "Expired", setID.Contains(kJob.nID) ? "유효 시간 지남" : "알람 없음·꺼짐");

			bChanged = true;
		}

		m_listPending.RemoveAll(kJob => nMinute - kJob.nMinute > m_nPendingMin || !setID.Contains(kJob.nID));

		while (m_listPending.Count > m_nPendingMax)	// 최대 넘음: 오래된 것부터 포기
		{
			HTAlarmLog.Write(m_listPending[0], "Expired", "펜딩 최대 넘음");

			m_listPending.RemoveAt(0);

			bChanged = true;
		}

		while (nQuota > 0 && m_listPending.Count > 0)	// 남은 한도: 펜딩 재전송 (오래된 것부터)
		{
			AlarmJob kJob = m_listPending[0];

			m_listPending.RemoveAt(0);

			kJob.nRetryOf	= kJob.nRetryOf == 0 ? kJob.nLogId : kJob.nRetryOf;
			kJob.nLogId		= HTAlarmLog.NewId();

			HTSend.Add(kJob);

			--nQuota;
			bChanged = true;
		}

		if (bChanged)
		{
			SavePending(Array.Empty<AlarmJob>());
		}
	}

	private static IEnumerable<AlarmJob> MakeJob(AlarmItem kAlarm, long nMinute, int nSum, AlarmHit kLast)	// 알람 발생 → 등록된 채널마다 작업 (메일: 받는 사람 모두 1건, 훅 1건, SMS: 나중에)
	{
		DateTimeOffset	kEnd	= DateTimeOffset.FromUnixTimeMilliseconds(nMinute * 60000).ToOffset(GetNow().Offset);	// 구간 끝 (KST)
		DateTimeOffset	kStart	= kEnd.AddMinutes(-kAlarm.nMinute);														// 구간 시작
		string			sTitle	= $"{GetTitleTag()}:{kAlarm.sKey} - [{nSum}]";	// [LM][존]:알람 제목 - [횟수]
		string			sText	= GetMailText(kAlarm.sQuery, kStart, kEnd, nSum, kLast.kRecord.sText ?? "", GetHitField(kLast));

		foreach ((string sChannel, string[] arrTarget) in GetChannel(kAlarm))
		{
			yield return new AlarmJob
			{
				nLogId		= HTAlarmLog.NewId(),
				nMinute		= nMinute,
				nID			= kAlarm.nID,
				sKey		= kAlarm.sKey,
				sQuery		= kAlarm.sQuery,
				sTime		= kEnd.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
				sWindow		= $"{kStart:HH:mm}~{kEnd:HH:mm}",
				nCount		= nSum,
				nLimit		= kAlarm.nCount,
				sChannel	= sChannel,
				arrTarget	= arrTarget,
				sTitle		= sTitle,
				sText		= sText,
			};
		}
	}

	public static IEnumerable<AlarmJob> MakeTestJob(AlarmItem kAlarm)	// 테스트 발송 작업 (제목: 알람 nCount 회·:[테스트], 내용: 실제 알람 형식, 구간: 보낸 시각 기준, 마지막 원문: 가상)
	{
		DateTimeOffset	kEnd	= GetNow();							// 보낸 시각 (KST)
		DateTimeOffset	kStart	= kEnd.AddMinutes(-kAlarm.nMinute);	// 구간 시작
		string			sTime	= kEnd.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
		string			sLine	= $"[{kEnd.ToString("yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] [ERROR] TEST";	// 가상 마지막 원문
		string			sField	= $"Machine: TEST\nFolder: TEST\nFile: TEST\nZone: {HTConfig.GetString("Base:sZone")}\nType: ERROR";	// 가상 필드

		return GetChannel(kAlarm).Select(kChannel => new AlarmJob
		{
			nID			= kAlarm.nID,
			sKey		= kAlarm.sKey,
			sQuery		= kAlarm.sQuery,
			sTime		= sTime,
			nLimit		= kAlarm.nCount,
			sChannel	= kChannel.sChannel,
			arrTarget	= kChannel.arrTarget,
			sTitle		= $"{GetTitleTag()}:{kAlarm.sKey} - [{kAlarm.nCount}]:[테스트]",
			sWindow		= $"{kStart:HH:mm}~{kEnd:HH:mm}",
			nCount		= kAlarm.nCount,
			sText		= GetMailText(kAlarm.sQuery, kStart, kEnd, kAlarm.nCount, sLine, sField),
		});
	}

	private static string GetMailText(string sQuery, DateTimeOffset kStart, DateTimeOffset kEnd, int nCount, string sLine, string sField)	// 메일·훅 내용: 마지막 원문 → 빈 줄 2개 → Query·Time·Count → 빈 줄 → 필드 → 빈 줄 2개 → 쿼리 링크 (없는 것: 뺌)
	{
		StringBuilder	kText	= new StringBuilder();
		string			sLink	= HTWeb.GetQueryLink(sQuery, kStart, kEnd);	// 쿼리 탭 바로 실행 주소

		if (sLine != "")
		{
			kText.Append(sLine).Append("\n\n\n");
		}

		kText.Append($"Query: {sQuery}\nTime: {kStart:yyyy-MM-dd HH:mm} - {kEnd:yyyy-MM-dd HH:mm}\nCount: {nCount}회");

		if (sField != "")
		{
			kText.Append("\n\n").Append(sField);
		}

		if (sLink != "")
		{
			kText.Append("\n\n\n").Append(sLink);
		}

		return kText.ToString();
	}

	private static string GetHitField(AlarmHit kHit)	// 마지막 맞은 줄 필드: Machine·Folder·File·Zone·Type·추가 필드 (줄 없음: "")
	{
		if (kHit.kRecord.sText == null)
		{
			return "";
		}

		string			sFolder	= kHit.sFolder.TrimEnd('\\', '/');																	// 대상 폴더
		string			sPath	= HTQuery.GetFilePath(kHit.nFileId);																	// 클라 경로
		string			sFile	= sFolder != "" && sPath.Length > sFolder.Length && sPath.StartsWith(sFolder, StringComparison.OrdinalIgnoreCase) ? sPath[(sFolder.Length + 1)..] : sPath;	// Folder 아래 경로 (화면 File 칸과 같음)
		StringBuilder	kText	= new StringBuilder();

		kText.Append("Machine: ").Append(kHit.sMachine).Append("\nFolder: ").Append(kHit.sFolder).Append("\nFile: ").Append(sFile).Append("\nZone: ").Append(kHit.sZone).Append("\nType: ").Append(kHit.kRecord.sType);

		foreach ((string sKey, string sValue) in kHit.kRecord.arrField)
		{
			kText.Append('\n').Append(sKey).Append(": ").Append(sValue);
		}

		return kText.ToString();
	}

	private static string GetTitleTag()	// 메일·훅 제목 앞: [LM] 고정 + [존] (설정 Base:sZone, 예: [LM][Real], 존 없음: [LM])
	{
		string sZone = HTConfig.GetString("Base:sZone").Trim();

		return sZone == "" ? "[LM]" : $"[LM][{sZone}]";
	}

	private static (string sChannel, string[] arrTarget)[] GetChannel(AlarmItem kAlarm)	// 등록된 채널 (메일: 받는 사람 모두 1건, 훅 1건, SMS: 나중에)
	{
		return new[] { ("Mail", kAlarm.aMail), ("Hook", kAlarm.sHook == "" ? Array.Empty<string>() : new[] { kAlarm.sHook }) }.Where(kChannel => kChannel.Item2.Length > 0).ToArray();
	}

	private static string GetPendingFile()	// 펜딩 파일 (기준 폴더 하위 File\Alarm\pending.json)
	{
		return Path.Combine(GetBaseDir(), "File", "Alarm", "pending.json");
	}

	private static List<AlarmJob> LoadPending()	// 펜딩 읽기 (없음·오류: 빈 목록, 오류: 출력)
	{
		string sFile = GetPendingFile();

		try
		{
			return File.Exists(sFile) ? JsonSerializer.Deserialize<List<AlarmJob>>(File.ReadAllText(sFile)) ?? new List<AlarmJob>() : new List<AlarmJob>();
		}
		catch (Exception kException)
		{
			HTLog.Write(WARN_, $"알람 펜딩 읽기 실패: {sFile} ({kException.Message})");

			return new List<AlarmJob>();
		}
	}

	public static void SavePending(IEnumerable<AlarmJob> listMore)	// 펜딩 저장 (임시 파일 후 교체, listMore: 종료 때 못 보낸 작업 함께)
	{
		string sFile = GetPendingFile();

		lock (m_kSaveLock)
		{
			try
			{
				List<AlarmJob> listSave = m_listPending.Concat(listMore).ToList();	// 저장할 작업

				Directory.CreateDirectory(Path.GetDirectoryName(sFile)!);
				File.WriteAllText(sFile + ".tmp", JsonSerializer.Serialize(listSave));
				File.Move(sFile + ".tmp", sFile, true);
			}
			catch (Exception kException)
			{
				HTLog.Write(WARN_, $"알람 펜딩 저장 실패: {sFile} ({kException.Message})");
			}
		}
	}
}

static class HTSend	// 알람 발송 스레드 풀 X개: 공유 큐 (통계 스레드 Add) 를 한가한 스레드가 꺼내 채널별 발송 (메일·훅) → 알람 로그 + 실패: 결과 큐 (통계 스레드가 매분 꺼내 펜딩)
{
	private const int													m_nThreadCount	= 5;	// 발송 스레드 수
	private const int													m_nTimeoutMs	= 10000;	// 발송 1건 제한 (ms)
	private const int													m_nStopWaitMs	= 5000;	// 종료 때 남은 큐 처리 대기 (ms)

	public static readonly ConcurrentQueue<(AlarmJob kJob, string sError)>	m_kResult	= new ConcurrentQueue<(AlarmJob kJob, string sError)>();	// 실패 작업 (통계 스레드가 매분 꺼냄)
	private static BlockingCollection<AlarmJob>							m_kQueue	= new BlockingCollection<AlarmJob>();	// 발송 큐 (공유)
	private static Thread[]												m_arrThread	= Array.Empty<Thread>();				// 발송 스레드
	private static readonly HttpClient									m_kHttp		= new HttpClient { Timeout = TimeSpan.FromMilliseconds(m_nTimeoutMs) };	// 훅 (1개 공유)

	public static void Start()	// 발송 스레드 시작
	{
		if (m_arrThread.Length > 0)
		{
			return;
		}

		m_kQueue	= new BlockingCollection<AlarmJob>();
		m_arrThread	= Enumerable.Range(0, m_nThreadCount).Select(nIndex => new Thread(Run) { IsBackground = true, Name = $"HTSend{nIndex}" }).ToArray();

		foreach (Thread kThread in m_arrThread)
		{
			kThread.Start();
		}
	}

	public static List<AlarmJob> Stop()	// 종료: 새 작업 막고 남은 큐 X초 처리 → 못 보낸 작업·실패 작업 (펜딩 저장용)
	{
		m_kQueue.CompleteAdding();

		long nEnd = Environment.TickCount64 + m_nStopWaitMs;	// 대기 끝

		foreach (Thread kThread in m_arrThread)
		{
			kThread.Join((int)Math.Max(0, nEnd - Environment.TickCount64));
		}

		m_arrThread = Array.Empty<Thread>();

		List<AlarmJob> listLeft = new List<AlarmJob>();	// 못 보낸 작업

		while (m_kQueue.TryTake(out AlarmJob? kJob))
		{
			listLeft.Add(kJob);
		}

		while (m_kResult.TryDequeue(out (AlarmJob kJob, string sError) kResult))
		{
			listLeft.Add(kResult.kJob);
		}

		return listLeft;
	}

	public static void Add(AlarmJob kJob)	// 통계 스레드: 발송 큐 끝에 (한가한 스레드가 깨어 꺼냄)
	{
		m_kQueue.Add(kJob);
	}

	private static void Run()	// 발송 스레드: 큐 빌 때까지 꺼내 보냄, 비면 대기 (CPU 0)
	{
		foreach (AlarmJob kJob in m_kQueue.GetConsumingEnumerable())
		{
			string sError = "";	// 실패 사유 (성공: "")

			++kJob.nTry;

			try
			{
				Send(kJob);
			}
			catch (Exception kException)
			{
				sError = GetErrorText(kException, $"알람 발송 #{kJob.nID} {kJob.sChannel}");
			}

			HTAlarmLog.Write(kJob, sError != "" ? "Failed" : (kJob.nRetryOf != 0 ? "Resent" : "Sent"), sError);

			if (sError != "")
			{
				m_kResult.Enqueue((kJob, sError));
			}
		}
	}

	public static int GetQueueCount()	// 못 보낸 발송 큐 수
	{
		return m_kQueue.Count;
	}

	public static string SendNow(AlarmJob kJob)	// 바로 보냄 (테스트 발송, 요청 스레드, 반환: 실패 사유·성공 "")
	{
		try
		{
			Send(kJob);

			return "";
		}
		catch (Exception kException)
		{
			return GetErrorText(kException, $"알람 테스트 발송 #{kJob.nID} {kJob.sChannel}");
		}
	}

	private static void Send(AlarmJob kJob)	// 채널별 발송 (실패: 예외)
	{
		if (kJob.sChannel == "Mail")
		{
			SendMail(kJob);
		}
		else
		{
			SendHook(kJob);
		}
	}

	public static string GetMailError()	// 메일 설정 확인 (Mail:sHost·보내는 주소 없음: "Mail Off", 있음: "")
	{
		return HTConfig.GetString("Mail:sHost") == "" || GetMailFrom() == "" ? "Mail Off" : "";
	}

	private static string GetMailFrom()	// 보내는 주소 (Mail:sFrom, 없음: 로그인 계정 Mail:sID)
	{
		string sFrom = HTConfig.GetString("Mail:sFrom");

		return sFrom != "" ? sFrom : HTConfig.GetString("Mail:sID");
	}

	private static void SendMail(AlarmJob kJob)	// 메일 (SMTP, 설정 Mail:sHost·nPort·bSsl·sFrom (없음: sID), 인증: Mail:sID·sCode 있을 때만): 제목·내용 텍스트, 받는 사람 모두
	{
		if (GetMailError() != "")
		{
			throw new RelayException(GetMailError());
		}

		using System.Net.Mail.SmtpClient	kClient	= new System.Net.Mail.SmtpClient(HTConfig.GetString("Mail:sHost"), HTConfig.GetInt("Mail:nPort", 25)) { EnableSsl = HTConfig.GetBool("Mail:bSsl"), Timeout = m_nTimeoutMs };
		using System.Net.Mail.MailMessage	kMail	= new System.Net.Mail.MailMessage { From = new System.Net.Mail.MailAddress(GetMailFrom()), Subject = kJob.sTitle, Body = kJob.sText };

		if (HTConfig.GetString("Mail:sID") != "")
		{
			kClient.Credentials = new NetworkCredential(HTConfig.GetString("Mail:sID"), HTConfig.GetString("Mail:sPass"));
		}

		foreach (string sMail in kJob.arrTarget)
		{
			kMail.To.Add(sMail);
		}

		try
		{
			kClient.Send(kMail);
		}
		catch (System.Net.Mail.SmtpException kException)	// SMTP 거부·접속 실패: 상태 이름 (상세: 로그)
		{
			HTLog.Write(WARN_, $"알람 메일 SMTP: {kException.StatusCode} ({kException.InnerException?.Message ?? kException.Message})");

			throw new RelayException($"Mail {kException.StatusCode}");
		}
	}

	private static void SendHook(AlarmJob kJob)	// 훅: POST {"sTitle", "sText"} (2xx: 성공, 길이 있는 본문: chunked 못 받는 곳 대비)
	{
		using StringContent kBody = new StringContent(JsonSerializer.Serialize(new { kJob.sTitle, kJob.sText }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Encoding.UTF8, "application/json");	// 본문

		try
		{
			using HttpResponseMessage kResponse = m_kHttp.PostAsync(kJob.arrTarget[0], kBody).GetAwaiter().GetResult();

			if (!kResponse.IsSuccessStatusCode)
			{
				throw new RelayException($"HTTP {(int)kResponse.StatusCode}");
			}
		}
		catch (TaskCanceledException)	// 제한 시간 넘음 (User Stop 아님)
		{
			throw new TimeoutException();
		}
		catch (HttpRequestException kException)	// 접속 안 됨·주소 오류 (상세: 로그)
		{
			HTLog.Write(WARN_, $"알람 훅: {kException.Message}");

			throw new RelayException("Hook Off");
		}
	}
}

static class HTAlarmLog	// 알람 로그: 기준 폴더 하위 File\Alarm\log\yyyyMMdd.jsonl 추가 전용 (1줄 = 발송 작업 결과 1건, 받는 곳 안 씀) + 조회 API
{
	private const int				m_nPageSize	= 100;	// 조회 한 페이지 줄 수
	private const int				m_nDayMax	= 31;	// 조회 최대 기간 (일, 메모리 보호)
	private const int				m_nPageMax	= 100;	// 조회 최대 페이지 (보관 줄 수 상한)
	private static long				m_nLogId	= GetNow().ToUnixTimeMilliseconds() * 100;	// 줄 번호 (시작 시각 기준 → 재시작해도 겹침 없음)
	private static readonly object	m_kLock		= new object();	// 파일 추가 1건씩

	public static long NewId()	// 새 줄 번호
	{
		return Interlocked.Increment(ref m_nLogId);
	}

	public static void Write(AlarmJob kJob, string sResult, string sError)	// 결과 1줄 추가 (Sent·Failed·Deferred·Resent·Expired, 실패: 출력만)
	{
		string sLine = new JsonObject
		{
			["nLogId"]		= kJob.nLogId,
			["sTime"]		= kJob.sTime,
			["nID"]			= kJob.nID,
			["sKey"]		= kJob.sKey,
			["sQuery"]		= kJob.sQuery,
			["nCount"]		= kJob.nCount,
			["nLimit"]		= kJob.nLimit,
			["sWindow"]		= kJob.sWindow,
			["sChannel"]	= kJob.sChannel,
			["sResult"]		= sResult,
			["sError"]		= sError,
			["nRetryOf"]	= kJob.nRetryOf,
			["nTry"]		= kJob.nTry,
			["sDone"]		= GetNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
		}.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });	// 줄 (JSON)
		string sFile = Path.Combine(GetBaseDir(), "File", "Alarm", "log", GetNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");

		lock (m_kLock)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(sFile)!);
				File.AppendAllText(sFile, sLine + "\n");
			}
			catch (Exception kException)
			{
				HTLog.Write(WARN_, $"알람 로그 쓰기 실패: {sFile} ({kException.Message})");
			}
		}
	}

	public static IResult OnLog(long? nStart, long? nEnd, int? nID, string? sResult, int? nPage)	// 알람 로그 조회: 기간 (yyyyMMddHHmm, 기록 날짜 파일, 최대 X일)·알람 ID·결과 → 최신 순 페이지 {sError, nTotal, nPage, nPageSize, arrLog}
	{
		DateTime	kNow	= GetNow().DateTime;
		DateTime	kEnd	= nEnd != null && DateTime.TryParseExact(nEnd.ToString(), "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kEndValue) ? kEndValue : kNow;	// 끝 (없음: 지금)
		DateTime	kStart	= nStart != null && DateTime.TryParseExact(nStart.ToString(), "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kStartValue) ? kStartValue : kEnd.AddDays(-1);	// 시작 (없음: 1일 전)

		if (kStart > kEnd || (kEnd - kStart).TotalDays > m_nDayMax)
		{
			return Results.Json(new { sError = $"기간 오류 (최대 {m_nDayMax}일)", nTotal = 0, nPage = 0, nPageSize = m_nPageSize, arrLog = Array.Empty<object>() });
		}

		int				nPageValue	= Math.Clamp(nPage ?? 0, 0, m_nPageMax);			// 페이지 (0부터, 상한: 메모리)
		int				nKeep		= (nPageValue + 1) * m_nPageSize;				// 보관할 최신 줄 수 (이 페이지까지)
		Queue<JsonNode>	queueLog	= new Queue<JsonNode>();						// 맞는 줄 중 최신 nKeep 개 (오래된 순)
		int				nTotal		= 0;											// 맞는 줄 수

		for (DateTime kDay = kStart.Date; kDay <= kEnd.Date; kDay = kDay.AddDays(1))
		{
			string sFile = Path.Combine(GetBaseDir(), "File", "Alarm", "log", kDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");

			if (!File.Exists(sFile))
			{
				continue;
			}

			using FileStream	kStream	= new FileStream(sFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);	// 쓰는 중에도 읽음 (쓰기 잠금 안 잡음)
			using StreamReader	kReader	= new StreamReader(kStream, Encoding.UTF8);

			for (string? sLine = kReader.ReadLine(); sLine != null; sLine = kReader.ReadLine())
			{
				try
				{
					JsonNode?	kLog	= JsonNode.Parse(sLine);
					string		sDone	= kLog?["sDone"]?.GetValue<string>() ?? "";	// 기록 시각

					if (kLog == null || !DateTime.TryParseExact(sDone, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime kDone) || kDone < kStart || kDone >= kEnd.AddMinutes(1)
						|| (nID != null && kLog["nID"]?.GetValue<int>() != nID) || (!string.IsNullOrEmpty(sResult) && kLog["sResult"]?.GetValue<string>() != sResult))
					{
						continue;
					}

					++nTotal;

					queueLog.Enqueue(kLog);

					if (queueLog.Count > nKeep)	// 오래된 것 버림 (메모리 상한)
					{
						queueLog.Dequeue();
					}
				}
				catch (Exception kException) when (kException is JsonException or InvalidOperationException or FormatException)	// 깨진 줄 (쓰는 중 종료)·이상한 값: 건너뜀
				{
				}
			}
		}

		return Results.Json(new { sError = "", nTotal, nPage = nPageValue, nPageSize = m_nPageSize, arrLog = queueLog.Reverse().Skip(nPageValue * m_nPageSize).Take(m_nPageSize) });
	}
}

class IngestWorker	// 적재 로직 스레드 1개 (HTQuery): 라이트 큐 (수신 스레드 Add) ↔ 리드 큐 스왑 (잠금: 스왑 순간) → 잠금 없이 처리 (원문 쓰기·색인 버퍼), 원문 파일 열어 둠
{
	private readonly object						m_kQueueLock	= new object();													// 라이트 큐 Add·스왑 잠금 (짧게)
	private List<LogBatch>						m_listWrite		= new List<LogBatch>();											// 라이트 큐 (수신 스레드 Add)
	private List<LogBatch>						m_listRead		= new List<LogBatch>();											// 리드 큐 (로직 스레드만, 스왑으로 바뀜)
	private int									m_nQueueLine;																	// 라이트 큐 줄 수 (수신 스레드 Interlocked, 최대 넘음: 역압)
	private readonly AutoResetEvent				m_kSignal		= new AutoResetEvent(false);									// 라이트 큐에 들어옴 → 로직 스레드 깨움
	private readonly Thread						m_kThread;																		// 로직 스레드
	private volatile bool						m_bStop;																		// 멈춤 요청 (남은 큐 다 처리 뒤 끝)
	private long								m_nWarnTick;																	// 마지막 부하 로그 시각 (TickCount64)
	private long								m_nIdleTick		= Environment.TickCount64;										// 마지막 원문 파일 정리 시각
	private readonly Dictionary<string, (FileStream kStream, long nTick)>	m_hmapStream	= new Dictionary<string, (FileStream kStream, long nTick)>(StringComparer.OrdinalIgnoreCase);	// 열어 둔 원문 파일 (경로 → 스트림·마지막 쓰기 시각, 로직 스레드만)
	public readonly object						m_kBufferLock	= new object();													// 색인 버퍼 잠금 (로직 스레드 Add ↔ 타이머 Flush 가 통째로 가져감)
	public Dictionary<long, IndexBuffer>		m_hmapBuffer	= new Dictionary<long, IndexBuffer>();							// 원문 시간 (yyyyMMddHH) → 색인 버퍼 (세그먼트 파일 전)
	public readonly MemoryStream				m_kWriteBuffer	= new MemoryStream();											// Log 1건 원문 레코드 모아 쓰기 (로직 스레드만)
	public readonly int							m_nIndex;																		// 워커 번호 (로그)
	public AlarmMatcher?						m_kAlarmMatcher;													// 알람 카운터 기준 스냅샷 (버퍼 잠금)
	public AlarmCount							m_kAlarmCount	= new AlarmCount(0);								// 이번 분 알람별 맞은 줄 수·마지막 줄 (버퍼 잠금, 통계 스레드가 매분 가져감)
	public readonly List<(AlarmMatcher kMatcher, AlarmCount kCount)>	m_listAlarmOld	= new List<(AlarmMatcher kMatcher, AlarmCount kCount)>();	// 분 중간 스냅샷 바뀜: 앞 카운터 (통계 스레드가 같이 가져감)

	public IngestWorker(int nIndex)
	{
		m_nIndex	= nIndex;
		m_kThread	= new Thread(Run) { IsBackground = true, Name = $"Ingest{nIndex}" };

		m_kThread.Start();
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

	public bool TryAdd(LogBatch kBatch)	// 수신 스레드: 라이트 큐 끝에 (모든 워커 합 최대 줄 수 넘음: false → 호출 쪽이 기다렸다 다시)
	{
		if (Volatile.Read(ref HTQuery.m_nIngestQueueLine) >= HTQuery.m_nIngestQueueMax)
		{
			return false;
		}

		lock (m_kQueueLock)
		{
			m_listWrite.Add(kBatch);
		}

		Interlocked.Add(ref m_nQueueLine, kBatch.listLine.Count);
		Interlocked.Add(ref HTQuery.m_nIngestQueueLine, kBatch.listLine.Count);
		m_kSignal.Set();

		return true;
	}

	public void RequestStop()	// 종료 요청: 남은 큐 다 처리하고 원문 파일 닫은 뒤 스레드 끝남
	{
		m_bStop = true;

		m_kSignal.Set();
	}

	public void Join()	// 종료 기다림 (최대 X초)
	{
		m_kThread.Join(10000);
	}

	private void Run()	// 로직 스레드: 신호 (또는 m_nIngestIdleMs) 마다 스왑 → 처리, 멈춤 요청: 남은 것 처리 뒤 원문 파일 닫음 (예외: 출력 후 계속)
	{
		while (!m_bStop)
		{
			try
			{
				m_kSignal.WaitOne(HTQuery.m_nIngestIdleMs);
				Drain();
			}
			catch (Exception kException)	// 메모리 부족·디스크 오류 등
			{
				HTLog.Write(ERROR, $"적재 워커 {m_nIndex} 오류: {kException.Message}");

				Thread.Sleep(1000);
			}
		}

		try
		{
			Drain();
		}
		catch (Exception kException)	// 종료 중 오류
		{
			HTLog.Write(ERROR, $"적재 워커 {m_nIndex} 종료 오류: {kException.Message}");
		}

		CloseOriginStream(null);
	}

	private void Drain()	// 큐 스왑 (잠금: 포인터 바꿈만) → 리드 큐 처리 (잠금 없음), 밀림: 부하 로그, 오래 안 쓴 원문 파일 닫음
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

		HTQuery.ReportLoad(m_nIndex, nLine);	// 밀림 → 클라 Pause, 풀림 → Resume

		if (listRead.Count > 0)
		{
			int		nBatch	= listRead.Count;			// 이번 건수
			long	nBegin	= Stopwatch.GetTimestamp();	// 처리 시작

			Interlocked.Add(ref m_nQueueLine, -nLine);
			Interlocked.Add(ref HTQuery.m_nIngestQueueLine, -nLine);

			foreach (LogBatch kBatch in listRead)
			{
				HTQuery.SaveOrigin(this, kBatch);
			}

			listRead.Clear();

			if (nLine >= HTQuery.m_nIngestQueueWarn && Environment.TickCount64 - m_nWarnTick >= HTQuery.m_nIngestWarnSec * 1000L)	// 밀림: 부하 로그 (간격 제한)
			{
				m_nWarnTick = Environment.TickCount64;

				HTLog.Write(WARN_, $"적재 부하: 워커 {m_nIndex} 라이트 큐 {nLine:N0}줄 ({nBatch:N0}건) 한 번에 처리 ({Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds:N0} ms, 원문 파일 {m_hmapStream.Count}개 열림)");
			}
		}

		if (Environment.TickCount64 - m_nIdleTick >= HTQuery.m_nOriginIdleSec * 1000L / 2)	// 오래 안 쓴 원문 파일 닫음 (주기 확인)
		{
			m_nIdleTick = Environment.TickCount64;

			foreach (string sFile in m_hmapStream.Where(kPair => Environment.TickCount64 - kPair.Value.nTick >= HTQuery.m_nOriginIdleSec * 1000L).Select(kPair => kPair.Key).ToList())
			{
				CloseOriginStream(sFile);
			}
		}
	}

	public FileStream GetOriginStream(string sFile)	// 열어 둔 원문 파일 (없음: 이어 쓰기로 엶·폴더 만듦, 최대 수 넘음: 가장 오래 안 쓴 것 닫음, 쿼리 읽기 허용)
	{
		if (m_hmapStream.TryGetValue(sFile, out (FileStream kStream, long nTick) kOpen))
		{
			m_hmapStream[sFile] = (kOpen.kStream, Environment.TickCount64);

			return kOpen.kStream;
		}

		if (m_hmapStream.Count >= HTQuery.m_nOriginOpenMax)	// 너무 많이 열림: 가장 오래 안 쓴 것 닫음
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

class IndexBuffer	// 원문 시간 (yyyyMMddHH) 하나의 색인 버퍼 (워커가 채우고 타이머가 통째로 가져가 세그먼트로)
{
	public readonly List<(int nMs, int nFileId, long nOffset, int nLength)>	m_listDoc	= new List<(int nMs, int nFileId, long nOffset, int nLength)>();	// 줄 (버퍼 안 줄 번호 순: 분초·원문 파일 번호·원문 위치·원문 바이트)
	public readonly Dictionary<string, List<int>>							m_hmapWord	= new Dictionary<string, List<int>>(StringComparer.Ordinal);			// 단어 → 버퍼 안 줄 번호 (오름차순)
	public readonly Dictionary<string, List<int>>							m_hmapField	= new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);	// 필드 키 "필드\t값" → 버퍼 안 줄 번호 (오름차순, 값 대소문자 무시)
	public readonly long													m_nTick		= Environment.TickCount64;											// 처음 쌓인 시각 (m_nFlushSec 초 지나면 세그먼트 파일)
}

record struct SegmentHead(int nTermCount, int nFieldBytes, int[] arrBucket);	// 세그먼트 머리 (단어 수, 필드 사전 바이트, 버킷 시작 257: 버킷 b 의 단어 사전 칸 = [b] ~ [b + 1] 미포함)

record struct FieldEntry(string sKey, int nOffset, int nCount, int nBytes);	// 세그먼트 필드 사전 칸 (키 "필드\t값", 포스팅 위치·건수·바이트)

record struct OriginRecord(string sTime, string sText, long nOffset, int nLength, string sType, (string sKey, string sValue)[] arrField);	// 원문 레코드 1줄 (원문 시간, 원문 (받은 줄 그대로), 원문 바이트 위치·길이, Type, 추가 필드 (클라 aField))

class HTDump	// 덤프 탭: 덤프 목록 (여러 머신 동시)·다운 (LMServer Dump 폴더 zip)·zip 받기·삭제 API (페이지 쪽: IndexHtml 의 HTDump)
{
	private const int	m_nDumpResultMs		= 60000;			// 끝난 다운 결과 보관 (ms, 지남: 버리고 zip 유무로 판단, 조회 안 한 것도 정리)

	private readonly ClientListener		m_kListener;		// LMClient TCP 수신 (HTWeb 이 넘김)

	private readonly ConcurrentDictionary<string, DumpSave>	m_hmapDumpSave = new ConcurrentDictionary<string, DumpSave>(StringComparer.OrdinalIgnoreCase);	// 다운 중·끝난 덤프 zip → 진행·결과 (같은 파일 동시 다운 방지, 끝난 것은 다음 조회에 알리고 해제)

	public HTDump(ClientListener kListener)
	{
		m_kListener = kListener;

		try	// 앞 실행에서 남은 받는 중 zip (서비스 중지·종료로 끝나지 못함) 정리
		{
			string sDumpDir = Path.Combine(GetBaseDir(), "Dump");

			if (Directory.Exists(sDumpDir))
			{
				foreach (string sTemp in Directory.EnumerateFiles(sDumpDir, "*.zip.tmp", SearchOption.AllDirectories))
				{
					File.Delete(sTemp);
				}
			}
		}
		catch (Exception kException)	// 사용 중·권한 없음: 다음 실행 때 다시
		{
			HTLog.Write(WARN_, $"덤프 임시 파일 정리 실패: {kException.Message}");
		}
	}

	public void MapRoute(WebApplication WebApp)	// 라우트 등록 (덤프 탭)
	{
		WebApp.MapPost("/api/dump", OnDump);					// 덤프 목록 (여러 머신 동시, 본문 JSON)
		WebApp.MapPost("/api/dump/download", OnDumpDownload);	// 덤프 다운 (머신 1대 → LMServer Dump 폴더 zip, 본문 JSON)
		WebApp.MapGet("/api/dump/zip", OnDumpZip);				// 다운한 덤프 zip 받기 (브라우저 로컬 저장)
		WebApp.MapPost("/api/dump/delete", OnDumpDelete);		// 덤프 삭제 (머신 1대, 본문 JSON)
	}

	private IResult OnDump(DumpParam kParam)	// 덤프 목록: 머신마다 클라에 Dump → 끝난 순서대로 한 줄씩 {nIndex, sMachine, sError, kResult} (NDJSON)
	{
		string[] arrMachine = kParam.arrMachine ?? Array.Empty<string>();	// 조회할 머신 (nIndex = 이 순서)

		if (arrMachine.Length > HTWeb.m_nMachineMax)	// 머신 수 초과: 조기 반환
		{
			return Results.BadRequest($"머신 수 초과 (최대 {HTWeb.m_nMachineMax})");
		}

		return Results.Stream(async (Stream kBody) =>
		{
			Task<(int nIndex, byte[]? arrResult, string sError)>[] arrTask = arrMachine.Select((sMachine, nIndex) => RequestDump(nIndex, sMachine)).ToArray();	// 모든 머신 동시 요청

			await foreach (Task<(int nIndex, byte[]? arrResult, string sError)> kTask in Task.WhenEach(arrTask))	// 끝난 순서대로 전송
			{
				(int nIndex, byte[]? arrResult, string sError) = await kTask;

				await HTWeb.WriteSearchLine(kBody, nIndex, arrMachine[nIndex], sError, arrResult);
			}
		}, "application/x-ndjson; charset=utf-8");
	}

	private async Task<(int nIndex, byte[]? arrResult, string sError)> RequestDump(int nIndex, string sMachine)	// 머신 1대 덤프 목록 요청 (nIndex: 결과 순서)
	{
		if (string.IsNullOrEmpty(sMachine))
		{
			return (nIndex, null, "조회 조건 오류");
		}

		(byte[]? arrResult, string sError) = await m_kListener.Request(sMachine, new Dictionary<string, object> { ["sType"] = "Dump" }, HTWeb.m_nDumpWaitSec);

		return (nIndex, MarkDumpZip(sMachine, arrResult), sError);
	}

	private static byte[]? MarkDumpZip(string sMachine, byte[]? arrResult)	// 덤프 목록 결과에 LMServer zip 유무 추가 (arrFile[].bZip, 결과 없음·형식 오류: 그대로)
	{
		if (arrResult == null)
		{
			return null;
		}

		try
		{
			JsonNode? kRoot = JsonNode.Parse(arrResult);	// 목록은 최대 m_nDumpMax 건 (검색 결과와 달리 작음 → 다시 직렬화)

			if (kRoot?["arrFile"] is not JsonArray arrFile)	// 오류 결과 (목록 없음)
			{
				return arrResult;
			}

			foreach (JsonObject kFile in arrFile.OfType<JsonObject>())
			{
				string sZip = GetDumpZip(sMachine, kFile["sPath"]?.GetValue<string>() ?? "");	// 다운 때와 같은 zip 경로

				kFile["bZip"] = sZip != "" && File.Exists(sZip);
			}

			return JsonSerializer.SerializeToUtf8Bytes(kRoot, HTWeb.m_kJsonOption);
		}
		catch (Exception)	// 형식 오류: 그대로 (bZip 없음 → 압축)
		{
			return arrResult;
		}
	}

	private IResult OnDumpDownload(DumpParam kParam, HttpRequest kRequest)	// 덤프 다운 시작·진행 조회: 바로 응답 (페이지 재호출 → ToolManager·IIS 요청 시간 제한 회피), 저장: SaveDump 가 따로 끝까지 (이미 있음: 바로 완료)
	{
		string sMachine	= kParam.sMachine ?? "";
		string sPath	= kParam.sPath ?? "";
		string sZip		= GetDumpZip(sMachine, sPath);	// 저장할 zip (조건 오류: "")

		if (sZip == "")
		{
			return MakeDumpSave(sMachine, "다운 조건 오류", sZip, null);
		}

		foreach (KeyValuePair<string, DumpSave> kPair in m_hmapDumpSave)	// 끝난 지 오래된 결과 정리 (조회 안 한 것 메모리 잔류 방지)
		{
			if (kPair.Value.m_bEnd && Environment.TickCount64 - kPair.Value.m_nEndTick > m_nDumpResultMs)
			{
				m_hmapDumpSave.TryRemove(kPair);
			}
		}

		if (m_hmapDumpSave.TryGetValue(sZip, out DumpSave? kSave))	// 다운 중·끝남
		{
			if (!kSave.m_bEnd)	// 다운 중: 진행
			{
				return MakeDumpSave(sMachine, "", sZip, kSave);
			}

			m_hmapDumpSave.TryRemove(new KeyValuePair<string, DumpSave>(sZip, kSave));	// 끝난 결과는 한 번만 알림

			if (Environment.TickCount64 - kSave.m_nEndTick <= m_nDumpResultMs)	// 방금 끝남: 결과 (오래된 결과는 버리고 아래에서 다시 판단)
			{
				return MakeDumpSave(sMachine, kSave.m_sError, sZip, kSave);
			}
		}

		if (File.Exists(sZip))	// 이미 받은 덤프: 바로 완료
		{
			return MakeDumpSave(sMachine, "", sZip, null);
		}

		kSave = new DumpSave();

		if (m_hmapDumpSave.TryAdd(sZip, kSave))	// 다운 시작 (같은 파일 동시 요청: 먼저 등록한 쪽만 저장, 나머지는 다음 조회에서 그 진행)
		{
			_ = SaveDump(sMachine, sPath, sZip, HTWeb.GetAddr(kRequest.HttpContext), kSave);	// 페이지와 무관하게 끝까지 저장 (예외는 SaveDump 안에서 처리)

			if (kSave.m_bEnd)	// 바로 끝남 (미접속 등): 결과는 이번에 알림
			{
				m_hmapDumpSave.TryRemove(new KeyValuePair<string, DumpSave>(sZip, kSave));
			}
		}

		return MakeDumpSave(sMachine, "", sZip, kSave);
	}

	private async Task SaveDump(string sMachine, string sPath, string sZip, string sAddr, DumpSave kSave)	// 덤프 조각 차례로 받아 .tmp zip 에 압축 → 다 받으면 sZip 으로 이름 변경 (실패: 임시 파일 삭제), 진행·결과: kSave
	{
		string	sTemp	= sZip + ".tmp";				// 받는 중인 zip (완료 전에는 sZip 없음 → 이미 받음 판정 안 됨)
		long	nSize	= 0;							// 원본 크기 (첫 조각 때)
		long	nOffset	= 0;							// 받은 크기
		string	sError	= "";							// 오류 (없음: "")
		long	nBegin	= Stopwatch.GetTimestamp();

		try
		{
			(byte[]? arrData, long nSize, string sError) kChunk = await HTWeb.RequestRead(m_kListener, "DumpRead", sMachine, sPath, 0, HTWeb.m_nDumpChunk);	// 첫 조각 (파일 크기 확인)

			if (kChunk.arrData == null)	// 미접속·허용 안 된 경로·읽기 실패
			{
				throw new RelayException(kChunk.sError);
			}

			nSize			= kChunk.nSize;
			kSave.m_nSize	= nSize;

			Directory.CreateDirectory(Path.GetDirectoryName(sZip)!);	// Dump\머신 (없으면 생성)

			HTLog.Write(INFO_, $"덤프 다운 시작: {sMachine} {sPath} ({nSize:N0} bytes, {sAddr})");

			using (FileStream kFile = new FileStream(sTemp, FileMode.Create, FileAccess.Write, FileShare.None))
			using (ZipArchive kZip = new ZipArchive(kFile, ZipArchiveMode.Create))
			{
				using Stream kEntry = kZip.CreateEntry(Path.GetFileName(sPath), CompressionLevel.Optimal).Open();	// zip 안 덤프 (압축: LMServer, 게임 서버 CPU 안 씀)

				while (true)
				{
					await kEntry.WriteAsync(kChunk.arrData);

					nOffset			+= kChunk.arrData.Length;
					kSave.m_nOffset	= nOffset;

					if (nOffset >= nSize)	// 다 받음 (첫 조각 때 크기까지만)
					{
						break;
					}

					kChunk = await HTWeb.RequestRead(m_kListener, "DumpRead", sMachine, sPath, nOffset, (int)Math.Min(HTWeb.m_nDumpChunk, nSize - nOffset));

					if (kChunk.arrData == null || kChunk.arrData.Length == 0)	// 중간 오류 (끊김·삭제·크기 줄어듦)
					{
						throw new RelayException(kChunk.sError == "" ? "No Data" : kChunk.sError);
					}
				}
			}

			File.Move(sTemp, sZip, true);
		}
		catch (Exception kException)	// 조각 요청·파일 쓰기 실패
		{
			sError = GetErrorText(kException, $"덤프 다운 {sMachine} {sPath}");

			try
			{
				File.Delete(sTemp);
			}
			catch (Exception)	// 폴더 없음 등: 임시 파일 없음
			{
			}

			DeleteEmptyDumpDir(sZip);	// 이번에 만든 Dump\머신 폴더가 비면 삭제
		}

		HTLog.Write(sError == "" ? INFO_ : WARN_, $"덤프 다운 {(sError == "" ? "완료" : "실패")}: {sMachine} {sPath} → {sZip} ({nOffset:N0}/{nSize:N0} bytes, {(long)Stopwatch.GetElapsedTime(nBegin).TotalMilliseconds}ms, {sAddr}){(sError == "" ? "" : $" ({sError})")}");

		kSave.m_sError		= sError;
		kSave.m_nEndTick	= Environment.TickCount64;
		kSave.m_bEnd		= true;	// 마지막에 (조회 쪽은 m_bEnd 를 본 뒤 오류·시각을 읽음), 해제는 다음 조회
	}

	private static IResult OnDumpZip(string? sMachine, string? sPath, HttpRequest kRequest)	// 다운한 덤프 zip 을 브라우저로 전송 (첨부 파일, Dump\머신\덤프 파일명.zip 만, 없음·다운 중: 404)
	{
		string sZip = GetDumpZip(sMachine ?? "", sPath ?? "");	// 다운한 zip (조건 오류: "")

		if (sZip == "" || !File.Exists(sZip))	// 조건 오류·아직 다운 안 함 (다운 중은 .tmp 만 있음)
		{
			return Results.NotFound("다운한 zip 없음");
		}

		HTLog.Write(INFO_, $"덤프 zip 받기: {sZip} ({HTWeb.GetAddr(kRequest.HttpContext)})");

		return Results.File(sZip, "application/zip", Path.GetFileName(sZip));
	}

	private static string GetDumpZip(string sMachine, string sPath)	// 덤프 저장 zip: 기준 폴더 하위 Dump\머신\덤프 파일명.zip (머신·경로 오류: "")
	{
		string sName = Path.GetFileName(sPath);	// 덤프 파일명 (폴더 제외 → 기준 폴더 밖 경로 불가)

		if (sMachine == "" || sMachine.Length > 255 || sMachine.Trim('.') == "" || sMachine.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || sPath.Length > 1000 || sName.Length <= 4 || !sName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))	// 머신명이 폴더명으로 안 됨·덤프 아님
		{
			return "";
		}

		string sDumpDir	= Path.GetFullPath(Path.Combine(GetBaseDir(), "Dump"));								// 덤프 저장 폴더
		string sZip		= Path.GetFullPath(Path.Combine(sDumpDir, sMachine, Path.ChangeExtension(sName, ".zip")));	// 저장할 zip

		return string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(sZip)), sDumpDir, StringComparison.OrdinalIgnoreCase) ? sZip : "";	// Dump\머신\ 바로 아래만 (".. " 처럼 정리되면 밖이 되는 이름: 제외)
	}

	private static IResult MakeDumpSave(string sMachine, string sError, string sZip, DumpSave? kSave)	// 덤프 다운 응답 {sMachine, sError, sZip (전체 경로), bDone, bExist (이미 있음), nSize·nOffset (원본·받은 바이트), nZipSize} (kSave null: 다운 안 함)
	{
		if (sError == "" && kSave != null && kSave.m_bEnd)	// 다운 끝: 그 오류 (조회 사이에 끝남·바로 실패)
		{
			sError = kSave.m_sError;
		}

		bool bDone		= sError == "" && (kSave == null || kSave.m_bEnd);					// 완료 (이미 있음·다운 끝)
		long nZipSize	= bDone && File.Exists(sZip) ? new FileInfo(sZip).Length : 0;		// zip 크기 (완료 전·오류: 0)

		return Results.Json(new { sMachine, sError, sZip, bDone, bExist = bDone && kSave == null, nSize = kSave?.m_nSize ?? 0, nOffset = kSave?.m_nOffset ?? 0, nZipSize });
	}

	private async Task<IResult> OnDumpDelete(DumpParam kParam, HttpRequest kRequest)	// 덤프 삭제 (머신 1대): 클라에 DumpDelete → 성공: 다운 zip·빈 Dump\머신 폴더도 삭제 → {sMachine, sError, sZipError, kResult} (요청자 IP 로그)
	{
		string sMachine	= kParam.sMachine ?? "";
		string sPath	= kParam.sPath ?? "";

		if (sMachine == "" || sPath == "" || sPath.Length > 1000)
		{
			return HTWeb.MakeResult(sMachine, "삭제 조건 오류", null);
		}

		(byte[]? arrResult, string sError) = await m_kListener.Request(sMachine, new Dictionary<string, object> { ["sType"] = "DumpDelete", ["sPath"] = sPath }, HTWeb.m_nDumpWaitSec);	// 경로 허용 검사는 클라에서

		string	sZip		= GetDumpZip(sMachine, sPath);	// 다운 zip (조건 오류: "")
		string	sZipError	= "";							// zip 삭제 오류 (없음·zip 없음: "")
		bool	bZipDeleted	= false;						// zip 삭제함
		bool	bDirDeleted	= false;						// 빈 Dump\머신 폴더 삭제함

		if (sError == "" && GetResultError(arrResult) == "" && sZip != "")	// 덤프 삭제 성공: 다운 zip·빈 폴더 삭제
		{
			try
			{
				if (File.Exists(sZip))
				{
					File.Delete(sZip);

					bZipDeleted = true;
				}
			}
			catch (Exception kException)	// 사용 중·권한 없음 (덤프는 이미 삭제됨)
			{
				sZipError = GetErrorText(kException, $"덤프 zip 삭제 {sZip}");
			}

			bDirDeleted = DeleteEmptyDumpDir(sZip);
		}

		HTLog.Write(NOTI_, $"덤프 삭제 요청: {sMachine} {sPath} ({HTWeb.GetAddr(kRequest.HttpContext)}){(sError == "" ? "" : $" (오류: {sError})")}{(bZipDeleted ? $" (zip 삭제: {sZip})" : "")}{(sZipError == "" ? "" : $" (zip 삭제 실패: {sZip}, {sZipError})")}{(bDirDeleted ? $" (빈 폴더 삭제: {Path.GetDirectoryName(sZip)})" : "")}");

		return Results.Stream((Stream kBody) => HTWeb.WriteResult(kBody, new { sMachine, sError, sZipError }, arrResult), "application/json; charset=utf-8");
	}

	private static bool DeleteEmptyDumpDir(string sZip)	// zip 이 있던 Dump\머신 폴더 안에 파일·폴더가 없으면 삭제 (반환: 삭제함, 남은 파일 있음·실패: false)
	{
		string? sDir = Path.GetDirectoryName(sZip);	// Dump\머신

		try
		{
			if (sDir == null || !Directory.Exists(sDir) || Directory.EnumerateFileSystemEntries(sDir).Any())	// 폴더 없음·남은 파일 있음
			{
				return false;
			}

			Directory.Delete(sDir);	// 하위 포함 안 함 (그 사이 다른 다운이 파일을 만들면 실패 → 유지)

			return true;
		}
		catch (Exception)	// 다른 다운이 사용 중 등: 유지
		{
			return false;
		}
	}

	private static string GetResultError(byte[]? arrResult)	// 클라 결과의 sError (없음: "", 결과 없음·형식 오류: 오류 문구)
	{
		if (arrResult == null)
		{
			return "결과 없음";
		}

		try
		{
			using JsonDocument kDoc = JsonDocument.Parse(arrResult);

			return kDoc.RootElement.GetProperty("sError").GetString() ?? "";
		}
		catch (Exception)	// 결과 형식 오류
		{
			return "결과 형식 오류";
		}
	}
}

class HTFile	// 파일 탭: 클라 감시 폴더 보기·파일 다운 (조각마다 브라우저로 그대로) API (페이지 쪽: IndexHtml 의 HTFile)
{
	private readonly ClientListener		m_kListener;		// LMClient TCP 수신 (HTWeb 이 넘김)

	public HTFile(ClientListener kListener)
	{
		m_kListener = kListener;
	}

	public void MapRoute(WebApplication WebApp)	// 라우트 등록 (파일 탭)
	{
		WebApp.MapGet("/api/files", OnFiles);					// 폴더 보기 (머신 1대, 파일 1번 창 트리)
		WebApp.MapGet("/api/files/download", OnFileDownload);	// 파일 다운 (머신 1대, 브라우저로 그대로)
	}

	private async Task OnFileDownload(string? sMachine, string? sPath, HttpResponse kResponse)	// 파일 다운 (머신 1대): 클라에 FileRead 조각 (HTWeb.m_nDumpChunk) 마다 요청 → 그대로 브라우저로 (첨부, 압축 안 함, 첫 조각 받으면 바로 응답 → ToolManager 시간 제한 회피)
	//   첫 조각 오류: 404 + 오류 문구, 중간 오류: 연결 끊음, 크기: 첫 조각 때 (이후 커져도 그 크기까지)
	{
		if (string.IsNullOrEmpty(sMachine) || string.IsNullOrEmpty(sPath) || sPath.Length > 1000)
		{
			kResponse.StatusCode = StatusCodes.Status400BadRequest;

			await kResponse.WriteAsync("다운 조건 오류");

			return;
		}

		string											sAddr	= HTWeb.GetAddr(kResponse.HttpContext);					// 요청자 IP
		CancellationToken								kAbort	= kResponse.HttpContext.RequestAborted;					// 브라우저 다운 취소
		(byte[]? arrData, long nSize, string sError)	kChunk	= await HTWeb.RequestRead(m_kListener, "FileRead", sMachine, sPath, 0, HTWeb.m_nDumpChunk);	// 첫 조각 (파일 크기 확인)

		if (kChunk.arrData == null)	// 첫 조각 오류 (미접속·허용 안 된 경로·읽기 실패): 응답 전 → 오류 문구
		{
			HTLog.Write(WARN_, $"파일 다운 실패: {sMachine} {sPath} ({kChunk.sError}, {sAddr})");

			kResponse.StatusCode = StatusCodes.Status404NotFound;

			await kResponse.WriteAsync($"다운 실패: {kChunk.sError}");

			return;
		}

		Microsoft.Net.Http.Headers.ContentDispositionHeaderValue kDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");	// 첨부 파일 (한글 파일명은 filename* 로)

		kDisposition.SetHttpFileName(Path.GetFileName(sPath));

		kResponse.ContentType					= "application/octet-stream";
		kResponse.ContentLength					= kChunk.nSize;	// 브라우저 진행률 표시
		kResponse.Headers.ContentDisposition	= kDisposition.ToString();
		kResponse.Headers.CacheControl			= "no-store";

		HTLog.Write(INFO_, $"파일 다운: {sMachine} {sPath} ({kChunk.nSize:N0} bytes, {sAddr})");

		long nSize		= kChunk.nSize;	// 보낼 전체 크기
		long nOffset	= 0;			// 보낸 크기

		try
		{
			while (true)
			{
				await kResponse.Body.WriteAsync(kChunk.arrData, kAbort);

				nOffset += kChunk.arrData.Length;

				if (nOffset >= nSize)	// 다 보냄
				{
					break;
				}

				kChunk = await HTWeb.RequestRead(m_kListener, "FileRead", sMachine, sPath, nOffset, (int)Math.Min(HTWeb.m_nDumpChunk, nSize - nOffset));

				if (kChunk.arrData == null || kChunk.arrData.Length == 0)	// 중간 오류 (끊김·삭제·크기 줄어듦): 연결 끊음
				{
					HTLog.Write(WARN_, $"파일 다운 중단: {sMachine} {sPath} ({nOffset:N0}/{nSize:N0} bytes, {(kChunk.sError == "" ? "읽은 바이트 없음" : kChunk.sError)}, {sAddr})");

					kResponse.HttpContext.Abort();

					return;
				}
			}
		}
		catch (OperationCanceledException) when (kAbort.IsCancellationRequested)	// 브라우저가 다운 취소
		{
			HTLog.Write(INFO_, $"파일 다운 취소: {sMachine} {sPath} ({nOffset:N0}/{nSize:N0} bytes, {sAddr})");
		}
	}

	private async Task<IResult> OnFiles(string? sMachine, string? sPath)	// 폴더 보기 (머신 1대): 클라에 Files → kResult 그대로 (sPath "": 클라 aLogFolder 목록, 오류: sError, 경로 허용 검사: 클라)
	{
		if (string.IsNullOrEmpty(sMachine) || (sPath ?? "").Length > 1000)
		{
			return HTWeb.MakeResult(sMachine, "조회 조건 오류", null);
		}

		(byte[]? arrResult, string sError) = await m_kListener.Request(sMachine, new Dictionary<string, object> { ["sType"] = "Files", ["sPath"] = sPath ?? "" }, HTWeb.m_nViewWaitSec);

		return HTWeb.MakeResult(sMachine, sError, arrResult);
	}
}

class HTMain	// 진입점
{
	private const int	m_nStartFailWaitSec	= 10;	// 시작 실패 시 콘솔 종료 대기 (초, 더블클릭 실행에서 사유 확인)

	static int Main(string[] args)	// 인자 검사 후 서버 실행 (0: 정상 종료, 1: 인자·설정 오류·시작 실패)
	{
		Console.OutputEncoding = Encoding.UTF8;	// 한글 콘솔 출력

		if (!HTConfig.Load())	// 설정 파일 로딩 (형식 오류: 종료)
		{
			return 1;
		}

		HTWeb? kWeb = null;	// 웹 서버 (설정 Base:bWeb 가 false·키 없음: null)

		if (HTConfig.GetBool("Base:bWeb"))	// Base:bWeb true 일 때만
		{
			HTLog.Write(INFO_, "웹: 실행 (설정 Base:bWeb 가 true)");

			int nPort = GetDefaultPort();	// 포트 (1번 인자)

			if (args.Length > 0 && (!int.TryParse(args[0], out nPort) || nPort <= 0 || nPort > 65535))
			{
				HTLog.Write(ERROR, $"포트 오류: {args[0]} (1~65535)");

				return 1;
			}

			string sHost = args.Length > 1 ? args[1] : "localhost";	// 호스트 (2번 인자)

			kWeb = new HTWeb(nPort, sHost);
		}

		HTTimer.Start();	// 타이머 시작
		HTAlarm.Start(HTQuery.GetAlarmList());	// 알람 통계·발송 시작

		Execute();

		try
		{
			Service(kWeb);	// 서비스 SCM 연동 + 호스트 실행 (웹 서버는 호스트와 함께 시작·중지)
		}
		catch (Exception kException)	// 호스트 시작 실패 (웹 포트 사용 중 등): 사유 기록 후 종료
		{
			HTLog.Write(FATAL, $"서버 시작 실패: {GetStartError(kException)}");

			if (!WindowsServiceHelpers.IsWindowsService() && Environment.UserInteractive)	// 콘솔 실행: 사유 볼 수 있게 대기 후 종료
			{
				HTLog.Write(INFO_, $"{m_nStartFailWaitSec}초 후 종료");

				Thread.Sleep(m_nStartFailWaitSec * 1000);
			}

			return 1;
		}
		finally	// 호스트 시작 실패 (포트 사용 중 등) 도 남은 색인 저장
		{
			HTTimer.Stop();	// 타이머 중지 (진행 중인 Batch 완료 대기)

			HTQuery.Close();	// 남은 색인 버퍼 모두 세그먼트 파일로 (타이머 중지 후)

			HTAlarm.Stop();	// 알람 통계 중지·남은 발송·펜딩 저장 (적재 끝난 뒤)
		}

		return 0;
	}

	static string GetStartError(Exception kException)	// 시작 실패 사유 (포트 사용 중: 이미 실행 중인 같은 exe PID 포함, 그 외: 예외 메시지)
	{
		bool bInUse = false;	// 포트 사용 중

		for (Exception? kItem = kException; kItem != null; kItem = kItem.InnerException)
		{
			if (kItem is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
			{
				bInUse = true;

				break;
			}
		}

		if (!bInUse)
		{
			return kException.Message;
		}

		string sRunning = string.Join("·", Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Where(kProcess => kProcess.Id != Environment.ProcessId).Select(kProcess => kProcess.Id));	// 이미 실행 중인 같은 이름 프로세스

		return $"웹 포트 사용 중 ({kException.Message}{(sRunning == "" ? ", 다른 프로그램이 사용 중" : $", 이미 실행 중인 {Process.GetCurrentProcess().ProcessName} PID {sRunning}")})";
	}

	static void Execute()
	{
		HTLog.Write(NOTI_, "앱: 실행");	// 차후 작업할 코드 아래에 추가
	}

	static void Service(HTWeb? pWeb)	// 서비스 SCM 연동 + 호스트 실행 (웹 서버: 호스트와 함께 시작·중지, 서비스 중지·Ctrl+C 까지 대기)
	{
		HostApplicationBuilder kBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
		{
			ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,	// 서비스 실행 시 기준 폴더 = exe 폴더
		});

		kBuilder.Services.AddWindowsService();				// 서비스 실행 시 SCM 연동: 시작 응답·중지 요청 수신 (콘솔·dotnet run 실행은 영향 없음)
		kBuilder.Logging.SetMinimumLevel(LogLevel.Warning);	// 기본 로그 최소화

		if (pWeb != null)	// 웹 서버: 호스트 시작·중지 시 함께 시작·중지 (서비스 중지 응답 전 웹 서버 먼저 중지)
		{
			kBuilder.Services.AddHostedService(_ => pWeb);
		}

		kBuilder.Build().Run();	// 시작 → 서비스 중지·Ctrl+C 대기 → 중지 → 해제 (SCM 시작·중지 응답 포함)
	}
}

class ClientListener	// LMClient TCP 수신: 한 줄 = JSON 1건 (Hello: 머신 등록·필드 순서, File: 콘솔 출력, Log: 원문 저장, Ping: 수신 시각 갱신, *Result: 요청 결과), 접속 목록 관리, 클라 요청(검색·보기)
{
	private readonly int									m_nPort;		// 수신 포트 (0: 사용 안 함)
	private readonly ConcurrentDictionary<long, ClientInfo>	m_hmapClient;	// 접속 클라 (접속 번호 → 정보)
	private long											m_nClientSeq;	// 접속 번호 발급
	private long											m_nReqSeq;		// 요청 번호 발급
	private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, SearchTrack>>	m_hmapTrack = new ConcurrentDictionary<string, ConcurrentDictionary<long, SearchTrack>>();	// 진행 중인 검색 (페이지 sToken → 머신별 진행 (요청 번호 → 진행))

	private const int	m_nIdleSec	= 90;					// 클라 무수신 제한 (초, 클라 Ping 주기 × 3, 초과: 연결 종료)
	private const int	m_nLineMax	= 1536 * 1024 * 1024;	// 수신 한 줄 최대 바이트 (클라가 글자 수 합 m_nSearchTextTotal 에서 끊음 → 한글 3바이트도 안쪽, 초과: 연결 종료)

	private static ClientListener?	m_kInstance;	// 수신 1개 (HTQuery 적재 부하 → 모든 클라에 Pause·Resume)

	public ClientListener(int nPort)
	{
		m_nPort			= nPort;
		m_hmapClient	= new ConcurrentDictionary<long, ClientInfo>();
		m_kInstance		= this;
	}

	public static int Broadcast(string sLine)	// 접속한 모든 클라에 한 줄 (Pause·Resume, 기다리지 않음, 반환: 보낸 클라 수)
	{
		int nCount = 0;	// 보낸 수

		foreach (ClientInfo kInfo in m_kInstance?.m_hmapClient.Values ?? Enumerable.Empty<ClientInfo>())
		{
			if (kInfo.m_kWriter != null)
			{
				SendLine(kInfo, sLine);

				++nCount;
			}
		}

		return nCount;
	}

	private static void SendLine(ClientInfo kInfo, string sLine)	// 클라 1대에 한 줄 (기다리지 않음, 전송 오류: 무시 → 끊김은 ReadLoop 가 처리)
	{
		_ = kInfo.m_kWriter?.WriteAsync(sLine, CancellationToken.None).ContinueWith(kTask => _ = kTask.Exception, TaskContinuationOptions.OnlyOnFaulted);
	}

	public int GetPort()	// 수신 포트
	{
		return m_nPort;
	}

	public bool Start(CancellationToken kStop)	// 수신 시작: 모든 인터페이스 바인딩 (원격 LMClient 접속) (false: nPort 미설정·범위 오류·포트 사용 중)
	{
		if (m_nPort <= 0 || m_nPort > 65535)
		{
			return false;
		}

		TcpListener kListener = new TcpListener(IPAddress.Any, m_nPort);

		try
		{
			kListener.Start();
		}
		catch (SocketException kException)	// 포트 사용 중 등
		{
			HTLog.Write(ERROR, $"TCP 수신 실패: {m_nPort} ({kException.Message})");

			return false;
		}

		kStop.Register(kListener.Stop);	// 웹 서버 종료 시 수신 중지

		_ = AcceptLoop(kListener, kStop);

		return true;
	}

	public List<object> GetClientList()	// 접속 클라 목록 (접속 순)
	{
		return m_hmapClient.OrderBy(kPair => kPair.Key).Select(kPair => (object)new
		{
			nId				= kPair.Key,
			sMachine		= kPair.Value.m_sMachine,
			sEndPoint		= kPair.Value.m_sEndPoint,
			sDir			= kPair.Value.m_sDir,
			sConnectTime	= kPair.Value.m_kConnectTime.ToString("yyyy-MM-dd HH:mm:ss"),
			sLastTime		= kPair.Value.m_kLastTime.ToString("yyyy-MM-dd HH:mm:ss"),
			nEventCount		= kPair.Value.m_nEventCount,
			sLastEvent		= kPair.Value.m_sLastEvent,
		}).ToList();
	}

	public async Task<(byte[]? arrResult, string sError)> Request(string sMachine, Dictionary<string, object> hmapRequest, int nWaitSec, ConcurrentDictionary<long, SearchTrack>? hmapTrack = null)	// 머신에 요청 → 결과 대기 (같은 머신 여러 접속: 최근 접속, 미접속·시간 초과·끊김: sError, hmapTrack: 진행 조회·중지)
	{
		ClientInfo? kInfo = m_hmapClient.Where(kPair => kPair.Value.m_kWriter != null && string.Equals(kPair.Value.m_sMachine, sMachine, StringComparison.OrdinalIgnoreCase)).OrderByDescending(kPair => kPair.Key).Select(kPair => kPair.Value).FirstOrDefault();

		if (kInfo == null || kInfo.m_kWriter == null)	// 접속 안 된 머신
		{
			return (null, "접속 안 된 머신");
		}

		long								nReqId	= Interlocked.Increment(ref m_nReqSeq);	// 요청 번호
		TaskCompletionSource<byte[]>		kWait	= new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);	// 결과 대기 (클라 결과 JSON 바이트)

		kInfo.m_hmapPending[nReqId]	= kWait;
		hmapRequest["nReqId"]		= nReqId;

		SearchTrack? kTrack = hmapTrack == null ? null : new SearchTrack(kInfo, nReqId);	// 진행 조회·중지 (페이지 sToken 의 머신별 진행)

		if (kTrack != null)
		{
			hmapTrack![nReqId]			= kTrack;
			kInfo.m_hmapTrack[nReqId]	= kTrack;
		}

		using CancellationTokenSource kTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(nWaitSec));	// 전송 + 결과 대기 시간 제한

		bool bSent = false;	// 요청 전송 완료

		try
		{
			await kInfo.m_kWriter.WriteAsync(JsonSerializer.Serialize(hmapRequest), kTimeout.Token);

			bSent = true;

			byte[] arrResult = await kWait.Task.WaitAsync(kTimeout.Token);

			return (arrResult, "");
		}
		catch (OperationCanceledException) when (kTimeout.IsCancellationRequested)	// 응답 없음
		{
			if (!bSent)	// 클라가 수신을 안 해 전송이 막힘: 연결 정리 (ReadLoop 종료 → 클라 재접속)
			{
				kInfo.m_kClient?.Close();
			}

			return (null, $"응답 시간 초과 ({nWaitSec}초)");
		}
		catch (Exception kException)	// 전송 실패·연결 끊김
		{
			return (null, GetErrorText(kException, $"클라 요청 {sMachine}"));
		}
		finally
		{
			kInfo.m_hmapPending.TryRemove(nReqId, out _);

			if (kTrack != null)	// 진행 등록 해제
			{
				hmapTrack!.TryRemove(nReqId, out _);
				kInfo.m_hmapTrack.TryRemove(nReqId, out _);
			}
		}
	}

	public ConcurrentDictionary<long, SearchTrack>? BeginTrack(string? sToken)	// 검색 1건 진행 등록 → 머신별 진행 보관 (sToken 없음·64자 초과: null → 진행 조회·중지 안 됨)
	{
		if (sToken == null || sToken.Length > 64)
		{
			return null;
		}

		ConcurrentDictionary<long, SearchTrack> hmapTrack = new ConcurrentDictionary<long, SearchTrack>();	// 머신별 진행 (요청 번호 → 진행)

		m_hmapTrack[sToken] = hmapTrack;

		return hmapTrack;
	}

	public void EndTrack(string? sToken, ConcurrentDictionary<long, SearchTrack>? hmapTrack)	// 검색 1건 진행 해제 (같은 sToken 의 다음 검색 것은 남김)
	{
		if (sToken != null && hmapTrack != null)
		{
			m_hmapTrack.TryRemove(new KeyValuePair<string, ConcurrentDictionary<long, SearchTrack>>(sToken, hmapTrack));
		}
	}

	public SearchTrack[] GetTrackList(string? sToken)	// 진행 중인 머신별 검색 (없음: 빈 배열)
	{
		return sToken != null && m_hmapTrack.TryGetValue(sToken, out ConcurrentDictionary<long, SearchTrack>? hmapTrack) ? hmapTrack.Values.ToArray() : Array.Empty<SearchTrack>();
	}

	public async Task<bool> Stop(string? sToken)	// 검색 중지: 진행 중인 머신마다 Stop 전송 (false: 진행 중인 검색 없음·전송 모두 실패)
	{
		bool[] arrSent = await Task.WhenAll(GetTrackList(sToken).Select(StopTrack));	// 머신별 전송 결과

		return arrSent.Contains(true);
	}

	private static async Task<bool> StopTrack(SearchTrack kTrack)	// 머신 1대 검색 중지: 해당 클라에 Stop 전송 (false: 전송 실패)
	{
		if (kTrack.m_kInfo.m_kWriter == null)
		{
			return false;
		}

		try
		{
			using CancellationTokenSource kTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));	// 전송 제한

			await kTrack.m_kInfo.m_kWriter.WriteAsync(JsonSerializer.Serialize(new { sType = "Stop", nReqId = kTrack.m_nReqId }), kTimeout.Token);

			return true;
		}
		catch (Exception)	// 연결 끊김·전송 시간 초과: 줄 일부 전송 가능 → 연결 정리 (깨진 줄 방지, 클라 재접속)
		{
			kTrack.m_kInfo.m_kClient?.Close();

			return false;
		}
	}

	private async Task AcceptLoop(TcpListener kListener, CancellationToken kStop)	// 접속 대기 → 클라마다 ReadLoop 실행
	{
		while (!kStop.IsCancellationRequested)
		{
			TcpClient kClient;	// 접속 클라

			try
			{
				kClient = await kListener.AcceptTcpClientAsync(kStop);
			}
			catch (Exception) when (kStop.IsCancellationRequested)	// 종료 요청
			{
				break;
			}
			catch (Exception kException)	// 일시 오류 (자원 부족 등): 잠깐 쉬고 계속 대기 (접속 대기가 끝나지 않게)
			{
				HTLog.Write(WARN_, $"TCP 접속 오류: {kException.Message}");

				try
				{
					await Task.Delay(1000, kStop);
				}
				catch (OperationCanceledException)	// 종료 요청
				{
					break;
				}

				continue;
			}

			_ = ReadLoop(kClient, kStop);
		}
	}

	private async Task ReadLoop(TcpClient kClient, CancellationToken kStop)	// 클라 한 명 수신: 줄 단위 처리 (끊김·m_nIdleSec 초 무수신·줄 길이 초과 시 종료 → 목록에서 제거)
	{
		long		nId		= Interlocked.Increment(ref m_nClientSeq);								// 접속 번호
		ClientInfo	kInfo	= new ClientInfo(kClient.Client.RemoteEndPoint?.ToString() ?? "");	// 접속 정보

		m_hmapClient[nId] = kInfo;

		HTLog.Write(INFO_, $"클라 접속: #{nId} {kInfo.m_sEndPoint} (접속 {m_hmapClient.Count}개)");

		try
		{
			using (kClient)
			{
				SetKeepAlive(kClient.Client);	// 클라가 FIN 없이 사라진 경우 감지

				NetworkStream				kStream	= kClient.GetStream();
				LineReader					kReader	= new LineReader(kStream, m_nLineMax);	// 줄 길이 제한 수신 (비정상 클라 메모리 고갈 방지)
				using CancellationTokenSource	kIdle	= CancellationTokenSource.CreateLinkedTokenSource(kStop);	// 무수신 시간 제한 (수신마다 다시 계산)

				kInfo.m_kClient	= kClient;
				kInfo.m_kWriter	= new LineWriter(kStream);	// 요청 전송 (검색·보기)

				kIdle.CancelAfter(TimeSpan.FromSeconds(m_nIdleSec));

				while (await kReader.ReadLineAsync(kIdle.Token) is ReadOnlyMemory<byte> kLine)
				{
					kIdle.CancelAfter(TimeSpan.FromSeconds(m_nIdleSec));

					LogBatch? kBatch = OnMessage(nId, kInfo, kLine);	// Log 건: 적재 라이트 큐로 (가득: 비워질 때까지 기다림 → 이 클라 수신만 늦어짐)

					if (kBatch != null)
					{
						await HTQuery.EnqueueAsync(kBatch, kStop);

						kIdle.CancelAfter(TimeSpan.FromSeconds(m_nIdleSec));	// 큐 가득으로 기다린 동안은 무수신 시간에서 뺌
					}
				}
			}
		}
		catch (OperationCanceledException) when (!kStop.IsCancellationRequested)	// 무수신 시간 초과
		{
			HTLog.Write(WARN_, $"클라 무응답: #{nId} {kInfo.m_sMachine} ({m_nIdleSec}초 무수신, 연결 종료)");
		}
		catch (Exception kException) when (!kStop.IsCancellationRequested)	// 끊김 (클라 강제 종료 등)·줄 길이 초과
		{
			HTLog.Write(WARN_, $"클라 오류: #{nId} {kInfo.m_sMachine} ({kException.Message})");
		}
		catch (Exception)	// 종료 요청
		{
		}
		finally
		{
			m_hmapClient.TryRemove(nId, out _);

			foreach (TaskCompletionSource<byte[]> kWait in kInfo.m_hmapPending.Values)	// 결과 대기 중인 요청: 끊김 처리
			{
				kWait.TrySetException(new RelayException("Client Off"));	// 클라 연결 끊김
			}

			HTLog.Write(INFO_, $"클라 종료: #{nId} {kInfo.m_sMachine} {kInfo.m_sEndPoint} (접속 {m_hmapClient.Count}개)");
		}
	}

	private static LogBatch? OnMessage(long nId, ClientInfo kInfo, ReadOnlyMemory<byte> kLine)	// 메시지 처리: 요청 결과: 바이트 그대로 전달, 작은 메시지만 파싱 (형식 오류: 출력 후 무시), 반환: Log 건 (그 외: null)
	{
		kInfo.m_kLastTime = GetNow();

		if (!TryGetHead(kLine.Span, out string sType, out long nReqId))	// JSON 형식 오류
		{
			HTLog.Write(WARN_, $"클라 형식 오류: #{nId} {kInfo.m_sEndPoint}");

			return null;
		}

		if (sType == "SearchProgress")	// 검색 진행 → 진행 기록
		{
			if (kInfo.m_hmapTrack.TryGetValue(nReqId, out SearchTrack? kTrack))
			{
				using JsonDocument kProgress = JsonDocument.Parse(kLine);

				kTrack.m_nFileTotal	= GetLong(kProgress.RootElement, "nFileTotal");
				kTrack.m_nFileDone	= GetLong(kProgress.RootElement, "nFileDone");
				kTrack.m_nFound		= GetLong(kProgress.RootElement, "nFound");
				kTrack.m_nElapsedMs	= GetLong(kProgress.RootElement, "nElapsedMs");
			}

			return null;
		}

		if (sType == "SearchResult" || sType == "ViewResult" || sType == "DumpResult" || sType == "DumpReadResult" || sType == "DumpDeleteResult" || sType == "FilesResult" || sType == "FileReadResult")	// 요청 결과 → 대기 중인 요청에 바이트 복사본 전달 (시간 초과 후 도착: 버림)
		{
			if (kInfo.m_hmapPending.TryRemove(nReqId, out TaskCompletionSource<byte[]>? kWait))
			{
				kWait.TrySetResult(kLine.ToArray());	// 수신 버퍼는 다음 줄에 재사용 → 복사
			}

			return null;
		}

		using JsonDocument kDoc = JsonDocument.Parse(kLine);	// 작은 메시지 (Hello·File·Log·Ping)

		return OnMessage(nId, kInfo, kDoc.RootElement);
	}

	private static bool TryGetHead(ReadOnlySpan<byte> kLine, out string sType, out long nReqId)	// JSON 형식 검사 (할당 없이 끝까지 읽음) + 최상위 sType·nReqId (없음: "", -1)
	{
		sType	= "";
		nReqId	= -1;

		try
		{
			Utf8JsonReader kReader = new Utf8JsonReader(kLine);	// 읽기 (형식 오류: JsonException)

			if (!kReader.Read() || kReader.TokenType != JsonTokenType.StartObject)	// 객체 아님
			{
				return false;
			}

			while (kReader.Read())
			{
				if (kReader.CurrentDepth != 1 || kReader.TokenType != JsonTokenType.PropertyName)	// 최상위 속성 이름만
				{
					continue;
				}

				if (kReader.ValueTextEquals("sType"u8))
				{
					kReader.Read();

					sType = kReader.TokenType == JsonTokenType.String ? kReader.GetString() ?? "" : "";
				}
				else if (kReader.ValueTextEquals("nReqId"u8))
				{
					kReader.Read();

					nReqId = kReader.TokenType == JsonTokenType.Number && kReader.TryGetInt64(out long nValue) ? nValue : -1;
				}
			}

			return true;
		}
		catch (JsonException)	// 형식 오류
		{
			return false;
		}
	}

	private static LogBatch? OnMessage(long nId, ClientInfo kInfo, JsonElement kRoot)	// 작은 메시지 종류별 처리 (kRoot 는 호출 끝나면 해제됨, 반환: Log 건 → 적재 큐, 그 외: null)
	{
		string sType = GetText(kRoot, "sType");	// 메시지 종류 (Hello·File·Log·Ping)

		if (sType == "Hello")	// 접속 직후 머신 등록·필드 순서
		{
			kInfo.m_sMachine	= GetText(kRoot, "sMachine");
			kInfo.m_sDir		= GetText(kRoot, "sDir");

			if (kRoot.TryGetProperty("arrField", out JsonElement kKeys) && kKeys.ValueKind == JsonValueKind.Array)	// 클라 aField 키 (설정 순서, 예전 클라: 없음)
			{
				try
				{
					HTQuery.SetFieldOrder(kKeys.EnumerateArray().Where(kKey => kKey.ValueKind == JsonValueKind.String).Select(kKey => (kKey.GetString() ?? "").Trim()).ToList());
				}
				catch (Exception kException) when (kException is IOException or UnauthorizedAccessException)	// field.txt 실패: 접속 유지
				{
					HTLog.Write(WARN_, $"쿼리 필드 순서 실패: {kException.Message}");
				}
			}

			HTLog.Write(INFO_, $"클라 등록: #{nId} {kInfo.m_sMachine}(폴더 {(kInfo.m_sDir == "" ? 0 : kInfo.m_sDir.Split('|').Length)}개)");	// sDir: 클라 aLogFolder 목록 ("|" 구분)

			if (HTQuery.IsIngestPaused())	// 밀림으로 멈춤 중: 이 클라도 멈춤 (풀리면 Resume 같이 받음)
			{
				SendLine(kInfo, JsonSerializer.Serialize(new { sType = "Pause" }));
			}

			return null;
		}

		if (sType == "Log")	// 로그 감시 줄 (aLogType 줄만) → Log 건 (호출 쪽이 HTQuery 적재 큐에)
		{
			List<string> listLine = new List<string>();	// 받은 줄

			if (kRoot.TryGetProperty("arrLine", out JsonElement kLines) && kLines.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement kLine in kLines.EnumerateArray())
				{
					if (kLine.ValueKind == JsonValueKind.String)
					{
						listLine.Add(kLine.GetString() ?? "");
					}
				}
			}

			List<long> listNo = new List<long>();	// 받은 줄 번호 (arrLine 과 같은 순서, 예전 클라: 없음)

			if (kRoot.TryGetProperty("arrNo", out JsonElement kNos) && kNos.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement kNo in kNos.EnumerateArray())
				{
					listNo.Add(kNo.ValueKind == JsonValueKind.Number && kNo.TryGetInt64(out long nNo) ? nNo : 0);
				}
			}

			List<(string sKey, string sValue)[]> listField = new List<(string sKey, string sValue)[]>();	// 받은 줄 추가 필드 (arrLine 과 같은 순서, 클라 aField, 예전 클라·aField 없음: 없음)

			if (kRoot.TryGetProperty("arrField", out JsonElement kFields) && kFields.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement kField in kFields.EnumerateArray())
				{
					listField.Add(HTQuery.ReadLineField(kField));
				}
			}

			return listLine.Count == 0 ? null : new LogBatch(kInfo.m_sMachine, GetText(kRoot, "sPath"), GetText(kRoot, "sFolder"), GetText(kRoot, "sZone"), listLine, listNo.Count == listLine.Count ? listNo : null, listField.Count == listLine.Count ? listField : null);	// sFolder: 클라 대상 폴더, sZone: 클라 Base:sZone, arrField: 클라 aField 값 (예전 클라: 없음)
		}

		if (sType == "File")	// 파일 이벤트 (Changed: 로그 쓰기마다 → 출력 안 함 (폭주 방지), 건수·마지막 이벤트만 갱신)
		{
			string sEvent	= GetText(kRoot, "sEvent");		// 이벤트 종류
			string sOldPath	= GetText(kRoot, "sOldPath");	// 이름 변경 전 경로
			string sMessage	= GetText(kRoot, "sMessage");	// 오류 메시지

			kInfo.m_nEventCount++;
			kInfo.m_sLastEvent = $"{sEvent} {GetText(kRoot, "sPath")}";

			if (sEvent == "Changed")
			{
				return null;
			}

			HTLog.Write(INFO_, $"[{kInfo.m_sMachine}] {kInfo.m_sLastEvent} (크기: {GetLong(kRoot, "nSize")}){(sOldPath == "" ? "" : $" (이전: {sOldPath})")}{(sMessage == "" ? "" : $" ({sMessage})")}");
		}

		return null;
	}

	private static string GetText(JsonElement kRoot, string sKey)	// 문자열 값 (객체 아님·키 없음·문자열 아님: "")
	{
		if (kRoot.ValueKind != JsonValueKind.Object || !kRoot.TryGetProperty(sKey, out JsonElement kValue) || kValue.ValueKind != JsonValueKind.String)
		{
			return "";
		}

		return kValue.GetString() ?? "";
	}

	private static long GetLong(JsonElement kRoot, string sKey)	// 정수 값 (객체 아님·키 없음·정수 아님: -1)
	{
		if (kRoot.ValueKind != JsonValueKind.Object || !kRoot.TryGetProperty(sKey, out JsonElement kValue) || kValue.ValueKind != JsonValueKind.Number || !kValue.TryGetInt64(out long nValue))
		{
			return -1;
		}

		return nValue;
	}
}

enum SEARCH_OP	// 검색 조건 연산 (SearchNode)
{
	SEARCH_OP_TERM	= 0,	// 검색어
	SEARCH_OP_AND	= 1,	// 왼쪽·오른쪽 모두
	SEARCH_OP_OR	= 2,	// 왼쪽·오른쪽 하나라도
	SEARCH_OP_NOT	= 3,	// 왼쪽이고 오른쪽 아님
}

class SearchNode	// 검색 조건 트리 (서치·쿼리 공통, LMClient SearchNode·페이지 HTKey 와 같은 규칙): 대문자 AND·OR·NOT 왼쪽부터, 괄호 안 먼저 (예: "A AND (B NOT C) OR D" = (A AND (B NOT C)) OR D)
//   연산자: 공백 + 대문자 AND·OR·NOT + 공백·여는 괄호 (소문자·단어 안: 검색어), 괄호: 검색어 자리 '(' 와 짝 ')' (검색어 안 괄호 Init(x): 그대로)
//   형식 오류 (연산자로 끝남·괄호 뒤 검색어 등): 전체를 검색어 하나로
{
	public readonly SEARCH_OP	m_eOp;		// 연산
	public readonly string		m_sTerm;	// 검색어 (SEARCH_OP_TERM 만, 그 외: "")
	public readonly SearchNode?	m_kLeft;	// 왼쪽 (연산만)
	public readonly SearchNode?	m_kRight;	// 오른쪽 (연산만)

	private SearchNode(SEARCH_OP eOp, string sTerm, SearchNode? kLeft, SearchNode? kRight)
	{
		m_eOp		= eOp;
		m_sTerm		= sTerm;
		m_kLeft		= kLeft;
		m_kRight	= kRight;
	}

	public static bool IsValid(string sKey)	// 검색어 형식 맞음 (빈 괄호·연산자로 시작·끝·빈 검색어: false, 페이지 HTKey.IsValid 와 같음)
	{
		string sTrim = sKey.Trim();

		if (sTrim == "")
		{
			return false;
		}

		List<(char cType, string sTerm)>?	listToken	= Tokenize(sTrim);	// 토큰 (null: 형식 오류)
		int									nIndex		= 0;				// 읽을 토큰

		return listToken != null && ParseExpr(listToken, ref nIndex) != null && nIndex == listToken.Count && !listToken.Any(kToken => kToken.cType == 'T' && Regex.IsMatch(kToken.sTerm.Trim(), @"^(AND|OR|NOT)(\s|$)|\s(AND|OR|NOT)$"));	// 검색어가 대문자 AND·OR·NOT 로 시작·끝남 (예: "A AND"): 연산자 빠짐으로 봄
	}

	public static SearchNode? Parse(string sKey)	// 검색어 → 조건 트리 (빈 검색어: null, 형식 오류: 전체를 검색어 하나로)
	{
		string sTrim = sKey.Trim();

		if (sTrim == "")
		{
			return null;
		}

		List<(char cType, string sTerm)>?	listToken	= Tokenize(sTrim);	// 토큰 (null: 형식 오류)
		int									nIndex		= 0;				// 읽을 토큰
		SearchNode?							kRoot		= listToken == null ? null : ParseExpr(listToken, ref nIndex);

		if (listToken == null || kRoot == null || nIndex != listToken.Count)	// 형식 오류: 전체를 검색어 하나로
		{
			return new SearchNode(SEARCH_OP.SEARCH_OP_TERM, sTrim, null, null);
		}

		return kRoot;
	}

	private static SearchNode? ParseExpr(List<(char cType, string sTerm)> listToken, ref int nIndex)	// 식: 항 (연산자 항)… 왼쪽부터 차례로 (null: 형식 오류)
	{
		SearchNode? kLeft = ParseTerm(listToken, ref nIndex);	// 지금까지 결과

		while (kLeft != null && nIndex < listToken.Count && listToken[nIndex].cType != ')')
		{
			SEARCH_OP eOp = listToken[nIndex].cType switch	// 연산자
			{
				'&'	=> SEARCH_OP.SEARCH_OP_AND,
				'|'	=> SEARCH_OP.SEARCH_OP_OR,
				'!'	=> SEARCH_OP.SEARCH_OP_NOT,
				_	=> SEARCH_OP.SEARCH_OP_TERM,
			};

			if (eOp == SEARCH_OP.SEARCH_OP_TERM)	// 연산자 자리에 다른 토큰
			{
				return null;
			}

			++nIndex;

			SearchNode? kRight = ParseTerm(listToken, ref nIndex);

			kLeft = kRight == null ? null : new SearchNode(eOp, "", kLeft, kRight);
		}

		return kLeft;
	}

	private static SearchNode? ParseTerm(List<(char cType, string sTerm)> listToken, ref int nIndex)	// 항: 검색어 또는 (식) (닫는 괄호 없이 끝나면 끝에서 닫힘, null: 형식 오류)
	{
		if (nIndex >= listToken.Count)
		{
			return null;
		}

		(char cType, string sTerm) = listToken[nIndex++];

		if (cType == 'T')
		{
			return sTerm == "" ? null : new SearchNode(SEARCH_OP.SEARCH_OP_TERM, sTerm, null, null);
		}

		if (cType != '(')
		{
			return null;
		}

		SearchNode? kInner = ParseExpr(listToken, ref nIndex);	// 괄호 안

		if (nIndex < listToken.Count && listToken[nIndex].cType == ')')
		{
			++nIndex;
		}

		return kInner;
	}

	private static List<(char cType, string sTerm)>? Tokenize(string sKey)	// 토큰 나누기: 검색어 T·AND &·OR |·NOT !·괄호 (null: 형식 오류)
	{
		List<(char cType, string sTerm)>	listToken	= new List<(char cType, string sTerm)>();
		int									nPos		= 0;	// 읽을 위치
		int									nDepth		= 0;	// 열린 괄호 수
		bool								bOperand	= true;	// 검색어·여는 괄호 차례 (false: 연산자·닫는 괄호 차례)

		while (true)
		{
			while (nPos < sKey.Length && char.IsWhiteSpace(sKey[nPos]))
			{
				++nPos;
			}

			if (nPos >= sKey.Length)
			{
				break;
			}

			if (bOperand && sKey[nPos] == '(')	// 여는 괄호
			{
				listToken.Add(('(', ""));

				++nDepth;
				++nPos;

				continue;
			}

			if (bOperand)	// 검색어: 다음 연산자·닫는 괄호 앞까지
			{
				int nEnd = FindTermEnd(sKey, nPos, nDepth);	// 검색어 끝

				listToken.Add(('T', sKey[nPos..nEnd].Trim()));

				nPos		= nEnd;
				bOperand	= false;

				continue;
			}

			if (sKey[nPos] == ')' && nDepth > 0)	// 닫는 괄호
			{
				listToken.Add((')', ""));

				--nDepth;
				++nPos;

				continue;
			}

			int nOpLength = GetOperator(sKey, nPos, out char cType);	// 연산자 길이 (0: 아님)

			if (nOpLength == 0)	// 연산자 자리에 다른 글자 (예: (A) B)
			{
				return null;
			}

			listToken.Add((cType, ""));

			nPos		+= nOpLength;
			bOperand	= true;
		}

		return bOperand ? null : listToken;	// 연산자·여는 괄호로 끝남: 형식 오류
	}

	private static int FindTermEnd(string sKey, int nStart, int nDepth)	// 검색어 끝: 공백 뒤 연산자·괄호 안 닫는 괄호 앞 (검색어 안 짝 맞는 괄호: 검색어 글자)
	{
		int nInner = 0;	// 검색어 안에서 열린 괄호 수 (예: Init(x))

		for (int nIndex = nStart; nIndex < sKey.Length; ++nIndex)
		{
			char cChar = sKey[nIndex];

			if (char.IsWhiteSpace(cChar))	// 공백 뒤 연산자·닫는 괄호면 여기서 끝
			{
				int nNext = nIndex;	// 공백 다음 글자

				while (nNext < sKey.Length && char.IsWhiteSpace(sKey[nNext]))
				{
					++nNext;
				}

				if (nNext < sKey.Length && (GetOperator(sKey, nNext, out _) > 0 || (nDepth > 0 && nInner == 0 && sKey[nNext] == ')' && IsClose(sKey, nNext))))
				{
					return nIndex;
				}

				nIndex = nNext - 1;

				continue;
			}

			if (cChar == '(')
			{
				++nInner;
			}
			else if (cChar == ')' && nInner > 0)
			{
				--nInner;
			}
			else if (cChar == ')' && nDepth > 0 && IsClose(sKey, nIndex))	// 괄호 안 검색어 끝
			{
				return nIndex;
			}
		}

		return sKey.Length;
	}

	private static bool IsClose(string sKey, int nPos)	// nPos 의 ')' 가 닫는 괄호 (뒤에 공백·')' 만 이어진 뒤 끝·연산자, 아님: 검색어 글자)
	{
		int nNext = nPos + 1;	// 뒤 글자

		while (nNext < sKey.Length && (char.IsWhiteSpace(sKey[nNext]) || sKey[nNext] == ')'))
		{
			++nNext;
		}

		return nNext >= sKey.Length || GetOperator(sKey, nNext, out _) > 0;
	}

	private static int GetOperator(string sKey, int nPos, out char cType)	// nPos 의 연산자 길이 (대문자 AND·OR·NOT + 공백·여는 괄호, 0: 아님), cType: & | !
	{
		foreach ((string sWord, char cOp) in new[] { ("AND", '&'), ("OR", '|'), ("NOT", '!') })
		{
			int nNext = nPos + sWord.Length;	// 연산자 뒤 글자

			if (string.CompareOrdinal(sKey, nPos, sWord, 0, sWord.Length) == 0 && nNext < sKey.Length && (char.IsWhiteSpace(sKey[nNext]) || sKey[nNext] == '('))
			{
				cType = cOp;

				return sWord.Length;
			}
		}

		cType = ' ';

		return 0;
	}
}

class ClientInfo	// 접속 클라 정보 (ReadLoop 1개만 갱신, /api/client 조회)
{
	public readonly string			m_sEndPoint;		// 접속 주소 (아이피:포트)
	public readonly DateTimeOffset	m_kConnectTime;		// 접속 시각 (KST)
	public DateTimeOffset			m_kLastTime;		// 마지막 수신 시각 (KST)
	public string					m_sMachine		= "";	// 머신명 (Hello)
	public string					m_sDir			= "";	// 클라 aLogFolder 목록 (Hello, "|" 구분)
	public long						m_nEventCount;		// 파일 이벤트 수
	public string					m_sLastEvent	= "";	// 마지막 파일 이벤트
	public LineWriter?				m_kWriter;			// 요청 전송 (접속 직후 설정)
	public TcpClient?				m_kClient;			// 연결 (요청 전송이 막히면 닫음)

	public readonly ConcurrentDictionary<long, SearchTrack>	m_hmapTrack = new ConcurrentDictionary<long, SearchTrack>();	// 진행 중인 검색 (요청 번호 → 진행)

	public readonly ConcurrentDictionary<long, TaskCompletionSource<byte[]>>	m_hmapPending = new ConcurrentDictionary<long, TaskCompletionSource<byte[]>>();	// 결과 대기 중인 요청 (요청 번호 → 결과 JSON 바이트 대기)

	public ClientInfo(string sEndPoint)
	{
		m_sEndPoint		= sEndPoint;
		m_kConnectTime	= GetNow();
		m_kLastTime		= m_kConnectTime;
	}
}

class SearchParam	// 로그 검색 요청 본문 (POST /api/search, JSON)
{
	public string[]?	arrMachine	{ get; set; }	// 검색할 머신 (결과 nIndex = 이 순서)
	public long?		nStart		{ get; set; }	// 시작 yyyyMMddHH
	public long?		nEnd		{ get; set; }	// 종료 yyyyMMddHH (24시 = 그날 23시 파일까지)
	public string?		sKey		{ get; set; }	// 검색어
	public int?			nMax		{ get; set; }	// 머신당 최대 건수 (페이지 설정값)
	public string?		sToken		{ get; set; }	// 진행 조회·중지용 (페이지가 만든 검색 표시)
}

class DumpParam	// 덤프 요청 본문 (POST /api/dump: arrMachine, /api/dump/download·delete: sMachine·sPath, JSON)
{
	public string[]?	arrMachine	{ get; set; }	// 조회할 머신 (결과 nIndex = 이 순서)
	public string?		sMachine	{ get; set; }	// 다운·삭제할 머신
	public string?		sPath		{ get; set; }	// 다운·삭제할 덤프 (클라 전체 경로)
}

class SavedParam	// 저장 쿼리 본문 (POST /api/query/saved: sKey·sQuery·nMinute·sOldKey, /api/query/saved/delete: sKey, /api/query/saved/move: sKey·nTo, JSON)
{
	public string?	sKey	{ get; set; }	// 제목
	public string?	sQuery	{ get; set; }	// 쿼리
	public string?	sOldKey	{ get; set; }	// 수정할 저장 쿼리 제목 (비움: 추가·같은 제목이면 쿼리만 바꿈)
	public int?		nMinute	{ get; set; }	// 간격 (분, 페이지 D)
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

class DumpSave	// 다운 중인 덤프 1개 (HTDump.SaveDump 가 갱신, 페이지가 /api/dump/download 로 진행 조회)
{
	public long				m_nSize;			// 원본 크기 (첫 조각 때, 0: 아직 모름)
	public long				m_nOffset;			// 받은 크기
	public string			m_sError	= "";	// 실패 오류 (없음: "")
	public long				m_nEndTick;			// 끝난 시각 (Environment.TickCount64)
	public volatile bool	m_bEnd;				// 끝남 (성공: zip 있음, 실패: m_sError) → 다음 조회에 결과 알린 뒤 해제
}

class SearchTrack	// 머신 1대 검색 진행 (클라가 갱신, /api/progress: 머신 합 조회, /api/stop: 모두 중지)
{
	public readonly ClientInfo	m_kInfo;				// 검색 중인 클라
	public readonly long		m_nReqId;				// 요청 번호
	public long					m_nFileTotal	= -1;	// 대상 파일 수 (-1: 아직 모름·앞 검색 대기 중)
	public long					m_nFileDone;			// 검색한 파일 수
	public long					m_nFound;				// 찾은 줄 수
	public long					m_nElapsedMs;			// 검색 시간 (ms)

	public SearchTrack(ClientInfo kInfo, long nReqId)
	{
		m_kInfo		= kInfo;
		m_nReqId	= nReqId;
	}
}

class LineWriter	// 줄 단위 전송 (여러 요청의 동시 전송 직렬화, 묶음 전송 시 flush 1회)
{
	private readonly StreamWriter	m_kWriter;	// UTF-8, '\n' 구분 (flush 는 WriteLinesAsync 끝에 1회)
	private readonly SemaphoreSlim	m_kLock;	// 전송 1건씩

	public LineWriter(Stream kStream)
	{
		m_kWriter	= new StreamWriter(kStream, new UTF8Encoding(false), 16 * 1024) { NewLine = "\n", AutoFlush = false };
		m_kLock		= new SemaphoreSlim(1, 1);
	}

	public Task WriteAsync(string sLine, CancellationToken kStop)	// 한 줄 전송
	{
		return WriteLinesAsync(new[] { sLine }, kStop);
	}

	public async Task WriteLinesAsync(IReadOnlyList<string> listLine, CancellationToken kStop)	// 여러 줄 전송 (끝에 flush 1회)
	{
		await m_kLock.WaitAsync(kStop);

		try
		{
			foreach (string sLine in listLine)
			{
				await m_kWriter.WriteLineAsync(sLine.AsMemory(), kStop);
			}

			await m_kWriter.FlushAsync(kStop);
		}
		finally
		{
			m_kLock.Release();
		}
	}
}

class LineReader	// 줄 단위 수신 ('\n' 구분, 한 줄 최대 바이트 제한 → 초과 시 InvalidDataException), 줄은 UTF-8 바이트 그대로 (문자열로 바꾸지 않음)
{
	private readonly Stream	m_kStream;		// 수신 스트림
	private readonly int	m_nMaxBytes;	// 한 줄 최대 바이트
	private readonly byte[]	m_arrBuffer;	// 수신 버퍼
	private int				m_nStart;		// 버퍼 안 처리 안 한 시작
	private int				m_nEnd;			// 버퍼 안 데이터 끝
	private MemoryStream	m_kLine;		// 버퍼보다 긴 줄 누적 (큰 줄 처리 후 작게 다시 생성)
	private bool			m_bLineUsed;	// 앞 줄이 m_kLine 을 사용 (다음 읽기 전에 비움)

	public LineReader(Stream kStream, int nMaxBytes)
	{
		m_kStream	= kStream;
		m_nMaxBytes	= nMaxBytes;
		m_arrBuffer	= new byte[64 * 1024];
		m_kLine		= new MemoryStream();
	}

	public async Task<ReadOnlyMemory<byte>?> ReadLineAsync(CancellationToken kStop)	// 한 줄 (끝 '\r' 제거, 연결 종료: null), 반환 메모리는 다음 ReadLineAsync 전까지만 유효
	{
		if (m_bLineUsed)	// 앞 줄 누적분 비움 (큰 줄 버퍼는 버려 메모리 반환)
		{
			m_kLine		= m_kLine.Capacity > 1024 * 1024 ? new MemoryStream() : m_kLine;
			m_bLineUsed	= false;

			m_kLine.SetLength(0);
		}

		while (true)
		{
			int nIndex = m_arrBuffer.AsSpan(m_nStart, m_nEnd - m_nStart).IndexOf((byte)'\n');	// 줄 끝 (처리 안 한 시작 기준)

			if (nIndex >= 0)
			{
				ReadOnlyMemory<byte> kLine;	// 이번 줄

				if (m_kLine.Length == 0)	// 버퍼 안에 다 있음: 복사 없이
				{
					kLine = m_arrBuffer.AsMemory(m_nStart, nIndex);
				}
				else
				{
					if (m_kLine.Length + nIndex > m_nMaxBytes)
					{
						throw new InvalidDataException($"수신 줄 길이 초과 ({m_nMaxBytes} bytes)");
					}

					m_kLine.Write(m_arrBuffer, m_nStart, nIndex);

					kLine		= m_kLine.GetBuffer().AsMemory(0, (int)m_kLine.Length);
					m_bLineUsed	= true;
				}

				m_nStart += nIndex + 1;

				return kLine.Length > 0 && kLine.Span[^1] == (byte)'\r' ? kLine.Slice(0, kLine.Length - 1) : kLine;
			}

			if (m_nEnd > m_nStart)	// 줄 끝 없음: 남은 부분 누적
			{
				m_kLine.Write(m_arrBuffer, m_nStart, m_nEnd - m_nStart);
			}

			m_nStart	= 0;
			m_nEnd		= 0;

			if (m_kLine.Length > m_nMaxBytes)	// 한 줄 최대 초과: 비정상 상대 → 연결 종료
			{
				throw new InvalidDataException($"수신 줄 길이 초과 ({m_nMaxBytes} bytes)");
			}

			int nRead = await m_kStream.ReadAsync(m_arrBuffer.AsMemory(), kStop);

			if (nRead == 0)	// 연결 종료
			{
				return null;
			}

			m_nEnd = nRead;
		}
	}
}

class RelayException(string sMessage) : Exception(sMessage)	// 상대(클라·서버)가 보낸 오류 글 그대로 (GetErrorText 가 바꾸지 않음)
{
}

# HMX

> 게임 서버 로그 수집·쿼리·알람 툴 그룹 — 머신별 에이전트 Client + 중앙 웹·TCP 서버 Server

## 개요
- 유형: 2개 프로젝트 솔루션 (Client: 콘솔·윈도우 서비스, Server: ASP.NET Core 웹 서버 + TCP 수신)
- 프레임워크: .NET 10 (net10.0) file-based app (`#:` 지시문), Microsoft.Extensions.Hosting.WindowsServices 10.0.10
- 역할: 게임 서버 머신 로그(`*yyyyMMdd_HH.log`)의 지정 레벨 줄 중앙 수집·역색인 → 웹 쿼리·통계·알람, 머신 원격 검색·파일 보기·덤프 수집

## 구성 관계
```
[게임 서버 머신 N대]                          [Server 머신]
 Client ── TCP 801 (JSON 한 줄) ────────▶ Server ── HTTP 800 ──▶ 브라우저
  aLogFolder 로그·덤프 감시   Hello·Count·Log·File·Ping    Data\Origin·Index·Alarm·Count   (직접·IIS·ToolManager 경유)
  원격 요청 처리   ◀── Search·View·Dump·Files·Pause/Resume   알람 → 메일(SMTP)·훅
```

## 구조
| 경로 | 설명 |
|---|---|
| HMX.slnx | 솔루션 (Server.csproj·Client.csproj + /Common/ 폴더) |
| Common/ | 서버·클라 공통 소스 → [Common.md](Common/Common.md) |
| ReadMe.txt | 서버 설치 메모: `icacls` 로 IIS_IUSRS 에 Json 폴더 수정 권한 부여 |
| .gitignore | .vs·.user·bin·obj·log·Properties 제외 |
| Client/ | 로그 감시 에이전트 → [Client.md](Client/Client.md) |
| Server/ | 중앙 웹·TCP 서버 → [Server.md](Server/Server.md) |

## 주요 기능
- 수집: Client 가 대상 폴더 최신 시간 로그의 aLogType 줄 (NOTI·ERROR·FATAL) + aField 추출 값 전송 (aField: Field.json 정의 → 접속 때 Count 답으로 받음), Count 동기화로 중복·누락 방지
- 적재·쿼리: Server 원문 저장 + 단어·필드 세그먼트 역색인 → 기간 쿼리, 시간·분·초별 건수, 필드별 통계
- 알람: 쿼리 조건 nMinute 분 nCount 건 이상 → 메일·Dooray 훅 (SMS: 설정만, 미구현)
- 서치: 여러 머신 원본 로그 파일 직접 검색 (Client 처리, 동시 2건·Throttle 부하 제한, 제한 시간 nLimit, 저장 서치 Search.json)
- 부하 분산 (Server Gate): 기능마다 세션 1건 → 전체 X건 (쿼리·서치·보기·덤프 다운·파일 다운 따로) + 스레드 양보 (연속 0.5초 → 0.1초 쉼, 클라 Throttle 과 같음) + 제한 시간 (페이지 설정 버튼: 서치·쿼리 기본 60초)
- 덤프·파일: 머신 `*.dmp` 목록·zip 수집·삭제, 로그 폴더 트리·파일 다운
- 운영 (ServerManager 이식, 서버 운영 탭): 게임 서버 XLO 모니터 점검·패치 시각·X분 동접 기록 (Redis)·동접 차트·모니터 (서버 목록: Server\Json\Manager.json)
- 흐름 제어: 서버 적재 밀림 시 전 클라 Pause → 안정 시 Resume

## 포트
| 구분 | 값 | 출처 |
|---|---|---|
| Server 웹 (HTTP) | 800 | 코드 기본값·On.bat (IIS: HTTP_PLATFORM_PORT) |
| Server TCP 수신 ↔ Client 접속 | 801 | 양쪽 json nPort (Client 키 없음 기본: 9091) |

## 공통 설계
- 진입 .cs (`#:sdk`·`#:package`·`#:include` 지시문, Main) + 클래스별 .cs (1 클래스 1 파일, 파일명 = 클래스명, HT 접두사 없음, 진입: `return Main.Run(args);` → 클래스 Main 안 진입 함수 이름 Main 불가) → `dotnet run --file` 단독 실행과 csproj 빌드가 같은 소스 공유
- 공통·기능 연결은 Main (서버: 라우트·TCP 시작·타이머 / 클라: 탭 준비 순서·서버 요청 분배·접속·Pause), 탭 기능: TabQuery·TabSearch·TabDump·TabFile (서버·클라 각각)
- 빌드 후 PublishExe 타깃 → bin\Release\net10.0 에 단일 exe (프레임워크 종속 win-x64, 워크스테이션 GC, Json 폴더 보존)
- 설정 Json\{프로젝트명}.json (모든 설정 json: 기준 폴더 하위 Json 폴더, 예전 위치 기준 폴더에만 있음: 그 파일 + 경고, bLog 등 최상위, Base 묶음 없음), `sCode` 키 → MySQL 호환 AES 복호화 → 같은 위치 `sPass` 로 메모리 보관, 경로 값 (`X:\…`·`\\…` 시작) 은 `\` 하나로 써도 됨 (Config.FixPathEscape, 서버·클라 공통)
- 웹에서 고치는 설정은 키별 json 따로 관리 (서버: aQuery → Query.json·aSearch → Search.json·aAlarm → Alarm.json·aField → Field.json, Main.m_arrConfigSplit)
- 로그 Log\yyyyMMdd\{프로젝트명}_yyyyMMdd_HH.log (레벨 DEBUG·INFO_·NOTI_·WARN_·ERROR·FATAL, bLog)
- 공통 클래스 (Common, 같이 씀): Global·Config·Log·AES·Count·Define (프로젝트별 값: 각 Main 상수), 프로젝트별: Timer·TCP (서버 TCPServer·클라 TCPClient)·SearchNode·LineWriter·LineReader (같은 규칙, 코드 다름)
- 검색어 규칙: 대문자 AND·OR·NOT·괄호, 왼쪽부터 (클라 SearchNode = 서버 SearchNode = 페이지 Key)
- 계정 관리 (공통 UI, 다른 툴 재사용 단위): Auth.cs + Web.cs 계정 팝업(400 × 550px 고정, 관리 계정 고정 + 10개씩 페이지)·추가/수정 창 + Account.json → 명세: Server.cs 머리 UI/UX 가이드 "# 화면 틀 → ## 계정 관리"

## 실행·배포
- 개발: HMX.slnx (Visual Studio 2026) F5, 상위 ServerTools.slnx 의 `/2.HMX/` 폴더에도 포함
- Server: On.bat·Off.bat (콘솔) / IIS (web.config, HttpPlatformHandler) / 윈도우 서비스
- Client: 각 게임 서버 머신 On.bat·Off.bat (콘솔) / 윈도우 서비스
- 런타임 설치 파일 동봉 (Server\Tool: dotnet·aspnetcore 10.0.13·httpPlatformHandler_amd64.msi, Client: dotnet 10.0.13)

## 연관
- 상위: [ServerTools.md](../ServerTools.md)
- 하위: [Client.md](Client/Client.md), [Server.md](Server/Server.md), [Common.md](Common/Common.md)
- 경유: [ToolManager.md](../ToolManager/ToolManager.md) (Server 프록시 경유 접속 대응)
- UI 기준: ServerManager·HMX·HTB·DataCompare·DataFinder·ToolManager 가 Server 공통 UI (Primer·로그인·힌트·존 색) 따름
- UI/UX 가이드: Server.cs 머리 **UI/UX Guidelines** (모든 툴 공통, 기준 구현 HMX: 디자인·존 색·화면 틀·명령 창·상자·표·팝업·입력·글·브라우저 저장·차트·목록 관리 화면), UX/UI 수정 때마다: 공통 기능으로 이 가이드에 같이 정리

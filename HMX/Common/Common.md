# Common

> HMX 공통 소스 — Server·Client 가 같이 쓰는 클래스 (Log·AES 처럼 공통으로 쓸 수 있는 것만)

## 구조
| 파일 | 클래스 | 역할 |
|---|---|---|
| Global.cs | Global | 전역 함수: 기본 포트·머신 이름 (sMachine)·프로젝트명·설정 파일·설정 폴더 (기준 폴더\Json)·기준 폴더·KST 시각·남은 물리 메모리·TCP keepalive·오류 두 단어 (GetErrorText)·JSON 값 (GetText·GetLong)·와일드카드 (TryGetWild·IsWildMatch: ab*·*ab·*ab*, 서버 쿼리·알람·클라 서치 공통)·대기 (Delay) |
| Config.cs | Config | 설정 Json\{프로젝트명}.json + 따로 관리 키 파일 (Main.m_arrConfigSplit, 예: aQuery → Query.json) 합쳐 로딩·조회 (문자열·정수·실수·bool·배열·하위 섹션)·최상위 배열 저장 (SetArray: 따로 관리 키 → 그 파일), sCode → sPass 복호화, 예전 형식: Base 묶음 → 최상위로 읽음·따로 관리 키가 기본 파일에만 있음 → 파일 만듦 (기본 파일 그대로)·Json 폴더에 없고 기준 폴더에 있음 → 그 파일 (경고) |
| Log.cs | Log | 시간별 로그 Log\yyyyMMdd\{프로젝트명}_yyyyMMdd_HH.log (bLog), 콘솔 레벨 색 |
| AES.cs | AES | MySQL AES-128-ECB 호환 암호화·복호화 (키 = 마스터 키 GetMasterKey: 메모리만, 서버: 로그인 사용 첫 접속 입력·안 씀 솔루션명 HMX, Client: 서버에서 받음 (TCP MasterKey)) |
| Count.cs | Count | 로그 감시 받은 진행 Data\Count\머신\대상 폴더\Count.data |
| Define.cs | Define·SEARCH_OP·SEARCH_STOP·RelayException·FieldRule·LogBatch·*Param·AlarmItem·SearchLine·LogField·DumpFile·FileItem·ChunkResult | 공유 상수 (머신 최대·대기·덤프 조각 4MB·폴더 깊이·블록 크기·JSON 옵션)·데이터 정의 (서버 요청 본문·클라 결과) |

## 사용
- 파일 기반 실행: 진입 .cs 의 `#:include ../../Common/{파일}.cs` (진입 소스 Source\Server.cs·Source\Client.cs, `dotnet run --file Source\Server.cs`)
- csproj 빌드: `<Compile Include="..\Common\*.cs" Link="Common\%(Filename)%(Extension)" />` (VS: 프로젝트 안 Common 폴더)
- 솔루션: HMX.slnx `/Common/`, ServerTools.slnx `/2.HMX/Common/`
- 기준: 서버·클라 코드가 같은 것만 (프로젝트 고유 상태·클래스 참조 없음), 같은 소스 수정 → 두 프로젝트 함께 빌드 확인

## 프로젝트별 값 (각 Main 상수)
| 상수 | Server | Client | 사용 |
|---|---|---|---|
| m_nDefaultPort | 800 (웹) | 9091 (Server TCP) | Global.GetDefaultPort |
| m_sPeerOff | Client Off | Server Off | Global.GetErrorText (상대 끊김) |
| m_sSelfError | Server Error | Client Error | Global.GetErrorText (그 밖 오류) |
| m_arrConfigSplit | aQuery → Query.json, aAlarm → Alarm.json, aField → Field.json, Manager → Manager.json | 없음 | Config 따로 관리 설정 (웹에서 고치는 키, 파일: `{"키": [...]}`) |

## 프로젝트가 줄 것
- Main 상수 4개 (위 표), global using (System.Net.Sockets·System.Runtime.InteropServices·System.Text.Json·System.Text.Json.Nodes·System.Text.Encodings.Web·Microsoft.Extensions.Hosting.WindowsServices·`using static Global`·`using static Log`)

## 연관
- 상위: [HMX.md](../HMX.md)
- 사용: [Server.md](../Server/Server.md), [Client.md](../Client/Client.md)

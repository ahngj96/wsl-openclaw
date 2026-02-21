# OpenClawWinManager

OpenClawWinManager는 Windows에서 WSL 기반 OpenClaw를 설치·기동·온보드·모니터링할 수 있게 돕는 데스크톱 도구입니다.  
처음 설치부터 토큰 설정, 게이트웨이 상태 확인까지 한 화면에서 처리하도록 만든 MVP 버전입니다.

---

## 핵심 기능

- WSL 배포판 조회
  - `wsl -l -q`로 WSL 배포판 목록을 읽어옵니다.
- OpenClaw 설치
  - 선택한 배포판에서 공식 설치 스크립트를 내려받아 실행합니다.
- Gateway 제어
  - 포트 지정 시작
  - OpenClaw 전체 프로세스 중지
- 온보드
  - 온보드 실행을 통해 대시보드 연동 준비
- 토큰 관리
  - `Get token` 버튼으로 `openclaw config get gateway.auth.token` 조회
  - 토큰 복사 기능
- 모니터링
  - 모니터 포트 등록 후 health 체크
  - 토큰 필요/연결 상태/무결성 상태를 요약 라벨로 표시
- 실시간 로그
  - WSL 실행 표준 출력/오류를 로그창으로 스트리밍
  - 실행 컨텍스트(`root`, `user`) 태그를 붙여 추적성 강화
- 상태 UI
  - 진행률바, 상태 라벨, 버튼 활성/비활성 자동 제어

---

## 실행 요구 사항

- Windows 10/11 (x64)
- WSL2 설치됨
- WSL 배포판 1개 이상 존재
- OpenClaw 설치(앱에서 설치 가능)

> WSL2 자체 설치/구성은 포함되지 않습니다.

---

## 사용 방법

### 1) 실행 파일 실행
```
bin/Release/net8.0-windows/win-x64/publish/OpenClawWinManager.exe
```

해당 exe는 Self-contained 단일 실행 파일이므로 대상 PC에 별도 .NET 런타임이 없어도 실행할 수 있습니다.

### 2) 기본 사용 흐름
1. 배포판 선택
2. 게이트웨이 포트 입력(기본값 `18789`)
3. 필요 시 `Check`로 배포판 존재 확인
4. 미설치 상태면 `Install then Onboard`
5. `Start Gateway`로 실행
6. `Add`로 모니터 포트 등록 후 `Monitoring` 클릭
7. 온보드 완료 후 `Get token`으로 토큰 조회 및 복사

### 3) 버튼
- **Start Gateway**: WSL에서 OpenClaw Gateway 시작
- **Install then Onboard**: OpenClaw 설치(필요 시) 후 온보드 실행 준비
- **Stop all OpenClaw**: OpenClaw 관련 프로세스 종료 시도
- **Add**: 모니터 대상 포트 등록
- **Monitoring**: 모니터 시작/중지
- **Copy token**: 토큰 클립보드 복사
- **Get token**: `openclaw config get gateway.auth.token` 조회

---

## 권한 구분

- 설치/검증: root 컨텍스트
- 온보드/시작/중지/토큰 조회: 사용자 컨텍스트

로그에서 `[root]`, `[user]`로 어느 권한에서 실행했는지 확인할 수 있습니다.

---

## 개발/빌드

### 빌드
```powershell
dotnet build .\OpenClawWinManager.sln
```

### 단일 실행 파일 배포
```powershell
dotnet publish OpenClawWinManager.csproj -c Release -r win-x64 /p:SelfContained=true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

출력:
`bin/Release/net8.0-windows/win-x64/publish/OpenClawWinManager.exe`

---

## 문제 해결

- **토큰이 안 보임**
  - 온보드가 끝난 뒤 `Get token`을 다시 클릭합니다.
- **gateway 토큰 누락 메시지**
  - 대시보드 인증 토큰을 Control UI에 설정했는지 확인하세요.
- **포트 충돌**
- 선택한 포트가 이미 사용 중인지 확인하고 다른 포트를 시도합니다.
- **프로세스가 안 죽음**
  - Stop 후에도 동작 중이면 1회 더 실행하고 로그를 확인하세요.

---

## 라이선스

프로젝트 루트의 `LICENSE` 파일을 참고하세요.

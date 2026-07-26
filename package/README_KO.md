# Fault StP LIGHTKRAVTE 한국어 패치 v1.0.0

## 범위

- 일본어 원문 기준 스크립트 48개, 대사/내레이션 6,746개
- UI 85개, 인물명 58개, 사용자 UI 1개, 로케일명 233개, Tips 48개
- 기존 일본어·영어·중국어(간체)는 수정하지 않음
- 원래 비어 있던 `zh-TW` 선택 버튼을 화면에서 `한국어`로 사용
- 번역 모델: Codex 내부 모델만 사용

## 설치

1. 게임을 완전히 종료합니다.
2. 압축을 임의의 폴더에 풉니다. 게임 폴더 안에는 풀지 마세요.
3. PowerShell에서 아래 명령을 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-KoreanPatch.ps1
```

기본 게임 경로가 다르면:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-KoreanPatch.ps1 -GameRoot 'D:\Games\Fault StP LIGHTKRAVTE'
```

설치기는 패키지 149개 파일, 게임 빌드 11개 핵심 파일의 SHA-256,
실행 중인 게임 프로세스, 모든 덮어쓰기 충돌을 먼저 검사합니다. 원본 백업은 게임 및
패키지 바깥의 `%LOCALAPPDATA%\CodexGamePatches\FaultStPLIGHTKRAVTE`에 보관합니다.

## 복원

게임을 종료한 뒤 설치 때와 같은 압축 해제 폴더에서 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\Restore-KoreanPatch.ps1
```

복원기는 설치 당시의 외부 저널과 원본/패치 복구 사본을 검증합니다. 패치가 추가한
파일은 설치 해시와 정확히 일치할 때만 지우며, 기존 파일은 백업 해시를 확인한 뒤
복구합니다. 사용자가 설치 후 수정한 파일은 덮어쓰거나 지우지 않습니다.

## 게임 안에서

언어 선택 화면의 `한국어` 버튼을 누릅니다. 내부적으로 일본어 원문 리소스를 유지한
채 한국어 표시만 문맥별 UID로 치환합니다. 일본어·영어·중국어 버튼을 누르면 한국어
모드가 꺼지고 해당 원본 언어가 그대로 표시됩니다.

## 글꼴

패치는 Windows의 `맑은 고딕`을 우선 사용합니다. 없으면 Noto Sans CJK KR,
Noto Sans KR, 나눔고딕 순으로 찾습니다. 설치된 지원 글꼴이 하나도 없으면 한국어가
정상 표시되지 않습니다.

## 무결성

- `package_manifest.json`: 설치 대상과 원본 빌드 고정값
- `CHECKSUMS.sha256`: 압축 안의 모든 배포 파일(체크섬 파일 자체 제외)
- `reports/`: 번역·글꼴·런타임·교차검수 결과

알 수 없는 게임 빌드, 혼합된 모드 파일, 변조된 패키지나 백업에서는 설치/복원이
쓰기 전에 중단됩니다.

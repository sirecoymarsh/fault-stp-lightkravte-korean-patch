# Fault StP LIGHTKRAVTE 한국어 패치

> 비공개 보관·유지보수용 저장소

`Fault StP LIGHTKRAVTE`의 일본어 원문을 기준으로 제작한 한국어 패치입니다.
원본에 숨겨져 있던 `zh-TW` 선택 슬롯을 `한국어`로 재사용하며, 일본어·영어·간체
중국어는 그대로 보존합니다.

![한국어 언어 선택기](docs/images/language-selector-ko.jpg)

## v1.0.0

- 시나리오 대사·내레이션 6,746개
- 관리 텍스트 425개
- 루비 독음 40개
- 교차 검수 교정 208건, 미해결 0건
- 치명 품질 결함 0건
- 한글 글꼴 누락 0
- 언어 선택기, 런타임 스모크, 설치·복원 fixture 및 라이브 설치 검증 PASS
- 번역에는 Codex 내부 모델만 사용

## 설치

1. 게임을 완전히 종료합니다.
2. `dist/FaultStP_LIGHTKRAVTE_Korean_v1.0.0.zip`을 게임 폴더 밖에 풉니다.
3. 압축을 푼 폴더에서 다음을 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-KoreanPatch.ps1
```

게임 경로가 기본값과 다르면:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-KoreanPatch.ps1 `
  -GameRoot 'D:\Games\Fault StP LIGHTKRAVTE'
```

## 복원

게임을 종료한 뒤 설치에 사용한 폴더에서 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\Restore-KoreanPatch.ps1
```

원본 백업은 게임 및 패키지 폴더 밖의
`%LOCALAPPDATA%\CodexGamePatches\FaultStPLIGHTKRAVTE`에 저장됩니다.

## 무결성

배포 ZIP:

```text
SHA-256 E193B6F145B0C4BB8774B29D458611AA0C55FBCD55962AAB4C8331F125C2692F
Size    1,238,781 bytes
```

`package/`는 최종 배포 ZIP을 그대로 압축 해제한 사본입니다. 설치기는 패키지
체크섬, 지원 게임 빌드, 실행 중인 프로세스와 덮어쓰기 충돌을 쓰기 전에
검사합니다.

## 저장소 구성

- `dist/`: 최종 배포 ZIP과 SHA-256
- `package/`: 무결성 검증 가능한 압축 해제 패키지
- `src/`: BepInEx/Harmony 런타임 플러그인 소스 스냅샷
- `docs/images/`: 실제 게임 GUI 검증 화면

게임 실행 파일과 원본 게임 에셋은 포함하지 않습니다.


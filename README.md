# Fault StP LIGHTKRAVTE 한국어 패치

> 비공개 보관·유지보수용 저장소

`Fault StP LIGHTKRAVTE`의 일본어 원문을 기준으로 제작한 한국어 패치입니다.
원본에 숨겨져 있던 `zh-TW` 선택 슬롯을 `한국어`로 재사용하며, 일본어·영어·간체
중국어는 그대로 보존합니다.

![한국어 언어 선택기](docs/images/language-selector-ko.jpg)

## v1.0.1

- 시나리오 대사·내레이션 6,746개, 관리 텍스트 425개
- 노출 일본어 0, speaker 34/34, 루비 30/30 및 의도적 제거 16개
- 폰트 closure 1,176자(한글 1,075자), 글리프 누락 0
- 품질 후보 46개 전수 문맥 검토, 5개 행 교정, 치명 결함 0
- `RevealableTMProText`의 폰트 머티리얼 캐시를 대사 할당·로케일 폰트 변경·
  `PreRender` 직전에 검증/무효화해 간헐적인 `ㅁㅁㅁㅁ` 렌더 경로를 보강
- 4096×4096 단일 페이지 동적 TMP 아틀라스와 긴 대사 자동 축소 유지
- 글리프 누락과 머티리얼 캐시 불일치를 구분하는 자동 렌더 진단 지원
- 설치·복원·중단 복구·변조 거부·설정 보존을 포함한 전체 fixture PASS
- 실제 게임 폴더 설치 및 152개 payload 정적 Verify PASS
- 번역에는 Codex 내부 모델만 사용

v1.0.1은 사용자의 요청에 따라 게임을 직접 실행하지 않고 빌드·정적 검사·fixture·
라이브 설치 Verify까지 완료했습니다. 간헐 렌더, 최장 대사, 복원 루비의 최종 화면
확인은 자연 플레이 로그로 이어갑니다.

## 설치

1. 게임을 완전히 종료합니다.
2. `dist/FaultStP_LIGHTKRAVTE_Korean_v1.0.1.zip`을 게임 폴더 밖에 풉니다.
3. 압축을 푼 폴더에서 다음을 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-KoreanPatch.ps1
```

게임 경로가 기본값과 다르면:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-KoreanPatch.ps1 `
  -GameRoot 'D:\Games\Fault StP LIGHTKRAVTE'
```

## 검증

```powershell
powershell -ExecutionPolicy Bypass -File .\Patch-Korean.ps1 `
  -Action Verify -GameRoot 'D:\Games\Fault StP LIGHTKRAVTE' -Json
```

## 자동 렌더 진단

`BepInEx\config\kr.cadriaclord.lightkravte.korean.cfg`에서 다음 값을 켜면
`BepInEx\LogOutput.log`에 텍스트·폰트·머티리얼 캐시 상태가 자동 기록됩니다.

```ini
[Diagnostics]
LogTextRendering = true
```

## 복원

게임을 종료한 뒤 설치에 사용한 폴더에서 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\Restore-KoreanPatch.ps1
```

원본 백업은 게임 및 패키지 폴더 밖의
`%LOCALAPPDATA%\CodexGamePatches\FaultStPLIGHTKRAVTE`에 저장됩니다. 설정 파일에서
허용된 네 값만 변경된 경우 복원 시 사용자 설정을 보존합니다.

## 무결성

v1.0.1 배포 ZIP:

```text
SHA-256 56588A685467408DACF1E0B9BED79AB54AA5CB50DF05F28B240BC28519C6A800
Size    1,267,771 bytes
```

`package/`는 최종 배포 ZIP의 루트와 바이트 단위로 동일한 사본입니다. 설치기는
패키지 전체 체크섬, 지원 게임 빌드, 실행 중인 프로세스와 덮어쓰기 충돌을 쓰기
전에 검사합니다. 런타임 변경을 허용하는 것은 스키마 검증된 비실행 설정 파일 한
개뿐이며, 나머지 payload는 정확한 해시가 필요합니다.

## 저장소 구성

- `dist/`: v1.0.0·v1.0.1 배포 ZIP과 SHA-256
- `package/`: 현재 v1.0.1의 무결성 검증 가능한 압축 해제 패키지
- `src/`: BepInEx/Harmony 런타임 플러그인 소스 스냅샷
- `docs/images/`: 실제 게임 GUI 검증 화면

게임 실행 파일과 원본 게임 에셋은 포함하지 않습니다.

# Fault StP LIGHTKRAVTE 한국어 런타임 패치 기술 보고서

## 상태

- 대상: `Fault StP LIGHTKRAVTE`, Unity 2020.3.7f1, Mono, Windows x64
- 서사 엔진: Naninovel
- 번역: 일본어 원문 기준 스크립트 48개, `PrintText` 6,746개
- 관리 텍스트: UI·인물명·로케일명·Tips 등 425개
- 언어 계약: `zh-TW` 선택 슬롯만 한국어로 사용하며 `ja`, `en`, `zh-CN`은 보존
- 번역 모델: Codex 내부 모델만 사용
- 플러그인: .NET Framework `csc.exe`로 재빌드 및 Mono.Cecil 정적 감사 통과
- 스테이징: 원본 게임과 분리된 순수 오버레이

## 신뢰 대상 빌드

플러그인은 `StrictBuildCheck=true`일 때 다음 게임 어셈블리 SHA-256을
독립적으로 확인한 뒤에만 후킹한다.

| 파일 | SHA-256 |
| --- | --- |
| `Assembly-CSharp.dll` | `7B7594F992C6F26CFBC0CD1FA92DC587109290F70F543CA90F306A4C488F7797` |
| `Utilities.dll` | `EFAAC90B12592609F673DC08D5CC164DB5D6C9E050F10755361AA3B68BF5F7F8` |
| `Elringus.Naninovel.Runtime.dll` | `E20198AE1932E6DEA7040ECF7F33F661308E99E983FC2C7F1F84AFDE30D631C1` |

설치기는 실행 파일, 에셋, 카탈로그, 번들, 위 어셈블리와 원래의 로더
루트 파일을 합쳐 총 11개 원본 파일의 길이와 SHA-256을 추가로 고정한다.

## 로더 호환성 수정

대상 폴더에 먼저 존재하던 `winhttp.dll`은 Doorstop 4.3 계열이었지만,
초기 스테이징의 BepInEx 5.4.21 preloader는 구형
`BepInEx.Preloader.Entrypoint.Main` 계약만 제공했다. 실기 로그 수집 중
Doorstop 4.x가 요구하는 `static void Doorstop.Entrypoint.Start()`와 맞지
않아 chainloader가 시작되지 않는 사실을 확인했다.

최종 스테이징은 서로 다른 버전을 섞지 않고 공식
`BepInEx_win_x64_5.4.23.5.zip` 한 아카이브의 proxy, 버전 마커, config,
core를 한 세트로 사용한다.

| 항목 | 검증값 |
| --- | --- |
| 공식 릴리스 | `https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5` |
| 아카이브 SHA-256 | `82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4` |
| GitHub 자산 digest | `sha256:82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4` |
| BepInEx | 5.4.23.5 |
| Doorstop | 4.5.0 |
| `winhttp.dll` | PE machine `0x8664`(x64), SHA-256 `8C6CDBC38836DEE87E3368F5DE1994D7C0CCEBF29E4CE7ABA3C0981F9375412C` |
| Preloader | public static `System.Void Doorstop.Entrypoint::Start()` |
| Harmony 참조 | 2.9.0.0 |

`vendor/bepinex_5.4.23.5_vendor_manifest.json`은 공식 자산 digest, 배포본
22개 파일의 길이·SHA-256, PE 아키텍처와 실제 어셈블리 버전을 기록한다.
`stage.ps1`은 이 manifest 전체를 먼저 검증하고, 정확히 일치하는
`BepInEx/core/**`, `winhttp.dll`, `.doorstop_version`,
`doorstop_config.ini`만 복사한다. live 게임 폴더에서 로더 파일을
재사용하지 않는다.

## 언어 선택기

`LanguageWheelButtonController.InitializeManagers`에서
`TraditionalChineseButton`이 `Utilities.Locale.zhTW` / `zh-TW`에
대응함을 확인했다. 배포 카탈로그에는 `zh-TW` 스크립트나 관리 텍스트가
없다.

독립 Unity 에셋 감사에서 실제 미표시 원인을 추가로 확정했다.
`SettingsUICustom/LanguageWheelPanel/Wheel/Buttons/TraditionalChineseButton`
은 삭제된 것이 아니라 프리팹에 `m_IsActive=false`로 직렬화되어 있다.
`Button`, `LanguageWheelButtonController`, `EventTrigger`는 모두 존재하고
interactable 값도 true지만, 비활성 상태에서는 `Awake/InitializeManagers`가
실행되지 않아 `zh-TW` 클릭 리스너가 설치되지 않는다. 더구나 이 버튼은
활성 `SimplifiedChineseButton`과 위치 `(250,-165)`, 크기 `(160,50)`가
완전히 같아 단순 활성화만 하면 간체 중국어를 가리고 raycast를 가로챈다.

기계 감사는 다음 두 번들을 읽기 전용으로 검사했다.

| 대상 | SHA-256 | 확인 내용 |
| --- | --- | --- |
| `ffdf3229911cca1b5eaeddd6135cfcff.bundle` | `79D2DB790E8D21200C6F464BB4EC43772F1927C340B1A7F3E2D937C958B98D93` | 설정 프리팹, 네 버튼의 active/좌표/컴포넌트/navigation |
| `7443a426cc273843ed5743fb31c72054.bundle` | `F36709125B1E605AA8BFB96E9DEAFD87075248F5C5128EEA8D2CF1966C638F56` | `LanguageWheel` controller와 네 pointer clip |

결과와 재현 코드는
`diagnostics/locale_selector_asset_audit.json`,
`diagnostics/LOCALE_SELECTOR_ASSET_AUDIT.md`,
`diagnostics/audit_language_selector_assets.py`에 보존했다.

`LanguageWheelButtonController.SetOnClick(Utilities.Locale)`의 Harmony
prefix는 다음 최소 동작만 수행한다.

1. `zhTW` 선택 시 한국어 모드를 활성화한다.
2. 게임 내부 locale 인자를 `ja`로 바꿔 기존 일본어 리소스의 실행
   흐름, 저장·불러오기, UI 상태를 그대로 이용한다.
3. 일본어·영어·중국어(간체) 버튼을 선택하면 한국어 모드를 끄고
   해당 언어의 원래 동작을 통과시킨다.
4. 화면의 `TraditionalChineseButton` 표기를 `한국어`로 바꾼다.

`SelectorBridge`는 디스크 에셋을 수정하지 않고 로드된 scene instance만
다음과 같이 보완한다.

1. `gameObject.scene.IsValid()`로 프리팹 asset 객체를 제외한다.
2. 휴면 controller와 button을 활성·interactable 상태로 보장하고
   `SetActive(true)`로 원래 `InitializeManagers`/`SetOnClick(zhTW)` 경로를
   그대로 살린다. 동기적 `Awake` 재진입은 전용 guard로 차단한다.
3. 겹친 버튼을 빈 아래 중앙 `(0,-280)`으로 옮겨 원본 세 버튼과 설정값
   표시 영역을 모두 피한다.
4. 한국어 모드가 꺼진 동안에도 `한국어` 레이블만 한국어 uGUI/TMP
   fallback을 유지해 일본어·영어·간체 중국어에서 다시 선택할 수 있게
   한다. 플러그인 종료 때는 원래 font/fallback으로 복원한다.
5. Explicit navigation을
   `KO.up=JA`, `KO.left=EN`, `KO.right=zh-CN`,
   `JA.down=KO`, `EN.right=KO`, `zh-CN.left=KO`로 연결한다.
   원본 세 버튼은 계속 활성·interactable이며 서로도 도달 가능하다.

원본 `LanguageWheel_zh-TW`와 `LanguageWheel_zh-CN` clip은 모두
`outer_ring`의 Z 회전을 `-129°`로 지정한다. 이는 두 중국어 버튼이
원래 같은 자리를 교대로 쓰도록 만든 흔적이다. 아래 중앙 한국어 버튼에는
이 포인터가 맞지 않으므로 `LanguageWheelController.SetAnimationState(int)`
하나에 좁은 prefix를 추가했다.

- 한국어 state `1`: `Wheel` Animator와 `outer_ring`이 모두 존재할 때만
  Animator를 잠시 끄고 `outer_ring.localRotation`을 Z `180°`로 설정한 뒤
  원래 메서드를 생략한다.
- 원본 state `0`, `2`, `3`: Animator가 꺼져 있으면 다시 켜고 게임의
  원래 `SetInteger`와 authored clip을 그대로 통과시킨다.
- Animator나 `outer_ring`을 찾지 못하면 한국어 state도 원래 메서드를
  통과시키는 안전 폴백을 사용한다.

`Wheel` Animator의 유일한 binding은 `outer_ring` Z 회전이다.
`base`, `aero_icon`, `inner_ring`은 각자 별도 Animator를 사용하므로
한국어 pointer 보정이 장식 애니메이션이나 원본 언어 전환을 멈추지 않는다.

가짜 `zh-TW` 리소스 provider나 Naninovel 비동기 상태 머신은 만들지
않는다.

## 문맥 기반 대사 치환

플러그인은 Naninovel 공개 확장 지점인 다음 작업을 등록한다.

- `IScriptPlayer.AddPreExecutionTask(Func<Command, UniTask>)`
- `IScriptPlayer.AddPostExecutionTask(Func<Command, UniTask>)`

한국어 모드의 `Naninovel.Commands.PrintText` 실행 직전에
`PlaybackSpot.ScriptName`과 **0부터 시작하는**
`PlaybackSpot.LineIndex`로 번역을 조회한다. 원래 `StringParameter`
객체를 저장한 뒤 한국어 객체를 잠시 설치하고, 실행 직후 정확히 원래
객체로 복원한다. 이 방식은 텍스트 표시, backlog, wait, printer 상태를
Naninovel 자체 흐름에 맡기면서 공유 스크립트 에셋을 영구 변경하지
않는다.

정적 감사 예시:

- 번역 UID `ch01-00:28:6d375c5c`
- 런타임 키 `ch01-00:28`
- 사용 좌표 `PlaybackSpot.LineIndex`
- 금지 좌표 `PlaybackSpot.LineNumber` 참조 없음

## 관리 텍스트

한국어 모드에서만 세 지점을 좁게 후킹한다.

- `TextManager.GetRecordValue(key, category)`
- private `TextManager.ApplyRecord(ManagedTextRecord)`
- `TextManager.GetAllRecords(string[])`

한국어와 일본어는 내부적으로 모두 `ja`를 사용하므로 같은 locale 선택이
no-op가 될 수 있다. 이를 보완하기 위해 locale 변경 완료 이벤트 뒤
`ITextManager.ApplyManagedTextAsync()`를 실행하고, 로드된
`ManagedTextProvider`의 값 변경 알림을 호출한다. 같은 locale 전환은
selection postfix에서 별도로 처리한다.

### 화자명

`CharacterManager.GetDisplayName`은 **원문 locale이 선택된 동안**
`ITextManager.GetRecordValue(CharacterNames)`를 호출하지 않고
`CharacterMetadata.DisplayName`을 바로 반환한다. 한국어 모드는 스크립트
실행 기반을 `ja`로 유지하므로, 일반 관리 텍스트가 정상 적용되어도
화자명만 일본어로 남는 경로였다.

플러그인은 `CharacterManager.GetDisplayName` 하나를 추가로 후킹해
한국어 모드일 때만 기존 `CharacterNames` 한국어 레코드를 직접 반환한다.
일본어·영어·중국어(간체) 모드에서는 원래 메서드를 그대로 통과시킨다.
6,746개 `PrintText`의 화자 ID를 전수 검사한 결과 화자 있음 5,457개,
고유 ID 34개였고, 33개는 직접 매핑된다. 유일하게 설정 키 대신 일본어
역할명이 들어간 `ch03-04:1145`의 `医療班`은 기존
`CharacterNames:med → 의료반`에 연결한다. 같은
`GetDisplayName`을 사용하는 대사창, backlog, chat printer, authored
template, 이름 식에도 함께 적용된다.

비-`PrintText` 명령의 문자열 필드도 전수 검사했다. 일본어가 남은
54건은 `PlayBgm`/`StopBgm`의 내부 오디오 리소스 경로뿐이며 화면 표시
문자열은 0건이다. 상세 결과는
`localization/reports/runtime_display_string_audit.json`에 기록했다.

## 글꼴

게임에 포함된 기본 UGUI/TMP 글꼴에는 한글 글리프가 없다. 플러그인은
선택적으로 동봉한 `fonts/Korean.ttf`를 우선 사용하고, 없으면 다음
설치 글꼴을 순서대로 찾는다.

1. Malgun Gothic
2. 맑은 고딕
3. Noto Sans CJK KR
4. Noto Sans KR
5. NanumGothic

1차 실기 스모크에서 `Font.CreateDynamicFontFromOSFont`로 만든 legacy
dynamic `Font`에 TMP 2.1.x가 요구하는 source face data가 없어
`FontEngine.LoadFontFace(Font, 90)`가 실패하는 것을 확인했다. 최종
플러그인은 `Font.GetPathsToOSFonts()`로 실제 폰트 파일 경로를 찾고
`new Font(absolutePath)`의 file-backed 경로를 **TMP source 전용**으로
사용한다. 별도로 uGUI에는 `Font.CreateDynamicFontFromOSFont`가 만든
legacy dynamic 글꼴을 사용한다. TMP 2.1.x에는 전자가 필요하고,
Unity `Text`의 `HasCharacter`/texture 요청에는 후자가 필요하므로 두
경로를 섞지 않는다.

TMP 동적 fallback asset에는 최종 번역 closure 중 한글·자모만 미리
채운다. U+30FB 강조점과 U+301C 물결표 등 일본어 기호는 원래 게임의
primary font가 담당하며, 정적 합집합 coverage로 확인했다. UGUI
`Text`는 한글을 실제로 표시하는 component만 동적 OS 글꼴로 바꾸며,
한국어 모드를 끄면 원래 글꼴을 복원한다.

최종 글꼴 감사는 번역에 필요한 1,174개 code point를 대상으로
Windows 한글 fallback과 원래 게임 글꼴의 합집합을 검사했고 누락은
0개였다. 이 중 1,073개 한글 code point는 Malgun Gothic에서 확인했다.

## 데이터 및 실패 동작

- 입력: 재귀적 UTF-8 JSONL
- 스크립트 레코드: `{"uid":"script:line:hash","ko":"..."}`
- 빈 `ko`: hard error
- 같은 런타임 키의 충돌 번역: hard error
- 알 수 없는 부가 필드: 무시
- 누락 레코드: 원문 유지 후 위치를 한 번만 debug 로그에 기록

태그, 선택지, 루비 구조를 보존하며, 발음용 루비 40개는 한국어 독음으로
현지화했다. 강조용 `<ruby="・">`는 구조적 표식이므로 유지한다.

## 가역 설치

스테이징과 릴리스 manifest는 모든 payload 파일의 길이와 SHA-256을
기록한다. 설치기는 다음 순서를 따른다.

1. `LIGHTKRAVTE.exe`가 종료되었는지 확인한다.
2. 신뢰 원본 11개의 길이와 SHA-256을 다시 검사한다.
3. 충돌하는 기존 세 로더 루트 파일을 게임·패키지 외부에 백업한다.
4. 복구용 patched 사본과 불변 설치 journal을 먼저 작성한다.
5. sibling 임시 파일을 거쳐 원자적으로 각 파일을 교체한다.
6. 설치 파일 전체를 manifest와 다시 대조한다.

플러그인 설정은 BepInEx가 생성하는 canonical UTF-8/CRLF 형식으로
미리 스테이징한다. 사용자가 언어 버튼을 바꾸면
`KoreanActive = true|false` 한 줄만 달라질 수 있으므로 package
manifest에 두 상태의 길이와 SHA-256을 별도로 고정한다. 설치 검증과
복원은 이 두 상태만 runtime-mutable로 허용하고, 그 밖의 설정 변조는
일반 payload와 동일하게 거부한다.

복원은 현재 파일이 설치본, 위에서 명시한 runtime-mutable 상태 또는
원본의 허용 해시일 때만 진행한다.
사용자가 설치 뒤 수정한 파일은 덮거나 지우지 않는다. 새로 추가된
파일도 설치 당시 해시와 정확히 같을 때만 제거하며, 기존 세 로더
파일은 백업 해시 검증 후 원상 복구한다.

## 검증 산출물

- `build/LightKravteKorean.dll`
- `plugin_assembly_audit.json`
- `runtime_contract_test.json`
- `staging_manifest.json`
- `vendor/bepinex_5.4.23.5_vendor_manifest.json`
- `tests/runtime_smoke_tools_validation.json`
- `localization/reports/runtime_display_string_audit.json`
- `diagnostics/locale_selector_asset_audit.json`
- `diagnostics/LOCALE_SELECTOR_ASSET_AUDIT.md`
- `diagnostics/locale_selector_fix_validation.json`
- `diagnostics/validate_language_selector_fix.py`

초기 `-nographics` 실기 실행은 Unity shader/video 초기화 단계에서
그래픽 장치 부재로 종료되어 로더 검증과 분리했다. 첫 실그래픽
스모크에서는 위의 OS dynamic Font 문제와, engine 초기화 event의
provider보다 플러그인 callback이 먼저 실행되어 private `textManager`
할당 전 refresh가 호출되는 순서 문제를 발견했다. 최종 코드는
`ApplyManagedTextAsync` 뒤 한 프레임을 양보하고 활성 provider만
refresh한다.

스모크 도구는 실그래픽 실행을 기본값으로 사용하고, 정확히 자신이
시작한 process만 종료하며 BepInEx 로그의 생성 시각과 hash freshness를
검증한다.

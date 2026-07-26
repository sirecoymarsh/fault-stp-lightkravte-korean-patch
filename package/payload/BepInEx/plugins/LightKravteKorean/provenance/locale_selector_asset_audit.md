# LIGHTKRAVTE 언어 선택기 독립 감사

## 결론

화면에 언어가 세 개만 보이는 이유는 `zh-TW` 데이터가 없어서가 아니라,
커스텀 설정 UI 프리팹에서 `TraditionalChineseButton` 자체가
`m_IsActive = false`로 직렬화되어 있기 때문이다. 버튼 컴포넌트와
`LanguageWheelButtonController`는 모두 존재하고 활성화 가능하지만, 원본 상태에서는
`Awake`가 실행되지 않아 클릭 리스너도 설치되지 않는다.

더 중요한 점은 비활성 `TraditionalChineseButton`과 활성
`SimplifiedChineseButton`의 `RectTransform`이 완전히 동일하다는 것이다. 단순히
`SetActive(true)`만 호출하면 나중 sibling인 전통 중국어 버튼이 간체 중국어 버튼
위에 그려지고 raycast도 가로챈다. 그러므로 네 번째 한국어 항목을 만들려면
**활성화와 비중첩 재배치가 함께** 필요하다.

기계 판독 결과는
[`locale_selector_asset_audit.json`](locale_selector_asset_audit.json)에 있다. 재현
스크립트는
[`audit_language_selector_assets.py`](audit_language_selector_assets.py)이며 게임
폴더를 읽기만 한다. 최종 런타임 소스의 위치·포인터·navigation을 이 에셋 결과와
대조한 검증은
[`locale_selector_fix_validation.json`](locale_selector_fix_validation.json)
(`PASS`)과
[`validate_language_selector_fix.py`](validate_language_selector_fix.py)에 있다.

## 감사 대상

| 항목 | 값 |
| --- | --- |
| Unity | `2020.3.7f1` |
| UI 프리팹 주소 | `Assets/00Assets/prefabs/UI/Settings/SettingsUICustom.prefab` |
| UI bundle | `ffdf3229911cca1b5eaeddd6135cfcff.bundle` |
| UI serialized file | `CAB-dc78c3a571e80650d2d6f6db3e8e519f` |
| UI bundle SHA-256 | `79D2DB790E8D21200C6F464BB4EC43772F1927C340B1A7F3E2D937C958B98D93` |
| animation bundle | `7443a426cc273843ed5743fb31c72054.bundle` |
| animation bundle SHA-256 | `F36709125B1E605AA8BFB96E9DEAFD87075248F5C5128EEA8D2CF1966C638F56` |

전체 계층은 다음과 같다.

```text
SettingsUICustom
└─ LanguageWheelPanel
   └─ Wheel
      └─ Buttons
         ├─ JapaneseButton
         ├─ EnglishButton
         ├─ SimplifiedChineseButton
         └─ TraditionalChineseButton
```

## 직렬화된 버튼 상태와 배치

| 버튼 | activeSelf | sibling | anchoredPosition | sizeDelta | label |
| --- | ---: | ---: | --- | --- | --- |
| `JapaneseButton` | true | 0 | `(0, 240)` | `(160, 50)` | `日本語` |
| `EnglishButton` | true | 1 | `(-250, -165)` | `(160, 50)` | `English` |
| `SimplifiedChineseButton` | true | 2 | `(250, -165)` | `(160, 50)` | `简体中文` + `风语计划` |
| `TraditionalChineseButton` | **false** | 3 | **`(250, -165)`** | `(160, 50)` | `繁體中文` |

네 버튼 모두 다음 컴포넌트를 가지며 `Button.m_Interactable = true`다.

```text
RectTransform
CanvasRenderer
UnityEngine.UI.Button
LanguageWheelButtonController
UnityEngine.EventSystems.EventTrigger
```

따라서 `zh-TW` 버튼은 삭제되었거나 상호작용 불가능한 것이 아니라 **프리팹에서
비활성이고, 활성 `zh-CN` 버튼과 동일 좌표에 포개진 상태**다.

## 코드 경로

`Assembly-CSharp.dll`의 IL은
[`locale_selector_controller_il.txt`](locale_selector_controller_il.txt)에
보존했다.

### 버튼 초기화

`LanguageWheelButtonController.InitializeManagers`는 오브젝트 이름에 따라 네
항목을 모두 명시적으로 처리한다.

| 이름 | locale 문자열 | `Utilities.Locale` | wheel state |
| --- | --- | --- | ---: |
| `EnglishButton` | `en` | `en` | 0 |
| `TraditionalChineseButton` | `zh-TW` | `zhTW` | 1 |
| `SimplifiedChineseButton` | `zh-CN` | `zhCN` | 2 |
| `JapaneseButton` | `ja` | `ja` | 3 |

- `InitializeManagers` IL `0054..0142`: 네 이름 분기, `onClick` 등록,
  `TrySetCurrentSelectedButton`, `buttonLocale` 기록.
- `SetOnClick` IL `0000..0047`: 현재 버튼 기록, locale 변경, 효과음, 패널 닫기.
- `OnSelect` IL `0016..0060`: enum에 맞춰 wheel state `0..3` 설정.
- `LanguageWheelPanelController.Show` IL `0029..0034`: 기록된 현재 버튼을 실제
  EventSystem 선택으로 만든다.

`SelectInitialLocale`는 최초 실행 때 시스템 중국어이면 `zh-CN`, 일본어이면 source
locale `ja`, 나머지는 `en`을 고른다. `zh-TW`를 자동 선택하는 경로는 없다. 이것과
프리팹의 비활성 플래그가 합쳐져 원본 게임에서 `zh-TW`가 완전히 휴면 상태가 된다.

## 키보드/게임패드 경로

원본 `Button.navigation`은 Explicit 모드이고, 활성 세 버튼에서
`TraditionalChineseButton`으로 들어가는 링크가 하나도 없다.

- Japanese: left English, right zh-CN
- English: up Japanese, right zh-CN
- zh-CN: up Japanese, left English
- zh-TW: up zh-CN, left English

따라서 한국어 버튼을 활성화·재배치한 뒤 최소한 한 개의 진입 링크가 필요하다.
한국어를 아래쪽에 둘 경우 `zh-CN.selectOnDown = Korean`과
`Korean.selectOnUp = zh-CN`만으로도 그래프가 연결된다. 더 자연스럽게 하려면
한국어의 left/right를 English/zh-CN으로 함께 지정한다.

## 중앙 선택 포인터

`Wheel`의 `LanguageWheel` AnimatorController는 네 clip을 가지며, 각 clip에는
binding이 **하나뿐**이다.

- binding path hash: `3673658243`
- `CRC32("outer_ring")`: `3673658243`
- 대상: `Wheel/outer_ring`
- 대상 sprite: `outer_ring`, `576×639`, pivot `(0.5, 0.451)`
- 대상 속성: Z 회전

`outer_ring` 이미지는 위쪽에 선택 포인터 돌기가 붙은 링이다
([`outer_ring.png`](outer_ring.png)). clip 상수는 다음과 같다.

| state | clip | Z |
| ---: | --- | ---: |
| 0 | `LanguageWheel_en` | `+129°` |
| 1 | `LanguageWheel_zh-TW` | `-129°` |
| 2 | `LanguageWheel_zh-CN` | `-129°` |
| 3 | `LanguageWheel_ja` | `+0.5°` |

즉 원본은 두 중국어 버튼을 같은 자리에 번갈아 쓰도록 제작되었고, 두 중국어
state의 포인터 각도까지 같다.

### 임의 위치의 정확한 포인터 각도

버튼 중심의 anchored position을 `(x, y)`라고 할 때 위쪽을 `0°`로 하는 링의 Z
각도는 다음과 같이 계산할 수 있다.

```csharp
float angle = Mathf.Atan2(-x, y) * Mathf.Rad2Deg;
```

- `(250, +165)` → `-56.575°` (실사용 반올림 `-57°`)
- `(0, -280)` → `±180°`
- `(390, -300)` → `-127.569°` (기존 `-129°` clip과 거의 일치)

## 안전한 구현안

### A. 최소 코드 변경: 기존 포인터 광선 사용

한국어 버튼을 `(390, -300)` 근처로 이동하면 간체 중국어와 겹치지 않으면서
기존 zh-TW `-129°` 포인터를 그대로 쓸 수 있다.

장점:

- Animator 보정 훅이 필요 없다.
- 원본 ja/en/zh-CN clip과 transition을 전혀 건드리지 않는다.

단점:

- zh-CN과 한국어가 같은 포인터 광선에 놓인다.
- 어느 항목이 선택됐는지는 각 버튼의 기존 `Outline`으로 구분해야 한다.

### B. 시각적으로 독립된 네 번째 방향

한국어 버튼을 빈 위치로 이동하고 포인터도 그 위치에 맞춘다. 화면 뒤의 현재
언어값과 겹치는 `(250,+165)`보다 `(0,-280)` 같은 아래 중앙이 읽기 쉽다.

`LanguageWheelPanel`은 `(1920,1080)` 전체 화면 RectTransform이고 컴포넌트에
`Mask`/`RectMask2D`가 없다. `Buttons`에도 mask가 없으므로 `(0,-280)`은 잘리지
않는다. 원형 포인터 sprite의 아래쪽 끝은 중심에서 약 `210.5 px`, 한국어 버튼의
윗변은 중심에서 `255 px` 아래라 약 `44.5 px` 간격이 남는다. 영어 버튼과의
수평 간격은 `90 px`, 간체 중국어의 두 줄 label 영역과도 수평 `90 px` 및 수직
`40 px` 이상 떨어져 직사각형이 교차하지 않는다.

`LanguageWheelController.SetAnimationState(int)`에 좁은 prefix를 두는 방식이
안전하다.

1. 한국어 state `1`이면 `Wheel` Animator를 disable한다.
2. `outer_ring.localRotation`을 계산한 각도로 직접 설정한다.
3. 원본 state `0`, `2`, `3`이면 Animator를 다시 enable하고 원래
   `SetInteger`를 통과시킨다.

이 Animator가 구동하는 것은 `outer_ring` 하나뿐이다. `base`, `aero_icon`,
`inner_ring`은 각자 별도 Animator를 가지므로 `Wheel` Animator를 잠시 끄더라도
그 장식 애니메이션은 멈추지 않는다.

Animator를 켜 둔 채 `outer_ring`을 한 번만 회전시키는 구현은 안전하지 않다.
다음 Animator update 또는 0.25초 transition이 transform을 다시 `-129°`로
덮어쓴다.

## 런타임 활성화 순서

1. `Resources.FindObjectsOfTypeAll<LanguageWheelButtonController>()`로 휴면
   controller를 찾는다.
2. `controller.gameObject.scene.IsValid()`로 프리팹 asset 자체를 제외한다.
3. live instance에만 `SetActive(true)`를 호출한다.
4. 같은 frame 안에서 `RectTransform.anchoredPosition`을 비중첩 위치로 바꾼다.
5. `Text`를 `한국어`로 바꾸고 한국어 글꼴을 직접 보장한다.
6. Explicit navigation 진입/복귀 링크를 만든다.
7. 한국어 모드이면 `wheel.SetCurrentLanguageButton(koreanButton)`으로 패널을
   다시 열 때 한국어가 선택되게 한다.

`SetActive(true)`는 휴면 controller의 `Awake/InitializeManagers`를 동기적으로
실행하므로, 기존 `SetOnClick(zhTW)` 경로를 그대로 살릴 수 있다. 에셋 오브젝트
필터와 비중첩 재배치가 빠지면 각각 프리팹 변형 시도와 zh-CN 가림 문제가 생긴다.

# 런타임 표시 문자열 감사

- 상태: **PASS**
- PrintText: 6,746개 (화자 있음 5,457, 화자 없음 1,289)
- 고유 화자 ID: 34개 (직접 매핑 33, 별칭 매핑 1)
- 번역 잔존 검사: 스크립트 6,746개 + 관리 문자열 425개, 표시 가능한 일본어 잔존 0개
- 비-PrintText 일본어 문자열: BGM 리소스 경로 54건만 확인 (화면 표시 대상 아님)

## 원인

한국어 모드는 실제 Naninovel 로캘을 `ja`로 유지합니다. `CharacterManager.GetDisplayName`은 원문 로캘에서 `CharacterNames` 관리 문자열 조회를 생략하므로 일본어 `CharacterMetadata.DisplayName`이 표시되었습니다.

## 수정

`CharacterManager.GetDisplayName`을 좁게 가로채 한국어 모드에서만 기존 `CharacterNames` 한국어 값을 반환합니다. 유일한 예외 `医療班`은 기존 `med → 의료반` 값으로 연결했습니다.

## 검사

- [x] all_named_printtext_authors_have_korean_route
- [x] non_printtext_visible_japanese_residue_free
- [x] script_translation_visible_japanese_residue_free
- [x] managed_translation_visible_japanese_residue_free
- [x] character_manager_get_display_name_patch
- [x] managed_character_name_lookup
- [x] literal_medic_alias

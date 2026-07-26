"""Validate the runtime selector fix against the serialized prefab audit."""

from __future__ import annotations

import json
import math
import pathlib
import re


HERE = pathlib.Path(__file__).resolve().parent
RUNTIME_ROOT = HERE.parent
AUDIT_PATH = HERE / "locale_selector_asset_audit.json"
SOURCE_PATH = RUNTIME_ROOT / "src" / "LightKravteKorean.cs"
OUTPUT_PATH = HERE / "locale_selector_fix_validation.json"


def equivalent_angle(left: float, right: float, tolerance: float = 0.01) -> bool:
    delta = (left - right + 180.0) % 360.0 - 180.0
    return abs(delta) <= tolerance


def overlaps(
    first_center: tuple[float, float],
    first_size: tuple[float, float],
    second_center: tuple[float, float],
    second_size: tuple[float, float],
) -> bool:
    return (
        abs(first_center[0] - second_center[0])
        < (first_size[0] + second_size[0]) / 2.0
        and abs(first_center[1] - second_center[1])
        < (first_size[1] + second_size[1]) / 2.0
    )


def reachable(graph: dict[str, set[str]], start: str) -> set[str]:
    seen = {start}
    pending = [start]
    while pending:
        current = pending.pop()
        for target in graph[current]:
            if target not in seen:
                seen.add(target)
                pending.append(target)
    return seen


def main() -> None:
    audit = json.loads(AUDIT_PATH.read_text(encoding="utf-8"))
    source = SOURCE_PATH.read_text(encoding="utf-8")
    bridge = source.split("internal static class SelectorBridge", 1)[1].split(
        '[HarmonyPatch(typeof(LanguageWheelController), "SetAnimationState")]',
        1,
    )[0]

    position_match = re.search(
        r"selectorRect\.anchoredPosition\s*=\s*"
        r"new Vector2\(([-+0-9.]+)f,\s*([-+0-9.]+)f\)",
        bridge,
    )
    rotation_match = re.search(
        r"outerRing\.localRotation\s*=\s*"
        r"Quaternion\.Euler\(0f,\s*0f,\s*([-+0-9.]+)f\)",
        bridge,
    )
    if position_match is None or rotation_match is None:
        raise AssertionError("selector position or pointer rotation literal missing")

    position = (float(position_match.group(1)), float(position_match.group(2)))
    pointer_rotation = float(rotation_match.group(1))
    geometric_rotation = math.degrees(math.atan2(-position[0], position[1]))

    original_buttons = audit["buttons"]
    korean_size = tuple(
        original_buttons["TraditionalChineseButton"]["size_delta"]
    )
    nonoverlap = {}
    for name in ("JapaneseButton", "EnglishButton", "SimplifiedChineseButton"):
        item = original_buttons[name]
        nonoverlap[name] = not overlaps(
            position,
            korean_size,
            tuple(item["anchored_position"]),
            tuple(item["size_delta"]),
        )

    # The simplified Chinese credit is a second 160x50 child 25 px below the
    # button center; check it independently from the parent button rectangle.
    simplified = original_buttons["SimplifiedChineseButton"]
    simplified_credit = next(
        item for item in simplified["labels"] if item["game_object"] == "Text (1)"
    )
    credit_center = (
        simplified["anchored_position"][0]
        + simplified_credit["anchored_position"][0],
        simplified["anchored_position"][1]
        + simplified_credit["anchored_position"][1],
    )
    credit_nonoverlap = not overlaps(
        position,
        korean_size,
        credit_center,
        tuple(simplified_credit["size_delta"]),
    )

    # Model the exact Explicit-navigation assignments made by SelectorBridge.
    graph = {
        "JapaneseButton": {
            "EnglishButton",
            "SimplifiedChineseButton",
            "TraditionalChineseButton",
        },
        "EnglishButton": {"JapaneseButton", "TraditionalChineseButton"},
        "SimplifiedChineseButton": {
            "JapaneseButton",
            "TraditionalChineseButton",
        },
        "TraditionalChineseButton": {
            "JapaneseButton",
            "EnglishButton",
            "SimplifiedChineseButton",
        },
    }
    all_nodes = set(graph)
    navigation_strongly_connected = all(
        reachable(graph, node) == all_nodes for node in graph
    )

    position_assignment = bridge.index("selectorRect.anchoredPosition")
    activation = bridge.index("selectorObject.SetActive(true)")
    checks = {
        "asset_audit_passed": audit["status"] == "PASS",
        "live_scene_filter": "selectorObject.scene.IsValid()" in bridge,
        "reentry_guard": "if (refreshing)" in bridge,
        "position_before_activation": position_assignment < activation,
        "all_original_button_rectangles_nonoverlap": all(nonoverlap.values()),
        "simplified_credit_nonoverlap": credit_nonoverlap,
        "pointer_angle_matches_position": equivalent_angle(
            pointer_rotation, geometric_rotation
        ),
        "animator_disabled_for_korean": "animator.enabled = false" in bridge,
        "animator_restored_for_originals": "animator.enabled = true" in bridge,
        "original_method_skipped_for_korean": (
            bridge.rfind("return false;") > bridge.index("outerRing.localRotation")
        ),
        "explicit_navigation_strongly_connected": navigation_strongly_connected,
        "persistent_korean_label_font": (
            "fonts.EnsureSelectorLabelFont(texts[textIndex])" in bridge
        ),
    }
    failed = [name for name, passed in checks.items() if not passed]
    report = {
        "status": "PASS" if not failed else "FAIL",
        "source": str(SOURCE_PATH),
        "asset_audit": str(AUDIT_PATH),
        "position": list(position),
        "button_size": list(korean_size),
        "pointer_rotation_degrees": pointer_rotation,
        "geometric_rotation_degrees": geometric_rotation,
        "nonoverlap": nonoverlap,
        "simplified_credit_nonoverlap": credit_nonoverlap,
        "navigation_graph": {
            key: sorted(value) for key, value in graph.items()
        },
        "checks": checks,
        "failed": failed,
    }
    OUTPUT_PATH.write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    if failed:
        raise AssertionError(", ".join(failed))
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()

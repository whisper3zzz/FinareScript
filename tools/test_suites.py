#!/usr/bin/env python3
"""Inventory and select Finare's behavior-oriented managed test suites."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
TEST_ROOT = ROOT / "tests" / "Finare.Tests"
SUITES = ("Compiler", "Graph", "Authoring", "Runtime", "Unreal", "Delivery")
_CLASS = re.compile(r"^\s*public\s+(?:(?:sealed|static|abstract|unsafe|partial)\s+)*class\s+(\w+)")
_TRAIT = re.compile(r'\[Trait\(\s*"Category"\s*,\s*"([^"]+)"\s*\)\]')
_IGNORED_PARTS = {"fixtures", "bin", "obj"}
_CS_NON_CODE = re.compile(
    r'//[^\n]*|/\*.*?\*/|(?P<raw>"{3,}).*?(?P=raw)|@"(?:""|[^"])*"'
    r'|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'',
    re.DOTALL,
)


def _source_classes(path: Path) -> list[tuple[str, list[str]]]:
    """Read top-level test declarations and their nearby Category traits."""

    source = path.read_text(encoding="utf-8")
    lines = source.splitlines()
    # Test fixtures embed entire C# programs. Mask their literals/comments for
    # declaration discovery while retaining original attribute lines for traits.
    code_lines = _CS_NON_CODE.sub(
        lambda match: re.sub(r"[^\n]", " ", match.group()), source
    ).splitlines()
    result: list[tuple[str, list[str]]] = []
    for index, line in enumerate(code_lines):
        match = _CLASS.match(line)
        if not match or not match.group(1).endswith("Tests"):
            continue
        traits: list[str] = []
        prior_index = index - 1
        while prior_index >= 0:
            prior = lines[prior_index].strip()
            if not prior or prior.startswith("//") or prior.startswith("/*") or prior.endswith("*/"):
                prior_index -= 1
                continue
            if prior.startswith("["):
                traits.extend(_TRAIT.findall(prior))
                prior_index -= 1
                continue
            break
        result.append((match.group(1), traits))
    return result


def _source_files(test_root: Path):
    for path in sorted(test_root.rglob("*.cs")):
        relative_parts = {part.casefold() for part in path.relative_to(test_root).parts}
        if relative_parts & _IGNORED_PARTS:
            continue
        yield path


def inventory(test_root: Path = TEST_ROOT) -> dict[str, list[dict[str, object]]]:
    """Return each suite's classes, grouping partial declarations by name."""

    classes: dict[str, dict[str, object]] = {}
    for path in _source_files(test_root):
        if path.name == "Usings.cs":
            continue
        for name, source_traits in _source_classes(path):
            entry = classes.setdefault(name, {"class": name, "files": [], "traits": []})
            entry["files"].append(path.relative_to(test_root).as_posix())
            entry["traits"].extend(source_traits)
    result = {suite: [] for suite in SUITES}
    for entry in sorted(classes.values(), key=lambda value: str(value["class"])):
        traits = entry["traits"]
        if len(traits) != 1 or traits[0] not in SUITES:
            raise ValueError(
                f"{entry['class']} must have exactly one valid Category trait across its partial files; "
                f"found {traits or 'none'}"
            )
        result[traits[0]].append(entry)
    return result


def _command(suite: str) -> list[str]:
    return [
        "dotnet",
        "test",
        "tests/Finare.Tests/Finare.Tests.csproj",
        "--filter",
        f"Category={suite}",
    ]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suite", choices=SUITES, help="print the dotnet filter for one suite")
    parser.add_argument("--json", action="store_true", help="print the machine-readable inventory")
    args = parser.parse_args(argv)
    try:
        data = inventory()
    except (OSError, ValueError) as error:
        print(f"[error] {error}", file=sys.stderr)
        return 1
    if args.suite:
        print(" ".join(_command(args.suite)))
        return 0
    if args.json:
        print(json.dumps(data, indent=2, sort_keys=True))
        return 0
    for suite in SUITES:
        print(f"{suite}: {len(data[suite])} classes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

"""Structural checks for local Markdown documents.

The project has a large body of technical documentation.  This module keeps
repository validation focused on durable structure (files and local links),
so normal prose and roadmap wording can evolve without updating a validator
for every sentence.
"""

from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path
from urllib.parse import unquote
import re


_INLINE_LINK = re.compile(r"(?<!!)\[[^\]\n]+\]\(\s*(?:<([^>\n]+)>|([^\s)]+))")
_REFERENCE_DEFINITION = re.compile(r"^ {0,3}\[([^\]\n]+)\]:\s*(?:<([^>\n]+)>|(\S+))")
_REFERENCE_USE = re.compile(r"(?<!!)\[[^\]\n]+\]\[([^\]\n]*)\]")
_FENCE = re.compile(r"^ {0,3}(```+|~~~+)")
_REMOTE_SCHEMES = ("http://", "https://", "mailto:", "ftp://")
_IGNORED_DIRS = {
    ".git",
    ".codex",
    "artifacts",
    "bin",
    "obj",
    ".vs",
    "node_modules",
    "binaries",
    "intermediate",
    "saved",
    "deriveddatacache",
    "__pycache__",
}


@dataclass(frozen=True)
class LinkIssue:
    """One malformed or missing local Markdown link."""

    source: Path
    line: int
    target: str
    reason: str

    def format(self, root: Path) -> str:
        try:
            source = self.source.relative_to(root).as_posix()
        except ValueError:
            source = self.source.as_posix()
        return f"{source}:{self.line}: local link '{self.target}' {self.reason}"


@dataclass(frozen=True)
class MarkdownReport:
    """Result of a local documentation structure scan."""

    files: tuple[Path, ...]
    checked_links: int
    ignored_generated_links: int
    issues: tuple[LinkIssue, ...]

    @property
    def passed(self) -> bool:
        return not self.issues


def discover_markdown(root: Path) -> tuple[Path, ...]:
    """Return repository Markdown files, excluding build/cache directories."""

    root = root.resolve()
    files = []
    # Prune generated trees while walking.  This matters for Unreal projects,
    # where Saved/Intermediate can contain thousands of transient documents.
    for directory, subdirectories, names in os.walk(root):
        subdirectories[:] = [
            name for name in subdirectories if name.casefold() not in _IGNORED_DIRS
        ]
        files.extend(Path(directory) / name for name in names if name.casefold().endswith(".md"))
    return tuple(sorted(files))


def _is_fenced(line: str, fence: str | None) -> tuple[bool, str | None]:
    match = _FENCE.match(line)
    if match:
        marker = match.group(1)[0]
        if fence is None:
            return True, marker
        if marker == fence:
            return True, None
    return fence is not None, fence


def _target_path(source: Path, target: str, root: Path) -> Path | None:
    target = unquote(target.strip())
    # Fragments and query strings identify a location in a document; they do
    # not change the local file that must exist.
    target = re.split(r"[?#]", target, maxsplit=1)[0]
    if not target or target.startswith("#"):
        return None
    if target.startswith(_REMOTE_SCHEMES) or target.startswith("//"):
        return None
    # Markdown paths are source-relative.  A leading slash is commonly used
    # for a repository-root link in project docs, so resolve it against root.
    if target.startswith("/"):
        candidate = root / target.lstrip("/")
    else:
        candidate = source.parent / target
    try:
        candidate = candidate.resolve()
        candidate.relative_to(root)
    except ValueError:
        raise ValueError("escapes the repository root")
    return candidate


def _is_generated_target(target: str) -> bool:
    parts = {part.lower() for part in Path(unquote(target)).parts}
    return "artifacts" in parts


def _iter_links(path: Path):
    lines = path.read_text(encoding="utf-8").splitlines()
    references: dict[str, tuple[str, int]] = {}
    fence: str | None = None
    # Reference definitions may appear after their use.  Collect definitions
    # in a first pass so link validation is independent of document order.
    for line_number, line in enumerate(lines, start=1):
        in_fence, fence = _is_fenced(line, fence)
        if in_fence:
            continue
        definition = _REFERENCE_DEFINITION.match(line)
        if definition:
            target = definition.group(2) or definition.group(3)
            references[definition.group(1).strip().casefold()] = (target, line_number)
    fence = None
    for line_number, line in enumerate(lines, start=1):
        in_fence, fence = _is_fenced(line, fence)
        if in_fence:
            continue
        definition = _REFERENCE_DEFINITION.match(line)
        if definition:
            target = definition.group(2) or definition.group(3)
            references[definition.group(1).strip().casefold()] = (target, line_number)
        for match in _INLINE_LINK.finditer(line):
            yield line_number, match.group(1) or match.group(2)
        for match in _REFERENCE_USE.finditer(line):
            key = match.group(1).strip() or match.group(0).split("]", 1)[0][1:]
            if key.casefold() in references:
                yield line_number, references[key.casefold()][0]
            else:
                yield line_number, f"[{key}]"


def validate_markdown(root: Path, required: tuple[Path, ...] = ()) -> MarkdownReport:
    """Validate required files and every local Markdown link without network access."""

    root = root.resolve()
    files = discover_markdown(root)
    issues: list[LinkIssue] = []
    for required_path in required:
        path = required_path if required_path.is_absolute() else root / required_path
        if not path.is_file():
            issues.append(LinkIssue(path, 1, path.as_posix(), "is missing"))
    checked = 0
    ignored_generated = 0
    for source in files:
        for line_number, target in _iter_links(source):
            if target.startswith("[") and target.endswith("]"):
                issues.append(LinkIssue(source, line_number, target, "has no reference definition"))
                continue
            try:
                candidate = _target_path(source, target, root)
            except ValueError as error:
                issues.append(LinkIssue(source, line_number, target, str(error)))
                continue
            if candidate is None:
                continue
            if _is_generated_target(target):
                ignored_generated += 1
                continue
            checked += 1
            if not candidate.is_file():
                issues.append(LinkIssue(source, line_number, target, "does not resolve to a file"))
    return MarkdownReport(files, checked, ignored_generated, tuple(issues))


def format_report(report: MarkdownReport, root: Path) -> str:
    """Produce a concise actionable report suitable for CLI output."""

    if report.issues:
        return "\n".join(issue.format(root) for issue in report.issues)
    return (
        f"checked {len(report.files)} Markdown files and {report.checked_links} local links"
        + (f" ({report.ignored_generated_links} generated links ignored)" if report.ignored_generated_links else "")
    )

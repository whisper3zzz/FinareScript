from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))

from repository_checks.markdown import validate_markdown  # noqa: E402


class MarkdownStructureTests(unittest.TestCase):
    def test_checks_inline_and_reference_links_without_network_access(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "docs").mkdir()
            (root / "docs" / "guide.md").write_text("# Guide\n", encoding="utf-8")
            (root / "README.md").write_text(
                "[guide](docs/guide.md) and [reference][guide]\n"
                "[guide]: docs/guide.md\n"
                "```\n[ignored](missing.md)\n```\n",
                encoding="utf-8",
            )
            report = validate_markdown(root)
            self.assertTrue(report.passed)
            self.assertEqual(report.checked_links, 2)

    def test_reports_missing_local_links_and_ignores_generated_artifact_targets(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "README.md").write_text(
                "[missing](docs/missing.md)\n"
                "[generated](artifacts/run/report.md)\n",
                encoding="utf-8",
            )
            report = validate_markdown(root)
            self.assertFalse(report.passed)
            self.assertEqual(len(report.issues), 1)
            self.assertIn("docs/missing.md", report.issues[0].target)
            self.assertEqual(report.ignored_generated_links, 1)

    def test_strips_fragments_and_reports_paths_that_escape_the_repository(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "docs").mkdir()
            (root / "docs" / "guide.md").write_text("# Guide\n", encoding="utf-8")
            (root / "README.md").write_text(
                "[guide](docs/guide.md#usage)\n"
                "[outside](../outside.md)\n",
                encoding="utf-8",
            )
            report = validate_markdown(root)
            self.assertEqual(report.checked_links, 1)
            self.assertEqual(len(report.issues), 1)
            self.assertIn("escapes the repository root", report.issues[0].reason)

    def test_prunes_unreal_and_python_generated_trees(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for relative in ("Binaries/hidden.md", "Intermediate/hidden.md", "__pycache__/hidden.md"):
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("# generated\n", encoding="utf-8")
            (root / "README.md").write_text("# Finare\n", encoding="utf-8")
            report = validate_markdown(root)
            self.assertEqual([path.name for path in report.files], ["README.md"])

    def test_required_documents_are_checked_as_files(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "README.md").write_text("# Finare\n", encoding="utf-8")
            report = validate_markdown(root, (Path("docs") / "TESTING.md",))
            self.assertFalse(report.passed)
            self.assertIn("is missing", report.issues[0].reason)


if __name__ == "__main__":
    unittest.main()

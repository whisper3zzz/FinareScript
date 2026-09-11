from __future__ import annotations

import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))

from test_suites import SUITES, _source_classes, inventory, main  # noqa: E402


class TestSuiteInventoryTests(unittest.TestCase):
    def test_repository_classes_have_one_behavior_suite(self) -> None:
        suites = inventory()
        self.assertEqual(set(SUITES), set(suites))
        classes = [entry for entries in suites.values() for entry in entries]
        self.assertTrue(all(suites.values()), "every documented suite must contain tests")
        self.assertEqual(len(classes), len({entry["class"] for entry in classes}))
        self.assertTrue(all(len(entry["traits"]) == 1 for entry in classes))

        partial = next(
            entry for entry in classes
            if entry["class"] == "InProcessUnrealNativeInvocationTransportTests"
        )
        self.assertGreater(len(partial["files"]), 1)
        self.assertEqual(partial["traits"], ["Unreal"])

    def test_partial_class_without_trait_is_not_counted_as_a_second_trait(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Main.cs").write_text(
                '[Trait("Category", "Compiler")]\n'
                "public sealed partial class SharedTests {}\n",
                encoding="utf-8",
            )
            (root / "Part.cs").write_text(
                "public sealed partial class SharedTests {}\n", encoding="utf-8"
            )
            (root / "Other.cs").write_text(
                '[Trait("Category", "Graph")]\n'
                "public sealed class OtherTests {}\n",
                encoding="utf-8",
            )
            self.assertEqual(
                [entry["class"] for entry in inventory(root)["Compiler"]],
                ["SharedTests"],
            )
            bad = root / "Bad.cs"
            bad.write_text(
                '[Trait("Category", "Compiler")]\n'
                '[Trait("Category", "Graph")]\n'
                "public sealed class BadTests {}\n",
                encoding="utf-8",
            )
            with self.assertRaises(ValueError):
                inventory(root)

    def test_suite_selection_prints_native_dotnet_filter(self) -> None:
        output = StringIO()
        with redirect_stdout(output):
            self.assertEqual(main(["--suite", "Runtime"]), 0)
        self.assertEqual(
            output.getvalue().strip(),
            "dotnet test tests/Finare.Tests/Finare.Tests.csproj --filter Category=Runtime",
        )

    def test_missing_unknown_and_nonadjacent_categories_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for declaration in (
                "public class MissingTests {}\n",
                '[Trait("Category", "Unknown")]\npublic class UnknownTests {}\n',
                '[Trait("Category", "Graph")]\npublic class FirstTests {}\npublic class MissingTests {}\n',
            ):
                with self.subTest(declaration=declaration):
                    (root / "Cases.cs").write_text(declaration, encoding="utf-8")
                    with self.assertRaisesRegex(ValueError, "exactly one valid Category"):
                        inventory(root)

    def test_subdirectories_are_included_but_fixtures_and_source_literals_are_not(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Compiler").mkdir()
            (root / "Fixtures").mkdir()
            (root / "Fixtures" / "Example.cs").write_text("public class FixtureTests {}", encoding="utf-8")
            (root / "Compiler" / "Cases.cs").write_text(
                '// public class CommentTests {}\n'
                '/*\npublic class BlockCommentTests {}\n*/\n'
                'var source = """\npublic class EmbeddedTests {}\n""";\n'
                '[Trait("Category", "Compiler")]\n'
                '    public class NestedFolderTests {}\n', encoding="utf-8")
            self.assertEqual(
                [entry["class"] for entry in inventory(root)["Compiler"]], ["NestedFolderTests"])


if __name__ == "__main__":
    unittest.main()

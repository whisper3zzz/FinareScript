from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from repository_checks.architecture import validate_architecture


class ArchitectureTests(unittest.TestCase):
    def test_analyzer_build_dependency_is_not_a_runtime_reference_but_still_checks_cycles(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runtime = root / "src" / "Finare.Unreal" / "Finare.Unreal.csproj"
            analyzer = root / "src" / "Generator" / "Generator.csproj"
            runtime.parent.mkdir(parents=True)
            analyzer.parent.mkdir(parents=True)
            runtime.write_text('<Project><ItemGroup><ProjectReference Include="../Generator/Generator.csproj" '
                'ReferenceOutputAssembly="false" OutputItemType="Analyzer" /></ItemGroup></Project>')
            analyzer.write_text('<Project><ItemGroup><PackageReference Include="Microsoft.CodeAnalysis.CSharp" />'
                '</ItemGroup></Project>')
            self.assertEqual(validate_architecture(root), [])
            analyzer.write_text('<Project><ItemGroup><ProjectReference Include="../Finare.Unreal/Finare.Unreal.csproj" '
                'ReferenceOutputAssembly="false" /></ItemGroup></Project>')
            self.assertTrue(any("dependency cycle" in issue for issue in validate_architecture(root)))

    def test_repository_runtime_closure_is_compiler_free(self):
        self.assertEqual(validate_architecture(ROOT), [])

    def test_transitive_compiler_leaks_cycles_and_missing_projects_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def project(name, references=(), packages=()):
                path = root / "src" / name / f"{name}.csproj"
                path.parent.mkdir(parents=True, exist_ok=True)
                entries = ''.join(f'<ProjectReference Include="../{ref}/{ref}.csproj" />' for ref in references)
                entries += ''.join(f'<PackageReference Include="{package}" />' for package in packages)
                path.write_text(f'<Project><ItemGroup>{entries}</ItemGroup></Project>', encoding="utf-8")
            project("Finare.Core")
            project("Finare.Runtime", ["Finare.Core"])
            project("Finare.Unreal", ["Finare.Runtime"])
            self.assertEqual(validate_architecture(root), [])
            project("Finare.CSharp", packages=["Microsoft.CodeAnalysis.CSharp"])
            project("Finare.Runtime", ["Finare.CSharp"])
            issues = validate_architecture(root)
            self.assertTrue(any("Finare.Unreal: compiler" in issue for issue in issues))
            self.assertTrue(any("Finare.Unreal: transitive Roslyn" in issue for issue in issues))
            project("Finare.CSharp", ["Finare.Runtime", "Missing"])
            issues = validate_architecture(root)
            self.assertTrue(any("dependency cycle" in issue for issue in issues))
            self.assertTrue(any("unresolved project" in issue for issue in issues))


if __name__ == "__main__":
    unittest.main()

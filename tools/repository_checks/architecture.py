"""Check actual project dependency closures, not architecture prose."""
from pathlib import Path
import xml.etree.ElementTree as ET


def validate_architecture(root: Path) -> list[str]:
    projects = {path.stem: path for path in (root / "src").glob("*/*.csproj")}
    edges: dict[str, set[str]] = {}
    packages: dict[str, set[str]] = {}
    issues = []
    for name, path in projects.items():
        tree = ET.parse(path)
        edges[name] = set()
        packages[name] = set()
        for element in tree.iter():
            tag = element.tag.rsplit("}", 1)[-1]
            value = element.get("Include", "")
            if tag == "ProjectReference":
                target = (path.parent / value.replace("\\", "/")).resolve()
                if not target.is_file() or target.stem not in projects:
                    issues.append(f"{name}: unresolved project reference {value}")
                edges[name].add(target.stem)
            elif tag in ("PackageReference", "Reference"):
                packages[name].add(value.split(",", 1)[0])

    def closure(name: str, stack: tuple[str, ...] = ()) -> set[str]:
        if name in stack:
            issues.append("dependency cycle: " + " -> ".join((*stack, name)))
            return set()
        result = {name}
        for dependency in edges.get(name, set()):
            result.update(closure(dependency, (*stack, name)))
        return result

    neutral = {"Finare.Core": set(), "Finare.Semantics": {"Finare.Core"},
               "Finare.Graph": {"Finare.Core", "Finare.Semantics"},
               "Finare.Authoring": {"Finare.Core", "Finare.Graph"}}
    for name, allowed in neutral.items():
        if extra := edges.get(name, set()) - allowed:
            issues.append(f"{name}: forbidden direct dependencies {sorted(extra)}")
    for name in projects:
        dependencies = closure(name)
        if name in {"Finare.Runtime", "Finare.Unreal", "Finare.ProgramModel", *neutral}:
            forbidden = dependencies & {"Finare.CSharp", "Finare.Compilation", "Finare.Cli"}
            if forbidden:
                issues.append(f"{name}: compiler/tooling leaked into runtime contracts: {sorted(forbidden)}")
            compiler_packages = {package for dependency in dependencies for package in packages.get(dependency, set())
                                 if package.startswith("Microsoft.CodeAnalysis")}
            if compiler_packages:
                issues.append(f"{name}: transitive Roslyn dependency: {sorted(compiler_packages)}")
        if name == "Finare.CSharp" and dependencies & {"Finare.Runtime", "Finare.Unreal"}:
            issues.append("Finare.CSharp: compiler depends on runtime or Unreal")
    return sorted(set(issues))

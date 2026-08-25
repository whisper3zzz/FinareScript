# MSBuild project loading

UnifiedScript can analyze or emit an SDK-style `.csproj` directly. Project
loading delegates evaluation to the selected .NET SDK's MSBuild, so imported
props/targets, `Compile` globs, target-framework constants, NuGet compile assets,
ordinary assembly references, and built `ProjectReference` outputs all come
from the same evaluated build graph as `dotnet build`.

```powershell
dotnet run --project src/UnifiedScript.Cli -- `
  analyze samples/ProjectLoading/ProjectLoadingDemo.csproj `
  --out artifacts/project-loading

dotnet run --project src/UnifiedScript.Cli -- `
  emit samples/ProjectLoading/ProjectLoadingDemo.csproj `
  --out artifacts/project-loading-emit
```

Restore and build run by default before evaluation. `--configuration Release`
selects another configuration, and `--framework net10.0` is required when a
project declares multiple target frameworks. `--no-restore` and `--no-build`
are available for a build graph whose assets and referenced outputs are already
current.

The resulting compilation request keeps MSBuild's language version,
preprocessor symbols, nullable context, unsafe/overflow settings, optimization
level, exact compile references, and copy-local runtime dependency paths.
Those inputs participate in semantic configuration identity. CoreCLR execution
uses only the copy-local runtime set, so framework reference assemblies are
never loaded as runtime implementations.

Project loading executes restore, build, and custom MSBuild targets from the
project. Treat an untrusted project like any other untrusted build input.
Compiler analyzers and source generators are not replayed by the UnifiedScript
compilation host yet; projects that depend on generator-only source can build in
MSBuild but will receive ordinary missing-symbol diagnostics during UnifiedScript
analysis. Project files must remain buildable, and project-reference outputs
must exist when `--no-build` is used.

using System.Text.Json;
using UnifiedScript.Authoring;
using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Graph;
using UnifiedScript.Runtime;
using UnifiedScript.Semantics;
using UnifiedScript.Unreal;

namespace UnifiedScript.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (FileNotFoundException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 66;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 65;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 74;
        }
    }

    private static int Run(IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args[0] == "identity-init")
        {
            return RunIdentityInit(args);
        }

        if (args.Count > 0 && args[0] == "layout-init")
        {
            return RunLayout(args, synchronize: false);
        }

        if (args.Count > 0 && args[0] == "layout-sync")
        {
            return RunLayout(args, synchronize: true);
        }

        if (args.Count > 0 && args[0] == "apply-patch")
        {
            return RunApply(args, isPlan: false);
        }

        if (args.Count > 0 && args[0] == "apply-plan")
        {
            return RunApply(args, isPlan: true);
        }

        if (args.Count > 0 && args[0] == "apply-declaration-patch")
        {
            return RunDeclarationApply(args);
        }

        if (args.Count > 0 && args[0] == "execute")
        {
            return RunExecute(args);
        }

        if (args.Count > 0 && args[0] == "debug-serve")
        {
            return RunDebugServe(args);
        }

        if (args.Count > 0 && args[0] == "ue-bindings")
        {
            return RunUnrealBindings(args);
        }

        if (args.Count > 0 && args[0] is "emit" or "emit-files")
        {
            return RunEmit(args);
        }

        return RunAnalyze(args);
    }

    private static int RunUnrealBindings(IReadOnlyList<string> args)
    {
        if (!TryParseUnrealBindingArguments(args, out UnrealBindingOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without Unreal binding options.");
        if (!File.Exists(options.ManifestPath))
        {
            Console.Error.WriteLine($"Unreal reflection manifest does not exist: {options.ManifestPath}");
            return 66;
        }

        string fullManifestPath = Path.GetFullPath(options.ManifestPath);
        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        EnsureDifferentPath(
            fullManifestPath,
            fullOutputPath,
            "Generated bindings cannot overwrite the Unreal reflection manifest.");
        string? fullNativeOutputPath = options.NativeOutputPath is null
            ? null
            : Path.GetFullPath(options.NativeOutputPath);
        if (fullNativeOutputPath is not null)
        {
            EnsureDifferentPath(
                fullManifestPath,
                fullNativeOutputPath,
                "Generated native thunks cannot overwrite the Unreal reflection manifest.");
            EnsureDifferentPath(
                fullOutputPath,
                fullNativeOutputPath,
                "Managed bindings and native thunks require different output paths.");
        }
        string? fullReportPath = options.ReportPath is null
            ? null
            : Path.GetFullPath(options.ReportPath);
        if (fullReportPath is not null)
        {
            EnsureDifferentPath(
                fullManifestPath,
                fullReportPath,
                "Binding coverage cannot overwrite the Unreal reflection manifest.");
            EnsureDifferentPath(
                fullOutputPath,
                fullReportPath,
                "Managed bindings and binding coverage require different output paths.");
            if (fullNativeOutputPath is not null)
            {
                EnsureDifferentPath(
                    fullNativeOutputPath,
                    fullReportPath,
                    "Native thunks and binding coverage require different output paths.");
            }
        }
        UnrealReflectionManifest manifest = UnrealReflectionJson.Deserialize(
            File.ReadAllText(fullManifestPath));
        UnrealBindingGenerationResult result = new UnrealBindingGenerator().Generate(
            manifest,
            new UnrealBindingGeneratorOptions(options.Namespace));
        foreach (UnrealBindingDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(
                $"warning {diagnostic.Code}: {diagnostic.Message} [{diagnostic.MemberPath}]");
        }

        WriteAtomically(fullOutputPath, result.Source);
        Console.WriteLine(fullOutputPath);
        UnrealNativeThunkGenerationResult? nativeResult = null;
        if (fullNativeOutputPath is not null || fullReportPath is not null || options.FailOnRejected)
        {
            nativeResult = new UnrealNativeThunkGenerator().Generate(manifest);
            foreach (UnrealNativeThunkDiagnostic diagnostic in nativeResult.Diagnostics)
            {
                Console.Error.WriteLine(
                    $"warning {diagnostic.Code}: {diagnostic.Message} [{diagnostic.MemberPath}]");
            }
            if (fullNativeOutputPath is not null)
            {
                WriteAtomically(fullNativeOutputPath, nativeResult.Source);
                Console.WriteLine(fullNativeOutputPath);
            }
        }
        UnrealBindingCoverageReport? coverage = null;
        if (fullReportPath is not null || options.FailOnRejected)
        {
            coverage = UnrealBindingCoverageReport.Create(
                manifest,
                result,
                nativeResult!);
        }
        if (fullReportPath is not null)
        {
            WriteAtomically(fullReportPath, UnrealBindingCoverageJson.Serialize(coverage!));
            Console.WriteLine(fullReportPath);
        }
        if (options.FailOnRejected && coverage!.Summary.RejectedCount > 0)
        {
            Console.Error.WriteLine(
                $"Unreal binding generation rejected {coverage.Summary.RejectedCount} member(s). " +
                "See diagnostics or the coverage report for details.");
            return 65;
        }
        return 0;
    }

    private static int RunEmit(IReadOnlyList<string> args)
    {
        if (!TryParseCompilationArguments(args, out AnalyzeOptions? options) ||
            string.IsNullOrWhiteSpace(options?.OutputDirectory))
        {
            PrintUsage();
            return 64;
        }

        foreach (string inputPath in options.InputPaths)
        {
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Input file does not exist: {inputPath}");
                return 66;
            }
        }

        CSharpCompilationRequest request;
        try
        {
            request = LoadAnalysisRequest(options);
        }
        catch (CSharpProjectLoadException exception)
        {
            Console.Error.WriteLine($"error USCS3001: {exception.Message}");
            return 2;
        }
        CSharpProgramEmitResult result = new CSharpProgramEmitter().Emit(request);
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!result.Succeeded || result.Image is null)
        {
            Console.Error.WriteLine(
                "Emission failed; PE, PDB, source-map, and declaration artifacts were not emitted.");
            return 2;
        }

        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        string baseName = GetOutputBaseName(options);
        string assemblyPath = Path.Combine(outputDirectory, $"{baseName}.dll");
        string pdbPath = Path.Combine(outputDirectory, $"{baseName}.pdb");
        string sourceMapPath = Path.Combine(outputDirectory, $"{baseName}.sourcemap.json");
        string assetPath = Path.Combine(outputDirectory, $"{baseName}.asset.json");
        var outputs = new List<OutputFile>
        {
            new OutputFile(assemblyPath, result.Image.PortableExecutable),
            new OutputFile(pdbPath, result.Image.PortablePdb),
            new OutputFile(
                sourceMapPath,
                CSharpProgramSourceMapJson.Serialize(result.Image.SourceMap) + "\n"),
            new OutputFile(
                assetPath,
                ScriptAssetJson.Serialize(result.Image.ScriptAssets) + "\n"),
        };
        foreach (IdentitySidecarDocument sidecar in result.Image.IdentitySidecars)
        {
            outputs.Add(new OutputFile(
                Path.Combine(outputDirectory, GetIdentityArtifactName(sidecar.SourcePath)),
                IdentitySidecarJson.Serialize(sidecar) + "\n"));
        }

        WriteTransactionally(outputs);
        foreach (OutputFile output in outputs)
        {
            Console.WriteLine(Path.GetFullPath(output.Path));
        }

        return 0;
    }

    private static int RunExecute(IReadOnlyList<string> args)
    {
        if (!TryParseExecuteArguments(args, out ExecuteOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without execution options.");
        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file does not exist: {options.InputPath}");
            return 66;
        }

        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        CSharpSourceDocument[] sources = LoadSources(
            [options.InputPath],
            options.IdentityPath is null ? [] : [options.IdentityPath]);
        var request = new CSharpCompilationRequest(
            options.AssemblyName,
            sources,
            options.ReferencePaths,
            effectCatalog);
        try
        {
            using CoreClrExecutionSession session = new CoreClrExecutionHost().CompileAndLoad(request);
            SemanticId methodId = options.MethodId ??
                session.ResolveVisualMethodId(options.QualifiedMethodName!);
            ExecutionArgument[] arguments = ParseExecutionArguments(options.ArgumentsJson);
            ExecutionResult result = session.Execute(new ExecutionInvocation(methodId, arguments));
            Console.WriteLine(ExecutionJson.Serialize(result));
            return result.Outcome == ExecutionOutcome.Returned ? 0 : 3;
        }
        catch (ExecutionCompilationException exception)
        {
            foreach (UnifiedDiagnostic diagnostic in exception.Diagnostics)
            {
                Console.Error.WriteLine(FormatDiagnostic(diagnostic));
            }

            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (ExecutionInvocationException exception)
        {
            Console.Error.WriteLine($"error {exception.Code}: {exception.Message}");
            return 3;
        }
    }

    private static int RunDebugServe(IReadOnlyList<string> args)
    {
        if (!TryParseDebugServeArguments(args, out DebugServeOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without debug server options.");
        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file does not exist: {options.InputPath}");
            return 66;
        }

        if (!Directory.Exists(options.ContentRoot))
        {
            Console.Error.WriteLine($"Visual workbench root does not exist: {options.ContentRoot}");
            return 66;
        }

        string fullInputPath = Path.GetFullPath(options.InputPath);
        string logicalPath = GetLogicalInputPath(fullInputPath);
        string identityPath = options.IdentityPath ?? options.InputPath + ".usids";
        IdentitySidecarDocument identitySidecar = File.Exists(identityPath)
            ? LoadIdentitySidecar(identityPath)
            : IdentitySidecarDocument.CreateEmpty(logicalPath);
        string[] additionalSourcePaths = options.AdditionalSourcePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string? missingSource = additionalSourcePaths.FirstOrDefault(path => !File.Exists(path));
        if (missingSource is not null)
        {
            Console.Error.WriteLine($"Additional source file does not exist: {missingSource}");
            return 66;
        }

        CSharpSourceDocument[] sources =
        [
            new CSharpSourceDocument(
                logicalPath,
                File.ReadAllText(fullInputPath),
                identitySidecar),
            .. additionalSourcePaths
                .Where(path => !string.Equals(path, fullInputPath, StringComparison.OrdinalIgnoreCase))
                .Select(path => new CSharpSourceDocument(
                    GetLogicalInputPath(path),
                    File.ReadAllText(path),
                    IdentitySidecarDocument.CreateEmpty(GetLogicalInputPath(path)))),
        ];
        var sourceFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [logicalPath] = fullInputPath,
        };
        foreach (string path in additionalSourcePaths.Where(path =>
            !string.Equals(path, fullInputPath, StringComparison.OrdinalIgnoreCase)))
        {
            sourceFiles.Add(GetLogicalInputPath(path), path);
        }
        var request = new CSharpCompilationRequest(
            options.AssemblyName,
            sources,
            options.ReferencePaths,
            LoadEffectCatalog(options.EffectCatalogPath));
        try
        {
            using var server = new CoreClrDebugHttpServer(
                request,
                fullInputPath,
                identityPath,
                options.ContentRoot,
                options.Port,
                new AuthoringArtifactOutputs(
                    options.DeclarationOutputPath,
                    options.SurfaceOutputPath,
                    options.ManagedAssemblyOutputPath,
                    options.PortablePdbOutputPath),
                sourceFiles);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                Console.WriteLine(
                    $"UnifiedScript visual debug server ready at http://127.0.0.1:{options.Port}/");
                server.RunAsync(cancellation.Token).GetAwaiter().GetResult();
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            return 0;
        }
        catch (ExecutionCompilationException exception)
        {
            foreach (UnifiedDiagnostic diagnostic in exception.Diagnostics)
            {
                Console.Error.WriteLine(FormatDiagnostic(diagnostic));
            }

            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static int RunLayout(IReadOnlyList<string> args, bool synchronize)
    {
        if (!TryParseLayoutArguments(args, synchronize, out LayoutOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException("Argument parsing succeeded without layout options.");
        if (!File.Exists(options.GraphPath))
        {
            Console.Error.WriteLine($"Graph document does not exist: {options.GraphPath}");
            return 66;
        }

        if (options.LayoutPath is not null && !File.Exists(options.LayoutPath))
        {
            Console.Error.WriteLine($"Graph layout sidecar does not exist: {options.LayoutPath}");
            return 66;
        }

        string fullGraphPath = Path.GetFullPath(options.GraphPath);
        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        EnsureDifferentPath(
            fullGraphPath,
            fullOutputPath,
            "Graph layout output cannot overwrite the semantic graph document.");
        GraphDocument graph = GraphJson.Deserialize(File.ReadAllText(fullGraphPath));
        var layoutSynchronizer = new GraphLayoutSynchronizer();
        GraphLayoutDocument result;
        if (options.LayoutPath is null)
        {
            result = layoutSynchronizer.Create(graph);
        }
        else
        {
            string fullLayoutPath = Path.GetFullPath(options.LayoutPath);
            EnsureDifferentPath(
                fullLayoutPath,
                fullOutputPath,
                "In-place graph layout synchronization is not supported; choose a different --out path.");
            result = layoutSynchronizer.Synchronize(
                graph,
                GraphLayoutJson.Deserialize(File.ReadAllText(fullLayoutPath)));
        }

        WriteAtomically(fullOutputPath, GraphLayoutJson.Serialize(result) + "\n");
        Console.WriteLine(fullOutputPath);
        return 0;
    }

    private static int RunAnalyze(IReadOnlyList<string> args)
    {
        if (!TryParseCompilationArguments(args, out AnalyzeOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException("Argument parsing succeeded without options.");
        foreach (string inputPath in options.InputPaths)
        {
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Input file does not exist: {inputPath}");
                return 66;
            }
        }

        CSharpCompilationRequest request;
        try
        {
            request = LoadAnalysisRequest(options);
        }
        catch (CSharpProjectLoadException exception)
        {
            Console.Error.WriteLine($"error USCS3001: {exception.Message}");
            return 2;
        }
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!result.Succeeded || result.Hir is null || result.Semantics is null ||
            result.ScriptAssets is null)
        {
            Console.Error.WriteLine(
                "Analysis failed; declaration, HIR, semantics, and graph artifacts were not emitted.");
            return 2;
        }

        GraphDocument graph = new HirGraphProjector().Project(
            result.Hir,
            result.Semantics,
            result.DeclarationReferences);
        string hirJson = HirJson.Serialize(HirDocument.Create(result.Hir));
        string semanticJson = SemanticJson.Serialize(result.Semantics);
        string graphJson = GraphJson.Serialize(graph);
        string assetJson = ScriptAssetJson.Serialize(result.ScriptAssets);
        string authoringSurfaceJson = AuthoringSurfaceJson.Serialize(
            new AuthoringSurfaceProjector().Project(result.ScriptAssets, graph));
        if (options.OutputDirectory is null)
        {
            Console.WriteLine(graphJson);
            return 0;
        }

        string fullOutputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
        string baseName = GetOutputBaseName(options);
        string hirPath = Path.Combine(fullOutputDirectory, $"{baseName}.hir.json");
        string semanticPath = Path.Combine(fullOutputDirectory, $"{baseName}.semantics.json");
        string graphPath = Path.Combine(fullOutputDirectory, $"{baseName}.graph.json");
        string assetPath = Path.Combine(fullOutputDirectory, $"{baseName}.asset.json");
        string authoringSurfacePath = Path.Combine(
            fullOutputDirectory,
            $"{baseName}.surface.json");
        File.WriteAllText(hirPath, hirJson + "\n");
        File.WriteAllText(semanticPath, semanticJson + "\n");
        File.WriteAllText(graphPath, graphJson + "\n");
        File.WriteAllText(assetPath, assetJson + "\n");
        File.WriteAllText(authoringSurfacePath, authoringSurfaceJson + "\n");
        Console.WriteLine(hirPath);
        Console.WriteLine(semanticPath);
        Console.WriteLine(graphPath);
        Console.WriteLine(assetPath);
        Console.WriteLine(authoringSurfacePath);
        foreach (IdentitySidecarDocument sidecar in result.IdentitySidecars)
        {
            string identityPath = Path.Combine(
                fullOutputDirectory,
                GetIdentityArtifactName(sidecar.SourcePath));
            WriteAtomically(identityPath, IdentitySidecarJson.Serialize(sidecar) + "\n");
            Console.WriteLine(identityPath);
        }

        return 0;
    }

    private static int RunIdentityInit(IReadOnlyList<string> args)
    {
        if (!TryParseIdentityArguments(args, out IdentityOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException("Argument parsing succeeded without identity options.");
        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file does not exist: {options.InputPath}");
            return 66;
        }

        string fullInputPath = Path.GetFullPath(options.InputPath);
        string logicalPath = GetLogicalInputPath(fullInputPath);
        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        var request = new CSharpCompilationRequest(
            options.AssemblyName,
            [new CSharpSourceDocument(
                logicalPath,
                File.ReadAllText(fullInputPath),
                IdentitySidecarDocument.CreateEmpty(logicalPath))],
            options.ReferencePaths,
            effectCatalog);
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!result.Succeeded || result.IdentitySidecars.Count != 1)
        {
            Console.Error.WriteLine("Identity initialization failed; no sidecar was written.");
            return 2;
        }

        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        EnsureDifferentPath(fullInputPath, fullOutputPath, "Identity output cannot overwrite the input source.");
        WriteAtomically(
            fullOutputPath,
            IdentitySidecarJson.Serialize(result.IdentitySidecars[0]) + "\n");
        Console.WriteLine(fullOutputPath);
        return 0;
    }

    private static int RunApply(IReadOnlyList<string> args, bool isPlan)
    {
        if (!TryParsePatchArguments(args, isPlan, out PatchOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException("Argument parsing succeeded without patch options.");
        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file does not exist: {options.InputPath}");
            return 66;
        }

        if (!File.Exists(options.EditDocumentPath))
        {
            Console.Error.WriteLine(
                $"{(isPlan ? "Patch plan" : "Patch")} file does not exist: " +
                options.EditDocumentPath);
            return 66;
        }

        string fullInputPath = Path.GetFullPath(options.InputPath);
        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        StringComparison pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(fullInputPath, fullOutputPath, pathComparison))
        {
            throw new ArgumentException(
                "In-place source patching is not supported yet; choose a different --out path.");
        }

        EnsureDifferentPath(
            Path.GetFullPath(options.EditDocumentPath),
            fullOutputPath,
            "Source output cannot overwrite the input edit document.");

        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        IdentitySidecarDocument? identitySidecar = options.IdentityPath is null
            ? null
            : LoadIdentitySidecar(options.IdentityPath);
        var request = new CSharpCompilationRequest(
            options.AssemblyName,
            [new CSharpSourceDocument(
                GetLogicalInputPath(fullInputPath),
                File.ReadAllText(fullInputPath),
                identitySidecar)],
            options.ReferencePaths,
            effectCatalog);
        string editJson = File.ReadAllText(options.EditDocumentPath);
        CSharpPatchResult result = isPlan
            ? new CSharpPatchPlanApplier().Apply(
                request,
                SemanticPatchPlanJson.Deserialize(editJson))
            : new CSharpSourcePatchApplier().Apply(
                request,
                SemanticPatchJson.Deserialize(editJson));
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!result.Succeeded || result.PatchedRequest is null)
        {
            Console.Error.WriteLine(
                $"{(isPlan ? "Patch plan" : "Patch")} failed; no source file was written.");
            return 3;
        }

        string patchedSource = AssertSinglePatchedSource(result.PatchedRequest);
        var outputs = new List<OutputFile>
        {
            new(fullOutputPath, patchedSource),
        };
        if (identitySidecar is not null)
        {
            string fullIdentityInputPath = Path.GetFullPath(options.IdentityPath!);
            string fullIdentityOutputPath = Path.GetFullPath(options.IdentityOutputPath!);
            EnsureDifferentPath(
                fullIdentityInputPath,
                fullIdentityOutputPath,
                "Identity output cannot overwrite the input sidecar.");
            EnsureDifferentPath(
                fullIdentityInputPath,
                fullOutputPath,
                "Source output cannot overwrite the input identity sidecar.");
            EnsureDifferentPath(
                fullInputPath,
                fullIdentityOutputPath,
                "Identity output cannot overwrite the input source.");
            EnsureDifferentPath(
                Path.GetFullPath(options.EditDocumentPath),
                fullIdentityOutputPath,
                "Identity output cannot overwrite the input edit document.");
            IdentitySidecarDocument updatedSidecar = AssertSingleIdentitySidecar(result.Analysis) with
            {
                SourcePath = GetLogicalInputPath(fullOutputPath),
            };
            outputs.Add(new OutputFile(
                fullIdentityOutputPath,
                IdentitySidecarJson.Serialize(updatedSidecar) + "\n"));
        }

        WriteTransactionally(outputs);
        Console.WriteLine(fullOutputPath);
        if (options.IdentityOutputPath is not null)
        {
            Console.WriteLine(Path.GetFullPath(options.IdentityOutputPath));
        }

        return 0;
    }

    private static int RunDeclarationApply(IReadOnlyList<string> args)
    {
        if (!TryParseDeclarationPatchArguments(args, out DeclarationPatchOptions? options))
        {
            PrintUsage();
            return 64;
        }

        options = options ?? throw new InvalidOperationException(
            "Argument parsing succeeded without declaration patch options.");
        if (!File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file does not exist: {options.InputPath}");
            return 66;
        }

        if (!File.Exists(options.PatchPath))
        {
            Console.Error.WriteLine($"Declaration patch file does not exist: {options.PatchPath}");
            return 66;
        }

        string fullInputPath = Path.GetFullPath(options.InputPath);
        string fullPatchPath = Path.GetFullPath(options.PatchPath);
        string fullOutputPath = Path.GetFullPath(options.OutputPath);
        string fullAssetOutputPath = Path.GetFullPath(options.AssetOutputPath);
        var inputPaths = new List<string> { fullInputPath, fullPatchPath };
        if (options.IdentityPath is not null)
        {
            inputPaths.Add(Path.GetFullPath(options.IdentityPath));
        }

        var outputPaths = new List<string> { fullOutputPath, fullAssetOutputPath };
        if (options.IdentityOutputPath is not null)
        {
            outputPaths.Add(Path.GetFullPath(options.IdentityOutputPath));
        }

        foreach (string inputPath in inputPaths)
        {
            foreach (string outputPath in outputPaths)
            {
                EnsureDifferentPath(
                    inputPath,
                    outputPath,
                    "Declaration patch outputs cannot overwrite source, patch, or identity inputs.");
            }
        }

        IdentitySidecarDocument? identitySidecar = options.IdentityPath is null
            ? null
            : LoadIdentitySidecar(options.IdentityPath);
        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        var request = new CSharpCompilationRequest(
            options.AssemblyName,
            [new CSharpSourceDocument(
                GetLogicalInputPath(fullInputPath),
                File.ReadAllText(fullInputPath),
                identitySidecar)],
            options.ReferencePaths,
            effectCatalog);
        DeclarationPatchDocument patch = DeclarationPatchJson.Deserialize(File.ReadAllText(fullPatchPath));
        CSharpPatchResult result = new CSharpDeclarationPatchApplier().Apply(request, patch);
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!result.Succeeded || result.PatchedRequest is null || result.Analysis?.ScriptAssets is null)
        {
            Console.Error.WriteLine(
                "Declaration patch failed; source, identity, and asset outputs were not written.");
            return 3;
        }

        string patchedSource = AssertSinglePatchedSource(result.PatchedRequest);
        string outputLogicalPath = GetLogicalInputPath(fullOutputPath);
        IdentitySidecarDocument? updatedSidecar = identitySidecar is null
            ? null
            : AssertSingleIdentitySidecar(result.Analysis) with { SourcePath = outputLogicalPath };
        var outputRequest = new CSharpCompilationRequest(
            options.AssemblyName,
            [new CSharpSourceDocument(outputLogicalPath, patchedSource, updatedSidecar)],
            options.ReferencePaths,
            effectCatalog);
        CSharpAnalysisResult outputAnalysis = new CSharpSemanticPipeline().Analyze(outputRequest);
        foreach (UnifiedDiagnostic diagnostic in outputAnalysis.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!outputAnalysis.Succeeded || outputAnalysis.ScriptAssets is null)
        {
            Console.Error.WriteLine(
                "Declaration patch output validation failed; no output files were written.");
            return 3;
        }

        var outputs = new List<OutputFile>
        {
            new(fullOutputPath, patchedSource),
            new(fullAssetOutputPath, ScriptAssetJson.Serialize(outputAnalysis.ScriptAssets) + "\n"),
        };
        if (updatedSidecar is not null)
        {
            IdentitySidecarDocument outputSidecar = AssertSingleIdentitySidecar(outputAnalysis);
            outputs.Add(new OutputFile(
                Path.GetFullPath(options.IdentityOutputPath!),
                IdentitySidecarJson.Serialize(outputSidecar) + "\n"));
        }

        WriteTransactionally(outputs);
        foreach (OutputFile output in outputs)
        {
            Console.WriteLine(Path.GetFullPath(output.Path));
        }

        return 0;
    }

    private static CSharpSourceDocument[] LoadSources(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string> identityPaths)
    {
        var identities = new Dictionary<string, IdentitySidecarDocument>(StringComparer.Ordinal);
        foreach (string identityPath in identityPaths)
        {
            IdentitySidecarDocument sidecar = LoadIdentitySidecar(identityPath);
            if (!identities.TryAdd(sidecar.SourcePath, sidecar))
            {
                throw new ArgumentException(
                    $"More than one identity sidecar was supplied for '{sidecar.SourcePath}'.");
            }
        }

        CSharpSourceDocument[] sources = inputPaths
            .Select(Path.GetFullPath)
            .Select(fullPath =>
            {
                string logicalPath = GetLogicalInputPath(fullPath);
                identities.Remove(logicalPath, out IdentitySidecarDocument? sidecar);
                return new CSharpSourceDocument(
                    logicalPath,
                    File.ReadAllText(fullPath),
                    sidecar);
            })
            .ToArray();
        if (identities.Count > 0)
        {
            throw new ArgumentException(
                $"Identity sidecar '{identities.Values.First().SourcePath}' does not match any input source.");
        }

        return sources;
    }

    private static CSharpCompilationRequest LoadAnalysisRequest(AnalyzeOptions options)
    {
        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        bool projectInput = options.InputPaths.Count == 1 &&
            string.Equals(
                Path.GetExtension(options.InputPaths[0]),
                ".csproj",
                StringComparison.OrdinalIgnoreCase);
        if (!projectInput && options.InputPaths.Any(path =>
                string.Equals(
                    Path.GetExtension(path),
                    ".csproj",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new CSharpProjectLoadException(
                "A .csproj must be the command's only input; MSBuild owns its complete Compile item set.");
        }
        if (!projectInput)
        {
            if (options.Configuration != "Debug" || options.TargetFramework is not null ||
                !options.Restore || !options.Build)
            {
                throw new CSharpProjectLoadException(
                    "--configuration, --framework, --no-restore, and --no-build require a .csproj input.");
            }
            CSharpSourceDocument[] sources = LoadSources(options.InputPaths, options.IdentityPaths);
            return new CSharpCompilationRequest(
                options.AssemblyName ?? "UnifiedScript.Analysis",
                sources,
                options.ReferencePaths,
                effectCatalog);
        }

        CSharpProjectLoadResult loaded = new CSharpProjectLoader().Load(
            options.InputPaths[0],
            effectCatalog,
            new CSharpProjectLoadOptions(
                options.Configuration,
                options.TargetFramework,
                options.Restore,
                options.Build));
        CSharpSourceDocument[] projectSources = AttachIdentitySidecars(
            loaded.Request.Sources,
            options.IdentityPaths);
        string[] compileReferences = loaded.Request.ReferencePaths
            .Concat(options.ReferencePaths.Select(Path.GetFullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] runtimeReferences = loaded.Request.RuntimeReferencePaths
            .Concat(options.ReferencePaths.Select(Path.GetFullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new CSharpCompilationRequest(
            options.AssemblyName ?? loaded.Request.AssemblyName,
            projectSources,
            compileReferences,
            effectCatalog,
            loaded.Request.Settings,
            runtimeReferences,
            loaded.Request.IncludeDefaultReferences);
    }

    private static CSharpSourceDocument[] AttachIdentitySidecars(
        IReadOnlyList<CSharpSourceDocument> sources,
        IReadOnlyList<string> identityPaths)
    {
        var identities = new Dictionary<string, IdentitySidecarDocument>(StringComparer.Ordinal);
        foreach (string identityPath in identityPaths)
        {
            IdentitySidecarDocument sidecar = LoadIdentitySidecar(identityPath);
            if (!identities.TryAdd(sidecar.SourcePath, sidecar))
            {
                throw new ArgumentException(
                    $"More than one identity sidecar was supplied for '{sidecar.SourcePath}'.");
            }
        }
        CSharpSourceDocument[] attached = sources
            .Select(source =>
            {
                identities.Remove(source.LogicalPath, out IdentitySidecarDocument? sidecar);
                return new CSharpSourceDocument(
                    source.LogicalPath,
                    source.Source,
                    sidecar,
                    source.IdentityPins);
            })
            .ToArray();
        if (identities.Count > 0)
        {
            throw new ArgumentException(
                $"Identity sidecar '{identities.Values.First().SourcePath}' does not match any project source.");
        }
        return attached;
    }

    private static IdentitySidecarDocument LoadIdentitySidecar(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Identity sidecar does not exist.", path);
        }

        return IdentitySidecarJson.Deserialize(File.ReadAllText(path));
    }

    private static MethodEffectCatalog LoadEffectCatalog(string? path)
    {
        MethodEffectCatalog catalog = MethodEffectCatalog.CreateDefault();
        if (path is null)
        {
            return catalog;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Effect catalog does not exist.", path);
        }

        MethodEffectCatalogDocument document = MethodEffectCatalog.ReadDocument(File.ReadAllText(path));
        return catalog.WithOverrides(document.Rules);
    }

    private static bool TryParseCompilationArguments(
        IReadOnlyList<string> args,
        out AnalyzeOptions? options)
    {
        options = null;
        if (args.Count < 2 ||
            args[0] is not ("analyze" or "analyze-files" or "emit" or "emit-files"))
        {
            return false;
        }

        string command = args[0];
        var inputPaths = new List<string>();
        int index = 1;
        while (index < args.Count && !args[index].StartsWith("--", StringComparison.Ordinal))
        {
            inputPaths.Add(args[index++]);
        }

        if (inputPaths.Count == 0 ||
            command is "analyze" or "emit" && inputPaths.Count != 1)
        {
            return false;
        }

        string? outputDirectory = null;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string configuration = "Debug";
        string? targetFramework = null;
        bool restore = true;
        bool build = true;
        var referencePaths = new List<string>();
        var identityPaths = new List<string>();
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--no-restore" && restore)
            {
                restore = false;
                continue;
            }
            if (option == "--no-build" && build)
            {
                build = false;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--out" when outputDirectory is null:
                    outputDirectory = value;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--configuration" when configuration == "Debug":
                    configuration = value;
                    break;
                case "--framework" when targetFramework is null:
                    targetFramework = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                case "--identity":
                    identityPaths.Add(value);
                    break;
                default:
                    return false;
            }
        }

        options = new AnalyzeOptions(
            inputPaths,
            outputDirectory,
            effectCatalogPath,
            assemblyName,
            referencePaths,
            identityPaths,
            configuration,
            targetFramework,
            restore,
            build);
        return true;
    }

    private static bool TryParseUnrealBindingArguments(
        IReadOnlyList<string> args,
        out UnrealBindingOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "ue-bindings" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string manifestPath = args[1];
        string? outputPath = null;
        string? nativeOutputPath = null;
        string? reportPath = null;
        string? bindingNamespace = null;
        bool failOnRejected = false;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (option == "--fail-on-rejected" && !failOnRejected)
            {
                failOnRejected = true;
                continue;
            }
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--namespace" when bindingNamespace is null:
                    bindingNamespace = value;
                    break;
                case "--native-out" when nativeOutputPath is null:
                    nativeOutputPath = value;
                    break;
                case "--report" when reportPath is null:
                    reportPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (outputPath is null)
        {
            return false;
        }

        options = new UnrealBindingOptions(
            manifestPath,
            outputPath,
            bindingNamespace ?? "UnifiedScript.Unreal.Generated",
            nativeOutputPath,
            reportPath,
            failOnRejected);
        return true;
    }

    private static bool TryParseExecuteArguments(
        IReadOnlyList<string> args,
        out ExecuteOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "execute" || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? qualifiedMethodName = null;
        SemanticId? methodId = null;
        string argumentsJson = "[]";
        bool hasArguments = false;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string? identityPath = null;
        var referencePaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--method" when qualifiedMethodName is null:
                    qualifiedMethodName = value;
                    break;
                case "--method-id" when methodId is null && SemanticId.TryParse(value, out SemanticId parsed):
                    methodId = parsed;
                    break;
                case "--args" when !hasArguments:
                    argumentsJson = value;
                    hasArguments = true;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                case "--identity" when identityPath is null:
                    identityPath = value;
                    break;
                default:
                    return false;
            }
        }

        if ((qualifiedMethodName is null) == (methodId is null))
        {
            return false;
        }

        options = new ExecuteOptions(
            inputPath,
            qualifiedMethodName,
            methodId,
            argumentsJson,
            effectCatalogPath,
            assemblyName ?? "UnifiedScript.Execution",
            referencePaths,
            identityPath);
        return true;
    }

    private static bool TryParseDebugServeArguments(
        IReadOnlyList<string> args,
        out DebugServeOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "debug-serve" || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? contentRoot = null;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string? identityPath = null;
        string? declarationOutputPath = null;
        string? surfaceOutputPath = null;
        string? managedAssemblyOutputPath = null;
        string? portablePdbOutputPath = null;
        int port = 4173;
        bool hasPort = false;
        var referencePaths = new List<string>();
        var additionalSourcePaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--root" when contentRoot is null:
                    contentRoot = value;
                    break;
                case "--port" when !hasPort && int.TryParse(value, out int parsedPort):
                    port = parsedPort;
                    hasPort = true;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                case "--source":
                    additionalSourcePaths.Add(value);
                    break;
                case "--identity" when identityPath is null:
                    identityPath = value;
                    break;
                case "--asset-out" when declarationOutputPath is null:
                    declarationOutputPath = value;
                    break;
                case "--surface-out" when surfaceOutputPath is null:
                    surfaceOutputPath = value;
                    break;
                case "--managed-out" when managedAssemblyOutputPath is null:
                    managedAssemblyOutputPath = value;
                    break;
                case "--pdb-out" when portablePdbOutputPath is null:
                    portablePdbOutputPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (contentRoot is null || port is < 1024 or > 65535)
        {
            return false;
        }

        options = new DebugServeOptions(
            inputPath,
            contentRoot,
            port,
            effectCatalogPath,
            assemblyName ?? "UnifiedScript.Analysis",
            referencePaths,
            identityPath,
            additionalSourcePaths,
            declarationOutputPath,
            surfaceOutputPath,
            managedAssemblyOutputPath,
            portablePdbOutputPath);
        return true;
    }

    private static ExecutionArgument[] ParseExecutionArguments(string argumentsJson)
    {
        using JsonDocument document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ExecutionInvocationException(
                "UVR1005",
                "--args must contain one JSON array whose elements match the method parameters.");
        }

        return document.RootElement
            .EnumerateArray()
            .Select(static element => new ExecutionArgument(element.GetRawText()))
            .ToArray();
    }

    private static bool TryParseIdentityArguments(IReadOnlyList<string> args, out IdentityOptions? options)
    {
        options = null;
        if (args.Count < 4 || args[0] != "identity-init" || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? outputPath = null;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        var referencePaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                default:
                    return false;
            }
        }

        if (outputPath is null)
        {
            return false;
        }

        options = new IdentityOptions(
            inputPath,
            outputPath,
            effectCatalogPath,
            assemblyName ?? "UnifiedScript.Analysis",
            referencePaths);
        return true;
    }

    private static bool TryParseLayoutArguments(
        IReadOnlyList<string> args,
        bool synchronize,
        out LayoutOptions? options)
    {
        options = null;
        string command = synchronize ? "layout-sync" : "layout-init";
        if (args.Count < 4 || args[0] != command || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string graphPath = args[1];
        string? layoutPath = null;
        string? outputPath = null;
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--layout" when synchronize && layoutPath is null:
                    layoutPath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (outputPath is null || synchronize && layoutPath is null)
        {
            return false;
        }

        options = new LayoutOptions(graphPath, layoutPath, outputPath);
        return true;
    }

    private static bool TryParsePatchArguments(
        IReadOnlyList<string> args,
        bool isPlan,
        out PatchOptions? options)
    {
        options = null;
        string command = isPlan ? "apply-plan" : "apply-patch";
        string editOption = isPlan ? "--plan" : "--patch";
        if (args.Count < 6 || args[0] != command || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? editDocumentPath = null;
        string? outputPath = null;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string? identityPath = null;
        string? identityOutputPath = null;
        var referencePaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            if (option == editOption && editDocumentPath is null)
            {
                editDocumentPath = value;
                continue;
            }

            switch (option)
            {
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                case "--identity" when identityPath is null:
                    identityPath = value;
                    break;
                case "--out-identity" when identityOutputPath is null:
                    identityOutputPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (editDocumentPath is null || outputPath is null ||
            (identityPath is null) != (identityOutputPath is null))
        {
            return false;
        }

        options = new PatchOptions(
            inputPath,
            editDocumentPath,
            outputPath,
            effectCatalogPath,
            assemblyName ?? "UnifiedScript.Analysis",
            referencePaths,
            identityPath,
            identityOutputPath);
        return true;
    }

    private static bool TryParseDeclarationPatchArguments(
        IReadOnlyList<string> args,
        out DeclarationPatchOptions? options)
    {
        options = null;
        if (args.Count < 8 || args[0] != "apply-declaration-patch" ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? patchPath = null;
        string? outputPath = null;
        string? assetOutputPath = null;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        string? identityPath = null;
        string? identityOutputPath = null;
        var referencePaths = new List<string>();
        int index = 2;
        while (index < args.Count)
        {
            string option = args[index++];
            if (index >= args.Count)
            {
                return false;
            }

            string value = args[index++];
            switch (option)
            {
                case "--declaration-patch" when patchPath is null:
                    patchPath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--out-asset" when assetOutputPath is null:
                    assetOutputPath = value;
                    break;
                case "--effects" when effectCatalogPath is null:
                    effectCatalogPath = value;
                    break;
                case "--assembly" when assemblyName is null:
                    assemblyName = value;
                    break;
                case "--reference":
                    referencePaths.Add(value);
                    break;
                case "--identity" when identityPath is null:
                    identityPath = value;
                    break;
                case "--out-identity" when identityOutputPath is null:
                    identityOutputPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (patchPath is null || outputPath is null || assetOutputPath is null ||
            (identityPath is null) != (identityOutputPath is null))
        {
            return false;
        }

        options = new DeclarationPatchOptions(
            inputPath,
            patchPath,
            outputPath,
            assetOutputPath,
            effectCatalogPath,
            assemblyName ?? "UnifiedScript.Analysis",
            referencePaths,
            identityPath,
            identityOutputPath);
        return true;
    }

    private static string FormatDiagnostic(UnifiedDiagnostic diagnostic) =>
        $"{diagnostic.Span.FilePath}({diagnostic.Span.Start.Line},{diagnostic.Span.Start.Column}): " +
        $"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Code}: {diagnostic.Message}";

    private static string GetLogicalInputPath(string fullInputPath)
    {
        string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, fullInputPath);
        bool escapesWorkingDirectory = relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        string path = escapesWorkingDirectory ? fullInputPath : relativePath;
        return path.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string GetOutputBaseName(AnalyzeOptions options)
    {
        if (options.InputPaths.Count == 1)
        {
            return Path.GetFileNameWithoutExtension(options.InputPaths[0]);
        }

        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        return new string((options.AssemblyName ?? "UnifiedScript.Analysis")
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
    }

    private static string GetIdentityArtifactName(string sourcePath)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(sourcePath
            .Select(character => character is '/' or '\\' || invalidCharacters.Contains(character)
                ? '_'
                : character)
            .ToArray()) + ".usids";
    }

    private static string AssertSinglePatchedSource(CSharpCompilationRequest request)
    {
        if (request.Sources.Count != 1)
        {
            throw new InvalidOperationException("The apply-patch CLI currently requires one source document.");
        }

        return request.Sources[0].Source;
    }

    private static IdentitySidecarDocument AssertSingleIdentitySidecar(CSharpAnalysisResult? analysis)
    {
        if (analysis?.IdentitySidecars.Count != 1)
        {
            throw new InvalidOperationException("A sidecar-enabled patch must produce one updated identity sidecar.");
        }

        return analysis.IdentitySidecars[0];
    }

    private static void EnsureDifferentPath(string first, string second, string message)
    {
        StringComparison pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(first, second, pathComparison))
        {
            throw new ArgumentException(message);
        }
    }

    private static void WriteAtomically(string outputPath, string content)
        => WriteTransactionally([new OutputFile(outputPath, content)]);

    private static void WriteTransactionally(IReadOnlyList<OutputFile> outputs)
    {
        string[] fullPaths = outputs.Select(output => Path.GetFullPath(output.Path)).ToArray();
        if (fullPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fullPaths.Length)
        {
            throw new ArgumentException("Transactional output paths must be distinct.", nameof(outputs));
        }

        var pending = new List<PendingOutput>(outputs.Count);
        var movedTargets = new List<PendingOutput>(outputs.Count);
        bool committed = false;
        try
        {
            for (int index = 0; index < outputs.Count; index++)
            {
                string fullPath = fullPaths[index];
                string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
                Directory.CreateDirectory(directory);
                string nonce = Guid.NewGuid().ToString("N");
                string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{nonce}.tmp");
                string backupPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{nonce}.bak");
                File.WriteAllBytes(temporaryPath, outputs[index].Content);
                pending.Add(new PendingOutput(fullPath, temporaryPath, backupPath));
            }

            foreach (PendingOutput output in pending)
            {
                if (File.Exists(output.Path))
                {
                    File.Move(output.Path, output.BackupPath);
                }

                File.Move(output.TemporaryPath, output.Path);
                movedTargets.Add(output);
            }

            committed = true;
        }
        catch
        {
            foreach (PendingOutput output in movedTargets.AsEnumerable().Reverse())
            {
                TryDelete(output.Path);
            }

            foreach (PendingOutput output in pending.AsEnumerable().Reverse())
            {
                if (File.Exists(output.BackupPath))
                {
                    File.Move(output.BackupPath, output.Path, overwrite: true);
                }
            }

            throw;
        }
        finally
        {
            foreach (PendingOutput output in pending)
            {
                TryDelete(output.TemporaryPath);
                if (committed)
                {
                    TryDelete(output.BackupPath);
                }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        "Usage:\n" +
        "  unifiedscript analyze <input.cs|project.csproj> [options]\n" +
        "  unifiedscript analyze-files <input.cs> <additional.cs>... [options]\n" +
        "  unifiedscript emit <input.cs|project.csproj> --out <directory> [options]\n" +
        "  unifiedscript emit-files <input.cs> <additional.cs>... --out <directory> [options]\n" +
        "  unifiedscript identity-init <input.cs> --out <identity.usids> [options]\n" +
        "  unifiedscript layout-init <graph.json> --out <layout.usgraph>\n" +
        "  unifiedscript layout-sync <graph.json> --layout <layout.usgraph> --out <updated.usgraph>\n" +
        "  unifiedscript apply-patch <input.cs> --patch <patch.json> --out <output.cs> [options]\n" +
        "  unifiedscript apply-plan <input.cs> --plan <plan.json> --out <output.cs> [options]\n" +
        "  unifiedscript apply-declaration-patch <input.cs> --declaration-patch <patch.json> " +
        "--out <output.cs> --out-asset <asset.json> [options]\n" +
        "  unifiedscript execute <input.cs> (--method <name> | --method-id <us_...>) [options]\n" +
        "  unifiedscript debug-serve <input.cs> --root <visual-editor-directory> [--port <number>] [options]\n" +
        "  unifiedscript ue-bindings <reflection.json> --out <bindings.cs> [--native-out <thunks.cpp>] [--report <coverage.json>] [--fail-on-rejected] [--namespace <name>]\n" +
        "Options:\n" +
        "  --out <path>            Artifact directory, initialized sidecar, or patched source output.\n" +
        "  --patch <patch.json>    Semantic patch document required by apply-patch.\n" +
        "  --plan <plan.json>      Ordered semantic patch plan required by apply-plan.\n" +
        "  --declaration-patch <file> Typed declaration patch required by apply-declaration-patch.\n" +
        "  --out-asset <file>      Revalidated Declaration IR output.\n" +
        "  --identity <file.usids> Use a persistent identity sidecar; may repeat for *-files commands.\n" +
        "  --out-identity <file>   Updated sidecar output required by sidecar-enabled apply-patch.\n" +
        "  --method <name>         Fully qualified Visual method required by execute.\n" +
        "  --method-id <us_...>    Exact semantic method ID alternative for overloaded methods.\n" +
        "  --args <json-array>     Execute arguments; defaults to an empty array.\n" +
        "  --layout <file.usgraph> Existing visual layout required by layout-sync.\n" +
        "  --root <directory>      Static visual workbench root required by debug-serve.\n" +
        "  --port <number>         Loopback debug server port; defaults to 4173.\n" +
        "  --source <input.cs>     Add a compilation source to debug-serve; may repeat.\n" +
        "  --asset-out <path>      Transactional Declaration IR output for debug-serve.\n" +
        "  --surface-out <path>    Transactional Authoring Surface output for debug-serve.\n" +
        "  --managed-out <path>    Transactional managed assembly output for debug-serve.\n" +
        "  --pdb-out <path>        Transactional portable PDB output for debug-serve.\n" +
        "  --namespace <name>      Namespace for generated Unreal managed bindings.\n" +
        "  --native-out <path>     Also emit ABI v2 direct native thunk C++.\n" +
        "  --report <path>         Emit native/fallback/rejected UE API coverage JSON.\n" +
        "  --fail-on-rejected      Return data-error status when any UE member is rejected.\n" +
        "  --assembly <name>        Set the analysis assembly name.\n" +
        "  --effects <catalog.json> Apply effect catalog overrides.\n" +
        "  --reference <assembly>   Add a metadata reference; may be repeated.\n" +
        "  --configuration <name>   Select the MSBuild configuration; defaults to Debug.\n" +
        "  --framework <tfm>        Select one framework from a multi-target .csproj.\n" +
        "  --no-restore             Do not restore the .csproj before loading.\n" +
        "  --no-build               Do not build the .csproj before loading.\n" +
        "Analyze writes graph JSON to stdout when --out is omitted; emit and apply commands require --out.");

    private sealed record AnalyzeOptions(
        IReadOnlyList<string> InputPaths,
        string? OutputDirectory,
        string? EffectCatalogPath,
        string? AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        IReadOnlyList<string> IdentityPaths,
        string Configuration,
        string? TargetFramework,
        bool Restore,
        bool Build);

    private sealed record ExecuteOptions(
        string InputPath,
        string? QualifiedMethodName,
        SemanticId? MethodId,
        string ArgumentsJson,
        string? EffectCatalogPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        string? IdentityPath);

    private sealed record DebugServeOptions(
        string InputPath,
        string ContentRoot,
        int Port,
        string? EffectCatalogPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        string? IdentityPath,
        IReadOnlyList<string> AdditionalSourcePaths,
        string? DeclarationOutputPath,
        string? SurfaceOutputPath,
        string? ManagedAssemblyOutputPath,
        string? PortablePdbOutputPath);

    private sealed record UnrealBindingOptions(
        string ManifestPath,
        string OutputPath,
        string Namespace,
        string? NativeOutputPath,
        string? ReportPath,
        bool FailOnRejected);

    private sealed record IdentityOptions(
        string InputPath,
        string OutputPath,
        string? EffectCatalogPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths);

    private sealed record LayoutOptions(
        string GraphPath,
        string? LayoutPath,
        string OutputPath);

    private sealed record PatchOptions(
        string InputPath,
        string EditDocumentPath,
        string OutputPath,
        string? EffectCatalogPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        string? IdentityPath,
        string? IdentityOutputPath);

    private sealed record DeclarationPatchOptions(
        string InputPath,
        string PatchPath,
        string OutputPath,
        string AssetOutputPath,
        string? EffectCatalogPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths,
        string? IdentityPath,
        string? IdentityOutputPath);

    private sealed record OutputFile(string Path, byte[] Content)
    {
        public OutputFile(string path, string content)
            : this(path, System.Text.Encoding.UTF8.GetBytes(content))
        {
        }
    }

    private sealed record PendingOutput(string Path, string TemporaryPath, string BackupPath);
}

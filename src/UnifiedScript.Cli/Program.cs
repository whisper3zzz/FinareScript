using System.Text.Json;
using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Graph;
using UnifiedScript.Semantics;

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
        if (args.Count > 0 && args[0] == "apply-patch")
        {
            return RunApplyPatch(args);
        }

        return RunAnalyze(args);
    }

    private static int RunAnalyze(IReadOnlyList<string> args)
    {
        if (!TryParseAnalyzeArguments(args, out AnalyzeOptions? options))
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

        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        CSharpSourceDocument[] sources = options.InputPaths
            .Select(Path.GetFullPath)
            .Select(fullPath => new CSharpSourceDocument(
                GetLogicalInputPath(fullPath),
                File.ReadAllText(fullPath)))
            .ToArray();
        var request = new CSharpCompilationRequest(
            options.AssemblyName,
            sources,
            options.ReferencePaths,
            effectCatalog);
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(request);
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!result.Succeeded || result.Hir is null || result.Semantics is null)
        {
            Console.Error.WriteLine("Analysis failed; HIR, semantics, and graph artifacts were not emitted.");
            return 2;
        }

        GraphDocument graph = new HirGraphProjector().Project(result.Hir, result.Semantics);
        string hirJson = HirJson.Serialize(HirDocument.Create(result.Hir));
        string semanticJson = SemanticJson.Serialize(result.Semantics);
        string graphJson = GraphJson.Serialize(graph);
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
        File.WriteAllText(hirPath, hirJson + "\n");
        File.WriteAllText(semanticPath, semanticJson + "\n");
        File.WriteAllText(graphPath, graphJson + "\n");
        Console.WriteLine(hirPath);
        Console.WriteLine(semanticPath);
        Console.WriteLine(graphPath);
        return 0;
    }

    private static int RunApplyPatch(IReadOnlyList<string> args)
    {
        if (!TryParsePatchArguments(args, out PatchOptions? options))
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

        if (!File.Exists(options.PatchPath))
        {
            Console.Error.WriteLine($"Patch file does not exist: {options.PatchPath}");
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

        MethodEffectCatalog effectCatalog = LoadEffectCatalog(options.EffectCatalogPath);
        var request = new CSharpCompilationRequest(
            options.AssemblyName,
            [new CSharpSourceDocument(
                GetLogicalInputPath(fullInputPath),
                File.ReadAllText(fullInputPath))],
            options.ReferencePaths,
            effectCatalog);
        SemanticPatchDocument patch = SemanticPatchJson.Deserialize(File.ReadAllText(options.PatchPath));
        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);
        foreach (UnifiedDiagnostic diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(FormatDiagnostic(diagnostic));
        }

        if (!result.Succeeded || result.PatchedRequest is null)
        {
            Console.Error.WriteLine("Patch failed; no source file was written.");
            return 3;
        }

        string patchedSource = AssertSinglePatchedSource(result.PatchedRequest);
        WriteAtomically(fullOutputPath, patchedSource);
        Console.WriteLine(fullOutputPath);
        return 0;
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

    private static bool TryParseAnalyzeArguments(IReadOnlyList<string> args, out AnalyzeOptions? options)
    {
        options = null;
        if (args.Count < 2 || args[0] is not ("analyze" or "analyze-files"))
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

        if (inputPaths.Count == 0 || command == "analyze" && inputPaths.Count != 1)
        {
            return false;
        }

        string? outputDirectory = null;
        string? effectCatalogPath = null;
        string? assemblyName = null;
        var referencePaths = new List<string>();
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
                case "--out" when outputDirectory is null:
                    outputDirectory = value;
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

        options = new AnalyzeOptions(
            inputPaths,
            outputDirectory,
            effectCatalogPath,
            assemblyName ?? "UnifiedScript.Analysis",
            referencePaths);
        return true;
    }

    private static bool TryParsePatchArguments(IReadOnlyList<string> args, out PatchOptions? options)
    {
        options = null;
        if (args.Count < 6 || args[0] != "apply-patch" || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string inputPath = args[1];
        string? patchPath = null;
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
                case "--patch" when patchPath is null:
                    patchPath = value;
                    break;
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

        if (patchPath is null || outputPath is null)
        {
            return false;
        }

        options = new PatchOptions(
            inputPath,
            patchPath,
            outputPath,
            effectCatalogPath,
            assemblyName ?? "UnifiedScript.Analysis",
            referencePaths);
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
        return new string(options.AssemblyName
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
    }

    private static string AssertSinglePatchedSource(CSharpCompilationRequest request)
    {
        if (request.Sources.Count != 1)
        {
            throw new InvalidOperationException("The apply-patch CLI currently requires one source document.");
        }

        return request.Sources[0].Source;
    }

    private static void WriteAtomically(string outputPath, string content)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = Path.Combine(
            directory ?? Environment.CurrentDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        "Usage:\n" +
        "  unifiedscript analyze <input.cs> [options]\n" +
        "  unifiedscript analyze-files <input.cs> <additional.cs>... [options]\n" +
        "  unifiedscript apply-patch <input.cs> --patch <patch.json> --out <output.cs> [options]\n" +
        "Options:\n" +
        "  --out <path>            Artifact directory for analyze; source file for apply-patch.\n" +
        "  --patch <patch.json>    Semantic patch document required by apply-patch.\n" +
        "  --assembly <name>        Set the analysis assembly name.\n" +
        "  --effects <catalog.json> Apply effect catalog overrides.\n" +
        "  --reference <assembly>   Add a metadata reference; may be repeated.\n" +
        "Analyze writes graph JSON to stdout when --out is omitted; apply-patch requires --out.");

    private sealed record AnalyzeOptions(
        IReadOnlyList<string> InputPaths,
        string? OutputDirectory,
        string? EffectCatalogPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths);

    private sealed record PatchOptions(
        string InputPath,
        string PatchPath,
        string OutputPath,
        string? EffectCatalogPath,
        string AssemblyName,
        IReadOnlyList<string> ReferencePaths);
}

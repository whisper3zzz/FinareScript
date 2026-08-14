using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Graph;
using UnifiedScript.Semantics;

namespace UnifiedScript.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (!TryParseArguments(args, out string? inputPath, out string? outputDirectory))
        {
            PrintUsage();
            return 64;
        }

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input file does not exist: {inputPath}");
            return 66;
        }

        string fullInputPath = Path.GetFullPath(inputPath);
        string source = File.ReadAllText(fullInputPath);
        string logicalInputPath = GetLogicalInputPath(fullInputPath);
        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(source, logicalInputPath);
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

        if (outputDirectory is null)
        {
            Console.WriteLine(graphJson);
            return 0;
        }

        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
        string baseName = Path.GetFileNameWithoutExtension(fullInputPath);
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

    private static bool TryParseArguments(
        IReadOnlyList<string> args,
        out string? inputPath,
        out string? outputDirectory)
    {
        inputPath = null;
        outputDirectory = null;
        if (args.Count < 2 || args[0] != "analyze")
        {
            return false;
        }

        inputPath = args[1];
        for (int index = 2; index < args.Count; index++)
        {
            if (args[index] == "--out" && index + 1 < args.Count)
            {
                outputDirectory = args[++index];
                continue;
            }

            return false;
        }

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

    private static void PrintUsage() => Console.Error.WriteLine(
        "Usage: unifiedscript analyze <input.cs> [--out <directory>]\n" +
        "Without --out, the graph JSON is written to stdout.");
}

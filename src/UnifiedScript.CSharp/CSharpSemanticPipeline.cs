using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.CSharp;

public sealed class CSharpSemanticPipeline
{
    private readonly CSharpToHirLowerer lowerer = new();
    private readonly CSharpCompilationHost compilationHost = new();

    public CSharpAnalysisResult Analyze(string source, string filePath = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(source);
        return Analyze(CSharpCompilationRequest.Single(source, filePath));
    }

    public CSharpAnalysisResult Analyze(CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SyntaxTree[] syntaxTrees = request.Sources
            .Select(static document => CSharpSyntaxTree.ParseText(
                document.Source,
                CSharpToHirLowerer.ParseOptions,
                document.LogicalPath))
            .ToArray();
        CSharpLoweringResult[] lowerings = syntaxTrees.Select(lowerer.Lower).ToArray();
        var diagnostics = lowerings.SelectMany(static lowering => lowering.Diagnostics).ToList();
        if (lowerings.Any(static lowering => !lowering.Succeeded || lowering.CompilationUnit is null))
        {
            return new CSharpAnalysisResult(null, null, diagnostics);
        }

        HirCompilationUnit hir = MergeCompilationUnits(request, lowerings);
        CSharpCompilation compilation = compilationHost.Create(request, syntaxTrees);
        diagnostics.AddRange(compilation
            .GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            .Select(static diagnostic => CSharpToHirLowerer.FromRoslynDiagnostic(
                diagnostic,
                diagnostic.Location.SourceTree?.FilePath ?? "<compilation>")));
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error))
        {
            return new CSharpAnalysisResult(hir, null, diagnostics);
        }

        SemanticBindingResult binding = new RoslynSemanticBinder(request.EffectCatalog).Bind(
            compilation,
            syntaxTrees,
            hir,
            request.ConfigurationId);
        diagnostics.AddRange(binding.Diagnostics);
        return new CSharpAnalysisResult(
            hir,
            diagnostics.Any(static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error)
                ? null
                : binding.Compilation,
            diagnostics);
    }

    private static HirCompilationUnit MergeCompilationUnits(
        CSharpCompilationRequest request,
        IReadOnlyList<CSharpLoweringResult> lowerings)
    {
        string sourceFingerprint = string.Join(
            '|',
            request.Sources.Select(static source =>
                $"{source.LogicalPath}:{GetSourceDigest(source.Source)}"));
        SemanticId compilationId = SemanticId.FromKey(
            $"csharp-compilation/v0.2|{request.AssemblyName}|{sourceFingerprint}");
        if (lowerings.Count == 1)
        {
            return lowerings[0].CompilationUnit! with { Id = compilationId };
        }

        HirTypeDeclaration[] types = lowerings
            .SelectMany(static lowering => lowering.CompilationUnit!.Types)
            .OrderBy(static type => type.QualifiedName, StringComparer.Ordinal)
            .ThenBy(static type => type.Span.FilePath, StringComparer.Ordinal)
            .ToArray();
        return new HirCompilationUnit(
            compilationId,
            SourceSpan.Unknown("<compilation>"),
            types);
    }

    private static string GetSourceDigest(string source)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}

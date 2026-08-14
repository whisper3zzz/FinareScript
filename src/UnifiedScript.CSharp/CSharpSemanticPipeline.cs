using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnifiedScript.Core;

namespace UnifiedScript.CSharp;

public sealed class CSharpSemanticPipeline
{
    private readonly CSharpToHirLowerer lowerer = new();
    private readonly CSharpCompilationHost compilationHost = new();

    public CSharpAnalysisResult Analyze(string source, string filePath = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(source);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            CSharpToHirLowerer.ParseOptions,
            filePath);
        CSharpLoweringResult lowering = lowerer.Lower(syntaxTree);
        var diagnostics = lowering.Diagnostics.ToList();
        if (!lowering.Succeeded || lowering.CompilationUnit is null)
        {
            return new CSharpAnalysisResult(null, null, diagnostics);
        }

        CSharpCompilation compilation = compilationHost.Create(syntaxTree);
        diagnostics.AddRange(compilation
            .GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            .Select(diagnostic => CSharpToHirLowerer.FromRoslynDiagnostic(diagnostic, filePath)));
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error))
        {
            return new CSharpAnalysisResult(lowering.CompilationUnit, null, diagnostics);
        }

        SemanticBindingResult binding = new RoslynSemanticBinder().Bind(
            compilation,
            syntaxTree,
            lowering.CompilationUnit);
        diagnostics.AddRange(binding.Diagnostics);
        return new CSharpAnalysisResult(
            lowering.CompilationUnit,
            diagnostics.Any(static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error)
                ? null
                : binding.Compilation,
            diagnostics);
    }
}

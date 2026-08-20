using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Graph;
using UnifiedScript.Semantics;

namespace UnifiedScript.CSharp;

public sealed record CSharpProgramImage(
    string AssemblyName,
    byte[] PortableExecutable,
    byte[] PortablePdb,
    CSharpProgramSourceMap SourceMap,
    HirCompilationUnit Hir,
    BoundCompilation Semantics,
    IReadOnlyList<IdentitySidecarDocument> IdentitySidecars);

public sealed record CSharpProgramEmitResult(
    CSharpProgramImage? Image,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics)
{
    public bool Succeeded =>
        Image is not null &&
        Diagnostics.All(static diagnostic =>
            diagnostic.Severity != UnifiedDiagnosticSeverity.Error);
}

public sealed class CSharpProgramEmitter
{
    private readonly CSharpSemanticPipeline semanticPipeline = new();
    private readonly CSharpCompilationHost compilationHost = new();

    public CSharpProgramEmitResult Emit(CSharpCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CSharpAnalysisResult analysis = semanticPipeline.Analyze(request);
        if (!analysis.Succeeded || analysis.Hir is null || analysis.Semantics is null)
        {
            return new CSharpProgramEmitResult(null, analysis.Diagnostics);
        }

        _ = new HirGraphProjector().Project(analysis.Hir, analysis.Semantics);

        using var portableExecutable = new MemoryStream();
        using var portablePdb = new MemoryStream();
        EmitResult emit = compilationHost.Create(request).Emit(
            portableExecutable,
            portablePdb,
            options: new EmitOptions(
                debugInformationFormat: DebugInformationFormat.PortablePdb,
                pdbFilePath: $"{request.AssemblyName}.pdb"));
        UnifiedDiagnostic[] diagnostics = analysis.Diagnostics
            .Concat(emit.Diagnostics
                .Where(static diagnostic =>
                    diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
                .Select(static diagnostic => CSharpToHirLowerer.FromRoslynDiagnostic(
                    diagnostic,
                    diagnostic.Location.SourceTree?.FilePath ?? "<compilation>")))
            .Distinct()
            .ToArray();
        if (!emit.Success || diagnostics.Any(static diagnostic =>
                diagnostic.Severity == UnifiedDiagnosticSeverity.Error))
        {
            return new CSharpProgramEmitResult(null, diagnostics);
        }

        byte[] portableExecutableBytes = portableExecutable.ToArray();
        byte[] portablePdbBytes = portablePdb.ToArray();
        CSharpProgramSourceMap sourceMap = new CSharpProgramSourceMapBuilder().Build(
            request.AssemblyName,
            portableExecutableBytes,
            portablePdbBytes,
            analysis.Hir,
            analysis.Semantics);
        return new CSharpProgramEmitResult(
            new CSharpProgramImage(
                request.AssemblyName,
                portableExecutableBytes,
                portablePdbBytes,
                sourceMap,
                analysis.Hir,
                analysis.Semantics,
                analysis.IdentitySidecars),
            diagnostics);
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Finare.Core;
using Finare.Core.Hir;
using Finare.Graph;
using Finare.Semantics;

namespace Finare.CSharp;

public sealed record CSharpProgramImage(
    string AssemblyName,
    byte[] PortableExecutable,
    byte[] PortablePdb,
    CSharpProgramSourceMap SourceMap,
    HirCompilationUnit Hir,
    BoundCompilation Semantics,
    ScriptAssetDocument ScriptAssets,
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

    public CSharpProgramEmitResult Emit(
        CSharpCompilationRequest request,
        CSharpDebugInstrumentation? debugInstrumentation = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        CSharpAnalysisResult analysis = semanticPipeline.Analyze(request);
        if (!analysis.Succeeded || analysis.Hir is null || analysis.Semantics is null ||
            analysis.ScriptAssets is null)
        {
            return new CSharpProgramEmitResult(null, analysis.Diagnostics);
        }

        _ = new HirGraphProjector().Project(
            analysis.Hir,
            analysis.Semantics,
            analysis.DeclarationReferences);

        using var sourceMapPortableExecutable = new MemoryStream();
        using var sourceMapPortablePdb = new MemoryStream();
        EmitResult sourceMapEmit = compilationHost.Create(request).Emit(
            sourceMapPortableExecutable,
            sourceMapPortablePdb,
            options: new EmitOptions(
                debugInformationFormat: DebugInformationFormat.PortablePdb,
                pdbFilePath: $"{request.AssemblyName}.pdb"));
        byte[] portableExecutableBytes = sourceMapPortableExecutable.ToArray();
        byte[] portablePdbBytes = sourceMapPortablePdb.ToArray();
        EmitResult? debugEmit = null;
        if (debugInstrumentation is not null && sourceMapEmit.Success)
        {
            IReadOnlyList<SyntaxTree> instrumentedTrees = new CSharpDebugInstrumenter()
                .Instrument(request, analysis.Hir, compilationHost);
            MetadataReference[] debugReferences =
                [MetadataReference.CreateFromFile(debugInstrumentation.ProbeAssemblyPath)];
            using var debugPortableExecutable = new MemoryStream();
            using var debugPortablePdb = new MemoryStream();
            debugEmit = compilationHost.Create(request, instrumentedTrees, debugReferences).Emit(
                debugPortableExecutable,
                debugPortablePdb,
                options: new EmitOptions(
                    debugInformationFormat: DebugInformationFormat.PortablePdb,
                    pdbFilePath: $"{request.AssemblyName}.pdb"));
            portableExecutableBytes = debugPortableExecutable.ToArray();
            portablePdbBytes = debugPortablePdb.ToArray();
        }

        UnifiedDiagnostic[] diagnostics = analysis.Diagnostics
            .Concat(sourceMapEmit.Diagnostics
                .Concat(debugEmit?.Diagnostics ?? [])
                .Where(static diagnostic =>
                    diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
                .Select(static diagnostic => CSharpToHirLowerer.FromRoslynDiagnostic(
                    diagnostic,
                    diagnostic.Location.SourceTree?.FilePath ?? "<compilation>")))
            .Distinct()
            .ToArray();
        if (!sourceMapEmit.Success || debugEmit is { Success: false } || diagnostics.Any(static diagnostic =>
                diagnostic.Severity == UnifiedDiagnosticSeverity.Error))
        {
            return new CSharpProgramEmitResult(null, diagnostics);
        }

        CSharpProgramSourceMap sourceMap = new CSharpProgramSourceMapBuilder().Build(
            request.AssemblyName,
            sourceMapPortableExecutable.ToArray(),
            sourceMapPortablePdb.ToArray(),
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
                analysis.ScriptAssets,
                analysis.IdentitySidecars),
            diagnostics);
    }
}

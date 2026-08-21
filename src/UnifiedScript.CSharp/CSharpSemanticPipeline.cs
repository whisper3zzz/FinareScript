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

        var identitySidecars = new List<IdentitySidecarDocument>();
        var declarationIdentities = new Dictionary<SemanticId, SemanticId>();
        var identityReconciler = new CSharpIdentityReconciler();
        for (int index = 0; index < request.Sources.Count; index++)
        {
            CSharpSourceDocument source = request.Sources[index];
            if (source.IdentitySidecar is null)
            {
                continue;
            }

            IdentityReconciliationResult reconciliation = identityReconciler.Reconcile(
                source,
                syntaxTrees[index],
                lowerings[index].CompilationUnit!);
            diagnostics.AddRange(reconciliation.Diagnostics);
            identitySidecars.Add(reconciliation.Sidecar);
            foreach (KeyValuePair<SemanticId, SemanticId> identity in reconciliation.DeclarationIdentities)
            {
                declarationIdentities.Add(identity.Key, identity.Value);
            }
            lowerings[index] = lowerings[index] with
            {
                CompilationUnit = reconciliation.CompilationUnit,
            };
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error))
        {
            return new CSharpAnalysisResult(null, null, diagnostics)
            {
                IdentitySidecars = GetOriginalIdentitySidecars(request),
            };
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
            return new CSharpAnalysisResult(hir, null, diagnostics)
            {
                IdentitySidecars = GetOriginalIdentitySidecars(request),
            };
        }

        SemanticBindingResult binding = new RoslynSemanticBinder(request.EffectCatalog).Bind(
            compilation,
            syntaxTrees,
            hir,
            request.ConfigurationId);
        diagnostics.AddRange(binding.Diagnostics);
        bool hasBindingErrors = diagnostics.Any(
            static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error);
        ScriptAssetLoweringResult assetLowering = hasBindingErrors || binding.Compilation is null
            ? new ScriptAssetLoweringResult(
                null,
                new Dictionary<SemanticId, SemanticId>(),
                [])
            : new CSharpScriptAssetLowerer().Lower(
                compilation,
                syntaxTrees,
                hir,
                binding.Compilation,
                declarationIdentities);
        diagnostics.AddRange(assetLowering.Diagnostics);
        bool hasErrors = diagnostics.Any(
            static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error);
        return new CSharpAnalysisResult(
            hir,
            hasErrors ? null : binding.Compilation,
            diagnostics)
        {
            IdentitySidecars = hasErrors
                ? GetOriginalIdentitySidecars(request)
                : identitySidecars,
            ScriptAssets = hasErrors ? null : assetLowering.Document,
            DeclarationReferences = hasErrors
                ? new Dictionary<SemanticId, SemanticId>()
                : assetLowering.DeclarationReferences,
        };
    }

    private static IReadOnlyList<IdentitySidecarDocument> GetOriginalIdentitySidecars(
        CSharpCompilationRequest request) => request.Sources
        .Select(static source => source.IdentitySidecar)
        .Where(static sidecar => sidecar is not null)
        .Cast<IdentitySidecarDocument>()
        .ToArray();

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

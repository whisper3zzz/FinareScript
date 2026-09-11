using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Finare.Core;
using Finare.Core.Hir;
using Finare.Semantics;

namespace Finare.CSharp;

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
            .Select(document => CSharpSyntaxTree.ParseText(
                document.Source,
                request.Settings.ParseOptions,
                document.LogicalPath))
            .ToArray();
        VisualProfileReport profile = new VisualProfileInspector().Inspect(syntaxTrees);
        CSharpLoweringResult[] lowerings = syntaxTrees.Select(lowerer.Lower).ToArray();
        var diagnostics = lowerings.SelectMany(static lowering => lowering.Diagnostics).ToList();
        CSharpMethodProjection[] methodProjections = lowerings
            .SelectMany(static lowering => lowering.MethodProjections)
            .ToArray();
        if (lowerings.Any(static lowering => !lowering.Succeeded || lowering.CompilationUnit is null))
        {
            return new CSharpAnalysisResult(null, null, diagnostics)
            {
                MethodProjections = methodProjections,
                Profile = profile,
            };
        }

        CSharpCompilation compilation = compilationHost.Create(request, syntaxTrees);
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
                lowerings[index].CompilationUnit!,
                compilation,
                lowerings[index].MethodProjections);
            diagnostics.AddRange(reconciliation.Diagnostics);
            identitySidecars.Add(reconciliation.Sidecar);
            foreach (KeyValuePair<SemanticId, SemanticId> identity in reconciliation.DeclarationIdentities)
            {
                declarationIdentities.Add(identity.Key, identity.Value);
            }
            lowerings[index] = lowerings[index] with
            {
                CompilationUnit = reconciliation.CompilationUnit,
                MethodProjections = reconciliation.MethodProjections,
            };
        }

        methodProjections = lowerings
            .SelectMany(static lowering => lowering.MethodProjections)
            .ToArray();
        profile = SynchronizeProfileMethodIds(profile, methodProjections);

        if (diagnostics.Any(static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error))
        {
            return new CSharpAnalysisResult(null, null, diagnostics)
            {
                MethodProjections = methodProjections,
                IdentitySidecars = GetOriginalIdentitySidecars(request),
                Profile = profile,
            };
        }

        HirCompilationUnit hir = MergeCompilationUnits(request, lowerings);
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
                MethodProjections = methodProjections,
                IdentitySidecars = GetOriginalIdentitySidecars(request),
                Profile = profile,
            };
        }

        HashSet<SemanticId> graphMethodIds = hir.Types
            .SelectMany(static type => type.Methods)
            .Select(static method => method.Id)
            .ToHashSet();
        SemanticBindingResult binding = new RoslynSemanticBinder(request.EffectCatalog).Bind(
            compilation,
            syntaxTrees,
            hir,
            request.ConfigurationId,
            graphMethodIds,
            methodProjections);
        var sourceOnlyMethodIds = methodProjections
            .Where(static projection => !projection.IsGraphEditable)
            .Select(static projection => projection.Id)
            .ToHashSet();
        // Removing a method from the graph can expose an opaque-call boundary
        // in a caller that was bindable in the previous pass. Rebind until
        // the graph reaches a fixed point, converting only graph capability
        // errors on implicit entries to source-only warnings. Roslyn and
        // declaration errors, and capability errors on explicit [Visual]
        // methods, remain in the final binding diagnostics as errors.
        for (int iteration = 0; ; iteration++)
        {
            Dictionary<SemanticId, IReadOnlyList<UnifiedDiagnostic>> fallbackDiagnostics =
                FindImplicitGraphCapabilityFailures(methodProjections, binding.Diagnostics);
            if (fallbackDiagnostics.Count == 0)
            {
                break;
            }

            if (iteration >= methodProjections.Length)
            {
                throw new InvalidOperationException(
                    "Source-only graph fallback did not converge after removing every exported method.");
            }

            HashSet<SemanticId> opaqueMethodIds = fallbackDiagnostics.Keys.ToHashSet();
            foreach (SemanticId opaqueMethodId in opaqueMethodIds)
            {
                if (!sourceOnlyMethodIds.Add(opaqueMethodId))
                {
                    throw new InvalidOperationException(
                        $"Source-only graph fallback attempted to remove method '{opaqueMethodId}' twice.");
                }
            }

            foreach (IReadOnlyList<UnifiedDiagnostic> failures in fallbackDiagnostics.Values)
            {
                diagnostics.AddRange(failures.Select(VisualProfileValidator.AsSourceOnlyCapabilityWarning));
            }

            hir = RemoveGraphMethods(hir, opaqueMethodIds);
            methodProjections = methodProjections
                .Select(projection => opaqueMethodIds.Contains(projection.Id)
                    ? projection with
                    {
                        BodyRepresentation = MethodBodyRepresentation.OpaqueCode,
                        CapabilityDiagnostics = projection.CapabilityDiagnostics
                            .Concat(fallbackDiagnostics[projection.Id]
                                .Select(VisualProfileValidator.AsSourceOnlyCapabilityWarning))
                            .ToArray(),
                    }
                    : projection)
                .ToArray();
            profile = ReclassifyProfile(profile, methodProjections, opaqueMethodIds);
            graphMethodIds = hir.Types
                .SelectMany(static type => type.Methods)
                .Select(static method => method.Id)
                .ToHashSet();
            binding = new RoslynSemanticBinder(request.EffectCatalog).Bind(
                compilation,
                syntaxTrees,
                hir,
                request.ConfigurationId,
                graphMethodIds,
                methodProjections);
        }
        diagnostics.AddRange(binding.Diagnostics);
        bool hasBindingErrors = diagnostics.Any(
            static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error);
        ScriptAssetLoweringResult assetLowering = hasBindingErrors || binding.Compilation is null
            ? new ScriptAssetLoweringResult(
                null,
                new Dictionary<SemanticId, SemanticId>(),
                new Dictionary<SemanticId, SemanticId>(),
                [])
            : new CSharpScriptAssetLowerer().Lower(
                compilation,
                syntaxTrees,
                hir,
                binding.Compilation,
                declarationIdentities,
                methodProjections);
        diagnostics.AddRange(assetLowering.Diagnostics);
        bool hasErrors = diagnostics.Any(
            static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error);
        return new CSharpAnalysisResult(
            hir,
            hasErrors ? null : binding.Compilation,
            diagnostics)
        {
            MethodProjections = methodProjections,
            IdentitySidecars = hasErrors
                ? GetOriginalIdentitySidecars(request)
                : identitySidecars,
            ScriptAssets = hasErrors ? null : assetLowering.Document,
            DeclarationReferences = hasErrors
                ? new Dictionary<SemanticId, SemanticId>()
                : assetLowering.DeclarationReferences,
            DeclarationOwners = hasErrors
                ? new Dictionary<SemanticId, SemanticId>()
                : assetLowering.DeclarationOwners,
            Profile = profile,
        };
    }

    private static Dictionary<SemanticId, IReadOnlyList<UnifiedDiagnostic>>
        FindImplicitGraphCapabilityFailures(
            IReadOnlyList<CSharpMethodProjection> methodProjections,
            IReadOnlyList<UnifiedDiagnostic> diagnostics)
    {
        var failures = new Dictionary<SemanticId, List<UnifiedDiagnostic>>();
        foreach (UnifiedDiagnostic diagnostic in diagnostics.Where(IsGraphCapabilityDiagnostic))
        {
            CSharpMethodProjection[] matches = methodProjections
                .Where(static projection => projection.IsImplicitEntry)
                .Where(static projection => projection.IsGraphEditable)
                .Where(projection => Contains(projection.Span, diagnostic.Span))
                .ToArray();
            if (matches.Length == 1)
            {
                failures.TryAdd(matches[0].Id, []);
                failures[matches[0].Id].Add(diagnostic);
            }
        }

        return failures.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<UnifiedDiagnostic>)pair.Value);
    }

    private static bool IsGraphCapabilityDiagnostic(UnifiedDiagnostic diagnostic) =>
        diagnostic.Severity == UnifiedDiagnosticSeverity.Error && diagnostic.Code is
            SemanticDiagnosticCodes.GraphUnsafeSignature or
            SemanticDiagnosticCodes.ConditionalBranchEffectsNotSupported or
            SemanticDiagnosticCodes.ShortCircuitRightEffectsNotSupported or
            SemanticDiagnosticCodes.CoalesceRightEffectsNotSupported or
            SemanticDiagnosticCodes.CoalesceConversionNotSupported or
            SemanticDiagnosticCodes.ConditionalAccessEffectsNotSupported or
            SemanticDiagnosticCodes.ConditionalAccessTypeShapeNotSupported or
            SemanticDiagnosticCodes.ForEachCollectionNotSupported or
            SemanticDiagnosticCodes.OpaqueCodeBoundaryNotSupported;

    private static bool Contains(SourceSpan outer, SourceSpan inner)
    {
        if (!string.Equals(outer.FilePath, inner.FilePath, StringComparison.Ordinal))
        {
            return false;
        }

        return Compare(outer.Start, inner.Start) <= 0 &&
            Compare(inner.End, outer.End) <= 0;
    }

    private static int Compare(SourcePosition left, SourcePosition right) =>
        left.Line != right.Line
            ? left.Line.CompareTo(right.Line)
            : left.Column.CompareTo(right.Column);

    private static HirCompilationUnit RemoveGraphMethods(
        HirCompilationUnit hir,
        IReadOnlySet<SemanticId> opaqueMethodIds) => hir with
        {
            Types = hir.Types
                .Select(type => type with
                {
                    Methods = type.Methods
                        .Where(method => !opaqueMethodIds.Contains(method.Id))
                        .ToArray(),
                })
                .Where(static type => type.Methods.Count != 0)
                .ToArray(),
        };

    private static VisualProfileReport ReclassifyProfile(
        VisualProfileReport profile,
        IReadOnlyList<CSharpMethodProjection> methodProjections,
        IReadOnlySet<SemanticId> opaqueMethodIds) => profile with
        {
            Methods = profile.Methods
                .Select(method =>
                {
                    CSharpMethodProjection? projection = methodProjections.SingleOrDefault(
                        candidate => candidate.QualifiedName == method.QualifiedName);
                    if (projection is null || !opaqueMethodIds.Contains(projection.Id))
                    {
                        return method;
                    }

                    return method with
                    {
                        Representation = VisualMethodRepresentation.OpaqueCode,
                        GraphEditable = false,
                        AuthoringBoundary = "sourceOnly",
                        CapabilityDiagnostics = projection.CapabilityDiagnostics,
                    };
                })
                .ToArray(),
        };

    private static VisualProfileReport SynchronizeProfileMethodIds(
        VisualProfileReport profile,
        IReadOnlyList<CSharpMethodProjection> methodProjections)
    {
        Dictionary<string, SemanticId> projectionIds = methodProjections
            .GroupBy(static projection => projection.QualifiedName, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Id,
                StringComparer.Ordinal);
        return profile with
        {
            Methods = profile.Methods
                .Select(method => projectionIds.TryGetValue(method.QualifiedName, out SemanticId id)
                    ? method with { Id = id }
                    : method)
                .ToArray(),
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

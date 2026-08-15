using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.CSharp;

public sealed class CSharpSourcePatchApplier
{
    private readonly CSharpSemanticPipeline pipeline = new();

    public CSharpPatchResult Apply(
        CSharpCompilationRequest request,
        SemanticPatchDocument patch)
    {
        ArgumentNullException.ThrowIfNull(request);
        SemanticPatchJson.Validate(patch);

        CSharpAnalysisResult original = pipeline.Analyze(request);
        if (!original.Succeeded || original.Hir is null)
        {
            return Failure(original.Diagnostics);
        }

        if (patch.SourceCompilationId != original.Hir.Id)
        {
            return Failure(new UnifiedDiagnostic(
                SemanticPatchDiagnosticCodes.BaselineMismatch,
                UnifiedDiagnosticSeverity.Error,
                $"Patch baseline '{patch.SourceCompilationId}' does not match current source " +
                $"compilation '{original.Hir.Id}'. Re-analyze the source before applying the patch.",
                SourceSpan.Unknown("<patch>")));
        }

        if (original.Semantics is null ||
            patch.SemanticConfigurationId != original.Semantics.ConfigurationId)
        {
            return Failure(new UnifiedDiagnostic(
                SemanticPatchDiagnosticCodes.ConfigurationMismatch,
                UnifiedDiagnosticSeverity.Error,
                $"Patch semantic configuration '{patch.SemanticConfigurationId}' does not match current " +
                $"configuration '{original.Semantics?.ConfigurationId}'. Re-analyze the graph before " +
                "applying the patch.",
                SourceSpan.Unknown("<patch>")));
        }

        IReadOnlyDictionary<SemanticId, HirNode> hirNodes = original.Hir
            .DescendantsAndSelf()
            .ToDictionary(static node => node.Id);
        var documents = request.Sources.ToDictionary(
            static source => source.LogicalPath,
            static source => new ParsedDocument(
                source,
                CSharpSyntaxTree.ParseText(
                    source.Source,
                    CSharpToHirLowerer.ParseOptions,
                    source.LogicalPath)));
        var replacements = new Dictionary<string, Dictionary<TextSpan, PendingReplacement>>(
            StringComparer.Ordinal);

        foreach (ReplaceLiteralPatchOperation operation in patch.Operations
                     .Cast<ReplaceLiteralPatchOperation>()
                     .OrderBy(static operation => operation.TargetNodeId.Value, StringComparer.Ordinal))
        {
            if (!hirNodes.TryGetValue(operation.TargetNodeId, out HirNode? target))
            {
                return Failure(new UnifiedDiagnostic(
                    SemanticPatchDiagnosticCodes.TargetNotFound,
                    UnifiedDiagnosticSeverity.Error,
                    $"Patch target '{operation.TargetNodeId}' was not found in the current HIR.",
                    SourceSpan.Unknown("<patch>"),
                    operation.TargetNodeId));
            }

            if (target is not HirLiteralExpression literal)
            {
                return Failure(new UnifiedDiagnostic(
                    SemanticPatchDiagnosticCodes.TargetKindMismatch,
                    UnifiedDiagnosticSeverity.Error,
                    $"Patch target '{operation.TargetNodeId}' is '{target.GetType().Name}', not a literal.",
                    target.Span,
                    operation.TargetNodeId));
            }

            if (!string.Equals(literal.ValueText, operation.ExpectedValueText, StringComparison.Ordinal))
            {
                return Failure(new UnifiedDiagnostic(
                    SemanticPatchDiagnosticCodes.PreconditionFailed,
                    UnifiedDiagnosticSeverity.Error,
                    $"Literal precondition failed: expected '{operation.ExpectedValueText}', " +
                    $"but the current value is '{literal.ValueText}'.",
                    literal.Span,
                    literal.Id));
            }

            if (!documents.TryGetValue(literal.Span.FilePath, out ParsedDocument? document))
            {
                return Failure(new UnifiedDiagnostic(
                    SemanticPatchDiagnosticCodes.TargetNotFound,
                    UnifiedDiagnosticSeverity.Error,
                    $"Source document '{literal.Span.FilePath}' for target '{literal.Id}' was not found.",
                    literal.Span,
                    literal.Id));
            }

            LiteralExpressionSyntax? syntax = document.Root
                .DescendantNodes()
                .OfType<LiteralExpressionSyntax>()
                .SingleOrDefault(candidate => RoslynSourceSpan.FromNode(
                    candidate,
                    document.Source.LogicalPath) == literal.Span);
            if (syntax is null)
            {
                return Failure(new UnifiedDiagnostic(
                    SemanticPatchDiagnosticCodes.TargetNotFound,
                    UnifiedDiagnosticSeverity.Error,
                    $"Roslyn syntax for literal target '{literal.Id}' was not found at its HIR source span.",
                    literal.Span,
                    literal.Id));
            }

            if (!TryParseLiteral(operation.NewValueText, out LiteralExpressionSyntax? replacement))
            {
                return Failure(new UnifiedDiagnostic(
                    SemanticPatchDiagnosticCodes.InvalidReplacement,
                    UnifiedDiagnosticSeverity.Error,
                    $"Replacement '{operation.NewValueText}' is not one complete C# literal expression.",
                    literal.Span,
                    literal.Id));
            }

            if (!replacements.TryGetValue(document.Source.LogicalPath, out var documentReplacements))
            {
                documentReplacements = [];
                replacements.Add(document.Source.LogicalPath, documentReplacements);
            }

            var annotation = new SyntaxAnnotation("UnifiedScript.IdentityPin", literal.Id.Value);
            documentReplacements.Add(
                syntax.Span,
                new PendingReplacement(
                    literal.Id,
                    replacement!
                        .WithTriviaFrom(syntax)
                        .WithAdditionalAnnotations(annotation),
                    annotation));
        }

        IReadOnlyDictionary<string, IdentitySidecarDocument> effectiveSidecars = original.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        CSharpSourceDocument[] patchedSources = request.Sources
            .Select(source => ReplaceDocument(
                source,
                documents[source.LogicalPath],
                replacements,
                effectiveSidecars.GetValueOrDefault(source.LogicalPath)))
            .ToArray();
        var patchedRequest = new CSharpCompilationRequest(
            request.AssemblyName,
            patchedSources,
            request.ReferencePaths,
            request.EffectCatalog);
        CSharpAnalysisResult patchedAnalysis = pipeline.Analyze(patchedRequest);
        if (!patchedAnalysis.Succeeded)
        {
            return Failure(patchedAnalysis.Diagnostics);
        }

        return new CSharpPatchResult(patchedRequest, patchedAnalysis, patchedAnalysis.Diagnostics);
    }

    private static CSharpSourceDocument ReplaceDocument(
        CSharpSourceDocument source,
        ParsedDocument document,
        IReadOnlyDictionary<string, Dictionary<TextSpan, PendingReplacement>> replacements,
        IdentitySidecarDocument? effectiveSidecar)
    {
        if (!replacements.TryGetValue(source.LogicalPath, out var documentReplacements))
        {
            return effectiveSidecar is null
                ? source
                : new CSharpSourceDocument(source.LogicalPath, source.Source, effectiveSidecar);
        }

        SyntaxNode[] targets = document.Root
            .DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(literal => documentReplacements.ContainsKey(literal.Span))
            .Cast<SyntaxNode>()
            .ToArray();
        SyntaxNode changedRoot = document.Root.ReplaceNodes(
            targets,
            (original, _) => documentReplacements[original.Span].Syntax);
        CSharpIdentityPin[] pins = effectiveSidecar is null
            ? []
            : documentReplacements.Values
                .Select(replacement => new CSharpIdentityPin(
                    replacement.TargetId,
                    "literal",
                    RoslynSourceSpan.FromNode(
                        changedRoot.GetAnnotatedNodes(replacement.Annotation).Single(),
                        source.LogicalPath)))
                .ToArray();
        return new CSharpSourceDocument(
            source.LogicalPath,
            changedRoot.ToFullString(),
            effectiveSidecar,
            pins);
    }

    private static bool TryParseLiteral(
        string text,
        out LiteralExpressionSyntax? literal)
    {
        ExpressionSyntax expression = SyntaxFactory.ParseExpression(
            text,
            options: CSharpToHirLowerer.ParseOptions,
            consumeFullText: true);
        literal = expression as LiteralExpressionSyntax;
        return literal is not null &&
            !literal.ContainsDiagnostics &&
            literal.SpanStart == 0 &&
            literal.Span.Length == text.Length;
    }

    private static CSharpPatchResult Failure(params UnifiedDiagnostic[] diagnostics) =>
        new(null, null, diagnostics);

    private static CSharpPatchResult Failure(IReadOnlyList<UnifiedDiagnostic> diagnostics) =>
        new(null, null, diagnostics);

    private sealed record ParsedDocument(CSharpSourceDocument Source, SyntaxTree SyntaxTree)
    {
        public SyntaxNode Root { get; } = SyntaxTree.GetRoot();
    }

    private sealed record PendingReplacement(
        SemanticId TargetId,
        LiteralExpressionSyntax Syntax,
        SyntaxAnnotation Annotation);
}

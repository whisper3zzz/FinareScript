using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Finare.Core;

namespace Finare.CSharp;

/// <summary>
/// Describes a source-file identity transition without performing any file IO.
/// </summary>
public sealed record CSharpSourceIdentityTransition(
    string OriginalLogicalPath,
    string TargetLogicalPath,
    string TargetSource,
    bool PreserveIdentity)
{
    public string NormalizedOriginalLogicalPath => NormalizePath(OriginalLogicalPath);

    public string NormalizedTargetLogicalPath => NormalizePath(TargetLogicalPath);

    private static string NormalizePath(string path) =>
        (path ?? string.Empty).Replace('\\', '/');
}

public sealed record CSharpSourceIdentityTransitionOutcome(
    CSharpSourceIdentityTransition Transition,
    CSharpSourceDocument? Source,
    IdentitySidecarDocument? IdentitySidecar);

public sealed record CSharpSourceIdentityTransitionPlanResult(
    CSharpCompilationRequest? Request,
    CSharpAnalysisResult? Analysis,
    IReadOnlyList<CSharpSourceIdentityTransitionOutcome> Transitions,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics)
{
    public bool Succeeded =>
        Request is not null &&
        Analysis?.Succeeded == true &&
        Diagnostics.All(static diagnostic => diagnostic.Severity != UnifiedDiagnosticSeverity.Error);

    public CSharpCompilationRequest? PlannedRequest => Request;

    public IReadOnlyList<IdentitySidecarDocument> IdentitySidecars =>
        Transitions
            .Select(static transition => transition.IdentitySidecar)
            .Where(static sidecar => sidecar is not null)
            .Cast<IdentitySidecarDocument>()
            .ToArray();
}

/// <summary>
/// Plans source renames and duplicates while preserving identities through the
/// official semantic identity pipeline. The planner never writes source or
/// sidecar files.
/// </summary>
public sealed class CSharpSourceIdentityTransitionPlanner
{
    public const string InvalidTransitionCode = "UVS3110";
    public const string TopologyMismatchCode = "UVS3111";
    public const string TransitionPipelineCode = "UVS3112";

    private readonly CSharpSemanticPipeline pipeline;

    public CSharpSourceIdentityTransitionPlanner(CSharpSemanticPipeline? pipeline = null)
    {
        this.pipeline = pipeline ?? new CSharpSemanticPipeline();
    }

    public CSharpSourceIdentityTransitionPlanResult Plan(
        CSharpCompilationRequest request,
        IReadOnlyList<CSharpSourceIdentityTransition> transitions)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transitions);

        var diagnostics = new List<UnifiedDiagnostic>();
        if (!ValidateTransitions(request, transitions, diagnostics))
        {
            return Failure(transitions, diagnostics);
        }

        CSharpAnalysisResult current;
        try
        {
            // Reconcile first. This makes an old-but-valid sidecar the source
            // of current live IDs before any source-path transition is pinned.
            current = pipeline.Analyze(request);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(Error(
                TransitionPipelineCode,
                exception.Message,
                SourceSpan.Unknown("<identity-transition>")));
            return Failure(transitions, diagnostics);
        }

        diagnostics.AddRange(current.Diagnostics);
        if (!current.Succeeded)
        {
            return Failure(transitions, diagnostics);
        }

        var reconciledSidecars = current.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        var transitionInputs = new Dictionary<string, TransitionInput>(StringComparer.Ordinal);
        foreach (CSharpSourceIdentityTransition transition in transitions)
        {
            CSharpSourceDocument original = request.Sources.Single(source =>
                string.Equals(source.LogicalPath, transition.NormalizedOriginalLogicalPath,
                    StringComparison.Ordinal));
            IdentitySidecarDocument? sidecar = reconciledSidecars.GetValueOrDefault(original.LogicalPath);
            if (!transition.PreserveIdentity || sidecar is null)
            {
                transitionInputs.Add(
                    transition.NormalizedOriginalLogicalPath,
                    new TransitionInput(transition, original, sidecar, []));
                continue;
            }

            if (!TryCreatePins(
                    original,
                    transition,
                    sidecar,
                    request.Settings.ParseOptions,
                    out CSharpIdentityPin[] pins,
                    out UnifiedDiagnostic? topologyDiagnostic))
            {
                diagnostics.Add(topologyDiagnostic!);
                return Failure(transitions, diagnostics);
            }

            transitionInputs.Add(
                transition.NormalizedOriginalLogicalPath,
                new TransitionInput(transition, original, sidecar, pins));
        }

        CSharpCompilationRequest candidateRequest;
        try
        {
            candidateRequest = CreateCandidateRequest(
                request,
                transitionInputs,
                reconciledSidecars);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(Error(
                InvalidTransitionCode,
                exception.Message,
                SourceSpan.Unknown("<identity-transition>")));
            return Failure(transitions, diagnostics);
        }

        CSharpAnalysisResult planned;
        try
        {
            planned = pipeline.Analyze(candidateRequest);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(Error(
                TransitionPipelineCode,
                exception.Message,
                SourceSpan.Unknown("<identity-transition>")));
            return Failure(transitions, diagnostics);
        }

        diagnostics.AddRange(planned.Diagnostics);
        if (!planned.Succeeded)
        {
            return Failure(transitions, diagnostics);
        }

        var finalSidecars = planned.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        var outcomes = transitions.Select(transition =>
        {
            string targetPath = transition.NormalizedTargetLogicalPath;
            CSharpSourceDocument source = candidateRequest.Sources.Single(item =>
                string.Equals(item.LogicalPath, targetPath, StringComparison.Ordinal));
            return new CSharpSourceIdentityTransitionOutcome(
                transition,
                source,
                finalSidecars.GetValueOrDefault(targetPath));
        }).ToArray();

        return new CSharpSourceIdentityTransitionPlanResult(
            candidateRequest,
            planned,
            outcomes,
            diagnostics);
    }

    private static CSharpCompilationRequest CreateCandidateRequest(
        CSharpCompilationRequest request,
        IReadOnlyDictionary<string, TransitionInput> transitionInputs,
        IReadOnlyDictionary<string, IdentitySidecarDocument> reconciledSidecars)
    {
        List<CSharpSourceDocument> candidateSources = [];
        foreach (CSharpSourceDocument source in request.Sources)
        {
            if (transitionInputs.TryGetValue(source.LogicalPath, out TransitionInput? input))
            {
                if (!input.Transition.PreserveIdentity)
                {
                    IdentitySidecarDocument? reconciled = reconciledSidecars.GetValueOrDefault(source.LogicalPath);
                    candidateSources.Add(new CSharpSourceDocument(
                        source.LogicalPath,
                        source.Source,
                        reconciled));
                }
            }
            else
            {
                IdentitySidecarDocument? reconciled = reconciledSidecars.GetValueOrDefault(source.LogicalPath);
                candidateSources.Add(new CSharpSourceDocument(
                    source.LogicalPath,
                    source.Source,
                    reconciled));
            }
        }

        foreach (TransitionInput input in transitionInputs.Values)
        {
            string targetPath = input.Transition.NormalizedTargetLogicalPath;
            IdentitySidecarDocument? targetSidecar = input.Transition.PreserveIdentity &&
                input.IdentitySidecar is not null
                ? input.IdentitySidecar with { SourcePath = targetPath }
                : input.IdentitySidecar is not null && !input.Transition.PreserveIdentity
                    ? IdentitySidecarDocument.CreateEmpty(targetPath)
                    : null;
            candidateSources.Add(new CSharpSourceDocument(
                targetPath,
                input.Transition.TargetSource,
                targetSidecar,
                input.Transition.PreserveIdentity ? input.Pins : []));
        }

        return new CSharpCompilationRequest(
            request.AssemblyName,
            candidateSources,
            request.ReferencePaths,
            request.EffectCatalog,
            request.Settings,
            request.RuntimeReferencePaths,
            request.IncludeDefaultReferences);
    }

    private static bool ValidateTransitions(
        CSharpCompilationRequest request,
        IReadOnlyList<CSharpSourceIdentityTransition> transitions,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        var originals = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var requestPaths = request.Sources.Select(static source => source.LogicalPath)
            .ToHashSet(StringComparer.Ordinal);
        foreach (CSharpSourceIdentityTransition transition in transitions)
        {
            string original = transition.NormalizedOriginalLogicalPath;
            string target = transition.NormalizedTargetLogicalPath;
            if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(target) ||
                string.IsNullOrWhiteSpace(transition.TargetSource))
            {
                diagnostics.Add(Error(
                    InvalidTransitionCode,
                    "Source identity transitions require original path, target path, and target source.",
                    SourceSpan.Unknown(target)));
                continue;
            }

            if (!requestPaths.Contains(original) || !originals.Add(original) || !targets.Add(target) ||
                (!transition.PreserveIdentity && string.Equals(original, target, StringComparison.Ordinal)))
            {
                diagnostics.Add(Error(
                    InvalidTransitionCode,
                    $"Invalid or colliding source identity transition '{original}' -> '{target}'.",
                    SourceSpan.Unknown(target)));
            }
        }

        var transitionOriginals = transitions
            .Select(static transition => transition.NormalizedOriginalLogicalPath)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string target in targets)
        {
            if (requestPaths.Contains(target) && !transitionOriginals.Contains(target))
            {
                diagnostics.Add(Error(
                    InvalidTransitionCode,
                    $"Transition target '{target}' collides with an existing source.",
                    SourceSpan.Unknown(target)));
            }
        }

        return !diagnostics.Any(static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error);
    }

    private static bool TryCreatePins(
        CSharpSourceDocument original,
        CSharpSourceIdentityTransition transition,
        IdentitySidecarDocument sidecar,
        CSharpParseOptions parseOptions,
        out CSharpIdentityPin[] pins,
        out UnifiedDiagnostic? diagnostic)
    {
        pins = [];
        diagnostic = null;
        SyntaxTree oldTree = CSharpSyntaxTree.ParseText(
            original.Source,
            parseOptions,
            path: original.LogicalPath);
        SyntaxTree targetTree = CSharpSyntaxTree.ParseText(
            transition.TargetSource,
            parseOptions,
            path: transition.NormalizedTargetLogicalPath);
        SyntaxNode oldRoot = oldTree.GetRoot();
        SyntaxNode targetRoot = targetTree.GetRoot();
        if (!TryMapTopology(oldRoot, targetRoot, out Dictionary<SyntaxNode, SyntaxNode> mapping))
        {
            diagnostic = Error(
                TopologyMismatchCode,
                $"Source transition '{original.LogicalPath}' -> '{transition.NormalizedTargetLogicalPath}' " +
                "changed syntax topology; stable identities were not guessed.",
                SourceSpan.Unknown(transition.NormalizedTargetLogicalPath));
            return false;
        }

        var mapped = new List<CSharpIdentityPin>(sidecar.Entries.Count);
        foreach (IdentityEntry entry in sidecar.Entries)
        {
            SyntaxNode[] matches = oldRoot.DescendantNodesAndSelf()
                .Where(node => node.SpanStart == entry.Anchor.Start &&
                    node.Span.Length == entry.Anchor.Length)
                .ToArray();
            SyntaxNode[] mappedMatches = matches
                .Where(mapping.ContainsKey)
                .Select(node => mapping[node])
                .Cast<SyntaxNode>()
                .ToArray();
            SourceSpan[] targetSpans = mappedMatches
                .Select(node => RoslynSourceSpan.FromNode(
                    node,
                    transition.NormalizedTargetLogicalPath))
                .Distinct()
                .ToArray();
            if (matches.Length == 0 || targetSpans.Length != 1)
            {
                diagnostic = Error(
                    TopologyMismatchCode,
                    $"Identity '{entry.Id}' could not be mapped uniquely from its recorded syntax anchor.",
                    SourceSpan.Unknown(transition.NormalizedTargetLogicalPath),
                    entry.Id);
                return false;
            }

            mapped.Add(new CSharpIdentityPin(entry.Id, entry.Kind, targetSpans[0]));
        }

        var duplicateSpans = mapped
            .GroupBy(static pin => (pin.Id, pin.CurrentSpan.FilePath,
                pin.CurrentSpan.Start.Line, pin.CurrentSpan.Start.Column,
                pin.CurrentSpan.End.Line, pin.CurrentSpan.End.Column))
            .Where(static group => group.Count() > 1)
            .ToArray();
        if (duplicateSpans.Length > 0)
        {
            diagnostic = Error(
                TopologyMismatchCode,
                "Multiple identity pins resolved to an ambiguous target span.",
                SourceSpan.Unknown(transition.NormalizedTargetLogicalPath));
            return false;
        }

        pins = mapped.ToArray();
        return true;
    }

    private static bool TryMapTopology(
        SyntaxNode oldNode,
        SyntaxNode targetNode,
        out Dictionary<SyntaxNode, SyntaxNode> mapping)
    {
        Dictionary<SyntaxNode, SyntaxNode> result =
            new(ReferenceEqualityComparer.Instance);
        bool succeeded = Visit(oldNode, targetNode);
        mapping = result;
        return succeeded;

        bool Visit(SyntaxNode oldCurrent, SyntaxNode targetCurrent)
        {
            if (oldCurrent.RawKind != targetCurrent.RawKind)
            {
                return false;
            }

            SyntaxNodeOrToken[] oldChildren = oldCurrent.ChildNodesAndTokens().ToArray();
            SyntaxNodeOrToken[] targetChildren = targetCurrent.ChildNodesAndTokens().ToArray();
            if (oldChildren.Length != targetChildren.Length)
            {
                return false;
            }

            result.Add(oldCurrent, targetCurrent);
            for (int index = 0; index < oldChildren.Length; index++)
            {
                SyntaxNodeOrToken oldChild = oldChildren[index];
                SyntaxNodeOrToken targetChild = targetChildren[index];
                if (oldChild.IsToken != targetChild.IsToken || oldChild.RawKind != targetChild.RawKind)
                {
                    result.Remove(oldCurrent);
                    return false;
                }

                if (oldChild.IsNode && !Visit(oldChild.AsNode()!, targetChild.AsNode()!))
                {
                    result.Remove(oldCurrent);
                    return false;
                }
            }

            return true;
        }
    }

    private static CSharpSourceIdentityTransitionPlanResult Failure(
        IReadOnlyList<CSharpSourceIdentityTransition> transitions,
        IReadOnlyList<UnifiedDiagnostic> diagnostics) =>
        new(null, null, transitions.Select(transition =>
            new CSharpSourceIdentityTransitionOutcome(
                transition,
                new CSharpSourceDocument(
                    string.IsNullOrWhiteSpace(transition.NormalizedTargetLogicalPath)
                        ? "<identity-transition>"
                        : transition.NormalizedTargetLogicalPath,
                    transition.TargetSource ?? string.Empty),
                null)).ToArray(), diagnostics);

    private static UnifiedDiagnostic Error(
        string code,
        string message,
        SourceSpan span,
        SemanticId? id = null) =>
        new(code, UnifiedDiagnosticSeverity.Error, message, span, id);

    private sealed record TransitionInput(
        CSharpSourceIdentityTransition Transition,
        CSharpSourceDocument Original,
        IdentitySidecarDocument? IdentitySidecar,
        IReadOnlyList<CSharpIdentityPin> Pins);
}

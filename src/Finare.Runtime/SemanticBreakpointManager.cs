using Finare.Core;
using Finare.CSharp;

namespace Finare.Runtime;

public sealed class SemanticBreakpointManager
{
    private readonly object gate = new();
    private readonly Dictionary<SemanticId, SemanticBreakpoint> breakpoints = [];
    private CoreClrProgramGeneration? generation;
    private CSharpProgramSourceMap? sourceMap;
    private BreakpointBindingSnapshot? snapshot;

    public IReadOnlyList<SemanticBreakpoint> Breakpoints
    {
        get
        {
            lock (gate)
            {
                return breakpoints.Values
                    .OrderBy(static breakpoint => breakpoint.BreakpointId.Value, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public BreakpointBindingSnapshot? CurrentSnapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public BreakpointBindingSnapshot? Set(SemanticBreakpoint breakpoint)
    {
        ValidateBreakpoint(breakpoint);
        lock (gate)
        {
            breakpoints[breakpoint.BreakpointId] = breakpoint;
            RebindCore();
            return snapshot;
        }
    }

    public bool Remove(SemanticId breakpointId)
    {
        if (breakpointId.IsEmpty)
        {
            throw new ArgumentException("A breakpoint ID cannot be empty.", nameof(breakpointId));
        }

        lock (gate)
        {
            bool removed = breakpoints.Remove(breakpointId);
            if (removed)
            {
                RebindCore();
            }

            return removed;
        }
    }

    public void Unbind()
    {
        lock (gate)
        {
            generation = null;
            sourceMap = null;
            snapshot = null;
        }
    }

    public BreakpointBindingSnapshot Bind(
        CoreClrProgramGeneration programGeneration,
        CSharpProgramSourceMap programSourceMap)
    {
        ArgumentNullException.ThrowIfNull(programGeneration);
        ArgumentNullException.ThrowIfNull(programSourceMap);
        if (programGeneration.SourceCompilationId != programSourceMap.SourceCompilationId ||
            programGeneration.SemanticConfigurationId != programSourceMap.SemanticConfigurationId)
        {
            throw new ArgumentException(
                "The program generation and source map do not describe the same compilation.",
                nameof(programSourceMap));
        }

        lock (gate)
        {
            generation = programGeneration;
            sourceMap = CSharpProgramSourceMapJson.NormalizeAndValidate(programSourceMap);
            RebindCore();
            return snapshot!;
        }
    }

    public BreakpointHitResolution ResolveHit(CoreClrBreakpointHit hit)
    {
        hit = BreakpointProtocolJson.NormalizeAndValidate(hit);
        lock (gate)
        {
            if (generation is null || snapshot is null)
            {
                throw new BreakpointProtocolException(
                    "UDB1001",
                    "No CoreCLR program generation is bound for breakpoint hit resolution.");
            }

            if (hit.DomainId != generation.DomainId ||
                hit.GenerationId != generation.GenerationId ||
                hit.ImageKey != generation.ImageKey)
            {
                throw new BreakpointProtocolException(
                    "UDB1002",
                    "The breakpoint hit belongs to a stale or different CoreCLR program generation.");
            }

            SemanticBreakpointBinding[] matchingBindings = snapshot.Bindings
                .Where(static binding => binding.State == BreakpointBindingState.Bound)
                .Where(binding => binding.MetadataToken == hit.MetadataToken &&
                    binding.Locations.Any(location => location.IlOffset == hit.IlOffset))
                .ToArray();
            if (matchingBindings.Length == 0)
            {
                throw new BreakpointProtocolException(
                    "UDB1003",
                    $"No enabled semantic breakpoint is bound to method token " +
                    $"0x{hit.MetadataToken:x8} at IL_{hit.IlOffset:x4}.");
            }

            SemanticId methodId = matchingBindings
                .Select(static binding => binding.MethodId!.Value)
                .Distinct()
                .Single();
            return BreakpointProtocolJson.NormalizeAndValidate(new BreakpointHitResolution(
                BreakpointHitResolution.CurrentSchemaVersion,
                hit,
                methodId,
                matchingBindings
                    .Select(static binding => new BreakpointHitMatch(
                        binding.BreakpointId,
                        binding.NodeId))
                    .ToArray()));
        }
    }

    private void RebindCore()
    {
        if (generation is null || sourceMap is null)
        {
            snapshot = null;
            return;
        }

        var nodeIndex = sourceMap.Methods
            .SelectMany(method => method.Nodes.Select(node => new NodeLocation(method, node)))
            .ToDictionary(static item => item.Node.NodeId);
        SemanticBreakpointBinding[] bindings = breakpoints.Values
            .OrderBy(static breakpoint => breakpoint.BreakpointId.Value, StringComparer.Ordinal)
            .Select(breakpoint => BindBreakpoint(breakpoint, nodeIndex))
            .ToArray();
        snapshot = BreakpointProtocolJson.NormalizeAndValidate(new BreakpointBindingSnapshot(
            BreakpointBindingSnapshot.CurrentSchemaVersion,
            generation,
            bindings));
    }

    private static SemanticBreakpointBinding BindBreakpoint(
        SemanticBreakpoint breakpoint,
        IReadOnlyDictionary<SemanticId, NodeLocation> nodeIndex)
    {
        if (!breakpoint.Enabled)
        {
            return new SemanticBreakpointBinding(
                breakpoint.BreakpointId,
                breakpoint.NodeId,
                BreakpointBindingState.Disabled,
                null,
                null,
                null,
                [],
                null);
        }

        if (!nodeIndex.TryGetValue(breakpoint.NodeId, out NodeLocation? location))
        {
            return new SemanticBreakpointBinding(
                breakpoint.BreakpointId,
                breakpoint.NodeId,
                BreakpointBindingState.Pending,
                null,
                null,
                null,
                [],
                "The semantic node is not executable in this program generation.");
        }

        IReadOnlyDictionary<int, CSharpSequencePoint> points = location.Method.SequencePoints
            .ToDictionary(static point => point.IlOffset);
        return new SemanticBreakpointBinding(
            breakpoint.BreakpointId,
            breakpoint.NodeId,
            BreakpointBindingState.Bound,
            location.Method.MethodId,
            location.Method.MetadataToken,
            location.Node.SourceSpan,
            location.Node.IlOffsets
                .Select(offset => new CoreClrBreakpointLocation(
                    location.Method.MetadataToken,
                    offset,
                    points[offset].Span))
                .ToArray(),
            null);
    }

    private static void ValidateBreakpoint(SemanticBreakpoint breakpoint)
    {
        ArgumentNullException.ThrowIfNull(breakpoint);
        if (breakpoint.BreakpointId.IsEmpty || breakpoint.NodeId.IsEmpty)
        {
            throw new ArgumentException("A semantic breakpoint requires breakpoint and node IDs.",
                nameof(breakpoint));
        }
    }

    private sealed record NodeLocation(
        CSharpMethodSourceMap Method,
        CSharpNodeIlMap Node);
}

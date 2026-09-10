using System.Text.Json;
using Finare.Core;
using Finare.Runtime;

namespace Finare.Tests;

public sealed class BreakpointProtocolJsonTests
{
    private static readonly SemanticId DomainId = SemanticId.FromKey("debug-domain");
    private static readonly SemanticId GenerationId = SemanticId.FromKey("debug-generation");
    private static readonly SemanticId CompilationId = SemanticId.FromKey("debug-compilation");
    private static readonly SemanticId ConfigurationId = SemanticId.FromKey("debug-configuration");
    private static readonly SemanticId MethodId = SemanticId.FromKey("debug-method");
    private static readonly SemanticId NodeId = SemanticId.FromKey("debug-node");
    private static readonly SemanticId BreakpointId = SemanticId.FromKey("debug-breakpoint");
    private static readonly ProgramImageKey ImageKey = new($"sha256:{new string('a', 64)}");
    private static readonly SourceSpan Span = new(
        "Tests/Debug/Rules.cs",
        new SourcePosition(10, 9),
        new SourcePosition(10, 24));

    [Fact]
    public void BindingHitAndResolutionJson_RoundTripCanonically()
    {
        var generation = new CoreClrProgramGeneration(
            DomainId,
            1,
            GenerationId,
            ImageKey,
            CompilationId,
            ConfigurationId);
        var binding = new SemanticBreakpointBinding(
            BreakpointId,
            NodeId,
            BreakpointBindingState.Bound,
            MethodId,
            0x06000001,
            Span,
            [new CoreClrBreakpointLocation(0x06000001, 4, Span)],
            null);
        var snapshot = new BreakpointBindingSnapshot(
            BreakpointBindingSnapshot.CurrentSchemaVersion,
            generation,
            [binding]);
        CoreClrBreakpointHit hit = CoreClrBreakpointHit.Create(generation, 0x06000001, 4);
        var resolution = new BreakpointHitResolution(
            BreakpointHitResolution.CurrentSchemaVersion,
            hit,
            MethodId,
            [new BreakpointHitMatch(BreakpointId, NodeId)]);

        string bindingsJson = BreakpointProtocolJson.SerializeBindings(snapshot);
        string hitJson = BreakpointProtocolJson.SerializeHit(hit);
        string resolutionJson = BreakpointProtocolJson.SerializeResolution(resolution);

        Assert.Equal(
            bindingsJson,
            BreakpointProtocolJson.SerializeBindings(
                BreakpointProtocolJson.DeserializeBindings(bindingsJson)));
        Assert.Equal(
            hitJson,
            BreakpointProtocolJson.SerializeHit(BreakpointProtocolJson.DeserializeHit(hitJson)));
        Assert.Equal(
            resolutionJson,
            BreakpointProtocolJson.SerializeResolution(
                BreakpointProtocolJson.DeserializeResolution(resolutionJson)));
    }

    [Fact]
    public void BindingJson_RejectsPendingBindingThatCarriesExecutableLocation()
    {
        var generation = new CoreClrProgramGeneration(
            DomainId,
            1,
            GenerationId,
            ImageKey,
            CompilationId,
            ConfigurationId);
        var invalid = new SemanticBreakpointBinding(
            BreakpointId,
            NodeId,
            BreakpointBindingState.Pending,
            MethodId,
            0x06000001,
            Span,
            [new CoreClrBreakpointLocation(0x06000001, 4, Span)],
            "pending");

        Assert.Throws<JsonException>(() => BreakpointProtocolJson.SerializeBindings(
            new BreakpointBindingSnapshot(
                BreakpointBindingSnapshot.CurrentSchemaVersion,
                generation,
                [invalid])));
    }

    [Fact]
    public void HitJson_RejectsOldSchemaAndNonMethodToken()
    {
        var oldSchema = new CoreClrBreakpointHit(
            "unifiedscript.breakpoint-hit/v0.0",
            DomainId,
            GenerationId,
            ImageKey,
            0x06000001,
            0);
        var wrongToken = oldSchema with
        {
            SchemaVersion = CoreClrBreakpointHit.CurrentSchemaVersion,
            MetadataToken = 0x02000001,
        };

        Assert.Throws<JsonException>(() => BreakpointProtocolJson.SerializeHit(oldSchema));
        Assert.Throws<JsonException>(() => BreakpointProtocolJson.SerializeHit(wrongToken));
    }
}

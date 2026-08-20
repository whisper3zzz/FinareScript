using UnifiedScript.Core;

namespace UnifiedScript.Runtime;

public sealed record CoreClrProgramGeneration(
    SemanticId DomainId,
    long Number,
    SemanticId GenerationId,
    ProgramImageKey ImageKey,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId);

public sealed record SemanticBreakpoint(
    SemanticId BreakpointId,
    SemanticId NodeId,
    bool Enabled = true);

public enum BreakpointBindingState
{
    Bound,
    Pending,
    Disabled,
}

public sealed record CoreClrBreakpointLocation(
    int MetadataToken,
    int IlOffset,
    SourceSpan Span);

public sealed record SemanticBreakpointBinding(
    SemanticId BreakpointId,
    SemanticId NodeId,
    BreakpointBindingState State,
    SemanticId? MethodId,
    int? MetadataToken,
    SourceSpan? NodeSourceSpan,
    IReadOnlyList<CoreClrBreakpointLocation> Locations,
    string? Message);

public sealed record BreakpointBindingSnapshot(
    string SchemaVersion,
    CoreClrProgramGeneration Generation,
    IReadOnlyList<SemanticBreakpointBinding> Bindings)
{
    public const string CurrentSchemaVersion = "unifiedscript.breakpoint-bindings/v0.1";
}

public sealed record CoreClrBreakpointHit(
    string SchemaVersion,
    SemanticId DomainId,
    SemanticId GenerationId,
    ProgramImageKey ImageKey,
    int MetadataToken,
    int IlOffset)
{
    public const string CurrentSchemaVersion = "unifiedscript.breakpoint-hit/v0.1";

    public static CoreClrBreakpointHit Create(
        CoreClrProgramGeneration generation,
        int metadataToken,
        int ilOffset)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return new CoreClrBreakpointHit(
            CurrentSchemaVersion,
            generation.DomainId,
            generation.GenerationId,
            generation.ImageKey,
            metadataToken,
            ilOffset);
    }
}

public sealed record BreakpointHitMatch(
    SemanticId BreakpointId,
    SemanticId NodeId);

public sealed record BreakpointHitResolution(
    string SchemaVersion,
    CoreClrBreakpointHit Hit,
    SemanticId MethodId,
    IReadOnlyList<BreakpointHitMatch> Matches)
{
    public const string CurrentSchemaVersion = "unifiedscript.breakpoint-resolution/v0.1";
}

public sealed class BreakpointProtocolException : Exception
{
    public BreakpointProtocolException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

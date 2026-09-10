using Finare.Core;

namespace Finare.CSharp;

public sealed record CSharpProgramSourceMap(
    string SchemaVersion,
    string AssemblyName,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    IReadOnlyList<CSharpMethodSourceMap> Methods)
{
    public const string CurrentSchemaVersion = "unifiedscript.source-map/v0.1";
}

public sealed record CSharpMethodSourceMap(
    SemanticId MethodId,
    string MetadataName,
    int MetadataToken,
    int? EntryIlOffset,
    IReadOnlyList<CSharpSequencePoint> SequencePoints,
    IReadOnlyList<CSharpNodeIlMap> Nodes);

public sealed record CSharpSequencePoint(
    int IlOffset,
    SourceSpan Span);

public sealed record CSharpNodeIlMap(
    SemanticId NodeId,
    SourceSpan SourceSpan,
    IReadOnlyList<int> IlOffsets);

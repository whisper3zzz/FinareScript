using Finare.Core;

namespace Finare.Semantics;

public enum UnifiedTypeKind
{
    Unknown,
    Void,
    Boolean,
    Integer,
    FloatingPoint,
    String,
    Enum,
    Struct,
    Object,
    Interface,
    Delegate,
    Array,
    TypeParameter,
    TaskLike,
    Enumerable,
    AsyncEnumerable,
    BorrowedSequence,
}

public enum UnifiedNullability
{
    Unknown,
    Oblivious,
    NotNull,
    Nullable,
}

public enum UnifiedNullFlowState
{
    Unknown,
    NotNull,
    MaybeNull,
}

[Flags]
public enum MethodEffects
{
    None = 0,
    ReadsState = 1 << 0,
    WritesState = 1 << 1,
    Allocates = 1 << 2,
    Throws = 1 << 3,
    Latent = 1 << 4,
    Async = 1 << 5,
    GameThreadOnly = 1 << 6,
    AuthorityOnly = 1 << 7,
    EditorOnly = 1 << 8,
}

public sealed record TypeDescriptor(
    SemanticId Id,
    string DisplayName,
    string MetadataName,
    UnifiedTypeKind Kind,
    UnifiedNullability Nullability,
    bool IsGraphSafe,
    SemanticId? ElementTypeId = null);

public enum ForEachIterationStrategy
{
    Indexed,
    Enumerator,
    AsyncEnumerator,
}

public enum ForEachIterationKind
{
    Value,
    Ref,
    RefReadOnly,
}

public enum ForEachDisposalKind
{
    None,
    Required,
    Conditional,
}

public sealed record BoundForEachInfo(
    SemanticId NodeId,
    ForEachIterationStrategy Strategy,
    ForEachIterationKind IterationKind,
    SemanticId CollectionTypeId,
    SemanticId ElementTypeId,
    SemanticId? EnumeratorTypeId,
    SemanticId? GetEnumeratorMethodId,
    SemanticId? MoveNextMethodId,
    SemanticId? CurrentGetterMethodId,
    SemanticId? DisposeMethodId,
    ForEachDisposalKind DisposalKind)
{
    public bool RequiresDisposal => DisposalKind != ForEachDisposalKind.None;
}

public sealed record ParameterDescriptor(
    string Name,
    SemanticId TypeId);

public enum MethodBodyRepresentation
{
    VisualGraph,
    OpaqueCode,
    ExternalApi,
}

public sealed record MethodDescriptor(
    SemanticId Id,
    string DisplayName,
    string MetadataName,
    SemanticId ContainingTypeId,
    SemanticId ReturnTypeId,
    IReadOnlyList<ParameterDescriptor> Parameters,
    bool IsStatic,
    MethodBodyRepresentation BodyRepresentation,
    MethodEffects Effects);

public sealed record BoundNodeInfo(
    SemanticId NodeId,
    SemanticId? TypeId = null,
    SemanticId? DeclaredSymbolId = null,
    SemanticId? ReferencedSymbolId = null,
    SemanticId? InvokedMethodId = null,
    MethodEffects Effects = MethodEffects.None,
    UnifiedNullFlowState NullFlowState = UnifiedNullFlowState.Unknown);

public sealed record BoundCompilation(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId ConfigurationId,
    IReadOnlyList<TypeDescriptor> Types,
    IReadOnlyList<MethodDescriptor> Methods,
    IReadOnlyList<BoundNodeInfo> Nodes,
    IReadOnlyList<BoundForEachInfo> ForEachLoops)
{
    public const string CurrentSchemaVersion = "unifiedscript.semantics/v0.6";
}

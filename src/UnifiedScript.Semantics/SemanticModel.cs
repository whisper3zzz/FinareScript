using UnifiedScript.Core;

namespace UnifiedScript.Semantics;

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
}

public enum UnifiedNullability
{
    Unknown,
    Oblivious,
    NotNull,
    Nullable,
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
    bool IsGraphSafe);

public sealed record ParameterDescriptor(
    string Name,
    SemanticId TypeId);

public sealed record MethodDescriptor(
    SemanticId Id,
    string DisplayName,
    string MetadataName,
    SemanticId ContainingTypeId,
    SemanticId ReturnTypeId,
    IReadOnlyList<ParameterDescriptor> Parameters,
    bool IsStatic,
    MethodEffects Effects);

public sealed record BoundNodeInfo(
    SemanticId NodeId,
    SemanticId? TypeId = null,
    SemanticId? DeclaredSymbolId = null,
    SemanticId? ReferencedSymbolId = null,
    SemanticId? InvokedMethodId = null,
    MethodEffects Effects = MethodEffects.None);

public sealed record BoundCompilation(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    IReadOnlyList<TypeDescriptor> Types,
    IReadOnlyList<MethodDescriptor> Methods,
    IReadOnlyList<BoundNodeInfo> Nodes)
{
    public const string CurrentSchemaVersion = "unifiedscript.semantics/v0.1";
}

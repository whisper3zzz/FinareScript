using System.Text.Json.Serialization;

namespace UnifiedScript.Unreal;

public enum UnrealReflectedTypeKind
{
    Class,
    Struct,
    Enum,
}

public enum UnrealValueKind
{
    Void,
    Boolean,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,
    String,
    Name,
    Text,
    Object,
    Class,
    SoftObject,
    SoftClass,
    Struct,
    Enum,
    Array,
    Set,
    Map,
    Unsupported,
}

public sealed record UnrealBlueprintMetadata(
    string? DisplayName = null,
    string? Category = null,
    string? Tooltip = null,
    bool Deprecated = false,
    string? DeprecationMessage = null,
    string? WorldContextParameter = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Latent { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LatentInfoParameter { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ThreadSafe { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CallableWithoutWorldContext { get; init; }
}

public sealed record UnrealEnumValueDefinition(
    string Name,
    long Value,
    string? DisplayName = null);

public enum UnrealParameterDirection
{
    Input,
    Output,
    InputOutput,
}

public enum UnrealNativeAccess
{
    Public,
    Protected,
    Private,
}

public enum UnrealDelegateKind
{
    SingleCast,
    Multicast,
}

public sealed record UnrealEngineIdentity(
    int Major,
    int Minor,
    int Patch,
    string Branch);

public sealed record UnrealTypeReference(
    UnrealValueKind Kind,
    string NativeName,
    string? TypePath = null,
    UnrealTypeReference? ElementType = null,
    UnrealTypeReference? KeyType = null,
    UnrealTypeReference? ValueType = null,
    string? HeaderPath = null);

public sealed record UnrealTypeTraits(
    bool Abstract,
    bool BlueprintType)
{
    public bool Interface { get; init; }
}

public sealed record UnrealPropertyAccess(
    bool Readable,
    bool Writable,
    bool EditorVisible,
    UnrealNativeAccess NativeAccess = UnrealNativeAccess.Public);

public sealed record UnrealReflectedProperty(
    string Path,
    string Name,
    UnrealTypeReference Type,
    UnrealPropertyAccess Access)
{
    public UnrealBlueprintMetadata Metadata { get; init; } = new();
}

public sealed record UnrealReflectedParameter(
    string Name,
    UnrealTypeReference Type,
    UnrealParameterDirection Direction)
{
    /// <summary>Canonical Unreal default literal exported from CPP_Default_* metadata.</summary>
    public string? DefaultValue { get; init; }
}

public sealed record UnrealFunctionTraits(
    bool Static,
    bool Pure,
    bool Callable,
    UnrealNativeAccess NativeAccess = UnrealNativeAccess.Public);

public sealed record UnrealReflectedFunction(
    string Path,
    string Name,
    UnrealTypeReference ReturnType,
    IReadOnlyList<UnrealReflectedParameter> Parameters,
    UnrealFunctionTraits Traits)
{
    public UnrealBlueprintMetadata Metadata { get; init; } = new();
}

public sealed record UnrealDelegateTraits(
    UnrealDelegateKind Kind,
    bool BlueprintAssignable,
    bool BlueprintCallable,
    bool BlueprintAuthorityOnly,
    UnrealNativeAccess NativeAccess = UnrealNativeAccess.Public);

public sealed record UnrealReflectedDelegate(
    string Path,
    string Name,
    string SignaturePath,
    UnrealTypeReference ReturnType,
    IReadOnlyList<UnrealReflectedParameter> Parameters,
    UnrealDelegateTraits Traits)
{
    public UnrealBlueprintMetadata Metadata { get; init; } = new();
}

public sealed record UnrealReflectedType(
    string Path,
    string Name,
    string NativeName,
    string Module,
    UnrealReflectedTypeKind Kind,
    string? BaseTypePath,
    UnrealTypeTraits Traits,
    IReadOnlyList<UnrealReflectedProperty> Properties,
    IReadOnlyList<UnrealReflectedFunction> Functions,
    string? HeaderPath = null)
{
    public IReadOnlyList<UnrealReflectedDelegate> Delegates { get; init; } = [];

    public IReadOnlyList<string> InterfacePaths { get; init; } = [];

    public IReadOnlyList<UnrealEnumValueDefinition> EnumValues { get; init; } = [];

    public UnrealBlueprintMetadata Metadata { get; init; } = new();
}

public sealed record UnrealReflectionManifest(
    string SchemaVersion,
    UnrealEngineIdentity Engine,
    IReadOnlyList<string> Modules,
    IReadOnlyList<UnrealReflectedType> Types)
{
    public const string CurrentSchemaVersion = "unifiedscript.unreal-reflection/v0.5";

    public const string PreviousSchemaVersion = "unifiedscript.unreal-reflection/v0.4";

    public const string LegacySchemaVersion = "unifiedscript.unreal-reflection/v0.3";

    public const string OlderSchemaVersion = "unifiedscript.unreal-reflection/v0.2";

    public const string OldestSchemaVersion = "unifiedscript.unreal-reflection/v0.1";

    public static UnrealReflectionManifest Create(
        UnrealEngineIdentity engine,
        IReadOnlyList<string> modules,
        IReadOnlyList<UnrealReflectedType> types) =>
        new(CurrentSchemaVersion, engine, modules, types);
}

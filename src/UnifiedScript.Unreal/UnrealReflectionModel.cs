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
    Struct,
    Enum,
    Array,
    Set,
    Map,
    Unsupported,
}

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
    UnrealTypeReference? ValueType = null);

public sealed record UnrealTypeTraits(
    bool Abstract,
    bool BlueprintType);

public sealed record UnrealPropertyAccess(
    bool Readable,
    bool Writable,
    bool EditorVisible,
    UnrealNativeAccess NativeAccess = UnrealNativeAccess.Public);

public sealed record UnrealReflectedProperty(
    string Path,
    string Name,
    UnrealTypeReference Type,
    UnrealPropertyAccess Access);

public sealed record UnrealReflectedParameter(
    string Name,
    UnrealTypeReference Type,
    UnrealParameterDirection Direction);

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
    UnrealFunctionTraits Traits);

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
    string? HeaderPath = null);

public sealed record UnrealReflectionManifest(
    string SchemaVersion,
    UnrealEngineIdentity Engine,
    IReadOnlyList<string> Modules,
    IReadOnlyList<UnrealReflectedType> Types)
{
    public const string CurrentSchemaVersion = "unifiedscript.unreal-reflection/v0.1";

    public static UnrealReflectionManifest Create(
        UnrealEngineIdentity engine,
        IReadOnlyList<string> modules,
        IReadOnlyList<UnrealReflectedType> types) =>
        new(CurrentSchemaVersion, engine, modules, types);
}

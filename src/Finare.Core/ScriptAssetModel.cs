using System.Text.Json;
using System.Text.Json.Serialization;

namespace Finare.Core;

public enum ScriptStateTypeKind
{
    Boolean,
    Integer,
    FloatingPoint,
    String,
    Enum,
    Name,
    Text,
    Object,
    Class,
    Struct,
    SoftObject,
    SoftClass,
    Array,
    Set,
    Map,
}

public sealed record ScriptExposure(
    bool InstanceEditable,
    bool SaveGame,
    bool Replicated,
    string? Category = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    global::Finare.ReplicationCondition ReplicationCondition = global::Finare.ReplicationCondition.None)
{
    public static ScriptExposure None { get; } = new(false, false, false);
}

public sealed record ScriptStateDeclaration(
    SemanticId Id,
    string Name,
    string TypeDisplayName,
    string TypeMetadataName,
    ScriptStateTypeKind TypeKind,
    JsonElement DefaultValue,
    SourceSpan Span,
    ScriptExposure Exposure,
    ScriptFunctionValueTypeDeclaration? UnrealType = null);

public enum ScriptFunctionValueTypeKind
{
    Enum,
    Struct,
    Array,
    Set,
    Map,
}

public sealed record ScriptFunctionStructFieldDeclaration(
    int Index,
    string NativeName,
    string TypeMetadataName,
    ScriptFunctionValueTypeDeclaration? UnrealType = null);

public sealed record ScriptFunctionTypeArgumentDeclaration(
    string TypeMetadataName,
    ScriptFunctionValueTypeDeclaration? UnrealType = null);

public sealed record ScriptFunctionValueTypeDeclaration(
    ScriptFunctionValueTypeKind Kind,
    string UnrealTypePath,
    string UnrealNativeName,
    IReadOnlyList<ScriptFunctionStructFieldDeclaration> Fields,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ScriptFunctionTypeArgumentDeclaration>? TypeArguments = null);

public sealed record ScriptParameterDeclaration(
    SemanticId Id,
    string Name,
    string TypeMetadataName,
    ScriptFunctionValueTypeDeclaration? UnrealType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    ScriptParameterDirection Direction = ScriptParameterDirection.Input);

public enum ScriptParameterDirection
{
    Input,
    Output,
    InputOutput,
}

public enum ScriptBlueprintEventSuperMode
{
    Explicit,
}

public sealed record ScriptFunctionOverrideDeclaration(
    string DeclaringTypePath,
    string FunctionName);

public sealed record ScriptBlueprintEventDeclaration(
    ScriptBlueprintEventSuperMode SuperMode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ScriptFunctionOverrideDeclaration? Override = null);

public enum ScriptRpcTarget { Server = 1, Client = 2, Multicast = 3 }

public sealed record ScriptRpcDeclaration(ScriptRpcTarget Target, bool Reliable);

public enum ScriptInputTriggerEvent
{
    Started,
    Ongoing,
    Triggered,
    Completed,
    Canceled,
}

public enum ScriptInputValueType
{
    Boolean,
    Axis1D,
    Axis2D,
    Axis3D,
}

public sealed record ScriptInputActionBindingDeclaration(
    SemanticId Id,
    string ActionPath,
    ScriptInputTriggerEvent TriggerEvent,
    ScriptInputValueType ValueType,
    bool ConsumeInput);

public enum ScriptDelegateSource
{
    Target,
    Host,
}

public sealed record ScriptDelegateBindingDeclaration(
    SemanticId Id,
    string SourceTypePath,
    string EventPath,
    ScriptDelegateSource Source,
    string? SourceMember);

public sealed record ScriptInterfaceFunctionDeclaration(
    SemanticId Id,
    string InterfacePath,
    string FunctionName);

public enum ScriptLifecycleHookKind
{
    BeginPlay,
    Tick,
    EndPlay,
}

public sealed record ScriptLifecycleHookDeclaration(
    SemanticId Id,
    ScriptLifecycleHookKind Kind,
    string ManagedName,
    SourceSpan Span);

public sealed record ScriptFunctionDeclaration(
    SemanticId Id,
    string Name,
    string QualifiedName,
    string ReturnTypeMetadataName,
    bool IsStatic,
    bool IsPublic,
    bool IsEvent,
    IReadOnlyList<ScriptParameterDeclaration> Parameters,
    ScriptFunctionValueTypeDeclaration? ReturnUnrealType = null,
    ScriptBlueprintEventDeclaration? BlueprintEvent = null,
    IReadOnlyList<ScriptInputActionBindingDeclaration>? InputActions = null,
    IReadOnlyList<ScriptDelegateBindingDeclaration>? DelegateBindings = null,
    IReadOnlyList<ScriptInterfaceFunctionDeclaration>? InterfaceFunctions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ScriptRpcDeclaration? Rpc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RepNotifyProperty = null);

/// <summary>
/// Engine-neutral declaration of the native class shape selected by a Script
/// Asset.  The paths are intentionally opaque here; Unreal consumers validate
/// and resolve them in their own layer.
/// </summary>
public sealed record ScriptClassBindingDeclaration(
    string Kind,
    string? BaseClassPath = null,
    string? GeneratedClassPath = null);

public sealed record ScriptAssetDeclaration(
    SemanticId Id,
    string QualifiedName,
    string AssetPath,
    SourceSpan Span,
    IReadOnlyList<ScriptStateDeclaration> States,
    IReadOnlyList<ScriptFunctionDeclaration> Functions,
    IReadOnlyList<ScriptLifecycleHookDeclaration>? LifecycleHooks = null,
    ScriptClassBindingDeclaration? ClassBinding = null);

public sealed record ScriptAssetDocument(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    IReadOnlyList<ScriptAssetDeclaration> Assets)
{
    public const string CurrentSchemaVersion = "unifiedscript.script-asset/v0.1";

    public const string OverrideSchemaVersion = "unifiedscript.script-asset/v0.2";

    public const string ClassBindingSchemaVersion = "unifiedscript.script-asset/v0.3";

    public static ScriptAssetDocument Create(
        SemanticId sourceCompilationId,
        SemanticId semanticConfigurationId,
        IReadOnlyList<ScriptAssetDeclaration> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        string schemaVersion = assets.Any(static asset => asset?.ClassBinding is not null)
            ? ClassBindingSchemaVersion
            : assets.Any(static asset =>
            asset?.Functions?.Any(static function => function?.BlueprintEvent?.Override is not null) == true)
            ? OverrideSchemaVersion
            : CurrentSchemaVersion;
        return new(schemaVersion, sourceCompilationId, semanticConfigurationId, assets);
    }
}

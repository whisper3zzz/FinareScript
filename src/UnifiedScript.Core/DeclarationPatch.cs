using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedScript.Core;

public sealed record ScriptStatePatchPrecondition(
    string Name,
    string TypeMetadataName,
    ScriptStateTypeKind TypeKind,
    JsonElement DefaultValue,
    ScriptExposure Exposure);

public sealed record ScriptStatePatchSpec(
    string Name,
    string TypeName,
    ScriptStateTypeKind TypeKind,
    JsonElement DefaultValue,
    ScriptExposure Exposure);

public sealed record ScriptAssetPatchPrecondition(
    string QualifiedName,
    string AssetPath);

public sealed record ScriptAssetPatchSpec(
    string Namespace,
    string Name,
    string AssetPath,
    string? BaseTypeName,
    bool IncludeActorLifecycle);

public sealed record ScriptFunctionParameterPatchPrecondition(
    string Name,
    string TypeMetadataName);

public sealed record ScriptFunctionParameterPatchSpec(
    string Name,
    string TypeName);

public sealed record ScriptFunctionPatchPrecondition(
    string Name,
    string ReturnTypeMetadataName,
    bool IsStatic,
    IReadOnlyList<ScriptFunctionParameterPatchPrecondition> Parameters);

public sealed record ScriptFunctionPatchSpec(
    string Name,
    string ReturnTypeName,
    bool IsStatic,
    IReadOnlyList<ScriptFunctionParameterPatchSpec> Parameters);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(AddScriptStatePatchOperation), "addState")]
[JsonDerivedType(typeof(UpdateScriptStatePatchOperation), "updateState")]
[JsonDerivedType(typeof(RemoveScriptStatePatchOperation), "removeState")]
[JsonDerivedType(typeof(AddScriptAssetPatchOperation), "addAsset")]
[JsonDerivedType(typeof(UpdateScriptAssetPatchOperation), "updateAsset")]
[JsonDerivedType(typeof(RemoveScriptAssetPatchOperation), "removeAsset")]
[JsonDerivedType(typeof(AddScriptFunctionPatchOperation), "addFunction")]
[JsonDerivedType(typeof(UpdateScriptFunctionPatchOperation), "updateFunction")]
[JsonDerivedType(typeof(RemoveScriptFunctionPatchOperation), "removeFunction")]
[JsonDerivedType(typeof(AddUnrealLifecycleHookPatchOperation), "addLifecycleHook")]
[JsonDerivedType(typeof(AddUnrealEventHandlerPatchOperation), "addUnrealEventHandler")]
public abstract record DeclarationPatchOperation;

public sealed record AddScriptAssetPatchOperation(
    string TargetSourcePath,
    ScriptAssetPatchSpec Asset) : DeclarationPatchOperation;

public sealed record UpdateScriptAssetPatchOperation(
    SemanticId TargetAssetId,
    ScriptAssetPatchPrecondition Expected,
    string ReplacementAssetPath) : DeclarationPatchOperation;

public sealed record RemoveScriptAssetPatchOperation(
    SemanticId TargetAssetId,
    ScriptAssetPatchPrecondition Expected) : DeclarationPatchOperation;

public sealed record AddScriptStatePatchOperation(
    SemanticId TargetAssetId,
    ScriptStatePatchSpec State) : DeclarationPatchOperation;

public sealed record UpdateScriptStatePatchOperation(
    SemanticId TargetStateId,
    ScriptStatePatchPrecondition Expected,
    ScriptStatePatchSpec Replacement) : DeclarationPatchOperation;

public sealed record RemoveScriptStatePatchOperation(
    SemanticId TargetStateId,
    ScriptStatePatchPrecondition Expected) : DeclarationPatchOperation;

public sealed record AddScriptFunctionPatchOperation(
    SemanticId TargetAssetId,
    ScriptFunctionPatchSpec Function) : DeclarationPatchOperation;

public sealed record UpdateScriptFunctionPatchOperation(
    SemanticId TargetFunctionId,
    ScriptFunctionPatchPrecondition Expected,
    ScriptFunctionPatchSpec Replacement) : DeclarationPatchOperation;

public sealed record RemoveScriptFunctionPatchOperation(
    SemanticId TargetFunctionId,
    ScriptFunctionPatchPrecondition Expected) : DeclarationPatchOperation;

public sealed record AddUnrealLifecycleHookPatchOperation(
    SemanticId TargetAssetId,
    string Hook) : DeclarationPatchOperation;

public sealed record UnrealEventHandlerParameterPatchSpec(
    string Name,
    string TypeName);

/// <summary>
/// Declaration-level Event Entry contract shared by native authoring surfaces
/// and the C# source rewriter. The canonical UE event path is the stable key;
/// generated wrapper member names are intentionally absent.
/// </summary>
public sealed record UnrealEventHandlerPatchSpec(
    string Name,
    string SourceTypeName,
    string EventPath,
    string Source,
    string? SourceMember,
    IReadOnlyList<UnrealEventHandlerParameterPatchSpec> Parameters);

public sealed record AddUnrealEventHandlerPatchOperation(
    SemanticId TargetAssetId,
    UnrealEventHandlerPatchSpec Handler) : DeclarationPatchOperation;

public sealed record DeclarationPatchDocument(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    IReadOnlyList<DeclarationPatchOperation> Operations)
{
    public const string CurrentSchemaVersion = "unifiedscript.declaration-patch/v0.3";
    public const string EventSchemaVersion = "unifiedscript.declaration-patch/v0.2";
    public const string LegacySchemaVersion = "unifiedscript.declaration-patch/v0.1";

    public static DeclarationPatchDocument Create(
        SemanticId sourceCompilationId,
        SemanticId semanticConfigurationId,
        IEnumerable<DeclarationPatchOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return DeclarationPatchJson.NormalizeAndValidate(new DeclarationPatchDocument(
            CurrentSchemaVersion,
            sourceCompilationId,
            semanticConfigurationId,
            operations.ToArray()));
    }
}

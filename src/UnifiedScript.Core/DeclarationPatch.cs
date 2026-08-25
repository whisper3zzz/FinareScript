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

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(AddScriptStatePatchOperation), "addState")]
[JsonDerivedType(typeof(UpdateScriptStatePatchOperation), "updateState")]
[JsonDerivedType(typeof(RemoveScriptStatePatchOperation), "removeState")]
[JsonDerivedType(typeof(AddUnrealEventHandlerPatchOperation), "addUnrealEventHandler")]
public abstract record DeclarationPatchOperation;

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
    public const string CurrentSchemaVersion = "unifiedscript.declaration-patch/v0.2";
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

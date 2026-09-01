using System.Text.Json;

namespace UnifiedScript.Core;

public enum ScriptStateTypeKind
{
    Boolean,
    Integer,
    FloatingPoint,
    String,
    Enum,
}

public sealed record ScriptExposure(
    bool InstanceEditable,
    bool SaveGame,
    bool Replicated,
    string? Category = null)
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
    ScriptExposure Exposure);

public sealed record ScriptParameterDeclaration(
    SemanticId Id,
    string Name,
    string TypeMetadataName);

public sealed record ScriptFunctionDeclaration(
    SemanticId Id,
    string Name,
    string QualifiedName,
    string ReturnTypeMetadataName,
    bool IsStatic,
    bool IsPublic,
    bool IsEvent,
    IReadOnlyList<ScriptParameterDeclaration> Parameters);

public sealed record ScriptAssetDeclaration(
    SemanticId Id,
    string QualifiedName,
    string AssetPath,
    SourceSpan Span,
    IReadOnlyList<ScriptStateDeclaration> States,
    IReadOnlyList<ScriptFunctionDeclaration> Functions);

public sealed record ScriptAssetDocument(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    IReadOnlyList<ScriptAssetDeclaration> Assets)
{
    public const string CurrentSchemaVersion = "unifiedscript.script-asset/v0.1";

    public static ScriptAssetDocument Create(
        SemanticId sourceCompilationId,
        SemanticId semanticConfigurationId,
        IReadOnlyList<ScriptAssetDeclaration> assets) =>
        new(CurrentSchemaVersion, sourceCompilationId, semanticConfigurationId, assets);
}

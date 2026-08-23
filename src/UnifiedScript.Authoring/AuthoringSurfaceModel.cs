using UnifiedScript.Core;
using UnifiedScript.Graph;

namespace UnifiedScript.Authoring;

public enum AuthoringActionCategory
{
    Entry,
    State,
    Flow,
    Call,
    Value,
    Exit,
}

public enum AuthoringEditKind
{
    Literal,
    Operator,
}

public sealed record AuthoringEditCapability(
    SemanticId Id,
    AuthoringEditKind Kind,
    string Label,
    string Operation,
    SemanticId TargetNodeId,
    string CurrentValue,
    string? ExpectedValueText,
    string? ExpectedOperator,
    SemanticId? ExpectedResultTypeId,
    SemanticId? ExpectedOperandTypeId,
    SemanticId? ExpectedRightOperandTypeId,
    IReadOnlyList<string> Options);

public sealed record AuthoringActionSurface(
    SemanticId Id,
    string GraphKind,
    AuthoringActionCategory Category,
    string Title,
    string Summary,
    SourceSpan Span,
    SemanticId RegionId,
    SemanticId? DebugTargetId,
    IReadOnlyList<SemanticId> DeclarationReferenceIds,
    IReadOnlyList<AuthoringEditCapability> EditCapabilities);

public sealed record AuthoringRegionSurface(
    SemanticId Id,
    GraphRegionKind Kind,
    SemanticId? ParentRegionId,
    SourceSpan Span,
    bool IsImplicit,
    SemanticId? AnchorActionId,
    IReadOnlyList<SemanticId> DirectActionIds,
    IReadOnlyList<SemanticId> ChildRegionIds);

public sealed record AuthoringFunctionSurface(
    SemanticId Id,
    string Name,
    string QualifiedName,
    string ReturnTypeMetadataName,
    bool IsStatic,
    IReadOnlyList<ScriptParameterDeclaration> Parameters,
    SemanticId RootRegionId,
    IReadOnlyList<AuthoringRegionSurface> Regions,
    IReadOnlyList<AuthoringActionSurface> Actions);

public sealed record AuthoringAssetSurface(
    SemanticId Id,
    string QualifiedName,
    string AssetPath,
    SourceSpan Span,
    IReadOnlyList<ScriptStateDeclaration> States,
    IReadOnlyList<AuthoringFunctionSurface> Functions);

public sealed record AuthoringSurfaceDocument(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    IReadOnlyList<AuthoringAssetSurface> Assets)
{
    public const string CurrentSchemaVersion = "unifiedscript.authoring-surface/v0.1";
}

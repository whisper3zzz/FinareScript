using Finare.Core;
using Finare.Graph;

namespace Finare.Authoring;

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

public sealed record AuthoringPortSurface(
    string Name,
    GraphPortDirection Direction,
    GraphPortKind Kind,
    SemanticId? TypeId,
    string? TypeDisplayName);

public sealed record AuthoringEdgeSurface(
    SemanticId Id,
    GraphEdgeKind Kind,
    SemanticId FromNode,
    string FromPort,
    SemanticId ToNode,
    string ToPort);

public sealed record AuthoringParameterSurface(
    SemanticId Id,
    string Name,
    string TypeMetadataName,
    SemanticId? TypeId,
    string? TypeDisplayName);

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
    IReadOnlyList<AuthoringEditCapability> EditCapabilities,
    IReadOnlyList<AuthoringPortSurface> Ports,
    IReadOnlyDictionary<string, string> Properties);

public sealed record AuthoringRegionSurface(
    SemanticId Id,
    GraphRegionKind Kind,
    SemanticId? ParentRegionId,
    SemanticId SourceNodeId,
    SourceSpan Span,
    bool IsImplicit,
    SemanticId? AnchorActionId,
    IReadOnlyList<SemanticId> DirectStatementIds,
    IReadOnlyList<SemanticId> DirectActionIds,
    IReadOnlyList<SemanticId> ChildRegionIds);

public sealed record AuthoringFunctionSurface(
    SemanticId Id,
    string Name,
    string QualifiedName,
    string ReturnTypeMetadataName,
    bool IsStatic,
    IReadOnlyList<AuthoringParameterSurface> Parameters,
    SemanticId RootRegionId,
    IReadOnlyList<AuthoringRegionSurface> Regions,
    IReadOnlyList<AuthoringActionSurface> Actions,
    IReadOnlyList<AuthoringEdgeSurface> Edges);

public sealed record AuthoringAssetSurface(
    SemanticId Id,
    string QualifiedName,
    string AssetPath,
    SourceSpan Span,
    IReadOnlyList<ScriptStateDeclaration> States,
    IReadOnlyList<AuthoringFunctionSurface> Functions);

public sealed record AuthoringDeclarationOwner(
    SemanticId DeclarationId,
    SemanticId OwnerAssetId);

public sealed record AuthoringSurfaceDocument(
    string SchemaVersion,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    IReadOnlyList<AuthoringAssetSurface> Assets)
{
    public const string LegacySchemaVersion = "unifiedscript.authoring-surface/v0.4";
    public const string CurrentSchemaVersion = "unifiedscript.authoring-surface/v0.5";

    public IReadOnlyList<AuthoringDeclarationOwner> DeclarationOwners { get; init; } = [];
}

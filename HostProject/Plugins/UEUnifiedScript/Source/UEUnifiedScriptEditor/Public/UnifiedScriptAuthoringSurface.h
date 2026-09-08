/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 17:54:22
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 14:22:40
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptEditor\Public\UnifiedScriptAuthoringSurface.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"

struct FUnifiedScriptAuthoringSpan
{
    FString FilePath;
    int32 StartLine = 0;
    int32 StartColumn = 0;
    int32 EndLine = 0;
    int32 EndColumn = 0;

    bool IsValid() const;
    FString ToDisplayString() const;
};

struct FUnifiedScriptAuthoringExposure
{
    bool bInstanceEditable = false;
    bool bSaveGame = false;
    bool bReplicated = false;
    FString Category;
};

struct FUnifiedScriptAuthoringState
{
    FString Id;
    FString Name;
    FString TypeDisplayName;
    FString TypeMetadataName;
    FString TypeKind;
    FString DefaultValue;
    FUnifiedScriptAuthoringSpan Span;
    FUnifiedScriptAuthoringExposure Exposure;
};

struct FUnifiedScriptAuthoringParameter
{
    FString Id;
    FString Name;
    FString TypeMetadataName;
    FString TypeId;
    FString TypeDisplayName;
};

struct FUnifiedScriptAuthoringEdit
{
    FString Id;
    FString Kind;
    FString Label;
    FString Operation;
    FString TargetNodeId;
    FString CurrentValue;
    FString ExpectedValueText;
    FString ExpectedOperator;
    FString ExpectedResultTypeId;
    FString ExpectedOperandTypeId;
    FString ExpectedRightOperandTypeId;
    TArray<FString> Options;
};

struct FUnifiedScriptAuthoringPort
{
    FString Name;
    FString Direction;
    FString Kind;
    FString TypeId;
    FString TypeDisplayName;

    bool IsInput() const { return Direction == TEXT("input"); }
    bool IsControl() const { return Kind == TEXT("control"); }
};

struct FUnifiedScriptAuthoringEdge
{
    FString Id;
    FString Kind;
    FString FromNode;
    FString FromPort;
    FString ToNode;
    FString ToPort;
};

struct FUnifiedScriptAuthoringAction
{
    FString Id;
    FString GraphKind;
    FString Category;
    FString Title;
    FString Summary;
    FUnifiedScriptAuthoringSpan Span;
    FString RegionId;
    FString DebugTargetId;
    TArray<FString> DeclarationReferenceIds;
    TArray<FUnifiedScriptAuthoringEdit> EditCapabilities;
    TArray<FUnifiedScriptAuthoringPort> Ports;
    TMap<FString, FString> Properties;

    const FUnifiedScriptAuthoringPort* FindPort(
        const FString& PortName,
        const FString& PortDirection) const;
};

struct FUnifiedScriptAuthoringRegion
{
    FString Id;
    FString Kind;
    FString ParentRegionId;
    FString SourceNodeId;
    FUnifiedScriptAuthoringSpan Span;
    bool bImplicit = false;
    FString AnchorActionId;
    TArray<FString> DirectStatementIds;
    TArray<FString> DirectActionIds;
    TArray<FString> ChildRegionIds;
};

struct FUnifiedScriptAuthoringFunction
{
    FString Id;
    FString Name;
    FString QualifiedName;
    FString ReturnTypeMetadataName;
    bool bStatic = false;
    TArray<FUnifiedScriptAuthoringParameter> Parameters;
    FString RootRegionId;
    TArray<FUnifiedScriptAuthoringRegion> Regions;
    TArray<FUnifiedScriptAuthoringAction> Actions;
    TArray<FUnifiedScriptAuthoringEdge> Edges;

    const FUnifiedScriptAuthoringRegion* FindRegion(const FString& RegionId) const;
    const FUnifiedScriptAuthoringAction* FindAction(const FString& ActionId) const;
    const FUnifiedScriptAuthoringEdge* FindEdge(const FString& EdgeId) const;
};

struct FUnifiedScriptAuthoringAsset
{
    FString Id;
    FString QualifiedName;
    FString AssetPath;
    FUnifiedScriptAuthoringSpan Span;
    TArray<FUnifiedScriptAuthoringState> States;
    TArray<FUnifiedScriptAuthoringFunction> Functions;
};

struct FUnifiedScriptAuthoringDeclarationOwner
{
    FString DeclarationId;
    FString OwnerAssetId;
};

struct FUnifiedScriptAuthoringSurfaceDocument
{
    FString SchemaVersion;
    FString SourceCompilationId;
    FString SemanticConfigurationId;
    TArray<FUnifiedScriptAuthoringAsset> Assets;
    TArray<FUnifiedScriptAuthoringDeclarationOwner> DeclarationOwners;

    const FUnifiedScriptAuthoringAsset* FindDeclarationOwnerAsset(
        const FString& DeclarationId) const;

    int32 GetStateCount() const;
    int32 GetFunctionCount() const;
    int32 GetActionCount() const;
    int32 GetEdgeCount() const;
};

/** Strict reader for the engine-neutral Authoring Surface v0.4/v0.5 editing contract. */
class UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptAuthoringSurfaceReader
{
public:
    static bool LoadFile(
        const FString& SurfacePath,
        FUnifiedScriptAuthoringSurfaceDocument& OutDocument,
        FString& OutError);

    static bool ParseJson(
        const FString& SurfaceJson,
        FUnifiedScriptAuthoringSurfaceDocument& OutDocument,
        FString& OutError);

    /** Builds the same semantic-patch v0.28 payload consumed by the shared authoring host. */
    static bool BuildSemanticPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringEdit& Edit,
        const FString& NewValue,
        FString& OutPatchJson,
        FString& OutError);

    /** Builds a preconditioned semantic-patch v0.28 data-edge rewire transaction. */
    static bool BuildDataEdgeReconnectionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringEdge& ExpectedEdge,
        const FString& NewSourceNodeId,
        const FString& NewSourcePort,
        FString& OutPatchJson,
        FString& OutError);

    /** Maps a safe control-wire gesture to a region-aware statement move. */
    static bool BuildControlEdgeReconnectionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringEdge& ExpectedEdge,
        const FString& NewSourceNodeId,
        const FString& NewSourcePort,
        FString& OutPatchJson,
        FString& OutError);

    /** Replaces one connected data input with an explicit typed default expression. */
    static bool BuildDataEdgeDisconnectionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringEdge& ExpectedEdge,
        FString& OutPatchJson,
        FString& OutError);

    /** Replaces one connected data input with a bounded typed literal or default value. */
    static bool BuildDataInputValuePatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringEdge& ExpectedEdge,
        const FString& NewValueText,
        FString& OutPatchJson,
        FString& OutError);

    /** Promotes the expression feeding one data input to a named local declaration. */
    static bool BuildDataInputPromotionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringEdge& ExpectedEdge,
        const FString& LocalName,
        FString& OutPatchJson,
        FString& OutError);

    /** Shares a computed graph value through an automatically introduced local temporary. */
    static bool BuildSharedDataEdgePatchPlan(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringEdge& ExpectedEdge,
        const FString& NewSourceNodeId,
        const FString& NewSourcePort,
        FString& OutPlanJson,
        FString& OutError);

    /** Inserts a bounded built-in node template into an explicit structured block. */
    static bool BuildBasicNodeInsertionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringRegion& BlockRegion,
        const FString& TemplateId,
        FString& OutPatchJson,
        FString& OutError);

    /** Inserts a typed case/default section into one exact switch statement. */
    static bool BuildSwitchSectionInsertionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringAction& SwitchStatement,
        const FString& LabelText,
        FString& OutPatchJson,
        FString& OutError);

    /** Replaces all labels on one exact switch section from bounded pattern text. */
    static bool BuildSwitchSectionReplacementPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringAction& CaseLabel,
        const FString& LabelText,
        FString& OutPatchJson,
        FString& OutError);

    /** Moves one exact switch section by one ordered sibling slot. */
    static bool BuildSwitchSectionMovePatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringAction& CaseLabel,
        int32 Direction,
        FString& OutPatchJson,
        FString& OutError);

    /** Deletes one exact empty/break-only switch section. */
    static bool BuildSwitchSectionDeletionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringAction& CaseLabel,
        FString& OutPatchJson,
        FString& OutError);

    /** Inserts one reflected instance call using existing same-function parameters. */
    static bool BuildMemberInvocationInsertionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringRegion& BlockRegion,
        const FUnifiedScriptAuthoringParameter& Receiver,
        const FString& MemberName,
        const FString& ResultTypeName,
        const FString& ResultTypeId,
        const TArray<FUnifiedScriptAuthoringParameter>& Arguments,
        FString& OutPatchJson,
        FString& OutError);

    /** Inserts one reflected readable member access using an existing typed receiver. */
    static bool BuildMemberAccessInsertionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringRegion& BlockRegion,
        const FUnifiedScriptAuthoringParameter& Receiver,
        const FString& MemberName,
        const FString& ResultTypeName,
        const FString& ResultTypeId,
        FString& OutPatchJson,
        FString& OutError);

    /** Creates one C#-owned Script Asset in an existing workspace source. */
    static bool BuildScriptAssetCreationPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FString& TargetSourcePath,
        const FString& Namespace,
        const FString& TypeName,
        const FString& AssetPath,
        bool bIncludeActorLifecycle,
        FString& OutPatchJson,
        FString& OutError);

    /** Changes the stable engine-facing path of one Script Asset. */
    static bool BuildScriptAssetPathUpdatePatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringAsset& Asset,
        const FString& AssetPath,
        FString& OutPatchJson,
        FString& OutError);

    /** Removes one exact Script Asset type after preview confirmation. */
    static bool BuildScriptAssetRemovalPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringAsset& Asset,
        FString& OutPatchJson,
        FString& OutError);

    /** Adds one exposed C# state declaration. DefaultValueText is strict JSON. */
    static bool BuildStateAdditionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringAsset& Asset,
        const FString& Name,
        const FString& TypeName,
        const FString& TypeKind,
        const FString& DefaultValueText,
        const FUnifiedScriptAuthoringExposure& Exposure,
        FString& OutPatchJson,
        FString& OutError);

    /** Updates one exact state declaration and its exposure contract. */
    static bool BuildStateUpdatePatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringState& State,
        const FString& Name,
        const FString& TypeName,
        const FString& TypeKind,
        const FString& DefaultValueText,
        const FUnifiedScriptAuthoringExposure& Exposure,
        FString& OutPatchJson,
        FString& OutError);

    /** Removes one exact state declaration. */
    static bool BuildStateRemovalPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringState& State,
        FString& OutPatchJson,
        FString& OutError);

    /** Adds one bounded scalar Visual function with a generated safe body. */
    static bool BuildFunctionAdditionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringAsset& Asset,
        const FString& Name,
        const FString& ReturnTypeName,
        bool bStatic,
        const TArray<FUnifiedScriptAuthoringParameter>& Parameters,
        FString& OutPatchJson,
        FString& OutError);

    /** Updates one ordinary Visual function contract with exact preconditions. */
    static bool BuildFunctionUpdatePatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FString& Name,
        const FString& ReturnTypeName,
        bool bStatic,
        const TArray<FUnifiedScriptAuthoringParameter>& Parameters,
        FString& OutPatchJson,
        FString& OutError);

    /** Removes one exact Visual function or reflected Event Entry. */
    static bool BuildFunctionRemovalPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        FString& OutPatchJson,
        FString& OutError);

    /** Adds beginPlay, tick, or endPlay to an UnrealActorScript asset. */
    static bool BuildLifecycleHookAdditionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringAsset& Asset,
        const FString& Hook,
        FString& OutPatchJson,
        FString& OutError);

    /** Adds a declaration-authored visual method bound to one canonical UE event. */
    static bool BuildUnrealEventHandlerInsertionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringAsset& Asset,
        const FString& HandlerName,
        const FString& SourceTypeName,
        const FString& EventPath,
        const TArray<FUnifiedScriptAuthoringParameter>& Parameters,
        FString& OutPatchJson,
        FString& OutError);

    /** Deletes one statement with exact parent block and sibling preconditions. */
    static bool BuildStatementDeletionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringAction& Statement,
        FString& OutPatchJson,
        FString& OutError);

    /** Duplicates one statement into an exact same-block gap with fresh semantic identity. */
    static bool BuildStatementDuplicationPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringAction& SourceStatement,
        const FUnifiedScriptAuthoringAction& DestinationStatement,
        const FString& NewLocalName,
        FString& OutPatchJson,
        FString& OutError);

    /** Duplicates one consecutive statement range into an exact same-method region gap. */
    static bool BuildStatementRangeDuplicationPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const TArray<FString>& SourceStatementActionIds,
        const FUnifiedScriptAuthoringAction& DestinationStatement,
        const TMap<FString, FString>& NewLocalNamesByActionId,
        FString& OutPatchJson,
        FString& OutError);

    /** Extracts one consecutive statement range into a typed private Visual method. */
    static bool BuildCallableExtractionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const TArray<FString>& StatementActionIds,
        const FString& MethodName,
        FString& OutPatchJson,
        FString& OutError);

    /** Wraps two or more consecutive statements in one explicit lexical block. */
    static bool BuildStatementCollapsePatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const TArray<FString>& StatementActionIds,
        FString& OutPatchJson,
        FString& OutError);

    /** Expands one nested lexical block back into its parent statement list. */
    static bool BuildBlockExpansionPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringRegion& BlockRegion,
        FString& OutPatchJson,
        FString& OutError);

    /** Expands the same machine-independent path tokens used by Script Assets. */
    static FString ResolveConfiguredPath(const FString& ConfiguredPath);
};

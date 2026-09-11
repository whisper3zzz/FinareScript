/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 17:54:22
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 14:22:40
 * @FilePath: \Plugins\Finare\Source\FinareEditor\Public\FinareAuthoringSurface.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"

struct FFinareClassBindingDeclaration;

struct FFinareAuthoringSpan
{
    FString FilePath;
    int32 StartLine = 0;
    int32 StartColumn = 0;
    int32 EndLine = 0;
    int32 EndColumn = 0;

    bool IsValid() const;
    FString ToDisplayString() const;
};

struct FFinareAuthoringExposure
{
    bool bInstanceEditable = false;
    bool bSaveGame = false;
    bool bReplicated = false;
    FString Category;
};

struct FFinareAuthoringState
{
    FString Id;
    FString Name;
    FString TypeDisplayName;
    FString TypeMetadataName;
    FString TypeKind;
    FString DefaultValue;
    FFinareAuthoringSpan Span;
    FFinareAuthoringExposure Exposure;
};

struct FFinareAuthoringParameter
{
    FString Id;
    FString Name;
    FString TypeMetadataName;
    FString TypeId;
    FString TypeDisplayName;
};

struct FFinareAuthoringEdit
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

struct FFinareAuthoringPort
{
    FString Name;
    FString Direction;
    FString Kind;
    FString TypeId;
    FString TypeDisplayName;

    bool IsInput() const { return Direction == TEXT("input"); }
    bool IsControl() const { return Kind == TEXT("control"); }
};

struct FFinareAuthoringEdge
{
    FString Id;
    FString Kind;
    FString FromNode;
    FString FromPort;
    FString ToNode;
    FString ToPort;
};

struct FFinareAuthoringAction
{
    FString Id;
    FString GraphKind;
    FString Category;
    FString Title;
    FString Summary;
    FFinareAuthoringSpan Span;
    FString RegionId;
    FString DebugTargetId;
    TArray<FString> DeclarationReferenceIds;
    TArray<FFinareAuthoringEdit> EditCapabilities;
    TArray<FFinareAuthoringPort> Ports;
    TMap<FString, FString> Properties;

    const FFinareAuthoringPort* FindPort(
        const FString& PortName,
        const FString& PortDirection) const;
};

struct FFinareAuthoringRegion
{
    FString Id;
    FString Kind;
    FString ParentRegionId;
    FString SourceNodeId;
    FFinareAuthoringSpan Span;
    bool bImplicit = false;
    FString AnchorActionId;
    TArray<FString> DirectStatementIds;
    TArray<FString> DirectActionIds;
    TArray<FString> ChildRegionIds;
};

struct FFinareAuthoringFunction
{
    FString Id;
    FString Name;
    FString QualifiedName;
    FString ReturnTypeMetadataName;
    bool bStatic = false;
    TArray<FFinareAuthoringParameter> Parameters;
    FString RootRegionId;
    TArray<FFinareAuthoringRegion> Regions;
    TArray<FFinareAuthoringAction> Actions;
    TArray<FFinareAuthoringEdge> Edges;

    const FFinareAuthoringRegion* FindRegion(const FString& RegionId) const;
    const FFinareAuthoringAction* FindAction(const FString& ActionId) const;
    const FFinareAuthoringEdge* FindEdge(const FString& EdgeId) const;
};

struct FFinareAuthoringAsset
{
    FString Id;
    FString QualifiedName;
    FString AssetPath;
    FFinareAuthoringSpan Span;
    TArray<FFinareAuthoringState> States;
    TArray<FFinareAuthoringFunction> Functions;
};

struct FFinareAuthoringDeclarationOwner
{
    FString DeclarationId;
    FString OwnerAssetId;
};

struct FFinareAuthoringSurfaceDocument
{
    FString SchemaVersion;
    FString SourceCompilationId;
    FString SemanticConfigurationId;
    TArray<FFinareAuthoringAsset> Assets;
    TArray<FFinareAuthoringDeclarationOwner> DeclarationOwners;

    const FFinareAuthoringAsset* FindDeclarationOwnerAsset(
        const FString& DeclarationId) const;

    int32 GetStateCount() const;
    int32 GetFunctionCount() const;
    int32 GetActionCount() const;
    int32 GetEdgeCount() const;
};

/** Strict reader for the engine-neutral Authoring Surface v0.4/v0.5 editing contract. */
class FINAREEDITOR_API FFinareAuthoringSurfaceReader
{
public:
    static bool LoadFile(
        const FString& SurfacePath,
        FFinareAuthoringSurfaceDocument& OutDocument,
        FString& OutError);

    static bool ParseJson(
        const FString& SurfaceJson,
        FFinareAuthoringSurfaceDocument& OutDocument,
        FString& OutError);

    /** Builds the same semantic-patch v0.28 payload consumed by the shared authoring host. */
    static bool BuildSemanticPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringEdit& Edit,
        const FString& NewValue,
        FString& OutPatchJson,
        FString& OutError);

    /** Builds a preconditioned semantic-patch v0.28 data-edge rewire transaction. */
    static bool BuildDataEdgeReconnectionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringEdge& ExpectedEdge,
        const FString& NewSourceNodeId,
        const FString& NewSourcePort,
        FString& OutPatchJson,
        FString& OutError);

    /** Maps a safe control-wire gesture to a region-aware statement move. */
    static bool BuildControlEdgeReconnectionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringEdge& ExpectedEdge,
        const FString& NewSourceNodeId,
        const FString& NewSourcePort,
        FString& OutPatchJson,
        FString& OutError);

    /** Replaces one connected data input with an explicit typed default expression. */
    static bool BuildDataEdgeDisconnectionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringEdge& ExpectedEdge,
        FString& OutPatchJson,
        FString& OutError);

    /** Replaces one connected data input with a bounded typed literal or default value. */
    static bool BuildDataInputValuePatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringEdge& ExpectedEdge,
        const FString& NewValueText,
        FString& OutPatchJson,
        FString& OutError);

    /** Promotes the expression feeding one data input to a named local declaration. */
    static bool BuildDataInputPromotionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringEdge& ExpectedEdge,
        const FString& LocalName,
        FString& OutPatchJson,
        FString& OutError);

    /** Shares a computed graph value through an automatically introduced local temporary. */
    static bool BuildSharedDataEdgePatchPlan(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringEdge& ExpectedEdge,
        const FString& NewSourceNodeId,
        const FString& NewSourcePort,
        FString& OutPlanJson,
        FString& OutError);

    /** Inserts a bounded built-in node template into an explicit structured block. */
    static bool BuildBasicNodeInsertionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringRegion& BlockRegion,
        const FString& TemplateId,
        FString& OutPatchJson,
        FString& OutError);

    /** Inserts a typed case/default section into one exact switch statement. */
    static bool BuildSwitchSectionInsertionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringAction& SwitchStatement,
        const FString& LabelText,
        FString& OutPatchJson,
        FString& OutError);

    /** Replaces all labels on one exact switch section from bounded pattern text. */
    static bool BuildSwitchSectionReplacementPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringAction& CaseLabel,
        const FString& LabelText,
        FString& OutPatchJson,
        FString& OutError);

    /** Moves one exact switch section by one ordered sibling slot. */
    static bool BuildSwitchSectionMovePatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringAction& CaseLabel,
        int32 Direction,
        FString& OutPatchJson,
        FString& OutError);

    /** Deletes one exact empty/break-only switch section. */
    static bool BuildSwitchSectionDeletionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringAction& CaseLabel,
        FString& OutPatchJson,
        FString& OutError);

    /** Inserts one reflected instance call using existing same-function parameters. */
    static bool BuildMemberInvocationInsertionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringRegion& BlockRegion,
        const FFinareAuthoringParameter& Receiver,
        const FString& MemberName,
        const FString& ResultTypeName,
        const FString& ResultTypeId,
        const TArray<FFinareAuthoringParameter>& Arguments,
        FString& OutPatchJson,
        FString& OutError);

    /** Inserts one reflected readable member access using an existing typed receiver. */
    static bool BuildMemberAccessInsertionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringRegion& BlockRegion,
        const FFinareAuthoringParameter& Receiver,
        const FString& MemberName,
        const FString& ResultTypeName,
        const FString& ResultTypeId,
        FString& OutPatchJson,
        FString& OutError);

    /** Creates one C#-owned Script Asset in an existing workspace source. */
    static bool BuildScriptAssetCreationPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FString& TargetSourcePath,
        const FString& Namespace,
        const FString& TypeName,
        const FString& AssetPath,
        bool bIncludeActorLifecycle,
        FString& OutPatchJson,
        FString& OutError);

    /** Changes the stable engine-facing path of one Script Asset. */
    static bool BuildScriptAssetPathUpdatePatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringAsset& Asset,
        const FString& AssetPath,
        FString& OutPatchJson,
        FString& OutError);

    /** Changes or clears the exact native base-class declaration of one selected Script Asset. */
    static bool BuildScriptClassBasePatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringAsset& Asset,
        const FFinareClassBindingDeclaration& ExpectedClassBinding,
        const FString& ReplacementBaseClassPath,
        FString& OutPatchJson,
        FString& OutError);

    /** Removes one exact Script Asset type after preview confirmation. */
    static bool BuildScriptAssetRemovalPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringAsset& Asset,
        FString& OutPatchJson,
        FString& OutError);

    /** Adds one exposed C# state declaration. DefaultValueText is strict JSON. */
    static bool BuildStateAdditionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringAsset& Asset,
        const FString& Name,
        const FString& TypeName,
        const FString& TypeKind,
        const FString& DefaultValueText,
        const FFinareAuthoringExposure& Exposure,
        FString& OutPatchJson,
        FString& OutError);

    /** Updates one exact state declaration and its exposure contract. */
    static bool BuildStateUpdatePatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringState& State,
        const FString& Name,
        const FString& TypeName,
        const FString& TypeKind,
        const FString& DefaultValueText,
        const FFinareAuthoringExposure& Exposure,
        FString& OutPatchJson,
        FString& OutError);

    /** Removes one exact state declaration. */
    static bool BuildStateRemovalPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringState& State,
        FString& OutPatchJson,
        FString& OutError);

    /** Adds one bounded scalar Visual function with a generated safe body. */
    static bool BuildFunctionAdditionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringAsset& Asset,
        const FString& Name,
        const FString& ReturnTypeName,
        bool bStatic,
        const TArray<FFinareAuthoringParameter>& Parameters,
        FString& OutPatchJson,
        FString& OutError);

    /** Updates one ordinary Visual function contract with exact preconditions. */
    static bool BuildFunctionUpdatePatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FString& Name,
        const FString& ReturnTypeName,
        bool bStatic,
        const TArray<FFinareAuthoringParameter>& Parameters,
        FString& OutPatchJson,
        FString& OutError);

    /** Removes one exact Visual function or reflected Event Entry. */
    static bool BuildFunctionRemovalPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        FString& OutPatchJson,
        FString& OutError);

    /** Adds beginPlay, tick, or endPlay to an UnrealActorScript asset. */
    static bool BuildLifecycleHookAdditionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringAsset& Asset,
        const FString& Hook,
        FString& OutPatchJson,
        FString& OutError);

    /** Adds a declaration-authored visual method bound to one canonical UE event. */
    static bool BuildUnrealEventHandlerInsertionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringAsset& Asset,
        const FString& HandlerName,
        const FString& SourceTypeName,
        const FString& EventPath,
        const TArray<FFinareAuthoringParameter>& Parameters,
        FString& OutPatchJson,
        FString& OutError);

    /** Deletes one statement with exact parent block and sibling preconditions. */
    static bool BuildStatementDeletionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringAction& Statement,
        FString& OutPatchJson,
        FString& OutError);

    /** Duplicates one statement into an exact same-block gap with fresh semantic identity. */
    static bool BuildStatementDuplicationPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringAction& SourceStatement,
        const FFinareAuthoringAction& DestinationStatement,
        const FString& NewLocalName,
        FString& OutPatchJson,
        FString& OutError);

    /** Duplicates one consecutive statement range into an exact same-method region gap. */
    static bool BuildStatementRangeDuplicationPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const TArray<FString>& SourceStatementActionIds,
        const FFinareAuthoringAction& DestinationStatement,
        const TMap<FString, FString>& NewLocalNamesByActionId,
        FString& OutPatchJson,
        FString& OutError);

    /** Extracts one consecutive statement range into a typed private Visual method. */
    static bool BuildCallableExtractionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const TArray<FString>& StatementActionIds,
        const FString& MethodName,
        FString& OutPatchJson,
        FString& OutError);

    /** Wraps two or more consecutive statements in one explicit lexical block. */
    static bool BuildStatementCollapsePatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const TArray<FString>& StatementActionIds,
        FString& OutPatchJson,
        FString& OutError);

    /** Expands one nested lexical block back into its parent statement list. */
    static bool BuildBlockExpansionPatch(
        const FFinareAuthoringSurfaceDocument& Document,
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringRegion& BlockRegion,
        FString& OutPatchJson,
        FString& OutError);

    /** Expands the same machine-independent path tokens used by Script Assets. */
    static FString ResolveConfiguredPath(const FString& ConfiguredPath);
};

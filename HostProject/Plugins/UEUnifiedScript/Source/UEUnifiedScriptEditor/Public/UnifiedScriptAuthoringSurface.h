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

struct FUnifiedScriptAuthoringSurfaceDocument
{
    FString SchemaVersion;
    FString SourceCompilationId;
    FString SemanticConfigurationId;
    TArray<FUnifiedScriptAuthoringAsset> Assets;

    int32 GetStateCount() const;
    int32 GetFunctionCount() const;
    int32 GetActionCount() const;
    int32 GetEdgeCount() const;
};

/** Strict reader for the engine-neutral Authoring Surface v0.3 editing contract. */
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

    /** Builds the same semantic-patch v0.22 payload consumed by the shared authoring host. */
    static bool BuildSemanticPatch(
        const FUnifiedScriptAuthoringSurfaceDocument& Document,
        const FUnifiedScriptAuthoringEdit& Edit,
        const FString& NewValue,
        FString& OutPatchJson,
        FString& OutError);

    /** Builds a preconditioned semantic-patch v0.22 data-edge rewire transaction. */
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

    /** Expands the same machine-independent path tokens used by Script Assets. */
    static FString ResolveConfiguredPath(const FString& ConfiguredPath);
};

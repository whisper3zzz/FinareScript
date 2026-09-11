#pragma once

#include "CoreMinimal.h"

class UBlueprint;
class UFinareAsset;

/** Observable result of reconciling Declaration IR with one generated proxy. */
struct FINAREEDITOR_API FFinareBlueprintClassSyncResult
{
    int32 DeclaredStateCount = 0;
    int32 AddedPropertyCount = 0;
    int32 UpdatedPropertyCount = 0;
    int32 RenamedPropertyCount = 0;
    int32 RemovedPropertyCount = 0;
    int32 PreservedDefaultOverrideCount = 0;
    int32 DeclaredFunctionCount = 0;
    int32 AddedFunctionCount = 0;
    int32 UpdatedFunctionCount = 0;
    int32 RemovedFunctionCount = 0;
    int32 DeclaredLifecycleHookCount = 0;
    int32 UpdatedLifecycleHookCount = 0;
    int32 DeclaredInputBindingCount = 0;
    int32 UpdatedInputBindingCount = 0;
    int32 DeclaredDelegateBindingCount = 0;
    int32 UpdatedDelegateBindingCount = 0;
    int32 DeclaredInterfaceBindingCount = 0;
    int32 UpdatedInterfaceBindingCount = 0;
    bool bChanged = false;
};

/** Candidate bytes published together with the UE Blueprint transaction. */
struct FINAREEDITOR_API FFinareExternalPublicationFile
{
    FString Path;
    TArray<uint8> Content;
};

/** Deterministic seams used by editor Automation to exercise parent rollback. */
enum class EFinareBlueprintClassMaterializerFailurePoint : uint8
{
    None,
    AfterParentMutation,
    AfterPackageSave,
};

struct FINAREEDITOR_API FFinareBlueprintClassMaterializerOptions
{
    EFinareBlueprintClassMaterializerFailurePoint FailurePoint =
        EFinareBlueprintClassMaterializerFailurePoint::None;
    /** Authoring operation id used by the shared recovery receipt. */
    FString JournalTransactionId;
    /** Files whose preimages and candidate bytes belong to the same receipt. */
    TArray<FFinareExternalPublicationFile> ExternalPublicationFiles;
    /** Test seam: fail after this many external outputs have been written. */
    int32 FailAfterExternalPublicationWrite = INDEX_NONE;
    /** Overrides a null BaseClassPath with the parent observed by native history. */
    bool bOverrideParentClassPath = false;
    FString ParentClassPathOverride;
};

/**
 * Materializes reviewed managed state and functions as ordinary Blueprint members.
 *
 * Semantic state IDs become Blueprint variable GUIDs, so source renames retain
 * reflection identity. Product metadata distinguishes generated variables from
 * user-owned Blueprint members and records the previous declaration default,
 * allowing later builds to preserve intentional Blueprint default overrides.
 */
class FINAREEDITOR_API FFinareBlueprintClassMaterializer
{
public:
    /** Resolves the configured declaration and exact generated Blueprint. */
    static bool Synchronize(
        UFinareAsset& ScriptAsset,
        bool bValidateOnly,
        FFinareBlueprintClassSyncResult& OutResult,
        FString& OutError);

    /**
     * JSON seam used by editor transactions and Automation fixtures after the
     * exact ScriptAsset/Blueprint relationship has already been established.
     */
    static bool SynchronizeJson(
        UFinareAsset& ScriptAsset,
        UBlueprint& Blueprint,
        const FString& DeclarationJson,
        bool bValidateOnly,
        FFinareBlueprintClassSyncResult& OutResult,
        FString& OutError);

    static bool SynchronizeJson(
        UFinareAsset& ScriptAsset,
        UBlueprint& Blueprint,
        const FString& DeclarationJson,
        bool bValidateOnly,
        FFinareBlueprintClassSyncResult& OutResult,
        FString& OutError,
        const FFinareBlueprintClassMaterializerOptions& Options);
};

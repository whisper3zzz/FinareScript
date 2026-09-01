#pragma once

#include "CoreMinimal.h"

class UBlueprint;
class UUnifiedScriptAsset;

/** Observable result of reconciling Declaration IR with one generated proxy. */
struct UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptBlueprintClassSyncResult
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
    bool bChanged = false;
};

/**
 * Materializes reviewed managed state and functions as ordinary Blueprint members.
 *
 * Semantic state IDs become Blueprint variable GUIDs, so source renames retain
 * reflection identity. Product metadata distinguishes generated variables from
 * user-owned Blueprint members and records the previous declaration default,
 * allowing later builds to preserve intentional Blueprint default overrides.
 */
class UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptBlueprintClassMaterializer
{
public:
    /** Resolves the configured declaration and exact generated Blueprint. */
    static bool Synchronize(
        UUnifiedScriptAsset& ScriptAsset,
        bool bValidateOnly,
        FUnifiedScriptBlueprintClassSyncResult& OutResult,
        FString& OutError);

    /**
     * JSON seam used by editor transactions and Automation fixtures after the
     * exact ScriptAsset/Blueprint relationship has already been established.
     */
    static bool SynchronizeJson(
        UUnifiedScriptAsset& ScriptAsset,
        UBlueprint& Blueprint,
        const FString& DeclarationJson,
        bool bValidateOnly,
        FUnifiedScriptBlueprintClassSyncResult& OutResult,
        FString& OutError);
};

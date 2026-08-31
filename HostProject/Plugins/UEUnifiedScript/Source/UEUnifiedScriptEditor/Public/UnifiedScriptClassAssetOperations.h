#pragma once

#include "CoreMinimal.h"

class UBlueprint;
class UUnifiedScriptAsset;

/** Deterministic failure seams used by headless transaction rollback coverage. */
enum class EUnifiedScriptClassAssetOperationFailurePoint : uint8
{
    None,
    AfterAssetMutation,
    AfterExternalPublication,
};

struct FUnifiedScriptClassAssetOperationResult
{
    FString Operation;
    FString TransactionId;
    FString Error;
    FString BlueprintAssetPath;
    FString ScriptAssetPath;
    FString SourcePath;
    FString ManagedTypeName;
    bool bSucceeded = false;
    bool bRolledBack = false;
    UBlueprint* Blueprint = nullptr;
    UUnifiedScriptAsset* ScriptAsset = nullptr;
};

/**
 * Applies identity-sensitive Script Class operations across the UE proxy,
 * Script Asset, managed source, deployment manifest, and first-Play receipt.
 */
class UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptClassAssetOperations
{
public:
    static bool Rename(
        UUnifiedScriptAsset* ScriptAsset,
        const FString& TargetBlueprintPackagePath,
        FUnifiedScriptClassAssetOperationResult& OutResult,
        EUnifiedScriptClassAssetOperationFailurePoint FailurePoint =
            EUnifiedScriptClassAssetOperationFailurePoint::None);

    static bool Duplicate(
        UUnifiedScriptAsset* ScriptAsset,
        const FString& TargetBlueprintPackagePath,
        FUnifiedScriptClassAssetOperationResult& OutResult,
        EUnifiedScriptClassAssetOperationFailurePoint FailurePoint =
            EUnifiedScriptClassAssetOperationFailurePoint::None);

    /** True only while the complete-class transaction invokes AssetTools. */
    static bool IsAssetToolsMutationActive();
};

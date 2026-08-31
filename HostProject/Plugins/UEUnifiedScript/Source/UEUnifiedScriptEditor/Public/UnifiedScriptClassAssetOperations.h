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
    FString RecoveryReceiptPath;
    bool bSucceeded = false;
    bool bRolledBack = false;
    UBlueprint* Blueprint = nullptr;
    UUnifiedScriptAsset* ScriptAsset = nullptr;
};

struct FUnifiedScriptClassAssetRecoveryResult
{
    int32 PendingReceiptCount = 0;
    int32 RecoveredReceiptCount = 0;
    int32 CleanedCommittedReceiptCount = 0;
    int32 DeferredLiveReceiptCount = 0;
    TArray<FString> RecoveredTransactionIds;
    TArray<FString> ArchivePaths;
    FString Error;
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

    static bool Delete(
        UUnifiedScriptAsset* ScriptAsset,
        FUnifiedScriptClassAssetOperationResult& OutResult,
        EUnifiedScriptClassAssetOperationFailurePoint FailurePoint =
            EUnifiedScriptClassAssetOperationFailurePoint::None);

    /** Restores interrupted transactions before their assets are loaded. */
    static bool RecoverPendingTransactions(
        FUnifiedScriptClassAssetRecoveryResult& OutResult);

    /** True for either half of a persistent Script Class asset pair. */
    static bool IsCompleteClassAsset(const UObject* Asset);

    /** True only while the complete-class transaction invokes AssetTools. */
    static bool IsAssetToolsMutationActive();
};

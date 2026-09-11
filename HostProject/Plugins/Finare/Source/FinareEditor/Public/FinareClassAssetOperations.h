#pragma once

#include "CoreMinimal.h"

class UBlueprint;
class UFinareAsset;

/** Deterministic failure seams used by headless transaction rollback coverage. */
enum class EFinareClassAssetOperationFailurePoint : uint8
{
	None,
	AfterAssetMutation,
	AfterExternalPublication,
};

struct FFinareClassAssetOperationResult
{
	FString Operation;
	FString TransactionId;
	FString Error;
	FString BlueprintAssetPath;
	FString ScriptAssetPath;
	FString SourcePath;
	FString ManagedTypeName;
	FString RecoveryReceiptPath;
	/** UE packages whose references were rewritten during a successful rename. */
	TArray<FString> RepairedUEReferencerPackages;
	bool bSucceeded = false;
	bool bRolledBack = false;
	UBlueprint* Blueprint = nullptr;
	UFinareAsset* ScriptAsset = nullptr;
};

struct FFinareClassAssetRecoveryResult
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
class FINAREEDITOR_API FFinareClassAssetOperations
{
public:
	static bool Rename(
		UFinareAsset* ScriptAsset,
		const FString& TargetBlueprintPackagePath,
		FFinareClassAssetOperationResult& OutResult,
		EFinareClassAssetOperationFailurePoint FailurePoint =
			EFinareClassAssetOperationFailurePoint::None);

	static bool Duplicate(
		UFinareAsset* ScriptAsset,
		const FString& TargetBlueprintPackagePath,
		FFinareClassAssetOperationResult& OutResult,
		EFinareClassAssetOperationFailurePoint FailurePoint =
			EFinareClassAssetOperationFailurePoint::None);

	static bool Delete(
		UFinareAsset* ScriptAsset,
		FFinareClassAssetOperationResult& OutResult,
		EFinareClassAssetOperationFailurePoint FailurePoint =
			EFinareClassAssetOperationFailurePoint::None);

	/** Restores interrupted transactions before their assets are loaded. */
	static bool RecoverPendingTransactions(
		FFinareClassAssetRecoveryResult& OutResult);

	/** True for either half of a persistent Script Class asset pair. */
	static bool IsCompleteClassAsset(const UObject* Asset);

	/** True only while the complete-class transaction invokes AssetTools. */
	static bool IsAssetToolsMutationActive();

	/** Detects a generated Blueprint copy that still points at another class's sidecar. */
	static bool IsOrphanedGeneratedBlueprintDuplicate(const UBlueprint* Blueprint);

	/** Removes one detected Blueprint-only copy under the complete-class guard. */
	static bool DeleteOrphanedGeneratedBlueprintDuplicate(
		UBlueprint* Blueprint,
		FString& OutError);
};

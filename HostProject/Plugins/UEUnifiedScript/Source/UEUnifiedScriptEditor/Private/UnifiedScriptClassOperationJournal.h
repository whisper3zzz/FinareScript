#pragma once

#include "CoreMinimal.h"

namespace UE::UnifiedScript::ClassOperationJournal
{
enum class EPhase : uint8
{
    Prepared,
    AssetsMutated,
    ExternalPublished,
    Committed,
};

struct FSnapshot
{
    FString Path;
    bool bExisted = false;
    TArray<uint8> Bytes;
};

struct FReceipt
{
    FString Operation;
    FString TransactionId;
    FString ReceiptPath;
    FString CreatedUtc;
    FString UpdatedUtc;
    uint32 OwnerProcessId = 0;
    EPhase Phase = EPhase::Prepared;
    TArray<FSnapshot> Snapshots;
};

struct FRecoveryReport
{
    int32 PendingReceiptCount = 0;
    int32 RecoveredReceiptCount = 0;
    int32 CleanedCommittedReceiptCount = 0;
    int32 DeferredLiveReceiptCount = 0;
    TArray<FString> RecoveredTransactionIds;
    TArray<FString> ArchivePaths;
};

FString GetPendingRoot();
FString GetRecoveredRoot();

bool CaptureSnapshot(
    const FString& Path,
    FSnapshot& OutSnapshot,
    FString& OutError);

bool Begin(
    const FString& Operation,
    const FString& TransactionId,
    const TArray<FSnapshot>& Snapshots,
    FReceipt& OutReceipt,
    FString& OutError);

bool Advance(FReceipt& Receipt, EPhase Phase, FString& OutError);

/** Restores only this receipt's captured files and leaves it pending. */
bool RestoreCapturedFiles(const FReceipt& Receipt, FString& OutError);

/** Marks the transaction terminal and removes its pending receipt when possible. */
bool Close(FReceipt& Receipt, FString& OutError);

/** Restores every non-committed receipt and archives the interrupted current bytes. */
bool RecoverPending(
    FRecoveryReport& OutReport,
    FString& OutError,
    bool bIncludeCurrentProcessForTesting = false);
}

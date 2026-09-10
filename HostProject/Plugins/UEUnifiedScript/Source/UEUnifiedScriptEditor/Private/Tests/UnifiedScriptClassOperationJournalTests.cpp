#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptClassOperationJournal.h"

#include "HAL/FileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"

namespace UE::UnifiedScript::ClassOperationJournal::Tests
{
bool ReadBytes(const FString& Path, TArray<uint8>& OutBytes)
{
    OutBytes.Reset();
    return FFileHelper::LoadFileToArray(OutBytes, *Path);
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptClassOperationJournalTest,
    "UnifiedScript.Unreal.ScriptClass.OperationJournal",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptClassOperationJournalTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::ClassOperationJournal;
    using namespace UE::UnifiedScript::ClassOperationJournal::Tests;
    const FString ProbeRoot = FPaths::Combine(
        FPaths::ProjectSavedDir(),
        TEXT("UnifiedScript/ClassOperationJournalFocusedTests"),
        FGuid::NewGuid().ToString(EGuidFormats::Digits));
    const FString ExistingPath = ProbeRoot / TEXT("existing.bin");
    const FString OtherPath = ProbeRoot / TEXT("other.bin");
    const FString ReceiptBlocker = ProbeRoot / TEXT("receipt-blocker");
    FString FirstReceiptPath;
    FString SecondReceiptPath;
    IFileManager::Get().MakeDirectory(*ProbeRoot, true);
    ON_SCOPE_EXIT
    {
        if (!FirstReceiptPath.IsEmpty())
        {
            IFileManager::Get().Delete(*FirstReceiptPath, false, true);
        }
        if (!SecondReceiptPath.IsEmpty())
        {
            IFileManager::Get().Delete(*SecondReceiptPath, false, true);
        }
        IFileManager::Get().DeleteDirectory(*ProbeRoot, false, true);
    };

    const TArray<uint8> ExistingBefore = {0x01, 0x02, 0x03};
    const TArray<uint8> OtherBefore = {0x0a, 0x0b, 0x0c};
    const TArray<uint8> Interrupted = {0xde, 0xad, 0xbe, 0xef};
    if (!TestTrue(
            TEXT("Writes journal test preimages"),
            FFileHelper::SaveArrayToFile(ExistingBefore, *ExistingPath) &&
                FFileHelper::SaveArrayToFile(OtherBefore, *OtherPath)))
    {
        return false;
    }

    FSnapshot ExistingSnapshot;
    FSnapshot OtherSnapshot;
    FString Error;
    if (!TestTrue(
            TEXT("Captures the first receipt preimage"),
            CaptureSnapshot(ExistingPath, ExistingSnapshot, Error)) ||
        !TestTrue(
            TEXT("Captures the second receipt preimage"),
            CaptureSnapshot(OtherPath, OtherSnapshot, Error)))
    {
        return false;
    }

    const FString TransactionIdA = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    const FString TransactionIdB = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    TArray<FSnapshot> FirstSnapshots;
    TArray<FSnapshot> SecondSnapshots;
    FirstSnapshots.Add(ExistingSnapshot);
    SecondSnapshots.Add(OtherSnapshot);
    FReceipt FirstReceipt;
    FReceipt SecondReceipt;
    if (!TestTrue(
            TEXT("Creates the first focused journal receipt"),
            Begin(TEXT("create"), TransactionIdA, FirstSnapshots, FirstReceipt, Error)))
    {
        return false;
    }
    FirstReceiptPath = FirstReceipt.ReceiptPath;

    if (!TestTrue(
            TEXT("Creates the second focused journal receipt"),
            Begin(TEXT("create"), TransactionIdB, SecondSnapshots, SecondReceipt, Error)))
    {
        return false;
    }
    SecondReceiptPath = SecondReceipt.ReceiptPath;

    IFileManager::Get().MakeDirectory(*ReceiptBlocker, true);
    FirstReceipt.ReceiptPath = ReceiptBlocker;
    const EPhase FirstPhaseBeforeWriteFailure = FirstReceipt.Phase;
    const bool bAdvanceWriteFailed = !Advance(
        FirstReceipt,
        EPhase::AssetsMutated,
        Error);
    FirstReceipt.ReceiptPath = FirstReceiptPath;
    TestTrue(TEXT("Receipt phase write failure is reported"), bAdvanceWriteFailed);
    TestTrue(
        TEXT("Receipt phase remains unchanged when Advance cannot publish"),
        FirstReceipt.Phase == FirstPhaseBeforeWriteFailure);
    FString PersistedFirstReceipt;
    TestTrue(
        TEXT("Failed Advance leaves the original prepared receipt"),
        FFileHelper::LoadFileToString(PersistedFirstReceipt, *FirstReceiptPath) &&
            PersistedFirstReceipt.Contains(TEXT("prepared")));

    if (!TestTrue(
            TEXT("Advances the first receipt after the failed write is cleared"),
            Advance(FirstReceipt, EPhase::AssetsMutated, Error)) ||
        !TestTrue(
            TEXT("Mutates both receipt-owned files"),
            FFileHelper::SaveArrayToFile(Interrupted, *ExistingPath) &&
                FFileHelper::SaveArrayToFile(Interrupted, *OtherPath)))
    {
        return false;
    }

    if (!TestTrue(
            TEXT("Restores only the selected receipt's captured file"),
            RestoreCapturedFiles(FirstReceipt, Error)))
    {
        return false;
    }
    TArray<uint8> RestoredExisting;
    TArray<uint8> UnchangedOther;
    TestTrue(
        TEXT("Selected receipt restores its exact preimage"),
        ReadBytes(ExistingPath, RestoredExisting) && RestoredExisting == ExistingBefore);
    TestTrue(
        TEXT("Selected receipt restore does not touch another receipt"),
        ReadBytes(OtherPath, UnchangedOther) && UnchangedOther == Interrupted);
    TestTrue(
        TEXT("Targeted restore leaves both receipts pending"),
        IFileManager::Get().FileExists(*FirstReceiptPath) &&
            IFileManager::Get().FileExists(*SecondReceiptPath));

    IFileManager::Get().Delete(*ExistingPath, false, true);
    IFileManager::Get().MakeDirectory(*ExistingPath, true);
    const bool bRestoreFailed = !RestoreCapturedFiles(FirstReceipt, Error);
    TestTrue(TEXT("Targeted restore reports a real file restore failure"), bRestoreFailed);
    TestTrue(
        TEXT("Failed targeted restore keeps its receipt pending"),
        IFileManager::Get().FileExists(*FirstReceiptPath));
    IFileManager::Get().DeleteDirectory(*ExistingPath, false, true);
    TestTrue(
        TEXT("Failed targeted restore can be retried"),
        RestoreCapturedFiles(FirstReceipt, Error));

    SecondReceipt.ReceiptPath = ReceiptBlocker;
    const EPhase SecondPhaseBeforeCloseFailure = SecondReceipt.Phase;
    const bool bCloseWriteFailed = !Close(SecondReceipt, Error);
    SecondReceipt.ReceiptPath = SecondReceiptPath;
    TestTrue(TEXT("Receipt close write failure is reported"), bCloseWriteFailed);
    TestTrue(
        TEXT("Receipt phase remains unchanged when Close cannot publish"),
        SecondReceipt.Phase == SecondPhaseBeforeCloseFailure);
    TestTrue(
        TEXT("Close write failure leaves the second receipt pending"),
        IFileManager::Get().FileExists(*SecondReceiptPath));

    const bool bClosedFirst = Close(FirstReceipt, Error);
    const bool bClosedSecond = Close(SecondReceipt, Error);
    TestTrue(TEXT("Closes the first receipt after targeted recovery"), bClosedFirst);
    TestTrue(TEXT("Retries and closes the second receipt"), bClosedSecond);
    return true;
}

#endif

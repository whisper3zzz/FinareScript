#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptClassAssetOperations.h"
#include "UnifiedScriptClassOperationJournal.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "AssetToolsModule.h"
#include "Engine/Blueprint.h"
#include "Editor.h"
#include "HAL/FileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptAssetDefinition.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptClassFactory.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptProjectSetup.h"
#include "UObject/SavePackage.h"

namespace UE::UnifiedScript::ClassAssetOperations::Tests
{
FString ResolveConfiguredPath(const FString& Value)
{
    FString Result = Value;
    Result.ReplaceInline(
        TEXT("{ProjectDir}"),
        *FPaths::ConvertRelativePathToFull(FPaths::ProjectDir()),
        ESearchCase::IgnoreCase);
    Result = FPaths::ConvertRelativePathToFull(Result);
    FPaths::NormalizeFilename(Result);
    return Result;
}

UUnifiedScriptAsset* GetScriptAsset(UBlueprint* Blueprint)
{
    if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
    {
        return nullptr;
    }
    if (const AUnifiedScriptActor* Defaults = Cast<AUnifiedScriptActor>(
            Blueprint->GeneratedClass->GetDefaultObject()))
    {
        return Defaults->ScriptComponent != nullptr
            ? Defaults->ScriptComponent->ScriptAsset.Get()
            : nullptr;
    }
    if (const UUnifiedScriptComponent* Defaults = Cast<UUnifiedScriptComponent>(
            Blueprint->GeneratedClass->GetDefaultObject()))
    {
        return Defaults->ScriptAsset.Get();
    }
    return nullptr;
}

struct FScopedOperationCleanup
{
    FString DeploymentPath;
    TArray<uint8> DeploymentBytes;
    FString FirstPlayRequestPath;
    bool bHadFirstPlayRequest = false;
    TArray<uint8> FirstPlayRequestBytes;
    TArray<FString> SourcePaths;
    TArray<FString> PackageNames;
    TArray<TWeakObjectPtr<UObject>> Assets;

    ~FScopedOperationCleanup()
    {
        for (const FString& SourcePath : SourcePaths)
        {
            IFileManager::Get().Delete(*SourcePath, false, true);
        }
        for (const FString& PackageName : PackageNames)
        {
            const FString Filename = FPackageName::LongPackageNameToFilename(
                PackageName,
                FPackageName::GetAssetPackageExtension());
            IFileManager::Get().Delete(*Filename, false, true);
        }
        if (!DeploymentPath.IsEmpty())
        {
            FFileHelper::SaveArrayToFile(DeploymentBytes, *DeploymentPath);
        }
        if (!FirstPlayRequestPath.IsEmpty())
        {
            if (bHadFirstPlayRequest)
            {
                FFileHelper::SaveArrayToFile(
                    FirstPlayRequestBytes,
                    *FirstPlayRequestPath);
            }
            else
            {
                IFileManager::Get().Delete(*FirstPlayRequestPath, false, true);
            }
        }
    }
};

bool ReadBytes(const FString& Path, TArray<uint8>& OutBytes)
{
    OutBytes.Reset();
    return FFileHelper::LoadFileToArray(OutBytes, *Path);
}

bool RunJournalRecoveryProbe(FAutomationTestBase& Test)
{
    using namespace UE::UnifiedScript::ClassOperationJournal;
    const FString TransactionId =
        FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    const FString ProbeRoot = FPaths::Combine(
        FPaths::ProjectSavedDir(),
        TEXT("UnifiedScript/ClassOperationJournalTests"),
        TransactionId);
    const FString ExistingPath = ProbeRoot / TEXT("existing.bin");
    const FString CreatedPath = ProbeRoot / TEXT("created.bin");
    IFileManager::Get().MakeDirectory(*ProbeRoot, true);
    const TArray<uint8> Before = {0x00, 0x13, 0x37, 0xff};
    const TArray<uint8> Interrupted = {0xde, 0xad, 0xbe, 0xef};
    if (!Test.TestTrue(
            TEXT("Writes the journal recovery preimage"),
            FFileHelper::SaveArrayToFile(Before, *ExistingPath)))
    {
        IFileManager::Get().DeleteDirectory(*ProbeRoot, false, true);
        return false;
    }

    TArray<FSnapshot> Snapshots;
    FString Error;
    FSnapshot ExistingSnapshot;
    FSnapshot CreatedSnapshot;
    if (!Test.TestTrue(
            TEXT("Captures an existing journal preimage"),
            CaptureSnapshot(ExistingPath, ExistingSnapshot, Error)) ||
        !Test.TestTrue(
            TEXT("Captures a missing journal preimage"),
            CaptureSnapshot(CreatedPath, CreatedSnapshot, Error)))
    {
        IFileManager::Get().DeleteDirectory(*ProbeRoot, false, true);
        return false;
    }
    Snapshots.Add(MoveTemp(ExistingSnapshot));
    Snapshots.Add(MoveTemp(CreatedSnapshot));

    FReceipt Receipt;
    if (!Test.TestTrue(
            *FString::Printf(TEXT("Persists a prepared recovery receipt: %s"), *Error),
            Begin(TEXT("rename"), TransactionId, Snapshots, Receipt, Error)) ||
        !Test.TestTrue(
            TEXT("Advances the recovery receipt after asset mutation"),
            Advance(Receipt, EPhase::AssetsMutated, Error)) ||
        !Test.TestTrue(
            TEXT("Writes an interrupted existing output"),
            FFileHelper::SaveArrayToFile(Interrupted, *ExistingPath)) ||
        !Test.TestTrue(
            TEXT("Writes an interrupted newly-created output"),
            FFileHelper::SaveArrayToFile(Interrupted, *CreatedPath)) ||
        !Test.TestTrue(
            TEXT("Advances the recovery receipt after external publication"),
            Advance(Receipt, EPhase::ExternalPublished, Error)))
    {
        IFileManager::Get().Delete(*Receipt.ReceiptPath, false, true);
        IFileManager::Get().DeleteDirectory(*ProbeRoot, false, true);
        return false;
    }

    FRecoveryReport LiveOwnerReport;
    if (!Test.TestTrue(
            TEXT("Recovery scan accepts a live-owner receipt"),
            RecoverPending(LiveOwnerReport, Error)) ||
        !Test.TestEqual(
            TEXT("Recovery defers a receipt owned by the live Editor process"),
            LiveOwnerReport.DeferredLiveReceiptCount,
            1) ||
        !Test.TestTrue(
            TEXT("Deferred live-owner receipt remains pending"),
            IFileManager::Get().FileExists(*Receipt.ReceiptPath)))
    {
        IFileManager::Get().Delete(*Receipt.ReceiptPath, false, true);
        IFileManager::Get().DeleteDirectory(*ProbeRoot, false, true);
        return false;
    }

    FRecoveryReport Report;
    const bool bRecovered = RecoverPending(Report, Error, true);
    TArray<uint8> Restored;
    const bool bReadRestored = ReadBytes(ExistingPath, Restored);
    const FString* ArchivePath = Report.ArchivePaths.FindByPredicate(
        [&TransactionId](const FString& Path)
        {
            return FPaths::GetCleanFilename(Path) == TransactionId;
        });
    const bool bPassed =
        Test.TestTrue(
            *FString::Printf(TEXT("Recovers the pending journal: %s"), *Error),
            bRecovered) &&
        Test.TestTrue(
            TEXT("Reports the recovered transaction identity"),
            Report.RecoveredTransactionIds.Contains(TransactionId)) &&
        Test.TestTrue(
            TEXT("Reads the byte-exact recovered preimage"),
            bReadRestored) &&
        Test.TestEqual(
            TEXT("Recovery restores the existing preimage exactly"),
            Restored,
            Before) &&
        Test.TestFalse(
            TEXT("Recovery removes the interrupted newly-created output"),
            IFileManager::Get().FileExists(*CreatedPath)) &&
        Test.TestFalse(
            TEXT("Recovery closes the pending receipt"),
            IFileManager::Get().FileExists(*Receipt.ReceiptPath)) &&
        Test.TestNotNull(
            TEXT("Recovery reports its archive"),
            ArchivePath) &&
        Test.TestTrue(
            TEXT("Recovery archives the interrupted receipt"),
            ArchivePath != nullptr &&
                IFileManager::Get().FileExists(
                    *(*ArchivePath / TEXT("receipt.json")))) &&
        Test.TestTrue(
            TEXT("Recovery records preserved interrupted bytes"),
            ArchivePath != nullptr &&
                IFileManager::Get().FileExists(
                    *(*ArchivePath / TEXT("current-0000.bin"))));

    IFileManager::Get().DeleteDirectory(*ProbeRoot, false, true);
    if (ArchivePath != nullptr)
    {
        IFileManager::Get().DeleteDirectory(**ArchivePath, false, true);
    }
    return bPassed;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptClassAssetOperationsTest,
    "UnifiedScript.Unreal.ScriptClass.AssetOperations",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptClassAssetOperationsTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::ClassAssetOperations::Tests;
    if (!RunJournalRecoveryProbe(*this))
    {
        return false;
    }
    FScopedOperationCleanup Cleanup;
    Cleanup.DeploymentPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectDir(),
        TEXT("../samples/Unreal/UnifiedScript.Unreal.Demo.deployment.json")));
    if (!TestTrue(
            TEXT("Snapshots deployment before class operations"),
            ReadBytes(Cleanup.DeploymentPath, Cleanup.DeploymentBytes)))
    {
        return false;
    }
    Cleanup.FirstPlayRequestPath = FUnifiedScriptProjectSetup::GetFirstPlayRequestPath(
        FPaths::ProjectDir());
    Cleanup.bHadFirstPlayRequest = IFileManager::Get().FileExists(
        *Cleanup.FirstPlayRequestPath);
    if (Cleanup.bHadFirstPlayRequest &&
        !ReadBytes(Cleanup.FirstPlayRequestPath, Cleanup.FirstPlayRequestBytes))
    {
        AddError(TEXT("Cannot snapshot the existing first-Play request."));
        return false;
    }

    const FString Suffix = FGuid::NewGuid()
        .ToString(EGuidFormats::Digits)
        .Left(10);
    const FString OriginalName = TEXT("M13Ops_") + Suffix;
    const FString RenamedName = OriginalName + TEXT("_Renamed");
    const FString DuplicateName = OriginalName + TEXT("_Copy");
    const FString RenameFailureName = OriginalName + TEXT("_RenameRollback");
    const FString DuplicateFailureName = OriginalName + TEXT("_DuplicateRollback");
    const FString BareDuplicateName = OriginalName + TEXT("_BareBlueprintCopy");
    const FString DirtyReferencerRenameName =
        OriginalName + TEXT("_DirtyReferencerRename");
    const FString ReferencerName = OriginalName + TEXT("_UEReferencer");
    const FString PackageRoot = TEXT("/Game/UnifiedScriptAutomation");
    const FString OriginalPackage = PackageRoot / OriginalName;
    const FString OriginalSidecar = PackageRoot / (TEXT("SA_") + OriginalName);
    const FString RenamedPackage = PackageRoot / RenamedName;
    const FString RenamedSidecar = PackageRoot / (TEXT("SA_") + RenamedName);
    const FString DuplicatePackage = PackageRoot / DuplicateName;
    const FString DuplicateSidecar = PackageRoot / (TEXT("SA_") + DuplicateName);
    const FString RenameFailurePackage = PackageRoot / RenameFailureName;
    const FString RenameFailureSidecar =
        PackageRoot / (TEXT("SA_") + RenameFailureName);
    const FString DuplicateFailurePackage = PackageRoot / DuplicateFailureName;
    const FString DuplicateFailureSidecar =
        PackageRoot / (TEXT("SA_") + DuplicateFailureName);
    const FString BareDuplicatePackage = PackageRoot / BareDuplicateName;
    const FString DirtyReferencerRenamePackage =
        PackageRoot / DirtyReferencerRenameName;
    const FString DirtyReferencerRenameSidecar =
        PackageRoot / (TEXT("SA_") + DirtyReferencerRenameName);
    const FString ReferencerPackage = PackageRoot / ReferencerName;
    Cleanup.PackageNames = {
        OriginalPackage,
        OriginalSidecar,
        RenamedPackage,
        RenamedSidecar,
        DuplicatePackage,
        DuplicateSidecar,
        RenameFailurePackage,
        RenameFailureSidecar,
        DuplicateFailurePackage,
        DuplicateFailureSidecar,
        BareDuplicatePackage,
        DirtyReferencerRenamePackage,
        DirtyReferencerRenameSidecar,
        ReferencerPackage,
    };

    UPackage* Package = CreatePackage(*OriginalPackage);
    UUnifiedScriptActorClassFactory* Factory =
        NewObject<UUnifiedScriptActorClassFactory>();
    UBlueprint* Blueprint = Cast<UBlueprint>(Factory->FactoryCreateNew(
        UBlueprint::StaticClass(),
        Package,
        *OriginalName,
        RF_Public | RF_Standalone | RF_Transactional,
        nullptr,
        GWarn,
        TEXT("UnifiedScriptClassAssetOperationsAutomation")));
    UUnifiedScriptAsset* ScriptAsset = GetScriptAsset(Blueprint);
    if (!TestNotNull(TEXT("Creates the source Script Class proxy"), Blueprint) ||
        !TestNotNull(TEXT("Creates the source Script Class sidecar"), ScriptAsset))
    {
        return false;
    }
    TestTrue(
        TEXT("Factory creates the product-owned generated Blueprint type"),
        Blueprint->IsA<UUnifiedScriptBlueprint>());
    Cleanup.Assets.Add(Blueprint);
    Cleanup.Assets.Add(ScriptAsset);
    Cleanup.SourcePaths.Add(ResolveConfiguredPath(ScriptAsset->SourcePath));
    FSavePackageArgs SaveArguments;
    SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
    const FString OriginalBlueprintFilename = FPackageName::LongPackageNameToFilename(
        OriginalPackage,
        FPackageName::GetAssetPackageExtension());
    const FString OriginalSidecarFilename = FPackageName::LongPackageNameToFilename(
        OriginalSidecar,
        FPackageName::GetAssetPackageExtension());
    if (!TestTrue(
            TEXT("Saves the source Blueprint before asset operations"),
            UPackage::SavePackage(
                Blueprint->GetOutermost(),
                Blueprint,
                *OriginalBlueprintFilename,
                SaveArguments)) ||
        !TestTrue(
            TEXT("Saves the source sidecar before asset operations"),
            UPackage::SavePackage(
                ScriptAsset->GetOutermost(),
                ScriptAsset,
                *OriginalSidecarFilename,
                SaveArguments)))
    {
        return false;
    }

    UPackage* ReferencerOuter = CreatePackage(*ReferencerPackage);
    UUnifiedScriptAsset* UEReferencer = NewObject<UUnifiedScriptAsset>(
        ReferencerOuter,
        *ReferencerName,
        RF_Public | RF_Standalone | RF_Transactional);
    UEReferencer->GeneratedClassPath = FSoftClassPath(
        Blueprint->GeneratedClass->GetPathName());
    UEReferencer->MarkPackageDirty();
    FAssetRegistryModule::AssetCreated(UEReferencer);
    Cleanup.Assets.Add(UEReferencer);
    const FString ReferencerFilename = FPackageName::LongPackageNameToFilename(
        ReferencerPackage,
        FPackageName::GetAssetPackageExtension());
    if (!TestTrue(
            TEXT("Saves a real UE package that references the generated class"),
            UPackage::SavePackage(
                ReferencerOuter,
                UEReferencer,
                *ReferencerFilename,
                SaveArguments)))
    {
        return false;
    }
    IAssetRegistry& AssetRegistry =
        FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
    AssetRegistry.ScanModifiedAssetFiles({ReferencerFilename});
    TArray<FName> InitialUEReferencers;
    AssetRegistry.GetReferencers(
        FName(*OriginalPackage),
        InitialUEReferencers,
        UE::AssetRegistry::EDependencyCategory::Package);
    TestTrue(
        TEXT("Asset Registry discovers the saved external UE referencer"),
        InitialUEReferencers.Contains(FName(*ReferencerPackage)));
    UEReferencer->MarkPackageDirty();
    FUnifiedScriptClassAssetOperationResult DirtyReferencerRenameResult;
    TestFalse(
        TEXT("Rename rejects an external UE referencer with unsaved changes"),
        FUnifiedScriptClassAssetOperations::Rename(
            ScriptAsset,
            DirtyReferencerRenamePackage,
            DirtyReferencerRenameResult));
    TestTrue(
        TEXT("Dirty referencer rejection gives an actionable save boundary"),
        DirtyReferencerRenameResult.Error.Contains(TEXT("has unsaved changes")));
    UEReferencer->GetOutermost()->SetDirtyFlag(false);

    const FGuid OriginalClassId = ScriptAsset->ScriptClassId;
    FString OriginalSource;
    TestTrue(
        TEXT("Reads the original managed source"),
        FFileHelper::LoadFileToString(
            OriginalSource,
            *ResolveConfiguredPath(ScriptAsset->SourcePath)));
    const FString RenameReferencePath = FPaths::Combine(
        FPaths::GetPath(ResolveConfiguredPath(ScriptAsset->SourcePath)),
        OriginalName + TEXT("_RenameReference.cs"));
    Cleanup.SourcePaths.Add(RenameReferencePath);
    const FString RenameReferenceSource = FString::Printf(
        TEXT("namespace UnifiedScript.Unreal.Demo; sealed class RenameReference { %s? Value; const string Text = \"%s\"; }\n"),
        *OriginalName,
        *OriginalName);
    TestTrue(
        TEXT("Writes a semantic cross-file rename reference"),
        FFileHelper::SaveStringToFile(
            RenameReferenceSource,
            *RenameReferencePath));

    FUnifiedScriptClassAssetOperationResult RenameResult;
    if (!TestTrue(
            *FString::Printf(TEXT("Renames the complete Script Class: %s"), *RenameResult.Error),
            FUnifiedScriptClassAssetOperations::Rename(
                ScriptAsset,
                RenamedPackage,
                RenameResult)))
    {
        return false;
    }
    Blueprint = RenameResult.Blueprint;
    ScriptAsset = RenameResult.ScriptAsset;
    Cleanup.SourcePaths.Add(RenameResult.SourcePath);
    TestTrue(TEXT("Rename transaction reports success"), RenameResult.bSucceeded);
    TestTrue(
        TEXT("Rename reports the repaired external UE package"),
        RenameResult.RepairedUEReferencerPackages.Contains(ReferencerPackage));
    TestFalse(
        TEXT("Rename closes its committed recovery receipt"),
        IFileManager::Get().FileExists(*RenameResult.RecoveryReceiptPath));
    TestEqual(TEXT("Rename preserves stable ClassId"), ScriptAsset->ScriptClassId, OriginalClassId);
    TestEqual(
        TEXT("Rename updates managed type identity"),
        ScriptAsset->ScriptTypeName,
        FString(TEXT("UnifiedScript.Unreal.Demo.")) + RenamedName);
    TestEqual(
        TEXT("Rename updates generated class identity"),
        ScriptAsset->GeneratedClassPath.ToString(),
        RenamedPackage + TEXT(".") + RenamedName + TEXT("_C"));
    TestEqual(
        TEXT("Rename rewrites the UE soft class reference"),
        UEReferencer->GeneratedClassPath.ToString(),
        RenamedPackage + TEXT(".") + RenamedName + TEXT("_C"));
    TestFalse(
        TEXT("Rename removes the old Blueprint redirector package"),
        IFileManager::Get().FileExists(*OriginalBlueprintFilename));
    TestFalse(
        TEXT("Rename removes the old Script Asset redirector package"),
        IFileManager::Get().FileExists(*OriginalSidecarFilename));
    TestFalse(
        TEXT("Rename removes the old source file"),
        IFileManager::Get().FileExists(*Cleanup.SourcePaths[0]));
    FString RenamedSource;
    TestTrue(
        TEXT("Rename publishes the new source file"),
        FFileHelper::LoadFileToString(RenamedSource, *RenameResult.SourcePath));
    TestTrue(
        TEXT("Rename preserves authored behavior"),
        RenamedSource.Contains(TEXT("return value + 1;")));
    TestTrue(
        TEXT("Rename updates the exact class declaration"),
        RenamedSource.Contains(TEXT("class ") + RenamedName + TEXT(" ")));
    TestFalse(
        TEXT("Rename removes the old class declaration"),
        RenamedSource.Contains(TEXT("class ") + OriginalName + TEXT(" ")));
    FString RenamedReferenceSource;
    TestTrue(
        TEXT("Rename publishes the cross-file semantic reference"),
        FFileHelper::LoadFileToString(
            RenamedReferenceSource,
            *RenameReferencePath));
    TestTrue(
        TEXT("Rename updates the Roslyn-bound external type reference"),
        RenamedReferenceSource.Contains(RenamedName + TEXT("? Value")));
    TestTrue(
        TEXT("Rename preserves unrelated string text with the old identifier"),
        RenamedReferenceSource.Contains(TEXT("\"") + OriginalName + TEXT("\"")));

    FUnifiedScriptFirstPlayRequest RenameRequest;
    FString RequestError;
    TestTrue(
        TEXT("Rename publishes a new first-Play request"),
        FUnifiedScriptProjectSetup::ReadFirstPlayRequest(
            Cleanup.FirstPlayRequestPath,
            RenameRequest,
            RequestError));
    TestEqual(
        TEXT("Rename request points to the renamed Blueprint"),
        RenameRequest.BlueprintAssetPath,
        RenamedPackage + TEXT(".") + RenamedName);

    TArray<uint8> RenamedSourceBeforeFailure;
    TArray<uint8> DeploymentBeforeRenameFailure;
    TArray<uint8> RequestBeforeRenameFailure;
    TArray<uint8> ReferenceBeforeRenameFailure;
    TestTrue(
        TEXT("Snapshots renamed source before rename rollback coverage"),
        ReadBytes(RenameResult.SourcePath, RenamedSourceBeforeFailure));
    TestTrue(
        TEXT("Snapshots deployment before rename rollback coverage"),
        ReadBytes(Cleanup.DeploymentPath, DeploymentBeforeRenameFailure));
    TestTrue(
        TEXT("Snapshots request before rename rollback coverage"),
        ReadBytes(Cleanup.FirstPlayRequestPath, RequestBeforeRenameFailure));
    TestTrue(
        TEXT("Snapshots semantic reference before rename rollback coverage"),
        ReadBytes(RenameReferencePath, ReferenceBeforeRenameFailure));
    FUnifiedScriptClassAssetOperationResult RenameFailureResult;
    TestFalse(
        TEXT("Injected post-publication failure rejects the rename"),
        FUnifiedScriptClassAssetOperations::Rename(
            ScriptAsset,
            RenameFailurePackage,
            RenameFailureResult,
            EUnifiedScriptClassAssetOperationFailurePoint::AfterExternalPublication));
    TestTrue(
        TEXT("Injected rename failure rolls back the full transaction"),
        RenameFailureResult.bRolledBack);
    TestFalse(
        TEXT("Rename rollback closes its recovery receipt"),
        IFileManager::Get().FileExists(
            *RenameFailureResult.RecoveryReceiptPath));
    TArray<uint8> RenamedSourceAfterFailure;
    TArray<uint8> DeploymentAfterRenameFailure;
    TArray<uint8> RequestAfterRenameFailure;
    TArray<uint8> ReferenceAfterRenameFailure;
    TestTrue(
        TEXT("Reads renamed source after rollback"),
        ReadBytes(RenameResult.SourcePath, RenamedSourceAfterFailure));
    TestTrue(
        TEXT("Reads deployment after rename rollback"),
        ReadBytes(Cleanup.DeploymentPath, DeploymentAfterRenameFailure));
    TestTrue(
        TEXT("Reads request after rename rollback"),
        ReadBytes(Cleanup.FirstPlayRequestPath, RequestAfterRenameFailure));
    TestTrue(
        TEXT("Reads semantic reference after rename rollback"),
        ReadBytes(RenameReferencePath, ReferenceAfterRenameFailure));
    TestEqual(
        TEXT("Rename rollback restores source bytes exactly"),
        RenamedSourceAfterFailure,
        RenamedSourceBeforeFailure);
    TestEqual(
        TEXT("Rename rollback restores deployment bytes exactly"),
        DeploymentAfterRenameFailure,
        DeploymentBeforeRenameFailure);
    TestEqual(
        TEXT("Rename rollback restores request bytes exactly"),
        RequestAfterRenameFailure,
        RequestBeforeRenameFailure);
    TestEqual(
        TEXT("Rename rollback restores semantic reference bytes exactly"),
        ReferenceAfterRenameFailure,
        ReferenceBeforeRenameFailure);
    TestEqual(
        TEXT("Rename rollback restores generated class identity"),
        ScriptAsset->GeneratedClassPath.ToString(),
        RenamedPackage + TEXT(".") + RenamedName + TEXT("_C"));
    TestEqual(
        TEXT("Rename rollback restores the UE soft class reference"),
        UEReferencer->GeneratedClassPath.ToString(),
        RenamedPackage + TEXT(".") + RenamedName + TEXT("_C"));
    TestFalse(
        TEXT("Rename rollback removes the failed target source"),
        IFileManager::Get().FileExists(*RenameFailureResult.SourcePath));
    TestFalse(
        TEXT("Rename rollback removes the failed Blueprint package"),
        FPackageName::DoesPackageExist(RenameFailurePackage));
    TestFalse(
        TEXT("Rename rollback removes the failed sidecar package"),
        FPackageName::DoesPackageExist(RenameFailureSidecar));

    FUnifiedScriptClassAssetOperationResult DuplicateResult;
    if (!TestTrue(
            *FString::Printf(TEXT("Duplicates the complete Script Class: %s"), *DuplicateResult.Error),
            FUnifiedScriptClassAssetOperations::Duplicate(
                ScriptAsset,
                DuplicatePackage,
                DuplicateResult)))
    {
        return false;
    }
    Cleanup.Assets.Add(DuplicateResult.Blueprint);
    Cleanup.Assets.Add(DuplicateResult.ScriptAsset);
    Cleanup.SourcePaths.Add(DuplicateResult.SourcePath);
    TestTrue(TEXT("Duplicate transaction reports success"), DuplicateResult.bSucceeded);
    TestFalse(
        TEXT("Duplicate closes its committed recovery receipt"),
        IFileManager::Get().FileExists(*DuplicateResult.RecoveryReceiptPath));
    TestNotEqual(
        TEXT("Duplicate receives an independent ClassId"),
        DuplicateResult.ScriptAsset->ScriptClassId,
        OriginalClassId);
    TestEqual(
        TEXT("Duplicate proxy points to its own sidecar"),
        GetScriptAsset(DuplicateResult.Blueprint),
        DuplicateResult.ScriptAsset);
    FString DuplicateSource;
    TestTrue(
        TEXT("Duplicate publishes an independent source file"),
        FFileHelper::LoadFileToString(DuplicateSource, *DuplicateResult.SourcePath));
    TestTrue(
        TEXT("Duplicate preserves authored behavior"),
        DuplicateSource.Contains(TEXT("return value + 1;")));
    TestTrue(
        TEXT("Duplicate updates the exact class declaration"),
        DuplicateSource.Contains(TEXT("class ") + DuplicateName + TEXT(" ")));
    TestTrue(
        TEXT("Duplicate writes its fresh semantic identity"),
        DuplicateSource.Contains(FString::Printf(
            TEXT("ClassId = \"us_%s\""),
            *DuplicateResult.ScriptAsset->ScriptClassId
                .ToString(EGuidFormats::Digits)
                .ToLower())));

    TArray<uint8> DeploymentBeforeFailure;
    TArray<uint8> RequestBeforeFailure;
    TestTrue(
        TEXT("Snapshots committed deployment before rollback coverage"),
        ReadBytes(Cleanup.DeploymentPath, DeploymentBeforeFailure));
    TestTrue(
        TEXT("Snapshots committed request before rollback coverage"),
        ReadBytes(Cleanup.FirstPlayRequestPath, RequestBeforeFailure));
    FUnifiedScriptClassAssetOperationResult FailureResult;
    TestFalse(
        TEXT("Injected post-publication failure rejects the duplicate"),
        FUnifiedScriptClassAssetOperations::Duplicate(
            ScriptAsset,
            DuplicateFailurePackage,
            FailureResult,
            EUnifiedScriptClassAssetOperationFailurePoint::AfterExternalPublication));
    TestTrue(TEXT("Injected failure rolls back the full transaction"), FailureResult.bRolledBack);
    TestFalse(
        TEXT("Duplicate rollback closes its recovery receipt"),
        IFileManager::Get().FileExists(*FailureResult.RecoveryReceiptPath));
    TArray<uint8> DeploymentAfterFailure;
    TArray<uint8> RequestAfterFailure;
    TestTrue(
        TEXT("Reads deployment after rollback"),
        ReadBytes(Cleanup.DeploymentPath, DeploymentAfterFailure));
    TestTrue(
        TEXT("Reads request after rollback"),
        ReadBytes(Cleanup.FirstPlayRequestPath, RequestAfterFailure));
    TestEqual(
        TEXT("Rollback restores deployment bytes exactly"),
        DeploymentAfterFailure,
        DeploymentBeforeFailure);
    TestEqual(
        TEXT("Rollback restores first-Play bytes exactly"),
        RequestAfterFailure,
        RequestBeforeFailure);
    TestFalse(
        TEXT("Rollback removes the new managed source"),
        IFileManager::Get().FileExists(*FailureResult.SourcePath));
    TestFalse(
        TEXT("Rollback removes the duplicated Blueprint package"),
        FPackageName::DoesPackageExist(DuplicateFailurePackage));
    TestFalse(
        TEXT("Rollback removes the duplicated Script Asset package"),
        FPackageName::DoesPackageExist(DuplicateFailureSidecar));

    TestTrue(
        TEXT("Generated Blueprint is recognized as one half of a complete Script Class"),
        FUnifiedScriptClassAssetOperations::IsCompleteClassAsset(
            DuplicateResult.Blueprint));
    TestTrue(
        TEXT("Script Asset is recognized as one half of a complete Script Class"),
        FUnifiedScriptClassAssetOperations::IsCompleteClassAsset(
            DuplicateResult.ScriptAsset));
    TArray<UObject*> BareDeleteObjects{DuplicateResult.Blueprint};
    FCanDeleteAssetResult BareDeleteResult;
    FEditorDelegates::OnAssetsCanDelete.Broadcast(
        BareDeleteObjects,
        BareDeleteResult);
    TestFalse(
        TEXT("Editor guard rejects bare generated Blueprint deletion"),
        BareDeleteResult.Get());
    FOnPreDestructiveAssetActionArgs BareRenameArgs(
        BareDeleteObjects,
        EDestructiveAssetActions::AssetRename);
    FEditorDelegates::OnPreDestructiveAssetActionWithArgs.Broadcast(
        BareRenameArgs);
    TestFalse(
        TEXT("Editor guard rejects bare generated Blueprint rename or move"),
        BareRenameArgs.Result.bSuccess);

    UBlueprint* BareDuplicate = Cast<UBlueprint>(
        FAssetToolsModule::GetModule().Get().DuplicateAsset(
            BareDuplicateName,
            PackageRoot,
            DuplicateResult.Blueprint));
    if (!TestNotNull(
            TEXT("Low-level AssetTools can create a Blueprint-only copy before guard cleanup"),
            BareDuplicate))
    {
        return false;
    }
    TestTrue(
        TEXT("Blueprint-only copy is classified as an orphaned Script Class proxy"),
        FUnifiedScriptClassAssetOperations::IsOrphanedGeneratedBlueprintDuplicate(
            BareDuplicate));
    TArray<FString> BareDuplicateCleanupErrors;
    TestEqual(
        TEXT("Deferred guard removes exactly one Blueprint-only copy"),
        UE::UnifiedScript::Editor::ProcessPendingUnifiedScriptBlueprintDuplicateCleanup(
            BareDuplicateCleanupErrors),
        1);
    TestTrue(
        TEXT("Blueprint-only duplicate cleanup reports no errors"),
        BareDuplicateCleanupErrors.IsEmpty());
    TestFalse(
        TEXT("Blueprint-only duplicate package does not survive cleanup"),
        FPackageName::DoesPackageExist(BareDuplicatePackage));

    const FString ManagedReferenceProbePath = FPaths::Combine(
        FPaths::GetPath(DuplicateResult.SourcePath),
        DuplicateName + TEXT("_ReferenceProbe.cs"));
    Cleanup.SourcePaths.Add(ManagedReferenceProbePath);
    const FString ManagedReferenceProbe = FString::Printf(
        TEXT("namespace UnifiedScript.Unreal.Demo; sealed class ReferenceProbe { %s? Value; }\n"),
        *DuplicateName);
    TestTrue(
        TEXT("Writes an external managed reference probe"),
        FFileHelper::SaveStringToFile(
            ManagedReferenceProbe,
            *ManagedReferenceProbePath));
    FUnifiedScriptClassAssetOperationResult ReferencedDeleteResult;
    TestFalse(
        TEXT("Delete rejects a Script Class with a managed source referencer"),
        FUnifiedScriptClassAssetOperations::Delete(
            DuplicateResult.ScriptAsset,
            ReferencedDeleteResult));
    TestTrue(
        TEXT("Referenced delete explains the managed referencer"),
        ReferencedDeleteResult.Error.Contains(TEXT("managed semantic references remain")));
    TestTrue(
        TEXT("Rejected referenced delete preserves the managed source"),
        IFileManager::Get().FileExists(*DuplicateResult.SourcePath));
    TestTrue(
        TEXT("Removes the managed reference probe before delete transaction coverage"),
        IFileManager::Get().Delete(
            *ManagedReferenceProbePath,
            false,
            true));

    TArray<uint8> DuplicateSourceBeforeDeleteFailure;
    TArray<uint8> DeploymentBeforeDeleteFailure;
    TArray<uint8> RequestBeforeDeleteFailure;
    TestTrue(
        TEXT("Snapshots duplicate source before delete rollback coverage"),
        ReadBytes(
            DuplicateResult.SourcePath,
            DuplicateSourceBeforeDeleteFailure));
    TestTrue(
        TEXT("Snapshots deployment before delete rollback coverage"),
        ReadBytes(
            Cleanup.DeploymentPath,
            DeploymentBeforeDeleteFailure));
    TestTrue(
        TEXT("Snapshots request before delete rollback coverage"),
        ReadBytes(
            Cleanup.FirstPlayRequestPath,
            RequestBeforeDeleteFailure));
    FUnifiedScriptClassAssetOperationResult DeleteFailureResult;
    TestFalse(
        TEXT("Injected post-publication failure rejects complete-class delete"),
        FUnifiedScriptClassAssetOperations::Delete(
            DuplicateResult.ScriptAsset,
            DeleteFailureResult,
            EUnifiedScriptClassAssetOperationFailurePoint::AfterExternalPublication));
    TestTrue(
        TEXT("Injected delete failure rolls back the full transaction"),
        DeleteFailureResult.bRolledBack);
    TestFalse(
        TEXT("Delete rollback closes its recovery receipt"),
        IFileManager::Get().FileExists(
            *DeleteFailureResult.RecoveryReceiptPath));
    TArray<uint8> DuplicateSourceAfterDeleteFailure;
    TArray<uint8> DeploymentAfterDeleteFailure;
    TArray<uint8> RequestAfterDeleteFailure;
    TestTrue(
        TEXT("Reads duplicate source after delete rollback"),
        ReadBytes(
            DuplicateResult.SourcePath,
            DuplicateSourceAfterDeleteFailure));
    TestTrue(
        TEXT("Reads deployment after delete rollback"),
        ReadBytes(
            Cleanup.DeploymentPath,
            DeploymentAfterDeleteFailure));
    TestTrue(
        TEXT("Reads request after delete rollback"),
        ReadBytes(
            Cleanup.FirstPlayRequestPath,
            RequestAfterDeleteFailure));
    TestEqual(
        TEXT("Delete rollback restores source bytes exactly"),
        DuplicateSourceAfterDeleteFailure,
        DuplicateSourceBeforeDeleteFailure);
    TestEqual(
        TEXT("Delete rollback restores deployment bytes exactly"),
        DeploymentAfterDeleteFailure,
        DeploymentBeforeDeleteFailure);
    TestEqual(
        TEXT("Delete rollback restores request bytes exactly"),
        RequestAfterDeleteFailure,
        RequestBeforeDeleteFailure);
    TestTrue(
        TEXT("Delete rollback restores the Blueprint package"),
        FPackageName::DoesPackageExist(DuplicatePackage));
    TestTrue(
        TEXT("Delete rollback restores the Script Asset package"),
        FPackageName::DoesPackageExist(DuplicateSidecar));

    const FString DeletedManagedTypeName = DuplicateResult.ManagedTypeName;
    const FString DeletedSourcePath = DuplicateResult.SourcePath;
    FUnifiedScriptClassAssetOperationResult DeleteResult;
    TestTrue(
        TEXT("Deletes the complete unreferenced Script Class"),
        FUnifiedScriptClassAssetOperations::Delete(
            DuplicateResult.ScriptAsset,
            DeleteResult));
    TestTrue(TEXT("Delete transaction reports success"), DeleteResult.bSucceeded);
    TestFalse(
        TEXT("Delete closes its committed recovery receipt"),
        IFileManager::Get().FileExists(*DeleteResult.RecoveryReceiptPath));
    TestNull(TEXT("Delete does not return a stale Blueprint pointer"), DeleteResult.Blueprint);
    TestNull(TEXT("Delete does not return a stale Script Asset pointer"), DeleteResult.ScriptAsset);
    TestFalse(
        TEXT("Delete removes the managed source"),
        IFileManager::Get().FileExists(*DeletedSourcePath));
    TestFalse(
        TEXT("Delete removes the Blueprint package"),
        FPackageName::DoesPackageExist(DuplicatePackage));
    TestFalse(
        TEXT("Delete removes the Script Asset package"),
        FPackageName::DoesPackageExist(DuplicateSidecar));
    FString DeploymentAfterDelete;
    TestTrue(
        TEXT("Reads deployment after complete-class delete"),
        FFileHelper::LoadFileToString(
            DeploymentAfterDelete,
            *Cleanup.DeploymentPath));
    TestFalse(
        TEXT("Delete removes the exact deployment identity"),
        DeploymentAfterDelete.Contains(DeletedManagedTypeName));
    TestFalse(
        TEXT("Delete removes its matching first-Play request"),
        IFileManager::Get().FileExists(*Cleanup.FirstPlayRequestPath));
    return true;
}

#endif

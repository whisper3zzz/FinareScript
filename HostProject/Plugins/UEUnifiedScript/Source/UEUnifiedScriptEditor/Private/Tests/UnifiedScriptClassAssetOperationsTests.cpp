#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptClassAssetOperations.h"

#include "Engine/Blueprint.h"
#include "HAL/FileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
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
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptClassAssetOperationsTest,
    "UnifiedScript.Unreal.ScriptClass.AssetOperations",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptClassAssetOperationsTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::ClassAssetOperations::Tests;
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

    const FGuid OriginalClassId = ScriptAsset->ScriptClassId;
    FString OriginalSource;
    TestTrue(
        TEXT("Reads the original managed source"),
        FFileHelper::LoadFileToString(
            OriginalSource,
            *ResolveConfiguredPath(ScriptAsset->SourcePath)));

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
    TestEqual(TEXT("Rename preserves stable ClassId"), ScriptAsset->ScriptClassId, OriginalClassId);
    TestEqual(
        TEXT("Rename updates managed type identity"),
        ScriptAsset->ScriptTypeName,
        FString(TEXT("UnifiedScript.Unreal.Demo.")) + RenamedName);
    TestEqual(
        TEXT("Rename updates generated class identity"),
        ScriptAsset->GeneratedClassPath.ToString(),
        RenamedPackage + TEXT(".") + RenamedName + TEXT("_C"));
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
    TestTrue(
        TEXT("Snapshots renamed source before rename rollback coverage"),
        ReadBytes(RenameResult.SourcePath, RenamedSourceBeforeFailure));
    TestTrue(
        TEXT("Snapshots deployment before rename rollback coverage"),
        ReadBytes(Cleanup.DeploymentPath, DeploymentBeforeRenameFailure));
    TestTrue(
        TEXT("Snapshots request before rename rollback coverage"),
        ReadBytes(Cleanup.FirstPlayRequestPath, RequestBeforeRenameFailure));
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
    TArray<uint8> RenamedSourceAfterFailure;
    TArray<uint8> DeploymentAfterRenameFailure;
    TArray<uint8> RequestAfterRenameFailure;
    TestTrue(
        TEXT("Reads renamed source after rollback"),
        ReadBytes(RenameResult.SourcePath, RenamedSourceAfterFailure));
    TestTrue(
        TEXT("Reads deployment after rename rollback"),
        ReadBytes(Cleanup.DeploymentPath, DeploymentAfterRenameFailure));
    TestTrue(
        TEXT("Reads request after rename rollback"),
        ReadBytes(Cleanup.FirstPlayRequestPath, RequestAfterRenameFailure));
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
        TEXT("Rename rollback restores generated class identity"),
        ScriptAsset->GeneratedClassPath.ToString(),
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
    return true;
}

#endif

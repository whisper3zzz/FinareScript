#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptClassFactory.h"

#include "Engine/Blueprint.h"
#include "HAL/FileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptComponent.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::ClassFactory::Tests
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

struct FScopedCreatedClassCleanup
{
    FString DeploymentPath;
    TArray<uint8> OriginalDeployment;
    TArray<FString> SourcePaths;
    TArray<FString> PackagePaths;

    ~FScopedCreatedClassCleanup()
    {
        if (!DeploymentPath.IsEmpty() && !OriginalDeployment.IsEmpty())
        {
            FFileHelper::SaveArrayToFile(OriginalDeployment, *DeploymentPath);
        }
        for (const FString& SourcePath : SourcePaths)
        {
            IFileManager::Get().Delete(*SourcePath, false, true);
        }
        for (const FString& PackagePath : PackagePaths)
        {
            IFileManager::Get().Delete(*PackagePath, false, true);
        }
    }
};

template <typename TFactory>
bool CreateAndVerify(
    FAutomationTestBase& Test,
    const FString& TypeName,
    EUnifiedScriptClassKind ExpectedKind,
    FScopedCreatedClassCleanup& Cleanup)
{
    const FString PackageName = FString::Printf(
        TEXT("/Game/UnifiedScriptAutomation/%s"),
        *TypeName);
    UPackage* Package = CreatePackage(*PackageName);
    TFactory* Factory = NewObject<TFactory>();
    UBlueprint* Blueprint = Cast<UBlueprint>(Factory->FactoryCreateNew(
        UBlueprint::StaticClass(),
        Package,
        *TypeName,
        RF_Public | RF_Standalone | RF_Transactional,
        nullptr,
        GWarn,
        TEXT("UnifiedScriptClassFactoryAutomation")));
    Test.TestNotNull(TEXT("Script Class Blueprint is created"), Blueprint);
    if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
    {
        return false;
    }

    UUnifiedScriptAsset* ScriptAsset = nullptr;
    if (ExpectedKind == EUnifiedScriptClassKind::Actor)
    {
        Test.TestTrue(
            TEXT("Generated Actor is selectable as an ordinary Actor class"),
            Blueprint->GeneratedClass->IsChildOf(AActor::StaticClass()));
        Test.TestNotNull(
            TEXT("Generated Actor exposes an editable Blueprint property"),
            FindFProperty<FProperty>(Blueprint->GeneratedClass, TEXT("ManagedActorState")));
        Test.TestNotNull(
            TEXT("Generated Actor exposes a callable Blueprint function"),
            Blueprint->GeneratedClass->FindFunctionByName(TEXT("SetManagedActorState")));
        const AUnifiedScriptActor* Defaults = Cast<AUnifiedScriptActor>(
            Blueprint->GeneratedClass->GetDefaultObject());
        Test.TestNotNull(TEXT("Actor proxy has the expected native parent"), Defaults);
        ScriptAsset = Defaults != nullptr && Defaults->ScriptComponent != nullptr
            ? Defaults->ScriptComponent->ScriptAsset.Get()
            : nullptr;
    }
    else
    {
        Test.TestTrue(
            TEXT("Generated Component is attachable as an ordinary ActorComponent class"),
            Blueprint->GeneratedClass->IsChildOf(UActorComponent::StaticClass()));
        Test.TestNotNull(
            TEXT("Generated Component exposes an editable Blueprint property"),
            FindFProperty<FProperty>(Blueprint->GeneratedClass, TEXT("bStartAutomatically")));
        Test.TestNotNull(
            TEXT("Generated Component exposes a callable Blueprint function"),
            Blueprint->GeneratedClass->FindFunctionByName(TEXT("SetScriptStateInt32")));
        const UUnifiedScriptComponent* Defaults = Cast<UUnifiedScriptComponent>(
            Blueprint->GeneratedClass->GetDefaultObject());
        Test.TestNotNull(TEXT("Component proxy has the expected native parent"), Defaults);
        ScriptAsset = Defaults != nullptr ? Defaults->ScriptAsset.Get() : nullptr;
    }
    Test.TestNotNull(TEXT("Generated proxy retains its Script Asset sidecar"), ScriptAsset);
    if (ScriptAsset == nullptr)
    {
        return false;
    }

    Test.TestEqual(TEXT("Script Class kind is stable"), ScriptAsset->ScriptClassKind, ExpectedKind);
    Test.TestTrue(TEXT("Script Class ID is persistent"), ScriptAsset->ScriptClassId.IsValid());
    Test.TestEqual(
        TEXT("Generated class path is exact"),
        ScriptAsset->GeneratedClassPath.ToString(),
        Blueprint->GeneratedClass->GetPathName());
    Test.TestTrue(
        TEXT("Managed type identity includes the created class"),
        ScriptAsset->ScriptTypeName.EndsWith(TEXT(".") + TypeName));
    Test.TestTrue(
        TEXT("Declaration identity uses a stable project/class path"),
        ScriptAsset->DeclarationAssetPath.EndsWith(TEXT("/") + TypeName));

    const FString SourcePath = ResolveConfiguredPath(ScriptAsset->SourcePath);
    Cleanup.SourcePaths.Add(SourcePath);
    FString Source;
    Test.TestTrue(TEXT("Managed Script Class source is written"), FFileHelper::LoadFileToString(Source, *SourcePath));
    Test.TestTrue(
        TEXT("Managed source pins the same semantic class ID"),
        Source.Contains(FString::Printf(
            TEXT("ClassId = \"us_%s\""),
            *ScriptAsset->ScriptClassId.ToString(EGuidFormats::Digits).ToLower())));
    Test.TestTrue(TEXT("Managed source exposes editable state"), Source.Contains(TEXT("[Expose(")));
    Test.TestTrue(TEXT("Managed source includes BeginPlay"), Source.Contains(TEXT("OnBeginPlay")));
    Test.TestTrue(TEXT("Managed source includes Tick"), Source.Contains(TEXT("OnTick")));

    const FString BlueprintFilename = FPackageName::LongPackageNameToFilename(
        PackageName,
        FPackageName::GetAssetPackageExtension());
    const FString ScriptFilename = FPackageName::LongPackageNameToFilename(
        ScriptAsset->GetOutermost()->GetName(),
        FPackageName::GetAssetPackageExtension());
    Cleanup.PackagePaths.Add(BlueprintFilename);
    Cleanup.PackagePaths.Add(ScriptFilename);
    FSavePackageArgs SaveArguments;
    SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
    SaveArguments.SaveFlags = SAVE_None;
    Test.TestTrue(TEXT("Blueprint proxy package can be saved"), UPackage::SavePackage(
        Package,
        Blueprint,
        *BlueprintFilename,
        SaveArguments));
    Test.TestTrue(TEXT("Script Asset sidecar package can be saved"), UPackage::SavePackage(
        ScriptAsset->GetOutermost(),
        ScriptAsset,
        *ScriptFilename,
        SaveArguments));
    Test.TestTrue(TEXT("Saved Blueprint proxy exists"), IFileManager::Get().FileExists(*BlueprintFilename));
    Test.TestTrue(TEXT("Saved Script Asset sidecar exists"), IFileManager::Get().FileExists(*ScriptFilename));
    return true;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptClassFactoryContractTest,
    "UnifiedScript.Unreal.ScriptClass.FactoryContract",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptClassFactoryContractTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::ClassFactory::Tests;
    FScopedCreatedClassCleanup Cleanup;
    Cleanup.DeploymentPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectDir(),
        TEXT("../samples/Unreal/UnifiedScript.Unreal.Demo.deployment.json")));
    if (!FFileHelper::LoadFileToArray(Cleanup.OriginalDeployment, *Cleanup.DeploymentPath))
    {
        AddError(TEXT("Cannot snapshot the deployment manifest before the Script Class factory test."));
        return false;
    }

    const FString Suffix = FGuid::NewGuid()
        .ToString(EGuidFormats::Digits)
        .Left(12);
    const bool bActor = CreateAndVerify<UUnifiedScriptActorClassFactory>(
        *this,
        TEXT("M13Actor_") + Suffix,
        EUnifiedScriptClassKind::Actor,
        Cleanup);
    const bool bComponent = CreateAndVerify<UUnifiedScriptActorComponentClassFactory>(
        *this,
        TEXT("M13Component_") + Suffix,
        EUnifiedScriptClassKind::ActorComponent,
        Cleanup);
    return bActor && bComponent;
}

#endif

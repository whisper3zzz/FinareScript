#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptClassFactory.h"

#include "Engine/Blueprint.h"
#include "Engine/World.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptClassBases.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptProjectSetup.h"
#include "UnifiedScriptWorldSubsystem.h"
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
    FString FirstPlayRequestPath;
    TArray<uint8> OriginalFirstPlayRequest;
    bool bHadFirstPlayRequest = false;
    TArray<FString> SourcePaths;
    TArray<FString> PackagePaths;

    ~FScopedCreatedClassCleanup()
    {
        if (!DeploymentPath.IsEmpty() && !OriginalDeployment.IsEmpty())
        {
            FFileHelper::SaveArrayToFile(OriginalDeployment, *DeploymentPath);
        }
        if (!FirstPlayRequestPath.IsEmpty())
        {
            if (bHadFirstPlayRequest)
            {
                FFileHelper::SaveArrayToFile(
                    OriginalFirstPlayRequest,
                    *FirstPlayRequestPath);
            }
            else
            {
                IFileManager::Get().Delete(*FirstPlayRequestPath, false, true);
            }
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
    FScopedCreatedClassCleanup* Cleanup)
{
    const bool bSupportsFirstPlay =
        ExpectedKind == EUnifiedScriptClassKind::Actor ||
        ExpectedKind == EUnifiedScriptClassKind::ActorComponent;
    const FString FirstPlayRequestPath = FUnifiedScriptProjectSetup::GetFirstPlayRequestPath(
        FPaths::ProjectDir());
    TArray<uint8> FirstPlayBefore;
    const bool bHadFirstPlayBefore = FFileHelper::LoadFileToArray(
        FirstPlayBefore,
        *FirstPlayRequestPath);
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

    const UUnifiedScriptBlueprint* UnifiedBlueprint =
        Cast<UUnifiedScriptBlueprint>(Blueprint);
    Test.TestNotNull(
        TEXT("Generated proxy uses the product-owned Blueprint asset class"),
        UnifiedBlueprint);
    UUnifiedScriptAsset* ScriptAsset = UnifiedBlueprint != nullptr
        ? UnifiedBlueprint->ScriptAsset.Get()
        : nullptr;
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
        Test.TestTrue(
            TEXT("Actor CDO and Blueprint proxy retain the same sidecar"),
            Defaults != nullptr && Defaults->ScriptComponent != nullptr &&
                Defaults->ScriptComponent->ScriptAsset == ScriptAsset);
    }
    else if (ExpectedKind == EUnifiedScriptClassKind::ActorComponent)
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
        Test.TestTrue(
            TEXT("Component CDO and Blueprint proxy retain the same sidecar"),
            Defaults != nullptr && Defaults->ScriptAsset == ScriptAsset);
    }
    else if (ExpectedKind == EUnifiedScriptClassKind::Object)
    {
        Test.TestTrue(
            TEXT("Generated Object is instantiable as an ordinary UObject class"),
            Blueprint->GeneratedClass->IsChildOf(UUnifiedScriptObject::StaticClass()));
        Test.TestNotNull(
            TEXT("Generated Object has the expected native CDO"),
            Cast<UUnifiedScriptObject>(Blueprint->GeneratedClass->GetDefaultObject()));
    }
    else if (ExpectedKind == EUnifiedScriptClassKind::BlueprintFunctionLibrary)
    {
        Test.TestTrue(
            TEXT("Generated library is an ordinary Blueprint Function Library class"),
            Blueprint->GeneratedClass->IsChildOf(
                UUnifiedScriptBlueprintFunctionLibrary::StaticClass()));
        Test.TestEqual(
            TEXT("Generated library uses the Function Library Blueprint kind"),
            Blueprint->BlueprintType,
            BPTYPE_FunctionLibrary);
    }
    Test.TestNotNull(TEXT("Generated proxy retains its Script Asset sidecar"), ScriptAsset);
    if (ScriptAsset == nullptr)
    {
        return false;
    }

    UClass* ExpectedTargetClass =
        ExpectedKind == EUnifiedScriptClassKind::Actor
        ? AUnifiedScriptActor::StaticClass()
        : ExpectedKind == EUnifiedScriptClassKind::ActorComponent
        ? AActor::StaticClass()
        : ExpectedKind == EUnifiedScriptClassKind::Object
        ? UUnifiedScriptObject::StaticClass()
        : UUnifiedScriptBlueprintFunctionLibrary::StaticClass();
    Test.TestEqual(
        TEXT("Script Class pins the exact reviewed native target contract"),
        ScriptAsset->RequiredTargetClass.Get(),
        ExpectedTargetClass);
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

    if (bSupportsFirstPlay)
    {
        FUnifiedScriptFirstPlayRequest FirstPlayRequest;
        FString FirstPlayError;
        Test.TestTrue(
            TEXT("Lifecycle Script Class writes its first-Play handoff"),
            FUnifiedScriptProjectSetup::ReadFirstPlayRequest(
                FirstPlayRequestPath,
                FirstPlayRequest,
                FirstPlayError));
        Test.TestEqual(
            TEXT("First-Play handoff pins the same semantic class ID"),
            FirstPlayRequest.RequestId,
            FString::Printf(
                TEXT("us_%s"),
                *ScriptAsset->ScriptClassId.ToString(EGuidFormats::Digits).ToLower()));
        Test.TestEqual(
            TEXT("First-Play handoff pins the Blueprint proxy"),
            FirstPlayRequest.BlueprintAssetPath,
            Blueprint->GetPathName());
        Test.TestEqual(
            TEXT("First-Play handoff pins the Script Asset sidecar"),
            FirstPlayRequest.ScriptAssetPath,
            ScriptAsset->GetPathName());
        Test.TestTrue(
            TEXT("First-Play handoff waits for Build/Onboard"),
            FirstPlayRequest.IsBuildRequired());
    }
    else
    {
        TArray<uint8> FirstPlayAfter;
        const bool bHasFirstPlayAfter = FFileHelper::LoadFileToArray(
            FirstPlayAfter,
            *FirstPlayRequestPath);
        Test.TestEqual(
            TEXT("Non-lifecycle proxy preserves first-Play request existence"),
            bHasFirstPlayAfter,
            bHadFirstPlayBefore);
        Test.TestTrue(
            TEXT("Non-lifecycle proxy preserves first-Play request bytes"),
            !bHadFirstPlayBefore || FirstPlayAfter == FirstPlayBefore);
    }

    const FString SourcePath = ResolveConfiguredPath(ScriptAsset->SourcePath);
    if (Cleanup != nullptr)
    {
        Cleanup->SourcePaths.Add(SourcePath);
    }
    FString Source;
    Test.TestTrue(TEXT("Managed Script Class source is written"), FFileHelper::LoadFileToString(Source, *SourcePath));
    Test.TestTrue(
        TEXT("Managed source pins the same semantic class ID"),
        Source.Contains(FString::Printf(
            TEXT("ClassId = \"us_%s\""),
            *ScriptAsset->ScriptClassId.ToString(EGuidFormats::Digits).ToLower())));
    const TCHAR* ManagedKind =
        ExpectedKind == EUnifiedScriptClassKind::Actor ? TEXT("Actor") :
        ExpectedKind == EUnifiedScriptClassKind::ActorComponent ? TEXT("ActorComponent") :
        ExpectedKind == EUnifiedScriptClassKind::Object ? TEXT("Object") :
        TEXT("BlueprintFunctionLibrary");
    Test.TestTrue(
        TEXT("Managed source pins the reviewed proxy class kind"),
        Source.Contains(FString::Printf(
            TEXT("[UnrealScriptClass(UnrealScriptClassKind.%s,"),
            ManagedKind)));
    Test.TestTrue(
        TEXT("Managed source pins the exact generated Blueprint class path"),
        Source.Contains(FString::Printf(
            TEXT("GeneratedClassPath = \"%s\""),
            *Blueprint->GeneratedClass->GetPathName())));
    Test.TestTrue(
        TEXT("Managed source emits a Visual-compatible block body"),
        Source.Contains(TEXT("AddOne(int value)\n    {\n        return value + 1;\n    }")));
    if (ExpectedKind == EUnifiedScriptClassKind::BlueprintFunctionLibrary)
    {
        Test.TestTrue(
            TEXT("Managed library emits a static class and function"),
            Source.Contains(TEXT("public static class")) &&
                Source.Contains(TEXT("public static int AddOne")));
        Test.TestFalse(
            TEXT("Managed library does not emit instance state"),
            Source.Contains(TEXT("[Expose(")));
    }
    else
    {
        Test.TestTrue(
            TEXT("Managed instance source exposes editable state"),
            Source.Contains(TEXT("[Expose(")));
        if (ExpectedKind == EUnifiedScriptClassKind::Object)
        {
            Test.TestTrue(
                TEXT("Managed Object uses generic object lifecycle hooks"),
                Source.Contains(TEXT(": UnrealObjectScript")) &&
                    Source.Contains(TEXT("OnInitialized")) &&
                    Source.Contains(TEXT("OnUpdate")));
            Test.TestFalse(
                TEXT("Managed Object does not advertise gameplay Blueprint events"),
                Source.Contains(TEXT("[UnrealBlueprintEvent]")));
        }
        else
        {
            const TCHAR* ExpectedBase = ExpectedKind == EUnifiedScriptClassKind::Actor
                ? TEXT("UnrealActorScript")
                : TEXT("UnrealActorComponentScript");
            Test.TestTrue(
                TEXT("Managed lifecycle source uses its exact reviewed base"),
                Source.Contains(FString::Printf(TEXT(": %s"), ExpectedBase)));
            Test.TestTrue(TEXT("Managed source includes BeginPlay"), Source.Contains(TEXT("OnBeginPlay")));
            Test.TestTrue(TEXT("Managed source includes Tick"), Source.Contains(TEXT("OnTick")));
            Test.TestTrue(TEXT("Managed source includes EndPlay"), Source.Contains(TEXT("OnEndPlay")));
            Test.TestTrue(
                TEXT("Managed source includes an explicit-Super Blueprint event"),
                Source.Contains(TEXT("[UnrealBlueprintEvent]")) &&
                    Source.Contains(TEXT("OnManagedActivated(int amount)")));
        }
    }

    const FString BlueprintFilename = FPackageName::LongPackageNameToFilename(
        PackageName,
        FPackageName::GetAssetPackageExtension());
    const FString ScriptFilename = FPackageName::LongPackageNameToFilename(
        ScriptAsset->GetOutermost()->GetName(),
        FPackageName::GetAssetPackageExtension());
    if (Cleanup != nullptr)
    {
        Cleanup->PackagePaths.Add(BlueprintFilename);
        Cleanup->PackagePaths.Add(ScriptFilename);
    }
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

constexpr const TCHAR* PersistentActorType = TEXT("M13C0PersistentActor");
constexpr const TCHAR* PersistentComponentType = TEXT("M13C0PersistentComponent");
constexpr const TCHAR* PersistentPackageRoot =
    TEXT("/Game/UnifiedScriptAutomation");

FString PersistentPackageName(const TCHAR* TypeName)
{
    return FString::Printf(TEXT("%s/%s"), PersistentPackageRoot, TypeName);
}

FString PersistentObjectPath(const TCHAR* TypeName)
{
    return FString::Printf(
        TEXT("%s/%s.%s"),
        PersistentPackageRoot,
        TypeName,
        TypeName);
}

FString PersistentSidecarPath(const TCHAR* TypeName)
{
    return FString::Printf(
        TEXT("%s/SA_%s.SA_%s"),
        PersistentPackageRoot,
        TypeName,
        TypeName);
}

FString ProcessReceiptPath(const TCHAR* Stage)
{
    return FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectSavedDir(),
        TEXT("UnifiedScript/ScriptClassGoldenPath"),
        FString(Stage) + TEXT(".pid")));
}

bool WriteProcessReceipt(FAutomationTestBase& Test, const TCHAR* Stage)
{
    const FString Path = ProcessReceiptPath(Stage);
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Path), true);
    return Test.TestTrue(
        *FString::Printf(TEXT("Writes %s process receipt"), Stage),
        FFileHelper::SaveStringToFile(
            FString::FromInt(static_cast<int32>(FPlatformProcess::GetCurrentProcessId())),
            *Path,
            FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM));
}

UUnifiedScriptComponent* GetActorScriptComponent(UBlueprint* Blueprint)
{
    const AUnifiedScriptActor* Defaults = Blueprint != nullptr &&
            Blueprint->GeneratedClass != nullptr
        ? Cast<AUnifiedScriptActor>(Blueprint->GeneratedClass->GetDefaultObject())
        : nullptr;
    return Defaults != nullptr ? Defaults->ScriptComponent : nullptr;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptClassFactoryContractTest,
    "UnifiedScript.Unreal.ScriptClass.FactoryContract",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptClassFactoryContractTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::ClassFactory::Tests;
    const UUnifiedScriptActorClassFactory* ActorFactory =
        GetDefault<UUnifiedScriptActorClassFactory>();
    const UUnifiedScriptActorComponentClassFactory* ComponentFactory =
        GetDefault<UUnifiedScriptActorComponentClassFactory>();
    const UUnifiedScriptObjectClassFactory* ObjectFactory =
        GetDefault<UUnifiedScriptObjectClassFactory>();
    const UUnifiedScriptBlueprintFunctionLibraryClassFactory* LibraryFactory =
        GetDefault<UUnifiedScriptBlueprintFunctionLibraryClassFactory>();
    TestFalse(
        TEXT("Actor Script Class is hidden from create-then-rename menus"),
        ActorFactory->ShouldShowInNewMenu());
    TestFalse(
        TEXT("Component Script Class is hidden from create-then-rename menus"),
        ComponentFactory->ShouldShowInNewMenu());
    TestFalse(
        TEXT("Object Script Class is hidden from create-then-rename menus"),
        ObjectFactory->ShouldShowInNewMenu());
    TestFalse(
        TEXT("Function Library Script Class is hidden from create-then-rename menus"),
        LibraryFactory->ShouldShowInNewMenu());
    FScopedCreatedClassCleanup Cleanup;
    Cleanup.DeploymentPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectDir(),
        TEXT("../samples/Unreal/UnifiedScript.Unreal.Demo.deployment.json")));
    if (!FFileHelper::LoadFileToArray(Cleanup.OriginalDeployment, *Cleanup.DeploymentPath))
    {
        AddError(TEXT("Cannot snapshot the deployment manifest before the Script Class factory test."));
        return false;
    }
    Cleanup.FirstPlayRequestPath = FUnifiedScriptProjectSetup::GetFirstPlayRequestPath(
        FPaths::ProjectDir());
    Cleanup.bHadFirstPlayRequest = FFileHelper::LoadFileToArray(
        Cleanup.OriginalFirstPlayRequest,
        *Cleanup.FirstPlayRequestPath);

    const FString Suffix = FGuid::NewGuid()
        .ToString(EGuidFormats::Digits)
        .Left(12);
    const bool bActor = CreateAndVerify<UUnifiedScriptActorClassFactory>(
        *this,
        TEXT("M13Actor_") + Suffix,
        EUnifiedScriptClassKind::Actor,
        &Cleanup);
    const bool bComponent = CreateAndVerify<UUnifiedScriptActorComponentClassFactory>(
        *this,
        TEXT("M13Component_") + Suffix,
        EUnifiedScriptClassKind::ActorComponent,
        &Cleanup);
    const bool bObject = CreateAndVerify<UUnifiedScriptObjectClassFactory>(
        *this,
        TEXT("M13Object_") + Suffix,
        EUnifiedScriptClassKind::Object,
        &Cleanup);
    const bool bLibrary =
        CreateAndVerify<UUnifiedScriptBlueprintFunctionLibraryClassFactory>(
            *this,
            TEXT("M13Library_") + Suffix,
            EUnifiedScriptClassKind::BlueprintFunctionLibrary,
            &Cleanup);
    return bActor && bComponent && bObject && bLibrary;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptPersistentClassCreateTest,
    "UnifiedScript.Integration.ScriptClass.CreatePersistent",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptPersistentClassCreateTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::ClassFactory::Tests;
    const bool bActor = CreateAndVerify<UUnifiedScriptActorClassFactory>(
        *this,
        PersistentActorType,
        EUnifiedScriptClassKind::Actor,
        nullptr);
    const bool bComponent = CreateAndVerify<UUnifiedScriptActorComponentClassFactory>(
        *this,
        PersistentComponentType,
        EUnifiedScriptClassKind::ActorComponent,
        nullptr);
    return bActor && bComponent && WriteProcessReceipt(*this, TEXT("create"));
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptPersistentClassReloadLifecycleTest,
    "UnifiedScript.Integration.ScriptClass.ReloadLifecycle",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptPersistentClassReloadLifecycleTest::RunTest(
    const FString& Parameters)
{
    using namespace UE::UnifiedScript::ClassFactory::Tests;
    FString CreateProcessText;
    if (!TestTrue(
            TEXT("Reads the first-process creation receipt"),
            FFileHelper::LoadFileToString(
                CreateProcessText,
                *ProcessReceiptPath(TEXT("create")))))
    {
        return false;
    }
    CreateProcessText.TrimStartAndEndInline();
    TestNotEqual(
        TEXT("Reload verification runs in a different Editor process"),
        FCString::Atoi(*CreateProcessText),
        static_cast<int32>(FPlatformProcess::GetCurrentProcessId()));

    UBlueprint* ActorBlueprint = LoadObject<UBlueprint>(
        nullptr,
        *PersistentObjectPath(PersistentActorType));
    UBlueprint* ComponentBlueprint = LoadObject<UBlueprint>(
        nullptr,
        *PersistentObjectPath(PersistentComponentType));
    if (!TestNotNull(TEXT("Reloads the saved Actor Blueprint"), ActorBlueprint) ||
        !TestNotNull(TEXT("Reloads the saved Component Blueprint"), ComponentBlueprint) ||
        !TestNotNull(TEXT("Reloaded Actor Blueprint has a generated class"),
            ActorBlueprint != nullptr ? ActorBlueprint->GeneratedClass.Get() : nullptr) ||
        !TestNotNull(TEXT("Reloaded Component Blueprint has a generated class"),
            ComponentBlueprint != nullptr ? ComponentBlueprint->GeneratedClass.Get() : nullptr))
    {
        return false;
    }

    UUnifiedScriptAsset* ActorAsset = LoadObject<UUnifiedScriptAsset>(
        nullptr,
        *PersistentSidecarPath(PersistentActorType));
    UUnifiedScriptAsset* ComponentAsset = LoadObject<UUnifiedScriptAsset>(
        nullptr,
        *PersistentSidecarPath(PersistentComponentType));
    if (!TestNotNull(TEXT("Reloads the saved Actor Script Asset"), ActorAsset) ||
        !TestNotNull(TEXT("Reloads the saved Component Script Asset"), ComponentAsset))
    {
        return false;
    }

    TestTrue(
        TEXT("Reloaded Actor CDO retains the exact Script Asset"),
        GetActorScriptComponent(ActorBlueprint) != nullptr &&
            GetActorScriptComponent(ActorBlueprint)->ScriptAsset == ActorAsset);
    const UUnifiedScriptComponent* ComponentDefaults =
        Cast<UUnifiedScriptComponent>(ComponentBlueprint->GeneratedClass->GetDefaultObject());
    TestTrue(
        TEXT("Reloaded Component CDO retains the exact Script Asset"),
        ComponentDefaults != nullptr && ComponentDefaults->ScriptAsset == ComponentAsset);

    const int32 InitialManagedWorldCount =
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Creates the second-process gameplay World"), World))
    {
        return false;
    }
    World->AddToRoot();
    UUnifiedScriptWorldSubsystem* Host =
        World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    if (!TestNotNull(TEXT("Gameplay World owns a managed host"), Host))
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }

    AUnifiedScriptActor* ScriptActor = Cast<AUnifiedScriptActor>(
        World->SpawnActor(ActorBlueprint->GeneratedClass));
    AActor* ComponentOwner = World->SpawnActor<AActor>();
    UUnifiedScriptComponent* AttachedComponent = ComponentOwner != nullptr
        ? NewObject<UUnifiedScriptComponent>(
            ComponentOwner,
            ComponentBlueprint->GeneratedClass)
        : nullptr;
    if (!TestNotNull(TEXT("Spawns the reloaded Script Actor class"), ScriptActor) ||
        !TestNotNull(TEXT("Spawns a Component owner"), ComponentOwner) ||
        !TestNotNull(TEXT("Attaches the reloaded Script Component class"), AttachedComponent))
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }
    ComponentOwner->AddInstanceComponent(AttachedComponent);
    AttachedComponent->RegisterComponentWithWorld(World);

    UUnifiedScriptComponent* ActorComponent = ScriptActor->ScriptComponent;
    if (!TestNotNull(TEXT("Reloaded Script Actor owns its host component"), ActorComponent))
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }
    FString IdentityError;
    TestTrue(
        *FString::Printf(TEXT("Actor generated-class identity reloads exactly: %s"), *IdentityError),
        ActorAsset->ValidateGeneratedClassIdentity(
            ActorComponent,
            ScriptActor,
            IdentityError));
    IdentityError.Reset();
    TestTrue(
        *FString::Printf(TEXT("Component generated-class identity reloads exactly: %s"), *IdentityError),
        ComponentAsset->ValidateGeneratedClassIdentity(
            AttachedComponent,
            ComponentOwner,
            IdentityError));
    ActorComponent->BeginPlay();
    AttachedComponent->BeginPlay();
    TestTrue(
        *FString::Printf(TEXT("Actor Script Class enters managed BeginPlay: %s"),
            *ActorComponent->LastScriptError),
        ActorComponent->bScriptRunning);
    TestTrue(
        *FString::Printf(TEXT("Component Script Class enters managed BeginPlay: %s"),
            *AttachedComponent->LastScriptError),
        AttachedComponent->bScriptRunning);
    TestEqual(TEXT("Actor class materializes its editable StartValue"),
        ActorComponent->GetScriptStateInt32(TEXT("StartValue"), -1), 1);
    TestEqual(TEXT("Component class materializes its editable StartValue"),
        AttachedComponent->GetScriptStateInt32(TEXT("StartValue"), -1), 1);
    TestEqual(TEXT("Two generated Script Classes are live in one World"),
        Host->GetManagedScriptCount(), 2);

    Host->EndManagedFrame();
    Host->BeginManagedFrame();
    ActorComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
    AttachedComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
    TestTrue(
        *FString::Printf(TEXT("Actor Script Class completes managed Tick: %s"),
            *ActorComponent->LastScriptError),
        ActorComponent->bScriptRunning && ActorComponent->LastScriptError.IsEmpty());
    TestTrue(
        *FString::Printf(TEXT("Component Script Class completes managed Tick: %s"),
            *AttachedComponent->LastScriptError),
        AttachedComponent->bScriptRunning && AttachedComponent->LastScriptError.IsEmpty());

    ActorComponent->EndPlay(EEndPlayReason::Destroyed);
    AttachedComponent->EndPlay(EEndPlayReason::Destroyed);
    TestFalse(TEXT("Actor Script Class stops on EndPlay"), ActorComponent->bScriptRunning);
    TestFalse(TEXT("Component Script Class stops on EndPlay"), AttachedComponent->bScriptRunning);
    TestEqual(TEXT("EndPlay releases both managed instances"),
        Host->GetManagedScriptCount(), 0);
    TestEqual(TEXT("EndPlay releases both lifecycle claims"),
        Host->GetGameplayLifecycleClaimCount(), 0);

    AttachedComponent->UnregisterComponent();
    ScriptActor->Destroy();
    ComponentOwner->Destroy();
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    TestEqual(
        TEXT("World teardown releases its collectible managed context"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialManagedWorldCount);
    return WriteProcessReceipt(*this, TEXT("reload"));
}

#endif

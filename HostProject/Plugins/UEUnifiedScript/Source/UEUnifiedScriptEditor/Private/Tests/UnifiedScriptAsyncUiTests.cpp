#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptBlueprintClassMaterializer.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptInvocationBridge.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UEUnifiedScriptEditorModule.h"
#include "Blueprint/UserWidget.h"
#include "Components/Button.h"
#include "Components/StaticMeshComponent.h"
#include "Components/TextBlock.h"
#include "Editor.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "GameFramework/GameModeBase.h"
#include "HAL/FileManager.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "PackageTools.h"
#include "PlayInEditorDataTypes.h"
#include "Settings/LevelEditorPlaySettings.h"
#include "Tests/AutomationEditorCommon.h"
#include "UObject/SavePackage.h"
#include "UObject/UObjectIterator.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::AsyncUI::Tests
{
constexpr const TCHAR* PackageName = TEXT("/Game/UnifiedScriptDemo/BP_AsyncUiDemo");
constexpr const TCHAR* BlueprintPath = TEXT("/Game/UnifiedScriptDemo/BP_AsyncUiDemo.BP_AsyncUiDemo");

struct FScenario
{
    FAutomationTestBase& Test;
    TStrongObjectPtr<UUnifiedScriptBlueprint> Blueprint;
    int32 InitialWorlds = FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    bool bOwnsPackage = false;
    bool bStartedPIE = false;
    bool bPreviousPlay = false;
    explicit FScenario(FAutomationTestBase& InTest) : Test(InTest) {}
    ~FScenario()
    {
        if (bStartedPIE)
        {
            if (GEditor->PlayWorld) GEditor->EndPlayMap();
            IUEUnifiedScriptEditorModule::Get().SetPrecompiledAutomationPlay(bPreviousPlay);
        }
        Test.TestEqual(TEXT("UI managed World detached"), FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(), InitialWorlds);
        Blueprint.Reset();
        if (bOwnsPackage)
        {
            if (UPackage* Package = FindPackage(nullptr, PackageName))
            {
                Package->SetDirtyFlag(false);
                TArray<UPackage*> Packages{Package};
                UPackageTools::UnloadPackages(Packages);
            }
            IFileManager::Get().Delete(*FPackageName::LongPackageNameToFilename(
                PackageName, FPackageName::GetAssetPackageExtension()), false, true);
        }
    }

    bool Build()
    {
        FString Declaration;
        const FString DeclarationPath = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir() /
            TEXT("../artifacts/ue/compiled-chunks/release/UnifiedScript.Unreal.Demo.asset.json"));
        if (!Test.TestTrue(TEXT("Read emitted C# declaration"), FFileHelper::LoadFileToString(Declaration, *DeclarationPath))) return false;
        if (FindObject<UObject>(nullptr, BlueprintPath) != nullptr || FPackageName::DoesPackageExist(PackageName))
        {
            Blueprint.Reset(LoadObject<UUnifiedScriptBlueprint>(nullptr, BlueprintPath));
            FGuid ExpectedId;
            FGuid::ParseExact(TEXT("762043152e714d68b05a31426591ee03"), EGuidFormats::Digits, ExpectedId);
            if (!Test.TestTrue(TEXT("Reuse only the exact persistent Demo class without modifying it"),
                Blueprint.IsValid() && Blueprint->ScriptAsset && Blueprint->ScriptAsset->ScriptClassId == ExpectedId &&
                Blueprint->ScriptAsset->ScriptTypeName == TEXT("UnifiedScript.Unreal.Demo.AsyncUiDemoScript") &&
                Blueprint->ScriptAsset->GeneratedClassPath.ToString() == FString(BlueprintPath) + TEXT("_C"))) return false;
            FUnifiedScriptBlueprintClassSyncResult Result;
            FString Error;
            if (!FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                *Blueprint->ScriptAsset, *Blueprint, Declaration, true, Result, Error))
            {
                Test.AddError(Error);
                return false;
            }
            return Test.TestFalse(TEXT("Persistent Demo class matches emitted declarations; run the Demo preparer if stale"), Result.bChanged);
        }
        UPackage* Package = CreatePackage(PackageName);
        bOwnsPackage = true;
        Blueprint.Reset(Cast<UUnifiedScriptBlueprint>(FKismetEditorUtilities::CreateBlueprint(
            AUnifiedScriptActor::StaticClass(), Package, TEXT("BP_AsyncUiDemo"), BPTYPE_Normal,
            UUnifiedScriptBlueprint::StaticClass(), UUnifiedScriptBlueprintGeneratedClass::StaticClass())));
        if (!Test.TestNotNull(TEXT("Create UI Script Class"), Blueprint.Get())) return false;
        auto* Asset = NewObject<UUnifiedScriptAsset>(Package, TEXT("AsyncUiScript"), RF_Public | RF_Standalone);
        FGuid::ParseExact(TEXT("762043152e714d68b05a31426591ee03"), EGuidFormats::Digits, Asset->ScriptClassId);
        Asset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
        Asset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
        Asset->ScriptTypeName = TEXT("UnifiedScript.Unreal.Demo.AsyncUiDemoScript");
        Asset->DeclarationAssetPath = TEXT("Unreal/AsyncUiDemo");
        Asset->DeclarationPath = DeclarationPath;
        Asset->CompiledChunkPath = TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.uschunk");
        Asset->ScriptAssemblyPath = TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
        Blueprint->ScriptAsset = Asset;
        FUnifiedScriptBlueprintClassSyncResult Result;
        FString Error;
        if (!FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, Declaration, false, Result, Error))
        {
            Test.AddError(Error);
            return false;
        }
        auto* CDO = CastChecked<AUnifiedScriptActor>(Blueprint->GeneratedClass->GetDefaultObject());
        CDO->GetUnifiedScriptComponent()->ScriptAsset = Asset;
        FSavePackageArgs Save;
        Save.TopLevelFlags = RF_Public | RF_Standalone;
        Save.SaveFlags = SAVE_NoError;
        if (!Test.TestTrue(TEXT("Save UI Script Class"), UPackage::SavePackage(Package, Blueprint.Get(),
            *FPackageName::LongPackageNameToFilename(PackageName, FPackageName::GetAssetPackageExtension()), Save))) return false;
        Blueprint.Reset();
        Package->SetDirtyFlag(false);
        TArray<UPackage*> Packages{Package};
        if (!Test.TestTrue(TEXT("Unload saved UI class"), UPackageTools::UnloadPackages(Packages))) return false;
        Blueprint.Reset(LoadObject<UUnifiedScriptBlueprint>(nullptr, BlueprintPath));
        return Test.TestNotNull(TEXT("Reload saved UI class"), Blueprint.Get());
    }
};

class FRunUI final : public IAutomationLatentCommand
{
    TSharedPtr<FScenario> Scenario;
    TWeakObjectPtr<AUnifiedScriptActor> Actor;
    TWeakObjectPtr<UUserWidget> Hud;
    TWeakObjectPtr<UUserWidget> RetiredHud;
    TStrongObjectPtr<UButton> RetiredButton;
    double Started = FPlatformTime::Seconds();
    int32 Phase = 0;

    void MeshPath(const TCHAR* Value)
    {
        FindFProperty<FStrProperty>(Actor->GetClass(), TEXT("MeshPath"))->SetPropertyValue_InContainer(Actor.Get(), Value);
    }
    UButton* Button(const TCHAR* Name) { return CastChecked<UButton>(Hud->GetWidgetFromName(Name)); }
    int32 MeshCount(UWorld* World)
    {
        int32 Count = 0;
        for (TActorIterator<AStaticMeshActor> It(World); It; ++It) ++Count;
        return Count;
    }
public:
    explicit FRunUI(TSharedPtr<FScenario> InScenario) : Scenario(MoveTemp(InScenario)) {}
    virtual bool Update() override
    {
        auto& S = *Scenario;
        if (FPlatformTime::Seconds() - Started > 40)
        {
            S.Test.AddError(FString::Printf(TEXT("Async UI timed out in phase %d, state %s"), Phase,
                Actor.IsValid() ? *Actor->GetUnifiedScriptComponent()->ManagedState : TEXT("no Actor")));
            Scenario.Reset();
            return true;
        }
        UWorld* World = GEditor->PlayWorld;
        if (!World || !World->HasBegunPlay()) return false;
        if (Phase == 0)
        {
            FActorSpawnParameters Spawn;
            Spawn.bDeferConstruction = true;
            Actor = World->SpawnActor<AUnifiedScriptActor>(S.Blueprint->GeneratedClass, FTransform::Identity, Spawn);
            if (!S.Test.TestNotNull(TEXT("Spawn saved Script Class"), Actor.Get())) { Scenario.Reset(); return true; }
            // Fixture inputs belong to this instance, never the user's saved CDO.
            MeshPath(TEXT("/Engine/BasicShapes/Cube.Cube"));
            FindFProperty<FStrProperty>(Actor->GetClass(), TEXT("WidgetClassPath"))->SetPropertyValue_InContainer(
                Actor.Get(), TEXT("/Script/UEUnifiedScriptHost.UnifiedScriptAsyncDemoWidget"));
            Actor->FinishSpawning(FTransform::Identity);
            Phase = 1;
        }
        auto* Host = World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
        auto* Component = Actor->GetUnifiedScriptComponent();
        const FString State = Component->ManagedState;
        if (Phase == 1 || Phase == 7)
        {
            if (State != TEXT("UI:Ready")) return false;
            for (TObjectIterator<UUserWidget> It; It; ++It)
                if (It->GetWorld() == World && It->IsInViewport() && It->GetWidgetFromName(TEXT("SpawnButton"))) Hud = *It;
            if (!S.Test.TestNotNull(TEXT("Managed CreateWidget added the HUD to the viewport"), Hud.Get())) { Scenario.Reset(); return true; }
            S.Test.TestEqual(TEXT("Exactly two UI event subscriptions"), Host->GetEventSubscriptionCount(), 2);
            if (Phase == 7)
            {
                S.Test.TestFalse(TEXT("Retired view removed from viewport"), RetiredHud.IsValid() && RetiredHud->IsInViewport());
                RetiredButton->OnClicked.Broadcast();
                S.Test.TestEqual(TEXT("Retired delegate cannot start a request"), Host->GetPendingAsyncRequestCount(), 0);
                RetiredButton.Reset();
                CollectGarbage(RF_NoFlags);
                S.Test.TestFalse(TEXT("Retired widget is collectible"), RetiredHud.IsValid());
                S.Test.TestEqual(TEXT("Only replacement HUD class load is retained"), Host->GetOwnedAsyncResourceCount(), 1);
                Button(TEXT("SpawnButton"))->OnClicked.Broadcast();
                Actor->Destroy();
                S.Test.TestEqual(TEXT("Actor deletion cancels requests"), Host->GetPendingAsyncRequestCount(), 0);
                S.Test.TestEqual(TEXT("Actor deletion releases subscriptions"), Host->GetEventSubscriptionCount(), 0);
                S.Test.TestEqual(TEXT("Actor deletion releases async resources"), Host->GetOwnedAsyncResourceCount(), 0);
                S.Test.TestFalse(TEXT("Actor deletion removes HUD"), Hud.IsValid() && Hud->IsInViewport());
                if (!S.Test.HasAnyErrors()) S.Test.AddInfo(TEXT("PASS: generated UMG, cancel/retry, Actor replacement, reload and teardown."));
                Scenario.Reset();
                return true;
            }
            Button(TEXT("SpawnButton"))->OnClicked.Broadcast();
            Button(TEXT("SpawnButton"))->OnClicked.Broadcast();
            S.Test.TestEqual(TEXT("Duplicate click does not duplicate pending or completed request"),
                Host->GetPendingAsyncRequestCount() + Host->GetQueuedAsyncCompletionCount(), 1);
            S.Test.TestFalse(TEXT("Load button disabled while loading"), Button(TEXT("SpawnButton"))->GetIsEnabled());
            Button(TEXT("CancelButton"))->OnClicked.Broadcast();
            S.Test.TestEqual(TEXT("Cancel reported"), Component->ManagedState, FString(TEXT("UI:Canceled")));
            S.Test.TestEqual(TEXT("Cancel removed native request"), Host->GetPendingAsyncRequestCount(), 0);
            // A synchronously finished load may already have a terminal native row.
            // The next managed pump discards it because its request was canceled.
            Phase = 9;
            return false;
        }
        if (Phase == 9)
        {
            if (Host->GetQueuedAsyncCompletionCount() != 0) return false;
            S.Test.TestEqual(TEXT("Canceled completion cannot spawn an Actor"), MeshCount(World), 0);
            S.Test.TestEqual(TEXT("Canceled completion cannot overwrite UI state"), Component->ManagedState, FString(TEXT("UI:Canceled")));
            Button(TEXT("SpawnButton"))->OnClicked.Broadcast();
            Phase = 2;
            return false;
        }
        if (Phase == 2 && State == TEXT("UI:Spawned:1"))
        {
            S.Test.TestEqual(TEXT("One visible spawned Actor"), MeshCount(World), 1);
            for (TActorIterator<AStaticMeshActor> It(World); It; ++It)
            {
                S.Test.TestNotNull(TEXT("Async mesh assigned"), It->GetStaticMeshComponent()->GetStaticMesh().Get());
                S.Test.TestEqual(TEXT("Byte-wrapped mobility enum"), It->GetStaticMeshComponent()->Mobility.GetValue(), EComponentMobility::Movable);
            }
            S.Test.TestEqual(TEXT("UMG text matches gameplay state"),
                CastChecked<UTextBlock>(Hud->GetWidgetFromName(TEXT("StatusText")))->GetText().ToString(), FString(TEXT("Spawned:1")));
            MeshPath(TEXT("/Script/Engine.Actor")); // valid object, wrong asset type
            Phase = 3;
            return false;
        }
        if (Phase == 3) { Button(TEXT("SpawnButton"))->OnClicked.Broadcast(); Phase = 4; return false; }
        if (Phase == 4 && State.StartsWith(TEXT("UI:Error:")))
        {
            S.Test.TestEqual(TEXT("Failure preserves last successful Actor"), MeshCount(World), 1);
            S.Test.TestTrue(TEXT("Failure re-enables retry"), Button(TEXT("SpawnButton"))->GetIsEnabled());
            MeshPath(TEXT("/Engine/BasicShapes/Cube.Cube"));
            Phase = 5;
            return false;
        }
        if (Phase == 5) { Button(TEXT("SpawnButton"))->OnClicked.Broadcast(); Phase = 6; return false; }
        if (Phase == 6 && State == TEXT("UI:Spawned:2"))
        {
            S.Test.TestEqual(TEXT("Success replaces previous Actor"), MeshCount(World), 1);
            MeshPath(TEXT("/Engine/BasicShapes/Sphere.Sphere"));
            Phase = 8;
            return false;
        }
        if (Phase == 8)
        {
            Button(TEXT("SpawnButton"))->OnClicked.Broadcast();
            S.Test.TestEqual(TEXT("Reload occurs with an unconsumed resource request"),
                Host->GetPendingAsyncRequestCount() + Host->GetQueuedAsyncCompletionCount(), 1);
            RetiredHud = Hud;
            RetiredButton.Reset(Button(TEXT("SpawnButton")));
            S.Test.TestTrue(TEXT("Compatible generation reload"), Component->ReloadScript());
            S.Test.TestEqual(TEXT("Reload releases old preview Actor"), MeshCount(World), 0);
            Hud.Reset();
            Phase = 7;
        }
        return false;
    }
};
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FUnifiedScriptAsyncUiTest, "UnifiedScript.Unreal.UI.AsyncGameplay",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptAsyncUiTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::AsyncUI::Tests;
    if (!TestNull(TEXT("No existing Play session"), GEditor->PlayWorld)) return false;
    auto Scenario = MakeShared<FScenario>(*this);
    if (!Scenario->Build()) return false;
    FAutomationEditorCommonUtils::CreateNewMap();
    auto* Settings = NewObject<ULevelEditorPlaySettings>();
    Settings->SetPlayNetMode(EPlayNetMode::PIE_Standalone);
    Settings->SetPlayNumberOfClients(1);
    Settings->SetRunUnderOneProcess(true);
    Settings->GameGetsMouseControl = false;
    FRequestPlaySessionParams Session;
    Session.EditorPlaySettings = Settings;
    Session.GameModeOverride = AGameModeBase::StaticClass();
    Session.bAllowOnlineSubsystem = false;
    Scenario->bPreviousPlay = IUEUnifiedScriptEditorModule::Get().SetPrecompiledAutomationPlay(true);
    GEditor->RequestPlaySession(Session);
    Scenario->bStartedPIE = true;
    ADD_LATENT_AUTOMATION_COMMAND(FRunUI(Scenario));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FUnifiedScriptEmptyDelegateTest, "UnifiedScript.Unreal.UI.EmptyDelegateParameters",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptEmptyDelegateTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Delegate test World"), World)) return false;
    {
        FUnifiedScriptObjectRegistry Registry(World);
        FUnifiedScriptInvocationBridge Bridge(Registry);
        auto* Empty = FindFProperty<FMulticastDelegateProperty>(UButton::StaticClass(), TEXT("OnClicked"));
        auto* NonEmpty = FindFProperty<FMulticastDelegateProperty>(UUnifiedScriptComponent::StaticClass(), TEXT("OnManagedSignal"));
        TArray<FUnifiedScriptValue> Arguments;
        FUnifiedScriptInvocationResult Result;
        TestTrue(TEXT("Zero-parameter event accepts null UE parameter block"),
            Bridge.ReadDelegateArguments(Empty->SignatureFunction, nullptr, Arguments, Result));
        TestEqual(TEXT("Zero-parameter event has no values"), Arguments.Num(), 0);
        TestFalse(TEXT("Non-empty event still requires its parameter block"),
            Bridge.ReadDelegateArguments(NonEmpty->SignatureFunction, nullptr, Arguments, Result));
        TestFalse(TEXT("Missing signature is rejected"), Bridge.ReadDelegateArguments(nullptr, nullptr, Arguments, Result));
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FUnifiedScriptByteEnumTest, "UnifiedScript.Unreal.UI.GeneratedByteEnum",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptByteEnumTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Enum test World"), World)) return false;
    {
        FUnifiedScriptObjectRegistry Registry(World);
        FUnifiedScriptInvocationBridge Bridge(Registry);
        auto* Actor = World->SpawnActor<AStaticMeshActor>();
        FUnifiedScriptObjectHandleResult HandleResult;
        const auto Handle = Registry.RegisterObject(Actor, HandleResult);
        FUnifiedScriptInvocationResult Result;
        FUnifiedScriptValue Returned;
        constexpr uint64 Member = UnifiedScriptMemberId("/Script/Engine.StaticMeshActor:SetMobility");
        constexpr uint64 Enum = UnifiedScriptMemberId("/Script/Engine.EComponentMobility");
        for (int64 Invalid : { -1ll, 256ll })
        {
            const TArray<FUnifiedScriptValue> Arguments{FUnifiedScriptValue::FromEnumTypeId(Invalid, Enum)};
            TestFalse(TEXT("Byte enum rejects overflow before native mutation"), Bridge.InvokeById(Handle, Member, Arguments, Returned, Result));
            TestEqual(TEXT("Rejected enum preserves mobility"), Actor->GetStaticMeshComponent()->Mobility.GetValue(), EComponentMobility::Static);
        }
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}
#endif

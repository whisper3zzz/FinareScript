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
#include "Editor.h"
#include "FileHelpers.h"
#include "Engine/Engine.h"
#include "Engine/NetConnection.h"
#include "Engine/NetDriver.h"
#include "EngineUtils.h"
#include "GameFramework/GameModeBase.h"
#include "GameFramework/PlayerController.h"
#include "HAL/FileManager.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "PackageTools.h"
#include "PlayInEditorDataTypes.h"
#include "Settings/LevelEditorPlaySettings.h"
#include "Tests/AutomationEditorCommon.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::Networking::Tests
{
constexpr const TCHAR* MapName = TEXT("/Game/UnifiedScriptTests/NetworkingMap");

struct FScenario
{
    FAutomationTestBase& Test;
    TStrongObjectPtr<UUnifiedScriptBlueprint> Blueprint;
    int32 InitialWorldCount = FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    bool bOwnsPackage = false;
    bool bOwnsMap = false;
    bool bStartedPIE = false;
    bool bPreviousPrecompiledPlay = false;
    const bool bComponent;
    const FString ClassName;
    const FString PackageName;

    explicit FScenario(FAutomationTestBase& InTest, bool bInComponent = false)
        : Test(InTest), bComponent(bInComponent),
          ClassName(bInComponent ? TEXT("BP_NetworkingComponentDemo") : TEXT("BP_NetworkingDemo")),
          PackageName(TEXT("/Game/UnifiedScriptTests/") + ClassName) {}
    bool ValidateInitialNotifyLifetime();
    ~FScenario()
    {
        if (bStartedPIE && GEditor)
        {
            if (GEditor->PlayWorld) GEditor->EndPlayMap();
            IUEUnifiedScriptEditorModule::Get().SetPrecompiledAutomationPlay(bPreviousPrecompiledPlay);
        }
        Test.TestEqual(TEXT("All networking managed Worlds detach"),
            FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(), InitialWorldCount);
        if (bOwnsMap)
        {
            FAutomationEditorCommonUtils::CreateNewMap();
            IFileManager::Get().Delete(*FPackageName::LongPackageNameToFilename(
                MapName, FPackageName::GetMapPackageExtension()), false, true);
        }
        Blueprint.Reset();
        if (bOwnsPackage)
        {
            if (UPackage* Package = FindPackage(nullptr, *PackageName))
            {
                Package->SetDirtyFlag(false);
                TArray<UPackage*> Packages{Package};
                UPackageTools::UnloadPackages(Packages);
            }
            IFileManager::Get().Delete(*FPackageName::LongPackageNameToFilename(
                PackageName, FPackageName::GetAssetPackageExtension()), false, true);
        }
    }

    UUnifiedScriptComponent* Component(UObject* Object) const
    {
        return bComponent ? Cast<UUnifiedScriptComponent>(Object) :
            CastChecked<AUnifiedScriptActor>(Object)->GetUnifiedScriptComponent();
    }

    AActor* Owner(UObject* Object) const
    {
        return bComponent ? Component(Object)->GetOwner() : CastChecked<AActor>(Object);
    }

    FString TextValue(UObject* Object, const TCHAR* Name) const
    {
        const FStrProperty* Property = FindFProperty<FStrProperty>(Object->GetClass(), Name);
        return Property ? Property->GetPropertyValue_InContainer(Object) : TEXT("<missing>");
    }

    UFunction* Function(const TCHAR* ManagedName) const
    {
        auto* Class = Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
        for (const auto& Pair : Class->ManagedFunctionBindings)
            if (Pair.Value.ManagedName == ManagedName) return Class->FindFunctionByName(Pair.Key);
        return nullptr;
    }

    int32 Value(UObject* Object, const TCHAR* Name) const
    {
        const FIntProperty* Property = FindFProperty<FIntProperty>(Object->GetClass(), Name);
        return Property ? Property->GetPropertyValue_InContainer(Object) : MIN_int32;
    }

    void Call(UObject* Object, const TCHAR* Name, int32 Argument)
    {
        UFunction* Method = Function(Name);
        FStructOnScope Parameters(Method);
        for (TFieldIterator<FIntProperty> It(Method); It; ++It)
            if (It->HasAnyPropertyFlags(CPF_Parm)) It->SetPropertyValue_InContainer(Parameters.GetStructMemory(), Argument);
        Object->ProcessEvent(Method, Parameters.GetStructMemory());
    }

    bool Build()
    {
        if (!Test.TestFalse(TEXT("Fixture must not overwrite an existing asset"),
            FindObject<UUnifiedScriptBlueprint>(nullptr, *(PackageName + TEXT(".") + ClassName)) != nullptr ||
            FindObject<UUnifiedScriptAsset>(nullptr, *(PackageName + TEXT(".NetworkingScript"))) != nullptr ||
            FPackageName::DoesPackageExist(PackageName))) return false;
        FString Declaration;
        const FString DeclarationPath = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir() /
            TEXT("../artifacts/ue/compiled-chunks/release/UnifiedScript.Unreal.Demo.asset.json"));
        if (!Test.TestTrue(TEXT("Load declarations emitted from the actual C# sample"),
            FFileHelper::LoadFileToString(Declaration, *DeclarationPath))) return false;
        UPackage* Package = CreatePackage(*PackageName);
        bOwnsPackage = true;
        Blueprint.Reset(Cast<UUnifiedScriptBlueprint>(FKismetEditorUtilities::CreateBlueprint(
            bComponent ? UUnifiedScriptComponent::StaticClass() : AUnifiedScriptActor::StaticClass(), Package, FName(*ClassName), BPTYPE_Normal,
            UUnifiedScriptBlueprint::StaticClass(), UUnifiedScriptBlueprintGeneratedClass::StaticClass())));
        if (!Test.TestNotNull(TEXT("Create generated networking Actor"), Blueprint.Get())) return false;
        UUnifiedScriptAsset* Asset = NewObject<UUnifiedScriptAsset>(Package, TEXT("NetworkingScript"), RF_Public | RF_Standalone);
        FGuid::ParseExact(bComponent ? TEXT("762043152e714d68b05a31426591ee02") : TEXT("762043152e714d68b05a31426591ee01"), EGuidFormats::Digits, Asset->ScriptClassId);
        Asset->ScriptClassKind = bComponent ? EUnifiedScriptClassKind::ActorComponent : EUnifiedScriptClassKind::Actor;
        Asset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
        Asset->ScriptTypeName = bComponent ? TEXT("UnifiedScript.Unreal.Demo.NetworkingComponentDemoScript") : TEXT("UnifiedScript.Unreal.Demo.NetworkingDemoScript");
        Asset->DeclarationAssetPath = bComponent ? TEXT("Unreal/NetworkingComponentDemo") : TEXT("Unreal/NetworkingDemo");
        Asset->DeclarationPath = DeclarationPath;
        Asset->CompiledChunkPath = TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.uschunk");
        Asset->ScriptAssemblyPath = TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
        Blueprint->ScriptAsset = Asset;
        FUnifiedScriptBlueprintClassSyncResult Result;
        FString Error;
        if (!Test.TestTrue(*FString::Printf(TEXT("Materialize networking declarations: %s"), *Error),
            FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, Declaration, false, Result, Error)))
        {
            Test.AddError(Error);
            return false;
        }
        auto* Class = Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
        UObject* CDO = Class->GetDefaultObject();
        Test.TestTrue(TEXT("Network declarations enable replication"), bComponent ? Component(CDO)->GetIsReplicated() : Owner(CDO)->GetIsReplicated());
        if (!bComponent) Owner(CDO)->bAlwaysRelevant = true;
        Component(CDO)->ScriptAsset = Asset;
        Component(CDO)->bTickAutomatically = false;
        for (const TCHAR* Name : {TEXT("ServerAdd"), TEXT("ClientAccepted"), TEXT("AllObserved")})
        {
            UFunction* Method = Function(Name);
            if (!Test.TestNotNull(Name, Method)) return false;
            Test.TestTrue(TEXT("RPC is native, reliable and networked"), Method->HasAllFunctionFlags(FUNC_Native | FUNC_Net | FUNC_NetReliable));
        }
        Test.TestTrue(TEXT("Server direction"), Function(TEXT("ServerAdd"))->HasAnyFunctionFlags(FUNC_NetServer));
        Test.TestTrue(TEXT("Client direction"), Function(TEXT("ClientAccepted"))->HasAnyFunctionFlags(FUNC_NetClient));
        Test.TestTrue(TEXT("Multicast direction"), Function(TEXT("AllObserved"))->HasAnyFunctionFlags(FUNC_NetMulticast));
        auto* Score = FindFProperty<FIntProperty>(Class, TEXT("Score"));
        auto* Secret = FindFProperty<FIntProperty>(Class, TEXT("OwnerSecret"));
        if (!Test.TestNotNull(TEXT("Replicated Score"), Score) || !Test.TestNotNull(TEXT("Owner-only state"), Secret)) return false;
        Test.TestTrue(TEXT("Score replicates with notification"), Score->HasAllPropertyFlags(CPF_Net | CPF_RepNotify));
        Test.TestEqual(TEXT("RepNotify uses stable generated function identity"), Score->RepNotifyFunc, Function(TEXT("ScoreChanged"))->GetFName());
        Test.TestEqual(TEXT("Owner-only condition reaches compiled property"), Secret->GetBlueprintReplicationCondition(), COND_OwnerOnly);
        if (!Test.TestTrue(TEXT("Unchanged declarations validate"),
            FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, Declaration, true, Result, Error))) return false;
        const int32 PropertyCount = Blueprint->NewVariables.Num();
        UClass* PublishedClass = Blueprint->GeneratedClass;
        for (const FString& Invalid : {
            Declaration.Replace(TEXT("\"repNotifyProperty\": \"Score\""), TEXT("\"repNotifyProperty\": \"Missing\"")),
            Declaration.Replace(TEXT("\"target\": \"server\""), TEXT("\"target\": \"invalid\"")),
            Declaration.Replace(TEXT("\"replicationCondition\": \"ownerOnly\""), TEXT("\"replicationCondition\": \"invalid\""))})
        {
            Test.TestNotEqual(TEXT("Mutation changes the network contract"), Invalid, Declaration);
            Test.TestFalse(TEXT("Invalid network contract cannot publish"),
                FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, Invalid, false, Result, Error));
            Test.TestFalse(TEXT("Invalid contract explains the failure"), Error.IsEmpty());
            Test.TestEqual(TEXT("Failure preserves generated class"), Blueprint->GeneratedClass.Get(), PublishedClass);
            Test.TestEqual(TEXT("Failure preserves property set"), Blueprint->NewVariables.Num(), PropertyCount);
        }
        FSavePackageArgs Save;
        Save.TopLevelFlags = RF_Public | RF_Standalone;
        Save.SaveFlags = SAVE_NoError;
        Test.TestTrue(TEXT("Save runtime networking defaults"), UPackage::SavePackage(Package, Blueprint.Get(),
            *FPackageName::LongPackageNameToFilename(PackageName, FPackageName::GetAssetPackageExtension()), Save));
        Blueprint.Reset();
        Package->SetDirtyFlag(false);
        TArray<UPackage*> Packages{Package};
        if (!Test.TestTrue(TEXT("Unload generated networking class before runtime"), UPackageTools::UnloadPackages(Packages))) return false;
        Blueprint.Reset(LoadObject<UUnifiedScriptBlueprint>(nullptr, *(PackageName + TEXT(".") + ClassName)));
        if (!Test.TestNotNull(TEXT("Reload persisted networking class"), Blueprint.Get())) return false;
        CDO = Blueprint->GeneratedClass->GetDefaultObject();
        Test.TestTrue(TEXT("Replication default survives reload"), bComponent ? Component(CDO)->GetIsReplicated() : Owner(CDO)->GetIsReplicated());
        Test.TestEqual(TEXT("RepNotify binding survives reload"),
            FindFProperty<FIntProperty>(Blueprint->GeneratedClass, TEXT("Score"))->RepNotifyFunc, Function(TEXT("ScoreChanged"))->GetFName());
        return !Test.HasAnyErrors();
    }
};

// Runs actual UE client connections and actor channels in CMD/NullRHI. No role
// spoofing or manually copied replicated values stand in for network delivery.
class FConnectedScenario final : public IAutomationLatentCommand
{
    TSharedPtr<FScenario> Scenario;
    double Started = FPlatformTime::Seconds();
    int32 Phase = 0;
    TWeakObjectPtr<AActor> ServerActor;
    TWeakObjectPtr<UObject> ServerScript;
    TArray<TWeakObjectPtr<UObject>> Clients;
public:
    explicit FConnectedScenario(TSharedPtr<FScenario> InScenario) : Scenario(MoveTemp(InScenario)) {}
    virtual bool Update() override
    {
        auto& S = *Scenario;
        if (FPlatformTime::Seconds() - Started > 45.0)
        {
            S.Test.AddError(FString::Printf(TEXT("Connected networking scenario timed out in phase %d"), Phase));
            Scenario.Reset();
            return true;
        }
        UWorld* Server = nullptr;
        TArray<UWorld*> ClientWorlds;
        for (const FWorldContext& Context : GEngine->GetWorldContexts())
        {
            UWorld* World = Context.World();
            if (Context.WorldType != EWorldType::PIE || !World || !World->GetNetDriver()) continue;
            if (World->GetNetDriver()->IsServer()) Server = World;
            else ClientWorlds.Add(World);
        }
        if (!Server || ClientWorlds.Num() != 2 || Server->GetNetDriver()->ClientConnections.Num() != 2) return false;
        for (UNetConnection* Connection : Server->GetNetDriver()->ClientConnections)
            if (!Connection->PlayerController || !Connection->ViewTarget) return false;

        if (Phase == 0)
        {
            FActorSpawnParameters Params;
            Params.Owner = Server->GetNetDriver()->ClientConnections[0]->PlayerController;
            Params.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
            ServerActor = Server->SpawnActor<AActor>(S.bComponent ? AActor::StaticClass() : S.Blueprint->GeneratedClass.Get(), Params);
            if (!S.Test.TestNotNull(TEXT("Spawn server Actor"), ServerActor.Get())) { Scenario.Reset(); return true; }
            if (S.bComponent)
            {
                ServerActor->SetReplicates(true);
                ServerActor->bAlwaysRelevant = true;
                auto* Component = NewObject<UUnifiedScriptComponent>(ServerActor.Get(), S.Blueprint->GeneratedClass, TEXT("NetworkBehavior"));
                ServerActor->AddInstanceComponent(Component);
                Component->RegisterComponent();
                ServerScript = Component;
            }
            else ServerScript = ServerActor.Get();
            if (!S.Test.TestTrue(*S.Component(ServerScript.Get())->LastScriptError,
                S.Component(ServerScript.Get())->bScriptRunning)) { Scenario.Reset(); return true; }
            S.Call(ServerScript.Get(), TEXT("InitializeScore"), 10);
            ServerActor->ForceNetUpdate();
            Phase = 1;
            S.Test.AddInfo(TEXT("Server spawned generated Actor; waiting for replication on two clients."));
        }
        Clients.Reset();
        for (UWorld* World : ClientWorlds)
        {
            if (S.bComponent)
            {
                for (TActorIterator<AActor> It(World); It; ++It)
                {
                    TInlineComponentArray<UUnifiedScriptComponent*> Components(*It);
                    for (auto* Component : Components)
                        if (Component->GetClass() == S.Blueprint->GeneratedClass) Clients.Add(Component);
                }
            }
            else for (TActorIterator<AUnifiedScriptActor> It(World, S.Blueprint->GeneratedClass.Get()); It; ++It) Clients.Add(*It);
        }
        if (Clients.Num() != 2) return false;
        UObject* Owner = nullptr;
        UObject* Observer = nullptr;
        for (auto Client : Clients)
        {
            if (!S.Component(Client.Get())->bScriptRunning) return false;
            if (S.Owner(Client.Get())->HasLocalNetOwner()) Owner = Client.Get(); else Observer = Client.Get();
        }
        if (!Owner || !Observer) return false;
        if (Phase == 1)
        {
            if (S.Value(Owner, TEXT("Score")) != 10 || S.Value(Observer, TEXT("Score")) != 10 ||
                S.Value(Owner, TEXT("OwnerSecret")) != 100) return false;
            S.Test.TestEqual(TEXT("Owner-only value is withheld from observer"), S.Value(Observer, TEXT("OwnerSecret")), 0);
            for (auto Client : Clients)
            {
                S.Test.TestEqual(TEXT("Initial RepNotify runs once"), S.Value(Client.Get(), TEXT("Notifications")), 1);
                S.Test.TestEqual(TEXT("Initial RepNotify receives previous value"), S.Value(Client.Get(), TEXT("PreviousScore")), 0);
                if (S.bComponent)
                {
                    S.Test.TestEqual(TEXT("Initial string value"), S.TextValue(Client.Get(), TEXT("Label")), FString(TEXT("Ready")));
                    S.Test.TestEqual(TEXT("Initial string RepNotify owns the previous value"), S.TextValue(Client.Get(), TEXT("PreviousLabel")), FString());
                    S.Test.TestEqual(TEXT("Initial string notification runs once"), S.Value(Client.Get(), TEXT("LabelNotifications")), 1);
                    S.Test.TestEqual(TEXT("InitialOnly sends the initial component value"), S.Value(Client.Get(), TEXT("InitialScore")), 10);
                    S.Test.TestEqual(TEXT("InitialOrOwner initially reaches both clients"), S.Value(Client.Get(), TEXT("InitialOrOwnerScore")), 10);
                }
            }
            if (S.bComponent)
            {
                S.Test.TestEqual(TEXT("SkipOwner hides initial component state from owner"), S.Value(Owner, TEXT("ObserverScore")), 0);
                S.Test.TestEqual(TEXT("SkipOwner delivers initial component state to observer"), S.Value(Observer, TEXT("ObserverScore")), 10);
            }
            // A normal local method tries to edit replicated state: client flush
            // must restore authority's values rather than contaminate the mirror.
            S.Call(Owner, TEXT("InitializeScore"), 999);
            S.Test.TestEqual(TEXT("Client cannot overwrite authoritative state"), S.Value(Owner, TEXT("Score")), 10);
            {
                FUnifiedScriptObjectRegistry Registry(Observer->GetWorld());
                FUnifiedScriptInvocationBridge Bridge(Registry);
                FUnifiedScriptObjectHandleResult HandleResult;
                const auto Handle = Registry.RegisterObject(Observer, HandleResult);
                FUnifiedScriptInvocationResult Result;
                FUnifiedScriptValue Return;
                TArray<FUnifiedScriptValue> Args{FUnifiedScriptValue::FromInt32(7)};
                const FString ClassPath = S.Blueprint->GeneratedClass->GetPathName();
                S.Test.TestFalse(TEXT("Non-owning client cannot send Server RPC"),
                    Bridge.Invoke(Handle, ClassPath + TEXT(":ServerAdd"), Args, Return, Result));
                S.Test.TestEqual(TEXT("Ownership rejection is explicit"), Result.Error, EUnifiedScriptInvocationError::FunctionNotCallable);
                S.Test.TestFalse(TEXT("Client cannot originate Multicast RPC"),
                    Bridge.Invoke(Handle, ClassPath + TEXT(":AllObserved"), Args, Return, Result));
                S.Test.TestFalse(TEXT("Direct bridge writes also enforce replication authority"),
                    Bridge.WriteProperty(Handle, ClassPath + TEXT(":Score"), Args[0], Result));
                S.Test.TestEqual(TEXT("Replicated write rejection is explicit"), Result.Error, EUnifiedScriptInvocationError::PropertyNotWritable);
            }
            if (S.bComponent)
            {
                FUnifiedScriptObjectRegistry Registry(Owner->GetWorld());
                FUnifiedScriptInvocationBridge Bridge(Registry);
                FUnifiedScriptObjectHandleResult HandleResult;
                const auto Handle = Registry.RegisterObject(Owner, HandleResult);
                FUnifiedScriptInvocationResult Result;
                FUnifiedScriptValue Return;
                S.Component(Owner)->SetIsReplicated(false);
                S.Test.TestFalse(TEXT("A non-replicating component cannot send even with an owned replicated Actor"),
                    Bridge.Invoke(Handle, S.Blueprint->GeneratedClass->GetPathName() + TEXT(":ServerPing"), {}, Return, Result));
                S.Test.TestEqual(TEXT("Non-replicating component rejection is explicit"), Result.Error, EUnifiedScriptInvocationError::FunctionNotCallable);
                S.Component(Owner)->SetIsReplicated(true);
            }
            S.Call(Owner, TEXT("RequestAdd"), 7);
            Phase = 2;
            S.Test.AddInfo(TEXT("Owning client sent Server RPC through C# SendRpc."));
        }
        if (Phase == 2 || Phase == 3)
        {
            const int32 Expected = Phase == 2 ? 17 : 20;
            if (S.Value(ServerScript.Get(), TEXT("Score")) != Expected || S.Value(Owner, TEXT("Score")) != Expected ||
                S.Value(Observer, TEXT("Score")) != Expected || S.Value(Owner, TEXT("AcknowledgedScore")) != Expected ||
                S.Value(Owner, TEXT("BroadcastScore")) != Expected || S.Value(Observer, TEXT("BroadcastScore")) != Expected ||
                S.Value(ServerScript.Get(), TEXT("BroadcastScore")) != Expected ||
                S.Value(Owner, TEXT("OwnerSecret")) != Expected * 10) return false;
            S.Test.TestEqual(TEXT("Client RPC reaches only owner"), S.Value(Observer, TEXT("AcknowledgedScore")), 0);
            S.Test.TestEqual(TEXT("Replication retains owner-only audience"), S.Value(Observer, TEXT("OwnerSecret")), 0);
            for (auto Client : Clients)
            {
                S.Test.TestEqual(TEXT("RepNotify counts real changes"), S.Value(Client.Get(), TEXT("Notifications")), Phase);
                S.Test.TestEqual(TEXT("RepNotify receives preceding replicated value"), S.Value(Client.Get(), TEXT("PreviousScore")), Phase == 2 ? 10 : 17);
            }
            if (S.bComponent)
            {
                const FString Label = Phase == 2 ? TEXT("First") : TEXT("Second");
                for (auto Client : Clients)
                {
                    S.Test.TestEqual(TEXT("InitialOnly withholds later component changes"), S.Value(Client.Get(), TEXT("InitialScore")), 10);
                    S.Test.TestEqual(TEXT("Zero-input RPC updates replicated component state"), S.Value(Client.Get(), TEXT("Pings")), Phase - 1);
                    S.Test.TestEqual(TEXT("Replicated string reaches both clients"), S.TextValue(Client.Get(), TEXT("Label")), Label);
                    S.Test.TestEqual(TEXT("String RepNotify preserves previous data"), S.TextValue(Client.Get(), TEXT("PreviousLabel")), FString(Phase == 2 ? TEXT("Ready") : TEXT("First")));
                    S.Test.TestEqual(TEXT("String notifications include reload"), S.Value(Client.Get(), TEXT("LabelNotifications")), Phase);
                    S.Test.TestEqual(TEXT("Two-input multicast carries float payload"),
                        FindFProperty<FFloatProperty>(Client->GetClass(), TEXT("BroadcastGain"))->GetPropertyValue_InContainer(Client.Get()), 1.5f);
                }
                S.Test.TestEqual(TEXT("InitialOrOwner keeps updating owner"), S.Value(Owner, TEXT("InitialOrOwnerScore")), Expected);
                S.Test.TestEqual(TEXT("InitialOrOwner preserves observer's initial value"), S.Value(Observer, TEXT("InitialOrOwnerScore")), 10);
                S.Test.TestEqual(TEXT("SkipOwner keeps the owning mirror unchanged"), S.Value(Owner, TEXT("ObserverScore")), 0);
                S.Test.TestEqual(TEXT("SkipOwner keeps updating observer"), S.Value(Observer, TEXT("ObserverScore")), Expected);
                S.Test.TestEqual(TEXT("Authority still changes its InitialOnly value"), S.Value(ServerScript.Get(), TEXT("InitialScore")), Expected);
                S.Test.TestEqual(TEXT("Three-input client RPC carries string"), S.TextValue(Owner, TEXT("AcknowledgedLabel")), Label);
                S.Test.TestEqual(TEXT("Object RPC resolves to the owning client's Actor, not a server handle"),
                    FindFProperty<FObjectPropertyBase>(Owner->GetClass(), TEXT("AcknowledgedActor"))->GetObjectPropertyValue_InContainer(Owner), static_cast<UObject*>(S.Owner(Owner)));
                S.Test.TestEqual(TEXT("Four-input server RPC carries float"),
                    FindFProperty<FFloatProperty>(ServerScript->GetClass(), TEXT("Gain"))->GetPropertyValue_InContainer(ServerScript.Get()), 1.5f);
                S.Test.TestTrue(TEXT("Four-input server RPC carries bool"),
                    FindFProperty<FBoolProperty>(ServerScript->GetClass(), TEXT("Enabled"))->GetPropertyValue_InContainer(ServerScript.Get()));
                S.Test.TestEqual(TEXT("Four-input server RPC carries Vector"),
                    *FindFProperty<FStructProperty>(ServerScript->GetClass(), TEXT("Aim"))->ContainerPtrToValuePtr<FVector>(ServerScript.Get()), FVector(1, 2, 3));
            }
            if (Phase == 2)
            {
                S.Test.TestTrue(TEXT("Reload owning client's managed generation"), S.Component(Owner)->ReloadScript());
                S.Call(Owner, TEXT("RequestAdd"), 3);
                Phase = 3;
                return false;
            }
            if (!S.Test.HasAnyErrors() && S.bComponent)
                S.Test.AddInfo(TEXT("PASS: dynamic ActorComponent replication, all five exposed conditions, string RepNotify, 0/2/3/4-input RPC with int/float/bool/Vector/string/object payloads, ownership and reload."));
            else if (!S.Test.HasAnyErrors())
                S.Test.AddInfo(TEXT("PASS: two connected clients, replicated/owner-only state, RepNotify old values, Server/Client/Multicast RPC and reload."));
            Scenario.Reset();
            return true;
        }
        return false;
    }
};
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FUnifiedScriptConnectedNetworkingTest,
    "UnifiedScript.Unreal.Networking.ConnectedScriptClass",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FUnifiedScriptConnectedComponentNetworkingTest,
    "UnifiedScript.Unreal.Networking.ConnectedComponent",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

static bool RunNetworkingScenario(FAutomationTestBase& Test, bool bComponent)
{
    using namespace UE::UnifiedScript::Networking::Tests;
    if (!Test.TestNotNull(TEXT("CMD editor engine available"), GEditor) ||
        !Test.TestNull(TEXT("Networking test must not interrupt an existing PIE session"), GEditor->PlayWorld)) return false;
    auto Scenario = MakeShared<FScenario>(Test, bComponent);
    if (!Scenario->Build()) return false;
    if (!bComponent && !Scenario->ValidateInitialNotifyLifetime()) return false;
    if (!Test.TestFalse(TEXT("Fixture must not overwrite an existing map"), FPackageName::DoesPackageExist(MapName))) return false;
    UWorld* Map = FAutomationEditorCommonUtils::CreateNewMap();
    Scenario->bOwnsMap = true;
    if (!Test.TestTrue(TEXT("Save a real map for replicated level identity"), UEditorLoadingAndSavingUtils::SaveMap(Map, MapName))) return false;
    auto* Settings = NewObject<ULevelEditorPlaySettings>();
    Settings->SetPlayNetMode(EPlayNetMode::PIE_Client);
    Settings->SetPlayNumberOfClients(2);
    Settings->bLaunchSeparateServer = true;
    Settings->GameGetsMouseControl = false;
    Settings->SetRunUnderOneProcess(true);
    FRequestPlaySessionParams Session;
    Session.EditorPlaySettings = Settings;
    Session.GameModeOverride = AGameModeBase::StaticClass();
    Session.bAllowOnlineSubsystem = false;
    Session.GlobalMapOverride = MapName;
    // This is a networking runtime test using explicitly emitted inputs, not
    // a test of the separate daily-build coordinator or its source mutations.
    Scenario->bPreviousPrecompiledPlay = IUEUnifiedScriptEditorModule::Get().SetPrecompiledAutomationPlay(true);
    GEditor->RequestPlaySession(Session);
    Scenario->bStartedPIE = true;
    ADD_LATENT_AUTOMATION_COMMAND(FConnectedScenario(Scenario));
    return true;
}

bool FUnifiedScriptConnectedNetworkingTest::RunTest(const FString& Parameters)
{
    return RunNetworkingScenario(*this, false);
}

bool FUnifiedScriptConnectedComponentNetworkingTest::RunTest(const FString& Parameters)
{
    return RunNetworkingScenario(*this, true);
}

bool UE::UnifiedScript::Networking::Tests::FScenario::ValidateInitialNotifyLifetime()
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!Test.TestNotNull(TEXT("Create initial-notify World"), World)) return false;
    World->AddToRoot();
    World->InitializeActorsForPlay(FURL());
    auto* Actor = World->SpawnActor<AUnifiedScriptActor>(Blueprint->GeneratedClass);
    auto* Canceled = World->SpawnActor<AUnifiedScriptActor>(Blueprint->GeneratedClass);
    bool Ready = Test.TestNotNull(TEXT("Create pending Actor"), Actor) && Test.TestNotNull(TEXT("Create canceled Actor"), Canceled);
    if (Ready)
    {
        UFunction* Notify = Function(TEXT("ScoreChanged"));
        {
            FStructOnScope OldValue(Notify);
            auto* Property = FindFProperty<FIntProperty>(Notify, TEXT("previous"));
            Property->SetPropertyValue_InContainer(OldValue.GetStructMemory(), 42);
            Test.TestTrue(TEXT("Queue owns initial previous value"), Actor->GetUnifiedScriptComponent()->QueueInitialRepNotify(Actor, Notify, OldValue.GetStructMemory()));
            Test.TestTrue(TEXT("Queue a later-canceled initial value"), Canceled->GetUnifiedScriptComponent()->QueueInitialRepNotify(Canceled, Notify, OldValue.GetStructMemory()));
            Property->SetPropertyValue_InContainer(OldValue.GetStructMemory(), 73);
            Test.TestTrue(TEXT("Pre-start changes coalesce"), Actor->GetUnifiedScriptComponent()->QueueInitialRepNotify(Actor, Notify, OldValue.GetStructMemory()));
        }
        // The source parameter block is gone. The queue must survive GC without
        // a raw parameter address or unreported function/object reference.
        CollectGarbage(RF_NoFlags);
        Canceled->StopManagedBehavior();
        Test.TestTrue(TEXT("Initial pending notification survives activation"), Actor->StartManagedBehavior());
        Test.TestEqual(TEXT("Initial queue preserves first previous value"), Value(Actor, TEXT("PreviousScore")), 42);
        Test.TestEqual(TEXT("Initial queue delivers once"), Value(Actor, TEXT("Notifications")), 1);
        Test.TestTrue(TEXT("Canceled Actor can start without replaying old notifications"), Canceled->StartManagedBehavior());
        Test.TestEqual(TEXT("Stop discards pending initial notifications"), Value(Canceled, TEXT("Notifications")), 0);
        Actor->StopManagedBehavior();
        FStructOnScope OldValue(Notify);
        Test.TestFalse(TEXT("Stopping does not re-arm the initial notification queue"),
            Actor->GetUnifiedScriptComponent()->QueueInitialRepNotify(Actor, Notify, OldValue.GetStructMemory()));
        Canceled->StopManagedBehavior();
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    if (Ready && !Test.HasAnyErrors()) Test.AddInfo(TEXT("PASS: initial notification snapshots survive GC, coalesce, and clear on stop."));
    return Ready && !Test.HasAnyErrors();
}

#endif

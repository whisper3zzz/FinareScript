#if WITH_DEV_AUTOMATION_TESTS
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareBlueprintClassMaterializer.h"
#include "FinareComponent.h"
#include "FinareWorldLibrary.h"
#include "FinareWorldSubsystem.h"
#include "Engine/DataAsset.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "GameFramework/WorldSettings.h"
#include "HAL/FileManager.h"
#include "Interfaces/IPluginManager.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "UObject/StrongObjectPtr.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareManagedObjectLifecycleTest,
    "Finare.Unreal.Objects.ManagedLifecycle",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareManagedObjectLifecycleTest::RunTest(const FString& Parameters)
{
    const FString PackageName = TEXT("/Game/FinareAutomation/BP_ManagedObject");
    const FString Filename = FPackageName::LongPackageNameToFilename(PackageName, FPackageName::GetAssetPackageExtension());
    const bool bExisted = IFileManager::Get().FileExists(*Filename);
    TArray<uint8> Previous;
    if (bExisted && !TestTrue(TEXT("Reads existing fixture"), FFileHelper::LoadFileToArray(Previous, *Filename))) return false;
    ON_SCOPE_EXIT
    {
        if (bExisted) TestTrue(TEXT("Restores fixture"), FFileHelper::SaveArrayToFile(Previous, *Filename));
        else if (IFileManager::Get().FileExists(*Filename))
            TestTrue(TEXT("Removes fixture"), IFileManager::Get().Delete(*Filename));
    };
    if (bExisted && !TestTrue(TEXT("Isolates fixture"), IFileManager::Get().Delete(*Filename))) return false;
    UPackage* Package = CreatePackage(*PackageName);
    TStrongObjectPtr<UFinareBlueprint> Blueprint(Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
        UPrimaryDataAsset::StaticClass(), Package, TEXT("BP_ManagedObject"), BPTYPE_Normal,
        UFinareBlueprint::StaticClass(), UFinareBlueprintGeneratedClass::StaticClass())));
    if (!TestNotNull(TEXT("Creates ordinary PrimaryDataAsset Script Class"), Blueprint.Get())) return false;
    UFinareAsset* Asset = NewObject<UFinareAsset>(Package);
    FGuid::ParseExact(TEXT("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), EGuidFormats::Digits, Asset->ScriptClassId);
    Asset->ScriptClassKind = EFinareClassKind::Object;
    Asset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    Asset->ScriptTypeName = TEXT("Finare.Unreal.Demo.NativeManagedObjectDemoScript");
    Asset->ScriptAssemblyPath = FPaths::Combine(IPluginManager::Get().FindPlugin(TEXT("Finare"))->GetBaseDir(),
        TEXT("Binaries/Managed/Demo/Finare.Unreal.Demo.dll"));
    Asset->DeclarationAssetPath = TEXT("Tests/ManagedObject");
    Asset->DeclarationPath = FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("Automation/Finare"),
        FGuid::NewGuid().ToString(EGuidFormats::Digits) + TEXT(".json"));
    Blueprint->ScriptAsset = Asset;
    const FString Json = TEXT(R"JSON({"schemaVersion":"unifiedscript.script-asset/v0.3","assets":[{
      "id":"us_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","qualifiedName":"Finare.Unreal.Demo.NativeManagedObjectDemoScript","assetPath":"Tests/ManagedObject",
      "states":[
        {"id":"us_11111111111111111111111111111111","name":"Value","typeMetadataName":"System.Int32","typeKind":"integer",
         "defaultValue":7,"exposure":{"instanceEditable":true,"saveGame":true,"replicated":false}},
        {"id":"us_22222222222222222222222222222222","name":"WorldTime","typeMetadataName":"System.Single","typeKind":"floatingPoint",
         "defaultValue":0,"exposure":{"instanceEditable":true,"saveGame":false,"replicated":false}},
        {"id":"us_33333333333333333333333333333333","name":"Child","typeMetadataName":"Finare.Unreal.UnrealObject","typeKind":"object",
         "unrealType":{"kind":"object","unrealTypePath":"/Script/CoreUObject.Object","unrealNativeName":"Object","fields":[]},
         "defaultValue":null,"exposure":{"instanceEditable":true,"saveGame":false,"replicated":false}},
        {"id":"us_66666666666666666666666666666666","name":"RejectStart","typeMetadataName":"System.Boolean","typeKind":"boolean",
         "defaultValue":false,"exposure":{"instanceEditable":true,"saveGame":false,"replicated":false}},
        {"id":"us_77777777777777777777777777777777","name":"RejectAfterGeneration","typeMetadataName":"System.Int32","typeKind":"integer",
         "defaultValue":0,"exposure":{"instanceEditable":true,"saveGame":false,"replicated":false}}
      ],"functions":[{
        "id":"us_44444444444444444444444444444444","name":"Add","qualifiedName":"Finare.Unreal.Demo.NativeManagedObjectDemoScript.Add(System.Int32)",
        "returnTypeMetadataName":"System.Int32","isStatic":false,"isPublic":true,"isEvent":false,
        "parameters":[{"id":"us_55555555555555555555555555555555","name":"amount","typeMetadataName":"System.Int32"}]
      }],"lifecycleHooks":[],
      "classBinding":{"kind":"Object","baseClassPath":"/Script/Engine.PrimaryDataAsset",
        "generatedClassPath":"/Game/FinareAutomation/BP_ManagedObject.BP_ManagedObject_C"}
    }]})JSON");
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Asset->DeclarationPath), true);
    ON_SCOPE_EXIT { IFileManager::Get().Delete(*Asset->DeclarationPath); };
    if (!TestTrue(TEXT("Writes Object declaration"), FFileHelper::SaveStringToFile(Json, *Asset->DeclarationPath))) return false;
    FString Error;
    FFinareBlueprintClassSyncResult Sync;
    const bool bSynced = FFinareBlueprintClassMaterializer::SynchronizeJson(*Asset, *Blueprint, Json, false, Sync, Error);
    if (!TestTrue(*FString::Printf(TEXT("Materializes ordinary Object parent: %s"), *Error), bSynced)) return false;
    FIntProperty* Value = FindFProperty<FIntProperty>(Blueprint->GeneratedClass, TEXT("Value"));
    FFloatProperty* WorldTime = FindFProperty<FFloatProperty>(Blueprint->GeneratedClass, TEXT("WorldTime"));
    FObjectProperty* Child = FindFProperty<FObjectProperty>(Blueprint->GeneratedClass, TEXT("Child"));
    FBoolProperty* RejectStart = FindFProperty<FBoolProperty>(Blueprint->GeneratedClass, TEXT("RejectStart"));
    UFunction* Add = nullptr;
    for (const auto& Pair : CastChecked<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass)->ManagedFunctionBindings)
        if (Pair.Value.ManagedName == TEXT("Add")) Add = Blueprint->GeneratedClass->FindFunctionByName(Pair.Key);
    if (!TestNotNull(TEXT("Object value property"), Value) || !TestNotNull(TEXT("Object WorldTime property"), WorldTime) ||
        !TestNotNull(TEXT("Object child property"), Child) || !TestNotNull(TEXT("Object rejection property"), RejectStart) ||
        !TestNotNull(TEXT("Object Add function"), Add)) return false;
    auto CallAdd = [&](UObject* Receiver, int32 Amount)
    {
        FStructOnScope Args(Add);
        FindFProperty<FIntProperty>(Add, TEXT("amount"))->SetPropertyValue_InContainer(Args.GetStructMemory(), Amount);
        Receiver->ProcessEvent(Add, Args.GetStructMemory());
        return CastFieldChecked<FIntProperty>(Add->GetReturnProperty())->GetPropertyValue_InContainer(Args.GetStructMemory());
    };
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    World->AddToRoot();
    GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(World);
    bool bWorldDestroyed = false;
    ON_SCOPE_EXIT
    {
        if (!bWorldDestroyed)
        {
            if (World->HasBegunPlay()) World->EndPlay(EEndPlayReason::Quit);
            GEngine->DestroyWorldContext(World);
            World->RemoveFromRoot();
            World->DestroyWorld(false);
        }
    };
    World->InitializeActorsForPlay(FURL());
    World->GetWorldSettings()->NotifyBeginPlay();
    World->TimeSeconds = 17.5;
    UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    TStrongObjectPtr<UObject> A(UFinareWorldLibrary::CreateObject(World, Blueprint->GeneratedClass, nullptr));
    if (!TestNotNull(TEXT("Factory activates ordinary UObject script"), A.Get())) return false;
    TestEqual(TEXT("No Finare native Object base required"), A->GetClass()->GetSuperClass(), UPrimaryDataAsset::StaticClass());
    TestEqual(TEXT("Factory starts exactly one generation"), Host->GetManagedScriptCount(), 1);
    TestEqual(TEXT("World owns one internal execution host"), Host->GetManagedObjectExecutionHostCount(), 1);
    TestEqual(TEXT("Object initialization writes native property"), Value->GetPropertyValue_InContainer(A.Get()), 8);
    TestEqual(TEXT("Object World context follows Outer"), WorldTime->GetPropertyValue_InContainer(A.Get()), 17.5f);
    TestNotNull(TEXT("C# generated factory returns a typed object stored in a native property"), Child->GetObjectPropertyValue_InContainer(A.Get()));
    TestEqual(TEXT("Native ProcessEvent invokes C# on actual UObject"), CallAdd(A.Get(), 5), 13);
    UFinareComponent* HostA = Host->FindManagedObjectExecutionHost(A.Get(), Asset);
    if (!TestNotNull(TEXT("Finds internal object execution host"), HostA)) return false;
    TestFalse(TEXT("Object execution does not invent an Actor Tick"), HostA->IsComponentTickEnabled());
    if (!TestTrue(*FString::Printf(TEXT("Reload ordinary Object: %s"), *HostA->LastScriptError), HostA->ReloadScript())) return false;
    TestEqual(TEXT("Reload retains native value then initializes new generation"), Value->GetPropertyValue_InContainer(A.Get()), 14);

    AddExpectedError(TEXT("Finare component: managedException"), EAutomationExpectedErrorFlags::Contains, 4);
    // Fail candidate generation 3 while allowing the existing generation 2 to
    // restart. Making both generations reject the same state cannot roll back.
    FindFProperty<FIntProperty>(Blueprint->GeneratedClass, TEXT("RejectAfterGeneration"))->SetPropertyValue_InContainer(A.Get(), 2);
    RejectStart->SetPropertyValue_InContainer(A.Get(), true);
    TestFalse(TEXT("Failed Object reload rejects candidate"), HostA->ReloadScript());
    RejectStart->SetPropertyValue_InContainer(A.Get(), false);
    TestTrue(TEXT("Failed Object reload keeps the old generation running"), HostA->bScriptRunning);
    TestEqual(TEXT("Restored generation initializes once and remains callable"), CallAdd(A.Get(), 1), 16);
    RejectStart->SetPropertyValue_InContainer(Blueprint->GeneratedClass->GetDefaultObject(), true);
    TestNull(TEXT("Failed factory activation returns null"), UFinareWorldLibrary::CreateObject(World, Blueprint->GeneratedClass, nullptr));
    TStrongObjectPtr<UObject> B(NewObject<UObject>(World, Blueprint->GeneratedClass));
    TestEqual(TEXT("External NewObject remains lazy"), Host->GetManagedScriptCount(), 1);
    AddExpectedError(TEXT("has no exact managed owner"), EAutomationExpectedErrorFlags::Contains, 2);
    CallAdd(B.Get(), 1);
    CallAdd(B.Get(), 1);
    TestEqual(TEXT("Failed lazy retries do not accumulate execution hosts"), Host->GetManagedObjectExecutionHostCount(), 1);
    TestEqual(TEXT("Failed activation preserves existing generation"), Host->GetManagedScriptCount(), 1);
    RejectStart->SetPropertyValue_InContainer(Blueprint->GeneratedClass->GetDefaultObject(), false);
    RejectStart->SetPropertyValue_InContainer(B.Get(), false);
    TestEqual(TEXT("First successful function call activates external NewObject"), CallAdd(B.Get(), 3), 11);
    TestEqual(TEXT("Two independent UObject generations"), Host->GetManagedScriptCount(), 2);
    UFinareComponent* HostB = Host->FindManagedObjectExecutionHost(B.Get(), Asset);
    if (!TestNotNull(TEXT("B owns a distinct execution host"), HostB)) return false;
    TWeakObjectPtr<UObject> WeakB(B.Get());
    B.Reset();
    CollectGarbage(RF_NoFlags);
    TestFalse(TEXT("Execution host does not root its receiver"), WeakB.IsValid());
    World->Tick(LEVELTICK_All, 0.016f);
    TestEqual(TEXT("GC retires dead receiver generation"), Host->GetManagedScriptCount(), 1);
    TestEqual(TEXT("GC releases dead receiver execution host"), Host->GetManagedObjectExecutionHostCount(), 1);
    TestTrue(TEXT("GC retirement has no handle cleanup error"), HostB->LastScriptError.IsEmpty());
    TestTrue(TEXT("Live Object receives no synthetic Tick"), HostA->bScriptRunning);
    World->EndPlay(EEndPlayReason::Quit);
    GEngine->DestroyWorldContext(World);
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    bWorldDestroyed = true;
    TestEqual(TEXT("World teardown retires remaining object generation"), Host->GetManagedScriptCount(), 0);
    TestEqual(TEXT("World teardown releases all internal object hosts"), Host->GetManagedObjectExecutionHostCount(), 0);
    return !HasAnyErrors();
}
#endif

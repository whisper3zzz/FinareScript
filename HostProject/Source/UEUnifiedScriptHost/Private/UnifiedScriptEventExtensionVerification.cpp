#include "UnifiedScriptEventExtensionVerification.h"

#include "Engine/Engine.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "HAL/PlatformProcess.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "HAL/PlatformProperties.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UObject/SoftObjectPath.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"
#include "UObject/GarbageCollection.h"

#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptEventExtensionFixture.h"
#include "UnifiedScriptWorldSubsystem.h"

namespace UE::UnifiedScript::EventExtensionVerification
{
namespace Private
{
struct FReceipt
{
    FString PackageDirectory;
    TArray<FString> PackageNames;
    TArray<FString> PackageFilenames;
    FString NativeActorClassPath;
    FString OrdinaryParentActorClassPath;
    FString IntermediateActorClassPath;
    FString ComponentClassPath;
    FString ComponentChildWithParentClassPath;
    FString ComponentChildWithoutParentClassPath;
};

bool ReadArgument(const TCHAR* Prefix, FString& OutValue)
{
    if (!FParse::Value(FCommandLine::Get(), Prefix, OutValue))
    {
        return false;
    }
    OutValue.TrimQuotesInline();
    return !OutValue.IsEmpty();
}

void Finish(bool bSuccess, const FString& Detail)
{
    FString ResultPath;
    const bool bHasResultPath = ReadArgument(
        TEXT("UnifiedScriptEventExtensionResult="),
        ResultPath);
    const FString Result = FString::Printf(
        TEXT("%s\n%s\n"),
        bSuccess ? TEXT("PASS") : TEXT("FAIL"),
        *Detail);
    bool bResultWritten = true;
    if (bHasResultPath)
    {
        bResultWritten = FFileHelper::SaveStringToFile(
            Result,
            *ResultPath,
            FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
    }
    if (!bResultWritten)
    {
        bSuccess = false;
        UE_LOG(LogTemp, Error, TEXT("Could not write verifier result '%s'."), *ResultPath);
    }
    UE_LOG(
        LogTemp,
        Display,
        TEXT("UNIFIEDSCRIPT_EVENT_EXTENSION %s %s"),
        bSuccess ? TEXT("PASS") : TEXT("FAIL"),
        *Detail.ReplaceCharWithEscapedChar());
    FPlatformMisc::RequestExitWithStatus(
        false,
        bSuccess ? 0 : 1,
        TEXT("UnifiedScriptEventExtensionVerify"));
}

bool ReadStringField(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Name,
    FString& OutValue,
    FString& OutError)
{
    if (!Object.IsValid() || !Object->TryGetStringField(Name, OutValue) || OutValue.IsEmpty())
    {
        OutError = FString::Printf(TEXT("Receipt field '%s' is missing."), Name);
        return false;
    }
    return true;
}

bool LoadReceipt(const FString& ReceiptPath, FReceipt& OutReceipt, FString& OutError)
{
    FString Json;
    if (!FFileHelper::LoadFileToString(Json, *ReceiptPath))
    {
        OutError = FString::Printf(TEXT("Could not read event extension receipt '%s'."), *ReceiptPath);
        return false;
    }
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = TEXT("Event extension receipt is not valid JSON.");
        return false;
    }
    FString SchemaVersion;
    if (!ReadStringField(Root, TEXT("schemaVersion"), SchemaVersion, OutError) ||
        SchemaVersion != TEXT("unifiedscript.event-extension-fixture/v0.1") ||
        !ReadStringField(Root, TEXT("packageDirectory"), OutReceipt.PackageDirectory, OutError))
    {
        if (OutError.IsEmpty()) OutError = TEXT("Event extension receipt schemaVersion is invalid.");
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* Packages = nullptr;
    if (!Root->TryGetArrayField(TEXT("packages"), Packages) || Packages == nullptr || Packages->Num() != 6)
    {
        OutError = TEXT("Event extension receipt must contain six packages.");
        return false;
    }
    for (const TSharedPtr<FJsonValue>& Value : *Packages)
    {
        const TSharedPtr<FJsonObject> Package = Value.IsValid() ? Value->AsObject() : nullptr;
        FString PackageName;
        FString PackageFilename;
        if (!ReadStringField(Package, TEXT("packageName"), PackageName, OutError) ||
            !ReadStringField(Package, TEXT("packageFilename"), PackageFilename, OutError))
        {
            return false;
        }
        OutReceipt.PackageNames.Add(PackageName);
        OutReceipt.PackageFilenames.Add(PackageFilename);
    }
    return ReadStringField(Root, TEXT("nativeActorClassPath"), OutReceipt.NativeActorClassPath, OutError) &&
        ReadStringField(Root, TEXT("ordinaryParentActorClassPath"), OutReceipt.OrdinaryParentActorClassPath, OutError) &&
        ReadStringField(Root, TEXT("intermediateActorClassPath"), OutReceipt.IntermediateActorClassPath, OutError) &&
        ReadStringField(Root, TEXT("componentClassPath"), OutReceipt.ComponentClassPath, OutError) &&
        ReadStringField(Root, TEXT("componentChildWithParentClassPath"), OutReceipt.ComponentChildWithParentClassPath, OutError) &&
        ReadStringField(Root, TEXT("componentChildWithoutParentClassPath"), OutReceipt.ComponentChildWithoutParentClassPath, OutError);
}

FString NormalizeRuntimePath(FString Path)
{
    if (FPaths::IsRelative(Path))
    {
        Path = FPaths::Combine(FPlatformProcess::BaseDir(), Path);
    }
    Path = FPaths::ConvertRelativePathToFull(Path);
    FPaths::NormalizeFilename(Path);
    return Path;
}

bool IsUnder(const FString& Root, const FString& Candidate)
{
    return Candidate == Root || Candidate.StartsWith(Root + TEXT("/"), ESearchCase::IgnoreCase);
}

bool ValidateScriptAsset(
    const UUnifiedScriptAsset* ScriptAsset,
    const FString& ExpectedClassPath,
    const FString& PackagedRoot,
    FString& OutError)
{
    if (ScriptAsset == nullptr ||
        !ScriptAsset->GeneratedClassPath.ToString().Equals(ExpectedClassPath, ESearchCase::CaseSensitive))
    {
        OutError = FString::Printf(TEXT("Cooked ScriptAsset does not target '%s'."), *ExpectedClassPath);
        return false;
    }
    FString ProgramPath;
    FString TypeName;
    if (!ScriptAsset->ResolveRuntimeConfiguration(true, ProgramPath, TypeName, OutError))
    {
        return false;
    }
    ProgramPath = NormalizeRuntimePath(ProgramPath);
    if (!FPlatformProperties::RequiresCookedData() ||
        !ProgramPath.EndsWith(TEXT(".uschunk"), ESearchCase::IgnoreCase) ||
        !ProgramPath.Contains(TEXT("/Plugins/UEUnifiedScript/Binaries/Managed/Demo/"), ESearchCase::IgnoreCase) ||
        !IFileManager::Get().FileExists(*ProgramPath) ||
        !IsUnder(PackagedRoot, ProgramPath))
    {
        OutError = FString::Printf(TEXT("Cooked ScriptAsset program path is invalid: %s."), *ProgramPath);
        return false;
    }
    return true;
}

bool InvokeEvaluate(
    AUnifiedScriptEventExtensionFixture& Actor,
    int32 ExpectedReturn,
    int32 ExpectedAccumulator,
    int32 ExpectedOutput,
    FString& OutError)
{
    UFunction* Function = Actor.GetClass()->FindFunctionByName(
        TEXT("EvaluateInherited"),
        EIncludeSuperFlag::ExcludeSuper);
    const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Actor.GetClass());
    if (Function == nullptr || GeneratedClass == nullptr ||
        !GeneratedClass->IsManagedFunctionBound(Function->GetFName()))
    {
        OutError = TEXT("Cooked EvaluateInherited lost its Blueprint VM contract.");
        return false;
    }
    int32 Accumulator = 5;
    int32 Output = 0;
    const int32 ReturnValue = Actor.EvaluateInherited(7, Accumulator, Output);
    if (ReturnValue != ExpectedReturn || Accumulator != ExpectedAccumulator ||
        Output != ExpectedOutput)
    {
        OutError = FString::Printf(
            TEXT("Cooked EvaluateInherited returned unexpected values (return=%d accumulator=%d output=%d)."),
            ReturnValue,
            Accumulator,
            Output);
        return false;
    }
    return true;
}

bool InvokeExchangeArray(AUnifiedScriptEventExtensionFixture& Actor, FString& OutError)
{
    UFunction* Function = Actor.GetClass()->FindFunctionByName(
        TEXT("ExchangeInheritedArray"), EIncludeSuperFlag::ExcludeSuper);
    const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Actor.GetClass());
    if (Function == nullptr || GeneratedClass == nullptr ||
        !GeneratedClass->IsManagedFunctionBound(Function->GetFName()))
    {
        OutError = TEXT("Cooked ExchangeInheritedArray lost its Blueprint VM contract.");
        return false;
    }
    TArray<int32> Expected;
    Expected.Reserve(258);
    for (int32 Index = 0; Index < 256; ++Index)
    {
        Expected.Add(Index);
    }
    Expected.Add(100);
    Expected.Add(101);
    TArray<int32> Input;
    Input.Reserve(256);
    for (int32 Index = 0; Index < 256; ++Index)
    {
        Input.Add(Index);
    }
    TArray<int32> State{100, 101};
    TArray<int32> Output;
    const TArray<int32> ReturnValue = Actor.ExchangeInheritedArray(Input, State, Output);
    const bool bValid = ReturnValue == Expected && State == Expected && Output == Expected;
    if (!bValid)
    {
        OutError = TEXT("Cooked ExchangeInheritedArray returned an unexpected 258-item result.");
    }
    return bValid;
}

bool InvokeReceiveInherited(AUnifiedScriptEventExtensionFixture& Actor, AActor& ObservedActor, FString& OutError)
{
    UFunction* Function = Actor.GetClass()->FindFunctionByName(
        TEXT("ReceiveInherited"), EIncludeSuperFlag::ExcludeSuper);
    FStrProperty* ObservedText = FindFProperty<FStrProperty>(Actor.GetClass(), TEXT("ObservedText"));
    FObjectPropertyBase* ObservedObject = FindFProperty<FObjectPropertyBase>(Actor.GetClass(), TEXT("ObservedActor"));
    const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Actor.GetClass());
    if (Function == nullptr || ObservedText == nullptr || ObservedObject == nullptr ||
        GeneratedClass == nullptr ||
        !GeneratedClass->IsManagedFunctionBound(Function->GetFName()))
    {
        OutError = TEXT("Cooked ReceiveInherited lost its Blueprint VM contract.");
        return false;
    }
    Actor.ReceiveInherited(TEXT("cooked-parent"), &ObservedActor);
    if (ObservedText->GetPropertyValue_InContainer(&Actor) != TEXT("cooked-parent") ||
        ObservedObject->GetObjectPropertyValue_InContainer(&Actor) != &ObservedActor)
    {
        OutError = TEXT("Cooked ReceiveInherited did not persist both observed parameters.");
        return false;
    }
    return true;
}

bool StartComponentActor(
    UWorld& World,
    UClass& ComponentClass,
    const TCHAR* Name,
    AActor*& OutActor,
    UUnifiedScriptComponent*& OutComponent,
    FString& OutError)
{
    OutActor = World.SpawnActor<AActor>();
    OutComponent = OutActor != nullptr
        ? NewObject<UUnifiedScriptComponent>(OutActor, &ComponentClass, FName(Name))
        : nullptr;
    if (OutActor == nullptr || OutComponent == nullptr)
    {
        OutError = TEXT("Cooked component fixture Actor/component could not be created.");
        return false;
    }
    OutActor->AddInstanceComponent(OutComponent);
    OutComponent->RegisterComponentWithWorld(&World);
    OutActor->DispatchBeginPlay();
    if (!OutComponent->bScriptRunning || !OutComponent->HasBegunPlay())
    {
        OutError = TEXT("Cooked component fixture did not start its managed script during DispatchBeginPlay.");
        return false;
    }
    return true;
}

void DestroyComponentActor(UWorld& World, AActor*& Actor, UUnifiedScriptComponent*& Component)
{
    if (Actor != nullptr && Actor->HasActorBegunPlay())
    {
        Actor->RouteEndPlay(EEndPlayReason::Quit);
    }
    if (Component != nullptr)
    {
        Component->DestroyComponent();
        Component = nullptr;
    }
    if (Actor != nullptr && !Actor->IsActorBeingDestroyed())
    {
        World.DestroyActor(Actor);
    }
    Actor = nullptr;
}
}

void Run()
{
    FString ReceiptPath;
    if (!Private::ReadArgument(TEXT("UnifiedScriptEventExtensionReceipt="), ReceiptPath))
    {
        Private::Finish(false, TEXT("UnifiedScriptEventExtensionReceipt is required."));
        return;
    }
    Private::FReceipt Receipt;
    FString Error;
    if (!Private::LoadReceipt(ReceiptPath, Receipt, Error))
    {
        Private::Finish(false, Error);
        return;
    }
    UClass* NativeClass = LoadObject<UClass>(nullptr, *Receipt.NativeActorClassPath);
    UClass* OrdinaryClass = LoadObject<UClass>(nullptr, *Receipt.OrdinaryParentActorClassPath);
    UClass* IntermediateClass = LoadObject<UClass>(nullptr, *Receipt.IntermediateActorClassPath);
    UClass* ComponentClass = LoadObject<UClass>(nullptr, *Receipt.ComponentClassPath);
    UClass* ChildWithParentClass = LoadObject<UClass>(nullptr, *Receipt.ComponentChildWithParentClassPath);
    UClass* ChildWithoutParentClass = LoadObject<UClass>(nullptr, *Receipt.ComponentChildWithoutParentClassPath);
    if (NativeClass == nullptr || OrdinaryClass == nullptr || IntermediateClass == nullptr ||
        ComponentClass == nullptr || ChildWithParentClass == nullptr || ChildWithoutParentClass == nullptr)
    {
        Private::Finish(false, TEXT("Cooked event extension fixture classes could not be loaded."));
        return;
    }
    if (GEngine == nullptr)
    {
        Private::Finish(false, TEXT("The global Engine was unavailable for the event extension World."));
        return;
    }
    FString PackagedRoot = Private::NormalizeRuntimePath(
        FPaths::Combine(FPlatformProcess::BaseDir(), TEXT("../../")));
    while (PackagedRoot.EndsWith(TEXT("/"))) PackagedRoot.LeftChopInline(1);
    FString HostFxrPath;
    const int32 InitialWorldCount = FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (World == nullptr)
    {
        Private::Finish(false, TEXT("Could not create event extension verification World."));
        return;
    }
    World->AddToRoot();
    FWorldContext* WorldContext = &GEngine->CreateNewWorldContext(EWorldType::Game);
    WorldContext->SetCurrentWorld(World);
    World->InitializeActorsForPlay(FURL());
    UUnifiedScriptWorldSubsystem* Host = World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    if (Host == nullptr || !Host->IsManagedWorldReady())
    {
        GEngine->DestroyWorldContext(World);
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        Private::Finish(false, TEXT("Event extension verification World host was not ready."));
        return;
    }
    HostFxrPath = Private::NormalizeRuntimePath(FUnifiedScriptCoreClrHost::Get().GetHostFxrPath());
    if (!FPlatformProperties::RequiresCookedData() ||
        !Private::IsUnder(PackagedRoot, HostFxrPath) ||
        !HostFxrPath.Contains(TEXT("/Plugins/UEUnifiedScript/Binaries/ThirdParty/DotNet/10.0/"), ESearchCase::IgnoreCase))
    {
        GEngine->DestroyWorldContext(World);
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        Private::Finish(false, TEXT("Event extension verification is not running from the packaged Host project."));
        return;
    }

    AUnifiedScriptEventExtensionFixture* NativeActor = nullptr;
    AUnifiedScriptEventExtensionFixture* OrdinaryActor = nullptr;
    AUnifiedScriptEventExtensionFixture* IntermediateActor = nullptr;
    AActor* BaseComponentActor = nullptr;
    UUnifiedScriptComponent* BaseComponent = nullptr;
    AActor* ChildWithParentActor = nullptr;
    UUnifiedScriptComponent* ChildWithParent = nullptr;
    AActor* ChildWithoutParentActor = nullptr;
    UUnifiedScriptComponent* ChildWithoutParent = nullptr;
    const int32 InitialManagedScriptCount = Host->GetManagedScriptCount();
    const int32 InitialRegisteredObjects = Host->NumRegisteredObjects();
    const int32 InitialLifecycleClaims = Host->GetGameplayLifecycleClaimCount();
    const uint64 InitialFastInvocationCount = Host->GetFastInvocationCount();

    auto CleanupWorld = [&]()
    {
        Private::DestroyComponentActor(*World, BaseComponentActor, BaseComponent);
        Private::DestroyComponentActor(*World, ChildWithParentActor, ChildWithParent);
        Private::DestroyComponentActor(*World, ChildWithoutParentActor, ChildWithoutParent);
        auto DestroyFixtureActor = [&](AUnifiedScriptEventExtensionFixture*& Actor)
        {
            if (Actor != nullptr && Actor->HasActorBegunPlay())
            {
                Actor->RouteEndPlay(EEndPlayReason::Quit);
            }
            if (Actor != nullptr && !Actor->IsActorBeingDestroyed())
            {
                World->DestroyActor(Actor);
            }
            Actor = nullptr;
        };
        DestroyFixtureActor(NativeActor);
        DestroyFixtureActor(OrdinaryActor);
        DestroyFixtureActor(IntermediateActor);
        // ReceiveInherited deliberately passes OrdinaryActor through the real
        // UObject registry.  The managed instance releases its handle during
        // StopScript; the registry's delete listener retires the observed
        // actor when UE actually destroys it, so collect while the World is
        // still rooted before checking the final registry count.
        CollectGarbage(RF_NoFlags);
        const bool bHostClean =
            Host->GetManagedScriptCount() == InitialManagedScriptCount &&
            Host->NumRegisteredObjects() == InitialRegisteredObjects &&
            Host->GetGameplayLifecycleClaimCount() == InitialLifecycleClaims;
        if (!bHostClean && Error.IsEmpty())
        {
            Error = FString::Printf(
                TEXT("Cooked event extension World did not release managed state (managed=%d/%d registered=%d/%d claims=%d/%d)."),
                Host->GetManagedScriptCount(),
                InitialManagedScriptCount,
                Host->NumRegisteredObjects(),
                InitialRegisteredObjects,
                Host->GetGameplayLifecycleClaimCount(),
                InitialLifecycleClaims);
        }
        World->RemoveFromRoot();
        GEngine->DestroyWorldContext(World);
        World->DestroyWorld(false);
        World = nullptr;
        const bool bWorldClean =
            FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount() == InitialWorldCount;
        if (!bWorldClean && Error.IsEmpty())
        {
            Error = TEXT("Cooked event extension verification leaked a managed World.");
        }
        return bHostClean && bWorldClean;
    };
    auto Fail = [&](const FString& Fallback)
    {
        const bool bClean = CleanupWorld();
        const FString Detail = Error.IsEmpty() ? Fallback : Error;
        Private::Finish(
            false,
            bClean ? Detail : FString::Printf(TEXT("%s Cleanup failed: %s"), *Detail, *Error));
    };

    auto ValidateClassAsset = [&](UClass* Class, bool bActor, const FString& ExpectedPath)
    {
        const UUnifiedScriptAsset* Asset = nullptr;
        if (bActor)
        {
            const AUnifiedScriptActor* CDO = Cast<AUnifiedScriptActor>(Class->GetDefaultObject());
            Asset = CDO != nullptr && CDO->ScriptComponent != nullptr
                ? CDO->ScriptComponent->ScriptAsset.Get() : nullptr;
        }
        else
        {
            const UUnifiedScriptComponent* CDO = Cast<UUnifiedScriptComponent>(Class->GetDefaultObject());
            Asset = CDO != nullptr ? CDO->ScriptAsset.Get() : nullptr;
        }
        return Private::ValidateScriptAsset(Asset, ExpectedPath, PackagedRoot, Error);
    };
    auto HasManagedBinding = [](UClass* Class, const FName FunctionName)
    {
        const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
            Cast<UUnifiedScriptBlueprintGeneratedClass>(Class);
        return GeneratedClass != nullptr && GeneratedClass->IsManagedFunctionBound(FunctionName);
    };
    if (!ValidateClassAsset(NativeClass, true, Receipt.NativeActorClassPath) ||
        !ValidateClassAsset(IntermediateClass, true, Receipt.IntermediateActorClassPath) ||
        !ValidateClassAsset(ComponentClass, false, Receipt.ComponentClassPath) ||
        !ValidateClassAsset(ChildWithParentClass, false, Receipt.ComponentClassPath) ||
        !ValidateClassAsset(ChildWithoutParentClass, false, Receipt.ComponentClassPath) ||
        !HasManagedBinding(NativeClass, TEXT("EvaluateInherited")) ||
        !HasManagedBinding(NativeClass, TEXT("ExchangeInheritedArray")) ||
        !HasManagedBinding(IntermediateClass, TEXT("EvaluateInherited")) ||
        !HasManagedBinding(IntermediateClass, TEXT("ReceiveInherited")) ||
        !HasManagedBinding(ComponentClass, TEXT("ReceiveBeginPlay")) ||
        !HasManagedBinding(ComponentClass, TEXT("ReceiveEndPlay")))
    {
        Fail(TEXT("Cooked event extension ScriptAsset or managed binding validation failed."));
        return;
    }

    NativeActor = World->SpawnActor<AUnifiedScriptEventExtensionFixture>(NativeClass);
    OrdinaryActor = World->SpawnActor<AUnifiedScriptEventExtensionFixture>(OrdinaryClass);
    IntermediateActor = World->SpawnActor<AUnifiedScriptEventExtensionFixture>(IntermediateClass);
    if (NativeActor == nullptr || OrdinaryActor == nullptr || IntermediateActor == nullptr)
    {
        Fail(TEXT("Event extension actor fixture could not be spawned."));
        return;
    }
    NativeActor->DispatchBeginPlay();
    IntermediateActor->DispatchBeginPlay();
    if (NativeActor->ScriptComponent == nullptr || !NativeActor->ScriptComponent->bScriptRunning ||
        IntermediateActor->ScriptComponent == nullptr || !IntermediateActor->ScriptComponent->bScriptRunning)
    {
        Fail(TEXT("Cooked managed actor fixture did not start its ScriptComponent."));
        return;
    }
    const uint64 BeforeEvaluate = Host->GetFastInvocationCount();
    const int32 BeforeNativeParent = NativeActor->ParentInvocationCount;
    if (!Private::InvokeEvaluate(*NativeActor, 12, 12, 12, Error) ||
        Host->GetFastInvocationCount() != BeforeEvaluate + 1 ||
        NativeActor->ParentInvocationCount != BeforeNativeParent + 1)
    {
        Fail(TEXT("Cooked native managed EvaluateInherited verification failed."));
        return;
    }
    const uint64 BeforeArray = Host->GetFastInvocationCount();
    const int32 BeforeArrayParent = NativeActor->ParentInvocationCount;
    if (!Private::InvokeExchangeArray(*NativeActor, Error) ||
        Host->GetFastInvocationCount() != BeforeArray + 1 ||
        NativeActor->ParentInvocationCount != BeforeArrayParent + 1)
    {
        Fail(TEXT("Cooked native managed ExchangeInheritedArray verification failed."));
        return;
    }
    const uint64 BeforeIntermediateEvaluate = Host->GetFastInvocationCount();
    if (!Private::InvokeEvaluate(*IntermediateActor, 99, 77, 88, Error) ||
        Host->GetFastInvocationCount() != BeforeIntermediateEvaluate + 1 ||
        IntermediateActor->ParentInvocationCount != 0)
    {
        Fail(TEXT("Cooked intermediate Blueprint EvaluateInherited verification failed."));
        return;
    }
    const uint64 BeforeIntermediateReceive = Host->GetFastInvocationCount();
    if (!Private::InvokeReceiveInherited(*IntermediateActor, *OrdinaryActor, Error) ||
        Host->GetFastInvocationCount() != BeforeIntermediateReceive + 1 ||
        IntermediateActor->ParentInvocationCount != 0)
    {
        Fail(TEXT("Cooked intermediate Blueprint ReceiveInherited verification failed."));
        return;
    }

    const bool bComponentsStarted =
        Private::StartComponentActor(*World, *ComponentClass, TEXT("CookedComponentBase"), BaseComponentActor, BaseComponent, Error) &&
        Private::StartComponentActor(*World, *ChildWithParentClass, TEXT("CookedComponentWithParent"), ChildWithParentActor, ChildWithParent, Error) &&
        Private::StartComponentActor(*World, *ChildWithoutParentClass, TEXT("CookedComponentWithoutParent"), ChildWithoutParentActor, ChildWithoutParent, Error);
    if (!bComponentsStarted || BaseComponent == nullptr || ChildWithParent == nullptr ||
        ChildWithoutParent == nullptr ||
        BaseComponent->ManagedState != TEXT("Initialized|ReceiveBegin") ||
        ChildWithParent->ManagedState != TEXT("ChildBegin|ReceiveBegin") ||
        ChildWithoutParent->ManagedState != TEXT("ChildBegin"))
    {
        Fail(TEXT("Cooked component BeginPlay verification failed."));
        return;
    }
    BaseComponentActor->RouteEndPlay(EEndPlayReason::Quit);
    ChildWithParentActor->RouteEndPlay(EEndPlayReason::Quit);
    ChildWithoutParentActor->RouteEndPlay(EEndPlayReason::Quit);
    const bool bComponentEndStates =
        BaseComponent->ManagedState == TEXT("Initialized|ReceiveBegin|ReceiveEnd:4|Stopped") &&
        ChildWithParent->ManagedState == TEXT("ChildEnd|ReceiveEnd:4|Stopped") &&
        ChildWithoutParent->ManagedState == TEXT("ChildEnd|Stopped") &&
        !BaseComponent->bScriptRunning && !BaseComponent->HasBegunPlay() &&
        !ChildWithParent->bScriptRunning && !ChildWithParent->HasBegunPlay() &&
        !ChildWithoutParent->bScriptRunning && !ChildWithoutParent->HasBegunPlay();
    if (!bComponentEndStates)
    {
        Fail(TEXT("Cooked component EndPlay verification failed."));
        return;
    }
    const uint64 FinalFastInvocationCount = Host->GetFastInvocationCount();
    if (!CleanupWorld())
    {
        Private::Finish(false, Error.IsEmpty() ? TEXT("Cooked event extension cleanup failed.") : Error);
        return;
    }
    if (FinalFastInvocationCount < InitialFastInvocationCount + 4)
    {
        Private::Finish(false, TEXT("Cooked event extension managed invocation count did not advance for all actor calls."));
        return;
    }
    Private::Finish(
        true,
        FString::Printf(
            TEXT("Cooked parent events and lifecycle components passed; managed fast calls=%llu hostfxr=%s."),
            static_cast<unsigned long long>(FinalFastInvocationCount - InitialFastInvocationCount),
            *HostFxrPath));
}
}

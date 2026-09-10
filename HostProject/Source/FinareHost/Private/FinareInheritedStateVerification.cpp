#include "FinareInheritedStateVerification.h"

#include "Engine/Engine.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformProperties.h"
#include "Misc/CommandLine.h"
#include "Misc/CoreDelegates.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "FinareActor.h"
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareComponent.h"
#include "FinareCoreClrHost.h"
#include "FinareWorldSubsystem.h"
#include "UObject/UnrealType.h"

namespace UE::Finare::InheritedStateVerification
{
namespace Private
{
struct FReceipt
{
    FString FixtureId;
    FString PackageDirectory;
    FString ScriptClassId;
    FString ScriptTypeName;
    FString StageDirectory;
    FString BaseClassPath;
    FString ChildClassPath;
    FString GrandchildClassPath;
    FString ScriptAssetPath;
};

struct FState
{
    int32 Seed = 0;
    TArray<int32> Values;
    TSet<int32> Enabled;
    TMap<int32, int32> Weights;
    int32 TickCount = 0;
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
    const bool bHasResultPath = ReadArgument(TEXT("FinareInheritedStateResult="), ResultPath);
    FString Result = FString::Printf(TEXT("%s\n%s\n"), bSuccess ? TEXT("PASS") : TEXT("FAIL"), *Detail);
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
        UE_LOG(LogTemp, Error, TEXT("Could not write inherited-state result '%s'."), *ResultPath);
    }
    UE_LOG(
        LogTemp,
        Display,
        TEXT("FINARE_INHERITED_STATE %s %s"),
        bSuccess ? TEXT("PASS") : TEXT("FAIL"),
        *Detail.ReplaceCharWithEscapedChar());
    FPlatformMisc::RequestExitWithStatus(false, bSuccess ? 0 : 1, TEXT("FinareInheritedStateVerify"));
}

bool ReadStringField(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Name,
    FString& OutValue,
    FString& OutError)
{
    if (!Object.IsValid() || !Object->TryGetStringField(Name, OutValue) || OutValue.IsEmpty())
    {
        OutError = FString::Printf(TEXT("Inherited-state receipt field '%s' is missing."), Name);
        return false;
    }
    return true;
}

bool LoadReceipt(const FString& Path, FReceipt& Out, FString& OutError)
{
    FString Json;
    if (!FFileHelper::LoadFileToString(Json, *Path))
    {
        OutError = FString::Printf(TEXT("Could not read inherited-state receipt '%s'."), *Path);
        return false;
    }
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = TEXT("Inherited-state receipt is invalid JSON.");
        return false;
    }
    FString Schema;
    if (!ReadStringField(Root, TEXT("schemaVersion"), Schema, OutError) ||
        !Schema.Equals(TEXT("unifiedscript.inherited-state-cook-fixture/v0.1"), ESearchCase::CaseSensitive) ||
        !ReadStringField(Root, TEXT("fixtureId"), Out.FixtureId, OutError) ||
        !ReadStringField(Root, TEXT("packageDirectory"), Out.PackageDirectory, OutError) ||
        !ReadStringField(Root, TEXT("scriptClassId"), Out.ScriptClassId, OutError) ||
        !ReadStringField(Root, TEXT("scriptTypeName"), Out.ScriptTypeName, OutError) ||
        !ReadStringField(Root, TEXT("stageDirectory"), Out.StageDirectory, OutError) ||
        !ReadStringField(Root, TEXT("baseClassPath"), Out.BaseClassPath, OutError) ||
        !ReadStringField(Root, TEXT("childClassPath"), Out.ChildClassPath, OutError) ||
        !ReadStringField(Root, TEXT("grandchildClassPath"), Out.GrandchildClassPath, OutError) ||
        !ReadStringField(Root, TEXT("scriptAssetPath"), Out.ScriptAssetPath, OutError))
    {
        return false;
    }
    FGuid FixtureGuid;
    if (Out.FixtureId.Len() != 32 ||
        !Out.FixtureId.Equals(Out.FixtureId.ToUpper(), ESearchCase::CaseSensitive) ||
        !FGuid::ParseExact(Out.FixtureId, EGuidFormats::Digits, FixtureGuid) ||
        !Out.PackageDirectory.Equals(FString::Printf(TEXT("/Game/UnifiedScriptInheritedState_%s"), *Out.FixtureId), ESearchCase::CaseSensitive) ||
        !Out.StageDirectory.Equals(
            FString::Printf(TEXT("{PluginDir}/Binaries/Managed/InheritedState/%s"), *Out.FixtureId),
            ESearchCase::CaseSensitive))
    {
        OutError = TEXT("Inherited-state receipt has invalid GUID, package directory, or stage directory.");
        return false;
    }
    if (!Out.ScriptClassId.StartsWith(TEXT("us_")) || Out.ScriptClassId.Len() != 35)
    {
        OutError = TEXT("Inherited-state receipt has an invalid ScriptClassId.");
        return false;
    }
    FGuid ScriptGuid;
    if (!FGuid::ParseExact(Out.ScriptClassId.Mid(3), EGuidFormats::Digits, ScriptGuid))
    {
        OutError = TEXT("Inherited-state receipt ScriptClassId is not a stable GUID.");
        return false;
    }
    const FString ExpectedBase = Out.PackageDirectory + TEXT("/InheritedStateBase.InheritedStateBase_C");
    const FString ExpectedChild = Out.PackageDirectory + TEXT("/InheritedStateChild.InheritedStateChild_C");
    const FString ExpectedGrandchild = Out.PackageDirectory + TEXT("/InheritedStateGrandchild.InheritedStateGrandchild_C");
    const FString ExpectedAsset = Out.PackageDirectory + TEXT("/InheritedStateScriptAsset.InheritedStateScriptAsset");
    if (!Out.BaseClassPath.Equals(ExpectedBase, ESearchCase::CaseSensitive) ||
        !Out.ChildClassPath.Equals(ExpectedChild, ESearchCase::CaseSensitive) ||
        !Out.GrandchildClassPath.Equals(ExpectedGrandchild, ESearchCase::CaseSensitive) ||
        !Out.ScriptAssetPath.Equals(ExpectedAsset, ESearchCase::CaseSensitive))
    {
        OutError = TEXT("Inherited-state receipt class or ScriptAsset path is not the fixed fixture layout.");
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* Packages = nullptr;
    if (!Root->TryGetArrayField(TEXT("packages"), Packages) || Packages == nullptr || Packages->Num() != 4)
    {
        OutError = TEXT("Inherited-state receipt must contain exactly four packages.");
        return false;
    }
    return true;
}

FString NormalizePath(FString Path)
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
    return Candidate.Equals(Root, ESearchCase::IgnoreCase) ||
        Candidate.StartsWith(Root + TEXT("/"), ESearchCase::IgnoreCase);
}

int32 Signature(const FState& State)
{
    int32 Result = State.Seed * 17;
    for (int32 Index = 0; Index < State.Values.Num(); ++Index)
    {
        Result += State.Values[Index] * (Index + 1);
    }
    for (int32 Value : State.Enabled)
    {
        Result += Value * 3;
    }
    for (const TPair<int32, int32>& Pair : State.Weights)
    {
        Result += Pair.Key * 5 + Pair.Value * 7 + Pair.Key * Pair.Value * 11;
    }
    return Result;
}

void SetArray(const FArrayProperty& Property, UObject& Object, TConstArrayView<int32> Values)
{
    FScriptArrayHelper Helper(&Property, Property.ContainerPtrToValuePtr<void>(&Object));
    Helper.EmptyValues();
    const FIntProperty* Inner = CastFieldChecked<FIntProperty>(Property.Inner);
    for (const int32 Value : Values)
    {
        const int32 Index = Helper.AddValue();
        Inner->SetPropertyValue(Helper.GetRawPtr(Index), Value);
    }
}

void SetSet(const FSetProperty& Property, UObject& Object, std::initializer_list<int32> Values)
{
    FScriptSetHelper Helper(&Property, Property.ContainerPtrToValuePtr<void>(&Object));
    Helper.EmptyElements();
    const FIntProperty* Element = CastFieldChecked<FIntProperty>(Property.ElementProp);
    for (const int32 Value : Values)
    {
        const int32 Index = Helper.AddDefaultValue_Invalid_NeedsRehash();
        Element->SetPropertyValue(Helper.GetElementPtr(Index), Value);
    }
    Helper.Rehash();
}

void SetMap(const FMapProperty& Property, UObject& Object, std::initializer_list<TPair<int32, int32>> Values)
{
    FScriptMapHelper Helper(&Property, Property.ContainerPtrToValuePtr<void>(&Object));
    Helper.EmptyValues();
    const FIntProperty* Key = CastFieldChecked<FIntProperty>(Property.KeyProp);
    const FIntProperty* Value = CastFieldChecked<FIntProperty>(Property.ValueProp);
    for (const TPair<int32, int32>& Pair : Values)
    {
        const int32 Index = Helper.AddDefaultValue_Invalid_NeedsRehash();
        Key->SetPropertyValue(Helper.GetKeyPtr(Index), Pair.Key);
        Value->SetPropertyValue(Helper.GetValuePtr(Index), Pair.Value);
    }
    Helper.Rehash();
}

TArray<int32> ReadArray(const FArrayProperty& Property, UObject& Object)
{
    TArray<int32> Result;
    FScriptArrayHelper Helper(&Property, Property.ContainerPtrToValuePtr<void>(&Object));
    const FIntProperty* Inner = CastFieldChecked<FIntProperty>(Property.Inner);
    for (int32 Index = 0; Index < Helper.Num(); ++Index)
    {
        Result.Add(Inner->GetPropertyValue(Helper.GetRawPtr(Index)));
    }
    return Result;
}

TSet<int32> ReadSet(const FSetProperty& Property, UObject& Object)
{
    TSet<int32> Result;
    FScriptSetHelper Helper(&Property, Property.ContainerPtrToValuePtr<void>(&Object));
    const FIntProperty* Element = CastFieldChecked<FIntProperty>(Property.ElementProp);
    for (int32 Index = 0; Index < Helper.GetMaxIndex(); ++Index)
    {
        if (Helper.IsValidIndex(Index))
        {
            Result.Add(Element->GetPropertyValue(Helper.GetElementPtr(Index)));
        }
    }
    return Result;
}

TMap<int32, int32> ReadMap(const FMapProperty& Property, UObject& Object)
{
    TMap<int32, int32> Result;
    FScriptMapHelper Helper(&Property, Property.ContainerPtrToValuePtr<void>(&Object));
    const FIntProperty* Key = CastFieldChecked<FIntProperty>(Property.KeyProp);
    const FIntProperty* Value = CastFieldChecked<FIntProperty>(Property.ValueProp);
    for (int32 Index = 0; Index < Helper.GetMaxIndex(); ++Index)
    {
        if (Helper.IsValidIndex(Index))
        {
            Result.Add(Key->GetPropertyValue(Helper.GetKeyPtr(Index)), Value->GetPropertyValue(Helper.GetValuePtr(Index)));
        }
    }
    return Result;
}

bool ReadState(UObject& Object, FState& Out)
{
    const UClass* Class = Object.GetClass();
    const FIntProperty* Seed = FindFProperty<FIntProperty>(Class, TEXT("Seed"));
    const FArrayProperty* Values = FindFProperty<FArrayProperty>(Class, TEXT("Values"));
    const FSetProperty* Enabled = FindFProperty<FSetProperty>(Class, TEXT("Enabled"));
    const FMapProperty* Weights = FindFProperty<FMapProperty>(Class, TEXT("Weights"));
    const FIntProperty* TickCount = FindFProperty<FIntProperty>(Class, TEXT("TickCount"));
    if (Seed == nullptr || Values == nullptr || Enabled == nullptr || Weights == nullptr || TickCount == nullptr)
    {
        return false;
    }
    Out.Seed = Seed->GetPropertyValue_InContainer(&Object);
    Out.Values = ReadArray(*Values, Object);
    Out.Enabled = ReadSet(*Enabled, Object);
    Out.Weights = ReadMap(*Weights, Object);
    Out.TickCount = TickCount->GetPropertyValue_InContainer(&Object);
    return true;
}

bool SameState(const FState& Actual, const FState& Expected)
{
    if (Actual.Seed != Expected.Seed || Actual.Values != Expected.Values || Actual.Enabled.Num() != Expected.Enabled.Num() || Actual.Weights.Num() != Expected.Weights.Num() || Actual.TickCount != Expected.TickCount)
    {
        return false;
    }
    for (int32 Value : Expected.Enabled)
    {
        if (!Actual.Enabled.Contains(Value)) return false;
    }
    for (const TPair<int32, int32>& Pair : Expected.Weights)
    {
        if (Actual.Weights.FindRef(Pair.Key) != Pair.Value) return false;
    }
    return true;
}

bool CheckCdo(UClass& Class, UFinareAsset& Asset, UObject* ExpectedArchetype, const FState& Expected, FString& OutError)
{
    AFinareActor* Cdo = Cast<AFinareActor>(Class.GetDefaultObject());
    UFinareComponent* Component = Cdo != nullptr ? Cdo->GetFinareComponent() : nullptr;
    FState Actual;
    if (Cdo == nullptr || Component == nullptr || FindFProperty<FProperty>(&Class, TEXT("UnexposedHelper")) != nullptr || Component->GetFName() != FName(TEXT("UnifiedScript")) || Component->GetOuter() != Cdo || Component->GetArchetype() != ExpectedArchetype || Component->ScriptAsset != &Asset || !Component->bStartAutomatically || !Component->bTickAutomatically || !ReadState(*Cdo, Actual) || !SameState(Actual, Expected))
    {
        OutError = FString::Printf(TEXT("Cooked CDO contract failed for %s (component=%s archetype=%s expectedArchetype=%s)."), *Class.GetPathName(), Component != nullptr ? *Component->GetPathName() : TEXT("<null>"), Component != nullptr && Component->GetArchetype() != nullptr ? *Component->GetArchetype()->GetPathName() : TEXT("<null>"), ExpectedArchetype != nullptr ? *ExpectedArchetype->GetPathName() : TEXT("<null>"));
        return false;
    }
    return true;
}

bool CheckInstance(AFinareActor& Actor, UFinareAsset& Asset, UObject* ExpectedArchetype, FString& OutError)
{
    UFinareComponent* Component = Actor.GetFinareComponent();
    if (Component == nullptr || Component->GetFName() != FName(TEXT("UnifiedScript")) || Component->GetOuter() != &Actor || Component->GetArchetype() != ExpectedArchetype || Component->ScriptAsset != &Asset || !Component->bStartAutomatically || !Component->bTickAutomatically)
    {
        OutError = FString::Printf(TEXT("Cooked instance component contract failed for %s."), *Actor.GetPathName());
        return false;
    }
    return true;
}

void DestroyActor(AFinareActor*& Actor)
{
    if (Actor != nullptr)
    {
        if (Actor->HasActorBegunPlay()) Actor->RouteEndPlay(EEndPlayReason::Destroyed);
        if (!Actor->IsActorBeingDestroyed()) Actor->Destroy();
        Actor = nullptr;
    }
}
}

void Run()
{
    using Private::FState;
    FString ReceiptPath;
    if (!Private::ReadArgument(TEXT("FinareInheritedStateReceipt="), ReceiptPath))
    {
        Private::Finish(false, TEXT("FinareInheritedStateReceipt is required."));
        return;
    }
    Private::FReceipt Receipt;
    FString Error;
    if (!Private::LoadReceipt(ReceiptPath, Receipt, Error))
    {
        Private::Finish(false, Error);
        return;
    }
    if (GEngine == nullptr || !FPlatformProperties::RequiresCookedData())
    {
        Private::Finish(false, TEXT("Inherited-state verifier requires a cooked runtime and Engine."));
        return;
    }
    UClass* BaseClass = LoadObject<UClass>(nullptr, *Receipt.BaseClassPath);
    UClass* ChildClass = LoadObject<UClass>(nullptr, *Receipt.ChildClassPath);
    UClass* GrandchildClass = LoadObject<UClass>(nullptr, *Receipt.GrandchildClassPath);
    UFinareAsset* ScriptAsset = LoadObject<UFinareAsset>(nullptr, *Receipt.ScriptAssetPath);
    if (BaseClass == nullptr || ChildClass == nullptr || GrandchildClass == nullptr || ScriptAsset == nullptr || ChildClass->GetSuperClass() != BaseClass || GrandchildClass->GetSuperClass() != ChildClass)
    {
        Private::Finish(false, TEXT("Cooked inherited-state classes or exact parent chain could not be loaded."));
        return;
    }
    AFinareActor* BaseCdo = Cast<AFinareActor>(BaseClass->GetDefaultObject());
    AFinareActor* ChildCdo = Cast<AFinareActor>(ChildClass->GetDefaultObject());
    AFinareActor* GrandchildCdo = Cast<AFinareActor>(GrandchildClass->GetDefaultObject());
    UFinareComponent* BaseComponent = BaseCdo != nullptr ? BaseCdo->GetFinareComponent() : nullptr;
    FString PackagedRoot = Private::NormalizePath(FPaths::Combine(FPlatformProcess::BaseDir(), TEXT("../../")));
    while (PackagedRoot.EndsWith(TEXT("/"))) PackagedRoot.LeftChopInline(1);
    FString ProgramPath;
    FString TypeName;
    FGuid ExpectedScriptGuid;
    const bool bScriptIdValid = Receipt.ScriptClassId.StartsWith(TEXT("us_")) &&
        FGuid::ParseExact(Receipt.ScriptClassId.Mid(3), EGuidFormats::Digits, ExpectedScriptGuid);
    const FString ExpectedStageChunkField = FString::Printf(TEXT("{PluginDir}/Binaries/Managed/InheritedState/%s/BlueprintInheritedStateFixture.uschunk"), *Receipt.FixtureId);
    const FString ExpectedStageAssemblyField = FString::Printf(TEXT("{PluginDir}/Binaries/Managed/InheritedState/%s/BlueprintInheritedStateFixture.dll"), *Receipt.FixtureId);
    const FString ExpectedStageDeclarationField = FString::Printf(TEXT("{PluginDir}/Binaries/Managed/InheritedState/%s/BlueprintInheritedStateFixture.asset.json"), *Receipt.FixtureId);
    if (BaseComponent == nullptr || BaseComponent->ScriptAsset != ScriptAsset || !ScriptAsset->GeneratedClassPath.ToString().Equals(Receipt.BaseClassPath, ESearchCase::CaseSensitive) || !bScriptIdValid || ScriptAsset->ScriptClassId != ExpectedScriptGuid || !ScriptAsset->ScriptTypeName.Equals(Receipt.ScriptTypeName, ESearchCase::CaseSensitive) || !ScriptAsset->CompiledChunkPath.Equals(ExpectedStageChunkField, ESearchCase::CaseSensitive) || !ScriptAsset->ScriptAssemblyPath.Equals(ExpectedStageAssemblyField, ESearchCase::CaseSensitive) || !ScriptAsset->DeclarationPath.Equals(ExpectedStageDeclarationField, ESearchCase::CaseSensitive) || !ScriptAsset->ResolveRuntimeConfiguration(true, ProgramPath, TypeName, Error))
    {
        Private::Finish(false, Error.IsEmpty() ? TEXT("Cooked inherited-state ScriptAsset identity is invalid.") : Error);
        return;
    }
    ProgramPath = Private::NormalizePath(ProgramPath);
    const FString ExpectedProgram = Private::NormalizePath(FPaths::Combine(PackagedRoot, TEXT("Plugins/Finare/Binaries/Managed/InheritedState"), Receipt.FixtureId, TEXT("BlueprintInheritedStateFixture.uschunk")));
    FString HostFxrPath = Private::NormalizePath(FFinareCoreClrHost::Get().GetHostFxrPath());
    if (!ProgramPath.Equals(ExpectedProgram, ESearchCase::IgnoreCase) || !IFileManager::Get().FileExists(*ProgramPath) || !Private::IsUnder(PackagedRoot, ProgramPath) || !HostFxrPath.Contains(TEXT("/Plugins/Finare/Binaries/ThirdParty/DotNet/10.0/"), ESearchCase::IgnoreCase) || !Private::IsUnder(PackagedRoot, HostFxrPath))
    {
        Private::Finish(false, FString::Printf(TEXT("Cooked inherited-state paths are invalid; program=%s expected=%s hostfxr=%s."), *ProgramPath, *ExpectedProgram, *HostFxrPath));
        return;
    }
    const FState BaseState{3, {2, 4}, {5, 7}, {{11, 13}, {17, 19}}, 0};
    const FState ChildState{9, {3, 8, 13}, {4, 10}, {{2, 5}, {7, 11}}, 0};
    if (!Private::CheckCdo(*BaseClass, *ScriptAsset, Cast<AFinareActor>(AFinareActor::StaticClass()->GetDefaultObject())->GetFinareComponent(), BaseState, Error) || !Private::CheckCdo(*ChildClass, *ScriptAsset, BaseComponent, ChildState, Error) || !Private::CheckCdo(*GrandchildClass, *ScriptAsset, Cast<AFinareActor>(ChildClass->GetDefaultObject())->GetFinareComponent(), ChildState, Error))
    {
        Private::Finish(false, Error);
        return;
    }
    const int32 InitialWorldCount = FFinareCoreClrHost::Get().GetManagedWorldCount();
    UWorld* FirstWorld = UWorld::CreateWorld(EWorldType::Game, false);
    UWorld* SecondWorld = UWorld::CreateWorld(EWorldType::Game, false);
    FWorldContext* FirstContext = nullptr;
    FWorldContext* SecondContext = nullptr;
    UFinareWorldSubsystem* FirstHost = nullptr;
    UFinareWorldSubsystem* SecondHost = nullptr;
    AFinareActor* FirstActor = nullptr;
    AFinareActor* SecondActor = nullptr;
    auto Cleanup = [&]()
    {
        Private::DestroyActor(FirstActor);
        Private::DestroyActor(SecondActor);
        CollectGarbage(RF_NoFlags);
        if (FirstContext != nullptr && FirstWorld != nullptr)
        {
            GEngine->DestroyWorldContext(FirstWorld);
        }
        if (SecondContext != nullptr && SecondWorld != nullptr)
        {
            GEngine->DestroyWorldContext(SecondWorld);
        }
        FirstContext = nullptr;
        SecondContext = nullptr;
        if (FirstWorld != nullptr)
        {
            FirstWorld->RemoveFromRoot();
            FirstWorld->DestroyWorld(false);
            FirstWorld = nullptr;
        }
        if (SecondWorld != nullptr)
        {
            SecondWorld->RemoveFromRoot();
            SecondWorld->DestroyWorld(false);
            SecondWorld = nullptr;
        }
        return FFinareCoreClrHost::Get().GetManagedWorldCount() == InitialWorldCount;
    };
    if (FirstWorld == nullptr || SecondWorld == nullptr)
    {
        const bool bClean = Cleanup();
        Private::Finish(false, bClean ? TEXT("Could not create cooked inherited-state Worlds.") : TEXT("Could not create cooked inherited-state Worlds; cleanup leaked a managed World."));
        return;
    }
    FirstWorld->AddToRoot();
    SecondWorld->AddToRoot();
    FirstContext = &GEngine->CreateNewWorldContext(EWorldType::Game);
    FirstContext->SetCurrentWorld(FirstWorld);
    SecondContext = &GEngine->CreateNewWorldContext(EWorldType::Game);
    SecondContext->SetCurrentWorld(SecondWorld);
    FirstWorld->InitializeActorsForPlay(FURL());
    SecondWorld->InitializeActorsForPlay(FURL());
    FirstHost = FirstWorld->GetSubsystem<UFinareWorldSubsystem>();
    SecondHost = SecondWorld->GetSubsystem<UFinareWorldSubsystem>();
    FirstActor = FirstWorld->SpawnActor<AFinareActor>(ChildClass);
    SecondActor = SecondWorld->SpawnActor<AFinareActor>(GrandchildClass);
    const FIntProperty* Seed = FindFProperty<FIntProperty>(ChildClass, TEXT("Seed"));
    const FArrayProperty* Values = FindFProperty<FArrayProperty>(ChildClass, TEXT("Values"));
    const FSetProperty* Enabled = FindFProperty<FSetProperty>(ChildClass, TEXT("Enabled"));
    const FMapProperty* Weights = FindFProperty<FMapProperty>(ChildClass, TEXT("Weights"));
    FState FirstState{12, {1, 6}, {8, 9}, {{3, 10}, {14, 20}}, 0};
    if (FirstActor == nullptr || SecondActor == nullptr || FirstHost == nullptr || SecondHost == nullptr || Seed == nullptr || Values == nullptr || Enabled == nullptr || Weights == nullptr)
    {
        const bool bClean = Cleanup();
        Private::Finish(false, bClean ? TEXT("Cooked inherited-state runtime fixture could not be created.") : TEXT("Cooked inherited-state runtime fixture could not be created; cleanup leaked a managed World."));
        return;
    }
    Seed->SetPropertyValue_InContainer(FirstActor, FirstState.Seed); Private::SetArray(*Values, *FirstActor, FirstState.Values); Private::SetSet(*Enabled, *FirstActor, {8, 9}); Private::SetMap(*Weights, *FirstActor, {{3, 10}, {14, 20}});
    FString Failure;
    UFinareComponent* ChildCdoComponent = Cast<AFinareActor>(ChildClass->GetDefaultObject())->GetFinareComponent();
    UFinareComponent* GrandchildCdoComponent = Cast<AFinareActor>(GrandchildClass->GetDefaultObject())->GetFinareComponent();
    if (!Private::CheckInstance(*FirstActor, *ScriptAsset, ChildCdoComponent, Failure) || !Private::CheckInstance(*SecondActor, *ScriptAsset, GrandchildCdoComponent, Failure))
    {
        Cleanup(); Private::Finish(false, Failure); return;
    }
    FirstActor->DispatchBeginPlay(); SecondActor->DispatchBeginPlay();
    UFinareComponent* FirstComponent = FirstActor->GetFinareComponent();
    UFinareComponent* SecondComponent = SecondActor->GetFinareComponent();
    const FState SecondState = ChildState;
    FState ActualFirst;
    FState ActualSecond;
    if (FirstComponent == nullptr || SecondComponent == nullptr || !FirstComponent->bScriptRunning || !SecondComponent->bScriptRunning || FirstHost->GetManagedScriptCount() != 1 || SecondHost->GetManagedScriptCount() != 1 || FirstHost->GetGameplayLifecycleClaimCount() != 1 || SecondHost->GetGameplayLifecycleClaimCount() != 1 || FFinareCoreClrHost::Get().GetManagedWorldCount() != InitialWorldCount + 2 || !Private::ReadState(*FirstActor, ActualFirst) || !Private::ReadState(*SecondActor, ActualSecond) || !Private::SameState(ActualFirst, FirstState) || !Private::SameState(ActualSecond, SecondState) || FirstComponent->ManagedState != FString::Printf(TEXT("InheritedState:%d:Ticks:0"), Private::Signature(FirstState)) || SecondComponent->ManagedState != FString::Printf(TEXT("InheritedState:%d:Ticks:0"), Private::Signature(SecondState)) || !FMath::IsNearlyEqual(FirstActor->GetActorRotation().Yaw, FRotator::NormalizeAxis(static_cast<float>(Private::Signature(FirstState) % 360)), 0.01f) || !FMath::IsNearlyEqual(SecondActor->GetActorRotation().Yaw, FRotator::NormalizeAxis(static_cast<float>(Private::Signature(SecondState) % 360)), 0.01f))
    {
        Failure = TEXT("Cooked inherited-state BeginPlay or complete managed state verification failed.");
    }
    FString FirstGeneration;
    FFinareRuntimeDebugContext FirstContextBeforeReload;
    FFinareRuntimeDebugContext SecondContextBeforeReload;
    if (Failure.IsEmpty())
    {
        FirstHost->BeginManagedFrame(); FirstComponent->TickComponent(0.016f, LEVELTICK_All, nullptr); FirstHost->EndManagedFrame();
        FState FirstTick = FirstState; FirstTick.TickCount = 1;
        if (!Private::ReadState(*FirstActor, ActualFirst) || !Private::SameState(ActualFirst, FirstTick) || Private::ReadState(*SecondActor, ActualSecond) == false || !Private::SameState(ActualSecond, SecondState) || FirstComponent->ManagedState != FString::Printf(TEXT("InheritedState:%d:Ticks:1"), Private::Signature(FirstState))) Failure = TEXT("Cooked First World tick verification failed.");
        if (Failure.IsEmpty()) { SecondHost->BeginManagedFrame(); SecondComponent->TickComponent(0.016f, LEVELTICK_All, nullptr); SecondHost->EndManagedFrame(); }
    }
    if (Failure.IsEmpty())
    {
        FState SecondTick = SecondState; SecondTick.TickCount = 1;
        if (!Private::ReadState(*SecondActor, ActualSecond) || !Private::SameState(ActualSecond, SecondTick) || SecondComponent->ManagedState != FString::Printf(TEXT("InheritedState:%d:Ticks:1"), Private::Signature(SecondState))) Failure = TEXT("Cooked Second World isolated tick verification failed.");
        if (!FirstComponent->GetRuntimeDebugContext(FirstContextBeforeReload) || !SecondComponent->GetRuntimeDebugContext(SecondContextBeforeReload)) Failure = TEXT("Cooked inherited-state runtime identity was unavailable before reload.");
        FirstGeneration = FirstComponent->ActiveGeneration;
        if (!Failure.IsEmpty())
        {
            // Preserve the existing failure and let the common cleanup below run.
        }
        else if (!FirstComponent->ReloadScript() || FirstComponent->ActiveGeneration == FirstGeneration)
        {
            Failure = TEXT("Cooked First World same-chunk reload failed.");
        }
    }
    if (Failure.IsEmpty())
    {
        FFinareRuntimeDebugContext FirstContextAfterReload;
        FFinareRuntimeDebugContext SecondContextAfterFirstReload;
        FState FirstReloadState = FirstState; FirstReloadState.TickCount = 1;
        FState SecondAfterFirstReloadState = SecondState; SecondAfterFirstReloadState.TickCount = 1;
        if (!FirstComponent->bScriptRunning || !SecondComponent->bScriptRunning || !Private::ReadState(*FirstActor, ActualFirst) || !Private::ReadState(*SecondActor, ActualSecond) || !Private::SameState(ActualFirst, FirstReloadState) || !Private::SameState(ActualSecond, SecondAfterFirstReloadState) || !FirstComponent->GetRuntimeDebugContext(FirstContextAfterReload) || !SecondComponent->GetRuntimeDebugContext(SecondContextAfterFirstReload) || FirstContextAfterReload.Generation != FirstContextBeforeReload.Generation + 1 || FirstContextAfterReload.WorldHostId != FirstContextBeforeReload.WorldHostId || FirstContextAfterReload.InstanceId != FirstContextBeforeReload.InstanceId || FirstContextAfterReload.HostHandle != FirstContextBeforeReload.HostHandle || FirstContextAfterReload.TargetHandle != FirstContextBeforeReload.TargetHandle || SecondContextAfterFirstReload.Generation != SecondContextBeforeReload.Generation || SecondContextAfterFirstReload.WorldHostId != SecondContextBeforeReload.WorldHostId || SecondContextAfterFirstReload.InstanceId != SecondContextBeforeReload.InstanceId || SecondContextAfterFirstReload.HostHandle != SecondContextBeforeReload.HostHandle || SecondContextAfterFirstReload.TargetHandle != SecondContextBeforeReload.TargetHandle)
        {
            Failure = TEXT("Cooked First reload did not preserve complete state or runtime identity.");
        }
    }
    if (Failure.IsEmpty())
    {
        FirstHost->BeginManagedFrame(); FirstComponent->TickComponent(0.016f, LEVELTICK_All, nullptr); FirstHost->EndManagedFrame();
        FState FirstReloadTick = FirstState; FirstReloadTick.TickCount = 2;
        FState SecondAfterFirstReloadTick = SecondState; SecondAfterFirstReloadTick.TickCount = 1;
        if (!Private::ReadState(*FirstActor, ActualFirst) || !Private::SameState(ActualFirst, FirstReloadTick) || !Private::ReadState(*SecondActor, ActualSecond) || !Private::SameState(ActualSecond, SecondAfterFirstReloadTick)) Failure = TEXT("Cooked First World reload tick was not isolated from Second World.");
    }
    auto DestroyFirstWorld = [&]()
    {
        Private::DestroyActor(FirstActor);
        if (FirstHost->GetManagedScriptCount() != 0 || FirstHost->GetGameplayLifecycleClaimCount() != 0) Failure = TEXT("Cooked First World did not release its script and lifecycle claim.");
        CollectGarbage(RF_NoFlags);
        if (FirstContext != nullptr && FirstWorld != nullptr) GEngine->DestroyWorldContext(FirstWorld);
        FirstContext = nullptr;
        if (FirstWorld != nullptr) { FirstWorld->RemoveFromRoot(); FirstWorld->DestroyWorld(false); FirstWorld = nullptr; }
        if (Failure.IsEmpty() && FFinareCoreClrHost::Get().GetManagedWorldCount() != InitialWorldCount + 1) Failure = TEXT("Cooked First World teardown did not leave exactly one managed World.");
    };
    if (Failure.IsEmpty())
    {
        DestroyFirstWorld();
    }
    if (Failure.IsEmpty())
    {
        SecondHost->BeginManagedFrame(); SecondComponent->TickComponent(0.016f, LEVELTICK_All, nullptr); SecondHost->EndManagedFrame();
        FState SecondAfterFirstTeardownTick = SecondState; SecondAfterFirstTeardownTick.TickCount = 2;
        if (!Private::ReadState(*SecondActor, ActualSecond) || !Private::SameState(ActualSecond, SecondAfterFirstTeardownTick)) Failure = TEXT("Cooked Second World did not continue independently after First teardown.");
    }
    if (Failure.IsEmpty())
    {
        const FString SecondGenerationBeforeReload = SecondComponent->ActiveGeneration;
        if (!SecondComponent->ReloadScript() || SecondComponent->ActiveGeneration == SecondGenerationBeforeReload)
        {
            Failure = TEXT("Cooked Second World same-chunk reload failed.");
        }
    }
    if (Failure.IsEmpty())
    {
        FFinareRuntimeDebugContext SecondContextAfterReload;
        FState SecondReloadState;
        FState SecondReloadExpected = SecondState; SecondReloadExpected.TickCount = 2;
        if (!SecondComponent->bScriptRunning || !Private::ReadState(*SecondActor, SecondReloadState) || !Private::SameState(SecondReloadState, SecondReloadExpected) || !SecondComponent->GetRuntimeDebugContext(SecondContextAfterReload) || SecondContextAfterReload.WorldHostId != SecondContextBeforeReload.WorldHostId || SecondContextAfterReload.InstanceId != SecondContextBeforeReload.InstanceId || SecondContextAfterReload.HostHandle != SecondContextBeforeReload.HostHandle || SecondContextAfterReload.TargetHandle != SecondContextBeforeReload.TargetHandle || SecondContextAfterReload.Generation == SecondContextBeforeReload.Generation)
        {
            Failure = TEXT("Cooked Second reload did not preserve complete state or runtime identity.");
        }
    }
    if (Failure.IsEmpty())
    {
        SecondHost->BeginManagedFrame(); SecondComponent->TickComponent(0.016f, LEVELTICK_All, nullptr); SecondHost->EndManagedFrame();
        FState SecondFinalState = SecondState; SecondFinalState.TickCount = 3;
        if (!Private::ReadState(*SecondActor, ActualSecond) || !Private::SameState(ActualSecond, SecondFinalState)) Failure = TEXT("Cooked Second World final tick verification failed.");
    }
    Private::DestroyActor(SecondActor);
    if (Failure.IsEmpty() && (SecondHost->GetManagedScriptCount() != 0 || SecondHost->GetGameplayLifecycleClaimCount() != 0)) Failure = TEXT("Cooked Second World did not release its script and lifecycle claim.");
    const bool bClean = Cleanup();
    if (!bClean && Failure.IsEmpty()) Failure = TEXT("Cooked inherited-state cleanup leaked a managed World.");
    Private::Finish(Failure.IsEmpty(), Failure.IsEmpty() ? FString::Printf(TEXT("Cooked inherited state passed; program=%s hostfxr=%s."), *ProgramPath, *HostFxrPath) : Failure);
}
}

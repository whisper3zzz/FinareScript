#include "UnifiedScriptActiveClassOpsVerification.h"

#include "Engine/Engine.h"
#include "Engine/World.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformMisc.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformProperties.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::ActiveClassOpsVerification
{
namespace Private
{
struct FBranch
{
    FString Role;
    FString ClassId;
    FString TypeName;
    FString BaseClassPath;
    FString ChildClassPath;
    FString GrandchildClassPath;
    FString ScriptAssetPath;
    FString DeclarationAssetPath;
};

struct FReceipt
{
    FString FixtureId;
    FString PackageDirectory;
    FString StageDirectory;
    FString ChunkPath;
    FString AssemblyPath;
    FString DeclarationPath;
    TArray<FBranch> Branches;
};

bool ReadArg(const TCHAR* Name, FString& Out)
{
    if (!FParse::Value(FCommandLine::Get(), Name, Out))
    {
        return false;
    }
    Out.TrimQuotesInline();
    return !Out.IsEmpty();
}

void Finish(bool bSuccess, const FString& Detail)
{
    FString ResultPath;
    const bool bHasResult = ReadArg(TEXT("UnifiedScriptActiveClassOpsResult="), ResultPath);
    const FString Text = FString::Printf(TEXT("%s\n%s\n"), bSuccess ? TEXT("PASS") : TEXT("FAIL"), *Detail);
    if (bHasResult && !FFileHelper::SaveStringToFile(Text, *ResultPath, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
    {
        bSuccess = false;
        UE_LOG(LogTemp, Error, TEXT("Could not write active class operations result '%s'."), *ResultPath);
    }
    UE_LOG(LogTemp, Display, TEXT("UNIFIEDSCRIPT_ACTIVE_CLASS_OPS %s %s"), bSuccess ? TEXT("PASS") : TEXT("FAIL"), *Detail);
    FPlatformMisc::RequestExitWithStatus(false, bSuccess ? 0 : 1, TEXT("UnifiedScriptActiveClassOpsVerify"));
}

bool RequiredString(const TSharedPtr<FJsonObject>& Object, const TCHAR* Name, FString& Out, FString& Error)
{
    if (!Object.IsValid() || !Object->TryGetStringField(Name, Out) || Out.IsEmpty())
    {
        Error = FString::Printf(TEXT("Active class operations receipt field '%s' is missing."), Name);
        return false;
    }
    return true;
}

bool LoadReceipt(const FString& Path, FReceipt& Out, FString& Error)
{
    FString Text;
    TSharedPtr<FJsonObject> Root;
    if (!FFileHelper::LoadFileToString(Text, *Path))
    {
        Error = FString::Printf(TEXT("Could not parse active class operations receipt '%s'."), *Path);
        return false;
    }
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Text);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        Error = FString::Printf(TEXT("Could not parse active class operations receipt '%s'."), *Path);
        return false;
    }
    FString Schema;
    if (!RequiredString(Root, TEXT("schemaVersion"), Schema, Error) ||
        !Schema.Equals(TEXT("unifiedscript.active-class-ops-cook-fixture/v0.1"), ESearchCase::CaseSensitive) ||
        !RequiredString(Root, TEXT("fixtureId"), Out.FixtureId, Error) ||
        !RequiredString(Root, TEXT("packageDirectory"), Out.PackageDirectory, Error) ||
        !RequiredString(Root, TEXT("stageDirectory"), Out.StageDirectory, Error) ||
        !RequiredString(Root, TEXT("chunkPath"), Out.ChunkPath, Error) ||
        !RequiredString(Root, TEXT("assemblyPath"), Out.AssemblyPath, Error) ||
        !RequiredString(Root, TEXT("declarationPath"), Out.DeclarationPath, Error))
    {
        if (Error.IsEmpty()) Error = TEXT("Active class operations receipt schema is invalid.");
        return false;
    }
    FGuid FixtureGuid;
    if (Out.FixtureId.Len() != 32 ||
        !Out.FixtureId.Equals(Out.FixtureId.ToUpper(), ESearchCase::CaseSensitive) ||
        !FGuid::ParseExact(Out.FixtureId, EGuidFormats::Digits, FixtureGuid) ||
        !FixtureGuid.IsValid() ||
        !Out.PackageDirectory.Equals(FString::Printf(TEXT("/Game/M13C3ActiveClassOps_%s"), *Out.FixtureId), ESearchCase::CaseSensitive) ||
        !Out.StageDirectory.Equals(FString::Printf(TEXT("{PluginDir}/Binaries/Managed/ActiveClassOps/%s"), *Out.FixtureId), ESearchCase::CaseSensitive))
    {
        Error = TEXT("Active class operations receipt has invalid fixture or stage identity.");
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* BranchValues = nullptr;
    if (!Root->TryGetArrayField(TEXT("branches"), BranchValues) || BranchValues == nullptr || BranchValues->Num() != 2)
    {
        Error = TEXT("Active class operations receipt must contain exactly two branches.");
        return false;
    }
    TSet<FString> Roles;
    for (const TSharedPtr<FJsonValue>& Value : *BranchValues)
    {
        const TSharedPtr<FJsonObject> Object = Value.IsValid() ? Value->AsObject() : nullptr;
        FBranch Branch;
        if (!RequiredString(Object, TEXT("role"), Branch.Role, Error) ||
            !RequiredString(Object, TEXT("classId"), Branch.ClassId, Error) ||
            !RequiredString(Object, TEXT("typeName"), Branch.TypeName, Error) ||
            !RequiredString(Object, TEXT("baseClassPath"), Branch.BaseClassPath, Error) ||
            !RequiredString(Object, TEXT("childClassPath"), Branch.ChildClassPath, Error) ||
            !RequiredString(Object, TEXT("grandchildClassPath"), Branch.GrandchildClassPath, Error) ||
            !RequiredString(Object, TEXT("scriptAssetPath"), Branch.ScriptAssetPath, Error) ||
            !RequiredString(Object, TEXT("declarationAssetPath"), Branch.DeclarationAssetPath, Error))
        {
            return false;
        }
        if ((Branch.Role != TEXT("renamed") && Branch.Role != TEXT("duplicate")) || Roles.Contains(Branch.Role) ||
            !Branch.ClassId.StartsWith(TEXT("us_")) || Branch.ClassId.Len() != 35 ||
            !Branch.ClassId.Mid(3).Equals(Branch.ClassId.Mid(3).ToLower(), ESearchCase::CaseSensitive))
        {
            Error = TEXT("Active class operations receipt has invalid or duplicate branch identity.");
            return false;
        }
        FGuid ClassGuid;
        if (!FGuid::ParseExact(Branch.ClassId.Mid(3), EGuidFormats::Digits, ClassGuid) || !ClassGuid.IsValid())
        {
            Error = TEXT("Active class operations branch has an invalid ClassId.");
            return false;
        }
        Roles.Add(Branch.Role);
        Out.Branches.Add(MoveTemp(Branch));
    }
    const TArray<TSharedPtr<FJsonValue>>* Packages = nullptr;
    if (!Root->TryGetArrayField(TEXT("packages"), Packages) || Packages == nullptr || Packages->Num() != 8)
    {
        Error = TEXT("Active class operations receipt must contain exactly eight packages.");
        return false;
    }
    TSet<FString> PackageNames;
    for (const TSharedPtr<FJsonValue>& Value : *Packages)
    {
        const TSharedPtr<FJsonObject> Object = Value.IsValid() ? Value->AsObject() : nullptr;
        FString PackageName;
        FString PackageFilename;
        if (!RequiredString(Object, TEXT("packageName"), PackageName, Error) ||
            !RequiredString(Object, TEXT("packageFilename"), PackageFilename, Error) ||
            PackageNames.Contains(PackageName))
        {
            Error = TEXT("Active class operations receipt contains an invalid package set.");
            return false;
        }
        PackageNames.Add(PackageName);
    }
    return PackageNames.Num() == 8;
}

FString Normalize(FString Path)
{
    Path = FPaths::ConvertRelativePathToFull(Path);
    FPaths::NormalizeFilename(Path);
    while (Path.Len() > 1 && Path.EndsWith(TEXT("/")))
    {
        Path.LeftChopInline(1);
    }
    return Path;
}

bool Under(const FString& Root, const FString& Path)
{
    return Path.Equals(Root, ESearchCase::IgnoreCase) || Path.StartsWith(Root + TEXT("/"), ESearchCase::IgnoreCase);
}

bool ResolveClassId(const FString& Value, FGuid& Out)
{
    return Value.StartsWith(TEXT("us_")) && Value.Len() == 35 &&
        FGuid::ParseExact(Value.Mid(3), EGuidFormats::Digits, Out) && Out.IsValid();
}

UFunction* FindManagedFunction(AActor& Actor, const TCHAR* ManagedName)
{
    for (UClass* Candidate = Actor.GetClass(); Candidate != nullptr; Candidate = Candidate->GetSuperClass())
    {
        const UUnifiedScriptBlueprintGeneratedClass* Generated = Cast<UUnifiedScriptBlueprintGeneratedClass>(Candidate);
        if (!Generated) continue;
        for (const TPair<FName, FUnifiedScriptManagedFunctionBinding>& Pair : Generated->ManagedFunctionBindings)
        {
            if (Pair.Value.ManagedName == ManagedName)
            {
                if (UFunction* Function = Actor.FindFunction(Pair.Key)) return Function;
            }
        }
    }
    return nullptr;
}

bool CallAddOne(AUnifiedScriptActor& Actor, int32 Input, int32 Expected, FString& Error)
{
    UFunction* Function = FindManagedFunction(Actor, TEXT("AddOne"));
    if (!Function) { Error = TEXT("Cooked active branch has no reflected AddOne."); return false; }
    struct FParams { int32 Value = 0; int32 ReturnValue = 0; } Params;
    Params.Value = Input;
    Actor.ProcessEvent(Function, &Params);
    if (Params.ReturnValue != Expected) { Error = FString::Printf(TEXT("Cooked AddOne returned %d, expected %d."), Params.ReturnValue, Expected); return false; }
    return true;
}

bool CallReceiveTick(AUnifiedScriptActor& Actor, int32 Expected, FString& Error)
{
    UFunction* Function = Actor.FindFunction(TEXT("ReceiveTick"));
    if (!Function) { Error = TEXT("Cooked active branch has no reflected ReceiveTick."); return false; }
    const UUnifiedScriptBlueprintGeneratedClass* Generated = Cast<UUnifiedScriptBlueprintGeneratedClass>(Actor.GetClass());
    bool bRoute = false;
    for (UClass* Candidate = Actor.GetClass(); Candidate != nullptr && !bRoute; Candidate = Candidate->GetSuperClass())
    {
        if (const UUnifiedScriptBlueprintGeneratedClass* CandidateGenerated = Cast<UUnifiedScriptBlueprintGeneratedClass>(Candidate))
        {
            for (const TPair<FName, FUnifiedScriptManagedFunctionBinding>& Pair : CandidateGenerated->ManagedFunctionBindings)
            {
                if (Pair.Value.BlueprintOverrideDeclaringTypePath == TEXT("/Script/Engine.Actor") && Pair.Value.BlueprintOverrideFunctionName == TEXT("ReceiveTick"))
                {
                    bRoute = true;
                    break;
                }
            }
        }
    }
    if (!bRoute) { Error = TEXT("Cooked ReceiveTick has no Engine.Actor override route."); return false; }
    FProperty* Delta = FindFProperty<FProperty>(Function, TEXT("DeltaSeconds"));
    FFloatProperty* FloatDelta = CastField<FFloatProperty>(Delta);
    if (!FloatDelta) { Error = TEXT("Cooked ReceiveTick has no float DeltaSeconds parameter."); return false; }
    FStructOnScope Parameters(Function);
    FloatDelta->SetPropertyValue_InContainer(Parameters.GetStructMemory(), 0.1f);
    Actor.ProcessEvent(Function, Parameters.GetStructMemory());
    const FIntProperty* Count = FindFProperty<FIntProperty>(Actor.GetClass(), TEXT("OverrideCount"));
    if (!Count || Count->GetPropertyValue_InContainer(&Actor) != Expected)
    {
        Error = TEXT("Cooked ReceiveTick did not preserve the expected managed override state.");
        return false;
    }
    return true;
}

bool CheckCdo(UClass& Class, UUnifiedScriptAsset& Asset, UObject* ExpectedArchetype, const FString& TypeName, FString& Error)
{
    AUnifiedScriptActor* Cdo = Cast<AUnifiedScriptActor>(Class.GetDefaultObject());
    UUnifiedScriptComponent* Component = Cdo ? Cdo->GetUnifiedScriptComponent() : nullptr;
    if (!Cdo || !Component || Component->GetFName() != FName(TEXT("UnifiedScript")) || Component->GetOuter() != Cdo || Component->GetArchetype() != ExpectedArchetype || Component->ScriptAsset != &Asset || !Component->bStartAutomatically || !Component->bTickAutomatically || !Asset.ScriptTypeName.Equals(TypeName, ESearchCase::CaseSensitive))
    {
        Error = FString::Printf(TEXT("Cooked active CDO contract failed for %s."), *Class.GetPathName());
        return false;
    }
    return true;
}

bool CheckInstance(AUnifiedScriptActor& Actor, UUnifiedScriptAsset& Asset, UObject* ExpectedArchetype, FString& Error)
{
    UUnifiedScriptComponent* Component = Actor.GetUnifiedScriptComponent();
    if (!Component || Component->GetFName() != FName(TEXT("UnifiedScript")) || Component->GetOuter() != &Actor || Component->GetArchetype() != ExpectedArchetype || Component->ScriptAsset != &Asset || !Component->bStartAutomatically || !Component->bTickAutomatically)
    {
        Error = FString::Printf(TEXT("Cooked active instance component contract failed for %s."), *Actor.GetPathName());
        return false;
    }
    return true;
}

void DestroyActor(AUnifiedScriptActor*& Actor)
{
    if (Actor)
    {
        if (Actor->HasActorBegunPlay()) Actor->RouteEndPlay(EEndPlayReason::Destroyed);
        if (!Actor->IsActorBeingDestroyed()) Actor->Destroy();
        Actor = nullptr;
    }
}
}

void Run()
{
    FString ReceiptPath;
    if (!Private::ReadArg(TEXT("UnifiedScriptActiveClassOpsReceipt="), ReceiptPath))
    {
        Private::Finish(false, TEXT("UnifiedScriptActiveClassOpsReceipt is required."));
        return;
    }
    Private::FReceipt Receipt;
    FString Error;
    if (!Private::LoadReceipt(ReceiptPath, Receipt, Error) || GEngine == nullptr || !FPlatformProperties::RequiresCookedData())
    {
        Private::Finish(false, Error.IsEmpty() ? TEXT("Active class operations verifier requires a cooked Engine.") : Error);
        return;
    }
    struct FLoadedBranch { UClass* Base = nullptr; UClass* Child = nullptr; UClass* Grandchild = nullptr; UUnifiedScriptAsset* Asset = nullptr; FString ProgramPath; };
    FLoadedBranch Branches[2];
    int32 RenamedIndex = INDEX_NONE;
    int32 DuplicateIndex = INDEX_NONE;
    const FString PackagedRoot = Private::Normalize(FPaths::Combine(FPlatformProcess::BaseDir(), TEXT("../../")));
    const FString ExpectedChunkField = FString::Printf(TEXT("{PluginDir}/Binaries/Managed/ActiveClassOps/%s/ActiveClassAssetOperationsFixture.uschunk"), *Receipt.FixtureId);
    const FString ExpectedAssemblyField = FString::Printf(TEXT("{PluginDir}/Binaries/Managed/ActiveClassOps/%s/ActiveClassAssetOperationsFixture.dll"), *Receipt.FixtureId);
    const FString ExpectedDeclarationField = FString::Printf(TEXT("{PluginDir}/Binaries/Managed/ActiveClassOps/%s/ActiveClassAssetOperationsFixture.asset.json"), *Receipt.FixtureId);
    const FString ExpectedProgram = Private::Normalize(FPaths::Combine(PackagedRoot, TEXT("Plugins/UEUnifiedScript/Binaries/Managed/ActiveClassOps"), Receipt.FixtureId, TEXT("ActiveClassAssetOperationsFixture.uschunk")));
    for (int32 Index = 0; Index < Receipt.Branches.Num() && Index < 2; ++Index)
    {
        const Private::FBranch& Branch = Receipt.Branches[Index];
        FLoadedBranch& Loaded = Branches[Index];
        Loaded.Base = LoadObject<UClass>(nullptr, *Branch.BaseClassPath);
        Loaded.Child = LoadObject<UClass>(nullptr, *Branch.ChildClassPath);
        Loaded.Grandchild = LoadObject<UClass>(nullptr, *Branch.GrandchildClassPath);
        Loaded.Asset = LoadObject<UUnifiedScriptAsset>(nullptr, *Branch.ScriptAssetPath);
        FString TypeName;
        FGuid ClassId;
        if (!Loaded.Base || !Loaded.Child || !Loaded.Grandchild || !Loaded.Asset || Loaded.Child->GetSuperClass() != Loaded.Base || Loaded.Grandchild->GetSuperClass() != Loaded.Child || !Private::ResolveClassId(Branch.ClassId, ClassId) || Loaded.Asset->ScriptClassId != ClassId || !Loaded.Asset->ScriptTypeName.Equals(Branch.TypeName, ESearchCase::CaseSensitive) || !Loaded.Asset->GeneratedClassPath.ToString().Equals(Branch.BaseClassPath, ESearchCase::CaseSensitive) || !Loaded.Asset->DeclarationAssetPath.Equals(Branch.DeclarationAssetPath, ESearchCase::CaseSensitive) || !Loaded.Asset->CompiledChunkPath.Equals(ExpectedChunkField, ESearchCase::CaseSensitive) || !Loaded.Asset->ScriptAssemblyPath.Equals(ExpectedAssemblyField, ESearchCase::CaseSensitive) || !Loaded.Asset->DeclarationPath.Equals(ExpectedDeclarationField, ESearchCase::CaseSensitive) || !Loaded.Asset->ResolveRuntimeConfiguration(true, Loaded.ProgramPath, TypeName, Error))
        {
            Private::Finish(false, Error.IsEmpty() ? FString::Printf(TEXT("Cooked active branch identity failed for %s."), *Branch.Role) : Error); return;
        }
        Loaded.ProgramPath = Private::Normalize(Loaded.ProgramPath);
        const bool bProgramMatches = Loaded.ProgramPath.Equals(ExpectedProgram, ESearchCase::IgnoreCase);
        const bool bProgramExists = IFileManager::Get().FileExists(*Loaded.ProgramPath);
        const bool bProgramContained = Private::Under(PackagedRoot, Loaded.ProgramPath);
        if (!bProgramMatches || !bProgramExists || !bProgramContained)
        {
            Private::Finish(false, FString::Printf(TEXT("Cooked active branch did not resolve its archive-local chunk. actual='%s' expected='%s' root='%s' exists=%s contained=%s."), *Loaded.ProgramPath, *ExpectedProgram, *PackagedRoot, bProgramExists ? TEXT("true") : TEXT("false"), bProgramContained ? TEXT("true") : TEXT("false"))); return;
        }
        AUnifiedScriptActor* BaseCdo = Cast<AUnifiedScriptActor>(Loaded.Base->GetDefaultObject());
        UUnifiedScriptComponent* BaseComponent = BaseCdo ? BaseCdo->GetUnifiedScriptComponent() : nullptr;
        AUnifiedScriptActor* ChildCdo = Cast<AUnifiedScriptActor>(Loaded.Child->GetDefaultObject());
        UUnifiedScriptComponent* ChildComponent = ChildCdo ? ChildCdo->GetUnifiedScriptComponent() : nullptr;
        if (!BaseComponent || !ChildComponent || !Private::CheckCdo(*Loaded.Base, *Loaded.Asset, Cast<AUnifiedScriptActor>(AUnifiedScriptActor::StaticClass()->GetDefaultObject())->GetUnifiedScriptComponent(), Branch.TypeName, Error) || !Private::CheckCdo(*Loaded.Child, *Loaded.Asset, BaseComponent, Branch.TypeName, Error) || !Private::CheckCdo(*Loaded.Grandchild, *Loaded.Asset, ChildComponent, Branch.TypeName, Error))
        {
            Private::Finish(false, Error.IsEmpty() ? TEXT("Cooked active CDO/archetype contract failed.") : Error); return;
        }
        if (Branch.Role == TEXT("renamed")) RenamedIndex = Index; else if (Branch.Role == TEXT("duplicate")) DuplicateIndex = Index;
    }
    if (RenamedIndex == INDEX_NONE || DuplicateIndex == INDEX_NONE || Branches[RenamedIndex].Asset->ScriptClassId == Branches[DuplicateIndex].Asset->ScriptClassId)
    {
        Private::Finish(false, TEXT("Cooked active branches are not distinct renamed/duplicate identities.")); return;
    }
    const int32 InitialWorldCount = FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    UWorld* Worlds[2] = { UWorld::CreateWorld(EWorldType::Game, false), UWorld::CreateWorld(EWorldType::Game, false) };
    FWorldContext* Contexts[2] = { nullptr, nullptr };
    UUnifiedScriptWorldSubsystem* Hosts[2] = { nullptr, nullptr };
    AUnifiedScriptActor* Actors[2] = { nullptr, nullptr };
    UUnifiedScriptComponent* Components[2] = { nullptr, nullptr };
    uint64 WorldHostIds[2] = { 0, 0 };
    auto Cleanup = [&]()
    {
        for (int32 Index = 0; Index < 2; ++Index)
        {
            Private::DestroyActor(Actors[Index]);
            const bool bNativeLeaked = Hosts[Index] && (Hosts[Index]->GetManagedScriptCount() != 0 || Hosts[Index]->GetGameplayLifecycleClaimCount() != 0);
            const bool bCoreClrLeaked = WorldHostIds[Index] != 0 && FUnifiedScriptCoreClrHost::Get().GetManagedScriptCount(WorldHostIds[Index]) != 0;
            if ((bNativeLeaked || bCoreClrLeaked) && Error.IsEmpty()) Error = FString::Printf(TEXT("Cooked active World %d leaked managed state before destruction."), Index);
            if (Contexts[Index] && Worlds[Index] && GEngine) GEngine->DestroyWorldContext(Worlds[Index]);
            Contexts[Index] = nullptr;
            if (Worlds[Index]) { Worlds[Index]->RemoveFromRoot(); Worlds[Index]->DestroyWorld(false); Worlds[Index] = nullptr; }
        }
    };
    if (!Worlds[0] || !Worlds[1]) { Cleanup(); Private::Finish(false, TEXT("Could not create both cooked active Worlds.")); return; }
    for (int32 Index = 0; Index < 2; ++Index)
    {
        Worlds[Index]->AddToRoot();
        Contexts[Index] = &GEngine->CreateNewWorldContext(EWorldType::Game);
        Contexts[Index]->SetCurrentWorld(Worlds[Index]);
        Worlds[Index]->InitializeActorsForPlay(FURL());
        Hosts[Index] = Worlds[Index]->GetSubsystem<UUnifiedScriptWorldSubsystem>();
        Actors[Index] = Worlds[Index]->SpawnActor<AUnifiedScriptActor>(Branches[Index].Grandchild);
        Components[Index] = Actors[Index] ? Actors[Index]->ScriptComponent.Get() : nullptr;
    }
    if (!Hosts[0] || !Hosts[1] || !Actors[0] || !Actors[1] || !Components[0] || !Components[1])
    {
        Cleanup(); Private::Finish(false, TEXT("Could not spawn both cooked active grandchild actors.")); return;
    }
    for (int32 Index = 0; Index < 2; ++Index)
    {
        AUnifiedScriptActor* GrandchildCdo = Cast<AUnifiedScriptActor>(Branches[Index].Grandchild->GetDefaultObject());
        UUnifiedScriptComponent* GrandchildComponent = GrandchildCdo ? GrandchildCdo->GetUnifiedScriptComponent() : nullptr;
        if (!GrandchildCdo || !GrandchildComponent || !Private::CheckInstance(*Actors[Index], *Branches[Index].Asset, GrandchildComponent, Error))
        {
            Cleanup(); Private::Finish(false, Error.IsEmpty() ? TEXT("Cooked active instance component contract failed.") : Error); return;
        }
    }
    Actors[0]->DispatchBeginPlay(); Actors[1]->DispatchBeginPlay();
    FUnifiedScriptRuntimeDebugContext Before[2];
    const FString GenerationBefore[2] = { Components[0]->ActiveGeneration, Components[1]->ActiveGeneration };
    FIntProperty* StartProperties[2] = { FindFProperty<FIntProperty>(Actors[0]->GetClass(), TEXT("StartValue")), FindFProperty<FIntProperty>(Actors[1]->GetClass(), TEXT("StartValue")) };
    FIntProperty* CountProperties[2] = { FindFProperty<FIntProperty>(Actors[0]->GetClass(), TEXT("OverrideCount")), FindFProperty<FIntProperty>(Actors[1]->GetClass(), TEXT("OverrideCount")) };
    const bool bHaveInitialDebugContexts = Components[0]->GetRuntimeDebugContext(Before[0]) && Components[1]->GetRuntimeDebugContext(Before[1]);
    if (bHaveInitialDebugContexts)
    {
        WorldHostIds[0] = Before[0].WorldHostId;
        WorldHostIds[1] = Before[1].WorldHostId;
    }
    if (!StartProperties[0] || !StartProperties[1] || !CountProperties[0] || !CountProperties[1] || !Components[0]->bScriptRunning || !Components[1]->bScriptRunning || Hosts[0]->GetManagedScriptCount() != 1 || Hosts[1]->GetManagedScriptCount() != 1 || Hosts[0]->GetGameplayLifecycleClaimCount() != 1 || Hosts[1]->GetGameplayLifecycleClaimCount() != 1 || FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount() != InitialWorldCount + 2 || !bHaveInitialDebugContexts || WorldHostIds[0] == 0 || WorldHostIds[1] == 0 || Before[0].InstanceId == 0 || Before[1].InstanceId == 0 || Before[0].WorldHostId == Before[1].WorldHostId || Before[0].InstanceId == Before[1].InstanceId || StartProperties[0]->GetPropertyValue_InContainer(Actors[0]) != 102 || StartProperties[1]->GetPropertyValue_InContainer(Actors[1]) != 102 || CountProperties[0]->GetPropertyValue_InContainer(Actors[0]) != 0 || CountProperties[1]->GetPropertyValue_InContainer(Actors[1]) != 0)
    {
        Cleanup(); Private::Finish(false, TEXT("Cooked active initial state/lifecycle contract failed.")); return;
    }
    for (int32 Index = 0; Index < 2; ++Index)
    {
        const FString ActualProgram = Private::Normalize(Components[Index]->GetActiveProgramPath());
        if (!ActualProgram.Equals(ExpectedProgram, ESearchCase::IgnoreCase)) { Cleanup(); Private::Finish(false, TEXT("Cooked active component executed a non-archive program.")); return; }
    }
    const FString HostFxrPath = Private::Normalize(FUnifiedScriptCoreClrHost::Get().GetHostFxrPath());
    if (!Private::Under(PackagedRoot, HostFxrPath) || !HostFxrPath.Contains(TEXT("/Plugins/UEUnifiedScript/Binaries/ThirdParty/DotNet/10.0/"), ESearchCase::IgnoreCase)) { Cleanup(); Private::Finish(false, FString::Printf(TEXT("Cooked active hostfxr is outside the archive: %s"), *HostFxrPath)); return; }
    StartProperties[0]->SetPropertyValue_InContainer(Actors[0], 20); StartProperties[1]->SetPropertyValue_InContainer(Actors[1], 40);
    for (int32 Index = 0; Index < 2; ++Index) { Hosts[Index]->BeginManagedFrame(); Components[Index]->TickComponent(0.1f, LEVELTICK_All, nullptr); Hosts[Index]->EndManagedFrame(); }
    if (StartProperties[0]->GetPropertyValue_InContainer(Actors[0]) != 30 || StartProperties[1]->GetPropertyValue_InContainer(Actors[1]) != 50 || !Private::CallAddOne(*Actors[0], 12, 113, Error) || !Private::CallAddOne(*Actors[1], 12, 113, Error) || !Private::CallReceiveTick(*Actors[0], 100, Error) || !Private::CallReceiveTick(*Actors[1], 100, Error))
    { Cleanup(); Private::Finish(false, Error.IsEmpty() ? TEXT("Cooked active state/tick/event verification failed.") : Error); return; }
    if (!Components[0]->ReloadScript() || Components[0]->ActiveGeneration == GenerationBefore[0] || StartProperties[0]->GetPropertyValue_InContainer(Actors[0]) != 131 || CountProperties[0]->GetPropertyValue_InContainer(Actors[0]) != 100 || StartProperties[1]->GetPropertyValue_InContainer(Actors[1]) != 50 || CountProperties[1]->GetPropertyValue_InContainer(Actors[1]) != 100 || Components[1]->ActiveGeneration != GenerationBefore[1])
    { Cleanup(); Private::Finish(false, TEXT("Cooked active first-World reload was not isolated or did not preserve state.")); return; }
    FUnifiedScriptRuntimeDebugContext AfterFirstReload;
    if (!Components[0]->GetRuntimeDebugContext(AfterFirstReload) || AfterFirstReload.Generation != Before[0].Generation + 1 || AfterFirstReload.WorldHostId != Before[0].WorldHostId || AfterFirstReload.InstanceId != Before[0].InstanceId || AfterFirstReload.HostHandle != Before[0].HostHandle || AfterFirstReload.TargetHandle != Before[0].TargetHandle || !Private::CallReceiveTick(*Actors[0], 200, Error))
    { Cleanup(); Private::Finish(false, Error.IsEmpty() ? TEXT("Cooked active first-World identity/event reload failed.") : Error); return; }
    Hosts[0]->BeginManagedFrame(); Components[0]->TickComponent(0.1f, LEVELTICK_All, nullptr); Hosts[0]->EndManagedFrame();
    if (StartProperties[0]->GetPropertyValue_InContainer(Actors[0]) != 141 || !Components[1]->ReloadScript() || Components[1]->ActiveGeneration == GenerationBefore[1] || StartProperties[1]->GetPropertyValue_InContainer(Actors[1]) != 151 || CountProperties[1]->GetPropertyValue_InContainer(Actors[1]) != 100)
    { Cleanup(); Private::Finish(false, TEXT("Cooked active second-World reload was not isolated or did not preserve state.")); return; }
    FUnifiedScriptRuntimeDebugContext AfterSecondReload;
    FUnifiedScriptRuntimeDebugContext FirstAfterSecondReload;
    if (!Components[1]->GetRuntimeDebugContext(AfterSecondReload) || AfterSecondReload.Generation != Before[1].Generation + 1 || AfterSecondReload.WorldHostId != Before[1].WorldHostId || AfterSecondReload.InstanceId != Before[1].InstanceId || AfterSecondReload.HostHandle != Before[1].HostHandle || AfterSecondReload.TargetHandle != Before[1].TargetHandle || !Components[0]->GetRuntimeDebugContext(FirstAfterSecondReload) || FirstAfterSecondReload.Generation != AfterFirstReload.Generation || FirstAfterSecondReload.WorldHostId != AfterFirstReload.WorldHostId || FirstAfterSecondReload.InstanceId != AfterFirstReload.InstanceId || FirstAfterSecondReload.HostHandle != AfterFirstReload.HostHandle || FirstAfterSecondReload.TargetHandle != AfterFirstReload.TargetHandle || !Private::CallReceiveTick(*Actors[1], 200, Error))
    { Cleanup(); Private::Finish(false, Error.IsEmpty() ? TEXT("Cooked active second-World identity/event reload failed.") : Error); return; }
    Hosts[1]->BeginManagedFrame(); Components[1]->TickComponent(0.1f, LEVELTICK_All, nullptr); Hosts[1]->EndManagedFrame();
    if (StartProperties[1]->GetPropertyValue_InContainer(Actors[1]) != 161 || StartProperties[0]->GetPropertyValue_InContainer(Actors[0]) != 141 || CountProperties[0]->GetPropertyValue_InContainer(Actors[0]) != 200)
    { Cleanup(); Private::Finish(false, TEXT("Cooked active peer state changed during second reload.")); return; }
    Private::DestroyActor(Actors[0]);
    if (Hosts[0]->GetManagedScriptCount() != 0 || Hosts[0]->GetGameplayLifecycleClaimCount() != 0 || FUnifiedScriptCoreClrHost::Get().GetManagedScriptCount(Before[0].WorldHostId) != 0 || Hosts[1]->GetManagedScriptCount() != 1 || Hosts[1]->GetGameplayLifecycleClaimCount() != 1)
    { Cleanup(); Private::Finish(false, TEXT("Cooked active first-World teardown did not isolate the peer.")); return; }
    CollectGarbage(RF_NoFlags);
    if (Contexts[0] && GEngine) GEngine->DestroyWorldContext(Worlds[0]);
    Contexts[0] = nullptr;
    Worlds[0]->RemoveFromRoot();
    Worlds[0]->DestroyWorld(false);
    Worlds[0] = nullptr;
    Hosts[0] = nullptr;
    Components[0] = nullptr;
    if (FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount() != InitialWorldCount + 1)
    { Cleanup(); Private::Finish(false, TEXT("Cooked active first World destruction did not leave the peer managed World.")); return; }
    if (!Private::CallAddOne(*Actors[1], 12, 113, Error)) { Cleanup(); Private::Finish(false, Error); return; }
    Private::DestroyActor(Actors[1]);
    if (Hosts[1]->GetManagedScriptCount() != 0 || Hosts[1]->GetGameplayLifecycleClaimCount() != 0 || FUnifiedScriptCoreClrHost::Get().GetManagedScriptCount(Before[1].WorldHostId) != 0)
    {
        Cleanup(); Private::Finish(false, TEXT("Cooked active second World did not release native and CoreCLR scripts before teardown.")); return;
    }
    Cleanup();
    if (!Error.IsEmpty() || FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount() != InitialWorldCount) { Private::Finish(false, Error.IsEmpty() ? TEXT("Cooked active final managed World count did not return to baseline.") : Error); return; }
    Private::Finish(true, FString::Printf(TEXT("Cooked active class operations passed; program=%s hostfxr=%s."), *ExpectedProgram, *HostFxrPath));
}
}

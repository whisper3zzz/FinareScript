#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptClassAssetOperations.h"
#include "UnifiedScriptClassFactory.h"
#include "UnifiedScriptBlueprintClassMaterializer.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformTime.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "PackageTools.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptProjectSetup.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/Package.h"

namespace UE::UnifiedScript::ActiveClassAssetOperations::Tests
{
FString Normalize(const FString& Value)
{
    FString Result = FPaths::ConvertRelativePathToFull(Value);
    FPaths::NormalizeFilename(Result);
    return Result;
}

bool ReadJson(const FString& Path, TSharedPtr<FJsonObject>& Out, FString& Error)
{
    FString Text;
    if (!FFileHelper::LoadFileToString(Text, *Path))
    {
        Error = FString::Printf(TEXT("Cannot read JSON '%s'."), *Path);
        return false;
    }
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Text);
    if (!FJsonSerializer::Deserialize(Reader, Out) || !Out.IsValid())
    {
        Error = FString::Printf(TEXT("Invalid JSON '%s': %s"), *Path, *Reader->GetErrorMessage());
        return false;
    }
    return true;
}

bool WriteJson(const FString& Path, const TSharedPtr<FJsonObject>& Root, FString& Error)
{
    FString Text;
    const TSharedRef<TJsonWriter<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>::Create(&Text);
    if (!FJsonSerializer::Serialize(Root.ToSharedRef(), Writer) || !FFileHelper::SaveStringToFile(Text + TEXT("\n"), *Path))
    {
        Error = FString::Printf(TEXT("Cannot write JSON '%s'."), *Path);
        return false;
    }
    return true;
}

bool ResolveDeployment(const TSharedPtr<FJsonObject>& Config, FString& OutPath, FString& Error)
{
    const TSharedPtr<FJsonObject>* Deployment = nullptr;
    FString Manifest;
    if (!Config.IsValid() || !Config->TryGetObjectField(TEXT("deployment"), Deployment) ||
        Deployment == nullptr || !(*Deployment)->TryGetStringField(TEXT("manifest"), Manifest))
    {
        Error = TEXT("Onboarding has no deployment manifest.");
        return false;
    }
    OutPath = Manifest;
    if (FPaths::IsRelative(OutPath))
    {
        OutPath = FPaths::Combine(FPaths::ProjectDir(), TEXT("Config"), OutPath);
    }
    OutPath = Normalize(OutPath);
    return true;
}

struct FWorldFixture
{
    UWorld* World = nullptr;
    FWorldContext* Context = nullptr;
    AUnifiedScriptActor* Actor = nullptr;
    UUnifiedScriptWorldSubsystem* Host = nullptr;
};

void DestroyActor(FWorldFixture& Fixture)
{
    if (Fixture.Actor != nullptr && !Fixture.Actor->IsActorBeingDestroyed())
    {
        if (Fixture.Actor->HasActorBegunPlay())
        {
            Fixture.Actor->RouteEndPlay(EEndPlayReason::Destroyed);
        }
        Fixture.Actor->Destroy();
    }
    Fixture.Actor = nullptr;
}

void DestroyWorld(FWorldFixture& Fixture)
{
    DestroyActor(Fixture);
    Fixture.Host = nullptr;
    if (Fixture.Context != nullptr && GEngine != nullptr)
    {
        GEngine->DestroyWorldContext(Fixture.World);
        Fixture.Context = nullptr;
    }
    if (Fixture.World != nullptr)
    {
        Fixture.World->RemoveFromRoot();
        Fixture.World->DestroyWorld(false);
        Fixture.World = nullptr;
    }
}

bool MakeWorld(FWorldFixture& Out, FString& Error)
{
    Out.World = UWorld::CreateWorld(EWorldType::Game, false);
    if (Out.World == nullptr || GEngine == nullptr)
    {
        Error = TEXT("Could not create active asset operation world.");
        return false;
    }
    Out.World->AddToRoot();
    Out.Context = &GEngine->CreateNewWorldContext(EWorldType::Game);
    Out.Context->SetCurrentWorld(Out.World);
    Out.World->InitializeActorsForPlay(FURL());
    Out.Host = Out.World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    if (Out.Host == nullptr)
    {
        Error = TEXT("Active asset operation world has no managed host subsystem.");
        return false;
    }
    return true;
}

UUnifiedScriptComponent* GetComponent(UBlueprint* Blueprint);

UUnifiedScriptAsset* GetScriptAsset(UBlueprint* Blueprint)
{
    if (const UUnifiedScriptBlueprint* ManagedBlueprint = Cast<UUnifiedScriptBlueprint>(Blueprint))
    {
        return ManagedBlueprint->ScriptAsset;
    }
    return GetComponent(Blueprint) != nullptr ? GetComponent(Blueprint)->ScriptAsset.Get() : nullptr;
}

UUnifiedScriptComponent* GetComponent(UBlueprint* Blueprint)
{
    if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
    {
        return nullptr;
    }
    const AUnifiedScriptActor* Defaults = Cast<AUnifiedScriptActor>(Blueprint->GeneratedClass->GetDefaultObject());
    return Defaults != nullptr ? Defaults->ScriptComponent : nullptr;
}

bool FindManagedSource(
    FAutomationTestBase& Test,
    const FString& ClassesDirectory,
    const FString& ManagedTypeName,
    FString& OutPath,
    FString& OutSource)
{
    TArray<FString> Candidates;
    IFileManager::Get().FindFilesRecursive(Candidates, *ClassesDirectory, TEXT("*.cs"), true, false);
    TArray<FString> Matches;
    const FString ClassMarker = TEXT("class ") + ManagedTypeName;
    for (const FString& Candidate : Candidates)
    {
        FString Source;
        if (FFileHelper::LoadFileToString(Source, *Candidate) && Source.Contains(ClassMarker))
        {
            Matches.Add(Candidate);
        }
    }
    if (!Test.TestEqual(
            FString::Printf(TEXT("Finds one source for managed type %s"), *ManagedTypeName),
            Matches.Num(),
            1))
    {
        return false;
    }
    OutPath = Matches[0];
    if (!Test.TestTrue(TEXT("Reads managed source for active fixture"), FFileHelper::LoadFileToString(OutSource, *OutPath)))
    {
        return false;
    }
    return true;
}

bool ReplaceSourceText(
    FAutomationTestBase& Test,
    const FString& Path,
    FString& InOutSource,
    const FString& From,
    const FString& To,
    const TCHAR* Label)
{
    if (!Test.TestTrue(
            FString::Printf(TEXT("Active fixture source contains %s"), Label),
            InOutSource.Contains(From)))
    {
        return false;
    }
    InOutSource = InOutSource.Replace(*From, *To, ESearchCase::CaseSensitive);
    if (!Test.TestTrue(
            FString::Printf(TEXT("Writes active fixture source %s"), Label),
            FFileHelper::SaveStringToFile(InOutSource, *Path)))
    {
        return false;
    }
    return true;
}

bool PatchInitialOnTick(
    FAutomationTestBase& Test,
    const FString& Path,
    FString& InOutSource)
{
    const FString EmptyLf =
        TEXT("    protected override void OnTick(float deltaSeconds)\n")
        TEXT("    {\n")
        TEXT("    }");
    const FString EmptyCrLf =
        TEXT("    protected override void OnTick(float deltaSeconds)\r\n")
        TEXT("    {\r\n")
        TEXT("    }");
    const FString BodyLf =
        TEXT("    protected override void OnTick(float deltaSeconds)\n")
        TEXT("    {\n")
        TEXT("        StartValue += 10;\n")
        TEXT("    }");
    const FString BodyCrLf =
        TEXT("    protected override void OnTick(float deltaSeconds)\r\n")
        TEXT("    {\r\n")
        TEXT("        StartValue += 10;\r\n")
        TEXT("    }");
    if (InOutSource.Contains(EmptyLf))
    {
        return ReplaceSourceText(Test, Path, InOutSource, EmptyLf, BodyLf, TEXT("OnTick"));
    }
    return ReplaceSourceText(Test, Path, InOutSource, EmptyCrLf, BodyCrLf, TEXT("OnTick"));
}

bool PatchAddOneBody(
    FAutomationTestBase& Test,
    const FString& Path,
    FString& InOutSource)
{
    return ReplaceSourceText(
        Test,
        Path,
        InOutSource,
        TEXT("return value + 1;"),
        TEXT("return value + 101;"),
        TEXT("renamed AddOne body"));
}

bool PatchExplicitClassId(
    FAutomationTestBase& Test,
    const FString& Path,
    FString& InOutSource,
    const FGuid& CurrentClassId,
    const FString& ReplacementClassId)
{
    const FString CurrentText = TEXT("us_") + CurrentClassId.ToString(EGuidFormats::Digits).ToLower();
    return ReplaceSourceText(
        Test,
        Path,
        InOutSource,
        CurrentText,
        ReplacementClassId,
        TEXT("explicit ClassId"));
}

bool SetStartValueAndTick(
    FAutomationTestBase& Test,
    FWorldFixture& Fixture,
    int32 Input,
    int32 Expected,
    const TCHAR* Label)
{
    if (!Test.TestNotNull(*FString::Printf(TEXT("%s Actor"), Label), Fixture.Actor) ||
        !Test.TestNotNull(*FString::Printf(TEXT("%s ScriptComponent"), Label), Fixture.Actor->ScriptComponent.Get()) ||
        !Test.TestNotNull(*FString::Printf(TEXT("%s managed host"), Label), Fixture.Host))
    {
        return false;
    }
    FIntProperty* StartValue = FindFProperty<FIntProperty>(Fixture.Actor->GetClass(), TEXT("StartValue"));
    if (!Test.TestNotNull(*FString::Printf(TEXT("%s StartValue property"), Label), StartValue))
    {
        return false;
    }
    StartValue->SetPropertyValue_InContainer(Fixture.Actor, Input);
    Fixture.Host->BeginManagedFrame();
    Fixture.Actor->ScriptComponent->TickComponent(0.1f, LEVELTICK_All, nullptr);
    Fixture.Host->EndManagedFrame();
    return Test.TestEqual(
        FString::Printf(TEXT("%s Tick applies managed +10 body"), Label),
        StartValue->GetPropertyValue_InContainer(Fixture.Actor),
        Expected);
}

bool TickExistingStartValue(
    FAutomationTestBase& Test,
    FWorldFixture& Fixture,
    int32 Expected,
    const TCHAR* Label)
{
    if (!Test.TestNotNull(*FString::Printf(TEXT("%s Actor"), Label), Fixture.Actor) ||
        !Test.TestNotNull(*FString::Printf(TEXT("%s ScriptComponent"), Label), Fixture.Actor->ScriptComponent.Get()) ||
        !Test.TestNotNull(*FString::Printf(TEXT("%s managed host"), Label), Fixture.Host))
    {
        return false;
    }
    FIntProperty* StartValue = FindFProperty<FIntProperty>(Fixture.Actor->GetClass(), TEXT("StartValue"));
    if (!Test.TestNotNull(*FString::Printf(TEXT("%s StartValue property"), Label), StartValue))
    {
        return false;
    }
    Fixture.Host->BeginManagedFrame();
    Fixture.Actor->ScriptComponent->TickComponent(0.1f, LEVELTICK_All, nullptr);
    Fixture.Host->EndManagedFrame();
    return Test.TestEqual(
        FString::Printf(TEXT("%s Tick applies managed +10 body without resetting state"), Label),
        StartValue->GetPropertyValue_InContainer(Fixture.Actor),
        Expected);
}

bool ReadStartValue(
    FAutomationTestBase& Test,
    AUnifiedScriptActor* Actor,
    int32& OutValue,
    const TCHAR* Label)
{
    if (!Test.TestNotNull(*FString::Printf(TEXT("%s Actor"), Label), Actor))
    {
        return false;
    }
    FIntProperty* StartValue = FindFProperty<FIntProperty>(Actor->GetClass(), TEXT("StartValue"));
    if (!Test.TestNotNull(*FString::Printf(TEXT("%s StartValue property"), Label), StartValue))
    {
        return false;
    }
    OutValue = StartValue->GetPropertyValue_InContainer(Actor);
    return true;
}

bool SynchronizeAssetFromConfiguredDeclaration(
    FAutomationTestBase& Test,
    UUnifiedScriptAsset* ScriptAsset,
    UBlueprint* Blueprint,
    const TCHAR* Label)
{
    if (!Test.TestNotNull(*FString::Printf(TEXT("%s ScriptAsset"), Label), ScriptAsset) ||
        !Test.TestNotNull(*FString::Printf(TEXT("%s Blueprint"), Label), Blueprint))
    {
        return false;
    }
    FString DeclarationPath;
    FString DeclarationAssetPath;
    ScriptAsset->ResolveDeclarationConfiguration(DeclarationPath, DeclarationAssetPath);
    FString Declaration;
    if (!Test.TestTrue(
            *FString::Printf(TEXT("Reads %s declaration"), Label),
            FFileHelper::LoadFileToString(Declaration, *DeclarationPath)))
    {
        return false;
    }
    FUnifiedScriptBlueprintClassSyncResult SyncResult;
    FString SyncError;
    if (!FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            Declaration,
            false,
            SyncResult,
            SyncError))
    {
        Test.AddError(FString::Printf(TEXT("%s declaration synchronization failed: %s"), Label, *SyncError));
        return false;
    }
    return true;
}

UBlueprint* CreateChild(FAutomationTestBase& Test, UClass* Parent, const FString& PackageName, const FString& Name)
{
    UPackage* Package = CreatePackage(*PackageName);
    UBlueprint* Blueprint = Package != nullptr
        ? FKismetEditorUtilities::CreateBlueprint(
            Parent, Package, FName(*Name), BPTYPE_Normal, UBlueprint::StaticClass(),
            UBlueprintGeneratedClass::StaticClass(), FName(TEXT("UnifiedScriptActiveClassAssetOperations")))
        : nullptr;
    if (!Test.TestNotNull(*FString::Printf(TEXT("Creates active ordinary Blueprint %s"), *Name), Blueprint))
    {
        return nullptr;
    }
    FKismetEditorUtilities::CompileBlueprint(Blueprint);
    Test.TestEqual(*FString::Printf(TEXT("Compiles active ordinary Blueprint %s"), *Name), Blueprint->Status, BS_UpToDate);
    return Blueprint;
}

bool InvokeAddOne(FAutomationTestBase& Test, AUnifiedScriptActor* Actor, int32 Input, int32 Expected, const TCHAR* Label)
{
    if (!Test.TestNotNull(*FString::Printf(TEXT("%s actor"), Label), Actor))
    {
        return false;
    }
    UFunction* Function = nullptr;
    for (UClass* Candidate = Actor->GetClass(); Candidate != nullptr && Function == nullptr;
         Candidate = Candidate->GetSuperClass())
    {
        const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
            Cast<UUnifiedScriptBlueprintGeneratedClass>(Candidate);
        if (GeneratedClass == nullptr)
        {
            continue;
        }
        for (const TPair<FName, FUnifiedScriptManagedFunctionBinding>& Pair :
             GeneratedClass->ManagedFunctionBindings)
        {
            if (Pair.Value.ManagedName == TEXT("AddOne"))
            {
                Function = Actor->FindFunction(Pair.Key);
                break;
            }
        }
    }
    if (!Test.TestNotNull(*FString::Printf(TEXT("%s managed AddOne function"), Label), Function))
    {
        return false;
    }
    struct FParams { int32 Value = 0; int32 ReturnValue = 0; } Params;
    Params.Value = Input;
    Actor->ProcessEvent(Function, &Params);
    return Test.TestEqual(*FString::Printf(TEXT("%s managed AddOne result"), Label), Params.ReturnValue, Expected);
}

bool RunBuilder(FAutomationTestBase& Test, const FString& ProjectPath, const FString& OutputRoot, const FString& BuildLogPath)
{
    const FString Script = Normalize(FPaths::Combine(FPaths::ProjectDir(), TEXT("../tools/build-active-class-asset-operations-fixture.ps1")));
    const FString PowerShell = TEXT("C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe");
    const FString Args = FString::Printf(
        TEXT("-NoProfile -ExecutionPolicy Bypass -File \"%s\" -ProjectPath \"%s\" -OutputRoot \"%s\" -EngineRoot \"%s\" -LogPath \"%s\""),
        *Script, *ProjectPath, *OutputRoot,
        *Normalize(FPaths::Combine(FPaths::EngineDir(), TEXT(".."))), *BuildLogPath);
    int32 ReturnCode = -1;
    FString StdOut;
    FString StdErr;
    void* ReadPipe = nullptr;
    void* WritePipe = nullptr;
    if (!FPlatformProcess::CreatePipe(ReadPipe, WritePipe))
    {
        Test.AddError(TEXT("Could not create the active fixture builder output pipe."));
        return false;
    }

    uint32 ProcessId = 0;
    FProcHandle Process = FPlatformProcess::CreateProc(
        *PowerShell,
        *Args,
        false,
        true,
        true,
        &ProcessId,
        0,
        *FPaths::GetPath(ProjectPath),
        WritePipe,
        nullptr,
        WritePipe);
    FPlatformProcess::ClosePipe(nullptr, WritePipe);
    WritePipe = nullptr;
    if (!Process.IsValid())
    {
        FPlatformProcess::ClosePipe(ReadPipe, nullptr);
        Test.AddError(TEXT("Could not start the active fixture builder."));
        return false;
    }

    const double StartedAt = FPlatformTime::Seconds();
    constexpr double TimeoutSeconds = 600.0;
    bool bTimedOut = false;
    while (FPlatformProcess::IsProcRunning(Process))
    {
        const FString PendingOutput = FPlatformProcess::ReadPipe(ReadPipe);
        if (!PendingOutput.IsEmpty())
        {
            StdOut += PendingOutput;
        }
        if (FPlatformTime::Seconds() - StartedAt > TimeoutSeconds)
        {
            bTimedOut = true;
            FPlatformProcess::TerminateProc(Process, true);
            break;
        }
        FPlatformProcess::Sleep(0.25f);
    }
    if (bTimedOut)
    {
        FPlatformProcess::WaitForProc(Process);
        ReturnCode = -1;
    }
    else if (!FPlatformProcess::GetProcReturnCode(Process, &ReturnCode))
    {
        ReturnCode = -1;
    }
    StdOut = FPlatformProcess::ReadPipe(ReadPipe);
    FPlatformProcess::ClosePipe(ReadPipe, nullptr);
    ReadPipe = nullptr;
    FPlatformProcess::CloseProc(Process);
    if (bTimedOut)
    {
        StdErr = TEXT("Active fixture builder exceeded the 600 second timeout.");
    }
    FFileHelper::SaveStringToFile(StdOut + TEXT("\n") + StdErr, *BuildLogPath);
    if (ReturnCode != 0)
    {
        Test.AddError(FString::Printf(TEXT("Active fixture builder failed (%d): %s"), ReturnCode, *StdErr));
        return false;
    }
    return Test.TestTrue(TEXT("Builds and emits the active managed fixture"), true);
}

bool LoadIdentityEntryIds(
    FAutomationTestBase& Test,
    const FString& SidecarPath,
    TSet<FString>& OutStateIds,
    TSet<FString>& OutFunctionIds,
    const TCHAR* Label)
{
    FString Json;
    if (!Test.TestTrue(
            *FString::Printf(TEXT("%s identity sidecar exists"), Label),
            FFileHelper::LoadFileToString(Json, *SidecarPath)))
    {
        return false;
    }
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!Test.TestTrue(
            *FString::Printf(TEXT("%s identity sidecar parses"), Label),
            FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid()))
    {
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* Entries = nullptr;
    if (!Test.TestTrue(
            *FString::Printf(TEXT("%s identity sidecar has entries"), Label),
            Root->TryGetArrayField(TEXT("entries"), Entries) && Entries != nullptr))
    {
        return false;
    }
    OutStateIds.Reset();
    OutFunctionIds.Reset();
    for (const TSharedPtr<FJsonValue>& EntryValue : *Entries)
    {
        const TSharedPtr<FJsonObject> Entry = EntryValue.IsValid()
            ? EntryValue->AsObject()
            : nullptr;
        FString Id;
        FString Kind;
        if (!Entry.IsValid() ||
            !Entry->TryGetStringField(TEXT("id"), Id) ||
            !Entry->TryGetStringField(TEXT("kind"), Kind))
        {
            Test.AddError(FString::Printf(TEXT("%s identity sidecar contains an invalid entry."), Label));
            return false;
        }
        if (Kind == TEXT("state"))
        {
            OutStateIds.Add(Id);
        }
        else if (Kind == TEXT("function"))
        {
            OutFunctionIds.Add(Id);
        }
    }
    return Test.TestTrue(
        *FString::Printf(TEXT("%s identity sidecar has state/function IDs"), Label),
        OutStateIds.Num() > 0 && OutFunctionIds.Num() > 0);
}

bool IdentityIdSetsEqual(const TSet<FString>& Left, const TSet<FString>& Right)
{
    if (Left.Num() != Right.Num())
    {
        return false;
    }
    for (const FString& Id : Left)
    {
        if (!Right.Contains(Id))
        {
            return false;
        }
    }
    return true;
}

bool ConfigureFixtureProject(
    const FString& OutputRoot,
    const FString& OutputRootRelative,
    const FString& FixtureProject,
    FString& OutOnboardingPath,
    FString& OutError)
{
    const FString OnboardingPath = Normalize(FPaths::Combine(FPaths::ProjectDir(), TEXT("Config/UnifiedScript.onboarding.json")));
    TSharedPtr<FJsonObject> Config;
    if (!ReadJson(OnboardingPath, Config, OutError))
    {
        return false;
    }
    FString OriginalDeploymentPath;
    if (!ResolveDeployment(Config, OriginalDeploymentPath, OutError))
    {
        return false;
    }
    FString DeploymentText;
    if (!FFileHelper::LoadFileToString(DeploymentText, *OriginalDeploymentPath))
    {
        OutError = TEXT("Could not snapshot deployment manifest.");
        return false;
    }
    TSharedPtr<FJsonObject> Deployment;
    const TSharedRef<TJsonReader<>> DeploymentReader = TJsonReaderFactory<>::Create(DeploymentText);
    if (!FJsonSerializer::Deserialize(DeploymentReader, Deployment) || !Deployment.IsValid())
    {
        OutError = TEXT("Deployment manifest is invalid JSON.");
        return false;
    }
    const FString DeclarationPath = Normalize(FPaths::Combine(OutputRoot, TEXT("declaration/ActiveClassAssetOperationsFixture.asset.json")));
    const FString AssemblyPath = Normalize(FPaths::Combine(OutputRoot, TEXT("publish/ActiveClassAssetOperationsFixture.dll")));
    const TArray<TSharedPtr<FJsonValue>>* Scripts = nullptr;
    if (!Deployment->TryGetArrayField(TEXT("scripts"), Scripts) || Scripts == nullptr || Scripts->IsEmpty())
    {
        OutError = TEXT("Deployment manifest has no baseline script entry.");
        return false;
    }
    const TSharedPtr<FJsonObject> Baseline = (*Scripts)[0]->AsObject();
    Baseline->SetStringField(TEXT("assemblyPath"), AssemblyPath);
    Baseline->SetStringField(TEXT("declarationPath"), DeclarationPath);
    const FString FixtureDeployment = Normalize(FPaths::Combine(OutputRoot, TEXT("ActiveClassAssetOperationsFixture.deployment.json")));
    if (!WriteJson(FixtureDeployment, Deployment, OutError))
    {
        return false;
    }
    const FString FixtureProjectAbsolute = Normalize(FixtureProject);
    const TSharedPtr<FJsonObject>* Managed = nullptr;
    if (!Config->TryGetObjectField(TEXT("managed"), Managed) || Managed == nullptr)
    {
        OutError = TEXT("Onboarding has no managed section.");
        return false;
    }
    (*Managed)->SetArrayField(TEXT("projects"), {
        MakeShared<FJsonValueString>(FixtureProjectAbsolute)});
    TSharedPtr<FJsonObject> Chunk = MakeShared<FJsonObject>();
    Chunk->SetStringField(TEXT("project"), FixtureProjectAbsolute);
    Chunk->SetStringField(TEXT("stagePath"), Normalize(FPaths::Combine(OutputRoot, TEXT("declaration/ActiveClassAssetOperationsFixture.uschunk"))));
    (*Managed)->SetArrayField(TEXT("chunks"), {MakeShared<FJsonValueObject>(Chunk)});
    const TSharedPtr<FJsonObject>* DeploymentSection = nullptr;
    if (!Config->TryGetObjectField(TEXT("deployment"), DeploymentSection) || DeploymentSection == nullptr)
    {
        OutError = TEXT("Onboarding has no deployment section.");
        return false;
    }
    (*DeploymentSection)->SetStringField(TEXT("manifest"), FixtureDeployment);
    OutOnboardingPath = OnboardingPath;
    return WriteJson(OnboardingPath, Config, OutError);
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptActiveClassAssetOperationsTest,
    "UnifiedScript.Unreal.ScriptClass.ActiveClassAssetOperations",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptActiveClassAssetOperationsTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::ActiveClassAssetOperations::Tests;
    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits).ToUpper();
    const FString OutputRootRelative = TEXT("artifacts/m13-3c-3-active-asset-operations/") + Suffix;
    const FString OutputRoot = Normalize(FPaths::Combine(FPaths::ProjectDir(), TEXT("../"), OutputRootRelative));
    const FString FixtureProject = FPaths::Combine(OutputRoot, TEXT("ActiveClassAssetOperationsFixture.csproj"));
    const FString ClassesDirectory = FPaths::Combine(OutputRoot, TEXT("Classes"));
    const FString BuilderLog = FPaths::Combine(OutputRoot, TEXT("fixture-build.log"));
    IFileManager::Get().MakeDirectory(*ClassesDirectory, true);

    const FString RepoRoot = Normalize(FPaths::Combine(FPaths::ProjectDir(), TEXT("../")));
    const FString Bindings = Normalize(FPaths::Combine(RepoRoot, TEXT("samples/Unreal/UEUnifiedScriptRuntime.bindings.cs")));
    const FString ProjectTemplate = FPaths::Combine(
        RepoRoot,
        TEXT("tests/UnifiedScript.Tests/Fixtures/ActiveClassAssetOperations/ActiveClassAssetOperationsFixture.csproj"));
    FString ProjectXml;
    FString BindingsText;
    const FString GeneratedDirectory = FPaths::Combine(OutputRoot, TEXT("Generated"));
    const FString GeneratedBindings = FPaths::Combine(GeneratedDirectory, TEXT("UEUnifiedScriptRuntime.bindings.cs"));
    IFileManager::Get().MakeDirectory(*GeneratedDirectory, true);
    if (!FFileHelper::LoadFileToString(ProjectXml, *ProjectTemplate) ||
        !FFileHelper::LoadFileToString(BindingsText, *Bindings) ||
        !FFileHelper::SaveStringToFile(BindingsText, *GeneratedBindings) ||
        !FFileHelper::SaveStringToFile(ProjectXml, *FixtureProject))
    {
        AddError(TEXT("Could not write the isolated active fixture project."));
        return false;
    }

    const FString OnboardingPath = Normalize(FPaths::Combine(FPaths::ProjectDir(), TEXT("Config/UnifiedScript.onboarding.json")));
    TArray<uint8> OriginalOnboarding;
    if (!FFileHelper::LoadFileToArray(OriginalOnboarding, *OnboardingPath))
    {
        AddError(TEXT("Could not snapshot onboarding configuration."));
        return false;
    }
    const FString FirstPlayPath = FUnifiedScriptProjectSetup::GetFirstPlayRequestPath(FPaths::ProjectDir());
    const bool bHadFirstPlay = IFileManager::Get().FileExists(*FirstPlayPath);
    TArray<uint8> OriginalFirstPlay;
    if (bHadFirstPlay && !FFileHelper::LoadFileToArray(OriginalFirstPlay, *FirstPlayPath))
    {
        AddError(TEXT("Could not snapshot the existing first-play request; refusing to modify onboarding."));
        return false;
    }
    bool bRestoreOnboarding = false;
    ON_SCOPE_EXIT
    {
        if (bRestoreOnboarding)
        {
            if (!FFileHelper::SaveArrayToFile(OriginalOnboarding, *OnboardingPath))
            {
                AddError(TEXT("Failed to restore onboarding configuration after active fixture."));
            }
            if (bHadFirstPlay)
            {
                if (!FFileHelper::SaveArrayToFile(OriginalFirstPlay, *FirstPlayPath))
                {
                    AddError(TEXT("Failed to restore the existing first-play request after active fixture."));
                }
            }
            else if (IFileManager::Get().FileExists(*FirstPlayPath))
            {
                if (!IFileManager::Get().Delete(*FirstPlayPath, false, true))
                {
                    AddError(TEXT("Failed to remove the active fixture first-play request."));
                }
            }
        }
    };
    // Arm recovery before any configuration write, so a partial onboarding
    // write is restored even when ConfigureFixtureProject reports an error.
    bRestoreOnboarding = true;
    FString ConfiguredOnboardingPath;
    FString SetupError;
    if (!ConfigureFixtureProject(OutputRoot, OutputRootRelative, FixtureProject, ConfiguredOnboardingPath, SetupError))
    {
        AddError(SetupError);
        return false;
    }

    const FString Root = TEXT("/Game/M13C3ActiveClassOps_") + Suffix;
    const FString BaseName = TEXT("ActiveBase_") + Suffix;
    const FString ChildName = TEXT("ActiveChild_") + Suffix;
    const FString GrandchildName = TEXT("ActiveGrandchild_") + Suffix;
    const FString RenamedName = BaseName + TEXT("_Renamed");
    const FString DuplicateName = BaseName + TEXT("_Duplicate");
    const FString FailedRenameName = BaseName + TEXT("_Failed");
    TArray<FString> PackageNames = {
        Root / BaseName, Root / ChildName, Root / GrandchildName,
        Root / (TEXT("SA_") + BaseName), Root / FailedRenameName, Root / RenamedName,
        Root / (TEXT("SA_") + RenamedName), Root / DuplicateName,
        Root / (TEXT("SA_") + DuplicateName),
        Root / (DuplicateName + TEXT("_Child")), Root / (DuplicateName + TEXT("_Grandchild"))};
    ON_SCOPE_EXIT
    {
        TArray<UPackage*> Packages;
        for (const FString& Name : PackageNames)
        {
            if (UPackage* Package = FindPackage(nullptr, *Name))
            {
                Package->SetDirtyFlag(false);
                Packages.AddUnique(Package);
            }
        }
        if (Packages.Num() > 0)
        {
            UPackageTools::UnloadPackages(Packages);
        }
        CollectGarbage(RF_NoFlags);
        for (const FString& Name : PackageNames)
        {
            const FString Filename = FPackageName::LongPackageNameToFilename(Name, FPackageName::GetAssetPackageExtension());
            if (IFileManager::Get().FileExists(*Filename))
            {
                IFileManager::Get().Delete(*Filename, false, true);
            }
        }
    };

    UPackage* BasePackage = CreatePackage(*(Root / BaseName));
    UUnifiedScriptActorClassFactory* Factory = NewObject<UUnifiedScriptActorClassFactory>();
    UBlueprint* BaseBlueprint = BasePackage != nullptr && Factory != nullptr
        ? Cast<UBlueprint>(Factory->FactoryCreateNew(
            UBlueprint::StaticClass(), BasePackage, FName(*BaseName),
            RF_Public | RF_Standalone | RF_Transactional, nullptr, GWarn,
            TEXT("UnifiedScriptActiveClassAssetOperations")))
        : nullptr;
    UUnifiedScriptAsset* ScriptAsset = GetScriptAsset(BaseBlueprint);
    if (!TestNotNull(TEXT("Factory creates active managed base"), BaseBlueprint) ||
        !TestNotNull(TEXT("Factory creates active ScriptAsset"), ScriptAsset))
    {
        return false;
    }
    FString InitialSourcePath;
    FString InitialSource;
    if (!FindManagedSource(*this, ClassesDirectory, BaseName, InitialSourcePath, InitialSource) ||
        !PatchInitialOnTick(*this, InitialSourcePath, InitialSource))
    {
        return false;
    }
    if (!RunBuilder(*this, FixtureProject, OutputRootRelative, BuilderLog))
    {
        return false;
    }
    const FString InitialSidecarPath = InitialSourcePath + TEXT(".usids");
    TArray<uint8> InitialSourceBytes;
    TArray<uint8> InitialSidecarBytes;
    if (!TestTrue(TEXT("Snapshots the active source bytes"),
            FFileHelper::LoadFileToArray(InitialSourceBytes, *InitialSourcePath)) ||
        !TestTrue(TEXT("Snapshots the active source identity sidecar"),
            FFileHelper::LoadFileToArray(InitialSidecarBytes, *InitialSidecarPath)))
    {
        return false;
    }
    TSet<FString> InitialStateIds;
    TSet<FString> InitialFunctionIds;
    if (!LoadIdentityEntryIds(
            *this,
            InitialSidecarPath,
            InitialStateIds,
            InitialFunctionIds,
            TEXT("Initial active")))
    {
        return false;
    }
    FString DeclarationPath;
    FString DeclarationAssetPath;
    ScriptAsset->ResolveDeclarationConfiguration(DeclarationPath, DeclarationAssetPath);
    FString Declaration;
    if (!TestTrue(TEXT("Reads the emitted active declaration"), FFileHelper::LoadFileToString(Declaration, *DeclarationPath)))
    {
        return false;
    }
    FUnifiedScriptBlueprintClassSyncResult SyncResult;
    FString SyncError;
    if (!TestTrue(TEXT("Materializes the emitted active declaration into the factory Blueprint"),
            FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                *ScriptAsset, *BaseBlueprint, Declaration, false, SyncResult, SyncError)))
    {
        AddError(SyncError);
        return false;
    }
    AUnifiedScriptActor* MaterializedDefaults = Cast<AUnifiedScriptActor>(BaseBlueprint->GeneratedClass->GetDefaultObject());
    if (MaterializedDefaults == nullptr || MaterializedDefaults->ScriptComponent == nullptr)
    {
        AddError(TEXT("Materialized active factory Blueprint lost its native ScriptComponent."));
        return false;
    }
    TestTrue(TEXT("SynchronizeJson preserves the factory Blueprint ScriptAsset"),
        Cast<UUnifiedScriptBlueprint>(BaseBlueprint) != nullptr &&
        Cast<UUnifiedScriptBlueprint>(BaseBlueprint)->ScriptAsset == ScriptAsset);
    TestTrue(TEXT("SynchronizeJson preserves the factory CDO ScriptAsset"),
        MaterializedDefaults->ScriptComponent->ScriptAsset.Get() == ScriptAsset);
    const FGuid OriginalClassId = ScriptAsset->ScriptClassId;
    const FString OriginalTypeName = ScriptAsset->ScriptTypeName;
    const FString OriginalBlueprintObjectPath = BaseBlueprint->GetPathName();
    const FString OriginalScriptAssetObjectPath = ScriptAsset->GetPathName();
    const FSoftClassPath OriginalGeneratedClassPath = ScriptAsset->GeneratedClassPath;
    UBlueprint* ChildBlueprint = CreateChild(*this, BaseBlueprint->GeneratedClass, Root / ChildName, ChildName);
    UBlueprint* GrandchildBlueprint = ChildBlueprint != nullptr
        ? CreateChild(*this, ChildBlueprint->GeneratedClass, Root / GrandchildName, GrandchildName)
        : nullptr;
    if (!TestNotNull(TEXT("Creates active child/grandchild"), GrandchildBlueprint))
    {
        return false;
    }
    TestTrue(TEXT("Active child inherits ScriptAsset"), GetScriptAsset(ChildBlueprint) == ScriptAsset);
    TestTrue(TEXT("Active grandchild inherits ScriptAsset"), GetScriptAsset(GrandchildBlueprint) == ScriptAsset);
    TestTrue(TEXT("Active child has an independent component archetype"), GetComponent(ChildBlueprint) != GetComponent(BaseBlueprint));
    TestTrue(TEXT("Active grandchild follows child component archetype"), GetComponent(GrandchildBlueprint) != nullptr && GetComponent(GrandchildBlueprint)->GetArchetype() == GetComponent(ChildBlueprint));

    const int32 InitialManagedWorldCount = FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    FWorldFixture First;
    FWorldFixture Second;
    FString WorldError;
    if (!MakeWorld(First, WorldError) || !MakeWorld(Second, WorldError))
    {
        AddError(WorldError);
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    First.Actor = First.World->SpawnActor<AUnifiedScriptActor>(ChildBlueprint->GeneratedClass, FTransform::Identity);
    Second.Actor = Second.World->SpawnActor<AUnifiedScriptActor>(GrandchildBlueprint->GeneratedClass, FTransform::Identity);
    if (!TestNotNull(TEXT("Spawns active child actor"), First.Actor) || !TestNotNull(TEXT("Spawns active grandchild actor"), Second.Actor))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    First.Actor->DispatchBeginPlay();
    Second.Actor->DispatchBeginPlay();
    if (!TestNotNull(TEXT("Original child ScriptComponent exists"), First.Actor->ScriptComponent.Get()) ||
        !TestNotNull(TEXT("Original grandchild ScriptComponent exists"), Second.Actor->ScriptComponent.Get()))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    TestTrue(TEXT("Original child managed script starts"), First.Actor->ScriptComponent->bScriptRunning);
    TestTrue(TEXT("Original grandchild managed script starts"), Second.Actor->ScriptComponent->bScriptRunning);
    UUnifiedScriptComponent* FirstComponent = First.Actor->ScriptComponent.Get();
    UUnifiedScriptComponent* SecondComponent = Second.Actor->ScriptComponent.Get();
    TestEqual(TEXT("Original child owns one lifecycle claim"), First.Host->GetGameplayLifecycleClaimCount(), 1);
    TestEqual(TEXT("Original grandchild owns one lifecycle claim"), Second.Host->GetGameplayLifecycleClaimCount(), 1);
    FUnifiedScriptRuntimeDebugContext FirstInitialContext;
    FUnifiedScriptRuntimeDebugContext SecondInitialContext;
    TestTrue(TEXT("Original child exposes a managed runtime identity"), First.Actor->ScriptComponent->GetRuntimeDebugContext(FirstInitialContext));
    TestTrue(TEXT("Original grandchild exposes a managed runtime identity"), Second.Actor->ScriptComponent->GetRuntimeDebugContext(SecondInitialContext));
    TestEqual(TEXT("Two active Worlds are registered with CoreClr"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialManagedWorldCount + 2);
    TestTrue(TEXT("Original worlds use distinct managed instances"), FirstInitialContext.InstanceId != SecondInitialContext.InstanceId);
    TestTrue(TEXT("Original child component uses the configured managed source"), First.Actor->ScriptComponent->GetActiveProgramPath().Contains(TEXT("ActiveClassAssetOperationsFixture")));
    TestTrue(TEXT("Original grandchild component uses the configured managed source"), Second.Actor->ScriptComponent->GetActiveProgramPath().Contains(TEXT("ActiveClassAssetOperationsFixture")));
    TestTrue(TEXT("Original child applies the real managed OnTick body"), SetStartValueAndTick(*this, First, 20, 30, TEXT("Original child")));
    TestTrue(TEXT("Original grandchild applies the real managed OnTick body"), SetStartValueAndTick(*this, Second, 20, 30, TEXT("Original grandchild")));
    TestEqual(TEXT("Original child AddOne is callable"), InvokeAddOne(*this, First.Actor, 7, 8, TEXT("Original child")), true);
    TestEqual(TEXT("Original grandchild AddOne is callable"), InvokeAddOne(*this, Second.Actor, 8, 9, TEXT("Original grandchild")), true);

    const FString FailedRenameSourcePath = FPaths::Combine(
        ClassesDirectory,
        FailedRenameName + TEXT(".cs"));
    const FString FailedRenameSidecarPath = FailedRenameSourcePath + TEXT(".usids");
    const TArray<uint8> CollisionSidecarBytes = {
        static_cast<uint8>('{'), static_cast<uint8>('"'), static_cast<uint8>('x'),
        static_cast<uint8>('"'), static_cast<uint8>(':'), static_cast<uint8>('1'),
        static_cast<uint8>('}')};
    if (!TestTrue(TEXT("Creates the sidecar-only rename collision"),
            FFileHelper::SaveArrayToFile(CollisionSidecarBytes, *FailedRenameSidecarPath)))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    FUnifiedScriptClassAssetOperationResult SidecarCollisionResult;
    TestFalse(TEXT("Rejects a sidecar-only rename target before mutation"),
        FUnifiedScriptClassAssetOperations::Rename(
            ScriptAsset,
            Root / FailedRenameName,
            SidecarCollisionResult));
    TestTrue(TEXT("Sidecar-only collision keeps the original ClassId"),
        ScriptAsset->ScriptClassId == OriginalClassId);
    TestEqual(TEXT("Sidecar-only collision keeps the Blueprint identity"),
        BaseBlueprint->GetPathName(),
        OriginalBlueprintObjectPath);
    TestEqual(TEXT("Sidecar-only collision keeps the ScriptAsset identity"),
        ScriptAsset->GetPathName(),
        OriginalScriptAssetObjectPath);
    TestEqual(TEXT("Sidecar-only collision keeps the managed type"),
        ScriptAsset->ScriptTypeName,
        OriginalTypeName);
    TestTrue(TEXT("Sidecar-only collision keeps the generated class path"),
        ScriptAsset->GeneratedClassPath == OriginalGeneratedClassPath);
    TestTrue(TEXT("Sidecar-only collision creates no recovery receipt"),
        SidecarCollisionResult.RecoveryReceiptPath.IsEmpty());
    TArray<uint8> CurrentBytes;
    TestTrue(TEXT("Sidecar-only collision keeps source bytes"),
        FFileHelper::LoadFileToArray(CurrentBytes, *InitialSourcePath) &&
        CurrentBytes == InitialSourceBytes);
    TestTrue(TEXT("Sidecar-only collision keeps original identity bytes"),
        FFileHelper::LoadFileToArray(CurrentBytes, *InitialSidecarPath) &&
        CurrentBytes == InitialSidecarBytes);
    TestTrue(TEXT("Sidecar-only collision keeps target sidecar"),
        FFileHelper::LoadFileToArray(CurrentBytes, *FailedRenameSidecarPath) &&
        CurrentBytes == CollisionSidecarBytes);
    TestTrue(TEXT("Removes the collision fixture sidecar"),
        IFileManager::Get().Delete(*FailedRenameSidecarPath, false, true));

    const TArray<uint8> MalformedSidecarBytes = {
        static_cast<uint8>('{'), static_cast<uint8>('n'), static_cast<uint8>('o'),
        static_cast<uint8>('t'), static_cast<uint8>(':'), static_cast<uint8>('j'),
        static_cast<uint8>('s'), static_cast<uint8>('o'), static_cast<uint8>('n')};
    if (!TestTrue(TEXT("Writes the malformed original sidecar fixture"),
            FFileHelper::SaveArrayToFile(MalformedSidecarBytes, *InitialSidecarPath)))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    FUnifiedScriptClassAssetOperationResult MalformedSidecarResult;
    TestFalse(TEXT("Rejects a malformed original identity sidecar before mutation"),
        FUnifiedScriptClassAssetOperations::Rename(
            ScriptAsset,
            Root / FailedRenameName,
            MalformedSidecarResult));
    TestTrue(TEXT("Malformed sidecar rejection keeps the original ClassId"),
        ScriptAsset->ScriptClassId == OriginalClassId);
    TestEqual(TEXT("Malformed sidecar rejection keeps the Blueprint identity"),
        BaseBlueprint->GetPathName(),
        OriginalBlueprintObjectPath);
    TestEqual(TEXT("Malformed sidecar rejection keeps the ScriptAsset identity"),
        ScriptAsset->GetPathName(),
        OriginalScriptAssetObjectPath);
    TestEqual(TEXT("Malformed sidecar rejection keeps the managed type"),
        ScriptAsset->ScriptTypeName,
        OriginalTypeName);
    TestTrue(TEXT("Malformed sidecar rejection keeps the generated class path"),
        ScriptAsset->GeneratedClassPath == OriginalGeneratedClassPath);
    TestTrue(TEXT("Malformed sidecar rejection creates no recovery receipt"),
        MalformedSidecarResult.RecoveryReceiptPath.IsEmpty());
    TestTrue(TEXT("Malformed sidecar rejection keeps source bytes"),
        FFileHelper::LoadFileToArray(CurrentBytes, *InitialSourcePath) &&
        CurrentBytes == InitialSourceBytes);
    TestTrue(TEXT("Malformed sidecar rejection keeps malformed bytes"),
        FFileHelper::LoadFileToArray(CurrentBytes, *InitialSidecarPath) &&
        CurrentBytes == MalformedSidecarBytes);
    TestTrue(TEXT("Restores the original identity sidecar fixture"),
        FFileHelper::SaveArrayToFile(InitialSidecarBytes, *InitialSidecarPath));

    FUnifiedScriptClassAssetOperationResult FailedRename;
    const FString FailedRenamePackage = Root / FailedRenameName;
    TestFalse(TEXT("Injected rename publication failure rolls back"), FUnifiedScriptClassAssetOperations::Rename(
        ScriptAsset, FailedRenamePackage, FailedRename,
        EUnifiedScriptClassAssetOperationFailurePoint::AfterExternalPublication));
    TestTrue(TEXT("Injected rename failure reaches rollback"), FailedRename.bRolledBack);
    TestTrue(TEXT("Injected rename failure reports the publication injection"),
        FailedRename.Error.Contains(TEXT("Injected Script Class transaction failure")));
    TestTrue(TEXT("Injected rename restores original identity sidecar bytes"),
        FFileHelper::LoadFileToArray(CurrentBytes, *InitialSidecarPath) &&
        CurrentBytes == InitialSidecarBytes);
    TestFalse(TEXT("Injected rename removes the new identity sidecar"),
        IFileManager::Get().FileExists(*FailedRenameSidecarPath));
    TestTrue(TEXT("Rollback keeps original child managed OnTick body"), TickExistingStartValue(*this, First, 40, TEXT("After failed rename")));
    TestTrue(TEXT("Failed rename keeps old managed instance callable"), InvokeAddOne(*this, First.Actor, 9, 10, TEXT("After failed rename")));

    FUnifiedScriptClassAssetOperationResult RenameResult;
    const FString RenamedPackage = Root / RenamedName;
    if (!TestTrue(TEXT("Renames active Script Class"), FUnifiedScriptClassAssetOperations::Rename(ScriptAsset, RenamedPackage, RenameResult)) ||
        !TestNotNull(TEXT("Renamed Blueprint exists"), RenameResult.Blueprint) ||
        !TestNotNull(TEXT("Renamed ScriptAsset exists"), RenameResult.ScriptAsset))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    TestTrue(TEXT("Rename preserves ClassId"), RenameResult.ScriptAsset->ScriptClassId == OriginalClassId);
    TestTrue(TEXT("Rename changes managed type name"), RenameResult.ScriptAsset->ScriptTypeName != OriginalTypeName);
    const FString RenamedSidecarPath = FPaths::Combine(
        ClassesDirectory,
        RenamedName + TEXT(".cs.usids"));
    TSet<FString> RenamedStateIds;
    TSet<FString> RenamedFunctionIds;
    if (!LoadIdentityEntryIds(
            *this,
            RenamedSidecarPath,
            RenamedStateIds,
            RenamedFunctionIds,
            TEXT("Renamed active")))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    TestFalse(TEXT("Successful rename removes the old identity sidecar"),
        IFileManager::Get().FileExists(*InitialSidecarPath));
    TestTrue(TEXT("Successful rename preserves state entry IDs"),
        IdentityIdSetsEqual(InitialStateIds, RenamedStateIds));
    TestTrue(TEXT("Successful rename preserves function entry IDs"),
        IdentityIdSetsEqual(InitialFunctionIds, RenamedFunctionIds));
    TestTrue(TEXT("Old active instance remains callable before rebuild"), InvokeAddOne(*this, First.Actor, 10, 11, TEXT("Before renamed rebuild")));
    TestTrue(TEXT("Rename-before-build keeps the original managed OnTick body"), TickExistingStartValue(*this, First, 50, TEXT("Before renamed rebuild")));
    FString RenamedSourcePath;
    FString RenamedSource;
    if (!FindManagedSource(*this, ClassesDirectory, RenamedName, RenamedSourcePath, RenamedSource) ||
        !PatchAddOneBody(*this, RenamedSourcePath, RenamedSource))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    const FString ValidRenamedSource = RenamedSource;
    FUnifiedScriptRuntimeDebugContext FirstBeforeInvalidReloadContext;
    int32 FirstStartValueBeforeInvalidReload = 0;
    if (!TestTrue(TEXT("Captures the renamed instance before invalid candidate"),
            FirstComponent->GetRuntimeDebugContext(FirstBeforeInvalidReloadContext)) ||
        !ReadStartValue(*this, First.Actor, FirstStartValueBeforeInvalidReload, TEXT("Before invalid candidate")))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    const int32 FirstManagedScriptsBeforeInvalidReload = First.Host->GetManagedScriptCount();
    const int32 FirstLifecycleClaimsBeforeInvalidReload = First.Host->GetGameplayLifecycleClaimCount();
    const FString FirstProgramBeforeInvalidReload = FirstComponent->GetActiveProgramPath();
    UUnifiedScriptAsset* FirstAssetBeforeInvalidReload = FirstComponent->ScriptAsset.Get();
    const FString InvalidClassIdDigits = TEXT("11111111111111111111111111111111");
    FString InvalidClassIdSource = RenamedSource;
    if (!PatchExplicitClassId(
            *this,
            RenamedSourcePath,
            InvalidClassIdSource,
            OriginalClassId,
            TEXT("us_") + InvalidClassIdDigits) ||
        !RunBuilder(
            *this,
            FixtureProject,
            OutputRootRelative,
            FPaths::Combine(OutputRoot, TEXT("fixture-build-invalid-class-id.log"))))
    {
        FFileHelper::SaveStringToFile(ValidRenamedSource, *RenamedSourcePath);
        RunBuilder(*this, FixtureProject, OutputRootRelative, FPaths::Combine(OutputRoot, TEXT("fixture-build-invalid-class-id-restore.log")));
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    const FString ExpectedInvalidClassIdError = FString::Printf(
        TEXT("UnifiedScript Class '%s' expects semantic ID '%s', but the selected declaration has '%s'. Rebuild or recreate the stale class proxy instead of binding by name."),
        *RenameResult.ScriptAsset->GetPathName(),
        *OriginalClassId.ToString(EGuidFormats::Digits),
        *InvalidClassIdDigits);
    AddExpectedError(
        *ExpectedInvalidClassIdError,
        EAutomationExpectedErrorFlags::Contains,
        1);
    TestFalse(TEXT("Rejects active reload with a changed explicit ClassId"), FirstComponent->ReloadScript());
    TestEqual(
        TEXT("Invalid ClassId reload reports the exact declaration identity failure"),
        FirstComponent->LastScriptError,
        ExpectedInvalidClassIdError);
    TSharedPtr<FJsonObject> InvalidReloadTransaction;
    const TSharedRef<TJsonReader<>> InvalidReloadReader =
        TJsonReaderFactory<>::Create(FirstComponent->LastReloadTransaction);
    const TArray<TSharedPtr<FJsonValue>>* InvalidReloadPhases = nullptr;
    const bool bInvalidReloadTransactionParsed =
        FJsonSerializer::Deserialize(InvalidReloadReader, InvalidReloadTransaction) &&
        InvalidReloadTransaction.IsValid() &&
        InvalidReloadTransaction->TryGetArrayField(TEXT("phases"), InvalidReloadPhases) &&
        InvalidReloadPhases != nullptr;
    bool bStateMigrationFailed = false;
    bool bOldGenerationStopped = false;
    bool bCandidateStarted = false;
    if (bInvalidReloadTransactionParsed)
    {
        for (const TSharedPtr<FJsonValue>& PhaseValue : *InvalidReloadPhases)
        {
            const TSharedPtr<FJsonObject>* PhaseObject = nullptr;
            if (!PhaseValue.IsValid() ||
                !PhaseValue->TryGetObject(PhaseObject) ||
                PhaseObject == nullptr ||
                !PhaseObject->IsValid())
            {
                continue;
            }
            FString PhaseName;
            FString PhaseState;
            (*PhaseObject)->TryGetStringField(TEXT("name"), PhaseName);
            (*PhaseObject)->TryGetStringField(TEXT("state"), PhaseState);
            bStateMigrationFailed |=
                PhaseName == TEXT("stateMigration") && PhaseState == TEXT("failed");
            bOldGenerationStopped |= PhaseName == TEXT("oldGenerationStop");
            bCandidateStarted |= PhaseName == TEXT("candidateStart");
        }
    }
    TestTrue(TEXT("Invalid ClassId reload records a valid transaction JSON"), bInvalidReloadTransactionParsed);
    TestTrue(
        TEXT("Invalid ClassId reload fails during state migration"),
        bStateMigrationFailed);
    TestFalse(
        TEXT("Invalid ClassId reload does not stop the old generation"),
        bOldGenerationStopped);
    TestFalse(
        TEXT("Invalid ClassId reload does not start a candidate generation"),
        bCandidateStarted);
    FUnifiedScriptRuntimeDebugContext FirstAfterInvalidReloadContext;
    TestTrue(TEXT("Invalid ClassId reload retains runtime identity"), FirstComponent->GetRuntimeDebugContext(FirstAfterInvalidReloadContext));
    TestEqual(TEXT("Invalid ClassId reload retains generation"), FirstAfterInvalidReloadContext.Generation, FirstBeforeInvalidReloadContext.Generation);
    TestEqual(TEXT("Invalid ClassId reload retains instance identity"), FirstAfterInvalidReloadContext.InstanceId, FirstBeforeInvalidReloadContext.InstanceId);
    TestEqual(TEXT("Invalid ClassId reload retains World host identity"), FirstAfterInvalidReloadContext.WorldHostId, FirstBeforeInvalidReloadContext.WorldHostId);
    TestEqual(TEXT("Invalid ClassId reload retains native host handle"), FirstAfterInvalidReloadContext.HostHandle, FirstBeforeInvalidReloadContext.HostHandle);
    TestEqual(TEXT("Invalid ClassId reload retains native target handle"), FirstAfterInvalidReloadContext.TargetHandle, FirstBeforeInvalidReloadContext.TargetHandle);
    int32 FirstStartValueAfterInvalidReload = 0;
    TestTrue(TEXT("Invalid ClassId reload retains native StartValue"),
        ReadStartValue(*this, First.Actor, FirstStartValueAfterInvalidReload, TEXT("After invalid candidate")) &&
        FirstStartValueAfterInvalidReload == FirstStartValueBeforeInvalidReload);
    TestTrue(TEXT("Invalid ClassId reload retains component object"), First.Actor->ScriptComponent.Get() == FirstComponent);
    TestTrue(TEXT("Invalid ClassId reload retains ScriptAsset ownership"), FirstComponent->ScriptAsset.Get() == FirstAssetBeforeInvalidReload);
    TestEqual(TEXT("Invalid ClassId reload retains active program"), FirstComponent->GetActiveProgramPath(), FirstProgramBeforeInvalidReload);
    TestTrue(TEXT("Invalid ClassId reload leaves the old body running"), InvokeAddOne(*this, First.Actor, 10, 11, TEXT("After invalid ClassId reload")));
    TestEqual(TEXT("Invalid ClassId reload retains managed script count"), First.Host->GetManagedScriptCount(), FirstManagedScriptsBeforeInvalidReload);
    TestEqual(TEXT("Invalid ClassId reload retains lifecycle claim count"), First.Host->GetGameplayLifecycleClaimCount(), FirstLifecycleClaimsBeforeInvalidReload);
    if (!TestTrue(TEXT("Restores valid renamed source after invalid candidate"),
            FFileHelper::SaveStringToFile(ValidRenamedSource, *RenamedSourcePath)) ||
        !RunBuilder(
            *this,
            FixtureProject,
            OutputRootRelative,
            FPaths::Combine(OutputRoot, TEXT("fixture-build-renamed-valid.log"))))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    TestTrue(TEXT("Reloads renamed active instance"), First.Actor->ScriptComponent->ReloadScript());
    TestTrue(TEXT("Renamed active instance uses the new AddOne body"), InvokeAddOne(*this, First.Actor, 11, 112, TEXT("After renamed reload")));
    TestTrue(TEXT("Renamed reload keeps the managed OnTick body"), TickExistingStartValue(*this, First, 161, TEXT("After renamed reload")));
    TestTrue(TEXT("Second world keeps the old AddOne body until its own reload"), InvokeAddOne(*this, Second.Actor, 10, 11, TEXT("Second before own reload")));
    TestTrue(TEXT("Second world retains accumulated old OnTick state before own reload"), TickExistingStartValue(*this, Second, 40, TEXT("Second before own reload")));
    FUnifiedScriptRuntimeDebugContext FirstAfterRenameContext;
    TestTrue(TEXT("Renamed active instance keeps a runtime identity"), First.Actor->ScriptComponent->GetRuntimeDebugContext(FirstAfterRenameContext));
    TestTrue(TEXT("Renamed reload preserves the original component object"), First.Actor->ScriptComponent == FirstComponent);
    TestTrue(TEXT("Renamed reload preserves the second component object"), Second.Actor->ScriptComponent == SecondComponent);
    TestEqual(TEXT("Renamed reload keeps the managed instance identity"), FirstAfterRenameContext.InstanceId, FirstInitialContext.InstanceId);
    TestEqual(TEXT("Renamed reload keeps the first World identity"), FirstAfterRenameContext.WorldHostId, FirstInitialContext.WorldHostId);
    TestEqual(TEXT("Renamed reload keeps the first lifecycle claim"), First.Host->GetGameplayLifecycleClaimCount(), 1);
    TestEqual(TEXT("Renamed reload leaves the second lifecycle claim isolated"), Second.Host->GetGameplayLifecycleClaimCount(), 1);

    TArray<uint8> RenamedSidecarBytes;
    TArray<uint8> RenamedSourceBytes;
    if (!TestTrue(TEXT("Snapshots the renamed source before duplicate"),
            FFileHelper::LoadFileToArray(RenamedSourceBytes, *RenamedSourcePath)) ||
        !TestTrue(TEXT("Snapshots the renamed identity sidecar before duplicate"),
            FFileHelper::LoadFileToArray(RenamedSidecarBytes, *RenamedSidecarPath)))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    FUnifiedScriptClassAssetOperationResult DuplicateResult;
    const FString DuplicatePackage = Root / DuplicateName;
    if (!TestTrue(TEXT("Duplicates active Script Class"), FUnifiedScriptClassAssetOperations::Duplicate(
        RenameResult.ScriptAsset, DuplicatePackage, DuplicateResult)) ||
        !TestNotNull(TEXT("Duplicate Blueprint exists"), DuplicateResult.Blueprint) ||
        !TestNotNull(TEXT("Duplicate ScriptAsset exists"), DuplicateResult.ScriptAsset))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    TestTrue(TEXT("Duplicate receives a new ClassId"), DuplicateResult.ScriptAsset->ScriptClassId != OriginalClassId);
    TestTrue(TEXT("Duplicate receives a distinct generated class"), DuplicateResult.ScriptAsset->GeneratedClassPath != RenameResult.ScriptAsset->GeneratedClassPath);
    const FString DuplicateSidecarPath = FPaths::Combine(
        ClassesDirectory,
        DuplicateName + TEXT(".cs.usids"));
    TSet<FString> DuplicateStateIds;
    TSet<FString> DuplicateFunctionIds;
    if (!LoadIdentityEntryIds(
            *this,
            DuplicateSidecarPath,
            DuplicateStateIds,
            DuplicateFunctionIds,
            TEXT("Duplicate active")))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    TestTrue(TEXT("Duplicate does not rewrite the original identity sidecar"),
        FFileHelper::LoadFileToArray(CurrentBytes, *RenamedSidecarPath) &&
        CurrentBytes == RenamedSidecarBytes);
    TestTrue(TEXT("Duplicate does not rewrite the original managed source"),
        FFileHelper::LoadFileToArray(CurrentBytes, *RenamedSourcePath) &&
        CurrentBytes == RenamedSourceBytes);
    bool bDisjointStateIds = true;
    for (const FString& Id : RenamedStateIds)
    {
        bDisjointStateIds &= !DuplicateStateIds.Contains(Id);
    }
    bool bDisjointFunctionIds = true;
    for (const FString& Id : RenamedFunctionIds)
    {
        bDisjointFunctionIds &= !DuplicateFunctionIds.Contains(Id);
    }
    TestTrue(TEXT("Duplicate owns independent state entry IDs"), bDisjointStateIds);
    TestTrue(TEXT("Duplicate owns independent function entry IDs"), bDisjointFunctionIds);
    UBlueprint* DuplicateChild = CreateChild(*this, DuplicateResult.Blueprint->GeneratedClass, Root / (DuplicateName + TEXT("_Child")), DuplicateName + TEXT("_Child"));
    UBlueprint* DuplicateGrandchild = DuplicateChild != nullptr
        ? CreateChild(*this, DuplicateChild->GeneratedClass, Root / (DuplicateName + TEXT("_Grandchild")), DuplicateName + TEXT("_Grandchild"))
        : nullptr;
    TestTrue(TEXT("Duplicate child and grandchild inherit the duplicate ScriptAsset"),
        DuplicateChild != nullptr && DuplicateGrandchild != nullptr &&
        GetScriptAsset(DuplicateChild) == DuplicateResult.ScriptAsset &&
        GetScriptAsset(DuplicateGrandchild) == DuplicateResult.ScriptAsset);
    if (!RunBuilder(*this, FixtureProject, OutputRootRelative, FPaths::Combine(OutputRoot, TEXT("fixture-build-duplicate.log"))))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    if (!SynchronizeAssetFromConfiguredDeclaration(
            *this,
            DuplicateResult.ScriptAsset,
            DuplicateResult.Blueprint,
            TEXT("Duplicate")))
    {
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    if (DuplicateChild != nullptr)
    {
        FKismetEditorUtilities::CompileBlueprint(DuplicateChild);
    }
    if (DuplicateGrandchild != nullptr)
    {
        FKismetEditorUtilities::CompileBlueprint(DuplicateGrandchild);
    }
    TestTrue(TEXT("Duplicate child keeps the synchronized ScriptAsset"), GetScriptAsset(DuplicateChild) == DuplicateResult.ScriptAsset);
    TestTrue(TEXT("Duplicate grandchild keeps the synchronized ScriptAsset"), GetScriptAsset(DuplicateGrandchild) == DuplicateResult.ScriptAsset);
    FWorldFixture DuplicateWorld;
    if (!MakeWorld(DuplicateWorld, WorldError))
    {
        AddError(WorldError);
        DestroyWorld(First);
        DestroyWorld(Second);
        return false;
    }
    FUnifiedScriptRuntimeDebugContext DuplicateContext;
    DuplicateWorld.Actor = DuplicateGrandchild != nullptr
        ? DuplicateWorld.World->SpawnActor<AUnifiedScriptActor>(DuplicateGrandchild->GeneratedClass, FTransform::Identity)
        : nullptr;
    if (TestNotNull(TEXT("Spawns duplicate active actor"), DuplicateWorld.Actor) &&
        TestNotNull(TEXT("Duplicate active actor has a ScriptComponent"), DuplicateWorld.Actor->ScriptComponent.Get()))
    {
        DuplicateWorld.Actor->DispatchBeginPlay();
        TestTrue(TEXT("Duplicate active script starts independently"), DuplicateWorld.Actor->ScriptComponent->bScriptRunning);
        TestTrue(TEXT("Duplicate grandchild actor uses the duplicate ScriptAsset"), DuplicateWorld.Actor->ScriptComponent->ScriptAsset.Get() == DuplicateResult.ScriptAsset);
        TestTrue(TEXT("Duplicate grandchild actor has the duplicate component archetype"),
            DuplicateGrandchild != nullptr &&
            DuplicateWorld.Actor->ScriptComponent->GetArchetype() == GetComponent(DuplicateGrandchild));
        TestTrue(TEXT("Duplicate active AddOne uses the duplicate body"), InvokeAddOne(*this, DuplicateWorld.Actor, 12, 113, TEXT("Duplicate")));
        TestTrue(TEXT("Duplicate active OnTick remains independent"), SetStartValueAndTick(*this, DuplicateWorld, 20, 30, TEXT("Duplicate")));
        TestTrue(TEXT("Duplicate grandchild exposes a managed runtime identity"), DuplicateWorld.Actor->ScriptComponent->GetRuntimeDebugContext(DuplicateContext));
        TestTrue(TEXT("Duplicate grandchild owns an independent managed instance"),
            DuplicateContext.InstanceId != FirstInitialContext.InstanceId &&
            DuplicateContext.InstanceId != SecondInitialContext.InstanceId);
        TestTrue(TEXT("Duplicate grandchild owns an independent managed World"),
            DuplicateContext.WorldHostId != FirstInitialContext.WorldHostId &&
            DuplicateContext.WorldHostId != SecondInitialContext.WorldHostId);
        TestEqual(TEXT("Duplicate World owns one lifecycle claim"), DuplicateWorld.Host->GetGameplayLifecycleClaimCount(), 1);
    }
    TestEqual(TEXT("Duplicate World is added to the managed World count"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialManagedWorldCount + 3);
    if (TestNotNull(TEXT("Second world retains its ScriptComponent before own reload"), Second.Actor->ScriptComponent.Get()))
    {
        TestTrue(TEXT("Second world reloads independently onto the renamed body"), Second.Actor->ScriptComponent->ReloadScript());
        TestTrue(TEXT("Second world uses the renamed AddOne after its own reload"), InvokeAddOne(*this, Second.Actor, 10, 111, TEXT("Second after own reload")));
        TestTrue(TEXT("Second world keeps the synchronized OnTick body"), TickExistingStartValue(*this, Second, 151, TEXT("Second after own reload")));
    }
    DestroyActor(DuplicateWorld);
    TestEqual(TEXT("Duplicate World releases managed scripts before teardown"), DuplicateWorld.Host->GetManagedScriptCount(), 0);
    TestEqual(TEXT("Duplicate World releases lifecycle claims before teardown"), DuplicateWorld.Host->GetGameplayLifecycleClaimCount(), 0);
    if (DuplicateContext.WorldHostId != 0)
    {
        TestEqual(TEXT("Duplicate World releases its CoreClr script before teardown"),
            FUnifiedScriptCoreClrHost::Get().GetManagedScriptCount(DuplicateContext.WorldHostId),
            0);
    }
    DestroyWorld(DuplicateWorld);
    TestEqual(TEXT("Duplicate World teardown leaves the two original Worlds"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialManagedWorldCount + 2);

    DestroyActor(First);
    TestEqual(TEXT("First World releases managed scripts before teardown"), First.Host->GetManagedScriptCount(), 0);
    TestEqual(TEXT("First World releases lifecycle claims before teardown"), First.Host->GetGameplayLifecycleClaimCount(), 0);
    TestEqual(TEXT("First World releases its CoreClr script before teardown"),
        FUnifiedScriptCoreClrHost::Get().GetManagedScriptCount(FirstInitialContext.WorldHostId),
        0);
    DestroyWorld(First);
    TestEqual(TEXT("First World teardown leaves the second World"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialManagedWorldCount + 1);
    TestEqual(TEXT("Second World remains managed after First teardown"), Second.Host->GetManagedScriptCount(), 1);
    TestEqual(TEXT("Second World retains its lifecycle claim after First teardown"), Second.Host->GetGameplayLifecycleClaimCount(), 1);

    DestroyActor(Second);
    TestEqual(TEXT("Second World releases managed scripts before teardown"), Second.Host->GetManagedScriptCount(), 0);
    TestEqual(TEXT("Second World releases lifecycle claims before teardown"), Second.Host->GetGameplayLifecycleClaimCount(), 0);
    TestEqual(TEXT("Second World releases its CoreClr script before teardown"),
        FUnifiedScriptCoreClrHost::Get().GetManagedScriptCount(SecondInitialContext.WorldHostId),
        0);
    DestroyWorld(Second);
    TestEqual(TEXT("Active fixture restores the managed World baseline"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialManagedWorldCount);
    return !HasAnyErrors();
}

#endif

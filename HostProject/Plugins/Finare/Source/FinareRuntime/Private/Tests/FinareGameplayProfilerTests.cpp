#if WITH_DEV_AUTOMATION_TESTS

#include "FinareWorldSubsystem.h"

#include "Dom/JsonObject.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "Misc/AutomationTest.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "FinareAsset.h"
#include "FinareComponent.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareGameplayProfilerFullAttributionTest,
    "Finare.Unreal.Profiler.FullAttribution",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareGameplayProfilerFullAttributionTest::RunTest(
    const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Gameplay profiler World exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();
    UFinareWorldSubsystem* Host =
        World->GetSubsystem<UFinareWorldSubsystem>();
    TestNotNull(TEXT("Gameplay profiler World host exists"), Host);
    if (Host == nullptr)
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }

    AActor* Actor = World->SpawnActor<AActor>();
    TestNotNull(TEXT("Gameplay profiler target Actor exists"), Actor);
    if (Actor == nullptr)
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }
    UFinareComponent* Component =
        NewObject<UFinareComponent>(Actor);
    UFinareAsset* Asset = NewObject<UFinareAsset>(World);
    Asset->ScriptAssemblyPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/Finare.Unreal.Demo.dll");
    Asset->ScriptTypeName =
        TEXT("Finare.Unreal.Demo.GameplayProfilerDemoScript");
    Component->ScriptAsset = Asset;
    Actor->AddInstanceComponent(Component);
    Component->RegisterComponentWithWorld(World);
    Component->BeginPlay();
    TestTrue(
        *FString::Printf(
            TEXT("Full-attribution workload starts: %s"),
            *Component->LastScriptError),
        Component->bScriptRunning);

    FString ProfilerError;
    TestTrue(
        *FString::Printf(
            TEXT("Full-attribution profiler enables: %s"),
            *ProfilerError),
        Component->SetGameplayProfilerEnabled(true, ProfilerError));
    Host->EndManagedFrame();
    Host->BeginManagedFrame();
    Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    Host->EndManagedFrame();
    Host->BeginManagedFrame();
    Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    TestEqual(
        TEXT("Full-attribution workload completes its managed Tick"),
        Component->ManagedState,
        FString(TEXT("GameplayProfilerDemo")));

    FString ProfileJson;
    TestTrue(
        *FString::Printf(
            TEXT("Full-attribution trace exports: %s"),
            *ProfilerError),
        Component->CaptureGameplayProfileTrace(ProfileJson, ProfilerError));
    UE_LOG(
        LogTemp,
        Display,
        TEXT("FINARE_GAMEPLAY_PROFILE_JSON %s"),
        *ProfileJson);
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader =
        TJsonReaderFactory<>::Create(ProfileJson);
    const bool bParsed =
        FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid();
    TestTrue(TEXT("Full-attribution JSON is readable"), bParsed);

    const TSharedPtr<FJsonObject>* Aggregate = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* Boundaries = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* WorldPhases = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* Nodes = nullptr;
    const TSharedPtr<FJsonObject>* Pressure = nullptr;
    FString SchemaVersion;
    const bool bHasAggregate = bParsed &&
        Root->TryGetStringField(TEXT("schemaVersion"), SchemaVersion) &&
        SchemaVersion == TEXT("unifiedscript.gameplay-profile/v0.3") &&
        Root->TryGetObjectField(TEXT("aggregate"), Aggregate) &&
        Aggregate != nullptr &&
        (*Aggregate)->TryGetArrayField(TEXT("boundaries"), Boundaries) &&
        Boundaries != nullptr &&
        (*Aggregate)->TryGetArrayField(TEXT("worldPhases"), WorldPhases) &&
        WorldPhases != nullptr &&
        (*Aggregate)->TryGetArrayField(TEXT("nodes"), Nodes) &&
        Nodes != nullptr &&
        (*Aggregate)->TryGetObjectField(TEXT("bufferPressure"), Pressure) &&
        Pressure != nullptr;
    TestTrue(
        TEXT("Full-attribution trace binds every aggregate family"),
        bHasAggregate);

    TMap<FString, double> BoundaryCounts;
    if (Boundaries != nullptr)
    {
        for (const TSharedPtr<FJsonValue>& Value : *Boundaries)
        {
            const TSharedPtr<FJsonObject> Boundary = Value->AsObject();
            FString Name;
            double Count = 0.0;
            if (Boundary.IsValid() &&
                Boundary->TryGetStringField(TEXT("name"), Name) &&
                Boundary->TryGetNumberField(TEXT("count"), Count))
            {
                BoundaryCounts.Add(MoveTemp(Name), Count);
            }
        }
    }
    for (const TCHAR* BoundaryName : {
             TEXT("directThunk"), TEXT("callFrame"),
             TEXT("reflectionFallback"), TEXT("commands"), TEXT("events"),
             TEXT("batchTrace"), TEXT("batchOverlap")})
    {
        TestTrue(
            *FString::Printf(TEXT("%s boundary is attributed"), BoundaryName),
            BoundaryCounts.FindRef(BoundaryName) >= 1.0);
    }

    bool bWorldPhasesAttributed =
        WorldPhases != nullptr && WorldPhases->Num() == 3;
    if (WorldPhases != nullptr)
    {
        for (const TSharedPtr<FJsonValue>& Value : *WorldPhases)
        {
            const TSharedPtr<FJsonObject> Phase = Value->AsObject();
            double Count = 0.0;
            double Nanoseconds = 0.0;
            bWorldPhasesAttributed = bWorldPhasesAttributed && Phase.IsValid() &&
                Phase->TryGetNumberField(TEXT("count"), Count) && Count >= 1.0 &&
                Phase->TryGetNumberField(
                    TEXT("totalNanoseconds"), Nanoseconds) &&
                Nanoseconds > 0.0;
        }
    }
    TestTrue(
        TEXT("All World scheduler phases are attributed"),
        bWorldPhasesAttributed);

    bool bSemanticNodesAttributed = Nodes != nullptr && Nodes->Num() >= 2;
    if (Nodes != nullptr)
    {
        for (const TSharedPtr<FJsonValue>& Value : *Nodes)
        {
            const TSharedPtr<FJsonObject> Node = Value->AsObject();
            FString MethodId;
            FString NodeId;
            double HitCount = 0.0;
            double Nanoseconds = 0.0;
            const TArray<TSharedPtr<FJsonValue>>* NodeBoundaries = nullptr;
            bSemanticNodesAttributed = bSemanticNodesAttributed && Node.IsValid() &&
                Node->TryGetStringField(TEXT("methodId"), MethodId) &&
                MethodId.StartsWith(TEXT("us_")) &&
                Node->TryGetStringField(TEXT("nodeId"), NodeId) &&
                NodeId.StartsWith(TEXT("us_")) &&
                Node->TryGetNumberField(TEXT("hitCount"), HitCount) &&
                HitCount >= 1.0 &&
                Node->TryGetNumberField(TEXT("totalNanoseconds"), Nanoseconds) &&
                Nanoseconds >= 0.0 &&
                Node->TryGetArrayField(TEXT("boundaries"), NodeBoundaries) &&
                NodeBoundaries != nullptr && NodeBoundaries->Num() == 7;
        }
    }
    TestTrue(
        TEXT("Semantic source and graph nodes share the gameplay timeline"),
        bSemanticNodesAttributed);

    double AllocatedBytes = 0.0;
    TestTrue(
        TEXT("Managed allocation is attributed"),
        Aggregate != nullptr &&
            (*Aggregate)->TryGetNumberField(
                TEXT("allocatedBytes"), AllocatedBytes) &&
            AllocatedBytes > 0.0);
    double CommandOverflow = 0.0;
    double TraceOverflow = 0.0;
    double OverlapOverflow = 0.0;
    TestTrue(
        TEXT("Command, Trace, and Overlap pressure are attributed"),
        Pressure != nullptr &&
            (*Pressure)->TryGetNumberField(
                TEXT("commandOverflowCount"), CommandOverflow) &&
            CommandOverflow >= 1.0 &&
            (*Pressure)->TryGetNumberField(
                TEXT("traceOverflowCount"), TraceOverflow) &&
            TraceOverflow >= 1.0 &&
            (*Pressure)->TryGetNumberField(
                TEXT("overlapOverflowCount"), OverlapOverflow) &&
            OverlapOverflow >= 1.0);

    Host->EndManagedFrame();
    Component->EndPlay(EEndPlayReason::Destroyed);
    Component->UnregisterComponent();
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

#endif

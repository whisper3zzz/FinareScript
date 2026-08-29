#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptWorldLibrary.h"

#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/UObjectGlobals.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptRuntimeOwnershipTest,
    "UnifiedScript.Unreal.Coexistence.RuntimeOwnership",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptRuntimeOwnershipTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    UWorld* OtherWorld = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Fixture World exists"), World);
    TestNotNull(TEXT("Foreign World exists"), OtherWorld);
    if (World == nullptr || OtherWorld == nullptr)
    {
        return false;
    }
    World->AddToRoot();
    OtherWorld->AddToRoot();

    UUnifiedScriptWorldSubsystem* Host =
        World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    TestNotNull(TEXT("World owns the runtime arbitration subsystem"), Host);
    if (Host == nullptr)
    {
        World->RemoveFromRoot();
        OtherWorld->RemoveFromRoot();
        World->DestroyWorld(false);
        OtherWorld->DestroyWorld(false);
        return false;
    }

    UUnifiedScriptComponent* GameplayHost =
        NewObject<UUnifiedScriptComponent>(World);
    UUnifiedScriptComponent* PuertsClaimant =
        NewObject<UUnifiedScriptComponent>(World);
    UUnifiedScriptComponent* ManagedClaimant =
        NewObject<UUnifiedScriptComponent>(World);
    UUnifiedScriptComponent* ForeignClaimant =
        NewObject<UUnifiedScriptComponent>(OtherWorld);
    const FName PlayerLifecycle(TEXT("BlockBreaker.TS_Player"));
    const FName RifleLifecycle(TEXT("BlockBreaker.TS_Rifle"));
    FString Error;

    TestTrue(
        TEXT("Puerts claims the Player lifecycle through the shared Blueprint API"),
        UUnifiedScriptWorldLibrary::ClaimGameplayLifecycle(
            World,
            PuertsClaimant,
            GameplayHost,
            PlayerLifecycle,
            EUnifiedScriptRuntimeOwner::Puerts,
            Error));
    TestTrue(TEXT("Successful claim has no error"), Error.IsEmpty());
    TestEqual(TEXT("World owns one lifecycle claim"), Host->GetGameplayLifecycleClaimCount(), 1);
    TestTrue(
        TEXT("The same claimant can retry idempotently"),
        UUnifiedScriptWorldLibrary::ClaimGameplayLifecycle(
            World,
            PuertsClaimant,
            GameplayHost,
            PlayerLifecycle,
            EUnifiedScriptRuntimeOwner::Puerts,
            Error));
    TestEqual(TEXT("Idempotent retry does not duplicate the claim"), Host->GetGameplayLifecycleClaimCount(), 1);

    TestFalse(
        TEXT("UnifiedScript cannot double-own the Puerts Player lifecycle"),
        UUnifiedScriptWorldLibrary::ClaimGameplayLifecycle(
            World,
            ManagedClaimant,
            GameplayHost,
            PlayerLifecycle,
            EUnifiedScriptRuntimeOwner::UnifiedScript,
            Error));
    TestTrue(TEXT("Conflict names the existing owner"), Error.Contains(TEXT("already owned by puerts")));
    TestTrue(
        TEXT("A distinct route can migrate independently to UnifiedScript"),
        UUnifiedScriptWorldLibrary::ClaimGameplayLifecycle(
            World,
            ManagedClaimant,
            GameplayHost,
            RifleLifecycle,
            EUnifiedScriptRuntimeOwner::UnifiedScript,
            Error));
    TestEqual(TEXT("Independent routes coexist in one World"), Host->GetGameplayLifecycleClaimCount(), 2);

    TestFalse(
        TEXT("A foreign World claimant is rejected"),
        UUnifiedScriptWorldLibrary::ClaimGameplayLifecycle(
            World,
            ForeignClaimant,
            GameplayHost,
            FName(TEXT("BlockBreaker.Foreign")),
            EUnifiedScriptRuntimeOwner::Puerts,
            Error));
    TestTrue(TEXT("Foreign claim reports the World boundary"), Error.Contains(TEXT("same World")));
    TestFalse(
        TEXT("A different runtime cannot release another claimant's route"),
        UUnifiedScriptWorldLibrary::ReleaseGameplayLifecycle(
            World,
            ManagedClaimant,
            GameplayHost,
            PlayerLifecycle,
            EUnifiedScriptRuntimeOwner::UnifiedScript,
            Error));

    const FString Report =
        UUnifiedScriptWorldLibrary::GetGameplayLifecycleOwnershipReport(World);
    TestTrue(
        TEXT("Ownership report is schema-versioned"),
        Report.Contains(TEXT("unifiedscript.runtime-ownership-report/v0.1")));
    TestTrue(TEXT("Ownership report contains the Player key"), Report.Contains(PlayerLifecycle.ToString()));
    TestTrue(TEXT("Ownership report contains the Puerts lane"), Report.Contains(TEXT("\"runtimeOwner\":\"puerts\"")));
    TestTrue(TEXT("Ownership report contains the managed lane"), Report.Contains(TEXT("\"runtimeOwner\":\"unifiedScript\"")));

    TestTrue(
        TEXT("Puerts releases its own route"),
        UUnifiedScriptWorldLibrary::ReleaseGameplayLifecycle(
            World,
            PuertsClaimant,
            GameplayHost,
            PlayerLifecycle,
            EUnifiedScriptRuntimeOwner::Puerts,
            Error));
    TestTrue(
        TEXT("UnifiedScript releases its own route"),
        UUnifiedScriptWorldLibrary::ReleaseGameplayLifecycle(
            World,
            ManagedClaimant,
            GameplayHost,
            RifleLifecycle,
            EUnifiedScriptRuntimeOwner::UnifiedScript,
            Error));
    TestEqual(TEXT("All ownership claims were released"), Host->GetGameplayLifecycleClaimCount(), 0);

    World->RemoveFromRoot();
    OtherWorld->RemoveFromRoot();
    World->DestroyWorld(false);
    OtherWorld->DestroyWorld(false);
    return true;
}

#endif

#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptObjectRegistry.h"

#include "Async/Async.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "UnifiedScriptComponent.h"
#include "UObject/GarbageCollection.h"
#include "UObject/UObjectGlobals.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptObjectRegistryLifecycleTest,
    "UnifiedScript.Unreal.ObjectRegistry.Lifecycle",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptObjectRegistryLifecycleTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    UWorld* OtherWorld = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Fixture world exists"), World);
    TestNotNull(TEXT("Other fixture world exists"), OtherWorld);
    if (World == nullptr || OtherWorld == nullptr)
    {
        return false;
    }

    World->AddToRoot();
    OtherWorld->AddToRoot();

    {
        FUnifiedScriptObjectRegistry Registry(World);
        FUnifiedScriptObjectHandleResult Result;

        UUnifiedScriptComponent* First = NewObject<UUnifiedScriptComponent>(World);
        FUnifiedScriptObjectHandle FirstHandle = Registry.RegisterObject(First, Result);
        TestTrue(TEXT("First registration succeeds"), Result.IsSuccess());
        TestTrue(TEXT("First handle is well formed"), FirstHandle.IsWellFormed());
        TestEqual(TEXT("Registry owns one weak entry"), Registry.Num(), 1);

        FUnifiedScriptObjectHandle DuplicateHandle = Registry.RegisterObject(First, Result);
        TestTrue(TEXT("Duplicate registration succeeds"), Result.IsSuccess());
        TestTrue(TEXT("Duplicate registration reuses the same handle"), DuplicateHandle == FirstHandle);
        TestTrue(
            TEXT("First handle resolves to the registered UObject"),
            Registry.ResolveObject(FirstHandle, Result) == First);
        TestTrue(TEXT("Resolution reports success"), Result.IsSuccess());

        UUnifiedScriptComponent* Foreign = NewObject<UUnifiedScriptComponent>(OtherWorld);
        FUnifiedScriptObjectHandle ForeignHandle = Registry.RegisterObject(Foreign, Result);
        TestTrue(TEXT("Foreign-world registration returns a null handle"), ForeignHandle.IsNull());
        TestTrue(
            TEXT("Foreign-world registration is rejected explicitly"),
            Result.Error == EUnifiedScriptObjectHandleError::WorldMismatch);

        TestTrue(TEXT("Explicit release succeeds"), Registry.ReleaseObject(FirstHandle, Result));
        TestTrue(TEXT("Release reports success"), Result.IsSuccess());
        TestEqual(TEXT("Release removes the active entry"), Registry.Num(), 0);
        TestNull(TEXT("Released handle no longer resolves"), Registry.ResolveObject(FirstHandle, Result));
        TestTrue(
            TEXT("Released handle is stale"),
            Result.Error == EUnifiedScriptObjectHandleError::StaleHandle);

        UUnifiedScriptComponent* Reused = NewObject<UUnifiedScriptComponent>(World);
        FUnifiedScriptObjectHandle ReusedHandle = Registry.RegisterObject(Reused, Result);
        TestTrue(TEXT("Reused-slot registration succeeds"), Result.IsSuccess());
        TestEqual(TEXT("Released slot is reused"), ReusedHandle.GetSlotIndex(), FirstHandle.GetSlotIndex());
        TestTrue(
            TEXT("Reused slot advances its generation"),
            ReusedHandle.GetGeneration() != FirstHandle.GetGeneration());
        TestNull(TEXT("Old generation remains stale"), Registry.ResolveObject(FirstHandle, Result));
        TestTrue(
            TEXT("Old generation reports stale"),
            Result.Error == EUnifiedScriptObjectHandleError::StaleHandle);

        const TFuture<EUnifiedScriptObjectHandleError> OffThreadResult = Async(
            EAsyncExecution::ThreadPool,
            [&Registry, ReusedHandle]()
            {
                FUnifiedScriptObjectHandleResult ThreadResult;
                Registry.ResolveObject(ReusedHandle, ThreadResult);
                return ThreadResult.Error;
            });
        TestTrue(
            TEXT("Off-thread access is rejected"),
            OffThreadResult.Get() == EUnifiedScriptObjectHandleError::NotGameThread);

        Reused = nullptr;
        CollectGarbage(GARBAGE_COLLECTION_KEEPFLAGS);
        TestEqual(TEXT("UObject deletion retires its weak handle"), Registry.Num(), 0);
        TestNull(TEXT("Destroyed UObject handle no longer resolves"), Registry.ResolveObject(ReusedHandle, Result));
        TestTrue(
            TEXT("Destroyed UObject reports a stale handle"),
            Result.Error == EUnifiedScriptObjectHandleError::StaleHandle);
    }

    World->RemoveFromRoot();
    OtherWorld->RemoveFromRoot();
    World->DestroyWorld(false);
    OtherWorld->DestroyWorld(false);
    return true;
}

#endif

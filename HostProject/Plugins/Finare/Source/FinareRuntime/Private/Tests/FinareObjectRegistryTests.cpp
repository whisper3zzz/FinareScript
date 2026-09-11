#if WITH_DEV_AUTOMATION_TESTS

#include "FinareObjectRegistry.h"
#include "FinareNativeTransport.h"

#include "Async/Async.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "FinareComponent.h"
#include "UObject/GarbageCollection.h"
#include "UObject/UObjectGlobals.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareObjectRegistryLifecycleTest,
    "Finare.Unreal.ObjectRegistry.Lifecycle",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareObjectRegistryLifecycleTest::RunTest(const FString& Parameters)
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
        FFinareObjectRegistry Registry(World);
        FFinareObjectHandleResult Result;

        UFinareComponent* First = NewObject<UFinareComponent>(World);
        FFinareObjectHandle FirstHandle = Registry.RegisterObject(First, Result);
        TestTrue(TEXT("First registration succeeds"), Result.IsSuccess());
        TestTrue(TEXT("First handle is well formed"), FirstHandle.IsWellFormed());
        TestEqual(TEXT("Registry owns one weak entry"), Registry.Num(), 1);

        FFinareObjectHandle DuplicateHandle = Registry.RegisterObject(First, Result);
        TestTrue(TEXT("Duplicate registration succeeds"), Result.IsSuccess());
        TestTrue(TEXT("Duplicate registration reuses the same handle"), DuplicateHandle == FirstHandle);
        TestTrue(
            TEXT("First handle resolves to the registered UObject"),
            Registry.ResolveObject(FirstHandle, Result) == First);
        TestTrue(TEXT("Resolution reports success"), Result.IsSuccess());

        UFinareComponent* Foreign = NewObject<UFinareComponent>(OtherWorld);
        FFinareObjectHandle ForeignHandle = Registry.RegisterObject(Foreign, Result);
        TestTrue(TEXT("Foreign-world registration returns a null handle"), ForeignHandle.IsNull());
        TestTrue(
            TEXT("Foreign-world registration is rejected explicitly"),
            Result.Error == EFinareObjectHandleError::WorldMismatch);

        TestTrue(TEXT("Explicit release succeeds"), Registry.ReleaseObject(FirstHandle, Result));
        TestTrue(TEXT("Release reports success"), Result.IsSuccess());
        TestEqual(TEXT("First release preserves the duplicate owner"), Registry.Num(), 1);
        TestTrue(
            TEXT("Reference-counted handle still resolves"),
            Registry.ResolveObject(FirstHandle, Result) == First);
        TestTrue(TEXT("Final explicit release succeeds"), Registry.ReleaseObject(FirstHandle, Result));
        TestEqual(TEXT("Release removes the active entry"), Registry.Num(), 0);
        TestNull(TEXT("Released handle no longer resolves"), Registry.ResolveObject(FirstHandle, Result));
        TestTrue(
            TEXT("Released handle is stale"),
            Result.Error == EFinareObjectHandleError::StaleHandle);

        UFinareComponent* Reused = NewObject<UFinareComponent>(World);
        FFinareObjectHandle ReusedHandle = Registry.RegisterObject(Reused, Result);
        TestTrue(TEXT("Reused-slot registration succeeds"), Result.IsSuccess());
        TestEqual(TEXT("Released slot is reused"), ReusedHandle.GetSlotIndex(), FirstHandle.GetSlotIndex());
        TestTrue(
            TEXT("Reused slot advances its generation"),
            ReusedHandle.GetGeneration() != FirstHandle.GetGeneration());
        TestNull(TEXT("Old generation remains stale"), Registry.ResolveObject(FirstHandle, Result));
        TestTrue(
            TEXT("Old generation reports stale"),
            Result.Error == EFinareObjectHandleError::StaleHandle);

        const TFuture<EFinareObjectHandleError> OffThreadResult = Async(
            EAsyncExecution::ThreadPool,
            [&Registry, ReusedHandle]()
            {
                FFinareObjectHandleResult ThreadResult;
                Registry.ResolveObject(ReusedHandle, ThreadResult);
                return ThreadResult.Error;
            });
        TestTrue(
            TEXT("Off-thread access is rejected"),
            OffThreadResult.Get() == EFinareObjectHandleError::NotGameThread);

        Reused = nullptr;
        CollectGarbage(GARBAGE_COLLECTION_KEEPFLAGS);
        TestEqual(TEXT("UObject deletion retires its weak handle"), Registry.Num(), 0);
        TestNull(TEXT("Destroyed UObject handle no longer resolves"), Registry.ResolveObject(ReusedHandle, Result));
        TestTrue(
            TEXT("Destroyed UObject reports a stale handle"),
            Result.Error == EFinareObjectHandleError::StaleHandle);

        UFinareComponent* GcLeased = NewObject<UFinareComponent>(World);
        FFinareObjectHandle GcLeasedHandle = Registry.RegisterObject(GcLeased, Result);
        uint64 GcReferenceScope = 0;
        uint64 GcSecondReferenceScope = 0;
        TestTrue(TEXT("GC lease scope opens"), Registry.OpenReferenceScope(GcReferenceScope, Result));
        TestTrue(
            TEXT("Second GC lease scope opens"),
            Registry.OpenReferenceScope(GcSecondReferenceScope, Result));
        TestTrue(
            TEXT("GC lease is retained"),
            Registry.RetainReference(GcReferenceScope, GcLeasedHandle, Result));
        TestTrue(
            TEXT("Repeated GC lease is retained"),
            Registry.RetainReference(GcReferenceScope, GcLeasedHandle, Result));
        TestTrue(
            TEXT("Concurrent GC lease is retained"),
            Registry.RetainReference(GcSecondReferenceScope, GcLeasedHandle, Result));
        GcLeased = nullptr;
        CollectGarbage(GARBAGE_COLLECTION_KEEPFLAGS);
        TestTrue(
            TEXT("First release after UObject GC is idempotent"),
            Registry.ReleaseReference(GcReferenceScope, GcLeasedHandle, Result));
        TestTrue(TEXT("GC release reports success"), Result.IsSuccess());
        TestTrue(
            TEXT("Second release after UObject GC is idempotent"),
            Registry.ReleaseReference(GcReferenceScope, GcLeasedHandle, Result));
        TestTrue(TEXT("Second GC release reports success"), Result.IsSuccess());
        TestTrue(
            TEXT("Concurrent release after UObject GC is idempotent"),
            Registry.ReleaseReference(GcSecondReferenceScope, GcLeasedHandle, Result));
        TestTrue(TEXT("Concurrent GC release reports success"), Result.IsSuccess());
        TestTrue(
            TEXT("Close after UObject GC is idempotent"),
            Registry.CloseReferenceScope(GcReferenceScope, Result));
        TestTrue(TEXT("GC scope close reports success"), Result.IsSuccess());
        TestTrue(
            TEXT("Close second scope after UObject GC is idempotent"),
            Registry.CloseReferenceScope(GcSecondReferenceScope, Result));
        TestTrue(TEXT("Second GC scope close reports success"), Result.IsSuccess());

        UFinareComponent* Leased = NewObject<UFinareComponent>(World);
        FFinareObjectHandle LeasedHandle = Registry.RegisterObject(Leased, Result);
        uint64 ReferenceScope = 0;
        uint64 SecondReferenceScope = 0;
        TestTrue(TEXT("Reference scope opens"), Registry.OpenReferenceScope(ReferenceScope, Result));
        TestTrue(TEXT("Second reference scope opens"), Registry.OpenReferenceScope(SecondReferenceScope, Result));
        TestTrue(TEXT("First explicit UObject lease succeeds"), Registry.RetainReference(ReferenceScope, LeasedHandle, Result));
        TestTrue(TEXT("Repeated UObject lease succeeds"), Registry.RetainReference(ReferenceScope, LeasedHandle, Result));
        TestTrue(TEXT("Concurrent generation lease succeeds"), Registry.RetainReference(SecondReferenceScope, LeasedHandle, Result));
        TestTrue(TEXT("One lease releases independently"), Registry.ReleaseReference(ReferenceScope, LeasedHandle, Result));
        TestTrue(TEXT("Leased handle remains resolvable"), Registry.ResolveObject(LeasedHandle, Result) == Leased);
        TestTrue(TEXT("Closing first scope preserves concurrent lease"), Registry.CloseReferenceScope(ReferenceScope, Result));
        TestTrue(TEXT("Concurrent scope keeps handle resolvable"), Registry.ResolveObject(LeasedHandle, Result) == Leased);
        TestTrue(TEXT("Closing second scope releases its lease"), Registry.CloseReferenceScope(SecondReferenceScope, Result));
        TestTrue(TEXT("Registration reference remains after scope close"), Registry.ResolveObject(LeasedHandle, Result) == Leased);
        TestTrue(TEXT("Registration reference releases after scope close"), Registry.ReleaseObject(LeasedHandle, Result));

        UFinareComponent* Invalidated = NewObject<UFinareComponent>(World);
        FFinareObjectHandle InvalidatedHandle = Registry.RegisterObject(Invalidated, Result);
        uint64 InvalidatedScope = 0;
        TestTrue(TEXT("InvalidateAll lease scope opens"), Registry.OpenReferenceScope(InvalidatedScope, Result));
        TestTrue(
            TEXT("InvalidateAll lease is retained"),
            Registry.RetainReference(InvalidatedScope, InvalidatedHandle, Result));
        TestTrue(TEXT("InvalidateAll succeeds"), Registry.InvalidateAll(Result));
        TestTrue(
            TEXT("Release after InvalidateAll is idempotent"),
            Registry.ReleaseReference(InvalidatedScope, InvalidatedHandle, Result));
        TestTrue(TEXT("InvalidateAll release reports success"), Result.IsSuccess());
        TestTrue(
            TEXT("Close after InvalidateAll is idempotent"),
            Registry.CloseReferenceScope(InvalidatedScope, Result));
        TestTrue(TEXT("InvalidateAll scope close reports success"), Result.IsSuccess());
    }

    World->RemoveFromRoot();
    OtherWorld->RemoveFromRoot();
    World->DestroyWorld(false);
    OtherWorld->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareUObjectReferenceTransportAbiTest,
    "Finare.Unreal.ObjectRegistry.UObjectReferenceTransport",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareUObjectReferenceTransportAbiTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Fixture world exists"), World)) return false;
    World->AddToRoot();
    FFinareObjectRegistry Registry(World);
    FFinareInvocationBridge Bridge(Registry);
    FFinareNativeBridgeContext* Context = new FFinareNativeBridgeContext(Bridge);
    const FFinareUObjectReferenceTransportV1* Table = Finare_GetUObjectReferenceTransportV1();
    TestEqual(TEXT("UObject reference ABI version"), Table->AbiVersion, uint32(1));
    TestEqual(TEXT("UObject reference table size"), Table->TableSize, uint32(sizeof(FFinareUObjectReferenceTransportV1)));
    TestTrue(TEXT("UObject reference table has all entry points"),
        Table->OpenScope != nullptr && Table->CloseScope != nullptr &&
        Table->Retain != nullptr && Table->Release != nullptr);
    TestTrue(TEXT("UObject reference table shares context guard"),
        Table->RetainContext == Finare_GetNativeTransportV1()->RetainContext);
    TestTrue(TEXT("UObject reference context retained"), Table->RetainContext(Context) != 0);

    FFinareObjectHandleResult Result;
    UFinareComponent* Component = NewObject<UFinareComponent>(World);
    FFinareObjectHandle Handle = Registry.RegisterObject(Component, Result);
    uint64 Scope = 0;
    TestEqual(TEXT("Open scope status"), Table->OpenScope(Context, &Scope), EFinareUObjectReferenceStatus::Success);
    TestEqual(TEXT("Retain status"), Table->Retain(Context, Scope, Handle.Value), EFinareUObjectReferenceStatus::Success);
    TestTrue(TEXT("Release registration reference while lease is live"), Registry.ReleaseObject(Handle, Result));
    TestTrue(TEXT("Lease keeps UObject handle alive"), Registry.ResolveObject(Handle, Result) == Component);
    TestEqual(TEXT("Close scope status"), Table->CloseScope(Context, Scope), EFinareUObjectReferenceStatus::Success);
    TestNull(TEXT("Closed lease no longer keeps handle alive"), Registry.ResolveObject(Handle, Result));
    TestEqual(TEXT("Stale release is idempotent"), Table->Release(Context, Scope, Handle.Value), EFinareUObjectReferenceStatus::InvalidScope);
    Table->ReleaseContext(Context);
    Context->Release();
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

#endif

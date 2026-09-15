#if WITH_DEV_AUTOMATION_TESTS
#include "FinareInteropParityFixture.h"
#include "FinareWorldSubsystem.h"
#include "FinareNativeTransport.h"
#include "FinareInvocationBridge.h"
#include "FinareInteropParityThunks.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Async/Async.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareInteropParityManagedTest, "Finare.Unreal.InteropParity.Managed",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FFinareInteropParityManagedTest::RunTest(const FString& Parameters)
{
    using namespace UE::Finare::Parity;
    using namespace UE::Finare::InteropParityGenerated;
    FFinareGeneratedThunkTable Table{&DescribeReadResult, &DescribeInvokeResultCount, &DescribeInvokeResult,
        &ReadProperty, &WriteProperty, &Invoke, &InvokeInt32x2, &InvokeFixed};
    EFinareValueKind Kind; uint64 TypeId = 0;
    const bool bRegister = !FFinareGeneratedThunkRegistry::DescribeReadResult(
        FinareMemberId("/Script/FinareRuntime.FinareInteropParityFixture:Result"), Kind, TypeId);
    if (bRegister) TestTrue(TEXT("Register parity thunks"), FFinareGeneratedThunkRegistry::RegisterTable(TEXT("InteropParityTest"), Table));
    ON_SCOPE_EXIT { if (bRegister) FFinareGeneratedThunkRegistry::UnregisterTable(TEXT("InteropParityTest")); };
    const int32 Initial = FBuffer::Live;
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    World->AddToRoot();
    ON_SCOPE_EXIT { World->RemoveFromRoot(); World->DestroyWorld(false); FBuffer::Saved = {}; };
    auto* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    auto* Fixture = NewObject<UFinareInteropParityFixture>(World);
    auto* Target = NewObject<UFinareInteropFixture>(World);
    Fixture->Bytes = {0, 0}; Fixture->ReadOnlyNumbers = {5};
    TScriptInterface<IFinareInteropContract> Contract;
    Contract.SetObject(Target); Contract.SetInterface(Target);
    Fixture->Targets.Add(Contract);
    FFinareParityItem Item; Item.Value = 11; Item.Target = Contract;
    Fixture->Group.Items.Add(Item);
    FFinareObjectHandleResult Result;
    const auto Handle = Host->RegisterObject(Fixture, Result);
    const FString Assembly = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir() /
        TEXT("../artifacts/interop-parity/fixture-build/bin/InteropParity/debug/Finare.InteropParity.dll"));
    FString Error;
    const FString Type = TEXT("Finare.InteropParity.ParityScript");
    const bool Started = Host->CreateManagedScript(7050, Handle, Handle, Assembly, Type, Error);
    TestTrue(*FString::Printf(TEXT("C# interop: %s"), *Error), Started);
    if (!Started) return false;
    TestEqual(TEXT("C# reached end of interop scenario"), Fixture->Result, 1234);
    TestEqual(TEXT("C# directly mutated native array"), Fixture->Numbers[0], 42);
    TestEqual(TEXT("Byte storage"), Fixture->Bytes[0], uint8(0xAB));
    TestEqual(TEXT("Read-only storage untouched"), Fixture->ReadOnlyNumbers[0], 5);
    TestEqual(TEXT("Struct element write visible in native UE"), Fixture->Group.Items[1].Value, 73);
    TestEqual(TEXT("Native owner retained"), FBuffer::Live, Initial + 1);
    TestEqual(TEXT("Ordinary C++ callbacks reject worker execution"),
        Async(EAsyncExecution::ThreadPool, [] { return FBuffer::Saved(5); }).Get(), 0);
    TestTrue(*Error, Host->TickManagedScript(7050, 0.016f, Error));
    auto PreviousCallback = FBuffer::Saved;
    int32 Generation = 0;
    // Reset only scenario inputs; old script generation remains active until publication.
    Fixture->Group.Items.SetNum(1);
    TestTrue(*Error, Host->ReloadManagedScript(7050, Assembly, Type, Generation, Error));
    TestEqual(TEXT("Old callback expires on reload"), PreviousCallback(5), 0);
    TestEqual(TEXT("Reload leaves one owner"), FBuffer::Live, Initial + 1);
    TestTrue(*Error, Host->TickManagedScript(7050, 0.016f, Error));
    auto Retired = FBuffer::Saved;
    TestTrue(TEXT("Destroy generation"), Host->DestroyManagedScript(7050, Error));
    TestEqual(TEXT("Retained C++ callable cannot enter destroyed C#"), Retired(5), 0);
    TestEqual(TEXT("Native owners destroyed exactly once"), FBuffer::Live, Initial);
    return !HasAnyErrors();
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareInteropParityMemoryTest, "Finare.Unreal.InteropParity.MemoryIdentity",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FFinareInteropParityMemoryTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false); World->AddToRoot();
    ON_SCOPE_EXIT { World->RemoveFromRoot(); World->DestroyWorld(false); };
    FFinareObjectRegistry Registry(World); FFinareInvocationBridge Bridge(Registry);
    auto* Fixture = NewObject<UFinareInteropParityFixture>(World); Fixture->Numbers = {1, 2};
    FFinareObjectHandleResult Result; const auto Handle = Registry.RegisterObject(Fixture, Result);
    struct FProbe { int32* Expected; bool Same = false; bool WorkerBlocked = false; } Probe{Fixture->Numbers.GetData()};
    FString Error;
    TestTrue(TEXT("Borrow"), Bridge.BorrowNativeData(Handle, TEXT("/Script/FinareRuntime.FinareInteropParityFixture:Numbers"), 2, 4, true,
        [](void* User, void* Data, int32 Count) { auto& P = *static_cast<FProbe*>(User); P.Same = Data == P.Expected && Count == 2;
            P.WorkerBlocked = Async(EAsyncExecution::ThreadPool, [] { return Finare_IsNativeMemoryBorrowed(); }).Get();
            static_cast<int32*>(Data)[0] = 99; }, &Probe, Error));
    TestTrue(TEXT("Callback sees exact TArray allocation, without staging or copyback"), Probe.Same);
    TestTrue(TEXT("Worker dispatch observes active borrow before queueing"), Probe.WorkerBlocked);
    TestEqual(TEXT("Native storage modified"), Fixture->Numbers[0], 99);
    TestFalse(TEXT("Reject incompatible layout"), Bridge.BorrowNativeData(Handle, TEXT("/Script/FinareRuntime.FinareInteropParityFixture:Numbers"), 4, 8, true,
        [](void*, void*, int32) {}, nullptr, Error));
    return !HasAnyErrors();
}
#endif

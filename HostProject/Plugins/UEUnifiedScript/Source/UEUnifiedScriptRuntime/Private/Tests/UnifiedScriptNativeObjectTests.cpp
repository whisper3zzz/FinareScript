#if WITH_DEV_AUTOMATION_TESTS

#include "Async/Async.h"
#include "Engine/World.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "UnifiedScriptNativeExtensionFixture.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptWorldSubsystem.h"

namespace UE::UnifiedScript::NativeObjectTests
{
using EStatus = EUnifiedScriptNativeObjectStatus;
constexpr uint64 OwnerType = UnifiedScriptMemberId("/Native/Tests.Counter");
constexpr uint64 ChildType = UnifiedScriptMemberId("/Native/Tests.CounterChild");
constexpr uint64 ConstructId = UnifiedScriptMemberId("/Native/Tests.Counter:Construct(int32)");
constexpr uint64 ChildId = UnifiedScriptMemberId("/Native/Tests.Counter:Child()");
constexpr uint64 AddId = UnifiedScriptMemberId("/Native/Tests.CounterChild:Add(int32)");
constexpr uint64 FailedConstructId = UnifiedScriptMemberId("/Native/Tests.Counter:FailedConstruct()");
int32 LiveCount = 0;
int32 DestroyCount = 0;
struct FChild { int32 Value = 0; };
struct FCounter
{
    explicit FCounter(int32 Value) : Child{Value} { ++LiveCount; }
    ~FCounter() { --LiveCount; ++DestroyCount; }
    FChild Child;
};
void Destroy(void* Pointer) { delete static_cast<FCounter*>(Pointer); }
FUnifiedScriptNativeValueV3 Integer(int32 Value)
{
    FUnifiedScriptNativeValueV3 Result;
    Result.Kind = EUnifiedScriptNativeValueKindV3::Int32;
    Result.Payload0 = static_cast<uint32>(Value);
    return Result;
}
EStatus Construct(FUnifiedScriptNativeObjectRegistry& Registry, uint64 Scope,
    FUnifiedScriptNativeObjectHandle Target, TConstArrayView<FUnifiedScriptNativeValueV3> Arguments,
    FUnifiedScriptNativeValueV3&, FUnifiedScriptNativeObjectHandle& OutHandle)
{
    if (Target.Value || Arguments.Num() != 1 || Arguments[0].Kind != EUnifiedScriptNativeValueKindV3::Int32)
        return EStatus::InvalidArgument;
    auto Object = MakeUnique<FCounter>(static_cast<int32>(Arguments[0].Payload0));
    const EStatus Status = Registry.RegisterOwned(Scope, OwnerType, Object.Get(), &Destroy, OutHandle);
    if (Status == EStatus::Success) static_cast<void>(Object.Release());
    return Status;
}
EStatus Child(FUnifiedScriptNativeObjectRegistry& Registry, uint64 Scope,
    FUnifiedScriptNativeObjectHandle Target, TConstArrayView<FUnifiedScriptNativeValueV3> Arguments,
    FUnifiedScriptNativeValueV3&, FUnifiedScriptNativeObjectHandle& OutHandle)
{
    if (!Arguments.IsEmpty()) return EStatus::InvalidArgument;
    void* Pointer = nullptr;
    auto Status = Registry.Resolve(Scope, Target, OwnerType, Pointer);
    if (Status != EStatus::Success) return Status;
    return Registry.RegisterBorrowed(Scope, ChildType, &static_cast<FCounter*>(Pointer)->Child, Target, OutHandle);
}
EStatus Add(FUnifiedScriptNativeObjectRegistry& Registry, uint64 Scope,
    FUnifiedScriptNativeObjectHandle Target, TConstArrayView<FUnifiedScriptNativeValueV3> Arguments,
    FUnifiedScriptNativeValueV3& OutValue, FUnifiedScriptNativeObjectHandle&)
{
    if (Arguments.Num() != 1 || Arguments[0].Kind != EUnifiedScriptNativeValueKindV3::Int32)
        return EStatus::InvalidArgument;
    void* Pointer = nullptr;
    auto Status = Registry.Resolve(Scope, Target, ChildType, Pointer);
    if (Status != EStatus::Success) return Status;
    auto* Value = static_cast<FChild*>(Pointer);
    Value->Value += static_cast<int32>(Arguments[0].Payload0);
    OutValue = Integer(Value->Value);
    return EStatus::Success;
}
EStatus FailedConstruct(FUnifiedScriptNativeObjectRegistry& Registry, uint64 Scope,
    FUnifiedScriptNativeObjectHandle Target, TConstArrayView<FUnifiedScriptNativeValueV3>,
    FUnifiedScriptNativeValueV3& OutValue, FUnifiedScriptNativeObjectHandle& OutHandle)
{
    const FUnifiedScriptNativeValueV3 Argument = Integer(99);
    const auto Status = Construct(Registry, Scope, Target, MakeArrayView(&Argument, 1), OutValue, OutHandle);
    return Status == EStatus::Success ? EStatus::InvalidArgument : Status;
}
void Register(FUnifiedScriptNativeObjectRegistry& Registry)
{
    check(Registry.RegisterMember(ConstructId, &Construct) == EStatus::Success);
    check(Registry.RegisterMember(ChildId, &Child) == EStatus::Success);
    check(Registry.RegisterMember(AddId, &Add) == EStatus::Success);
    check(Registry.RegisterMember(FailedConstructId, &FailedConstruct) == EStatus::Success);
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FUnifiedScriptNativeObjectRegistryTest,
    "UnifiedScript.Unreal.NativeObjects.Ownership", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FUnifiedScriptNativeObjectRegistryTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::NativeObjectTests;
    const int32 InitialLive = LiveCount;
    const int32 InitialDestroyed = DestroyCount;
    FUnifiedScriptNativeObjectRegistry Registry, OtherWorld;
    Register(Registry);
    uint64 Scope = 0, OtherScope = 0, ForeignScope = 0;
    TestTrue(TEXT("Open generation"), Registry.OpenScope(Scope) == EStatus::Success);
    Registry.OpenScope(OtherScope);
    OtherWorld.OpenScope(ForeignScope);
    FUnifiedScriptNativeValueV3 Value;
    FUnifiedScriptNativeObjectHandle Owner, Borrowed, Nested, Unused;
    const FUnifiedScriptNativeValueV3 Argument = Integer(10);
    const auto Arguments = MakeArrayView(&Argument, 1);
    TestTrue(TEXT("Native construction"), Registry.Invoke(Scope, {}, ConstructId, Arguments, Value, Owner) == EStatus::Success);
    TestTrue(TEXT("Borrow native child"), Registry.Invoke(Scope, Owner, ChildId, {}, Value, Borrowed) == EStatus::Success);
    void* Pointer = nullptr;
    Registry.Resolve(Scope, Borrowed, ChildType, Pointer);
    TestTrue(TEXT("Nested borrow"), Registry.RegisterBorrowed(Scope, ChildType, Pointer, Borrowed, Nested) == EStatus::Success);
    TestTrue(TEXT("Instance mutation"), Registry.Invoke(Scope, Borrowed, AddId, Arguments, Value, Unused) == EStatus::Success);
    TestEqual(TEXT("Mutation result"), Value.Payload0, uint64(20));
    TestTrue(TEXT("Wrong type rejected"), Registry.Resolve(Scope, Borrowed, OwnerType, Pointer) == EStatus::WrongType);
    TestTrue(TEXT("Cross-generation rejected"), Registry.Resolve(OtherScope, Borrowed, ChildType, Pointer) == EStatus::WrongOwner);
    TestTrue(TEXT("Cross-World rejected"), OtherWorld.Resolve(ForeignScope, Borrowed, ChildType, Pointer) == EStatus::InvalidHandle);
    TestTrue(TEXT("Foreign scope rejected"), OtherWorld.Resolve(Scope, Borrowed, ChildType, Pointer) == EStatus::InvalidScope);
    TestTrue(TEXT("Wrong owner cannot release"), Registry.Release(OtherScope, Owner) == EStatus::WrongOwner);
    Registry.Resolve(Scope, Owner, OwnerType, Pointer);
    TestTrue(TEXT("Double deleting ownership rejected"), Registry.RegisterOwned(Scope, OwnerType, Pointer, &Destroy, Unused) == EStatus::WrongOwner);
    const int32 BeforeInvalid = Registry.Num();
    TestTrue(TEXT("Duplicate member rejected"), Registry.RegisterMember(AddId, &Add) == EStatus::DuplicateMember);
    TestTrue(TEXT("Failed factory rejected"), Registry.Invoke(Scope, {}, FailedConstructId, {}, Value, Unused) == EStatus::InvalidArgument);
    TestEqual(TEXT("Failed factory publishes no handle"), Unused.Value, uint64(0));
    TestEqual(TEXT("Failed factory rolls back allocation"), Registry.Num(), BeforeInvalid);
    TestEqual(TEXT("Failed allocation deleted"), DestroyCount, InitialDestroyed + 1);
    TestTrue(TEXT("Worker release rejected without mutation"), Async(EAsyncExecution::ThreadPool,
        [&] { return Registry.Release(Scope, Owner); }).Get() == EStatus::WrongThread);
    TestTrue(TEXT("Borrow release"), Registry.Release(Scope, Borrowed) == EStatus::Success);
    TestEqual(TEXT("Borrow release does not delete parent"), LiveCount, InitialLive + 1);
    TestTrue(TEXT("Nested alias retired"), Registry.Resolve(Scope, Nested, ChildType, Pointer) == EStatus::InvalidHandle);
    Registry.Invoke(Scope, Owner, ChildId, {}, Value, Borrowed);
    Registry.InvalidateBorrowed(Scope, Owner);
    TestTrue(TEXT("Native mutation can invalidate child storage"), Registry.Resolve(Scope, Borrowed, ChildType, Pointer) == EStatus::InvalidHandle);
    Registry.Invoke(Scope, Owner, ChildId, {}, Value, Borrowed);
    TestTrue(TEXT("Owner release"), Registry.Release(Scope, Owner) == EStatus::Success);
    TestTrue(TEXT("Alias dies with owner"), Registry.Resolve(Scope, Borrowed, ChildType, Pointer) == EStatus::InvalidHandle);
    TestTrue(TEXT("Double release rejected"), Registry.Release(Scope, Owner) == EStatus::InvalidHandle);
    FUnifiedScriptNativeObjectHandle Replacement;
    Registry.Invoke(Scope, {}, ConstructId, Arguments, Value, Replacement);
    TestNotEqual(TEXT("Old identity never reused"), Replacement.Value, Owner.Value);
    Registry.CloseScope(Scope);
    TestTrue(TEXT("Generation cannot be reopened by a call"), Registry.Invoke(Scope, {}, ConstructId, Arguments, Value, Unused) == EStatus::InvalidScope);
    Registry.Invoke(OtherScope, {}, ConstructId, Arguments, Value, Owner);
    Registry.Resolve(OtherScope, Owner, OwnerType, Pointer);
    auto* ChildPointer = &static_cast<FCounter*>(Pointer)->Child;
    FUnifiedScriptNativeObjectHandle Tail = Owner;
    for (int32 Index = 0; Index < 10000; ++Index)
    {
        FUnifiedScriptNativeObjectHandle Next;
        if (!TestTrue(TEXT("Deep borrow chain registration"),
            Registry.RegisterBorrowed(OtherScope, ChildType, ChildPointer, Tail, Next) == EStatus::Success)) break;
        Tail = Next;
    }
    TestEqual(TEXT("Deep chain owns only one native allocation"), LiveCount, InitialLive + 1);
    Registry.InvalidateBorrowed(OtherScope, Owner);
    TestEqual(TEXT("Iterative invalidation retires deep chain"), Registry.Num(), 1);
    for (int32 Index = 0; Index < 1000; ++Index)
        Registry.Invoke(OtherScope, {}, ConstructId, Arguments, Value, Unused);
    TestEqual(TEXT("Independent owned objects registered"), Registry.Num(), 1001);
    Registry.Detach();
    TestEqual(TEXT("World releases all native objects"), Registry.Num(), 0);
    TestEqual(TEXT("No live owned objects remain"), LiveCount, InitialLive);
    TestTrue(TEXT("Detached World refuses scopes"), Registry.OpenScope(Scope) == EStatus::Unavailable);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FUnifiedScriptNativeObjectAbiTest,
    "UnifiedScript.Unreal.NativeObjects.TransportValidation", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FUnifiedScriptNativeObjectAbiTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::NativeObjectTests;
    const int32 InitialLive = LiveCount;
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("ABI World"), World)) return false;
    {
        FUnifiedScriptObjectRegistry Objects(World);
        FUnifiedScriptInvocationBridge Bridge(Objects);
        auto* Context = new FUnifiedScriptNativeBridgeContext(Bridge);
        const auto* Table = UEUnifiedScript_GetNativeObjectTransportV1();
        TestEqual(TEXT("Optional table uses same lifetime guard"), Table->RetainContext, UEUnifiedScript_GetNativeTransportV1()->RetainContext);
        TestTrue(TEXT("Retain native context"), Table->RetainContext(Context) != 0);
        Context->ExecuteNativeObjects([](auto& Registry) { Register(Registry); });
        uint64 Scope = 0, Handle = 0;
        Table->OpenScope(Context, &Scope);
        FUnifiedScriptNativeValueV3 Value = Integer(123), Argument = Integer(7);
        TestTrue(TEXT("Null argument pointer rejected"),
            Table->Invoke(Context, Scope, 0, ConstructId, nullptr, 1, &Value, &Handle) == EStatus::InvalidArgument);
        TestEqual(TEXT("No partial handle"), Handle, uint64(0));
        TestTrue(TEXT("No partial value"), Value.Kind == EUnifiedScriptNativeValueKindV3::Void);
        Argument.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        TestTrue(TEXT("Arena values rejected in fixed lane"),
            Table->Invoke(Context, Scope, 0, ConstructId, &Argument, 1, &Value, &Handle) == EStatus::InvalidArgument);
        Argument = Integer(7);
        TestTrue(TEXT("Oversize input rejected before reading cells"),
            Table->Invoke(Context, Scope, 0, ConstructId, &Argument, 17, &Value, &Handle) == EStatus::InvalidArgument);
        TestEqual(TEXT("Invalid ABI calls did not construct"), LiveCount, InitialLive);
        TestTrue(TEXT("Valid construction crosses table"),
            Table->Invoke(Context, Scope, 0, ConstructId, &Argument, 1, &Value, &Handle) == EStatus::Success);
        TestTrue(TEXT("Wrong-thread dispatch rejected"), Async(EAsyncExecution::ThreadPool, [&]
        {
            FUnifiedScriptNativeValueV3 WorkerValue;
            uint64 WorkerHandle = 0;
            return Table->Invoke(Context, Scope, Handle, ChildId, nullptr, 0, &WorkerValue, &WorkerHandle);
        }).Get() == EStatus::WrongThread);
        Context->Detach();
        TestEqual(TEXT("Detach destroys native ownership before last context release"), LiveCount, InitialLive);
        TestTrue(TEXT("Retained detached table refuses old handles"),
            Table->Invoke(Context, Scope, Handle, ChildId, nullptr, 0, &Value, &Handle) == EStatus::Unavailable);
        Table->ReleaseContext(Context);
        Context->Release();
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FUnifiedScriptNativeObjectManagedTest,
    "UnifiedScript.Unreal.NativeObjects.ManagedLifetime", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FUnifiedScriptNativeObjectManagedTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::NativeObjectTests;
    const int32 InitialLive = LiveCount;
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("World"), World)) return false;
    auto* Host = World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    auto Plugin = IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (TestNotNull(TEXT("Host"), Host) && TestTrue(TEXT("Plugin"), Plugin.IsValid()))
    {
        auto* Context = Host->GetNativeInvocationContext();
        if (TestNotNull(TEXT("Native context"), Context))
        {
            Context->ExecuteNativeObjects([](auto& Registry) { Register(Registry); });
            auto* Target = NewObject<UUnifiedScriptNativeExtensionFixture>(World);
            FUnifiedScriptObjectHandleResult Result;
            const auto Handle = Host->RegisterObject(Target, Result);
            const FString Assembly = FPaths::Combine(Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll"));
            const FString Type = TEXT("UnifiedScript.Unreal.Demo.NativeObjectLifetimeDemoScript");
            const FString FailureType = TEXT("UnifiedScript.Unreal.Demo.NativeObjectStartFailureDemoScript");
            FString Error;
            TestFalse(TEXT("Failure after native allocation rejects script creation"),
                Host->CreateManagedScript(7010, Handle, Handle, Assembly, FailureType, Error));
            TestTrue(TEXT("Startup failure reached managed OnStart"), Error.Contains(TEXT("expected startup failure")));
            TestEqual(TEXT("Failed start releases allocated native objects"), LiveCount, InitialLive);
            bool Started = Host->CreateManagedScript(7011, Handle, Handle, Assembly, Type, Error);
            TestTrue(*FString::Printf(TEXT("Managed construction and borrow: %s"), *Error), Started);
            if (Started)
            {
                TestEqual(TEXT("One owned object, child does not own"), LiveCount, InitialLive + 1);
                TestTrue(TEXT("Managed instance call"), Host->TickManagedScript(7011, 0.016f, Error));
                int32 Generation = 0;
                TestFalse(TEXT("Failed candidate rolls back"), Host->ReloadManagedScript(7011, Assembly, FailureType, Generation, Error));
                TestTrue(TEXT("Candidate failure reached managed OnStart"), Error.Contains(TEXT("expected startup failure")));
                TestEqual(TEXT("Only previous native owner survives rollback"), LiveCount, InitialLive + 1);
                TestTrue(TEXT("Retained generation native handle still works"), Host->TickManagedScript(7011, 0.016f, Error));
                TestTrue(TEXT("Replace generation"), Host->ReloadManagedScript(7011, Assembly, Type, Generation, Error));
                TestEqual(TEXT("Old generation cleaned despite missing Dispose"), LiveCount, InitialLive + 1);
                TestTrue(TEXT("Destroy managed script"), Host->DestroyManagedScript(7011, Error));
                TestEqual(TEXT("Script destroy cleans native ownership"), LiveCount, InitialLive);
                TestTrue(TEXT("Second script for World teardown"), Host->CreateManagedScript(7012, Handle, Handle, Assembly, Type, Error));
            }
        }
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    TestEqual(TEXT("World teardown cleans forgotten ownership"), LiveCount, InitialLive);
    return true;
}
#endif

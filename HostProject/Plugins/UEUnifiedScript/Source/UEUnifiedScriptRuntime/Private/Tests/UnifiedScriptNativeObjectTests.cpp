#if WITH_DEV_AUTOMATION_TESTS

#include "Async/Async.h"
#include "Engine/World.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "UnifiedScriptNativeExtensionFixture.h"
#include "UnifiedScriptNativeCounter.h"
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

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FUnifiedScriptNativeObjectLibraryTest,
    "UnifiedScript.Unreal.NativeObjects.GeneratedLibrary", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FUnifiedScriptNativeObjectLibraryTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::NativeObjectTests;
    const int32 InitialLive = LiveCount;
    FUnifiedScriptNativeObjectRegistry Existing, Future, LocalCollision;
    uint64 Scope = 0, FutureScope = 0, CollisionScope = 0;
    Existing.OpenScope(Scope);
    LocalCollision.OpenScope(CollisionScope);
    LocalCollision.RegisterMember(ConstructId, &Construct);
    FUnifiedScriptNativeValueV3 Value;
    FUnifiedScriptNativeObjectHandle Owner, Borrowed, FutureOwner, ManualOwner, ManualBorrowed, Unused;
    const auto Argument = Integer(12);
    const auto Arguments = MakeArrayView(&Argument, 1);
    const FUnifiedScriptNativeObjectMemberEntry Entries[] = {{ConstructId, &Construct}, {ChildId, &Child}, {AddId, &Add}};
    TestTrue(TEXT("Not registered before module load"), Existing.Invoke(Scope, {}, ConstructId, Arguments, Value, Unused) == EStatus::UnknownMember);
    auto Manual = MakeUnique<FCounter>(4);
    TestTrue(TEXT("Independent native owner"), Existing.RegisterOwned(Scope, OwnerType, Manual.Get(), &Destroy, ManualOwner) == EStatus::Success);
    static_cast<void>(Manual.Release());
    {
        FUnifiedScriptNativeObjectLibrary Library(MakeArrayView(Entries));
        TestTrue(TEXT("Existing World sees late registration"), Existing.Invoke(Scope, {}, ConstructId, Arguments, Value, Owner) == EStatus::Success);
        TestTrue(TEXT("Generated borrowed return"), Existing.Invoke(Scope, Owner, ChildId, {}, Value, Borrowed) == EStatus::Success);
        TestTrue(TEXT("Library-owned alias on independent owner"), Existing.Invoke(Scope, ManualOwner, ChildId, {}, Value, ManualBorrowed) == EStatus::Success);
        TestTrue(TEXT("Explicit registration cannot overwrite generated member"), Existing.RegisterMember(AddId, &Add) == EStatus::DuplicateMember);
        TestTrue(TEXT("Earlier explicit registration cannot silently shadow library"), LocalCollision.Invoke(CollisionScope, {}, ConstructId, Arguments, Value, Unused) == EStatus::DuplicateMember);
        Future.OpenScope(FutureScope);
        TestTrue(TEXT("New generation sees registration"), Future.Invoke(FutureScope, {}, ConstructId, Arguments, Value, FutureOwner) == EStatus::Success);
        {
            FUnifiedScriptNativeObjectRegistry NewWorld;
            uint64 NewScope = 0;
            NewWorld.OpenScope(NewScope);
            TestTrue(TEXT("New World sees installed library"), NewWorld.Invoke(NewScope, {}, ConstructId, Arguments, Value, Unused) == EStatus::Success);
        }
        const int32 BeforeDuplicate = LiveCount;
        {
            FUnifiedScriptNativeObjectLibrary Duplicate(MakeArrayView(Entries));
            TestTrue(TEXT("Conflicting libraries fail closed"), Existing.Invoke(Scope, {}, ConstructId, Arguments, Value, Unused) == EStatus::DuplicateMember);
            TestEqual(TEXT("Collision has no constructor side effects"), LiveCount, BeforeDuplicate);
        }
        TestTrue(TEXT("Removing conflicting library restores original member"), Existing.Invoke(Scope, Borrowed, AddId, Arguments, Value, Unused) == EStatus::Success);
        TestEqual(TEXT("Original object survives collision"), Value.Payload0, uint64(24));
    }
    void* Pointer = nullptr;
    TestEqual(TEXT("Module retirement deletes only its owned allocations"), LiveCount, InitialLive + 1);
    TestTrue(TEXT("Retired member unavailable"), Existing.Invoke(Scope, {}, ConstructId, Arguments, Value, Unused) == EStatus::UnknownMember);
    TestTrue(TEXT("Owned handle retired"), Existing.Resolve(Scope, Owner, OwnerType, Pointer) == EStatus::InvalidHandle);
    TestTrue(TEXT("Borrowed handle retired"), Existing.Resolve(Scope, Borrowed, ChildType, Pointer) == EStatus::InvalidHandle);
    TestTrue(TEXT("Other generation owner retired"), Future.Resolve(FutureScope, FutureOwner, OwnerType, Pointer) == EStatus::InvalidHandle);
    TestTrue(TEXT("Library alias retired even when its parent is independent"), Existing.Resolve(Scope, ManualBorrowed, ChildType, Pointer) == EStatus::InvalidHandle);
    TestTrue(TEXT("Independent parent survives library retirement"), Existing.Resolve(Scope, ManualOwner, OwnerType, Pointer) == EStatus::Success);
    TestEqual(TEXT("Generation scope remains open"), Existing.NumScopes(), 1);
    TestTrue(TEXT("Local member becomes callable after collision removed"), LocalCollision.Invoke(CollisionScope, {}, ConstructId, Arguments, Value, Unused) == EStatus::Success);
    LocalCollision.CloseScope(CollisionScope);
    Existing.CloseScope(Scope);
    TestEqual(TEXT("No native leak"), LiveCount, InitialLive);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FUnifiedScriptNativeObjectArgumentsTest,
    "UnifiedScript.Unreal.NativeObjects.ObjectArguments", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FUnifiedScriptNativeObjectArgumentsTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::NativeObjectTests;
    using FExample = UE::UnifiedScript::NativeExample::FCounter;
    constexpr uint64 CounterType = UnifiedScriptMemberId("/Native/UEUnifiedScriptRuntime.NativeCounter");
    constexpr uint64 CounterChildType = UnifiedScriptMemberId("/Native/UEUnifiedScriptRuntime.NativeCounterChild");
    constexpr uint64 Create = UnifiedScriptMemberId("/Native/UEUnifiedScriptRuntime.NativeCounter:Create(int32)");
    constexpr uint64 GetChild = UnifiedScriptMemberId("/Native/UEUnifiedScriptRuntime.NativeCounter:GetChild()");
    constexpr uint64 CreateFrom = UnifiedScriptMemberId("/Native/UEUnifiedScriptRuntime.NativeCounter:Create(UE::UnifiedScript::NativeExample::FCounterChildconst&)");
    constexpr uint64 Read = UnifiedScriptMemberId("/Native/UEUnifiedScriptRuntime.NativeCounter:ReadPointer(UE::UnifiedScript::NativeExample::FCounterChild*)");
    constexpr uint64 ReadConst = UnifiedScriptMemberId("/Native/UEUnifiedScriptRuntime.NativeCounter:ReadConstPointer(UE::UnifiedScript::NativeExample::FCounterChildconst*)");
    constexpr uint64 Optional = UnifiedScriptMemberId("/Native/UEUnifiedScriptRuntime.NativeCounter:ReadOptional(UE::UnifiedScript::NativeExample::FCounterChild*)");
    constexpr uint64 OptionalConst = UnifiedScriptMemberId("/Native/UEUnifiedScriptRuntime.NativeCounter:ReadOptionalConst(UE::UnifiedScript::NativeExample::FCounterChildconst*)");
    constexpr uint64 AddFrom = UnifiedScriptMemberId("/Native/UEUnifiedScriptRuntime.NativeCounter:AddFrom(UE::UnifiedScript::NativeExample::FCounterChildconst&)");
    constexpr uint64 AddTo = UnifiedScriptMemberId("/Native/UEUnifiedScriptRuntime.NativeCounter:AddTo(UE::UnifiedScript::NativeExample::FCounterChild&,int32)");
    constexpr uint64 Sum = UnifiedScriptMemberId("/Native/UEUnifiedScriptRuntime.NativeCounter:Sum(UE::UnifiedScript::NativeExample::FCounterChildconst&,UE::UnifiedScript::NativeExample::FCounterChildconst&)");
    const int32 InitialLive = FExample::LiveCount;
    FUnifiedScriptNativeObjectRegistry Registry, OtherWorld;
    uint64 Scope = 0, OtherScope = 0, ForeignScope = 0;
    Registry.OpenScope(Scope);
    Registry.OpenScope(OtherScope);
    OtherWorld.OpenScope(ForeignScope);
    FUnifiedScriptNativeValueV3 Result;
    FUnifiedScriptNativeObjectHandle Output;
    auto Construct = [&](FUnifiedScriptNativeObjectRegistry& InRegistry, uint64 InScope, int32 Initial,
        FUnifiedScriptNativeObjectHandle& Owner, FUnifiedScriptNativeObjectHandle& Child)
    {
        const auto Argument = Integer(Initial);
        return TestTrue(TEXT("Generated constructor"), InRegistry.Invoke(InScope, {}, Create, MakeArrayView(&Argument, 1), Result, Owner) == EStatus::Success) &&
            TestTrue(TEXT("Generated borrow"), InRegistry.Invoke(InScope, Owner, GetChild, {}, Result, Child) == EStatus::Success);
    };
    auto ObjectCell = [&](FUnifiedScriptNativeObjectHandle Handle, uint64 Type = 0)
    {
        FUnifiedScriptNativeValueV3 Cell;
        Cell.Kind = EUnifiedScriptNativeValueKindV3::NativeObject;
        Cell.TypeId = Type ? Type : CounterChildType;
        Cell.Payload0 = Handle.Value;
        Cell.Flags = Handle.Value ? EUnifiedScriptNativeValueFlagsV3::None : EUnifiedScriptNativeValueFlagsV3::Null;
        return Cell;
    };
    FUnifiedScriptNativeObjectHandle Owner, Child, Donor, DonorChild, OtherOwner, OtherChild, ForeignOwner, ForeignChild;
    if (!Construct(Registry, Scope, 10, Owner, Child) || !Construct(Registry, Scope, 7, Donor, DonorChild) ||
        !Construct(Registry, OtherScope, 5, OtherOwner, OtherChild) || !Construct(OtherWorld, ForeignScope, 4, ForeignOwner, ForeignChild)) return false;
    const auto Argument = ObjectCell(DonorChild);
    for (uint64 Member : {Read, ReadConst, Optional, OptionalConst})
    {
        TestTrue(TEXT("Generated object pointer call"), Registry.Invoke(Scope, Owner, Member, MakeArrayView(&Argument, 1), Result, Output) == EStatus::Success);
        TestEqual(TEXT("Pointer reads live storage"), Result.Payload0, uint64(7));
    }
    const auto Null = ObjectCell({});
    for (uint64 Member : {Optional, OptionalConst})
    {
        TestTrue(TEXT("Explicit nullable pointer"), Registry.Invoke(Scope, Owner, Member, MakeArrayView(&Null, 1), Result, Output) == EStatus::Success);
        TestEqual(TEXT("Native sees null"), static_cast<int32>(Result.Payload0), -1);
    }
    FUnifiedScriptNativeValueV3 Arguments[] = {Argument, Integer(3)};
    TestTrue(TEXT("Mutable object reference"), Registry.Invoke(Scope, {}, AddTo, MakeArrayView(Arguments), Result, Output) == EStatus::Success);
    Registry.Invoke(Scope, Owner, Read, MakeArrayView(&Argument, 1), Result, Output);
    TestEqual(TEXT("Reference writes original storage"), Result.Payload0, uint64(10));
    const auto Alias = ObjectCell(Child);
    TestTrue(TEXT("Const reference preserves alias"), Registry.Invoke(Scope, Owner, AddFrom, MakeArrayView(&Alias, 1), Result, Output) == EStatus::Success);
    TestEqual(TEXT("Aliased input is not a detached copy"), Result.Payload0, uint64(20));
    Arguments[0] = Alias;
    Arguments[1] = Argument;
    TestTrue(TEXT("Multiple object inputs"), Registry.Invoke(Scope, {}, Sum, MakeArrayView(Arguments), Result, Output) == EStatus::Success);
    TestEqual(TEXT("Both live values used"), Result.Payload0, uint64(30));
    FUnifiedScriptNativeObjectHandle Copy;
    TestTrue(TEXT("Object-input constructor"), Registry.Invoke(Scope, {}, CreateFrom, MakeArrayView(&Argument, 1), Result, Copy) == EStatus::Success);
    void* Pointer = nullptr;
    Registry.Resolve(Scope, Copy, CounterType, Pointer);
    if (TestNotNull(TEXT("Constructed copy"), Pointer)) TestEqual(TEXT("Constructor receives reference"), static_cast<FExample*>(Pointer)->Scale(2), 20);

    const int32 BeforeRejected = FExample::ArgumentCalls;
    const int32 BeforeLive = FExample::LiveCount;
    auto Reject = [&](const FUnifiedScriptNativeValueV3& Cell, EStatus Expected)
    {
        TestTrue(TEXT("Invalid object input rejected"), Registry.Invoke(Scope, Owner, Read, MakeArrayView(&Cell, 1), Result, Output) == Expected);
        TestEqual(TEXT("No rejected output handle"), Output.Value, uint64(0));
        TestTrue(TEXT("No rejected output value"), Result.Kind == EUnifiedScriptNativeValueKindV3::Void);
    };
    Reject(Null, EStatus::InvalidArgument);
    Reject(ObjectCell(Owner, CounterType), EStatus::WrongType);
    Reject(ObjectCell(Owner), EStatus::WrongType);
    Reject(ObjectCell(OtherChild), EStatus::WrongOwner);
    Reject(ObjectCell(ForeignChild), EStatus::InvalidHandle);
    for (int32 Variant = 0; Variant < 10; ++Variant)
    {
        auto Bad = Argument;
        switch (Variant)
        {
        case 0: Bad.Reserved16 = 1; break;
        case 1: Bad.Auxiliary = 1; break;
        case 2: Bad.Payload1 = 1; break;
        case 3: Bad.Payload2 = 1; break;
        case 4: Bad.Payload3 = 1; break;
        case 5: Bad.TypeId = 0; break;
        case 6: Bad.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked; break;
        case 7: Bad.Flags = EUnifiedScriptNativeValueFlagsV3::Null; break;
        case 8: Bad.Payload0 = 0; break;
        case 9: Bad.Kind = EUnifiedScriptNativeValueKindV3::Object; break;
        }
        Reject(Bad, EStatus::InvalidArgument);
    }
    TestTrue(TEXT("Null reference constructor rejected"), Registry.Invoke(Scope, {}, CreateFrom, MakeArrayView(&Null, 1), Result, Output) == EStatus::InvalidArgument);
    TestTrue(TEXT("Null const reference rejected"), Registry.Invoke(Scope, Owner, AddFrom, MakeArrayView(&Null, 1), Result, Output) == EStatus::InvalidArgument);
    Arguments[0] = Null;
    Arguments[1] = Integer(3);
    TestTrue(TEXT("Null mutable reference rejected"), Registry.Invoke(Scope, {}, AddTo, MakeArrayView(Arguments), Result, Output) == EStatus::InvalidArgument);
    Arguments[0] = Argument;
    Arguments[1].Kind = EUnifiedScriptNativeValueKindV3::Boolean;
    Arguments[1].Payload0 = 1;
    TestTrue(TEXT("Later fixed argument validated before reference mutation"), Registry.Invoke(Scope, {}, AddTo, MakeArrayView(Arguments), Result, Output) == EStatus::InvalidArgument);
    Arguments[1] = ObjectCell(OtherChild);
    TestTrue(TEXT("Later object cannot cross generations"), Registry.Invoke(Scope, {}, Sum, MakeArrayView(Arguments), Result, Output) == EStatus::WrongOwner);
    TestEqual(TEXT("Rejected calls have no native side effects"), FExample::ArgumentCalls, BeforeRejected);
    TestEqual(TEXT("Rejected constructor allocates nothing"), FExample::LiveCount, BeforeLive);
    Registry.Resolve(Scope, Donor, CounterType, Pointer);
    if (TestNotNull(TEXT("Donor still live"), Pointer)) TestEqual(TEXT("Rejected writes preserve donor"), static_cast<FExample*>(Pointer)->Scale(1), 10);
    Registry.Release(Scope, Donor);
    Reject(Argument, EStatus::InvalidHandle);
    Registry.CloseScope(OtherScope);
    Reject(ObjectCell(OtherChild), EStatus::InvalidHandle);
    Registry.CloseScope(Scope);
    TestTrue(TEXT("Closed generation refuses input"), Registry.Invoke(Scope, {}, CreateFrom, MakeArrayView(&Alias, 1), Result, Output) == EStatus::InvalidScope);
    OtherWorld.Detach();
    TestEqual(TEXT("All generated owners released"), FExample::LiveCount, InitialLive);
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
        TestEqual(TEXT("Object table advertises optional tail"), Table->TableSize, uint32(sizeof(FUnifiedScriptNativeObjectTransportWithCapabilities)));
        TestEqual(TEXT("Object input capability"), reinterpret_cast<const FUnifiedScriptNativeObjectTransportWithCapabilities*>(Table)->Capabilities & 1, uint64(1));
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
    const int32& GeneratedLive = UE::UnifiedScript::NativeExample::FCounter::LiveCount;
    const int32 InitialLive = GeneratedLive;
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("World"), World)) return false;
    auto* Host = World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    auto Plugin = IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (TestNotNull(TEXT("Host"), Host) && TestTrue(TEXT("Plugin"), Plugin.IsValid()))
    {
        auto* Context = Host->GetNativeInvocationContext();
        if (TestNotNull(TEXT("Native context"), Context))
        {
            // No fixture registration: the generated native library is module-owned.
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
            TestEqual(TEXT("Failed start releases allocated native objects"), GeneratedLive, InitialLive);
            bool Started = Host->CreateManagedScript(7011, Handle, Handle, Assembly, Type, Error);
            TestTrue(*FString::Printf(TEXT("Managed construction and borrow: %s"), *Error), Started);
            if (Started)
            {
                TestEqual(TEXT("One owned object, child does not own"), GeneratedLive, InitialLive + 1);
                TestTrue(TEXT("Managed instance call"), Host->TickManagedScript(7011, 0.016f, Error));
                int32 Generation = 0;
                TestFalse(TEXT("Failed candidate rolls back"), Host->ReloadManagedScript(7011, Assembly, FailureType, Generation, Error));
                TestTrue(TEXT("Candidate failure reached managed OnStart"), Error.Contains(TEXT("expected startup failure")));
                TestEqual(TEXT("Only previous native owner survives rollback"), GeneratedLive, InitialLive + 1);
                TestTrue(TEXT("Retained generation native handle still works"), Host->TickManagedScript(7011, 0.016f, Error));
                TestTrue(TEXT("Replace generation"), Host->ReloadManagedScript(7011, Assembly, Type, Generation, Error));
                TestEqual(TEXT("Old generation cleaned despite missing Dispose"), GeneratedLive, InitialLive + 1);
                TestTrue(TEXT("Destroy managed script"), Host->DestroyManagedScript(7011, Error));
                TestEqual(TEXT("Script destroy cleans native ownership"), GeneratedLive, InitialLive);
                TestTrue(TEXT("Second script for World teardown"), Host->CreateManagedScript(7012, Handle, Handle, Assembly, Type, Error));
            }
        }
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    TestEqual(TEXT("World teardown cleans forgotten ownership"), GeneratedLive, InitialLive);
    return true;
}
#endif

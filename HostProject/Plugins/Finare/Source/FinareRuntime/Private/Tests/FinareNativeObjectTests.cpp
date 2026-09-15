#if WITH_DEV_AUTOMATION_TESTS

#include "Async/Async.h"
#include "Engine/World.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "FinareNativeExtensionFixture.h"
#include "FinareNativeCounter.h"
#include "FinareNativeObjectValues.h"
#include "FinareNativeTransport.h"
#include "FinareWorldSubsystem.h"

namespace UE::Finare::NativeObjectTests
{
	using EStatus = EFinareNativeObjectStatus;
	constexpr uint64 OwnerType = FinareMemberId("/Native/Tests.Counter");
	constexpr uint64 ChildType = FinareMemberId("/Native/Tests.CounterChild");
	constexpr uint64 ConstructId = FinareMemberId("/Native/Tests.Counter:Construct(int32)");
	constexpr uint64 ChildId = FinareMemberId("/Native/Tests.Counter:Child()");
	constexpr uint64 AddId = FinareMemberId("/Native/Tests.CounterChild:Add(int32)");
	constexpr uint64 FailedConstructId = FinareMemberId("/Native/Tests.Counter:FailedConstruct()");
	constexpr uint64 TextEchoId = FinareMemberId("/Native/Tests.Text:Echo(FStringconst&)");
	constexpr uint64 TextDetachId = FinareMemberId("/Native/Tests.Text:Detach(FStringconst&)");
	constexpr uint64 TextFactoryId = FinareMemberId("/Native/Tests.Text:Factory(FStringconst&)");
	constexpr uint64 TextOuterReadFailId = FinareMemberId("/Native/Tests.Text:OuterReadFail(FStringconst&)");
	constexpr uint64 TextOuterUnreadFailId = FinareMemberId("/Native/Tests.Text:OuterUnreadFail(FStringconst&)");
	constexpr uint64 TextRetireScalarId = FinareMemberId("/Native/Tests.Text:RetireScalar(FStringconst&)");
	constexpr uint64 TextRetireFactoryId = FinareMemberId("/Native/Tests.Text:RetireFactory(FStringconst&)");
	constexpr uint64 TextCapacityId = FinareMemberId("/Native/Tests.Text:Capacity(FStringconst&)");
	int32 TextEchoCalls = 0;
	int32 LiveCount = 0;
	int32 DestroyCount = 0;

	struct FChild
	{
		int32 Value = 0;
	};

	struct FCounter
	{
		explicit FCounter(int32 Value) : Child{Value} { ++LiveCount; }

		~FCounter()
		{
			--LiveCount;
			++DestroyCount;
		}

		FChild Child;
	};

	void Destroy(void* Pointer) { delete static_cast<FCounter*>(Pointer); }

	FFinareNativeValueV3 Integer(int32 Value)
	{
		FFinareNativeValueV3 Result;
		Result.Kind = EFinareNativeValueKindV3::Int32;
		Result.Payload0 = static_cast<uint32>(Value);
		return Result;
	}

	EStatus Construct(FFinareNativeObjectRegistry& Registry, uint64 Scope,
	                  FFinareNativeObjectHandle Target, TConstArrayView<FFinareNativeValueV3> Arguments,
	                  FFinareNativeValueV3&, FFinareNativeObjectHandle& OutHandle)
	{
		if (Target.Value || Arguments.Num() != 1 || Arguments[0].Kind != EFinareNativeValueKindV3::Int32)
		{
			return EStatus::InvalidArgument;
		}
		auto Object = MakeUnique<FCounter>(static_cast<int32>(Arguments[0].Payload0));
		const EStatus Status = Registry.RegisterOwned(Scope, OwnerType, Object.Get(), &Destroy, OutHandle);
		if (Status == EStatus::Success)
		{
			static_cast<void>(Object.Release());
		}
		return Status;
	}

	EStatus Child(FFinareNativeObjectRegistry& Registry, uint64 Scope,
	              FFinareNativeObjectHandle Target, TConstArrayView<FFinareNativeValueV3> Arguments,
	              FFinareNativeValueV3&, FFinareNativeObjectHandle& OutHandle)
	{
		if (!Arguments.IsEmpty())
		{
			return EStatus::InvalidArgument;
		}
		void* Pointer = nullptr;
		auto Status = Registry.Resolve(Scope, Target, OwnerType, Pointer);
		if (Status != EStatus::Success)
		{
			return Status;
		}
		return Registry.RegisterBorrowed(Scope, ChildType, &static_cast<FCounter*>(Pointer)->Child, Target, OutHandle);
	}

	EStatus Add(FFinareNativeObjectRegistry& Registry, uint64 Scope,
	            FFinareNativeObjectHandle Target, TConstArrayView<FFinareNativeValueV3> Arguments,
	            FFinareNativeValueV3& OutValue, FFinareNativeObjectHandle&)
	{
		if (Arguments.Num() != 1 || Arguments[0].Kind != EFinareNativeValueKindV3::Int32)
		{
			return EStatus::InvalidArgument;
		}
		void* Pointer = nullptr;
		auto Status = Registry.Resolve(Scope, Target, ChildType, Pointer);
		if (Status != EStatus::Success)
		{
			return Status;
		}
		auto* Value = static_cast<FChild*>(Pointer);
		Value->Value += static_cast<int32>(Arguments[0].Payload0);
		OutValue = Integer(Value->Value);
		return EStatus::Success;
	}

	EStatus FailedConstruct(FFinareNativeObjectRegistry& Registry, uint64 Scope,
	                        FFinareNativeObjectHandle Target, TConstArrayView<FFinareNativeValueV3>,
	                        FFinareNativeValueV3& OutValue, FFinareNativeObjectHandle& OutHandle)
	{
		const FFinareNativeValueV3 Argument = Integer(99);
		const auto Status = Construct(Registry, Scope, Target, MakeArrayView(&Argument, 1), OutValue, OutHandle);
		return Status == EStatus::Success ? EStatus::InvalidArgument : Status;
	}

	void Register(FFinareNativeObjectRegistry& Registry)
	{
		check(Registry.RegisterMember(ConstructId, &Construct) == EStatus::Success);
		check(Registry.RegisterMember(ChildId, &Child) == EStatus::Success);
		check(Registry.RegisterMember(AddId, &Add) == EStatus::Success);
		check(Registry.RegisterMember(FailedConstructId, &FailedConstruct) == EStatus::Success);
	}

	EStatus EchoText(FFinareNativeObjectRegistry&, uint64 Scope, FFinareNativeObjectHandle Target,
	                 TConstArrayView<FFinareNativeValueV3> Arguments, TConstArrayView<uint8> Input,
	                 FFinareNativeValueV3& OutValue, FFinareNativeObjectHandle&, TArray<uint8>& OutBytes)
	{
		if (Scope == 0 || Target.Value || Arguments.Num() != 1)
		{
			return EStatus::InvalidArgument;
		}
		++TextEchoCalls;
		FString Text;
		const EStatus Status = UE::Finare::NativeObjectValues::ReadText(Input, Arguments[0], Text);
		if (Status != EStatus::Success)
		{
			return Status;
		}
		return UE::Finare::NativeObjectValues::WriteText(Text, OutBytes, OutValue)
			       ? EStatus::Success
			       : EStatus::InvalidArgument;
	}

	EStatus DetachText(FFinareNativeObjectRegistry& Registry, uint64, FFinareNativeObjectHandle,
	                   TConstArrayView<FFinareNativeValueV3>, TConstArrayView<uint8>, FFinareNativeValueV3&,
	                   FFinareNativeObjectHandle&, TArray<uint8>&)
	{
		Registry.Detach();
		return EStatus::Success;
	}

	EStatus TextFactory(FFinareNativeObjectRegistry& Registry, uint64 Scope, FFinareNativeObjectHandle Target,
	                    TConstArrayView<FFinareNativeValueV3> Arguments, TConstArrayView<uint8> Input,
	                    FFinareNativeValueV3& OutValue, FFinareNativeObjectHandle& OutHandle, TArray<uint8>& OutBytes)
	{
		if (Target.Value || !Arguments.IsEmpty() || !Input.IsEmpty())
		{
			return EStatus::InvalidArgument;
		}
		auto Object = MakeUnique<FCounter>(123);
		const EStatus Status = Registry.RegisterOwned(Scope, OwnerType, Object.Get(), &Destroy, OutHandle);
		if (Status != EStatus::Success)
		{
			return Status;
		}
		static_cast<void>(Object.Release());
		return UE::Finare::NativeObjectValues::WriteText(FString(TEXT("owned")), OutBytes, OutValue)
			       ? EStatus::Success
			       : EStatus::InvalidArgument;
	}

	EStatus NestedReadThenFail(FFinareNativeObjectRegistry& Registry, uint64 Scope, FFinareNativeObjectHandle,
	                           TConstArrayView<FFinareNativeValueV3> Arguments, TConstArrayView<uint8> Input,
	                           FFinareNativeValueV3&, FFinareNativeObjectHandle&, TArray<uint8>&)
	{
		if (!Arguments.IsEmpty() || !Input.IsEmpty())
		{
			return EStatus::InvalidArgument;
		}
		uint64 Token = 0;
		uint32 Required = 0;
		EStatus Status = Registry.BeginText(Scope, {}, TextFactoryId, {}, {}, Token, Required);
		if (Status != EStatus::Success)
		{
			return Status;
		}
		TArray<uint8> Bytes;
		Bytes.SetNumZeroed(Required);
		FFinareNativeValueV3 Value;
		FFinareNativeObjectHandle Handle;
		uint32 Reported = 0;
		Status = Registry.ReadText(Scope, Token, Bytes.GetData(), Bytes.Num(), Reported, Value, Handle);
		return Status == EStatus::Success ? EStatus::InvalidArgument : Status;
	}

	EStatus NestedBeginThenFail(FFinareNativeObjectRegistry& Registry, uint64 Scope, FFinareNativeObjectHandle,
	                            TConstArrayView<FFinareNativeValueV3> Arguments, TConstArrayView<uint8> Input,
	                            FFinareNativeValueV3&, FFinareNativeObjectHandle&, TArray<uint8>&)
	{
		if (!Arguments.IsEmpty() || !Input.IsEmpty())
		{
			return EStatus::InvalidArgument;
		}
		uint64 Token = 0;
		uint32 Required = 0;
		const EStatus Status = Registry.BeginText(Scope, {}, TextFactoryId, {}, {}, Token, Required);
		return Status == EStatus::Success ? EStatus::InvalidArgument : Status;
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareNativeObjectRegistryTest,
                                 "Finare.Unreal.NativeObjects.Ownership",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeObjectRegistryTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::NativeObjectTests;
	TextEchoCalls = 0;
	const int32 InitialLive = LiveCount;
	const int32 InitialDestroyed = DestroyCount;
	FFinareNativeObjectRegistry Registry, OtherWorld;
	Register(Registry);
	uint64 Scope = 0, OtherScope = 0, ForeignScope = 0;
	TestTrue(TEXT("Open generation"), Registry.OpenScope(Scope) == EStatus::Success);
	Registry.OpenScope(OtherScope);
	OtherWorld.OpenScope(ForeignScope);
	FFinareNativeValueV3 Value;
	FFinareNativeObjectHandle Owner, Borrowed, Nested, Unused;
	const FFinareNativeValueV3 Argument = Integer(10);
	const auto Arguments = MakeArrayView(&Argument, 1);
	TestTrue(
		TEXT("Native construction"),
		Registry.Invoke(Scope, {}, ConstructId, Arguments, Value, Owner) == EStatus::Success);
	TestTrue(
		TEXT("Borrow native child"), Registry.Invoke(Scope, Owner, ChildId, {}, Value, Borrowed) == EStatus::Success);
	void* Pointer = nullptr;
	Registry.Resolve(Scope, Borrowed, ChildType, Pointer);
	TestTrue(
		TEXT("Nested borrow"),
		Registry.RegisterBorrowed(Scope, ChildType, Pointer, Borrowed, Nested) == EStatus::Success);
	TestTrue(
		TEXT("Instance mutation"),
		Registry.Invoke(Scope, Borrowed, AddId, Arguments, Value, Unused) == EStatus::Success);
	TestEqual(TEXT("Mutation result"), Value.Payload0, uint64(20));
	TestTrue(TEXT("Wrong type rejected"), Registry.Resolve(Scope, Borrowed, OwnerType, Pointer) == EStatus::WrongType);
	TestTrue(
		TEXT("Cross-generation rejected"),
		Registry.Resolve(OtherScope, Borrowed, ChildType, Pointer) == EStatus::WrongOwner);
	TestTrue(
		TEXT("Cross-World rejected"),
		OtherWorld.Resolve(ForeignScope, Borrowed, ChildType, Pointer) == EStatus::InvalidHandle);
	TestTrue(
		TEXT("Foreign scope rejected"),
		OtherWorld.Resolve(Scope, Borrowed, ChildType, Pointer) == EStatus::InvalidScope);
	TestTrue(TEXT("Wrong owner cannot release"), Registry.Release(OtherScope, Owner) == EStatus::WrongOwner);
	Registry.Resolve(Scope, Owner, OwnerType, Pointer);
	TestTrue(
		TEXT("Double deleting ownership rejected"),
		Registry.RegisterOwned(Scope, OwnerType, Pointer, &Destroy, Unused) == EStatus::WrongOwner);
	const int32 BeforeInvalid = Registry.Num();
	TestTrue(TEXT("Duplicate member rejected"), Registry.RegisterMember(AddId, &Add) == EStatus::DuplicateMember);
	TestTrue(
		TEXT("Failed factory rejected"),
		Registry.Invoke(Scope, {}, FailedConstructId, {}, Value, Unused) == EStatus::InvalidArgument);
	TestEqual(TEXT("Failed factory publishes no handle"), Unused.Value, uint64(0));
	TestEqual(TEXT("Failed factory rolls back allocation"), Registry.Num(), BeforeInvalid);
	TestEqual(TEXT("Failed allocation deleted"), DestroyCount, InitialDestroyed + 1);
	TestTrue(TEXT("Worker release rejected without mutation"), Async(EAsyncExecution::ThreadPool,
	                                                                 [&]
	                                                                 {
		                                                                 return Registry.Release(Scope, Owner);
	                                                                 }).Get() == EStatus::WrongThread);
	TestTrue(TEXT("Borrow release"), Registry.Release(Scope, Borrowed) == EStatus::Success);
	TestEqual(TEXT("Borrow release does not delete parent"), LiveCount, InitialLive + 1);
	TestTrue(
		TEXT("Nested alias retired"), Registry.Resolve(Scope, Nested, ChildType, Pointer) == EStatus::InvalidHandle);
	Registry.Invoke(Scope, Owner, ChildId, {}, Value, Borrowed);
	Registry.InvalidateBorrowed(Scope, Owner);
	TestTrue(
		TEXT("Native mutation can invalidate child storage"),
		Registry.Resolve(Scope, Borrowed, ChildType, Pointer) == EStatus::InvalidHandle);
	Registry.Invoke(Scope, Owner, ChildId, {}, Value, Borrowed);
	TestTrue(TEXT("Owner release"), Registry.Release(Scope, Owner) == EStatus::Success);
	TestTrue(
		TEXT("Alias dies with owner"), Registry.Resolve(Scope, Borrowed, ChildType, Pointer) == EStatus::InvalidHandle);
	TestTrue(TEXT("Double release rejected"), Registry.Release(Scope, Owner) == EStatus::InvalidHandle);
	FFinareNativeObjectHandle Replacement;
	Registry.Invoke(Scope, {}, ConstructId, Arguments, Value, Replacement);
	TestNotEqual(TEXT("Old identity never reused"), Replacement.Value, Owner.Value);
	Registry.CloseScope(Scope);
	TestTrue(
		TEXT("Generation cannot be reopened by a call"),
		Registry.Invoke(Scope, {}, ConstructId, Arguments, Value, Unused) == EStatus::InvalidScope);
	Registry.Invoke(OtherScope, {}, ConstructId, Arguments, Value, Owner);
	Registry.Resolve(OtherScope, Owner, OwnerType, Pointer);
	auto* ChildPointer = &static_cast<FCounter*>(Pointer)->Child;
	FFinareNativeObjectHandle Tail = Owner;
	for (int32 Index = 0; Index < 10000; ++Index)
	{
		FFinareNativeObjectHandle Next;
		if (!TestTrue(TEXT("Deep borrow chain registration"),
		              Registry.RegisterBorrowed(OtherScope, ChildType, ChildPointer, Tail, Next) == EStatus::Success))
		{
			break;
		}
		Tail = Next;
	}
	TestEqual(TEXT("Deep chain owns only one native allocation"), LiveCount, InitialLive + 1);
	Registry.InvalidateBorrowed(OtherScope, Owner);
	TestEqual(TEXT("Iterative invalidation retires deep chain"), Registry.Num(), 1);
	for (int32 Index = 0; Index < 1000; ++Index)
	{
		Registry.Invoke(OtherScope, {}, ConstructId, Arguments, Value, Unused);
	}
	TestEqual(TEXT("Independent owned objects registered"), Registry.Num(), 1001);
	Registry.Detach();
	TestEqual(TEXT("World releases all native objects"), Registry.Num(), 0);
	TestEqual(TEXT("No live owned objects remain"), LiveCount, InitialLive);
	TestTrue(TEXT("Detached World refuses scopes"), Registry.OpenScope(Scope) == EStatus::Unavailable);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareNativeObjectTextTest,
                                 "Finare.Unreal.NativeObjects.TextSnapshots",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeObjectTextTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::NativeObjectTests;
	FFinareNativeObjectRegistry Registry;
	const FFinareNativeObjectMemberEntry Entry{TextEchoId, nullptr, &EchoText};
	FFinareNativeObjectLibrary Library(MakeArrayView(&Entry, 1));
	uint64 Scope = 0;
	TestTrue(TEXT("Open text scope"), Registry.OpenScope(Scope) == EStatus::Success);
	const uint8 Source[] = {'h', 0xC3, 0xA9, 0, 0xE4, 0xB8, 0x96, 0xE7, 0x95, 0x8C};
	TArray<uint8> Input;
	Input.SetNumUninitialized(static_cast<int32>(sizeof(Source)));
	FMemory::Memcpy(Input.GetData(), Source, sizeof(Source));
	FFinareNativeValueV3 Cell;
	Cell.Kind = EFinareNativeValueKindV3::String;
	Cell.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
	Cell.Payload1 = Input.Num();
	uint64 Token = 0;
	uint32 Required = 0;
	TestTrue(TEXT("Begin snapshots without publishing"), Registry.BeginText(Scope, {}, TextEchoId,
	                                                                        MakeArrayView(&Cell, 1),
	                                                                        MakeArrayView(Input.GetData(), Input.Num()),
	                                                                        Token, Required) == EStatus::Success);
	TestEqual(TEXT("Begin invokes text member once"), TextEchoCalls, 1);
	Input[0] = 'X';
	FFinareNativeValueV3 Value;
	FFinareNativeObjectHandle Handle;
	uint8 Small[1] = {};
	uint32 Reported = 0;
	TestTrue(
		TEXT("Insufficient read preserves token"),
		Registry.ReadText(Scope, Token, Small, 1, Reported, Value, Handle) == EStatus::TextBufferTooSmall);
	TArray<uint8> Output;
	Output.SetNumZeroed(Required);
	TestTrue(TEXT("Read publishes once"), Registry.ReadText(Scope, Token, Output.GetData(), Output.Num(), Reported,
	                                                        Value, Handle) == EStatus::Success);
	TestEqual(TEXT("Text output size"), Reported, Required);
	TestTrue(TEXT("Text output bytes"), Output.Num() == sizeof(Source) && FMemory::Memcmp(
		         Output.GetData(), Source, sizeof(Source)) == 0);
	TestEqual(TEXT("Reads do not invoke text member again"), TextEchoCalls, 1);
	TestTrue(TEXT("Duplicate read rejects token"), Registry.ReadText(Scope, Token, Output.GetData(), Output.Num(),
	                                                                 Reported, Value,
	                                                                 Handle) == EStatus::InvalidTextToken);
	TestEqual(TEXT("Duplicate read keeps side effects at one"), TextEchoCalls, 1);
	TestTrue(
		TEXT("Fixed invoke rejects rich-only member"),
		Registry.Invoke(Scope, {}, TextEchoId, {}, Value, Handle) == EStatus::UnknownMember);
	const uint8 Invalid[] = {0xC0, 0x80};
	Cell.Payload1 = UE_ARRAY_COUNT(Invalid);
	Token = 0;
	TestTrue(TEXT("Invalid UTF8 rejected before callback"), Registry.BeginText(Scope, {}, TextEchoId,
	                                                                           MakeArrayView(&Cell, 1),
	                                                                           MakeArrayView(
		                                                                           Invalid, UE_ARRAY_COUNT(Invalid)),
	                                                                           Token, Required) ==
	         EStatus::InvalidArgument);
	Cell.Payload1 = Input.Num();
	uint64 OtherScope = 0;
	TestTrue(TEXT("Open wrong-scope text scope"), Registry.OpenScope(OtherScope) == EStatus::Success);
	uint64 ForeignToken = 0;
	uint32 ForeignRequired = 0;
	TestTrue(TEXT("Begin second pending text"), Registry.BeginText(Scope, {}, TextEchoId, MakeArrayView(&Cell, 1),
	                                                               MakeArrayView(Input.GetData(), Input.Num()),
	                                                               ForeignToken, ForeignRequired) == EStatus::Success);
	TArray<uint8> ForeignOutput;
	ForeignOutput.SetNumZeroed(ForeignRequired);
	FFinareNativeValueV3 ForeignValue;
	FFinareNativeObjectHandle ForeignHandle;
	uint32 ForeignReported = 0;
	TestTrue(TEXT("Wrong-scope read preserves token"), Registry.ReadText(OtherScope, ForeignToken,
	                                                                     ForeignOutput.GetData(),
	                                                                     ForeignOutput.Num(), ForeignReported,
	                                                                     ForeignValue,
	                                                                     ForeignHandle) == EStatus::WrongOwner);
	TestTrue(
		TEXT("Wrong-scope discard preserves token"),
		Registry.DiscardText(OtherScope, ForeignToken) == EStatus::WrongOwner);
	TestTrue(TEXT("Owner can read after wrong-scope attempts"), Registry.ReadText(Scope, ForeignToken,
		         ForeignOutput.GetData(),
		         ForeignOutput.Num(), ForeignReported, ForeignValue, ForeignHandle) == EStatus::Success);
	Registry.CloseScope(OtherScope);
	Registry.CloseScope(Scope);

	FFinareNativeObjectRegistry ReentrantRegistry;
	FFinareNativeObjectMemberEntry DetachEntry{TextDetachId, nullptr, &DetachText};
	FFinareNativeObjectLibrary DetachLibrary(MakeArrayView(&DetachEntry, 1));
	uint64 ReentrantScope = 0;
	TestTrue(TEXT("Open reentrant detach scope"), ReentrantRegistry.OpenScope(ReentrantScope) == EStatus::Success);
	TestTrue(TEXT("Reentrant detach unwinds pending reservation"), ReentrantRegistry.BeginText(
		         ReentrantScope, {}, TextDetachId, {}, {}, Token, Required) == EStatus::InvalidScope);
	TestEqual(TEXT("Reentrant detach closes scopes"), ReentrantRegistry.NumScopes(), 0);

	FFinareNativeObjectRegistry NestedRegistry;
	const FFinareNativeObjectMemberEntry NestedEntries[] = {
		{TextFactoryId, nullptr, &TextFactory},
		{TextOuterReadFailId, nullptr, &NestedReadThenFail},
		{TextOuterUnreadFailId, nullptr, &NestedBeginThenFail},
	};
	FFinareNativeObjectLibrary NestedLibrary(MakeArrayView(NestedEntries));
	uint64 NestedScope = 0;
	TestTrue(TEXT("Open nested rollback scope"), NestedRegistry.OpenScope(NestedScope) == EStatus::Success);
	const int32 NestedInitialLive = LiveCount;
	TestTrue(TEXT("Read nested factory then outer failure rolls back ownership"), NestedRegistry.BeginText(
		         NestedScope, {}, TextOuterReadFailId, {}, {}, Token, Required) == EStatus::InvalidArgument);
	TestEqual(TEXT("Read nested factory leaves no owner"), LiveCount, NestedInitialLive);
	TestTrue(TEXT("Unconsumed nested factory then outer failure rolls back ownership"), NestedRegistry.BeginText(
		         NestedScope, {}, TextOuterUnreadFailId, {}, {}, Token, Required) == EStatus::InvalidArgument);
	TestEqual(TEXT("Unconsumed nested factory leaves no owner"), LiveCount, NestedInitialLive);
	NestedRegistry.CloseScope(NestedScope);

	FFinareNativeObjectRegistry DiscardRegistry, ScopeCloseRegistry;
	const FFinareNativeObjectMemberEntry DiscardEntries[] = {
		{TextRetireScalarId, nullptr, &EchoText},
		{TextRetireFactoryId, nullptr, &TextFactory},
	};
	uint64 DiscardScope = 0;
	uint64 ScopeClose = 0;
	FFinareNativeObjectHandle UnusedHandle;
	uint32 DiscardRequired = 0;
	{
		FFinareNativeObjectLibrary DiscardLibrary(MakeArrayView(DiscardEntries));
		TestTrue(TEXT("Open explicit discard scope"), DiscardRegistry.OpenScope(DiscardScope) == EStatus::Success);
		TestTrue(TEXT("Open scope-close pending scope"), ScopeCloseRegistry.OpenScope(ScopeClose) == EStatus::Success);
		TestTrue(TEXT("Begin unread scope-close scalar result"), ScopeCloseRegistry.BeginText(
			         ScopeClose, {}, TextRetireScalarId,
			         MakeArrayView(&Cell, 1), MakeArrayView(Input.GetData(), Input.Num()), Token,
			         DiscardRequired) == EStatus::Success);
		ScopeCloseRegistry.CloseScope(ScopeClose);
		TestTrue(TEXT("Scope close removes unread scalar token"), ScopeCloseRegistry.ReadText(ScopeClose, Token,
			         nullptr, 0, DiscardRequired, Value, UnusedHandle) == EStatus::InvalidScope);
		const int32 BeforeDiscard = LiveCount;
		TestTrue(TEXT("Begin unpublished factory"), DiscardRegistry.BeginText(DiscardScope, {}, TextRetireFactoryId,
		                                                                      {}, {}, Token,
		                                                                      DiscardRequired) == EStatus::Success);
		TestEqual(TEXT("Factory owner exists before discard"), LiveCount, BeforeDiscard + 1);
		TestTrue(
			TEXT("Discard retires unpublished factory owner"),
			DiscardRegistry.DiscardText(DiscardScope, Token) == EStatus::Success);
		TestEqual(TEXT("Discard removes factory owner"), LiveCount, BeforeDiscard);
		TestTrue(
			TEXT("Duplicate discard rejects retired token"),
			DiscardRegistry.DiscardText(DiscardScope, Token) == EStatus::InvalidTextToken);
		TestTrue(TEXT("Begin unread scalar result"), DiscardRegistry.BeginText(DiscardScope, {}, TextRetireScalarId,
		                                                                       MakeArrayView(&Cell, 1),
		                                                                       MakeArrayView(
			                                                                       Input.GetData(), Input.Num()), Token,
		                                                                       DiscardRequired) == EStatus::Success);
	}
	TestTrue(TEXT("Library retirement removes unread scalar token"), DiscardRegistry.ReadText(DiscardScope, Token,
		         nullptr, 0, DiscardRequired, Value, UnusedHandle) == EStatus::InvalidTextToken);
	DiscardRegistry.CloseScope(DiscardScope);

	FFinareNativeObjectRegistry CapacityRegistry;
	const FFinareNativeObjectMemberEntry CapacityEntry{TextCapacityId, nullptr, &EchoText};
	FFinareNativeObjectLibrary CapacityLibrary(MakeArrayView(&CapacityEntry, 1));
	uint64 CapacityScope = 0;
	TestTrue(TEXT("Open pending capacity scope"), CapacityRegistry.OpenScope(CapacityScope) == EStatus::Success);
	TextEchoCalls = 0;
	TArray<uint64> CapacityTokens;
	for (int32 Index = 0; Index < 64; ++Index)
	{
		uint64 CapacityToken = 0;
		uint32 CapacityRequired = 0;
		TestTrue(TEXT("Fill pending capacity"), CapacityRegistry.BeginText(CapacityScope, {}, TextCapacityId,
		                                                                   MakeArrayView(&Cell, 1),
		                                                                   MakeArrayView(Input.GetData(), Input.Num()),
		                                                                   CapacityToken,
		                                                                   CapacityRequired) == EStatus::Success);
		CapacityTokens.Add(CapacityToken);
	}
	uint64 RecoveredToken = 0;
	uint32 RecoveredRequired = 0;
	TestTrue(TEXT("Pending capacity refuses before callback"), CapacityRegistry.BeginText(
		         CapacityScope, {}, TextCapacityId,
		         MakeArrayView(&Cell, 1), MakeArrayView(Input.GetData(), Input.Num()), RecoveredToken,
		         RecoveredRequired) == EStatus::Exhausted);
	TestEqual(TEXT("Exhausted begin did not invoke callback"), TextEchoCalls, 64);
	TestTrue(
		TEXT("Discard frees pending capacity"),
		CapacityRegistry.DiscardText(CapacityScope, CapacityTokens[0]) == EStatus::Success);
	TestTrue(TEXT("Pending capacity recovers after discard"), CapacityRegistry.BeginText(
		         CapacityScope, {}, TextCapacityId,
		         MakeArrayView(&Cell, 1), MakeArrayView(Input.GetData(), Input.Num()), RecoveredToken,
		         RecoveredRequired) == EStatus::Success);
	CapacityTokens[0] = RecoveredToken;
	for (const uint64 CapacityToken : CapacityTokens)
	{
		CapacityRegistry.DiscardText(CapacityScope, CapacityToken);
	}
	CapacityRegistry.CloseScope(CapacityScope);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareNativeObjectLibraryTest,
                                 "Finare.Unreal.NativeObjects.GeneratedLibrary",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeObjectLibraryTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::NativeObjectTests;
	const int32 InitialLive = LiveCount;
	FFinareNativeObjectRegistry Existing, Future, LocalCollision;
	uint64 Scope = 0, FutureScope = 0, CollisionScope = 0;
	Existing.OpenScope(Scope);
	LocalCollision.OpenScope(CollisionScope);
	LocalCollision.RegisterMember(ConstructId, &Construct);
	FFinareNativeValueV3 Value;
	FFinareNativeObjectHandle Owner, Borrowed, FutureOwner, ManualOwner, ManualBorrowed, Unused;
	const auto Argument = Integer(12);
	const auto Arguments = MakeArrayView(&Argument, 1);
	const FFinareNativeObjectMemberEntry Entries[] = {{ConstructId, &Construct}, {ChildId, &Child}, {AddId, &Add}};
	TestTrue(
		TEXT("Not registered before module load"),
		Existing.Invoke(Scope, {}, ConstructId, Arguments, Value, Unused) == EStatus::UnknownMember);
	auto Manual = MakeUnique<FCounter>(4);
	TestTrue(TEXT("Independent native owner"), Existing.RegisterOwned(Scope, OwnerType, Manual.Get(), &Destroy,
	                                                                  ManualOwner) == EStatus::Success);
	static_cast<void>(Manual.Release());
	{
		FFinareNativeObjectLibrary Library(MakeArrayView(Entries));
		TestTrue(TEXT("Existing World sees late registration"), Existing.Invoke(Scope, {}, ConstructId, Arguments,
			         Value, Owner) == EStatus::Success);
		TestTrue(
			TEXT("Generated borrowed return"),
			Existing.Invoke(Scope, Owner, ChildId, {}, Value, Borrowed) == EStatus::Success);
		TestTrue(TEXT("Library-owned alias on independent owner"), Existing.Invoke(Scope, ManualOwner, ChildId, {},
			         Value, ManualBorrowed) == EStatus::Success);
		TestTrue(
			TEXT("Explicit registration cannot overwrite generated member"),
			Existing.RegisterMember(AddId, &Add) == EStatus::DuplicateMember);
		TestTrue(
			TEXT("Earlier explicit registration cannot silently shadow library"),
			LocalCollision.Invoke(CollisionScope, {}, ConstructId, Arguments, Value, Unused) ==
			EStatus::DuplicateMember);
		Future.OpenScope(FutureScope);
		TestTrue(TEXT("New generation sees registration"), Future.Invoke(FutureScope, {}, ConstructId, Arguments, Value,
		                                                                 FutureOwner) == EStatus::Success);
		{
			FFinareNativeObjectRegistry NewWorld;
			uint64 NewScope = 0;
			NewWorld.OpenScope(NewScope);
			TestTrue(TEXT("New World sees installed library"), NewWorld.Invoke(NewScope, {}, ConstructId, Arguments,
			                                                                   Value, Unused) == EStatus::Success);
		}
		const int32 BeforeDuplicate = LiveCount;
		{
			FFinareNativeObjectLibrary Duplicate(MakeArrayView(Entries));
			TestTrue(
				TEXT("Conflicting libraries fail closed"),
				Existing.Invoke(Scope, {}, ConstructId, Arguments, Value, Unused) == EStatus::DuplicateMember);
			TestEqual(TEXT("Collision has no constructor side effects"), LiveCount, BeforeDuplicate);
		}
		TestTrue(
			TEXT("Removing conflicting library restores original member"),
			Existing.Invoke(Scope, Borrowed, AddId, Arguments, Value, Unused) == EStatus::Success);
		TestEqual(TEXT("Original object survives collision"), Value.Payload0, uint64(24));
	}
	void* Pointer = nullptr;
	TestEqual(TEXT("Module retirement deletes only its owned allocations"), LiveCount, InitialLive + 1);
	TestTrue(
		TEXT("Retired member unavailable"),
		Existing.Invoke(Scope, {}, ConstructId, Arguments, Value, Unused) == EStatus::UnknownMember);
	TestTrue(
		TEXT("Owned handle retired"), Existing.Resolve(Scope, Owner, OwnerType, Pointer) == EStatus::InvalidHandle);
	TestTrue(
		TEXT("Borrowed handle retired"),
		Existing.Resolve(Scope, Borrowed, ChildType, Pointer) == EStatus::InvalidHandle);
	TestTrue(
		TEXT("Other generation owner retired"),
		Future.Resolve(FutureScope, FutureOwner, OwnerType, Pointer) == EStatus::InvalidHandle);
	TestTrue(
		TEXT("Library alias retired even when its parent is independent"),
		Existing.Resolve(Scope, ManualBorrowed, ChildType, Pointer) == EStatus::InvalidHandle);
	TestTrue(
		TEXT("Independent parent survives library retirement"),
		Existing.Resolve(Scope, ManualOwner, OwnerType, Pointer) == EStatus::Success);
	TestEqual(TEXT("Generation scope remains open"), Existing.NumScopes(), 1);
	TestTrue(
		TEXT("Local member becomes callable after collision removed"),
		LocalCollision.Invoke(CollisionScope, {}, ConstructId, Arguments, Value, Unused) == EStatus::Success);
	LocalCollision.CloseScope(CollisionScope);
	Existing.CloseScope(Scope);
	TestEqual(TEXT("No native leak"), LiveCount, InitialLive);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareNativeObjectArgumentsTest,
                                 "Finare.Unreal.NativeObjects.ObjectArguments",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeObjectArgumentsTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::NativeObjectTests;
	using FExample = UE::Finare::NativeExample::FCounter;
	constexpr uint64 CounterType = FinareMemberId("/Native/FinareRuntime.NativeCounter");
	constexpr uint64 CounterChildType = FinareMemberId("/Native/FinareRuntime.NativeCounterChild");
	constexpr uint64 Create = FinareMemberId("/Native/FinareRuntime.NativeCounter:Create(int32)");
	constexpr uint64 GetChild = FinareMemberId("/Native/FinareRuntime.NativeCounter:GetChild()");
	constexpr uint64 CreateFrom = FinareMemberId(
		"/Native/FinareRuntime.NativeCounter:Create(UE::Finare::NativeExample::FCounterChildconst&)");
	constexpr uint64 Read = FinareMemberId(
		"/Native/FinareRuntime.NativeCounter:ReadPointer(UE::Finare::NativeExample::FCounterChild*)");
	constexpr uint64 ReadConst = FinareMemberId(
		"/Native/FinareRuntime.NativeCounter:ReadConstPointer(UE::Finare::NativeExample::FCounterChildconst*)");
	constexpr uint64 Optional = FinareMemberId(
		"/Native/FinareRuntime.NativeCounter:ReadOptional(UE::Finare::NativeExample::FCounterChild*)");
	constexpr uint64 OptionalConst = FinareMemberId(
		"/Native/FinareRuntime.NativeCounter:ReadOptionalConst(UE::Finare::NativeExample::FCounterChildconst*)");
	constexpr uint64 AddFrom = FinareMemberId(
		"/Native/FinareRuntime.NativeCounter:AddFrom(UE::Finare::NativeExample::FCounterChildconst&)");
	constexpr uint64 AddTo = FinareMemberId(
		"/Native/FinareRuntime.NativeCounter:AddTo(UE::Finare::NativeExample::FCounterChild&,int32)");
	constexpr uint64 Sum = FinareMemberId(
		"/Native/FinareRuntime.NativeCounter:Sum(UE::Finare::NativeExample::FCounterChildconst&,UE::Finare::NativeExample::FCounterChildconst&)");
	const int32 InitialLive = FExample::LiveCount;
	FFinareNativeObjectRegistry Registry, OtherWorld;
	uint64 Scope = 0, OtherScope = 0, ForeignScope = 0;
	Registry.OpenScope(Scope);
	Registry.OpenScope(OtherScope);
	OtherWorld.OpenScope(ForeignScope);
	FFinareNativeValueV3 Result;
	FFinareNativeObjectHandle Output;
	auto Construct = [&](FFinareNativeObjectRegistry& InRegistry, uint64 InScope, int32 Initial,
	                     FFinareNativeObjectHandle& Owner, FFinareNativeObjectHandle& Child)
	{
		const auto Argument = Integer(Initial);
		return TestTrue(
				TEXT("Generated constructor"),
				InRegistry.Invoke(InScope, {}, Create, MakeArrayView(&Argument, 1), Result, Owner) == EStatus::Success)
			&&
			TestTrue(
				TEXT("Generated borrow"),
				InRegistry.Invoke(InScope, Owner, GetChild, {}, Result, Child) == EStatus::Success);
	};
	auto ObjectCell = [&](FFinareNativeObjectHandle Handle, uint64 Type = 0)
	{
		FFinareNativeValueV3 Cell;
		Cell.Kind = EFinareNativeValueKindV3::NativeObject;
		Cell.TypeId = Type ? Type : CounterChildType;
		Cell.Payload0 = Handle.Value;
		Cell.Flags = Handle.Value ? EFinareNativeValueFlagsV3::None : EFinareNativeValueFlagsV3::Null;
		return Cell;
	};
	FFinareNativeObjectHandle Owner, Child, Donor, DonorChild, OtherOwner, OtherChild, ForeignOwner, ForeignChild;
	if (!Construct(Registry, Scope, 10, Owner, Child) || !Construct(Registry, Scope, 7, Donor, DonorChild) ||
		!Construct(Registry, OtherScope, 5, OtherOwner, OtherChild) || !Construct(
			OtherWorld, ForeignScope, 4, ForeignOwner, ForeignChild))
	{
		return false;
	}
	const auto Argument = ObjectCell(DonorChild);
	for (uint64 Member : {Read, ReadConst, Optional, OptionalConst})
	{
		TestTrue(TEXT("Generated object pointer call"), Registry.Invoke(Scope, Owner, Member,
		                                                                MakeArrayView(&Argument, 1), Result,
		                                                                Output) == EStatus::Success);
		TestEqual(TEXT("Pointer reads live storage"), Result.Payload0, uint64(7));
	}
	const auto Null = ObjectCell({});
	for (uint64 Member : {Optional, OptionalConst})
	{
		TestTrue(TEXT("Explicit nullable pointer"), Registry.Invoke(Scope, Owner, Member, MakeArrayView(&Null, 1),
		                                                            Result, Output) == EStatus::Success);
		TestEqual(TEXT("Native sees null"), static_cast<int32>(Result.Payload0), -1);
	}
	FFinareNativeValueV3 Arguments[] = {Argument, Integer(3)};
	TestTrue(TEXT("Mutable object reference"), Registry.Invoke(Scope, {}, AddTo, MakeArrayView(Arguments), Result,
	                                                           Output) == EStatus::Success);
	Registry.Invoke(Scope, Owner, Read, MakeArrayView(&Argument, 1), Result, Output);
	TestEqual(TEXT("Reference writes original storage"), Result.Payload0, uint64(10));
	const auto Alias = ObjectCell(Child);
	TestTrue(TEXT("Const reference preserves alias"), Registry.Invoke(Scope, Owner, AddFrom, MakeArrayView(&Alias, 1),
	                                                                  Result, Output) == EStatus::Success);
	TestEqual(TEXT("Aliased input is not a detached copy"), Result.Payload0, uint64(20));
	Arguments[0] = Alias;
	Arguments[1] = Argument;
	TestTrue(
		TEXT("Multiple object inputs"),
		Registry.Invoke(Scope, {}, Sum, MakeArrayView(Arguments), Result, Output) == EStatus::Success);
	TestEqual(TEXT("Both live values used"), Result.Payload0, uint64(30));
	FFinareNativeObjectHandle Copy;
	TestTrue(TEXT("Object-input constructor"), Registry.Invoke(Scope, {}, CreateFrom, MakeArrayView(&Argument, 1),
	                                                           Result, Copy) == EStatus::Success);
	void* Pointer = nullptr;
	Registry.Resolve(Scope, Copy, CounterType, Pointer);
	if (TestNotNull(TEXT("Constructed copy"), Pointer))
	{
		TestEqual(TEXT("Constructor receives reference"), static_cast<FExample*>(Pointer)->Scale(2), 20);
	}

	const int32 BeforeRejected = FExample::ArgumentCalls;
	const int32 BeforeLive = FExample::LiveCount;
	auto Reject = [&](const FFinareNativeValueV3& Cell, EStatus Expected)
	{
		TestTrue(TEXT("Invalid object input rejected"), Registry.Invoke(Scope, Owner, Read, MakeArrayView(&Cell, 1),
		                                                                Result, Output) == Expected);
		TestEqual(TEXT("No rejected output handle"), Output.Value, uint64(0));
		TestTrue(TEXT("No rejected output value"), Result.Kind == EFinareNativeValueKindV3::Void);
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
		case 0: Bad.Reserved16 = 1;
			break;
		case 1: Bad.Auxiliary = 1;
			break;
		case 2: Bad.Payload1 = 1;
			break;
		case 3: Bad.Payload2 = 1;
			break;
		case 4: Bad.Payload3 = 1;
			break;
		case 5: Bad.TypeId = 0;
			break;
		case 6: Bad.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
			break;
		case 7: Bad.Flags = EFinareNativeValueFlagsV3::Null;
			break;
		case 8: Bad.Payload0 = 0;
			break;
		case 9: Bad.Kind = EFinareNativeValueKindV3::Object;
			break;
		}
		Reject(Bad, EStatus::InvalidArgument);
	}
	TestTrue(TEXT("Null reference constructor rejected"), Registry.Invoke(Scope, {}, CreateFrom,
	                                                                      MakeArrayView(&Null, 1), Result,
	                                                                      Output) == EStatus::InvalidArgument);
	TestTrue(TEXT("Null const reference rejected"), Registry.Invoke(Scope, Owner, AddFrom, MakeArrayView(&Null, 1),
	                                                                Result, Output) == EStatus::InvalidArgument);
	Arguments[0] = Null;
	Arguments[1] = Integer(3);
	TestTrue(TEXT("Null mutable reference rejected"), Registry.Invoke(Scope, {}, AddTo, MakeArrayView(Arguments),
	                                                                  Result, Output) == EStatus::InvalidArgument);
	Arguments[0] = Argument;
	Arguments[1].Kind = EFinareNativeValueKindV3::Boolean;
	Arguments[1].Payload0 = 1;
	TestTrue(
		TEXT("Later fixed argument validated before reference mutation"),
		Registry.Invoke(Scope, {}, AddTo, MakeArrayView(Arguments), Result, Output) == EStatus::InvalidArgument);
	Arguments[1] = ObjectCell(OtherChild);
	TestTrue(TEXT("Later object cannot cross generations"), Registry.Invoke(Scope, {}, Sum, MakeArrayView(Arguments),
	                                                                        Result, Output) == EStatus::WrongOwner);
	TestEqual(TEXT("Rejected calls have no native side effects"), FExample::ArgumentCalls, BeforeRejected);
	TestEqual(TEXT("Rejected constructor allocates nothing"), FExample::LiveCount, BeforeLive);
	Registry.Resolve(Scope, Donor, CounterType, Pointer);
	if (TestNotNull(TEXT("Donor still live"), Pointer))
	{
		TestEqual(TEXT("Rejected writes preserve donor"), static_cast<FExample*>(Pointer)->Scale(1), 10);
	}
	Registry.Release(Scope, Donor);
	Reject(Argument, EStatus::InvalidHandle);
	Registry.CloseScope(OtherScope);
	Reject(ObjectCell(OtherChild), EStatus::InvalidHandle);
	Registry.CloseScope(Scope);
	TestTrue(TEXT("Closed generation refuses input"), Registry.Invoke(Scope, {}, CreateFrom, MakeArrayView(&Alias, 1),
	                                                                  Result, Output) == EStatus::InvalidScope);
	OtherWorld.Detach();
	TestEqual(TEXT("All generated owners released"), FExample::LiveCount, InitialLive);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareNativeObjectAbiTest,
                                 "Finare.Unreal.NativeObjects.TransportValidation",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeObjectAbiTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::NativeObjectTests;
	const int32 InitialLive = LiveCount;
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("ABI World"), World))
	{
		return false;
	}
	{
		FFinareObjectRegistry Objects(World);
		FFinareInvocationBridge Bridge(Objects);
		auto* Context = new FFinareNativeBridgeContext(Bridge);
		const auto* Table = Finare_GetNativeObjectTransportV1();
		const auto* Extended = reinterpret_cast<const FFinareNativeObjectTransportWithText*>(Table);
		TestEqual(TEXT("Object table advertises memory tail"), Table->TableSize,
		          uint32(sizeof(FFinareNativeObjectTransportWithMemory)));
		TestEqual(TEXT("Object memory capability"), Extended->Base.Capabilities & 4, uint64(4));
		TestTrue(TEXT("Memory entry point"), reinterpret_cast<const FFinareNativeObjectTransportWithMemory*>(Table)->Borrow != nullptr);
		TestEqual(TEXT("Object input capability"), Extended->Base.Capabilities & 1, uint64(1));
		TestEqual(TEXT("Object text capability"), Extended->Base.Capabilities & 2, uint64(2));
		TestNotNull(TEXT("Text transport tail"), Extended->TextTransport);
		TestEqual(TEXT("Text transport ABI"), Extended->TextTransport->AbiVersion, uint32(1));
		TestEqual(TEXT("Text transport value size"), Extended->TextTransport->ValueSize,
		          uint32(sizeof(FFinareNativeValueV3)));
		TestEqual(TEXT("Optional table uses same lifetime guard"), Table->RetainContext,
		          Finare_GetNativeTransportV1()->RetainContext);
		TestTrue(TEXT("Retain native context"), Table->RetainContext(Context) != 0);
		const FFinareNativeObjectMemberEntry TextEntry{TextEchoId, nullptr, &EchoText};
		FFinareNativeObjectLibrary TextLibrary(MakeArrayView(&TextEntry, 1));
		Context->ExecuteNativeObjects([](auto& Registry) { Register(Registry); });
		uint64 Scope = 0, Handle = 0;
		Table->OpenScope(Context, &Scope);
		FFinareNativeValueV3 Value = Integer(123), Argument = Integer(7);
		TestTrue(TEXT("Null argument pointer rejected"),
		         Table->Invoke(Context, Scope, 0, ConstructId, nullptr, 1, &Value,
		                       &Handle) == EStatus::InvalidArgument);
		TestEqual(TEXT("No partial handle"), Handle, uint64(0));
		TestTrue(TEXT("No partial value"), Value.Kind == EFinareNativeValueKindV3::Void);
		Argument.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
		TestTrue(TEXT("Arena values rejected in fixed lane"),
		         Table->Invoke(Context, Scope, 0, ConstructId, &Argument, 1, &Value, &Handle) ==
		         EStatus::InvalidArgument);
		Argument = Integer(7);
		TestTrue(TEXT("Oversize input rejected before reading cells"),
		         Table->Invoke(Context, Scope, 0, ConstructId, &Argument, 17, &Value, &Handle) ==
		         EStatus::InvalidArgument);
		TestEqual(TEXT("Invalid ABI calls did not construct"), LiveCount, InitialLive);
		TestTrue(TEXT("Valid construction crosses table"),
		         Table->Invoke(Context, Scope, 0, ConstructId, &Argument, 1, &Value, &Handle) == EStatus::Success);
		const uint8 TextInput[] = {'o', 'k'};
		FFinareNativeValueV3 TextArgument;
		TextArgument.Kind = EFinareNativeValueKindV3::String;
		TextArgument.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
		TextArgument.Payload1 = UE_ARRAY_COUNT(TextInput);
		uint64 TextToken = 0;
		uint32 TextRequired = 0;
		TestTrue(TEXT("Exported text tail begins rich call"), Extended->TextTransport->Begin(Context, Scope, 0,
			         TextEchoId, &TextArgument, 1, TextInput, UE_ARRAY_COUNT(TextInput), &TextToken,
			         &TextRequired) == EStatus::Success);
		TArray<uint8> TextOutput;
		TextOutput.SetNumZeroed(TextRequired);
		FFinareNativeValueV3 TextValue;
		uint64 TextHandle = 0;
		uint32 TextReported = 0;
		TestTrue(TEXT("Exported text tail reads rich result"), Extended->TextTransport->Read(Context, Scope, TextToken,
			         TextOutput.GetData(), TextOutput.Num(), &TextReported, &TextValue,
			         &TextHandle) == EStatus::Success);
		TestTrue(TEXT("Exported text tail preserves bytes"), TextOutput.Num() == UE_ARRAY_COUNT(TextInput) &&
		         FMemory::Memcmp(TextOutput.GetData(), TextInput, UE_ARRAY_COUNT(TextInput)) == 0);
		TestTrue(TEXT("Wrong-thread dispatch rejected"), Async(EAsyncExecution::ThreadPool, [&]
		{
			FFinareNativeValueV3 WorkerValue;
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

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareNativeObjectManagedTest,
                                 "Finare.Unreal.NativeObjects.ManagedLifetime",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeObjectManagedTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::NativeObjectTests;
	const int32& GeneratedLive = UE::Finare::NativeExample::FCounter::LiveCount;
	const int32 InitialLive = GeneratedLive;
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("World"), World))
	{
		return false;
	}
	auto* Host = World->GetSubsystem<UFinareWorldSubsystem>();
	auto Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
	if (TestNotNull(TEXT("Host"), Host) && TestTrue(TEXT("Plugin"), Plugin.IsValid()))
	{
		auto* Context = Host->GetNativeInvocationContext();
		if (TestNotNull(TEXT("Native context"), Context))
		{
			// No fixture registration: the generated native library is module-owned.
			auto* Target = NewObject<UFinareNativeExtensionFixture>(World);
			FFinareObjectHandleResult Result;
			const auto Handle = Host->RegisterObject(Target, Result);
			const FString Assembly = FPaths::Combine(Plugin->GetBaseDir(),
			                                         TEXT("Binaries/Managed/Demo/Finare.Unreal.Demo.dll"));
			const FString Type = TEXT("Finare.Unreal.Demo.NativeObjectLifetimeDemoScript");
			const FString FailureType = TEXT("Finare.Unreal.Demo.NativeObjectStartFailureDemoScript");
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
				TestFalse(TEXT("Failed candidate rolls back"), Host->ReloadManagedScript(
					          7011, Assembly, FailureType, Generation, Error));
				TestTrue(
					TEXT("Candidate failure reached managed OnStart"),
					Error.Contains(TEXT("expected startup failure")));
				TestEqual(TEXT("Only previous native owner survives rollback"), GeneratedLive, InitialLive + 1);
				TestTrue(
					TEXT("Retained generation native handle still works"),
					Host->TickManagedScript(7011, 0.016f, Error));
				TestTrue(TEXT("Replace generation"), Host->ReloadManagedScript(
					         7011, Assembly, Type, Generation, Error));
				TestEqual(TEXT("Old generation cleaned despite missing Dispose"), GeneratedLive, InitialLive + 1);
				TestTrue(TEXT("Destroy managed script"), Host->DestroyManagedScript(7011, Error));
				TestEqual(TEXT("Script destroy cleans native ownership"), GeneratedLive, InitialLive);
				TestTrue(TEXT("Second script for World teardown"), Host->CreateManagedScript(
					         7012, Handle, Handle, Assembly, Type, Error));
			}
		}
	}
	World->RemoveFromRoot();
	World->DestroyWorld(false);
	TestEqual(TEXT("World teardown cleans forgotten ownership"), GeneratedLive, InitialLive);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareNativeObjectTextManagedTest,
                                 "Finare.Unreal.NativeObjects.ManagedTextLifetime",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeObjectTextManagedTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::NativeObjectTests;
	const int32& GeneratedLive = UE::Finare::NativeExample::FCounter::LiveCount;
	const int32 InitialLive = GeneratedLive;
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("World"), World))
	{
		return false;
	}
	auto* Host = World->GetSubsystem<UFinareWorldSubsystem>();
	auto Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
	if (TestNotNull(TEXT("Host"), Host) && TestTrue(TEXT("Plugin"), Plugin.IsValid()))
	{
		auto* Target = NewObject<UFinareNativeExtensionFixture>(World);
		FFinareObjectHandleResult Result;
		const auto Handle = Host->RegisterObject(Target, Result);
		const FString Assembly = FPaths::Combine(Plugin->GetBaseDir(),
		                                         TEXT("Binaries/Managed/Demo/Finare.Unreal.Demo.dll"));
		const FString Type = TEXT("Finare.Unreal.Demo.NativeObjectTextGameplayDemoScript");
		FString Error;
		bool Started = Host->CreateManagedScript(7013, Handle, Handle, Assembly, Type, Error);
		TestTrue(*FString::Printf(TEXT("Managed text startup: %s"), *Error), Started);
		if (Started)
		{
			TestEqual(TEXT("Text script owns one native counter"), GeneratedLive, InitialLive + 1);
			TestTrue(*FString::Printf(TEXT("Managed text tick: %s"), *Error),
			         Host->TickManagedScript(7013, 0.016f, Error));
			int32 Generation = 0;
			TestTrue(*FString::Printf(TEXT("Managed text reload: %s"), *Error),
			         Host->ReloadManagedScript(7013, Assembly, Type, Generation, Error));
			TestTrue(TEXT("Managed text reload advances generation"), Generation > 0);
			TestEqual(TEXT("Reload leaves one native counter"), GeneratedLive, InitialLive + 1);
			TestTrue(*FString::Printf(TEXT("Reloaded managed text tick: %s"), *Error),
			         Host->TickManagedScript(7013, 0.016f, Error));
			TestTrue(TEXT("Destroy managed text script"), Host->DestroyManagedScript(7013, Error));
			TestEqual(TEXT("Text script destroy cleans native ownership"), GeneratedLive, InitialLive);
			TestTrue(*FString::Printf(TEXT("Start text script for World teardown: %s"), *Error),
			         Host->CreateManagedScript(7014, Handle, Handle, Assembly, Type, Error));
			TestEqual(TEXT("World teardown script owns one native counter"), GeneratedLive, InitialLive + 1);
		}
	}
	World->RemoveFromRoot();
	World->DestroyWorld(false);
	TestEqual(TEXT("Text World teardown cleans forgotten ownership"), GeneratedLive, InitialLive);
	return true;
}
#endif

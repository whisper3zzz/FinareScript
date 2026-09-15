#include "FinareNativeObjectRegistry.h"
#include "FinareNativeTransport.h"
#include "FinareNativeObjectValues.h"
#include "FinareCppCallback.h"
#include <atomic>

namespace
{
	// No slot reuse and no wraparound. Cross-World and old-generation tokens cannot alias.
	std::atomic<uint64> NextNativeToken{1};

	uint64 AllocateNativeToken()
	{
		uint64 Current = NextNativeToken.load(std::memory_order_relaxed);
		while (Current != MAX_uint64)
		{
			if (NextNativeToken.compare_exchange_weak(Current, Current + 1, std::memory_order_relaxed))
			{
				return Current;
			}
		}
		return 0;
	}

	using EStatus = EFinareNativeObjectStatus;

	struct FLibraryEntry
	{
		uint64 Library;
		FFinareNativeObjectMember Invoke;
		FFinareNativeObjectTextMember TextInvoke;
		decltype(FFinareNativeObjectMemberEntry::Memory) Memory = nullptr;
	};

	struct FLibraries
	{
		TMap<uint64, TArray<FLibraryEntry>> Members;
		TMap<uint64, TArray<FFinareNativeObjectCastEntry>> Casts;
		TSet<FFinareNativeObjectRegistry*> Registries;
	};

	FLibraries& Libraries()
	{
		// Module static destructors may run in any order; the process owns this index.
		static FLibraries* Value = new FLibraries();
		return *Value;
	}

	bool IsFixedCell(const FFinareNativeValueV3& Value, bool bAllowVoid)
	{
		return Value.Flags == EFinareNativeValueFlagsV3::None && !Value.Reserved16 && !Value.Auxiliary &&
		((bAllowVoid && Value.Kind == EFinareNativeValueKindV3::Void) ||
			(Value.Kind >= EFinareNativeValueKindV3::Boolean && Value.Kind <= EFinareNativeValueKindV3::Enum) ||
			Value.Kind == EFinareNativeValueKindV3::Vector3 || Value.Kind == EFinareNativeValueKindV3::Rotator3);
	}
}

struct FFinareNativeObjectRegistry::FImpl
{
	static constexpr uint32 MaxTextInputBytes = 16u * 1024u * 1024u;
	static constexpr uint32 MaxTextOutputBytes = 16u * 1024u * 1024u;
	static constexpr uint32 MaxTextPendingBytes = 16u * 1024u * 1024u;
	static constexpr uint32 MaxTextPendingCount = 64;

	struct FEntry
	{
		uint64 Scope;
		uint64 TypeId;
		void* Pointer;
		FFinareNativeObjectDestroy Destroy;
		uint64 Owner;
		TSet<uint64> Children;
		uint64 Library = 0;
	};

	struct FPendingText
	{
		uint64 Scope = 0;
		uint64 Library = 0;
		FFinareNativeValueV3 Value;
		FFinareNativeObjectHandle Handle;
		TArray<uint8> Bytes;
		TArray<uint64> Allocations;
	};

	bool bAttached = true;
	TMap<uint64, TSet<uint64>> Scopes;
	TMap<uint64, FEntry> Entries;
	TMap<void*, int32> PointerCounts;
	TMap<uint64, FFinareNativeObjectMember> Members;
	TMap<uint64, FFinareNativeObjectTextMember> TextMembers;
	TMap<uint64, FPendingText> PendingText;
	uint32 PendingTextBytes = 0;
	uint32 PendingTextReservations = 0;
	TArray<uint64>* Allocations = nullptr;
	uint64 CurrentLibrary = 0;

	void RetireUnpublished(uint64 Token)
	{
		if (PendingText.Contains(Token))
		{
			RetirePending(Token);
		}
		else
		{
			Retire(Token);
		}
	}

	void RetirePending(uint64 Token)
	{
		FPendingText Pending;
		if (!PendingText.RemoveAndCopyValue(Token, Pending))
		{
			return;
		}
		PendingTextBytes -= FMath::Min<uint32>(PendingTextBytes, Pending.Bytes.Num());
		for (int32 Index = Pending.Allocations.Num() - 1; Index >= 0; --Index)
		{
			RetireUnpublished(Pending.Allocations[Index]);
		}
	}

	EStatus Check(uint64 Scope) const
	{
		if (Finare_IsNativeMemoryBorrowed()) return EStatus::Unavailable;
		if (!IsInGameThread())
		{
			return EStatus::WrongThread;
		}
		if (!bAttached)
		{
			return EStatus::Unavailable;
		}
		return Scopes.Contains(Scope) ? EStatus::Success : EStatus::InvalidScope;
	}

	void Retire(uint64 Handle)
	{
		// Remove the entire borrowed subtree before calling user destruction.
		// Iteration avoids a native stack overflow for arbitrarily deep borrow chains.
		TArray<uint64> Pending{Handle};
		TArray<FEntry> Retired;
		for (int32 Index = 0; Index < Pending.Num(); ++Index)
		{
			FEntry Entry;
			if (!Entries.RemoveAndCopyValue(Pending[Index], Entry))
			{
				continue;
			}
			if (auto* Parent = Entries.Find(Entry.Owner))
			{
				Parent->Children.Remove(Pending[Index]);
			}
			for (uint64 Child : Entry.Children)
			{
				Pending.Add(Child);
			}
			if (auto* Scope = Scopes.Find(Entry.Scope))
			{
				Scope->Remove(Pending[Index]);
			}
			if (--PointerCounts.FindChecked(Entry.Pointer) == 0)
			{
				PointerCounts.Remove(Entry.Pointer);
			}
			Retired.Add(MoveTemp(Entry));
		}
		for (int32 Index = Retired.Num() - 1; Index >= 0; --Index)
		{
			if (Retired[Index].Destroy)
			{
				Retired[Index].Destroy(Retired[Index].Pointer);
			}
		}
	}
};

FFinareNativeObjectRegistry::FFinareNativeObjectRegistry() : Impl(MakeUnique<FImpl>())
{
	check(IsInGameThread());
	Libraries().Registries.Add(this);
}

FFinareNativeObjectRegistry::~FFinareNativeObjectRegistry()
{
	// A retained detached context may finally be released on a managed worker.
	// All game-thread destruction must already have happened during Detach.
	if (Impl->bAttached)
	{
		Detach();
	}
	check(Impl->Entries.IsEmpty());
}

EStatus FFinareNativeObjectRegistry::OpenScope(uint64& OutScope)
{
	OutScope = 0;
	if (!IsInGameThread())
	{
		return EStatus::WrongThread;
	}
	if (!Impl->bAttached)
	{
		return EStatus::Unavailable;
	}
	const uint64 Token = AllocateNativeToken();
	if (!Token)
	{
		return EStatus::Exhausted;
	}
	Impl->Scopes.Add(Token, {});
	OutScope = Token;
	return EStatus::Success;
}

EStatus FFinareNativeObjectRegistry::CloseScope(uint64 Scope)
{
	const EStatus Status = Impl->Check(Scope);
	if (Status != EStatus::Success)
	{
		return Status;
	}
	TSet<uint64> Handles;
	Impl->Scopes.RemoveAndCopyValue(Scope, Handles); // Reject reentrant calls into this generation.
	TArray<uint64> Pending;
	for (const auto& Entry : Impl->PendingText)
	{
		if (Entry.Value.Scope == Scope)
		{
			Pending.Add(Entry.Key);
		}
	}
	for (uint64 Token : Pending)
	{
		Impl->RetirePending(Token);
	}
	for (uint64 Handle : Handles)
	{
		Impl->Retire(Handle);
	}
	return EStatus::Success;
}

EStatus FFinareNativeObjectRegistry::RegisterOwned(uint64 Scope, uint64 TypeId,
                                                   void* Pointer, FFinareNativeObjectDestroy Destroy,
                                                   FFinareNativeObjectHandle& OutHandle)
{
	OutHandle = {};
	const EStatus Status = Impl->Check(Scope);
	if (Status != EStatus::Success)
	{
		return Status;
	}
	if (!TypeId || !Pointer || !Destroy)
	{
		return EStatus::InvalidArgument;
	}
	// An address must not acquire two deleting owners, even in different scopes.
	if (Impl->PointerCounts.Contains(Pointer))
	{
		return EStatus::WrongOwner;
	}
	const uint64 Token = AllocateNativeToken();
	if (!Token)
	{
		return EStatus::Exhausted;
	}
	Impl->Entries.Add(Token, {Scope, TypeId, Pointer, Destroy, 0});
	Impl->Entries.FindChecked(Token).Library = Impl->CurrentLibrary;
	Impl->Scopes.FindChecked(Scope).Add(Token);
	Impl->PointerCounts.Add(Pointer, 1);
	if (Impl->Allocations)
	{
		Impl->Allocations->Add(Token);
	}
	OutHandle.Value = Token;
	return EStatus::Success;
}

EStatus FFinareNativeObjectRegistry::RegisterBorrowed(uint64 Scope, uint64 TypeId,
                                                      void* Pointer, FFinareNativeObjectHandle Owner,
                                                      FFinareNativeObjectHandle& OutHandle)
{
	OutHandle = {};
	const EStatus Status = Impl->Check(Scope);
	if (Status != EStatus::Success)
	{
		return Status;
	}
	if (!TypeId || !Pointer)
	{
		return EStatus::InvalidArgument;
	}
	FImpl::FEntry* Parent = Impl->Entries.Find(Owner.Value);
	if (!Parent)
	{
		return EStatus::InvalidHandle;
	}
	if (Parent->Scope != Scope)
	{
		return EStatus::WrongOwner;
	}
	const uint64 Token = AllocateNativeToken();
	if (!Token)
	{
		return EStatus::Exhausted;
	}
	Parent->Children.Add(Token);
	Impl->Entries.Add(Token, {Scope, TypeId, Pointer, nullptr, Owner.Value});
	Impl->Entries.FindChecked(Token).Library = Impl->CurrentLibrary;
	Impl->Scopes.FindChecked(Scope).Add(Token);
	++Impl->PointerCounts.FindOrAdd(Pointer);
	if (Impl->Allocations)
	{
		Impl->Allocations->Add(Token);
	}
	OutHandle.Value = Token;
	return EStatus::Success;
}

EStatus FFinareNativeObjectRegistry::Resolve(uint64 Scope, FFinareNativeObjectHandle Handle,
                                             uint64 TypeId, void*& OutPointer) const
{
	OutPointer = nullptr;
	const EStatus Status = Impl->Check(Scope);
	if (Status != EStatus::Success)
	{
		return Status;
	}
	const FImpl::FEntry* Entry = Impl->Entries.Find(Handle.Value);
	if (!Entry)
	{
		return EStatus::InvalidHandle;
	}
	if (Entry->Scope != Scope)
	{
		return EStatus::WrongOwner;
	}
	if (!TypeId) return EStatus::WrongType;
	uint64 CurrentType = Entry->TypeId;
	void* CurrentPointer = Entry->Pointer;
	TSet<uint64> Visited;
	while (Visited.Num() < 64 && !Visited.Contains(CurrentType))
	{
		if (CurrentType == TypeId) { OutPointer = CurrentPointer; return EStatus::Success; }
		Visited.Add(CurrentType);
		const FFinareNativeObjectCastEntry* Edge = nullptr;
		for (const auto& Library : Libraries().Casts)
			for (const auto& Candidate : Library.Value)
				if (Candidate.DerivedType == CurrentType)
				{
					if (Edge) return EStatus::WrongType; // Ambiguous module declarations never guess an adjustment.
					Edge = &Candidate;
				}
		if (!Edge || !Edge->Cast) return EStatus::WrongType;
		CurrentPointer = Edge->Cast(CurrentPointer); CurrentType = Edge->BaseType;
	}
	return EStatus::WrongType;
}

EStatus FFinareNativeObjectRegistry::BorrowMemory(uint64 Scope, FFinareNativeObjectHandle Target, uint64 MemberId,
	uint8 Kind, uint32 ElementSize, bool bWrite, FFinareMemoryCallback Callback, void* UserData)
{
	EStatus Status = Impl->Check(Scope);
	if (Status != EStatus::Success) return Status;
	if (!Callback) return EStatus::InvalidArgument;
	const auto* Entries = Libraries().Members.Find(MemberId);
	if (!Entries || Entries->IsEmpty()) return EStatus::UnknownMember;
	if (Entries->Num() != 1) return EStatus::DuplicateMember;
	if (!(*Entries)[0].Memory) return EStatus::InvalidArgument;
	void* Data = nullptr; int32 Count = 0;
	Status = (*Entries)[0].Memory(*this, Scope, Target, Kind, ElementSize, bWrite, Data, Count);
	if (Status != EStatus::Success) return Status;
	return Finare_WithNativeMemory(Data, Count, ElementSize, Callback, UserData) ? EStatus::Success : EStatus::InvalidArgument;
}

EStatus FFinareNativeObjectRegistry::Release(uint64 Scope, FFinareNativeObjectHandle Handle)
{
	const EStatus Status = Impl->Check(Scope);
	if (Status != EStatus::Success)
	{
		return Status;
	}
	const FImpl::FEntry* Entry = Impl->Entries.Find(Handle.Value);
	if (!Entry)
	{
		return EStatus::InvalidHandle;
	}
	if (Entry->Scope != Scope)
	{
		return EStatus::WrongOwner;
	}
	Impl->Retire(Handle.Value);
	return EStatus::Success;
}

EStatus FFinareNativeObjectRegistry::InvalidateBorrowed(uint64 Scope, FFinareNativeObjectHandle Owner)
{
	const EStatus Status = Impl->Check(Scope);
	if (Status != EStatus::Success)
	{
		return Status;
	}
	const FImpl::FEntry* Entry = Impl->Entries.Find(Owner.Value);
	if (!Entry)
	{
		return EStatus::InvalidHandle;
	}
	if (Entry->Scope != Scope)
	{
		return EStatus::WrongOwner;
	}
	const TArray<uint64> Children = Entry->Children.Array();
	for (uint64 Child : Children)
	{
		Impl->Retire(Child);
	}
	return EStatus::Success;
}

EStatus FFinareNativeObjectRegistry::RegisterMember(uint64 MemberId, FFinareNativeObjectMember Member)
{
	if (!IsInGameThread())
	{
		return EStatus::WrongThread;
	}
	if (!Impl->bAttached)
	{
		return EStatus::Unavailable;
	}
	if (!MemberId || !Member)
	{
		return EStatus::InvalidArgument;
	}
	if (Impl->Members.Contains(MemberId) || Impl->TextMembers.Contains(MemberId) || Libraries().Members.
		Contains(MemberId))
	{
		return EStatus::DuplicateMember;
	}
	Impl->Members.Add(MemberId, Member);
	return EStatus::Success;
}

EStatus FFinareNativeObjectRegistry::BeginText(uint64 Scope, FFinareNativeObjectHandle Target,
                                               uint64 MemberId, TConstArrayView<FFinareNativeValueV3> Arguments,
                                               TConstArrayView<uint8> Input,
                                               uint64& OutToken, uint32& OutRequiredBytes)
{
	OutToken = 0;
	OutRequiredBytes = 0;
	EStatus Status = Impl->Check(Scope);
	if (Status != EStatus::Success)
	{
		return Status;
	}
	if (Input.Num() > FImpl::MaxTextInputBytes || Arguments.Num() > 16)
	{
		return EStatus::InvalidArgument;
	}
	FFinareNativeObjectTextMember Member = Impl->TextMembers.FindRef(MemberId);
	uint64 LibraryId = 0;
	if (const auto* Generated = Libraries().Members.Find(MemberId))
	{
		if (Generated->Num() != 1 || Member)
		{
			return EStatus::DuplicateMember;
		}
		Member = (*Generated)[0].TextInvoke;
		LibraryId = (*Generated)[0].Library;
	}
	if (!Member)
	{
		return EStatus::UnknownMember;
	}
	if (Target.Value)
	{
		const auto* Entry = Impl->Entries.Find(Target.Value);
		if (!Entry)
		{
			return EStatus::InvalidHandle;
		}
		if (Entry->Scope != Scope)
		{
			return EStatus::WrongOwner;
		}
	}
	for (const auto& Value : Arguments)
	{
		if (Value.Kind == EFinareNativeValueKindV3::NativeObject)
		{
			void* Pointer = nullptr;
			Status = UE::Finare::NativeObjectValues::ReadObject(*this, Scope, Value, Value.TypeId, true, Pointer);
			if (Status != EStatus::Success)
			{
				return Status;
			}
		}
		else if (Value.Kind == EFinareNativeValueKindV3::String)
		{
			if (!UE::Finare::NativeObjectValues::IsTextCell(Value) || Value.Payload0 > static_cast<uint64>(Input.Num())
				||
				Value.Payload1 > static_cast<uint64>(Input.Num()) - Value.Payload0)
			{
				return EStatus::InvalidArgument;
			}
			if (Value.Payload1)
			{
				const TConstArrayView<uint8> Bytes(Input.GetData() + Value.Payload0,
				                                   static_cast<int32>(Value.Payload1));
				if (!UE::Finare::NativeObjectValues::IsValidUtf8(Bytes))
				{
					return EStatus::InvalidArgument;
				}
			}
		}
		else if (Value.Kind == EFinareNativeValueKindV3::Callback && ResolveCppCallback(Value, Value.TypeId).IsValid()) { }
		else if (!IsFixedCell(Value, false))
		{
			return EStatus::InvalidArgument;
		}
	}
	if (Impl->PendingText.Num() + Impl->PendingTextReservations >= FImpl::MaxTextPendingCount ||
		Impl->PendingTextBytes >= FImpl::MaxTextPendingBytes)
	{
		return EStatus::Exhausted;
	}
	const uint64 ReservedToken = AllocateNativeToken();
	if (!ReservedToken)
	{
		return EStatus::Exhausted;
	}
	++Impl->PendingTextReservations;
	auto ReleaseReservation = [&]()
	{
		check(Impl->PendingTextReservations);
		--Impl->PendingTextReservations;
	};
	FFinareNativeValueV3 Value;
	FFinareNativeObjectHandle Handle;
	TArray<uint8> Bytes;
	TArray<uint64> Allocations;
	TArray<uint64>* ParentAllocations = Impl->Allocations;
	const uint64 PreviousLibrary = Impl->CurrentLibrary;
	Impl->Allocations = &Allocations;
	Impl->CurrentLibrary = LibraryId;
	Status = Member(*this, Scope, Target, Arguments, Input, Value, Handle, Bytes);
	Impl->CurrentLibrary = PreviousLibrary;
	Impl->Allocations = ParentAllocations;
	if (Status == EStatus::Success)
	{
		if (Bytes.Num() > FImpl::MaxTextOutputBytes ||
			Value.Kind == EFinareNativeValueKindV3::String &&
			(!UE::Finare::NativeObjectValues::IsTextCell(Value) || Value.Payload0 > static_cast<uint64>(Bytes.Num()) ||
				Value.Payload1 > static_cast<uint64>(Bytes.Num()) - Value.Payload0 ||
				Value.Payload1 && !UE::Finare::NativeObjectValues::IsValidUtf8(
					TConstArrayView<uint8>(Bytes.GetData() + Value.Payload0, static_cast<int32>(Value.Payload1)))) ||
			Value.Kind != EFinareNativeValueKindV3::String &&
			(!IsFixedCell(Value, true) || Bytes.Num() != 0))
		{
			Status = EStatus::InvalidArgument;
		}
		if (Handle.Value)
		{
			const auto* Entry = Impl->Entries.Find(Handle.Value);
			if (!Entry)
			{
				Status = EStatus::InvalidHandle;
			}
			else if (Entry->Scope != Scope)
			{
				Status = EStatus::WrongOwner;
			}
		}
		if (Impl->Check(Scope) != EStatus::Success)
		{
			Status = EStatus::InvalidScope;
		}
	}
	if (Status != EStatus::Success)
	{
		ReleaseReservation();
		for (int32 Index = Allocations.Num() - 1; Index >= 0; --Index)
		{
			Impl->RetireUnpublished(Allocations[Index]);
		}
		return Status;
	}
	if (static_cast<uint32>(Bytes.Num()) > FImpl::MaxTextPendingBytes - Impl->PendingTextBytes)
	{
		ReleaseReservation();
		for (int32 Index = Allocations.Num() - 1; Index >= 0; --Index)
		{
			Impl->RetireUnpublished(Allocations[Index]);
		}
		return EStatus::Exhausted;
	}
	FImpl::FPendingText Pending;
	Pending.Scope = Scope;
	Pending.Library = LibraryId;
	Pending.Value = Value;
	Pending.Handle = Handle;
	Pending.Bytes = MoveTemp(Bytes);
	Pending.Allocations = MoveTemp(Allocations);
	Impl->PendingTextBytes += Pending.Bytes.Num();
	Impl->PendingText.Add(ReservedToken, MoveTemp(Pending));
	ReleaseReservation();
	if (ParentAllocations)
	{
		ParentAllocations->Add(ReservedToken);
	}
	OutToken = ReservedToken;
	OutRequiredBytes = Impl->PendingText.FindChecked(ReservedToken).Bytes.Num();
	return EStatus::Success;
}

EStatus FFinareNativeObjectRegistry::ReadText(uint64 Scope, uint64 Token, uint8* Output, uint32 Capacity,
                                              uint32& OutRequiredBytes, FFinareNativeValueV3& OutValue,
                                              FFinareNativeObjectHandle& OutHandle)
{
	OutRequiredBytes = 0;
	OutValue = {};
	OutHandle = {};
	const EStatus Status = Impl->Check(Scope);
	if (Status != EStatus::Success)
	{
		return Status;
	}
	FImpl::FPendingText* Pending = Impl->PendingText.Find(Token);
	if (!Pending)
	{
		return EStatus::InvalidTextToken;
	}
	if (Pending->Scope != Scope)
	{
		return EStatus::WrongOwner;
	}
	if (Pending->Handle.Value)
	{
		const auto* Entry = Impl->Entries.Find(Pending->Handle.Value);
		if (!Entry || Entry->Scope != Scope)
		{
			Impl->RetirePending(Token);
			return Entry ? EStatus::WrongOwner : EStatus::InvalidHandle;
		}
	}
	OutRequiredBytes = Pending->Bytes.Num();
	if (Capacity < static_cast<uint32>(Pending->Bytes.Num()))
	{
		return EStatus::TextBufferTooSmall;
	}
	if (Pending->Bytes.Num() && !Output)
	{
		return EStatus::InvalidArgument;
	}
	if (Pending->Bytes.Num())
	{
		FMemory::Memcpy(Output, Pending->Bytes.GetData(), Pending->Bytes.Num());
	}
	OutValue = Pending->Value;
	OutHandle = Pending->Handle;
	TArray<uint64> Allocations = MoveTemp(Pending->Allocations);
	if (Impl->Allocations)
	{
		Impl->Allocations->Append(Allocations);
	}
	Impl->PendingTextBytes -= Pending->Bytes.Num();
	Impl->PendingText.Remove(Token);
	return EStatus::Success;
}

EStatus FFinareNativeObjectRegistry::DiscardText(uint64 Scope, uint64 Token)
{
	const EStatus Status = Impl->Check(Scope);
	if (Status != EStatus::Success)
	{
		return Status;
	}
	FImpl::FPendingText* Pending = Impl->PendingText.Find(Token);
	if (!Pending)
	{
		return EStatus::InvalidTextToken;
	}
	if (Pending->Scope != Scope)
	{
		return EStatus::WrongOwner;
	}
	Impl->RetirePending(Token);
	return EStatus::Success;
}

EStatus FFinareNativeObjectRegistry::Invoke(uint64 Scope, FFinareNativeObjectHandle Target,
                                            uint64 MemberId, TConstArrayView<FFinareNativeValueV3> Arguments,
                                            FFinareNativeValueV3& OutValue, FFinareNativeObjectHandle& OutHandle)
{
	OutValue = {};
	OutHandle = {};
	EStatus Status = Impl->Check(Scope);
	if (Status != EStatus::Success)
	{
		return Status;
	}
	FFinareNativeObjectMember Member = Impl->Members.FindRef(MemberId);
	uint64 LibraryId = 0;
	if (const auto* Generated = Libraries().Members.Find(MemberId))
	{
		if (Member || Generated->Num() != 1)
		{
			return EStatus::DuplicateMember;
		}
		Member = (*Generated)[0].Invoke;
		LibraryId = (*Generated)[0].Library;
	}
	if (!Member)
	{
		return EStatus::UnknownMember;
	}
	if (Target.Value)
	{
		const auto* Entry = Impl->Entries.Find(Target.Value);
		if (!Entry)
		{
			return EStatus::InvalidHandle;
		}
		if (Entry->Scope != Scope)
		{
			return EStatus::WrongOwner;
		}
	}
	// Cells contain fixed values or typed ownership tokens, never native pointers.
	if (Arguments.Num() > 16)
	{
		return EStatus::InvalidArgument;
	}
	for (const auto& Value : Arguments)
	{
		if (Value.Kind == EFinareNativeValueKindV3::NativeObject)
		{
			void* Pointer = nullptr;
			Status = UE::Finare::NativeObjectValues::ReadObject(*this, Scope, Value, Value.TypeId, true, Pointer);
			if (Status != EStatus::Success)
			{
				return Status;
			}
		}
		else if (Value.Kind == EFinareNativeValueKindV3::Callback && ResolveCppCallback(Value, Value.TypeId).IsValid()) { }
		else if (!IsFixedCell(Value, false))
		{
			return EStatus::InvalidArgument;
		}
	}
	FFinareNativeValueV3 Value;
	FFinareNativeObjectHandle Handle;
	TArray<uint64> Allocations;
	TArray<uint64>* ParentAllocations = Impl->Allocations;
	const uint64 PreviousLibrary = Impl->CurrentLibrary;
	Impl->Allocations = &Allocations;
	Impl->CurrentLibrary = LibraryId;
	Status = Member(*this, Scope, Target, Arguments, Value, Handle);
	Impl->CurrentLibrary = PreviousLibrary;
	Impl->Allocations = ParentAllocations;
	if (Status == EStatus::Success && !IsFixedCell(Value, true))
	{
		Status = EStatus::InvalidArgument;
	}
	// A failed constructor/factory must not leak unpublished native ownership.
	if (Status == EStatus::Success && Handle.Value)
	{
		const auto* Entry = Impl->Entries.Find(Handle.Value);
		if (!Entry)
		{
			Status = EStatus::InvalidHandle;
		}
		else if (Entry->Scope != Scope)
		{
			Status = EStatus::WrongOwner;
		}
	}
	if (Status == EStatus::Success)
	{
		if (ParentAllocations)
		{
			ParentAllocations->Append(Allocations);
		}
		OutValue = Value;
		OutHandle = Handle;
	}
	else
	{
		for (int32 Index = Allocations.Num() - 1; Index >= 0; --Index)
		{
			Impl->RetireUnpublished(Allocations[Index]);
		}
	}
	return Status;
}

void FFinareNativeObjectRegistry::Detach()
{
	check(IsInGameThread());
	if (!Impl->bAttached)
	{
		return;
	}
	Impl->bAttached = false;
	Libraries().Registries.Remove(this);
	Impl->Scopes.Reset();
	TArray<uint64> Handles;
	Impl->Entries.GenerateKeyArray(Handles);
	for (uint64 Handle : Handles)
	{
		Impl->Retire(Handle);
	}
	Impl->Members.Reset();
	Impl->TextMembers.Reset();
	TArray<uint64> Pending;
	Impl->PendingText.GenerateKeyArray(Pending);
	for (uint64 Token : Pending)
	{
		Impl->RetirePending(Token);
	}
}

int32 FFinareNativeObjectRegistry::Num() const
{
	check(IsInGameThread());
	return Impl->Entries.Num();
}

int32 FFinareNativeObjectRegistry::NumScopes() const
{
	check(IsInGameThread());
	return Impl->Scopes.Num();
}

FFinareNativeObjectLibrary::FFinareNativeObjectLibrary(TConstArrayView<FFinareNativeObjectMemberEntry> Members,
	TConstArrayView<FFinareNativeObjectCastEntry> Casts)
{
	check(IsInGameThread() || Libraries().Registries.IsEmpty());
	Id = AllocateNativeToken();
	check(Id);
	Libraries().Casts.Add(Id, TArray<FFinareNativeObjectCastEntry>(Casts));
	for (const auto& Member : Members)
	{
		check(Member.MemberId && (Member.Invoke || Member.TextInvoke || Member.Memory));
		Libraries().Members.FindOrAdd(Member.MemberId).Add({Id, Member.Invoke, Member.TextInvoke, Member.Memory});
	}
}

FFinareNativeObjectLibrary::~FFinareNativeObjectLibrary()
{
	Libraries().Casts.Remove(Id);
	// Unregister before invoking destructors, while the module's code is still loaded.
	check(IsInGameThread() || Libraries().Registries.IsEmpty());
	for (auto It = Libraries().Members.CreateIterator(); It; ++It)
	{
		It.Value().RemoveAll([&](const auto& Entry) { return Entry.Library == Id; });
		if (It.Value().IsEmpty())
		{
			It.RemoveCurrent();
		}
	}
	// User destructors may detach another World. Do not iterate a mutated set
	// or call a registry that disappeared during an earlier destructor.
	const auto Registries = Libraries().Registries.Array();
	for (auto* Registry : Registries)
	{
		if (Libraries().Registries.Contains(Registry))
		{
			Registry->ReleaseLibrary(Id);
		}
	}
}

void FFinareNativeObjectRegistry::ReleaseLibrary(uint64 LibraryId)
{
	check(IsInGameThread());
	TArray<uint64> Handles;
	for (const auto& Entry : Impl->Entries)
	{
		if (Entry.Value.Library == LibraryId)
		{
			Handles.Add(Entry.Key);
		}
	}
	for (uint64 Handle : Handles)
	{
		Impl->Retire(Handle);
	}
	TArray<uint64> Pending;
	for (const auto& Entry : Impl->PendingText)
	{
		if (Entry.Value.Library == LibraryId)
		{
			Pending.Add(Entry.Key);
		}
	}
	for (uint64 Token : Pending)
	{
		Impl->RetirePending(Token);
	}
}

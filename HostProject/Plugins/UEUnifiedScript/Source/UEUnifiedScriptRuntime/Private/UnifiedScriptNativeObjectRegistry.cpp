#include "UnifiedScriptNativeObjectRegistry.h"
#include "UnifiedScriptNativeTransport.h"
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
            return Current;
    }
    return 0;
}
using EStatus = EUnifiedScriptNativeObjectStatus;
bool IsFixedCell(const FUnifiedScriptNativeValueV3& Value, bool bAllowVoid)
{
    return Value.Flags == EUnifiedScriptNativeValueFlagsV3::None && !Value.Reserved16 && !Value.Auxiliary &&
        ((bAllowVoid && Value.Kind == EUnifiedScriptNativeValueKindV3::Void) ||
         (Value.Kind >= EUnifiedScriptNativeValueKindV3::Boolean && Value.Kind <= EUnifiedScriptNativeValueKindV3::Enum) ||
         Value.Kind == EUnifiedScriptNativeValueKindV3::Vector3 || Value.Kind == EUnifiedScriptNativeValueKindV3::Rotator3);
}
}

struct FUnifiedScriptNativeObjectRegistry::FImpl
{
    struct FEntry
    {
        uint64 Scope;
        uint64 TypeId;
        void* Pointer;
        FUnifiedScriptNativeObjectDestroy Destroy;
        uint64 Owner;
        TSet<uint64> Children;
    };
    bool bAttached = true;
    TMap<uint64, TSet<uint64>> Scopes;
    TMap<uint64, FEntry> Entries;
    TMap<void*, int32> PointerCounts;
    TMap<uint64, FUnifiedScriptNativeObjectMember> Members;
    TArray<uint64>* Allocations = nullptr;

    EStatus Check(uint64 Scope) const
    {
        if (!IsInGameThread()) return EStatus::WrongThread;
        if (!bAttached) return EStatus::Unavailable;
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
            if (!Entries.RemoveAndCopyValue(Pending[Index], Entry)) continue;
            if (auto* Parent = Entries.Find(Entry.Owner)) Parent->Children.Remove(Pending[Index]);
            for (uint64 Child : Entry.Children) Pending.Add(Child);
            if (auto* Scope = Scopes.Find(Entry.Scope)) Scope->Remove(Pending[Index]);
            if (--PointerCounts.FindChecked(Entry.Pointer) == 0) PointerCounts.Remove(Entry.Pointer);
            Retired.Add(MoveTemp(Entry));
        }
        for (int32 Index = Retired.Num() - 1; Index >= 0; --Index)
            if (Retired[Index].Destroy) Retired[Index].Destroy(Retired[Index].Pointer);
    }
};

FUnifiedScriptNativeObjectRegistry::FUnifiedScriptNativeObjectRegistry() : Impl(MakeUnique<FImpl>()) {}
FUnifiedScriptNativeObjectRegistry::~FUnifiedScriptNativeObjectRegistry()
{
    // A retained detached context may finally be released on a managed worker.
    // All game-thread destruction must already have happened during Detach.
    if (Impl->bAttached) Detach();
    check(Impl->Entries.IsEmpty());
}

EStatus FUnifiedScriptNativeObjectRegistry::OpenScope(uint64& OutScope)
{
    OutScope = 0;
    if (!IsInGameThread()) return EStatus::WrongThread;
    if (!Impl->bAttached) return EStatus::Unavailable;
    const uint64 Token = AllocateNativeToken();
    if (!Token) return EStatus::Exhausted;
    Impl->Scopes.Add(Token, {});
    OutScope = Token;
    return EStatus::Success;
}

EStatus FUnifiedScriptNativeObjectRegistry::CloseScope(uint64 Scope)
{
    const EStatus Status = Impl->Check(Scope);
    if (Status != EStatus::Success) return Status;
    TSet<uint64> Handles;
    Impl->Scopes.RemoveAndCopyValue(Scope, Handles); // Reject reentrant calls into this generation.
    for (uint64 Handle : Handles) Impl->Retire(Handle);
    return EStatus::Success;
}

EStatus FUnifiedScriptNativeObjectRegistry::RegisterOwned(uint64 Scope, uint64 TypeId,
    void* Pointer, FUnifiedScriptNativeObjectDestroy Destroy, FUnifiedScriptNativeObjectHandle& OutHandle)
{
    OutHandle = {};
    const EStatus Status = Impl->Check(Scope);
    if (Status != EStatus::Success) return Status;
    if (!TypeId || !Pointer || !Destroy) return EStatus::InvalidArgument;
    // An address must not acquire two deleting owners, even in different scopes.
    if (Impl->PointerCounts.Contains(Pointer)) return EStatus::WrongOwner;
    const uint64 Token = AllocateNativeToken();
    if (!Token) return EStatus::Exhausted;
    Impl->Entries.Add(Token, {Scope, TypeId, Pointer, Destroy, 0});
    Impl->Scopes.FindChecked(Scope).Add(Token);
    Impl->PointerCounts.Add(Pointer, 1);
    if (Impl->Allocations) Impl->Allocations->Add(Token);
    OutHandle.Value = Token;
    return EStatus::Success;
}

EStatus FUnifiedScriptNativeObjectRegistry::RegisterBorrowed(uint64 Scope, uint64 TypeId,
    void* Pointer, FUnifiedScriptNativeObjectHandle Owner, FUnifiedScriptNativeObjectHandle& OutHandle)
{
    OutHandle = {};
    const EStatus Status = Impl->Check(Scope);
    if (Status != EStatus::Success) return Status;
    if (!TypeId || !Pointer) return EStatus::InvalidArgument;
    FImpl::FEntry* Parent = Impl->Entries.Find(Owner.Value);
    if (!Parent) return EStatus::InvalidHandle;
    if (Parent->Scope != Scope) return EStatus::WrongOwner;
    const uint64 Token = AllocateNativeToken();
    if (!Token) return EStatus::Exhausted;
    Parent->Children.Add(Token);
    Impl->Entries.Add(Token, {Scope, TypeId, Pointer, nullptr, Owner.Value});
    Impl->Scopes.FindChecked(Scope).Add(Token);
    ++Impl->PointerCounts.FindOrAdd(Pointer);
    if (Impl->Allocations) Impl->Allocations->Add(Token);
    OutHandle.Value = Token;
    return EStatus::Success;
}

EStatus FUnifiedScriptNativeObjectRegistry::Resolve(uint64 Scope, FUnifiedScriptNativeObjectHandle Handle,
    uint64 TypeId, void*& OutPointer) const
{
    OutPointer = nullptr;
    const EStatus Status = Impl->Check(Scope);
    if (Status != EStatus::Success) return Status;
    const FImpl::FEntry* Entry = Impl->Entries.Find(Handle.Value);
    if (!Entry) return EStatus::InvalidHandle;
    if (Entry->Scope != Scope) return EStatus::WrongOwner;
    if (!TypeId || Entry->TypeId != TypeId) return EStatus::WrongType;
    OutPointer = Entry->Pointer;
    return EStatus::Success;
}

EStatus FUnifiedScriptNativeObjectRegistry::Release(uint64 Scope, FUnifiedScriptNativeObjectHandle Handle)
{
    const EStatus Status = Impl->Check(Scope);
    if (Status != EStatus::Success) return Status;
    const FImpl::FEntry* Entry = Impl->Entries.Find(Handle.Value);
    if (!Entry) return EStatus::InvalidHandle;
    if (Entry->Scope != Scope) return EStatus::WrongOwner;
    Impl->Retire(Handle.Value);
    return EStatus::Success;
}

EStatus FUnifiedScriptNativeObjectRegistry::InvalidateBorrowed(uint64 Scope, FUnifiedScriptNativeObjectHandle Owner)
{
    const EStatus Status = Impl->Check(Scope);
    if (Status != EStatus::Success) return Status;
    const FImpl::FEntry* Entry = Impl->Entries.Find(Owner.Value);
    if (!Entry) return EStatus::InvalidHandle;
    if (Entry->Scope != Scope) return EStatus::WrongOwner;
    const TArray<uint64> Children = Entry->Children.Array();
    for (uint64 Child : Children) Impl->Retire(Child);
    return EStatus::Success;
}

EStatus FUnifiedScriptNativeObjectRegistry::RegisterMember(uint64 MemberId, FUnifiedScriptNativeObjectMember Member)
{
    if (!IsInGameThread()) return EStatus::WrongThread;
    if (!Impl->bAttached) return EStatus::Unavailable;
    if (!MemberId || !Member) return EStatus::InvalidArgument;
    if (Impl->Members.Contains(MemberId)) return EStatus::DuplicateMember;
    Impl->Members.Add(MemberId, Member);
    return EStatus::Success;
}

EStatus FUnifiedScriptNativeObjectRegistry::Invoke(uint64 Scope, FUnifiedScriptNativeObjectHandle Target,
    uint64 MemberId, TConstArrayView<FUnifiedScriptNativeValueV3> Arguments,
    FUnifiedScriptNativeValueV3& OutValue, FUnifiedScriptNativeObjectHandle& OutHandle)
{
    OutValue = {};
    OutHandle = {};
    EStatus Status = Impl->Check(Scope);
    if (Status != EStatus::Success) return Status;
    const auto* Member = Impl->Members.Find(MemberId);
    if (!Member) return EStatus::UnknownMember;
    if (Target.Value)
    {
        const auto* Entry = Impl->Entries.Find(Target.Value);
        if (!Entry) return EStatus::InvalidHandle;
        if (Entry->Scope != Scope) return EStatus::WrongOwner;
    }
    // Fixed ABI lane only; pointer/arena-backed cells cannot enter this route.
    if (Arguments.Num() > 16) return EStatus::InvalidArgument;
    for (const auto& Value : Arguments)
        if (!IsFixedCell(Value, false)) return EStatus::InvalidArgument;
    FUnifiedScriptNativeValueV3 Value;
    FUnifiedScriptNativeObjectHandle Handle;
    TArray<uint64> Allocations;
    TArray<uint64>* ParentAllocations = Impl->Allocations;
    Impl->Allocations = &Allocations;
    Status = (*Member)(*this, Scope, Target, Arguments, Value, Handle);
    Impl->Allocations = ParentAllocations;
    if (Status == EStatus::Success && !IsFixedCell(Value, true)) Status = EStatus::InvalidArgument;
    // A failed constructor/factory must not leak unpublished native ownership.
    if (Status == EStatus::Success && Handle.Value)
    {
        const auto* Entry = Impl->Entries.Find(Handle.Value);
        if (!Entry) Status = EStatus::InvalidHandle;
        else if (Entry->Scope != Scope) Status = EStatus::WrongOwner;
    }
    if (Status == EStatus::Success)
    {
        if (ParentAllocations) ParentAllocations->Append(Allocations);
        OutValue = Value;
        OutHandle = Handle;
    }
    else
        for (int32 Index = Allocations.Num() - 1; Index >= 0; --Index) Impl->Retire(Allocations[Index]);
    return Status;
}

void FUnifiedScriptNativeObjectRegistry::Detach()
{
    check(IsInGameThread());
    if (!Impl->bAttached) return;
    Impl->bAttached = false;
    Impl->Scopes.Reset();
    TArray<uint64> Handles;
    Impl->Entries.GenerateKeyArray(Handles);
    for (uint64 Handle : Handles) Impl->Retire(Handle);
    Impl->Members.Reset();
}

int32 FUnifiedScriptNativeObjectRegistry::Num() const { check(IsInGameThread()); return Impl->Entries.Num(); }
int32 FUnifiedScriptNativeObjectRegistry::NumScopes() const { check(IsInGameThread()); return Impl->Scopes.Num(); }

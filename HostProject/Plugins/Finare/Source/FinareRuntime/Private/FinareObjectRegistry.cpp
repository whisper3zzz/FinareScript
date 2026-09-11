#include "FinareObjectRegistry.h"

#include "Engine/World.h"
#include "HAL/CriticalSection.h"
#include "Misc/ScopeLock.h"
#include "UObject/UObjectArray.h"
#include "UObject/WeakObjectPtr.h"

#include <atomic>

namespace UE::Finare::Private
{
uint32 AllocateGeneration()
{
    static std::atomic<uint32> NextGeneration{1};
    for (;;)
    {
        const uint32 Generation = NextGeneration.fetch_add(
            1,
            std::memory_order_relaxed);
        if (Generation != 0)
        {
            return Generation;
        }
    }
}

uint64 AllocateReferenceScope()
{
    static std::atomic<uint64> NextScope{1};
    for (;;)
    {
        const uint64 Scope = NextScope.fetch_add(1, std::memory_order_relaxed);
        if (Scope != 0)
        {
            return Scope;
        }
    }
}

void Succeed(FFinareObjectHandleResult& OutResult)
{
    OutResult.Error = EFinareObjectHandleError::None;
    OutResult.Message.Reset();
}

void Fail(
    FFinareObjectHandleResult& OutResult,
    EFinareObjectHandleError Error,
    const TCHAR* Message)
{
    OutResult.Error = Error;
    OutResult.Message = Message;
}
}

struct FFinareObjectRegistry::FImpl final : FUObjectArray::FUObjectDeleteListener
{
    struct FEntry
    {
        TWeakObjectPtr<UObject> Object;
        uint32 Generation = 0;
        uint32 ReferenceCount = 0;
        bool bActive = false;
    };

    explicit FImpl(UWorld* InWorld)
        : World(InWorld)
        , WorldIdentity(InWorld)
    {
        check(IsInGameThread());
        GUObjectArray.AddUObjectDeleteListener(this);
        bDeleteListenerRegistered = true;
    }

    virtual ~FImpl() override
    {
        if (bDeleteListenerRegistered)
        {
            GUObjectArray.RemoveUObjectDeleteListener(this);
        }
    }

    virtual void NotifyUObjectDeleted(const UObjectBase* Object, int32 Index) override
    {
        FScopeLock Lock(&CriticalSection);
        if (Object == WorldIdentity)
        {
            InvalidateAllLocked();
            World.Reset();
            WorldIdentity = nullptr;
            return;
        }

        if (uint32* SlotIndex = SlotsByObject.Find(Object))
        {
            RetireSlotLocked(*SlotIndex, Object);
        }
    }

    virtual void OnUObjectArrayShutdown() override
    {
        {
            FScopeLock Lock(&CriticalSection);
            InvalidateAllLocked();
            World.Reset();
            WorldIdentity = nullptr;
        }
        GUObjectArray.RemoveUObjectDeleteListener(this);
        bDeleteListenerRegistered = false;
    }

    virtual SIZE_T GetAllocatedSize() const override
    {
        FScopeLock Lock(&CriticalSection);
        return Entries.GetAllocatedSize() +
            FreeSlots.GetAllocatedSize() +
            SlotsByObject.GetAllocatedSize();
    }

    bool ValidateThread(FFinareObjectHandleResult& OutResult) const
    {
        if (IsInGameThread())
        {
            return true;
        }

        UE::Finare::Private::Fail(
            OutResult,
            EFinareObjectHandleError::NotGameThread,
            TEXT("Finare UObject handles may only be accessed on the game thread."));
        return false;
    }

    UWorld* GetWorld(FFinareObjectHandleResult& OutResult) const
    {
        FScopeLock Lock(&CriticalSection);
        UWorld* RegistryWorld = World.Get();
        if (RegistryWorld != nullptr)
        {
            return RegistryWorld;
        }

        UE::Finare::Private::Fail(
            OutResult,
            EFinareObjectHandleError::RegistryWorldUnavailable,
            TEXT("The Finare UObject registry world is no longer available."));
        return nullptr;
    }

    bool DecodeHandleLocked(
        FFinareObjectHandle Handle,
        uint32& OutSlotIndex,
        FFinareObjectHandleResult& OutResult) const
    {
        if (!Handle.IsWellFormed())
        {
            UE::Finare::Private::Fail(
                OutResult,
                EFinareObjectHandleError::InvalidHandle,
                TEXT("The Finare UObject handle encoding is invalid."));
            return false;
        }

        OutSlotIndex = Handle.GetSlotIndex();
        if (!Entries.IsValidIndex(static_cast<int32>(OutSlotIndex)))
        {
            UE::Finare::Private::Fail(
                OutResult,
                EFinareObjectHandleError::StaleHandle,
                TEXT("The Finare UObject handle refers to an unavailable slot."));
            return false;
        }

        const FEntry& Entry = Entries[static_cast<int32>(OutSlotIndex)];
        if (!Entry.bActive || Entry.Generation != Handle.GetGeneration())
        {
            UE::Finare::Private::Fail(
                OutResult,
                EFinareObjectHandleError::StaleHandle,
                TEXT("The Finare UObject handle generation is stale."));
            return false;
        }
        return true;
    }

    void RetireSlotLocked(uint32 SlotIndex, const UObjectBase* KnownObject = nullptr)
    {
        FEntry& Entry = Entries[static_cast<int32>(SlotIndex)];
        if (!Entry.bActive)
        {
            return;
        }

        const UObjectBase* ObjectIdentity = KnownObject;
        if (ObjectIdentity == nullptr)
        {
            ObjectIdentity = Entry.Object.GetEvenIfUnreachable();
        }
        if (ObjectIdentity != nullptr)
        {
            SlotsByObject.Remove(ObjectIdentity);
        }

        Entry.Object.Reset();
        Entry.ReferenceCount = 0;
        Entry.bActive = false;
        // Per-scope lease counts remain intact. Managed leases may share a
        // handle, and each Release must be able to consume its own count after
        // the registry entry has become stale.
        FreeSlots.Add(SlotIndex);
        --ActiveCount;
    }

    void InvalidateAllLocked()
    {
        SlotsByObject.Reset();
        FreeSlots.Reset();
        ActiveCount = 0;
        // Keep each scope's lease counts until its managed leases release or
        // the scope closes. InvalidateAll makes handles stale; it does not
        // erase ownership bookkeeping needed for idempotent cleanup.
        for (int32 SlotIndex = 0; SlotIndex < Entries.Num(); ++SlotIndex)
        {
            FEntry& Entry = Entries[SlotIndex];
            if (Entry.bActive)
            {
                Entry.Object.Reset();
                Entry.ReferenceCount = 0;
                Entry.bActive = false;
            }
            FreeSlots.Add(static_cast<uint32>(SlotIndex));
        }
    }

    TWeakObjectPtr<UWorld> World;
    const UObjectBase* WorldIdentity = nullptr;
    TArray<FEntry> Entries;
    TArray<uint32> FreeSlots;
    TMap<const UObjectBase*, uint32> SlotsByObject;
    mutable FCriticalSection CriticalSection;
    int32 ActiveCount = 0;
    TMap<uint64, TMap<uint64, uint32>> ReferenceScopes;
    bool bDeleteListenerRegistered = false;
};

FFinareObjectRegistry::FFinareObjectRegistry(UWorld* InWorld)
    : Impl(MakeUnique<FImpl>(InWorld))
{
}

FFinareObjectRegistry::~FFinareObjectRegistry() = default;

FFinareObjectHandle FFinareObjectRegistry::RegisterObject(
    UObject* Object,
    FFinareObjectHandleResult& OutResult)
{
    using namespace UE::Finare::Private;

    if (!Impl->ValidateThread(OutResult))
    {
        return {};
    }
    UWorld* RegistryWorld = Impl->GetWorld(OutResult);
    if (RegistryWorld == nullptr)
    {
        return {};
    }
    if (Object == nullptr || !IsValid(Object))
    {
        Fail(
            OutResult,
            EFinareObjectHandleError::NullObject,
            TEXT("A live UObject is required to create a Finare handle."));
        return {};
    }
    if (UWorld* ObjectWorld = Object->GetWorld();
        ObjectWorld != nullptr && ObjectWorld != RegistryWorld)
    {
        Fail(
            OutResult,
            EFinareObjectHandleError::WorldMismatch,
            TEXT("The UObject does not belong to the registry world."));
        return {};
    }

    FScopeLock Lock(&Impl->CriticalSection);
    if (uint32* ExistingSlot = Impl->SlotsByObject.Find(Object))
    {
        FImpl::FEntry& Entry = Impl->Entries[static_cast<int32>(*ExistingSlot)];
        if (Entry.bActive && Entry.Object.Get() == Object)
        {
            ++Entry.ReferenceCount;
            Succeed(OutResult);
            return FFinareObjectHandle::Make(*ExistingSlot, Entry.Generation);
        }
        Impl->RetireSlotLocked(*ExistingSlot, Object);
    }

    uint32 SlotIndex;
    if (!Impl->FreeSlots.IsEmpty())
    {
        SlotIndex = Impl->FreeSlots.Pop(EAllowShrinking::No);
    }
    else
    {
        SlotIndex = static_cast<uint32>(Impl->Entries.AddDefaulted());
    }

    FImpl::FEntry& Entry = Impl->Entries[static_cast<int32>(SlotIndex)];
    Entry.Object = Object;
    Entry.Generation = AllocateGeneration();
    Entry.ReferenceCount = 1;
    Entry.bActive = true;
    Impl->SlotsByObject.Add(Object, SlotIndex);
    ++Impl->ActiveCount;
    Succeed(OutResult);
    return FFinareObjectHandle::Make(SlotIndex, Entry.Generation);
}

UObject* FFinareObjectRegistry::ResolveObject(
    FFinareObjectHandle Handle,
    FFinareObjectHandleResult& OutResult)
{
    using namespace UE::Finare::Private;

    if (!Impl->ValidateThread(OutResult))
    {
        return nullptr;
    }

    FScopeLock Lock(&Impl->CriticalSection);
    UWorld* RegistryWorld = Impl->World.Get();
    if (RegistryWorld == nullptr)
    {
        Fail(
            OutResult,
            EFinareObjectHandleError::RegistryWorldUnavailable,
            TEXT("The Finare UObject registry world is no longer available."));
        return nullptr;
    }
    uint32 SlotIndex;
    if (!Impl->DecodeHandleLocked(Handle, SlotIndex, OutResult))
    {
        return nullptr;
    }

    UObject* Object = Impl->Entries[static_cast<int32>(SlotIndex)].Object.Get();
    if (Object == nullptr)
    {
        Impl->RetireSlotLocked(SlotIndex);
        Fail(
            OutResult,
            EFinareObjectHandleError::StaleHandle,
            TEXT("The UObject referenced by the Finare handle has been destroyed."));
        return nullptr;
    }
    if (UWorld* ObjectWorld = Object->GetWorld();
        ObjectWorld != nullptr && ObjectWorld != RegistryWorld)
    {
        Impl->RetireSlotLocked(SlotIndex, Object);
        Fail(
            OutResult,
            EFinareObjectHandleError::WorldMismatch,
            TEXT("The UObject no longer belongs to the registry world."));
        return nullptr;
    }

    Succeed(OutResult);
    return Object;
}

bool FFinareObjectRegistry::ReleaseObject(
    FFinareObjectHandle Handle,
    FFinareObjectHandleResult& OutResult)
{
    using namespace UE::Finare::Private;

    if (!Impl->ValidateThread(OutResult) || Impl->GetWorld(OutResult) == nullptr)
    {
        return false;
    }

    FScopeLock Lock(&Impl->CriticalSection);
    uint32 SlotIndex;
    if (!Impl->DecodeHandleLocked(Handle, SlotIndex, OutResult))
    {
        return false;
    }

    FImpl::FEntry& Entry = Impl->Entries[static_cast<int32>(SlotIndex)];
    check(Entry.ReferenceCount > 0);
    if (--Entry.ReferenceCount > 0)
    {
        Succeed(OutResult);
        return true;
    }
    Impl->RetireSlotLocked(SlotIndex);
    Succeed(OutResult);
    return true;
}

bool FFinareObjectRegistry::OpenReferenceScope(
    uint64& OutScope,
    FFinareObjectHandleResult& OutResult)
{
    using namespace UE::Finare::Private;
    OutScope = 0;
    if (!Impl->ValidateThread(OutResult) || Impl->GetWorld(OutResult) == nullptr)
    {
        return false;
    }
    FScopeLock Lock(&Impl->CriticalSection);
    OutScope = AllocateReferenceScope();
    Impl->ReferenceScopes.Add(OutScope);
    Succeed(OutResult);
    return true;
}

bool FFinareObjectRegistry::CloseReferenceScope(
    uint64 Scope,
    FFinareObjectHandleResult& OutResult)
{
    using namespace UE::Finare::Private;
    if (!Impl->ValidateThread(OutResult))
    {
        return false;
    }
    FScopeLock Lock(&Impl->CriticalSection);
    TMap<uint64, uint32>* Leases = Impl->ReferenceScopes.Find(Scope);
    if (Leases == nullptr)
    {
        Fail(OutResult, EFinareObjectHandleError::InvalidScope,
            TEXT("The UObject reference scope is invalid or already closed."));
        return false;
    }
    TArray<TPair<uint64, uint32>> LeasesToRelease;
    LeasesToRelease.Reserve(Leases->Num());
    for (const auto& Lease : *Leases)
    {
        LeasesToRelease.Add(Lease);
    }
    for (const auto& Lease : LeasesToRelease)
    {
        FFinareObjectHandle Handle{Lease.Key};
        uint32 SlotIndex = 0;
        if (!Impl->DecodeHandleLocked(Handle, SlotIndex, OutResult))
        {
            OutResult = {};
            continue;
        }
        FImpl::FEntry& Entry = Impl->Entries[static_cast<int32>(SlotIndex)];
        const uint32 Count = FMath::Min(Lease.Value, Entry.ReferenceCount);
        Entry.ReferenceCount -= Count;
        if (Entry.ReferenceCount == 0)
        {
            Impl->RetireSlotLocked(SlotIndex);
        }
    }
    Impl->ReferenceScopes.Remove(Scope);
    Succeed(OutResult);
    return true;
}

bool FFinareObjectRegistry::RetainReference(
    uint64 Scope,
    FFinareObjectHandle Handle,
    FFinareObjectHandleResult& OutResult)
{
    using namespace UE::Finare::Private;
    if (!Impl->ValidateThread(OutResult) || Impl->GetWorld(OutResult) == nullptr)
    {
        return false;
    }
    FScopeLock Lock(&Impl->CriticalSection);
    if (!Impl->ReferenceScopes.Contains(Scope))
    {
        Fail(OutResult, EFinareObjectHandleError::InvalidScope,
            TEXT("The UObject reference scope is invalid or already closed."));
        return false;
    }
    uint32 SlotIndex = 0;
    if (!Impl->DecodeHandleLocked(Handle, SlotIndex, OutResult))
    {
        return false;
    }
    UObject* Object = Impl->Entries[static_cast<int32>(SlotIndex)].Object.Get();
    UWorld* RegistryWorld = Impl->World.Get();
    if (Object == nullptr || RegistryWorld == nullptr)
    {
        Fail(OutResult, EFinareObjectHandleError::StaleHandle,
            TEXT("The UObject reference is no longer available."));
        return false;
    }
    if (UWorld* ObjectWorld = Object->GetWorld(); ObjectWorld != nullptr && ObjectWorld != RegistryWorld)
    {
        Fail(OutResult, EFinareObjectHandleError::WorldMismatch,
            TEXT("The UObject reference belongs to another world."));
        return false;
    }
    ++Impl->Entries[static_cast<int32>(SlotIndex)].ReferenceCount;
    ++Impl->ReferenceScopes[Scope].FindOrAdd(Handle.Value);
    Succeed(OutResult);
    return true;
}

bool FFinareObjectRegistry::ReleaseReference(
    uint64 Scope,
    FFinareObjectHandle Handle,
    FFinareObjectHandleResult& OutResult)
{
    using namespace UE::Finare::Private;
    if (!Impl->ValidateThread(OutResult))
    {
        return false;
    }
    FScopeLock Lock(&Impl->CriticalSection);
    TMap<uint64, uint32>* Leases = Impl->ReferenceScopes.Find(Scope);
    if (Leases == nullptr)
    {
        Fail(OutResult, EFinareObjectHandleError::InvalidScope,
            TEXT("The UObject reference scope is invalid or already closed."));
        return false;
    }
    uint32* LeaseCount = Leases->Find(Handle.Value);
    if (LeaseCount == nullptr)
    {
        Fail(OutResult, EFinareObjectHandleError::InvalidHandle,
            TEXT("The UObject reference lease was already released."));
        return false;
    }
    --*LeaseCount;
    if (*LeaseCount == 0) Leases->Remove(Handle.Value);
    uint32 SlotIndex = 0;
    if (!Impl->DecodeHandleLocked(Handle, SlotIndex, OutResult))
    {
        // UObject deletion retires leases eagerly. Releasing such a lease is
        // idempotent from the managed owner's point of view.
        OutResult = {};
        Succeed(OutResult);
        return true;
    }
    FImpl::FEntry& Entry = Impl->Entries[static_cast<int32>(SlotIndex)];
    check(Entry.ReferenceCount > 0);
    --Entry.ReferenceCount;
    if (Entry.ReferenceCount == 0) Impl->RetireSlotLocked(SlotIndex);
    Succeed(OutResult);
    return true;
}

bool FFinareObjectRegistry::InvalidateAll(FFinareObjectHandleResult& OutResult)
{
    using namespace UE::Finare::Private;

    if (!Impl->ValidateThread(OutResult))
    {
        return false;
    }

    FScopeLock Lock(&Impl->CriticalSection);
    Impl->InvalidateAllLocked();
    Succeed(OutResult);
    return true;
}

int32 FFinareObjectRegistry::Num() const
{
    if (!IsInGameThread())
    {
        return 0;
    }

    FScopeLock Lock(&Impl->CriticalSection);
    return Impl->ActiveCount;
}

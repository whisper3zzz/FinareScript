#include "UnifiedScriptObjectRegistry.h"

#include "Engine/World.h"
#include "HAL/CriticalSection.h"
#include "Misc/ScopeLock.h"
#include "UObject/UObjectArray.h"
#include "UObject/WeakObjectPtr.h"

#include <atomic>

namespace UE::UnifiedScript::Private
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

void Succeed(FUnifiedScriptObjectHandleResult& OutResult)
{
    OutResult.Error = EUnifiedScriptObjectHandleError::None;
    OutResult.Message.Reset();
}

void Fail(
    FUnifiedScriptObjectHandleResult& OutResult,
    EUnifiedScriptObjectHandleError Error,
    const TCHAR* Message)
{
    OutResult.Error = Error;
    OutResult.Message = Message;
}
}

struct FUnifiedScriptObjectRegistry::FImpl final : FUObjectArray::FUObjectDeleteListener
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

    bool ValidateThread(FUnifiedScriptObjectHandleResult& OutResult) const
    {
        if (IsInGameThread())
        {
            return true;
        }

        UE::UnifiedScript::Private::Fail(
            OutResult,
            EUnifiedScriptObjectHandleError::NotGameThread,
            TEXT("UnifiedScript UObject handles may only be accessed on the game thread."));
        return false;
    }

    UWorld* GetWorld(FUnifiedScriptObjectHandleResult& OutResult) const
    {
        FScopeLock Lock(&CriticalSection);
        UWorld* RegistryWorld = World.Get();
        if (RegistryWorld != nullptr)
        {
            return RegistryWorld;
        }

        UE::UnifiedScript::Private::Fail(
            OutResult,
            EUnifiedScriptObjectHandleError::RegistryWorldUnavailable,
            TEXT("The UnifiedScript UObject registry world is no longer available."));
        return nullptr;
    }

    bool DecodeHandleLocked(
        FUnifiedScriptObjectHandle Handle,
        uint32& OutSlotIndex,
        FUnifiedScriptObjectHandleResult& OutResult) const
    {
        if (!Handle.IsWellFormed())
        {
            UE::UnifiedScript::Private::Fail(
                OutResult,
                EUnifiedScriptObjectHandleError::InvalidHandle,
                TEXT("The UnifiedScript UObject handle encoding is invalid."));
            return false;
        }

        OutSlotIndex = Handle.GetSlotIndex();
        if (!Entries.IsValidIndex(static_cast<int32>(OutSlotIndex)))
        {
            UE::UnifiedScript::Private::Fail(
                OutResult,
                EUnifiedScriptObjectHandleError::StaleHandle,
                TEXT("The UnifiedScript UObject handle refers to an unavailable slot."));
            return false;
        }

        const FEntry& Entry = Entries[static_cast<int32>(OutSlotIndex)];
        if (!Entry.bActive || Entry.Generation != Handle.GetGeneration())
        {
            UE::UnifiedScript::Private::Fail(
                OutResult,
                EUnifiedScriptObjectHandleError::StaleHandle,
                TEXT("The UnifiedScript UObject handle generation is stale."));
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
        FreeSlots.Add(SlotIndex);
        --ActiveCount;
    }

    void InvalidateAllLocked()
    {
        SlotsByObject.Reset();
        FreeSlots.Reset();
        ActiveCount = 0;
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
    bool bDeleteListenerRegistered = false;
};

FUnifiedScriptObjectRegistry::FUnifiedScriptObjectRegistry(UWorld* InWorld)
    : Impl(MakeUnique<FImpl>(InWorld))
{
}

FUnifiedScriptObjectRegistry::~FUnifiedScriptObjectRegistry() = default;

FUnifiedScriptObjectHandle FUnifiedScriptObjectRegistry::RegisterObject(
    UObject* Object,
    FUnifiedScriptObjectHandleResult& OutResult)
{
    using namespace UE::UnifiedScript::Private;

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
            EUnifiedScriptObjectHandleError::NullObject,
            TEXT("A live UObject is required to create a UnifiedScript handle."));
        return {};
    }
    if (UWorld* ObjectWorld = Object->GetWorld();
        ObjectWorld != nullptr && ObjectWorld != RegistryWorld)
    {
        Fail(
            OutResult,
            EUnifiedScriptObjectHandleError::WorldMismatch,
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
            return FUnifiedScriptObjectHandle::Make(*ExistingSlot, Entry.Generation);
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
    return FUnifiedScriptObjectHandle::Make(SlotIndex, Entry.Generation);
}

UObject* FUnifiedScriptObjectRegistry::ResolveObject(
    FUnifiedScriptObjectHandle Handle,
    FUnifiedScriptObjectHandleResult& OutResult)
{
    using namespace UE::UnifiedScript::Private;

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
            EUnifiedScriptObjectHandleError::RegistryWorldUnavailable,
            TEXT("The UnifiedScript UObject registry world is no longer available."));
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
            EUnifiedScriptObjectHandleError::StaleHandle,
            TEXT("The UObject referenced by the UnifiedScript handle has been destroyed."));
        return nullptr;
    }
    if (UWorld* ObjectWorld = Object->GetWorld();
        ObjectWorld != nullptr && ObjectWorld != RegistryWorld)
    {
        Impl->RetireSlotLocked(SlotIndex, Object);
        Fail(
            OutResult,
            EUnifiedScriptObjectHandleError::WorldMismatch,
            TEXT("The UObject no longer belongs to the registry world."));
        return nullptr;
    }

    Succeed(OutResult);
    return Object;
}

bool FUnifiedScriptObjectRegistry::ReleaseObject(
    FUnifiedScriptObjectHandle Handle,
    FUnifiedScriptObjectHandleResult& OutResult)
{
    using namespace UE::UnifiedScript::Private;

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

bool FUnifiedScriptObjectRegistry::InvalidateAll(FUnifiedScriptObjectHandleResult& OutResult)
{
    using namespace UE::UnifiedScript::Private;

    if (!Impl->ValidateThread(OutResult))
    {
        return false;
    }

    FScopeLock Lock(&Impl->CriticalSection);
    Impl->InvalidateAllLocked();
    Succeed(OutResult);
    return true;
}

int32 FUnifiedScriptObjectRegistry::Num() const
{
    if (!IsInGameThread())
    {
        return 0;
    }

    FScopeLock Lock(&Impl->CriticalSection);
    return Impl->ActiveCount;
}

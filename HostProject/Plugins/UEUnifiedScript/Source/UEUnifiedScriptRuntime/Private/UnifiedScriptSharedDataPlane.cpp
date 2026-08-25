#include "UnifiedScriptSharedDataPlane.h"

#include "Engine/World.h"
#include "EngineUtils.h"
#include "GameFramework/Actor.h"
#include "Misc/ScopeRWLock.h"

namespace UE::UnifiedScript::SharedData::Private
{
constexpr int32 InitialActorCapacity = 256;

FUnifiedScriptTransformSnapshotEntryV4 EncodeTransform(
    uint64 WorldHostId,
    FUnifiedScriptObjectHandle Handle,
    const FTransform& Transform)
{
    const FVector Translation = Transform.GetTranslation();
    const FQuat Rotation = Transform.GetRotation();
    const FVector Scale = Transform.GetScale3D();
    return {
        {WorldHostId, Handle.Value},
        Translation.X,
        Translation.Y,
        Translation.Z,
        Rotation.X,
        Rotation.Y,
        Rotation.Z,
        Rotation.W,
        Scale.X,
        Scale.Y,
        Scale.Z,
    };
}
}

FUnifiedScriptSharedDataPlane::FUnifiedScriptSharedDataPlane(
    UWorld& InWorld,
    FUnifiedScriptObjectRegistry& InObjectRegistry,
    uint64 InWorldHostId)
    : World(&InWorld)
    , ObjectRegistry(&InObjectRegistry)
    , WorldHostId(InWorldHostId)
{
    check(IsInGameThread());
    check(InWorldHostId != 0);
    Buffers[0].Reserve(UE::UnifiedScript::SharedData::Private::InitialActorCapacity);
    Buffers[1].Reserve(UE::UnifiedScript::SharedData::Private::InitialActorCapacity);
    SeenActors.Reserve(UE::UnifiedScript::SharedData::Private::InitialActorCapacity);
}

FUnifiedScriptSharedDataPlane::~FUnifiedScriptSharedDataPlane()
{
    Shutdown();
}

void FUnifiedScriptSharedDataPlane::PublishTransformSnapshot()
{
    check(IsInGameThread());
    FWriteScopeLock Lock(SnapshotLock);
    UWorld* CurrentWorld = World.Get();
    if (bShutdown || CurrentWorld == nullptr || ObjectRegistry == nullptr)
    {
        return;
    }

    const int32 WriteBufferIndex = 1 - ActiveBufferIndex;
    FSnapshotBuffer& WriteBuffer = Buffers[WriteBufferIndex];
    WriteBuffer.Reset();
    SeenActors.Reset();

    for (TActorIterator<AActor> It(CurrentWorld); It; ++It)
    {
        AActor* Actor = *It;
        if (!IsValid(Actor))
        {
            continue;
        }

        const TWeakObjectPtr<AActor> WeakActor(Actor);
        SeenActors.Add(WeakActor);
        FUnifiedScriptObjectHandle* ExistingHandle = ActorHandles.Find(WeakActor);
        if (ExistingHandle == nullptr)
        {
            FUnifiedScriptObjectHandleResult Result;
            const FUnifiedScriptObjectHandle NewHandle =
                ObjectRegistry->RegisterObject(Actor, Result);
            if (!Result.IsSuccess())
            {
                continue;
            }
            ExistingHandle = &ActorHandles.Add(WeakActor, NewHandle);
        }

        WriteBuffer.Add(UE::UnifiedScript::SharedData::Private::EncodeTransform(
            WorldHostId,
            *ExistingHandle,
            Actor->GetActorTransform()));
    }

    for (auto It = ActorHandles.CreateIterator(); It; ++It)
    {
        if (SeenActors.Contains(It.Key()))
        {
            continue;
        }
        if (It.Key().IsValid())
        {
            FUnifiedScriptObjectHandleResult ReleaseResult;
            ObjectRegistry->ReleaseObject(It.Value(), ReleaseResult);
        }
        It.RemoveCurrent();
    }

    ActiveBufferIndex = WriteBufferIndex;
    ++PublishedGeneration;
    if (PublishedGeneration == 0)
    {
        ++PublishedGeneration;
    }
}

bool FUnifiedScriptSharedDataPlane::AcquireTransformSnapshot(
    FUnifiedScriptSharedSnapshotViewV4& OutView) const
{
    FReadScopeLock Lock(SnapshotLock);
    if (bShutdown || WorldHostId == 0)
    {
        OutView = {};
        return false;
    }

    const FSnapshotBuffer& ActiveBuffer = Buffers[ActiveBufferIndex];
    OutView = {
        UE::UnifiedScript::SharedData::AbiVersion,
        sizeof(FUnifiedScriptSharedSnapshotViewV4),
        UE::UnifiedScript::SharedData::TransformSnapshotSchemaId,
        WorldHostId,
        PublishedGeneration,
        ActiveBuffer.GetData(),
        static_cast<uint32>(ActiveBuffer.Num()),
        static_cast<uint32>(ActiveBuffer.Max()),
        sizeof(FUnifiedScriptTransformSnapshotEntryV4),
        EUnifiedScriptSharedSnapshotFlagsV4::BorrowedUntilNextPublish,
    };
    return true;
}

void FUnifiedScriptSharedDataPlane::Shutdown()
{
    check(IsInGameThread());
    FWriteScopeLock Lock(SnapshotLock);
    if (bShutdown)
    {
        return;
    }
    bShutdown = true;

    if (ObjectRegistry != nullptr)
    {
        for (const TPair<TWeakObjectPtr<AActor>, FUnifiedScriptObjectHandle>& Pair :
             ActorHandles)
        {
            if (!Pair.Key.IsValid())
            {
                continue;
            }
            FUnifiedScriptObjectHandleResult Result;
            ObjectRegistry->ReleaseObject(Pair.Value, Result);
        }
    }
    ActorHandles.Reset();
    SeenActors.Reset();
    Buffers[0].Reset();
    Buffers[1].Reset();
    World.Reset();
    ObjectRegistry = nullptr;
    WorldHostId = 0;
    PublishedGeneration = 0;
}

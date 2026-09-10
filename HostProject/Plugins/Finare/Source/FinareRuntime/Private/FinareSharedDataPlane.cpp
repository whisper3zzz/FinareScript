#include "FinareSharedDataPlane.h"

#include "Engine/World.h"
#include "EngineUtils.h"
#include "GameFramework/Actor.h"
#include "Misc/ScopeRWLock.h"

namespace UE::Finare::SharedData::Private
{
constexpr int32 InitialActorCapacity = 256;

FFinareTransformSnapshotEntryV4 EncodeTransform(
    uint64 WorldHostId,
    FFinareObjectHandle Handle,
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

FFinareSharedDataPlane::FFinareSharedDataPlane(
    UWorld& InWorld,
    FFinareObjectRegistry& InObjectRegistry,
    uint64 InWorldHostId)
    : World(&InWorld)
    , ObjectRegistry(&InObjectRegistry)
    , WorldHostId(InWorldHostId)
{
    check(IsInGameThread());
    check(InWorldHostId != 0);
    Buffers[0].Reserve(UE::Finare::SharedData::Private::InitialActorCapacity);
    Buffers[1].Reserve(UE::Finare::SharedData::Private::InitialActorCapacity);
    SeenActors.Reserve(UE::Finare::SharedData::Private::InitialActorCapacity);
}

FFinareSharedDataPlane::~FFinareSharedDataPlane()
{
    Shutdown();
}

void FFinareSharedDataPlane::PublishTransformSnapshot()
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

        if (WriteBuffer.Num() >= static_cast<int32>(
                UE::Finare::SharedData::MaximumTransformSnapshotCount))
        {
            ++DroppedSnapshotEntryCount;
            continue;
        }

        const TWeakObjectPtr<AActor> WeakActor(Actor);
        SeenActors.Add(WeakActor);
        FFinareObjectHandle* ExistingHandle = ActorHandles.Find(WeakActor);
        if (ExistingHandle == nullptr)
        {
            FFinareObjectHandleResult Result;
            const FFinareObjectHandle NewHandle =
                ObjectRegistry->RegisterObject(Actor, Result);
            if (!Result.IsSuccess())
            {
                continue;
            }
            ExistingHandle = &ActorHandles.Add(WeakActor, NewHandle);
        }

        WriteBuffer.Add(UE::Finare::SharedData::Private::EncodeTransform(
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
            FFinareObjectHandleResult ReleaseResult;
            ObjectRegistry->ReleaseObject(It.Value(), ReleaseResult);
        }
        It.RemoveCurrent();
    }

    ActiveBufferIndex = WriteBufferIndex;
    TotalPublishedEntryCount += static_cast<uint64>(WriteBuffer.Num());
    ++PublishedGeneration;
    if (PublishedGeneration == 0)
    {
        ++PublishedGeneration;
    }
}

bool FFinareSharedDataPlane::AcquireTransformSnapshot(
    FFinareSharedSnapshotViewV4& OutView) const
{
    FReadScopeLock Lock(SnapshotLock);
    if (bShutdown || WorldHostId == 0)
    {
        OutView = {};
        return false;
    }

    const FSnapshotBuffer& ActiveBuffer = Buffers[ActiveBufferIndex];
    OutView = {
        UE::Finare::SharedData::AbiVersion,
        sizeof(FFinareSharedSnapshotViewV4),
        UE::Finare::SharedData::TransformSnapshotSchemaId,
        WorldHostId,
        PublishedGeneration,
        ActiveBuffer.GetData(),
        static_cast<uint32>(ActiveBuffer.Num()),
        static_cast<uint32>(ActiveBuffer.Max()),
        sizeof(FFinareTransformSnapshotEntryV4),
        EFinareSharedSnapshotFlagsV4::BorrowedUntilNextPublish,
    };
    return true;
}

bool FFinareSharedDataPlane::PublishGeneratedSnapshot(
    uint64 SchemaId,
    uint32 EntrySize,
    const void* Entries,
    uint32 EntryCount)
{
    check(IsInGameThread());
    FWriteScopeLock Lock(SnapshotLock);
    if (bShutdown || WorldHostId == 0 || SchemaId == 0 || EntrySize == 0)
    {
        return false;
    }

    FGeneratedSnapshotChannel* Channel = GeneratedSnapshotChannels.Find(SchemaId);
    const uint64 ByteCount = static_cast<uint64>(EntrySize) * EntryCount;
    if (EntryCount > UE::Finare::SharedData::MaximumGeneratedSnapshotCount ||
        ByteCount > UE::Finare::SharedData::MaximumGeneratedChannelBytes ||
        (EntryCount > 0 && Entries == nullptr))
    {
        if (Channel != nullptr)
        {
            Channel->DroppedEntryCount =
                EntryCount > MAX_uint64 - Channel->DroppedEntryCount
                    ? MAX_uint64
                    : Channel->DroppedEntryCount + EntryCount;
        }
        return false;
    }
    if (Channel == nullptr)
    {
        if (GeneratedSnapshotChannels.Num() >= static_cast<int32>(
                UE::Finare::SharedData::MaximumGeneratedChannelCount))
        {
            return false;
        }
        Channel = &GeneratedSnapshotChannels.Add(SchemaId);
        Channel->EntrySize = EntrySize;
    }
    else if (Channel->EntrySize != EntrySize)
    {
        return false;
    }

    const int32 WriteIndex = 1 - Channel->ActiveBufferIndex;
    TArray<uint8>& WriteBuffer = Channel->Buffers[WriteIndex];
    WriteBuffer.SetNumUninitialized(static_cast<int32>(ByteCount));
    if (ByteCount > 0)
    {
        FMemory::Memcpy(WriteBuffer.GetData(), Entries, static_cast<SIZE_T>(ByteCount));
    }
    Channel->EntryCounts[WriteIndex] = EntryCount;
    Channel->ActiveBufferIndex = WriteIndex;
    ++Channel->PublicationId;
    if (Channel->PublicationId == 0)
    {
        ++Channel->PublicationId;
    }
    return true;
}

bool FFinareSharedDataPlane::AcquireGeneratedSnapshot(
    uint64 SchemaId,
    uint32 EntrySize,
    FFinareGeneratedDataViewV1& OutView) const
{
    FReadScopeLock Lock(SnapshotLock);
    const FGeneratedSnapshotChannel* Channel = GeneratedSnapshotChannels.Find(SchemaId);
    if (bShutdown || WorldHostId == 0 || SchemaId == 0 || EntrySize == 0 ||
        Channel == nullptr || Channel->EntrySize != EntrySize ||
        Channel->PublicationId == 0)
    {
        OutView = {};
        return false;
    }

    const TArray<uint8>& ActiveBuffer = Channel->Buffers[Channel->ActiveBufferIndex];
    OutView = {
        UE::Finare::SharedData::AbiVersion,
        sizeof(FFinareGeneratedDataViewV1),
        SchemaId,
        WorldHostId,
        Channel->PublicationId,
        ActiveBuffer.GetData(),
        Channel->EntryCounts[Channel->ActiveBufferIndex],
        Channel->EntryCounts[Channel->ActiveBufferIndex],
        EntrySize,
        static_cast<EFinareGeneratedDataFlagsV1>(3u),
        Channel->DroppedEntryCount,
    };
    return true;
}

bool FFinareSharedDataPlane::GetTelemetry(
    FFinareSharedDataTelemetryV1& OutTelemetry) const
{
    FReadScopeLock Lock(SnapshotLock);
    if (bShutdown || WorldHostId == 0)
    {
        OutTelemetry = {};
        return false;
    }

    const FSnapshotBuffer& ActiveBuffer = Buffers[ActiveBufferIndex];
    OutTelemetry = {
        UE::Finare::DataPlaneSchema::Revision,
        sizeof(FFinareSharedDataTelemetryV1),
        UE::Finare::DataPlaneSchema::CompatibilityId,
        WorldHostId,
        PublishedGeneration,
        TotalPublishedEntryCount,
        DroppedSnapshotEntryCount,
        static_cast<uint32>(ActiveBuffer.Num()),
        UE::Finare::SharedData::MaximumTransformSnapshotCount,
    };
    return true;
}

void FFinareSharedDataPlane::Shutdown()
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
        for (const TPair<TWeakObjectPtr<AActor>, FFinareObjectHandle>& Pair :
             ActorHandles)
        {
            if (!Pair.Key.IsValid())
            {
                continue;
            }
            FFinareObjectHandleResult Result;
            ObjectRegistry->ReleaseObject(Pair.Value, Result);
        }
    }
    ActorHandles.Reset();
    SeenActors.Reset();
    Buffers[0].Reset();
    Buffers[1].Reset();
    GeneratedSnapshotChannels.Reset();
    World.Reset();
    ObjectRegistry = nullptr;
    WorldHostId = 0;
    PublishedGeneration = 0;
}

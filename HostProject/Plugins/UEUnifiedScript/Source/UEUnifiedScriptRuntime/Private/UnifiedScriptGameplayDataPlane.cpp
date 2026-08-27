#include "UnifiedScriptGameplayDataPlane.h"

#include "Components/PrimitiveComponent.h"
#include "Engine/DamageEvents.h"
#include "Engine/OverlapResult.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "GameFramework/Controller.h"
#include "Kismet/GameplayStatics.h"
#include "Misc/App.h"
#include "Particles/ParticleSystem.h"
#include "Particles/ParticleSystemComponent.h"

namespace UE::UnifiedScript::GameplayData::Private
{
constexpr int32 InitialCommandCapacity = 256;
constexpr int32 InitialTraceCapacity = 256;
constexpr int32 InitialOverlapCapacity = 256;

uint64 AllocateNonZero(uint64& Next)
{
    const uint64 Result = Next++;
    if (Next == 0)
    {
        Next = 1;
    }
    return Result == 0 ? Next++ : Result;
}

EUnifiedScriptCommandStatusV4 MapHandleError(
    EUnifiedScriptObjectHandleError Error)
{
    return Error == EUnifiedScriptObjectHandleError::WorldMismatch
        ? EUnifiedScriptCommandStatusV4::WorldMismatch
        : EUnifiedScriptCommandStatusV4::StaleHandle;
}

FTransform DecodeTransform(const FUnifiedScriptCommandV4& Command)
{
    return FTransform(
        FQuat(
            Command.RotationX,
            Command.RotationY,
            Command.RotationZ,
            Command.RotationW).GetNormalized(),
        FVector(Command.Vector0X, Command.Vector0Y, Command.Vector0Z),
        FVector(Command.Vector2X, Command.Vector2Y, Command.Vector2Z));
}

FUnifiedScriptGameplayEventV4 MakeCommandEvent(
    const FUnifiedScriptCommandV4& Command,
    uint64 FrameNumber)
{
    FUnifiedScriptGameplayEventV4 Event;
    Event.SequenceId = Command.SequenceId;
    Event.Kind = EUnifiedScriptGameplayEventKindV4::CommandCompleted;
    Event.CommandType = Command.Type;
    Event.Source = Command.Target;
    Event.FrameNumber = FrameNumber;
    return Event;
}
}

FUnifiedScriptGameplayDataPlane::FUnifiedScriptGameplayDataPlane(
    UWorld& InWorld,
    FUnifiedScriptObjectRegistry& InObjectRegistry,
    uint64 InWorldHostId)
    : World(&InWorld)
    , ObjectRegistry(&InObjectRegistry)
    , WorldHostId(InWorldHostId)
{
    check(IsInGameThread());
    check(InWorldHostId != 0);
    Commands.Reserve(UE::UnifiedScript::GameplayData::Private::InitialCommandCapacity);
    CommandReservations.Reserve(32);
    ReservationIndices.Reserve(32);
    EventBuffers[0].Reserve(UE::UnifiedScript::GameplayData::MaximumEventCount);
    EventBuffers[1].Reserve(UE::UnifiedScript::GameplayData::MaximumEventCount);
    EventOwnedHandles[0].Reserve(UE::UnifiedScript::GameplayData::MaximumEventCount);
    EventOwnedHandles[1].Reserve(UE::UnifiedScript::GameplayData::MaximumEventCount);
    PendingEvents.Reserve(UE::UnifiedScript::GameplayData::MaximumEventCount);
    PendingEventOwnedHandles.Reserve(UE::UnifiedScript::GameplayData::MaximumEventCount);
    TraceRequests.Reserve(UE::UnifiedScript::GameplayData::Private::InitialTraceCapacity);
    TraceResults.Reserve(UE::UnifiedScript::GameplayData::Private::InitialTraceCapacity);
    TraceOwnedHandles.Reserve(UE::UnifiedScript::GameplayData::Private::InitialTraceCapacity);
    OverlapRequests.Reserve(
        UE::UnifiedScript::GameplayData::Private::InitialOverlapCapacity);
    OverlapResults.Reserve(
        UE::UnifiedScript::GameplayData::Private::InitialOverlapCapacity);
    OverlapOwnedHandles.Reserve(
        UE::UnifiedScript::GameplayData::Private::InitialOverlapCapacity);
}

FUnifiedScriptGameplayDataPlane::~FUnifiedScriptGameplayDataPlane()
{
    Shutdown();
}

void FUnifiedScriptGameplayDataPlane::BeginFrame()
{
    check(IsInGameThread());
    if (bShutdown)
    {
        return;
    }
    if (Phase == EUnifiedScriptGameplayDataPhaseV4::ManagedTick)
    {
        EndFrame();
    }
    Phase = EUnifiedScriptGameplayDataPhaseV4::NativePreActorTick;
    ++FrameNumber;
    if (FrameNumber == 0)
    {
        ++FrameNumber;
    }
    PublishPendingEvents();
    Commands.Reset();
    CommandReservations.Reset();
    ReservationIndices.Reset();
    ActiveCommandBatchToken = 0;
    ActiveTraceBatchToken = 0;
    ActiveOverlapBatchToken = 0;
    Phase = EUnifiedScriptGameplayDataPhaseV4::ManagedTick;
}

void FUnifiedScriptGameplayDataPlane::EndFrame()
{
    check(IsInGameThread());
    if (bShutdown || Phase != EUnifiedScriptGameplayDataPhaseV4::ManagedTick)
    {
        return;
    }
    Phase = EUnifiedScriptGameplayDataPhaseV4::NativePostActorTick;
    ExecuteCommittedCommands();
    ActiveTraceBatchToken = 0;
    ActiveOverlapBatchToken = 0;
    Phase = EUnifiedScriptGameplayDataPhaseV4::Idle;
}

bool FUnifiedScriptGameplayDataPlane::BeginCommandBatch(
    uint32 Capacity,
    FUnifiedScriptCommandBatchViewV4& OutView)
{
    check(IsInGameThread());
    OutView = {};
    if (Capacity > UE::UnifiedScript::GameplayData::MaximumCommandCount ||
        static_cast<uint64>(Commands.Num()) + Capacity >
            UE::UnifiedScript::GameplayData::MaximumCommandCount)
    {
        ++CommandOverflowCount;
        return false;
    }
    if (!IsManagedPhase() || ActiveCommandBatchToken != 0 || Capacity == 0)
    {
        return false;
    }

    const uint32 Offset = static_cast<uint32>(Commands.Num());
    Commands.AddZeroed(static_cast<int32>(Capacity));
    FCommandReservation Reservation;
    Reservation.Token =
        UE::UnifiedScript::GameplayData::Private::AllocateNonZero(NextBatchToken);
    Reservation.FirstSequenceId = NextSequenceId;
    for (uint32 Index = 0; Index < Capacity; ++Index)
    {
        UE::UnifiedScript::GameplayData::Private::AllocateNonZero(NextSequenceId);
    }
    Reservation.Offset = Offset;
    Reservation.Capacity = Capacity;
    const int32 ReservationIndex = CommandReservations.Add(Reservation);
    ReservationIndices.Add(Reservation.Token, ReservationIndex);
    ActiveCommandBatchToken = Reservation.Token;

    OutView = {
        UE::UnifiedScript::GameplayData::AbiVersion,
        sizeof(FUnifiedScriptCommandBatchViewV4),
        UE::UnifiedScript::GameplayData::CommandSchemaId,
        WorldHostId,
        FrameNumber,
        Reservation.Token,
        Reservation.FirstSequenceId,
        Commands.GetData() + Offset,
        Capacity,
        sizeof(FUnifiedScriptCommandV4),
        0,
        0,
    };
    return true;
}

bool FUnifiedScriptGameplayDataPlane::CommitCommandBatch(
    uint64 BatchToken,
    uint32 CommandCount)
{
    check(IsInGameThread());
    if (!IsManagedPhase() || BatchToken == 0 ||
        BatchToken != ActiveCommandBatchToken)
    {
        return false;
    }
    const int32* ReservationIndex = ReservationIndices.Find(BatchToken);
    if (ReservationIndex == nullptr)
    {
        return false;
    }
    FCommandReservation& Reservation = CommandReservations[*ReservationIndex];
    if (Reservation.bCommitted || CommandCount > Reservation.Capacity)
    {
        return false;
    }
    Reservation.Count = CommandCount;
    Reservation.bCommitted = true;
    ActiveCommandBatchToken = 0;
    for (uint32 Index = 0; Index < CommandCount; ++Index)
    {
        Commands[static_cast<int32>(Reservation.Offset + Index)].SequenceId =
            Reservation.FirstSequenceId + Index;
    }
    TotalCommittedCommandCount += CommandCount;
    if (CommandCount > 0)
    {
        LastCommittedSequenceId = Reservation.FirstSequenceId + CommandCount - 1;
    }
    return true;
}

bool FUnifiedScriptGameplayDataPlane::AcquireEvents(
    FUnifiedScriptGameplayEventViewV4& OutView) const
{
    check(IsInGameThread());
    if (bShutdown || WorldHostId == 0)
    {
        OutView = {};
        return false;
    }
    const TArray<FUnifiedScriptGameplayEventV4>& ActiveEvents =
        EventBuffers[ActiveEventBufferIndex];
    OutView = {
        UE::UnifiedScript::GameplayData::AbiVersion,
        sizeof(FUnifiedScriptGameplayEventViewV4),
        UE::UnifiedScript::GameplayData::EventSchemaId,
        WorldHostId,
        FrameNumber,
        ActiveEvents.GetData(),
        static_cast<uint32>(ActiveEvents.Num()),
        static_cast<uint32>(ActiveEvents.Max()),
        sizeof(FUnifiedScriptGameplayEventV4),
        0,
        DroppedEventCount,
    };
    return true;
}

bool FUnifiedScriptGameplayDataPlane::BeginTraceBatch(
    uint32 Capacity,
    FUnifiedScriptTraceRequestViewV4& OutView)
{
    check(IsInGameThread());
    OutView = {};
    if (Capacity > UE::UnifiedScript::GameplayData::MaximumTraceCount)
    {
        ++TraceOverflowCount;
        return false;
    }
    if (!IsManagedPhase() || ActiveTraceBatchToken != 0 || Capacity == 0)
    {
        return false;
    }
    if (ObjectRegistry != nullptr)
    {
        for (FUnifiedScriptObjectHandle Handle : TraceOwnedHandles)
        {
            FUnifiedScriptObjectHandleResult Result;
            ObjectRegistry->ReleaseObject(Handle, Result);
        }
    }
    TraceOwnedHandles.Reset();
    TraceResults.Reset();
    TraceRequests.SetNumZeroed(static_cast<int32>(Capacity));
    ActiveTraceBatchToken =
        UE::UnifiedScript::GameplayData::Private::AllocateNonZero(NextBatchToken);
    OutView = {
        UE::UnifiedScript::GameplayData::AbiVersion,
        sizeof(FUnifiedScriptTraceRequestViewV4),
        UE::UnifiedScript::GameplayData::TraceRequestSchemaId,
        WorldHostId,
        FrameNumber,
        ActiveTraceBatchToken,
        TraceRequests.GetData(),
        Capacity,
        sizeof(FUnifiedScriptTraceRequestV4),
        0,
        0,
    };
    return true;
}

bool FUnifiedScriptGameplayDataPlane::ExecuteTraceBatch(
    uint64 BatchToken,
    uint32 RequestCount,
    FUnifiedScriptTraceResultViewV4& OutView)
{
    check(IsInGameThread());
    OutView = {};
    if (!IsManagedPhase() || BatchToken == 0 ||
        BatchToken != ActiveTraceBatchToken ||
        RequestCount > static_cast<uint32>(TraceRequests.Num()))
    {
        return false;
    }
    TraceResults.Reset();
    TraceResults.Reserve(static_cast<int32>(RequestCount));
    for (uint32 Index = 0; Index < RequestCount; ++Index)
    {
        FUnifiedScriptTraceRequestV4& Request = TraceRequests[static_cast<int32>(Index)];
        if (Request.SequenceId == 0)
        {
            Request.SequenceId =
                UE::UnifiedScript::GameplayData::Private::AllocateNonZero(NextSequenceId);
        }
        TraceResults.Add(ExecuteTrace(Request));
    }
    TotalExecutedTraceCount += RequestCount;
    ActiveTraceBatchToken = 0;
    OutView = {
        UE::UnifiedScript::GameplayData::AbiVersion,
        sizeof(FUnifiedScriptTraceResultViewV4),
        UE::UnifiedScript::GameplayData::TraceResultSchemaId,
        WorldHostId,
        FrameNumber,
        BatchToken,
        TraceResults.GetData(),
        static_cast<uint32>(TraceResults.Num()),
        static_cast<uint32>(TraceResults.Max()),
        sizeof(FUnifiedScriptTraceResultV4),
        0,
    };
    return true;
}

void FUnifiedScriptGameplayDataPlane::CancelTraceBatch(uint64 BatchToken)
{
    check(IsInGameThread());
    if (BatchToken != 0 && BatchToken == ActiveTraceBatchToken)
    {
        ActiveTraceBatchToken = 0;
    }
}

bool FUnifiedScriptGameplayDataPlane::BeginOverlapBatch(
    uint32 Capacity,
    FUnifiedScriptOverlapRequestViewV1& OutView)
{
    check(IsInGameThread());
    OutView = {};
    if (Capacity > UE::UnifiedScript::GameplayData::MaximumOverlapCount)
    {
        ++OverlapOverflowCount;
        return false;
    }
    if (!IsManagedPhase() || ActiveOverlapBatchToken != 0 || Capacity == 0)
    {
        return false;
    }
    if (ObjectRegistry != nullptr)
    {
        for (FUnifiedScriptObjectHandle Handle : OverlapOwnedHandles)
        {
            FUnifiedScriptObjectHandleResult Result;
            ObjectRegistry->ReleaseObject(Handle, Result);
        }
    }
    OverlapOwnedHandles.Reset();
    OverlapResults.Reset();
    OverlapRequests.SetNumZeroed(static_cast<int32>(Capacity));
    ActiveOverlapBatchToken =
        UE::UnifiedScript::GameplayData::Private::AllocateNonZero(NextBatchToken);
    OutView = {
        UE::UnifiedScript::GameplayData::AbiVersion,
        sizeof(FUnifiedScriptOverlapRequestViewV1),
        UE::UnifiedScript::GameplayData::OverlapRequestSchemaId,
        WorldHostId,
        FrameNumber,
        ActiveOverlapBatchToken,
        OverlapRequests.GetData(),
        Capacity,
        sizeof(FUnifiedScriptOverlapRequestV1),
        0,
        0,
    };
    return true;
}

bool FUnifiedScriptGameplayDataPlane::ExecuteOverlapBatch(
    uint64 BatchToken,
    uint32 RequestCount,
    FUnifiedScriptOverlapResultViewV1& OutView)
{
    check(IsInGameThread());
    OutView = {};
    if (!IsManagedPhase() || BatchToken == 0 ||
        BatchToken != ActiveOverlapBatchToken ||
        RequestCount > static_cast<uint32>(OverlapRequests.Num()))
    {
        return false;
    }
    OverlapResults.Reset();
    OverlapResults.Reserve(static_cast<int32>(RequestCount));
    for (uint32 Index = 0; Index < RequestCount; ++Index)
    {
        FUnifiedScriptOverlapRequestV1& Request =
            OverlapRequests[static_cast<int32>(Index)];
        if (Request.SequenceId == 0)
        {
            Request.SequenceId =
                UE::UnifiedScript::GameplayData::Private::AllocateNonZero(
                    NextSequenceId);
        }
        OverlapResults.Add(ExecuteOverlap(Request));
    }
    TotalExecutedOverlapCount += RequestCount;
    ActiveOverlapBatchToken = 0;
    OutView = {
        UE::UnifiedScript::GameplayData::AbiVersion,
        sizeof(FUnifiedScriptOverlapResultViewV1),
        UE::UnifiedScript::GameplayData::OverlapResultSchemaId,
        WorldHostId,
        FrameNumber,
        BatchToken,
        OverlapResults.GetData(),
        static_cast<uint32>(OverlapResults.Num()),
        static_cast<uint32>(OverlapResults.Max()),
        sizeof(FUnifiedScriptOverlapResultV1),
        0,
    };
    return true;
}

void FUnifiedScriptGameplayDataPlane::CancelOverlapBatch(uint64 BatchToken)
{
    check(IsInGameThread());
    if (BatchToken != 0 && BatchToken == ActiveOverlapBatchToken)
    {
        ActiveOverlapBatchToken = 0;
    }
}

EUnifiedScriptGameplayDataPhaseV4 FUnifiedScriptGameplayDataPlane::GetPhase() const
{
    return Phase;
}

uint64 FUnifiedScriptGameplayDataPlane::GetFrameNumber() const
{
    return FrameNumber;
}

bool FUnifiedScriptGameplayDataPlane::GetTelemetry(
    FUnifiedScriptGameplayDataTelemetryV1& OutTelemetry) const
{
    check(IsInGameThread());
    if (bShutdown || WorldHostId == 0)
    {
        OutTelemetry = {};
        return false;
    }
    OutTelemetry = {
        UE::UnifiedScript::DataPlaneSchema::Revision,
        sizeof(FUnifiedScriptGameplayDataTelemetryV1),
        UE::UnifiedScript::DataPlaneSchema::CompatibilityId,
        WorldHostId,
        FrameNumber,
        CommandOverflowCount,
        DroppedEventCount,
        TraceOverflowCount,
        TotalCommittedCommandCount,
        TotalExecutedCommandCount,
        TotalExecutedTraceCount,
        LastCommittedSequenceId,
        LastExecutedSequenceId,
        LastPublishedEventFrame,
        UE::UnifiedScript::GameplayData::MaximumCommandCount,
        UE::UnifiedScript::GameplayData::MaximumEventCount,
        UE::UnifiedScript::GameplayData::MaximumTraceCount,
        static_cast<uint32>(Phase),
        OverlapOverflowCount,
        TotalExecutedOverlapCount,
        UE::UnifiedScript::GameplayData::MaximumOverlapCount,
    };
    return true;
}

bool FUnifiedScriptGameplayDataPlane::IsManagedPhase() const
{
    return !bShutdown && Phase == EUnifiedScriptGameplayDataPhaseV4::ManagedTick &&
        World.IsValid() && ObjectRegistry != nullptr;
}

void FUnifiedScriptGameplayDataPlane::PublishPendingEvents()
{
    const int32 WriteIndex = 1 - ActiveEventBufferIndex;
    if (ObjectRegistry != nullptr)
    {
        for (FUnifiedScriptObjectHandle Handle : EventOwnedHandles[WriteIndex])
        {
            FUnifiedScriptObjectHandleResult Result;
            ObjectRegistry->ReleaseObject(Handle, Result);
        }
    }
    EventOwnedHandles[WriteIndex].Reset();
    EventOwnedHandles[WriteIndex].Append(PendingEventOwnedHandles);
    PendingEventOwnedHandles.Reset();
    EventBuffers[WriteIndex].Reset();
    EventBuffers[WriteIndex].Append(PendingEvents);
    PendingEvents.Reset();
    ActiveEventBufferIndex = WriteIndex;
    LastPublishedEventFrame = FrameNumber;
}

void FUnifiedScriptGameplayDataPlane::ExecuteCommittedCommands()
{
    for (const FCommandReservation& Reservation : CommandReservations)
    {
        if (!Reservation.bCommitted)
        {
            continue;
        }
        for (uint32 Index = 0; Index < Reservation.Count; ++Index)
        {
            FUnifiedScriptCommandV4& Command =
                Commands[static_cast<int32>(Reservation.Offset + Index)];
            const FUnifiedScriptGameplayEventV4 Event = ExecuteCommand(Command);
            ++TotalExecutedCommandCount;
            LastExecutedSequenceId = Command.SequenceId;
            const bool bOwnsArgument =
                Event.Status == EUnifiedScriptCommandStatusV4::Success &&
                (Event.Kind == EUnifiedScriptGameplayEventKindV4::ActorSpawned ||
                 Event.Kind == EUnifiedScriptGameplayEventKindV4::EffectSpawned) &&
                Event.Argument.ObjectHandle != 0;
            QueueEvent(
                Event,
                bOwnsArgument
                    ? FUnifiedScriptObjectHandle{Event.Argument.ObjectHandle}
                    : FUnifiedScriptObjectHandle{});
        }
    }
}

FUnifiedScriptGameplayEventV4 FUnifiedScriptGameplayDataPlane::ExecuteCommand(
    FUnifiedScriptCommandV4& Command)
{
    using namespace UE::UnifiedScript::GameplayData::Private;
    FUnifiedScriptGameplayEventV4 Event = MakeCommandEvent(Command, FrameNumber);
    UWorld* CurrentWorld = World.Get();
    if (CurrentWorld == nullptr)
    {
        Event.Status = EUnifiedScriptCommandStatusV4::ExecutionFailed;
        return Event;
    }

    UObject* TargetObject = nullptr;
    EUnifiedScriptCommandStatusV4 Status = EUnifiedScriptCommandStatusV4::Success;
    switch (Command.Type)
    {
    case EUnifiedScriptCommandTypeV4::SetActorTransform:
    {
        Status = ResolveObject(Command.Target, AActor::StaticClass(), TargetObject);
        AActor* Actor = Cast<AActor>(TargetObject);
        if (Status == EUnifiedScriptCommandStatusV4::Success &&
            !Actor->SetActorTransform(
                DecodeTransform(Command),
                (Command.Flags & 1u) != 0,
                nullptr,
                (Command.Flags & 2u) != 0
                    ? ETeleportType::TeleportPhysics
                    : ETeleportType::None))
        {
            Status = EUnifiedScriptCommandStatusV4::ExecutionFailed;
        }
        break;
    }
    case EUnifiedScriptCommandTypeV4::SetActorVelocity:
    {
        Status = ResolveObject(Command.Target, AActor::StaticClass(), TargetObject);
        AActor* Actor = Cast<AActor>(TargetObject);
        UPrimitiveComponent* Root = Actor != nullptr
            ? Cast<UPrimitiveComponent>(Actor->GetRootComponent())
            : nullptr;
        if (Status == EUnifiedScriptCommandStatusV4::Success && Root == nullptr)
        {
            Status = EUnifiedScriptCommandStatusV4::TypeMismatch;
        }
        if (Status == EUnifiedScriptCommandStatusV4::Success)
        {
            Root->SetPhysicsLinearVelocity(
                FVector(Command.Vector0X, Command.Vector0Y, Command.Vector0Z));
        }
        break;
    }
    case EUnifiedScriptCommandTypeV4::ApplyDamage:
    {
        Status = ResolveObject(Command.Target, AActor::StaticClass(), TargetObject);
        UObject* InstigatorObject = nullptr;
        UObject* CauserObject = nullptr;
        UObject* DamageTypeObject = nullptr;
        if (Status == EUnifiedScriptCommandStatusV4::Success)
        {
            Status = ResolveObject(
                Command.Argument0,
                AController::StaticClass(),
                InstigatorObject,
                true);
        }
        if (Status == EUnifiedScriptCommandStatusV4::Success)
        {
            Status = ResolveObject(
                Command.Argument1,
                AActor::StaticClass(),
                CauserObject,
                true);
        }
        if (Status == EUnifiedScriptCommandStatusV4::Success)
        {
            Status = ResolveObject(
                Command.Argument2,
                UClass::StaticClass(),
                DamageTypeObject,
                true);
        }
        if (Status == EUnifiedScriptCommandStatusV4::Success)
        {
            Event.Value0 = UGameplayStatics::ApplyDamage(
                CastChecked<AActor>(TargetObject),
                static_cast<float>(Command.Scalar0),
                Cast<AController>(InstigatorObject),
                Cast<AActor>(CauserObject),
                Cast<UClass>(DamageTypeObject));
            Event.Kind = EUnifiedScriptGameplayEventKindV4::DamageApplied;
        }
        break;
    }
    case EUnifiedScriptCommandTypeV4::SpawnActor:
    {
        UObject* ClassObject = nullptr;
        Status = ResolveObject(
            Command.Argument0,
            UClass::StaticClass(),
            ClassObject);
        UClass* ActorClass = Cast<UClass>(ClassObject);
        if (Status == EUnifiedScriptCommandStatusV4::Success &&
            (ActorClass == nullptr || !ActorClass->IsChildOf(AActor::StaticClass())))
        {
            Status = EUnifiedScriptCommandStatusV4::TypeMismatch;
        }
        if (Status == EUnifiedScriptCommandStatusV4::Success)
        {
            AActor* Spawned = CurrentWorld->SpawnActor<AActor>(
                ActorClass,
                DecodeTransform(Command));
            if (Spawned == nullptr)
            {
                Status = EUnifiedScriptCommandStatusV4::ExecutionFailed;
            }
            else
            {
                FUnifiedScriptObjectHandle OwnedHandle;
                Event.Argument = RegisterEventObject(Spawned, OwnedHandle);
                Event.Kind = EUnifiedScriptGameplayEventKindV4::ActorSpawned;
            }
        }
        break;
    }
    case EUnifiedScriptCommandTypeV4::DestroyActor:
    {
        Status = ResolveObject(Command.Target, AActor::StaticClass(), TargetObject);
        if (Status == EUnifiedScriptCommandStatusV4::Success &&
            !CastChecked<AActor>(TargetObject)->Destroy())
        {
            Status = EUnifiedScriptCommandStatusV4::ExecutionFailed;
        }
        if (Status == EUnifiedScriptCommandStatusV4::Success)
        {
            Event.Kind = EUnifiedScriptGameplayEventKindV4::ActorDestroyed;
        }
        break;
    }
    case EUnifiedScriptCommandTypeV4::SpawnEmitter:
    {
        UObject* ParticleObject = nullptr;
        Status = ResolveObject(
            Command.Argument0,
            UParticleSystem::StaticClass(),
            ParticleObject);
        if (Status == EUnifiedScriptCommandStatusV4::Success)
        {
            UParticleSystemComponent* Component =
                UGameplayStatics::SpawnEmitterAtLocation(
                    CurrentWorld,
                    CastChecked<UParticleSystem>(ParticleObject),
                    DecodeTransform(Command),
                    true);
            if (Component == nullptr)
            {
                if (FApp::CanEverRender() &&
                    !CurrentWorld->IsNetMode(NM_DedicatedServer))
                {
                    Status = EUnifiedScriptCommandStatusV4::ExecutionFailed;
                }
                else
                {
                    Event.Kind = EUnifiedScriptGameplayEventKindV4::EffectSpawned;
                }
            }
            else
            {
                FUnifiedScriptObjectHandle OwnedHandle;
                Event.Argument = RegisterEventObject(Component, OwnedHandle);
                Event.Kind = EUnifiedScriptGameplayEventKindV4::EffectSpawned;
            }
        }
        break;
    }
    case EUnifiedScriptCommandTypeV4::None:
    default:
        Status = EUnifiedScriptCommandStatusV4::InvalidCommand;
        break;
    }
    Event.Status = Status;
    return Event;
}

void FUnifiedScriptGameplayDataPlane::QueueEvent(
    const FUnifiedScriptGameplayEventV4& Event,
    FUnifiedScriptObjectHandle OwnedArgument)
{
    if (PendingEvents.Num() >=
        static_cast<int32>(UE::UnifiedScript::GameplayData::MaximumEventCount))
    {
        ++DroppedEventCount;
        if (!OwnedArgument.IsNull() && ObjectRegistry != nullptr)
        {
            FUnifiedScriptObjectHandleResult Result;
            ObjectRegistry->ReleaseObject(OwnedArgument, Result);
        }
        return;
    }
    PendingEvents.Add(Event);
    if (!OwnedArgument.IsNull())
    {
        PendingEventOwnedHandles.Add(OwnedArgument);
    }
}

EUnifiedScriptCommandStatusV4 FUnifiedScriptGameplayDataPlane::ResolveObject(
    const FUnifiedScriptSharedObjectHandleV4& Handle,
    UClass* ExpectedClass,
    UObject*& OutObject,
    bool bAllowNull) const
{
    OutObject = nullptr;
    if (Handle.ObjectHandle == 0)
    {
        return bAllowNull
            ? EUnifiedScriptCommandStatusV4::Success
            : EUnifiedScriptCommandStatusV4::StaleHandle;
    }
    if (Handle.WorldHostId != WorldHostId)
    {
        return EUnifiedScriptCommandStatusV4::WorldMismatch;
    }
    if (ObjectRegistry == nullptr)
    {
        return EUnifiedScriptCommandStatusV4::StaleHandle;
    }
    FUnifiedScriptObjectHandleResult Result;
    OutObject = ObjectRegistry->ResolveObject({Handle.ObjectHandle}, Result);
    if (!Result.IsSuccess())
    {
        return UE::UnifiedScript::GameplayData::Private::MapHandleError(Result.Error);
    }
    if (ExpectedClass != nullptr && !OutObject->IsA(ExpectedClass))
    {
        OutObject = nullptr;
        return EUnifiedScriptCommandStatusV4::TypeMismatch;
    }
    return EUnifiedScriptCommandStatusV4::Success;
}

FUnifiedScriptSharedObjectHandleV4
FUnifiedScriptGameplayDataPlane::RegisterEventObject(
    UObject* Object,
    FUnifiedScriptObjectHandle& OutOwnedHandle)
{
    OutOwnedHandle = {};
    if (Object == nullptr || ObjectRegistry == nullptr)
    {
        return {};
    }
    FUnifiedScriptObjectHandleResult Result;
    OutOwnedHandle = ObjectRegistry->RegisterObject(Object, Result);
    return Result.IsSuccess()
        ? FUnifiedScriptSharedObjectHandleV4{WorldHostId, OutOwnedHandle.Value}
        : FUnifiedScriptSharedObjectHandleV4{};
}

FUnifiedScriptTraceResultV4 FUnifiedScriptGameplayDataPlane::ExecuteTrace(
    const FUnifiedScriptTraceRequestV4& Request)
{
    FUnifiedScriptTraceResultV4 Result;
    Result.SequenceId = Request.SequenceId;
    UWorld* CurrentWorld = World.Get();
    const FUnifiedScriptSharedObjectHandleV4 IgnoredActors[] = {
        Request.IgnoreActor0,
        Request.IgnoreActor1,
        Request.IgnoreActor2,
        Request.IgnoreActor3,
    };
    if (CurrentWorld == nullptr || Request.CollisionChannel >= ECC_MAX ||
        Request.IgnoreActorCount > UE_ARRAY_COUNT(IgnoredActors))
    {
        Result.Status = EUnifiedScriptTraceStatusV4::InvalidRequest;
        return Result;
    }

    FCollisionQueryParams QueryParams(SCENE_QUERY_STAT(UnifiedScriptBatchTrace), true);
    for (uint8 Index = 0; Index < Request.IgnoreActorCount; ++Index)
    {
        UObject* IgnoredObject = nullptr;
        const EUnifiedScriptCommandStatusV4 ResolveStatus = ResolveObject(
            IgnoredActors[Index],
            AActor::StaticClass(),
            IgnoredObject);
        if (ResolveStatus != EUnifiedScriptCommandStatusV4::Success)
        {
            Result.Status = ResolveStatus == EUnifiedScriptCommandStatusV4::WorldMismatch
                ? EUnifiedScriptTraceStatusV4::WorldMismatch
                : EUnifiedScriptTraceStatusV4::StaleHandle;
            return Result;
        }
        QueryParams.AddIgnoredActor(CastChecked<AActor>(IgnoredObject));
    }

    const FVector Start(Request.StartX, Request.StartY, Request.StartZ);
    const FVector End(Request.EndX, Request.EndY, Request.EndZ);
    FHitResult Hit;
    bool bHit = false;
    const ECollisionChannel Channel =
        static_cast<ECollisionChannel>(Request.CollisionChannel);
    switch (Request.Shape)
    {
    case EUnifiedScriptTraceShapeV4::Line:
        bHit = CurrentWorld->LineTraceSingleByChannel(
            Hit, Start, End, Channel, QueryParams);
        break;
    case EUnifiedScriptTraceShapeV4::Sphere:
        if (Request.ExtentX <= 0.0)
        {
            Result.Status = EUnifiedScriptTraceStatusV4::InvalidRequest;
            return Result;
        }
        bHit = CurrentWorld->SweepSingleByChannel(
            Hit,
            Start,
            End,
            FQuat::Identity,
            Channel,
            FCollisionShape::MakeSphere(Request.ExtentX),
            QueryParams);
        break;
    case EUnifiedScriptTraceShapeV4::Box:
        if (Request.ExtentX <= 0.0 || Request.ExtentY <= 0.0 ||
            Request.ExtentZ <= 0.0)
        {
            Result.Status = EUnifiedScriptTraceStatusV4::InvalidRequest;
            return Result;
        }
        bHit = CurrentWorld->SweepSingleByChannel(
            Hit,
            Start,
            End,
            FQuat(
                Request.RotationX,
                Request.RotationY,
                Request.RotationZ,
                Request.RotationW).GetNormalized(),
            Channel,
            FCollisionShape::MakeBox(
                FVector(Request.ExtentX, Request.ExtentY, Request.ExtentZ)),
            QueryParams);
        break;
    default:
        Result.Status = EUnifiedScriptTraceStatusV4::InvalidRequest;
        return Result;
    }

    Result.Status = EUnifiedScriptTraceStatusV4::Success;
    if (!bHit)
    {
        return Result;
    }
    Result.Flags = EUnifiedScriptTraceResultFlagsV4::BlockingHit;
    if (Hit.bStartPenetrating)
    {
        Result.Flags = static_cast<EUnifiedScriptTraceResultFlagsV4>(
            static_cast<uint8>(Result.Flags) |
            static_cast<uint8>(EUnifiedScriptTraceResultFlagsV4::StartPenetrating));
    }
    Result.FaceIndex = Hit.FaceIndex;
    Result.Distance = Hit.Distance;
    Result.Time = Hit.Time;
    Result.LocationX = Hit.Location.X;
    Result.LocationY = Hit.Location.Y;
    Result.LocationZ = Hit.Location.Z;
    Result.ImpactPointX = Hit.ImpactPoint.X;
    Result.ImpactPointY = Hit.ImpactPoint.Y;
    Result.ImpactPointZ = Hit.ImpactPoint.Z;
    Result.NormalX = Hit.Normal.X;
    Result.NormalY = Hit.Normal.Y;
    Result.NormalZ = Hit.Normal.Z;
    Result.ImpactNormalX = Hit.ImpactNormal.X;
    Result.ImpactNormalY = Hit.ImpactNormal.Y;
    Result.ImpactNormalZ = Hit.ImpactNormal.Z;
    Result.Item = Hit.Item;
    Result.ElementIndex = Hit.ElementIndex;

    FUnifiedScriptObjectHandle OwnedActorHandle;
    Result.HitActor = RegisterEventObject(Hit.GetActor(), OwnedActorHandle);
    if (!OwnedActorHandle.IsNull())
    {
        TraceOwnedHandles.Add(OwnedActorHandle);
    }
    FUnifiedScriptObjectHandle OwnedComponentHandle;
    Result.HitComponent = RegisterEventObject(Hit.GetComponent(), OwnedComponentHandle);
    if (!OwnedComponentHandle.IsNull())
    {
        TraceOwnedHandles.Add(OwnedComponentHandle);
    }
    return Result;
}

FUnifiedScriptOverlapResultV1 FUnifiedScriptGameplayDataPlane::ExecuteOverlap(
    const FUnifiedScriptOverlapRequestV1& Request)
{
    FUnifiedScriptOverlapResultV1 Result;
    Result.SequenceId = Request.SequenceId;
    UWorld* CurrentWorld = World.Get();
    const FUnifiedScriptSharedObjectHandleV4 IgnoredActors[] = {
        Request.IgnoreActor0,
        Request.IgnoreActor1,
        Request.IgnoreActor2,
        Request.IgnoreActor3,
    };
    if (CurrentWorld == nullptr || Request.CollisionChannel >= ECC_MAX ||
        Request.IgnoreActorCount > UE_ARRAY_COUNT(IgnoredActors))
    {
        Result.Status = EUnifiedScriptTraceStatusV4::InvalidRequest;
        return Result;
    }

    FCollisionQueryParams QueryParams(
        SCENE_QUERY_STAT(UnifiedScriptBatchOverlap), true);
    for (uint8 Index = 0; Index < Request.IgnoreActorCount; ++Index)
    {
        UObject* IgnoredObject = nullptr;
        const EUnifiedScriptCommandStatusV4 ResolveStatus = ResolveObject(
            IgnoredActors[Index],
            AActor::StaticClass(),
            IgnoredObject);
        if (ResolveStatus != EUnifiedScriptCommandStatusV4::Success)
        {
            Result.Status = ResolveStatus ==
                    EUnifiedScriptCommandStatusV4::WorldMismatch
                ? EUnifiedScriptTraceStatusV4::WorldMismatch
                : EUnifiedScriptTraceStatusV4::StaleHandle;
            return Result;
        }
        QueryParams.AddIgnoredActor(CastChecked<AActor>(IgnoredObject));
    }

    FCollisionShape Shape;
    FQuat Rotation = FQuat::Identity;
    switch (Request.Shape)
    {
    case EUnifiedScriptTraceShapeV4::Sphere:
        if (Request.ExtentX <= 0.0)
        {
            Result.Status = EUnifiedScriptTraceStatusV4::InvalidRequest;
            return Result;
        }
        Shape = FCollisionShape::MakeSphere(Request.ExtentX);
        break;
    case EUnifiedScriptTraceShapeV4::Box:
        if (Request.ExtentX <= 0.0 || Request.ExtentY <= 0.0 ||
            Request.ExtentZ <= 0.0)
        {
            Result.Status = EUnifiedScriptTraceStatusV4::InvalidRequest;
            return Result;
        }
        Shape = FCollisionShape::MakeBox(
            FVector(Request.ExtentX, Request.ExtentY, Request.ExtentZ));
        Rotation = FQuat(
            Request.RotationX,
            Request.RotationY,
            Request.RotationZ,
            Request.RotationW).GetNormalized();
        break;
    default:
        Result.Status = EUnifiedScriptTraceStatusV4::InvalidRequest;
        return Result;
    }

    TArray<FOverlapResult> Overlaps;
    CurrentWorld->OverlapMultiByChannel(
        Overlaps,
        FVector(Request.CenterX, Request.CenterY, Request.CenterZ),
        Rotation,
        static_cast<ECollisionChannel>(Request.CollisionChannel),
        Shape,
        QueryParams);
    Result.Status = EUnifiedScriptTraceStatusV4::Success;
    for (const FOverlapResult& Overlap : Overlaps)
    {
        AActor* Actor = Overlap.GetActor();
        UPrimitiveComponent* Component = Overlap.GetComponent();
        if (Actor == nullptr && Component == nullptr)
        {
            continue;
        }
        Result.Flags = EUnifiedScriptOverlapResultFlagsV1::HasOverlap;
        FUnifiedScriptObjectHandle OwnedActorHandle;
        Result.HitActor = RegisterEventObject(Actor, OwnedActorHandle);
        if (!OwnedActorHandle.IsNull())
        {
            OverlapOwnedHandles.Add(OwnedActorHandle);
        }
        FUnifiedScriptObjectHandle OwnedComponentHandle;
        Result.HitComponent = RegisterEventObject(Component, OwnedComponentHandle);
        if (!OwnedComponentHandle.IsNull())
        {
            OverlapOwnedHandles.Add(OwnedComponentHandle);
        }
        break;
    }
    return Result;
}

void FUnifiedScriptGameplayDataPlane::Shutdown()
{
    check(IsInGameThread());
    if (bShutdown)
    {
        return;
    }
    bShutdown = true;
    Phase = EUnifiedScriptGameplayDataPhaseV4::ShuttingDown;
    if (ObjectRegistry != nullptr)
    {
        auto ReleaseHandles = [this](TArray<FUnifiedScriptObjectHandle>& Handles)
        {
            for (FUnifiedScriptObjectHandle Handle : Handles)
            {
                FUnifiedScriptObjectHandleResult Result;
                ObjectRegistry->ReleaseObject(Handle, Result);
            }
            Handles.Reset();
        };
        ReleaseHandles(EventOwnedHandles[0]);
        ReleaseHandles(EventOwnedHandles[1]);
        ReleaseHandles(PendingEventOwnedHandles);
        ReleaseHandles(TraceOwnedHandles);
        ReleaseHandles(OverlapOwnedHandles);
    }
    Commands.Reset();
    CommandReservations.Reset();
    ReservationIndices.Reset();
    EventBuffers[0].Reset();
    EventBuffers[1].Reset();
    PendingEvents.Reset();
    TraceRequests.Reset();
    TraceResults.Reset();
    OverlapRequests.Reset();
    OverlapResults.Reset();
    World.Reset();
    ObjectRegistry = nullptr;
    WorldHostId = 0;
    ActiveCommandBatchToken = 0;
    ActiveTraceBatchToken = 0;
    ActiveOverlapBatchToken = 0;
}

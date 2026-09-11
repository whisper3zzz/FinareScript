#include "FinareGameplayDataPlane.h"

#include "Components/PrimitiveComponent.h"
#include "Engine/DamageEvents.h"
#include "Engine/OverlapResult.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "GameFramework/Controller.h"
#include "HAL/PlatformTime.h"
#include "Kismet/GameplayStatics.h"
#include "Misc/App.h"
#include "Particles/ParticleSystem.h"
#include "Particles/ParticleSystemComponent.h"

namespace UE::Finare::GameplayData::Private
{
	constexpr int32 InitialCommandCapacity = 256;
	constexpr int32 InitialTraceCapacity = 256;
	constexpr int32 InitialOverlapCapacity = 256;

	uint64 CyclesToNanoseconds(uint64 Cycles)
	{
		const double Nanoseconds = FPlatformTime::ToSeconds64(Cycles) * 1000000000.0;
		return Nanoseconds >= static_cast<double>(MAX_uint64)
			       ? MAX_uint64
			       : static_cast<uint64>(Nanoseconds);
	}

	void AccumulatePhase(
		uint64 StartedCycles,
		uint64 FinishedCycles,
		uint64& Count,
		uint64& TotalNanoseconds)
	{
		if (Count != MAX_uint64)
		{
			++Count;
		}
		const uint64 ElapsedNanoseconds = CyclesToNanoseconds(
			FinishedCycles >= StartedCycles ? FinishedCycles - StartedCycles : 0);
		TotalNanoseconds = ElapsedNanoseconds > MAX_uint64 - TotalNanoseconds
			                   ? MAX_uint64
			                   : TotalNanoseconds + ElapsedNanoseconds;
	}

	uint64 AllocateNonZero(uint64& Next)
	{
		const uint64 Result = Next++;
		if (Next == 0)
		{
			Next = 1;
		}
		return Result == 0 ? Next++ : Result;
	}

	EFinareCommandStatusV4 MapHandleError(
		EFinareObjectHandleError Error)
	{
		return Error == EFinareObjectHandleError::WorldMismatch
			       ? EFinareCommandStatusV4::WorldMismatch
			       : EFinareCommandStatusV4::StaleHandle;
	}

	FTransform DecodeTransform(const FFinareCommandV4& Command)
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

	FFinareGameplayEventV4 MakeCommandEvent(
		const FFinareCommandV4& Command,
		uint64 FrameNumber)
	{
		FFinareGameplayEventV4 Event;
		Event.SequenceId = Command.SequenceId;
		Event.Kind = EFinareGameplayEventKindV4::CommandCompleted;
		Event.CommandType = Command.Type;
		Event.Source = Command.Target;
		Event.FrameNumber = FrameNumber;
		return Event;
	}
}

FFinareGameplayDataPlane::FFinareGameplayDataPlane(
	UWorld& InWorld,
	FFinareObjectRegistry& InObjectRegistry,
	uint64 InWorldHostId)
	: World(&InWorld)
	  , ObjectRegistry(&InObjectRegistry)
	  , WorldHostId(InWorldHostId)
{
	check(IsInGameThread());
	check(InWorldHostId != 0);
	Commands.Reserve(UE::Finare::GameplayData::Private::InitialCommandCapacity);
	CommandReservations.Reserve(32);
	ReservationIndices.Reserve(32);
	EventBuffers[0].Reserve(UE::Finare::GameplayData::MaximumEventCount);
	EventBuffers[1].Reserve(UE::Finare::GameplayData::MaximumEventCount);
	EventOwnedHandles[0].Reserve(UE::Finare::GameplayData::MaximumEventCount);
	EventOwnedHandles[1].Reserve(UE::Finare::GameplayData::MaximumEventCount);
	PendingEvents.Reserve(UE::Finare::GameplayData::MaximumEventCount);
	PendingEventOwnedHandles.Reserve(UE::Finare::GameplayData::MaximumEventCount);
	TraceRequests.Reserve(UE::Finare::GameplayData::Private::InitialTraceCapacity);
	TraceResults.Reserve(UE::Finare::GameplayData::Private::InitialTraceCapacity);
	TraceOwnedHandles.Reserve(UE::Finare::GameplayData::Private::InitialTraceCapacity);
	OverlapRequests.Reserve(
		UE::Finare::GameplayData::Private::InitialOverlapCapacity);
	OverlapResults.Reserve(
		UE::Finare::GameplayData::Private::InitialOverlapCapacity);
	OverlapOwnedHandles.Reserve(
		UE::Finare::GameplayData::Private::InitialOverlapCapacity);
}

FFinareGameplayDataPlane::~FFinareGameplayDataPlane()
{
	Shutdown();
}

void FFinareGameplayDataPlane::BeginFrame()
{
	check(IsInGameThread());
	if (bShutdown)
	{
		return;
	}
	if (Phase == EFinareGameplayDataPhaseV4::ManagedTick)
	{
		EndFrame();
	}
	const uint64 PreActorTickStartedCycles = FPlatformTime::Cycles64();
	Phase = EFinareGameplayDataPhaseV4::NativePreActorTick;
	++FrameNumber;
	if (FrameNumber == 0)
	{
		++FrameNumber;
	}
	PublishPendingEvents();
	PublishPendingGeneratedEvents();
	Commands.Reset();
	CommandReservations.Reset();
	ReservationIndices.Reset();
	ActiveCommandBatchToken = 0;
	ActiveTraceBatchToken = 0;
	ActiveOverlapBatchToken = 0;
	UE::Finare::GameplayData::Private::AccumulatePhase(
		PreActorTickStartedCycles,
		FPlatformTime::Cycles64(),
		NativePreActorTickCount,
		NativePreActorTickNanoseconds);
	Phase = EFinareGameplayDataPhaseV4::ManagedTick;
	ManagedTickStartedCycles = FPlatformTime::Cycles64();
}

void FFinareGameplayDataPlane::EndFrame()
{
	check(IsInGameThread());
	if (bShutdown || Phase != EFinareGameplayDataPhaseV4::ManagedTick)
	{
		return;
	}
	const uint64 ManagedTickFinishedCycles = FPlatformTime::Cycles64();
	UE::Finare::GameplayData::Private::AccumulatePhase(
		ManagedTickStartedCycles,
		ManagedTickFinishedCycles,
		ManagedTickCount,
		ManagedTickNanoseconds);
	ManagedTickStartedCycles = 0;
	Phase = EFinareGameplayDataPhaseV4::NativePostActorTick;
	const uint64 PostActorTickStartedCycles = FPlatformTime::Cycles64();
	ExecuteCommittedCommands();
	ActiveTraceBatchToken = 0;
	ActiveOverlapBatchToken = 0;
	UE::Finare::GameplayData::Private::AccumulatePhase(
		PostActorTickStartedCycles,
		FPlatformTime::Cycles64(),
		NativePostActorTickCount,
		NativePostActorTickNanoseconds);
	Phase = EFinareGameplayDataPhaseV4::Idle;
}

bool FFinareGameplayDataPlane::BeginCommandBatch(
	uint32 Capacity,
	FFinareCommandBatchViewV4& OutView)
{
	check(IsInGameThread());
	OutView = {};
	if (Capacity > UE::Finare::GameplayData::MaximumCommandCount ||
		static_cast<uint64>(Commands.Num()) + Capacity >
		UE::Finare::GameplayData::MaximumCommandCount)
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
		UE::Finare::GameplayData::Private::AllocateNonZero(NextBatchToken);
	Reservation.FirstSequenceId = NextSequenceId;
	for (uint32 Index = 0; Index < Capacity; ++Index)
	{
		UE::Finare::GameplayData::Private::AllocateNonZero(NextSequenceId);
	}
	Reservation.Offset = Offset;
	Reservation.Capacity = Capacity;
	const int32 ReservationIndex = CommandReservations.Add(Reservation);
	ReservationIndices.Add(Reservation.Token, ReservationIndex);
	ActiveCommandBatchToken = Reservation.Token;

	OutView = {
		UE::Finare::GameplayData::AbiVersion,
		sizeof(FFinareCommandBatchViewV4),
		UE::Finare::GameplayData::CommandSchemaId,
		WorldHostId,
		FrameNumber,
		Reservation.Token,
		Reservation.FirstSequenceId,
		Commands.GetData() + Offset,
		Capacity,
		sizeof(FFinareCommandV4),
		0,
		0,
	};
	return true;
}

bool FFinareGameplayDataPlane::CommitCommandBatch(
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

bool FFinareGameplayDataPlane::AcquireEvents(
	FFinareGameplayEventViewV4& OutView) const
{
	check(IsInGameThread());
	if (bShutdown || WorldHostId == 0)
	{
		OutView = {};
		return false;
	}
	const TArray<FFinareGameplayEventV4>& ActiveEvents =
		EventBuffers[ActiveEventBufferIndex];
	OutView = {
		UE::Finare::GameplayData::AbiVersion,
		sizeof(FFinareGameplayEventViewV4),
		UE::Finare::GameplayData::EventSchemaId,
		WorldHostId,
		FrameNumber,
		ActiveEvents.GetData(),
		static_cast<uint32>(ActiveEvents.Num()),
		static_cast<uint32>(ActiveEvents.Max()),
		sizeof(FFinareGameplayEventV4),
		0,
		DroppedEventCount,
	};
	return true;
}

bool FFinareGameplayDataPlane::QueueGeneratedEvents(
	uint64 SchemaId,
	uint32 EntrySize,
	const void* Entries,
	uint32 EntryCount)
{
	check(IsInGameThread());
	if (bShutdown || WorldHostId == 0 || SchemaId == 0 || EntrySize == 0)
	{
		return false;
	}

	FGeneratedEventChannel* Channel = GeneratedEventChannels.Find(SchemaId);
	if (Channel == nullptr)
	{
		if (GeneratedEventChannels.Num() >= static_cast<int32>(
			UE::Finare::GameplayData::MaximumGeneratedChannelCount))
		{
			return false;
		}
		Channel = &GeneratedEventChannels.Add(SchemaId);
		Channel->EntrySize = EntrySize;
	}
	else if (Channel->EntrySize != EntrySize)
	{
		return false;
	}

	const uint64 CombinedEntryCount =
		static_cast<uint64>(Channel->PendingEntryCount) + EntryCount;
	const uint64 CombinedByteCount = CombinedEntryCount * EntrySize;
	if (CombinedEntryCount > UE::Finare::GameplayData::MaximumGeneratedEventCount ||
		CombinedByteCount > UE::Finare::GameplayData::MaximumGeneratedChannelBytes ||
		(EntryCount > 0 && Entries == nullptr))
	{
		Channel->DroppedEntryCount = EntryCount > MAX_uint64 - Channel->DroppedEntryCount
			                             ? MAX_uint64
			                             : Channel->DroppedEntryCount + EntryCount;
		return false;
	}
	if (EntryCount > 0)
	{
		const uint64 AddedByteCount = static_cast<uint64>(EntrySize) * EntryCount;
		Channel->PendingBytes.Append(
			static_cast<const uint8*>(Entries),
			static_cast<int32>(AddedByteCount));
		Channel->PendingEntryCount += EntryCount;
	}
	return true;
}

bool FFinareGameplayDataPlane::AcquireGeneratedEvents(
	uint64 SchemaId,
	uint32 EntrySize,
	FFinareGeneratedDataViewV1& OutView) const
{
	check(IsInGameThread());
	const FGeneratedEventChannel* Channel = GeneratedEventChannels.Find(SchemaId);
	if (bShutdown || WorldHostId == 0 || SchemaId == 0 || EntrySize == 0 ||
		Channel == nullptr || Channel->EntrySize != EntrySize ||
		Channel->PublicationId == 0)
	{
		OutView = {};
		return false;
	}

	const TArray<uint8>& ActiveBuffer = Channel->Buffers[Channel->ActiveBufferIndex];
	OutView = {
		UE::Finare::GameplayData::AbiVersion,
		sizeof(FFinareGeneratedDataViewV1),
		SchemaId,
		WorldHostId,
		Channel->PublicationId,
		ActiveBuffer.GetData(),
		Channel->EntryCounts[Channel->ActiveBufferIndex],
		Channel->EntryCounts[Channel->ActiveBufferIndex],
		EntrySize,
		static_cast<EFinareGeneratedDataFlagsV1>(5u),
		Channel->DroppedEntryCount,
	};
	return true;
}

bool FFinareGameplayDataPlane::BeginTraceBatch(
	uint32 Capacity,
	FFinareTraceRequestViewV4& OutView)
{
	check(IsInGameThread());
	OutView = {};
	if (Capacity > UE::Finare::GameplayData::MaximumTraceCount)
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
		for (FFinareObjectHandle Handle : TraceOwnedHandles)
		{
			FFinareObjectHandleResult Result;
			ObjectRegistry->ReleaseObject(Handle, Result);
		}
	}
	TraceOwnedHandles.Reset();
	TraceResults.Reset();
	TraceRequests.SetNumZeroed(static_cast<int32>(Capacity));
	ActiveTraceBatchToken =
		UE::Finare::GameplayData::Private::AllocateNonZero(NextBatchToken);
	OutView = {
		UE::Finare::GameplayData::AbiVersion,
		sizeof(FFinareTraceRequestViewV4),
		UE::Finare::GameplayData::TraceRequestSchemaId,
		WorldHostId,
		FrameNumber,
		ActiveTraceBatchToken,
		TraceRequests.GetData(),
		Capacity,
		sizeof(FFinareTraceRequestV4),
		0,
		0,
	};
	return true;
}

bool FFinareGameplayDataPlane::ExecuteTraceBatch(
	uint64 BatchToken,
	uint32 RequestCount,
	FFinareTraceResultViewV4& OutView)
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
		FFinareTraceRequestV4& Request = TraceRequests[static_cast<int32>(Index)];
		if (Request.SequenceId == 0)
		{
			Request.SequenceId =
				UE::Finare::GameplayData::Private::AllocateNonZero(NextSequenceId);
		}
		TraceResults.Add(ExecuteTrace(Request));
	}
	TotalExecutedTraceCount += RequestCount;
	ActiveTraceBatchToken = 0;
	OutView = {
		UE::Finare::GameplayData::AbiVersion,
		sizeof(FFinareTraceResultViewV4),
		UE::Finare::GameplayData::TraceResultSchemaId,
		WorldHostId,
		FrameNumber,
		BatchToken,
		TraceResults.GetData(),
		static_cast<uint32>(TraceResults.Num()),
		static_cast<uint32>(TraceResults.Max()),
		sizeof(FFinareTraceResultV4),
		0,
	};
	return true;
}

void FFinareGameplayDataPlane::CancelTraceBatch(uint64 BatchToken)
{
	check(IsInGameThread());
	if (BatchToken != 0 && BatchToken == ActiveTraceBatchToken)
	{
		ActiveTraceBatchToken = 0;
	}
}

bool FFinareGameplayDataPlane::BeginOverlapBatch(
	uint32 Capacity,
	FFinareOverlapRequestViewV1& OutView)
{
	check(IsInGameThread());
	OutView = {};
	if (Capacity > UE::Finare::GameplayData::MaximumOverlapCount)
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
		for (FFinareObjectHandle Handle : OverlapOwnedHandles)
		{
			FFinareObjectHandleResult Result;
			ObjectRegistry->ReleaseObject(Handle, Result);
		}
	}
	OverlapOwnedHandles.Reset();
	OverlapResults.Reset();
	OverlapRequests.SetNumZeroed(static_cast<int32>(Capacity));
	ActiveOverlapBatchToken =
		UE::Finare::GameplayData::Private::AllocateNonZero(NextBatchToken);
	OutView = {
		UE::Finare::GameplayData::AbiVersion,
		sizeof(FFinareOverlapRequestViewV1),
		UE::Finare::GameplayData::OverlapRequestSchemaId,
		WorldHostId,
		FrameNumber,
		ActiveOverlapBatchToken,
		OverlapRequests.GetData(),
		Capacity,
		sizeof(FFinareOverlapRequestV1),
		0,
		0,
	};
	return true;
}

bool FFinareGameplayDataPlane::ExecuteOverlapBatch(
	uint64 BatchToken,
	uint32 RequestCount,
	FFinareOverlapResultViewV1& OutView)
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
		FFinareOverlapRequestV1& Request =
			OverlapRequests[static_cast<int32>(Index)];
		if (Request.SequenceId == 0)
		{
			Request.SequenceId =
				UE::Finare::GameplayData::Private::AllocateNonZero(
					NextSequenceId);
		}
		OverlapResults.Add(ExecuteOverlap(Request));
	}
	TotalExecutedOverlapCount += RequestCount;
	ActiveOverlapBatchToken = 0;
	OutView = {
		UE::Finare::GameplayData::AbiVersion,
		sizeof(FFinareOverlapResultViewV1),
		UE::Finare::GameplayData::OverlapResultSchemaId,
		WorldHostId,
		FrameNumber,
		BatchToken,
		OverlapResults.GetData(),
		static_cast<uint32>(OverlapResults.Num()),
		static_cast<uint32>(OverlapResults.Max()),
		sizeof(FFinareOverlapResultV1),
		0,
	};
	return true;
}

void FFinareGameplayDataPlane::CancelOverlapBatch(uint64 BatchToken)
{
	check(IsInGameThread());
	if (BatchToken != 0 && BatchToken == ActiveOverlapBatchToken)
	{
		ActiveOverlapBatchToken = 0;
	}
}

EFinareGameplayDataPhaseV4 FFinareGameplayDataPlane::GetPhase() const
{
	return Phase;
}

uint64 FFinareGameplayDataPlane::GetFrameNumber() const
{
	return FrameNumber;
}

bool FFinareGameplayDataPlane::GetTelemetry(
	FFinareGameplayDataTelemetryV1& OutTelemetry) const
{
	check(IsInGameThread());
	if (bShutdown || WorldHostId == 0)
	{
		OutTelemetry = {};
		return false;
	}
	OutTelemetry = {
		UE::Finare::DataPlaneSchema::Revision,
		sizeof(FFinareGameplayDataTelemetryV1),
		UE::Finare::DataPlaneSchema::CompatibilityId,
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
		UE::Finare::GameplayData::MaximumCommandCount,
		UE::Finare::GameplayData::MaximumEventCount,
		UE::Finare::GameplayData::MaximumTraceCount,
		static_cast<uint32>(Phase),
		OverlapOverflowCount,
		TotalExecutedOverlapCount,
		UE::Finare::GameplayData::MaximumOverlapCount,
		NativePreActorTickCount,
		NativePreActorTickNanoseconds,
		ManagedTickCount,
		ManagedTickNanoseconds,
		NativePostActorTickCount,
		NativePostActorTickNanoseconds,
	};
	return true;
}

bool FFinareGameplayDataPlane::IsManagedPhase() const
{
	return !bShutdown && Phase == EFinareGameplayDataPhaseV4::ManagedTick &&
		World.IsValid() && ObjectRegistry != nullptr;
}

void FFinareGameplayDataPlane::PublishPendingEvents()
{
	const int32 WriteIndex = 1 - ActiveEventBufferIndex;
	if (ObjectRegistry != nullptr)
	{
		for (FFinareObjectHandle Handle : EventOwnedHandles[WriteIndex])
		{
			FFinareObjectHandleResult Result;
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

void FFinareGameplayDataPlane::PublishPendingGeneratedEvents()
{
	for (TPair<uint64, FGeneratedEventChannel>& Pair : GeneratedEventChannels)
	{
		FGeneratedEventChannel& Channel = Pair.Value;
		const int32 WriteIndex = 1 - Channel.ActiveBufferIndex;
		Channel.Buffers[WriteIndex].Reset();
		Channel.Buffers[WriteIndex].Append(Channel.PendingBytes);
		Channel.EntryCounts[WriteIndex] = Channel.PendingEntryCount;
		Channel.PendingBytes.Reset();
		Channel.PendingEntryCount = 0;
		Channel.ActiveBufferIndex = WriteIndex;
		++Channel.PublicationId;
		if (Channel.PublicationId == 0)
		{
			++Channel.PublicationId;
		}
	}
}

void FFinareGameplayDataPlane::ExecuteCommittedCommands()
{
	for (const FCommandReservation& Reservation : CommandReservations)
	{
		if (!Reservation.bCommitted)
		{
			continue;
		}
		for (uint32 Index = 0; Index < Reservation.Count; ++Index)
		{
			FFinareCommandV4& Command =
				Commands[static_cast<int32>(Reservation.Offset + Index)];
			const FFinareGameplayEventV4 Event = ExecuteCommand(Command);
			++TotalExecutedCommandCount;
			LastExecutedSequenceId = Command.SequenceId;
			const bool bOwnsArgument =
				Event.Status == EFinareCommandStatusV4::Success &&
				(Event.Kind == EFinareGameplayEventKindV4::ActorSpawned ||
					Event.Kind == EFinareGameplayEventKindV4::EffectSpawned) &&
				Event.Argument.ObjectHandle != 0;
			QueueEvent(
				Event,
				bOwnsArgument
					? FFinareObjectHandle{Event.Argument.ObjectHandle}
					: FFinareObjectHandle{});
		}
	}
}

FFinareGameplayEventV4 FFinareGameplayDataPlane::ExecuteCommand(
	FFinareCommandV4& Command)
{
	using namespace UE::Finare::GameplayData::Private;
	FFinareGameplayEventV4 Event = MakeCommandEvent(Command, FrameNumber);
	UWorld* CurrentWorld = World.Get();
	if (CurrentWorld == nullptr)
	{
		Event.Status = EFinareCommandStatusV4::ExecutionFailed;
		return Event;
	}

	UObject* TargetObject = nullptr;
	EFinareCommandStatusV4 Status = EFinareCommandStatusV4::Success;
	switch (Command.Type)
	{
	case EFinareCommandTypeV4::SetActorTransform:
		{
			Status = ResolveObject(Command.Target, AActor::StaticClass(), TargetObject);
			AActor* Actor = Cast<AActor>(TargetObject);
			if (Status == EFinareCommandStatusV4::Success &&
				!Actor->SetActorTransform(
					DecodeTransform(Command),
					(Command.Flags & 1u) != 0,
					nullptr,
					(Command.Flags & 2u) != 0
						? ETeleportType::TeleportPhysics
						: ETeleportType::None))
			{
				Status = EFinareCommandStatusV4::ExecutionFailed;
			}
			break;
		}
	case EFinareCommandTypeV4::SetActorVelocity:
		{
			Status = ResolveObject(Command.Target, AActor::StaticClass(), TargetObject);
			AActor* Actor = Cast<AActor>(TargetObject);
			UPrimitiveComponent* Root = Actor != nullptr
				                            ? Cast<UPrimitiveComponent>(Actor->GetRootComponent())
				                            : nullptr;
			if (Status == EFinareCommandStatusV4::Success && Root == nullptr)
			{
				Status = EFinareCommandStatusV4::TypeMismatch;
			}
			if (Status == EFinareCommandStatusV4::Success)
			{
				Root->SetPhysicsLinearVelocity(
					FVector(Command.Vector0X, Command.Vector0Y, Command.Vector0Z));
			}
			break;
		}
	case EFinareCommandTypeV4::ApplyDamage:
		{
			Status = ResolveObject(Command.Target, AActor::StaticClass(), TargetObject);
			UObject* InstigatorObject = nullptr;
			UObject* CauserObject = nullptr;
			UObject* DamageTypeObject = nullptr;
			if (Status == EFinareCommandStatusV4::Success)
			{
				Status = ResolveObject(
					Command.Argument0,
					AController::StaticClass(),
					InstigatorObject,
					true);
			}
			if (Status == EFinareCommandStatusV4::Success)
			{
				Status = ResolveObject(
					Command.Argument1,
					AActor::StaticClass(),
					CauserObject,
					true);
			}
			if (Status == EFinareCommandStatusV4::Success)
			{
				Status = ResolveObject(
					Command.Argument2,
					UClass::StaticClass(),
					DamageTypeObject,
					true);
			}
			if (Status == EFinareCommandStatusV4::Success)
			{
				Event.Value0 = UGameplayStatics::ApplyDamage(
					CastChecked<AActor>(TargetObject),
					static_cast<float>(Command.Scalar0),
					Cast<AController>(InstigatorObject),
					Cast<AActor>(CauserObject),
					Cast<UClass>(DamageTypeObject));
				Event.Kind = EFinareGameplayEventKindV4::DamageApplied;
			}
			break;
		}
	case EFinareCommandTypeV4::SpawnActor:
		{
			UObject* ClassObject = nullptr;
			Status = ResolveObject(
				Command.Argument0,
				UClass::StaticClass(),
				ClassObject);
			UClass* ActorClass = Cast<UClass>(ClassObject);
			if (Status == EFinareCommandStatusV4::Success &&
				(ActorClass == nullptr || !ActorClass->IsChildOf(AActor::StaticClass())))
			{
				Status = EFinareCommandStatusV4::TypeMismatch;
			}
			if (Status == EFinareCommandStatusV4::Success)
			{
				AActor* Spawned = CurrentWorld->SpawnActor<AActor>(
					ActorClass,
					DecodeTransform(Command));
				if (Spawned == nullptr)
				{
					Status = EFinareCommandStatusV4::ExecutionFailed;
				}
				else
				{
					FFinareObjectHandle OwnedHandle;
					Event.Argument = RegisterEventObject(Spawned, OwnedHandle);
					Event.Kind = EFinareGameplayEventKindV4::ActorSpawned;
				}
			}
			break;
		}
	case EFinareCommandTypeV4::DestroyActor:
		{
			Status = ResolveObject(Command.Target, AActor::StaticClass(), TargetObject);
			if (Status == EFinareCommandStatusV4::Success &&
				!CastChecked<AActor>(TargetObject)->Destroy())
			{
				Status = EFinareCommandStatusV4::ExecutionFailed;
			}
			if (Status == EFinareCommandStatusV4::Success)
			{
				Event.Kind = EFinareGameplayEventKindV4::ActorDestroyed;
			}
			break;
		}
	case EFinareCommandTypeV4::SpawnEmitter:
		{
			UObject* ParticleObject = nullptr;
			Status = ResolveObject(
				Command.Argument0,
				UParticleSystem::StaticClass(),
				ParticleObject);
			if (Status == EFinareCommandStatusV4::Success)
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
						Status = EFinareCommandStatusV4::ExecutionFailed;
					}
					else
					{
						Event.Kind = EFinareGameplayEventKindV4::EffectSpawned;
					}
				}
				else
				{
					FFinareObjectHandle OwnedHandle;
					Event.Argument = RegisterEventObject(Component, OwnedHandle);
					Event.Kind = EFinareGameplayEventKindV4::EffectSpawned;
				}
			}
			break;
		}
	case EFinareCommandTypeV4::None:
	default:
		Status = EFinareCommandStatusV4::InvalidCommand;
		break;
	}
	Event.Status = Status;
	return Event;
}

void FFinareGameplayDataPlane::QueueEvent(
	const FFinareGameplayEventV4& Event,
	FFinareObjectHandle OwnedArgument)
{
	if (PendingEvents.Num() >=
		static_cast<int32>(UE::Finare::GameplayData::MaximumEventCount))
	{
		++DroppedEventCount;
		if (!OwnedArgument.IsNull() && ObjectRegistry != nullptr)
		{
			FFinareObjectHandleResult Result;
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

EFinareCommandStatusV4 FFinareGameplayDataPlane::ResolveObject(
	const FFinareSharedObjectHandleV4& Handle,
	UClass* ExpectedClass,
	UObject*& OutObject,
	bool bAllowNull) const
{
	OutObject = nullptr;
	if (Handle.ObjectHandle == 0)
	{
		return bAllowNull
			       ? EFinareCommandStatusV4::Success
			       : EFinareCommandStatusV4::StaleHandle;
	}
	if (Handle.WorldHostId != WorldHostId)
	{
		return EFinareCommandStatusV4::WorldMismatch;
	}
	if (ObjectRegistry == nullptr)
	{
		return EFinareCommandStatusV4::StaleHandle;
	}
	FFinareObjectHandleResult Result;
	OutObject = ObjectRegistry->ResolveObject({Handle.ObjectHandle}, Result);
	if (!Result.IsSuccess())
	{
		return UE::Finare::GameplayData::Private::MapHandleError(Result.Error);
	}
	if (ExpectedClass != nullptr && !OutObject->IsA(ExpectedClass))
	{
		OutObject = nullptr;
		return EFinareCommandStatusV4::TypeMismatch;
	}
	return EFinareCommandStatusV4::Success;
}

FFinareSharedObjectHandleV4
FFinareGameplayDataPlane::RegisterEventObject(
	UObject* Object,
	FFinareObjectHandle& OutOwnedHandle)
{
	OutOwnedHandle = {};
	if (Object == nullptr || ObjectRegistry == nullptr)
	{
		return {};
	}
	FFinareObjectHandleResult Result;
	OutOwnedHandle = ObjectRegistry->RegisterObject(Object, Result);
	return Result.IsSuccess()
		       ? FFinareSharedObjectHandleV4{WorldHostId, OutOwnedHandle.Value}
		       : FFinareSharedObjectHandleV4{};
}

FFinareTraceResultV4 FFinareGameplayDataPlane::ExecuteTrace(
	const FFinareTraceRequestV4& Request)
{
	FFinareTraceResultV4 Result;
	Result.SequenceId = Request.SequenceId;
	UWorld* CurrentWorld = World.Get();
	const FFinareSharedObjectHandleV4 IgnoredActors[] = {
		Request.IgnoreActor0,
		Request.IgnoreActor1,
		Request.IgnoreActor2,
		Request.IgnoreActor3,
	};
	if (CurrentWorld == nullptr || Request.CollisionChannel >= ECC_MAX ||
		Request.IgnoreActorCount > UE_ARRAY_COUNT(IgnoredActors))
	{
		Result.Status = EFinareTraceStatusV4::InvalidRequest;
		return Result;
	}

	FCollisionQueryParams QueryParams(SCENE_QUERY_STAT(FinareBatchTrace), true);
	for (uint8 Index = 0; Index < Request.IgnoreActorCount; ++Index)
	{
		UObject* IgnoredObject = nullptr;
		const EFinareCommandStatusV4 ResolveStatus = ResolveObject(
			IgnoredActors[Index],
			AActor::StaticClass(),
			IgnoredObject);
		if (ResolveStatus != EFinareCommandStatusV4::Success)
		{
			Result.Status = ResolveStatus == EFinareCommandStatusV4::WorldMismatch
				                ? EFinareTraceStatusV4::WorldMismatch
				                : EFinareTraceStatusV4::StaleHandle;
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
	case EFinareTraceShapeV4::Line:
		bHit = CurrentWorld->LineTraceSingleByChannel(
			Hit, Start, End, Channel, QueryParams);
		break;
	case EFinareTraceShapeV4::Sphere:
		if (Request.ExtentX <= 0.0)
		{
			Result.Status = EFinareTraceStatusV4::InvalidRequest;
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
	case EFinareTraceShapeV4::Box:
		if (Request.ExtentX <= 0.0 || Request.ExtentY <= 0.0 ||
			Request.ExtentZ <= 0.0)
		{
			Result.Status = EFinareTraceStatusV4::InvalidRequest;
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
		Result.Status = EFinareTraceStatusV4::InvalidRequest;
		return Result;
	}

	Result.Status = EFinareTraceStatusV4::Success;
	if (!bHit)
	{
		return Result;
	}
	Result.Flags = EFinareTraceResultFlagsV4::BlockingHit;
	if (Hit.bStartPenetrating)
	{
		Result.Flags = static_cast<EFinareTraceResultFlagsV4>(
			static_cast<uint8>(Result.Flags) |
			static_cast<uint8>(EFinareTraceResultFlagsV4::StartPenetrating));
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

	FFinareObjectHandle OwnedActorHandle;
	Result.HitActor = RegisterEventObject(Hit.GetActor(), OwnedActorHandle);
	if (!OwnedActorHandle.IsNull())
	{
		TraceOwnedHandles.Add(OwnedActorHandle);
	}
	FFinareObjectHandle OwnedComponentHandle;
	Result.HitComponent = RegisterEventObject(Hit.GetComponent(), OwnedComponentHandle);
	if (!OwnedComponentHandle.IsNull())
	{
		TraceOwnedHandles.Add(OwnedComponentHandle);
	}
	return Result;
}

FFinareOverlapResultV1 FFinareGameplayDataPlane::ExecuteOverlap(
	const FFinareOverlapRequestV1& Request)
{
	FFinareOverlapResultV1 Result;
	Result.SequenceId = Request.SequenceId;
	UWorld* CurrentWorld = World.Get();
	const FFinareSharedObjectHandleV4 IgnoredActors[] = {
		Request.IgnoreActor0,
		Request.IgnoreActor1,
		Request.IgnoreActor2,
		Request.IgnoreActor3,
	};
	if (CurrentWorld == nullptr || Request.CollisionChannel >= ECC_MAX ||
		Request.IgnoreActorCount > UE_ARRAY_COUNT(IgnoredActors))
	{
		Result.Status = EFinareTraceStatusV4::InvalidRequest;
		return Result;
	}

	FCollisionQueryParams QueryParams(
		SCENE_QUERY_STAT(FinareBatchOverlap), true);
	for (uint8 Index = 0; Index < Request.IgnoreActorCount; ++Index)
	{
		UObject* IgnoredObject = nullptr;
		const EFinareCommandStatusV4 ResolveStatus = ResolveObject(
			IgnoredActors[Index],
			AActor::StaticClass(),
			IgnoredObject);
		if (ResolveStatus != EFinareCommandStatusV4::Success)
		{
			Result.Status = ResolveStatus ==
			                EFinareCommandStatusV4::WorldMismatch
				                ? EFinareTraceStatusV4::WorldMismatch
				                : EFinareTraceStatusV4::StaleHandle;
			return Result;
		}
		QueryParams.AddIgnoredActor(CastChecked<AActor>(IgnoredObject));
	}

	FCollisionShape Shape;
	FQuat Rotation = FQuat::Identity;
	switch (Request.Shape)
	{
	case EFinareTraceShapeV4::Sphere:
		if (Request.ExtentX <= 0.0)
		{
			Result.Status = EFinareTraceStatusV4::InvalidRequest;
			return Result;
		}
		Shape = FCollisionShape::MakeSphere(Request.ExtentX);
		break;
	case EFinareTraceShapeV4::Box:
		if (Request.ExtentX <= 0.0 || Request.ExtentY <= 0.0 ||
			Request.ExtentZ <= 0.0)
		{
			Result.Status = EFinareTraceStatusV4::InvalidRequest;
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
		Result.Status = EFinareTraceStatusV4::InvalidRequest;
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
	Result.Status = EFinareTraceStatusV4::Success;
	for (const FOverlapResult& Overlap : Overlaps)
	{
		AActor* Actor = Overlap.GetActor();
		UPrimitiveComponent* Component = Overlap.GetComponent();
		if (Actor == nullptr && Component == nullptr)
		{
			continue;
		}
		Result.Flags = EFinareOverlapResultFlagsV1::HasOverlap;
		FFinareObjectHandle OwnedActorHandle;
		Result.HitActor = RegisterEventObject(Actor, OwnedActorHandle);
		if (!OwnedActorHandle.IsNull())
		{
			OverlapOwnedHandles.Add(OwnedActorHandle);
		}
		FFinareObjectHandle OwnedComponentHandle;
		Result.HitComponent = RegisterEventObject(Component, OwnedComponentHandle);
		if (!OwnedComponentHandle.IsNull())
		{
			OverlapOwnedHandles.Add(OwnedComponentHandle);
		}
		break;
	}
	return Result;
}

void FFinareGameplayDataPlane::Shutdown()
{
	check(IsInGameThread());
	if (bShutdown)
	{
		return;
	}
	bShutdown = true;
	Phase = EFinareGameplayDataPhaseV4::ShuttingDown;
	if (ObjectRegistry != nullptr)
	{
		auto ReleaseHandles = [this](TArray<FFinareObjectHandle>& Handles)
		{
			for (FFinareObjectHandle Handle : Handles)
			{
				FFinareObjectHandleResult Result;
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
	GeneratedEventChannels.Reset();
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

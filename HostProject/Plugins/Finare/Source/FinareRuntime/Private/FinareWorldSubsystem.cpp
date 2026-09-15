#include "FinareWorldSubsystem.h"

#include "Dom/JsonObject.h"
#include "Components/ActorComponent.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "Subsystems/SubsystemCollection.h"
#include "FinareCoreClrHost.h"
#include "FinareComponent.h"
#include "FinareAsset.h"
#include "FinareRuntimeDiagnostic.h"
#include "FinareWorldContext.h"

#include <atomic>

namespace UE::Finare::WorldHost::Private
{
	uint64 AllocateWorldHostId()
	{
		static std::atomic<uint64> NextWorldHostId{1};
		for (;;)
		{
			const uint64 Result = NextWorldHostId.fetch_add(1, std::memory_order_relaxed);
			if (Result != 0)
			{
				return Result;
			}
		}
	}

	void FailUnavailable(FFinareObjectHandleResult& OutResult)
	{
		OutResult.Error = EFinareObjectHandleError::RegistryWorldUnavailable;
		OutResult.Message =
			TEXT("The Finare world runtime host is not ready.");
	}

	const TCHAR* RuntimeOwnerName(EFinareRuntimeOwner RuntimeOwner)
	{
		return RuntimeOwner == EFinareRuntimeOwner::Puerts
			       ? TEXT("puerts")
			       : TEXT("unifiedScript");
	}
}

void UFinareWorldSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
	Super::Initialize(Collection);
	check(IsInGameThread());
	check(HostState == EFinareWorldHostState::Stopped);
	check(ObjectRegistry == nullptr);
	check(InvocationBridge == nullptr);
	check(SharedDataPlane == nullptr);
	check(GameplayDataPlane == nullptr);
	check(AsyncService == nullptr);
	check(NativeContext == nullptr);

	UWorld* World = GetWorld();
	check(World != nullptr);

	ObjectRegistry = MakeUnique<FFinareObjectRegistry>(World);
	InvocationBridge = MakeUnique<FFinareInvocationBridge>(*ObjectRegistry);
	NativeContext = new FFinareNativeBridgeContext(*InvocationBridge);
	WorldHostId = UE::Finare::WorldHost::Private::AllocateWorldHostId();
	AsyncService = MakeUnique<FFinareAsyncService>(
		*World,
		*ObjectRegistry,
		WorldHostId);
	NativeContext->AttachAsyncService(AsyncService.Get());
	SharedDataPlane = MakeUnique<FFinareSharedDataPlane>(
		*World,
		*ObjectRegistry,
		WorldHostId);
	NativeContext->AttachSharedDataPlane(SharedDataPlane.Get());
	SharedDataPlane->PublishTransformSnapshot();
	GameplayDataPlane = MakeUnique<FFinareGameplayDataPlane>(
		*World,
		*ObjectRegistry,
		WorldHostId);
	NativeContext->AttachGameplayDataPlane(GameplayDataPlane.Get());
	GameplayDataPlane->BeginFrame();
	PreActorTickHandle = FWorldDelegates::OnWorldPreActorTick.AddUObject(
		this,
		&UFinareWorldSubsystem::HandleWorldPreActorTick);
	PostActorTickHandle = FWorldDelegates::OnWorldPostActorTick.AddUObject(
		this,
		&UFinareWorldSubsystem::HandleWorldPostActorTick);
	HostState = EFinareWorldHostState::NativeReady;
	FFinareRuntimeBreadcrumbs::RecordLifecycle(
		TEXT("WorldInitialize.NativeReady"),
		WorldHostId,
		World->GetName());
	FFinareCoreClrHost& CoreClrHost = FFinareCoreClrHost::Get();
	if (CoreClrHost.Initialize())
	{
		FString Error;
		if (CoreClrHost.CreateManagedWorld(
			WorldHostId,
			NativeContext,
			GetNativeTransport(),
			Error))
		{
			HostState = EFinareWorldHostState::ManagedReady;
			FFinareRuntimeBreadcrumbs::RecordLifecycle(
				TEXT("WorldInitialize.ManagedReady"),
				WorldHostId,
				World->GetName());
		}
		else
		{
			FFinareRuntimeBreadcrumbs::RecordFailure(
				TEXT("WorldInitialize.CreateManagedWorld"),
				Error,
				WorldHostId,
				0,
				0);
			UE_LOG(LogTemp, Error, TEXT("%s"), *Error);
		}
	}
	else
	{
		FFinareRuntimeBreadcrumbs::RecordFailure(
			TEXT("WorldInitialize.CoreClr"),
			TEXT("The process CoreCLR host failed to initialize."),
			WorldHostId,
			0,
			0);
	}
}

void UFinareWorldSubsystem::Deinitialize()
{
	check(IsInGameThread());
	if (PreActorTickHandle.IsValid())
	{
		FWorldDelegates::OnWorldPreActorTick.Remove(PreActorTickHandle);
		PreActorTickHandle.Reset();
	}
	if (PostActorTickHandle.IsValid())
	{
		FWorldDelegates::OnWorldPostActorTick.Remove(PostActorTickHandle);
		PostActorTickHandle.Reset();
	}
	ShutdownNativeRuntime();
	Super::Deinitialize();
}

void UFinareWorldSubsystem::BeginManagedFrame()
{
	check(IsInGameThread());
	if (!IsNativeRuntimeReady())
	{
		return;
	}
	NativeContext->PumpGameThreadCalls();
	if (GameplayDataPlane != nullptr &&
		GameplayDataPlane->GetPhase() ==
		EFinareGameplayDataPhaseV4::ManagedTick)
	{
		GameplayDataPlane->EndFrame();
	}
	if (SharedDataPlane != nullptr)
	{
		SharedDataPlane->PublishTransformSnapshot();
	}
	if (GameplayDataPlane != nullptr)
	{
		GameplayDataPlane->BeginFrame();
	}
	PruneManagedReceiverBindings();
}

void UFinareWorldSubsystem::EndManagedFrame()
{
	check(IsInGameThread());
	if (GameplayDataPlane != nullptr && IsNativeRuntimeReady())
	{
		GameplayDataPlane->EndFrame();
	}
}

EFinareGameplayDataPhaseV4
UFinareWorldSubsystem::GetGameplayDataPhase() const
{
	return GameplayDataPlane != nullptr
		       ? GameplayDataPlane->GetPhase()
		       : EFinareGameplayDataPhaseV4::Idle;
}

uint64 UFinareWorldSubsystem::GetGameplayFrameNumber() const
{
	return GameplayDataPlane != nullptr
		       ? GameplayDataPlane->GetFrameNumber()
		       : 0;
}

bool UFinareWorldSubsystem::PublishGeneratedSnapshotRows(
	uint64 SchemaId,
	uint32 EntrySize,
	const void* Entries,
	uint32 EntryCount)
{
	check(IsInGameThread());
	return IsNativeRuntimeReady() && SharedDataPlane != nullptr &&
		SharedDataPlane->PublishGeneratedSnapshot(
			SchemaId,
			EntrySize,
			Entries,
			EntryCount);
}

bool UFinareWorldSubsystem::QueueGeneratedEventRows(
	uint64 SchemaId,
	uint32 EntrySize,
	const void* Entries,
	uint32 EntryCount)
{
	check(IsInGameThread());
	return IsNativeRuntimeReady() && GameplayDataPlane != nullptr &&
		GameplayDataPlane->QueueGeneratedEvents(
			SchemaId,
			EntrySize,
			Entries,
			EntryCount);
}

bool UFinareWorldSubsystem::RegisterGeneratedDataPlaneExtension(
	const FFinareDataPlaneExtensionDescriptor& Extension,
	FString& OutError)
{
	check(IsInGameThread());
	if (!IsNativeRuntimeReady())
	{
		OutError = TEXT("The World runtime is not ready for data-plane extensions.");
		return false;
	}
	return DataPlaneExtensionRegistry.Register(Extension, OutError);
}

bool UFinareWorldSubsystem::UnregisterGeneratedDataPlaneExtension(
	uint64 ExtensionKey,
	uint64 SchemaFingerprint)
{
	check(IsInGameThread());
	return DataPlaneExtensionRegistry.Unregister(ExtensionKey, SchemaFingerprint);
}

bool UFinareWorldSubsystem::PublishGeneratedSnapshotRows(
	const FFinareDataPlaneExtensionDescriptor& Extension,
	const FFinareDataPlaneExtensionChannelDescriptor& Channel,
	uint32 EntrySize,
	const void* Entries,
	uint32 EntryCount)
{
	check(IsInGameThread());
	FString Error;
	return IsNativeRuntimeReady() && SharedDataPlane != nullptr &&
		DataPlaneExtensionRegistry.ValidateRows(
			Extension,
			Channel,
			EFinareDataPlaneExtensionChannelKind::Snapshot,
			GetGameplayDataPhase(),
			EntrySize,
			Entries,
			EntryCount,
			Error) &&
		SharedDataPlane->PublishGeneratedSnapshot(
			Channel.SchemaId,
			EntrySize,
			Entries,
			EntryCount);
}

bool UFinareWorldSubsystem::QueueGeneratedEventRows(
	const FFinareDataPlaneExtensionDescriptor& Extension,
	const FFinareDataPlaneExtensionChannelDescriptor& Channel,
	uint32 EntrySize,
	const void* Entries,
	uint32 EntryCount)
{
	check(IsInGameThread());
	FString Error;
	return IsNativeRuntimeReady() && GameplayDataPlane != nullptr &&
		DataPlaneExtensionRegistry.ValidateRows(
			Extension,
			Channel,
			EFinareDataPlaneExtensionChannelKind::Event,
			GetGameplayDataPhase(),
			EntrySize,
			Entries,
			EntryCount,
			Error) &&
		GameplayDataPlane->QueueGeneratedEvents(
			Channel.SchemaId,
			EntrySize,
			Entries,
			EntryCount);
}

void UFinareWorldSubsystem::HandleWorldPreActorTick(
	UWorld* World,
	ELevelTick TickType,
	float DeltaSeconds)
{
	(void)TickType;
	(void)DeltaSeconds;
	if (World == GetWorld())
	{
		BeginManagedFrame();
	}
}

void UFinareWorldSubsystem::HandleWorldPostActorTick(
	UWorld* World,
	ELevelTick TickType,
	float DeltaSeconds)
{
	(void)TickType;
	(void)DeltaSeconds;
	if (World == GetWorld())
	{
		EndManagedFrame();
	}
}

EFinareWorldHostState UFinareWorldSubsystem::GetHostState() const
{
	return HostState;
}

bool UFinareWorldSubsystem::IsNativeRuntimeReady() const
{
	return (HostState == EFinareWorldHostState::NativeReady ||
			HostState == EFinareWorldHostState::ManagedReady) &&
		ObjectRegistry != nullptr &&
		InvocationBridge != nullptr &&
		NativeContext != nullptr;
}

bool UFinareWorldSubsystem::IsCoreClrRuntimeReady() const
{
	return FFinareCoreClrHost::Get().IsReady();
}

bool UFinareWorldSubsystem::IsManagedWorldReady() const
{
	return HostState == EFinareWorldHostState::ManagedReady;
}

uint64 UFinareWorldSubsystem::GetWorldHostId() const
{
	return WorldHostId;
}

bool UFinareWorldSubsystem::ClaimGameplayLifecycle(
	UObject* Claimant,
	UObject* HostObject,
	FName LifecycleKey,
	EFinareRuntimeOwner RuntimeOwner,
	FString& OutError)
{
	check(IsInGameThread());
	OutError.Reset();
	PruneGameplayLifecycleClaims();
	UWorld* World = GetWorld();
	if (!IsNativeRuntimeReady() || World == nullptr)
	{
		OutError = TEXT("The Finare native World host is unavailable.");
		return false;
	}
	if (!IsValid(Claimant) || !IsValid(HostObject) || LifecycleKey.IsNone())
	{
		OutError = TEXT("A lifecycle claim requires valid claimant/host objects and a non-empty key.");
		return false;
	}
	if (UE::Finare::WorldContext::Resolve(Claimant) != World ||
		UE::Finare::WorldContext::Resolve(HostObject) != World)
	{
		OutError = TEXT("Lifecycle claimant and host objects must belong to the same World host.");
		return false;
	}
	if (RuntimeOwner != EFinareRuntimeOwner::Puerts &&
		RuntimeOwner != EFinareRuntimeOwner::Finare)
	{
		OutError = TEXT("The requested gameplay lifecycle runtime owner is invalid.");
		return false;
	}

	const FFinareGameplayLifecycleClaimKey Key{
		FObjectKey(HostObject),
		LifecycleKey
	};
	if (const FFinareGameplayLifecycleClaim* Existing =
		GameplayLifecycleClaims.Find(Key))
	{
		if (Existing->ClaimantKey == FObjectKey(Claimant) &&
			Existing->RuntimeOwner == RuntimeOwner)
		{
			return true;
		}
		OutError = FString::Printf(
			TEXT(
				"Gameplay lifecycle '%s' on '%s' is already owned by %s claimant '%s'; %s claimant '%s' was rejected."),
			*LifecycleKey.ToString(),
			*HostObject->GetPathName(),
			UE::Finare::WorldHost::Private::RuntimeOwnerName(
				Existing->RuntimeOwner),
			Existing->Claimant.IsValid()
				? *Existing->Claimant->GetPathName()
				: TEXT("<retired>"),
			UE::Finare::WorldHost::Private::RuntimeOwnerName(RuntimeOwner),
			*Claimant->GetPathName());
		return false;
	}

	GameplayLifecycleClaims.Add(
		Key,
		FFinareGameplayLifecycleClaim{
			HostObject,
			Claimant,
			FObjectKey(Claimant),
			LifecycleKey,
			RuntimeOwner
		});
	return true;
}

bool UFinareWorldSubsystem::ReleaseGameplayLifecycle(
	UObject* Claimant,
	UObject* HostObject,
	FName LifecycleKey,
	EFinareRuntimeOwner RuntimeOwner,
	FString& OutError)
{
	check(IsInGameThread());
	OutError.Reset();
	PruneGameplayLifecycleClaims();
	if (!IsValid(Claimant) || !IsValid(HostObject) || LifecycleKey.IsNone())
	{
		OutError = TEXT("A lifecycle release requires valid claimant/host objects and a non-empty key.");
		return false;
	}
	const FFinareGameplayLifecycleClaimKey Key{
		FObjectKey(HostObject),
		LifecycleKey
	};
	const FFinareGameplayLifecycleClaim* Existing =
		GameplayLifecycleClaims.Find(Key);
	if (Existing == nullptr)
	{
		OutError = FString::Printf(
			TEXT("Gameplay lifecycle '%s' on '%s' has no active owner."),
			*LifecycleKey.ToString(),
			*HostObject->GetPathName());
		return false;
	}
	if (Existing->ClaimantKey != FObjectKey(Claimant) ||
		Existing->RuntimeOwner != RuntimeOwner)
	{
		OutError = FString::Printf(
			TEXT("Gameplay lifecycle '%s' can only be released by its %s claimant '%s'."),
			*LifecycleKey.ToString(),
			UE::Finare::WorldHost::Private::RuntimeOwnerName(
				Existing->RuntimeOwner),
			Existing->Claimant.IsValid()
				? *Existing->Claimant->GetPathName()
				: TEXT("<retired>"));
		return false;
	}
	GameplayLifecycleClaims.Remove(Key);
	return true;
}

bool UFinareWorldSubsystem::RegisterManagedReceiver(
	UObject* Receiver,
	UFinareComponent* ExecutionHost,
	FString& OutError)
{
	check(IsInGameThread());
	OutError.Reset();
	UWorld* World = GetWorld();
	if (!IsNativeRuntimeReady() || World == nullptr || !World->IsGameWorld())
	{
		OutError = TEXT("The Finare native World host is unavailable for runtime receiver registration.");
		return false;
	}
	if (!IsValid(Receiver) || !IsValid(ExecutionHost) ||
		Receiver->IsTemplate() || ExecutionHost->IsTemplate())
	{
		OutError = TEXT("A managed receiver route requires non-template receiver and execution host objects.");
		return false;
	}
	if (UE::Finare::WorldContext::Resolve(Receiver) != World ||
		UE::Finare::WorldContext::Resolve(ExecutionHost) != World)
	{
		OutError = TEXT("Managed receiver and execution host must belong to the same World host.");
		return false;
	}

	const FObjectKey Key(Receiver);
	if (FFinareManagedReceiverBinding* Existing = ManagedReceiverBindings.Find(Key))
	{
		if (Existing->Receiver.Get() == Receiver &&
			Existing->ExecutionHost.Get() == ExecutionHost)
		{
			return true;
		}
		OutError = FString::Printf(
			TEXT("Native receiver '%s' is already owned by execution host '%s'."),
			*Receiver->GetPathName(),
			Existing->ExecutionHost.IsValid()
				? *Existing->ExecutionHost->GetPathName()
				: TEXT("<retired>"));
		return false;
	}

	ManagedReceiverBindings.Add(
		Key,
		FFinareManagedReceiverBinding{Receiver, ExecutionHost});
	return true;
}

void UFinareWorldSubsystem::RetainManagedObjectExecutionHost(
	UFinareComponent* ExecutionHost)
{
	check(IsInGameThread());
	if (IsValid(ExecutionHost) &&
		UE::Finare::WorldContext::Resolve(ExecutionHost) == GetWorld())
	{
		ManagedObjectExecutionHosts.AddUnique(ExecutionHost);
	}
}

void UFinareWorldSubsystem::ReleaseManagedObjectExecutionHost(
	UFinareComponent* ExecutionHost)
{
	check(IsInGameThread());
	ManagedObjectExecutionHosts.Remove(ExecutionHost);
}

UFinareComponent* UFinareWorldSubsystem::FindManagedObjectExecutionHost(
	UObject* Receiver,
	UFinareAsset* ScriptAsset) const
{
	check(IsInGameThread());
	for (UFinareComponent* ExecutionHost : ManagedObjectExecutionHosts)
	{
		if (IsValid(ExecutionHost) &&
			ExecutionHost->GetNativeReceiver() == Receiver &&
			(ScriptAsset == nullptr || ExecutionHost->ScriptAsset == ScriptAsset))
		{
			return ExecutionHost;
		}
	}
	return nullptr;
}

void UFinareWorldSubsystem::UnregisterManagedReceiver(
	UObject* Receiver,
	UFinareComponent* ExecutionHost)
{
	check(IsInGameThread());
	if (Receiver == nullptr || ExecutionHost == nullptr)
	{
		return;
	}
	const FObjectKey Key(Receiver);
	if (const FFinareManagedReceiverBinding* Existing = ManagedReceiverBindings.Find(Key);
		Existing != nullptr && Existing->ExecutionHost.Get() == ExecutionHost)
	{
		ManagedReceiverBindings.Remove(Key);
	}
}

UFinareComponent* UFinareWorldSubsystem::ResolveManagedReceiver(
	UObject* Receiver,
	const UFinareBlueprintGeneratedClass* GeneratedClass,
	bool bConfigured,
	FString& OutError) const
{
	check(IsInGameThread());
	OutError.Reset();
	UWorld* World = GetWorld();
	if (!IsValid(Receiver) || World == nullptr ||
		UE::Finare::WorldContext::Resolve(Receiver) != World)
	{
		OutError = TEXT("The managed receiver is invalid or belongs to another World host.");
		return nullptr;
	}

	auto Matches = [GeneratedClass, bConfigured](UFinareComponent* Candidate)
	{
		if (Candidate == nullptr || !IsValid(Candidate))
		{
			return false;
		}
		if (bConfigured)
		{
			return GeneratedClass != nullptr &&
				Candidate->OwnsConfiguredManagedOverride(GeneratedClass);
		}
		return GeneratedClass == nullptr
			       ? Candidate->bScriptRunning
			       : Candidate->OwnsActiveManagedOverride(GeneratedClass);
	};

	const FObjectKey DirectKey(Receiver);
	if (const FFinareManagedReceiverBinding* Direct =
			ManagedReceiverBindings.Find(DirectKey))
	{
		UFinareComponent* Candidate = Direct->ExecutionHost.Get();
		if (Direct->Receiver.Get() == Receiver && Matches(Candidate))
		{
			return Candidate;
		}
		OutError = FString::Printf(
			TEXT("Native receiver '%s' has no matching %s managed generation."),
			*Receiver->GetPathName(),
			bConfigured ? TEXT("configured") : TEXT("active"));
		return nullptr;
	}

	// Actor dispatch is resolved from the receiver-to-host table rather than
	// by treating every component on the Actor as the same host.  This keeps
	// multiple ordinary native components independently addressable.
	UFinareComponent* Match = nullptr;
	for (const TPair<FObjectKey, FFinareManagedReceiverBinding>& Pair :
		 ManagedReceiverBindings)
	{
		UFinareComponent* Candidate = Pair.Value.ExecutionHost.Get();
		if (Candidate == nullptr ||
			Candidate->GetScriptTarget() != Receiver ||
			!Matches(Candidate))
		{
			continue;
		}
		if (Match != nullptr && Match != Candidate)
		{
			OutError = FString::Printf(
				TEXT("Native receiver '%s' has multiple matching managed execution hosts."),
				*Receiver->GetPathName());
			return nullptr;
		}
		Match = Candidate;
	}
	if (Match == nullptr)
	{
		OutError = FString::Printf(
			TEXT("Native receiver '%s' has no matching %s managed execution host."),
			*Receiver->GetPathName(),
			bConfigured ? TEXT("configured") : TEXT("active"));
	}
	return Match;
}

void UFinareWorldSubsystem::PruneManagedReceiverBindings()
{
	check(IsInGameThread());
	UWorld* World = GetWorld();
	if (World == nullptr || !World->IsGameWorld())
	{
		return;
	}

	TArray<FObjectKey, TInlineAllocator<8>> StaleKeys;
	TArray<UFinareComponent*, TInlineAllocator<8>> HostsToStop;
	TArray<UFinareComponent*, TInlineAllocator<8>> HostsToSuspend;
	TArray<UFinareComponent*, TInlineAllocator<8>> HostsToResume;
	for (TPair<FObjectKey, FFinareManagedReceiverBinding>& Pair :
		 ManagedReceiverBindings)
	{
		UObject* Receiver = Pair.Value.Receiver.Get();
		UFinareComponent* ExecutionHost = Pair.Value.ExecutionHost.Get();
		bool bStale = !IsValid(Receiver) || !IsValid(ExecutionHost) ||
			Receiver->IsTemplate() || ExecutionHost->IsTemplate() ||
			UE::Finare::WorldContext::Resolve(Receiver) != World ||
			UE::Finare::WorldContext::Resolve(ExecutionHost) != World;
		if (!bStale)
		{
			if (UActorComponent* ReceiverComponent = Cast<UActorComponent>(Receiver))
			{
				bStale = Receiver != ExecutionHost &&
					(ReceiverComponent->GetOwner() == nullptr ||
					 ReceiverComponent->GetOwner() != ExecutionHost->GetOwner());
				if (!bStale && Receiver != ExecutionHost)
				{
					if (!ReceiverComponent->IsRegistered())
					{
						// Unregister is reversible and need not emit EndPlay. Retain
						// the weak configuration, but release the managed generation.
						if (ExecutionHost->bScriptRunning)
						{
							Pair.Value.bResumeAfterRegistration = true;
							HostsToSuspend.AddUnique(ExecutionHost);
						}
						continue;
					}
					if (Pair.Value.bResumeAfterRegistration)
					{
						Pair.Value.bResumeAfterRegistration = false;
						HostsToResume.AddUnique(ExecutionHost);
					}
				}
			}
			else if (AActor* ReceiverActor = Cast<AActor>(Receiver))
			{
				bStale = ReceiverActor->IsActorBeingDestroyed();
			}
		}
		if (!bStale)
		{
			continue;
		}
		StaleKeys.Add(Pair.Key);
		if (ExecutionHost != nullptr)
		{
			HostsToStop.AddUnique(ExecutionHost);
		}
	}

	// Remove the routes before stopping components. StopScript/DestroyComponent
	// can synchronously invoke OnUnregister, so mutating the map while iterating
	// it would invalidate the traversal and could leave another route behind.
	for (const FObjectKey& Key : StaleKeys)
	{
		ManagedReceiverBindings.Remove(Key);
	}
	for (UFinareComponent* ExecutionHost : HostsToSuspend)
	{
		if (IsValid(ExecutionHost)) ExecutionHost->StopScript();
	}
	for (UFinareComponent* ExecutionHost : HostsToResume)
	{
		if (IsValid(ExecutionHost)) ExecutionHost->StartScript();
	}
	for (UFinareComponent* ExecutionHost : HostsToStop)
	{
		if (!IsValid(ExecutionHost))
		{
			continue;
		}
		ExecutionHost->StopScript();
		if (ExecutionHost->IsRegistered())
		{
			ExecutionHost->DestroyComponent();
		}
	}
}

int32 UFinareWorldSubsystem::GetGameplayLifecycleClaimCount() const
{
	int32 Count = 0;
	const UWorld* World = GetWorld();
	for (const TPair<
		     FFinareGameplayLifecycleClaimKey,
		     FFinareGameplayLifecycleClaim>& Pair : GameplayLifecycleClaims)
	{
		const FFinareGameplayLifecycleClaim& Claim = Pair.Value;
		if (Claim.HostObject.IsValid() && Claim.Claimant.IsValid() &&
			Claim.HostObject->GetWorld() == World && Claim.Claimant->GetWorld() == World)
		{
			++Count;
		}
	}
	return Count;
}

FString UFinareWorldSubsystem::BuildGameplayLifecycleOwnershipReport() const
{
	TArray<const FFinareGameplayLifecycleClaim*> Claims;
	const UWorld* World = GetWorld();
	for (const TPair<
		     FFinareGameplayLifecycleClaimKey,
		     FFinareGameplayLifecycleClaim>& Pair : GameplayLifecycleClaims)
	{
		const FFinareGameplayLifecycleClaim& Claim = Pair.Value;
		if (Claim.HostObject.IsValid() && Claim.Claimant.IsValid() &&
			Claim.HostObject->GetWorld() == World && Claim.Claimant->GetWorld() == World)
		{
			Claims.Add(&Claim);
		}
	}
	Claims.Sort([](
		const FFinareGameplayLifecycleClaim& Left,
		const FFinareGameplayLifecycleClaim& Right)
		{
			const int32 KeyOrder = Left.LifecycleKey.Compare(Right.LifecycleKey);
			return KeyOrder != 0
				       ? KeyOrder < 0
				       : Left.HostObject->GetPathName() < Right.HostObject->GetPathName();
		});

	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(
		TEXT("schemaVersion"),
		TEXT("unifiedscript.runtime-ownership-report/v0.1"));
	Root->SetStringField(TEXT("worldHostId"), LexToString(WorldHostId));
	Root->SetNumberField(TEXT("claimCount"), Claims.Num());
	TArray<TSharedPtr<FJsonValue>> JsonClaims;
	for (const FFinareGameplayLifecycleClaim* Claim : Claims)
	{
		const TSharedRef<FJsonObject> JsonClaim = MakeShared<FJsonObject>();
		JsonClaim->SetStringField(TEXT("lifecycleKey"), Claim->LifecycleKey.ToString());
		JsonClaim->SetStringField(
			TEXT("runtimeOwner"),
			UE::Finare::WorldHost::Private::RuntimeOwnerName(
				Claim->RuntimeOwner));
		JsonClaim->SetStringField(
			TEXT("hostObject"),
			Claim->HostObject->GetPathName());
		JsonClaim->SetStringField(
			TEXT("claimant"),
			Claim->Claimant->GetPathName());
		JsonClaims.Add(MakeShared<FJsonValueObject>(JsonClaim));
	}
	Root->SetArrayField(TEXT("claims"), MoveTemp(JsonClaims));
	FString Json;
	const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
		TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Json);
	return FJsonSerializer::Serialize(Root, Writer) ? Json : FString();
}

void UFinareWorldSubsystem::PruneGameplayLifecycleClaims()
{
	UWorld* World = GetWorld();
	for (auto Iterator = GameplayLifecycleClaims.CreateIterator(); Iterator; ++Iterator)
	{
		const FFinareGameplayLifecycleClaim& Claim = Iterator.Value();
		if (!Claim.HostObject.IsValid() || !Claim.Claimant.IsValid() ||
			Claim.HostObject->GetWorld() != World || Claim.Claimant->GetWorld() != World)
		{
			Iterator.RemoveCurrent();
		}
	}
}

FFinareObjectHandle UFinareWorldSubsystem::RegisterObject(
	UObject* Object,
	FFinareObjectHandleResult& OutResult)
{
	if (!IsNativeRuntimeReady())
	{
		UE::Finare::WorldHost::Private::FailUnavailable(OutResult);
		return {};
	}
	return ObjectRegistry->RegisterObject(Object, OutResult);
}

UObject* UFinareWorldSubsystem::ResolveObject(
	FFinareObjectHandle Handle,
	FFinareObjectHandleResult& OutResult)
{
	if (!IsNativeRuntimeReady())
	{
		UE::Finare::WorldHost::Private::FailUnavailable(OutResult);
		return nullptr;
	}
	return ObjectRegistry->ResolveObject(Handle, OutResult);
}

void UFinareWorldSubsystem::RetainLoadedResource(UObject* Resource)
{
	check(IsInGameThread());
	if (IsNativeRuntimeReady() && IsValid(Resource))
	{
		LoadedResources.AddUnique(Resource);
	}
}

bool UFinareWorldSubsystem::ReleaseObject(
	FFinareObjectHandle Handle,
	FFinareObjectHandleResult& OutResult)
{
	if (!IsNativeRuntimeReady())
	{
		UE::Finare::WorldHost::Private::FailUnavailable(OutResult);
		return false;
	}
	return ObjectRegistry->ReleaseObject(Handle, OutResult);
}

bool UFinareWorldSubsystem::CreateManagedScript(
	uint64 InstanceId,
	FFinareObjectHandle Host,
	FFinareObjectHandle Target,
	const FString& AssemblyPath,
	const FString& TypeName,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	if (InstanceId == 0 || Host.IsNull() || Target.IsNull())
	{
		OutError = TEXT("A managed script requires non-null instance, host, and target handles.");
		return false;
	}
	return FFinareCoreClrHost::Get().CreateManagedScript(
		WorldHostId,
		InstanceId,
		Host.Value,
		Target.Value,
		AssemblyPath,
		TypeName,
		OutError);
}

bool UFinareWorldSubsystem::CreateManagedScriptWithBlueprintContract(
	uint64 InstanceId,
	FFinareObjectHandle Host,
	FFinareObjectHandle Target,
	const FString& AssemblyPath,
	const FString& TypeName,
	const FString& BlueprintOverrideContractJson,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	if (InstanceId == 0 || Host.IsNull() || Target.IsNull())
	{
		OutError = TEXT("A managed script requires non-null instance, host, and target handles.");
		return false;
	}
	return FFinareCoreClrHost::Get().CreateManagedScriptWithBlueprintContract(
		WorldHostId,
		InstanceId,
		Host.Value,
		Target.Value,
		AssemblyPath,
		TypeName,
		BlueprintOverrideContractJson,
		OutError);
}

bool UFinareWorldSubsystem::TickManagedScript(
	uint64 InstanceId,
	float DeltaSeconds,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().TickManagedScript(
		WorldHostId,
		InstanceId,
		DeltaSeconds,
		OutError);
}

bool UFinareWorldSubsystem::ConfigureManagedScriptDebug(
	uint64 InstanceId,
	const FString& ConfigurationJson,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().ConfigureManagedScriptDebug(
		WorldHostId, InstanceId, ConfigurationJson, OutError);
}

bool UFinareWorldSubsystem::BeginManagedScriptDebugTick(
	uint64 InstanceId,
	float DeltaSeconds,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().BeginManagedScriptDebugTick(
		WorldHostId, InstanceId, DeltaSeconds, OutError);
}

bool UFinareWorldSubsystem::GetManagedScriptDebugState(
	uint64 InstanceId,
	FString& OutJson,
	FString& OutError) const
{
	if (!IsManagedWorldReady())
	{
		OutJson.Reset();
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().GetManagedScriptDebugState(
		WorldHostId, InstanceId, OutJson, OutError);
}

bool UFinareWorldSubsystem::ControlManagedScriptDebug(
	uint64 InstanceId,
	int32 Control,
	const FString& PauseId,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().ControlManagedScriptDebug(
		WorldHostId, InstanceId, Control, PauseId, OutError);
}

bool UFinareWorldSubsystem::DisableManagedScriptDebug(
	uint64 InstanceId,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().DisableManagedScriptDebug(
		WorldHostId, InstanceId, OutError);
}

bool UFinareWorldSubsystem::IsManagedScriptDebugActive(
	uint64 InstanceId,
	bool& bOutActive,
	FString& OutError) const
{
	if (!IsManagedWorldReady())
	{
		bOutActive = false;
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().IsManagedScriptDebugActive(
		WorldHostId, InstanceId, bOutActive, OutError);
}

void UFinareWorldSubsystem::PumpManagedBridgeCalls()
{
	check(IsInGameThread());
	if (IsNativeRuntimeReady())
	{
		NativeContext->PumpGameThreadCalls();
	}
}

bool UFinareWorldSubsystem::DispatchManagedEvent(
	uint64 InstanceId,
	int32 EventKind,
	FFinareObjectHandle Source,
	FFinareObjectHandle Argument,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	if (InstanceId == 0 || EventKind <= 0 || Source.IsNull())
	{
		OutError = TEXT("A managed event requires an instance, kind, and source handle.");
		return false;
	}
	return FFinareCoreClrHost::Get().DispatchManagedEvent(
		WorldHostId,
		InstanceId,
		EventKind,
		Source.Value,
		Argument.Value,
		OutError);
}

bool UFinareWorldSubsystem::InvokeManagedScriptFunction(
	uint64 InstanceId,
	const FString& FunctionName,
	TConstArrayView<FFinareManagedFunctionValueV1> Arguments,
	FFinareManagedFunctionValueV1& OutResult,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutResult = {};
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().InvokeManagedScriptFunction(
		WorldHostId,
		InstanceId,
		FunctionName,
		Arguments,
		OutResult,
		OutError);
}

bool UFinareWorldSubsystem::InvokeManagedScriptFunctionV2(
	uint64 InstanceId,
	const FString& AssemblyPath,
	const FString& TypeName,
	const FString& FunctionName,
	FFinareNativeCallFrameV3& Frame,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().InvokeManagedScriptFunctionV2(
		WorldHostId,
		InstanceId,
		AssemblyPath,
		TypeName,
		FunctionName,
		Frame,
		OutError);
}

bool UFinareWorldSubsystem::ReloadManagedScript(
	uint64 InstanceId,
	const FString& AssemblyPath,
	const FString& TypeName,
	int32& OutGeneration,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutGeneration = 0;
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().ReloadManagedScript(
		WorldHostId,
		InstanceId,
		AssemblyPath,
		TypeName,
		OutGeneration,
		OutError);
}

bool UFinareWorldSubsystem::PrepareManagedScriptReload(
	uint64 InstanceId,
	const FString& AssemblyPath,
	const FString& TypeName,
	int32& OutCandidateGeneration,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutCandidateGeneration = 0;
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().PrepareManagedScriptReload(
		WorldHostId,
		InstanceId,
		AssemblyPath,
		TypeName,
		OutCandidateGeneration,
		OutError);
}

bool UFinareWorldSubsystem::CommitManagedScriptReload(
	uint64 InstanceId,
	int32 CandidateGeneration,
	int32& OutActiveGeneration,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutActiveGeneration = 0;
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().CommitManagedScriptReload(
		WorldHostId,
		InstanceId,
		CandidateGeneration,
		OutActiveGeneration,
		OutError);
}

bool UFinareWorldSubsystem::AbortManagedScriptReload(
	uint64 InstanceId,
	int32 CandidateGeneration,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().AbortManagedScriptReload(
		WorldHostId,
		InstanceId,
		CandidateGeneration,
		OutError);
}

bool UFinareWorldSubsystem::FinalizeManagedScriptReload(
	uint64 InstanceId,
	int32 CandidateGeneration,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().FinalizeManagedScriptReload(
		WorldHostId,
		InstanceId,
		CandidateGeneration,
		OutError);
}

bool UFinareWorldSubsystem::PrepareManagedStaticLibraryReload(
	const FString& AssemblyPath,
	const FString& TypeName,
	int32& OutCandidateGeneration,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutCandidateGeneration = 0;
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().PrepareManagedStaticLibraryReload(
		WorldHostId,
		AssemblyPath,
		TypeName,
		OutCandidateGeneration,
		OutError);
}

bool UFinareWorldSubsystem::CommitManagedStaticLibraryReload(
	const FString& AssemblyPath,
	const FString& TypeName,
	int32 CandidateGeneration,
	int32& OutActiveGeneration,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutActiveGeneration = 0;
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().CommitManagedStaticLibraryReload(
		WorldHostId,
		AssemblyPath,
		TypeName,
		CandidateGeneration,
		OutActiveGeneration,
		OutError);
}

bool UFinareWorldSubsystem::AbortManagedStaticLibraryReload(
	const FString& AssemblyPath,
	const FString& TypeName,
	int32 CandidateGeneration,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().AbortManagedStaticLibraryReload(
		WorldHostId,
		AssemblyPath,
		TypeName,
		CandidateGeneration,
		OutError);
}

bool UFinareWorldSubsystem::FinalizeManagedStaticLibraryReload(
	const FString& AssemblyPath,
	const FString& TypeName,
	int32 CandidateGeneration,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().FinalizeManagedStaticLibraryReload(
		WorldHostId,
		AssemblyPath,
		TypeName,
		CandidateGeneration,
		OutError);
}

bool UFinareWorldSubsystem::GetManagedScriptReloadState(
	uint64 InstanceId,
	FString& OutJson,
	FString& OutError) const
{
	if (!IsManagedWorldReady())
	{
		OutJson.Reset();
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().GetManagedScriptReloadState(
		WorldHostId,
		InstanceId,
		OutJson,
		OutError);
}

bool UFinareWorldSubsystem::SetManagedScriptProfilerEnabled(
	uint64 InstanceId,
	bool bEnabled,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().SetManagedScriptProfilerEnabled(
		WorldHostId,
		InstanceId,
		bEnabled,
		OutError);
}

bool UFinareWorldSubsystem::GetManagedScriptProfilerTrace(
	uint64 InstanceId,
	FString& OutJson,
	FString& OutError) const
{
	if (!IsManagedWorldReady())
	{
		OutJson.Reset();
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().GetManagedScriptProfilerTrace(
		WorldHostId,
		InstanceId,
		OutJson,
		OutError);
}

bool UFinareWorldSubsystem::DestroyManagedScript(
	uint64 InstanceId,
	FString& OutError)
{
	if (!IsManagedWorldReady())
	{
		OutError = TEXT("The Finare managed World is not ready.");
		return false;
	}
	return FFinareCoreClrHost::Get().DestroyManagedScript(
		WorldHostId,
		InstanceId,
		OutError);
}

int32 UFinareWorldSubsystem::GetManagedScriptCount() const
{
	return IsManagedWorldReady()
		       ? FFinareCoreClrHost::Get().GetManagedScriptCount(WorldHostId)
		       : 0;
}

bool UFinareWorldSubsystem::GetManagedInvocationMetrics(
	uint64& OutCallFrameInvocationCount,
	uint64& OutLegacyInvocationCount) const
{
	OutCallFrameInvocationCount = 0;
	OutLegacyInvocationCount = 0;
	return IsManagedWorldReady() &&
		FFinareCoreClrHost::Get().GetManagedInvocationMetrics(
			WorldHostId,
			OutCallFrameInvocationCount,
			OutLegacyInvocationCount);
}

int32 UFinareWorldSubsystem::NumRegisteredObjects() const
{
	return IsNativeRuntimeReady() ? ObjectRegistry->Num() : 0;
}

int32 UFinareWorldSubsystem::GetEventSubscriptionCount() const
{
	return NativeContext != nullptr ? NativeContext->GetEventSubscriptionCount() : 0;
}

uint64 UFinareWorldSubsystem::GetFastInvocationCount() const
{
	return InvocationBridge != nullptr
		       ? InvocationBridge->GetFastInvocationCount()
		       : 0;
}

int32 UFinareWorldSubsystem::GetPendingAsyncRequestCount() const
{
	return AsyncService != nullptr ? AsyncService->NumPending() : 0;
}

int32 UFinareWorldSubsystem::GetQueuedAsyncCompletionCount() const
{
	return AsyncService != nullptr ? AsyncService->NumCompleted() : 0;
}

int32 UFinareWorldSubsystem::GetOwnedAsyncResourceCount() const
{
	return AsyncService != nullptr ? AsyncService->NumOwnedResources() : 0;
}

FFinareNativeBridgeContext*
UFinareWorldSubsystem::GetNativeInvocationContext() const
{
	return IsNativeRuntimeReady() ? NativeContext : nullptr;
}

const FFinareNativeTransportV2*
UFinareWorldSubsystem::GetNativeTransport() const
{
	return Finare_GetNativeTransportV2();
}

void UFinareWorldSubsystem::ShutdownNativeRuntime()
{
	if (HostState == EFinareWorldHostState::Stopped)
	{
		return;
	}

	const bool bManagedWorldWasReady = IsManagedWorldReady();
	const uint64 ShuttingDownWorldHostId = WorldHostId;
	// Ordinary UObject scripts have no UE BeginPlay/EndPlay callback. Stop their
	// World-owned sidecars while the managed World is still alive so their
	// generation and object leases are retired through the normal path.
	TArray<UFinareComponent*, TInlineAllocator<8>> ObjectHostsToStop;
	for (TObjectPtr<UFinareComponent> ObjectHost : ManagedObjectExecutionHosts)
	{
		ObjectHostsToStop.Add(ObjectHost.Get());
	}
	for (UFinareComponent* ObjectHost : ObjectHostsToStop)
	{
		if (IsValid(ObjectHost))
		{
			ObjectHost->StopScript();
		}
	}
	ManagedObjectExecutionHosts.Reset();
	HostState = EFinareWorldHostState::ShuttingDown;
	GameplayLifecycleClaims.Reset();
	ManagedReceiverBindings.Reset();
	DataPlaneExtensionRegistry.Reset();
	FFinareRuntimeBreadcrumbs::RecordLifecycle(
		TEXT("WorldShutdown.Begin"),
		ShuttingDownWorldHostId);
	if (NativeContext != nullptr)
	{
		NativeContext->SuspendWorkerDispatch();
	}
	FFinareRuntimeBreadcrumbs::RecordLifecycle(
		TEXT("WorldShutdown.WorkerDispatchSuspended"),
		ShuttingDownWorldHostId);
	if (bManagedWorldWasReady)
	{
		FString Error;
		if (FFinareCoreClrHost::Get().DestroyManagedWorld(WorldHostId, Error))
		{
			FFinareRuntimeBreadcrumbs::RecordLifecycle(
				TEXT("WorldShutdown.ManagedWorldDestroyed"),
				ShuttingDownWorldHostId);
		}
		else
		{
			FFinareRuntimeBreadcrumbs::RecordFailure(
				TEXT("WorldShutdown.DestroyManagedWorld"),
				Error,
				ShuttingDownWorldHostId,
				0,
				0);
			ensureMsgf(false, TEXT("%s"), *Error);
		}
	}
	else
	{
		FFinareRuntimeBreadcrumbs::RecordLifecycle(
			TEXT("WorldShutdown.ManagedWorldSkipped"),
			ShuttingDownWorldHostId);
	}
	if (NativeContext != nullptr)
	{
		NativeContext->Detach();
	}
	FFinareRuntimeBreadcrumbs::RecordLifecycle(
		TEXT("WorldShutdown.NativeContextDetached"),
		ShuttingDownWorldHostId);

	if (SharedDataPlane != nullptr)
	{
		SharedDataPlane->Shutdown();
		SharedDataPlane.Reset();
	}
	if (GameplayDataPlane != nullptr)
	{
		GameplayDataPlane->Shutdown();
		GameplayDataPlane.Reset();
	}
	if (AsyncService != nullptr)
	{
		AsyncService->Shutdown();
		AsyncService.Reset();
	}
	FFinareRuntimeBreadcrumbs::RecordLifecycle(
		TEXT("WorldShutdown.ServicesStopped"),
		ShuttingDownWorldHostId);

	if (ObjectRegistry != nullptr)
	{
		FFinareObjectHandleResult Result;
		ObjectRegistry->InvalidateAll(Result);
		ensureMsgf(
			Result.IsSuccess(),
			TEXT("Finare failed to invalidate world handles: %s"),
			*Result.Message);
	}
	FFinareRuntimeBreadcrumbs::RecordLifecycle(
		TEXT("WorldShutdown.HandlesInvalidated"),
		ShuttingDownWorldHostId);

	InvocationBridge.Reset();
	ObjectRegistry.Reset();
	if (NativeContext != nullptr)
	{
		NativeContext->Release();
		NativeContext = nullptr;
	}
	WorldHostId = 0;
	LoadedResources.Reset();
	HostState = EFinareWorldHostState::Stopped;
	FFinareRuntimeBreadcrumbs::RecordLifecycle(
		TEXT("WorldShutdown.Complete"),
		ShuttingDownWorldHostId);
}

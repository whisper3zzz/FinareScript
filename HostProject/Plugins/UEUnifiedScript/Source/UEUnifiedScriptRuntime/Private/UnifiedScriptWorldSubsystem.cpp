#include "UnifiedScriptWorldSubsystem.h"

#include "Engine/World.h"
#include "Subsystems/SubsystemCollection.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptRuntimeDiagnostic.h"

#include <atomic>

namespace UE::UnifiedScript::WorldHost::Private
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

void FailUnavailable(FUnifiedScriptObjectHandleResult& OutResult)
{
    OutResult.Error = EUnifiedScriptObjectHandleError::RegistryWorldUnavailable;
    OutResult.Message =
        TEXT("The UnifiedScript world runtime host is not ready.");
}
}

void UUnifiedScriptWorldSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
    Super::Initialize(Collection);
    check(IsInGameThread());
    check(HostState == EUnifiedScriptWorldHostState::Stopped);
    check(ObjectRegistry == nullptr);
    check(InvocationBridge == nullptr);
    check(SharedDataPlane == nullptr);
    check(GameplayDataPlane == nullptr);
    check(AsyncService == nullptr);
    check(NativeContext == nullptr);

    UWorld* World = GetWorld();
    check(World != nullptr);

    ObjectRegistry = MakeUnique<FUnifiedScriptObjectRegistry>(World);
    InvocationBridge = MakeUnique<FUnifiedScriptInvocationBridge>(*ObjectRegistry);
    NativeContext = new FUnifiedScriptNativeBridgeContext(*InvocationBridge);
    WorldHostId = UE::UnifiedScript::WorldHost::Private::AllocateWorldHostId();
    AsyncService = MakeUnique<FUnifiedScriptAsyncService>(
        *World,
        *ObjectRegistry,
        WorldHostId);
    NativeContext->AttachAsyncService(AsyncService.Get());
    SharedDataPlane = MakeUnique<FUnifiedScriptSharedDataPlane>(
        *World,
        *ObjectRegistry,
        WorldHostId);
    NativeContext->AttachSharedDataPlane(SharedDataPlane.Get());
    SharedDataPlane->PublishTransformSnapshot();
    GameplayDataPlane = MakeUnique<FUnifiedScriptGameplayDataPlane>(
        *World,
        *ObjectRegistry,
        WorldHostId);
    NativeContext->AttachGameplayDataPlane(GameplayDataPlane.Get());
    GameplayDataPlane->BeginFrame();
    PreActorTickHandle = FWorldDelegates::OnWorldPreActorTick.AddUObject(
        this,
        &UUnifiedScriptWorldSubsystem::HandleWorldPreActorTick);
    PostActorTickHandle = FWorldDelegates::OnWorldPostActorTick.AddUObject(
        this,
        &UUnifiedScriptWorldSubsystem::HandleWorldPostActorTick);
    HostState = EUnifiedScriptWorldHostState::NativeReady;
    FUnifiedScriptRuntimeBreadcrumbs::RecordLifecycle(
        TEXT("WorldInitialize.NativeReady"),
        WorldHostId,
        World->GetName());
    FUnifiedScriptCoreClrHost& CoreClrHost = FUnifiedScriptCoreClrHost::Get();
    if (CoreClrHost.Initialize())
    {
        FString Error;
        if (CoreClrHost.CreateManagedWorld(
                WorldHostId,
                NativeContext,
                GetNativeTransport(),
                Error))
        {
            HostState = EUnifiedScriptWorldHostState::ManagedReady;
            FUnifiedScriptRuntimeBreadcrumbs::RecordLifecycle(
                TEXT("WorldInitialize.ManagedReady"),
                WorldHostId,
                World->GetName());
        }
        else
        {
            FUnifiedScriptRuntimeBreadcrumbs::RecordFailure(
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
        FUnifiedScriptRuntimeBreadcrumbs::RecordFailure(
            TEXT("WorldInitialize.CoreClr"),
            TEXT("The process CoreCLR host failed to initialize."),
            WorldHostId,
            0,
            0);
    }
}

void UUnifiedScriptWorldSubsystem::Deinitialize()
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

void UUnifiedScriptWorldSubsystem::BeginManagedFrame()
{
    check(IsInGameThread());
    if (!IsNativeRuntimeReady())
    {
        return;
    }
    NativeContext->PumpGameThreadCalls();
    if (GameplayDataPlane != nullptr &&
        GameplayDataPlane->GetPhase() ==
            EUnifiedScriptGameplayDataPhaseV4::ManagedTick)
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
}

void UUnifiedScriptWorldSubsystem::EndManagedFrame()
{
    check(IsInGameThread());
    if (GameplayDataPlane != nullptr && IsNativeRuntimeReady())
    {
        GameplayDataPlane->EndFrame();
    }
}

EUnifiedScriptGameplayDataPhaseV4
UUnifiedScriptWorldSubsystem::GetGameplayDataPhase() const
{
    return GameplayDataPlane != nullptr
        ? GameplayDataPlane->GetPhase()
        : EUnifiedScriptGameplayDataPhaseV4::Idle;
}

uint64 UUnifiedScriptWorldSubsystem::GetGameplayFrameNumber() const
{
    return GameplayDataPlane != nullptr
        ? GameplayDataPlane->GetFrameNumber()
        : 0;
}

bool UUnifiedScriptWorldSubsystem::PublishGeneratedSnapshotRows(
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

bool UUnifiedScriptWorldSubsystem::QueueGeneratedEventRows(
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

void UUnifiedScriptWorldSubsystem::HandleWorldPreActorTick(
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

void UUnifiedScriptWorldSubsystem::HandleWorldPostActorTick(
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

EUnifiedScriptWorldHostState UUnifiedScriptWorldSubsystem::GetHostState() const
{
    return HostState;
}

bool UUnifiedScriptWorldSubsystem::IsNativeRuntimeReady() const
{
    return (HostState == EUnifiedScriptWorldHostState::NativeReady ||
            HostState == EUnifiedScriptWorldHostState::ManagedReady) &&
        ObjectRegistry != nullptr &&
        InvocationBridge != nullptr &&
        NativeContext != nullptr;
}

bool UUnifiedScriptWorldSubsystem::IsCoreClrRuntimeReady() const
{
    return FUnifiedScriptCoreClrHost::Get().IsReady();
}

bool UUnifiedScriptWorldSubsystem::IsManagedWorldReady() const
{
    return HostState == EUnifiedScriptWorldHostState::ManagedReady;
}

uint64 UUnifiedScriptWorldSubsystem::GetWorldHostId() const
{
    return WorldHostId;
}

FUnifiedScriptObjectHandle UUnifiedScriptWorldSubsystem::RegisterObject(
    UObject* Object,
    FUnifiedScriptObjectHandleResult& OutResult)
{
    if (!IsNativeRuntimeReady())
    {
        UE::UnifiedScript::WorldHost::Private::FailUnavailable(OutResult);
        return {};
    }
    return ObjectRegistry->RegisterObject(Object, OutResult);
}

void UUnifiedScriptWorldSubsystem::RetainLoadedResource(UObject* Resource)
{
    check(IsInGameThread());
    if (IsNativeRuntimeReady() && IsValid(Resource))
    {
        LoadedResources.AddUnique(Resource);
    }
}

bool UUnifiedScriptWorldSubsystem::ReleaseObject(
    FUnifiedScriptObjectHandle Handle,
    FUnifiedScriptObjectHandleResult& OutResult)
{
    if (!IsNativeRuntimeReady())
    {
        UE::UnifiedScript::WorldHost::Private::FailUnavailable(OutResult);
        return false;
    }
    return ObjectRegistry->ReleaseObject(Handle, OutResult);
}

bool UUnifiedScriptWorldSubsystem::CreateManagedScript(
    uint64 InstanceId,
    FUnifiedScriptObjectHandle Host,
    FUnifiedScriptObjectHandle Target,
    const FString& AssemblyPath,
    const FString& TypeName,
    FString& OutError)
{
    if (!IsManagedWorldReady())
    {
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    if (InstanceId == 0 || Host.IsNull() || Target.IsNull())
    {
        OutError = TEXT("A managed script requires non-null instance, host, and target handles.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().CreateManagedScript(
        WorldHostId,
        InstanceId,
        Host.Value,
        Target.Value,
        AssemblyPath,
        TypeName,
        OutError);
}

bool UUnifiedScriptWorldSubsystem::TickManagedScript(
    uint64 InstanceId,
    float DeltaSeconds,
    FString& OutError)
{
    if (!IsManagedWorldReady())
    {
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().TickManagedScript(
        WorldHostId,
        InstanceId,
        DeltaSeconds,
        OutError);
}

bool UUnifiedScriptWorldSubsystem::ConfigureManagedScriptDebug(
    uint64 InstanceId,
    const FString& ConfigurationJson,
    FString& OutError)
{
    if (!IsManagedWorldReady())
    {
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().ConfigureManagedScriptDebug(
        WorldHostId, InstanceId, ConfigurationJson, OutError);
}

bool UUnifiedScriptWorldSubsystem::BeginManagedScriptDebugTick(
    uint64 InstanceId,
    float DeltaSeconds,
    FString& OutError)
{
    if (!IsManagedWorldReady())
    {
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().BeginManagedScriptDebugTick(
        WorldHostId, InstanceId, DeltaSeconds, OutError);
}

bool UUnifiedScriptWorldSubsystem::GetManagedScriptDebugState(
    uint64 InstanceId,
    FString& OutJson,
    FString& OutError) const
{
    if (!IsManagedWorldReady())
    {
        OutJson.Reset();
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().GetManagedScriptDebugState(
        WorldHostId, InstanceId, OutJson, OutError);
}

bool UUnifiedScriptWorldSubsystem::ControlManagedScriptDebug(
    uint64 InstanceId,
    int32 Control,
    const FString& PauseId,
    FString& OutError)
{
    if (!IsManagedWorldReady())
    {
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().ControlManagedScriptDebug(
        WorldHostId, InstanceId, Control, PauseId, OutError);
}

bool UUnifiedScriptWorldSubsystem::DisableManagedScriptDebug(
    uint64 InstanceId,
    FString& OutError)
{
    if (!IsManagedWorldReady())
    {
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().DisableManagedScriptDebug(
        WorldHostId, InstanceId, OutError);
}

bool UUnifiedScriptWorldSubsystem::IsManagedScriptDebugActive(
    uint64 InstanceId,
    bool& bOutActive,
    FString& OutError) const
{
    if (!IsManagedWorldReady())
    {
        bOutActive = false;
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().IsManagedScriptDebugActive(
        WorldHostId, InstanceId, bOutActive, OutError);
}

void UUnifiedScriptWorldSubsystem::PumpManagedBridgeCalls()
{
    check(IsInGameThread());
    if (IsNativeRuntimeReady())
    {
        NativeContext->PumpGameThreadCalls();
    }
}

bool UUnifiedScriptWorldSubsystem::DispatchManagedEvent(
    uint64 InstanceId,
    int32 EventKind,
    FUnifiedScriptObjectHandle Source,
    FUnifiedScriptObjectHandle Argument,
    FString& OutError)
{
    if (!IsManagedWorldReady())
    {
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    if (InstanceId == 0 || EventKind <= 0 || Source.IsNull())
    {
        OutError = TEXT("A managed event requires an instance, kind, and source handle.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().DispatchManagedEvent(
        WorldHostId,
        InstanceId,
        EventKind,
        Source.Value,
        Argument.Value,
        OutError);
}

bool UUnifiedScriptWorldSubsystem::ReloadManagedScript(
    uint64 InstanceId,
    const FString& AssemblyPath,
    const FString& TypeName,
    int32& OutGeneration,
    FString& OutError)
{
    if (!IsManagedWorldReady())
    {
        OutGeneration = 0;
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().ReloadManagedScript(
        WorldHostId,
        InstanceId,
        AssemblyPath,
        TypeName,
        OutGeneration,
        OutError);
}

bool UUnifiedScriptWorldSubsystem::GetManagedScriptReloadState(
    uint64 InstanceId,
    FString& OutJson,
    FString& OutError) const
{
    if (!IsManagedWorldReady())
    {
        OutJson.Reset();
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().GetManagedScriptReloadState(
        WorldHostId,
        InstanceId,
        OutJson,
        OutError);
}

bool UUnifiedScriptWorldSubsystem::SetManagedScriptProfilerEnabled(
    uint64 InstanceId,
    bool bEnabled,
    FString& OutError)
{
    if (!IsManagedWorldReady())
    {
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().SetManagedScriptProfilerEnabled(
        WorldHostId,
        InstanceId,
        bEnabled,
        OutError);
}

bool UUnifiedScriptWorldSubsystem::GetManagedScriptProfilerTrace(
    uint64 InstanceId,
    FString& OutJson,
    FString& OutError) const
{
    if (!IsManagedWorldReady())
    {
        OutJson.Reset();
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().GetManagedScriptProfilerTrace(
        WorldHostId,
        InstanceId,
        OutJson,
        OutError);
}

bool UUnifiedScriptWorldSubsystem::DestroyManagedScript(
    uint64 InstanceId,
    FString& OutError)
{
    if (!IsManagedWorldReady())
    {
        OutError = TEXT("The UnifiedScript managed World is not ready.");
        return false;
    }
    return FUnifiedScriptCoreClrHost::Get().DestroyManagedScript(
        WorldHostId,
        InstanceId,
        OutError);
}

int32 UUnifiedScriptWorldSubsystem::GetManagedScriptCount() const
{
    return IsManagedWorldReady()
        ? FUnifiedScriptCoreClrHost::Get().GetManagedScriptCount(WorldHostId)
        : 0;
}

bool UUnifiedScriptWorldSubsystem::GetManagedInvocationMetrics(
    uint64& OutCallFrameInvocationCount,
    uint64& OutLegacyInvocationCount) const
{
    OutCallFrameInvocationCount = 0;
    OutLegacyInvocationCount = 0;
    return IsManagedWorldReady() &&
        FUnifiedScriptCoreClrHost::Get().GetManagedInvocationMetrics(
            WorldHostId,
            OutCallFrameInvocationCount,
            OutLegacyInvocationCount);
}

int32 UUnifiedScriptWorldSubsystem::NumRegisteredObjects() const
{
    return IsNativeRuntimeReady() ? ObjectRegistry->Num() : 0;
}

int32 UUnifiedScriptWorldSubsystem::GetEventSubscriptionCount() const
{
    return NativeContext != nullptr ? NativeContext->GetEventSubscriptionCount() : 0;
}

uint64 UUnifiedScriptWorldSubsystem::GetFastInvocationCount() const
{
    return InvocationBridge != nullptr
        ? InvocationBridge->GetFastInvocationCount()
        : 0;
}

int32 UUnifiedScriptWorldSubsystem::GetPendingAsyncRequestCount() const
{
    return AsyncService != nullptr ? AsyncService->NumPending() : 0;
}

int32 UUnifiedScriptWorldSubsystem::GetQueuedAsyncCompletionCount() const
{
    return AsyncService != nullptr ? AsyncService->NumCompleted() : 0;
}

int32 UUnifiedScriptWorldSubsystem::GetOwnedAsyncResourceCount() const
{
    return AsyncService != nullptr ? AsyncService->NumOwnedResources() : 0;
}

FUnifiedScriptNativeBridgeContext*
UUnifiedScriptWorldSubsystem::GetNativeInvocationContext() const
{
    return IsNativeRuntimeReady() ? NativeContext : nullptr;
}

const FUnifiedScriptNativeTransportV2*
UUnifiedScriptWorldSubsystem::GetNativeTransport() const
{
    return UEUnifiedScript_GetNativeTransportV2();
}

void UUnifiedScriptWorldSubsystem::ShutdownNativeRuntime()
{
    if (HostState == EUnifiedScriptWorldHostState::Stopped)
    {
        return;
    }

    const bool bManagedWorldWasReady = IsManagedWorldReady();
    const uint64 ShuttingDownWorldHostId = WorldHostId;
    HostState = EUnifiedScriptWorldHostState::ShuttingDown;
    FUnifiedScriptRuntimeBreadcrumbs::RecordLifecycle(
        TEXT("WorldShutdown.Begin"),
        ShuttingDownWorldHostId);
    if (NativeContext != nullptr)
    {
        NativeContext->SuspendWorkerDispatch();
    }
    FUnifiedScriptRuntimeBreadcrumbs::RecordLifecycle(
        TEXT("WorldShutdown.WorkerDispatchSuspended"),
        ShuttingDownWorldHostId);
    if (bManagedWorldWasReady)
    {
        FString Error;
        if (FUnifiedScriptCoreClrHost::Get().DestroyManagedWorld(WorldHostId, Error))
        {
            FUnifiedScriptRuntimeBreadcrumbs::RecordLifecycle(
                TEXT("WorldShutdown.ManagedWorldDestroyed"),
                ShuttingDownWorldHostId);
        }
        else
        {
            FUnifiedScriptRuntimeBreadcrumbs::RecordFailure(
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
        FUnifiedScriptRuntimeBreadcrumbs::RecordLifecycle(
            TEXT("WorldShutdown.ManagedWorldSkipped"),
            ShuttingDownWorldHostId);
    }
    if (NativeContext != nullptr)
    {
        NativeContext->Detach();
    }
    FUnifiedScriptRuntimeBreadcrumbs::RecordLifecycle(
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
    FUnifiedScriptRuntimeBreadcrumbs::RecordLifecycle(
        TEXT("WorldShutdown.ServicesStopped"),
        ShuttingDownWorldHostId);

    if (ObjectRegistry != nullptr)
    {
        FUnifiedScriptObjectHandleResult Result;
        ObjectRegistry->InvalidateAll(Result);
        ensureMsgf(
            Result.IsSuccess(),
            TEXT("UnifiedScript failed to invalidate world handles: %s"),
            *Result.Message);
    }
    FUnifiedScriptRuntimeBreadcrumbs::RecordLifecycle(
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
    HostState = EUnifiedScriptWorldHostState::Stopped;
    FUnifiedScriptRuntimeBreadcrumbs::RecordLifecycle(
        TEXT("WorldShutdown.Complete"),
        ShuttingDownWorldHostId);
}

#include "UnifiedScriptWorldSubsystem.h"

#include "Engine/World.h"
#include "Subsystems/SubsystemCollection.h"
#include "UnifiedScriptCoreClrHost.h"

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
    check(NativeContext == nullptr);

    UWorld* World = GetWorld();
    check(World != nullptr);

    ObjectRegistry = MakeUnique<FUnifiedScriptObjectRegistry>(World);
    InvocationBridge = MakeUnique<FUnifiedScriptInvocationBridge>(*ObjectRegistry);
    NativeContext = new FUnifiedScriptNativeBridgeContext(*InvocationBridge);
    WorldHostId = UE::UnifiedScript::WorldHost::Private::AllocateWorldHostId();
    HostState = EUnifiedScriptWorldHostState::NativeReady;
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
        }
        else
        {
            UE_LOG(LogTemp, Error, TEXT("%s"), *Error);
        }
    }
}

void UUnifiedScriptWorldSubsystem::Deinitialize()
{
    check(IsInGameThread());
    ShutdownNativeRuntime();
    Super::Deinitialize();
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

uint64 UUnifiedScriptWorldSubsystem::GetFastInvocationCount() const
{
    return InvocationBridge != nullptr
        ? InvocationBridge->GetFastInvocationCount()
        : 0;
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
    HostState = EUnifiedScriptWorldHostState::ShuttingDown;
    if (bManagedWorldWasReady)
    {
        FString Error;
        ensureMsgf(
            FUnifiedScriptCoreClrHost::Get().DestroyManagedWorld(WorldHostId, Error),
            TEXT("%s"),
            *Error);
    }
    if (NativeContext != nullptr)
    {
        NativeContext->Detach();
    }

    if (ObjectRegistry != nullptr)
    {
        FUnifiedScriptObjectHandleResult Result;
        ObjectRegistry->InvalidateAll(Result);
        ensureMsgf(
            Result.IsSuccess(),
            TEXT("UnifiedScript failed to invalidate world handles: %s"),
            *Result.Message);
    }

    InvocationBridge.Reset();
    ObjectRegistry.Reset();
    if (NativeContext != nullptr)
    {
        NativeContext->Release();
        NativeContext = nullptr;
    }
    WorldHostId = 0;
    HostState = EUnifiedScriptWorldHostState::Stopped;
}

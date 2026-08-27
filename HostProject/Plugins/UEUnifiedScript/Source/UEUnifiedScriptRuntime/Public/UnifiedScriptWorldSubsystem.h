#pragma once

#include "CoreMinimal.h"
#include "Subsystems/WorldSubsystem.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptObjectRegistry.h"
#include "UnifiedScriptWorldSubsystem.generated.h"

enum class EUnifiedScriptWorldHostState : uint8
{
    Stopped = 0,
    NativeReady = 1,
    ManagedReady = 2,
    ShuttingDown = 3,
};

/**
 * One native UnifiedScript runtime boundary per supported UWorld.
 *
 * The subsystem exclusively owns the weak-object registry and invocation
 * bridge. Managed transports retain the opaque context, while Deinitialize
 * detaches it before invalidating handles and destroying native state.
 */
UCLASS()
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptWorldSubsystem final
    : public UWorldSubsystem
{
    GENERATED_BODY()

public:
    virtual void Initialize(FSubsystemCollectionBase& Collection) override;
    virtual void Deinitialize() override;

    /** Publishes snapshots/events and opens the managed command/trace phase. */
    void BeginManagedFrame();

    /** Closes the managed phase and executes committed commands on the Game Thread. */
    void EndManagedFrame();

    EUnifiedScriptGameplayDataPhaseV4 GetGameplayDataPhase() const;
    uint64 GetGameplayFrameNumber() const;

    EUnifiedScriptWorldHostState GetHostState() const;
    bool IsNativeRuntimeReady() const;
    bool IsCoreClrRuntimeReady() const;
    bool IsManagedWorldReady() const;
    uint64 GetWorldHostId() const;

    FUnifiedScriptObjectHandle RegisterObject(
        UObject* Object,
        FUnifiedScriptObjectHandleResult& OutResult);

    /** Keeps a synchronously loaded asset alive for the lifetime of this World host. */
    void RetainLoadedResource(UObject* Resource);

    bool ReleaseObject(
        FUnifiedScriptObjectHandle Handle,
        FUnifiedScriptObjectHandleResult& OutResult);

    bool CreateManagedScript(
        uint64 InstanceId,
        FUnifiedScriptObjectHandle Host,
        FUnifiedScriptObjectHandle Target,
        const FString& AssemblyPath,
        const FString& TypeName,
        FString& OutError);

    bool TickManagedScript(
        uint64 InstanceId,
        float DeltaSeconds,
        FString& OutError);
    bool ConfigureManagedScriptDebug(
        uint64 InstanceId,
        const FString& ConfigurationJson,
        FString& OutError);
    bool BeginManagedScriptDebugTick(
        uint64 InstanceId,
        float DeltaSeconds,
        FString& OutError);
    bool GetManagedScriptDebugState(
        uint64 InstanceId,
        FString& OutJson,
        FString& OutError) const;
    bool ControlManagedScriptDebug(
        uint64 InstanceId,
        int32 Control,
        const FString& PauseId,
        FString& OutError);
    bool DisableManagedScriptDebug(uint64 InstanceId, FString& OutError);
    bool IsManagedScriptDebugActive(
        uint64 InstanceId,
        bool& bOutActive,
        FString& OutError) const;
    void PumpManagedBridgeCalls();

    bool DispatchManagedEvent(
        uint64 InstanceId,
        int32 EventKind,
        FUnifiedScriptObjectHandle Source,
        FUnifiedScriptObjectHandle Argument,
        FString& OutError);

    bool ReloadManagedScript(
        uint64 InstanceId,
        const FString& AssemblyPath,
        const FString& TypeName,
        int32& OutGeneration,
        FString& OutError);

    bool GetManagedScriptReloadState(
        uint64 InstanceId,
        FString& OutJson,
        FString& OutError) const;
    bool SetManagedScriptProfilerEnabled(
        uint64 InstanceId,
        bool bEnabled,
        FString& OutError);
    bool GetManagedScriptProfilerTrace(
        uint64 InstanceId,
        FString& OutJson,
        FString& OutError) const;

    bool DestroyManagedScript(uint64 InstanceId, FString& OutError);

    int32 GetManagedScriptCount() const;

    bool GetManagedInvocationMetrics(
        uint64& OutCallFrameInvocationCount,
        uint64& OutLegacyInvocationCount) const;

    int32 NumRegisteredObjects() const;

    int32 GetEventSubscriptionCount() const;

    uint64 GetFastInvocationCount() const;

    int32 GetPendingAsyncRequestCount() const;

    int32 GetQueuedAsyncCompletionCount() const;

    int32 GetOwnedAsyncResourceCount() const;

    /** Borrowed pointer. A consumer must retain it through the transport table. */
    FUnifiedScriptNativeBridgeContext* GetNativeInvocationContext() const;

    const FUnifiedScriptNativeTransportV2* GetNativeTransport() const;

private:
    void HandleWorldPreActorTick(UWorld* World, ELevelTick TickType, float DeltaSeconds);
    void HandleWorldPostActorTick(UWorld* World, ELevelTick TickType, float DeltaSeconds);
    void ShutdownNativeRuntime();

    EUnifiedScriptWorldHostState HostState = EUnifiedScriptWorldHostState::Stopped;
    TUniquePtr<FUnifiedScriptObjectRegistry> ObjectRegistry;
    TUniquePtr<FUnifiedScriptInvocationBridge> InvocationBridge;
    TUniquePtr<FUnifiedScriptSharedDataPlane> SharedDataPlane;
    TUniquePtr<FUnifiedScriptGameplayDataPlane> GameplayDataPlane;
    TUniquePtr<FUnifiedScriptAsyncService> AsyncService;
    FUnifiedScriptNativeBridgeContext* NativeContext = nullptr;
    uint64 WorldHostId = 0;
    FDelegateHandle PreActorTickHandle;
    FDelegateHandle PostActorTickHandle;

    UPROPERTY(Transient)
    TArray<TObjectPtr<UObject>> LoadedResources;
};

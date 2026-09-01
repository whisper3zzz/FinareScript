#pragma once

#include "CoreMinimal.h"
#include "Subsystems/WorldSubsystem.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptManagedFunctionAbi.h"
#include "UnifiedScriptObjectRegistry.h"
#include "UnifiedScriptDataPlaneExtension.h"
#include "UObject/ObjectKey.h"

#include <type_traits>

#include "UnifiedScriptWorldSubsystem.generated.h"

enum class EUnifiedScriptWorldHostState : uint8
{
    Stopped = 0,
    NativeReady = 1,
    ManagedReady = 2,
    ShuttingDown = 3,
};

/** Runtime lane that owns one gameplay lifecycle route in a World. */
UENUM(BlueprintType)
enum class EUnifiedScriptRuntimeOwner : uint8
{
    Puerts = 0,
    UnifiedScript = 1,
};

struct FUnifiedScriptGameplayLifecycleClaimKey
{
    FObjectKey HostObject;
    FName LifecycleKey;

    bool operator==(const FUnifiedScriptGameplayLifecycleClaimKey& Other) const
    {
        return HostObject == Other.HostObject && LifecycleKey == Other.LifecycleKey;
    }

    friend uint32 GetTypeHash(const FUnifiedScriptGameplayLifecycleClaimKey& Value)
    {
        return HashCombine(GetTypeHash(Value.HostObject), GetTypeHash(Value.LifecycleKey));
    }
};

struct FUnifiedScriptGameplayLifecycleClaim
{
    TWeakObjectPtr<UObject> HostObject;
    TWeakObjectPtr<UObject> Claimant;
    FObjectKey ClaimantKey;
    FName LifecycleKey;
    EUnifiedScriptRuntimeOwner RuntimeOwner = EUnifiedScriptRuntimeOwner::Puerts;
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

    bool PublishGeneratedSnapshotRows(
        uint64 SchemaId,
        uint32 EntrySize,
        const void* Entries,
        uint32 EntryCount);

    bool QueueGeneratedEventRows(
        uint64 SchemaId,
        uint32 EntrySize,
        const void* Entries,
        uint32 EntryCount);

    bool RegisterGeneratedDataPlaneExtension(
        const FUnifiedScriptDataPlaneExtensionDescriptor& Extension,
        FString& OutError);

    bool UnregisterGeneratedDataPlaneExtension(
        uint64 ExtensionKey,
        uint64 SchemaFingerprint);

    bool PublishGeneratedSnapshotRows(
        const FUnifiedScriptDataPlaneExtensionDescriptor& Extension,
        const FUnifiedScriptDataPlaneExtensionChannelDescriptor& Channel,
        uint32 EntrySize,
        const void* Entries,
        uint32 EntryCount);

    bool QueueGeneratedEventRows(
        const FUnifiedScriptDataPlaneExtensionDescriptor& Extension,
        const FUnifiedScriptDataPlaneExtensionChannelDescriptor& Channel,
        uint32 EntrySize,
        const void* Entries,
        uint32 EntryCount);

    template <typename T>
    bool PublishGeneratedSnapshotRows(uint64 SchemaId, TConstArrayView<T> Entries)
    {
        static_assert(std::is_trivially_copyable_v<T>);
        static_assert(std::is_standard_layout_v<T>);
        return PublishGeneratedSnapshotRows(
            SchemaId,
            sizeof(T),
            Entries.GetData(),
            static_cast<uint32>(Entries.Num()));
    }

    template <typename T>
    bool QueueGeneratedEventRows(uint64 SchemaId, TConstArrayView<T> Entries)
    {
        static_assert(std::is_trivially_copyable_v<T>);
        static_assert(std::is_standard_layout_v<T>);
        return QueueGeneratedEventRows(
            SchemaId,
            sizeof(T),
            Entries.GetData(),
            static_cast<uint32>(Entries.Num()));
    }

    template <typename T>
    bool PublishGeneratedSnapshotRows(
        const FUnifiedScriptDataPlaneExtensionDescriptor& Extension,
        const FUnifiedScriptDataPlaneExtensionChannelDescriptor& Channel,
        TConstArrayView<T> Entries)
    {
        static_assert(std::is_trivially_copyable_v<T>);
        static_assert(std::is_standard_layout_v<T>);
        return PublishGeneratedSnapshotRows(
            Extension,
            Channel,
            sizeof(T),
            Entries.GetData(),
            static_cast<uint32>(Entries.Num()));
    }

    template <typename T>
    bool QueueGeneratedEventRows(
        const FUnifiedScriptDataPlaneExtensionDescriptor& Extension,
        const FUnifiedScriptDataPlaneExtensionChannelDescriptor& Channel,
        TConstArrayView<T> Entries)
    {
        static_assert(std::is_trivially_copyable_v<T>);
        static_assert(std::is_standard_layout_v<T>);
        return QueueGeneratedEventRows(
            Extension,
            Channel,
            sizeof(T),
            Entries.GetData(),
            static_cast<uint32>(Entries.Num()));
    }

    EUnifiedScriptWorldHostState GetHostState() const;
    bool IsNativeRuntimeReady() const;
    bool IsCoreClrRuntimeReady() const;
    bool IsManagedWorldReady() const;
    uint64 GetWorldHostId() const;

    /** Atomically claims one host/lifecycle route for exactly one runtime claimant. */
    bool ClaimGameplayLifecycle(
        UObject* Claimant,
        UObject* HostObject,
        FName LifecycleKey,
        EUnifiedScriptRuntimeOwner RuntimeOwner,
        FString& OutError);

    /** Releases only a claim owned by the same claimant and runtime lane. */
    bool ReleaseGameplayLifecycle(
        UObject* Claimant,
        UObject* HostObject,
        FName LifecycleKey,
        EUnifiedScriptRuntimeOwner RuntimeOwner,
        FString& OutError);

    int32 GetGameplayLifecycleClaimCount() const;
    FString BuildGameplayLifecycleOwnershipReport() const;

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

    bool InvokeManagedScriptFunction(
        uint64 InstanceId,
        const FString& FunctionName,
        TConstArrayView<FUnifiedScriptManagedFunctionValueV1> Arguments,
        FUnifiedScriptManagedFunctionValueV1& OutResult,
        FString& OutError);

    bool ReloadManagedScript(
        uint64 InstanceId,
        const FString& AssemblyPath,
        const FString& TypeName,
        int32& OutGeneration,
        FString& OutError);
    bool PrepareManagedScriptReload(
        uint64 InstanceId,
        const FString& AssemblyPath,
        const FString& TypeName,
        int32& OutCandidateGeneration,
        FString& OutError);
    bool CommitManagedScriptReload(
        uint64 InstanceId,
        int32 CandidateGeneration,
        int32& OutActiveGeneration,
        FString& OutError);
    bool AbortManagedScriptReload(
        uint64 InstanceId,
        int32 CandidateGeneration,
        FString& OutError);
    bool FinalizeManagedScriptReload(
        uint64 InstanceId,
        int32 CandidateGeneration,
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
    void PruneGameplayLifecycleClaims();
    void ShutdownNativeRuntime();

    EUnifiedScriptWorldHostState HostState = EUnifiedScriptWorldHostState::Stopped;
    TUniquePtr<FUnifiedScriptObjectRegistry> ObjectRegistry;
    TUniquePtr<FUnifiedScriptInvocationBridge> InvocationBridge;
    TUniquePtr<FUnifiedScriptSharedDataPlane> SharedDataPlane;
    TUniquePtr<FUnifiedScriptGameplayDataPlane> GameplayDataPlane;
    FUnifiedScriptDataPlaneExtensionRegistry DataPlaneExtensionRegistry;
    TUniquePtr<FUnifiedScriptAsyncService> AsyncService;
    FUnifiedScriptNativeBridgeContext* NativeContext = nullptr;
    uint64 WorldHostId = 0;
    FDelegateHandle PreActorTickHandle;
    FDelegateHandle PostActorTickHandle;
    TMap<
        FUnifiedScriptGameplayLifecycleClaimKey,
        FUnifiedScriptGameplayLifecycleClaim> GameplayLifecycleClaims;

    UPROPERTY(Transient)
    TArray<TObjectPtr<UObject>> LoadedResources;
};

#pragma once

#include "CoreMinimal.h"
#include "Subsystems/WorldSubsystem.h"
#include "FinareNativeTransport.h"
#include "FinareManagedFunctionAbi.h"
#include "FinareObjectRegistry.h"
#include "FinareDataPlaneExtension.h"
#include "UObject/ObjectKey.h"

#include <type_traits>

#include "FinareWorldSubsystem.generated.h"

enum class EFinareWorldHostState : uint8
{
    Stopped = 0,
    NativeReady = 1,
    ManagedReady = 2,
    ShuttingDown = 3,
};

/** Runtime lane that owns one gameplay lifecycle route in a World. */
UENUM(BlueprintType)
enum class EFinareRuntimeOwner : uint8
{
    Puerts = 0,
    Finare = 1,
};

struct FFinareGameplayLifecycleClaimKey
{
    FObjectKey HostObject;
    FName LifecycleKey;

    bool operator==(const FFinareGameplayLifecycleClaimKey& Other) const
    {
        return HostObject == Other.HostObject && LifecycleKey == Other.LifecycleKey;
    }

    friend uint32 GetTypeHash(const FFinareGameplayLifecycleClaimKey& Value)
    {
        return HashCombine(GetTypeHash(Value.HostObject), GetTypeHash(Value.LifecycleKey));
    }
};

struct FFinareGameplayLifecycleClaim
{
    TWeakObjectPtr<UObject> HostObject;
    TWeakObjectPtr<UObject> Claimant;
    FObjectKey ClaimantKey;
    FName LifecycleKey;
    EFinareRuntimeOwner RuntimeOwner = EFinareRuntimeOwner::Puerts;
};

/**
 * One native Finare runtime boundary per supported UWorld.
 *
 * The subsystem exclusively owns the weak-object registry and invocation
 * bridge. Managed transports retain the opaque context, while Deinitialize
 * detaches it before invalidating handles and destroying native state.
 */
UCLASS()
class FINARERUNTIME_API UFinareWorldSubsystem final
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

    EFinareGameplayDataPhaseV4 GetGameplayDataPhase() const;
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
        const FFinareDataPlaneExtensionDescriptor& Extension,
        FString& OutError);

    bool UnregisterGeneratedDataPlaneExtension(
        uint64 ExtensionKey,
        uint64 SchemaFingerprint);

    bool PublishGeneratedSnapshotRows(
        const FFinareDataPlaneExtensionDescriptor& Extension,
        const FFinareDataPlaneExtensionChannelDescriptor& Channel,
        uint32 EntrySize,
        const void* Entries,
        uint32 EntryCount);

    bool QueueGeneratedEventRows(
        const FFinareDataPlaneExtensionDescriptor& Extension,
        const FFinareDataPlaneExtensionChannelDescriptor& Channel,
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
        const FFinareDataPlaneExtensionDescriptor& Extension,
        const FFinareDataPlaneExtensionChannelDescriptor& Channel,
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
        const FFinareDataPlaneExtensionDescriptor& Extension,
        const FFinareDataPlaneExtensionChannelDescriptor& Channel,
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

    EFinareWorldHostState GetHostState() const;
    bool IsNativeRuntimeReady() const;
    bool IsCoreClrRuntimeReady() const;
    bool IsManagedWorldReady() const;
    uint64 GetWorldHostId() const;

    /** Atomically claims one host/lifecycle route for exactly one runtime claimant. */
    bool ClaimGameplayLifecycle(
        UObject* Claimant,
        UObject* HostObject,
        FName LifecycleKey,
        EFinareRuntimeOwner RuntimeOwner,
        FString& OutError);

    /** Releases only a claim owned by the same claimant and runtime lane. */
    bool ReleaseGameplayLifecycle(
        UObject* Claimant,
        UObject* HostObject,
        FName LifecycleKey,
        EFinareRuntimeOwner RuntimeOwner,
        FString& OutError);

    int32 GetGameplayLifecycleClaimCount() const;
    FString BuildGameplayLifecycleOwnershipReport() const;

    FFinareObjectHandle RegisterObject(
        UObject* Object,
        FFinareObjectHandleResult& OutResult);

    UObject* ResolveObject(
        FFinareObjectHandle Handle,
        FFinareObjectHandleResult& OutResult);

    /** Keeps a synchronously loaded asset alive for the lifetime of this World host. */
    void RetainLoadedResource(UObject* Resource);

    bool ReleaseObject(
        FFinareObjectHandle Handle,
        FFinareObjectHandleResult& OutResult);

    bool CreateManagedScript(
        uint64 InstanceId,
        FFinareObjectHandle Host,
        FFinareObjectHandle Target,
        const FString& AssemblyPath,
        const FString& TypeName,
        FString& OutError);
    bool CreateManagedScriptWithBlueprintContract(
        uint64 InstanceId,
        FFinareObjectHandle Host,
        FFinareObjectHandle Target,
        const FString& AssemblyPath,
        const FString& TypeName,
        const FString& BlueprintOverrideContractJson,
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
        FFinareObjectHandle Source,
        FFinareObjectHandle Argument,
        FString& OutError);

    bool InvokeManagedScriptFunction(
        uint64 InstanceId,
        const FString& FunctionName,
        TConstArrayView<FFinareManagedFunctionValueV1> Arguments,
        FFinareManagedFunctionValueV1& OutResult,
        FString& OutError);

    bool InvokeManagedScriptFunctionV2(
        uint64 InstanceId,
        const FString& AssemblyPath,
        const FString& TypeName,
        const FString& FunctionName,
        FFinareNativeCallFrameV3& Frame,
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

    /** A zero candidate means this World has a cold static-library cache and is not a target. */
    bool PrepareManagedStaticLibraryReload(
        const FString& AssemblyPath,
        const FString& TypeName,
        int32& OutCandidateGeneration,
        FString& OutError);
    bool CommitManagedStaticLibraryReload(
        const FString& AssemblyPath,
        const FString& TypeName,
        int32 CandidateGeneration,
        int32& OutActiveGeneration,
        FString& OutError);
    bool AbortManagedStaticLibraryReload(
        const FString& AssemblyPath,
        const FString& TypeName,
        int32 CandidateGeneration,
        FString& OutError);
    bool FinalizeManagedStaticLibraryReload(
        const FString& AssemblyPath,
        const FString& TypeName,
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
    FFinareNativeBridgeContext* GetNativeInvocationContext() const;

    const FFinareNativeTransportV2* GetNativeTransport() const;

private:
    void HandleWorldPreActorTick(UWorld* World, ELevelTick TickType, float DeltaSeconds);
    void HandleWorldPostActorTick(UWorld* World, ELevelTick TickType, float DeltaSeconds);
    void PruneGameplayLifecycleClaims();
    void ShutdownNativeRuntime();

    EFinareWorldHostState HostState = EFinareWorldHostState::Stopped;
    TUniquePtr<FFinareObjectRegistry> ObjectRegistry;
    TUniquePtr<FFinareInvocationBridge> InvocationBridge;
    TUniquePtr<FFinareSharedDataPlane> SharedDataPlane;
    TUniquePtr<FFinareGameplayDataPlane> GameplayDataPlane;
    FFinareDataPlaneExtensionRegistry DataPlaneExtensionRegistry;
    TUniquePtr<FFinareAsyncService> AsyncService;
    FFinareNativeBridgeContext* NativeContext = nullptr;
    uint64 WorldHostId = 0;
    FDelegateHandle PreActorTickHandle;
    FDelegateHandle PostActorTickHandle;
    TMap<
        FFinareGameplayLifecycleClaimKey,
        FFinareGameplayLifecycleClaim> GameplayLifecycleClaims;

    UPROPERTY(Transient)
    TArray<TObjectPtr<UObject>> LoadedResources;
};

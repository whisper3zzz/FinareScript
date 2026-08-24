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

    EUnifiedScriptWorldHostState GetHostState() const;
    bool IsNativeRuntimeReady() const;
    bool IsCoreClrRuntimeReady() const;
    bool IsManagedWorldReady() const;
    uint64 GetWorldHostId() const;

    FUnifiedScriptObjectHandle RegisterObject(
        UObject* Object,
        FUnifiedScriptObjectHandleResult& OutResult);

    bool ReleaseObject(
        FUnifiedScriptObjectHandle Handle,
        FUnifiedScriptObjectHandleResult& OutResult);

    bool CreateManagedScript(
        uint64 InstanceId,
        FUnifiedScriptObjectHandle Target,
        const FString& AssemblyPath,
        const FString& TypeName,
        FString& OutError);

    bool TickManagedScript(
        uint64 InstanceId,
        float DeltaSeconds,
        FString& OutError);

    bool ReloadManagedScript(
        uint64 InstanceId,
        const FString& AssemblyPath,
        const FString& TypeName,
        int32& OutGeneration,
        FString& OutError);

    bool DestroyManagedScript(uint64 InstanceId, FString& OutError);

    int32 GetManagedScriptCount() const;

    int32 NumRegisteredObjects() const;

    uint64 GetFastInvocationCount() const;

    /** Borrowed pointer. A consumer must retain it through the transport table. */
    FUnifiedScriptNativeBridgeContext* GetNativeInvocationContext() const;

    const FUnifiedScriptNativeTransportV2* GetNativeTransport() const;

private:
    void ShutdownNativeRuntime();

    EUnifiedScriptWorldHostState HostState = EUnifiedScriptWorldHostState::Stopped;
    TUniquePtr<FUnifiedScriptObjectRegistry> ObjectRegistry;
    TUniquePtr<FUnifiedScriptInvocationBridge> InvocationBridge;
    FUnifiedScriptNativeBridgeContext* NativeContext = nullptr;
    uint64 WorldHostId = 0;
};

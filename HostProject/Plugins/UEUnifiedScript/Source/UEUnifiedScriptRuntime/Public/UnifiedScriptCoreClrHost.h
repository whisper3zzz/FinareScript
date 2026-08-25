#pragma once

#include "CoreMinimal.h"

class FUnifiedScriptNativeBridgeContext;
struct FUnifiedScriptNativeTransportV2;

enum class EUnifiedScriptCoreClrHostState : uint8
{
    Stopped = 0,
    Ready = 1,
    Failed = 2,
};

/** Process-wide hostfxr bootstrap. Per-World isolation remains managed ALC state. */
class UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptCoreClrHost final
{
public:
    static FUnifiedScriptCoreClrHost& Get();

    bool Initialize();
    void Shutdown();

    EUnifiedScriptCoreClrHostState GetState() const;
    bool IsReady() const;
    void* GetLoadAssemblyAndGetFunctionPointer() const;
    bool CreateManagedWorld(
        uint64 WorldHostId,
        FUnifiedScriptNativeBridgeContext* Context,
        const FUnifiedScriptNativeTransportV2* Transport,
        FString& OutError);
    bool DestroyManagedWorld(uint64 WorldHostId, FString& OutError);
    bool CreateManagedScript(
        uint64 WorldHostId,
        uint64 InstanceId,
        uint64 Host,
        uint64 Target,
        const FString& AssemblyPath,
        const FString& TypeName,
        FString& OutError);
    bool TickManagedScript(
        uint64 WorldHostId,
        uint64 InstanceId,
        float DeltaSeconds,
        FString& OutError);
    bool DispatchManagedEvent(
        uint64 WorldHostId,
        uint64 InstanceId,
        int32 EventKind,
        uint64 Source,
        uint64 Argument,
        FString& OutError);
    bool ReloadManagedScript(
        uint64 WorldHostId,
        uint64 InstanceId,
        const FString& AssemblyPath,
        const FString& TypeName,
        int32& OutGeneration,
        FString& OutError);
    bool DestroyManagedScript(
        uint64 WorldHostId,
        uint64 InstanceId,
        FString& OutError);
    int32 GetManagedScriptCount(uint64 WorldHostId) const;
    bool GetManagedInvocationMetrics(
        uint64 WorldHostId,
        uint64& OutCallFrameInvocationCount,
        uint64& OutLegacyInvocationCount) const;
    int32 GetManagedWorldCount() const;
    FString GetHostFxrPath() const;
    FString GetLastError() const;

private:
    FUnifiedScriptCoreClrHost() = default;
    ~FUnifiedScriptCoreClrHost() = default;

    bool InitializeLocked();
    bool FailLocked(FString Message);

    mutable FCriticalSection StateLock;
    EUnifiedScriptCoreClrHostState State = EUnifiedScriptCoreClrHostState::Stopped;
    void* HostFxrLibrary = nullptr;
    void* LoadAssemblyAndGetFunctionPointer = nullptr;
    void* CreateWorldEntryPoint = nullptr;
    void* DestroyWorldEntryPoint = nullptr;
    void* GetWorldCountEntryPoint = nullptr;
    void* CreateScriptEntryPoint = nullptr;
    void* TickScriptEntryPoint = nullptr;
    void* DispatchEventEntryPoint = nullptr;
    void* ReloadScriptEntryPoint = nullptr;
    void* DestroyScriptEntryPoint = nullptr;
    void* GetScriptCountEntryPoint = nullptr;
    void* GetInvocationMetricsEntryPoint = nullptr;
    void* GetLastManagedErrorEntryPoint = nullptr;
    FString LoadedHostFxrPath;
    FString LastError;
};

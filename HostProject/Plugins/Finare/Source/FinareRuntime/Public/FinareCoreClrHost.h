#pragma once

#include "CoreMinimal.h"
#include "FinareManagedFunctionAbi.h"

class FFinareNativeBridgeContext;
struct FFinareNativeTransportV2;
struct FFinareNativeCallFrameV3;

enum class EFinareCoreClrHostState : uint8
{
	Stopped = 0,
	Ready = 1,
	Failed = 2,
};

/** Process-wide hostfxr bootstrap. Per-World isolation remains managed ALC state. */
class FINARERUNTIME_API FFinareCoreClrHost final
{
public:
	static FFinareCoreClrHost& Get();

	bool Initialize();
	bool HasManagedConstruction(const FString& Program, const FString& Type, bool& OutHasHook, FString& OutError);
	bool ConstructManagedObject(void* Context, const FFinareNativeTransportV2* Transport,
		const void* Operations, uint64 Host, bool bIsCDO, const FString& Program,
		const FString& Type, FString& OutError);
	void Shutdown();

	EFinareCoreClrHostState GetState() const;
	bool IsReady() const;
	void* GetLoadAssemblyAndGetFunctionPointer() const;
	bool CreateManagedWorld(
		uint64 WorldHostId,
		FFinareNativeBridgeContext* Context,
		const FFinareNativeTransportV2* Transport,
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
	/** Creates a script through the validated activation-contract export. */
	bool CreateManagedScriptWithBlueprintContract(
		uint64 WorldHostId,
		uint64 InstanceId,
		uint64 Host,
		uint64 Target,
		const FString& AssemblyPath,
		const FString& TypeName,
		const FString& BlueprintOverrideContractJson,
		FString& OutError);
	bool TickManagedScript(
		uint64 WorldHostId,
		uint64 InstanceId,
		float DeltaSeconds,
		FString& OutError);
	bool ConfigureManagedScriptDebug(
		uint64 WorldHostId,
		uint64 InstanceId,
		const FString& ConfigurationJson,
		FString& OutError);
	bool BeginManagedScriptDebugTick(
		uint64 WorldHostId,
		uint64 InstanceId,
		float DeltaSeconds,
		FString& OutError);
	bool GetManagedScriptDebugState(
		uint64 WorldHostId,
		uint64 InstanceId,
		FString& OutJson,
		FString& OutError) const;
	bool GetManagedScriptReloadState(
		uint64 WorldHostId,
		uint64 InstanceId,
		FString& OutJson,
		FString& OutError) const;
	bool SetManagedScriptProfilerEnabled(
		uint64 WorldHostId,
		uint64 InstanceId,
		bool bEnabled,
		FString& OutError);
	bool GetManagedScriptProfilerTrace(
		uint64 WorldHostId,
		uint64 InstanceId,
		FString& OutJson,
		FString& OutError) const;
	bool ControlManagedScriptDebug(
		uint64 WorldHostId,
		uint64 InstanceId,
		int32 Control,
		const FString& PauseId,
		FString& OutError);
	bool DisableManagedScriptDebug(
		uint64 WorldHostId,
		uint64 InstanceId,
		FString& OutError);
	bool IsManagedScriptDebugActive(
		uint64 WorldHostId,
		uint64 InstanceId,
		bool& bOutActive,
		FString& OutError) const;
	bool DispatchManagedEvent(
		uint64 WorldHostId,
		uint64 InstanceId,
		int32 EventKind,
		uint64 Source,
		uint64 Argument,
		FString& OutError);
	bool InvokeManagedScriptFunction(
		uint64 WorldHostId,
		uint64 InstanceId,
		const FString& FunctionName,
		TConstArrayView<FFinareManagedFunctionValueV1> Arguments,
		FFinareManagedFunctionValueV1& OutResult,
		FString& OutError);
	bool InvokeManagedScriptFunctionV2(
		uint64 WorldHostId,
		uint64 InstanceId,
		const FString& AssemblyPath,
		const FString& TypeName,
		const FString& FunctionName,
		FFinareNativeCallFrameV3& Frame,
		FString& OutError);
	bool ReloadManagedScript(
		uint64 WorldHostId,
		uint64 InstanceId,
		const FString& AssemblyPath,
		const FString& TypeName,
		int32& OutGeneration,
		FString& OutError);
	bool PrepareManagedScriptReload(
		uint64 WorldHostId,
		uint64 InstanceId,
		const FString& AssemblyPath,
		const FString& TypeName,
		int32& OutCandidateGeneration,
		FString& OutError);
	bool CommitManagedScriptReload(
		uint64 WorldHostId,
		uint64 InstanceId,
		int32 CandidateGeneration,
		int32& OutActiveGeneration,
		FString& OutError);
	bool AbortManagedScriptReload(
		uint64 WorldHostId,
		uint64 InstanceId,
		int32 CandidateGeneration,
		FString& OutError);
	bool FinalizeManagedScriptReload(
		uint64 WorldHostId,
		uint64 InstanceId,
		int32 CandidateGeneration,
		FString& OutError);

	/** A zero candidate means this World has a cold static-library cache and is not a target. */
	bool PrepareManagedStaticLibraryReload(
		uint64 WorldHostId,
		const FString& AssemblyPath,
		const FString& TypeName,
		int32& OutCandidateGeneration,
		FString& OutError);
	bool CommitManagedStaticLibraryReload(
		uint64 WorldHostId,
		const FString& AssemblyPath,
		const FString& TypeName,
		int32 CandidateGeneration,
		int32& OutActiveGeneration,
		FString& OutError);
	bool AbortManagedStaticLibraryReload(
		uint64 WorldHostId,
		const FString& AssemblyPath,
		const FString& TypeName,
		int32 CandidateGeneration,
		FString& OutError);
	bool FinalizeManagedStaticLibraryReload(
		uint64 WorldHostId,
		const FString& AssemblyPath,
		const FString& TypeName,
		int32 CandidateGeneration,
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
	int32 CollectRetiredManagedLoadContexts() const;
	FString GetHostFxrPath() const;
	FString GetLastError() const;

private:
	FFinareCoreClrHost() = default;
	~FFinareCoreClrHost() = default;

	bool InitializeLocked();
	bool FailLocked(FString Message);

	mutable FCriticalSection StateLock;
	EFinareCoreClrHostState State = EFinareCoreClrHostState::Stopped;
	void* HostFxrLibrary = nullptr;
	void* LoadAssemblyAndGetFunctionPointer = nullptr;
	void* CreateWorldEntryPoint = nullptr;
	void* DestroyWorldEntryPoint = nullptr;
	void* GetWorldCountEntryPoint = nullptr;
	void* CollectRetiredLoadContextsEntryPoint = nullptr;
	void* CreateScriptEntryPoint = nullptr;
	void* CreateScriptWithBlueprintContractEntryPoint = nullptr;
	void* TickScriptEntryPoint = nullptr;
	void* ConfigureScriptDebugEntryPoint = nullptr;
	void* BeginScriptDebugTickEntryPoint = nullptr;
	void* GetScriptDebugStateEntryPoint = nullptr;
	void* GetScriptReloadStateEntryPoint = nullptr;
	void* SetScriptProfilerEnabledEntryPoint = nullptr;
	void* GetScriptProfilerTraceEntryPoint = nullptr;
	void* ControlScriptDebugEntryPoint = nullptr;
	void* DisableScriptDebugEntryPoint = nullptr;
	void* IsScriptDebugActiveEntryPoint = nullptr;
	void* DispatchEventEntryPoint = nullptr;
	void* InvokeScriptFunctionEntryPoint = nullptr;
	void* InvokeScriptFunctionV2EntryPoint = nullptr;
	void* ReloadScriptEntryPoint = nullptr;
	void* PrepareScriptReloadEntryPoint = nullptr;
	void* CommitScriptReloadEntryPoint = nullptr;
	void* AbortScriptReloadEntryPoint = nullptr;
	void* FinalizeScriptReloadEntryPoint = nullptr;
	void* PrepareStaticLibraryReloadEntryPoint = nullptr;
	void* CommitStaticLibraryReloadEntryPoint = nullptr;
	void* AbortStaticLibraryReloadEntryPoint = nullptr;
	void* FinalizeStaticLibraryReloadEntryPoint = nullptr;
	void* DestroyScriptEntryPoint = nullptr;
	void* GetScriptCountEntryPoint = nullptr;
	void* GetInvocationMetricsEntryPoint = nullptr;
	void* GetLastManagedErrorEntryPoint = nullptr;
	FString LoadedHostFxrPath;
	FString LastError;
};

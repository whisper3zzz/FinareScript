#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "StructUtils/PropertyBag.h"
#include "FinareManagedFunctionAbi.h"
#include "FinareRuntimeDiagnostic.h"
#include "FinareComponent.generated.h"

class UFinareAsset;
class UFinareBlueprintGeneratedClass;
class UEnhancedInputComponent;
class UInputAction;
class AActor;
class APlayerController;
struct FPropertyChangedChainEvent;
struct FInputActionInstance;
struct FFinareInitialRepNotify;
struct FFinareStateMigrationReport;
struct FFinareNativeCallFrameV3;
enum class EFinareManagedInputValueType : uint8;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(
	FFinareManagedSignal,
	int32,
	Value,
	UObject*,
	Context);

DECLARE_DYNAMIC_MULTICAST_DELEGATE_FourParams(
	FFinareBenchmarkSignal,
	int32,
	Value,
	float,
	Weight,
	bool,
	bEnabled,
	UObject*,
	Context);

UCLASS(BlueprintType, Blueprintable, ClassGroup=(Finare), meta=(BlueprintSpawnableComponent))
class FINARERUNTIME_API UFinareComponent : public UActorComponent
{
	GENERATED_BODY()

public:
	UFinareComponent();

	/** General reflected event used by the end-to-end managed subscription demo. */
	UPROPERTY(BlueprintAssignable, Category="Finare|Events")
	FFinareManagedSignal OnManagedSignal;

	/** Four-value event used by the versioned representative delegate workload. */
	UPROPERTY(BlueprintAssignable, Category="Finare|Verification")
	FFinareBenchmarkSignal OnBenchmarkSignal;

	/** Preferred project asset selected from the Content Browser. */
	UPROPERTY(EditAnywhere, Category="Finare", meta=(DisplayName="Script Asset"))
	TObjectPtr<UFinareAsset> ScriptAsset;

	/** Optional soft asset path used when ScriptAsset is not assigned. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare", meta=(AdvancedDisplay))
	FString ScriptAssetPath;

	/** Legacy direct configuration retained for existing components. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Runtime", meta=(AdvancedDisplay))
	FString ScriptAssemblyPath;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Runtime", meta=(AdvancedDisplay))
	FString ScriptTypeName;

	/**
	 * Canonical coexistence-manifest key claimed before managed activation.
	 * Empty derives a stable key from the resolved managed type name.
	 */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Coexistence")
	FName LifecycleOwnershipKey;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare")
	bool bStartAutomatically = true;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Runtime")
	bool bTickAutomatically = true;

	/** C# [Expose] state materialized from the selected Script Asset declaration. */
	UPROPERTY(EditAnywhere, Category="Finare|State", meta=(FixedLayout, DisplayName="Script State"))
	FInstancedPropertyBag ScriptState;

	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare")
	FString ActiveGeneration;

	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare")
	FString ActiveScriptAssetPath;

	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare|Coexistence")
	FName ActiveLifecycleOwnershipKey;

	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare")
	FString ActiveDeclarationAssetPath;

	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare")
	int32 MaterializedStateCount = 0;

	/** Number of state values explicitly overridden by this component instance. */
	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare|State")
	int32 MaterializedStateOverrideCount = 0;

	/** Summary of the last declaration/default/override reconciliation. */
	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare|State")
	FString LastStateMigration;

	/** Script-controlled diagnostic state used by the first hosted demo. */
	UPROPERTY(EditInstanceOnly, BlueprintReadWrite, Transient, Category="Finare|Runtime")
	FString ManagedState;

	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare")
	FString LastScriptError;

	/** End-to-end configuration, verification, state materialization, and managed start latency. */
	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare|Profiler")
	double LastStartMilliseconds = 0.0;

	/** Last compile-to-World reload transaction, including rollback and resource evidence. */
	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare|Reload")
	FString LastReloadTransaction;

	/** Opt-in bounded gameplay/bridge profiler state. Disabled by default. */
	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare|Profiler")
	bool bGameplayProfilerEnabled = false;

	/** Last captured portable unifiedscript.gameplay-profile/v0.3 trace. */
	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare|Profiler")
	FString LastGameplayProfileTrace;

	UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="Finare")
	bool bScriptRunning = false;

	UFUNCTION(BlueprintCallable, Category="Finare")
	bool StartScript();

	UFUNCTION(BlueprintCallable, Category="Finare")
	void StopScript();

	UFUNCTION(BlueprintCallable, Category="Finare")
	bool ReloadScript();

	/** Routes a generated Blueprint function through the owning managed instance. */
	bool InvokeManagedScriptFunction(
		const FString& FunctionName,
		TConstArrayView<FFinareManagedFunctionValueV1> Arguments,
		FFinareManagedFunctionValueV1& OutResult,
		FString& OutError);

	bool InvokeManagedScriptFunctionV2(
		const FString& FunctionName,
		FFinareNativeCallFrameV3& Frame,
		FString& OutError);

	/** Retains initial network notifications until automatic managed activation. */
	bool QueueInitialRepNotify(UObject* Target, UFunction* Function, const void* Parameters);

	/** Extends an authoring publication transaction through the live World swap. */
	bool ReloadScriptWithTransaction(const FString& PublicationTransactionJson);

	/**
	 * Validates the configured candidate, state migration, and live ownership
	 * without mutating runtime/debug/state fields. Authoring surfaces use this
	 * as a batch barrier before changing any matching instance.
	 */
	bool PreflightScriptReloadWithTransaction(
		const FString& PublicationTransactionJson,
		FString& OutPreflightTransactionJson) const;

	/** Loads, starts, and rebinds a candidate while retaining the old generation. */
	bool PrepareScriptReloadWithTransaction(const FString& PreflightTransactionJson);

	/** Publishes a prepared candidate without invoking managed lifecycle code. */
	bool CommitPreparedScriptReload();

	/** Restores retained state/generation after any batch prepare or commit failure. */
	bool AbortPreparedScriptReload(const FString& Reason);

	/** Retires the retained old generation after every batch member commits. */
	bool FinalizePreparedScriptReload();

	bool HasPreparedScriptReload() const { return PreparedScriptReload.IsValid(); }

	UFUNCTION(BlueprintCallable, Category="Finare")
	void RequestReload();

	/** Rebuilds Script State from the configured declaration while preserving compatible overrides. */
	UFUNCTION(BlueprintCallable, CallInEditor, Category="Finare|State")
	bool RefreshScriptState();

	/** Extends an authoring transaction through a stopped/editor instance state refresh. */
	bool RefreshScriptStateWithTransaction(const FString& PublicationTransactionJson);

	UFUNCTION(BlueprintPure, Category="Finare|State")
	int32 GetScriptStateInt32(const FString& Name, int32 Fallback) const;

	UFUNCTION(BlueprintCallable, Category="Finare|State")
	bool SetScriptStateInt32(const FString& Name, int32 Value);

	UFUNCTION(BlueprintPure, Category="Finare|State")
	bool IsScriptStateOverridden(const FString& Name) const;

	/** Returns the exact live World/instance/generation/handle debug identity. */
	bool GetRuntimeDebugContext(FFinareRuntimeDebugContext& OutContext) const;

	/** Resolved loose assembly or verified .uschunk currently owned by the live generation. */
	const FString& GetActiveProgramPath() const { return ActiveResolvedAssemblyPath; }

	/** Pauses automatic managed ticks without blocking the UE Game Thread. */
	bool PauseAtDebugFrameBoundary();
	bool ContinueFromDebugFrameBoundary();
	bool StepOneDebugFrame();
	bool IsPausedAtDebugFrameBoundary() const { return bDebugPausedAtFrameBoundary; }

	/** Attaches an instrumented semantic generation to this live World instance. */
	bool ConfigureSemanticDebug(const FString& ConfigurationJson, FString& OutError);
	bool ControlSemanticDebug(
		EFinareSemanticDebugControl Control,
		const FString& PauseId,
		FString& OutError);
	bool DisableSemanticDebug(FString& OutError);
	const FString& GetSemanticDebugStateJson() const { return SemanticDebugStateJson; }

	/** Enables bounded timing/allocation/bridge profiling for this live script. */
	bool SetGameplayProfilerEnabled(bool bEnabled, FString& OutError);

	/** Captures the current portable profiler timeline and aggregate JSON. */
	bool CaptureGameplayProfileTrace(FString& OutJson, FString& OutError);

	/** Restores one state to the current C# declaration default. */
	UFUNCTION(BlueprintCallable, CallInEditor, Category="Finare|State")
	bool ResetScriptStateOverride(const FString& Name);

	/** Restores every state to the current C# declaration defaults. */
	UFUNCTION(BlueprintCallable, CallInEditor, Category="Finare|State")
	bool ResetAllScriptStateOverrides();

	/** UObject whose behavior is implemented by the managed script. */
	UFUNCTION(BlueprintPure, Category="Finare")
	UObject* GetScriptTarget();

	/** Exact active managed class/receiver used by the Blueprint parent bridge. */
	const UFinareBlueprintGeneratedClass* GetActiveManagedGeneratedClass() const;
	UObject* GetParentInvocationReceiver() const;
	/** Exact configured owner used only by pre-start identity-bearing callbacks. */
	bool OwnsConfiguredManagedOverride(
		const UFinareBlueprintGeneratedClass* GeneratedClass) const;
	bool OwnsActiveManagedOverride(
		const UFinareBlueprintGeneratedClass* GeneratedClass) const;

	UFUNCTION(BlueprintPure, Category="Finare|Verification")
	int32 Add(int32 Left, int32 Right) const;

	UFUNCTION(BlueprintPure, Category="Finare|Verification")
	FVector AddBenchmarkVector(FVector Left, FVector Right) const;

	UFUNCTION(BlueprintPure, Category="Finare|Verification")
	TArray<int32> EchoBenchmarkArray(const TArray<int32>& Values) const;

	UFUNCTION(BlueprintPure, Category="Finare|Verification")
	TMap<int32, int32> EchoBenchmarkMap(
		const TMap<int32, int32>& Values) const;

	/** Resets the deterministic native Actor state used by frame workloads. */
	UFUNCTION(BlueprintCallable, Category="Finare|Verification")
	void ResetBenchmarkActorState();

	/** Applies one movement/look step and returns X/Y/Yaw as one value payload. */
	UFUNCTION(BlueprintCallable, Category="Finare|Verification")
	FVector ApplyBenchmarkActorInput(
		float MoveForward,
		float MoveRight,
		float LookHorizontal,
		float LookVertical);

	UFUNCTION(BlueprintCallable, Category="Finare|Verification")
	void BroadcastManagedSignal(int32 Value, UObject* Context);

	UFUNCTION(BlueprintCallable, Category="Finare|Verification")
	void BroadcastBenchmarkSignal(
		int32 Value,
		float Weight,
		bool bEnabled,
		UObject* Context);

	bool ConsumeReloadRequest();

	/** Exact number of Enhanced Input handles owned by this live generation. */
	int32 GetManagedInputBindingCount() const
	{
		return ManagedInputBindingHandles.Num();
	}

	virtual void BeginPlay() override;
	virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;
	virtual void TickComponent(
		float DeltaTime,
		ELevelTick TickType,
		FActorComponentTickFunction* ThisTickFunction) override;
#if WITH_EDITOR
	virtual void PostEditChangeChainProperty(
		FPropertyChangedChainEvent& PropertyChangedEvent) override;
#endif

private:
	TArray<TSharedPtr<FFinareInitialRepNotify>> InitialRepNotifies;
	bool bManagedStartAttempted = false;

	struct FPreparedScriptReloadState
	{
		bool bWasRunning = false;
		bool bCommitted = false;
		int32 FromGeneration = 0;
		int32 CandidateGeneration = 0;
		FString TransactionJson;
		FInstancedPropertyBag PreviousScriptState;
		TArray<FGuid> PreviousOverrideIds;
		bool bPreviousOverrideBaselineInitialized = false;
		int32 PreviousMaterializedStateCount = 0;
		int32 PreviousMaterializedStateOverrideCount = 0;
		FString PreviousLastStateMigration;
		FString PreviousManagedState;
		bool bPreviousStartAutomatically = false;
		bool bPreviousTickAutomatically = false;
		TWeakObjectPtr<UFinareAsset> PreviousActiveAsset;
		FString PreviousActiveScriptAssetPath;
		FString PreviousActiveDeclarationAssetPath;
		FString PreviousResolvedAssemblyPath;
		FString PreviousResolvedTypeName;
		TWeakObjectPtr<UFinareAsset> CandidateAsset;
		FString CandidateDeclarationAssetPath;
		FString CandidateResolvedAssemblyPath;
		FString CandidateResolvedTypeName;
	};

	UFUNCTION()
	void HandleActorBeginOverlap(AActor* OverlappedActor, AActor* OtherActor);

	UFUNCTION()
	void HandleActorEndOverlap(AActor* OverlappedActor, AActor* OtherActor);

	void BindManagedActorEvents();
	void UnbindManagedActorEvents();
	void DispatchManagedActorEvent(int32 EventKind, AActor* OtherActor);
	bool BindManagedInputActions(FString& OutError);
	void UnbindManagedInputActions();
	void HandleManagedInputAction(
		const FInputActionInstance& ActionInstance,
		FName ManagedName,
		EFinareManagedInputValueType ValueType);

	bool ResolveScriptConfiguration(
		FString& OutAssemblyPath,
		FString& OutTypeName,
		UFinareAsset*& OutAsset,
		FString& OutError) const;
	bool HasConfiguredManagedLifecycleOverride(FName FunctionName) const;
	bool HasActiveManagedLifecycleOverride(FName FunctionName) const;
	bool BuildScriptState(
		const UFinareAsset* ResolvedAsset,
		const FInstancedPropertyBag& ExistingState,
		TConstArrayView<FGuid> ExistingOverrideIds,
		FInstancedPropertyBag& OutState,
		TArray<FGuid>& OutOverrideIds,
		FFinareStateMigrationReport& OutMigration,
		FString& OutDeclarationAssetPath,
		FString& OutError) const;
	TArray<FGuid> GetEffectiveScriptStateOverrideIds() const;
	void MarkScriptStateOverridden(const FGuid& PropertyId);
	void ApplyStateMigrationDiagnostics(
		const FFinareStateMigrationReport& Migration);
	FString ResolveConfiguredPath(const FString& ConfiguredPath) const;
	void SetScriptFailure(FString Error);
	bool RefreshSemanticDebugState(FString& OutError, bool& bOutFaulted);
	bool DisableSemanticDebugAndDrain(FString& OutError);

	UPROPERTY(Transient)
	TObjectPtr<UFinareAsset> ActiveScriptAsset;

	/** Stable declaration IDs explicitly overridden on this component instance. */
	UPROPERTY()
	TArray<FGuid> ScriptStateOverrideIds;

	/** Distinguishes new components from legacy bags that predate override tracking. */
	UPROPERTY()
	bool bScriptStateOverrideBaselineInitialized = false;

	FString ActiveResolvedAssemblyPath;
	FString ActiveResolvedTypeName;
	TWeakObjectPtr<UObject> ActiveLifecycleHostObject;
	bool bReloadRequested = false;
	bool bActorEventsBound = false;
	bool bManagedCreatedInputComponent = false;
	bool bStopRequestedByInputFailure = false;
	TWeakObjectPtr<UEnhancedInputComponent> ManagedEnhancedInputComponent;
	TWeakObjectPtr<APlayerController> ManagedInputPlayerController;
	TArray<uint32> ManagedInputBindingHandles;

	UPROPERTY(Transient)
	TArray<TObjectPtr<UInputAction>> ManagedInputActions;

	uint64 ManagedScriptInstanceId = 0;
	uint64 ManagedHostHandle = 0;
	uint64 ManagedTargetHandle = 0;
	FFinareRuntimeDiagnostic LastRuntimeDiagnostic;
	bool bDebugPausedAtFrameBoundary = false;
	bool bDebugStepFrameRequested = false;
	bool bSemanticDebugEnabled = false;
	bool bSemanticDebugTickActive = false;
	bool bSemanticDebugPaused = false;
	FString SemanticDebugStateJson;
	TUniquePtr<FPreparedScriptReloadState> PreparedScriptReload;
	FVector BenchmarkActorLocation = FVector::ZeroVector;
	FRotator BenchmarkActorRotation = FRotator::ZeroRotator;
};

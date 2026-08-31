#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "StructUtils/PropertyBag.h"
#include "UnifiedScriptRuntimeDiagnostic.h"
#include "UnifiedScriptComponent.generated.h"

class UUnifiedScriptAsset;
class AActor;
struct FPropertyChangedChainEvent;
struct FUnifiedScriptStateMigrationReport;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(
    FUnifiedScriptManagedSignal,
    int32,
    Value,
    UObject*,
    Context);

DECLARE_DYNAMIC_MULTICAST_DELEGATE_FourParams(
    FUnifiedScriptBenchmarkSignal,
    int32,
    Value,
    float,
    Weight,
    bool,
    bEnabled,
    UObject*,
    Context);

UCLASS(BlueprintType, Blueprintable, ClassGroup=(UnifiedScript), meta=(BlueprintSpawnableComponent))
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    UUnifiedScriptComponent();

    /** General reflected event used by the end-to-end managed subscription demo. */
    UPROPERTY(BlueprintAssignable, Category="UnifiedScript|Events")
    FUnifiedScriptManagedSignal OnManagedSignal;

    /** Four-value event used by the versioned representative delegate workload. */
    UPROPERTY(BlueprintAssignable, Category="UnifiedScript|Verification")
    FUnifiedScriptBenchmarkSignal OnBenchmarkSignal;

    /** Preferred project asset selected from the Content Browser. */
    UPROPERTY(EditAnywhere, Category="UnifiedScript", meta=(DisplayName="Script Asset"))
    TObjectPtr<UUnifiedScriptAsset> ScriptAsset;

    /** Optional soft asset path used when ScriptAsset is not assigned. */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript", meta=(AdvancedDisplay))
    FString ScriptAssetPath;

    /** Legacy direct configuration retained for existing components. */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Runtime", meta=(AdvancedDisplay))
    FString ScriptAssemblyPath;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Runtime", meta=(AdvancedDisplay))
    FString ScriptTypeName;

    /**
     * Canonical coexistence-manifest key claimed before managed activation.
     * Empty derives a stable key from the resolved managed type name.
     */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Coexistence")
    FName LifecycleOwnershipKey;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript")
    bool bStartAutomatically = true;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Runtime")
    bool bTickAutomatically = true;

    /** C# [Expose] state materialized from the selected Script Asset declaration. */
    UPROPERTY(EditAnywhere, Category="UnifiedScript|State", meta=(FixedLayout, DisplayName="Script State"))
    FInstancedPropertyBag ScriptState;

    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript")
    FString ActiveGeneration;

    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript")
    FString ActiveScriptAssetPath;

    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript|Coexistence")
    FName ActiveLifecycleOwnershipKey;

    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript")
    FString ActiveDeclarationAssetPath;

    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript")
    int32 MaterializedStateCount = 0;

    /** Number of state values explicitly overridden by this component instance. */
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript|State")
    int32 MaterializedStateOverrideCount = 0;

    /** Summary of the last declaration/default/override reconciliation. */
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript|State")
    FString LastStateMigration;

    /** Script-controlled diagnostic state used by the first hosted demo. */
    UPROPERTY(EditInstanceOnly, BlueprintReadWrite, Transient, Category="UnifiedScript|Runtime")
    FString ManagedState;

    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript")
    FString LastScriptError;

    /** End-to-end configuration, verification, state materialization, and managed start latency. */
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript|Profiler")
    double LastStartMilliseconds = 0.0;

    /** Last compile-to-World reload transaction, including rollback and resource evidence. */
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript|Reload")
    FString LastReloadTransaction;

    /** Opt-in bounded gameplay/bridge profiler state. Disabled by default. */
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript|Profiler")
    bool bGameplayProfilerEnabled = false;

    /** Last captured portable unifiedscript.gameplay-profile/v0.3 trace. */
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript|Profiler")
    FString LastGameplayProfileTrace;

    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript")
    bool bScriptRunning = false;

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    bool StartScript();

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    void StopScript();

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    bool ReloadScript();

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

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    void RequestReload();

    /** Rebuilds Script State from the configured declaration while preserving compatible overrides. */
    UFUNCTION(BlueprintCallable, CallInEditor, Category="UnifiedScript|State")
    bool RefreshScriptState();

    /** Extends an authoring transaction through a stopped/editor instance state refresh. */
    bool RefreshScriptStateWithTransaction(const FString& PublicationTransactionJson);

    UFUNCTION(BlueprintPure, Category="UnifiedScript|State")
    int32 GetScriptStateInt32(const FString& Name, int32 Fallback) const;

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|State")
    bool SetScriptStateInt32(const FString& Name, int32 Value);

    UFUNCTION(BlueprintPure, Category="UnifiedScript|State")
    bool IsScriptStateOverridden(const FString& Name) const;

    /** Returns the exact live World/instance/generation/handle debug identity. */
    bool GetRuntimeDebugContext(FUnifiedScriptRuntimeDebugContext& OutContext) const;

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
        EUnifiedScriptSemanticDebugControl Control,
        const FString& PauseId,
        FString& OutError);
    bool DisableSemanticDebug(FString& OutError);
    const FString& GetSemanticDebugStateJson() const { return SemanticDebugStateJson; }

    /** Enables bounded timing/allocation/bridge profiling for this live script. */
    bool SetGameplayProfilerEnabled(bool bEnabled, FString& OutError);

    /** Captures the current portable profiler timeline and aggregate JSON. */
    bool CaptureGameplayProfileTrace(FString& OutJson, FString& OutError);

    /** Restores one state to the current C# declaration default. */
    UFUNCTION(BlueprintCallable, CallInEditor, Category="UnifiedScript|State")
    bool ResetScriptStateOverride(const FString& Name);

    /** Restores every state to the current C# declaration defaults. */
    UFUNCTION(BlueprintCallable, CallInEditor, Category="UnifiedScript|State")
    bool ResetAllScriptStateOverrides();

    /** UObject whose behavior is implemented by the managed script. */
    UFUNCTION(BlueprintPure, Category="UnifiedScript")
    UObject* GetScriptTarget();

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Verification")
    int32 Add(int32 Left, int32 Right) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Verification")
    FVector AddBenchmarkVector(FVector Left, FVector Right) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Verification")
    TArray<int32> EchoBenchmarkArray(const TArray<int32>& Values) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Verification")
    TMap<int32, int32> EchoBenchmarkMap(
        const TMap<int32, int32>& Values) const;

    /** Resets the deterministic native Actor state used by frame workloads. */
    UFUNCTION(BlueprintCallable, Category="UnifiedScript|Verification")
    void ResetBenchmarkActorState();

    /** Applies one movement/look step and returns X/Y/Yaw as one value payload. */
    UFUNCTION(BlueprintCallable, Category="UnifiedScript|Verification")
    FVector ApplyBenchmarkActorInput(
        float MoveForward,
        float MoveRight,
        float LookHorizontal,
        float LookVertical);

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|Verification")
    void BroadcastManagedSignal(int32 Value, UObject* Context);

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|Verification")
    void BroadcastBenchmarkSignal(
        int32 Value,
        float Weight,
        bool bEnabled,
        UObject* Context);

    bool ConsumeReloadRequest();

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
    UFUNCTION()
    void HandleActorBeginOverlap(AActor* OverlappedActor, AActor* OtherActor);

    UFUNCTION()
    void HandleActorEndOverlap(AActor* OverlappedActor, AActor* OtherActor);

    void BindManagedActorEvents();
    void UnbindManagedActorEvents();
    void DispatchManagedActorEvent(int32 EventKind, AActor* OtherActor);

    bool ResolveScriptConfiguration(
        FString& OutAssemblyPath,
        FString& OutTypeName,
        UUnifiedScriptAsset*& OutAsset,
        FString& OutError) const;
    bool BuildScriptState(
        const UUnifiedScriptAsset* ResolvedAsset,
        const FInstancedPropertyBag& ExistingState,
        TConstArrayView<FGuid> ExistingOverrideIds,
        FInstancedPropertyBag& OutState,
        TArray<FGuid>& OutOverrideIds,
        FUnifiedScriptStateMigrationReport& OutMigration,
        FString& OutDeclarationAssetPath,
        FString& OutError) const;
    TArray<FGuid> GetEffectiveScriptStateOverrideIds() const;
    void MarkScriptStateOverridden(const FGuid& PropertyId);
    void ApplyStateMigrationDiagnostics(
        const FUnifiedScriptStateMigrationReport& Migration);
    FString ResolveConfiguredPath(const FString& ConfiguredPath) const;
    void SetScriptFailure(FString Error);
    bool RefreshSemanticDebugState(FString& OutError, bool& bOutFaulted);
    bool DisableSemanticDebugAndDrain(FString& OutError);

    UPROPERTY(Transient)
    TObjectPtr<UUnifiedScriptAsset> ActiveScriptAsset;

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
    uint64 ManagedScriptInstanceId = 0;
    uint64 ManagedHostHandle = 0;
    uint64 ManagedTargetHandle = 0;
    FUnifiedScriptRuntimeDiagnostic LastRuntimeDiagnostic;
    bool bDebugPausedAtFrameBoundary = false;
    bool bDebugStepFrameRequested = false;
    bool bSemanticDebugEnabled = false;
    bool bSemanticDebugTickActive = false;
    bool bSemanticDebugPaused = false;
    FString SemanticDebugStateJson;
    FVector BenchmarkActorLocation = FVector::ZeroVector;
    FRotator BenchmarkActorRotation = FRotator::ZeroRotator;
};

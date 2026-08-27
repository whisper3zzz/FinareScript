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

UCLASS(BlueprintType, Blueprintable, ClassGroup=(UnifiedScript), meta=(BlueprintSpawnableComponent))
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    UUnifiedScriptComponent();

    /** General reflected event used by the end-to-end managed subscription demo. */
    UPROPERTY(BlueprintAssignable, Category="UnifiedScript|Events")
    FUnifiedScriptManagedSignal OnManagedSignal;

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

    /** Last compile-to-World reload transaction, including rollback and resource evidence. */
    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript|Reload")
    FString LastReloadTransaction;

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

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    void RequestReload();

    /** Rebuilds Script State from the configured declaration while preserving compatible overrides. */
    UFUNCTION(BlueprintCallable, CallInEditor, Category="UnifiedScript|State")
    bool RefreshScriptState();

    UFUNCTION(BlueprintPure, Category="UnifiedScript|State")
    int32 GetScriptStateInt32(const FString& Name, int32 Fallback) const;

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|State")
    bool SetScriptStateInt32(const FString& Name, int32 Value);

    UFUNCTION(BlueprintPure, Category="UnifiedScript|State")
    bool IsScriptStateOverridden(const FString& Name) const;

    /** Returns the exact live World/instance/generation/handle debug identity. */
    bool GetRuntimeDebugContext(FUnifiedScriptRuntimeDebugContext& OutContext) const;

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

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|Verification")
    void BroadcastManagedSignal(int32 Value, UObject* Context);

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
};

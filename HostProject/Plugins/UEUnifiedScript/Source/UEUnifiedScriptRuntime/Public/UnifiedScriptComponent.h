#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "StructUtils/PropertyBag.h"
#include "UnifiedScriptComponent.generated.h"

class UUnifiedScriptAsset;
class AActor;

UCLASS(BlueprintType, Blueprintable, ClassGroup=(UnifiedScript), meta=(BlueprintSpawnableComponent))
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    UUnifiedScriptComponent();

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

    /** Script-controlled diagnostic state used by the first hosted demo. */
    UPROPERTY(EditInstanceOnly, BlueprintReadWrite, Transient, Category="UnifiedScript|Runtime")
    FString ManagedState;

    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript")
    FString LastScriptError;

    UPROPERTY(VisibleInstanceOnly, BlueprintReadOnly, Transient, Category="UnifiedScript")
    bool bScriptRunning = false;

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    bool StartScript();

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    void StopScript();

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    bool ReloadScript();

    UFUNCTION(BlueprintCallable, Category="UnifiedScript")
    void RequestReload();

    /** Rebuilds Script State from the configured declaration while preserving compatible overrides. */
    UFUNCTION(BlueprintCallable, CallInEditor, Category="UnifiedScript|State")
    bool RefreshScriptState();

    UFUNCTION(BlueprintPure, Category="UnifiedScript|State")
    int32 GetScriptStateInt32(const FString& Name, int32 Fallback) const;

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|State")
    bool SetScriptStateInt32(const FString& Name, int32 Value);

    /** UObject whose behavior is implemented by the managed script. */
    UFUNCTION(BlueprintPure, Category="UnifiedScript")
    UObject* GetScriptTarget();

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Verification")
    int32 Add(int32 Left, int32 Right) const;

    bool ConsumeReloadRequest();

    virtual void BeginPlay() override;
    virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;
    virtual void TickComponent(
        float DeltaTime,
        ELevelTick TickType,
        FActorComponentTickFunction* ThisTickFunction) override;

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
        FInstancedPropertyBag& OutState,
        FString& OutDeclarationAssetPath,
        FString& OutError) const;
    FString ResolveConfiguredPath(const FString& ConfiguredPath) const;
    void SetScriptFailure(FString Error);

    UPROPERTY(Transient)
    TObjectPtr<UUnifiedScriptAsset> ActiveScriptAsset;

    FString ActiveResolvedAssemblyPath;
    FString ActiveResolvedTypeName;
    bool bReloadRequested = false;
    bool bActorEventsBound = false;
    uint64 ManagedScriptInstanceId = 0;
    uint64 ManagedHostHandle = 0;
    uint64 ManagedTargetHandle = 0;
};

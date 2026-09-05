/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-25 10:42:29
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 18:35:18
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Public\UnifiedScriptInteropFixture.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"
#include "InputAction.h"
#include "UObject/Interface.h"
#include "UObject/Object.h"
#include "UnifiedScriptInteropFixture.generated.h"

UENUM(BlueprintType)
enum class EUnifiedScriptInteropMode : uint8
{
    Idle,
    Active,
    Suspended,
};

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(
    FUnifiedScriptInteropChanged,
    int32,
    NewValue,
    UObject*,
    Context);

UINTERFACE(BlueprintType)
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptInteropContract : public UInterface
{
    GENERATED_BODY()
};

class UEUNIFIEDSCRIPTRUNTIME_API IUnifiedScriptInteropContract
{
    GENERATED_BODY()

public:
    /** Reviewed native interface route used by generated managed Script Classes. */
    UFUNCTION(BlueprintCallable, BlueprintNativeEvent, Category="UnifiedScript|Interop")
    int32 TransformManagedValue(int32 Value);
};

/** Cook-safe Enhanced Input action used by the Script Class product smoke. */
UCLASS()
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptInputActionFixture : public UInputAction
{
    GENERATED_BODY()

public:
    UUnifiedScriptInputActionFixture();
};

/** Public interop fixture used to keep generated common-type bindings executable. */
UCLASS(BlueprintType)
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptInteropFixture
    : public UObject
    , public IUnifiedScriptInteropContract
{
    GENERATED_BODY()

public:
    virtual int32 TransformManagedValue_Implementation(int32 Value) override;

    UPROPERTY(BlueprintAssignable, Category="UnifiedScript|Interop")
    FUnifiedScriptInteropChanged OnInteropChanged;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    uint8 ByteValue = 8;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    int32 Int32Value = 32;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    int64 Int64Value = 64;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    float FloatValue = 1.25f;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    double DoubleValue = 2.5;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    FName NameValue = TEXT("Fixture");

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    FText TextValue = FText::FromString(TEXT("Ready"));

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    FString StringValue = TEXT("Ready");

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    FVector VectorValue = FVector(1.0, 2.0, 3.0);

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    FRotator RotatorValue = FRotator(10.0, 20.0, 30.0);

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    FTransform TransformValue = FTransform::Identity;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    TObjectPtr<UObject> ObjectValue = nullptr;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    TScriptInterface<IUnifiedScriptInteropContract> InterfaceValue;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    TObjectPtr<UClass> ClassValue = nullptr;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    TSoftObjectPtr<UObject> SoftObjectValue;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    TSoftClassPtr<UObject> SoftClassValue;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    EUnifiedScriptInteropMode Mode = EUnifiedScriptInteropMode::Idle;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    TArray<int32> IntArrayValue;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    TSet<int32> IntSetValue;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="UnifiedScript|Interop")
    TMap<int32, int32> IntMapValue;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    FVector AddVector(FVector Left, FVector Right) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop|Fixed")
    int32 FixedArity0() const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop|Fixed")
    float FixedArity1(float Value) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop|Fixed")
    bool FixedArity2(bool bEnabled, int32 Count) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop|Fixed")
    UObject* FixedArity3(UObject* Context, bool bEnabled, float Weight) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop|Fixed")
    FVector FixedArity4(
        FVector Location,
        int32 Count,
        float Scale,
        bool bEnabled) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    FTransform EchoTransform(FTransform Value) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    UObject* EchoObject(UObject* Value) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    TScriptInterface<IUnifiedScriptInteropContract> EchoInterface(
        TScriptInterface<IUnifiedScriptInteropContract> Value) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    UClass* EchoClass(UClass* Value) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    int32 StringLength(FString Value) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    int32 SumArray(const TArray<int32>& Values) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    int32 CountSet(const TSet<int32>& Values) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    int32 SumMap(const TMap<int32, int32>& Values) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    TArray<int32> EchoArray(const TArray<int32>& Values) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    TSet<int32> EchoSet(const TSet<int32>& Values) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    TMap<int32, int32> EchoMap(const TMap<int32, int32>& Values) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    TArray<int32> MakeArray(int32 Count) const;

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|Interop")
    bool MutateValues(int32 Input, int32& OutDoubled, UPARAM(ref) int32& Accumulator) const;

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|Interop")
    bool MutateInterfaces(
        TScriptInterface<IUnifiedScriptInteropContract> Input,
        TScriptInterface<IUnifiedScriptInteropContract>& OutInterface,
        UPARAM(ref) TScriptInterface<IUnifiedScriptInteropContract>& Accumulator) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop")
    int64 AddInt64(int64 Left, int64 Right) const;

    UFUNCTION(BlueprintPure, Category="UnifiedScript|Interop", meta=(DisplayName="Add With Default"))
    int32 AddWithDefault(int32 Value, int32 Amount = 1) const;

    UFUNCTION(BlueprintCallable, Category="UnifiedScript|Interop")
    void BroadcastInteropChanged(int32 NewValue, UObject* Context);
};

#pragma once

#include "CoreMinimal.h"
#include "InputAction.h"
#include "GameFramework/Actor.h"
#include "UObject/Interface.h"
#include "UObject/Object.h"
#include "UObject/SoftObjectPath.h"
#include "FinareInteropFixture.generated.h"

UENUM(BlueprintType)
enum class EFinareInteropMode : uint8
{
	Idle,
	Active,
	Suspended,
};

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(
	FFinareInteropChanged,
	int32,
	NewValue,
	UObject*,
	Context);

UINTERFACE(BlueprintType)
class FINARERUNTIME_API UFinareInteropContract : public UInterface
{
	GENERATED_BODY()
};

class FINARERUNTIME_API IFinareInteropContract
{
	GENERATED_BODY()

public:
	/** Reviewed native interface route used by generated managed Script Classes. */
	UFUNCTION(BlueprintCallable, BlueprintNativeEvent, Category="Finare|Interop")
	int32 TransformManagedValue(int32 Value);
};

/** Cook-safe Enhanced Input action used by the Script Class product smoke. */
UCLASS()
class FINARERUNTIME_API UFinareInputActionFixture : public UInputAction
{
	GENERATED_BODY()

public:
	UFinareInputActionFixture();
};

/** Public interop fixture used to keep generated common-type bindings executable. */
UCLASS(BlueprintType)
class FINARERUNTIME_API UFinareInteropFixture
	: public UObject
	  , public IFinareInteropContract
{
	GENERATED_BODY()

public:
	virtual int32 TransformManagedValue_Implementation(int32 Value) override;

	UPROPERTY(BlueprintAssignable, Category="Finare|Interop")
	FFinareInteropChanged OnInteropChanged;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	uint8 ByteValue = 8;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	int32 Int32Value = 32;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	int64 Int64Value = 64;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	float FloatValue = 1.25f;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	double DoubleValue = 2.5;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	FName NameValue = TEXT("Fixture");

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	FText TextValue = FText::FromString(TEXT("Ready"));

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	FString StringValue = TEXT("Ready");

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	FVector VectorValue = FVector(1.0, 2.0, 3.0);

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	FRotator RotatorValue = FRotator(10.0, 20.0, 30.0);

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	FTransform TransformValue = FTransform::Identity;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	TObjectPtr<UObject> ObjectValue = nullptr;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	TScriptInterface<IFinareInteropContract> InterfaceValue;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	TObjectPtr<UClass> ClassValue = nullptr;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	TSoftObjectPtr<UObject> SoftObjectValue;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	TSoftClassPtr<UObject> SoftClassValue;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	EFinareInteropMode Mode = EFinareInteropMode::Idle;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	TArray<int32> IntArrayValue;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	TSet<int32> IntSetValue;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop")
	TMap<int32, int32> IntMapValue;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop|References")
	TArray<TObjectPtr<AActor>> ActorArrayValue;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop|References")
	TSet<TSubclassOf<AActor>> ActorClassSetValue;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop|References")
	TMap<FName, TSoftObjectPtr<UObject>> SoftObjectMapValue;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Finare|Interop|References")
	TArray<TSoftClassPtr<AActor>> SoftClassArrayValue;


	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	FVector AddVector(FVector Left, FVector Right) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop|Fixed")
	int32 FixedArity0() const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop|Fixed")
	float FixedArity1(float Value) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop|Fixed")
	bool FixedArity2(bool bEnabled, int32 Count) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop|Fixed")
	UObject* FixedArity3(UObject* Context, bool bEnabled, float Weight) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop|Fixed")
	FVector FixedArity4(
		FVector Location,
		int32 Count,
		float Scale,
		bool bEnabled) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	FTransform EchoTransform(FTransform Value) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	UObject* EchoObject(UObject* Value) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	TScriptInterface<IFinareInteropContract> EchoInterface(
		TScriptInterface<IFinareInteropContract> Value) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	UClass* EchoClass(UClass* Value) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	int32 StringLength(FString Value) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	int32 SumArray(const TArray<int32>& Values) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	int32 CountSet(const TSet<int32>& Values) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	int32 SumMap(const TMap<int32, int32>& Values) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	TArray<int32> EchoArray(const TArray<int32>& Values) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	TSet<int32> EchoSet(const TSet<int32>& Values) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	TMap<int32, int32> EchoMap(const TMap<int32, int32>& Values) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop|References")
	TArray<AActor*> EchoActorArray(const TArray<AActor*>& Values) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop|References")
	TSet<TSubclassOf<AActor>> EchoActorClassSet(
		const TSet<TSubclassOf<AActor>>& Values) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop|References")
	TMap<FName, TSoftObjectPtr<UObject>> EchoSoftObjectMap(
		const TMap<FName, TSoftObjectPtr<UObject>>& Values) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop|References")
	TArray<TSoftClassPtr<AActor>> EchoSoftClassArray(
		const TArray<TSoftClassPtr<AActor>>& Values) const;


	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	TArray<int32> MakeArray(int32 Count) const;

	UFUNCTION(BlueprintCallable, Category="Finare|Interop")
	bool MutateValues(int32 Input, int32& OutDoubled, UPARAM(ref) int32& Accumulator) const;

	UFUNCTION(BlueprintCallable, Category="Finare|Interop|References")
	bool MutateActorArray(
		const TArray<AActor*>& Input,
		TArray<AActor*>& OutActors,
		UPARAM(ref) TArray<AActor*>& Accumulator) const;

	UFUNCTION(BlueprintCallable, Category="Finare|Interop|References")
	bool MutateActorClasses(
		const TSet<TSubclassOf<AActor>>& Input,
		TSet<TSubclassOf<AActor>>& OutClasses,
		UPARAM(ref) TSet<TSubclassOf<AActor>>& Accumulator) const;

	UFUNCTION(BlueprintCallable, Category="Finare|Interop|References")
	bool MutateSoftObjectMap(
		const TMap<FName, TSoftObjectPtr<UObject>>& Input,
		TMap<FName, TSoftObjectPtr<UObject>>& OutPaths,
		UPARAM(ref) TMap<FName, TSoftObjectPtr<UObject>>& Accumulator) const;

	UFUNCTION(BlueprintCallable, Category="Finare|Interop|References")
	bool MutateSoftClassArray(
		const TArray<TSoftClassPtr<AActor>>& Input,
		TArray<TSoftClassPtr<AActor>>& OutPaths,
		UPARAM(ref) TArray<TSoftClassPtr<AActor>>& Accumulator) const;


	UFUNCTION(BlueprintCallable, Category="Finare|Interop")
	bool MutateInterfaces(
		TScriptInterface<IFinareInteropContract> Input,
		TScriptInterface<IFinareInteropContract>& OutInterface,
		UPARAM(ref) TScriptInterface<IFinareInteropContract>& Accumulator) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop")
	int64 AddInt64(int64 Left, int64 Right) const;

	UFUNCTION(BlueprintPure, Category="Finare|Interop", meta=(DisplayName="Add With Default"))
	int32 AddWithDefault(int32 Value, int32 Amount = 1) const;

	UFUNCTION(BlueprintCallable, Category="Finare|Interop")
	void BroadcastInteropChanged(int32 NewValue, UObject* Context);
};

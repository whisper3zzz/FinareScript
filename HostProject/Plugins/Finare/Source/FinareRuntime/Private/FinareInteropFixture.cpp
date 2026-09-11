#include "FinareInteropFixture.h"

UFinareInputActionFixture::UFinareInputActionFixture()
{
	ValueType = EInputActionValueType::Axis1D;
}

int32 UFinareInteropFixture::TransformManagedValue_Implementation(int32 Value)
{
	return Value;
}

FVector UFinareInteropFixture::AddVector(FVector Left, FVector Right) const
{
	return Left + Right;
}

int32 UFinareInteropFixture::FixedArity0() const
{
	return 17;
}

float UFinareInteropFixture::FixedArity1(float Value) const
{
	return Value + 1.5f;
}

bool UFinareInteropFixture::FixedArity2(bool bEnabled, int32 Count) const
{
	return bEnabled && Count == 7;
}

UObject* UFinareInteropFixture::FixedArity3(
	UObject* Context,
	bool bEnabled,
	float Weight) const
{
	return bEnabled && Weight > 0.0f ? Context : nullptr;
}

FVector UFinareInteropFixture::FixedArity4(
	FVector Location,
	int32 Count,
	float Scale,
	bool bEnabled) const
{
	return bEnabled ? Location + FVector(static_cast<double>(Count) * Scale) : Location;
}

FTransform UFinareInteropFixture::EchoTransform(FTransform Value) const
{
	return Value;
}

UObject* UFinareInteropFixture::EchoObject(UObject* Value) const
{
	return Value;
}

TScriptInterface<IFinareInteropContract> UFinareInteropFixture::EchoInterface(
	TScriptInterface<IFinareInteropContract> Value) const
{
	return Value;
}

UClass* UFinareInteropFixture::EchoClass(UClass* Value) const
{
	return Value;
}

int32 UFinareInteropFixture::StringLength(FString Value) const
{
	return Value.Len();
}

int32 UFinareInteropFixture::SumArray(const TArray<int32>& Values) const
{
	int32 Result = 0;
	for (const int32 Value : Values)
	{
		Result += Value;
	}
	return Result;
}

int32 UFinareInteropFixture::CountSet(const TSet<int32>& Values) const
{
	return Values.Num();
}

int32 UFinareInteropFixture::SumMap(const TMap<int32, int32>& Values) const
{
	int32 Result = 0;
	for (const TPair<int32, int32>& Entry : Values)
	{
		Result += Entry.Key + Entry.Value;
	}
	return Result;
}

TArray<int32> UFinareInteropFixture::EchoArray(const TArray<int32>& Values) const
{
	return Values;
}

TSet<int32> UFinareInteropFixture::EchoSet(const TSet<int32>& Values) const
{
	return Values;
}

TMap<int32, int32> UFinareInteropFixture::EchoMap(
	const TMap<int32, int32>& Values) const
{
	return Values;
}

TArray<AActor*> UFinareInteropFixture::EchoActorArray(
	const TArray<AActor*>& Values) const
{
	return Values;
}

TSet<TSubclassOf<AActor>> UFinareInteropFixture::EchoActorClassSet(
	const TSet<TSubclassOf<AActor>>& Values) const
{
	return Values;
}

TMap<FName, TSoftObjectPtr<UObject>> UFinareInteropFixture::EchoSoftObjectMap(
	const TMap<FName, TSoftObjectPtr<UObject>>& Values) const
{
	return Values;
}

TArray<TSoftClassPtr<AActor>> UFinareInteropFixture::EchoSoftClassArray(
	const TArray<TSoftClassPtr<AActor>>& Values) const
{
	return Values;
}


TArray<int32> UFinareInteropFixture::MakeArray(int32 Count) const
{
	TArray<int32> Values;
	Count = FMath::Clamp(Count, 0, 4096);
	Values.Reserve(Count);
	for (int32 Index = 0; Index < Count; ++Index)
	{
		Values.Add(Index);
	}
	return Values;
}

bool UFinareInteropFixture::MutateValues(
	int32 Input,
	int32& OutDoubled,
	int32& Accumulator) const
{
	OutDoubled = Input * 2;
	Accumulator += Input;
	return true;
}

bool UFinareInteropFixture::MutateActorArray(
	const TArray<AActor*>& Input,
	TArray<AActor*>& OutActors,
	TArray<AActor*>& Accumulator) const
{
	OutActors = Input;
	Accumulator.Append(Input);
	return true;
}

bool UFinareInteropFixture::MutateActorClasses(
	const TSet<TSubclassOf<AActor>>& Input,
	TSet<TSubclassOf<AActor>>& OutClasses,
	TSet<TSubclassOf<AActor>>& Accumulator) const
{
	OutClasses = Input;
	for (const TSubclassOf<AActor>& Class : Input)
	{
		Accumulator.Add(Class);
	}
	return true;
}

bool UFinareInteropFixture::MutateSoftObjectMap(
	const TMap<FName, TSoftObjectPtr<UObject>>& Input,
	TMap<FName, TSoftObjectPtr<UObject>>& OutPaths,
	TMap<FName, TSoftObjectPtr<UObject>>& Accumulator) const
{
	OutPaths = Input;
	for (const TPair<FName, TSoftObjectPtr<UObject>>& Entry : Input)
	{
		Accumulator.Add(Entry.Key, Entry.Value);
	}
	return true;
}

bool UFinareInteropFixture::MutateSoftClassArray(
	const TArray<TSoftClassPtr<AActor>>& Input,
	TArray<TSoftClassPtr<AActor>>& OutPaths,
	TArray<TSoftClassPtr<AActor>>& Accumulator) const
{
	OutPaths = Input;
	Accumulator.Append(Input);
	return true;
}


bool UFinareInteropFixture::MutateInterfaces(
	TScriptInterface<IFinareInteropContract> Input,
	TScriptInterface<IFinareInteropContract>& OutInterface,
	TScriptInterface<IFinareInteropContract>& Accumulator) const
{
	OutInterface = Input;
	Accumulator = Input;
	return Input.GetObject() != nullptr;
}

int64 UFinareInteropFixture::AddInt64(int64 Left, int64 Right) const
{
	return Left + Right;
}

int32 UFinareInteropFixture::AddWithDefault(int32 Value, int32 Amount) const
{
	return Value + Amount;
}

void UFinareInteropFixture::BroadcastInteropChanged(int32 NewValue, UObject* Context)
{
	OnInteropChanged.Broadcast(NewValue, Context);
}

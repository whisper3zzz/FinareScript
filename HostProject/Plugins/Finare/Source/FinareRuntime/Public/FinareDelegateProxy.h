#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "FinareNativeTransport.h"
#include "FinareDelegateProxy.generated.h"

class FMulticastDelegateProperty;

/** Transient UObject endpoint whose runtime-created UFunctions mirror arbitrary delegate signatures. */
UCLASS(Transient, meta = (FinareBindingInternal))
class FINARERUNTIME_API UFinareDelegateProxy final : public UObject
{
	GENERATED_BODY()

public:
	static UFunction* FindOrCreateDispatchFunction(const UFunction* Signature);

	bool Bind(
		FFinareNativeBridgeContext* InContext,
		uint64 InSubscriptionId,
		FFinareObjectHandle InSourceHandle,
		uint64 InEventId,
		UObject* InSource,
		const FMulticastDelegateProperty* InProperty,
		FFinareNativeEventCallbackV4 InCallback,
		void* InUserData);

	void Unbind();

private:
	DECLARE_FUNCTION(execDispatch);

	void Dispatch(const UFunction* DispatchFunction, const void* ParameterMemory);

	FFinareNativeBridgeContext* Context = nullptr;
	uint64 SubscriptionId = 0;
	FFinareObjectHandle SourceHandle;
	uint64 EventId = 0;
	TWeakObjectPtr<UObject> Source;
	const FMulticastDelegateProperty* Property = nullptr;
	FScriptDelegate BoundDelegate;
	FFinareNativeEventCallbackV4 Callback = nullptr;
	void* UserData = nullptr;
};

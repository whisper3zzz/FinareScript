/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-25 13:37:42
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 14:22:40
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Public\UnifiedScriptDelegateProxy.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptDelegateProxy.generated.h"

class FMulticastDelegateProperty;

/** Transient UObject endpoint whose runtime-created UFunctions mirror arbitrary delegate signatures. */
UCLASS(Transient, meta = (UnifiedScriptBindingInternal))
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptDelegateProxy final : public UObject
{
    GENERATED_BODY()

public:
    static UFunction* FindOrCreateDispatchFunction(const UFunction* Signature);

    bool Bind(
        FUnifiedScriptNativeBridgeContext* InContext,
        uint64 InSubscriptionId,
        FUnifiedScriptObjectHandle InSourceHandle,
        uint64 InEventId,
        UObject* InSource,
        const FMulticastDelegateProperty* InProperty,
        FUnifiedScriptNativeEventCallbackV4 InCallback,
        void* InUserData);

    void Unbind();

private:
    DECLARE_FUNCTION(execDispatch);

    void Dispatch(const UFunction* DispatchFunction, const void* ParameterMemory);

    FUnifiedScriptNativeBridgeContext* Context = nullptr;
    uint64 SubscriptionId = 0;
    FUnifiedScriptObjectHandle SourceHandle;
    uint64 EventId = 0;
    TWeakObjectPtr<UObject> Source;
    const FMulticastDelegateProperty* Property = nullptr;
    FScriptDelegate BoundDelegate;
    FUnifiedScriptNativeEventCallbackV4 Callback = nullptr;
    void* UserData = nullptr;
};

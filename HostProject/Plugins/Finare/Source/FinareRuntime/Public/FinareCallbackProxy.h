#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "FinareCallbackTransport.h"
#include "FinareCallbackProxy.generated.h"

class FFinareNativeBridgeContext;

/** A retained, typed dynamic delegate endpoint. Saved copies never own the managed handler. */
UCLASS(Transient, meta = (FinareBindingInternal))
class FINARERUNTIME_API UFinareCallbackProxy final : public UObject
{
    GENERATED_BODY()
public:
    bool Bind(FFinareNativeBridgeContext* Context, uint64 Scope, uint64 Token,
        UFunction* Signature, FFinareCallback Callback, void* UserData);
    void Unbind();
    bool CopyDelegate(uint64 Scope, uint64 SignatureId, FScriptDelegate& OutDelegate) const;
    uint64 GetScope() const { return CallbackScope; }
    uint64 GetToken() const { return CallbackToken; }

private:
    DECLARE_FUNCTION(execDispatch);
    bool Dispatch(UFunction* Function, void* Parameters);
    static UFunction* DispatchFunction(UFunction* Signature);
    FFinareNativeBridgeContext* CallbackContext = nullptr;
    uint64 CallbackScope = 0, CallbackToken = 0, CallbackSignatureId = 0;
    FFinareCallback Handler = nullptr;
    void* HandlerData = nullptr;
    FScriptDelegate BoundDelegate;
};

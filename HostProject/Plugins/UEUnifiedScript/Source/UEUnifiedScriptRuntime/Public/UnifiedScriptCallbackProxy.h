#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "UnifiedScriptCallbackTransport.h"
#include "UnifiedScriptCallbackProxy.generated.h"

class FUnifiedScriptNativeBridgeContext;

/** A retained, typed dynamic delegate endpoint. Saved copies never own the managed handler. */
UCLASS(Transient, meta = (UnifiedScriptBindingInternal))
class UEUNIFIEDSCRIPTRUNTIME_API UUnifiedScriptCallbackProxy final : public UObject
{
    GENERATED_BODY()
public:
    bool Bind(FUnifiedScriptNativeBridgeContext* Context, uint64 Scope, uint64 Token,
        UFunction* Signature, FUnifiedScriptCallback Callback, void* UserData);
    void Unbind();
    bool CopyDelegate(uint64 Scope, uint64 SignatureId, FScriptDelegate& OutDelegate) const;
    uint64 GetScope() const { return CallbackScope; }
    uint64 GetToken() const { return CallbackToken; }

private:
    DECLARE_FUNCTION(execDispatch);
    bool Dispatch(UFunction* Function, void* Parameters);
    static UFunction* DispatchFunction(UFunction* Signature);
    FUnifiedScriptNativeBridgeContext* CallbackContext = nullptr;
    uint64 CallbackScope = 0, CallbackToken = 0, CallbackSignatureId = 0;
    FUnifiedScriptCallback Handler = nullptr;
    void* HandlerData = nullptr;
    FScriptDelegate BoundDelegate;
};

#include "UnifiedScriptNativeObjectTransport.h"
#include "UnifiedScriptNativeTransport.h"

namespace
{
using EStatus = EUnifiedScriptNativeObjectStatus;

template <typename F> EStatus Execute(void* Context, F&& Operation)
{
    if (!IsInGameThread()) return EStatus::WrongThread;
    if (!Context) return EStatus::Unavailable;
    EStatus Status = EStatus::Unavailable;
    static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->ExecuteNativeObjects(
        [&](FUnifiedScriptNativeObjectRegistry& Registry) { Status = Operation(Registry); });
    return Status;
}

EStatus OpenScope(void* Context, uint64* Scope)
{
    if (!Scope) return EStatus::InvalidArgument;
    *Scope = 0;
    return Execute(Context, [&](auto& Registry) { return Registry.OpenScope(*Scope); });
}
EStatus CloseScope(void* Context, uint64 Scope)
{
    return Execute(Context, [&](auto& Registry) { return Registry.CloseScope(Scope); });
}
EStatus ReleaseObject(void* Context, uint64 Scope, uint64 Handle)
{
    return Execute(Context, [&](auto& Registry) { return Registry.Release(Scope, {Handle}); });
}
EStatus Invoke(void* Context, uint64 Scope, uint64 Target, uint64 MemberId,
    const FUnifiedScriptNativeValueV3* Arguments, uint32 Count,
    FUnifiedScriptNativeValueV3* OutValue, uint64* OutHandle)
{
    if (OutValue) *OutValue = {};
    if (OutHandle) *OutHandle = 0;
    if (!OutValue || !OutHandle || Count > 16 || (Count && !Arguments)) return EStatus::InvalidArgument;
    return Execute(Context, [&](auto& Registry)
    {
        FUnifiedScriptNativeObjectHandle Handle;
        const auto Status = Registry.Invoke(Scope, {Target}, MemberId,
            MakeArrayView(Arguments, static_cast<int32>(Count)), *OutValue, Handle);
        *OutHandle = Handle.Value;
        return Status;
    });
}
}

extern "C" const FUnifiedScriptNativeObjectTransportV1* UEUnifiedScript_GetNativeObjectTransportV1()
{
    static const FUnifiedScriptNativeObjectTransportWithCapabilities Table{{
        1, sizeof(FUnifiedScriptNativeObjectTransportWithCapabilities), sizeof(FUnifiedScriptNativeValueV3), 0,
        UEUnifiedScript_GetNativeTransportV1()->RetainContext,
        UEUnifiedScript_GetNativeTransportV1()->ReleaseContext,
        &OpenScope, &CloseScope, &ReleaseObject, &Invoke}, 1};
    return &Table.Base;
}

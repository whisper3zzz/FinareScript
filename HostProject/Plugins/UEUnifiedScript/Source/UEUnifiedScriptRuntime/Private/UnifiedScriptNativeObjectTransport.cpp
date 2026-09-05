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

EStatus BeginText(void* Context, uint64 Scope, uint64 Target, uint64 MemberId,
    const FUnifiedScriptNativeValueV3* Arguments, uint32 Count, const uint8* Input, uint32 InputBytes,
    uint64* Token, uint32* RequiredBytes)
{
    if (!Token || !RequiredBytes || Count > 16 || InputBytes > 16u * 1024u * 1024u ||
        (Count && !Arguments) || (InputBytes && !Input)) return EStatus::InvalidArgument;
    *Token = 0; *RequiredBytes = 0;
    return Execute(Context, [&](auto& Registry)
    {
        return Registry.BeginText(Scope, {Target}, MemberId,
            MakeArrayView(Arguments, static_cast<int32>(Count)),
            MakeArrayView(Input, static_cast<int32>(InputBytes)), *Token, *RequiredBytes);
    });
}

EStatus ReadText(void* Context, uint64 Scope, uint64 Token, uint8* Output, uint32 Capacity,
    uint32* RequiredBytes, FUnifiedScriptNativeValueV3* OutValue, uint64* OutHandle)
{
    if (!RequiredBytes || !OutValue || !OutHandle || (Capacity && !Output)) return EStatus::InvalidArgument;
    *RequiredBytes = 0; *OutValue = {}; *OutHandle = 0;
    return Execute(Context, [&](auto& Registry)
    {
        FUnifiedScriptNativeObjectHandle Handle;
        const EStatus Status = Registry.ReadText(Scope, Token, Output, Capacity, *RequiredBytes, *OutValue, Handle);
        *OutHandle = Handle.Value;
        return Status;
    });
}

EStatus DiscardText(void* Context, uint64 Scope, uint64 Token)
{
    return Execute(Context, [&](auto& Registry) { return Registry.DiscardText(Scope, Token); });
}
}

extern "C" const FUnifiedScriptNativeObjectTransportV1* UEUnifiedScript_GetNativeObjectTransportV1()
{
    static const FUnifiedScriptNativeObjectTextTransportV1 TextTable{
        1, sizeof(FUnifiedScriptNativeObjectTextTransportV1), sizeof(FUnifiedScriptNativeValueV3), 0,
        16u * 1024u * 1024u, 16u * 1024u * 1024u, 16u * 1024u * 1024u, 64,
        &BeginText, &ReadText, &DiscardText};
    static const FUnifiedScriptNativeObjectTransportWithText Table{{{
        1, sizeof(FUnifiedScriptNativeObjectTransportWithText), sizeof(FUnifiedScriptNativeValueV3), 0,
        UEUnifiedScript_GetNativeTransportV1()->RetainContext,
        UEUnifiedScript_GetNativeTransportV1()->ReleaseContext,
        &OpenScope, &CloseScope, &ReleaseObject, &Invoke}, 3}, &TextTable};
    return &Table.Base.Base;
}

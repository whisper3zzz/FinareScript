#include "FinareNativeTransport.h"

namespace UE::Finare::NativeTransport::Private
{
uint8 RetainContext(void* Context);
void ReleaseContext(void* Context);
}

namespace UE::Finare::UObjectReferenceTransport::Private
{
EFinareUObjectReferenceStatus OpenScope(void* Context, uint64* OutScope)
{
    if (Context == nullptr || OutScope == nullptr)
    {
        return EFinareUObjectReferenceStatus::Unavailable;
    }
    FFinareObjectHandleResult Result;
    return static_cast<FFinareNativeBridgeContext*>(Context)->OpenUObjectReferenceScope(*OutScope, Result)
        ? EFinareUObjectReferenceStatus::Success
        : (Result.Error == EFinareObjectHandleError::NotGameThread
            ? EFinareUObjectReferenceStatus::WrongThread
            : EFinareUObjectReferenceStatus::Unavailable);
}

EFinareUObjectReferenceStatus CloseScope(void* Context, uint64 Scope)
{
    if (Context == nullptr || Scope == 0)
    {
        return EFinareUObjectReferenceStatus::InvalidScope;
    }
    FFinareObjectHandleResult Result;
    return static_cast<FFinareNativeBridgeContext*>(Context)->CloseUObjectReferenceScope(Scope, Result);
}

EFinareUObjectReferenceStatus Retain(void* Context, uint64 Scope, uint64 Handle)
{
    if (Context == nullptr || Scope == 0)
    {
        return EFinareUObjectReferenceStatus::InvalidScope;
    }
    FFinareObjectHandleResult Result;
    return static_cast<FFinareNativeBridgeContext*>(Context)->RetainUObjectReference(Scope, Handle, Result);
}

EFinareUObjectReferenceStatus Release(void* Context, uint64 Scope, uint64 Handle)
{
    if (Context == nullptr || Scope == 0)
    {
        return EFinareUObjectReferenceStatus::InvalidScope;
    }
    FFinareObjectHandleResult Result;
    return static_cast<FFinareNativeBridgeContext*>(Context)->ReleaseUObjectReference(Scope, Handle, Result);
}
}

extern "C" const FFinareUObjectReferenceTransportV1*
Finare_GetUObjectReferenceTransportV1()
{
    using namespace UE::Finare::UObjectReferenceTransport::Private;
    static const FFinareUObjectReferenceTransportV1 Transport = {
        1,
        sizeof(FFinareUObjectReferenceTransportV1),
        0,
        0,
        &UE::Finare::NativeTransport::Private::RetainContext,
        &UE::Finare::NativeTransport::Private::ReleaseContext,
        &OpenScope,
        &CloseScope,
        &Retain,
        &Release,
    };
    return &Transport;
}

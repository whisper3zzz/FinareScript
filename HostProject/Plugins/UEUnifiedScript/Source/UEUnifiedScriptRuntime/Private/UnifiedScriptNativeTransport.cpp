#include "UnifiedScriptNativeTransport.h"

#include "Containers/StringConv.h"
#include "Misc/ScopeLock.h"

FUnifiedScriptNativeBridgeContext::FUnifiedScriptNativeBridgeContext(
    FUnifiedScriptInvocationBridge& InBridge)
    : Bridge(&InBridge)
{
}

bool FUnifiedScriptNativeBridgeContext::TryRetain()
{
    FScopeLock Lock(&BridgeLock);
    if (Bridge == nullptr)
    {
        return false;
    }
    ReferenceCount.fetch_add(1, std::memory_order_relaxed);
    return true;
}

void FUnifiedScriptNativeBridgeContext::Release()
{
    const uint32 Previous = ReferenceCount.fetch_sub(1, std::memory_order_acq_rel);
    check(Previous > 0);
    if (Previous == 1)
    {
        delete this;
    }
}

void FUnifiedScriptNativeBridgeContext::Detach()
{
    FScopeLock Lock(&BridgeLock);
    Bridge = nullptr;
}

bool FUnifiedScriptNativeBridgeContext::Execute(
    TFunctionRef<void(FUnifiedScriptInvocationBridge&)> Operation)
{
    FScopeLock Lock(&BridgeLock);
    if (Bridge == nullptr)
    {
        return false;
    }
    Operation(*Bridge);
    return true;
}

namespace UE::UnifiedScript::NativeTransport::Private
{
bool TryDecodeUtf8(const uint8* Data, int32 Length, FString& OutValue)
{
    if (Length < 0 || (Length > 0 && Data == nullptr))
    {
        return false;
    }
    if (Length == 0)
    {
        OutValue.Reset();
        return true;
    }

    const FUTF8ToTCHAR Converted(reinterpret_cast<const UTF8CHAR*>(Data), Length);
    OutValue = FString(Converted.Length(), Converted.Get());
    return true;
}

void Complete(
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData,
    const FUnifiedScriptValue& Value,
    const FUnifiedScriptInvocationResult& Result)
{
    if (Callback == nullptr)
    {
        return;
    }

    const FTCHARToUTF8 StringUtf8(*Value.StringValue);
    const FTCHARToUTF8 MessageUtf8(*Result.Message);
    const bool bHasString = Value.Kind == EUnifiedScriptValueKind::String;
    Callback(
        UserData,
        Result.IsSuccess() ? 1 : 0,
        static_cast<uint8>(Value.Kind),
        Value.BooleanValue ? 1 : 0,
        Value.Int32Value,
        bHasString ? reinterpret_cast<const uint8*>(StringUtf8.Get()) : nullptr,
        bHasString ? StringUtf8.Length() : 0,
        static_cast<uint8>(Result.Error),
        static_cast<uint8>(Result.HandleError),
        reinterpret_cast<const uint8*>(MessageUtf8.Get()),
        MessageUtf8.Length());
}

void CompleteFailure(
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData,
    EUnifiedScriptInvocationError Error,
    const TCHAR* Message)
{
    FUnifiedScriptInvocationResult Result;
    Result.Error = Error;
    Result.Message = Message;
    Complete(Callback, UserData, {}, Result);
}

FUnifiedScriptNativeBridgeContext* ResolveContext(
    void* Context,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData)
{
    if (Context != nullptr)
    {
        return static_cast<FUnifiedScriptNativeBridgeContext*>(Context);
    }

    CompleteFailure(
        Callback,
        UserData,
        EUnifiedScriptInvocationError::TransportUnavailable,
        TEXT("The UnifiedScript native invocation context is unavailable."));
    return nullptr;
}

bool DecodeMemberPath(
    const uint8* Data,
    int32 Length,
    FString& OutPath,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData)
{
    if (TryDecodeUtf8(Data, Length, OutPath) && !OutPath.IsEmpty())
    {
        return true;
    }

    CompleteFailure(
        Callback,
        UserData,
        EUnifiedScriptInvocationError::InvalidMemberPath,
        TEXT("The UnifiedScript native member path is invalid UTF-8 or empty."));
    return false;
}

bool DecodeValue(
    const FUnifiedScriptNativeValueV1& NativeValue,
    FUnifiedScriptValue& OutValue,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData)
{
    if (NativeValue.Reserved != 0)
    {
        CompleteFailure(
            Callback,
            UserData,
            EUnifiedScriptInvocationError::ValueTypeMismatch,
            TEXT("The UnifiedScript native value contains non-zero reserved data."));
        return false;
    }

    switch (static_cast<EUnifiedScriptValueKind>(NativeValue.Kind))
    {
    case EUnifiedScriptValueKind::Boolean:
        if (NativeValue.BooleanValue > 1)
        {
            CompleteFailure(
                Callback,
                UserData,
                EUnifiedScriptInvocationError::ValueTypeMismatch,
                TEXT("The UnifiedScript native boolean value is invalid."));
            return false;
        }
        OutValue = FUnifiedScriptValue::FromBoolean(NativeValue.BooleanValue != 0);
        return true;
    case EUnifiedScriptValueKind::Int32:
        OutValue = FUnifiedScriptValue::FromInt32(NativeValue.Int32Value);
        return true;
    case EUnifiedScriptValueKind::String:
    {
        FString StringValue;
        if (!TryDecodeUtf8(
                NativeValue.StringData,
                NativeValue.StringLength,
                StringValue))
        {
            CompleteFailure(
                Callback,
                UserData,
                EUnifiedScriptInvocationError::ValueTypeMismatch,
                TEXT("The UnifiedScript native string slice is invalid."));
            return false;
        }
        OutValue = FUnifiedScriptValue::FromString(MoveTemp(StringValue));
        return true;
    }
    default:
        CompleteFailure(
            Callback,
            UserData,
            EUnifiedScriptInvocationError::UnsupportedValueType,
            TEXT("The UnifiedScript native value kind is unsupported."));
        return false;
    }
}

void ReadProperty(
    void* Context,
    uint64 Target,
    const uint8* PropertyPathData,
    int32 PropertyPathLength,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData)
{
    FUnifiedScriptNativeBridgeContext* NativeContext =
        ResolveContext(Context, Callback, UserData);
    if (NativeContext == nullptr)
    {
        return;
    }
    if (!NativeContext->Execute(
            [&](FUnifiedScriptInvocationBridge& Bridge)
            {
                FString PropertyPath;
                if (!DecodeMemberPath(
                        PropertyPathData,
                        PropertyPathLength,
                        PropertyPath,
                        Callback,
                        UserData))
                {
                    return;
                }
                FUnifiedScriptValue Value;
                FUnifiedScriptInvocationResult Result;
                Bridge.ReadProperty({Target}, PropertyPath, Value, Result);
                Complete(Callback, UserData, Value, Result);
            }))
    {
        CompleteFailure(
            Callback,
            UserData,
            EUnifiedScriptInvocationError::TransportUnavailable,
            TEXT("The UnifiedScript world invocation context has been detached."));
    }
}

void WriteProperty(
    void* Context,
    uint64 Target,
    const uint8* PropertyPathData,
    int32 PropertyPathLength,
    const FUnifiedScriptNativeValueV1* NativeValue,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData)
{
    FUnifiedScriptNativeBridgeContext* NativeContext =
        ResolveContext(Context, Callback, UserData);
    if (NativeContext == nullptr)
    {
        return;
    }
    if (!NativeContext->Execute(
            [&](FUnifiedScriptInvocationBridge& Bridge)
            {
                FString PropertyPath;
                if (!DecodeMemberPath(
                        PropertyPathData,
                        PropertyPathLength,
                        PropertyPath,
                        Callback,
                        UserData))
                {
                    return;
                }
                if (NativeValue == nullptr)
                {
                    CompleteFailure(
                        Callback,
                        UserData,
                        EUnifiedScriptInvocationError::ValueTypeMismatch,
                        TEXT("The UnifiedScript native property value is missing."));
                    return;
                }

                FUnifiedScriptValue Value;
                if (!DecodeValue(*NativeValue, Value, Callback, UserData))
                {
                    return;
                }

                FUnifiedScriptInvocationResult Result;
                Bridge.WriteProperty({Target}, PropertyPath, Value, Result);
                Complete(Callback, UserData, {}, Result);
            }))
    {
        CompleteFailure(
            Callback,
            UserData,
            EUnifiedScriptInvocationError::TransportUnavailable,
            TEXT("The UnifiedScript world invocation context has been detached."));
    }
}

void Invoke(
    void* Context,
    uint64 Target,
    const uint8* FunctionPathData,
    int32 FunctionPathLength,
    const FUnifiedScriptNativeValueV1* NativeArguments,
    int32 ArgumentCount,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData)
{
    FUnifiedScriptNativeBridgeContext* NativeContext =
        ResolveContext(Context, Callback, UserData);
    if (NativeContext == nullptr)
    {
        return;
    }
    if (!NativeContext->Execute(
            [&](FUnifiedScriptInvocationBridge& Bridge)
            {
                FString FunctionPath;
                if (!DecodeMemberPath(
                        FunctionPathData,
                        FunctionPathLength,
                        FunctionPath,
                        Callback,
                        UserData))
                {
                    return;
                }
                if (ArgumentCount < 0 ||
                    (ArgumentCount > 0 && NativeArguments == nullptr))
                {
                    CompleteFailure(
                        Callback,
                        UserData,
                        EUnifiedScriptInvocationError::ArgumentCountMismatch,
                        TEXT("The UnifiedScript native argument slice is invalid."));
                    return;
                }

                TArray<FUnifiedScriptValue, TInlineAllocator<8>> Arguments;
                Arguments.Reserve(ArgumentCount);
                for (int32 Index = 0; Index < ArgumentCount; ++Index)
                {
                    FUnifiedScriptValue Value;
                    if (!DecodeValue(
                            NativeArguments[Index],
                            Value,
                            Callback,
                            UserData))
                    {
                        return;
                    }
                    Arguments.Add(MoveTemp(Value));
                }

                FUnifiedScriptValue ReturnValue;
                FUnifiedScriptInvocationResult Result;
                Bridge.Invoke(
                    {Target},
                    FunctionPath,
                    Arguments,
                    ReturnValue,
                    Result);
                Complete(Callback, UserData, ReturnValue, Result);
            }))
    {
        CompleteFailure(
            Callback,
            UserData,
            EUnifiedScriptInvocationError::TransportUnavailable,
            TEXT("The UnifiedScript world invocation context has been detached."));
    }
}

void ReadPropertyById(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData)
{
    FUnifiedScriptNativeBridgeContext* NativeContext =
        ResolveContext(Context, Callback, UserData);
    if (NativeContext == nullptr)
    {
        return;
    }
    if (!NativeContext->Execute(
            [&](FUnifiedScriptInvocationBridge& Bridge)
            {
                FUnifiedScriptValue Value;
                FUnifiedScriptInvocationResult Result;
                Bridge.ReadPropertyById({Target}, MemberId, Value, Result);
                Complete(Callback, UserData, Value, Result);
            }))
    {
        CompleteFailure(
            Callback,
            UserData,
            EUnifiedScriptInvocationError::TransportUnavailable,
            TEXT("The UnifiedScript world invocation context has been detached."));
    }
}

void WritePropertyById(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    const FUnifiedScriptNativeValueV1* NativeValue,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData)
{
    FUnifiedScriptNativeBridgeContext* NativeContext =
        ResolveContext(Context, Callback, UserData);
    if (NativeContext == nullptr)
    {
        return;
    }
    if (!NativeContext->Execute(
            [&](FUnifiedScriptInvocationBridge& Bridge)
            {
                if (NativeValue == nullptr)
                {
                    CompleteFailure(
                        Callback,
                        UserData,
                        EUnifiedScriptInvocationError::ValueTypeMismatch,
                        TEXT("The UnifiedScript native fast property value is missing."));
                    return;
                }

                FUnifiedScriptValue Value;
                if (!DecodeValue(*NativeValue, Value, Callback, UserData))
                {
                    return;
                }
                FUnifiedScriptInvocationResult Result;
                Bridge.WritePropertyById({Target}, MemberId, Value, Result);
                Complete(Callback, UserData, {}, Result);
            }))
    {
        CompleteFailure(
            Callback,
            UserData,
            EUnifiedScriptInvocationError::TransportUnavailable,
            TEXT("The UnifiedScript world invocation context has been detached."));
    }
}

void InvokeById(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    const FUnifiedScriptNativeValueV1* NativeArguments,
    int32 ArgumentCount,
    FUnifiedScriptNativeResultCallbackV1 Callback,
    void* UserData)
{
    FUnifiedScriptNativeBridgeContext* NativeContext =
        ResolveContext(Context, Callback, UserData);
    if (NativeContext == nullptr)
    {
        return;
    }
    if (!NativeContext->Execute(
            [&](FUnifiedScriptInvocationBridge& Bridge)
            {
                if (ArgumentCount < 0 ||
                    (ArgumentCount > 0 && NativeArguments == nullptr))
                {
                    CompleteFailure(
                        Callback,
                        UserData,
                        EUnifiedScriptInvocationError::ArgumentCountMismatch,
                        TEXT("The UnifiedScript native fast argument slice is invalid."));
                    return;
                }

                TArray<FUnifiedScriptValue, TInlineAllocator<8>> Arguments;
                Arguments.Reserve(ArgumentCount);
                for (int32 Index = 0; Index < ArgumentCount; ++Index)
                {
                    FUnifiedScriptValue Value;
                    if (!DecodeValue(
                            NativeArguments[Index],
                            Value,
                            Callback,
                            UserData))
                    {
                        return;
                    }
                    Arguments.Add(MoveTemp(Value));
                }

                FUnifiedScriptValue ReturnValue;
                FUnifiedScriptInvocationResult Result;
                Bridge.InvokeById(
                    {Target},
                    MemberId,
                    Arguments,
                    ReturnValue,
                    Result);
                Complete(Callback, UserData, ReturnValue, Result);
            }))
    {
        CompleteFailure(
            Callback,
            UserData,
            EUnifiedScriptInvocationError::TransportUnavailable,
            TEXT("The UnifiedScript world invocation context has been detached."));
    }
}

uint8 RetainContext(void* Context)
{
    return Context != nullptr &&
        static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->TryRetain()
        ? 1
        : 0;
}

void ReleaseContext(void* Context)
{
    if (Context != nullptr)
    {
        static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->Release();
    }
}
}

extern "C" const FUnifiedScriptNativeTransportV1*
UEUnifiedScript_GetNativeTransportV1()
{
    using namespace UE::UnifiedScript::NativeTransport::Private;
    static const FUnifiedScriptNativeTransportV1 Transport = {
        1,
        sizeof(FUnifiedScriptNativeTransportV1),
        sizeof(FUnifiedScriptNativeValueV1),
        0,
        &ReadProperty,
        &WriteProperty,
        &Invoke,
        &RetainContext,
        &ReleaseContext,
    };
    return &Transport;
}

extern "C" const FUnifiedScriptNativeTransportV2*
UEUnifiedScript_GetNativeTransportV2()
{
    using namespace UE::UnifiedScript::NativeTransport::Private;
    static const FUnifiedScriptNativeTransportV2 Transport = {
        2,
        sizeof(FUnifiedScriptNativeTransportV2),
        sizeof(FUnifiedScriptNativeValueV1),
        0,
        &ReadProperty,
        &WriteProperty,
        &Invoke,
        &RetainContext,
        &ReleaseContext,
        &ReadPropertyById,
        &WritePropertyById,
        &InvokeById,
    };
    return &Transport;
}

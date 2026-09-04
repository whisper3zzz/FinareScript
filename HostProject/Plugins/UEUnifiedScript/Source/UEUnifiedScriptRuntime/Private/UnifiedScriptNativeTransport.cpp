#include "UnifiedScriptNativeTransport.h"

#include "Containers/StringConv.h"
#include "HAL/Event.h"
#include "HAL/PlatformProcess.h"
#include "Misc/ScopeLock.h"
#include "Misc/ScopeExit.h"
#include "UnifiedScriptDelegateProxy.h"
#include "UnifiedScriptGeneratedThunkRegistry.h"
#include "UObject/Field.h"

#include <atomic>
#include <bit>

namespace UE::UnifiedScript::NativeTransport::LifecycleDiagnostics
{
std::atomic<int32> GLiveContextCount{0};
std::atomic<int32> GAttachedContextCount{0};
std::atomic<int32> GPendingWorkerDispatchCount{0};
std::atomic<uint64> GRejectedWorkerDispatchCount{0};

bool TryReserveWorkerDispatch()
{
    int32 Count = GPendingWorkerDispatchCount.load(std::memory_order_relaxed);
    while (Count <
           FUnifiedScriptNativeBridgeContext::MaximumPendingWorkerDispatchCount)
    {
        if (GPendingWorkerDispatchCount.compare_exchange_weak(
                Count,
                Count + 1,
                std::memory_order_acq_rel,
                std::memory_order_relaxed))
        {
            return true;
        }
    }
    GRejectedWorkerDispatchCount.fetch_add(1, std::memory_order_relaxed);
    return false;
}
}

struct FUnifiedScriptNativeBridgeContext::FPendingGameThreadCall
{
    explicit FPendingGameThreadCall(
        TUniqueFunction<void(FUnifiedScriptInvocationBridge&)>&& InOperation)
        : Operation(MoveTemp(InOperation))
        , Completion(FPlatformProcess::GetSynchEventFromPool(true))
    {
    }

    ~FPendingGameThreadCall()
    {
        FPlatformProcess::ReturnSynchEventToPool(Completion);
    }

    TUniqueFunction<void(FUnifiedScriptInvocationBridge&)> Operation;
    FEvent* Completion = nullptr;
    std::atomic<bool> bExecuted{false};
};

FUnifiedScriptNativeBridgeContext::FUnifiedScriptNativeBridgeContext(
    FUnifiedScriptInvocationBridge& InBridge)
    : Bridge(&InBridge)
    , FastBridge(&InBridge)
{
    UE::UnifiedScript::NativeTransport::LifecycleDiagnostics::GLiveContextCount.fetch_add(
        1,
        std::memory_order_relaxed);
    UE::UnifiedScript::NativeTransport::LifecycleDiagnostics::GAttachedContextCount.fetch_add(
        1,
        std::memory_order_relaxed);
}

FUnifiedScriptNativeBridgeContext::~FUnifiedScriptNativeBridgeContext()
{
    UE::UnifiedScript::NativeTransport::LifecycleDiagnostics::GLiveContextCount.fetch_sub(
        1,
        std::memory_order_relaxed);
}

int32 FUnifiedScriptNativeBridgeContext::GetLiveContextCount()
{
    return UE::UnifiedScript::NativeTransport::LifecycleDiagnostics::GLiveContextCount.load(
        std::memory_order_relaxed);
}

int32 FUnifiedScriptNativeBridgeContext::GetAttachedContextCount()
{
    return UE::UnifiedScript::NativeTransport::LifecycleDiagnostics::GAttachedContextCount.load(
        std::memory_order_relaxed);
}

int32 FUnifiedScriptNativeBridgeContext::GetGlobalPendingWorkerDispatchCount()
{
    return UE::UnifiedScript::NativeTransport::LifecycleDiagnostics::
        GPendingWorkerDispatchCount.load(std::memory_order_relaxed);
}

uint64 FUnifiedScriptNativeBridgeContext::GetGlobalRejectedWorkerDispatchCount()
{
    return UE::UnifiedScript::NativeTransport::LifecycleDiagnostics::
        GRejectedWorkerDispatchCount.load(std::memory_order_relaxed);
}

int32 FUnifiedScriptNativeBridgeContext::GetEventSubscriptionCount()
{
    FScopeLock Lock(&BridgeLock);
    return EventSubscriptions.Num();
}

void FUnifiedScriptNativeBridgeContext::AttachSharedDataPlane(
    FUnifiedScriptSharedDataPlane* InSharedDataPlane)
{
    check(IsInGameThread());
    FScopeLock Lock(&BridgeLock);
    check(Bridge != nullptr);
    SharedDataPlane = InSharedDataPlane;
}

void FUnifiedScriptNativeBridgeContext::AttachGameplayDataPlane(
    FUnifiedScriptGameplayDataPlane* InGameplayDataPlane)
{
    check(IsInGameThread());
    FScopeLock Lock(&BridgeLock);
    check(Bridge != nullptr);
    GameplayDataPlane = InGameplayDataPlane;
}

void FUnifiedScriptNativeBridgeContext::AttachAsyncService(
    FUnifiedScriptAsyncService* InAsyncService)
{
    check(IsInGameThread());
    FScopeLock Lock(&BridgeLock);
    check(Bridge != nullptr);
    AsyncService = InAsyncService;
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
    check(IsInGameThread());
    SuspendWorkerDispatch();
    TArray<TStrongObjectPtr<UObject>> Subscriptions;
    bool bWasAttached = false;
    {
        FScopeLock Lock(&BridgeLock);
        bWasAttached = Bridge != nullptr;
        FastBridge.store(nullptr, std::memory_order_release);
        Bridge = nullptr;
        SharedDataPlane = nullptr;
        GameplayDataPlane = nullptr;
        AsyncService = nullptr;
        PendingCallFrameResult.Reset();
        EventSubscriptions.GenerateValueArray(Subscriptions);
        EventSubscriptions.Reset();
    }
    if (bWasAttached)
    {
        NativeObjects.Detach();
        UE::UnifiedScript::NativeTransport::LifecycleDiagnostics::GAttachedContextCount.fetch_sub(
            1,
            std::memory_order_relaxed);
    }
    for (TStrongObjectPtr<UObject>& Subscription : Subscriptions)
    {
        if (UUnifiedScriptDelegateProxy* Proxy =
                Cast<UUnifiedScriptDelegateProxy>(Subscription.Get()))
        {
            Proxy->Unbind();
        }
        Release();
    }
}

bool FUnifiedScriptNativeBridgeContext::Execute(
    TUniqueFunction<void(FUnifiedScriptInvocationBridge&)>&& Operation)
{
    if (IsInGameThread())
    {
        FScopeLock Lock(&BridgeLock);
        if (Bridge == nullptr)
        {
            return false;
        }
        Operation(*Bridge);
        return true;
    }

    TSharedPtr<FPendingGameThreadCall> Pending =
        MakeShared<FPendingGameThreadCall>(MoveTemp(Operation));
    {
        FScopeLock Lock(&BridgeLock);
        if (Bridge == nullptr ||
            !bAcceptWorkerDispatch.load(std::memory_order_acquire))
        {
            return false;
        }
        if (!UE::UnifiedScript::NativeTransport::LifecycleDiagnostics::
                TryReserveWorkerDispatch())
        {
            return false;
        }
        ReferenceCount.fetch_add(1, std::memory_order_relaxed);
        PendingGameThreadCalls.Enqueue(Pending);
    }
    Pending->Completion->Wait();
    const bool bExecuted = Pending->bExecuted.load(std::memory_order_acquire);
    Release();
    return bExecuted;
}

bool FUnifiedScriptNativeBridgeContext::ExecuteGameThreadFast(
    TUniqueFunction<void(FUnifiedScriptInvocationBridge&)>&& Operation)
{
    if (!IsInGameThread())
    {
        return Execute(MoveTemp(Operation));
    }
    FUnifiedScriptInvocationBridge* Current =
        FastBridge.load(std::memory_order_acquire);
    if (Current == nullptr)
    {
        return false;
    }
    Operation(*Current);
    return true;
}

bool FUnifiedScriptNativeBridgeContext::ExecuteCallFrame(
    TUniqueFunction<void(
        FUnifiedScriptInvocationBridge&,
        FPendingCallFrameResult&)>&& Operation)
{
    return Execute(
        [this, Operation = MoveTemp(Operation)](
            FUnifiedScriptInvocationBridge& CurrentBridge) mutable
        {
            Operation(CurrentBridge, PendingCallFrameResult);
        });
}

bool FUnifiedScriptNativeBridgeContext::ExecuteNativeObjects(
    TFunctionRef<void(FUnifiedScriptNativeObjectRegistry&)> Operation)
{
    if (!IsInGameThread() || FastBridge.load(std::memory_order_acquire) == nullptr) return false;
    Operation(NativeObjects);
    return true;
}

void FUnifiedScriptNativeBridgeContext::PumpGameThreadCalls()
{
    check(IsInGameThread());
    TSharedPtr<FPendingGameThreadCall> Pending;
    while (PendingGameThreadCalls.Dequeue(Pending))
    {
        UE::UnifiedScript::NativeTransport::LifecycleDiagnostics::
            GPendingWorkerDispatchCount.fetch_sub(1, std::memory_order_relaxed);
        {
            FScopeLock Lock(&BridgeLock);
            if (Bridge != nullptr &&
                bAcceptWorkerDispatch.load(std::memory_order_acquire))
            {
                Pending->Operation(*Bridge);
                Pending->bExecuted.store(true, std::memory_order_release);
            }
        }
        Pending->Completion->Trigger();
    }
}

void FUnifiedScriptNativeBridgeContext::SuspendWorkerDispatch()
{
    check(IsInGameThread());
    bAcceptWorkerDispatch.store(false, std::memory_order_release);
    TSharedPtr<FPendingGameThreadCall> Pending;
    while (PendingGameThreadCalls.Dequeue(Pending))
    {
        UE::UnifiedScript::NativeTransport::LifecycleDiagnostics::
            GPendingWorkerDispatchCount.fetch_sub(1, std::memory_order_relaxed);
        Pending->Completion->Trigger();
    }
}

bool FUnifiedScriptNativeBridgeContext::AcquireTransformSnapshot(
    FUnifiedScriptSharedSnapshotViewV4& OutView)
{
    if (!IsInGameThread())
    {
        OutView = {};
        return false;
    }
    FScopeLock Lock(&BridgeLock);
    return Bridge != nullptr && SharedDataPlane != nullptr &&
        SharedDataPlane->AcquireTransformSnapshot(OutView);
}

bool FUnifiedScriptNativeBridgeContext::AcquireGeneratedSnapshot(
    uint64 SchemaId,
    uint32 EntrySize,
    FUnifiedScriptGeneratedDataViewV1& OutView)
{
    if (!IsInGameThread())
    {
        OutView = {};
        return false;
    }
    FScopeLock Lock(&BridgeLock);
    return Bridge != nullptr && SharedDataPlane != nullptr &&
        SharedDataPlane->AcquireGeneratedSnapshot(SchemaId, EntrySize, OutView);
}

bool FUnifiedScriptNativeBridgeContext::GetSharedDataTelemetry(
    FUnifiedScriptSharedDataTelemetryV1& OutTelemetry)
{
    if (!IsInGameThread())
    {
        OutTelemetry = {};
        return false;
    }
    FScopeLock Lock(&BridgeLock);
    return Bridge != nullptr && SharedDataPlane != nullptr &&
        SharedDataPlane->GetTelemetry(OutTelemetry);
}

bool FUnifiedScriptNativeBridgeContext::ExecuteGameplayData(
    TFunctionRef<bool(FUnifiedScriptGameplayDataPlane&)> Operation)
{
    if (!IsInGameThread())
    {
        return false;
    }
    FScopeLock Lock(&BridgeLock);
    return Bridge != nullptr && GameplayDataPlane != nullptr &&
        Operation(*GameplayDataPlane);
}

bool FUnifiedScriptNativeBridgeContext::ExecuteAsyncService(
    TFunctionRef<bool(FUnifiedScriptAsyncService&)> Operation)
{
    if (!IsInGameThread())
    {
        return false;
    }
    FScopeLock Lock(&BridgeLock);
    return Bridge != nullptr && AsyncService != nullptr && Operation(*AsyncService);
}

namespace UE::UnifiedScript::NativeTransport::Private
{
constexpr int32 ObjectHandlePayloadSize = sizeof(uint64);
constexpr int32 VectorPayloadSize = sizeof(uint64) * 3;

void WriteUInt64LittleEndian(uint8* Destination, uint64 Value)
{
    for (int32 Index = 0; Index < sizeof(uint64); ++Index)
    {
        Destination[Index] = static_cast<uint8>(Value >> (Index * 8));
    }
}

uint64 ReadUInt64LittleEndian(const uint8* Source)
{
    uint64 Value = 0;
    for (int32 Index = 0; Index < sizeof(uint64); ++Index)
    {
        Value |= static_cast<uint64>(Source[Index]) << (Index * 8);
    }
    return Value;
}

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

    FString StringValue;
    switch (Value.Kind)
    {
    case EUnifiedScriptValueKind::String:
        StringValue = Value.StringValue;
        break;
    case EUnifiedScriptValueKind::Name:
        StringValue = Value.NameValue.ToString();
        break;
    case EUnifiedScriptValueKind::Text:
        StringValue = Value.TextValue.ToString();
        break;
    default:
        break;
    }
    const FTCHARToUTF8 StringUtf8(*StringValue);
    const FTCHARToUTF8 MessageUtf8(*Result.Message);
    const bool bHasString = Value.Kind == EUnifiedScriptValueKind::String ||
        Value.Kind == EUnifiedScriptValueKind::Name ||
        Value.Kind == EUnifiedScriptValueKind::Text;
    uint8 BinaryPayload[VectorPayloadSize] = {};
    const uint8* ValueData = bHasString
        ? reinterpret_cast<const uint8*>(StringUtf8.Get())
        : nullptr;
    int32 ValueLength = bHasString ? StringUtf8.Length() : 0;
    if (Value.Kind == EUnifiedScriptValueKind::Float64)
    {
        WriteUInt64LittleEndian(BinaryPayload, std::bit_cast<uint64>(Value.Float64Value));
        ValueData = BinaryPayload;
        ValueLength = sizeof(double);
    }
    else if (Value.Kind == EUnifiedScriptValueKind::Object ||
        Value.Kind == EUnifiedScriptValueKind::Class)
    {
        WriteUInt64LittleEndian(BinaryPayload, Value.ObjectHandleValue.Value);
        ValueData = BinaryPayload;
        ValueLength = ObjectHandlePayloadSize;
    }
    else if (Value.Kind == EUnifiedScriptValueKind::Vector)
    {
        WriteUInt64LittleEndian(
            BinaryPayload,
            std::bit_cast<uint64>(Value.VectorValue.X));
        WriteUInt64LittleEndian(
            BinaryPayload + sizeof(uint64),
            std::bit_cast<uint64>(Value.VectorValue.Y));
        WriteUInt64LittleEndian(
            BinaryPayload + sizeof(uint64) * 2,
            std::bit_cast<uint64>(Value.VectorValue.Z));
        ValueData = BinaryPayload;
        ValueLength = VectorPayloadSize;
    }
    Callback(
        UserData,
        Result.IsSuccess() ? 1 : 0,
        static_cast<uint8>(Value.Kind),
        Value.BooleanValue ? 1 : 0,
        Value.Kind == EUnifiedScriptValueKind::Float32 ? std::bit_cast<int32>(Value.Float32Value) : Value.Int32Value,
        ValueData,
        ValueLength,
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
    case EUnifiedScriptValueKind::Float32:
        OutValue = FUnifiedScriptValue::FromFloat32(std::bit_cast<float>(NativeValue.Int32Value));
        return true;
    case EUnifiedScriptValueKind::Float64:
        if (NativeValue.StringData == nullptr || NativeValue.StringLength != sizeof(double))
        {
            CompleteFailure(Callback, UserData, EUnifiedScriptInvocationError::ValueTypeMismatch,
                TEXT("The UnifiedScript native binary64 payload must contain exactly eight bytes."));
            return false;
        }
        OutValue = FUnifiedScriptValue::FromFloat64(std::bit_cast<double>(ReadUInt64LittleEndian(NativeValue.StringData)));
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
    case EUnifiedScriptValueKind::Name:
    case EUnifiedScriptValueKind::Text:
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
                TEXT("The UnifiedScript native text value is invalid UTF-8."));
            return false;
        }
        OutValue = static_cast<EUnifiedScriptValueKind>(NativeValue.Kind) ==
                EUnifiedScriptValueKind::Name
            ? FUnifiedScriptValue::FromName(FName(*StringValue))
            : FUnifiedScriptValue::FromText(FText::FromString(StringValue));
        return true;
    }
    case EUnifiedScriptValueKind::Object:
    case EUnifiedScriptValueKind::Class:
        if (NativeValue.StringData == nullptr ||
            NativeValue.StringLength != ObjectHandlePayloadSize)
        {
            CompleteFailure(
                Callback,
                UserData,
                EUnifiedScriptInvocationError::ValueTypeMismatch,
                TEXT("The UnifiedScript native object handle payload is invalid."));
            return false;
        }
        OutValue = FUnifiedScriptValue::FromObjectHandle(
            {ReadUInt64LittleEndian(NativeValue.StringData)},
            static_cast<EUnifiedScriptValueKind>(NativeValue.Kind) ==
                EUnifiedScriptValueKind::Class);
        return true;
    case EUnifiedScriptValueKind::Vector:
        if (NativeValue.StringData == nullptr ||
            NativeValue.StringLength != VectorPayloadSize)
        {
            CompleteFailure(
                Callback,
                UserData,
                EUnifiedScriptInvocationError::ValueTypeMismatch,
                TEXT("The UnifiedScript native vector payload is invalid."));
            return false;
        }
        OutValue = FUnifiedScriptValue::FromVector(FVector(
            std::bit_cast<double>(ReadUInt64LittleEndian(NativeValue.StringData)),
            std::bit_cast<double>(ReadUInt64LittleEndian(
                NativeValue.StringData + sizeof(uint64))),
            std::bit_cast<double>(ReadUInt64LittleEndian(
                NativeValue.StringData + sizeof(uint64) * 2))));
        return true;
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

constexpr uint32 V3TransformByteSize = 10u * sizeof(double);
constexpr uint32 V3MaxContainerDepth = 16;

bool AddArenaBytesV3(uint32& ByteCursor, uint64 ByteCount)
{
    if (ByteCount > MAX_uint32 || ByteCursor > MAX_uint32 - ByteCount)
    {
        return false;
    }
    ByteCursor += static_cast<uint32>(ByteCount);
    return true;
}

bool MeasureValueV3(
    const FUnifiedScriptValue& Value,
    uint32& ByteCursor,
    uint32 Depth = 0)
{
    if (Depth > V3MaxContainerDepth)
    {
        return false;
    }
    switch (Value.Kind)
    {
    case EUnifiedScriptValueKind::ByteBuffer:
        return static_cast<uint32>(Value.ByteBufferValue.Num()) <= FUnifiedScriptByteBuffer::MaximumLength &&
            AddArenaBytesV3(ByteCursor, Value.ByteBufferValue.Num());
    case EUnifiedScriptValueKind::String:
    {
        const FTCHARToUTF8 Utf8(*Value.StringValue);
        return AddArenaBytesV3(ByteCursor, Utf8.Length());
    }
    case EUnifiedScriptValueKind::Name:
    {
        const FString Text = Value.NameValue.ToString();
        const FTCHARToUTF8 Utf8(*Text);
        return AddArenaBytesV3(ByteCursor, Utf8.Length());
    }
    case EUnifiedScriptValueKind::Text:
    {
        const FString Text = Value.TextValue.ToString();
        const FTCHARToUTF8 Utf8(*Text);
        return AddArenaBytesV3(ByteCursor, Utf8.Length());
    }
    case EUnifiedScriptValueKind::Transform:
        return AddArenaBytesV3(ByteCursor, V3TransformByteSize);
    case EUnifiedScriptValueKind::Struct:
    case EUnifiedScriptValueKind::Array:
    case EUnifiedScriptValueKind::Set:
    case EUnifiedScriptValueKind::Map:
    {
        if (Value.TypeIdValue == 0 ||
            (Value.Kind == EUnifiedScriptValueKind::Map &&
                Value.ContainerValues.Num() % 2 != 0) ||
            !AddArenaBytesV3(
                ByteCursor,
                static_cast<uint64>(Value.ContainerValues.Num()) *
                    sizeof(FUnifiedScriptNativeValueV3)))
        {
            return false;
        }
        for (const FUnifiedScriptValue& Element : Value.ContainerValues)
        {
            if (!MeasureValueV3(Element, ByteCursor, Depth + 1))
            {
                return false;
            }
        }
        return true;
    }
    default:
        return true;
    }
}

void HashBytesV3(uint64& Hash, const void* Data, uint64 ByteCount)
{
    const uint8* Bytes = static_cast<const uint8*>(Data);
    for (uint64 Index = 0; Index < ByteCount; ++Index)
    {
        Hash ^= Bytes[Index];
        Hash *= 1099511628211ull;
    }
}

uint64 HashRequestV3(
    const FUnifiedScriptNativeCallFrameV3& Frame,
    uint32 EntryByteCount)
{
    uint64 Hash = 14695981039346656037ull;
    HashBytesV3(Hash, &Frame.ArgumentCount, sizeof(Frame.ArgumentCount));
    HashBytesV3(Hash, &Frame.ResultCount, sizeof(Frame.ResultCount));
    if (Frame.ArgumentCount > 0)
    {
        HashBytesV3(
            Hash,
            Frame.Arguments,
            static_cast<uint64>(Frame.ArgumentCount) *
                sizeof(FUnifiedScriptNativeValueV3));
    }
    if (Frame.ResultCount > 0)
    {
        HashBytesV3(
            Hash,
            Frame.Results,
            static_cast<uint64>(Frame.ResultCount) *
                sizeof(FUnifiedScriptNativeValueV3));
    }
    if (EntryByteCount > 0)
    {
        HashBytesV3(Hash, Frame.ByteBuffer, EntryByteCount);
    }
    return Hash;
}

void ResetV3Output(FUnifiedScriptNativeCallFrameV3& Frame, uint32 EntryByteCount)
{
    Frame.ResultCount = 0;
    Frame.ByteCount = EntryByteCount;
    Frame.RequiredResultCapacity = 0;
    Frame.RequiredByteCapacity = 0;
    Frame.InvocationError = 0;
    Frame.HandleError = 0;
    Frame.Status = EUnifiedScriptNativeCallStatusV3::Uninitialized;
    Frame.ErrorMessageOffset = 0;
    Frame.ErrorMessageLength = 0;
}

void InvalidFrameV3(FUnifiedScriptNativeCallFrameV3* Frame, uint32 EntryByteCount = 0)
{
    if (Frame == nullptr)
    {
        return;
    }
    ResetV3Output(*Frame, EntryByteCount);
    Frame->Status = EUnifiedScriptNativeCallStatusV3::InvalidFrame;
}

void InvocationFailureV3(
    FUnifiedScriptNativeCallFrameV3& Frame,
    uint32 EntryByteCount,
    const FUnifiedScriptInvocationResult& Result)
{
    ResetV3Output(Frame, EntryByteCount);
    Frame.InvocationError = static_cast<uint8>(Result.Error);
    Frame.HandleError = static_cast<uint8>(Result.HandleError);
    Frame.Status = EUnifiedScriptNativeCallStatusV3::InvocationFailure;
}

void UnsupportedValueV3(FUnifiedScriptNativeCallFrameV3& Frame, uint32 EntryByteCount)
{
    FUnifiedScriptInvocationResult Result;
    Result.Error = EUnifiedScriptInvocationError::UnsupportedValueType;
    InvocationFailureV3(Frame, EntryByteCount, Result);
}

bool ValidateFrameV3(
    FUnifiedScriptNativeCallFrameV3* Frame,
    uint32 ExpectedArgumentCount,
    uint32 ExpectedResultCount,
    uint32& OutEntryByteCount)
{
    if (Frame == nullptr)
    {
        return false;
    }
    OutEntryByteCount = Frame->ByteCount;
    const bool bValid =
        Frame->Flags == 0 &&
        Frame->Reserved8 == 0 &&
        Frame->Reserved32 == 0 &&
        Frame->Status == EUnifiedScriptNativeCallStatusV3::Uninitialized &&
        Frame->InvocationError == 0 &&
        Frame->HandleError == 0 &&
        Frame->RequiredResultCapacity == 0 &&
        Frame->RequiredByteCapacity == 0 &&
        Frame->ErrorMessageOffset == 0 &&
        Frame->ErrorMessageLength == 0 &&
        Frame->ArgumentCount == ExpectedArgumentCount &&
        (ExpectedArgumentCount == 0 || Frame->Arguments != nullptr) &&
        Frame->ResultCount == ExpectedResultCount &&
        Frame->ResultCapacity >= ExpectedResultCount &&
        (ExpectedResultCount == 0 || Frame->Results != nullptr) &&
        Frame->ByteCount <= Frame->ByteCapacity &&
        (Frame->ByteCapacity == 0 || Frame->ByteBuffer != nullptr);
    if (!bValid)
    {
        InvalidFrameV3(Frame, OutEntryByteCount);
        return false;
    }
    return true;
}

bool ToNativeKindV3(
    EUnifiedScriptValueKind Kind,
    EUnifiedScriptNativeValueKindV3& OutKind)
{
    switch (Kind)
    {
    case EUnifiedScriptValueKind::Void:
        OutKind = EUnifiedScriptNativeValueKindV3::Void;
        return true;
    case EUnifiedScriptValueKind::Boolean:
        OutKind = EUnifiedScriptNativeValueKindV3::Boolean;
        return true;
    case EUnifiedScriptValueKind::Int8:
        OutKind = EUnifiedScriptNativeValueKindV3::Int8;
        return true;
    case EUnifiedScriptValueKind::UInt8:
        OutKind = EUnifiedScriptNativeValueKindV3::UInt8;
        return true;
    case EUnifiedScriptValueKind::Int16:
        OutKind = EUnifiedScriptNativeValueKindV3::Int16;
        return true;
    case EUnifiedScriptValueKind::UInt16:
        OutKind = EUnifiedScriptNativeValueKindV3::UInt16;
        return true;
    case EUnifiedScriptValueKind::Int32:
        OutKind = EUnifiedScriptNativeValueKindV3::Int32;
        return true;
    case EUnifiedScriptValueKind::UInt32:
        OutKind = EUnifiedScriptNativeValueKindV3::UInt32;
        return true;
    case EUnifiedScriptValueKind::Int64:
        OutKind = EUnifiedScriptNativeValueKindV3::Int64;
        return true;
    case EUnifiedScriptValueKind::UInt64:
        OutKind = EUnifiedScriptNativeValueKindV3::UInt64;
        return true;
    case EUnifiedScriptValueKind::Float32:
        OutKind = EUnifiedScriptNativeValueKindV3::Float32;
        return true;
    case EUnifiedScriptValueKind::Float64:
        OutKind = EUnifiedScriptNativeValueKindV3::Float64;
        return true;
    case EUnifiedScriptValueKind::Enum:
        OutKind = EUnifiedScriptNativeValueKindV3::Enum;
        return true;
    case EUnifiedScriptValueKind::String:
        OutKind = EUnifiedScriptNativeValueKindV3::String;
        return true;
    case EUnifiedScriptValueKind::Name:
        OutKind = EUnifiedScriptNativeValueKindV3::Name;
        return true;
    case EUnifiedScriptValueKind::Text:
        OutKind = EUnifiedScriptNativeValueKindV3::Text;
        return true;
    case EUnifiedScriptValueKind::Object:
        OutKind = EUnifiedScriptNativeValueKindV3::Object;
        return true;
    case EUnifiedScriptValueKind::Class:
        OutKind = EUnifiedScriptNativeValueKindV3::Class;
        return true;
    case EUnifiedScriptValueKind::Vector:
        OutKind = EUnifiedScriptNativeValueKindV3::Vector3;
        return true;
    case EUnifiedScriptValueKind::Rotator:
        OutKind = EUnifiedScriptNativeValueKindV3::Rotator3;
        return true;
    case EUnifiedScriptValueKind::Transform:
        OutKind = EUnifiedScriptNativeValueKindV3::Transform;
        return true;
    case EUnifiedScriptValueKind::Struct:
        OutKind = EUnifiedScriptNativeValueKindV3::Struct;
        return true;
    case EUnifiedScriptValueKind::Array:
        OutKind = EUnifiedScriptNativeValueKindV3::Array;
        return true;
    case EUnifiedScriptValueKind::Set:
        OutKind = EUnifiedScriptNativeValueKindV3::Set;
        return true;
    case EUnifiedScriptValueKind::ByteBuffer:
        OutKind = EUnifiedScriptNativeValueKindV3::ByteBuffer;
        return true;
    case EUnifiedScriptValueKind::Map:
        OutKind = EUnifiedScriptNativeValueKindV3::Map;
        return true;
    default:
        return false;
    }
}

bool ValidateResultDescriptorV3(
    const FUnifiedScriptNativeValueV3& Descriptor,
    EUnifiedScriptValueKind ExpectedKind,
    uint64 ExpectedTypeId)
{
    EUnifiedScriptNativeValueKindV3 NativeKind;
    if (!ToNativeKindV3(ExpectedKind, NativeKind) || Descriptor.Kind != NativeKind ||
        Descriptor.Reserved16 != 0 || Descriptor.Auxiliary != 0)
    {
        return false;
    }
    if (ExpectedKind == EUnifiedScriptValueKind::Enum)
    {
        return ExpectedTypeId != 0 && Descriptor.TypeId == ExpectedTypeId &&
            Descriptor.Flags == EUnifiedScriptNativeValueFlagsV3::None;
    }
    if (ExpectedKind == EUnifiedScriptValueKind::Transform)
    {
        return Descriptor.TypeId == 0 &&
            Descriptor.Flags == EUnifiedScriptNativeValueFlagsV3::ArenaBacked &&
            (Descriptor.Payload1 == 0 || Descriptor.Payload1 == V3TransformByteSize);
    }
    if (ExpectedKind == EUnifiedScriptValueKind::ByteBuffer)
        return Descriptor.TypeId == 0 && Descriptor.Flags == EUnifiedScriptNativeValueFlagsV3::ArenaBacked &&
            Descriptor.Payload0 == 0 && Descriptor.Payload1 == 0 && Descriptor.Payload2 == 0 && Descriptor.Payload3 == 0;
    if (ExpectedKind == EUnifiedScriptValueKind::String ||
        ExpectedKind == EUnifiedScriptValueKind::Name ||
        ExpectedKind == EUnifiedScriptValueKind::Text)
    {
        return Descriptor.TypeId == 0 &&
            Descriptor.Flags == EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
    }
    if (ExpectedKind == EUnifiedScriptValueKind::Struct ||
        ExpectedKind == EUnifiedScriptValueKind::Array ||
        ExpectedKind == EUnifiedScriptValueKind::Set ||
        ExpectedKind == EUnifiedScriptValueKind::Map)
    {
        return ExpectedTypeId != 0 && Descriptor.TypeId == ExpectedTypeId &&
            Descriptor.Flags == EUnifiedScriptNativeValueFlagsV3::ArenaBacked &&
            (Descriptor.Payload3 == 0 ||
                Descriptor.Payload3 == sizeof(FUnifiedScriptNativeValueV3));
    }
    return Descriptor.TypeId == 0 &&
        Descriptor.Flags == EUnifiedScriptNativeValueFlagsV3::None;
}

double ReadDoubleV3(const uint8* Buffer, uint64 Offset)
{
    uint64 Bits = 0;
    FMemory::Memcpy(&Bits, Buffer + Offset, sizeof(Bits));
    return std::bit_cast<double>(Bits);
}

void WriteDoubleV3(uint8* Buffer, uint32 Offset, double Value)
{
    const uint64 Bits = std::bit_cast<uint64>(Value);
    FMemory::Memcpy(Buffer + Offset, &Bits, sizeof(Bits));
}

bool DecodeValueV3(
    const FUnifiedScriptNativeValueV3& Native,
    const FUnifiedScriptNativeCallFrameV3& Frame,
    uint32 EntryByteCount,
    FUnifiedScriptValue& OutValue,
    uint32 Depth = 0)
{
    constexpr uint32 MaxContainerDepth = 16;
    if (Native.Reserved16 != 0 || Native.Auxiliary != 0 || Depth > MaxContainerDepth)
    {
        return false;
    }
    switch (Native.Kind)
    {
    case EUnifiedScriptNativeValueKindV3::ByteBuffer:
    {
        if (Native.Flags != EUnifiedScriptNativeValueFlagsV3::ArenaBacked || Native.TypeId || Native.Payload2 || Native.Payload3 ||
            Native.Payload0 > EntryByteCount || Native.Payload1 > EntryByteCount - Native.Payload0 ||
            Native.Payload1 > FUnifiedScriptByteBuffer::MaximumLength) return false;
        const auto View = Native.Payload1 == 0 ? TConstArrayView<uint8>() :
            MakeArrayView(Frame.ByteBuffer + Native.Payload0, static_cast<int32>(Native.Payload1));
        OutValue = FUnifiedScriptValue::FromByteBuffer(FUnifiedScriptByteBuffer::CopyFrom(View));
        return true;
    }
    case EUnifiedScriptNativeValueKindV3::Boolean:
        if (Native.Flags != EUnifiedScriptNativeValueFlagsV3::None ||
            Native.TypeId != 0 || Native.Payload0 > 1)
        {
            return false;
        }
        OutValue = FUnifiedScriptValue::FromBoolean(Native.Payload0 != 0);
        return true;
    case EUnifiedScriptNativeValueKindV3::Int8:
        OutValue = FUnifiedScriptValue::FromInt8(static_cast<int8>(Native.Payload0));
        break;
    case EUnifiedScriptNativeValueKindV3::UInt8:
        OutValue = FUnifiedScriptValue::FromUInt8(static_cast<uint8>(Native.Payload0));
        break;
    case EUnifiedScriptNativeValueKindV3::Int16:
        OutValue = FUnifiedScriptValue::FromInt16(static_cast<int16>(Native.Payload0));
        break;
    case EUnifiedScriptNativeValueKindV3::UInt16:
        OutValue = FUnifiedScriptValue::FromUInt16(static_cast<uint16>(Native.Payload0));
        break;
    case EUnifiedScriptNativeValueKindV3::Int32:
        OutValue = FUnifiedScriptValue::FromInt32(static_cast<int32>(Native.Payload0));
        break;
    case EUnifiedScriptNativeValueKindV3::UInt32:
        OutValue = FUnifiedScriptValue::FromUInt32(static_cast<uint32>(Native.Payload0));
        break;
    case EUnifiedScriptNativeValueKindV3::Int64:
        OutValue = FUnifiedScriptValue::FromInt64(static_cast<int64>(Native.Payload0));
        break;
    case EUnifiedScriptNativeValueKindV3::UInt64:
        OutValue = FUnifiedScriptValue::FromUInt64(Native.Payload0);
        break;
    case EUnifiedScriptNativeValueKindV3::Float32:
        OutValue = FUnifiedScriptValue::FromFloat32(
            std::bit_cast<float>(static_cast<uint32>(Native.Payload0)));
        break;
    case EUnifiedScriptNativeValueKindV3::Float64:
        OutValue = FUnifiedScriptValue::FromFloat64(std::bit_cast<double>(Native.Payload0));
        break;
    case EUnifiedScriptNativeValueKindV3::Enum:
        if (Native.TypeId == 0)
        {
            return false;
        }
        OutValue = FUnifiedScriptValue::FromEnumTypeId(
            static_cast<int64>(Native.Payload0),
            Native.TypeId);
        return Native.Flags == EUnifiedScriptNativeValueFlagsV3::None;
    case EUnifiedScriptNativeValueKindV3::Object:
    case EUnifiedScriptNativeValueKindV3::Class:
        if (Native.Flags != EUnifiedScriptNativeValueFlagsV3::None ||
            Native.TypeId != 0 || Native.Payload1 != 0 ||
            Native.Payload2 != 0 || Native.Payload3 != 0)
        {
            return false;
        }
        OutValue = FUnifiedScriptValue::FromObjectHandle(
            {Native.Payload0},
            Native.Kind == EUnifiedScriptNativeValueKindV3::Class);
        return true;
    case EUnifiedScriptNativeValueKindV3::String:
    case EUnifiedScriptNativeValueKindV3::Name:
    case EUnifiedScriptNativeValueKindV3::Text:
    {
        if (Native.Flags != EUnifiedScriptNativeValueFlagsV3::ArenaBacked ||
            Native.TypeId != 0 || Native.Payload0 > EntryByteCount ||
            Native.Payload1 > EntryByteCount - Native.Payload0 ||
            Native.Payload1 > MAX_int32)
        {
            return false;
        }
        FString Decoded;
        const uint8* Utf8Data = Native.Payload1 == 0
            ? nullptr
            : Frame.ByteBuffer + static_cast<uint32>(Native.Payload0);
        if (!TryDecodeUtf8(
                Utf8Data,
                static_cast<int32>(Native.Payload1),
                Decoded))
        {
            return false;
        }
        if (Native.Kind == EUnifiedScriptNativeValueKindV3::String)
        {
            OutValue = FUnifiedScriptValue::FromString(MoveTemp(Decoded));
        }
        else if (Native.Kind == EUnifiedScriptNativeValueKindV3::Name)
        {
            OutValue = FUnifiedScriptValue::FromName(FName(*Decoded));
        }
        else
        {
            OutValue = FUnifiedScriptValue::FromText(FText::FromString(Decoded));
        }
        return true;
    }
    case EUnifiedScriptNativeValueKindV3::Vector3:
        OutValue = FUnifiedScriptValue::FromVector(FVector(
            std::bit_cast<double>(Native.Payload0),
            std::bit_cast<double>(Native.Payload1),
            std::bit_cast<double>(Native.Payload2)));
        break;
    case EUnifiedScriptNativeValueKindV3::Rotator3:
        OutValue = FUnifiedScriptValue::FromRotator(FRotator(
            std::bit_cast<double>(Native.Payload0),
            std::bit_cast<double>(Native.Payload1),
            std::bit_cast<double>(Native.Payload2)));
        break;
    case EUnifiedScriptNativeValueKindV3::Transform:
    {
        if (Native.Flags != EUnifiedScriptNativeValueFlagsV3::ArenaBacked ||
            Native.TypeId != 0 || Native.Payload1 != V3TransformByteSize ||
            Native.Payload0 > EntryByteCount ||
            EntryByteCount - static_cast<uint32>(Native.Payload0) < V3TransformByteSize)
        {
            return false;
        }
        const uint64 Offset = Native.Payload0;
        OutValue = FUnifiedScriptValue::FromTransform(FTransform(
            FQuat(
                ReadDoubleV3(Frame.ByteBuffer, Offset + 0 * sizeof(double)),
                ReadDoubleV3(Frame.ByteBuffer, Offset + 1 * sizeof(double)),
                ReadDoubleV3(Frame.ByteBuffer, Offset + 2 * sizeof(double)),
                ReadDoubleV3(Frame.ByteBuffer, Offset + 3 * sizeof(double))),
            FVector(
                ReadDoubleV3(Frame.ByteBuffer, Offset + 4 * sizeof(double)),
                ReadDoubleV3(Frame.ByteBuffer, Offset + 5 * sizeof(double)),
                ReadDoubleV3(Frame.ByteBuffer, Offset + 6 * sizeof(double))),
            FVector(
                ReadDoubleV3(Frame.ByteBuffer, Offset + 7 * sizeof(double)),
                ReadDoubleV3(Frame.ByteBuffer, Offset + 8 * sizeof(double)),
                ReadDoubleV3(Frame.ByteBuffer, Offset + 9 * sizeof(double)))));
        return true;
    }
    case EUnifiedScriptNativeValueKindV3::Struct:
    case EUnifiedScriptNativeValueKindV3::Array:
    case EUnifiedScriptNativeValueKindV3::Set:
    case EUnifiedScriptNativeValueKindV3::Map:
    {
        const uint64 CellsPerEntry =
            Native.Kind == EUnifiedScriptNativeValueKindV3::Map ? 2ull : 1ull;
        if (Native.Flags != EUnifiedScriptNativeValueFlagsV3::ArenaBacked ||
            Native.TypeId == 0 || Native.Payload3 != sizeof(FUnifiedScriptNativeValueV3) ||
            Native.Payload2 > MAX_int32 ||
            Native.Payload2 > MAX_uint64 / CellsPerEntry)
        {
            return false;
        }
        const uint64 CellCount = Native.Payload2 * CellsPerEntry;
        if (CellCount > MAX_uint32 / sizeof(FUnifiedScriptNativeValueV3))
        {
            return false;
        }
        const uint64 DescriptorBytes = CellCount * sizeof(FUnifiedScriptNativeValueV3);
        if (Native.Payload1 != DescriptorBytes ||
            Native.Payload0 > EntryByteCount ||
            DescriptorBytes > EntryByteCount - Native.Payload0)
        {
            return false;
        }
        TArray<FUnifiedScriptValue> Values;
        Values.Reserve(static_cast<int32>(CellCount));
        for (uint64 Index = 0; Index < CellCount; ++Index)
        {
            FUnifiedScriptNativeValueV3 Cell;
            FMemory::Memcpy(
                &Cell,
                Frame.ByteBuffer + Native.Payload0 +
                    Index * sizeof(FUnifiedScriptNativeValueV3),
                sizeof(Cell));
            FUnifiedScriptValue Decoded;
            if (!DecodeValueV3(Cell, Frame, EntryByteCount, Decoded, Depth + 1))
            {
                return false;
            }
            Values.Add(MoveTemp(Decoded));
        }
        if (Native.Kind == EUnifiedScriptNativeValueKindV3::Struct)
        {
            OutValue = FUnifiedScriptValue::FromStruct(MoveTemp(Values), Native.TypeId);
        }
        else if (Native.Kind == EUnifiedScriptNativeValueKindV3::Array)
        {
            OutValue = FUnifiedScriptValue::FromArray(MoveTemp(Values), Native.TypeId);
        }
        else if (Native.Kind == EUnifiedScriptNativeValueKindV3::Set)
        {
            OutValue = FUnifiedScriptValue::FromSet(MoveTemp(Values), Native.TypeId);
        }
        else
        {
            OutValue = FUnifiedScriptValue::FromMap(MoveTemp(Values), Native.TypeId);
        }
        return true;
    }
    default:
        return false;
    }
    return Native.Flags == EUnifiedScriptNativeValueFlagsV3::None && Native.TypeId == 0;
}

bool EncodeValueV3(
    const FUnifiedScriptValue& Value,
    FUnifiedScriptNativeCallFrameV3& Frame,
    FUnifiedScriptNativeValueV3& Native,
    uint32& ByteCursor,
    uint32 Depth = 0)
{
    if (Depth > V3MaxContainerDepth)
    {
        return false;
    }
    Native = {};
    switch (Value.Kind)
    {
    case EUnifiedScriptValueKind::ByteBuffer:
    {
        const auto View = Value.ByteBufferValue.GetView();
        const uint32 Count = static_cast<uint32>(View.Num());
        if (Count > FUnifiedScriptByteBuffer::MaximumLength || ByteCursor > Frame.ByteCapacity ||
            Count > Frame.ByteCapacity - ByteCursor) return false;
        Native.Kind = EUnifiedScriptNativeValueKindV3::ByteBuffer;
        Native.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        Native.Payload0 = ByteCursor;
        Native.Payload1 = Count;
        if (Count) FMemory::Memcpy(Frame.ByteBuffer + ByteCursor, View.GetData(), Count);
        ByteCursor += Count;
        break;
    }
    case EUnifiedScriptValueKind::Boolean:
        Native.Kind = EUnifiedScriptNativeValueKindV3::Boolean;
        Native.Payload0 = Value.BooleanValue ? 1 : 0;
        break;
    case EUnifiedScriptValueKind::Int8:
        Native.Kind = EUnifiedScriptNativeValueKindV3::Int8;
        Native.Payload0 = static_cast<uint64>(Value.Int8Value);
        break;
    case EUnifiedScriptValueKind::UInt8:
        Native.Kind = EUnifiedScriptNativeValueKindV3::UInt8;
        Native.Payload0 = Value.UInt8Value;
        break;
    case EUnifiedScriptValueKind::Int16:
        Native.Kind = EUnifiedScriptNativeValueKindV3::Int16;
        Native.Payload0 = static_cast<uint64>(Value.Int16Value);
        break;
    case EUnifiedScriptValueKind::UInt16:
        Native.Kind = EUnifiedScriptNativeValueKindV3::UInt16;
        Native.Payload0 = Value.UInt16Value;
        break;
    case EUnifiedScriptValueKind::Int32:
        Native.Kind = EUnifiedScriptNativeValueKindV3::Int32;
        Native.Payload0 = static_cast<uint64>(Value.Int32Value);
        break;
    case EUnifiedScriptValueKind::UInt32:
        Native.Kind = EUnifiedScriptNativeValueKindV3::UInt32;
        Native.Payload0 = Value.UInt32Value;
        break;
    case EUnifiedScriptValueKind::Int64:
        Native.Kind = EUnifiedScriptNativeValueKindV3::Int64;
        Native.Payload0 = static_cast<uint64>(Value.Int64Value);
        break;
    case EUnifiedScriptValueKind::UInt64:
        Native.Kind = EUnifiedScriptNativeValueKindV3::UInt64;
        Native.Payload0 = Value.UInt64Value;
        break;
    case EUnifiedScriptValueKind::Float32:
        Native.Kind = EUnifiedScriptNativeValueKindV3::Float32;
        Native.Payload0 = std::bit_cast<uint32>(Value.Float32Value);
        break;
    case EUnifiedScriptValueKind::Float64:
        Native.Kind = EUnifiedScriptNativeValueKindV3::Float64;
        Native.Payload0 = std::bit_cast<uint64>(Value.Float64Value);
        break;
    case EUnifiedScriptValueKind::Enum:
        Native.Kind = EUnifiedScriptNativeValueKindV3::Enum;
        Native.TypeId = Value.TypeIdValue;
        Native.Payload0 = static_cast<uint64>(Value.Int64Value);
        break;
    case EUnifiedScriptValueKind::String:
    case EUnifiedScriptValueKind::Name:
    case EUnifiedScriptValueKind::Text:
    {
        const FString Text = Value.Kind == EUnifiedScriptValueKind::String
            ? Value.StringValue
            : Value.Kind == EUnifiedScriptValueKind::Name
                ? Value.NameValue.ToString()
                : Value.TextValue.ToString();
        const FTCHARToUTF8 Utf8(*Text);
        const uint32 Utf8Length = static_cast<uint32>(Utf8.Length());
        if (ByteCursor > Frame.ByteCapacity ||
            Utf8Length > Frame.ByteCapacity - ByteCursor)
        {
            return false;
        }
        Native.Kind = Value.Kind == EUnifiedScriptValueKind::String
            ? EUnifiedScriptNativeValueKindV3::String
            : Value.Kind == EUnifiedScriptValueKind::Name
                ? EUnifiedScriptNativeValueKindV3::Name
                : EUnifiedScriptNativeValueKindV3::Text;
        Native.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        Native.Payload0 = ByteCursor;
        Native.Payload1 = Utf8Length;
        if (Utf8Length > 0)
        {
            FMemory::Memcpy(Frame.ByteBuffer + ByteCursor, Utf8.Get(), Utf8Length);
        }
        ByteCursor += Utf8Length;
        break;
    }
    case EUnifiedScriptValueKind::Object:
        Native.Kind = EUnifiedScriptNativeValueKindV3::Object;
        Native.Payload0 = Value.ObjectHandleValue.Value;
        break;
    case EUnifiedScriptValueKind::Class:
        Native.Kind = EUnifiedScriptNativeValueKindV3::Class;
        Native.Payload0 = Value.ObjectHandleValue.Value;
        break;
    case EUnifiedScriptValueKind::Vector:
        Native.Kind = EUnifiedScriptNativeValueKindV3::Vector3;
        Native.Payload0 = std::bit_cast<uint64>(Value.VectorValue.X);
        Native.Payload1 = std::bit_cast<uint64>(Value.VectorValue.Y);
        Native.Payload2 = std::bit_cast<uint64>(Value.VectorValue.Z);
        break;
    case EUnifiedScriptValueKind::Rotator:
        Native.Kind = EUnifiedScriptNativeValueKindV3::Rotator3;
        Native.Payload0 = std::bit_cast<uint64>(Value.RotatorValue.Pitch);
        Native.Payload1 = std::bit_cast<uint64>(Value.RotatorValue.Yaw);
        Native.Payload2 = std::bit_cast<uint64>(Value.RotatorValue.Roll);
        break;
    case EUnifiedScriptValueKind::Transform:
    {
        if (ByteCursor > Frame.ByteCapacity ||
            V3TransformByteSize > Frame.ByteCapacity - ByteCursor)
        {
            return false;
        }
        Native.Kind = EUnifiedScriptNativeValueKindV3::Transform;
        Native.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        Native.Payload0 = ByteCursor;
        Native.Payload1 = V3TransformByteSize;
        const FQuat Rotation = Value.TransformValue.GetRotation();
        const FVector Translation = Value.TransformValue.GetTranslation();
        const FVector Scale = Value.TransformValue.GetScale3D();
        WriteDoubleV3(Frame.ByteBuffer, ByteCursor + 0 * sizeof(double), Rotation.X);
        WriteDoubleV3(Frame.ByteBuffer, ByteCursor + 1 * sizeof(double), Rotation.Y);
        WriteDoubleV3(Frame.ByteBuffer, ByteCursor + 2 * sizeof(double), Rotation.Z);
        WriteDoubleV3(Frame.ByteBuffer, ByteCursor + 3 * sizeof(double), Rotation.W);
        WriteDoubleV3(Frame.ByteBuffer, ByteCursor + 4 * sizeof(double), Translation.X);
        WriteDoubleV3(Frame.ByteBuffer, ByteCursor + 5 * sizeof(double), Translation.Y);
        WriteDoubleV3(Frame.ByteBuffer, ByteCursor + 6 * sizeof(double), Translation.Z);
        WriteDoubleV3(Frame.ByteBuffer, ByteCursor + 7 * sizeof(double), Scale.X);
        WriteDoubleV3(Frame.ByteBuffer, ByteCursor + 8 * sizeof(double), Scale.Y);
        WriteDoubleV3(Frame.ByteBuffer, ByteCursor + 9 * sizeof(double), Scale.Z);
        ByteCursor += V3TransformByteSize;
        break;
    }
    case EUnifiedScriptValueKind::Struct:
    case EUnifiedScriptValueKind::Array:
    case EUnifiedScriptValueKind::Set:
    case EUnifiedScriptValueKind::Map:
    {
        if (Value.TypeIdValue == 0 ||
            (Value.Kind == EUnifiedScriptValueKind::Map &&
                Value.ContainerValues.Num() % 2 != 0))
        {
            return false;
        }
        const uint64 DescriptorByteCount =
            static_cast<uint64>(Value.ContainerValues.Num()) *
            sizeof(FUnifiedScriptNativeValueV3);
        if (ByteCursor > Frame.ByteCapacity ||
            DescriptorByteCount > Frame.ByteCapacity - ByteCursor)
        {
            return false;
        }
        const uint32 DescriptorOffset = ByteCursor;
        ByteCursor += static_cast<uint32>(DescriptorByteCount);
        for (int32 Index = 0; Index < Value.ContainerValues.Num(); ++Index)
        {
            FUnifiedScriptNativeValueV3 Element;
            if (!EncodeValueV3(
                    Value.ContainerValues[Index],
                    Frame,
                    Element,
                    ByteCursor,
                    Depth + 1))
            {
                return false;
            }
            FMemory::Memcpy(
                Frame.ByteBuffer + DescriptorOffset +
                    Index * sizeof(FUnifiedScriptNativeValueV3),
                &Element,
                sizeof(Element));
        }
        Native.Kind = Value.Kind == EUnifiedScriptValueKind::Struct
            ? EUnifiedScriptNativeValueKindV3::Struct
            : Value.Kind == EUnifiedScriptValueKind::Array
                ? EUnifiedScriptNativeValueKindV3::Array
                : Value.Kind == EUnifiedScriptValueKind::Set
                    ? EUnifiedScriptNativeValueKindV3::Set
                    : EUnifiedScriptNativeValueKindV3::Map;
        Native.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        Native.TypeId = Value.TypeIdValue;
        Native.Payload0 = DescriptorOffset;
        Native.Payload1 = DescriptorByteCount;
        Native.Payload2 = Value.Kind == EUnifiedScriptValueKind::Map
            ? Value.ContainerValues.Num() / 2
            : Value.ContainerValues.Num();
        Native.Payload3 = sizeof(FUnifiedScriptNativeValueV3);
        break;
    }
    default:
        return false;
    }
    return true;
}

bool PreflightResultV3(
    FUnifiedScriptNativeCallFrameV3& Frame,
    uint32 EntryByteCount,
    EUnifiedScriptValueKind ExpectedKind,
    uint64 ExpectedTypeId)
{
    const uint32 ExpectedResultCount =
        ExpectedKind == EUnifiedScriptValueKind::Void ? 0u : 1u;
    if (Frame.ResultCount != ExpectedResultCount ||
        Frame.ResultCapacity < ExpectedResultCount ||
        (ExpectedResultCount > 0 && Frame.Results == nullptr))
    {
        InvalidFrameV3(&Frame, EntryByteCount);
        return false;
    }
    if (ExpectedResultCount == 0)
    {
        return true;
    }
    if (!ValidateResultDescriptorV3(Frame.Results[0], ExpectedKind, ExpectedTypeId))
    {
        InvalidFrameV3(&Frame, EntryByteCount);
        return false;
    }
    const uint32 RequiredBytes = EntryByteCount +
        (ExpectedKind == EUnifiedScriptValueKind::Transform ? V3TransformByteSize : 0u);
    if (RequiredBytes > Frame.ByteCapacity)
    {
        ResetV3Output(Frame, EntryByteCount);
        Frame.RequiredResultCapacity = ExpectedResultCount;
        Frame.RequiredByteCapacity = RequiredBytes;
        Frame.Status = EUnifiedScriptNativeCallStatusV3::BufferTooSmall;
        return false;
    }
    return true;
}

void ReadPropertyByIdV3(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    FUnifiedScriptNativeCallFrameV3* Frame)
{
    uint32 EntryByteCount = Frame != nullptr ? Frame->ByteCount : 0;
    if (!ValidateFrameV3(Frame, 0, 1, EntryByteCount))
    {
        return;
    }
    EUnifiedScriptValueKind ExpectedKind;
    uint64 ExpectedTypeId = 0;
    if (!FUnifiedScriptGeneratedThunkRegistry::DescribeReadResult(
            MemberId, ExpectedKind, ExpectedTypeId))
    {
        FUnifiedScriptInvocationResult Result;
        Result.Error = EUnifiedScriptInvocationError::InvalidMemberPath;
        InvocationFailureV3(*Frame, EntryByteCount, Result);
        return;
    }
    EUnifiedScriptNativeValueKindV3 SupportedKind;
    if (!ToNativeKindV3(ExpectedKind, SupportedKind))
    {
        UnsupportedValueV3(*Frame, EntryByteCount);
        return;
    }
    if (!PreflightResultV3(*Frame, EntryByteCount, ExpectedKind, ExpectedTypeId))
    {
        return;
    }
    const bool bExecuted = Context != nullptr &&
        static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->Execute(
            [&](FUnifiedScriptInvocationBridge& Bridge)
            {
                FUnifiedScriptValue Value;
                FUnifiedScriptInvocationResult Result;
                if (!Bridge.ReadPropertyById({Target}, MemberId, Value, Result))
                {
                    InvocationFailureV3(*Frame, EntryByteCount, Result);
                    return;
                }
                uint32 RequiredByteCapacity = EntryByteCount;
                if (!MeasureValueV3(Value, RequiredByteCapacity))
                {
                    UnsupportedValueV3(*Frame, EntryByteCount);
                    return;
                }
                if (RequiredByteCapacity > Frame->ByteCapacity)
                {
                    ResetV3Output(*Frame, EntryByteCount);
                    Frame->RequiredResultCapacity = 1;
                    Frame->RequiredByteCapacity = RequiredByteCapacity;
                    Frame->Status = EUnifiedScriptNativeCallStatusV3::BufferTooSmall;
                    return;
                }
                ResetV3Output(*Frame, EntryByteCount);
                uint32 ByteCursor = EntryByteCount;
                if (!EncodeValueV3(Value, *Frame, Frame->Results[0], ByteCursor))
                {
                    UnsupportedValueV3(*Frame, EntryByteCount);
                    return;
                }
                Frame->ResultCount = 1;
                Frame->ByteCount = ByteCursor;
                Frame->Status = EUnifiedScriptNativeCallStatusV3::Success;
            });
    if (!bExecuted)
    {
        ResetV3Output(*Frame, EntryByteCount);
        Frame->Status = EUnifiedScriptNativeCallStatusV3::TransportUnavailable;
    }
}

void WritePropertyByIdV3(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    FUnifiedScriptNativeCallFrameV3* Frame)
{
    uint32 EntryByteCount = Frame != nullptr ? Frame->ByteCount : 0;
    if (!ValidateFrameV3(Frame, 1, 0, EntryByteCount))
    {
        return;
    }
    FUnifiedScriptValue Value;
    if (!DecodeValueV3(Frame->Arguments[0], *Frame, EntryByteCount, Value))
    {
        InvalidFrameV3(Frame, EntryByteCount);
        return;
    }
    const bool bExecuted = Context != nullptr &&
        static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->Execute(
            [&](FUnifiedScriptInvocationBridge& Bridge)
            {
                FUnifiedScriptInvocationResult Result;
                if (!Bridge.WritePropertyById({Target}, MemberId, Value, Result))
                {
                    InvocationFailureV3(*Frame, EntryByteCount, Result);
                    return;
                }
                ResetV3Output(*Frame, EntryByteCount);
                Frame->Status = EUnifiedScriptNativeCallStatusV3::Success;
            });
    if (!bExecuted)
    {
        ResetV3Output(*Frame, EntryByteCount);
        Frame->Status = EUnifiedScriptNativeCallStatusV3::TransportUnavailable;
    }
}

void InvokeByIdV3(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    FUnifiedScriptNativeCallFrameV3* Frame)
{
    uint32 EntryByteCount = Frame != nullptr ? Frame->ByteCount : 0;
    if (Frame == nullptr || Frame->ArgumentCount > 64)
    {
        InvalidFrameV3(Frame, EntryByteCount);
        return;
    }
    uint32 ExpectedResultCount = 0;
    if (!FUnifiedScriptGeneratedThunkRegistry::DescribeInvokeResultCount(
            MemberId, ExpectedResultCount) ||
        ExpectedResultCount > 64)
    {
        FUnifiedScriptInvocationResult Result;
        Result.Error = EUnifiedScriptInvocationError::InvalidMemberPath;
        InvocationFailureV3(*Frame, EntryByteCount, Result);
        return;
    }
    if (!ValidateFrameV3(
            Frame,
            Frame->ArgumentCount,
            ExpectedResultCount,
            EntryByteCount))
    {
        return;
    }
    uint32 RequiredByteCapacity = EntryByteCount;
    bool bRequiresRequestHash = false;
    bool bHasBinaryPayload = false;
    uint64 BinaryInputBytes = 0;
    for (uint32 Index = 0; Index < Frame->ArgumentCount; ++Index)
    {
        const auto& Argument = Frame->Arguments[Index];
        if (Argument.Kind != EUnifiedScriptNativeValueKindV3::ByteBuffer) continue;
        bHasBinaryPayload = true;
        // Check before decoding: repeated offsets must not amplify allocation.
        if (Argument.Payload1 > FUnifiedScriptByteBuffer::MaximumLength ||
            EntryByteCount > FUnifiedScriptByteBuffer::MaximumCallBytes ||
            BinaryInputBytes + Argument.Payload1 > FUnifiedScriptByteBuffer::MaximumCallBytes)
        {
            InvalidFrameV3(Frame, EntryByteCount);
            return;
        }
        BinaryInputBytes += Argument.Payload1;
    }
    for (uint32 ResultIndex = 0; ResultIndex < ExpectedResultCount; ++ResultIndex)
    {
        EUnifiedScriptValueKind ExpectedKind;
        uint64 ExpectedTypeId = 0;
        if (!FUnifiedScriptGeneratedThunkRegistry::DescribeInvokeResult(
                MemberId, ResultIndex, ExpectedKind, ExpectedTypeId))
        {
            InvalidFrameV3(Frame, EntryByteCount);
            return;
        }
        EUnifiedScriptNativeValueKindV3 SupportedKind;
        if (!ToNativeKindV3(ExpectedKind, SupportedKind))
        {
            UnsupportedValueV3(*Frame, EntryByteCount);
            return;
        }
        if (!ValidateResultDescriptorV3(
                Frame->Results[ResultIndex], ExpectedKind, ExpectedTypeId))
        {
            InvalidFrameV3(Frame, EntryByteCount);
            return;
        }
        bHasBinaryPayload |= ExpectedKind == EUnifiedScriptValueKind::ByteBuffer;
        if (ExpectedKind == EUnifiedScriptValueKind::Transform)
        {
            if (RequiredByteCapacity > MAX_uint32 - V3TransformByteSize)
            {
                InvalidFrameV3(Frame, EntryByteCount);
                return;
            }
            RequiredByteCapacity += V3TransformByteSize;
        }
        else if (
            ExpectedKind == EUnifiedScriptValueKind::Name ||
            ExpectedKind == EUnifiedScriptValueKind::String ||
            ExpectedKind == EUnifiedScriptValueKind::Text ||
            ExpectedKind == EUnifiedScriptValueKind::Struct ||
            ExpectedKind == EUnifiedScriptValueKind::Array ||
            ExpectedKind == EUnifiedScriptValueKind::Set ||
            ExpectedKind == EUnifiedScriptValueKind::Map ||
            ExpectedKind == EUnifiedScriptValueKind::ByteBuffer)
        {
            bRequiresRequestHash = true;
        }
    }
    if (RequiredByteCapacity > Frame->ByteCapacity)
    {
        ResetV3Output(*Frame, EntryByteCount);
        Frame->RequiredResultCapacity = ExpectedResultCount;
        Frame->RequiredByteCapacity = RequiredByteCapacity;
        Frame->Status = EUnifiedScriptNativeCallStatusV3::BufferTooSmall;
        return;
    }
    TArray<FUnifiedScriptValue, TInlineAllocator<8>> Arguments;
    Arguments.Reserve(Frame->ArgumentCount);
    for (uint32 Index = 0; Index < Frame->ArgumentCount; ++Index)
    {
        FUnifiedScriptValue Value;
        if (!DecodeValueV3(Frame->Arguments[Index], *Frame, EntryByteCount, Value))
        {
            InvalidFrameV3(Frame, EntryByteCount);
            return;
        }
        Arguments.Add(MoveTemp(Value));
    }
    const uint64 RequestHash = bRequiresRequestHash
        ? HashRequestV3(*Frame, EntryByteCount)
        : 0;
    const bool bExecuted = Context != nullptr &&
        static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->ExecuteCallFrame(
            [&](FUnifiedScriptInvocationBridge& Bridge,
                FUnifiedScriptNativeBridgeContext::FPendingCallFrameResult& Pending)
            {
                FUnifiedScriptValue ReturnValue;
                TArray<FUnifiedScriptValue> OutputValues;
                FUnifiedScriptInvocationResult Result;
                const bool bUsePending = Pending.bValid && Pending.Operation == 2 &&
                    Pending.Target == Target && Pending.MemberId == MemberId &&
                    Pending.RequestHash == RequestHash;
                if (bUsePending)
                {
                    ReturnValue = Pending.ReturnValue;
                    OutputValues = Pending.OutputValues;
                }
                else
                {
                    Pending.Reset();
                    if (!Bridge.InvokeById(
                            {Target},
                            MemberId,
                            Arguments,
                            ReturnValue,
                            OutputValues,
                            Result))
                    {
                        InvocationFailureV3(*Frame, EntryByteCount, Result);
                        return;
                    }
                }
                const uint32 ProducedResultCount =
                    (ReturnValue.Kind == EUnifiedScriptValueKind::Void ? 0u : 1u) +
                    static_cast<uint32>(OutputValues.Num());
                if (ProducedResultCount != ExpectedResultCount)
                {
                    Pending.Reset();
                    Result.Error = EUnifiedScriptInvocationError::UnsupportedParameter;
                    InvocationFailureV3(*Frame, EntryByteCount, Result);
                    return;
                }
                uint32 MeasuredByteCapacity = EntryByteCount;
                bool bMeasured = ReturnValue.Kind == EUnifiedScriptValueKind::Void ||
                    MeasureValueV3(ReturnValue, MeasuredByteCapacity);
                for (const FUnifiedScriptValue& OutputValue : OutputValues)
                {
                    bMeasured = bMeasured &&
                        MeasureValueV3(OutputValue, MeasuredByteCapacity);
                }
                if (!bMeasured || (bHasBinaryPayload && MeasuredByteCapacity > FUnifiedScriptByteBuffer::MaximumCallBytes))
                {
                    Pending.Reset();
                    UnsupportedValueV3(*Frame, EntryByteCount);
                    return;
                }
                if (MeasuredByteCapacity > Frame->ByteCapacity)
                {
                    Pending.bValid = true;
                    Pending.Operation = 2;
                    Pending.Target = Target;
                    Pending.MemberId = MemberId;
                    Pending.RequestHash = RequestHash;
                    Pending.ReturnValue = ReturnValue;
                    Pending.OutputValues = OutputValues;
                    ResetV3Output(*Frame, EntryByteCount);
                    Frame->RequiredResultCapacity = ExpectedResultCount;
                    Frame->RequiredByteCapacity = MeasuredByteCapacity;
                    Frame->Status = EUnifiedScriptNativeCallStatusV3::BufferTooSmall;
                    return;
                }
                Pending.Reset();
                ResetV3Output(*Frame, EntryByteCount);
                uint32 ResultIndex = 0;
                uint32 ByteCursor = EntryByteCount;
                if (ReturnValue.Kind != EUnifiedScriptValueKind::Void &&
                    !EncodeValueV3(
                        ReturnValue,
                        *Frame,
                        Frame->Results[ResultIndex++],
                        ByteCursor))
                {
                    UnsupportedValueV3(*Frame, EntryByteCount);
                    return;
                }
                for (const FUnifiedScriptValue& OutputValue : OutputValues)
                {
                    if (!EncodeValueV3(
                            OutputValue,
                            *Frame,
                            Frame->Results[ResultIndex++],
                            ByteCursor))
                    {
                        UnsupportedValueV3(*Frame, EntryByteCount);
                        return;
                    }
                }
                Frame->ResultCount = ResultIndex;
                Frame->ByteCount = ByteCursor;
                Frame->Status = EUnifiedScriptNativeCallStatusV3::Success;
            });
    if (!bExecuted)
    {
        ResetV3Output(*Frame, EntryByteCount);
        Frame->Status = EUnifiedScriptNativeCallStatusV3::TransportUnavailable;
    }
}

uint8 InvokeInt32x2ByIdV3(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    int32 Argument0,
    int32 Argument1,
    int32* OutReturnValue,
    uint8* OutInvocationError,
    uint8* OutHandleError)
{
    if (OutReturnValue == nullptr || OutInvocationError == nullptr ||
        OutHandleError == nullptr)
    {
        return static_cast<uint8>(EUnifiedScriptNativeCallStatusV3::InvalidFrame);
    }

    *OutReturnValue = 0;
    *OutInvocationError = 0;
    *OutHandleError = 0;
    EUnifiedScriptNativeCallStatusV3 Status =
        EUnifiedScriptNativeCallStatusV3::TransportUnavailable;
    const bool bExecuted = Context != nullptr &&
        static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->ExecuteGameThreadFast(
            [&](FUnifiedScriptInvocationBridge& Bridge)
            {
                FUnifiedScriptInvocationResult Result;
                if (!Bridge.InvokeInt32x2ById(
                        {Target},
                        MemberId,
                        Argument0,
                        Argument1,
                        *OutReturnValue,
                        Result))
                {
                    *OutInvocationError = static_cast<uint8>(Result.Error);
                    *OutHandleError = static_cast<uint8>(Result.HandleError);
                    Status = EUnifiedScriptNativeCallStatusV3::InvocationFailure;
                    return;
                }
                Status = EUnifiedScriptNativeCallStatusV3::Success;
            });
    return static_cast<uint8>(
        bExecuted ? Status : EUnifiedScriptNativeCallStatusV3::TransportUnavailable);
}

uint8 InvokeFixedByIdV3(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    const FUnifiedScriptNativeValueV3* Arguments,
    uint32 ArgumentCount,
    FUnifiedScriptNativeValueV3* OutReturnValue,
    uint8* OutInvocationError,
    uint8* OutHandleError)
{
    constexpr uint32 MaximumArgumentCount = 4;
    if (OutInvocationError == nullptr || OutHandleError == nullptr ||
        ArgumentCount > MaximumArgumentCount ||
        (ArgumentCount > 0 && Arguments == nullptr))
    {
        return static_cast<uint8>(EUnifiedScriptNativeCallStatusV3::InvalidFrame);
    }

    *OutInvocationError = 0;
    *OutHandleError = 0;
    EUnifiedScriptNativeCallStatusV3 Status =
        EUnifiedScriptNativeCallStatusV3::TransportUnavailable;
    const bool bExecuted = Context != nullptr &&
        static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->ExecuteGameThreadFast(
            [&](FUnifiedScriptInvocationBridge& Bridge)
            {
                FUnifiedScriptInvocationResult Result;
                if (!Bridge.InvokeFixedById(
                        {Target},
                        MemberId,
                        MakeArrayView(Arguments, static_cast<int32>(ArgumentCount)),
                        OutReturnValue,
                        Result))
                {
                    *OutInvocationError = static_cast<uint8>(Result.Error);
                    *OutHandleError = static_cast<uint8>(Result.HandleError);
                    Status = EUnifiedScriptNativeCallStatusV3::InvocationFailure;
                    return;
                }
                Status = EUnifiedScriptNativeCallStatusV3::Success;
            });
    return static_cast<uint8>(
        bExecuted ? Status : EUnifiedScriptNativeCallStatusV3::TransportUnavailable);
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

uint8 AcquireTransformSnapshot(
    void* Context,
    FUnifiedScriptSharedSnapshotViewV4* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->AcquireTransformSnapshot(*OutView)
        ? 1
        : 0;
}

uint8 AcquireGeneratedSnapshot(
    void* Context,
    uint64 SchemaId,
    uint32 EntrySize,
    FUnifiedScriptGeneratedDataViewV1* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->AcquireGeneratedSnapshot(SchemaId, EntrySize, *OutView)
        ? 1
        : 0;
}

uint8 GetSharedDataTelemetry(
    void* Context,
    FUnifiedScriptSharedDataTelemetryV1* OutTelemetry,
    uint32 OutTelemetrySize)
{
    if (Context == nullptr || OutTelemetry == nullptr ||
        OutTelemetrySize < sizeof(uint32) * 2)
    {
        return 0;
    }
    FUnifiedScriptSharedDataTelemetryV1 Current;
    if (!static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
             ->GetSharedDataTelemetry(Current))
    {
        return 0;
    }
    FMemory::Memcpy(
        OutTelemetry,
        &Current,
        FMath::Min<uint32>(OutTelemetrySize, sizeof(Current)));
    return 1;
}

uint8 BeginCommandBatch(
    void* Context,
    uint32 Capacity,
    FUnifiedScriptCommandBatchViewV4* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FUnifiedScriptGameplayDataPlane& DataPlane)
        {
            return DataPlane.BeginCommandBatch(Capacity, *OutView);
        }) ? 1 : 0;
}

uint8 CommitCommandBatch(void* Context, uint64 BatchToken, uint32 CommandCount)
{
    if (Context == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FUnifiedScriptGameplayDataPlane& DataPlane)
        {
            return DataPlane.CommitCommandBatch(BatchToken, CommandCount);
        }) ? 1 : 0;
}

uint8 AcquireGameplayEvents(
    void* Context,
    FUnifiedScriptGameplayEventViewV4* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FUnifiedScriptGameplayDataPlane& DataPlane)
        {
            return DataPlane.AcquireEvents(*OutView);
        }) ? 1 : 0;
}

uint8 AcquireGeneratedEvents(
    void* Context,
    uint64 SchemaId,
    uint32 EntrySize,
    FUnifiedScriptGeneratedDataViewV1* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FUnifiedScriptGameplayDataPlane& DataPlane)
        {
            return DataPlane.AcquireGeneratedEvents(SchemaId, EntrySize, *OutView);
        }) ? 1 : 0;
}

uint8 BeginTraceBatch(
    void* Context,
    uint32 Capacity,
    FUnifiedScriptTraceRequestViewV4* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FUnifiedScriptGameplayDataPlane& DataPlane)
        {
            return DataPlane.BeginTraceBatch(Capacity, *OutView);
        }) ? 1 : 0;
}

uint8 ExecuteTraceBatch(
    void* Context,
    uint64 BatchToken,
    uint32 RequestCount,
    FUnifiedScriptTraceResultViewV4* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FUnifiedScriptGameplayDataPlane& DataPlane)
        {
            return DataPlane.ExecuteTraceBatch(BatchToken, RequestCount, *OutView);
        }) ? 1 : 0;
}

void CancelTraceBatch(void* Context, uint64 BatchToken)
{
    if (Context == nullptr)
    {
        return;
    }
    static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FUnifiedScriptGameplayDataPlane& DataPlane)
        {
            DataPlane.CancelTraceBatch(BatchToken);
            return true;
        });
}

uint8 BeginOverlapBatch(
    void* Context,
    uint32 Capacity,
    FUnifiedScriptOverlapRequestViewV1* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FUnifiedScriptGameplayDataPlane& DataPlane)
        {
            return DataPlane.BeginOverlapBatch(Capacity, *OutView);
        }) ? 1 : 0;
}

uint8 ExecuteOverlapBatch(
    void* Context,
    uint64 BatchToken,
    uint32 RequestCount,
    FUnifiedScriptOverlapResultViewV1* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FUnifiedScriptGameplayDataPlane& DataPlane)
        {
            return DataPlane.ExecuteOverlapBatch(
                BatchToken,
                RequestCount,
                *OutView);
        }) ? 1 : 0;
}

void CancelOverlapBatch(void* Context, uint64 BatchToken)
{
    if (Context == nullptr)
    {
        return;
    }
    static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FUnifiedScriptGameplayDataPlane& DataPlane)
        {
            DataPlane.CancelOverlapBatch(BatchToken);
            return true;
        });
}

uint8 GetGameplayPhase(void* Context, uint8* OutPhase, uint64* OutFrameNumber)
{
    if (Context == nullptr || OutPhase == nullptr || OutFrameNumber == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FUnifiedScriptGameplayDataPlane& DataPlane)
        {
            *OutPhase = static_cast<uint8>(DataPlane.GetPhase());
            *OutFrameNumber = DataPlane.GetFrameNumber();
            return true;
        }) ? 1 : 0;
}

uint8 GetGameplayDataTelemetry(
    void* Context,
    FUnifiedScriptGameplayDataTelemetryV1* OutTelemetry,
    uint32 OutTelemetrySize)
{
    if (Context == nullptr || OutTelemetry == nullptr ||
        OutTelemetrySize < sizeof(uint32) * 2)
    {
        return 0;
    }
    FUnifiedScriptGameplayDataTelemetryV1 Current;
    const bool bRead = static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FUnifiedScriptGameplayDataPlane& DataPlane)
        {
            return DataPlane.GetTelemetry(Current);
        });
    if (!bRead)
    {
        return 0;
    }
    FMemory::Memcpy(
        OutTelemetry,
        &Current,
        FMath::Min<uint32>(OutTelemetrySize, sizeof(Current)));
    return 1;
}

uint8 SubmitAsyncRequest(
    void* Context,
    const FUnifiedScriptAsyncRequestV5* Request)
{
    if (Context == nullptr || Request == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteAsyncService([&](FUnifiedScriptAsyncService& Service)
        {
            return Service.Submit(*Request);
        }) ? 1 : 0;
}

uint8 PumpAsyncOwner(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    float DeltaSeconds)
{
    if (Context == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteAsyncService([&](FUnifiedScriptAsyncService& Service)
        {
            return Service.PumpOwner(
                OwnerInstanceId,
                OwnerGeneration,
                DeltaSeconds);
        }) ? 1 : 0;
}

uint8 CancelAsyncRequest(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    uint64 RequestId)
{
    if (Context == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteAsyncService([&](FUnifiedScriptAsyncService& Service)
        {
            return Service.Cancel(OwnerInstanceId, OwnerGeneration, RequestId);
        }) ? 1 : 0;
}

uint8 PollAsyncCompletions(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    FUnifiedScriptAsyncCompletionViewV5* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
        ->ExecuteAsyncService([&](FUnifiedScriptAsyncService& Service)
        {
            return Service.Poll(OwnerInstanceId, OwnerGeneration, *OutView);
        }) ? 1 : 0;
}

uint32 CancelAsyncOwner(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration)
{
    uint32 Canceled = 0;
    if (Context != nullptr)
    {
        static_cast<FUnifiedScriptNativeBridgeContext*>(Context)
            ->ExecuteAsyncService([&](FUnifiedScriptAsyncService& Service)
            {
                Canceled = Service.CancelOwner(OwnerInstanceId, OwnerGeneration);
                return true;
            });
    }
    return Canceled;
}
}

UFunction* UUnifiedScriptDelegateProxy::FindOrCreateDispatchFunction(
    const UFunction* Signature)
{
    check(IsInGameThread());
    if (Signature == nullptr || Signature->GetReturnProperty() != nullptr)
    {
        return nullptr;
    }

    const uint64 SignatureId = UnifiedScriptMemberId(
        TCHAR_TO_UTF8(*Signature->GetPathName()));
    const FName FunctionName(*FString::Printf(
        TEXT("UnifiedScriptEvent_%016llX"),
        static_cast<unsigned long long>(SignatureId)));
    UClass* ProxyClass = StaticClass();
    if (UFunction* Existing = ProxyClass->FindFunctionByName(
            FunctionName,
            EIncludeSuperFlag::ExcludeSuper))
    {
        return Existing->IsSignatureCompatibleWith(Signature) ? Existing : nullptr;
    }

    UFunction* Function = NewObject<UFunction>(
        ProxyClass,
        FunctionName,
        RF_Public | RF_Transient);
    Function->FunctionFlags = FUNC_Native | FUNC_Public;
    FField::FLinkedListBuilder PropertyListBuilder(&Function->ChildProperties);
    for (TFieldIterator<FProperty> It(Signature, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const FProperty* Parameter = *It;
        if (!Parameter->HasAnyPropertyFlags(CPF_Parm))
        {
            continue;
        }
        FProperty* Clone = CastFieldChecked<FProperty>(
            FField::Duplicate(Parameter, Function, Parameter->GetFName()));
        Clone->Next = nullptr;
        PropertyListBuilder.AppendNoTerminate(*Clone);
    }
    Function->StaticLink(true);
    Function->SetNativeFunc(&UUnifiedScriptDelegateProxy::execDispatch);
    ProxyClass->AddFunctionToFunctionMap(Function, FunctionName);
    return Function->IsSignatureCompatibleWith(Signature) ? Function : nullptr;
}

bool UUnifiedScriptDelegateProxy::Bind(
    FUnifiedScriptNativeBridgeContext* InContext,
    uint64 InSubscriptionId,
    FUnifiedScriptObjectHandle InSourceHandle,
    uint64 InEventId,
    UObject* InSource,
    const FMulticastDelegateProperty* InProperty,
    FUnifiedScriptNativeEventCallbackV4 InCallback,
    void* InUserData)
{
    check(IsInGameThread());
    if (Context != nullptr || InContext == nullptr || InSubscriptionId == 0 ||
        InSourceHandle.IsNull() || InEventId == 0 || InSource == nullptr ||
        InProperty == nullptr || InProperty->SignatureFunction == nullptr ||
        InCallback == nullptr)
    {
        return false;
    }

    UFunction* Function = FindOrCreateDispatchFunction(InProperty->SignatureFunction);
    if (Function == nullptr)
    {
        return false;
    }

    FScriptDelegate Delegate;
    Delegate.BindUFunction(this, Function->GetFName());
    InProperty->AddDelegate(Delegate, InSource);

    Context = InContext;
    SubscriptionId = InSubscriptionId;
    SourceHandle = InSourceHandle;
    EventId = InEventId;
    Source = InSource;
    Property = InProperty;
    BoundDelegate = MoveTemp(Delegate);
    Callback = InCallback;
    UserData = InUserData;
    return true;
}

void UUnifiedScriptDelegateProxy::Unbind()
{
    check(IsInGameThread());
    if (Property != nullptr)
    {
        if (UObject* SourceObject = Source.Get())
        {
            Property->RemoveDelegate(BoundDelegate, SourceObject);
        }
    }
    Context = nullptr;
    SubscriptionId = 0;
    SourceHandle = {};
    EventId = 0;
    Source.Reset();
    Property = nullptr;
    BoundDelegate.Unbind();
    Callback = nullptr;
    UserData = nullptr;
}

DEFINE_FUNCTION(UUnifiedScriptDelegateProxy::execDispatch)
{
    UUnifiedScriptDelegateProxy* Proxy = CastChecked<UUnifiedScriptDelegateProxy>(Context);
    Proxy->Dispatch(Stack.Node, Stack.Locals);
}

void UUnifiedScriptDelegateProxy::Dispatch(
    const UFunction* DispatchFunction,
    const void* ParameterMemory)
{
    using namespace UE::UnifiedScript::NativeTransport::Private;
    check(IsInGameThread());
    FUnifiedScriptNativeBridgeContext* EventContext = Context;
    FUnifiedScriptNativeEventCallbackV4 EventCallback = Callback;
    void* EventUserData = UserData;
    const uint64 LocalSubscriptionId = SubscriptionId;
    const FUnifiedScriptObjectHandle LocalSourceHandle = SourceHandle;
    const uint64 LocalEventId = EventId;
    if (EventContext == nullptr || EventCallback == nullptr ||
        DispatchFunction == nullptr || ParameterMemory == nullptr ||
        !EventContext->TryRetain())
    {
        return;
    }
    ON_SCOPE_EXIT
    {
        EventContext->Release();
    };

    TArray<FUnifiedScriptValue> Arguments;
    FUnifiedScriptInvocationResult Result;
    const bool bRead = EventContext->Execute(
        [&](FUnifiedScriptInvocationBridge& Bridge)
        {
            Bridge.ReadDelegateArguments(
                DispatchFunction,
                ParameterMemory,
                Arguments,
                Result);
        });
    if (!bRead || !Result.IsSuccess())
    {
        UE_LOG(
            LogTemp,
            Error,
            TEXT("UnifiedScript event argument marshalling failed: %s"),
            *Result.Message);
        return;
    }

    ON_SCOPE_EXIT
    {
        EventContext->Execute(
            [&](FUnifiedScriptInvocationBridge& Bridge)
            {
                Bridge.ReleaseEventArguments(Arguments);
            });
    };

    TArray<FUnifiedScriptNativeValueV3> NativeArguments;
    NativeArguments.SetNum(Arguments.Num());
    TArray<uint8> ByteBuffer;
    bool bEncoded = false;
    uint32 ByteCount = 0;
    constexpr uint32 MaximumByteCapacity = 16u * 1024u * 1024u;
    for (uint32 Capacity = 4096; Capacity <= MaximumByteCapacity; Capacity *= 2)
    {
        ByteBuffer.SetNumUninitialized(static_cast<int32>(Capacity));
        FUnifiedScriptNativeCallFrameV3 EncodingFrame;
        EncodingFrame.ByteBuffer = ByteBuffer.GetData();
        EncodingFrame.ByteCapacity = Capacity;
        ByteCount = 0;
        bEncoded = true;
        for (int32 Index = 0; Index < Arguments.Num(); ++Index)
        {
            if (!EncodeValueV3(
                    Arguments[Index],
                    EncodingFrame,
                    NativeArguments[Index],
                    ByteCount))
            {
                bEncoded = false;
                break;
            }
        }
        if (bEncoded)
        {
            break;
        }
    }
    if (!bEncoded)
    {
        UE_LOG(LogTemp, Error, TEXT("UnifiedScript event payload exceeded the ABI v4 limit."));
        return;
    }

    FUnifiedScriptNativeEventFrameV4 Frame;
    Frame.FrameSize = sizeof(Frame);
    Frame.SubscriptionId = LocalSubscriptionId;
    Frame.Source = LocalSourceHandle.Value;
    Frame.EventId = LocalEventId;
    Frame.Arguments = NativeArguments.GetData();
    Frame.ArgumentCount = static_cast<uint32>(NativeArguments.Num());
    Frame.ByteBuffer = ByteCount > 0 ? ByteBuffer.GetData() : nullptr;
    Frame.ByteCount = ByteCount;
    EventCallback(EventUserData, &Frame);
}

bool FUnifiedScriptNativeBridgeContext::SubscribeEvent(
    FUnifiedScriptObjectHandle Source,
    uint64 EventId,
    const FString& EventPath,
    FUnifiedScriptNativeEventCallbackV4 Callback,
    void* UserData,
    uint64& OutSubscriptionId)
{
    check(IsInGameThread());
    OutSubscriptionId = 0;
    if (Source.IsNull() || EventId == 0 || EventPath.IsEmpty() || Callback == nullptr ||
        !TryRetain())
    {
        return false;
    }

    bool bBound = false;
    {
        FScopeLock Lock(&BridgeLock);
        if (Bridge != nullptr)
        {
            UObject* SourceObject = nullptr;
            const FMulticastDelegateProperty* DelegateProperty = nullptr;
            FUnifiedScriptInvocationResult Result;
            if (Bridge->ResolveMulticastDelegate(
                    Source,
                    EventPath,
                    SourceObject,
                    DelegateProperty,
                    Result))
            {
                uint64 CandidateId = NextEventSubscriptionId++;
                if (CandidateId == 0)
                {
                    CandidateId = NextEventSubscriptionId++;
                }
                TStrongObjectPtr<UObject> Proxy(NewObject<UUnifiedScriptDelegateProxy>());
                if (CastChecked<UUnifiedScriptDelegateProxy>(Proxy.Get())->Bind(
                        this,
                        CandidateId,
                        Source,
                        EventId,
                        SourceObject,
                        DelegateProperty,
                        Callback,
                        UserData))
                {
                    EventSubscriptions.Add(CandidateId, MoveTemp(Proxy));
                    OutSubscriptionId = CandidateId;
                    bBound = true;
                }
            }
            else
            {
                UE_LOG(LogTemp, Error, TEXT("%s"), *Result.Message);
            }
        }
    }
    if (!bBound)
    {
        Release();
    }
    return bBound;
}

bool FUnifiedScriptNativeBridgeContext::UnsubscribeEvent(uint64 SubscriptionId)
{
    check(IsInGameThread());
    TStrongObjectPtr<UObject> Subscription;
    {
        FScopeLock Lock(&BridgeLock);
        if (!EventSubscriptions.RemoveAndCopyValue(SubscriptionId, Subscription))
        {
            return false;
        }
    }
    CastChecked<UUnifiedScriptDelegateProxy>(Subscription.Get())->Unbind();
    Release();
    return true;
}

namespace UE::UnifiedScript::NativeTransport::Private
{
uint64 SubscribeEvent(
    void* Context,
    uint64 Source,
    uint64 EventId,
    const uint8* EventPathData,
    int32 EventPathLength,
    FUnifiedScriptNativeEventCallbackV4 Callback,
    void* UserData)
{
    FString EventPath;
    if (Context == nullptr || Source == 0 || EventId == 0 || Callback == nullptr ||
        !TryDecodeUtf8(EventPathData, EventPathLength, EventPath) ||
        EventPath.IsEmpty() ||
        UnifiedScriptMemberId(TCHAR_TO_UTF8(*EventPath)) != EventId)
    {
        return 0;
    }
    uint64 SubscriptionId = 0;
    return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->SubscribeEvent(
            {Source},
            EventId,
            EventPath,
            Callback,
            UserData,
            SubscriptionId)
        ? SubscriptionId
        : 0;
}

uint8 UnsubscribeEvent(void* Context, uint64 SubscriptionId)
{
    return Context != nullptr && SubscriptionId != 0 &&
        static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->UnsubscribeEvent(
            SubscriptionId)
        ? 1
        : 0;
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
        UEUnifiedScript_GetNativeTransportV3(),
        UEUnifiedScript_GetNativeEventTransportV4(),
        UEUnifiedScript_GetSharedDataTransportV4(),
        UEUnifiedScript_GetGameplayDataTransportV4(),
        UEUnifiedScript_GetAsyncTransportV5(),
        UEUnifiedScript_GetNativeObjectTransportV1(),
    };
    return &Transport;
}

extern "C" const FUnifiedScriptAsyncTransportV5*
UEUnifiedScript_GetAsyncTransportV5()
{
    using namespace UE::UnifiedScript::NativeTransport::Private;
    constexpr uint64 Capabilities =
        static_cast<uint64>(EUnifiedScriptAsyncCapabilitiesV5::Delay) |
        static_cast<uint64>(EUnifiedScriptAsyncCapabilitiesV5::AsyncAssetLoad) |
        static_cast<uint64>(EUnifiedScriptAsyncCapabilitiesV5::Cancellation) |
        static_cast<uint64>(EUnifiedScriptAsyncCapabilitiesV5::Timeout) |
        static_cast<uint64>(EUnifiedScriptAsyncCapabilitiesV5::GenerationOwnership) |
        static_cast<uint64>(EUnifiedScriptAsyncCapabilitiesV5::WorldTeardown);
    static const FUnifiedScriptAsyncTransportV5 Transport = {
        UE::UnifiedScript::AsyncService::AbiVersion,
        sizeof(FUnifiedScriptAsyncTransportV5),
        sizeof(FUnifiedScriptAsyncRequestV5),
        sizeof(FUnifiedScriptAsyncCompletionV5),
        sizeof(FUnifiedScriptAsyncCompletionViewV5),
        0,
        Capabilities,
        &RetainContext,
        &ReleaseContext,
        &SubmitAsyncRequest,
        &PumpAsyncOwner,
        &CancelAsyncRequest,
        &PollAsyncCompletions,
        &CancelAsyncOwner,
    };
    return &Transport;
}

extern "C" const FUnifiedScriptGameplayDataTransportV4*
UEUnifiedScript_GetGameplayDataTransportV4()
{
    using namespace UE::UnifiedScript::NativeTransport::Private;
    constexpr uint64 Capabilities =
        static_cast<uint64>(EUnifiedScriptGameplayDataCapabilitiesV4::WorldPhaseScheduler) |
        static_cast<uint64>(EUnifiedScriptGameplayDataCapabilitiesV4::CommandBuffer) |
        static_cast<uint64>(EUnifiedScriptGameplayDataCapabilitiesV4::EventBuffer) |
        static_cast<uint64>(EUnifiedScriptGameplayDataCapabilitiesV4::BatchLineTrace) |
        static_cast<uint64>(EUnifiedScriptGameplayDataCapabilitiesV4::BatchSphereTrace) |
        static_cast<uint64>(EUnifiedScriptGameplayDataCapabilitiesV4::BatchBoxTrace) |
        static_cast<uint64>(
            EUnifiedScriptGameplayDataCapabilitiesV4::SchemaGeneratedLayouts) |
        static_cast<uint64>(EUnifiedScriptGameplayDataCapabilitiesV4::CapacityTelemetry) |
        static_cast<uint64>(EUnifiedScriptGameplayDataCapabilitiesV4::BatchSphereOverlap) |
        static_cast<uint64>(EUnifiedScriptGameplayDataCapabilitiesV4::BatchBoxOverlap) |
        static_cast<uint64>(EUnifiedScriptGameplayDataCapabilitiesV4::GeneratedEventChannels);
    static const FUnifiedScriptGameplayDataTransportV4 Transport = {
        UE::UnifiedScript::GameplayData::AbiVersion,
        sizeof(FUnifiedScriptGameplayDataTransportV4),
        sizeof(FUnifiedScriptCommandV4),
        sizeof(FUnifiedScriptGameplayEventV4),
        sizeof(FUnifiedScriptTraceRequestV4),
        sizeof(FUnifiedScriptTraceResultV4),
        Capabilities,
        &RetainContext,
        &ReleaseContext,
        &BeginCommandBatch,
        &CommitCommandBatch,
        &AcquireGameplayEvents,
        &BeginTraceBatch,
        &ExecuteTraceBatch,
        &CancelTraceBatch,
        &GetGameplayPhase,
        UE::UnifiedScript::DataPlaneSchema::CompatibilityId,
        UE::UnifiedScript::DataPlaneSchema::Revision,
        UE::UnifiedScript::GameplayData::MaximumCommandCount,
        UE::UnifiedScript::GameplayData::MaximumEventCount,
        UE::UnifiedScript::GameplayData::MaximumTraceCount,
        &GetGameplayDataTelemetry,
        sizeof(FUnifiedScriptOverlapRequestV1),
        sizeof(FUnifiedScriptOverlapResultV1),
        UE::UnifiedScript::GameplayData::MaximumOverlapCount,
        0,
        &BeginOverlapBatch,
        &ExecuteOverlapBatch,
        &CancelOverlapBatch,
        sizeof(FUnifiedScriptGeneratedDataViewV1),
        UE::UnifiedScript::GameplayData::MaximumGeneratedEventCount,
        UE::UnifiedScript::GameplayData::MaximumGeneratedChannelBytes,
        &AcquireGeneratedEvents,
    };
    return &Transport;
}

extern "C" const FUnifiedScriptSharedDataTransportV4*
UEUnifiedScript_GetSharedDataTransportV4()
{
    using namespace UE::UnifiedScript::NativeTransport::Private;
    constexpr uint64 Capabilities =
        static_cast<uint64>(EUnifiedScriptSharedDataCapabilitiesV4::TransformSnapshot) |
        static_cast<uint64>(EUnifiedScriptSharedDataCapabilitiesV4::DoubleBuffered) |
        static_cast<uint64>(EUnifiedScriptSharedDataCapabilitiesV4::WorldScopedHandles) |
        static_cast<uint64>(EUnifiedScriptSharedDataCapabilitiesV4::SchemaGeneratedLayouts) |
        static_cast<uint64>(EUnifiedScriptSharedDataCapabilitiesV4::CapacityTelemetry) |
        static_cast<uint64>(
            EUnifiedScriptSharedDataCapabilitiesV4::GeneratedSnapshotChannels);
    static const FUnifiedScriptSharedDataTransportV4 Transport = {
        UE::UnifiedScript::SharedData::AbiVersion,
        sizeof(FUnifiedScriptSharedDataTransportV4),
        sizeof(FUnifiedScriptSharedSnapshotViewV4),
        sizeof(FUnifiedScriptTransformSnapshotEntryV4),
        UE::UnifiedScript::SharedData::TransformSnapshotSchemaId,
        Capabilities,
        &RetainContext,
        &ReleaseContext,
        &AcquireTransformSnapshot,
        UE::UnifiedScript::DataPlaneSchema::CompatibilityId,
        UE::UnifiedScript::DataPlaneSchema::Revision,
        UE::UnifiedScript::SharedData::MaximumTransformSnapshotCount,
        &GetSharedDataTelemetry,
        sizeof(FUnifiedScriptGeneratedDataViewV1),
        UE::UnifiedScript::SharedData::MaximumGeneratedSnapshotCount,
        UE::UnifiedScript::SharedData::MaximumGeneratedChannelBytes,
        &AcquireGeneratedSnapshot,
    };
    return &Transport;
}

extern "C" const FUnifiedScriptNativeEventTransportV4*
UEUnifiedScript_GetNativeEventTransportV4()
{
    using namespace UE::UnifiedScript::NativeTransport::Private;
    static const FUnifiedScriptNativeEventTransportV4 Transport = {
        4,
        sizeof(FUnifiedScriptNativeEventTransportV4),
        sizeof(FUnifiedScriptNativeEventFrameV4),
        sizeof(FUnifiedScriptNativeValueV3),
        &RetainContext,
        &ReleaseContext,
        &SubscribeEvent,
        &UnsubscribeEvent,
    };
    return &Transport;
}

extern "C" const FUnifiedScriptNativeTransportV3*
UEUnifiedScript_GetNativeTransportV3()
{
    using namespace UE::UnifiedScript::NativeTransport::Private;
    constexpr uint64 Capabilities =
        static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::FixedScalars) |
        static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::Enums) |
        static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::ObjectHandles) |
        static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::VectorAndRotator) |
        static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::Transform) |
        static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::Utf8Text) |
        static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::Containers) |
        static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::OutputAndReference) |
        static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::Utf8Results) |
        static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::ContainerResults) |
        static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::Structs) |
        static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::ByteBuffers);

    static const FUnifiedScriptNativeTransportV3 Transport = {
        3,
        sizeof(FUnifiedScriptNativeTransportV3),
        sizeof(FUnifiedScriptNativeValueV3),
        sizeof(FUnifiedScriptNativeCallFrameV3),
        Capabilities,
        &RetainContext,
        &ReleaseContext,
        &ReadPropertyByIdV3,
        &WritePropertyByIdV3,
        &InvokeByIdV3,
        &InvokeInt32x2ByIdV3,
        &InvokeFixedByIdV3,
    };
    return &Transport;
}

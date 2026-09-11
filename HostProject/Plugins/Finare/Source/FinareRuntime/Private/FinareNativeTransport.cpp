#include "FinareNativeTransport.h"

#include "Containers/StringConv.h"
#include "HAL/Event.h"
#include "HAL/PlatformProcess.h"
#include "Misc/ScopeLock.h"
#include "Misc/ScopeExit.h"
#include "FinareDelegateProxy.h"
#include "FinareCallbackProxy.h"
#include "FinareGeneratedThunkRegistry.h"
#include "UObject/Field.h"

#include <atomic>
#include <bit>

namespace UE::Finare::NativeTransport::LifecycleDiagnostics
{
std::atomic<int32> GLiveContextCount{0};
std::atomic<int32> GAttachedContextCount{0};
std::atomic<int32> GPendingWorkerDispatchCount{0};
std::atomic<uint64> GRejectedWorkerDispatchCount{0};

bool TryReserveWorkerDispatch()
{
    int32 Count = GPendingWorkerDispatchCount.load(std::memory_order_relaxed);
    while (Count <
           FFinareNativeBridgeContext::MaximumPendingWorkerDispatchCount)
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

struct FFinareNativeBridgeContext::FPendingGameThreadCall
{
    explicit FPendingGameThreadCall(
        TUniqueFunction<void(FFinareInvocationBridge&)>&& InOperation)
        : Operation(MoveTemp(InOperation))
        , Completion(FPlatformProcess::GetSynchEventFromPool(true))
    {
    }

    ~FPendingGameThreadCall()
    {
        FPlatformProcess::ReturnSynchEventToPool(Completion);
    }

    TUniqueFunction<void(FFinareInvocationBridge&)> Operation;
    FEvent* Completion = nullptr;
    std::atomic<bool> bExecuted{false};
};

FFinareNativeBridgeContext::FFinareNativeBridgeContext(
    FFinareInvocationBridge& InBridge)
    : Bridge(&InBridge)
    , FastBridge(&InBridge)
{
    UE::Finare::NativeTransport::LifecycleDiagnostics::GLiveContextCount.fetch_add(
        1,
        std::memory_order_relaxed);
    UE::Finare::NativeTransport::LifecycleDiagnostics::GAttachedContextCount.fetch_add(
        1,
        std::memory_order_relaxed);
}

FFinareNativeBridgeContext::~FFinareNativeBridgeContext()
{
    UE::Finare::NativeTransport::LifecycleDiagnostics::GLiveContextCount.fetch_sub(
        1,
        std::memory_order_relaxed);
}

int32 FFinareNativeBridgeContext::GetLiveContextCount()
{
    return UE::Finare::NativeTransport::LifecycleDiagnostics::GLiveContextCount.load(
        std::memory_order_relaxed);
}

int32 FFinareNativeBridgeContext::GetAttachedContextCount()
{
    return UE::Finare::NativeTransport::LifecycleDiagnostics::GAttachedContextCount.load(
        std::memory_order_relaxed);
}

int32 FFinareNativeBridgeContext::GetGlobalPendingWorkerDispatchCount()
{
    return UE::Finare::NativeTransport::LifecycleDiagnostics::
        GPendingWorkerDispatchCount.load(std::memory_order_relaxed);
}

uint64 FFinareNativeBridgeContext::GetGlobalRejectedWorkerDispatchCount()
{
    return UE::Finare::NativeTransport::LifecycleDiagnostics::
        GRejectedWorkerDispatchCount.load(std::memory_order_relaxed);
}

int32 FFinareNativeBridgeContext::GetEventSubscriptionCount()
{
    FScopeLock Lock(&BridgeLock);
    return EventSubscriptions.Num();
}

void FFinareNativeBridgeContext::AttachSharedDataPlane(
    FFinareSharedDataPlane* InSharedDataPlane)
{
    check(IsInGameThread());
    FScopeLock Lock(&BridgeLock);
    check(Bridge != nullptr);
    SharedDataPlane = InSharedDataPlane;
}

void FFinareNativeBridgeContext::AttachGameplayDataPlane(
    FFinareGameplayDataPlane* InGameplayDataPlane)
{
    check(IsInGameThread());
    FScopeLock Lock(&BridgeLock);
    check(Bridge != nullptr);
    GameplayDataPlane = InGameplayDataPlane;
}

void FFinareNativeBridgeContext::AttachAsyncService(
    FFinareAsyncService* InAsyncService)
{
    check(IsInGameThread());
    FScopeLock Lock(&BridgeLock);
    check(Bridge != nullptr);
    AsyncService = InAsyncService;
}

bool FFinareNativeBridgeContext::TryRetain()
{
    FScopeLock Lock(&BridgeLock);
    if (Bridge == nullptr)
    {
        return false;
    }
    ReferenceCount.fetch_add(1, std::memory_order_relaxed);
    return true;
}

void FFinareNativeBridgeContext::Release()
{
    const uint32 Previous = ReferenceCount.fetch_sub(1, std::memory_order_acq_rel);
    check(Previous > 0);
    if (Previous == 1)
    {
        delete this;
    }
}

void FFinareNativeBridgeContext::Detach()
{
    check(IsInGameThread());
    SuspendWorkerDispatch();
    TArray<TStrongObjectPtr<UObject>> Subscriptions;
    bool bWasAttached = false;
    {
        FScopeLock Lock(&BridgeLock);
        bWasAttached = Bridge != nullptr;
        for (auto& Entry : CallbackProxies)
        {
            CastChecked<UFinareCallbackProxy>(Entry.Value.Get())->Unbind();
            if (Bridge)
            {
                FFinareObjectHandleResult HandleResult;
                Bridge->GetObjectRegistry().ReleaseObject({Entry.Key}, HandleResult);
            }
        }
        CallbackProxies.Reset();
        CallbackScopes.Reset();
        FastBridge.store(nullptr, std::memory_order_release);
        Bridge = nullptr;
        SharedDataPlane = nullptr;
        GameplayDataPlane = nullptr;
        AsyncService = nullptr;
        PendingCallFrameResult.Reset();
        CorrelatedResults.Reset();
        CorrelatedResultBytes = 0;
        EventSubscriptions.GenerateValueArray(Subscriptions);
        EventSubscriptions.Reset();
    }
    if (bWasAttached)
    {
        NativeObjects.Detach();
        UE::Finare::NativeTransport::LifecycleDiagnostics::GAttachedContextCount.fetch_sub(
            1,
            std::memory_order_relaxed);
    }
    for (TStrongObjectPtr<UObject>& Subscription : Subscriptions)
    {
        if (UFinareDelegateProxy* Proxy =
                Cast<UFinareDelegateProxy>(Subscription.Get()))
        {
            Proxy->Unbind();
        }
        Release();
    }
}

bool FFinareNativeBridgeContext::Execute(
    TUniqueFunction<void(FFinareInvocationBridge&)>&& Operation)
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
        if (!UE::Finare::NativeTransport::LifecycleDiagnostics::
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

bool FFinareNativeBridgeContext::ExecuteGameThreadFast(
    TUniqueFunction<void(FFinareInvocationBridge&)>&& Operation)
{
    if (!IsInGameThread())
    {
        return Execute(MoveTemp(Operation));
    }
    FFinareInvocationBridge* Current =
        FastBridge.load(std::memory_order_acquire);
    if (Current == nullptr)
    {
        return false;
    }
    Operation(*Current);
    return true;
}

bool FFinareNativeBridgeContext::ExecuteCallFrame(
    TUniqueFunction<void(
        FFinareInvocationBridge&,
        FPendingCallFrameResult&)>&& Operation,
    uint64 CorrelationId)
{
    bool bAccepted = true;
    const bool bExecuted = Execute(
        [this, CorrelationId, &bAccepted, Operation = MoveTemp(Operation)](
            FFinareInvocationBridge& CurrentBridge) mutable
        {
            if (CorrelationId == 0)
            {
                Operation(CurrentBridge, PendingCallFrameResult);
                return;
            }
            TSharedPtr<FPendingCallFrameResult> Pending = CorrelatedResults.FindRef(CorrelationId);
            if (!Pending)
            {
                if (CorrelatedResults.Num() >= MaximumCorrelatedResults)
                {
                    bAccepted = false;
                    return;
                }
                Pending = MakeShared<FPendingCallFrameResult>();
                CorrelatedResults.Add(CorrelationId, Pending);
            }
            if (Pending->bInFlight)
            {
                bAccepted = false;
                return;
            }
            // Keep the entry stable when a callback reenters and grows the map.
            const uint32 PreviousBytes = Pending->EncodedByteCount;
            Pending->bInFlight = true;
            Operation(CurrentBridge, *Pending);
            Pending->bInFlight = false;
            if (Bridge == nullptr)
            {
                bAccepted = false;
                return; // Detach already cleared the map and accounting.
            }
            CorrelatedResultBytes -= PreviousBytes;
            if (Pending->bValid &&
                CorrelatedResultBytes + Pending->EncodedByteCount <= MaximumCorrelatedResultBytes)
            {
                CorrelatedResultBytes += Pending->EncodedByteCount;
            }
            else
            {
                bAccepted = !Pending->bValid;
                CorrelatedResults.Remove(CorrelationId);
            }
        });
    return bExecuted && bAccepted;
}

void FFinareNativeBridgeContext::ReleaseCorrelatedResult(uint64 CorrelationId)
{
    Execute([this, CorrelationId](FFinareInvocationBridge&)
    {
        const auto Pending = CorrelatedResults.FindRef(CorrelationId);
        if (Pending && !Pending->bInFlight)
        {
            CorrelatedResultBytes -= Pending->EncodedByteCount;
            CorrelatedResults.Remove(CorrelationId);
        }
    });
}

bool FFinareNativeBridgeContext::ExecuteNativeObjects(
    TFunctionRef<void(FFinareNativeObjectRegistry&)> Operation)
{
    if (!IsInGameThread() || FastBridge.load(std::memory_order_acquire) == nullptr) return false;
    Operation(NativeObjects);
    return true;
}

namespace
{
EFinareUObjectReferenceStatus ToReferenceStatus(
    const FFinareObjectHandleResult& Result)
{
    switch (Result.Error)
    {
    case EFinareObjectHandleError::None: return EFinareUObjectReferenceStatus::Success;
    case EFinareObjectHandleError::NotGameThread: return EFinareUObjectReferenceStatus::WrongThread;
    case EFinareObjectHandleError::WorldMismatch: return EFinareUObjectReferenceStatus::WorldMismatch;
    case EFinareObjectHandleError::InvalidHandle: return EFinareUObjectReferenceStatus::InvalidHandle;
    case EFinareObjectHandleError::StaleHandle: return EFinareUObjectReferenceStatus::StaleHandle;
    case EFinareObjectHandleError::InvalidScope: return EFinareUObjectReferenceStatus::InvalidScope;
    default: return EFinareUObjectReferenceStatus::Unavailable;
    }
}
}

bool FFinareNativeBridgeContext::OpenUObjectReferenceScope(
    uint64& OutScope,
    FFinareObjectHandleResult& OutResult)
{
    OutScope = 0;
    if (!IsInGameThread())
    {
        OutResult.Error = EFinareObjectHandleError::NotGameThread;
        OutResult.Message = TEXT("UObject reference scopes are game-thread-only.");
        return false;
    }
    FFinareInvocationBridge* Current = FastBridge.load(std::memory_order_acquire);
    if (Current == nullptr)
    {
        OutResult.Error = EFinareObjectHandleError::RegistryWorldUnavailable;
        OutResult.Message = TEXT("The UObject reference registry is unavailable.");
        return false;
    }
    return Current->GetObjectRegistry().OpenReferenceScope(OutScope, OutResult);
}

EFinareUObjectReferenceStatus FFinareNativeBridgeContext::CloseUObjectReferenceScope(
    uint64 Scope,
    FFinareObjectHandleResult& OutResult)
{
    if (!IsInGameThread())
    {
        OutResult.Error = EFinareObjectHandleError::NotGameThread;
        OutResult.Message = TEXT("UObject reference scopes are game-thread-only.");
        return EFinareUObjectReferenceStatus::WrongThread;
    }
    FFinareInvocationBridge* Current = FastBridge.load(std::memory_order_acquire);
    if (Current == nullptr)
    {
        OutResult.Error = EFinareObjectHandleError::RegistryWorldUnavailable;
        OutResult.Message = TEXT("The UObject reference registry is unavailable.");
        return EFinareUObjectReferenceStatus::Unavailable;
    }
    Current->GetObjectRegistry().CloseReferenceScope(Scope, OutResult);
    return ToReferenceStatus(OutResult);
}

EFinareUObjectReferenceStatus FFinareNativeBridgeContext::RetainUObjectReference(
    uint64 Scope,
    uint64 Handle,
    FFinareObjectHandleResult& OutResult)
{
    if (!IsInGameThread())
    {
        OutResult.Error = EFinareObjectHandleError::NotGameThread;
        OutResult.Message = TEXT("UObject references are game-thread-only.");
        return EFinareUObjectReferenceStatus::WrongThread;
    }
    FFinareInvocationBridge* Current = FastBridge.load(std::memory_order_acquire);
    if (Current == nullptr)
    {
        OutResult.Error = EFinareObjectHandleError::RegistryWorldUnavailable;
        OutResult.Message = TEXT("The UObject reference registry is unavailable.");
        return EFinareUObjectReferenceStatus::Unavailable;
    }
    Current->GetObjectRegistry().RetainReference(Scope, {Handle}, OutResult);
    return ToReferenceStatus(OutResult);
}

EFinareUObjectReferenceStatus FFinareNativeBridgeContext::ReleaseUObjectReference(
    uint64 Scope,
    uint64 Handle,
    FFinareObjectHandleResult& OutResult)
{
    if (!IsInGameThread())
    {
        OutResult.Error = EFinareObjectHandleError::NotGameThread;
        OutResult.Message = TEXT("UObject references are game-thread-only.");
        return EFinareUObjectReferenceStatus::WrongThread;
    }
    FFinareInvocationBridge* Current = FastBridge.load(std::memory_order_acquire);
    if (Current == nullptr)
    {
        OutResult.Error = EFinareObjectHandleError::RegistryWorldUnavailable;
        OutResult.Message = TEXT("The UObject reference registry is unavailable.");
        return EFinareUObjectReferenceStatus::Unavailable;
    }
    Current->GetObjectRegistry().ReleaseReference(Scope, {Handle}, OutResult);
    return ToReferenceStatus(OutResult);
}

void FFinareNativeBridgeContext::PumpGameThreadCalls()
{
    check(IsInGameThread());
    TSharedPtr<FPendingGameThreadCall> Pending;
    while (PendingGameThreadCalls.Dequeue(Pending))
    {
        UE::Finare::NativeTransport::LifecycleDiagnostics::
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

void FFinareNativeBridgeContext::SuspendWorkerDispatch()
{
    check(IsInGameThread());
    bAcceptWorkerDispatch.store(false, std::memory_order_release);
    TSharedPtr<FPendingGameThreadCall> Pending;
    while (PendingGameThreadCalls.Dequeue(Pending))
    {
        UE::Finare::NativeTransport::LifecycleDiagnostics::
            GPendingWorkerDispatchCount.fetch_sub(1, std::memory_order_relaxed);
        Pending->Completion->Trigger();
    }
}

bool FFinareNativeBridgeContext::AcquireTransformSnapshot(
    FFinareSharedSnapshotViewV4& OutView)
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

bool FFinareNativeBridgeContext::AcquireGeneratedSnapshot(
    uint64 SchemaId,
    uint32 EntrySize,
    FFinareGeneratedDataViewV1& OutView)
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

bool FFinareNativeBridgeContext::GetSharedDataTelemetry(
    FFinareSharedDataTelemetryV1& OutTelemetry)
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

bool FFinareNativeBridgeContext::ExecuteGameplayData(
    TFunctionRef<bool(FFinareGameplayDataPlane&)> Operation)
{
    if (!IsInGameThread())
    {
        return false;
    }
    FScopeLock Lock(&BridgeLock);
    return Bridge != nullptr && GameplayDataPlane != nullptr &&
        Operation(*GameplayDataPlane);
}

bool FFinareNativeBridgeContext::ExecuteAsyncService(
    TFunctionRef<bool(FFinareAsyncService&)> Operation)
{
    if (!IsInGameThread())
    {
        return false;
    }
    FScopeLock Lock(&BridgeLock);
    return Bridge != nullptr && AsyncService != nullptr && Operation(*AsyncService);
}

namespace UE::Finare::NativeTransport::Private
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

bool EncodeContainerPayloadV3(
    const FFinareValue& Value,
    TArray<uint8>& OutPayload);

bool DecodeContainerPayloadV3(
    const FFinareNativeValueV1& NativeValue,
    FFinareValue& OutValue);

void Complete(
    FFinareNativeResultCallbackV1 Callback,
    void* UserData,
    const FFinareValue& Value,
    const FFinareInvocationResult& Result)
{
    if (Callback == nullptr)
    {
        return;
    }

    FFinareInvocationResult EffectiveResult = Result;
    FFinareValue EmptyValue;
    const FFinareValue* ReportedValue = &Value;
    TArray<uint8> ContainerPayload;
    if (EffectiveResult.IsSuccess() &&
        (Value.Kind == EFinareValueKind::Array ||
         Value.Kind == EFinareValueKind::Set ||
         Value.Kind == EFinareValueKind::Map) &&
        !EncodeContainerPayloadV3(Value, ContainerPayload))
    {
        EffectiveResult.Error = EFinareInvocationError::UnsupportedValueType;
        EffectiveResult.Message = TEXT("The reflected collection result cannot be encoded in the bounded V3 payload.");
        EmptyValue = {};
        ReportedValue = &EmptyValue;
    }
    if (!EffectiveResult.IsSuccess())
    {
        EmptyValue = {};
        ReportedValue = &EmptyValue;
    }

    FString StringValue;
    switch (ReportedValue->Kind)
    {
    case EFinareValueKind::String:
    case EFinareValueKind::SoftObject:
    case EFinareValueKind::SoftClass:
        StringValue = ReportedValue->StringValue;
        break;
    case EFinareValueKind::Name:
        StringValue = ReportedValue->NameValue.ToString();
        break;
    case EFinareValueKind::Text:
        StringValue = ReportedValue->TextValue.ToString();
        break;
    default:
        break;
    }
    const FTCHARToUTF8 StringUtf8(*StringValue);
    const FTCHARToUTF8 MessageUtf8(*EffectiveResult.Message);
    const bool bHasString = ReportedValue->Kind == EFinareValueKind::String ||
        ReportedValue->Kind == EFinareValueKind::SoftObject ||
        ReportedValue->Kind == EFinareValueKind::SoftClass ||
        ReportedValue->Kind == EFinareValueKind::Name ||
        ReportedValue->Kind == EFinareValueKind::Text;
    uint8 BinaryPayload[sizeof(double) * 10] = {};
    TArray<uint8> EnumPayload;
    int32 EncodedInt32 = ReportedValue->Int32Value;
    switch (ReportedValue->Kind)
    {
    case EFinareValueKind::Int8: EncodedInt32 = ReportedValue->Int8Value; break;
    case EFinareValueKind::UInt8: EncodedInt32 = ReportedValue->UInt8Value; break;
    case EFinareValueKind::Int16: EncodedInt32 = ReportedValue->Int16Value; break;
    case EFinareValueKind::UInt16: EncodedInt32 = ReportedValue->UInt16Value; break;
    case EFinareValueKind::UInt32: EncodedInt32 = std::bit_cast<int32>(ReportedValue->UInt32Value); break;
    case EFinareValueKind::Float32: EncodedInt32 = std::bit_cast<int32>(ReportedValue->Float32Value); break;
    default: break;
    }
    const uint8* ValueData = bHasString
        ? reinterpret_cast<const uint8*>(StringUtf8.Get())
        : nullptr;
    int32 ValueLength = bHasString ? StringUtf8.Length() : 0;
    if (ReportedValue->Kind == EFinareValueKind::Int64 || ReportedValue->Kind == EFinareValueKind::UInt64)
    {
        WriteUInt64LittleEndian(BinaryPayload, ReportedValue->Kind == EFinareValueKind::Int64
            ? std::bit_cast<uint64>(ReportedValue->Int64Value) : ReportedValue->UInt64Value);
        ValueData = BinaryPayload;
        ValueLength = sizeof(uint64);
    }
    else if (ReportedValue->Kind == EFinareValueKind::Enum)
    {
        const FTCHARToUTF8 Path(ReportedValue->TypePath.IsNone() ? TEXT("") : *ReportedValue->TypePath.ToString());
        if (ReportedValue->TypeIdValue == 0 || Path.Length() > 4096)
        {
            FFinareInvocationResult Failure;
            Failure.Error = EFinareInvocationError::ValueTypeMismatch;
            Failure.Message = TEXT("Enum result requires a stable type ID and a bounded type path.");
            Complete(Callback, UserData, {}, Failure);
            return;
        }
        EnumPayload.SetNumUninitialized(16 + Path.Length());
        WriteUInt64LittleEndian(EnumPayload.GetData(), std::bit_cast<uint64>(ReportedValue->Int64Value));
        WriteUInt64LittleEndian(EnumPayload.GetData() + 8, ReportedValue->TypeIdValue);
        if (Path.Length() > 0) FMemory::Memcpy(EnumPayload.GetData() + 16, Path.Get(), Path.Length());
        ValueData = EnumPayload.GetData();
        ValueLength = EnumPayload.Num();
    }
    else if (ReportedValue->Kind == EFinareValueKind::Float64)
    {
        WriteUInt64LittleEndian(BinaryPayload, std::bit_cast<uint64>(ReportedValue->Float64Value));
        ValueData = BinaryPayload;
        ValueLength = sizeof(double);
    }
    else if (ReportedValue->Kind == EFinareValueKind::Object ||
        ReportedValue->Kind == EFinareValueKind::Class)
    {
        WriteUInt64LittleEndian(BinaryPayload, ReportedValue->ObjectHandleValue.Value);
        ValueData = BinaryPayload;
        ValueLength = ObjectHandlePayloadSize;
    }
    else if (ReportedValue->Kind == EFinareValueKind::Vector)
    {
        WriteUInt64LittleEndian(
            BinaryPayload,
            std::bit_cast<uint64>(ReportedValue->VectorValue.X));
        WriteUInt64LittleEndian(
            BinaryPayload + sizeof(uint64),
            std::bit_cast<uint64>(ReportedValue->VectorValue.Y));
        WriteUInt64LittleEndian(
            BinaryPayload + sizeof(uint64) * 2,
            std::bit_cast<uint64>(ReportedValue->VectorValue.Z));
        ValueData = BinaryPayload;
        ValueLength = VectorPayloadSize;
    }
    else if (ReportedValue->Kind == EFinareValueKind::Rotator)
    {
        WriteUInt64LittleEndian(BinaryPayload, std::bit_cast<uint64>(ReportedValue->RotatorValue.Pitch));
        WriteUInt64LittleEndian(BinaryPayload + 8, std::bit_cast<uint64>(ReportedValue->RotatorValue.Yaw));
        WriteUInt64LittleEndian(BinaryPayload + 16, std::bit_cast<uint64>(ReportedValue->RotatorValue.Roll));
        ValueData = BinaryPayload;
        ValueLength = 24;
    }
    else if (ReportedValue->Kind == EFinareValueKind::Transform)
    {
        const FQuat Q = ReportedValue->TransformValue.GetRotation();
        const FVector T = ReportedValue->TransformValue.GetTranslation();
        const FVector S = ReportedValue->TransformValue.GetScale3D();
        const double Cells[] = {Q.X, Q.Y, Q.Z, Q.W, T.X, T.Y, T.Z, S.X, S.Y, S.Z};
        for (int32 Index = 0; Index < 10; ++Index)
            WriteUInt64LittleEndian(BinaryPayload + Index * 8, std::bit_cast<uint64>(Cells[Index]));
        ValueData = BinaryPayload;
        ValueLength = 80;
    }
    else if (ReportedValue->Kind == EFinareValueKind::Array ||
        ReportedValue->Kind == EFinareValueKind::Set ||
        ReportedValue->Kind == EFinareValueKind::Map)
    {
        ValueData = ContainerPayload.GetData();
        ValueLength = ContainerPayload.Num();
    }
    Callback(
        UserData,
        EffectiveResult.IsSuccess() ? 1 : 0,
        static_cast<uint8>(ReportedValue->Kind),
        ReportedValue->BooleanValue ? 1 : 0,
        EncodedInt32,
        ValueData,
        ValueLength,
        static_cast<uint8>(EffectiveResult.Error),
        static_cast<uint8>(EffectiveResult.HandleError),
        reinterpret_cast<const uint8*>(MessageUtf8.Get()),
        MessageUtf8.Length());
}

void CompleteFailure(
    FFinareNativeResultCallbackV1 Callback,
    void* UserData,
    EFinareInvocationError Error,
    const TCHAR* Message)
{
    FFinareInvocationResult Result;
    Result.Error = Error;
    Result.Message = Message;
    Complete(Callback, UserData, {}, Result);
}

FFinareNativeBridgeContext* ResolveContext(
    void* Context,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData)
{
    if (Context != nullptr)
    {
        return static_cast<FFinareNativeBridgeContext*>(Context);
    }

    CompleteFailure(
        Callback,
        UserData,
        EFinareInvocationError::TransportUnavailable,
        TEXT("The Finare native invocation context is unavailable."));
    return nullptr;
}

bool DecodeMemberPath(
    const uint8* Data,
    int32 Length,
    FString& OutPath,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData)
{
    if (TryDecodeUtf8(Data, Length, OutPath) && !OutPath.IsEmpty())
    {
        return true;
    }

    CompleteFailure(
        Callback,
        UserData,
        EFinareInvocationError::InvalidMemberPath,
        TEXT("The Finare native member path is invalid UTF-8 or empty."));
    return false;
}

bool DecodeValue(
    const FFinareNativeValueV1& NativeValue,
    FFinareValue& OutValue,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData)
{
    const auto Invalid = [&](const TCHAR* Message)
    {
        CompleteFailure(Callback, UserData, EFinareInvocationError::ValueTypeMismatch, Message);
        return false;
    };
    if (NativeValue.Reserved != 0)
    {
        CompleteFailure(
            Callback,
            UserData,
            EFinareInvocationError::ValueTypeMismatch,
            TEXT("The Finare native value contains non-zero reserved data."));
        return false;
    }

    switch (static_cast<EFinareValueKind>(NativeValue.Kind))
    {
    case EFinareValueKind::Boolean:
        if (NativeValue.BooleanValue > 1)
        {
            CompleteFailure(
                Callback,
                UserData,
                EFinareInvocationError::ValueTypeMismatch,
                TEXT("The Finare native boolean value is invalid."));
            return false;
        }
        OutValue = FFinareValue::FromBoolean(NativeValue.BooleanValue != 0);
        return true;
    case EFinareValueKind::Int32:
        OutValue = FFinareValue::FromInt32(NativeValue.Int32Value);
        return true;
    case EFinareValueKind::Int8:
        if (NativeValue.Int32Value < MIN_int8 || NativeValue.Int32Value > MAX_int8) return Invalid(TEXT("Int8 payload is outside its declared width."));
        OutValue = FFinareValue::FromInt8(static_cast<int8>(NativeValue.Int32Value));
        return true;
    case EFinareValueKind::UInt8:
        if (NativeValue.Int32Value < 0 || NativeValue.Int32Value > MAX_uint8) return Invalid(TEXT("UInt8 payload is outside its declared width."));
        OutValue = FFinareValue::FromUInt8(static_cast<uint8>(NativeValue.Int32Value));
        return true;
    case EFinareValueKind::Int16:
        if (NativeValue.Int32Value < MIN_int16 || NativeValue.Int32Value > MAX_int16) return Invalid(TEXT("Int16 payload is outside its declared width."));
        OutValue = FFinareValue::FromInt16(static_cast<int16>(NativeValue.Int32Value));
        return true;
    case EFinareValueKind::UInt16:
        if (NativeValue.Int32Value < 0 || NativeValue.Int32Value > MAX_uint16) return Invalid(TEXT("UInt16 payload is outside its declared width."));
        OutValue = FFinareValue::FromUInt16(static_cast<uint16>(NativeValue.Int32Value));
        return true;
    case EFinareValueKind::UInt32:
        OutValue = FFinareValue::FromUInt32(std::bit_cast<uint32>(NativeValue.Int32Value));
        return true;
    case EFinareValueKind::Int64:
    case EFinareValueKind::UInt64:
        if (NativeValue.StringData == nullptr || NativeValue.StringLength != 8) return Invalid(TEXT("64-bit integer payload requires exactly eight bytes."));
        OutValue = static_cast<EFinareValueKind>(NativeValue.Kind) == EFinareValueKind::Int64
            ? FFinareValue::FromInt64(std::bit_cast<int64>(ReadUInt64LittleEndian(NativeValue.StringData)))
            : FFinareValue::FromUInt64(ReadUInt64LittleEndian(NativeValue.StringData));
        return true;
    case EFinareValueKind::Enum:
    {
        if (NativeValue.StringData == nullptr || NativeValue.StringLength < 16 || NativeValue.StringLength > 16 + 4096)
            return Invalid(TEXT("Enum payload requires a value, type ID and optional bounded type path."));
        const int64 Value = std::bit_cast<int64>(ReadUInt64LittleEndian(NativeValue.StringData));
        const uint64 TypeId = ReadUInt64LittleEndian(NativeValue.StringData + 8);
        if (TypeId == 0) return Invalid(TEXT("Enum payload requires a nonzero type ID."));
        if (NativeValue.StringLength == 16)
        {
            OutValue = FFinareValue::FromEnumTypeId(Value, TypeId);
            return true;
        }
        for (int32 Index = 16; Index < NativeValue.StringLength; ++Index)
            if (NativeValue.StringData[Index] == 0) return Invalid(TEXT("Enum type path contains a null byte."));
        FString Path;
        if (!TryDecodeUtf8(NativeValue.StringData + 16, NativeValue.StringLength - 16, Path) || Path.TrimStartAndEnd().IsEmpty())
            return Invalid(TEXT("Enum type path is invalid."));
        const FTCHARToUTF8 PathUtf8(*Path);
        if (FinareMemberId(PathUtf8.Get()) != TypeId) return Invalid(TEXT("Enum type path and ID disagree."));
        OutValue = FFinareValue::FromEnum(Value, FName(*Path));
        return true;
    }
    case EFinareValueKind::Float32:
        OutValue = FFinareValue::FromFloat32(std::bit_cast<float>(NativeValue.Int32Value));
        return true;
    case EFinareValueKind::Float64:
        if (NativeValue.StringData == nullptr || NativeValue.StringLength != sizeof(double))
        {
            CompleteFailure(Callback, UserData, EFinareInvocationError::ValueTypeMismatch,
                TEXT("The Finare native binary64 payload must contain exactly eight bytes."));
            return false;
        }
        OutValue = FFinareValue::FromFloat64(std::bit_cast<double>(ReadUInt64LittleEndian(NativeValue.StringData)));
        return true;
    case EFinareValueKind::String:
    case EFinareValueKind::SoftObject:
    case EFinareValueKind::SoftClass:
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
                EFinareInvocationError::ValueTypeMismatch,
                TEXT("The Finare native string slice is invalid."));
            return false;
        }
        OutValue = static_cast<EFinareValueKind>(NativeValue.Kind) ==
                EFinareValueKind::SoftObject
            ? FFinareValue::FromSoftObjectPath(MoveTemp(StringValue))
            : static_cast<EFinareValueKind>(NativeValue.Kind) ==
                EFinareValueKind::SoftClass
                ? FFinareValue::FromSoftClassPath(MoveTemp(StringValue))
                : FFinareValue::FromString(MoveTemp(StringValue));
        return true;
    }
    case EFinareValueKind::Name:
    case EFinareValueKind::Text:
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
                EFinareInvocationError::ValueTypeMismatch,
                TEXT("The Finare native text value is invalid UTF-8."));
            return false;
        }
        OutValue = static_cast<EFinareValueKind>(NativeValue.Kind) ==
                EFinareValueKind::Name
            ? FFinareValue::FromName(FName(*StringValue))
            : FFinareValue::FromText(FText::FromString(StringValue));
        return true;
    }
    case EFinareValueKind::Object:
    case EFinareValueKind::Class:
        if (NativeValue.StringData == nullptr ||
            NativeValue.StringLength != ObjectHandlePayloadSize)
        {
            CompleteFailure(
                Callback,
                UserData,
                EFinareInvocationError::ValueTypeMismatch,
                TEXT("The Finare native object handle payload is invalid."));
            return false;
        }
        OutValue = FFinareValue::FromObjectHandle(
            {ReadUInt64LittleEndian(NativeValue.StringData)},
            static_cast<EFinareValueKind>(NativeValue.Kind) ==
                EFinareValueKind::Class);
        return true;
    case EFinareValueKind::Vector:
        if (NativeValue.StringData == nullptr ||
            NativeValue.StringLength != VectorPayloadSize)
        {
            CompleteFailure(
                Callback,
                UserData,
                EFinareInvocationError::ValueTypeMismatch,
                TEXT("The Finare native vector payload is invalid."));
            return false;
        }
        OutValue = FFinareValue::FromVector(FVector(
            std::bit_cast<double>(ReadUInt64LittleEndian(NativeValue.StringData)),
            std::bit_cast<double>(ReadUInt64LittleEndian(
                NativeValue.StringData + sizeof(uint64))),
            std::bit_cast<double>(ReadUInt64LittleEndian(
                NativeValue.StringData + sizeof(uint64) * 2))));
        return true;
    case EFinareValueKind::Rotator:
        if (NativeValue.StringData == nullptr || NativeValue.StringLength != 24) return Invalid(TEXT("Rotator payload requires three binary64 cells."));
        OutValue = FFinareValue::FromRotator(FRotator(
            std::bit_cast<double>(ReadUInt64LittleEndian(NativeValue.StringData)),
            std::bit_cast<double>(ReadUInt64LittleEndian(NativeValue.StringData + 8)),
            std::bit_cast<double>(ReadUInt64LittleEndian(NativeValue.StringData + 16))));
        return true;
    case EFinareValueKind::Transform:
    {
        if (NativeValue.StringData == nullptr || NativeValue.StringLength != 80) return Invalid(TEXT("Transform payload requires ten binary64 cells."));
        const auto At = [&](int32 Index) { return std::bit_cast<double>(ReadUInt64LittleEndian(NativeValue.StringData + Index * 8)); };
        OutValue = FFinareValue::FromTransform(FTransform(FQuat(At(0), At(1), At(2), At(3)),
            FVector(At(4), At(5), At(6)), FVector(At(7), At(8), At(9))));
        return true;
    }
    case EFinareValueKind::Array:
    case EFinareValueKind::Set:
    case EFinareValueKind::Map:
        if (DecodeContainerPayloadV3(NativeValue, OutValue))
        {
            return true;
        }
        return Invalid(TEXT("The Finare reflected collection payload is invalid."));
    default:
        CompleteFailure(
            Callback,
            UserData,
            EFinareInvocationError::UnsupportedValueType,
            TEXT("The Finare native value kind is unsupported."));
        return false;
    }
}

void ReadProperty(
    void* Context,
    uint64 Target,
    const uint8* PropertyPathData,
    int32 PropertyPathLength,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData)
{
    FFinareNativeBridgeContext* NativeContext =
        ResolveContext(Context, Callback, UserData);
    if (NativeContext == nullptr)
    {
        return;
    }
    if (!NativeContext->Execute(
            [&](FFinareInvocationBridge& Bridge)
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
                FFinareValue Value;
                FFinareInvocationResult Result;
                Bridge.ReadProperty({Target}, PropertyPath, Value, Result);
                Complete(Callback, UserData, Value, Result);
            }))
    {
        CompleteFailure(
            Callback,
            UserData,
            EFinareInvocationError::TransportUnavailable,
            TEXT("The Finare world invocation context has been detached."));
    }
}

void WriteProperty(
    void* Context,
    uint64 Target,
    const uint8* PropertyPathData,
    int32 PropertyPathLength,
    const FFinareNativeValueV1* NativeValue,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData)
{
    FFinareNativeBridgeContext* NativeContext =
        ResolveContext(Context, Callback, UserData);
    if (NativeContext == nullptr)
    {
        return;
    }
    if (!NativeContext->Execute(
            [&](FFinareInvocationBridge& Bridge)
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
                        EFinareInvocationError::ValueTypeMismatch,
                        TEXT("The Finare native property value is missing."));
                    return;
                }

                FFinareValue Value;
                if (!DecodeValue(*NativeValue, Value, Callback, UserData))
                {
                    return;
                }

                FFinareInvocationResult Result;
                Bridge.WriteProperty({Target}, PropertyPath, Value, Result);
                Complete(Callback, UserData, {}, Result);
            }))
    {
        CompleteFailure(
            Callback,
            UserData,
            EFinareInvocationError::TransportUnavailable,
            TEXT("The Finare world invocation context has been detached."));
    }
}

void Invoke(
    void* Context,
    uint64 Target,
    const uint8* FunctionPathData,
    int32 FunctionPathLength,
    const FFinareNativeValueV1* NativeArguments,
    int32 ArgumentCount,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData)
{
    FFinareNativeBridgeContext* NativeContext =
        ResolveContext(Context, Callback, UserData);
    if (NativeContext == nullptr)
    {
        return;
    }
    if (!NativeContext->Execute(
            [&](FFinareInvocationBridge& Bridge)
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
                        EFinareInvocationError::ArgumentCountMismatch,
                        TEXT("The Finare native argument slice is invalid."));
                    return;
                }

                TArray<FFinareValue, TInlineAllocator<8>> Arguments;
                Arguments.Reserve(ArgumentCount);
                for (int32 Index = 0; Index < ArgumentCount; ++Index)
                {
                    FFinareValue Value;
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

                FFinareValue ReturnValue;
                FFinareInvocationResult Result;
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
            EFinareInvocationError::TransportUnavailable,
            TEXT("The Finare world invocation context has been detached."));
    }
}

void ReadPropertyById(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData)
{
    FFinareNativeBridgeContext* NativeContext =
        ResolveContext(Context, Callback, UserData);
    if (NativeContext == nullptr)
    {
        return;
    }
    if (!NativeContext->Execute(
            [&](FFinareInvocationBridge& Bridge)
            {
                FFinareValue Value;
                FFinareInvocationResult Result;
                Bridge.ReadPropertyById({Target}, MemberId, Value, Result);
                Complete(Callback, UserData, Value, Result);
            }))
    {
        CompleteFailure(
            Callback,
            UserData,
            EFinareInvocationError::TransportUnavailable,
            TEXT("The Finare world invocation context has been detached."));
    }
}

void WritePropertyById(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    const FFinareNativeValueV1* NativeValue,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData)
{
    FFinareNativeBridgeContext* NativeContext =
        ResolveContext(Context, Callback, UserData);
    if (NativeContext == nullptr)
    {
        return;
    }
    if (!NativeContext->Execute(
            [&](FFinareInvocationBridge& Bridge)
            {
                if (NativeValue == nullptr)
                {
                    CompleteFailure(
                        Callback,
                        UserData,
                        EFinareInvocationError::ValueTypeMismatch,
                        TEXT("The Finare native fast property value is missing."));
                    return;
                }

                FFinareValue Value;
                if (!DecodeValue(*NativeValue, Value, Callback, UserData))
                {
                    return;
                }
                FFinareInvocationResult Result;
                Bridge.WritePropertyById({Target}, MemberId, Value, Result);
                Complete(Callback, UserData, {}, Result);
            }))
    {
        CompleteFailure(
            Callback,
            UserData,
            EFinareInvocationError::TransportUnavailable,
            TEXT("The Finare world invocation context has been detached."));
    }
}

void InvokeById(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    const FFinareNativeValueV1* NativeArguments,
    int32 ArgumentCount,
    FFinareNativeResultCallbackV1 Callback,
    void* UserData)
{
    FFinareNativeBridgeContext* NativeContext =
        ResolveContext(Context, Callback, UserData);
    if (NativeContext == nullptr)
    {
        return;
    }
    if (!NativeContext->Execute(
            [&](FFinareInvocationBridge& Bridge)
            {
                if (ArgumentCount < 0 ||
                    (ArgumentCount > 0 && NativeArguments == nullptr))
                {
                    CompleteFailure(
                        Callback,
                        UserData,
                        EFinareInvocationError::ArgumentCountMismatch,
                        TEXT("The Finare native fast argument slice is invalid."));
                    return;
                }

                TArray<FFinareValue, TInlineAllocator<8>> Arguments;
                Arguments.Reserve(ArgumentCount);
                for (int32 Index = 0; Index < ArgumentCount; ++Index)
                {
                    FFinareValue Value;
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

                FFinareValue ReturnValue;
                FFinareInvocationResult Result;
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
            EFinareInvocationError::TransportUnavailable,
            TEXT("The Finare world invocation context has been detached."));
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
    const FFinareValue& Value,
    uint32& ByteCursor,
    uint32 Depth = 0)
{
    if (Depth > V3MaxContainerDepth)
    {
        return false;
    }
    switch (Value.Kind)
    {
    case EFinareValueKind::ByteBuffer:
        return static_cast<uint32>(Value.ByteBufferValue.Num()) <= FFinareByteBuffer::MaximumLength &&
            AddArenaBytesV3(ByteCursor, Value.ByteBufferValue.Num());
    case EFinareValueKind::String:
    {
        const FTCHARToUTF8 Utf8(*Value.StringValue);
        return AddArenaBytesV3(ByteCursor, Utf8.Length());
    }
    case EFinareValueKind::Name:
    {
        const FString Text = Value.NameValue.ToString();
        const FTCHARToUTF8 Utf8(*Text);
        return AddArenaBytesV3(ByteCursor, Utf8.Length());
    }
    case EFinareValueKind::Text:
    {
        const FString Text = Value.TextValue.ToString();
        const FTCHARToUTF8 Utf8(*Text);
        return AddArenaBytesV3(ByteCursor, Utf8.Length());
    }
    case EFinareValueKind::Transform:
        return AddArenaBytesV3(ByteCursor, V3TransformByteSize);
    case EFinareValueKind::Struct:
    case EFinareValueKind::Array:
    case EFinareValueKind::Set:
    case EFinareValueKind::Map:
    {
        if (Value.TypeIdValue == 0 ||
            (Value.Kind == EFinareValueKind::Map &&
                Value.ContainerValues.Num() % 2 != 0) ||
            !AddArenaBytesV3(
                ByteCursor,
                static_cast<uint64>(Value.ContainerValues.Num()) *
                    sizeof(FFinareNativeValueV3)))
        {
            return false;
        }
        for (const FFinareValue& Element : Value.ContainerValues)
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
    const FFinareNativeCallFrameV3& Frame,
    uint32 EntryByteCount)
{
    uint64 Hash = 14695981039346656037ull;
    HashBytesV3(Hash, &Frame.Flags, sizeof(Frame.Flags));
    HashBytesV3(Hash, &Frame.ArgumentCount, sizeof(Frame.ArgumentCount));
    HashBytesV3(Hash, &Frame.ResultCount, sizeof(Frame.ResultCount));
    if (Frame.ArgumentCount > 0)
    {
        HashBytesV3(
            Hash,
            Frame.Arguments,
            static_cast<uint64>(Frame.ArgumentCount) *
                sizeof(FFinareNativeValueV3));
    }
    if (Frame.ResultCount > 0)
    {
        HashBytesV3(
            Hash,
            Frame.Results,
            static_cast<uint64>(Frame.ResultCount) *
                sizeof(FFinareNativeValueV3));
    }
    if (EntryByteCount > 0)
    {
        HashBytesV3(Hash, Frame.ByteBuffer, EntryByteCount);
    }
    return Hash;
}

void ResetV3Output(FFinareNativeCallFrameV3& Frame, uint32 EntryByteCount)
{
    Frame.ResultCount = 0;
    Frame.ByteCount = EntryByteCount;
    Frame.RequiredResultCapacity = 0;
    Frame.RequiredByteCapacity = 0;
    Frame.InvocationError = 0;
    Frame.HandleError = 0;
    Frame.Status = EFinareNativeCallStatusV3::Uninitialized;
    Frame.ErrorMessageOffset = 0;
    Frame.ErrorMessageLength = 0;
}

void InvalidFrameV3(FFinareNativeCallFrameV3* Frame, uint32 EntryByteCount = 0)
{
    if (Frame == nullptr)
    {
        return;
    }
    ResetV3Output(*Frame, EntryByteCount);
    Frame->Status = EFinareNativeCallStatusV3::InvalidFrame;
}

void InvocationFailureV3(
    FFinareNativeCallFrameV3& Frame,
    uint32 EntryByteCount,
    const FFinareInvocationResult& Result)
{
    ResetV3Output(Frame, EntryByteCount);
    Frame.InvocationError = static_cast<uint8>(Result.Error);
    Frame.HandleError = static_cast<uint8>(Result.HandleError);
    Frame.Status = EFinareNativeCallStatusV3::InvocationFailure;
}

void UnsupportedValueV3(FFinareNativeCallFrameV3& Frame, uint32 EntryByteCount)
{
    FFinareInvocationResult Result;
    Result.Error = EFinareInvocationError::UnsupportedValueType;
    InvocationFailureV3(Frame, EntryByteCount, Result);
}

bool ValidateFrameV3(
    FFinareNativeCallFrameV3* Frame,
    uint32 ExpectedArgumentCount,
    uint32 ExpectedResultCount,
    uint32& OutEntryByteCount,
    EFinareNativeCallFrameFlagsV3 ExpectedFlags =
        EFinareNativeCallFrameFlagsV3::None)
{
    if (Frame == nullptr)
    {
        return false;
    }
    OutEntryByteCount = Frame->ByteCount;
    const bool bValid =
        Frame->Flags == static_cast<uint32>(ExpectedFlags) &&
        Frame->Reserved8 == 0 &&
        Frame->Reserved32 == 0 &&
        Frame->Status == EFinareNativeCallStatusV3::Uninitialized &&
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
    EFinareValueKind Kind,
    EFinareNativeValueKindV3& OutKind)
{
    switch (Kind)
    {
    case EFinareValueKind::Void:
        OutKind = EFinareNativeValueKindV3::Void;
        return true;
    case EFinareValueKind::Boolean:
        OutKind = EFinareNativeValueKindV3::Boolean;
        return true;
    case EFinareValueKind::Int8:
        OutKind = EFinareNativeValueKindV3::Int8;
        return true;
    case EFinareValueKind::UInt8:
        OutKind = EFinareNativeValueKindV3::UInt8;
        return true;
    case EFinareValueKind::Int16:
        OutKind = EFinareNativeValueKindV3::Int16;
        return true;
    case EFinareValueKind::UInt16:
        OutKind = EFinareNativeValueKindV3::UInt16;
        return true;
    case EFinareValueKind::Int32:
        OutKind = EFinareNativeValueKindV3::Int32;
        return true;
    case EFinareValueKind::UInt32:
        OutKind = EFinareNativeValueKindV3::UInt32;
        return true;
    case EFinareValueKind::Int64:
        OutKind = EFinareNativeValueKindV3::Int64;
        return true;
    case EFinareValueKind::UInt64:
        OutKind = EFinareNativeValueKindV3::UInt64;
        return true;
    case EFinareValueKind::Float32:
        OutKind = EFinareNativeValueKindV3::Float32;
        return true;
    case EFinareValueKind::Float64:
        OutKind = EFinareNativeValueKindV3::Float64;
        return true;
    case EFinareValueKind::Enum:
        OutKind = EFinareNativeValueKindV3::Enum;
        return true;
    case EFinareValueKind::String:
        OutKind = EFinareNativeValueKindV3::String;
        return true;
    case EFinareValueKind::Name:
        OutKind = EFinareNativeValueKindV3::Name;
        return true;
    case EFinareValueKind::Text:
        OutKind = EFinareNativeValueKindV3::Text;
        return true;
    case EFinareValueKind::Object:
        OutKind = EFinareNativeValueKindV3::Object;
        return true;
    case EFinareValueKind::Class:
        OutKind = EFinareNativeValueKindV3::Class;
        return true;
    case EFinareValueKind::Vector:
        OutKind = EFinareNativeValueKindV3::Vector3;
        return true;
    case EFinareValueKind::Rotator:
        OutKind = EFinareNativeValueKindV3::Rotator3;
        return true;
    case EFinareValueKind::Transform:
        OutKind = EFinareNativeValueKindV3::Transform;
        return true;
    case EFinareValueKind::Struct:
        OutKind = EFinareNativeValueKindV3::Struct;
        return true;
    case EFinareValueKind::Array:
        OutKind = EFinareNativeValueKindV3::Array;
        return true;
    case EFinareValueKind::Set:
        OutKind = EFinareNativeValueKindV3::Set;
        return true;
    case EFinareValueKind::ByteBuffer:
        OutKind = EFinareNativeValueKindV3::ByteBuffer;
        return true;
    case EFinareValueKind::Map:
        OutKind = EFinareNativeValueKindV3::Map;
        return true;
    default:
        return false;
    }
}

bool ValidateResultDescriptorV3(
    const FFinareNativeValueV3& Descriptor,
    EFinareValueKind ExpectedKind,
    uint64 ExpectedTypeId)
{
    EFinareNativeValueKindV3 NativeKind;
    if (!ToNativeKindV3(ExpectedKind, NativeKind) || Descriptor.Kind != NativeKind ||
        Descriptor.Reserved16 != 0 || Descriptor.Auxiliary != 0)
    {
        return false;
    }
    if (ExpectedKind == EFinareValueKind::Enum)
    {
        return ExpectedTypeId != 0 && Descriptor.TypeId == ExpectedTypeId &&
            Descriptor.Flags == EFinareNativeValueFlagsV3::None;
    }
    if (ExpectedKind == EFinareValueKind::Transform)
    {
        return Descriptor.TypeId == 0 &&
            Descriptor.Flags == EFinareNativeValueFlagsV3::ArenaBacked &&
            (Descriptor.Payload1 == 0 || Descriptor.Payload1 == V3TransformByteSize);
    }
    if (ExpectedKind == EFinareValueKind::ByteBuffer)
        return Descriptor.TypeId == 0 && Descriptor.Flags == EFinareNativeValueFlagsV3::ArenaBacked &&
            Descriptor.Payload0 == 0 && Descriptor.Payload1 == 0 && Descriptor.Payload2 == 0 && Descriptor.Payload3 == 0;
    if (ExpectedKind == EFinareValueKind::String ||
        ExpectedKind == EFinareValueKind::Name ||
        ExpectedKind == EFinareValueKind::Text)
    {
        return Descriptor.TypeId == 0 &&
            Descriptor.Flags == EFinareNativeValueFlagsV3::ArenaBacked;
    }
    if (ExpectedKind == EFinareValueKind::Struct ||
        ExpectedKind == EFinareValueKind::Array ||
        ExpectedKind == EFinareValueKind::Set ||
        ExpectedKind == EFinareValueKind::Map)
    {
        return ExpectedTypeId != 0 && Descriptor.TypeId == ExpectedTypeId &&
            Descriptor.Flags == EFinareNativeValueFlagsV3::ArenaBacked &&
            (Descriptor.Payload3 == 0 ||
                Descriptor.Payload3 == sizeof(FFinareNativeValueV3));
    }
    return Descriptor.TypeId == 0 &&
        Descriptor.Flags == EFinareNativeValueFlagsV3::None;
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
    const FFinareNativeValueV3& Native,
    const FFinareNativeCallFrameV3& Frame,
    uint32 EntryByteCount,
    FFinareValue& OutValue,
    uint32 Depth = 0)
{
    constexpr uint32 MaxContainerDepth = 16;
    if (Native.Reserved16 != 0 || Native.Auxiliary != 0 || Depth > MaxContainerDepth)
    {
        return false;
    }
    switch (Native.Kind)
    {
    case EFinareNativeValueKindV3::Callback:
        if (Depth || Native.Flags != EFinareNativeValueFlagsV3::None || !Native.TypeId ||
            !Native.Payload0 || !Native.Payload1 || Native.Payload2 || Native.Payload3) return false;
        OutValue = FFinareValue::FromCallbackToken(Native.Payload0, Native.Payload1, Native.TypeId);
        return true;
    case EFinareNativeValueKindV3::ByteBuffer:
    {
        if (Native.Flags != EFinareNativeValueFlagsV3::ArenaBacked || Native.TypeId || Native.Payload2 || Native.Payload3 ||
            Native.Payload0 > EntryByteCount || Native.Payload1 > EntryByteCount - Native.Payload0 ||
            Native.Payload1 > FFinareByteBuffer::MaximumLength) return false;
        const auto View = Native.Payload1 == 0 ? TConstArrayView<uint8>() :
            MakeArrayView(Frame.ByteBuffer + Native.Payload0, static_cast<int32>(Native.Payload1));
        OutValue = FFinareValue::FromByteBuffer(FFinareByteBuffer::CopyFrom(View));
        return true;
    }
    case EFinareNativeValueKindV3::Boolean:
        if (Native.Flags != EFinareNativeValueFlagsV3::None ||
            Native.TypeId != 0 || Native.Payload0 > 1)
        {
            return false;
        }
        OutValue = FFinareValue::FromBoolean(Native.Payload0 != 0);
        return true;
    case EFinareNativeValueKindV3::Int8:
        OutValue = FFinareValue::FromInt8(static_cast<int8>(Native.Payload0));
        break;
    case EFinareNativeValueKindV3::UInt8:
        OutValue = FFinareValue::FromUInt8(static_cast<uint8>(Native.Payload0));
        break;
    case EFinareNativeValueKindV3::Int16:
        OutValue = FFinareValue::FromInt16(static_cast<int16>(Native.Payload0));
        break;
    case EFinareNativeValueKindV3::UInt16:
        OutValue = FFinareValue::FromUInt16(static_cast<uint16>(Native.Payload0));
        break;
    case EFinareNativeValueKindV3::Int32:
        OutValue = FFinareValue::FromInt32(static_cast<int32>(Native.Payload0));
        break;
    case EFinareNativeValueKindV3::UInt32:
        OutValue = FFinareValue::FromUInt32(static_cast<uint32>(Native.Payload0));
        break;
    case EFinareNativeValueKindV3::Int64:
        OutValue = FFinareValue::FromInt64(static_cast<int64>(Native.Payload0));
        break;
    case EFinareNativeValueKindV3::UInt64:
        OutValue = FFinareValue::FromUInt64(Native.Payload0);
        break;
    case EFinareNativeValueKindV3::Float32:
        OutValue = FFinareValue::FromFloat32(
            std::bit_cast<float>(static_cast<uint32>(Native.Payload0)));
        break;
    case EFinareNativeValueKindV3::Float64:
        OutValue = FFinareValue::FromFloat64(std::bit_cast<double>(Native.Payload0));
        break;
    case EFinareNativeValueKindV3::Enum:
        if (Native.TypeId == 0)
        {
            return false;
        }
        OutValue = FFinareValue::FromEnumTypeId(
            static_cast<int64>(Native.Payload0),
            Native.TypeId);
        return Native.Flags == EFinareNativeValueFlagsV3::None;
    case EFinareNativeValueKindV3::Object:
    case EFinareNativeValueKindV3::Class:
        if (Native.Flags != EFinareNativeValueFlagsV3::None ||
            Native.TypeId != 0 || Native.Payload1 != 0 ||
            Native.Payload2 != 0 || Native.Payload3 != 0)
        {
            return false;
        }
        OutValue = FFinareValue::FromObjectHandle(
            {Native.Payload0},
            Native.Kind == EFinareNativeValueKindV3::Class);
        return true;
    case EFinareNativeValueKindV3::String:
    case EFinareNativeValueKindV3::Name:
    case EFinareNativeValueKindV3::Text:
    {
        if (Native.Flags != EFinareNativeValueFlagsV3::ArenaBacked ||
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
        if (Native.Kind == EFinareNativeValueKindV3::String)
        {
            OutValue = FFinareValue::FromString(MoveTemp(Decoded));
        }
        else if (Native.Kind == EFinareNativeValueKindV3::Name)
        {
            OutValue = FFinareValue::FromName(FName(*Decoded));
        }
        else
        {
            OutValue = FFinareValue::FromText(FText::FromString(Decoded));
        }
        return true;
    }
    case EFinareNativeValueKindV3::Vector3:
        OutValue = FFinareValue::FromVector(FVector(
            std::bit_cast<double>(Native.Payload0),
            std::bit_cast<double>(Native.Payload1),
            std::bit_cast<double>(Native.Payload2)));
        break;
    case EFinareNativeValueKindV3::Rotator3:
        OutValue = FFinareValue::FromRotator(FRotator(
            std::bit_cast<double>(Native.Payload0),
            std::bit_cast<double>(Native.Payload1),
            std::bit_cast<double>(Native.Payload2)));
        break;
    case EFinareNativeValueKindV3::Transform:
    {
        if (Native.Flags != EFinareNativeValueFlagsV3::ArenaBacked ||
            Native.TypeId != 0 || Native.Payload1 != V3TransformByteSize ||
            Native.Payload0 > EntryByteCount ||
            EntryByteCount - static_cast<uint32>(Native.Payload0) < V3TransformByteSize)
        {
            return false;
        }
        const uint64 Offset = Native.Payload0;
        OutValue = FFinareValue::FromTransform(FTransform(
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
    case EFinareNativeValueKindV3::Struct:
    case EFinareNativeValueKindV3::Array:
    case EFinareNativeValueKindV3::Set:
    case EFinareNativeValueKindV3::Map:
    {
        const uint64 CellsPerEntry =
            Native.Kind == EFinareNativeValueKindV3::Map ? 2ull : 1ull;
        if (Native.Flags != EFinareNativeValueFlagsV3::ArenaBacked ||
            Native.TypeId == 0 || Native.Payload3 != sizeof(FFinareNativeValueV3) ||
            Native.Payload2 > MAX_int32 ||
            Native.Payload2 > MAX_uint64 / CellsPerEntry)
        {
            return false;
        }
        const uint64 CellCount = Native.Payload2 * CellsPerEntry;
        if (CellCount > MAX_uint32 / sizeof(FFinareNativeValueV3))
        {
            return false;
        }
        const uint64 DescriptorBytes = CellCount * sizeof(FFinareNativeValueV3);
        if (Native.Payload1 != DescriptorBytes ||
            Native.Payload0 > EntryByteCount ||
            DescriptorBytes > EntryByteCount - Native.Payload0)
        {
            return false;
        }
        TArray<FFinareValue> Values;
        Values.Reserve(static_cast<int32>(CellCount));
        for (uint64 Index = 0; Index < CellCount; ++Index)
        {
            FFinareNativeValueV3 Cell;
            FMemory::Memcpy(
                &Cell,
                Frame.ByteBuffer + Native.Payload0 +
                    Index * sizeof(FFinareNativeValueV3),
                sizeof(Cell));
            FFinareValue Decoded;
            if (!DecodeValueV3(Cell, Frame, EntryByteCount, Decoded, Depth + 1))
            {
                return false;
            }
            Values.Add(MoveTemp(Decoded));
        }
        if (Native.Kind == EFinareNativeValueKindV3::Struct)
        {
            OutValue = FFinareValue::FromStruct(MoveTemp(Values), Native.TypeId);
        }
        else if (Native.Kind == EFinareNativeValueKindV3::Array)
        {
            OutValue = FFinareValue::FromArray(MoveTemp(Values), Native.TypeId);
        }
        else if (Native.Kind == EFinareNativeValueKindV3::Set)
        {
            OutValue = FFinareValue::FromSet(MoveTemp(Values), Native.TypeId);
        }
        else
        {
            OutValue = FFinareValue::FromMap(MoveTemp(Values), Native.TypeId);
        }
        return true;
    }
    default:
        return false;
    }
    return Native.Flags == EFinareNativeValueFlagsV3::None && Native.TypeId == 0;
}

bool EncodeValueV3(
    const FFinareValue& Value,
    FFinareNativeCallFrameV3& Frame,
    FFinareNativeValueV3& Native,
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
    case EFinareValueKind::ByteBuffer:
    {
        const auto View = Value.ByteBufferValue.GetView();
        const uint32 Count = static_cast<uint32>(View.Num());
        if (Count > FFinareByteBuffer::MaximumLength || ByteCursor > Frame.ByteCapacity ||
            Count > Frame.ByteCapacity - ByteCursor) return false;
        Native.Kind = EFinareNativeValueKindV3::ByteBuffer;
        Native.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
        Native.Payload0 = ByteCursor;
        Native.Payload1 = Count;
        if (Count) FMemory::Memcpy(Frame.ByteBuffer + ByteCursor, View.GetData(), Count);
        ByteCursor += Count;
        break;
    }
    case EFinareValueKind::Boolean:
        Native.Kind = EFinareNativeValueKindV3::Boolean;
        Native.Payload0 = Value.BooleanValue ? 1 : 0;
        break;
    case EFinareValueKind::Int8:
        Native.Kind = EFinareNativeValueKindV3::Int8;
        Native.Payload0 = static_cast<uint64>(Value.Int8Value);
        break;
    case EFinareValueKind::UInt8:
        Native.Kind = EFinareNativeValueKindV3::UInt8;
        Native.Payload0 = Value.UInt8Value;
        break;
    case EFinareValueKind::Int16:
        Native.Kind = EFinareNativeValueKindV3::Int16;
        Native.Payload0 = static_cast<uint64>(Value.Int16Value);
        break;
    case EFinareValueKind::UInt16:
        Native.Kind = EFinareNativeValueKindV3::UInt16;
        Native.Payload0 = Value.UInt16Value;
        break;
    case EFinareValueKind::Int32:
        Native.Kind = EFinareNativeValueKindV3::Int32;
        Native.Payload0 = static_cast<uint64>(Value.Int32Value);
        break;
    case EFinareValueKind::UInt32:
        Native.Kind = EFinareNativeValueKindV3::UInt32;
        Native.Payload0 = Value.UInt32Value;
        break;
    case EFinareValueKind::Int64:
        Native.Kind = EFinareNativeValueKindV3::Int64;
        Native.Payload0 = static_cast<uint64>(Value.Int64Value);
        break;
    case EFinareValueKind::UInt64:
        Native.Kind = EFinareNativeValueKindV3::UInt64;
        Native.Payload0 = Value.UInt64Value;
        break;
    case EFinareValueKind::Float32:
        Native.Kind = EFinareNativeValueKindV3::Float32;
        Native.Payload0 = std::bit_cast<uint32>(Value.Float32Value);
        break;
    case EFinareValueKind::Float64:
        Native.Kind = EFinareNativeValueKindV3::Float64;
        Native.Payload0 = std::bit_cast<uint64>(Value.Float64Value);
        break;
    case EFinareValueKind::Enum:
        Native.Kind = EFinareNativeValueKindV3::Enum;
        Native.TypeId = Value.TypeIdValue;
        Native.Payload0 = static_cast<uint64>(Value.Int64Value);
        break;
    case EFinareValueKind::String:
    case EFinareValueKind::Name:
    case EFinareValueKind::Text:
    {
        const FString Text = Value.Kind == EFinareValueKind::String
            ? Value.StringValue
            : Value.Kind == EFinareValueKind::Name
                ? Value.NameValue.ToString()
                : Value.TextValue.ToString();
        const FTCHARToUTF8 Utf8(*Text);
        const uint32 Utf8Length = static_cast<uint32>(Utf8.Length());
        if (ByteCursor > Frame.ByteCapacity ||
            Utf8Length > Frame.ByteCapacity - ByteCursor)
        {
            return false;
        }
        Native.Kind = Value.Kind == EFinareValueKind::String
            ? EFinareNativeValueKindV3::String
            : Value.Kind == EFinareValueKind::Name
                ? EFinareNativeValueKindV3::Name
                : EFinareNativeValueKindV3::Text;
        Native.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
        Native.Payload0 = ByteCursor;
        Native.Payload1 = Utf8Length;
        if (Utf8Length > 0)
        {
            FMemory::Memcpy(Frame.ByteBuffer + ByteCursor, Utf8.Get(), Utf8Length);
        }
        ByteCursor += Utf8Length;
        break;
    }
    case EFinareValueKind::Object:
        Native.Kind = EFinareNativeValueKindV3::Object;
        Native.Payload0 = Value.ObjectHandleValue.Value;
        break;
    case EFinareValueKind::Class:
        Native.Kind = EFinareNativeValueKindV3::Class;
        Native.Payload0 = Value.ObjectHandleValue.Value;
        break;
    case EFinareValueKind::Vector:
        Native.Kind = EFinareNativeValueKindV3::Vector3;
        Native.Payload0 = std::bit_cast<uint64>(Value.VectorValue.X);
        Native.Payload1 = std::bit_cast<uint64>(Value.VectorValue.Y);
        Native.Payload2 = std::bit_cast<uint64>(Value.VectorValue.Z);
        break;
    case EFinareValueKind::Rotator:
        Native.Kind = EFinareNativeValueKindV3::Rotator3;
        Native.Payload0 = std::bit_cast<uint64>(Value.RotatorValue.Pitch);
        Native.Payload1 = std::bit_cast<uint64>(Value.RotatorValue.Yaw);
        Native.Payload2 = std::bit_cast<uint64>(Value.RotatorValue.Roll);
        break;
    case EFinareValueKind::Transform:
    {
        if (ByteCursor > Frame.ByteCapacity ||
            V3TransformByteSize > Frame.ByteCapacity - ByteCursor)
        {
            return false;
        }
        Native.Kind = EFinareNativeValueKindV3::Transform;
        Native.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
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
    case EFinareValueKind::Struct:
    case EFinareValueKind::Array:
    case EFinareValueKind::Set:
    case EFinareValueKind::Map:
    {
        if (Value.TypeIdValue == 0 ||
            (Value.Kind == EFinareValueKind::Map &&
                Value.ContainerValues.Num() % 2 != 0))
        {
            return false;
        }
        const uint64 DescriptorByteCount =
            static_cast<uint64>(Value.ContainerValues.Num()) *
            sizeof(FFinareNativeValueV3);
        if (ByteCursor > Frame.ByteCapacity ||
            DescriptorByteCount > Frame.ByteCapacity - ByteCursor)
        {
            return false;
        }
        const uint32 DescriptorOffset = ByteCursor;
        ByteCursor += static_cast<uint32>(DescriptorByteCount);
        for (int32 Index = 0; Index < Value.ContainerValues.Num(); ++Index)
        {
            FFinareNativeValueV3 Element;
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
                    Index * sizeof(FFinareNativeValueV3),
                &Element,
                sizeof(Element));
        }
        Native.Kind = Value.Kind == EFinareValueKind::Struct
            ? EFinareNativeValueKindV3::Struct
            : Value.Kind == EFinareValueKind::Array
                ? EFinareNativeValueKindV3::Array
                : Value.Kind == EFinareValueKind::Set
                    ? EFinareNativeValueKindV3::Set
                    : EFinareNativeValueKindV3::Map;
        Native.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
        Native.TypeId = Value.TypeIdValue;
        Native.Payload0 = DescriptorOffset;
        Native.Payload1 = DescriptorByteCount;
        Native.Payload2 = Value.Kind == EFinareValueKind::Map
            ? Value.ContainerValues.Num() / 2
            : Value.ContainerValues.Num();
        Native.Payload3 = sizeof(FFinareNativeValueV3);
        break;
    }
    default:
        return false;
    }
    return true;
}

bool IsFlatOwnedCollectionValue(const FFinareValue& Value)
{
    // Property state collections are deliberately flat in this tranche. A
    // nested container/struct must not accidentally become a generic V3 DAG
    // accepted by the legacy property-path lane.
    return Value.Kind != EFinareValueKind::Struct &&
        Value.Kind != EFinareValueKind::Array &&
        Value.Kind != EFinareValueKind::Set &&
        Value.Kind != EFinareValueKind::Map;
}

bool EncodeContainerPayloadV3(
    const FFinareValue& Value,
    TArray<uint8>& OutPayload)
{
    if ((Value.Kind != EFinareValueKind::Array &&
         Value.Kind != EFinareValueKind::Set &&
         Value.Kind != EFinareValueKind::Map) ||
        Value.TypeIdValue == 0 ||
        (Value.Kind == EFinareValueKind::Map &&
            Value.ContainerValues.Num() % 2 != 0))
    {
        return false;
    }
    for (const FFinareValue& Child : Value.ContainerValues)
    {
        if (!IsFlatOwnedCollectionValue(Child))
        {
            return false;
        }
    }

    constexpr uint32 RootSize = sizeof(FFinareNativeValueV3);
    if (FFinareByteBuffer::MaximumCallBytes < RootSize)
    {
        return false;
    }
    const uint32 MaximumArena =
        FFinareByteBuffer::MaximumCallBytes - RootSize;
    uint32 RequiredBytes = 0;
    if (!MeasureValueV3(Value, RequiredBytes) || RequiredBytes > MaximumArena)
    {
        return false;
    }
    TArray<uint8> Arena;
    Arena.SetNumZeroed(static_cast<int32>(RequiredBytes));
    FFinareNativeCallFrameV3 Frame;
    Frame.ByteBuffer = Arena.GetData();
    Frame.ByteCapacity = RequiredBytes;
    FFinareNativeValueV3 Root;
    uint32 ByteCursor = 0;
    if (!EncodeValueV3(Value, Frame, Root, ByteCursor) ||
        ByteCursor != RequiredBytes)
    {
        return false;
    }

    OutPayload.SetNumUninitialized(static_cast<int32>(RootSize + ByteCursor));
    FMemory::Memcpy(OutPayload.GetData(), &Root, RootSize);
    if (ByteCursor > 0)
    {
        FMemory::Memcpy(OutPayload.GetData() + RootSize, Arena.GetData(), ByteCursor);
    }
    return true;
}

bool DecodeContainerPayloadV3(
    const FFinareNativeValueV1& NativeValue,
    FFinareValue& OutValue)
{
    constexpr uint32 RootSize = sizeof(FFinareNativeValueV3);
    if (NativeValue.StringData == nullptr ||
        NativeValue.StringLength < static_cast<int32>(RootSize) ||
        NativeValue.StringLength > static_cast<int32>(FFinareByteBuffer::MaximumCallBytes))
    {
        return false;
    }
    FFinareNativeValueV3 Root;
    FMemory::Memcpy(&Root, NativeValue.StringData, RootSize);
    const EFinareValueKind ExpectedKind =
        static_cast<EFinareValueKind>(NativeValue.Kind);
    const EFinareNativeValueKindV3 ExpectedNativeKind =
        ExpectedKind == EFinareValueKind::Array
            ? EFinareNativeValueKindV3::Array
            : ExpectedKind == EFinareValueKind::Set
                ? EFinareNativeValueKindV3::Set
                : EFinareNativeValueKindV3::Map;
    if (Root.Kind != ExpectedNativeKind)
    {
        return false;
    }
    const uint64 CellsPerEntry = ExpectedKind == EFinareValueKind::Map ? 2ull : 1ull;
    if (Root.Reserved16 != 0 || Root.Auxiliary != 0 ||
        Root.Flags != EFinareNativeValueFlagsV3::ArenaBacked ||
        Root.TypeId == 0 || Root.Payload3 != sizeof(FFinareNativeValueV3) ||
        Root.Payload2 > MAX_int32 ||
        Root.Payload2 > MAX_uint64 / CellsPerEntry)
    {
        return false;
    }
    const uint64 CellCount = Root.Payload2 * CellsPerEntry;
    if (CellCount > MAX_uint32 / sizeof(FFinareNativeValueV3) ||
        Root.Payload1 != CellCount * sizeof(FFinareNativeValueV3))
    {
        return false;
    }
    const uint32 ArenaBytes =
        static_cast<uint32>(NativeValue.StringLength - RootSize);
    if (Root.Payload0 > ArenaBytes || Root.Payload1 > ArenaBytes - Root.Payload0)
    {
        return false;
    }
    const uint8* DescriptorData =
        NativeValue.StringData + RootSize + Root.Payload0;
    for (uint64 Index = 0; Index < CellCount; ++Index)
    {
        FFinareNativeValueV3 Cell;
        FMemory::Memcpy(
            &Cell,
            DescriptorData + Index * sizeof(FFinareNativeValueV3),
            sizeof(Cell));
        if (Cell.Kind == EFinareNativeValueKindV3::Struct ||
            Cell.Kind == EFinareNativeValueKindV3::Array ||
            Cell.Kind == EFinareNativeValueKindV3::Set ||
            Cell.Kind == EFinareNativeValueKindV3::Map)
        {
            return false;
        }
    }
    FFinareNativeCallFrameV3 Frame;
    Frame.ByteBuffer = const_cast<uint8*>(NativeValue.StringData + RootSize);
    Frame.ByteCapacity = ArenaBytes;
    Frame.ByteCount = ArenaBytes;
    if (!DecodeValueV3(Root, Frame, ArenaBytes, OutValue) ||
        OutValue.Kind != ExpectedKind)
    {
        return false;
    }
    for (const FFinareValue& Child : OutValue.ContainerValues)
    {
        if (!IsFlatOwnedCollectionValue(Child))
        {
            return false;
        }
    }
    return true;
}

bool PreflightResultV3(
    FFinareNativeCallFrameV3& Frame,
    uint32 EntryByteCount,
    EFinareValueKind ExpectedKind,
    uint64 ExpectedTypeId)
{
    const uint32 ExpectedResultCount =
        ExpectedKind == EFinareValueKind::Void ? 0u : 1u;
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
        (ExpectedKind == EFinareValueKind::Transform ? V3TransformByteSize : 0u);
    if (RequiredBytes > Frame.ByteCapacity)
    {
        ResetV3Output(Frame, EntryByteCount);
        Frame.RequiredResultCapacity = ExpectedResultCount;
        Frame.RequiredByteCapacity = RequiredBytes;
        Frame.Status = EFinareNativeCallStatusV3::BufferTooSmall;
        return false;
    }
    return true;
}

void ReadPropertyByIdV3(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    FFinareNativeCallFrameV3* Frame)
{
    uint32 EntryByteCount = Frame != nullptr ? Frame->ByteCount : 0;
    if (!ValidateFrameV3(Frame, 0, 1, EntryByteCount))
    {
        return;
    }
    EFinareValueKind ExpectedKind;
    uint64 ExpectedTypeId = 0;
    if (!FFinareGeneratedThunkRegistry::DescribeReadResult(
            MemberId, ExpectedKind, ExpectedTypeId))
    {
        FFinareInvocationResult Result;
        Result.Error = EFinareInvocationError::InvalidMemberPath;
        InvocationFailureV3(*Frame, EntryByteCount, Result);
        return;
    }
    EFinareNativeValueKindV3 SupportedKind;
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
        static_cast<FFinareNativeBridgeContext*>(Context)->Execute(
            [&](FFinareInvocationBridge& Bridge)
            {
                FFinareValue Value;
                FFinareInvocationResult Result;
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
                    Frame->Status = EFinareNativeCallStatusV3::BufferTooSmall;
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
                Frame->Status = EFinareNativeCallStatusV3::Success;
            });
    if (!bExecuted)
    {
        ResetV3Output(*Frame, EntryByteCount);
        Frame->Status = EFinareNativeCallStatusV3::TransportUnavailable;
    }
}

void WritePropertyByIdV3(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    FFinareNativeCallFrameV3* Frame)
{
    uint32 EntryByteCount = Frame != nullptr ? Frame->ByteCount : 0;
    if (!ValidateFrameV3(Frame, 1, 0, EntryByteCount))
    {
        return;
    }
    FFinareValue Value;
    if (!DecodeValueV3(Frame->Arguments[0], *Frame, EntryByteCount, Value))
    {
        InvalidFrameV3(Frame, EntryByteCount);
        return;
    }
    const bool bExecuted = Context != nullptr &&
        static_cast<FFinareNativeBridgeContext*>(Context)->Execute(
            [&](FFinareInvocationBridge& Bridge)
            {
                FFinareInvocationResult Result;
                if (!Bridge.WritePropertyById({Target}, MemberId, Value, Result))
                {
                    InvocationFailureV3(*Frame, EntryByteCount, Result);
                    return;
                }
                ResetV3Output(*Frame, EntryByteCount);
                Frame->Status = EFinareNativeCallStatusV3::Success;
            });
    if (!bExecuted)
    {
        ResetV3Output(*Frame, EntryByteCount);
        Frame->Status = EFinareNativeCallStatusV3::TransportUnavailable;
    }
}

void InvokeByIdV3Core(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    FFinareNativeCallFrameV3* Frame,
    uint64 CorrelationId)
{
    uint32 EntryByteCount = Frame != nullptr ? Frame->ByteCount : 0;
    if (Frame == nullptr || Frame->ArgumentCount > 64)
    {
        InvalidFrameV3(Frame, EntryByteCount);
        return;
    }
    const uint32 ParentFlag =
        static_cast<uint32>(EFinareNativeCallFrameFlagsV3::BlueprintParent);
    const bool bParentCall = Frame->Flags == ParentFlag;
    if (Frame->Flags != 0 && Frame->Flags != ParentFlag)
    {
        InvalidFrameV3(Frame, EntryByteCount);
        return;
    }
    if (bParentCall && CorrelationId == 0)
    {
        FFinareInvocationResult Result;
        Result.Error = EFinareInvocationError::FunctionNotCallable;
        Result.Message = TEXT("Blueprint parent calls require a correlated ABI v3 invocation.");
        InvocationFailureV3(*Frame, EntryByteCount, Result);
        return;
    }
    uint32 ExpectedResultCount = 0;
    if (bParentCall)
    {
        ExpectedResultCount = Frame->ResultCount;
    }
    else if (!FFinareGeneratedThunkRegistry::DescribeInvokeResultCount(
                 MemberId, ExpectedResultCount))
    {
        FFinareInvocationResult Result;
        Result.Error = EFinareInvocationError::InvalidMemberPath;
        InvocationFailureV3(*Frame, EntryByteCount, Result);
        return;
    }
    if (ExpectedResultCount > 64)
    {
        FFinareInvocationResult Result;
        Result.Error = EFinareInvocationError::InvalidMemberPath;
        InvocationFailureV3(*Frame, EntryByteCount, Result);
        return;
    }
    if (!ValidateFrameV3(
            Frame,
            Frame->ArgumentCount,
            ExpectedResultCount,
            EntryByteCount,
            bParentCall
                ? EFinareNativeCallFrameFlagsV3::BlueprintParent
                : EFinareNativeCallFrameFlagsV3::None))
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
        if (Argument.Kind != EFinareNativeValueKindV3::ByteBuffer) continue;
        bHasBinaryPayload = true;
        // Check before decoding: repeated offsets must not amplify allocation.
        if (Argument.Payload1 > FFinareByteBuffer::MaximumLength ||
            EntryByteCount > FFinareByteBuffer::MaximumCallBytes ||
            BinaryInputBytes + Argument.Payload1 > FFinareByteBuffer::MaximumCallBytes)
        {
            InvalidFrameV3(Frame, EntryByteCount);
            return;
        }
        BinaryInputBytes += Argument.Payload1;
    }
    for (uint32 ResultIndex = 0;
         ResultIndex < ExpectedResultCount && !bParentCall;
         ++ResultIndex)
    {
        EFinareValueKind ExpectedKind;
        uint64 ExpectedTypeId = 0;
        if (!FFinareGeneratedThunkRegistry::DescribeInvokeResult(
                MemberId, ResultIndex, ExpectedKind, ExpectedTypeId))
        {
            InvalidFrameV3(Frame, EntryByteCount);
            return;
        }
        EFinareNativeValueKindV3 SupportedKind;
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
        bHasBinaryPayload |= ExpectedKind == EFinareValueKind::ByteBuffer;
        if (ExpectedKind == EFinareValueKind::Transform)
        {
            if (RequiredByteCapacity > MAX_uint32 - V3TransformByteSize)
            {
                InvalidFrameV3(Frame, EntryByteCount);
                return;
            }
            RequiredByteCapacity += V3TransformByteSize;
        }
        else if (
            ExpectedKind == EFinareValueKind::Name ||
            ExpectedKind == EFinareValueKind::String ||
            ExpectedKind == EFinareValueKind::Text ||
            ExpectedKind == EFinareValueKind::Struct ||
            ExpectedKind == EFinareValueKind::Array ||
            ExpectedKind == EFinareValueKind::Set ||
            ExpectedKind == EFinareValueKind::Map ||
            ExpectedKind == EFinareValueKind::ByteBuffer)
        {
            bRequiresRequestHash = true;
        }
    }
    if (RequiredByteCapacity > Frame->ByteCapacity)
    {
        ResetV3Output(*Frame, EntryByteCount);
        Frame->RequiredResultCapacity = ExpectedResultCount;
        Frame->RequiredByteCapacity = RequiredByteCapacity;
        Frame->Status = EFinareNativeCallStatusV3::BufferTooSmall;
        return;
    }
    TArray<FFinareValue, TInlineAllocator<8>> Arguments;
    Arguments.Reserve(Frame->ArgumentCount);
    for (uint32 Index = 0; Index < Frame->ArgumentCount && !bParentCall; ++Index)
    {
        FFinareValue Value;
        if (!DecodeValueV3(Frame->Arguments[Index], *Frame, EntryByteCount, Value))
        {
            InvalidFrameV3(Frame, EntryByteCount);
            return;
        }
        Arguments.Add(MoveTemp(Value));
    }
    const uint64 RequestHash = bParentCall || bRequiresRequestHash
        ? HashRequestV3(*Frame, EntryByteCount)
        : 0;
    const bool bExecuted = Context != nullptr &&
        static_cast<FFinareNativeBridgeContext*>(Context)->ExecuteCallFrame(
            [&](FFinareInvocationBridge& Bridge,
                FFinareNativeBridgeContext::FPendingCallFrameResult& Pending)
            {
                FFinareValue ReturnValue;
                TArray<FFinareValue> OutputValues;
                FFinareInvocationResult Result;
                const uint8 ExpectedOperation = bParentCall ? 3 : 2;
                const bool bUsePending = Pending.bValid &&
                    Pending.Operation == ExpectedOperation &&
                    Pending.Target == Target && Pending.MemberId == MemberId &&
                    Pending.RequestHash == RequestHash;
                if (CorrelationId != 0 && Pending.bValid && !bUsePending)
                {
                    InvalidFrameV3(Frame, EntryByteCount);
                    return; // An identity cannot be reused for a different pending request.
                }
                if (bParentCall)
                {
                    auto PublishParentResult = [&](
                        const TArray<uint8>& ResultCells,
                        const TArray<uint8>& Arena)
                    {
                        const uint32 CellBytes = ExpectedResultCount *
                            sizeof(FFinareNativeValueV3);
                        if (ResultCells.Num() != static_cast<int32>(CellBytes) ||
                            Arena.Num() < static_cast<int32>(EntryByteCount) ||
                            Arena.Num() > static_cast<int32>(FFinareByteBuffer::MaximumCallBytes))
                        {
                            Pending.Reset();
                            UnsupportedValueV3(*Frame, EntryByteCount);
                            return;
                        }
                        if (CellBytes > 0)
                        {
                            FMemory::Memcpy(
                                Frame->Results,
                                ResultCells.GetData(),
                                CellBytes);
                        }
                        const uint32 PublishedByteCount = Arena.Num();
                        if (PublishedByteCount > 0)
                        {
                            FMemory::Memcpy(
                                Frame->ByteBuffer,
                                Arena.GetData(),
                                PublishedByteCount);
                        }
                        Pending.Reset();
                        ResetV3Output(*Frame, EntryByteCount);
                        Frame->ResultCount = ExpectedResultCount;
                        Frame->ByteCount = PublishedByteCount;
                        Frame->Status = EFinareNativeCallStatusV3::Success;
                    };

                    if (bUsePending)
                    {
                        if (Pending.ParentArena.Num() > static_cast<int32>(Frame->ByteCapacity))
                        {
                            ResetV3Output(*Frame, EntryByteCount);
                            Frame->RequiredResultCapacity = ExpectedResultCount;
                            Frame->RequiredByteCapacity = Pending.ParentArena.Num();
                            Frame->Status = EFinareNativeCallStatusV3::BufferTooSmall;
                            return;
                        }
                        PublishParentResult(Pending.ParentResultCells, Pending.ParentArena);
                        return;
                    }

                    Pending.Reset();
                    FFinareParentCallResult ParentResult;
                    if (!Bridge.InvokeParentById(
                            {Target},
                            MemberId,
                            *Frame,
                            EntryByteCount,
                            ParentResult,
                            Result))
                    {
                        InvocationFailureV3(*Frame, EntryByteCount, Result);
                        return;
                    }
                    if (ParentResult.ResultCells.Num() != static_cast<int32>(
                            ExpectedResultCount * sizeof(FFinareNativeValueV3)) ||
                        ParentResult.Arena.Num() < static_cast<int32>(EntryByteCount))
                    {
                        UnsupportedValueV3(*Frame, EntryByteCount);
                        return;
                    }
                    if (ParentResult.Arena.Num() > static_cast<int32>(Frame->ByteCapacity))
                    {
                        Pending.bValid = true;
                        Pending.Operation = ExpectedOperation;
                        Pending.Target = Target;
                        Pending.MemberId = MemberId;
                        Pending.RequestHash = RequestHash;
                        Pending.EncodedByteCount = ParentResult.Arena.Num();
                        Pending.ParentResultCells = MoveTemp(ParentResult.ResultCells);
                        Pending.ParentArena = MoveTemp(ParentResult.Arena);
                        ResetV3Output(*Frame, EntryByteCount);
                        Frame->RequiredResultCapacity = ExpectedResultCount;
                        Frame->RequiredByteCapacity = Pending.EncodedByteCount;
                        Frame->Status = EFinareNativeCallStatusV3::BufferTooSmall;
                        return;
                    }
                    PublishParentResult(ParentResult.ResultCells, ParentResult.Arena);
                    return;
                }
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
                    (ReturnValue.Kind == EFinareValueKind::Void ? 0u : 1u) +
                    static_cast<uint32>(OutputValues.Num());
                if (ProducedResultCount != ExpectedResultCount)
                {
                    Pending.Reset();
                    Result.Error = EFinareInvocationError::UnsupportedParameter;
                    InvocationFailureV3(*Frame, EntryByteCount, Result);
                    return;
                }
                uint32 MeasuredByteCapacity = EntryByteCount;
                bool bMeasured = ReturnValue.Kind == EFinareValueKind::Void ||
                    MeasureValueV3(ReturnValue, MeasuredByteCapacity);
                for (const FFinareValue& OutputValue : OutputValues)
                {
                    bMeasured = bMeasured &&
                        MeasureValueV3(OutputValue, MeasuredByteCapacity);
                }
                if (!bMeasured || (bHasBinaryPayload && MeasuredByteCapacity > FFinareByteBuffer::MaximumCallBytes))
                {
                    Pending.Reset();
                    UnsupportedValueV3(*Frame, EntryByteCount);
                    return;
                }
                if (MeasuredByteCapacity > Frame->ByteCapacity)
                {
                    Pending.bValid = true;
                    Pending.Operation = ExpectedOperation;
                    Pending.Target = Target;
                    Pending.MemberId = MemberId;
                    Pending.RequestHash = RequestHash;
                    Pending.EncodedByteCount = MeasuredByteCapacity;
                    Pending.ReturnValue = ReturnValue;
                    Pending.OutputValues = OutputValues;
                    ResetV3Output(*Frame, EntryByteCount);
                    Frame->RequiredResultCapacity = ExpectedResultCount;
                    Frame->RequiredByteCapacity = MeasuredByteCapacity;
                    Frame->Status = EFinareNativeCallStatusV3::BufferTooSmall;
                    return;
                }
                Pending.Reset();
                ResetV3Output(*Frame, EntryByteCount);
                uint32 ResultIndex = 0;
                uint32 ByteCursor = EntryByteCount;
                if (ReturnValue.Kind != EFinareValueKind::Void &&
                    !EncodeValueV3(
                        ReturnValue,
                        *Frame,
                        Frame->Results[ResultIndex++],
                        ByteCursor))
                {
                    UnsupportedValueV3(*Frame, EntryByteCount);
                    return;
                }
                for (const FFinareValue& OutputValue : OutputValues)
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
                Frame->Status = EFinareNativeCallStatusV3::Success;
            }, CorrelationId);
    if (!bExecuted)
    {
        ResetV3Output(*Frame, EntryByteCount);
        Frame->Status = EFinareNativeCallStatusV3::TransportUnavailable;
    }
}

void InvokeByIdV3(void* Context, uint64 Target, uint64 MemberId, FFinareNativeCallFrameV3* Frame)
{
    InvokeByIdV3Core(Context, Target, MemberId, Frame, 0);
}

void InvokeCorrelatedByIdV3(void* Context, uint64 CorrelationId, uint64 Target,
    uint64 MemberId, FFinareNativeCallFrameV3* Frame)
{
    if (CorrelationId == 0)
    {
        InvalidFrameV3(Frame, Frame ? Frame->ByteCount : 0);
        return;
    }
    InvokeByIdV3Core(Context, Target, MemberId, Frame, CorrelationId);
}

void ReleaseCorrelatedResultV3(void* Context, uint64 CorrelationId)
{
    if (Context && CorrelationId != 0)
        static_cast<FFinareNativeBridgeContext*>(Context)->ReleaseCorrelatedResult(CorrelationId);
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
        return static_cast<uint8>(EFinareNativeCallStatusV3::InvalidFrame);
    }

    *OutReturnValue = 0;
    *OutInvocationError = 0;
    *OutHandleError = 0;
    EFinareNativeCallStatusV3 Status =
        EFinareNativeCallStatusV3::TransportUnavailable;
    const bool bExecuted = Context != nullptr &&
        static_cast<FFinareNativeBridgeContext*>(Context)->ExecuteGameThreadFast(
            [&](FFinareInvocationBridge& Bridge)
            {
                FFinareInvocationResult Result;
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
                    Status = EFinareNativeCallStatusV3::InvocationFailure;
                    return;
                }
                Status = EFinareNativeCallStatusV3::Success;
            });
    return static_cast<uint8>(
        bExecuted ? Status : EFinareNativeCallStatusV3::TransportUnavailable);
}

uint8 InvokeFixedByIdV3(
    void* Context,
    uint64 Target,
    uint64 MemberId,
    const FFinareNativeValueV3* Arguments,
    uint32 ArgumentCount,
    FFinareNativeValueV3* OutReturnValue,
    uint8* OutInvocationError,
    uint8* OutHandleError)
{
    constexpr uint32 MaximumArgumentCount = 4;
    if (OutInvocationError == nullptr || OutHandleError == nullptr ||
        ArgumentCount > MaximumArgumentCount ||
        (ArgumentCount > 0 && Arguments == nullptr))
    {
        return static_cast<uint8>(EFinareNativeCallStatusV3::InvalidFrame);
    }

    *OutInvocationError = 0;
    *OutHandleError = 0;
    EFinareNativeCallStatusV3 Status =
        EFinareNativeCallStatusV3::TransportUnavailable;
    const bool bExecuted = Context != nullptr &&
        static_cast<FFinareNativeBridgeContext*>(Context)->ExecuteGameThreadFast(
            [&](FFinareInvocationBridge& Bridge)
            {
                FFinareInvocationResult Result;
                if (!Bridge.InvokeFixedById(
                        {Target},
                        MemberId,
                        MakeArrayView(Arguments, static_cast<int32>(ArgumentCount)),
                        OutReturnValue,
                        Result))
                {
                    *OutInvocationError = static_cast<uint8>(Result.Error);
                    *OutHandleError = static_cast<uint8>(Result.HandleError);
                    Status = EFinareNativeCallStatusV3::InvocationFailure;
                    return;
                }
                Status = EFinareNativeCallStatusV3::Success;
            });
    return static_cast<uint8>(
        bExecuted ? Status : EFinareNativeCallStatusV3::TransportUnavailable);
}

uint8 RetainContext(void* Context)
{
    return Context != nullptr &&
        static_cast<FFinareNativeBridgeContext*>(Context)->TryRetain()
        ? 1
        : 0;
}

void ReleaseContext(void* Context)
{
    if (Context != nullptr)
    {
        static_cast<FFinareNativeBridgeContext*>(Context)->Release();
    }
}

uint8 AcquireTransformSnapshot(
    void* Context,
    FFinareSharedSnapshotViewV4* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->AcquireTransformSnapshot(*OutView)
        ? 1
        : 0;
}

uint8 AcquireGeneratedSnapshot(
    void* Context,
    uint64 SchemaId,
    uint32 EntrySize,
    FFinareGeneratedDataViewV1* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->AcquireGeneratedSnapshot(SchemaId, EntrySize, *OutView)
        ? 1
        : 0;
}

uint8 GetSharedDataTelemetry(
    void* Context,
    FFinareSharedDataTelemetryV1* OutTelemetry,
    uint32 OutTelemetrySize)
{
    if (Context == nullptr || OutTelemetry == nullptr ||
        OutTelemetrySize < sizeof(uint32) * 2)
    {
        return 0;
    }
    FFinareSharedDataTelemetryV1 Current;
    if (!static_cast<FFinareNativeBridgeContext*>(Context)
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
    FFinareCommandBatchViewV4* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FFinareGameplayDataPlane& DataPlane)
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
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FFinareGameplayDataPlane& DataPlane)
        {
            return DataPlane.CommitCommandBatch(BatchToken, CommandCount);
        }) ? 1 : 0;
}

uint8 AcquireGameplayEvents(
    void* Context,
    FFinareGameplayEventViewV4* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FFinareGameplayDataPlane& DataPlane)
        {
            return DataPlane.AcquireEvents(*OutView);
        }) ? 1 : 0;
}

uint8 AcquireGeneratedEvents(
    void* Context,
    uint64 SchemaId,
    uint32 EntrySize,
    FFinareGeneratedDataViewV1* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FFinareGameplayDataPlane& DataPlane)
        {
            return DataPlane.AcquireGeneratedEvents(SchemaId, EntrySize, *OutView);
        }) ? 1 : 0;
}

uint8 BeginTraceBatch(
    void* Context,
    uint32 Capacity,
    FFinareTraceRequestViewV4* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FFinareGameplayDataPlane& DataPlane)
        {
            return DataPlane.BeginTraceBatch(Capacity, *OutView);
        }) ? 1 : 0;
}

uint8 ExecuteTraceBatch(
    void* Context,
    uint64 BatchToken,
    uint32 RequestCount,
    FFinareTraceResultViewV4* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FFinareGameplayDataPlane& DataPlane)
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
    static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FFinareGameplayDataPlane& DataPlane)
        {
            DataPlane.CancelTraceBatch(BatchToken);
            return true;
        });
}

uint8 BeginOverlapBatch(
    void* Context,
    uint32 Capacity,
    FFinareOverlapRequestViewV1* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FFinareGameplayDataPlane& DataPlane)
        {
            return DataPlane.BeginOverlapBatch(Capacity, *OutView);
        }) ? 1 : 0;
}

uint8 ExecuteOverlapBatch(
    void* Context,
    uint64 BatchToken,
    uint32 RequestCount,
    FFinareOverlapResultViewV1* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FFinareGameplayDataPlane& DataPlane)
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
    static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FFinareGameplayDataPlane& DataPlane)
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
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FFinareGameplayDataPlane& DataPlane)
        {
            *OutPhase = static_cast<uint8>(DataPlane.GetPhase());
            *OutFrameNumber = DataPlane.GetFrameNumber();
            return true;
        }) ? 1 : 0;
}

uint8 GetGameplayDataTelemetry(
    void* Context,
    FFinareGameplayDataTelemetryV1* OutTelemetry,
    uint32 OutTelemetrySize)
{
    if (Context == nullptr || OutTelemetry == nullptr ||
        OutTelemetrySize < sizeof(uint32) * 2)
    {
        return 0;
    }
    FFinareGameplayDataTelemetryV1 Current;
    const bool bRead = static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteGameplayData([&](FFinareGameplayDataPlane& DataPlane)
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
    const FFinareAsyncRequestV5* Request)
{
    if (Context == nullptr || Request == nullptr)
    {
        return 0;
    }
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteAsyncService([&](FFinareAsyncService& Service)
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
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteAsyncService([&](FFinareAsyncService& Service)
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
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteAsyncService([&](FFinareAsyncService& Service)
        {
            return Service.Cancel(OwnerInstanceId, OwnerGeneration, RequestId);
        }) ? 1 : 0;
}

uint8 PollAsyncCompletions(
    void* Context,
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    FFinareAsyncCompletionViewV5* OutView)
{
    if (Context == nullptr || OutView == nullptr)
    {
        return 0;
    }
    return static_cast<FFinareNativeBridgeContext*>(Context)
        ->ExecuteAsyncService([&](FFinareAsyncService& Service)
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
        static_cast<FFinareNativeBridgeContext*>(Context)
            ->ExecuteAsyncService([&](FFinareAsyncService& Service)
            {
                Canceled = Service.CancelOwner(OwnerInstanceId, OwnerGeneration);
                return true;
            });
    }
    return Canceled;
}
}

UFunction* UFinareDelegateProxy::FindOrCreateDispatchFunction(
    const UFunction* Signature)
{
    check(IsInGameThread());
    if (Signature == nullptr || Signature->GetReturnProperty() != nullptr)
    {
        return nullptr;
    }

    const uint64 SignatureId = FinareMemberId(
        TCHAR_TO_UTF8(*Signature->GetPathName()));
    const FName FunctionName(*FString::Printf(
        TEXT("FinareEvent_%016llX"),
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
    Function->SetNativeFunc(&UFinareDelegateProxy::execDispatch);
    ProxyClass->AddFunctionToFunctionMap(Function, FunctionName);
    return Function->IsSignatureCompatibleWith(Signature) ? Function : nullptr;
}

bool UFinareDelegateProxy::Bind(
    FFinareNativeBridgeContext* InContext,
    uint64 InSubscriptionId,
    FFinareObjectHandle InSourceHandle,
    uint64 InEventId,
    UObject* InSource,
    const FMulticastDelegateProperty* InProperty,
    FFinareNativeEventCallbackV4 InCallback,
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

void UFinareDelegateProxy::Unbind()
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

DEFINE_FUNCTION(UFinareDelegateProxy::execDispatch)
{
    UFinareDelegateProxy* Proxy = CastChecked<UFinareDelegateProxy>(Context);
    Proxy->Dispatch(Stack.Node, Stack.Locals);
}

void UFinareDelegateProxy::Dispatch(
    const UFunction* DispatchFunction,
    const void* ParameterMemory)
{
    using namespace UE::Finare::NativeTransport::Private;
    check(IsInGameThread());
    FFinareNativeBridgeContext* EventContext = Context;
    FFinareNativeEventCallbackV4 EventCallback = Callback;
    void* EventUserData = UserData;
    const uint64 LocalSubscriptionId = SubscriptionId;
    const FFinareObjectHandle LocalSourceHandle = SourceHandle;
    const uint64 LocalEventId = EventId;
    if (EventContext == nullptr || EventCallback == nullptr ||
        DispatchFunction == nullptr ||
        (DispatchFunction->ParmsSize > 0 && ParameterMemory == nullptr) ||
        !EventContext->TryRetain())
    {
        return;
    }
    ON_SCOPE_EXIT
    {
        EventContext->Release();
    };

    TArray<FFinareValue> Arguments;
    FFinareInvocationResult Result;
    const bool bRead = EventContext->Execute(
        [&](FFinareInvocationBridge& Bridge)
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
            TEXT("Finare event argument marshalling failed: %s"),
            *Result.Message);
        return;
    }

    ON_SCOPE_EXIT
    {
        EventContext->Execute(
            [&](FFinareInvocationBridge& Bridge)
            {
                Bridge.ReleaseEventArguments(Arguments);
            });
    };

    TArray<FFinareNativeValueV3> NativeArguments;
    NativeArguments.SetNum(Arguments.Num());
    TArray<uint8> ByteBuffer;
    bool bEncoded = false;
    uint32 ByteCount = 0;
    constexpr uint32 MaximumByteCapacity = 16u * 1024u * 1024u;
    for (uint32 Capacity = 4096; Capacity <= MaximumByteCapacity; Capacity *= 2)
    {
        ByteBuffer.SetNumUninitialized(static_cast<int32>(Capacity));
        FFinareNativeCallFrameV3 EncodingFrame;
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
        UE_LOG(LogTemp, Error, TEXT("Finare event payload exceeded the ABI v4 limit."));
        return;
    }

    FFinareNativeEventFrameV4 Frame;
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

bool FFinareNativeBridgeContext::SubscribeEvent(
    FFinareObjectHandle Source,
    uint64 EventId,
    const FString& EventPath,
    FFinareNativeEventCallbackV4 Callback,
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
            FFinareInvocationResult Result;
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
                TStrongObjectPtr<UObject> Proxy(NewObject<UFinareDelegateProxy>());
                if (CastChecked<UFinareDelegateProxy>(Proxy.Get())->Bind(
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

bool FFinareNativeBridgeContext::UnsubscribeEvent(uint64 SubscriptionId)
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
    CastChecked<UFinareDelegateProxy>(Subscription.Get())->Unbind();
    Release();
    return true;
}

namespace UE::Finare::NativeTransport::Private
{
uint64 SubscribeEvent(
    void* Context,
    uint64 Source,
    uint64 EventId,
    const uint8* EventPathData,
    int32 EventPathLength,
    FFinareNativeEventCallbackV4 Callback,
    void* UserData)
{
    FString EventPath;
    if (Context == nullptr || Source == 0 || EventId == 0 || Callback == nullptr ||
        !TryDecodeUtf8(EventPathData, EventPathLength, EventPath) ||
        EventPath.IsEmpty() ||
        FinareMemberId(TCHAR_TO_UTF8(*EventPath)) != EventId)
    {
        return 0;
    }
    uint64 SubscriptionId = 0;
    return static_cast<FFinareNativeBridgeContext*>(Context)->SubscribeEvent(
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
        static_cast<FFinareNativeBridgeContext*>(Context)->UnsubscribeEvent(
            SubscriptionId)
        ? 1
        : 0;
}
}

extern "C" const FFinareNativeTransportV1*
Finare_GetNativeTransportV1()
{
    using namespace UE::Finare::NativeTransport::Private;
    static const FFinareNativeTransportV1 Transport = {
        1,
        sizeof(FFinareNativeTransportV1),
        sizeof(FFinareNativeValueV1),
        0,
        &ReadProperty,
        &WriteProperty,
        &Invoke,
        &RetainContext,
        &ReleaseContext,
    };
    return &Transport;
}

extern "C" const FFinareNativeTransportV2*
Finare_GetNativeTransportV2()
{
    using namespace UE::Finare::NativeTransport::Private;
    static const FFinareNativeTransportV2 Transport = {
        2,
        sizeof(FFinareNativeTransportV2),
        sizeof(FFinareNativeValueV1),
        0,
        &ReadProperty,
        &WriteProperty,
        &Invoke,
        &RetainContext,
        &ReleaseContext,
        &ReadPropertyById,
        &WritePropertyById,
        &InvokeById,
        Finare_GetNativeTransportV3(),
        Finare_GetNativeEventTransportV4(),
        Finare_GetSharedDataTransportV4(),
        Finare_GetGameplayDataTransportV4(),
        Finare_GetAsyncTransportV5(),
        Finare_GetNativeObjectTransportV1(),
        Finare_GetCallbackTransport(),
        Finare_GetUObjectReferenceTransportV1(),
    };
    return &Transport;
}

extern "C" const FFinareAsyncTransportV5*
Finare_GetAsyncTransportV5()
{
    using namespace UE::Finare::NativeTransport::Private;
    constexpr uint64 Capabilities =
        static_cast<uint64>(EFinareAsyncCapabilitiesV5::Delay) |
        static_cast<uint64>(EFinareAsyncCapabilitiesV5::AsyncAssetLoad) |
        static_cast<uint64>(EFinareAsyncCapabilitiesV5::Cancellation) |
        static_cast<uint64>(EFinareAsyncCapabilitiesV5::Timeout) |
        static_cast<uint64>(EFinareAsyncCapabilitiesV5::GenerationOwnership) |
        static_cast<uint64>(EFinareAsyncCapabilitiesV5::WorldTeardown);
    static const FFinareAsyncTransportV5 Transport = {
        UE::Finare::AsyncService::AbiVersion,
        sizeof(FFinareAsyncTransportV5),
        sizeof(FFinareAsyncRequestV5),
        sizeof(FFinareAsyncCompletionV5),
        sizeof(FFinareAsyncCompletionViewV5),
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

extern "C" const FFinareGameplayDataTransportV4*
Finare_GetGameplayDataTransportV4()
{
    using namespace UE::Finare::NativeTransport::Private;
    constexpr uint64 Capabilities =
        static_cast<uint64>(EFinareGameplayDataCapabilitiesV4::WorldPhaseScheduler) |
        static_cast<uint64>(EFinareGameplayDataCapabilitiesV4::CommandBuffer) |
        static_cast<uint64>(EFinareGameplayDataCapabilitiesV4::EventBuffer) |
        static_cast<uint64>(EFinareGameplayDataCapabilitiesV4::BatchLineTrace) |
        static_cast<uint64>(EFinareGameplayDataCapabilitiesV4::BatchSphereTrace) |
        static_cast<uint64>(EFinareGameplayDataCapabilitiesV4::BatchBoxTrace) |
        static_cast<uint64>(
            EFinareGameplayDataCapabilitiesV4::SchemaGeneratedLayouts) |
        static_cast<uint64>(EFinareGameplayDataCapabilitiesV4::CapacityTelemetry) |
        static_cast<uint64>(EFinareGameplayDataCapabilitiesV4::BatchSphereOverlap) |
        static_cast<uint64>(EFinareGameplayDataCapabilitiesV4::BatchBoxOverlap) |
        static_cast<uint64>(EFinareGameplayDataCapabilitiesV4::GeneratedEventChannels);
    static const FFinareGameplayDataTransportV4 Transport = {
        UE::Finare::GameplayData::AbiVersion,
        sizeof(FFinareGameplayDataTransportV4),
        sizeof(FFinareCommandV4),
        sizeof(FFinareGameplayEventV4),
        sizeof(FFinareTraceRequestV4),
        sizeof(FFinareTraceResultV4),
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
        UE::Finare::DataPlaneSchema::CompatibilityId,
        UE::Finare::DataPlaneSchema::Revision,
        UE::Finare::GameplayData::MaximumCommandCount,
        UE::Finare::GameplayData::MaximumEventCount,
        UE::Finare::GameplayData::MaximumTraceCount,
        &GetGameplayDataTelemetry,
        sizeof(FFinareOverlapRequestV1),
        sizeof(FFinareOverlapResultV1),
        UE::Finare::GameplayData::MaximumOverlapCount,
        0,
        &BeginOverlapBatch,
        &ExecuteOverlapBatch,
        &CancelOverlapBatch,
        sizeof(FFinareGeneratedDataViewV1),
        UE::Finare::GameplayData::MaximumGeneratedEventCount,
        UE::Finare::GameplayData::MaximumGeneratedChannelBytes,
        &AcquireGeneratedEvents,
    };
    return &Transport;
}

extern "C" const FFinareSharedDataTransportV4*
Finare_GetSharedDataTransportV4()
{
    using namespace UE::Finare::NativeTransport::Private;
    constexpr uint64 Capabilities =
        static_cast<uint64>(EFinareSharedDataCapabilitiesV4::TransformSnapshot) |
        static_cast<uint64>(EFinareSharedDataCapabilitiesV4::DoubleBuffered) |
        static_cast<uint64>(EFinareSharedDataCapabilitiesV4::WorldScopedHandles) |
        static_cast<uint64>(EFinareSharedDataCapabilitiesV4::SchemaGeneratedLayouts) |
        static_cast<uint64>(EFinareSharedDataCapabilitiesV4::CapacityTelemetry) |
        static_cast<uint64>(
            EFinareSharedDataCapabilitiesV4::GeneratedSnapshotChannels);
    static const FFinareSharedDataTransportV4 Transport = {
        UE::Finare::SharedData::AbiVersion,
        sizeof(FFinareSharedDataTransportV4),
        sizeof(FFinareSharedSnapshotViewV4),
        sizeof(FFinareTransformSnapshotEntryV4),
        UE::Finare::SharedData::TransformSnapshotSchemaId,
        Capabilities,
        &RetainContext,
        &ReleaseContext,
        &AcquireTransformSnapshot,
        UE::Finare::DataPlaneSchema::CompatibilityId,
        UE::Finare::DataPlaneSchema::Revision,
        UE::Finare::SharedData::MaximumTransformSnapshotCount,
        &GetSharedDataTelemetry,
        sizeof(FFinareGeneratedDataViewV1),
        UE::Finare::SharedData::MaximumGeneratedSnapshotCount,
        UE::Finare::SharedData::MaximumGeneratedChannelBytes,
        &AcquireGeneratedSnapshot,
    };
    return &Transport;
}

extern "C" const FFinareNativeEventTransportV4*
Finare_GetNativeEventTransportV4()
{
    using namespace UE::Finare::NativeTransport::Private;
    static const FFinareNativeEventTransportV4 Transport = {
        4,
        sizeof(FFinareNativeEventTransportV4),
        sizeof(FFinareNativeEventFrameV4),
        sizeof(FFinareNativeValueV3),
        &RetainContext,
        &ReleaseContext,
        &SubscribeEvent,
        &UnsubscribeEvent,
    };
    return &Transport;
}

extern "C" const FFinareNativeTransportV3*
Finare_GetNativeTransportV3()
{
    using namespace UE::Finare::NativeTransport::Private;
    constexpr uint64 Capabilities =
        static_cast<uint64>(EFinareNativeCapabilitiesV3::FixedScalars) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::Enums) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::ObjectHandles) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::VectorAndRotator) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::Transform) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::Utf8Text) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::Containers) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::OutputAndReference) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::Utf8Results) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::ContainerResults) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::Structs) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::ByteBuffers) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::CallbackArguments) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::CorrelatedResults) |
        static_cast<uint64>(EFinareNativeCapabilitiesV3::BlueprintParentCalls);

    static const FFinareNativeTransportV3 Transport = {
        3,
        sizeof(FFinareNativeTransportV3),
        sizeof(FFinareNativeValueV3),
        sizeof(FFinareNativeCallFrameV3),
        Capabilities,
        &RetainContext,
        &ReleaseContext,
        &ReadPropertyByIdV3,
        &WritePropertyByIdV3,
        &InvokeByIdV3,
        &InvokeInt32x2ByIdV3,
        &InvokeFixedByIdV3,
        &InvokeCorrelatedByIdV3,
        &ReleaseCorrelatedResultV3,
    };
    return &Transport;
}

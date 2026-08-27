#include "UnifiedScriptAsyncService.h"

#include "Engine/AssetManager.h"
#include "Engine/StreamableManager.h"
#include "Engine/World.h"
#include "UnifiedScriptObjectRegistry.h"
#include "UObject/SoftObjectPath.h"
#include "UObject/StrongObjectPtr.h"

namespace UE::UnifiedScript::AsyncService::Private
{
bool TryDecodeUtf8(const uint8* Data, uint32 Length, FString& OutValue)
{
    if ((Length > 0 && Data == nullptr) || Length > static_cast<uint32>(MAX_int32))
    {
        return false;
    }
    if (Length == 0)
    {
        OutValue.Reset();
        return true;
    }
    const FUTF8ToTCHAR Converted(
        reinterpret_cast<const UTF8CHAR*>(Data),
        static_cast<int32>(Length));
    OutValue = FString(Converted.Length(), Converted.Get());
    return true;
}
}

struct FUnifiedScriptAsyncService::FImpl
{
    struct FPendingRequest
    {
        uint64 RequestId = 0;
        uint64 OwnerInstanceId = 0;
        uint32 OwnerGeneration = 0;
        EUnifiedScriptAsyncOperationV5 Operation = EUnifiedScriptAsyncOperationV5::Delay;
        float RemainingSeconds = 0.0f;
        float TimeoutSeconds = 0.0f;
        FString Payload;
        TSharedPtr<FStreamableHandle> StreamableHandle;
    };

    FImpl(
        UWorld& InWorld,
        FUnifiedScriptObjectRegistry& InRegistry,
        uint64 InWorldHostId)
        : World(&InWorld)
        , Registry(&InRegistry)
        , WorldHostId(InWorldHostId)
    {
        check(WorldHostId != 0);
    }

    bool Submit(const FUnifiedScriptAsyncRequestV5& Request)
    {
        check(IsInGameThread());
        if (bShutdown || World == nullptr || Registry == nullptr ||
            Request.AbiVersion != UE::UnifiedScript::AsyncService::AbiVersion ||
            Request.StructSize < sizeof(FUnifiedScriptAsyncRequestV5) ||
            Request.RequestId == 0 || Request.OwnerInstanceId == 0 ||
            Request.OwnerGeneration == 0 || Request.Reserved8 != 0 ||
            Request.Flags != 0 || Request.Reserved32 != 0 ||
            !FMath::IsFinite(Request.DurationSeconds) ||
            !FMath::IsFinite(Request.TimeoutSeconds) ||
            Request.DurationSeconds < 0.0f || Request.TimeoutSeconds < 0.0f ||
            PendingRequests.Contains(Request.RequestId) ||
            CompletedRequests.ContainsByPredicate(
                [&Request](const FUnifiedScriptAsyncCompletionV5& Completion)
                {
                    return Completion.RequestId == Request.RequestId;
                }))
        {
            return false;
        }

        FString Payload;
        if (!UE::UnifiedScript::AsyncService::Private::TryDecodeUtf8(
                Request.PayloadData,
                Request.PayloadLength,
                Payload))
        {
            return false;
        }

        switch (Request.Operation)
        {
        case EUnifiedScriptAsyncOperationV5::Delay:
            if (!Payload.IsEmpty())
            {
                return false;
            }
            break;
        case EUnifiedScriptAsyncOperationV5::LoadObject:
        case EUnifiedScriptAsyncOperationV5::LoadClass:
            if (Payload.IsEmpty() || Request.DurationSeconds != 0.0f)
            {
                return false;
            }
            break;
        default:
            return false;
        }

        TUniquePtr<FPendingRequest> Pending = MakeUnique<FPendingRequest>();
        Pending->RequestId = Request.RequestId;
        Pending->OwnerInstanceId = Request.OwnerInstanceId;
        Pending->OwnerGeneration = Request.OwnerGeneration;
        Pending->Operation = Request.Operation;
        Pending->RemainingSeconds = Request.DurationSeconds;
        Pending->TimeoutSeconds = Request.TimeoutSeconds;
        Pending->Payload = MoveTemp(Payload);
        PendingRequests.Add(Request.RequestId, MoveTemp(Pending));

        if (Request.Operation == EUnifiedScriptAsyncOperationV5::Delay)
        {
            return true;
        }

        FPendingRequest* Added = PendingRequests.FindChecked(Request.RequestId).Get();
        const FSoftObjectPath AssetPath(Added->Payload);
        if (!AssetPath.IsValid())
        {
            Complete(Request.RequestId, EUnifiedScriptAsyncCompletionStatusV5::Failed, nullptr, 1);
            return true;
        }
        if (UObject* Existing = AssetPath.ResolveObject())
        {
            CompleteAsset(Request.RequestId, Existing);
            return true;
        }

        TSharedPtr<FStreamableHandle> Handle =
            UAssetManager::GetStreamableManager().RequestAsyncLoad(
                AssetPath,
                FStreamableDelegate::CreateRaw(
                    this,
                    &FImpl::HandleAssetLoaded,
                    Request.RequestId));
        if (FPendingRequest* Current = FindPending(Request.RequestId))
        {
            Current->StreamableHandle = MoveTemp(Handle);
            if (!Current->StreamableHandle.IsValid())
            {
                Complete(
                    Request.RequestId,
                    EUnifiedScriptAsyncCompletionStatusV5::Failed,
                    nullptr,
                    2);
            }
        }
        return true;
    }

    bool PumpOwner(uint64 OwnerInstanceId, uint32 OwnerGeneration, float DeltaSeconds)
    {
        check(IsInGameThread());
        if (bShutdown || OwnerInstanceId == 0 || OwnerGeneration == 0 ||
            !FMath::IsFinite(DeltaSeconds) || DeltaSeconds < 0.0f)
        {
            return false;
        }

        TArray<uint64> Succeeded;
        TArray<uint64> TimedOut;
        for (TPair<uint64, TUniquePtr<FPendingRequest>>& Pair : PendingRequests)
        {
            FPendingRequest& Pending = *Pair.Value;
            if (Pending.OwnerInstanceId != OwnerInstanceId ||
                Pending.OwnerGeneration != OwnerGeneration)
            {
                continue;
            }
            if (Pending.Operation == EUnifiedScriptAsyncOperationV5::Delay)
            {
                Pending.RemainingSeconds -= DeltaSeconds;
                if (Pending.RemainingSeconds <= 0.0f)
                {
                    Succeeded.Add(Pair.Key);
                    continue;
                }
            }
            if (Pending.TimeoutSeconds > 0.0f)
            {
                Pending.TimeoutSeconds -= DeltaSeconds;
                if (Pending.TimeoutSeconds <= 0.0f)
                {
                    TimedOut.Add(Pair.Key);
                }
            }
        }
        for (uint64 RequestId : Succeeded)
        {
            Complete(
                RequestId,
                EUnifiedScriptAsyncCompletionStatusV5::Succeeded,
                nullptr,
                0);
        }
        for (uint64 RequestId : TimedOut)
        {
            if (FPendingRequest* Pending = FindPending(RequestId))
            {
                if (Pending->StreamableHandle.IsValid())
                {
                    Pending->StreamableHandle->CancelHandle();
                }
                Complete(
                    RequestId,
                    EUnifiedScriptAsyncCompletionStatusV5::TimedOut,
                    nullptr,
                    0);
            }
        }
        return true;
    }

    bool Cancel(uint64 OwnerInstanceId, uint32 OwnerGeneration, uint64 RequestId)
    {
        check(IsInGameThread());
        FPendingRequest* Pending = FindPending(RequestId);
        if (bShutdown || Pending == nullptr ||
            Pending->OwnerInstanceId != OwnerInstanceId ||
            Pending->OwnerGeneration != OwnerGeneration)
        {
            return false;
        }
        if (Pending->StreamableHandle.IsValid())
        {
            Pending->StreamableHandle->CancelHandle();
        }
        Complete(
            RequestId,
            EUnifiedScriptAsyncCompletionStatusV5::Canceled,
            nullptr,
            0);
        return true;
    }

    bool Poll(
        uint64 OwnerInstanceId,
        uint32 OwnerGeneration,
        FUnifiedScriptAsyncCompletionViewV5& OutView)
    {
        check(IsInGameThread());
        OutView = {};
        if (bShutdown || OwnerInstanceId == 0 || OwnerGeneration == 0)
        {
            return false;
        }

        PollBuffer.Reset();
        TArray<FUnifiedScriptAsyncCompletionV5> Remaining;
        Remaining.Reserve(CompletedRequests.Num());
        for (const FUnifiedScriptAsyncCompletionV5& Completion : CompletedRequests)
        {
            if (Completion.OwnerInstanceId == OwnerInstanceId &&
                Completion.OwnerGeneration == OwnerGeneration)
            {
                PollBuffer.Add(Completion);
            }
            else
            {
                Remaining.Add(Completion);
            }
        }
        CompletedRequests = MoveTemp(Remaining);
        OutView.AbiVersion = UE::UnifiedScript::AsyncService::AbiVersion;
        OutView.StructSize = sizeof(FUnifiedScriptAsyncCompletionViewV5);
        OutView.WorldHostId = WorldHostId;
        OutView.Completions = PollBuffer.GetData();
        OutView.Count = static_cast<uint32>(PollBuffer.Num());
        OutView.Sequence = ++PollSequence;
        return true;
    }

    uint32 CancelOwner(uint64 OwnerInstanceId, uint32 OwnerGeneration)
    {
        check(IsInGameThread());
        if (OwnerInstanceId == 0 || OwnerGeneration == 0)
        {
            return 0;
        }
        TArray<uint64> OwnedRequests;
        for (const TPair<uint64, TUniquePtr<FPendingRequest>>& Pair : PendingRequests)
        {
            if (Pair.Value->OwnerInstanceId == OwnerInstanceId &&
                Pair.Value->OwnerGeneration == OwnerGeneration)
            {
                OwnedRequests.Add(Pair.Key);
            }
        }
        for (uint64 RequestId : OwnedRequests)
        {
            if (FPendingRequest* Pending = FindPending(RequestId);
                Pending != nullptr && Pending->StreamableHandle.IsValid())
            {
                Pending->StreamableHandle->CancelHandle();
            }
            PendingRequests.Remove(RequestId);
        }
        CompletedRequests.RemoveAll(
            [OwnerInstanceId, OwnerGeneration](
                const FUnifiedScriptAsyncCompletionV5& Completion)
            {
                return Completion.OwnerInstanceId == OwnerInstanceId &&
                    Completion.OwnerGeneration == OwnerGeneration;
            });
        PollBuffer.Reset();
        return static_cast<uint32>(OwnedRequests.Num());
    }

    void Shutdown()
    {
        check(IsInGameThread());
        if (bShutdown)
        {
            return;
        }
        bShutdown = true;
        for (TPair<uint64, TUniquePtr<FPendingRequest>>& Pair : PendingRequests)
        {
            if (Pair.Value->StreamableHandle.IsValid())
            {
                Pair.Value->StreamableHandle->CancelHandle();
            }
        }
        PendingRequests.Reset();
        CompletedRequests.Reset();
        PollBuffer.Reset();
        RetainedResources.Reset();
        Registry = nullptr;
        World = nullptr;
    }

    void HandleAssetLoaded(uint64 RequestId)
    {
        check(IsInGameThread());
        if (FPendingRequest* Pending = FindPending(RequestId))
        {
            const FSoftObjectPath AssetPath(Pending->Payload);
            CompleteAsset(RequestId, AssetPath.ResolveObject());
        }
    }

    void CompleteAsset(uint64 RequestId, UObject* Resource)
    {
        FPendingRequest* Pending = FindPending(RequestId);
        if (Pending == nullptr)
        {
            return;
        }
        if (Pending->Operation == EUnifiedScriptAsyncOperationV5::LoadClass)
        {
            Resource = Cast<UClass>(Resource);
        }
        if (!IsValid(Resource) || Registry == nullptr)
        {
            Complete(
                RequestId,
                EUnifiedScriptAsyncCompletionStatusV5::Failed,
                nullptr,
                3);
            return;
        }

        FUnifiedScriptObjectHandleResult HandleResult;
        const FUnifiedScriptObjectHandle Handle =
            Registry->RegisterObject(Resource, HandleResult);
        if (!HandleResult.IsSuccess() || Handle.IsNull())
        {
            Complete(
                RequestId,
                EUnifiedScriptAsyncCompletionStatusV5::Failed,
                nullptr,
                4,
                static_cast<uint8>(HandleResult.Error));
            return;
        }
        RetainedResources.Emplace(Resource);
        Complete(
            RequestId,
            EUnifiedScriptAsyncCompletionStatusV5::Succeeded,
            Resource,
            0,
            0,
            Handle.Value);
    }

    void Complete(
        uint64 RequestId,
        EUnifiedScriptAsyncCompletionStatusV5 Status,
        UObject* Resource,
        uint32 ErrorCode,
        uint8 HandleError = 0,
        uint64 ResultHandle = 0)
    {
        (void)Resource;
        TUniquePtr<FPendingRequest> Pending;
        if (!PendingRequests.RemoveAndCopyValue(RequestId, Pending) || !Pending)
        {
            return;
        }
        FUnifiedScriptAsyncCompletionV5& Completion = CompletedRequests.AddDefaulted_GetRef();
        Completion.RequestId = Pending->RequestId;
        Completion.OwnerInstanceId = Pending->OwnerInstanceId;
        Completion.OwnerGeneration = Pending->OwnerGeneration;
        Completion.Operation = Pending->Operation;
        Completion.Status = Status;
        Completion.HandleError = HandleError;
        Completion.ResultHandle = ResultHandle;
        Completion.ErrorCode = ErrorCode;
    }

    FPendingRequest* FindPending(uint64 RequestId)
    {
        TUniquePtr<FPendingRequest>* Pending = PendingRequests.Find(RequestId);
        return Pending != nullptr ? Pending->Get() : nullptr;
    }

    UWorld* World = nullptr;
    FUnifiedScriptObjectRegistry* Registry = nullptr;
    uint64 WorldHostId = 0;
    TMap<uint64, TUniquePtr<FPendingRequest>> PendingRequests;
    TArray<FUnifiedScriptAsyncCompletionV5> CompletedRequests;
    TArray<FUnifiedScriptAsyncCompletionV5> PollBuffer;
    TArray<TStrongObjectPtr<UObject>> RetainedResources;
    uint64 PollSequence = 0;
    bool bShutdown = false;
};

FUnifiedScriptAsyncService::FUnifiedScriptAsyncService(
    UWorld& InWorld,
    FUnifiedScriptObjectRegistry& InRegistry,
    uint64 InWorldHostId)
    : Impl(MakeUnique<FImpl>(InWorld, InRegistry, InWorldHostId))
{
}

FUnifiedScriptAsyncService::~FUnifiedScriptAsyncService()
{
    if (Impl)
    {
        Impl->Shutdown();
    }
}

bool FUnifiedScriptAsyncService::Submit(const FUnifiedScriptAsyncRequestV5& Request)
{
    return Impl && Impl->Submit(Request);
}

bool FUnifiedScriptAsyncService::PumpOwner(
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    float DeltaSeconds)
{
    return Impl && Impl->PumpOwner(OwnerInstanceId, OwnerGeneration, DeltaSeconds);
}

bool FUnifiedScriptAsyncService::Cancel(
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    uint64 RequestId)
{
    return Impl && Impl->Cancel(OwnerInstanceId, OwnerGeneration, RequestId);
}

bool FUnifiedScriptAsyncService::Poll(
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration,
    FUnifiedScriptAsyncCompletionViewV5& OutView)
{
    return Impl && Impl->Poll(OwnerInstanceId, OwnerGeneration, OutView);
}

uint32 FUnifiedScriptAsyncService::CancelOwner(
    uint64 OwnerInstanceId,
    uint32 OwnerGeneration)
{
    return Impl ? Impl->CancelOwner(OwnerInstanceId, OwnerGeneration) : 0;
}

void FUnifiedScriptAsyncService::Shutdown()
{
    if (Impl)
    {
        Impl->Shutdown();
    }
}

int32 FUnifiedScriptAsyncService::NumPending() const
{
    return Impl ? Impl->PendingRequests.Num() : 0;
}

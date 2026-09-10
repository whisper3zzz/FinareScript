#pragma once

#include "CoreMinimal.h"
#include "FinareObjectRegistry.h"
#include "UObject/ScriptDelegates.h"

struct FFinareNativeValueV3;
struct FFinareValue;
struct FFinareInvocationResult;

enum class EFinareCallbackStatus : uint8
{
    Success, Unavailable, WrongThread, InvalidScope, InvalidHandle,
    WrongSignature, WrongOwner, InvalidArgument, HandlerFailed, Exhausted
};

struct alignas(8) FFinareCallbackRequest
{
    uint32 AbiVersion = 1, Size = 64;
    uint64 Token = 0, Scope = 0, SignatureId = 0;
    const FFinareNativeValueV3* Arguments = nullptr;
    uint32 ArgumentCount = 0, ByteCount = 0;
    const uint8* ByteBuffer = nullptr;
    uint64 Reserved = 0;
};

/** Owned by the managed callback until the native caller invokes Release exactly once. */
struct alignas(8) FFinareCallbackResponse
{
    uint32 AbiVersion = 1, Size = 56;
    EFinareCallbackStatus Status = EFinareCallbackStatus::Unavailable;
    uint8 Reserved0 = 0;
    uint16 Reserved1 = 0;
    uint32 ResultCount = 0;
    const FFinareNativeValueV3* Results = nullptr;
    const uint8* ByteBuffer = nullptr;
    uint32 ByteCount = 0, Reserved2 = 0;
    void* Owner = nullptr;
    void (*Release)(void*) = nullptr;
};

using FFinareCallback = void (*)(void*, const FFinareCallbackRequest*, FFinareCallbackResponse*);

struct alignas(8) FFinareCallbackTransport
{
    uint32 AbiVersion = 1, TableSize = 64, RequestSize = 64, ResponseSize = 56;
    uint8 (*RetainContext)(void*) = nullptr;
    void (*ReleaseContext)(void*) = nullptr;
    EFinareCallbackStatus (*OpenScope)(void*, uint64*) = nullptr;
    EFinareCallbackStatus (*CloseScope)(void*, uint64) = nullptr;
    EFinareCallbackStatus (*RegisterCallback)(void*, uint64, const uint8*, uint32, FFinareCallback, void*, uint64*) = nullptr;
    EFinareCallbackStatus (*ReleaseCallback)(void*, uint64, uint64) = nullptr;
};
static_assert(sizeof(FFinareCallbackRequest) == 64);
static_assert(sizeof(FFinareCallbackResponse) == 56);
static_assert(sizeof(FFinareCallbackTransport) == 64);
static_assert(STRUCT_OFFSET(FFinareCallbackResponse, Release) == 48);

extern "C" FINARERUNTIME_API const FFinareCallbackTransport* Finare_GetCallbackTransport();

FINARERUNTIME_API bool Finare_ResolveCallback(FFinareObjectRegistry& Registry,
    const FFinareValue& Value, uint64 SignatureId, FScriptDelegate& OutDelegate, FFinareInvocationResult& OutResult);

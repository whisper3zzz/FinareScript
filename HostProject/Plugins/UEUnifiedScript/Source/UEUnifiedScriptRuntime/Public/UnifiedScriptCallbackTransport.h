#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptObjectRegistry.h"
#include "UObject/ScriptDelegates.h"

struct FUnifiedScriptNativeValueV3;
struct FUnifiedScriptValue;
struct FUnifiedScriptInvocationResult;

enum class EUnifiedScriptCallbackStatus : uint8
{
    Success, Unavailable, WrongThread, InvalidScope, InvalidHandle,
    WrongSignature, WrongOwner, InvalidArgument, HandlerFailed, Exhausted
};

struct alignas(8) FUnifiedScriptCallbackRequest
{
    uint32 AbiVersion = 1, Size = 64;
    uint64 Token = 0, Scope = 0, SignatureId = 0;
    const FUnifiedScriptNativeValueV3* Arguments = nullptr;
    uint32 ArgumentCount = 0, ByteCount = 0;
    const uint8* ByteBuffer = nullptr;
    uint64 Reserved = 0;
};

/** Owned by the managed callback until the native caller invokes Release exactly once. */
struct alignas(8) FUnifiedScriptCallbackResponse
{
    uint32 AbiVersion = 1, Size = 56;
    EUnifiedScriptCallbackStatus Status = EUnifiedScriptCallbackStatus::Unavailable;
    uint8 Reserved0 = 0;
    uint16 Reserved1 = 0;
    uint32 ResultCount = 0;
    const FUnifiedScriptNativeValueV3* Results = nullptr;
    const uint8* ByteBuffer = nullptr;
    uint32 ByteCount = 0, Reserved2 = 0;
    void* Owner = nullptr;
    void (*Release)(void*) = nullptr;
};

using FUnifiedScriptCallback = void (*)(void*, const FUnifiedScriptCallbackRequest*, FUnifiedScriptCallbackResponse*);

struct alignas(8) FUnifiedScriptCallbackTransport
{
    uint32 AbiVersion = 1, TableSize = 64, RequestSize = 64, ResponseSize = 56;
    uint8 (*RetainContext)(void*) = nullptr;
    void (*ReleaseContext)(void*) = nullptr;
    EUnifiedScriptCallbackStatus (*OpenScope)(void*, uint64*) = nullptr;
    EUnifiedScriptCallbackStatus (*CloseScope)(void*, uint64) = nullptr;
    EUnifiedScriptCallbackStatus (*RegisterCallback)(void*, uint64, const uint8*, uint32, FUnifiedScriptCallback, void*, uint64*) = nullptr;
    EUnifiedScriptCallbackStatus (*ReleaseCallback)(void*, uint64, uint64) = nullptr;
};
static_assert(sizeof(FUnifiedScriptCallbackRequest) == 64);
static_assert(sizeof(FUnifiedScriptCallbackResponse) == 56);
static_assert(sizeof(FUnifiedScriptCallbackTransport) == 64);
static_assert(STRUCT_OFFSET(FUnifiedScriptCallbackResponse, Release) == 48);

extern "C" UEUNIFIEDSCRIPTRUNTIME_API const FUnifiedScriptCallbackTransport* UEUnifiedScript_GetCallbackTransport();

UEUNIFIEDSCRIPTRUNTIME_API bool UEUnifiedScript_ResolveCallback(FUnifiedScriptObjectRegistry& Registry,
    const FUnifiedScriptValue& Value, uint64 SignatureId, FScriptDelegate& OutDelegate, FUnifiedScriptInvocationResult& OutResult);

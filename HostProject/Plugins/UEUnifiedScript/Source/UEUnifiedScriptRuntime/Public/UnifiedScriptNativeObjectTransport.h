#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptNativeObjectRegistry.h"
#include <type_traits>

struct FUnifiedScriptNativeValueV3;

/** Independent optional table: existing UObject v1/v2/v3 layouts are unchanged. */
struct alignas(8) FUnifiedScriptNativeObjectTransportV1
{
    uint32 AbiVersion = 1;
    uint32 TableSize = 0;
    uint32 ValueSize = 0;
    uint32 Reserved = 0;
    uint8 (*RetainContext)(void*) = nullptr;
    void (*ReleaseContext)(void*) = nullptr;
    EUnifiedScriptNativeObjectStatus (*OpenScope)(void*, uint64*) = nullptr;
    EUnifiedScriptNativeObjectStatus (*CloseScope)(void*, uint64) = nullptr;
    EUnifiedScriptNativeObjectStatus (*ReleaseObject)(void*, uint64, uint64) = nullptr;
    EUnifiedScriptNativeObjectStatus (*Invoke)(void*, uint64, uint64, uint64,
        const FUnifiedScriptNativeValueV3*, uint32, FUnifiedScriptNativeValueV3*, uint64*) = nullptr;
};

static_assert(std::is_standard_layout_v<FUnifiedScriptNativeObjectTransportV1>);
static_assert(sizeof(FUnifiedScriptNativeObjectTransportV1) == 64);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeObjectTransportV1, Invoke) == 56);

/** Optional v1 tail: bit 0 admits typed object argument cells. */
struct alignas(8) FUnifiedScriptNativeObjectTransportWithCapabilities
{
    FUnifiedScriptNativeObjectTransportV1 Base;
    uint64 Capabilities = 1;
};
static_assert(sizeof(FUnifiedScriptNativeObjectTransportWithCapabilities) == 72);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeObjectTransportWithCapabilities, Capabilities) == 64);

struct alignas(8) FUnifiedScriptNativeObjectTextTransportV1
{
    uint32 AbiVersion = 1;
    uint32 TableSize = 0;
    uint32 ValueSize = 0;
    uint32 Reserved = 0;
    uint32 MaxInputBytes = 0;
    uint32 MaxOutputBytes = 0;
    uint32 MaxPendingBytes = 0;
    uint32 MaxPendingCount = 0;
    EUnifiedScriptNativeObjectStatus (*Begin)(void*, uint64, uint64, uint64,
        const FUnifiedScriptNativeValueV3*, uint32, const uint8*, uint32, uint64*, uint32*) = nullptr;
    EUnifiedScriptNativeObjectStatus (*Read)(void*, uint64, uint64, uint8*, uint32, uint32*,
        FUnifiedScriptNativeValueV3*, uint64*) = nullptr;
    EUnifiedScriptNativeObjectStatus (*Discard)(void*, uint64, uint64) = nullptr;
};
static_assert(sizeof(FUnifiedScriptNativeObjectTextTransportV1) == 56);

/** Optional v1 tail: bit 1 advertises the bounded UTF-8 text lane. */
struct alignas(8) FUnifiedScriptNativeObjectTransportWithText
{
    FUnifiedScriptNativeObjectTransportWithCapabilities Base;
    const FUnifiedScriptNativeObjectTextTransportV1* TextTransport = nullptr;
};
static_assert(sizeof(FUnifiedScriptNativeObjectTransportWithText) == 80);
static_assert(STRUCT_OFFSET(FUnifiedScriptNativeObjectTransportWithText, TextTransport) == 72);

extern "C" UEUNIFIEDSCRIPTRUNTIME_API const FUnifiedScriptNativeObjectTransportV1*
UEUnifiedScript_GetNativeObjectTransportV1();

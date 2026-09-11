#pragma once

#include "CoreMinimal.h"
#include "FinareNativeObjectRegistry.h"
#include <type_traits>

struct FFinareNativeValueV3;

/** Independent optional table: existing UObject v1/v2/v3 layouts are unchanged. */
struct alignas(8) FFinareNativeObjectTransportV1
{
	uint32 AbiVersion = 1;
	uint32 TableSize = 0;
	uint32 ValueSize = 0;
	uint32 Reserved = 0;
	uint8 (*RetainContext)(void*) = nullptr;
	void (*ReleaseContext)(void*) = nullptr;
	EFinareNativeObjectStatus (*OpenScope)(void*, uint64*) = nullptr;
	EFinareNativeObjectStatus (*CloseScope)(void*, uint64) = nullptr;
	EFinareNativeObjectStatus (*ReleaseObject)(void*, uint64, uint64) = nullptr;
	EFinareNativeObjectStatus (*Invoke)(void*, uint64, uint64, uint64,
	                                    const FFinareNativeValueV3*, uint32, FFinareNativeValueV3*, uint64*) = nullptr;
};

static_assert(std::is_standard_layout_v<FFinareNativeObjectTransportV1>);
static_assert(sizeof(FFinareNativeObjectTransportV1) == 64);
static_assert(STRUCT_OFFSET(FFinareNativeObjectTransportV1, Invoke) == 56);

/** Optional v1 tail: bit 0 admits typed object argument cells. */
struct alignas(8) FFinareNativeObjectTransportWithCapabilities
{
	FFinareNativeObjectTransportV1 Base;
	uint64 Capabilities = 1;
};

static_assert(sizeof(FFinareNativeObjectTransportWithCapabilities) == 72);
static_assert(STRUCT_OFFSET(FFinareNativeObjectTransportWithCapabilities, Capabilities) == 64);

struct alignas(8) FFinareNativeObjectTextTransportV1
{
	uint32 AbiVersion = 1;
	uint32 TableSize = 0;
	uint32 ValueSize = 0;
	uint32 Reserved = 0;
	uint32 MaxInputBytes = 0;
	uint32 MaxOutputBytes = 0;
	uint32 MaxPendingBytes = 0;
	uint32 MaxPendingCount = 0;
	EFinareNativeObjectStatus (*Begin)(void*, uint64, uint64, uint64,
	                                   const FFinareNativeValueV3*, uint32, const uint8*, uint32, uint64*,
	                                   uint32*) = nullptr;
	EFinareNativeObjectStatus (*Read)(void*, uint64, uint64, uint8*, uint32, uint32*,
	                                  FFinareNativeValueV3*, uint64*) = nullptr;
	EFinareNativeObjectStatus (*Discard)(void*, uint64, uint64) = nullptr;
};

static_assert(sizeof(FFinareNativeObjectTextTransportV1) == 56);

/** Optional v1 tail: bit 1 advertises the bounded UTF-8 text lane. */
struct alignas(8) FFinareNativeObjectTransportWithText
{
	FFinareNativeObjectTransportWithCapabilities Base;
	const FFinareNativeObjectTextTransportV1* TextTransport = nullptr;
};

static_assert(sizeof(FFinareNativeObjectTransportWithText) == 80);
static_assert(STRUCT_OFFSET(FFinareNativeObjectTransportWithText, TextTransport) == 72);

extern "C" FINARERUNTIME_API const FFinareNativeObjectTransportV1*
Finare_GetNativeObjectTransportV1();

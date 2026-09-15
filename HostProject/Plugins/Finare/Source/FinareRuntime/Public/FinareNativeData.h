#pragma once

#include "CoreMinimal.h"

struct FFinareNativeCallFrameV3;
using FFinareMemoryCallback = void (*)(void* UserData, void* Data, int32 Count);

/** Optional synchronous native property access. No address is retained by a view. */
struct alignas(8) FFinareNativeDataTransportV1
{
	uint32 Version = 1;
	uint32 Size = 0;
	void (*Execute)(void*, uint64, const uint8*, int32, uint8, FFinareNativeCallFrameV3*) = nullptr;
	uint8 (*Borrow)(void*, uint64, const uint8*, int32, uint8, uint32, uint8,
		FFinareMemoryCallback, void*, uint8*, int32) = nullptr;
};
static_assert(sizeof(FFinareNativeDataTransportV1) == 24);

extern "C" FINARERUNTIME_API const FFinareNativeDataTransportV1* Finare_GetNativeDataTransportV1();
/** Borrow callbacks cannot reenter UE and invalidate their own storage. */
FINARERUNTIME_API bool Finare_IsNativeMemoryBorrowed();
FINARERUNTIME_API bool Finare_WithNativeMemory(void* Data, int32 Count, uint32 ElementSize, FFinareMemoryCallback Callback, void* UserData);

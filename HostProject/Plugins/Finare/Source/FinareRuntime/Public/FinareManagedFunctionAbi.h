#pragma once

#include "CoreMinimal.h"

/** Stable value tags for the non-allocating native-to-managed scalar call ABI v1. */
enum class EFinareManagedFunctionValueKindV1 : uint32
{
    Void = 0,
    Boolean = 1,
    Byte = 2,
    Int32 = 3,
    Int64 = 4,
    Single = 5,
    Double = 6,
};

/**
 * Fixed-width scalar value shared with Finare.Unreal.
 *
 * New value families extend a later ABI revision instead of changing this
 * layout. Reserved must remain zero for v1 calls.
 */
struct alignas(8) FFinareManagedFunctionValueV1
{
    EFinareManagedFunctionValueKindV1 Kind =
        EFinareManagedFunctionValueKindV1::Void;
    uint32 Reserved = 0;
    uint64 Payload = 0;
};

static_assert(sizeof(FFinareManagedFunctionValueV1) == 16);
static_assert(alignof(FFinareManagedFunctionValueV1) == 8);

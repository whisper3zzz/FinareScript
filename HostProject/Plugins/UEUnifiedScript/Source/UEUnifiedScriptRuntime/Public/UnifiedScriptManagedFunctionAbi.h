#pragma once

#include "CoreMinimal.h"

/** Stable value tags for the non-allocating native-to-managed scalar call ABI v1. */
enum class EUnifiedScriptManagedFunctionValueKindV1 : uint32
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
 * Fixed-width scalar value shared with UnifiedScript.Unreal.
 *
 * New value families extend a later ABI revision instead of changing this
 * layout. Reserved must remain zero for v1 calls.
 */
struct alignas(8) FUnifiedScriptManagedFunctionValueV1
{
    EUnifiedScriptManagedFunctionValueKindV1 Kind =
        EUnifiedScriptManagedFunctionValueKindV1::Void;
    uint32 Reserved = 0;
    uint64 Payload = 0;
};

static_assert(sizeof(FUnifiedScriptManagedFunctionValueV1) == 16);
static_assert(alignof(FUnifiedScriptManagedFunctionValueV1) == 8);

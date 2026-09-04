#pragma once

#include "CoreMinimal.h"

struct FUnifiedScriptNativeValueV3;

/** Separate from UObject identities: these tokens never contain native addresses. */
struct FUnifiedScriptNativeObjectHandle
{
    uint64 Value = 0;
};

enum class EUnifiedScriptNativeObjectStatus : uint8
{
    Success = 0,
    Unavailable = 1,
    WrongThread = 2,
    InvalidScope = 3,
    InvalidHandle = 4,
    WrongType = 5,
    WrongOwner = 6,
    InvalidArgument = 7,
    UnknownMember = 8,
    DuplicateMember = 9,
    Exhausted = 10,
};

class FUnifiedScriptNativeObjectRegistry;
using FUnifiedScriptNativeObjectDestroy = void (*)(void*);
using FUnifiedScriptNativeObjectMember = EUnifiedScriptNativeObjectStatus (*)(
    FUnifiedScriptNativeObjectRegistry&, uint64 Scope, FUnifiedScriptNativeObjectHandle Target,
    TConstArrayView<FUnifiedScriptNativeValueV3>, FUnifiedScriptNativeValueV3&,
    FUnifiedScriptNativeObjectHandle&);

/**
 * Game-thread-only native ownership domain, owned by one World bridge context.
 * Scope IDs and object IDs are process-unique, never recycled. Owned objects are
 * destroyed on release/scope close/World detach. Borrowed children never delete
 * their pointers and are retired before their owner is destroyed.
 * A borrowed declaration promises its address remains valid while its owner
 * lives; native code must invalidate children before replacing that storage.
 */
class UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptNativeObjectRegistry final
{
public:
    FUnifiedScriptNativeObjectRegistry();
    ~FUnifiedScriptNativeObjectRegistry();
    FUnifiedScriptNativeObjectRegistry(const FUnifiedScriptNativeObjectRegistry&) = delete;
    FUnifiedScriptNativeObjectRegistry& operator=(const FUnifiedScriptNativeObjectRegistry&) = delete;

    EUnifiedScriptNativeObjectStatus OpenScope(uint64& OutScope);
    EUnifiedScriptNativeObjectStatus CloseScope(uint64 Scope);
    EUnifiedScriptNativeObjectStatus RegisterOwned(uint64 Scope, uint64 TypeId,
        void* Pointer, FUnifiedScriptNativeObjectDestroy Destroy, FUnifiedScriptNativeObjectHandle& OutHandle);
    EUnifiedScriptNativeObjectStatus RegisterBorrowed(uint64 Scope, uint64 TypeId,
        void* Pointer, FUnifiedScriptNativeObjectHandle Owner, FUnifiedScriptNativeObjectHandle& OutHandle);
    EUnifiedScriptNativeObjectStatus Resolve(uint64 Scope, FUnifiedScriptNativeObjectHandle Handle,
        uint64 TypeId, void*& OutPointer) const;
    EUnifiedScriptNativeObjectStatus Release(uint64 Scope, FUnifiedScriptNativeObjectHandle Handle);
    EUnifiedScriptNativeObjectStatus InvalidateBorrowed(uint64 Scope, FUnifiedScriptNativeObjectHandle Owner);
    EUnifiedScriptNativeObjectStatus RegisterMember(uint64 MemberId, FUnifiedScriptNativeObjectMember Member);
    EUnifiedScriptNativeObjectStatus Invoke(uint64 Scope, FUnifiedScriptNativeObjectHandle Target,
        uint64 MemberId, TConstArrayView<FUnifiedScriptNativeValueV3> Arguments,
        FUnifiedScriptNativeValueV3& OutValue, FUnifiedScriptNativeObjectHandle& OutHandle);
    void Detach();
    int32 Num() const;
    int32 NumScopes() const;

private:
    struct FImpl;
    TUniquePtr<FImpl> Impl;
};

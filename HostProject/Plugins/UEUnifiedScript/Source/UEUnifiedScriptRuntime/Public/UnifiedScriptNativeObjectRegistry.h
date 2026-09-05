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
    InvalidTextToken = 11,
    TextBufferTooSmall = 12,
    TextUnavailable = 13,
};

class FUnifiedScriptNativeObjectRegistry;
using FUnifiedScriptNativeObjectDestroy = void (*)(void*);
using FUnifiedScriptNativeObjectMember = EUnifiedScriptNativeObjectStatus (*)(
    FUnifiedScriptNativeObjectRegistry&, uint64 Scope, FUnifiedScriptNativeObjectHandle Target,
    TConstArrayView<FUnifiedScriptNativeValueV3>, FUnifiedScriptNativeValueV3&,
    FUnifiedScriptNativeObjectHandle&);
using FUnifiedScriptNativeObjectTextMember = EUnifiedScriptNativeObjectStatus (*)(
    FUnifiedScriptNativeObjectRegistry&, uint64 Scope, FUnifiedScriptNativeObjectHandle Target,
    TConstArrayView<FUnifiedScriptNativeValueV3>, TConstArrayView<uint8>, FUnifiedScriptNativeValueV3&,
    FUnifiedScriptNativeObjectHandle&, TArray<uint8>&);

struct FUnifiedScriptNativeObjectMemberEntry
{
    uint64 MemberId;
    FUnifiedScriptNativeObjectMember Invoke;
    FUnifiedScriptNativeObjectTextMember TextInvoke = nullptr;
};

/** Generated module-lifetime registration, usable by existing and future Worlds. */
class UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptNativeObjectLibrary final
{
public:
    explicit FUnifiedScriptNativeObjectLibrary(TConstArrayView<FUnifiedScriptNativeObjectMemberEntry> Members);
    ~FUnifiedScriptNativeObjectLibrary();
    FUnifiedScriptNativeObjectLibrary(const FUnifiedScriptNativeObjectLibrary&) = delete;
    FUnifiedScriptNativeObjectLibrary& operator=(const FUnifiedScriptNativeObjectLibrary&) = delete;
private:
    uint64 Id = 0;
};

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
    EUnifiedScriptNativeObjectStatus BeginText(uint64 Scope, FUnifiedScriptNativeObjectHandle Target,
        uint64 MemberId, TConstArrayView<FUnifiedScriptNativeValueV3> Arguments, TConstArrayView<uint8> Input,
        uint64& OutToken, uint32& OutRequiredBytes);
    EUnifiedScriptNativeObjectStatus ReadText(uint64 Scope, uint64 Token, uint8* Output, uint32 Capacity,
        uint32& OutRequiredBytes, FUnifiedScriptNativeValueV3& OutValue, FUnifiedScriptNativeObjectHandle& OutHandle);
    EUnifiedScriptNativeObjectStatus DiscardText(uint64 Scope, uint64 Token);
    void Detach();
    int32 Num() const;
    int32 NumScopes() const;

private:
    friend class FUnifiedScriptNativeObjectLibrary;
    void ReleaseLibrary(uint64 LibraryId);
    struct FImpl;
    TUniquePtr<FImpl> Impl;
};

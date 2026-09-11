#pragma once

#include "CoreMinimal.h"

struct FFinareNativeValueV3;

/** Separate from UObject identities: these tokens never contain native addresses. */
struct FFinareNativeObjectHandle
{
	uint64 Value = 0;
};

enum class EFinareNativeObjectStatus : uint8
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

class FFinareNativeObjectRegistry;
using FFinareNativeObjectDestroy = void (*)(void*);
using FFinareNativeObjectMember = EFinareNativeObjectStatus (*)(
	FFinareNativeObjectRegistry&, uint64 Scope, FFinareNativeObjectHandle Target,
	TConstArrayView<FFinareNativeValueV3>, FFinareNativeValueV3&,
	FFinareNativeObjectHandle&);
using FFinareNativeObjectTextMember = EFinareNativeObjectStatus (*)(
	FFinareNativeObjectRegistry&, uint64 Scope, FFinareNativeObjectHandle Target,
	TConstArrayView<FFinareNativeValueV3>, TConstArrayView<uint8>, FFinareNativeValueV3&,
	FFinareNativeObjectHandle&, TArray<uint8>&);

struct FFinareNativeObjectMemberEntry
{
	uint64 MemberId;
	FFinareNativeObjectMember Invoke;
	FFinareNativeObjectTextMember TextInvoke = nullptr;
};

/** Generated module-lifetime registration, usable by existing and future Worlds. */
class FINARERUNTIME_API FFinareNativeObjectLibrary final
{
public:
	explicit FFinareNativeObjectLibrary(TConstArrayView<FFinareNativeObjectMemberEntry> Members);
	~FFinareNativeObjectLibrary();
	FFinareNativeObjectLibrary(const FFinareNativeObjectLibrary&) = delete;
	FFinareNativeObjectLibrary& operator=(const FFinareNativeObjectLibrary&) = delete;

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
class FINARERUNTIME_API FFinareNativeObjectRegistry final
{
public:
	FFinareNativeObjectRegistry();
	~FFinareNativeObjectRegistry();
	FFinareNativeObjectRegistry(const FFinareNativeObjectRegistry&) = delete;
	FFinareNativeObjectRegistry& operator=(const FFinareNativeObjectRegistry&) = delete;

	EFinareNativeObjectStatus OpenScope(uint64& OutScope);
	EFinareNativeObjectStatus CloseScope(uint64 Scope);
	EFinareNativeObjectStatus RegisterOwned(uint64 Scope, uint64 TypeId,
	                                        void* Pointer, FFinareNativeObjectDestroy Destroy,
	                                        FFinareNativeObjectHandle& OutHandle);
	EFinareNativeObjectStatus RegisterBorrowed(uint64 Scope, uint64 TypeId,
	                                           void* Pointer, FFinareNativeObjectHandle Owner,
	                                           FFinareNativeObjectHandle& OutHandle);
	EFinareNativeObjectStatus Resolve(uint64 Scope, FFinareNativeObjectHandle Handle,
	                                  uint64 TypeId, void*& OutPointer) const;
	EFinareNativeObjectStatus Release(uint64 Scope, FFinareNativeObjectHandle Handle);
	EFinareNativeObjectStatus InvalidateBorrowed(uint64 Scope, FFinareNativeObjectHandle Owner);
	EFinareNativeObjectStatus RegisterMember(uint64 MemberId, FFinareNativeObjectMember Member);
	EFinareNativeObjectStatus Invoke(uint64 Scope, FFinareNativeObjectHandle Target,
	                                 uint64 MemberId, TConstArrayView<FFinareNativeValueV3> Arguments,
	                                 FFinareNativeValueV3& OutValue, FFinareNativeObjectHandle& OutHandle);
	EFinareNativeObjectStatus BeginText(uint64 Scope, FFinareNativeObjectHandle Target,
	                                    uint64 MemberId, TConstArrayView<FFinareNativeValueV3> Arguments,
	                                    TConstArrayView<uint8> Input,
	                                    uint64& OutToken, uint32& OutRequiredBytes);
	EFinareNativeObjectStatus ReadText(uint64 Scope, uint64 Token, uint8* Output, uint32 Capacity,
	                                   uint32& OutRequiredBytes, FFinareNativeValueV3& OutValue,
	                                   FFinareNativeObjectHandle& OutHandle);
	EFinareNativeObjectStatus DiscardText(uint64 Scope, uint64 Token);
	void Detach();
	int32 Num() const;
	int32 NumScopes() const;

private:
	friend class FFinareNativeObjectLibrary;
	void ReleaseLibrary(uint64 LibraryId);
	struct FImpl;
	TUniquePtr<FImpl> Impl;
};

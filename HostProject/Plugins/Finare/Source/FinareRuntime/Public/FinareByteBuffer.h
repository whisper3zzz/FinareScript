#pragma once

#include "CoreMinimal.h"
#include "Containers/ArrayView.h"

/** Immutable shared snapshot, never a retained pointer into a managed call arena. */
class FFinareByteBuffer
{
public:
    static constexpr uint32 MaximumLength = 16u * 1024u * 1024u;
    static constexpr uint32 MaximumCallBytes = 64u * 1024u * 1024u;
    static FFinareByteBuffer CopyFrom(TConstArrayView<uint8> Source)
    {
        TArray<uint8> Copy;
        Copy.Append(Source.GetData(), Source.Num());
        return TakeOwnership(MoveTemp(Copy));
    }
    static FFinareByteBuffer TakeOwnership(TArray<uint8>&& Source)
    {
        FFinareByteBuffer Result;
        Result.Bytes = MakeShared<TArray<uint8>, ESPMode::ThreadSafe>(MoveTemp(Source));
        return Result;
    }
    int32 Num() const { return Bytes ? Bytes->Num() : 0; }
    /** The view is valid while a copy of this buffer retains its storage. */
    TConstArrayView<uint8> GetView() const
    {
        return Bytes ? MakeArrayView(*Bytes) : TConstArrayView<uint8>();
    }
private:
    TSharedPtr<const TArray<uint8>, ESPMode::ThreadSafe> Bytes;
};

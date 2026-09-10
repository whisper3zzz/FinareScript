#include "FinareCompiledChunk.h"

#include "Misc/FileHelper.h"

THIRD_PARTY_INCLUDES_START
#include <openssl/sha.h>
THIRD_PARTY_INCLUDES_END

namespace UE::Finare::CompiledChunk::Private
{
constexpr int32 HeaderSize = 96;
constexpr int32 DirectoryEntrySize = 64;
constexpr int32 Alignment = 16;
constexpr int32 DigestOffset = 56;
constexpr int32 DigestSize = 32;
constexpr uint32 MaximumSections = 1024;
constexpr uint32 AllowedSectionFlags = 0x7;
constexpr uint8 Magic[8] = {'U', 'S', 'C', 'H', 'U', 'N', 'K', 0};

uint16 ReadUInt16(TConstArrayView<uint8> Data, int32 Offset)
{
    return static_cast<uint16>(Data[Offset]) |
        static_cast<uint16>(Data[Offset + 1]) << 8;
}

uint32 ReadUInt32(TConstArrayView<uint8> Data, int32 Offset)
{
    return static_cast<uint32>(Data[Offset]) |
        static_cast<uint32>(Data[Offset + 1]) << 8 |
        static_cast<uint32>(Data[Offset + 2]) << 16 |
        static_cast<uint32>(Data[Offset + 3]) << 24;
}

uint64 ReadUInt64(TConstArrayView<uint8> Data, int32 Offset)
{
    return static_cast<uint64>(ReadUInt32(Data, Offset)) |
        static_cast<uint64>(ReadUInt32(Data, Offset + 4)) << 32;
}

int32 Align(int32 Value)
{
    return (Value + Alignment - 1) & ~(Alignment - 1);
}

bool IsZero(TConstArrayView<uint8> Data, int32 Offset, int32 Length)
{
    for (int32 Index = 0; Index < Length; ++Index)
    {
        if (Data[Offset + Index] != 0)
        {
            return false;
        }
    }
    return true;
}

bool HashMatches(
    TConstArrayView<uint8> Data,
    int32 Offset,
    int32 Length,
    const uint8* Expected)
{
    if (Length < 0 || Length > MAX_uint32)
    {
        return false;
    }
    uint8 Digest[SHA256_DIGEST_LENGTH]{};
    if (SHA256(
            Data.GetData() + Offset,
            static_cast<size_t>(Length),
            Digest) == nullptr)
    {
        return false;
    }
    return FMemory::Memcmp(Digest, Expected, DigestSize) == 0;
}
}

bool FFinareCompiledChunk::LoadSection(
    const FString& ChunkPath,
    EFinareCompiledChunkSection Section,
    TArray<uint8>& OutData,
    FString& OutError)
{
    using namespace UE::Finare::CompiledChunk::Private;

    OutData.Reset();
    TArray<uint8> Bytes;
    if (!FFileHelper::LoadFileToArray(Bytes, *ChunkPath))
    {
        OutError = FString::Printf(
            TEXT("Compiled chunk does not exist or cannot be read: '%s'."),
            *ChunkPath);
        return false;
    }
    if (Bytes.Num() < HeaderSize || Bytes.Num() > MAX_uint32 ||
        FMemory::Memcmp(Bytes.GetData(), Magic, sizeof(Magic)) != 0)
    {
        OutError = TEXT("Compiled chunk header or magic is invalid.");
        return false;
    }

    const TConstArrayView<uint8> View(Bytes);
    const uint16 Major = ReadUInt16(View, 8);
    const uint16 Minor = ReadUInt16(View, 10);
    const uint32 ParsedHeaderSize = ReadUInt32(View, 12);
    const uint32 FileFlags = ReadUInt32(View, 16);
    const uint32 SectionCount = ReadUInt32(View, 20);
    const uint32 ParsedEntrySize = ReadUInt32(View, 24);
    const uint32 Reserved = ReadUInt32(View, 28);
    const uint64 DirectoryOffset = ReadUInt64(View, 32);
    const uint64 PayloadOffset = ReadUInt64(View, 40);
    const uint64 FileLength = ReadUInt64(View, 48);
    if (Major != 1 || Minor != 0 || ParsedHeaderSize != HeaderSize ||
        FileFlags != 0 || Reserved != 0 || ParsedEntrySize != DirectoryEntrySize ||
        DirectoryOffset != HeaderSize || FileLength != static_cast<uint64>(Bytes.Num()) ||
        SectionCount == 0 || SectionCount > MaximumSections)
    {
        OutError = TEXT("Compiled chunk header is not canonical v1.0.");
        return false;
    }

    const int64 DirectoryEnd64 = static_cast<int64>(HeaderSize) +
        static_cast<int64>(SectionCount) * DirectoryEntrySize;
    if (DirectoryEnd64 > MAX_int32)
    {
        OutError = TEXT("Compiled chunk directory exceeds supported bounds.");
        return false;
    }
    const int32 DirectoryEnd = static_cast<int32>(DirectoryEnd64);
    const int32 ExpectedPayloadOffset = Align(DirectoryEnd);
    if (PayloadOffset != static_cast<uint64>(ExpectedPayloadOffset) ||
        ExpectedPayloadOffset > Bytes.Num() ||
        !IsZero(View, DigestOffset + DigestSize, HeaderSize - DigestOffset - DigestSize) ||
        !IsZero(View, DirectoryEnd, ExpectedPayloadOffset - DirectoryEnd))
    {
        OutError = TEXT("Compiled chunk offsets or canonical padding are invalid.");
        return false;
    }

    TArray<uint8> DigestInput = Bytes;
    FMemory::Memzero(DigestInput.GetData() + DigestOffset, DigestSize);
    if (!HashMatches(
            TConstArrayView<uint8>(DigestInput),
            0,
            DigestInput.Num(),
            Bytes.GetData() + DigestOffset))
    {
        OutError = TEXT("Compiled chunk content digest does not match.");
        return false;
    }

    const uint32 RequestedKind = static_cast<uint32>(Section);
    uint32 PreviousKind = 0;
    int32 ExpectedSectionOffset = ExpectedPayloadOffset;
    bool bFound = false;
    for (uint32 Index = 0; Index < SectionCount; ++Index)
    {
        const int32 EntryOffset = HeaderSize +
            static_cast<int32>(Index) * DirectoryEntrySize;
        const uint32 Kind = ReadUInt32(View, EntryOffset);
        const uint32 Flags = ReadUInt32(View, EntryOffset + 4);
        const uint64 Offset64 = ReadUInt64(View, EntryOffset + 8);
        const uint64 StoredLength64 = ReadUInt64(View, EntryOffset + 16);
        const uint64 LogicalLength64 = ReadUInt64(View, EntryOffset + 24);
        if (Kind == 0 || Kind <= PreviousKind || (Flags & ~AllowedSectionFlags) != 0 ||
            StoredLength64 == 0 || StoredLength64 != LogicalLength64 ||
            Offset64 > MAX_int32 || StoredLength64 > MAX_int32)
        {
            OutError = TEXT("Compiled chunk directory contains an invalid section entry.");
            return false;
        }

        ExpectedSectionOffset = Align(ExpectedSectionOffset);
        const int32 Offset = static_cast<int32>(Offset64);
        const int32 StoredLength = static_cast<int32>(StoredLength64);
        if (Offset != ExpectedSectionOffset || Offset > Bytes.Num() ||
            StoredLength > Bytes.Num() - Offset ||
            !HashMatches(
                View,
                Offset,
                StoredLength,
                Bytes.GetData() + EntryOffset + 32))
        {
            OutError = TEXT("Compiled chunk section bounds or digest are invalid.");
            return false;
        }
        if (Kind == RequestedKind)
        {
            OutData.Append(Bytes.GetData() + Offset, StoredLength);
            bFound = true;
        }

        PreviousKind = Kind;
        ExpectedSectionOffset = Offset + StoredLength;
        if (Index + 1 < SectionCount)
        {
            const int32 NextOffset = Align(ExpectedSectionOffset);
            if (!IsZero(View, ExpectedSectionOffset, NextOffset - ExpectedSectionOffset))
            {
                OutError = TEXT("Compiled chunk section padding is not canonical.");
                return false;
            }
        }
    }

    if (ExpectedSectionOffset != Bytes.Num())
    {
        OutError = TEXT("Compiled chunk contains trailing bytes.");
        return false;
    }
    if (!bFound)
    {
        OutError = FString::Printf(
            TEXT("Compiled chunk is missing required section %u."),
            RequestedKind);
        return false;
    }

    OutError.Reset();
    return true;
}

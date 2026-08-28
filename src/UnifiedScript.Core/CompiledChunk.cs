using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace UnifiedScript.Core;

public enum UnifiedScriptChunkSectionKind : uint
{
    RuntimeManifest = 1,
    ManagedAssembly = 2,
    DeclarationData = 3,
    BindingManifest = 4,
    DependencyManifest = 5,
    PortablePdb = 0x1001,
    SourceMap = 0x1002,
    AuthoringSurface = 0x1003,
    IdentityData = 0x1004,
    Hir = 0x1005,
    Semantics = 0x1006,
    ScriptAssets = 0x1007,
}

[Flags]
public enum UnifiedScriptChunkSectionFlags : uint
{
    None = 0,
    RuntimeRequired = 1 << 0,
    Debug = 1 << 1,
    Editor = 1 << 2,
}

public sealed record UnifiedScriptChunkSection(
    UnifiedScriptChunkSectionKind Kind,
    UnifiedScriptChunkSectionFlags Flags,
    ReadOnlyMemory<byte> Data);

public sealed record UnifiedScriptChunkReadProfile(
    int ByteLength,
    long StreamReadBytes,
    long StreamReadStopwatchTicks,
    long InputMaterializationBytes,
    long InputMaterializationStopwatchTicks,
    long HeaderValidationStopwatchTicks,
    long WholeFileDigestStopwatchTicks,
    long StorageMaterializationBytes,
    long StorageMaterializationStopwatchTicks,
    long DirectoryValidationStopwatchTicks,
    long SectionDigestStopwatchTicks)
{
    public long WholeFileCopyBytes => checked(
        InputMaterializationBytes + StorageMaterializationBytes);

    public int WholeFileCopyCount =>
        (InputMaterializationBytes > 0 ? 1 : 0) +
        (StorageMaterializationBytes > 0 ? 1 : 0);

    public long TotalStopwatchTicks => checked(
        StreamReadStopwatchTicks +
        InputMaterializationStopwatchTicks +
        HeaderValidationStopwatchTicks +
        WholeFileDigestStopwatchTicks +
        StorageMaterializationStopwatchTicks +
        DirectoryValidationStopwatchTicks +
        SectionDigestStopwatchTicks);
}

public sealed record UnifiedScriptChunkReadResult(
    UnifiedScriptChunk Chunk,
    UnifiedScriptChunkReadProfile Profile);

public sealed class UnifiedScriptChunk
{
    private readonly IReadOnlyDictionary<UnifiedScriptChunkSectionKind, UnifiedScriptChunkSection>
        sectionsByKind;

    internal UnifiedScriptChunk(
        IReadOnlyList<UnifiedScriptChunkSection> sections,
        string contentDigest,
        int byteLength)
    {
        Sections = sections;
        ContentDigest = contentDigest;
        ByteLength = byteLength;
        sectionsByKind = sections.ToDictionary(static section => section.Kind);
    }

    public const ushort FormatMajorVersion = 1;

    public const ushort FormatMinorVersion = 0;

    public string FormatVersion => $"{FormatMajorVersion}.{FormatMinorVersion}";

    public string ContentDigest { get; }

    public int ByteLength { get; }

    public IReadOnlyList<UnifiedScriptChunkSection> Sections { get; }

    public bool TryGetSection(
        UnifiedScriptChunkSectionKind kind,
        out UnifiedScriptChunkSection? section) => sectionsByKind.TryGetValue(kind, out section);

    public UnifiedScriptChunkSection GetRequiredSection(UnifiedScriptChunkSectionKind kind) =>
        sectionsByKind.TryGetValue(kind, out UnifiedScriptChunkSection? section)
            ? section
            : throw new InvalidDataException($"UnifiedScript chunk is missing section '{kind}'.");
}

public static class UnifiedScriptChunkCodec
{
    private static ReadOnlySpan<byte> Magic => "USCHUNK\0"u8;

    private const int HeaderSize = 96;
    private const int DirectoryEntrySize = 64;
    private const int Alignment = 16;
    private const int DigestOffset = 56;
    private const int DigestSize = 32;
    private const int MaximumSections = 1024;
    private const UnifiedScriptChunkSectionFlags AllowedSectionFlags =
        UnifiedScriptChunkSectionFlags.RuntimeRequired |
        UnifiedScriptChunkSectionFlags.Debug |
        UnifiedScriptChunkSectionFlags.Editor;

    public static byte[] Write(IEnumerable<UnifiedScriptChunkSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        UnifiedScriptChunkSection[] ordered = sections
            .OrderBy(static section => (uint)section.Kind)
            .ToArray();
        ValidateForWrite(ordered);

        int directoryLength = checked(ordered.Length * DirectoryEntrySize);
        int payloadOffset = Align(checked(HeaderSize + directoryLength));
        int fileLength = payloadOffset;
        foreach (UnifiedScriptChunkSection section in ordered)
        {
            fileLength = Align(fileLength);
            fileLength = checked(fileLength + section.Data.Length);
        }

        byte[] bytes = new byte[fileLength];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), UnifiedScriptChunk.FormatMajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), UnifiedScriptChunk.FormatMinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), checked((uint)ordered.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), DirectoryEntrySize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), checked((ulong)payloadOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), checked((ulong)fileLength));

        int nextPayloadOffset = payloadOffset;
        for (int index = 0; index < ordered.Length; index++)
        {
            UnifiedScriptChunkSection section = ordered[index];
            nextPayloadOffset = Align(nextPayloadOffset);
            int entryOffset = HeaderSize + (index * DirectoryEntrySize);
            Span<byte> entry = bytes.AsSpan(entryOffset, DirectoryEntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)section.Kind);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)section.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], checked((ulong)nextPayloadOffset));
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], checked((ulong)section.Data.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(entry[24..], checked((ulong)section.Data.Length));
            SHA256.HashData(section.Data.Span, entry[32..]);
            section.Data.Span.CopyTo(bytes.AsSpan(nextPayloadOffset));
            nextPayloadOffset = checked(nextPayloadOffset + section.Data.Length);
        }

        ComputeContentDigest(bytes).CopyTo(bytes.AsSpan(DigestOffset, DigestSize));
        return bytes;
    }

    public static void Write(Stream destination, IEnumerable<UnifiedScriptChunkSection> sections)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The chunk destination stream must be writable.",
                nameof(destination));
        }

        destination.Write(Write(sections));
    }

    public static UnifiedScriptChunk Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The chunk source stream must be readable.", nameof(source));
        }

        if (source.CanSeek && source.Length - source.Position > int.MaxValue)
        {
            throw new InvalidDataException("UnifiedScript chunk exceeds the supported 2 GiB limit.");
        }

        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return Read(buffer.ToArray());
    }

    public static UnifiedScriptChunkReadResult ReadProfiled(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The chunk source stream must be readable.", nameof(source));
        }
        if (source.CanSeek && source.Length - source.Position > int.MaxValue)
        {
            throw new InvalidDataException("UnifiedScript chunk exceeds the supported 2 GiB limit.");
        }

        var profile = new ChunkReadProfileBuilder();
        using var buffer = new MemoryStream();
        long started = Stopwatch.GetTimestamp();
        source.CopyTo(buffer);
        profile.StreamReadStopwatchTicks = ElapsedStopwatchTicks(started);
        profile.StreamReadBytes = buffer.Length;

        started = Stopwatch.GetTimestamp();
        byte[] content = buffer.ToArray();
        profile.InputMaterializationStopwatchTicks = ElapsedStopwatchTicks(started);
        profile.InputMaterializationBytes = content.Length;
        UnifiedScriptChunk chunk = ReadCore(content, profile);
        return new UnifiedScriptChunkReadResult(chunk, profile.Build(content.Length));
    }

    public static UnifiedScriptChunk Read(ReadOnlySpan<byte> source) => ReadCore(source, null);

    public static UnifiedScriptChunkReadResult ReadProfiled(ReadOnlySpan<byte> source)
    {
        var profile = new ChunkReadProfileBuilder();
        UnifiedScriptChunk chunk = ReadCore(source, profile);
        return new UnifiedScriptChunkReadResult(chunk, profile.Build(source.Length));
    }

    private static UnifiedScriptChunk ReadCore(
        ReadOnlySpan<byte> source,
        ChunkReadProfileBuilder? profile)
    {
        long headerStarted = StartProfilePhase(profile);
        if (source.Length < HeaderSize)
        {
            throw new InvalidDataException("UnifiedScript chunk header is truncated.");
        }

        if (!source[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("UnifiedScript chunk magic is invalid.");
        }

        ushort majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(source[8..]);
        ushort minorVersion = BinaryPrimitives.ReadUInt16LittleEndian(source[10..]);
        if (majorVersion != UnifiedScriptChunk.FormatMajorVersion ||
            minorVersion != UnifiedScriptChunk.FormatMinorVersion)
        {
            throw new InvalidDataException(
                $"Unsupported UnifiedScript chunk format {majorVersion}.{minorVersion}.");
        }

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(source[12..]);
        uint fileFlags = BinaryPrimitives.ReadUInt32LittleEndian(source[16..]);
        uint sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(source[20..]);
        uint directoryEntrySize = BinaryPrimitives.ReadUInt32LittleEndian(source[24..]);
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(source[28..]);
        ulong directoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(source[32..]);
        ulong payloadOffset = BinaryPrimitives.ReadUInt64LittleEndian(source[40..]);
        ulong fileLength = BinaryPrimitives.ReadUInt64LittleEndian(source[48..]);
        if (headerSize != HeaderSize || fileFlags != 0 || reserved != 0 ||
            directoryEntrySize != DirectoryEntrySize || directoryOffset != HeaderSize)
        {
            throw new InvalidDataException("UnifiedScript chunk header is not canonical v1.0.");
        }

        if (sectionCount == 0 || sectionCount > MaximumSections)
        {
            throw new InvalidDataException(
                $"UnifiedScript chunk section count '{sectionCount}' is invalid.");
        }

        if (fileLength != checked((ulong)source.Length))
        {
            throw new InvalidDataException("UnifiedScript chunk file length does not match its header.");
        }

        int expectedPayloadOffset = Align(checked(HeaderSize +
            (checked((int)sectionCount) * DirectoryEntrySize)));
        if (payloadOffset != checked((ulong)expectedPayloadOffset))
        {
            throw new InvalidDataException("UnifiedScript chunk payload offset is not canonical.");
        }
        profile?.AddHeaderValidation(ElapsedStopwatchTicks(headerStarted));

        ReadOnlySpan<byte> expectedContentDigest = source.Slice(DigestOffset, DigestSize);
        long digestStarted = StartProfilePhase(profile);
        byte[] actualContentDigest = ComputeContentDigest(source);
        profile?.AddWholeFileDigest(ElapsedStopwatchTicks(digestStarted));
        if (!CryptographicOperations.FixedTimeEquals(expectedContentDigest, actualContentDigest))
        {
            throw new InvalidDataException("UnifiedScript chunk content digest does not match.");
        }

        long directoryStarted = StartProfilePhase(profile);
        ValidateZeroPadding(source.Slice(DigestOffset + DigestSize, HeaderSize -
            (DigestOffset + DigestSize)));
        ValidateZeroPadding(source.Slice(
            HeaderSize + checked((int)sectionCount) * DirectoryEntrySize,
            expectedPayloadOffset -
                (HeaderSize + checked((int)sectionCount) * DirectoryEntrySize)));

        int sectionCountValue = checked((int)sectionCount);
        profile?.AddDirectoryValidation(ElapsedStopwatchTicks(directoryStarted));
        long storageStarted = StartProfilePhase(profile);
        byte[] storage = source.ToArray();
        if (profile is not null)
        {
            profile.StorageMaterializationBytes = storage.Length;
            profile.StorageMaterializationStopwatchTicks =
                ElapsedStopwatchTicks(storageStarted);
        }
        var sections = new List<UnifiedScriptChunkSection>(sectionCountValue);
        uint previousKind = 0;
        int expectedSectionOffset = expectedPayloadOffset;
        for (int index = 0; index < sectionCountValue; index++)
        {
            directoryStarted = StartProfilePhase(profile);
            ReadOnlySpan<byte> entry = source.Slice(
                HeaderSize + (index * DirectoryEntrySize),
                DirectoryEntrySize);
            uint rawKind = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var flags = (UnifiedScriptChunkSectionFlags)
                BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            ulong rawOffset = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            ulong rawStoredLength = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            ulong rawLogicalLength = BinaryPrimitives.ReadUInt64LittleEndian(entry[24..]);
            if (rawKind == 0 || rawKind <= previousKind)
            {
                throw new InvalidDataException(
                    "UnifiedScript chunk sections must have unique ascending kinds.");
            }

            if ((flags & ~AllowedSectionFlags) != 0)
            {
                throw new InvalidDataException(
                    $"UnifiedScript chunk section 0x{rawKind:x8} uses unsupported flags.");
            }

            if (rawStoredLength == 0 || rawStoredLength != rawLogicalLength ||
                rawStoredLength > int.MaxValue || rawOffset > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"UnifiedScript chunk section 0x{rawKind:x8} has invalid lengths.");
            }

            int offset = checked((int)rawOffset);
            int storedLength = checked((int)rawStoredLength);
            expectedSectionOffset = Align(expectedSectionOffset);
            if (offset != expectedSectionOffset || storedLength > source.Length - offset)
            {
                throw new InvalidDataException(
                    $"UnifiedScript chunk section 0x{rawKind:x8} has non-canonical bounds.");
            }

            ReadOnlySpan<byte> payload = source.Slice(offset, storedLength);
            profile?.AddDirectoryValidation(ElapsedStopwatchTicks(directoryStarted));
            long sectionDigestStarted = StartProfilePhase(profile);
            byte[] actualSectionDigest = SHA256.HashData(payload);
            profile?.AddSectionDigest(ElapsedStopwatchTicks(sectionDigestStarted));
            if (!CryptographicOperations.FixedTimeEquals(entry[32..], actualSectionDigest))
            {
                throw new InvalidDataException(
                    $"UnifiedScript chunk section 0x{rawKind:x8} digest does not match.");
            }

            directoryStarted = StartProfilePhase(profile);
            sections.Add(new UnifiedScriptChunkSection(
                (UnifiedScriptChunkSectionKind)rawKind,
                flags,
                storage.AsMemory(offset, storedLength)));
            previousKind = rawKind;
            expectedSectionOffset = checked(offset + storedLength);

            if (index + 1 < sectionCountValue)
            {
                int alignedNext = Align(expectedSectionOffset);
                ValidateZeroPadding(source.Slice(
                    expectedSectionOffset,
                    alignedNext - expectedSectionOffset));
            }
            profile?.AddDirectoryValidation(ElapsedStopwatchTicks(directoryStarted));
        }

        directoryStarted = StartProfilePhase(profile);
        if (expectedSectionOffset != source.Length)
        {
            throw new InvalidDataException("UnifiedScript chunk contains trailing bytes.");
        }
        profile?.AddDirectoryValidation(ElapsedStopwatchTicks(directoryStarted));

        return new UnifiedScriptChunk(
            sections,
            "sha256:" + Convert.ToHexString(expectedContentDigest).ToLowerInvariant(),
            source.Length);
    }

    private static void ValidateForWrite(IReadOnlyList<UnifiedScriptChunkSection> sections)
    {
        if (sections.Count == 0 || sections.Count > MaximumSections)
        {
            throw new ArgumentException(
                $"A UnifiedScript chunk requires between 1 and {MaximumSections} sections.",
                nameof(sections));
        }

        uint previousKind = 0;
        foreach (UnifiedScriptChunkSection section in sections)
        {
            uint rawKind = (uint)section.Kind;
            if (rawKind == 0 || rawKind == previousKind)
            {
                throw new ArgumentException(
                    "UnifiedScript chunk section kinds must be non-zero and unique.",
                    nameof(sections));
            }

            if ((section.Flags & ~AllowedSectionFlags) != 0)
            {
                throw new ArgumentException(
                    $"UnifiedScript chunk section '{section.Kind}' uses unsupported flags.",
                    nameof(sections));
            }

            if (section.Data.IsEmpty)
            {
                throw new ArgumentException(
                    $"UnifiedScript chunk section '{section.Kind}' cannot be empty.",
                    nameof(sections));
            }

            previousKind = rawKind;
        }
    }

    private static byte[] ComputeContentDigest(ReadOnlySpan<byte> bytes)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes[..DigestOffset]);
        Span<byte> zeroDigest = stackalloc byte[DigestSize];
        zeroDigest.Clear();
        hash.AppendData(zeroDigest);
        hash.AppendData(bytes[(DigestOffset + DigestSize)..]);
        return hash.GetHashAndReset();
    }

    private static int Align(int value) => checked((value + (Alignment - 1)) & ~(Alignment - 1));

    private static long StartProfilePhase(ChunkReadProfileBuilder? profile) =>
        profile is null ? 0 : Stopwatch.GetTimestamp();

    private static long ElapsedStopwatchTicks(long started) =>
        Math.Max(0, Stopwatch.GetTimestamp() - started);

    private static void ValidateZeroPadding(ReadOnlySpan<byte> padding)
    {
        if (padding.ContainsAnyExcept((byte)0))
        {
            throw new InvalidDataException("UnifiedScript chunk padding must be zero-filled.");
        }
    }

    private sealed class ChunkReadProfileBuilder
    {
        public long StreamReadBytes { get; set; }

        public long StreamReadStopwatchTicks { get; set; }

        public long InputMaterializationBytes { get; set; }

        public long InputMaterializationStopwatchTicks { get; set; }

        public long HeaderValidationStopwatchTicks { get; private set; }

        public long WholeFileDigestStopwatchTicks { get; private set; }

        public long StorageMaterializationBytes { get; set; }

        public long StorageMaterializationStopwatchTicks { get; set; }

        public long DirectoryValidationStopwatchTicks { get; private set; }

        public long SectionDigestStopwatchTicks { get; private set; }

        public void AddHeaderValidation(long ticks) =>
            HeaderValidationStopwatchTicks = checked(HeaderValidationStopwatchTicks + ticks);

        public void AddWholeFileDigest(long ticks) =>
            WholeFileDigestStopwatchTicks = checked(WholeFileDigestStopwatchTicks + ticks);

        public void AddDirectoryValidation(long ticks) =>
            DirectoryValidationStopwatchTicks = checked(DirectoryValidationStopwatchTicks + ticks);

        public void AddSectionDigest(long ticks) =>
            SectionDigestStopwatchTicks = checked(SectionDigestStopwatchTicks + ticks);

        public UnifiedScriptChunkReadProfile Build(int byteLength) => new(
            byteLength,
            StreamReadBytes,
            StreamReadStopwatchTicks,
            InputMaterializationBytes,
            InputMaterializationStopwatchTicks,
            HeaderValidationStopwatchTicks,
            WholeFileDigestStopwatchTicks,
            StorageMaterializationBytes,
            StorageMaterializationStopwatchTicks,
            DirectoryValidationStopwatchTicks,
            SectionDigestStopwatchTicks);
    }
}

public static class UnifiedScriptProgramChunk
{
    private static readonly UnifiedScriptChunkSectionKind[] RequiredRuntimeSections =
    [
        UnifiedScriptChunkSectionKind.RuntimeManifest,
        UnifiedScriptChunkSectionKind.ManagedAssembly,
        UnifiedScriptChunkSectionKind.DeclarationData,
        UnifiedScriptChunkSectionKind.BindingManifest,
        UnifiedScriptChunkSectionKind.DependencyManifest,
    ];

    public static UnifiedScriptChunk Read(ReadOnlySpan<byte> source)
    {
        UnifiedScriptChunk chunk = UnifiedScriptChunkCodec.Read(source);
        Validate(chunk);
        return chunk;
    }

    public static UnifiedScriptChunk Read(Stream source)
    {
        UnifiedScriptChunk chunk = UnifiedScriptChunkCodec.Read(source);
        Validate(chunk);
        return chunk;
    }

    public static void Validate(UnifiedScriptChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        foreach (UnifiedScriptChunkSectionKind kind in RequiredRuntimeSections)
        {
            UnifiedScriptChunkSection section = chunk.GetRequiredSection(kind);
            if ((section.Flags & UnifiedScriptChunkSectionFlags.RuntimeRequired) == 0)
            {
                throw new InvalidDataException(
                    $"UnifiedScript program section '{kind}' must be marked runtime-required.");
            }
        }

        foreach (UnifiedScriptChunkSection section in chunk.Sections)
        {
            if ((section.Flags & UnifiedScriptChunkSectionFlags.RuntimeRequired) != 0 &&
                (section.Flags & (UnifiedScriptChunkSectionFlags.Debug |
                    UnifiedScriptChunkSectionFlags.Editor)) != 0)
            {
                throw new InvalidDataException(
                    $"UnifiedScript program section '{section.Kind}' mixes runtime and sidecar flags.");
            }
        }
    }
}

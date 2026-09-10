using System.Text;
using Finare.Core;

namespace Finare.Tests;

public sealed class CompiledChunkTests
{
    [Fact]
    public void Write_IsDeterministicAndCanonicalAcrossInputOrder()
    {
        FinareChunkSection[] sections = CreateProgramSections();

        byte[] first = FinareChunkCodec.Write(sections);
        byte[] second = FinareChunkCodec.Write(sections.Reverse());

        Assert.Equal(first, second);
        Assert.Equal("USCHUNK\0", Encoding.ASCII.GetString(first, 0, 8));
        FinareChunk chunk = FinareProgramChunk.Read(first);
        Assert.Equal("1.0", chunk.FormatVersion);
        Assert.StartsWith("sha256:", chunk.ContentDigest, StringComparison.Ordinal);
        Assert.Equal(first.Length, chunk.ByteLength);
        Assert.Equal(
            sections.OrderBy(static section => (uint)section.Kind).Select(static section => section.Kind),
            chunk.Sections.Select(static section => section.Kind));
    }

    [Fact]
    public void Read_RoundTripsBinaryRuntimeAndOptionalSidecarSections()
    {
        FinareChunkSection[] sections =
        [
            .. CreateProgramSections(),
            Section(
                FinareChunkSectionKind.PortablePdb,
                FinareChunkSectionFlags.Debug,
                [0, 255, 127, 1]),
            Section(
                FinareChunkSectionKind.AuthoringSurface,
                FinareChunkSectionFlags.Editor,
                "authoring-sidecar"u8.ToArray()),
        ];

        FinareChunk chunk = FinareProgramChunk.Read(
            FinareChunkCodec.Write(sections));

        Assert.Equal(7, chunk.Sections.Count);
        Assert.Equal(
            new byte[] { 0, 255, 127, 1 },
            chunk.GetRequiredSection(FinareChunkSectionKind.PortablePdb).Data.ToArray());
        Assert.Equal(
            "authoring-sidecar",
            Encoding.UTF8.GetString(chunk.GetRequiredSection(
                FinareChunkSectionKind.AuthoringSurface).Data.Span));
    }

    [Fact]
    public void Read_RejectsCorruptedPayloadBeforeItCanBeLoaded()
    {
        byte[] bytes = FinareChunkCodec.Write(CreateProgramSections());
        bytes[^1] ^= 0x5a;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            FinareChunkCodec.Read(bytes));

        Assert.Contains("content digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfiledStreamReadOwnsOneWholeFileCopyAndDigest()
    {
        byte[] bytes = FinareChunkCodec.Write(CreateProgramSections());

        FinareChunkReadResult streamResult = FinareChunkCodec.ReadProfiled(
            new MemoryStream(bytes, writable: false));
        FinareChunkReadResult spanResult = FinareChunkCodec.ReadProfiled(bytes);

        Assert.Equal(streamResult.Chunk.ContentDigest, spanResult.Chunk.ContentDigest);
        Assert.Equal(bytes.Length, streamResult.Profile.ByteLength);
        Assert.Equal(bytes.Length, streamResult.Profile.StreamReadBytes);
        Assert.Equal(bytes.Length, streamResult.Profile.WholeFileCopyBytes);
        Assert.Equal(1, streamResult.Profile.WholeFileCopyCount);
        Assert.Equal(0, streamResult.Profile.StorageMaterializationBytes);
        Assert.Equal(bytes.Length, spanResult.Profile.WholeFileCopyBytes);
        Assert.Equal(1, spanResult.Profile.WholeFileCopyCount);
        Assert.True(streamResult.Profile.WholeFileDigestStopwatchTicks >= 0);
        Assert.True(streamResult.Profile.SectionDigestStopwatchTicks >= 0);
        Assert.True(streamResult.Profile.TotalStopwatchTicks >=
            streamResult.Profile.WholeFileDigestStopwatchTicks);
    }

    [Fact]
    public void Write_RejectsDuplicateSectionKinds()
    {
        FinareChunkSection section = Section(
            FinareChunkSectionKind.ManagedAssembly,
            FinareChunkSectionFlags.RuntimeRequired,
            [1]);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            FinareChunkCodec.Write([section, section]));

        Assert.Contains("unique", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProgramProfile_RejectsMissingOrMisflaggedRuntimeSections()
    {
        byte[] missing = FinareChunkCodec.Write(
            CreateProgramSections().Where(static section =>
                section.Kind != FinareChunkSectionKind.BindingManifest));
        InvalidDataException missingException = Assert.Throws<InvalidDataException>(() =>
            FinareProgramChunk.Read(missing));
        Assert.Contains("BindingManifest", missingException.Message, StringComparison.Ordinal);

        FinareChunkSection[] misflagged = CreateProgramSections()
            .Select(section => section.Kind == FinareChunkSectionKind.DeclarationData
                ? section with { Flags = FinareChunkSectionFlags.Editor }
                : section)
            .ToArray();
        InvalidDataException flagsException = Assert.Throws<InvalidDataException>(() =>
            FinareProgramChunk.Read(FinareChunkCodec.Write(misflagged)));
        Assert.Contains("runtime-required", flagsException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsUnsupportedFormatBeforeDigestEvaluation()
    {
        byte[] bytes = FinareChunkCodec.Write(CreateProgramSections());
        bytes[8] = 2;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            FinareChunkCodec.Read(bytes));

        Assert.Contains("Unsupported", exception.Message, StringComparison.Ordinal);
    }

    private static FinareChunkSection[] CreateProgramSections() =>
    [
        Section(
            FinareChunkSectionKind.RuntimeManifest,
            FinareChunkSectionFlags.RuntimeRequired,
            "runtime-v1"u8.ToArray()),
        Section(
            FinareChunkSectionKind.ManagedAssembly,
            FinareChunkSectionFlags.RuntimeRequired,
            [0x4d, 0x5a, 1, 2, 3, 4]),
        Section(
            FinareChunkSectionKind.DeclarationData,
            FinareChunkSectionFlags.RuntimeRequired,
            "declarations"u8.ToArray()),
        Section(
            FinareChunkSectionKind.BindingManifest,
            FinareChunkSectionFlags.RuntimeRequired,
            "bindings"u8.ToArray()),
        Section(
            FinareChunkSectionKind.DependencyManifest,
            FinareChunkSectionFlags.RuntimeRequired,
            "dependencies"u8.ToArray()),
    ];

    private static FinareChunkSection Section(
        FinareChunkSectionKind kind,
        FinareChunkSectionFlags flags,
        byte[] data) => new(kind, flags, data);
}

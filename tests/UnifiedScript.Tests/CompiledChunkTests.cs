using System.Text;
using UnifiedScript.Core;

namespace UnifiedScript.Tests;

public sealed class CompiledChunkTests
{
    [Fact]
    public void Write_IsDeterministicAndCanonicalAcrossInputOrder()
    {
        UnifiedScriptChunkSection[] sections = CreateProgramSections();

        byte[] first = UnifiedScriptChunkCodec.Write(sections);
        byte[] second = UnifiedScriptChunkCodec.Write(sections.Reverse());

        Assert.Equal(first, second);
        Assert.Equal("USCHUNK\0", Encoding.ASCII.GetString(first, 0, 8));
        UnifiedScriptChunk chunk = UnifiedScriptProgramChunk.Read(first);
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
        UnifiedScriptChunkSection[] sections =
        [
            .. CreateProgramSections(),
            Section(
                UnifiedScriptChunkSectionKind.PortablePdb,
                UnifiedScriptChunkSectionFlags.Debug,
                [0, 255, 127, 1]),
            Section(
                UnifiedScriptChunkSectionKind.AuthoringSurface,
                UnifiedScriptChunkSectionFlags.Editor,
                "authoring-sidecar"u8.ToArray()),
        ];

        UnifiedScriptChunk chunk = UnifiedScriptProgramChunk.Read(
            UnifiedScriptChunkCodec.Write(sections));

        Assert.Equal(7, chunk.Sections.Count);
        Assert.Equal(
            new byte[] { 0, 255, 127, 1 },
            chunk.GetRequiredSection(UnifiedScriptChunkSectionKind.PortablePdb).Data.ToArray());
        Assert.Equal(
            "authoring-sidecar",
            Encoding.UTF8.GetString(chunk.GetRequiredSection(
                UnifiedScriptChunkSectionKind.AuthoringSurface).Data.Span));
    }

    [Fact]
    public void Read_RejectsCorruptedPayloadBeforeItCanBeLoaded()
    {
        byte[] bytes = UnifiedScriptChunkCodec.Write(CreateProgramSections());
        bytes[^1] ^= 0x5a;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            UnifiedScriptChunkCodec.Read(bytes));

        Assert.Contains("content digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfiledStreamReadAccountsForEveryCurrentWholeFileCopyAndDigest()
    {
        byte[] bytes = UnifiedScriptChunkCodec.Write(CreateProgramSections());

        UnifiedScriptChunkReadResult streamResult = UnifiedScriptChunkCodec.ReadProfiled(
            new MemoryStream(bytes, writable: false));
        UnifiedScriptChunkReadResult spanResult = UnifiedScriptChunkCodec.ReadProfiled(bytes);

        Assert.Equal(streamResult.Chunk.ContentDigest, spanResult.Chunk.ContentDigest);
        Assert.Equal(bytes.Length, streamResult.Profile.ByteLength);
        Assert.Equal(bytes.Length, streamResult.Profile.StreamReadBytes);
        Assert.Equal(bytes.Length * 2L, streamResult.Profile.WholeFileCopyBytes);
        Assert.Equal(2, streamResult.Profile.WholeFileCopyCount);
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
        UnifiedScriptChunkSection section = Section(
            UnifiedScriptChunkSectionKind.ManagedAssembly,
            UnifiedScriptChunkSectionFlags.RuntimeRequired,
            [1]);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            UnifiedScriptChunkCodec.Write([section, section]));

        Assert.Contains("unique", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProgramProfile_RejectsMissingOrMisflaggedRuntimeSections()
    {
        byte[] missing = UnifiedScriptChunkCodec.Write(
            CreateProgramSections().Where(static section =>
                section.Kind != UnifiedScriptChunkSectionKind.BindingManifest));
        InvalidDataException missingException = Assert.Throws<InvalidDataException>(() =>
            UnifiedScriptProgramChunk.Read(missing));
        Assert.Contains("BindingManifest", missingException.Message, StringComparison.Ordinal);

        UnifiedScriptChunkSection[] misflagged = CreateProgramSections()
            .Select(section => section.Kind == UnifiedScriptChunkSectionKind.DeclarationData
                ? section with { Flags = UnifiedScriptChunkSectionFlags.Editor }
                : section)
            .ToArray();
        InvalidDataException flagsException = Assert.Throws<InvalidDataException>(() =>
            UnifiedScriptProgramChunk.Read(UnifiedScriptChunkCodec.Write(misflagged)));
        Assert.Contains("runtime-required", flagsException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsUnsupportedFormatBeforeDigestEvaluation()
    {
        byte[] bytes = UnifiedScriptChunkCodec.Write(CreateProgramSections());
        bytes[8] = 2;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            UnifiedScriptChunkCodec.Read(bytes));

        Assert.Contains("Unsupported", exception.Message, StringComparison.Ordinal);
    }

    private static UnifiedScriptChunkSection[] CreateProgramSections() =>
    [
        Section(
            UnifiedScriptChunkSectionKind.RuntimeManifest,
            UnifiedScriptChunkSectionFlags.RuntimeRequired,
            "runtime-v1"u8.ToArray()),
        Section(
            UnifiedScriptChunkSectionKind.ManagedAssembly,
            UnifiedScriptChunkSectionFlags.RuntimeRequired,
            [0x4d, 0x5a, 1, 2, 3, 4]),
        Section(
            UnifiedScriptChunkSectionKind.DeclarationData,
            UnifiedScriptChunkSectionFlags.RuntimeRequired,
            "declarations"u8.ToArray()),
        Section(
            UnifiedScriptChunkSectionKind.BindingManifest,
            UnifiedScriptChunkSectionFlags.RuntimeRequired,
            "bindings"u8.ToArray()),
        Section(
            UnifiedScriptChunkSectionKind.DependencyManifest,
            UnifiedScriptChunkSectionFlags.RuntimeRequired,
            "dependencies"u8.ToArray()),
    ];

    private static UnifiedScriptChunkSection Section(
        UnifiedScriptChunkSectionKind kind,
        UnifiedScriptChunkSectionFlags flags,
        byte[] data) => new(kind, flags, data);
}

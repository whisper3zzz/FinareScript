using System.Runtime.CompilerServices;

namespace Finare.Unreal;

public enum UnrealDataPlaneExtensionChannelKind : byte
{
    Snapshot = 1,
    Event = 2,
}

public enum UnrealDataPlaneExtensionPublicationPhase : byte
{
    Native = 1,
}

/// <summary>
/// Stable identity and compatibility fence for one generated data-plane
/// extension. The fingerprint covers the complete extension schema.
/// </summary>
public readonly record struct UnrealDataPlaneExtensionIdentity
{
    public UnrealDataPlaneExtensionIdentity(
        string id,
        ulong key,
        uint revision,
        uint requiredAbiVersion,
        ulong requiredLayoutCompatibilityId,
        ulong schemaFingerprint)
    {
        if (string.IsNullOrWhiteSpace(id) || key == 0 || revision == 0 ||
            requiredAbiVersion == 0 || requiredLayoutCompatibilityId == 0 ||
            schemaFingerprint == 0)
        {
            throw new ArgumentException(
                "A data-plane extension requires complete non-zero identity and compatibility metadata.");
        }
        Id = id;
        Key = key;
        Revision = revision;
        RequiredAbiVersion = requiredAbiVersion;
        RequiredLayoutCompatibilityId = requiredLayoutCompatibilityId;
        SchemaFingerprint = schemaFingerprint;
    }

    public string Id { get; }
    public ulong Key { get; }
    public uint Revision { get; }
    public uint RequiredAbiVersion { get; }
    public ulong RequiredLayoutCompatibilityId { get; }
    public ulong SchemaFingerprint { get; }

    public void RequireCompatibleRuntime()
    {
        if (RequiredAbiVersion != UnrealSharedDataAbiV4.Version ||
            RequiredLayoutCompatibilityId != UnrealDataPlaneLayoutV1.CompatibilityId)
        {
            throw new NotSupportedException(
                $"Data-plane extension '{Id}' requires ABI {RequiredAbiVersion} and " +
                $"layout 0x{RequiredLayoutCompatibilityId:X16}, but this runtime exposes ABI " +
                $"{UnrealSharedDataAbiV4.Version} and layout " +
                $"0x{UnrealDataPlaneLayoutV1.CompatibilityId:X16}.");
        }
    }
}

/// <summary>
/// Generated, typed channel descriptor. Native publication must register the
/// matching extension descriptor before a channel can contain rows.
/// </summary>
public readonly record struct UnrealDataPlaneExtensionChannel<T>
    where T : unmanaged
{
    public UnrealDataPlaneExtensionChannel(
        UnrealDataPlaneExtensionIdentity extension,
        string name,
        UnrealDataPlaneExtensionChannelKind kind,
        UnrealDataPlaneExtensionPublicationPhase publicationPhase,
        ulong schemaId,
        uint expectedEntrySize,
        uint maximumEntryCount,
        ulong maximumByteCount,
        string profileBoundary)
    {
        uint actualEntrySize = checked((uint)Unsafe.SizeOf<T>());
        ulong requiredBytes = checked((ulong)expectedEntrySize * maximumEntryCount);
        uint globalMaximumEntries = kind == UnrealDataPlaneExtensionChannelKind.Snapshot
            ? UnrealSharedDataAbiV4.MaximumGeneratedSnapshotCount
            : UnrealGameplayDataAbiV4.MaximumGeneratedEventCount;
        if (string.IsNullOrWhiteSpace(name) ||
            kind is not (UnrealDataPlaneExtensionChannelKind.Snapshot or
                UnrealDataPlaneExtensionChannelKind.Event) ||
            publicationPhase != UnrealDataPlaneExtensionPublicationPhase.Native ||
            schemaId == 0 || expectedEntrySize == 0 || actualEntrySize != expectedEntrySize ||
            maximumEntryCount == 0 || maximumEntryCount > globalMaximumEntries ||
            maximumByteCount < requiredBytes ||
            maximumByteCount > UnrealSharedDataAbiV4.MaximumGeneratedChannelBytes ||
            string.IsNullOrWhiteSpace(profileBoundary))
        {
            throw new ArgumentException(
                "A generated data-plane channel has invalid identity, layout, phase, or bounds.");
        }
        Extension = extension;
        Name = name;
        Kind = kind;
        PublicationPhase = publicationPhase;
        SchemaId = schemaId;
        EntrySize = expectedEntrySize;
        MaximumEntryCount = maximumEntryCount;
        MaximumByteCount = maximumByteCount;
        ProfileBoundary = profileBoundary;
    }

    public UnrealDataPlaneExtensionIdentity Extension { get; }
    public string Name { get; }
    public UnrealDataPlaneExtensionChannelKind Kind { get; }
    public UnrealDataPlaneExtensionPublicationPhase PublicationPhase { get; }
    public ulong SchemaId { get; }
    public uint EntrySize { get; }
    public uint MaximumEntryCount { get; }
    public ulong MaximumByteCount { get; }
    public string ProfileBoundary { get; }

    internal void RequireCompatible(
        UnrealDataPlaneExtensionChannelKind expectedKind)
    {
        Extension.RequireCompatibleRuntime();
        if (Kind != expectedKind || EntrySize != checked((uint)Unsafe.SizeOf<T>()))
        {
            throw new InvalidOperationException(
                $"Generated channel '{Extension.Id}/{Name}' does not match the requested row route.");
        }
    }
}

using System.Runtime.InteropServices;

namespace Finare.Unreal;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeEventFrameV4
{
    public uint AbiVersion;
    public uint FrameSize;
    public ulong SubscriptionId;
    public ulong Source;
    public ulong EventId;
    public nint Arguments;
    public uint ArgumentCount;
    public uint Reserved;
    public nint ByteBuffer;
    public uint ByteCount;
    public uint Reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeEventTransportTableV4
{
    public uint AbiVersion;
    public uint TableSize;
    public uint FrameSize;
    public uint ValueSize;
    public nint RetainContext;
    public nint ReleaseContext;
    public nint Subscribe;
    public nint Unsubscribe;
}

public static class UnrealNativeEventAbiV4
{
    public const uint Version = 4;
    public const int FrameSize = 64;
    public const int TransportTableSize = 48;
}

public interface IUnrealNativeEventTransport
{
    bool SupportsEvents { get; }

    IUnrealEventSubscription Subscribe(
        UnrealObjectHandle source,
        UnrealEventDescriptor descriptor,
        Delegate handler);
}

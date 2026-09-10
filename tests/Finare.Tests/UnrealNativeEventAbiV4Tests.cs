using System.Runtime.InteropServices;
using Finare.Unreal;

namespace Finare.Tests;

public sealed class UnrealNativeEventAbiV4Tests
{
    [Fact]
    public void EventFrameLayoutMatchesNativeContract()
    {
        Assert.Equal(
            UnrealNativeEventAbiV4.FrameSize,
            Marshal.SizeOf<UnrealNativeEventFrameV4>());
        Assert.Equal(0, OffsetOf<UnrealNativeEventFrameV4>(nameof(UnrealNativeEventFrameV4.AbiVersion)));
        Assert.Equal(8, OffsetOf<UnrealNativeEventFrameV4>(nameof(UnrealNativeEventFrameV4.SubscriptionId)));
        Assert.Equal(32, OffsetOf<UnrealNativeEventFrameV4>(nameof(UnrealNativeEventFrameV4.Arguments)));
        Assert.Equal(48, OffsetOf<UnrealNativeEventFrameV4>(nameof(UnrealNativeEventFrameV4.ByteBuffer)));
        Assert.Equal(60, OffsetOf<UnrealNativeEventFrameV4>(nameof(UnrealNativeEventFrameV4.Reserved2)));
    }

    [Fact]
    public void EventTransportTableLayoutMatchesNativeContract()
    {
        Assert.Equal(
            UnrealNativeEventAbiV4.TransportTableSize,
            Marshal.SizeOf<UnrealNativeEventTransportTableV4>());
        Assert.Equal(0, OffsetOf<UnrealNativeEventTransportTableV4>(nameof(UnrealNativeEventTransportTableV4.AbiVersion)));
        Assert.Equal(16, OffsetOf<UnrealNativeEventTransportTableV4>(nameof(UnrealNativeEventTransportTableV4.RetainContext)));
        Assert.Equal(32, OffsetOf<UnrealNativeEventTransportTableV4>(nameof(UnrealNativeEventTransportTableV4.Subscribe)));
        Assert.Equal(40, OffsetOf<UnrealNativeEventTransportTableV4>(nameof(UnrealNativeEventTransportTableV4.Unsubscribe)));
    }

    private static int OffsetOf<T>(string fieldName)
        where T : struct => Marshal.OffsetOf<T>(fieldName).ToInt32();
}

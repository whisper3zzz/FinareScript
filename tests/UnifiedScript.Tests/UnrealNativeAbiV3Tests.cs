using System.Runtime.InteropServices;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealNativeAbiV3Tests
{
    [Fact]
    public void ValueLayoutMatchesNativeContract()
    {
        Assert.Equal(UnrealNativeAbiV3.ValueSize, Marshal.SizeOf<UnrealNativeValueV3>());
        Assert.Equal(0, OffsetOf<UnrealNativeValueV3>(nameof(UnrealNativeValueV3.Kind)));
        Assert.Equal(1, OffsetOf<UnrealNativeValueV3>(nameof(UnrealNativeValueV3.Flags)));
        Assert.Equal(4, OffsetOf<UnrealNativeValueV3>(nameof(UnrealNativeValueV3.Auxiliary)));
        Assert.Equal(8, OffsetOf<UnrealNativeValueV3>(nameof(UnrealNativeValueV3.TypeId)));
        Assert.Equal(16, OffsetOf<UnrealNativeValueV3>(nameof(UnrealNativeValueV3.Payload0)));
        Assert.Equal(40, OffsetOf<UnrealNativeValueV3>(nameof(UnrealNativeValueV3.Payload3)));
    }

    [Fact]
    public void CallFrameLayoutMatchesNativeContract()
    {
        Assert.Equal(UnrealNativeAbiV3.CallFrameSize, Marshal.SizeOf<UnrealNativeCallFrameV3>());
        Assert.Equal(0, OffsetOf<UnrealNativeCallFrameV3>(nameof(UnrealNativeCallFrameV3.Arguments)));
        Assert.Equal(16, OffsetOf<UnrealNativeCallFrameV3>(nameof(UnrealNativeCallFrameV3.Results)));
        Assert.Equal(32, OffsetOf<UnrealNativeCallFrameV3>(nameof(UnrealNativeCallFrameV3.ByteBuffer)));
        Assert.Equal(48, OffsetOf<UnrealNativeCallFrameV3>(nameof(UnrealNativeCallFrameV3.RequiredResultCapacity)));
        Assert.Equal(58, OffsetOf<UnrealNativeCallFrameV3>(nameof(UnrealNativeCallFrameV3.Status)));
        Assert.Equal(60, OffsetOf<UnrealNativeCallFrameV3>(nameof(UnrealNativeCallFrameV3.ErrorMessageOffset)));
        Assert.Equal(68, OffsetOf<UnrealNativeCallFrameV3>(nameof(UnrealNativeCallFrameV3.Reserved32)));
    }

    [Fact]
    public void TransportTableLayoutMatchesNativeContract()
    {
        Assert.Equal(UnrealNativeAbiV3.TransportTableSize, Marshal.SizeOf<UnrealNativeTransportTableV3>());
        Assert.Equal(0, OffsetOf<UnrealNativeTransportTableV3>(nameof(UnrealNativeTransportTableV3.AbiVersion)));
        Assert.Equal(16, OffsetOf<UnrealNativeTransportTableV3>(nameof(UnrealNativeTransportTableV3.Capabilities)));
        Assert.Equal(24, OffsetOf<UnrealNativeTransportTableV3>(nameof(UnrealNativeTransportTableV3.RetainContext)));
        Assert.Equal(56, OffsetOf<UnrealNativeTransportTableV3>(nameof(UnrealNativeTransportTableV3.InvokeById)));
        Assert.Equal(
            UnrealNativeAbiV3.ScalarTransportTableSize,
            Marshal.SizeOf<UnrealNativeScalarTransportTableV3>());
        Assert.Equal(
            UnrealNativeAbiV3.FixedTransportTableSize,
            Marshal.SizeOf<UnrealNativeFixedTransportTableV3>());
        Assert.Equal(
            64,
            OffsetOf<UnrealNativeScalarTransportTableV3>(
                nameof(UnrealNativeScalarTransportTableV3.InvokeInt32x2ById)));
        Assert.Equal(
            72,
            OffsetOf<UnrealNativeFixedTransportTableV3>(
                nameof(UnrealNativeFixedTransportTableV3.InvokeFixedById)));
    }

    [Fact]
    public void StableEnumValuesAreAppendOnly()
    {
        Assert.Equal(0, (byte)UnrealNativeValueKindV3.Void);
        Assert.Equal(12, (byte)UnrealNativeValueKindV3.Enum);
        Assert.Equal(18, (byte)UnrealNativeValueKindV3.Vector3);
        Assert.Equal(24, (byte)UnrealNativeValueKindV3.Map);
        Assert.Equal(5, (byte)UnrealNativeCallStatusV3.TransportUnavailable);
    }

    [Fact]
    public void SoftHandlesRoundTripAsUtf8AssetPathsWithoutAbiExpansion()
    {
        Span<byte> arena = stackalloc byte[256];
        int byteCount = 0;
        var softObject = new UnrealSoftObjectHandle("/Game/Items/BP_Item.BP_Item");
        Assert.True(UnrealNativeValueCodecV3.TryEncode(
            softObject,
            arena,
            ref byteCount,
            out UnrealNativeValueV3 encodedObject,
            out UnrealNativeCapabilitiesV3 objectCapability));
        Assert.Equal(UnrealNativeValueKindV3.String, encodedObject.Kind);
        Assert.Equal(UnrealNativeCapabilitiesV3.Utf8Text, objectCapability);
        Assert.True(UnrealNativeValueCodecV3.TryDecode(
            encodedObject,
            arena,
            byteCount,
            out UnrealSoftObjectHandle decodedObject));
        Assert.Equal(softObject, decodedObject);

        int classByteCount = 0;
        var softClass = new UnrealSoftClassHandle("/Game/Pawns/BP_Pawn.BP_Pawn_C");
        Assert.True(UnrealNativeValueCodecV3.TryEncode(
            softClass,
            arena,
            ref classByteCount,
            out UnrealNativeValueV3 encodedClass,
            out _));
        Assert.True(UnrealNativeValueCodecV3.TryDecode(
            encodedClass,
            arena,
            classByteCount,
            out UnrealSoftClassHandle decodedClass));
        Assert.Equal(softClass, decodedClass);
    }

    private static int OffsetOf<T>(string fieldName)
        where T : struct => Marshal.OffsetOf<T>(fieldName).ToInt32();
}

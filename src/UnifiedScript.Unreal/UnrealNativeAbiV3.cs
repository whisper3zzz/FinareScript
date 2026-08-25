using System.Runtime.InteropServices;

namespace UnifiedScript.Unreal;

/// <summary>
/// Stable value-kind numbers shared with FUnifiedScriptNativeValueV3.
/// Append-only after ABI v3 is published.
/// </summary>
public enum UnrealNativeValueKindV3 : byte
{
    Void = 0,
    Boolean = 1,
    Int8 = 2,
    UInt8 = 3,
    Int16 = 4,
    UInt16 = 5,
    Int32 = 6,
    UInt32 = 7,
    Int64 = 8,
    UInt64 = 9,
    Float32 = 10,
    Float64 = 11,
    Enum = 12,
    Name = 13,
    String = 14,
    Text = 15,
    Object = 16,
    Class = 17,
    Vector3 = 18,
    Rotator3 = 19,
    Transform = 20,
    Struct = 21,
    Array = 22,
    Set = 23,
    Map = 24,
}

[Flags]
public enum UnrealNativeValueFlagsV3 : byte
{
    None = 0,
    Null = 1 << 0,
    ArenaBacked = 1 << 1,
}

public enum UnrealNativeCallStatusV3 : byte
{
    Uninitialized = 0,
    Success = 1,
    InvocationFailure = 2,
    BufferTooSmall = 3,
    InvalidFrame = 4,
    TransportUnavailable = 5,
}

[Flags]
public enum UnrealNativeCapabilitiesV3 : ulong
{
    None = 0,
    FixedScalars = 1UL << 0,
    Enums = 1UL << 1,
    ObjectHandles = 1UL << 2,
    VectorAndRotator = 1UL << 3,
    Transform = 1UL << 4,
    Utf8Text = 1UL << 5,
    Containers = 1UL << 6,
    OutputAndReference = 1UL << 7,
    Utf8Results = 1UL << 8,
    ContainerResults = 1UL << 9,
}

/// <summary>
/// Fixed 48-byte value cell used by ABI v3. Variable-size payloads use
/// offsets into the caller-owned call-frame byte arena.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeValueV3
{
    public UnrealNativeValueKindV3 Kind;
    public UnrealNativeValueFlagsV3 Flags;
    public ushort Reserved16;
    public uint Auxiliary;
    public ulong TypeId;
    public ulong Payload0;
    public ulong Payload1;
    public ulong Payload2;
    public ulong Payload3;
}

/// <summary>
/// Fixed 72-byte synchronous request/response frame used by ABI v3. All
/// pointers are borrowed only for the duration of the native call.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeCallFrameV3
{
    public nint Arguments;
    public uint ArgumentCount;
    public uint Flags;
    public nint Results;
    public uint ResultCapacity;
    // In/out: expected result descriptors on entry, produced cells on success.
    public uint ResultCount;
    public nint ByteBuffer;
    public uint ByteCapacity;
    // In/out arena cursor: input bytes occupied on entry, total bytes used on success.
    public uint ByteCount;
    public uint RequiredResultCapacity;
    public uint RequiredByteCapacity;
    public byte InvocationError;
    public byte HandleError;
    public UnrealNativeCallStatusV3 Status;
    public byte Reserved8;
    public uint ErrorMessageOffset;
    public uint ErrorMessageLength;
    public uint Reserved32;
}

/// <summary>
/// Independent 64-byte ABI v3 transport table. Capabilities are advertised
/// only when all relevant entry points implement the call-frame contract.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeTransportTableV3
{
    public uint AbiVersion;
    public uint TableSize;
    public uint ValueSize;
    public uint CallFrameSize;
    public UnrealNativeCapabilitiesV3 Capabilities;
    public nint RetainContext;
    public nint ReleaseContext;
    public nint ReadPropertyById;
    public nint WritePropertyById;
    public nint InvokeById;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeScalarTransportTableV3
{
    public UnrealNativeTransportTableV3 Base;
    public nint InvokeInt32x2ById;
}

public static class UnrealNativeAbiV3
{
    public const uint Version = 3;
    public const int ValueSize = 48;
    public const int CallFrameSize = 72;
    public const int TransportTableSize = 64;
    public const int ScalarTransportTableSize = 72;
}

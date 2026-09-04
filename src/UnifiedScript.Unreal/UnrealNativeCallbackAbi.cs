using System.Runtime.InteropServices;

namespace UnifiedScript.Unreal;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeCallbackRequest
{
    public uint AbiVersion, Size;
    public ulong Token, Scope, SignatureId;
    public nint Arguments;
    public uint ArgumentCount, ByteCount;
    public nint ByteBuffer;
    public ulong Reserved;
}

/// <summary>Owned response. The native caller must invoke Release exactly once after consuming it.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeCallbackResponse
{
    public uint AbiVersion, Size;
    public UnrealCallbackStatus Status;
    public byte Reserved0;
    public ushort Reserved1;
    public uint ResultCount;
    public nint Results, ByteBuffer;
    public uint ByteCount, Reserved2;
    public nint Owner, Release;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UnrealNativeCallbackTransportTable
{
    public uint AbiVersion, TableSize, RequestSize, ResponseSize;
    public nint RetainContext, ReleaseContext, OpenScope, CloseScope, RegisterCallback, ReleaseCallback;
}

public static class UnrealNativeCallbackAbi
{
    public const uint Version = 1;
    public const int RequestSize = 64, ResponseSize = 56, TableSize = 64;
    public const int MaximumArguments = 16, MaximumBytes = 16 * 1024 * 1024;
}

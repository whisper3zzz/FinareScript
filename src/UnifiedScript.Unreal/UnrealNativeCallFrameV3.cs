using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace UnifiedScript.Unreal;

public enum UnrealNativeOperationV3 : byte
{
    ReadProperty,
    WriteProperty,
    Invoke,
}

public readonly record struct UnrealNativeCallOutcomeV3(
    UnrealNativeCallStatusV3 Status,
    UnrealInvocationError InvocationError,
    UnrealObjectHandleError HandleError,
    int ResultCount,
    int ByteCount,
    int RequiredResultCapacity,
    int RequiredByteCapacity,
    string Message)
{
    public static UnrealNativeCallOutcomeV3 Succeeded(int resultCount, int byteCount) =>
        new(
            UnrealNativeCallStatusV3.Success,
            UnrealInvocationError.None,
            UnrealObjectHandleError.None,
            resultCount,
            byteCount,
            resultCount,
            byteCount,
            string.Empty);
}

/// <summary>
/// Span-based ABI v3 transport boundary. Implementations borrow every span for
/// one synchronous call and must not retain managed references.
/// </summary>
public interface IUnrealNativeCallFrameTransportV3
{
    UnrealNativeCapabilitiesV3 Capabilities { get; }

    UnrealNativeCallOutcomeV3 ExecuteById(
        UnrealNativeOperationV3 operation,
        UnrealObjectHandle target,
        ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments,
        Span<UnrealNativeValueV3> results,
        Span<byte> byteBuffer,
        int inputByteCount);
}

internal interface IUnrealNativeScalarInvocationTransportV3
{
    bool SupportsInt32x2Invocation { get; }

    UnrealNativeCallOutcomeV3 ExecuteInt32x2ById(
        UnrealObjectHandle target,
        ulong memberId,
        int argument0,
        int argument1,
        out int result);
}

internal interface IUnrealNativeFixedInvocationTransportV3
{
    bool SupportsFixedInvocation { get; }

    UnrealNativeCallOutcomeV3 ExecuteFixedById(
        UnrealObjectHandle target,
        ulong memberId,
        ReadOnlySpan<UnrealNativeValueV3> arguments,
        Span<UnrealNativeValueV3> results);
}

internal static class UnrealNativeFixedValueCodecV3
{
    public static bool TryEncode<T>(T value, out UnrealNativeValueV3 encoded)
    {
        encoded = default;
        if (!IsSupported(typeof(T)))
        {
            return false;
        }
        int byteCount = 0;
        return UnrealNativeValueCodecV3.TryEncode(
            value,
            Span<byte>.Empty,
            ref byteCount,
            out encoded,
            out _) &&
            byteCount == 0;
    }

    public static bool TryCreateResultDescriptor<T>(out UnrealNativeValueV3 descriptor)
    {
        descriptor = default;
        return IsSupported(typeof(T)) &&
            UnrealNativeValueCodecV3.TryCreateResultDescriptor<T>(out descriptor, out _);
    }

    public static bool TryDecode<T>(in UnrealNativeValueV3 value, out T decoded)
    {
        decoded = default!;
        return IsSupported(typeof(T)) &&
            UnrealNativeValueCodecV3.TryDecode(
                in value,
                ReadOnlySpan<byte>.Empty,
                0,
                out decoded);
    }

    private static bool IsSupported(Type type) =>
        type == typeof(int) ||
        type == typeof(float) ||
        type == typeof(bool) ||
        type == typeof(UnrealObjectHandle) ||
        type == typeof(UnrealVector);
}

internal static class UnrealNativeValueCodecV3
{
    internal const int TransformByteSize = 10 * sizeof(double);

    public static bool TryEncode<T>(
        T value,
        Span<byte> arena,
        ref int byteCount,
        out UnrealNativeValueV3 encoded,
        out UnrealNativeCapabilitiesV3 requiredCapability)
    {
        encoded = default;
        requiredCapability = UnrealNativeCapabilitiesV3.FixedScalars;
        if (typeof(T) == typeof(bool))
        {
            encoded.Kind = UnrealNativeValueKindV3.Boolean;
            encoded.Payload0 = Reinterpret<T, bool>(ref value) ? 1UL : 0UL;
            return true;
        }
        if (typeof(T) == typeof(sbyte))
        {
            encoded.Kind = UnrealNativeValueKindV3.Int8;
            encoded.Payload0 = unchecked((ulong)Reinterpret<T, sbyte>(ref value));
            return true;
        }
        if (typeof(T) == typeof(byte))
        {
            encoded.Kind = UnrealNativeValueKindV3.UInt8;
            encoded.Payload0 = Reinterpret<T, byte>(ref value);
            return true;
        }
        if (typeof(T) == typeof(short))
        {
            encoded.Kind = UnrealNativeValueKindV3.Int16;
            encoded.Payload0 = unchecked((ulong)Reinterpret<T, short>(ref value));
            return true;
        }
        if (typeof(T) == typeof(ushort))
        {
            encoded.Kind = UnrealNativeValueKindV3.UInt16;
            encoded.Payload0 = Reinterpret<T, ushort>(ref value);
            return true;
        }
        if (typeof(T) == typeof(int))
        {
            encoded.Kind = UnrealNativeValueKindV3.Int32;
            encoded.Payload0 = unchecked((ulong)Reinterpret<T, int>(ref value));
            return true;
        }
        if (typeof(T) == typeof(uint))
        {
            encoded.Kind = UnrealNativeValueKindV3.UInt32;
            encoded.Payload0 = Reinterpret<T, uint>(ref value);
            return true;
        }
        if (typeof(T) == typeof(long))
        {
            encoded.Kind = UnrealNativeValueKindV3.Int64;
            encoded.Payload0 = unchecked((ulong)Reinterpret<T, long>(ref value));
            return true;
        }
        if (typeof(T) == typeof(ulong))
        {
            encoded.Kind = UnrealNativeValueKindV3.UInt64;
            encoded.Payload0 = Reinterpret<T, ulong>(ref value);
            return true;
        }
        if (typeof(T) == typeof(float))
        {
            encoded.Kind = UnrealNativeValueKindV3.Float32;
            encoded.Payload0 = unchecked((uint)BitConverter.SingleToInt32Bits(
                Reinterpret<T, float>(ref value)));
            return true;
        }
        if (typeof(T) == typeof(double))
        {
            encoded.Kind = UnrealNativeValueKindV3.Float64;
            encoded.Payload0 = unchecked((ulong)BitConverter.DoubleToInt64Bits(
                Reinterpret<T, double>(ref value)));
            return true;
        }
        if (typeof(T) == typeof(UnrealObjectHandle))
        {
            requiredCapability = UnrealNativeCapabilitiesV3.ObjectHandles;
            encoded.Kind = UnrealNativeValueKindV3.Object;
            encoded.Payload0 = Reinterpret<T, UnrealObjectHandle>(ref value).Value;
            return true;
        }
        if (typeof(T) == typeof(UnrealClassHandle))
        {
            requiredCapability = UnrealNativeCapabilitiesV3.ObjectHandles;
            encoded.Kind = UnrealNativeValueKindV3.Class;
            encoded.Payload0 = Reinterpret<T, UnrealClassHandle>(ref value).Value;
            return true;
        }
        if (typeof(T) == typeof(string))
        {
            requiredCapability = UnrealNativeCapabilitiesV3.Utf8Text;
            return TryEncodeUtf8(
                Reinterpret<T, string>(ref value) ?? string.Empty,
                UnrealNativeValueKindV3.String,
                arena,
                ref byteCount,
                out encoded);
        }
        if (typeof(T) == typeof(UnrealName))
        {
            requiredCapability = UnrealNativeCapabilitiesV3.Utf8Text;
            return TryEncodeUtf8(
                Reinterpret<T, UnrealName>(ref value).Value,
                UnrealNativeValueKindV3.Name,
                arena,
                ref byteCount,
                out encoded);
        }
        if (typeof(T) == typeof(UnrealText))
        {
            requiredCapability = UnrealNativeCapabilitiesV3.Utf8Text;
            return TryEncodeUtf8(
                Reinterpret<T, UnrealText>(ref value).Value,
                UnrealNativeValueKindV3.Text,
                arena,
                ref byteCount,
                out encoded);
        }
        if (typeof(T) == typeof(UnrealEnumValue))
        {
            UnrealEnumValue enumValue = Reinterpret<T, UnrealEnumValue>(ref value);
            ulong typeId = UnrealTypeIdV3.FromPath(enumValue.TypePath);
            if (typeId == 0)
            {
                return false;
            }
            requiredCapability = UnrealNativeCapabilitiesV3.Enums;
            encoded.Kind = UnrealNativeValueKindV3.Enum;
            encoded.TypeId = typeId;
            encoded.Payload0 = unchecked((ulong)enumValue.Value);
            return true;
        }
        if (typeof(T) == typeof(UnrealVector))
        {
            requiredCapability = UnrealNativeCapabilitiesV3.VectorAndRotator;
            UnrealVector vector = Reinterpret<T, UnrealVector>(ref value);
            encoded.Kind = UnrealNativeValueKindV3.Vector3;
            encoded.Payload0 = DoubleBits(vector.X);
            encoded.Payload1 = DoubleBits(vector.Y);
            encoded.Payload2 = DoubleBits(vector.Z);
            return true;
        }
        if (typeof(T) == typeof(UnrealRotator))
        {
            requiredCapability = UnrealNativeCapabilitiesV3.VectorAndRotator;
            UnrealRotator rotator = Reinterpret<T, UnrealRotator>(ref value);
            encoded.Kind = UnrealNativeValueKindV3.Rotator3;
            encoded.Payload0 = DoubleBits(rotator.Pitch);
            encoded.Payload1 = DoubleBits(rotator.Yaw);
            encoded.Payload2 = DoubleBits(rotator.Roll);
            return true;
        }
        if (typeof(T) == typeof(UnrealTransform))
        {
            requiredCapability = UnrealNativeCapabilitiesV3.Transform;
            if (byteCount < 0 || arena.Length - byteCount < TransformByteSize)
            {
                return false;
            }
            UnrealTransform transform = Reinterpret<T, UnrealTransform>(ref value);
            int offset = byteCount;
            WriteDouble(arena, offset + 0 * sizeof(double), transform.Rotation.X);
            WriteDouble(arena, offset + 1 * sizeof(double), transform.Rotation.Y);
            WriteDouble(arena, offset + 2 * sizeof(double), transform.Rotation.Z);
            WriteDouble(arena, offset + 3 * sizeof(double), transform.Rotation.W);
            WriteDouble(arena, offset + 4 * sizeof(double), transform.Translation.X);
            WriteDouble(arena, offset + 5 * sizeof(double), transform.Translation.Y);
            WriteDouble(arena, offset + 6 * sizeof(double), transform.Translation.Z);
            WriteDouble(arena, offset + 7 * sizeof(double), transform.Scale3D.X);
            WriteDouble(arena, offset + 8 * sizeof(double), transform.Scale3D.Y);
            WriteDouble(arena, offset + 9 * sizeof(double), transform.Scale3D.Z);
            byteCount += TransformByteSize;
            encoded.Kind = UnrealNativeValueKindV3.Transform;
            encoded.Flags = UnrealNativeValueFlagsV3.ArenaBacked;
            encoded.Payload0 = (ulong)offset;
            encoded.Payload1 = TransformByteSize;
            return true;
        }
        if (TryGetStructShape(typeof(T), out UnrealStructShape structShape))
        {
            return TryEncodeStruct(
                value,
                structShape,
                arena,
                ref byteCount,
                out encoded,
                out requiredCapability);
        }
        if (TryGetContainerShape(
                typeof(T),
                out UnrealNativeValueKindV3 containerKind,
                out Type? elementType,
                out Type? keyType,
                out Type? valueType,
                out string? nativeName))
        {
            return TryEncodeContainer(
                value,
                containerKind,
                elementType,
                keyType,
                valueType,
                nativeName,
                arena,
                ref byteCount,
                out encoded,
                out requiredCapability);
        }
        return false;
    }

    public static bool TryEncodeObject(
        object? value,
        Type type,
        Span<byte> arena,
        ref int byteCount,
        out UnrealNativeValueV3 encoded,
        out UnrealNativeCapabilitiesV3 requiredCapability)
    {
        ArgumentNullException.ThrowIfNull(type);
        return TryEncodeDynamic(
            value,
            type,
            arena,
            ref byteCount,
            out encoded,
            out requiredCapability);
    }

    public static bool TryCreateResultDescriptor<T>(
        out UnrealNativeValueV3 descriptor,
        out UnrealNativeCapabilitiesV3 requiredCapability) =>
        TryCreateResultDescriptor(typeof(T), out descriptor, out requiredCapability);

    public static bool TryCreateResultDescriptor(
        Type type,
        out UnrealNativeValueV3 descriptor,
        out UnrealNativeCapabilitiesV3 requiredCapability)
    {
        ArgumentNullException.ThrowIfNull(type);
        descriptor = default;
        requiredCapability = UnrealNativeCapabilitiesV3.FixedScalars;
        if (type == typeof(bool)) descriptor.Kind = UnrealNativeValueKindV3.Boolean;
        else if (type == typeof(sbyte)) descriptor.Kind = UnrealNativeValueKindV3.Int8;
        else if (type == typeof(byte)) descriptor.Kind = UnrealNativeValueKindV3.UInt8;
        else if (type == typeof(short)) descriptor.Kind = UnrealNativeValueKindV3.Int16;
        else if (type == typeof(ushort)) descriptor.Kind = UnrealNativeValueKindV3.UInt16;
        else if (type == typeof(int)) descriptor.Kind = UnrealNativeValueKindV3.Int32;
        else if (type == typeof(uint)) descriptor.Kind = UnrealNativeValueKindV3.UInt32;
        else if (type == typeof(long)) descriptor.Kind = UnrealNativeValueKindV3.Int64;
        else if (type == typeof(ulong)) descriptor.Kind = UnrealNativeValueKindV3.UInt64;
        else if (type == typeof(float)) descriptor.Kind = UnrealNativeValueKindV3.Float32;
        else if (type == typeof(double)) descriptor.Kind = UnrealNativeValueKindV3.Float64;
        else if (type == typeof(UnrealObjectHandle))
        {
            descriptor.Kind = UnrealNativeValueKindV3.Object;
            requiredCapability = UnrealNativeCapabilitiesV3.ObjectHandles;
        }
        else if (type == typeof(UnrealClassHandle))
        {
            descriptor.Kind = UnrealNativeValueKindV3.Class;
            requiredCapability = UnrealNativeCapabilitiesV3.ObjectHandles;
        }
        else if (type == typeof(string))
        {
            descriptor.Kind = UnrealNativeValueKindV3.String;
            descriptor.Flags = UnrealNativeValueFlagsV3.ArenaBacked;
            requiredCapability = UnrealNativeCapabilitiesV3.Utf8Results;
        }
        else if (type == typeof(UnrealName))
        {
            descriptor.Kind = UnrealNativeValueKindV3.Name;
            descriptor.Flags = UnrealNativeValueFlagsV3.ArenaBacked;
            requiredCapability = UnrealNativeCapabilitiesV3.Utf8Results;
        }
        else if (type == typeof(UnrealText))
        {
            descriptor.Kind = UnrealNativeValueKindV3.Text;
            descriptor.Flags = UnrealNativeValueFlagsV3.ArenaBacked;
            requiredCapability = UnrealNativeCapabilitiesV3.Utf8Results;
        }
        else if (type == typeof(UnrealVector))
        {
            descriptor.Kind = UnrealNativeValueKindV3.Vector3;
            requiredCapability = UnrealNativeCapabilitiesV3.VectorAndRotator;
        }
        else if (type == typeof(UnrealRotator))
        {
            descriptor.Kind = UnrealNativeValueKindV3.Rotator3;
            requiredCapability = UnrealNativeCapabilitiesV3.VectorAndRotator;
        }
        else if (type == typeof(UnrealTransform))
        {
            descriptor.Kind = UnrealNativeValueKindV3.Transform;
            descriptor.Flags = UnrealNativeValueFlagsV3.ArenaBacked;
            descriptor.Payload1 = TransformByteSize;
            requiredCapability = UnrealNativeCapabilitiesV3.Transform;
        }
        else if (TryGetStructShape(type, out UnrealStructShape structShape))
        {
            descriptor.Kind = UnrealNativeValueKindV3.Struct;
            descriptor.Flags = UnrealNativeValueFlagsV3.ArenaBacked;
            descriptor.TypeId = UnrealTypeIdV3.FromPath(structShape.Attribute.Path);
            descriptor.Payload2 = (ulong)structShape.Fields.Length;
            descriptor.Payload3 = UnrealNativeAbiV3.ValueSize;
            requiredCapability = UnrealNativeCapabilitiesV3.Structs;
        }
        else if (TryGetContainerShape(
                     type,
                     out UnrealNativeValueKindV3 containerKind,
                     out _,
                     out _,
                     out _,
                     out string? nativeName))
        {
            descriptor.Kind = containerKind;
            descriptor.Flags = UnrealNativeValueFlagsV3.ArenaBacked;
            descriptor.TypeId = UnrealTypeIdV3.FromPath(nativeName);
            descriptor.Payload3 = UnrealNativeAbiV3.ValueSize;
            requiredCapability = UnrealNativeCapabilitiesV3.ContainerResults;
        }
        else
        {
            return false;
        }
        return true;
    }

    public static bool TryDecode<T>(
        in UnrealNativeValueV3 value,
        ReadOnlySpan<byte> arena,
        int byteCount,
        out T decoded)
    {
        decoded = default!;
        if (typeof(T) == typeof(bool) && value.Kind == UnrealNativeValueKindV3.Boolean)
            return Set(value.Payload0 != 0, out decoded);
        if (typeof(T) == typeof(sbyte) && value.Kind == UnrealNativeValueKindV3.Int8)
            return Set(unchecked((sbyte)value.Payload0), out decoded);
        if (typeof(T) == typeof(byte) && value.Kind == UnrealNativeValueKindV3.UInt8)
            return Set((byte)value.Payload0, out decoded);
        if (typeof(T) == typeof(short) && value.Kind == UnrealNativeValueKindV3.Int16)
            return Set(unchecked((short)value.Payload0), out decoded);
        if (typeof(T) == typeof(ushort) && value.Kind == UnrealNativeValueKindV3.UInt16)
            return Set((ushort)value.Payload0, out decoded);
        if (typeof(T) == typeof(int) && value.Kind == UnrealNativeValueKindV3.Int32)
            return Set(unchecked((int)value.Payload0), out decoded);
        if (typeof(T) == typeof(uint) && value.Kind == UnrealNativeValueKindV3.UInt32)
            return Set((uint)value.Payload0, out decoded);
        if (typeof(T) == typeof(long) && value.Kind == UnrealNativeValueKindV3.Int64)
            return Set(unchecked((long)value.Payload0), out decoded);
        if (typeof(T) == typeof(ulong) && value.Kind == UnrealNativeValueKindV3.UInt64)
            return Set(value.Payload0, out decoded);
        if (typeof(T) == typeof(float) && value.Kind == UnrealNativeValueKindV3.Float32)
            return Set(BitConverter.Int32BitsToSingle(unchecked((int)value.Payload0)), out decoded);
        if (typeof(T) == typeof(double) && value.Kind == UnrealNativeValueKindV3.Float64)
            return Set(BitConverter.Int64BitsToDouble(unchecked((long)value.Payload0)), out decoded);
        if (typeof(T) == typeof(UnrealObjectHandle) &&
            value.Kind == UnrealNativeValueKindV3.Object)
            return Set(new UnrealObjectHandle(value.Payload0), out decoded);
        if (typeof(T) == typeof(UnrealClassHandle) &&
            value.Kind == UnrealNativeValueKindV3.Class)
            return Set(new UnrealClassHandle(value.Payload0), out decoded);
        if (typeof(T) == typeof(string) && value.Kind == UnrealNativeValueKindV3.String &&
            TryDecodeUtf8(value, arena, byteCount, out string? stringValue))
            return Set(stringValue, out decoded);
        if (typeof(T) == typeof(UnrealName) && value.Kind == UnrealNativeValueKindV3.Name &&
            TryDecodeUtf8(value, arena, byteCount, out string? nameValue))
            return Set(new UnrealName(nameValue), out decoded);
        if (typeof(T) == typeof(UnrealText) && value.Kind == UnrealNativeValueKindV3.Text &&
            TryDecodeUtf8(value, arena, byteCount, out string? textValue))
            return Set(new UnrealText(textValue), out decoded);
        if (typeof(T) == typeof(UnrealVector) && value.Kind == UnrealNativeValueKindV3.Vector3)
            return Set(new UnrealVector(
                BitsDouble(value.Payload0),
                BitsDouble(value.Payload1),
                BitsDouble(value.Payload2)), out decoded);
        if (typeof(T) == typeof(UnrealRotator) && value.Kind == UnrealNativeValueKindV3.Rotator3)
            return Set(new UnrealRotator(
                BitsDouble(value.Payload0),
                BitsDouble(value.Payload1),
                BitsDouble(value.Payload2)), out decoded);
        if (typeof(T) == typeof(UnrealTransform) &&
            value.Kind == UnrealNativeValueKindV3.Transform &&
            value.Flags == UnrealNativeValueFlagsV3.ArenaBacked &&
            value.Payload0 <= int.MaxValue &&
            value.Payload1 == TransformByteSize)
        {
            int offset = (int)value.Payload0;
            if (offset < 0 || byteCount < 0 || byteCount > arena.Length ||
                byteCount - offset < TransformByteSize)
            {
                return false;
            }
            return Set(new UnrealTransform(
                new UnrealQuaternion(
                    ReadDouble(arena, offset + 0 * sizeof(double)),
                    ReadDouble(arena, offset + 1 * sizeof(double)),
                    ReadDouble(arena, offset + 2 * sizeof(double)),
                    ReadDouble(arena, offset + 3 * sizeof(double))),
                new UnrealVector(
                    ReadDouble(arena, offset + 4 * sizeof(double)),
                    ReadDouble(arena, offset + 5 * sizeof(double)),
                    ReadDouble(arena, offset + 6 * sizeof(double))),
                new UnrealVector(
                    ReadDouble(arena, offset + 7 * sizeof(double)),
                    ReadDouble(arena, offset + 8 * sizeof(double)),
                    ReadDouble(arena, offset + 9 * sizeof(double)))), out decoded);
        }
        if (TryDecodeStruct(typeof(T), value, arena, byteCount, 0, out object? structValue))
        {
            decoded = (T)structValue!;
            return true;
        }
        if (TryDecodeContainer(
                typeof(T),
                value,
                arena,
                byteCount,
                0,
                out object? container))
        {
            decoded = (T)container!;
            return true;
        }
        return false;
    }

    public static bool TryDecodeObject(
        Type type,
        in UnrealNativeValueV3 value,
        ReadOnlySpan<byte> arena,
        int byteCount,
        out object? decoded)
    {
        ArgumentNullException.ThrowIfNull(type);
        decoded = null;
        if (type == typeof(bool) && value.Kind == UnrealNativeValueKindV3.Boolean)
            decoded = value.Payload0 != 0;
        else if (type == typeof(sbyte) && value.Kind == UnrealNativeValueKindV3.Int8)
            decoded = unchecked((sbyte)value.Payload0);
        else if (type == typeof(byte) && value.Kind == UnrealNativeValueKindV3.UInt8)
            decoded = (byte)value.Payload0;
        else if (type == typeof(short) && value.Kind == UnrealNativeValueKindV3.Int16)
            decoded = unchecked((short)value.Payload0);
        else if (type == typeof(ushort) && value.Kind == UnrealNativeValueKindV3.UInt16)
            decoded = (ushort)value.Payload0;
        else if (type == typeof(int) && value.Kind == UnrealNativeValueKindV3.Int32)
            decoded = unchecked((int)value.Payload0);
        else if (type == typeof(uint) && value.Kind == UnrealNativeValueKindV3.UInt32)
            decoded = (uint)value.Payload0;
        else if (type == typeof(long) && value.Kind == UnrealNativeValueKindV3.Int64)
            decoded = unchecked((long)value.Payload0);
        else if (type == typeof(ulong) && value.Kind == UnrealNativeValueKindV3.UInt64)
            decoded = value.Payload0;
        else if (type == typeof(float) && value.Kind == UnrealNativeValueKindV3.Float32)
            decoded = BitConverter.Int32BitsToSingle(unchecked((int)value.Payload0));
        else if (type == typeof(double) && value.Kind == UnrealNativeValueKindV3.Float64)
            decoded = BitConverter.Int64BitsToDouble(unchecked((long)value.Payload0));
        else if (type == typeof(UnrealObjectHandle) &&
                 value.Kind == UnrealNativeValueKindV3.Object)
            decoded = new UnrealObjectHandle(value.Payload0);
        else if (type == typeof(UnrealClassHandle) &&
                 value.Kind == UnrealNativeValueKindV3.Class)
            decoded = new UnrealClassHandle(value.Payload0);
        else if (type == typeof(string) && value.Kind == UnrealNativeValueKindV3.String &&
                 TryDecodeUtf8(value, arena, byteCount, out string stringValue))
            decoded = stringValue;
        else if (type == typeof(UnrealName) && value.Kind == UnrealNativeValueKindV3.Name &&
                 TryDecodeUtf8(value, arena, byteCount, out string nameValue))
            decoded = new UnrealName(nameValue);
        else if (type == typeof(UnrealText) && value.Kind == UnrealNativeValueKindV3.Text &&
                 TryDecodeUtf8(value, arena, byteCount, out string textValue))
            decoded = new UnrealText(textValue);
        else if (type == typeof(UnrealVector) &&
                 value.Kind == UnrealNativeValueKindV3.Vector3)
            decoded = new UnrealVector(
                BitsDouble(value.Payload0),
                BitsDouble(value.Payload1),
                BitsDouble(value.Payload2));
        else if (type == typeof(UnrealRotator) &&
                 value.Kind == UnrealNativeValueKindV3.Rotator3)
            decoded = new UnrealRotator(
                BitsDouble(value.Payload0),
                BitsDouble(value.Payload1),
                BitsDouble(value.Payload2));
        else if (type == typeof(UnrealTransform) &&
                 value.Kind == UnrealNativeValueKindV3.Transform &&
                 value.Flags == UnrealNativeValueFlagsV3.ArenaBacked &&
                 value.Payload0 <= int.MaxValue &&
                 value.Payload1 == TransformByteSize)
        {
            int offset = (int)value.Payload0;
            if (offset < 0 || byteCount < 0 || byteCount > arena.Length ||
                byteCount - offset < TransformByteSize)
            {
                return false;
            }
            decoded = new UnrealTransform(
                new UnrealQuaternion(
                    ReadDouble(arena, offset + 0 * sizeof(double)),
                    ReadDouble(arena, offset + 1 * sizeof(double)),
                    ReadDouble(arena, offset + 2 * sizeof(double)),
                    ReadDouble(arena, offset + 3 * sizeof(double))),
                new UnrealVector(
                    ReadDouble(arena, offset + 4 * sizeof(double)),
                    ReadDouble(arena, offset + 5 * sizeof(double)),
                    ReadDouble(arena, offset + 6 * sizeof(double))),
                new UnrealVector(
                    ReadDouble(arena, offset + 7 * sizeof(double)),
                    ReadDouble(arena, offset + 8 * sizeof(double)),
                    ReadDouble(arena, offset + 9 * sizeof(double))));
        }
        else if (TryDecodeStruct(type, value, arena, byteCount, 0, out object? structValue))
        {
            decoded = structValue;
        }
        else if (TryDecodeContainer(type, value, arena, byteCount, 0, out object? container))
        {
            decoded = container;
        }
        else
        {
            return false;
        }
        return true;
    }

    private static bool TryEncodeUtf8(
        string value,
        UnrealNativeValueKindV3 kind,
        Span<byte> arena,
        ref int byteCount,
        out UnrealNativeValueV3 encoded)
    {
        encoded = default;
        int length = Encoding.UTF8.GetByteCount(value);
        if (byteCount < 0 || length > arena.Length - byteCount)
        {
            return false;
        }
        int offset = byteCount;
        int written = Encoding.UTF8.GetBytes(value.AsSpan(), arena[offset..]);
        byteCount += written;
        encoded.Kind = kind;
        encoded.Flags = UnrealNativeValueFlagsV3.ArenaBacked;
        encoded.Payload0 = (ulong)offset;
        encoded.Payload1 = (ulong)written;
        return true;
    }

    private sealed record UnrealStructShape(
        UnrealStructAttribute Attribute,
        PropertyInfo[] Fields,
        ConstructorInfo Constructor);

    private static bool TryGetStructShape(Type type, out UnrealStructShape shape)
    {
        shape = null!;
        UnrealStructAttribute? attribute = type.GetCustomAttribute<UnrealStructAttribute>();
        if (attribute is null || !type.IsValueType)
        {
            return false;
        }
        var indexedFields = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(static property => (
                Property: property,
                Field: property.GetCustomAttribute<UnrealStructFieldAttribute>()))
            .Where(static item => item.Field is not null)
            .OrderBy(static item => item.Field!.Index)
            .ToArray();
        if (indexedFields.Length == 0 || indexedFields
                .Select(static item => item.Field!.Index)
                .Where((index, position) => index != position)
                .Any())
        {
            return false;
        }
        PropertyInfo[] fields = indexedFields.Select(static item => item.Property).ToArray();
        ConstructorInfo? constructor = type.GetConstructor(
            fields.Select(static field => field.PropertyType).ToArray());
        if (constructor is null || fields.Any(static field => !field.CanRead))
        {
            return false;
        }
        shape = new UnrealStructShape(attribute, fields, constructor);
        return true;
    }

    private static bool TryEncodeStruct(
        object? value,
        UnrealStructShape shape,
        Span<byte> arena,
        ref int byteCount,
        out UnrealNativeValueV3 encoded,
        out UnrealNativeCapabilitiesV3 requiredCapability)
    {
        encoded = default;
        requiredCapability = UnrealNativeCapabilitiesV3.Structs;
        if (value is null)
        {
            return false;
        }
        int descriptorBytes;
        try
        {
            descriptorBytes = checked(shape.Fields.Length * UnrealNativeAbiV3.ValueSize);
        }
        catch (OverflowException)
        {
            return false;
        }
        if (byteCount < 0 || descriptorBytes > arena.Length - byteCount)
        {
            return false;
        }
        int descriptorOffset = byteCount;
        byteCount += descriptorBytes;
        for (int index = 0; index < shape.Fields.Length; index++)
        {
            PropertyInfo field = shape.Fields[index];
            if (!TryEncodeDynamic(
                    field.GetValue(value),
                    field.PropertyType,
                    arena,
                    ref byteCount,
                    out UnrealNativeValueV3 cell,
                    out UnrealNativeCapabilitiesV3 fieldCapability))
            {
                return false;
            }
            requiredCapability |= fieldCapability;
            WriteCell(arena, descriptorOffset, index, cell);
        }
        encoded.Kind = UnrealNativeValueKindV3.Struct;
        encoded.Flags = UnrealNativeValueFlagsV3.ArenaBacked;
        encoded.TypeId = UnrealTypeIdV3.FromPath(shape.Attribute.Path);
        encoded.Payload0 = (ulong)descriptorOffset;
        encoded.Payload1 = (ulong)descriptorBytes;
        encoded.Payload2 = (ulong)shape.Fields.Length;
        encoded.Payload3 = UnrealNativeAbiV3.ValueSize;
        return encoded.TypeId != 0;
    }

    private static bool TryDecodeStruct(
        Type type,
        in UnrealNativeValueV3 value,
        ReadOnlySpan<byte> arena,
        int byteCount,
        int depth,
        out object? decoded)
    {
        const int maxDepth = 16;
        decoded = null;
        if (depth > maxDepth ||
            !TryGetStructShape(type, out UnrealStructShape shape) ||
            value.Kind != UnrealNativeValueKindV3.Struct ||
            value.Flags != UnrealNativeValueFlagsV3.ArenaBacked ||
            value.TypeId != UnrealTypeIdV3.FromPath(shape.Attribute.Path) ||
            value.Payload2 != (ulong)shape.Fields.Length ||
            value.Payload3 != UnrealNativeAbiV3.ValueSize ||
            byteCount < 0 || byteCount > arena.Length)
        {
            return false;
        }
        int descriptorBytes = checked(shape.Fields.Length * UnrealNativeAbiV3.ValueSize);
        if (value.Payload0 > (ulong)byteCount ||
            value.Payload1 != (ulong)descriptorBytes ||
            value.Payload1 > (ulong)byteCount - value.Payload0)
        {
            return false;
        }
        int descriptorOffset = checked((int)value.Payload0);
        object?[] arguments = new object?[shape.Fields.Length];
        for (int index = 0; index < shape.Fields.Length; index++)
        {
            UnrealNativeValueV3 cell = ReadCell(arena, descriptorOffset, index);
            if (!TryDecodeNested(
                    shape.Fields[index].PropertyType,
                    cell,
                    arena,
                    byteCount,
                    depth + 1,
                    out arguments[index]))
            {
                return false;
            }
        }
        decoded = shape.Constructor.Invoke(arguments);
        return true;
    }

    private static bool TryEncodeContainer(
        object? value,
        UnrealNativeValueKindV3 kind,
        Type? elementType,
        Type? keyType,
        Type? valueType,
        string nativeName,
        Span<byte> arena,
        ref int byteCount,
        out UnrealNativeValueV3 encoded,
        out UnrealNativeCapabilitiesV3 requiredCapability)
    {
        encoded = default;
        requiredCapability = UnrealNativeCapabilitiesV3.Containers;
        if (value is null || value is not IEnumerable enumerable ||
            !TryGetCount(value, out int count) || count < 0)
        {
            return false;
        }
        int cellsPerEntry = kind == UnrealNativeValueKindV3.Map ? 2 : 1;
        int cellCount;
        int descriptorBytes;
        try
        {
            cellCount = checked(count * cellsPerEntry);
            descriptorBytes = checked(cellCount * UnrealNativeAbiV3.ValueSize);
        }
        catch (OverflowException)
        {
            return false;
        }
        if (byteCount < 0 || descriptorBytes > arena.Length - byteCount)
        {
            return false;
        }

        int descriptorOffset = byteCount;
        byteCount += descriptorBytes;
        int cellIndex = 0;
        foreach (object? item in enumerable)
        {
            if (kind == UnrealNativeValueKindV3.Map)
            {
                if (item is null || keyType is null || valueType is null)
                {
                    return false;
                }
                Type pairType = item.GetType();
                object? key = pairType.GetProperty("Key")?.GetValue(item);
                object? mappedValue = pairType.GetProperty("Value")?.GetValue(item);
                if (!TryEncodeDynamic(
                        key,
                        keyType,
                        arena,
                        ref byteCount,
                        out UnrealNativeValueV3 keyCell,
                        out UnrealNativeCapabilitiesV3 keyCapability) ||
                    !TryEncodeDynamic(
                        mappedValue,
                        valueType,
                        arena,
                        ref byteCount,
                        out UnrealNativeValueV3 valueCell,
                        out UnrealNativeCapabilitiesV3 valueCapability))
                {
                    return false;
                }
                requiredCapability |= keyCapability | valueCapability;
                WriteCell(arena, descriptorOffset, cellIndex++, keyCell);
                WriteCell(arena, descriptorOffset, cellIndex++, valueCell);
            }
            else
            {
                if (elementType is null || !TryEncodeDynamic(
                        item,
                        elementType,
                        arena,
                        ref byteCount,
                        out UnrealNativeValueV3 cell,
                        out UnrealNativeCapabilitiesV3 cellCapability))
                {
                    return false;
                }
                requiredCapability |= cellCapability;
                WriteCell(arena, descriptorOffset, cellIndex++, cell);
            }
        }
        if (cellIndex != cellCount)
        {
            return false;
        }

        encoded.Kind = kind;
        encoded.Flags = UnrealNativeValueFlagsV3.ArenaBacked;
        encoded.TypeId = UnrealTypeIdV3.FromPath(nativeName);
        encoded.Payload0 = (ulong)descriptorOffset;
        encoded.Payload1 = (ulong)descriptorBytes;
        encoded.Payload2 = (ulong)count;
        encoded.Payload3 = UnrealNativeAbiV3.ValueSize;
        return encoded.TypeId != 0;
    }

    private static bool TryEncodeDynamic(
        object? value,
        Type type,
        Span<byte> arena,
        ref int byteCount,
        out UnrealNativeValueV3 encoded,
        out UnrealNativeCapabilitiesV3 requiredCapability)
    {
        encoded = default;
        requiredCapability = UnrealNativeCapabilitiesV3.FixedScalars;
        if (type == typeof(bool) && value is bool booleanValue)
        {
            encoded.Kind = UnrealNativeValueKindV3.Boolean;
            encoded.Payload0 = booleanValue ? 1UL : 0UL;
            return true;
        }
        if (type == typeof(sbyte) && value is sbyte int8Value)
        {
            encoded.Kind = UnrealNativeValueKindV3.Int8;
            encoded.Payload0 = unchecked((ulong)int8Value);
            return true;
        }
        if (type == typeof(byte) && value is byte uint8Value)
        {
            encoded.Kind = UnrealNativeValueKindV3.UInt8;
            encoded.Payload0 = uint8Value;
            return true;
        }
        if (type == typeof(short) && value is short int16Value)
        {
            encoded.Kind = UnrealNativeValueKindV3.Int16;
            encoded.Payload0 = unchecked((ulong)int16Value);
            return true;
        }
        if (type == typeof(ushort) && value is ushort uint16Value)
        {
            encoded.Kind = UnrealNativeValueKindV3.UInt16;
            encoded.Payload0 = uint16Value;
            return true;
        }
        if (type == typeof(int) && value is int int32Value)
        {
            encoded.Kind = UnrealNativeValueKindV3.Int32;
            encoded.Payload0 = unchecked((ulong)int32Value);
            return true;
        }
        if (type == typeof(uint) && value is uint uint32Value)
        {
            encoded.Kind = UnrealNativeValueKindV3.UInt32;
            encoded.Payload0 = uint32Value;
            return true;
        }
        if (type == typeof(long) && value is long int64Value)
        {
            encoded.Kind = UnrealNativeValueKindV3.Int64;
            encoded.Payload0 = unchecked((ulong)int64Value);
            return true;
        }
        if (type == typeof(ulong) && value is ulong uint64Value)
        {
            encoded.Kind = UnrealNativeValueKindV3.UInt64;
            encoded.Payload0 = uint64Value;
            return true;
        }
        if (type == typeof(float) && value is float float32Value)
        {
            encoded.Kind = UnrealNativeValueKindV3.Float32;
            encoded.Payload0 = unchecked((uint)BitConverter.SingleToInt32Bits(float32Value));
            return true;
        }
        if (type == typeof(double) && value is double float64Value)
        {
            encoded.Kind = UnrealNativeValueKindV3.Float64;
            encoded.Payload0 = DoubleBits(float64Value);
            return true;
        }
        if (type == typeof(string) && value is string stringValue)
        {
            requiredCapability = UnrealNativeCapabilitiesV3.Utf8Text;
            return TryEncodeUtf8(
                stringValue,
                UnrealNativeValueKindV3.String,
                arena,
                ref byteCount,
                out encoded);
        }
        if (type == typeof(UnrealName) && value is UnrealName nameValue)
        {
            requiredCapability = UnrealNativeCapabilitiesV3.Utf8Text;
            return TryEncodeUtf8(
                nameValue.Value,
                UnrealNativeValueKindV3.Name,
                arena,
                ref byteCount,
                out encoded);
        }
        if (type == typeof(UnrealText) && value is UnrealText textValue)
        {
            requiredCapability = UnrealNativeCapabilitiesV3.Utf8Text;
            return TryEncodeUtf8(
                textValue.Value,
                UnrealNativeValueKindV3.Text,
                arena,
                ref byteCount,
                out encoded);
        }
        if (type == typeof(UnrealObjectHandle) && value is UnrealObjectHandle objectHandle)
        {
            requiredCapability = UnrealNativeCapabilitiesV3.ObjectHandles;
            encoded.Kind = UnrealNativeValueKindV3.Object;
            encoded.Payload0 = objectHandle.Value;
            return true;
        }
        if (type == typeof(UnrealClassHandle) && value is UnrealClassHandle classHandle)
        {
            requiredCapability = UnrealNativeCapabilitiesV3.ObjectHandles;
            encoded.Kind = UnrealNativeValueKindV3.Class;
            encoded.Payload0 = classHandle.Value;
            return true;
        }
        if (type == typeof(UnrealEnumValue) && value is UnrealEnumValue enumValue)
        {
            requiredCapability = UnrealNativeCapabilitiesV3.Enums;
            encoded.Kind = UnrealNativeValueKindV3.Enum;
            encoded.TypeId = UnrealTypeIdV3.FromPath(enumValue.TypePath);
            encoded.Payload0 = unchecked((ulong)enumValue.Value);
            return encoded.TypeId != 0;
        }
        if (type == typeof(UnrealVector) && value is UnrealVector vector)
        {
            requiredCapability = UnrealNativeCapabilitiesV3.VectorAndRotator;
            encoded.Kind = UnrealNativeValueKindV3.Vector3;
            encoded.Payload0 = DoubleBits(vector.X);
            encoded.Payload1 = DoubleBits(vector.Y);
            encoded.Payload2 = DoubleBits(vector.Z);
            return true;
        }
        if (type == typeof(UnrealRotator) && value is UnrealRotator rotator)
        {
            requiredCapability = UnrealNativeCapabilitiesV3.VectorAndRotator;
            encoded.Kind = UnrealNativeValueKindV3.Rotator3;
            encoded.Payload0 = DoubleBits(rotator.Pitch);
            encoded.Payload1 = DoubleBits(rotator.Yaw);
            encoded.Payload2 = DoubleBits(rotator.Roll);
            return true;
        }
        if (type == typeof(UnrealTransform) && value is UnrealTransform transform)
        {
            requiredCapability = UnrealNativeCapabilitiesV3.Transform;
            if (byteCount < 0 || TransformByteSize > arena.Length - byteCount)
            {
                return false;
            }
            int offset = byteCount;
            WriteDouble(arena, offset + 0 * sizeof(double), transform.Rotation.X);
            WriteDouble(arena, offset + 1 * sizeof(double), transform.Rotation.Y);
            WriteDouble(arena, offset + 2 * sizeof(double), transform.Rotation.Z);
            WriteDouble(arena, offset + 3 * sizeof(double), transform.Rotation.W);
            WriteDouble(arena, offset + 4 * sizeof(double), transform.Translation.X);
            WriteDouble(arena, offset + 5 * sizeof(double), transform.Translation.Y);
            WriteDouble(arena, offset + 6 * sizeof(double), transform.Translation.Z);
            WriteDouble(arena, offset + 7 * sizeof(double), transform.Scale3D.X);
            WriteDouble(arena, offset + 8 * sizeof(double), transform.Scale3D.Y);
            WriteDouble(arena, offset + 9 * sizeof(double), transform.Scale3D.Z);
            byteCount += TransformByteSize;
            encoded.Kind = UnrealNativeValueKindV3.Transform;
            encoded.Flags = UnrealNativeValueFlagsV3.ArenaBacked;
            encoded.Payload0 = (ulong)offset;
            encoded.Payload1 = TransformByteSize;
            return true;
        }
        if (TryGetStructShape(type, out UnrealStructShape structShape))
        {
            return TryEncodeStruct(
                value,
                structShape,
                arena,
                ref byteCount,
                out encoded,
                out requiredCapability);
        }
        if (TryGetContainerShape(
                type,
                out UnrealNativeValueKindV3 containerKind,
                out Type? elementType,
                out Type? keyType,
                out Type? valueType,
                out string? nativeName))
        {
            return TryEncodeContainer(
                value,
                containerKind,
                elementType,
                keyType,
                valueType,
                nativeName,
                arena,
                ref byteCount,
                out encoded,
                out requiredCapability);
        }
        return false;
    }

    private static bool TryGetContainerShape(
        Type type,
        out UnrealNativeValueKindV3 kind,
        out Type? elementType,
        out Type? keyType,
        out Type? valueType,
        out string nativeName)
    {
        kind = default;
        elementType = null;
        keyType = null;
        valueType = null;
        nativeName = string.Empty;
        if (!type.IsGenericType)
        {
            return false;
        }
        Type definition = type.GetGenericTypeDefinition();
        Type[] arguments = type.GetGenericArguments();
        if (definition == typeof(IReadOnlyList<>))
        {
            kind = UnrealNativeValueKindV3.Array;
            elementType = arguments[0];
            if (!TryGetNativeName(elementType, out string elementName)) return false;
            nativeName = $"TArray<{elementName}>";
            return true;
        }
        if (definition == typeof(IReadOnlySet<>))
        {
            kind = UnrealNativeValueKindV3.Set;
            elementType = arguments[0];
            if (!TryGetNativeName(elementType, out string elementName)) return false;
            nativeName = $"TSet<{elementName}>";
            return true;
        }
        if (definition == typeof(IReadOnlyDictionary<,>))
        {
            kind = UnrealNativeValueKindV3.Map;
            keyType = arguments[0];
            valueType = arguments[1];
            if (!TryGetNativeName(keyType, out string keyName) ||
                !TryGetNativeName(valueType, out string valueName)) return false;
            nativeName = $"TMap<{keyName},{valueName}>";
            return true;
        }
        return false;
    }

    private static bool TryGetNativeName(Type type, out string name)
    {
        name = type == typeof(bool) ? "bool" :
            type == typeof(sbyte) ? "int8" :
            type == typeof(byte) ? "uint8" :
            type == typeof(short) ? "int16" :
            type == typeof(ushort) ? "uint16" :
            type == typeof(int) ? "int32" :
            type == typeof(uint) ? "uint32" :
            type == typeof(long) ? "int64" :
            type == typeof(ulong) ? "uint64" :
            type == typeof(float) ? "float" :
            type == typeof(double) ? "double" :
            type == typeof(string) ? "FString" :
            type == typeof(UnrealName) ? "FName" :
            type == typeof(UnrealText) ? "FText" :
            type == typeof(UnrealObjectHandle) ? "UObject*" :
            type == typeof(UnrealClassHandle) ? "UClass*" :
            type == typeof(UnrealVector) ? "FVector" :
            type == typeof(UnrealRotator) ? "FRotator" :
            type == typeof(UnrealTransform) ? "FTransform" :
            TryGetStructShape(type, out UnrealStructShape structShape)
                ? structShape.Attribute.NativeName
                : string.Empty;
        if (name.Length != 0)
        {
            return true;
        }
        if (TryGetContainerShape(type, out _, out _, out _, out _, out string containerName))
        {
            name = containerName;
            return true;
        }
        return false;
    }

    private static bool TryGetCount(object value, out int count)
    {
        if (value is ICollection collection)
        {
            count = collection.Count;
            return true;
        }
        object? result = value.GetType().GetProperty("Count")?.GetValue(value);
        if (result is int typedCount)
        {
            count = typedCount;
            return true;
        }
        count = 0;
        return false;
    }

    private static void WriteCell(
        Span<byte> arena,
        int descriptorOffset,
        int cellIndex,
        in UnrealNativeValueV3 cell)
    {
        int offset = checked(descriptorOffset + cellIndex * UnrealNativeAbiV3.ValueSize);
        UnrealNativeValueV3 copy = cell;
        MemoryMarshal.Write(arena.Slice(offset, UnrealNativeAbiV3.ValueSize), in copy);
    }

    private static bool TryDecodeContainer(
        Type type,
        in UnrealNativeValueV3 container,
        ReadOnlySpan<byte> arena,
        int byteCount,
        int depth,
        out object? decoded)
    {
        const int maxDepth = 16;
        decoded = null;
        if (depth > maxDepth ||
            !TryGetContainerShape(
                type,
                out UnrealNativeValueKindV3 expectedKind,
                out Type? elementType,
                out Type? keyType,
                out Type? valueType,
                out string nativeName) ||
            container.Kind != expectedKind ||
            container.Flags != UnrealNativeValueFlagsV3.ArenaBacked ||
            container.TypeId != UnrealTypeIdV3.FromPath(nativeName) ||
            container.Payload2 > int.MaxValue ||
            container.Payload3 != UnrealNativeAbiV3.ValueSize ||
            byteCount < 0 || byteCount > arena.Length)
        {
            return false;
        }
        int entries = (int)container.Payload2;
        int cellsPerEntry = expectedKind == UnrealNativeValueKindV3.Map ? 2 : 1;
        int cellCount;
        int descriptorBytes;
        try
        {
            cellCount = checked(entries * cellsPerEntry);
            descriptorBytes = checked(cellCount * UnrealNativeAbiV3.ValueSize);
        }
        catch (OverflowException)
        {
            return false;
        }
        if (container.Payload0 > (ulong)byteCount ||
            container.Payload1 != (ulong)descriptorBytes ||
            container.Payload1 > (ulong)byteCount - container.Payload0)
        {
            return false;
        }
        int descriptorOffset = checked((int)container.Payload0);

        if (expectedKind == UnrealNativeValueKindV3.Array)
        {
            Type listType = typeof(List<>).MakeGenericType(elementType!);
            var list = (IList)Activator.CreateInstance(listType, entries)!;
            for (int index = 0; index < entries; index++)
            {
                UnrealNativeValueV3 cell = ReadCell(arena, descriptorOffset, index);
                if (!TryDecodeNested(
                        elementType!, cell, arena, byteCount, depth + 1, out object? item))
                {
                    return false;
                }
                list.Add(item);
            }
            decoded = list;
            return true;
        }
        if (expectedKind == UnrealNativeValueKindV3.Set)
        {
            Type setType = typeof(HashSet<>).MakeGenericType(elementType!);
            object set = Activator.CreateInstance(setType)!;
            System.Reflection.MethodInfo add = setType.GetMethod("Add", [elementType!])!;
            for (int index = 0; index < entries; index++)
            {
                UnrealNativeValueV3 cell = ReadCell(arena, descriptorOffset, index);
                if (!TryDecodeNested(
                        elementType!, cell, arena, byteCount, depth + 1, out object? item))
                {
                    return false;
                }
                _ = add.Invoke(set, [item]);
            }
            decoded = set;
            return true;
        }

        Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(keyType!, valueType!);
        var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType, entries)!;
        for (int index = 0; index < entries; index++)
        {
            UnrealNativeValueV3 keyCell = ReadCell(arena, descriptorOffset, index * 2);
            UnrealNativeValueV3 valueCell = ReadCell(arena, descriptorOffset, index * 2 + 1);
            if (!TryDecodeNested(
                    keyType!, keyCell, arena, byteCount, depth + 1, out object? key) ||
                !TryDecodeNested(
                    valueType!, valueCell, arena, byteCount, depth + 1, out object? mappedValue) ||
                key is null)
            {
                return false;
            }
            dictionary.Add(key, mappedValue);
        }
        decoded = dictionary;
        return true;
    }

    private static bool TryDecodeNested(
        Type type,
        in UnrealNativeValueV3 value,
        ReadOnlySpan<byte> arena,
        int byteCount,
        int depth,
        out object? decoded)
    {
        if (TryGetStructShape(type, out _))
        {
            return TryDecodeStruct(type, value, arena, byteCount, depth, out decoded);
        }
        if (TryGetContainerShape(type, out _, out _, out _, out _, out _))
        {
            return TryDecodeContainer(type, value, arena, byteCount, depth, out decoded);
        }
        return TryDecodeObject(type, value, arena, byteCount, out decoded);
    }

    private static UnrealNativeValueV3 ReadCell(
        ReadOnlySpan<byte> arena,
        int descriptorOffset,
        int cellIndex)
    {
        int offset = checked(descriptorOffset + cellIndex * UnrealNativeAbiV3.ValueSize);
        return MemoryMarshal.Read<UnrealNativeValueV3>(
            arena.Slice(offset, UnrealNativeAbiV3.ValueSize));
    }

    private static bool TryDecodeUtf8(
        in UnrealNativeValueV3 value,
        ReadOnlySpan<byte> arena,
        int byteCount,
        out string decoded)
    {
        decoded = string.Empty;
        if (value.Flags != UnrealNativeValueFlagsV3.ArenaBacked || value.TypeId != 0 ||
            value.Payload0 > (ulong)byteCount || value.Payload1 > (ulong)byteCount - value.Payload0)
        {
            return false;
        }
        decoded = Encoding.UTF8.GetString(arena.Slice(
            checked((int)value.Payload0),
            checked((int)value.Payload1)));
        return true;
    }

    private static bool Set<TValue, T>(TValue value, out T destination)
    {
        destination = Unsafe.As<TValue, T>(ref value);
        return true;
    }

    private static TTo Reinterpret<TFrom, TTo>(ref TFrom value) =>
        Unsafe.As<TFrom, TTo>(ref value);

    private static ulong DoubleBits(double value) =>
        unchecked((ulong)BitConverter.DoubleToInt64Bits(value));

    private static double BitsDouble(ulong value) =>
        BitConverter.Int64BitsToDouble(unchecked((long)value));

    private static void WriteDouble(Span<byte> arena, int offset, double value) =>
        BinaryPrimitives.WriteInt64LittleEndian(
            arena.Slice(offset, sizeof(double)),
            BitConverter.DoubleToInt64Bits(value));

    private static double ReadDouble(ReadOnlySpan<byte> arena, int offset) =>
        BitConverter.Int64BitsToDouble(
            BinaryPrimitives.ReadInt64LittleEndian(arena.Slice(offset, sizeof(double))));
}

public static class UnrealTypeIdV3
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong FromPath(string? typePath)
    {
        if (string.IsNullOrWhiteSpace(typePath))
        {
            return 0;
        }

        ulong hash = OffsetBasis;
        foreach (char character in typePath)
        {
            if (character > 0x7f)
            {
                return 0;
            }
            hash ^= (byte)character;
            hash = unchecked(hash * Prime);
        }
        return hash;
    }
}

using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace UnifiedScript.Runtime;

internal static class ExecutionValueCodec
{
    public static object? Decode(ExecutionArgument argument, Type targetType)
    {
        using JsonDocument document = JsonDocument.Parse(argument.Json);
        try
        {
            return Decode(document.RootElement, targetType);
        }
        catch (ExecutionInvocationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or
            InvalidOperationException or OverflowException or ArgumentException)
        {
            throw new ExecutionInvocationException(
                "UVR1006",
                $"Argument JSON cannot be converted to '{GetMetadataName(targetType)}': " +
                exception.Message);
        }
    }

    public static string EncodeScalar(object? value, Type declaredType)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteScalar(writer, value, declaredType);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string GetMetadataName(Type type)
    {
        if (type.IsArray)
        {
            return $"{GetMetadataName(type.GetElementType()!)}[]";
        }

        if (type.IsGenericType)
        {
            string definitionName = type.GetGenericTypeDefinition().FullName ?? type.Name;
            int arityIndex = definitionName.IndexOf('`');
            if (arityIndex >= 0)
            {
                definitionName = definitionName[..arityIndex];
            }

            return $"{definitionName.Replace('+', '.')}<" +
                string.Join(',', type.GetGenericArguments().Select(GetMetadataName)) + ">";
        }

        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    private static object? Decode(JsonElement element, Type targetType)
    {
        Type? nullableUnderlying = Nullable.GetUnderlyingType(targetType);
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (!targetType.IsValueType || nullableUnderlying is not null)
            {
                return null;
            }

            throw new ExecutionInvocationException(
                "UVR1006",
                $"JSON null is not valid for '{GetMetadataName(targetType)}'.");
        }

        if (nullableUnderlying is not null)
        {
            object? underlying = Decode(element, nullableUnderlying);
            return Activator.CreateInstance(targetType, underlying);
        }

        if (targetType.IsEnum)
        {
            return element.ValueKind == JsonValueKind.String
                ? Enum.Parse(targetType, element.GetString()!, ignoreCase: false)
                : Enum.ToObject(targetType, Decode(element, Enum.GetUnderlyingType(targetType))!);
        }

        if (targetType == typeof(IntPtr))
        {
            return new IntPtr(element.GetInt64());
        }

        if (targetType == typeof(UIntPtr))
        {
            return new UIntPtr(element.GetUInt64());
        }

        object? scalar = Type.GetTypeCode(targetType) switch
        {
            TypeCode.Boolean => element.GetBoolean(),
            TypeCode.Char => DecodeCharacter(element),
            TypeCode.SByte => element.GetSByte(),
            TypeCode.Byte => element.GetByte(),
            TypeCode.Int16 => element.GetInt16(),
            TypeCode.UInt16 => element.GetUInt16(),
            TypeCode.Int32 => element.GetInt32(),
            TypeCode.UInt32 => element.GetUInt32(),
            TypeCode.Int64 => element.GetInt64(),
            TypeCode.UInt64 => element.GetUInt64(),
            TypeCode.Single => element.GetSingle(),
            TypeCode.Double => element.GetDouble(),
            TypeCode.Decimal => element.GetDecimal(),
            TypeCode.String => element.GetString(),
            _ => null,
        };
        if (scalar is not null || targetType == typeof(string))
        {
            return scalar;
        }

        if (targetType.IsArray && targetType.GetArrayRank() == 1)
        {
            return DecodeArray(element, targetType.GetElementType()!);
        }

        if (TryGetEnumerableElementType(targetType, out Type? elementType))
        {
            Array values = DecodeArray(element, elementType!);
            if (targetType.IsAssignableFrom(values.GetType()))
            {
                return values;
            }

            if (targetType.IsGenericType &&
                targetType.GetGenericTypeDefinition() == typeof(List<>) &&
                Activator.CreateInstance(targetType) is IList list)
            {
                foreach (object? value in values)
                {
                    _ = list.Add(value);
                }

                return list;
            }
        }

        throw new ExecutionInvocationException(
            "UVR1006",
            $"CoreCLR execution arguments do not yet support '{GetMetadataName(targetType)}'.");
    }

    private static Array DecodeArray(JsonElement element, Type elementType)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ExecutionInvocationException(
                "UVR1006",
                $"'{GetMetadataName(elementType)}[]' requires a JSON array.");
        }

        JsonElement.ArrayEnumerator source = element.EnumerateArray();
        var values = new List<object?>();
        foreach (JsonElement item in source)
        {
            values.Add(Decode(item, elementType));
        }

        Array result = Array.CreateInstance(elementType, values.Count);
        for (int index = 0; index < values.Count; index++)
        {
            result.SetValue(values[index], index);
        }

        return result;
    }

    private static char DecodeCharacter(JsonElement element)
    {
        string value = element.GetString() ?? string.Empty;
        if (value.Length != 1)
        {
            throw new FormatException("A character argument must be one JSON string character.");
        }

        return value[0];
    }

    private static bool TryGetEnumerableElementType(Type type, out Type? elementType)
    {
        Type[] candidates = [type, .. type.GetInterfaces()];
        Type[] elements = candidates
            .Where(static candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(static candidate => candidate.GetGenericArguments()[0])
            .Distinct()
            .ToArray();
        elementType = elements.Length == 1 ? elements[0] : null;
        return elementType is not null;
    }

    private static void WriteScalar(Utf8JsonWriter writer, object? value, Type declaredType)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        Type runtimeType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (runtimeType.IsEnum)
        {
            string? name = Enum.GetName(runtimeType, value);
            if (name is not null)
            {
                writer.WriteStringValue(name);
                return;
            }

            runtimeType = Enum.GetUnderlyingType(runtimeType);
            value = Convert.ChangeType(value, runtimeType, CultureInfo.InvariantCulture);
        }

        switch (Type.GetTypeCode(runtimeType))
        {
            case TypeCode.Boolean:
                writer.WriteBooleanValue((bool)value);
                break;
            case TypeCode.Char:
                writer.WriteStringValue(((char)value).ToString());
                break;
            case TypeCode.SByte:
                writer.WriteNumberValue((sbyte)value);
                break;
            case TypeCode.Byte:
                writer.WriteNumberValue((byte)value);
                break;
            case TypeCode.Int16:
                writer.WriteNumberValue((short)value);
                break;
            case TypeCode.UInt16:
                writer.WriteNumberValue((ushort)value);
                break;
            case TypeCode.Int32:
                writer.WriteNumberValue((int)value);
                break;
            case TypeCode.UInt32:
                writer.WriteNumberValue((uint)value);
                break;
            case TypeCode.Int64:
                writer.WriteNumberValue((long)value);
                break;
            case TypeCode.UInt64:
                writer.WriteNumberValue((ulong)value);
                break;
            case TypeCode.Single:
                writer.WriteNumberValue((float)value);
                break;
            case TypeCode.Double:
                writer.WriteNumberValue((double)value);
                break;
            case TypeCode.Decimal:
                writer.WriteNumberValue((decimal)value);
                break;
            case TypeCode.String:
                writer.WriteStringValue((string)value);
                break;
            default:
                if (runtimeType == typeof(IntPtr))
                {
                    writer.WriteNumberValue(((IntPtr)value).ToInt64());
                    break;
                }

                if (runtimeType == typeof(UIntPtr))
                {
                    writer.WriteNumberValue(((UIntPtr)value).ToUInt64());
                    break;
                }

                throw new ExecutionInvocationException(
                    "UVR1005",
                    $"Execution-result v0.1 supports scalar returns; " +
                    $"'{GetMetadataName(declaredType)}' is outside that boundary.");
        }
    }
}

#pragma once

#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptNativeObjectRegistry.h"
#include <string>
#include <bit>
#include <type_traits>

namespace UE::UnifiedScript::NativeObjectValues
{
inline bool IsValidUtf8(TConstArrayView<uint8> Bytes)
{
    for (int32 Index = 0; Index < Bytes.Num();)
    {
        const uint8 Lead = Bytes[Index++];
        int32 Continuations = 0;
        uint32 CodePoint = 0;
        if (Lead <= 0x7f) continue;
        if (Lead >= 0xc2 && Lead <= 0xdf) { Continuations = 1; CodePoint = Lead & 0x1f; }
        else if (Lead >= 0xe0 && Lead <= 0xef) { Continuations = 2; CodePoint = Lead & 0x0f; }
        else if (Lead >= 0xf0 && Lead <= 0xf4) { Continuations = 3; CodePoint = Lead & 0x07; }
        else return false;
        if (Index + Continuations > Bytes.Num()) return false;
        for (int32 Part = 0; Part < Continuations; ++Part)
        {
            const uint8 Byte = Bytes[Index++];
            if ((Byte & 0xc0) != 0x80) return false;
            CodePoint = (CodePoint << 6) | (Byte & 0x3f);
        }
        if (Continuations == 2 && CodePoint < 0x800) return false;
        if (Continuations == 3 && CodePoint < 0x10000) return false;
        if (CodePoint > 0x10ffff || (CodePoint >= 0xd800 && CodePoint <= 0xdfff)) return false;
    }
    return true;
}

inline bool IsTextCell(const FUnifiedScriptNativeValueV3& Cell)
{
    return Cell.Kind == EUnifiedScriptNativeValueKindV3::String &&
        Cell.Flags == EUnifiedScriptNativeValueFlagsV3::ArenaBacked && !Cell.Reserved16 &&
        !Cell.Auxiliary && !Cell.TypeId && !Cell.Payload2 && !Cell.Payload3;
}

inline EUnifiedScriptNativeObjectStatus ReadText(TConstArrayView<uint8> Input,
    const FUnifiedScriptNativeValueV3& Cell, FString& OutValue)
{
    OutValue.Reset();
    if (!IsTextCell(Cell) || Cell.Payload0 > static_cast<uint64>(Input.Num()) ||
        Cell.Payload1 > static_cast<uint64>(Input.Num()) - Cell.Payload0)
        return EUnifiedScriptNativeObjectStatus::InvalidArgument;
    if (Cell.Payload1 == 0) return EUnifiedScriptNativeObjectStatus::Success;
    const TConstArrayView<uint8> Bytes(Input.GetData() + Cell.Payload0, static_cast<int32>(Cell.Payload1));
    if (!IsValidUtf8(Bytes)) return EUnifiedScriptNativeObjectStatus::InvalidArgument;
    FUTF8ToTCHAR Converter(reinterpret_cast<const UTF8CHAR*>(Bytes.GetData()), Bytes.Num());
    OutValue = FString(Converter.Length(), Converter.Get());
    return EUnifiedScriptNativeObjectStatus::Success;
}

inline EUnifiedScriptNativeObjectStatus ReadText(TConstArrayView<uint8> Input,
    const FUnifiedScriptNativeValueV3& Cell, std::string& OutValue)
{
    OutValue.clear();
    if (!IsTextCell(Cell) || Cell.Payload0 > static_cast<uint64>(Input.Num()) ||
        Cell.Payload1 > static_cast<uint64>(Input.Num()) - Cell.Payload0)
        return EUnifiedScriptNativeObjectStatus::InvalidArgument;
    if (Cell.Payload1 == 0) return EUnifiedScriptNativeObjectStatus::Success;
    const TConstArrayView<uint8> Bytes(Input.GetData() + Cell.Payload0, static_cast<int32>(Cell.Payload1));
    if (!IsValidUtf8(Bytes)) return EUnifiedScriptNativeObjectStatus::InvalidArgument;
    OutValue.assign(reinterpret_cast<const char*>(Bytes.GetData()), Bytes.Num());
    return EUnifiedScriptNativeObjectStatus::Success;
}

inline bool WriteTextBytes(TConstArrayView<uint8> Bytes, TArray<uint8>& Output,
    FUnifiedScriptNativeValueV3& OutValue)
{
    constexpr int32 MaximumTextBytes = 16 * 1024 * 1024;
    if (Bytes.Num() > MaximumTextBytes || Output.Num() > MaximumTextBytes - Bytes.Num()) return false;
    const int32 Offset = Output.Num();
    if (Bytes.Num()) Output.Append(Bytes.GetData(), Bytes.Num());
    OutValue = {};
    OutValue.Kind = EUnifiedScriptNativeValueKindV3::String;
    OutValue.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
    OutValue.Payload0 = static_cast<uint64>(Offset);
    OutValue.Payload1 = static_cast<uint64>(Bytes.Num());
    return true;
}

inline bool WriteText(const FString& Value, TArray<uint8>& Output, FUnifiedScriptNativeValueV3& OutValue)
{
    constexpr int32 MaximumTextBytes = 16 * 1024 * 1024;
    if (!Value.Len()) return WriteTextBytes({}, Output, OutValue);
    if (Value.Len() > MaximumTextBytes) return false;
    const int32 ByteCount = FPlatformString::ConvertedLength<UTF8CHAR>(GetData(Value), Value.Len());
    if (ByteCount < 0 || ByteCount > MaximumTextBytes) return false;
    FTCHARToUTF8 Converter(GetData(Value), Value.Len());
    return WriteTextBytes(TConstArrayView<uint8>(reinterpret_cast<const uint8*>(Converter.Get()), Converter.Length()), Output, OutValue);
}

inline bool WriteText(const std::string& Value, TArray<uint8>& Output, FUnifiedScriptNativeValueV3& OutValue)
{
    if (Value.size() > 16 * 1024 * 1024 || Value.size() > static_cast<size_t>(MAX_int32)) return false;
    return WriteTextBytes(TConstArrayView<uint8>(reinterpret_cast<const uint8*>(Value.data()), static_cast<int32>(Value.size())), Output, OutValue);
}
inline bool IsObjectCell(const FUnifiedScriptNativeValueV3& Cell)
{
    return Cell.Kind == EUnifiedScriptNativeValueKindV3::NativeObject && Cell.TypeId &&
        !Cell.Reserved16 && !Cell.Auxiliary && !Cell.Payload1 && !Cell.Payload2 && !Cell.Payload3 &&
        ((Cell.Flags == EUnifiedScriptNativeValueFlagsV3::None && Cell.Payload0) ||
         (Cell.Flags == EUnifiedScriptNativeValueFlagsV3::Null && !Cell.Payload0));
}

inline EUnifiedScriptNativeObjectStatus ReadObject(FUnifiedScriptNativeObjectRegistry& Registry, uint64 Scope,
    const FUnifiedScriptNativeValueV3& Cell, uint64 TypeId, bool bAllowNull, void*& OutPointer)
{
    OutPointer = nullptr;
    if (!IsObjectCell(Cell)) return EUnifiedScriptNativeObjectStatus::InvalidArgument;
    if (Cell.TypeId != TypeId) return EUnifiedScriptNativeObjectStatus::WrongType;
    if (!Cell.Payload0) return bAllowNull ? EUnifiedScriptNativeObjectStatus::Success : EUnifiedScriptNativeObjectStatus::InvalidArgument;
    return Registry.Resolve(Scope, {Cell.Payload0}, TypeId, OutPointer);
}

template <typename T> constexpr EUnifiedScriptNativeValueKindV3 Kind()
{
    using K = EUnifiedScriptNativeValueKindV3;
    if constexpr (std::is_same_v<T, bool>) return K::Boolean;
    else if constexpr (std::is_same_v<T, int8>) return K::Int8;
    else if constexpr (std::is_same_v<T, uint8>) return K::UInt8;
    else if constexpr (std::is_same_v<T, int16>) return K::Int16;
    else if constexpr (std::is_same_v<T, uint16>) return K::UInt16;
    else if constexpr (std::is_same_v<T, int32>) return K::Int32;
    else if constexpr (std::is_same_v<T, uint32>) return K::UInt32;
    else if constexpr (std::is_same_v<T, int64>) return K::Int64;
    else if constexpr (std::is_same_v<T, uint64>) return K::UInt64;
    else if constexpr (std::is_same_v<T, float>) return K::Float32;
    else if constexpr (std::is_same_v<T, double>) return K::Float64;
    else if constexpr (std::is_enum_v<T>) return K::Enum;
    else if constexpr (std::is_same_v<T, FVector>) return K::Vector3;
    else if constexpr (std::is_same_v<T, FRotator>) return K::Rotator3;
    else static_assert(sizeof(T) == 0, "Unsupported native object value type");
}

template <typename T> bool Read(const FUnifiedScriptNativeValueV3& Cell, T& Value, uint64 TypeId = 0)
{
    if (Cell.Kind != Kind<T>() || Cell.TypeId != TypeId || Cell.Flags != EUnifiedScriptNativeValueFlagsV3::None ||
        Cell.Reserved16 || Cell.Auxiliary) return false;
    if constexpr (std::is_same_v<T, bool>) { if (Cell.Payload0 > 1) return false; Value = Cell.Payload0 != 0; }
    else if constexpr (std::is_integral_v<T> || std::is_enum_v<T>) Value = static_cast<T>(Cell.Payload0);
    else if constexpr (std::is_same_v<T, float>) Value = std::bit_cast<float>(static_cast<uint32>(Cell.Payload0));
    else if constexpr (std::is_same_v<T, double>) Value = std::bit_cast<double>(Cell.Payload0);
    else Value = T(std::bit_cast<double>(Cell.Payload0), std::bit_cast<double>(Cell.Payload1), std::bit_cast<double>(Cell.Payload2));
    return true;
}

template <typename T> FUnifiedScriptNativeValueV3 Write(const T& Value, uint64 TypeId = 0)
{
    FUnifiedScriptNativeValueV3 Cell;
    Cell.Kind = Kind<T>();
    Cell.TypeId = TypeId;
    if constexpr (std::is_integral_v<T> || std::is_enum_v<T>) Cell.Payload0 = static_cast<uint64>(Value);
    else if constexpr (std::is_same_v<T, float>) Cell.Payload0 = std::bit_cast<uint32>(Value);
    else if constexpr (std::is_same_v<T, double>) Cell.Payload0 = std::bit_cast<uint64>(Value);
    else if constexpr (std::is_same_v<T, FVector>)
    {
        Cell.Payload0 = std::bit_cast<uint64>(Value.X);
        Cell.Payload1 = std::bit_cast<uint64>(Value.Y);
        Cell.Payload2 = std::bit_cast<uint64>(Value.Z);
    }
    else
    {
        Cell.Payload0 = std::bit_cast<uint64>(Value.Pitch);
        Cell.Payload1 = std::bit_cast<uint64>(Value.Yaw);
        Cell.Payload2 = std::bit_cast<uint64>(Value.Roll);
    }
    return Cell;
}
}

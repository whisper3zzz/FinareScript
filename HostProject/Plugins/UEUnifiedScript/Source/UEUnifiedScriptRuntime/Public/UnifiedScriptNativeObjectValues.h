#pragma once

#include "UnifiedScriptNativeTransport.h"
#include <bit>
#include <type_traits>

namespace UE::UnifiedScript::NativeObjectValues
{
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

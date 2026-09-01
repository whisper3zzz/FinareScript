#include "UnifiedScriptBlueprint.h"

#include "Engine/Engine.h"
#include "GameFramework/Actor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

#include UE_INLINE_GENERATED_CPP_BY_NAME(UnifiedScriptBlueprint)

namespace UE::UnifiedScript::Blueprint::Private
{
constexpr int32 ManagedFunctionAbiV2 = 2;
constexpr int32 MaximumManagedFunctionArenaBytes = 64 * 1024;
const FName WorldContextObjectName(TEXT("WorldContextObject"));
const FName KismetWorldContextName(TEXT("__WorldContext"));

bool PackValue(
    const FProperty& Property,
    const void* Container,
    FUnifiedScriptManagedFunctionValueV1& OutValue)
{
    const void* Value = Property.ContainerPtrToValuePtr<void>(Container);
    if (const FBoolProperty* Typed = CastField<FBoolProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptManagedFunctionValueKindV1::Boolean;
        OutValue.Payload = Typed->GetPropertyValue(Value) ? 1 : 0;
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptManagedFunctionValueKindV1::Byte;
        OutValue.Payload = Typed->GetPropertyValue(Value);
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptManagedFunctionValueKindV1::Int32;
        OutValue.Payload = static_cast<uint64>(static_cast<int64>(
            Typed->GetPropertyValue(Value)));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(&Property))
    {
        OutValue.Kind = EUnifiedScriptManagedFunctionValueKindV1::Int64;
        OutValue.Payload = static_cast<uint64>(Typed->GetPropertyValue(Value));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptManagedFunctionValueKindV1::Single;
        const float Scalar = Typed->GetPropertyValue(Value);
        static_assert(sizeof(Scalar) == sizeof(uint32));
        uint32 Bits = 0;
        FMemory::Memcpy(&Bits, &Scalar, sizeof(Bits));
        OutValue.Payload = Bits;
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptManagedFunctionValueKindV1::Double;
        const double Scalar = Typed->GetPropertyValue(Value);
        static_assert(sizeof(Scalar) == sizeof(uint64));
        FMemory::Memcpy(&OutValue.Payload, &Scalar, sizeof(OutValue.Payload));
        return true;
    }
    return false;
}

bool UnpackReturnValue(
    const FProperty& Property,
    const FUnifiedScriptManagedFunctionValueV1& Value,
    void* Result)
{
    if (const FBoolProperty* Typed = CastField<FBoolProperty>(&Property);
        Typed != nullptr &&
        Value.Kind == EUnifiedScriptManagedFunctionValueKindV1::Boolean &&
        Value.Payload <= 1)
    {
        Typed->SetPropertyValue(Result, Value.Payload != 0);
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(&Property);
        Typed != nullptr &&
        Value.Kind == EUnifiedScriptManagedFunctionValueKindV1::Byte &&
        Value.Payload <= MAX_uint8)
    {
        Typed->SetPropertyValue(Result, static_cast<uint8>(Value.Payload));
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptManagedFunctionValueKindV1::Int32)
    {
        Typed->SetPropertyValue(Result, static_cast<int32>(Value.Payload));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptManagedFunctionValueKindV1::Int64)
    {
        Typed->SetPropertyValue(Result, static_cast<int64>(Value.Payload));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptManagedFunctionValueKindV1::Single)
    {
        const uint32 Bits = static_cast<uint32>(Value.Payload);
        float Scalar = 0.0f;
        FMemory::Memcpy(&Scalar, &Bits, sizeof(Scalar));
        Typed->SetPropertyValue(Result, Scalar);
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptManagedFunctionValueKindV1::Double)
    {
        double Scalar = 0.0;
        FMemory::Memcpy(&Scalar, &Value.Payload, sizeof(Scalar));
        Typed->SetPropertyValue(Result, Scalar);
        return true;
    }
    return false;
}

UUnifiedScriptComponent* ResolveComponent(UObject* Context)
{
    if (UUnifiedScriptComponent* Component = Cast<UUnifiedScriptComponent>(Context))
    {
        return Component;
    }
    if (AActor* Actor = Cast<AActor>(Context))
    {
        return Actor->FindComponentByClass<UUnifiedScriptComponent>();
    }
    return nullptr;
}

UUnifiedScriptWorldSubsystem* ResolveWorldHost(UObject* WorldContextObject)
{
    UWorld* World = GEngine != nullptr && WorldContextObject != nullptr
        ? GEngine->GetWorldFromContextObject(
            WorldContextObject,
            EGetWorldErrorMode::ReturnNull)
        : nullptr;
    return World != nullptr
        ? World->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
}

uint64 DoubleBits(double Value)
{
    uint64 Bits = 0;
    static_assert(sizeof(Bits) == sizeof(Value));
    FMemory::Memcpy(&Bits, &Value, sizeof(Bits));
    return Bits;
}

double BitsDouble(uint64 Bits)
{
    double Value = 0.0;
    static_assert(sizeof(Bits) == sizeof(Value));
    FMemory::Memcpy(&Value, &Bits, sizeof(Value));
    return Value;
}

uint64 TypeIdFromPath(const FString& Path)
{
    constexpr uint64 OffsetBasis = 14695981039346656037ull;
    constexpr uint64 Prime = 1099511628211ull;
    if (Path.IsEmpty())
    {
        return 0;
    }
    uint64 Hash = OffsetBasis;
    for (const TCHAR Character : Path)
    {
        if (Character > 0x7f)
        {
            return 0;
        }
        Hash ^= static_cast<uint8>(Character);
        Hash *= Prime;
    }
    return Hash;
}

bool PathStartsWith(TConstArrayView<FName> Path, TConstArrayView<FName> Prefix)
{
    if (Path.Num() < Prefix.Num())
    {
        return false;
    }
    for (int32 Index = 0; Index < Prefix.Num(); ++Index)
    {
        if (Path[Index] != Prefix[Index])
        {
            return false;
        }
    }
    return true;
}

void CollectDirectStructFields(
    const FUnifiedScriptManagedValueTypeBinding& Binding,
    TConstArrayView<FName> Prefix,
    TArray<FName, TInlineAllocator<32>>& OutNames)
{
    OutNames.Reset();
    for (const FUnifiedScriptManagedStructFieldPath& Field : Binding.StructFields)
    {
        if (Field.Segments.Num() == Prefix.Num() + 1 &&
            PathStartsWith(Field.Segments, Prefix))
        {
            OutNames.Add(Field.Segments.Last());
        }
    }
}

bool PackValueV2(
    const FProperty& Property,
    const void* Container,
    UUnifiedScriptWorldSubsystem& Host,
    TArray<uint8, TInlineAllocator<512>>& Arena,
    const FUnifiedScriptManagedValueTypeBinding* TypeBinding,
    TConstArrayView<FName> StructPrefix,
    FUnifiedScriptNativeValueV3& OutValue,
    FString& OutError)
{
    const void* Value = Property.ContainerPtrToValuePtr<void>(Container);
    const UEnum* Enum = nullptr;
    int64 EnumValue = 0;
    if (const FEnumProperty* EnumProperty = CastField<FEnumProperty>(&Property))
    {
        Enum = EnumProperty->GetEnum();
        EnumValue = EnumProperty->GetUnderlyingProperty()->GetSignedIntPropertyValue(Value);
    }
    else if (const FByteProperty* ByteEnumProperty = CastField<FByteProperty>(&Property);
             ByteEnumProperty != nullptr && ByteEnumProperty->Enum != nullptr)
    {
        Enum = ByteEnumProperty->Enum;
        EnumValue = ByteEnumProperty->GetPropertyValue(Value);
    }
    if (Enum != nullptr)
    {
        const FString TypePath = Enum->GetPathName();
        if (StructPrefix.IsEmpty() && TypeBinding != nullptr &&
            !TypeBinding->UnrealTypePath.IsEmpty() &&
            TypeBinding->UnrealTypePath != TypePath)
        {
            OutError = FString::Printf(
                TEXT("Blueprint enum parameter '%s' resolved '%s', expected '%s'."),
                *Property.GetName(),
                *TypePath,
                *TypeBinding->UnrealTypePath);
            return false;
        }
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Enum;
        OutValue.TypeId = TypeIdFromPath(TypePath);
        OutValue.Payload0 = static_cast<uint64>(EnumValue);
        return OutValue.TypeId != 0;
    }
    if (const FBoolProperty* Typed = CastField<FBoolProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Boolean;
        OutValue.Payload0 = Typed->GetPropertyValue(Value) ? 1 : 0;
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::UInt8;
        OutValue.Payload0 = Typed->GetPropertyValue(Value);
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Int32;
        OutValue.Payload0 = static_cast<uint64>(static_cast<int64>(
            Typed->GetPropertyValue(Value)));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(&Property))
    {
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Int64;
        OutValue.Payload0 = static_cast<uint64>(Typed->GetPropertyValue(Value));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Float32;
        const float Scalar = Typed->GetPropertyValue(Value);
        uint32 Bits = 0;
        FMemory::Memcpy(&Bits, &Scalar, sizeof(Bits));
        OutValue.Payload0 = Bits;
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Float64;
        OutValue.Payload0 = DoubleBits(Typed->GetPropertyValue(Value));
        return true;
    }
    if (const FStrProperty* Typed = CastField<FStrProperty>(&Property))
    {
        const FString& String = Typed->GetPropertyValue(Value);
        const FTCHARToUTF8 Utf8(*String);
        if (Utf8.Length() > MaximumManagedFunctionArenaBytes - Arena.Num())
        {
            OutError = FString::Printf(
                TEXT("Blueprint string parameter '%s' exceeds the %d-byte managed call arena."),
                *Property.GetName(),
                MaximumManagedFunctionArenaBytes);
            return false;
        }
        const int32 Offset = Arena.Num();
        if (Utf8.Length() > 0)
        {
            const int32 AddedOffset = Arena.AddUninitialized(Utf8.Length());
            FMemory::Memcpy(Arena.GetData() + AddedOffset, Utf8.Get(), Utf8.Length());
        }
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::String;
        OutValue.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        OutValue.Payload0 = static_cast<uint64>(Offset);
        OutValue.Payload1 = static_cast<uint64>(Utf8.Length());
        return true;
    }
    if (const FNameProperty* Typed = CastField<FNameProperty>(&Property))
    {
        const FString String = Typed->GetPropertyValue(Value).ToString();
        const FTCHARToUTF8 Utf8(*String);
        if (Utf8.Length() > MaximumManagedFunctionArenaBytes - Arena.Num())
        {
            OutError = FString::Printf(
                TEXT("Blueprint name parameter '%s' exceeds the %d-byte managed call arena."),
                *Property.GetName(),
                MaximumManagedFunctionArenaBytes);
            return false;
        }
        const int32 Offset = Arena.Num();
        if (Utf8.Length() > 0)
        {
            const int32 AddedOffset = Arena.AddUninitialized(Utf8.Length());
            FMemory::Memcpy(Arena.GetData() + AddedOffset, Utf8.Get(), Utf8.Length());
        }
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Name;
        OutValue.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        OutValue.Payload0 = static_cast<uint64>(Offset);
        OutValue.Payload1 = static_cast<uint64>(Utf8.Length());
        return true;
    }
    if (const FObjectPropertyBase* Typed = CastField<FObjectPropertyBase>(&Property))
    {
        UObject* Object = Typed->GetObjectPropertyValue(Value);
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Object;
        if (Object == nullptr)
        {
            OutValue.Flags = EUnifiedScriptNativeValueFlagsV3::Null;
            return true;
        }
        FUnifiedScriptObjectHandleResult HandleResult;
        const FUnifiedScriptObjectHandle Handle = Host.RegisterObject(Object, HandleResult);
        if (!HandleResult.IsSuccess())
        {
            OutError = HandleResult.Message;
            return false;
        }
        OutValue.Payload0 = Handle.Value;
        return true;
    }
    if (const FStructProperty* Typed = CastField<FStructProperty>(&Property);
        Typed != nullptr && Typed->Struct->IsChildOf(TBaseStructure<FVector>::Get()))
    {
        const FVector& Vector = *static_cast<const FVector*>(Value);
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Vector3;
        OutValue.Payload0 = DoubleBits(Vector.X);
        OutValue.Payload1 = DoubleBits(Vector.Y);
        OutValue.Payload2 = DoubleBits(Vector.Z);
        return true;
    }
    if (const FStructProperty* Typed = CastField<FStructProperty>(&Property))
    {
        if (TypeBinding == nullptr)
        {
            OutError = FString::Printf(
                TEXT("Blueprint struct parameter '%s' has no persistent managed field contract."),
                *Property.GetName());
            return false;
        }
        const FString TypePath = Typed->Struct->GetPathName();
        if (StructPrefix.IsEmpty() && !TypeBinding->UnrealTypePath.IsEmpty() &&
            TypeBinding->UnrealTypePath != TypePath)
        {
            OutError = FString::Printf(
                TEXT("Blueprint struct parameter '%s' resolved '%s', expected '%s'."),
                *Property.GetName(),
                *TypePath,
                *TypeBinding->UnrealTypePath);
            return false;
        }
        TArray<FName, TInlineAllocator<32>> FieldNames;
        CollectDirectStructFields(*TypeBinding, StructPrefix, FieldNames);
        const int64 DescriptorBytes =
            static_cast<int64>(FieldNames.Num()) * sizeof(FUnifiedScriptNativeValueV3);
        if (DescriptorBytes > MaximumManagedFunctionArenaBytes - Arena.Num())
        {
            OutError = FString::Printf(
                TEXT("Blueprint struct parameter '%s' exceeds the %d-byte managed call arena."),
                *Property.GetName(),
                MaximumManagedFunctionArenaBytes);
            return false;
        }
        const int32 DescriptorOffset = Arena.AddZeroed(static_cast<int32>(DescriptorBytes));
        for (int32 Index = 0; Index < FieldNames.Num(); ++Index)
        {
            const FProperty* Field = FindFProperty<FProperty>(Typed->Struct, FieldNames[Index]);
            if (Field == nullptr)
            {
                OutError = FString::Printf(
                    TEXT("Blueprint struct '%s' no longer contains managed field '%s'."),
                    *TypePath,
                    *FieldNames[Index].ToString());
                return false;
            }
            TArray<FName, TInlineAllocator<8>> ChildPrefix;
            ChildPrefix.Append(StructPrefix.GetData(), StructPrefix.Num());
            ChildPrefix.Add(FieldNames[Index]);
            FUnifiedScriptNativeValueV3 Cell;
            if (!PackValueV2(
                    *Field,
                    Value,
                    Host,
                    Arena,
                    TypeBinding,
                    ChildPrefix,
                    Cell,
                    OutError))
            {
                return false;
            }
            FMemory::Memcpy(
                Arena.GetData() + DescriptorOffset +
                    Index * sizeof(FUnifiedScriptNativeValueV3),
                &Cell,
                sizeof(Cell));
        }
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Struct;
        OutValue.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        OutValue.TypeId = TypeIdFromPath(TypePath);
        OutValue.Payload0 = DescriptorOffset;
        OutValue.Payload1 = DescriptorBytes;
        OutValue.Payload2 = FieldNames.Num();
        OutValue.Payload3 = sizeof(FUnifiedScriptNativeValueV3);
        return OutValue.TypeId != 0;
    }
    OutError = FString::Printf(
        TEXT("Blueprint parameter '%s' uses unsupported ABI v2 property '%s'."),
        *Property.GetName(),
        *Property.GetClass()->GetName());
    return false;
}

bool UnpackReturnValueV2(
    const FProperty& Property,
    const FUnifiedScriptNativeValueV3& Value,
    TConstArrayView<uint8> Arena,
    uint32 ByteCount,
    UUnifiedScriptWorldSubsystem& Host,
    const FUnifiedScriptManagedValueTypeBinding* TypeBinding,
    TConstArrayView<FName> StructPrefix,
    void* Result,
    FString& OutError)
{
    const UEnum* Enum = nullptr;
    FNumericProperty* EnumStorage = nullptr;
    if (const FEnumProperty* EnumProperty = CastField<FEnumProperty>(&Property))
    {
        Enum = EnumProperty->GetEnum();
        EnumStorage = EnumProperty->GetUnderlyingProperty();
    }
    else if (FByteProperty* ByteEnumProperty = const_cast<FByteProperty*>(
                 CastField<FByteProperty>(&Property));
             ByteEnumProperty != nullptr && ByteEnumProperty->Enum != nullptr)
    {
        Enum = ByteEnumProperty->Enum;
        EnumStorage = ByteEnumProperty;
    }
    if (Enum != nullptr)
    {
        const FString TypePath = Enum->GetPathName();
        if (Value.Kind != EUnifiedScriptNativeValueKindV3::Enum ||
            Value.Flags != EUnifiedScriptNativeValueFlagsV3::None ||
            Value.TypeId != TypeIdFromPath(TypePath) ||
            (StructPrefix.IsEmpty() && TypeBinding != nullptr &&
             !TypeBinding->UnrealTypePath.IsEmpty() &&
             TypeBinding->UnrealTypePath != TypePath))
        {
            OutError = FString::Printf(
                TEXT("Managed enum result does not match Blueprint enum '%s'."),
                *TypePath);
            return false;
        }
        EnumStorage->SetIntPropertyValue(Result, static_cast<int64>(Value.Payload0));
        return true;
    }
    if (const FBoolProperty* Typed = CastField<FBoolProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Boolean &&
        Value.Payload0 <= 1)
    {
        Typed->SetPropertyValue(Result, Value.Payload0 != 0);
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::UInt8 &&
        Value.Payload0 <= MAX_uint8)
    {
        Typed->SetPropertyValue(Result, static_cast<uint8>(Value.Payload0));
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Int32)
    {
        Typed->SetPropertyValue(Result, static_cast<int32>(Value.Payload0));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Int64)
    {
        Typed->SetPropertyValue(Result, static_cast<int64>(Value.Payload0));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Float32)
    {
        const uint32 Bits = static_cast<uint32>(Value.Payload0);
        float Scalar = 0.0f;
        FMemory::Memcpy(&Scalar, &Bits, sizeof(Scalar));
        Typed->SetPropertyValue(Result, Scalar);
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Float64)
    {
        Typed->SetPropertyValue(Result, BitsDouble(Value.Payload0));
        return true;
    }
    if (const FStrProperty* Typed = CastField<FStrProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::String &&
        Value.Flags == EUnifiedScriptNativeValueFlagsV3::ArenaBacked &&
        Value.Payload0 <= ByteCount && Value.Payload1 <= ByteCount - Value.Payload0)
    {
        const int32 Offset = static_cast<int32>(Value.Payload0);
        const int32 Length = static_cast<int32>(Value.Payload1);
        const FUTF8ToTCHAR Converted(
            reinterpret_cast<const ANSICHAR*>(Arena.GetData() + Offset),
            Length);
        Typed->SetPropertyValue(Result, FString(Converted.Length(), Converted.Get()));
        return true;
    }
    if (const FNameProperty* Typed = CastField<FNameProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Name &&
        Value.Flags == EUnifiedScriptNativeValueFlagsV3::ArenaBacked &&
        Value.Payload0 <= ByteCount && Value.Payload1 <= ByteCount - Value.Payload0)
    {
        const int32 Offset = static_cast<int32>(Value.Payload0);
        const int32 Length = static_cast<int32>(Value.Payload1);
        const FUTF8ToTCHAR Converted(
            reinterpret_cast<const ANSICHAR*>(Arena.GetData() + Offset),
            Length);
        Typed->SetPropertyValue(
            Result,
            FName(FString(Converted.Length(), Converted.Get())));
        return true;
    }
    if (const FObjectPropertyBase* Typed = CastField<FObjectPropertyBase>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Object)
    {
        if (Value.Payload0 == 0)
        {
            Typed->SetObjectPropertyValue(Result, nullptr);
            return true;
        }
        FUnifiedScriptObjectHandleResult HandleResult;
        UObject* Object = Host.ResolveObject({Value.Payload0}, HandleResult);
        if (!HandleResult.IsSuccess() ||
            (Object != nullptr && !Object->IsA(Typed->PropertyClass)))
        {
            OutError = HandleResult.IsSuccess()
                ? FString::Printf(
                    TEXT("Returned object '%s' is not a '%s'."),
                    *GetPathNameSafe(Object),
                    *GetPathNameSafe(Typed->PropertyClass))
                : HandleResult.Message;
            return false;
        }
        Typed->SetObjectPropertyValue(Result, Object);
        return true;
    }
    if (const FStructProperty* Typed = CastField<FStructProperty>(&Property);
        Typed != nullptr && Typed->Struct->IsChildOf(TBaseStructure<FVector>::Get()) &&
        Value.Kind == EUnifiedScriptNativeValueKindV3::Vector3)
    {
        *static_cast<FVector*>(Result) = FVector(
            BitsDouble(Value.Payload0),
            BitsDouble(Value.Payload1),
            BitsDouble(Value.Payload2));
        return true;
    }
    if (const FStructProperty* Typed = CastField<FStructProperty>(&Property))
    {
        if (TypeBinding == nullptr)
        {
            OutError = FString::Printf(
                TEXT("Blueprint struct result '%s' has no persistent managed field contract."),
                *Property.GetName());
            return false;
        }
        const FString TypePath = Typed->Struct->GetPathName();
        TArray<FName, TInlineAllocator<32>> FieldNames;
        CollectDirectStructFields(*TypeBinding, StructPrefix, FieldNames);
        const uint64 DescriptorBytes =
            static_cast<uint64>(FieldNames.Num()) * sizeof(FUnifiedScriptNativeValueV3);
        if (Value.Kind != EUnifiedScriptNativeValueKindV3::Struct ||
            Value.Flags != EUnifiedScriptNativeValueFlagsV3::ArenaBacked ||
            Value.TypeId != TypeIdFromPath(TypePath) ||
            Value.Payload1 != DescriptorBytes ||
            Value.Payload2 != static_cast<uint64>(FieldNames.Num()) ||
            Value.Payload3 != sizeof(FUnifiedScriptNativeValueV3) ||
            Value.Payload0 > ByteCount || Value.Payload1 > ByteCount - Value.Payload0 ||
            (StructPrefix.IsEmpty() && !TypeBinding->UnrealTypePath.IsEmpty() &&
             TypeBinding->UnrealTypePath != TypePath))
        {
            OutError = FString::Printf(
                TEXT("Managed struct result does not match Blueprint struct '%s'."),
                *TypePath);
            return false;
        }
        const int32 DescriptorOffset = static_cast<int32>(Value.Payload0);
        for (int32 Index = 0; Index < FieldNames.Num(); ++Index)
        {
            const FProperty* Field = FindFProperty<FProperty>(Typed->Struct, FieldNames[Index]);
            if (Field == nullptr)
            {
                OutError = FString::Printf(
                    TEXT("Blueprint struct '%s' no longer contains managed field '%s'."),
                    *TypePath,
                    *FieldNames[Index].ToString());
                return false;
            }
            FUnifiedScriptNativeValueV3 Cell;
            FMemory::Memcpy(
                &Cell,
                Arena.GetData() + DescriptorOffset +
                    Index * sizeof(FUnifiedScriptNativeValueV3),
                sizeof(Cell));
            TArray<FName, TInlineAllocator<8>> ChildPrefix;
            ChildPrefix.Append(StructPrefix.GetData(), StructPrefix.Num());
            ChildPrefix.Add(FieldNames[Index]);
            void* FieldValue = Field->ContainerPtrToValuePtr<void>(Result);
            if (!UnpackReturnValueV2(
                    *Field,
                    Cell,
                    Arena,
                    ByteCount,
                    Host,
                    TypeBinding,
                    ChildPrefix,
                    FieldValue,
                    OutError))
            {
                return false;
            }
        }
        return true;
    }
    OutError = FString::Printf(
        TEXT("Managed ABI v2 return kind %u does not match Blueprint property '%s'."),
        static_cast<uint32>(Value.Kind),
        *Property.GetName());
    return false;
}
}

#if WITH_EDITOR
UClass* UUnifiedScriptBlueprint::GetBlueprintClass() const
{
    return UUnifiedScriptBlueprintGeneratedClass::StaticClass();
}
#endif

void UUnifiedScriptBlueprintGeneratedClass::Bind()
{
    // UClass::Bind binds every owned UFunction. Register first so Blueprint
    // compiler REINST classes and package-load relinks can resolve the dynamic
    // native functions without a handwritten UHT registration table.
    RegisterManagedNativeFunctions();
    Super::Bind();
    bManagedFunctionsBound = false;
    if (IsInGameThread())
    {
        BindManagedFunctions();
    }
}

void UUnifiedScriptBlueprintGeneratedClass::PurgeClass(bool bRecompilingOnLoad)
{
    Super::PurgeClass(bRecompilingOnLoad);
    bManagedFunctionsBound = false;
    RegisteredManagedNativeFunctions.Reset();
}

void UUnifiedScriptBlueprintGeneratedClass::PostLoad()
{
    bManagedFunctionsBound = false;
    RegisteredManagedNativeFunctions.Reset();
    Super::PostLoad();
    if (IsInGameThread())
    {
        BindManagedFunctions();
    }
}

void UUnifiedScriptBlueprintGeneratedClass::PostLoadDefaultObject(UObject* Object)
{
    Super::PostLoadDefaultObject(Object);
    if (Object == GetDefaultObject(false) && IsInGameThread())
    {
        // UFunction::Bind may run after the generated class PostLoad and restore
        // Blueprint bytecode dispatch. The CDO is loaded only after every owned
        // function has linked, so this is the durable post-load rebind point.
        BindManagedFunctions();
    }
}

void UUnifiedScriptBlueprintGeneratedClass::PostInitInstance(
    UObject* InObj,
    FObjectInstancingGraph* InstanceGraph)
{
    Super::PostInitInstance(InObj, InstanceGraph);
    // CDO construction may happen on the async loading thread. The first
    // gameplay instance is constructed on the game thread; re-apply the table
    // there in case a cooked UFunction::Bind ran after the class/CDO hooks and
    // restored ProcessInternal.
    bool bNeedsManagedFunctionRebind = !bManagedFunctionsBound;
    if (IsInGameThread() && !bNeedsManagedFunctionRebind)
    {
        for (const TPair<FName, FUnifiedScriptManagedFunctionBinding>& Pair :
             ManagedFunctionBindings)
        {
            if (!IsManagedFunctionBound(Pair.Key))
            {
                bNeedsManagedFunctionRebind = true;
                break;
            }
        }
    }
    if (IsInGameThread() && bNeedsManagedFunctionRebind)
    {
        BindManagedFunctions();
    }
}

void UUnifiedScriptBlueprintGeneratedClass::BindManagedFunctions()
{
    check(IsInGameThread());
    RegisterManagedNativeFunctions();
    bool bAllFunctionsBound = true;
    for (const TPair<FName, FUnifiedScriptManagedFunctionBinding>& Pair :
         ManagedFunctionBindings)
    {
        UFunction* Function = FindFunctionByName(
            Pair.Key,
            EIncludeSuperFlag::ExcludeSuper);
        if (Function == nullptr)
        {
            bAllFunctionsBound = false;
            continue;
        }
        // Blueprint VM calls, including an explicit Call Parent Function from a
        // child override, only invoke the installed dispatcher for a native
        // UFunction. Register the generic thunk on this generated class before
        // any later UFunction::Bind pass tries to resolve it.
        Function->FunctionFlags |= FUNC_Native;
        Function->FunctionFlags |= FUNC_Public | FUNC_BlueprintCallable;
        const bool bManagedEventEntry = Pair.Value.bBlueprintEvent ||
            Pair.Value.bInterfaceFunction;
        if (bManagedEventEntry)
        {
            Function->FunctionFlags |= FUNC_Event | FUNC_BlueprintEvent;
        }
        else
        {
            Function->FunctionFlags &= ~(FUNC_Event | FUNC_BlueprintEvent);
        }
        Function->SetNativeFunc(
            &UUnifiedScriptBlueprintGeneratedClass::execDispatchManagedFunction);
#if UE_BLUEPRINT_EVENTGRAPH_FASTCALLS
        if (bManagedEventEntry)
        {
            // The empty generated base graph is only a durable signature for
            // managed Blueprint events and interface implementations. Cook or
            // package reload may patch it as an ubergraph fast-call stub, which
            // would bypass the native managed implementation installed above.
            // Child Blueprint overrides own separate functions and keep their
            // normal fast-call path; only the managed base entry is cleared.
            Function->EventGraphFunction = nullptr;
            Function->EventGraphCallOffset = INDEX_NONE;
        }
#endif
    }
    bManagedFunctionsBound = bAllFunctionsBound;
}

void UUnifiedScriptBlueprintGeneratedClass::PrepareManagedFunctionsForRecompile()
{
    check(IsInGameThread());
    // FBlueprintCompileReinstancer duplicates the old class before its native
    // lookup table is rebuilt. Keep the transition copy on ordinary Blueprint
    // bytecode until its generated-class Bind pass installs the dynamic table.
    for (const TPair<FName, FUnifiedScriptManagedFunctionBinding>& Pair :
         ManagedFunctionBindings)
    {
        if (UFunction* Function = FindFunctionByName(
                Pair.Key,
                EIncludeSuperFlag::ExcludeSuper))
        {
            Function->FunctionFlags &= ~FUNC_Native;
        }
    }
    bManagedFunctionsBound = false;
}

void UUnifiedScriptBlueprintGeneratedClass::RegisterManagedNativeFunctions()
{
    for (const TPair<FName, FUnifiedScriptManagedFunctionBinding>& Pair :
         ManagedFunctionBindings)
    {
        if (RegisteredManagedNativeFunctions.Contains(Pair.Key))
        {
            continue;
        }
        AddNativeFunction(
            *Pair.Key.ToString(),
            &UUnifiedScriptBlueprintGeneratedClass::execDispatchManagedFunction);
        RegisteredManagedNativeFunctions.Add(Pair.Key);
    }
}

bool UUnifiedScriptBlueprintGeneratedClass::IsManagedFunctionBound(
    FName FunctionName) const
{
    const FUnifiedScriptManagedFunctionBinding* Binding =
        ManagedFunctionBindings.Find(FunctionName);
    const UFunction* Function = FindFunctionByName(
        FunctionName,
        EIncludeSuperFlag::ExcludeSuper);
    if (Binding == nullptr || Function == nullptr ||
        !Function->HasAnyFunctionFlags(FUNC_Native) ||
        Function->GetNativeFunc() !=
            &UUnifiedScriptBlueprintGeneratedClass::execDispatchManagedFunction)
    {
        return false;
    }
#if UE_BLUEPRINT_EVENTGRAPH_FASTCALLS
    if ((Binding->bBlueprintEvent || Binding->bInterfaceFunction) &&
        Function->EventGraphFunction != nullptr)
    {
        return false;
    }
#endif
    return true;
}

DEFINE_FUNCTION(UUnifiedScriptBlueprintGeneratedClass::execDispatchManagedFunction)
{
    using namespace UE::UnifiedScript::Blueprint::Private;
    // Stack.Node is the frame that owns the currently executing bytecode. For
    // an explicit Blueprint Call Parent Function that is the child ubergraph,
    // not the managed parent UFunction selected by EX_FinalFunction.
    UFunction* Function = Stack.CurrentNativeFunction;

    // ProcessEvent supplies an already materialized parameter frame, while a
    // call emitted by the Blueprint VM leaves its arguments in the caller's
    // bytecode stream. A generic dynamic thunk must consume that stream before
    // it can return, including on later dispatch errors, otherwise the caller
    // resumes in the middle of its argument list.
    TUniquePtr<FStructOnScope> DecodedParameters;
    void* ParameterContainer = Stack.Locals;
    if (Function != nullptr && Stack.Node != Function)
    {
        DecodedParameters = MakeUnique<FStructOnScope>(Function);
        ParameterContainer = DecodedParameters->GetStructMemory();
        for (TFieldIterator<FProperty> It(
                 Function,
                 EFieldIteratorFlags::ExcludeSuper);
             It;
             ++It)
        {
            FProperty* Parameter = *It;
            if (!Parameter->HasAnyPropertyFlags(CPF_Parm) ||
                Parameter->HasAnyPropertyFlags(CPF_ReturnParm))
            {
                continue;
            }
            Stack.Step(
                Stack.Object,
                Parameter->ContainerPtrToValuePtr<void>(ParameterContainer));
        }
        P_FINISH;
    }
    const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass = Function != nullptr
        ? Cast<UUnifiedScriptBlueprintGeneratedClass>(Function->GetOuter())
        : nullptr;
    const FUnifiedScriptManagedFunctionBinding* Binding = GeneratedClass != nullptr
        ? GeneratedClass->ManagedFunctionBindings.Find(Function->GetFName())
        : nullptr;
    const FProperty* ReturnProperty = Function != nullptr
        ? Function->GetReturnProperty()
        : nullptr;
    if (ReturnProperty != nullptr && RESULT_PARAM != nullptr)
    {
        ReturnProperty->ClearValue(RESULT_PARAM);
    }
    if (Function == nullptr || Binding == nullptr || Binding->ManagedName.IsEmpty())
    {
        UE_LOG(
            LogTemp,
            Error,
            TEXT("UnifiedScript managed function binding is missing: function='%s', owner='%s', ownerType='%s', flags=0x%08x, super='%s'."),
            Function != nullptr ? *Function->GetPathName() : TEXT("<null>"),
            Function != nullptr && Function->GetOuter() != nullptr
                ? *Function->GetOuter()->GetPathName()
                : TEXT("<null>"),
            Function != nullptr && Function->GetOuter() != nullptr
                ? *Function->GetOuter()->GetClass()->GetName()
                : TEXT("<null>"),
            Function != nullptr ? static_cast<uint32>(Function->FunctionFlags) : 0u,
            Function != nullptr && Function->GetSuperFunction() != nullptr
                ? *Function->GetSuperFunction()->GetPathName()
                : TEXT("<null>"));
        return;
    }
    if (Binding->AbiVersion >= ManagedFunctionAbiV2)
    {
        UUnifiedScriptComponent* Component = nullptr;
        UUnifiedScriptWorldSubsystem* Host = nullptr;
        if (Binding->bStatic)
        {
            const FObjectPropertyBase* WorldContextProperty =
                FindFProperty<FObjectPropertyBase>(Function, WorldContextObjectName);
            UObject* WorldContextObject = WorldContextProperty != nullptr
                ? WorldContextProperty->GetObjectPropertyValue_InContainer(
                    ParameterContainer)
                : nullptr;
            Host = ResolveWorldHost(WorldContextObject);
        }
        else
        {
            Component = ResolveComponent(Context);
            Host = Component != nullptr
                ? ResolveWorldHost(Component)
                : nullptr;
        }
        if (Host == nullptr || (!Binding->bStatic && Component == nullptr))
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UnifiedScript function '%s' has no managed World host."),
                *Function->GetPathName());
            return;
        }

        TArray<FUnifiedScriptNativeValueV3, TInlineAllocator<4>> ArgumentsV2;
        TArray<uint8, TInlineAllocator<512>> Arena;
        Arena.Reserve(MaximumManagedFunctionArenaBytes);
        FString MarshalError;
        int32 ManagedParameterIndex = 0;
        for (TFieldIterator<FProperty> It(Function, EFieldIteratorFlags::ExcludeSuper); It; ++It)
        {
            const FProperty* Parameter = *It;
            if (!Parameter->HasAnyPropertyFlags(CPF_Parm) ||
                Parameter->HasAnyPropertyFlags(CPF_ReturnParm) ||
                (Binding->bStatic &&
                 (Parameter->GetFName() == WorldContextObjectName ||
                  Parameter->GetFName() == KismetWorldContextName)))
            {
                continue;
            }
            FUnifiedScriptNativeValueV3& Value = ArgumentsV2.AddDefaulted_GetRef();
            const FUnifiedScriptManagedValueTypeBinding* ParameterType =
                Binding->ParameterTypes.IsValidIndex(ManagedParameterIndex)
                    ? &Binding->ParameterTypes[ManagedParameterIndex]
                    : nullptr;
            if (ArgumentsV2.Num() > 4 ||
                !PackValueV2(
                    *Parameter,
                    ParameterContainer,
                    *Host,
                    Arena,
                    ParameterType,
                    {},
                    Value,
                    MarshalError))
            {
                UE_LOG(
                    LogTemp,
                    Error,
                    TEXT("UnifiedScript cannot marshal Blueprint parameter '%s' for '%s': %s"),
                    *Parameter->GetName(),
                    *Function->GetPathName(),
                    MarshalError.IsEmpty() ? TEXT("unsupported call shape") : *MarshalError);
                return;
            }
            ++ManagedParameterIndex;
        }
        const uint32 InputByteCount = static_cast<uint32>(Arena.Num());
        Arena.SetNumUninitialized(MaximumManagedFunctionArenaBytes, EAllowShrinking::No);
        FUnifiedScriptNativeValueV3 ResultV2;
        FUnifiedScriptNativeCallFrameV3 Frame;
        Frame.Arguments = ArgumentsV2.GetData();
        Frame.ArgumentCount = ArgumentsV2.Num();
        Frame.Results = ReturnProperty != nullptr ? &ResultV2 : nullptr;
        Frame.ResultCapacity = ReturnProperty != nullptr ? 1 : 0;
        Frame.ByteBuffer = Arena.GetData();
        Frame.ByteCapacity = Arena.Num();
        Frame.ByteCount = InputByteCount;

        FString Error;
        bool bInvoked = false;
        if (Binding->bStatic)
        {
            UUnifiedScriptAsset* ScriptAsset = GeneratedClass->ManagedScriptAsset;
            FString AssemblyPath;
            FString TypeName;
            if (ScriptAsset == nullptr ||
                !ScriptAsset->ResolveRuntimeConfiguration(
                    true,
                    AssemblyPath,
                    TypeName,
                    Error))
            {
                UE_LOG(
                    LogTemp,
                    Error,
                    TEXT("UnifiedScript static function '%s' cannot resolve its Script Asset: %s"),
                    *Function->GetPathName(),
                    Error.IsEmpty() ? TEXT("missing generated-class sidecar") : *Error);
                return;
            }
            bInvoked = Host->InvokeManagedScriptFunctionV2(
                0,
                AssemblyPath,
                TypeName,
                Binding->ManagedName,
                Frame,
                Error);
        }
        else
        {
            bInvoked = Component->InvokeManagedScriptFunctionV2(
                Binding->ManagedName,
                Frame,
                Error);
        }
        if (!bInvoked || Frame.Status != EUnifiedScriptNativeCallStatusV3::Success)
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UnifiedScript function '%s' ABI v2 dispatch failed (status=%u): %s"),
                *Function->GetPathName(),
                static_cast<uint32>(Frame.Status),
                *Error);
            return;
        }
        const uint32 ExpectedResultCount = ReturnProperty != nullptr ? 1 : 0;
        if (Frame.ResultCount != ExpectedResultCount)
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UnifiedScript function '%s' returned %u result cells; expected %u."),
                *Function->GetPathName(),
                Frame.ResultCount,
                ExpectedResultCount);
            return;
        }
        if (ReturnProperty != nullptr &&
            (RESULT_PARAM == nullptr ||
             !UnpackReturnValueV2(
                 *ReturnProperty,
                 ResultV2,
                 Arena,
                  Frame.ByteCount,
                  *Host,
                  &Binding->ReturnType,
                  {},
                  RESULT_PARAM,
                 Error)))
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UnifiedScript cannot marshal ABI v2 return value for '%s': %s"),
                *Function->GetPathName(),
                *Error);
        }
        return;
    }

    TArray<FUnifiedScriptManagedFunctionValueV1, TInlineAllocator<4>> Arguments;
    for (TFieldIterator<FProperty> It(Function, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const FProperty* Parameter = *It;
        if (!Parameter->HasAnyPropertyFlags(CPF_Parm) ||
            Parameter->HasAnyPropertyFlags(CPF_ReturnParm))
        {
            continue;
        }
        FUnifiedScriptManagedFunctionValueV1& Value = Arguments.AddDefaulted_GetRef();
        if (Arguments.Num() > 4 ||
            !PackValue(*Parameter, ParameterContainer, Value))
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UnifiedScript cannot marshal Blueprint parameter '%s' for '%s'."),
                *Parameter->GetName(),
                *Function->GetPathName());
            return;
        }
    }

    UUnifiedScriptComponent* Component = ResolveComponent(Context);
    if (Component == nullptr)
    {
        UE_LOG(
            LogTemp,
            Error,
            TEXT("UnifiedScript function '%s' has no managed component host."),
            *Function->GetPathName());
        return;
    }
    FUnifiedScriptManagedFunctionValueV1 Result;
    FString Error;
    if (!Component->InvokeManagedScriptFunction(
            Binding->ManagedName,
            Arguments,
            Result,
            Error))
    {
        UE_LOG(LogTemp, Error, TEXT("UnifiedScript function '%s' failed: %s"),
            *Function->GetPathName(), *Error);
        return;
    }
    if (ReturnProperty == nullptr)
    {
        if (Result.Kind != EUnifiedScriptManagedFunctionValueKindV1::Void)
        {
            UE_LOG(LogTemp, Error, TEXT("UnifiedScript void function '%s' returned kind %u."),
                *Function->GetPathName(), static_cast<uint32>(Result.Kind));
        }
        return;
    }
    if (RESULT_PARAM == nullptr || !UnpackReturnValue(*ReturnProperty, Result, RESULT_PARAM))
    {
        UE_LOG(LogTemp, Error, TEXT("UnifiedScript cannot marshal return value for '%s'."),
            *Function->GetPathName());
    }
}

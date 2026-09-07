#include "UnifiedScriptBlueprint.h"

#include "Engine/Engine.h"
#include "GameFramework/Actor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptPropertyValueCodec.h"
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
    FString& OutError,
    bool bDirectValue = false)
{
    const void* Value = bDirectValue ? Container : Property.ContainerPtrToValuePtr<void>(Container);
    if (StructPrefix.Num() > 16)
    {
        OutError = TEXT("Managed function value nesting exceeds 16 levels.");
        return false;
    }
    const FArrayProperty* ArrayProperty = CastField<FArrayProperty>(&Property);
    const FSetProperty* SetProperty = CastField<FSetProperty>(&Property);
    const FMapProperty* MapProperty = CastField<FMapProperty>(&Property);
    if (ArrayProperty || SetProperty || MapProperty)
    {
        const int32 Count = ArrayProperty ? FScriptArrayHelper(ArrayProperty, Value).Num() :
            SetProperty ? FScriptSetHelper(SetProperty, Value).Num() : FScriptMapHelper(MapProperty, Value).Num();
        const int32 CellsPerEntry = MapProperty ? 2 : 1;
        const int64 DescriptorBytes = static_cast<int64>(Count) * CellsPerEntry * sizeof(FUnifiedScriptNativeValueV3);
        if (DescriptorBytes > MaximumManagedFunctionArenaBytes - Arena.Num())
        {
            OutError = TEXT("Container arguments exceed the bounded managed function arena.");
            return false;
        }
        const int32 Offset = Arena.AddZeroed(static_cast<int32>(DescriptorBytes));
        int32 CellIndex = 0;
        auto PackCell = [&](const FProperty& ChildProperty, const void* ChildValue, const TCHAR* Segment)
        {
            TArray<FName, TInlineAllocator<8>> Prefix;
            Prefix.Append(StructPrefix.GetData(), StructPrefix.Num());
            Prefix.Add(FName(Segment));
            FUnifiedScriptNativeValueV3 Cell;
            if (!PackValueV2(ChildProperty, ChildValue, Host, Arena, TypeBinding, Prefix, Cell, OutError, true))
                return false;
            FMemory::Memcpy(Arena.GetData() + Offset + CellIndex++ * sizeof(Cell), &Cell, sizeof(Cell));
            return true;
        };
        if (ArrayProperty)
        {
            FScriptArrayHelper Array(ArrayProperty, Value);
            for (int32 Index = 0; Index < Array.Num(); ++Index)
                if (!PackCell(*ArrayProperty->Inner, Array.GetRawPtr(Index), TEXT("$element"))) return false;
        }
        else if (SetProperty)
        {
            FScriptSetHelper Set(SetProperty, Value);
            for (int32 Index = 0; Index < Set.GetMaxIndex(); ++Index)
                if (Set.IsValidIndex(Index) && !PackCell(*SetProperty->ElementProp, Set.GetElementPtr(Index), TEXT("$element"))) return false;
        }
        else
        {
            FScriptMapHelper Map(MapProperty, Value);
            for (int32 Index = 0; Index < Map.GetMaxIndex(); ++Index)
                if (Map.IsValidIndex(Index) &&
                    (!PackCell(*MapProperty->KeyProp, Map.GetKeyPtr(Index), TEXT("$key")) ||
                     !PackCell(*MapProperty->ValueProp, Map.GetValuePtr(Index), TEXT("$value")))) return false;
        }
        OutValue.Kind = ArrayProperty ? EUnifiedScriptNativeValueKindV3::Array :
            SetProperty ? EUnifiedScriptNativeValueKindV3::Set : EUnifiedScriptNativeValueKindV3::Map;
        OutValue.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        OutValue.TypeId = UE::UnifiedScript::PropertyValueCodec::TypeIdFromPath(
            UE::UnifiedScript::PropertyValueCodec::ContainerValueNativeName(Property));
        OutValue.Payload0 = Offset;
        OutValue.Payload1 = DescriptorBytes;
        OutValue.Payload2 = Count;
        OutValue.Payload3 = sizeof(FUnifiedScriptNativeValueV3);
        return OutValue.TypeId != 0;
    }
    if (const FSoftObjectProperty* Typed = CastField<FSoftObjectProperty>(&Property))
    {
        const FString Path = Typed->GetPropertyValue(Value).ToSoftObjectPath().ToString();
        const FTCHARToUTF8 Utf8(*Path);
        if (Utf8.Length() > MaximumManagedFunctionArenaBytes - Arena.Num())
        {
            OutError = TEXT("Soft Reference path exceeds the managed function arena.");
            return false;
        }
        const int32 Offset = Arena.AddUninitialized(Utf8.Length());
        if (Utf8.Length() > 0) FMemory::Memcpy(Arena.GetData() + Offset, Utf8.Get(), Utf8.Length());
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::String;
        OutValue.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        OutValue.Payload0 = Offset;
        OutValue.Payload1 = Utf8.Length();
        return true;
    }
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
        OutValue.TypeId = UE::UnifiedScript::PropertyValueCodec::TypeIdFromPath(TypePath);
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
    if (const FTextProperty* Typed = CastField<FTextProperty>(&Property))
    {
        const FString String = Typed->GetPropertyValue(Value).ToString();
        const FTCHARToUTF8 Utf8(*String);
        if (Utf8.Length() > MaximumManagedFunctionArenaBytes - Arena.Num())
        {
            OutError = FString::Printf(
                TEXT("Blueprint text parameter '%s' exceeds the %d-byte managed call arena."),
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
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Text;
        OutValue.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        OutValue.Payload0 = static_cast<uint64>(Offset);
        OutValue.Payload1 = static_cast<uint64>(Utf8.Length());
        return true;
    }
    if (const FObjectPropertyBase* Typed = CastField<FObjectPropertyBase>(&Property))
    {
        UObject* Object = Typed->GetObjectPropertyValue(Value);
        OutValue.Kind = CastField<FClassProperty>(&Property) != nullptr
            ? EUnifiedScriptNativeValueKindV3::Class
            : EUnifiedScriptNativeValueKindV3::Object;
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
    if (const FStructProperty* Typed = CastField<FStructProperty>(&Property);
        Typed != nullptr && Typed->Struct == TBaseStructure<FRotator>::Get())
    {
        const FRotator& Rotator = *static_cast<const FRotator*>(Value);
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Rotator3;
        OutValue.Payload0 = DoubleBits(Rotator.Pitch);
        OutValue.Payload1 = DoubleBits(Rotator.Yaw);
        OutValue.Payload2 = DoubleBits(Rotator.Roll);
        return true;
    }
    if (const FStructProperty* Typed = CastField<FStructProperty>(&Property);
        Typed != nullptr && Typed->Struct == TBaseStructure<FTransform>::Get())
    {
        constexpr int32 TransformByteSize = 10 * sizeof(double);
        if (TransformByteSize > MaximumManagedFunctionArenaBytes - Arena.Num())
        {
            OutError = FString::Printf(
                TEXT("Blueprint transform parameter '%s' exceeds the %d-byte managed call arena."),
                *Property.GetName(),
                MaximumManagedFunctionArenaBytes);
            return false;
        }
        const FTransform& Transform = *static_cast<const FTransform*>(Value);
        const FQuat Rotation = Transform.GetRotation();
        const FVector Translation = Transform.GetTranslation();
        const FVector Scale = Transform.GetScale3D();
        const double Components[10] = {
            Rotation.X, Rotation.Y, Rotation.Z, Rotation.W,
            Translation.X, Translation.Y, Translation.Z,
            Scale.X, Scale.Y, Scale.Z,
        };
        const int32 Offset = Arena.AddUninitialized(TransformByteSize);
        FMemory::Memcpy(Arena.GetData() + Offset, Components, TransformByteSize);
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Transform;
        OutValue.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        OutValue.Payload0 = static_cast<uint64>(Offset);
        OutValue.Payload1 = TransformByteSize;
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
        OutValue.TypeId = UE::UnifiedScript::PropertyValueCodec::TypeIdFromPath(TypePath);
        OutValue.Payload0 = DescriptorOffset;
        OutValue.Payload1 = DescriptorBytes;
        OutValue.Payload2 = FieldNames.Num();
        OutValue.Payload3 = sizeof(FUnifiedScriptNativeValueV3);
        return OutValue.TypeId != 0;
    }
    OutError = FString::Printf(
        TEXT("Blueprint parameter '%s' uses unsupported managed-call property '%s'."),
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
    if (ByteCount > static_cast<uint32>(Arena.Num()) || StructPrefix.Num() > 16)
    {
        OutError = TEXT("Managed function result has invalid arena bounds or nesting.");
        return false;
    }
    const FArrayProperty* ArrayProperty = CastField<FArrayProperty>(&Property);
    const FSetProperty* SetProperty = CastField<FSetProperty>(&Property);
    const FMapProperty* MapProperty = CastField<FMapProperty>(&Property);
    if (ArrayProperty || SetProperty || MapProperty)
    {
        const EUnifiedScriptNativeValueKindV3 Kind = ArrayProperty ? EUnifiedScriptNativeValueKindV3::Array :
            SetProperty ? EUnifiedScriptNativeValueKindV3::Set : EUnifiedScriptNativeValueKindV3::Map;
        const uint64 CellsPerEntry = MapProperty ? 2 : 1;
        if (Value.Kind != Kind || Value.Flags != EUnifiedScriptNativeValueFlagsV3::ArenaBacked ||
            Value.TypeId != UE::UnifiedScript::PropertyValueCodec::TypeIdFromPath(
                UE::UnifiedScript::PropertyValueCodec::ContainerValueNativeName(Property)) ||
            Value.Payload2 > MaximumManagedFunctionArenaBytes / (CellsPerEntry * sizeof(FUnifiedScriptNativeValueV3)) ||
            Value.Payload3 != sizeof(FUnifiedScriptNativeValueV3) ||
            Value.Payload1 != Value.Payload2 * CellsPerEntry * sizeof(FUnifiedScriptNativeValueV3) ||
            Value.Payload0 > ByteCount || Value.Payload1 > ByteCount - Value.Payload0)
        {
            OutError = TEXT("Managed container result has invalid kind, type identity, count, or bounds.");
            return false;
        }
        const int32 Count = static_cast<int32>(Value.Payload2);
        int32 CellIndex = 0;
        auto UnpackCell = [&](const FProperty& ChildProperty, void* ChildValue, const TCHAR* Segment)
        {
            TArray<FName, TInlineAllocator<8>> Prefix;
            Prefix.Append(StructPrefix.GetData(), StructPrefix.Num());
            Prefix.Add(FName(Segment));
            FUnifiedScriptNativeValueV3 Cell;
            FMemory::Memcpy(&Cell, Arena.GetData() + Value.Payload0 + CellIndex++ * sizeof(Cell), sizeof(Cell));
            return UnpackReturnValueV2(ChildProperty, Cell, Arena, ByteCount, Host, TypeBinding, Prefix, ChildValue, OutError);
        };
        if (ArrayProperty)
        {
            FScriptArrayHelper Array(ArrayProperty, Result);
            Array.EmptyAndAddValues(Count);
            for (int32 Index = 0; Index < Count; ++Index)
                if (!UnpackCell(*ArrayProperty->Inner, Array.GetRawPtr(Index), TEXT("$element"))) return false;
        }
        else if (SetProperty)
        {
            FScriptSetHelper Set(SetProperty, Result);
            Set.EmptyElements(Count);
            for (int32 Index = 0; Index < Count; ++Index)
            {
                const int32 Added = Set.AddDefaultValue_Invalid_NeedsRehash();
                if (!UnpackCell(*SetProperty->ElementProp, Set.GetElementPtr(Added), TEXT("$element"))) return false;
            }
            Set.Rehash();
        }
        else
        {
            FScriptMapHelper Map(MapProperty, Result);
            Map.EmptyValues(Count);
            for (int32 Index = 0; Index < Count; ++Index)
            {
                const int32 Added = Map.AddDefaultValue_Invalid_NeedsRehash();
                if (!UnpackCell(*MapProperty->KeyProp, Map.GetKeyPtr(Added), TEXT("$key")) ||
                    !UnpackCell(*MapProperty->ValueProp, Map.GetValuePtr(Added), TEXT("$value"))) return false;
            }
            Map.Rehash();
        }
        return true;
    }
    if (const FSoftObjectProperty* Typed = CastField<FSoftObjectProperty>(&Property))
    {
        if (Value.Kind != EUnifiedScriptNativeValueKindV3::String ||
            Value.Flags != EUnifiedScriptNativeValueFlagsV3::ArenaBacked || Value.TypeId != 0 ||
            Value.Payload0 > ByteCount || Value.Payload1 > ByteCount - Value.Payload0)
        {
            OutError = TEXT("Managed Soft Reference result has an invalid path cell.");
            return false;
        }
        const FUTF8ToTCHAR Converted(reinterpret_cast<const ANSICHAR*>(Arena.GetData() + Value.Payload0),
            static_cast<int32>(Value.Payload1));
        const FSoftObjectPath Path(FString(Converted.Length(), Converted.Get()));
        if (!Path.IsNull() && !Path.IsValid())
        {
            OutError = TEXT("Managed Soft Reference result is not a valid Unreal asset path.");
            return false;
        }
        Typed->SetPropertyValue(Result, FSoftObjectPtr(Path));
        return true;
    }
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
            Value.TypeId != UE::UnifiedScript::PropertyValueCodec::TypeIdFromPath(TypePath) ||
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
    if (const FTextProperty* Typed = CastField<FTextProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Text &&
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
            FText::FromString(FString(Converted.Length(), Converted.Get())));
        return true;
    }
    if (const FObjectPropertyBase* Typed = CastField<FObjectPropertyBase>(&Property);
        Typed != nullptr && Value.Kind ==
            (CastField<FClassProperty>(&Property) != nullptr
                ? EUnifiedScriptNativeValueKindV3::Class
                : EUnifiedScriptNativeValueKindV3::Object))
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
        Typed != nullptr && Typed->Struct == TBaseStructure<FRotator>::Get() &&
        Value.Kind == EUnifiedScriptNativeValueKindV3::Rotator3)
    {
        *static_cast<FRotator*>(Result) = FRotator(
            BitsDouble(Value.Payload0),
            BitsDouble(Value.Payload1),
            BitsDouble(Value.Payload2));
        return true;
    }
    if (const FStructProperty* Typed = CastField<FStructProperty>(&Property);
        Typed != nullptr && Typed->Struct == TBaseStructure<FTransform>::Get() &&
        Value.Kind == EUnifiedScriptNativeValueKindV3::Transform &&
        Value.Flags == EUnifiedScriptNativeValueFlagsV3::ArenaBacked &&
        Value.Payload1 == 10 * sizeof(double) && Value.Payload0 <= ByteCount &&
        Value.Payload1 <= ByteCount - Value.Payload0)
    {
        double Components[10] = {};
        FMemory::Memcpy(
            Components,
            Arena.GetData() + static_cast<int32>(Value.Payload0),
            sizeof(Components));
        *static_cast<FTransform*>(Result) = FTransform(
            FQuat(Components[0], Components[1], Components[2], Components[3]),
            FVector(Components[4], Components[5], Components[6]),
            FVector(Components[7], Components[8], Components[9]));
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
            Value.TypeId != UE::UnifiedScript::PropertyValueCodec::TypeIdFromPath(TypePath) ||
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
        TEXT("Managed return kind %u does not match Blueprint property '%s'."),
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
        Function->FunctionFlags &= ~(FUNC_Net | FUNC_NetServer | FUNC_NetClient | FUNC_NetMulticast | FUNC_NetReliable);
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
        Function->FunctionFlags |= UnifiedScriptRpcFlags(Pair.Value.RpcTarget, Pair.Value.bReliable);
        if (!Pair.Value.RepNotifyProperty.IsNone())
        {
            FProperty* Property = FindFProperty<FProperty>(this, Pair.Value.RepNotifyProperty);
            if (Property && Property->HasAllPropertyFlags(CPF_Net | CPF_RepNotify))
            {
                // UE's Blueprint compiler accepts only zero-parameter OnRep,
                // but RepLayout also supports a native previous-value parameter.
                // The declaration validator has checked that exact property type.
                Property->RepNotifyFunc = Pair.Key;
            }
            else bAllFunctionsBound = false;
        }
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
    if (Binding->RpcTarget != EUnifiedScriptRpcTarget::None &&
        !Function->HasAllFunctionFlags(UnifiedScriptRpcFlags(Binding->RpcTarget, Binding->bReliable))) return false;
    if (!Binding->RepNotifyProperty.IsNone())
    {
        const FProperty* Property = FindFProperty<FProperty>(this, Binding->RepNotifyProperty);
        if (!Property || Property->RepNotifyFunc != FunctionName || !Property->HasAllPropertyFlags(CPF_Net | CPF_RepNotify)) return false;
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
    const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass = Function != nullptr
        ? Cast<UUnifiedScriptBlueprintGeneratedClass>(Function->GetOuter())
        : nullptr;
    const FUnifiedScriptManagedFunctionBinding* Binding = GeneratedClass != nullptr
        ? GeneratedClass->ManagedFunctionBindings.Find(Function->GetFName())
        : nullptr;

    // ProcessEvent supplies an already materialized parameter frame, while a
    // call emitted by the Blueprint VM leaves its arguments in the caller's
    // bytecode stream. A generic dynamic thunk must consume that stream before
    // it can return, including on later dispatch errors, otherwise the caller
    // resumes in the middle of its argument list.
    TUniquePtr<FStructOnScope> DecodedParameters;
    void* ParameterContainer = Stack.Locals;
    TMap<FName, void*> DecodedOutputDestinations;
    if (Function != nullptr && Stack.Node != Function)
    {
        DecodedParameters = MakeUnique<FStructOnScope>(Function);
        ParameterContainer = DecodedParameters->GetStructMemory();
        int32 ManagedParameterIndex = 0;
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
            const bool bInjectedWorldContext = Binding != nullptr && Binding->bStatic &&
                (Parameter->GetFName() == WorldContextObjectName ||
                 Parameter->GetFName() == KismetWorldContextName);
            Stack.Step(
                Stack.Object,
                Parameter->ContainerPtrToValuePtr<void>(ParameterContainer));
            if (!bInjectedWorldContext)
            {
                const int32 BindingParameterIndex = Binding != nullptr &&
                        Binding->ParameterNames.Num() == Binding->ParameterTypes.Num()
                    ? Binding->ParameterNames.IndexOfByKey(Parameter->GetFName())
                    : ManagedParameterIndex;
                const EUnifiedScriptManagedParameterDirection Direction =
                    Binding != nullptr &&
                        Binding->ParameterDirections.IsValidIndex(BindingParameterIndex)
                    ? Binding->ParameterDirections[BindingParameterIndex]
                    : Parameter->HasAnyPropertyFlags(CPF_OutParm)
                        ? EUnifiedScriptManagedParameterDirection::Output
                        : EUnifiedScriptManagedParameterDirection::Input;
                if (Direction != EUnifiedScriptManagedParameterDirection::Input &&
                    Stack.MostRecentPropertyAddress != nullptr)
                {
                    DecodedOutputDestinations.Add(
                        Parameter->GetFName(),
                        Stack.MostRecentPropertyAddress);
                }
                ++ManagedParameterIndex;
            }
        }
        P_FINISH;
    }
    // ProcessEvent copies the caller's parameter block into Stack.Locals and
    // exposes the original out/ref destinations through Stack.OutParms. A
    // Blueprint VM call may also populate this chain with the enclosing
    // frame's destination, so preserve the decoded output net when it is
    // already present and use Stack.OutParms for direct ProcessEvent calls.
    for (FOutParmRec* OutParameter = Stack.OutParms;
         OutParameter != nullptr;
         OutParameter = OutParameter->NextOutParm)
    {
        if (OutParameter->Property != nullptr &&
            OutParameter->PropAddr != nullptr &&
            !OutParameter->Property->HasAnyPropertyFlags(CPF_ReturnParm))
        {
            // During a nested Blueprint VM call, Stack.Step has already
            // captured the caller's temporary output net.  The OutParms chain
            // can point at the enclosing ProcessEvent frame instead; replacing
            // the temporary would make downstream Blueprint nodes observe the
            // pre-call default.  Direct ProcessEvent calls have no earlier net
            // and continue to use the OutParms destination.
            DecodedOutputDestinations.FindOrAdd(
                OutParameter->Property->GetFName(),
                OutParameter->PropAddr);
        }
    }
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

        if (Component && !Binding->RepNotifyProperty.IsNone() &&
            Component->QueueInitialRepNotify(Context, Function, ParameterContainer)) return;

        TArray<FUnifiedScriptNativeValueV3, TInlineAllocator<4>> ArgumentsV2;
        TArray<const FProperty*, TInlineAllocator<4>> ManagedParameters;
        if (Binding->ParameterNames.Num() == Binding->ParameterTypes.Num())
        {
            for (const FName ParameterName : Binding->ParameterNames)
            {
                const FProperty* Parameter = FindFProperty<FProperty>(
                    Function,
                    ParameterName);
                if (Parameter == nullptr ||
                    !Parameter->HasAnyPropertyFlags(CPF_Parm) ||
                    Parameter->HasAnyPropertyFlags(CPF_ReturnParm))
                {
                    UE_LOG(
                        LogTemp,
                        Error,
                        TEXT("UnifiedScript function '%s' cannot resolve managed parameter '%s'."),
                        *Function->GetPathName(),
                        *ParameterName.ToString());
                    return;
                }
                ManagedParameters.Add(Parameter);
            }
        }
        else
        {
            for (TFieldIterator<FProperty> It(
                     Function,
                     EFieldIteratorFlags::ExcludeSuper);
                 It;
                 ++It)
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
                ManagedParameters.Add(Parameter);
            }
        }
        if (ManagedParameters.Num() != Binding->ParameterTypes.Num())
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UnifiedScript function '%s' resolved %d managed parameters; expected %d."),
                *Function->GetPathName(),
                ManagedParameters.Num(),
                Binding->ParameterTypes.Num());
            return;
        }
        TArray<uint8, TInlineAllocator<512>> Arena;
        Arena.Reserve(MaximumManagedFunctionArenaBytes);
        FString MarshalError;
        for (int32 ManagedParameterIndex = 0;
             ManagedParameterIndex < ManagedParameters.Num();
             ++ManagedParameterIndex)
        {
            const FProperty* Parameter = ManagedParameters[ManagedParameterIndex];
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
        }
        const uint32 InputByteCount = static_cast<uint32>(Arena.Num());
        Arena.SetNumUninitialized(MaximumManagedFunctionArenaBytes, EAllowShrinking::No);
        uint32 ExpectedResultCount = ReturnProperty != nullptr ? 1u : 0u;
        for (EUnifiedScriptManagedParameterDirection Direction :
             Binding->ParameterDirections)
        {
            if (Direction != EUnifiedScriptManagedParameterDirection::Input)
            {
                ++ExpectedResultCount;
            }
        }
        TArray<FUnifiedScriptNativeValueV3, TInlineAllocator<5>> ResultsV2;
        ResultsV2.SetNumZeroed(ExpectedResultCount);
        FUnifiedScriptNativeCallFrameV3 Frame;
        Frame.Arguments = ArgumentsV2.GetData();
        Frame.ArgumentCount = ArgumentsV2.Num();
        Frame.Results = ResultsV2.GetData();
        Frame.ResultCapacity = ResultsV2.Num();
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
                TEXT("UnifiedScript function '%s' ABI v%d dispatch failed (status=%u): %s"),
                *Function->GetPathName(),
                Binding->AbiVersion,
                static_cast<uint32>(Frame.Status),
                *Error);
            return;
        }
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
        // Decode every output into owned storage before changing caller memory.
        // A malformed element or stale ObjectHandle must not partially overwrite
        // a ref container or publish a return value before later outputs fail.
        FStructOnScope DecodedResults(Function);
        void* ResultContainer = DecodedResults.GetStructMemory();
        int32 ResultIndex = 0;
        if (ReturnProperty != nullptr &&
            (RESULT_PARAM == nullptr ||
             !UnpackReturnValueV2(
                 *ReturnProperty,
                 ResultsV2[ResultIndex++],
                 Arena,
                  Frame.ByteCount,
                  *Host,
                  &Binding->ReturnType,
                  {},
                  ReturnProperty->ContainerPtrToValuePtr<void>(ResultContainer),
                 Error)))
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UnifiedScript cannot marshal ABI v%d return value for '%s': %s"),
                Binding->AbiVersion,
                *Function->GetPathName(),
                *Error);
            return;
        }
        for (int32 OutputParameterIndex = 0;
             OutputParameterIndex < ManagedParameters.Num();
             ++OutputParameterIndex)
        {
            const FProperty* Parameter = ManagedParameters[OutputParameterIndex];
            const EUnifiedScriptManagedParameterDirection Direction =
                Binding->ParameterDirections.IsValidIndex(OutputParameterIndex)
                ? Binding->ParameterDirections[OutputParameterIndex]
                : EUnifiedScriptManagedParameterDirection::Input;
            if (Direction != EUnifiedScriptManagedParameterDirection::Input)
            {
                void* ParameterValue =
                    Parameter->ContainerPtrToValuePtr<void>(ResultContainer);
                const FUnifiedScriptManagedValueTypeBinding* ParameterType =
                    Binding->ParameterTypes.IsValidIndex(OutputParameterIndex)
                    ? &Binding->ParameterTypes[OutputParameterIndex]
                    : nullptr;
                if (!ResultsV2.IsValidIndex(ResultIndex) ||
                    !UnpackReturnValueV2(
                        *Parameter,
                        ResultsV2[ResultIndex++],
                        Arena,
                        Frame.ByteCount,
                        *Host,
                        ParameterType,
                        {},
                        ParameterValue,
                        Error))
                {
                    UE_LOG(
                        LogTemp,
                        Error,
                        TEXT("UnifiedScript cannot marshal ABI v3 output '%s' for '%s': %s"),
                        *Parameter->GetName(),
                        *Function->GetPathName(),
                        *Error);
                    return;
                }
            }
        }
        if (ReturnProperty != nullptr)
            ReturnProperty->CopyCompleteValue(RESULT_PARAM,
                ReturnProperty->ContainerPtrToValuePtr<void>(ResultContainer));
        for (int32 Index = 0; Index < ManagedParameters.Num(); ++Index)
        {
            const EUnifiedScriptManagedParameterDirection Direction =
                Binding->ParameterDirections.IsValidIndex(Index)
                    ? Binding->ParameterDirections[Index] : EUnifiedScriptManagedParameterDirection::Input;
            if (Direction != EUnifiedScriptManagedParameterDirection::Input)
            {
                const FProperty* Parameter = ManagedParameters[Index];
                const void* ParameterValue = Parameter->ContainerPtrToValuePtr<void>(ResultContainer);
                if (void** Destination =
                        DecodedOutputDestinations.Find(Parameter->GetFName());
                    Destination != nullptr && *Destination != nullptr)
                {
                    Parameter->CopyCompleteValue(*Destination, ParameterValue);
                }
                else
                {
                    Parameter->CopyCompleteValue(
                        Parameter->ContainerPtrToValuePtr<void>(ParameterContainer), ParameterValue);
                }
            }
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

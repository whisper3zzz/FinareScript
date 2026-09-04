#include "UnifiedScriptInvocationBridge.h"

#include "Containers/StringConv.h"
#include "UnifiedScriptGeneratedThunkRegistry.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptBlueprint.h"
#include "GameFramework/Actor.h"
#include "Components/ActorComponent.h"
#include "Net/Core/PushModel/PushModel.h"
#include "UObject/Class.h"
#include "UObject/EnumProperty.h"
#include "UObject/StrProperty.h"
#include "UObject/StructOnScope.h"
#include "UObject/TextProperty.h"
#include "UObject/UObjectGlobals.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::Invocation::Private
{
AActor* NetworkOwner(UObject* Object)
{
    if (AActor* Actor = Cast<AActor>(Object)) return Actor;
    if (UActorComponent* Component = Cast<UActorComponent>(Object)) return Component->GetOwner();
    return nullptr;
}

void Succeed(FUnifiedScriptInvocationResult& OutResult)
{
    OutResult.Error = EUnifiedScriptInvocationError::None;
    OutResult.HandleError = EUnifiedScriptObjectHandleError::None;
    OutResult.Message.Reset();
}

void Fail(
    FUnifiedScriptInvocationResult& OutResult,
    EUnifiedScriptInvocationError Error,
    FString Message)
{
    OutResult.Error = Error;
    OutResult.HandleError = EUnifiedScriptObjectHandleError::None;
    OutResult.Message = MoveTemp(Message);
}

UObject* ResolveTarget(
    FUnifiedScriptObjectRegistry& Registry,
    FUnifiedScriptObjectHandle Target,
    FUnifiedScriptInvocationResult& OutResult)
{
    FUnifiedScriptObjectHandleResult HandleResult;
    UObject* Object = Registry.ResolveObject(Target, HandleResult);
    if (Object != nullptr)
    {
        return Object;
    }

    OutResult.Error = EUnifiedScriptInvocationError::ObjectHandleFailure;
    OutResult.HandleError = HandleResult.Error;
    OutResult.Message = MoveTemp(HandleResult.Message);
    return nullptr;
}

bool ResolveMemberIdentity(
    UObject* Target,
    const FString& MemberPath,
    UClass*& OutTargetClass,
    FName& OutMemberName,
    FUnifiedScriptInvocationResult& OutResult)
{
    int32 SeparatorIndex = INDEX_NONE;
    if (!MemberPath.FindLastChar(TEXT(':'), SeparatorIndex) ||
        SeparatorIndex <= 0 ||
        SeparatorIndex == MemberPath.Len() - 1)
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::InvalidMemberPath,
            FString::Printf(TEXT("Invalid UnifiedScript member path '%s'."), *MemberPath));
        return false;
    }

    const FString ClassPath = MemberPath.Left(SeparatorIndex);
    const FString MemberName = MemberPath.Mid(SeparatorIndex + 1);
    UClass* TargetClass = FindObject<UClass>(nullptr, *ClassPath);
    if (TargetClass == nullptr)
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::TargetTypeNotFound,
            FString::Printf(TEXT("UnifiedScript target type '%s' is not loaded."), *ClassPath));
        return false;
    }
    if (!Target->IsA(TargetClass))
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::TargetTypeMismatch,
            FString::Printf(
                TEXT("Object '%s' is not an instance of '%s'."),
                *Target->GetPathName(),
                *ClassPath));
        return false;
    }

    OutTargetClass = TargetClass;
    OutMemberName = FName(*MemberName);
    return true;
}

bool ReadValue(
    FUnifiedScriptObjectRegistry& Registry,
    const FProperty* Property,
    const void* Container,
    FUnifiedScriptValue& OutValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    if (const FBoolProperty* BoolProperty = CastField<FBoolProperty>(Property))
    {
        OutValue = FUnifiedScriptValue::FromBoolean(
            BoolProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FIntProperty* IntProperty = CastField<FIntProperty>(Property))
    {
        OutValue = FUnifiedScriptValue::FromInt32(
            IntProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FInt8Property* TypedProperty = CastField<FInt8Property>(Property))
    {
        OutValue = FUnifiedScriptValue::FromInt8(
            TypedProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FByteProperty* TypedProperty = CastField<FByteProperty>(Property))
    {
        const uint8 Value = TypedProperty->GetPropertyValue_InContainer(Container);
        OutValue = TypedProperty->Enum != nullptr
            ? FUnifiedScriptValue::FromEnum(Value, FName(*TypedProperty->Enum->GetPathName()))
            : FUnifiedScriptValue::FromUInt8(Value);
        return true;
    }
    if (const FInt16Property* TypedProperty = CastField<FInt16Property>(Property))
    {
        OutValue = FUnifiedScriptValue::FromInt16(
            TypedProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FUInt16Property* TypedProperty = CastField<FUInt16Property>(Property))
    {
        OutValue = FUnifiedScriptValue::FromUInt16(
            TypedProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FUInt32Property* TypedProperty = CastField<FUInt32Property>(Property))
    {
        OutValue = FUnifiedScriptValue::FromUInt32(
            TypedProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FInt64Property* TypedProperty = CastField<FInt64Property>(Property))
    {
        OutValue = FUnifiedScriptValue::FromInt64(
            TypedProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FUInt64Property* TypedProperty = CastField<FUInt64Property>(Property))
    {
        OutValue = FUnifiedScriptValue::FromUInt64(
            TypedProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FFloatProperty* TypedProperty = CastField<FFloatProperty>(Property))
    {
        OutValue = FUnifiedScriptValue::FromFloat32(
            TypedProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FDoubleProperty* TypedProperty = CastField<FDoubleProperty>(Property))
    {
        OutValue = FUnifiedScriptValue::FromFloat64(
            TypedProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FStrProperty* StringProperty = CastField<FStrProperty>(Property))
    {
        OutValue = FUnifiedScriptValue::FromString(
            StringProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FNameProperty* TypedProperty = CastField<FNameProperty>(Property))
    {
        OutValue = FUnifiedScriptValue::FromName(
            TypedProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FTextProperty* TypedProperty = CastField<FTextProperty>(Property))
    {
        OutValue = FUnifiedScriptValue::FromText(
            TypedProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FEnumProperty* TypedProperty = CastField<FEnumProperty>(Property))
    {
        const void* ValueAddress = TypedProperty->ContainerPtrToValuePtr<void>(Container);
        OutValue = FUnifiedScriptValue::FromEnum(
            TypedProperty->GetUnderlyingProperty()->GetSignedIntPropertyValue(ValueAddress),
            FName(*TypedProperty->GetEnum()->GetPathName()));
        return true;
    }
    if (const FStructProperty* TypedProperty = CastField<FStructProperty>(Property))
    {
        if (TypedProperty->Struct == TBaseStructure<FVector>::Get())
        {
            OutValue = FUnifiedScriptValue::FromVector(
                *TypedProperty->ContainerPtrToValuePtr<FVector>(Container));
            return true;
        }
        if (TypedProperty->Struct == TBaseStructure<FRotator>::Get())
        {
            OutValue = FUnifiedScriptValue::FromRotator(
                *TypedProperty->ContainerPtrToValuePtr<FRotator>(Container));
            return true;
        }
        if (TypedProperty->Struct == TBaseStructure<FTransform>::Get())
        {
            OutValue = FUnifiedScriptValue::FromTransform(
                *TypedProperty->ContainerPtrToValuePtr<FTransform>(Container));
            return true;
        }
    }
    if (const FClassProperty* TypedProperty = CastField<FClassProperty>(Property))
    {
        UClass* Object = Cast<UClass>(TypedProperty->GetObjectPropertyValue_InContainer(Container));
        if (Object == nullptr)
        {
            OutValue = FUnifiedScriptValue::FromObjectHandle({}, true);
            return true;
        }
        FUnifiedScriptObjectHandleResult HandleResult;
        const FUnifiedScriptObjectHandle Handle = Registry.RegisterObject(Object, HandleResult);
        if (!HandleResult.IsSuccess())
        {
            OutResult.Error = EUnifiedScriptInvocationError::ObjectHandleFailure;
            OutResult.HandleError = HandleResult.Error;
            OutResult.Message = MoveTemp(HandleResult.Message);
            return false;
        }
        OutValue = FUnifiedScriptValue::FromObjectHandle(Handle, true);
        return true;
    }
    if (const FObjectPropertyBase* TypedProperty = CastField<FObjectPropertyBase>(Property))
    {
        UObject* Object = TypedProperty->GetObjectPropertyValue_InContainer(Container);
        if (Object == nullptr)
        {
            OutValue = FUnifiedScriptValue::FromObjectHandle({});
            return true;
        }
        FUnifiedScriptObjectHandleResult HandleResult;
        const FUnifiedScriptObjectHandle Handle = Registry.RegisterObject(Object, HandleResult);
        if (!HandleResult.IsSuccess())
        {
            OutResult.Error = EUnifiedScriptInvocationError::ObjectHandleFailure;
            OutResult.HandleError = HandleResult.Error;
            OutResult.Message = MoveTemp(HandleResult.Message);
            return false;
        }
        OutValue = FUnifiedScriptValue::FromObjectHandle(Handle);
        return true;
    }

    Fail(
        OutResult,
        EUnifiedScriptInvocationError::UnsupportedValueType,
        FString::Printf(
            TEXT("Property '%s' has unsupported native type '%s'."),
            *Property->GetName(),
            *Property->GetCPPType()));
    return false;
}

bool WriteValue(
    FUnifiedScriptObjectRegistry& Registry,
    FProperty* Property,
    void* Container,
    const FUnifiedScriptValue& Value,
    FUnifiedScriptInvocationResult& OutResult)
{
    if (FBoolProperty* BoolProperty = CastField<FBoolProperty>(Property))
    {
        bool NativeValue = false;
        if (!Value.TryGetBoolean(NativeValue))
        {
            Fail(
                OutResult,
                EUnifiedScriptInvocationError::ValueTypeMismatch,
                FString::Printf(TEXT("Property '%s' requires a boolean value."), *Property->GetName()));
            return false;
        }
        BoolProperty->SetPropertyValue_InContainer(Container, NativeValue);
        return true;
    }
    if (FIntProperty* IntProperty = CastField<FIntProperty>(Property))
    {
        int32 NativeValue = 0;
        if (!Value.TryGetInt32(NativeValue))
        {
            Fail(
                OutResult,
                EUnifiedScriptInvocationError::ValueTypeMismatch,
                FString::Printf(TEXT("Property '%s' requires an int32 value."), *Property->GetName()));
            return false;
        }
        IntProperty->SetPropertyValue_InContainer(Container, NativeValue);
        return true;
    }
    if (FInt8Property* TypedProperty = CastField<FInt8Property>(Property))
    {
        int8 NativeValue = 0;
        if (Value.TryGetInt8(NativeValue))
        {
            TypedProperty->SetPropertyValue_InContainer(Container, NativeValue);
            return true;
        }
    }
    if (FByteProperty* TypedProperty = CastField<FByteProperty>(Property))
    {
        uint8 NativeValue = 0;
        const bool bValid = TypedProperty->Enum != nullptr
            ? [&]
            {
                int64 EnumValue = 0;
                if (!Value.TryGetEnum(EnumValue, FName(*TypedProperty->Enum->GetPathName())) ||
                    EnumValue < 0 || EnumValue > MAX_uint8)
                {
                    return false;
                }
                NativeValue = static_cast<uint8>(EnumValue);
                return true;
            }()
            : Value.TryGetUInt8(NativeValue);
        if (bValid)
        {
            TypedProperty->SetPropertyValue_InContainer(Container, NativeValue);
            return true;
        }
    }
    if (FInt16Property* TypedProperty = CastField<FInt16Property>(Property))
    {
        int16 NativeValue = 0;
        if (Value.TryGetInt16(NativeValue))
        {
            TypedProperty->SetPropertyValue_InContainer(Container, NativeValue);
            return true;
        }
    }
    if (FUInt16Property* TypedProperty = CastField<FUInt16Property>(Property))
    {
        uint16 NativeValue = 0;
        if (Value.TryGetUInt16(NativeValue))
        {
            TypedProperty->SetPropertyValue_InContainer(Container, NativeValue);
            return true;
        }
    }
    if (FUInt32Property* TypedProperty = CastField<FUInt32Property>(Property))
    {
        uint32 NativeValue = 0;
        if (Value.TryGetUInt32(NativeValue))
        {
            TypedProperty->SetPropertyValue_InContainer(Container, NativeValue);
            return true;
        }
    }
    if (FInt64Property* TypedProperty = CastField<FInt64Property>(Property))
    {
        int64 NativeValue = 0;
        if (Value.TryGetInt64(NativeValue))
        {
            TypedProperty->SetPropertyValue_InContainer(Container, NativeValue);
            return true;
        }
    }
    if (FUInt64Property* TypedProperty = CastField<FUInt64Property>(Property))
    {
        uint64 NativeValue = 0;
        if (Value.TryGetUInt64(NativeValue))
        {
            TypedProperty->SetPropertyValue_InContainer(Container, NativeValue);
            return true;
        }
    }
    if (FFloatProperty* TypedProperty = CastField<FFloatProperty>(Property))
    {
        float NativeValue = 0.0f;
        if (Value.TryGetFloat32(NativeValue))
        {
            TypedProperty->SetPropertyValue_InContainer(Container, NativeValue);
            return true;
        }
    }
    if (FDoubleProperty* TypedProperty = CastField<FDoubleProperty>(Property))
    {
        double NativeValue = 0.0;
        if (Value.TryGetFloat64(NativeValue))
        {
            TypedProperty->SetPropertyValue_InContainer(Container, NativeValue);
            return true;
        }
    }
    if (FStrProperty* StringProperty = CastField<FStrProperty>(Property))
    {
        FString NativeValue;
        if (!Value.TryGetString(NativeValue))
        {
            Fail(
                OutResult,
                EUnifiedScriptInvocationError::ValueTypeMismatch,
                FString::Printf(TEXT("Property '%s' requires a string value."), *Property->GetName()));
            return false;
        }
        StringProperty->SetPropertyValue_InContainer(Container, NativeValue);
        return true;
    }
    if (FNameProperty* TypedProperty = CastField<FNameProperty>(Property))
    {
        FName NativeValue;
        if (Value.TryGetName(NativeValue))
        {
            TypedProperty->SetPropertyValue_InContainer(Container, NativeValue);
            return true;
        }
    }
    if (FTextProperty* TypedProperty = CastField<FTextProperty>(Property))
    {
        FText NativeValue;
        if (Value.TryGetText(NativeValue))
        {
            TypedProperty->SetPropertyValue_InContainer(Container, NativeValue);
            return true;
        }
    }
    if (FEnumProperty* TypedProperty = CastField<FEnumProperty>(Property))
    {
        int64 NativeValue = 0;
        if (Value.TryGetEnum(NativeValue, FName(*TypedProperty->GetEnum()->GetPathName())))
        {
            void* ValueAddress = TypedProperty->ContainerPtrToValuePtr<void>(Container);
            TypedProperty->GetUnderlyingProperty()->SetIntPropertyValue(ValueAddress, NativeValue);
            return true;
        }
    }
    if (FStructProperty* TypedProperty = CastField<FStructProperty>(Property))
    {
        if (TypedProperty->Struct == TBaseStructure<FVector>::Get())
        {
            FVector NativeValue;
            if (Value.TryGetVector(NativeValue))
            {
                *TypedProperty->ContainerPtrToValuePtr<FVector>(Container) = NativeValue;
                return true;
            }
        }
        else if (TypedProperty->Struct == TBaseStructure<FRotator>::Get())
        {
            FRotator NativeValue;
            if (Value.TryGetRotator(NativeValue))
            {
                *TypedProperty->ContainerPtrToValuePtr<FRotator>(Container) = NativeValue;
                return true;
            }
        }
        else if (TypedProperty->Struct == TBaseStructure<FTransform>::Get())
        {
            FTransform NativeValue;
            if (Value.TryGetTransform(NativeValue))
            {
                *TypedProperty->ContainerPtrToValuePtr<FTransform>(Container) = NativeValue;
                return true;
            }
        }
    }
    if (FObjectPropertyBase* TypedProperty = CastField<FObjectPropertyBase>(Property))
    {
        const bool bClass = CastField<FClassProperty>(TypedProperty) != nullptr;
        FUnifiedScriptObjectHandle Handle;
        if (Value.TryGetObjectHandle(Handle, bClass))
        {
            UObject* Object = nullptr;
            if (!Handle.IsNull())
            {
                FUnifiedScriptObjectHandleResult HandleResult;
                Object = Registry.ResolveObject(Handle, HandleResult);
                if (!HandleResult.IsSuccess())
                {
                    OutResult.Error = EUnifiedScriptInvocationError::ObjectHandleFailure;
                    OutResult.HandleError = HandleResult.Error;
                    OutResult.Message = MoveTemp(HandleResult.Message);
                    return false;
                }
            }
            if (const FClassProperty* ClassProperty = CastField<FClassProperty>(TypedProperty))
            {
                const UClass* Class = Cast<UClass>(Object);
                if (Object != nullptr &&
                    (Class == nullptr || !Class->IsChildOf(ClassProperty->MetaClass)))
                {
                    Object = nullptr;
                }
            }
            else if (Object != nullptr && !Object->IsA(TypedProperty->PropertyClass))
            {
                Object = nullptr;
            }
            if (Object != nullptr || Handle.IsNull())
            {
                TypedProperty->SetObjectPropertyValue_InContainer(Container, Object);
                return true;
            }
        }
    }

    const FStructProperty* StructProperty = CastField<FStructProperty>(Property);
    const bool bSupportedStruct = StructProperty != nullptr &&
        (StructProperty->Struct == TBaseStructure<FVector>::Get() ||
         StructProperty->Struct == TBaseStructure<FRotator>::Get() ||
         StructProperty->Struct == TBaseStructure<FTransform>::Get());
    if (CastField<FNumericProperty>(Property) != nullptr ||
        CastField<FEnumProperty>(Property) != nullptr ||
        CastField<FNameProperty>(Property) != nullptr ||
        CastField<FTextProperty>(Property) != nullptr ||
        CastField<FObjectPropertyBase>(Property) != nullptr ||
        bSupportedStruct)
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::ValueTypeMismatch,
            FString::Printf(
                TEXT("Property '%s' received an incompatible UnifiedScript value."),
                *Property->GetName()));
        return false;
    }

    Fail(
        OutResult,
        EUnifiedScriptInvocationError::UnsupportedValueType,
        FString::Printf(
            TEXT("Property '%s' has unsupported native type '%s'."),
            *Property->GetName(),
            *Property->GetCPPType()));
    return false;
}
}

FUnifiedScriptValue FUnifiedScriptValue::FromBoolean(bool Value)
{
    FUnifiedScriptValue Result;
    Result.Kind = EUnifiedScriptValueKind::Boolean;
    Result.BooleanValue = Value;
    return Result;
}

FUnifiedScriptValue FUnifiedScriptValue::FromInt32(int32 Value)
{
    FUnifiedScriptValue Result;
    Result.Kind = EUnifiedScriptValueKind::Int32;
    Result.Int32Value = Value;
    return Result;
}

FUnifiedScriptValue FUnifiedScriptValue::FromString(FString Value)
{
    FUnifiedScriptValue Result;
    Result.Kind = EUnifiedScriptValueKind::String;
    Result.StringValue = MoveTemp(Value);
    return Result;
}

#define UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(Name, KindName, Type, Field) \
    FUnifiedScriptValue FUnifiedScriptValue::From##Name(Type Value)         \
    {                                                                      \
        FUnifiedScriptValue Result;                                        \
        Result.Kind = EUnifiedScriptValueKind::KindName;                   \
        Result.Field = Value;                                              \
        return Result;                                                     \
    }

UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(Int8, Int8, int8, Int8Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(UInt8, UInt8, uint8, UInt8Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(Int16, Int16, int16, Int16Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(UInt16, UInt16, uint16, UInt16Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(UInt32, UInt32, uint32, UInt32Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(Int64, Int64, int64, Int64Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(UInt64, UInt64, uint64, UInt64Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(Float32, Float32, float, Float32Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(Float64, Float64, double, Float64Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(Name, Name, FName, NameValue)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(Text, Text, FText, TextValue)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(Vector, Vector, FVector, VectorValue)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(Rotator, Rotator, FRotator, RotatorValue)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY(Transform, Transform, FTransform, TransformValue)

#undef UE_UNIFIEDSCRIPT_DEFINE_VALUE_FACTORY

FUnifiedScriptValue FUnifiedScriptValue::FromObjectHandle(
    FUnifiedScriptObjectHandle Value,
    bool bClass)
{
    FUnifiedScriptValue Result;
    Result.Kind = bClass ? EUnifiedScriptValueKind::Class : EUnifiedScriptValueKind::Object;
    Result.ObjectHandleValue = Value;
    return Result;
}

FUnifiedScriptValue FUnifiedScriptValue::FromEnum(int64 Value, FName InTypePath)
{
    FUnifiedScriptValue Result;
    Result.Kind = EUnifiedScriptValueKind::Enum;
    Result.Int64Value = Value;
    Result.TypePath = InTypePath;
    if (!InTypePath.IsNone())
    {
        const FString TypePathString = InTypePath.ToString();
        const FTCHARToUTF8 TypePathUtf8(*TypePathString);
        Result.TypeIdValue = UnifiedScriptMemberId(TypePathUtf8.Get());
    }
    return Result;
}

FUnifiedScriptValue FUnifiedScriptValue::FromEnumTypeId(int64 Value, uint64 TypeId)
{
    FUnifiedScriptValue Result;
    Result.Kind = EUnifiedScriptValueKind::Enum;
    Result.Int64Value = Value;
    Result.TypeIdValue = TypeId;
    return Result;
}

namespace
{
FUnifiedScriptValue FromContainer(
    EUnifiedScriptValueKind Kind,
    TArray<FUnifiedScriptValue> Values,
    uint64 TypeId)
{
    FUnifiedScriptValue Result;
    Result.Kind = Kind;
    Result.TypeIdValue = TypeId;
    Result.ContainerValues = MoveTemp(Values);
    return Result;
}
}

FUnifiedScriptValue FUnifiedScriptValue::FromArray(
    TArray<FUnifiedScriptValue> Values,
    uint64 TypeId)
{
    return FromContainer(EUnifiedScriptValueKind::Array, MoveTemp(Values), TypeId);
}

FUnifiedScriptValue FUnifiedScriptValue::FromStruct(
    TArray<FUnifiedScriptValue> Fields,
    uint64 TypeId)
{
    return FromContainer(EUnifiedScriptValueKind::Struct, MoveTemp(Fields), TypeId);
}

FUnifiedScriptValue FUnifiedScriptValue::FromSet(
    TArray<FUnifiedScriptValue> Values,
    uint64 TypeId)
{
    return FromContainer(EUnifiedScriptValueKind::Set, MoveTemp(Values), TypeId);
}

FUnifiedScriptValue FUnifiedScriptValue::FromMap(
    TArray<FUnifiedScriptValue> FlattenedEntries,
    uint64 TypeId)
{
    return FromContainer(EUnifiedScriptValueKind::Map, MoveTemp(FlattenedEntries), TypeId);
}

bool FUnifiedScriptValue::TryGetBoolean(bool& OutValue) const
{
    if (Kind != EUnifiedScriptValueKind::Boolean)
    {
        return false;
    }
    OutValue = BooleanValue;
    return true;
}

bool FUnifiedScriptValue::TryGetInt32(int32& OutValue) const
{
    if (Kind != EUnifiedScriptValueKind::Int32)
    {
        return false;
    }
    OutValue = Int32Value;
    return true;
}

bool FUnifiedScriptValue::TryGetString(FString& OutValue) const
{
    if (Kind != EUnifiedScriptValueKind::String)
    {
        return false;
    }
    OutValue = StringValue;
    return true;
}

#define UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(Name, KindName, Type, Field) \
    bool FUnifiedScriptValue::TryGet##Name(Type& OutValue) const           \
    {                                                                     \
        if (Kind != EUnifiedScriptValueKind::KindName)                    \
        {                                                                 \
            return false;                                                 \
        }                                                                 \
        OutValue = Field;                                                 \
        return true;                                                      \
    }

UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(Int8, Int8, int8, Int8Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(UInt8, UInt8, uint8, UInt8Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(Int16, Int16, int16, Int16Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(UInt16, UInt16, uint16, UInt16Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(UInt32, UInt32, uint32, UInt32Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(Int64, Int64, int64, Int64Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(UInt64, UInt64, uint64, UInt64Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(Float32, Float32, float, Float32Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(Float64, Float64, double, Float64Value)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(Name, Name, FName, NameValue)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(Text, Text, FText, TextValue)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(Vector, Vector, FVector, VectorValue)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(Rotator, Rotator, FRotator, RotatorValue)
UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER(Transform, Transform, FTransform, TransformValue)

#undef UE_UNIFIEDSCRIPT_DEFINE_VALUE_READER

bool FUnifiedScriptValue::TryGetObjectHandle(
    FUnifiedScriptObjectHandle& OutValue,
    bool bClass) const
{
    const EUnifiedScriptValueKind ExpectedKind =
        bClass ? EUnifiedScriptValueKind::Class : EUnifiedScriptValueKind::Object;
    if (Kind != ExpectedKind)
    {
        return false;
    }
    OutValue = ObjectHandleValue;
    return true;
}

bool FUnifiedScriptValue::TryGetEnum(int64& OutValue, FName ExpectedTypePath) const
{
    if (Kind != EUnifiedScriptValueKind::Enum)
    {
        return false;
    }
    if (TypePath.IsNone())
    {
        const FTCHARToUTF8 ExpectedPath(*ExpectedTypePath.ToString());
        if (TypeIdValue == 0 || TypeIdValue != UnifiedScriptMemberId(ExpectedPath.Get())) return false;
    }
    else if (TypePath != ExpectedTypePath) return false;
    OutValue = Int64Value;
    return true;
}

bool FUnifiedScriptValue::TryGetEnumById(int64& OutValue, uint64 ExpectedTypeId) const
{
    if (Kind != EUnifiedScriptValueKind::Enum ||
        TypeIdValue == 0 ||
        TypeIdValue != ExpectedTypeId)
    {
        return false;
    }
    OutValue = Int64Value;
    return true;
}

namespace
{
bool TryGetContainer(
    const FUnifiedScriptValue& Value,
    EUnifiedScriptValueKind ExpectedKind,
    uint64 ExpectedTypeId,
    bool bRequireEvenCount,
    TConstArrayView<FUnifiedScriptValue>& OutValues)
{
    if (Value.Kind != ExpectedKind ||
        ExpectedTypeId == 0 ||
        Value.TypeIdValue != ExpectedTypeId ||
        (bRequireEvenCount && Value.ContainerValues.Num() % 2 != 0))
    {
        return false;
    }
    OutValues = Value.ContainerValues;
    return true;
}
}

bool FUnifiedScriptValue::TryGetArray(
    uint64 ExpectedTypeId,
    TConstArrayView<FUnifiedScriptValue>& OutValues) const
{
    return TryGetContainer(*this, EUnifiedScriptValueKind::Array, ExpectedTypeId, false, OutValues);
}

bool FUnifiedScriptValue::TryGetStruct(
    uint64 ExpectedTypeId,
    TConstArrayView<FUnifiedScriptValue>& OutFields) const
{
    return TryGetContainer(*this, EUnifiedScriptValueKind::Struct, ExpectedTypeId, false, OutFields);
}

bool FUnifiedScriptValue::TryGetSet(
    uint64 ExpectedTypeId,
    TConstArrayView<FUnifiedScriptValue>& OutValues) const
{
    return TryGetContainer(*this, EUnifiedScriptValueKind::Set, ExpectedTypeId, false, OutValues);
}

bool FUnifiedScriptValue::TryGetMap(
    uint64 ExpectedTypeId,
    TConstArrayView<FUnifiedScriptValue>& OutFlattenedEntries) const
{
    return TryGetContainer(
        *this,
        EUnifiedScriptValueKind::Map,
        ExpectedTypeId,
        true,
        OutFlattenedEntries);
}

FUnifiedScriptInvocationBridge::FUnifiedScriptInvocationBridge(
    FUnifiedScriptObjectRegistry& InRegistry)
    : Registry(InRegistry)
{
}

bool FUnifiedScriptInvocationBridge::ReadProperty(
    FUnifiedScriptObjectHandle Target,
    const FString& PropertyPath,
    FUnifiedScriptValue& OutValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;

    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr)
    {
        return false;
    }

    UClass* TargetClass = nullptr;
    FName PropertyName;
    if (!ResolveMemberIdentity(Object, PropertyPath, TargetClass, PropertyName, OutResult))
    {
        return false;
    }

    FProperty* Property = FindFProperty<FProperty>(TargetClass, PropertyName);
    if (Property == nullptr)
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::PropertyNotFound,
            FString::Printf(TEXT("Property '%s' was not found."), *PropertyPath));
        return false;
    }
    if (!Property->HasAnyPropertyFlags(CPF_BlueprintVisible))
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::PropertyNotReadable,
            FString::Printf(TEXT("Property '%s' is not Blueprint-readable."), *PropertyPath));
        return false;
    }
    if (!ReadValue(Registry, Property, Object, OutValue, OutResult))
    {
        return false;
    }

    Succeed(OutResult);
    return true;
}

bool FUnifiedScriptInvocationBridge::WriteProperty(
    FUnifiedScriptObjectHandle Target,
    const FString& PropertyPath,
    const FUnifiedScriptValue& Value,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;

    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr)
    {
        return false;
    }

    UClass* TargetClass = nullptr;
    FName PropertyName;
    if (!ResolveMemberIdentity(Object, PropertyPath, TargetClass, PropertyName, OutResult))
    {
        return false;
    }

    FProperty* Property = FindFProperty<FProperty>(TargetClass, PropertyName);
    if (Property == nullptr)
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::PropertyNotFound,
            FString::Printf(TEXT("Property '%s' was not found."), *PropertyPath));
        return false;
    }
    if (!Property->HasAnyPropertyFlags(CPF_BlueprintVisible) ||
        Property->HasAnyPropertyFlags(CPF_BlueprintReadOnly))
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::PropertyNotWritable,
            FString::Printf(TEXT("Property '%s' is not Blueprint-writable."), *PropertyPath));
        return false;
    }
    if (Property->HasAnyPropertyFlags(CPF_Net) && Cast<UUnifiedScriptBlueprintGeneratedClass>(Property->GetOwnerClass()) != nullptr)
    {
        AActor* Owner = NetworkOwner(Object);
        if (Owner == nullptr || !Owner->HasAuthority())
        {
            Fail(OutResult, EUnifiedScriptInvocationError::PropertyNotWritable,
                TEXT("Replicated Script Class state can only be written by authority; send a Server RPC instead."));
            return false;
        }
    }
    if (!WriteValue(Registry, Property, Object, Value, OutResult))
    {
        return false;
    }
    // Generated state writes do not execute a K2 SetVariable node. Notify the
    // standard replication dirty tracker as that node would, including Iris.
    MARK_PROPERTY_DIRTY(Object, Property);

    Succeed(OutResult);
    return true;
}

bool FUnifiedScriptInvocationBridge::Invoke(
    FUnifiedScriptObjectHandle Target,
    const FString& FunctionPath,
    TConstArrayView<FUnifiedScriptValue> Arguments,
    FUnifiedScriptValue& OutReturnValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;

    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr)
    {
        return false;
    }

    UClass* TargetClass = nullptr;
    FName FunctionName;
    if (!ResolveMemberIdentity(Object, FunctionPath, TargetClass, FunctionName, OutResult))
    {
        return false;
    }

    UFunction* Function = TargetClass->FindFunctionByName(FunctionName);
    const FUnifiedScriptManagedFunctionBinding* RpcBinding = nullptr;
    for (UClass* Class = TargetClass; Class != nullptr; Class = Class->GetSuperClass())
    {
        auto* Generated = Cast<UUnifiedScriptBlueprintGeneratedClass>(Class);
        if (Generated == nullptr) continue;
        for (const auto& Pair : Generated->ManagedFunctionBindings)
        {
            if (Pair.Value.RpcTarget != EUnifiedScriptRpcTarget::None &&
                ((Function != nullptr && Function->GetFName() == Pair.Key) ||
                 (Function == nullptr && Pair.Value.ManagedName == FunctionName.ToString())))
            {
                RpcBinding = &Pair.Value;
                Function = Generated->FindFunctionByName(Pair.Key);
                break;
            }
        }
        if (RpcBinding != nullptr) break;
    }
    if (Function == nullptr)
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::FunctionNotFound,
            FString::Printf(TEXT("Function '%s' was not found."), *FunctionPath));
        return false;
    }
    if (Function->HasAnyFunctionFlags(FUNC_Static) ||
        !Function->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_BlueprintPure))
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::FunctionNotCallable,
            FString::Printf(TEXT("Function '%s' is not an instance Blueprint call."), *FunctionPath));
        return false;
    }

    TArray<FProperty*, TInlineAllocator<8>> InputProperties;
    if (RpcBinding != nullptr)
    {
        AActor* Owner = NetworkOwner(Object);
        const UActorComponent* Component = Cast<UActorComponent>(Object);
        const bool bClient = Owner != nullptr && Owner->GetNetMode() == NM_Client;
        // bReplicates itself is not replicated. An Actor enabled at runtime on
        // the server can retain a non-replicating archetype on its clients;
        // UE's received local role identifies that replica, not the CDO flag.
        const bool bNetworkedActor = Owner != nullptr && (bClient
            ? Owner->GetLocalRole() == ROLE_AutonomousProxy || Owner->GetLocalRole() == ROLE_SimulatedProxy
            : Owner->GetIsReplicated());
        const bool bAllowed = bNetworkedActor &&
            (Component == nullptr || Component->GetIsReplicated()) &&
            (!bClient || (RpcBinding->RpcTarget == EUnifiedScriptRpcTarget::Server && Owner->HasLocalNetOwner())) &&
            (bClient || RpcBinding->RpcTarget != EUnifiedScriptRpcTarget::Client ||
                Owner->GetNetMode() == NM_Standalone || Owner->GetNetConnection() != nullptr || Owner->HasLocalNetOwner());
        if (!bAllowed || !Function->HasAnyFunctionFlags(FUNC_Net))
        {
            Fail(OutResult, EUnifiedScriptInvocationError::FunctionNotCallable,
                FString::Printf(TEXT("RPC '%s' has no valid replicated owner/connection or authority for this direction (networkedActor=%d, componentReplicated=%d, localOwner=%d, netFunction=%d)."),
                    *FunctionPath, bNetworkedActor, Component == nullptr || Component->GetIsReplicated(),
                    Owner != nullptr && Owner->HasLocalNetOwner(), Function->HasAnyFunctionFlags(FUNC_Net)));
            return false;
        }
    }
    FProperty* ReturnProperty = nullptr;
    for (TFieldIterator<FProperty> Iterator(Function); Iterator; ++Iterator)
    {
        FProperty* Parameter = *Iterator;
        if (!Parameter->HasAnyPropertyFlags(CPF_Parm))
        {
            continue;
        }
        if (Parameter->HasAnyPropertyFlags(CPF_ReturnParm))
        {
            ReturnProperty = Parameter;
            continue;
        }
        if (Parameter->HasAnyPropertyFlags(CPF_OutParm | CPF_ReferenceParm))
        {
            Fail(
                OutResult,
                EUnifiedScriptInvocationError::UnsupportedParameter,
                FString::Printf(
                    TEXT("Function '%s' contains unsupported output or reference parameter '%s'."),
                    *FunctionPath,
                    *Parameter->GetName()));
            return false;
        }
        InputProperties.Add(Parameter);
    }

    if (InputProperties.Num() != Arguments.Num())
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::ArgumentCountMismatch,
            FString::Printf(
                TEXT("Function '%s' requires %d arguments but received %d."),
                *FunctionPath,
                InputProperties.Num(),
                Arguments.Num()));
        return false;
    }

    FStructOnScope ParameterStorage(Function);
    void* ParameterMemory = ParameterStorage.GetStructMemory();
    for (int32 Index = 0; Index < InputProperties.Num(); ++Index)
    {
        if (!WriteValue(Registry, InputProperties[Index], ParameterMemory, Arguments[Index], OutResult))
        {
            return false;
        }
    }

    Object->ProcessEvent(Function, ParameterMemory);
    if (ReturnProperty != nullptr)
    {
        if (!ReadValue(Registry, ReturnProperty, ParameterMemory, OutReturnValue, OutResult))
        {
            return false;
        }
    }
    else
    {
        OutReturnValue = {};
    }

    Succeed(OutResult);
    return true;
}

bool FUnifiedScriptInvocationBridge::ReadPropertyById(
    FUnifiedScriptObjectHandle Target,
    uint64 MemberIdValue,
    FUnifiedScriptValue& OutValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;
    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr ||
        !FUnifiedScriptGeneratedThunkRegistry::ReadProperty(
            Object,
            Registry,
            MemberIdValue,
            OutValue,
            OutResult))
    {
        return false;
    }
    FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool FUnifiedScriptInvocationBridge::WritePropertyById(
    FUnifiedScriptObjectHandle Target,
    uint64 MemberIdValue,
    const FUnifiedScriptValue& Value,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;
    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr ||
        !FUnifiedScriptGeneratedThunkRegistry::WriteProperty(
            Object,
            Registry,
            MemberIdValue,
            Value,
            OutResult))
    {
        return false;
    }
    FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool FUnifiedScriptInvocationBridge::InvokeById(
    FUnifiedScriptObjectHandle Target,
    uint64 MemberIdValue,
    TConstArrayView<FUnifiedScriptValue> Arguments,
    FUnifiedScriptValue& OutReturnValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    TArray<FUnifiedScriptValue> IgnoredOutputValues;
    return InvokeById(
        Target,
        MemberIdValue,
        Arguments,
        OutReturnValue,
        IgnoredOutputValues,
        OutResult);
}

bool FUnifiedScriptInvocationBridge::InvokeById(
    FUnifiedScriptObjectHandle Target,
    uint64 MemberIdValue,
    TConstArrayView<FUnifiedScriptValue> Arguments,
    FUnifiedScriptValue& OutReturnValue,
    TArray<FUnifiedScriptValue>& OutOutputValues,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;
    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr ||
        !FUnifiedScriptGeneratedThunkRegistry::Invoke(
            Object,
            Registry,
            MemberIdValue,
            Arguments,
            OutReturnValue,
            OutOutputValues,
            OutResult))
    {
        return false;
    }
    FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool FUnifiedScriptInvocationBridge::InvokeInt32x2ById(
    FUnifiedScriptObjectHandle Target,
    uint64 MemberIdValue,
    int32 Argument0,
    int32 Argument1,
    int32& OutReturnValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;
    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr ||
        !FUnifiedScriptGeneratedThunkRegistry::InvokeInt32x2(
            Object,
            MemberIdValue,
            Argument0,
            Argument1,
            OutReturnValue,
            OutResult))
    {
        return false;
    }
    FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool FUnifiedScriptInvocationBridge::InvokeFixedById(
    FUnifiedScriptObjectHandle Target,
    uint64 MemberIdValue,
    TConstArrayView<FUnifiedScriptNativeValueV3> Arguments,
    FUnifiedScriptNativeValueV3* OutReturnValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;
    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr ||
        !FUnifiedScriptGeneratedThunkRegistry::InvokeFixed(
            Object,
            Registry,
            MemberIdValue,
            Arguments,
            OutReturnValue,
            OutResult))
    {
        return false;
    }
    FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
    return true;
}

uint64 FUnifiedScriptInvocationBridge::GetFastInvocationCount() const
{
    return FastInvocationCount.load(std::memory_order_relaxed);
}

bool FUnifiedScriptInvocationBridge::ResolveMulticastDelegate(
    FUnifiedScriptObjectHandle Target,
    const FString& EventPath,
    UObject*& OutSource,
    const FMulticastDelegateProperty*& OutProperty,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;
    check(IsInGameThread());
    OutSource = nullptr;
    OutProperty = nullptr;

    UObject* Source = ResolveTarget(Registry, Target, OutResult);
    UClass* TargetClass = nullptr;
    FName MemberName;
    if (Source == nullptr ||
        !ResolveMemberIdentity(Source, EventPath, TargetClass, MemberName, OutResult))
    {
        return false;
    }

    const FMulticastDelegateProperty* Property =
        FindFProperty<FMulticastDelegateProperty>(TargetClass, MemberName);
    if (Property == nullptr || Property->SignatureFunction == nullptr)
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::PropertyNotFound,
            FString::Printf(
                TEXT("UnifiedScript event '%s' is not a multicast delegate property."),
                *EventPath));
        return false;
    }
    if (!Property->HasAnyPropertyFlags(CPF_BlueprintAssignable))
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::PropertyNotWritable,
            FString::Printf(
                TEXT("UnifiedScript event '%s' is not BlueprintAssignable."),
                *EventPath));
        return false;
    }

    OutSource = Source;
    OutProperty = Property;
    Succeed(OutResult);
    return true;
}

bool FUnifiedScriptInvocationBridge::ReadDelegateArguments(
    const UFunction* Signature,
    const void* ParameterMemory,
    TArray<FUnifiedScriptValue>& OutArguments,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;
    check(IsInGameThread());
    OutArguments.Reset();
    if (Signature == nullptr || (Signature->ParmsSize > 0 && ParameterMemory == nullptr))
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::InvalidMemberPath,
            TEXT("A delegate dispatch requires a signature and memory for non-empty parameters."));
        return false;
    }

    for (TFieldIterator<FProperty> It(Signature, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const FProperty* Parameter = *It;
        if (!Parameter->HasAnyPropertyFlags(CPF_Parm) ||
            Parameter->HasAnyPropertyFlags(CPF_ReturnParm))
        {
            continue;
        }

        FUnifiedScriptValue Value;
        if (!ReadValue(Registry, Parameter, ParameterMemory, Value, OutResult))
        {
            ReleaseEventArguments(OutArguments);
            OutArguments.Reset();
            return false;
        }
        OutArguments.Add(MoveTemp(Value));
    }

    Succeed(OutResult);
    return true;
}

void FUnifiedScriptInvocationBridge::ReleaseEventArguments(
    TConstArrayView<FUnifiedScriptValue> Arguments)
{
    check(IsInGameThread());
    TFunction<void(const FUnifiedScriptValue&)> ReleaseValue =
        [this, &ReleaseValue](const FUnifiedScriptValue& Value)
    {
        if ((Value.Kind == EUnifiedScriptValueKind::Object ||
             Value.Kind == EUnifiedScriptValueKind::Class) &&
            !Value.ObjectHandleValue.IsNull())
        {
            FUnifiedScriptObjectHandleResult Result;
            Registry.ReleaseObject(Value.ObjectHandleValue, Result);
            ensureMsgf(Result.IsSuccess(), TEXT("%s"), *Result.Message);
        }
        for (const FUnifiedScriptValue& Child : Value.ContainerValues)
        {
            ReleaseValue(Child);
        }
    };

    for (const FUnifiedScriptValue& Value : Arguments)
    {
        ReleaseValue(Value);
    }
}

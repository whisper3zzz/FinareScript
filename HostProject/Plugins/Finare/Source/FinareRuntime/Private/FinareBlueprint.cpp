#include "FinareBlueprint.h"

#include "Engine/Engine.h"
#include "GameFramework/Actor.h"
#include "FinareAsset.h"
#include "FinareComponent.h"
#include "FinareNativeTransport.h"
#include "FinarePropertyValueCodec.h"
#include "FinareWorldSubsystem.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

#include UE_INLINE_GENERATED_CPP_BY_NAME(FinareBlueprint)

namespace UE::Finare::Blueprint::Private
{
constexpr int32 ManagedFunctionAbiV2 = 2;
constexpr int32 MaximumManagedFunctionArenaBytes = 64 * 1024;
const FName WorldContextObjectName(TEXT("WorldContextObject"));
const FName KismetWorldContextName(TEXT("__WorldContext"));

enum class EManagedComponentResolution : uint8
{
    None,
    Unique,
    Ambiguous,
};

bool PackValue(
    const FProperty& Property,
    const void* Container,
    FFinareManagedFunctionValueV1& OutValue)
{
    const void* Value = Property.ContainerPtrToValuePtr<void>(Container);
    if (const FBoolProperty* Typed = CastField<FBoolProperty>(&Property))
    {
        OutValue.Kind = EFinareManagedFunctionValueKindV1::Boolean;
        OutValue.Payload = Typed->GetPropertyValue(Value) ? 1 : 0;
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(&Property))
    {
        OutValue.Kind = EFinareManagedFunctionValueKindV1::Byte;
        OutValue.Payload = Typed->GetPropertyValue(Value);
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(&Property))
    {
        OutValue.Kind = EFinareManagedFunctionValueKindV1::Int32;
        OutValue.Payload = static_cast<uint64>(static_cast<int64>(
            Typed->GetPropertyValue(Value)));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(&Property))
    {
        OutValue.Kind = EFinareManagedFunctionValueKindV1::Int64;
        OutValue.Payload = static_cast<uint64>(Typed->GetPropertyValue(Value));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(&Property))
    {
        OutValue.Kind = EFinareManagedFunctionValueKindV1::Single;
        const float Scalar = Typed->GetPropertyValue(Value);
        static_assert(sizeof(Scalar) == sizeof(uint32));
        uint32 Bits = 0;
        FMemory::Memcpy(&Bits, &Scalar, sizeof(Bits));
        OutValue.Payload = Bits;
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(&Property))
    {
        OutValue.Kind = EFinareManagedFunctionValueKindV1::Double;
        const double Scalar = Typed->GetPropertyValue(Value);
        static_assert(sizeof(Scalar) == sizeof(uint64));
        FMemory::Memcpy(&OutValue.Payload, &Scalar, sizeof(OutValue.Payload));
        return true;
    }
    return false;
}

bool UnpackReturnValue(
    const FProperty& Property,
    const FFinareManagedFunctionValueV1& Value,
    void* Result)
{
    if (const FBoolProperty* Typed = CastField<FBoolProperty>(&Property);
        Typed != nullptr &&
        Value.Kind == EFinareManagedFunctionValueKindV1::Boolean &&
        Value.Payload <= 1)
    {
        Typed->SetPropertyValue(Result, Value.Payload != 0);
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(&Property);
        Typed != nullptr &&
        Value.Kind == EFinareManagedFunctionValueKindV1::Byte &&
        Value.Payload <= MAX_uint8)
    {
        Typed->SetPropertyValue(Result, static_cast<uint8>(Value.Payload));
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(&Property);
        Typed != nullptr && Value.Kind == EFinareManagedFunctionValueKindV1::Int32)
    {
        Typed->SetPropertyValue(Result, static_cast<int32>(Value.Payload));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(&Property);
        Typed != nullptr && Value.Kind == EFinareManagedFunctionValueKindV1::Int64)
    {
        Typed->SetPropertyValue(Result, static_cast<int64>(Value.Payload));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(&Property);
        Typed != nullptr && Value.Kind == EFinareManagedFunctionValueKindV1::Single)
    {
        const uint32 Bits = static_cast<uint32>(Value.Payload);
        float Scalar = 0.0f;
        FMemory::Memcpy(&Scalar, &Bits, sizeof(Scalar));
        Typed->SetPropertyValue(Result, Scalar);
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(&Property);
        Typed != nullptr && Value.Kind == EFinareManagedFunctionValueKindV1::Double)
    {
        double Scalar = 0.0;
        FMemory::Memcpy(&Scalar, &Value.Payload, sizeof(Scalar));
        Typed->SetPropertyValue(Result, Scalar);
        return true;
    }
    return false;
}

UFinareComponent* ResolveComponent(
    UObject* Context,
    const UFinareBlueprintGeneratedClass* GeneratedClass,
    FString& OutError,
    EManagedComponentResolution& OutResolution)
{
    OutResolution = EManagedComponentResolution::None;
    if (UFinareComponent* Component = Cast<UFinareComponent>(Context))
    {
        if (Component->OwnsActiveManagedOverride(GeneratedClass))
        {
            OutResolution = EManagedComponentResolution::Unique;
            return Component;
        }
        OutError = TEXT("The managed function context is not owned by its active Script Component.");
        return nullptr;
    }
    AActor* Actor = Cast<AActor>(Context);
    if (Actor == nullptr)
    {
        OutError = TEXT("The managed function context is neither an Actor nor a Script Component.");
        return nullptr;
    }
    TArray<UFinareComponent*> Matches;
    Actor->GetComponents<UFinareComponent>(Matches);
    Matches.RemoveAll([&](UFinareComponent* Component)
    {
        return Component == nullptr || !Component->OwnsActiveManagedOverride(GeneratedClass);
    });
    if (Matches.Num() != 1)
    {
        OutResolution = Matches.Num() > 1
            ? EManagedComponentResolution::Ambiguous
            : EManagedComponentResolution::None;
        OutError = FString::Printf(
            TEXT("The managed function context has %d active Script Components for its generated owner; exact ownership is required."),
            Matches.Num());
        return nullptr;
    }
    OutResolution = EManagedComponentResolution::Unique;
    return Matches[0];
}

UFinareComponent* ResolveConfiguredComponent(
    UObject* Context,
    const UFinareBlueprintGeneratedClass* GeneratedClass,
    FString& OutError)
{
    if (UFinareComponent* Component = Cast<UFinareComponent>(Context))
    {
        if (Component->OwnsConfiguredManagedOverride(GeneratedClass))
        {
            return Component;
        }
        OutError = TEXT("The pre-start managed function context is not owned by its configured Script Component.");
        return nullptr;
    }
    AActor* Actor = Cast<AActor>(Context);
    if (Actor == nullptr)
    {
        OutError = TEXT("The pre-start managed function context is neither an Actor nor a Script Component.");
        return nullptr;
    }
    TArray<UFinareComponent*> Matches;
    Actor->GetComponents<UFinareComponent>(Matches);
    Matches.RemoveAll([&](UFinareComponent* Component)
    {
        return Component == nullptr || !Component->OwnsConfiguredManagedOverride(GeneratedClass);
    });
    if (Matches.Num() != 1)
    {
        OutError = FString::Printf(
            TEXT("The pre-start managed function context has %d configured Script Components for its generated owner; exact ownership is required."),
            Matches.Num());
        return nullptr;
    }
    return Matches[0];
}

UFinareWorldSubsystem* ResolveWorldHost(UObject* WorldContextObject)
{
    UWorld* World = GEngine != nullptr && WorldContextObject != nullptr
        ? GEngine->GetWorldFromContextObject(
            WorldContextObject,
            EGetWorldErrorMode::ReturnNull)
        : nullptr;
    return World != nullptr
        ? World->GetSubsystem<UFinareWorldSubsystem>()
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
    const FFinareManagedValueTypeBinding& Binding,
    TConstArrayView<FName> Prefix,
    TArray<FName, TInlineAllocator<32>>& OutNames)
{
    OutNames.Reset();
    for (const FFinareManagedStructFieldPath& Field : Binding.StructFields)
    {
        if (Field.Segments.Num() == Prefix.Num() + 1 &&
            PathStartsWith(Field.Segments, Prefix))
        {
            OutNames.Add(Field.Segments.Last());
        }
    }
}

constexpr TCHAR BlueprintOverrideActivationSchema[] =
    TEXT("unifiedscript.blueprint-override-contract/v0.1");
constexpr int32 BlueprintOverrideActivationMaximumBytes = 256 * 1024;
constexpr int32 BlueprintOverrideActivationMaximumDepth = 16;

TSharedPtr<FJsonObject> MakeActivationShape(
    const TCHAR* Kind,
    const FString& NativeName,
    const FString& TypePath)
{
    TSharedPtr<FJsonObject> Shape = MakeShared<FJsonObject>();
    Shape->SetStringField(TEXT("kind"), Kind);
    Shape->SetStringField(TEXT("nativeName"), NativeName);
    Shape->SetStringField(TEXT("typePath"), TypePath);
    return Shape;
}

bool HasBindingPath(
    const FFinareManagedValueTypeBinding& Binding,
    TConstArrayView<FName> Path)
{
    return Binding.StructFields.ContainsByPredicate(
        [&Path](const FFinareManagedStructFieldPath& Field)
        {
            return Field.Segments.Num() == Path.Num() &&
                PathStartsWith(Field.Segments, Path);
        });
}

bool IsNativeConstParameter(const FProperty& Property)
{
    if (Property.HasAnyPropertyFlags(CPF_ConstParm))
    {
        return true;
    }
#if WITH_METADATA
    return Property.HasMetaData(TEXT("NativeConst"));
#else
    return false;
#endif
}

bool GetNativeParameterDirection(
    const FProperty& Property,
    EFinareManagedParameterDirection& OutDirection)
{
    if (!Property.HasAnyPropertyFlags(CPF_Parm) ||
        Property.HasAnyPropertyFlags(CPF_ReturnParm))
    {
        return false;
    }
    if (IsNativeConstParameter(Property) ||
        !Property.HasAnyPropertyFlags(CPF_OutParm))
    {
        OutDirection = EFinareManagedParameterDirection::Input;
    }
    else if (Property.HasAnyPropertyFlags(CPF_ReferenceParm))
    {
        OutDirection = EFinareManagedParameterDirection::InputOutput;
    }
    else
    {
        OutDirection = EFinareManagedParameterDirection::Output;
    }
    return true;
}

bool BuildActivationShape(
    const FProperty& Property,
    const FFinareManagedValueTypeBinding* Binding,
    TConstArrayView<FName> Prefix,
    int32 Depth,
    TArray<TArray<FName>>* ConsumedPaths,
    TSharedPtr<FJsonObject>& OutShape,
    FString& OutError)
{
    if (Property.ArrayDim != 1)
    {
        OutError = FString::Printf(
            TEXT("Blueprint activation property '%s' uses a fixed native array, which has no reviewed ABI shape."),
            *Property.GetName());
        return false;
    }
    if (Depth > BlueprintOverrideActivationMaximumDepth)
    {
        OutError = FString::Printf(
            TEXT("Blueprint activation shape for '%s' exceeds the %d-level nesting limit."),
            *Property.GetName(),
            BlueprintOverrideActivationMaximumDepth);
        return false;
    }
    if (Binding == nullptr)
    {
        OutError = FString::Printf(
            TEXT("Blueprint activation shape for '%s' has no persistent type binding."),
            *Property.GetName());
        return false;
    }
    if (ConsumedPaths != nullptr && !Prefix.IsEmpty() &&
        !ConsumedPaths->ContainsByPredicate(
            [&Prefix](const TArray<FName>& Existing)
            {
                return Existing.Num() == Prefix.Num() &&
                    PathStartsWith(Existing, Prefix);
            }))
    {
        TArray<FName>& Consumed = ConsumedPaths->AddDefaulted_GetRef();
        Consumed.Append(Prefix.GetData(), Prefix.Num());
    }

    const FString NativeName =
        UE::Finare::PropertyValueCodec::ContainerValueNativeName(Property);
    const bool bTopLevel = Prefix.IsEmpty();
    const auto RequireEmptyBinding = [&]()
    {
        if (bTopLevel &&
            (!Binding->UnrealTypePath.IsEmpty() || !Binding->StructFields.IsEmpty()))
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation shape for '%s' has unexpected struct/type metadata."),
                *Property.GetName());
            return false;
        }
        return true;
    };

    if (const FSoftClassProperty* SoftClass = CastField<FSoftClassProperty>(&Property))
    {
        (void)SoftClass;
        if (!RequireEmptyBinding()) return false;
        OutShape = MakeActivationShape(TEXT("value"), NativeName, TEXT(""));
        return true;
    }
    if (const FSoftObjectProperty* SoftObject = CastField<FSoftObjectProperty>(&Property))
    {
        (void)SoftObject;
        if (!RequireEmptyBinding()) return false;
        OutShape = MakeActivationShape(TEXT("value"), NativeName, TEXT(""));
        return true;
    }

    const UEnum* Enum = nullptr;
    if (const FEnumProperty* EnumProperty = CastField<FEnumProperty>(&Property))
    {
        Enum = EnumProperty->GetEnum();
    }
    else if (const FByteProperty* ByteProperty = CastField<FByteProperty>(&Property))
    {
        Enum = ByteProperty->Enum;
    }
    if (Enum != nullptr)
    {
        const FString TypePath = Enum->GetPathName();
        if (bTopLevel && Binding->UnrealTypePath != TypePath)
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation enum '%s' resolved '%s', expected '%s'."),
                *Property.GetName(),
                *TypePath,
                *Binding->UnrealTypePath);
            return false;
        }
        if (bTopLevel && !Binding->StructFields.IsEmpty())
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation enum '%s' has unexpected field paths."),
                *Property.GetName());
            return false;
        }
        OutShape = MakeActivationShape(TEXT("enum"), NativeName, TypePath);
        return true;
    }

    if (const FArrayProperty* Array = CastField<FArrayProperty>(&Property))
    {
        if (bTopLevel && !Binding->UnrealTypePath.IsEmpty())
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation array '%s' has unexpected type path '%s'."),
                *Property.GetName(),
                *Binding->UnrealTypePath);
            return false;
        }
        TArray<FName, TInlineAllocator<8>> ChildPrefix;
        ChildPrefix.Append(Prefix.GetData(), Prefix.Num());
        ChildPrefix.Add(FName(TEXT("$element")));
        if (!HasBindingPath(*Binding, ChildPrefix))
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation array '%s' is missing its $element contract."),
                *Property.GetName());
            return false;
        }
        TSharedPtr<FJsonObject> Element;
        if (!BuildActivationShape(
                *Array->Inner,
                Binding,
                ChildPrefix,
                Depth + 1,
                ConsumedPaths,
                Element,
                OutError))
        {
            return false;
        }
        OutShape = MakeActivationShape(TEXT("array"), NativeName, TEXT(""));
        OutShape->SetObjectField(TEXT("element"), Element);
        return true;
    }
    if (const FSetProperty* Set = CastField<FSetProperty>(&Property))
    {
        if (bTopLevel && !Binding->UnrealTypePath.IsEmpty())
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation set '%s' has unexpected type path '%s'."),
                *Property.GetName(),
                *Binding->UnrealTypePath);
            return false;
        }
        TArray<FName, TInlineAllocator<8>> ChildPrefix;
        ChildPrefix.Append(Prefix.GetData(), Prefix.Num());
        ChildPrefix.Add(FName(TEXT("$element")));
        if (!HasBindingPath(*Binding, ChildPrefix))
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation set '%s' is missing its $element contract."),
                *Property.GetName());
            return false;
        }
        TSharedPtr<FJsonObject> Element;
        if (!BuildActivationShape(
                *Set->ElementProp,
                Binding,
                ChildPrefix,
                Depth + 1,
                ConsumedPaths,
                Element,
                OutError))
        {
            return false;
        }
        OutShape = MakeActivationShape(TEXT("set"), NativeName, TEXT(""));
        OutShape->SetObjectField(TEXT("element"), Element);
        return true;
    }
    if (const FMapProperty* Map = CastField<FMapProperty>(&Property))
    {
        if (bTopLevel && !Binding->UnrealTypePath.IsEmpty())
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation map '%s' has unexpected type path '%s'."),
                *Property.GetName(),
                *Binding->UnrealTypePath);
            return false;
        }
        TArray<FName, TInlineAllocator<8>> KeyPrefix;
        KeyPrefix.Append(Prefix.GetData(), Prefix.Num());
        KeyPrefix.Add(FName(TEXT("$key")));
        TArray<FName, TInlineAllocator<8>> ValuePrefix;
        ValuePrefix.Append(Prefix.GetData(), Prefix.Num());
        ValuePrefix.Add(FName(TEXT("$value")));
        if (!HasBindingPath(*Binding, KeyPrefix) ||
            !HasBindingPath(*Binding, ValuePrefix))
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation map '%s' is missing its $key or $value contract."),
                *Property.GetName());
            return false;
        }
        TSharedPtr<FJsonObject> Key;
        TSharedPtr<FJsonObject> Value;
        if (!BuildActivationShape(
                *Map->KeyProp,
                Binding,
                KeyPrefix,
                Depth + 1,
                ConsumedPaths,
                Key,
                OutError) ||
            !BuildActivationShape(
                *Map->ValueProp,
                Binding,
                ValuePrefix,
                Depth + 1,
                ConsumedPaths,
                Value,
                OutError))
        {
            return false;
        }
        OutShape = MakeActivationShape(TEXT("map"), NativeName, TEXT(""));
        OutShape->SetObjectField(TEXT("key"), Key);
        OutShape->SetObjectField(TEXT("value"), Value);
        return true;
    }

    if (const FStructProperty* Struct = CastField<FStructProperty>(&Property))
    {
        if (Struct->Struct == TBaseStructure<FVector>::Get() ||
            Struct->Struct == TBaseStructure<FRotator>::Get() ||
            Struct->Struct == TBaseStructure<FTransform>::Get())
        {
            if (!RequireEmptyBinding()) return false;
            OutShape = MakeActivationShape(TEXT("value"), NativeName, TEXT(""));
            return true;
        }
        const FString TypePath = Struct->Struct->GetPathName();
        if (bTopLevel && Binding->UnrealTypePath != TypePath)
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation struct '%s' resolved '%s', expected '%s'."),
                *Property.GetName(),
                *TypePath,
                *Binding->UnrealTypePath);
            return false;
        }
        TArray<FName, TInlineAllocator<32>> FieldNames;
        CollectDirectStructFields(*Binding, Prefix, FieldNames);
        if (FieldNames.IsEmpty())
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation struct '%s' has no persisted direct fields."),
                *TypePath);
            return false;
        }
        TSet<FName> SeenFields;
        TArray<TSharedPtr<FJsonValue>> Fields;
        for (int32 Index = 0; Index < FieldNames.Num(); ++Index)
        {
            const FName FieldName = FieldNames[Index];
            if (SeenFields.Contains(FieldName))
            {
                OutError = FString::Printf(
                    TEXT("Blueprint activation struct '%s' repeats field '%s'."),
                    *TypePath,
                    *FieldName.ToString());
                return false;
            }
            SeenFields.Add(FieldName);
            const FProperty* Field = FindFProperty<FProperty>(Struct->Struct, FieldName);
            if (Field == nullptr)
            {
                OutError = FString::Printf(
                    TEXT("Blueprint activation struct '%s' no longer contains field '%s'."),
                    *TypePath,
                    *FieldName.ToString());
                return false;
            }
            TArray<FName, TInlineAllocator<8>> ChildPrefix;
            ChildPrefix.Append(Prefix.GetData(), Prefix.Num());
            ChildPrefix.Add(FieldName);
            TSharedPtr<FJsonObject> FieldShape;
            if (!BuildActivationShape(
                    *Field,
                    Binding,
                    ChildPrefix,
                    Depth + 1,
                    ConsumedPaths,
                    FieldShape,
                    OutError))
            {
                return false;
            }
            TSharedPtr<FJsonObject> FieldObject = MakeShared<FJsonObject>();
            FieldObject->SetStringField(TEXT("nativeName"), Field->GetName());
            FieldObject->SetObjectField(TEXT("type"), FieldShape);
            Fields.Add(MakeShared<FJsonValueObject>(FieldObject));
        }
        OutShape = MakeActivationShape(TEXT("struct"), NativeName, TypePath);
        OutShape->SetArrayField(TEXT("fields"), MoveTemp(Fields));
        return true;
    }

    if (!RequireEmptyBinding() || NativeName.IsEmpty())
    {
        if (OutError.IsEmpty())
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation property '%s' uses an unsupported native shape '%s'."),
                *Property.GetName(),
                *Property.GetClass()->GetName());
        }
        return false;
    }
    OutShape = MakeActivationShape(TEXT("value"), NativeName, TEXT(""));
    return true;
}

bool BuildValidatedActivationShape(
    const FProperty& Property,
    const FFinareManagedValueTypeBinding& Binding,
    TSharedPtr<FJsonObject>& OutShape,
    FString& OutError)
{
    OutError.Reset();
    TArray<FName> Prefix;
    TArray<TArray<FName>> ConsumedPaths;
    if (!BuildActivationShape(
            Property,
            &Binding,
            Prefix,
            0,
            &ConsumedPaths,
            OutShape,
            OutError))
    {
        return false;
    }

    const auto HasPath = [](const TArray<TArray<FName>>& Paths,
                            const TArray<FName>& Path)
    {
        return Paths.ContainsByPredicate(
            [&Path](const TArray<FName>& Candidate)
            {
                return Candidate.Num() == Path.Num() &&
                    PathStartsWith(Candidate, Path);
            });
    };
    if (ConsumedPaths.Num() != Binding.StructFields.Num())
    {
        OutError = FString::Printf(
            TEXT("Blueprint activation shape for '%s' does not consume every persisted field path."),
            *Property.GetName());
        return false;
    }
    for (const FFinareManagedStructFieldPath& Field : Binding.StructFields)
    {
        if (!HasPath(ConsumedPaths, Field.Segments))
        {
            OutError = FString::Printf(
                TEXT("Blueprint activation shape for '%s' leaves persisted field path '%s' unused."),
                *Property.GetName(),
                *FString::JoinBy(Field.Segments, TEXT("."),
                    [](const FName& Segment) { return Segment.ToString(); }));
            return false;
        }
    }

    return true;
}

bool BuildActivationShapeJson(
    const FProperty& Property,
    const FFinareManagedValueTypeBinding& Binding,
    FString& OutJson,
    FString& OutError)
{
    OutJson.Reset();
    TSharedPtr<FJsonObject> Shape;
    if (!BuildValidatedActivationShape(Property, Binding, Shape, OutError))
    {
        return false;
    }

    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutJson);
    if (!FJsonSerializer::Serialize(Shape.ToSharedRef(), Writer))
    {
        OutError = FString::Printf(
            TEXT("Failed to serialize the activation shape for '%s'."),
            *Property.GetName());
        OutJson.Reset();
        return false;
    }
    const FTCHARToUTF8 Utf8(*OutJson);
    if (Utf8.Length() > BlueprintOverrideActivationMaximumBytes)
    {
        OutJson.Reset();
        OutError = FString::Printf(
            TEXT("The activation shape for '%s' exceeds the %d-byte UTF-8 limit."),
            *Property.GetName(),
            BlueprintOverrideActivationMaximumBytes);
        return false;
    }
    return true;
}

bool SerializeActivationContract(
    const TArray<TSharedPtr<FJsonValue>>& Routes,
    FString& OutJson,
    FString& OutError)
{
    TSharedPtr<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schema"), BlueprintOverrideActivationSchema);
    Root->SetArrayField(TEXT("overrides"), Routes);
    OutJson.Reset();
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutJson);
    if (!FJsonSerializer::Serialize(Root.ToSharedRef(), Writer))
    {
        OutError = TEXT("Failed to serialize the Blueprint override activation contract.");
        return false;
    }
    const FTCHARToUTF8 Utf8(*OutJson);
    if (Utf8.Length() > BlueprintOverrideActivationMaximumBytes)
    {
        OutJson.Reset();
        OutError = FString::Printf(
            TEXT("The Blueprint override activation contract exceeds the %d-byte UTF-8 limit."),
            BlueprintOverrideActivationMaximumBytes);
        return false;
    }
    return true;
}

bool PackValueV2(
    const FProperty& Property,
    const void* Container,
    UFinareWorldSubsystem& Host,
    TArray<uint8, TInlineAllocator<512>>& Arena,
    const FFinareManagedValueTypeBinding* TypeBinding,
    TConstArrayView<FName> StructPrefix,
    FFinareNativeValueV3& OutValue,
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
        const int64 DescriptorBytes = static_cast<int64>(Count) * CellsPerEntry * sizeof(FFinareNativeValueV3);
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
            FFinareNativeValueV3 Cell;
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
        OutValue.Kind = ArrayProperty ? EFinareNativeValueKindV3::Array :
            SetProperty ? EFinareNativeValueKindV3::Set : EFinareNativeValueKindV3::Map;
        OutValue.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
        OutValue.TypeId = UE::Finare::PropertyValueCodec::TypeIdFromPath(
            UE::Finare::PropertyValueCodec::ContainerValueNativeName(Property));
        OutValue.Payload0 = Offset;
        OutValue.Payload1 = DescriptorBytes;
        OutValue.Payload2 = Count;
        OutValue.Payload3 = sizeof(FFinareNativeValueV3);
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
        OutValue.Kind = EFinareNativeValueKindV3::String;
        OutValue.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
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
        OutValue.Kind = EFinareNativeValueKindV3::Enum;
        OutValue.TypeId = UE::Finare::PropertyValueCodec::TypeIdFromPath(TypePath);
        OutValue.Payload0 = static_cast<uint64>(EnumValue);
        return OutValue.TypeId != 0;
    }
    if (const FBoolProperty* Typed = CastField<FBoolProperty>(&Property))
    {
        OutValue.Kind = EFinareNativeValueKindV3::Boolean;
        OutValue.Payload0 = Typed->GetPropertyValue(Value) ? 1 : 0;
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(&Property))
    {
        OutValue.Kind = EFinareNativeValueKindV3::UInt8;
        OutValue.Payload0 = Typed->GetPropertyValue(Value);
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(&Property))
    {
        OutValue.Kind = EFinareNativeValueKindV3::Int32;
        OutValue.Payload0 = static_cast<uint64>(static_cast<int64>(
            Typed->GetPropertyValue(Value)));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(&Property))
    {
        OutValue.Kind = EFinareNativeValueKindV3::Int64;
        OutValue.Payload0 = static_cast<uint64>(Typed->GetPropertyValue(Value));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(&Property))
    {
        OutValue.Kind = EFinareNativeValueKindV3::Float32;
        const float Scalar = Typed->GetPropertyValue(Value);
        uint32 Bits = 0;
        FMemory::Memcpy(&Bits, &Scalar, sizeof(Bits));
        OutValue.Payload0 = Bits;
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(&Property))
    {
        OutValue.Kind = EFinareNativeValueKindV3::Float64;
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
        OutValue.Kind = EFinareNativeValueKindV3::String;
        OutValue.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
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
        OutValue.Kind = EFinareNativeValueKindV3::Name;
        OutValue.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
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
        OutValue.Kind = EFinareNativeValueKindV3::Text;
        OutValue.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
        OutValue.Payload0 = static_cast<uint64>(Offset);
        OutValue.Payload1 = static_cast<uint64>(Utf8.Length());
        return true;
    }
    if (const FObjectPropertyBase* Typed = CastField<FObjectPropertyBase>(&Property))
    {
        UObject* Object = Typed->GetObjectPropertyValue(Value);
        OutValue.Kind = CastField<FClassProperty>(&Property) != nullptr
            ? EFinareNativeValueKindV3::Class
            : EFinareNativeValueKindV3::Object;
        if (Object == nullptr)
        {
            OutValue.Flags = EFinareNativeValueFlagsV3::Null;
            return true;
        }
        FFinareObjectHandleResult HandleResult;
        const FFinareObjectHandle Handle = Host.RegisterObject(Object, HandleResult);
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
        OutValue.Kind = EFinareNativeValueKindV3::Vector3;
        OutValue.Payload0 = DoubleBits(Vector.X);
        OutValue.Payload1 = DoubleBits(Vector.Y);
        OutValue.Payload2 = DoubleBits(Vector.Z);
        return true;
    }
    if (const FStructProperty* Typed = CastField<FStructProperty>(&Property);
        Typed != nullptr && Typed->Struct == TBaseStructure<FRotator>::Get())
    {
        const FRotator& Rotator = *static_cast<const FRotator*>(Value);
        OutValue.Kind = EFinareNativeValueKindV3::Rotator3;
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
        OutValue.Kind = EFinareNativeValueKindV3::Transform;
        OutValue.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
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
            static_cast<int64>(FieldNames.Num()) * sizeof(FFinareNativeValueV3);
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
            FFinareNativeValueV3 Cell;
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
                    Index * sizeof(FFinareNativeValueV3),
                &Cell,
                sizeof(Cell));
        }
        OutValue.Kind = EFinareNativeValueKindV3::Struct;
        OutValue.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
        OutValue.TypeId = UE::Finare::PropertyValueCodec::TypeIdFromPath(TypePath);
        OutValue.Payload0 = DescriptorOffset;
        OutValue.Payload1 = DescriptorBytes;
        OutValue.Payload2 = FieldNames.Num();
        OutValue.Payload3 = sizeof(FFinareNativeValueV3);
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
    const FFinareNativeValueV3& Value,
    TConstArrayView<uint8> Arena,
    uint32 ByteCount,
    UFinareWorldSubsystem& Host,
    const FFinareManagedValueTypeBinding* TypeBinding,
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
        const EFinareNativeValueKindV3 Kind = ArrayProperty ? EFinareNativeValueKindV3::Array :
            SetProperty ? EFinareNativeValueKindV3::Set : EFinareNativeValueKindV3::Map;
        const uint64 CellsPerEntry = MapProperty ? 2 : 1;
        if (Value.Kind != Kind || Value.Flags != EFinareNativeValueFlagsV3::ArenaBacked ||
            Value.TypeId != UE::Finare::PropertyValueCodec::TypeIdFromPath(
                UE::Finare::PropertyValueCodec::ContainerValueNativeName(Property)) ||
            Value.Payload2 > MaximumManagedFunctionArenaBytes / (CellsPerEntry * sizeof(FFinareNativeValueV3)) ||
            Value.Payload3 != sizeof(FFinareNativeValueV3) ||
            Value.Payload1 != Value.Payload2 * CellsPerEntry * sizeof(FFinareNativeValueV3) ||
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
            FFinareNativeValueV3 Cell;
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
        if (Value.Kind != EFinareNativeValueKindV3::String ||
            Value.Flags != EFinareNativeValueFlagsV3::ArenaBacked || Value.TypeId != 0 ||
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
        if (Value.Kind != EFinareNativeValueKindV3::Enum ||
            Value.Flags != EFinareNativeValueFlagsV3::None ||
            Value.TypeId != UE::Finare::PropertyValueCodec::TypeIdFromPath(TypePath) ||
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
        Typed != nullptr && Value.Kind == EFinareNativeValueKindV3::Boolean &&
        Value.Payload0 <= 1)
    {
        Typed->SetPropertyValue(Result, Value.Payload0 != 0);
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(&Property);
        Typed != nullptr && Value.Kind == EFinareNativeValueKindV3::UInt8 &&
        Value.Payload0 <= MAX_uint8)
    {
        Typed->SetPropertyValue(Result, static_cast<uint8>(Value.Payload0));
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(&Property);
        Typed != nullptr && Value.Kind == EFinareNativeValueKindV3::Int32)
    {
        Typed->SetPropertyValue(Result, static_cast<int32>(Value.Payload0));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(&Property);
        Typed != nullptr && Value.Kind == EFinareNativeValueKindV3::Int64)
    {
        Typed->SetPropertyValue(Result, static_cast<int64>(Value.Payload0));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(&Property);
        Typed != nullptr && Value.Kind == EFinareNativeValueKindV3::Float32)
    {
        const uint32 Bits = static_cast<uint32>(Value.Payload0);
        float Scalar = 0.0f;
        FMemory::Memcpy(&Scalar, &Bits, sizeof(Scalar));
        Typed->SetPropertyValue(Result, Scalar);
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(&Property);
        Typed != nullptr && Value.Kind == EFinareNativeValueKindV3::Float64)
    {
        Typed->SetPropertyValue(Result, BitsDouble(Value.Payload0));
        return true;
    }
    if (const FStrProperty* Typed = CastField<FStrProperty>(&Property);
        Typed != nullptr && Value.Kind == EFinareNativeValueKindV3::String &&
        Value.Flags == EFinareNativeValueFlagsV3::ArenaBacked &&
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
        Typed != nullptr && Value.Kind == EFinareNativeValueKindV3::Name &&
        Value.Flags == EFinareNativeValueFlagsV3::ArenaBacked &&
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
        Typed != nullptr && Value.Kind == EFinareNativeValueKindV3::Text &&
        Value.Flags == EFinareNativeValueFlagsV3::ArenaBacked &&
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
                ? EFinareNativeValueKindV3::Class
                : EFinareNativeValueKindV3::Object))
    {
        if (Value.Payload0 == 0)
        {
            Typed->SetObjectPropertyValue(Result, nullptr);
            return true;
        }
        FFinareObjectHandleResult HandleResult;
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
        Value.Kind == EFinareNativeValueKindV3::Rotator3)
    {
        *static_cast<FRotator*>(Result) = FRotator(
            BitsDouble(Value.Payload0),
            BitsDouble(Value.Payload1),
            BitsDouble(Value.Payload2));
        return true;
    }
    if (const FStructProperty* Typed = CastField<FStructProperty>(&Property);
        Typed != nullptr && Typed->Struct == TBaseStructure<FTransform>::Get() &&
        Value.Kind == EFinareNativeValueKindV3::Transform &&
        Value.Flags == EFinareNativeValueFlagsV3::ArenaBacked &&
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
        Value.Kind == EFinareNativeValueKindV3::Vector3)
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
            static_cast<uint64>(FieldNames.Num()) * sizeof(FFinareNativeValueV3);
        if (Value.Kind != EFinareNativeValueKindV3::Struct ||
            Value.Flags != EFinareNativeValueFlagsV3::ArenaBacked ||
            Value.TypeId != UE::Finare::PropertyValueCodec::TypeIdFromPath(TypePath) ||
            Value.Payload1 != DescriptorBytes ||
            Value.Payload2 != static_cast<uint64>(FieldNames.Num()) ||
            Value.Payload3 != sizeof(FFinareNativeValueV3) ||
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
            FFinareNativeValueV3 Cell;
            FMemory::Memcpy(
                &Cell,
                Arena.GetData() + DescriptorOffset +
                    Index * sizeof(FFinareNativeValueV3),
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
UClass* UFinareBlueprint::GetBlueprintClass() const
{
    return UFinareBlueprintGeneratedClass::StaticClass();
}
#endif

void UFinareBlueprintGeneratedClass::Bind()
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

void UFinareBlueprintGeneratedClass::PurgeClass(bool bRecompilingOnLoad)
{
    Super::PurgeClass(bRecompilingOnLoad);
    bManagedFunctionsBound = false;
    RegisteredManagedNativeFunctions.Reset();
}

void UFinareBlueprintGeneratedClass::PostLoad()
{
    bManagedFunctionsBound = false;
    RegisteredManagedNativeFunctions.Reset();
    Super::PostLoad();
    if (IsInGameThread())
    {
        BindManagedFunctions();
    }
}

void UFinareBlueprintGeneratedClass::PostLoadDefaultObject(UObject* Object)
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

void UFinareBlueprintGeneratedClass::PostInitInstance(
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
        for (const TPair<FName, FFinareManagedFunctionBinding>& Pair :
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

void UFinareBlueprintGeneratedClass::BindManagedFunctions()
{
    check(IsInGameThread());
    RegisterManagedNativeFunctions();
    bool bAllFunctionsBound = true;
    for (const TPair<FName, FFinareManagedFunctionBinding>& Pair :
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
            &UFinareBlueprintGeneratedClass::execDispatchManagedFunction);
        Function->FunctionFlags |= FinareRpcFlags(Pair.Value.RpcTarget, Pair.Value.bReliable);
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

bool UFinareBlueprintGeneratedClass::BuildBlueprintOverrideActivationContract(
    FString& OutJson,
    FString& OutError) const
{
    using namespace UE::Finare::Blueprint::Private;
    check(IsInGameThread());
    OutJson.Reset();
    OutError.Reset();

    TArray<FName> OverrideNames;
    for (const TPair<FName, FFinareManagedFunctionBinding>& Pair :
         ManagedFunctionBindings)
    {
        const FFinareManagedFunctionBinding& Binding = Pair.Value;
        const bool bHasDeclaringPath =
            !Binding.BlueprintOverrideDeclaringTypePath.IsEmpty();
        const bool bHasFunctionName = !Binding.BlueprintOverrideFunctionName.IsEmpty();
        if (bHasDeclaringPath != bHasFunctionName)
        {
            OutError = FString::Printf(
                TEXT("Managed binding '%s' has an incomplete Blueprint override route."),
                *Pair.Key.ToString());
            return false;
        }
        if (bHasDeclaringPath)
        {
            OverrideNames.Add(Pair.Key);
        }
    }
    OverrideNames.Sort(
        [this](const FName& Left, const FName& Right)
        {
            const FFinareManagedFunctionBinding& A =
                ManagedFunctionBindings.FindChecked(Left);
            const FFinareManagedFunctionBinding& B =
                ManagedFunctionBindings.FindChecked(Right);
            const FString ARoute = A.BlueprintOverrideDeclaringTypePath +
                TEXT("\n") + A.BlueprintOverrideFunctionName;
            const FString BRoute = B.BlueprintOverrideDeclaringTypePath +
                TEXT("\n") + B.BlueprintOverrideFunctionName;
            if (ARoute != BRoute) return ARoute < BRoute;
            return A.ManagedName < B.ManagedName;
        });

    TArray<TSharedPtr<FJsonValue>> Routes;
    TArray<FString> SeenDeclaringPaths;
    TArray<FString> SeenFunctionNames;
    TArray<FString> SeenManagedNames;
    for (const FName& InternalName : OverrideNames)
    {
        const FFinareManagedFunctionBinding& Binding =
            ManagedFunctionBindings.FindChecked(InternalName);
        const FString& DeclaringTypePath =
            Binding.BlueprintOverrideDeclaringTypePath;
        const FString& OverrideFunctionName = Binding.BlueprintOverrideFunctionName;
        if (!Binding.bBlueprintEvent || Binding.bStatic ||
            Binding.RpcTarget != EFinareRpcTarget::None ||
            Binding.bInterfaceFunction || !Binding.RepNotifyProperty.IsNone() ||
            Binding.BlueprintEventSuperMode !=
                EFinareBlueprintEventSuperMode::Explicit)
        {
            OutError = FString::Printf(
                TEXT("Blueprint override binding '%s' has conflicting flags; it must be an instance Explicit Blueprint event without interface, RPC, or RepNotify routes."),
                *Binding.ManagedName);
            return false;
        }
        if (Binding.AbiVersion != 3)
        {
            OutError = FString::Printf(
                TEXT("Blueprint override binding '%s' uses ABI version %d; activation requires persisted ABI v3."),
                *Binding.ManagedName,
                Binding.AbiVersion);
            return false;
        }
        if (Binding.ManagedName.IsEmpty())
        {
            OutError = FString::Printf(
                TEXT("Blueprint override binding '%s' has no managed method name."),
                *InternalName.ToString());
            return false;
        }
        for (int32 Index = 0; Index < SeenDeclaringPaths.Num(); ++Index)
        {
            if (SeenDeclaringPaths[Index].Equals(DeclaringTypePath, ESearchCase::IgnoreCase) &&
                SeenFunctionNames[Index].Equals(OverrideFunctionName, ESearchCase::IgnoreCase))
            {
                OutError = FString::Printf(
                    TEXT("Blueprint override route '%s:%s' is duplicated by managed method '%s'."),
                    *DeclaringTypePath,
                    *OverrideFunctionName,
                    *Binding.ManagedName);
                return false;
            }
        }
        if (SeenManagedNames.ContainsByPredicate(
                [&Binding](const FString& Name)
                {
                    return Name == Binding.ManagedName;
                }))
        {
            OutError = FString::Printf(
                TEXT("Blueprint override managed method '%s' is duplicated."),
                *Binding.ManagedName);
            return false;
        }
        SeenDeclaringPaths.Add(DeclaringTypePath);
        SeenFunctionNames.Add(OverrideFunctionName);
        SeenManagedNames.Add(Binding.ManagedName);

        UFunction* LocalFunction = FindFunctionByName(
            InternalName,
            EIncludeSuperFlag::ExcludeSuper);
        if (LocalFunction == nullptr ||
            LocalFunction->GetOuterUClass() != this ||
            !LocalFunction->HasAnyFunctionFlags(FUNC_BlueprintEvent) ||
            LocalFunction->HasAnyFunctionFlags(FUNC_Static | FUNC_Net | FUNC_Final))
        {
            OutError = FString::Printf(
                TEXT("Blueprint override '%s' has no valid local Blueprint event '%s' on generated class '%s'."),
                *Binding.ManagedName,
                *InternalName.ToString(),
                *GetPathName());
            return false;
        }

        UClass* DeclaringClass = FSoftClassPath(DeclaringTypePath).TryLoadClass<UObject>();
        if (DeclaringClass == nullptr ||
            this == DeclaringClass ||
            !IsChildOf(DeclaringClass))
        {
            OutError = FString::Printf(
                TEXT("Blueprint override '%s' declaring class '%s' is not an exact ancestor of '%s'."),
                *Binding.ManagedName,
                *DeclaringTypePath,
                *GetPathName());
            return false;
        }
        if (InternalName != FName(*OverrideFunctionName))
        {
            OutError = FString::Printf(
                TEXT("Blueprint override '%s' must use inherited event name '%s' as its local function name, but the binding key is '%s'."),
                *Binding.ManagedName,
                *OverrideFunctionName,
                *InternalName.ToString());
            return false;
        }
        UFunction* ParentFunction = DeclaringClass->FindFunctionByName(
            FName(*OverrideFunctionName),
            EIncludeSuperFlag::ExcludeSuper);
        if (ParentFunction == nullptr ||
            ParentFunction->GetOuterUClass() != DeclaringClass ||
            !ParentFunction->HasAnyFunctionFlags(FUNC_BlueprintEvent) ||
            ParentFunction->HasAnyFunctionFlags(FUNC_Final | FUNC_Static | FUNC_Net))
        {
            OutError = FString::Printf(
                TEXT("Blueprint override '%s' target '%s:%s' is not a directly declared instance Blueprint event."),
                *Binding.ManagedName,
                *DeclaringTypePath,
                *OverrideFunctionName);
            return false;
        }

        if (Binding.ParameterTypes.Num() != Binding.ParameterNames.Num() ||
            Binding.ParameterTypes.Num() != Binding.ParameterDirections.Num() ||
            Binding.ParameterTypes.Num() > 4)
        {
            OutError = FString::Printf(
                TEXT("Blueprint override '%s' has inconsistent or unsupported persisted parameter metadata."),
                *Binding.ManagedName);
            return false;
        }

        TArray<const FProperty*> LocalParameters;
        for (TFieldIterator<FProperty> It(
                 LocalFunction,
                 EFieldIteratorFlags::ExcludeSuper);
             It;
             ++It)
        {
            if (It->HasAnyPropertyFlags(CPF_Parm) &&
                !It->HasAnyPropertyFlags(CPF_ReturnParm))
            {
                LocalParameters.Add(*It);
            }
        }
        TArray<const FProperty*> ParentParameters;
        for (TFieldIterator<FProperty> It(
                 ParentFunction,
                 EFieldIteratorFlags::ExcludeSuper);
             It;
             ++It)
        {
            if (It->HasAnyPropertyFlags(CPF_Parm) &&
                !It->HasAnyPropertyFlags(CPF_ReturnParm))
            {
                ParentParameters.Add(*It);
            }
        }
        if (LocalParameters.Num() != Binding.ParameterTypes.Num() ||
            ParentParameters.Num() != Binding.ParameterTypes.Num())
        {
            OutError = FString::Printf(
                TEXT("Blueprint override '%s' persisted %d parameters, local function has %d, parent has %d."),
                *Binding.ManagedName,
                Binding.ParameterTypes.Num(),
                LocalParameters.Num(),
                ParentParameters.Num());
            return false;
        }

        auto FindParameter = [](const TArray<const FProperty*>& Parameters, FName Name)
            -> const FProperty*
        {
            for (const FProperty* Parameter : Parameters)
            {
                if (Parameter != nullptr && Parameter->GetFName() == Name)
                {
                    return Parameter;
                }
            }
            return nullptr;
        };
        TArray<TSharedPtr<FJsonValue>> Parameters;
        TSet<FName> SeenParameterNames;
        for (int32 Index = 0; Index < Binding.ParameterTypes.Num(); ++Index)
        {
            if (Binding.ParameterNames[Index].IsNone() ||
                SeenParameterNames.Contains(Binding.ParameterNames[Index]))
            {
                OutError = FString::Printf(
                    TEXT("Blueprint override '%s' contains a duplicate or empty persisted native parameter name at index %d."),
                    *Binding.ManagedName,
                    Index);
                return false;
            }
            SeenParameterNames.Add(Binding.ParameterNames[Index]);
            const FProperty* LocalParameter = FindParameter(
                LocalParameters,
                Binding.ParameterNames[Index]);
            const FProperty* ParentParameter = FindParameter(
                ParentParameters,
                Binding.ParameterNames[Index]);
            if (LocalParameter == nullptr || ParentParameter == nullptr)
            {
                OutError = FString::Printf(
                    TEXT("Blueprint override '%s' persisted parameter '%s' is missing from the local or inherited function."),
                    *Binding.ManagedName,
                    *Binding.ParameterNames[Index].ToString());
                return false;
            }
            EFinareManagedParameterDirection LocalDirection;
            EFinareManagedParameterDirection ParentDirection;
            if (!GetNativeParameterDirection(*LocalParameter, LocalDirection) ||
                !GetNativeParameterDirection(*ParentParameter, ParentDirection) ||
                LocalDirection != ParentDirection ||
                Binding.ParameterDirections[Index] != ParentDirection)
            {
                OutError = FString::Printf(
                    TEXT("Blueprint override '%s' parameter %d direction does not match its persisted native contract."),
                    *Binding.ManagedName,
                    Index);
                return false;
            }
            if (Binding.ParameterNames[Index].IsNone() ||
                Binding.ParameterNames[Index] != LocalParameter->GetFName() ||
                Binding.ParameterNames[Index] != ParentParameter->GetFName())
            {
                OutError = FString::Printf(
                    TEXT("Blueprint override '%s' parameter %d native name does not match the persisted binding."),
                    *Binding.ManagedName,
                    Index);
                return false;
            }
            TSharedPtr<FJsonObject> LocalShape;
            if (!BuildValidatedActivationShape(
                    *LocalParameter,
                    Binding.ParameterTypes[Index],
                    LocalShape,
                    OutError) ||
                !LocalParameter->SameType(ParentParameter))
            {
                if (OutError.IsEmpty())
                {
                    OutError = FString::Printf(
                        TEXT("Blueprint override '%s' parameter '%s' local and inherited native properties differ."),
                        *Binding.ManagedName,
                        *Binding.ParameterNames[Index].ToString());
                }
                return false;
            }
            TSharedPtr<FJsonObject> Parameter = MakeShared<FJsonObject>();
            Parameter->SetStringField(
                TEXT("direction"),
                ParentDirection == EFinareManagedParameterDirection::Input
                    ? TEXT("in")
                    : ParentDirection == EFinareManagedParameterDirection::InputOutput
                        ? TEXT("ref")
                        : TEXT("out"));
            Parameter->SetObjectField(TEXT("type"), LocalShape);
            Parameters.Add(MakeShared<FJsonValueObject>(Parameter));
        }

        TSharedPtr<FJsonObject> LocalReturn;
        TSharedPtr<FJsonObject> ParentReturn;
        const FProperty* LocalReturnProperty = LocalFunction->GetReturnProperty();
        const FProperty* ParentReturnProperty = ParentFunction->GetReturnProperty();
        if ((LocalReturnProperty == nullptr) != (ParentReturnProperty == nullptr) ||
            (LocalReturnProperty == nullptr &&
                (!Binding.ReturnType.UnrealTypePath.IsEmpty() ||
                 !Binding.ReturnType.StructFields.IsEmpty())))
        {
            OutError = FString::Printf(
                TEXT("Blueprint override '%s' local, inherited, and persisted return shapes differ."),
                *Binding.ManagedName);
            return false;
        }
        if (LocalReturnProperty != nullptr)
        {
            if (!BuildValidatedActivationShape(
                    *LocalReturnProperty,
                    Binding.ReturnType,
                    LocalReturn,
                    OutError) ||
                !LocalReturnProperty->SameType(ParentReturnProperty))
            {
                if (OutError.IsEmpty())
                {
                    OutError = FString::Printf(
                        TEXT("Blueprint override '%s' local and inherited native return properties differ."),
                        *Binding.ManagedName);
                }
                return false;
            }
        }
        else
        {
            ParentReturn = MakeActivationShape(TEXT("void"), TEXT(""), TEXT(""));
        }

        TSharedPtr<FJsonObject> Route = MakeShared<FJsonObject>();
        Route->SetStringField(TEXT("managedName"), Binding.ManagedName);
        Route->SetStringField(TEXT("declaringClassPath"), DeclaringTypePath);
        Route->SetStringField(TEXT("functionName"), OverrideFunctionName);
        Route->SetStringField(TEXT("superMode"), TEXT("explicit"));
        Route->SetObjectField(TEXT("returnType"), LocalReturn != nullptr
            ? LocalReturn
            : ParentReturn);
        Route->SetArrayField(TEXT("parameters"), MoveTemp(Parameters));
        Routes.Add(MakeShared<FJsonValueObject>(Route));
    }
    return SerializeActivationContract(Routes, OutJson, OutError);
}

void UFinareBlueprintGeneratedClass::PrepareManagedFunctionsForRecompile()
{
    check(IsInGameThread());
    // FBlueprintCompileReinstancer duplicates the old class before its native
    // lookup table is rebuilt. Keep the transition copy on ordinary Blueprint
    // bytecode until its generated-class Bind pass installs the dynamic table.
    for (const TPair<FName, FFinareManagedFunctionBinding>& Pair :
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

void UFinareBlueprintGeneratedClass::RegisterManagedNativeFunctions()
{
    for (const TPair<FName, FFinareManagedFunctionBinding>& Pair :
         ManagedFunctionBindings)
    {
        if (RegisteredManagedNativeFunctions.Contains(Pair.Key))
        {
            continue;
        }
        AddNativeFunction(
            *Pair.Key.ToString(),
            &UFinareBlueprintGeneratedClass::execDispatchManagedFunction);
        RegisteredManagedNativeFunctions.Add(Pair.Key);
    }
}

bool UFinareBlueprintGeneratedClass::IsManagedFunctionBound(
    FName FunctionName) const
{
    const FFinareManagedFunctionBinding* Binding =
        ManagedFunctionBindings.Find(FunctionName);
    const UFunction* Function = FindFunctionByName(
        FunctionName,
        EIncludeSuperFlag::ExcludeSuper);
    if (Binding == nullptr || Function == nullptr ||
        !Function->HasAnyFunctionFlags(FUNC_Native) ||
        Function->GetNativeFunc() !=
            &UFinareBlueprintGeneratedClass::execDispatchManagedFunction)
    {
        return false;
    }
    if (Binding->RpcTarget != EFinareRpcTarget::None &&
        !Function->HasAllFunctionFlags(FinareRpcFlags(Binding->RpcTarget, Binding->bReliable))) return false;
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

const FFinareManagedFunctionBinding*
UFinareBlueprintGeneratedClass::FindManagedOverrideBinding(uint64 MemberId) const
{
    for (const TPair<FName, FFinareManagedFunctionBinding>& Pair :
         ManagedFunctionBindings)
    {
        const FFinareManagedFunctionBinding& Binding = Pair.Value;
        if (Binding.BlueprintOverrideDeclaringTypePath.IsEmpty() ||
            Binding.BlueprintOverrideFunctionName.IsEmpty())
        {
            continue;
        }
        const FString MemberPath = Binding.BlueprintOverrideDeclaringTypePath +
            TEXT(":") + Binding.BlueprintOverrideFunctionName;
        const FTCHARToUTF8 Utf8(*MemberPath);
        if (FinareMemberId(Utf8.Get()) == MemberId)
        {
            return &Binding;
        }
    }
    return nullptr;
}

DEFINE_FUNCTION(UFinareBlueprintGeneratedClass::execDispatchManagedFunction)
{
    using namespace UE::Finare::Blueprint::Private;
    // Stack.Node is the frame that owns the currently executing bytecode. For
    // an explicit Blueprint Call Parent Function that is the child ubergraph,
    // not the managed parent UFunction selected by EX_FinalFunction.
    UFunction* Function = Stack.CurrentNativeFunction;
    const UFinareBlueprintGeneratedClass* GeneratedClass = Function != nullptr
        ? Cast<UFinareBlueprintGeneratedClass>(Function->GetOuter())
        : nullptr;
    const FFinareManagedFunctionBinding* Binding = GeneratedClass != nullptr
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
                const EFinareManagedParameterDirection Direction =
                    Binding != nullptr &&
                        Binding->ParameterDirections.IsValidIndex(BindingParameterIndex)
                    ? Binding->ParameterDirections[BindingParameterIndex]
                    : Parameter->HasAnyPropertyFlags(CPF_OutParm)
                        ? EFinareManagedParameterDirection::Output
                        : EFinareManagedParameterDirection::Input;
                if (Direction != EFinareManagedParameterDirection::Input &&
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
            TEXT("Finare managed function binding is missing: function='%s', owner='%s', ownerType='%s', flags=0x%08x, super='%s'."),
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
        UFinareComponent* Component = nullptr;
        UFinareWorldSubsystem* Host = nullptr;
        bool bConfiguredPreStartOwner = false;
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
            FString OwnershipError;
            EManagedComponentResolution ActiveResolution =
                EManagedComponentResolution::None;
            Component = ResolveComponent(
                Context,
                GeneratedClass,
                OwnershipError,
                ActiveResolution);
            // RepNotify functions can be delivered before StartScript. That
            // one path is allowed to resolve the configured owner, while all
            // ordinary dispatch remains active-owner-only.
            if (Component == nullptr &&
                ActiveResolution == EManagedComponentResolution::None &&
                !Binding->RepNotifyProperty.IsNone())
            {
                Component = ResolveConfiguredComponent(
                    Context,
                    GeneratedClass,
                    OwnershipError);
                bConfiguredPreStartOwner = Component != nullptr;
            }
            if (Component == nullptr)
            {
                UE_LOG(
                    LogTemp,
                    Error,
                    TEXT("Finare function '%s' has no exact managed owner: %s"),
                    *Function->GetPathName(),
                    *OwnershipError);
                return;
            }
            Host = Component != nullptr
                ? ResolveWorldHost(Component)
                : nullptr;
        }
        if (Host == nullptr || (!Binding->bStatic && Component == nullptr))
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("Finare function '%s' has no managed World host."),
                *Function->GetPathName());
            return;
        }

        if (Component && !Binding->RepNotifyProperty.IsNone())
        {
            if (Component->QueueInitialRepNotify(
                    Context,
                    Function,
                    ParameterContainer))
            {
                return;
            }
            // A configured owner is eligible only for the pre-start queue.
            // If the queue rejects this callback, do not invoke managed code
            // on a component that has not started its generation.
            if (bConfiguredPreStartOwner)
            {
                return;
            }
        }

        TArray<FFinareNativeValueV3, TInlineAllocator<4>> ArgumentsV2;
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
                        TEXT("Finare function '%s' cannot resolve managed parameter '%s'."),
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
                TEXT("Finare function '%s' resolved %d managed parameters; expected %d."),
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
            FFinareNativeValueV3& Value = ArgumentsV2.AddDefaulted_GetRef();
            const FFinareManagedValueTypeBinding* ParameterType =
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
                    TEXT("Finare cannot marshal Blueprint parameter '%s' for '%s': %s"),
                    *Parameter->GetName(),
                    *Function->GetPathName(),
                    MarshalError.IsEmpty() ? TEXT("unsupported call shape") : *MarshalError);
                return;
            }
        }
        const uint32 InputByteCount = static_cast<uint32>(Arena.Num());
        Arena.SetNumUninitialized(MaximumManagedFunctionArenaBytes, EAllowShrinking::No);
        uint32 ExpectedResultCount = ReturnProperty != nullptr ? 1u : 0u;
        for (EFinareManagedParameterDirection Direction :
             Binding->ParameterDirections)
        {
            if (Direction != EFinareManagedParameterDirection::Input)
            {
                ++ExpectedResultCount;
            }
        }
        TArray<FFinareNativeValueV3, TInlineAllocator<5>> ResultsV2;
        ResultsV2.SetNumZeroed(ExpectedResultCount);
        FFinareNativeCallFrameV3 Frame;
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
            UFinareAsset* ScriptAsset = GeneratedClass->ManagedScriptAsset;
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
                    TEXT("Finare static function '%s' cannot resolve its Script Asset: %s"),
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
        if (!bInvoked || Frame.Status != EFinareNativeCallStatusV3::Success)
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("Finare function '%s' ABI v%d dispatch failed (status=%u): %s"),
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
                TEXT("Finare function '%s' returned %u result cells; expected %u."),
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
                TEXT("Finare cannot marshal ABI v%d return value for '%s': %s"),
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
            const EFinareManagedParameterDirection Direction =
                Binding->ParameterDirections.IsValidIndex(OutputParameterIndex)
                ? Binding->ParameterDirections[OutputParameterIndex]
                : EFinareManagedParameterDirection::Input;
            if (Direction != EFinareManagedParameterDirection::Input)
            {
                void* ParameterValue =
                    Parameter->ContainerPtrToValuePtr<void>(ResultContainer);
                const FFinareManagedValueTypeBinding* ParameterType =
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
                        TEXT("Finare cannot marshal ABI v3 output '%s' for '%s': %s"),
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
            const EFinareManagedParameterDirection Direction =
                Binding->ParameterDirections.IsValidIndex(Index)
                    ? Binding->ParameterDirections[Index] : EFinareManagedParameterDirection::Input;
            if (Direction != EFinareManagedParameterDirection::Input)
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

    TArray<FFinareManagedFunctionValueV1, TInlineAllocator<4>> Arguments;
    for (TFieldIterator<FProperty> It(Function, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const FProperty* Parameter = *It;
        if (!Parameter->HasAnyPropertyFlags(CPF_Parm) ||
            Parameter->HasAnyPropertyFlags(CPF_ReturnParm))
        {
            continue;
        }
        FFinareManagedFunctionValueV1& Value = Arguments.AddDefaulted_GetRef();
        if (Arguments.Num() > 4 ||
            !PackValue(*Parameter, ParameterContainer, Value))
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("Finare cannot marshal Blueprint parameter '%s' for '%s'."),
                *Parameter->GetName(),
                *Function->GetPathName());
            return;
        }
    }

    FString OwnershipError;
    EManagedComponentResolution ActiveResolution =
        EManagedComponentResolution::None;
    UFinareComponent* Component = ResolveComponent(
        Context,
        GeneratedClass,
        OwnershipError,
        ActiveResolution);
    if (Component == nullptr)
    {
        UE_LOG(
            LogTemp,
            Error,
            TEXT("Finare function '%s' has no exact managed owner: %s"),
            *Function->GetPathName(),
            *OwnershipError);
        return;
    }
    FFinareManagedFunctionValueV1 Result;
    FString Error;
    if (!Component->InvokeManagedScriptFunction(
            Binding->ManagedName,
            Arguments,
            Result,
            Error))
    {
        UE_LOG(LogTemp, Error, TEXT("Finare function '%s' failed: %s"),
            *Function->GetPathName(), *Error);
        return;
    }
    if (ReturnProperty == nullptr)
    {
        if (Result.Kind != EFinareManagedFunctionValueKindV1::Void)
        {
            UE_LOG(LogTemp, Error, TEXT("Finare void function '%s' returned kind %u."),
                *Function->GetPathName(), static_cast<uint32>(Result.Kind));
        }
        return;
    }
    if (RESULT_PARAM == nullptr || !UnpackReturnValue(*ReturnProperty, Result, RESULT_PARAM))
    {
        UE_LOG(LogTemp, Error, TEXT("Finare cannot marshal return value for '%s'."),
            *Function->GetPathName());
    }
}

/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 17:37:05
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-24 19:01:04
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Private\UnifiedScriptDeclarationMaterializer.cpp
 * @Description:
 */
#include "UnifiedScriptDeclarationMaterializer.h"
#include "UnifiedScriptClassBinding.h"
#include "UnifiedScriptSoftObjectPath.h"

#include "Containers/StringConv.h"
#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Math/Quat.h"
#include "Math/Rotator.h"
#include "Math/Transform.h"
#include "Math/Vector.h"
#include "Misc/FileHelper.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "StructUtils/PropertyBagMapRef.h"
#include "UnifiedScriptCompiledChunk.h"
#include "UObject/SoftObjectPath.h"
#include "UObject/TextProperty.h"

namespace UE::UnifiedScript::Declaration::Private
{
constexpr TCHAR LegacySchemaVersion[] = TEXT("unifiedscript.script-asset/v0.1");
constexpr TCHAR OverrideSchemaVersion[] = TEXT("unifiedscript.script-asset/v0.2");
constexpr TCHAR ClassBindingSchemaVersion[] = TEXT("unifiedscript.script-asset/v0.3");

struct FStateUnrealType
{
    bool bPresent = false;
    uint8 Kind = 0;
    FString Path;
    FString NativeName;
    TArray<FString> FieldNativeNames;
    TArray<FString> FieldMetadataNames;
    TArray<FStateUnrealType> FieldTypes;
    TArray<FString> ArgumentMetadataNames;
    TArray<FStateUnrealType> TypeArguments;
};

bool ReadJsonNumberToken(const TSharedPtr<FJsonValue>& Value, FString& OutToken)
{
    return Value.IsValid() && Value->Type == EJson::Number &&
        Value->TryGetString(OutToken) && !OutToken.IsEmpty();
}

bool IsJsonIntegerToken(const FString& Token)
{
    int32 Start = 0;
    if (Token.StartsWith(TEXT("-")))
    {
        Start = 1;
    }
    if (Start == Token.Len())
    {
        return false;
    }
    if (Token[Start] == TEXT('0') && Token.Len() > Start + 1)
    {
        return false;
    }
    for (int32 Index = Start; Index < Token.Len(); ++Index)
    {
        if (Token[Index] < TEXT('0') || Token[Index] > TEXT('9'))
        {
            return false;
        }
    }
    return true;
}

bool ParseJsonInt64(const TSharedPtr<FJsonValue>& Value, int64& OutValue)
{
    FString Token;
    if (!ReadJsonNumberToken(Value, Token) || !IsJsonIntegerToken(Token))
    {
        return false;
    }
    const bool bNegative = Token.StartsWith(TEXT("-"));
    const int32 Start = bNegative ? 1 : 0;
    uint64 Magnitude = 0;
    const uint64 Limit = bNegative ? (static_cast<uint64>(MAX_int64) + 1ull) :
        static_cast<uint64>(MAX_int64);
    for (int32 Index = Start; Index < Token.Len(); ++Index)
    {
        const uint64 Digit = static_cast<uint64>(Token[Index] - TEXT('0'));
        if (Magnitude > (Limit - Digit) / 10ull)
        {
            return false;
        }
        Magnitude = Magnitude * 10ull + Digit;
    }
    if (bNegative)
    {
        if (Magnitude == static_cast<uint64>(MAX_int64) + 1ull)
        {
            OutValue = MIN_int64;
        }
        else
        {
            OutValue = -static_cast<int64>(Magnitude);
        }
    }
    else
    {
        OutValue = static_cast<int64>(Magnitude);
    }
    return true;
}

bool ParseJsonDouble(const TSharedPtr<FJsonValue>& Value, double& OutValue)
{
    FString Token;
    return ReadJsonNumberToken(Value, Token) && LexTryParseString(OutValue, *Token) &&
        FMath::IsFinite(OutValue);
}

bool ParseSemanticId(const FString& SemanticId, FGuid& OutId)
{
    FString Digits = SemanticId;
    if (!Digits.RemoveFromStart(TEXT("us_")) || Digits.Len() != 32)
    {
        return false;
    }
    return FGuid::ParseExact(Digits, EGuidFormats::Digits, OutId);
}

bool HasBlueprintOverrideMetadata(
    const TArray<TSharedPtr<FJsonValue>>& Assets)
{
    for (const TSharedPtr<FJsonValue>& AssetValue : Assets)
    {
        const TSharedPtr<FJsonObject> Asset = AssetValue.IsValid()
            ? AssetValue->AsObject()
            : nullptr;
        const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
        if (!Asset.IsValid() ||
            !Asset->TryGetArrayField(TEXT("functions"), Functions) ||
            Functions == nullptr)
        {
            continue;
        }
        for (const TSharedPtr<FJsonValue>& FunctionValue : *Functions)
        {
            const TSharedPtr<FJsonObject> Function = FunctionValue.IsValid()
                ? FunctionValue->AsObject()
                : nullptr;
            const TSharedPtr<FJsonObject>* BlueprintEvent = nullptr;
            const TSharedPtr<FJsonObject>* Override = nullptr;
            if (Function.IsValid() &&
                Function->TryGetObjectField(TEXT("blueprintEvent"), BlueprintEvent) &&
                BlueprintEvent != nullptr && BlueprintEvent->IsValid() &&
                (*BlueprintEvent)->TryGetObjectField(TEXT("override"), Override) &&
                Override != nullptr && Override->IsValid())
            {
                return true;
            }
        }
    }
    return false;
}

bool IsLegalEnumValue(const UEnum* Enum, int64 Value)
{
    if (Enum == nullptr)
    {
        return false;
    }
    for (int32 Index = 0; Index < Enum->NumEnums(); ++Index)
    {
        if (Enum->GetValueByIndex(Index) == Value)
        {
            return true;
        }
    }
    return false;
}

bool IsLegalPropertyBagEnumValue(const UEnum* Enum, int64 Value)
{
    if (!IsLegalEnumValue(Enum, Value))
    {
        return false;
    }

    // PropertyBag follows UHT here: non enum-class UEnums are represented by
    // FByteProperty, while enum classes retain their declared integral type.
    return Enum->GetCppForm() == UEnum::ECppForm::EnumClass ||
        (Value >= 0 && Value <= 255);
}

bool ResolvePropertyType(
    const FString& Kind,
    const FString& MetadataName,
    const FStateUnrealType& UnrealType,
    EPropertyBagPropertyType& OutType,
    const UObject*& OutTypeObject)
{
    OutTypeObject = nullptr;
    if (Kind == TEXT("boolean"))
    {
        OutType = EPropertyBagPropertyType::Bool;
        return MetadataName == TEXT("System.Boolean");
    }
    if (Kind == TEXT("integer"))
    {
        if (MetadataName == TEXT("System.Byte"))
        {
            OutType = EPropertyBagPropertyType::Byte;
            return true;
        }
        if (MetadataName == TEXT("System.Int64"))
        {
            OutType = EPropertyBagPropertyType::Int64;
            return true;
        }
        if (MetadataName == TEXT("System.Int32"))
        {
            OutType = EPropertyBagPropertyType::Int32;
            return true;
        }
        return false;
    }
    if (Kind == TEXT("floatingPoint"))
    {
        if (MetadataName == TEXT("System.Single"))
        {
            OutType = EPropertyBagPropertyType::Float;
            return true;
        }
        if (MetadataName == TEXT("System.Double"))
        {
            OutType = EPropertyBagPropertyType::Double;
            return true;
        }
        return false;
    }
    if (Kind == TEXT("string") && MetadataName == TEXT("System.String"))
    {
        OutType = EPropertyBagPropertyType::String;
        return true;
    }
    if (Kind == TEXT("enum"))
    {
        if (UnrealType.bPresent)
        {
            if (UnrealType.Kind != 0 || !UnrealType.TypeArguments.IsEmpty())
            {
                return false;
            }
            UEnum* Enum = LoadObject<UEnum>(nullptr, *UnrealType.Path);
            if (Enum == nullptr || Enum->GetPathName() != UnrealType.Path ||
                Enum->GetName() != UnrealType.NativeName)
            {
                return false;
            }
            OutType = EPropertyBagPropertyType::Enum;
            OutTypeObject = Enum;
            return true;
        }
        OutType = EPropertyBagPropertyType::String;
        return MetadataName == TEXT("System.Enum") || !MetadataName.IsEmpty();
    }
    if (Kind == TEXT("name") &&
        MetadataName == TEXT("UnifiedScript.Unreal.UnrealName"))
    {
        OutType = EPropertyBagPropertyType::Name;
        return true;
    }
    if (Kind == TEXT("text") &&
        MetadataName == TEXT("UnifiedScript.Unreal.UnrealText"))
    {
        OutType = EPropertyBagPropertyType::Text;
        return true;
    }
    if (Kind == TEXT("object") &&
        MetadataName == TEXT("UnifiedScript.Unreal.UnrealObjectHandle"))
    {
        OutType = EPropertyBagPropertyType::Object;
        OutTypeObject = UObject::StaticClass();
        return true;
    }
    if (Kind == TEXT("class") &&
        MetadataName == TEXT("UnifiedScript.Unreal.UnrealClassHandle"))
    {
        OutType = EPropertyBagPropertyType::Class;
        OutTypeObject = UObject::StaticClass();
        return true;
    }
    if (Kind == TEXT("softObject") &&
        MetadataName == TEXT("UnifiedScript.Unreal.UnrealSoftObjectHandle") &&
        !UnrealType.bPresent)
    {
        OutType = EPropertyBagPropertyType::SoftObject;
        OutTypeObject = UObject::StaticClass();
        return true;
    }
    if (Kind == TEXT("softClass") &&
        MetadataName == TEXT("UnifiedScript.Unreal.UnrealSoftClassHandle") &&
        !UnrealType.bPresent)
    {
        OutType = EPropertyBagPropertyType::SoftClass;
        OutTypeObject = UObject::StaticClass();
        return true;
    }
    if (Kind == TEXT("struct") &&
        MetadataName == TEXT("UnifiedScript.Unreal.UnrealVector") &&
        UnrealType.bPresent && UnrealType.Kind == 1 &&
        UnrealType.Path == TEXT("/Script/CoreUObject.Vector") &&
        UnrealType.NativeName == TEXT("Vector"))
    {
        OutType = EPropertyBagPropertyType::Struct;
        OutTypeObject = TBaseStructure<FVector>::Get();
        return true;
    }
    if (Kind == TEXT("struct") && MetadataName == TEXT("UnifiedScript.Unreal.UnrealRotator") &&
        UnrealType.bPresent && UnrealType.Kind == 1 &&
        UnrealType.Path == TEXT("/Script/CoreUObject.Rotator") &&
        UnrealType.NativeName == TEXT("Rotator"))
    {
        OutType = EPropertyBagPropertyType::Struct;
        OutTypeObject = TBaseStructure<FRotator>::Get();
        return true;
    }
    if (Kind == TEXT("struct") && MetadataName == TEXT("UnifiedScript.Unreal.UnrealTransform") &&
        UnrealType.bPresent && UnrealType.Kind == 1 &&
        UnrealType.Path == TEXT("/Script/CoreUObject.Transform") &&
        UnrealType.NativeName == TEXT("Transform"))
    {
        OutType = EPropertyBagPropertyType::Struct;
        OutTypeObject = TBaseStructure<FTransform>::Get();
        return true;
    }
    return false;
}

bool ReadJsonUnrealType(
    const TSharedPtr<FJsonObject>& State,
    FStateUnrealType& OutType,
    int32 Depth = 0)
{
    if (Depth > 16)
    {
        return false;
    }
    OutType = {};
    const TSharedPtr<FJsonObject>* TypeObject = nullptr;
    if (!State->TryGetObjectField(TEXT("unrealType"), TypeObject) || TypeObject == nullptr)
    {
        return true;
    }

    FString Kind;
    OutType.bPresent = true;
    if (!(*TypeObject)->TryGetStringField(TEXT("kind"), Kind) ||
        !(*TypeObject)->TryGetStringField(TEXT("unrealTypePath"), OutType.Path) ||
        !(*TypeObject)->TryGetStringField(TEXT("unrealNativeName"), OutType.NativeName))
    {
        return false;
    }
    if (Kind == TEXT("array") || Kind == TEXT("set") || Kind == TEXT("map"))
    {
        const FString ExpectedNativeName = Kind == TEXT("array") ? TEXT("TArray") :
            Kind == TEXT("set") ? TEXT("TSet") : TEXT("TMap");
        const TArray<TSharedPtr<FJsonValue>>* Fields = nullptr;
        const TArray<TSharedPtr<FJsonValue>>* Arguments = nullptr;
        if (!OutType.Path.IsEmpty() || OutType.NativeName != ExpectedNativeName ||
            !(*TypeObject)->TryGetArrayField(TEXT("fields"), Fields) ||
            Fields == nullptr || !Fields->IsEmpty() ||
            !(*TypeObject)->TryGetArrayField(TEXT("typeArguments"), Arguments) ||
            Arguments == nullptr || Arguments->Num() != (Kind == TEXT("map") ? 2 : 1))
        {
            return false;
        }
        OutType.Kind = Kind == TEXT("array") ? 2 : Kind == TEXT("set") ? 3 : 4;
        for (const TSharedPtr<FJsonValue>& ArgumentValue : *Arguments)
        {
            const TSharedPtr<FJsonObject> Argument = ArgumentValue.IsValid()
                ? ArgumentValue->AsObject() : nullptr;
            FString MetadataName;
            if (!Argument.IsValid() ||
                !Argument->TryGetStringField(TEXT("typeMetadataName"), MetadataName) ||
                MetadataName.IsEmpty())
            {
                return false;
            }
            OutType.ArgumentMetadataNames.Add(MetadataName);
            FStateUnrealType Child;
            if (Argument->HasField(TEXT("unrealType")))
            {
                const TSharedPtr<FJsonObject>* ChildObject = nullptr;
                if (!Argument->TryGetObjectField(TEXT("unrealType"), ChildObject) ||
                    ChildObject == nullptr || ChildObject->IsValid() == false)
                {
                    return false;
                }
                // Recurse through the same strict shape rules used by state
                // metadata. The helper expects the owner to contain the field.
                TSharedPtr<FJsonObject> ChildOwner = MakeShared<FJsonObject>();
                ChildOwner->SetObjectField(TEXT("unrealType"), *ChildObject);
                if (!ReadJsonUnrealType(ChildOwner, Child, Depth + 1) ||
                    (Child.bPresent && Child.Kind >= 2))
                {
                    return false;
                }
            }
            OutType.TypeArguments.Add(MoveTemp(Child));
        }
        return true;
    }
    if (Kind == TEXT("enum"))
    {
        if ((*TypeObject)->HasField(TEXT("fields")))
        {
            const TArray<TSharedPtr<FJsonValue>>* Fields = nullptr;
            if (!(*TypeObject)->TryGetArrayField(TEXT("fields"), Fields) ||
                Fields == nullptr || !Fields->IsEmpty())
            {
                return false;
            }
        }
        OutType.Kind = 0;
        return true;
    }
    if (Kind == TEXT("struct"))
    {
        const TArray<TSharedPtr<FJsonValue>>* Fields = nullptr;
        if (!(*TypeObject)->TryGetArrayField(TEXT("fields"), Fields) || Fields == nullptr)
        {
            return false;
        }
        OutType.Kind = 1;
        for (int32 FieldIndex = 0; FieldIndex < Fields->Num(); ++FieldIndex)
        {
            const TSharedPtr<FJsonObject> Field = (*Fields)[FieldIndex].IsValid()
                ? (*Fields)[FieldIndex]->AsObject() : nullptr;
            double DeclaredIndex = -1.0;
            FString NativeName;
            FString MetadataName;
            if (!Field.IsValid() ||
                !Field->TryGetNumberField(TEXT("index"), DeclaredIndex) ||
                DeclaredIndex != static_cast<double>(FieldIndex) ||
                !Field->TryGetStringField(TEXT("nativeName"), NativeName) || NativeName.IsEmpty() ||
                !Field->TryGetStringField(TEXT("typeMetadataName"), MetadataName) || MetadataName.IsEmpty())
            {
                return false;
            }
            OutType.FieldNativeNames.Add(MoveTemp(NativeName));
            OutType.FieldMetadataNames.Add(MoveTemp(MetadataName));
            FStateUnrealType FieldType;
            if (Field->HasField(TEXT("unrealType")))
            {
                TSharedPtr<FJsonObject> FieldOwner = MakeShared<FJsonObject>();
                const TSharedPtr<FJsonObject>* Nested = nullptr;
                if (!Field->TryGetObjectField(TEXT("unrealType"), Nested) ||
                    Nested == nullptr || !Nested->IsValid())
                {
                    return false;
                }
                FieldOwner->SetObjectField(TEXT("unrealType"), *Nested);
                if (!ReadJsonUnrealType(FieldOwner, FieldType, Depth + 1))
                {
                    return false;
                }
            }
            OutType.FieldTypes.Add(MoveTemp(FieldType));
        }
        return true;
    }
    return false;
}

FString InferLeafKind(
    const FString& MetadataName,
    const FStateUnrealType& UnrealType)
{
    if (UnrealType.bPresent && UnrealType.Kind == 0)
    {
        return TEXT("enum");
    }
    if (UnrealType.bPresent && UnrealType.Kind == 1)
    {
        return TEXT("struct");
    }
    if (MetadataName == TEXT("System.Boolean")) return TEXT("boolean");
    if (MetadataName == TEXT("System.Byte") || MetadataName == TEXT("System.Int32") ||
        MetadataName == TEXT("System.Int64")) return TEXT("integer");
    if (MetadataName == TEXT("System.Single") || MetadataName == TEXT("System.Double"))
        return TEXT("floatingPoint");
    if (MetadataName == TEXT("System.String")) return TEXT("string");
    if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealName")) return TEXT("name");
    if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealText")) return TEXT("text");
    if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealObjectHandle")) return TEXT("object");
    if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealClassHandle")) return TEXT("class");
    if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealSoftObjectHandle")) return TEXT("softObject");
    if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealSoftClassHandle")) return TEXT("softClass");
    return FString();
}

bool IsValidCollectionLeafDescriptor(
    const FString& MetadataName,
    const FStateUnrealType& UnrealType)
{
    if (!UnrealType.bPresent)
    {
        return MetadataName == TEXT("System.Boolean") || MetadataName == TEXT("System.Byte") ||
            MetadataName == TEXT("System.Int32") || MetadataName == TEXT("System.Int64") ||
            MetadataName == TEXT("System.Single") || MetadataName == TEXT("System.Double") ||
            MetadataName == TEXT("System.String") ||
            MetadataName == TEXT("UnifiedScript.Unreal.UnrealName") ||
            MetadataName == TEXT("UnifiedScript.Unreal.UnrealText") ||
            MetadataName == TEXT("UnifiedScript.Unreal.UnrealObjectHandle") ||
            MetadataName == TEXT("UnifiedScript.Unreal.UnrealClassHandle") ||
            MetadataName == TEXT("UnifiedScript.Unreal.UnrealSoftObjectHandle") ||
            MetadataName == TEXT("UnifiedScript.Unreal.UnrealSoftClassHandle");
    }
    if (UnrealType.Kind == 0)
    {
        return UnrealType.FieldNativeNames.IsEmpty() && UnrealType.FieldMetadataNames.IsEmpty() &&
            UnrealType.FieldTypes.IsEmpty() && UnrealType.TypeArguments.IsEmpty();
    }
    if (UnrealType.Kind != 1 || !UnrealType.TypeArguments.IsEmpty())
    {
        return false;
    }
    if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealVector"))
    {
        return UnrealType.Path == TEXT("/Script/CoreUObject.Vector") &&
            UnrealType.NativeName == TEXT("Vector") &&
            UnrealType.FieldNativeNames == TArray<FString>{TEXT("X"), TEXT("Y"), TEXT("Z")} &&
            UnrealType.FieldMetadataNames == TArray<FString>{TEXT("System.Double"), TEXT("System.Double"), TEXT("System.Double")} &&
            UnrealType.FieldTypes.Num() == 3 &&
            !UnrealType.FieldTypes[0].bPresent && !UnrealType.FieldTypes[1].bPresent &&
            !UnrealType.FieldTypes[2].bPresent;
    }
    if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealRotator"))
    {
        return UnrealType.Path == TEXT("/Script/CoreUObject.Rotator") &&
            UnrealType.NativeName == TEXT("Rotator") && UnrealType.FieldNativeNames.IsEmpty() &&
            UnrealType.FieldMetadataNames.IsEmpty() && UnrealType.FieldTypes.IsEmpty();
    }
    if (MetadataName == TEXT("UnifiedScript.Unreal.UnrealTransform"))
    {
        return UnrealType.Path == TEXT("/Script/CoreUObject.Transform") &&
            UnrealType.NativeName == TEXT("Transform") && UnrealType.FieldNativeNames.IsEmpty() &&
            UnrealType.FieldMetadataNames.IsEmpty() && UnrealType.FieldTypes.IsEmpty();
    }
    return false;
}

bool ResolveContainerPropertyType(
    const FString& TypeKind,
    const FString& MetadataName,
    const FStateUnrealType& UnrealType,
    FPropertyBagPropertyDesc& OutDesc)
{
    const uint8 ExpectedKind = TypeKind == TEXT("array") ? 2 :
        TypeKind == TEXT("set") ? 3 : TypeKind == TEXT("map") ? 4 : 0;
    const int32 Arity = TypeKind == TEXT("map") ? 2 : 1;
    if (ExpectedKind == 0 || !UnrealType.bPresent || UnrealType.Kind != ExpectedKind ||
        UnrealType.ArgumentMetadataNames.Num() != Arity || UnrealType.TypeArguments.Num() != Arity)
    {
        return false;
    }
    const FString Definition = TypeKind == TEXT("array")
        ? TEXT("System.Collections.Generic.IReadOnlyList")
        : TypeKind == TEXT("set")
            ? TEXT("System.Collections.Generic.IReadOnlySet")
            : TEXT("System.Collections.Generic.IReadOnlyDictionary");
    if (MetadataName != Definition + TEXT("<") +
        FString::Join(UnrealType.ArgumentMetadataNames, TEXT(", ")) + TEXT(">"))
    {
        return false;
    }

    EPropertyBagPropertyType ValueType = EPropertyBagPropertyType::None;
    const UObject* ValueObject = nullptr;
    const FString ValueKind = InferLeafKind(
        UnrealType.ArgumentMetadataNames[TypeKind == TEXT("map") ? 1 : 0],
        UnrealType.TypeArguments[TypeKind == TEXT("map") ? 1 : 0]);
    if (!IsValidCollectionLeafDescriptor(
            UnrealType.ArgumentMetadataNames[TypeKind == TEXT("map") ? 1 : 0],
            UnrealType.TypeArguments[TypeKind == TEXT("map") ? 1 : 0]) ||
        ValueKind.IsEmpty() ||
        !ResolvePropertyType(
            ValueKind,
            UnrealType.ArgumentMetadataNames[TypeKind == TEXT("map") ? 1 : 0],
            UnrealType.TypeArguments[TypeKind == TEXT("map") ? 1 : 0],
            ValueType,
            ValueObject))
    {
        return false;
    }
    if (UnrealType.TypeArguments[TypeKind == TEXT("map") ? 1 : 0].bPresent &&
        UnrealType.TypeArguments[TypeKind == TEXT("map") ? 1 : 0].Kind >= 2)
    {
        return false;
    }

    const EPropertyBagContainerType Container = TypeKind == TEXT("array")
        ? EPropertyBagContainerType::Array
        : TypeKind == TEXT("set") ? EPropertyBagContainerType::Set : EPropertyBagContainerType::Map;
    if (Container != EPropertyBagContainerType::Map)
    {
        OutDesc = FPropertyBagPropertyDesc(FName(), Container, ValueType, ValueObject);
        return true;
    }

    EPropertyBagPropertyType KeyType = EPropertyBagPropertyType::None;
    const UObject* KeyObject = nullptr;
    const FString KeyKind = InferLeafKind(
        UnrealType.ArgumentMetadataNames[0], UnrealType.TypeArguments[0]);
    if (!IsValidCollectionLeafDescriptor(
            UnrealType.ArgumentMetadataNames[0], UnrealType.TypeArguments[0]) ||
        KeyKind.IsEmpty() ||
        !ResolvePropertyType(
            KeyKind,
            UnrealType.ArgumentMetadataNames[0],
            UnrealType.TypeArguments[0],
            KeyType,
            KeyObject) ||
        UnrealType.TypeArguments[0].bPresent && UnrealType.TypeArguments[0].Kind >= 2)
    {
        return false;
    }
    OutDesc = FPropertyBagPropertyDesc(
        FName(),
        EPropertyBagContainerType::Map,
        ValueType,
        ValueObject,
        CPF_Edit,
        KeyType,
        KeyObject);
    return true;
}

struct FScopedPropertyValue
{
    explicit FScopedPropertyValue(FProperty* InProperty)
        : Property(InProperty)
    {
        if (Property != nullptr)
        {
            Value = FMemory::Malloc(Property->GetSize(), Property->GetMinAlignment());
            Property->InitializeValue(Value);
        }
    }

    ~FScopedPropertyValue()
    {
        if (Property != nullptr && Value != nullptr)
        {
            Property->DestroyValue(Value);
            FMemory::Free(Value);
        }
    }

    FProperty* Property = nullptr;
    void* Value = nullptr;
};

bool SetPropertyFromJson(
    FProperty* Property,
    EPropertyBagPropertyType ExpectedType,
    const UObject* TypeObject,
    const FString& MetadataName,
    const FStateUnrealType& UnrealType,
    const TSharedPtr<FJsonValue>& Value,
    void* Destination,
    FString& OutError)
{
    if (Property == nullptr || Destination == nullptr || !Value.IsValid())
    {
        return false;
    }
    if (Value->IsNull())
    {
        if (ExpectedType == EPropertyBagPropertyType::Object ||
            ExpectedType == EPropertyBagPropertyType::Class)
        {
            CastFieldChecked<FObjectPropertyBase>(Property)->SetObjectPropertyValue(
                Destination, nullptr);
            return true;
        }
        OutError = TEXT("Collection defaults cannot contain null for this leaf type.");
        return false;
    }

    if (ExpectedType == EPropertyBagPropertyType::Bool)
    {
        bool Parsed = false;
        if (Value->TryGetBool(Parsed))
        {
            CastFieldChecked<FBoolProperty>(Property)->SetPropertyValue(Destination, Parsed);
            return true;
        }
    }
    else if (ExpectedType == EPropertyBagPropertyType::Byte ||
        ExpectedType == EPropertyBagPropertyType::Int32 ||
        ExpectedType == EPropertyBagPropertyType::Int64)
    {
        int64 Parsed = 0;
        const int64 Minimum = ExpectedType == EPropertyBagPropertyType::Byte
            ? 0 : ExpectedType == EPropertyBagPropertyType::Int32 ? MIN_int32 : MIN_int64;
        const int64 Maximum = ExpectedType == EPropertyBagPropertyType::Byte
            ? MAX_uint8 : ExpectedType == EPropertyBagPropertyType::Int32 ? MAX_int32 : MAX_int64;
        if (ParseJsonInt64(Value, Parsed) && Parsed >= Minimum && Parsed <= Maximum)
        {
            if (ExpectedType == EPropertyBagPropertyType::Byte)
            {
                CastFieldChecked<FByteProperty>(Property)->SetPropertyValue(
                    Destination, static_cast<uint8>(Parsed));
            }
            else if (ExpectedType == EPropertyBagPropertyType::Int32)
            {
                CastFieldChecked<FIntProperty>(Property)->SetPropertyValue(
                    Destination, static_cast<int32>(Parsed));
            }
            else
            {
                CastFieldChecked<FInt64Property>(Property)->SetPropertyValue(Destination, Parsed);
            }
            return true;
        }
    }
    else if (ExpectedType == EPropertyBagPropertyType::Float ||
        ExpectedType == EPropertyBagPropertyType::Double)
    {
        double Parsed = 0.0;
        if (ParseJsonDouble(Value, Parsed))
        {
            if (ExpectedType == EPropertyBagPropertyType::Float)
            {
                const float Converted = static_cast<float>(Parsed);
                if (!FMath::IsFinite(Converted))
                {
                    return false;
                }
                CastFieldChecked<FFloatProperty>(Property)->SetPropertyValue(Destination, Converted);
            }
            else
            {
                CastFieldChecked<FDoubleProperty>(Property)->SetPropertyValue(Destination, Parsed);
            }
            return true;
        }
    }
    else if (ExpectedType == EPropertyBagPropertyType::String && Value->Type == EJson::String)
    {
        CastFieldChecked<FStrProperty>(Property)->SetPropertyValue(Destination, Value->AsString());
        return true;
    }
    else if (ExpectedType == EPropertyBagPropertyType::Name && Value->Type == EJson::String)
    {
        CastFieldChecked<FNameProperty>(Property)->SetPropertyValue(Destination, FName(*Value->AsString()));
        return true;
    }
    else if (ExpectedType == EPropertyBagPropertyType::Text && Value->Type == EJson::String)
    {
        CastFieldChecked<FTextProperty>(Property)->SetPropertyValue(
            Destination, FText::FromString(Value->AsString()));
        return true;
    }
    else if ((ExpectedType == EPropertyBagPropertyType::SoftObject ||
              ExpectedType == EPropertyBagPropertyType::SoftClass) && Value->Type == EJson::String)
    {
        const FString PathText = Value->AsString();
        if (UnifiedScriptSoftObjectPath::IsValid(PathText))
        {
            CastFieldChecked<FSoftObjectProperty>(Property)->SetPropertyValue(
                Destination, FSoftObjectPtr(FSoftObjectPath(PathText)));
            return true;
        }
    }
    else if (ExpectedType == EPropertyBagPropertyType::Enum && Value->Type == EJson::String)
    {
        FString Token = Value->AsString();
        int64 Parsed = 0;
        const UEnum* Enum = Cast<UEnum>(TypeObject);
        if (IsJsonIntegerToken(Token) && LexTryParseString(Parsed, *Token) &&
            IsLegalPropertyBagEnumValue(Enum, Parsed))
        {
            if (const FEnumProperty* EnumProperty = CastField<FEnumProperty>(Property))
            {
                EnumProperty->GetUnderlyingProperty()->SetIntPropertyValue(
                    Destination, Parsed);
            }
            else if (const FByteProperty* ByteProperty = CastField<FByteProperty>(Property))
            {
                ByteProperty->SetPropertyValue(Destination, static_cast<uint8>(Parsed));
            }
            else
            {
                return false;
            }
            return true;
        }
    }
    else if (ExpectedType == EPropertyBagPropertyType::Struct && Value->Type == EJson::Object)
    {
        const TSharedPtr<FJsonObject> Object = Value->AsObject();
        if (TypeObject == TBaseStructure<FVector>::Get() && Object.IsValid() && Object->Values.Num() == 3)
        {
            double X = 0.0, Y = 0.0, Z = 0.0;
            if (ParseJsonDouble(Object->TryGetField(TEXT("x")), X) &&
                ParseJsonDouble(Object->TryGetField(TEXT("y")), Y) &&
                ParseJsonDouble(Object->TryGetField(TEXT("z")), Z))
            {
                const FVector Vector(X, Y, Z);
                CastFieldChecked<FStructProperty>(Property)->CopyCompleteValue(Destination, &Vector);
                return true;
            }
        }
        else if (TypeObject == TBaseStructure<FRotator>::Get() && Object.IsValid() && Object->Values.Num() == 3)
        {
            double Pitch = 0.0, Yaw = 0.0, Roll = 0.0;
            if (ParseJsonDouble(Object->TryGetField(TEXT("pitch")), Pitch) &&
                ParseJsonDouble(Object->TryGetField(TEXT("yaw")), Yaw) &&
                ParseJsonDouble(Object->TryGetField(TEXT("roll")), Roll))
            {
                const FRotator Rotator(Pitch, Yaw, Roll);
                CastFieldChecked<FStructProperty>(Property)->CopyCompleteValue(Destination, &Rotator);
                return true;
            }
        }
        else if (TypeObject == TBaseStructure<FTransform>::Get() && Object.IsValid() && Object->Values.Num() == 3)
        {
            const TSharedPtr<FJsonObject>* Rotation = nullptr;
            const TSharedPtr<FJsonObject>* Translation = nullptr;
            const TSharedPtr<FJsonObject>* Scale = nullptr;
            if (Object->TryGetObjectField(TEXT("rotation"), Rotation) &&
                Object->TryGetObjectField(TEXT("translation"), Translation) &&
                Object->TryGetObjectField(TEXT("scale3D"), Scale) &&
                Rotation != nullptr && Translation != nullptr && Scale != nullptr)
            {
                double X = 0.0, Y = 0.0, Z = 0.0, W = 0.0;
                double TX = 0.0, TY = 0.0, TZ = 0.0;
                double SX = 0.0, SY = 0.0, SZ = 0.0;
                if ((*Rotation)->Values.Num() == 4 && (*Translation)->Values.Num() == 3 && (*Scale)->Values.Num() == 3 &&
                    ParseJsonDouble((*Rotation)->TryGetField(TEXT("x")), X) &&
                    ParseJsonDouble((*Rotation)->TryGetField(TEXT("y")), Y) &&
                    ParseJsonDouble((*Rotation)->TryGetField(TEXT("z")), Z) &&
                    ParseJsonDouble((*Rotation)->TryGetField(TEXT("w")), W) &&
                    ParseJsonDouble((*Translation)->TryGetField(TEXT("x")), TX) &&
                    ParseJsonDouble((*Translation)->TryGetField(TEXT("y")), TY) &&
                    ParseJsonDouble((*Translation)->TryGetField(TEXT("z")), TZ) &&
                    ParseJsonDouble((*Scale)->TryGetField(TEXT("x")), SX) &&
                    ParseJsonDouble((*Scale)->TryGetField(TEXT("y")), SY) &&
                    ParseJsonDouble((*Scale)->TryGetField(TEXT("z")), SZ) &&
                    FMath::IsNearlyEqual(X * X + Y * Y + Z * Z + W * W, 1.0, 1e-6))
                {
                    const FTransform Transform(FQuat(X, Y, Z, W), FVector(TX, TY, TZ), FVector(SX, SY, SZ));
                    CastFieldChecked<FStructProperty>(Property)->CopyCompleteValue(Destination, &Transform);
                    return true;
                }
            }
        }
    }

    OutError = FString::Printf(TEXT("Collection leaf '%s' has an invalid default."), *MetadataName);
    return false;
}

bool AddCollectionLeaf(
    FScriptArrayHelper& Helper,
    FProperty* Property,
    EPropertyBagPropertyType Type,
    const UObject* TypeObject,
    const FString& MetadataName,
    const FStateUnrealType& UnrealType,
    const TSharedPtr<FJsonValue>& Value,
    FString& OutError)
{
    const int32 Index = Helper.AddValue();
    if (!SetPropertyFromJson(Property, Type, TypeObject, MetadataName, UnrealType, Value,
            Helper.GetRawPtr(Index), OutError))
    {
        Helper.RemoveValues(Index);
        return false;
    }
    return true;
}

bool ValidateCollectionHashability(
    const FPropertyBagPropertyDesc& Desc,
    FString& OutError)
{
    if (Desc.ContainerTypes.IsEmpty())
    {
        return true;
    }
    const EPropertyBagContainerType Container = Desc.ContainerTypes.GetFirstContainerType();
    if (Container == EPropertyBagContainerType::Array)
    {
        return true;
    }
    if (Container == EPropertyBagContainerType::Set)
    {
        const FSetProperty* Set = CastField<FSetProperty>(Desc.CachedProperty);
        if (Set == nullptr || Set->ElementProp == nullptr ||
            !Set->ElementProp->HasAllPropertyFlags(CPF_HasGetValueTypeHash))
        {
            OutError = FString::Printf(
                TEXT("Collection state '%s' uses an element type that Unreal cannot hash."),
                *Desc.Name.ToString());
            return false;
        }
        return true;
    }
    if (Container == EPropertyBagContainerType::Map)
    {
        const FMapProperty* Map = CastField<FMapProperty>(Desc.CachedProperty);
        if (Map == nullptr || Map->KeyProp == nullptr ||
            !Map->KeyProp->HasAllPropertyFlags(CPF_HasGetValueTypeHash))
        {
            OutError = FString::Printf(
                TEXT("Collection state '%s' uses a map key type that Unreal cannot hash."),
                *Desc.Name.ToString());
            return false;
        }
    }
    return true;
}

bool SetCollectionJsonDefault(
    FInstancedPropertyBag& Bag,
    const FPropertyBagPropertyDesc& Desc,
    const FStateUnrealType& UnrealType,
    const TSharedPtr<FJsonValue>& DefaultValue,
    FString& OutError)
{
    const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
    if (!DefaultValue.IsValid() || !DefaultValue->TryGetArray(Items) || Items == nullptr ||
        UnrealType.TypeArguments.Num() != (Desc.ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Map ? 2 : 1))
    {
        OutError = FString::Printf(TEXT("Collection state '%s' requires an array default."), *Desc.Name.ToString());
        return false;
    }
    const int32 ValueArgumentIndex = Desc.ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Map ? 1 : 0;
    const FString& ValueMetadata = UnrealType.ArgumentMetadataNames[ValueArgumentIndex];
    const FStateUnrealType& ValueUnrealType = UnrealType.TypeArguments[ValueArgumentIndex];
    if (Desc.ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Array)
    {
        const FArrayProperty* Array = CastField<FArrayProperty>(Desc.CachedProperty);
        FScriptArrayHelper Helper(
            Array,
            Desc.CachedProperty->ContainerPtrToValuePtr<void>(Bag.GetMutableValue().GetMemory()));
        for (const TSharedPtr<FJsonValue>& Item : *Items)
        {
            if (!AddCollectionLeaf(Helper, Array->Inner, Desc.ValueType, Desc.ValueTypeObject,
                    ValueMetadata, ValueUnrealType, Item, OutError)) return false;
        }
        return true;
    }
    if (Desc.ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Set)
    {
        const FSetProperty* Set = CastField<FSetProperty>(Desc.CachedProperty);
        FScriptSetHelper Helper(
            Set,
            Desc.CachedProperty->ContainerPtrToValuePtr<void>(Bag.GetMutableValue().GetMemory()));
        for (const TSharedPtr<FJsonValue>& Item : *Items)
        {
            FScopedPropertyValue Candidate(Set->ElementProp);
            if (!SetPropertyFromJson(Set->ElementProp, Desc.ValueType, Desc.ValueTypeObject,
                    ValueMetadata, ValueUnrealType, Item, Candidate.Value, OutError)) return false;
            if (Helper.FindElementIndex(Candidate.Value) != INDEX_NONE)
            {
                OutError = FString::Printf(TEXT("Collection state '%s' contains a duplicate set value."), *Desc.Name.ToString());
                return false;
            }
            const int32 Index = Helper.AddDefaultValue_Invalid_NeedsRehash();
            Set->ElementProp->CopyCompleteValue(Helper.GetElementPtr(Index), Candidate.Value);
        }
        Helper.Rehash();
        return true;
    }
    const FMapProperty* Map = CastField<FMapProperty>(Desc.CachedProperty);
    FScriptMapHelper Helper(
        Map,
        Desc.CachedProperty->ContainerPtrToValuePtr<void>(Bag.GetMutableValue().GetMemory()));
    const FString& KeyMetadata = UnrealType.ArgumentMetadataNames[0];
    const FStateUnrealType& KeyUnrealType = UnrealType.TypeArguments[0];
    for (const TSharedPtr<FJsonValue>& Item : *Items)
    {
        const TSharedPtr<FJsonObject> Object = Item.IsValid() ? Item->AsObject() : nullptr;
        const TSharedPtr<FJsonValue> Key = Object.IsValid() ? Object->TryGetField(TEXT("key")) : nullptr;
        const TSharedPtr<FJsonValue> Value = Object.IsValid() ? Object->TryGetField(TEXT("value")) : nullptr;
        if (!Object.IsValid() || Object->Values.Num() != 2 || !Key.IsValid() || !Value.IsValid())
        {
            OutError = FString::Printf(TEXT("Collection state '%s' contains an invalid map entry."), *Desc.Name.ToString());
            return false;
        }
        FScopedPropertyValue KeyValue(Map->KeyProp);
        FScopedPropertyValue MappedValue(Map->ValueProp);
        if (!SetPropertyFromJson(Map->KeyProp, Desc.KeyType, Desc.KeyTypeObject,
                KeyMetadata, KeyUnrealType, Key, KeyValue.Value, OutError) ||
            !SetPropertyFromJson(Map->ValueProp, Desc.ValueType, Desc.ValueTypeObject,
                ValueMetadata, ValueUnrealType, Value, MappedValue.Value, OutError)) return false;
        bool bDuplicate = false;
        for (int32 PairIndex = 0; PairIndex < Helper.GetMaxIndex(); ++PairIndex)
        {
            if (Helper.IsValidIndex(PairIndex) && Map->KeyProp->Identical(
                    Helper.GetKeyPtr(PairIndex), KeyValue.Value))
            {
                bDuplicate = true;
                break;
            }
        }
        if (bDuplicate)
        {
            OutError = FString::Printf(TEXT("Collection state '%s' contains a duplicate map key."), *Desc.Name.ToString());
            return false;
        }
        const int32 Index = Helper.AddDefaultValue_Invalid_NeedsRehash();
        Map->KeyProp->CopyCompleteValue(Helper.GetKeyPtr(Index), KeyValue.Value);
        Map->ValueProp->CopyCompleteValue(Helper.GetValuePtr(Index), MappedValue.Value);
    }
    Helper.Rehash();
    return true;
}

bool SetDefaultValue(
    FInstancedPropertyBag& Bag,
    const FPropertyBagPropertyDesc& Desc,
    const TSharedPtr<FJsonValue>& DefaultValue,
    const FStateUnrealType* UnrealType,
    FString& OutError)
{
    if (!DefaultValue.IsValid())
    {
        OutError = FString::Printf(
            TEXT("State '%s' is missing its defaultValue."),
            *Desc.Name.ToString());
        return false;
    }
    if (!Desc.ContainerTypes.IsEmpty())
    {
        return UnrealType != nullptr &&
            !DefaultValue->IsNull() &&
            SetCollectionJsonDefault(Bag, Desc, *UnrealType, DefaultValue, OutError);
    }
    if (DefaultValue->IsNull())
    {
        return Desc.ValueType == EPropertyBagPropertyType::String ||
            Desc.ValueType == EPropertyBagPropertyType::Object ||
            Desc.ValueType == EPropertyBagPropertyType::Class;
    }

    EPropertyBagResult Result = EPropertyBagResult::TypeMismatch;
    switch (Desc.ValueType)
    {
    case EPropertyBagPropertyType::Bool:
        if (DefaultValue->Type == EJson::Boolean)
        {
            Result = Bag.SetValueBool(Desc.Name, DefaultValue->AsBool());
        }
        break;
    case EPropertyBagPropertyType::Byte:
        if (DefaultValue->Type == EJson::Number)
        {
            int64 Number = 0;
            if (ParseJsonInt64(DefaultValue, Number) && Number >= 0 && Number <= MAX_uint8)
            {
                Result = Bag.SetValueByte(Desc.Name, static_cast<uint8>(Number));
            }
        }
        break;
    case EPropertyBagPropertyType::Int32:
        if (DefaultValue->Type == EJson::Number)
        {
            int64 Number = 0;
            if (ParseJsonInt64(DefaultValue, Number) && Number >= MIN_int32 && Number <= MAX_int32)
            {
                Result = Bag.SetValueInt32(Desc.Name, static_cast<int32>(Number));
            }
        }
        break;
    case EPropertyBagPropertyType::Int64:
        if (DefaultValue->Type == EJson::Number)
        {
            int64 Number = 0;
            if (ParseJsonInt64(DefaultValue, Number))
            {
                Result = Bag.SetValueInt64(Desc.Name, Number);
            }
        }
        break;
    case EPropertyBagPropertyType::Float:
        if (DefaultValue->Type == EJson::Number)
        {
            double Number = 0.0;
            if (ParseJsonDouble(DefaultValue, Number) && FMath::IsFinite(static_cast<float>(Number)))
            {
                Result = Bag.SetValueFloat(Desc.Name, static_cast<float>(Number));
            }
        }
        break;
    case EPropertyBagPropertyType::Double:
        if (DefaultValue->Type == EJson::Number)
        {
            double Number = 0.0;
            if (ParseJsonDouble(DefaultValue, Number))
            {
                Result = Bag.SetValueDouble(Desc.Name, Number);
            }
        }
        break;
    case EPropertyBagPropertyType::String:
        if (DefaultValue->Type == EJson::String)
        {
            Result = Bag.SetValueString(Desc.Name, DefaultValue->AsString());
        }
        break;
    case EPropertyBagPropertyType::SoftObject:
    case EPropertyBagPropertyType::SoftClass:
        if (DefaultValue->Type == EJson::String)
        {
            const FString PathText = DefaultValue->AsString();
            if (!UnifiedScriptSoftObjectPath::IsValid(PathText))
            {
                break;
            }
            const FSoftObjectPath Path(PathText);
            if (PathText.IsEmpty() || (Path.IsValid() && Path.ToString() == PathText))
            {
                Result = Bag.SetValueSoftPath(Desc.Name, Path);
            }
        }
        break;
    case EPropertyBagPropertyType::Enum:
        if (DefaultValue->Type == EJson::String)
        {
            int64 EnumValue = 0;
            const UEnum* Enum = Cast<UEnum>(Desc.ValueTypeObject);
            if (LexTryParseString(EnumValue, *DefaultValue->AsString()) &&
                IsLegalPropertyBagEnumValue(Enum, EnumValue))
            {
                Result = Bag.SetValueEnum(
                    Desc.Name,
                    EnumValue,
                    const_cast<UEnum*>(Enum));
            }
        }
        break;
    case EPropertyBagPropertyType::Name:
        if (DefaultValue->Type == EJson::String)
        {
            Result = Bag.SetValueName(Desc.Name, FName(*DefaultValue->AsString()));
        }
        break;
    case EPropertyBagPropertyType::Text:
        if (DefaultValue->Type == EJson::String)
        {
            Result = Bag.SetValueText(Desc.Name, FText::FromString(DefaultValue->AsString()));
        }
        break;
    case EPropertyBagPropertyType::Struct:
        if (Desc.ValueTypeObject == TBaseStructure<FVector>::Get() &&
            DefaultValue->Type == EJson::Object)
        {
            const TSharedPtr<FJsonObject> Object = DefaultValue->AsObject();
            double X = 0.0;
            double Y = 0.0;
            double Z = 0.0;
            if (Object.IsValid() &&
                Object->TryGetNumberField(TEXT("x"), X) && FMath::IsFinite(X) &&
                Object->TryGetNumberField(TEXT("y"), Y) && FMath::IsFinite(Y) &&
                Object->TryGetNumberField(TEXT("z"), Z) && FMath::IsFinite(Z) &&
                Object->Values.Num() == 3)
            {
                Result = Bag.SetValueStruct(Desc.Name, FVector(X, Y, Z));
            }
        }
        else if (Desc.ValueTypeObject == TBaseStructure<FRotator>::Get() &&
            DefaultValue->Type == EJson::Object)
        {
            const TSharedPtr<FJsonObject> Object = DefaultValue->AsObject();
            double Pitch = 0.0;
            double Yaw = 0.0;
            double Roll = 0.0;
            if (Object.IsValid() && Object->Values.Num() == 3 &&
                Object->TryGetNumberField(TEXT("pitch"), Pitch) && FMath::IsFinite(Pitch) &&
                Object->TryGetNumberField(TEXT("yaw"), Yaw) && FMath::IsFinite(Yaw) &&
                Object->TryGetNumberField(TEXT("roll"), Roll) && FMath::IsFinite(Roll))
            {
                Result = Bag.SetValueStruct(Desc.Name, FRotator(Pitch, Yaw, Roll));
            }
        }
        else if (Desc.ValueTypeObject == TBaseStructure<FTransform>::Get() &&
            DefaultValue->Type == EJson::Object)
        {
            const TSharedPtr<FJsonObject> Object = DefaultValue->AsObject();
            const TSharedPtr<FJsonObject>* RotationObject = nullptr;
            const TSharedPtr<FJsonObject>* TranslationObject = nullptr;
            const TSharedPtr<FJsonObject>* ScaleObject = nullptr;
            if (Object.IsValid() && Object->Values.Num() == 3 &&
                Object->TryGetObjectField(TEXT("rotation"), RotationObject) &&
                Object->TryGetObjectField(TEXT("translation"), TranslationObject) &&
                Object->TryGetObjectField(TEXT("scale3D"), ScaleObject) &&
                RotationObject != nullptr && TranslationObject != nullptr && ScaleObject != nullptr)
            {
                double X = 0.0;
                double Y = 0.0;
                double Z = 0.0;
                double W = 0.0;
                double TX = 0.0;
                double TY = 0.0;
                double TZ = 0.0;
                double SX = 0.0;
                double SY = 0.0;
                double SZ = 0.0;
                if ((*RotationObject)->Values.Num() == 4 &&
                    (*RotationObject)->TryGetNumberField(TEXT("x"), X) && FMath::IsFinite(X) &&
                    (*RotationObject)->TryGetNumberField(TEXT("y"), Y) && FMath::IsFinite(Y) &&
                    (*RotationObject)->TryGetNumberField(TEXT("z"), Z) && FMath::IsFinite(Z) &&
                    (*RotationObject)->TryGetNumberField(TEXT("w"), W) && FMath::IsFinite(W) &&
                    (*TranslationObject)->Values.Num() == 3 &&
                    (*TranslationObject)->TryGetNumberField(TEXT("x"), TX) && FMath::IsFinite(TX) &&
                    (*TranslationObject)->TryGetNumberField(TEXT("y"), TY) && FMath::IsFinite(TY) &&
                    (*TranslationObject)->TryGetNumberField(TEXT("z"), TZ) && FMath::IsFinite(TZ) &&
                    (*ScaleObject)->Values.Num() == 3 &&
                    (*ScaleObject)->TryGetNumberField(TEXT("x"), SX) && FMath::IsFinite(SX) &&
                    (*ScaleObject)->TryGetNumberField(TEXT("y"), SY) && FMath::IsFinite(SY) &&
                    (*ScaleObject)->TryGetNumberField(TEXT("z"), SZ) && FMath::IsFinite(SZ))
                {
                    const double LengthSquared = X * X + Y * Y + Z * Z + W * W;
                    if (FMath::IsNearlyEqual(LengthSquared, 1.0, 1e-6))
                    {
                        Result = Bag.SetValueStruct(
                            Desc.Name,
                            FTransform(FQuat(X, Y, Z, W), FVector(TX, TY, TZ), FVector(SX, SY, SZ)));
                    }
                }
            }
        }
        break;
    default:
        break;
    }

    if (Result != EPropertyBagResult::Success)
    {
        OutError = FString::Printf(
            TEXT("State '%s' has a defaultValue incompatible with its declared type."),
            *Desc.Name.ToString());
        return false;
    }
    return true;
}

bool SelectAsset(
    const TArray<TSharedPtr<FJsonValue>>& Assets,
    const FString& RequestedAssetPath,
    TSharedPtr<FJsonObject>& OutAsset,
    FString& OutAssetPath,
    FString& OutError)
{
    for (const TSharedPtr<FJsonValue>& AssetValue : Assets)
    {
        const TSharedPtr<FJsonObject> Asset = AssetValue.IsValid()
            ? AssetValue->AsObject()
            : nullptr;
        FString AssetPath;
        if (!Asset.IsValid() || !Asset->TryGetStringField(TEXT("assetPath"), AssetPath))
        {
            OutError = TEXT("Declaration assets must contain an assetPath string.");
            return false;
        }
        if ((RequestedAssetPath.IsEmpty() && Assets.Num() == 1) ||
            AssetPath == RequestedAssetPath)
        {
            OutAsset = Asset;
            OutAssetPath = MoveTemp(AssetPath);
            return true;
        }
    }

    OutError = RequestedAssetPath.IsEmpty()
        ? TEXT("DeclarationAssetPath is required when a declaration contains multiple assets.")
        : FString::Printf(
            TEXT("Declaration does not contain assetPath '%s'."),
            *RequestedAssetPath);
    return false;
}

TArray<FGuid> CollectAllPropertyIds(const FInstancedPropertyBag& Bag)
{
    TArray<FGuid> Result;
    if (const UPropertyBag* BagStruct = Bag.GetPropertyBagStruct())
    {
        Result.Reserve(BagStruct->GetPropertyDescs().Num());
        for (const FPropertyBagPropertyDesc& Desc : BagStruct->GetPropertyDescs())
        {
            Result.Add(Desc.ID);
        }
    }
    return Result;
}

enum class EBinaryDefaultKind : uint8
{
    Null = 0,
    Boolean = 1,
    SignedInteger = 2,
    UnsignedInteger = 3,
    FloatingPoint = 4,
    String = 5,
    Enum = 6,
    Name = 7,
    Text = 8,
    Vector = 9,
    Rotator = 10,
    Transform = 11,
    SoftObject = 12,
    SoftClass = 13,
    Array = 14,
    Set = 15,
    Map = 16,
};

struct FBinaryDefault
{
    EBinaryDefaultKind Kind = EBinaryDefaultKind::Null;
    bool bBoolean = false;
    int64 SignedInteger = 0;
    uint64 UnsignedInteger = 0;
    double FloatingPoint = 0.0;
    FString Text;
    FVector Vector = FVector::ZeroVector;
    FRotator Rotator = FRotator::ZeroRotator;
    FTransform Transform = FTransform::Identity;
    TArray<FBinaryDefault> Children;
};

class FBinaryDeclarationReader
{
public:
    explicit FBinaryDeclarationReader(TConstArrayView<uint8> InData)
        : Data(InData)
    {
    }

    bool ReadHeader(FString& OutError)
    {
        constexpr uint8 Magic[8] = {'U', 'S', 'R', 'D', 'E', 'C', 0, 0};
        if (Data.Num() < 16 || FMemory::Memcmp(Data.GetData(), Magic, sizeof(Magic)) != 0)
        {
            OutError = TEXT("Compiled declaration payload magic is invalid.");
            return false;
        }
        Offset = 8;
        uint16 Major = 0;
        uint16 Minor = 0;
        uint32 Reserved = 0;
        if (!ReadUInt16(Major) || !ReadUInt16(Minor) || !ReadUInt32(Reserved) ||
            Major != 1 || Minor > 5 || Reserved != 0)
        {
            OutError = TEXT("Compiled declaration payload is not canonical v1.0.");
            return false;
        }
        MinorVersion = Minor;
        return true;
    }

    uint16 GetMinorVersion() const { return MinorVersion; }

    bool ReadByte(uint8& OutValue)
    {
        if (!Require(1))
        {
            return false;
        }
        OutValue = Data[Offset++];
        return true;
    }

    bool ReadUInt16(uint16& OutValue)
    {
        if (!Require(2))
        {
            return false;
        }
        OutValue = static_cast<uint16>(Data[Offset]) |
            static_cast<uint16>(Data[Offset + 1]) << 8;
        Offset += 2;
        return true;
    }

    bool ReadUInt32(uint32& OutValue)
    {
        if (!Require(4))
        {
            return false;
        }
        OutValue = static_cast<uint32>(Data[Offset]) |
            static_cast<uint32>(Data[Offset + 1]) << 8 |
            static_cast<uint32>(Data[Offset + 2]) << 16 |
            static_cast<uint32>(Data[Offset + 3]) << 24;
        Offset += 4;
        return true;
    }

    bool ReadUInt64(uint64& OutValue)
    {
        uint32 Low = 0;
        uint32 High = 0;
        if (!ReadUInt32(Low) || !ReadUInt32(High))
        {
            return false;
        }
        OutValue = static_cast<uint64>(Low) | static_cast<uint64>(High) << 32;
        return true;
    }

    bool ReadString(FString& OutValue)
    {
        uint32 Length = 0;
        if (!ReadUInt32(Length) || Length > 16 * 1024 * 1024 ||
            Length > static_cast<uint32>(MAX_int32) ||
            !Require(static_cast<int32>(Length)))
        {
            return false;
        }
        const FUTF8ToTCHAR Converted(
            reinterpret_cast<const ANSICHAR*>(Data.GetData() + Offset),
            static_cast<int32>(Length));
        OutValue = FString(Converted.Length(), Converted.Get());
        Offset += static_cast<int32>(Length);
        return true;
    }

    bool ReadNullableString(FString& OutValue, bool& bOutHasValue)
    {
        uint8 HasValue = 0;
        if (!ReadByte(HasValue) || HasValue > 1)
        {
            return false;
        }
        bOutHasValue = HasValue != 0;
        if (!bOutHasValue)
        {
            OutValue.Reset();
            return true;
        }
        return ReadString(OutValue);
    }

    bool ReadCount(uint32& OutCount)
    {
        return ReadUInt32(OutCount) && OutCount <= 1'000'000;
    }

    bool ReadDefault(FBinaryDefault& OutDefault, int32 Depth = 0)
    {
        if (Depth > 16)
        {
            return false;
        }
        uint8 Kind = 0;
        if (!ReadByte(Kind) || Kind > static_cast<uint8>(EBinaryDefaultKind::Map) ||
            Kind >= static_cast<uint8>(EBinaryDefaultKind::Rotator) &&
                Kind < static_cast<uint8>(EBinaryDefaultKind::SoftObject) && MinorVersion < 2 ||
            Kind >= static_cast<uint8>(EBinaryDefaultKind::SoftObject) &&
                Kind < static_cast<uint8>(EBinaryDefaultKind::Array) && MinorVersion < 3 ||
            Kind >= static_cast<uint8>(EBinaryDefaultKind::Array) && MinorVersion < 4)
        {
            return false;
        }
        OutDefault.Kind = static_cast<EBinaryDefaultKind>(Kind);
        switch (OutDefault.Kind)
        {
        case EBinaryDefaultKind::Null:
            return true;
        case EBinaryDefaultKind::Boolean:
        {
            uint8 Value = 0;
            if (!ReadByte(Value) || Value > 1)
            {
                return false;
            }
            OutDefault.bBoolean = Value != 0;
            return true;
        }
        case EBinaryDefaultKind::SignedInteger:
        {
            uint64 Bits = 0;
            if (!ReadUInt64(Bits))
            {
                return false;
            }
            OutDefault.SignedInteger = static_cast<int64>(Bits);
            return true;
        }
        case EBinaryDefaultKind::UnsignedInteger:
            return ReadUInt64(OutDefault.UnsignedInteger);
        case EBinaryDefaultKind::FloatingPoint:
        {
            uint64 Bits = 0;
            if (!ReadUInt64(Bits))
            {
                return false;
            }
            FMemory::Memcpy(&OutDefault.FloatingPoint, &Bits, sizeof(double));
            return FMath::IsFinite(OutDefault.FloatingPoint);
        }
        case EBinaryDefaultKind::String:
        case EBinaryDefaultKind::Enum:
        case EBinaryDefaultKind::Name:
        case EBinaryDefaultKind::Text:
        case EBinaryDefaultKind::SoftObject:
        case EBinaryDefaultKind::SoftClass:
            return ReadString(OutDefault.Text);
        case EBinaryDefaultKind::Array:
        case EBinaryDefaultKind::Set:
        case EBinaryDefaultKind::Map:
        {
            uint32 Count = 0;
            if (!ReadCount(Count) || Count > 1'000'000)
            {
                return false;
            }
            OutDefault.Children.SetNum(static_cast<int32>(Count));
            for (FBinaryDefault& Child : OutDefault.Children)
            {
                if (!ReadDefault(Child, Depth + 1))
                {
                    return false;
                }
            }
            return true;
        }
        case EBinaryDefaultKind::Vector:
        {
            double Components[3] = {};
            for (double& Component : Components)
            {
                uint64 Bits = 0;
                if (!ReadUInt64(Bits))
                {
                    return false;
                }
                FMemory::Memcpy(&Component, &Bits, sizeof(double));
                if (!FMath::IsFinite(Component))
                {
                    return false;
                }
            }
            OutDefault.Vector = FVector(Components[0], Components[1], Components[2]);
            return true;
        }
        case EBinaryDefaultKind::Rotator:
        {
            double Components[3] = {};
            for (double& Component : Components)
            {
                uint64 Bits = 0;
                if (!ReadUInt64(Bits))
                {
                    return false;
                }
                FMemory::Memcpy(&Component, &Bits, sizeof(double));
                if (!FMath::IsFinite(Component))
                {
                    return false;
                }
            }
            OutDefault.Rotator = FRotator(Components[0], Components[1], Components[2]);
            return true;
        }
        case EBinaryDefaultKind::Transform:
        {
            double Components[10] = {};
            for (double& Component : Components)
            {
                uint64 Bits = 0;
                if (!ReadUInt64(Bits))
                {
                    return false;
                }
                FMemory::Memcpy(&Component, &Bits, sizeof(double));
                if (!FMath::IsFinite(Component))
                {
                    return false;
                }
            }
            const double LengthSquared = Components[0] * Components[0] +
                Components[1] * Components[1] + Components[2] * Components[2] +
                Components[3] * Components[3];
            if (!FMath::IsNearlyEqual(LengthSquared, 1.0, 1e-6))
            {
                return false;
            }
            OutDefault.Transform = FTransform(
                FQuat(Components[0], Components[1], Components[2], Components[3]),
                FVector(Components[4], Components[5], Components[6]),
                FVector(Components[7], Components[8], Components[9]));
            return true;
        }
        default:
            return false;
        }
    }

    bool ReadUnrealType(FStateUnrealType& OutType, int32 Depth = 0)
    {
        if (Depth > 16)
        {
            return false;
        }
        uint8 HasType = 0;
        if (!ReadByte(HasType) || HasType > 1)
        {
            return false;
        }
        OutType = {};
        if (HasType == 0)
        {
            return true;
        }

        OutType.bPresent = true;
        uint32 FieldCount = 0;
        if (!ReadByte(OutType.Kind) || OutType.Kind > 4 ||
            !ReadString(OutType.Path) ||
            !ReadString(OutType.NativeName) || OutType.NativeName.IsEmpty() ||
            !ReadCount(FieldCount))
        {
            return false;
        }
        if (OutType.Kind < 2 && OutType.Path.IsEmpty())
        {
            return false;
        }
        for (uint32 FieldIndex = 0; FieldIndex < FieldCount; ++FieldIndex)
        {
            uint32 DeclaredIndex = 0;
            FString NativeName;
            FString MetadataName;
            FStateUnrealType NestedType;
            if (!ReadUInt32(DeclaredIndex) || DeclaredIndex != FieldIndex ||
                !ReadString(NativeName) || NativeName.IsEmpty() ||
                !ReadString(MetadataName) || MetadataName.IsEmpty() ||
                !ReadUnrealType(NestedType, Depth + 1))
            {
                return false;
            }
            OutType.FieldNativeNames.Add(MoveTemp(NativeName));
            OutType.FieldMetadataNames.Add(MoveTemp(MetadataName));
            OutType.FieldTypes.Add(MoveTemp(NestedType));
        }
        if (OutType.Kind == 2 || OutType.Kind == 3 || OutType.Kind == 4)
        {
            const int32 ExpectedCount = OutType.Kind == 4 ? 2 : 1;
            uint32 ArgumentCount = 0;
            if (!ReadCount(ArgumentCount) || ArgumentCount != ExpectedCount)
            {
                return false;
            }
            for (uint32 ArgumentIndex = 0; ArgumentIndex < ArgumentCount; ++ArgumentIndex)
            {
                FString MetadataName;
                FStateUnrealType ArgumentType;
                if (!ReadString(MetadataName) || MetadataName.IsEmpty() ||
                    !ReadUnrealType(ArgumentType, Depth + 1))
                {
                    return false;
                }
                if (ArgumentType.bPresent && ArgumentType.Kind >= 2)
                {
                    return false;
                }
                OutType.ArgumentMetadataNames.Add(MoveTemp(MetadataName));
                OutType.TypeArguments.Add(MoveTemp(ArgumentType));
            }
            return FieldCount == 0;
        }
        return OutType.Kind == 0 || OutType.Kind == 1;
    }

    bool IsAtEnd() const { return Offset == Data.Num(); }

private:
    bool Require(int32 Length) const
    {
        return Length >= 0 && Offset >= 0 && Offset <= Data.Num() &&
            Length <= Data.Num() - Offset;
    }

    TConstArrayView<uint8> Data;
    int32 Offset = 0;
    uint16 MinorVersion = 0;
};

bool ResolveBinaryPropertyType(
    uint8 TypeKind,
    const FString& MetadataName,
    const FStateUnrealType& UnrealType,
    EPropertyBagPropertyType& OutType,
    const UObject*& OutTypeObject)
{
    switch (TypeKind)
    {
    case 0:
        return ResolvePropertyType(TEXT("boolean"), MetadataName, UnrealType, OutType, OutTypeObject);
    case 1:
        return ResolvePropertyType(TEXT("integer"), MetadataName, UnrealType, OutType, OutTypeObject);
    case 2:
        return ResolvePropertyType(TEXT("floatingPoint"), MetadataName, UnrealType, OutType, OutTypeObject);
    case 3:
        return ResolvePropertyType(TEXT("string"), MetadataName, UnrealType, OutType, OutTypeObject);
    case 4:
        return ResolvePropertyType(TEXT("enum"), MetadataName, UnrealType, OutType, OutTypeObject);
    case 5:
        return ResolvePropertyType(TEXT("name"), MetadataName, UnrealType, OutType, OutTypeObject);
    case 6:
        return ResolvePropertyType(TEXT("text"), MetadataName, UnrealType, OutType, OutTypeObject);
    case 7:
        return ResolvePropertyType(TEXT("object"), MetadataName, UnrealType, OutType, OutTypeObject);
    case 8:
        return ResolvePropertyType(TEXT("class"), MetadataName, UnrealType, OutType, OutTypeObject);
    case 9:
        return ResolvePropertyType(TEXT("struct"), MetadataName, UnrealType, OutType, OutTypeObject);
    case 10:
        return ResolvePropertyType(TEXT("softObject"), MetadataName, UnrealType, OutType, OutTypeObject);
    case 11:
        return ResolvePropertyType(TEXT("softClass"), MetadataName, UnrealType, OutType, OutTypeObject);
    default:
        return false;
    }
}

bool SetPropertyFromBinary(
    FProperty* Property,
    EPropertyBagPropertyType ExpectedType,
    const UObject* TypeObject,
    const FString& MetadataName,
    const FBinaryDefault& Default,
    void* Destination,
    FString& OutError)
{
    if (Property == nullptr || Destination == nullptr)
    {
        return false;
    }
    switch (ExpectedType)
    {
    case EPropertyBagPropertyType::Bool:
        if (Default.Kind == EBinaryDefaultKind::Boolean)
        {
            CastFieldChecked<FBoolProperty>(Property)->SetPropertyValue(Destination, Default.bBoolean);
            return true;
        }
        break;
    case EPropertyBagPropertyType::Byte:
        if (Default.Kind == EBinaryDefaultKind::UnsignedInteger && Default.UnsignedInteger <= MAX_uint8)
        {
            CastFieldChecked<FByteProperty>(Property)->SetPropertyValue(Destination, static_cast<uint8>(Default.UnsignedInteger));
            return true;
        }
        break;
    case EPropertyBagPropertyType::Int32:
        if (Default.Kind == EBinaryDefaultKind::SignedInteger && Default.SignedInteger >= MIN_int32 && Default.SignedInteger <= MAX_int32)
        {
            CastFieldChecked<FIntProperty>(Property)->SetPropertyValue(Destination, static_cast<int32>(Default.SignedInteger));
            return true;
        }
        break;
    case EPropertyBagPropertyType::Int64:
        if (Default.Kind == EBinaryDefaultKind::SignedInteger)
        {
            CastFieldChecked<FInt64Property>(Property)->SetPropertyValue(Destination, Default.SignedInteger);
            return true;
        }
        break;
    case EPropertyBagPropertyType::Float:
        if (Default.Kind == EBinaryDefaultKind::FloatingPoint && FMath::IsFinite(static_cast<float>(Default.FloatingPoint)))
        {
            CastFieldChecked<FFloatProperty>(Property)->SetPropertyValue(Destination, static_cast<float>(Default.FloatingPoint));
            return true;
        }
        break;
    case EPropertyBagPropertyType::Double:
        if (Default.Kind == EBinaryDefaultKind::FloatingPoint)
        {
            CastFieldChecked<FDoubleProperty>(Property)->SetPropertyValue(Destination, Default.FloatingPoint);
            return true;
        }
        break;
    case EPropertyBagPropertyType::String:
        if (Default.Kind == EBinaryDefaultKind::String)
        {
            CastFieldChecked<FStrProperty>(Property)->SetPropertyValue(Destination, Default.Text);
            return true;
        }
        break;
    case EPropertyBagPropertyType::Name:
        if (Default.Kind == EBinaryDefaultKind::Name)
        {
            CastFieldChecked<FNameProperty>(Property)->SetPropertyValue(Destination, FName(*Default.Text));
            return true;
        }
        break;
    case EPropertyBagPropertyType::Text:
        if (Default.Kind == EBinaryDefaultKind::Text)
        {
            CastFieldChecked<FTextProperty>(Property)->SetPropertyValue(Destination, FText::FromString(Default.Text));
            return true;
        }
        break;
    case EPropertyBagPropertyType::SoftObject:
    case EPropertyBagPropertyType::SoftClass:
        if (Default.Kind == (ExpectedType == EPropertyBagPropertyType::SoftObject
                ? EBinaryDefaultKind::SoftObject : EBinaryDefaultKind::SoftClass) &&
            UnifiedScriptSoftObjectPath::IsValid(Default.Text))
        {
            CastFieldChecked<FSoftObjectProperty>(Property)->SetPropertyValue(
                Destination, FSoftObjectPtr(FSoftObjectPath(Default.Text)));
            return true;
        }
        break;
    case EPropertyBagPropertyType::Enum:
    {
        int64 Parsed = 0;
        const UEnum* Enum = Cast<UEnum>(TypeObject);
        if (Default.Kind == EBinaryDefaultKind::Enum && IsJsonIntegerToken(Default.Text) &&
            LexTryParseString(Parsed, *Default.Text) && IsLegalPropertyBagEnumValue(Enum, Parsed))
        {
            if (const FEnumProperty* EnumProperty = CastField<FEnumProperty>(Property))
            {
                EnumProperty->GetUnderlyingProperty()->SetIntPropertyValue(Destination, Parsed);
            }
            else if (const FByteProperty* ByteProperty = CastField<FByteProperty>(Property))
            {
                ByteProperty->SetPropertyValue(Destination, static_cast<uint8>(Parsed));
            }
            else
            {
                break;
            }
            return true;
        }
        break;
    }
    case EPropertyBagPropertyType::Struct:
        if (Default.Kind == EBinaryDefaultKind::Vector && TypeObject == TBaseStructure<FVector>::Get())
        {
            CastFieldChecked<FStructProperty>(Property)->CopyCompleteValue(Destination, &Default.Vector);
            return true;
        }
        if (Default.Kind == EBinaryDefaultKind::Rotator && TypeObject == TBaseStructure<FRotator>::Get())
        {
            CastFieldChecked<FStructProperty>(Property)->CopyCompleteValue(Destination, &Default.Rotator);
            return true;
        }
        if (Default.Kind == EBinaryDefaultKind::Transform && TypeObject == TBaseStructure<FTransform>::Get())
        {
            CastFieldChecked<FStructProperty>(Property)->CopyCompleteValue(Destination, &Default.Transform);
            return true;
        }
        break;
    case EPropertyBagPropertyType::Object:
    case EPropertyBagPropertyType::Class:
        if (Default.Kind == EBinaryDefaultKind::Null)
        {
            CastFieldChecked<FObjectPropertyBase>(Property)->SetObjectPropertyValue(Destination, nullptr);
            return true;
        }
        break;
    default:
        break;
    }
    OutError = FString::Printf(TEXT("Collection leaf '%s' has an invalid binary default."), *MetadataName);
    return false;
}

bool SetCollectionBinaryDefault(
    FInstancedPropertyBag& Bag,
    const FPropertyBagPropertyDesc& Desc,
    const FStateUnrealType& UnrealType,
    const FBinaryDefault& Default,
    FString& OutError)
{
    if (Default.Children.Num() > 1'000'000 ||
        UnrealType.TypeArguments.Num() != (Desc.ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Map ? 2 : 1))
    {
        return false;
    }
    const int32 ValueArgumentIndex = Desc.ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Map ? 1 : 0;
    if (Desc.ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Array)
    {
        const FArrayProperty* Array = CastField<FArrayProperty>(Desc.CachedProperty);
        FScriptArrayHelper Helper(
            Array,
            Desc.CachedProperty->ContainerPtrToValuePtr<void>(Bag.GetMutableValue().GetMemory()));
        for (const FBinaryDefault& Child : Default.Children)
        {
            const int32 Index = Helper.AddValue();
            if (!SetPropertyFromBinary(Array->Inner, Desc.ValueType, Desc.ValueTypeObject,
                    UnrealType.ArgumentMetadataNames[ValueArgumentIndex], Child,
                    Helper.GetRawPtr(Index), OutError))
            {
                Helper.RemoveValues(Index);
                return false;
            }
        }
        return true;
    }
    if (Desc.ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Set)
    {
        const FSetProperty* Set = CastField<FSetProperty>(Desc.CachedProperty);
        FScriptSetHelper Helper(
            Set,
            Desc.CachedProperty->ContainerPtrToValuePtr<void>(Bag.GetMutableValue().GetMemory()));
        for (const FBinaryDefault& Child : Default.Children)
        {
            FScopedPropertyValue Candidate(Set->ElementProp);
            if (!SetPropertyFromBinary(Set->ElementProp, Desc.ValueType, Desc.ValueTypeObject,
                    UnrealType.ArgumentMetadataNames[0], Child, Candidate.Value, OutError)) return false;
            if (Helper.FindElementIndex(Candidate.Value) != INDEX_NONE)
            {
                OutError = FString::Printf(TEXT("Collection state '%s' contains a duplicate set value."), *Desc.Name.ToString());
                return false;
            }
            const int32 Index = Helper.AddDefaultValue_Invalid_NeedsRehash();
            Set->ElementProp->CopyCompleteValue(Helper.GetElementPtr(Index), Candidate.Value);
        }
        Helper.Rehash();
        return true;
    }
    if (Default.Children.Num() % 2 != 0)
    {
        return false;
    }
    const FMapProperty* Map = CastField<FMapProperty>(Desc.CachedProperty);
    FScriptMapHelper Helper(
        Map,
        Desc.CachedProperty->ContainerPtrToValuePtr<void>(Bag.GetMutableValue().GetMemory()));
    for (int32 Index = 0; Index < Default.Children.Num(); Index += 2)
    {
        FScopedPropertyValue KeyValue(Map->KeyProp);
        FScopedPropertyValue MappedValue(Map->ValueProp);
        if (!SetPropertyFromBinary(Map->KeyProp, Desc.KeyType, Desc.KeyTypeObject,
                UnrealType.ArgumentMetadataNames[0], Default.Children[Index], KeyValue.Value, OutError) ||
            !SetPropertyFromBinary(Map->ValueProp, Desc.ValueType, Desc.ValueTypeObject,
                UnrealType.ArgumentMetadataNames[1], Default.Children[Index + 1], MappedValue.Value, OutError)) return false;
        bool bDuplicate = false;
        for (int32 PairIndex = 0; PairIndex < Helper.GetMaxIndex(); ++PairIndex)
        {
            if (Helper.IsValidIndex(PairIndex) && Map->KeyProp->Identical(
                    Helper.GetKeyPtr(PairIndex), KeyValue.Value))
            {
                bDuplicate = true;
                break;
            }
        }
        if (bDuplicate)
        {
            OutError = FString::Printf(TEXT("Collection state '%s' contains a duplicate map key."), *Desc.Name.ToString());
            return false;
        }
        const int32 PairIndex = Helper.AddDefaultValue_Invalid_NeedsRehash();
        Map->KeyProp->CopyCompleteValue(Helper.GetKeyPtr(PairIndex), KeyValue.Value);
        Map->ValueProp->CopyCompleteValue(Helper.GetValuePtr(PairIndex), MappedValue.Value);
    }
    Helper.Rehash();
    return true;
}

bool SetBinaryDefaultValue(
    FInstancedPropertyBag& Bag,
    const FPropertyBagPropertyDesc& Desc,
    const FBinaryDefault& Default,
    const FStateUnrealType* UnrealType,
    FString& OutError)
{
    if (!Desc.ContainerTypes.IsEmpty())
    {
        const EBinaryDefaultKind ExpectedKind =
            Desc.ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Array
                ? EBinaryDefaultKind::Array
                : Desc.ContainerTypes.GetFirstContainerType() == EPropertyBagContainerType::Set
                    ? EBinaryDefaultKind::Set : EBinaryDefaultKind::Map;
        if (UnrealType == nullptr || Default.Kind != ExpectedKind)
        {
            OutError = FString::Printf(
                TEXT("State '%s' has a binary default incompatible with its collection type."),
                *Desc.Name.ToString());
            return false;
        }
        return SetCollectionBinaryDefault(Bag, Desc, *UnrealType, Default, OutError);
    }
    if (Default.Kind == EBinaryDefaultKind::Null)
    {
        return Desc.ValueType == EPropertyBagPropertyType::String ||
            Desc.ValueType == EPropertyBagPropertyType::Object ||
            Desc.ValueType == EPropertyBagPropertyType::Class;
    }

    EPropertyBagResult Result = EPropertyBagResult::TypeMismatch;
    switch (Desc.ValueType)
    {
    case EPropertyBagPropertyType::Bool:
        if (Default.Kind == EBinaryDefaultKind::Boolean)
        {
            Result = Bag.SetValueBool(Desc.Name, Default.bBoolean);
        }
        break;
    case EPropertyBagPropertyType::Byte:
        if (Default.Kind == EBinaryDefaultKind::UnsignedInteger && Default.UnsignedInteger <= MAX_uint8)
        {
            Result = Bag.SetValueByte(Desc.Name, static_cast<uint8>(Default.UnsignedInteger));
        }
        break;
    case EPropertyBagPropertyType::Int32:
        if (Default.Kind == EBinaryDefaultKind::SignedInteger &&
            Default.SignedInteger >= MIN_int32 && Default.SignedInteger <= MAX_int32)
        {
            Result = Bag.SetValueInt32(Desc.Name, static_cast<int32>(Default.SignedInteger));
        }
        else if (Default.Kind == EBinaryDefaultKind::UnsignedInteger &&
            Default.UnsignedInteger <= MAX_int32)
        {
            Result = Bag.SetValueInt32(Desc.Name, static_cast<int32>(Default.UnsignedInteger));
        }
        break;
    case EPropertyBagPropertyType::Int64:
        if (Default.Kind == EBinaryDefaultKind::SignedInteger)
        {
            Result = Bag.SetValueInt64(Desc.Name, Default.SignedInteger);
        }
        else if (Default.Kind == EBinaryDefaultKind::UnsignedInteger &&
            Default.UnsignedInteger <= MAX_int64)
        {
            Result = Bag.SetValueInt64(Desc.Name, static_cast<int64>(Default.UnsignedInteger));
        }
        break;
    case EPropertyBagPropertyType::Float:
        if (Default.Kind == EBinaryDefaultKind::FloatingPoint)
        {
            Result = Bag.SetValueFloat(Desc.Name, static_cast<float>(Default.FloatingPoint));
        }
        break;
    case EPropertyBagPropertyType::Double:
        if (Default.Kind == EBinaryDefaultKind::FloatingPoint)
        {
            Result = Bag.SetValueDouble(Desc.Name, Default.FloatingPoint);
        }
        break;
    case EPropertyBagPropertyType::String:
        if (Default.Kind == EBinaryDefaultKind::String ||
            Default.Kind == EBinaryDefaultKind::Enum)
        {
            Result = Bag.SetValueString(Desc.Name, Default.Text);
        }
        break;
    case EPropertyBagPropertyType::Enum:
        if (Default.Kind == EBinaryDefaultKind::Enum)
        {
            int64 EnumValue = 0;
            const UEnum* Enum = Cast<UEnum>(Desc.ValueTypeObject);
            if (LexTryParseString(EnumValue, *Default.Text) &&
                IsLegalPropertyBagEnumValue(Enum, EnumValue))
            {
                Result = Bag.SetValueEnum(
                    Desc.Name,
                    EnumValue,
                    const_cast<UEnum*>(Enum));
            }
        }
        break;
    case EPropertyBagPropertyType::Name:
        if (Default.Kind == EBinaryDefaultKind::Name)
        {
            Result = Bag.SetValueName(Desc.Name, FName(*Default.Text));
        }
        break;
    case EPropertyBagPropertyType::Text:
        if (Default.Kind == EBinaryDefaultKind::Text)
        {
            Result = Bag.SetValueText(Desc.Name, FText::FromString(Default.Text));
        }
        break;
    case EPropertyBagPropertyType::SoftObject:
    case EPropertyBagPropertyType::SoftClass:
        if (Default.Kind == (Desc.ValueType == EPropertyBagPropertyType::SoftObject
                ? EBinaryDefaultKind::SoftObject : EBinaryDefaultKind::SoftClass))
        {
            if (!UnifiedScriptSoftObjectPath::IsValid(Default.Text))
            {
                break;
            }
            const FSoftObjectPath Path(Default.Text);
            if (Default.Text.IsEmpty() || (Path.IsValid() && Path.ToString() == Default.Text))
            {
                Result = Bag.SetValueSoftPath(Desc.Name, Path);
            }
        }
        break;
    case EPropertyBagPropertyType::Struct:
        if (Default.Kind == EBinaryDefaultKind::Vector &&
            Desc.ValueTypeObject == TBaseStructure<FVector>::Get())
        {
            Result = Bag.SetValueStruct(Desc.Name, Default.Vector);
        }
        else if (Default.Kind == EBinaryDefaultKind::Rotator &&
            Desc.ValueTypeObject == TBaseStructure<FRotator>::Get())
        {
            Result = Bag.SetValueStruct(Desc.Name, Default.Rotator);
        }
        else if (Default.Kind == EBinaryDefaultKind::Transform &&
            Desc.ValueTypeObject == TBaseStructure<FTransform>::Get())
        {
            Result = Bag.SetValueStruct(Desc.Name, Default.Transform);
        }
        break;
    default:
        break;
    }

    if (Result != EPropertyBagResult::Success)
    {
        OutError = FString::Printf(
            TEXT("State '%s' has a binary default incompatible with its declared type."),
            *Desc.Name.ToString());
        return false;
    }
    return true;
}
}

bool FUnifiedScriptDeclarationMaterializer::MaterializeFile(
    const FString& DeclarationPath,
    const FString& RequestedAssetPath,
    const FInstancedPropertyBag& ExistingState,
    FInstancedPropertyBag& OutState,
    FString& OutAssetPath,
    FString& OutError)
{
    FString Json;
    if (!IFileManager::Get().FileExists(*DeclarationPath) ||
        !FFileHelper::LoadFileToString(Json, *DeclarationPath))
    {
        OutError = FString::Printf(
            TEXT("The Script Asset declaration does not exist or cannot be read: '%s'."),
            *DeclarationPath);
        return false;
    }
    return MaterializeJson(
        Json,
        RequestedAssetPath,
        ExistingState,
        OutState,
        OutAssetPath,
        OutError);
}

bool FUnifiedScriptDeclarationMaterializer::MaterializeFileWithOverrides(
    const FString& DeclarationPath,
    const FString& RequestedAssetPath,
    const FInstancedPropertyBag& ExistingState,
    TConstArrayView<FGuid> ExistingOverrideIds,
    FInstancedPropertyBag& OutState,
    TArray<FGuid>& OutOverrideIds,
    FUnifiedScriptStateMigrationReport& OutMigration,
    FString& OutAssetPath,
    FString& OutError)
{
    FString Json;
    if (!IFileManager::Get().FileExists(*DeclarationPath) ||
        !FFileHelper::LoadFileToString(Json, *DeclarationPath))
    {
        OutError = FString::Printf(
            TEXT("The Script Asset declaration does not exist or cannot be read: '%s'."),
            *DeclarationPath);
        return false;
    }
    return MaterializeJsonWithOverrides(
        Json,
        RequestedAssetPath,
        ExistingState,
        ExistingOverrideIds,
        OutState,
        OutOverrideIds,
        OutMigration,
        OutAssetPath,
        OutError);
}

bool FUnifiedScriptDeclarationMaterializer::MaterializeChunkFileWithOverrides(
    const FString& ChunkPath,
    const FString& RequestedAssetPath,
    const FInstancedPropertyBag& ExistingState,
    TConstArrayView<FGuid> ExistingOverrideIds,
    FInstancedPropertyBag& OutState,
    TArray<FGuid>& OutOverrideIds,
    FUnifiedScriptStateMigrationReport& OutMigration,
    FString& OutAssetPath,
    FString& OutError)
{
    using namespace UE::UnifiedScript::Declaration::Private;

    TArray<uint8> Payload;
    if (!FUnifiedScriptCompiledChunk::LoadSection(
            ChunkPath,
            EUnifiedScriptCompiledChunkSection::DeclarationData,
            Payload,
            OutError))
    {
        return false;
    }

    FBinaryDeclarationReader Reader(Payload);
    FString CompilationIdText;
    FString ConfigurationIdText;
    FGuid CompilationId;
    FGuid ConfigurationId;
    uint32 AssetCount = 0;
    if (!Reader.ReadHeader(OutError) ||
        !Reader.ReadString(CompilationIdText) ||
        !Reader.ReadString(ConfigurationIdText) ||
        !ParseSemanticId(CompilationIdText, CompilationId) ||
        !ParseSemanticId(ConfigurationIdText, ConfigurationId) ||
        !Reader.ReadCount(AssetCount) || AssetCount == 0)
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("Compiled declaration header or identity is invalid.");
        }
        return false;
    }

    TSet<FGuid> SeenAssetIds;
    TSet<FString> SeenAssetPaths;
    TArray<FPropertyBagPropertyDesc> Descs;
    TArray<FBinaryDefault> Defaults;
    TArray<FStateUnrealType> UnrealTypes;
    FGuid SelectedAssetId;
    FString SelectedQualifiedName;
    FString SelectedAssetPath;
    TOptional<FUnifiedScriptClassBindingDeclaration> SelectedClassBinding;
    bool bSelected = false;
    for (uint32 AssetIndex = 0; AssetIndex < AssetCount; ++AssetIndex)
    {
        FString AssetIdText;
        FString QualifiedName;
        FString AssetPath;
        FGuid AssetId;
        uint32 StateCount = 0;
        if (!Reader.ReadString(AssetIdText) || !ParseSemanticId(AssetIdText, AssetId) ||
            !Reader.ReadString(QualifiedName) || QualifiedName.IsEmpty() ||
            !Reader.ReadString(AssetPath) || AssetPath.IsEmpty() ||
            SeenAssetIds.Contains(AssetId) || SeenAssetPaths.Contains(AssetPath))
        {
            OutError = TEXT("Compiled declaration contains an invalid or duplicate asset.");
            return false;
        }
        SeenAssetIds.Add(AssetId);
        SeenAssetPaths.Add(AssetPath);

        TOptional<FUnifiedScriptClassBindingDeclaration> AssetClassBinding;
        if (Reader.GetMinorVersion() >= 5)
        {
            uint8 HasBinding = 0;
            if (!Reader.ReadByte(HasBinding) || HasBinding > 1)
            {
                OutError = FString::Printf(
                    TEXT("Compiled declaration asset '%s' contains an invalid class binding presence flag."),
                    *AssetPath);
                return false;
            }
            if (HasBinding != 0)
            {
                FUnifiedScriptClassBindingDeclaration Binding;
                FString BaseClassPath;
                FString GeneratedClassPath;
                bool bHasBaseClassPath = false;
                bool bHasGeneratedClassPath = false;
                if (!Reader.ReadString(Binding.Kind) ||
                    !Reader.ReadNullableString(BaseClassPath, bHasBaseClassPath) ||
                    !Reader.ReadNullableString(GeneratedClassPath, bHasGeneratedClassPath))
                {
                    OutError = FString::Printf(
                        TEXT("Compiled declaration asset '%s' contains a truncated class binding."),
                        *AssetPath);
                    return false;
                }
                if (bHasBaseClassPath)
                {
                    Binding.BaseClassPath = MoveTemp(BaseClassPath);
                }
                if (bHasGeneratedClassPath)
                {
                    Binding.GeneratedClassPath = MoveTemp(GeneratedClassPath);
                }
                if (!Binding.Validate(OutError))
                {
                    return false;
                }
                AssetClassBinding = MoveTemp(Binding);
            }
        }

        if (!Reader.ReadCount(StateCount))
        {
            OutError = FString::Printf(
                TEXT("Compiled declaration asset '%s' has an invalid state count."),
                *AssetPath);
            return false;
        }

        const bool bUseAsset =
            (RequestedAssetPath.IsEmpty() && AssetCount == 1) ||
            AssetPath == RequestedAssetPath;
        if (bUseAsset)
        {
            if (bSelected)
            {
                OutError = TEXT("Compiled declaration selects more than one asset.");
                return false;
            }
            bSelected = true;
            SelectedAssetId = AssetId;
            SelectedQualifiedName = QualifiedName;
            SelectedAssetPath = AssetPath;
            SelectedClassBinding = MoveTemp(AssetClassBinding);
            Descs.Reserve(static_cast<int32>(StateCount));
            Defaults.Reserve(static_cast<int32>(StateCount));
            UnrealTypes.Reserve(static_cast<int32>(StateCount));
        }

        TSet<FGuid> SeenStateIds;
        TSet<FName> SeenStateNames;
        for (uint32 StateIndex = 0; StateIndex < StateCount; ++StateIndex)
        {
            FString StateIdText;
            FString Name;
            FString TypeDisplayName;
            FString MetadataName;
            FGuid StateId;
            uint8 TypeKind = 0;
            FStateUnrealType UnrealType;
            FBinaryDefault Default;
            uint8 ExposureFlags = 0;
            FString Category;
            bool bHasCategory = false;
            if (!Reader.ReadString(StateIdText) || !ParseSemanticId(StateIdText, StateId) ||
                !Reader.ReadString(Name) || Name.IsEmpty() ||
                !Reader.ReadString(TypeDisplayName) || TypeDisplayName.IsEmpty() ||
                !Reader.ReadString(MetadataName) || MetadataName.IsEmpty() ||
                !Reader.ReadByte(TypeKind) ||
                (Reader.GetMinorVersion() >= 1 && !Reader.ReadUnrealType(UnrealType)) ||
                !Reader.ReadDefault(Default) ||
                !Reader.ReadByte(ExposureFlags) || (ExposureFlags & ~0x07) != 0 ||
                !Reader.ReadNullableString(Category, bHasCategory))
            {
                OutError = FString::Printf(
                    TEXT("Compiled declaration asset '%s' contains an invalid state."),
                    *AssetPath);
                return false;
            }

            if (TypeKind == 4 && UnrealType.bPresent && UnrealType.Kind == 0 &&
                Reader.GetMinorVersion() < 2)
            {
                OutError = FString::Printf(
                    TEXT("Compiled declaration asset '%s' uses a typed enum without the required payload version."),
                    *AssetPath);
                return false;
            }
            if ((TypeKind == 10 || TypeKind == 11) && Reader.GetMinorVersion() < 3)
            {
                OutError = FString::Printf(
                    TEXT("Compiled declaration asset '%s' uses a soft reference without the required payload version."),
                    *AssetPath);
                return false;
            }
            if ((TypeKind == 12 || TypeKind == 13 || TypeKind == 14) &&
                Reader.GetMinorVersion() < 4)
            {
                OutError = FString::Printf(
                    TEXT("Compiled declaration asset '%s' uses a collection state without the required payload version."),
                    *AssetPath);
                return false;
            }

            if (!bUseAsset)
            {
                continue;
            }

            const FName PropertyName(Name);
            if (PropertyName.IsNone() || SeenStateIds.Contains(StateId) ||
                SeenStateNames.Contains(PropertyName))
            {
                OutError = FString::Printf(
                    TEXT("Compiled declaration contains invalid or duplicate state '%s'."),
                    *Name);
                return false;
            }

            EPropertyFlags Flags = CPF_Edit;
            if ((ExposureFlags & (1 << 0)) == 0)
            {
                Flags |= CPF_EditConst;
            }
            if ((ExposureFlags & (1 << 1)) != 0)
            {
                Flags |= CPF_SaveGame;
            }
            if ((ExposureFlags & (1 << 2)) != 0)
            {
                Flags |= CPF_Net;
            }
            FPropertyBagPropertyDesc Desc;
            const bool bContainer = TypeKind == 12 || TypeKind == 13 || TypeKind == 14;
            if (bContainer)
            {
                const FString ContainerKind = TypeKind == 12 ? TEXT("array") :
                    TypeKind == 13 ? TEXT("set") : TEXT("map");
                if (!ResolveContainerPropertyType(
                        ContainerKind, MetadataName, UnrealType, Desc))
                {
                    OutError = FString::Printf(
                        TEXT("Compiled declaration contains invalid collection type '%s'."),
                        *Name);
                    return false;
                }
                Desc.Name = PropertyName;
                Desc.PropertyFlags = Flags;
            }
            else
            {
                EPropertyBagPropertyType PropertyType = EPropertyBagPropertyType::None;
                const UObject* PropertyTypeObject = nullptr;
                if (!ResolveBinaryPropertyType(
                        TypeKind,
                        MetadataName,
                        UnrealType,
                        PropertyType,
                        PropertyTypeObject))
                {
                    OutError = FString::Printf(
                        TEXT("Compiled declaration contains invalid or duplicate state '%s'."),
                        *Name);
                    return false;
                }
                Desc = FPropertyBagPropertyDesc(
                    PropertyName,
                    EPropertyBagContainerType::None,
                    PropertyType,
                    PropertyTypeObject,
                    Flags);
            }
            Descs.Add(MoveTemp(Desc));
            FPropertyBagPropertyDesc& AddedDesc = Descs.Last();
            AddedDesc.ID = StateId;
#if WITH_EDITOR
            AddedDesc.SetMetaData(TEXT("DisplayName"), Name);
            AddedDesc.SetMetaData(TEXT("UnifiedScriptSemanticId"), StateIdText);
            if (bHasCategory && !Category.IsEmpty())
            {
                AddedDesc.SetMetaData(TEXT("Category"), Category);
            }
#endif
            Defaults.Add(MoveTemp(Default));
            UnrealTypes.Add(MoveTemp(UnrealType));
            SeenStateIds.Add(StateId);
            SeenStateNames.Add(PropertyName);
        }
    }

    if (!Reader.IsAtEnd())
    {
        OutError = TEXT("Compiled declaration contains trailing or truncated bytes.");
        return false;
    }
    if (!bSelected)
    {
        OutError = RequestedAssetPath.IsEmpty()
            ? TEXT("DeclarationAssetPath is required when a chunk contains multiple assets.")
            : FString::Printf(
                TEXT("Compiled declaration does not contain assetPath '%s'."),
                *RequestedAssetPath);
        return false;
    }

    FInstancedPropertyBag DefaultsBag;
    if (!Descs.IsEmpty() &&
        DefaultsBag.AddProperties(Descs) != EPropertyBagAlterationResult::Success)
    {
        OutError = TEXT("Unreal could not create the compiled Script State layout.");
        return false;
    }
    for (const FPropertyBagPropertyDesc& InputDesc : Descs)
    {
        const FPropertyBagPropertyDesc* MaterializedDesc =
            DefaultsBag.FindPropertyDescByID(InputDesc.ID);
        if (MaterializedDesc == nullptr ||
            !ValidateCollectionHashability(*MaterializedDesc, OutError))
        {
            if (OutError.IsEmpty())
            {
                OutError = TEXT("Unreal could not resolve a compiled Script State descriptor.");
            }
            return false;
        }
    }
    for (int32 Index = 0; Index < Descs.Num(); ++Index)
    {
        const FPropertyBagPropertyDesc* MaterializedDesc =
            DefaultsBag.FindPropertyDescByID(Descs[Index].ID);
        if (MaterializedDesc == nullptr)
        {
            OutError = TEXT("Unreal could not resolve a compiled Script State descriptor.");
            return false;
        }
        if (!SetBinaryDefaultValue(
                DefaultsBag,
                *MaterializedDesc,
                Defaults[Index],
                &UnrealTypes[Index],
                OutError))
        {
            return false;
        }
    }

    TSet<FGuid> RequestedOverrideIds;
    RequestedOverrideIds.Reserve(ExistingOverrideIds.Num());
    for (const FGuid& ExistingOverrideId : ExistingOverrideIds)
    {
        RequestedOverrideIds.Add(ExistingOverrideId);
    }

    TArray<FGuid> CompatibleOverrideIds;
    CompatibleOverrideIds.Reserve(RequestedOverrideIds.Num());
    for (const FPropertyBagPropertyDesc& TargetDesc : Descs)
    {
        if (!RequestedOverrideIds.Contains(TargetDesc.ID))
        {
            continue;
        }
        const FPropertyBagPropertyDesc* ExistingDesc =
            ExistingState.FindPropertyDescByID(TargetDesc.ID);
        if (ExistingDesc != nullptr && TargetDesc.CompatibleType(*ExistingDesc))
        {
            CompatibleOverrideIds.Add(TargetDesc.ID);
        }
    }

    FInstancedPropertyBag Candidate;
    if (Descs.IsEmpty())
    {
        Candidate.Reset();
    }
    else if (ExistingState.IsValid())
    {
        Candidate = ExistingState;
        Candidate.MigrateToNewBagInstanceWithOverrides(DefaultsBag, CompatibleOverrideIds);
    }
    else
    {
        Candidate = MoveTemp(DefaultsBag);
    }

    FUnifiedScriptStateMigrationReport Migration;
    Migration.AssetId = SelectedAssetId;
    Migration.AssetQualifiedName = MoveTemp(SelectedQualifiedName);
    Migration.DeclaredStateCount = Descs.Num();
    Migration.PreservedOverrideCount = CompatibleOverrideIds.Num();
    Migration.DefaultedStateCount = Descs.Num() - CompatibleOverrideIds.Num();
    Migration.DroppedOverrideCount = RequestedOverrideIds.Num() -
        CompatibleOverrideIds.Num();
    Migration.ClassBinding = MoveTemp(SelectedClassBinding);
    OutState = MoveTemp(Candidate);
    OutOverrideIds = MoveTemp(CompatibleOverrideIds);
    OutMigration = Migration;
    OutAssetPath = MoveTemp(SelectedAssetPath);
    OutError.Reset();
    return true;
}

bool FUnifiedScriptDeclarationMaterializer::MaterializeJson(
    const FString& DeclarationJson,
    const FString& RequestedAssetPath,
    const FInstancedPropertyBag& ExistingState,
    FInstancedPropertyBag& OutState,
    FString& OutAssetPath,
    FString& OutError)
{
    using namespace UE::UnifiedScript::Declaration::Private;

    const TArray<FGuid> LegacyOverrideIds = CollectAllPropertyIds(ExistingState);
    TArray<FGuid> MigratedOverrideIds;
    FUnifiedScriptStateMigrationReport Migration;
    return MaterializeJsonWithOverrides(
        DeclarationJson,
        RequestedAssetPath,
        ExistingState,
        LegacyOverrideIds,
        OutState,
        MigratedOverrideIds,
        Migration,
        OutAssetPath,
        OutError);
}

bool FUnifiedScriptDeclarationMaterializer::MaterializeJsonWithOverrides(
    const FString& DeclarationJson,
    const FString& RequestedAssetPath,
    const FInstancedPropertyBag& ExistingState,
    TConstArrayView<FGuid> ExistingOverrideIds,
    FInstancedPropertyBag& OutState,
    TArray<FGuid>& OutOverrideIds,
    FUnifiedScriptStateMigrationReport& OutMigration,
    FString& OutAssetPath,
    FString& OutError)
{
    using namespace UE::UnifiedScript::Declaration::Private;

    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(DeclarationJson);
    if (!FJsonSerializer::Deserialize(
            Reader, Root, FJsonSerializer::EFlags::StoreNumbersAsStrings) || !Root.IsValid())
    {
        OutError = FString::Printf(
            TEXT("Script Asset declaration JSON is invalid: %s"),
            *Reader->GetErrorMessage());
        return false;
    }

    FString ParsedSchema;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), ParsedSchema) ||
        (!ParsedSchema.Equals(LegacySchemaVersion, ESearchCase::CaseSensitive) &&
            !ParsedSchema.Equals(OverrideSchemaVersion, ESearchCase::CaseSensitive) &&
            !ParsedSchema.Equals(ClassBindingSchemaVersion, ESearchCase::CaseSensitive)))
    {
        OutError = FString::Printf(
            TEXT("Unsupported Script Asset declaration schema '%s'."),
            *ParsedSchema);
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
    if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr || Assets->IsEmpty())
    {
        OutError = TEXT("Script Asset declaration must contain at least one asset.");
        return false;
    }
    if (!ValidateUnifiedScriptClassBindingsJson(*Assets, ParsedSchema, OutError))
    {
        return false;
    }
    if (ParsedSchema.Equals(LegacySchemaVersion, ESearchCase::CaseSensitive) &&
        HasBlueprintOverrideMetadata(*Assets))
    {
        OutError = FString::Printf(
            TEXT("Script Asset declaration schema '%s' cannot contain Blueprint override metadata; use '%s'."),
            LegacySchemaVersion,
            OverrideSchemaVersion);
        return false;
    }

    TSharedPtr<FJsonObject> Asset;
    FString SelectedAssetPath;
    if (!SelectAsset(*Assets, RequestedAssetPath, Asset, SelectedAssetPath, OutError))
    {
        return false;
    }

    FString AssetIdText;
    FString AssetQualifiedName;
    FGuid AssetId;
    TOptional<FUnifiedScriptClassBindingDeclaration> SelectedClassBinding;
    if (!Asset->TryGetStringField(TEXT("id"), AssetIdText) ||
        !ParseSemanticId(AssetIdText, AssetId) ||
        !Asset->TryGetStringField(TEXT("qualifiedName"), AssetQualifiedName) ||
        AssetQualifiedName.IsEmpty())
    {
        OutError = FString::Printf(
            TEXT("Declaration asset '%s' has an invalid class identity."),
            *SelectedAssetPath);
        return false;
    }
    if (!FUnifiedScriptClassBindingDeclaration::ReadJson(
            Asset, SelectedClassBinding, OutError))
    {
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* States = nullptr;
    if (!Asset->TryGetArrayField(TEXT("states"), States) || States == nullptr)
    {
        OutError = FString::Printf(
            TEXT("Declaration asset '%s' must contain a states array."),
            *SelectedAssetPath);
        return false;
    }

    TArray<FPropertyBagPropertyDesc> Descs;
    TArray<TSharedPtr<FJsonValue>> Defaults;
    TArray<FStateUnrealType> UnrealTypes;
    TSet<FGuid> SeenIds;
    TSet<FName> SeenNames;
    Descs.Reserve(States->Num());
    Defaults.Reserve(States->Num());
    UnrealTypes.Reserve(States->Num());
    for (const TSharedPtr<FJsonValue>& StateValue : *States)
    {
        const TSharedPtr<FJsonObject> State = StateValue.IsValid()
            ? StateValue->AsObject()
            : nullptr;
        FString SemanticId;
        FString Name;
        FString TypeKind;
        FString MetadataName;
        if (!State.IsValid() ||
            !State->TryGetStringField(TEXT("id"), SemanticId) ||
            !State->TryGetStringField(TEXT("name"), Name) ||
            !State->TryGetStringField(TEXT("typeKind"), TypeKind) ||
            !State->TryGetStringField(TEXT("typeMetadataName"), MetadataName))
        {
            OutError = FString::Printf(
                TEXT("Declaration asset '%s' contains an incomplete state."),
                *SelectedAssetPath);
            return false;
        }

        FGuid Id;
        const FName PropertyName(Name);
        FStateUnrealType UnrealType;
        EPropertyBagPropertyType PropertyType = EPropertyBagPropertyType::None;
        const UObject* PropertyTypeObject = nullptr;
        if (!ParseSemanticId(SemanticId, Id))
        {
            OutError = FString::Printf(TEXT("State '%s' has invalid semantic id '%s'."), *Name, *SemanticId);
            return false;
        }
        if (Name.IsEmpty() || PropertyName.IsNone())
        {
            OutError = TEXT("Declaration state names cannot be empty.");
            return false;
        }
        if (SeenIds.Contains(Id) || SeenNames.Contains(PropertyName))
        {
            OutError = FString::Printf(TEXT("Declaration contains duplicate state '%s' or semantic id."), *Name);
            return false;
        }
        if (!ReadJsonUnrealType(State, UnrealType) ||
            (TypeKind == TEXT("array") || TypeKind == TEXT("set") || TypeKind == TEXT("map")
                ? false : !ResolvePropertyType(
                    TypeKind,
                    MetadataName,
                    UnrealType,
                    PropertyType,
                    PropertyTypeObject)))
        {
            OutError = FString::Printf(
                TEXT("State '%s' uses unsupported declaration type '%s' (%s)."),
                *Name,
                *TypeKind,
                *MetadataName);
            return false;
        }

        bool bInstanceEditable = false;
        bool bSaveGame = false;
        bool bReplicated = false;
        FString Category;
        const TSharedPtr<FJsonObject>* Exposure = nullptr;
        if (State->TryGetObjectField(TEXT("exposure"), Exposure) && Exposure != nullptr)
        {
            (*Exposure)->TryGetBoolField(TEXT("instanceEditable"), bInstanceEditable);
            (*Exposure)->TryGetBoolField(TEXT("saveGame"), bSaveGame);
            (*Exposure)->TryGetBoolField(TEXT("replicated"), bReplicated);
            (*Exposure)->TryGetStringField(TEXT("category"), Category);
        }

        EPropertyFlags Flags = CPF_Edit;
        if (!bInstanceEditable)
        {
            Flags |= CPF_EditConst;
        }
        if (bSaveGame)
        {
            Flags |= CPF_SaveGame;
        }
        if (bReplicated)
        {
            Flags |= CPF_Net;
        }

        FPropertyBagPropertyDesc Desc;
        if (TypeKind == TEXT("array") || TypeKind == TEXT("set") || TypeKind == TEXT("map"))
        {
            if (!ResolveContainerPropertyType(TypeKind, MetadataName, UnrealType, Desc))
            {
                OutError = FString::Printf(
                    TEXT("State '%s' uses unsupported or nested collection metadata."), *Name);
                return false;
            }
            Desc.Name = PropertyName;
            Desc.PropertyFlags = static_cast<uint64>(Flags);
        }
        else
        {
            Desc = FPropertyBagPropertyDesc(
                PropertyName,
                EPropertyBagContainerType::None,
                PropertyType,
                PropertyTypeObject,
                Flags);
        }
        Descs.Add(MoveTemp(Desc));
        FPropertyBagPropertyDesc& AddedDesc = Descs.Last();
        AddedDesc.ID = Id;
#if WITH_EDITOR
        AddedDesc.SetMetaData(TEXT("DisplayName"), Name);
        AddedDesc.SetMetaData(TEXT("UnifiedScriptSemanticId"), SemanticId);
        if (!Category.IsEmpty())
        {
            AddedDesc.SetMetaData(TEXT("Category"), Category);
        }
#endif
        Defaults.Add(State->TryGetField(TEXT("defaultValue")));
        UnrealTypes.Add(MoveTemp(UnrealType));
        SeenIds.Add(Id);
        SeenNames.Add(PropertyName);
    }

    FInstancedPropertyBag DefaultsBag;
    if (!Descs.IsEmpty() &&
        DefaultsBag.AddProperties(Descs) != EPropertyBagAlterationResult::Success)
    {
        OutError = TEXT("Unreal could not create the declared Script State layout.");
        return false;
    }
    for (const FPropertyBagPropertyDesc& InputDesc : Descs)
    {
        const FPropertyBagPropertyDesc* MaterializedDesc =
            DefaultsBag.FindPropertyDescByID(InputDesc.ID);
        if (MaterializedDesc == nullptr ||
            !ValidateCollectionHashability(*MaterializedDesc, OutError))
        {
            if (OutError.IsEmpty())
            {
                OutError = TEXT("Unreal could not resolve a declared Script State descriptor.");
            }
            return false;
        }
    }
    for (int32 Index = 0; Index < Descs.Num(); ++Index)
    {
        const FPropertyBagPropertyDesc* MaterializedDesc =
            DefaultsBag.FindPropertyDescByID(Descs[Index].ID);
        if (MaterializedDesc == nullptr)
        {
            OutError = TEXT("Unreal could not resolve a declared Script State descriptor.");
            return false;
        }
        if (!SetDefaultValue(
                DefaultsBag,
                *MaterializedDesc,
                Defaults[Index],
                &UnrealTypes[Index],
                OutError))
        {
            return false;
        }
    }

    TSet<FGuid> RequestedOverrideIds;
    RequestedOverrideIds.Reserve(ExistingOverrideIds.Num());
    for (const FGuid& ExistingOverrideId : ExistingOverrideIds)
    {
        RequestedOverrideIds.Add(ExistingOverrideId);
    }

    TArray<FGuid> CompatibleOverrideIds;
    CompatibleOverrideIds.Reserve(RequestedOverrideIds.Num());
    for (const FPropertyBagPropertyDesc& TargetDesc : Descs)
    {
        if (!RequestedOverrideIds.Contains(TargetDesc.ID))
        {
            continue;
        }

        const FPropertyBagPropertyDesc* ExistingDesc =
            ExistingState.FindPropertyDescByID(TargetDesc.ID);
        if (ExistingDesc != nullptr && TargetDesc.CompatibleType(*ExistingDesc))
        {
            CompatibleOverrideIds.Add(TargetDesc.ID);
        }
    }

    FInstancedPropertyBag Candidate;
    if (Descs.IsEmpty())
    {
        Candidate.Reset();
    }
    else if (ExistingState.IsValid())
    {
        Candidate = ExistingState;
        Candidate.MigrateToNewBagInstanceWithOverrides(
            DefaultsBag,
            CompatibleOverrideIds);
    }
    else
    {
        Candidate = MoveTemp(DefaultsBag);
    }

    FUnifiedScriptStateMigrationReport Migration;
    Migration.AssetId = AssetId;
    Migration.AssetQualifiedName = MoveTemp(AssetQualifiedName);
    Migration.DeclaredStateCount = Descs.Num();
    Migration.PreservedOverrideCount = CompatibleOverrideIds.Num();
    Migration.DefaultedStateCount = Descs.Num() - CompatibleOverrideIds.Num();
    Migration.DroppedOverrideCount = RequestedOverrideIds.Num() -
        CompatibleOverrideIds.Num();
    Migration.ClassBinding = MoveTemp(SelectedClassBinding);

    OutState = MoveTemp(Candidate);
    OutOverrideIds = MoveTemp(CompatibleOverrideIds);
    OutMigration = Migration;
    OutAssetPath = MoveTemp(SelectedAssetPath);
    OutError.Reset();
    return true;
}

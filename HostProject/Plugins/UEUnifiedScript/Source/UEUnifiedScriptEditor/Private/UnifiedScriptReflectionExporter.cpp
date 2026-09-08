#include "UnifiedScriptReflectionExporter.h"

#include "Algo/Unique.h"
#include "AssetRegistry/AssetData.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "AssetRegistry/IAssetRegistry.h"
#include "Dom/JsonObject.h"
#include "Engine/Blueprint.h"
#include "HAL/FileManager.h"
#include "Misc/EngineVersion.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UObject/Class.h"
#include "UObject/Package.h"
#include "UObject/UObjectIterator.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::Private
{
constexpr const TCHAR* ReflectionSchema = TEXT("unifiedscript.unreal-reflection/v0.5");
constexpr const TCHAR* InternalBindingMetadata = TEXT("UnifiedScriptBindingInternal");

TSharedRef<FJsonObject> TypeReference(
    const FString& Kind,
    const FString& NativeName,
    const FString& TypePath = FString(),
    const FString& HeaderPath = FString())
{
    TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
    Json->SetStringField(TEXT("kind"), Kind);
    Json->SetStringField(TEXT("nativeName"), NativeName);
    if (!TypePath.IsEmpty())
    {
        Json->SetStringField(TEXT("typePath"), TypePath);
    }
    if (!HeaderPath.IsEmpty())
    {
        Json->SetStringField(TEXT("headerPath"), HeaderPath);
    }
    return Json;
}

template <typename TField>
TSharedRef<FJsonObject> BlueprintMetadata(const TField* Field)
{
    TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
    const FString DisplayName = Field->GetDisplayNameText().ToString();
    const FString Category = Field->GetMetaData(TEXT("Category"));
    const FString Tooltip = Field->GetToolTipText().ToString();
    const FString DeprecationMessage = Field->GetMetaData(TEXT("DeprecationMessage"));
    const FString WorldContext = Field->GetMetaData(TEXT("WorldContext"));
    const FString LatentInfo = Field->GetMetaData(TEXT("LatentInfo"));
    if (!DisplayName.IsEmpty()) Json->SetStringField(TEXT("displayName"), DisplayName);
    if (!Category.IsEmpty()) Json->SetStringField(TEXT("category"), Category);
    if (!Tooltip.IsEmpty()) Json->SetStringField(TEXT("tooltip"), Tooltip);
    const bool bDeprecated = Field->HasMetaData(TEXT("DeprecatedFunction")) ||
        Field->HasMetaData(TEXT("DeprecatedProperty"));
    Json->SetBoolField(TEXT("deprecated"), bDeprecated);
    if (bDeprecated && !DeprecationMessage.IsEmpty())
    {
        Json->SetStringField(TEXT("deprecationMessage"), DeprecationMessage);
    }
    if (!WorldContext.IsEmpty()) Json->SetStringField(TEXT("worldContextParameter"), WorldContext);
    if (Field->HasMetaData(TEXT("Latent"))) Json->SetBoolField(TEXT("latent"), true);
    if (!LatentInfo.IsEmpty()) Json->SetStringField(TEXT("latentInfoParameter"), LatentInfo);
    if (Field->HasMetaData(TEXT("BlueprintThreadSafe")))
    {
        Json->SetBoolField(TEXT("threadSafe"), true);
    }
    if (Field->HasMetaData(TEXT("CallableWithoutWorldContext")))
    {
        Json->SetBoolField(TEXT("callableWithoutWorldContext"), true);
    }
    return Json;
}

FString ReflectedHeaderPath(const UField* Type)
{
    FString HeaderPath = Type->GetMetaData(TEXT("ModuleRelativePath"));
    HeaderPath.ReplaceInline(TEXT("\\"), TEXT("/"));
    HeaderPath.RemoveFromStart(TEXT("Public/"));
    HeaderPath.RemoveFromStart(TEXT("Private/"));
    HeaderPath.RemoveFromStart(TEXT("Classes/"));
    return HeaderPath;
}

FString CanonicalStructTypePath(const UScriptStruct* Struct)
{
    if (Struct->IsChildOf(TBaseStructure<FVector>::Get()))
    {
        return TEXT("/Script/CoreUObject.Vector");
    }
    if (Struct->IsChildOf(TBaseStructure<FRotator>::Get()))
    {
        return TEXT("/Script/CoreUObject.Rotator");
    }
    if (Struct->IsChildOf(TBaseStructure<FTransform>::Get()))
    {
        return TEXT("/Script/CoreUObject.Transform");
    }
    return Struct->GetPathName();
}

FString CanonicalNativeName(const FProperty* Property)
{
    if (const FArrayProperty* Array = CastField<FArrayProperty>(Property))
    {
        return FString::Printf(TEXT("TArray<%s>"), *CanonicalNativeName(Array->Inner));
    }
    if (const FSetProperty* Set = CastField<FSetProperty>(Property))
    {
        return FString::Printf(TEXT("TSet<%s>"), *CanonicalNativeName(Set->ElementProp));
    }
    if (const FMapProperty* Map = CastField<FMapProperty>(Property))
    {
        return FString::Printf(
            TEXT("TMap<%s,%s>"),
            *CanonicalNativeName(Map->KeyProp),
            *CanonicalNativeName(Map->ValueProp));
    }
    if (const FInterfaceProperty* Interface = CastField<FInterfaceProperty>(Property))
    {
        // UClass::GetName() omits the reflected U prefix, so the generated
        // C++ interface is the I-prefixed form of that stable name.
        const FString InterfaceName = Interface->InterfaceClass != nullptr
            ? Interface->InterfaceClass->GetName()
            : FString();
        return FString::Printf(TEXT("TScriptInterface<I%s>"), *InterfaceName);
    }
    return Property->GetCPPType();
}

TSharedRef<FJsonObject> PropertyTypeReference(const FProperty* Property, int32 Depth = 0)
{
    const FString NativeName = CanonicalNativeName(Property);
    if (Depth > 16) return TypeReference(TEXT("unsupported"), NativeName);
    if (const FDelegateProperty* Delegate = CastField<FDelegateProperty>(Property))
    {
        const UFunction* Signature = Delegate->SignatureFunction;
        if (!Signature) return TypeReference(TEXT("unsupported"), NativeName);
        TSharedRef<FJsonObject> Json = TypeReference(TEXT("callback"), NativeName, Signature->GetPathName(), ReflectedHeaderPath(Signature));
        TSharedRef<FJsonObject> Callback = MakeShared<FJsonObject>();
        const FProperty* Return = Signature->GetReturnProperty();
        Callback->SetObjectField(TEXT("returnType"), Return ? PropertyTypeReference(Return, Depth + 1) : TypeReference(TEXT("void"), TEXT("void")));
        TArray<TSharedPtr<FJsonValue>> Parameters;
        for (TFieldIterator<FProperty> It(Signature, EFieldIteratorFlags::ExcludeSuper); It; ++It)
        {
            const FProperty* Parameter = *It;
            if (!Parameter->HasAnyPropertyFlags(CPF_Parm) || Parameter->HasAnyPropertyFlags(CPF_ReturnParm)) continue;
            TSharedRef<FJsonObject> ParameterJson = MakeShared<FJsonObject>();
            ParameterJson->SetStringField(TEXT("name"), Parameter->GetName());
            ParameterJson->SetObjectField(TEXT("type"), PropertyTypeReference(Parameter, Depth + 1));
            const bool bOutput = Parameter->HasAnyPropertyFlags(CPF_OutParm) && !Parameter->HasAnyPropertyFlags(CPF_ConstParm);
            const bool bReference = Parameter->HasAnyPropertyFlags(CPF_ReferenceParm);
            ParameterJson->SetStringField(TEXT("direction"), bOutput && bReference ? TEXT("inputOutput") : bOutput ? TEXT("output") : TEXT("input"));
            Parameters.Add(MakeShared<FJsonValueObject>(ParameterJson));
        }
        Callback->SetArrayField(TEXT("parameters"), Parameters);
        Json->SetObjectField(TEXT("callbackSignature"), Callback);
        return Json;
    }
    if (CastField<FBoolProperty>(Property))
    {
        return TypeReference(TEXT("boolean"), NativeName);
    }
    if (CastField<FInt8Property>(Property))
    {
        return TypeReference(TEXT("int8"), NativeName);
    }
    if (const FByteProperty* Byte = CastField<FByteProperty>(Property))
    {
        return Byte->Enum
            ? TypeReference(TEXT("enum"), NativeName, Byte->Enum->GetPathName())
            : TypeReference(TEXT("uint8"), NativeName);
    }
    if (CastField<FInt16Property>(Property))
    {
        return TypeReference(TEXT("int16"), NativeName);
    }
    if (CastField<FUInt16Property>(Property))
    {
        return TypeReference(TEXT("uint16"), NativeName);
    }
    if (CastField<FIntProperty>(Property))
    {
        return TypeReference(TEXT("int32"), NativeName);
    }
    if (CastField<FUInt32Property>(Property))
    {
        return TypeReference(TEXT("uint32"), NativeName);
    }
    if (CastField<FInt64Property>(Property))
    {
        return TypeReference(TEXT("int64"), NativeName);
    }
    if (CastField<FUInt64Property>(Property))
    {
        return TypeReference(TEXT("uint64"), NativeName);
    }
    if (CastField<FFloatProperty>(Property))
    {
        return TypeReference(TEXT("float32"), NativeName);
    }
    if (CastField<FDoubleProperty>(Property))
    {
        return TypeReference(TEXT("float64"), NativeName);
    }
    if (CastField<FStrProperty>(Property))
    {
        return TypeReference(TEXT("string"), NativeName);
    }
    if (CastField<FNameProperty>(Property))
    {
        return TypeReference(TEXT("name"), NativeName);
    }
    if (CastField<FTextProperty>(Property))
    {
        return TypeReference(TEXT("text"), NativeName);
    }
    if (const FEnumProperty* Enum = CastField<FEnumProperty>(Property))
    {
        return TypeReference(
            TEXT("enum"),
            NativeName,
            Enum->GetEnum()->GetPathName(),
            ReflectedHeaderPath(Enum->GetEnum()));
    }
    if (const FStructProperty* Struct = CastField<FStructProperty>(Property))
    {
        return TypeReference(
            TEXT("struct"),
            NativeName,
            CanonicalStructTypePath(Struct->Struct),
            ReflectedHeaderPath(Struct->Struct));
    }
    if (const FArrayProperty* Array = CastField<FArrayProperty>(Property))
    {
        TSharedRef<FJsonObject> Json = TypeReference(TEXT("array"), NativeName);
        Json->SetObjectField(TEXT("elementType"), PropertyTypeReference(Array->Inner, Depth + 1));
        return Json;
    }
    if (const FSetProperty* Set = CastField<FSetProperty>(Property))
    {
        TSharedRef<FJsonObject> Json = TypeReference(TEXT("set"), NativeName);
        Json->SetObjectField(TEXT("elementType"), PropertyTypeReference(Set->ElementProp, Depth + 1));
        return Json;
    }
    if (const FMapProperty* Map = CastField<FMapProperty>(Property))
    {
        TSharedRef<FJsonObject> Json = TypeReference(TEXT("map"), NativeName);
        Json->SetObjectField(TEXT("keyType"), PropertyTypeReference(Map->KeyProp, Depth + 1));
        Json->SetObjectField(TEXT("valueType"), PropertyTypeReference(Map->ValueProp, Depth + 1));
        return Json;
    }
    if (const FSoftClassProperty* SoftClass = CastField<FSoftClassProperty>(Property))
    {
        return TypeReference(
            TEXT("softClass"),
            NativeName,
            SoftClass->MetaClass->GetPathName(),
            ReflectedHeaderPath(SoftClass->MetaClass));
    }
    if (const FSoftObjectProperty* SoftObject = CastField<FSoftObjectProperty>(Property))
    {
        return TypeReference(
            TEXT("softObject"),
            NativeName,
            SoftObject->PropertyClass->GetPathName(),
            ReflectedHeaderPath(SoftObject->PropertyClass));
    }
    if (const FInterfaceProperty* Interface = CastField<FInterfaceProperty>(Property))
    {
        return TypeReference(
            TEXT("interface"),
            NativeName,
            Interface->InterfaceClass->GetPathName(),
            ReflectedHeaderPath(Interface->InterfaceClass));
    }
    if (CastField<FWeakObjectProperty>(Property) || CastField<FLazyObjectProperty>(Property) ||
        CastField<FInterfaceProperty>(Property))
    {
        return TypeReference(TEXT("unsupported"), NativeName);
    }
    if (const FClassProperty* Class = CastField<FClassProperty>(Property))
    {
        return TypeReference(
            TEXT("class"),
            NativeName,
            Class->MetaClass->GetPathName(),
            ReflectedHeaderPath(Class->MetaClass));
    }
    if (const FObjectPropertyBase* Object = CastField<FObjectPropertyBase>(Property))
    {
        return TypeReference(
            TEXT("object"),
            NativeName,
            Object->PropertyClass->GetPathName(),
            ReflectedHeaderPath(Object->PropertyClass));
    }
    return TypeReference(TEXT("unsupported"), NativeName);
}

FString ModuleName(const UObject* Type)
{
    FString Name = Type->GetOutermost()->GetName();
    if (Name.RemoveFromStart(TEXT("/Script/")))
    {
        return Name;
    }
    if (!Name.RemoveFromStart(TEXT("/")))
    {
        return FString();
    }
    FString Mount;
    FString Remainder;
    if (Name.Split(TEXT("/"), &Mount, &Remainder) ||
        Name.Split(TEXT("."), &Mount, &Remainder))
    {
        return Mount;
    }
    return Name;
}

const TCHAR* PropertyNativeAccess(const FProperty* Property)
{
    if (Property->HasAnyPropertyFlags(CPF_NativeAccessSpecifierPrivate))
    {
        return TEXT("private");
    }
    if (Property->HasAnyPropertyFlags(CPF_NativeAccessSpecifierProtected))
    {
        return TEXT("protected");
    }
    return TEXT("public");
}

const TCHAR* FunctionNativeAccess(const UFunction* Function)
{
    if (Function->HasAnyFunctionFlags(FUNC_Private))
    {
        return TEXT("private");
    }
    if (Function->HasAnyFunctionFlags(FUNC_Protected))
    {
        return TEXT("protected");
    }
    return TEXT("public");
}

TSharedRef<FJsonObject> ExportProperty(const UStruct* Owner, const FProperty* Property)
{
    TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
    const FString OwnerPath = Owner->GetPathName();
    Json->SetStringField(TEXT("path"), OwnerPath + TEXT(":") + Property->GetName());
    Json->SetStringField(TEXT("name"), Property->GetName());
    Json->SetObjectField(TEXT("type"), PropertyTypeReference(Property));

    TSharedRef<FJsonObject> Access = MakeShared<FJsonObject>();
    Access->SetBoolField(TEXT("readable"), true);
    Access->SetBoolField(
        TEXT("writable"),
        !Property->HasAnyPropertyFlags(CPF_BlueprintReadOnly | CPF_EditConst));
    Access->SetBoolField(TEXT("editorVisible"), Property->HasAnyPropertyFlags(CPF_Edit));
    Access->SetStringField(TEXT("nativeAccess"), PropertyNativeAccess(Property));
    Json->SetObjectField(TEXT("access"), Access);
    Json->SetObjectField(TEXT("metadata"), BlueprintMetadata(Property));
    return Json;
}

TSharedRef<FJsonObject> ExportFunction(const UClass* Class, const UFunction* Function)
{
    TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
    const FString ClassPath = Class->GetPathName();
    Json->SetStringField(TEXT("path"), ClassPath + TEXT(":") + Function->GetName());
    Json->SetStringField(TEXT("name"), Function->GetName());

    const FProperty* ReturnProperty = Function->GetReturnProperty();
    Json->SetObjectField(
        TEXT("returnType"),
        ReturnProperty
            ? PropertyTypeReference(ReturnProperty)
            : TypeReference(TEXT("void"), TEXT("void")));

    TArray<TSharedPtr<FJsonValue>> Parameters;
    for (TFieldIterator<FProperty> It(Function, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const FProperty* Parameter = *It;
        if (!Parameter->HasAnyPropertyFlags(CPF_Parm) ||
            Parameter->HasAnyPropertyFlags(CPF_ReturnParm))
        {
            continue;
        }

        TSharedRef<FJsonObject> ParameterJson = MakeShared<FJsonObject>();
        ParameterJson->SetStringField(TEXT("name"), Parameter->GetName());
        ParameterJson->SetObjectField(TEXT("type"), PropertyTypeReference(Parameter));
        const bool bOutput =
            Parameter->HasAnyPropertyFlags(CPF_OutParm) &&
            !Parameter->HasAnyPropertyFlags(CPF_ConstParm);
        const bool bReference = Parameter->HasAnyPropertyFlags(CPF_ReferenceParm);
        ParameterJson->SetStringField(
            TEXT("direction"),
            bOutput && bReference ? TEXT("inputOutput") : bOutput ? TEXT("output") : TEXT("input"));
        const FString DefaultKey = TEXT("CPP_Default_") + Parameter->GetName();
        if (!bOutput && Function->HasMetaData(*DefaultKey))
        {
            ParameterJson->SetStringField(TEXT("defaultValue"), Function->GetMetaData(*DefaultKey));
        }
        Parameters.Add(MakeShared<FJsonValueObject>(ParameterJson));
    }
    Json->SetArrayField(TEXT("parameters"), Parameters);

    TSharedRef<FJsonObject> Traits = MakeShared<FJsonObject>();
    Traits->SetBoolField(TEXT("static"), Function->HasAnyFunctionFlags(FUNC_Static));
    Traits->SetBoolField(TEXT("pure"), Function->HasAnyFunctionFlags(FUNC_BlueprintPure));
    Traits->SetBoolField(
        TEXT("callable"),
        Function->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_BlueprintPure));
    if (Class->HasAnyClassFlags(CLASS_Interface) &&
        !Function->HasAnyFunctionFlags(FUNC_Static | FUNC_BlueprintEvent))
    {
        Traits->SetBoolField(TEXT("nativeInterfaceCall"), true);
    }
    Traits->SetStringField(TEXT("nativeAccess"), FunctionNativeAccess(Function));
    Json->SetObjectField(TEXT("traits"), Traits);
    TSharedRef<FJsonObject> Metadata = BlueprintMetadata(Function);
    const bool bThreadSafe = Function->HasMetaData(TEXT("BlueprintThreadSafe")) ||
        !Function->HasMetaData(TEXT("NotBlueprintThreadSafe")) &&
        Class->HasMetaData(TEXT("BlueprintThreadSafe"));
    if (bThreadSafe)
    {
        Metadata->SetBoolField(TEXT("threadSafe"), true);
    }
    Json->SetObjectField(TEXT("metadata"), Metadata);
    return Json;
}

const UFunction* DelegateSignature(const FProperty* Property)
{
    if (const FDelegateProperty* Delegate = CastField<FDelegateProperty>(Property))
    {
        return Delegate->SignatureFunction;
    }
    if (const FMulticastDelegateProperty* Delegate = CastField<FMulticastDelegateProperty>(Property))
    {
        return Delegate->SignatureFunction;
    }
    return nullptr;
}

TSharedRef<FJsonObject> ExportDelegate(
    const UClass* Class,
    const FProperty* Property,
    const UFunction* Signature)
{
    TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
    const FString ClassPath = Class->GetPathName();
    Json->SetStringField(TEXT("path"), ClassPath + TEXT(":") + Property->GetName());
    Json->SetStringField(TEXT("name"), Property->GetName());
    Json->SetStringField(TEXT("signaturePath"), Signature->GetPathName());

    const FProperty* ReturnProperty = Signature->GetReturnProperty();
    Json->SetObjectField(
        TEXT("returnType"),
        ReturnProperty
            ? PropertyTypeReference(ReturnProperty)
            : TypeReference(TEXT("void"), TEXT("void")));

    TArray<TSharedPtr<FJsonValue>> Parameters;
    for (TFieldIterator<FProperty> It(Signature, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const FProperty* Parameter = *It;
        if (!Parameter->HasAnyPropertyFlags(CPF_Parm) ||
            Parameter->HasAnyPropertyFlags(CPF_ReturnParm))
        {
            continue;
        }

        TSharedRef<FJsonObject> ParameterJson = MakeShared<FJsonObject>();
        ParameterJson->SetStringField(TEXT("name"), Parameter->GetName());
        ParameterJson->SetObjectField(TEXT("type"), PropertyTypeReference(Parameter));
        const bool bOutput =
            Parameter->HasAnyPropertyFlags(CPF_OutParm) &&
            !Parameter->HasAnyPropertyFlags(CPF_ConstParm);
        const bool bReference = Parameter->HasAnyPropertyFlags(CPF_ReferenceParm);
        ParameterJson->SetStringField(
            TEXT("direction"),
            bOutput && bReference ? TEXT("inputOutput") : bOutput ? TEXT("output") : TEXT("input"));
        Parameters.Add(MakeShared<FJsonValueObject>(ParameterJson));
    }
    Json->SetArrayField(TEXT("parameters"), Parameters);

    TSharedRef<FJsonObject> Traits = MakeShared<FJsonObject>();
    Traits->SetStringField(
        TEXT("kind"),
        CastField<FMulticastDelegateProperty>(Property) ? TEXT("multicast") : TEXT("singleCast"));
    Traits->SetBoolField(
        TEXT("blueprintAssignable"),
        Property->HasAnyPropertyFlags(CPF_BlueprintAssignable));
    Traits->SetBoolField(
        TEXT("blueprintCallable"),
        Property->HasAnyPropertyFlags(CPF_BlueprintCallable));
    Traits->SetBoolField(
        TEXT("blueprintAuthorityOnly"),
        Property->HasAnyPropertyFlags(CPF_BlueprintAuthorityOnly));
    Traits->SetStringField(TEXT("nativeAccess"), PropertyNativeAccess(Property));
    Json->SetObjectField(TEXT("traits"), Traits);
    Json->SetObjectField(TEXT("metadata"), BlueprintMetadata(Property));
    return Json;
}

TSharedRef<FJsonObject> ExportClass(
    const UClass* Class,
    FUnifiedScriptReflectionExportResult& Result)
{
    TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
    const FString ClassPath = Class->GetPathName();
    Json->SetStringField(TEXT("path"), ClassPath);
    Json->SetStringField(TEXT("name"), Class->GetName());
    Json->SetStringField(TEXT("nativeName"), FString(Class->GetPrefixCPP()) + Class->GetName());
    Json->SetStringField(TEXT("module"), ModuleName(Class));
    FString HeaderPath = Class->GetMetaData(TEXT("ModuleRelativePath"));
    HeaderPath.ReplaceInline(TEXT("\\"), TEXT("/"));
    HeaderPath.RemoveFromStart(TEXT("Public/"));
    HeaderPath.RemoveFromStart(TEXT("Private/"));
    HeaderPath.RemoveFromStart(TEXT("Classes/"));
    if (!HeaderPath.IsEmpty())
    {
        Json->SetStringField(TEXT("headerPath"), HeaderPath);
    }
    Json->SetStringField(TEXT("kind"), TEXT("class"));
    if (const UClass* Super = Class->GetSuperClass())
    {
        Json->SetStringField(TEXT("baseTypePath"), Super->GetPathName());
    }

    TSharedRef<FJsonObject> Traits = MakeShared<FJsonObject>();
    Traits->SetBoolField(TEXT("abstract"), Class->HasAnyClassFlags(CLASS_Abstract));
    Traits->SetBoolField(
        TEXT("blueprintType"),
        Class->GetBoolMetaData(TEXT("BlueprintType")) ||
            Cast<UBlueprint>(Class->ClassGeneratedBy) != nullptr);
    Traits->SetBoolField(TEXT("interface"), Class->HasAnyClassFlags(CLASS_Interface));
    Json->SetObjectField(TEXT("traits"), Traits);
    Json->SetObjectField(TEXT("metadata"), BlueprintMetadata(Class));
    TArray<TSharedPtr<FJsonValue>> InterfaceValues;
    for (const FImplementedInterface& Interface : Class->Interfaces)
    {
        if (Interface.Class != nullptr)
        {
            InterfaceValues.Add(MakeShared<FJsonValueString>(Interface.Class->GetPathName()));
        }
    }
    Json->SetArrayField(TEXT("interfacePaths"), InterfaceValues);
    Json->SetArrayField(TEXT("enumValues"), {});

    TArray<const FProperty*> Properties;
    TArray<const FProperty*> Delegates;
    for (TFieldIterator<FProperty> It(Class, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const FProperty* Property = *It;
        if (const UFunction* Signature = DelegateSignature(Property))
        {
            if (Property->HasAnyPropertyFlags(CPF_BlueprintAssignable | CPF_BlueprintCallable))
            {
                Delegates.Add(Property);
            }
            continue;
        }
        if (!Property->HasAnyPropertyFlags(CPF_Parm) &&
            Property->HasAnyPropertyFlags(CPF_BlueprintVisible))
        {
            Properties.Add(Property);
        }
    }
    Properties.Sort([](const FProperty& Left, const FProperty& Right)
    {
        return Left.GetName() < Right.GetName();
    });
    TArray<TSharedPtr<FJsonValue>> PropertyValues;
    for (const FProperty* Property : Properties)
    {
        PropertyValues.Add(MakeShared<FJsonValueObject>(ExportProperty(Class, Property)));
        ++Result.PropertyCount;
    }
    Json->SetArrayField(TEXT("properties"), PropertyValues);

    Delegates.Sort([](const FProperty& Left, const FProperty& Right)
    {
        return Left.GetName() < Right.GetName();
    });
    TArray<TSharedPtr<FJsonValue>> DelegateValues;
    for (const FProperty* Property : Delegates)
    {
        const UFunction* Signature = DelegateSignature(Property);
        check(Signature != nullptr);
        DelegateValues.Add(MakeShared<FJsonValueObject>(ExportDelegate(Class, Property, Signature)));
        ++Result.DelegateCount;
    }
    Json->SetArrayField(TEXT("delegates"), DelegateValues);

    TArray<const UFunction*> Functions;
    for (TFieldIterator<UFunction> It(Class, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const UFunction* Function = *It;
        if (Function->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_BlueprintPure))
        {
            Functions.Add(Function);
        }
    }
    Functions.Sort([](const UFunction& Left, const UFunction& Right)
    {
        return Left.GetName() < Right.GetName();
    });
    TArray<TSharedPtr<FJsonValue>> FunctionValues;
    for (const UFunction* Function : Functions)
    {
        FunctionValues.Add(MakeShared<FJsonValueObject>(ExportFunction(Class, Function)));
        ++Result.FunctionCount;
    }
    Json->SetArrayField(TEXT("functions"), FunctionValues);
    ++Result.TypeCount;
    return Json;
}

bool IsBuiltInStruct(const UScriptStruct* Struct)
{
    return Struct->IsChildOf(TBaseStructure<FVector>::Get()) ||
        Struct->IsChildOf(TBaseStructure<FRotator>::Get()) ||
        Struct->IsChildOf(TBaseStructure<FTransform>::Get());
}

void CollectReferencedStructs(
    const FProperty* Property,
    TSet<const UScriptStruct*>& Structs,
    int32 Depth = 0)
{
    if (Depth > 16) return;
    if (const FStructProperty* StructProperty = CastField<FStructProperty>(Property))
    {
        if (!IsBuiltInStruct(StructProperty->Struct))
        {
            Structs.Add(StructProperty->Struct);
        }
        return;
    }
    if (const FArrayProperty* Array = CastField<FArrayProperty>(Property))
    {
        CollectReferencedStructs(Array->Inner, Structs, Depth + 1);
    }
    else if (const FSetProperty* Set = CastField<FSetProperty>(Property))
    {
        CollectReferencedStructs(Set->ElementProp, Structs, Depth + 1);
    }
    else if (const FMapProperty* Map = CastField<FMapProperty>(Property))
    {
        CollectReferencedStructs(Map->KeyProp, Structs, Depth + 1);
        CollectReferencedStructs(Map->ValueProp, Structs, Depth + 1);
    }
    else if (const UFunction* Signature = DelegateSignature(Property); Signature != nullptr)
    {
        for (TFieldIterator<FProperty> It(Signature, EFieldIteratorFlags::ExcludeSuper); It; ++It)
            if (It->HasAnyPropertyFlags(CPF_Parm)) CollectReferencedStructs(*It, Structs, Depth + 1);
    }
}

void CollectReferencedEnums(const FProperty* Property, TSet<const UEnum*>& Enums, int32 Depth = 0)
{
    if (Depth > 16) return;
    if (const FEnumProperty* EnumProperty = CastField<FEnumProperty>(Property))
    {
        Enums.Add(EnumProperty->GetEnum());
        return;
    }
    if (const FByteProperty* ByteProperty = CastField<FByteProperty>(Property))
    {
        if (ByteProperty->Enum != nullptr) Enums.Add(ByteProperty->Enum);
        return;
    }
    if (const FArrayProperty* Array = CastField<FArrayProperty>(Property))
    {
        CollectReferencedEnums(Array->Inner, Enums, Depth + 1);
    }
    else if (const FSetProperty* Set = CastField<FSetProperty>(Property))
    {
        CollectReferencedEnums(Set->ElementProp, Enums, Depth + 1);
    }
    else if (const FMapProperty* Map = CastField<FMapProperty>(Property))
    {
        CollectReferencedEnums(Map->KeyProp, Enums, Depth + 1);
        CollectReferencedEnums(Map->ValueProp, Enums, Depth + 1);
    }
    else if (const UFunction* Signature = DelegateSignature(Property); Signature != nullptr)
    {
        for (TFieldIterator<FProperty> It(Signature, EFieldIteratorFlags::ExcludeSuper); It; ++It)
            if (It->HasAnyPropertyFlags(CPF_Parm)) CollectReferencedEnums(*It, Enums, Depth + 1);
    }
}

void CollectReferencedClasses(const FProperty* Property, TSet<const UClass*>& Classes, int32 Depth = 0)
{
    if (Property == nullptr || Depth > 16) return;
    if (const FInterfaceProperty* Interface = CastField<FInterfaceProperty>(Property))
    {
        if (Interface->InterfaceClass != nullptr)
        {
            Classes.Add(Interface->InterfaceClass);
        }
        return;
    }
    if (const FObjectPropertyBase* Object = CastField<FObjectPropertyBase>(Property))
    {
        if (Object->PropertyClass != nullptr &&
            Object->PropertyClass->HasAnyClassFlags(CLASS_Interface))
        {
            Classes.Add(Object->PropertyClass);
        }
        return;
    }
    if (const FArrayProperty* Array = CastField<FArrayProperty>(Property))
    {
        CollectReferencedClasses(Array->Inner, Classes, Depth + 1);
    }
    else if (const FSetProperty* Set = CastField<FSetProperty>(Property))
    {
        CollectReferencedClasses(Set->ElementProp, Classes, Depth + 1);
    }
    else if (const FMapProperty* Map = CastField<FMapProperty>(Property))
    {
        CollectReferencedClasses(Map->KeyProp, Classes, Depth + 1);
        CollectReferencedClasses(Map->ValueProp, Classes, Depth + 1);
    }
    else if (const FStructProperty* Struct = CastField<FStructProperty>(Property))
    {
        for (TFieldIterator<FProperty> It(Struct->Struct, EFieldIteratorFlags::ExcludeSuper); It; ++It)
        {
            CollectReferencedClasses(*It, Classes, Depth + 1);
        }
    }
    else if (const FDelegateProperty* Delegate = CastField<FDelegateProperty>(Property); Delegate && Delegate->SignatureFunction)
    {
        for (TFieldIterator<FProperty> It(Delegate->SignatureFunction, EFieldIteratorFlags::ExcludeSuper); It; ++It)
        {
            if (It->HasAnyPropertyFlags(CPF_Parm))
            {
                CollectReferencedClasses(*It, Classes, Depth + 1);
            }
        }
    }
}

void CollectClassDependencies(const UClass* Class, TSet<const UClass*>& Classes)
{
    if (Class == nullptr) return;
    // Preserve interface declaration inheritance while avoiding wholesale
    // discovery of engine base APIs for ordinary selected classes.
    if (Class->HasAnyClassFlags(CLASS_Interface))
    {
        if (const UClass* Super = Class->GetSuperClass();
            Super != nullptr && Super->HasAnyClassFlags(CLASS_Interface))
        {
            Classes.Add(Super);
        }
    }
    for (const FImplementedInterface& Interface : Class->Interfaces)
    {
        if (Interface.Class != nullptr)
        {
            Classes.Add(Interface.Class);
        }
    }
    for (TFieldIterator<FProperty> It(Class, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const FProperty* Property = *It;
        if ((!Property->HasAnyPropertyFlags(CPF_Parm) &&
             Property->HasAnyPropertyFlags(CPF_BlueprintVisible)) ||
            DelegateSignature(Property) != nullptr)
        {
            CollectReferencedClasses(Property, Classes);
        }
    }
    for (TFieldIterator<UFunction> It(Class, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const UFunction* Function = *It;
        if (!Function->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_BlueprintPure))
        {
            continue;
        }
        for (TFieldIterator<FProperty> ParameterIt(Function, EFieldIteratorFlags::ExcludeSuper);
             ParameterIt;
             ++ParameterIt)
        {
            if (ParameterIt->HasAnyPropertyFlags(CPF_Parm))
            {
                CollectReferencedClasses(*ParameterIt, Classes);
            }
        }
    }
}

TSharedRef<FJsonObject> ExportStruct(
    const UScriptStruct* Struct,
    FUnifiedScriptReflectionExportResult& Result)
{
    TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
    Json->SetStringField(TEXT("path"), Struct->GetPathName());
    Json->SetStringField(TEXT("name"), Struct->GetName());
    Json->SetStringField(
        TEXT("nativeName"),
        FString(Struct->GetPrefixCPP()) + Struct->GetName());
    Json->SetStringField(TEXT("module"), ModuleName(Struct));
    FString HeaderPath = Struct->GetMetaData(TEXT("ModuleRelativePath"));
    HeaderPath.ReplaceInline(TEXT("\\"), TEXT("/"));
    HeaderPath.RemoveFromStart(TEXT("Public/"));
    HeaderPath.RemoveFromStart(TEXT("Private/"));
    HeaderPath.RemoveFromStart(TEXT("Classes/"));
    if (!HeaderPath.IsEmpty())
    {
        Json->SetStringField(TEXT("headerPath"), HeaderPath);
    }
    Json->SetStringField(TEXT("kind"), TEXT("struct"));
    if (const UStruct* Super = Struct->GetSuperStruct())
    {
        Json->SetStringField(TEXT("baseTypePath"), Super->GetPathName());
    }

    TSharedRef<FJsonObject> Traits = MakeShared<FJsonObject>();
    Traits->SetBoolField(TEXT("abstract"), false);
    Traits->SetBoolField(TEXT("blueprintType"), Struct->GetBoolMetaData(TEXT("BlueprintType")));
    Traits->SetBoolField(TEXT("interface"), false);
    Json->SetObjectField(TEXT("traits"), Traits);
    Json->SetObjectField(TEXT("metadata"), BlueprintMetadata(Struct));
    Json->SetArrayField(TEXT("interfacePaths"), {});
    Json->SetArrayField(TEXT("enumValues"), {});

    TArray<const FProperty*> Properties;
    for (TFieldIterator<FProperty> It(Struct, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const FProperty* Property = *It;
        if (!Property->HasAnyPropertyFlags(
                CPF_Parm | CPF_NativeAccessSpecifierPrivate | CPF_NativeAccessSpecifierProtected))
        {
            Properties.Add(Property);
        }
    }
    Properties.Sort([](const FProperty& Left, const FProperty& Right)
    {
        return Left.GetName() < Right.GetName();
    });
    TArray<TSharedPtr<FJsonValue>> PropertyValues;
    for (const FProperty* Property : Properties)
    {
        PropertyValues.Add(MakeShared<FJsonValueObject>(ExportProperty(Struct, Property)));
        ++Result.PropertyCount;
    }
    Json->SetArrayField(TEXT("properties"), PropertyValues);
    Json->SetArrayField(TEXT("functions"), {});
    Json->SetArrayField(TEXT("delegates"), {});
    ++Result.TypeCount;
    return Json;
}

TSharedRef<FJsonObject> ExportEnum(const UEnum* Enum, FUnifiedScriptReflectionExportResult& Result)
{
    TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
    Json->SetStringField(TEXT("path"), Enum->GetPathName());
    Json->SetStringField(TEXT("name"), Enum->GetName());
    Json->SetStringField(TEXT("nativeName"), Enum->GetName());
    Json->SetStringField(TEXT("module"), ModuleName(Enum));
    const FString HeaderPath = ReflectedHeaderPath(Enum);
    if (!HeaderPath.IsEmpty()) Json->SetStringField(TEXT("headerPath"), HeaderPath);
    Json->SetStringField(TEXT("kind"), TEXT("enum"));
    TSharedRef<FJsonObject> Traits = MakeShared<FJsonObject>();
    Traits->SetBoolField(TEXT("abstract"), false);
    Traits->SetBoolField(TEXT("blueprintType"), Enum->GetBoolMetaData(TEXT("BlueprintType")));
    Traits->SetBoolField(TEXT("interface"), false);
    Json->SetObjectField(TEXT("traits"), Traits);
    Json->SetObjectField(TEXT("metadata"), BlueprintMetadata(Enum));
    Json->SetArrayField(TEXT("interfacePaths"), {});
    Json->SetArrayField(TEXT("properties"), {});
    Json->SetArrayField(TEXT("functions"), {});
    Json->SetArrayField(TEXT("delegates"), {});
    TArray<TSharedPtr<FJsonValue>> Values;
    for (int32 Index = 0; Index < Enum->NumEnums(); ++Index)
    {
        if (Enum->HasMetaData(TEXT("Hidden"), Index)) continue;
        TSharedRef<FJsonObject> Value = MakeShared<FJsonObject>();
        Value->SetStringField(TEXT("name"), Enum->GetNameStringByIndex(Index));
        Value->SetNumberField(TEXT("value"), static_cast<double>(Enum->GetValueByIndex(Index)));
        const FString DisplayName = Enum->GetDisplayNameTextByIndex(Index).ToString();
        if (!DisplayName.IsEmpty()) Value->SetStringField(TEXT("displayName"), DisplayName);
        Values.Add(MakeShared<FJsonValueObject>(Value));
    }
    Json->SetArrayField(TEXT("enumValues"), Values);
    ++Result.TypeCount;
    return Json;
}
}

bool FUnifiedScriptReflectionExporter::Export(
    const FUnifiedScriptReflectionExportOptions& Options,
    FUnifiedScriptReflectionExportResult& OutResult,
    FText& OutError)
{
    using namespace UE::UnifiedScript::Private;

    OutResult = FUnifiedScriptReflectionExportResult();
    if (Options.OutputPath.IsEmpty() || FPaths::IsRelative(Options.OutputPath))
    {
        OutError = FText::FromString(TEXT("The reflection output path must be absolute."));
        return false;
    }
    if (Options.Modules.IsEmpty())
    {
        OutError = FText::FromString(TEXT("At least one reflected module is required."));
        return false;
    }

    TArray<FString> Modules = Options.Modules;
    for (FString& Module : Modules)
    {
        Module.TrimStartAndEndInline();
    }
    Modules.Sort();
    Modules.SetNum(Algo::Unique(Modules));
    TSet<FString> ModuleSet;
    for (const FString& Module : Modules)
    {
        if (Module.IsEmpty())
        {
            OutError = FText::FromString(TEXT("Reflected module names cannot be empty."));
            return false;
        }
        ModuleSet.Add(Module);
    }

    TArray<FString> ClassPaths = Options.ClassPaths;
    TSet<FString> ClassPathSet;
    for (FString& ClassPath : ClassPaths)
    {
        ClassPath.TrimStartAndEndInline();
        FString ScriptModule;
        FString TypeName;
        const bool bHasModule = ClassPath.RemoveFromStart(TEXT("/Script/")) &&
            ClassPath.Split(TEXT("."), &ScriptModule, &TypeName) &&
            !ScriptModule.IsEmpty() && !TypeName.IsEmpty();
        if (!bHasModule || !ModuleSet.Contains(ScriptModule) ||
            ClassPathSet.Contains(TEXT("/Script/") + ScriptModule + TEXT(".") + TypeName))
        {
            OutError = FText::FromString(
                TEXT("Reflected class paths must be unique /Script/Module.Type values whose module is selected."));
            return false;
        }
        ClassPath = TEXT("/Script/") + ScriptModule + TEXT(".") + TypeName;
        ClassPathSet.Add(ClassPath);
    }
    ClassPaths.Sort();

    TArray<FString> TypeNames = Options.TypeNames;
    TSet<FString> TypeNameSet;
    for (FString& TypeName : TypeNames)
    {
        TypeName.TrimStartAndEndInline();
        bool bValidIdentifier = !TypeName.IsEmpty() &&
            (FChar::IsAlpha(TypeName[0]) || TypeName[0] == TEXT('_'));
        for (const TCHAR Character : TypeName)
        {
            if (!FChar::IsAlnum(Character) && Character != TEXT('_'))
            {
                bValidIdentifier = false;
                break;
            }
        }
        if (!bValidIdentifier || TypeNameSet.Contains(TypeName))
        {
            OutError = FText::FromString(
                TEXT("Reflected source type names must be unique C++/C# identifiers."));
            return false;
        }
        TypeNameSet.Add(TypeName);
    }
    TypeNames.Sort();

    TArray<FString> BlueprintAssetPaths = Options.BlueprintAssetPaths;
    TSet<FString> BlueprintAssetPathSet;
    for (FString& BlueprintAssetPath : BlueprintAssetPaths)
    {
        BlueprintAssetPath.TrimStartAndEndInline();
        if (!FPackageName::IsValidObjectPath(BlueprintAssetPath) ||
            BlueprintAssetPathSet.Contains(BlueprintAssetPath))
        {
            OutError = FText::FromString(
                TEXT("Reflected Blueprint assets must be unique exact object paths such as /Game/Blueprints/BP_Actor.BP_Actor."));
            return false;
        }
        BlueprintAssetPathSet.Add(BlueprintAssetPath);
    }
    BlueprintAssetPaths.Sort();

    TArray<const UClass*> Classes;
    TSet<const UClass*> ExplicitClassSet;
    for (const FString& BlueprintAssetPath : BlueprintAssetPaths)
    {
        UBlueprint* Blueprint = LoadObject<UBlueprint>(nullptr, *BlueprintAssetPath);
        if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
        {
            OutError = FText::FromString(FString::Printf(
                TEXT("Requested Blueprint asset '%s' could not load a generated class."),
                *BlueprintAssetPath));
            return false;
        }
        const UClass* GeneratedClass = Blueprint->GeneratedClass;
        if (GeneratedClass->HasMetaData(InternalBindingMetadata))
        {
            OutError = FText::FromString(FString::Printf(
                TEXT("Requested Blueprint asset '%s' is internal to UnifiedScript binding infrastructure."),
                *BlueprintAssetPath));
            return false;
        }
        Classes.AddUnique(GeneratedClass);
        ExplicitClassSet.Add(GeneratedClass);
        Modules.AddUnique(ModuleName(GeneratedClass));
    }
    for (TObjectIterator<UClass> It; It; ++It)
    {
        const UClass* Class = *It;
        if (Class->HasAnyFlags(RF_NewerVersionExists) || !Class->HasAnyClassFlags(CLASS_Native))
        {
            continue;
        }
        if (ModuleSet.Contains(ModuleName(Class)) &&
            (ClassPathSet.IsEmpty() || ClassPathSet.Contains(Class->GetPathName())))
        {
            if (Class->HasMetaData(InternalBindingMetadata))
            {
                ++OutResult.ExcludedTypeCount;
                continue;
            }
            Classes.Add(Class);
            if (ClassPathSet.Contains(Class->GetPathName()))
            {
                ExplicitClassSet.Add(Class);
            }
        }
    }
    if (!TypeNameSet.IsEmpty())
    {
        TMap<FString, TArray<const UClass*>> CandidatesByName;
        FARFilter BlueprintFilter;
        BlueprintFilter.ClassPaths.Add(UBlueprint::StaticClass()->GetClassPathName());
        BlueprintFilter.bRecursiveClasses = true;
        TArray<FAssetData> BlueprintAssets;
        IAssetRegistry& AssetRegistry =
            FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
        AssetRegistry.GetAssets(BlueprintFilter, BlueprintAssets);
        BlueprintAssets.Sort([](const FAssetData& Left, const FAssetData& Right)
        {
            return Left.GetObjectPathString() < Right.GetObjectPathString();
        });
        for (const FAssetData& Asset : BlueprintAssets)
        {
            const FString GeneratedTypeName = Asset.AssetName.ToString() + TEXT("_C");
            if (!TypeNameSet.Contains(GeneratedTypeName))
            {
                continue;
            }
            UBlueprint* Blueprint = Cast<UBlueprint>(Asset.GetAsset());
            if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
            {
                continue;
            }
            CandidatesByName.FindOrAdd(GeneratedTypeName).AddUnique(Blueprint->GeneratedClass);
        }

        for (TObjectIterator<UClass> It; It; ++It)
        {
            const UClass* Class = *It;
            const bool bNativeScriptClass = Class->HasAnyClassFlags(CLASS_Native) &&
                Class->GetPathName().StartsWith(TEXT("/Script/"));
            const bool bBlueprintGeneratedClass = !bNativeScriptClass &&
                Cast<UBlueprint>(Class->ClassGeneratedBy) != nullptr &&
                Class->GetPathName().StartsWith(TEXT("/"));
            if (Class->HasAnyFlags(RF_NewerVersionExists) ||
                (!bNativeScriptClass && !bBlueprintGeneratedClass) ||
                Class->HasMetaData(InternalBindingMetadata) ||
                !TypeNameSet.Contains(Class->GetName()))
            {
                continue;
            }
            CandidatesByName.FindOrAdd(Class->GetName()).AddUnique(Class);
        }
        for (const FString& TypeName : TypeNames)
        {
            TArray<const UClass*>* Candidates = CandidatesByName.Find(TypeName);
            if (Candidates == nullptr || Candidates->IsEmpty())
            {
                OutError = FText::FromString(FString::Printf(
                    TEXT("Source-requested reflected type '%s' was not found among native /Script or Blueprint generated classes."),
                    *TypeName));
                return false;
            }
            Candidates->Sort([](const UClass& Left, const UClass& Right)
            {
                return Left.GetPathName() < Right.GetPathName();
            });
            TArray<const UClass*> ExplicitCandidates = Candidates->FilterByPredicate(
                [&ExplicitClassSet](const UClass* Candidate)
                {
                    return ExplicitClassSet.Contains(Candidate);
                });
            const TArray<const UClass*>& ResolvedCandidates =
                ExplicitCandidates.IsEmpty() ? *Candidates : ExplicitCandidates;
            if (ResolvedCandidates.Num() != 1)
            {
                TArray<FString> CandidatePaths;
                for (const UClass* Candidate : ResolvedCandidates)
                {
                    CandidatePaths.Add(Candidate->GetPathName());
                }
                OutError = FText::FromString(FString::Printf(
                    TEXT("Source-requested reflected type '%s' is ambiguous: %s"),
                    *TypeName,
                    *FString::Join(CandidatePaths, TEXT(", "))));
                return false;
            }
            const UClass* Discovered = ResolvedCandidates[0];
            if (!Classes.Contains(Discovered))
            {
                Classes.Add(Discovered);
                Modules.AddUnique(ModuleName(Discovered));
                ++OutResult.DiscoveredClassCount;
            }
        }
        Modules.Sort();
    }
    for (const UClass* Class : Classes)
    {
        if (Cast<UBlueprint>(Class->ClassGeneratedBy) != nullptr &&
            !Class->GetPathName().StartsWith(TEXT("/Script/")))
        {
            ++OutResult.DiscoveredBlueprintClassCount;
        }
    }
    Classes.Sort([](const UClass& Left, const UClass& Right)
    {
        return Left.GetPathName() < Right.GetPathName();
    });
    if (!ClassPathSet.IsEmpty())
    {
        TSet<FString> Missing = ClassPathSet;
        for (const UClass* Class : Classes)
        {
            Missing.Remove(Class->GetPathName());
        }
        if (!Missing.IsEmpty())
        {
            TArray<FString> MissingPaths = Missing.Array();
            MissingPaths.Sort();
            OutError = FText::FromString(FString::Printf(
                TEXT("Requested reflected classes could not be loaded: %s"),
                *FString::Join(MissingPaths, TEXT(", "))));
            return false;
        }
    }

    // A class allowlist still needs the UInterface and base declarations that
    // appear in its reflected inheritance and signatures. Close this set before
    // exporting so contract projection can resolve those stable paths locally.
    TSet<const UClass*> KnownClasses;
    for (const UClass* Class : Classes)
    {
        KnownClasses.Add(Class);
    }
    for (int32 Index = 0; Index < Classes.Num(); ++Index)
    {
        TSet<const UClass*> Dependencies;
        CollectClassDependencies(Classes[Index], Dependencies);
        for (const UClass* Dependency : Dependencies)
        {
            if (Dependency == nullptr ||
                Dependency->HasMetaData(InternalBindingMetadata) ||
                ModuleName(Dependency).IsEmpty() ||
                KnownClasses.Contains(Dependency))
            {
                continue;
            }
            KnownClasses.Add(Dependency);
            Classes.Add(Dependency);
            Modules.AddUnique(ModuleName(Dependency));
        }
    }
    Classes.Sort([](const UClass& Left, const UClass& Right)
    {
        return Left.GetPathName() < Right.GetPathName();
    });
    Modules.Sort();

    TSet<const UScriptStruct*> ReferencedStructSet;
    TSet<const UEnum*> ReferencedEnumSet;
    for (const UClass* Class : Classes)
    {
        for (TFieldIterator<FProperty> It(Class, EFieldIteratorFlags::ExcludeSuper); It; ++It)
        {
            const FProperty* Property = *It;
            if (DelegateSignature(Property) != nullptr)
            {
                if (Property->HasAnyPropertyFlags(CPF_BlueprintAssignable | CPF_BlueprintCallable))
                {
                    CollectReferencedStructs(Property, ReferencedStructSet);
                    CollectReferencedEnums(Property, ReferencedEnumSet);
                }
            }
            else if (!Property->HasAnyPropertyFlags(CPF_Parm) &&
                     Property->HasAnyPropertyFlags(CPF_BlueprintVisible))
            {
                CollectReferencedStructs(Property, ReferencedStructSet);
                CollectReferencedEnums(Property, ReferencedEnumSet);
            }
        }
        for (TFieldIterator<UFunction> It(Class, EFieldIteratorFlags::ExcludeSuper); It; ++It)
        {
            if (!It->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_BlueprintPure))
            {
                continue;
            }
            for (TFieldIterator<FProperty> ParameterIt(*It, EFieldIteratorFlags::ExcludeSuper);
                 ParameterIt;
                 ++ParameterIt)
            {
                if (ParameterIt->HasAnyPropertyFlags(CPF_Parm))
                {
                    CollectReferencedStructs(*ParameterIt, ReferencedStructSet);
                    CollectReferencedEnums(*ParameterIt, ReferencedEnumSet);
                }
            }
        }
    }
    int32 PreviousStructCount = INDEX_NONE;
    while (PreviousStructCount != ReferencedStructSet.Num())
    {
        PreviousStructCount = ReferencedStructSet.Num();
        const TArray<const UScriptStruct*> Snapshot = ReferencedStructSet.Array();
        for (const UScriptStruct* Struct : Snapshot)
        {
            for (TFieldIterator<FProperty> It(Struct, EFieldIteratorFlags::ExcludeSuper); It; ++It)
            {
                if (!It->HasAnyPropertyFlags(
                        CPF_NativeAccessSpecifierPrivate | CPF_NativeAccessSpecifierProtected))
                {
                    CollectReferencedStructs(*It, ReferencedStructSet);
                    CollectReferencedEnums(*It, ReferencedEnumSet);
                }
            }
        }
    }
    TArray<const UScriptStruct*> ReferencedStructs = ReferencedStructSet.Array();
    ReferencedStructs.Sort([](const UScriptStruct& Left, const UScriptStruct& Right)
    {
        return Left.GetPathName() < Right.GetPathName();
    });
    for (const UScriptStruct* Struct : ReferencedStructs)
    {
        Modules.AddUnique(ModuleName(Struct));
    }
    TArray<const UEnum*> ReferencedEnums = ReferencedEnumSet.Array();
    ReferencedEnums.Sort([](const UEnum& Left, const UEnum& Right)
    {
        return Left.GetPathName() < Right.GetPathName();
    });
    for (const UEnum* Enum : ReferencedEnums)
    {
        Modules.AddUnique(ModuleName(Enum));
    }
    Modules.Sort();

    TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schemaVersion"), ReflectionSchema);
    const FEngineVersion& Version = FEngineVersion::Current();
    TSharedRef<FJsonObject> Engine = MakeShared<FJsonObject>();
    Engine->SetNumberField(TEXT("major"), Version.GetMajor());
    Engine->SetNumberField(TEXT("minor"), Version.GetMinor());
    Engine->SetNumberField(TEXT("patch"), Version.GetPatch());
    Engine->SetStringField(TEXT("branch"), Version.GetBranch());
    Root->SetObjectField(TEXT("engine"), Engine);

    TArray<TSharedPtr<FJsonValue>> ModuleValues;
    for (const FString& Module : Modules)
    {
        ModuleValues.Add(MakeShared<FJsonValueString>(Module));
    }
    Root->SetArrayField(TEXT("modules"), ModuleValues);

    TArray<TSharedPtr<FJsonValue>> TypeValues;
    for (const UClass* Class : Classes)
    {
        TypeValues.Add(MakeShared<FJsonValueObject>(ExportClass(Class, OutResult)));
    }
    for (const UScriptStruct* Struct : ReferencedStructs)
    {
        TypeValues.Add(MakeShared<FJsonValueObject>(ExportStruct(Struct, OutResult)));
    }
    for (const UEnum* Enum : ReferencedEnums)
    {
        TypeValues.Add(MakeShared<FJsonValueObject>(ExportEnum(Enum, OutResult)));
    }
    Root->SetArrayField(TEXT("types"), TypeValues);

    FString Json;
    const TSharedRef<TJsonWriter<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>::Create(&Json);
    if (!FJsonSerializer::Serialize(Root, Writer))
    {
        OutError = FText::FromString(TEXT("Unreal failed to serialize the reflection manifest."));
        return false;
    }

    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Options.OutputPath), true);
    if (!FFileHelper::SaveStringToFile(
            Json,
            *Options.OutputPath,
            FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
    {
        OutError = FText::Format(
            NSLOCTEXT("UnifiedScript", "ReflectionWriteFailed", "Could not write '{0}'."),
            FText::FromString(Options.OutputPath));
        return false;
    }
    return true;
}

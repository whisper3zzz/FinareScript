#include "UnifiedScriptReflectionExporter.h"

#include "Algo/Unique.h"
#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Misc/EngineVersion.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UObject/Class.h"
#include "UObject/Package.h"
#include "UObject/UObjectIterator.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::Private
{
constexpr const TCHAR* ReflectionSchema = TEXT("unifiedscript.unreal-reflection/v0.2");
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
    return Property->GetCPPType();
}

TSharedRef<FJsonObject> PropertyTypeReference(const FProperty* Property)
{
    const FString NativeName = CanonicalNativeName(Property);
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
        Json->SetObjectField(TEXT("elementType"), PropertyTypeReference(Array->Inner));
        return Json;
    }
    if (const FSetProperty* Set = CastField<FSetProperty>(Property))
    {
        TSharedRef<FJsonObject> Json = TypeReference(TEXT("set"), NativeName);
        Json->SetObjectField(TEXT("elementType"), PropertyTypeReference(Set->ElementProp));
        return Json;
    }
    if (const FMapProperty* Map = CastField<FMapProperty>(Property))
    {
        TSharedRef<FJsonObject> Json = TypeReference(TEXT("map"), NativeName);
        Json->SetObjectField(TEXT("keyType"), PropertyTypeReference(Map->KeyProp));
        Json->SetObjectField(TEXT("valueType"), PropertyTypeReference(Map->ValueProp));
        return Json;
    }
    if (CastField<FSoftClassProperty>(Property) || CastField<FSoftObjectProperty>(Property) ||
        CastField<FWeakObjectProperty>(Property) || CastField<FLazyObjectProperty>(Property) ||
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
    return Name.RemoveFromStart(TEXT("/Script/")) ? Name : FString();
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
        Parameters.Add(MakeShared<FJsonValueObject>(ParameterJson));
    }
    Json->SetArrayField(TEXT("parameters"), Parameters);

    TSharedRef<FJsonObject> Traits = MakeShared<FJsonObject>();
    Traits->SetBoolField(TEXT("static"), Function->HasAnyFunctionFlags(FUNC_Static));
    Traits->SetBoolField(TEXT("pure"), Function->HasAnyFunctionFlags(FUNC_BlueprintPure));
    Traits->SetBoolField(
        TEXT("callable"),
        Function->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_BlueprintPure));
    Traits->SetStringField(TEXT("nativeAccess"), FunctionNativeAccess(Function));
    Json->SetObjectField(TEXT("traits"), Traits);
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
    Traits->SetBoolField(TEXT("blueprintType"), Class->GetBoolMetaData(TEXT("BlueprintType")));
    Json->SetObjectField(TEXT("traits"), Traits);

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
    TSet<const UScriptStruct*>& Structs)
{
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
        CollectReferencedStructs(Array->Inner, Structs);
    }
    else if (const FSetProperty* Set = CastField<FSetProperty>(Property))
    {
        CollectReferencedStructs(Set->ElementProp, Structs);
    }
    else if (const FMapProperty* Map = CastField<FMapProperty>(Property))
    {
        CollectReferencedStructs(Map->KeyProp, Structs);
        CollectReferencedStructs(Map->ValueProp, Structs);
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
    Json->SetObjectField(TEXT("traits"), Traits);

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

    TArray<const UClass*> Classes;
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
        }
    }
    Classes.Sort([](const UClass& Left, const UClass& Right)
    {
        return Left.GetPathName() < Right.GetPathName();
    });
    if (!ClassPathSet.IsEmpty() && Classes.Num() != ClassPathSet.Num())
    {
        TSet<FString> Missing = ClassPathSet;
        for (const UClass* Class : Classes)
        {
            Missing.Remove(Class->GetPathName());
        }
        TArray<FString> MissingPaths = Missing.Array();
        MissingPaths.Sort();
        OutError = FText::FromString(FString::Printf(
            TEXT("Requested reflected classes could not be loaded: %s"),
            *FString::Join(MissingPaths, TEXT(", "))));
        return false;
    }

    TSet<const UScriptStruct*> ReferencedStructSet;
    for (const UClass* Class : Classes)
    {
        for (TFieldIterator<FProperty> It(Class, EFieldIteratorFlags::ExcludeSuper); It; ++It)
        {
            if (!It->HasAnyPropertyFlags(CPF_Parm) &&
                It->HasAnyPropertyFlags(CPF_BlueprintVisible))
            {
                CollectReferencedStructs(*It, ReferencedStructSet);
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

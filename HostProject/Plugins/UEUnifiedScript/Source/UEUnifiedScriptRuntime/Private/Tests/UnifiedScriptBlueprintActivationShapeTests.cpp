#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptBlueprintPrivate.h"

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "GameFramework/Actor.h"
#include "Misc/AutomationTest.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UnifiedScriptInteropFixture.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::BlueprintActivationShapeTests::Private
{

void AddPath(
    FUnifiedScriptManagedValueTypeBinding& Binding,
    const TCHAR* First,
    const TCHAR* Second = nullptr)
{
    FUnifiedScriptManagedStructFieldPath& Field =
        Binding.StructFields.AddDefaulted_GetRef();
    Field.Segments.Add(FName(First));
    if (Second != nullptr)
    {
        Field.Segments.Add(FName(Second));
    }
}

FUnifiedScriptManagedValueTypeBinding EmptyBinding()
{
    return FUnifiedScriptManagedValueTypeBinding();
}

bool ReadShape(
    FAutomationTestBase& Test,
    const FString& Json,
    TSharedPtr<FJsonObject>& OutShape)
{
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!Test.TestTrue(TEXT("Activation shape JSON parses"),
            FJsonSerializer::Deserialize(Reader, OutShape) && OutShape.IsValid()))
    {
        return false;
    }
    return true;
}

bool CheckKind(
    FAutomationTestBase& Test,
    const TCHAR* Label,
    const FProperty* Property,
    const FUnifiedScriptManagedValueTypeBinding& Binding,
    const TCHAR* ExpectedKind,
    const TCHAR* ExpectedNativeName)
{
    FString Json;
    FString Error;
    const bool bBuilt = Property != nullptr &&
        UE::UnifiedScript::Blueprint::Private::BuildActivationShapeJson(
            *Property,
            Binding,
            Json,
            Error);
    Test.TestTrue(*FString::Printf(TEXT("%s builds: %s"), Label, *Error), bBuilt);
    if (!bBuilt)
    {
        return false;
    }
    TSharedPtr<FJsonObject> Shape;
    if (!ReadShape(Test, Json, Shape))
    {
        return false;
    }
    FString Kind;
    FString NativeName;
    Test.TestTrue(*FString::Printf(TEXT("%s has a kind"), Label),
        Shape->TryGetStringField(TEXT("kind"), Kind));
    Test.TestTrue(*FString::Printf(TEXT("%s has a native name"), Label),
        Shape->TryGetStringField(TEXT("nativeName"), NativeName));
    Test.TestEqual(*FString::Printf(TEXT("%s kind"), Label), Kind, FString(ExpectedKind));
    Test.TestEqual(
        *FString::Printf(TEXT("%s native name"), Label),
        NativeName,
        FString(ExpectedNativeName));
    return true;
}

} // namespace UE::UnifiedScript::BlueprintActivationShapeTests::Private

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptBlueprintActivationShapeTest,
    "UnifiedScript.Unreal.ScriptClass.BlueprintOverrideActivationShapes",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptBlueprintActivationShapeTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::BlueprintActivationShapeTests::Private;

    UClass* FixtureClass = UUnifiedScriptInteropFixture::StaticClass();
    const FProperty* Mode = FindFProperty<FProperty>(FixtureClass, TEXT("Mode"));
    const FProperty* Vector = FindFProperty<FProperty>(FixtureClass, TEXT("VectorValue"));
    const FProperty* SoftObject =
        FindFProperty<FProperty>(FixtureClass, TEXT("SoftObjectValue"));
    const FProperty* SoftClass =
        FindFProperty<FProperty>(FixtureClass, TEXT("SoftClassValue"));
    const FProperty* Array = FindFProperty<FProperty>(FixtureClass, TEXT("IntArrayValue"));
    const FProperty* Set = FindFProperty<FProperty>(FixtureClass, TEXT("IntSetValue"));
    const FProperty* Map = FindFProperty<FProperty>(FixtureClass, TEXT("IntMapValue"));
    if (!TestNotNull(TEXT("Interop enum property exists"), Mode) ||
        !TestNotNull(TEXT("Interop fixed FVector property exists"), Vector) ||
        !TestNotNull(TEXT("Interop soft object property exists"), SoftObject) ||
        !TestNotNull(TEXT("Interop soft class property exists"), SoftClass) ||
        !TestNotNull(TEXT("Interop array property exists"), Array) ||
        !TestNotNull(TEXT("Interop set property exists"), Set) ||
        !TestNotNull(TEXT("Interop map property exists"), Map))
    {
        return false;
    }

    FUnifiedScriptManagedValueTypeBinding EnumBinding = EmptyBinding();
    const UEnum* Enum = nullptr;
    if (const FEnumProperty* EnumProperty = CastField<FEnumProperty>(Mode))
    {
        Enum = EnumProperty->GetEnum();
    }
    else if (const FByteProperty* ByteProperty = CastField<FByteProperty>(Mode))
    {
        Enum = ByteProperty->Enum;
    }
    TestNotNull(TEXT("Interop enum reflection is typed"), Enum);
    if (Enum != nullptr)
    {
        EnumBinding.UnrealTypePath = Enum->GetPathName();
        CheckKind(
            *this,
            TEXT("Enum"),
            Mode,
            EnumBinding,
            TEXT("enum"),
            *Enum->GetName());
    }

    CheckKind(*this, TEXT("Fixed FVector"), Vector, EmptyBinding(), TEXT("value"), TEXT("FVector"));
    CheckKind(
        *this,
        TEXT("Soft object"),
        SoftObject,
        EmptyBinding(),
        TEXT("value"),
        TEXT("FSoftObjectPath"));
    CheckKind(
        *this,
        TEXT("Soft class"),
        SoftClass,
        EmptyBinding(),
        TEXT("value"),
        TEXT("FSoftClassPath"));

    FUnifiedScriptManagedValueTypeBinding ArrayBinding = EmptyBinding();
    AddPath(ArrayBinding, TEXT("$element"));
    CheckKind(
        *this,
        TEXT("Array"),
        Array,
        ArrayBinding,
        TEXT("array"),
        TEXT("TArray<int32>"));
    FUnifiedScriptManagedValueTypeBinding SetBinding = EmptyBinding();
    AddPath(SetBinding, TEXT("$element"));
    CheckKind(
        *this,
        TEXT("Set"),
        Set,
        SetBinding,
        TEXT("set"),
        TEXT("TSet<int32>"));
    FUnifiedScriptManagedValueTypeBinding MapBinding = EmptyBinding();
    AddPath(MapBinding, TEXT("$key"));
    AddPath(MapBinding, TEXT("$value"));
    CheckKind(
        *this,
        TEXT("Map"),
        Map,
        MapBinding,
        TEXT("map"),
        TEXT("TMap<int32,int32>"));

    FUnifiedScriptManagedValueTypeBinding MissingElementBinding = EmptyBinding();
    FString MissingElementJson;
    FString MissingElementError;
    TestFalse(
        TEXT("Array shape rejects a missing element marker"),
        UE::UnifiedScript::Blueprint::Private::BuildActivationShapeJson(
            *Array,
            MissingElementBinding,
            MissingElementJson,
            MissingElementError));
    FUnifiedScriptManagedValueTypeBinding ExtraElementBinding = ArrayBinding;
    AddPath(ExtraElementBinding, TEXT("$element"), TEXT("unused"));
    FString ExtraElementJson;
    FString ExtraElementError;
    TestFalse(
        TEXT("Array shape rejects an unconsumed nested marker"),
        UE::UnifiedScript::Blueprint::Private::BuildActivationShapeJson(
            *Array,
            ExtraElementBinding,
            ExtraElementJson,
            ExtraElementError));
    FUnifiedScriptManagedValueTypeBinding DuplicateElementBinding = ArrayBinding;
    AddPath(DuplicateElementBinding, TEXT("$element"));
    FString DuplicateElementJson;
    FString DuplicateElementError;
    TestFalse(
        TEXT("Array shape rejects a duplicate element marker"),
        UE::UnifiedScript::Blueprint::Private::BuildActivationShapeJson(
            *Array,
            DuplicateElementBinding,
            DuplicateElementJson,
            DuplicateElementError));

    FProperty* MutableArray = const_cast<FProperty*>(Array);
    const int32 OriginalArrayDim = MutableArray->ArrayDim;
    MutableArray->ArrayDim = 2;
    FString FixedArrayJson;
    FString FixedArrayError;
    TestFalse(
        TEXT("Activation shape rejects fixed native arrays"),
        UE::UnifiedScript::Blueprint::Private::BuildActivationShapeJson(
            *MutableArray,
            ArrayBinding,
            FixedArrayJson,
            FixedArrayError));
    MutableArray->ArrayDim = OriginalArrayDim;
    TestTrue(TEXT("Fixed native array reports a useful error"),
        FixedArrayError.Contains(TEXT("fixed native array")));

    UFunction* SetActorLocation = AActor::StaticClass()->FindFunctionByName(
        TEXT("K2_SetActorLocation"));
    const FProperty* SweepHitResult = SetActorLocation != nullptr
        ? FindFProperty<FProperty>(SetActorLocation, TEXT("SweepHitResult"))
        : nullptr;
    const FStructProperty* HitResultProperty = CastField<FStructProperty>(SweepHitResult);
    TestNotNull(TEXT("K2_SetActorLocation exposes a reflected FHitResult"), HitResultProperty);
    if (HitResultProperty != nullptr)
    {
        FUnifiedScriptManagedValueTypeBinding StructBinding = EmptyBinding();
        StructBinding.UnrealTypePath = HitResultProperty->Struct->GetPathName();
        // Keep two real fields in deliberately reverse native field order.
        AddPath(StructBinding, TEXT("Distance"));
        AddPath(StructBinding, TEXT("Time"));
        FString StructJson;
        FString StructError;
        TestTrue(
            *FString::Printf(TEXT("FHitResult shape builds: %s"), *StructError),
            UE::UnifiedScript::Blueprint::Private::BuildActivationShapeJson(
                *HitResultProperty,
                StructBinding,
                StructJson,
                StructError));
        TSharedPtr<FJsonObject> StructShape;
        if (ReadShape(*this, StructJson, StructShape))
        {
            const TArray<TSharedPtr<FJsonValue>>* Fields = nullptr;
            TestTrue(TEXT("FHitResult shape has ordered fields"),
                StructShape->TryGetArrayField(TEXT("fields"), Fields) &&
                Fields != nullptr && Fields->Num() == 2);
            if (Fields != nullptr && Fields->Num() == 2)
            {
                FString FirstName;
                FString SecondName;
                (*Fields)[0]->AsObject()->TryGetStringField(TEXT("nativeName"), FirstName);
                (*Fields)[1]->AsObject()->TryGetStringField(TEXT("nativeName"), SecondName);
                TestEqual(TEXT("FHitResult field order follows persisted schema"),
                    FirstName, FString(TEXT("Distance")));
                TestEqual(TEXT("FHitResult second field follows persisted schema"),
                    SecondName, FString(TEXT("Time")));
            }
        }

        FUnifiedScriptManagedValueTypeBinding InvalidFieldBinding = StructBinding;
        AddPath(InvalidFieldBinding, TEXT("MissingActivationField"));
        FString InvalidJson;
        FString InvalidError;
        TestFalse(
            TEXT("FHitResult shape rejects an unreflected field"),
            UE::UnifiedScript::Blueprint::Private::BuildActivationShapeJson(
                *HitResultProperty,
                InvalidFieldBinding,
                InvalidJson,
                InvalidError));
        TestTrue(TEXT("Invalid struct field reports the field path"),
            InvalidError.Contains(TEXT("MissingActivationField")));
    }

    FUnifiedScriptManagedValueTypeBinding ExtraPathBinding = EmptyBinding();
    if (Enum != nullptr)
    {
        ExtraPathBinding.UnrealTypePath = Enum->GetPathName();
    }
    AddPath(ExtraPathBinding, TEXT("unused"));
    FString ExtraPathJson;
    FString ExtraPathError;
    const FProperty* ExtraPathProperty = Enum != nullptr ? Mode : Vector;
    TestFalse(
        TEXT("Scalar shape rejects an unused persisted path"),
        UE::UnifiedScript::Blueprint::Private::BuildActivationShapeJson(
            *ExtraPathProperty,
            ExtraPathBinding,
            ExtraPathJson,
            ExtraPathError));
    TestTrue(TEXT("Unused scalar path reports a contract error"), !ExtraPathError.IsEmpty());
    return true;
}

#endif

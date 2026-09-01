#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptBlueprintClassMaterializer.h"

#include "EdGraphSchema_K2.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptClassBases.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/Package.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::BlueprintClass::Tests
{
FString SemanticId(const TCHAR* Digits)
{
    return FString::Printf(TEXT("us_%s"), Digits);
}

FGuid Guid(const TCHAR* Digits)
{
    FGuid Result;
    ensureAlways(FGuid::ParseExact(Digits, EGuidFormats::Digits, Result));
    return Result;
}

FString State(
    const TCHAR* Id,
    const TCHAR* Name,
    const TCHAR* DisplayType,
    const TCHAR* MetadataType,
    const TCHAR* TypeKind,
    const TCHAR* DefaultJson,
    bool bInstanceEditable = true,
    bool bSaveGame = false,
    bool bReplicated = false,
    const TCHAR* Category = TEXT("State"))
{
    return FString::Printf(
        TEXT(R"JSON(
      {
        "id": "us_%s",
        "name": "%s",
        "typeDisplayName": "%s",
        "typeMetadataName": "%s",
        "typeKind": "%s",
        "defaultValue": %s,
        "span": {
          "filePath": "Managed/StateActor.cs",
          "start": { "line": 1, "column": 1 },
          "end": { "line": 1, "column": 2 }
        },
        "exposure": {
          "instanceEditable": %s,
          "saveGame": %s,
          "replicated": %s,
          "category": "%s"
        }
      })JSON"),
        Id,
        Name,
        DisplayType,
        MetadataType,
        TypeKind,
        DefaultJson,
        bInstanceEditable ? TEXT("true") : TEXT("false"),
        bSaveGame ? TEXT("true") : TEXT("false"),
        bReplicated ? TEXT("true") : TEXT("false"),
        Category);
}

FString Parameter(
    const TCHAR* Id,
    const TCHAR* Name,
    const TCHAR* MetadataType)
{
    return FString::Printf(
        TEXT(R"JSON({
        "id": "us_%s",
        "name": "%s",
        "typeMetadataName": "%s"
      })JSON"),
        Id,
        Name,
        MetadataType);
}

FString Function(
    const TCHAR* Id,
    const TCHAR* Name,
    const TCHAR* ReturnType,
    const FString& Parameters,
    bool bIsStatic = false,
    bool bIsPublic = true,
    bool bIsEvent = false)
{
    return FString::Printf(
        TEXT(R"JSON({
      "id": "us_%s",
      "name": "%s",
      "qualifiedName": "Tests.StateActor.%s()",
      "returnTypeMetadataName": "%s",
      "isStatic": %s,
      "isPublic": %s,
      "isEvent": %s,
      "parameters": [%s]
    })JSON"),
        Id,
        Name,
        Name,
        ReturnType,
        bIsStatic ? TEXT("true") : TEXT("false"),
        bIsPublic ? TEXT("true") : TEXT("false"),
        bIsEvent ? TEXT("true") : TEXT("false"),
        *Parameters);
}

FString InitialFunctions()
{
    const FString MixParameters = FString::Join(
        TArray<FString>{
            Parameter(TEXT("a1111111111111111111111111111111"), TEXT("value"), TEXT("System.Int32")),
            Parameter(TEXT("a2222222222222222222222222222222"), TEXT("doubleValue"), TEXT("System.Boolean")),
            Parameter(TEXT("a3333333333333333333333333333333"), TEXT("increment"), TEXT("System.Byte")),
            Parameter(TEXT("a4444444444444444444444444444444"), TEXT("bias"), TEXT("System.Int64")),
        },
        TEXT(","));
    return FString::Join(
        TArray<FString>{
            Function(
                TEXT("88888888888888888888888888888888"),
                TEXT("Mix"),
                TEXT("System.Int64"),
                MixParameters),
            Function(
                TEXT("99999999999999999999999999999999"),
                TEXT("ResetValue"),
                TEXT("System.Void"),
                FString()),
            Function(
                TEXT("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
                TEXT("InternalEvent"),
                TEXT("System.Void"),
                FString(),
                false,
                false,
                true),
        },
        TEXT(","));
}

FString ReloadedFunctions()
{
    const FString MixParameters = FString::Join(
        TArray<FString>{
            Parameter(TEXT("a1111111111111111111111111111111"), TEXT("value"), TEXT("System.Int32")),
            Parameter(TEXT("a2222222222222222222222222222222"), TEXT("doubleValue"), TEXT("System.Boolean")),
            Parameter(TEXT("a3333333333333333333333333333333"), TEXT("increment"), TEXT("System.Byte")),
            Parameter(TEXT("a4444444444444444444444444444444"), TEXT("bias"), TEXT("System.Int64")),
        },
        TEXT(","));
    return Function(
        TEXT("88888888888888888888888888888888"),
        TEXT("Combine"),
        TEXT("System.Int64"),
        MixParameters);
}

FString Document(
    const FString& States,
    const FString& Functions = FString(),
    const TCHAR* AssetId = TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
    const TCHAR* QualifiedName = TEXT("Tests.StateActor"),
    const TCHAR* AssetPath = TEXT("Tests/StateActor"))
{
    return FString::Printf(
        TEXT(R"JSON(
{
  "schemaVersion": "unifiedscript.script-asset/v0.1",
  "sourceCompilationId": "us_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "semanticConfigurationId": "us_cccccccccccccccccccccccccccccccc",
  "assets": [{
    "id": "us_%s",
    "qualifiedName": "%s",
    "assetPath": "%s",
    "span": {
      "filePath": "Managed/StateActor.cs",
      "start": { "line": 1, "column": 1 },
      "end": { "line": 50, "column": 2 }
    },
    "states": [%s
    ],
    "functions": [%s]
  }]
}
)JSON"),
        AssetId,
        QualifiedName,
        AssetPath,
        *States,
        *Functions);
}

FString InitialStates()
{
    TArray<FString> States = {
        State(
            TEXT("11111111111111111111111111111111"),
            TEXT("Enabled"),
            TEXT("bool"),
            TEXT("System.Boolean"),
            TEXT("boolean"),
            TEXT("true")),
        State(
            TEXT("22222222222222222222222222222222"),
            TEXT("ByteValue"),
            TEXT("byte"),
            TEXT("System.Byte"),
            TEXT("integer"),
            TEXT("7")),
        State(
            TEXT("33333333333333333333333333333333"),
            TEXT("Count"),
            TEXT("int"),
            TEXT("System.Int32"),
            TEXT("integer"),
            TEXT("3"),
            true,
            true,
            true,
            TEXT("Stats")),
        State(
            TEXT("44444444444444444444444444444444"),
            TEXT("Large"),
            TEXT("long"),
            TEXT("System.Int64"),
            TEXT("integer"),
            TEXT("9001")),
        State(
            TEXT("55555555555555555555555555555555"),
            TEXT("Ratio"),
            TEXT("float"),
            TEXT("System.Single"),
            TEXT("floatingPoint"),
            TEXT("1.25")),
        State(
            TEXT("66666666666666666666666666666666"),
            TEXT("Precision"),
            TEXT("double"),
            TEXT("System.Double"),
            TEXT("floatingPoint"),
            TEXT("2.5")),
        State(
            TEXT("77777777777777777777777777777777"),
            TEXT("Label"),
            TEXT("string"),
            TEXT("System.String"),
            TEXT("string"),
            TEXT("\"Ready\""),
            false),
    };
    return FString::Join(States, TEXT(","));
}

FString ReloadedStates()
{
    TArray<FString> States = {
        State(
            TEXT("22222222222222222222222222222222"),
            TEXT("ByteValue"),
            TEXT("byte"),
            TEXT("System.Byte"),
            TEXT("integer"),
            TEXT("7")),
        State(
            TEXT("33333333333333333333333333333333"),
            TEXT("Score"),
            TEXT("int"),
            TEXT("System.Int32"),
            TEXT("integer"),
            TEXT("9"),
            true,
            true,
            true,
            TEXT("Stats")),
        State(
            TEXT("44444444444444444444444444444444"),
            TEXT("Large"),
            TEXT("double"),
            TEXT("System.Double"),
            TEXT("floatingPoint"),
            TEXT("42.5")),
        State(
            TEXT("55555555555555555555555555555555"),
            TEXT("Ratio"),
            TEXT("float"),
            TEXT("System.Single"),
            TEXT("floatingPoint"),
            TEXT("1.25")),
        State(
            TEXT("66666666666666666666666666666666"),
            TEXT("Precision"),
            TEXT("double"),
            TEXT("System.Double"),
            TEXT("floatingPoint"),
            TEXT("2.5")),
        State(
            TEXT("77777777777777777777777777777777"),
            TEXT("Label"),
            TEXT("string"),
            TEXT("System.String"),
            TEXT("string"),
            TEXT("\"Ready\""),
            false),
    };
    return FString::Join(States, TEXT(","));
}
} // namespace UE::UnifiedScript::BlueprintClass::Tests

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptBlueprintClassMaterializerTest,
    "UnifiedScript.Unreal.ScriptClass.BlueprintStateMaterialization",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptBlueprintClassMaterializerTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::BlueprintClass::Tests;

    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString AssetName = TEXT("BP_UnifiedScriptState_") + Suffix;
    UPackage* Package = CreatePackage(*(TEXT("/Engine/Transient/") + AssetName));
    UUnifiedScriptBlueprint* Blueprint = Cast<UUnifiedScriptBlueprint>(
        FKismetEditorUtilities::CreateBlueprint(
            AActor::StaticClass(),
            Package,
            FName(*AssetName),
            BPTYPE_Normal,
            UUnifiedScriptBlueprint::StaticClass(),
            UUnifiedScriptBlueprintGeneratedClass::StaticClass(),
            FName(TEXT("UnifiedScriptBlueprintStateMaterialization"))));
    if (!TestNotNull(TEXT("Creates a transient UnifiedScript Blueprint"), Blueprint) ||
        !TestNotNull(TEXT("Blueprint compiles an initial generated class"), Blueprint->GeneratedClass.Get()))
    {
        return false;
    }

    UUnifiedScriptAsset* ScriptAsset = NewObject<UUnifiedScriptAsset>(GetTransientPackage());
    ScriptAsset->ScriptClassId = Guid(TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    ScriptAsset->ScriptTypeName = TEXT("Tests.StateActor");
    ScriptAsset->DeclarationAssetPath = TEXT("Tests/StateActor");
    Blueprint->ScriptAsset = ScriptAsset;

    FUnifiedScriptBlueprintClassSyncResult Result;
    FString Error;
    const bool bInitial = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *Blueprint,
        Document(InitialStates(), InitialFunctions()),
        false,
        Result,
        Error);
    TestTrue(*FString::Printf(TEXT("Materializes reviewed state: %s"), *Error), bInitial);
    if (!bInitial)
    {
        return false;
    }
    TestTrue(TEXT("Initial materialization changes the proxy"), Result.bChanged);
    TestEqual(TEXT("Seven reviewed types become properties"), Result.AddedPropertyCount, 7);
    TestEqual(TEXT("Seven states are declared"), Result.DeclaredStateCount, 7);
    TestEqual(TEXT("Two public non-event functions are declared"), Result.DeclaredFunctionCount, 2);
    TestEqual(TEXT("Two callable functions become graphs"), Result.AddedFunctionCount, 2);
    TestEqual(TEXT("Blueprint remains compiled"), Blueprint->Status, BS_UpToDate);

    UClass* GeneratedClass = Blueprint->GeneratedClass;
    UUnifiedScriptBlueprintGeneratedClass* ManagedGeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(GeneratedClass);
    TestNotNull(TEXT("Proxy uses the managed-function generated class"), ManagedGeneratedClass);
    const FName MixInternalName(TEXT("USF_88888888888888888888888888888888"));
    const UFunction* MixFunction = GeneratedClass->FindFunctionByName(
        MixInternalName,
        EIncludeSuperFlag::ExcludeSuper);
    TestNotNull(TEXT("Managed function becomes reflected UFunction"), MixFunction);
    if (MixFunction != nullptr)
    {
        TestTrue(TEXT("Managed UFunction is Blueprint callable"),
            MixFunction->HasAnyFunctionFlags(FUNC_BlueprintCallable));
        TestTrue(TEXT("Managed UFunction avoids unresolved native registration"),
            !MixFunction->HasAnyFunctionFlags(FUNC_Native));
        TestTrue(TEXT("Managed UFunction is bound to generic direct dispatch"),
            ManagedGeneratedClass != nullptr &&
                ManagedGeneratedClass->IsManagedFunctionBound(MixInternalName));
        const FInt64Property* ReturnProperty =
            CastField<FInt64Property>(MixFunction->GetReturnProperty());
        TestNotNull(TEXT("Managed long return becomes int64"), ReturnProperty);
        TArray<const FProperty*> ReflectedParameters;
        for (TFieldIterator<FProperty> It(MixFunction, EFieldIteratorFlags::ExcludeSuper); It; ++It)
        {
            if (It->HasAnyPropertyFlags(CPF_Parm) &&
                !It->HasAnyPropertyFlags(CPF_ReturnParm))
            {
                ReflectedParameters.Add(*It);
            }
        }
        TestEqual(TEXT("Four managed arguments become typed pins"), ReflectedParameters.Num(), 4);
        if (ReflectedParameters.Num() == 4)
        {
            TestNotNull(TEXT("Argument 0 is int32"), CastField<FIntProperty>(ReflectedParameters[0]));
            TestNotNull(TEXT("Argument 1 is bool"), CastField<FBoolProperty>(ReflectedParameters[1]));
            TestNotNull(TEXT("Argument 2 is byte"), CastField<FByteProperty>(ReflectedParameters[2]));
            TestNotNull(TEXT("Argument 3 is int64"), CastField<FInt64Property>(ReflectedParameters[3]));
        }
    }
    if (ManagedGeneratedClass != nullptr)
    {
        const FUnifiedScriptManagedFunctionBinding* Binding =
            ManagedGeneratedClass->ManagedFunctionBindings.Find(MixInternalName);
        TestTrue(TEXT("Generated class persists the managed function binding"), Binding != nullptr);
        if (Binding != nullptr)
        {
            TestEqual(TEXT("Binding targets the exact managed name"), Binding->ManagedName, FString(TEXT("Mix")));
            TestEqual(TEXT("Binding persists stable semantic identity"), Binding->FunctionId,
                Guid(TEXT("88888888888888888888888888888888")));
        }
    }
    const FBoolProperty* Enabled = FindFProperty<FBoolProperty>(GeneratedClass, TEXT("Enabled"));
    const FByteProperty* ByteValue = FindFProperty<FByteProperty>(GeneratedClass, TEXT("ByteValue"));
    const FIntProperty* Count = FindFProperty<FIntProperty>(GeneratedClass, TEXT("Count"));
    const FInt64Property* Large = FindFProperty<FInt64Property>(GeneratedClass, TEXT("Large"));
    const FFloatProperty* Ratio = FindFProperty<FFloatProperty>(GeneratedClass, TEXT("Ratio"));
    const FDoubleProperty* Precision = FindFProperty<FDoubleProperty>(GeneratedClass, TEXT("Precision"));
    const FStrProperty* Label = FindFProperty<FStrProperty>(GeneratedClass, TEXT("Label"));
    TestNotNull(TEXT("bool becomes FBoolProperty"), Enabled);
    TestNotNull(TEXT("byte becomes FByteProperty"), ByteValue);
    TestNotNull(TEXT("int becomes FIntProperty"), Count);
    TestNotNull(TEXT("long becomes FInt64Property"), Large);
    TestNotNull(TEXT("float becomes FFloatProperty"), Ratio);
    TestNotNull(TEXT("double becomes FDoubleProperty"), Precision);
    TestNotNull(TEXT("string becomes FStrProperty"), Label);
    if (Count == nullptr || Label == nullptr)
    {
        return false;
    }
    TestTrue(TEXT("SaveGame exposure reaches reflected flags"), Count->HasAnyPropertyFlags(CPF_SaveGame));
    TestTrue(TEXT("Replicated exposure reaches reflected flags"), Count->HasAnyPropertyFlags(CPF_Net));
    TestEqual(TEXT("Category reaches reflection metadata"), Count->GetMetaData(TEXT("Category")), FString(TEXT("Stats")));
    TestEqual(
        TEXT("Stable state ID reaches reflection metadata"),
        Count->GetMetaData(TEXT("UnifiedScript.StateId")),
        SemanticId(TEXT("33333333333333333333333333333333")));
    TestEqual(
        TEXT("String default reaches the generated CDO"),
        Label->GetPropertyValue_InContainer(GeneratedClass->GetDefaultObject()),
        FString(TEXT("Ready")));
    TestEqual(
        TEXT("State ID becomes the Blueprint variable GUID"),
        FBlueprintEditorUtils::FindMemberVariableGuidByName(Blueprint, TEXT("Count")),
        Guid(TEXT("33333333333333333333333333333333")));

    FEdGraphPinType UserType;
    UserType.PinCategory = UEdGraphSchema_K2::PC_Boolean;
    TestTrue(
        TEXT("Adds a user-owned control variable"),
        FBlueprintEditorUtils::AddMemberVariable(Blueprint, TEXT("UserOwned"), UserType, TEXT("false")));
    FKismetEditorUtilities::CompileBlueprint(Blueprint);
    Count = FindFProperty<FIntProperty>(Blueprint->GeneratedClass, TEXT("Count"));
    if (!TestNotNull(TEXT("Count survives the user variable compile"), Count))
    {
        return false;
    }
    UObject* Cdo = Blueprint->GeneratedClass->GetDefaultObject();
    Cdo->Modify();
    Count->SetPropertyValue_InContainer(Cdo, 77);

    const bool bReloaded = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *Blueprint,
        Document(ReloadedStates(), ReloadedFunctions()),
        false,
        Result,
        Error);
    TestTrue(*FString::Printf(TEXT("Reloaded state materializes: %s"), *Error), bReloaded);
    if (!bReloaded)
    {
        return false;
    }
    TestEqual(TEXT("One stable state is renamed"), Result.RenamedPropertyCount, 1);
    TestEqual(TEXT("One stale generated state is removed"), Result.RemovedPropertyCount, 1);
    TestEqual(TEXT("Stable function rename updates one binding"), Result.UpdatedFunctionCount, 1);
    TestEqual(TEXT("One stale generated function is removed"), Result.RemovedFunctionCount, 1);
    TestEqual(TEXT("One Blueprint default override is preserved"), Result.PreservedDefaultOverrideCount, 1);
    TestNull(TEXT("Old generated name leaves reflection"), FindFProperty<FProperty>(Blueprint->GeneratedClass, TEXT("Count")));
    TestNull(TEXT("Removed generated state leaves reflection"), FindFProperty<FProperty>(Blueprint->GeneratedClass, TEXT("Enabled")));
    TestNotNull(TEXT("User-owned variable survives reconciliation"), FindFProperty<FProperty>(Blueprint->GeneratedClass, TEXT("UserOwned")));
    const FDoubleProperty* RetypedLarge =
        FindFProperty<FDoubleProperty>(Blueprint->GeneratedClass, TEXT("Large"));
    if (TestNotNull(TEXT("Unmodified declaration default permits a type change"), RetypedLarge))
    {
        TestEqual(
            TEXT("Accepted type change applies its new class default"),
            RetypedLarge->GetPropertyValue_InContainer(
                Blueprint->GeneratedClass->GetDefaultObject()),
            42.5);
    }

    const FIntProperty* Score = FindFProperty<FIntProperty>(Blueprint->GeneratedClass, TEXT("Score"));
    if (!TestNotNull(TEXT("Renamed state remains an int property"), Score))
    {
        return false;
    }
    TestEqual(
        TEXT("Blueprint override survives source rename/default change"),
        Score->GetPropertyValue_InContainer(Blueprint->GeneratedClass->GetDefaultObject()),
        77);
    TestEqual(
        TEXT("Renamed property retains stable GUID"),
        FBlueprintEditorUtils::FindMemberVariableGuidByName(Blueprint, TEXT("Score")),
        Guid(TEXT("33333333333333333333333333333333")));
    const UFunction* RenamedFunction = Blueprint->GeneratedClass->FindFunctionByName(
        MixInternalName,
        EIncludeSuperFlag::ExcludeSuper);
    TestNotNull(TEXT("Source rename preserves the reflected internal function"), RenamedFunction);
    ManagedGeneratedClass = Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
    if (ManagedGeneratedClass != nullptr)
    {
        const FUnifiedScriptManagedFunctionBinding* Binding =
            ManagedGeneratedClass->ManagedFunctionBindings.Find(MixInternalName);
        TestTrue(TEXT("Renamed managed binding remains present"), Binding != nullptr);
        if (Binding != nullptr)
        {
            TestEqual(TEXT("Stable binding follows the source rename"), Binding->ManagedName, FString(TEXT("Combine")));
        }
    }

    const bool bNoChange = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *Blueprint,
        Document(ReloadedStates(), ReloadedFunctions()),
        true,
        Result,
        Error);
    TestTrue(*FString::Printf(TEXT("ValidateOnly accepts synchronized state: %s"), *Error), bNoChange);
    TestFalse(TEXT("Synchronized state is a no-change replay"), Result.bChanged);

    const FString TypeChange = State(
        TEXT("33333333333333333333333333333333"),
        TEXT("Score"),
        TEXT("double"),
        TEXT("System.Double"),
        TEXT("floatingPoint"),
        TEXT("9.0"));
    TestFalse(
        TEXT("Type change with a Blueprint override is rejected"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            Document(TypeChange, ReloadedFunctions()),
            false,
            Result,
            Error));
    TestTrue(TEXT("Type-change rejection is actionable"), Error.Contains(TEXT("overridden")));
    TestNotNull(TEXT("Rejected change leaves the int property intact"), FindFProperty<FIntProperty>(Blueprint->GeneratedClass, TEXT("Score")));

    const FString Collision = State(
        TEXT("88888888888888888888888888888888"),
        TEXT("UserOwned"),
        TEXT("bool"),
        TEXT("System.Boolean"),
        TEXT("boolean"),
        TEXT("false"));
    TestFalse(
        TEXT("A managed state cannot claim a user-owned Blueprint name"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            Document(Collision, ReloadedFunctions()),
            false,
            Result,
            Error));
    TestTrue(TEXT("Collision rejection identifies ownership"), Error.Contains(TEXT("different identity")));

    const FString Unsupported = State(
        TEXT("99999999999999999999999999999999"),
        TEXT("Mode"),
        TEXT("Mode"),
        TEXT("Tests.Mode"),
        TEXT("enum"),
        TEXT("\"Idle\""));
    TestFalse(
        TEXT("Unsupported enum fails before Blueprint mutation"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            Document(Unsupported, ReloadedFunctions()),
            false,
            Result,
            Error));
    TestTrue(TEXT("Unsupported-type rejection lists reviewed types"), Error.Contains(TEXT("Reviewed types")));
    TestEqual(TEXT("Rejected plans leave six generated plus one user property"), Blueprint->NewVariables.Num(), 7);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptBlueprintManagedFunctionDispatchTest,
    "UnifiedScript.Unreal.ScriptClass.BlueprintManagedFunctionDispatch",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptBlueprintManagedFunctionDispatchTest::RunTest(
    const FString& Parameters)
{
    using namespace UE::UnifiedScript::BlueprintClass::Tests;

    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString AssetName = TEXT("BP_UnifiedScriptFunctionDispatch_") + Suffix;
    UPackage* Package = CreatePackage(*(TEXT("/Engine/Transient/") + AssetName));
    UUnifiedScriptBlueprint* Blueprint = Cast<UUnifiedScriptBlueprint>(
        FKismetEditorUtilities::CreateBlueprint(
            UUnifiedScriptComponent::StaticClass(),
            Package,
            FName(*AssetName),
            BPTYPE_Normal,
            UUnifiedScriptBlueprint::StaticClass(),
            UUnifiedScriptBlueprintGeneratedClass::StaticClass(),
            FName(TEXT("UnifiedScriptBlueprintManagedFunctionDispatch"))));
    if (!TestNotNull(TEXT("Creates the managed dispatch Blueprint"), Blueprint) ||
        !TestNotNull(
            TEXT("Managed dispatch Blueprint has a generated class"),
            Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr))
    {
        return false;
    }

    constexpr const TCHAR* DemoClassId = TEXT("cefc9ee5805e3ed5610154145ea986dd");
    constexpr const TCHAR* DemoQualifiedName =
        TEXT("UnifiedScript.Unreal.Demo.DemoScript");
    constexpr const TCHAR* DemoAssetPath = TEXT("Unreal/Demo");
    constexpr const TCHAR* FunctionId = TEXT("4b2b9a44f72edea2e4f0cdf31376c2d1");
    const FString RewireParameters = FString::Join(
        TArray<FString>{
            Parameter(
                TEXT("9d7059e580b913b0ecc7e7a77b140414"),
                TEXT("value"),
                TEXT("System.Int32")),
            Parameter(
                TEXT("d422d800eeeed7faab2f376492a09420"),
                TEXT("doubleValue"),
                TEXT("System.Boolean")),
        },
        TEXT(","));
    const FString RewireFunction = Function(
        FunctionId,
        TEXT("RewirePreview"),
        TEXT("System.Int32"),
        RewireParameters);
    const FString InitialValueState = State(
        TEXT("d4463d57397c77264a0e282ca6c29432"),
        TEXT("InitialValue"),
        TEXT("int"),
        TEXT("System.Int32"),
        TEXT("integer"),
        TEXT("10"),
        true,
        true,
        false,
        TEXT("Demo"));

    UUnifiedScriptAsset* ScriptAsset = NewObject<UUnifiedScriptAsset>(Package);
    ScriptAsset->ScriptClassId = Guid(DemoClassId);
    ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::ActorComponent;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    ScriptAsset->ScriptTypeName = DemoQualifiedName;
    ScriptAsset->DeclarationAssetPath = DemoAssetPath;
    ScriptAsset->CompiledChunkPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.uschunk");
    ScriptAsset->ScriptAssemblyPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
    Blueprint->ScriptAsset = ScriptAsset;

    FUnifiedScriptBlueprintClassSyncResult SyncResult;
    FString Error;
    const bool bSynchronized =
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            Document(
                InitialValueState,
                RewireFunction,
                DemoClassId,
                DemoQualifiedName,
                DemoAssetPath),
            false,
            SyncResult,
            Error);
    if (!TestTrue(
            *FString::Printf(TEXT("Materializes the live managed function: %s"), *Error),
            bSynchronized))
    {
        return false;
    }

    const FName InternalName(
        *FString::Printf(TEXT("USF_%s"), FunctionId));
    UFunction* ManagedFunction = Blueprint->GeneratedClass->FindFunctionByName(
        InternalName,
        EIncludeSuperFlag::ExcludeSuper);
    const FIntProperty* ValueProperty = ManagedFunction != nullptr
        ? FindFProperty<FIntProperty>(ManagedFunction, TEXT("value"))
        : nullptr;
    const FBoolProperty* DoubleValueProperty = ManagedFunction != nullptr
        ? FindFProperty<FBoolProperty>(ManagedFunction, TEXT("doubleValue"))
        : nullptr;
    const FIntProperty* ReturnProperty = ManagedFunction != nullptr
        ? CastField<FIntProperty>(ManagedFunction->GetReturnProperty())
        : nullptr;
    if (!TestNotNull(TEXT("Live dispatch UFunction exists"), ManagedFunction) ||
        !TestNotNull(TEXT("Live dispatch int argument exists"), ValueProperty) ||
        !TestNotNull(TEXT("Live dispatch bool argument exists"), DoubleValueProperty) ||
        !TestNotNull(TEXT("Live dispatch int return exists"), ReturnProperty))
    {
        return false;
    }

    const int32 InitialManagedWorldCount =
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Creates a gameplay World for managed dispatch"), World))
    {
        return false;
    }
    World->AddToRoot();
    UUnifiedScriptWorldSubsystem* Host =
        World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    UUnifiedScriptComponent* Component =
        NewObject<UUnifiedScriptComponent>(World, Blueprint->GeneratedClass);
    const bool bRuntimeObjectsReady =
        TestNotNull(TEXT("Managed dispatch World has a host"), Host) &&
        TestNotNull(TEXT("Creates the generated managed component"), Component);
    if (bRuntimeObjectsReady)
    {
        Component->ScriptAsset = ScriptAsset;
        Component->RegisterComponentWithWorld(World);
        Component->BeginPlay();
        TestTrue(
            *FString::Printf(TEXT("Starts the real managed DemoScript: %s"),
                *Component->LastScriptError),
            Component->bScriptRunning);

        if (Component->bScriptRunning)
        {
            FStructOnScope FunctionParameters(ManagedFunction);
            uint8* ParameterMemory = FunctionParameters.GetStructMemory();
            ValueProperty->SetPropertyValue_InContainer(ParameterMemory, 21);
            DoubleValueProperty->SetPropertyValue_InContainer(ParameterMemory, true);
            Component->ProcessEvent(ManagedFunction, ParameterMemory);
            TestEqual(
                TEXT("Blueprint UFunction reaches CoreCLR and returns managed result"),
                ReturnProperty->GetPropertyValue_InContainer(ParameterMemory),
                42);
            Component->EndPlay(EEndPlayReason::Destroyed);
            TestFalse(TEXT("Managed dispatch instance stops cleanly"),
                Component->bScriptRunning);
        }
    }

    if (Component != nullptr)
    {
        Component->UnregisterComponent();
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    TestEqual(
        TEXT("Managed dispatch World releases its collectible context"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialManagedWorldCount);
    return bRuntimeObjectsReady && !HasAnyErrors();
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptBlueprintManagedStaticFunctionDispatchTest,
    "UnifiedScript.Unreal.ScriptClass.BlueprintManagedStaticFunctionDispatch",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptBlueprintManagedStaticFunctionDispatchTest::RunTest(
    const FString& Parameters)
{
    using namespace UE::UnifiedScript::BlueprintClass::Tests;

    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString AssetName = TEXT("BP_UnifiedScriptStaticDispatch_") + Suffix;
    UPackage* Package = CreatePackage(*(TEXT("/Engine/Transient/") + AssetName));
    UUnifiedScriptBlueprint* Blueprint = Cast<UUnifiedScriptBlueprint>(
        FKismetEditorUtilities::CreateBlueprint(
            UUnifiedScriptBlueprintFunctionLibrary::StaticClass(),
            Package,
            FName(*AssetName),
            BPTYPE_FunctionLibrary,
            UUnifiedScriptBlueprint::StaticClass(),
            UUnifiedScriptBlueprintGeneratedClass::StaticClass(),
            FName(TEXT("UnifiedScriptBlueprintManagedStaticFunctionDispatch"))));
    if (!TestNotNull(TEXT("Creates a managed static FunctionLibrary Blueprint"), Blueprint) ||
        !TestNotNull(
            TEXT("Static FunctionLibrary Blueprint has a generated class"),
            Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr))
    {
        return false;
    }

    constexpr const TCHAR* LibraryClassId = TEXT("0fdbad6aa2af414e8d8f073115cf03ab");
    constexpr const TCHAR* LibraryQualifiedName =
        TEXT("UnifiedScript.Unreal.Demo.DemoLibrary");
    constexpr const TCHAR* LibraryAssetPath = TEXT("Unreal/DemoLibrary");
    constexpr const TCHAR* DescribeId = TEXT("10101010101010101010101010101010");
    constexpr const TCHAR* EchoObjectId = TEXT("20202020202020202020202020202020");
    constexpr const TCHAR* OffsetVectorId = TEXT("30303030303030303030303030303030");

    const FString DescribeParameters = FString::Join(
        TArray<FString>{
            Parameter(
                TEXT("11111111111111111111111111111110"),
                TEXT("value"),
                TEXT("UnifiedScript.Unreal.UnrealObjectHandle")),
            Parameter(
                TEXT("11111111111111111111111111111120"),
                TEXT("location"),
                TEXT("UnifiedScript.Unreal.UnrealVector")),
            Parameter(
                TEXT("11111111111111111111111111111130"),
                TEXT("label"),
                TEXT("System.String")),
        },
        TEXT(","));
    const FString EchoParameters = Parameter(
        TEXT("22222222222222222222222222222220"),
        TEXT("value"),
        TEXT("UnifiedScript.Unreal.UnrealObjectHandle"));
    const FString VectorParameters = Parameter(
        TEXT("33333333333333333333333333333330"),
        TEXT("value"),
        TEXT("UnifiedScript.Unreal.UnrealVector"));
    const FString Functions = FString::Join(
        TArray<FString>{
            Function(
                DescribeId,
                TEXT("Describe"),
                TEXT("System.String"),
                DescribeParameters,
                true),
            Function(
                EchoObjectId,
                TEXT("EchoObject"),
                TEXT("UnifiedScript.Unreal.UnrealObjectHandle"),
                EchoParameters,
                true),
            Function(
                OffsetVectorId,
                TEXT("OffsetVector"),
                TEXT("UnifiedScript.Unreal.UnrealVector"),
                VectorParameters,
                true),
        },
        TEXT(","));

    UUnifiedScriptAsset* ScriptAsset = NewObject<UUnifiedScriptAsset>(Package);
    ScriptAsset->ScriptClassId = Guid(LibraryClassId);
    ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::BlueprintFunctionLibrary;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    ScriptAsset->ScriptTypeName = LibraryQualifiedName;
    ScriptAsset->DeclarationAssetPath = LibraryAssetPath;
    ScriptAsset->CompiledChunkPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.uschunk");
    ScriptAsset->ScriptAssemblyPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
    Blueprint->ScriptAsset = ScriptAsset;

    FUnifiedScriptBlueprintClassSyncResult SyncResult;
    FString Error;
    const bool bSynchronized =
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            Document(
                FString(),
                Functions,
                LibraryClassId,
                LibraryQualifiedName,
                LibraryAssetPath),
            false,
            SyncResult,
            Error);
    if (!TestTrue(
            *FString::Printf(
                TEXT("Materializes the static managed FunctionLibrary: %s"),
                *Error),
            bSynchronized))
    {
        return false;
    }
    TestEqual(TEXT("Three static functions are materialized"),
        SyncResult.DeclaredFunctionCount, 3);

    UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
    if (!TestNotNull(TEXT("Static proxy retains the product GeneratedClass"), GeneratedClass))
    {
        return false;
    }
    TestEqual(
        TEXT("GeneratedClass retains the static Script Asset through cook/load"),
        GeneratedClass->ManagedScriptAsset.Get(),
        ScriptAsset);

    const FName DescribeInternalName(
        *FString::Printf(TEXT("USF_%s"), DescribeId));
    UFunction* DescribeFunction = GeneratedClass->FindFunctionByName(
        DescribeInternalName,
        EIncludeSuperFlag::ExcludeSuper);
    const FName EchoObjectInternalName(
        *FString::Printf(TEXT("USF_%s"), EchoObjectId));
    UFunction* EchoObjectFunction = GeneratedClass->FindFunctionByName(
        EchoObjectInternalName,
        EIncludeSuperFlag::ExcludeSuper);
    const FName OffsetVectorInternalName(
        *FString::Printf(TEXT("USF_%s"), OffsetVectorId));
    UFunction* OffsetVectorFunction = GeneratedClass->FindFunctionByName(
        OffsetVectorInternalName,
        EIncludeSuperFlag::ExcludeSuper);
    const FUnifiedScriptManagedFunctionBinding* DescribeBinding =
        GeneratedClass->ManagedFunctionBindings.Find(DescribeInternalName);
    if (!TestNotNull(TEXT("Static Describe UFunction exists"), DescribeFunction) ||
        !TestNotNull(TEXT("Static EchoObject UFunction exists"), EchoObjectFunction) ||
        !TestNotNull(TEXT("Static OffsetVector UFunction exists"), OffsetVectorFunction) ||
        !TestNotNull(TEXT("Static Describe binding exists"), DescribeBinding))
    {
        return false;
    }
    TestTrue(TEXT("Generated FunctionLibrary UFunction is static"),
        DescribeFunction->HasAnyFunctionFlags(FUNC_Static));
    TestEqual(TEXT("Static binding selects ABI v2"), DescribeBinding->AbiVersion, 2);
    TestTrue(TEXT("Static binding owns library routing"), DescribeBinding->bStatic);
    TestNotNull(
        TEXT("Static call persists a hidden WorldContextObject"),
        FindFProperty<FObjectPropertyBase>(DescribeFunction, TEXT("WorldContextObject")));
    TestNotNull(TEXT("String parameter becomes FString"),
        FindFProperty<FStrProperty>(DescribeFunction, TEXT("label")));
    TestNotNull(
        TEXT("FunctionLibrary compiler owns its separate Kismet World context"),
        FindFProperty<FObjectPropertyBase>(DescribeFunction, TEXT("__WorldContext")));
    const FStructProperty* LocationProperty =
        FindFProperty<FStructProperty>(DescribeFunction, TEXT("location"));
    TestTrue(TEXT("UnrealVector parameter becomes FVector"),
        LocationProperty != nullptr &&
            LocationProperty->Struct == TBaseStructure<FVector>::Get());

    const int32 InitialManagedWorldCount =
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Creates a gameplay World for static dispatch"), World))
    {
        return false;
    }
    World->AddToRoot();
    UUnifiedScriptWorldSubsystem* Host =
        World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    UUnifiedScriptComponent* WorldContext =
        NewObject<UUnifiedScriptComponent>(World);
    const bool bRuntimeReady =
        TestNotNull(TEXT("Static dispatch World has a managed host"), Host) &&
        TestNotNull(TEXT("Static dispatch has a World context object"), WorldContext);
    if (bRuntimeReady)
    {
        WorldContext->RegisterComponentWithWorld(World);
        UObject* LibraryObject = GeneratedClass->GetDefaultObject();
        const FObjectPropertyBase* ContextProperty =
            FindFProperty<FObjectPropertyBase>(DescribeFunction, TEXT("WorldContextObject"));
        const FObjectPropertyBase* ValueProperty =
            FindFProperty<FObjectPropertyBase>(DescribeFunction, TEXT("value"));
        const FStrProperty* LabelProperty =
            FindFProperty<FStrProperty>(DescribeFunction, TEXT("label"));
        const FStrProperty* ReturnProperty =
            CastField<FStrProperty>(DescribeFunction->GetReturnProperty());
        if (TestNotNull(TEXT("Static Describe context property exists"), ContextProperty) &&
            TestNotNull(TEXT("Static Describe object property exists"), ValueProperty) &&
            TestNotNull(TEXT("Static Describe label property exists"), LabelProperty) &&
            TestNotNull(TEXT("Static Describe return property exists"), ReturnProperty))
        {
            FStructOnScope Call(DescribeFunction);
            uint8* Memory = Call.GetStructMemory();
            ContextProperty->SetObjectPropertyValue_InContainer(Memory, WorldContext);
            ValueProperty->SetObjectPropertyValue_InContainer(Memory, WorldContext);
            *LocationProperty->ContainerPtrToValuePtr<FVector>(Memory) =
                FVector(1.0, 2.0, 3.0);
            LabelProperty->SetPropertyValue_InContainer(Memory, TEXT("demo"));
            LibraryObject->ProcessEvent(DescribeFunction, Memory);
            TestEqual(
                TEXT("Static String/ObjectHandle/FVector call reaches CoreCLR"),
                ReturnProperty->GetPropertyValue_InContainer(Memory),
                FString(TEXT("demo")));
        }

        const FObjectPropertyBase* EchoContextProperty =
            FindFProperty<FObjectPropertyBase>(EchoObjectFunction, TEXT("WorldContextObject"));
        const FObjectPropertyBase* EchoValueProperty =
            FindFProperty<FObjectPropertyBase>(EchoObjectFunction, TEXT("value"));
        const FObjectPropertyBase* EchoReturnProperty = CastField<FObjectPropertyBase>(
            EchoObjectFunction->GetReturnProperty());
        if (TestNotNull(TEXT("Static EchoObject context property exists"), EchoContextProperty) &&
            TestNotNull(TEXT("Static EchoObject input property exists"), EchoValueProperty) &&
            TestNotNull(TEXT("Static EchoObject return property exists"), EchoReturnProperty))
        {
            FStructOnScope Call(EchoObjectFunction);
            uint8* Memory = Call.GetStructMemory();
            EchoContextProperty->SetObjectPropertyValue_InContainer(Memory, WorldContext);
            EchoValueProperty->SetObjectPropertyValue_InContainer(Memory, WorldContext);
            LibraryObject->ProcessEvent(EchoObjectFunction, Memory);
            TestEqual(
                TEXT("Static ObjectHandle return resolves through the World registry"),
                EchoReturnProperty->GetObjectPropertyValue_InContainer(Memory),
                static_cast<UObject*>(WorldContext));
        }

        const FObjectPropertyBase* VectorContextProperty =
            FindFProperty<FObjectPropertyBase>(OffsetVectorFunction, TEXT("WorldContextObject"));
        const FStructProperty* VectorValueProperty =
            FindFProperty<FStructProperty>(OffsetVectorFunction, TEXT("value"));
        const FStructProperty* VectorReturnProperty = CastField<FStructProperty>(
            OffsetVectorFunction->GetReturnProperty());
        if (TestNotNull(TEXT("Static OffsetVector context property exists"), VectorContextProperty) &&
            TestNotNull(TEXT("Static OffsetVector input property exists"), VectorValueProperty) &&
            TestNotNull(TEXT("Static OffsetVector return property exists"), VectorReturnProperty))
        {
            FStructOnScope Call(OffsetVectorFunction);
            uint8* Memory = Call.GetStructMemory();
            const FVector Expected(4.0, 5.0, 6.0);
            VectorContextProperty->SetObjectPropertyValue_InContainer(Memory, WorldContext);
            *VectorValueProperty->ContainerPtrToValuePtr<FVector>(Memory) = Expected;
            LibraryObject->ProcessEvent(OffsetVectorFunction, Memory);
            TestEqual(
                TEXT("Static FVector return stays inline in the native value cell"),
                *VectorReturnProperty->ContainerPtrToValuePtr<FVector>(Memory),
                Expected);
        }
    }

    if (WorldContext != nullptr)
    {
        WorldContext->UnregisterComponent();
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    TestEqual(
        TEXT("Static library collectible context retires with its World"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
        InitialManagedWorldCount);
    return bRuntimeReady && !HasAnyErrors();
}

#endif

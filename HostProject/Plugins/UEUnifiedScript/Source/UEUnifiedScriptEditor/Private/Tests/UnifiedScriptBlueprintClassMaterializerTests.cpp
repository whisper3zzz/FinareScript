#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptBlueprintClassMaterializer.h"

#include "Dom/JsonObject.h"
#include "EdGraph/EdGraph.h"
#include "EdGraph/EdGraphPin.h"
#include "EdGraphSchema_K2.h"
#include "Engine/Engine.h"
#include "Engine/EngineTypes.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "Components/StaticMeshComponent.h"
#include "EnhancedInputComponent.h"
#include "GameFramework/Actor.h"
#include "InputAction.h"
#include "K2Node_CallParentFunction.h"
#include "K2Node_Event.h"
#include "K2Node_FunctionEntry.h"
#include "K2Node_FunctionResult.h"
#include "K2Node_CallFunction.h"
#include "K2Node_VariableSet.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformFileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/Guid.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/Parse.h"
#include "Misc/ScopeExit.h"
#include "PackageTools.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptClassBases.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptDeclarationMaterializer.h"
#include "UnifiedScriptInteropFixture.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "Interfaces/IPluginManager.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/SoftObjectPtr.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::BlueprintClass::Tests
{
FString SemanticId(const TCHAR* Digits)
{
    return FString::Printf(TEXT("us_%s"), Digits);
}

bool ReadFixtureOutputArgument(FString& OutPath)
{
    if (!FParse::Value(
            FCommandLine::Get(),
            TEXT("UnifiedScriptFunctionFixtureOutput="),
            OutPath))
    {
        return false;
    }
    OutPath.TrimQuotesInline();
    return !OutPath.IsEmpty();
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
    const TCHAR* Category = TEXT("State"),
    const FString& UnrealType = FString())
{
    return FString::Printf(
        TEXT(R"JSON(
      {
        "id": "us_%s",
        "name": "%s",
        "typeDisplayName": "%s",
        "typeMetadataName": "%s",
        "typeKind": "%s"%s,
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
        UnrealType.IsEmpty()
            ? TEXT("")
            : *FString::Printf(TEXT(",\n        \"unrealType\": %s"), *UnrealType),
        DefaultJson,
        bInstanceEditable ? TEXT("true") : TEXT("false"),
        bSaveGame ? TEXT("true") : TEXT("false"),
        bReplicated ? TEXT("true") : TEXT("false"),
        Category);
}

FString VectorType()
{
    return TEXT(R"JSON({
      "kind": "struct",
      "unrealTypePath": "/Script/CoreUObject.Vector",
      "unrealNativeName": "Vector",
      "fields": [
        { "index": 0, "nativeName": "X", "typeMetadataName": "System.Double" },
        { "index": 1, "nativeName": "Y", "typeMetadataName": "System.Double" },
        { "index": 2, "nativeName": "Z", "typeMetadataName": "System.Double" }
      ]
    })JSON");
}

FString Parameter(
    const TCHAR* Id,
    const TCHAR* Name,
    const TCHAR* MetadataType,
    const FString& UnrealType = FString(),
    const TCHAR* Direction = TEXT("input"))
{
    return FString::Printf(
        TEXT(R"JSON({
        "id": "us_%s",
        "name": "%s",
        "typeMetadataName": "%s"%s%s
      })JSON"),
        Id,
        Name,
        MetadataType,
        UnrealType.IsEmpty()
            ? TEXT("")
            : *FString::Printf(TEXT(",\n        \"unrealType\": %s"), *UnrealType),
        FCString::Strcmp(Direction, TEXT("input")) == 0
            ? TEXT("")
            : *FString::Printf(TEXT(",\n        \"direction\": \"%s\""), Direction));
}

FString Function(
    const TCHAR* Id,
    const TCHAR* Name,
    const TCHAR* ReturnType,
    const FString& Parameters,
    bool bIsStatic = false,
    bool bIsPublic = true,
    bool bIsEvent = false,
    const FString& ReturnUnrealType = FString(),
    bool bBlueprintEvent = false,
    const FString& AdditionalFields = FString())
{
    return FString::Printf(
        TEXT(R"JSON({
      "id": "us_%s",
      "name": "%s",
      "qualifiedName": "Tests.StateActor.%s()",
      "returnTypeMetadataName": "%s"%s,
      "isStatic": %s,
      "isPublic": %s,
      "isEvent": %s%s%s,
      "parameters": [%s]
    })JSON"),
        Id,
        Name,
        Name,
        ReturnType,
        ReturnUnrealType.IsEmpty()
            ? TEXT("")
            : *FString::Printf(
                TEXT(",\n      \"returnUnrealType\": %s"),
                *ReturnUnrealType),
        bIsStatic ? TEXT("true") : TEXT("false"),
        bIsPublic ? TEXT("true") : TEXT("false"),
        bIsEvent ? TEXT("true") : TEXT("false"),
        bBlueprintEvent
            ? TEXT(",\n      \"blueprintEvent\": { \"superMode\": \"explicit\" }")
            : TEXT(""),
        *AdditionalFields,
        *Parameters);
}

FString LifecycleHooks()
{
    return TEXT(R"JSON(
      {
        "id": "us_11111111111111111111111111111110",
        "kind": "beginPlay",
        "managedName": "OnBeginPlay",
        "span": { "filePath": "Managed/StateActor.cs", "start": { "line": 1, "column": 1 }, "end": { "line": 1, "column": 2 } }
      },
      {
        "id": "us_22222222222222222222222222222220",
        "kind": "tick",
        "managedName": "OnTick",
        "span": { "filePath": "Managed/StateActor.cs", "start": { "line": 2, "column": 1 }, "end": { "line": 2, "column": 2 } }
      },
      {
        "id": "us_33333333333333333333333333333330",
        "kind": "endPlay",
        "managedName": "OnEndPlay",
        "span": { "filePath": "Managed/StateActor.cs", "start": { "line": 3, "column": 1 }, "end": { "line": 3, "column": 2 } }
      })JSON");
}

FString InteropModeType()
{
    return TEXT(R"JSON({
      "kind": "enum",
      "unrealTypePath": "/Script/UEUnifiedScriptRuntime.EUnifiedScriptInteropMode",
      "unrealNativeName": "EUnifiedScriptInteropMode",
      "fields": []
    })JSON");
}

FString HitResultType()
{
    return TEXT(R"JSON({
      "kind": "struct",
      "unrealTypePath": "/Script/Engine.HitResult",
      "unrealNativeName": "FHitResult",
      "fields": [
        { "index": 0, "nativeName": "BoneName", "typeMetadataName": "UnifiedScript.Unreal.UnrealName" },
        { "index": 1, "nativeName": "Distance", "typeMetadataName": "System.Single" },
        { "index": 2, "nativeName": "ElementIndex", "typeMetadataName": "System.Byte" },
        { "index": 3, "nativeName": "FaceIndex", "typeMetadataName": "System.Int32" },
        { "index": 4, "nativeName": "ImpactNormal", "typeMetadataName": "UnifiedScript.Unreal.UnrealVector" },
        { "index": 5, "nativeName": "ImpactPoint", "typeMetadataName": "UnifiedScript.Unreal.UnrealVector" },
        { "index": 6, "nativeName": "Item", "typeMetadataName": "System.Int32" },
        { "index": 7, "nativeName": "Location", "typeMetadataName": "UnifiedScript.Unreal.UnrealVector" },
        { "index": 8, "nativeName": "MyBoneName", "typeMetadataName": "UnifiedScript.Unreal.UnrealName" },
        { "index": 9, "nativeName": "MyItem", "typeMetadataName": "System.Int32" },
        { "index": 10, "nativeName": "Normal", "typeMetadataName": "UnifiedScript.Unreal.UnrealVector" },
        { "index": 11, "nativeName": "PenetrationDepth", "typeMetadataName": "System.Single" },
        { "index": 12, "nativeName": "Time", "typeMetadataName": "System.Single" },
        { "index": 13, "nativeName": "TraceEnd", "typeMetadataName": "UnifiedScript.Unreal.UnrealVector" },
        { "index": 14, "nativeName": "TraceStart", "typeMetadataName": "UnifiedScript.Unreal.UnrealVector" },
        { "index": 15, "nativeName": "bBlockingHit", "typeMetadataName": "System.Boolean" },
        { "index": 16, "nativeName": "bStartPenetrating", "typeMetadataName": "System.Boolean" }
      ]
    })JSON");
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
                TEXT("dddddddddddddddddddddddddddddddd"),
                TEXT("Activate"),
                TEXT("System.Void"),
                Parameter(
                    TEXT("d1111111111111111111111111111111"),
                    TEXT("amount"),
                    TEXT("System.Int32")),
                false,
                true,
                false,
                FString(),
                true),
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
    return FString::Join(
        TArray<FString>{
            Function(
                TEXT("88888888888888888888888888888888"),
                TEXT("Combine"),
                TEXT("System.Int64"),
                MixParameters),
            Function(
                TEXT("dddddddddddddddddddddddddddddddd"),
                TEXT("Activate"),
                TEXT("System.Void"),
                Parameter(
                    TEXT("d1111111111111111111111111111111"),
                    TEXT("amount"),
                    TEXT("System.Int32")),
                false,
                true,
                false,
                FString(),
                true),
        },
        TEXT(","));
}

FString Document(
    const FString& States,
    const FString& Functions = FString(),
    const TCHAR* AssetId = TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
    const TCHAR* QualifiedName = TEXT("Tests.StateActor"),
    const TCHAR* AssetPath = TEXT("Tests/StateActor"),
    const FString& Lifecycle = FString())
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
    "functions": [%s],
    "lifecycleHooks": [%s]
  }]
}
)JSON"),
        AssetId,
        QualifiedName,
        AssetPath,
        *States,
        *Functions,
        *Lifecycle);
}

FString ActorDocument(const FString& States, const FString& Functions)
{
    return Document(
        States,
        Functions,
        TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        TEXT("Tests.StateActor"),
        TEXT("Tests/StateActor"),
        LifecycleHooks());
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
        State(
            TEXT("88888888888888888888888888888881"),
            TEXT("RowName"),
            TEXT("UnrealName"),
            TEXT("UnifiedScript.Unreal.UnrealName"),
            TEXT("name"),
            TEXT("\"Player\"")),
        State(
            TEXT("88888888888888888888888888888882"),
            TEXT("DisplayText"),
            TEXT("UnrealText"),
            TEXT("UnifiedScript.Unreal.UnrealText"),
            TEXT("text"),
            TEXT("\"Loaded\"")),
        State(
            TEXT("88888888888888888888888888888883"),
            TEXT("Target"),
            TEXT("UnrealObjectHandle"),
            TEXT("UnifiedScript.Unreal.UnrealObjectHandle"),
            TEXT("object"),
            TEXT("null")),
        State(
            TEXT("88888888888888888888888888888884"),
            TEXT("ActorClass"),
            TEXT("UnrealClassHandle"),
            TEXT("UnifiedScript.Unreal.UnrealClassHandle"),
            TEXT("class"),
            TEXT("null")),
        State(
            TEXT("88888888888888888888888888888885"),
            TEXT("Offset"),
            TEXT("UnrealVector"),
            TEXT("UnifiedScript.Unreal.UnrealVector"),
            TEXT("struct"),
            TEXT("{ \"x\": 1.5, \"y\": 2.5, \"z\": 3.5 }"),
            true,
            false,
            false,
            TEXT("State"),
            VectorType()),
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
        State(
            TEXT("88888888888888888888888888888881"),
            TEXT("RowName"),
            TEXT("UnrealName"),
            TEXT("UnifiedScript.Unreal.UnrealName"),
            TEXT("name"),
            TEXT("\"Player\"")),
        State(
            TEXT("88888888888888888888888888888882"),
            TEXT("DisplayText"),
            TEXT("UnrealText"),
            TEXT("UnifiedScript.Unreal.UnrealText"),
            TEXT("text"),
            TEXT("\"Loaded\"")),
        State(
            TEXT("88888888888888888888888888888883"),
            TEXT("Target"),
            TEXT("UnrealObjectHandle"),
            TEXT("UnifiedScript.Unreal.UnrealObjectHandle"),
            TEXT("object"),
            TEXT("null")),
        State(
            TEXT("88888888888888888888888888888884"),
            TEXT("ActorClass"),
            TEXT("UnrealClassHandle"),
            TEXT("UnifiedScript.Unreal.UnrealClassHandle"),
            TEXT("class"),
            TEXT("null")),
        State(
            TEXT("88888888888888888888888888888885"),
            TEXT("Offset"),
            TEXT("UnrealVector"),
            TEXT("UnifiedScript.Unreal.UnrealVector"),
            TEXT("struct"),
            TEXT("{ \"x\": 1.5, \"y\": 2.5, \"z\": 3.5 }"),
            true,
            false,
            false,
            TEXT("State"),
            VectorType()),
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
        ActorDocument(InitialStates(), InitialFunctions()),
        false,
        Result,
        Error);
    TestTrue(*FString::Printf(TEXT("Materializes reviewed state: %s"), *Error), bInitial);
    if (!bInitial)
    {
        return false;
    }
    TestTrue(TEXT("Initial materialization changes the proxy"), Result.bChanged);
    TestEqual(TEXT("Twelve reviewed types become properties"), Result.AddedPropertyCount, 12);
    TestEqual(TEXT("Twelve states are declared"), Result.DeclaredStateCount, 12);
    TestEqual(TEXT("Three public managed functions are declared"), Result.DeclaredFunctionCount, 3);
    TestEqual(TEXT("Three callable functions become graphs"), Result.AddedFunctionCount, 3);
    TestEqual(TEXT("Three lifecycle hooks are declared"), Result.DeclaredLifecycleHookCount, 3);
    TestEqual(TEXT("Initial lifecycle identities are persisted"), Result.UpdatedLifecycleHookCount, 3);
    TestEqual(TEXT("Blueprint remains compiled"), Blueprint->Status, BS_UpToDate);

    UClass* GeneratedClass = Blueprint->GeneratedClass;
    UUnifiedScriptBlueprintGeneratedClass* ManagedGeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(GeneratedClass);
    TestNotNull(TEXT("Proxy uses the managed-function generated class"), ManagedGeneratedClass);
    const FName MixInternalName(TEXT("USF_88888888888888888888888888888888"));
    const FName ActivateInternalName(TEXT("USF_dddddddddddddddddddddddddddddddd"));
    const UFunction* MixFunction = GeneratedClass->FindFunctionByName(
        MixInternalName,
        EIncludeSuperFlag::ExcludeSuper);
    TestNotNull(TEXT("Managed function becomes reflected UFunction"), MixFunction);
    if (MixFunction != nullptr)
    {
        TestTrue(TEXT("Managed UFunction is Blueprint callable"),
            MixFunction->HasAnyFunctionFlags(FUNC_BlueprintCallable));
        TestTrue(TEXT("Managed UFunction is registered for Blueprint VM dispatch"),
            MixFunction->HasAnyFunctionFlags(FUNC_Native));
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

        const FUnifiedScriptManagedFunctionBinding* EventBinding =
            ManagedGeneratedClass->ManagedFunctionBindings.Find(ActivateInternalName);
        TestTrue(TEXT("Generated class persists the Blueprint event binding"),
            EventBinding != nullptr);
        if (EventBinding != nullptr)
        {
            TestTrue(TEXT("Binding retains Blueprint event behavior"),
                EventBinding->bBlueprintEvent);
            TestEqual(TEXT("Binding retains explicit Super behavior"),
                EventBinding->BlueprintEventSuperMode,
                EUnifiedScriptBlueprintEventSuperMode::Explicit);
        }
        TestEqual(TEXT("Generated class persists three lifecycle bindings"),
            ManagedGeneratedClass->ManagedLifecycleBindings.Num(), 3);
        const FUnifiedScriptManagedLifecycleBinding* TickBinding =
            ManagedGeneratedClass->ManagedLifecycleBindings.Find(
                EUnifiedScriptManagedLifecycleHookKind::Tick);
        TestTrue(TEXT("Tick lifecycle binding exists"), TickBinding != nullptr);
        if (TickBinding != nullptr)
        {
            TestEqual(TEXT("Tick binding retains exact managed method"),
                TickBinding->ManagedName, FName(TEXT("OnTick")));
            TestEqual(TEXT("Tick binding retains stable source ID"),
                TickBinding->HookId,
                Guid(TEXT("22222222222222222222222222222220")));
        }
    }
    UFunction* ActivateFunction = GeneratedClass->FindFunctionByName(
        ActivateInternalName,
        EIncludeSuperFlag::ExcludeSuper);
    if (TestNotNull(TEXT("Managed Blueprint event becomes a reflected UFunction"),
            ActivateFunction))
    {
        TestTrue(TEXT("Managed event is an ordinary Blueprint event"),
            ActivateFunction->HasAllFunctionFlags(FUNC_Event | FUNC_BlueprintEvent));
        TestTrue(TEXT("Managed event remains Blueprint callable"),
            ActivateFunction->HasAnyFunctionFlags(FUNC_BlueprintCallable));
        TestTrue(TEXT("Managed event uses the native parent-dispatch contract"),
            ActivateFunction->HasAnyFunctionFlags(FUNC_Native));
        TestTrue(TEXT("Blueprint schema exposes the event as overridable"),
            UEdGraphSchema_K2::CanKismetOverrideFunction(ActivateFunction));
    }
    if (ActivateFunction != nullptr)
    {
        const FString ChildName = TEXT("BP_UnifiedScriptEventOverride_") + Suffix;
        UPackage* ChildPackage = CreatePackage(
            *(TEXT("/Engine/Transient/") + ChildName));
        UBlueprint* ChildBlueprint = FKismetEditorUtilities::CreateBlueprint(
            GeneratedClass,
            ChildPackage,
            FName(*ChildName),
            BPTYPE_Normal,
            UBlueprint::StaticClass(),
            UBlueprintGeneratedClass::StaticClass(),
            FName(TEXT("UnifiedScriptBlueprintEventOverride")));
        UEdGraph* EventGraph = ChildBlueprint != nullptr &&
                !ChildBlueprint->UbergraphPages.IsEmpty()
            ? ChildBlueprint->UbergraphPages[0]
            : nullptr;
        int32 NodePositionY = 0;
        UK2Node_Event* OverrideEvent = EventGraph != nullptr
            ? FKismetEditorUtilities::AddDefaultEventNode(
                ChildBlueprint,
                EventGraph,
                ActivateInternalName,
                GeneratedClass,
                NodePositionY)
            : nullptr;
        UK2Node_CallParentFunction* ParentCall = nullptr;
        if (OverrideEvent != nullptr)
        {
            FGraphNodeCreator<UK2Node_CallParentFunction> Creator(*EventGraph);
            ParentCall = Creator.CreateNode();
            ParentCall->SetFromFunction(ActivateFunction);
            ParentCall->AllocateDefaultPins();
            Creator.Finalize();
            const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
            UEdGraphPin* EventThen = OverrideEvent->FindPin(
                UEdGraphSchema_K2::PN_Then,
                EGPD_Output);
            UEdGraphPin* ParentExec = ParentCall->GetExecPin();
            UEdGraphPin* EventAmount = OverrideEvent->FindPin(
                TEXT("amount"),
                EGPD_Output);
            UEdGraphPin* ParentAmount = ParentCall->FindPin(
                TEXT("amount"),
                EGPD_Input);
            TestTrue(TEXT("Explicit Super exec path connects"),
                EventThen != nullptr && ParentExec != nullptr &&
                Schema->TryCreateConnection(EventThen, ParentExec));
            TestTrue(TEXT("Explicit Super parameter path connects"),
                EventAmount != nullptr && ParentAmount != nullptr &&
                Schema->TryCreateConnection(EventAmount, ParentAmount));
        }
        TestNotNull(TEXT("Child Blueprint creates the managed event override"),
            OverrideEvent);
        TestNotNull(TEXT("Child override creates an explicit Call Parent Function"),
            ParentCall);
        if (ChildBlueprint != nullptr && OverrideEvent != nullptr && ParentCall != nullptr)
        {
            FKismetEditorUtilities::CompileBlueprint(ChildBlueprint);
            TestEqual(TEXT("Child Blueprint event override compiles"),
                ChildBlueprint->Status, BS_UpToDate);
        }
    }
    const FBoolProperty* Enabled = FindFProperty<FBoolProperty>(GeneratedClass, TEXT("Enabled"));
    const FByteProperty* ByteValue = FindFProperty<FByteProperty>(GeneratedClass, TEXT("ByteValue"));
    const FIntProperty* Count = FindFProperty<FIntProperty>(GeneratedClass, TEXT("Count"));
    const FInt64Property* Large = FindFProperty<FInt64Property>(GeneratedClass, TEXT("Large"));
    const FFloatProperty* Ratio = FindFProperty<FFloatProperty>(GeneratedClass, TEXT("Ratio"));
    const FDoubleProperty* Precision = FindFProperty<FDoubleProperty>(GeneratedClass, TEXT("Precision"));
    const FStrProperty* Label = FindFProperty<FStrProperty>(GeneratedClass, TEXT("Label"));
    const FNameProperty* RowName = FindFProperty<FNameProperty>(GeneratedClass, TEXT("RowName"));
    const FTextProperty* DisplayText = FindFProperty<FTextProperty>(GeneratedClass, TEXT("DisplayText"));
    const FObjectPropertyBase* Target =
        FindFProperty<FObjectPropertyBase>(GeneratedClass, TEXT("Target"));
    const FClassProperty* ActorClass =
        FindFProperty<FClassProperty>(GeneratedClass, TEXT("ActorClass"));
    const FStructProperty* Offset =
        FindFProperty<FStructProperty>(GeneratedClass, TEXT("Offset"));
    TestNotNull(TEXT("bool becomes FBoolProperty"), Enabled);
    TestNotNull(TEXT("byte becomes FByteProperty"), ByteValue);
    TestNotNull(TEXT("int becomes FIntProperty"), Count);
    TestNotNull(TEXT("long becomes FInt64Property"), Large);
    TestNotNull(TEXT("float becomes FFloatProperty"), Ratio);
    TestNotNull(TEXT("double becomes FDoubleProperty"), Precision);
    TestNotNull(TEXT("string becomes FStrProperty"), Label);
    TestNotNull(TEXT("UnrealName becomes FNameProperty"), RowName);
    TestNotNull(TEXT("UnrealText becomes FTextProperty"), DisplayText);
    TestNotNull(TEXT("UnrealObjectHandle becomes FObjectProperty"), Target);
    TestNotNull(TEXT("UnrealClassHandle becomes FClassProperty"), ActorClass);
    TestTrue(TEXT("UnrealVector becomes FVector property"),
        Offset != nullptr && Offset->Struct == TBaseStructure<FVector>::Get());
    if (Count == nullptr || Label == nullptr || RowName == nullptr ||
        DisplayText == nullptr || Target == nullptr || ActorClass == nullptr ||
        Offset == nullptr)
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
        TEXT("FName default reaches the generated CDO"),
        RowName->GetPropertyValue_InContainer(GeneratedClass->GetDefaultObject()),
        FName(TEXT("Player")));
    TestEqual(
        TEXT("FText default reaches the generated CDO"),
        DisplayText->GetPropertyValue_InContainer(GeneratedClass->GetDefaultObject()).ToString(),
        FString(TEXT("Loaded")));
    TestNull(
        TEXT("UObject handle default reaches the generated CDO as null"),
        Target->GetObjectPropertyValue_InContainer(GeneratedClass->GetDefaultObject()));
    TestNull(
        TEXT("UClass handle default reaches the generated CDO as null"),
        ActorClass->GetObjectPropertyValue_InContainer(GeneratedClass->GetDefaultObject()));
    TestEqual(
        TEXT("FVector default reaches the generated CDO"),
        *Offset->ContainerPtrToValuePtr<FVector>(GeneratedClass->GetDefaultObject()),
        FVector(1.5, 2.5, 3.5));
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
    ManagedGeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
    TestTrue(TEXT("Direct Blueprint compile restores managed native dispatch"),
        ManagedGeneratedClass != nullptr &&
            ManagedGeneratedClass->IsManagedFunctionBound(MixInternalName));
    UObject* Cdo = Blueprint->GeneratedClass->GetDefaultObject();
    Cdo->Modify();
    Count->SetPropertyValue_InContainer(Cdo, 77);

    const bool bReloaded = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *Blueprint,
        ActorDocument(ReloadedStates(), ReloadedFunctions()),
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
    TestEqual(TEXT("Compatible reload retains lifecycle identities without churn"),
        Result.UpdatedLifecycleHookCount, 0);
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
        const FUnifiedScriptManagedFunctionBinding* ReloadedEvent =
            ManagedGeneratedClass->ManagedFunctionBindings.Find(ActivateInternalName);
        TestTrue(TEXT("Compatible reload retains the Blueprint event binding"),
            ReloadedEvent != nullptr && ReloadedEvent->bBlueprintEvent);
        TestEqual(TEXT("Compatible reload retains all lifecycle bindings"),
            ManagedGeneratedClass->ManagedLifecycleBindings.Num(), 3);
    }

    const bool bNoChange = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *Blueprint,
        ActorDocument(ReloadedStates(), ReloadedFunctions()),
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
            ActorDocument(TypeChange, ReloadedFunctions()),
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
            ActorDocument(Collision, ReloadedFunctions()),
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
            ActorDocument(Unsupported, ReloadedFunctions()),
            false,
            Result,
            Error));
    TestTrue(TEXT("Unsupported-type rejection lists reviewed types"), Error.Contains(TEXT("Reviewed types")));
    TestEqual(TEXT("Rejected plans leave eleven generated plus one user property"), Blueprint->NewVariables.Num(), 12);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptCollectionBlueprintStateMaterializationTest,
    "UnifiedScript.Unreal.ScriptClass.CollectionStateMaterialization",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptCollectionBlueprintStateMaterializationTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::BlueprintClass::Tests;

    const FString ArrayType = TEXT(R"JSON({
      "kind":"array", "unrealTypePath":"", "unrealNativeName":"TArray",
      "fields":[], "typeArguments":[{"typeMetadataName":"System.Int32"}]
    })JSON");
    const FString SetType = TEXT(R"JSON({
      "kind":"set", "unrealTypePath":"", "unrealNativeName":"TSet",
      "fields":[], "typeArguments":[{"typeMetadataName":"System.Int32"}]
    })JSON");
    const FString NameSetType = TEXT(R"JSON({
      "kind":"set", "unrealTypePath":"", "unrealNativeName":"TSet",
      "fields":[], "typeArguments":[{"typeMetadataName":"UnifiedScript.Unreal.UnrealName"}]
    })JSON");
    const FString MapType = TEXT(R"JSON({
      "kind":"map", "unrealTypePath":"", "unrealNativeName":"TMap",
      "fields":[], "typeArguments":[
        {"typeMetadataName":"System.Int32"},
        {"typeMetadataName":"System.String"}
      ]
    })JSON");
    const FString ObjectMapType = TEXT(R"JSON({
      "kind":"map", "unrealTypePath":"", "unrealNativeName":"TMap",
      "fields":[], "typeArguments":[
        {"typeMetadataName":"UnifiedScript.Unreal.UnrealObjectHandle"},
        {"typeMetadataName":"System.Int32"}
      ]
    })JSON");

    const FString NumbersId = TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1");
    const FString NamesId = TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2");
    const FString MappingId = TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa3");
    const FString ObjectRefsId = TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa4");
    const auto MakeDocument = [&](bool bNumbersSet, bool bReordered, bool bRenameObjectRefs = false)
    {
        const FString NumbersMetadata = bNumbersSet
            ? TEXT("System.Collections.Generic.IReadOnlySet<System.Int32>")
            : TEXT("System.Collections.Generic.IReadOnlyList<System.Int32>");
        const FString NumbersType = bNumbersSet ? SetType : ArrayType;
        const FString NumbersKind = bNumbersSet ? TEXT("set") : TEXT("array");
        const FString NamesDefault = bReordered ? TEXT("[\"Beta\",\"Alpha\"]") : TEXT("[\"Alpha\",\"Beta\"]");
        const FString MapDefault = bReordered
            ? TEXT("[{\"key\":2,\"value\":\"\"},{\"key\":1,\"value\":\"one,two\"}]")
            : TEXT("[{\"key\":1,\"value\":\"one,two\"},{\"key\":2,\"value\":\"\"}]");
        const TCHAR* ObjectRefsName = bRenameObjectRefs ? TEXT("ObjectRefsRenamed") : TEXT("ObjectRefs");
        const FString States = FString::Join(
            TArray<FString>{
                State(*NumbersId, TEXT("Numbers"), *NumbersMetadata, *NumbersMetadata,
                    *NumbersKind, TEXT("[1,2]"), true, false, false, TEXT("Collections"), NumbersType),
                State(
                    *NamesId,
                    TEXT("Names"),
                    TEXT("IReadOnlySet<UnrealName>"),
                    TEXT("System.Collections.Generic.IReadOnlySet<UnifiedScript.Unreal.UnrealName>"),
                    TEXT("set"),
                    *NamesDefault,
                    true, false, false, TEXT("Collections"), NameSetType),
                State(
                    *MappingId,
                    TEXT("Mapping"),
                    TEXT("IReadOnlyDictionary<int, string>"),
                    TEXT("System.Collections.Generic.IReadOnlyDictionary<System.Int32, System.String>"),
                    TEXT("map"),
                    *MapDefault,
                    true, false, false, TEXT("Collections"), MapType),
                State(
                    *ObjectRefsId,
                    ObjectRefsName,
                    TEXT("IReadOnlyDictionary<UnrealObjectHandle, int>"),
                    TEXT("System.Collections.Generic.IReadOnlyDictionary<UnifiedScript.Unreal.UnrealObjectHandle, System.Int32>"),
                    TEXT("map"),
                    TEXT("[]"),
                    true, false, false, TEXT("Collections"), ObjectMapType),
            },
            TEXT(","));
        return Document(
            States,
            FString(),
            TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            TEXT("Tests.CollectionStateActor"),
            TEXT("Tests/CollectionStateActor"));
    };

    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString AssetName = TEXT("BP_UnifiedScriptCollectionState_") + Suffix;
    UPackage* Package = CreatePackage(*(TEXT("/Engine/Transient/") + AssetName));
    UUnifiedScriptBlueprint* Blueprint = Cast<UUnifiedScriptBlueprint>(
        FKismetEditorUtilities::CreateBlueprint(
            AActor::StaticClass(),
            Package,
            FName(*AssetName),
            BPTYPE_Normal,
            UUnifiedScriptBlueprint::StaticClass(),
            UUnifiedScriptBlueprintGeneratedClass::StaticClass(),
            FName(TEXT("UnifiedScriptCollectionStateMaterialization"))));
    if (!TestNotNull(TEXT("Creates a collection state Blueprint"), Blueprint))
    {
        return false;
    }
    UUnifiedScriptAsset* ScriptAsset = NewObject<UUnifiedScriptAsset>(Package);
    ScriptAsset->ScriptClassId = Guid(TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    ScriptAsset->ScriptTypeName = TEXT("Tests.CollectionStateActor");
    ScriptAsset->DeclarationAssetPath = TEXT("Tests/CollectionStateActor");
    Blueprint->ScriptAsset = ScriptAsset;

    FUnifiedScriptBlueprintClassSyncResult Result;
    FString Error;
    const auto Synchronize = [&](bool bNumbersSet, bool bReordered, bool bValidateOnly, bool bRenameObjectRefs = false)
    {
        Error.Reset();
        return FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset, *Blueprint, MakeDocument(bNumbersSet, bReordered, bRenameObjectRefs),
            bValidateOnly, Result, Error);
    };

    const bool bInitial = Synchronize(false, false, false);
    TestTrue(*FString::Printf(TEXT("Initial array/set/map state materializes: %s"), *Error), bInitial);
    if (Blueprint->GeneratedClass == nullptr)
    {
        return false;
    }
    const FArrayProperty* NumbersArray = FindFProperty<FArrayProperty>(Blueprint->GeneratedClass, TEXT("Numbers"));
    const FSetProperty* NamesSet = FindFProperty<FSetProperty>(Blueprint->GeneratedClass, TEXT("Names"));
    const FMapProperty* Mapping = FindFProperty<FMapProperty>(Blueprint->GeneratedClass, TEXT("Mapping"));
    TestNotNull(TEXT("IReadOnlyList state becomes an array property"), NumbersArray);
    TestNotNull(TEXT("IReadOnlySet state becomes a set property"), NamesSet);
    TestNotNull(TEXT("IReadOnlyDictionary state becomes a map property"), Mapping);
    if (NumbersArray == nullptr || NamesSet == nullptr || Mapping == nullptr)
    {
        return false;
    }
    TestEqual(TEXT("Initial array default has two entries"),
        FScriptArrayHelper(NumbersArray, NumbersArray->ContainerPtrToValuePtr<void>(
            Blueprint->GeneratedClass->GetDefaultObject())).Num(), 2);
    TestEqual(TEXT("Initial set default has two entries"),
        FScriptSetHelper(NamesSet, NamesSet->ContainerPtrToValuePtr<void>(
            Blueprint->GeneratedClass->GetDefaultObject())).Num(), 2);
    TestEqual(TEXT("Initial map default has two entries"),
        FScriptMapHelper(Mapping, Mapping->ContainerPtrToValuePtr<void>(
            Blueprint->GeneratedClass->GetDefaultObject())).Num(), 2);
    {
        const FStrProperty* MapValueProperty = CastField<FStrProperty>(Mapping->ValueProp);
        FScriptMapHelper MapHelper(
            Mapping,
            Mapping->ContainerPtrToValuePtr<void>(Blueprint->GeneratedClass->GetDefaultObject()));
        TSet<FString> MapValues;
        for (int32 PairIndex = 0; PairIndex < MapHelper.GetMaxIndex(); ++PairIndex)
        {
            if (MapHelper.IsValidIndex(PairIndex) && MapValueProperty != nullptr)
            {
                MapValues.Add(MapValueProperty->GetPropertyValue(MapHelper.GetValuePtr(PairIndex)));
            }
        }
        TestTrue(TEXT("Map default preserves punctuation and an empty string"),
            MapValues.Contains(TEXT("one,two")) && MapValues.Contains(TEXT("")));
    }

    TestTrue(TEXT("Reordered Set/Map defaults are native-semantic no-override changes"),
        Synchronize(false, true, false));
    TestEqual(TEXT("Reordering does not preserve a false override"),
        Result.PreservedDefaultOverrideCount, 0);
    TestFalse(TEXT("Reordering does not report a collection property update"), Result.bChanged);
    TestEqual(TEXT("Reordering updates no collection property metadata"),
        Result.UpdatedPropertyCount, 0);

    TestTrue(TEXT("An unoverridden collection type change is accepted"),
        Synchronize(true, true, false));
    const FSetProperty* NumbersSet = FindFProperty<FSetProperty>(Blueprint->GeneratedClass, TEXT("Numbers"));
    TestNotNull(TEXT("Collection type change produces the new set property"), NumbersSet);
    if (NumbersSet == nullptr)
    {
        return false;
    }
    TestEqual(TEXT("Collection type change applies the new default"),
        FScriptSetHelper(NumbersSet, NumbersSet->ContainerPtrToValuePtr<void>(
            Blueprint->GeneratedClass->GetDefaultObject())).Num(), 2);

    UObject* Cdo = Blueprint->GeneratedClass->GetDefaultObject();
    const TCHAR* NumbersEnd = NumbersSet->ImportText_Direct(
        TEXT("(1,2,3)"), NumbersSet->ContainerPtrToValuePtr<void>(Cdo), Cdo, PPF_None, nullptr);
    TestNotNull(TEXT("Can establish a real Set CDO override"), NumbersEnd);
    if (NumbersEnd == nullptr)
    {
        return false;
    }
    // A collection-to-scalar declaration change must be rejected before the
    // Blueprint is recompiled when an existing instance owns a collection
    // override.  Keep the CDO at its declaration default so BuildPlan does
    // not mask the instance-preservation preflight with a CDO override error.
    NumbersEnd = NumbersSet->ImportText_Direct(
        TEXT("(1,2)"), NumbersSet->ContainerPtrToValuePtr<void>(Cdo), Cdo, PPF_None, nullptr);
    TestNotNull(TEXT("Restores the Set CDO declaration default before instance check"), NumbersEnd);
    AActor* CollectionOverrideInstance = NewObject<AActor>(GetTransientPackage(), Blueprint->GeneratedClass);
    TestNotNull(TEXT("Creates an instance with a collection override"), CollectionOverrideInstance);
    if (CollectionOverrideInstance == nullptr)
    {
        return false;
    }
    CollectionOverrideInstance->AddToRoot();
    TWeakObjectPtr<UObject> CollectionOverrideInstanceWeak(CollectionOverrideInstance);
    ON_SCOPE_EXIT
    {
        if (UObject* LiveInstance = CollectionOverrideInstanceWeak.Get())
        {
            LiveInstance->RemoveFromRoot();
        }
    };
    NumbersEnd = NumbersSet->ImportText_Direct(
        TEXT("(1,2,3)"),
        NumbersSet->ContainerPtrToValuePtr<void>(CollectionOverrideInstance),
        CollectionOverrideInstance,
        PPF_None,
        nullptr);
    TestNotNull(TEXT("Sets the live collection override"), NumbersEnd);
    const FString ScalarNumbersState = State(
        *NumbersId,
        TEXT("Numbers"),
        TEXT("int"),
        TEXT("System.Int32"),
        TEXT("integer"),
        TEXT("7"),
        true,
        false,
        false,
        TEXT("Collections"));
    const FString ScalarDocument = Document(
        FString::Join(
            TArray<FString>{
                ScalarNumbersState,
                State(
                    *NamesId,
                    TEXT("Names"),
                    TEXT("IReadOnlySet<UnrealName>"),
                    TEXT("System.Collections.Generic.IReadOnlySet<UnifiedScript.Unreal.UnrealName>"),
                    TEXT("set"),
                    TEXT("[\"Beta\",\"Alpha\"]"),
                    true, false, false, TEXT("Collections"), NameSetType),
                State(
                    *MappingId,
                    TEXT("Mapping"),
                    TEXT("IReadOnlyDictionary<int, string>"),
                    TEXT("System.Collections.Generic.IReadOnlyDictionary<System.Int32, System.String>"),
                    TEXT("map"),
                    TEXT("[{\"key\":2,\"value\":\"\"},{\"key\":1,\"value\":\"one,two\"}]"),
                    true, false, false, TEXT("Collections"), MapType)},
            TEXT(",")),
        FString(),
        TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        TEXT("Tests.CollectionStateActor"),
        TEXT("Tests/CollectionStateActor"));
    TestFalse(TEXT("Collection-to-scalar change with a live collection override is rejected"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset, *Blueprint, ScalarDocument, false, Result, Error));
    TestTrue(TEXT("Collection-to-scalar rejection identifies the incompatible instance override"),
        Error.Contains(TEXT("instance override")));
    TestNotNull(TEXT("Rejected collection-to-scalar change keeps the old Set property"),
        FindFProperty<FSetProperty>(Blueprint->GeneratedClass, TEXT("Numbers")));

    // Establish a CDO override as well; this remains the existing CDO-level
    // type-change regression below.
    Cdo = Blueprint->GeneratedClass->GetDefaultObject();
    NumbersSet = FindFProperty<FSetProperty>(Blueprint->GeneratedClass, TEXT("Numbers"));
    NumbersEnd = NumbersSet != nullptr
        ? NumbersSet->ImportText_Direct(
            TEXT("(1,2,3)"), NumbersSet->ContainerPtrToValuePtr<void>(Cdo), Cdo, PPF_None, nullptr)
        : nullptr;
    TestFalse(TEXT("A type change with a real collection override is rejected"),
        Synchronize(false, true, false));
    TestTrue(TEXT("Collection override type-change rejection is actionable"), Error.Contains(TEXT("overridden")));
    const FSetProperty* RejectedNumbersSet =
        FindFProperty<FSetProperty>(Blueprint->GeneratedClass, TEXT("Numbers"));
    TestNotNull(TEXT("Rejected type change leaves the Set property intact"), RejectedNumbersSet);
    if (RejectedNumbersSet != nullptr)
    {
        const FIntProperty* ElementProperty = CastField<FIntProperty>(RejectedNumbersSet->ElementProp);
        FScriptSetHelper SetHelper(
            RejectedNumbersSet,
            RejectedNumbersSet->ContainerPtrToValuePtr<void>(
                Blueprint->GeneratedClass->GetDefaultObject()));
        TSet<int32> Values;
        for (int32 Index = 0; Index < SetHelper.GetMaxIndex(); ++Index)
        {
            if (SetHelper.IsValidIndex(Index) && ElementProperty != nullptr)
            {
                Values.Add(ElementProperty->GetPropertyValue(SetHelper.GetElementPtr(Index)));
            }
        }
        TestTrue(TEXT("Rejected type change preserves the overridden Set CDO values"),
            Values.Contains(1) && Values.Contains(2) && Values.Contains(3));
    }

    // Exercise archive replacement for an object reference used as a map key.
    // The referenced value is another live generated-class instance, so a
    // successful reinstance must replace both the owner and key, then rehash
    // the restored map before the key can be found.
    const FMapProperty* ObjectRefs = FindFProperty<FMapProperty>(
        Blueprint->GeneratedClass, TEXT("ObjectRefs"));
    TestNotNull(TEXT("Object reference collection property exists"), ObjectRefs);
    if (ObjectRefs == nullptr)
    {
        return false;
    }
    FObjectPropertyBase* ObjectKey = CastField<FObjectPropertyBase>(ObjectRefs->KeyProp);
    const FIntProperty* ObjectValue = CastField<FIntProperty>(ObjectRefs->ValueProp);
    TestNotNull(TEXT("Object reference collection has an object key"), ObjectKey);
    TestNotNull(TEXT("Object reference collection has an integer value"), ObjectValue);
    if (ObjectKey == nullptr || ObjectValue == nullptr)
    {
        return false;
    }
    UWorld* ObjectWorld = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Creates a world for object-reference reinstancing"), ObjectWorld);
    if (ObjectWorld == nullptr || GEngine == nullptr)
    {
        return false;
    }
    ObjectWorld->AddToRoot();
    FWorldContext& ObjectWorldContext = GEngine->CreateNewWorldContext(EWorldType::Game);
    ObjectWorldContext.SetCurrentWorld(ObjectWorld);
    ObjectWorld->InitializeActorsForPlay(FURL());
    ON_SCOPE_EXIT
    {
        if (ObjectWorld != nullptr)
        {
            ObjectWorld->RemoveFromRoot();
            GEngine->DestroyWorldContext(ObjectWorld);
            ObjectWorld->DestroyWorld(false);
        }
    };
    AActor* ObjectCollectionInstance = ObjectWorld->SpawnActor<AActor>(Blueprint->GeneratedClass);
    AActor* ObjectReferenceTarget = ObjectWorld->SpawnActor<AActor>(Blueprint->GeneratedClass);
    TestNotNull(TEXT("Creates an object-reference collection owner"), ObjectCollectionInstance);
    TestNotNull(TEXT("Creates an object-reference collection target"), ObjectReferenceTarget);
    if (ObjectCollectionInstance == nullptr || ObjectReferenceTarget == nullptr)
    {
        return false;
    }
    FScriptMapHelper ObjectRefsHelper(
        ObjectRefs,
        ObjectRefs->ContainerPtrToValuePtr<void>(ObjectCollectionInstance));
    ObjectRefsHelper.EmptyValues();
    const int32 ObjectReferenceIndex = ObjectRefsHelper.AddDefaultValue_Invalid_NeedsRehash();
    ObjectKey->SetObjectPropertyValue(
        ObjectRefsHelper.GetKeyPtr(ObjectReferenceIndex), ObjectReferenceTarget);
    ObjectValue->SetPropertyValue(ObjectRefsHelper.GetValuePtr(ObjectReferenceIndex), 42);
    ObjectRefsHelper.Rehash();

    TMap<UObject*, TWeakObjectPtr<UObject>> TestReplacedObjects;
    const FDelegateHandle TestReplacedObjectsHandle = FCoreUObjectDelegates::OnObjectsReplaced.AddLambda(
        [&TestReplacedObjects](const FCoreUObjectDelegates::FReplacementObjectMap& ReplacementMap)
        {
            for (const TPair<UObject*, UObject*>& Pair : ReplacementMap)
            {
                TestReplacedObjects.Add(Pair.Key, TWeakObjectPtr<UObject>(Pair.Value));
            }
        });
    const bool bObjectRename = Synchronize(true, true, false, true);
    FCoreUObjectDelegates::OnObjectsReplaced.Remove(TestReplacedObjectsHandle);
    TestTrue(*FString::Printf(
        TEXT("Object reference collection rename/reinstance succeeds: %s"), *Error), bObjectRename);
    const FMapProperty* RenamedObjectRefs = FindFProperty<FMapProperty>(
        Blueprint->GeneratedClass, TEXT("ObjectRefsRenamed"));
    TestNotNull(TEXT("Object reference collection keeps its renamed property"), RenamedObjectRefs);
    const auto ResolveTestReplacement = [&TestReplacedObjects](UObject* OldObject) -> UObject*
    {
        TSet<UObject*> Visited;
        UObject* Current = OldObject;
        while (Current != nullptr && !Visited.Contains(Current))
        {
            Visited.Add(Current);
            const TWeakObjectPtr<UObject>* Next = TestReplacedObjects.Find(Current);
            if (Next == nullptr)
            {
                return Current;
            }
            Current = Next->Get();
        }
        return nullptr;
    };
    UObject* NewOwner = ResolveTestReplacement(ObjectCollectionInstance);
    UObject* NewTarget = ResolveTestReplacement(ObjectReferenceTarget);
    TestNotNull(TEXT("Replacement map identifies the exact new object-reference owner"), NewOwner);
    TestNotNull(TEXT("Replacement map identifies the exact new map-key target"), NewTarget);
    bool bReplacedCollectionKey = false;
    bool bOldCollectionKeyRemains = false;
    if (RenamedObjectRefs != nullptr && NewOwner != nullptr && NewTarget != nullptr)
    {
        FObjectPropertyBase* RenamedKey = CastField<FObjectPropertyBase>(RenamedObjectRefs->KeyProp);
        const FIntProperty* RenamedValue = CastField<FIntProperty>(RenamedObjectRefs->ValueProp);
        FScriptMapHelper RenamedRefs(
            RenamedObjectRefs,
            RenamedObjectRefs->ContainerPtrToValuePtr<void>(NewOwner));
        if (RenamedKey != nullptr && RenamedValue != nullptr)
        {
            uint8* ValueForNewTarget = RenamedRefs.FindValueFromHash(&NewTarget);
            if (ValueForNewTarget != nullptr &&
                RenamedValue->GetPropertyValue(ValueForNewTarget) == 42)
            {
                bReplacedCollectionKey = true;
            }
            for (int32 PairIndex = 0; PairIndex < RenamedRefs.GetMaxIndex(); ++PairIndex)
            {
                if (RenamedRefs.IsValidIndex(PairIndex) &&
                    RenamedKey->GetObjectPropertyValue(RenamedRefs.GetKeyPtr(PairIndex)) == ObjectReferenceTarget)
                {
                    bOldCollectionKeyRemains = true;
                    break;
                }
            }
        }
    }
    TestTrue(TEXT("Object reference collection key follows its reinstanced object and retains its value"),
        bReplacedCollectionKey);
    TestFalse(TEXT("Object reference collection no longer contains the retired target key"),
        bOldCollectionKeyRemains);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptInheritedCollectionDefaultsTest,
    "UnifiedScript.Unreal.ScriptClass.InheritedCollectionDefaults",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptInheritedCollectionDefaultsTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::BlueprintClass::Tests;

    const FString ArrayType = TEXT(R"JSON({
      "kind":"array", "unrealTypePath":"", "unrealNativeName":"TArray",
      "fields":[], "typeArguments":[{"typeMetadataName":"System.Int32"}]
    })JSON");
    const FString SetType = TEXT(R"JSON({
      "kind":"set", "unrealTypePath":"", "unrealNativeName":"TSet",
      "fields":[], "typeArguments":[{"typeMetadataName":"System.Int32"}]
    })JSON");
    const FString MapType = TEXT(R"JSON({
      "kind":"map", "unrealTypePath":"", "unrealNativeName":"TMap",
      "fields":[], "typeArguments":[
        {"typeMetadataName":"System.Int32"},
        {"typeMetadataName":"System.Int32"}
      ]
    })JSON");
    const FString ArrayId = TEXT("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1");
    const FString SetId = TEXT("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb2");
    const FString MapId = TEXT("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb3");
    const auto MakeDocument = [&](bool bArrayAsSet,
                                  const TCHAR* ArrayName,
                                  const TCHAR* SetName,
                                  const TCHAR* MapName,
                                  const TCHAR* ArrayDefault,
                                  const TCHAR* SetDefault,
                                  const TCHAR* MapDefault)
    {
        const FString ArrayMetadata = bArrayAsSet
            ? TEXT("System.Collections.Generic.IReadOnlySet<System.Int32>")
            : TEXT("System.Collections.Generic.IReadOnlyList<System.Int32>");
        const FString ArrayKind = bArrayAsSet ? TEXT("set") : TEXT("array");
        const FString& ArrayShape = bArrayAsSet ? SetType : ArrayType;
        return Document(
            FString::Join(
                TArray<FString>{
                    State(*ArrayId, ArrayName, *ArrayMetadata, *ArrayMetadata,
                        *ArrayKind, ArrayDefault, true, false, false, TEXT("Collections"), ArrayShape),
                    State(*SetId, SetName,
                        TEXT("System.Collections.Generic.IReadOnlySet<System.Int32>"),
                        TEXT("System.Collections.Generic.IReadOnlySet<System.Int32>"),
                        TEXT("set"), SetDefault, true, false, false, TEXT("Collections"), SetType),
                    State(*MapId, MapName,
                        TEXT("System.Collections.Generic.IReadOnlyDictionary<System.Int32, System.Int32>"),
                        TEXT("System.Collections.Generic.IReadOnlyDictionary<System.Int32, System.Int32>"),
                        TEXT("map"), MapDefault, true, false, false, TEXT("Collections"), MapType),
                },
                TEXT(",")),
            FString(),
            TEXT("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            TEXT("Tests.InheritedCollectionActor"),
            TEXT("Tests/InheritedCollectionActor"));
    };

    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString BaseName = TEXT("BP_UnifiedScriptInheritedCollections_") + Suffix;
    const FString ChildName = BaseName + TEXT("_Child");
    const FString SiblingName = BaseName + TEXT("_Sibling");
    const FString GrandchildName = BaseName + TEXT("_Grandchild");
    const FString BasePackageName = TEXT("/Engine/Transient/") + BaseName;
    const FString ChildPackageName = TEXT("/Engine/Transient/") + ChildName;
    const FString SiblingPackageName = TEXT("/Engine/Transient/") + SiblingName;
    const FString GrandchildPackageName = TEXT("/Engine/Transient/") + GrandchildName;
    UPackage* BasePackage = CreatePackage(*BasePackageName);
    UPackage* ChildPackage = nullptr;
    UPackage* SiblingPackage = nullptr;
    UPackage* GrandchildPackage = nullptr;
    UUnifiedScriptBlueprint* BaseBlueprint = Cast<UUnifiedScriptBlueprint>(
        FKismetEditorUtilities::CreateBlueprint(
            AActor::StaticClass(),
            BasePackage,
            FName(*BaseName),
            BPTYPE_Normal,
            UUnifiedScriptBlueprint::StaticClass(),
            UUnifiedScriptBlueprintGeneratedClass::StaticClass(),
            FName(TEXT("UnifiedScriptInheritedCollectionDefaults"))));
    if (!TestNotNull(TEXT("Creates the generated collection base Blueprint"), BaseBlueprint))
    {
        return false;
    }
    UUnifiedScriptAsset* ScriptAsset = NewObject<UUnifiedScriptAsset>(BasePackage);
    ScriptAsset->ScriptClassId = Guid(TEXT("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
    ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(BaseBlueprint->GeneratedClass);
    ScriptAsset->ScriptTypeName = TEXT("Tests.InheritedCollectionActor");
    ScriptAsset->DeclarationAssetPath = TEXT("Tests/InheritedCollectionActor");
    BaseBlueprint->ScriptAsset = ScriptAsset;
    TStrongObjectPtr<UUnifiedScriptBlueprint> BaseBlueprintRoot(BaseBlueprint);
    TStrongObjectPtr<UUnifiedScriptAsset> ScriptAssetRoot(ScriptAsset);

    UBlueprint* ChildBlueprint = nullptr;
    UBlueprint* SiblingBlueprint = nullptr;
    UBlueprint* GrandchildBlueprint = nullptr;
    UWorld* FirstWorld = nullptr;
    UWorld* SecondWorld = nullptr;
    AActor* FirstChild = nullptr;
    AActor* SecondChild = nullptr;
    AActor* FirstSibling = nullptr;
    AActor* NewSibling = nullptr;
    AActor* FirstGrandchild = nullptr;
    AActor* SecondGrandchild = nullptr;
    ON_SCOPE_EXIT
    {
        // Reinstancing and world teardown can release actor UObjects before
        // the package cleanup runs.  Drop all raw actor references first and
        // look packages up by their stable names below.
        FirstChild = nullptr;
        SecondChild = nullptr;
        FirstSibling = nullptr;
        NewSibling = nullptr;
        FirstGrandchild = nullptr;
        SecondGrandchild = nullptr;
        if (GEngine != nullptr)
        {
            if (FirstWorld != nullptr)
            {
                GEngine->DestroyWorldContext(FirstWorld);
            }
            if (SecondWorld != nullptr)
            {
                GEngine->DestroyWorldContext(SecondWorld);
            }
        }
        if (FirstWorld != nullptr)
        {
            FirstWorld->RemoveFromRoot();
            FirstWorld->DestroyWorld(false);
            FirstWorld = nullptr;
        }
        if (SecondWorld != nullptr)
        {
            SecondWorld->RemoveFromRoot();
            SecondWorld->DestroyWorld(false);
            SecondWorld = nullptr;
        }
        BaseBlueprintRoot.Reset();
        ScriptAssetRoot.Reset();
        TArray<UPackage*> PackagesToUnload;
        for (const FString& PackageName : {BasePackageName, ChildPackageName, SiblingPackageName, GrandchildPackageName})
        {
            if (UPackage* Package = FindPackage(nullptr, *PackageName))
            {
                Package->SetDirtyFlag(false);
                PackagesToUnload.Add(Package);
            }
        }
        UPackageTools::UnloadPackages(PackagesToUnload);
    };

    FUnifiedScriptBlueprintClassSyncResult Result;
    FString Error;
    const FString InitialDocument = MakeDocument(
        false, TEXT("Numbers"), TEXT("NumbersSet"), TEXT("Mapping"),
        TEXT("[1,2]"), TEXT("[3,4]"), TEXT("[{\"key\":1,\"value\":2}]"));
    TestTrue(TEXT("Initial collection defaults materialize"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset, *BaseBlueprint, InitialDocument, false, Result, Error));
    if (BaseBlueprint->GeneratedClass == nullptr)
    {
        return false;
    }

    // The parent is fully materialized before ordinary child Blueprints are
    // created.  Package lifetimes are kept by the Blueprint outers and the
    // cleanup resolves them by name, so this also covers early failures.
    ChildPackage = CreatePackage(*ChildPackageName);
    ChildBlueprint = FKismetEditorUtilities::CreateBlueprint(
        BaseBlueprint->GeneratedClass,
        ChildPackage,
        FName(*ChildName),
        BPTYPE_Normal,
        UBlueprint::StaticClass(),
        UBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("UnifiedScriptInheritedCollectionDefaultsChild")));
    TestNotNull(TEXT("Creates an ordinary Blueprint child"), ChildBlueprint);
    if (ChildBlueprint == nullptr)
    {
        return false;
    }
    SiblingPackage = CreatePackage(*SiblingPackageName);
    SiblingBlueprint = FKismetEditorUtilities::CreateBlueprint(
        BaseBlueprint->GeneratedClass,
        SiblingPackage,
        FName(*SiblingName),
        BPTYPE_Normal,
        UBlueprint::StaticClass(),
        UBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("UnifiedScriptInheritedCollectionDefaultsSibling")));
    TestNotNull(TEXT("Creates an untouched ordinary Blueprint sibling"), SiblingBlueprint);
    if (SiblingBlueprint == nullptr)
    {
        return false;
    }

    FKismetEditorUtilities::CompileBlueprint(ChildBlueprint);
    FKismetEditorUtilities::CompileBlueprint(SiblingBlueprint);

    auto SetArrayValue = [](UObject* Object, const FArrayProperty* Property, const TCHAR* Text)
    {
        return Object != nullptr && Property != nullptr &&
            Property->ImportText_Direct(
                Text,
                Property->ContainerPtrToValuePtr<void>(Object),
                Object,
                PPF_None,
                nullptr) != nullptr;
    };
    auto SetSetValue = [](UObject* Object, const FSetProperty* Property, const TCHAR* Text)
    {
        return Object != nullptr && Property != nullptr &&
            Property->ImportText_Direct(
                Text,
                Property->ContainerPtrToValuePtr<void>(Object),
                Object,
                PPF_None,
                nullptr) != nullptr;
    };
    auto SetMapValue = [](UObject* Object, const FMapProperty* Property, int32 Key, int32 Value)
    {
        if (Object == nullptr || Property == nullptr)
        {
            return false;
        }
        FScriptMapHelper Helper(
            Property,
            Property->ContainerPtrToValuePtr<void>(Object));
        Helper.EmptyValues();
        const int32 PairIndex = Helper.AddDefaultValue_Invalid_NeedsRehash();
        CastField<FIntProperty>(Property->KeyProp)->SetPropertyValue(Helper.GetKeyPtr(PairIndex), Key);
        CastField<FIntProperty>(Property->ValueProp)->SetPropertyValue(Helper.GetValuePtr(PairIndex), Value);
        Helper.Rehash();
        return true;
    };
    auto ReadArray = [](UObject* Object, const FArrayProperty* Property)
    {
        TArray<int32> Values;
        if (Object == nullptr || Property == nullptr || CastField<FIntProperty>(Property->Inner) == nullptr)
        {
            return Values;
        }
        const FIntProperty* Inner = CastField<FIntProperty>(Property->Inner);
        FScriptArrayHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Object));
        for (int32 Index = 0; Index < Helper.Num(); ++Index)
        {
            Values.Add(Inner->GetPropertyValue(Helper.GetRawPtr(Index)));
        }
        return Values;
    };
    auto ReadSet = [](UObject* Object, const FSetProperty* Property)
    {
        TSet<int32> Values;
        if (Object == nullptr || Property == nullptr || CastField<FIntProperty>(Property->ElementProp) == nullptr)
        {
            return Values;
        }
        const FIntProperty* Element = CastField<FIntProperty>(Property->ElementProp);
        FScriptSetHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Object));
        for (int32 Index = 0; Index < Helper.GetMaxIndex(); ++Index)
        {
            if (Helper.IsValidIndex(Index))
            {
                Values.Add(Element->GetPropertyValue(Helper.GetElementPtr(Index)));
            }
        }
        return Values;
    };
    auto ReadMap = [](UObject* Object, const FMapProperty* Property)
    {
        TMap<int32, int32> Values;
        if (Object == nullptr || Property == nullptr ||
            CastField<FIntProperty>(Property->KeyProp) == nullptr ||
            CastField<FIntProperty>(Property->ValueProp) == nullptr)
        {
            return Values;
        }
        const FIntProperty* Key = CastField<FIntProperty>(Property->KeyProp);
        const FIntProperty* Value = CastField<FIntProperty>(Property->ValueProp);
        FScriptMapHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Object));
        for (int32 Index = 0; Index < Helper.GetMaxIndex(); ++Index)
        {
            if (Helper.IsValidIndex(Index))
            {
                Values.Add(
                    Key->GetPropertyValue(Helper.GetKeyPtr(Index)),
                    Value->GetPropertyValue(Helper.GetValuePtr(Index)));
            }
        }
        return Values;
    };
    const auto ArrayMatches = [](const TArray<int32>& Actual, std::initializer_list<int32> Expected)
    {
        if (Actual.Num() != static_cast<int32>(Expected.size()))
        {
            return false;
        }
        int32 Index = 0;
        for (const int32 Value : Expected)
        {
            if (Actual[Index++] != Value)
            {
                return false;
            }
        }
        return true;
    };

    UClass* ChildClass = ChildBlueprint->GeneratedClass;
    UClass* SiblingClass = SiblingBlueprint->GeneratedClass;
    const FArrayProperty* ChildArray = FindFProperty<FArrayProperty>(ChildClass, TEXT("Numbers"));
    const FSetProperty* ChildSet = FindFProperty<FSetProperty>(ChildClass, TEXT("NumbersSet"));
    const FMapProperty* ChildMap = FindFProperty<FMapProperty>(ChildClass, TEXT("Mapping"));
    const FArrayProperty* SiblingArray = FindFProperty<FArrayProperty>(SiblingClass, TEXT("Numbers"));
    const FSetProperty* SiblingSet = FindFProperty<FSetProperty>(SiblingClass, TEXT("NumbersSet"));
    const FMapProperty* SiblingMap = FindFProperty<FMapProperty>(SiblingClass, TEXT("Mapping"));
    TestNotNull(TEXT("Child inherits array property"), ChildArray);
    TestNotNull(TEXT("Child inherits set property"), ChildSet);
    TestNotNull(TEXT("Child inherits map property"), ChildMap);
    TestNotNull(TEXT("Sibling inherits array property"), SiblingArray);
    TestNotNull(TEXT("Sibling inherits set property"), SiblingSet);
    TestNotNull(TEXT("Sibling inherits map property"), SiblingMap);
    if (ChildArray == nullptr || ChildSet == nullptr || ChildMap == nullptr ||
        SiblingArray == nullptr || SiblingSet == nullptr || SiblingMap == nullptr)
    {
        return false;
    }

    UObject* ChildCdo = ChildClass->GetDefaultObject();
    TestTrue(TEXT("Child array whole-value override is established"), SetArrayValue(ChildCdo, ChildArray, TEXT("(20,21)")));
    TestTrue(TEXT("Child set whole-value override is established"), SetSetValue(ChildCdo, ChildSet, TEXT("(30,40)")));
    TestTrue(TEXT("Child map whole-value override is established"), SetMapValue(ChildCdo, ChildMap, 5, 6));

    GrandchildPackage = CreatePackage(*GrandchildPackageName);
    GrandchildBlueprint = FKismetEditorUtilities::CreateBlueprint(
        ChildBlueprint->GeneratedClass,
        GrandchildPackage,
        FName(*GrandchildName),
        BPTYPE_Normal,
        UBlueprint::StaticClass(),
        UBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("UnifiedScriptInheritedCollectionDefaultsGrandchild")));
    TestNotNull(TEXT("Creates an ordinary Blueprint grandchild"), GrandchildBlueprint);
    if (GrandchildBlueprint == nullptr)
    {
        return false;
    }
    FKismetEditorUtilities::CompileBlueprint(GrandchildBlueprint);
    UClass* GrandchildClass = GrandchildBlueprint->GeneratedClass;
    const FArrayProperty* GrandchildArray = FindFProperty<FArrayProperty>(GrandchildClass, TEXT("Numbers"));
    const FSetProperty* GrandchildSet = FindFProperty<FSetProperty>(GrandchildClass, TEXT("NumbersSet"));
    const FMapProperty* GrandchildMap = FindFProperty<FMapProperty>(GrandchildClass, TEXT("Mapping"));
    TestNotNull(TEXT("Grandchild inherits array property"), GrandchildArray);
    TestNotNull(TEXT("Grandchild inherits set property"), GrandchildSet);
    TestNotNull(TEXT("Grandchild inherits map property"), GrandchildMap);
    if (GrandchildArray == nullptr || GrandchildSet == nullptr || GrandchildMap == nullptr)
    {
        return false;
    }
    UObject* GrandchildCdo = GrandchildClass->GetDefaultObject();
    TestTrue(TEXT("Grandchild independent map override is established"), SetMapValue(GrandchildCdo, GrandchildMap, 9, 10));
    TestTrue(TEXT("Grandchild array initially follows child"),
        ArrayMatches(ReadArray(GrandchildCdo, GrandchildArray), {20, 21}));

    auto MakeWorld = [&]() -> UWorld*
    {
        UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
        if (World == nullptr || GEngine == nullptr)
        {
            return nullptr;
        }
        World->AddToRoot();
        FWorldContext& Context = GEngine->CreateNewWorldContext(EWorldType::Game);
        Context.SetCurrentWorld(World);
        World->InitializeActorsForPlay(FURL());
        return World;
    };
    FirstWorld = MakeWorld();
    SecondWorld = MakeWorld();
    TestNotNull(TEXT("Creates first collection inheritance world"), FirstWorld);
    TestNotNull(TEXT("Creates second collection inheritance world"), SecondWorld);
    if (FirstWorld == nullptr || SecondWorld == nullptr)
    {
        return false;
    }
    FirstChild = FirstWorld->SpawnActor<AActor>(ChildClass);
    SecondChild = SecondWorld->SpawnActor<AActor>(ChildClass);
    FirstSibling = FirstWorld->SpawnActor<AActor>(SiblingClass);
    FirstGrandchild = FirstWorld->SpawnActor<AActor>(GrandchildClass);
    SecondGrandchild = SecondWorld->SpawnActor<AActor>(GrandchildClass);
    TestNotNull(TEXT("Creates first child instance"), FirstChild);
    TestNotNull(TEXT("Creates second child instance"), SecondChild);
    TestNotNull(TEXT("Creates an untouched sibling instance"), FirstSibling);
    TestNotNull(TEXT("Creates first grandchild instance"), FirstGrandchild);
    TestNotNull(TEXT("Creates second grandchild instance"), SecondGrandchild);
    if (FirstChild == nullptr || SecondChild == nullptr || FirstSibling == nullptr ||
        FirstGrandchild == nullptr || SecondGrandchild == nullptr)
    {
        return false;
    }
    const FArrayProperty* SecondChildArray = FindFProperty<FArrayProperty>(SecondChild->GetClass(), TEXT("Numbers"));
    TestTrue(TEXT("Second child gets an independent whole-value override"),
        SetArrayValue(SecondChild, SecondChildArray, TEXT("(70,71)")));
    const FName FirstChildName = FirstChild->GetFName();
    const FName SecondChildName = SecondChild->GetFName();
    const FName FirstSiblingName = FirstSibling->GetFName();
    const FName FirstGrandchildName = FirstGrandchild->GetFName();
    const FName SecondGrandchildName = SecondGrandchild->GetFName();
    ChildPackage->SetDirtyFlag(false);
    SiblingPackage->SetDirtyFlag(false);
    GrandchildPackage->SetDirtyFlag(false);

    const FString UpdatedDocument = MakeDocument(
        false, TEXT("NumbersRenamed"), TEXT("NumbersSetRenamed"), TEXT("MappingRenamed"),
        TEXT("[8,9]"), TEXT("[10,11]"), TEXT("[{\"key\":6,\"value\":7}]"));
    Error.Reset();
    TestTrue(*FString::Printf(TEXT("Renaming base collection states succeeds: %s"), *Error),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset, *BaseBlueprint, UpdatedDocument, false, Result, Error));

    const auto FindActorByName = [](UWorld* World, FName Name) -> AActor*
    {
        if (World == nullptr)
        {
            return nullptr;
        }
        for (TActorIterator<AActor> It(World); It; ++It)
        {
            if (It->GetFName() == Name)
            {
                return *It;
            }
        }
        return nullptr;
    };
    FirstChild = FindActorByName(FirstWorld, FirstChildName);
    SecondChild = FindActorByName(SecondWorld, SecondChildName);
    FirstSibling = FindActorByName(FirstWorld, FirstSiblingName);
    FirstGrandchild = FindActorByName(FirstWorld, FirstGrandchildName);
    SecondGrandchild = FindActorByName(SecondWorld, SecondGrandchildName);
    TestNotNull(TEXT("First child is reacquired after Blueprint reinstancing"), FirstChild);
    TestNotNull(TEXT("Second child is reacquired after Blueprint reinstancing"), SecondChild);
    TestNotNull(TEXT("Sibling is reacquired after Blueprint reinstancing"), FirstSibling);
    TestNotNull(TEXT("First grandchild is reacquired after Blueprint reinstancing"), FirstGrandchild);
    TestNotNull(TEXT("Second grandchild is reacquired after Blueprint reinstancing"), SecondGrandchild);

    const FArrayProperty* BaseRenamedArray = FindFProperty<FArrayProperty>(BaseBlueprint->GeneratedClass, TEXT("NumbersRenamed"));
    const FSetProperty* BaseRenamedSet = FindFProperty<FSetProperty>(BaseBlueprint->GeneratedClass, TEXT("NumbersSetRenamed"));
    const FMapProperty* BaseRenamedMap = FindFProperty<FMapProperty>(BaseBlueprint->GeneratedClass, TEXT("MappingRenamed"));
    SiblingClass = SiblingBlueprint->GeneratedClass;
    const FArrayProperty* SiblingRenamedArray = FindFProperty<FArrayProperty>(SiblingClass, TEXT("NumbersRenamed"));
    const FSetProperty* SiblingRenamedSet = FindFProperty<FSetProperty>(SiblingClass, TEXT("NumbersSetRenamed"));
    const FMapProperty* SiblingRenamedMap = FindFProperty<FMapProperty>(SiblingClass, TEXT("MappingRenamed"));
    const FArrayProperty* ChildRenamedArray = FindFProperty<FArrayProperty>(ChildBlueprint->GeneratedClass, TEXT("NumbersRenamed"));
    const FSetProperty* ChildRenamedSet = FindFProperty<FSetProperty>(ChildBlueprint->GeneratedClass, TEXT("NumbersSetRenamed"));
    const FMapProperty* ChildRenamedMap = FindFProperty<FMapProperty>(ChildBlueprint->GeneratedClass, TEXT("MappingRenamed"));
    const FArrayProperty* GrandchildRenamedArray = FindFProperty<FArrayProperty>(GrandchildBlueprint->GeneratedClass, TEXT("NumbersRenamed"));
    const FSetProperty* GrandchildRenamedSet = FindFProperty<FSetProperty>(GrandchildBlueprint->GeneratedClass, TEXT("NumbersSetRenamed"));
    const FMapProperty* GrandchildRenamedMap = FindFProperty<FMapProperty>(GrandchildBlueprint->GeneratedClass, TEXT("MappingRenamed"));
    TestNotNull(TEXT("Base array rename exists"), BaseRenamedArray);
    TestNotNull(TEXT("Base set rename exists"), BaseRenamedSet);
    TestNotNull(TEXT("Base map rename exists"), BaseRenamedMap);
    TestNotNull(TEXT("Sibling array rename exists"), SiblingRenamedArray);
    TestNotNull(TEXT("Sibling set rename exists"), SiblingRenamedSet);
    TestNotNull(TEXT("Sibling map rename exists"), SiblingRenamedMap);
    TestTrue(TEXT("Child whole array override survives rename"),
        ArrayMatches(ReadArray(ChildBlueprint->GeneratedClass->GetDefaultObject(), ChildRenamedArray), {20, 21}));
    TestTrue(TEXT("Grandchild follower copies the final child array"),
        ArrayMatches(ReadArray(GrandchildBlueprint->GeneratedClass->GetDefaultObject(), GrandchildRenamedArray), {20, 21}));
    TestTrue(TEXT("Second child instance keeps its own array override"),
        ArrayMatches(ReadArray(SecondChild, FindFProperty<FArrayProperty>(SecondChild->GetClass(), TEXT("NumbersRenamed"))), {70, 71}));
    TestTrue(TEXT("First child follower copies child CDO array"),
        ArrayMatches(ReadArray(FirstChild, FindFProperty<FArrayProperty>(FirstChild->GetClass(), TEXT("NumbersRenamed"))), {20, 21}));
    const TSet<int32> GrandchildSetValues = ReadSet(
        GrandchildBlueprint->GeneratedClass->GetDefaultObject(), GrandchildRenamedSet);
    TestTrue(TEXT("Grandchild follower copies final child set"),
        GrandchildSetValues.Num() == 2 &&
        GrandchildSetValues.Contains(30) && GrandchildSetValues.Contains(40));
    const TMap<int32, int32> ChildMapValues = ReadMap(
        ChildBlueprint->GeneratedClass->GetDefaultObject(), ChildRenamedMap);
    TestTrue(TEXT("Child whole map override survives rename without hybrid entries"),
        ChildMapValues.Num() == 1 && ChildMapValues.FindRef(5) == 6);
    const TMap<int32, int32> GrandchildMapValues = ReadMap(
        GrandchildBlueprint->GeneratedClass->GetDefaultObject(), GrandchildRenamedMap);
    TestTrue(TEXT("Grandchild independent map override survives rename"),
        GrandchildMapValues.Num() == 1 && GrandchildMapValues.FindRef(9) == 10);
    TestTrue(TEXT("Base array receives the new declaration default"),
        ArrayMatches(ReadArray(BaseBlueprint->GeneratedClass->GetDefaultObject(), BaseRenamedArray), {8, 9}));
    TestTrue(TEXT("Untouched sibling CDO follows the new array default"),
        ArrayMatches(ReadArray(SiblingClass->GetDefaultObject(), SiblingRenamedArray), {8, 9}));
    const TSet<int32> SiblingSetValues = ReadSet(SiblingClass->GetDefaultObject(), SiblingRenamedSet);
    TestTrue(TEXT("Untouched sibling CDO follows the new set default"),
        SiblingSetValues.Num() == 2 && SiblingSetValues.Contains(10) && SiblingSetValues.Contains(11));
    const TMap<int32, int32> SiblingMapValues = ReadMap(SiblingClass->GetDefaultObject(), SiblingRenamedMap);
    TestTrue(TEXT("Untouched sibling CDO follows the complete new map default"),
        SiblingMapValues.Num() == 1 && SiblingMapValues.FindRef(6) == 7);
    TestTrue(TEXT("Collection restoration marks the changed child package dirty"), ChildPackage->IsDirty());
    TestTrue(TEXT("Collection restoration marks the changed grandchild package dirty"), GrandchildPackage->IsDirty());
    NewSibling = FirstWorld->SpawnActor<AActor>(SiblingClass);
    TestNotNull(TEXT("New sibling instance can be spawned after reinstance"), NewSibling);
    TestTrue(TEXT("New sibling instance follows the new array default"),
        ArrayMatches(ReadArray(NewSibling, SiblingRenamedArray), {8, 9}));
    TestTrue(TEXT("New sibling instance follows the complete new set default"),
        ReadSet(NewSibling, SiblingRenamedSet).Num() == 2 &&
        ReadSet(NewSibling, SiblingRenamedSet).Contains(10) &&
        ReadSet(NewSibling, SiblingRenamedSet).Contains(11));
    const TMap<int32, int32> NewSiblingMapValues = ReadMap(NewSibling, SiblingRenamedMap);
    TestTrue(TEXT("New sibling instance follows the complete new map default"),
        NewSiblingMapValues.Num() == 1 && NewSiblingMapValues.FindRef(6) == 7);
    const FName NewSiblingName = NewSibling->GetFName();

    const UClass* BaseClassBeforeReject = BaseBlueprint->GeneratedClass;
    const UClass* ChildClassBeforeReject = ChildBlueprint->GeneratedClass;
    const UClass* GrandchildClassBeforeReject = GrandchildBlueprint->GeneratedClass;
    const UClass* SiblingClassBeforeReject = SiblingBlueprint->GeneratedClass;
    const UClass* ChildParentBeforeReject = ChildBlueprint->GeneratedClass->GetSuperClass();
    const TArray<int32> ChildArrayBeforeReject = ReadArray(
        ChildBlueprint->GeneratedClass->GetDefaultObject(), ChildRenamedArray);
    const TArray<int32> SecondChildArrayBeforeReject = ReadArray(
        SecondChild, FindFProperty<FArrayProperty>(SecondChild->GetClass(), TEXT("NumbersRenamed")));
    const TArray<int32> NewSiblingArrayBeforeReject = ReadArray(
        NewSibling, FindFProperty<FArrayProperty>(NewSibling->GetClass(), TEXT("NumbersRenamed")));
    const TMap<int32, int32> NewSiblingMapBeforeReject = ReadMap(
        NewSibling, FindFProperty<FMapProperty>(NewSibling->GetClass(), TEXT("MappingRenamed")));
    const TMap<int32, int32> GrandchildMapBeforeReject = ReadMap(
        GrandchildBlueprint->GeneratedClass->GetDefaultObject(), GrandchildRenamedMap);
    const bool ChildPackageDirtyBeforeReject = ChildPackage->IsDirty();
    const bool SiblingPackageDirtyBeforeReject = SiblingPackage->IsDirty();
    const bool GrandchildPackageDirtyBeforeReject = GrandchildPackage->IsDirty();

    const FString TypeChangedDocument = MakeDocument(
        true, TEXT("NumbersRenamed"), TEXT("NumbersSetRenamed"), TEXT("MappingRenamed"),
        TEXT("[8,9]"), TEXT("[10,11]"), TEXT("[{\"key\":6,\"value\":7}]"));
    Error.Reset();
    TestFalse(TEXT("Validate-only rejects a collection retype with derived overrides"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset, *BaseBlueprint, TypeChangedDocument, true, Result, Error));
    TestTrue(TEXT("Validate-only retype rejection names the instance override"),
        Error.Contains(TEXT("instance override")));
    Error.Reset();
    TestFalse(TEXT("Apply rejects a collection retype with derived overrides"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset, *BaseBlueprint, TypeChangedDocument, false, Result, Error));
    TestTrue(TEXT("Apply retype rejection leaves the old array property"),
        FindFProperty<FArrayProperty>(BaseBlueprint->GeneratedClass, TEXT("NumbersRenamed")) != nullptr);
    TestTrue(TEXT("Rejected retype preserves generated class identities and parent relation"),
        BaseBlueprint->GeneratedClass == BaseClassBeforeReject &&
        ChildBlueprint->GeneratedClass == ChildClassBeforeReject &&
        GrandchildBlueprint->GeneratedClass == GrandchildClassBeforeReject &&
        SiblingBlueprint->GeneratedClass == SiblingClassBeforeReject &&
        ChildBlueprint->GeneratedClass->GetSuperClass() == ChildParentBeforeReject);
    TestTrue(TEXT("Rejected retype preserves derived CDO and independent instance values"),
        ReadArray(ChildBlueprint->GeneratedClass->GetDefaultObject(), ChildRenamedArray) == ChildArrayBeforeReject &&
        ReadArray(SecondChild, FindFProperty<FArrayProperty>(SecondChild->GetClass(), TEXT("NumbersRenamed"))) ==
            SecondChildArrayBeforeReject &&
        ReadArray(NewSibling, FindFProperty<FArrayProperty>(NewSibling->GetClass(), TEXT("NumbersRenamed"))) ==
            NewSiblingArrayBeforeReject &&
        ReadMap(NewSibling, FindFProperty<FMapProperty>(NewSibling->GetClass(), TEXT("MappingRenamed"))).Num() ==
            NewSiblingMapBeforeReject.Num() &&
        ReadMap(NewSibling, FindFProperty<FMapProperty>(NewSibling->GetClass(), TEXT("MappingRenamed"))).FindRef(5) ==
            NewSiblingMapBeforeReject.FindRef(5) &&
        ReadMap(GrandchildBlueprint->GeneratedClass->GetDefaultObject(), GrandchildRenamedMap).Num() ==
            GrandchildMapBeforeReject.Num() &&
        ReadMap(GrandchildBlueprint->GeneratedClass->GetDefaultObject(), GrandchildRenamedMap).FindRef(9) ==
            GrandchildMapBeforeReject.FindRef(9));
    TestTrue(TEXT("Rejected retype preserves child package dirty state"),
        ChildPackage->IsDirty() == ChildPackageDirtyBeforeReject);
    TestTrue(TEXT("Rejected retype preserves sibling package dirty state"),
        SiblingPackage->IsDirty() == SiblingPackageDirtyBeforeReject);
    TestTrue(TEXT("Rejected retype preserves grandchild package dirty state"),
        GrandchildPackage->IsDirty() == GrandchildPackageDirtyBeforeReject);

    // Make every derived object a follower, then the same retype is allowed.
    const FArrayProperty* CurrentBaseArray = FindFProperty<FArrayProperty>(BaseBlueprint->GeneratedClass, TEXT("NumbersRenamed"));
    const FArrayProperty* CurrentChildArray = FindFProperty<FArrayProperty>(ChildBlueprint->GeneratedClass, TEXT("NumbersRenamed"));
    const FArrayProperty* CurrentGrandchildArray = FindFProperty<FArrayProperty>(GrandchildBlueprint->GeneratedClass, TEXT("NumbersRenamed"));
    SetArrayValue(ChildBlueprint->GeneratedClass->GetDefaultObject(), CurrentChildArray, TEXT("(8,9)"));
    SetArrayValue(GrandchildBlueprint->GeneratedClass->GetDefaultObject(), CurrentGrandchildArray, TEXT("(8,9)"));
    SetArrayValue(FirstChild, FindFProperty<FArrayProperty>(FirstChild->GetClass(), TEXT("NumbersRenamed")), TEXT("(8,9)"));
    SetArrayValue(SecondChild, FindFProperty<FArrayProperty>(SecondChild->GetClass(), TEXT("NumbersRenamed")), TEXT("(8,9)"));
    SetArrayValue(FirstGrandchild, FindFProperty<FArrayProperty>(FirstGrandchild->GetClass(), TEXT("NumbersRenamed")), TEXT("(8,9)"));
    SetArrayValue(SecondGrandchild, FindFProperty<FArrayProperty>(SecondGrandchild->GetClass(), TEXT("NumbersRenamed")), TEXT("(8,9)"));
    TestNotNull(TEXT("Pure follower retype starts with the base array"), CurrentBaseArray);
    Error.Reset();
    TestTrue(TEXT("Pure followers accept collection retype"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset, *BaseBlueprint, TypeChangedDocument, false, Result, Error));
    FirstChild = FindActorByName(FirstWorld, FirstChildName);
    SecondChild = FindActorByName(SecondWorld, SecondChildName);
    FirstGrandchild = FindActorByName(FirstWorld, FirstGrandchildName);
    SecondGrandchild = FindActorByName(SecondWorld, SecondGrandchildName);
    NewSibling = FindActorByName(FirstWorld, NewSiblingName);
    TestNotNull(TEXT("First child is reacquired after pure follower retype"), FirstChild);
    TestNotNull(TEXT("Second child is reacquired after pure follower retype"), SecondChild);
    TestNotNull(TEXT("First grandchild is reacquired after pure follower retype"), FirstGrandchild);
    TestNotNull(TEXT("Second grandchild is reacquired after pure follower retype"), SecondGrandchild);
    TestNotNull(TEXT("New sibling is reacquired after pure follower retype"), NewSibling);
    if (FirstChild == nullptr || SecondChild == nullptr || FirstGrandchild == nullptr ||
        SecondGrandchild == nullptr || NewSibling == nullptr)
    {
        return false;
    }
    SiblingClass = SiblingBlueprint->GeneratedClass;
    TestNotNull(TEXT("Pure follower retype produces a Set property"),
        FindFProperty<FSetProperty>(BaseBlueprint->GeneratedClass, TEXT("NumbersRenamed")));
    const FSetProperty* RetypedBaseSet = FindFProperty<FSetProperty>(BaseBlueprint->GeneratedClass, TEXT("NumbersRenamed"));
    const FSetProperty* RetypedChildSet = FindFProperty<FSetProperty>(ChildBlueprint->GeneratedClass, TEXT("NumbersRenamed"));
    const FSetProperty* RetypedSiblingSet = FindFProperty<FSetProperty>(SiblingBlueprint->GeneratedClass, TEXT("NumbersRenamed"));
    const FSetProperty* RetypedGrandchildSet = FindFProperty<FSetProperty>(GrandchildBlueprint->GeneratedClass, TEXT("NumbersRenamed"));
    TestTrue(TEXT("Pure follower retype updates all derived CDOs"),
        RetypedBaseSet != nullptr && RetypedChildSet != nullptr &&
        RetypedSiblingSet != nullptr && RetypedGrandchildSet != nullptr &&
        ReadSet(BaseBlueprint->GeneratedClass->GetDefaultObject(), RetypedBaseSet).Num() == 2 &&
        ReadSet(BaseBlueprint->GeneratedClass->GetDefaultObject(), RetypedBaseSet).Contains(8) &&
        ReadSet(BaseBlueprint->GeneratedClass->GetDefaultObject(), RetypedBaseSet).Contains(9) &&
        ReadSet(ChildBlueprint->GeneratedClass->GetDefaultObject(), RetypedChildSet).Num() == 2 &&
        ReadSet(ChildBlueprint->GeneratedClass->GetDefaultObject(), RetypedChildSet).Contains(8) &&
        ReadSet(ChildBlueprint->GeneratedClass->GetDefaultObject(), RetypedChildSet).Contains(9) &&
        ReadSet(SiblingBlueprint->GeneratedClass->GetDefaultObject(), RetypedSiblingSet).Num() == 2 &&
        ReadSet(SiblingBlueprint->GeneratedClass->GetDefaultObject(), RetypedSiblingSet).Contains(8) &&
        ReadSet(SiblingBlueprint->GeneratedClass->GetDefaultObject(), RetypedSiblingSet).Contains(9) &&
        ReadSet(GrandchildBlueprint->GeneratedClass->GetDefaultObject(), RetypedGrandchildSet).Num() == 2 &&
        ReadSet(GrandchildBlueprint->GeneratedClass->GetDefaultObject(), RetypedGrandchildSet).Contains(8) &&
        ReadSet(GrandchildBlueprint->GeneratedClass->GetDefaultObject(), RetypedGrandchildSet).Contains(9));
    TestTrue(TEXT("Pure follower retype updates both existing world instances"),
        ReadSet(FirstChild, FindFProperty<FSetProperty>(FirstChild->GetClass(), TEXT("NumbersRenamed"))).Num() == 2 &&
        ReadSet(FirstChild, FindFProperty<FSetProperty>(FirstChild->GetClass(), TEXT("NumbersRenamed"))).Contains(8) &&
        ReadSet(SecondChild, FindFProperty<FSetProperty>(SecondChild->GetClass(), TEXT("NumbersRenamed"))).Num() == 2 &&
        ReadSet(SecondChild, FindFProperty<FSetProperty>(SecondChild->GetClass(), TEXT("NumbersRenamed"))).Contains(9) &&
        ReadSet(FirstGrandchild, FindFProperty<FSetProperty>(FirstGrandchild->GetClass(), TEXT("NumbersRenamed"))).Num() == 2 &&
        ReadSet(FirstGrandchild, FindFProperty<FSetProperty>(FirstGrandchild->GetClass(), TEXT("NumbersRenamed"))).Contains(8) &&
        ReadSet(SecondGrandchild, FindFProperty<FSetProperty>(SecondGrandchild->GetClass(), TEXT("NumbersRenamed"))).Num() == 2 &&
        ReadSet(SecondGrandchild, FindFProperty<FSetProperty>(SecondGrandchild->GetClass(), TEXT("NumbersRenamed"))).Contains(9));
    const FSetProperty* NewSiblingRetypedSet =
        FindFProperty<FSetProperty>(NewSibling->GetClass(), TEXT("NumbersRenamed"));
    const TSet<int32> NewSiblingRetypedSetValues = ReadSet(NewSibling, NewSiblingRetypedSet);
    const FSetProperty* NewSiblingExistingSet =
        FindFProperty<FSetProperty>(NewSibling->GetClass(), TEXT("NumbersSetRenamed"));
    const TSet<int32> NewSiblingExistingSetValues = ReadSet(NewSibling, NewSiblingExistingSet);
    const FSetProperty* SiblingCdoExistingSet =
        FindFProperty<FSetProperty>(SiblingClass, TEXT("NumbersSetRenamed"));
    const TSet<int32> SiblingCdoExistingSetValues = ReadSet(SiblingClass->GetDefaultObject(), SiblingCdoExistingSet);
    TestTrue(TEXT("New sibling keeps the complete set after pure follower retype"),
        NewSiblingRetypedSetValues.Num() == 2 &&
        NewSiblingRetypedSetValues.Contains(8) &&
        NewSiblingRetypedSetValues.Contains(9));
    TestTrue(TEXT("New sibling keeps the existing set state after pure follower retype"),
        NewSiblingExistingSetValues.Num() == 2 &&
        NewSiblingExistingSetValues.Contains(10) &&
        NewSiblingExistingSetValues.Contains(11) &&
        SiblingCdoExistingSetValues.Num() == 2 &&
        SiblingCdoExistingSetValues.Contains(10) &&
        SiblingCdoExistingSetValues.Contains(11));
    TestTrue(TEXT("New sibling keeps the complete map while array follows retype"),
        ReadMap(NewSibling, FindFProperty<FMapProperty>(NewSibling->GetClass(), TEXT("MappingRenamed"))).Num() == 1 &&
        ReadMap(NewSibling, FindFProperty<FMapProperty>(NewSibling->GetClass(), TEXT("MappingRenamed"))).FindRef(6) == 7);
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
    constexpr const TCHAR* ActivationFunctionId =
        TEXT("6c4d746b47fc4fa8ae54f6d4e3ce1a01");
    constexpr const TCHAR* InputFunctionId =
        TEXT("ad58fc50ea45402b967fe7d06552a8d1");
    constexpr const TCHAR* DelegateFunctionId =
        TEXT("2bc08f81fbde47f5b85f19d3952041a2");
    constexpr const TCHAR* InterfaceFunctionId =
        TEXT("75af9e6e61724bf0a36bfe445685879b");
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
        RewireParameters,
        false,
        true,
        false,
        FString(),
        true);
    const FString ActivationFunction = Function(
        ActivationFunctionId,
        TEXT("OnManagedActivated"),
        TEXT("System.Void"),
        Parameter(
            TEXT("51cce46fc53049898dddf7bd6139c5ee"),
            TEXT("amount"),
            TEXT("System.Int32")),
        false,
        true,
        false,
        FString(),
        true);
    const FString InputFunction = Function(
        InputFunctionId,
        TEXT("OnManagedMove"),
        TEXT("System.Void"),
        Parameter(
            TEXT("a7c6265a5a144552a0fc34ebdd2ccf40"),
            TEXT("value"),
            TEXT("System.Single")),
        false,
        true,
        false,
        FString(),
        false,
        TEXT(R"JSON(,
      "inputActions": [{
        "id": "us_43971457ceac41beb545c5fa2ca503d1",
        "actionPath": "/Script/UEUnifiedScriptRuntime.UnifiedScriptInputActionFixture",
        "triggerEvent": "triggered",
        "valueType": "axis1D",
        "consumeInput": true
      }])JSON"));
    const FString DelegateFunction = Function(
        DelegateFunctionId,
        TEXT("OnManagedSignal"),
        TEXT("System.Void"),
        FString::Join(
            TArray<FString>{
                Parameter(
                    TEXT("13904f09252e47eea553f72913d3d77e"),
                    TEXT("value"),
                    TEXT("System.Int32")),
                Parameter(
                    TEXT("7588b815fa94489c8c90ca3d15e681d5"),
                    TEXT("signalContext"),
                    TEXT("UnifiedScript.Unreal.UnrealObjectHandle")),
            },
            TEXT(",")),
        false,
        false,
        true,
        FString(),
        false,
        TEXT(R"JSON(,
      "delegateBindings": [{
        "id": "us_e32bc94f847c4f92941e502658b89c2c",
        "sourceTypePath": "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent",
        "eventPath": "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnManagedSignal",
        "source": "host",
        "sourceMember": null
      }])JSON"));
    const FString InterfaceFunction = Function(
        InterfaceFunctionId,
        TEXT("TransformManagedValue"),
        TEXT("System.Int32"),
        Parameter(
            TEXT("1eb6944f834e467e9bfd253b4ba6aa8b"),
            TEXT("value"),
            TEXT("System.Int32")),
        false,
        true,
        false,
        FString(),
        false,
        TEXT(R"JSON(,
      "interfaceFunctions": [{
        "id": "us_0590a882f00746a1ad5db6df4f49d3c0",
        "interfacePath": "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropContract",
        "functionName": "TransformManagedValue"
      }])JSON"));
    const FString ManagedFunctions = FString::Join(
        TArray<FString>{
            RewireFunction,
            ActivationFunction,
            InputFunction,
            DelegateFunction,
            InterfaceFunction,
        },
        TEXT(","));
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

    const FString ScriptDocument = Document(
        InitialValueState,
        ManagedFunctions,
        DemoClassId,
        DemoQualifiedName,
        DemoAssetPath);
    FUnifiedScriptBlueprintClassSyncResult SyncResult;
    FString Error;
    const bool bSynchronized =
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            ScriptDocument,
            false,
            SyncResult,
            Error);
    if (!TestTrue(
            *FString::Printf(TEXT("Materializes the live managed function: %s"), *Error),
            bSynchronized))
    {
        return false;
    }
    TestEqual(TEXT("One Enhanced Input contract is declared"),
        SyncResult.DeclaredInputBindingCount, 1);
    TestEqual(TEXT("One delegate contract is declared"),
        SyncResult.DeclaredDelegateBindingCount, 1);
    TestEqual(TEXT("One interface contract is declared"),
        SyncResult.DeclaredInterfaceBindingCount, 1);
    const UUnifiedScriptBlueprintGeneratedClass* DispatchGeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
    if (!TestNotNull(TEXT("Gameplay contracts use the product GeneratedClass"),
            DispatchGeneratedClass))
    {
        return false;
    }
    TestEqual(TEXT("GeneratedClass persists the exact input route"),
        DispatchGeneratedClass->ManagedInputActionBindings.Num(), 1);
    TestEqual(TEXT("GeneratedClass persists the exact delegate route"),
        DispatchGeneratedClass->ManagedDelegateBindings.Num(), 1);
    TestEqual(TEXT("GeneratedClass persists the exact interface route"),
        DispatchGeneratedClass->ManagedInterfaceBindings.Num(), 1);
    TestTrue(TEXT("Generated component implements the native interface"),
        Blueprint->GeneratedClass->ImplementsInterface(
            UUnifiedScriptInteropContract::StaticClass()));
    TestTrue(TEXT("Native interface function binds to the generic managed dispatcher"),
        DispatchGeneratedClass->IsManagedFunctionBound(
            TEXT("TransformManagedValue")));
    const FUnifiedScriptManagedFunctionBinding* InterfaceDispatchBinding =
        DispatchGeneratedClass->ManagedFunctionBindings.Find(
            TEXT("TransformManagedValue"));
    TestTrue(TEXT("Native interface function retains its event-entry contract"),
        InterfaceDispatchBinding != nullptr &&
            InterfaceDispatchBinding->bInterfaceFunction);

    UEdGraph* InterfaceGraph = nullptr;
    for (const FBPInterfaceDescription& Interface : Blueprint->ImplementedInterfaces)
    {
        for (UEdGraph* Graph : Interface.Graphs)
        {
            if (Graph != nullptr &&
                Graph->GetFName() == TEXT("TransformManagedValue"))
            {
                InterfaceGraph = Graph;
                break;
            }
        }
        if (InterfaceGraph != nullptr)
        {
            break;
        }
    }
    FKismetUserDeclaredFunctionMetadata* InterfaceMetadata =
        FBlueprintEditorUtils::GetGraphFunctionMetaData(InterfaceGraph);
    if (TestNotNull(
            TEXT("Managed interface owns its generated Blueprint graph"),
            InterfaceGraph))
    {
        // Kismet owns native-interface graph signatures and does not expose
        // user-declared function metadata for them, even immediately after
        // creation. Prove the durable GeneratedClass binding keeps
        // ValidateOnly idempotent instead of reporting a user graph collision.
        TestNull(
            TEXT("Kismet interface graph has no user-function metadata carrier"),
            InterfaceMetadata);
        FUnifiedScriptBlueprintClassSyncResult ReloadReplayResult;
        FString ReloadReplayError;
        const bool bReloadReplay =
            FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                *ScriptAsset,
                *Blueprint,
                ScriptDocument,
                true,
                ReloadReplayResult,
                ReloadReplayError);
        TestTrue(
            *FString::Printf(
                TEXT("Reloaded interface metadata loss remains a no-change replay: %s"),
                *ReloadReplayError),
            bReloadReplay);
        TestFalse(
            TEXT("Persisted interface ownership prevents regeneration churn"),
            ReloadReplayResult.bChanged);
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
    TestTrue(TEXT("Live managed dispatch remains a Blueprint event"),
        ManagedFunction->HasAllFunctionFlags(FUNC_Event | FUNC_BlueprintEvent));
    const FUnifiedScriptManagedFunctionBinding* DispatchBinding =
        DispatchGeneratedClass != nullptr
            ? DispatchGeneratedClass->ManagedFunctionBindings.Find(InternalName)
            : nullptr;
    TestTrue(TEXT("Live event binding persists explicit Super semantics"),
        DispatchBinding != nullptr && DispatchBinding->bBlueprintEvent &&
        DispatchBinding->BlueprintEventSuperMode ==
            EUnifiedScriptBlueprintEventSuperMode::Explicit);

    const FName ActivationInternalName(
        *FString::Printf(TEXT("USF_%s"), ActivationFunctionId));
    UFunction* ActivationParentFunction =
        Blueprint->GeneratedClass->FindFunctionByName(
            ActivationInternalName,
            EIncludeSuperFlag::ExcludeSuper);
    const FString ChildName = TEXT("BP_UnifiedScriptManagedSuper_") + Suffix;
    UPackage* ChildPackage = CreatePackage(
        *(TEXT("/Engine/Transient/") + ChildName));
    UBlueprint* ChildBlueprint = ActivationParentFunction != nullptr
        ? FKismetEditorUtilities::CreateBlueprint(
            Blueprint->GeneratedClass,
            ChildPackage,
            FName(*ChildName),
            BPTYPE_Normal,
            UBlueprint::StaticClass(),
            UBlueprintGeneratedClass::StaticClass(),
            FName(TEXT("UnifiedScriptBlueprintManagedSuperDispatch")))
        : nullptr;
    UEdGraph* ChildEventGraph = ChildBlueprint != nullptr &&
            !ChildBlueprint->UbergraphPages.IsEmpty()
        ? ChildBlueprint->UbergraphPages[0]
        : nullptr;
    int32 EventPositionY = 0;
    UK2Node_Event* ActivationOverride = ChildEventGraph != nullptr
        ? FKismetEditorUtilities::AddDefaultEventNode(
            ChildBlueprint,
            ChildEventGraph,
            ActivationInternalName,
            Blueprint->GeneratedClass,
            EventPositionY)
        : nullptr;
    UK2Node_CallParentFunction* ActivationParentCall = nullptr;
    if (ActivationOverride != nullptr)
    {
        FGraphNodeCreator<UK2Node_CallParentFunction> Creator(*ChildEventGraph);
        ActivationParentCall = Creator.CreateNode();
        ActivationParentCall->SetFromFunction(ActivationParentFunction);
        ActivationParentCall->AllocateDefaultPins();
        Creator.Finalize();
        const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
        UEdGraphPin* EventThen = ActivationOverride->FindPin(
            UEdGraphSchema_K2::PN_Then,
            EGPD_Output);
        UEdGraphPin* ParentExec = ActivationParentCall->GetExecPin();
        UEdGraphPin* EventAmount = ActivationOverride->FindPin(
            TEXT("amount"),
            EGPD_Output);
        UEdGraphPin* ParentAmount = ActivationParentCall->FindPin(
            TEXT("amount"),
            EGPD_Input);
        TestTrue(TEXT("Live Super exec path connects"),
            EventThen != nullptr && ParentExec != nullptr &&
                Schema->TryCreateConnection(EventThen, ParentExec));
        TestTrue(TEXT("Live Super argument path connects"),
            EventAmount != nullptr && ParentAmount != nullptr &&
                Schema->TryCreateConnection(EventAmount, ParentAmount));
    }
    if (!TestNotNull(TEXT("Live managed Blueprint event exists"),
            ActivationParentFunction) ||
        !TestNotNull(TEXT("Creates a live child Blueprint override"),
            ActivationOverride) ||
        !TestNotNull(TEXT("Creates a live explicit Call Parent node"),
            ActivationParentCall))
    {
        return false;
    }
    FKismetEditorUtilities::CompileBlueprint(ChildBlueprint);
    if (!TestEqual(TEXT("Live child Blueprint override compiles"),
            ChildBlueprint->Status,
            BS_UpToDate))
    {
        return false;
    }
    UFunction* CompiledChildOverride =
        ChildBlueprint->GeneratedClass->FindFunctionByName(
            ActivationInternalName,
            EIncludeSuperFlag::ExcludeSuper);
    if (!TestTrue(TEXT("Child override owns Blueprint bytecode"),
            CompiledChildOverride != nullptr &&
                !CompiledChildOverride->HasAnyFunctionFlags(FUNC_Native)))
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
    AActor* InputOwner = World->SpawnActor<AActor>();
    UUnifiedScriptComponent* Component =
        InputOwner != nullptr
        ? NewObject<UUnifiedScriptComponent>(
            InputOwner,
            ChildBlueprint->GeneratedClass)
        : nullptr;
    const bool bRuntimeObjectsReady =
        TestNotNull(TEXT("Managed dispatch World has a host"), Host) &&
        TestNotNull(TEXT("Creates an Actor owner for Enhanced Input"), InputOwner) &&
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
            UEnhancedInputComponent* EnhancedInput = InputOwner != nullptr
                ? Cast<UEnhancedInputComponent>(InputOwner->InputComponent)
                : nullptr;
            TestEqual(TEXT("Runtime owns one Enhanced Input handle"),
                Component->GetManagedInputBindingCount(), 1);
            if (TestNotNull(TEXT("Runtime creates an Enhanced Input component"),
                    EnhancedInput))
            {
                TestEqual(TEXT("Enhanced Input component has one generated binding"),
                    EnhancedInput->GetActionEventBindings().Num(), 1);
            }

            FStructOnScope FunctionParameters(ManagedFunction);
            uint8* ParameterMemory = FunctionParameters.GetStructMemory();
            ValueProperty->SetPropertyValue_InContainer(ParameterMemory, 21);
            DoubleValueProperty->SetPropertyValue_InContainer(ParameterMemory, true);
            Component->ProcessEvent(ManagedFunction, ParameterMemory);
            TestEqual(
                TEXT("Blueprint UFunction reaches CoreCLR and returns managed result"),
                ReturnProperty->GetPropertyValue_InContainer(ParameterMemory),
                42);

            TestEqual(TEXT("Native interface executes the managed implementation"),
                IUnifiedScriptInteropContract::Execute_TransformManagedValue(
                    Component,
                    21),
                42);

            Component->ManagedState = TEXT("Delegate");
            Component->BroadcastManagedSignal(73, Component);
            TestEqual(TEXT("Generated delegate contract dispatches exactly once"),
                Component->ManagedState,
                FString(TEXT("Delegate|Event:73")));

            if (EnhancedInput != nullptr &&
                EnhancedInput->GetActionEventBindings().Num() == 1)
            {
                const FEnhancedInputActionEventBinding& InputBinding =
                    *EnhancedInput->GetActionEventBindings()[0];
                FInputActionInstance InputInstance(InputBinding.GetAction());
                InputBinding.Execute(InputInstance);
                TestEqual(TEXT("Enhanced Input reaches the managed Axis1D handler"),
                    Component->ManagedState,
                    FString(TEXT("Input:0")));
            }

            TestTrue(
                *FString::Printf(TEXT("Compatible gameplay-contract reload succeeds: %s"),
                    *Component->LastScriptError),
                Component->ReloadScript());
            TestEqual(TEXT("Reload retains exactly one native input handle"),
                Component->GetManagedInputBindingCount(), 1);
            if (EnhancedInput != nullptr)
            {
                TestEqual(TEXT("Reload does not duplicate Enhanced Input bindings"),
                    EnhancedInput->GetActionEventBindings().Num(), 1);
            }
            Component->ManagedState = TEXT("Reloaded");
            Component->BroadcastManagedSignal(74, Component);
            TestEqual(TEXT("Reload replaces rather than duplicates delegate subscriptions"),
                Component->ManagedState,
                FString(TEXT("Reloaded|Event:74")));
            TestEqual(TEXT("Reloaded interface uses the active generation"),
                IUnifiedScriptInteropContract::Execute_TransformManagedValue(
                    Component,
                    22),
                44);

            UFunction* ActivationOverrideFunction = Component->FindFunction(
                ActivationInternalName);
            const FIntProperty* AmountProperty = ActivationOverrideFunction != nullptr
                ? FindFProperty<FIntProperty>(
                    ActivationOverrideFunction,
                    TEXT("amount"))
                : nullptr;
            if (TestNotNull(TEXT("Child override resolves at runtime"),
                    ActivationOverrideFunction) &&
                TestNotNull(TEXT("Child override retains its amount pin"),
                    AmountProperty))
            {
                FStructOnScope ActivationParameters(ActivationOverrideFunction);
                AmountProperty->SetPropertyValue_InContainer(
                    ActivationParameters.GetStructMemory(),
                    5);
                Component->ProcessEvent(
                    ActivationOverrideFunction,
                    ActivationParameters.GetStructMemory());
                TestEqual(
                    TEXT("Child Call Parent executes C# with the Blueprint argument"),
                    Component->ManagedState,
                    FString(TEXT("Activated:15")));
            }
            Component->EndPlay(EEndPlayReason::Destroyed);
            TestFalse(TEXT("Managed dispatch instance stops cleanly"),
                Component->bScriptRunning);
            TestEqual(TEXT("Stop releases every managed input handle"),
                Component->GetManagedInputBindingCount(), 0);
            TestNull(TEXT("Stop destroys its owned Enhanced Input component"),
                InputOwner != nullptr ? InputOwner->InputComponent.Get() : nullptr);
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
    FString FixtureOutputRoot;
    const bool bFixtureOutputRequested = ReadFixtureOutputArgument(FixtureOutputRoot);
    const FString FixtureDirectoryName = TEXT("UnifiedScriptFunctionFixture_") + Suffix;
    const FString AssetName = bFixtureOutputRequested
        ? TEXT("BP_UnifiedScriptFunctionFixture_") + Suffix
        : TEXT("BP_UnifiedScriptStaticDispatch_") + Suffix;
    const FString PackageName = bFixtureOutputRequested
        ? TEXT("/Game/") + FixtureDirectoryName + TEXT("/FunctionFixture")
        : TEXT("/Game/UnifiedScriptTests/") + AssetName;
    FString PackageFilename = FPackageName::LongPackageNameToFilename(
        PackageName,
        FPackageName::GetAssetPackageExtension());
    PackageFilename = FPaths::ConvertRelativePathToFull(PackageFilename);
    FPaths::NormalizeFilename(PackageFilename);
    bool bKeepFixture = false;
    ON_SCOPE_EXIT
    {
        if (UPackage* ExistingPackage = FindPackage(nullptr, *PackageName))
        {
            ExistingPackage->SetDirtyFlag(false);
            TArray<UPackage*> PackagesToUnload{ExistingPackage};
            UPackageTools::UnloadPackages(PackagesToUnload);
        }
        if (!bKeepFixture)
        {
            IFileManager::Get().Delete(*PackageFilename, false, true, true);
        }
    };
    UPackage* Package = CreatePackage(*PackageName);
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
    const FString CallerAssetName = AssetName + TEXT("_Caller");
    UBlueprint* CallerBlueprint = FKismetEditorUtilities::CreateBlueprint(
        AActor::StaticClass(),
        Package,
        FName(*CallerAssetName),
        BPTYPE_Normal,
        UBlueprint::StaticClass(),
        UBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("UnifiedScriptBlueprintManagedStaticFunctionCaller")));
    if (!TestNotNull(TEXT("Creates a normal Blueprint VM caller"), CallerBlueprint))
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
    constexpr const TCHAR* EchoModeId = TEXT("40404040404040404040404040404040");
    constexpr const TCHAR* EchoHitResultId = TEXT("50505050505050505050505050505050");
    constexpr const TCHAR* ResolveCommonId = TEXT("60606060606060606060606060606060");

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
    const FString ModeValueType = InteropModeType();
    const FString ModeParameters = Parameter(
        TEXT("44444444444444444444444444444440"),
        TEXT("value"),
        TEXT("UnifiedScript.Unreal.Generated.EUnifiedScriptInteropMode"),
        ModeValueType);
    const FString HitValueType = HitResultType();
    const FString HitParameters = Parameter(
        TEXT("55555555555555555555555555555550"),
        TEXT("value"),
        TEXT("UnifiedScript.Unreal.Generated.HitResult"),
        HitValueType);
    const FString CommonParameters = FString::Join(
        TArray<FString>{
            Parameter(
                TEXT("66666666666666666666666666666610"),
                TEXT("name"),
                TEXT("UnifiedScript.Unreal.UnrealName")),
            Parameter(
                TEXT("66666666666666666666666666666620"),
                TEXT("rotation"),
                TEXT("UnifiedScript.Unreal.UnrealRotator"),
                FString(),
                TEXT("inputOutput")),
            Parameter(
                TEXT("66666666666666666666666666666630"),
                TEXT("label"),
                TEXT("UnifiedScript.Unreal.UnrealText"),
                FString(),
                TEXT("output")),
            Parameter(
                TEXT("66666666666666666666666666666640"),
                TEXT("transform"),
                TEXT("UnifiedScript.Unreal.UnrealTransform")),
        },
        TEXT(","));
    struct FExchangeShape
    {
        const TCHAR* Name;
        const TCHAR* Id;
        const TCHAR* Metadata;
        FString Type;
    };
    const TArray<FExchangeShape> ExchangeShapes{
        {TEXT("ExchangeArray"), TEXT("71717171717171717171717171717171"),
         TEXT("System.Collections.Generic.IReadOnlyList<System.Int32>"),
         TEXT(R"JSON({"kind":"array","unrealTypePath":"","unrealNativeName":"TArray","fields":[],"typeArguments":[{"typeMetadataName":"System.Int32"}]})JSON")},
        {TEXT("ExchangeSet"), TEXT("72727272727272727272727272727272"),
         TEXT("System.Collections.Generic.IReadOnlySet<UnifiedScript.Unreal.UnrealName>"),
         TEXT(R"JSON({"kind":"set","unrealTypePath":"","unrealNativeName":"TSet","fields":[],"typeArguments":[{"typeMetadataName":"UnifiedScript.Unreal.UnrealName"}]})JSON")},
        {TEXT("ExchangeMap"), TEXT("73737373737373737373737373737373"),
         TEXT("System.Collections.Generic.IReadOnlyDictionary<System.String, UnifiedScript.Unreal.UnrealSoftObjectHandle>"),
         TEXT(R"JSON({"kind":"map","unrealTypePath":"","unrealNativeName":"TMap","fields":[],"typeArguments":[{"typeMetadataName":"System.String"},{"typeMetadataName":"UnifiedScript.Unreal.UnrealSoftObjectHandle"}]})JSON")},
        {TEXT("ExchangeSoftObject"), TEXT("74747474747474747474747474747474"),
         TEXT("UnifiedScript.Unreal.UnrealSoftObjectHandle"), FString()},
        {TEXT("ExchangeSoftClass"), TEXT("75757575757575757575757575757575"),
         TEXT("UnifiedScript.Unreal.UnrealSoftClassHandle"), FString()},
    };
    FString Functions = FString::Join(
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
            Function(
                EchoModeId,
                TEXT("EchoMode"),
                TEXT("UnifiedScript.Unreal.Generated.EUnifiedScriptInteropMode"),
                ModeParameters,
                true,
                true,
                false,
                ModeValueType),
            Function(
                EchoHitResultId,
                TEXT("EchoHitResult"),
                TEXT("UnifiedScript.Unreal.Generated.HitResult"),
                HitParameters,
                true,
                true,
                false,
                HitValueType),
            Function(
                ResolveCommonId,
                TEXT("ResolveCommonValues"),
                TEXT("UnifiedScript.Unreal.UnrealClassHandle"),
                CommonParameters,
                true),
        },
        TEXT(","));

    for (const FExchangeShape& Shape : ExchangeShapes)
    {
        const FString ShapeParameters = FString::Join(TArray<FString>{
            Parameter(TEXT("81818181818181818181818181818181"), TEXT("replacement"), Shape.Metadata, Shape.Type),
            Parameter(TEXT("82828282828282828282828282828282"), TEXT("value"), Shape.Metadata, Shape.Type, TEXT("inputOutput")),
            Parameter(TEXT("83838383838383838383838383838383"), TEXT("previous"), Shape.Metadata, Shape.Type, TEXT("output")),
        }, TEXT(","));
        Functions += TEXT(",") + Function(Shape.Id, Shape.Name, Shape.Metadata, ShapeParameters,
            true, true, false, Shape.Type);
    }
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

    // Exercise the generated functions through real Blueprint bytecode.  Each
    // The wrapper is a user function in a normal Actor Blueprint, so the K2
    // compiler supplies the hidden __WorldContext parameter and the VM
    // executes the generated managed call node rather than ProcessEvent on the
    // library function directly.
    TArray<FName> BlueprintCallerNames;
    TMap<FName, FName> BlueprintObservedOutputNames;
    UUnifiedScriptBlueprintGeneratedClass* MaterializedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
    if (!TestNotNull(TEXT("Materialized FunctionLibrary class is available for VM callers"), MaterializedClass))
    {
        return false;
    }
    const UEdGraphSchema_K2* GraphSchema = GetDefault<UEdGraphSchema_K2>();
    for (const FExchangeShape& Shape : ExchangeShapes)
    {
        const FName ExchangeName(*FString::Printf(TEXT("USF_%s"), Shape.Id));
        UFunction* Exchange = MaterializedClass->FindFunctionByName(
            ExchangeName,
            EIncludeSuperFlag::ExcludeSuper);
        if (!TestNotNull(*FString::Printf(TEXT("%s target exists before creating VM caller"), Shape.Name), Exchange))
        {
            return false;
        }

        const FName CallerName(*FString::Printf(TEXT("Call_%s"), Shape.Name));
        UEdGraph* CallerGraph = FBlueprintEditorUtils::CreateNewGraph(
            CallerBlueprint,
            CallerName,
            UEdGraph::StaticClass(),
            UEdGraphSchema_K2::StaticClass());
        if (!TestNotNull(*FString::Printf(TEXT("%s Blueprint caller graph is created"), Shape.Name), CallerGraph))
        {
            return false;
        }
        FBlueprintEditorUtils::AddFunctionGraph<UClass>(CallerBlueprint, CallerGraph, true, nullptr);

        TArray<UK2Node_FunctionEntry*> Entries;
        CallerGraph->GetNodesOfClass(Entries);
        TArray<UK2Node_FunctionResult*> Results;
        CallerGraph->GetNodesOfClass(Results);
        if (Entries.Num() == 1 && Results.Num() == 0)
        {
            if (UK2Node_FunctionResult* CreatedResult =
                    FBlueprintEditorUtils::FindOrCreateFunctionResultNode(Entries[0]))
            {
                Results.Add(CreatedResult);
            }
        }
        if (!TestEqual(*FString::Printf(TEXT("%s caller has one function entry"), Shape.Name), Entries.Num(), 1) ||
            !TestEqual(*FString::Printf(TEXT("%s caller has one function result"), Shape.Name), Results.Num(), 1))
        {
            return false;
        }

        UK2Node_CallFunction* CallNode = NewObject<UK2Node_CallFunction>(CallerGraph);
        CallNode->FunctionReference.SetExternalMember(ExchangeName, MaterializedClass);
        CallNode->CreateNewGuid();
        CallerGraph->AddNode(CallNode, true, false);
        CallNode->AllocateDefaultPins();
        if (!TestTrue(
                *FString::Printf(TEXT("%s caller resolves generated CallFunction pins"), Shape.Name),
                CallNode->FindPin(TEXT("replacement")) != nullptr &&
                    CallNode->FindPin(TEXT("value")) != nullptr &&
                    CallNode->FindPin(TEXT("previous")) != nullptr &&
                    CallNode->GetReturnValuePin() != nullptr))
        {
            return false;
        }
        const FName ObservedOutputName(*FString::Printf(TEXT("Observed_%s"), Shape.Name));
        if (!TestTrue(
                *FString::Printf(TEXT("%s caller adds an output observation property"), Shape.Name),
                FBlueprintEditorUtils::AddMemberVariable(
                    CallerBlueprint,
                    ObservedOutputName,
                    CallNode->FindPin(TEXT("previous"))->PinType)))
        {
            return false;
        }
        BlueprintObservedOutputNames.Add(FName(Shape.Id), ObservedOutputName);

        UK2Node_FunctionEntry* Entry = Entries[0];
        UK2Node_FunctionResult* Result = Results[0];
        UEdGraphPin* CallWorldContext = CallNode->FindPin(TEXT("WorldContextObject"));
        if (CallWorldContext != nullptr && Entry->FindPin(TEXT("WorldContextObject")) == nullptr)
        {
            Entry->CreateUserDefinedPin(
                TEXT("WorldContextObject"),
                CallWorldContext->PinType,
                EGPD_Output,
                false);
        }
        for (const FName PinName : {FName(TEXT("replacement")), FName(TEXT("value"))})
        {
            UEdGraphPin* CallPin = CallNode->FindPin(PinName);
            if (CallPin == nullptr ||
                Entry->CreateUserDefinedPin(PinName, CallPin->PinType, EGPD_Output) == nullptr)
            {
                AddError(*FString::Printf(TEXT("%s caller could not create input pin %s"), Shape.Name, *PinName.ToString()));
                return false;
            }
        }
        for (const FName PinName : {FName(TEXT("previous")), FName(TEXT("ReturnValue"))})
        {
            UEdGraphPin* CallPin = PinName == TEXT("ReturnValue")
                ? CallNode->GetReturnValuePin()
                : CallNode->FindPin(PinName);
            if (CallPin == nullptr ||
                Result->CreateUserDefinedPin(PinName, CallPin->PinType, EGPD_Input) == nullptr)
            {
                AddError(*FString::Printf(TEXT("%s caller could not create output pin %s"), Shape.Name, *PinName.ToString()));
                return false;
            }
        }

        UK2Node_VariableSet* ObserveOutput = NewObject<UK2Node_VariableSet>(CallerGraph);
        ObserveOutput->VariableReference.SetSelfMember(
            BlueprintObservedOutputNames.FindChecked(FName(Shape.Id)));
        ObserveOutput->CreateNewGuid();
        CallerGraph->AddNode(ObserveOutput, true, false);
        ObserveOutput->AllocateDefaultPins();
        UEdGraphPin* ObserveValue = ObserveOutput->FindPin(
            BlueprintObservedOutputNames.FindChecked(FName(Shape.Id)),
            EGPD_Input);
        if (!TestNotNull(
                *FString::Printf(TEXT("%s caller creates persistent out observation pin"), Shape.Name),
                ObserveValue))
        {
            return false;
        }

        UEdGraphPin* EntryThen = Entry->FindPin(UEdGraphSchema_K2::PN_Then, EGPD_Output);
        UEdGraphPin* CallExecute = CallNode->FindPin(UEdGraphSchema_K2::PN_Execute, EGPD_Input);
        UEdGraphPin* CallThen = CallNode->FindPin(UEdGraphSchema_K2::PN_Then, EGPD_Output);
        UEdGraphPin* ResultExecute = Result->FindPin(UEdGraphSchema_K2::PN_Execute, EGPD_Input);
        UEdGraphPin* ObserveExecute = ObserveOutput->GetExecPin();
        UEdGraphPin* ObserveThen = ObserveOutput->GetThenPin();
        UEdGraphPin* EntryWorldContext = Entry->FindPin(TEXT("WorldContextObject"));
        if (EntryWorldContext == nullptr)
        {
            EntryWorldContext = Entry->GetAutoWorldContextPin();
        }
        const bool bConnectedExec =
            EntryThen != nullptr && CallExecute != nullptr && CallThen != nullptr &&
            ObserveExecute != nullptr && ObserveThen != nullptr && ResultExecute != nullptr &&
            GraphSchema->TryCreateConnection(EntryThen, CallExecute) &&
            GraphSchema->TryCreateConnection(CallThen, ObserveExecute) &&
            GraphSchema->TryCreateConnection(ObserveThen, ResultExecute);
        UEdGraphPin* EntryKismetWorldContext = Entry->GetAutoWorldContextPin();
        UEdGraphPin* CallKismetWorldContext = CallNode->FindPin(TEXT("__WorldContext"));
        const bool bConnectedInputs =
            bConnectedExec &&
            EntryWorldContext != nullptr && CallWorldContext != nullptr &&
            GraphSchema->TryCreateConnection(EntryWorldContext, CallWorldContext) &&
            (EntryKismetWorldContext == nullptr || CallKismetWorldContext == nullptr ||
             GraphSchema->TryCreateConnection(EntryKismetWorldContext, CallKismetWorldContext)) &&
            GraphSchema->TryCreateConnection(Entry->FindPin(TEXT("replacement")), CallNode->FindPin(TEXT("replacement"))) &&
            GraphSchema->TryCreateConnection(Entry->FindPin(TEXT("value")), CallNode->FindPin(TEXT("value")));
        const bool bConnectedOutputs =
            bConnectedInputs &&
            GraphSchema->TryCreateConnection(CallNode->FindPin(TEXT("previous")), Result->FindPin(TEXT("previous"))) &&
            GraphSchema->TryCreateConnection(CallNode->FindPin(TEXT("previous")), ObserveValue) &&
            GraphSchema->TryCreateConnection(CallNode->GetReturnValuePin(), Result->FindPin(TEXT("ReturnValue")));
        if (!TestTrue(*FString::Printf(TEXT("%s caller connects typed input/ref/out/return pins"), Shape.Name), bConnectedOutputs))
        {
            return false;
        }
        FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(CallerBlueprint);
        BlueprintCallerNames.Add(CallerName);
    }
    FKismetEditorUtilities::CompileBlueprint(CallerBlueprint);
    if (!TestEqual(TEXT("Normal Blueprint with VM callers compiles"), CallerBlueprint->Status, BS_UpToDate))
    {
        return false;
    }
    UBlueprintGeneratedClass* CallerGeneratedClass =
        Cast<UBlueprintGeneratedClass>(CallerBlueprint->GeneratedClass);
    if (!TestNotNull(TEXT("Compiled Blueprint retains VM caller class"), CallerGeneratedClass))
    {
        return false;
    }
    for (const FName CallerName : BlueprintCallerNames)
    {
        UFunction* Caller = CallerGeneratedClass->FindFunctionByName(CallerName, EIncludeSuperFlag::ExcludeSuper);
        TestNotNull(*FString::Printf(TEXT("Blueprint VM caller %s exists"), *CallerName.ToString()), Caller);
        if (Caller != nullptr)
        {
            TestTrue(*FString::Printf(TEXT("Blueprint VM caller %s has bytecode"), *CallerName.ToString()), Caller->Script.Num() > 0);
            TestFalse(*FString::Printf(TEXT("Blueprint VM caller %s is not native"), *CallerName.ToString()), Caller->HasAnyFunctionFlags(FUNC_Native));
        }
    }
    FSavePackageArgs SaveArguments;
    SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
    SaveArguments.SaveFlags = SAVE_None;
    if (!TestTrue(
            TEXT("Persistent typed FunctionLibrary package saves"),
            UPackage::SavePackage(
                Package,
                Blueprint,
                *PackageFilename,
                SaveArguments)))
    {
        return false;
    }
    Blueprint = nullptr;
    ScriptAsset = nullptr;
    Package->SetDirtyFlag(false);
    TArray<UPackage*> PackagesToUnload{Package};
    if (!TestTrue(
            TEXT("Persistent typed FunctionLibrary package unloads"),
            UPackageTools::UnloadPackages(PackagesToUnload)))
    {
        return false;
    }
    Package = LoadPackage(nullptr, *PackageName, LOAD_None);
    Blueprint = Package != nullptr
        ? FindObject<UUnifiedScriptBlueprint>(Package, *AssetName)
        : nullptr;
    CallerBlueprint = Package != nullptr
        ? FindObject<UBlueprint>(Package, *CallerAssetName)
        : nullptr;
    ScriptAsset = Blueprint != nullptr ? Blueprint->ScriptAsset.Get() : nullptr;
    if (!TestNotNull(TEXT("Persistent typed FunctionLibrary package reloads"), Package) ||
        !TestNotNull(TEXT("Persistent typed FunctionLibrary Blueprint reloads"), Blueprint) ||
        !TestNotNull(TEXT("Persistent normal Blueprint caller reloads"), CallerBlueprint) ||
        !TestNotNull(TEXT("Persistent typed FunctionLibrary Script Asset reloads"), ScriptAsset))
    {
        return false;
    }
    TestEqual(TEXT("Common and container/Soft Reference functions are materialized"),
        SyncResult.DeclaredFunctionCount, 6 + ExchangeShapes.Num());

    UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
    if (!TestNotNull(TEXT("Static proxy retains the product GeneratedClass"), GeneratedClass))
    {
        return false;
    }
    CallerGeneratedClass = CallerBlueprint != nullptr
        ? Cast<UBlueprintGeneratedClass>(CallerBlueprint->GeneratedClass)
        : nullptr;
    if (!TestNotNull(TEXT("Reloaded Blueprint retains the VM caller class"), CallerGeneratedClass))
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
    const FName EchoModeInternalName(
        *FString::Printf(TEXT("USF_%s"), EchoModeId));
    UFunction* EchoModeFunction = GeneratedClass->FindFunctionByName(
        EchoModeInternalName,
        EIncludeSuperFlag::ExcludeSuper);
    const FName EchoHitResultInternalName(
        *FString::Printf(TEXT("USF_%s"), EchoHitResultId));
    UFunction* EchoHitResultFunction = GeneratedClass->FindFunctionByName(
        EchoHitResultInternalName,
        EIncludeSuperFlag::ExcludeSuper);
    const FName ResolveCommonInternalName(
        *FString::Printf(TEXT("USF_%s"), ResolveCommonId));
    UFunction* ResolveCommonFunction = GeneratedClass->FindFunctionByName(
        ResolveCommonInternalName,
        EIncludeSuperFlag::ExcludeSuper);
    const FUnifiedScriptManagedFunctionBinding* DescribeBinding =
        GeneratedClass->ManagedFunctionBindings.Find(DescribeInternalName);
    const FUnifiedScriptManagedFunctionBinding* EchoModeBinding =
        GeneratedClass->ManagedFunctionBindings.Find(EchoModeInternalName);
    const FUnifiedScriptManagedFunctionBinding* EchoHitResultBinding =
        GeneratedClass->ManagedFunctionBindings.Find(EchoHitResultInternalName);
    const FUnifiedScriptManagedFunctionBinding* ResolveCommonBinding =
        GeneratedClass->ManagedFunctionBindings.Find(ResolveCommonInternalName);
    if (!TestNotNull(TEXT("Static Describe UFunction exists"), DescribeFunction) ||
        !TestNotNull(TEXT("Static EchoObject UFunction exists"), EchoObjectFunction) ||
        !TestNotNull(TEXT("Static OffsetVector UFunction exists"), OffsetVectorFunction) ||
        !TestNotNull(TEXT("Static EchoMode UFunction exists"), EchoModeFunction) ||
        !TestNotNull(TEXT("Static EchoHitResult UFunction exists"), EchoHitResultFunction) ||
        !TestNotNull(TEXT("Static ResolveCommonValues UFunction exists"), ResolveCommonFunction) ||
        !TestNotNull(TEXT("Static Describe binding exists"), DescribeBinding) ||
        !TestNotNull(TEXT("Static EchoMode binding exists"), EchoModeBinding) ||
        !TestNotNull(TEXT("Static EchoHitResult binding exists"), EchoHitResultBinding) ||
        !TestNotNull(TEXT("Static ResolveCommonValues binding exists"), ResolveCommonBinding))
    {
        return false;
    }
    TestTrue(TEXT("Generated FunctionLibrary UFunction is static"),
        DescribeFunction->HasAnyFunctionFlags(FUNC_Static));
    TestTrue(
        TEXT("Persistent FunctionLibrary restores native managed dispatch"),
        GeneratedClass->IsManagedFunctionBound(DescribeInternalName));
    TestEqual(TEXT("Static binding selects multi-result ABI v3"), DescribeBinding->AbiVersion, 3);
    TestTrue(TEXT("Static binding owns library routing"), DescribeBinding->bStatic);
    TestEqual(
        TEXT("Typed enum binding retains one argument contract"),
        EchoModeBinding->ParameterTypes.Num(),
        1);
    TestEqual(
        TEXT("Typed enum return retains exact Unreal path"),
        EchoModeBinding->ReturnType.UnrealTypePath,
        FString(TEXT("/Script/UEUnifiedScriptRuntime.EUnifiedScriptInteropMode")));
    TestEqual(
        TEXT("General struct binding retains stable generated field order"),
        EchoHitResultBinding->ReturnType.StructFields.Num(),
        17);
    TestEqual(
        TEXT("Common function binding retains four parameter directions"),
        ResolveCommonBinding->ParameterDirections.Num(),
        4);
    TestTrue(
        TEXT("Common function binding retains stable source parameter order"),
        ResolveCommonBinding->ParameterNames ==
            TArray<FName>{TEXT("name"), TEXT("rotation"), TEXT("label"), TEXT("transform")});
    TestTrue(
        TEXT("Common function binding retains ref and out directions"),
        ResolveCommonBinding->ParameterDirections.Num() == 4 &&
        ResolveCommonBinding->ParameterDirections[1] ==
            EUnifiedScriptManagedParameterDirection::InputOutput &&
        ResolveCommonBinding->ParameterDirections[2] ==
            EUnifiedScriptManagedParameterDirection::Output);
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
    const FProperty* ModeProperty =
        FindFProperty<FProperty>(EchoModeFunction, TEXT("value"));
    const FProperty* ModeReturnProperty = EchoModeFunction->GetReturnProperty();
    const FStructProperty* HitProperty =
        FindFProperty<FStructProperty>(EchoHitResultFunction, TEXT("value"));
    const FStructProperty* HitReturnProperty = CastField<FStructProperty>(
        EchoHitResultFunction->GetReturnProperty());
    TestTrue(
        TEXT("Typed enum parameter uses the exact UEnum pin"),
        (CastField<FEnumProperty>(ModeProperty) != nullptr &&
         CastField<FEnumProperty>(ModeProperty)->GetEnum() == StaticEnum<EUnifiedScriptInteropMode>()) ||
        (CastField<FByteProperty>(ModeProperty) != nullptr &&
         CastField<FByteProperty>(ModeProperty)->Enum == StaticEnum<EUnifiedScriptInteropMode>()));
    TestTrue(
        TEXT("Typed enum return uses the exact UEnum pin"),
        (CastField<FEnumProperty>(ModeReturnProperty) != nullptr &&
         CastField<FEnumProperty>(ModeReturnProperty)->GetEnum() == StaticEnum<EUnifiedScriptInteropMode>()) ||
        (CastField<FByteProperty>(ModeReturnProperty) != nullptr &&
         CastField<FByteProperty>(ModeReturnProperty)->Enum == StaticEnum<EUnifiedScriptInteropMode>()));
    TestTrue(
        TEXT("General struct parameter uses the exact FHitResult pin"),
        HitProperty != nullptr && HitProperty->Struct == FHitResult::StaticStruct());
    TestTrue(
        TEXT("General struct return uses the exact FHitResult pin"),
        HitReturnProperty != nullptr &&
            HitReturnProperty->Struct == FHitResult::StaticStruct());
    const FNameProperty* CommonNameProperty =
        FindFProperty<FNameProperty>(ResolveCommonFunction, TEXT("name"));
    const FStructProperty* CommonRotationProperty =
        FindFProperty<FStructProperty>(ResolveCommonFunction, TEXT("rotation"));
    const FTextProperty* CommonLabelProperty =
        FindFProperty<FTextProperty>(ResolveCommonFunction, TEXT("label"));
    const FStructProperty* CommonTransformProperty =
        FindFProperty<FStructProperty>(ResolveCommonFunction, TEXT("transform"));
    const FClassProperty* CommonReturnProperty = CastField<FClassProperty>(
        ResolveCommonFunction->GetReturnProperty());
    TestNotNull(TEXT("UnrealName parameter becomes FName"), CommonNameProperty);
    TestTrue(
        TEXT("UnrealRotator ref parameter keeps native type and direction flags"),
        CommonRotationProperty != nullptr &&
        CommonRotationProperty->Struct == TBaseStructure<FRotator>::Get() &&
        CommonRotationProperty->HasAllPropertyFlags(CPF_OutParm | CPF_ReferenceParm));
    TestTrue(
        TEXT("UnrealText out parameter becomes FText output"),
        CommonLabelProperty != nullptr &&
        CommonLabelProperty->HasAnyPropertyFlags(CPF_OutParm));
    TestTrue(
        TEXT("UnrealTransform parameter becomes FTransform"),
        CommonTransformProperty != nullptr &&
        CommonTransformProperty->Struct == TBaseStructure<FTransform>::Get());
    TestNotNull(TEXT("UnrealClassHandle return becomes UClass"), CommonReturnProperty);

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
        UObject* CallerObject = CallerGeneratedClass->GetDefaultObject();
        for (const FExchangeShape& Shape : ExchangeShapes)
        {
            UFunction* Exchange = GeneratedClass->FindFunctionByName(
                FName(*FString::Printf(TEXT("USF_%s"), Shape.Id)));
            if (!TestNotNull(*FString::Printf(TEXT("%s persists after package reload"), Shape.Name), Exchange)) continue;
            const FProperty* Replacement = FindFProperty<FProperty>(Exchange, TEXT("replacement"));
            const FProperty* Value = FindFProperty<FProperty>(Exchange, TEXT("value"));
            const FProperty* Previous = FindFProperty<FProperty>(Exchange, TEXT("previous"));
            const FProperty* Return = Exchange->GetReturnProperty();
            const FObjectPropertyBase* Context = FindFProperty<FObjectPropertyBase>(Exchange, TEXT("WorldContextObject"));
            if (!TestTrue(TEXT("Exchange signature retains input/ref/out/return pins"),
                    Replacement && Value && Previous && Return && Context)) continue;
            TestTrue(TEXT("Exchange ref pin keeps reference and output flags"),
                Value->HasAllPropertyFlags(CPF_ReferenceParm | CPF_OutParm));
            FStructOnScope Call(Exchange);
            FStructOnScope Before(Exchange);
            void* Memory = Call.GetStructMemory();
            auto Fill = [&](const FProperty& Property, void* Address, bool bReplacement)
            {
                const FString Path = bReplacement ? TEXT("/Game/Test/NewAsset.NewAsset") : TEXT("/Game/Test/OldAsset.OldAsset");
                if (const FArrayProperty* Typed = CastField<FArrayProperty>(&Property))
                {
                    FScriptArrayHelper Array(Typed, Address);
                    Array.AddValues(2);
                    CastFieldChecked<FIntProperty>(Typed->Inner)->SetPropertyValue(Array.GetRawPtr(0), bReplacement ? 42 : 7);
                    CastFieldChecked<FIntProperty>(Typed->Inner)->SetPropertyValue(Array.GetRawPtr(1), bReplacement ? 99 : -3);
                }
                else if (const FSetProperty* SetType = CastField<FSetProperty>(&Property))
                {
                    FScriptSetHelper Set(SetType, Address);
                    const int32 Index = Set.AddDefaultValue_Invalid_NeedsRehash();
                    CastFieldChecked<FNameProperty>(SetType->ElementProp)->SetPropertyValue(Set.GetElementPtr(Index), bReplacement ? TEXT("New") : TEXT("Old"));
                    Set.Rehash();
                }
                else if (const FMapProperty* MapType = CastField<FMapProperty>(&Property))
                {
                    FScriptMapHelper Map(MapType, Address);
                    const int32 Index = Map.AddDefaultValue_Invalid_NeedsRehash();
                    CastFieldChecked<FStrProperty>(MapType->KeyProp)->SetPropertyValue(Map.GetKeyPtr(Index), bReplacement ? TEXT("New") : TEXT("Old"));
                    CastFieldChecked<FSoftObjectProperty>(MapType->ValueProp)->SetPropertyValue(Map.GetValuePtr(Index), FSoftObjectPtr(FSoftObjectPath(Path)));
                    Map.Rehash();
                }
                else
                {
                    CastFieldChecked<FSoftObjectProperty>(&Property)->SetPropertyValue(Address, FSoftObjectPtr(FSoftObjectPath(Path)));
                }
            };
            Context->SetObjectPropertyValue_InContainer(Memory, WorldContext);
            Fill(*Replacement, Replacement->ContainerPtrToValuePtr<void>(Memory), true);
            Fill(*Value, Value->ContainerPtrToValuePtr<void>(Memory), false);
            Value->CopyCompleteValue_InContainer(Before.GetStructMemory(), Memory);
            LibraryObject->ProcessEvent(Exchange, Memory);
            TestTrue(*FString::Printf(TEXT("%s writes replacement to ref"), Shape.Name),
                Value->Identical(Value->ContainerPtrToValuePtr<void>(Memory), Replacement->ContainerPtrToValuePtr<void>(Memory)));
            TestTrue(*FString::Printf(TEXT("%s returns the previous value"), Shape.Name),
                Return->Identical(Return->ContainerPtrToValuePtr<void>(Memory), Value->ContainerPtrToValuePtr<void>(Before.GetStructMemory())));
            TestTrue(*FString::Printf(TEXT("%s writes the previous value to out"), Shape.Name),
                Previous->Identical(Previous->ContainerPtrToValuePtr<void>(Memory), Value->ContainerPtrToValuePtr<void>(Before.GetStructMemory())));
        }

        for (const FExchangeShape& Shape : ExchangeShapes)
        {
            const FName CallerName(*FString::Printf(TEXT("Call_%s"), Shape.Name));
            UFunction* Caller = CallerGeneratedClass->FindFunctionByName(
                CallerName,
                EIncludeSuperFlag::ExcludeSuper);
            if (!TestNotNull(*FString::Printf(TEXT("%s Blueprint VM caller survives package reload"), Shape.Name), Caller))
            {
                continue;
            }
            TestTrue(*FString::Printf(TEXT("%s caller has compiled Blueprint bytecode"), Shape.Name), Caller->Script.Num() > 0);
            TestFalse(*FString::Printf(TEXT("%s caller is not a native shortcut"), Shape.Name), Caller->HasAnyFunctionFlags(FUNC_Native));
            const FProperty* Replacement = FindFProperty<FProperty>(Caller, TEXT("replacement"));
            const FProperty* Value = FindFProperty<FProperty>(Caller, TEXT("value"));
            const FProperty* Previous = FindFProperty<FProperty>(Caller, TEXT("previous"));
            const FProperty* Return = Caller->GetReturnProperty();
            const FProperty* Observed = FindFProperty<FProperty>(
                CallerGeneratedClass,
                BlueprintObservedOutputNames.FindChecked(FName(Shape.Id)));
            const FObjectPropertyBase* Context = FindFProperty<FObjectPropertyBase>(Caller, TEXT("WorldContextObject"));
            if (Context == nullptr)
            {
                Context = FindFProperty<FObjectPropertyBase>(Caller, TEXT("__WorldContext"));
            }
            if (!TestTrue(
                    *FString::Printf(TEXT("%s caller retains typed input/ref/out/return and explicit WorldContext pins"), Shape.Name),
                    Replacement != nullptr && Value != nullptr && Previous != nullptr && Return != nullptr &&
                    Observed != nullptr && Context != nullptr))
            {
                continue;
            }
            const FObjectPropertyBase* KismetContext =
                FindFProperty<FObjectPropertyBase>(Caller, TEXT("__WorldContext"));
            FStructOnScope Call(Caller);
            FStructOnScope Before(Caller);
            void* Memory = Call.GetStructMemory();
            const FString Path = TEXT("/Game/Test/OldAsset.OldAsset");
            const FString ReplacementPath = TEXT("/Game/Test/NewAsset.NewAsset");
            auto Fill = [&](const FProperty& Property, void* Address, bool bReplacement)
            {
                const FString SelectedPath = bReplacement ? ReplacementPath : Path;
                if (const FArrayProperty* Typed = CastField<FArrayProperty>(&Property))
                {
                    FScriptArrayHelper Array(Typed, Address);
                    Array.AddValues(2);
                    CastFieldChecked<FIntProperty>(Typed->Inner)->SetPropertyValue(Array.GetRawPtr(0), bReplacement ? 42 : 7);
                    CastFieldChecked<FIntProperty>(Typed->Inner)->SetPropertyValue(Array.GetRawPtr(1), bReplacement ? 99 : -3);
                }
                else if (const FSetProperty* SetType = CastField<FSetProperty>(&Property))
                {
                    FScriptSetHelper Set(SetType, Address);
                    const int32 Index = Set.AddDefaultValue_Invalid_NeedsRehash();
                    CastFieldChecked<FNameProperty>(SetType->ElementProp)->SetPropertyValue(Set.GetElementPtr(Index), bReplacement ? TEXT("New") : TEXT("Old"));
                    Set.Rehash();
                }
                else if (const FMapProperty* MapType = CastField<FMapProperty>(&Property))
                {
                    FScriptMapHelper Map(MapType, Address);
                    const int32 Index = Map.AddDefaultValue_Invalid_NeedsRehash();
                    CastFieldChecked<FStrProperty>(MapType->KeyProp)->SetPropertyValue(Map.GetKeyPtr(Index), bReplacement ? TEXT("New") : TEXT("Old"));
                    CastFieldChecked<FSoftObjectProperty>(MapType->ValueProp)->SetPropertyValue(Map.GetValuePtr(Index), FSoftObjectPtr(FSoftObjectPath(SelectedPath)));
                    Map.Rehash();
                }
                else
                {
                    CastFieldChecked<FSoftObjectProperty>(&Property)->SetPropertyValue(Address, FSoftObjectPtr(FSoftObjectPath(SelectedPath)));
                }
            };
            Context->SetObjectPropertyValue_InContainer(Memory, WorldContext);
            if (KismetContext != nullptr)
            {
                KismetContext->SetObjectPropertyValue_InContainer(Memory, WorldContext);
            }
            Fill(*Replacement, Replacement->ContainerPtrToValuePtr<void>(Memory), true);
            Fill(*Value, Value->ContainerPtrToValuePtr<void>(Memory), false);
            Value->CopyCompleteValue_InContainer(Before.GetStructMemory(), Memory);
            CallerObject->ProcessEvent(Caller, Memory);
            TestTrue(*FString::Printf(TEXT("%s Blueprint VM writes replacement to ref"), Shape.Name),
                Value->Identical(Value->ContainerPtrToValuePtr<void>(Memory), Replacement->ContainerPtrToValuePtr<void>(Memory)));
            TestTrue(*FString::Printf(TEXT("%s Blueprint VM returns the original value"), Shape.Name),
                Return->Identical(Return->ContainerPtrToValuePtr<void>(Memory), Value->ContainerPtrToValuePtr<void>(Before.GetStructMemory())));
            TestTrue(*FString::Printf(TEXT("%s Blueprint VM writes the original value to its Previous out parameter"), Shape.Name),
                Previous->Identical(
                    Previous->ContainerPtrToValuePtr<void>(Memory),
                    Value->ContainerPtrToValuePtr<void>(Before.GetStructMemory())));
            TestTrue(*FString::Printf(TEXT("%s Blueprint VM writes the original value to out"), Shape.Name),
                Observed->Identical(
                    Observed->ContainerPtrToValuePtr<void>(CallerObject),
                    Value->ContainerPtrToValuePtr<void>(Before.GetStructMemory())));
        }
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

        const FObjectPropertyBase* CommonContextProperty =
            FindFProperty<FObjectPropertyBase>(
                ResolveCommonFunction,
                TEXT("WorldContextObject"));
        if (TestNotNull(TEXT("Common-values context property exists"), CommonContextProperty) &&
            TestNotNull(TEXT("Common-values FName input exists"), CommonNameProperty) &&
            TestNotNull(TEXT("Common-values FRotator ref exists"), CommonRotationProperty) &&
            TestNotNull(TEXT("Common-values FText output exists"), CommonLabelProperty) &&
            TestNotNull(TEXT("Common-values FTransform input exists"), CommonTransformProperty) &&
            TestNotNull(TEXT("Common-values UClass return exists"), CommonReturnProperty))
        {
            FStructOnScope Call(ResolveCommonFunction);
            uint8* Memory = Call.GetStructMemory();
            CommonContextProperty->SetObjectPropertyValue_InContainer(Memory, WorldContext);
            CommonNameProperty->SetPropertyValue_InContainer(Memory, TEXT("Player"));
            *CommonRotationProperty->ContainerPtrToValuePtr<FRotator>(Memory) =
                FRotator(1.0, 2.0, 3.0);
            *CommonTransformProperty->ContainerPtrToValuePtr<FTransform>(Memory) =
                FTransform(
                    FQuat::Identity,
                    FVector(10.0, 20.0, 30.0),
                    FVector::OneVector);
            LibraryObject->ProcessEvent(ResolveCommonFunction, Memory);
            TestEqual(
                TEXT("Static FName/FText/UClass/FRotator/FTransform call reaches CoreCLR"),
                *CommonRotationProperty->ContainerPtrToValuePtr<FRotator>(Memory),
                FRotator::ZeroRotator);
            TestEqual(
                TEXT("Managed out FText writes back into the Blueprint frame"),
                CommonLabelProperty->GetPropertyValue_InContainer(Memory).ToString(),
                FString());
            TestNull(
                TEXT("Managed UClass return preserves a null class handle"),
                CommonReturnProperty->GetObjectPropertyValue_InContainer(Memory));
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

        const FObjectPropertyBase* ModeContextProperty =
            FindFProperty<FObjectPropertyBase>(EchoModeFunction, TEXT("WorldContextObject"));
        auto SetEnumValue = [](const FProperty& Property, void* Container, int64 Value)
        {
            void* Address = Property.ContainerPtrToValuePtr<void>(Container);
            if (const FEnumProperty* EnumProperty = CastField<FEnumProperty>(&Property))
            {
                EnumProperty->GetUnderlyingProperty()->SetIntPropertyValue(Address, Value);
                return true;
            }
            if (const FByteProperty* ByteProperty = CastField<FByteProperty>(&Property))
            {
                ByteProperty->SetPropertyValue(Address, static_cast<uint8>(Value));
                return ByteProperty->Enum != nullptr;
            }
            return false;
        };
        auto GetEnumValue = [](const FProperty& Property, const void* Container, int64& OutValue)
        {
            const void* Address = Property.ContainerPtrToValuePtr<void>(Container);
            if (const FEnumProperty* EnumProperty = CastField<FEnumProperty>(&Property))
            {
                OutValue = EnumProperty->GetUnderlyingProperty()->GetSignedIntPropertyValue(Address);
                return true;
            }
            if (const FByteProperty* ByteProperty = CastField<FByteProperty>(&Property))
            {
                OutValue = ByteProperty->GetPropertyValue(Address);
                return ByteProperty->Enum != nullptr;
            }
            return false;
        };
        if (TestNotNull(TEXT("Static EchoMode context property exists"), ModeContextProperty) &&
            TestNotNull(TEXT("Static EchoMode input property exists"), ModeProperty) &&
            TestNotNull(TEXT("Static EchoMode return property exists"), ModeReturnProperty))
        {
            FStructOnScope Call(EchoModeFunction);
            uint8* Memory = Call.GetStructMemory();
            ModeContextProperty->SetObjectPropertyValue_InContainer(Memory, WorldContext);
            const int64 Expected = static_cast<int64>(EUnifiedScriptInteropMode::Suspended);
            TestTrue(TEXT("Writes the typed enum input"),
                SetEnumValue(*ModeProperty, Memory, Expected));
            LibraryObject->ProcessEvent(EchoModeFunction, Memory);
            int64 Actual = -1;
            TestTrue(TEXT("Reads the typed enum return"),
                GetEnumValue(*ModeReturnProperty, Memory, Actual));
            TestEqual(
                TEXT("Static typed UEnum crosses the managed ABI"),
                Actual,
                Expected);
        }

        const FObjectPropertyBase* HitContextProperty =
            FindFProperty<FObjectPropertyBase>(EchoHitResultFunction, TEXT("WorldContextObject"));
        if (TestNotNull(TEXT("Static EchoHitResult context property exists"), HitContextProperty) &&
            TestNotNull(TEXT("Static EchoHitResult input property exists"), HitProperty) &&
            TestNotNull(TEXT("Static EchoHitResult return property exists"), HitReturnProperty))
        {
            FStructOnScope Call(EchoHitResultFunction);
            uint8* Memory = Call.GetStructMemory();
            HitContextProperty->SetObjectPropertyValue_InContainer(Memory, WorldContext);
            FHitResult Expected;
            Expected.bBlockingHit = true;
            Expected.bStartPenetrating = true;
            Expected.BoneName = TEXT("spine_03");
            Expected.MyBoneName = TEXT("root");
            Expected.Distance = 123.5f;
            Expected.FaceIndex = 17;
            Expected.Item = 8;
            Expected.Location = FVector(11.0, 22.0, 33.0);
            Expected.ImpactPoint = FVector(44.0, 55.0, 66.0);
            Expected.Normal = FVector(0.0, 0.0, 1.0);
            *HitProperty->ContainerPtrToValuePtr<FHitResult>(Memory) = Expected;
            LibraryObject->ProcessEvent(EchoHitResultFunction, Memory);
            const FHitResult& Actual =
                *HitReturnProperty->ContainerPtrToValuePtr<FHitResult>(Memory);
            TestEqual(TEXT("General UStruct preserves bBlockingHit"),
                Actual.bBlockingHit, Expected.bBlockingHit);
            TestEqual(TEXT("General UStruct preserves bStartPenetrating"),
                Actual.bStartPenetrating, Expected.bStartPenetrating);
            TestEqual(TEXT("General UStruct preserves BoneName"),
                Actual.BoneName, Expected.BoneName);
            TestEqual(TEXT("General UStruct preserves Distance"),
                Actual.Distance, Expected.Distance);
            TestEqual(TEXT("General UStruct preserves Location"),
                Actual.Location, Expected.Location);
            TestEqual(TEXT("General UStruct preserves ImpactPoint"),
                Actual.ImpactPoint, Expected.ImpactPoint);
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

    bool bRecreatedRuntimeReady = true;
    if (bRuntimeReady)
    {
        UWorld* RecreatedWorld = UWorld::CreateWorld(EWorldType::Game, false);
        bRecreatedRuntimeReady = TestNotNull(
            TEXT("Recreates a gameplay World after static dispatch teardown"),
            RecreatedWorld);
        if (RecreatedWorld != nullptr)
        {
            RecreatedWorld->AddToRoot();
            UUnifiedScriptWorldSubsystem* RecreatedHost =
                RecreatedWorld->GetSubsystem<UUnifiedScriptWorldSubsystem>();
            UUnifiedScriptComponent* RecreatedContext =
                NewObject<UUnifiedScriptComponent>(RecreatedWorld);
            bRecreatedRuntimeReady =
                TestNotNull(TEXT("Recreated World has a managed host"), RecreatedHost) &&
                TestNotNull(TEXT("Recreated World has a context object"), RecreatedContext);
            if (bRecreatedRuntimeReady)
            {
                RecreatedContext->RegisterComponentWithWorld(RecreatedWorld);
                UObject* CallerObject = CallerGeneratedClass->GetDefaultObject();
                UFunction* Caller = CallerGeneratedClass->FindFunctionByName(
                    TEXT("Call_ExchangeArray"), EIncludeSuperFlag::ExcludeSuper);
                const FProperty* Replacement = Caller != nullptr
                    ? FindFProperty<FProperty>(Caller, TEXT("replacement"))
                    : nullptr;
                const FProperty* Value = Caller != nullptr
                    ? FindFProperty<FProperty>(Caller, TEXT("value"))
                    : nullptr;
                const FProperty* Previous = Caller != nullptr
                    ? FindFProperty<FProperty>(Caller, TEXT("previous"))
                    : nullptr;
                const FProperty* Return = Caller != nullptr
                    ? Caller->GetReturnProperty()
                    : nullptr;
                const FProperty* Observed = FindFProperty<FProperty>(
                    CallerGeneratedClass,
                    BlueprintObservedOutputNames.FindChecked(FName(TEXT("71717171717171717171717171717171"))));
                const FObjectPropertyBase* Context = Caller != nullptr
                    ? FindFProperty<FObjectPropertyBase>(Caller, TEXT("WorldContextObject"))
                    : nullptr;
                if (Context == nullptr && Caller != nullptr)
                {
                    Context = FindFProperty<FObjectPropertyBase>(Caller, TEXT("__WorldContext"));
                }
                const FObjectPropertyBase* KismetContext = Caller != nullptr
                    ? FindFProperty<FObjectPropertyBase>(Caller, TEXT("__WorldContext"))
                    : nullptr;
                bRecreatedRuntimeReady =
                    TestNotNull(TEXT("Recreated World retains the Array VM caller"), Caller) &&
                    TestNotNull(TEXT("Recreated Array VM caller retains its context"), Context) &&
                    TestNotNull(TEXT("Recreated Array VM caller retains its ref pin"), Value) &&
                    TestNotNull(TEXT("Recreated Array VM caller retains its out pin"), Previous) &&
                    TestNotNull(TEXT("Recreated Array VM caller retains its return pin"), Return) &&
                    TestNotNull(TEXT("Recreated Array VM caller retains its observation"), Observed);
                if (bRecreatedRuntimeReady)
                {
                    FStructOnScope Call(Caller);
                    FStructOnScope Before(Caller);
                    void* Memory = Call.GetStructMemory();
                    auto FillArray = [](const FProperty& Property, void* Address, bool bReplacement)
                    {
                        const FArrayProperty* ArrayProperty = CastField<FArrayProperty>(&Property);
                        FScriptArrayHelper Array(ArrayProperty, Address);
                        Array.AddValues(2);
                        FIntProperty* Inner = CastFieldChecked<FIntProperty>(ArrayProperty->Inner);
                        Inner->SetPropertyValue(Array.GetRawPtr(0), bReplacement ? 42 : 7);
                        Inner->SetPropertyValue(Array.GetRawPtr(1), bReplacement ? 99 : -3);
                    };
                    Context->SetObjectPropertyValue_InContainer(Memory, RecreatedContext);
                    if (KismetContext != nullptr)
                    {
                        KismetContext->SetObjectPropertyValue_InContainer(Memory, RecreatedContext);
                    }
                    FillArray(*Replacement, Replacement->ContainerPtrToValuePtr<void>(Memory), true);
                    FillArray(*Value, Value->ContainerPtrToValuePtr<void>(Memory), false);
                    Value->CopyCompleteValue_InContainer(Before.GetStructMemory(), Memory);
                    CallerObject->ProcessEvent(Caller, Memory);
                    TestTrue(
                        TEXT("Recreated World Array VM still writes the ref value"),
                        Value->Identical(
                            Value->ContainerPtrToValuePtr<void>(Memory),
                            Replacement->ContainerPtrToValuePtr<void>(Memory)));
                    TestTrue(
                        TEXT("Recreated World Array VM still writes the caller Previous out"),
                        Previous->Identical(
                            Previous->ContainerPtrToValuePtr<void>(Memory),
                            Value->ContainerPtrToValuePtr<void>(Before.GetStructMemory())));
                    TestTrue(
                        TEXT("Recreated World Array VM still writes the return value"),
                        Return->Identical(
                            Return->ContainerPtrToValuePtr<void>(Memory),
                            Value->ContainerPtrToValuePtr<void>(Before.GetStructMemory())));
                    TestTrue(
                        TEXT("Recreated World Array VM still reaches the downstream observation"),
                        Observed->Identical(
                            Observed->ContainerPtrToValuePtr<void>(CallerObject),
                            Value->ContainerPtrToValuePtr<void>(Before.GetStructMemory())));
                }
                RecreatedContext->UnregisterComponent();
            }
            RecreatedWorld->RemoveFromRoot();
            RecreatedWorld->DestroyWorld(false);
        }
        TestEqual(
            TEXT("Recreated static dispatch World also retires its collectible context"),
            FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
            InitialManagedWorldCount);
    }

    bool bFixtureReceiptWritten = !bFixtureOutputRequested;
    if (bFixtureOutputRequested && bRuntimeReady && bRecreatedRuntimeReady &&
        !HasAnyErrors() &&
        Package != nullptr && Blueprint != nullptr && CallerBlueprint != nullptr &&
        Blueprint->GeneratedClass != nullptr && CallerBlueprint->GeneratedClass != nullptr)
    {
        IFileManager::Get().MakeDirectory(*FixtureOutputRoot, true);
        const TSharedRef<FJsonObject> Receipt = MakeShared<FJsonObject>();
        Receipt->SetStringField(
            TEXT("schemaVersion"),
            TEXT("unifiedscript.function-closure-fixture/v0.1"));
        Receipt->SetStringField(TEXT("packageName"), PackageName);
        Receipt->SetStringField(TEXT("packageFilename"), PackageFilename);
        Receipt->SetStringField(
            TEXT("libraryClassPath"),
            Blueprint->GeneratedClass->GetPathName());
        Receipt->SetStringField(
            TEXT("callerClassPath"),
            CallerBlueprint->GeneratedClass->GetPathName());
        Receipt->SetStringField(TEXT("libraryAssetPath"), Blueprint->GetPathName());
        Receipt->SetStringField(TEXT("callerAssetPath"), CallerBlueprint->GetPathName());
        TArray<TSharedPtr<FJsonValue>> CallerFunctions;
        TArray<TSharedPtr<FJsonValue>> ObservedProperties;
        TArray<TSharedPtr<FJsonValue>> LibraryFunctions;
        for (const FExchangeShape& Shape : ExchangeShapes)
        {
            CallerFunctions.Add(MakeShared<FJsonValueString>(
                FString::Printf(TEXT("Call_%s"), Shape.Name)));
            ObservedProperties.Add(MakeShared<FJsonValueString>(
                FString::Printf(TEXT("Observed_%s"), Shape.Name)));
            LibraryFunctions.Add(MakeShared<FJsonValueString>(
                FString::Printf(TEXT("USF_%s"), Shape.Id)));
        }
        Receipt->SetArrayField(TEXT("callerFunctionNames"), MoveTemp(CallerFunctions));
        Receipt->SetArrayField(TEXT("observedPropertyNames"), MoveTemp(ObservedProperties));
        Receipt->SetArrayField(TEXT("libraryFunctionNames"), MoveTemp(LibraryFunctions));
        FString ReceiptJson;
        const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&ReceiptJson);
        bFixtureReceiptWritten = FJsonSerializer::Serialize(Receipt, Writer) &&
            FFileHelper::SaveStringToFile(
                ReceiptJson,
                *FPaths::Combine(FixtureOutputRoot, TEXT("function-closure-receipt.json")),
                FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
        TestTrue(TEXT("Function closure fixture receipt writes successfully"), bFixtureReceiptWritten);
    }
    bKeepFixture = bFixtureOutputRequested && bFixtureReceiptWritten &&
        bRuntimeReady && bRecreatedRuntimeReady && !HasAnyErrors();
    return bRuntimeReady && bRecreatedRuntimeReady && bFixtureReceiptWritten && !HasAnyErrors();
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptCommonStateMaterializationTest,
    "UnifiedScript.Unreal.ScriptClass.CommonStateMaterialization",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptCommonStateMaterializationTest::RunTest(const FString& Parameters)
{
    const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (!TestTrue(TEXT("UEUnifiedScript plugin is installed"), Plugin.IsValid()))
    {
        return false;
    }

    const FString DemoDirectory = FPaths::Combine(Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
    const FString DeclarationPath = FPaths::Combine(
        DemoDirectory,
        TEXT("UnifiedScript.Unreal.Demo.asset.json"));
    const FString ChunkPath = FPaths::Combine(
        DemoDirectory,
        TEXT("UnifiedScript.Unreal.Demo.uschunk"));
    const FString AssemblyPath = FPaths::Combine(
        DemoDirectory,
        TEXT("UnifiedScript.Unreal.Demo.dll"));
    FString DeclarationJson;
    if (!TestTrue(TEXT("Common State declaration is staged"),
            FFileHelper::LoadFileToString(DeclarationJson, *DeclarationPath)) ||
        !TestTrue(TEXT("Common State chunk is staged"), IFileManager::Get().FileExists(*ChunkPath)) ||
        !TestTrue(TEXT("Common State assembly is staged"), IFileManager::Get().FileExists(*AssemblyPath)))
    {
        return false;
    }

    // The managed state bridge resolves member paths from the script's
    // declared GeneratedClassPath, so the fixture uses that real content
    // identity instead of a transient class with a different path.
    const FString AssetName = TEXT("BP_CommonStateGameplay");
    const FString PackageName = TEXT("/Game/UnifiedScriptDemo/BP_CommonStateGameplay");
    const FString PackageFilename = FPackageName::LongPackageNameToFilename(
        PackageName, FPackageName::GetAssetPackageExtension());
    const bool bHadOriginalPackage = IFileManager::Get().FileExists(*PackageFilename);
    TArray<uint8> OriginalPackageBytes;
    IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
    const bool bOriginalPackageReadOnly = bHadOriginalPackage &&
        PlatformFile.IsReadOnly(*PackageFilename);
    if (bHadOriginalPackage && !FFileHelper::LoadFileToArray(
            OriginalPackageBytes,
            *PackageFilename))
    {
        TestFalse(TEXT("Common State fixture refuses to overwrite an unreadable existing Blueprint"), true);
        return false;
    }
    if (bHadOriginalPackage && bOriginalPackageReadOnly &&
        !PlatformFile.SetReadOnly(*PackageFilename, false))
    {
        TestFalse(TEXT("Common State fixture can make its existing Blueprint temporarily writable"), true);
        return false;
    }
    ON_SCOPE_EXIT
    {
        PlatformFile.SetReadOnly(*PackageFilename, false);
        if (bHadOriginalPackage)
        {
            if (!FFileHelper::SaveArrayToFile(OriginalPackageBytes, *PackageFilename))
            {
                AddError(TEXT("Common State fixture failed to restore the existing Blueprint bytes."));
            }
            if (!PlatformFile.SetReadOnly(*PackageFilename, bOriginalPackageReadOnly))
            {
                AddError(TEXT("Common State fixture failed to restore the existing Blueprint read-only flag."));
            }
        }
        else if (IFileManager::Get().FileExists(*PackageFilename) &&
            !IFileManager::Get().Delete(*PackageFilename, false, true, true))
        {
            AddError(TEXT("Common State fixture failed to remove its temporary Blueprint."));
        }
    };
    UPackage* Package = CreatePackage(*PackageName);
    UUnifiedScriptBlueprint* Blueprint = Cast<UUnifiedScriptBlueprint>(
        FKismetEditorUtilities::CreateBlueprint(
            AUnifiedScriptActor::StaticClass(),
            Package,
            FName(*AssetName),
            BPTYPE_Normal,
            UUnifiedScriptBlueprint::StaticClass(),
            UUnifiedScriptBlueprintGeneratedClass::StaticClass(),
            FName(TEXT("UnifiedScriptCommonStateMaterialization"))));
    if (!TestNotNull(TEXT("Common State Blueprint is created"), Blueprint) ||
        !TestNotNull(TEXT("Common State Blueprint has a generated class"),
            Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr))
    {
        return false;
    }

    UUnifiedScriptAsset* ScriptAsset = NewObject<UUnifiedScriptAsset>(Package);
    FGuid ScriptClassId;
    FGuid::ParseExact(
        TEXT("8a75f32a3390590852cb9611129bfd08"),
        EGuidFormats::Digits,
        ScriptClassId);
    ScriptAsset->ScriptClassId = ScriptClassId;
    ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
    ScriptAsset->CompiledChunkPath = ChunkPath;
    ScriptAsset->ScriptAssemblyPath = AssemblyPath;
    ScriptAsset->ScriptTypeName = TEXT("UnifiedScript.Unreal.Demo.CommonStateGameplayDemoScript");
    ScriptAsset->DeclarationPath = DeclarationPath;
    ScriptAsset->DeclarationAssetPath = TEXT("Unreal/CommonStateGameplay");
    Blueprint->ScriptAsset = ScriptAsset;

    FUnifiedScriptBlueprintClassSyncResult Result;
    FString Error;
    const bool bInitial = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *Blueprint,
        DeclarationJson,
        false,
        Result,
        Error);
    if (!TestTrue(*FString::Printf(TEXT("Common State defaults materialize: %s"), *Error), bInitial))
    {
        return false;
    }

    const FString SavedBlueprintPath = FPaths::Combine(
        FPaths::ProjectSavedDir(), TEXT("Automation"), AssetName + TEXT(".uasset"));
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(SavedBlueprintPath), true);
    FSavePackageArgs SaveArguments;
    SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
    SaveArguments.SaveFlags = SAVE_NoError;
    TestTrue(TEXT("Common State generated Blueprint saves"), UPackage::SavePackage(
        Package, Blueprint, *SavedBlueprintPath, SaveArguments));
    IFileManager::Get().Delete(*SavedBlueprintPath, false, true, true);

    UClass* GeneratedClass = Blueprint->GeneratedClass;
    UObject* Cdo = GeneratedClass->GetDefaultObject();
    if (AUnifiedScriptActor* CdoActor = Cast<AUnifiedScriptActor>(Cdo))
    {
        CdoActor->GetUnifiedScriptComponent()->ScriptAsset = ScriptAsset;
    }
    else
    {
        TestFalse(TEXT("Common State generated class has an Actor CDO"), true);
        return false;
    }
    FByteProperty* TickGroup = FindFProperty<FByteProperty>(GeneratedClass, TEXT("TickGroup"));
    FStructProperty* Facing = FindFProperty<FStructProperty>(GeneratedClass, TEXT("Facing"));
    FStructProperty* Placement = FindFProperty<FStructProperty>(GeneratedClass, TEXT("Placement"));
    TestNotNull(TEXT("Typed enum state becomes an enum byte property"), TickGroup);
    TestTrue(TEXT("Rotator state uses the native Rotator struct"),
        Facing != nullptr && Facing->Struct == TBaseStructure<FRotator>::Get());
    TestTrue(TEXT("Transform state uses the native Transform struct"),
        Placement != nullptr && Placement->Struct == TBaseStructure<FTransform>::Get());
    if (TickGroup == nullptr || Facing == nullptr || Placement == nullptr)
    {
        return false;
    }
    TestEqual(TEXT("Typed enum default reaches the Blueprint CDO"),
        static_cast<int32>(TickGroup->GetPropertyValue_InContainer(Cdo)), 2);
    FRotator* FacingValue = const_cast<FRotator*>(
        Facing->ContainerPtrToValuePtr<FRotator>(Cdo));
    const FTransform* PlacementValue = Placement->ContainerPtrToValuePtr<FTransform>(Cdo);
    TestEqual(TEXT("Rotator default reaches the Blueprint CDO"), FacingValue->Yaw, 45.0);
    TestTrue(TEXT("Transform default retains a non-identity quaternion"),
        !PlacementValue->GetRotation().Equals(FQuat::Identity, KINDA_SMALL_NUMBER));
    TestEqual(TEXT("Transform default retains its translation"), PlacementValue->GetLocation(), FVector(120, 0, 40));
    TestEqual(TEXT("Transform default retains its non-uniform scale"),
        PlacementValue->GetScale3D(), FVector(1.25, 0.75, 1.0));

    // A source change updates an untouched transform, while explicit CDO
    // overrides remain attached to their stable state identities.
    TickGroup->SetPropertyValue_InContainer(Cdo, 5);
    FacingValue = const_cast<FRotator*>(
        Facing->ContainerPtrToValuePtr<FRotator>(Cdo));
    FacingValue->Yaw = 123.0;

    auto BuildCommonStateDeclaration = [&](const TCHAR* EnumDefault,
                                           double FacingYaw,
                                           double PlacementX,
                                           const TCHAR* EnumPath,
                                           double RotationW) -> FString
    {
        TSharedPtr<FJsonObject> Root;
        const TSharedRef<TJsonReader<>> Reader =
            TJsonReaderFactory<>::Create(DeclarationJson);
        if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
        {
            return FString();
        }
        const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
        if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr)
        {
            return FString();
        }
        for (const TSharedPtr<FJsonValue>& AssetValue : *Assets)
        {
            const TSharedPtr<FJsonObject> Asset = AssetValue->AsObject();
            FString QualifiedName;
            if (!Asset.IsValid() ||
                !Asset->TryGetStringField(TEXT("qualifiedName"), QualifiedName) ||
                QualifiedName != TEXT("UnifiedScript.Unreal.Demo.CommonStateGameplayDemoScript"))
            {
                continue;
            }
            const TArray<TSharedPtr<FJsonValue>>* States = nullptr;
            if (!Asset->TryGetArrayField(TEXT("states"), States) || States == nullptr)
            {
                return FString();
            }
            for (const TSharedPtr<FJsonValue>& StateValue : *States)
            {
                const TSharedPtr<FJsonObject> State = StateValue->AsObject();
                FString Name;
                if (!State.IsValid() || !State->TryGetStringField(TEXT("name"), Name))
                {
                    continue;
                }
                if (Name == TEXT("TickGroup"))
                {
                    State->SetStringField(TEXT("defaultValue"), EnumDefault);
                    if (EnumPath != nullptr)
                    {
                        const TSharedPtr<FJsonObject> UnrealType =
                            State->GetObjectField(TEXT("unrealType"));
                        if (!UnrealType.IsValid())
                        {
                            return FString();
                        }
                        UnrealType->SetStringField(TEXT("unrealTypePath"), EnumPath);
                    }
                }
                else if (Name == TEXT("Facing"))
                {
                    const TSharedPtr<FJsonObject> Default =
                        State->GetObjectField(TEXT("defaultValue"));
                    if (!Default.IsValid())
                    {
                        return FString();
                    }
                    Default->SetNumberField(TEXT("yaw"), FacingYaw);
                }
                else if (Name == TEXT("Placement"))
                {
                    const TSharedPtr<FJsonObject> Default =
                        State->GetObjectField(TEXT("defaultValue"));
                    const TSharedPtr<FJsonObject> Translation =
                        Default.IsValid()
                            ? Default->GetObjectField(TEXT("translation"))
                            : nullptr;
                    if (!Translation.IsValid())
                    {
                        return FString();
                    }
                    Translation->SetNumberField(TEXT("x"), PlacementX);
                    const TSharedPtr<FJsonObject> Rotation =
                        Default->GetObjectField(TEXT("rotation"));
                    if (!Rotation.IsValid())
                    {
                        return FString();
                    }
                    Rotation->SetNumberField(TEXT("w"), RotationW);
                }
            }
            FString Output;
            const TSharedRef<TJsonWriter<>> Writer =
                TJsonWriterFactory<>::Create(&Output);
            return FJsonSerializer::Serialize(Root.ToSharedRef(), Writer)
                ? Output
                : FString();
        }
        return FString();
    };

    const FString ChangedDeclaration = BuildCommonStateDeclaration(
        TEXT("4"), 90.0, 200.0, nullptr, 0.9238795325112867);
    if (!TestTrue(TEXT("Common State declaration mutation produced JSON"), !ChangedDeclaration.IsEmpty()))
    {
        return false;
    }

    FInstancedPropertyBag JsonState;
    TArray<FGuid> JsonOverrideIds;
    FUnifiedScriptStateMigrationReport JsonMigration;
    FString JsonAssetPath;
    FString JsonError;
    const bool bJsonMaterialized =
        FUnifiedScriptDeclarationMaterializer::MaterializeJsonWithOverrides(
            DeclarationJson,
            TEXT("Unreal/CommonStateGameplay"),
            FInstancedPropertyBag(),
            {},
            JsonState,
            JsonOverrideIds,
            JsonMigration,
            JsonAssetPath,
            JsonError);
    TestTrue(*FString::Printf(TEXT("Common State JSON PropertyBag materializes: %s"), *JsonError),
        bJsonMaterialized);
    if (bJsonMaterialized)
    {
        UEnum* TickGroupEnum = LoadObject<UEnum>(nullptr, TEXT("/Script/Engine.ETickingGroup"));
        const FPropertyBagPropertyDesc* TickGroupDesc =
            JsonState.FindPropertyDescByName(TEXT("TickGroup"));
        const auto JsonTickGroup = JsonState.GetValueEnumInt64(
            TEXT("TickGroup"), TickGroupEnum);
        const auto JsonFacing = JsonState.GetValueStruct<FRotator>(TEXT("Facing"));
        const auto JsonPlacement = JsonState.GetValueStruct<FTransform>(TEXT("Placement"));
        TestTrue(TEXT("JSON PropertyBag preserves typed enum identity"),
            TickGroupEnum != nullptr && TickGroupDesc != nullptr &&
            TickGroupDesc->ValueType == EPropertyBagPropertyType::Enum &&
            TickGroupDesc->ValueTypeObject == TickGroupEnum);
        TestTrue(TEXT("JSON PropertyBag preserves typed enum value 2"),
            JsonTickGroup.IsValid() && JsonTickGroup.GetValue() == 2);
        TestTrue(TEXT("JSON PropertyBag preserves Rotator default"),
            JsonFacing.IsValid() && FMath::IsNearlyEqual(JsonFacing.GetValue()->Yaw, 45.0));
        TestTrue(TEXT("JSON PropertyBag preserves non-identity Transform"),
            JsonPlacement.IsValid() &&
            JsonPlacement.GetValue()->GetRotation().Equals(
                FQuat(0.0, 0.0, 0.3826834323650898, 0.9238795325112867), 1e-5) &&
            JsonPlacement.GetValue()->GetLocation().Equals(FVector(120, 0, 40), 1e-4) &&
            JsonPlacement.GetValue()->GetScale3D().Equals(FVector(1.25, 0.75, 1.0), 1e-4));

        FInstancedPropertyBag RejectedJsonState = JsonState;
        TArray<FGuid> RejectedOverrideIds;
        FUnifiedScriptStateMigrationReport RejectedMigration;
        FString RejectedAssetPath;
        FString RejectedError;
        const FString InvalidTransformDeclaration = BuildCommonStateDeclaration(
            TEXT("2"), 45.0, 120.0, nullptr, 0.5);
        const bool bInvalidJson =
            FUnifiedScriptDeclarationMaterializer::MaterializeJsonWithOverrides(
                InvalidTransformDeclaration,
                TEXT("Unreal/CommonStateGameplay"),
                JsonState,
                {},
                RejectedJsonState,
                RejectedOverrideIds,
                RejectedMigration,
                RejectedAssetPath,
                RejectedError);
        TestFalse(TEXT("JSON PropertyBag rejects a non-normalized Transform transactionally"),
            bInvalidJson);
        const auto PreservedPlacement = RejectedJsonState.GetValueStruct<FTransform>(
            TEXT("Placement"));
        TestTrue(TEXT("Rejected JSON Transform leaves the previous bag value unchanged"),
            PreservedPlacement.IsValid() && PreservedPlacement.GetValue()->GetLocation().Equals(
                FVector(120, 0, 40), 1e-4));
    }

    Error.Reset();
    const bool bChanged = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *Blueprint,
        ChangedDeclaration,
        false,
        Result,
        Error);
    if (!TestTrue(*FString::Printf(TEXT("Common State source update succeeds: %s"), *Error), bChanged))
    {
        return false;
    }
    GeneratedClass = Blueprint->GeneratedClass;
    Cdo = GeneratedClass->GetDefaultObject();
    TickGroup = FindFProperty<FByteProperty>(GeneratedClass, TEXT("TickGroup"));
    Facing = FindFProperty<FStructProperty>(GeneratedClass, TEXT("Facing"));
    Placement = FindFProperty<FStructProperty>(GeneratedClass, TEXT("Placement"));
    TestEqual(TEXT("CDO enum override survives a source default change"),
        static_cast<int32>(TickGroup->GetPropertyValue_InContainer(Cdo)), 5);
    TestEqual(TEXT("CDO Rotator override survives a source default change"),
        Facing->ContainerPtrToValuePtr<FRotator>(Cdo)->Yaw, 123.0);
    TestEqual(TEXT("Untouched Transform source default propagates"),
        Placement->ContainerPtrToValuePtr<FTransform>(Cdo)->GetLocation(), FVector(200, 0, 40));

    const uint8 StableTickGroup = TickGroup->GetPropertyValue_InContainer(Cdo);
    const double StableFacingYaw = Facing->ContainerPtrToValuePtr<FRotator>(Cdo)->Yaw;
    const int32 StablePropertyCount = Blueprint->NewVariables.Num();
    const FString InvalidEnumDeclaration = BuildCommonStateDeclaration(
        TEXT("-1"), 90.0, 200.0, nullptr, 0.9238795325112867);
    Error.Reset();
    TestFalse(
        TEXT("Out-of-range byte enum default is rejected transactionally"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            InvalidEnumDeclaration,
            false,
            Result,
            Error));
    TickGroup = FindFProperty<FByteProperty>(Blueprint->GeneratedClass, TEXT("TickGroup"));
    Facing = FindFProperty<FStructProperty>(Blueprint->GeneratedClass, TEXT("Facing"));
    TestEqual(TEXT("Enum rejection preserves the CDO"),
        static_cast<uint8>(TickGroup->GetPropertyValue_InContainer(
            Blueprint->GeneratedClass->GetDefaultObject())), StableTickGroup);
    TestEqual(TEXT("Enum rejection preserves the Rotator CDO override"),
        Facing->ContainerPtrToValuePtr<FRotator>(Blueprint->GeneratedClass->GetDefaultObject())->Yaw,
        StableFacingYaw);
    TestEqual(TEXT("Enum rejection preserves the Blueprint variable set"),
        Blueprint->NewVariables.Num(), StablePropertyCount);

    const FString InvalidTypeDeclaration = BuildCommonStateDeclaration(
        TEXT("4"), 90.0, 200.0, TEXT("/Script/Engine.DoesNotExist"),
        0.9238795325112867);
    Error.Reset();
    TestFalse(
        TEXT("Unknown typed enum identity is rejected transactionally"),
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            InvalidTypeDeclaration,
            false,
            Result,
            Error));
    TestEqual(TEXT("Type rejection preserves the Blueprint variable set"),
        Blueprint->NewVariables.Num(), StablePropertyCount);

    // A successful recompile can replace the generated CDO. Rebind the
    // sidecar on the final CDO before exercising the real managed lifecycle.
    if (AUnifiedScriptActor* FinalCdo = Cast<AUnifiedScriptActor>(
            Blueprint->GeneratedClass->GetDefaultObject()))
    {
        FinalCdo->GetUnifiedScriptComponent()->ScriptAsset = ScriptAsset;
    }

    const int32 InitialManagedWorldCount =
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Common State gameplay World is created"), World))
    {
        return false;
    }
    World->AddToRoot();
    World->InitializeActorsForPlay(FURL());
    AUnifiedScriptActor* Actor = Cast<AUnifiedScriptActor>(World->SpawnActor(GeneratedClass));
    UUnifiedScriptWorldSubsystem* Host = World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    if (!TestNotNull(TEXT("Common State gameplay Actor spawns"), Actor) ||
        !TestNotNull(TEXT("Common State gameplay World owns a managed host"), Host))
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }
    UUnifiedScriptComponent* Component = Actor->ScriptComponent;
    FRotator* ActorFacingValue = const_cast<FRotator*>(
        Facing->ContainerPtrToValuePtr<FRotator>(Actor));
    ActorFacingValue->Yaw = 210.0;
    Component->BeginPlay();
    TestTrue(*FString::Printf(TEXT("Common State managed script starts: %s"), *Component->LastScriptError),
        Component->bScriptRunning);
    TestEqual(TEXT("Managed BeginPlay applies the configured Transform"),
        Actor->GetActorLocation(), FVector(200, 0, 40));
    TestEqual(TEXT("Managed BeginPlay applies the configured Transform scale"),
        Actor->GetActorScale3D(), FVector(1.25, 0.75, 1.0));
    TestEqual(TEXT("Managed BeginPlay applies the typed enum TickGroup"),
        static_cast<uint8>(Actor->PrimaryActorTick.TickGroup), static_cast<uint8>(5));
    TestEqual(TEXT("Managed BeginPlay hydrates the explicit instance Facing override"),
        Facing->ContainerPtrToValuePtr<FRotator>(Actor)->Yaw, 210.0);
    const double FacingBeforeTick =
        Facing->ContainerPtrToValuePtr<FRotator>(Actor)->Yaw;
    Host->BeginManagedFrame();
    Component->TickComponent(1.0f, LEVELTICK_All, nullptr);
    Host->EndManagedFrame();
    TestTrue(*FString::Printf(TEXT("Common State managed tick succeeds: %s"), *Component->LastScriptError),
        Component->bScriptRunning && Component->LastScriptError.IsEmpty());
    TestEqual(TEXT("Managed tick changes the native Facing property"),
        Facing->ContainerPtrToValuePtr<FRotator>(Actor)->Yaw,
        FacingBeforeTick + 10.0);
    TestEqual(TEXT("Managed tick changes the Actor rotation"), Actor->GetActorRotation().Yaw,
        static_cast<double>(FRotator::NormalizeAxis(static_cast<float>(FacingBeforeTick + 10.0))));

    const double BeforeReloadFacing = Facing->ContainerPtrToValuePtr<FRotator>(Actor)->Yaw;
    Error.Reset();
    TestTrue(*FString::Printf(TEXT("Common State managed script reloads: %s"), *Error),
        Component->ReloadScript());
    TestEqual(TEXT("Reload preserves the hydrated Facing value before the next tick"),
        Facing->ContainerPtrToValuePtr<FRotator>(Actor)->Yaw, BeforeReloadFacing);
    Host->BeginManagedFrame();
    Component->TickComponent(1.0f, LEVELTICK_All, nullptr);
    Host->EndManagedFrame();
    TestTrue(*FString::Printf(TEXT("Common State reloaded tick succeeds: %s"), *Component->LastScriptError),
        Component->bScriptRunning && Component->LastScriptError.IsEmpty());
    TestEqual(TEXT("Reloaded script advances the preserved Facing value exactly once"),
        Facing->ContainerPtrToValuePtr<FRotator>(Actor)->Yaw, BeforeReloadFacing + 10.0);
    TestEqual(TEXT("Reloaded script applies the same Facing to Actor rotation"),
        Actor->GetActorRotation().Yaw,
        static_cast<double>(FRotator::NormalizeAxis(static_cast<float>(BeforeReloadFacing + 10.0))));

    Component->EndPlay(EEndPlayReason::Destroyed);
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    TestEqual(TEXT("Common State World cleanup releases the managed context"),
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(), InitialManagedWorldCount);
    return true;
}

namespace UE::UnifiedScript::SoftReferenceTests
{
class FScenario final
{
public:
    FAutomationTestBase& Test;
    const FString PackageName = TEXT("/Game/UnifiedScriptDemo/BP_SoftReferenceGameplay");
    const FString AssetName = TEXT("BP_SoftReferenceGameplay");
    const FString BlueprintPath = PackageName + TEXT(".") + AssetName;
    UPackage* Package = nullptr;
    UUnifiedScriptBlueprint* Blueprint = nullptr;
    UWorld* World = nullptr;
    AUnifiedScriptActor* Actor = nullptr;
    UUnifiedScriptWorldSubsystem* Host = nullptr;
    FString PackageFilename;
    FString DeclarationPath;
    FString ChunkPath;
    FString AssemblyPath;
    bool bHadOriginalPackage = false;
    bool bOriginalPackageReadOnly = false;
    bool bMayRestorePackage = false;
    bool bOwnsTemporaryPackage = false;
    TArray<uint8> OriginalPackageBytes;
    int32 InitialManagedWorldCount = 0;

    explicit FScenario(FAutomationTestBase& InTest)
        : Test(InTest)
    {
    }

    ~FScenario()
    {
        if (Actor != nullptr && !Actor->IsActorBeingDestroyed())
        {
            Actor->Destroy();
        }
        if (World != nullptr)
        {
            World->RemoveFromRoot();
            World->DestroyWorld(false);
            World = nullptr;
        }
        Blueprint = nullptr;
        if (Package != nullptr)
        {
            Package->SetDirtyFlag(false);
            TArray<UPackage*> Packages{Package};
            UPackageTools::UnloadPackages(Packages);
            Package = nullptr;
        }

        IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
        if (bMayRestorePackage && !PackageFilename.IsEmpty() && bHadOriginalPackage)
        {
            PlatformFile.SetReadOnly(*PackageFilename, false);
            if (!FFileHelper::SaveArrayToFile(OriginalPackageBytes, *PackageFilename))
            {
                Test.AddError(TEXT("Soft reference fixture failed to restore the existing Blueprint bytes."));
            }
            if (!PlatformFile.SetReadOnly(*PackageFilename, bOriginalPackageReadOnly))
            {
                Test.AddError(TEXT("Soft reference fixture failed to restore the Blueprint read-only flag."));
            }
        }
        else if (bMayRestorePackage && bOwnsTemporaryPackage && !PackageFilename.IsEmpty() &&
            IFileManager::Get().FileExists(*PackageFilename) &&
            !IFileManager::Get().Delete(*PackageFilename, false, true, true))
        {
            Test.AddError(TEXT("Soft reference fixture failed to remove its temporary Blueprint."));
        }
    }

    bool Build()
    {
        const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
        if (!Test.TestTrue(TEXT("Soft reference plugin is installed"), Plugin.IsValid()))
        {
            return false;
        }
        const FString DemoDirectory = FPaths::Combine(Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
        DeclarationPath = FPaths::Combine(DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.asset.json"));
        ChunkPath = FPaths::Combine(DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.uschunk"));
        AssemblyPath = FPaths::Combine(DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.dll"));
        FString DeclarationJson;
        if (!Test.TestTrue(TEXT("Soft reference declaration is staged"),
                FFileHelper::LoadFileToString(DeclarationJson, *DeclarationPath)) ||
            !Test.TestTrue(TEXT("Soft reference chunk is staged"), IFileManager::Get().FileExists(*ChunkPath)) ||
            !Test.TestTrue(TEXT("Soft reference assembly is staged"), IFileManager::Get().FileExists(*AssemblyPath)))
        {
            return false;
        }

        PackageFilename = FPackageName::LongPackageNameToFilename(
            PackageName, FPackageName::GetAssetPackageExtension());
        if (!Test.TestTrue(TEXT("Soft reference fixture has a valid package filename"),
                !PackageFilename.IsEmpty()))
        {
            return false;
        }
        if (!Test.TestTrue(TEXT("Soft reference fixture has no loaded user Blueprint"),
                FindPackage(nullptr, *PackageName) == nullptr))
        {
            return false;
        }
        bHadOriginalPackage = IFileManager::Get().FileExists(*PackageFilename);
        IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
        bOriginalPackageReadOnly = bHadOriginalPackage && PlatformFile.IsReadOnly(*PackageFilename);
        if (bHadOriginalPackage &&
            !Test.TestTrue(TEXT("Soft reference fixture can read the existing Blueprint bytes"),
                FFileHelper::LoadFileToArray(OriginalPackageBytes, *PackageFilename)))
        {
            return false;
        }
        if (bHadOriginalPackage && bOriginalPackageReadOnly &&
            !Test.TestTrue(TEXT("Soft reference fixture can make the existing Blueprint writable"),
                PlatformFile.SetReadOnly(*PackageFilename, false)))
        {
            return false;
        }
        bMayRestorePackage = true;
        bOwnsTemporaryPackage = !bHadOriginalPackage;

        Package = CreatePackage(*PackageName);
        Blueprint = Cast<UUnifiedScriptBlueprint>(FKismetEditorUtilities::CreateBlueprint(
            AUnifiedScriptActor::StaticClass(),
            Package,
            FName(*AssetName),
            BPTYPE_Normal,
            UUnifiedScriptBlueprint::StaticClass(),
            UUnifiedScriptBlueprintGeneratedClass::StaticClass(),
            FName(TEXT("UnifiedScriptSoftReferenceMaterialization"))));
        if (!Test.TestNotNull(TEXT("Soft reference Blueprint is created"), Blueprint) ||
            !Test.TestNotNull(TEXT("Soft reference Blueprint has a generated class"),
                Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr))
        {
            return false;
        }

        UUnifiedScriptAsset* ScriptAsset = NewObject<UUnifiedScriptAsset>(Package);
        FGuid::ParseExact(TEXT("12cc5b929c9135b650045fa1ef9d0797"), EGuidFormats::Digits, ScriptAsset->ScriptClassId);
        ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
        ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
        ScriptAsset->CompiledChunkPath = ChunkPath;
        ScriptAsset->ScriptAssemblyPath = AssemblyPath;
        ScriptAsset->ScriptTypeName = TEXT("UnifiedScript.Unreal.Demo.SoftReferenceGameplayDemoScript");
        ScriptAsset->DeclarationPath = DeclarationPath;
        ScriptAsset->DeclarationAssetPath = TEXT("Unreal/SoftReferenceGameplay");
        Blueprint->ScriptAsset = ScriptAsset;

        FUnifiedScriptBlueprintClassSyncResult Result;
        FString Error;
        if (!Test.TestTrue(*FString::Printf(TEXT("Soft reference states materialize: %s"), *Error),
                FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                    *ScriptAsset, *Blueprint, DeclarationJson, false, Result, Error)))
        {
            return false;
        }

        FSoftObjectProperty* Mesh = FindFProperty<FSoftObjectProperty>(
            Blueprint->GeneratedClass, TEXT("PreviewMesh"));
        FSoftClassProperty* ActorClass = FindFProperty<FSoftClassProperty>(
            Blueprint->GeneratedClass, TEXT("PreviewActorClass"));
        UObject* Cdo = Blueprint->GeneratedClass->GetDefaultObject();
        Test.TestTrue(TEXT("Soft object state materializes as FSoftObjectProperty"), Mesh != nullptr);
        Test.TestTrue(TEXT("Soft class state materializes as FSoftClassProperty"), ActorClass != nullptr);
        if (Mesh == nullptr || ActorClass == nullptr)
        {
            return false;
        }

        // Exercise the JSON PropertyBag consumer separately from the Blueprint
        // class materializer. Soft paths must remain paths in this phase; this
        // call must not resolve either asset.
        FInstancedPropertyBag SoftJsonState;
        TArray<FGuid> SoftJsonOverrideIds;
        FUnifiedScriptStateMigrationReport SoftJsonMigration;
        FString SoftJsonAssetPath;
        FString SoftJsonError;
        const bool bSoftJsonMaterialized =
            FUnifiedScriptDeclarationMaterializer::MaterializeJsonWithOverrides(
                DeclarationJson,
                TEXT("Unreal/SoftReferenceGameplay"),
                FInstancedPropertyBag(),
                {},
                SoftJsonState,
                SoftJsonOverrideIds,
                SoftJsonMigration,
                SoftJsonAssetPath,
                SoftJsonError);
        Test.TestTrue(*FString::Printf(TEXT("Soft reference JSON PropertyBag materializes: %s"), *SoftJsonError),
            bSoftJsonMaterialized);
        if (!bSoftJsonMaterialized)
        {
            return false;
        }
        const FPropertyBagPropertyDesc* SoftMeshDesc =
            SoftJsonState.FindPropertyDescByName(TEXT("PreviewMesh"));
        const FPropertyBagPropertyDesc* SoftClassDesc =
            SoftJsonState.FindPropertyDescByName(TEXT("PreviewActorClass"));
        const TValueOrError<FSoftObjectPath, EPropertyBagResult> SoftMeshPath =
            SoftJsonState.GetValueSoftPath(TEXT("PreviewMesh"));
        const TValueOrError<FSoftObjectPath, EPropertyBagResult> SoftClassPath =
            SoftJsonState.GetValueSoftPath(TEXT("PreviewActorClass"));
        Test.TestTrue(TEXT("Soft JSON PropertyBag keeps exact soft object/class descriptors"),
            SoftMeshDesc != nullptr && SoftClassDesc != nullptr &&
            SoftMeshDesc->ValueType == EPropertyBagPropertyType::SoftObject &&
            SoftClassDesc->ValueType == EPropertyBagPropertyType::SoftClass);
        Test.TestTrue(TEXT("Soft JSON PropertyBag keeps the object path without loading"),
            SoftMeshPath.IsValid() &&
            SoftMeshPath.GetValue().ToString() == TEXT("/Engine/BasicShapes/Cube.Cube"));
        Test.TestTrue(TEXT("Soft JSON PropertyBag keeps the class path without loading"),
            SoftClassPath.IsValid() &&
            SoftClassPath.GetValue().ToString() == TEXT("/Script/Engine.StaticMeshActor"));

        TSharedPtr<FJsonObject> SoftInvalidRoot;
        const TSharedRef<TJsonReader<>> SoftInvalidReader = TJsonReaderFactory<>::Create(DeclarationJson);
        if (!Test.TestTrue(TEXT("Soft reference JSON parses for transactional validation"),
                FJsonSerializer::Deserialize(SoftInvalidReader, SoftInvalidRoot) && SoftInvalidRoot.IsValid()))
        {
            return false;
        }
        const TArray<TSharedPtr<FJsonValue>>* SoftInvalidAssets = nullptr;
        if (!SoftInvalidRoot->TryGetArrayField(TEXT("assets"), SoftInvalidAssets) || SoftInvalidAssets == nullptr)
        {
            return false;
        }
        for (const TSharedPtr<FJsonValue>& AssetValue : *SoftInvalidAssets)
        {
            const TSharedPtr<FJsonObject> Asset = AssetValue->AsObject();
            FString QualifiedName;
            if (!Asset.IsValid() || !Asset->TryGetStringField(TEXT("qualifiedName"), QualifiedName) ||
                QualifiedName != TEXT("UnifiedScript.Unreal.Demo.SoftReferenceGameplayDemoScript"))
            {
                continue;
            }
            const TArray<TSharedPtr<FJsonValue>>* States = nullptr;
            if (!Asset->TryGetArrayField(TEXT("states"), States) || States == nullptr)
            {
                return false;
            }
            for (const TSharedPtr<FJsonValue>& StateValue : *States)
            {
                const TSharedPtr<FJsonObject> State = StateValue->AsObject();
                FString Name;
                if (State.IsValid() && State->TryGetStringField(TEXT("name"), Name) &&
                    Name == TEXT("PreviewMesh"))
                {
                    State->SetStringField(TEXT("defaultValue"), TEXT("Game/Invalid.Cube"));
                }
            }
        }
        FString InvalidSoftJsonDeclaration;
        {
            const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&InvalidSoftJsonDeclaration);
            if (!Test.TestTrue(TEXT("Invalid soft JSON serializes for rejection"),
                    FJsonSerializer::Serialize(SoftInvalidRoot.ToSharedRef(), Writer)))
            {
                return false;
            }
        }
        FInstancedPropertyBag RejectedSoftJsonState;
        TArray<FGuid> RejectedSoftJsonOverrideIds;
        FUnifiedScriptStateMigrationReport RejectedSoftJsonMigration;
        FString RejectedSoftJsonAssetPath;
        FString RejectedSoftJsonError;
        Test.TestFalse(TEXT("Invalid soft JSON path is rejected transactionally"),
            FUnifiedScriptDeclarationMaterializer::MaterializeJsonWithOverrides(
                InvalidSoftJsonDeclaration,
                TEXT("Unreal/SoftReferenceGameplay"),
                SoftJsonState,
                {},
                RejectedSoftJsonState,
                RejectedSoftJsonOverrideIds,
                RejectedSoftJsonMigration,
                RejectedSoftJsonAssetPath,
                RejectedSoftJsonError));
        const TValueOrError<FSoftObjectPath, EPropertyBagResult> PreservedSoftMeshPath =
            SoftJsonState.GetValueSoftPath(TEXT("PreviewMesh"));
        Test.TestTrue(TEXT("Rejected soft JSON leaves the prior PropertyBag path and overrides unchanged"),
            PreservedSoftMeshPath.IsValid() &&
            PreservedSoftMeshPath.GetValue().ToString() == TEXT("/Engine/BasicShapes/Cube.Cube") &&
            RejectedSoftJsonOverrideIds == SoftJsonOverrideIds);

        const FString LazySoftPath = TEXT("/Game/UnifiedScriptDemo/__NeverLoadedSoftReference.Asset");
        const FString LazyDeclaration = DeclarationJson.Replace(
            TEXT("/Engine/BasicShapes/Cube.Cube"), *LazySoftPath);
        FInstancedPropertyBag LazyJsonState;
        TArray<FGuid> LazyJsonOverrideIds;
        FUnifiedScriptStateMigrationReport LazyJsonMigration;
        FString LazyJsonAssetPath;
        FString LazyJsonError;
        Test.TestTrue(TEXT("A legal missing soft path materializes without loading an asset"),
            FUnifiedScriptDeclarationMaterializer::MaterializeJsonWithOverrides(
                LazyDeclaration,
                TEXT("Unreal/SoftReferenceGameplay"),
                FInstancedPropertyBag(),
                {},
                LazyJsonState,
                LazyJsonOverrideIds,
                LazyJsonMigration,
                LazyJsonAssetPath,
                LazyJsonError));
        const TValueOrError<FSoftObjectPath, EPropertyBagResult> LazyPathValue =
            LazyJsonState.GetValueSoftPath(TEXT("PreviewMesh"));
        Test.TestTrue(TEXT("Legal missing soft path remains exact and unresolved"),
            LazyPathValue.IsValid() && LazyPathValue.GetValue().ToString() == LazySoftPath &&
            FindPackage(nullptr, TEXT("/Game/UnifiedScriptDemo/__NeverLoadedSoftReference")) == nullptr);

        TSharedPtr<FJsonObject> SoftWrongKindRoot;
        const TSharedRef<TJsonReader<>> SoftWrongKindReader = TJsonReaderFactory<>::Create(DeclarationJson);
        if (!Test.TestTrue(TEXT("Soft reference JSON parses for wrong-kind validation"),
                FJsonSerializer::Deserialize(SoftWrongKindReader, SoftWrongKindRoot) && SoftWrongKindRoot.IsValid()))
        {
            return false;
        }
        const TArray<TSharedPtr<FJsonValue>>* SoftWrongKindAssets = nullptr;
        if (!SoftWrongKindRoot->TryGetArrayField(TEXT("assets"), SoftWrongKindAssets) || SoftWrongKindAssets == nullptr)
        {
            return false;
        }
        for (const TSharedPtr<FJsonValue>& AssetValue : *SoftWrongKindAssets)
        {
            const TSharedPtr<FJsonObject> Asset = AssetValue->AsObject();
            FString QualifiedName;
            if (!Asset.IsValid() || !Asset->TryGetStringField(TEXT("qualifiedName"), QualifiedName) ||
                QualifiedName != TEXT("UnifiedScript.Unreal.Demo.SoftReferenceGameplayDemoScript"))
            {
                continue;
            }
            const TArray<TSharedPtr<FJsonValue>>* States = nullptr;
            if (!Asset->TryGetArrayField(TEXT("states"), States) || States == nullptr)
            {
                return false;
            }
            for (const TSharedPtr<FJsonValue>& StateValue : *States)
            {
                const TSharedPtr<FJsonObject> State = StateValue->AsObject();
                FString Name;
                if (State.IsValid() && State->TryGetStringField(TEXT("name"), Name) &&
                    Name == TEXT("PreviewMesh"))
                {
                    State->SetNumberField(TEXT("defaultValue"), 7.0);
                }
            }
        }
        FString WrongKindDeclaration;
        {
            const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&WrongKindDeclaration);
            if (!Test.TestTrue(TEXT("Wrong-kind soft JSON serializes for rejection"),
                    FJsonSerializer::Serialize(SoftWrongKindRoot.ToSharedRef(), Writer)))
            {
                return false;
            }
        }
        FInstancedPropertyBag WrongKindState;
        TArray<FGuid> WrongKindOverrideIds;
        FUnifiedScriptStateMigrationReport WrongKindMigration;
        FString WrongKindAssetPath;
        FString WrongKindError;
        Test.TestFalse(TEXT("Wrong-kind soft JSON is rejected transactionally"),
            FUnifiedScriptDeclarationMaterializer::MaterializeJsonWithOverrides(
                WrongKindDeclaration,
                TEXT("Unreal/SoftReferenceGameplay"),
                SoftJsonState,
                {},
                WrongKindState,
                WrongKindOverrideIds,
                WrongKindMigration,
                WrongKindAssetPath,
                WrongKindError));

        Test.TestEqual(TEXT("Soft object default remains an unloaded path"),
            Mesh->GetPropertyValue_InContainer(Cdo).ToSoftObjectPath().ToString(),
            FString(TEXT("/Engine/BasicShapes/Cube.Cube")));
        Test.TestEqual(TEXT("Soft class default remains an unloaded path"),
            ActorClass->GetPropertyValue_InContainer(Cdo).ToSoftObjectPath().ToString(),
            FString(TEXT("/Script/Engine.StaticMeshActor")));

        Mesh->SetPropertyValue_InContainer(
            Cdo,
            FSoftObjectPtr(FSoftObjectPath(TEXT("/Engine/BasicShapes/Sphere.Sphere"))));
        FString ChangedDeclaration = DeclarationJson;
        TSharedPtr<FJsonObject> Root;
        const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(DeclarationJson);
        if (!Test.TestTrue(TEXT("Soft reference declaration parses for override test"),
                FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid()))
        {
            return false;
        }
        const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
        if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr)
        {
            return false;
        }
        for (const TSharedPtr<FJsonValue>& AssetValue : *Assets)
        {
            const TSharedPtr<FJsonObject> Asset = AssetValue->AsObject();
            FString QualifiedName;
            if (!Asset.IsValid() || !Asset->TryGetStringField(TEXT("qualifiedName"), QualifiedName) ||
                QualifiedName != TEXT("UnifiedScript.Unreal.Demo.SoftReferenceGameplayDemoScript"))
            {
                continue;
            }
            const TArray<TSharedPtr<FJsonValue>>* States = nullptr;
            if (!Asset->TryGetArrayField(TEXT("states"), States) || States == nullptr)
            {
                return false;
            }
            for (const TSharedPtr<FJsonValue>& StateValue : *States)
            {
                const TSharedPtr<FJsonObject> State = StateValue->AsObject();
                FString Name;
                if (!State.IsValid() || !State->TryGetStringField(TEXT("name"), Name))
                {
                    continue;
                }
                if (Name == TEXT("PreviewMesh"))
                {
                    State->SetStringField(TEXT("defaultValue"), TEXT("/Engine/BasicShapes/Cylinder.Cylinder"));
                }
                else if (Name == TEXT("PreviewActorClass"))
                {
                    State->SetStringField(TEXT("defaultValue"), TEXT(""));
                }
            }
        }
        {
            FString Output;
            const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Output);
            if (!Test.TestTrue(TEXT("Soft reference source update serializes"),
                    FJsonSerializer::Serialize(Root.ToSharedRef(), Writer)))
            {
                return false;
            }
            ChangedDeclaration = MoveTemp(Output);
        }
        Error.Reset();
        if (!Test.TestTrue(*FString::Printf(TEXT("Soft reference source update succeeds: %s"), *Error),
                FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                    *ScriptAsset, *Blueprint, ChangedDeclaration, false, Result, Error)))
        {
            return false;
        }
        Cdo = Blueprint->GeneratedClass->GetDefaultObject();
        Mesh = FindFProperty<FSoftObjectProperty>(Blueprint->GeneratedClass, TEXT("PreviewMesh"));
        ActorClass = FindFProperty<FSoftClassProperty>(Blueprint->GeneratedClass, TEXT("PreviewActorClass"));
        Test.TestEqual(TEXT("Soft object CDO override survives source update"),
            Mesh->GetPropertyValue_InContainer(Cdo).ToSoftObjectPath().ToString(),
            FString(TEXT("/Engine/BasicShapes/Sphere.Sphere")));
        Test.TestTrue(TEXT("Soft class source default propagates to an untouched property"),
            ActorClass->GetPropertyValue_InContainer(Cdo).ToSoftObjectPath().IsNull());

        FSoftObjectPath PreservedPath = Mesh->GetPropertyValue_InContainer(Cdo).ToSoftObjectPath();
        FString InvalidDeclaration = ChangedDeclaration.Replace(
            TEXT("/Engine/BasicShapes/Cylinder.Cylinder"), TEXT("Game/Invalid.Cube"));
        Error.Reset();
        Test.TestFalse(TEXT("Invalid soft default is rejected transactionally"),
            FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                *ScriptAsset, *Blueprint, InvalidDeclaration, false, Result, Error));
        Mesh = FindFProperty<FSoftObjectProperty>(Blueprint->GeneratedClass, TEXT("PreviewMesh"));
        Test.TestEqual(TEXT("Rejected soft default preserves the CDO override"),
            Mesh->GetPropertyValue_InContainer(Blueprint->GeneratedClass->GetDefaultObject())
                .ToSoftObjectPath(), PreservedPath);

        IFileManager::Get().MakeDirectory(*FPaths::GetPath(PackageFilename), true);
        FSavePackageArgs SaveArguments;
        SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
        SaveArguments.SaveFlags = SAVE_NoError;
        if (!Test.TestTrue(TEXT("Soft reference generated Blueprint saves"),
                UPackage::SavePackage(Package, Blueprint, *PackageFilename, SaveArguments)))
        {
            return false;
        }

        UClass* GeneratedClass = Blueprint->GeneratedClass;
        InitialManagedWorldCount = FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
        World = UWorld::CreateWorld(EWorldType::Game, false);
        if (!Test.TestNotNull(TEXT("Soft reference gameplay World is created"), World))
        {
            return false;
        }
        World->AddToRoot();
        World->InitializeActorsForPlay(FURL());
        Actor = Cast<AUnifiedScriptActor>(World->SpawnActor(GeneratedClass));
        Host = World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
        if (!Test.TestNotNull(TEXT("Soft reference gameplay Actor spawns"), Actor) ||
            !Test.TestNotNull(TEXT("Soft reference gameplay host exists"), Host))
        {
            return false;
        }
        Mesh = FindFProperty<FSoftObjectProperty>(GeneratedClass, TEXT("PreviewMesh"));
        ActorClass = FindFProperty<FSoftClassProperty>(GeneratedClass, TEXT("PreviewActorClass"));
        Mesh->SetPropertyValue_InContainer(
            Actor,
            FSoftObjectPtr(FSoftObjectPath(TEXT("/Engine/BasicShapes/Sphere.Sphere"))));
        ActorClass->SetPropertyValue_InContainer(
            Actor,
            FSoftObjectPtr(FSoftObjectPath(TEXT("/Script/Engine.StaticMeshActor"))));
        Actor->GetUnifiedScriptComponent()->ScriptAsset = ScriptAsset;
        Actor->GetUnifiedScriptComponent()->BeginPlay();
        Test.TestTrue(TEXT("Soft reference instance override is present before managed hydrate"),
            Mesh->GetPropertyValue_InContainer(Actor).ToSoftObjectPath().ToString() ==
                TEXT("/Engine/BasicShapes/Sphere.Sphere"));
        Test.TestEqual(TEXT("Soft reference managed World is registered"),
            FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(), InitialManagedWorldCount + 1);
        return true;
    }

    void Pump()
    {
        if (Host != nullptr && Actor != nullptr && Actor->GetUnifiedScriptComponent() != nullptr)
        {
            Host->BeginManagedFrame();
            Actor->GetUnifiedScriptComponent()->TickComponent(0.016f, LEVELTICK_All, nullptr);
            Host->EndManagedFrame();
        }
    }

    int32 PreviewCount() const
    {
        int32 Count = 0;
        if (World != nullptr)
        {
            for (TActorIterator<AStaticMeshActor> It(World); It; ++It)
            {
                ++Count;
            }
        }
        return Count;
    }

    FString PreviewPath() const
    {
        if (World == nullptr)
        {
            return FString();
        }
        for (TActorIterator<AStaticMeshActor> It(World); It; ++It)
        {
            if (It->GetStaticMeshComponent()->GetStaticMesh() != nullptr)
            {
                return It->GetStaticMeshComponent()->GetStaticMesh()->GetPathName();
            }
        }
        return FString();
    }

    bool FinishWorld()
    {
        if (Actor != nullptr)
        {
            Actor->GetUnifiedScriptComponent()->EndPlay(EEndPlayReason::Destroyed);
            Actor->Destroy();
            Actor = nullptr;
        }
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        World = nullptr;
        Test.TestEqual(TEXT("Soft reference World cleanup releases managed state"),
            FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(), InitialManagedWorldCount);
        return !Test.HasAnyErrors();
    }
};

class FRun final : public IAutomationLatentCommand
{
    TSharedPtr<FScenario> Scenario;
    FAutomationTestBase& Test;
    double Started = FPlatformTime::Seconds();
    int32 Phase = 0;

public:
    FRun(const TSharedPtr<FScenario>& InScenario, FAutomationTestBase& InTest)
        : Scenario(InScenario), Test(InTest)
    {
    }

    virtual bool Update() override
    {
        FScenario& S = *Scenario;
        if (FPlatformTime::Seconds() - Started > 45.0)
        {
            Test.AddError(FString::Printf(
                TEXT("Soft reference gameplay timed out in phase %d (state=%s,error=%s)."),
                Phase,
                S.Actor != nullptr ? *S.Actor->GetUnifiedScriptComponent()->ManagedState : TEXT("<none>"),
                S.Actor != nullptr ? *S.Actor->GetUnifiedScriptComponent()->LastScriptError : TEXT("<none>")));
            Scenario.Reset();
            return true;
        }
        S.Pump();
        UUnifiedScriptComponent* Component = S.Actor != nullptr
            ? S.Actor->GetUnifiedScriptComponent()
            : nullptr;
        if (Component == nullptr)
        {
            Scenario.Reset();
            return true;
        }
        const FString State = Component->ManagedState;
        if (Phase == 0)
        {
            if (State != TEXT("SoftReferences:Loaded"))
            {
                return false;
            }
            Test.TestEqual(TEXT("Instance soft override loads the requested Sphere"),
                S.PreviewPath(), FString(TEXT("/Engine/BasicShapes/Sphere.Sphere")));
            Test.TestEqual(TEXT("One preview Actor is spawned after async load"), S.PreviewCount(), 1);
            FUnifiedScriptManagedFunctionValueV1 Value;
            FString Error;
            Test.TestTrue(TEXT("Managed soft state mutator is callable"),
                Component->InvokeManagedScriptFunction(TEXT("UseAlternatePreviewMesh"), {}, Value, Error));
            Phase = 1;
            return false;
        }
        if (Phase == 1)
        {
            if (State != TEXT("SoftReferences:Loaded"))
            {
                return false;
            }
            Test.TestEqual(TEXT("Managed flush and retry replace the preview with Cube"),
                S.PreviewPath(), FString(TEXT("/Engine/BasicShapes/Cube.Cube")));
            Test.TestEqual(TEXT("Retry keeps exactly one preview Actor"), S.PreviewCount(), 1);
            FSoftObjectProperty* Mesh = FindFProperty<FSoftObjectProperty>(
                S.Actor->GetClass(), TEXT("PreviewMesh"));
            Test.TestEqual(TEXT("Managed flush writes the alternate soft object path"),
                Mesh->GetPropertyValue_InContainer(S.Actor).ToSoftObjectPath().ToString(),
                FString(TEXT("/Engine/BasicShapes/Cube.Cube")));
            Mesh->SetPropertyValue_InContainer(
                S.Actor,
                FSoftObjectPtr(FSoftObjectPath(TEXT("/Script/Engine.Actor"))));
            Test.TestTrue(TEXT("Reload is accepted for the invalid asset type retry"), Component->ReloadScript());
            Phase = 2;
            return false;
        }
        if (Phase == 2)
        {
            if (!State.StartsWith(TEXT("SoftReferences:Error:")))
            {
                return false;
            }
            Test.TestEqual(TEXT("Failed soft load cleans the previous preview Actor"), S.PreviewCount(), 0);
            FSoftObjectProperty* Mesh = FindFProperty<FSoftObjectProperty>(
                S.Actor->GetClass(), TEXT("PreviewMesh"));
            Mesh->SetPropertyValue_InContainer(
                S.Actor,
                FSoftObjectPtr(FSoftObjectPath(TEXT("/Engine/BasicShapes/Sphere.Sphere"))));
            Test.TestTrue(TEXT("Reload retries after correcting the soft object path"), Component->ReloadScript());
            Phase = 3;
            return false;
        }
        if (Phase == 3)
        {
            if (State != TEXT("SoftReferences:Loaded"))
            {
                return false;
            }
            Test.TestEqual(TEXT("Corrected soft path loads again"),
                S.PreviewPath(), FString(TEXT("/Engine/BasicShapes/Sphere.Sphere")));
            Test.TestEqual(TEXT("Corrected retry leaves one preview Actor"), S.PreviewCount(), 1);
            Test.TestTrue(TEXT("Managed soft reference cleanup reaches World teardown"), S.FinishWorld());
            Scenario.Reset();
            return true;
        }
        return false;
    }
};
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptSoftReferenceMaterializationTest,
    "UnifiedScript.Unreal.ScriptClass.SoftReferenceMaterialization",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptSoftReferenceMaterializationTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::SoftReferenceTests;
    TSharedPtr<FScenario> Scenario = MakeShared<FScenario>(*this);
    if (!Scenario->Build())
    {
        return false;
    }
    ADD_LATENT_AUTOMATION_COMMAND(FRun(Scenario, *this));
    return true;
}

#endif

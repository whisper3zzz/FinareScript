#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptBlueprintClassMaterializer.h"

#include "EdGraphSchema_K2.h"
#include "Engine/EngineTypes.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "K2Node_CallParentFunction.h"
#include "K2Node_Event.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "HAL/FileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/PackageName.h"
#include "Misc/ScopeExit.h"
#include "PackageTools.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptClassBases.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptInteropFixture.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
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
    const TCHAR* MetadataType,
    const FString& UnrealType = FString())
{
    return FString::Printf(
        TEXT(R"JSON({
        "id": "us_%s",
        "name": "%s",
        "typeMetadataName": "%s"%s
      })JSON"),
        Id,
        Name,
        MetadataType,
        UnrealType.IsEmpty()
            ? TEXT("")
            : *FString::Printf(TEXT(",\n        \"unrealType\": %s"), *UnrealType));
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
    bool bBlueprintEvent = false)
{
    return FString::Printf(
        TEXT(R"JSON({
      "id": "us_%s",
      "name": "%s",
      "qualifiedName": "Tests.StateActor.%s()",
      "returnTypeMetadataName": "%s"%s,
      "isStatic": %s,
      "isPublic": %s,
      "isEvent": %s%s,
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
    TestEqual(TEXT("Seven reviewed types become properties"), Result.AddedPropertyCount, 7);
    TestEqual(TEXT("Seven states are declared"), Result.DeclaredStateCount, 7);
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
    constexpr const TCHAR* ActivationFunctionId =
        TEXT("6c4d746b47fc4fa8ae54f6d4e3ce1a01");
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
    const FString ManagedFunctions = FString::Join(
        TArray<FString>{RewireFunction, ActivationFunction},
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

    FUnifiedScriptBlueprintClassSyncResult SyncResult;
    FString Error;
    const bool bSynchronized =
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ScriptAsset,
            *Blueprint,
            Document(
                InitialValueState,
                ManagedFunctions,
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
    TestTrue(TEXT("Live managed dispatch remains a Blueprint event"),
        ManagedFunction->HasAllFunctionFlags(FUNC_Event | FUNC_BlueprintEvent));
    const UUnifiedScriptBlueprintGeneratedClass* DispatchGeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
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
    UUnifiedScriptComponent* Component =
        NewObject<UUnifiedScriptComponent>(World, ChildBlueprint->GeneratedClass);
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
    const FString PackageName = TEXT("/Game/UnifiedScriptTests/") + AssetName;
    const FString PackageFilename = FPackageName::LongPackageNameToFilename(
        PackageName,
        FPackageName::GetAssetPackageExtension());
    ON_SCOPE_EXIT
    {
        if (UPackage* ExistingPackage = FindPackage(nullptr, *PackageName))
        {
            ExistingPackage->SetDirtyFlag(false);
            TArray<UPackage*> PackagesToUnload{ExistingPackage};
            UPackageTools::UnloadPackages(PackagesToUnload);
        }
        IFileManager::Get().Delete(*PackageFilename, false, true, true);
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

    constexpr const TCHAR* LibraryClassId = TEXT("0fdbad6aa2af414e8d8f073115cf03ab");
    constexpr const TCHAR* LibraryQualifiedName =
        TEXT("UnifiedScript.Unreal.Demo.DemoLibrary");
    constexpr const TCHAR* LibraryAssetPath = TEXT("Unreal/DemoLibrary");
    constexpr const TCHAR* DescribeId = TEXT("10101010101010101010101010101010");
    constexpr const TCHAR* EchoObjectId = TEXT("20202020202020202020202020202020");
    constexpr const TCHAR* OffsetVectorId = TEXT("30303030303030303030303030303030");
    constexpr const TCHAR* EchoModeId = TEXT("40404040404040404040404040404040");
    constexpr const TCHAR* EchoHitResultId = TEXT("50505050505050505050505050505050");

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
    ScriptAsset = Blueprint != nullptr ? Blueprint->ScriptAsset.Get() : nullptr;
    if (!TestNotNull(TEXT("Persistent typed FunctionLibrary package reloads"), Package) ||
        !TestNotNull(TEXT("Persistent typed FunctionLibrary Blueprint reloads"), Blueprint) ||
        !TestNotNull(TEXT("Persistent typed FunctionLibrary Script Asset reloads"), ScriptAsset))
    {
        return false;
    }
    TestEqual(TEXT("Five static functions are materialized"),
        SyncResult.DeclaredFunctionCount, 5);

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
    const FUnifiedScriptManagedFunctionBinding* DescribeBinding =
        GeneratedClass->ManagedFunctionBindings.Find(DescribeInternalName);
    const FUnifiedScriptManagedFunctionBinding* EchoModeBinding =
        GeneratedClass->ManagedFunctionBindings.Find(EchoModeInternalName);
    const FUnifiedScriptManagedFunctionBinding* EchoHitResultBinding =
        GeneratedClass->ManagedFunctionBindings.Find(EchoHitResultInternalName);
    if (!TestNotNull(TEXT("Static Describe UFunction exists"), DescribeFunction) ||
        !TestNotNull(TEXT("Static EchoObject UFunction exists"), EchoObjectFunction) ||
        !TestNotNull(TEXT("Static OffsetVector UFunction exists"), OffsetVectorFunction) ||
        !TestNotNull(TEXT("Static EchoMode UFunction exists"), EchoModeFunction) ||
        !TestNotNull(TEXT("Static EchoHitResult UFunction exists"), EchoHitResultFunction) ||
        !TestNotNull(TEXT("Static Describe binding exists"), DescribeBinding) ||
        !TestNotNull(TEXT("Static EchoMode binding exists"), EchoModeBinding) ||
        !TestNotNull(TEXT("Static EchoHitResult binding exists"), EchoHitResultBinding))
    {
        return false;
    }
    TestTrue(TEXT("Generated FunctionLibrary UFunction is static"),
        DescribeFunction->HasAnyFunctionFlags(FUNC_Static));
    TestTrue(
        TEXT("Persistent FunctionLibrary restores native managed dispatch"),
        GeneratedClass->IsManagedFunctionBound(DescribeInternalName));
    TestEqual(TEXT("Static binding selects ABI v2"), DescribeBinding->AbiVersion, 2);
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
    return bRuntimeReady && !HasAnyErrors();
}

#endif

#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptBlueprintClassMaterializer.h"

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "EdGraph/EdGraph.h"
#include "EdGraphSchema_K2.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "K2Node_Event.h"
#include "K2Node_FunctionEntry.h"
#include "K2Node_FunctionResult.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/Guid.h"
#include "Misc/PackageName.h"
#include "PackageTools.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptEventExtensionFixture.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"

namespace UE::UnifiedScript::BlueprintClass::OverrideTests
{
FString OverrideFunction(
    const TCHAR* Id,
    const TCHAR* Name,
    const TCHAR* ReturnType,
    const FString& Parameters,
    const FString& DeclaringClassPath,
    const TCHAR* NativeFunctionName)
{
    return FString::Printf(
        TEXT(R"JSON({
      "id": "us_%s",
      "name": "%s",
      "returnTypeMetadataName": "%s",
      "isStatic": false,
      "isPublic": true,
      "isEvent": false,
      "blueprintEvent": {
        "superMode": "explicit",
        "override": {
          "declaringTypePath": "%s",
          "functionName": "%s"
        }
      },
      "parameters": [%s]
    })JSON"),
        Id,
        Name,
        ReturnType,
        *DeclaringClassPath,
        NativeFunctionName,
        *Parameters);
}

FString Parameter(
    const TCHAR* Id,
    const TCHAR* Name,
    const TCHAR* Type,
    const TCHAR* Direction = TEXT("input"))
{
    return FString::Printf(
        TEXT(R"JSON({
        "id": "us_%s",
        "name": "%s",
        "typeMetadataName": "%s",
        "direction": "%s"
      })JSON"),
        Id,
        Name,
        Type,
        Direction);
}

FString Document(
    const FString& Functions,
    const FString& DeclaringClassPath,
    const TCHAR* AssetId = TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
    const TCHAR* QualifiedName = TEXT("Tests.OverrideActor"),
    const TCHAR* AssetPath = TEXT("Tests/OverrideActor"))
{
    return FString::Printf(
        TEXT(R"JSON({
  "schemaVersion": "unifiedscript.script-asset/v0.2",
  "sourceCompilationId": "us_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "semanticConfigurationId": "us_cccccccccccccccccccccccccccccccc",
  "assets": [{
    "id": "us_%s",
    "qualifiedName": "%s",
    "assetPath": "%s",
    "states": [],
    "functions": [%s],
    "lifecycleHooks": []
  }]
})JSON"),
        AssetId,
        QualifiedName,
        AssetPath,
        *Functions);
}

UUnifiedScriptBlueprint* CreateBlueprint(
    const FString& PackageName,
    const FString& AssetName,
    UPackage*& OutPackage)
{
    OutPackage = CreatePackage(*PackageName);
    return Cast<UUnifiedScriptBlueprint>(FKismetEditorUtilities::CreateBlueprint(
        AUnifiedScriptEventExtensionFixture::StaticClass(),
        OutPackage,
        FName(*AssetName),
        BPTYPE_Normal,
        UUnifiedScriptBlueprint::StaticClass(),
        UUnifiedScriptBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("UnifiedScriptBlueprintOverrideMaterialization"))));
}

UUnifiedScriptAsset* AttachScriptAsset(
    UUnifiedScriptBlueprint& Blueprint,
    const FGuid& ScriptClassId)
{
    UUnifiedScriptAsset* ScriptAsset = NewObject<UUnifiedScriptAsset>(
        Blueprint.GetOutermost(),
        TEXT("OverrideScriptAsset"));
    ScriptAsset->ScriptClassId = ScriptClassId;
    ScriptAsset->ScriptClassKind = EUnifiedScriptClassKind::Actor;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint.GeneratedClass);
    ScriptAsset->ScriptTypeName = TEXT("Tests.OverrideActor");
    ScriptAsset->DeclarationAssetPath = TEXT("Tests/OverrideActor");
    Blueprint.ScriptAsset = ScriptAsset;
    return ScriptAsset;
}

const UEdGraph* FindFunctionGraph(
    const UBlueprint& Blueprint,
    FName FunctionName)
{
    for (const UEdGraph* Graph : Blueprint.FunctionGraphs)
    {
        if (Graph != nullptr && Graph->GetFName() == FunctionName)
        {
            return Graph;
        }
    }
    return nullptr;
}

} // namespace UE::UnifiedScript::BlueprintClass::OverrideTests

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptBlueprintOverrideMaterializerTest,
    "UnifiedScript.Unreal.ScriptClass.BlueprintInheritedEventMaterialization",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptBlueprintOverrideMaterializerTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::BlueprintClass::OverrideTests;

    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString AssetName = TEXT("BP_UnifiedScriptOverride_") + Suffix;
    const FString PackageName = TEXT("/Game/UnifiedScriptOverrideFixture_") + Suffix;
    const FString PackageFilename = FPackageName::LongPackageNameToFilename(
        PackageName,
        FPackageName::GetAssetPackageExtension());
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(PackageFilename), true);
    TArray<FString> OwnedPackageFilenames{PackageFilename};
    ON_SCOPE_EXIT
    {
        for (const FString& OwnedPackageFilename : OwnedPackageFilenames)
        {
            IFileManager::Get().Delete(*OwnedPackageFilename, false, true, true);
        }
    };

    UPackage* Package = nullptr;
    UUnifiedScriptBlueprint* Blueprint =
        CreateBlueprint(PackageName, AssetName, Package);
    if (!TestNotNull(TEXT("Creates Blueprint with the native event fixture parent"), Blueprint) ||
        !TestNotNull(TEXT("Creates the generated proxy class"),
            Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr))
    {
        return false;
    }

    const FString DeclaringClassPath =
        AUnifiedScriptEventExtensionFixture::StaticClass()->GetPathName();
    const FString EvaluateParameters = FString::Join(
        TArray<FString>{
            Parameter(TEXT("11111111111111111111111111111111"), TEXT("ManagedInput"), TEXT("System.Int32")),
            Parameter(TEXT("22222222222222222222222222222222"), TEXT("ManagedAccumulator"), TEXT("System.Int32"), TEXT("inputOutput")),
            Parameter(TEXT("33333333333333333333333333333333"), TEXT("ManagedOutput"), TEXT("System.Int32"), TEXT("output"))},
        TEXT(","));
    const FString ReceiveParameters = FString::Join(
        TArray<FString>{
            Parameter(TEXT("44444444444444444444444444444444"), TEXT("ManagedText"), TEXT("System.String")),
            Parameter(TEXT("55555555555555555555555555555555"), TEXT("ManagedActor"), TEXT("UnifiedScript.Unreal.UnrealObjectHandle"))},
        TEXT(","));
    const FString Functions = FString::Join(
        TArray<FString>{
            OverrideFunction(
                TEXT("88888888888888888888888888888888"),
                TEXT("ManagedEvaluate"),
                TEXT("System.Int32"),
                EvaluateParameters,
                DeclaringClassPath,
                TEXT("EvaluateInherited")),
            OverrideFunction(
                TEXT("99999999999999999999999999999999"),
                TEXT("ManagedReceive"),
                TEXT("System.Void"),
                ReceiveParameters,
                DeclaringClassPath,
                TEXT("ReceiveInherited"))},
        TEXT(","));
    const FGuid ScriptClassId(
        0xaaaaaaaa,
        0xaaaaaaaa,
        0xaaaaaaaa,
        0xaaaaaaaa);
    UUnifiedScriptAsset* ScriptAsset = AttachScriptAsset(*Blueprint, ScriptClassId);

    FUnifiedScriptBlueprintClassSyncResult Result;
    FString Error;
    const FString Declaration = Document(Functions, DeclaringClassPath);
    const bool bInitial = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *Blueprint,
        Declaration,
        false,
        Result,
        Error);
    if (!TestTrue(*FString::Printf(TEXT("Inherited event materialization succeeds: %s"), *Error), bInitial))
    {
        return false;
    }
    TestEqual(TEXT("Two inherited events are declared"), Result.DeclaredFunctionCount, 2);
    TestEqual(TEXT("Two inherited event graphs are created"), Result.AddedFunctionCount, 2);
    TestEqual(TEXT("Proxy compiles after inherited event materialization"), Blueprint->Status, BS_UpToDate);

    UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
    if (!TestNotNull(TEXT("Generated class keeps managed binding state"), GeneratedClass))
    {
        return false;
    }
    UFunction* Evaluate = GeneratedClass->FindFunctionByName(
        TEXT("EvaluateInherited"), EIncludeSuperFlag::ExcludeSuper);
    UFunction* Receive = GeneratedClass->FindFunctionByName(
        TEXT("ReceiveInherited"), EIncludeSuperFlag::ExcludeSuper);
    TestNotNull(TEXT("Inherited BlueprintNativeEvent is materialized by native name"), Evaluate);
    TestNotNull(TEXT("Inherited BlueprintImplementableEvent is materialized by native name"), Receive);
    if (Evaluate == nullptr || Receive == nullptr)
    {
        return false;
    }
    TestTrue(TEXT("Materialized inherited event is a Blueprint event"),
        Evaluate->HasAllFunctionFlags(FUNC_Event | FUNC_BlueprintEvent));
    TestTrue(TEXT("Evaluate uses generic managed native dispatch"),
        GeneratedClass->IsManagedFunctionBound(TEXT("EvaluateInherited")));
    TestTrue(TEXT("Receive uses generic managed native dispatch"),
        GeneratedClass->IsManagedFunctionBound(TEXT("ReceiveInherited")));
    FString ActivationContractJson;
    FString ActivationContractError;
    const bool bActivationContract =
        GeneratedClass->BuildBlueprintOverrideActivationContract(
            ActivationContractJson,
            ActivationContractError);
    TestTrue(*FString::Printf(
        TEXT("Native Blueprint override activation contract builds: %s"),
        *ActivationContractError),
        bActivationContract);
    if (bActivationContract)
    {
        TSharedPtr<FJsonObject> ActivationRoot;
        const TSharedRef<TJsonReader<>> ActivationReader =
            TJsonReaderFactory<>::Create(ActivationContractJson);
        const TArray<TSharedPtr<FJsonValue>>* ActivationRoutes = nullptr;
        TestTrue(TEXT("Activation contract JSON parses"),
            FJsonSerializer::Deserialize(ActivationReader, ActivationRoot) &&
                ActivationRoot.IsValid());
        TestTrue(TEXT("Activation contract uses v0.1 schema"),
            ActivationRoot.IsValid() &&
                ActivationRoot->GetStringField(TEXT("schema")) ==
                    TEXT("unifiedscript.blueprint-override-contract/v0.1"));
        TestTrue(TEXT("Activation contract retains both inherited routes"),
            ActivationRoot.IsValid() &&
                ActivationRoot->TryGetArrayField(TEXT("overrides"), ActivationRoutes) &&
                ActivationRoutes != nullptr && ActivationRoutes->Num() == 2);
    }
    const UEdGraph* EvaluateGraph = FindFunctionGraph(*Blueprint, TEXT("EvaluateInherited"));
    const UEdGraph* ReceiveGraph = FindFunctionGraph(*Blueprint, TEXT("ReceiveInherited"));
    TestNotNull(TEXT("Evaluate graph uses the inherited native function name"), EvaluateGraph);
    TestNotNull(TEXT("Receive graph uses the inherited native function name"), ReceiveGraph);
    const FUnifiedScriptManagedFunctionBinding* EvaluateBinding =
        GeneratedClass->ManagedFunctionBindings.Find(TEXT("EvaluateInherited"));
    const FUnifiedScriptManagedFunctionBinding* ReceiveBinding =
        GeneratedClass->ManagedFunctionBindings.Find(TEXT("ReceiveInherited"));
    if (EvaluateGraph != nullptr)
    {
        TestEqual(TEXT("Evaluate graph keeps C# semantic graph identity"),
            EvaluateGraph->GraphGuid,
            FGuid(0x88888888, 0x88888888, 0x88888888, 0x88888888));
        TArray<UK2Node_FunctionEntry*> EntryNodes;
        TArray<UK2Node_FunctionResult*> ResultNodes;
        EvaluateGraph->GetNodesOfClass(EntryNodes);
        EvaluateGraph->GetNodesOfClass(ResultNodes);
        TestEqual(TEXT("Evaluate graph has one inherited entry"), EntryNodes.Num(), 1);
        TestEqual(TEXT("Evaluate graph has one inherited result"), ResultNodes.Num(), 1);
        if (EntryNodes.Num() == 1 && ResultNodes.Num() == 1)
        {
            UEdGraphPin* NativeInput = EntryNodes[0]->FindPin(
                TEXT("NativeInput"), EGPD_Output);
            UEdGraphPin* NativeAccumulator = EntryNodes[0]->FindPin(
                TEXT("NativeAccumulator"), EGPD_Output);
            UEdGraphPin* NativeOutput = ResultNodes[0]->FindPin(
                TEXT("NativeOutput"), EGPD_Input);
            UEdGraphPin* ReturnValue = ResultNodes[0]->FindPin(
                UEdGraphSchema_K2::PN_ReturnValue, EGPD_Input);
            TestNotNull(TEXT("Native input pin exists"), NativeInput);
            TestNotNull(TEXT("Native ref pin exists"), NativeAccumulator);
            TestNotNull(TEXT("Native out pin exists"), NativeOutput);
            TestNotNull(TEXT("Native return pin exists"), ReturnValue);
            if (NativeInput == nullptr || NativeAccumulator == nullptr ||
                NativeOutput == nullptr || ReturnValue == nullptr)
            {
                return false;
            }
            TestEqual(TEXT("Native input name is persisted on entry pin"),
                NativeInput->PinName,
                FName(TEXT("NativeInput")));
            TestEqual(TEXT("Native ref name is persisted on entry pin"),
                NativeAccumulator->PinName,
                FName(TEXT("NativeAccumulator")));
            TestEqual(TEXT("Native out name is persisted on result pin"),
                NativeOutput->PinName,
                FName(TEXT("NativeOutput")));
            TestEqual(TEXT("Native return pin remains the final result pin"),
                ReturnValue->PinName,
                UEdGraphSchema_K2::PN_ReturnValue);
        }
    }
    TestNotNull(TEXT("Evaluate binding is persisted"), EvaluateBinding);
    TestNotNull(TEXT("Receive binding is persisted"), ReceiveBinding);
    if (EvaluateBinding != nullptr)
    {
        TestEqual(TEXT("Binding preserves original declaring class path"),
            EvaluateBinding->BlueprintOverrideDeclaringTypePath,
            DeclaringClassPath);
        TestEqual(TEXT("Binding preserves original native function spelling"),
            EvaluateBinding->BlueprintOverrideFunctionName,
            FString(TEXT("EvaluateInherited")));
        TestEqual(TEXT("Binding preserves native parameter order"),
            EvaluateBinding->ParameterNames,
            TArray<FName>{TEXT("NativeInput"), TEXT("NativeAccumulator"), TEXT("NativeOutput")});
        TestEqual(TEXT("Binding preserves input/ref/out directions"),
            EvaluateBinding->ParameterDirections,
            TArray<EUnifiedScriptManagedParameterDirection>{
                EUnifiedScriptManagedParameterDirection::Input,
                EUnifiedScriptManagedParameterDirection::InputOutput,
                EUnifiedScriptManagedParameterDirection::Output});
    }
    if (ReceiveGraph != nullptr)
    {
        TArray<UK2Node_FunctionEntry*> EntryNodes;
        ReceiveGraph->GetNodesOfClass(EntryNodes);
        TestEqual(TEXT("Receive graph has one inherited entry"), EntryNodes.Num(), 1);
        if (EntryNodes.Num() == 1)
        {
            UEdGraphPin* NativeText = EntryNodes[0]->FindPin(
                TEXT("NativeText"), EGPD_Output);
            UEdGraphPin* NativeActor = EntryNodes[0]->FindPin(
                TEXT("NativeActor"), EGPD_Output);
            TestNotNull(TEXT("Native const text pin exists"), NativeText);
            TestNotNull(TEXT("Native Actor pin exists"), NativeActor);
            if (NativeText == nullptr || NativeActor == nullptr)
            {
                return false;
            }
            TestEqual(TEXT("Receive uses the native text parameter name"),
                NativeText->PinName,
                FName(TEXT("NativeText")));
            const UFunction* ReceiveFunction =
                AUnifiedScriptEventExtensionFixture::StaticClass()->FindFunctionByName(
                    TEXT("ReceiveInherited"), EIncludeSuperFlag::ExcludeSuper);
            const FProperty* NativeTextProperty = ReceiveFunction != nullptr
                ? FindFProperty<FProperty>(ReceiveFunction, TEXT("NativeText"))
                : nullptr;
            FEdGraphPinType NativeTextPinType;
            const bool bNativeTextPinType = NativeTextProperty != nullptr &&
                GetDefault<UEdGraphSchema_K2>()->ConvertPropertyToPinType(
                    NativeTextProperty,
                    NativeTextPinType) &&
                NativeText->PinType == NativeTextPinType;
            TestTrue(TEXT("Native text pin matches reflected property shape"),
                bNativeTextPinType);
            TestEqual(TEXT("Managed binding treats const text as input"),
                ReceiveBinding != nullptr
                    ? ReceiveBinding->ParameterDirections[0]
                    : EUnifiedScriptManagedParameterDirection::Output,
                EUnifiedScriptManagedParameterDirection::Input);
            TestEqual(TEXT("Receive uses the native Actor parameter name"),
                NativeActor->PinName,
                FName(TEXT("NativeActor")));
        }
    }

    FUnifiedScriptBlueprintClassSyncResult ReplayResult;
    Error.Reset();
    const bool bReplay = FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
        *ScriptAsset,
        *Blueprint,
        Declaration,
        false,
        ReplayResult,
        Error);
    TestTrue(*FString::Printf(TEXT("Second inherited event synchronization succeeds: %s"), *Error), bReplay);
    TestFalse(TEXT("Second inherited event synchronization is idempotent"), ReplayResult.bChanged);
    TestEqual(TEXT("Second synchronization creates no graph"), ReplayResult.AddedFunctionCount, 0);
    TestEqual(TEXT("Second synchronization updates no graph"), ReplayResult.UpdatedFunctionCount, 0);

    FSavePackageArgs SaveArguments;
    SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
    SaveArguments.SaveFlags = SAVE_NoError;
    const bool bSaved = UPackage::SavePackage(
        Package,
        Blueprint,
        *PackageFilename,
        SaveArguments);
    TestTrue(TEXT("Inherited event Blueprint saves"), bSaved);
    if (bSaved)
    {
        Package->SetDirtyFlag(false);
        TArray<UPackage*> PackagesToUnload{Package};
        const bool bUnloaded = UPackageTools::UnloadPackages(PackagesToUnload);
        TestTrue(TEXT("Inherited event package unloads for reload"), bUnloaded);
        UPackage* ReloadedPackage = LoadPackage(nullptr, *PackageName, LOAD_None);
        UUnifiedScriptBlueprint* ReloadedBlueprint = ReloadedPackage != nullptr
            ? FindObject<UUnifiedScriptBlueprint>(ReloadedPackage, *AssetName)
            : nullptr;
        TestNotNull(TEXT("Inherited event Blueprint reloads"), ReloadedBlueprint);
        if (ReloadedBlueprint != nullptr)
        {
            UUnifiedScriptBlueprintGeneratedClass* ReloadedClass =
                Cast<UUnifiedScriptBlueprintGeneratedClass>(ReloadedBlueprint->GeneratedClass);
            TestTrue(TEXT("Reloaded Evaluate binding uses generic managed native dispatch"),
                ReloadedClass != nullptr &&
                    ReloadedClass->IsManagedFunctionBound(TEXT("EvaluateInherited")));
            TestTrue(TEXT("Reloaded Receive binding uses generic managed native dispatch"),
                ReloadedClass != nullptr &&
                    ReloadedClass->IsManagedFunctionBound(TEXT("ReceiveInherited")));
            const FUnifiedScriptManagedFunctionBinding* ReloadedBinding =
                ReloadedClass != nullptr
                    ? ReloadedClass->ManagedFunctionBindings.Find(TEXT("EvaluateInherited"))
                    : nullptr;
            TestNotNull(TEXT("Reloaded generated class retains inherited binding"), ReloadedClass);
            TestNotNull(TEXT("Reloaded binding retains native identity"), ReloadedBinding);
            if (ReloadedBinding != nullptr)
            {
                TestEqual(TEXT("Reloaded binding retains original native function spelling"),
                    ReloadedBinding->BlueprintOverrideFunctionName,
                    FString(TEXT("EvaluateInherited")));
            }
            FString ReloadedActivationJson;
            FString ReloadedActivationError;
            TestTrue(*FString::Printf(
                TEXT("Reloaded class rebuilds native activation contract: %s"),
                *ReloadedActivationError),
                ReloadedClass != nullptr &&
                    ReloadedClass->BuildBlueprintOverrideActivationContract(
                        ReloadedActivationJson,
                        ReloadedActivationError));
            TestEqual(
                TEXT("Saved/reloaded activation contract preserves the complete JSON"),
                ReloadedActivationJson,
                ActivationContractJson);

            const FString RetargetFunctions = FString::Join(
                TArray<FString>{
                    OverrideFunction(
                        TEXT("88888888888888888888888888888888"),
                        TEXT("ManagedEvaluate"),
                        TEXT("System.Int32"),
                        EvaluateParameters,
                        DeclaringClassPath,
                        TEXT("EvaluateInheritedAlternate")),
                    OverrideFunction(
                        TEXT("99999999999999999999999999999999"),
                        TEXT("ManagedReceive"),
                        TEXT("System.Void"),
                        ReceiveParameters,
                        DeclaringClassPath,
                        TEXT("ReceiveInherited"))},
                TEXT(","));
            FUnifiedScriptBlueprintClassSyncResult RetargetResult;
            FString RetargetError;
            const bool bRetargeted = ReloadedBlueprint->ScriptAsset != nullptr &&
                FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                    *ReloadedBlueprint->ScriptAsset,
                    *ReloadedBlueprint,
                    Document(RetargetFunctions, DeclaringClassPath),
                    false,
                    RetargetResult,
                    RetargetError);
            TestTrue(*FString::Printf(
                TEXT("Product-owned inherited graph retargets: %s"),
                *RetargetError), bRetargeted);
            TestEqual(TEXT("Retarget updates exactly one inherited graph"),
                RetargetResult.UpdatedFunctionCount,
                1);
            const UEdGraph* RetargetedGraph = FindFunctionGraph(
                *ReloadedBlueprint,
                TEXT("EvaluateInheritedAlternate"));
            TestNotNull(TEXT("Retargeted graph uses the new inherited function name"),
                RetargetedGraph);
            TestNull(TEXT("Retarget removes the old inherited function graph"),
                FindFunctionGraph(*ReloadedBlueprint, TEXT("EvaluateInherited")));
            UUnifiedScriptBlueprintGeneratedClass* RetargetedClass =
                Cast<UUnifiedScriptBlueprintGeneratedClass>(ReloadedBlueprint->GeneratedClass);
            TestTrue(TEXT("Retargeted BNE uses generic managed native dispatch"),
                RetargetedClass != nullptr &&
                    RetargetedClass->IsManagedFunctionBound(
                        TEXT("EvaluateInheritedAlternate")));
            const FUnifiedScriptManagedFunctionBinding* RetargetedBinding =
                RetargetedClass != nullptr
                    ? RetargetedClass->ManagedFunctionBindings.Find(
                        TEXT("EvaluateInheritedAlternate"))
                    : nullptr;
            TestNotNull(TEXT("Retargeted binding is persisted"), RetargetedBinding);
            if (RetargetedBinding != nullptr)
            {
                TestEqual(TEXT("Retargeted binding preserves original native spelling"),
                    RetargetedBinding->BlueprintOverrideFunctionName,
                    FString(TEXT("EvaluateInheritedAlternate")));
            }
        }
    }

    UPackage* CollisionPackage = nullptr;
    const FString CollisionAssetName = TEXT("BP_UnifiedScriptOverrideCollision_") + Suffix;
    const FString CollisionPackageName =
        TEXT("/Game/UnifiedScriptOverrideCollision_") + Suffix;
    OwnedPackageFilenames.Add(FPackageName::LongPackageNameToFilename(
        CollisionPackageName,
        FPackageName::GetAssetPackageExtension()));
    UUnifiedScriptBlueprint* CollisionBlueprint = CreateBlueprint(
        CollisionPackageName,
        CollisionAssetName,
        CollisionPackage);
    UUnifiedScriptAsset* CollisionAsset = CollisionBlueprint != nullptr
        ? AttachScriptAsset(*CollisionBlueprint, ScriptClassId)
        : nullptr;
    UEdGraph* Ubergraph = CollisionBlueprint != nullptr &&
            !CollisionBlueprint->UbergraphPages.IsEmpty()
        ? CollisionBlueprint->UbergraphPages[0]
        : nullptr;
    int32 NodePositionY = 0;
    UK2Node_Event* UserEvent = Ubergraph != nullptr
        ? FKismetEditorUtilities::AddDefaultEventNode(
            CollisionBlueprint,
            Ubergraph,
            TEXT("ReceiveInherited"),
            AUnifiedScriptEventExtensionFixture::StaticClass(),
            NodePositionY)
        : nullptr;
    if (UserEvent != nullptr)
    {
        UserEvent->SetEnabledState(ENodeEnabledState::Enabled, true);
    }
    const int32 NodesBefore = Ubergraph != nullptr ? Ubergraph->Nodes.Num() : 0;
    const bool bDirtyBefore = CollisionPackage != nullptr && CollisionPackage->IsDirty();
    FUnifiedScriptBlueprintClassSyncResult CollisionResult;
    Error.Reset();
    const bool bCollision = CollisionAsset != nullptr &&
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *CollisionAsset,
            *CollisionBlueprint,
            Document(
                OverrideFunction(
                    TEXT("88888888888888888888888888888888"),
                    TEXT("ManagedReceive"),
                    TEXT("System.Void"),
                    ReceiveParameters,
                    DeclaringClassPath,
                    TEXT("ReceiveInherited")),
                DeclaringClassPath),
            false,
            CollisionResult,
            Error);
    TestFalse(*FString::Printf(TEXT("User-owned inherited event collision is rejected: %s"), *Error), bCollision);
    TestTrue(TEXT("Collision diagnostic identifies user ownership"), Error.Contains(TEXT("user-owned")));
    TestNotNull(TEXT("User-owned event remains present after rejection"), UserEvent);
    TestEqual(TEXT("Collision rejection leaves Ubergraph nodes unchanged"),
        Ubergraph != nullptr ? Ubergraph->Nodes.Num() : 0,
        NodesBefore);
    TestEqual(TEXT("Collision rejection leaves package dirty state unchanged"),
        CollisionPackage != nullptr && CollisionPackage->IsDirty(),
        bDirtyBefore);

    // A normal Engine.Actor event is a second parent-chain surface. This
    // proves the declaring class need not be the fixture's immediate parent,
    // while the native function itself is still declared directly by Actor.
    UPackage* ActorEventPackage = nullptr;
    const FString ActorEventAssetName = TEXT("BP_UnifiedScriptActorEvent_") + Suffix;
    const FString ActorEventPackageName =
        TEXT("/Game/UnifiedScriptActorEvent_") + Suffix;
    OwnedPackageFilenames.Add(FPackageName::LongPackageNameToFilename(
        ActorEventPackageName,
        FPackageName::GetAssetPackageExtension()));
    UUnifiedScriptBlueprint* ActorEventBlueprint = CreateBlueprint(
        ActorEventPackageName,
        ActorEventAssetName,
        ActorEventPackage);
    UUnifiedScriptAsset* ActorEventAsset = ActorEventBlueprint != nullptr
        ? AttachScriptAsset(*ActorEventBlueprint, ScriptClassId)
        : nullptr;
    const FString ActorClassPath = AActor::StaticClass()->GetPathName();
    const FString ActorEventParameters = Parameter(
        TEXT("66666666666666666666666666666666"),
        TEXT("ManagedOtherActor"),
        TEXT("UnifiedScript.Unreal.UnrealObjectHandle"));
    if (ActorEventBlueprint != nullptr)
    {
        // CreateBlueprint seeds disabled ghost event nodes. The materializer
        // must distinguish those placeholders from a user-owned event.
    }
    FUnifiedScriptBlueprintClassSyncResult ActorEventResult;
    FString ActorEventError;
    const bool bActorEvent = ActorEventAsset != nullptr &&
        FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
            *ActorEventAsset,
            *ActorEventBlueprint,
            Document(
                OverrideFunction(
                    TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                    TEXT("ManagedActorBeginOverlap"),
                    TEXT("System.Void"),
                    ActorEventParameters,
                    ActorClassPath,
                    TEXT("ReceiveActorBeginOverlap")),
                ActorClassPath),
            false,
            ActorEventResult,
            ActorEventError);
    TestTrue(*FString::Printf(
        TEXT("Engine.Actor inherited event materializes: %s"),
        *ActorEventError), bActorEvent);
    if (bActorEvent && ActorEventBlueprint != nullptr)
    {
        UUnifiedScriptBlueprintGeneratedClass* ActorEventClass =
            Cast<UUnifiedScriptBlueprintGeneratedClass>(ActorEventBlueprint->GeneratedClass);
        TestTrue(TEXT("Engine.Actor event uses generic managed native dispatch"),
            ActorEventClass != nullptr &&
                ActorEventClass->IsManagedFunctionBound(
                    TEXT("ReceiveActorBeginOverlap")));
        TestNotNull(TEXT("Engine.Actor event graph is materialized"),
            FindFunctionGraph(*ActorEventBlueprint, TEXT("ReceiveActorBeginOverlap")));
    }

    int32 RejectedCaseIndex = 0;
    auto RunRejected = [this, &Suffix, &ScriptClassId, &RejectedCaseIndex](
        const TCHAR* Label,
        const FString& Candidate,
        const TCHAR* ExpectedDiagnostic)
    {
        const FString CaseSuffix = Suffix + TEXT("_") +
            FString::FromInt(++RejectedCaseIndex);
        UPackage* CasePackage = nullptr;
        UUnifiedScriptBlueprint* CaseBlueprint = CreateBlueprint(
            TEXT("/Game/UnifiedScriptOverrideRejected_") + CaseSuffix,
            TEXT("BP_UnifiedScriptOverrideRejected_") + CaseSuffix,
            CasePackage);
        UUnifiedScriptAsset* CaseAsset = CaseBlueprint != nullptr
            ? AttachScriptAsset(*CaseBlueprint, ScriptClassId)
            : nullptr;
        UUnifiedScriptBlueprintGeneratedClass* CaseClass = CaseBlueprint != nullptr
            ? Cast<UUnifiedScriptBlueprintGeneratedClass>(CaseBlueprint->GeneratedClass)
            : nullptr;
        const int32 GraphCountBefore = CaseBlueprint != nullptr
            ? CaseBlueprint->FunctionGraphs.Num()
            : 0;
        const int32 BindingCountBefore = CaseClass != nullptr
            ? CaseClass->ManagedFunctionBindings.Num()
            : 0;
        if (CasePackage != nullptr)
        {
            CasePackage->SetDirtyFlag(false);
        }
        FUnifiedScriptBlueprintClassSyncResult CaseResult;
        FString CaseError;
        const bool bAccepted = CaseAsset != nullptr &&
            FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                *CaseAsset,
                *CaseBlueprint,
                Candidate,
                false,
                CaseResult,
                CaseError);
        TestFalse(*FString::Printf(
            TEXT("Rejected override case %s: %s"), Label, *CaseError), bAccepted);
        TestFalse(*FString::Printf(
            TEXT("Rejected override case %s has a diagnostic"), Label),
            CaseError.IsEmpty());
        if (ExpectedDiagnostic != nullptr && *ExpectedDiagnostic != TEXT('\0'))
        {
            TestTrue(*FString::Printf(
                TEXT("Rejected override case %s explains the contract"), Label),
                CaseError.Contains(ExpectedDiagnostic));
        }
        TestEqual(*FString::Printf(
            TEXT("Rejected override case %s leaves graphs unchanged"), Label),
            CaseBlueprint != nullptr ? CaseBlueprint->FunctionGraphs.Num() : 0,
            GraphCountBefore);
        TestEqual(*FString::Printf(
            TEXT("Rejected override case %s leaves bindings unchanged"), Label),
            CaseClass != nullptr ? CaseClass->ManagedFunctionBindings.Num() : 0,
            BindingCountBefore);
        TestFalse(*FString::Printf(
            TEXT("Rejected override case %s leaves package clean"), Label),
            CasePackage != nullptr && CasePackage->IsDirty());
    };

    RunRejected(
        TEXT("unrelated owner"),
        Document(
            OverrideFunction(
                TEXT("11111111111111111111111111111111"),
                TEXT("ManagedActorBeginOverlap"),
                TEXT("System.Void"),
                ActorEventParameters,
                TEXT("/Script/Engine.Pawn"),
                TEXT("ReceiveActorBeginOverlap")),
            TEXT("/Script/Engine.Pawn")),
        TEXT("not its child"));
    RunRejected(
        TEXT("non-direct owner"),
        Document(
            OverrideFunction(
                TEXT("22222222222222222222222222222222"),
                TEXT("ManagedWrongOwner"),
                TEXT("System.Void"),
                ReceiveParameters,
                ActorClassPath,
                TEXT("ReceiveInherited")),
            ActorClassPath),
        TEXT("declared directly"));
    RunRejected(
        TEXT("non-event function"),
        Document(
            OverrideFunction(
                TEXT("33333333333333333333333333333333"),
                TEXT("ManagedStart"),
                TEXT("System.Boolean"),
                TEXT("") ,
                AUnifiedScriptActor::StaticClass()->GetPathName(),
                TEXT("StartManagedBehavior")),
            AUnifiedScriptActor::StaticClass()->GetPathName()),
        TEXT("Blueprint event"));
    const FString DirectionMismatchParameters = EvaluateParameters.Replace(
        TEXT("\"direction\": \"inputOutput\""),
        TEXT("\"direction\": \"input\""));
    RunRejected(
        TEXT("parameter direction mismatch"),
        Document(
            OverrideFunction(
                TEXT("44444444444444444444444444444444"),
                TEXT("ManagedDirectionMismatch"),
                TEXT("System.Int32"),
                DirectionMismatchParameters,
                DeclaringClassPath,
                TEXT("EvaluateInherited")),
            DeclaringClassPath),
        TEXT("parameter"));
    RunRejected(
        TEXT("return mismatch"),
        Document(
            OverrideFunction(
                TEXT("55555555555555555555555555555555"),
                TEXT("ManagedReturnMismatch"),
                TEXT("System.Void"),
                EvaluateParameters,
                DeclaringClassPath,
                TEXT("EvaluateInherited")),
            DeclaringClassPath),
        TEXT("return"));
    RunRejected(
        TEXT("duplicate final native route"),
        Document(
            FString::Join(
                TArray<FString>{
                    OverrideFunction(
                        TEXT("66666666666666666666666666666666"),
                        TEXT("ManagedEvaluateA"),
                        TEXT("System.Int32"),
                        EvaluateParameters,
                        DeclaringClassPath,
                        TEXT("EvaluateInherited")),
                    OverrideFunction(
                        TEXT("77777777777777777777777777777777"),
                        TEXT("ManagedEvaluateB"),
                        TEXT("System.Int32"),
                        EvaluateParameters,
                        DeclaringClassPath,
                        TEXT("EvaluateInherited"))},
                TEXT(",")),
            DeclaringClassPath),
        TEXT("maps multiple"));
    RunRejected(
        TEXT("legacy schema with override"),
        Document(
            OverrideFunction(
                TEXT("88888888888888888888888888888888"),
                TEXT("ManagedEvaluate"),
                TEXT("System.Int32"),
                EvaluateParameters,
                DeclaringClassPath,
                TEXT("EvaluateInherited")),
            DeclaringClassPath).Replace(
                TEXT("unifiedscript.script-asset/v0.2"),
                TEXT("unifiedscript.script-asset/v0.1")),
        TEXT("v0.1"));

    return true;
}

#endif

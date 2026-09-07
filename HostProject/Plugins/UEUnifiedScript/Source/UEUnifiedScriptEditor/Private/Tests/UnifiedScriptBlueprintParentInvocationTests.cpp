#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptBlueprintClassMaterializer.h"

#include "AssetRegistry/AssetData.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "Interfaces/IPluginManager.h"
#include "K2Node_FunctionEntry.h"
#include "K2Node_FunctionResult.h"
#include "K2Node_CallParentFunction.h"
#include "K2Node_VariableSetRef.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/PackageName.h"
#include "Misc/ScopeExit.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UObject/Package.h"
#include "UObject/UnrealType.h"

#include "PackageTools.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptEventExtensionFixture.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptWorldSubsystem.h"

namespace UE::UnifiedScript::BlueprintClass::ParentInvocationTests
{
struct FDemoAsset
{
    FGuid ClassId;
    FString TypeName;
    FString AssetPath;
};

bool ReadDemoAsset(
    const FString& Declaration,
    const FString& TypeName,
    FDemoAsset& OutAsset,
    FString& OutError)
{
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Declaration);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = TEXT("Staged Demo declaration is not valid JSON.");
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
    if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr)
    {
        OutError = TEXT("Staged Demo declaration has no assets array.");
        return false;
    }
    for (const TSharedPtr<FJsonValue>& Value : *Assets)
    {
        const TSharedPtr<FJsonObject> Asset = Value.IsValid() ? Value->AsObject() : nullptr;
        FString QualifiedName;
        if (!Asset.IsValid() ||
            !Asset->TryGetStringField(TEXT("qualifiedName"), QualifiedName) ||
            QualifiedName != TypeName)
        {
            continue;
        }
        FString Id;
        if (!Asset->TryGetStringField(TEXT("id"), Id) ||
            !Id.StartsWith(TEXT("us_")) ||
            !FGuid::ParseExact(Id.Mid(3), EGuidFormats::Digits, OutAsset.ClassId) ||
            !Asset->TryGetStringField(TEXT("assetPath"), OutAsset.AssetPath))
        {
            OutError = FString::Printf(
                TEXT("Demo asset '%s' has incomplete identity metadata."),
                *TypeName);
            return false;
        }
        OutAsset.TypeName = TypeName;
        return true;
    }
    OutError = FString::Printf(TEXT("Demo declaration does not contain '%s'."), *TypeName);
    return false;
}

UUnifiedScriptBlueprint* CreateScriptBlueprint(
    UClass* ParentClass,
    const FString& PackageName,
    const FString& AssetName,
    UPackage*& OutPackage,
    const FName& CallingContext)
{
    OutPackage = CreatePackage(*PackageName);
    return Cast<UUnifiedScriptBlueprint>(FKismetEditorUtilities::CreateBlueprint(
        ParentClass,
        OutPackage,
        FName(*AssetName),
        BPTYPE_Normal,
        UUnifiedScriptBlueprint::StaticClass(),
        UUnifiedScriptBlueprintGeneratedClass::StaticClass(),
        CallingContext));
}

UUnifiedScriptBlueprint* CreateBlueprint(
    const FString& PackageName,
    const FString& AssetName,
    UPackage*& OutPackage)
{
    return CreateScriptBlueprint(
        AUnifiedScriptEventExtensionFixture::StaticClass(),
        PackageName,
        AssetName,
        OutPackage,
        FName(TEXT("UnifiedScriptBlueprintParentInvocation")));
}

UBlueprint* CreateNormalBlueprint(
    UClass* ParentClass,
    const FString& PackageName,
    const FString& AssetName,
    UPackage*& OutPackage)
{
    OutPackage = CreatePackage(*PackageName);
    return FKismetEditorUtilities::CreateBlueprint(
        ParentClass,
        OutPackage,
        FName(*AssetName),
        BPTYPE_Normal,
        UBlueprint::StaticClass(),
        UBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("UnifiedScriptBlueprintParentInvocationIntermediate")));
}

bool CreateIntermediateEvaluateBody(
    UBlueprint& Blueprint,
    FString& OutError)
{
    UEdGraph* Graph = FBlueprintEditorUtils::CreateNewGraph(
        &Blueprint,
        TEXT("EvaluateInherited"),
        UEdGraph::StaticClass(),
        UEdGraphSchema_K2::StaticClass());
    if (Graph == nullptr)
    {
        OutError = TEXT("Could not create the ordinary Blueprint intermediate function graph.");
        return false;
    }
    FBlueprintEditorUtils::AddFunctionGraph<UClass>(
        &Blueprint,
        Graph,
        false,
        AUnifiedScriptEventExtensionFixture::StaticClass());

    // The UClass overload creates a valid inherited graph by inserting the
    // stock Call Parent node. This test supplies the ordinary Blueprint body
    // itself, so remove that generated body before wiring the VM nodes.
    TArray<UK2Node_CallParentFunction*> GeneratedParentCalls;
    Graph->GetNodesOfClass(GeneratedParentCalls);
    for (UK2Node_CallParentFunction* ParentCall : GeneratedParentCalls)
    {
        if (ParentCall != nullptr)
        {
            ParentCall->DestroyNode();
        }
    }

    TArray<UK2Node_FunctionEntry*> Entries;
    Graph->GetNodesOfClass(Entries);
    TArray<UK2Node_FunctionResult*> Results;
    Graph->GetNodesOfClass(Results);
    if (Entries.Num() != 1 ||
        (Results.Num() == 0 &&
            (FBlueprintEditorUtils::FindOrCreateFunctionResultNode(Entries[0]) == nullptr)))
    {
        OutError = TEXT("The ordinary Blueprint intermediate graph has no entry/result pair.");
        return false;
    }
    Results.Reset();
    Graph->GetNodesOfClass(Results);
    if (Results.Num() != 1)
    {
        OutError = TEXT("The ordinary Blueprint intermediate graph did not retain one result node.");
        return false;
    }

    const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
    UEdGraphPin* EntryThen = Entries[0]->FindPin(UEdGraphSchema_K2::PN_Then, EGPD_Output);
    UEdGraphPin* ResultExecute = Results[0]->FindPin(UEdGraphSchema_K2::PN_Execute, EGPD_Input);
    if (EntryThen == nullptr || ResultExecute == nullptr)
    {
        OutError = TEXT("The ordinary Blueprint intermediate graph is missing the entry/result execution pins.");
        return false;
    }

    // UE represents a ref parameter as storage owned by FunctionEntry. It is
    // not a writable output on FunctionResult, so use the real K2 Set-by-Ref
    // node to update it in this intermediate VM body.
    UEdGraphPin* EntryAccumulator = Entries[0]->FindPin(
        TEXT("NativeAccumulator"), EGPD_Output);
    if (EntryAccumulator == nullptr)
    {
        OutError = TEXT("The ordinary intermediate Blueprint entry has no NativeAccumulator ref pin.");
        return false;
    }
    UK2Node_VariableSetRef* SetAccumulator = NewObject<UK2Node_VariableSetRef>(Graph);
    Graph->AddNode(SetAccumulator, true, false);
    SetAccumulator->CreateNewGuid();
    SetAccumulator->AllocateDefaultPins();
    SetAccumulator->NodePosX = 300;
    SetAccumulator->NodePosY = 0;
    UEdGraphPin* SetTarget = SetAccumulator->GetTargetPin();
    UEdGraphPin* SetValue = SetAccumulator->GetValuePin();
    if (SetTarget == nullptr || SetValue == nullptr ||
        !Schema->TryCreateConnection(EntryAccumulator, SetTarget))
    {
        OutError = TEXT("The ordinary intermediate Blueprint could not connect NativeAccumulator to Set-by-Ref.");
        return false;
    }
    SetValue->DefaultValue = TEXT("77");
    UEdGraphPin* SetExecute = SetAccumulator->FindPin(UEdGraphSchema_K2::PN_Execute, EGPD_Input);
    UEdGraphPin* SetThen = SetAccumulator->FindPin(UEdGraphSchema_K2::PN_Then, EGPD_Output);
    if (SetExecute == nullptr || SetThen == nullptr ||
        !Schema->TryCreateConnection(EntryThen, SetExecute) ||
        !Schema->TryCreateConnection(SetThen, ResultExecute))
    {
        OutError = TEXT("The ordinary intermediate Blueprint could not connect Set-by-Ref into the function body.");
        return false;
    }

    for (const TPair<FName, FString>& Default : TArray<TPair<FName, FString>>{
             {FName(TEXT("NativeOutput")), TEXT("88")},
             {FName(TEXT("ReturnValue")), TEXT("99")}})
    {
        UEdGraphPin* Pin = Results[0]->FindPin(Default.Key, EGPD_Input);
        if (Pin == nullptr)
        {
            FString PinNames;
            for (const UEdGraphPin* ExistingPin : Results[0]->Pins)
            {
                if (ExistingPin != nullptr)
                {
                    PinNames += ExistingPin->PinName.ToString() + TEXT(",");
                }
            }
            OutError = FString::Printf(
                TEXT("The ordinary Blueprint intermediate result pin '%s' could not receive its default; available result pins='%s'."),
                *Default.Key.ToString(),
                *PinNames);
            return false;
        }
        Schema->TrySetDefaultValue(*Pin, Default.Value);
    }
    FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(&Blueprint);
    FKismetEditorUtilities::CompileBlueprint(&Blueprint);
    if (Blueprint.Status != BS_UpToDate || Blueprint.GeneratedClass == nullptr)
    {
        OutError = TEXT("The ordinary Blueprint intermediate did not compile.");
        return false;
    }
    return true;
}

UUnifiedScriptAsset* AttachScriptAsset(
    UUnifiedScriptBlueprint& Blueprint,
    const FDemoAsset& DemoAsset,
    const FString& ChunkPath,
    const FString& AssemblyPath,
    const FString& DeclarationPath,
    EUnifiedScriptClassKind ClassKind = EUnifiedScriptClassKind::Actor)
{
    UUnifiedScriptAsset* ScriptAsset = NewObject<UUnifiedScriptAsset>(
        Blueprint.GetOutermost(),
        TEXT("ParentInvocationScriptAsset"));
    ScriptAsset->ScriptClassId = DemoAsset.ClassId;
    ScriptAsset->ScriptClassKind = ClassKind;
    ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint.GeneratedClass);
    ScriptAsset->CompiledChunkPath = ChunkPath;
    ScriptAsset->ScriptAssemblyPath = AssemblyPath;
    ScriptAsset->ScriptTypeName = DemoAsset.TypeName;
    ScriptAsset->DeclarationPath = DeclarationPath;
    ScriptAsset->DeclarationAssetPath = DemoAsset.AssetPath;
    Blueprint.ScriptAsset = ScriptAsset;
    return ScriptAsset;
}

void SetInt(const FProperty& Property, void* Memory, int32 Value)
{
    CastFieldChecked<FIntProperty>(&Property)->SetPropertyValue_InContainer(Memory, Value);
}

int32 GetInt(const FProperty& Property, const void* Memory)
{
    return CastFieldChecked<FIntProperty>(&Property)->GetPropertyValue_InContainer(Memory);
}

void SetIntArray(const FArrayProperty& Property, void* Memory, TConstArrayView<int32> Values)
{
    FScriptArrayHelper Array(&Property, Property.ContainerPtrToValuePtr<void>(Memory));
    Array.EmptyAndAddValues(Values.Num());
    FIntProperty* Inner = CastFieldChecked<FIntProperty>(Property.Inner);
    for (int32 Index = 0; Index < Values.Num(); ++Index)
    {
        Inner->SetPropertyValue(Array.GetRawPtr(Index), Values[Index]);
    }
}

TArray<int32> GetIntArray(const FArrayProperty& Property, const void* Memory)
{
    const void* Address = Property.ContainerPtrToValuePtr<void>(Memory);
    FScriptArrayHelper Array(&Property, const_cast<void*>(Address));
    TArray<int32> Values;
    Values.Reserve(Array.Num());
    FIntProperty* Inner = CastFieldChecked<FIntProperty>(Property.Inner);
    for (int32 Index = 0; Index < Array.Num(); ++Index)
    {
        Values.Add(Inner->GetPropertyValue(Array.GetRawPtr(Index)));
    }
    return Values;
}

struct FOwnedParentFrame
{
    TArray<FUnifiedScriptNativeValueV3> Arguments;
    TArray<FUnifiedScriptNativeValueV3> Results;
    FUnifiedScriptNativeCallFrameV3 Frame;

    explicit FOwnedParentFrame(bool bWrongResultDescriptor)
    {
        Arguments.SetNumZeroed(3);
        Arguments[0].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        Arguments[0].Payload0 = 7;
        Arguments[1].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        Arguments[1].Payload0 = 5;
        Arguments[2].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        Results.SetNumZeroed(3);
        for (FUnifiedScriptNativeValueV3& Result : Results)
        {
            Result.Kind = EUnifiedScriptNativeValueKindV3::Int32;
        }
        if (bWrongResultDescriptor)
        {
            Results[0].Kind = EUnifiedScriptNativeValueKindV3::ByteBuffer;
        }
        Frame = {};
        Frame.Arguments = Arguments.GetData();
        Frame.ArgumentCount = Arguments.Num();
        Frame.Flags = static_cast<uint32>(EUnifiedScriptNativeCallFrameFlagsV3::BlueprintParent);
        Frame.Results = Results.GetData();
        Frame.ResultCapacity = Results.Num();
        Frame.ResultCount = Results.Num();
        Frame.Status = EUnifiedScriptNativeCallStatusV3::Uninitialized;
    }
};
} // namespace UE::UnifiedScript::BlueprintClass::ParentInvocationTests

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptBlueprintParentInvocationTest,
    "UnifiedScript.Unreal.ScriptClass.BlueprintParentInvocation",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptBlueprintParentInvocationTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::BlueprintClass::ParentInvocationTests;

    const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (!TestNotNull(TEXT("UEUnifiedScript plugin is installed"), Plugin.Get()))
    {
        return false;
    }
    const FString DemoDirectory = FPaths::Combine(
        Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
    const FString DeclarationPath = FPaths::Combine(
        DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.asset.json"));
    const FString ChunkPath = FPaths::Combine(
        DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.uschunk"));
    const FString AssemblyPath = FPaths::Combine(
        DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.dll"));
    FString Declaration;
    if (!TestTrue(TEXT("Demo declaration is staged"),
            FFileHelper::LoadFileToString(Declaration, *DeclarationPath)) ||
        !TestTrue(TEXT("Demo chunk is staged"), IFileManager::Get().FileExists(*ChunkPath)) ||
        !TestTrue(TEXT("Demo assembly is staged"), IFileManager::Get().FileExists(*AssemblyPath)))
    {
        return false;
    }

    const FString DemoTypeName =
        TEXT("UnifiedScript.Unreal.Demo.BlueprintParentInvocationDemoScript");
    FDemoAsset DemoAsset;
    FString Error;
    if (!TestTrue(TEXT("Parent fixture asset identity is present"),
            ReadDemoAsset(Declaration, DemoTypeName, DemoAsset, Error)))
    {
        AddError(Error);
        return false;
    }

    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString PackageName = TEXT("/Game/UnifiedScriptParentInvocation_") + Suffix;
    const FString AssetName = TEXT("BP_UnifiedScriptParentInvocation_") + Suffix;
    const FString PackageFilename = FPackageName::LongPackageNameToFilename(
        PackageName,
        FPackageName::GetAssetPackageExtension());
    UPackage* Package = nullptr;
    UUnifiedScriptBlueprint* Blueprint =
        CreateBlueprint(PackageName, AssetName, Package);
    UUnifiedScriptAsset* ScriptAsset = Blueprint != nullptr
        ? AttachScriptAsset(*Blueprint, DemoAsset, ChunkPath, AssemblyPath, DeclarationPath)
        : nullptr;
    UWorld* World = nullptr;
    AUnifiedScriptEventExtensionFixture* Actor = nullptr;
    FWorldContext* WorldContext = nullptr;
    UUnifiedScriptComponent* Component = nullptr;
    const int32 InitialManagedWorldCount =
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();

    ON_SCOPE_EXIT
    {
        if (Component != nullptr)
        {
            Component->EndPlay(EEndPlayReason::Destroyed);
            Component = nullptr;
        }
        if (Actor != nullptr && !Actor->IsActorBeingDestroyed())
        {
            Actor->Destroy();
            Actor = nullptr;
        }
        if (WorldContext != nullptr && GEngine != nullptr)
        {
            GEngine->DestroyWorldContext(World);
            WorldContext = nullptr;
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
        IFileManager::Get().Delete(*PackageFilename, false, true, true);
        TestEqual(
            TEXT("Parent invocation World cleanup releases managed state"),
            FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
            InitialManagedWorldCount);
    };

    if (!TestNotNull(TEXT("Parent invocation Blueprint is created"), Blueprint) ||
        !TestNotNull(TEXT("Parent invocation generated class is created"),
            Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr) ||
        !TestNotNull(TEXT("Parent invocation Script Asset is attached"), ScriptAsset))
    {
        return false;
    }
    FUnifiedScriptBlueprintClassSyncResult SyncResult;
    Error.Reset();
    if (!TestTrue(*FString::Printf(TEXT("Parent route materializes: %s"), *Error),
            FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                *ScriptAsset, *Blueprint, Declaration, false, SyncResult, Error)))
    {
        return false;
    }

    UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
    if (!TestNotNull(TEXT("Parent fixture generated class is typed"), GeneratedClass))
    {
        return false;
    }
    UFunction* Evaluate = GeneratedClass->FindFunctionByName(
        TEXT("EvaluateInherited"), EIncludeSuperFlag::ExcludeSuper);
    UFunction* Receive = GeneratedClass->FindFunctionByName(
        TEXT("ReceiveInherited"), EIncludeSuperFlag::ExcludeSuper);
    UFunction* Exchange = GeneratedClass->FindFunctionByName(
        TEXT("ExchangeInheritedArray"), EIncludeSuperFlag::ExcludeSuper);
    TestTrue(TEXT("Parent BNE is bound to managed dispatch"),
        GeneratedClass->IsManagedFunctionBound(TEXT("EvaluateInherited")));
    TestTrue(TEXT("Parent BIE is bound to managed dispatch"),
        GeneratedClass->IsManagedFunctionBound(TEXT("ReceiveInherited")));
    TestTrue(TEXT("Parent array BNE is bound to managed dispatch"),
        GeneratedClass->IsManagedFunctionBound(TEXT("ExchangeInheritedArray")));
    if (!TestNotNull(TEXT("EvaluateInherited function exists"), Evaluate) ||
        !TestNotNull(TEXT("ReceiveInherited function exists"), Receive) ||
        !TestNotNull(TEXT("ExchangeInheritedArray function exists"), Exchange))
    {
        return false;
    }

    World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Parent invocation World is created"), World))
    {
        return false;
    }
    World->AddToRoot();
    if (GEngine != nullptr)
    {
        WorldContext = &GEngine->CreateNewWorldContext(EWorldType::Game);
        WorldContext->SetCurrentWorld(World);
    }
    World->InitializeActorsForPlay(FURL());
    Actor = World->SpawnActor<AUnifiedScriptEventExtensionFixture>(GeneratedClass);
    Component = Actor != nullptr ? Actor->GetUnifiedScriptComponent() : nullptr;
    UUnifiedScriptWorldSubsystem* Host = World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    if (!TestNotNull(TEXT("Parent invocation Actor spawns"), Actor) ||
        !TestNotNull(TEXT("Parent invocation component exists"), Component) ||
        !TestNotNull(TEXT("Parent invocation managed World host exists"), Host))
    {
        return false;
    }
    Component->ScriptAsset = ScriptAsset;
    Component->bStartAutomatically = false;
    Component->BeginPlay();
    TestTrue(TEXT("Parent invocation component starts a managed generation"),
        Component->StartScript());
    TestTrue(TEXT("Parent invocation component has an active managed generation"),
        !Component->ActiveGeneration.IsEmpty());
    TestTrue(TEXT("Parent invocation startup has no error"),
        Component->LastScriptError.IsEmpty());

    const FProperty* NativeInput = FindFProperty<FProperty>(Evaluate, TEXT("NativeInput"));
    const FProperty* NativeAccumulator = FindFProperty<FProperty>(Evaluate, TEXT("NativeAccumulator"));
    const FProperty* NativeOutput = FindFProperty<FProperty>(Evaluate, TEXT("NativeOutput"));
    const FProperty* EvaluateReturn = Evaluate->GetReturnProperty();
    if (!TestTrue(TEXT("EvaluateInherited has native input/ref/out/return properties"),
            NativeInput != nullptr && NativeAccumulator != nullptr &&
            NativeOutput != nullptr && EvaluateReturn != nullptr))
    {
        return false;
    }
    FStructOnScope EvaluateCall(Evaluate);
    void* EvaluateMemory = EvaluateCall.GetStructMemory();
    SetInt(*NativeInput, EvaluateMemory, 7);
    SetInt(*NativeAccumulator, EvaluateMemory, 5);
    Actor->ProcessEvent(Evaluate, EvaluateMemory);
    TestEqual(TEXT("Parent BNE returns the native parent value"),
        GetInt(*EvaluateReturn, EvaluateMemory), 12);
    TestEqual(TEXT("Parent BNE writes the native ref value"),
        GetInt(*NativeAccumulator, EvaluateMemory), 12);
    TestEqual(TEXT("Parent BNE writes the native out value"),
        GetInt(*NativeOutput, EvaluateMemory), 12);
    TestEqual(TEXT("Native parent executes exactly once for the first call"),
        Actor->ParentInvocationCount,
        1);

    FStructOnScope EvaluateAgain(Evaluate);
    void* EvaluateAgainMemory = EvaluateAgain.GetStructMemory();
    SetInt(*NativeInput, EvaluateAgainMemory, 3);
    SetInt(*NativeAccumulator, EvaluateAgainMemory, 4);
    Actor->ProcessEvent(Evaluate, EvaluateAgainMemory);
    TestEqual(TEXT("Repeated managed parent calls execute the parent again"),
        GetInt(*EvaluateReturn, EvaluateAgainMemory), 7);
    TestEqual(TEXT("Repeated parent calls increment the native counter"),
        Actor->ParentInvocationCount,
        2);

    const FProperty* NativeText = FindFProperty<FProperty>(Receive, TEXT("NativeText"));
    const FObjectPropertyBase* NativeActor = FindFProperty<FObjectPropertyBase>(Receive, TEXT("NativeActor"));
    if (TestTrue(TEXT("ReceiveInherited has native text and Actor inputs"),
            NativeText != nullptr && NativeActor != nullptr))
    {
        FStructOnScope ReceiveCall(Receive);
        void* ReceiveMemory = ReceiveCall.GetStructMemory();
        CastFieldChecked<FStrProperty>(NativeText)->SetPropertyValue_InContainer(
            ReceiveMemory,
            TEXT("parent"));
        NativeActor->SetObjectPropertyValue_InContainer(ReceiveMemory, Actor);
        Actor->ProcessEvent(Receive, ReceiveMemory);
        TestTrue(TEXT("Empty BIE parent call returns without a managed error"),
            Component->LastScriptError.IsEmpty());
    }

    const FArrayProperty* NativeArrayInput = CastField<FArrayProperty>(
        FindFProperty<FProperty>(Exchange, TEXT("NativeInput")));
    const FArrayProperty* NativeArrayState = CastField<FArrayProperty>(
        FindFProperty<FProperty>(Exchange, TEXT("NativeState")));
    const FArrayProperty* NativeArrayOutput = CastField<FArrayProperty>(
        FindFProperty<FProperty>(Exchange, TEXT("NativeOutput")));
    const FArrayProperty* ExchangeReturn = CastField<FArrayProperty>(
        Exchange->GetReturnProperty());
    if (!TestTrue(TEXT("Large parent call has typed array input/ref/out/return"),
            NativeArrayInput != nullptr && NativeArrayState != nullptr &&
            NativeArrayOutput != nullptr && ExchangeReturn != nullptr))
    {
        return false;
    }
    TArray<int32> LargeInput;
    for (int32 Index = 0; Index < 256; ++Index)
    {
        LargeInput.Add(Index);
    }
    FStructOnScope ExchangeCall(Exchange);
    void* ExchangeMemory = ExchangeCall.GetStructMemory();
    SetIntArray(*NativeArrayInput, ExchangeMemory, LargeInput);
    SetIntArray(*NativeArrayState, ExchangeMemory, TArray<int32>{100, 101});
    const int32 CounterBeforeArray = Actor->ParentInvocationCount;
    Actor->ProcessEvent(Exchange, ExchangeMemory);
    const TArray<int32> ResultArray = GetIntArray(*ExchangeReturn, ExchangeMemory);
    const TArray<int32> StateArray = GetIntArray(*NativeArrayState, ExchangeMemory);
    const TArray<int32> OutputArray = GetIntArray(*NativeArrayOutput, ExchangeMemory);
    TestEqual(TEXT("Large parent result retains all array elements"), ResultArray.Num(), 258);
    TestEqual(TEXT("Large parent ref result retains all array elements"), StateArray.Num(), 258);
    TestEqual(TEXT("Large parent out result retains all array elements"), OutputArray.Num(), 258);
    if (ResultArray.Num() == 258)
    {
        TestEqual(TEXT("Large parent result starts with native input"), ResultArray[0], 0);
        TestEqual(TEXT("Large parent result keeps native input tail"), ResultArray[255], 255);
        TestEqual(TEXT("Large parent result appends native ref input"), ResultArray[256], 100);
        TestEqual(TEXT("Large parent result appends native ref tail"), ResultArray[257], 101);
    }
    TestEqual(TEXT("Large parent retry executes native parent exactly once"),
        Actor->ParentInvocationCount,
        CounterBeforeArray + 1);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptBlueprintIntermediateParentInvocationTest,
    "UnifiedScript.Unreal.ScriptClass.BlueprintIntermediateParentInvocation",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptBlueprintIntermediateParentInvocationTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::BlueprintClass::ParentInvocationTests;

    const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (!TestNotNull(TEXT("UEUnifiedScript plugin is installed"), Plugin.Get()))
    {
        return false;
    }
    const FString DemoDirectory = FPaths::Combine(
        Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
    const FString DeclarationPath = FPaths::Combine(
        DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.asset.json"));
    const FString ChunkPath = FPaths::Combine(
        DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.uschunk"));
    const FString AssemblyPath = FPaths::Combine(
        DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.dll"));
    FString Declaration;
    if (!TestTrue(TEXT("Demo declaration is staged"),
            FFileHelper::LoadFileToString(Declaration, *DeclarationPath)) ||
        !TestTrue(TEXT("Demo chunk is staged"), IFileManager::Get().FileExists(*ChunkPath)) ||
        !TestTrue(TEXT("Demo assembly is staged"), IFileManager::Get().FileExists(*AssemblyPath)))
    {
        return false;
    }

    FDemoAsset DemoAsset;
    FString Error;
    if (!TestTrue(TEXT("Intermediate parent fixture identity is present"),
            ReadDemoAsset(
                Declaration,
                TEXT("UnifiedScript.Unreal.Demo.BlueprintParentInvocationDemoScript"),
                DemoAsset,
                Error)))
    {
        AddError(Error);
        return false;
    }

    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString IntermediatePackageName =
        TEXT("/Engine/Transient/UnifiedScriptParentIntermediate_") + Suffix;
    const FString IntermediateAssetName = TEXT("BP_ParentIntermediate_") + Suffix;
    UPackage* IntermediatePackage = nullptr;
    UBlueprint* IntermediateBlueprint = CreateNormalBlueprint(
        AUnifiedScriptEventExtensionFixture::StaticClass(),
        IntermediatePackageName,
        IntermediateAssetName,
        IntermediatePackage);
    UPackage* ManagedPackage = nullptr;
    UUnifiedScriptBlueprint* ManagedBlueprint = nullptr;
    UUnifiedScriptAsset* ScriptAsset = nullptr;
    UWorld* World = nullptr;
    FWorldContext* WorldContext = nullptr;
    AUnifiedScriptEventExtensionFixture* Actor = nullptr;
    UUnifiedScriptComponent* Component = nullptr;
    const int32 InitialManagedWorldCount =
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();

    ON_SCOPE_EXIT
    {
        if (Component != nullptr)
        {
            Component->EndPlay(EEndPlayReason::Destroyed);
            Component = nullptr;
        }
        if (Actor != nullptr && !Actor->IsActorBeingDestroyed())
        {
            Actor->Destroy();
            Actor = nullptr;
        }
        if (WorldContext != nullptr && GEngine != nullptr)
        {
            GEngine->DestroyWorldContext(World);
            WorldContext = nullptr;
        }
        if (World != nullptr)
        {
            World->RemoveFromRoot();
            World->DestroyWorld(false);
            World = nullptr;
        }
        ManagedBlueprint = nullptr;
        IntermediateBlueprint = nullptr;
        if (ManagedPackage != nullptr || IntermediatePackage != nullptr)
        {
            TArray<UPackage*> Packages;
            if (ManagedPackage != nullptr)
            {
                ManagedPackage->SetDirtyFlag(false);
                Packages.Add(ManagedPackage);
            }
            if (IntermediatePackage != nullptr)
            {
                IntermediatePackage->SetDirtyFlag(false);
                Packages.Add(IntermediatePackage);
            }
            UPackageTools::UnloadPackages(Packages);
            ManagedPackage = nullptr;
            IntermediatePackage = nullptr;
        }
        TestEqual(
            TEXT("Intermediate parent cleanup releases managed state"),
            FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
            InitialManagedWorldCount);
    };

    if (!TestNotNull(TEXT("Creates ordinary intermediate Blueprint"), IntermediateBlueprint) ||
        !TestTrue(TEXT("Ordinary intermediate Blueprint has a VM body"),
            IntermediateBlueprint != nullptr &&
                CreateIntermediateEvaluateBody(*IntermediateBlueprint, Error)))
    {
        AddError(Error);
        return false;
    }

    ManagedPackage = CreatePackage(
        *(TEXT("/Engine/Transient/UnifiedScriptParentManaged_") + Suffix));
    ManagedBlueprint = Cast<UUnifiedScriptBlueprint>(FKismetEditorUtilities::CreateBlueprint(
        IntermediateBlueprint->GeneratedClass,
        ManagedPackage,
        FName(*(TEXT("BP_ParentManaged_") + Suffix)),
        BPTYPE_Normal,
        UUnifiedScriptBlueprint::StaticClass(),
        UUnifiedScriptBlueprintGeneratedClass::StaticClass(),
        FName(TEXT("UnifiedScriptBlueprintParentInvocationManaged"))));
    ScriptAsset = ManagedBlueprint != nullptr
        ? AttachScriptAsset(*ManagedBlueprint, DemoAsset, ChunkPath, AssemblyPath, DeclarationPath)
        : nullptr;
    if (!TestNotNull(TEXT("Creates managed Blueprint below the intermediate parent"), ManagedBlueprint) ||
        !TestNotNull(TEXT("Intermediate parent test has a Script Asset"), ScriptAsset))
    {
        return false;
    }

    FUnifiedScriptBlueprintClassSyncResult SyncResult;
    Error.Reset();
    if (!TestTrue(TEXT("Materializes managed override below ordinary Blueprint parent"),
            FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                *ScriptAsset,
                *ManagedBlueprint,
                Declaration,
                false,
                SyncResult,
                Error)))
    {
        AddError(Error);
        return false;
    }
    UUnifiedScriptBlueprintGeneratedClass* ManagedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(ManagedBlueprint->GeneratedClass);
    UFunction* Evaluate = ManagedClass != nullptr
        ? ManagedClass->FindFunctionByName(TEXT("EvaluateInherited"), EIncludeSuperFlag::ExcludeSuper)
        : nullptr;
    if (!TestNotNull(TEXT("Intermediate managed override function exists"), Evaluate) ||
        !TestNotNull(TEXT("Intermediate managed generated class exists"), ManagedClass))
    {
        return false;
    }

    World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Intermediate parent World is created"), World))
    {
        return false;
    }
    World->AddToRoot();
    if (GEngine != nullptr)
    {
        WorldContext = &GEngine->CreateNewWorldContext(EWorldType::Game);
        WorldContext->SetCurrentWorld(World);
    }
    World->InitializeActorsForPlay(FURL());
    Actor = World->SpawnActor<AUnifiedScriptEventExtensionFixture>(ManagedClass);
    Component = Actor != nullptr ? Actor->GetUnifiedScriptComponent() : nullptr;
    if (!TestNotNull(TEXT("Intermediate parent Actor spawns"), Actor) ||
        !TestNotNull(TEXT("Intermediate parent component exists"), Component))
    {
        return false;
    }
    Component->ScriptAsset = ScriptAsset;
    Component->bStartAutomatically = false;
    Component->BeginPlay();
    if (!TestTrue(TEXT("Intermediate parent component starts managed code"), Component->StartScript()))
    {
        return false;
    }

    const FIntProperty* NativeInput = FindFProperty<FIntProperty>(Evaluate, TEXT("NativeInput"));
    const FIntProperty* NativeAccumulator = FindFProperty<FIntProperty>(Evaluate, TEXT("NativeAccumulator"));
    const FIntProperty* NativeOutput = FindFProperty<FIntProperty>(Evaluate, TEXT("NativeOutput"));
    const FIntProperty* ReturnValue = CastField<FIntProperty>(Evaluate->GetReturnProperty());
    if (!TestTrue(TEXT("Intermediate parent function keeps native pins"),
            NativeInput != nullptr && NativeAccumulator != nullptr &&
                NativeOutput != nullptr && ReturnValue != nullptr))
    {
        return false;
    }
    FStructOnScope Call(Evaluate);
    void* Memory = Call.GetStructMemory();
    SetInt(*NativeInput, Memory, 9);
    SetInt(*NativeAccumulator, Memory, 5);
    Actor->ProcessEvent(Evaluate, Memory);
    TestEqual(TEXT("Managed parent call reaches ordinary Blueprint VM body return"),
        GetInt(*ReturnValue, Memory), 99);
    TestEqual(TEXT("Ordinary Blueprint VM body controls ref output"),
        GetInt(*NativeAccumulator, Memory), 77);
    TestEqual(TEXT("Ordinary Blueprint VM body controls out output"),
        GetInt(*NativeOutput, Memory), 88);
    TestEqual(TEXT("Intermediate VM body does not call native implementation"),
        Actor->ParentInvocationCount,
        0);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptBlueprintParentInvocationComponentTest,
    "UnifiedScript.Unreal.ScriptClass.BlueprintParentInvocationComponent",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptBlueprintParentInvocationComponentTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::BlueprintClass::ParentInvocationTests;

    const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (!TestNotNull(TEXT("UEUnifiedScript plugin is installed"), Plugin.Get()))
    {
        return false;
    }
    const FString DemoDirectory = FPaths::Combine(
        Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
    const FString DeclarationPath = FPaths::Combine(
        DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.asset.json"));
    const FString ChunkPath = FPaths::Combine(
        DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.uschunk"));
    const FString AssemblyPath = FPaths::Combine(
        DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.dll"));
    FString Declaration;
    if (!TestTrue(TEXT("Demo declaration is staged"),
            FFileHelper::LoadFileToString(Declaration, *DeclarationPath)) ||
        !TestTrue(TEXT("Demo chunk is staged"), IFileManager::Get().FileExists(*ChunkPath)) ||
        !TestTrue(TEXT("Demo assembly is staged"), IFileManager::Get().FileExists(*AssemblyPath)))
    {
        return false;
    }

    FDemoAsset DemoAsset;
    FString Error;
    const FString DemoTypeName =
        TEXT("UnifiedScript.Unreal.Demo.BlueprintParentInvocationComponentDemoScript");
    if (!TestTrue(TEXT("Component parent fixture identity is present"),
            ReadDemoAsset(Declaration, DemoTypeName, DemoAsset, Error)))
    {
        AddError(Error);
        return false;
    }

    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString PackageName = TEXT("/Game/UnifiedScriptParentInvocationComponent_") + Suffix;
    const FString AssetName = TEXT("BP_UnifiedScriptParentInvocationComponent_") + Suffix;
    const FString PackageFilename = FPackageName::LongPackageNameToFilename(
        PackageName,
        FPackageName::GetAssetPackageExtension());
    UPackage* Package = nullptr;
    UUnifiedScriptBlueprint* Blueprint = CreateScriptBlueprint(
        UUnifiedScriptComponent::StaticClass(),
        PackageName,
        AssetName,
        Package,
        FName(TEXT("UnifiedScriptBlueprintParentInvocationComponent")));
    UUnifiedScriptAsset* ScriptAsset = Blueprint != nullptr
        ? AttachScriptAsset(
            *Blueprint,
            DemoAsset,
            ChunkPath,
            AssemblyPath,
            DeclarationPath,
            EUnifiedScriptClassKind::ActorComponent)
        : nullptr;
    UWorld* World = nullptr;
    FWorldContext* WorldContext = nullptr;
    AActor* Actor = nullptr;
    UUnifiedScriptComponent* Component = nullptr;
    const int32 InitialManagedWorldCount =
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();

    ON_SCOPE_EXIT
    {
        if (Component != nullptr)
        {
            Component->EndPlay(EEndPlayReason::Destroyed);
            Component->DestroyComponent();
            Component = nullptr;
        }
        if (Actor != nullptr && !Actor->IsActorBeingDestroyed())
        {
            Actor->Destroy();
            Actor = nullptr;
        }
        if (WorldContext != nullptr && GEngine != nullptr)
        {
            GEngine->DestroyWorldContext(World);
            WorldContext = nullptr;
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
        IFileManager::Get().Delete(*PackageFilename, false, true, true);
        TestEqual(
            TEXT("Component parent cleanup releases managed state"),
            FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
            InitialManagedWorldCount);
    };

    if (!TestNotNull(TEXT("Component parent Blueprint is created"), Blueprint) ||
        !TestNotNull(TEXT("Component parent generated class is created"),
            Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr) ||
        !TestNotNull(TEXT("Component parent Script Asset is attached"), ScriptAsset))
    {
        return false;
    }

    FUnifiedScriptBlueprintClassSyncResult SyncResult;
    if (!TestTrue(*FString::Printf(TEXT("Materializes component parent route: %s"), *Error),
            FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                *ScriptAsset, *Blueprint, Declaration, false, SyncResult, Error)))
    {
        AddError(Error);
        return false;
    }
    UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
    UFunction* ReceiveTick = GeneratedClass != nullptr
        ? GeneratedClass->FindFunctionByName(TEXT("ReceiveTick"), EIncludeSuperFlag::ExcludeSuper)
        : nullptr;
    if (!TestNotNull(TEXT("Component ReceiveTick function exists"), ReceiveTick) ||
        !TestTrue(TEXT("Component ReceiveTick uses managed dispatch"),
            GeneratedClass != nullptr &&
                GeneratedClass->IsManagedFunctionBound(TEXT("ReceiveTick"))))
    {
        return false;
    }

    World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Component parent World is created"), World))
    {
        return false;
    }
    World->AddToRoot();
    if (GEngine != nullptr)
    {
        WorldContext = &GEngine->CreateNewWorldContext(EWorldType::Game);
        WorldContext->SetCurrentWorld(World);
    }
    World->InitializeActorsForPlay(FURL());
    Actor = World->SpawnActor<AActor>();
    UUnifiedScriptWorldSubsystem* Host = World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    Component = Actor != nullptr
        ? NewObject<UUnifiedScriptComponent>(
            Actor,
            GeneratedClass,
            FName(TEXT("ParentInvocationComponent")))
        : nullptr;
    if (Actor != nullptr && Component != nullptr)
    {
        Actor->AddInstanceComponent(Component);
        Component->ScriptAsset = ScriptAsset;
        Component->bStartAutomatically = false;
        Component->bTickAutomatically = false;
        Component->LifecycleOwnershipKey = FName(TEXT("ParentInvocationComponentTest"));
        Component->RegisterComponent();
    }
    if (!TestNotNull(TEXT("Component parent Actor spawns"), Actor) ||
        !TestNotNull(TEXT("Component parent World host exists"), Host) ||
        !TestNotNull(TEXT("Generated component instance exists"), Component))
    {
        return false;
    }

    Component->BeginPlay();
    if (!TestTrue(TEXT("Generated component starts managed code"), Component->StartScript()))
    {
        AddError(Component->LastScriptError);
        return false;
    }
    TestEqual(
        TEXT("Component parent receiver is the component itself"),
        Component->GetParentInvocationReceiver(),
        static_cast<UObject*>(Component));
    TestEqual(
        TEXT("Component parent target remains the owning Actor"),
        Component->GetScriptTarget(),
        static_cast<UObject*>(Actor));

    const FProperty* DeltaSeconds = FindFProperty<FProperty>(ReceiveTick, TEXT("DeltaSeconds"));
    if (!TestNotNull(TEXT("Component ReceiveTick has DeltaSeconds"), DeltaSeconds))
    {
        return false;
    }
    FStructOnScope ReceiveCall(ReceiveTick);
    CastFieldChecked<FFloatProperty>(DeltaSeconds)->SetPropertyValue_InContainer(
        ReceiveCall.GetStructMemory(),
        0.25f);
    const uint64 FastCallsBefore = Host->GetFastInvocationCount();
    Component->ProcessEvent(ReceiveTick, ReceiveCall.GetStructMemory());
    TestEqual(
        TEXT("Component empty BIE parent performs one native bridge call"),
        Host->GetFastInvocationCount(),
        FastCallsBefore + 1);
    TestTrue(
        TEXT("Component parent call leaves no managed error"),
        Component->LastScriptError.IsEmpty());
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptBlueprintParentInvocationOwnershipTest,
    "UnifiedScript.Unreal.ScriptClass.BlueprintParentInvocationOwnership",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptBlueprintParentInvocationOwnershipTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::BlueprintClass::ParentInvocationTests;

    const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (!TestNotNull(TEXT("UEUnifiedScript plugin is installed"), Plugin.Get()))
    {
        return false;
    }
    const FString DemoDirectory = FPaths::Combine(
        Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
    const FString DeclarationPath = FPaths::Combine(
        DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.asset.json"));
    const FString ChunkPath = FPaths::Combine(
        DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.uschunk"));
    const FString AssemblyPath = FPaths::Combine(
        DemoDirectory, TEXT("UnifiedScript.Unreal.Demo.dll"));
    FString Declaration;
    if (!TestTrue(TEXT("Demo declaration is staged"),
            FFileHelper::LoadFileToString(Declaration, *DeclarationPath)) ||
        !TestTrue(TEXT("Demo chunk is staged"), IFileManager::Get().FileExists(*ChunkPath)) ||
        !TestTrue(TEXT("Demo assembly is staged"), IFileManager::Get().FileExists(*AssemblyPath)))
    {
        return false;
    }

    FDemoAsset DemoAsset;
    FString Error;
    if (!TestTrue(TEXT("Ownership fixture identity is present"),
            ReadDemoAsset(
                Declaration,
                TEXT("UnifiedScript.Unreal.Demo.BlueprintParentInvocationDemoScript"),
                DemoAsset,
                Error)))
    {
        AddError(Error);
        return false;
    }

    const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const FString PackageName = TEXT("/Game/UnifiedScriptParentInvocationOwnership_") + Suffix;
    const FString AssetName = TEXT("BP_UnifiedScriptParentInvocationOwnership_") + Suffix;
    const FString PackageFilename = FPackageName::LongPackageNameToFilename(
        PackageName,
        FPackageName::GetAssetPackageExtension());
    UPackage* Package = nullptr;
    UUnifiedScriptBlueprint* Blueprint = CreateBlueprint(PackageName, AssetName, Package);
    UUnifiedScriptAsset* ScriptAsset = Blueprint != nullptr
        ? AttachScriptAsset(*Blueprint, DemoAsset, ChunkPath, AssemblyPath, DeclarationPath)
        : nullptr;
    UWorld* World = nullptr;
    FWorldContext* WorldContext = nullptr;
    AUnifiedScriptEventExtensionFixture* Actor = nullptr;
    UUnifiedScriptComponent* CanonicalComponent = nullptr;
    UUnifiedScriptComponent* MatchingComponent = nullptr;
    UUnifiedScriptComponent* AmbiguousComponent = nullptr;
    const int32 InitialManagedWorldCount =
        FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();

    ON_SCOPE_EXIT
    {
        if (AmbiguousComponent != nullptr)
        {
            AmbiguousComponent->EndPlay(EEndPlayReason::Destroyed);
            AmbiguousComponent->DestroyComponent();
            AmbiguousComponent = nullptr;
        }
        if (MatchingComponent != nullptr)
        {
            MatchingComponent->EndPlay(EEndPlayReason::Destroyed);
            MatchingComponent->DestroyComponent();
            MatchingComponent = nullptr;
        }
        if (Actor != nullptr && !Actor->IsActorBeingDestroyed())
        {
            Actor->Destroy();
            Actor = nullptr;
        }
        if (WorldContext != nullptr && GEngine != nullptr)
        {
            GEngine->DestroyWorldContext(World);
            WorldContext = nullptr;
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
        IFileManager::Get().Delete(*PackageFilename, false, true, true);
        TestEqual(
            TEXT("Ownership test cleanup releases managed state"),
            FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(),
            InitialManagedWorldCount);
    };

    if (!TestNotNull(TEXT("Ownership Blueprint is created"), Blueprint) ||
        !TestNotNull(TEXT("Ownership Script Asset is attached"), ScriptAsset))
    {
        return false;
    }
    FUnifiedScriptBlueprintClassSyncResult SyncResult;
    if (!TestTrue(TEXT("Ownership actor route materializes"),
            FUnifiedScriptBlueprintClassMaterializer::SynchronizeJson(
                *ScriptAsset, *Blueprint, Declaration, false, SyncResult, Error)))
    {
        AddError(Error);
        return false;
    }
    UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
        Cast<UUnifiedScriptBlueprintGeneratedClass>(Blueprint->GeneratedClass);
    UFunction* Evaluate = GeneratedClass != nullptr
        ? GeneratedClass->FindFunctionByName(
            TEXT("EvaluateInherited"),
            EIncludeSuperFlag::ExcludeSuper)
        : nullptr;
    if (!TestNotNull(TEXT("Ownership EvaluateInherited function exists"), Evaluate) ||
        !TestTrue(TEXT("Ownership actor route uses managed dispatch"),
            GeneratedClass != nullptr &&
                GeneratedClass->IsManagedFunctionBound(TEXT("EvaluateInherited"))))
    {
        return false;
    }

    World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Ownership World is created"), World))
    {
        return false;
    }
    World->AddToRoot();
    if (GEngine != nullptr)
    {
        WorldContext = &GEngine->CreateNewWorldContext(EWorldType::Game);
        WorldContext->SetCurrentWorld(World);
    }
    World->InitializeActorsForPlay(FURL());
    Actor = World->SpawnActor<AUnifiedScriptEventExtensionFixture>(GeneratedClass);
    UUnifiedScriptWorldSubsystem* Host = World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    CanonicalComponent = Actor != nullptr ? Actor->GetUnifiedScriptComponent() : nullptr;
    if (!TestNotNull(TEXT("Ownership Actor spawns"), Actor) ||
        !TestNotNull(TEXT("Ownership World host exists"), Host) ||
        !TestNotNull(TEXT("Canonical component is enumerated first"), CanonicalComponent))
    {
        return false;
    }

    // Leave the actor's canonical component present but cold. A first-match
    // FindComponentByClass resolver would select it instead of the later
    // active owner below.
    CanonicalComponent->bStartAutomatically = false;
    CanonicalComponent->ScriptAsset = nullptr;
    MatchingComponent = NewObject<UUnifiedScriptComponent>(
        Actor,
        UUnifiedScriptComponent::StaticClass(),
        FName(TEXT("MatchingParentInvocationComponent")));
    if (MatchingComponent != nullptr)
    {
        Actor->AddInstanceComponent(MatchingComponent);
        MatchingComponent->ScriptAsset = ScriptAsset;
        MatchingComponent->bStartAutomatically = false;
        MatchingComponent->bTickAutomatically = false;
        MatchingComponent->LifecycleOwnershipKey = FName(TEXT("MatchingParentInvocationOwner"));
        MatchingComponent->RegisterComponent();
        MatchingComponent->BeginPlay();
    }
    if (!TestNotNull(TEXT("Matching component is created after cold canonical"), MatchingComponent) ||
        !TestTrue(TEXT("Matching component starts managed code"),
            MatchingComponent != nullptr && MatchingComponent->StartScript()))
    {
        return false;
    }

    const FProperty* NativeInput = FindFProperty<FProperty>(Evaluate, TEXT("NativeInput"));
    const FProperty* NativeAccumulator = FindFProperty<FProperty>(Evaluate, TEXT("NativeAccumulator"));
    const FProperty* NativeOutput = FindFProperty<FProperty>(Evaluate, TEXT("NativeOutput"));
    const FProperty* ReturnProperty = Evaluate->GetReturnProperty();
    if (!TestTrue(TEXT("Ownership call has typed native properties"),
            NativeInput != nullptr && NativeAccumulator != nullptr &&
                NativeOutput != nullptr && ReturnProperty != nullptr))
    {
        return false;
    }
    FStructOnScope EvaluateCall(Evaluate);
    void* EvaluateMemory = EvaluateCall.GetStructMemory();
    SetInt(*NativeInput, EvaluateMemory, 7);
    SetInt(*NativeAccumulator, EvaluateMemory, 5);
    const uint64 BeforeOwnedCall = Host->GetFastInvocationCount();
    Actor->ProcessEvent(Evaluate, EvaluateMemory);
    TestEqual(
        TEXT("Exact owner routes through the later active component"),
        Host->GetFastInvocationCount(),
        BeforeOwnedCall + 1);
    TestEqual(TEXT("Exact owner parent result is observed"), GetInt(*ReturnProperty, EvaluateMemory), 12);
    TestEqual(TEXT("Exact owner native counter increments"), Actor->ParentInvocationCount, 1);

    const FUnifiedScriptManagedFunctionBinding* EvaluateBinding =
        GeneratedClass->ManagedFunctionBindings.Find(TEXT("EvaluateInherited"));
    const FString EvaluateMemberPath = EvaluateBinding != nullptr
        ? EvaluateBinding->BlueprintOverrideDeclaringTypePath + TEXT(":") +
            EvaluateBinding->BlueprintOverrideFunctionName
        : FString();
    const uint64 EvaluateMemberId = UnifiedScriptMemberId(TCHAR_TO_UTF8(*EvaluateMemberPath));
    FUnifiedScriptObjectHandleResult HandleResult;
    const FUnifiedScriptObjectHandle MatchingHandle =
        Host->RegisterObject(MatchingComponent, HandleResult);
    const FUnifiedScriptObjectHandle ActorHandle =
        Host->RegisterObject(Actor, HandleResult);
    const FUnifiedScriptNativeTransportV3* CallFrameTransport =
        UEUnifiedScript_GetNativeTransportV3();
    if (!TestNotNull(TEXT("Parent rejection test exposes ABI v3 transport"), CallFrameTransport) ||
        !TestTrue(TEXT("Parent rejection test registers component host"), !MatchingHandle.IsNull()) ||
        !TestTrue(TEXT("Parent rejection test registers Actor handle"), !ActorHandle.IsNull()))
    {
        return false;
    }
    FUnifiedScriptNativeBridgeContext* NativeContext = Host->GetNativeInvocationContext();
    if (!TestNotNull(TEXT("Parent rejection test has native context"), NativeContext))
    {
        return false;
    }

    const int32 CounterBeforeRejectedFrames = Actor->ParentInvocationCount;
    {
        FOwnedParentFrame Owned(false);
        CallFrameTransport->InvokeCorrelatedById(
            NativeContext,
            8101,
            ActorHandle.Value,
            EvaluateMemberId,
            &Owned.Frame);
        TestEqual(
            TEXT("Parent frame rejects an Actor handle before the parent body"),
            Owned.Frame.Status,
            EUnifiedScriptNativeCallStatusV3::InvocationFailure);
        TestEqual(
            TEXT("Wrong parent host leaves native counter unchanged"),
            Actor->ParentInvocationCount,
            CounterBeforeRejectedFrames);
    }
    {
        FOwnedParentFrame Owned(true);
        CallFrameTransport->InvokeCorrelatedById(
            NativeContext,
            8102,
            MatchingHandle.Value,
            EvaluateMemberId,
            &Owned.Frame);
        TestEqual(
            TEXT("Parent frame rejects a wrong result descriptor before ProcessEvent"),
            Owned.Frame.Status,
            EUnifiedScriptNativeCallStatusV3::InvocationFailure);
        TestEqual(
            TEXT("Wrong parent descriptor leaves native counter unchanged"),
            Actor->ParentInvocationCount,
            CounterBeforeRejectedFrames);
    }
    {
        FOwnedParentFrame Owned(false);
        CallFrameTransport->InvokeById(
            NativeContext,
            MatchingHandle.Value,
            EvaluateMemberId,
            &Owned.Frame);
        TestEqual(
            TEXT("Uncorrelated parent frame is rejected before the parent body"),
            Owned.Frame.Status,
            EUnifiedScriptNativeCallStatusV3::InvocationFailure);
        TestEqual(
            TEXT("Uncorrelated parent frame leaves native counter unchanged"),
            Actor->ParentInvocationCount,
            CounterBeforeRejectedFrames);
    }

    AmbiguousComponent = NewObject<UUnifiedScriptComponent>(
        Actor,
        UUnifiedScriptComponent::StaticClass(),
        FName(TEXT("AmbiguousParentInvocationComponent")));
    if (AmbiguousComponent != nullptr)
    {
        Actor->AddInstanceComponent(AmbiguousComponent);
        AmbiguousComponent->ScriptAsset = ScriptAsset;
        AmbiguousComponent->bStartAutomatically = false;
        AmbiguousComponent->bTickAutomatically = false;
        AmbiguousComponent->LifecycleOwnershipKey = FName(TEXT("AmbiguousParentInvocationOwner"));
        AmbiguousComponent->RegisterComponent();
        AmbiguousComponent->BeginPlay();
    }
    if (!TestNotNull(TEXT("Ambiguous owner component is created"), AmbiguousComponent) ||
        !TestTrue(TEXT("Ambiguous owner component starts managed code"),
            AmbiguousComponent != nullptr && AmbiguousComponent->StartScript()))
    {
        return false;
    }
    const int32 CounterBeforeAmbiguous = Actor->ParentInvocationCount;
    AddExpectedError(
        TEXT("has no exact managed owner"),
        EAutomationExpectedErrorFlags::Contains,
        1);
    Actor->ProcessEvent(Evaluate, EvaluateMemory);
    TestEqual(
        TEXT("Ambiguous exact ownership refuses to call a parent"),
        Actor->ParentInvocationCount,
        CounterBeforeAmbiguous);

    AmbiguousComponent->EndPlay(EEndPlayReason::Destroyed);
    AmbiguousComponent->DestroyComponent();
    AmbiguousComponent = nullptr;
    SetInt(*NativeInput, EvaluateMemory, 3);
    SetInt(*NativeAccumulator, EvaluateMemory, 4);
    Actor->ProcessEvent(Evaluate, EvaluateMemory);
    TestEqual(
        TEXT("Original owner resumes after ambiguity is removed"),
        Actor->ParentInvocationCount,
        CounterBeforeAmbiguous + 1);
    return true;
}

#endif

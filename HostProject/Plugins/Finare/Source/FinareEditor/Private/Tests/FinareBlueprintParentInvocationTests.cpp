#if WITH_DEV_AUTOMATION_TESTS

#include "FinareBlueprintClassMaterializer.h"

#include "AssetRegistry/AssetData.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/Engine.h"
#include "EngineUtils.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "Interfaces/IPluginManager.h"
#include "K2Node_FunctionEntry.h"
#include "K2Node_FunctionResult.h"
#include "K2Node_CallFunction.h"
#include "K2Node_CallParentFunction.h"
#include "K2Node_Event.h"
#include "K2Node_VariableSet.h"
#include "K2Node_VariableSetRef.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Parse.h"
#include "Misc/PackageName.h"
#include "Misc/ScopeExit.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/UnrealType.h"

#include "PackageTools.h"
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareComponent.h"
#include "FinareCoreClrHost.h"
#include "FinareEventExtensionFixture.h"
#include "FinareNativeTransport.h"
#include "FinareWorldSubsystem.h"

namespace UE::Finare::BlueprintClass::ParentInvocationTests
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

	UFinareBlueprint* CreateScriptBlueprint(
		UClass* ParentClass,
		const FString& PackageName,
		const FString& AssetName,
		UPackage*& OutPackage,
		const FName& CallingContext)
	{
		OutPackage = CreatePackage(*PackageName);
		return Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
			ParentClass,
			OutPackage,
			FName(*AssetName),
			BPTYPE_Normal,
			UFinareBlueprint::StaticClass(),
			UFinareBlueprintGeneratedClass::StaticClass(),
			CallingContext));
	}

	UFinareBlueprint* CreateBlueprint(
		const FString& PackageName,
		const FString& AssetName,
		UPackage*& OutPackage)
	{
		return CreateScriptBlueprint(
			AFinareEventExtensionFixture::StaticClass(),
			PackageName,
			AssetName,
			OutPackage,
			FName(TEXT("FinareBlueprintParentInvocation")));
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
			FName(TEXT("FinareBlueprintParentInvocationIntermediate")));
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
			AFinareEventExtensionFixture::StaticClass());

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
			     {FName(TEXT("ReturnValue")), TEXT("99")}
		     })
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
					TEXT(
						"The ordinary Blueprint intermediate result pin '%s' could not receive its default; available result pins='%s'."),
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

	bool AddLifecycleChildEvent(
		UBlueprint& Blueprint,
		UClass& ParentClass,
		FName EventName,
		bool bCallParent,
		FString& OutError)
	{
		UEdGraph* EventGraph = Blueprint.UbergraphPages.IsEmpty()
			                       ? nullptr
			                       : Blueprint.UbergraphPages[0];
		if (EventGraph == nullptr)
		{
			OutError = TEXT("The lifecycle child Blueprint has no Ubergraph page.");
			return false;
		}

		int32 NodePositionY = 0;
		UK2Node_Event* Event = FKismetEditorUtilities::AddDefaultEventNode(
			&Blueprint,
			EventGraph,
			EventName,
			&ParentClass,
			NodePositionY);
		if (Event == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Could not add lifecycle event '%s' to the child Blueprint."),
				*EventName.ToString());
			return false;
		}
		Event->SetEnabledState(ENodeEnabledState::Enabled, true);
		UK2Node_VariableSet* SetState = NewObject<UK2Node_VariableSet>(EventGraph);
		SetState->VariableReference.SetSelfMember(TEXT("ManagedState"));
		SetState->CreateNewGuid();
		EventGraph->AddNode(SetState, true, false);
		SetState->AllocateDefaultPins();
		UEdGraphPin* StateValue = SetState->FindPin(TEXT("ManagedState"), EGPD_Input);
		const FString StateMarker = EventName == FName(TEXT("ReceiveBeginPlay"))
			                            ? TEXT("ChildBegin")
			                            : TEXT("ChildEnd");
		if (StateValue == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Lifecycle child event '%s' has no ManagedState value pin."),
				*EventName.ToString());
			return false;
		}
		StateValue->DefaultValue = StateMarker;
		const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
		UEdGraphPin* EventThen = Event->FindPin(UEdGraphSchema_K2::PN_Then, EGPD_Output);
		UEdGraphPin* SetExec = SetState->GetExecPin();
		UEdGraphPin* SetThen = SetState->GetThenPin();
		if (EventThen == nullptr || SetExec == nullptr || SetThen == nullptr ||
			!Schema->TryCreateConnection(EventThen, SetExec))
		{
			OutError = FString::Printf(
				TEXT("Lifecycle child event '%s' could not connect its state body."),
				*EventName.ToString());
			return false;
		}

		if (!bCallParent)
		{
			return true;
		}

		UFunction* ParentFunction = ParentClass.FindFunctionByName(
			EventName,
			EIncludeSuperFlag::IncludeSuper);
		if (ParentFunction == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Lifecycle child event '%s' has no parent UFunction."),
				*EventName.ToString());
			return false;
		}
		FGraphNodeCreator<UK2Node_CallParentFunction> Creator(*EventGraph);
		UK2Node_CallParentFunction* ParentCall = Creator.CreateNode();
		ParentCall->SetFromFunction(ParentFunction);
		ParentCall->AllocateDefaultPins();
		Creator.Finalize();
		UEdGraphPin* ParentExec = ParentCall->GetExecPin();
		if (ParentExec == nullptr || !Schema->TryCreateConnection(SetThen, ParentExec))
		{
			OutError = FString::Printf(
				TEXT("Lifecycle child event '%s' could not connect its parent exec pin."),
				*EventName.ToString());
			return false;
		}

		if (EventName == FName(TEXT("ReceiveEndPlay")))
		{
			UEdGraphPin* EventReason = Event->FindPin(TEXT("EndPlayReason"), EGPD_Output);
			UEdGraphPin* ParentReason = ParentCall->FindPin(TEXT("EndPlayReason"), EGPD_Input);
			if (EventReason == nullptr || ParentReason == nullptr ||
				!Schema->TryCreateConnection(EventReason, ParentReason))
			{
				OutError = TEXT("Lifecycle child ReceiveEndPlay could not connect EndPlayReason.");
				return false;
			}
		}
		return true;
	}

	bool AddObservedReceiveInheritedBody(
		UBlueprint& Blueprint,
		FString& OutError)
	{
		const FName ObservedTextName(TEXT("ObservedText"));
		const FName ObservedActorName(TEXT("ObservedActor"));
		FEdGraphPinType TextType;
		TextType.PinCategory = UEdGraphSchema_K2::PC_String;
		FBlueprintEditorUtils::AddMemberVariable(&Blueprint, ObservedTextName, TextType);
		FEdGraphPinType ActorType;
		ActorType.PinCategory = UEdGraphSchema_K2::PC_Object;
		ActorType.PinSubCategoryObject = AActor::StaticClass();
		FBlueprintEditorUtils::AddMemberVariable(&Blueprint, ObservedActorName, ActorType);

		UEdGraph* EventGraph = Blueprint.UbergraphPages.IsEmpty()
			                       ? nullptr
			                       : Blueprint.UbergraphPages[0];
		if (EventGraph == nullptr)
		{
			OutError = TEXT("The ordinary parent Blueprint has no Ubergraph page.");
			return false;
		}

		int32 NodePositionY = 400;
		UK2Node_Event* Event = FKismetEditorUtilities::AddDefaultEventNode(
			&Blueprint,
			EventGraph,
			FName(TEXT("ReceiveInherited")),
			AFinareEventExtensionFixture::StaticClass(),
			NodePositionY);
		if (Event == nullptr)
		{
			OutError = TEXT("Could not add ReceiveInherited to the ordinary parent Blueprint.");
			return false;
		}
		Event->SetEnabledState(ENodeEnabledState::Enabled, true);
		const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
		UEdGraphPin* EventThen = Event->FindPin(UEdGraphSchema_K2::PN_Then, EGPD_Output);
		UEdGraphPin* EventText = Event->FindPin(TEXT("NativeText"), EGPD_Output);
		UEdGraphPin* EventActor = Event->FindPin(TEXT("NativeActor"), EGPD_Output);
		if (EventThen == nullptr || EventText == nullptr || EventActor == nullptr)
		{
			OutError = TEXT("ReceiveInherited event pins were not materialized.");
			return false;
		}

		auto CreateSet = [&Blueprint, EventGraph](const FName MemberName)
		{
			UK2Node_VariableSet* Set = NewObject<UK2Node_VariableSet>(EventGraph);
			Set->VariableReference.SetSelfMember(MemberName);
			Set->CreateNewGuid();
			EventGraph->AddNode(Set, true, false);
			Set->AllocateDefaultPins();
			return Set;
		};
		UK2Node_VariableSet* SetText = CreateSet(ObservedTextName);
		UK2Node_VariableSet* SetActor = CreateSet(ObservedActorName);
		UEdGraphPin* SetTextValue = SetText->FindPin(ObservedTextName, EGPD_Input);
		UEdGraphPin* SetActorValue = SetActor->FindPin(ObservedActorName, EGPD_Input);
		UEdGraphPin* SetTextExec = SetText->GetExecPin();
		UEdGraphPin* SetTextThen = SetText->GetThenPin();
		UEdGraphPin* SetActorExec = SetActor->GetExecPin();
		if (SetTextValue == nullptr || SetActorValue == nullptr ||
			SetTextExec == nullptr || SetTextThen == nullptr || SetActorExec == nullptr ||
			!Schema->TryCreateConnection(EventThen, SetTextExec) ||
			!Schema->TryCreateConnection(EventText, SetTextValue) ||
			!Schema->TryCreateConnection(SetTextThen, SetActorExec) ||
			!Schema->TryCreateConnection(EventActor, SetActorValue))
		{
			OutError = TEXT("Could not wire ReceiveInherited observations in the ordinary parent Blueprint.");
			return false;
		}
		FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(&Blueprint);
		return true;
	}

	bool ReadCommandLineValue(const TCHAR* Prefix, FString& OutValue)
	{
		if (!FParse::Value(FCommandLine::Get(), Prefix, OutValue))
		{
			return false;
		}
		OutValue.TrimQuotesInline();
		return !OutValue.IsEmpty();
	}

	bool SaveFixturePackage(
		UPackage* Package,
		UObject* Asset,
		const FString& PackageFilename,
		FString& OutError)
	{
		if (Package == nullptr || Asset == nullptr)
		{
			OutError = TEXT("Fixture package or top-level asset is null.");
			return false;
		}
		IFileManager::Get().MakeDirectory(*FPaths::GetPath(PackageFilename), true);
		FSavePackageArgs SaveArguments;
		SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
		SaveArguments.SaveFlags = SAVE_NoError;
		if (!UPackage::SavePackage(
			Package,
			Asset,
			*PackageFilename,
			SaveArguments))
		{
			OutError = FString::Printf(
				TEXT("Could not save fixture package '%s'."),
				*PackageFilename);
			return false;
		}
		return true;
	}

	bool UnloadFixturePackages(TArray<UPackage*>& Packages)
	{
		TArray<UPackage*> ToUnload;
		for (int32 Index = Packages.Num() - 1; Index >= 0; --Index)
		{
			if (Packages[Index] != nullptr)
			{
				Packages[Index]->SetDirtyFlag(false);
				ToUnload.Add(Packages[Index]);
				Packages[Index] = nullptr;
			}
		}
		if (ToUnload.Num() > 0)
		{
			return UPackageTools::UnloadPackages(ToUnload);
		}
		return true;
	}

	bool ValidateReloadedFixtureClasses(
		const TArray<FString>& ClassPaths,
		FString& OutError)
	{
		for (const FString& ClassPath : ClassPaths)
		{
			UClass* LoadedClass = LoadObject<UClass>(nullptr, *ClassPath);
			if (LoadedClass == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Saved fixture class '%s' could not be loaded."),
					*ClassPath);
				return false;
			}
			if (const UFinareBlueprintGeneratedClass* ManagedClass =
				Cast<UFinareBlueprintGeneratedClass>(LoadedClass))
			{
				if (ManagedClass->ManagedFunctionBindings.Num() == 0)
				{
					OutError = FString::Printf(
						TEXT("Reloaded managed fixture class '%s' lost its persisted bindings."),
						*ClassPath);
					return false;
				}
			}
		}
		return true;
	}

	UFinareAsset* AttachScriptAsset(
		UFinareBlueprint& Blueprint,
		const FDemoAsset& DemoAsset,
		const FString& ChunkPath,
		const FString& AssemblyPath,
		const FString& DeclarationPath,
		EFinareClassKind ClassKind = EFinareClassKind::Actor)
	{
		UFinareAsset* ScriptAsset = NewObject<UFinareAsset>(
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
		TArray<FFinareNativeValueV3> Arguments;
		TArray<FFinareNativeValueV3> Results;
		FFinareNativeCallFrameV3 Frame;

		explicit FOwnedParentFrame(bool bWrongResultDescriptor)
		{
			Arguments.SetNumZeroed(3);
			Arguments[0].Kind = EFinareNativeValueKindV3::Int32;
			Arguments[0].Payload0 = 7;
			Arguments[1].Kind = EFinareNativeValueKindV3::Int32;
			Arguments[1].Payload0 = 5;
			Arguments[2].Kind = EFinareNativeValueKindV3::Int32;
			Results.SetNumZeroed(3);
			for (FFinareNativeValueV3& Result : Results)
			{
				Result.Kind = EFinareNativeValueKindV3::Int32;
			}
			if (bWrongResultDescriptor)
			{
				Results[0].Kind = EFinareNativeValueKindV3::ByteBuffer;
			}
			Frame = {};
			Frame.Arguments = Arguments.GetData();
			Frame.ArgumentCount = Arguments.Num();
			Frame.Flags = static_cast<uint32>(EFinareNativeCallFrameFlagsV3::BlueprintParent);
			Frame.Results = Results.GetData();
			Frame.ResultCapacity = Results.Num();
			Frame.ResultCount = Results.Num();
			Frame.Status = EFinareNativeCallStatusV3::Uninitialized;
		}
	};
} // namespace UE::Finare::BlueprintClass::ParentInvocationTests

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareBlueprintParentInvocationTest,
	"Finare.Unreal.ScriptClass.BlueprintParentInvocation",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareBlueprintParentInvocationTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::BlueprintClass::ParentInvocationTests;

	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
	if (!TestNotNull(TEXT("Finare plugin is installed"), Plugin.Get()))
	{
		return false;
	}
	const FString DemoDirectory = FPaths::Combine(
		Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
	const FString DeclarationPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.asset.json"));
	const FString ChunkPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.uschunk"));
	const FString AssemblyPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.dll"));
	FString Declaration;
	if (!TestTrue(TEXT("Demo declaration is staged"),
	              FFileHelper::LoadFileToString(Declaration, *DeclarationPath)) ||
		!TestTrue(TEXT("Demo chunk is staged"), IFileManager::Get().FileExists(*ChunkPath)) ||
		!TestTrue(TEXT("Demo assembly is staged"), IFileManager::Get().FileExists(*AssemblyPath)))
	{
		return false;
	}

	const FString DemoTypeName =
		TEXT("Finare.Unreal.Demo.BlueprintParentInvocationDemoScript");
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
	const FString AssetName = TEXT("BP_FinareParentInvocation_") + Suffix;
	const FString PackageFilename = FPackageName::LongPackageNameToFilename(
		PackageName,
		FPackageName::GetAssetPackageExtension());
	UPackage* Package = nullptr;
	UFinareBlueprint* Blueprint =
		CreateBlueprint(PackageName, AssetName, Package);
	UFinareAsset* ScriptAsset = Blueprint != nullptr
		                            ? AttachScriptAsset(*Blueprint, DemoAsset, ChunkPath, AssemblyPath, DeclarationPath)
		                            : nullptr;
	UWorld* World = nullptr;
	AFinareEventExtensionFixture* Actor = nullptr;
	FWorldContext* WorldContext = nullptr;
	UFinareComponent* Component = nullptr;
	const int32 InitialManagedWorldCount =
		FFinareCoreClrHost::Get().GetManagedWorldCount();

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
			FFinareCoreClrHost::Get().GetManagedWorldCount(),
			InitialManagedWorldCount);
	};

	if (!TestNotNull(TEXT("Parent invocation Blueprint is created"), Blueprint) ||
		!TestNotNull(TEXT("Parent invocation generated class is created"),
		             Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr) ||
		!TestNotNull(TEXT("Parent invocation Script Asset is attached"), ScriptAsset))
	{
		return false;
	}
	FFinareBlueprintClassSyncResult SyncResult;
	Error.Reset();
	if (!TestTrue(*FString::Printf(TEXT("Parent route materializes: %s"), *Error),
	              FFinareBlueprintClassMaterializer::SynchronizeJson(
		              *ScriptAsset, *Blueprint, Declaration, false, SyncResult, Error)))
	{
		return false;
	}

	UFinareBlueprintGeneratedClass* GeneratedClass =
		Cast<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass);
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
	Actor = World->SpawnActor<AFinareEventExtensionFixture>(GeneratedClass);
	Component = Actor != nullptr ? Actor->GetFinareComponent() : nullptr;
	UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
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

	// Exercise the native producer at the real component activation boundary:
	// a persisted-route mutation is rejected before the managed candidate is
	// created, and restoring the binding permits the same generation to start.
	const int32 ManagedScriptsBeforeActivationMismatch = Host->GetManagedScriptCount();
	FFinareManagedFunctionBinding* EvaluateBindingForActivation =
		GeneratedClass->ManagedFunctionBindings.Find(TEXT("EvaluateInherited"));
	const FString OriginalEvaluateRoute = EvaluateBindingForActivation != nullptr
		                                      ? EvaluateBindingForActivation->BlueprintOverrideFunctionName
		                                      : FString();
	Component->StopScript();
	if (EvaluateBindingForActivation != nullptr)
	{
		EvaluateBindingForActivation->BlueprintOverrideFunctionName =
			TEXT("EvaluateInheritedMissing");
	}
	AddExpectedError(
		TEXT("must use inherited event name"),
		EAutomationExpectedErrorFlags::Contains,
		1);
	TestFalse(TEXT("Activation rejects a persisted inherited-event route mismatch"),
	          Component->StartScript());
	TestEqual(TEXT("Activation mismatch does not create a managed candidate"),
	          Host->GetManagedScriptCount(),
	          ManagedScriptsBeforeActivationMismatch - 1);
	TestTrue(TEXT("Activation mismatch reports a native contract diagnostic"),
	         Component->LastScriptError.Contains(TEXT("activation")) ||
	         Component->LastScriptError.Contains(TEXT("binding")) ||
	         Component->LastScriptError.Contains(TEXT("override")));
	if (EvaluateBindingForActivation != nullptr)
	{
		EvaluateBindingForActivation->BlueprintOverrideFunctionName = OriginalEvaluateRoute;
	}
	TestTrue(TEXT("Restored inherited-event route starts again"), Component->StartScript());
	TestEqual(TEXT("Restored route returns to its original managed count"),
	          Host->GetManagedScriptCount(),
	          ManagedScriptsBeforeActivationMismatch);

	// The native class contract still validates when only the persisted
	// managed method identity is stale; the validated managed export must
	// reject before constructing the candidate instance.
	const FString OriginalManagedName = EvaluateBindingForActivation != nullptr
		                                    ? EvaluateBindingForActivation->ManagedName
		                                    : FString();
	const int32 ManagedScriptsBeforeManagedNameMismatch =
		Host->GetManagedScriptCount();
	const int32 RegisteredObjectsBeforeManagedNameMismatch =
		Host->NumRegisteredObjects();
	const int32 LifecycleClaimsBeforeManagedNameMismatch =
		Host->GetGameplayLifecycleClaimCount();
	Component->StopScript();
	const int32 RegisteredObjectsAfterStop = Host->NumRegisteredObjects();
	const int32 LifecycleClaimsAfterStop = Host->GetGameplayLifecycleClaimCount();
	if (EvaluateBindingForActivation != nullptr)
	{
		EvaluateBindingForActivation->ManagedName = TEXT("EvaluateInheritedRenamed");
	}
	AddExpectedError(
		TEXT("managedName expected"),
		EAutomationExpectedErrorFlags::Contains,
		1);
	TestFalse(TEXT("Activation rejects a stale managed override identity"),
	          Component->StartScript());
	TestEqual(TEXT("Managed identity mismatch does not create a candidate"),
	          Host->GetManagedScriptCount(),
	          ManagedScriptsBeforeManagedNameMismatch - 1);
	TestEqual(TEXT("Managed identity mismatch leaves no registered handle"),
	          Host->NumRegisteredObjects(),
	          RegisteredObjectsAfterStop);
	TestEqual(TEXT("Managed identity mismatch leaves lifecycle ownership released"),
	          Host->GetGameplayLifecycleClaimCount(),
	          LifecycleClaimsAfterStop);
	TestTrue(TEXT("Managed identity mismatch reports the route"),
	         Component->LastScriptError.Contains(TEXT("managedName")));
	if (EvaluateBindingForActivation != nullptr)
	{
		EvaluateBindingForActivation->ManagedName = OriginalManagedName;
	}
	TestTrue(TEXT("Restored managed identity starts again"), Component->StartScript());
	TestEqual(TEXT("Restored managed identity keeps the managed count"),
	          Host->GetManagedScriptCount(),
	          ManagedScriptsBeforeManagedNameMismatch);
	TestEqual(TEXT("Restored managed identity restores registered handles"),
	          Host->NumRegisteredObjects(),
	          RegisteredObjectsBeforeManagedNameMismatch);
	TestEqual(TEXT("Restored managed identity restores lifecycle ownership"),
	          Host->GetGameplayLifecycleClaimCount(),
	          LifecycleClaimsBeforeManagedNameMismatch);

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
	FFinareBlueprintIntermediateParentInvocationTest,
	"Finare.Unreal.ScriptClass.BlueprintIntermediateParentInvocation",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareBlueprintIntermediateParentInvocationTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::BlueprintClass::ParentInvocationTests;

	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
	if (!TestNotNull(TEXT("Finare plugin is installed"), Plugin.Get()))
	{
		return false;
	}
	const FString DemoDirectory = FPaths::Combine(
		Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
	const FString DeclarationPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.asset.json"));
	const FString ChunkPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.uschunk"));
	const FString AssemblyPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.dll"));
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
		              TEXT("Finare.Unreal.Demo.BlueprintParentInvocationDemoScript"),
		              DemoAsset,
		              Error)))
	{
		AddError(Error);
		return false;
	}

	const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
	const FString IntermediatePackageName =
		TEXT("/Engine/Transient/FinareParentIntermediate_") + Suffix;
	const FString IntermediateAssetName = TEXT("BP_ParentIntermediate_") + Suffix;
	UPackage* IntermediatePackage = nullptr;
	UBlueprint* IntermediateBlueprint = CreateNormalBlueprint(
		AFinareEventExtensionFixture::StaticClass(),
		IntermediatePackageName,
		IntermediateAssetName,
		IntermediatePackage);
	UPackage* ManagedPackage = nullptr;
	UFinareBlueprint* ManagedBlueprint = nullptr;
	UFinareAsset* ScriptAsset = nullptr;
	UWorld* World = nullptr;
	FWorldContext* WorldContext = nullptr;
	AFinareEventExtensionFixture* Actor = nullptr;
	UFinareComponent* Component = nullptr;
	const int32 InitialManagedWorldCount =
		FFinareCoreClrHost::Get().GetManagedWorldCount();

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
			FFinareCoreClrHost::Get().GetManagedWorldCount(),
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
		*(TEXT("/Engine/Transient/FinareParentManaged_") + Suffix));
	ManagedBlueprint = Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
		IntermediateBlueprint->GeneratedClass,
		ManagedPackage,
		FName(*(TEXT("BP_ParentManaged_") + Suffix)),
		BPTYPE_Normal,
		UFinareBlueprint::StaticClass(),
		UFinareBlueprintGeneratedClass::StaticClass(),
		FName(TEXT("FinareBlueprintParentInvocationManaged"))));
	ScriptAsset = ManagedBlueprint != nullptr
		              ? AttachScriptAsset(*ManagedBlueprint, DemoAsset, ChunkPath, AssemblyPath, DeclarationPath)
		              : nullptr;
	if (!TestNotNull(TEXT("Creates managed Blueprint below the intermediate parent"), ManagedBlueprint) ||
		!TestNotNull(TEXT("Intermediate parent test has a Script Asset"), ScriptAsset))
	{
		return false;
	}

	FFinareBlueprintClassSyncResult SyncResult;
	Error.Reset();
	if (!TestTrue(TEXT("Materializes managed override below ordinary Blueprint parent"),
	              FFinareBlueprintClassMaterializer::SynchronizeJson(
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
	UFinareBlueprintGeneratedClass* ManagedClass =
		Cast<UFinareBlueprintGeneratedClass>(ManagedBlueprint->GeneratedClass);
	UFunction* Evaluate = ManagedClass != nullptr
		                      ? ManagedClass->FindFunctionByName(
			                      TEXT("EvaluateInherited"), EIncludeSuperFlag::ExcludeSuper)
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
	Actor = World->SpawnActor<AFinareEventExtensionFixture>(ManagedClass);
	Component = Actor != nullptr ? Actor->GetFinareComponent() : nullptr;
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
	FFinareBlueprintParentInvocationComponentTest,
	"Finare.Unreal.ScriptClass.BlueprintParentInvocationComponent",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareBlueprintParentInvocationComponentTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::BlueprintClass::ParentInvocationTests;

	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
	if (!TestNotNull(TEXT("Finare plugin is installed"), Plugin.Get()))
	{
		return false;
	}
	const FString DemoDirectory = FPaths::Combine(
		Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
	const FString DeclarationPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.asset.json"));
	const FString ChunkPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.uschunk"));
	const FString AssemblyPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.dll"));
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
		TEXT("Finare.Unreal.Demo.BlueprintParentInvocationComponentDemoScript");
	if (!TestTrue(TEXT("Component parent fixture identity is present"),
	              ReadDemoAsset(Declaration, DemoTypeName, DemoAsset, Error)))
	{
		AddError(Error);
		return false;
	}

	const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
	const FString PackageName = TEXT("/Game/UnifiedScriptParentInvocationComponent_") + Suffix;
	const FString AssetName = TEXT("BP_FinareParentInvocationComponent_") + Suffix;
	const FString PackageFilename = FPackageName::LongPackageNameToFilename(
		PackageName,
		FPackageName::GetAssetPackageExtension());
	UPackage* Package = nullptr;
	UFinareBlueprint* Blueprint = CreateScriptBlueprint(
		UFinareComponent::StaticClass(),
		PackageName,
		AssetName,
		Package,
		FName(TEXT("FinareBlueprintParentInvocationComponent")));
	UFinareAsset* ScriptAsset = Blueprint != nullptr
		                            ? AttachScriptAsset(
			                            *Blueprint,
			                            DemoAsset,
			                            ChunkPath,
			                            AssemblyPath,
			                            DeclarationPath,
			                            EFinareClassKind::ActorComponent)
		                            : nullptr;
	UWorld* World = nullptr;
	FWorldContext* WorldContext = nullptr;
	AActor* Actor = nullptr;
	UFinareComponent* Component = nullptr;
	const int32 InitialManagedWorldCount =
		FFinareCoreClrHost::Get().GetManagedWorldCount();

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
			FFinareCoreClrHost::Get().GetManagedWorldCount(),
			InitialManagedWorldCount);
	};

	if (!TestNotNull(TEXT("Component parent Blueprint is created"), Blueprint) ||
		!TestNotNull(TEXT("Component parent generated class is created"),
		             Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr) ||
		!TestNotNull(TEXT("Component parent Script Asset is attached"), ScriptAsset))
	{
		return false;
	}

	FFinareBlueprintClassSyncResult SyncResult;
	if (!TestTrue(*FString::Printf(TEXT("Materializes component parent route: %s"), *Error),
	              FFinareBlueprintClassMaterializer::SynchronizeJson(
		              *ScriptAsset, *Blueprint, Declaration, false, SyncResult, Error)))
	{
		AddError(Error);
		return false;
	}
	UFinareBlueprintGeneratedClass* GeneratedClass =
		Cast<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass);
	UFunction* ReceiveTick = GeneratedClass != nullptr
		                         ? GeneratedClass->FindFunctionByName(
			                         TEXT("ReceiveTick"), EIncludeSuperFlag::ExcludeSuper)
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
	UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
	Component = Actor != nullptr
		            ? NewObject<UFinareComponent>(
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
	FFinareBlueprintParentInvocationOwnershipTest,
	"Finare.Unreal.ScriptClass.BlueprintParentInvocationOwnership",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareBlueprintParentInvocationOwnershipTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::BlueprintClass::ParentInvocationTests;

	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
	if (!TestNotNull(TEXT("Finare plugin is installed"), Plugin.Get()))
	{
		return false;
	}
	const FString DemoDirectory = FPaths::Combine(
		Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
	const FString DeclarationPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.asset.json"));
	const FString ChunkPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.uschunk"));
	const FString AssemblyPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.dll"));
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
		              TEXT("Finare.Unreal.Demo.BlueprintParentInvocationDemoScript"),
		              DemoAsset,
		              Error)))
	{
		AddError(Error);
		return false;
	}

	const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
	const FString PackageName = TEXT("/Game/UnifiedScriptParentInvocationOwnership_") + Suffix;
	const FString AssetName = TEXT("BP_FinareParentInvocationOwnership_") + Suffix;
	const FString PackageFilename = FPackageName::LongPackageNameToFilename(
		PackageName,
		FPackageName::GetAssetPackageExtension());
	UPackage* Package = nullptr;
	UFinareBlueprint* Blueprint = CreateBlueprint(PackageName, AssetName, Package);
	UFinareAsset* ScriptAsset = Blueprint != nullptr
		                            ? AttachScriptAsset(*Blueprint, DemoAsset, ChunkPath, AssemblyPath, DeclarationPath)
		                            : nullptr;
	UWorld* World = nullptr;
	FWorldContext* WorldContext = nullptr;
	AFinareEventExtensionFixture* Actor = nullptr;
	UFinareComponent* CanonicalComponent = nullptr;
	UFinareComponent* MatchingComponent = nullptr;
	UFinareComponent* AmbiguousComponent = nullptr;
	const int32 InitialManagedWorldCount =
		FFinareCoreClrHost::Get().GetManagedWorldCount();

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
			FFinareCoreClrHost::Get().GetManagedWorldCount(),
			InitialManagedWorldCount);
	};

	if (!TestNotNull(TEXT("Ownership Blueprint is created"), Blueprint) ||
		!TestNotNull(TEXT("Ownership Script Asset is attached"), ScriptAsset))
	{
		return false;
	}
	FFinareBlueprintClassSyncResult SyncResult;
	if (!TestTrue(TEXT("Ownership actor route materializes"),
	              FFinareBlueprintClassMaterializer::SynchronizeJson(
		              *ScriptAsset, *Blueprint, Declaration, false, SyncResult, Error)))
	{
		AddError(Error);
		return false;
	}
	UFinareBlueprintGeneratedClass* GeneratedClass =
		Cast<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass);
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
	Actor = World->SpawnActor<AFinareEventExtensionFixture>(GeneratedClass);
	UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
	CanonicalComponent = Actor != nullptr ? Actor->GetFinareComponent() : nullptr;
	if (!TestNotNull(TEXT("Ownership Actor spawns"), Actor) ||
		!TestNotNull(TEXT("Ownership World host exists"), Host) ||
		!TestNotNull(TEXT("Canonical component is enumerated first"), CanonicalComponent))
	{
		return false;
	}

	// Leave the actor's canonical component present but cold. It still points
	// at the configured generated owner, so a configured-owner lookup must
	// not steal a running call from the later active owner below.
	CanonicalComponent->bStartAutomatically = false;
	CanonicalComponent->ScriptAsset = ScriptAsset;
	MatchingComponent = NewObject<UFinareComponent>(
		Actor,
		UFinareComponent::StaticClass(),
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
	TestTrue(
		TEXT("Cold configured component still has the exact generated identity"),
		CanonicalComponent->OwnsConfiguredManagedOverride(GeneratedClass));

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

	const FFinareManagedFunctionBinding* EvaluateBinding =
		GeneratedClass->ManagedFunctionBindings.Find(TEXT("EvaluateInherited"));
	const FString EvaluateMemberPath = EvaluateBinding != nullptr
		                                   ? EvaluateBinding->BlueprintOverrideDeclaringTypePath + TEXT(":") +
		                                   EvaluateBinding->BlueprintOverrideFunctionName
		                                   : FString();
	const uint64 EvaluateMemberId = FinareMemberId(TCHAR_TO_UTF8(*EvaluateMemberPath));
	FFinareObjectHandleResult HandleResult;
	const FFinareObjectHandle MatchingHandle =
		Host->RegisterObject(MatchingComponent, HandleResult);
	const FFinareObjectHandle ActorHandle =
		Host->RegisterObject(Actor, HandleResult);
	const FFinareNativeTransportV3* CallFrameTransport =
		Finare_GetNativeTransportV3();
	if (!TestNotNull(TEXT("Parent rejection test exposes ABI v3 transport"), CallFrameTransport) ||
		!TestTrue(TEXT("Parent rejection test registers component host"), !MatchingHandle.IsNull()) ||
		!TestTrue(TEXT("Parent rejection test registers Actor handle"), !ActorHandle.IsNull()))
	{
		return false;
	}
	FFinareNativeBridgeContext* NativeContext = Host->GetNativeInvocationContext();
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
			EFinareNativeCallStatusV3::InvocationFailure);
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
			EFinareNativeCallStatusV3::InvocationFailure);
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
			EFinareNativeCallStatusV3::InvocationFailure);
		TestEqual(
			TEXT("Uncorrelated parent frame leaves native counter unchanged"),
			Actor->ParentInvocationCount,
			CounterBeforeRejectedFrames);
	}

	AmbiguousComponent = NewObject<UFinareComponent>(
		Actor,
		UFinareComponent::StaticClass(),
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

namespace UE::Finare::BlueprintClass::LiveSyncTests
{
	struct FLiveWorld
	{
		UWorld* World = nullptr;
		FWorldContext* WorldContext = nullptr;
		AFinareEventExtensionFixture* Actor = nullptr;
		UFinareComponent* Component = nullptr;
		UFinareWorldSubsystem* Host = nullptr;
	};

	bool RewriteEvaluateOverride(
		const FString& Source,
		const FString& DemoTypeName,
		bool bRemoveOverride,
		bool bLowercaseFunctionName,
		const FString& ReplacementFunctionName,
		FString& OutJson,
		FString& OutError)
	{
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Source);
		if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
		{
			OutError = TEXT("Live Sync declaration is not valid JSON.");
			return false;
		}
		const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
		if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr)
		{
			OutError = TEXT("Live Sync declaration has no assets array.");
			return false;
		}
		for (const TSharedPtr<FJsonValue>& AssetValue : *Assets)
		{
			const TSharedPtr<FJsonObject> Asset = AssetValue.IsValid()
				                                      ? AssetValue->AsObject()
				                                      : nullptr;
			FString QualifiedName;
			if (!Asset.IsValid() ||
				!Asset->TryGetStringField(TEXT("qualifiedName"), QualifiedName) ||
				!QualifiedName.Equals(DemoTypeName, ESearchCase::CaseSensitive))
			{
				continue;
			}
			const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
			if (!Asset->TryGetArrayField(TEXT("functions"), Functions) || Functions == nullptr)
			{
				OutError = TEXT("Live Sync fixture asset has no functions array.");
				return false;
			}
			for (const TSharedPtr<FJsonValue>& FunctionValue : *Functions)
			{
				const TSharedPtr<FJsonObject> Function = FunctionValue.IsValid()
					                                         ? FunctionValue->AsObject()
					                                         : nullptr;
				FString Name;
				if (!Function.IsValid() ||
					!Function->TryGetStringField(TEXT("name"), Name) ||
					!Name.Equals(TEXT("EvaluateInherited"), ESearchCase::CaseSensitive))
				{
					continue;
				}
				if (bRemoveOverride)
				{
					Function->RemoveField(TEXT("blueprintEvent"));
				}
				else if (bLowercaseFunctionName || !ReplacementFunctionName.IsEmpty())
				{
					const TSharedPtr<FJsonObject>* BlueprintEvent = nullptr;
					const TSharedPtr<FJsonObject>* Override = nullptr;
					if (!Function->TryGetObjectField(TEXT("blueprintEvent"), BlueprintEvent) ||
						BlueprintEvent == nullptr ||
						!(*BlueprintEvent)->TryGetObjectField(TEXT("override"), Override) ||
						Override == nullptr ||
						!Override->IsValid())
					{
						OutError = TEXT("Live Sync fixture EvaluateInherited has no override object.");
						return false;
					}
					(*Override)->SetStringField(
						TEXT("functionName"),
						ReplacementFunctionName.IsEmpty()
							? TEXT("evaluateInherited")
							: *ReplacementFunctionName);
				}
				const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutJson);
				if (!FJsonSerializer::Serialize(Root.ToSharedRef(), Writer))
				{
					OutError = TEXT("Could not serialize the live Sync declaration mutation.");
					return false;
				}
				return true;
			}
		}
		OutError = TEXT("Live Sync fixture asset does not contain EvaluateInherited.");
		return false;
	}

	bool RewriteEvaluateParameter(
		const FString& Source,
		const FString& DemoTypeName,
		int32 ParameterIndex,
		const TCHAR* FieldName,
		const FString& FieldValue,
		FString& OutJson,
		FString& OutError)
	{
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Source);
		if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
		{
			OutError = TEXT("Live Sync parameter declaration is not valid JSON.");
			return false;
		}
		const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
		if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr)
		{
			OutError = TEXT("Live Sync parameter declaration has no assets array.");
			return false;
		}
		for (const TSharedPtr<FJsonValue>& AssetValue : *Assets)
		{
			const TSharedPtr<FJsonObject> Asset = AssetValue.IsValid()
				                                      ? AssetValue->AsObject()
				                                      : nullptr;
			FString QualifiedName;
			if (!Asset.IsValid() ||
				!Asset->TryGetStringField(TEXT("qualifiedName"), QualifiedName) ||
				!QualifiedName.Equals(DemoTypeName, ESearchCase::CaseSensitive))
			{
				continue;
			}
			const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
			if (!Asset->TryGetArrayField(TEXT("functions"), Functions) || Functions == nullptr)
			{
				break;
			}
			for (const TSharedPtr<FJsonValue>& FunctionValue : *Functions)
			{
				const TSharedPtr<FJsonObject> Function = FunctionValue.IsValid()
					                                         ? FunctionValue->AsObject()
					                                         : nullptr;
				FString Name;
				if (!Function.IsValid() ||
					!Function->TryGetStringField(TEXT("name"), Name) ||
					!Name.Equals(TEXT("EvaluateInherited"), ESearchCase::CaseSensitive))
				{
					continue;
				}
				const TArray<TSharedPtr<FJsonValue>>* Parameters = nullptr;
				if (!Function->TryGetArrayField(TEXT("parameters"), Parameters) ||
					Parameters == nullptr || !Parameters->IsValidIndex(ParameterIndex))
				{
					OutError = TEXT("Live Sync EvaluateInherited parameter index is invalid.");
					return false;
				}
				const TSharedPtr<FJsonObject> Parameter = (*Parameters)[ParameterIndex].IsValid()
					                                          ? (*Parameters)[ParameterIndex]->AsObject()
					                                          : nullptr;
				if (!Parameter.IsValid())
				{
					OutError = TEXT("Live Sync EvaluateInherited parameter is not an object.");
					return false;
				}
				Parameter->SetStringField(FieldName, FieldValue);
				const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutJson);
				return FJsonSerializer::Serialize(Root.ToSharedRef(), Writer);
			}
		}
		OutError = TEXT("Live Sync fixture asset does not contain EvaluateInherited parameters.");
		return false;
	}

	bool AppendEvaluateAlternateOverride(
		const FString& Source,
		const FString& DemoTypeName,
		FString& OutJson,
		FString& OutError)
	{
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Source);
		if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
		{
			OutError = TEXT("Live Sync addition declaration is not valid JSON.");
			return false;
		}
		const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
		if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr)
		{
			OutError = TEXT("Live Sync addition declaration has no assets array.");
			return false;
		}
		for (const TSharedPtr<FJsonValue>& AssetValue : *Assets)
		{
			const TSharedPtr<FJsonObject> Asset = AssetValue.IsValid()
				                                      ? AssetValue->AsObject()
				                                      : nullptr;
			FString QualifiedName;
			if (!Asset.IsValid() ||
				!Asset->TryGetStringField(TEXT("qualifiedName"), QualifiedName) ||
				!QualifiedName.Equals(DemoTypeName, ESearchCase::CaseSensitive))
			{
				continue;
			}
			const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
			if (!Asset->TryGetArrayField(TEXT("functions"), Functions) || Functions == nullptr)
			{
				OutError = TEXT("Live Sync addition fixture has no functions array.");
				return false;
			}
			for (const TSharedPtr<FJsonValue>& FunctionValue : *Functions)
			{
				const TSharedPtr<FJsonObject> Function = FunctionValue.IsValid()
					                                         ? FunctionValue->AsObject()
					                                         : nullptr;
				FString Name;
				if (!Function.IsValid() ||
					!Function->TryGetStringField(TEXT("name"), Name) ||
					!Name.Equals(TEXT("EvaluateInherited"), ESearchCase::CaseSensitive))
				{
					continue;
				}
				TSharedPtr<FJsonObject> Added = MakeShared<FJsonObject>(*Function);
				Added->SetStringField(TEXT("id"), TEXT("us_11111111111111111111111111111111"));
				Added->SetStringField(TEXT("name"), TEXT("EvaluateAlternate"));
				Added->SetStringField(
					TEXT("qualifiedName"),
					TEXT(
						"Finare.Unreal.Demo.BlueprintParentInvocationDemoScript.EvaluateAlternate(System.Int32,ref System.Int32,out System.Int32)"));
				const TArray<TSharedPtr<FJsonValue>>* Parameters = nullptr;
				if (!Function->TryGetArrayField(TEXT("parameters"), Parameters) ||
					Parameters == nullptr || Parameters->Num() != 3)
				{
					OutError = TEXT("Live Sync addition source has an unexpected parameter list.");
					return false;
				}
				TArray<TSharedPtr<FJsonValue>> NewParameters;
				NewParameters.Reserve(Parameters->Num());
				const TCHAR* ParameterIds[] = {
					TEXT("us_22222222222222222222222222222222"),
					TEXT("us_33333333333333333333333333333333"),
					TEXT("us_44444444444444444444444444444444")
				};
				for (int32 Index = 0; Index < Parameters->Num(); ++Index)
				{
					const TSharedPtr<FJsonObject> SourceParameter = (*Parameters)[Index].IsValid()
						                                                ? (*Parameters)[Index]->AsObject()
						                                                : nullptr;
					if (!SourceParameter.IsValid())
					{
						OutError = TEXT("Live Sync addition parameter is not an object.");
						return false;
					}
					TSharedPtr<FJsonObject> Parameter = MakeShared<FJsonObject>(*SourceParameter);
					Parameter->SetStringField(TEXT("id"), ParameterIds[Index]);
					NewParameters.Add(MakeShared<FJsonValueObject>(Parameter));
				}
				Added->SetArrayField(TEXT("parameters"), MoveTemp(NewParameters));

				const TSharedPtr<FJsonObject>* BlueprintEvent = nullptr;
				const TSharedPtr<FJsonObject>* Override = nullptr;
				if (!Function->TryGetObjectField(TEXT("blueprintEvent"), BlueprintEvent) ||
					BlueprintEvent == nullptr ||
					!(*BlueprintEvent)->TryGetObjectField(TEXT("override"), Override) ||
					Override == nullptr || !Override->IsValid())
				{
					OutError = TEXT("Live Sync addition source has no override metadata.");
					return false;
				}
				TSharedPtr<FJsonObject> NewBlueprintEvent =
					MakeShared<FJsonObject>(**BlueprintEvent);
				TSharedPtr<FJsonObject> NewOverride = MakeShared<FJsonObject>(**Override);
				NewOverride->SetStringField(TEXT("functionName"), TEXT("EvaluateInheritedAlternate"));
				NewBlueprintEvent->SetObjectField(TEXT("override"), NewOverride);
				Added->SetObjectField(TEXT("blueprintEvent"), NewBlueprintEvent);

				TArray<TSharedPtr<FJsonValue>> NewFunctions = *Functions;
				NewFunctions.Add(MakeShared<FJsonValueObject>(Added));
				Asset->SetArrayField(TEXT("functions"), MoveTemp(NewFunctions));
				const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutJson);
				return FJsonSerializer::Serialize(Root.ToSharedRef(), Writer);
			}
		}
		OutError = TEXT("Live Sync addition fixture asset does not contain EvaluateInherited.");
		return false;
	}

	bool RewriteEvaluateManagedName(
		const FString& Source,
		const FString& DemoTypeName,
		FString& OutJson,
		FString& OutError)
	{
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Source);
		if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
		{
			OutError = TEXT("Live Sync managed-name declaration is not valid JSON.");
			return false;
		}
		const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
		if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr)
		{
			OutError = TEXT("Live Sync managed-name declaration has no assets array.");
			return false;
		}
		for (const TSharedPtr<FJsonValue>& AssetValue : *Assets)
		{
			const TSharedPtr<FJsonObject> Asset = AssetValue.IsValid()
				                                      ? AssetValue->AsObject()
				                                      : nullptr;
			FString QualifiedName;
			if (!Asset.IsValid() ||
				!Asset->TryGetStringField(TEXT("qualifiedName"), QualifiedName) ||
				!QualifiedName.Equals(DemoTypeName, ESearchCase::CaseSensitive))
			{
				continue;
			}
			const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
			if (!Asset->TryGetArrayField(TEXT("functions"), Functions) || Functions == nullptr)
			{
				break;
			}
			for (const TSharedPtr<FJsonValue>& FunctionValue : *Functions)
			{
				const TSharedPtr<FJsonObject> Function = FunctionValue.IsValid()
					                                         ? FunctionValue->AsObject()
					                                         : nullptr;
				FString Name;
				if (!Function.IsValid() ||
					!Function->TryGetStringField(TEXT("name"), Name) ||
					!Name.Equals(TEXT("EvaluateInherited"), ESearchCase::CaseSensitive))
				{
					continue;
				}
				Function->SetStringField(TEXT("name"), TEXT("evaluateInherited"));
				Function->SetStringField(
					TEXT("qualifiedName"),
					TEXT(
						"Finare.Unreal.Demo.BlueprintParentInvocationDemoScript.evaluateInherited(System.Int32,ref System.Int32,out System.Int32)"));
				const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutJson);
				return FJsonSerializer::Serialize(Root.ToSharedRef(), Writer);
			}
		}
		OutError = TEXT("Live Sync fixture asset does not contain EvaluateInherited.");
		return false;
	}

	bool StartLiveWorld(
		UFinareBlueprintGeneratedClass& GeneratedClass,
		UFinareAsset& ScriptAsset,
		FLiveWorld& OutWorld,
		FString& OutError)
	{
		OutWorld.World = UWorld::CreateWorld(EWorldType::Game, false);
		if (OutWorld.World == nullptr)
		{
			OutError = TEXT("Could not create a live Sync test World.");
			return false;
		}
		OutWorld.World->AddToRoot();
		if (GEngine != nullptr)
		{
			OutWorld.WorldContext = &GEngine->CreateNewWorldContext(EWorldType::Game);
			OutWorld.WorldContext->SetCurrentWorld(OutWorld.World);
		}
		OutWorld.World->InitializeActorsForPlay(FURL());
		OutWorld.Actor = OutWorld.World->SpawnActor<AFinareEventExtensionFixture>(
			&GeneratedClass);
		OutWorld.Component = OutWorld.Actor != nullptr
			                     ? OutWorld.Actor->GetFinareComponent()
			                     : nullptr;
		OutWorld.Host = OutWorld.World->GetSubsystem<UFinareWorldSubsystem>();
		if (OutWorld.Actor == nullptr || OutWorld.Component == nullptr || OutWorld.Host == nullptr)
		{
			OutError = TEXT("Live Sync test World could not create its Actor, Component, or host.");
			return false;
		}
		OutWorld.Component->ScriptAsset = &ScriptAsset;
		OutWorld.Component->bStartAutomatically = false;
		OutWorld.Component->BeginPlay();
		if (!OutWorld.Component->StartScript())
		{
			OutError = OutWorld.Component->LastScriptError;
			return false;
		}
		return true;
	}

	void StopLiveWorld(FLiveWorld& LiveWorld)
	{
		if (LiveWorld.Component != nullptr)
		{
			LiveWorld.Component->StopScript();
		}
	}

	void DestroyLiveWorld(FLiveWorld& LiveWorld)
	{
		if (LiveWorld.Component != nullptr)
		{
			if (LiveWorld.Component->HasBegunPlay())
			{
				LiveWorld.Component->EndPlay(EEndPlayReason::Destroyed);
			}
			LiveWorld.Component = nullptr;
		}
		if (LiveWorld.Actor != nullptr && !LiveWorld.Actor->IsActorBeingDestroyed())
		{
			LiveWorld.Actor->Destroy();
			LiveWorld.Actor = nullptr;
		}
		if (LiveWorld.WorldContext != nullptr && GEngine != nullptr)
		{
			GEngine->DestroyWorldContext(LiveWorld.World);
			LiveWorld.WorldContext = nullptr;
		}
		if (LiveWorld.World != nullptr)
		{
			LiveWorld.World->RemoveFromRoot();
			LiveWorld.World->DestroyWorld(false);
			LiveWorld.World = nullptr;
		}
		LiveWorld.Host = nullptr;
	}

	void RefreshLiveWorldAfterBlueprintReinstance(FLiveWorld& LiveWorld)
	{
		if (LiveWorld.World == nullptr)
		{
			return;
		}
		LiveWorld.Actor = nullptr;
		for (TActorIterator<AFinareEventExtensionFixture> It(LiveWorld.World); It; ++It)
		{
			LiveWorld.Actor = *It;
			break;
		}
		LiveWorld.Component = LiveWorld.Actor != nullptr
			                      ? LiveWorld.Actor->GetFinareComponent()
			                      : nullptr;
		LiveWorld.Host = LiveWorld.World->GetSubsystem<UFinareWorldSubsystem>();
	}

	bool InvokeEvaluate(
		FLiveWorld& LiveWorld,
		UFunction& Evaluate,
		int32 Input,
		int32 Accumulator,
		int32& OutReturn)
	{
		const FProperty* NativeInput = FindFProperty<FProperty>(&Evaluate, TEXT("NativeInput"));
		const FProperty* NativeAccumulator = FindFProperty<FProperty>(
			&Evaluate,
			TEXT("NativeAccumulator"));
		const FProperty* ReturnValue = Evaluate.GetReturnProperty();
		if (NativeInput == nullptr || NativeAccumulator == nullptr || ReturnValue == nullptr ||
			CastField<FIntProperty>(NativeInput) == nullptr ||
			CastField<FIntProperty>(NativeAccumulator) == nullptr ||
			CastField<FIntProperty>(ReturnValue) == nullptr)
		{
			return false;
		}
		FStructOnScope Call(&Evaluate);
		UE::Finare::BlueprintClass::ParentInvocationTests::SetInt(
			*NativeInput,
			Call.GetStructMemory(),
			Input);
		UE::Finare::BlueprintClass::ParentInvocationTests::SetInt(
			*NativeAccumulator,
			Call.GetStructMemory(),
			Accumulator);
		LiveWorld.Actor->ProcessEvent(&Evaluate, Call.GetStructMemory());
		OutReturn = UE::Finare::BlueprintClass::ParentInvocationTests::GetInt(
			*ReturnValue,
			Call.GetStructMemory());
		return true;
	}
} // namespace UE::Finare::BlueprintClass::LiveSyncTests

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareBlueprintOverrideLiveSyncTest,
	"Finare.Unreal.ScriptClass.BlueprintOverrideLiveSync",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareBlueprintOverrideLiveSyncTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::BlueprintClass::ParentInvocationTests;
	using namespace UE::Finare::BlueprintClass::LiveSyncTests;

	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
	if (!TestNotNull(TEXT("Finare plugin is installed"), Plugin.Get()))
	{
		return false;
	}
	const FString DemoDirectory = FPaths::Combine(
		Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
	const FString DeclarationPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.asset.json"));
	const FString ChunkPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.uschunk"));
	const FString AssemblyPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.dll"));
	FString Declaration;
	if (!TestTrue(TEXT("Live Sync declaration is staged"),
	              FFileHelper::LoadFileToString(Declaration, *DeclarationPath)) ||
		!TestTrue(TEXT("Live Sync chunk is staged"), IFileManager::Get().FileExists(*ChunkPath)) ||
		!TestTrue(TEXT("Live Sync assembly is staged"), IFileManager::Get().FileExists(*AssemblyPath)))
	{
		return false;
	}

	const FString DemoTypeName =
		TEXT("Finare.Unreal.Demo.BlueprintParentInvocationDemoScript");
	FDemoAsset DemoAsset;
	FString Error;
	if (!TestTrue(TEXT("Live Sync fixture identity is present"),
	              ReadDemoAsset(Declaration, DemoTypeName, DemoAsset, Error)))
	{
		AddError(Error);
		return false;
	}

	FString RemovedRouteDeclaration;
	FString LowercaseRouteDeclaration;
	FString RetargetedRouteDeclaration;
	FString LowercaseManagedNameDeclaration;
	FString PrimitiveMismatchDeclaration;
	FString DirectionMismatchDeclaration;
	FString AdditionDeclaration;
	if (!TestTrue(TEXT("Live Sync removal declaration is generated"),
	              RewriteEvaluateOverride(
		              Declaration,
		              DemoTypeName,
		              true,
		              false,
		              FString(),
		              RemovedRouteDeclaration,
		              Error)) ||
		!TestTrue(TEXT("Live Sync case-only route declaration is generated"),
		          RewriteEvaluateOverride(
			          Declaration,
			          DemoTypeName,
			          false,
			          true,
			          FString(),
			          LowercaseRouteDeclaration,
			          Error)) ||
		!TestTrue(TEXT("Live Sync retarget declaration is generated"),
		          RewriteEvaluateOverride(
			          Declaration,
			          DemoTypeName,
			          false,
			          false,
			          TEXT("EvaluateInheritedAlternate"),
			          RetargetedRouteDeclaration,
			          Error)) ||
		!TestTrue(TEXT("Live Sync managed-name declaration is generated"),
		          RewriteEvaluateManagedName(
			          Declaration,
			          DemoTypeName,
			          LowercaseManagedNameDeclaration,
			          Error)) ||
		!TestTrue(TEXT("Live Sync primitive mismatch declaration is generated"),
		          RewriteEvaluateParameter(
			          Declaration,
			          DemoTypeName,
			          0,
			          TEXT("typeMetadataName"),
			          TEXT("System.String"),
			          PrimitiveMismatchDeclaration,
			          Error)) ||
		!TestTrue(TEXT("Live Sync direction mismatch declaration is generated"),
		          RewriteEvaluateParameter(
			          Declaration,
			          DemoTypeName,
			          1,
			          TEXT("direction"),
			          TEXT("output"),
			          DirectionMismatchDeclaration,
			          Error)) ||
		!TestTrue(TEXT("Live Sync addition declaration is generated"),
		          AppendEvaluateAlternateOverride(
			          Declaration,
			          DemoTypeName,
			          AdditionDeclaration,
			          Error)))
	{
		AddError(Error);
		return false;
	}

	const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
	const FString PackageName = TEXT("/Game/UnifiedScriptOverrideLiveSync_") + Suffix;
	const FString AssetName = TEXT("BP_FinareOverrideLiveSync_") + Suffix;
	const FString PackageFilename = FPackageName::LongPackageNameToFilename(
		PackageName,
		FPackageName::GetAssetPackageExtension());
	UPackage* Package = nullptr;
	UFinareBlueprint* Blueprint = CreateBlueprint(PackageName, AssetName, Package);
	UFinareAsset* ScriptAsset = Blueprint != nullptr
		                            ? AttachScriptAsset(*Blueprint, DemoAsset, ChunkPath, AssemblyPath, DeclarationPath)
		                            : nullptr;
	UFinareBlueprintGeneratedClass* GeneratedClass = nullptr;
	FLiveWorld First;
	FLiveWorld Second;
	UPackage* OtherPackage = nullptr;
	FString OtherPackageFilename;
	UFinareBlueprint* OtherBlueprint = nullptr;
	UFinareAsset* OtherScriptAsset = nullptr;
	FLiveWorld Other;
	const int32 InitialManagedWorldCount =
		FFinareCoreClrHost::Get().GetManagedWorldCount();

	ON_SCOPE_EXIT
	{
		DestroyLiveWorld(Other);
		DestroyLiveWorld(Second);
		DestroyLiveWorld(First);
		OtherBlueprint = nullptr;
		Blueprint = nullptr;
		if (OtherPackage != nullptr || Package != nullptr)
		{
			TArray<UPackage*> Packages;
			if (OtherPackage != nullptr)
			{
				OtherPackage->SetDirtyFlag(false);
				Packages.Add(OtherPackage);
			}
			if (Package != nullptr)
			{
				Package->SetDirtyFlag(false);
				Packages.Add(Package);
			}
			UPackageTools::UnloadPackages(Packages);
			OtherPackage = nullptr;
			Package = nullptr;
		}
		IFileManager::Get().Delete(*PackageFilename, false, true, true);
		if (!OtherPackageFilename.IsEmpty())
		{
			IFileManager::Get().Delete(*OtherPackageFilename, false, true, true);
		}
		TestEqual(
			TEXT("Live Sync cleanup releases managed Worlds"),
			FFinareCoreClrHost::Get().GetManagedWorldCount(),
			InitialManagedWorldCount);
	};

	if (!TestNotNull(TEXT("Live Sync Blueprint is created"), Blueprint) ||
		!TestNotNull(TEXT("Live Sync Script Asset is attached"), ScriptAsset))
	{
		return false;
	}
	FFinareBlueprintClassSyncResult SyncResult;
	if (!TestTrue(TEXT("Live Sync initial declaration materializes"),
	              FFinareBlueprintClassMaterializer::SynchronizeJson(
		              *ScriptAsset, *Blueprint, Declaration, false, SyncResult, Error)))
	{
		AddError(Error);
		return false;
	}
	GeneratedClass = Cast<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass);
	UFunction* Evaluate = GeneratedClass != nullptr
		                      ? GeneratedClass->FindFunctionByName(
			                      TEXT("EvaluateInherited"), EIncludeSuperFlag::ExcludeSuper)
		                      : nullptr;
	if (!TestNotNull(TEXT("Live Sync generated class exists"), GeneratedClass) ||
		!TestNotNull(TEXT("Live Sync EvaluateInherited exists"), Evaluate) ||
		!TestTrue(TEXT("Live Sync generated class has the initial route"),
		          GeneratedClass->IsManagedFunctionBound(TEXT("EvaluateInherited"))))
	{
		return false;
	}

	Error.Reset();
	if (!TestTrue(TEXT("First live World starts"),
	              StartLiveWorld(*GeneratedClass, *ScriptAsset, First, Error)))
	{
		AddError(Error);
		return false;
	}
	Error.Reset();
	if (!TestTrue(TEXT("Second live World starts"),
	              StartLiveWorld(*GeneratedClass, *ScriptAsset, Second, Error)))
	{
		AddError(Error);
		return false;
	}

	// A byte-for-byte same contract is an idempotent no-change operation even
	// while both Worlds own the exact generated class.
	Error.Reset();
	TestTrue(TEXT("Same override contract is idempotent with active Worlds"),
	         FFinareBlueprintClassMaterializer::SynchronizeJson(
		         *ScriptAsset, *Blueprint, Declaration, false, SyncResult, Error));
	TestTrue(TEXT("Idempotent live Sync has no diagnostic"), Error.IsEmpty());

	FString ActiveContractBefore;
	FString ActiveContractError;
	TestTrue(TEXT("Active owner has a readable persisted override contract before rejection"),
	         GeneratedClass->BuildBlueprintOverrideActivationContract(
		         ActiveContractBefore,
		         ActiveContractError));
	UFinareBlueprintGeneratedClass* GeneratedClassBeforeRejected = GeneratedClass;
	const TArray<UEdGraph*> FunctionGraphsBeforeRejected = Blueprint->FunctionGraphs;
	const FString FirstGenerationBeforeRejected = First.Component->ActiveGeneration;
	const FString SecondGenerationBeforeRejected = Second.Component->ActiveGeneration;
	const int32 FirstManagedScriptsBeforeRejected = First.Host->GetManagedScriptCount();
	const int32 SecondManagedScriptsBeforeRejected = Second.Host->GetManagedScriptCount();
	const int32 FirstRegisteredObjectsBeforeRejected = First.Host->NumRegisteredObjects();
	const int32 SecondRegisteredObjectsBeforeRejected = Second.Host->NumRegisteredObjects();
	const int32 FirstLifecycleClaimsBeforeRejected = First.Host->GetGameplayLifecycleClaimCount();
	const int32 SecondLifecycleClaimsBeforeRejected = Second.Host->GetGameplayLifecycleClaimCount();
	auto VerifyRejectedState = [this, &Blueprint, &First, &Second, GeneratedClassBeforeRejected,
			&FunctionGraphsBeforeRejected, &ActiveContractBefore, &ActiveContractError,
			FirstGenerationBeforeRejected, SecondGenerationBeforeRejected,
			FirstManagedScriptsBeforeRejected, SecondManagedScriptsBeforeRejected,
			FirstRegisteredObjectsBeforeRejected, SecondRegisteredObjectsBeforeRejected,
			FirstLifecycleClaimsBeforeRejected, SecondLifecycleClaimsBeforeRejected](
		const TCHAR* Label)
	{
		TestTrue(
			*FString::Printf(TEXT("%s keeps the generated class"), Label),
			Blueprint->GeneratedClass == GeneratedClassBeforeRejected);
		TestEqual(
			*FString::Printf(TEXT("%s keeps the function graph count"), Label),
			Blueprint->FunctionGraphs.Num(),
			FunctionGraphsBeforeRejected.Num());
		const int32 GraphCount = FMath::Min(
			Blueprint->FunctionGraphs.Num(),
			FunctionGraphsBeforeRejected.Num());
		for (int32 Index = 0; Index < GraphCount; ++Index)
		{
			TestTrue(
				*FString::Printf(TEXT("%s keeps function graph %d"), Label, Index),
				Blueprint->FunctionGraphs[Index] == FunctionGraphsBeforeRejected[Index]);
		}
		TestEqual(
			*FString::Printf(TEXT("%s keeps First ActiveGeneration"), Label),
			First.Component->ActiveGeneration,
			FirstGenerationBeforeRejected);
		TestEqual(
			*FString::Printf(TEXT("%s keeps Second ActiveGeneration"), Label),
			Second.Component->ActiveGeneration,
			SecondGenerationBeforeRejected);
		TestEqual(
			*FString::Printf(TEXT("%s keeps First managed script count"), Label),
			First.Host->GetManagedScriptCount(),
			FirstManagedScriptsBeforeRejected);
		TestEqual(
			*FString::Printf(TEXT("%s keeps Second managed script count"), Label),
			Second.Host->GetManagedScriptCount(),
			SecondManagedScriptsBeforeRejected);
		TestEqual(
			*FString::Printf(TEXT("%s keeps First registered object count"), Label),
			First.Host->NumRegisteredObjects(),
			FirstRegisteredObjectsBeforeRejected);
		TestEqual(
			*FString::Printf(TEXT("%s keeps Second registered object count"), Label),
			Second.Host->NumRegisteredObjects(),
			SecondRegisteredObjectsBeforeRejected);
		TestEqual(
			*FString::Printf(TEXT("%s keeps First lifecycle claim count"), Label),
			First.Host->GetGameplayLifecycleClaimCount(),
			FirstLifecycleClaimsBeforeRejected);
		TestEqual(
			*FString::Printf(TEXT("%s keeps Second lifecycle claim count"), Label),
			Second.Host->GetGameplayLifecycleClaimCount(),
			SecondLifecycleClaimsBeforeRejected);
		FString ContractAfter;
		FString ContractError;
		TestTrue(
			*FString::Printf(TEXT("%s keeps the activation contract"), Label),
			Cast<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass)
			->BuildBlueprintOverrideActivationContract(ContractAfter, ContractError) &&
			ContractAfter.Equals(ActiveContractBefore, ESearchCase::CaseSensitive));
	};

	Error.Reset();
	SyncResult = FFinareBlueprintClassSyncResult{};
	TestFalse(TEXT("Live Sync rejects a valid same-signature retarget while active"),
	          FFinareBlueprintClassMaterializer::SynchronizeJson(
		          *ScriptAsset,
		          *Blueprint,
		          RetargetedRouteDeclaration,
		          false,
		          SyncResult,
		          Error));
	TestFalse(TEXT("Retarget rejection reports no change"), SyncResult.bChanged);
	TestTrue(TEXT("Retarget diagnostic names both routes and recovery"),
	         Error.Contains(TEXT("EvaluateInheritedAlternate")) &&
	         Error.Contains(TEXT("EvaluateInherited")) &&
	         Error.Contains(TEXT("Stop the affected instance")));

	Error.Reset();
	SyncResult = FFinareBlueprintClassSyncResult{};
	TestFalse(TEXT("Live Sync rejects override removal in validate-only mode"),
	          FFinareBlueprintClassMaterializer::SynchronizeJson(
		          *ScriptAsset,
		          *Blueprint,
		          RemovedRouteDeclaration,
		          true,
		          SyncResult,
		          Error));
	TestFalse(TEXT("Removal rejection reports no change"), SyncResult.bChanged);
	TestTrue(TEXT("Removal diagnostic names the class, route, and recovery"),
	         Error.Contains(Blueprint->GetPathName()) &&
	         Error.Contains(TEXT("EvaluateInherited")) &&
	         Error.Contains(TEXT("Stop the affected instance")) &&
	         Error.Contains(TEXT("synchronize the Script Class")) &&
	         Error.Contains(TEXT("restart")));

	Error.Reset();
	SyncResult = FFinareBlueprintClassSyncResult{};
	TestFalse(TEXT("Live Sync rejects case-only route change while active"),
	          FFinareBlueprintClassMaterializer::SynchronizeJson(
		          *ScriptAsset,
		          *Blueprint,
		          LowercaseRouteDeclaration,
		          false,
		          SyncResult,
		          Error));
	TestFalse(TEXT("Case-only route rejection reports no change"), SyncResult.bChanged);
	TestTrue(TEXT("Case-only route rejection remains actionable"),
	         Error.Contains(TEXT("evaluateInherited")) &&
	         Error.Contains(TEXT("Stop the affected instance")));
	FString OriginalRoute;
	if (const FFinareManagedFunctionBinding* Binding =
		GeneratedClass->ManagedFunctionBindings.Find(TEXT("EvaluateInherited")))
	{
		OriginalRoute = Binding->BlueprintOverrideFunctionName;
		TestTrue(TEXT("Active rejection preserves the raw route spelling"),
		         OriginalRoute.Equals(TEXT("EvaluateInherited"), ESearchCase::CaseSensitive));
	}
	Error.Reset();
	SyncResult = FFinareBlueprintClassSyncResult{};
	TestFalse(TEXT("Live Sync rejects managed-name case changes"),
	          FFinareBlueprintClassMaterializer::SynchronizeJson(
		          *ScriptAsset,
		          *Blueprint,
		          LowercaseManagedNameDeclaration,
		          false,
		          SyncResult,
		          Error));
	TestFalse(TEXT("Managed-name rejection reports no change"), SyncResult.bChanged);
	TestTrue(TEXT("Managed-name case diagnostic is actionable"),
	         Error.Contains(TEXT("EvaluateInherited")) &&
	         Error.Contains(TEXT("managed method identity")));
	VerifyRejectedState(TEXT("Managed-name rejection"));
	Error.Reset();
	SyncResult = FFinareBlueprintClassSyncResult{};
	TestFalse(TEXT("Live Sync rejects an added override route"),
	          FFinareBlueprintClassMaterializer::SynchronizeJson(
		          *ScriptAsset,
		          *Blueprint,
		          AdditionDeclaration,
		          false,
		          SyncResult,
		          Error));
	TestFalse(TEXT("Addition rejection reports no change"), SyncResult.bChanged);
	TestTrue(TEXT("Added-route diagnostic is actionable"),
	         Error.Contains(TEXT("EvaluateInheritedAlternate")) &&
	         Error.Contains(TEXT("was added")));
	Error.Reset();
	SyncResult = FFinareBlueprintClassSyncResult{};
	TestFalse(TEXT("Live Sync rejects a primitive override shape change"),
	          FFinareBlueprintClassMaterializer::SynchronizeJson(
		          *ScriptAsset,
		          *Blueprint,
		          PrimitiveMismatchDeclaration,
		          false,
		          SyncResult,
		          Error));
	TestFalse(TEXT("Primitive mismatch rejection reports no change"), SyncResult.bChanged);
	TestTrue(TEXT("Primitive mismatch is rejected before the live guard"),
	         Error.Contains(TEXT("NativeInput")) || Error.Contains(TEXT("parameter")));
	Error.Reset();
	SyncResult = FFinareBlueprintClassSyncResult{};
	TestFalse(TEXT("Live Sync rejects an override direction change"),
	          FFinareBlueprintClassMaterializer::SynchronizeJson(
		          *ScriptAsset,
		          *Blueprint,
		          DirectionMismatchDeclaration,
		          false,
		          SyncResult,
		          Error));
	TestFalse(TEXT("Direction mismatch rejection reports no change"), SyncResult.bChanged);
	TestTrue(TEXT("Direction mismatch is rejected before the live guard"),
	         Error.Contains(TEXT("NativeAccumulator")) || Error.Contains(TEXT("parameter")));
	VerifyRejectedState(TEXT("Final live rejection"));

	int32 FirstActiveReturn = 0;
	int32 SecondActiveReturn = 0;
	TestTrue(TEXT("First active instance remains callable before rejected Sync"),
	         InvokeEvaluate(First, *Evaluate, 3, 4, FirstActiveReturn));
	TestTrue(TEXT("Second active instance remains callable before rejected Sync"),
	         InvokeEvaluate(Second, *Evaluate, 4, 5, SecondActiveReturn));
	TestEqual(TEXT("First active instance keeps its parent result before rejection"), FirstActiveReturn, 7);
	TestEqual(TEXT("Second active instance keeps its parent result before rejection"), SecondActiveReturn, 9);

	StopLiveWorld(First);
	Error.Reset();
	TestFalse(TEXT("One remaining World still blocks route change"),
	          FFinareBlueprintClassMaterializer::SynchronizeJson(
		          *ScriptAsset,
		          *Blueprint,
		          LowercaseRouteDeclaration,
		          false,
		          SyncResult,
		          Error));
	TestTrue(TEXT("Remaining World route diagnostic is actionable"),
	         Error.Contains(TEXT("evaluateInherited")) &&
	         Error.Contains(TEXT("restart")));
	int32 SecondAfterRejection = 0;
	TestTrue(TEXT("Remaining active instance stays callable after rejected Sync"),
	         InvokeEvaluate(Second, *Evaluate, 4, 5, SecondAfterRejection));
	TestEqual(TEXT("Remaining active instance keeps its parent result"), SecondAfterRejection, 9);
	FString ActiveContractAfter;
	TestTrue(TEXT("Rejected live Sync leaves the activation contract unchanged"),
	         GeneratedClass->BuildBlueprintOverrideActivationContract(
		         ActiveContractAfter,
		         ActiveContractError) &&
	         ActiveContractAfter.Equals(ActiveContractBefore, ESearchCase::CaseSensitive));

	StopLiveWorld(Second);
	// SynchronizeJson may reinstance the generated class while applying the
	// stopped-only change.  Tear down the stopped actors before that compile;
	// a fresh World below will exercise the restored declaration through the
	// new exact generated class rather than retaining a stale component class.
	DestroyLiveWorld(First);
	DestroyLiveWorld(Second);
	Error.Reset();
	TestTrue(TEXT("Stopped exact owners permit route spelling update"),
	         FFinareBlueprintClassMaterializer::SynchronizeJson(
		         *ScriptAsset,
		         *Blueprint,
		         LowercaseRouteDeclaration,
		         false,
		         SyncResult,
		         Error));
	GeneratedClass = Cast<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass);
	const FFinareManagedFunctionBinding* LowercaseBinding =
		GeneratedClass->ManagedFunctionBindings.Find(TEXT("EvaluateInherited"));
	TestTrue(TEXT("Stopped update persists raw route spelling case-sensitively"),
	         LowercaseBinding != nullptr &&
	         LowercaseBinding->BlueprintOverrideFunctionName.Equals(
		         TEXT("evaluateInherited"), ESearchCase::CaseSensitive));

	Error.Reset();
	TestTrue(TEXT("Original route can be restored before restart"),
	         FFinareBlueprintClassMaterializer::SynchronizeJson(
		         *ScriptAsset,
		         *Blueprint,
		         Declaration,
		         false,
		         SyncResult,
		         Error));
	GeneratedClass = Cast<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass);
	Evaluate = GeneratedClass != nullptr
		           ? GeneratedClass->FindFunctionByName(
			           TEXT("EvaluateInherited"), EIncludeSuperFlag::ExcludeSuper)
		           : nullptr;
	const FFinareManagedFunctionBinding* RestoredBinding =
		GeneratedClass->ManagedFunctionBindings.Find(TEXT("EvaluateInherited"));
	TestTrue(TEXT("Restored route uses exact original spelling"),
	         RestoredBinding != nullptr &&
	         RestoredBinding->BlueprintOverrideFunctionName.Equals(
		         TEXT("EvaluateInherited"), ESearchCase::CaseSensitive));

	Error.Reset();
	TestTrue(TEXT("Stopped first World restarts after contract restore"),
	         StartLiveWorld(*GeneratedClass, *ScriptAsset, First, Error));
	int32 ReturnValue = 0;
	TestTrue(TEXT("Restarted managed instance still executes its parent route"),
	         Evaluate != nullptr && InvokeEvaluate(First, *Evaluate, 3, 4, ReturnValue));
	TestEqual(TEXT("Restored route keeps parent result"), ReturnValue, 7);

	// A different generated class may stay active while this exact class is
	// stopped; it must not participate in the live owner barrier.
	const FString OtherPackageName = TEXT("/Game/UnifiedScriptOverrideLiveSyncOther_") + Suffix;
	OtherBlueprint = CreateBlueprint(
		OtherPackageName,
		TEXT("BP_FinareOverrideLiveSyncOther_") + Suffix,
		OtherPackage);
	OtherPackageFilename = FPackageName::LongPackageNameToFilename(
		OtherPackageName,
		FPackageName::GetAssetPackageExtension());
	OtherScriptAsset = OtherBlueprint != nullptr
		                   ? AttachScriptAsset(
			                   *OtherBlueprint,
			                   DemoAsset,
			                   ChunkPath,
			                   AssemblyPath,
			                   DeclarationPath)
		                   : nullptr;
	if (!TestNotNull(TEXT("Different exact-class Blueprint is created"), OtherBlueprint) ||
		!TestNotNull(TEXT("Different exact-class Script Asset is attached"), OtherScriptAsset))
	{
		return false;
	}
	Error.Reset();
	if (!TestTrue(TEXT("Different exact-class declaration materializes"),
	              FFinareBlueprintClassMaterializer::SynchronizeJson(
		              *OtherScriptAsset,
		              *OtherBlueprint,
		              Declaration,
		              false,
		              SyncResult,
		              Error)))
	{
		AddError(Error);
		return false;
	}
	UFinareBlueprintGeneratedClass* OtherGeneratedClass =
		Cast<UFinareBlueprintGeneratedClass>(OtherBlueprint->GeneratedClass);
	if (!TestNotNull(TEXT("Different exact-class generated class exists"), OtherGeneratedClass))
	{
		return false;
	}
	Error.Reset();
	if (!TestTrue(TEXT("Different exact-class World starts"),
	              StartLiveWorld(*OtherGeneratedClass, *OtherScriptAsset, Other, Error)))
	{
		AddError(Error);
		return false;
	}
	StopLiveWorld(First);
	Error.Reset();
	TestTrue(TEXT("Other exact-class active World does not block target Sync"),
	         FFinareBlueprintClassMaterializer::SynchronizeJson(
		         *ScriptAsset,
		         *Blueprint,
		         LowercaseRouteDeclaration,
		         false,
		         SyncResult,
		         Error));
	TestTrue(TEXT("Other exact-class Sync has no live-owner diagnostic"), Error.IsEmpty());
	RefreshLiveWorldAfterBlueprintReinstance(First);
	GeneratedClass = Cast<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass);
	TestNotNull(TEXT("Stopped target Actor survives unrelated-class Sync"), First.Actor);
	TestNotNull(TEXT("Stopped target component is refreshed after reinstance"), First.Component);
	FFinareBlueprintClassMaterializer::SynchronizeJson(
		*ScriptAsset,
		*Blueprint,
		Declaration,
		false,
		SyncResult,
		Error);
	RefreshLiveWorldAfterBlueprintReinstance(First);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareBlueprintComponentLifecycleTest,
	"Finare.Unreal.ScriptClass.BlueprintComponentLifecycle",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareBlueprintComponentLifecycleTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::BlueprintClass::ParentInvocationTests;

	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
	if (!TestNotNull(TEXT("Finare plugin is installed"), Plugin.Get()))
	{
		return false;
	}
	const FString DemoDirectory = FPaths::Combine(
		Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
	const FString DeclarationPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.asset.json"));
	const FString ChunkPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.uschunk"));
	const FString AssemblyPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.dll"));
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
		TEXT("Finare.Unreal.Demo.BlueprintComponentLifecycleDemoScript");
	if (!TestTrue(TEXT("Component lifecycle fixture identity is present"),
	              ReadDemoAsset(Declaration, DemoTypeName, DemoAsset, Error)))
	{
		AddError(Error);
		return false;
	}

	const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
	const FString PackageName = TEXT("/Game/UnifiedScriptComponentLifecycle_") + Suffix;
	const FString AssetName = TEXT("BP_FinareComponentLifecycle_") + Suffix;
	const FString PackageFilename = FPackageName::LongPackageNameToFilename(
		PackageName,
		FPackageName::GetAssetPackageExtension());
	UPackage* Package = nullptr;
	UFinareBlueprint* Blueprint = CreateScriptBlueprint(
		UFinareComponent::StaticClass(),
		PackageName,
		AssetName,
		Package,
		FName(TEXT("FinareBlueprintComponentLifecycle")));
	UFinareAsset* ScriptAsset = Blueprint != nullptr
		                            ? AttachScriptAsset(
			                            *Blueprint,
			                            DemoAsset,
			                            ChunkPath,
			                            AssemblyPath,
			                            DeclarationPath,
			                            EFinareClassKind::ActorComponent)
		                            : nullptr;
	UWorld* World = nullptr;
	FWorldContext* WorldContext = nullptr;
	AActor* Actor = nullptr;
	UFinareComponent* Component = nullptr;
	AActor* ManualStartActor = nullptr;
	UFinareComponent* ManualStartComponent = nullptr;
	AActor* FailedStartActor = nullptr;
	UFinareComponent* FailedStartComponent = nullptr;
	UPackage* ChildWithParentPackage = nullptr;
	UPackage* ChildWithoutParentPackage = nullptr;
	const int32 InitialManagedWorldCount =
		FFinareCoreClrHost::Get().GetManagedWorldCount();

	ON_SCOPE_EXIT
	{
		if (Component != nullptr)
		{
			if (Component->HasBegunPlay())
			{
				Component->EndPlay(EEndPlayReason::Destroyed);
			}
			Component->DestroyComponent();
			Component = nullptr;
		}
		if (Actor != nullptr && !Actor->IsActorBeingDestroyed())
		{
			Actor->Destroy();
			Actor = nullptr;
		}
		if (ManualStartComponent != nullptr)
		{
			if (ManualStartComponent->HasBegunPlay())
			{
				ManualStartComponent->EndPlay(EEndPlayReason::Destroyed);
			}
			ManualStartComponent->DestroyComponent();
			ManualStartComponent = nullptr;
		}
		if (ManualStartActor != nullptr && !ManualStartActor->IsActorBeingDestroyed())
		{
			ManualStartActor->Destroy();
			ManualStartActor = nullptr;
		}
		if (FailedStartComponent != nullptr)
		{
			if (FailedStartComponent->HasBegunPlay())
			{
				FailedStartComponent->EndPlay(EEndPlayReason::Destroyed);
			}
			FailedStartComponent->DestroyComponent();
			FailedStartComponent = nullptr;
		}
		if (FailedStartActor != nullptr && !FailedStartActor->IsActorBeingDestroyed())
		{
			FailedStartActor->Destroy();
			FailedStartActor = nullptr;
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
		if (ChildWithParentPackage != nullptr)
		{
			ChildWithParentPackage->SetDirtyFlag(false);
			TArray<UPackage*> Packages{ChildWithParentPackage};
			UPackageTools::UnloadPackages(Packages);
			ChildWithParentPackage = nullptr;
		}
		if (ChildWithoutParentPackage != nullptr)
		{
			ChildWithoutParentPackage->SetDirtyFlag(false);
			TArray<UPackage*> Packages{ChildWithoutParentPackage};
			UPackageTools::UnloadPackages(Packages);
			ChildWithoutParentPackage = nullptr;
		}
		IFileManager::Get().Delete(*PackageFilename, false, true, true);
		TestEqual(
			TEXT("Component lifecycle cleanup releases managed state"),
			FFinareCoreClrHost::Get().GetManagedWorldCount(),
			InitialManagedWorldCount);
	};

	if (!TestNotNull(TEXT("Component lifecycle Blueprint is created"), Blueprint) ||
		!TestNotNull(TEXT("Component lifecycle generated class is created"),
		             Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr) ||
		!TestNotNull(TEXT("Component lifecycle Script Asset is attached"), ScriptAsset))
	{
		return false;
	}

	FFinareBlueprintClassSyncResult SyncResult;
	if (!TestTrue(TEXT("Materializes component lifecycle overrides"),
	              FFinareBlueprintClassMaterializer::SynchronizeJson(
		              *ScriptAsset, *Blueprint, Declaration, false, SyncResult, Error)))
	{
		AddError(Error);
		return false;
	}
	UFinareBlueprintGeneratedClass* GeneratedClass =
		Cast<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass);
	if (!TestNotNull(TEXT("Component lifecycle generated class is typed"), GeneratedClass))
	{
		return false;
	}
	TestTrue(TEXT("ReceiveBeginPlay is bound to managed dispatch"),
	         GeneratedClass->IsManagedFunctionBound(TEXT("ReceiveBeginPlay")));
	TestTrue(TEXT("ReceiveEndPlay is bound to managed dispatch"),
	         GeneratedClass->IsManagedFunctionBound(TEXT("ReceiveEndPlay")));

	World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("Component lifecycle World is created"), World))
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
	UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
	Component = Actor != nullptr
		            ? NewObject<UFinareComponent>(
			            Actor,
			            GeneratedClass,
			            FName(TEXT("ComponentLifecycle")))
		            : nullptr;
	if (Actor != nullptr && Component != nullptr)
	{
		Actor->AddInstanceComponent(Component);
		Component->ScriptAsset = nullptr;
		Component->ScriptAssetPath = ScriptAsset->GetPathName();
		Component->bStartAutomatically = true;
		Component->bTickAutomatically = false;
		Component->LifecycleOwnershipKey = FName(TEXT("ComponentLifecycleTest"));
		Component->RegisterComponent();
	}
	if (!TestNotNull(TEXT("Component lifecycle Actor spawns"), Actor) ||
		!TestNotNull(TEXT("Component lifecycle World host exists"), Host) ||
		!TestNotNull(TEXT("Generated component instance exists"), Component))
	{
		return false;
	}

	Actor->DispatchBeginPlay();
	TestTrue(TEXT("Actor DispatchBeginPlay reaches the component"), Actor->HasActorBegunPlay());
	TestTrue(TEXT("Component DispatchBeginPlay reaches the component"), Component->HasBegunPlay());
	TestTrue(TEXT("Component lifecycle script starts automatically"), Component->bScriptRunning);
	TestEqual(
		TEXT("BeginPlay starts managed code before native ReceiveBeginPlay"),
		Component->ManagedState,
		FString(TEXT("Initialized|ReceiveBegin")));
	TestTrue(TEXT("Component lifecycle startup has no error"), Component->LastScriptError.IsEmpty());

	// EndPlay must continue to use the published ActiveScriptAsset even after
	// the configured path is cleared, so the inherited event runs before the
	// managed generation is stopped.
	Component->ScriptAssetPath.Reset();
	Component->ScriptAsset = nullptr;
	Actor->RouteEndPlay(EEndPlayReason::Quit);
	TestFalse(TEXT("Component lifecycle script stops after native EndPlay"), Component->bScriptRunning);
	TestFalse(TEXT("Component lifecycle component has ended play"), Component->HasBegunPlay());
	TestEqual(
		TEXT("EndPlay dispatches before managed destruction"),
		Component->ManagedState,
		FString(TEXT("Initialized|ReceiveBegin|ReceiveEnd:4|Stopped")));
	TestTrue(TEXT("Component lifecycle EndPlay has no error"), Component->LastScriptError.IsEmpty());

	// A manually started component still receives the native lifecycle, but
	// it must not synthesize ReceiveBeginPlay when StartScript is called
	// after DispatchBeginPlay. EndPlay remains ordered from the active asset.
	ManualStartActor = World->SpawnActor<AActor>();
	ManualStartComponent = ManualStartActor != nullptr
		                       ? NewObject<UFinareComponent>(
			                       ManualStartActor,
			                       GeneratedClass,
			                       FName(TEXT("ManualStartLifecycleComponent")))
		                       : nullptr;
	if (ManualStartActor != nullptr && ManualStartComponent != nullptr)
	{
		ManualStartActor->AddInstanceComponent(ManualStartComponent);
		ManualStartComponent->ScriptAsset = ScriptAsset;
		ManualStartComponent->bStartAutomatically = false;
		ManualStartComponent->bTickAutomatically = false;
		ManualStartComponent->LifecycleOwnershipKey = FName(TEXT("ManualLifecycleTest"));
		ManualStartComponent->RegisterComponent();
	}
	TestNotNull(TEXT("Manual lifecycle Actor spawns"), ManualStartActor);
	TestNotNull(TEXT("Manual lifecycle component exists"), ManualStartComponent);
	if (ManualStartActor == nullptr || ManualStartComponent == nullptr)
	{
		return false;
	}
	AddExpectedError(
		TEXT("has no exact managed owner"),
		EAutomationExpectedErrorFlags::Contains,
		3);
	AddExpectedError(
		TEXT("The managed script assembly does not exist"),
		EAutomationExpectedErrorFlags::Contains,
		1);
	ManualStartActor->DispatchBeginPlay();
	TestTrue(TEXT("Manual lifecycle Actor begins native play"), ManualStartActor->HasActorBegunPlay());
	TestTrue(TEXT("Manual lifecycle component begins native play"), ManualStartComponent->HasBegunPlay());
	TestFalse(TEXT("Manual lifecycle component does not auto start"), ManualStartComponent->bScriptRunning);
	TestTrue(
		TEXT("Manual lifecycle has no managed state before explicit start"),
		ManualStartComponent->ManagedState.IsEmpty());
	TestTrue(TEXT("Manual lifecycle starts only when requested"), ManualStartComponent->StartScript());
	TestEqual(
		TEXT("Manual lifecycle start does not replay ReceiveBeginPlay"),
		ManualStartComponent->ManagedState,
		FString(TEXT("Initialized")));
	ManualStartActor->RouteEndPlay(EEndPlayReason::Quit);
	TestFalse(TEXT("Manual lifecycle component stops after EndPlay"), ManualStartComponent->bScriptRunning);
	TestFalse(TEXT("Manual lifecycle component has ended play"), ManualStartComponent->HasBegunPlay());
	TestEqual(
		TEXT("Manual lifecycle still dispatches EndPlay before destruction"),
		ManualStartComponent->ManagedState,
		FString(TEXT("Initialized|ReceiveEnd:4|Stopped")));
	ManualStartComponent->DestroyComponent();
	ManualStartComponent = nullptr;
	ManualStartActor->Destroy();
	ManualStartActor = nullptr;

	// A non-empty but missing assembly keeps the exact generated class
	// recognizable, so BeginPlay still runs native lifecycle code even though
	// managed activation fails before publishing an owner.
	UFinareAsset* FailedScriptAsset = NewObject<UFinareAsset>(
		Package,
		*FString::Printf(TEXT("FailedComponentLifecycleScriptAsset_%s"), *Suffix));
	FailedScriptAsset->ScriptClassId = DemoAsset.ClassId;
	FailedScriptAsset->ScriptClassKind = EFinareClassKind::ActorComponent;
	FailedScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
	FailedScriptAsset->CompiledChunkPath.Reset();
	FailedScriptAsset->ScriptAssemblyPath = FPaths::Combine(
		DemoDirectory,
		TEXT("Missing/BlueprintComponentLifecycle.dll"));
	FailedScriptAsset->ScriptTypeName = DemoAsset.TypeName;
	FailedScriptAsset->DeclarationPath = DeclarationPath;
	FailedScriptAsset->DeclarationAssetPath = DemoAsset.AssetPath;
	FailedStartActor = World->SpawnActor<AActor>();
	FailedStartComponent = FailedStartActor != nullptr
		                       ? NewObject<UFinareComponent>(
			                       FailedStartActor,
			                       GeneratedClass,
			                       FName(TEXT("FailedStartLifecycleComponent")))
		                       : nullptr;
	if (FailedStartActor != nullptr && FailedStartComponent != nullptr)
	{
		FailedStartActor->AddInstanceComponent(FailedStartComponent);
		FailedStartComponent->ScriptAsset = FailedScriptAsset;
		FailedStartComponent->bStartAutomatically = true;
		FailedStartComponent->bTickAutomatically = false;
		FailedStartComponent->LifecycleOwnershipKey = FName(TEXT("FailedLifecycleTest"));
		FailedStartComponent->RegisterComponent();
	}
	TestNotNull(TEXT("Failed lifecycle Actor spawns"), FailedStartActor);
	TestNotNull(TEXT("Failed lifecycle component exists"), FailedStartComponent);
	if (FailedStartActor == nullptr || FailedStartComponent == nullptr)
	{
		return false;
	}
	const int32 ManagedScriptsBeforeFailedStart = Host->GetManagedScriptCount();
	const int32 RegisteredObjectsBeforeFailedStart = Host->NumRegisteredObjects();
	const int32 LifecycleClaimsBeforeFailedStart = Host->GetGameplayLifecycleClaimCount();
	FailedStartActor->DispatchBeginPlay();
	TestTrue(TEXT("Failed lifecycle Actor still begins native play"), FailedStartActor->HasActorBegunPlay());
	TestTrue(TEXT("Failed lifecycle component still begins native play"), FailedStartComponent->HasBegunPlay());
	TestFalse(TEXT("Failed lifecycle does not publish a managed generation"), FailedStartComponent->bScriptRunning);
	TestTrue(TEXT("Failed lifecycle records the missing assembly"),
	         FailedStartComponent->LastScriptError.Contains(TEXT("BlueprintComponentLifecycle.dll")));
	TestEqual(
		TEXT("Failed lifecycle does not register a managed script"),
		Host->GetManagedScriptCount(),
		ManagedScriptsBeforeFailedStart);
	TestEqual(
		TEXT("Failed lifecycle does not register an object"),
		Host->NumRegisteredObjects(),
		RegisteredObjectsBeforeFailedStart);
	TestEqual(
		TEXT("Failed lifecycle does not claim gameplay ownership"),
		Host->GetGameplayLifecycleClaimCount(),
		LifecycleClaimsBeforeFailedStart);
	FailedStartActor->RouteEndPlay(EEndPlayReason::Quit);
	TestFalse(TEXT("Failed lifecycle component has ended native play"), FailedStartComponent->HasBegunPlay());
	TestFalse(TEXT("Failed lifecycle remains stopped after EndPlay"), FailedStartComponent->bScriptRunning);
	TestEqual(
		TEXT("Failed lifecycle leaves managed registration unchanged after EndPlay"),
		Host->NumRegisteredObjects(),
		RegisteredObjectsBeforeFailedStart);
	FailedStartComponent->DestroyComponent();
	FailedStartComponent = nullptr;
	FailedStartActor->Destroy();
	FailedStartActor = nullptr;

	// A regular Blueprint child owns the event body. With an explicit Call
	// Parent node the managed inherited event still runs; without it, the
	// child intentionally consumes the event while the managed generation
	// still starts and stops around the native lifecycle.
	if (Component != nullptr)
	{
		Component->DestroyComponent();
		Component = nullptr;
	}
	if (Actor != nullptr && !Actor->IsActorBeingDestroyed())
	{
		Actor->Destroy();
		Actor = nullptr;
	}

	auto RunChildLifecycle = [this, &World, &ScriptAsset, &GeneratedClass](
		UBlueprint& ChildBlueprint,
		bool bCallParent,
		const FString& ExpectedState) -> bool
	{
		FString ChildError;
		TestTrue(
			*FString::Printf(
				TEXT("Child lifecycle %s event graph is configured"),
				bCallParent ? TEXT("with parent") : TEXT("without parent")),
			AddLifecycleChildEvent(
				ChildBlueprint,
				*GeneratedClass,
				FName(TEXT("ReceiveBeginPlay")),
				bCallParent,
				ChildError));
		TestTrue(
			*FString::Printf(
				TEXT("Child lifecycle %s end graph is configured"),
				bCallParent ? TEXT("with parent") : TEXT("without parent")),
			AddLifecycleChildEvent(
				ChildBlueprint,
				*GeneratedClass,
				FName(TEXT("ReceiveEndPlay")),
				bCallParent,
				ChildError));
		if (!ChildError.IsEmpty())
		{
			AddError(ChildError);
			return false;
		}
		FKismetEditorUtilities::CompileBlueprint(&ChildBlueprint);
		if (!TestEqual(
			TEXT("Lifecycle child Blueprint compiles"),
			ChildBlueprint.Status,
			BS_UpToDate))
		{
			return false;
		}

		AActor* ChildActor = World->SpawnActor<AActor>();
		UFinareComponent* ChildComponent = ChildActor != nullptr
			                                   ? NewObject<UFinareComponent>(
				                                   ChildActor,
				                                   ChildBlueprint.GeneratedClass,
				                                   FName(TEXT("ChildLifecycleComponent")))
			                                   : nullptr;
		if (ChildActor != nullptr && ChildComponent != nullptr)
		{
			ChildActor->AddInstanceComponent(ChildComponent);
			ChildComponent->ScriptAsset = ScriptAsset;
			ChildComponent->bStartAutomatically = true;
			ChildComponent->bTickAutomatically = false;
			ChildComponent->LifecycleOwnershipKey = FName(
				bCallParent ? TEXT("LifecycleChildWithParent") : TEXT("LifecycleChildWithoutParent"));
			ChildComponent->RegisterComponent();
		}
		TestNotNull(TEXT("Lifecycle child Actor spawns"), ChildActor);
		TestNotNull(TEXT("Lifecycle child component exists"), ChildComponent);
		if (ChildActor == nullptr || ChildComponent == nullptr)
		{
			return false;
		}
		ChildActor->DispatchBeginPlay();
		TestTrue(TEXT("Lifecycle child Actor begins play"), ChildActor->HasActorBegunPlay());
		TestTrue(TEXT("Lifecycle child component begins play"), ChildComponent->HasBegunPlay());
		TestTrue(TEXT("Lifecycle child starts its managed generation"), ChildComponent->bScriptRunning);
		TestEqual(
			TEXT("Lifecycle child BeginPlay parent behavior"),
			ChildComponent->ManagedState,
			bCallParent ? FString(TEXT("ChildBegin|ReceiveBegin")) : FString(TEXT("ChildBegin")));
		ChildActor->RouteEndPlay(EEndPlayReason::Quit);
		TestFalse(TEXT("Lifecycle child stops after EndPlay"), ChildComponent->bScriptRunning);
		TestEqual(TEXT("Lifecycle child event parent behavior"), ChildComponent->ManagedState, ExpectedState);
		if (ChildComponent->HasBegunPlay())
		{
			ChildComponent->EndPlay(EEndPlayReason::Destroyed);
		}
		ChildComponent->DestroyComponent();
		ChildActor->Destroy();
		return true;
	};

	UBlueprint* ChildWithParent = CreateNormalBlueprint(
		GeneratedClass,
		TEXT("/Engine/Transient/FinareLifecycleChildWithParent_") + Suffix,
		TEXT("BP_FinareLifecycleChildWithParent_") + Suffix,
		ChildWithParentPackage);
	UBlueprint* ChildWithoutParent = CreateNormalBlueprint(
		GeneratedClass,
		TEXT("/Engine/Transient/FinareLifecycleChildWithoutParent_") + Suffix,
		TEXT("BP_FinareLifecycleChildWithoutParent_") + Suffix,
		ChildWithoutParentPackage);
	if (!TestNotNull(TEXT("Lifecycle child with parent is created"), ChildWithParent) ||
		!TestNotNull(TEXT("Lifecycle child without parent is created"), ChildWithoutParent))
	{
		return false;
	}
	if (!RunChildLifecycle(
			*ChildWithParent,
			true,
			TEXT("ChildEnd|ReceiveEnd:4|Stopped")) ||
		!RunChildLifecycle(
			*ChildWithoutParent,
			false,
			TEXT("ChildEnd|Stopped")))
	{
		return false;
	}
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareBlueprintEventExtensionCookFixtureTest,
	"Finare.Unreal.ScriptClass.BlueprintEventExtensionCookFixture",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareBlueprintEventExtensionCookFixtureTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::BlueprintClass::ParentInvocationTests;

	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
	if (!TestNotNull(TEXT("Finare plugin is installed"), Plugin.Get()))
	{
		return false;
	}
	const FString DemoDirectory = FPaths::Combine(
		Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"));
	const FString DeclarationPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.asset.json"));
	const FString ChunkPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.uschunk"));
	const FString AssemblyPath = FPaths::Combine(
		DemoDirectory, TEXT("Finare.Unreal.Demo.dll"));
	FString Declaration;
	FString Error;
	if (!TestTrue(TEXT("Cook fixture Demo declaration is staged"),
	              FFileHelper::LoadFileToString(Declaration, *DeclarationPath)) ||
		!TestTrue(TEXT("Cook fixture Demo chunk is staged"),
		          IFileManager::Get().FileExists(*ChunkPath)) ||
		!TestTrue(TEXT("Cook fixture Demo assembly is staged"),
		          IFileManager::Get().FileExists(*AssemblyPath)))
	{
		return false;
	}

	FDemoAsset ParentAsset;
	FDemoAsset LifecycleAsset;
	const FString ParentTypeName =
		TEXT("Finare.Unreal.Demo.BlueprintParentInvocationDemoScript");
	const FString LifecycleTypeName =
		TEXT("Finare.Unreal.Demo.BlueprintComponentLifecycleDemoScript");
	if (!TestTrue(TEXT("Cook fixture parent asset identity is present"),
	              ReadDemoAsset(Declaration, ParentTypeName, ParentAsset, Error)) ||
		!TestTrue(TEXT("Cook fixture lifecycle asset identity is present"),
		          ReadDemoAsset(Declaration, LifecycleTypeName, LifecycleAsset, Error)))
	{
		AddError(Error);
		return false;
	}

	FString OutputRoot;
	const bool bRetainFixture = ReadCommandLineValue(
		TEXT("FinareEventExtensionFixtureOutput="),
		OutputRoot);
	if (bRetainFixture)
	{
		OutputRoot = FPaths::ConvertRelativePathToFull(OutputRoot);
		FPaths::NormalizeFilename(OutputRoot);
		IFileManager::Get().MakeDirectory(*OutputRoot, true);
	}

	const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
	const FString PackageDirectory =
		TEXT("/Game/UnifiedScriptEventExtensionFixture_") + Suffix;
	const FString FixtureChunkPath =
		TEXT("{ProjectDir}/Plugins/Finare/Binaries/Managed/Demo/Finare.Unreal.Demo.uschunk");
	const FString FixtureAssemblyPath =
		TEXT("{ProjectDir}/Plugins/Finare/Binaries/Managed/Demo/Finare.Unreal.Demo.dll");
	const FString FixtureDeclarationPath =
		TEXT("{ProjectDir}/Plugins/Finare/Binaries/Managed/Demo/Finare.Unreal.Demo.asset.json");

	TArray<UPackage*> Packages;
	TArray<FString> PackageNames;
	TArray<FString> PackageFilenames;
	TArray<UObject*> PackageAssets;
	TArray<FString> ClassPaths;
	bool bFixtureSucceeded = false;
	FString ReceiptPath;
	const TArray<FString> FixtureLeafNames{
		TEXT("NativeManagedActor"),
		TEXT("OrdinaryParentActor"),
		TEXT("IntermediateManagedActor"),
		TEXT("ComponentBase"),
		TEXT("ComponentChildWithParent"),
		TEXT("ComponentChildWithoutParent")
	};
	for (const FString& LeafName : FixtureLeafNames)
	{
		const FString PackageName = PackageDirectory + TEXT("/") + LeafName;
		PackageNames.Add(PackageName);
		PackageFilenames.Add(FPackageName::LongPackageNameToFilename(
			PackageName,
			FPackageName::GetAssetPackageExtension()));
		PackageAssets.Add(nullptr);
	}
	ON_SCOPE_EXIT
	{
		for (const FString& PackageName : PackageNames)
		{
			if (UPackage* Package = FindPackage(nullptr, *PackageName))
			{
				Packages.AddUnique(Package);
			}
		}
		UnloadFixturePackages(Packages);
		if (!(bFixtureSucceeded && bRetainFixture))
		{
			for (const FString& PackageFilename : PackageFilenames)
			{
				IFileManager::Get().Delete(*PackageFilename, false, true, true);
			}
			const FString FixtureRoot = PackageFilenames.Num() > 0
				                            ? FPaths::GetPath(PackageFilenames[0])
				                            : FString();
			if (IFileManager::Get().DirectoryExists(*FixtureRoot))
			{
				IFileManager::Get().DeleteDirectory(*FixtureRoot, false, false);
			}
			if (!ReceiptPath.IsEmpty())
			{
				IFileManager::Get().Delete(*ReceiptPath, false, true, true);
			}
		}
	};

	auto AddPackage = [&](const FString& LeafName, const FString& AssetName, UBlueprint* Blueprint)
	{
		const FString PackageName = PackageDirectory + TEXT("/") + LeafName;
		UPackage* Package = Blueprint != nullptr ? Blueprint->GetOutermost() : nullptr;
		const int32 PackageIndex = PackageNames.IndexOfByKey(PackageName);
		check(PackageIndex != INDEX_NONE);
		PackageAssets[PackageIndex] = Blueprint;
		Packages.AddUnique(Package);
	};

	auto ConfigureActorScriptAsset = [&](UFinareBlueprint& Blueprint,
	                                     UFinareAsset& ScriptAsset)
	{
		ScriptAsset.CompiledChunkPath = FixtureChunkPath;
		ScriptAsset.ScriptAssemblyPath = FixtureAssemblyPath;
		ScriptAsset.DeclarationPath = FixtureDeclarationPath;
		Blueprint.ScriptAsset = &ScriptAsset;
		if (AFinareActor* CDO = Blueprint.GeneratedClass != nullptr
			                        ? Cast<AFinareActor>(Blueprint.GeneratedClass->GetDefaultObject())
			                        : nullptr)
		{
			if (CDO->ScriptComponent != nullptr)
			{
				CDO->ScriptComponent->ScriptAsset = &ScriptAsset;
				CDO->ScriptComponent->bStartAutomatically = true;
				CDO->ScriptComponent->bTickAutomatically = false;
			}
		}
	};

	auto ConfigureComponentScriptAsset = [&](UFinareBlueprint& Blueprint,
	                                         UFinareAsset& ScriptAsset)
	{
		ScriptAsset.CompiledChunkPath = FixtureChunkPath;
		ScriptAsset.ScriptAssemblyPath = FixtureAssemblyPath;
		ScriptAsset.DeclarationPath = FixtureDeclarationPath;
		Blueprint.ScriptAsset = &ScriptAsset;
		if (UFinareComponent* CDO = Blueprint.GeneratedClass != nullptr
			                            ? Cast<UFinareComponent>(Blueprint.GeneratedClass->GetDefaultObject())
			                            : nullptr)
		{
			CDO->ScriptAsset = &ScriptAsset;
			CDO->bStartAutomatically = true;
			CDO->bTickAutomatically = false;
		}
	};

	const FString NativePackageName = PackageDirectory + TEXT("/NativeManagedActor");
	UPackage* NativePackage = nullptr;
	UFinareBlueprint* NativeBlueprint = CreateBlueprint(
		NativePackageName,
		TEXT("BP_NativeManagedActor_") + Suffix,
		NativePackage);
	UFinareAsset* NativeScriptAsset = NativeBlueprint != nullptr
		                                  ? AttachScriptAsset(
			                                  *NativeBlueprint,
			                                  ParentAsset,
			                                  ChunkPath,
			                                  AssemblyPath,
			                                  DeclarationPath,
			                                  EFinareClassKind::Actor)
		                                  : nullptr;
	if (!TestNotNull(TEXT("Native managed actor Blueprint is created"), NativeBlueprint) ||
		!TestNotNull(TEXT("Native managed actor ScriptAsset is created"), NativeScriptAsset))
	{
		return false;
	}
	FFinareBlueprintClassSyncResult SyncResult;
	if (!TestTrue(TEXT("Native managed actor overrides materialize"),
	              FFinareBlueprintClassMaterializer::SynchronizeJson(
		              *NativeScriptAsset, *NativeBlueprint, Declaration, false, SyncResult, Error)))
	{
		AddError(Error);
		return false;
	}
	ConfigureActorScriptAsset(*NativeBlueprint, *NativeScriptAsset);
	NativeScriptAsset->SetFlags(RF_Public | RF_Standalone);
	AddPackage(TEXT("NativeManagedActor"), TEXT("BP_NativeManagedActor_") + Suffix, NativeBlueprint);
	ClassPaths.Add(NativeBlueprint->GeneratedClass->GetPathName());

	const FString OrdinaryPackageName = PackageDirectory + TEXT("/OrdinaryParentActor");
	UPackage* OrdinaryPackage = nullptr;
	UBlueprint* OrdinaryBlueprint = CreateNormalBlueprint(
		AFinareEventExtensionFixture::StaticClass(),
		OrdinaryPackageName,
		TEXT("BP_OrdinaryParentActor_") + Suffix,
		OrdinaryPackage);
	if (!TestNotNull(TEXT("Ordinary parent Blueprint is created"), OrdinaryBlueprint) ||
		!TestTrue(TEXT("Ordinary parent Evaluate body is created"),
		          OrdinaryBlueprint != nullptr &&
		          CreateIntermediateEvaluateBody(*OrdinaryBlueprint, Error)) ||
		!TestTrue(TEXT("Ordinary parent Receive body is created"),
		          OrdinaryBlueprint != nullptr &&
		          AddObservedReceiveInheritedBody(*OrdinaryBlueprint, Error)))
	{
		AddError(Error);
		return false;
	}
	FKismetEditorUtilities::CompileBlueprint(OrdinaryBlueprint);
	if (!TestEqual(TEXT("Ordinary parent Blueprint compiles"), OrdinaryBlueprint->Status, BS_UpToDate))
	{
		return false;
	}
	AddPackage(TEXT("OrdinaryParentActor"), TEXT("BP_OrdinaryParentActor_") + Suffix, OrdinaryBlueprint);
	ClassPaths.Add(OrdinaryBlueprint->GeneratedClass->GetPathName());

	const FString IntermediatePackageName = PackageDirectory + TEXT("/IntermediateManagedActor");
	UPackage* IntermediatePackage = nullptr;
	UFinareBlueprint* IntermediateBlueprint = CreateScriptBlueprint(
		OrdinaryBlueprint->GeneratedClass,
		IntermediatePackageName,
		TEXT("BP_IntermediateManagedActor_") + Suffix,
		IntermediatePackage,
		FName(TEXT("FinareBlueprintEventExtensionCookFixture")));
	UFinareAsset* IntermediateScriptAsset = IntermediateBlueprint != nullptr
		                                        ? AttachScriptAsset(
			                                        *IntermediateBlueprint,
			                                        ParentAsset,
			                                        ChunkPath,
			                                        AssemblyPath,
			                                        DeclarationPath,
			                                        EFinareClassKind::Actor)
		                                        : nullptr;
	if (!TestNotNull(TEXT("Intermediate managed actor Blueprint is created"), IntermediateBlueprint) ||
		!TestNotNull(TEXT("Intermediate managed actor ScriptAsset is created"), IntermediateScriptAsset) ||
		!TestTrue(TEXT("Intermediate managed actor overrides materialize"),
		          IntermediateBlueprint != nullptr && IntermediateScriptAsset != nullptr &&
		          FFinareBlueprintClassMaterializer::SynchronizeJson(
			          *IntermediateScriptAsset, *IntermediateBlueprint, Declaration, false, SyncResult, Error)))
	{
		AddError(Error);
		return false;
	}
	ConfigureActorScriptAsset(*IntermediateBlueprint, *IntermediateScriptAsset);
	IntermediateScriptAsset->SetFlags(RF_Public | RF_Standalone);
	AddPackage(TEXT("IntermediateManagedActor"), TEXT("BP_IntermediateManagedActor_") + Suffix, IntermediateBlueprint);
	ClassPaths.Add(IntermediateBlueprint->GeneratedClass->GetPathName());

	const FString ComponentPackageName = PackageDirectory + TEXT("/ComponentBase");
	UPackage* ComponentPackage = nullptr;
	UFinareBlueprint* ComponentBlueprint = CreateScriptBlueprint(
		UFinareComponent::StaticClass(),
		ComponentPackageName,
		TEXT("BP_ComponentBase_") + Suffix,
		ComponentPackage,
		FName(TEXT("FinareBlueprintEventExtensionCookFixture")));
	UFinareAsset* ComponentScriptAsset = ComponentBlueprint != nullptr
		                                     ? AttachScriptAsset(
			                                     *ComponentBlueprint,
			                                     LifecycleAsset,
			                                     ChunkPath,
			                                     AssemblyPath,
			                                     DeclarationPath,
			                                     EFinareClassKind::ActorComponent)
		                                     : nullptr;
	if (!TestNotNull(TEXT("Component base Blueprint is created"), ComponentBlueprint) ||
		!TestNotNull(TEXT("Component base ScriptAsset is created"), ComponentScriptAsset) ||
		!TestTrue(TEXT("Component base lifecycle overrides materialize"),
		          ComponentBlueprint != nullptr && ComponentScriptAsset != nullptr &&
		          FFinareBlueprintClassMaterializer::SynchronizeJson(
			          *ComponentScriptAsset, *ComponentBlueprint, Declaration, false, SyncResult, Error)))
	{
		AddError(Error);
		return false;
	}
	ConfigureComponentScriptAsset(*ComponentBlueprint, *ComponentScriptAsset);
	ComponentScriptAsset->SetFlags(RF_Public | RF_Standalone);
	UClass* ComponentGeneratedClass = ComponentBlueprint->GeneratedClass;
	if (!TestNotNull(TEXT("Component base generated class is available"), ComponentGeneratedClass))
	{
		return false;
	}
	AddPackage(TEXT("ComponentBase"), TEXT("BP_ComponentBase_") + Suffix, ComponentBlueprint);
	ClassPaths.Add(ComponentGeneratedClass->GetPathName());

	const FString ChildWithParentPackageName = PackageDirectory + TEXT("/ComponentChildWithParent");
	UPackage* ChildParentPackage = nullptr;
	UBlueprint* ChildWithParent = CreateNormalBlueprint(
		ComponentGeneratedClass,
		ChildWithParentPackageName,
		TEXT("BP_ComponentChildWithParent_") + Suffix,
		ChildParentPackage);
	if (!TestNotNull(TEXT("Component child with parent Blueprint is created"), ChildWithParent))
	{
		AddError(TEXT("Could not create the component child with parent package."));
		return false;
	}
	Error.Reset();
	if (!AddLifecycleChildEvent(
			*ChildWithParent,
			*ComponentGeneratedClass,
			FName(TEXT("ReceiveBeginPlay")),
			true,
			Error) ||
		!AddLifecycleChildEvent(
			*ChildWithParent,
			*ComponentGeneratedClass,
			FName(TEXT("ReceiveEndPlay")),
			true,
			Error))
	{
		AddError(Error);
		return false;
	}
	FKismetEditorUtilities::CompileBlueprint(ChildWithParent);
	if (!TestEqual(TEXT("Component child with parent compiles"), ChildWithParent->Status, BS_UpToDate))
	{
		return false;
	}
	AddPackage(TEXT("ComponentChildWithParent"), TEXT("BP_ComponentChildWithParent_") + Suffix, ChildWithParent);
	ClassPaths.Add(ChildWithParent->GeneratedClass->GetPathName());

	const FString ChildWithoutParentPackageName = PackageDirectory + TEXT("/ComponentChildWithoutParent");
	UPackage* ChildWithoutParentPackage = nullptr;
	UBlueprint* ChildWithoutParent = CreateNormalBlueprint(
		ComponentGeneratedClass,
		ChildWithoutParentPackageName,
		TEXT("BP_ComponentChildWithoutParent_") + Suffix,
		ChildWithoutParentPackage);
	if (!TestNotNull(TEXT("Component child without parent Blueprint is created"), ChildWithoutParent))
	{
		return false;
	}
	Error.Reset();
	if (!AddLifecycleChildEvent(
			*ChildWithoutParent,
			*ComponentGeneratedClass,
			FName(TEXT("ReceiveBeginPlay")),
			false,
			Error) ||
		!AddLifecycleChildEvent(
			*ChildWithoutParent,
			*ComponentGeneratedClass,
			FName(TEXT("ReceiveEndPlay")),
			false,
			Error))
	{
		AddError(Error);
		return false;
	}
	FKismetEditorUtilities::CompileBlueprint(ChildWithoutParent);
	if (!TestEqual(TEXT("Component child without parent compiles"), ChildWithoutParent->Status, BS_UpToDate))
	{
		return false;
	}
	AddPackage(TEXT("ComponentChildWithoutParent"), TEXT("BP_ComponentChildWithoutParent_") + Suffix,
	           ChildWithoutParent);
	ClassPaths.Add(ChildWithoutParent->GeneratedClass->GetPathName());

	if (!TestEqual(TEXT("Cook fixture has six packages"), Packages.Num(), 6) ||
		!TestEqual(TEXT("Cook fixture has six class paths"), ClassPaths.Num(), 6))
	{
		return false;
	}
	for (int32 Index = 0; Index < PackageNames.Num(); ++Index)
	{
		UPackage* Package = FindPackage(nullptr, *PackageNames[Index]);
		if (!TestTrue(
			*FString::Printf(TEXT("Cook fixture package %d saves"), Index),
			SaveFixturePackage(
				Package,
				PackageAssets[Index],
				PackageFilenames[Index],
				Error)))
		{
			AddError(Error);
			return false;
		}
	}
	if (!TestTrue(TEXT("Cook fixture unloads saved packages"), UnloadFixturePackages(Packages)) ||
		!TestTrue(TEXT("Cook fixture reloads saved classes"),
		          ValidateReloadedFixtureClasses(ClassPaths, Error)))
	{
		AddError(Error);
		return false;
	}
	UClass* ReloadedOrdinaryClass = LoadObject<UClass>(nullptr, *ClassPaths[1]);
	UFunction* ReloadedEvaluate = ReloadedOrdinaryClass != nullptr
		                              ? ReloadedOrdinaryClass->FindFunctionByName(
			                              TEXT("EvaluateInherited"),
			                              EIncludeSuperFlag::ExcludeSuper)
		                              : nullptr;
	UFunction* ReloadedReceive = ReloadedOrdinaryClass != nullptr
		                             ? ReloadedOrdinaryClass->FindFunctionByName(
			                             TEXT("ReceiveInherited"),
			                             EIncludeSuperFlag::ExcludeSuper)
		                             : nullptr;
	if (!TestNotNull(TEXT("Reloaded ordinary parent class exists"), ReloadedOrdinaryClass) ||
		!TestNotNull(TEXT("Reloaded ordinary Evaluate function exists"), ReloadedEvaluate) ||
		!TestNotNull(TEXT("Reloaded ordinary Receive function exists"), ReloadedReceive) ||
		!TestTrue(TEXT("Reloaded ordinary Evaluate retains Blueprint VM bytecode"),
		          ReloadedEvaluate != nullptr && ReloadedEvaluate->Script.Num() > 0 &&
		          !ReloadedEvaluate->HasAnyFunctionFlags(FUNC_Native)) ||
		!TestTrue(TEXT("Reloaded ordinary Receive retains Blueprint VM bytecode"),
		          ReloadedReceive != nullptr && ReloadedReceive->Script.Num() > 0 &&
		          !ReloadedReceive->HasAnyFunctionFlags(FUNC_Native)))
	{
		return false;
	}
	const FString ExpectedNativeAssetClassPath = ClassPaths[0];
	const FString ExpectedIntermediateAssetClassPath = ClassPaths[2];
	const FString ExpectedComponentAssetClassPath = ClassPaths[3];
	for (const TPair<FString, FString>& Pair : TArray<TPair<FString, FString>>{
		     {ClassPaths[0], ExpectedNativeAssetClassPath},
		     {ClassPaths[2], ExpectedIntermediateAssetClassPath}
	     })
	{
		UClass* ReloadedClass = LoadObject<UClass>(nullptr, *Pair.Key);
		AFinareActor* CDO = ReloadedClass != nullptr
			                    ? Cast<AFinareActor>(ReloadedClass->GetDefaultObject())
			                    : nullptr;
		if (!TestNotNull(TEXT("Reloaded actor CDO exists"), CDO) ||
			!TestNotNull(TEXT("Reloaded actor CDO retains ScriptAsset"),
			             CDO != nullptr && CDO->ScriptComponent != nullptr
				             ? CDO->ScriptComponent->ScriptAsset.Get()
				             : nullptr) ||
			!TestTrue(TEXT("Reloaded actor CDO ScriptAsset targets its exact class"),
			          CDO != nullptr && CDO->ScriptComponent != nullptr &&
			          CDO->ScriptComponent->ScriptAsset != nullptr &&
			          CDO->ScriptComponent->ScriptAsset->GeneratedClassPath.ToString().Equals(
				          Pair.Value,
				          ESearchCase::CaseSensitive)))
		{
			return false;
		}
	}
	for (const FString& PackageName : PackageNames)
	{
		if (UPackage* ReloadedPackage = FindPackage(nullptr, *PackageName))
		{
			Packages.AddUnique(ReloadedPackage);
		}
	}
	for (const FString& ComponentClassPath : TArray<FString>{
		     ClassPaths[3], ClassPaths[4], ClassPaths[5]
	     })
	{
		UClass* ReloadedClass = LoadObject<UClass>(nullptr, *ComponentClassPath);
		UFinareComponent* CDO = ReloadedClass != nullptr
			                        ? Cast<UFinareComponent>(ReloadedClass->GetDefaultObject())
			                        : nullptr;
		if (!TestNotNull(TEXT("Reloaded component CDO exists"), CDO) ||
			!TestNotNull(TEXT("Reloaded component CDO retains ScriptAsset"),
			             CDO != nullptr ? CDO->ScriptAsset.Get() : nullptr) ||
			!TestTrue(TEXT("Reloaded component CDO ScriptAsset targets the base class"),
			          CDO != nullptr && CDO->ScriptAsset != nullptr &&
			          CDO->ScriptAsset->GeneratedClassPath.ToString().Equals(
				          ExpectedComponentAssetClassPath,
				          ESearchCase::CaseSensitive)))
		{
			return false;
		}
	}

	if (bRetainFixture)
	{
		ReceiptPath = FPaths::Combine(OutputRoot, TEXT("event-extension-receipt.json"));
		TSharedRef<FJsonObject> Receipt = MakeShared<FJsonObject>();
		Receipt->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.event-extension-fixture/v0.1"));
		Receipt->SetStringField(TEXT("packageDirectory"), PackageDirectory);
		TArray<TSharedPtr<FJsonValue>> PackageValues;
		for (int32 Index = 0; Index < PackageNames.Num(); ++Index)
		{
			TSharedRef<FJsonObject> PackageObject = MakeShared<FJsonObject>();
			PackageObject->SetStringField(TEXT("packageName"), PackageNames[Index]);
			PackageObject->SetStringField(
				TEXT("packageFilename"), FPaths::ConvertRelativePathToFull(PackageFilenames[Index]));
			PackageValues.Add(MakeShared<FJsonValueObject>(PackageObject));
		}
		Receipt->SetArrayField(TEXT("packages"), PackageValues);
		Receipt->SetStringField(TEXT("nativeActorClassPath"), ClassPaths[0]);
		Receipt->SetStringField(TEXT("ordinaryParentActorClassPath"), ClassPaths[1]);
		Receipt->SetStringField(TEXT("intermediateActorClassPath"), ClassPaths[2]);
		Receipt->SetStringField(TEXT("componentClassPath"), ClassPaths[3]);
		Receipt->SetStringField(TEXT("componentChildWithParentClassPath"), ClassPaths[4]);
		Receipt->SetStringField(TEXT("componentChildWithoutParentClassPath"), ClassPaths[5]);
		FString ReceiptJson;
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&ReceiptJson);
		if (!FJsonSerializer::Serialize(Receipt, Writer) ||
			!FFileHelper::SaveStringToFile(
				ReceiptJson,
				*ReceiptPath,
				FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
		{
			AddError(TEXT("Could not write the event extension fixture receipt."));
			return false;
		}
	}
	bFixtureSucceeded = true;
	return true;
}

namespace UE::Finare::BlueprintClass::ParentInvocationTests
{
	bool CreateEvaluateWrapperBody(
		UBlueprint& Blueprint,
		UFunction& NativeEvaluate,
		FString& OutError)
	{
		FEdGraphPinType IntType;
		IntType.PinCategory = UEdGraphSchema_K2::PC_Int;
		if (!FBlueprintEditorUtils::AddMemberVariable(
			&Blueprint,
			TEXT("ObservedResult"),
			IntType))
		{
			OutError = TEXT("Could not add the ordinary Blueprint ObservedResult property.");
			return false;
		}

		UEdGraph* Graph = FBlueprintEditorUtils::CreateNewGraph(
			&Blueprint,
			TEXT("CallEvaluate"),
			UEdGraph::StaticClass(),
			UEdGraphSchema_K2::StaticClass());
		if (Graph == nullptr)
		{
			OutError = TEXT("Could not create the ordinary Blueprint CallEvaluate graph.");
			return false;
		}
		FBlueprintEditorUtils::AddFunctionGraph<UFunction>(
			&Blueprint,
			Graph,
			true,
			&NativeEvaluate);

		TArray<UK2Node_FunctionEntry*> Entries;
		Graph->GetNodesOfClass(Entries);
		TArray<UK2Node_FunctionResult*> Results;
		Graph->GetNodesOfClass(Results);
		if (Entries.Num() != 1 || Results.Num() != 1)
		{
			OutError = TEXT("The ordinary Blueprint CallEvaluate graph has no unique entry/result pair.");
			return false;
		}

		UK2Node_CallFunction* Call = NewObject<UK2Node_CallFunction>(Graph);
		Graph->AddNode(Call, true, false);
		Call->CreateNewGuid();
		Call->SetFromFunction(&NativeEvaluate);
		Call->AllocateDefaultPins();

		UK2Node_VariableSet* Observe = NewObject<UK2Node_VariableSet>(Graph);
		Observe->VariableReference.SetSelfMember(TEXT("ObservedResult"));
		Graph->AddNode(Observe, true, false);
		Observe->CreateNewGuid();
		Observe->AllocateDefaultPins();

		const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
		UEdGraphPin* EntryThen = Entries[0]->FindPin(UEdGraphSchema_K2::PN_Then, EGPD_Output);
		UEdGraphPin* CallExecute = Call->FindPin(UEdGraphSchema_K2::PN_Execute, EGPD_Input);
		UEdGraphPin* CallThen = Call->FindPin(UEdGraphSchema_K2::PN_Then, EGPD_Output);
		UEdGraphPin* ObserveExecute = Observe->GetExecPin();
		UEdGraphPin* ObserveThen = Observe->GetThenPin();
		UEdGraphPin* ResultExecute = Results[0]->FindPin(UEdGraphSchema_K2::PN_Execute, EGPD_Input);
		UEdGraphPin* CallReturn = Call->GetReturnValuePin();
		UEdGraphPin* ObserveValue = Observe->FindPin(TEXT("ObservedResult"), EGPD_Input);
		UEdGraphPin* ResultReturn = Results[0]->FindPin(TEXT("ReturnValue"), EGPD_Input);
		UEdGraphPin* EntryInput = Entries[0]->FindPin(TEXT("NativeInput"), EGPD_Output);
		UEdGraphPin* CallInput = Call->FindPin(TEXT("NativeInput"), EGPD_Input);
		UEdGraphPin* EntryAccumulator = Entries[0]->FindPin(TEXT("NativeAccumulator"), EGPD_Output);
		UEdGraphPin* CallAccumulatorInput = Call->FindPin(TEXT("NativeAccumulator"), EGPD_Input);
		UEdGraphPin* CallOutput = Call->FindPin(TEXT("NativeOutput"), EGPD_Output);
		UEdGraphPin* ResultOutput = Results[0]->FindPin(TEXT("NativeOutput"), EGPD_Input);
		const bool bConnected =
			EntryThen != nullptr && CallExecute != nullptr && CallThen != nullptr &&
			ObserveExecute != nullptr && ObserveThen != nullptr && ResultExecute != nullptr &&
			CallReturn != nullptr && ObserveValue != nullptr && ResultReturn != nullptr &&
			EntryInput != nullptr && CallInput != nullptr && EntryAccumulator != nullptr &&
			CallAccumulatorInput != nullptr && CallOutput != nullptr && ResultOutput != nullptr &&
			Schema->TryCreateConnection(EntryThen, CallExecute) &&
			Schema->TryCreateConnection(CallThen, ObserveExecute) &&
			Schema->TryCreateConnection(ObserveThen, ResultExecute) &&
			Schema->TryCreateConnection(EntryInput, CallInput) &&
			Schema->TryCreateConnection(EntryAccumulator, CallAccumulatorInput) &&
			Schema->TryCreateConnection(CallOutput, ResultOutput) &&
			Schema->TryCreateConnection(CallReturn, ObserveValue) &&
			Schema->TryCreateConnection(CallReturn, ResultReturn);
		if (!bConnected)
		{
			OutError = TEXT(
				"The ordinary Blueprint CallEvaluate graph could not connect typed input/ref/out/return pins.");
			return false;
		}
		FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(&Blueprint);
		return true;
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareBlueprintEventOverrideReloadTest,
	"Finare.Unreal.ScriptClass.BlueprintEventOverrideReload",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareBlueprintEventOverrideReloadTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::BlueprintClass::ParentInvocationTests;

	FString FixtureRoot;
	if (!ReadCommandLineValue(TEXT("BlueprintEventReloadFixtureRoot="), FixtureRoot))
	{
		FixtureRoot = FPaths::Combine(
			FPaths::ProjectDir(),
			TEXT("../artifacts/m13-3c-2-event-reload/fixture"));
	}
	FixtureRoot = FPaths::ConvertRelativePathToFull(FixtureRoot);
	const FString DeclarationPath = FPaths::Combine(
		FixtureRoot,
		TEXT("declaration/BlueprintEventReloadFixture.asset.json"));
	const FString ActiveAssembly = FPaths::Combine(
		FixtureRoot,
		TEXT("active/BlueprintEventReloadFixture.dll"));
	const FString V1Assembly = FPaths::Combine(
		FixtureRoot,
		TEXT("v1/BlueprintEventReloadFixture.dll"));
	const FString V2Assembly = FPaths::Combine(
		FixtureRoot,
		TEXT("v2/BlueprintEventReloadFixture.dll"));
	const FString IncompatibleAssembly = FPaths::Combine(
		FixtureRoot,
		TEXT("incompatible/BlueprintEventReloadFixture.dll"));
	FString Declaration;
	FString Error;
	UPackage* ManagedPackage = nullptr;
	UFinareBlueprint* ManagedBlueprint = nullptr;
	UPackage* CallerPackage = nullptr;
	UBlueprint* CallerBlueprint = nullptr;
	FString ManagedPackageName;
	FString CallerPackageName;
	ON_SCOPE_EXIT
	{
		if (CallerBlueprint != nullptr)
		{
			CallerBlueprint = nullptr;
		}
		if (ManagedBlueprint != nullptr)
		{
			ManagedBlueprint = nullptr;
		}
		TArray<UPackage*> Packages;
		if (!CallerPackageName.IsEmpty())
		{
			if (UPackage* Package = FindPackage(nullptr, *CallerPackageName))
			{
				Package->SetDirtyFlag(false);
				Packages.Add(Package);
			}
		}
		if (!ManagedPackageName.IsEmpty())
		{
			if (UPackage* Package = FindPackage(nullptr, *ManagedPackageName))
			{
				Package->SetDirtyFlag(false);
				Packages.Add(Package);
			}
		}
		if (Packages.Num() > 0)
		{
			UPackageTools::UnloadPackages(Packages);
		}
		IFileManager::Get().Copy(*ActiveAssembly, *V1Assembly, true, true);
	};
	if (!TestTrue(TEXT("Blueprint event reload declaration exists"),
	              FFileHelper::LoadFileToString(Declaration, *DeclarationPath)) ||
		!TestTrue(TEXT("Blueprint event reload active v1 assembly exists"),
		          IFileManager::Get().FileExists(*ActiveAssembly)) ||
		!TestTrue(TEXT("Blueprint event reload v2 assembly exists"),
		          IFileManager::Get().FileExists(*V2Assembly)) ||
		!TestTrue(TEXT("Blueprint event reload incompatible assembly exists"),
		          IFileManager::Get().FileExists(*IncompatibleAssembly)))
	{
		return false;
	}
	if (!TestTrue(TEXT("Blueprint event reload resets active assembly to v1"),
	              IFileManager::Get().Copy(*ActiveAssembly, *V1Assembly, true, true) == COPY_OK))
	{
		return false;
	}

	FDemoAsset FixtureAsset;
	const FString FixtureTypeName =
		TEXT("Finare.Tests.Fixtures.BlueprintEventReload.BlueprintEventReloadFixture");
	if (!TestTrue(TEXT("Blueprint event reload fixture identity is present"),
	              ReadDemoAsset(Declaration, FixtureTypeName, FixtureAsset, Error)))
	{
		AddError(Error);
		return false;
	}

	const int32 InitialManagedWorldCount =
		FFinareCoreClrHost::Get().GetManagedWorldCount();
	const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits);
	const FString PackageName = TEXT("/Engine/Transient/FinareBlueprintEventReload_") + Suffix;
	ManagedPackageName = PackageName;
	ManagedBlueprint = CreateBlueprint(
		PackageName,
		TEXT("BP_BlueprintEventReload_") + Suffix,
		ManagedPackage);
	UFinareAsset* ScriptAsset = ManagedBlueprint != nullptr
		                            ? AttachScriptAsset(
			                            *ManagedBlueprint,
			                            FixtureAsset,
			                            FString(),
			                            ActiveAssembly,
			                            DeclarationPath,
			                            EFinareClassKind::Actor)
		                            : nullptr;
	if (!TestNotNull(TEXT("Blueprint event reload managed Blueprint is created"), ManagedBlueprint) ||
		!TestNotNull(TEXT("Blueprint event reload ScriptAsset is attached"), ScriptAsset))
	{
		return false;
	}
	ScriptAsset->CompiledChunkPath.Reset();
	FFinareBlueprintClassSyncResult SyncResult;
	if (!TestTrue(TEXT("Blueprint event reload override materializes from the real declaration"),
	              FFinareBlueprintClassMaterializer::SynchronizeJson(
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
	UFinareBlueprintGeneratedClass* ManagedClass =
		Cast<UFinareBlueprintGeneratedClass>(ManagedBlueprint->GeneratedClass);
	UFunction* NativeEvaluate = AFinareEventExtensionFixture::StaticClass()->FindFunctionByName(
		TEXT("EvaluateInherited"),
		EIncludeSuperFlag::ExcludeSuper);
	if (!TestNotNull(TEXT("Blueprint event reload managed class exists"), ManagedClass) ||
		!TestTrue(TEXT("Blueprint event reload managed function is bound"),
		          ManagedClass != nullptr && ManagedClass->IsManagedFunctionBound(TEXT("EvaluateInherited"))) ||
		!TestNotNull(TEXT("Blueprint event reload native Evaluate signature exists"), NativeEvaluate))
	{
		return false;
	}

	CallerPackageName = TEXT("/Engine/Transient/FinareBlueprintEventReloadCaller_") + Suffix;
	CallerBlueprint = CreateNormalBlueprint(
		ManagedClass,
		CallerPackageName,
		TEXT("BP_BlueprintEventReloadCaller_") + Suffix,
		CallerPackage);
	if (!TestNotNull(TEXT("Blueprint event reload ordinary child Blueprint is created"), CallerBlueprint) ||
		!TestTrue(TEXT("Blueprint event reload ordinary child has CallEvaluate VM body"),
		          CallerBlueprint != nullptr &&
		          CreateEvaluateWrapperBody(*CallerBlueprint, *NativeEvaluate, Error)))
	{
		AddError(Error);
		return false;
	}
	FKismetEditorUtilities::CompileBlueprint(CallerBlueprint);
	UBlueprintGeneratedClass* CallerClass = CallerBlueprint != nullptr
		                                        ? Cast<UBlueprintGeneratedClass>(CallerBlueprint->GeneratedClass)
		                                        : nullptr;
	UFunction* CallEvaluate = CallerClass != nullptr
		                          ? CallerClass->FindFunctionByName(
			                          TEXT("CallEvaluate"), EIncludeSuperFlag::ExcludeSuper)
		                          : nullptr;
	if (!TestEqual(TEXT("Blueprint event reload ordinary child compiles"),
	               CallerBlueprint->Status,
	               BS_UpToDate) ||
		!TestNotNull(TEXT("Blueprint event reload CallEvaluate function exists"), CallEvaluate) ||
		!TestTrue(TEXT("Blueprint event reload CallEvaluate retains Blueprint bytecode"),
		          CallEvaluate != nullptr && CallEvaluate->Script.Num() > 0 &&
		          !CallEvaluate->HasAnyFunctionFlags(FUNC_Native)))
	{
		return false;
	}
	const FIntProperty* ObservedResult = CallerClass != nullptr
		                                     ? FindFProperty<FIntProperty>(CallerClass, TEXT("ObservedResult"))
		                                     : nullptr;
	if (!TestNotNull(TEXT("Blueprint event reload child has ObservedResult"), ObservedResult))
	{
		return false;
	}
	struct FReloadWorld
	{
		UWorld* World = nullptr;
		FWorldContext* Context = nullptr;
		UFinareWorldSubsystem* Host = nullptr;
		AFinareEventExtensionFixture* Actor = nullptr;
		UFinareComponent* Component = nullptr;
		int32 ManagedScriptsBefore = 0;
		int32 RegisteredObjectsBefore = 0;
		int32 LifecycleClaimsBefore = 0;
	} First, Second;
	auto CreateReloadWorld = [&](FReloadWorld& Out, const TCHAR* Label)
	{
		Out.World = UWorld::CreateWorld(EWorldType::Game, false);
		if (!TestNotNull(*FString::Printf(TEXT("%s reload World is created"), Label), Out.World))
		{
			return;
		}
		Out.World->AddToRoot();
		if (GEngine != nullptr)
		{
			Out.Context = &GEngine->CreateNewWorldContext(EWorldType::Game);
			Out.Context->SetCurrentWorld(Out.World);
		}
		Out.World->InitializeActorsForPlay(FURL());
		Out.Host = Out.World->GetSubsystem<UFinareWorldSubsystem>();
		Out.ManagedScriptsBefore = Out.Host != nullptr ? Out.Host->GetManagedScriptCount() : 0;
		Out.RegisteredObjectsBefore = Out.Host != nullptr ? Out.Host->NumRegisteredObjects() : 0;
		Out.LifecycleClaimsBefore = Out.Host != nullptr ? Out.Host->GetGameplayLifecycleClaimCount() : 0;
		Out.Actor = CallerClass != nullptr
			            ? Out.World->SpawnActor<AFinareEventExtensionFixture>(CallerClass)
			            : nullptr;
		Out.Component = Out.Actor != nullptr ? Out.Actor->GetFinareComponent() : nullptr;
		if (Out.Component != nullptr)
		{
			Out.Component->ScriptAsset = ScriptAsset;
			Out.Component->bStartAutomatically = true;
			Out.Component->bTickAutomatically = false;
		}
		TestNotNull(*FString::Printf(TEXT("%s reload World host exists"), Label), Out.Host);
		TestNotNull(*FString::Printf(TEXT("%s ordinary Blueprint child spawns"), Label), Out.Actor);
		TestNotNull(*FString::Printf(TEXT("%s child ScriptComponent exists"), Label), Out.Component);
		if (Out.Actor != nullptr)
		{
			Out.Actor->DispatchBeginPlay();
		}
		TestTrue(*FString::Printf(TEXT("%s child begins native play"), Label),
		         Out.Actor != nullptr && Out.Actor->HasActorBegunPlay());
		TestTrue(*FString::Printf(TEXT("%s child managed script starts"), Label),
		         Out.Component != nullptr && Out.Component->bScriptRunning);
	};
	auto DestroyReloadWorld = [&](FReloadWorld& Fixture, const TCHAR* Label)
	{
		if (Fixture.Actor != nullptr && Fixture.Actor->HasActorBegunPlay())
		{
			Fixture.Actor->RouteEndPlay(EEndPlayReason::Quit);
		}
		if (Fixture.Actor != nullptr && !Fixture.Actor->IsActorBeingDestroyed())
		{
			Fixture.Actor->Destroy();
		}
		TestTrue(*FString::Printf(TEXT("%s managed component stops before garbage collection"), Label),
		         Fixture.Component == nullptr || !Fixture.Component->bScriptRunning);
		Fixture.Actor = nullptr;
		Fixture.Component = nullptr;
		CollectGarbage(RF_NoFlags);
		if (Fixture.Host != nullptr)
		{
			TestEqual(*FString::Printf(TEXT("%s managed script count returns to baseline"), Label),
			          Fixture.Host->GetManagedScriptCount(), Fixture.ManagedScriptsBefore);
			TestEqual(*FString::Printf(TEXT("%s registered object count returns to baseline"), Label),
			          Fixture.Host->NumRegisteredObjects(), Fixture.RegisteredObjectsBefore);
			TestEqual(*FString::Printf(TEXT("%s lifecycle claims return to baseline"), Label),
			          Fixture.Host->GetGameplayLifecycleClaimCount(), Fixture.LifecycleClaimsBefore);
		}
		if (Fixture.Context != nullptr && GEngine != nullptr)
		{
			GEngine->DestroyWorldContext(Fixture.World);
			Fixture.Context = nullptr;
		}
		if (Fixture.World != nullptr)
		{
			Fixture.World->RemoveFromRoot();
			Fixture.World->DestroyWorld(false);
			Fixture.World = nullptr;
		}
	};
	ON_SCOPE_EXIT
	{
		DestroyReloadWorld(First, TEXT("First"));
		DestroyReloadWorld(Second, TEXT("Second"));
		TestEqual(
			TEXT("Blueprint event reload restores managed World count"),
			FFinareCoreClrHost::Get().GetManagedWorldCount(),
			InitialManagedWorldCount);
	};

	CreateReloadWorld(First, TEXT("First"));
	CreateReloadWorld(Second, TEXT("Second"));
	if (First.Actor == nullptr || Second.Actor == nullptr ||
		First.Component == nullptr || Second.Component == nullptr ||
		First.Host == nullptr || Second.Host == nullptr)
	{
		return false;
	}

	auto InvokeNative = [&](FReloadWorld& Fixture, int32 ExpectedReturn, const TCHAR* Label)
	{
		const uint64 BeforeFastCalls = Fixture.Host->GetFastInvocationCount();
		const int32 BeforeParentCalls = Fixture.Actor->ParentInvocationCount;
		int32 Accumulator = 5;
		int32 Output = 0;
		const int32 ReturnValue = Fixture.Actor->EvaluateInherited(7, Accumulator, Output);
		TestEqual(*FString::Printf(TEXT("%s native typed return"), Label), ReturnValue, ExpectedReturn);
		TestEqual(*FString::Printf(TEXT("%s native typed ref"), Label), Accumulator, 12);
		TestEqual(*FString::Printf(TEXT("%s native typed out"), Label), Output, 12);
		TestEqual(*FString::Printf(TEXT("%s native typed managed fast call"), Label),
		          Fixture.Host->GetFastInvocationCount(), BeforeFastCalls + 1);
		TestEqual(*FString::Printf(TEXT("%s native parent executes once"), Label),
		          Fixture.Actor->ParentInvocationCount, BeforeParentCalls + 1);
	};
	auto InvokeWrapper = [&](FReloadWorld& Fixture, int32 ExpectedReturn, const TCHAR* Label)
	{
		const FIntProperty* Input = FindFProperty<FIntProperty>(CallEvaluate, TEXT("NativeInput"));
		const FIntProperty* Accumulator = FindFProperty<FIntProperty>(CallEvaluate, TEXT("NativeAccumulator"));
		const FIntProperty* Output = FindFProperty<FIntProperty>(CallEvaluate, TEXT("NativeOutput"));
		const FIntProperty* Return = CastField<FIntProperty>(CallEvaluate->GetReturnProperty());
		if (!TestTrue(*FString::Printf(TEXT("%s wrapper signature exists"), Label),
		              Input != nullptr && Accumulator != nullptr && Output != nullptr && Return != nullptr))
		{
			return;
		}
		FStructOnScope Call(CallEvaluate);
		SetInt(*Input, Call.GetStructMemory(), 7);
		SetInt(*Accumulator, Call.GetStructMemory(), 5);
		const uint64 BeforeFastCalls = Fixture.Host->GetFastInvocationCount();
		const int32 BeforeParentCalls = Fixture.Actor->ParentInvocationCount;
		Fixture.Actor->ProcessEvent(CallEvaluate, Call.GetStructMemory());
		TestEqual(*FString::Printf(TEXT("%s ordinary Blueprint wrapper return"), Label),
		          GetInt(*Return, Call.GetStructMemory()), ExpectedReturn);
		TestEqual(*FString::Printf(TEXT("%s ordinary Blueprint wrapper ref"), Label),
		          GetInt(*Accumulator, Call.GetStructMemory()), 12);
		TestEqual(*FString::Printf(TEXT("%s ordinary Blueprint wrapper out"), Label),
		          GetInt(*Output, Call.GetStructMemory()), 12);
		TestEqual(*FString::Printf(TEXT("%s ordinary Blueprint wrapper downstream observation"), Label),
		          GetInt(*ObservedResult, Fixture.Actor), ExpectedReturn);
		TestEqual(*FString::Printf(TEXT("%s ordinary Blueprint wrapper managed fast call"), Label),
		          Fixture.Host->GetFastInvocationCount(), BeforeFastCalls + 1);
		TestEqual(*FString::Printf(TEXT("%s ordinary Blueprint wrapper native parent executes once"), Label),
		          Fixture.Actor->ParentInvocationCount, BeforeParentCalls + 1);
	};

	InvokeNative(First, 12, TEXT("First v1"));
	InvokeNative(Second, 12, TEXT("Second v1"));
	InvokeWrapper(First, 12, TEXT("First v1 wrapper"));
	InvokeWrapper(Second, 12, TEXT("Second v1 wrapper"));

	TestTrue(TEXT("Copies v2 into the same active DLL path"),
	         IFileManager::Get().Copy(*ActiveAssembly, *V2Assembly, true, true) == COPY_OK);
	InvokeNative(First, 12, TEXT("First remains v1 before reload"));
	InvokeNative(Second, 12, TEXT("Second remains v1 before reload"));
	const FString FirstGenerationBeforeFirstReload = First.Component->ActiveGeneration;
	const FString SecondGenerationBeforeFirstReload = Second.Component->ActiveGeneration;
	TestTrue(TEXT("First component reloads the v2 body"), First.Component->ReloadScript());
	TestTrue(TEXT("First generation changes after v2 reload"),
	         !First.Component->ActiveGeneration.Equals(FirstGenerationBeforeFirstReload, ESearchCase::CaseSensitive));
	TestEqual(TEXT("Second generation remains v1 until its own reload"),
	          Second.Component->ActiveGeneration, SecondGenerationBeforeFirstReload);
	InvokeNative(First, 112, TEXT("First v2 after reload"));
	InvokeWrapper(First, 112, TEXT("First v2 wrapper after reload"));
	InvokeNative(Second, 12, TEXT("Second remains v1 after First reload"));
	InvokeWrapper(Second, 12, TEXT("Second v1 wrapper after First reload"));

	TestTrue(TEXT("Copies incompatible route variant into the active DLL path"),
	         IFileManager::Get().Copy(*ActiveAssembly, *IncompatibleAssembly, true, true) == COPY_OK);
	FString ActivationContractBeforeReject;
	FString ActivationContractError;
	TestTrue(TEXT("Activation contract is readable before incompatible reload"),
	         ManagedClass->BuildBlueprintOverrideActivationContract(
		         ActivationContractBeforeReject,
		         ActivationContractError));
	UFinareBlueprintGeneratedClass* GeneratedClassBeforeReject = ManagedClass;
	const FString FirstGenerationBeforeReject = First.Component->ActiveGeneration;
	const FString SecondGenerationBeforeReject = Second.Component->ActiveGeneration;
	const int32 FirstScriptsBeforeReject = First.Host->GetManagedScriptCount();
	const int32 SecondScriptsBeforeReject = Second.Host->GetManagedScriptCount();
	const int32 FirstObjectsBeforeReject = First.Host->NumRegisteredObjects();
	const int32 SecondObjectsBeforeReject = Second.Host->NumRegisteredObjects();
	const int32 FirstClaimsBeforeReject = First.Host->GetGameplayLifecycleClaimCount();
	const int32 SecondClaimsBeforeReject = Second.Host->GetGameplayLifecycleClaimCount();
	AddExpectedError(
		TEXT(
			"Blueprint override route '/Script/FinareRuntime.FinareEventExtensionFixture:EvaluateInherited' route is missing"),
		EAutomationExpectedErrorFlags::Contains,
		2);
	TestFalse(TEXT("First rejects incompatible route while retaining v2"), First.Component->ReloadScript());
	TestFalse(TEXT("Second rejects incompatible route while retaining v1"), Second.Component->ReloadScript());
	auto HasIncompatibleRouteDiagnostic = [](const UFinareComponent& Component)
	{
		return Component.LastReloadTransaction.Contains(TEXT("activationContractValidation")) &&
			Component.LastReloadTransaction.Contains(TEXT("failed")) &&
			(Component.LastReloadTransaction.Contains(TEXT("EvaluateInheritedAlternate")) ||
				Component.LastScriptError.Contains(TEXT(":EvaluateInherited' route is missing")));
	};
	TestTrue(TEXT("First rejection reports activation/override contract and route"),
	         HasIncompatibleRouteDiagnostic(*First.Component));
	TestTrue(TEXT("Second rejection reports activation/override contract and route"),
	         HasIncompatibleRouteDiagnostic(*Second.Component));
	FString ActivationContractAfterReject;
	FString ActivationContractAfterRejectError;
	TestTrue(TEXT("Rejected reload keeps the persisted activation contract"),
	         ManagedClass == GeneratedClassBeforeReject &&
	         ManagedClass->BuildBlueprintOverrideActivationContract(
		         ActivationContractAfterReject,
		         ActivationContractAfterRejectError) &&
	         ActivationContractAfterReject.Equals(
		         ActivationContractBeforeReject,
		         ESearchCase::CaseSensitive));
	TestEqual(TEXT("First rejected reload keeps generation"), First.Component->ActiveGeneration,
	          FirstGenerationBeforeReject);
	TestEqual(TEXT("Second rejected reload keeps generation"), Second.Component->ActiveGeneration,
	          SecondGenerationBeforeReject);
	TestTrue(TEXT("Rejected reload keeps both scripts running"),
	         First.Component->bScriptRunning && Second.Component->bScriptRunning);
	TestEqual(TEXT("First rejection keeps managed script count"), First.Host->GetManagedScriptCount(),
	          FirstScriptsBeforeReject);
	TestEqual(TEXT("Second rejection keeps managed script count"), Second.Host->GetManagedScriptCount(),
	          SecondScriptsBeforeReject);
	TestEqual(TEXT("First rejection keeps registered object count"), First.Host->NumRegisteredObjects(),
	          FirstObjectsBeforeReject);
	TestEqual(TEXT("Second rejection keeps registered object count"), Second.Host->NumRegisteredObjects(),
	          SecondObjectsBeforeReject);
	TestEqual(TEXT("First rejection keeps lifecycle claims"), First.Host->GetGameplayLifecycleClaimCount(),
	          FirstClaimsBeforeReject);
	TestEqual(TEXT("Second rejection keeps lifecycle claims"), Second.Host->GetGameplayLifecycleClaimCount(),
	          SecondClaimsBeforeReject);
	InvokeNative(First, 112, TEXT("First v2 survives incompatible rejection"));
	InvokeNative(Second, 12, TEXT("Second v1 survives incompatible rejection"));
	InvokeWrapper(First, 112, TEXT("First v2 wrapper survives incompatible rejection"));
	InvokeWrapper(Second, 12, TEXT("Second v1 wrapper survives incompatible rejection"));

	TestTrue(TEXT("Copies v2 back into the active DLL path"),
	         IFileManager::Get().Copy(*ActiveAssembly, *V2Assembly, true, true) == COPY_OK);
	TestTrue(TEXT("Second component accepts compatible v2 reload"), Second.Component->ReloadScript());
	InvokeNative(First, 112, TEXT("First v2 remains after Second compatible reload"));
	InvokeWrapper(First, 112, TEXT("First v2 wrapper remains after Second compatible reload"));
	InvokeNative(Second, 112, TEXT("Second v2 after compatible reload"));
	InvokeWrapper(Second, 112, TEXT("Second v2 wrapper after compatible reload"));
	TestTrue(TEXT("First remains callable after Second compatible reload"), First.Component->bScriptRunning);

	return !HasAnyErrors();
}

#endif

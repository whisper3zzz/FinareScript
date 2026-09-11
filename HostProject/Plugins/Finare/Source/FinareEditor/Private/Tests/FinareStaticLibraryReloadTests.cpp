#if WITH_DEV_AUTOMATION_TESTS

#include "FinareBlueprintClassMaterializer.h"

#include "Dom/JsonObject.h"
#include "EdGraph/EdGraph.h"
#include "EdGraphSchema_K2.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "HAL/FileManager.h"
#include "K2Node_CallFunction.h"
#include "K2Node_FunctionEntry.h"
#include "K2Node_FunctionResult.h"
#include "K2Node_VariableSet.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "PackageTools.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "FinareAsset.h"
#include "FinareAuthoringWorkbench.h"
#include "FinareBlueprint.h"
#include "FinareClassBases.h"
#include "FinareComponent.h"
#include "FinareCoreClrHost.h"
#include "FinareWorldSubsystem.h"
#include "UObject/Package.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"
#include "Widgets/SFinareAuthoringSurface.h"

namespace UE::Finare::StaticLibraryReloadTests::Private
{
	const TCHAR* FixtureTypeName =
		TEXT("Finare.Tests.Fixtures.StaticLibraryReload.StaticLibraryReloadFixture");
	const TCHAR* FixtureFunctionName = TEXT("ExchangeArray");
	const TCHAR* FixtureFunctionId = TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
	const TCHAR* FixtureAssetId = TEXT("44444444444444444444444444444444");

	FString Parameter(
		const TCHAR* Id,
		const TCHAR* Name,
		const TCHAR* Metadata,
		const FString& UnrealType,
		const TCHAR* Direction = TEXT("input"))
	{
		return FString::Printf(
			TEXT(R"JSON({"id":"us_%s","name":"%s","typeMetadataName":"%s","unrealType":%s%s})JSON"),
			Id,
			Name,
			Metadata,
			*UnrealType,
			FCString::Strcmp(Direction, TEXT("input")) == 0
				? TEXT("")
				: *FString::Printf(TEXT(",\"direction\":\"%s\""), Direction));
	}

	FString ArrayType()
	{
		return TEXT(
			R"JSON({"kind":"array","unrealTypePath":"","unrealNativeName":"TArray","fields":[],"typeArguments":[{"typeMetadataName":"System.Int32"}]})JSON");
	}

	FString FixtureDocument()
	{
		const FString Parameters = FString::Join(
			TArray<FString>{
				Parameter(
					TEXT("11111111111111111111111111111111"),
					TEXT("replacement"),
					TEXT("System.Collections.Generic.IReadOnlyList<System.Int32>"),
					ArrayType()),
				Parameter(
					TEXT("22222222222222222222222222222222"),
					TEXT("value"),
					TEXT("System.Collections.Generic.IReadOnlyList<System.Int32>"),
					ArrayType(),
					TEXT("inputOutput")),
				Parameter(
					TEXT("33333333333333333333333333333333"),
					TEXT("previous"),
					TEXT("System.Collections.Generic.IReadOnlyList<System.Int32>"),
					ArrayType(),
					TEXT("output")),
			},
			TEXT(","));
		return FString::Printf(
			TEXT(
				R"JSON({"schemaVersion":"unifiedscript.script-asset/v0.1","sourceCompilationId":"us_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","semanticConfigurationId":"us_cccccccccccccccccccccccccccccccc","assets":[{"id":"us_%s","qualifiedName":"%s","assetPath":"StaticLibraryReload/Fixture","span":{"filePath":"StaticLibraryReloadFixture.cs","start":{"line":1,"column":1},"end":{"line":50,"column":2}},"states":[],"functions":[{"id":"us_%s","name":"%s","qualifiedName":"%s.%s()","returnTypeMetadataName":"System.Collections.Generic.IReadOnlyList<System.Int32>","returnUnrealType":%s,"isStatic":true,"isPublic":true,"isEvent":false,"parameters":[%s]}],"lifecycleHooks":[]}]})JSON"),
			FixtureAssetId,
			FixtureTypeName,
			FixtureFunctionId,
			FixtureFunctionName,
			FixtureTypeName,
			FixtureFunctionName,
			*ArrayType(),
			*Parameters);
	}

	FString FixtureRoot()
	{
		return FPaths::ConvertRelativePathToFull(
			FPaths::Combine(FPaths::ProjectDir(), TEXT("../artifacts/m13-3c-1c-b-static-library-reload/fixture")));
	}

	FString FixturePath(const TCHAR* Variant)
	{
		return FPaths::Combine(FixtureRoot(), Variant, TEXT("StaticLibraryReloadFixture.dll"));
	}

	bool CopyFixture(const TCHAR* Variant)
	{
		return IFileManager::Get().Copy(*FixturePath(TEXT("active")), *FixturePath(Variant), true, true) == COPY_OK;
	}

	bool ValidateStaticReloadReceipt(
		FAutomationTestBase& Test,
		const FString& Json,
		const TCHAR* ExpectedTransactionId,
		const TCHAR* ExpectedSource,
		const TCHAR* ExpectedBuild,
		int32 ExpectedParticipants)
	{
		FFinareReloadTransactionResult Parsed;
		FString ParseError;
		const bool bParsed = FFinareAuthoringWorkbench::ParseReloadTransaction(
			Json,
			Parsed,
			ParseError);
		if (!Test.TestTrue(
			TEXT("Static reload receipt remains readable by the existing transaction parser"),
			bParsed))
		{
			if (!ParseError.IsEmpty())
			{
				Test.AddError(ParseError);
			}
			return false;
		}
		Test.TestEqual(
			TEXT("Static reload receipt preserves transaction identity"), Parsed.TransactionId,
			FString(ExpectedTransactionId));
		Test.TestEqual(
			TEXT("Static reload receipt preserves transaction source"), Parsed.Source, FString(ExpectedSource));
		Test.TestTrue(TEXT("Static reload receipt preserves the incoming artifact phase"),
		              Parsed.Phases.ContainsByPredicate([](const FFinareReloadPhaseResult& Phase)
		              {
			              return Phase.Name == TEXT("artifactPublication");
		              }));

		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
		if (!Test.TestTrue(
			TEXT("Static reload receipt JSON parses"), FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid()))
		{
			return false;
		}
		FString Build;
		FString Scope;
		const TArray<TSharedPtr<FJsonValue>>* Participants = nullptr;
		Test.TestTrue(TEXT("Static reload receipt preserves incoming build identity"),
		              Root->TryGetStringField(TEXT("build"), Build) && Build == ExpectedBuild);
		Test.TestTrue(TEXT("Static reload receipt marks the static-library batch scope"),
		              Root->TryGetStringField(TEXT("scope"), Scope) && Scope == TEXT("staticLibraryBatch"));
		const bool bParticipants = Root->TryGetArrayField(TEXT("participants"), Participants) &&
			Participants != nullptr && Participants->Num() == ExpectedParticipants;
		if (!Test.TestTrue(TEXT("Static reload receipt contains each real World participant"), bParticipants))
		{
			return false;
		}
		for (const TSharedPtr<FJsonValue>& Value : *Participants)
		{
			const TSharedPtr<FJsonObject> Participant = Value.IsValid() ? Value->AsObject() : nullptr;
			int32 CandidateGeneration = 0;
			bool bCandidateKnown = false;
			bool bActiveKnown = false;
			Test.TestTrue(TEXT("Static reload receipt stores a real candidate generation"),
			              Participant.IsValid() &&
			              Participant->TryGetNumberField(TEXT("candidateGeneration"), CandidateGeneration) &&
			              Participant->TryGetBoolField(TEXT("candidateGenerationKnown"), bCandidateKnown) &&
			              CandidateGeneration > 0 && bCandidateKnown &&
			              Participant->TryGetBoolField(TEXT("activeGenerationKnown"), bActiveKnown) && bActiveKnown);
		}
		TSet<int32> CandidateGenerations;
		for (const TSharedPtr<FJsonValue>& Value : *Participants)
		{
			const TSharedPtr<FJsonObject> Participant = Value.IsValid() ? Value->AsObject() : nullptr;
			int32 CandidateGeneration = 0;
			if (Participant.IsValid() && Participant->TryGetNumberField(
				TEXT("candidateGeneration"), CandidateGeneration))
			{
				CandidateGenerations.Add(CandidateGeneration);
			}
		}
		Test.TestEqual(TEXT("Static reload receipt keeps participant generations independently addressable"),
		               CandidateGenerations.Num(), ExpectedParticipants);
		return !Test.HasAnyErrors();
	}

	struct FWorldFixture
	{
		UWorld* World = nullptr;
		UFinareWorldSubsystem* Host = nullptr;
		UFinareComponent* Context = nullptr;
		AActor* Caller = nullptr;
	};

	FWorldFixture CreateWorldFixture(FAutomationTestBase& Test, const TCHAR* Label, UClass* CallerClass)
	{
		FWorldFixture Result;
		Result.World = UWorld::CreateWorld(EWorldType::Game, false);
		if (!Test.TestNotNull(*FString::Printf(TEXT("%s World created"), Label), Result.World))
		{
			return Result;
		}
		Result.World->AddToRoot();
		Result.World->InitializeActorsForPlay(FURL());
		Result.Host = Result.World->GetSubsystem<UFinareWorldSubsystem>();
		Result.Context = NewObject<UFinareComponent>(Result.World);
		Result.Caller = CallerClass != nullptr
			                ? Result.World->SpawnActor<AActor>(CallerClass)
			                : nullptr;
		Test.TestNotNull(*FString::Printf(TEXT("%s managed World host ready"), Label), Result.Host);
		Test.TestNotNull(*FString::Printf(TEXT("%s World context created"), Label), Result.Context);
		Test.TestTrue(*FString::Printf(TEXT("%s managed World is ready"), Label),
		              Result.Host != nullptr && Result.Host->IsManagedWorldReady());
		Test.TestNotNull(*FString::Printf(TEXT("%s spawns a normal Blueprint caller"), Label), Result.Caller);
		if (Result.Context != nullptr)
		{
			Result.Context->RegisterComponentWithWorld(Result.World);
		}
		return Result;
	}

	void DestroyWorldFixture(FWorldFixture& Fixture)
	{
		if (Fixture.Context != nullptr)
		{
			Fixture.Context->UnregisterComponent();
			Fixture.Context = nullptr;
		}
		if (Fixture.World != nullptr && Fixture.Caller != nullptr)
		{
			Fixture.World->DestroyActor(Fixture.Caller);
			Fixture.Caller = nullptr;
		}
		if (Fixture.World != nullptr)
		{
			Fixture.World->RemoveFromRoot();
			Fixture.World->DestroyWorld(false);
			Fixture.World = nullptr;
		}
		Fixture.Host = nullptr;
	}

	bool InvokeCaller(
		FAutomationTestBase& Test,
		UClass* CallerClass,
		UObject* CallerObject,
		UFinareComponent* Context,
		bool bExpectReplacement)
	{
		UFunction* Caller = CallerClass->FindFunctionByName(
			TEXT("Call_ExchangeArray"),
			EIncludeSuperFlag::ExcludeSuper);
		const FProperty* Replacement = Caller != nullptr
			                               ? FindFProperty<FProperty>(Caller, TEXT("replacement"))
			                               : nullptr;
		const FProperty* Value = Caller != nullptr
			                         ? FindFProperty<FProperty>(Caller, TEXT("value"))
			                         : nullptr;
		const FProperty* Previous = Caller != nullptr
			                            ? FindFProperty<FProperty>(Caller, TEXT("previous"))
			                            : nullptr;
		const FProperty* Return = Caller != nullptr ? Caller->GetReturnProperty() : nullptr;
		const FProperty* Observed = FindFProperty<FProperty>(CallerClass, TEXT("Observed"));
		const FObjectPropertyBase* WorldContext = Caller != nullptr
			                                          ? FindFProperty<FObjectPropertyBase>(
				                                          Caller, TEXT("WorldContextObject"))
			                                          : nullptr;
		if (WorldContext == nullptr && Caller != nullptr)
		{
			WorldContext = FindFProperty<FObjectPropertyBase>(Caller, TEXT("__WorldContext"));
		}
		if (!Test.TestNotNull(TEXT("Static reload caller exists"), Caller) ||
			!Test.TestNotNull(TEXT("Static reload caller replacement pin exists"), Replacement) ||
			!Test.TestNotNull(TEXT("Static reload caller ref pin exists"), Value) ||
			!Test.TestNotNull(TEXT("Static reload caller out pin exists"), Previous) ||
			!Test.TestNotNull(TEXT("Static reload caller return pin exists"), Return) ||
			!Test.TestNotNull(TEXT("Static reload caller observation exists"), Observed) ||
			!Test.TestNotNull(TEXT("Static reload caller WorldContext pin exists"), WorldContext))
		{
			return false;
		}

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
		WorldContext->SetObjectPropertyValue_InContainer(Memory, Context);
		if (const FObjectPropertyBase* KismetWorldContext = FindFProperty<FObjectPropertyBase>(
			Caller, TEXT("__WorldContext")))
		{
			KismetWorldContext->SetObjectPropertyValue_InContainer(Memory, Context);
		}
		FillArray(*Replacement, Replacement->ContainerPtrToValuePtr<void>(Memory), true);
		FillArray(*Value, Value->ContainerPtrToValuePtr<void>(Memory), false);
		Value->CopyCompleteValue_InContainer(Before.GetStructMemory(), Memory);
		CallerObject->ProcessEvent(Caller, Memory);

		const void* Expected = bExpectReplacement
			                       ? Replacement->ContainerPtrToValuePtr<void>(Memory)
			                       : Value->ContainerPtrToValuePtr<void>(Before.GetStructMemory());
		return Test.TestTrue(
				TEXT("Static reload caller writes ref result"),
				Value->Identical(
					Value->ContainerPtrToValuePtr<void>(Memory),
					Replacement->ContainerPtrToValuePtr<void>(Memory))) &&
			Test.TestTrue(
				TEXT("Static reload caller writes expected out result"),
				Previous->Identical(Previous->ContainerPtrToValuePtr<void>(Memory), Expected)) &&
			Test.TestTrue(
				TEXT("Static reload caller writes expected return result"),
				Return->Identical(Return->ContainerPtrToValuePtr<void>(Memory), Expected)) &&
			Test.TestTrue(
				TEXT("Static reload caller observes expected downstream result"),
				Observed->Identical(Observed->ContainerPtrToValuePtr<void>(CallerObject), Expected));
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareStaticLibraryReloadTest,
	"Finare.Unreal.ScriptClass.StaticLibraryReload",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareStaticLibraryReloadTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::StaticLibraryReloadTests::Private;
	const FString ActiveAssembly = FixturePath(TEXT("active"));
	if (!TestTrue(TEXT("Static reload fixture v1 is staged"), IFileManager::Get().FileExists(*ActiveAssembly)) ||
		!TestTrue(TEXT("Static reload fixture v2 is staged"), IFileManager::Get().FileExists(*FixturePath(TEXT("v2"))))
		||
		!TestTrue(
			TEXT("Static reload incompatible fixture is staged"),
			IFileManager::Get().FileExists(*FixturePath(TEXT("incompatible")))))
	{
		return false;
	}
	if (!TestTrue(TEXT("Resets the active DLL path to fixture v1"), CopyFixture(TEXT("v1"))))
	{
		return false;
	}

	const int32 InitialManagedWorldCount = FFinareCoreClrHost::Get().GetManagedWorldCount();
	const FString PackageName = TEXT("/Game/UnifiedScriptTests/StaticLibraryReload_") +
		FGuid::NewGuid().ToString(EGuidFormats::Digits);
	const FString PackageFilename = FPaths::ConvertRelativePathToFull(
		FPackageName::LongPackageNameToFilename(
			PackageName,
			FPackageName::GetAssetPackageExtension()));
	UPackage* Package = CreatePackage(*PackageName);
	ON_SCOPE_EXIT
	{
		if (Package != nullptr)
		{
			Package->SetDirtyFlag(false);
			TArray<UPackage*> Packages{Package};
			UPackageTools::UnloadPackages(Packages);
		}
		if (IFileManager::Get().FileExists(*PackageFilename) &&
			!IFileManager::Get().Delete(*PackageFilename, false, true, true))
		{
			AddError(FString::Printf(
				TEXT("Static reload fixture package cleanup failed: %s"),
				*PackageFilename));
		}
	};

	UFinareBlueprint* LibraryBlueprint = Cast<UFinareBlueprint>(
		FKismetEditorUtilities::CreateBlueprint(
			UFinareBlueprintFunctionLibrary::StaticClass(),
			Package,
			TEXT("BP_StaticLibraryReload"),
			BPTYPE_FunctionLibrary,
			UFinareBlueprint::StaticClass(),
			UFinareBlueprintGeneratedClass::StaticClass(),
			FName(TEXT("StaticLibraryReload"))));
	UBlueprint* CallerBlueprint = FKismetEditorUtilities::CreateBlueprint(
		AActor::StaticClass(),
		Package,
		TEXT("BP_StaticLibraryReloadCaller"),
		BPTYPE_Normal,
		UBlueprint::StaticClass(),
		UBlueprintGeneratedClass::StaticClass(),
		FName(TEXT("StaticLibraryReloadCaller")));
	if (!TestNotNull(TEXT("Static reload FunctionLibrary Blueprint created"), LibraryBlueprint) ||
		!TestNotNull(TEXT("Static reload normal Blueprint caller created"), CallerBlueprint))
	{
		return false;
	}

	UFinareAsset* Asset = NewObject<UFinareAsset>(Package);
	Asset->ScriptClassId = FGuid(
		0x44444444,
		0x44444444,
		0x44444444,
		0x44444444);
	Asset->ScriptClassKind = EFinareClassKind::BlueprintFunctionLibrary;
	Asset->GeneratedClassPath = FSoftClassPath(LibraryBlueprint->GeneratedClass);
	Asset->ScriptAssemblyPath = ActiveAssembly;
	Asset->ScriptTypeName = FixtureTypeName;
	Asset->DeclarationAssetPath = TEXT("StaticLibraryReload/Fixture");
	LibraryBlueprint->ScriptAsset = Asset;

	FFinareBlueprintClassSyncResult SyncResult;
	FString SyncError;
	if (!TestTrue(
		TEXT("Static reload FunctionLibrary metadata materializes"),
		FFinareBlueprintClassMaterializer::SynchronizeJson(
			*Asset,
			*LibraryBlueprint,
			FixtureDocument(),
			false,
			SyncResult,
			SyncError)))
	{
		AddError(SyncError);
		return false;
	}

	UFinareBlueprintGeneratedClass* LibraryClass =
		Cast<UFinareBlueprintGeneratedClass>(LibraryBlueprint->GeneratedClass);
	UFunction* Exchange = LibraryClass != nullptr
		                      ? LibraryClass->FindFunctionByName(TEXT("USF_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"))
		                      : nullptr;
	if (!TestNotNull(TEXT("Static reload generated ExchangeArray function exists"), Exchange))
	{
		return false;
	}

	UEdGraph* CallerGraph = FBlueprintEditorUtils::CreateNewGraph(
		CallerBlueprint,
		TEXT("Call_ExchangeArray"),
		UEdGraph::StaticClass(),
		UEdGraphSchema_K2::StaticClass());
	FBlueprintEditorUtils::AddFunctionGraph<UClass>(CallerBlueprint, CallerGraph, true, nullptr);
	TArray<UK2Node_FunctionEntry*> Entries;
	CallerGraph->GetNodesOfClass(Entries);
	TArray<UK2Node_FunctionResult*> Results;
	CallerGraph->GetNodesOfClass(Results);
	if (Results.IsEmpty() && !Entries.IsEmpty())
	{
		if (UK2Node_FunctionResult* Result = FBlueprintEditorUtils::FindOrCreateFunctionResultNode(Entries[0]))
		{
			Results.Add(Result);
		}
	}
	UK2Node_CallFunction* CallNode = NewObject<UK2Node_CallFunction>(CallerGraph);
	CallNode->FunctionReference.SetExternalMember(Exchange->GetFName(), LibraryClass);
	CallerGraph->AddNode(CallNode, true, false);
	CallNode->AllocateDefaultPins();
	TArray<FName> InputNames{TEXT("replacement"), TEXT("value")};
	TArray<FName> OutputNames{TEXT("previous"), TEXT("ReturnValue")};
	if (Entries.Num() != 1 || Results.Num() != 1 ||
		CallNode->FindPin(TEXT("replacement")) == nullptr ||
		CallNode->FindPin(TEXT("value")) == nullptr ||
		CallNode->FindPin(TEXT("previous")) == nullptr ||
		CallNode->GetReturnValuePin() == nullptr)
	{
		AddError(TEXT("Static reload caller pins were not generated."));
		return false;
	}
	if (!TestTrue(
		TEXT("Static reload caller has a typed previous pin for observation"),
		FBlueprintEditorUtils::AddMemberVariable(
			CallerBlueprint,
			TEXT("Observed"),
			CallNode->FindPin(TEXT("previous"))->PinType)))
	{
		return false;
	}
	UEdGraphPin* CallWorldContext = CallNode->FindPin(TEXT("WorldContextObject"));
	if (CallWorldContext != nullptr && Entries[0]->FindPin(TEXT("WorldContextObject")) == nullptr)
	{
		Entries[0]->CreateUserDefinedPin(TEXT("WorldContextObject"), CallWorldContext->PinType, EGPD_Output, false);
	}
	UEdGraphPin* EntryWorldContext = Entries[0]->FindPin(TEXT("WorldContextObject"));
	UEdGraphPin* EntryKismetWorldContext = Entries[0]->GetAutoWorldContextPin();
	UEdGraphPin* CallKismetWorldContext = CallNode->FindPin(TEXT("__WorldContext"));
	for (const FName Name : InputNames)
	{
		Entries[0]->CreateUserDefinedPin(Name, CallNode->FindPin(Name)->PinType, EGPD_Output);
	}
	for (const FName Name : OutputNames)
	{
		UEdGraphPin* Pin = Name == TEXT("ReturnValue") ? CallNode->GetReturnValuePin() : CallNode->FindPin(Name);
		Results[0]->CreateUserDefinedPin(Name, Pin->PinType, EGPD_Input);
	}
	UK2Node_VariableSet* Observe = NewObject<UK2Node_VariableSet>(CallerGraph);
	Observe->VariableReference.SetSelfMember(TEXT("Observed"));
	CallerGraph->AddNode(Observe, true, false);
	Observe->AllocateDefaultPins();
	UEdGraphPin* ObserveValue = Observe->FindPin(TEXT("Observed"), EGPD_Input);
	const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
	const bool bConnected = Schema->TryCreateConnection(
			Entries[0]->FindPin(UEdGraphSchema_K2::PN_Then), CallNode->FindPin(UEdGraphSchema_K2::PN_Execute)) &&
		Schema->TryCreateConnection(CallNode->FindPin(UEdGraphSchema_K2::PN_Then), Observe->GetExecPin()) &&
		Schema->TryCreateConnection(Observe->GetThenPin(), Results[0]->FindPin(UEdGraphSchema_K2::PN_Execute)) &&
		EntryWorldContext != nullptr && CallWorldContext != nullptr &&
		Schema->TryCreateConnection(EntryWorldContext, CallWorldContext) &&
		(EntryKismetWorldContext == nullptr || CallKismetWorldContext == nullptr ||
			Schema->TryCreateConnection(EntryKismetWorldContext, CallKismetWorldContext)) &&
		Schema->TryCreateConnection(Entries[0]->FindPin(TEXT("replacement")), CallNode->FindPin(TEXT("replacement"))) &&
		Schema->TryCreateConnection(Entries[0]->FindPin(TEXT("value")), CallNode->FindPin(TEXT("value"))) &&
		Schema->TryCreateConnection(CallNode->FindPin(TEXT("previous")), Results[0]->FindPin(TEXT("previous"))) &&
		Schema->TryCreateConnection(CallNode->FindPin(TEXT("previous")), ObserveValue) &&
		Schema->TryCreateConnection(CallNode->GetReturnValuePin(), Results[0]->FindPin(TEXT("ReturnValue")));
	if (!TestTrue(TEXT("Static reload normal Blueprint caller connects VM pins"), bConnected))
	{
		return false;
	}
	FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(CallerBlueprint);
	FKismetEditorUtilities::CompileBlueprint(CallerBlueprint);
	UBlueprintGeneratedClass* CallerClass = Cast<UBlueprintGeneratedClass>(CallerBlueprint->GeneratedClass);
	if (!TestEqual(TEXT("Static reload Blueprint caller compiles"), CallerBlueprint->Status, BS_UpToDate) ||
		!TestNotNull(TEXT("Static reload Blueprint caller class exists"), CallerClass))
	{
		return false;
	}

	FWorldFixture First = CreateWorldFixture(*this, TEXT("First"), CallerClass);
	FWorldFixture Second = CreateWorldFixture(*this, TEXT("Second"), CallerClass);
	FWorldFixture Cold = CreateWorldFixture(*this, TEXT("Cold"), CallerClass);
	TestTrue(TEXT("Blueprint caller has real bytecode"),
	         CallerClass->FindFunctionByName(TEXT("Call_ExchangeArray"), EIncludeSuperFlag::ExcludeSuper) != nullptr &&
	         CallerClass->FindFunctionByName(TEXT("Call_ExchangeArray"), EIncludeSuperFlag::ExcludeSuper)->Script.Num()
	         > 0 &&
	         !CallerClass->FindFunctionByName(TEXT("Call_ExchangeArray"), EIncludeSuperFlag::ExcludeSuper)->
	                       HasAnyFunctionFlags(FUNC_Native));
	TestNotNull(TEXT("Cold World owns a distinct Blueprint caller"), Cold.Caller);
	const bool bInitial = First.Context != nullptr && Second.Context != nullptr &&
		First.Caller != nullptr && Second.Caller != nullptr &&
		InvokeCaller(*this, CallerClass, First.Caller, First.Context, false) &&
		InvokeCaller(*this, CallerClass, Second.Caller, Second.Context, false);
	TestTrue(TEXT("Two loaded Worlds execute v1 through the real Blueprint VM caller"), bInitial);

	TestTrue(TEXT("Copies fixture v2 into the active DLL path"), CopyFixture(TEXT("v2")));
	int32 DirectCandidateGeneration = 0;
	int32 DirectActiveGeneration = 0;
	FString DirectReloadError;
	TestTrue(
		TEXT("Native World wrapper prepares the v2 static-library candidate"),
		First.Host->PrepareManagedStaticLibraryReload(
			ActiveAssembly,
			FixtureTypeName,
			DirectCandidateGeneration,
			DirectReloadError) &&
		DirectCandidateGeneration > 0);
	TestTrue(
		TEXT("Native World wrapper commits the v2 static-library candidate"),
		First.Host->CommitManagedStaticLibraryReload(
			ActiveAssembly,
			FixtureTypeName,
			DirectCandidateGeneration,
			DirectActiveGeneration,
			DirectReloadError) &&
		DirectActiveGeneration > 0 &&
		InvokeCaller(*this, CallerClass, First.Caller, First.Context, true));
	DirectReloadError.Reset();
	TestTrue(
		TEXT("Native World wrapper aborts after commit and restores the v1 generation"),
		First.Host->AbortManagedStaticLibraryReload(
			ActiveAssembly,
			FixtureTypeName,
			DirectCandidateGeneration,
			DirectReloadError) &&
		InvokeCaller(*this, CallerClass, First.Caller, First.Context, false));
	TestTrue(
		TEXT("The second World remains on v1 during the direct first-World transaction"),
		InvokeCaller(*this, CallerClass, Second.Caller, Second.Context, false));
	const FString Publication = TEXT(
		R"JSON({"schemaVersion":"unifiedscript.reload-transaction/v0.1","transactionId":"static-library-reload-selected","source":"selectedAsset","build":"fixture-v2","state":"published","fromGeneration":0,"candidateGeneration":0,"activeGeneration":0,"phases":[{"name":"artifactPublication","state":"published","durationMicroseconds":0,"detail":"fixture v2"}]})JSON");
	const FFinareReloadBatchResult Selected =
		SFinareAuthoringSurface::ReloadMatchingComponentsForAsset(Asset, FString(), Publication);
	TestEqual(TEXT("Selected static reload matches only two loaded Worlds"), Selected.Matched, 2);
	TestEqual(TEXT("Selected static reload publishes both loaded Worlds"), Selected.Reloaded, 2);
	TestEqual(TEXT("Selected static reload ignores the cold World"), Selected.Prepared, 2);
	ValidateStaticReloadReceipt(
		*this,
		Selected.ObservedReloadTransactionJson,
		TEXT("static-library-reload-selected"),
		TEXT("selectedAsset"),
		TEXT("fixture-v2"),
		2);
	TestTrue(TEXT("Two loaded Worlds execute v2 through the real Blueprint VM caller"),
	         InvokeCaller(*this, CallerClass, First.Caller, First.Context, true) &&
	         InvokeCaller(*this, CallerClass, Second.Caller, Second.Context, true));

	TestTrue(TEXT("Copies incompatible fixture into the active DLL path"), CopyFixture(TEXT("incompatible")));
	const FFinareReloadBatchResult Rejected =
		SFinareAuthoringSurface::ReloadMatchingComponentsForAsset(Asset, FString(), Publication);
	TestEqual(TEXT("Incompatible candidate matches both loaded Worlds"), Rejected.Matched, 2);
	TestEqual(TEXT("Incompatible candidate rejects both prepared Worlds"), Rejected.PrepareRejected, 2);
	TestEqual(TEXT("Incompatible candidate prepares no World"), Rejected.Prepared, 0);
	TestEqual(TEXT("Incompatible candidate commits no World"), Rejected.Committed, 0);
	TestEqual(TEXT("Incompatible candidate reports both failures"), Rejected.Failed, 2);
	TestTrue(TEXT("Rejected candidate leaves v2 active in both loaded Worlds"),
	         InvokeCaller(*this, CallerClass, First.Caller, First.Context, true) &&
	         InvokeCaller(*this, CallerClass, Second.Caller, Second.Context, true));

	UFinareAsset* DuplicateAsset = NewObject<UFinareAsset>(Package);
	DuplicateAsset->ScriptClassKind = EFinareClassKind::BlueprintFunctionLibrary;
	DuplicateAsset->ScriptAssemblyPath = ActiveAssembly;
	DuplicateAsset->ScriptTypeName = FixtureTypeName;
	TestTrue(TEXT("Copies fixture v1 into the active DLL path"), CopyFixture(TEXT("v1")));
	const FString DailyPublication = TEXT(
		R"JSON({"schemaVersion":"unifiedscript.reload-transaction/v0.1","transactionId":"static-library-reload-daily","source":"dailyBuild","build":"fixture-v1","state":"published","fromGeneration":0,"candidateGeneration":0,"activeGeneration":0,"phases":[{"name":"artifactPublication","state":"published","durationMicroseconds":0,"detail":"fixture v1"}]})JSON");
	const FFinareReloadBatchResult Daily =
		SFinareAuthoringSurface::ReloadAllScriptClassComponents(DailyPublication);
	TestEqual(TEXT("Daily static reload deduplicates duplicate ScriptAssets by exact path/type"), Daily.Matched, 2);
	TestEqual(TEXT("Daily static reload publishes the v1 body to both loaded Worlds"), Daily.Reloaded, 2);
	ValidateStaticReloadReceipt(
		*this,
		Daily.ObservedReloadTransactionJson,
		TEXT("static-library-reload-daily"),
		TEXT("dailyBuild"),
		TEXT("fixture-v1"),
		2);
	TestTrue(TEXT("Daily reloaded Worlds execute v1 through the real Blueprint VM caller"),
	         InvokeCaller(*this, CallerClass, First.Caller, First.Context, false) &&
	         InvokeCaller(*this, CallerClass, Second.Caller, Second.Context, false));

	DestroyWorldFixture(First);
	DestroyWorldFixture(Second);
	DestroyWorldFixture(Cold);
	TestEqual(
		TEXT("Static reload test restores the managed World count"),
		FFinareCoreClrHost::Get().GetManagedWorldCount(),
		InitialManagedWorldCount);
	return !HasAnyErrors();
}

#endif

#include "FinareAuthoringWorkbench.h"

#include "Editor.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "FinareActor.h"
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareClassBinding.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/UnrealType.h"

#include "Misc/AutomationTest.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareNativePublicationCoordinatorTest,
	"Finare.Unreal.Editor.Authoring.NativePublicationCoordinator",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativePublicationCoordinatorTest::RunTest(const FString& Parameters)
{
	(void)Parameters;
	const FString Token = TEXT("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
	const FString Response = FString::Printf(
		TEXT("{\"schemaVersion\":\"unifiedscript.native-publication-prepare/v0.1\","
			"\"transactionId\":\"tx-native\",\"previewToken\":\"%s\","
			"\"baselineRevision\":1,\"candidateRevision\":2,\"publishesRuntime\":true,"
			"\"historyEntryTransactionId\":\"tx-native\",\"historyDirection\":\"commit\","
			"\"scriptAssets\":{\"schemaVersion\":\"unifiedscript.script-assets/v0.1\",\"assets\":[]},"
			"\"bindingChanges\":[{\"assetPath\":\"Demo\",\"assetId\":\"us_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\","
			"\"before\":{\"kind\":\"Actor\",\"baseClassPath\":\"/Script/Engine.Actor\"},"
			"\"after\":{\"kind\":\"Actor\",\"baseClassPath\":\"/Script/Engine.Pawn\"}}],"
			"\"outputs\":[{\"path\":\"C:/Finare/native.bin\",\"baselineExists\":false,"
			"\"baselineDigest\":\"sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\","
			"\"candidateDigest\":\"sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\","
			"\"content\":\"\"}]}"),
		*Token);

	FFinareNativePublicationPrepareResult Prepare;
	FString Error;
	TestTrue(
		*FString::Printf(TEXT("Native prepare parser accepts exact token and empty output content: %s"), *Error),
		FFinareAuthoringWorkbench::ParseNativePublicationPrepareResponse(Response, Prepare, Error));
	TestEqual(TEXT("Native prepare parser preserves PreviewToken"), Prepare.PreviewToken, Token);
	TestEqual(TEXT("Native prepare parser preserves one owner"), Prepare.BindingChanges.Num(), 1);
	if (Prepare.Outputs.Num() == 1)
	{
		TestEqual(TEXT("Native prepare parser accepts zero-byte candidate"), Prepare.Outputs[0].Content.Num(), 0);
	}

	const FString InvalidResponse = Response.Replace(*Token, TEXT("not-a-token"), ESearchCase::CaseSensitive);
	FFinareNativePublicationPrepareResult InvalidPrepare;
	TestFalse(
		TEXT("Native prepare parser rejects a malformed PreviewToken"),
		FFinareAuthoringWorkbench::ParseNativePublicationPrepareResponse(
			InvalidResponse, InvalidPrepare, Error));

	const FString DeclarationPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectDir(),
		TEXT("../artifacts/m13-3c-4-class-composition/class-binding/class-binding-fixture.asset.json")));
	FString Declaration;
	if (!TestTrue(
		TEXT("Coordinator fixture declaration is available"),
		FFileHelper::LoadFileToString(Declaration, *DeclarationPath) && !Declaration.IsEmpty()))
	{
		return false;
	}

	const FString Name = TEXT("NativePublicationCoordinator_") +
		FGuid::NewGuid().ToString(EGuidFormats::Digits);
	UPackage* Package = CreatePackage(*(TEXT("/Game/Temp/") + Name));
	UFinareBlueprint* Blueprint = Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
		AFinareActor::StaticClass(),
		Package,
		FName(*Name),
		BPTYPE_Normal,
		UFinareBlueprint::StaticClass(),
		UFinareBlueprintGeneratedClass::StaticClass(),
		FName(TEXT("FinareNativePublicationCoordinator"))));
	UFinareAsset* ScriptAsset = Blueprint != nullptr
		                            ? NewObject<UFinareAsset>(Package, FName(TEXT("ScriptAsset")))
		                            : nullptr;
	if (!TestNotNull(TEXT("Coordinator creates a real managed Blueprint"), Blueprint) ||
		!TestNotNull(TEXT("Coordinator creates a real Script Asset"), ScriptAsset) ||
		!TestNotNull(
			TEXT("Coordinator Blueprint has generated class"),
			Blueprint != nullptr ? Blueprint->GeneratedClass.Get() : nullptr))
	{
		return false;
	}
	ScriptAsset->SetFlags(RF_Public | RF_Standalone);
	FGuid FixtureScriptClassId;
	FGuid::ParseExact(
		TEXT("f10de75eb5e030d64d93c0b857ed5160"),
		EGuidFormats::Digits,
		FixtureScriptClassId);
	ScriptAsset->ScriptClassId = FixtureScriptClassId;
	ScriptAsset->ScriptClassKind = EFinareClassKind::Actor;
	ScriptAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
	ScriptAsset->ScriptTypeName = TEXT("ClassBindingFixture");
	ScriptAsset->DeclarationAssetPath = TEXT("Game/ClassBindingFixture");
	Blueprint->ScriptAsset = ScriptAsset;

	const FString ExternalRoot = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectSavedDir(),
		TEXT("Finare/NativePublicationCoordinator_") + Name));
	const FString ExternalPath = FPaths::Combine(ExternalRoot, TEXT("candidate.bin"));
	const FString HistoryPath = FPaths::Combine(
		FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory"))),
		TEXT("11111111-2222-3333-4444-555555555555.json"));
	const FString MarkerPath = FPaths::Combine(
		FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory"))),
		TEXT("11111111-2222-3333-4444-555555555555.completed.json"));
	const FString ExistingExternalPath = FPaths::Combine(ExternalRoot, TEXT("existing.bin"));
	const FString CreatedExternalPath = FPaths::Combine(ExternalRoot, TEXT("created.bin"));
	const FString SecondHistoryPath = FPaths::Combine(
		FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory"))),
		TEXT("22222222-3333-4444-5555-666666666666.json"));
	const FString SecondMarkerPath = FPaths::Combine(
		FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory"))),
		TEXT("22222222-3333-4444-5555-666666666666.completed.json"));
	const FString UndoHistoryPath = FPaths::Combine(
		FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory"))),
		TEXT("44444444-5555-6666-7777-888888888888.json"));
	const FString UndoMarkerPath = UndoHistoryPath.Replace(
		TEXT(".json"), TEXT(".completed.json"), ESearchCase::CaseSensitive);
	const FString RedoHistoryPath = FPaths::Combine(
		FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory"))),
		TEXT("55555555-6666-7777-8888-999999999999.json"));
	const FString RedoMarkerPath = RedoHistoryPath.Replace(
		TEXT(".json"), TEXT(".completed.json"), ESearchCase::CaseSensitive);
	UPackage* ParentPackage = nullptr;
	UBlueprint* ParentBlueprint = nullptr;
	UPackage* ChildPackage = nullptr;
	UBlueprint* ChildBlueprint = nullptr;
	ON_SCOPE_EXIT
	{
		IFileManager::Get().Delete(*ExternalPath, false, true, true);
		IFileManager::Get().Delete(*ExistingExternalPath, false, true, true);
		IFileManager::Get().Delete(*CreatedExternalPath, false, true, true);
		IFileManager::Get().DeleteDirectory(*ExternalRoot, false, true);
		IFileManager::Get().Delete(*HistoryPath, false, true, true);
		IFileManager::Get().Delete(*MarkerPath, false, true, true);
		IFileManager::Get().Delete(*SecondHistoryPath, false, true, true);
		IFileManager::Get().Delete(*SecondMarkerPath, false, true, true);
		IFileManager::Get().Delete(*UndoHistoryPath, false, true, true);
		IFileManager::Get().Delete(*UndoMarkerPath, false, true, true);
		IFileManager::Get().Delete(*RedoHistoryPath, false, true, true);
		IFileManager::Get().Delete(*RedoMarkerPath, false, true, true);
		const FString PackageFilename = FPackageName::LongPackageNameToFilename(
			Package->GetName(), FPackageName::GetAssetPackageExtension());
		IFileManager::Get().Delete(*PackageFilename, false, true, true);
		if (ParentPackage != nullptr)
		{
			const FString ParentPackageFilename = FPackageName::LongPackageNameToFilename(
				ParentPackage->GetName(), FPackageName::GetAssetPackageExtension());
			IFileManager::Get().Delete(*ParentPackageFilename, false, true, true);
		}
		if (ChildPackage != nullptr)
		{
			const FString ChildPackageFilename = FPackageName::LongPackageNameToFilename(
				ChildPackage->GetName(), FPackageName::GetAssetPackageExtension());
			IFileManager::Get().Delete(*ChildPackageFilename, false, true, true);
		}
	};

	FFinareAuthoringWorkbench::FNativePublicationContext Context;
	Context.ScriptAsset = ScriptAsset;
	Context.Blueprint = Blueprint;
	Context.GeneratedClass = Blueprint->GeneratedClass;
	Context.ScriptAssetObjectPath = ScriptAsset->GetPathName();
	Context.ScriptClassId = ScriptAsset->ScriptClassId;
	Context.ScriptTypeName = ScriptAsset->ScriptTypeName;
	Context.GeneratedClassPath = ScriptAsset->GeneratedClassPath.ToString();
	Context.GeneratedOwnerPath = Blueprint->GetPathName();
	Context.NativeParentClassPath = Blueprint->ParentClass != nullptr
		                                ? Blueprint->ParentClass->GetPathName()
		                                : FString();
	const FString OriginalParentClassPath = Context.NativeParentClassPath;
	Context.bHasExpectedPreviewRevisions = true;
	Context.ExpectedBaselineRevision = 11;
	Context.ExpectedCandidateRevision = 12;
	const auto WithDeclarationParent = [](const FString& Source, const TOptional<FString>& ParentPath)
	{
		FString Result = Source;
		const FString OldField = TEXT("\"baseClassPath\": \"/Script/FinareRuntime.FinareActor\"");
		const FString NewField = ParentPath.IsSet()
			                         ? FString::Printf(TEXT("\"baseClassPath\": \"%s\""), *ParentPath.GetValue())
			                         : TEXT("\"baseClassPath\": null");
		Result.ReplaceInline(*OldField, *NewField, ESearchCase::CaseSensitive);
		return Result;
	};
	const FString OriginalDeclaration = WithDeclarationParent(
		Declaration,
		TOptional<FString>(OriginalParentClassPath));

	FFinareNativePublicationPrepareResult RealPrepare;
	RealPrepare.SchemaVersion = TEXT("unifiedscript.native-publication-prepare/v0.1");
	RealPrepare.TransactionId = TEXT("11111111-2222-3333-4444-555555555555");
	RealPrepare.PreviewToken = Token;
	RealPrepare.BaselineRevision = 11;
	RealPrepare.CandidateRevision = 12;
	RealPrepare.bPublishesRuntime = true;
	RealPrepare.HistoryEntryTransactionId = RealPrepare.TransactionId;
	RealPrepare.HistoryDirection = TEXT("commit");
	RealPrepare.ScriptAssetsJson = OriginalDeclaration;
	FFinareNativeBindingChange Binding;
	Binding.AssetPath = ScriptAsset->DeclarationAssetPath;
	Binding.AssetId = TEXT("us_f10de75eb5e030d64d93c0b857ed5160");
	FFinareClassBindingDeclaration BeforeBinding;
	BeforeBinding.Kind = TEXT("Actor");
	BeforeBinding.BaseClassPath = Context.NativeParentClassPath;
	FFinareClassBindingDeclaration AfterBinding = BeforeBinding;
	Binding.Before = BeforeBinding;
	Binding.After = AfterBinding;
	RealPrepare.BindingChanges.Add(Binding);
	FFinareNativePublicationOutput Output;
	Output.Path = ExternalPath;
	Output.bBaselineExists = false;
	Output.BaselineDigest = TEXT("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
	Output.CandidateDigest = Output.BaselineDigest;
	RealPrepare.Outputs.Add(MoveTemp(Output));

	FString ApplyError;
	FFinareNativePublicationPrepareResult PositiveRevisionMismatch = RealPrepare;
	PositiveRevisionMismatch.CandidateRevision = 13;
	TestFalse(
		TEXT("Coordinator rejects a positive but unaccepted candidate revision at the apply boundary"),
		FFinareAuthoringWorkbench::ApplyNativePublication(
			Context, PositiveRevisionMismatch, Token, ApplyError));
	TestTrue(
		TEXT("Positive revision mismatch identifies the accepted preview revisions"),
		ApplyError.Contains(TEXT("accepted preview")));
	FFinareNativePublicationPrepareResult InvalidIdentityPrepare = RealPrepare;
	InvalidIdentityPrepare.TransactionId = TEXT("11111111/invalid");
	TestFalse(
		TEXT("Coordinator rejects an invalid durable transaction identity before materialization"),
		FFinareAuthoringWorkbench::ApplyNativePublication(
			Context, InvalidIdentityPrepare, Token, ApplyError));
	TestTrue(
		TEXT("Invalid transaction identity reports a precise rejection"),
		ApplyError.Contains(TEXT("invalid transaction identity")));
	TestTrue(
		TEXT("Coordinator applies a prepared native publication through the real materializer"),
		FFinareAuthoringWorkbench::ApplyNativePublication(
			Context, RealPrepare, Token, ApplyError));
	TestTrue(
		TEXT("Coordinator writes the prepared external candidate"),
		IFileManager::Get().FileExists(*ExternalPath));
	TestTrue(
		TEXT("Coordinator resolves the exact durable completion marker"),
		FFinareAuthoringWorkbench::ValidateNativeCompletionMarker(
			Context, RealPrepare, ApplyError));

	FIntProperty* RootCountProperty = FindFProperty<FIntProperty>(
		Blueprint->GeneratedClass,
		TEXT("Count"));
	if (!TestNotNull(TEXT("Coordinator materializes the root managed Count property"), RootCountProperty))
	{
		return false;
	}
	RootCountProperty->SetPropertyValue_InContainer(
		Blueprint->GeneratedClass->GetDefaultObject(),
		41);
	ChildPackage = CreatePackage(*(TEXT("/Game/Temp/") + Name + TEXT("_Child")));
	ChildBlueprint = ChildPackage != nullptr
		                 ? FKismetEditorUtilities::CreateBlueprint(
			                 Blueprint->GeneratedClass,
			                 ChildPackage,
			                 FName(*(Name + TEXT("_Child"))),
			                 BPTYPE_Normal,
			                 UBlueprint::StaticClass(),
			                 UBlueprintGeneratedClass::StaticClass(),
			                 FName(TEXT("FinareNativePublicationCoordinator")))
		                 : nullptr;
	if (!TestNotNull(TEXT("Coordinator creates a real child Blueprint"), ChildBlueprint) ||
		!TestNotNull(
			TEXT("Coordinator child Blueprint has generated class"),
			ChildBlueprint != nullptr ? ChildBlueprint->GeneratedClass.Get() : nullptr))
	{
		return false;
	}
	FIntProperty* ChildCountProperty = FindFProperty<FIntProperty>(
		ChildBlueprint->GeneratedClass,
		TEXT("Count"));
	if (!TestNotNull(TEXT("Coordinator child retains the managed Count property"), ChildCountProperty))
	{
		return false;
	}
	ChildCountProperty->SetPropertyValue_InContainer(
		ChildBlueprint->GeneratedClass->GetDefaultObject(),
		47);
	TestEqual(
		TEXT("Coordinator captures distinct root and child defaults"),
		RootCountProperty->GetPropertyValue_InContainer(Blueprint->GeneratedClass->GetDefaultObject()),
		41);
	TestEqual(
		TEXT("Coordinator captures the child explicit default"),
		ChildCountProperty->GetPropertyValue_InContainer(ChildBlueprint->GeneratedClass->GetDefaultObject()),
		47);

	ParentPackage = CreatePackage(*(TEXT("/Game/Temp/") + Name + TEXT("_Parent")));
	ParentBlueprint = ParentPackage != nullptr
		                  ? FKismetEditorUtilities::CreateBlueprint(
			                  AFinareActor::StaticClass(),
			                  ParentPackage,
			                  FName(*(Name + TEXT("_Parent"))),
			                  BPTYPE_Normal,
			                  UBlueprint::StaticClass(),
			                  UBlueprintGeneratedClass::StaticClass(),
			                  FName(TEXT("FinareNativePublicationCoordinator")))
		                  : nullptr;
	if (!TestNotNull(TEXT("Coordinator creates a real prospective parent"), ParentBlueprint) ||
		!TestNotNull(
			TEXT("Prospective parent has a generated class"),
			ParentBlueprint != nullptr ? ParentBlueprint->GeneratedClass.Get() : nullptr))
	{
		return false;
	}
	IFileManager::Get().MakeDirectory(*ExternalRoot, true);
	TArray<uint8> ExistingBytes;
	ExistingBytes.Add(1);
	ExistingBytes.Add(2);
	TestTrue(
		TEXT("Coordinator creates a non-empty existing output baseline"),
		FFileHelper::SaveArrayToFile(ExistingBytes, *ExistingExternalPath));

	FFinareNativePublicationPrepareResult ParentPrepare = RealPrepare;
	ParentPrepare.TransactionId = TEXT("22222222-3333-4444-5555-666666666666");
	ParentPrepare.HistoryEntryTransactionId = ParentPrepare.TransactionId;
	ParentPrepare.BindingChanges.Reset();
	ParentPrepare.ScriptAssetsJson = WithDeclarationParent(
		Declaration,
		TOptional<FString>(ParentBlueprint->GeneratedClass->GetPathName()));
	FFinareClassBindingDeclaration ParentAfterBinding = BeforeBinding;
	ParentAfterBinding.BaseClassPath = ParentBlueprint->GeneratedClass->GetPathName();
	FFinareNativeBindingChange ParentBinding = Binding;
	ParentBinding.After = ParentAfterBinding;
	ParentPrepare.BindingChanges.Add(ParentBinding);
	ParentPrepare.Outputs.Reset();
	FFinareNativePublicationOutput ExistingOutput;
	ExistingOutput.Path = ExistingExternalPath;
	ExistingOutput.bBaselineExists = true;
	ExistingOutput.BaselineDigest = TEXT("sha256:a12871fee210fb8619291eaea194581cbd2531e4b23759d225f6806923f63222");
	ExistingOutput.Content.Add(9);
	ExistingOutput.Content.Add(8);
	ExistingOutput.Content.Add(7);
	ExistingOutput.CandidateDigest = TEXT("sha256:06df4f7e1394f1c57cc6583fba4d8060a5a66f4f4771c14aeff6b9af8a28c9b3");
	ParentPrepare.Outputs.Add(MoveTemp(ExistingOutput));
	FFinareNativePublicationOutput NewOutput;
	NewOutput.Path = CreatedExternalPath;
	NewOutput.bBaselineExists = false;
	NewOutput.BaselineDigest = TEXT("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
	NewOutput.Content.Add(9);
	NewOutput.Content.Add(8);
	NewOutput.Content.Add(7);
	NewOutput.CandidateDigest = TEXT("sha256:06df4f7e1394f1c57cc6583fba4d8060a5a66f4f4771c14aeff6b9af8a28c9b3");
	ParentPrepare.Outputs.Add(MoveTemp(NewOutput));

	FFinareNativePublicationPrepareResult InconsistentCandidate = ParentPrepare;
	InconsistentCandidate.TransactionId = TEXT("99999999-aaaa-bbbb-cccc-dddddddddddd");
	InconsistentCandidate.HistoryEntryTransactionId = InconsistentCandidate.TransactionId;
	InconsistentCandidate.ScriptAssetsJson = OriginalDeclaration;
	TestFalse(
		TEXT("Coordinator rejects a native binding candidate whose ScriptAssets parent disagrees"),
		FFinareAuthoringWorkbench::ApplyNativePublication(
			Context, InconsistentCandidate, Token, ApplyError));
	TestTrue(
		TEXT("Inconsistent candidate reports the split parent contract"),
		ApplyError.Contains(TEXT("disagree")));
	TestTrue(
		TEXT("Inconsistent candidate leaves the original parent untouched"),
		Blueprint->ParentClass != nullptr &&
		Blueprint->ParentClass->GetPathName() == OriginalParentClassPath);
	TArray<uint8> InconsistentExistingBytes;
	TestTrue(
		TEXT("Inconsistent candidate leaves external baseline untouched"),
		FFileHelper::LoadFileToArray(InconsistentExistingBytes, *ExistingExternalPath) &&
		InconsistentExistingBytes == ExistingBytes);

	const FString ManagedPackageFilename = FPackageName::LongPackageNameToFilename(
		Package->GetName(), FPackageName::GetAssetPackageExtension());
	const FString ChildPackageFilename = FPackageName::LongPackageNameToFilename(
		ChildPackage->GetName(), FPackageName::GetAssetPackageExtension());
	const FString ParentPackageFilename = FPackageName::LongPackageNameToFilename(
		ParentPackage->GetName(), FPackageName::GetAssetPackageExtension());
	const auto SaveCoordinatorPackage = [](UObject& TopLevelObject, const FString& Filename)
	{
		FSavePackageArgs SaveArgs;
		SaveArgs.TopLevelFlags = RF_Public | RF_Standalone;
		SaveArgs.SaveFlags = SAVE_NoError;
		return UPackage::SavePackage(
			TopLevelObject.GetOutermost(),
			&TopLevelObject,
			*Filename,
			SaveArgs);
	};
	TestTrue(
		TEXT("Coordinator saves the root package baseline before rollback"),
		SaveCoordinatorPackage(*Blueprint, ManagedPackageFilename));
	TestTrue(
		TEXT("Coordinator saves the child package baseline before rollback"),
		SaveCoordinatorPackage(*ChildBlueprint, ChildPackageFilename));
	TestTrue(
		TEXT("Coordinator saves the prospective parent package baseline before rollback"),
		SaveCoordinatorPackage(*ParentBlueprint, ParentPackageFilename));

	TArray<uint8> PackageBytesBeforeRollback;
	TArray<uint8> ChildPackageBytesBeforeRollback;
	TArray<uint8> ParentPackageBytesBeforeRollback;
	TestTrue(
		TEXT("Coordinator captures the root package before injected rollback"),
		FFileHelper::LoadFileToArray(PackageBytesBeforeRollback, *ManagedPackageFilename));
	TestTrue(
		TEXT("Coordinator captures the child package before injected rollback"),
		FFileHelper::LoadFileToArray(ChildPackageBytesBeforeRollback, *ChildPackageFilename));
	TestTrue(
		TEXT("Coordinator captures the prospective parent package before injected rollback"),
		FFileHelper::LoadFileToArray(ParentPackageBytesBeforeRollback, *ParentPackageFilename));
	FFinareAuthoringWorkbench::FNativePublicationContext RollbackContext = Context;
	RollbackContext.FailAfterExternalPublicationWriteForTesting = 2;
	FFinareNativePublicationPrepareResult RollbackPrepare = ParentPrepare;
	RollbackPrepare.TransactionId = TEXT("33333333-4444-5555-6666-777777777777");
	RollbackPrepare.HistoryEntryTransactionId = RollbackPrepare.TransactionId;
	FString RollbackError;
	TestFalse(
		TEXT("Coordinator rolls back after two external outputs were written"),
		FFinareAuthoringWorkbench::ApplyNativePublication(
			RollbackContext, RollbackPrepare, Token, RollbackError));
	TestTrue(
		TEXT("Coordinator reports the exact external-write failure seam"),
		RollbackError == TEXT("Injected external publication failure after 2 external file writes."));
	TestTrue(
		TEXT("Coordinator rollback restores the original parent"),
		Blueprint->ParentClass != nullptr &&
		Blueprint->ParentClass->GetPathName() == Context.NativeParentClassPath);
	TestTrue(
		TEXT("Coordinator rollback restores the root package bytes"),
		[&]()
		{
			TArray<uint8> CurrentBytes;
			return FFileHelper::LoadFileToArray(CurrentBytes, *ManagedPackageFilename) &&
				CurrentBytes == PackageBytesBeforeRollback;
		}());
	TestTrue(
		TEXT("Coordinator rollback restores the child package bytes"),
		[&]()
		{
			TArray<uint8> CurrentBytes;
			return FFileHelper::LoadFileToArray(CurrentBytes, *ChildPackageFilename) &&
				CurrentBytes == ChildPackageBytesBeforeRollback;
		}());
	TestTrue(
		TEXT("Coordinator rollback restores the prospective parent package bytes"),
		[&]()
		{
			TArray<uint8> CurrentBytes;
			return FFileHelper::LoadFileToArray(CurrentBytes, *ParentPackageFilename) &&
				CurrentBytes == ParentPackageBytesBeforeRollback;
		}());
	TArray<uint8> RollbackExistingBytes;
	TestTrue(
		TEXT("Coordinator rollback restores the non-empty existing output"),
		FFileHelper::LoadFileToArray(RollbackExistingBytes, *ExistingExternalPath) &&
		RollbackExistingBytes == ExistingBytes);
	TestFalse(
		TEXT("Coordinator rollback removes the created output"),
		IFileManager::Get().FileExists(*CreatedExternalPath));
	FIntProperty* RollbackRootCountProperty = FindFProperty<FIntProperty>(
		Blueprint->GeneratedClass,
		TEXT("Count"));
	FIntProperty* RollbackChildCountProperty = ChildBlueprint->GeneratedClass != nullptr
		                                           ? FindFProperty<FIntProperty>(
			                                           ChildBlueprint->GeneratedClass, TEXT("Count"))
		                                           : nullptr;
	TestTrue(
		TEXT("Coordinator rollback restores root and child defaults"),
		RollbackRootCountProperty != nullptr && RollbackChildCountProperty != nullptr &&
		RollbackRootCountProperty->GetPropertyValue_InContainer(
			Blueprint->GeneratedClass->GetDefaultObject()) == 41 &&
		RollbackChildCountProperty->GetPropertyValue_InContainer(
			ChildBlueprint->GeneratedClass->GetDefaultObject()) == 47);

	Context.GeneratedClass = Blueprint->GeneratedClass;
	Context.NativeParentClassPath = Blueprint->ParentClass != nullptr
		                                ? Blueprint->ParentClass->GetPathName()
		                                : FString();

	TestTrue(
		TEXT("Coordinator applies a real parent change with existing and new outputs"),
		FFinareAuthoringWorkbench::ApplyNativePublication(
			Context, ParentPrepare, Token, ApplyError));
	TestTrue(
		TEXT("Real parent change adopts the exact generated superclass"),
		Blueprint->ParentClass == ParentBlueprint->GeneratedClass &&
		ChildBlueprint->ParentClass == Blueprint->GeneratedClass);
	TArray<uint8> ExistingAfter;
	TArray<uint8> ExpectedExistingAfter;
	ExpectedExistingAfter.Add(9);
	ExpectedExistingAfter.Add(8);
	ExpectedExistingAfter.Add(7);
	TestTrue(
		TEXT("Real parent change publishes the non-empty existing output"),
		FFileHelper::LoadFileToArray(ExistingAfter, *ExistingExternalPath) &&
		ExistingAfter == ExpectedExistingAfter);
	TestTrue(
		TEXT("Real parent change creates the previously absent output"),
		IFileManager::Get().FileExists(*CreatedExternalPath));
	TestTrue(
		TEXT("Coordinator completion marker records the actual parent and generated super"),
		FFinareAuthoringWorkbench::ValidateNativeCompletionMarker(
			Context, ParentPrepare, ApplyError));

	FFinareAuthoringWorkbench::FNativePublicationContext FreshCommitContext = Context;
	FreshCommitContext.GeneratedClass = Blueprint->GeneratedClass;
	FreshCommitContext.NativeParentClassPath = ParentBlueprint->GeneratedClass->GetPathName();
	const bool bCompletedRetry = FFinareAuthoringWorkbench::ApplyNativePublication(
		FreshCommitContext, ParentPrepare, Token, ApplyError);
	if (bCompletedRetry)
	{
		AddError(TEXT("Completed native publication unexpectedly materialized again."));
	}
	else
	{
		AddInfo(FString::Printf(TEXT("Completed retry rejection: %s"), *ApplyError));
	}
	TestFalse(
		TEXT("Coordinator rejects rematerialization of a completed transaction with a fresh context"),
		bCompletedRetry);
	TestTrue(
		TEXT("Completed transaction rejection identifies its existing completion marker"),
		ApplyError.Contains(TEXT("completion marker")));

	FFinareNativePublicationPrepareResult UndoPrepare = ParentPrepare;
	UndoPrepare.TransactionId = TEXT("44444444-5555-6666-7777-888888888888");
	UndoPrepare.HistoryDirection = TEXT("undo");
	UndoPrepare.HistoryEntryTransactionId = ParentPrepare.TransactionId;
	// Undo publishes the old generated snapshot bytes.  It does not delete
	// the generated output that remains part of the declaration projection.
	UndoPrepare.ScriptAssetsJson = WithDeclarationParent(Declaration, TOptional<FString>());
	UndoPrepare.BindingChanges[0].Before->BaseClassPath = ParentBlueprint->GeneratedClass->GetPathName();
	UndoPrepare.BindingChanges[0].After->BaseClassPath.Reset();
	for (FFinareNativePublicationOutput& UndoOutput : UndoPrepare.Outputs)
	{
		UndoOutput.bBaselineExists = true;
		UndoOutput.BaselineDigest = TEXT("sha256:06df4f7e1394f1c57cc6583fba4d8060a5a66f4f4771c14aeff6b9af8a28c9b3");
		UndoOutput.Content.Reset();
		UndoOutput.Content.Add(1);
		UndoOutput.Content.Add(2);
		UndoOutput.CandidateDigest = TEXT("sha256:a12871fee210fb8619291eaea194581cbd2531e4b23759d225f6806923f63222");
	}
	Context.GeneratedClass = Blueprint->GeneratedClass;
	Context.NativeParentClassPath = ParentBlueprint->GeneratedClass->GetPathName();
	const bool bUndoApplied = FFinareAuthoringWorkbench::ApplyNativePublication(
		Context, UndoPrepare, Token, ApplyError);
	if (!bUndoApplied)
	{
		AddInfo(FString::Printf(TEXT("Undo rejection: %s"), *ApplyError));
	}
	TestTrue(TEXT("Coordinator performs Undo using the recorded implicit parent"), bUndoApplied);
	TestTrue(
		TEXT("Coordinator Undo restores the recorded parent"),
		Blueprint->ParentClass != nullptr &&
		Blueprint->ParentClass->GetPathName() == OriginalParentClassPath &&
		ChildBlueprint->ParentClass == Blueprint->GeneratedClass);
	TestTrue(
		TEXT("Coordinator Undo completion marker validates exact parent"),
		FFinareAuthoringWorkbench::ValidateNativeCompletionMarker(
			Context, UndoPrepare, ApplyError));
	TArray<uint8> ExistingAfterUndo;
	TestTrue(
		TEXT("Coordinator Undo publishes the old existing output bytes"),
		FFileHelper::LoadFileToArray(ExistingAfterUndo, *ExistingExternalPath) &&
		ExistingAfterUndo == ExistingBytes);
	TestTrue(
		TEXT("Coordinator Undo keeps the generated output artifact"),
		IFileManager::Get().FileExists(*CreatedExternalPath));

	Context.GeneratedClass = Blueprint->GeneratedClass;
	Context.NativeParentClassPath = OriginalParentClassPath;
	FFinareNativePublicationPrepareResult RedoPrepare = UndoPrepare;
	RedoPrepare.TransactionId = TEXT("55555555-6666-7777-8888-999999999999");
	RedoPrepare.HistoryDirection = TEXT("redo");
	RedoPrepare.ScriptAssetsJson = WithDeclarationParent(Declaration, TOptional<FString>());
	RedoPrepare.BindingChanges[0].Before->BaseClassPath = OriginalParentClassPath;
	RedoPrepare.BindingChanges[0].After->BaseClassPath.Reset();
	for (FFinareNativePublicationOutput& RedoOutput : RedoPrepare.Outputs)
	{
		RedoOutput.bBaselineExists = true;
		RedoOutput.BaselineDigest = TEXT("sha256:a12871fee210fb8619291eaea194581cbd2531e4b23759d225f6806923f63222");
		RedoOutput.Content.Reset();
		RedoOutput.Content.Add(9);
		RedoOutput.Content.Add(8);
		RedoOutput.Content.Add(7);
		RedoOutput.CandidateDigest = TEXT("sha256:06df4f7e1394f1c57cc6583fba4d8060a5a66f4f4771c14aeff6b9af8a28c9b3");
	}
	Context.GeneratedClass = Blueprint->GeneratedClass;
	const bool bRedoApplied = FFinareAuthoringWorkbench::ApplyNativePublication(
		Context, RedoPrepare, Token, ApplyError);
	if (!bRedoApplied)
	{
		AddInfo(FString::Printf(TEXT("Redo rejection: %s"), *ApplyError));
	}
	TestTrue(TEXT("Coordinator performs Redo using the recorded implicit parent"), bRedoApplied);
	TestTrue(
		TEXT("Coordinator Redo restores the recorded parent"),
		Blueprint->ParentClass != nullptr &&
		Blueprint->ParentClass->GetPathName() == ParentBlueprint->GeneratedClass->GetPathName());
	TestTrue(
		TEXT("Coordinator Redo completion marker validates exact parent"),
		FFinareAuthoringWorkbench::ValidateNativeCompletionMarker(
			Context, RedoPrepare, ApplyError));
	TArray<uint8> ExistingAfterRedo;
	TestTrue(
		TEXT("Coordinator Redo republishes the candidate existing output bytes"),
		FFileHelper::LoadFileToArray(ExistingAfterRedo, *ExistingExternalPath) &&
		ExistingAfterRedo == ExpectedExistingAfter);
	TestTrue(
		TEXT("Coordinator Redo keeps the generated output artifact"),
		IFileManager::Get().FileExists(*CreatedExternalPath));

	Context.NativeParentClassPath = ParentBlueprint->GeneratedClass->GetPathName();
	FFinareNativePublicationPrepareResult ConflictingRedoPrepare = RedoPrepare;
	ConflictingRedoPrepare.TransactionId = TEXT("77777777-8888-9999-aaaa-bbbbbbbbbbbb");
	ConflictingRedoPrepare.BindingChanges[0].Before->BaseClassPath =
		ParentBlueprint->GeneratedClass->GetPathName();
	ConflictingRedoPrepare.BindingChanges[0].After->BaseClassPath = OriginalParentClassPath;
	ConflictingRedoPrepare.ScriptAssetsJson = WithDeclarationParent(
		Declaration,
		TOptional<FString>(OriginalParentClassPath));
	TestFalse(
		TEXT("Coordinator rejects an explicit source parent that conflicts with durable Redo history"),
		FFinareAuthoringWorkbench::ApplyNativePublication(
			Context, ConflictingRedoPrepare, Token, ApplyError));
	TestTrue(
		TEXT("Conflicting source parent reports the durable history mismatch"),
		ApplyError.Contains(TEXT("conflicts with the durable redo parent")));

	Context.GeneratedClass = Blueprint->GeneratedClass;
	Context.NativeParentClassPath = ParentBlueprint->GeneratedClass->GetPathName();
	FFinareNativePublicationPrepareResult MissingHistoryPrepare = RedoPrepare;
	MissingHistoryPrepare.TransactionId = TEXT("66666666-7777-8888-9999-aaaaaaaaaaaa");
	MissingHistoryPrepare.HistoryEntryTransactionId = TEXT("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
	MissingHistoryPrepare.HistoryDirection = TEXT("undo");
	MissingHistoryPrepare.BindingChanges[0].Before->BaseClassPath =
		ParentBlueprint->GeneratedClass->GetPathName();
	MissingHistoryPrepare.BindingChanges[0].After->BaseClassPath = OriginalParentClassPath;
	MissingHistoryPrepare.ScriptAssetsJson = WithDeclarationParent(
		Declaration,
		TOptional<FString>(OriginalParentClassPath));
	TestFalse(
		TEXT("Coordinator rejects Undo when the native history sidecar is missing"),
		FFinareAuthoringWorkbench::ApplyNativePublication(
			Context, MissingHistoryPrepare, Token, ApplyError));
	TestTrue(
		TEXT("Missing native history reports a recoverable error"),
		ApplyError.Contains(TEXT("required for undo")));

	TestFalse(
		TEXT("Coordinator rejects a stale preview token before materialization"),
		FFinareAuthoringWorkbench::ApplyNativePublication(
			Context, RedoPrepare, TEXT("wrong-preview-token"), ApplyError));
	FFinareNativePublicationPrepareResult StaleRevisionPrepare = RedoPrepare;
	StaleRevisionPrepare.CandidateRevision = StaleRevisionPrepare.BaselineRevision;
	TestFalse(
		TEXT("Coordinator rejects a stale candidate revision before materialization"),
		FFinareAuthoringWorkbench::ApplyNativePublication(
			Context, StaleRevisionPrepare, Token, ApplyError));
	FFinareAuthoringWorkbench::FNativePublicationContext WrongOwnerContext = Context;
	WrongOwnerContext.GeneratedOwnerPath = TEXT("/Game/Temp/ForeignOwner.ForeignOwner");
	TestFalse(
		TEXT("Coordinator rejects a foreign generated owner before materialization"),
		FFinareAuthoringWorkbench::ApplyNativePublication(
			WrongOwnerContext, RedoPrepare, Token, ApplyError));

	const FString CapturedScriptTypeName = Context.ScriptTypeName;
	Context.ScriptTypeName = TEXT("Stale.Context");
	TestFalse(
		TEXT("Coordinator rejects a stale captured owner context before another materialization"),
		FFinareAuthoringWorkbench::ApplyNativePublication(
			Context, ParentPrepare, Token, ApplyError));
	Context.ScriptTypeName = CapturedScriptTypeName;
	return true;
}

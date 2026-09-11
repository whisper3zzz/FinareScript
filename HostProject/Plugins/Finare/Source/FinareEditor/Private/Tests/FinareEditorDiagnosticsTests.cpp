#include "Misc/AutomationTest.h"

#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "FinareAuthoringWorkbench.h"
#include "FinareEditorDiagnostics.h"
#include "FinareRuntimeDiagnostic.h"

#if WITH_DEV_AUTOMATION_TESTS

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareUnifiedEditorDiagnosticsContractTest,
	"Finare.Unreal.Diagnostics.UnifiedEditorContract",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareUnifiedEditorDiagnosticsContractTest::RunTest(const FString& Parameters)
{
	FFinareAuthoringErrorResult AuthoringFailure;
	AuthoringFailure.Code = TEXT("AUTHORING_REJECTED");
	AuthoringFailure.Message = TEXT("The transaction contains compiler errors.");
	FFinareAuthoringDiagnostic CompilerDiagnostic;
	CompilerDiagnostic.Code = TEXT("CS0103");
	CompilerDiagnostic.Severity = TEXT("error");
	CompilerDiagnostic.Message = TEXT("The name 'MissingTarget' does not exist.");
	CompilerDiagnostic.SemanticId = TEXT("us_method_tick");
	CompilerDiagnostic.GraphNodeId = TEXT("us_node_missing_target");
	CompilerDiagnostic.Span.FilePath = TEXT("Managed/FirstPlayActor.cs");
	CompilerDiagnostic.Span.StartLine = 17;
	CompilerDiagnostic.Span.StartColumn = 9;
	AuthoringFailure.Diagnostics.Add(CompilerDiagnostic);

	const TArray<FFinareEditorDiagnostic> Authoring =
		FFinareEditorDiagnostics::FromAuthoringFailure(AuthoringFailure);
	TestEqual(TEXT("Normalizes one compiler diagnostic"), Authoring.Num(), 1);
	if (Authoring.Num() == 1)
	{
		TestEqual(TEXT("Classifies compiler errors"), Authoring[0].Domain, FString(TEXT("compile")));
		TestEqual(TEXT("Preserves source line"), Authoring[0].Line, 17);
		TestEqual(TEXT("Preserves semantic member"), Authoring[0].Member, FString(TEXT("us_method_tick")));
		TestEqual(TEXT("Preserves Graph node"), Authoring[0].GraphNode, FString(TEXT("us_node_missing_target")));
		TestEqual(TEXT("Carries compiler stage"), Authoring[0].Stage, FString(TEXT("compile")));
		TestEqual(TEXT("Compiler repair action is retry"), Authoring[0].RecoveryAction, FString(TEXT("retry")));
	}

	FFinareReloadTransactionResult Reload;
	Reload.TransactionId = TEXT("us_tx_restart");
	Reload.State = TEXT("restartRequired");
	Reload.RequiredAction = TEXT("restartEditor");
	Reload.Failure = TEXT("The edit would discard one explicit state override.");
	const FFinareEditorDiagnostic ReloadDiagnostic =
		FFinareEditorDiagnostics::FromReloadTransaction(
			Reload,
			TEXT("Managed/FirstPlayActor.cs"));
	TestEqual(TEXT("Reload uses restart guidance"), ReloadDiagnostic.RecoveryAction, FString(TEXT("restartEditor")));
	TestEqual(TEXT("Reload is its own domain"), ReloadDiagnostic.Domain, FString(TEXT("reload")));

	FFinareRuntimeDiagnostic Runtime;
	Runtime.Kind = TEXT("managed-exception");
	Runtime.Operation = TEXT("Tick");
	Runtime.Message = TEXT("Gameplay update failed.");
	Runtime.Source.FilePath = TEXT("Managed/FirstPlayActor.cs");
	Runtime.Source.Line = 31;
	Runtime.Source.Column = 13;
	Runtime.Source.MethodName = TEXT("FirstPlayActor.Tick");
	Runtime.Bridge.MemberPath = TEXT("/Script/Engine.Actor:SetActorLocation");
	Runtime.Bridge.HandleError = TEXT("StaleHandle");
	Runtime.WorldHostId = 41;
	Runtime.InstanceId = 73;
	Runtime.Generation = 7;
	const FFinareEditorDiagnostic RuntimeDiagnostic =
		FFinareEditorDiagnostics::FromRuntime(Runtime);
	TestEqual(TEXT("Object lifetime failures are classified"), RuntimeDiagnostic.Domain, FString(TEXT("lifetime")));
	TestEqual(TEXT("Runtime operation becomes stage"), RuntimeDiagnostic.Stage, FString(TEXT("Tick")));
	TestEqual(TEXT("Runtime carries generated member path"), RuntimeDiagnostic.Member, Runtime.Bridge.MemberPath);
	TestEqual(TEXT("Runtime carries exact source"), RuntimeDiagnostic.Line, 31);
	TestEqual(TEXT("Runtime carries World identity"), RuntimeDiagnostic.WorldHostId, static_cast<uint64>(41));
	TestEqual(TEXT("Runtime carries instance identity"), RuntimeDiagnostic.InstanceId, static_cast<uint64>(73));
	TestEqual(TEXT("Runtime carries generation"), RuntimeDiagnostic.Generation, 7);

	const FString TestRoot = FPaths::Combine(
		FPaths::ProjectSavedDir(),
		TEXT("Automation/FinareEditorDiagnostics"),
		FGuid::NewGuid().ToString(EGuidFormats::Digits));
	const FString ReportPath = FPaths::Combine(TestRoot, TEXT("coverage.json"));
	IFileManager::Get().MakeDirectory(*TestRoot, true);
	const FString Report = TEXT(
		"{\"schemaVersion\":\"unifiedscript.unreal-binding-coverage/v0.2\","
		"\"phase\":\"binding-generation\",\"packagePath\":\"/Game/FirstPlayActor\","
		"\"worldHostId\":\"101\",\"generation\":9,"
		"\"diagnostics\":[{\"code\":\"USUE5101\",\"severity\":\"error\","
		"\"message\":\"Unsupported delegate shape.\","
		"\"graphNodeId\":\"us_node_delegate\",\"requiredAction\":\"repair\","
		"\"memberPath\":\"/Script/Test.Actor:OnChanged\","
		"\"location\":{\"filePath\":\"Managed/FirstPlayActor.cs\","
		"\"line\":22,\"column\":5}}]}");
	TestTrue(TEXT("Creates structured coverage report"), FFileHelper::SaveStringToFile(Report, *ReportPath));
	const TArray<FFinareEditorDiagnostic> ReportDiagnostics =
		FFinareEditorDiagnostics::ReadReport(ReportPath);
	TestEqual(TEXT("Reads one structured report diagnostic"), ReportDiagnostics.Num(), 1);
	if (ReportDiagnostics.Num() == 1)
	{
		TestEqual(TEXT("Classifies coverage report"), ReportDiagnostics[0].Domain, FString(TEXT("coverage")));
		TestEqual(TEXT("Carries exact coverage member"), ReportDiagnostics[0].Member,
		          FString(TEXT("/Script/Test.Actor:OnChanged")));
		TestEqual(TEXT("Inherits producer stage"), ReportDiagnostics[0].Stage, FString(TEXT("binding-generation")));
		TestEqual(TEXT("Carries Graph node identity"), ReportDiagnostics[0].GraphNode,
		          FString(TEXT("us_node_delegate")));
		TestEqual(TEXT("Inherits package identity"), ReportDiagnostics[0].PackagePath,
		          FString(TEXT("/Game/FirstPlayActor")));
		TestEqual(TEXT("Inherits World identity"), ReportDiagnostics[0].WorldHostId, static_cast<uint64>(101));
		TestEqual(TEXT("Inherits generation"), ReportDiagnostics[0].Generation, 9);
		TestEqual(TEXT("Coverage action is repair"), ReportDiagnostics[0].RecoveryAction, FString(TEXT("repair")));
		TestEqual(TEXT("Carries report evidence"), ReportDiagnostics[0].EvidencePath, ReportPath);
	}

	const FString Clipboard = ReloadDiagnostic.ToClipboardText();
	TestTrue(TEXT("Clipboard diagnostic carries action"), Clipboard.Contains(TEXT("Recovery: Restart Editor")));
	TestTrue(TEXT("Clipboard diagnostic carries source"), Clipboard.Contains(TEXT("FirstPlayActor.cs")));

	const FString RuntimeClipboard = RuntimeDiagnostic.ToClipboardText();
	TestTrue(TEXT("Clipboard diagnostic carries stage"), RuntimeClipboard.Contains(TEXT("lifetime/Tick")));
	TestTrue(TEXT("Clipboard diagnostic carries runtime context"),
	         RuntimeClipboard.Contains(TEXT("World=41 Instance=73 Generation=7")));

	FFinareEditorDiagnostic SnapshotDiagnostic = RuntimeDiagnostic;
	SnapshotDiagnostic.Severity = TEXT("info");
	FFinareEditorDiagnostics::Publish(
		FText::FromString(TEXT("Automation snapshot")),
		{SnapshotDiagnostic},
		false);
	FString SnapshotJson;
	TestTrue(TEXT("Unified Message Log publication persists one bundle input"),
	         FFileHelper::LoadFileToString(
		         SnapshotJson,
		         *FFinareEditorDiagnostics::GetSnapshotPath()));
	TestTrue(TEXT("Editor diagnostic snapshot uses its allowlisted schema"),
	         SnapshotJson.Contains(TEXT("unifiedscript.editor-diagnostics/v0.2")));
	TestTrue(TEXT("Editor diagnostic snapshot retains stage"),
	         SnapshotJson.Contains(TEXT("\"stage\": \"Tick\"")));
	TestTrue(TEXT("Editor diagnostic snapshot retains World identity"),
	         SnapshotJson.Contains(TEXT("\"worldHostId\": \"41\"")));
	TestTrue(TEXT("Editor diagnostic snapshot retains generation"),
	         SnapshotJson.Contains(TEXT("\"generation\": 7")));
	TestTrue(TEXT("Editor diagnostic snapshot retains recovery guidance"),
	         SnapshotJson.Contains(TEXT("\"action\": \"retry\"")));

	IFileManager::Get().DeleteDirectory(*TestRoot, false, true);
	return true;
}

#endif

#include "Misc/AutomationTest.h"

#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "UnifiedScriptAuthoringWorkbench.h"
#include "UnifiedScriptEditorDiagnostics.h"
#include "UnifiedScriptRuntimeDiagnostic.h"

#if WITH_DEV_AUTOMATION_TESTS

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptUnifiedEditorDiagnosticsContractTest,
    "UnifiedScript.Unreal.Diagnostics.UnifiedEditorContract",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptUnifiedEditorDiagnosticsContractTest::RunTest(const FString& Parameters)
{
    FUnifiedScriptAuthoringErrorResult AuthoringFailure;
    AuthoringFailure.Code = TEXT("AUTHORING_REJECTED");
    AuthoringFailure.Message = TEXT("The transaction contains compiler errors.");
    FUnifiedScriptAuthoringDiagnostic CompilerDiagnostic;
    CompilerDiagnostic.Code = TEXT("CS0103");
    CompilerDiagnostic.Severity = TEXT("error");
    CompilerDiagnostic.Message = TEXT("The name 'MissingTarget' does not exist.");
    CompilerDiagnostic.SemanticId = TEXT("us_method_tick");
    CompilerDiagnostic.Span.FilePath = TEXT("Managed/FirstPlayActor.cs");
    CompilerDiagnostic.Span.StartLine = 17;
    CompilerDiagnostic.Span.StartColumn = 9;
    AuthoringFailure.Diagnostics.Add(CompilerDiagnostic);

    const TArray<FUnifiedScriptEditorDiagnostic> Authoring =
        FUnifiedScriptEditorDiagnostics::FromAuthoringFailure(AuthoringFailure);
    TestEqual(TEXT("Normalizes one compiler diagnostic"), Authoring.Num(), 1);
    if (Authoring.Num() == 1)
    {
        TestEqual(TEXT("Classifies compiler errors"), Authoring[0].Domain, FString(TEXT("compile")));
        TestEqual(TEXT("Preserves source line"), Authoring[0].Line, 17);
        TestEqual(TEXT("Preserves semantic member"), Authoring[0].Member, FString(TEXT("us_method_tick")));
        TestEqual(TEXT("Compiler repair action is retry"), Authoring[0].RecoveryAction, FString(TEXT("retry")));
    }

    FUnifiedScriptReloadTransactionResult Reload;
    Reload.TransactionId = TEXT("us_tx_restart");
    Reload.State = TEXT("restartRequired");
    Reload.RequiredAction = TEXT("restartEditor");
    Reload.Failure = TEXT("The edit would discard one explicit state override.");
    const FUnifiedScriptEditorDiagnostic ReloadDiagnostic =
        FUnifiedScriptEditorDiagnostics::FromReloadTransaction(
            Reload,
            TEXT("Managed/FirstPlayActor.cs"));
    TestEqual(TEXT("Reload uses restart guidance"), ReloadDiagnostic.RecoveryAction, FString(TEXT("restartEditor")));
    TestEqual(TEXT("Reload is its own domain"), ReloadDiagnostic.Domain, FString(TEXT("reload")));

    FUnifiedScriptRuntimeDiagnostic Runtime;
    Runtime.Kind = TEXT("managed-exception");
    Runtime.Operation = TEXT("Tick");
    Runtime.Message = TEXT("Gameplay update failed.");
    Runtime.Source.FilePath = TEXT("Managed/FirstPlayActor.cs");
    Runtime.Source.Line = 31;
    Runtime.Source.Column = 13;
    Runtime.Source.MethodName = TEXT("FirstPlayActor.Tick");
    Runtime.Bridge.MemberPath = TEXT("/Script/Engine.Actor:SetActorLocation");
    const FUnifiedScriptEditorDiagnostic RuntimeDiagnostic =
        FUnifiedScriptEditorDiagnostics::FromRuntime(Runtime);
    TestEqual(TEXT("Runtime is classified"), RuntimeDiagnostic.Domain, FString(TEXT("runtime")));
    TestEqual(TEXT("Runtime carries generated member path"), RuntimeDiagnostic.Member, Runtime.Bridge.MemberPath);
    TestEqual(TEXT("Runtime carries exact source"), RuntimeDiagnostic.Line, 31);

    const FString TestRoot = FPaths::Combine(
        FPaths::ProjectSavedDir(),
        TEXT("Automation/UnifiedScriptEditorDiagnostics"),
        FGuid::NewGuid().ToString(EGuidFormats::Digits));
    const FString ReportPath = FPaths::Combine(TestRoot, TEXT("coverage.json"));
    IFileManager::Get().MakeDirectory(*TestRoot, true);
    const FString Report = TEXT(
        "{\"schemaVersion\":\"unifiedscript.unreal-binding-coverage/v0.2\","
        "\"diagnostics\":[{\"code\":\"USUE5101\",\"severity\":\"error\","
        "\"message\":\"Unsupported delegate shape.\","
        "\"memberPath\":\"/Script/Test.Actor:OnChanged\","
        "\"location\":{\"filePath\":\"Managed/FirstPlayActor.cs\","
        "\"line\":22,\"column\":5}}]}");
    TestTrue(TEXT("Creates structured coverage report"), FFileHelper::SaveStringToFile(Report, *ReportPath));
    const TArray<FUnifiedScriptEditorDiagnostic> ReportDiagnostics =
        FUnifiedScriptEditorDiagnostics::ReadReport(ReportPath);
    TestEqual(TEXT("Reads one structured report diagnostic"), ReportDiagnostics.Num(), 1);
    if (ReportDiagnostics.Num() == 1)
    {
        TestEqual(TEXT("Classifies coverage report"), ReportDiagnostics[0].Domain, FString(TEXT("coverage")));
        TestEqual(TEXT("Carries exact coverage member"), ReportDiagnostics[0].Member, FString(TEXT("/Script/Test.Actor:OnChanged")));
        TestEqual(TEXT("Coverage action is repair"), ReportDiagnostics[0].RecoveryAction, FString(TEXT("repair")));
        TestEqual(TEXT("Carries report evidence"), ReportDiagnostics[0].EvidencePath, ReportPath);
    }

    const FString Clipboard = ReloadDiagnostic.ToClipboardText();
    TestTrue(TEXT("Clipboard diagnostic carries action"), Clipboard.Contains(TEXT("Recovery: Restart Editor")));
    TestTrue(TEXT("Clipboard diagnostic carries source"), Clipboard.Contains(TEXT("FirstPlayActor.cs")));

    FUnifiedScriptEditorDiagnostic SnapshotDiagnostic = RuntimeDiagnostic;
    SnapshotDiagnostic.Severity = TEXT("info");
    FUnifiedScriptEditorDiagnostics::Publish(
        FText::FromString(TEXT("Automation snapshot")),
        {SnapshotDiagnostic},
        false);
    FString SnapshotJson;
    TestTrue(TEXT("Unified Message Log publication persists one bundle input"),
        FFileHelper::LoadFileToString(
            SnapshotJson,
            *FUnifiedScriptEditorDiagnostics::GetSnapshotPath()));
    TestTrue(TEXT("Editor diagnostic snapshot uses its allowlisted schema"),
        SnapshotJson.Contains(TEXT("unifiedscript.editor-diagnostics/v0.1")));
    TestTrue(TEXT("Editor diagnostic snapshot retains recovery guidance"),
        SnapshotJson.Contains(TEXT("\"action\": \"retry\"")));

    IFileManager::Get().DeleteDirectory(*TestRoot, false, true);
    return true;
}

#endif

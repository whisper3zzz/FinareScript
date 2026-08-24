#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptAuthoringSurface.h"
#include "UnifiedScriptAuthoringWorkbench.h"

#include "Framework/Docking/TabManager.h"
#include "Graph/UnifiedScriptGraphPresentation.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "UnifiedScriptEdGraph.h"
#include "Widgets/SUnifiedScriptAuthoringSurface.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptAuthoringSurfaceTest,
    "UnifiedScript.Unreal.Editor.AuthoringSurface",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptAuthoringSurfaceTest::RunTest(const FString& Parameters)
{
    FString SurfacePath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectDir(),
        TEXT(".."),
        TEXT("samples/Unreal/UnifiedScript.Unreal.Demo/UnifiedScript.Unreal.Demo.surface.json")));
    FPaths::NormalizeFilename(SurfacePath);

    FUnifiedScriptAuthoringSurfaceDocument Document;
    FString Error;
    const bool bLoaded = FUnifiedScriptAuthoringSurfaceReader::LoadFile(
        SurfacePath,
        Document,
        Error);
    TestTrue(
        *FString::Printf(TEXT("Checked-in Demo Surface loads: %s"), *Error),
        bLoaded);
    if (!bLoaded)
    {
        return false;
    }

    TestEqual(TEXT("Demo Surface has one Script Asset"), Document.Assets.Num(), 1);
    TestEqual(TEXT("Demo Surface has one state"), Document.GetStateCount(), 1);
    TestEqual(TEXT("Demo Surface has three Visual functions"), Document.GetFunctionCount(), 3);
    TestEqual(TEXT("Demo Surface preserves all Graph nodes"), Document.GetActionCount(), 38);
    TestEqual(TEXT("Demo Surface preserves all Graph edges"), Document.GetEdgeCount(), 37);
    TestEqual(
        TEXT("Demo Surface keeps the engine-neutral asset path"),
        Document.Assets[0].AssetPath,
        FString(TEXT("Unreal/Demo")));

    const FUnifiedScriptAuthoringFunction& Function = Document.Assets[0].Functions[0];
    TestEqual(
        TEXT("Presentation compacts leaf values without changing the Surface"),
        FUnifiedScriptGraphPresentation::CountVisibleActions(Function),
        7);
    TestEqual(
        TEXT("Presentation retains every edge between visible nodes"),
        FUnifiedScriptGraphPresentation::CountVisibleEdges(Function),
        7);
    const FUnifiedScriptAuthoringAction* Assignment = Function.Actions.FindByPredicate(
        [](const FUnifiedScriptAuthoringAction& Action)
        {
            return Action.GraphKind == TEXT("assignment");
        });
    TestNotNull(TEXT("Demo exposes an assignment presentation node"), Assignment);
    if (Assignment != nullptr)
    {
        TestEqual(TEXT("Assignment uses an author-facing title"), Assignment->Title, FString(TEXT("Set result")));
        const FUnifiedScriptAuthoringPort* AssignedValue = Assignment->FindPort(
            TEXT("assignedValue"),
            TEXT("input"));
        TestNotNull(TEXT("Assignment value pin is present"), AssignedValue);
        if (AssignedValue != nullptr)
        {
            TestEqual(
                TEXT("Literal source is folded into its consumer pin"),
                FUnifiedScriptGraphPresentation::GetPinLabel(*Assignment, *AssignedValue, Function),
                FString(TEXT("Value = 100")));
        }
    }
    const FUnifiedScriptAuthoringEdit* LiteralEdit = nullptr;
    for (const FUnifiedScriptAuthoringAction& Action : Function.Actions)
    {
        LiteralEdit = Action.EditCapabilities.FindByPredicate(
            [](const FUnifiedScriptAuthoringEdit& Edit)
            {
                return Edit.Kind == TEXT("literal");
            });
        if (LiteralEdit != nullptr)
        {
            break;
        }
    }
    TestNotNull(TEXT("Visual behavior exposes a typed literal edit"), LiteralEdit);
    if (LiteralEdit == nullptr)
    {
        return false;
    }

    FString PatchJson;
    TestTrue(
        TEXT("Slate edit can produce the shared semantic patch contract"),
        FUnifiedScriptAuthoringSurfaceReader::BuildSemanticPatch(
            Document,
            *LiteralEdit,
            TEXT("120"),
            PatchJson,
            Error));
    TestTrue(TEXT("Patch uses semantic-patch v0.21"),
        PatchJson.Contains(TEXT("\"unifiedscript.patch/v0.21\"")));
    TestTrue(TEXT("Patch retains the exact target node"),
        PatchJson.Contains(LiteralEdit->TargetNodeId));
    TestTrue(TEXT("Patch carries the requested replacement"),
        PatchJson.Contains(TEXT("\"newValueText\": \"120\"")));

    const FUnifiedScriptAuthoringFunction& RewireFunction = Document.Assets[0].Functions[1];
    TestEqual(
        TEXT("Demo publishes the positive graph round-trip function"),
        RewireFunction.Name,
        FString(TEXT("RewirePreview")));
    const FUnifiedScriptAuthoringAction* RewireSource = RewireFunction.Actions.FindByPredicate(
        [](const FUnifiedScriptAuthoringAction& Action)
        {
            return Action.GraphKind == TEXT("binary") && Action.Title == TEXT("*");
        });
    const FUnifiedScriptAuthoringAction* RewireTarget = RewireFunction.Actions.FindByPredicate(
        [](const FUnifiedScriptAuthoringAction& Action)
        {
            return Action.Title == TEXT("Declare incremented");
        });
    const FUnifiedScriptAuthoringPort* RewireSourcePort = RewireSource != nullptr
        ? RewireSource->FindPort(TEXT("value"), TEXT("output"))
        : nullptr;
    const FUnifiedScriptAuthoringEdge* RewireEdge = RewireTarget != nullptr
        ? RewireFunction.Edges.FindByPredicate(
            [RewireTarget](const FUnifiedScriptAuthoringEdge& Edge)
            {
                return Edge.Kind == TEXT("data") && Edge.ToNode == RewireTarget->Id &&
                    Edge.ToPort == TEXT("value");
            })
        : nullptr;
    TestNotNull(TEXT("Demo contains the visible pure '*' data source"), RewireSource);
    TestNotNull(TEXT("Demo contains the 'Declare incremented' rewire target"), RewireTarget);
    TestNotNull(TEXT("Demo target retains its compiler-owned old edge"), RewireEdge);
    if (RewireEdge != nullptr && RewireSource != nullptr && RewireSourcePort != nullptr)
    {
        FString RewireJson;
        TestTrue(
            TEXT("Slate drag can produce the shared data-edge rewire contract"),
            FUnifiedScriptAuthoringSurfaceReader::BuildDataEdgeReconnectionPatch(
                Document,
                RewireFunction,
                *RewireEdge,
                RewireSource->Id,
                RewireSourcePort->Name,
                RewireJson,
                Error));
        TestTrue(TEXT("Data-edge patch selects reconnectDataEdge"),
            RewireJson.Contains(TEXT("\"operation\": \"reconnectDataEdge\"")));
        TestTrue(TEXT("Data-edge patch preserves the old edge precondition"),
            RewireJson.Contains(RewireEdge->Id));
        TestTrue(TEXT("Data-edge patch selects the new source endpoint"),
            RewireJson.Contains(RewireSource->Id));

        FString DisconnectJson;
        TestTrue(
            TEXT("Alt-click can produce a typed default disconnection patch"),
            FUnifiedScriptAuthoringSurfaceReader::BuildDataEdgeDisconnectionPatch(
                Document, RewireFunction, *RewireEdge, DisconnectJson, Error));
        TestTrue(TEXT("Disconnection uses its exact edge precondition"),
            DisconnectJson.Contains(TEXT("\"operation\": \"disconnectDataEdge\"")) &&
                DisconnectJson.Contains(RewireEdge->Id));

        FString SharedPlanJson;
        TestTrue(
            TEXT("Computed values can produce an atomic automatic-temporary patch plan"),
            FUnifiedScriptAuthoringSurfaceReader::BuildSharedDataEdgePatchPlan(
                Document,
                RewireFunction,
                *RewireEdge,
                RewireSource->Id,
                RewireSourcePort->Name,
                SharedPlanJson,
                Error));
        TestTrue(TEXT("Shared plan introduces and reconnects one produced local"),
            SharedPlanJson.Contains(TEXT("\"step\": \"introduceTemporary\"")) &&
                SharedPlanJson.Contains(TEXT("\"step\": \"reconnectValue\"")));
    }

    const FUnifiedScriptAuthoringRegion* RewireBlock = RewireFunction.Regions.FindByPredicate(
        [](const FUnifiedScriptAuthoringRegion& Region)
        {
            return Region.Kind == TEXT("block");
        });
    TestNotNull(TEXT("Surface v0.3 exposes the real root block"), RewireBlock);
    if (RewireBlock != nullptr && RewireTarget != nullptr)
    {
        FString InsertJson;
        TestTrue(TEXT("Context menu can build a structured branch insertion"),
            FUnifiedScriptAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
                Document, RewireFunction, *RewireBlock, TEXT("flow.branch"), InsertJson, Error));
        TestTrue(TEXT("Branch insertion targets the real HIR block"),
            InsertJson.Contains(RewireBlock->SourceNodeId) &&
                InsertJson.Contains(TEXT("\"statement\": \"if\"")));

        FString DeleteJson;
        TestTrue(TEXT("Delete key can build an exact statement deletion"),
            FUnifiedScriptAuthoringSurfaceReader::BuildStatementDeletionPatch(
                Document, RewireFunction, *RewireTarget, DeleteJson, Error));
        TestTrue(TEXT("Deletion retains parent-block and sibling preconditions"),
            DeleteJson.Contains(RewireBlock->SourceNodeId) &&
                DeleteJson.Contains(TEXT("expectedPreviousSiblingId")));
    }

    const FUnifiedScriptAuthoringFunction& ApiFunction = Document.Assets[0].Functions[2];
    const FUnifiedScriptAuthoringRegion* ApiBlock = ApiFunction.Regions.FindByPredicate(
        [](const FUnifiedScriptAuthoringRegion& Region)
        {
            return Region.Kind == TEXT("block");
        });
    const FUnifiedScriptAuthoringParameter* ComponentParameter =
        ApiFunction.Parameters.FindByPredicate([](const FUnifiedScriptAuthoringParameter& Parameter)
        {
            return Parameter.Name == TEXT("component");
        });
    const FUnifiedScriptAuthoringParameter* LeftParameter =
        ApiFunction.Parameters.FindByPredicate([](const FUnifiedScriptAuthoringParameter& Parameter)
        {
            return Parameter.Name == TEXT("left");
        });
    const FUnifiedScriptAuthoringParameter* RightParameter =
        ApiFunction.Parameters.FindByPredicate([](const FUnifiedScriptAuthoringParameter& Parameter)
        {
            return Parameter.Name == TEXT("right");
        });
    TestNotNull(TEXT("Reflection demo exposes a typed Unreal receiver"), ComponentParameter);
    if (ApiBlock != nullptr && ComponentParameter != nullptr && LeftParameter != nullptr &&
        RightParameter != nullptr)
    {
        FString AddExpressionJson;
        TestTrue(TEXT("Math palette can build a structured binary-expression local"),
            FUnifiedScriptAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
                Document, ApiFunction, *ApiBlock, TEXT("value.add"), AddExpressionJson, Error));
        TestTrue(TEXT("Add Expression uses the two typed function parameters"),
            AddExpressionJson.Contains(TEXT("\"expression\": \"binary\"")) &&
                AddExpressionJson.Contains(LeftParameter->Id) &&
                AddExpressionJson.Contains(RightParameter->Id));

        FString ApiPatchJson;
        TestTrue(TEXT("Reflection palette can build a structured Add call"),
            FUnifiedScriptAuthoringSurfaceReader::BuildMemberInvocationInsertionPatch(
                Document,
                ApiFunction,
                *ApiBlock,
                *ComponentParameter,
                TEXT("Add"),
                TEXT("int"),
                LeftParameter->TypeId,
                {*LeftParameter, *RightParameter},
                ApiPatchJson,
                Error));
        TestTrue(TEXT("Reflected call patch remains structured rather than raw C#"),
            ApiPatchJson.Contains(TEXT("\"expression\": \"memberInvocation\"")) &&
                ApiPatchJson.Contains(TEXT("\"memberName\": \"Add\"")));

        FString PropertyPatchJson;
        TestTrue(TEXT("Reflection palette can build a structured property read"),
            FUnifiedScriptAuthoringSurfaceReader::BuildMemberAccessInsertionPatch(
                Document,
                ApiFunction,
                *ApiBlock,
                *ComponentParameter,
                TEXT("MaterializedStateCount"),
                TEXT("int"),
                LeftParameter->TypeId,
                PropertyPatchJson,
                Error));
        TestTrue(TEXT("Reflected property patch remains structured rather than raw C#"),
            PropertyPatchJson.Contains(TEXT("\"expression\": \"memberAccess\"")) &&
                PropertyPatchJson.Contains(TEXT("\"memberName\": \"MaterializedStateCount\"")));
    }

    UUnifiedScriptEdGraph* TransactionGraph = NewObject<UUnifiedScriptEdGraph>(
        GetTransientPackage(),
        NAME_None,
        RF_Transient);
    TransactionGraph->Schema = UUnifiedScriptEdGraphSchema::StaticClass();
    UUnifiedScriptEdGraphNode* OldSourceNode = NewObject<UUnifiedScriptEdGraphNode>(TransactionGraph);
    UUnifiedScriptEdGraphNode* NewSourceNode = NewObject<UUnifiedScriptEdGraphNode>(TransactionGraph);
    UUnifiedScriptEdGraphNode* TargetNode = NewObject<UUnifiedScriptEdGraphNode>(TransactionGraph);
    OldSourceNode->CreateNewGuid();
    NewSourceNode->CreateNewGuid();
    TargetNode->CreateNewGuid();
    TransactionGraph->AddNode(OldSourceNode, false, false);
    TransactionGraph->AddNode(NewSourceNode, false, false);
    TransactionGraph->AddNode(TargetNode, false, false);
    FEdGraphPinType DataPinType;
    DataPinType.PinCategory = TEXT("data");
    DataPinType.PinSubCategory = FName(TEXT("System.Int32"));
    UEdGraphPin* OldOutput = OldSourceNode->CreatePin(
        EGPD_Output, DataPinType, FName(TEXT("result")));
    UEdGraphPin* NewOutput = NewSourceNode->CreatePin(
        EGPD_Output, DataPinType, FName(TEXT("result")));
    UEdGraphPin* TargetInput = TargetNode->CreatePin(
        EGPD_Input, DataPinType, FName(TEXT("value")));
    OldOutput->MakeLinkTo(TargetInput, false);
    bool bRewireSubmitted = false;
    TransactionGraph->OnDataRewireRequested =
        FOnUnifiedScriptDataRewireRequested::CreateLambda(
            [&bRewireSubmitted, NewOutput, TargetInput](
                const UEdGraphPin* SubmittedOutput,
                const UEdGraphPin* SubmittedInput)
            {
                bRewireSubmitted = SubmittedOutput == NewOutput && SubmittedInput == TargetInput;
            });
    const UUnifiedScriptEdGraphSchema* RewireSchema =
        GetDefault<UUnifiedScriptEdGraphSchema>();
    TestEqual(
        TEXT("Compatible occupied data input advertises a rewire"),
        RewireSchema->CanCreateConnection(NewOutput, TargetInput).Response,
        CONNECT_RESPONSE_MAKE);
    TestTrue(
        TEXT("Schema forwards compatible drag as a Workbench transaction"),
        RewireSchema->TryCreateConnection(NewOutput, TargetInput));
    TestTrue(TEXT("Graph transaction callback receives the endpoints"), bRewireSubmitted);
    TestTrue(TEXT("Old compiler-owned wire remains until the server commits"),
        TargetInput->LinkedTo.Contains(OldOutput));
    TestFalse(TEXT("New wire is not shown before the server commits"),
        TargetInput->LinkedTo.Contains(NewOutput));
    RewireSchema->BreakPinLinks(*TargetInput, true);
    TestTrue(TEXT("Delete-only pin gestures cannot mutate compiler-owned wires"),
        TargetInput->LinkedTo.Contains(OldOutput));

    TestEqual(
        TEXT("Workbench endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildSemanticPatchUrl(
            TEXT("  http://127.0.0.1:4173///  ")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/semantic-patch")));
    TestEqual(
        TEXT("Workbench patch-plan endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildSemanticPatchPlanUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/semantic-patch-plan")));

    FString SurfaceJson;
    TestTrue(TEXT("Demo Surface source is readable"),
        FFileHelper::LoadFileToString(SurfaceJson, *SurfacePath));
    SurfaceJson.ReplaceInline(
        TEXT("unifiedscript.authoring-surface/v0.3"),
        TEXT("unifiedscript.authoring-surface/v9.9"));
    FUnifiedScriptAuthoringSurfaceDocument FailedOutput = Document;
    TestFalse(
        TEXT("Unknown Authoring Surface schema is rejected"),
        FUnifiedScriptAuthoringSurfaceReader::ParseJson(
            SurfaceJson,
            FailedOutput,
            Error));
    TestEqual(
        TEXT("Failed parse leaves the previous document unchanged"),
        FailedOutput.GetActionCount(),
        38);

    const FString WorkspaceJson = FString::Printf(
        TEXT("{\"schemaVersion\":\"unifiedscript.authoring-workspace/v0.2\","
             "\"revision\":2,\"sourcePath\":\"DemoScript.cs\","
             "\"surface\":%s,\"generation\":{\"number\":3}}"),
        *SurfaceJson.Replace(
            TEXT("unifiedscript.authoring-surface/v9.9"),
            TEXT("unifiedscript.authoring-surface/v0.3")));
    FUnifiedScriptAuthoringWorkspaceResult Workspace;
    TestTrue(
        *FString::Printf(TEXT("Workbench response carries a strict Surface: %s"), *Error),
        FUnifiedScriptAuthoringWorkbench::ParseWorkspaceResponse(
            WorkspaceJson,
            Workspace,
            Error));
    TestEqual(TEXT("Workbench response revision is retained"), Workspace.Revision, int64(2));
    TestEqual(TEXT("Workbench response generation is retained"), Workspace.Generation, int64(3));
    TestEqual(TEXT("Workbench response replaces the Surface transactionally"),
        Workspace.Surface.GetActionCount(), 38);
    TestTrue(
        TEXT("Workbench error responses retain protocol diagnostics"),
        FUnifiedScriptAuthoringWorkbench::ParseErrorResponse(
            TEXT("{\"error\":{\"code\":\"UAW1002\",\"message\":\"conflict\"}}"),
            409).Contains(TEXT("UAW1002")));

    TestTrue(
        TEXT("Editor module registers the Authoring Surface tab"),
        FGlobalTabmanager::Get()->HasTabSpawner(TEXT("UnifiedScriptAuthoringSurface")));
    const TSharedRef<SUnifiedScriptAuthoringSurface> Widget =
        SNew(SUnifiedScriptAuthoringSurface).InitialSurfacePath(SurfacePath);
    TestTrue(TEXT("Native Slate workspace loads the checked-in Surface"),
        Widget->LoadSurface(SurfacePath));
    TestEqual(
        TEXT("Blueprint-style graph uses the compact presentation projection"),
        Widget->GetVisibleGraphNodeCount(),
        FUnifiedScriptGraphPresentation::CountVisibleActions(Function));
    TestEqual(
        TEXT("Blueprint-style graph materializes every visible compiler-owned wire"),
        Widget->GetVisibleGraphEdgeCount(),
        FUnifiedScriptGraphPresentation::CountVisibleEdges(Function));
    TestEqual(
        TEXT("My Script navigation activates the Demo visual function"),
        Widget->GetActiveFunctionId(),
        Function.Id);
    return true;
}

#endif

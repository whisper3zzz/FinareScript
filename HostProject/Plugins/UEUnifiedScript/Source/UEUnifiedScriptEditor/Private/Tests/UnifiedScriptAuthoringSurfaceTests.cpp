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
    TestEqual(TEXT("Demo Surface has four Visual functions"), Document.GetFunctionCount(), 4);
    TestEqual(TEXT("Demo Surface preserves all Graph nodes"), Document.GetActionCount(), 45);
    TestEqual(TEXT("Demo Surface preserves all Graph edges"), Document.GetEdgeCount(), 43);
    TestEqual(
        TEXT("Demo Surface keeps the engine-neutral asset path"),
        Document.Assets[0].AssetPath,
        FString(TEXT("Unreal/Demo")));

    const FUnifiedScriptAuthoringFunction* FunctionPtr =
        Document.Assets[0].Functions.FindByPredicate([](
            const FUnifiedScriptAuthoringFunction& Candidate)
        {
            return Candidate.Name == TEXT("ResolveInitialValue");
        });
    const FUnifiedScriptAuthoringFunction* EventFunction =
        Document.Assets[0].Functions.FindByPredicate([](
            const FUnifiedScriptAuthoringFunction& Candidate)
        {
            return Candidate.Name == TEXT("OnManagedSignal");
        });
    TestNotNull(TEXT("Demo retains the scalar Visual function"), FunctionPtr);
    TestNotNull(TEXT("Demo projects the attributed UE Event Entry"), EventFunction);
    if (FunctionPtr == nullptr || EventFunction == nullptr)
    {
        return false;
    }
    const FUnifiedScriptAuthoringFunction& Function = *FunctionPtr;
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
    const FUnifiedScriptAuthoringAction* EventEntry = EventFunction->Actions.FindByPredicate(
        [](const FUnifiedScriptAuthoringAction& Action)
        {
            return Action.GraphKind == TEXT("unrealEventEntry");
        });
    TestNotNull(TEXT("Attributed method has a distinct Event Entry graph node"), EventEntry);
    if (EventEntry != nullptr)
    {
        TestEqual(TEXT("Event Entry uses a Blueprint-style title"),
            EventEntry->Title, FString(TEXT("Event OnManagedSignal")));
        TestEqual(TEXT("Event Entry exposes two strongly typed payload pins"),
            EventEntry->Ports.Num(), 3);
        const FUnifiedScriptAuthoringPort* ValuePin = EventEntry->FindPort(
            TEXT("eventArg0"), TEXT("output"));
        const FUnifiedScriptAuthoringPort* ContextPin = EventEntry->FindPort(
            TEXT("eventArg1"), TEXT("output"));
        TestTrue(TEXT("Event payload pins retain int and UnrealObjectHandle types"),
            ValuePin != nullptr && ValuePin->TypeDisplayName == TEXT("int") &&
                ContextPin != nullptr &&
                ContextPin->TypeDisplayName == TEXT("UnrealObjectHandle"));
    }
    FString EventPatchJson;
    TestTrue(TEXT("Event palette can build a declaration-level Event Entry patch"),
        FUnifiedScriptAuthoringSurfaceReader::BuildUnrealEventHandlerInsertionPatch(
            Document,
            Document.Assets[0],
            TEXT("OnManagedSignalVisual2"),
            TEXT("global::UnifiedScript.Unreal.Generated.UnifiedScriptComponent"),
            TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnManagedSignal"),
            EventFunction->Parameters,
            EventPatchJson,
            Error));
    TestTrue(TEXT("Event Entry patch uses declaration v0.2 and the canonical event path"),
        EventPatchJson.Contains(TEXT("unifiedscript.declaration-patch/v0.2")) &&
            EventPatchJson.Contains(TEXT("addUnrealEventHandler")) &&
            EventPatchJson.Contains(
                TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnManagedSignal")));
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
    TestTrue(TEXT("Patch uses semantic-patch v0.24"),
        PatchJson.Contains(TEXT("\"unifiedscript.patch/v0.24\"")));
    TestTrue(TEXT("Patch retains the exact target node"),
        PatchJson.Contains(LiteralEdit->TargetNodeId));
    TestTrue(TEXT("Patch carries the requested replacement"),
        PatchJson.Contains(TEXT("\"newValueText\": \"120\"")));

    const FUnifiedScriptAuthoringRegion* ResolveRootBlock = Function.Regions.FindByPredicate(
        [](const FUnifiedScriptAuthoringRegion& Region)
        {
            return Region.Kind == TEXT("block");
        });
    const FUnifiedScriptAuthoringRegion* ResolveThenBlock = Function.Regions.FindByPredicate(
        [](const FUnifiedScriptAuthoringRegion& Region)
        {
            return Region.Kind == TEXT("then");
        });
    const FUnifiedScriptAuthoringAction* ResolveRootSource =
        ResolveRootBlock != nullptr && !ResolveRootBlock->DirectStatementIds.IsEmpty()
        ? Function.FindAction(ResolveRootBlock->DirectStatementIds[0])
        : nullptr;
    const FUnifiedScriptAuthoringAction* ResolveThenTarget =
        ResolveThenBlock != nullptr && !ResolveThenBlock->DirectStatementIds.IsEmpty()
        ? Function.FindAction(ResolveThenBlock->DirectStatementIds[0])
        : nullptr;
    const FUnifiedScriptAuthoringEdge* ResolveThenIncoming = ResolveThenTarget != nullptr
        ? Function.Edges.FindByPredicate(
            [ResolveThenTarget](const FUnifiedScriptAuthoringEdge& Edge)
            {
                return Edge.Kind == TEXT("control") &&
                    Edge.ToNode == ResolveThenTarget->Id;
            })
        : nullptr;
    TestNotNull(TEXT("Cross-region fixture exposes the root and then blocks"),
        ResolveRootBlock != nullptr && ResolveThenBlock != nullptr ? ResolveRootBlock : nullptr);
    TestNotNull(TEXT("Cross-region fixture exposes an ordinary then statement"),
        ResolveThenTarget);
    TestNotNull(TEXT("Cross-region fixture exposes a root predecessor"), ResolveRootSource);
    TestNotNull(TEXT("Cross-region fixture exposes the occupied then input"), ResolveThenIncoming);
    if (ResolveRootBlock != nullptr && ResolveThenBlock != nullptr &&
        ResolveRootSource != nullptr && ResolveThenTarget != nullptr &&
        ResolveThenIncoming != nullptr)
    {
        FString CrossRegionMoveJson;
        TestTrue(TEXT("Control drag can move an ordinary statement across structured regions"),
            FUnifiedScriptAuthoringSurfaceReader::BuildControlEdgeReconnectionPatch(
                Document,
                Function,
                *ResolveThenIncoming,
                ResolveRootSource->Id,
                TEXT("then"),
                CrossRegionMoveJson,
                Error));
        TestTrue(TEXT("Cross-region move keeps distinct source and destination block identities"),
            CrossRegionMoveJson.Contains(ResolveThenBlock->SourceNodeId) &&
                CrossRegionMoveJson.Contains(ResolveRootBlock->SourceNodeId) &&
                !ResolveThenTarget->DebugTargetId.IsEmpty() &&
                CrossRegionMoveJson.Contains(ResolveThenTarget->DebugTargetId) &&
                CrossRegionMoveJson.Contains(
                    FString::Printf(
                        TEXT("\"destinationParentBlockId\": \"%s\""),
                        *ResolveRootBlock->SourceNodeId)));
    }

    const FUnifiedScriptAuthoringFunction& RewireFunction =
        *Document.Assets[0].Functions.FindByPredicate([](
            const FUnifiedScriptAuthoringFunction& Candidate)
        {
            return Candidate.Name == TEXT("RewirePreview");
        });
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
        const FString ExpectedTemporaryName =
            TEXT("sharedValue_") + RewireSource->Id.Mid(3).ToLower();
        TestTrue(TEXT("Automatic temporary names retain the complete semantic identity"),
            SharedPlanJson.Contains(ExpectedTemporaryName));
        FString RepeatedSharedPlanJson;
        TestTrue(
            TEXT("The same shared-value gesture can be rebuilt deterministically"),
            FUnifiedScriptAuthoringSurfaceReader::BuildSharedDataEdgePatchPlan(
                Document,
                RewireFunction,
                *RewireEdge,
                RewireSource->Id,
                RewireSourcePort->Name,
                RepeatedSharedPlanJson,
                Error));
        TestEqual(TEXT("Automatic temporary patch plans are byte-stable"),
            RepeatedSharedPlanJson, SharedPlanJson);
    }

    const FUnifiedScriptAuthoringRegion* RewireBlock = RewireFunction.Regions.FindByPredicate(
        [](const FUnifiedScriptAuthoringRegion& Region)
        {
            return Region.Kind == TEXT("block");
        });
    TestNotNull(TEXT("Surface v0.4 exposes the real root block"), RewireBlock);
    if (RewireBlock != nullptr && RewireTarget != nullptr && RewireSource != nullptr)
    {
        FString InsertJson;
        TestTrue(TEXT("Context menu can build a structured branch insertion"),
            FUnifiedScriptAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
                Document, RewireFunction, *RewireBlock, TEXT("flow.branch"), InsertJson, Error));
        TestTrue(TEXT("Branch insertion targets the real HIR block"),
            InsertJson.Contains(RewireBlock->SourceNodeId) &&
                InsertJson.Contains(TEXT("\"statement\": \"if\"")));

        FString DoWhileJson;
        TestTrue(TEXT("Context menu can build a structured do/while insertion"),
            FUnifiedScriptAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
                Document,
                RewireFunction,
                *RewireBlock,
                TEXT("flow.doWhile"),
                DoWhileJson,
                Error));
        TestTrue(TEXT("Do/while insertion uses the typed structured payload"),
            DoWhileJson.Contains(TEXT("\"statement\": \"doWhile\"")));

        FString ForJson;
        TestTrue(TEXT("Context menu can build a bounded structured for insertion"),
            FUnifiedScriptAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
                Document,
                RewireFunction,
                *RewireBlock,
                TEXT("flow.for"),
                ForJson,
                Error));
        TestTrue(TEXT("For insertion keeps declaration, condition, and incrementor"),
            ForJson.Contains(TEXT("\"statement\": \"for\"")) &&
                ForJson.Contains(TEXT("\"expression\": \"forLocalReference\"")) &&
                ForJson.Contains(TEXT("\"isPostfix\": true")));

        FString SwitchJson;
        TestTrue(TEXT("Context menu can build a typed structured switch insertion"),
            FUnifiedScriptAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
                Document,
                RewireFunction,
                *RewireBlock,
                TEXT("flow.switch"),
                SwitchJson,
                Error));
        TestTrue(TEXT("Switch insertion binds one parameter and creates case/default sections"),
            SwitchJson.Contains(TEXT("\"statement\": \"switch\"")) &&
                SwitchJson.Contains(TEXT("\"governingExpression\"")) &&
                SwitchJson.Contains(TEXT("\"label\": \"pattern\"")) &&
                SwitchJson.Contains(TEXT("\"label\": \"default\"")));

        const FUnifiedScriptAuthoringAction* FirstStatement =
            RewireBlock->DirectStatementIds.Num() > 0
            ? RewireFunction.FindAction(RewireBlock->DirectStatementIds[0])
            : nullptr;
        const FUnifiedScriptAuthoringAction* SecondStatement =
            RewireBlock->DirectStatementIds.Num() > 1
            ? RewireFunction.FindAction(RewireBlock->DirectStatementIds[1])
            : nullptr;
        const FUnifiedScriptAuthoringEdge* FirstIncomingControl = FirstStatement != nullptr
            ? RewireFunction.Edges.FindByPredicate(
                [FirstStatement](const FUnifiedScriptAuthoringEdge& Edge)
                {
                    return Edge.Kind == TEXT("control") &&
                        Edge.ToNode == FirstStatement->Id && Edge.ToPort == TEXT("in");
                })
            : nullptr;
        TestNotNull(TEXT("Rewire demo exposes two sequential statement nodes"), SecondStatement);
        TestNotNull(TEXT("First statement has one compiler-owned control predecessor"),
            FirstIncomingControl);
        if (FirstStatement != nullptr && SecondStatement != nullptr &&
            FirstIncomingControl != nullptr)
        {
            FString ControlMoveJson;
            TestTrue(TEXT("Control drag maps to one region-aware statement move"),
                FUnifiedScriptAuthoringSurfaceReader::BuildControlEdgeReconnectionPatch(
                    Document,
                    RewireFunction,
                    *FirstIncomingControl,
                    SecondStatement->Id,
                    TEXT("then"),
                    ControlMoveJson,
                    Error));
            TestTrue(TEXT("Control move carries source and destination block preconditions"),
                ControlMoveJson.Contains(TEXT("\"operation\": \"moveStatement\"")) &&
                    ControlMoveJson.Contains(TEXT("\"expectedSourceParentBlockId\"")) &&
                    ControlMoveJson.Contains(TEXT("\"destinationParentBlockId\"")) &&
                    ControlMoveJson.Contains(RewireBlock->SourceNodeId));
        }

        const FUnifiedScriptAuthoringRegion* RewireThenBlock =
            RewireFunction.Regions.FindByPredicate([](
                const FUnifiedScriptAuthoringRegion& Region)
            {
                return Region.Kind == TEXT("then");
            });
        const FUnifiedScriptAuthoringAction* RewireThenReturn =
            RewireThenBlock != nullptr && !RewireThenBlock->DirectStatementIds.IsEmpty()
            ? RewireFunction.FindAction(RewireThenBlock->DirectStatementIds[0])
            : nullptr;
        const FUnifiedScriptAuthoringEdge* RewireThenIncoming = RewireThenReturn != nullptr
            ? RewireFunction.Edges.FindByPredicate(
                [RewireThenReturn](const FUnifiedScriptAuthoringEdge& Edge)
                {
                    return Edge.Kind == TEXT("control") &&
                        Edge.ToNode == RewireThenReturn->Id && Edge.ToPort == TEXT("in");
                })
            : nullptr;
        if (FirstStatement != nullptr && RewireThenReturn != nullptr &&
            RewireThenIncoming != nullptr)
        {
            FString InvalidTerminalMoveJson;
            TestFalse(TEXT("Cross-region drag rejects a terminal statement target"),
                FUnifiedScriptAuthoringSurfaceReader::BuildControlEdgeReconnectionPatch(
                    Document,
                    RewireFunction,
                    *RewireThenIncoming,
                    FirstStatement->Id,
                    TEXT("then"),
                    InvalidTerminalMoveJson,
                    Error));
            TestTrue(TEXT("Terminal rejection directs the author to a structured transform"),
                Error.Contains(TEXT("explicit structured transform")));
        }

        FString DeleteJson;
        TestTrue(TEXT("Delete key can build an exact statement deletion"),
            FUnifiedScriptAuthoringSurfaceReader::BuildStatementDeletionPatch(
                Document, RewireFunction, *RewireTarget, DeleteJson, Error));
        TestTrue(TEXT("Deletion retains parent-block and sibling preconditions"),
            DeleteJson.Contains(RewireBlock->SourceNodeId) &&
                DeleteJson.Contains(TEXT("expectedPreviousSiblingId")));

        FString DuplicateJson;
        const FString CopiedLocalName = RewireTarget->GraphKind == TEXT("variable")
            ? TEXT("incrementedCopy")
            : FString();
        TestTrue(TEXT("Copy/paste can build an exact same-block statement duplication"),
            FUnifiedScriptAuthoringSurfaceReader::BuildStatementDuplicationPatch(
                Document,
                RewireFunction,
                *RewireTarget,
                *RewireTarget,
                CopiedLocalName,
                DuplicateJson,
                Error));
        TestTrue(TEXT("Statement duplication preserves both source and destination gaps"),
            DuplicateJson.Contains(TEXT("\"operation\": \"duplicateStatement\"")) &&
                DuplicateJson.Contains(TEXT("\"expectedParentBlockId\"")) &&
                DuplicateJson.Contains(TEXT("\"expectedSourcePreviousSiblingId\"")) &&
                DuplicateJson.Contains(TEXT("\"expectedDestinationPreviousSiblingId\"")) &&
                DuplicateJson.Contains(TEXT("\"newLocalName\": \"incrementedCopy\"")));

        FUnifiedScriptAuthoringFunction TransferFunction = RewireFunction;
        FUnifiedScriptAuthoringRegion SwitchRegion;
        SwitchRegion.Id = RewireSource->Id;
        SwitchRegion.Kind = TEXT("switch");
        SwitchRegion.SourceNodeId = RewireSource->Id;
        FUnifiedScriptAuthoringRegion SwitchSection;
        SwitchSection.Id = RewireTarget->Id;
        SwitchSection.Kind = TEXT("switchSection");
        SwitchSection.ParentRegionId = SwitchRegion.Id;
        SwitchSection.SourceNodeId = RewireBlock->SourceNodeId;
        SwitchRegion.ChildRegionIds = {SwitchSection.Id};
        TransferFunction.Regions = {SwitchRegion, SwitchSection};

        FString BreakJson;
        TestTrue(TEXT("A switch-section block can author a nearest-switch break"),
            FUnifiedScriptAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
                Document,
                TransferFunction,
                SwitchSection,
                TEXT("flow.break"),
                BreakJson,
                Error));
        TestTrue(TEXT("Switch break emits the bounded terminal statement"),
            BreakJson.Contains(TEXT("\"statement\": \"break\"")));

        FString InvalidContinueJson;
        TestFalse(TEXT("A switch without a loop rejects continue"),
            FUnifiedScriptAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
                Document,
                TransferFunction,
                SwitchSection,
                TEXT("flow.continue"),
                InvalidContinueJson,
                Error));

        TransferFunction.Regions[0].Kind = TEXT("loop");
        FString ContinueJson;
        TestTrue(TEXT("A loop body can author continue"),
            FUnifiedScriptAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
                Document,
                TransferFunction,
                TransferFunction.Regions[1],
                TEXT("flow.continue"),
                ContinueJson,
                Error));
        TestTrue(TEXT("Loop continue emits the bounded terminal statement"),
            ContinueJson.Contains(TEXT("\"statement\": \"continue\"")));

        const FUnifiedScriptAuthoringParameter* ValueParameter =
            RewireFunction.Parameters.FindByPredicate([](
                const FUnifiedScriptAuthoringParameter& Parameter)
            {
                return Parameter.Name == TEXT("value");
            });
        TestNotNull(TEXT("Switch authoring fixture has a typed governing parameter"),
            ValueParameter);
        if (ValueParameter != nullptr)
        {
            FUnifiedScriptAuthoringFunction SwitchFunction = RewireFunction;
            const FString SwitchId = TEXT("us_a1111111111111111111111111111111");
            const FString SwitchRegionId = TEXT("us_a2222222222222222222222222222222");
            const FString FirstSectionId = TEXT("us_a3333333333333333333333333333333");
            const FString FirstRegionId = TEXT("us_a4444444444444444444444444444444");
            const FString FirstBodyId = TEXT("us_a5555555555555555555555555555555");
            const FString FirstLabelId = TEXT("us_a6666666666666666666666666666666");
            const FString DefaultSectionId = TEXT("us_a7777777777777777777777777777777");
            const FString DefaultRegionId = TEXT("us_a8888888888888888888888888888888");
            const FString DefaultBodyId = TEXT("us_a9999999999999999999999999999999");
            const FString DefaultLabelId = TEXT("us_abbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

            FUnifiedScriptAuthoringAction SwitchAction;
            SwitchAction.Id = SwitchId;
            SwitchAction.GraphKind = TEXT("switch");
            SwitchAction.RegionId = SwitchRegionId;
            SwitchAction.Ports.Add({TEXT("value"), TEXT("input"), TEXT("data"),
                ValueParameter->TypeId, ValueParameter->TypeDisplayName});
            SwitchAction.Properties.Add(TEXT("hasDefault"), TEXT("true"));

            const auto MakeLabel = [](
                const FString& Id,
                const FString& RegionId,
                const FString& SectionId,
                const FString& BodyId,
                const FString& DisplayText,
                bool bDefault)
            {
                FUnifiedScriptAuthoringAction Label;
                Label.Id = Id;
                Label.GraphKind = TEXT("switchCase");
                Label.RegionId = RegionId;
                Label.Properties.Add(TEXT("switchSectionId"), SectionId);
                Label.Properties.Add(TEXT("sectionId"), BodyId);
                Label.Properties.Add(TEXT("displayText"), DisplayText);
                Label.Properties.Add(TEXT("isDefault"), bDefault ? TEXT("true") : TEXT("false"));
                if (!bDefault)
                {
                    Label.Properties.Add(TEXT("patternText"), TEXT("0"));
                }
                return Label;
            };
            FUnifiedScriptAuthoringAction FirstLabel = MakeLabel(
                FirstLabelId,
                FirstRegionId,
                FirstSectionId,
                FirstBodyId,
                TEXT("case 0"),
                false);
            FUnifiedScriptAuthoringAction DefaultLabel = MakeLabel(
                DefaultLabelId,
                DefaultRegionId,
                DefaultSectionId,
                DefaultBodyId,
                TEXT("default"),
                true);

            FUnifiedScriptAuthoringRegion AuthoredSwitchRegion;
            AuthoredSwitchRegion.Id = SwitchRegionId;
            AuthoredSwitchRegion.Kind = TEXT("switch");
            AuthoredSwitchRegion.SourceNodeId = SwitchId;
            AuthoredSwitchRegion.ChildRegionIds = {FirstRegionId, DefaultRegionId};
            FUnifiedScriptAuthoringRegion FirstSection;
            FirstSection.Id = FirstRegionId;
            FirstSection.Kind = TEXT("switchSection");
            FirstSection.ParentRegionId = SwitchRegionId;
            FirstSection.SourceNodeId = FirstBodyId;
            FirstSection.DirectActionIds = {FirstLabelId};
            FUnifiedScriptAuthoringRegion DefaultSection;
            DefaultSection.Id = DefaultRegionId;
            DefaultSection.Kind = TEXT("switchSection");
            DefaultSection.ParentRegionId = SwitchRegionId;
            DefaultSection.SourceNodeId = DefaultBodyId;
            DefaultSection.DirectActionIds = {DefaultLabelId};
            SwitchFunction.Actions.Add(SwitchAction);
            SwitchFunction.Actions.Add(FirstLabel);
            SwitchFunction.Actions.Add(DefaultLabel);
            SwitchFunction.Regions.Add(AuthoredSwitchRegion);
            SwitchFunction.Regions.Add(FirstSection);
            SwitchFunction.Regions.Add(DefaultSection);

            FString InsertSectionJson;
            TestTrue(TEXT("Switch inspector can insert a full structured pattern section"),
                FUnifiedScriptAuthoringSurfaceReader::BuildSwitchSectionInsertionPatch(
                    Document,
                    SwitchFunction,
                    SwitchFunction.Actions[SwitchFunction.Actions.Num() - 3],
                    TEXT("> 10 and not 42"),
                    InsertSectionJson,
                    Error));
            TestTrue(TEXT("Pattern insertion preserves ordered section and pattern algebra"),
                InsertSectionJson.Contains(TEXT("\"operation\": \"insertSwitchSection\"")) &&
                    InsertSectionJson.Contains(FirstSectionId) &&
                    InsertSectionJson.Contains(DefaultSectionId) &&
                    InsertSectionJson.Contains(TEXT("\"pattern\": \"logical\"")) &&
                    InsertSectionJson.Contains(TEXT("\"pattern\": \"relational\"")) &&
                    InsertSectionJson.Contains(TEXT("\"pattern\": \"not\"")));

            FString DuplicateDefaultJson;
            TestFalse(TEXT("Switch inspector rejects a duplicate default section"),
                FUnifiedScriptAuthoringSurfaceReader::BuildSwitchSectionInsertionPatch(
                    Document,
                    SwitchFunction,
                    SwitchFunction.Actions[SwitchFunction.Actions.Num() - 3],
                    TEXT("default"),
                    DuplicateDefaultJson,
                    Error));

            FString ReplaceSectionJson;
            TestTrue(TEXT("Case inspector can replace a section with typed guarded pattern text"),
                FUnifiedScriptAuthoringSurfaceReader::BuildSwitchSectionReplacementPatch(
                    Document,
                    SwitchFunction,
                    SwitchFunction.Actions[SwitchFunction.Actions.Num() - 2],
                    TEXT("(int candidate) when doubleValue"),
                    ReplaceSectionJson,
                    Error));
            TestTrue(TEXT("Replacement pins the old labels and emits typed pattern/guard nodes"),
                ReplaceSectionJson.Contains(TEXT("\"operation\": \"replaceSwitchSection\"")) &&
                    ReplaceSectionJson.Contains(FirstLabelId) &&
                    ReplaceSectionJson.Contains(TEXT("\"pattern\": \"parenthesized\"")) &&
                    ReplaceSectionJson.Contains(TEXT("\"pattern\": \"declaration\"")) &&
                    ReplaceSectionJson.Contains(TEXT("\"guard\"")));

            FString MoveSectionJson;
            TestTrue(TEXT("Case inspector can move a section by one exact sibling slot"),
                FUnifiedScriptAuthoringSurfaceReader::BuildSwitchSectionMovePatch(
                    Document,
                    SwitchFunction,
                    SwitchFunction.Actions[SwitchFunction.Actions.Num() - 2],
                    1,
                    MoveSectionJson,
                    Error));
            TestTrue(TEXT("Section movement carries source and destination gaps"),
                MoveSectionJson.Contains(TEXT("\"operation\": \"moveSwitchSection\"")) &&
                    MoveSectionJson.Contains(TEXT("expectedSourceNextSectionId")) &&
                    MoveSectionJson.Contains(TEXT("expectedDestinationPreviousSectionId")));

            FString DeleteSectionJson;
            TestTrue(TEXT("Delete key can build a safe switch-section deletion"),
                FUnifiedScriptAuthoringSurfaceReader::BuildSwitchSectionDeletionPatch(
                    Document,
                    SwitchFunction,
                    SwitchFunction.Actions[SwitchFunction.Actions.Num() - 2],
                    DeleteSectionJson,
                    Error));
            TestTrue(TEXT("Section deletion pins its parent, siblings, and labels"),
                DeleteSectionJson.Contains(TEXT("\"operation\": \"deleteSwitchSection\"")) &&
                    DeleteSectionJson.Contains(SwitchId) &&
                    DeleteSectionJson.Contains(DefaultSectionId) &&
                    DeleteSectionJson.Contains(FirstLabelId));
        }
    }

    const FUnifiedScriptAuthoringFunction& ApiFunction =
        *Document.Assets[0].Functions.FindByPredicate([](
            const FUnifiedScriptAuthoringFunction& Candidate)
        {
            return Candidate.Name == TEXT("UEApiPreview");
        });
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

    UUnifiedScriptEdGraphNode* OldControlNode =
        NewObject<UUnifiedScriptEdGraphNode>(TransactionGraph);
    UUnifiedScriptEdGraphNode* NewControlNode =
        NewObject<UUnifiedScriptEdGraphNode>(TransactionGraph);
    UUnifiedScriptEdGraphNode* ControlTargetNode =
        NewObject<UUnifiedScriptEdGraphNode>(TransactionGraph);
    OldControlNode->NodeKind = TEXT("variable");
    NewControlNode->NodeKind = TEXT("assignment");
    ControlTargetNode->NodeKind = TEXT("assignment");
    TransactionGraph->AddNode(OldControlNode, false, false);
    TransactionGraph->AddNode(NewControlNode, false, false);
    TransactionGraph->AddNode(ControlTargetNode, false, false);
    FEdGraphPinType ControlPinType;
    ControlPinType.PinCategory = TEXT("exec");
    UEdGraphPin* OldControlOutput = OldControlNode->CreatePin(
        EGPD_Output, ControlPinType, FName(TEXT("then")));
    UEdGraphPin* NewControlOutput = NewControlNode->CreatePin(
        EGPD_Output, ControlPinType, FName(TEXT("execOut")));
    UEdGraphPin* ControlInput = ControlTargetNode->CreatePin(
        EGPD_Input, ControlPinType, FName(TEXT("execIn")));
    OldControlOutput->MakeLinkTo(ControlInput, false);
    bool bControlRewireSubmitted = false;
    TransactionGraph->OnControlRewireRequested =
        FOnUnifiedScriptControlRewireRequested::CreateLambda(
            [&bControlRewireSubmitted, NewControlOutput, ControlInput](
                const UEdGraphPin* SubmittedOutput,
                const UEdGraphPin* SubmittedInput)
            {
                bControlRewireSubmitted = SubmittedOutput == NewControlOutput &&
                    SubmittedInput == ControlInput;
            });
    TestEqual(
        TEXT("Single-path control output advertises a region-aware transform"),
        RewireSchema->CanCreateConnection(NewControlOutput, ControlInput).Response,
        CONNECT_RESPONSE_MAKE);
    TestTrue(
        TEXT("Schema forwards control drag without mutating compiler-owned links"),
        RewireSchema->TryCreateConnection(NewControlOutput, ControlInput));
    TestTrue(TEXT("Control transaction callback receives the endpoints"),
        bControlRewireSubmitted);
    TestTrue(TEXT("Old control wire remains until the statement move commits"),
        ControlInput->LinkedTo.Contains(OldControlOutput));
    TestFalse(TEXT("Proposed control wire is not drawn optimistically"),
        ControlInput->LinkedTo.Contains(NewControlOutput));

    TestEqual(
        TEXT("Workbench endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildSemanticPatchUrl(
            TEXT("  http://127.0.0.1:4173///  ")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/semantic-patch")));
    TestEqual(
        TEXT("Workbench declaration endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildDeclarationPatchUrl(
            TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/declaration-patch")));
    TestEqual(
        TEXT("Workbench patch-plan endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildSemanticPatchPlanUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/semantic-patch-plan")));
    TestEqual(
        TEXT("Workbench undo endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildUndoUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/undo")));
    TestEqual(
        TEXT("Workbench redo endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildRedoUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/redo")));

    FString SurfaceJson;
    TestTrue(TEXT("Demo Surface source is readable"),
        FFileHelper::LoadFileToString(SurfaceJson, *SurfacePath));
    SurfaceJson.ReplaceInline(
        TEXT("unifiedscript.authoring-surface/v0.4"),
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
        45);

    const FString WorkspaceJson = FString::Printf(
        TEXT("{\"schemaVersion\":\"unifiedscript.authoring-workspace/v0.3\","
             "\"revision\":2,\"sourcePath\":\"DemoScript.cs\","
             "\"history\":{\"canUndo\":true,\"canRedo\":false,"
             "\"undoDepth\":2,\"redoDepth\":0,"
             "\"undoTransactionId\":\"txn-2\",\"undoDescription\":\"Reconnect flow\","
             "\"lastOperationId\":\"op-2\"},"
             "\"surface\":%s,\"generation\":{\"number\":3}}"),
        *SurfaceJson.Replace(
            TEXT("unifiedscript.authoring-surface/v9.9"),
            TEXT("unifiedscript.authoring-surface/v0.4")));
    FUnifiedScriptAuthoringWorkspaceResult Workspace;
    TestTrue(
        *FString::Printf(TEXT("Workbench response carries a strict Surface: %s"), *Error),
        FUnifiedScriptAuthoringWorkbench::ParseWorkspaceResponse(
            WorkspaceJson,
            Workspace,
            Error));
    TestEqual(TEXT("Workbench response revision is retained"), Workspace.Revision, int64(2));
    TestEqual(TEXT("Workbench response generation is retained"), Workspace.Generation, int64(3));
    TestTrue(TEXT("Workbench response exposes undo availability"), Workspace.History.bCanUndo);
    TestFalse(TEXT("Workbench response exposes redo availability"), Workspace.History.bCanRedo);
    TestEqual(TEXT("Workbench response retains undo depth"), Workspace.History.UndoDepth, int64(2));
    TestEqual(TEXT("Workbench response retains undo label"), Workspace.History.UndoDescription, FString(TEXT("Reconnect flow")));
    TestEqual(TEXT("Workbench response retains operation identity"), Workspace.History.LastOperationId, FString(TEXT("op-2")));
    TestEqual(TEXT("Workbench response replaces the Surface transactionally"),
        Workspace.Surface.GetActionCount(), 45);
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
    TestTrue(
        TEXT("Slate context palette exposes the reflected multicast Event Entry action"),
        Widget->HasNodePaletteAction(
            TEXT("ue.event|UnifiedScriptComponent|OnManagedSignal")));
    TestTrue(
        TEXT("Slate context palette exposes typed switch construction"),
        Widget->HasNodePaletteAction(TEXT("flow.switch")));
    TestEqual(
        TEXT("Blueprint-style graph uses the compact presentation projection"),
        Widget->GetVisibleGraphNodeCount(),
        FUnifiedScriptGraphPresentation::CountVisibleActions(*EventFunction));
    TestEqual(
        TEXT("Blueprint-style graph materializes every visible compiler-owned wire"),
        Widget->GetVisibleGraphEdgeCount(),
        FUnifiedScriptGraphPresentation::CountVisibleEdges(*EventFunction));
    TestEqual(
        TEXT("My Script navigation activates the UE Event Entry function"),
        Widget->GetActiveFunctionId(),
        EventFunction->Id);
    return true;
}

#endif

#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptAuthoringSurface.h"
#include "UnifiedScriptAuthoringWorkbench.h"
#include "UnifiedScriptDebugWorkbench.h"

#include "Framework/Docking/TabManager.h"
#include "Graph/UnifiedScriptGraphLayoutStore.h"
#include "Graph/UnifiedScriptNodePalette.h"
#include "Graph/UnifiedScriptGraphPresentation.h"
#include "HAL/FileManager.h"
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
    TestEqual(TEXT("Demo Surface has seven Visual functions"), Document.GetFunctionCount(), 7);
    TestEqual(TEXT("Demo Surface preserves all Graph nodes"), Document.GetActionCount(), 70);
    TestEqual(TEXT("Demo Surface preserves all Graph edges"), Document.GetEdgeCount(), 68);
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
    const FUnifiedScriptAuthoringFunction* DefaultFunction =
        Document.Assets[0].Functions.FindByPredicate([](
            const FUnifiedScriptAuthoringFunction& Candidate)
        {
            return Candidate.Name == TEXT("UEDefaultPreview");
        });
    TestNotNull(TEXT("Demo retains the scalar Visual function"), FunctionPtr);
    TestNotNull(TEXT("Demo projects the attributed UE Event Entry"), EventFunction);
    TestNotNull(TEXT("Demo projects the reflected-default API function"), DefaultFunction);
    if (FunctionPtr == nullptr || EventFunction == nullptr || DefaultFunction == nullptr)
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
        const FString EventSubtitle =
            FUnifiedScriptGraphPresentation::GetNodeSubtitle(*EventEntry);
        TestEqual(TEXT("Default Event Entry content keeps only the author-facing signature"),
            EventSubtitle,
            FString(TEXT("void OnManagedSignal(int value, UnrealObjectHandle signalContext)")));
        TestFalse(TEXT("Default Event Entry content hides the generated API path"),
            EventSubtitle.Contains(TEXT("/Script/")));
        const FString EventTooltip =
            FUnifiedScriptGraphPresentation::GetNodeTooltip(*EventEntry);
        TestTrue(TEXT("On-demand Event Entry tooltip retains effects, API path, and exact types"),
            EventTooltip.Contains(TEXT("effects: readsState|writesState")) &&
                EventTooltip.Contains(TEXT("unrealEventPath: /Script/")) &&
                EventTooltip.Contains(TEXT("UnrealObjectHandle")) &&
                EventTooltip.Contains(EventEntry->Id));
    }
    const FUnifiedScriptAuthoringAction* MemberRead = EventFunction->Actions.FindByPredicate(
        [](const FUnifiedScriptAuthoringAction& Action)
        {
            return Action.GraphKind == TEXT("memberAccess") &&
                Action.Properties.FindRef(TEXT("member")) == TEXT("ManagedState");
        });
    TestNotNull(TEXT("Demo retains a generated member read for compact presentation"), MemberRead);
    if (MemberRead != nullptr)
    {
        TestEqual(TEXT("Member read subtitle describes only its receiver"),
            FUnifiedScriptGraphPresentation::GetNodeSubtitle(*MemberRead),
            FString(TEXT("From _component")));
        TestTrue(TEXT("Member read tooltip retains generated symbol and type identity"),
            FUnifiedScriptGraphPresentation::GetNodeTooltip(*MemberRead).Contains(
                TEXT("referencedSymbolId: us_")) &&
            FUnifiedScriptGraphPresentation::GetNodeTooltip(*MemberRead).Contains(
                TEXT("typeId: us_")));
    }
    FUnifiedScriptAuthoringAction OpaqueCall;
    OpaqueCall.Id = TEXT("us_opaque_call");
    OpaqueCall.GraphKind = TEXT("invocation");
    OpaqueCall.Category = TEXT("Calls");
    OpaqueCall.Title = TEXT("ClampInSource");
    OpaqueCall.Summary = TEXT("ClampInSource(value)");
    OpaqueCall.Properties.Add(TEXT("method"), TEXT("ClampInSource"));
    OpaqueCall.Properties.Add(TEXT("methodId"), TEXT("us_opaque_method"));
    OpaqueCall.Properties.Add(TEXT("calleeBodyRepresentation"), TEXT("opaqueCode"));
    OpaqueCall.Properties.Add(TEXT("authoringBoundary"), TEXT("sourceOnly"));
    OpaqueCall.Properties.Add(TEXT("calleeBodyEditable"), TEXT("false"));
    TestTrue(TEXT("Source-only call card exposes its opaque authoring boundary"),
        FUnifiedScriptGraphPresentation::GetNodeSubtitle(OpaqueCall).StartsWith(
            TEXT("Source-only C#")));
    const FString OpaqueTooltip =
        FUnifiedScriptGraphPresentation::GetNodeTooltip(OpaqueCall);
    TestTrue(TEXT("Source-only call tooltip retains exact non-editable contract"),
        OpaqueTooltip.Contains(TEXT("calleeBodyRepresentation: opaqueCode")) &&
            OpaqueTooltip.Contains(TEXT("authoringBoundary: sourceOnly")) &&
            OpaqueTooltip.Contains(TEXT("calleeBodyEditable: false")));
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
    TestTrue(TEXT("Event Entry patch uses declaration v0.3 and the canonical event path"),
        EventPatchJson.Contains(TEXT("unifiedscript.declaration-patch/v0.3")) &&
            EventPatchJson.Contains(TEXT("addUnrealEventHandler")) &&
            EventPatchJson.Contains(
                TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnManagedSignal")));
    FString DeclarationPatchJson;
    TestTrue(TEXT("My Script can create a runnable Actor Script Asset"),
        FUnifiedScriptAuthoringSurfaceReader::BuildScriptAssetCreationPatch(
            Document,
            Document.Assets[0].Span.FilePath,
            TEXT("UnifiedScript.Unreal.Demo"),
            TEXT("VisualActorScript"),
            TEXT("Unreal/VisualActor"),
            true,
            DeclarationPatchJson,
            Error) &&
        DeclarationPatchJson.Contains(TEXT("addAsset")) &&
        DeclarationPatchJson.Contains(TEXT("includeActorLifecycle")));
    TestTrue(TEXT("My Script can rename the engine-facing asset path"),
        FUnifiedScriptAuthoringSurfaceReader::BuildScriptAssetPathUpdatePatch(
            Document,
            Document.Assets[0],
            TEXT("Unreal/DemoRenamed"),
            DeclarationPatchJson,
            Error) &&
        DeclarationPatchJson.Contains(TEXT("updateAsset")) &&
        DeclarationPatchJson.Contains(TEXT("replacementAssetPath")));
    FUnifiedScriptAuthoringExposure NewExposure;
    NewExposure.bInstanceEditable = true;
    NewExposure.bSaveGame = true;
    NewExposure.Category = TEXT("Gameplay");
    TestTrue(TEXT("My Script can add exposed state without authoring JSON"),
        FUnifiedScriptAuthoringSurfaceReader::BuildStateAdditionPatch(
            Document,
            Document.Assets[0],
            TEXT("Ammo"),
            TEXT("int"),
            TEXT("integer"),
            TEXT("30"),
            NewExposure,
            DeclarationPatchJson,
            Error) &&
        DeclarationPatchJson.Contains(TEXT("addState")) &&
        DeclarationPatchJson.Contains(TEXT("instanceEditable")));
    TestTrue(TEXT("My Script can edit an exact exposed state contract"),
        FUnifiedScriptAuthoringSurfaceReader::BuildStateUpdatePatch(
            Document,
            Document.Assets[0].States[0],
            TEXT("InitialValue"),
            TEXT("int"),
            TEXT("integer"),
            TEXT("12"),
            NewExposure,
            DeclarationPatchJson,
            Error) &&
        DeclarationPatchJson.Contains(TEXT("updateState")) &&
        DeclarationPatchJson.Contains(TEXT("expected")));
    FUnifiedScriptAuthoringParameter FunctionParameter;
    FunctionParameter.Name = TEXT("amount");
    FunctionParameter.TypeMetadataName = TEXT("int");
    TestTrue(TEXT("My Script can create a typed Visual function"),
        FUnifiedScriptAuthoringSurfaceReader::BuildFunctionAdditionPatch(
            Document,
            Document.Assets[0],
            TEXT("ResolveAmmo"),
            TEXT("int"),
            false,
            {FunctionParameter},
            DeclarationPatchJson,
            Error) &&
        DeclarationPatchJson.Contains(TEXT("addFunction")) &&
        DeclarationPatchJson.Contains(TEXT("ResolveAmmo")));
    TestTrue(TEXT("My Script can update a function contract with preconditions"),
        FUnifiedScriptAuthoringSurfaceReader::BuildFunctionUpdatePatch(
            Document,
            Function,
            TEXT("ResolveInitialValue2"),
            TEXT("int"),
            Function.bStatic,
            Function.Parameters,
            DeclarationPatchJson,
            Error) &&
        DeclarationPatchJson.Contains(TEXT("updateFunction")) &&
        DeclarationPatchJson.Contains(TEXT("returnTypeMetadataName")));
    TestTrue(TEXT("My Script can remove a reflected Event Entry as a function"),
        FUnifiedScriptAuthoringSurfaceReader::BuildFunctionRemovalPatch(
            Document,
            *EventFunction,
            DeclarationPatchJson,
            Error) &&
        DeclarationPatchJson.Contains(TEXT("removeFunction")));
    TestTrue(TEXT("My Script can add a missing lifecycle hook"),
        FUnifiedScriptAuthoringSurfaceReader::BuildLifecycleHookAdditionPatch(
            Document,
            Document.Assets[0],
            TEXT("tick"),
            DeclarationPatchJson,
            Error) &&
        DeclarationPatchJson.Contains(TEXT("addLifecycleHook")));
    TestTrue(TEXT("My Script can remove an exact Script Asset through preview"),
        FUnifiedScriptAuthoringSurfaceReader::BuildScriptAssetRemovalPatch(
            Document,
            Document.Assets[0],
            DeclarationPatchJson,
            Error) &&
        DeclarationPatchJson.Contains(TEXT("removeAsset")));
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
    TestTrue(TEXT("Patch uses semantic-patch v0.28"),
        PatchJson.Contains(TEXT("\"unifiedscript.patch/v0.28\"")));
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

        FString PinValueJson;
        TestTrue(
            TEXT("Pin Details can build a typed literal replacement"),
            FUnifiedScriptAuthoringSurfaceReader::BuildDataInputValuePatch(
                Document,
                RewireFunction,
                *RewireEdge,
                TEXT("42"),
                PinValueJson,
                Error));
        TestTrue(TEXT("Pin value patch carries exact edge and typed expression facts"),
            PinValueJson.Contains(TEXT("\"operation\": \"setDataInputValue\"")) &&
                PinValueJson.Contains(TEXT("\"expression\": \"literal\"")) &&
                PinValueJson.Contains(TEXT("\"valueText\": \"42\"")) &&
                PinValueJson.Contains(RewireEdge->Id));

        FString PinDefaultJson;
        TestTrue(
            TEXT("Pin Details can reset through a bounded typed default"),
            FUnifiedScriptAuthoringSurfaceReader::BuildDataInputValuePatch(
                Document,
                RewireFunction,
                *RewireEdge,
                TEXT("default"),
                PinDefaultJson,
                Error));
        TestTrue(TEXT("Pin reset carries the semantic type name and ID"),
            PinDefaultJson.Contains(TEXT("\"expression\": \"default\"")) &&
                PinDefaultJson.Contains(TEXT("\"typeName\"")) &&
                PinDefaultJson.Contains(TEXT("\"expectedTypeId\"")));

        FString PromotionJson;
        TestTrue(
            TEXT("Pin Details can promote its source expression to a typed local"),
            FUnifiedScriptAuthoringSurfaceReader::BuildDataInputPromotionPatch(
                Document,
                RewireFunction,
                *RewireEdge,
                TEXT("promotedIncrement"),
                PromotionJson,
                Error));
        TestTrue(TEXT("Promotion keeps expression, statement, block, name, and type preconditions"),
            PromotionJson.Contains(TEXT("\"operation\": \"introduceTemporary\"")) &&
                PromotionJson.Contains(RewireEdge->FromNode) &&
                PromotionJson.Contains(RewireTarget->Id) &&
                PromotionJson.Contains(TEXT("\"expectedParentBlockId\"")) &&
                PromotionJson.Contains(TEXT("\"name\": \"promotedIncrement\"")) &&
                PromotionJson.Contains(TEXT("\"expectedTypeId\"")));
        TestFalse(TEXT("Promotion rejects a non-identifier local name"),
            FUnifiedScriptAuthoringSurfaceReader::BuildDataInputPromotionPatch(
                Document,
                RewireFunction,
                *RewireEdge,
                TEXT("not a name"),
                PromotionJson,
                Error));

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

            FString CollapseJson;
            TestTrue(TEXT("Multi-selection can collapse one consecutive statement range"),
                FUnifiedScriptAuthoringSurfaceReader::BuildStatementCollapsePatch(
                    Document,
                    RewireFunction,
                    {FirstStatement->Id, SecondStatement->Id},
                    CollapseJson,
                    Error));
            TestTrue(TEXT("Scope collapse carries ordered members and outer sibling gaps"),
                CollapseJson.Contains(TEXT("\"operation\": \"collapseStatements\"")) &&
                    CollapseJson.Contains(TEXT("\"expectedStatementIds\"")) &&
                    CollapseJson.Contains(TEXT("\"expectedPreviousSiblingId\"")) &&
                    CollapseJson.Contains(TEXT("\"expectedNextSiblingId\"")));

            FString ExtractionJson;
            TestTrue(TEXT("Multi-selection can build a bounded callable extraction"),
                FUnifiedScriptAuthoringSurfaceReader::BuildCallableExtractionPatch(
                    Document,
                    RewireFunction,
                    {FirstStatement->Id, SecondStatement->Id},
                    TEXT("ExtractRewireRange"),
                    ExtractionJson,
                    Error));
            TestTrue(TEXT("Callable extraction carries method, ordered range, gaps, and name"),
                ExtractionJson.Contains(TEXT("\"operation\": \"extractCallable\"")) &&
                    ExtractionJson.Contains(TEXT("\"expectedContainingMethodId\"")) &&
                    ExtractionJson.Contains(RewireFunction.Id) &&
                    ExtractionJson.Contains(TEXT("\"expectedParentBlockId\"")) &&
                    ExtractionJson.Contains(TEXT("\"expectedStatementIds\"")) &&
                    ExtractionJson.Contains(TEXT("\"expectedPreviousSiblingId\"")) &&
                    ExtractionJson.Contains(TEXT("\"expectedNextSiblingId\"")) &&
                    ExtractionJson.Contains(TEXT("\"methodName\": \"ExtractRewireRange\"")));
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

        if (FirstStatement != nullptr && SecondStatement != nullptr)
        {
            FUnifiedScriptAuthoringFunction RangeCopyFunction = RewireFunction;
            FUnifiedScriptAuthoringAction DestinationAction = *FirstStatement;
            DestinationAction.Id = TEXT("us_c1111111111111111111111111111111");
            DestinationAction.DebugTargetId = DestinationAction.Id;
            DestinationAction.RegionId = TEXT("us_c2222222222222222222222222222222");
            RangeCopyFunction.Actions.Add(DestinationAction);

            FUnifiedScriptAuthoringRegion DestinationBlock;
            DestinationBlock.Id = DestinationAction.RegionId;
            DestinationBlock.Kind = TEXT("then");
            DestinationBlock.SourceNodeId = TEXT("us_c3333333333333333333333333333333");
            DestinationBlock.Span = DestinationAction.Span;
            DestinationBlock.DirectStatementIds = {DestinationAction.Id};
            DestinationBlock.DirectActionIds = DestinationBlock.DirectStatementIds;
            RangeCopyFunction.Regions.Add(DestinationBlock);

            TMap<FString, FString> LocalRenames;
            for (const FUnifiedScriptAuthoringAction* Source : {FirstStatement, SecondStatement})
            {
                if (Source->GraphKind == TEXT("variable"))
                {
                    LocalRenames.Add(
                        Source->Id,
                        Source == FirstStatement
                            ? TEXT("doubledRangeCopy")
                            : TEXT("incrementedRangeCopy"));
                }
            }

            FString RangeCopyJson;
            TestTrue(TEXT("Multi-selection can build a cross-region copy transaction"),
                FUnifiedScriptAuthoringSurfaceReader::BuildStatementRangeDuplicationPatch(
                    Document,
                    RangeCopyFunction,
                    {FirstStatement->Id, SecondStatement->Id},
                    RangeCopyFunction.Actions.Last(),
                    LocalRenames,
                    RangeCopyJson,
                    Error));
            TestTrue(TEXT("Range copy preserves ordered source and distinct destination facts"),
                RangeCopyJson.Contains(TEXT("\"operation\": \"duplicateStatements\"")) &&
                    RangeCopyJson.Contains(TEXT("\"expectedStatementIds\"")) &&
                    RangeCopyJson.Contains(TEXT("\"expectedSourceParentBlockId\"")) &&
                    RangeCopyJson.Contains(TEXT("\"destinationParentBlockId\"")) &&
                    RangeCopyJson.Contains(RewireBlock->SourceNodeId) &&
                    RangeCopyJson.Contains(DestinationBlock.SourceNodeId) &&
                    RangeCopyJson.Contains(TEXT("doubledRangeCopy")) &&
                    RangeCopyJson.Contains(TEXT("incrementedRangeCopy")));
        }

        if (FirstStatement != nullptr && SecondStatement != nullptr)
        {
            FUnifiedScriptAuthoringFunction ScopedFunction = RewireFunction;
            FUnifiedScriptAuthoringRegion* ScopedParent =
                ScopedFunction.Regions.FindByPredicate([RewireBlock](
                    const FUnifiedScriptAuthoringRegion& Region)
                {
                    return Region.Id == RewireBlock->Id;
                });
            if (ScopedParent != nullptr)
            {
                ScopedParent->DirectStatementIds.Remove(FirstStatement->Id);
                ScopedParent->DirectStatementIds.Remove(SecondStatement->Id);
                ScopedParent->DirectActionIds.Remove(FirstStatement->Id);
                ScopedParent->DirectActionIds.Remove(SecondStatement->Id);
                FUnifiedScriptAuthoringRegion NestedScope;
                NestedScope.Id = TEXT("us_11111111111111111111111111111111");
                NestedScope.Kind = TEXT("block");
                NestedScope.ParentRegionId = ScopedParent->Id;
                NestedScope.SourceNodeId = TEXT("us_22222222222222222222222222222222");
                NestedScope.Span = FirstStatement->Span;
                NestedScope.Span.EndLine = SecondStatement->Span.EndLine;
                NestedScope.Span.EndColumn = SecondStatement->Span.EndColumn;
                NestedScope.DirectStatementIds = {FirstStatement->Id, SecondStatement->Id};
                NestedScope.DirectActionIds = NestedScope.DirectStatementIds;
                const FString ScopedParentSourceId = ScopedParent->SourceNodeId;
                ScopedParent->ChildRegionIds.Add(NestedScope.Id);
                ScopedFunction.Regions.Add(NestedScope);

                FString ExpandJson;
                TestTrue(TEXT("Nested lexical region can build an exact expansion patch"),
                    FUnifiedScriptAuthoringSurfaceReader::BuildBlockExpansionPatch(
                        Document,
                        ScopedFunction,
                        ScopedFunction.Regions.Last(),
                        ExpandJson,
                        Error));
                TestTrue(TEXT("Scope expansion carries block, children, parent, and sibling facts"),
                    ExpandJson.Contains(TEXT("\"operation\": \"expandBlock\"")) &&
                        ExpandJson.Contains(NestedScope.SourceNodeId) &&
                        ExpandJson.Contains(ScopedParentSourceId) &&
                        ExpandJson.Contains(TEXT("\"expectedChildStatementIds\"")));
            }
        }

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

    const TArray<FUnifiedScriptGraphPaletteAction> PaletteActions = {
        {TEXT("flow.branch"), TEXT("Flow Control"), TEXT("Branch"),
            TEXT("Insert an if block."), TEXT("if condition bool")},
        {TEXT("value.add"), TEXT("Math|Integer"), TEXT("Add Expression"),
            TEXT("Add two integers."), TEXT("integer plus sum")},
        {TEXT("local.int"), TEXT("Variables"), TEXT("Integer Local"),
            TEXT("Create an integer local."), TEXT("int variable number")},
        {TEXT("ue.event|Test|Signal"), TEXT("Unreal Events|Test"), TEXT("Event Signal"),
            TEXT("Create an event entry."), TEXT("ue delegate signal"), false},
        {TEXT("ue.call|Test|Wait"), TEXT("Test|Async"), TEXT("Wait"),
            TEXT("Generated latent call."), TEXT("wait async"), true, false, false, true,
            TEXT("Latent | Game thread only"),
            TEXT("Latent continuation authoring is unavailable.")},
    };
    TArray<FString> FavoriteActions = {TEXT("flow.branch")};
    TArray<FString> RecentActions = {TEXT("value.add"), TEXT("local.int")};
    TArray<FUnifiedScriptNodePaletteItem> RankedPalette =
        FUnifiedScriptNodePaletteModel::Build(
            PaletteActions,
            FString(),
            FavoriteActions,
            RecentActions,
            true);
    TestEqual(TEXT("Palette retains enabled and discoverable unavailable actions"),
        RankedPalette.Num(), 5);
    TestEqual(TEXT("Favorite node ranks before recent and alphabetical nodes"),
        RankedPalette[0].Action.ActionId, FString(TEXT("flow.branch")));
    TestEqual(TEXT("Most-recent node ranks immediately after favorites"),
        RankedPalette[1].Action.ActionId, FString(TEXT("value.add")));
    const TArray<FUnifiedScriptNodePaletteItem> SearchPalette =
        FUnifiedScriptNodePaletteModel::Build(
            PaletteActions,
            TEXT("integer plus"),
            FavoriteActions,
            RecentActions,
            true);
    TestEqual(TEXT("Palette token search matches title, tooltip, and keywords together"),
        SearchPalette.Num(), 1);
    if (!SearchPalette.IsEmpty())
    {
        TestEqual(TEXT("Keyword search resolves the typed Add action"),
            SearchPalette[0].Action.ActionId, FString(TEXT("value.add")));
    }
    const TArray<FUnifiedScriptNodePaletteItem> RestrictionPalette =
        FUnifiedScriptNodePaletteModel::Build(
            PaletteActions,
            TEXT("latent game thread"),
            FavoriteActions,
            RecentActions,
            true);
    TestEqual(TEXT("Generated policy text participates in Palette search"),
        RestrictionPalette.Num(), 1);
    if (!RestrictionPalette.IsEmpty())
    {
        TestFalse(TEXT("Unavailable generated actions stay non-executable"),
            RestrictionPalette[0].Action.bEnabled);
    }
    const TArray<FUnifiedScriptNodePaletteItem> FunctionPalette =
        FUnifiedScriptNodePaletteModel::Build(
            PaletteActions,
            FString(),
            FavoriteActions,
            RecentActions,
            false);
    TestEqual(TEXT("No active block hides statement actions but retains function events"),
        FunctionPalette.Num(), 1);
    if (!FunctionPalette.IsEmpty())
    {
        TestEqual(TEXT("Function-level context retains the Event Entry action"),
            FunctionPalette[0].Action.ActionId, FString(TEXT("ue.event|Test|Signal")));
    }
    TestFalse(TEXT("Toggling an existing favorite removes it"),
        FUnifiedScriptNodePaletteModel::ToggleFavorite(FavoriteActions, TEXT("flow.branch")));
    TestTrue(TEXT("Toggling a new favorite adds it"),
        FUnifiedScriptNodePaletteModel::ToggleFavorite(FavoriteActions, TEXT("value.add")));
    for (int32 Index = 0; Index < 12; ++Index)
    {
        FUnifiedScriptNodePaletteModel::RecordRecent(
            RecentActions,
            FString::Printf(TEXT("action-%d"), Index));
    }
    FUnifiedScriptNodePaletteModel::RecordRecent(RecentActions, TEXT("action-4"));
    TestEqual(TEXT("Recent-node history stays bounded"),
        RecentActions.Num(), FUnifiedScriptNodePaletteModel::MaxRecentActions);
    TestEqual(TEXT("Repeated recent usage moves one action to the front without duplication"),
        RecentActions[0], FString(TEXT("action-4")));
    TestEqual(TEXT("Recent-node history keeps unique actions"),
        RecentActions.FilterByPredicate([](const FString& Id)
        {
            return Id == TEXT("action-4");
        }).Num(), 1);

    FString CheckedInLayoutPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectDir(), TEXT(".."), TEXT("samples/Layout/DamageRules.usgraph")));
    FPaths::NormalizeFilename(CheckedInLayoutPath);
    FUnifiedScriptGraphFunctionLayout CheckedInLayout;
    TestTrue(
        TEXT("Native layout store reads the checked-in Comment/Reroute example"),
        FUnifiedScriptGraphLayoutStore::LoadFunction(
            CheckedInLayoutPath,
            TEXT("us_16919a6d2cc64371d8ebbfb46251b14a"),
            CheckedInLayout,
            Error));
    TestEqual(TEXT("Checked-in layout retains five placed semantic nodes"),
        CheckedInLayout.NodePositions.Num(), 5);
    TestEqual(TEXT("Checked-in layout retains one comment region"),
        CheckedInLayout.CommentRegions.Num(), 1);
    TestEqual(TEXT("Checked-in layout retains one routed edge"),
        CheckedInLayout.RoutingHints.Num(), 1);
    if (!CheckedInLayout.CommentRegions.IsEmpty())
    {
        TestEqual(TEXT("Comment title survives native parsing"),
            CheckedInLayout.CommentRegions[0].Title,
            FString(TEXT("Damage scale expression")));
        TestEqual(TEXT("Comment membership survives native parsing"),
            CheckedInLayout.CommentRegions[0].NodeIds.Num(), 3);
    }
    if (!CheckedInLayout.RoutingHints.IsEmpty())
    {
        TestEqual(TEXT("Routed edge retains ordered control points"),
            CheckedInLayout.RoutingHints[0].ControlPoints.Num(), 2);
        TestEqual(TEXT("First route point X remains exact"),
            CheckedInLayout.RoutingHints[0].ControlPoints[0].X, 620.0);
        TestEqual(TEXT("Second route point Y remains exact"),
            CheckedInLayout.RoutingHints[0].ControlPoints[1].Y, 168.0);
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

    UUnifiedScriptEdGraphRerouteNode* RerouteNode =
        NewObject<UUnifiedScriptEdGraphRerouteNode>(TransactionGraph);
    RerouteNode->EdgeId = TEXT("us_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    RerouteNode->RoutedPinCategory = DataPinType.PinCategory;
    RerouteNode->RoutedPinSubCategory = DataPinType.PinSubCategory;
    RerouteNode->AllocateDefaultPins();
    TransactionGraph->AddNode(RerouteNode, false, false);
    UUnifiedScriptEdGraphRerouteNode* FollowingRerouteNode =
        NewObject<UUnifiedScriptEdGraphRerouteNode>(TransactionGraph);
    FollowingRerouteNode->EdgeId = RerouteNode->EdgeId;
    FollowingRerouteNode->PointIndex = 1;
    FollowingRerouteNode->RoutedPinCategory = DataPinType.PinCategory;
    FollowingRerouteNode->RoutedPinSubCategory = DataPinType.PinSubCategory;
    FollowingRerouteNode->AllocateDefaultPins();
    TransactionGraph->AddNode(FollowingRerouteNode, false, false);
    OldOutput->BreakLinkTo(TargetInput);
    OldOutput->MakeLinkTo(RerouteNode->FindPinChecked(TEXT("in"), EGPD_Input), false);
    RerouteNode->FindPinChecked(TEXT("out"), EGPD_Output)->MakeLinkTo(
        FollowingRerouteNode->FindPinChecked(TEXT("in"), EGPD_Input), false);
    FollowingRerouteNode->FindPinChecked(TEXT("out"), EGPD_Output)->MakeLinkTo(
        TargetInput, false);
    RerouteNode->DestroyNode();
    TestTrue(TEXT("Deleting one reroute reconnects the remaining routed wire"),
        OldOutput->LinkedTo.Contains(
            FollowingRerouteNode->FindPinChecked(TEXT("in"), EGPD_Input)) &&
        FollowingRerouteNode->PointIndex == 0);
    FollowingRerouteNode->DestroyNode();
    TestTrue(TEXT("Deleting the final layout-only reroute reconnects the semantic wire"),
        OldOutput->LinkedTo.Contains(TargetInput) && TargetInput->LinkedTo.Contains(OldOutput));

    bool bRerouteRequested = false;
    TransactionGraph->OnRerouteRequested = FOnUnifiedScriptRerouteRequested::CreateLambda(
        [&bRerouteRequested, OldOutput, TargetInput](
            UEdGraphPin* SubmittedA,
            UEdGraphPin* SubmittedB,
            const FVector2D& Location)
        {
            bRerouteRequested = SubmittedA == OldOutput && SubmittedB == TargetInput &&
                Location.Equals(FVector2D(120.0, 80.0));
        });
    RewireSchema->OnPinConnectionDoubleCicked(
        OldOutput, TargetInput, FVector2f(120.0f, 80.0f));
    TestTrue(TEXT("Double-clicking a wire forwards an exact reroute request"),
        bRerouteRequested);

    const FString RoundTripLayoutPath = FPaths::Combine(
        FPaths::ProjectSavedDir(), TEXT("Automation/UnifiedScriptCanvasLayout.usgraph"));
    IFileManager::Get().Delete(*RoundTripLayoutPath, false, true);
    UUnifiedScriptEdGraphCommentNode* CommentNode =
        NewObject<UUnifiedScriptEdGraphCommentNode>(TransactionGraph);
    CommentNode->LayoutId = TEXT("automation-comment");
    CommentNode->NodeComment = TEXT("Gameplay calculation");
    CommentNode->NodePosX = -40;
    CommentNode->NodePosY = -20;
    CommentNode->NodeWidth = 480;
    CommentNode->NodeHeight = 260;
    CommentNode->AddNodeUnderComment(OldSourceNode);
    TransactionGraph->AddNode(CommentNode, false, false);
    OldSourceNode->SemanticId = TEXT("us_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    OldSourceNode->NodePosX = 20;
    OldSourceNode->NodePosY = 30;
    TargetNode->SemanticId = TEXT("us_cccccccccccccccccccccccccccccccc");
    TargetNode->NodePosX = 320;
    TargetNode->NodePosY = 140;
    UUnifiedScriptEdGraphRerouteNode* FirstRoute =
        NewObject<UUnifiedScriptEdGraphRerouteNode>(TransactionGraph);
    FirstRoute->EdgeId = TEXT("us_dddddddddddddddddddddddddddddddd");
    FirstRoute->PointIndex = 0;
    FirstRoute->NodePosX = 140;
    FirstRoute->NodePosY = 65;
    TransactionGraph->AddNode(FirstRoute, false, false);
    UUnifiedScriptEdGraphRerouteNode* SecondRoute =
        NewObject<UUnifiedScriptEdGraphRerouteNode>(TransactionGraph);
    SecondRoute->EdgeId = FirstRoute->EdgeId;
    SecondRoute->PointIndex = 1;
    SecondRoute->NodePosX = 240;
    SecondRoute->NodePosY = 115;
    TransactionGraph->AddNode(SecondRoute, false, false);
    const TArray<UEdGraphNode*> RoundTripNodes = {
        OldSourceNode, TargetNode, CommentNode, FirstRoute, SecondRoute};
    TestTrue(TEXT("Native canvas layout save succeeds"),
        FUnifiedScriptGraphLayoutStore::SaveFunction(
            RoundTripLayoutPath,
            TEXT("us_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
            RoundTripNodes,
            Error));
    FUnifiedScriptGraphFunctionLayout RoundTripLayout;
    TestTrue(TEXT("Native canvas layout reload succeeds"),
        FUnifiedScriptGraphLayoutStore::LoadFunction(
            RoundTripLayoutPath,
            TEXT("us_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
            RoundTripLayout,
            Error));
    TestEqual(TEXT("Round trip retains semantic node positions"),
        RoundTripLayout.NodePositions.Num(), 2);
    TestEqual(TEXT("Round trip retains the comment"),
        RoundTripLayout.CommentRegions.Num(), 1);
    TestEqual(TEXT("Round trip retains both route points"),
        RoundTripLayout.RoutingHints.Num() == 1
            ? RoundTripLayout.RoutingHints[0].ControlPoints.Num()
            : 0,
        2);
    if (!RoundTripLayout.CommentRegions.IsEmpty())
    {
        TestEqual(TEXT("Round trip retains comment membership"),
            RoundTripLayout.CommentRegions[0].NodeIds.Num(), 2);
    }
    FString InvalidLayoutJson;
    TestTrue(TEXT("Round-trip canvas JSON remains readable for strictness testing"),
        FFileHelper::LoadFileToString(InvalidLayoutJson, *RoundTripLayoutPath));
    TestEqual(TEXT("Strictness fixture removes exactly one required route field"),
        InvalidLayoutJson.ReplaceInline(
            TEXT("\"routingHints\":"),
            TEXT("\"missingRoutingHints\":")),
        1);
    TestTrue(TEXT("Strictness fixture can be written"),
        FFileHelper::SaveStringToFile(InvalidLayoutJson, *RoundTripLayoutPath));
    FUnifiedScriptGraphFunctionLayout InvalidLayout;
    TestFalse(TEXT("Matching layout functions cannot silently omit required canvas arrays"),
        FUnifiedScriptGraphLayoutStore::LoadFunction(
            RoundTripLayoutPath,
            TEXT("us_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
            InvalidLayout,
            Error));
    IFileManager::Get().Delete(*RoundTripLayoutPath, false, true);

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
        TEXT("Workbench derives the matching semantic preview endpoint"),
        FUnifiedScriptAuthoringWorkbench::BuildPreviewUrl(
            TEXT("http://127.0.0.1:4173/api/authoring/semantic-patch")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/preview/semantic-patch")));
    TestEqual(
        TEXT("Workbench derives the grouped commit preview endpoint"),
        FUnifiedScriptAuthoringWorkbench::BuildPreviewUrl(
            TEXT("http://127.0.0.1:4173/api/authoring/group/commit")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/preview/group/commit")));
    TestTrue(
        TEXT("Non-publishing group operations do not invent a preview endpoint"),
        FUnifiedScriptAuthoringWorkbench::BuildPreviewUrl(
            TEXT("http://127.0.0.1:4173/api/authoring/group/cancel")).IsEmpty());
    TestEqual(
        TEXT("Workbench workspace refresh endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildWorkspaceUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/workspace")));
    TestEqual(
        TEXT("Workbench undo endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildUndoUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/undo")));
    TestEqual(
        TEXT("Workbench redo endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildRedoUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/redo")));
    TestEqual(
        TEXT("Workbench group begin endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildGroupBeginUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/group/begin")));
    TestEqual(
        TEXT("Workbench group commit endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildGroupCommitUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/group/commit")));
    TestEqual(
        TEXT("Workbench group cancel endpoint is normalized"),
        FUnifiedScriptAuthoringWorkbench::BuildGroupCancelUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/authoring/group/cancel")));
    TestEqual(
        TEXT("Native debugger start endpoint is normalized"),
        FUnifiedScriptDebugWorkbench::BuildStartUrl(TEXT("http://127.0.0.1:4173///")),
        FString(TEXT("http://127.0.0.1:4173/api/debug/start")));
    TestEqual(
        TEXT("Native debugger wait endpoint is normalized"),
        FUnifiedScriptDebugWorkbench::BuildWaitUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/debug/wait")));
    TestEqual(
        TEXT("Native debugger Pause endpoint is normalized"),
        FUnifiedScriptDebugWorkbench::BuildPauseUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/debug/pause")));
    TestEqual(
        TEXT("Native debugger Step In endpoint is normalized"),
        FUnifiedScriptDebugWorkbench::BuildStepInUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/debug/step-in")));
    TestEqual(
        TEXT("Native debugger Step Out endpoint is normalized"),
        FUnifiedScriptDebugWorkbench::BuildStepOutUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/debug/step-out")));
    TestEqual(
        TEXT("Native debugger watch endpoint is normalized"),
        FUnifiedScriptDebugWorkbench::BuildEvaluateUrl(TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/debug/evaluate")));
    TestEqual(
        TEXT("Live World debug publish endpoint is normalized"),
        FUnifiedScriptDebugWorkbench::BuildLiveWorldPublishUrl(
            TEXT("http://127.0.0.1:4173/")),
        FString(TEXT("http://127.0.0.1:4173/api/debug/publish-live-world")));

    if (Assignment != nullptr)
    {
        TSet<FString> DebugTargets;
        DebugTargets.Add(Assignment->DebugTargetId);
        FString DebugStartJson;
        TestTrue(
            *FString::Printf(TEXT("Native debugger builds a guarded start request: %s"), *Error),
            FUnifiedScriptDebugWorkbench::BuildStartRequest(
                Document.SourceCompilationId,
                Document.SemanticConfigurationId,
                Function,
                Document.Assets[0],
                DebugTargets,
                DebugStartJson,
                Error));
        TestTrue(TEXT("Debug start carries the exact Visual method"),
            DebugStartJson.Contains(Function.Id));
        TestTrue(TEXT("Debug start carries the loaded compilation identity"),
            DebugStartJson.Contains(Document.SourceCompilationId));
        TestTrue(TEXT("Debug start carries the semantic breakpoint target"),
            DebugStartJson.Contains(Assignment->DebugTargetId));

        FString LivePublishRequestJson;
        TestTrue(
            TEXT("Native debugger builds a generation-guarded live publish request"),
            FUnifiedScriptDebugWorkbench::BuildLiveWorldPublishRequest(
                Document.SourceCompilationId,
                Document.SemanticConfigurationId,
                LivePublishRequestJson,
                Error));
        TestTrue(
            TEXT("Live publish carries both current compiler identities"),
            LivePublishRequestJson.Contains(Document.SourceCompilationId) &&
                LivePublishRequestJson.Contains(Document.SemanticConfigurationId));
        const FString LivePublishResponseJson = FString::Printf(
            TEXT("{\"schemaVersion\":\"unifiedscript.live-world-debug-publish/v0.1\","
                 "\"generation\":{\"domainId\":\"us_domain\",\"number\":9,"
                 "\"generationId\":\"us_live_generation\","
                 "\"imageKey\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\","
                 "\"sourceCompilationId\":\"%s\","
                 "\"semanticConfigurationId\":\"%s\"},"
                 "\"assemblyPath\":\"Binaries/Managed/Demo.dll\","
                 "\"portablePdbPath\":\"Binaries/Managed/Demo.pdb\","
                 "\"reloadTransaction\":{"
                 "\"schemaVersion\":\"unifiedscript.reload-transaction/v0.1\","
                 "\"transactionId\":\"live-world-test\","
                 "\"source\":\"liveWorldDebug\",\"state\":\"published\","
                 "\"fromGeneration\":9,\"candidateGeneration\":9,"
                 "\"activeGeneration\":9,\"phases\":[]}}"),
            *Document.SourceCompilationId,
            *Document.SemanticConfigurationId);
        FUnifiedScriptLiveWorldDebugPublish LivePublish;
        TestTrue(
            TEXT("Native debugger parses the instrumented live World publication"),
            FUnifiedScriptDebugWorkbench::ParseLiveWorldPublishResponse(
                LivePublishResponseJson,
                LivePublish,
                Error));
        FString LiveConfigurationJson;
        TestTrue(
            TEXT("Native debugger binds semantic breakpoints to runtime generation two"),
            FUnifiedScriptDebugWorkbench::BuildLiveWorldConfiguration(
                LivePublish,
                2,
                DebugTargets,
                LiveConfigurationJson,
                Error));
        TestTrue(
            TEXT("Live World configuration retains runtime, program, and breakpoint identities"),
            LiveConfigurationJson.Contains(TEXT("\"runtimeGeneration\": 2")) &&
                LiveConfigurationJson.Contains(TEXT("us_live_generation")) &&
                LiveConfigurationJson.Contains(Assignment->DebugTargetId));
        TestTrue(
            TEXT("Live World publication retains reload transaction provenance"),
            LivePublish.ReloadTransactionJson.Contains(TEXT("live-world-test")) &&
                LivePublish.ReloadTransactionJson.Contains(
                    TEXT("unifiedscript.reload-transaction/v0.1")));

        const FUnifiedScriptRuntimeSourceLocation RuntimeSource{
            Assignment->Span.FilePath,
            Assignment->Span.StartLine + 1,
            Assignment->Span.StartColumn + 1,
            Function.QualifiedName};
        FString RuntimeFailureMethodId;
        FString RuntimeFailureNodeId;
        TestTrue(
            TEXT("Runtime PDB coordinates resolve to the exact semantic graph node"),
            FUnifiedScriptDebugWorkbench::ResolveRuntimeDiagnosticNode(
                Document.Assets[0],
                MakeArrayView(&RuntimeSource, 1),
                RuntimeFailureMethodId,
                RuntimeFailureNodeId));
        TestEqual(TEXT("Runtime failure retains the Visual method identity"),
            RuntimeFailureMethodId, Function.Id);
        TestEqual(TEXT("Runtime failure retains the DebugTargetId"),
            RuntimeFailureNodeId, Assignment->DebugTargetId);

        const FString PausedDebugJson = FString::Printf(
            TEXT("{\"schemaVersion\":\"unifiedscript.debug-session/v0.2\","
                 "\"state\":\"paused\",\"generation\":{\"number\":7,"
                 "\"generationId\":\"us_generation\","
                 "\"sourceCompilationId\":\"%s\","
                 "\"semanticConfigurationId\":\"%s\"},"
                 "\"executionId\":12,\"pause\":{"
                 "\"schemaVersion\":\"unifiedscript.debug-pause/v0.2\","
                 "\"pauseId\":\"us_pause\",\"reason\":\"breakpoint\","
                 "\"stepKind\":null,\"methodId\":\"%s\",\"nodeId\":\"%s\"},"
                 "\"frames\":[{\"frameId\":31,\"index\":0,"
                 "\"methodId\":\"%s\",\"nodeId\":\"%s\","
                 "\"methodMetadataName\":\"Demo.ResolveInitialValue\","
                 "\"metadataToken\":100663297,\"ilOffset\":17,"
                 "\"sourceSpan\":{\"filePath\":\"Demo.cs\","
                 "\"start\":{\"line\":8,\"column\":9},"
                 "\"end\":{\"line\":8,\"column\":24}},\"hasLocals\":true}],"
                 "\"locals\":[],\"frameLocals\":[{\"frameId\":31,\"locals\":[{"
                 "\"name\":\"result\",\"typeMetadataName\":\"System.Int32\","
                 "\"display\":\"100\",\"kind\":\"scalar\","
                 "\"children\":[],\"truncated\":false}]}],\"result\":null,"
                 "\"breakpoints\":[{\"breakpointId\":\"us_breakpoint\","
                 "\"nodeId\":\"%s\",\"state\":\"bound\","
                 "\"methodId\":\"%s\",\"message\":null}]}"),
            *Document.SourceCompilationId,
            *Document.SemanticConfigurationId,
            *Function.Id,
            *Assignment->DebugTargetId,
            *Function.Id,
            *Assignment->DebugTargetId,
            *Assignment->DebugTargetId,
            *Function.Id);
        FUnifiedScriptDebugSession DebugSession;
        const bool bParsedDebugSession = TestTrue(
            *FString::Printf(TEXT("Native debugger parses a paused v0.2 session: %s"), *Error),
            FUnifiedScriptDebugWorkbench::ParseSessionResponse(
                PausedDebugJson, DebugSession, Error));
        if (bParsedDebugSession)
        {
            TestTrue(TEXT("Native debugger retains the exact pause"), DebugSession.IsPaused());
            TestEqual(TEXT("Native debugger retains one managed stack frame"),
                DebugSession.Frames.Num(), 1);
            if (DebugSession.Frames.Num() == 1)
            {
                TestEqual(TEXT("Native debugger retains frame semantic node identity"),
                    DebugSession.Frames[0].NodeId, Assignment->DebugTargetId);
            }
            TestEqual(TEXT("Native debugger retains one frame-local snapshot"),
                DebugSession.FrameLocals.Num(), 1);
            if (DebugSession.FrameLocals.Num() == 1)
            {
                TestEqual(TEXT("Native debugger retains one local in the selected frame"),
                    DebugSession.FrameLocals[0].Locals.Num(), 1);
                if (DebugSession.FrameLocals[0].Locals.Num() == 1)
                {
                    TestEqual(TEXT("Native debugger retains deeper-frame-ready locals"),
                        DebugSession.FrameLocals[0].Locals[0].Display,
                        FString(TEXT("100")));
                }
            }
        }

        FString ResumeJson;
        TestTrue(TEXT("Native debugger builds an exact-pause resume request"),
            FUnifiedScriptDebugWorkbench::BuildResumeRequest(
                DebugSession, ResumeJson, Error));
        TestTrue(TEXT("Resume request carries generation, execution, and pause IDs"),
            ResumeJson.Contains(TEXT("us_generation")) &&
                ResumeJson.Contains(TEXT("us_pause")) &&
                ResumeJson.Contains(TEXT("12")));
        FString EvaluateJson;
        TestTrue(TEXT("Native debugger builds a bounded frame watch request"),
            FUnifiedScriptDebugWorkbench::BuildEvaluateRequest(
                DebugSession, 31, TEXT("result"), EvaluateJson, Error));
        TestTrue(TEXT("Watch request carries the selected frame and expression"),
            EvaluateJson.Contains(TEXT("31")) && EvaluateJson.Contains(TEXT("result")));

        const FString EvaluationJson = FString::Printf(
            TEXT("{\"schemaVersion\":\"unifiedscript.debug-evaluation/v0.1\","
                 "\"generation\":{\"number\":7,\"generationId\":\"us_generation\","
                 "\"sourceCompilationId\":\"%s\","
                 "\"semanticConfigurationId\":\"%s\"},"
                 "\"pauseId\":\"us_pause\",\"frameId\":31,"
                 "\"expression\":\"result\",\"value\":{"
                 "\"name\":\"result\",\"typeMetadataName\":\"System.Int32\","
                 "\"display\":\"100\",\"kind\":\"scalar\","
                 "\"children\":[],\"truncated\":false}}"),
            *Document.SourceCompilationId,
            *Document.SemanticConfigurationId);
        FUnifiedScriptDebugEvaluation DebugEvaluation;
        TestTrue(TEXT("Native debugger parses bounded watch evaluation"),
            FUnifiedScriptDebugWorkbench::ParseEvaluationResponse(
                EvaluationJson, DebugEvaluation, Error));
        TestEqual(TEXT("Watch evaluation retains the detached value"),
            DebugEvaluation.Value.Display, FString(TEXT("100")));
    }

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
        70);

    const FString WorkspaceJson = FString::Printf(
        TEXT("{\"schemaVersion\":\"unifiedscript.authoring-workspace/v0.5\","
             "\"revision\":2,\"sourcePath\":\"DemoScript.cs\","
             "\"history\":{\"canUndo\":true,\"canRedo\":false,"
             "\"undoDepth\":2,\"redoDepth\":0,"
             "\"undoTransactionId\":\"txn-2\",\"undoDescription\":\"Reconnect flow\","
             "\"lastOperationId\":\"op-2\",\"persistenceState\":\"restored\"},"
             "\"group\":{\"id\":\"group-1\",\"description\":\"Damage tuning\","
             "\"baselineRevision\":1,\"stagedOperationCount\":2},"
             "\"profile\":{"
             "\"schemaVersion\":\"unifiedscript.visual-csharp-profile-report/v0.1\","
             "\"profile\":{"
             "\"schemaVersion\":\"unifiedscript.visual-csharp-profile/v0.3\","
             "\"displayName\":\"Visual C# profile v0.3\","
             "\"languageVersion\":\"C# preview parsed by Roslyn\","
             "\"opaqueCodeBoundary\":{\"unmarkedMethodsAreSourceOnly\":true,"
             "\"callsProjectAsSingleNodes\":true,\"bodiesAreGraphEditable\":false,"
             "\"requiresGraphSafeSignature\":true,\"allowsRefParameters\":false}},"
             "\"methods\":["
             "{\"representation\":\"visualGraph\",\"graphEditable\":true,"
             "\"authoringBoundary\":\"visualGraph\"},"
             "{\"representation\":\"opaqueCode\",\"graphEditable\":false,"
             "\"authoringBoundary\":\"sourceOnly\"}]},"
             "\"reloadTransaction\":{"
             "\"schemaVersion\":\"unifiedscript.reload-transaction/v0.1\","
             "\"transactionId\":\"txn-workspace\",\"source\":\"authoring\","
             "\"state\":\"published\",\"fromGeneration\":2,"
             "\"candidateGeneration\":3,\"activeGeneration\":3,"
             "\"phases\":[{\"name\":\"compile\",\"state\":\"succeeded\","
             "\"durationMicroseconds\":12,\"detail\":\"Compiled candidate.\"}]},"
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
    TestEqual(TEXT("Workbench response exposes restored history"), Workspace.History.PersistenceState, FString(TEXT("restored")));
    TestTrue(TEXT("Workbench response exposes an active grouped transaction"), Workspace.Group.bActive);
    TestEqual(TEXT("Workbench response retains group identity"), Workspace.Group.Id, FString(TEXT("group-1")));
    TestEqual(TEXT("Workbench response retains staged operation count"), Workspace.Group.StagedOperationCount, int64(2));
    TestEqual(TEXT("Workbench response retains the Visual C# profile"),
        Workspace.Profile.DisplayName, FString(TEXT("Visual C# profile v0.3")));
    TestEqual(TEXT("Workbench response counts Visual methods"), Workspace.Profile.VisualMethodCount, 1);
    TestEqual(TEXT("Workbench response counts source-only methods"), Workspace.Profile.OpaqueMethodCount, 1);
    TestTrue(TEXT("Workbench response retains the opaque call-node contract"),
        Workspace.Profile.bOpaqueCallsProjectAsSingleNodes);
    TestEqual(
        TEXT("Workbench response retains reload transaction identity"),
        Workspace.ReloadTransaction.TransactionId,
        FString(TEXT("txn-workspace")));
    TestEqual(
        TEXT("Workbench response retains observable reload phases"),
        Workspace.ReloadTransaction.Phases.Num(),
        1);
    TestEqual(TEXT("Workbench response replaces the Surface transactionally"),
        Workspace.Surface.GetActionCount(), 70);

    const FString PreviewJson =
        TEXT("{\"schemaVersion\":\"unifiedscript.authoring-preview/v0.1\","
             "\"baselineRevision\":2,\"candidateRevision\":3,"
             "\"transactionId\":\"txn-preview\",\"description\":\"Change value\","
             "\"publishesRuntime\":true,"
             "\"previewToken\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\","
             "\"files\":[{\"path\":\"DemoScript.cs\",\"beforeText\":\"return 1;\","
             "\"afterText\":\"return 2;\"}],"
             "\"diagnostics\":[{\"code\":\"CS0168\",\"severity\":\"warning\","
             "\"message\":\"unused\",\"span\":{\"filePath\":\"DemoScript.cs\","
             "\"start\":{\"line\":4,\"column\":9},"
             "\"end\":{\"line\":4,\"column\":14}}}]} ");
    FUnifiedScriptAuthoringPreviewResult Preview;
    TestTrue(
        *FString::Printf(TEXT("Workbench parses exact before/after preview: %s"), *Error),
        FUnifiedScriptAuthoringWorkbench::ParsePreviewResponse(PreviewJson, Preview, Error));
    TestEqual(TEXT("Preview retains candidate revision"), Preview.CandidateRevision, int64(3));
    TestEqual(TEXT("Preview retains one changed file"), Preview.Files.Num(), 1);
    TestEqual(TEXT("Preview retains exact before source"),
        Preview.Files[0].BeforeText, FString(TEXT("return 1;")));
    TestEqual(TEXT("Preview retains exact after source"),
        Preview.Files[0].AfterText, FString(TEXT("return 2;")));
    TestEqual(TEXT("Preview retains compiler warning"), Preview.Diagnostics.Num(), 1);

    const FString FailureJson =
        TEXT("{\"error\":{\"code\":\"UAW1003\",\"message\":\"rejected\"},"
             "\"diagnostics\":[{\"code\":\"CS0029\",\"severity\":\"error\","
             "\"message\":\"cannot convert\",\"span\":{\"filePath\":\"DemoScript.cs\","
             "\"start\":{\"line\":8,\"column\":12},"
             "\"end\":{\"line\":8,\"column\":16}},\"semanticId\":\"us_test\"}],"
             "\"quickFixes\":[{\"id\":\"open-first-diagnostic\","
             "\"label\":\"Open Source\",\"kind\":\"openFirstDiagnostic\"}],"
             "\"rollback\":{\"state\":\"unchanged\",\"revision\":2,"
             "\"generation\":3,\"message\":\"No writes.\"},"
             "\"reloadTransaction\":{"
             "\"schemaVersion\":\"unifiedscript.reload-transaction/v0.1\","
             "\"transactionId\":\"txn-failed\",\"source\":\"authoring\","
             "\"state\":\"rolledBack\",\"fromGeneration\":3,"
             "\"candidateGeneration\":4,\"activeGeneration\":3,"
             "\"phases\":[{\"name\":\"compile\",\"state\":\"failed\","
             "\"durationMicroseconds\":7,\"detail\":\"Compiler rejected candidate.\"},"
             "{\"name\":\"rollback\",\"state\":\"unchanged\","
             "\"durationMicroseconds\":0,\"detail\":\"No writes.\"}],"
             "\"failure\":\"Compiler rejected candidate.\"}}");
    FUnifiedScriptAuthoringErrorResult Failure;
    TestTrue(
        *FString::Printf(TEXT("Workbench parses structured authoring failure: %s"), *Error),
        FUnifiedScriptAuthoringWorkbench::ParseErrorResponse(
            FailureJson, 409, Failure, Error));
    TestEqual(TEXT("Failure retains protocol code"), Failure.Code, FString(TEXT("UAW1003")));
    TestEqual(TEXT("Failure retains locatable diagnostic"), Failure.Diagnostics.Num(), 1);
    TestEqual(TEXT("Failure retains safe quick fix"), Failure.QuickFixes.Num(), 1);
    TestTrue(TEXT("Failure explicitly confirms unchanged rollback"),
        Failure.Rollback.IsConfirmed());
    TestEqual(TEXT("Failure retains rollback revision"), Failure.Rollback.Revision, int64(2));
    TestEqual(
        TEXT("Failure retains reload transaction identity"),
        Failure.ReloadTransaction.TransactionId,
        FString(TEXT("txn-failed")));
    TestEqual(
        TEXT("Failure retains failed and rollback phases"),
        Failure.ReloadTransaction.Phases.Num(),
        2);
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
    TestTrue(TEXT("My Script navigation activates the UE Event Entry function"),
        Widget->SelectFunction(EventFunction->Id));
    TestTrue(
        TEXT("Slate context palette exposes the reflected multicast Event Entry action"),
        Widget->HasNodePaletteAction(
            TEXT("ue.event|UnifiedScriptComponent|OnManagedSignal")));
    FUnifiedScriptGraphPaletteAction GeneratedEventAction;
    TestTrue(
        TEXT("Slate retains the generated Event Entry node descriptor"),
        Widget->GetNodePaletteAction(
            TEXT("ue.event|UnifiedScriptComponent|OnManagedSignal"),
            GeneratedEventAction));
    TestEqual(TEXT("Generated Event Entry category drives the Palette"),
        GeneratedEventAction.Category,
        FString(TEXT("UnifiedScript|Events")));
    TestTrue(TEXT("Generated thread restriction reaches the author tooltip"),
        GeneratedEventAction.Tooltip.Contains(TEXT("Game thread only")));
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
    TestEqual(TEXT("Selected UE Event Entry remains active"),
        Widget->GetActiveFunctionId(), EventFunction->Id);
    TestTrue(TEXT("My Script can activate the reflected-default proof function"),
        Widget->SelectFunction(DefaultFunction->Id));
    if (Assignment != nullptr)
    {
        TestTrue(TEXT("Slate can toggle a graph/source semantic breakpoint"),
            Widget->ToggleDebugBreakpoint(Assignment->Id));
        TestTrue(TEXT("Slate retains semantic breakpoint state across function selection"),
            Widget->IsDebugBreakpointEnabled(Assignment->Id));
        TestTrue(TEXT("Slate can remove the semantic breakpoint without editing source"),
            Widget->ToggleDebugBreakpoint(Assignment->Id));
        TestFalse(TEXT("Removed semantic breakpoint is no longer active"),
            Widget->IsDebugBreakpointEnabled(Assignment->Id));
    }
    FUnifiedScriptGraphPaletteAction DefaultApiAction;
    TestTrue(TEXT("Generated API defaults keep the action context-valid"),
        Widget->GetNodePaletteAction(
            TEXT("ue.call|UnifiedScriptInteropFixture|AddWithDefault"),
            DefaultApiAction));
    TestTrue(TEXT("Default-backed action stays executable without an Amount parameter"),
        DefaultApiAction.bEnabled);
    TestEqual(TEXT("Generated display name drives the default-backed action title"),
        DefaultApiAction.Title,
        FString(TEXT("Add With Default")));
    TestEqual(TEXT("Generated API category drives the default-backed action"),
        DefaultApiAction.Category,
        FString(TEXT("UnifiedScript|Interop")));
    TestTrue(TEXT("Generated pin default reaches the Palette tooltip"),
        DefaultApiAction.Tooltip.Contains(TEXT("Defaults: Amount=1")));
    return true;
}

#endif

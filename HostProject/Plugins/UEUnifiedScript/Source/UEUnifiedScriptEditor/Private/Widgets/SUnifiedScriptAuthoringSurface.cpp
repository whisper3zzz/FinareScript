#include "Widgets/SUnifiedScriptAuthoringSurface.h"

#include "AssetRegistry/AssetData.h"
#include "ContentBrowserModule.h"
#include "DesktopPlatformModule.h"
#include "Dom/JsonObject.h"
#include "EdGraph/EdGraph.h"
#include "Editor.h"
#include "Framework/Application/SlateApplication.h"
#include "Graph/UnifiedScriptGraphPresentation.h"
#include "Graph/UnifiedScriptGraphLayoutStore.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformApplicationMisc.h"
#include "HttpModule.h"
#include "IDesktopPlatform.h"
#include "IContentBrowserSingleton.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "SourceCodeNavigation.h"
#include "Styling/AppStyle.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptAuthoringWorkbench.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptEdGraph.h"
#include "UObject/UObjectIterator.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SCheckBox.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Input/SMultiLineEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SSplitter.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SSeparator.h"
#include "Widgets/Views/STreeView.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "UnifiedScriptAuthoringSurface"

namespace UE::UnifiedScript::AuthoringSurface::Slate::Private
{
TSharedRef<STextBlock> MakeInspectorLabel(const FString& Text)
{
    return SNew(STextBlock)
        .Text(FText::FromString(Text))
        .Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
        .ColorAndOpacity(FLinearColor(0.55f, 0.58f, 0.62f));
}

TSharedRef<SWidget> MakeInspectorValue(const FString& Label, const FString& Value)
{
    return SNew(SVerticalBox)
        + SVerticalBox::Slot()
        .AutoHeight()
        [
            MakeInspectorLabel(Label)
        ]
        + SVerticalBox::Slot()
        .AutoHeight()
        .Padding(0.0f, 2.0f, 0.0f, 10.0f)
        [
            SNew(STextBlock)
            .Text(FText::FromString(Value))
            .AutoWrapText(true)
        ];
}
}

void SUnifiedScriptAuthoringSurface::Construct(const FArguments& InArgs)
{
    DemoSurfacePath = InArgs._InitialSurfacePath.IsEmpty()
        ? TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.surface.json")
        : InArgs._InitialSurfacePath;

    AuthoringGraph.Reset(NewObject<UUnifiedScriptEdGraph>(
        GetTransientPackage(),
        NAME_None,
        RF_Transient));
    AuthoringGraph->Schema = UUnifiedScriptEdGraphSchema::StaticClass();
    AuthoringGraph->OnDataRewireRequested =
        FOnUnifiedScriptDataRewireRequested::CreateSP(
            this,
            &SUnifiedScriptAuthoringSurface::HandleDataRewireRequested);
    AuthoringGraph->OnDataDisconnectRequested =
        FOnUnifiedScriptDataDisconnectRequested::CreateSP(
            this,
            &SUnifiedScriptAuthoringSurface::HandleDataDisconnectRequested);
    AuthoringGraph->OnNodeCreateRequested =
        FOnUnifiedScriptNodeCreateRequested::CreateSP(
            this,
            &SUnifiedScriptAuthoringSurface::HandleNodeCreateRequested);
    AuthoringGraph->OnStatementDeleteRequested =
        FOnUnifiedScriptStatementDeleteRequested::CreateSP(
            this,
            &SUnifiedScriptAuthoringSurface::HandleStatementDeleteRequested);

    FGraphAppearanceInfo GraphAppearance;
    GraphAppearance.CornerText = LOCTEXT("GraphCorner", "UNIFIEDSCRIPT  /  VISUAL BEHAVIOR");
    GraphAppearance.InstructionText = LOCTEXT(
        "GraphInstruction",
        "Select a function in My Script to inspect its visual behavior.");
    SGraphEditor::FGraphEditorEvents GraphEvents;
    GraphEvents.OnSelectionChanged = SGraphEditor::FOnSelectionChanged::CreateSP(
        this,
        &SUnifiedScriptAuthoringSurface::HandleGraphSelectionChanged);
    GraphEvents.OnNodeDoubleClicked = FSingleNodeEvent::CreateSP(
        this,
        &SUnifiedScriptAuthoringSurface::HandleGraphNodeDoubleClicked);

    ChildSlot
    [
        SNew(SBorder)
        .BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
        .Padding(10.0f)
        [
            SNew(SVerticalBox)
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 0.0f, 0.0f, 8.0f)
            [
                SNew(SHorizontalBox)
                + SHorizontalBox::Slot()
                .FillWidth(1.0f)
                .VAlign(VAlign_Center)
                [
                    SNew(SVerticalBox)
                    + SVerticalBox::Slot()
                    .AutoHeight()
                    [
                        SNew(STextBlock)
                        .Text(LOCTEXT("Title", "UnifiedScript Blueprint"))
                        .Font(FAppStyle::GetFontStyle(TEXT("HeadingExtraSmall")))
                    ]
                    + SVerticalBox::Slot()
                    .AutoHeight()
                    [
                        SNew(STextBlock)
                        .Text_Lambda([this]()
                        {
                            return FText::Format(
                                LOCTEXT("Counts", "C# <-> Visual  |  {0} assets  |  {1} states  |  {2} functions  |  {3} graph nodes"),
                                Document.Assets.Num(),
                                Document.GetStateCount(),
                                Document.GetFunctionCount(),
                                Document.GetActionCount());
                        })
                        .ColorAndOpacity(FLinearColor(0.55f, 0.58f, 0.62f))
                    ]
                ]
                + SHorizontalBox::Slot()
                .AutoWidth()
                .Padding(4.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("SelectedAsset", "Selected Script Asset"))
                    .ToolTipText(LOCTEXT("SelectedAssetTip", "Load the AuthoringSurfacePath from the selected UnifiedScript Data Asset."))
                    .OnClicked(this, &SUnifiedScriptAuthoringSurface::HandleLoadSelectedAsset)
                ]
                + SHorizontalBox::Slot()
                .AutoWidth()
                .Padding(4.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("Demo", "Demo"))
                    .OnClicked(this, &SUnifiedScriptAuthoringSurface::HandleLoadDemo)
                ]
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 0.0f, 0.0f, 8.0f)
            [
                SNew(SHorizontalBox)
                + SHorizontalBox::Slot()
                .AutoWidth()
                .VAlign(VAlign_Center)
                .Padding(0.0f, 0.0f, 8.0f, 0.0f)
                [
                    SNew(STextBlock)
                    .Text(LOCTEXT("WorkbenchUrl", "Workbench"))
                    .ColorAndOpacity(FLinearColor(0.55f, 0.58f, 0.62f))
                ]
                + SHorizontalBox::Slot()
                .FillWidth(1.0f)
                [
                    SAssignNew(ServerUrlTextBox, SEditableTextBox)
                    .Text(FText::FromString(TEXT("http://127.0.0.1:4173")))
                    .HintText(LOCTEXT("WorkbenchUrlHint", "http://127.0.0.1:4173"))
                ]
                + SHorizontalBox::Slot()
                .AutoWidth()
                .Padding(10.0f, 0.0f, 0.0f, 0.0f)
                .VAlign(VAlign_Center)
                [
                    SNew(SCheckBox)
                    .IsChecked(ECheckBoxState::Checked)
                    .OnCheckStateChanged_Lambda([this](ECheckBoxState State)
                    {
                        bReloadMatchingComponents = State == ECheckBoxState::Checked;
                    })
                    [
                        SNew(STextBlock).Text(LOCTEXT("LiveReload", "Reload Components"))
                    ]
                ]
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(0.0f, 0.0f, 0.0f, 8.0f)
            [
                SNew(SHorizontalBox)
                + SHorizontalBox::Slot()
                .FillWidth(1.0f)
                [
                    SAssignNew(PathTextBox, SEditableTextBox)
                    .HintText(LOCTEXT("PathHint", "Authoring Surface .surface.json path"))
                    .OnTextCommitted_Lambda([this](const FText&, ETextCommit::Type CommitType)
                    {
                        if (CommitType == ETextCommit::OnEnter)
                        {
                            HandleLoad();
                        }
                    })
                ]
                + SHorizontalBox::Slot()
                .AutoWidth()
                .Padding(6.0f, 0.0f, 0.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("Browse", "Browse..."))
                    .OnClicked(this, &SUnifiedScriptAuthoringSurface::HandleBrowse)
                ]
                + SHorizontalBox::Slot()
                .AutoWidth()
                .Padding(6.0f, 0.0f, 0.0f, 0.0f)
                [
                    SNew(SButton)
                    .Text(LOCTEXT("Reload", "Reload"))
                    .OnClicked(this, &SUnifiedScriptAuthoringSurface::HandleLoad)
                ]
                + SHorizontalBox::Slot()
                .AutoWidth()
                .Padding(10.0f, 0.0f, 0.0f, 0.0f)
                .VAlign(VAlign_Center)
                [
                    SNew(SCheckBox)
                    .IsChecked(ECheckBoxState::Checked)
                    .OnCheckStateChanged_Lambda([this](ECheckBoxState State)
                    {
                        bAutoReload = State == ECheckBoxState::Checked;
                    })
                    [
                        SNew(STextBlock).Text(LOCTEXT("AutoReload", "Auto Reload"))
                    ]
                ]
            ]
            + SVerticalBox::Slot()
            .FillHeight(1.0f)
            [
                SNew(SSplitter)
                + SSplitter::Slot()
                .Value(0.20f)
                [
                    SNew(SBorder)
                    .BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Panel")))
                    .Padding(0.0f)
                    [
                        SNew(SVerticalBox)
                        + SVerticalBox::Slot()
                        .AutoHeight()
                        .Padding(10.0f, 8.0f)
                        [
                            SNew(STextBlock)
                            .Text(LOCTEXT("MyScript", "MY SCRIPT"))
                            .Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
                            .ColorAndOpacity(FLinearColor(0.62f, 0.66f, 0.72f))
                        ]
                        + SVerticalBox::Slot()
                        .AutoHeight()
                        [
                            SNew(SSeparator)
                        ]
                        + SVerticalBox::Slot()
                        .FillHeight(1.0f)
                        .Padding(3.0f, 4.0f)
                        [
                            SAssignNew(TreeView, STreeView<FUnifiedScriptSurfaceTreeItemPtr>)
                            .TreeItemsSource(&RootItems)
                            .OnGenerateRow(this, &SUnifiedScriptAuthoringSurface::GenerateTreeRow)
                            .OnGetChildren(this, &SUnifiedScriptAuthoringSurface::GetTreeChildren)
                            .OnSelectionChanged(this, &SUnifiedScriptAuthoringSurface::HandleSelectionChanged)
                            .OnMouseButtonDoubleClick(this, &SUnifiedScriptAuthoringSurface::HandleTreeDoubleClick)
                            .SelectionMode(ESelectionMode::Single)
                        ]
                    ]
                ]
                + SSplitter::Slot()
                .Value(0.57f)
                [
                    SNew(SBorder)
                    .BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Panel")))
                    .Padding(0.0f)
                    [
                        SNew(SVerticalBox)
                        + SVerticalBox::Slot()
                        .AutoHeight()
                        .Padding(10.0f, 6.0f)
                        [
                            SNew(SHorizontalBox)
                            + SHorizontalBox::Slot()
                            .FillWidth(1.0f)
                            .VAlign(VAlign_Center)
                            [
                                SNew(SVerticalBox)
                                + SVerticalBox::Slot()
                                .AutoHeight()
                                [
                                    SNew(STextBlock)
                                    .Text_Lambda([this]()
                                    {
                                        return FText::FromString(ActiveFunctionId.IsEmpty()
                                            ? TEXT("Visual Behavior")
                                            : ActiveFunctionSignature);
                                    })
                                    .Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
                                ]
                                + SVerticalBox::Slot()
                                .AutoHeight()
                                [
                                    SNew(STextBlock)
                                    .Text_Lambda([this]()
                                    {
                                        return FText::Format(
                                            LOCTEXT("GraphNodeCount", "{0} visual nodes  /  {1} wires  /  Presentation v1"),
                                            ActiveGraphActionCount,
                                            GetVisibleGraphEdgeCount());
                                    })
                                    .Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
                                    .ColorAndOpacity(FLinearColor(0.48f, 0.52f, 0.58f))
                                ]
                            ]
                            + SHorizontalBox::Slot()
                            .AutoWidth()
                            .VAlign(VAlign_Center)
                            [
                                SNew(SButton)
                                .Text(LOCTEXT("FocusGraph", "Focus All"))
                                .ToolTipText(LOCTEXT("FocusGraphTip", "Frame every semantic node in the current function."))
                                .OnClicked(this, &SUnifiedScriptAuthoringSurface::HandleFocusGraph)
                            ]
                        ]
                        + SVerticalBox::Slot()
                        .AutoHeight()
                        [
                            SNew(SSeparator)
                        ]
                        + SVerticalBox::Slot()
                        .FillHeight(1.0f)
                        [
                            SAssignNew(GraphEditor, SGraphEditor)
                            .GraphToEdit(AuthoringGraph.Get())
                            .GraphEvents(GraphEvents)
                            .Appearance(GraphAppearance)
                            .IsEditable(true)
                            .IsEmpty_Lambda([this]()
                            {
                                return AuthoringGraph.IsValid() && AuthoringGraph->Nodes.IsEmpty();
                            })
                            .ShowGraphStateOverlay(false)
                            .AllowConnectionSlicing(false)
                        ]
                    ]
                ]
                + SSplitter::Slot()
                .Value(0.23f)
                [
                    SNew(SBorder)
                    .BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Panel")))
                    .Padding(0.0f)
                    [
                        SNew(SVerticalBox)
                        + SVerticalBox::Slot()
                        .AutoHeight()
                        .Padding(10.0f, 8.0f)
                        [
                            SNew(STextBlock)
                            .Text(LOCTEXT("Details", "DETAILS"))
                            .Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
                            .ColorAndOpacity(FLinearColor(0.62f, 0.66f, 0.72f))
                        ]
                        + SVerticalBox::Slot()
                        .AutoHeight()
                        [
                            SNew(SSeparator)
                        ]
                        + SVerticalBox::Slot()
                        .FillHeight(1.0f)
                        .Padding(10.0f, 8.0f)
                        [
                            SNew(SScrollBox)
                            + SScrollBox::Slot()
                            [
                                SAssignNew(InspectorBox, SVerticalBox)
                            ]
                        ]
                    ]
                ]
            ]
            + SVerticalBox::Slot()
            .AutoHeight()
            .Padding(2.0f, 8.0f, 2.0f, 0.0f)
            [
                SAssignNew(StatusText, STextBlock)
                .Text_Lambda([this]() { return FText::FromString(StatusMessage); })
                .ColorAndOpacity_Lambda([this]()
                {
                    return bStatusError
                        ? FSlateColor(FLinearColor(0.95f, 0.28f, 0.22f))
                        : FSlateColor(FLinearColor(0.32f, 0.72f, 0.46f));
                })
            ]
        ]
    ];

    RegisterActiveTimer(
        1.0f,
        FWidgetActiveTimerDelegate::CreateSP(
            this,
            &SUnifiedScriptAuthoringSurface::HandleAutoReload));
    PathTextBox->SetText(FText::FromString(DemoSurfacePath));
    if (!LoadSurface(DemoSurfacePath))
    {
        RebuildInspector(nullptr);
    }
}

SUnifiedScriptAuthoringSurface::~SUnifiedScriptAuthoringSurface()
{
    PersistCurrentLayout(true);
}

bool SUnifiedScriptAuthoringSurface::LoadSurface(const FString& ConfiguredPath)
{
    FUnifiedScriptAuthoringSurfaceDocument Candidate;
    FString Error;
    if (!FUnifiedScriptAuthoringSurfaceReader::LoadFile(
            ConfiguredPath,
            Candidate,
            Error))
    {
        SetStatus(MoveTemp(Error), true);
        return false;
    }

    Document = MoveTemp(Candidate);
    LoadedSurfacePath = FUnifiedScriptAuthoringSurfaceReader::ResolveConfiguredPath(ConfiguredPath);
    LoadedLayoutPath = FUnifiedScriptGraphLayoutStore::GetLayoutPath(LoadedSurfacePath);
    LoadedSurfaceTimestamp = IFileManager::Get().GetTimeStamp(*LoadedSurfacePath);
    PathTextBox->SetText(FText::FromString(ConfiguredPath));
    RebuildTree();
    SetStatus(
        FString::Printf(
            TEXT("Loaded %s  |  compilation %s"),
            *LoadedSurfacePath,
            *Document.SourceCompilationId),
        false);
    return true;
}

FReply SUnifiedScriptAuthoringSurface::HandleLoad()
{
    LoadedScriptAsset.Reset();
    LoadSurface(PathTextBox->GetText().ToString());
    return FReply::Handled();
}

FReply SUnifiedScriptAuthoringSurface::HandleBrowse()
{
    IDesktopPlatform* DesktopPlatform = FDesktopPlatformModule::Get();
    if (DesktopPlatform == nullptr)
    {
        SetStatus(TEXT("Desktop file dialog is unavailable."), true);
        return FReply::Handled();
    }

    TArray<FString> Files;
    const FString InitialDirectory = LoadedSurfacePath.IsEmpty()
        ? FPaths::ProjectDir()
        : FPaths::GetPath(LoadedSurfacePath);
    if (DesktopPlatform->OpenFileDialog(
            FSlateApplication::Get().FindBestParentWindowHandleForDialogs(nullptr),
            TEXT("Open UnifiedScript Authoring Surface"),
            InitialDirectory,
            TEXT(""),
            TEXT("UnifiedScript Surface (*.surface.json)|*.surface.json|JSON (*.json)|*.json"),
            EFileDialogFlags::None,
            Files) && !Files.IsEmpty())
    {
        LoadedScriptAsset.Reset();
        LoadSurface(Files[0]);
    }
    return FReply::Handled();
}

FReply SUnifiedScriptAuthoringSurface::HandleLoadSelectedAsset()
{
    TArray<FAssetData> SelectedAssets;
    FContentBrowserModule& ContentBrowser =
        FModuleManager::LoadModuleChecked<FContentBrowserModule>(TEXT("ContentBrowser"));
    ContentBrowser.Get().GetSelectedAssets(SelectedAssets);
    for (const FAssetData& AssetData : SelectedAssets)
    {
        if (UUnifiedScriptAsset* Asset = Cast<UUnifiedScriptAsset>(AssetData.GetAsset()))
        {
            const FString SurfacePath = Asset->ResolveAuthoringSurfacePath();
            if (SurfacePath.IsEmpty())
            {
                SetStatus(
                    FString::Printf(TEXT("Selected Script Asset '%s' has no AuthoringSurfacePath."), *Asset->GetPathName()),
                    true);
                return FReply::Handled();
            }
            if (LoadSurface(SurfacePath))
            {
                LoadedScriptAsset = Asset;
                const FString ServerUrl = Asset->ResolveAuthoringServerUrl();
                if (!ServerUrl.IsEmpty())
                {
                    ServerUrlTextBox->SetText(FText::FromString(ServerUrl));
                }
            }
            return FReply::Handled();
        }
    }
    SetStatus(TEXT("Select a UnifiedScript Data Asset in the Content Browser first."), true);
    return FReply::Handled();
}

FReply SUnifiedScriptAuthoringSurface::HandleLoadDemo()
{
    LoadedScriptAsset.Reset();
    ServerUrlTextBox->SetText(FText::FromString(TEXT("http://127.0.0.1:4173")));
    LoadSurface(DemoSurfacePath);
    return FReply::Handled();
}

FReply SUnifiedScriptAuthoringSurface::HandleOpenSource(FUnifiedScriptAuthoringSpan Span)
{
    const FString Path = ResolveSourcePath(Span.FilePath);
    if (Path.IsEmpty())
    {
        SetStatus(FString::Printf(TEXT("Source file cannot be resolved: '%s'."), *Span.FilePath), true);
        return FReply::Handled();
    }
    FSourceCodeNavigation::OpenSourceFile(Path, FMath::Max(1, Span.StartLine), FMath::Max(1, Span.StartColumn));
    return FReply::Handled();
}

FReply SUnifiedScriptAuthoringSurface::HandleCopyId(FString SemanticId)
{
    FPlatformApplicationMisc::ClipboardCopy(*SemanticId);
    SetStatus(FString::Printf(TEXT("Copied semantic ID %s"), *SemanticId), false);
    return FReply::Handled();
}

FReply SUnifiedScriptAuthoringSurface::HandleCopyPatch(
    FUnifiedScriptAuthoringEdit Edit,
    TSharedPtr<SEditableTextBox> ValueEditor)
{
    FString Patch;
    FString Error;
    const FString NewValue = ValueEditor.IsValid()
        ? ValueEditor->GetText().ToString()
        : FString();
    if (!FUnifiedScriptAuthoringSurfaceReader::BuildSemanticPatch(
            Document,
            Edit,
            NewValue,
            Patch,
            Error))
    {
        SetStatus(MoveTemp(Error), true);
        return FReply::Handled();
    }
    FPlatformApplicationMisc::ClipboardCopy(*Patch);
    SetStatus(FString::Printf(TEXT("Copied semantic-patch v0.21 for %s"), *Edit.TargetNodeId), false);
    return FReply::Handled();
}

FReply SUnifiedScriptAuthoringSurface::HandleApplyPatch(
    FUnifiedScriptAuthoringEdit Edit,
    TSharedPtr<SEditableTextBox> ValueEditor)
{
    if (bPatchRequestPending)
    {
        return FReply::Handled();
    }

    FString Patch;
    FString Error;
    const FString NewValue = ValueEditor.IsValid()
        ? ValueEditor->GetText().ToString()
        : FString();
    if (!FUnifiedScriptAuthoringSurfaceReader::BuildSemanticPatch(
            Document,
            Edit,
            NewValue,
            Patch,
            Error))
    {
        SetStatus(MoveTemp(Error), true);
        return FReply::Handled();
    }

    SubmitSemanticPatch(
        MoveTemp(Patch),
        FString::Printf(TEXT("Applying semantic patch to %s"), *Edit.TargetNodeId));
    return FReply::Handled();
}

void SUnifiedScriptAuthoringSurface::HandleDataRewireRequested(
    const UEdGraphPin* OutputPin,
    const UEdGraphPin* InputPin)
{
    if (bPatchRequestPending)
    {
        SetStatus(TEXT("A semantic patch is already in flight; wait for it to finish."), true);
        return;
    }
    const UUnifiedScriptEdGraphNode* SourceNode = OutputPin != nullptr
        ? Cast<UUnifiedScriptEdGraphNode>(OutputPin->GetOwningNode())
        : nullptr;
    const UUnifiedScriptEdGraphNode* TargetNode = InputPin != nullptr
        ? Cast<UUnifiedScriptEdGraphNode>(InputPin->GetOwningNode())
        : nullptr;
    const FUnifiedScriptAuthoringFunction* Function = FindFunction(ActiveFunctionId);
    if (SourceNode == nullptr || TargetNode == nullptr || Function == nullptr)
    {
        SetStatus(TEXT("The active graph endpoints no longer match the loaded Surface."), true);
        return;
    }

    const FString TargetPort = InputPin->PinName.ToString();
    const FUnifiedScriptAuthoringEdge* ExpectedEdge = nullptr;
    for (const FUnifiedScriptAuthoringEdge& Edge : Function->Edges)
    {
        if (Edge.Kind == TEXT("data") && Edge.ToNode == TargetNode->SemanticId &&
            Edge.ToPort == TargetPort)
        {
            if (ExpectedEdge != nullptr)
            {
                SetStatus(TEXT("The selected input has multiple compiler-owned data edges."), true);
                return;
            }
            ExpectedEdge = &Edge;
        }
    }
    if (ExpectedEdge == nullptr)
    {
        SetStatus(TEXT("The selected input has no compiler-owned data edge to replace."), true);
        return;
    }

    FString Patch;
    FString Error;
    if (!FUnifiedScriptAuthoringSurfaceReader::BuildDataEdgeReconnectionPatch(
            Document,
            *Function,
            *ExpectedEdge,
            SourceNode->SemanticId,
            OutputPin->PinName.ToString(),
            Patch,
            Error))
    {
        SetStatus(MoveTemp(Error), true);
        return;
    }

    const bool bComputedSharedValue = OutputPin->LinkedTo.Num() > 0 &&
        (SourceNode->NodeKind == TEXT("binary") || SourceNode->NodeKind == TEXT("unary") ||
            SourceNode->NodeKind == TEXT("cast") || SourceNode->NodeKind == TEXT("conditional"));
    if (bComputedSharedValue)
    {
        FString Plan;
        FString PlanError;
        if (FUnifiedScriptAuthoringSurfaceReader::BuildSharedDataEdgePatchPlan(
                Document,
                *Function,
                *ExpectedEdge,
                SourceNode->SemanticId,
                OutputPin->PinName.ToString(),
                Plan,
                PlanError))
        {
            SubmitAuthoringTransaction(
                MoveTemp(Plan),
                FUnifiedScriptAuthoringWorkbench::BuildSemanticPatchPlanUrl(
                    ServerUrlTextBox->GetText().ToString()),
                FString::Printf(
                    TEXT("Sharing %s through an automatic local temporary"),
                    *SourceNode->DisplayTitle));
            return;
        }
    }

    SubmitSemanticPatch(
        MoveTemp(Patch),
        FString::Printf(
            TEXT("Rewiring %s.%s -> %s.%s"),
            *SourceNode->DisplayTitle,
            *OutputPin->PinName.ToString(),
            *TargetNode->DisplayTitle,
            *TargetPort));
}

void SUnifiedScriptAuthoringSurface::HandleDataDisconnectRequested(
    const UEdGraphPin* OutputPin,
    const UEdGraphPin* InputPin)
{
    if (bPatchRequestPending || OutputPin == nullptr || InputPin == nullptr)
    {
        return;
    }
    const UUnifiedScriptEdGraphNode* TargetNode =
        Cast<UUnifiedScriptEdGraphNode>(InputPin->GetOwningNode());
    const FUnifiedScriptAuthoringFunction* Function = FindFunction(ActiveFunctionId);
    if (TargetNode == nullptr || Function == nullptr)
    {
        SetStatus(TEXT("The selected wire no longer belongs to the active Surface."), true);
        return;
    }
    const FUnifiedScriptAuthoringEdge* ExpectedEdge = Function->Edges.FindByPredicate(
        [TargetNode, InputPin](const FUnifiedScriptAuthoringEdge& Edge)
        {
            return Edge.Kind == TEXT("data") && Edge.ToNode == TargetNode->SemanticId &&
                Edge.ToPort == InputPin->PinName.ToString();
        });
    if (ExpectedEdge == nullptr)
    {
        SetStatus(TEXT("The selected data input has no compiler-owned edge."), true);
        return;
    }
    FString Patch;
    FString Error;
    if (!FUnifiedScriptAuthoringSurfaceReader::BuildDataEdgeDisconnectionPatch(
            Document, *Function, *ExpectedEdge, Patch, Error))
    {
        SetStatus(MoveTemp(Error), true);
        return;
    }
    SubmitSemanticPatch(MoveTemp(Patch), TEXT("Replacing the disconnected input with typed default(...)"));
}

void SUnifiedScriptAuthoringSurface::HandleNodeCreateRequested(
    const FString& ActionId,
    const FVector2D& Location)
{
    if (bPatchRequestPending)
    {
        return;
    }
    const FUnifiedScriptAuthoringFunction* Function = FindFunction(ActiveFunctionId);
    if (Function == nullptr)
    {
        SetStatus(TEXT("Select a function before creating a graph node."), true);
        return;
    }

    FString Patch;
    FString Error;
    bool bBuilt = false;
    if (const FUnifiedScriptReflectedPaletteAction* Reflected =
            ReflectedPaletteActions.Find(ActionId))
    {
        if (Reflected->bEvent)
        {
            const FUnifiedScriptAuthoringAsset* Asset = Document.Assets.FindByPredicate(
                [Function](const FUnifiedScriptAuthoringAsset& Candidate)
                {
                    return Candidate.Functions.ContainsByPredicate(
                        [Function](const FUnifiedScriptAuthoringFunction& CandidateFunction)
                        {
                            return CandidateFunction.Id == Function->Id;
                        });
                });
            if (Asset == nullptr)
            {
                SetStatus(TEXT("The active function has no owning Script Asset."), true);
                return;
            }
            bBuilt = FUnifiedScriptAuthoringSurfaceReader::BuildUnrealEventHandlerInsertionPatch(
                Document,
                *Asset,
                Reflected->HandlerName,
                Reflected->SourceTypeName,
                Reflected->EventPath,
                Reflected->EventParameters,
                Patch,
                Error);
            if (!bBuilt)
            {
                SetStatus(MoveTemp(Error), true);
                return;
            }
            SubmitAuthoringTransaction(
                MoveTemp(Patch),
                FUnifiedScriptAuthoringWorkbench::BuildDeclarationPatchUrl(
                    ServerUrlTextBox.IsValid() ? ServerUrlTextBox->GetText().ToString() : FString()),
                FString::Printf(TEXT("Creating UE Event Entry '%s'"), *Reflected->HandlerName));
            return;
        }

        const FUnifiedScriptAuthoringRegion* Block = ResolveActiveBlockRegion(*Function);
        if (Block == nullptr)
        {
            SetStatus(TEXT("Select a statement inside an editable block first."), true);
            return;
        }
        if (Reflected->bInvocation)
        {
            bBuilt = FUnifiedScriptAuthoringSurfaceReader::BuildMemberInvocationInsertionPatch(
                Document,
                *Function,
                *Block,
                Reflected->Receiver,
                Reflected->MemberName,
                Reflected->ResultTypeName,
                Reflected->ResultTypeId,
                Reflected->Arguments,
                Patch,
                Error);
        }
        else
        {
            bBuilt = FUnifiedScriptAuthoringSurfaceReader::BuildMemberAccessInsertionPatch(
                Document,
                *Function,
                *Block,
                Reflected->Receiver,
                Reflected->MemberName,
                Reflected->ResultTypeName,
                Reflected->ResultTypeId,
                Patch,
                Error);
        }
    }
    else
    {
        const FUnifiedScriptAuthoringRegion* Block = ResolveActiveBlockRegion(*Function);
        if (Block == nullptr)
        {
            SetStatus(TEXT("Select a statement inside an editable block first."), true);
            return;
        }
        bBuilt = FUnifiedScriptAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
            Document, *Function, *Block, ActionId, Patch, Error);
    }
    if (!bBuilt)
    {
        SetStatus(MoveTemp(Error), true);
        return;
    }
    SubmitSemanticPatch(
        MoveTemp(Patch),
        FString::Printf(TEXT("Creating graph node '%s'"), *ActionId));
}

void SUnifiedScriptAuthoringSurface::HandleStatementDeleteRequested(const UEdGraphNode* Node)
{
    if (bPatchRequestPending || Node == nullptr)
    {
        return;
    }
    const UUnifiedScriptEdGraphNode* ScriptNode = Cast<UUnifiedScriptEdGraphNode>(Node);
    const FUnifiedScriptAuthoringFunction* Function = FindFunction(ActiveFunctionId);
    const FUnifiedScriptAuthoringAction* Statement = ScriptNode != nullptr && Function != nullptr
        ? Function->FindAction(ScriptNode->SemanticId)
        : nullptr;
    if (Function == nullptr || Statement == nullptr)
    {
        SetStatus(TEXT("The selected node no longer belongs to the active Surface."), true);
        return;
    }
    FString Patch;
    FString Error;
    if (!FUnifiedScriptAuthoringSurfaceReader::BuildStatementDeletionPatch(
            Document, *Function, *Statement, Patch, Error))
    {
        SetStatus(MoveTemp(Error), true);
        return;
    }
    SubmitSemanticPatch(MoveTemp(Patch), FString::Printf(TEXT("Deleting %s"), *Statement->Title));
}

void SUnifiedScriptAuthoringSurface::SubmitSemanticPatch(FString Patch, FString Activity)
{
    if (bPatchRequestPending)
    {
        SetStatus(TEXT("A semantic patch is already in flight; wait for it to finish."), true);
        return;
    }

    const FString Url = FUnifiedScriptAuthoringWorkbench::BuildSemanticPatchUrl(
        ServerUrlTextBox->GetText().ToString());
    SubmitAuthoringTransaction(MoveTemp(Patch), Url, MoveTemp(Activity));
}

void SUnifiedScriptAuthoringSurface::SubmitAuthoringTransaction(
    FString Payload,
    FString Url,
    FString Activity)
{
    if (bPatchRequestPending)
    {
        SetStatus(TEXT("An authoring transaction is already in flight; wait for it to finish."), true);
        return;
    }
    if (Url.IsEmpty())
    {
        SetStatus(TEXT("Enter a Workbench loopback URL before applying a patch."), true);
        return;
    }

    const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request = FHttpModule::Get().CreateRequest();
    Request->SetURL(Url);
    Request->SetVerb(TEXT("POST"));
    Request->SetHeader(TEXT("Content-Type"), TEXT("application/json; charset=utf-8"));
    Request->SetContentAsString(Payload);
    const TWeakPtr<SUnifiedScriptAuthoringSurface> WeakThis = SharedThis(this);
    Request->OnProcessRequestComplete().BindLambda(
        [WeakThis](FHttpRequestPtr, FHttpResponsePtr Response, bool bSucceeded)
        {
            if (const TSharedPtr<SUnifiedScriptAuthoringSurface> Pinned = WeakThis.Pin())
            {
                Pinned->HandlePatchResponse(
                    bSucceeded && Response.IsValid(),
                    Response.IsValid() ? Response->GetResponseCode() : 0,
                    Response.IsValid() ? Response->GetContentAsString() : FString());
            }
        });

    bPatchRequestPending = true;
    SetStatus(FString::Printf(TEXT("%s via %s ..."), *Activity, *Url), false);
    if (!Request->ProcessRequest())
    {
        bPatchRequestPending = false;
        SetStatus(TEXT("The Workbench HTTP request could not be started."), true);
    }
}

void SUnifiedScriptAuthoringSurface::HandlePatchResponse(
    bool bRequestSucceeded,
    int32 StatusCode,
    FString ResponseJson)
{
    bPatchRequestPending = false;
    if (!bRequestSucceeded || StatusCode < 200 || StatusCode >= 300)
    {
        SetStatus(
            bRequestSucceeded
                ? FUnifiedScriptAuthoringWorkbench::ParseErrorResponse(ResponseJson, StatusCode)
                : TEXT("The Workbench loopback request failed; verify that the authoring host is running."),
            true);
        return;
    }

    FUnifiedScriptAuthoringWorkspaceResult Workspace;
    FString Error;
    if (!FUnifiedScriptAuthoringWorkbench::ParseWorkspaceResponse(
            ResponseJson,
            Workspace,
            Error))
    {
        SetStatus(MoveTemp(Error), true);
        return;
    }

    Document = MoveTemp(Workspace.Surface);
    if (!LoadedSurfacePath.IsEmpty())
    {
        LoadedSurfaceTimestamp = IFileManager::Get().GetTimeStamp(*LoadedSurfacePath);
    }
    RebuildTree();

    int32 Matched = 0;
    int32 Reloaded = 0;
    int32 Failed = 0;
    if (bReloadMatchingComponents)
    {
        ReloadMatchingComponents(Matched, Reloaded, Failed);
    }
    SetStatus(
        FString::Printf(
            TEXT("Applied revision %lld / Workbench generation %lld; Surface refreshed; components %d matched, %d updated, %d failed."),
            Workspace.Revision,
            Workspace.Generation,
            Matched,
            Reloaded,
            Failed),
        Failed > 0);
}

void SUnifiedScriptAuthoringSurface::ReloadMatchingComponents(
    int32& OutMatched,
    int32& OutReloaded,
    int32& OutFailed)
{
    OutMatched = 0;
    OutReloaded = 0;
    OutFailed = 0;
    const UUnifiedScriptAsset* PreferredAsset = LoadedScriptAsset.Get();
    for (TObjectIterator<UUnifiedScriptComponent> It; It; ++It)
    {
        UUnifiedScriptComponent* Component = *It;
        if (!IsValid(Component) || Component->IsTemplate() || Component->GetWorld() == nullptr)
        {
            continue;
        }

        const UUnifiedScriptAsset* ComponentAsset = Component->ScriptAsset;
        const bool bMatchesSelectedAsset = PreferredAsset != nullptr &&
            (ComponentAsset == PreferredAsset ||
                Component->ActiveScriptAssetPath == PreferredAsset->GetPathName());
        bool bMatchesSurfacePath = false;
        if (PreferredAsset == nullptr && ComponentAsset != nullptr && !LoadedSurfacePath.IsEmpty())
        {
            bMatchesSurfacePath =
                FUnifiedScriptAuthoringSurfaceReader::ResolveConfiguredPath(
                    ComponentAsset->ResolveAuthoringSurfacePath()) == LoadedSurfacePath;
        }
        if (!bMatchesSelectedAsset && !bMatchesSurfacePath)
        {
            continue;
        }

        ++OutMatched;
        const bool bUpdated = Component->bScriptRunning
            ? Component->ReloadScript()
            : Component->RefreshScriptState();
        if (bUpdated)
        {
            ++OutReloaded;
        }
        else
        {
            ++OutFailed;
        }
    }
}

EActiveTimerReturnType SUnifiedScriptAuthoringSurface::HandleAutoReload(
    double CurrentTime,
    float DeltaTime)
{
    if (bPendingGraphFocus && GraphEditor.IsValid())
    {
        GraphEditor->ZoomToFit(false);
        bPendingGraphFocus = false;
    }
    if (bAutoReload && !LoadedSurfacePath.IsEmpty())
    {
        const FDateTime Timestamp = IFileManager::Get().GetTimeStamp(*LoadedSurfacePath);
        if (Timestamp != FDateTime::MinValue() && Timestamp != LoadedSurfaceTimestamp)
        {
            LoadSurface(LoadedSurfacePath);
        }
    }
    PersistCurrentLayout(true);
    return EActiveTimerReturnType::Continue;
}

TSharedRef<ITableRow> SUnifiedScriptAuthoringSurface::GenerateTreeRow(
    FUnifiedScriptSurfaceTreeItemPtr Item,
    const TSharedRef<STableViewBase>& OwnerTable) const
{
    return SNew(STableRow<FUnifiedScriptSurfaceTreeItemPtr>, OwnerTable)
        .Padding(FMargin(2.0f, 3.0f))
        [
            SNew(SHorizontalBox)
            + SHorizontalBox::Slot()
            .AutoWidth()
            .VAlign(VAlign_Center)
            .Padding(0.0f, 0.0f, 7.0f, 0.0f)
            [
                SNew(SBorder)
                .BorderImage(FAppStyle::GetBrush(TEXT("WhiteBrush")))
                .BorderBackgroundColor(GetKindColor(Item->Kind))
                .Padding(FMargin(3.0f, 7.0f))
            ]
            + SHorizontalBox::Slot()
            .FillWidth(1.0f)
            [
                SNew(SVerticalBox)
                + SVerticalBox::Slot()
                .AutoHeight()
                [
                    SNew(STextBlock)
                    .Text(FText::FromString(Item->Label))
                    .HighlightText(PathTextBox.IsValid() ? PathTextBox->GetText() : FText::GetEmpty())
                ]
                + SVerticalBox::Slot()
                .AutoHeight()
                [
                    SNew(STextBlock)
                    .Text(FText::FromString(Item->Detail))
                    .Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
                    .ColorAndOpacity(FLinearColor(0.52f, 0.55f, 0.6f))
                ]
            ]
        ];
}

void SUnifiedScriptAuthoringSurface::GetTreeChildren(
    FUnifiedScriptSurfaceTreeItemPtr Item,
    TArray<FUnifiedScriptSurfaceTreeItemPtr>& OutChildren) const
{
    if (Item.IsValid())
    {
        OutChildren.Append(Item->Children);
    }
}

void SUnifiedScriptAuthoringSurface::HandleSelectionChanged(
    FUnifiedScriptSurfaceTreeItemPtr Item,
    ESelectInfo::Type SelectInfo)
{
    if (Item.IsValid() && Item->Kind == EUnifiedScriptSurfaceTreeItemKind::Function)
    {
        RebuildGraph(Item);
    }
    else if (Item.IsValid() && !ActiveFunctionId.IsEmpty())
    {
        const FUnifiedScriptAuthoringFunction* Function = FindFunction(ActiveFunctionId);
        if (Function != nullptr)
        {
            for (const FUnifiedScriptAuthoringRegion& Region : Function->Regions)
            {
                if (Region.Id == Item->SemanticId ||
                    Region.DirectStatementIds.Contains(Item->SemanticId))
                {
                    ActiveBlockRegionId = Region.Id;
                    if (AuthoringGraph.IsValid())
                    {
                        AuthoringGraph->ActiveBlockRegionId = ActiveBlockRegionId;
                    }
                    break;
                }
            }
        }
    }
    RebuildInspector(Item);
}

void SUnifiedScriptAuthoringSurface::HandleTreeDoubleClick(
    FUnifiedScriptSurfaceTreeItemPtr Item)
{
    if (Item.IsValid() && Item->Span.IsValid())
    {
        HandleOpenSource(Item->Span);
    }
}

void SUnifiedScriptAuthoringSurface::HandleGraphSelectionChanged(
    const FGraphPanelSelectionSet& Selection)
{
    for (UObject* Object : Selection)
    {
        if (const UUnifiedScriptEdGraphNode* Node = Cast<UUnifiedScriptEdGraphNode>(Object))
        {
            if (const FUnifiedScriptAuthoringFunction* Function = FindFunction(ActiveFunctionId))
            {
                const FUnifiedScriptAuthoringAction* Action = Function->FindAction(Node->SemanticId);
                for (const FUnifiedScriptAuthoringRegion& Region : Function->Regions)
                {
                    if (Region.DirectStatementIds.Contains(Node->SemanticId) ||
                        (Action != nullptr && Region.Id == Action->RegionId &&
                            (Region.Kind == TEXT("block") || Region.Kind == TEXT("then") ||
                                Region.Kind == TEXT("else") || Region.Kind == TEXT("loopBody"))))
                    {
                        ActiveBlockRegionId = Region.Id;
                        if (AuthoringGraph.IsValid())
                        {
                            AuthoringGraph->ActiveBlockRegionId = ActiveBlockRegionId;
                        }
                        break;
                    }
                }
            }
            RebuildInspector(FindSurfaceItem(Node->SemanticId));
            return;
        }
    }
}

void SUnifiedScriptAuthoringSurface::HandleGraphNodeDoubleClicked(UEdGraphNode* Node)
{
    if (const UUnifiedScriptEdGraphNode* UnifiedScriptNode = Cast<UUnifiedScriptEdGraphNode>(Node))
    {
        const FUnifiedScriptSurfaceTreeItemPtr Item = FindSurfaceItem(UnifiedScriptNode->SemanticId);
        if (Item.IsValid() && Item->Span.IsValid())
        {
            HandleOpenSource(Item->Span);
        }
    }
}

void SUnifiedScriptAuthoringSurface::RebuildTree()
{
    const FString PreferredFunctionId = ActiveFunctionId;
    FUnifiedScriptSurfaceTreeItemPtr FirstFunctionItem;
    RootItems.Reset();
    SurfaceItemsById.Reset();
    for (const FUnifiedScriptAuthoringAsset& Asset : Document.Assets)
    {
        FUnifiedScriptSurfaceTreeItemPtr AssetItem = MakeShared<FUnifiedScriptSurfaceTreeItem>();
        AssetItem->Kind = EUnifiedScriptSurfaceTreeItemKind::Asset;
        AssetItem->Label = Asset.AssetPath;
        AssetItem->Detail = Asset.QualifiedName;
        AssetItem->SemanticId = Asset.Id;
        AssetItem->Span = Asset.Span;
        SurfaceItemsById.Add(AssetItem->SemanticId, AssetItem);

        if (!Asset.States.IsEmpty())
        {
            FUnifiedScriptSurfaceTreeItemPtr StateGroup = MakeShared<FUnifiedScriptSurfaceTreeItem>();
            StateGroup->Kind = EUnifiedScriptSurfaceTreeItemKind::Group;
            StateGroup->Label = FString::Printf(TEXT("State (%d)"), Asset.States.Num());
            StateGroup->Detail = TEXT("C#-owned declarations");
            for (const FUnifiedScriptAuthoringState& State : Asset.States)
            {
                FUnifiedScriptSurfaceTreeItemPtr StateItem = MakeShared<FUnifiedScriptSurfaceTreeItem>();
                StateItem->Kind = EUnifiedScriptSurfaceTreeItemKind::State;
                StateItem->Label = State.Name;
                StateItem->Detail = FString::Printf(
                    TEXT("%s = %s%s"),
                    *State.TypeDisplayName,
                    *State.DefaultValue,
                    State.Exposure.bInstanceEditable ? TEXT("  [Editable]") : TEXT(""));
                StateItem->SemanticId = State.Id;
                StateItem->Category = State.Exposure.Category;
                StateItem->Span = State.Span;
                SurfaceItemsById.Add(StateItem->SemanticId, StateItem);
                StateGroup->Children.Add(MoveTemp(StateItem));
            }
            AssetItem->Children.Add(MoveTemp(StateGroup));
        }

        if (!Asset.Functions.IsEmpty())
        {
            FUnifiedScriptSurfaceTreeItemPtr FunctionGroup = MakeShared<FUnifiedScriptSurfaceTreeItem>();
            FunctionGroup->Kind = EUnifiedScriptSurfaceTreeItemKind::Group;
            FunctionGroup->Label = FString::Printf(TEXT("Behavior (%d)"), Asset.Functions.Num());
            FunctionGroup->Detail = TEXT("Visual Profile functions");
            for (const FUnifiedScriptAuthoringFunction& Function : Asset.Functions)
            {
                FUnifiedScriptSurfaceTreeItemPtr FunctionItem = MakeShared<FUnifiedScriptSurfaceTreeItem>();
                FunctionItem->Kind = EUnifiedScriptSurfaceTreeItemKind::Function;
                FunctionItem->Label = Function.Name;
                FunctionItem->Detail = BuildFunctionSignature(Function);
                FunctionItem->SemanticId = Function.Id;
                SurfaceItemsById.Add(FunctionItem->SemanticId, FunctionItem);
                if (!FirstFunctionItem.IsValid())
                {
                    FirstFunctionItem = FunctionItem;
                }
                if (const FUnifiedScriptAuthoringRegion* Root = Function.FindRegion(Function.RootRegionId))
                {
                    FunctionItem->Span = Root->Span;
                    AddRegionTree(Function, *Root, FunctionItem);
                }
                FunctionGroup->Children.Add(MoveTemp(FunctionItem));
            }
            AssetItem->Children.Add(MoveTemp(FunctionGroup));
        }
        RootItems.Add(MoveTemp(AssetItem));
    }

    TreeView->RequestTreeRefresh();
    TFunction<void(const FUnifiedScriptSurfaceTreeItemPtr&)> ExpandAll;
    ExpandAll = [this, &ExpandAll](const FUnifiedScriptSurfaceTreeItemPtr& Item)
        {
            TreeView->SetItemExpansion(Item, true);
            for (const FUnifiedScriptSurfaceTreeItemPtr& Child : Item->Children)
            {
                ExpandAll(Child);
            }
        };
    for (const FUnifiedScriptSurfaceTreeItemPtr& Root : RootItems)
    {
        ExpandAll(Root);
    }

    FUnifiedScriptSurfaceTreeItemPtr ItemToSelect;
    if (!PreferredFunctionId.IsEmpty())
    {
        const FUnifiedScriptSurfaceTreeItemPtr* Existing = SurfaceItemsById.Find(PreferredFunctionId);
        if (Existing != nullptr && (*Existing)->Kind == EUnifiedScriptSurfaceTreeItemKind::Function)
        {
            ItemToSelect = *Existing;
        }
    }
    if (!ItemToSelect.IsValid())
    {
        ItemToSelect = FirstFunctionItem;
    }
    if (!ItemToSelect.IsValid() && !RootItems.IsEmpty())
    {
        ItemToSelect = RootItems[0];
    }
    if (ItemToSelect.IsValid())
    {
        TreeView->SetSelection(ItemToSelect);
    }
    else
    {
        RebuildInspector(nullptr);
    }
}

void SUnifiedScriptAuthoringSurface::AddRegionTree(
    const FUnifiedScriptAuthoringFunction& Function,
    const FUnifiedScriptAuthoringRegion& Region,
    const FUnifiedScriptSurfaceTreeItemPtr& Parent)
{
    FUnifiedScriptSurfaceTreeItemPtr RegionItem = MakeShared<FUnifiedScriptSurfaceTreeItem>();
    RegionItem->Kind = EUnifiedScriptSurfaceTreeItemKind::Region;
    RegionItem->Label = Region.Kind.ToUpper();
    RegionItem->Detail = Region.bImplicit ? TEXT("implicit region") : TEXT("structured region");
    RegionItem->SemanticId = Region.Id;
    RegionItem->Category = Region.Kind;
    RegionItem->Span = Region.Span;
    SurfaceItemsById.Add(RegionItem->SemanticId, RegionItem);

    struct FOrderedChild
    {
        int32 Line = 0;
        bool bRegion = false;
        FString Id;
    };
    TArray<FOrderedChild> Ordered;
    for (const FString& ActionId : Region.DirectActionIds)
    {
        if (const FUnifiedScriptAuthoringAction* Action = Function.FindAction(ActionId))
        {
            Ordered.Add({Action->Span.StartLine, false, ActionId});
        }
    }
    for (const FString& ChildId : Region.ChildRegionIds)
    {
        if (const FUnifiedScriptAuthoringRegion* Child = Function.FindRegion(ChildId))
        {
            Ordered.Add({Child->Span.StartLine, true, ChildId});
        }
    }
    Ordered.Sort([](const FOrderedChild& Left, const FOrderedChild& Right)
    {
        if (Left.Line != Right.Line)
        {
            return Left.Line < Right.Line;
        }
        return Left.bRegion < Right.bRegion;
    });
    for (const FOrderedChild& Child : Ordered)
    {
        if (Child.bRegion)
        {
            if (const FUnifiedScriptAuthoringRegion* ChildRegion = Function.FindRegion(Child.Id))
            {
                AddRegionTree(Function, *ChildRegion, RegionItem);
            }
        }
        else if (const FUnifiedScriptAuthoringAction* Action = Function.FindAction(Child.Id))
        {
            FUnifiedScriptSurfaceTreeItemPtr ActionItem = MakeShared<FUnifiedScriptSurfaceTreeItem>();
            ActionItem->Kind = EUnifiedScriptSurfaceTreeItemKind::Action;
            ActionItem->Label = Action->Title;
            ActionItem->Detail = Action->Summary;
            ActionItem->SemanticId = Action->Id;
            ActionItem->Category = Action->Category;
            ActionItem->GraphKind = Action->GraphKind;
            ActionItem->Span = Action->Span;
            ActionItem->Edits = Action->EditCapabilities;
            SurfaceItemsById.Add(ActionItem->SemanticId, ActionItem);
            RegionItem->Children.Add(MoveTemp(ActionItem));
        }
    }
    if (Parent->Kind == EUnifiedScriptSurfaceTreeItemKind::Region)
    {
        Parent->Children.Add(MoveTemp(RegionItem));
    }
}

void SUnifiedScriptAuthoringSurface::RebuildGraph(
    const FUnifiedScriptSurfaceTreeItemPtr& FunctionItem)
{
    if (!AuthoringGraph.IsValid() || !FunctionItem.IsValid())
    {
        return;
    }

    PersistCurrentLayout(true);

    if (GraphEditor.IsValid())
    {
        GraphEditor->ClearSelectionSet();
    }
    AuthoringGraph->Nodes.Reset();
    GraphNodesBySemanticId.Reset();
    PersistedNodePositions.Reset();
    ActiveFunctionId = FunctionItem->SemanticId;
    ActiveBlockRegionId.Reset();
    ActiveFunctionSignature = FunctionItem->Detail;
    ActiveGraphActionCount = 0;

    const FUnifiedScriptAuthoringFunction* Function = FindFunction(ActiveFunctionId);
    if (Function != nullptr)
    {
        TMap<FString, FVector2D> LayoutPositions;
        FString LayoutError;
        if (!LoadedLayoutPath.IsEmpty() &&
            !FUnifiedScriptGraphLayoutStore::LoadFunction(
                LoadedLayoutPath, Function->Id, LayoutPositions, LayoutError))
        {
            SetStatus(MoveTemp(LayoutError), true);
        }
        if (const FUnifiedScriptAuthoringRegion* Root = Function->FindRegion(Function->RootRegionId))
        {
            for (const FString& ChildId : Root->ChildRegionIds)
            {
                const FUnifiedScriptAuthoringRegion* Child = Function->FindRegion(ChildId);
                if (Child != nullptr && Child->Kind == TEXT("block"))
                {
                    ActiveBlockRegionId = Child->Id;
                    break;
                }
            }
        }
        AuthoringGraph->ActiveBlockRegionId = ActiveBlockRegionId;
        RebuildNodePalette(*Function);
        TSet<FString> VisibleActionIds;
        for (const FUnifiedScriptAuthoringAction& Action : Function->Actions)
        {
            if (!FUnifiedScriptGraphPresentation::ShouldInlineAction(Action, *Function))
            {
                VisibleActionIds.Add(Action.Id);
            }
        }

        TMultiMap<FString, FString> IncomingNodes;
        for (const FUnifiedScriptAuthoringEdge& Edge : Function->Edges)
        {
            if (VisibleActionIds.Contains(Edge.FromNode) && VisibleActionIds.Contains(Edge.ToNode))
            {
                IncomingNodes.Add(Edge.ToNode, Edge.FromNode);
            }
        }

        TMap<FString, int32> CachedDepths;
        TSet<FString> Visiting;
        TFunction<int32(const FString&)> ComputeDepth;
        ComputeDepth = [&IncomingNodes, &CachedDepths, &Visiting, &ComputeDepth](
            const FString& NodeId) -> int32
        {
            if (const int32* Cached = CachedDepths.Find(NodeId))
            {
                return *Cached;
            }
            if (Visiting.Contains(NodeId))
            {
                return 0;
            }

            Visiting.Add(NodeId);
            int32 Depth = 0;
            TArray<FString> Sources;
            IncomingNodes.MultiFind(NodeId, Sources);
            for (const FString& SourceId : Sources)
            {
                Depth = FMath::Max(Depth, ComputeDepth(SourceId) + 1);
            }
            Visiting.Remove(NodeId);
            CachedDepths.Add(NodeId, Depth);
            return Depth;
        };

        TMap<int32, int32> RowsByDepth;
        for (const FUnifiedScriptAuthoringAction& Action : Function->Actions)
        {
            if (!VisibleActionIds.Contains(Action.Id))
            {
                continue;
            }
            const int32 Depth = ComputeDepth(Action.Id);
            int32& Row = RowsByDepth.FindOrAdd(Depth);
            const FVector2D* SavedPosition = LayoutPositions.Find(Action.Id);
            AddGraphNode(
                FindSurfaceItem(Action.Id),
                Action,
                *Function,
                SavedPosition != nullptr ? FMath::RoundToInt(SavedPosition->X) : Depth * 330,
                SavedPosition != nullptr ? FMath::RoundToInt(SavedPosition->Y) : Row * 180);
            ++Row;
        }

        for (const FUnifiedScriptAuthoringEdge& Edge : Function->Edges)
        {
            const TWeakObjectPtr<UUnifiedScriptEdGraphNode>* FromWeak =
                GraphNodesBySemanticId.Find(Edge.FromNode);
            const TWeakObjectPtr<UUnifiedScriptEdGraphNode>* ToWeak =
                GraphNodesBySemanticId.Find(Edge.ToNode);
            UUnifiedScriptEdGraphNode* FromNode = FromWeak != nullptr ? FromWeak->Get() : nullptr;
            UUnifiedScriptEdGraphNode* ToNode = ToWeak != nullptr ? ToWeak->Get() : nullptr;
            UEdGraphPin* FromPin = FromNode != nullptr
                ? FromNode->FindPin(Edge.FromPort, EGPD_Output)
                : nullptr;
            UEdGraphPin* ToPin = ToNode != nullptr
                ? ToNode->FindPin(Edge.ToPort, EGPD_Input)
                : nullptr;
            if (FromPin != nullptr && ToPin != nullptr)
            {
                FromPin->MakeLinkTo(ToPin, false);
            }
        }
        ActiveGraphActionCount = AuthoringGraph->Nodes.Num();
        for (const UEdGraphNode* Node : AuthoringGraph->Nodes)
        {
            if (const UUnifiedScriptEdGraphNode* ScriptNode = Cast<UUnifiedScriptEdGraphNode>(Node))
            {
                PersistedNodePositions.Add(
                    ScriptNode->SemanticId,
                    FVector2D(ScriptNode->NodePosX, ScriptNode->NodePosY));
            }
        }
    }

    AuthoringGraph->NotifyGraphChanged();
    if (GraphEditor.IsValid())
    {
        GraphEditor->NotifyGraphChanged();
        bPendingGraphFocus = !AuthoringGraph->Nodes.IsEmpty();
    }
}

UUnifiedScriptEdGraphNode* SUnifiedScriptAuthoringSurface::AddGraphNode(
    const FUnifiedScriptSurfaceTreeItemPtr& Item,
    const FUnifiedScriptAuthoringAction& Action,
    const FUnifiedScriptAuthoringFunction& Function,
    int32 NodePosX,
    int32 NodePosY)
{
    if (!AuthoringGraph.IsValid() || !Item.IsValid())
    {
        return nullptr;
    }

    UUnifiedScriptEdGraphNode* Node = NewObject<UUnifiedScriptEdGraphNode>(
        AuthoringGraph.Get(),
        NAME_None,
        RF_Transient);
    Node->CreateNewGuid();
    Node->SemanticId = Item->SemanticId;
    Node->DisplayTitle = FUnifiedScriptGraphPresentation::GetNodeTitle(Action);
    Node->Summary = Item->Detail;
    Node->NodeKind = Item->Kind == EUnifiedScriptSurfaceTreeItemKind::Region
        ? TEXT("region")
        : Item->GraphKind;
    Node->Category = Item->Category;
    Node->SourceDisplay = Item->Span.ToDisplayString();
    Node->bShowSummary = FUnifiedScriptGraphPresentation::ShouldShowSummary(Action);
    Node->bCompactPresentation = FUnifiedScriptGraphPresentation::IsCompactNode(Action);
    Node->bCanDelete = Function.Regions.ContainsByPredicate([&Action](
        const FUnifiedScriptAuthoringRegion& Region)
    {
        return Region.DirectStatementIds.Contains(Action.Id);
    });
    Node->NodePosX = NodePosX;
    Node->NodePosY = NodePosY;
    Node->AllocateDefaultPins();
    for (const FUnifiedScriptAuthoringPort& Port : Action.Ports)
    {
        FEdGraphPinType PinType;
        PinType.PinCategory = Port.IsControl() ? TEXT("exec") : TEXT("data");
        PinType.PinSubCategory = Port.TypeDisplayName.IsEmpty()
            ? NAME_None
            : FName(*Port.TypeDisplayName);
        UEdGraphPin* Pin = Node->CreatePin(
            Port.IsInput() ? EGPD_Input : EGPD_Output,
            PinType,
            FName(*Port.Name));
        Pin->PinFriendlyName = FText::FromString(
            FUnifiedScriptGraphPresentation::GetPinLabel(Action, Port, Function));
        Pin->PinToolTip = FUnifiedScriptGraphPresentation::GetPinTooltip(
            Action,
            Port,
            Function);
        Pin->bHidden = FUnifiedScriptGraphPresentation::ShouldHidePin(
            Action,
            Port,
            Function);
    }
    AuthoringGraph->AddNode(Node, false, false);
    GraphNodesBySemanticId.Add(Item->SemanticId, Node);
    return Node;
}

const FUnifiedScriptAuthoringFunction* SUnifiedScriptAuthoringSurface::FindFunction(
    const FString& FunctionId) const
{
    for (const FUnifiedScriptAuthoringAsset& Asset : Document.Assets)
    {
        for (const FUnifiedScriptAuthoringFunction& Function : Asset.Functions)
        {
            if (Function.Id == FunctionId)
            {
                return &Function;
            }
        }
    }
    return nullptr;
}

const FUnifiedScriptAuthoringRegion* SUnifiedScriptAuthoringSurface::FindRegion(
    const FUnifiedScriptAuthoringFunction& Function,
    const FString& RegionId) const
{
    return Function.FindRegion(RegionId);
}

const FUnifiedScriptAuthoringRegion* SUnifiedScriptAuthoringSurface::ResolveActiveBlockRegion(
    const FUnifiedScriptAuthoringFunction& Function) const
{
    const FUnifiedScriptAuthoringRegion* Region = FindRegion(Function, ActiveBlockRegionId);
    if (Region != nullptr && !Region->SourceNodeId.IsEmpty())
    {
        return Region;
    }
    const FUnifiedScriptAuthoringRegion* Root = FindRegion(Function, Function.RootRegionId);
    if (Root != nullptr)
    {
        for (const FString& ChildId : Root->ChildRegionIds)
        {
            const FUnifiedScriptAuthoringRegion* Child = FindRegion(Function, ChildId);
            if (Child != nullptr && Child->Kind == TEXT("block"))
            {
                return Child;
            }
        }
    }
    return nullptr;
}

void SUnifiedScriptAuthoringSurface::RebuildNodePalette(
    const FUnifiedScriptAuthoringFunction& Function)
{
    if (!AuthoringGraph.IsValid())
    {
        return;
    }
    AuthoringGraph->PaletteActions.Reset();
    const auto HasBoundType = [&Function](const FString& DisplayName)
    {
        return Function.Parameters.ContainsByPredicate(
                [&DisplayName](const FUnifiedScriptAuthoringParameter& Parameter)
                {
                    return Parameter.TypeDisplayName == DisplayName && !Parameter.TypeId.IsEmpty();
                }) ||
            Function.Actions.ContainsByPredicate(
                [&DisplayName](const FUnifiedScriptAuthoringAction& Action)
                {
                    return Action.Ports.ContainsByPredicate(
                        [&DisplayName](const FUnifiedScriptAuthoringPort& Port)
                        {
                            return !Port.IsControl() && Port.TypeDisplayName == DisplayName &&
                                !Port.TypeId.IsEmpty();
                        });
                });
    };
    if (HasBoundType(TEXT("int")))
    {
        AuthoringGraph->PaletteActions.Add(
            {TEXT("local.int"), TEXT("Variables"), TEXT("Integer Local"),
                TEXT("Insert a typed int local initialized to 0.")});
    }
    int32 IntParameterCount = 0;
    for (const FUnifiedScriptAuthoringParameter& Parameter : Function.Parameters)
    {
        if (Parameter.TypeDisplayName == TEXT("int") && !Parameter.TypeId.IsEmpty())
        {
            ++IntParameterCount;
        }
    }
    if (IntParameterCount >= 2)
    {
        AuthoringGraph->PaletteActions.Add(
            {TEXT("value.add"), TEXT("Math|Integer"), TEXT("Add Expression"),
                TEXT("Insert a typed local containing the sum of two int parameters.")});
    }
    if (HasBoundType(TEXT("bool")))
    {
        AuthoringGraph->PaletteActions.Add(
            {TEXT("flow.branch"), TEXT("Flow Control"), TEXT("Branch"),
                TEXT("Insert a structured C# if block with a true condition.")});
        AuthoringGraph->PaletteActions.Add(
            {TEXT("flow.while"), TEXT("Flow Control"), TEXT("While Loop"),
                TEXT("Insert a structured C# while block with a true condition.")});
    }
    if (Function.Actions.ContainsByPredicate(
            [](const FUnifiedScriptAuthoringAction& Action)
            {
                const FUnifiedScriptAuthoringPort* ReturnValue =
                    Action.GraphKind == TEXT("return")
                    ? Action.FindPort(TEXT("value"), TEXT("input"))
                    : nullptr;
                return ReturnValue != nullptr && !ReturnValue->TypeId.IsEmpty();
            }))
    {
        AuthoringGraph->PaletteActions.Add(
            {TEXT("flow.return"), TEXT("Flow Control"), TEXT("Return"),
                TEXT("Insert a typed return default(T) at the end of the selected block.")});
    }
    ReflectedPaletteActions.Reset();

    FString ConfiguredCatalogPath;
    if (const UUnifiedScriptAsset* ScriptAsset = LoadedScriptAsset.Get())
    {
        ConfiguredCatalogPath = ScriptAsset->ResolveApiNodeCatalogPath();
    }
    if (ConfiguredCatalogPath.IsEmpty())
    {
        ConfiguredCatalogPath =
            TEXT("{ProjectDir}/../samples/Unreal/UEUnifiedScriptRuntime.reflection.json");
    }
    const FString ManifestPath = FUnifiedScriptAuthoringSurfaceReader::ResolveConfiguredPath(
        ConfiguredCatalogPath);
    FString ManifestJson;
    TSharedPtr<FJsonObject> Manifest;
    if (!FFileHelper::LoadFileToString(ManifestJson, *ManifestPath) ||
        !FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(ManifestJson), Manifest) ||
        !Manifest.IsValid())
    {
        return;
    }

    const auto TypeDisplayName = [](const TSharedPtr<FJsonObject>& Type) -> FString
    {
        FString Kind;
        if (!Type.IsValid() || !Type->TryGetStringField(TEXT("kind"), Kind))
        {
            return FString();
        }
        if (Kind == TEXT("boolean")) return TEXT("bool");
        if (Kind == TEXT("int32")) return TEXT("int");
        if (Kind == TEXT("int64")) return TEXT("long");
        if (Kind == TEXT("float32")) return TEXT("float");
        if (Kind == TEXT("float64")) return TEXT("double");
        if (Kind == TEXT("string") || Kind == TEXT("name") || Kind == TEXT("text"))
        {
            return TEXT("string");
        }
        FString TypePath;
        if (Kind == TEXT("object") && Type->TryGetStringField(TEXT("typePath"), TypePath))
        {
            FString Left;
            FString Right;
            return TypePath.Split(TEXT("."), &Left, &Right, ESearchCase::CaseSensitive,
                ESearchDir::FromEnd) ? Right : TypePath;
        }
        return FString();
    };
    TFunction<FString(const TSharedPtr<FJsonObject>&)> ManagedEventTypeName;
    ManagedEventTypeName = [&ManagedEventTypeName](
        const TSharedPtr<FJsonObject>& Type) -> FString
    {
        FString Kind;
        if (!Type.IsValid() || !Type->TryGetStringField(TEXT("kind"), Kind))
        {
            return FString();
        }
        if (Kind == TEXT("boolean")) return TEXT("bool");
        if (Kind == TEXT("int8")) return TEXT("sbyte");
        if (Kind == TEXT("uint8")) return TEXT("byte");
        if (Kind == TEXT("int16")) return TEXT("short");
        if (Kind == TEXT("uint16")) return TEXT("ushort");
        if (Kind == TEXT("int32")) return TEXT("int");
        if (Kind == TEXT("uint32")) return TEXT("uint");
        if (Kind == TEXT("int64")) return TEXT("long");
        if (Kind == TEXT("uint64")) return TEXT("ulong");
        if (Kind == TEXT("float32")) return TEXT("float");
        if (Kind == TEXT("float64")) return TEXT("double");
        if (Kind == TEXT("string")) return TEXT("string");
        if (Kind == TEXT("name")) return TEXT("global::UnifiedScript.Unreal.UnrealName");
        if (Kind == TEXT("text")) return TEXT("global::UnifiedScript.Unreal.UnrealText");
        if (Kind == TEXT("object"))
        {
            return TEXT("global::UnifiedScript.Unreal.UnrealObjectHandle");
        }
        if (Kind == TEXT("class"))
        {
            return TEXT("global::UnifiedScript.Unreal.UnrealClassHandle");
        }
        if (Kind == TEXT("enum"))
        {
            return TEXT("global::UnifiedScript.Unreal.UnrealEnumValue");
        }
        FString TypePath;
        if (Kind == TEXT("struct") && Type->TryGetStringField(TEXT("typePath"), TypePath))
        {
            if (TypePath == TEXT("/Script/CoreUObject.Vector"))
            {
                return TEXT("global::UnifiedScript.Unreal.UnrealVector");
            }
            if (TypePath == TEXT("/Script/CoreUObject.Rotator"))
            {
                return TEXT("global::UnifiedScript.Unreal.UnrealRotator");
            }
            if (TypePath == TEXT("/Script/CoreUObject.Transform"))
            {
                return TEXT("global::UnifiedScript.Unreal.UnrealTransform");
            }
            return FString();
        }
        const TSharedPtr<FJsonObject>* Element = nullptr;
        if ((Kind == TEXT("array") || Kind == TEXT("set")) &&
            Type->TryGetObjectField(TEXT("elementType"), Element) && Element != nullptr)
        {
            const FString ManagedElement = ManagedEventTypeName(*Element);
            if (ManagedElement.IsEmpty()) return FString();
            return Kind == TEXT("array")
                ? FString::Printf(
                    TEXT("global::System.Collections.Generic.IReadOnlyList<%s>"),
                    *ManagedElement)
                : FString::Printf(
                    TEXT("global::System.Collections.Generic.IReadOnlySet<%s>"),
                    *ManagedElement);
        }
        const TSharedPtr<FJsonObject>* Key = nullptr;
        const TSharedPtr<FJsonObject>* Value = nullptr;
        if (Kind == TEXT("map") && Type->TryGetObjectField(TEXT("keyType"), Key) &&
            Key != nullptr && Type->TryGetObjectField(TEXT("valueType"), Value) &&
            Value != nullptr)
        {
            const FString ManagedKey = ManagedEventTypeName(*Key);
            const FString ManagedValue = ManagedEventTypeName(*Value);
            return ManagedKey.IsEmpty() || ManagedValue.IsEmpty()
                ? FString()
                : FString::Printf(
                    TEXT("global::System.Collections.Generic.IReadOnlyDictionary<%s, %s>"),
                    *ManagedKey,
                    *ManagedValue);
        }
        return FString();
    };
    const auto FindBoundTypeId = [&Function](const FString& DisplayName) -> FString
    {
        for (const FUnifiedScriptAuthoringParameter& Parameter : Function.Parameters)
        {
            if (Parameter.TypeDisplayName == DisplayName && !Parameter.TypeId.IsEmpty())
            {
                return Parameter.TypeId;
            }
        }
        for (const FUnifiedScriptAuthoringAction& Action : Function.Actions)
        {
            for (const FUnifiedScriptAuthoringPort& Port : Action.Ports)
            {
                if (Port.TypeDisplayName == DisplayName && !Port.TypeId.IsEmpty())
                {
                    return Port.TypeId;
                }
            }
        }
        return FString();
    };

    const TArray<TSharedPtr<FJsonValue>>* Types = nullptr;
    if (!Manifest->TryGetArrayField(TEXT("types"), Types) || Types == nullptr)
    {
        return;
    }
    FString ManagedNamespace = TEXT("UnifiedScript.Unreal.Generated");
    Manifest->TryGetStringField(TEXT("managedNamespace"), ManagedNamespace);
    const FUnifiedScriptAuthoringAsset* ActiveAsset = Document.Assets.FindByPredicate(
        [&Function](const FUnifiedScriptAuthoringAsset& Asset)
        {
            return Asset.Functions.ContainsByPredicate(
                [&Function](const FUnifiedScriptAuthoringFunction& Candidate)
                {
                    return Candidate.Id == Function.Id;
                });
        });
    for (const TSharedPtr<FJsonValue>& TypeValue : *Types)
    {
        const TSharedPtr<FJsonObject> Type = TypeValue.IsValid() ? TypeValue->AsObject() : nullptr;
        FString TypeName;
        FString ManagedTypeName;
        const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
        if (!Type.IsValid() || !Type->TryGetStringField(TEXT("name"), TypeName))
        {
            continue;
        }
        if (!Type->TryGetStringField(TEXT("managedTypeName"), ManagedTypeName))
        {
            ManagedTypeName = TypeName;
        }
        const TArray<TSharedPtr<FJsonValue>>* Delegates = nullptr;
        if (Type->TryGetArrayField(TEXT("delegates"), Delegates) && Delegates != nullptr)
        {
            for (const TSharedPtr<FJsonValue>& DelegateValue : *Delegates)
            {
                const TSharedPtr<FJsonObject> Delegate =
                    DelegateValue.IsValid() ? DelegateValue->AsObject() : nullptr;
                FString MemberName;
                FString EventPath;
                const TSharedPtr<FJsonObject>* Traits = nullptr;
                const TArray<TSharedPtr<FJsonValue>>* Parameters = nullptr;
                bool bBlueprintAssignable = false;
                if (!Delegate.IsValid() ||
                    !Delegate->TryGetStringField(TEXT("name"), MemberName) ||
                    !Delegate->TryGetStringField(TEXT("path"), EventPath) ||
                    !Delegate->TryGetObjectField(TEXT("traits"), Traits) || Traits == nullptr ||
                    !(*Traits)->TryGetBoolField(
                        TEXT("blueprintAssignable"),
                        bBlueprintAssignable) || !bBlueprintAssignable ||
                    !Delegate->TryGetArrayField(TEXT("parameters"), Parameters) ||
                    Parameters == nullptr)
                {
                    continue;
                }
                FString HandlerName = MemberName;
                for (int32 Suffix = 2; ActiveAsset != nullptr &&
                     ActiveAsset->Functions.ContainsByPredicate(
                         [&HandlerName](const FUnifiedScriptAuthoringFunction& Candidate)
                         {
                             return Candidate.Name == HandlerName;
                         }); ++Suffix)
                {
                    HandlerName = FString::Printf(TEXT("%s%d"), *MemberName, Suffix);
                }

                TArray<FUnifiedScriptAuthoringParameter> EventParameters;
                TArray<FString> SignatureParts;
                bool bSupported = true;
                for (const TSharedPtr<FJsonValue>& ParameterValue : *Parameters)
                {
                    const TSharedPtr<FJsonObject> Parameter =
                        ParameterValue.IsValid() ? ParameterValue->AsObject() : nullptr;
                    FString Name;
                    FString Direction;
                    const TSharedPtr<FJsonObject>* ParameterType = nullptr;
                    if (!Parameter.IsValid() ||
                        !Parameter->TryGetStringField(TEXT("name"), Name) ||
                        !Parameter->TryGetStringField(TEXT("direction"), Direction) ||
                        Direction != TEXT("input") ||
                        !Parameter->TryGetObjectField(TEXT("type"), ParameterType) ||
                        ParameterType == nullptr)
                    {
                        bSupported = false;
                        break;
                    }
                    const FString ManagedType = ManagedEventTypeName(*ParameterType);
                    if (ManagedType.IsEmpty())
                    {
                        bSupported = false;
                        break;
                    }
                    FUnifiedScriptAuthoringParameter EventParameter;
                    EventParameter.Name = Name;
                    EventParameter.TypeMetadataName = ManagedType;
                    EventParameter.TypeDisplayName = TypeDisplayName(*ParameterType);
                    EventParameters.Add(MoveTemp(EventParameter));
                    SignatureParts.Add(ManagedType + TEXT(" ") + Name);
                }
                if (!bSupported)
                {
                    continue;
                }

                const FString ActionId = FString::Printf(
                    TEXT("ue.event|%s|%s"),
                    *ManagedTypeName,
                    *MemberName);
                FUnifiedScriptReflectedPaletteAction EventAction;
                EventAction.bEvent = true;
                EventAction.EventPath = EventPath;
                EventAction.SourceTypeName =
                    TEXT("global::") + ManagedNamespace + TEXT(".") + ManagedTypeName;
                EventAction.HandlerName = HandlerName;
                EventAction.EventParameters = MoveTemp(EventParameters);
                ReflectedPaletteActions.Add(ActionId, MoveTemp(EventAction));
                AuthoringGraph->PaletteActions.Add(
                    {ActionId,
                        TEXT("Unreal Events|") + TypeName,
                        TEXT("Event ") + MemberName,
                        FString::Printf(
                            TEXT("Create a target-bound visual Event Entry.\n%s(%s)"),
                            *MemberName,
                            *FString::Join(SignatureParts, TEXT(", ")))});
            }
        }
        const FUnifiedScriptAuthoringParameter* Receiver = Function.Parameters.FindByPredicate(
            [&ManagedTypeName](const FUnifiedScriptAuthoringParameter& Parameter)
            {
                return !Parameter.TypeId.IsEmpty() &&
                    (Parameter.TypeMetadataName == ManagedTypeName ||
                        Parameter.TypeMetadataName.EndsWith(TEXT(".") + ManagedTypeName));
            });
        if (Receiver == nullptr)
        {
            continue;
        }
        const TArray<TSharedPtr<FJsonValue>>* Properties = nullptr;
        if (Type->TryGetArrayField(TEXT("properties"), Properties) && Properties != nullptr)
        {
            for (const TSharedPtr<FJsonValue>& PropertyValue : *Properties)
            {
                const TSharedPtr<FJsonObject> Property =
                    PropertyValue.IsValid() ? PropertyValue->AsObject() : nullptr;
                FString MemberName;
                const TSharedPtr<FJsonObject>* PropertyType = nullptr;
                const TSharedPtr<FJsonObject>* Access = nullptr;
                bool bReadable = false;
                if (!Property.IsValid() ||
                    !Property->TryGetStringField(TEXT("name"), MemberName) ||
                    !Property->TryGetObjectField(TEXT("type"), PropertyType) || PropertyType == nullptr ||
                    !Property->TryGetObjectField(TEXT("access"), Access) || Access == nullptr ||
                    !(*Access)->TryGetBoolField(TEXT("readable"), bReadable) || !bReadable)
                {
                    continue;
                }
                const FString ResultTypeName = TypeDisplayName(*PropertyType);
                const FString ResultTypeId = FindBoundTypeId(ResultTypeName);
                if (ResultTypeName.IsEmpty() || ResultTypeId.IsEmpty())
                {
                    continue;
                }
                const FString ActionId = FString::Printf(
                    TEXT("ue.get|%s|%s"), *ManagedTypeName, *MemberName);
                ReflectedPaletteActions.Add(ActionId,
                    {false, *Receiver, MemberName, ResultTypeName, ResultTypeId, {}});
                AuthoringGraph->PaletteActions.Add(
                    {ActionId, TEXT("Unreal API|") + TypeName + TEXT("|Properties"), MemberName,
                        FString::Printf(TEXT("Read reflected property %s.%s."), *TypeName, *MemberName)});
            }
        }
        if (!Type->TryGetArrayField(TEXT("functions"), Functions) || Functions == nullptr)
        {
            continue;
        }
        for (const TSharedPtr<FJsonValue>& FunctionValue : *Functions)
        {
            const TSharedPtr<FJsonObject> ReflectedFunction =
                FunctionValue.IsValid() ? FunctionValue->AsObject() : nullptr;
            FString MemberName;
            const TSharedPtr<FJsonObject>* ReturnType = nullptr;
            const TSharedPtr<FJsonObject>* Traits = nullptr;
            const TArray<TSharedPtr<FJsonValue>>* Parameters = nullptr;
            bool bCallable = false;
            if (!ReflectedFunction.IsValid() ||
                !ReflectedFunction->TryGetStringField(TEXT("name"), MemberName) ||
                !ReflectedFunction->TryGetObjectField(TEXT("returnType"), ReturnType) ||
                ReturnType == nullptr ||
                !ReflectedFunction->TryGetObjectField(TEXT("traits"), Traits) || Traits == nullptr ||
                !(*Traits)->TryGetBoolField(TEXT("callable"), bCallable) || !bCallable ||
                !ReflectedFunction->TryGetArrayField(TEXT("parameters"), Parameters) ||
                Parameters == nullptr)
            {
                continue;
            }
            const FString ResultTypeName = TypeDisplayName(*ReturnType);
            const FString ResultTypeId = FindBoundTypeId(ResultTypeName);
            if (ResultTypeName.IsEmpty() || ResultTypeId.IsEmpty())
            {
                continue;
            }

            TArray<FUnifiedScriptAuthoringParameter> Arguments;
            bool bAllArgumentsAvailable = true;
            for (const TSharedPtr<FJsonValue>& ParameterValue : *Parameters)
            {
                const TSharedPtr<FJsonObject> ParameterObject =
                    ParameterValue.IsValid() ? ParameterValue->AsObject() : nullptr;
                FString Name;
                FString Direction;
                const TSharedPtr<FJsonObject>* ParameterType = nullptr;
                if (!ParameterObject.IsValid() ||
                    !ParameterObject->TryGetStringField(TEXT("name"), Name) ||
                    !ParameterObject->TryGetStringField(TEXT("direction"), Direction) ||
                    Direction != TEXT("input") ||
                    !ParameterObject->TryGetObjectField(TEXT("type"), ParameterType) ||
                    ParameterType == nullptr)
                {
                    bAllArgumentsAvailable = false;
                    break;
                }
                const FString DisplayName = TypeDisplayName(*ParameterType);
                const FUnifiedScriptAuthoringParameter* Argument = Function.Parameters.FindByPredicate(
                    [&Name, &DisplayName](const FUnifiedScriptAuthoringParameter& Parameter)
                    {
                        return Parameter.Name.Equals(Name, ESearchCase::IgnoreCase) &&
                            Parameter.TypeDisplayName == DisplayName &&
                            !Parameter.TypeId.IsEmpty();
                    });
                if (Argument == nullptr)
                {
                    bAllArgumentsAvailable = false;
                    break;
                }
                Arguments.Add(*Argument);
            }
            if (!bAllArgumentsAvailable)
            {
                continue;
            }

            const FString ActionId = FString::Printf(
                TEXT("ue.call|%s|%s"), *ManagedTypeName, *MemberName);
            ReflectedPaletteActions.Add(ActionId,
                {true, *Receiver, MemberName, ResultTypeName, ResultTypeId, MoveTemp(Arguments)});
            AuthoringGraph->PaletteActions.Add(
                {ActionId, TEXT("Unreal API|") + TypeName, MemberName,
                    FString::Printf(TEXT("Insert reflected call %s.%s(...)."), *TypeName, *MemberName)});
        }
    }
}

void SUnifiedScriptAuthoringSurface::PersistCurrentLayout(bool bOnlyIfChanged)
{
    if (!AuthoringGraph.IsValid() || ActiveFunctionId.IsEmpty() || LoadedLayoutPath.IsEmpty() ||
        AuthoringGraph->Nodes.IsEmpty())
    {
        return;
    }
    TMap<FString, FVector2D> CurrentPositions;
    TArray<UEdGraphNode*> Nodes;
    for (UEdGraphNode* Node : AuthoringGraph->Nodes)
    {
        Nodes.Add(Node);
        if (const UUnifiedScriptEdGraphNode* ScriptNode = Cast<UUnifiedScriptEdGraphNode>(Node))
        {
            CurrentPositions.Add(
                ScriptNode->SemanticId,
                FVector2D(ScriptNode->NodePosX, ScriptNode->NodePosY));
        }
    }
    if (bOnlyIfChanged && CurrentPositions.OrderIndependentCompareEqual(PersistedNodePositions))
    {
        return;
    }
    FString Error;
    if (FUnifiedScriptGraphLayoutStore::SaveFunction(
            LoadedLayoutPath, ActiveFunctionId, Nodes, Error))
    {
        PersistedNodePositions = MoveTemp(CurrentPositions);
    }
    else if (!Error.IsEmpty())
    {
        SetStatus(MoveTemp(Error), true);
    }
}

FUnifiedScriptSurfaceTreeItemPtr SUnifiedScriptAuthoringSurface::FindSurfaceItem(
    const FString& SemanticId) const
{
    if (const FUnifiedScriptSurfaceTreeItemPtr* Item = SurfaceItemsById.Find(SemanticId))
    {
        return *Item;
    }
    return nullptr;
}

FReply SUnifiedScriptAuthoringSurface::HandleFocusGraph()
{
    if (GraphEditor.IsValid() && !AuthoringGraph->Nodes.IsEmpty())
    {
        GraphEditor->ZoomToFit(false);
    }
    return FReply::Handled();
}

int32 SUnifiedScriptAuthoringSurface::GetVisibleGraphNodeCount() const
{
    return AuthoringGraph.IsValid() ? AuthoringGraph->Nodes.Num() : 0;
}

int32 SUnifiedScriptAuthoringSurface::GetVisibleGraphEdgeCount() const
{
    if (!AuthoringGraph.IsValid())
    {
        return 0;
    }

    int32 Count = 0;
    for (const UEdGraphNode* Node : AuthoringGraph->Nodes)
    {
        for (const UEdGraphPin* Pin : Node->Pins)
        {
            if (Pin != nullptr && Pin->Direction == EGPD_Output)
            {
                Count += Pin->LinkedTo.Num();
            }
        }
    }
    return Count;
}

FString SUnifiedScriptAuthoringSurface::GetActiveFunctionId() const
{
    return ActiveFunctionId;
}

bool SUnifiedScriptAuthoringSurface::HasNodePaletteAction(const FString& ActionId) const
{
    return AuthoringGraph.IsValid() && AuthoringGraph->PaletteActions.ContainsByPredicate(
        [&ActionId](const FUnifiedScriptGraphPaletteAction& Action)
        {
            return Action.ActionId == ActionId;
        });
}

void SUnifiedScriptAuthoringSurface::RebuildInspector(
    const FUnifiedScriptSurfaceTreeItemPtr& Item)
{
    InspectorBox->ClearChildren();
    if (!Item.IsValid())
    {
        InspectorBox->AddSlot().AutoHeight()
        [
            SNew(STextBlock)
            .Text(LOCTEXT("EmptyInspector", "Load an Authoring Surface and select an item."))
            .ColorAndOpacity(FLinearColor(0.55f, 0.58f, 0.62f))
        ];
        return;
    }

    InspectorBox->AddSlot().AutoHeight()
    [
        SNew(STextBlock)
        .Text(FText::FromString(Item->Label))
        .Font(FAppStyle::GetFontStyle(TEXT("HeadingExtraSmall")))
    ];
    InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 14.0f)
    [
        SNew(STextBlock)
        .Text(FText::FromString(Item->Detail))
        .AutoWrapText(true)
        .ColorAndOpacity(FLinearColor(0.68f, 0.7f, 0.74f))
    ];

    InspectorBox->AddSlot().AutoHeight()
    [
        UE::UnifiedScript::AuthoringSurface::Slate::Private::MakeInspectorValue(
            TEXT("KIND"),
            GetKindLabel(Item->Kind) +
                (Item->Category.IsEmpty() ? FString() : TEXT("  ·  ") + Item->Category) +
                (Item->GraphKind.IsEmpty() ? FString() : TEXT("  ·  ") + Item->GraphKind))
    ];
    if (!Item->SemanticId.IsEmpty())
    {
        InspectorBox->AddSlot().AutoHeight()
        [
            SNew(SHorizontalBox)
            + SHorizontalBox::Slot().FillWidth(1.0f)
            [
                UE::UnifiedScript::AuthoringSurface::Slate::Private::MakeInspectorValue(
                    TEXT("SEMANTIC ID"), Item->SemanticId)
            ]
            + SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
            [
                SNew(SButton)
                .Text(LOCTEXT("CopyId", "Copy ID"))
                .OnClicked(this, &SUnifiedScriptAuthoringSurface::HandleCopyId, Item->SemanticId)
            ]
        ];
    }
    if (Item->Span.IsValid())
    {
        InspectorBox->AddSlot().AutoHeight()
        [
            SNew(SHorizontalBox)
            + SHorizontalBox::Slot().FillWidth(1.0f)
            [
                UE::UnifiedScript::AuthoringSurface::Slate::Private::MakeInspectorValue(
                    TEXT("SOURCE"), Item->Span.ToDisplayString())
            ]
            + SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
            [
                SNew(SButton)
                .Text(LOCTEXT("OpenSource", "Open Source"))
                .OnClicked(this, &SUnifiedScriptAuthoringSurface::HandleOpenSource, Item->Span)
            ]
        ];
        InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 14.0f)
        [
            SNew(SBox)
            .MinDesiredHeight(130.0f)
            [
                SNew(SMultiLineEditableTextBox)
                .Text(FText::FromString(BuildSourcePreview(Item->Span)))
                .IsReadOnly(true)
                .Font(FAppStyle::GetFontStyle(TEXT("MonoFont")))
            ]
        ];
    }

    if (!Item->Edits.IsEmpty())
    {
        InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 4.0f, 0.0f, 8.0f)
        [
            SNew(STextBlock)
            .Text(LOCTEXT("SemanticEdits", "SEMANTIC EDITS"))
            .Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
        ];
        for (const FUnifiedScriptAuthoringEdit& Edit : Item->Edits)
        {
            TSharedPtr<SEditableTextBox> ValueEditor;
            const FString Options = Edit.Options.IsEmpty()
                ? FString()
                : FString::Printf(TEXT("Options: %s"), *FString::Join(Edit.Options, TEXT("  ")));
            InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 8.0f)
            [
                SNew(SBorder)
                .BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
                .Padding(8.0f)
                [
                    SNew(SVerticalBox)
                    + SVerticalBox::Slot().AutoHeight()
                    [
                        SNew(STextBlock).Text(FText::FromString(Edit.Label))
                    ]
                    + SVerticalBox::Slot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 5.0f)
                    [
                        SNew(STextBlock)
                        .Text(FText::FromString(Options))
                        .Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
                        .ColorAndOpacity(FLinearColor(0.55f, 0.58f, 0.62f))
                    ]
                    + SVerticalBox::Slot().AutoHeight()
                    [
                        SNew(SHorizontalBox)
                        + SHorizontalBox::Slot().FillWidth(1.0f)
                        [
                            SAssignNew(ValueEditor, SEditableTextBox)
                            .Text(FText::FromString(Edit.CurrentValue))
                        ]
                        + SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
                        [
                            SNew(SButton)
                            .Text(LOCTEXT("CopyPatch", "Copy Patch"))
                            .ToolTipText(LOCTEXT("CopyPatchTip", "Copy a typed semantic-patch v0.21 payload using this value."))
                            .OnClicked(this, &SUnifiedScriptAuthoringSurface::HandleCopyPatch, Edit, ValueEditor)
                        ]
                        + SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
                        [
                            SNew(SButton)
                            .Text(LOCTEXT("ApplyPatch", "Apply & Reload"))
                            .ToolTipText(LOCTEXT(
                                "ApplyPatchTip",
                                "Submit the typed patch to the transactional Workbench and reload matching components."))
                            .IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
                            .OnClicked(this, &SUnifiedScriptAuthoringSurface::HandleApplyPatch, Edit, ValueEditor)
                        ]
                    ]
                ]
            ];
        }
    }
}

void SUnifiedScriptAuthoringSurface::SetStatus(FString Message, bool bError)
{
    StatusMessage = MoveTemp(Message);
    bStatusError = bError;
}

FString SUnifiedScriptAuthoringSurface::ResolveSourcePath(const FString& SourcePath) const
{
    TArray<FString> Candidates;
    if (!FPaths::IsRelative(SourcePath))
    {
        Candidates.Add(SourcePath);
    }
    else
    {
        Candidates.Add(FPaths::Combine(FPaths::ProjectDir(), SourcePath));
        Candidates.Add(FPaths::Combine(FPaths::ProjectDir(), TEXT(".."), SourcePath));
        if (!LoadedSurfacePath.IsEmpty())
        {
            Candidates.Add(FPaths::Combine(FPaths::GetPath(LoadedSurfacePath), SourcePath));
        }
    }
    for (FString Candidate : Candidates)
    {
        Candidate = FPaths::ConvertRelativePathToFull(Candidate);
        FPaths::NormalizeFilename(Candidate);
        if (IFileManager::Get().FileExists(*Candidate))
        {
            return Candidate;
        }
    }
    return FString();
}

FString SUnifiedScriptAuthoringSurface::BuildSourcePreview(
    const FUnifiedScriptAuthoringSpan& Span) const
{
    const FString Path = ResolveSourcePath(Span.FilePath);
    FString Source;
    if (Path.IsEmpty() || !FFileHelper::LoadFileToString(Source, *Path))
    {
        return FString::Printf(TEXT("Source preview unavailable\n%s"), *Span.ToDisplayString());
    }
    TArray<FString> Lines;
    Source.ParseIntoArrayLines(Lines, false);
    const int32 FirstLine = FMath::Max(1, Span.StartLine - 2);
    const int32 LastLine = FMath::Min(Lines.Num(), FMath::Max(Span.EndLine, Span.StartLine) + 2);
    TArray<FString> Preview;
    for (int32 Line = FirstLine; Line <= LastLine; ++Line)
    {
        Preview.Add(FString::Printf(
            TEXT("%s %4d  %s"),
            Line >= Span.StartLine && Line <= Span.EndLine ? TEXT(">") : TEXT(" "),
            Line,
            *Lines[Line - 1]));
    }
    return FString::Join(Preview, TEXT("\n"));
}

FString SUnifiedScriptAuthoringSurface::BuildFunctionSignature(
    const FUnifiedScriptAuthoringFunction& Function) const
{
    TArray<FString> Parameters;
    for (const FUnifiedScriptAuthoringParameter& Parameter : Function.Parameters)
    {
        Parameters.Add(Parameter.TypeMetadataName + TEXT(" ") + Parameter.Name);
    }
    return FString::Printf(
        TEXT("%s(%s) -> %s"),
        *Function.Name,
        *FString::Join(Parameters, TEXT(", ")),
        *Function.ReturnTypeMetadataName);
}

FString SUnifiedScriptAuthoringSurface::GetKindLabel(
    EUnifiedScriptSurfaceTreeItemKind Kind)
{
    switch (Kind)
    {
    case EUnifiedScriptSurfaceTreeItemKind::Asset: return TEXT("SCRIPT ASSET");
    case EUnifiedScriptSurfaceTreeItemKind::Group: return TEXT("GROUP");
    case EUnifiedScriptSurfaceTreeItemKind::State: return TEXT("STATE");
    case EUnifiedScriptSurfaceTreeItemKind::Function: return TEXT("FUNCTION");
    case EUnifiedScriptSurfaceTreeItemKind::Region: return TEXT("REGION");
    case EUnifiedScriptSurfaceTreeItemKind::Action: return TEXT("ACTION");
    default: return TEXT("ITEM");
    }
}

FSlateColor SUnifiedScriptAuthoringSurface::GetKindColor(
    EUnifiedScriptSurfaceTreeItemKind Kind)
{
    switch (Kind)
    {
    case EUnifiedScriptSurfaceTreeItemKind::Asset: return FLinearColor(0.2f, 0.55f, 0.95f);
    case EUnifiedScriptSurfaceTreeItemKind::State: return FLinearColor(0.65f, 0.38f, 0.92f);
    case EUnifiedScriptSurfaceTreeItemKind::Function: return FLinearColor(0.15f, 0.72f, 0.62f);
    case EUnifiedScriptSurfaceTreeItemKind::Region: return FLinearColor(0.95f, 0.62f, 0.18f);
    case EUnifiedScriptSurfaceTreeItemKind::Action: return FLinearColor(0.88f, 0.32f, 0.34f);
    default: return FLinearColor(0.42f, 0.45f, 0.5f);
    }
}

#undef LOCTEXT_NAMESPACE

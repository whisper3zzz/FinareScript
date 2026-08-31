#include "Modules/ModuleManager.h"

#include "EdGraphUtilities.h"
#include "Graph/SUnifiedScriptGraphNode.h"
#include "HAL/IConsoleManager.h"
#include "KismetPins/SGraphPinExec.h"
#include "SGraphNodeKnot.h"
#include "Styling/AppStyle.h"
#include "ToolMenus.h"
#include "UnifiedScriptEdGraph.h"
#include "Widgets/Docking/SDockTab.h"
#include "Widgets/SUnifiedScriptAuthoringSurface.h"
#include "Widgets/SUnifiedScriptProjectSetup.h"
#include "WorkspaceMenuStructure.h"
#include "WorkspaceMenuStructureModule.h"

#define LOCTEXT_NAMESPACE "UEUnifiedScriptEditorModule"

namespace UE::UnifiedScript::Editor::Private
{
const FName AuthoringSurfaceTabName(TEXT("UnifiedScriptAuthoringSurface"));
const FName ProjectSetupTabName(TEXT("UnifiedScriptProjectSetup"));

class FUnifiedScriptGraphPanelNodeFactory final : public FGraphPanelNodeFactory
{
public:
    virtual TSharedPtr<SGraphNode> CreateNode(UEdGraphNode* Node) const override
    {
        if (UUnifiedScriptEdGraphNode* UnifiedScriptNode = Cast<UUnifiedScriptEdGraphNode>(Node))
        {
            return SNew(SUnifiedScriptGraphNode, UnifiedScriptNode);
        }
        if (UUnifiedScriptEdGraphRerouteNode* Reroute =
                Cast<UUnifiedScriptEdGraphRerouteNode>(Node))
        {
            return SNew(SGraphNodeKnot, Reroute);
        }
        return nullptr;
    }
};

class FUnifiedScriptGraphPanelPinFactory final : public FGraphPanelPinFactory
{
public:
    virtual TSharedPtr<SGraphPin> CreatePin(UEdGraphPin* Pin) const override
    {
        if (Pin != nullptr && Cast<UUnifiedScriptEdGraphNode>(Pin->GetOwningNode()) != nullptr &&
            Pin->PinType.PinCategory == TEXT("exec"))
        {
            return SNew(SGraphPinExec, Pin);
        }
        return nullptr;
    }
};
}

class FUEUnifiedScriptEditorModule final : public IModuleInterface
{
public:
    virtual void StartupModule() override
    {
        using namespace UE::UnifiedScript::Editor::Private;
        GraphNodeFactory = MakeShared<FUnifiedScriptGraphPanelNodeFactory>();
        FEdGraphUtilities::RegisterVisualNodeFactory(GraphNodeFactory);
        GraphPinFactory = MakeShared<FUnifiedScriptGraphPanelPinFactory>();
        FEdGraphUtilities::RegisterVisualPinFactory(GraphPinFactory);

        FGlobalTabmanager::Get()->RegisterNomadTabSpawner(
            AuthoringSurfaceTabName,
            FOnSpawnTab::CreateRaw(this, &FUEUnifiedScriptEditorModule::SpawnAuthoringSurfaceTab))
            .SetDisplayName(LOCTEXT("AuthoringSurfaceTab", "UnifiedScript Authoring"))
            .SetTooltipText(LOCTEXT("AuthoringSurfaceTooltip", "Open the native UnifiedScript visual authoring workspace."))
            .SetGroup(WorkspaceMenu::GetMenuStructure().GetToolsCategory())
            .SetIcon(FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Code")));

        FGlobalTabmanager::Get()->RegisterNomadTabSpawner(
            ProjectSetupTabName,
            FOnSpawnTab::CreateRaw(this, &FUEUnifiedScriptEditorModule::SpawnProjectSetupTab))
            .SetDisplayName(LOCTEXT("ProjectSetupTab", "UnifiedScript Project Setup"))
            .SetTooltipText(LOCTEXT("ProjectSetupTooltip", "Adopt, diagnose, repair, and build this UnifiedScript project."))
            .SetGroup(WorkspaceMenu::GetMenuStructure().GetToolsCategory())
            .SetIcon(FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Settings")));

        OpenAuthoringConsoleCommand = IConsoleManager::Get().RegisterConsoleCommand(
            TEXT("UnifiedScript.OpenAuthoring"),
            TEXT("Open the UnifiedScript Authoring Surface."),
            FConsoleCommandDelegate::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::OpenAuthoringSurface),
            ECVF_Default);

        OpenProjectSetupConsoleCommand = IConsoleManager::Get().RegisterConsoleCommand(
            TEXT("UnifiedScript.OpenProjectSetup"),
            TEXT("Open UnifiedScript Project Setup."),
            FConsoleCommandDelegate::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::OpenProjectSetup),
            ECVF_Default);

        UToolMenus::RegisterStartupCallback(
            FSimpleMulticastDelegate::FDelegate::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::RegisterMenus));
    }

    virtual void ShutdownModule() override
    {
        if (GraphNodeFactory.IsValid())
        {
            FEdGraphUtilities::UnregisterVisualNodeFactory(GraphNodeFactory);
            GraphNodeFactory.Reset();
        }
        if (GraphPinFactory.IsValid())
        {
            FEdGraphUtilities::UnregisterVisualPinFactory(GraphPinFactory);
            GraphPinFactory.Reset();
        }
        UToolMenus::UnRegisterStartupCallback(this);
        UToolMenus::UnregisterOwner(this);
        if (OpenAuthoringConsoleCommand != nullptr)
        {
            IConsoleManager::Get().UnregisterConsoleObject(OpenAuthoringConsoleCommand);
            OpenAuthoringConsoleCommand = nullptr;
        }
        if (OpenProjectSetupConsoleCommand != nullptr)
        {
            IConsoleManager::Get().UnregisterConsoleObject(OpenProjectSetupConsoleCommand);
            OpenProjectSetupConsoleCommand = nullptr;
        }
        FGlobalTabmanager::Get()->UnregisterNomadTabSpawner(
            UE::UnifiedScript::Editor::Private::AuthoringSurfaceTabName);
        FGlobalTabmanager::Get()->UnregisterNomadTabSpawner(
            UE::UnifiedScript::Editor::Private::ProjectSetupTabName);
    }

private:
    TSharedPtr<FGraphPanelNodeFactory> GraphNodeFactory;
    TSharedPtr<FGraphPanelPinFactory> GraphPinFactory;
    IConsoleObject* OpenAuthoringConsoleCommand = nullptr;
    IConsoleObject* OpenProjectSetupConsoleCommand = nullptr;

    TSharedRef<SDockTab> SpawnAuthoringSurfaceTab(const FSpawnTabArgs& Args)
    {
        return SNew(SDockTab)
            .TabRole(ETabRole::NomadTab)
            [
                SNew(SUnifiedScriptAuthoringSurface)
            ];
    }

    TSharedRef<SDockTab> SpawnProjectSetupTab(const FSpawnTabArgs& Args)
    {
        return SNew(SDockTab)
            .TabRole(ETabRole::NomadTab)
            [
                SNew(SUnifiedScriptProjectSetup)
            ];
    }

    void OpenAuthoringSurface()
    {
        FGlobalTabmanager::Get()->TryInvokeTab(
            UE::UnifiedScript::Editor::Private::AuthoringSurfaceTabName);
    }

    void OpenProjectSetup()
    {
        FGlobalTabmanager::Get()->TryInvokeTab(
            UE::UnifiedScript::Editor::Private::ProjectSetupTabName);
    }

    void RegisterMenus()
    {
        FToolMenuOwnerScoped OwnerScoped(this);
        UToolMenu* Menu = UToolMenus::Get()->ExtendMenu(TEXT("LevelEditor.MainMenu.Tools"));
        FToolMenuSection& Section = Menu->FindOrAddSection(TEXT("Programming"));
        Section.AddMenuEntry(
            TEXT("OpenUnifiedScriptAuthoring"),
            LOCTEXT("OpenAuthoring", "UnifiedScript Authoring"),
            LOCTEXT("OpenAuthoringTooltip", "Open the Slate Authoring Surface for C# state and Visual behaviors."),
            FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Code")),
            FUIAction(FExecuteAction::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::OpenAuthoringSurface)));
        Section.AddMenuEntry(
            TEXT("OpenUnifiedScriptProjectSetup"),
            LOCTEXT("OpenProjectSetup", "UnifiedScript Project Setup"),
            LOCTEXT("OpenProjectSetupTooltip", "Preview, repair, diagnose, or build this UnifiedScript project."),
            FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Settings")),
            FUIAction(FExecuteAction::CreateRaw(
                this,
                &FUEUnifiedScriptEditorModule::OpenProjectSetup)));
    }
};

IMPLEMENT_MODULE(FUEUnifiedScriptEditorModule, UEUnifiedScriptEditor)

#undef LOCTEXT_NAMESPACE

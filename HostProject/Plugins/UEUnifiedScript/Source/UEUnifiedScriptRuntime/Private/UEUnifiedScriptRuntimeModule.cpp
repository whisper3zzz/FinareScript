#include "Engine/World.h"
#include "Misc/FileHelper.h"
#include "Misc/CommandLine.h"
#include "Misc/CoreDelegates.h"
#include "Misc/Parse.h"
#include "Modules/ModuleManager.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptCoreClrHost.h"

namespace UE::UnifiedScript::PackagedSmoke::Private
{
void Finish(bool bSuccess, const FString& Detail)
{
    FString ResultPath;
    if (FParse::Value(
            FCommandLine::Get(),
            TEXT("UnifiedScriptSmokeResult="),
            ResultPath))
    {
        ResultPath.TrimQuotesInline();
        FFileHelper::SaveStringToFile(
            FString::Printf(
                TEXT("%s\n%s\n"),
                bSuccess ? TEXT("PASS") : TEXT("FAIL"),
                *Detail),
            *ResultPath,
            FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
    }
    UE_LOG(
        LogTemp,
        Display,
        TEXT("UNIFIEDSCRIPT_PACKAGED_SMOKE %s detail=\"%s\""),
        bSuccess ? TEXT("PASS") : TEXT("FAIL"),
        *Detail.ReplaceCharWithEscapedChar());
    FPlatformMisc::RequestExitWithStatus(false, bSuccess ? 0 : 1, TEXT("UnifiedScriptPackagedSmoke"));
}

void Run()
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (World == nullptr)
    {
        Finish(false, TEXT("Unable to create the smoke-test World."));
        return;
    }

    UUnifiedScriptComponent* Component = NewObject<UUnifiedScriptComponent>(World);
    Component->ScriptAssemblyPath =
        TEXT("{PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll");
    Component->ScriptTypeName = TEXT("UnifiedScript.Unreal.Demo.DemoScript");
    Component->RegisterComponentWithWorld(World);
    Component->BeginPlay();

    const bool bStarted =
        Component->bScriptRunning && Component->ManagedState == TEXT("Started:10");
    if (bStarted)
    {
        Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
    }
    const bool bTicked = bStarted && Component->ManagedState == TEXT("Ticked");
    const FString HostFxrPath = FUnifiedScriptCoreClrHost::Get().GetHostFxrPath();
    const bool bUsesPackagedRuntime =
        HostFxrPath.Contains(TEXT("UEUnifiedScript/Binaries/ThirdParty/DotNet/10.0"));
    const FString Failure = !bStarted
        ? Component->LastScriptError
        : !bTicked
            ? TEXT("Managed OnTick did not update the component state.")
            : !bUsesPackagedRuntime
                ? FString::Printf(TEXT("CoreCLR used a non-packaged hostfxr: %s"), *HostFxrPath)
                : FString();

    Component->EndPlay(EEndPlayReason::Quit);
    Component->UnregisterComponent();
    World->RemoveFromRoot();
    World->DestroyWorld(false);

    Finish(
        Failure.IsEmpty(),
        Failure.IsEmpty()
            ? FString::Printf(TEXT("Managed Start/Tick/Stop succeeded; hostfxr=%s"), *HostFxrPath)
            : Failure);
}
}

class FUEUnifiedScriptRuntimeModule final : public IModuleInterface
{
public:
    virtual void StartupModule() override
    {
        if (FParse::Param(FCommandLine::Get(), TEXT("UnifiedScriptSmoke")))
        {
            SmokeDelegateHandle = FCoreDelegates::OnFEngineLoopInitComplete.AddStatic(
                &UE::UnifiedScript::PackagedSmoke::Private::Run);
        }
    }

    virtual void ShutdownModule() override
    {
        if (SmokeDelegateHandle.IsValid())
        {
            FCoreDelegates::OnFEngineLoopInitComplete.Remove(SmokeDelegateHandle);
            SmokeDelegateHandle.Reset();
        }
        FUnifiedScriptCoreClrHost::Get().Shutdown();
    }

private:
    FDelegateHandle SmokeDelegateHandle;
};

IMPLEMENT_MODULE(FUEUnifiedScriptRuntimeModule, UEUnifiedScriptRuntime);

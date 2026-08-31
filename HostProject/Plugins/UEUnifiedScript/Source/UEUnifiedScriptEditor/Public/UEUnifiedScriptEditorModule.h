#pragma once

#include "Modules/ModuleInterface.h"
#include "Modules/ModuleManager.h"

class UUnifiedScriptAsset;

/** Public editor navigation used by product workflows such as first Play. */
class UEUNIFIEDSCRIPTEDITOR_API IUEUnifiedScriptEditorModule : public IModuleInterface
{
public:
    static IUEUnifiedScriptEditorModule& Get()
    {
        return FModuleManager::LoadModuleChecked<IUEUnifiedScriptEditorModule>(
            TEXT("UEUnifiedScriptEditor"));
    }

    virtual void OpenAuthoringSurface(UUnifiedScriptAsset* ScriptAsset) = 0;
    virtual bool OpenAuthoringMember(const FString& SemanticId) = 0;
    virtual void OpenProjectSetup() = 0;
};

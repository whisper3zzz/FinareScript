#include "FinarePilotPlayLibrary.h"

#include "Editor.h"
#include "PlayInEditorDataTypes.h"

bool UFinarePilotPlayLibrary::RequestPlay(bool bSimulate)
{
#if WITH_EDITOR
    if (GEditor == nullptr || GEditor->PlayWorld != nullptr)
    {
        return false;
    }

    FRequestPlaySessionParams Params;
    Params.WorldType = bSimulate
        ? EPlaySessionWorldType::SimulateInEditor
        : EPlaySessionWorldType::PlayInEditor;
    Params.bAllowOnlineSubsystem = false;
    GEditor->RequestPlaySession(Params);
    return true;
#else
    return false;
#endif
}

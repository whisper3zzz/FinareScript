#include "Modules/ModuleManager.h"
#include "UnifiedScriptCoreClrHost.h"

class FUEUnifiedScriptRuntimeModule final : public IModuleInterface
{
public:
    virtual void ShutdownModule() override
    {
        FUnifiedScriptCoreClrHost::Get().Shutdown();
    }
};

IMPLEMENT_MODULE(FUEUnifiedScriptRuntimeModule, UEUnifiedScriptRuntime);

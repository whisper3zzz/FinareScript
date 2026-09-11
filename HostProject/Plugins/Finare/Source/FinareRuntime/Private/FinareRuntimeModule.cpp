#include "Modules/ModuleManager.h"
#include "FinareCoreClrHost.h"

class FFinareRuntimeModule final : public IModuleInterface
{
public:
	virtual void ShutdownModule() override
	{
		FFinareCoreClrHost::Get().Shutdown();
	}
};

IMPLEMENT_MODULE(FFinareRuntimeModule, FinareRuntime);

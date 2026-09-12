#if WITH_DEV_AUTOMATION_TESTS

#include "FinareReflectionExporter.h"
#include "Dom/JsonObject.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonSerializer.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareApiDiscoveryExportScopeTest,
	"Finare.Unreal.ApiDiscovery.ExportScope",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareApiDiscoveryExportScopeTest::RunTest(const FString& Parameters)
{
	auto ExportPaths = [this](FFinareReflectionExportOptions Options, const TCHAR* Name, TSet<FString>& Paths)
	{
		Options.OutputPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectSavedDir(), TEXT("Finare/ApiDiscovery"), Name));
		Options.bIncludePublicReflected = true;
		FFinareReflectionExportResult Result;
		FText Error;
		if (!TestTrue(FString::Printf(TEXT("%s export: %s"), Name, *Error.ToString()),
			FFinareReflectionExporter::Export(Options, Result, Error))) return false;
		FString Json;
		TSharedPtr<FJsonObject> Root;
		if (!FFileHelper::LoadFileToString(Json, *Options.OutputPath) ||
			!FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(Json), Root)) return false;
		for (const TSharedPtr<FJsonValue>& Type : Root->GetArrayField(TEXT("types")))
		{
			Paths.Add(Type->AsObject()->GetStringField(TEXT("path")));
		}
		return true;
	};
	FFinareReflectionExportOptions Module;
	Module.Modules = {TEXT("FinareRuntime")};
	Module.DiscoveryModules = Module.Modules;
	TSet<FString> ModulePaths;
	if (!ExportPaths(Module, TEXT("module-scope.json"), ModulePaths)) return false;
	TestTrue(TEXT("Module discovery finds the newly reflected class"), ModulePaths.Contains(
		TEXT("/Script/FinareRuntime.FinareApiDiscoveryComponent")));

	FFinareReflectionExportOptions Exact;
	Exact.Modules = {TEXT("Engine")};
	Exact.ClassPaths = {TEXT("/Script/Engine.Actor")};
	TSet<FString> ExactPaths;
	if (!ExportPaths(Exact, TEXT("exact-scope.json"), ExactPaths)) return false;
	FFinareReflectionExportOptions Combined = Module;
	Combined.Modules.Add(TEXT("Engine"));
	Combined.ClassPaths = Exact.ClassPaths;
	TSet<FString> CombinedPaths;
	if (!ExportPaths(Combined, TEXT("combined-scope.json"), CombinedPaths)) return false;
	TSet<FString> Expected = ModulePaths;
	Expected.Append(ExactPaths);
	TestEqual(TEXT("Module discovery preserves exact selection in other modules"), CombinedPaths.Num(), Expected.Num());
	for (const FString& Path : CombinedPaths)
	{
		TestTrue(FString::Printf(TEXT("No unintended module expansion: %s"), *Path), Expected.Contains(Path));
	}

	FFinareReflectionExportOptions Values;
	Values.Modules = {TEXT("Engine"), TEXT("CoreUObject")};
	Values.DiscoveryTypePaths = {TEXT("/Script/Engine.ECollisionChannel"), TEXT("/Script/CoreUObject.Vector")};
	TSet<FString> ValuePaths;
	if (!ExportPaths(Values, TEXT("value-scope.json"), ValuePaths)) return false;
	for (const FString& Path : Values.DiscoveryTypePaths)
	{
		TestTrue(TEXT("Standalone struct/enum root is exported"), ValuePaths.Contains(Path));
	}
	return true;
}

#endif

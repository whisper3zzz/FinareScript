#include "FinareSourceWatcher.h"
#include "DirectoryWatcherModule.h"
#include "IDirectoryWatcher.h"
#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonSerializer.h"

FFinareSourceWatcher::FFinareSourceWatcher(FString ConfigPath, TFunction<void()> OnChanged)
	: Config(MoveTemp(ConfigPath)), Callback(MoveTemp(OnChanged))
{
	Ticker = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateRaw(this, &FFinareSourceWatcher::Tick), 0.25f);
	RefreshDirectories();
}

FFinareSourceWatcher::~FFinareSourceWatcher()
{
	FTSTicker::GetCoreTicker().RemoveTicker(Ticker);
	StopDirectories();
}

bool FFinareSourceWatcher::IsSourceInput(FString Path)
{
	Path.ReplaceInline(TEXT("\\"), TEXT("/"));
	Path.ToLowerInline();
	for (const TCHAR* Ignored : {TEXT("/obj/"), TEXT("/bin/"), TEXT("/generated/"), TEXT("/saved/"), TEXT("/intermediate/")})
		if (Path.Contains(Ignored)) return false;
	if (Path.EndsWith(TEXT(".g.cs")) || Path.EndsWith(TEXT(".generated.cs"))) return false;
	return Path.EndsWith(TEXT(".cs")) || Path.EndsWith(TEXT(".csproj")) ||
		Path.EndsWith(TEXT(".props")) || Path.EndsWith(TEXT(".targets"));
}

void FFinareSourceWatcher::StopDirectories()
{
	if (auto* Module = FModuleManager::GetModulePtr<FDirectoryWatcherModule>(TEXT("DirectoryWatcher")))
		if (auto* Watcher = Module->Get())
			for (const auto& Pair : Directories) Watcher->UnregisterDirectoryChangedCallback_Handle(Pair.Key, Pair.Value);
	Directories.Reset();
}

void FFinareSourceWatcher::RefreshDirectories()
{
	NextDirectoryRetry = FPlatformTime::Seconds() + 2.0;
	bMissingDirectories = false;
	ConfigTime = IFileManager::Get().GetTimeStamp(*Config);
	FString Json;
	TSharedPtr<FJsonObject> Root;
	if (!FFileHelper::LoadFileToString(Json, *Config) ||
		!FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(Json), Root) || !Root.IsValid()) return;
	const TSharedPtr<FJsonObject>* Managed = nullptr;
	const TArray<TSharedPtr<FJsonValue>>* Projects = nullptr;
	if (!Root->TryGetObjectField(TEXT("managed"), Managed) || !(*Managed)->TryGetArrayField(TEXT("projects"), Projects)) return;
	StopDirectories();
	auto* Watcher = FModuleManager::LoadModuleChecked<FDirectoryWatcherModule>(TEXT("DirectoryWatcher")).Get();
	if (!Watcher) return;
	for (const auto& Project : *Projects)
	{
		FString Path;
		if (!Project->TryGetString(Path)) continue;
		Path = FPaths::GetPath(FPaths::ConvertRelativePathToFull(FPaths::GetPath(Config), Path));
		if (Directories.Contains(Path)) continue;
		if (!IFileManager::Get().DirectoryExists(*Path)) { bMissingDirectories = true; continue; }
		FDelegateHandle Handle;
		if (Watcher->RegisterDirectoryChangedCallback_Handle(Path,
			IDirectoryWatcher::FDirectoryChanged::CreateLambda([this](const TArray<FFileChangeData>& Changes)
			{
				for (const auto& Change : Changes)
					if (IsSourceInput(Change.Filename)) { PendingUntil = FPlatformTime::Seconds() + 0.75; break; }
			}), Handle)) Directories.Add(Path, Handle);
		else bMissingDirectories = true;
	}
}

bool FFinareSourceWatcher::Tick(float DeltaTime)
{
	if (IFileManager::Get().GetTimeStamp(*Config) != ConfigTime ||
		(bMissingDirectories && FPlatformTime::Seconds() >= NextDirectoryRetry)) RefreshDirectories();
	if (PendingUntil > 0 && FPlatformTime::Seconds() >= PendingUntil)
	{
		PendingUntil = 0;
		Callback();
	}
	return true;
}

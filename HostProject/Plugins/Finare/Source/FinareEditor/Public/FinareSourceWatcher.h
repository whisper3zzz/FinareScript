#pragma once

#include "CoreMinimal.h"
#include "Containers/Ticker.h"

/** Debounced IDE saves feed the same transactional build/reload queue as Play. */
class FINAREEDITOR_API FFinareSourceWatcher
{
public:
	FFinareSourceWatcher(FString ConfigPath, TFunction<void()> OnChanged);
	~FFinareSourceWatcher();
	static bool IsSourceInput(FString Path);

private:
	bool Tick(float DeltaTime);
	void RefreshDirectories();
	void StopDirectories();
	FString Config;
	TFunction<void()> Callback;
	FDateTime ConfigTime;
	TMap<FString, FDelegateHandle> Directories;
	FTSTicker::FDelegateHandle Ticker;
	double PendingUntil = 0;
	double NextDirectoryRetry = 0;
	bool bMissingDirectories = false;
};

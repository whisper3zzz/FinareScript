#pragma once

#include "CoreMinimal.h"

enum class EUnifiedScriptCompiledChunkSection : uint32
{
    RuntimeManifest = 1,
    ManagedAssembly = 2,
    DeclarationData = 3,
    BindingManifest = 4,
    DependencyManifest = 5,
};

/** Strict reader for deterministic .uschunk v1.0 containers. */
class UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptCompiledChunk
{
public:
    /**
     * Validates the complete chunk before copying one requested section.
     * Unknown non-critical section kinds are retained by the envelope but skipped here.
     */
    static bool LoadSection(
        const FString& ChunkPath,
        EUnifiedScriptCompiledChunkSection Section,
        TArray<uint8>& OutData,
        FString& OutError);
};

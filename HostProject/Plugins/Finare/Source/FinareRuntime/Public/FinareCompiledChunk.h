#pragma once

#include "CoreMinimal.h"

enum class EFinareCompiledChunkSection : uint32
{
    RuntimeManifest = 1,
    ManagedAssembly = 2,
    DeclarationData = 3,
    BindingManifest = 4,
    DependencyManifest = 5,
};

/** Strict reader for deterministic .uschunk v1.0 containers. */
class FINARERUNTIME_API FFinareCompiledChunk
{
public:
    /**
     * Validates the complete chunk before copying one requested section.
     * Unknown non-critical section kinds are retained by the envelope but skipped here.
     */
    static bool LoadSection(
        const FString& ChunkPath,
        EFinareCompiledChunkSection Section,
        TArray<uint8>& OutData,
        FString& OutError);
};

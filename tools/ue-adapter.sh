#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "${script_dir}/.." && pwd)"
host_project="${repository_root}/HostProject/UEUnifiedScriptHost.uproject"
artifact_directory="${repository_root}/artifacts/ue"
reflection_manifest="${artifact_directory}/UEUnifiedScriptRuntime.reflection.json"
generated_bindings="${artifact_directory}/UEUnifiedScriptRuntime.bindings.cs"

find_engine_root() {
    if [[ -n "${UNIFIEDSCRIPT_UE_ROOT:-}" ]]; then
        if [[ -f "${UNIFIEDSCRIPT_UE_ROOT}/Engine/Build/Build.version" ]]; then
            printf '%s\n' "${UNIFIEDSCRIPT_UE_ROOT}"
            return 0
        fi
        printf 'UNIFIEDSCRIPT_UE_ROOT is not an Unreal Engine root: %s\n' \
            "${UNIFIEDSCRIPT_UE_ROOT}" >&2
        return 66
    fi

    local candidate
    for candidate in \
        /Volumes/*/WorkSpace/UnrealEngine \
        /Users/Shared/Epic\ Games/UE_* \
        /Applications/UnrealEngine/UE_*; do
        if [[ -f "${candidate}/Engine/Build/Build.version" ]]; then
            printf '%s\n' "${candidate}"
            return 0
        fi
    done

    printf '%s\n' \
        'Unreal Engine was not found. Set UNIFIEDSCRIPT_UE_ROOT to its root directory.' >&2
    return 66
}

find_editor() {
    local engine_root="$1"
    local candidate
    for candidate in \
        "${engine_root}/Engine/Binaries/Mac/UnrealEditor" \
        "${engine_root}/Engine/Binaries/Mac/UnrealEditor.app/Contents/MacOS/UnrealEditor"; do
        if [[ -x "${candidate}" ]]; then
            printf '%s\n' "${candidate}"
            return 0
        fi
    done
    printf 'UnrealEditor is missing or not executable under: %s\n' "${engine_root}" >&2
    return 66
}

detect() {
    local engine_root="$1"
    local editor
    editor="$(find_editor "${engine_root}")"
    local build_script="${engine_root}/Engine/Build/BatchFiles/Mac/Build.sh"

    printf 'engineRoot=%s\n' "${engine_root}"
    printf 'hostProject=%s\n' "${host_project}"
    printf 'editor=%s\n' "${editor}"
    printf 'buildScript=%s\n' "${build_script}"
    sed -n '1,80p' "${engine_root}/Engine/Build/Build.version"

    [[ -x "${build_script}" ]] || {
        printf 'Unreal Build.sh is missing or not executable: %s\n' "${build_script}" >&2
        return 66
    }
}

build_host() {
    local engine_root="$1"
    "${engine_root}/Engine/Build/BatchFiles/Mac/Build.sh" \
        UEUnifiedScriptHostEditor \
        Mac \
        Development \
        "${host_project}" \
        -WaitMutex \
        -NoHotReload \
        -NoUBA \
        -MaxParallelActions=4 \
        -Module=UEUnifiedScriptHost \
        -Module=UEUnifiedScriptRuntime \
        -Module=UEUnifiedScriptEditor
}

export_reflection() {
    local engine_root="$1"
    local editor
    editor="$(find_editor "${engine_root}")"
    mkdir -p "${artifact_directory}"
    "${editor}" \
        "${host_project}" \
        -run=UnifiedScriptExport \
        "-Output=${reflection_manifest}" \
        -Modules=UEUnifiedScriptRuntime \
        -unattended \
        -nop4 \
        -nosplash \
        -NullRHI \
        -stdout \
        -FullStdOutLogOutput
}

generate_bindings() {
    [[ -f "${reflection_manifest}" ]] || {
        printf 'Reflection manifest does not exist: %s\n' "${reflection_manifest}" >&2
        printf '%s\n' 'Run tools/ue-adapter.sh export first.' >&2
        return 66
    }
    dotnet run \
        --project "${repository_root}/src/UnifiedScript.Cli/UnifiedScript.Cli.csproj" \
        -- \
        ue-bindings \
        "${reflection_manifest}" \
        --out "${generated_bindings}"
}

usage() {
    printf '%s\n' \
        'Usage: tools/ue-adapter.sh detect|build|export|bindings|verify' \
        'Set UNIFIEDSCRIPT_UE_ROOT when auto-discovery cannot locate the engine.'
}

command_name="${1:-}"
if [[ -z "${command_name}" ]]; then
    usage >&2
    exit 64
fi

engine_root="$(find_engine_root)"
case "${command_name}" in
    detect)
        detect "${engine_root}"
        ;;
    build)
        build_host "${engine_root}"
        ;;
    export)
        export_reflection "${engine_root}"
        ;;
    bindings)
        generate_bindings
        ;;
    verify)
        detect "${engine_root}"
        build_host "${engine_root}"
        export_reflection "${engine_root}"
        generate_bindings
        ;;
    *)
        usage >&2
        exit 64
        ;;
esac

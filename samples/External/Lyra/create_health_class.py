"""Create HealthHud with the installed normal Script Class factory.

Run after initial SDK onboarding. FINARE_LYRA_PILOT_ROOT must identify this
independent checkout. The sample keeps the new factory's persistent ClassId.
"""
import json
import os
import re
import traceback
from pathlib import Path

import unreal

root = Path(unreal.Paths.project_dir()).resolve()
report_path = root / 'Saved/Finare/LyraPilot/class-creation.json'
report = {'status': 'running', 'project': str(root)}
try:
    authorized = os.environ.get('FINARE_LYRA_PILOT_ROOT', '')
    assert authorized and Path(authorized).resolve() == root
    assert root != Path('D:/Project/LyraStarterGame').resolve(), 'Original reference is read-only'
    assert not unreal.EditorAssetLibrary.does_asset_exist('/Game/FinarePilot/HealthHud'), 'Class already exists'
    blueprint = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
        'HealthHud', '/Game/FinarePilot', unreal.FinareBlueprint,
        unreal.FinareActorComponentClassFactory())
    assert blueprint
    script_asset = blueprint.get_editor_property('script_asset')
    assert script_asset
    source = Path(script_asset.get_editor_property('source_path').replace('{ProjectDir}', str(root))).resolve()
    assert source.is_relative_to(root), 'Factory source path must be inside this checkout'
    factory_text = source.read_text(encoding='utf-8-sig')
    pattern = r'ClassId\s*=\s*"(us_[0-9a-f]+)"'
    factory_id = re.search(pattern, factory_text).group(1)
    sample_path = Path(__file__).resolve().with_name('HealthHud.cs')
    sample = sample_path.read_text(encoding='utf-8-sig')
    sample, replacements = re.subn(pattern, 'ClassId = "' + factory_id + '"', sample)
    assert replacements == 1
    source.write_text(sample, encoding='utf-8')
    report.update({'status': 'passed', 'class_id': factory_id,
                   'blueprint': blueprint.get_path_name(),
                   'script_asset': script_asset.get_path_name(), 'source': str(source)})
except Exception:
    report['status'] = 'failed'
    report['error'] = traceback.format_exc()
report_path.parent.mkdir(parents=True, exist_ok=True)
report_path.write_text(json.dumps(report, indent=2), encoding='utf-8')
unreal.log('FINARE_LYRA_HEALTH_CLASS_' + report['status'].upper())

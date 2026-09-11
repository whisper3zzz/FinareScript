"""PIE validation against Lyra's production GAS and original health widget.

Requires the independent pilot's FinarePilotValidation editor plugin solely to
request an offscreen PIE session. No native helper produces health events.
"""
import json
import os
import time
import traceback
from pathlib import Path

import unreal

unreal.EditorPythonScripting.set_keep_python_script_alive(True)
root = Path(unreal.Paths.project_dir()).resolve()
requested_root = os.environ.get('FINARE_LYRA_PILOT_ROOT')
if not requested_root or Path(requested_root).resolve() != root or root == Path('D:/Project/LyraStarterGame').resolve():
    raise RuntimeError('FINARE_LYRA_PILOT_ROOT must identify the independent pilot project')
report_path = root / 'Saved/Finare/LyraPilot/health-pie.json'
report_path.parent.mkdir(parents=True, exist_ok=True)
state = {'status': 'running', 'phase': 'setup', 'started': time.time(), 'observations': []}
level = unreal.get_editor_subsystem(unreal.LevelEditorSubsystem)
editor = unreal.get_editor_subsystem(unreal.UnrealEditorSubsystem)
handle = None
driver = None
health_widget_class = None
functions = {}


def record():
    report_path.write_text(json.dumps(state, indent=2), encoding='utf-8')


def finish(status, error=None):
    global handle, driver
    state['status'] = status
    state['elapsed_seconds'] = time.time() - state['started']
    if error:
        state['error'] = error
    record()
    if handle is not None:
        unreal.unregister_slate_post_tick_callback(handle)
        handle = None
    driver = None
    if level.is_in_play_in_editor():
        level.editor_request_end_play()
    unreal.log('FINARE_LYRA_HEALTH_PIE_' + status.upper())
    unreal.EditorPythonScripting.set_keep_python_script_alive(False)


def count():
    return driver.call_method(functions['GetHealthUpdateCount'], ())


def command(world, pc, text, phase):
    state['health_count_before'] = count()
    state['phase'] = phase
    state['phase_started'] = time.time()
    unreal.SystemLibrary.execute_console_command(world, text, pc)


def tick(delta):
    global driver
    try:
        if time.time() - state['started'] > 300:
            raise RuntimeError('Timed out in ' + state['phase'] + ': ' + state.get('waiting_for', ''))
        if state['phase'] == 'teardown':
            if not level.is_in_play_in_editor() and not editor.get_game_world():
                state['world_destroyed'] = True
                finish('passed')
            return
        world = editor.get_game_world()
        state['waiting_for'] = 'PIE World'
        if not world:
            return
        pc = unreal.GameplayStatics.get_player_controller(world, 0)
        if not pc:
            return
        pawn = pc.get_controlled_pawn()
        state['waiting_for'] = 'possessed Pawn'
        if not pawn:
            return
        health = unreal.LyraHealthComponent.find_health_component(pawn)
        if not health or health.get_max_health() <= 0:
            return
        widgets = [w for w in unreal.WidgetLibrary.get_all_widgets_of_class(world, health_widget_class, False)
                   if w.get_owning_player() == pc]
        state['waiting_for'] = 'exactly one original health widget: ' + str(len(widgets))
        if len(widgets) != 1:
            return
        widget = widgets[0]
        drivers = pc.get_components_by_class(unreal.FinareComponent)
        if not drivers and state['phase'] == 'play':
            state['waiting_for'] = 'Widget Construct to attach its health driver'
            record()
            return
        assert len(drivers) == 1, 'Expected one PC health driver, found ' + str(len(drivers))
        driver = drivers[0]
        assert driver == widget.get_editor_property('FinareHealthDriver'), 'Widget does not own its driver reference'
        error = driver.get_editor_property('last_script_error')
        assert not error, error
        if not driver.get_editor_property('script_running') and state['phase'] == 'play':
            state['waiting_for'] = 'health driver BeginPlay'
            record()
            return
        assert driver.get_editor_property('script_running') or state['phase'] == 'stopped_damage'
        value = health.get_health()
        displayed = widget.get_editor_property('HealthNumber').get_target_value()
        if state['phase'] == 'respawn' and pawn.get_path_name() == state['original_pawn']:
            state['waiting_for'] = 'replacement Pawn after actual elimination'
            return
        if state['phase'] == 'stopped_damage':
            assert abs(value - 75.0) < .01, 'Production damage did not change stopped-driver Pawn health'
            assert abs(displayed - 80.0) < .01, 'Stopped driver still changed the HUD'
            if time.time() - state['phase_started'] < 1.0:
                return
            state['stopped_driver_did_not_update_widget'] = True
            assert driver.start_script(), driver.get_editor_property('last_script_error')
            state['phase'] = 'restarted'
            record()
            return
        expected = {'play': 100., 'damage': 80., 'heal': 90., 'reload_damage': 80.,
                    'respawn': 100., 'respawn_damage': 80., 'restarted': 75., 'restart_damage': 70.}[state['phase']]
        asc = unreal.AbilitySystemLibrary.get_ability_system_component(pawn)
        tags = unreal.GameplayTagLibrary.get_debug_string_from_gameplay_tag_container(
            unreal.GameplayTagLibrary.get_owned_gameplay_tags(asc))
        if state['phase'] in ('play', 'respawn') and ('Gameplay.DamageImmunity' in tags or 'Cheat.GodMode' in tags):
            state['waiting_for'] = 'spawn damage immunity to end: ' + tags
            record()
            return
        state['waiting_for'] = 'health/HUD ' + str(expected) + ', observed ' + str(value) + '/' + str(displayed)
        state['live_health_callback_count'] = count()
        state['live_pawn_callback_count'] = driver.call_method(functions['GetPawnChangeCount'], ())
        state['live_widget_old_value'] = widget.get_editor_property('HealthOldValue')
        state['live_widget_new_value'] = widget.get_editor_property('HealthNewValue')
        state['live_widget_normalized'] = widget.get_editor_property('NormalizedHealth')
        state['widget_handle_preserved'] = driver.get_editor_property('WidgetHandle') == widget
        for name in ('GetCurrentPawnHandle', 'GetBoundHealthHandle', 'GetReceivedHealthCount',
                     'GetLastHealthCallbackHandle', 'GetHealthLookupCount',
                     'GetHealthBindingAttemptCount', 'GetHealthSubscriptionCount'):
            if name in functions:
                result = driver.call_method(functions[name], ())
                state[name] = result.get_path_name() if isinstance(result, unreal.Object) else result
        assert state.get('GetCurrentPawnHandle') == pawn.get_path_name(), \
            'Stored Pawn reference is unavailable after its event callback'
        assert state.get('GetBoundHealthHandle') == health.get_path_name(), \
            'Health driver is bound to a different Pawn health component'
        if abs(value - expected) > .01 or abs(displayed - expected) > .01:
            record()
            if state['phase'] not in ('play', 'respawn') and time.time() - state['phase_started'] > 20:
                raise RuntimeError('Health/HUD did not converge: ' + state['waiting_for'])
            return
        current_count = count()
        if state['phase'] in ('damage', 'heal', 'reload_damage', 'respawn_damage', 'restart_damage'):
            assert current_count == state['health_count_before'] + 1, 'Missing or duplicate managed health callback'
        # W_Healthbar.NormalizedHealth is an initialization cache. The original
        # Health Changed graph writes its live bar target into these materials.
        material_targets = {
            name: widget.get_editor_property(name).get_scalar_parameter_value('Health_Updated')
            for name in ('BarBorderMID', 'BarFillMID', 'BarGlowMID')
        }
        state['live_material_health_targets'] = material_targets
        if any(abs(target - health.get_health_normalized()) >= .01
               for target in material_targets.values()):
            # Original UMG animations can temporarily override material tracks
            # after Pawn initialization; require eventual display convergence.
            state['waiting_for'] = 'material animation convergence: ' + str(material_targets)
            record()
            if time.time() - state.get('phase_started', state['started']) > 20:
                raise RuntimeError(state['waiting_for'])
            return
        state['observations'].append({
            'phase': state['phase'], 'pawn': pawn.get_path_name(),
            'health': value, 'normalized': health.get_health_normalized(),
            'widget_normalized': widget.get_editor_property('NormalizedHealth'),
            'material_health_targets': material_targets,
            'numeric_target': displayed, 'widget': widget.get_path_name(),
            'driver': driver.get_path_name(), 'generation': driver.get_editor_property('active_generation'),
            'health_callback_count': current_count,
            'pawn_callback_count': driver.call_method(functions['GetPawnChangeCount'], ()),
        })
        if state['phase'] == 'play':
            state['original_pawn'] = pawn.get_path_name()
            state['original_widget'] = widget.get_path_name()
            state['original_driver'] = driver.get_path_name()
            state['world'] = world.get_path_name()
            command(world, pc, 'DamageSelf 20', 'damage')
        elif state['phase'] == 'damage':
            command(world, pc, 'HealSelf 10', 'heal')
        elif state['phase'] == 'heal':
            old_generation = driver.get_editor_property('active_generation')
            assert driver.reload_script(), driver.get_editor_property('last_script_error')
            assert driver.get_editor_property('active_generation') != old_generation
            assert driver.get_path_name() == state['original_driver']
            command(world, pc, 'DamageSelf 10', 'reload_damage')
        elif state['phase'] == 'reload_damage':
            command(world, pc, 'DamageSelfDestruct', 'respawn')
        elif state['phase'] == 'respawn':
            assert widget.get_path_name() == state['original_widget'], 'Original HUD was not retained through respawn'
            assert driver.get_path_name() == state['original_driver'], 'Driver was replaced through respawn'
            command(world, pc, 'DamageSelf 20', 'respawn_damage')
        elif state['phase'] == 'respawn_damage':
            driver.stop_script()
            assert not driver.get_editor_property('script_running')
            state['phase'] = 'stopped_damage'
            state['phase_started'] = time.time()
            unreal.SystemLibrary.execute_console_command(world, 'DamageSelf 5', pc)
        elif state['phase'] == 'restarted':
            command(world, pc, 'DamageSelf 5', 'restart_damage')
        else:
            state['phase'] = 'teardown'
            # Python UObject wrappers participate in UE GC. Release the last
            # global PIE component reference before requesting World cleanup.
            driver = None
            level.editor_request_end_play()
        record()
    except Exception:
        finish('failed', traceback.format_exc())


try:
    record()
    declaration = json.loads((root / 'Plugins/Finare/Binaries/Managed/Scripts/LyraStarterGame.Finare.asset.json').read_text(encoding='utf-8-sig'))
    asset = next(a for a in declaration['assets']
                 if (a.get('classBinding') or {}).get('generatedClassPath') == '/Game/FinarePilot/HealthHud.HealthHud_C')
    functions = {f['name']: 'USF_' + f['id'][3:] for f in asset['functions']}
    settings = unreal.get_default_object(unreal.LyraDeveloperSettings)
    settings.set_editor_property('override_bot_count', True)
    settings.set_editor_property('override_num_player_bots_to_spawn', 0)
    assert unreal.EditorLoadingAndSavingUtils.load_map('/ShooterMaps/Maps/L_Expanse')
    health_widget_class = unreal.load_class(None, '/Game/UI/Hud/W_Healthbar.W_Healthbar_C')
    assert health_widget_class
    state['phase'] = 'play'
    handle = unreal.register_slate_post_tick_callback(tick)
    assert unreal.FinarePilotPlayLibrary.request_play(False)
except Exception:
    finish('failed', traceback.format_exc())

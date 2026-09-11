"""Run with UE 5.8 Python in an authorized independent Lyra pilot checkout.

Set FINARE_LYRA_PILOT_ROOT to that checkout. First create and emit the normal
HealthHud Script Class and generated Lyra bindings. This edits the two Blueprint
assets through UE's graph API; it does not include or redistribute Lyra assets.
"""
import json
import os
import traceback
from pathlib import Path

import unreal

B = unreal.BlueprintEditorLibrary
P = unreal.BlueprintGraphPinLibrary
CLASS_ASSET = '/Game/FinarePilot/HealthHud.HealthHud'
WIDGET_ASSET = '/Game/UI/Hud/W_Healthbar.W_Healthbar'
DRIVER_PROPERTY = 'FinareHealthDriver'
RELEASE_FUNCTION = 'ReleaseFinareDriver'
MARKER = 'Finare.Lyra.HealthHud.Migration'


def connect(source, target):
    assert P.is_valid(source) and P.is_valid(target), 'Missing connection pin'
    # Some action spawners auto-connect their context pin.
    existing = P.list_connected_pins(source)
    if any(P.get_owning_node(p) == P.get_owning_node(target)
           and P.get_pin_name(p) == P.get_pin_name(target) for p in existing):
        return
    assert P.try_create_connection(source, target), 'Rejected graph connection'


def position(node, x, y):
    assert node, 'Node creation failed'
    B.set_node_pos(node, unreal.IntPoint(x, y))
    return node


def call(graph, path, x, y):
    return position(graph.add_call_function_node(path), x, y)


def compile_checked(bp):
    assert B.compile_blueprint(bp), 'Blueprint compilation failed: ' + bp.get_path_name()


def self_node(graph, x, y):
    names = ('变量|获得一个对自身的引用', 'Variables|Get a reference to self')
    name = next((n for n in graph.list_available_nodes([]) if n in names), None)
    assert name, 'Self node action is unavailable in this editor locale'
    return graph.create_node_from_name(name, unreal.Vector2D(x, y), [])


def bypass_delegate(graph, node):
    incoming = list(P.list_connected_pins(B.find_execute_pin(node)))
    outgoing = list(P.list_connected_pins(B.find_then_pin(node)))
    assert len(incoming) == 1 and len(outgoing) == 1, 'Unexpected original delegate flow'
    P.break_pin_links(B.find_execute_pin(node))
    P.break_pin_links(B.find_then_pin(node))
    connect(incoming[0], outgoing[0])
    graph.remove_nodes([node])


def migrate(root, report):
    declaration_path = root / 'Plugins/Finare/Binaries/Managed/Scripts/LyraStarterGame.Finare.asset.json'
    declaration = json.loads(declaration_path.read_text(encoding='utf-8-sig'))
    matching = [a for a in declaration['assets']
                if (a.get('classBinding') or {}).get('generatedClassPath') == CLASS_ASSET + '_C']
    assert len(matching) == 1, 'Expected one generated HealthHud class declaration'
    asset = matching[0]
    class_id = asset['id']
    initialize = next(f for f in asset['functions'] if f['name'] == 'Initialize')
    initialize_name = 'USF_' + initialize['id'][3:]
    driver_bp = unreal.load_asset(CLASS_ASSET)
    widget_bp = unreal.load_asset(WIDGET_ASSET)
    assert driver_bp and widget_bp, 'Expected normal Script Class and original Lyra widget'
    assert not unreal.EditorAssetLibrary.get_metadata_tag(widget_bp, MARKER), 'Widget is already migrated'
    graph = unreal.BlueprintGraphEditor.get_graph_editor_by_name(widget_bp, 'EventGraph')

    def original(name):
        node = unreal.load_object(None, WIDGET_ASSET + ':EventGraph.' + name)
        assert node, 'Original Lyra node missing: ' + name
        return node

    # Validate the known original flow before mutating either asset.
    bind_health = original('K2Node_AddDelegate_0')
    unbind_health = original('K2Node_RemoveDelegate_0')
    bind_pawn = original('K2Node_AddDelegate_3')
    old_initialize = original('K2Node_CallFunction_14')
    set_materials = original('K2Node_CallFunction_7')
    initialize_bars = original('K2Node_CallFunction_1')
    owning_player = original('K2Node_CallFunction_8')
    assert P.get_owning_node(P.list_connected_pins(B.find_then_pin(set_materials))[0]) == bind_pawn
    assert P.get_owning_node(P.list_connected_pins(B.find_then_pin(bind_pawn))[0]) == old_initialize
    assert P.get_owning_node(P.list_connected_pins(B.find_then_pin(old_initialize))[0]) == initialize_bars

    # UE permits a component to destroy itself. Calling this from its own graph
    # supplies the proper hidden Object context to K2_DestroyComponent.
    release = unreal.BlueprintGraphEditor.create_and_edit_function_graph(driver_bp, RELEASE_FUNCTION)
    destroy = call(release, '/Script/Engine.ActorComponent.K2_DestroyComponent', 240, 0)
    connect(release.find_graph_entry_pin(), B.find_execute_pin(destroy))
    compile_checked(driver_bp)
    driver_class = B.generated_class(driver_bp)
    assert driver_class

    assert B.add_member_variable(widget_bp, DRIVER_PROPERTY, B.get_object_reference_type(driver_class))
    cleanup = unreal.BlueprintGraphEditor.create_and_edit_function_graph(widget_bp, RELEASE_FUNCTION)
    get_driver = position(cleanup.add_get_member_variable_node(DRIVER_PROPERTY), 0, 160)
    is_valid = call(cleanup, '/Script/Engine.KismetSystemLibrary.IsValid', 240, 160)
    branch = position(cleanup.add_branch_node(), 480, 0)
    release_driver = call(cleanup, driver_class.get_path_name() + '.' + RELEASE_FUNCTION, 720, 0)
    connect(B.find_result_pin(get_driver), B.find_input_pin(is_valid, 'Object'))
    connect(cleanup.find_graph_entry_pin(), B.find_execute_pin(branch))
    connect(B.find_result_pin(is_valid), B.find_input_pin(branch, 'Condition'))
    connect(B.find_then_pin(branch), B.find_execute_pin(release_driver))
    connect(B.find_result_pin(get_driver), B.find_self_pin(release_driver))
    compile_checked(widget_bp)

    # Preserve the original animation reset and new-pawn display initialization.
    # Only responsibility for the native delegate subscriptions moves to C#.
    bypass_delegate(graph, bind_health)
    bypass_delegate(graph, unbind_health)
    P.break_pin_links(B.find_then_pin(set_materials))
    P.break_pin_links(B.find_execute_pin(initialize_bars))
    graph.remove_nodes([bind_pawn, old_initialize])

    cleanup_call = call(graph, B.generated_class(widget_bp).get_path_name() + '.' + RELEASE_FUNCTION, 900, 1600)
    owner_pin = B.find_result_pin(owning_player)
    actions = graph.list_available_nodes([owner_pin])
    add_name = next((n for n in actions if n in ('游戏|按类添加组件', 'Game|Add Component by Class')), None)
    assert add_name, 'Add Component by Class action is unavailable in this editor locale'
    add = graph.create_node_from_name(add_name, unreal.Vector2D(1150, 1600), [owner_pin])
    assert P.set_pin_value(B.find_input_pin(add, 'Class'), driver_class.get_path_name())
    set_driver = position(graph.add_set_member_variable_node(DRIVER_PROPERTY), 1420, 1600)
    initialize_call = call(graph, driver_class.get_path_name() + '.' + initialize_name, 1680, 1600)
    widget_self = self_node(graph, 1440, 1840)
    connect(B.find_then_pin(set_materials), B.find_execute_pin(cleanup_call))
    connect(B.find_then_pin(cleanup_call), B.find_execute_pin(add))
    connect(owner_pin, B.find_self_pin(add))
    connect(B.find_then_pin(add), B.find_execute_pin(set_driver))
    connect(B.find_result_pin(add), B.find_input_pin(set_driver, DRIVER_PROPERTY))
    connect(B.find_then_pin(set_driver), B.find_execute_pin(initialize_call))
    connect(B.find_result_pin(add), B.find_self_pin(initialize_call))
    widget_inputs = [p for p in B.list_input_pins(initialize_call)
                     if str(P.get_pin_name(p)) not in ('execute', 'self')]
    assert len(widget_inputs) == 1, 'Unexpected Initialize signature'
    connect(B.find_result_pin(widget_self), widget_inputs[0])
    connect(B.find_then_pin(initialize_call), B.find_execute_pin(initialize_bars))

    destruct = B.add_event_override(widget_bp, 'Destruct', unreal.IntPoint(900, 2200))
    assert not P.list_connected_pins(B.find_then_pin(destruct)), 'Preserve existing Destruct behavior before migration'
    cleanup_on_destruct = call(graph, B.generated_class(widget_bp).get_path_name() + '.' + RELEASE_FUNCTION, 1150, 2200)
    connect(B.find_then_pin(destruct), B.find_execute_pin(cleanup_on_destruct))
    compile_checked(widget_bp)
    unreal.EditorAssetLibrary.set_metadata_tag(widget_bp, MARKER, class_id)
    assert unreal.EditorAssetLibrary.save_loaded_asset(driver_bp, False)
    assert unreal.EditorAssetLibrary.save_loaded_asset(widget_bp, False)
    report.update({'class_id': class_id, 'initialize_function': initialize_name,
                   'driver': CLASS_ASSET, 'widget': WIDGET_ASSET,
                   'removed_native_delegate_nodes': 3, 'status': 'passed'})


root = Path(unreal.Paths.project_dir()).resolve()
report_path = root / 'Saved/Finare/LyraPilot/widget-migration.json'
report = {'status': 'running', 'project': str(root)}
try:
    authorized_root = os.environ.get('FINARE_LYRA_PILOT_ROOT', '')
    assert authorized_root and Path(authorized_root).resolve() == root, 'Set FINARE_LYRA_PILOT_ROOT to this independent checkout'
    assert root != Path('D:/Project/LyraStarterGame').resolve(), 'The original Lyra reference is read-only'
    migrate(root, report)
except Exception:
    report['status'] = 'failed'
    report['error'] = traceback.format_exc()
report_path.parent.mkdir(parents=True, exist_ok=True)
report_path.write_text(json.dumps(report, indent=2), encoding='utf-8')
unreal.log('FINARE_LYRA_WIDGET_MIGRATION_' + report['status'].upper())

const state = {
  view: 'structure', zoom: 1, artifactLoaded: false, graphDirty: false,
  sourceCompilationId: null, semanticConfigurationId: null,
  sourceText: '', sourcePath: 'workspace.cs', graphSchemaVersion: null,
  artifact: null, graph: null, surface: null,
  selectedAssetId: null, selectedFunctionId: null,
  asset: null, surfaceAsset: null, graphFunction: null, functionSurface: null,
  declarations: [], nodes: new Map(), selected: null,
  authoringConnected: false, authoringBusy: false, authoringRevision: null,
  generation: null, removedDeclarations: [], previewPatch: null,
  debugConnected: false, debugBusy: false, debugHealth: null, debugSession: null,
};

const editorBody = document.getElementById('editorBody');
const flowCanvas = document.getElementById('flowCanvas');
const zoomLabel = document.getElementById('zoomLabel');
const inspectorTitle = document.getElementById('inspectorTitle');
const inspectorBody = document.getElementById('inspectorBody');
const declarationSearch = document.getElementById('declarationSearch');
const debugStrip = document.getElementById('debugStrip');
const aiDrawer = document.getElementById('aiDrawer');
const variableDialog = document.getElementById('variableDialog');
const toast = document.getElementById('toast');

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>'"]/g, character => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;',
  })[character]);
}

function assertArtifact(condition, message) {
  if (!condition) throw new Error(message);
}

function semanticShort(value) {
  if (!value || value.length < 16) return value || '—';
  return `${value.slice(0, 10)}…${value.slice(-4)}`;
}

function shortType(metadataName) {
  const part = (metadataName || 'void').split(/[.+]/).at(-1);
  return ({ Boolean: 'bool', Int32: 'int', Single: 'float', Double: 'double', String: 'string', Void: 'void' })[part] || part;
}

function toPascalCase(value) {
  const normalized = value.trim().replace(/[^A-Za-z0-9_]+/g, ' ');
  return normalized.split(/\s+/).filter(Boolean)
    .map(part => part.charAt(0).toUpperCase() + part.slice(1)).join('') || 'NewState';
}

function artifactDefaultText(item) {
  if (item.typeKind === 'floatingPoint' && Number.isInteger(item.defaultValue)) return `${item.defaultValue}.0`;
  return String(item.defaultValue ?? 'null');
}

function activeBreakpointNodeIds() {
  return [...document.querySelectorAll('.breakpoint-toggle.active:not(:disabled)')]
    .map(button => button.closest('[data-debug-semantic-id]')?.dataset.debugSemanticId)
    .filter((value, index, values) => value && values.indexOf(value) === index);
}

function rememberSelection() {
  return {
    assetId: state.selectedAssetId,
    functionId: state.selectedFunctionId,
    selected: state.selected ? { ...state.selected } : null,
    breakpoints: activeBreakpointNodeIds(),
  };
}

async function loadWorkspaceArtifacts() {
  try {
    const response = await fetch('/api/authoring/workspace', { cache: 'no-store' });
    if (!response.ok) throw new Error(`Authoring workspace returned ${response.status}`);
    const workspace = await response.json();
    assertArtifact(workspace.schemaVersion === 'unifiedscript.authoring-workspace/v0.2', 'Unsupported authoring workspace protocol');
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.surface, workspace.sourceText, workspace);
    await connectDebugBridge();
    return;
  } catch (error) {
    console.info('Live authoring host is offline; loading checked-in Surface artifacts.', error.message);
  }

  try {
    const [assetResponse, graphResponse, surfaceResponse, sourceResponse] = await Promise.all([
      fetch('data/damage-policy.asset.json', { cache: 'no-store' }),
      fetch('data/damage-policy.graph.json', { cache: 'no-store' }),
      fetch('data/damage-policy.surface.json', { cache: 'no-store' }),
      fetch('data/damage-policy.source.cs', { cache: 'no-store' }),
    ]);
    [assetResponse, graphResponse, surfaceResponse, sourceResponse].forEach(response => {
      if (!response.ok) throw new Error(`Static artifact returned ${response.status}`);
    });
    const [artifact, graph, surface, sourceText] = await Promise.all([
      assetResponse.json(), graphResponse.json(), surfaceResponse.json(), sourceResponse.text(),
    ]);
    applyWorkspaceArtifacts(artifact, graph, surface, sourceText);
  } catch (error) {
    document.querySelector('.sync-status strong').textContent = 'SURFACE ERROR';
    flowCanvas.innerHTML = `<div class="surface-error"><strong>AUTHORING SURFACE UNAVAILABLE</strong><span>${escapeHtml(error.message)}</span></div>`;
    console.error(error);
  }
}

function validateWorkspaceArtifacts(artifact, graph, surface) {
  assertArtifact(artifact?.schemaVersion === 'unifiedscript.script-asset/v0.1', 'Unsupported Declaration IR artifact');
  assertArtifact(graph?.schemaVersion === 'unifiedscript.graph/v0.13', 'Unsupported Graph artifact');
  assertArtifact(surface?.schemaVersion === 'unifiedscript.authoring-surface/v0.4', 'Unsupported Authoring Surface artifact');
  assertArtifact(artifact.sourceCompilationId === graph.sourceCompilationId &&
    artifact.sourceCompilationId === surface.sourceCompilationId, 'Source compilation identity mismatch');
  assertArtifact(artifact.semanticConfigurationId === graph.semanticConfigurationId &&
    artifact.semanticConfigurationId === surface.semanticConfigurationId, 'Semantic configuration identity mismatch');
  assertArtifact(Array.isArray(artifact.assets) && Array.isArray(graph.functions) && Array.isArray(surface.assets), 'Malformed authoring artifacts');
  const declarationAssets = new Map(artifact.assets.map(asset => [asset.id, asset]));
  const graphFunctions = new Map(graph.functions.map(func => [func.id, func]));
  surface.assets.forEach(asset => {
    assertArtifact(declarationAssets.has(asset.id), `Surface asset ${asset.id} has no declaration`);
    asset.functions.forEach(func => {
      assertArtifact(graphFunctions.has(func.id), `Surface function ${func.id} has no Graph function`);
      const regionIds = new Set(func.regions.map(region => region.id));
      const actionIds = new Set(func.actions.map(action => action.id));
      assertArtifact(regionIds.has(func.rootRegionId), `Surface function ${func.id} has no root region`);
      func.regions.forEach(region => {
        region.childRegionIds.forEach(id => assertArtifact(regionIds.has(id), `Unknown child region ${id}`));
        region.directActionIds.forEach(id => assertArtifact(actionIds.has(id), `Unknown action ${id}`));
      });
    });
  });
}

function applyWorkspaceArtifacts(artifact, graph, surface, sourceText, workspace = null) {
  validateWorkspaceArtifacts(artifact, graph, surface);
  const remembered = rememberSelection();
  Object.assign(state, {
    sourceCompilationId: artifact.sourceCompilationId,
    semanticConfigurationId: artifact.semanticConfigurationId,
    sourceText,
    sourcePath: workspace?.sourcePath || artifact.assets[0]?.span?.filePath || 'workspace.cs',
    graphSchemaVersion: graph.schemaVersion,
    artifact, graph, surface,
    authoringConnected: Boolean(workspace),
    authoringRevision: workspace?.revision ?? null,
    generation: workspace?.generation ?? null,
    artifactLoaded: true, graphDirty: false, removedDeclarations: [], previewPatch: null,
  });
  if (workspace) {
    state.debugConnected = true;
    state.debugHealth = workspace.generation;
  }
  renderAssetPicker();
  const assetId = surface.assets.some(asset => asset.id === remembered.assetId)
    ? remembered.assetId : surface.assets[0]?.id;
  selectAsset(assetId, false, remembered.functionId, remembered.selected);
  restoreBreakpoints(remembered.breakpoints);
  if (!remembered.breakpoints.length) enableDefaultBreakpoint();
  setAuthoringBusy(false);
  document.querySelector('.sync-status strong').textContent = workspace
    ? `SYNCED · R${workspace.revision}` : 'SURFACE · READ ONLY';
  document.querySelector('.target-label').textContent = workspace
    ? `CLR GENERATION ${workspace.generation.number} · SURFACE v0.1`
    : 'AUTHORING SURFACE v0.1 · GRAPH v0.12';
}

function renderAssetPicker() {
  document.getElementById('assetPicker').innerHTML = state.surface.assets.map(asset =>
    `<option value="${asset.id}">${escapeHtml(asset.assetPath)}</option>`).join('');
}

function selectAsset(assetId, revealInspector = true, preferredFunctionId = null, preferredSelection = null) {
  const surfaceAsset = state.surface.assets.find(asset => asset.id === assetId);
  const asset = state.artifact.assets.find(candidate => candidate.id === assetId);
  if (!surfaceAsset || !asset) return;
  state.selectedAssetId = assetId;
  state.asset = asset;
  state.surfaceAsset = surfaceAsset;
  document.getElementById('assetPicker').value = assetId;
  document.getElementById('assetPathLabel').textContent = surfaceAsset.qualifiedName;
  state.declarations = asset.states.map(item => ({
    id: item.id, name: item.name, type: item.typeDisplayName, typeKind: item.typeKind,
    defaultValue: artifactDefaultText(item), editable: item.exposure.instanceEditable,
    save: item.exposure.saveGame, replicated: item.exposure.replicated,
    category: item.exposure.category || '', semanticId: item.id,
    sourceStartLine: item.span.start.line, sourceEndLine: item.span.end.line,
    original: {
      name: item.name, typeMetadataName: item.typeMetadataName, typeKind: item.typeKind,
      defaultValue: item.defaultValue,
      exposure: {
        instanceEditable: item.exposure.instanceEditable, saveGame: item.exposure.saveGame,
        replicated: item.exposure.replicated,
        ...(item.exposure.category ? { category: item.exposure.category } : {}),
      },
    },
  }));
  state.removedDeclarations = [];
  renderDeclarationList();
  renderFunctionList();
  renderSourceArtifact();
  renderAiDeclarations();
  const functionId = surfaceAsset.functions.some(func => func.id === preferredFunctionId)
    ? preferredFunctionId : surfaceAsset.functions[0]?.id;
  selectFunction(functionId, revealInspector, preferredSelection);
}

function renderDeclarationList() {
  const list = document.getElementById('stateList');
  list.innerHTML = state.declarations.map(item => `
    <button class="declaration-item" type="button" data-kind="state" data-id="${item.id}">
      <span class="declaration-glyph state-glyph">S</span>
      <span class="declaration-copy"><strong>${escapeHtml(item.name)}</strong><small>${escapeHtml(item.type)} · ${escapeHtml(item.defaultValue)}</small></span>
      ${item.editable ? '<span class="exposure-dot" title="Instance Editable"></span>' : item.save ? '<span class="save-dot" title="SaveGame"></span>' : '<span></span>'}
    </button>`).join('') || '<div class="empty-list">NO SCRIPT STATE</div>';
  list.querySelectorAll('.declaration-item').forEach(button =>
    button.addEventListener('click', () => selectDeclaration(button.dataset.id)));
  document.getElementById('stateCount').textContent = String(state.declarations.length);
}

function functionSignature(func) {
  return `(${func.parameters.map(parameter => shortType(parameter.typeMetadataName)).join(', ')}) → ${shortType(func.returnTypeMetadataName)}`;
}

function renderFunctionList() {
  const list = document.getElementById('functionList');
  list.innerHTML = state.surfaceAsset.functions.map(func => `
    <button class="declaration-item function-item" type="button" data-kind="function" data-id="${func.id}">
      <span class="declaration-glyph function-glyph">ƒ</span>
      <span class="declaration-copy"><strong>${escapeHtml(func.name)}</strong><small>${escapeHtml(functionSignature(func))}</small></span>
      <span class="function-arrow">›</span>
    </button>`).join('') || '<div class="empty-list">NO VISUAL FUNCTIONS</div>';
  list.querySelectorAll('.function-item').forEach(button =>
    button.addEventListener('click', () => selectFunction(button.dataset.id)));
  const count = state.surfaceAsset.functions.length;
  document.getElementById('functionCount').textContent = String(count);
  document.getElementById('functionSectionCount').textContent = String(count);
}

function selectFunction(functionId, revealInspector = true, preferredSelection = null) {
  const func = state.surfaceAsset?.functions.find(candidate => candidate.id === functionId);
  const graphFunction = state.graph?.functions.find(candidate => candidate.id === functionId);
  if (!func || !graphFunction) {
    flowCanvas.innerHTML = '<div class="surface-error">THIS ASSET HAS NO VISUAL FUNCTION</div>';
    return;
  }
  state.selectedFunctionId = functionId;
  state.functionSurface = func;
  state.graphFunction = graphFunction;
  state.nodes = new Map(func.actions.map(action => [action.id, action]));
  document.querySelectorAll('.function-item').forEach(button =>
    button.classList.toggle('selected', button.dataset.id === functionId));
  document.getElementById('functionTitle').textContent = func.name;
  document.getElementById('functionSubtitle').textContent = `${state.surfaceAsset.assetPath} / ${func.isStatic ? 'static' : 'instance'} / ${func.actions.length} semantic actions`;
  renderBehaviorGraph();
  renderAiBehavior();
  if (preferredSelection?.kind === 'state' && state.declarations.some(item => item.semanticId === preferredSelection.id)) {
    selectDeclaration(preferredSelection.id, revealInspector);
  } else if (preferredSelection?.kind === 'action' && state.nodes.has(preferredSelection.id)) {
    selectNode(preferredSelection.id, revealInspector);
  } else {
    const entry = func.actions.find(action => action.category === 'entry') || func.actions[0];
    if (entry) selectNode(entry.id, revealInspector);
  }
}

function spanOrder(item) {
  return (item?.span?.start?.line || 0) * 10000 + (item?.span?.start?.column || 0);
}

function renderBehaviorGraph() {
  const func = state.functionSurface;
  const regions = new Map(func.regions.map(region => [region.id, region]));
  const actions = new Map(func.actions.map(action => [action.id, action]));
  const ordinals = new Map(func.actions.slice()
    .sort((left, right) => spanOrder(left) - spanOrder(right) || left.id.localeCompare(right.id))
    .map((action, index) => [action.id, String(index + 1).padStart(2, '0')]));
  const connector = label => `<div class="flow-connector"><span>${escapeHtml(label)}</span></div>`;
  const renderAction = (actionId, compact = false) => {
    const action = actions.get(actionId);
    if (!action) return '';
    const editBadge = action.editCapabilities.length
      ? `<span class="edit-badge">${action.editCapabilities.length} EDIT${action.editCapabilities.length > 1 ? 'S' : ''}</span>` : '';
    return `<article class="flow-node action-${action.category}${compact ? ' compact region-action' : ''}"
      data-node-id="${action.id}" data-semantic-id="${action.id}" data-debug-semantic-id="${action.debugTargetId || ''}"
      data-line="${action.span.start.line}" tabindex="0">
      <div class="node-ordinal">${ordinals.get(action.id)}</div>
      <div class="node-content"><div class="node-kicker">${escapeHtml(action.category)} · ${escapeHtml(action.graphKind)}</div>
      <h3>${escapeHtml(action.title)}</h3><p class="action-summary">${escapeHtml(action.summary)}</p>
      <div class="action-meta">${editBadge}${action.declarationReferenceIds.length ? `<span>${action.declarationReferenceIds.length} STATE REF</span>` : ''}</div></div>
      <button class="breakpoint-toggle" type="button" aria-label="切换语义断点"${action.debugTargetId ? '' : ' disabled'}></button>
    </article>`;
  };
  const renderSequence = region => {
    const items = [
      ...region.directActionIds.map(id => ({ kind: 'action', id, order: spanOrder(actions.get(id)) })),
      ...region.childRegionIds.map(id => ({ kind: 'region', id, order: spanOrder(regions.get(id)) })),
    ].sort((left, right) => left.order - right.order || left.id.localeCompare(right.id));
    return items.map((item, index) =>
      `${index ? connector('then') : ''}${item.kind === 'action' ? renderAction(item.id) : renderRegion(item.id)}`).join('');
  };
  const renderLane = childId => {
    const child = regions.get(childId);
    const names = {
      then: 'TRUE', else: child?.isImplicit ? 'FALSE · IMPLICIT' : 'FALSE',
      loopInitializer: 'INITIALIZE', loopCollection: 'COLLECTION', loopBody: 'BODY',
      loopIncrement: 'INCREMENT', loopCleanup: 'CLEANUP', block: 'BODY',
    };
    const label = names[child?.kind] || child?.kind || 'REGION';
    const content = child ? renderSequence(child) : '';
    return `<div class="region-lane lane-${escapeHtml(child?.kind || 'unknown')}">
      <div class="lane-label"><span>${escapeHtml(label)}</span><i></i></div>
      <div class="lane-content">${content || `<div class="pass-through"><span>NO OP</span><small>${child?.isImplicit ? 'implicit pass-through' : 'empty region'}</small></div>`}</div>
    </div>`;
  };
  const renderRegion = regionId => {
    const region = regions.get(regionId);
    if (!region) return '';
    if (region.kind !== 'if' && region.kind !== 'loop') return renderSequence(region);
    const anchor = actions.get(region.anchorActionId);
    const prelude = region.directActionIds.filter(id => id !== region.anchorActionId)
      .map(id => renderAction(id)).join(connector('then'));
    return `${prelude}${prelude ? connector('enter region') : ''}<section class="semantic-region ${region.kind === 'if' ? 'branch-region' : 'loop-region'}"
      data-node-id="${anchor?.id || ''}" data-semantic-id="${anchor?.id || ''}"
      data-debug-semantic-id="${anchor?.debugTargetId || ''}" data-region-id="${region.id}"
      data-line="${anchor?.span?.start?.line || region.span.start.line}" tabindex="0">
      <header class="region-header"><div><span class="region-index">${anchor ? ordinals.get(anchor.id) : '—'}</span>
      <span class="node-kicker">STRUCTURED ${escapeHtml(region.kind)} REGION</span>
      <h3>${escapeHtml(anchor?.summary || region.kind)}</h3></div>
      <div class="region-tools">${anchor?.editCapabilities?.length ? `<span class="edit-badge">${anchor.editCapabilities.length} EDITS</span>` : ''}<span class="region-badge">${escapeHtml(region.kind)}</span><button class="breakpoint-toggle" type="button" aria-label="切换语义断点"${anchor?.debugTargetId ? '' : ' disabled'}></button></div></header>
      <div class="region-lanes ${region.kind}-lanes">${region.childRegionIds.map(renderLane).join('')}</div>
    </section>`;
  };
  flowCanvas.innerHTML = `<div class="flow-rail" aria-hidden="true"></div>${renderRegion(func.rootRegionId)}`;
  bindBehaviorInteractions(flowCanvas);
}

function renderAiDeclarations() {
  document.getElementById('aiDeclarations').textContent = state.declarations.map(item => {
    const exposure = [item.editable && 'instanceEditable', item.save && 'saveGame', item.replicated && 'replicated'].filter(Boolean);
    return `state ${item.name}: ${item.type} = ${item.defaultValue}${exposure.length ? `\n  expose: ${exposure.join(', ')}` : ''}${item.category ? `\n  category: ${item.category}` : ''}\n  identity: ${item.semanticId}`;
  }).join('\n\n') || 'no script state';
}

function regionDepth(regionId) {
  const regions = new Map(state.functionSurface.regions.map(region => [region.id, region]));
  let depth = 0;
  let current = regions.get(regionId);
  while (current?.parentRegionId) {
    depth += 1;
    current = regions.get(current.parentRegionId);
  }
  return Math.max(0, depth - 1);
}

function renderAiBehavior() {
  if (!state.functionSurface) return;
  const actions = state.functionSurface.actions.slice().sort((left, right) => spanOrder(left) - spanOrder(right));
  document.getElementById('aiBehavior').textContent = actions.map(action => {
    const indent = '  '.repeat(regionDepth(action.regionId));
    return `${indent}${action.category === 'entry' ? 'function' : action.graphKind} ${action.summary}\n${indent}  identity: ${action.id}${action.editCapabilities.length ? ` · editable: ${action.editCapabilities.map(edit => edit.operation).join(', ')}` : ''}`;
  }).join('\n');
}

function csharpLineMarkup(line) {
  const tokenPattern = /("(?:\\.|[^"\\])*"|\/\/.*|\b(?:using|namespace|public|private|protected|internal|sealed|static|class|enum|get|set|if|else|while|for|foreach|do|break|continue|return|float|double|int|bool|string|true|false|void|var)\b|\b\d+(?:\.\d+)?[fF]?\b|\b(?:ScriptAsset|Expose|Visual)\b)/g;
  let cursor = 0;
  let markup = '';
  for (const match of line.matchAll(tokenPattern)) {
    markup += escapeHtml(line.slice(cursor, match.index));
    const token = match[0];
    if (token.startsWith('//')) markup += `<span class="code-comment">${escapeHtml(token)}</span>`;
    else if (token.startsWith('"')) markup += `<s>${escapeHtml(token)}</s>`;
    else if (/^(?:ScriptAsset|Expose|Visual)$/.test(token)) markup += `<u>${token}</u>`;
    else if (/^\d/.test(token)) markup += `<n>${escapeHtml(token)}</n>`;
    else markup += `<i>${token}</i>`;
    cursor = match.index + token.length;
  }
  return markup + escapeHtml(line.slice(cursor));
}

function renderSourceArtifact() {
  const statesByLine = new Map();
  state.declarations.forEach(item => {
    for (let line = item.sourceStartLine; line <= item.sourceEndLine; line += 1) statesByLine.set(line, item);
  });
  const lines = state.sourceText.replace(/\r\n/g, '\n').replace(/\n$/, '').split('\n');
  document.querySelector('#codeEditor code').innerHTML = lines.map((line, index) => {
    const lineNumber = index + 1;
    const declaration = statesByLine.get(lineNumber);
    const attributes = declaration ? ` class="state-line" data-state-id="${declaration.id}"` : '';
    return `<span data-line="${lineNumber}"${attributes}><b>${lineNumber}</b>${csharpLineMarkup(line)}</span>`;
  }).join('\n');
  document.querySelector('.code-heading > div').innerHTML = `<span class="language-dot"></span>${escapeHtml(state.sourcePath.split('/').at(-1))} <em>CANONICAL TEXT</em>`;
}

function showCompactInspector() {
  if (window.innerWidth <= 900) document.querySelector('.inspector-panel').classList.add('compact-visible');
}

function selectDeclaration(id, revealInspector = true) {
  const item = state.declarations.find(candidate => candidate.id === id || candidate.semanticId === id);
  if (!item) return;
  state.selected = { kind: 'state', id: item.semanticId };
  document.querySelectorAll('.declaration-item').forEach(element =>
    element.classList.toggle('selected', element.dataset.kind === 'state' && element.dataset.id === item.id));
  document.querySelectorAll('.flow-node, .semantic-region').forEach(element => element.classList.remove('selected'));
  document.querySelectorAll('.state-line').forEach(line =>
    line.classList.toggle('state-highlight', line.dataset.stateId === item.id));
  document.querySelectorAll('#codeEditor [data-line]').forEach(line => line.classList.remove('highlighted'));
  document.getElementById('selectedLine').textContent = String(item.sourceStartLine || 1);
  renderStateInspector(item);
  if (revealInspector) showCompactInspector();
}

function exposureText(item) {
  const values = [];
  if (item.editable) values.push('InstanceEditable = true');
  if (item.save) values.push('SaveGame = true');
  if (item.replicated) values.push('Replicated = true');
  if (item.category) values.push(`Category = "${item.category}"`);
  return values.length ? `[Expose(${values.join(', ')})]\n` : '';
}

function normalizedDefault(item) {
  if (item.type === 'float' && !/[fF]$/.test(item.defaultValue)) return `${item.defaultValue}f`;
  if (item.type === 'string' && !/^".*"$/.test(item.defaultValue)) return `"${item.defaultValue}"`;
  return item.defaultValue;
}

function canonicalDeclaration(item) {
  return `${exposureText(item)}public ${item.type} ${item.name} { get; set; } = ${normalizedDefault(item)};`;
}

function toggleRow(id, title, subtitle, checked) {
  return `<label class="toggle-row"><span><strong>${title}</strong><small>${subtitle}</small></span><input id="${id}" type="checkbox"${checked ? ' checked' : ''}><i></i></label>`;
}

function renderStateInspector(item) {
  inspectorTitle.textContent = item.name;
  inspectorBody.innerHTML = `
    <div class="selection-kind"><span class="selection-icon">S</span><div><strong>SCRIPT STATE</strong><small>Declaration IR · persistent identity</small></div></div>
    <section class="inspector-section"><h3>DECLARATION</h3>
      <label class="field-label">Name<input id="propertyName" type="text" value="${escapeHtml(item.name)}"></label>
      <div class="field-grid"><label class="field-label">Type<select id="propertyType">${['int', 'float', 'bool', 'string'].map(type => `<option${type === item.type ? ' selected' : ''}>${type}</option>`).join('')}</select></label>
      <label class="field-label">Default<input id="propertyDefault" type="text" value="${escapeHtml(item.defaultValue)}"></label></div>
      <label class="field-label">Category<input id="propertyCategory" type="text" value="${escapeHtml(item.category)}" placeholder="Optional"></label>
    </section>
    <section class="inspector-section"><h3>ENGINE EXPOSURE</h3>
      ${toggleRow('toggleEditable', 'Instance Editable', 'Future UE Details panel', item.editable)}
      ${toggleRow('toggleSave', 'Save Game', 'Persistent state', item.save)}
      ${toggleRow('toggleReplicated', 'Replicated', 'Network contract', item.replicated)}
    </section>
    <section class="inspector-section source-contract"><div class="section-title-row"><h3>CANONICAL TEXT</h3><span>AI READABLE</span></div><pre id="canonicalDeclaration">${escapeHtml(canonicalDeclaration(item))}</pre></section>
    <section class="inspector-section identity-section"><h3>SEMANTIC IDENTITY</h3><button id="semanticId" class="identity-value" type="button">${semanticShort(item.semanticId)}</button><div class="identity-facts"><span>TEXT <b>✓</b></span><span>SURFACE <b>✓</b></span><span>AI <b>✓</b></span></div></section>
    <button id="removeStateButton" class="inspector-danger" type="button">REMOVE STATE</button>
    <div class="prototype-warning"><strong>DECLARATION IR + PATCH v0.2</strong><p>变量只声明一次；文本、可视化、AI 与未来 UE 反射层共享同一个语义身份。</p></div>`;
  bindStateInspector(item);
}

function bindStateInspector(item) {
  const controls = {
    name: document.getElementById('propertyName'), type: document.getElementById('propertyType'),
    defaultValue: document.getElementById('propertyDefault'), category: document.getElementById('propertyCategory'),
    editable: document.getElementById('toggleEditable'), save: document.getElementById('toggleSave'),
    replicated: document.getElementById('toggleReplicated'),
  };
  const update = () => {
    item.name = controls.name.value || item.name;
    item.type = controls.type.value;
    item.defaultValue = controls.defaultValue.value;
    item.category = controls.category.value.trim();
    item.editable = controls.editable.checked;
    item.save = controls.save.checked;
    item.replicated = controls.replicated.checked;
    document.getElementById('canonicalDeclaration').textContent = canonicalDeclaration(item);
    inspectorTitle.textContent = item.name;
    renderDeclarationList();
    document.querySelector(`.declaration-item[data-id="${item.id}"]`)?.classList.add('selected');
    renderAiDeclarations();
    markGraphDirty();
  };
  Object.values(controls).forEach(control => control.addEventListener('input', update));
  document.getElementById('semanticId').addEventListener('click', () => copyText(item.semanticId, 'Semantic ID copied'));
  document.getElementById('removeStateButton').addEventListener('click', () => removeDeclaration(item));
}

function removeDeclaration(item) {
  const index = state.declarations.indexOf(item);
  if (index < 0) return;
  if (item.original) state.removedDeclarations.push(item);
  state.declarations.splice(index, 1);
  renderDeclarationList();
  renderAiDeclarations();
  markGraphDirty();
  const next = state.declarations[Math.min(index, state.declarations.length - 1)];
  if (next) selectDeclaration(next.id, false);
  else selectNode(state.functionSurface.actions[0]?.id, false);
  showToast(`${item.name} marked for removal`, 'Commit Patch will validate behavior references');
}

function selectNode(id, revealInspector = true) {
  const node = state.nodes.get(id);
  if (!node) return;
  state.selected = { kind: 'action', id };
  document.querySelectorAll('.declaration-item').forEach(element =>
    element.classList.toggle('selected', element.dataset.kind === 'function' && element.dataset.id === state.selectedFunctionId));
  document.querySelectorAll('.state-line').forEach(line => line.classList.remove('state-highlight'));
  document.querySelectorAll('.flow-node, .semantic-region').forEach(element =>
    element.classList.toggle('selected', element.dataset.nodeId === id));
  document.querySelectorAll('#codeEditor [data-line]').forEach(line =>
    line.classList.toggle('highlighted', Number(line.dataset.line) === node.span.start.line));
  document.getElementById('selectedLine').textContent = String(node.span.start.line);
  renderNodeInspector(node);
  if (revealInspector) showCompactInspector();
}

function editControl(edit) {
  if (edit.kind === 'operator') {
    return `<label class="field-label">${escapeHtml(edit.label)}<select data-edit-input="${edit.id}">${edit.options.map(option => `<option${option === edit.currentValue ? ' selected' : ''}>${escapeHtml(option)}</option>`).join('')}</select></label>`;
  }
  return `<label class="field-label">${escapeHtml(edit.label)}<input data-edit-input="${edit.id}" type="text" value="${escapeHtml(edit.currentValue)}" spellcheck="false"></label>`;
}

function renderNodeInspector(node) {
  inspectorTitle.textContent = node.title;
  const editSection = node.editCapabilities.length ? `
    <section class="inspector-section behavior-editor"><div class="section-title-row"><h3>SEMANTIC EDITS</h3><span>SURFACE CAPABILITIES</span></div>
      ${node.editCapabilities.map(edit => `<div class="semantic-edit" data-edit="${edit.id}">${editControl(edit)}<button class="button primary inspector-save" data-apply-edit="${edit.id}" type="button">APPLY ${escapeHtml(edit.operation)}</button><small>Target ${semanticShort(edit.targetNodeId)} · typed precondition</small></div>`).join('')}
    </section>` : '';
  const references = node.declarationReferenceIds.map(id =>
    state.declarations.find(item => item.semanticId === id)?.name || semanticShort(id));
  inspectorBody.innerHTML = `
    <div class="selection-kind"><span class="selection-icon">N</span><div><strong>${escapeHtml(node.category)} · ${escapeHtml(node.graphKind)}</strong><small>Authoring Surface action</small></div></div>
    <section class="inspector-section"><h3>INTENT</h3><div class="node-inspector-title">${escapeHtml(node.title)}</div><p class="node-inspector-copy">${escapeHtml(node.summary)}</p></section>
    <section class="inspector-section"><h3>SOURCE + REFERENCES</h3>
      <div class="fact-grid"><span>${escapeHtml(state.sourcePath.split('/').at(-1))}</span><strong>Line ${node.span.start.line}</strong></div>
      <div class="fact-grid"><span>Owned by region</span><strong>${semanticShort(node.regionId)}</strong></div>
      <div class="fact-grid"><span>Script state</span><strong>${escapeHtml(references.join(', ') || 'none')}</strong></div>
    </section>${editSection}
    <section class="inspector-section source-contract"><div class="section-title-row"><h3>AI OUTLINE</h3><span>SEMANTIC</span></div><pre>${escapeHtml(`${node.graphKind} ${node.summary}\nidentity: ${node.id}`)}</pre></section>
    <section class="inspector-section identity-section"><h3>ACTION IDENTITY</h3><button id="semanticId" class="identity-value" type="button">${semanticShort(node.id)}</button><h3 class="debug-identity-heading">DEBUG TARGET</h3><button id="debugSemanticId" class="identity-value debug-identity" type="button"${node.debugTargetId ? '' : ' disabled'}>${semanticShort(node.debugTargetId)}</button><div class="identity-facts"><span>TEXT <b>✓</b></span><span>SURFACE <b>✓</b></span><span>DEBUG <b>${node.debugTargetId ? '✓' : '—'}</b></span></div></section>
    <div class="prototype-warning"><strong>AUTHORING SURFACE v0.1</strong><p>UI 不推断 Graph 结构；层级、摘要、断点身份与编辑能力均由通用 .NET 投影器提供。</p></div>`;
  document.getElementById('semanticId').addEventListener('click', () => copyText(node.id, 'Action ID copied'));
  if (node.debugTargetId) {
    document.getElementById('debugSemanticId').addEventListener('click', () => copyText(node.debugTargetId, 'Debug target copied'));
  }
  document.querySelectorAll('[data-apply-edit]').forEach(button =>
    button.addEventListener('click', () => applySurfaceEdit(node, button.dataset.applyEdit)));
}

function applySurfaceEdit(node, editId) {
  const edit = node.editCapabilities.find(candidate => candidate.id === editId);
  const input = document.querySelector(`[data-edit-input="${editId}"]`);
  const nextValue = input.value.trim();
  if (!nextValue || nextValue === edit.currentValue) {
    showToast('No semantic change', `${edit.label} is already ${edit.currentValue}`);
    return;
  }
  const operation = edit.kind === 'literal' ? {
    operation: 'replaceLiteral', targetNodeId: edit.targetNodeId,
    expectedValueText: edit.expectedValueText, newValueText: nextValue,
  } : {
    operation: 'changeOperator', targetNodeId: edit.targetNodeId,
    expectedOperator: edit.expectedOperator, newOperator: nextValue,
    expectedResultTypeId: edit.expectedResultTypeId,
    expectedOperandTypeId: edit.expectedOperandTypeId,
    ...(edit.expectedRightOperandTypeId ? { expectedRightOperandTypeId: edit.expectedRightOperandTypeId } : {}),
  };
  saveSemanticPatch({
    schemaVersion: 'unifiedscript.patch/v0.23',
    sourceCompilationId: state.sourceCompilationId,
    semanticConfigurationId: state.semanticConfigurationId,
    operations: [operation],
  });
}

function bindBehaviorInteractions(root = document) {
  root.querySelectorAll('.flow-node, .semantic-region').forEach(element => {
    element.addEventListener('click', event => {
      if (event.target.closest('.breakpoint-toggle')) return;
      if (event.target.closest('[data-node-id]') !== element) return;
      selectNode(element.dataset.nodeId);
    });
    element.addEventListener('keydown', event => {
      if (event.target !== element || !['Enter', ' '].includes(event.key)) return;
      event.preventDefault();
      selectNode(element.dataset.nodeId);
    });
  });
  root.querySelectorAll('.breakpoint-toggle').forEach(button => button.addEventListener('click', event => {
    event.stopPropagation();
    if (!button.disabled) button.classList.toggle('active');
  }));
}

function typeKindFor(type) {
  return ({ bool: 'boolean', int: 'integer', float: 'floatingPoint', string: 'string' })[type];
}

function metadataTypeFor(type) {
  return ({ bool: 'System.Boolean', int: 'System.Int32', float: 'System.Single', string: 'System.String' })[type];
}

function typedDefault(item) {
  const text = item.defaultValue.trim();
  if (item.type === 'bool') {
    if (!/^(true|false)$/i.test(text)) throw new Error(`${item.name} requires true or false`);
    return text.toLowerCase() === 'true';
  }
  if (item.type === 'int') {
    const value = Number(text);
    if (!/^[+-]?\d+$/.test(text) || !Number.isSafeInteger(value)) throw new Error(`${item.name} has an invalid int default`);
    return value;
  }
  if (item.type === 'float') {
    const value = Number(text);
    if (!text || !Number.isFinite(value)) throw new Error(`${item.name} has an invalid float default`);
    return value;
  }
  return item.defaultValue;
}

function exposureContract(item) {
  return {
    instanceEditable: item.editable, saveGame: item.save, replicated: item.replicated,
    ...(item.category ? { category: item.category } : {}),
  };
}

function replacementContract(item) {
  return {
    name: item.name, typeName: item.type, typeKind: typeKindFor(item.type),
    defaultValue: typedDefault(item), exposure: exposureContract(item),
  };
}

function declarationChanged(item) {
  if (!item.original) return true;
  return JSON.stringify({
    name: item.name, typeMetadataName: metadataTypeFor(item.type), typeKind: typeKindFor(item.type),
    defaultValue: typedDefault(item), exposure: exposureContract(item),
  }) !== JSON.stringify(item.original);
}

function aiSuggestedDeclaration(declarations) {
  const target = declarations.find(item => !item.editable) || declarations[0];
  if (!target) return null;
  target.editable = true;
  target.category ||= 'Gameplay';
  return target;
}

function buildDeclarationPatch(includeAiSuggestion = false) {
  const declarations = state.declarations.map(item => ({ ...item }));
  if (includeAiSuggestion) aiSuggestedDeclaration(declarations);
  const operations = declarations.flatMap(item => {
    if (!item.original) return [{ operation: 'addState', targetAssetId: state.selectedAssetId, state: replacementContract(item) }];
    if (!declarationChanged(item)) return [];
    return [{ operation: 'updateState', targetStateId: item.semanticId, expected: item.original, replacement: replacementContract(item) }];
  }).concat(state.removedDeclarations.map(item => ({
    operation: 'removeState', targetStateId: item.semanticId, expected: item.original,
  })));
  return {
    schemaVersion: 'unifiedscript.declaration-patch/v0.2',
    sourceCompilationId: state.sourceCompilationId,
    semanticConfigurationId: state.semanticConfigurationId,
    operations,
  };
}

function showDeclarationPatchPreview() {
  const preview = document.getElementById('patchPreview');
  const summary = document.getElementById('patchPreviewSummary');
  const json = document.getElementById('patchPreviewJson');
  const applyButton = document.getElementById('applyPreviewPatchButton');
  try {
    const patch = buildDeclarationPatch(true);
    state.previewPatch = patch;
    const suggested = aiSuggestedDeclaration(state.declarations.map(item => ({ ...item })));
    summary.textContent = suggested
      ? `${suggested.name}: Instance Editable / ${suggested.category}` : 'No state is available';
    json.textContent = JSON.stringify(patch, null, 2);
    applyButton.disabled = !state.authoringConnected || patch.operations.length === 0;
  } catch (error) {
    state.previewPatch = null;
    summary.textContent = error.message;
    json.textContent = '';
    applyButton.disabled = true;
  }
  preview.classList.add('visible');
}

function markGraphDirty() {
  state.graphDirty = true;
  document.querySelector('.sync-status strong').textContent = 'PATCH PENDING';
  document.querySelector('.target-label').textContent = 'DECLARATION PATCH · SURFACE BASELINE';
  document.querySelector('.dirty-dot').classList.add('visible');
  document.getElementById('saveButton').disabled = !state.authoringConnected || state.authoringBusy;
}

function setAuthoringBusy(busy) {
  state.authoringBusy = busy;
  const saveButton = document.getElementById('saveButton');
  saveButton.disabled = busy || !state.authoringConnected || !state.graphDirty;
  saveButton.textContent = busy ? 'COMMITTING…' : 'COMMIT PATCH';
  document.querySelector('.dirty-dot').classList.toggle('visible', state.graphDirty);
}

async function authoringRequest(path, patch) {
  const response = await fetch(path, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(patch),
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    const error = new Error(payload.diagnostics?.[0]?.message || payload.error?.message || `Authoring returned ${response.status}`);
    error.code = payload.error?.code;
    throw error;
  }
  if (payload.schemaVersion !== 'unifiedscript.authoring-workspace/v0.2') throw new Error('Unsupported authoring response');
  return payload;
}

async function saveDeclarationChanges(patch = null) {
  if (!state.authoringConnected) return showToast('Read-only artifacts', 'Start debug-serve to write source changes');
  try {
    const nextPatch = patch || buildDeclarationPatch();
    if (!nextPatch.operations.length) return showToast('Nothing to save', 'Declarations are synchronized');
    setAuthoringBusy(true);
    const workspace = await authoringRequest('/api/authoring/declaration-patch', nextPatch);
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.surface, workspace.sourceText, workspace);
    showToast(`Saved revision ${workspace.revision}`, `CoreCLR generation ${workspace.generation.number} is live`);
  } catch (error) {
    document.querySelector('.sync-status strong').textContent = error.code === 'UAW1002' ? 'EXTERNAL CONFLICT' : 'PATCH REJECTED';
    showToast(error.code || 'Save failed', error.message);
    setAuthoringBusy(false);
  }
}

async function saveSemanticPatch(patch) {
  if (!state.authoringConnected) return showToast('Read-only artifacts', 'Start debug-serve to apply semantic edits');
  try {
    setAuthoringBusy(true);
    const workspace = await authoringRequest('/api/authoring/semantic-patch', patch);
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.surface, workspace.sourceText, workspace);
    showToast(`Behavior saved · R${workspace.revision}`, `CoreCLR generation ${workspace.generation.number} reloaded`);
  } catch (error) {
    document.querySelector('.sync-status strong').textContent = error.code === 'UAW1002' ? 'EXTERNAL CONFLICT' : 'PATCH REJECTED';
    showToast(error.code || 'Behavior patch failed', error.message);
    setAuthoringBusy(false);
  }
}

function addDeclaration(item) {
  state.declarations.push(item);
  renderDeclarationList();
  renderAiDeclarations();
  markGraphDirty();
  selectDeclaration(item.id);
  showToast('Declaration created', 'Text · Surface · AI will share the new identity');
}

function updateNewVariablePreview() {
  const item = {
    name: toPascalCase(document.getElementById('newVariableName').value),
    type: document.getElementById('newVariableType').value,
    defaultValue: document.getElementById('newVariableDefault').value,
    editable: document.getElementById('newVariableEditable').checked,
    save: document.getElementById('newVariableSave').checked,
    replicated: document.getElementById('newVariableReplicated').checked,
    category: '',
  };
  document.getElementById('newVariablePreview').textContent = canonicalDeclaration(item).replace('\n', ' ');
}

function changeView(view) {
  state.view = view;
  document.body.dataset.view = view;
  editorBody.className = `editor-body view-${view}`;
  document.querySelectorAll('.view-button').forEach(button =>
    button.classList.toggle('active', button.dataset.view === view));
}

function setZoom(next) {
  state.zoom = Math.max(.75, Math.min(1.25, next));
  flowCanvas.style.setProperty('--zoom', state.zoom);
  zoomLabel.textContent = `${Math.round(state.zoom * 100)}%`;
}

async function connectDebugBridge() {
  try {
    const response = await fetch('/api/debug/health', { cache: 'no-store' });
    if (!response.ok) throw new Error(`Debug health returned ${response.status}`);
    const health = await response.json();
    assertArtifact(health.schemaVersion === 'unifiedscript.debug-health/v0.1', 'Unsupported debug protocol');
    assertArtifact(health.sourceCompilationId === state.sourceCompilationId, 'Debug compilation mismatch');
    assertArtifact(health.semanticConfigurationId === state.semanticConfigurationId, 'Debug configuration mismatch');
    state.debugConnected = true;
    state.debugHealth = health;
    document.querySelector('.sync-status strong').textContent = `SYNCED · R${state.authoringRevision} · CLR`;
  } catch (error) {
    state.debugConnected = false;
    console.info('CoreCLR debug bridge is offline.', error.message);
  }
}

function restoreBreakpoints(ids) {
  if (!ids?.length) return;
  document.querySelectorAll('[data-debug-semantic-id]').forEach(element => {
    if (ids.includes(element.dataset.debugSemanticId)) {
      element.querySelector('.breakpoint-toggle:not(:disabled)')?.classList.add('active');
    }
  });
}

function enableDefaultBreakpoint() {
  const buttons = [...document.querySelectorAll('.breakpoint-toggle:not(:disabled)')];
  buttons.at(-1)?.classList.add('active');
}

function clearDebugVisuals(hideStrip = true) {
  document.querySelectorAll('.executing, .paused').forEach(element => element.classList.remove('executing', 'paused'));
  if (hideStrip) debugStrip.classList.remove('visible');
}

function setDebugBusy(busy) {
  state.debugBusy = busy;
  document.getElementById('runButton').disabled = busy || state.debugSession?.state === 'paused';
  document.getElementById('continueButton').disabled = busy || state.debugSession?.state !== 'paused';
  document.getElementById('stepOverButton').disabled = busy || state.debugSession?.state !== 'paused';
}

async function debugRequest(path, body = {}) {
  const response = await fetch(path, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(payload.error?.message || `Debug returned ${response.status}`);
  if (payload.schemaVersion !== 'unifiedscript.debug-session/v0.1') throw new Error('Unsupported debug response');
  return payload;
}

function renderLiveDebugPause(response) {
  clearDebugVisuals(false);
  state.debugSession = response;
  const pausedElement = [...document.querySelectorAll('[data-debug-semantic-id]')]
    .find(element => element.dataset.debugSemanticId === response.pause.nodeId);
  if (!pausedElement) throw new Error(`Paused statement ${response.pause.nodeId} has no Surface action`);
  pausedElement.classList.add('executing', 'paused');
  pausedElement.scrollIntoView({ behavior: 'smooth', block: 'center' });
  selectNode(pausedElement.dataset.nodeId, false);
  const frame = response.frames[0];
  document.getElementById('debugReason').textContent = `PAUSED · ${response.pause.reason.toUpperCase()}`;
  document.getElementById('debugLocation').textContent = `${frame.methodMetadataName} · IL ${frame.ilOffset} · generation ${response.generation.number}`;
  document.getElementById('debugLocals').innerHTML = response.locals.map(local =>
    `<span title="${escapeHtml(local.typeMetadataName)}">${escapeHtml(local.name)} <b>${escapeHtml(local.display)}</b></span>`).join('');
  debugStrip.classList.add('visible');
  setDebugBusy(false);
}

function renderDebugCompletion(response) {
  state.debugSession = null;
  clearDebugVisuals();
  document.querySelector('.target-label').textContent = `CLR GENERATION ${response.generation.number} · COMPLETED`;
  setDebugBusy(false);
  showToast(`Execution returned ${response.result?.returnValue?.json ?? '<void>'}`, 'Live CoreCLR session completed');
}

function renderDebugError(error) {
  state.debugSession = null;
  clearDebugVisuals(false);
  document.getElementById('debugReason').textContent = 'DEBUG ERROR';
  document.getElementById('debugLocation').textContent = error.message;
  document.getElementById('debugLocals').replaceChildren();
  debugStrip.classList.add('visible');
  setDebugBusy(false);
  showToast('CoreCLR debug failed', error.message);
}

function defaultArgument(parameter) {
  const type = parameter.typeMetadataName;
  if (type === 'System.Boolean') return true;
  if (type === 'System.String') return '';
  if (type.endsWith('[]')) return [];
  return type.includes('Int') ? 12 : 1;
}

async function runDebug() {
  clearDebugVisuals();
  changeView('structure');
  if (!state.debugConnected) await connectDebugBridge();
  if (!state.debugConnected) return renderDebugError(new Error('Start debug-serve to enable live CoreCLR debugging.'));
  const breakpointNodeIds = activeBreakpointNodeIds();
  if (!breakpointNodeIds.length) return renderDebugError(new Error('Enable at least one semantic action breakpoint.'));
  setDebugBusy(true);
  try {
    const func = state.functionSurface;
    const request = {
      methodId: func.id,
      arguments: func.parameters.map(defaultArgument),
      breakpointNodeIds,
    };
    if (!func.isStatic) {
      request.receiver = {
        assetId: state.selectedAssetId,
        stateOverrides: state.declarations.filter(item => item.original)
          .map(item => ({ stateId: item.semanticId, value: typedDefault(item) })),
      };
    }
    const response = await debugRequest('/api/debug/start', request);
    response.state === 'paused' ? renderLiveDebugPause(response) : renderDebugCompletion(response);
  } catch (error) {
    renderDebugError(error);
  }
}

async function resumeDebug(stepOver) {
  if (state.debugSession?.state !== 'paused') return;
  setDebugBusy(true);
  try {
    const response = await debugRequest(stepOver ? '/api/debug/step-over' : '/api/debug/continue');
    response.state === 'paused' ? renderLiveDebugPause(response) : renderDebugCompletion(response);
  } catch (error) {
    renderDebugError(error);
  }
}

function showToast(title, subtitle) {
  toast.querySelector('strong').textContent = title;
  toast.querySelector('small').textContent = subtitle;
  toast.classList.add('visible');
  setTimeout(() => toast.classList.remove('visible'), 2600);
}

async function copyText(value, confirmation = 'Copied') {
  try { await navigator.clipboard.writeText(value); } catch { /* local contexts may deny clipboard */ }
  showToast(confirmation, value.length > 50 ? `${value.slice(0, 47)}…` : value);
}

document.getElementById('assetPicker').addEventListener('change', event => selectAsset(event.target.value));
document.querySelectorAll('.view-button').forEach(button =>
  button.addEventListener('click', () => changeView(button.dataset.view)));
document.getElementById('zoomInButton').addEventListener('click', () => setZoom(state.zoom + .1));
document.getElementById('zoomOutButton').addEventListener('click', () => setZoom(state.zoom - .1));
document.getElementById('fitButton').addEventListener('click', () => setZoom(.9));
document.getElementById('runButton').addEventListener('click', runDebug);
document.getElementById('continueButton').addEventListener('click', () => resumeDebug(false));
document.getElementById('stepOverButton').addEventListener('click', () => resumeDebug(true));
document.getElementById('closeInspectorButton').addEventListener('click', () =>
  document.querySelector('.inspector-panel').classList.remove('compact-visible'));
document.getElementById('aiViewButton').addEventListener('click', () => {
  renderAiDeclarations();
  renderAiBehavior();
  aiDrawer.classList.add('visible');
  aiDrawer.setAttribute('aria-hidden', 'false');
});
document.getElementById('closeAiDrawer').addEventListener('click', () => {
  aiDrawer.classList.remove('visible');
  aiDrawer.setAttribute('aria-hidden', 'true');
});
document.getElementById('previewPatchButton').addEventListener('click', showDeclarationPatchPreview);
document.getElementById('applyPreviewPatchButton').addEventListener('click', () =>
  state.previewPatch && saveDeclarationChanges(state.previewPatch));
document.getElementById('saveButton').addEventListener('click', () => saveDeclarationChanges());
document.getElementById('addVariableButton').addEventListener('click', () => {
  updateNewVariablePreview();
  variableDialog.showModal();
  setTimeout(() => document.getElementById('newVariableName').select(), 30);
});
document.getElementById('variableForm').addEventListener('submit', event => {
  if (event.submitter?.value === 'cancel') return;
  event.preventDefault();
  const nameInput = document.getElementById('newVariableName');
  if (!nameInput.reportValidity()) return;
  const pendingId = `pending_${crypto.getRandomValues(new Uint32Array(2)).join('')}`;
  const item = {
    id: pendingId, semanticId: pendingId, name: toPascalCase(nameInput.value),
    type: document.getElementById('newVariableType').value,
    defaultValue: document.getElementById('newVariableDefault').value,
    editable: document.getElementById('newVariableEditable').checked,
    save: document.getElementById('newVariableSave').checked,
    replicated: document.getElementById('newVariableReplicated').checked,
    category: '', sourceStartLine: 1, sourceEndLine: 1,
  };
  variableDialog.close();
  addDeclaration(item);
});
['newVariableName', 'newVariableType', 'newVariableDefault', 'newVariableEditable', 'newVariableSave', 'newVariableReplicated']
  .forEach(id => document.getElementById(id).addEventListener('input', updateNewVariablePreview));
declarationSearch.addEventListener('input', () => {
  const query = declarationSearch.value.trim().toLowerCase();
  document.querySelectorAll('.declaration-item').forEach(item => {
    item.hidden = query && !item.textContent.toLowerCase().includes(query);
  });
});
document.addEventListener('keydown', event => {
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 's') {
    event.preventDefault();
    if (state.graphDirty && !state.authoringBusy) saveDeclarationChanges();
  }
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault();
    declarationSearch.focus();
  }
  if (event.key === 'Escape' && aiDrawer.classList.contains('visible')) {
    document.getElementById('closeAiDrawer').click();
  }
});
document.getElementById('copyCodeButton').addEventListener('click', () =>
  copyText(document.getElementById('codeEditor').innerText, 'Canonical text copied'));

loadWorkspaceArtifacts();

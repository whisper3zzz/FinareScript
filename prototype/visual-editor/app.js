const state = {
  view: 'structure', zoom: 1, artifactLoaded: false, graphDirty: false,
  sourceCompilationId: null, semanticConfigurationId: null,
  sourceText: '', sourcePath: 'workspace.cs', graphSchemaVersion: null,
  artifact: null, graph: null, surface: null, profile: null,
  selectedAssetId: null, selectedFunctionId: null,
  asset: null, surfaceAsset: null, graphFunction: null, functionSurface: null,
  declarations: [], nodes: new Map(), selected: null,
  authoringConnected: false, authoringBusy: false, authoringRevision: null,
  authoringHistory: null, authoringGroup: null,
  generation: null, removedDeclarations: [], aiProposal: null,
  debugConnected: false, debugBusy: false, debugCommandPending: false,
  debugHealth: null, debugSession: null,
  debugFrameId: null,
  apiBrowser: null, apiBrowserMembers: [], selectedApiMemberId: null,
  authoringInsight: null, selectedInsightNodeId: null,
};

const editorBody = document.getElementById('editorBody');
const flowCanvas = document.getElementById('flowCanvas');
const zoomLabel = document.getElementById('zoomLabel');
const inspectorTitle = document.getElementById('inspectorTitle');
const inspectorBody = document.getElementById('inspectorBody');
const declarationSearch = document.getElementById('declarationSearch');
const debugStrip = document.getElementById('debugStrip');
const aiDrawer = document.getElementById('aiDrawer');
const apiBrowserDrawer = document.getElementById('apiBrowserDrawer');
const insightDrawer = document.getElementById('insightDrawer');
const variableDialog = document.getElementById('variableDialog');
const semanticReviewDialog = document.getElementById('semanticReviewDialog');
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
  if (item.defaultValue === undefined || item.defaultValue === null) return 'null';
  return typeof item.defaultValue === 'object' ? JSON.stringify(item.defaultValue) : String(item.defaultValue);
}

function cloneJsonValue(value) {
  return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

function canonicalJson(value, ignoredKeys = new Set(), applyIgnoredKeys = true) {
  if (Array.isArray(value)) return value.map(item => canonicalJson(item, ignoredKeys, false));
  if (value && typeof value === 'object') {
    return Object.keys(value).filter(key => !applyIgnoredKeys || !ignoredKeys.has(key)).sort()
      .reduce((result, key) => {
        result[key] = canonicalJson(value[key], ignoredKeys, false);
        return result;
      }, {});
  }
  return value;
}

function sameJsonContract(left, right, ignoredKeys = new Set()) {
  return JSON.stringify(canonicalJson(left, ignoredKeys)) ===
    JSON.stringify(canonicalJson(right, ignoredKeys));
}

function surfaceDeclarationMatches(asset, declarationId, kind = null) {
  if (!asset) return false;
  return (kind === 'function' ? asset.functions : kind === 'state' ? asset.states : [
    ...(asset.states || []), ...(asset.functions || []),
  ]).some(declaration => declaration.id === declarationId);
}

function surfaceDeclarationOwner(surface, declarationId, kind = null) {
  const projections = (surface?.assets || []).filter(asset =>
    surfaceDeclarationMatches(asset, declarationId, kind));
  const explicit = (surface?.declarationOwners || []).find(owner =>
    owner.declarationId === declarationId);
  if (explicit) {
    const owner = surface.assets.find(asset => asset.id === explicit.ownerAssetId);
    assertArtifact(owner && surfaceDeclarationMatches(owner, declarationId, kind),
      `Surface owner for ${declarationId} does not contain the declaration`);
    return owner;
  }
  return projections.length === 1 ? projections[0] : null;
}

function validateAuthoringSurface(surface) {
  const isLegacy = surface?.schemaVersion === 'unifiedscript.authoring-surface/v0.4';
  const isCurrent = surface?.schemaVersion === 'unifiedscript.authoring-surface/v0.5';
  assertArtifact(isLegacy || isCurrent, 'Unsupported Authoring Surface artifact');
  assertArtifact(Array.isArray(surface.assets), 'Authoring Surface assets are missing');
  const assetsById = new Map();
  const projections = new Map();
  const declarationKinds = new Map();
  surface.assets.forEach(asset => {
    assertArtifact(asset?.id && !assetsById.has(asset.id), `Duplicate Surface asset ${asset?.id || '<missing>'}`);
    assetsById.set(asset.id, asset);
    const localIds = new Set();
    [...(asset.states || []).map(item => ({ ...item, kind: 'state' })),
      ...(asset.functions || []).map(item => ({ ...item, kind: 'function' }))].forEach(item => {
      assertArtifact(item.id && !localIds.has(item.id),
        `Asset ${asset.id} contains a duplicate declaration ${item.id || '<missing>'}`);
      localIds.add(item.id);
      const previousKind = declarationKinds.get(item.id);
      assertArtifact(!previousKind || previousKind === item.kind,
        `Declaration ${item.id} is both a state and a function`);
      declarationKinds.set(item.id, item.kind);
      if (!projections.has(item.id)) projections.set(item.id, []);
      projections.get(item.id).push({ asset, item });
    });
  });

  const ownerEntries = new Map();
  (surface.declarationOwners || []).forEach(owner => {
    assertArtifact(owner?.declarationId && owner?.ownerAssetId && !ownerEntries.has(owner.declarationId),
      `Duplicate Surface declaration owner ${owner?.declarationId || '<missing>'}`);
    const ownerAsset = assetsById.get(owner.ownerAssetId);
    assertArtifact(ownerAsset && surfaceDeclarationMatches(
      ownerAsset, owner.declarationId, declarationKinds.get(owner.declarationId)),
    `Surface owner ${owner.ownerAssetId} does not contain ${owner.declarationId}`);
    ownerEntries.set(owner.declarationId, owner.ownerAssetId);
  });

  projections.forEach((entries, declarationId) => {
    const kind = declarationKinds.get(declarationId);
    const ignored = new Set(kind === 'function' ? ['qualifiedName'] : ['defaultValue']);
    for (const entry of entries.slice(1)) {
      assertArtifact(sameJsonContract(entries[0].item, entry.item, ignored),
        `Shared ${kind} ${declarationId} has an inconsistent contract`);
    }
    if (entries.length > 1 && kind === 'function') {
      assertArtifact(!isLegacy, `Legacy Surface repeats function ${declarationId}`);
      assertArtifact(ownerEntries.has(declarationId),
        `Shared function ${declarationId} requires an explicit source owner`);
    }
    if (entries.length > 1 && isCurrent) {
      assertArtifact(ownerEntries.has(declarationId),
        `Shared state ${declarationId} requires an explicit source owner`);
    }
  });
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
    assertArtifact(workspace.schemaVersion === 'unifiedscript.authoring-workspace/v0.5', 'Unsupported authoring workspace protocol');
    validateVisualProfile(workspace.profile);
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.surface, workspace.sourceText, workspace);
    await Promise.all([connectDebugBridge(), loadApiBrowser()]);
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
    await loadApiBrowser();
  } catch (error) {
    document.querySelector('.sync-status strong').textContent = 'SURFACE ERROR';
    flowCanvas.innerHTML = `<div class="surface-error"><strong>AUTHORING SURFACE UNAVAILABLE</strong><span>${escapeHtml(error.message)}</span></div>`;
    console.error(error);
  }
}

function validateWorkspaceArtifacts(artifact, graph, surface) {
  assertArtifact(artifact?.schemaVersion === 'unifiedscript.script-asset/v0.1', 'Unsupported Declaration IR artifact');
  assertArtifact(graph?.schemaVersion === 'unifiedscript.graph/v0.13', 'Unsupported Graph artifact');
  validateAuthoringSurface(surface);
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

function validateApiBrowser(browser) {
  assertArtifact(browser?.schemaVersion === 'unifiedscript.unreal-api-browser/v0.1', 'Unsupported Unreal API browser protocol');
  assertArtifact(browser.sources?.contractSchemaVersion === 'unifiedscript.unreal-api-contract/v0.2', 'API browser is not derived from the current Contract schema');
  assertArtifact(typeof browser.sources?.inputSha256 === 'string' && browser.sources.inputSha256.length === 64, 'API browser input identity is missing');
  assertArtifact(Array.isArray(browser.types) && Array.isArray(browser.diagnostics), 'Malformed Unreal API browser document');
  const members = browser.types.flatMap(type => (type.members || []).map(member => ({ ...member, owner: type })));
  assertArtifact(browser.summary?.typeCount === browser.types.length && browser.summary?.nodeCount === members.length, 'API browser summary does not match its generated index');
  return members;
}

async function loadApiBrowser() {
  const button = document.getElementById('apiBrowserButton');
  try {
    const response = await fetch('/api/unreal/browser', { cache: 'no-store' });
    if (!response.ok) throw new Error(`API browser returned ${response.status}`);
    const browser = await response.json();
    const members = validateApiBrowser(browser);
    state.apiBrowser = browser;
    state.apiBrowserMembers = members;
    state.selectedApiMemberId = members[0]?.id ?? null;
    button.disabled = false;
    button.title = `${browser.contractName} · ${members.length} generated nodes`;
    renderApiBrowser();
  } catch (error) {
    button.disabled = true;
    button.title = error.message;
    console.info('Generated Unreal API browser is unavailable.', error.message);
  }
}

function apiRoute(member) {
  return member.disposition || 'visualOnly';
}

function apiRouteLabel(route) {
  return ({
    directThunk: 'DIRECT THUNK', callFrame: 'CALL FRAME', dataPlaneOperation: 'DATA PLANE',
    reflectionFallback: 'REFLECTION', rejected: 'REJECTED', visualOnly: 'VISUAL ONLY',
  })[route] || String(route || 'UNKNOWN').toUpperCase();
}

function apiMemberSearchText(member) {
  return [member.title, member.kind, member.memberPath, member.memberId, member.category,
    member.tooltip, member.owner.name, member.owner.managedTypeName, apiRoute(member),
    ...(member.diagnosticCodes || []), ...(member.pins || []).flatMap(pin =>
      [pin.name, pin.direction, pin.type?.nativeType, pin.type?.typePath])]
    .filter(Boolean).join(' ').toLowerCase();
}

function filteredApiMembers() {
  const query = document.getElementById('apiBrowserSearch').value.trim().toLowerCase();
  const route = document.getElementById('apiBrowserRoute').value;
  return state.apiBrowserMembers.filter(member =>
    (!route || apiRoute(member) === route) && (!query || apiMemberSearchText(member).includes(query)));
}

function renderApiBrowser() {
  const browser = state.apiBrowser;
  if (!browser) return;
  document.getElementById('apiBrowserContract').textContent = browser.contractName;
  document.getElementById('apiBrowserFingerprint').textContent =
    `UE ${browser.engine.major}.${browser.engine.minor}.${browser.engine.patch} · ${browser.sources.inputSha256.slice(0, 16)}…`;
  const metricValues = [browser.summary.typeCount, browser.summary.memberCount,
    browser.summary.nodeCount, browser.summary.diagnosticCount];
  document.querySelectorAll('#apiBrowserMetrics strong').forEach((element, index) => {
    element.textContent = String(metricValues[index]);
  });

  const members = filteredApiMembers();
  const groups = new Map();
  members.forEach(member => {
    if (!groups.has(member.owner.path)) groups.set(member.owner.path, { type: member.owner, members: [] });
    groups.get(member.owner.path).members.push(member);
  });
  const results = document.getElementById('apiBrowserResults');
  results.innerHTML = [...groups.values()].map(group => `
    <section class="api-result-group"><header><strong>${escapeHtml(group.type.managedTypeName)}</strong><small>${escapeHtml(group.type.path)}</small><em>${group.members.length}</em></header>
      ${group.members.map(member => `<button class="api-result-item${member.id === state.selectedApiMemberId ? ' active' : ''}" type="button" data-api-member="${escapeHtml(member.id)}">
        <span class="api-kind">${escapeHtml(member.kind.slice(0, 2).toUpperCase())}</span><span><strong>${escapeHtml(member.title)}</strong><small>${escapeHtml(member.category)}</small></span>
        <em class="api-route route-${apiRoute(member)}">${apiRouteLabel(apiRoute(member))}</em></button>`).join('')}
    </section>`).join('') || '<div class="api-browser-empty">NO CONTRACT MEMBER MATCHES THIS FILTER</div>';
  results.querySelectorAll('[data-api-member]').forEach(button =>
    button.addEventListener('click', () => selectApiMember(button.dataset.apiMember)));
  if (!members.some(member => member.id === state.selectedApiMemberId)) {
    state.selectedApiMemberId = members[0]?.id ?? null;
  }
  renderApiMemberDetail();
  renderApiDiagnostics();
}

function selectApiMember(id) {
  state.selectedApiMemberId = id;
  document.querySelectorAll('[data-api-member]').forEach(button =>
    button.classList.toggle('active', button.dataset.apiMember === id));
  renderApiMemberDetail();
}

function renderApiMemberDetail() {
  const member = state.apiBrowserMembers.find(item => item.id === state.selectedApiMemberId);
  const detail = document.getElementById('apiBrowserDetail');
  if (!member) {
    detail.innerHTML = '<div class="surface-loading">SELECT AN API MEMBER</div>';
    return;
  }
  const constraints = member.constraints;
  const pins = (member.pins || []).map(pin => `<div class="api-pin"><span class="pin-direction ${pin.direction}">${escapeHtml(pin.direction)}</span><strong>${escapeHtml(pin.name)}</strong><code>${escapeHtml(pin.type?.nativeType || pin.type?.kind || 'flow')}</code>${pin.defaultValue !== undefined ? `<em>= ${escapeHtml(pin.defaultValue)}</em>` : ''}</div>`).join('');
  detail.innerHTML = `
    <div class="api-detail-heading"><span>${escapeHtml(member.kind)}</span><h3>${escapeHtml(member.title)}</h3><p>${escapeHtml(member.tooltip)}</p></div>
    <div class="api-detail-route route-${apiRoute(member)}"><strong>${apiRouteLabel(apiRoute(member))}</strong><span>${member.available ? 'AVAILABLE TO GENERATED CODE + VISUAL NODES' : 'REJECTED BY CONTRACT COVERAGE'}</span></div>
    <dl class="api-facts"><dt>Managed owner</dt><dd>${escapeHtml(member.owner.managedTypeName)}</dd><dt>Member ID</dt><dd><code>${escapeHtml(member.memberId)}</code></dd><dt>Contract path</dt><dd><code>${escapeHtml(member.memberPath)}</code></dd><dt>Thread</dt><dd>${escapeHtml(constraints.threadRequirement)}</dd><dt>World</dt><dd>${escapeHtml(constraints.worldRequirement)}${constraints.worldContextParameter ? ` · ${escapeHtml(constraints.worldContextParameter)}` : ''}</dd><dt>Behavior</dt><dd>${member.pure ? 'pure' : 'impure'}${constraints.latent ? ' · latent' : ''}${constraints.deprecated ? ' · deprecated' : ''}</dd></dl>
    <section class="api-pin-section"><div class="api-browser-section-title"><span>TYPED PINS</span><em>${member.pins.length}</em></div>${pins || '<div class="api-browser-empty">NO DATA PINS</div>'}</section>
    <section class="api-member-diagnostics"><div class="api-browser-section-title"><span>DIAGNOSTICS</span><em>${member.diagnosticCodes.length}</em></div>${member.diagnosticCodes.map(code => `<button type="button" data-diagnostic-code="${escapeHtml(code)}">${escapeHtml(code)}</button>`).join('') || '<div class="api-browser-empty">NO ROUTE DIAGNOSTICS</div>'}</section>`;
  detail.querySelectorAll('[data-diagnostic-code]').forEach(button =>
    button.addEventListener('click', () => {
      document.getElementById('apiBrowserSearch').value = button.dataset.diagnosticCode;
      renderApiBrowser();
    }));
}

function renderApiDiagnostics() {
  const query = document.getElementById('apiBrowserSearch').value.trim().toLowerCase();
  const diagnostics = state.apiBrowser.diagnostics.filter(item => !query ||
    [item.code, item.message, ...(item.memberPaths || [])].join(' ').toLowerCase().includes(query));
  document.getElementById('apiDiagnosticCount').textContent = String(diagnostics.length);
  document.getElementById('apiDiagnosticList').innerHTML = diagnostics.map(item => `
    <article class="api-diagnostic severity-${item.severity}"><header><strong>${escapeHtml(item.code)}</strong><em>${escapeHtml(item.severity)}</em></header><p>${escapeHtml(item.message)}</p><small>${item.memberPaths.length} CONTRACT PATH${item.memberPaths.length === 1 ? '' : 'S'}</small></article>`).join('') || '<div class="api-browser-empty">NO DIAGNOSTIC MATCHES THIS FILTER</div>';
}

function validateGameplayProfileTrace(trace) {
  assertArtifact(trace?.schemaVersion === 'unifiedscript.gameplay-profile/v0.3', 'Unsupported UE gameplay profile');
  assertArtifact(Array.isArray(trace.frames) && trace.aggregate?.frameCount === trace.frames.length,
    'Gameplay profile frame aggregate is malformed');
  assertArtifact(Array.isArray(trace.aggregate?.nodes) && Array.isArray(trace.aggregate?.boundaries),
    'Gameplay profile has no semantic-node or boundary aggregate');
  assertArtifact(trace.aggregate.frameCount > 0, 'Gameplay profile contains no captured frames');
  return trace;
}

function validateAuthoringInsight(snapshot) {
  assertArtifact(snapshot?.schemaVersion === 'unifiedscript.authoring-insight/v0.1', 'Unsupported authoring insight snapshot');
  assertArtifact(snapshot.sourceCompilationId === state.sourceCompilationId,
    'Profile was resolved against a different source compilation');
  assertArtifact(snapshot.semanticConfigurationId === state.semanticConfigurationId,
    'Profile was resolved against a different semantic configuration');
  assertArtifact(Array.isArray(snapshot.nodes) && Array.isArray(snapshot.boundaries) && Array.isArray(snapshot.apiRoutes),
    'Malformed authoring insight snapshot');
  return snapshot;
}

function currentInsightFocus() {
  const pause = state.debugSession?.state === 'paused' ? state.debugSession.pause : null;
  if (pause?.methodId && pause?.nodeId) return { methodId: pause.methodId, nodeId: pause.nodeId };
  if (state.selected?.kind !== 'action') return { methodId: null, nodeId: null };
  const action = state.nodes.get(state.selected.id);
  return action?.debugTargetId
    ? { methodId: state.functionSurface.id, nodeId: action.debugTargetId }
    : { methodId: null, nodeId: null };
}

async function importGameplayProfile(file) {
  if (!file) return;
  try {
    const trace = validateGameplayProfileTrace(JSON.parse(await file.text()));
    const focus = currentInsightFocus();
    const response = await fetch('/api/unreal/authoring-insight', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        schemaVersion: 'unifiedscript.authoring-insight-request/v0.1',
        trace,
        focusedMethodId: focus.methodId,
        focusedNodeId: focus.nodeId,
      }),
    });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(payload.error?.message || `Profile insight returned ${response.status}`);
    state.authoringInsight = validateAuthoringInsight(payload);
    state.selectedInsightNodeId = payload.nodes.find(node => node.focused)?.nodeId || payload.nodes[0]?.nodeId || null;
    renderAuthoringInsight();
    showToast(`Profile ${payload.traceId} loaded`, `${payload.summary.resolvedNodeCount}/${payload.nodes.length} semantic hotspots located`);
  } catch (error) {
    showToast('Profile import rejected', error.message);
  } finally {
    document.getElementById('insightFile').value = '';
  }
}

function formatInsightTime(nanoseconds) {
  if (nanoseconds >= 1_000_000) return `${(nanoseconds / 1_000_000).toFixed(3)} ms`;
  if (nanoseconds >= 1_000) return `${(nanoseconds / 1_000).toFixed(1)} µs`;
  return `${nanoseconds} ns`;
}

function renderAuthoringInsight() {
  const snapshot = state.authoringInsight;
  if (!snapshot) return;
  const summary = snapshot.summary;
  const values = [formatInsightTime(summary.averageFrameNanoseconds),
    `${(summary.allocatedBytes / 1024).toFixed(1)} KiB`, String(summary.bufferOverflowCount),
    `${summary.resolvedNodeCount}/${summary.resolvedNodeCount + summary.unresolvedNodeCount}`];
  document.querySelectorAll('#insightMetrics strong').forEach((element, index) => {
    element.textContent = values[index];
  });
  document.getElementById('insightTrace').textContent =
    `${snapshot.traceId} · ${summary.frameCount} FRAME${summary.frameCount === 1 ? '' : 'S'} · WORLD ${snapshot.worldHostId}`;
  document.getElementById('insightIdentity').textContent =
    `${semanticShort(snapshot.sourceCompilationId)} · ${snapshot.apiRoutes.reduce((total, route) => total + route.catalogMemberCount, 0)} generated API route entries`;
  document.getElementById('insightBoundaries').innerHTML = snapshot.boundaries.map(boundary => `
    <article class="insight-boundary"><header><strong>${escapeHtml(boundary.name)}</strong><span>${boundary.percentOfOwner.toFixed(1)}%</span></header>
      <p>${escapeHtml(boundary.contractRoute)} · ${boundary.count} calls · ${formatInsightTime(boundary.totalNanoseconds)}${boundary.catalogMemberCount ? ` · ${boundary.catalogMemberCount} APIs use route` : ''}</p>
      <div class="insight-bar"><i style="width:${Math.min(100, boundary.percentOfOwner)}%"></i></div></article>`).join('') || '<div class="api-browser-empty">NO ATTRIBUTED BOUNDARY COST</div>';
  document.getElementById('insightNodes').innerHTML = snapshot.nodes.map(node => `
    <button class="insight-node${node.nodeId === state.selectedInsightNodeId ? ' active' : ''}" type="button" data-insight-node="${escapeHtml(node.nodeId)}">
      <em>#${node.rank}</em><span><strong>${escapeHtml(node.title)}</strong><small>${node.sourceSpan ? `${escapeHtml(node.sourceSpan.filePath)}:${node.sourceSpan.start.line}` : 'UNRESOLVED IN CURRENT SURFACE'} · ${node.hitCount} hits</small></span><span>${formatInsightTime(node.totalNanoseconds)}</span>
    </button>`).join('') || '<div class="api-browser-empty">PROFILE CONTAINS NO SEMANTIC NODES</div>';
  document.querySelectorAll('[data-insight-node]').forEach(button =>
    button.addEventListener('click', () => selectInsightNode(button.dataset.insightNode)));
  renderInsightDetail();
}

function selectInsightNode(nodeId) {
  state.selectedInsightNodeId = nodeId;
  document.querySelectorAll('[data-insight-node]').forEach(button =>
    button.classList.toggle('active', button.dataset.insightNode === nodeId));
  renderInsightDetail();
}

function focusInsightNode() {
  const node = state.authoringInsight?.nodes.find(item => item.nodeId === state.selectedInsightNodeId);
  if (!node?.actionId) return;
  if (state.graphDirty) {
    showToast('Save or discard the pending patch first', 'Insight navigation would discard local edits');
    return;
  }
  const asset = surfaceDeclarationOwner(state.surface, node.methodId, 'function');
  const func = asset?.functions.find(candidate => candidate.id === node.methodId);
  if (!asset || !func) return;
  insightDrawer.classList.remove('visible');
  insightDrawer.setAttribute('aria-hidden', 'true');
  selectAsset(asset.id, false, func.id, { kind: 'action', id: node.actionId });
  selectFunction(func.id, false, { kind: 'action', id: node.actionId });
  selectNode(node.actionId, false);
  changeView('split');
  document.querySelectorAll('.profile-hot').forEach(element => element.classList.remove('profile-hot'));
  const element = [...document.querySelectorAll('[data-debug-semantic-id]')]
    .find(candidate => candidate.dataset.debugSemanticId === node.nodeId);
  element?.classList.add('profile-hot');
  element?.scrollIntoView({ behavior: 'smooth', block: 'center' });
}

function openInsightApiRoute() {
  const node = state.authoringInsight?.nodes.find(item => item.nodeId === state.selectedInsightNodeId);
  const boundary = node?.boundaries.find(item => item.catalogMemberCount > 0);
  if (!boundary || !state.apiBrowser) return;
  const route = boundary.contractRoute.charAt(0).toLowerCase() + boundary.contractRoute.slice(1);
  document.getElementById('apiBrowserRoute').value = route;
  document.getElementById('apiBrowserSearch').value = '';
  insightDrawer.classList.remove('visible');
  insightDrawer.setAttribute('aria-hidden', 'true');
  renderApiBrowser();
  apiBrowserDrawer.classList.add('visible');
  apiBrowserDrawer.setAttribute('aria-hidden', 'false');
}

function renderInsightDetail() {
  const node = state.authoringInsight?.nodes.find(item => item.nodeId === state.selectedInsightNodeId);
  const detail = document.getElementById('insightDetail');
  if (!node) {
    detail.innerHTML = '<div class="surface-loading">SELECT A HOTSPOT</div>';
    return;
  }
  const source = node.sourceSpan
    ? `${node.sourceSpan.filePath}:${node.sourceSpan.start.line}:${node.sourceSpan.start.column}`
    : 'Not present in the current authoring Surface';
  detail.innerHTML = `<div class="eyebrow">#${node.rank} · ${escapeHtml(node.category)} · ${node.percentOfTrace.toFixed(1)}% OF TRACE</div>
    <h3>${escapeHtml(node.title)}</h3><p>${escapeHtml(node.summary)}</p>
    <dl class="insight-detail-meta"><dt>Source</dt><dd>${escapeHtml(source)}</dd><dt>Semantic node</dt><dd>${escapeHtml(node.nodeId)}</dd><dt>Runtime</dt><dd>${formatInsightTime(node.totalNanoseconds)} · ${formatInsightTime(node.averageHitNanoseconds)}/hit</dd><dt>Debug state</dt><dd>${node.focused ? 'CURRENT DEBUG FOCUS' : 'captured profile node'}</dd></dl>
    <div class="insight-section-title"><span>NODE COST BREAKDOWN</span><em>boundary / route / cost</em></div>
    <div>${node.boundaries.map(boundary => `<div class="insight-node-boundary"><strong>${escapeHtml(boundary.name)}</strong><span>${escapeHtml(boundary.contractRoute)}</span><em>${boundary.percentOfOwner.toFixed(1)}% · ${formatInsightTime(boundary.totalNanoseconds)}</em></div>`).join('') || '<div class="api-browser-empty">NO NODE BOUNDARY COST</div>'}</div>
    <div class="insight-detail-actions"><button id="focusInsightNode" class="button primary" type="button"${node.actionId ? '' : ' disabled'}>FOCUS GRAPH + SOURCE</button><button id="openInsightApiRoute" class="button secondary" type="button"${node.boundaries.some(boundary => boundary.catalogMemberCount > 0) && state.apiBrowser ? '' : ' disabled'}>OPEN API ROUTE</button></div>`;
  document.getElementById('focusInsightNode').addEventListener('click', focusInsightNode);
  document.getElementById('openInsightApiRoute').addEventListener('click', openInsightApiRoute);
}

function validateVisualProfile(report) {
  assertArtifact(report?.schemaVersion === 'unifiedscript.visual-csharp-profile-report/v0.1', 'Unsupported Visual C# profile report');
  assertArtifact(report.profile?.schemaVersion === 'unifiedscript.visual-csharp-profile/v0.3', 'Unsupported Visual C# profile contract');
  const boundary = report.profile.opaqueCodeBoundary;
  assertArtifact(boundary?.markerAttribute === '[Visual]' &&
    boundary.unmarkedMethodsAreSourceOnly === true &&
    boundary.callsProjectAsSingleNodes === true &&
    boundary.bodiesAreGraphEditable === false &&
    boundary.requiresGraphSafeSignature === true &&
    boundary.allowsRefParameters === false, 'Unsupported opaque-code boundary contract');
  assertArtifact(Array.isArray(report.methods), 'Malformed Visual C# method profile');
  report.methods.forEach(method => {
    const validVisual = method.representation === 'visualGraph' &&
      method.authoringBoundary === 'visualGraph' && typeof method.graphEditable === 'boolean';
    const validOpaque = method.representation === 'opaqueCode' &&
      method.authoringBoundary === 'sourceOnly' && method.graphEditable === false;
    assertArtifact(validVisual || validOpaque, `Invalid authoring boundary for ${method.qualifiedName || method.name}`);
  });
}

function applyWorkspaceArtifacts(artifact, graph, surface, sourceText, workspace = null) {
  validateWorkspaceArtifacts(artifact, graph, surface);
  const remembered = rememberSelection();
  if (state.authoringInsight &&
      (state.authoringInsight.sourceCompilationId !== artifact.sourceCompilationId ||
       state.authoringInsight.semanticConfigurationId !== artifact.semanticConfigurationId)) {
    state.authoringInsight = null;
    state.selectedInsightNodeId = null;
  }
  Object.assign(state, {
    sourceCompilationId: artifact.sourceCompilationId,
    semanticConfigurationId: artifact.semanticConfigurationId,
    sourceText,
    sourcePath: workspace?.sourcePath || artifact.assets[0]?.span?.filePath || 'workspace.cs',
    graphSchemaVersion: graph.schemaVersion,
    artifact, graph, surface, profile: workspace?.profile ?? null,
    authoringConnected: Boolean(workspace),
    authoringRevision: workspace?.revision ?? null,
    authoringHistory: workspace?.history ?? null,
    authoringGroup: workspace?.group ?? null,
    generation: workspace?.generation ?? null,
    artifactLoaded: true, graphDirty: false, removedDeclarations: [], aiProposal: null,
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
  updateHistoryControls();
  const historyState = workspace?.history?.persistenceState?.toUpperCase();
  document.querySelector('.sync-status strong').textContent = workspace
    ? workspace.group
      ? `DRAFT · ${workspace.group.stagedOperationCount} STAGED · R${workspace.revision}`
      : `SYNCED · R${workspace.revision}${historyState ? ` · HISTORY ${historyState}` : ''}`
    : 'SURFACE · READ ONLY';
  document.querySelector('.target-label').textContent = workspace
    ? `CLR GENERATION ${workspace.generation.number} · ${workspace.profile.profile.displayName.toUpperCase()}`
    : 'AUTHORING SURFACE v0.1 · GRAPH v0.12';
  document.getElementById('opaqueMethodCount').textContent = String(
    workspace?.profile?.methods?.filter(method => method.representation === 'opaqueCode').length ?? 0);
  document.getElementById('insightButton').disabled = !workspace;
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
    typeMetadataName: item.typeMetadataName, rawDefaultValue: cloneJsonValue(item.defaultValue),
    defaultValue: artifactDefaultText(item), editable: item.exposure.instanceEditable,
    save: item.exposure.saveGame, replicated: item.exposure.replicated,
    category: item.exposure.category || '', semanticId: item.id,
    sourceFilePath: item.span?.filePath || state.sourcePath,
    sourceStartLine: item.span.start.line, sourceEndLine: item.span.end.line,
    sourceOwnerAssetId: surfaceDeclarationOwner(state.surface, item.id, 'state')?.id || null,
    original: {
      name: item.name, typeMetadataName: item.typeMetadataName, typeKind: item.typeKind,
      defaultValue: cloneJsonValue(item.defaultValue),
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
    if (item.sourceFilePath && item.sourceFilePath !== state.sourcePath) return;
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
    line.classList.toggle('state-highlight', line.dataset.stateId === item.id &&
      (!item.sourceFilePath || item.sourceFilePath === state.sourcePath)));
  document.querySelectorAll('#codeEditor [data-line]').forEach(line => line.classList.remove('highlighted'));
  document.getElementById('selectedLine').textContent = item.sourceFilePath && item.sourceFilePath !== state.sourcePath
    ? `${item.sourceFilePath}:${item.sourceStartLine || 1}` : String(item.sourceStartLine || 1);
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

function toggleRow(id, title, subtitle, checked, disabled = false) {
  return `<label class="toggle-row"><span><strong>${title}</strong><small>${subtitle}</small></span><input id="${id}" type="checkbox"${checked ? ' checked' : ''}${disabled ? ' disabled' : ''}><i></i></label>`;
}

function supportedStateType(item) {
  return ['bool', 'int', 'float', 'string'].includes(item.type);
}

function renderStateInspector(item) {
  inspectorTitle.textContent = item.name;
  const owner = surfaceDeclarationOwner(state.surface, item.semanticId, 'state');
  const sourceOwner = owner || (!item.original ? state.surfaceAsset : null);
  const isOwner = item.original ? owner?.id === state.selectedAssetId : true;
  const canEdit = isOwner && supportedStateType(item);
  const typeEditor = supportedStateType(item)
    ? `<select id="propertyType"${canEdit ? '' : ' disabled'}>${['int', 'float', 'bool', 'string'].map(type => `<option${type === item.type ? ' selected' : ''}>${type}</option>`).join('')}</select>`
    : `<input id="propertyType" type="text" value="${escapeHtml(item.type)}" disabled>`;
  const sourceLocation = item.sourceFilePath && item.sourceFilePath !== state.sourcePath
    ? `${item.sourceFilePath}:${item.sourceStartLine}` : `line ${item.sourceStartLine}`;
  const ownerSection = sourceOwner ? `
    <section class="inspector-section source-owner"><h3>SOURCE OWNER</h3>
      <div class="fact-grid"><span>${escapeHtml(sourceOwner.assetPath)}</span><strong>${isOwner ? 'SELECTED' : 'EFFECTIVE PROJECTION'}</strong></div>
      <p class="node-inspector-copy">This row shows the selected asset's effective default. Edits apply to the source owner.</p>
      <button id="sourceOwnerButton" class="button secondary" type="button"${isOwner ? ' disabled' : ''}>NAVIGATE TO SOURCE OWNER</button>
    </section>` : `
    <section class="inspector-section source-owner"><h3>SOURCE OWNER</h3>
      <p class="node-inspector-copy">This legacy projection has no unambiguous source owner and is read-only.</p>
    </section>`;
  const supportNotice = supportedStateType(item) ? '' :
    '<p class="node-inspector-copy">This state type is preserved as typed JSON and is read-only in the prototype editor.</p>';
  inspectorBody.innerHTML = `
    <div class="selection-kind"><span class="selection-icon">S</span><div><strong>SCRIPT STATE</strong><small>Declaration IR · persistent identity</small></div></div>
    ${ownerSection}
    <section class="inspector-section"><h3>DECLARATION</h3>
      <label class="field-label">Name<input id="propertyName" type="text" value="${escapeHtml(item.name)}"${canEdit ? '' : ' disabled'}></label>
      <div class="field-grid"><label class="field-label">Type${typeEditor}</label>
      <label class="field-label">Default<input id="propertyDefault" type="text" value="${escapeHtml(item.defaultValue)}"${canEdit ? '' : ' disabled'}></label></div>
      <label class="field-label">Category<input id="propertyCategory" type="text" value="${escapeHtml(item.category)}" placeholder="Optional"${canEdit ? '' : ' disabled'}></label>
      ${supportNotice}
      <div class="fact-grid"><span>Source</span><strong>${escapeHtml(sourceLocation)}</strong></div>
    </section>
    <section class="inspector-section"><h3>ENGINE EXPOSURE</h3>
      ${toggleRow('toggleEditable', 'Instance Editable', 'Future UE Details panel', item.editable, !canEdit)}
      ${toggleRow('toggleSave', 'Save Game', 'Persistent state', item.save, !canEdit)}
      ${toggleRow('toggleReplicated', 'Replicated', 'Network contract', item.replicated, !canEdit)}
    </section>
    <section class="inspector-section source-contract"><div class="section-title-row"><h3>CANONICAL TEXT</h3><span>AI READABLE</span></div><pre id="canonicalDeclaration">${escapeHtml(canonicalDeclaration(item))}</pre></section>
    <section class="inspector-section identity-section"><h3>SEMANTIC IDENTITY</h3><button id="semanticId" class="identity-value" type="button">${semanticShort(item.semanticId)}</button><div class="identity-facts"><span>TEXT <b>✓</b></span><span>SURFACE <b>✓</b></span><span>AI <b>✓</b></span></div></section>
    <button id="removeStateButton" class="inspector-danger" type="button"${canEdit ? '' : ' disabled'}>REMOVE STATE</button>
    <div class="prototype-warning"><strong>DECLARATION IR + PATCH v0.2</strong><p>变量只声明一次；文本、可视化、AI 与未来 UE 反射层共享同一个语义身份。</p></div>`;
  bindStateInspector(item, canEdit, owner);
}

function bindStateInspector(item, canEdit, owner) {
  const controls = {
    name: document.getElementById('propertyName'), type: document.getElementById('propertyType'),
    defaultValue: document.getElementById('propertyDefault'), category: document.getElementById('propertyCategory'),
    editable: document.getElementById('toggleEditable'), save: document.getElementById('toggleSave'),
    replicated: document.getElementById('toggleReplicated'),
  };
  const update = () => {
    if (!canEdit) return;
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
  document.getElementById('sourceOwnerButton')?.addEventListener('click', () => {
    if (!owner || owner.id === state.selectedAssetId) return;
    if (state.graphDirty) {
      showToast('Save or discard the pending patch first', 'Source-owner navigation would discard local edits');
      return;
    }
    selectAsset(owner.id, true, state.selectedFunctionId, { kind: 'state', id: item.semanticId });
  });
}

function removeDeclaration(item) {
  const index = state.declarations.indexOf(item);
  if (index < 0) return;
  if (item.original && !supportedStateType(item)) {
    showToast('Unsupported state types are read-only', 'This declaration cannot be removed from the prototype editor');
    return;
  }
  const owner = surfaceDeclarationOwner(state.surface, item.semanticId, 'state');
  if (item.original && (!owner || owner.id !== state.selectedAssetId)) {
    showToast('Select the source owner to remove this state', 'Effective inherited projections are read-only');
    return;
  }
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
    line.classList.toggle('highlighted', (!node.span.filePath || node.span.filePath === state.sourcePath) &&
      Number(line.dataset.line) === node.span.start.line));
  const nodeSourcePath = node.span.filePath || state.sourcePath;
  document.getElementById('selectedLine').textContent = nodeSourcePath !== state.sourcePath
    ? `${nodeSourcePath}:${node.span.start.line}` : String(node.span.start.line);
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
  const nodeSourcePath = node.span?.filePath || state.sourcePath;
  const isOpaqueCall = node.properties?.authoringBoundary === 'sourceOnly';
  const opaqueBoundarySection = isOpaqueCall ? `
    <section class="inspector-section source-contract"><div class="section-title-row"><h3>OPAQUE CODE BOUNDARY</h3><span>SOURCE ONLY</span></div>
      <p class="node-inspector-copy">This call is one graph node. Its unmarked C# method body remains source-only and cannot be opened or edited as graph actions.</p>
      <div class="fact-grid"><span>Callee representation</span><strong>${escapeHtml(node.properties.calleeBodyRepresentation)}</strong></div>
      <div class="fact-grid"><span>Body editable</span><strong>NO</strong></div>
    </section>` : '';
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
      <div class="fact-grid"><span>${escapeHtml(nodeSourcePath)}</span><strong>Line ${node.span.start.line}</strong></div>
      <div class="fact-grid"><span>Owned by region</span><strong>${semanticShort(node.regionId)}</strong></div>
      <div class="fact-grid"><span>Script state</span><strong>${escapeHtml(references.join(', ') || 'none')}</strong></div>
    </section>${opaqueBoundarySection}${editSection}
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
    schemaVersion: 'unifiedscript.patch/v0.28',
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
  if (!supportedStateType(item)) return cloneJsonValue(item.rawDefaultValue);
  const originalDefaultText = item.original
    ? artifactDefaultText({ typeKind: item.typeKind, defaultValue: item.original.defaultValue }) : null;
  const sameTypeAsOriginal = item.original &&
    metadataTypeFor(item.type) === item.original.typeMetadataName &&
    typeKindFor(item.type) === item.original.typeKind;
  if (sameTypeAsOriginal && item.defaultValue === originalDefaultText &&
      JSON.stringify(item.rawDefaultValue) === JSON.stringify(item.original.defaultValue)) {
    return cloneJsonValue(item.original.defaultValue);
  }
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
  return item.type === 'string' ? item.defaultValue : cloneJsonValue(item.rawDefaultValue);
}

function exposureContract(item) {
  return {
    instanceEditable: item.editable, saveGame: item.save, replicated: item.replicated,
    ...(item.category ? { category: item.category } : {}),
  };
}

function replacementContract(item) {
  if (!supportedStateType(item)) throw new Error(`${item.name} is read-only because its state type is not supported by this editor`);
  return {
    name: item.name, typeName: item.type, typeKind: typeKindFor(item.type),
    defaultValue: typedDefault(item), exposure: exposureContract(item),
  };
}

function declarationChanged(item) {
  if (!item.original) return true;
  return JSON.stringify({
    name: item.name,
    typeMetadataName: supportedStateType(item) ? metadataTypeFor(item.type) : item.typeMetadataName,
    typeKind: typeKindFor(item.type) || item.typeKind,
    defaultValue: typedDefault(item), exposure: exposureContract(item),
  }) !== JSON.stringify(item.original);
}

function buildDeclarationPatch() {
  const declarations = state.declarations.map(item => ({ ...item }));
  const operations = declarations.flatMap(item => {
    if (!item.original) return [{ operation: 'addState', targetAssetId: state.selectedAssetId, state: replacementContract(item) }];
    if (!declarationChanged(item)) return [];
    const owner = surfaceDeclarationOwner(state.surface, item.semanticId, 'state');
    if (owner?.id !== state.selectedAssetId) {
      throw new Error(`${item.name} belongs to source owner ${owner?.assetPath || 'an ambiguous legacy projection'}; select that owner before editing`);
    }
    return [{ operation: 'updateState', targetStateId: item.semanticId, expected: item.original, replacement: replacementContract(item) }];
  }).concat(state.removedDeclarations.map(item => {
    if (!supportedStateType(item)) {
      throw new Error(`${item.name} is read-only because its state type is not supported by this editor`);
    }
    const owner = surfaceDeclarationOwner(state.surface, item.semanticId, 'state');
    if (owner?.id !== state.selectedAssetId) {
      throw new Error(`${item.name} belongs to source owner ${owner?.assetPath || 'an ambiguous legacy projection'}; select that owner before removing it`);
    }
    return { operation: 'removeState', targetStateId: item.semanticId, expected: item.original };
  }));
  return {
    schemaVersion: 'unifiedscript.declaration-patch/v0.2',
    sourceCompilationId: state.sourceCompilationId,
    semanticConfigurationId: state.semanticConfigurationId,
    operations,
  };
}

function nextAiEditValue(edit) {
  if (edit.kind === 'operator') {
    const options = edit.options || [];
    const current = options.indexOf(edit.currentValue);
    const next = options[(current + 1) % options.length];
    if (!next || next === edit.currentValue) throw new Error('Selected operator has no alternative');
    return next;
  }
  if (edit.currentValue === 'true' || edit.currentValue === 'false') {
    return edit.currentValue === 'true' ? 'false' : 'true';
  }
  const numeric = /^(-?(?:\d+|\d*\.\d+))([fFdDmM]?)$/.exec(edit.currentValue);
  if (!numeric) throw new Error(`No deterministic suggestion is available for ${edit.currentValue}`);
  return `${Number(numeric[1]) + 1}${numeric[2]}`;
}

function buildAiProposalDraft() {
  const selectedNode = state.selected?.kind === 'action' ? state.nodes.get(state.selected.id) : null;
  const node = selectedNode?.editCapabilities?.length ? selectedNode
    : state.functionSurface?.actions.find(action => action.editCapabilities.length);
  if (!node) throw new Error('Select a behavior node with a typed edit capability');
  const edit = node.editCapabilities.find(candidate => ['literal', 'operator'].includes(candidate.kind));
  if (!edit) throw new Error('This behavior node has no supported typed AI edit');
  const nextValue = nextAiEditValue(edit);
  const step = edit.kind === 'literal' ? {
    step: 'replaceLiteral', stepId: `ai-${edit.id}`, targetNodeId: edit.targetNodeId,
    expectedValueText: edit.expectedValueText, newValueText: nextValue,
  } : {
    step: 'changeOperator', stepId: `ai-${edit.id}`, targetNodeId: edit.targetNodeId,
    expectedOperator: edit.expectedOperator, newOperator: nextValue,
    expectedResultTypeId: edit.expectedResultTypeId,
    expectedOperandTypeId: edit.expectedOperandTypeId,
    ...(edit.expectedRightOperandTypeId ? { expectedRightOperandTypeId: edit.expectedRightOperandTypeId } : {}),
  };
  const intent = document.getElementById('aiPrompt').value.trim();
  if (!intent) throw new Error('Describe the intended behavior change first');
  return {
    summary: `${node.title}: ${edit.currentValue} → ${nextValue}`,
    draft: {
      schemaVersion: 'unifiedscript.ai-semantic-patch-proposal-draft/v0.1',
      title: `Edit ${node.title}`.slice(0, 160), intent,
      producer: { tool: 'finare-workbench', version: '0.1', model: 'deterministic-demo' },
      plan: {
        schemaVersion: 'unifiedscript.patch-plan/v0.21',
        sourceCompilationId: state.sourceCompilationId,
        semanticConfigurationId: state.semanticConfigurationId,
        steps: [step],
      },
    },
  };
}

async function showAiProposalPreview() {
  const preview = document.getElementById('patchPreview');
  const summary = document.getElementById('patchPreviewSummary');
  const json = document.getElementById('patchPreviewJson');
  const applyButton = document.getElementById('applyPreviewPatchButton');
  try {
    if (!state.authoringConnected) throw new Error('Start debug-serve to validate AI proposals');
    applyButton.disabled = true;
    summary.textContent = 'Validating typed proposal…';
    const candidate = buildAiProposalDraft();
    const response = await fetch('/api/authoring/ai-semantic-proposal/draft', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(candidate.draft),
    });
    const proposal = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(proposal.error?.message || `Proposal validation returned ${response.status}`);
    assertArtifact(proposal.schemaVersion === 'unifiedscript.ai-semantic-patch-proposal/v0.1', 'Unsupported AI proposal contract');
    assertArtifact(/^us_[0-9a-f]{32}$/i.test(proposal.proposalId), 'AI proposal has no deterministic content identity');
    state.aiProposal = proposal;
    summary.textContent = `${candidate.summary} · ${semanticShort(proposal.proposalId)}`;
    json.textContent = JSON.stringify(proposal, null, 2);
    applyButton.disabled = false;
  } catch (error) {
    state.aiProposal = null;
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
  updateHistoryControls();
}

function setAuthoringBusy(busy) {
  state.authoringBusy = busy;
  const saveButton = document.getElementById('saveButton');
  saveButton.disabled = busy || !state.authoringConnected || !state.graphDirty;
  saveButton.textContent = busy ? 'COMMITTING…' : 'COMMIT PATCH';
  document.querySelector('.dirty-dot').classList.toggle('visible', state.graphDirty);
  updateHistoryControls();
}

function updateHistoryControls() {
  const history = state.authoringHistory;
  const unavailable = !state.authoringConnected || state.authoringBusy || state.graphDirty || Boolean(state.authoringGroup);
  const undoButton = document.getElementById('undoButton');
  const redoButton = document.getElementById('redoButton');
  const persistence = history?.persistenceState
    ? ` · journal ${history.persistenceState}${history.persistenceMessage ? `: ${history.persistenceMessage}` : ''}`
    : '';
  undoButton.disabled = unavailable || !history?.canUndo;
  redoButton.disabled = unavailable || !history?.canRedo;
  undoButton.title = history?.canUndo
    ? `Undo: ${history.undoDescription} (${history.undoDepth})${persistence}`
    : `Nothing to undo${persistence}`;
  redoButton.title = history?.canRedo
    ? `Redo: ${history.redoDescription} (${history.redoDepth})${persistence}`
    : `Nothing to redo${persistence}`;
  updateAuthoringGroupControls();
}

function updateAuthoringGroupControls() {
  const groupButton = document.getElementById('groupButton');
  const cancelButton = document.getElementById('cancelGroupButton');
  const group = state.authoringGroup;
  groupButton.disabled = !state.authoringConnected || state.authoringBusy ||
    Boolean(group && group.stagedOperationCount === 0);
  groupButton.textContent = group ? `COMMIT GROUP (${group.stagedOperationCount})` : 'GROUP EDITS';
  groupButton.title = group
    ? `Atomically publish ${group.stagedOperationCount} staged edit(s) as “${group.description}”`
    : 'Begin an in-memory named draft; source and CoreCLR remain unchanged until commit';
  cancelButton.hidden = !group;
  cancelButton.disabled = state.authoringBusy;
  document.getElementById('runButton').disabled = state.debugBusy || Boolean(group);
}

function newAuthoringOperationId() {
  return crypto.randomUUID?.() || `web-${Date.now()}-${crypto.getRandomValues(new Uint32Array(2)).join('')}`;
}

async function authoringRequest(path, patch, label = 'Visual authoring edit', options = {}) {
  const transactionId = options.transactionId || newAuthoringOperationId();
  const groupId = options.groupId ?? (state.authoringGroup?.id || null);
  const headers = {
    'Content-Type': 'application/json',
    'X-Finare-Transaction-Id': transactionId,
    'X-Finare-Transaction-Label': label,
  };
  if (groupId) headers['X-Finare-Group-Id'] = groupId;
  if (options.previewToken) headers['X-Finare-Preview-Token'] = options.previewToken;
  if (state.authoringRevision !== null) {
    headers['X-Finare-Baseline-Revision'] = String(state.authoringRevision);
  }
  const response = await fetch(path, {
    method: 'POST', headers, body: JSON.stringify(patch),
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    const error = new Error(payload.diagnostics?.[0]?.message || payload.error?.message || `Authoring returned ${response.status}`);
    error.code = payload.error?.code;
    throw error;
  }
  const workspace = payload.schemaVersion === 'unifiedscript.ai-semantic-proposal-commit/v0.1'
    ? payload.workspace : payload;
  if (payload.schemaVersion === 'unifiedscript.ai-semantic-proposal-commit/v0.1') {
    assertArtifact(payload.proposalId === patch.proposalId, 'Committed AI proposal identity changed');
  }
  if (workspace.schemaVersion !== 'unifiedscript.authoring-workspace/v0.5') throw new Error('Unsupported authoring response');
  validateVisualProfile(workspace.profile);
  return workspace;
}

function validateAuthoringPreview(preview) {
  assertArtifact(preview?.schemaVersion === 'unifiedscript.authoring-preview/v0.3', 'Unsupported authoring preview protocol');
  assertArtifact(preview.semanticDiff?.schemaVersion === 'unifiedscript.semantic-graph-diff/v0.1', 'Preview has no versioned semantic graph diff');
  assertArtifact(preview.semanticDiff.graphSchemaVersion === 'unifiedscript.graph/v0.13', 'Preview semantic diff targets an unsupported Graph schema');
  assertArtifact(Array.isArray(preview.semanticDiff.functions) && Array.isArray(preview.files), 'Malformed authoring preview');
  assertArtifact(preview.roundTrip?.schemaVersion === 'unifiedscript.csharp-graph-round-trip/v0.1', 'Preview has no C#/Graph round-trip evidence');
  for (const field of ['preservedSemanticIds', 'introducedSemanticIds', 'retiredSemanticIds',
    'preservedSourceComments', 'addedSourceComments', 'removedSourceComments',
    'preservedNodeLayouts', 'preservedGraphComments', 'preservedRoutingHints', 'newUnplacedNodes']) {
    assertArtifact(Number.isInteger(preview.roundTrip[field]) && preview.roundTrip[field] >= 0,
      `Preview round-trip field ${field} is invalid`);
  }
  assertArtifact(typeof preview.roundTrip.layoutChecked === 'boolean', 'Preview layout evidence is invalid');
  assertArtifact(typeof preview.previewToken === 'string' && /^[0-9a-f]{64}$/i.test(preview.previewToken), 'Preview token is invalid');
  return preview;
}

async function previewAuthoringRequest(path, patch, label, transactionId, groupId) {
  const previewPath = path.replace('/api/authoring/', '/api/authoring/preview/');
  const headers = {
    'Content-Type': 'application/json',
    'X-Finare-Transaction-Id': transactionId,
    'X-Finare-Transaction-Label': label,
  };
  if (groupId) headers['X-Finare-Group-Id'] = groupId;
  if (state.authoringRevision !== null) headers['X-Finare-Baseline-Revision'] = String(state.authoringRevision);
  const response = await fetch(previewPath, { method: 'POST', headers, body: JSON.stringify(patch) });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    const error = new Error(payload.diagnostics?.[0]?.message || payload.error?.message || `Preview returned ${response.status}`);
    error.code = payload.error?.code;
    throw error;
  }
  if (payload.schemaVersion === 'unifiedscript.ai-semantic-proposal-preview/v0.1') {
    assertArtifact(payload.proposal?.proposalId === patch.proposalId, 'Previewed AI proposal identity changed');
    const preview = validateAuthoringPreview(payload.preview);
    preview.aiProposal = payload.proposal;
    return preview;
  }
  return validateAuthoringPreview(payload);
}

function previewSideText(files, side) {
  if (!files.length) return '(No C# source text changed.)';
  return files.map(file => `===== ${file.path} =====\n${file[side]}`).join('\n\n');
}

function showSemanticReview(preview) {
  const diff = preview.semanticDiff;
  const summary = diff.summary;
  document.getElementById('semanticReviewTitle').textContent = preview.aiProposal
    ? `AI · ${preview.aiProposal.title} · ${semanticShort(preview.aiProposal.proposalId)}`
    : preview.description;
  document.getElementById('semanticReviewSummary').innerHTML = [
    [summary.addedFunctions, 'FUNCTIONS ADDED'], [summary.removedFunctions, 'FUNCTIONS REMOVED'],
    [summary.modifiedFunctions, 'FUNCTIONS MODIFIED'], [summary.changeCount, 'STABLE-ID CHANGES'],
    [`${preview.roundTrip.preservedSemanticIds} / ${preview.roundTrip.preservedSourceComments}`,
      'IDS / COMMENTS KEPT'],
    [`R${preview.baselineRevision} → R${preview.candidateRevision}`, preview.publishesRuntime ? 'SOURCE + RUNTIME' : 'DRAFT ONLY'],
  ].map(([value, label]) => `<div><strong>${escapeHtml(value)}</strong><span>${escapeHtml(label)}</span></div>`).join('');
  const changes = diff.functions.flatMap(func => func.changes.map(change => ({ func, change })));
  document.getElementById('semanticReviewChanges').innerHTML = changes.length
    ? changes.map(({ func, change }) => `<div class="semantic-change"><em>${escapeHtml(change.category)}</em><strong>${escapeHtml(func.qualifiedName)} · ${escapeHtml(change.entityKind)}/${escapeHtml(change.facet)}</strong><span>${escapeHtml(change.summary)}</span></div>`).join('')
    : '<div class="semantic-review-empty">No executable graph semantics changed. Review the exact declaration/source diff below.</div>';
  document.getElementById('semanticReviewBefore').textContent = previewSideText(preview.files, 'beforeText');
  document.getElementById('semanticReviewAfter').textContent = previewSideText(preview.files, 'afterText');
  semanticReviewDialog.returnValue = 'cancel';
  semanticReviewDialog.showModal();
  return new Promise(resolve => semanticReviewDialog.addEventListener('close', () => resolve(semanticReviewDialog.returnValue === 'commit'), { once: true }));
}

async function reviewAndCommitAuthoring(path, patch, label) {
  const transactionId = newAuthoringOperationId();
  const groupId = state.authoringGroup?.id || null;
  const preview = await previewAuthoringRequest(path, patch, label, transactionId, groupId);
  if (!await showSemanticReview(preview)) return null;
  return authoringRequest(path, patch, label, {
    transactionId, groupId, previewToken: preview.previewToken,
  });
}

async function toggleAuthoringGroup() {
  if (!state.authoringConnected || state.authoringBusy) return;
  const group = state.authoringGroup;
  const description = group?.description || window.prompt('Grouped transaction label', 'Grouped edit')?.trim();
  if (!description) return;
  const groupId = group?.id || `group-${newAuthoringOperationId()}`;
  try {
    setAuthoringBusy(true);
    const workspace = await authoringRequest(
      group ? '/api/authoring/group/commit' : '/api/authoring/group/begin',
      {},
      description,
      { transactionId: groupId, groupId: group ? groupId : null });
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.surface, workspace.sourceText, workspace);
    showToast(
      group ? `Committed “${description}”` : `Started “${description}”`,
      group
        ? `One Undo entry · CoreCLR generation ${workspace.generation.number}`
        : 'Edits now stage in memory without source writes or runtime reload');
  } catch (error) {
    showToast(error.code || 'Grouped transaction failed', error.message);
    setAuthoringBusy(false);
  }
}

async function cancelAuthoringGroup() {
  const group = state.authoringGroup;
  if (!group || state.authoringBusy) return;
  try {
    setAuthoringBusy(true);
    const workspace = await authoringRequest(
      '/api/authoring/group/cancel',
      {},
      group.description,
      { transactionId: group.id, groupId: group.id });
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.surface, workspace.sourceText, workspace);
    showToast(`Discarded “${group.description}”`, 'No source, history, component, or runtime generation was changed');
  } catch (error) {
    showToast(error.code || 'Cancel group failed', error.message);
    setAuthoringBusy(false);
  }
}

async function saveDeclarationChanges(patch = null) {
  if (!state.authoringConnected) return showToast('Read-only artifacts', 'Start debug-serve to write source changes');
  try {
    const nextPatch = patch || buildDeclarationPatch();
    if (!nextPatch.operations.length) return showToast('Nothing to save', 'Declarations are synchronized');
    setAuthoringBusy(true);
    const workspace = await reviewAndCommitAuthoring('/api/authoring/declaration-patch', nextPatch, 'Edit script declarations');
    if (!workspace) {
      setAuthoringBusy(false);
      return showToast('Change cancelled', 'Preview performed no source, history, artifact, or runtime write');
    }
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.surface, workspace.sourceText, workspace);
    showToast(
      workspace.group ? `Staged edit ${workspace.group.stagedOperationCount}` : `Saved revision ${workspace.revision}`,
      workspace.group ? 'Source and CoreCLR are unchanged until group commit' : `CoreCLR generation ${workspace.generation.number} is live`);
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
    const workspace = await reviewAndCommitAuthoring('/api/authoring/semantic-patch', patch, 'Edit behavior graph');
    if (!workspace) {
      setAuthoringBusy(false);
      return showToast('Change cancelled', 'Preview performed no source, history, artifact, or runtime write');
    }
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.surface, workspace.sourceText, workspace);
    showToast(
      workspace.group ? `Staged edit ${workspace.group.stagedOperationCount}` : `Behavior saved · R${workspace.revision}`,
      workspace.group ? 'Source and CoreCLR are unchanged until group commit' : `CoreCLR generation ${workspace.generation.number} reloaded`);
  } catch (error) {
    document.querySelector('.sync-status strong').textContent = error.code === 'UAW1002' ? 'EXTERNAL CONFLICT' : 'PATCH REJECTED';
    showToast(error.code || 'Behavior patch failed', error.message);
    setAuthoringBusy(false);
  }
}

async function applyAiSemanticProposal() {
  const proposal = state.aiProposal;
  if (!proposal || !state.authoringConnected) return;
  try {
    setAuthoringBusy(true);
    const workspace = await reviewAndCommitAuthoring(
      '/api/authoring/ai-semantic-proposal', proposal, `AI · ${proposal.title}`);
    if (!workspace) {
      setAuthoringBusy(false);
      return showToast('AI proposal cancelled', 'Preview performed no source, history, artifact, or runtime write');
    }
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.surface, workspace.sourceText, workspace);
    showToast(
      workspace.group ? `Staged AI proposal ${semanticShort(proposal.proposalId)}` : `AI proposal committed · R${workspace.revision}`,
      workspace.group ? 'Source and CoreCLR are unchanged until group commit' : `Compiler and transaction gates passed · generation ${workspace.generation.number}`);
  } catch (error) {
    document.querySelector('.sync-status strong').textContent = error.code === 'UAW1002' ? 'EXTERNAL CONFLICT' : 'AI PROPOSAL REJECTED';
    showToast(error.code || 'AI proposal failed', error.message);
    setAuthoringBusy(false);
  }
}

async function navigateAuthoringHistory(direction) {
  if (!state.authoringConnected) return showToast('Read-only artifacts', 'Start debug-serve to navigate edit history');
  if (state.graphDirty) return showToast('Commit pending declarations', 'Save or reload local declaration edits before history navigation');
  try {
    setAuthoringBusy(true);
    const workspace = await authoringRequest(
      `/api/authoring/${direction}`,
      {},
      direction === 'undo' ? 'Undo visual authoring edit' : 'Redo visual authoring edit');
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.surface, workspace.sourceText, workspace);
    const action = direction === 'undo' ? 'Undone' : 'Redone';
    showToast(`${action} · R${workspace.revision}`, `CoreCLR generation ${workspace.generation.number} reloaded`);
  } catch (error) {
    document.querySelector('.sync-status strong').textContent = error.code === 'UAW1002' ? 'EXTERNAL CONFLICT' : 'HISTORY REJECTED';
    showToast(error.code || `${direction} failed`, error.message);
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
  const commandBusy = busy || state.debugCommandPending;
  const sessionState = state.debugSession?.state;
  const paused = sessionState === 'paused';
  document.getElementById('runButton').disabled = commandBusy || Boolean(state.authoringGroup) || Boolean(sessionState);
  document.getElementById('pauseButton').disabled = commandBusy || sessionState !== 'running';
  document.getElementById('continueButton').disabled = commandBusy || !paused;
  document.getElementById('stepInButton').disabled = commandBusy || !paused;
  document.getElementById('stepOverButton').disabled = commandBusy || !paused;
  document.getElementById('stepOutButton').disabled = commandBusy || !paused || state.debugSession.frames.length < 2;
  document.getElementById('debugFramePicker').disabled = commandBusy || !paused;
  document.getElementById('debugWatchInput').disabled = commandBusy || !paused;
  document.getElementById('evaluateWatchButton').disabled = commandBusy || !paused || state.debugFrameId === null;
}

async function debugRequest(path, body = {}, schemaVersion = 'unifiedscript.debug-session/v0.2') {
  const response = await fetch(path, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(payload.error?.message || `Debug returned ${response.status}`);
  if (payload.schemaVersion !== schemaVersion) throw new Error('Unsupported debug response');
  return payload;
}

function debugExecutionRequest(includePause = false) {
  const session = state.debugSession;
  if (!session) throw new Error('There is no active debug execution.');
  const request = {
    generationId: session.generation.generationId,
    executionId: session.executionId,
  };
  if (includePause) request.pauseId = session.pause.pauseId;
  return request;
}

function renderDebugFrame(frameId) {
  const response = state.debugSession;
  if (response?.state !== 'paused') return;
  const frame = response.frames.find(candidate => candidate.frameId === Number(frameId)) || response.frames[0];
  state.debugFrameId = frame?.frameId ?? null;
  const locals = response.frameLocals.find(item => item.frameId === state.debugFrameId)?.locals || [];
  document.getElementById('debugLocals').innerHTML = locals.map(local =>
    `<span title="${escapeHtml(local.typeMetadataName)}">${escapeHtml(local.name)} <b>${escapeHtml(local.display)}</b></span>`).join('');
  document.getElementById('debugWatchResult').textContent = frame
    ? `${frame.methodMetadataName} · ${frame.sourceSpan?.file || 'generated IL'}:${frame.sourceSpan?.startLine || frame.ilOffset}`
    : 'No managed stack frame';
  const graphElement = frame?.nodeId && [...document.querySelectorAll('[data-debug-semantic-id]')]
    .find(element => element.dataset.debugSemanticId === frame.nodeId);
  if (graphElement) {
    document.querySelectorAll('.executing').forEach(element => element.classList.remove('executing'));
    graphElement.classList.add('executing');
    if (frame.index === 0) graphElement.classList.add('paused');
    graphElement.scrollIntoView({ behavior: 'smooth', block: 'center' });
    selectNode(graphElement.dataset.nodeId, false);
  }
  setDebugBusy(false);
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
  const picker = document.getElementById('debugFramePicker');
  picker.innerHTML = response.frames.map(candidate => {
    const location = candidate.sourceSpan ? `L${candidate.sourceSpan.startLine}` : `IL ${candidate.ilOffset}`;
    return `<option value="${candidate.frameId}">#${candidate.index} ${escapeHtml(candidate.methodMetadataName)} · ${location}</option>`;
  }).join('');
  state.debugFrameId = frame?.frameId ?? null;
  debugStrip.classList.add('visible');
  renderDebugFrame(state.debugFrameId);
}

function renderDebugRunning(response, reason = 'RUNNING') {
  state.debugSession = response;
  state.debugFrameId = null;
  clearDebugVisuals(false);
  document.getElementById('debugReason').textContent = reason;
  document.getElementById('debugLocation').textContent = `execution ${response.executionId} · generation ${response.generation.number}`;
  document.getElementById('debugLocals').replaceChildren();
  document.getElementById('debugFramePicker').replaceChildren();
  document.getElementById('debugWatchResult').textContent = 'Waiting for the next managed safe point';
  debugStrip.classList.add('visible');
  setDebugBusy(false);
}

function renderDebugCompletion(response) {
  state.debugSession = null;
  state.debugFrameId = null;
  clearDebugVisuals();
  document.querySelector('.target-label').textContent = `CLR GENERATION ${response.generation.number} · COMPLETED`;
  setDebugBusy(false);
  showToast(`Execution returned ${response.result?.returnValue?.json ?? '<void>'}`, 'Live CoreCLR session completed');
}

function renderDebugError(error) {
  state.debugSession = null;
  state.debugFrameId = null;
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
  setDebugBusy(true);
  try {
    const func = state.functionSurface;
    const request = {
      methodId: func.id,
      arguments: func.parameters.map(defaultArgument),
      breakpointNodeIds,
      expectedSourceCompilationId: state.sourceCompilationId,
      expectedSemanticConfigurationId: state.semanticConfigurationId,
    };
    if (!func.isStatic) {
      request.receiver = {
        assetId: state.selectedAssetId,
        stateOverrides: state.declarations.filter(item =>
          item.original && supportedStateType(item) && declarationChanged(item))
          .map(item => ({ stateId: item.semanticId, value: typedDefault(item) })),
      };
    }
    const response = await debugRequest('/api/debug/start', request);
    renderDebugRunning(response);
    await waitForDebugState();
  } catch (error) {
    renderDebugError(error);
  }
}

async function waitForDebugState() {
  while (state.debugSession?.state === 'running') {
    const request = debugExecutionRequest();
    const response = await debugRequest('/api/debug/wait', request);
    if (response.state === 'running') {
      renderDebugRunning(response);
      continue;
    }
    response.state === 'paused' ? renderLiveDebugPause(response) : renderDebugCompletion(response);
  }
}

async function pauseDebug() {
  if (state.debugSession?.state !== 'running') return;
  state.debugCommandPending = true;
  setDebugBusy(true);
  try {
    const request = debugExecutionRequest();
    await debugRequest('/api/debug/pause', request);
    document.getElementById('debugReason').textContent = 'PAUSE REQUESTED';
    state.debugCommandPending = false;
    setDebugBusy(false);
  } catch (error) {
    state.debugCommandPending = false;
    setDebugBusy(false);
    showToast('Pause failed', error.message);
  }
}

async function resumeDebug(mode) {
  if (state.debugSession?.state !== 'paused') return;
  setDebugBusy(true);
  try {
    const request = debugExecutionRequest(true);
    const response = await debugRequest(`/api/debug/${mode}`, request);
    renderDebugRunning(response, mode === 'continue' ? 'RUNNING' : `STEPPING · ${mode.replace('-', ' ').toUpperCase()}`);
    await waitForDebugState();
  } catch (error) {
    setDebugBusy(false);
    showToast('Debug command failed', error.message);
  }
}

async function evaluateDebugWatch() {
  if (state.debugSession?.state !== 'paused' || state.debugFrameId === null) return;
  const expression = document.getElementById('debugWatchInput').value.trim();
  if (!expression) return;
  setDebugBusy(true);
  try {
    const request = { ...debugExecutionRequest(true), frameId: state.debugFrameId, expression };
    const evaluation = await debugRequest(
      '/api/debug/evaluate', request, 'unifiedscript.debug-evaluation/v0.1');
    document.getElementById('debugWatchResult').textContent =
      `${evaluation.expression} = ${evaluation.value.display} · ${evaluation.value.typeMetadataName}`;
  } catch (error) {
    document.getElementById('debugWatchResult').textContent = error.message;
  } finally {
    setDebugBusy(false);
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
document.getElementById('pauseButton').addEventListener('click', pauseDebug);
document.getElementById('continueButton').addEventListener('click', () => resumeDebug('continue'));
document.getElementById('stepInButton').addEventListener('click', () => resumeDebug('step-in'));
document.getElementById('stepOverButton').addEventListener('click', () => resumeDebug('step-over'));
document.getElementById('stepOutButton').addEventListener('click', () => resumeDebug('step-out'));
document.getElementById('debugFramePicker').addEventListener('change', event => renderDebugFrame(event.target.value));
document.getElementById('evaluateWatchButton').addEventListener('click', evaluateDebugWatch);
document.getElementById('debugWatchInput').addEventListener('keydown', event => {
  if (event.key === 'Enter') evaluateDebugWatch();
});
document.getElementById('closeInspectorButton').addEventListener('click', () =>
  document.querySelector('.inspector-panel').classList.remove('compact-visible'));
document.getElementById('aiViewButton').addEventListener('click', () => {
  renderAiDeclarations();
  renderAiBehavior();
  aiDrawer.classList.add('visible');
  aiDrawer.setAttribute('aria-hidden', 'false');
});
document.getElementById('apiBrowserButton').addEventListener('click', () => {
  renderApiBrowser();
  apiBrowserDrawer.classList.add('visible');
  apiBrowserDrawer.setAttribute('aria-hidden', 'false');
  setTimeout(() => document.getElementById('apiBrowserSearch').focus(), 30);
});
document.getElementById('insightButton').addEventListener('click', () => {
  if (state.authoringInsight) renderAuthoringInsight();
  insightDrawer.classList.add('visible');
  insightDrawer.setAttribute('aria-hidden', 'false');
});
document.getElementById('closeInsight').addEventListener('click', () => {
  insightDrawer.classList.remove('visible');
  insightDrawer.setAttribute('aria-hidden', 'true');
});
document.getElementById('importInsightButton').addEventListener('click', () =>
  document.getElementById('insightFile').click());
document.getElementById('insightFile').addEventListener('change', event =>
  importGameplayProfile(event.target.files?.[0]));
document.getElementById('closeApiBrowser').addEventListener('click', () => {
  apiBrowserDrawer.classList.remove('visible');
  apiBrowserDrawer.setAttribute('aria-hidden', 'true');
});
document.getElementById('apiBrowserSearch').addEventListener('input', renderApiBrowser);
document.getElementById('apiBrowserRoute').addEventListener('change', renderApiBrowser);
document.getElementById('closeAiDrawer').addEventListener('click', () => {
  aiDrawer.classList.remove('visible');
  aiDrawer.setAttribute('aria-hidden', 'true');
});
document.getElementById('previewPatchButton').addEventListener('click', showAiProposalPreview);
document.getElementById('applyPreviewPatchButton').addEventListener('click', applyAiSemanticProposal);
document.getElementById('saveButton').addEventListener('click', () => saveDeclarationChanges());
document.getElementById('groupButton').addEventListener('click', toggleAuthoringGroup);
document.getElementById('cancelGroupButton').addEventListener('click', cancelAuthoringGroup);
document.getElementById('undoButton').addEventListener('click', () => navigateAuthoringHistory('undo'));
document.getElementById('redoButton').addEventListener('click', () => navigateAuthoringHistory('redo'));
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
  const editingText = event.target.matches?.('input, textarea, select, [contenteditable="true"]');
  if (!editingText && (event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'z') {
    event.preventDefault();
    if (!state.authoringBusy) navigateAuthoringHistory(event.shiftKey ? 'redo' : 'undo');
  }
  if (!editingText && (event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'y') {
    event.preventDefault();
    if (!state.authoringBusy) navigateAuthoringHistory('redo');
  }
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
  if (event.key === 'Escape' && apiBrowserDrawer.classList.contains('visible')) {
    document.getElementById('closeApiBrowser').click();
  }
  if (event.key === 'Escape' && insightDrawer.classList.contains('visible')) {
    document.getElementById('closeInsight').click();
  }
});
document.getElementById('copyCodeButton').addEventListener('click', () =>
  copyText(document.getElementById('codeEditor').innerText, 'Canonical text copied'));

loadWorkspaceArtifacts();

const state = {
  view: 'structure',
  zoom: 1,
  artifactLoaded: false,
  graphArtifactLoaded: false,
  graphDirty: false,
  sourceCompilationId: null,
  semanticConfigurationId: null,
  assetId: null,
  sourceText: null,
  sourcePath: 'DamagePolicy.cs',
  graphSchemaVersion: null,
  graphFunction: null,
  authoringConnected: false,
  authoringBusy: false,
  authoringRevision: null,
  generation: null,
  removedDeclarations: [],
  previewPatch: null,
  debugConnected: false,
  debugBusy: false,
  debugHealth: null,
  debugSession: null,
  selected: { kind: 'state', id: 'baseDamage' },
  declarations: [
    { id: 'baseDamage', name: 'BaseDamage', type: 'int', defaultValue: '20', editable: true, save: false, replicated: false, category: 'Damage', semanticId: 'us_8ad1c44ff31210aaec986daa117f7f2e' },
    { id: 'criticalMultiplier', name: 'CriticalMultiplier', type: 'float', defaultValue: '2.0', editable: true, save: false, replicated: false, category: 'Damage', semanticId: 'us_a738a8a6775e38c5a3fac2f1470b8c22' },
    { id: 'maxDamage', name: 'MaxDamage', type: 'int', defaultValue: '999', editable: false, save: true, replicated: false, semanticId: 'us_b35294ef19765cd35e1388fc4ff15de3' },
  ],
  nodes: {
    entry: { title: 'ApplyDamage', kind: 'FUNCTION ENTRY', line: 13, semanticId: 'us_71ac89a4bd0a9cd2b7ba53d110f8432d', detail: 'Parameters enter one shared semantic method body.' },
    scale: { title: 'Compute scaled', kind: 'LOCAL DECLARATION', line: 15, semanticId: 'us_302e621405ba62aa397d894c2f5a10b4', detail: 'Expression stays inline: incomingDamage × BaseDamage.' },
    criticalBranch: { title: 'critical?', kind: 'DECISION REGION', line: 16, semanticId: 'us_1d19d6276330b655a2c8b846fc460820', detail: 'Structured if region owns True and False lanes.' },
    criticalScale: { title: 'Scale critical damage', kind: 'LOCAL MUTATION', line: 18, semanticId: 'us_f931e6bbf8314b51f2b0867520d89d3a', detail: 'Mutates scaled using declared script state.' },
    clamp: { title: 'Math.Clamp', kind: 'PURE ENGINE CALL', line: 21, semanticId: 'us_494225517602e96082587c810710439e', detail: 'Bound engine call. Breakpoint installed on this semantic statement.' },
    return: { title: 'Return scaled', kind: 'RETURN', line: 22, semanticId: 'us_d8de098855897c9a7c8ad6532df103a2', detail: 'Returns the final graph-safe scalar.' },
  },
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
  return value.replace(/[&<>'"]/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[character]);
}

function toPascalCase(value) {
  const normalized = value.trim().replace(/[^A-Za-z0-9_]+/g, ' ');
  return normalized.split(/\s+/).filter(Boolean).map(part => part.charAt(0).toUpperCase() + part.slice(1)).join('') || 'NewState';
}

function semanticShort(value) {
  return `${value.slice(0, 10)}…${value.slice(-4)}`;
}

function declarationDomId(name) {
  return `${name.charAt(0).toLowerCase()}${name.slice(1)}`;
}

function artifactDefaultText(item) {
  if (item.typeKind === 'floatingPoint' && Number.isInteger(item.defaultValue)) return `${item.defaultValue}.0`;
  return String(item.defaultValue);
}

function renderDeclarationList() {
  const list = document.getElementById('stateList');
  list.replaceChildren();
  state.declarations.forEach(item => {
    const button = document.createElement('button');
    button.className = 'declaration-item';
    button.type = 'button';
    button.dataset.kind = 'state';
    button.dataset.id = item.id;
    button.innerHTML = `<span class="declaration-glyph state-glyph">S</span><span class="declaration-copy"><strong>${escapeHtml(item.name)}</strong><small>${escapeHtml(item.type)} · ${escapeHtml(item.defaultValue)}</small></span>${item.editable ? '<span class="exposure-dot" title="Instance Editable"></span>' : item.save ? '<span class="save-dot" title="SaveGame"></span>' : '<span></span>'}`;
    button.addEventListener('click', () => selectDeclaration(item.id));
    list.appendChild(button);
  });
  document.getElementById('stateCount').textContent = String(state.declarations.length);
}

async function loadWorkspaceArtifacts() {
  try {
    const workspaceResponse = await fetch('/api/authoring/workspace', { cache: 'no-store' });
    if (!workspaceResponse.ok) throw new Error(`Authoring workspace returned ${workspaceResponse.status}`);
    const workspace = await workspaceResponse.json();
    assertArtifact(workspace.schemaVersion === 'unifiedscript.authoring-workspace/v0.1', 'Unsupported authoring workspace protocol');
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.sourceText, workspace);
    state.authoringConnected = true;
    await connectDebugBridge();
    return;
  } catch (authoringError) {
    console.info('Live authoring host is offline; loading checked-in read-only artifacts.', authoringError.message);
  }

  try {
    const [assetResponse, graphResponse, sourceResponse] = await Promise.all([
      fetch('data/damage-policy.asset.json', { cache: 'no-store' }),
      fetch('data/damage-policy.graph.json', { cache: 'no-store' }),
      fetch('data/damage-policy.source.cs', { cache: 'no-store' }),
    ]);
    if (!assetResponse.ok) throw new Error(`Declaration artifact returned ${assetResponse.status}`);
    if (!graphResponse.ok) throw new Error(`Graph artifact returned ${graphResponse.status}`);
    if (!sourceResponse.ok) throw new Error(`Source artifact returned ${sourceResponse.status}`);
    const [artifact, graph, sourceText] = await Promise.all([
      assetResponse.json(),
      graphResponse.json(),
      sourceResponse.text(),
    ]);
    applyWorkspaceArtifacts(artifact, graph, sourceText);
  } catch (error) {
    document.querySelector('.sync-status strong').textContent = 'FALLBACK';
    console.warn('Falling back to embedded prototype workspace data.', error);
  }
}

function applyWorkspaceArtifacts(artifact, graph, sourceText, workspace = null) {
  if (artifact.schemaVersion !== 'unifiedscript.script-asset/v0.1' || artifact.assets?.length !== 1) {
    throw new Error('Unsupported Declaration IR artifact');
  }

  const selectedStateId = state.selected.kind === 'state'
    ? state.declarations.find(item => item.id === state.selected.id)?.semanticId
    : null;
  const selectedNodeId = state.selected.kind === 'node'
    ? state.nodes[state.selected.id]?.semanticId
    : null;
  const breakpointIds = activeBreakpointNodeIds();
  const asset = artifact.assets[0];
  const graphFunction = validateGraphArtifact(graph, artifact, asset, sourceText);
  state.sourceCompilationId = artifact.sourceCompilationId;
  state.semanticConfigurationId = artifact.semanticConfigurationId;
  state.assetId = asset.id;
  state.sourceText = sourceText;
  state.sourcePath = workspace?.sourcePath || asset.span.filePath;
  state.graphSchemaVersion = graph.schemaVersion;
  state.graphFunction = graphFunction;
  state.authoringConnected = Boolean(workspace);
  state.authoringRevision = workspace?.revision ?? null;
  state.generation = workspace?.generation ?? null;
  if (workspace) {
    state.debugConnected = true;
    state.debugHealth = {
      generationId: workspace.generation.generationId,
      sourceCompilationId: workspace.generation.sourceCompilationId,
      semanticConfigurationId: workspace.generation.semanticConfigurationId,
    };
  }
  state.declarations = asset.states.map(item => ({
    id: declarationDomId(item.name),
    name: item.name,
    type: item.typeDisplayName,
    typeKind: item.typeKind,
    defaultValue: artifactDefaultText(item),
    editable: item.exposure.instanceEditable,
    save: item.exposure.saveGame,
    replicated: item.exposure.replicated,
    category: item.exposure.category || '',
    semanticId: item.id,
    sourceStartLine: item.span.start.line,
    sourceEndLine: item.span.end.line,
    original: {
      name: item.name,
      typeMetadataName: item.typeMetadataName,
      typeKind: item.typeKind,
      defaultValue: item.defaultValue,
      exposure: {
        instanceEditable: item.exposure.instanceEditable,
        saveGame: item.exposure.saveGame,
        replicated: item.exposure.replicated,
        ...(item.exposure.category ? { category: item.exposure.category } : {}),
      },
    },
  }));
  state.removedDeclarations = [];
  state.artifactLoaded = true;
  state.graphArtifactLoaded = true;
  state.graphDirty = false;
  state.nodes = adaptGraphArtifact(graphFunction, asset);
  renderDeclarationList();
  renderSourceArtifact();
  renderBehaviorGraph();
  restoreBreakpoints(breakpointIds);
  renderAiDeclarations();
  renderAiBehavior();
  setAuthoringBusy(false);

  const selectedDeclaration = state.declarations.find(item => item.semanticId === selectedStateId);
  const selectedNode = Object.entries(state.nodes).find(([, node]) => node.semanticId === selectedNodeId);
  if (selectedDeclaration) selectDeclaration(selectedDeclaration.id, false);
  else if (selectedNode) selectNode(selectedNode[0], false);
  else if (state.declarations.length > 0) selectDeclaration(state.declarations[0].id, false);
  else selectNode('entry', false);

  document.querySelector('.sync-status strong').textContent = workspace
    ? `SYNCED · R${workspace.revision}`
    : 'IR + GRAPH';
  document.querySelector('.target-label').textContent = workspace
    ? `CLR GENERATION ${workspace.generation.number} · GRAPH v0.12`
    : 'GRAPH v0.12 · DECLARATION IR v0.1';
}

function assertArtifact(condition, message) {
  if (!condition) throw new Error(message);
}

function exactlyOne(values, label) {
  assertArtifact(values.length === 1, `Expected exactly one ${label}, found ${values.length}`);
  return values[0];
}

function validateGraphArtifact(graph, artifact, asset, sourceText) {
  assertArtifact(graph.schemaVersion === 'unifiedscript.graph/v0.12', 'Unsupported Graph artifact');
  assertArtifact(graph.sourceCompilationId === artifact.sourceCompilationId, 'Graph/source compilation identity mismatch');
  assertArtifact(graph.semanticConfigurationId === artifact.semanticConfigurationId, 'Graph/semantic configuration identity mismatch');
  const graphFunction = exactlyOne(graph.functions || [], 'Graph function');
  const declaredFunction = exactlyOne(asset.functions || [], 'Script Asset function');
  assertArtifact(graphFunction.id === declaredFunction.id, 'Graph/Declaration function identity mismatch');
  assertArtifact(Array.isArray(graphFunction.nodes) && Array.isArray(graphFunction.edges) && Array.isArray(graphFunction.regions), 'Malformed Graph function');

  const nodes = new Map(graphFunction.nodes.map(node => [node.id, node]));
  const regions = new Map(graphFunction.regions.map(region => [region.id, region]));
  assertArtifact(nodes.size === graphFunction.nodes.length, 'Duplicate Graph node identity');
  assertArtifact(regions.size === graphFunction.regions.length, 'Duplicate Graph region identity');
  assertArtifact(regions.has(graphFunction.rootRegionId), 'Graph root region is missing');
  graphFunction.edges.forEach(edge => {
    assertArtifact(nodes.has(edge.fromNode) && nodes.has(edge.toNode), `Graph edge ${edge.id} has a missing endpoint`);
  });
  graphFunction.regions.forEach(region => {
    (region.nodeIds || []).forEach(id => assertArtifact(nodes.has(id), `Region ${region.id} references missing node ${id}`));
    (region.childRegionIds || []).forEach(id => assertArtifact(regions.has(id), `Region ${region.id} references missing child ${id}`));
  });

  const declarationIds = new Map(asset.states.map(declaration => [declaration.name, declaration.id]));
  const stateReferences = graphFunction.nodes
    .filter(node => node.kind === 'identifier' && declarationIds.has(node.properties?.name));
  stateReferences.forEach(node => assertArtifact(
      node.properties.declarationId === declarationIds.get(node.properties.name),
      `Graph state reference ${node.properties.name} lost Declaration IR identity`,
  ));
  const sourceLineCount = sourceText.replace(/\r\n/g, '\n').replace(/\n$/, '').split('\n').length;
  graphFunction.nodes.forEach(node => assertArtifact(
    node.span?.filePath === asset.span.filePath
      && node.span.start.line >= 1
      && node.span.end.line <= sourceLineCount,
    `Graph node ${node.id} has a source span outside the loaded source artifact`,
  ));
  return graphFunction;
}

function adaptGraphArtifact(graphFunction, asset) {
  const nodesById = new Map(graphFunction.nodes.map(node => [node.id, node]));
  const dataInputs = new Map();
  graphFunction.edges.filter(edge => edge.kind === 'data').forEach(edge => {
    const key = `${edge.toNode}:${edge.toPort}`;
    assertArtifact(!dataInputs.has(key), `Duplicate data input ${key}`);
    dataInputs.set(key, edge.fromNode);
  });
  const inputNode = (node, port) => {
    const sourceId = dataInputs.get(`${node.id}:${port}`);
    assertArtifact(sourceId && nodesById.has(sourceId), `${node.kind} ${node.id} is missing ${port}`);
    return nodesById.get(sourceId);
  };
  const inputNodes = node => [...dataInputs.entries()]
    .filter(([key]) => key.startsWith(`${node.id}:`))
    .map(([, sourceId]) => nodesById.get(sourceId));
  const mergeRefs = (...groups) => {
    const result = new Map();
    groups.flat().filter(Boolean).forEach(reference => result.set(reference.declarationId, reference));
    return [...result.values()];
  };
  const expression = (node, visited = new Set()) => {
    assertArtifact(node && !visited.has(node.id), `Cyclic or missing expression at ${node?.id || 'unknown'}`);
    const path = new Set(visited);
    path.add(node.id);
    if (node.kind === 'identifier') {
      const reference = node.properties?.declarationId
        ? [{ name: node.properties.name, declarationId: node.properties.declarationId }]
        : [];
      return { text: node.properties?.name || node.label, refs: reference };
    }
    if (node.kind === 'literal') return { text: node.properties?.value ?? node.label, refs: [] };
    if (node.kind === 'binary') {
      const left = expression(inputNode(node, 'left'), path);
      const right = expression(inputNode(node, 'right'), path);
      return { text: `${left.text} ${node.properties.operator} ${right.text}`, refs: mergeRefs(left.refs, right.refs) };
    }
    if (node.kind === 'memberAccess') {
      const target = expression(inputNode(node, 'target'), path);
      return { text: `${target.text}.${node.properties.member || node.label}`, refs: target.refs };
    }
    if (node.kind === 'invocation') {
      const target = expression(inputNode(node, 'target'), path);
      const argumentsByPort = [...dataInputs.entries()]
        .filter(([key]) => key.startsWith(`${node.id}:arg`))
        .sort(([left], [right]) => Number(left.split(':arg')[1]) - Number(right.split(':arg')[1]))
        .map(([, sourceId]) => expression(nodesById.get(sourceId), path));
      return {
        text: `${target.text}(${argumentsByPort.map(argument => argument.text).join(', ')})`,
        refs: mergeRefs(target.refs, argumentsByPort.flatMap(argument => argument.refs)),
      };
    }
    throw new Error(`Unsupported inline Graph expression kind ${node.kind}`);
  };
  const containsKind = (node, kind, visited = new Set()) => {
    if (!node || visited.has(node.id)) return null;
    if (node.kind === kind) return node;
    const nextVisited = new Set(visited);
    nextVisited.add(node.id);
    for (const child of inputNodes(node)) {
      const match = containsKind(child, kind, nextVisited);
      if (match) return match;
    }
    return null;
  };
  const byKind = kind => graphFunction.nodes.filter(node => node.kind === kind);
  const entry = exactlyOne(byKind('entry'), 'entry node');
  const variable = exactlyOne(byKind('variable'), 'local declaration node');
  const branch = exactlyOne(byKind('branch'), 'branch node');
  const returned = exactlyOne(byKind('return'), 'return node');
  const assignments = byKind('assignment');
  assertArtifact(assignments.length === 2, `Expected two assignment statements, found ${assignments.length}`);
  const clampAssignment = exactlyOne(assignments.filter(node => containsKind(inputNode(node, 'assignedValue'), 'invocation')), 'call assignment');
  const criticalAssignment = exactlyOne(assignments.filter(node => node.id !== clampAssignment.id), 'branch assignment');
  assertArtifact(
    clampAssignment.properties.debugTargetId && criticalAssignment.properties.debugTargetId,
    'Assignment Graph nodes are missing executable debug statement identities',
  );
  const invocation = containsKind(inputNode(clampAssignment, 'assignedValue'), 'invocation');
  const ifRegion = exactlyOne(graphFunction.regions.filter(region => region.kind === 'if'), 'if region');
  const thenRegion = exactlyOne(graphFunction.regions.filter(region => region.kind === 'then'), 'then region');
  const elseRegion = exactlyOne(graphFunction.regions.filter(region => region.kind === 'else'), 'else region');
  assertArtifact(thenRegion.nodeIds.includes(criticalAssignment.id), 'Branch assignment is not owned by the then region');
  assertArtifact(elseRegion.isImplicit && elseRegion.nodeIds.length === 0, 'Compact prototype expects an implicit empty else region');

  const declarationFunction = exactlyOne(asset.functions, 'Declaration function');
  const simpleType = metadataName => ({
    'System.Int32': 'int',
    'System.Single': 'float',
    'System.Boolean': 'bool',
    'System.String': 'string',
  })[metadataName] || metadataName.split('.').at(-1);
  const localValue = expression(inputNode(variable, 'value'));
  const branchCondition = expression(inputNode(branch, 'condition'));
  const criticalTarget = expression(inputNode(criticalAssignment, 'target'));
  const criticalValue = expression(inputNode(criticalAssignment, 'assignedValue'));
  const clampTarget = expression(inputNode(clampAssignment, 'target'));
  const clampExpression = expression(inputNode(clampAssignment, 'assignedValue'));
  const clampMinimum = inputNode(invocation, 'arg1');
  assertArtifact(clampMinimum.kind === 'literal', 'Compact prototype expects a literal clamp minimum');
  const returnValue = expression(inputNode(returned, 'value'));
  const invocationArguments = ['arg0', 'arg1', 'arg2'].map((port, index) => {
    const value = expression(inputNode(invocation, port));
    return { label: ['value', 'min', 'max'][index], value: value.text, refs: value.refs };
  });
  const lineOf = node => node.span.start.line;
  return {
    entry: {
      title: declarationFunction.name,
      kind: 'FUNCTION ENTRY',
      line: lineOf(entry),
      semanticId: entry.id,
      debugSemanticId: null,
      detail: `${declarationFunction.name} enters the shared semantic method body.`,
      parameters: declarationFunction.parameters.map(parameter => ({ name: parameter.name, type: simpleType(parameter.typeMetadataName) })),
      returnType: simpleType(declarationFunction.returnTypeMetadataName),
    },
    scale: {
      title: `Declare ${variable.properties.name}`,
      kind: 'LOCAL DECLARATION',
      line: lineOf(variable),
      semanticId: variable.id,
      debugSemanticId: variable.id,
      detail: `${variable.properties.name} = ${localValue.text}; ordinary expression nodes stay inline.`,
      localName: variable.properties.name,
      localType: variable.properties.type || variable.properties.resolvedType,
      expression: localValue.text,
      declarationRefs: localValue.refs,
    },
    criticalBranch: {
      title: `${branchCondition.text}?`,
      kind: 'DECISION REGION',
      line: lineOf(branch),
      semanticId: branch.id,
      debugSemanticId: branch.id,
      regionId: ifRegion.id,
      detail: `Structured if region evaluates ${branchCondition.text} and owns explicit true plus implicit false lanes.`,
      condition: branchCondition.text,
      declarationRefs: branchCondition.refs,
    },
    criticalScale: {
      title: `${criticalTarget.text} ${criticalAssignment.properties.operator} ${criticalValue.text}`,
      kind: 'LOCAL MUTATION',
      line: lineOf(criticalAssignment),
      semanticId: criticalAssignment.id,
      debugSemanticId: criticalAssignment.properties.debugTargetId,
      regionId: thenRegion.id,
      detail: `Mutates ${criticalTarget.text} inside the true control region.`,
      target: criticalTarget.text,
      operator: criticalAssignment.properties.operator,
      operand: criticalValue.text,
      declarationRefs: mergeRefs(criticalTarget.refs, criticalValue.refs),
    },
    clamp: {
      title: invocation.label.split('(')[0].split(' ').at(-1),
      kind: 'PURE ENGINE CALL',
      line: lineOf(clampAssignment),
      semanticId: clampAssignment.id,
      debugSemanticId: clampAssignment.properties.debugTargetId,
      expressionNodeId: invocation.id,
      detail: `Assignment statement owns ${clampExpression.text}; breakpoints target the outer executable statement.`,
      target: clampTarget.text,
      expression: clampExpression.text,
      arguments: invocationArguments,
      minimumLiteralId: clampMinimum.id,
      minimumValue: clampMinimum.properties.value,
      declarationRefs: mergeRefs(clampTarget.refs, clampExpression.refs),
    },
    return: {
      title: `Return ${returnValue.text}`,
      kind: 'RETURN',
      line: lineOf(returned),
      semanticId: returned.id,
      debugSemanticId: returned.id,
      detail: `Returns ${returnValue.text} from the graph-safe method.`,
      expression: returnValue.text,
      returnType: simpleType(declarationFunction.returnTypeMetadataName),
      declarationRefs: returnValue.refs,
    },
  };
}

function expressionMarkup(value, references = []) {
  const identities = new Map(references.map(reference => [reference.name, reference.declarationId]));
  return value.split(/([A-Za-z_][A-Za-z0-9_]*)/).map(token => identities.has(token)
    ? `<span class="state-reference" data-declaration-id="${escapeHtml(identities.get(token))}">${escapeHtml(token)}</span>`
    : escapeHtml(token)).join('');
}

function renderBehaviorGraph() {
  const { entry, scale, criticalBranch, criticalScale, clamp, return: returned } = state.nodes;
  flowCanvas.innerHTML = `
    <div class="flow-rail" aria-hidden="true"></div>
    <article class="flow-node entry-node" data-node-id="entry" data-semantic-id="${entry.semanticId}" data-line="${entry.line}" tabindex="0">
      <div class="node-ordinal">01</div><div class="node-content"><div class="node-kicker">ENTRY</div><h3>${escapeHtml(entry.title)}</h3>
      <div class="token-row">${entry.parameters.map(parameter => `<span class="data-token parameter"><i></i>${escapeHtml(parameter.name)} <em>${escapeHtml(parameter.type)}</em></span>`).join('')}</div></div>
      <button class="breakpoint-toggle" type="button" aria-label="函数入口暂不支持语句断点" disabled></button>
    </article>
    <div class="flow-connector"><span>then</span></div>
    <article class="flow-node compute-node" data-node-id="scale" data-semantic-id="${scale.semanticId}" data-debug-semantic-id="${scale.debugSemanticId}" data-line="${scale.line}" tabindex="0">
      <div class="node-ordinal">02</div><div class="node-content"><div class="node-kicker">COMPUTE LOCAL</div>
      <h3><span class="local-name">${escapeHtml(scale.localName)}</span> <b>=</b> <span class="inline-expression">${expressionMarkup(scale.expression, scale.declarationRefs)}</span></h3>
      <p>普通表达式内联，不拆成细粒度小节点。</p></div><button class="breakpoint-toggle" type="button" aria-label="切换断点"></button>
    </article>
    <div class="flow-connector"><span>then</span></div>
    <section class="branch-region" data-node-id="criticalBranch" data-semantic-id="${criticalBranch.semanticId}" data-debug-semantic-id="${criticalBranch.debugSemanticId}" data-region-id="${criticalBranch.regionId}" data-line="${criticalBranch.line}" tabindex="0">
      <header class="region-header"><div><span class="region-index">03</span><span class="node-kicker">DECISION REGION</span><h3>${expressionMarkup(criticalBranch.condition, criticalBranch.declarationRefs)}?</h3></div><span class="region-badge">IF</span></header>
      <div class="branch-columns"><div class="branch-lane true-lane"><div class="lane-label"><span>TRUE</span><i></i></div>
      <article class="region-action flow-node compact" data-node-id="criticalScale" data-semantic-id="${criticalScale.semanticId}" data-debug-semantic-id="${criticalScale.debugSemanticId}" data-region-id="${criticalScale.regionId}" data-line="${criticalScale.line}" tabindex="0">
        <div class="node-content"><div class="node-kicker">MUTATE LOCAL</div><h3><span class="local-name">${escapeHtml(criticalScale.target)}</span> <b>${escapeHtml(criticalScale.operator)}</b> ${expressionMarkup(criticalScale.operand, criticalScale.declarationRefs)}</h3></div>
        <button class="breakpoint-toggle" type="button" aria-label="切换断点"></button>
      </article></div><div class="branch-lane false-lane"><div class="lane-label"><span>FALSE · IMPLICIT</span><i></i></div><div class="pass-through"><span>NO OP</span><small>保持 ${escapeHtml(criticalScale.target)}</small></div></div></div>
    </section>
    <div class="flow-connector merge"><span>merge</span></div>
    <article class="flow-node call-node has-breakpoint" data-node-id="clamp" data-semantic-id="${clamp.semanticId}" data-debug-semantic-id="${clamp.debugSemanticId}" data-expression-node-id="${clamp.expressionNodeId}" data-line="${clamp.line}" tabindex="0">
      <div class="node-ordinal">04</div><div class="node-content"><div class="node-kicker">ASSIGN · PURE ENGINE CALL</div><h3><span class="local-name">${escapeHtml(clamp.target)}</span> <b>=</b> ${escapeHtml(clamp.title)}</h3>
      <div class="argument-table">${clamp.arguments.map(argument => `<span>${escapeHtml(argument.label)}</span><strong>${expressionMarkup(argument.value, argument.refs)}</strong>`).join('')}</div></div>
      <button class="breakpoint-toggle active" type="button" aria-label="切换断点"></button>
    </article>
    <div class="flow-connector"><span>then</span></div>
    <article class="flow-node return-node" data-node-id="return" data-semantic-id="${returned.semanticId}" data-debug-semantic-id="${returned.debugSemanticId}" data-line="${returned.line}" tabindex="0">
      <div class="node-ordinal">05</div><div class="node-content"><div class="node-kicker">RETURN</div><h3><span class="local-name">${expressionMarkup(returned.expression, returned.declarationRefs)}</span> <em>${escapeHtml(returned.returnType)}</em></h3></div>
      <button class="breakpoint-toggle" type="button" aria-label="切换断点"></button>
    </article>`;
  bindBehaviorInteractions(flowCanvas);
}

function renderAiBehavior() {
  if (!state.graphArtifactLoaded) return;
  const { entry, scale, criticalBranch, criticalScale, clamp, return: returned } = state.nodes;
  const parameters = entry.parameters?.map(parameter => `${parameter.name}: ${parameter.type}`).join(', ') || 'incomingDamage: int, critical: bool';
  document.getElementById('aiBehavior').textContent = `function ${entry.title}(${parameters}) → ${entry.returnType || 'float'}\n  let ${scale.localName} = ${scale.expression}\n  when ${criticalBranch.condition}\n    ${criticalScale.target} ${criticalScale.operator} ${criticalScale.operand}\n  ${clamp.target} = ${clamp.expression}\n  return ${returned.expression}`;
}

function csharpLineMarkup(line) {
  const tokenPattern = /("(?:\\.|[^"\\])*"|\/\/.*|\b(?:using|namespace|public|private|protected|internal|sealed|static|class|enum|get|set|if|else|return|float|int|bool|string|true|false|void)\b|\b\d+(?:\.\d+)?[fF]?\b|\b(?:ScriptAsset|Expose|Visual)\b)/g;
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
  const stateByLine = new Map();
  state.declarations.forEach(item => {
    for (let line = item.sourceStartLine; line <= item.sourceEndLine; line += 1) stateByLine.set(line, item);
  });
  const lines = state.sourceText.replace(/\r\n/g, '\n').replace(/\n$/, '').split('\n');
  const code = document.querySelector('#codeEditor code');
  code.innerHTML = lines.map((line, index) => {
    const lineNumber = index + 1;
    const declaration = stateByLine.get(lineNumber);
    const classes = declaration
      ? ` class="state-line ${lineNumber === declaration.sourceStartLine ? 'state-attribute-line' : 'state-property-line'}" data-state-id="${declaration.id}"`
      : '';
    return `<span data-line="${lineNumber}"${classes}><b>${lineNumber}</b>${csharpLineMarkup(line)}</span>`;
  }).join('\n');
  document.querySelector('.code-heading > div').innerHTML = `<span class="language-dot"></span>${escapeHtml(state.sourcePath.split('/').at(-1))} <em>CANONICAL TEXT</em>`;
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

function typeKindFor(type) {
  return ({ bool: 'boolean', int: 'integer', float: 'floatingPoint', string: 'string' })[type];
}

function metadataTypeFor(type) {
  return ({ bool: 'System.Boolean', int: 'System.Int32', float: 'System.Single', string: 'System.String' })[type];
}

function typedDefault(item) {
  const text = item.defaultValue.trim();
  if (item.type === 'bool') {
    if (!/^(true|false)$/i.test(text)) throw new Error(`${item.name} 的 bool 默认值必须是 true 或 false`);
    return text.toLowerCase() === 'true';
  }
  if (item.type === 'int') {
    const value = Number(text);
    if (!/^[+-]?\d+$/.test(text) || !Number.isSafeInteger(value)) throw new Error(`${item.name} 的 int 默认值无效`);
    return value;
  }
  if (item.type === 'float') {
    const value = Number(text);
    if (text.length === 0 || !Number.isFinite(value)) throw new Error(`${item.name} 的 float 默认值无效`);
    return value;
  }
  return item.defaultValue;
}

function exposureContract(item) {
  return {
    instanceEditable: item.editable,
    saveGame: item.save,
    replicated: item.replicated,
    ...(item.category ? { category: item.category } : {}),
  };
}

function replacementContract(item) {
  return {
    name: item.name,
    typeName: item.type,
    typeKind: typeKindFor(item.type),
    defaultValue: typedDefault(item),
    exposure: exposureContract(item),
  };
}

function declarationChanged(item) {
  if (!item.original) return true;
  const current = {
    name: item.name,
    typeMetadataName: metadataTypeFor(item.type),
    typeKind: typeKindFor(item.type),
    defaultValue: typedDefault(item),
    exposure: exposureContract(item),
  };
  return JSON.stringify(current) !== JSON.stringify(item.original);
}

function buildDeclarationPatch(includeAiSuggestion = false) {
  const declarations = state.declarations.map(item => ({ ...item }));
  if (includeAiSuggestion) {
    const maxDamage = declarations.find(item => item.name === 'MaxDamage');
    if (maxDamage) {
      maxDamage.editable = true;
      maxDamage.category ||= 'Damage';
    }
  }

  const operations = declarations.flatMap(item => {
    if (!item.original) {
      return [{ operation: 'addState', targetAssetId: state.assetId, state: replacementContract(item) }];
    }
    if (!declarationChanged(item)) return [];
    return [{
      operation: 'updateState',
      targetStateId: item.semanticId,
      expected: item.original,
      replacement: replacementContract(item),
    }];
  }).concat(state.removedDeclarations.map(item => ({
    operation: 'removeState',
    targetStateId: item.semanticId,
    expected: item.original,
  })));
  return {
    schemaVersion: 'unifiedscript.declaration-patch/v0.1',
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
  state.previewPatch = null;
  applyButton.disabled = true;
  if (!state.artifactLoaded) {
    summary.textContent = '真实 Declaration IR 未加载，不能生成带编译前置条件的补丁';
    json.textContent = '';
  } else {
    try {
      const patch = buildDeclarationPatch(true);
      summary.textContent = `${patch.operations.length} 个可由 CLI 直接应用的类型化声明操作`;
      json.textContent = JSON.stringify(patch, null, 2);
      state.previewPatch = patch;
      applyButton.disabled = !state.authoringConnected || patch.operations.length === 0;
    } catch (error) {
      summary.textContent = error.message;
      json.textContent = '';
    }
  }
  preview.classList.add('visible');
}

function updateStateCodeLine(item) {
  const lines = [...document.querySelectorAll(`.state-line[data-state-id="${item.id}"]`)];
  if (lines.length !== 2) return;
  const [attributeLine, propertyLine] = lines;
  const attributeNumber = attributeLine.dataset.line;
  const propertyNumber = propertyLine.dataset.line;
  const attribute = exposureText(item).trim();
  const property = `    public ${item.type} ${item.name} { get; set; } = ${normalizedDefault(item)};`;
  attributeLine.innerHTML = `<b>${attributeNumber}</b>${csharpLineMarkup(attribute ? `    ${attribute}` : '')}`;
  propertyLine.innerHTML = `<b>${propertyNumber}</b>${csharpLineMarkup(property)}`;
}

function insertStateCodeLine(item) {
  const code = document.querySelector('#codeEditor code');
  const visualLine = [...code.querySelectorAll('[data-line]')].find(line => line.textContent.includes('[Visual]'));
  if (!visualLine) return;
  const nextNumber = Number(visualLine.dataset.line);
  let following = visualLine;
  while (following) {
    const currentNumber = Number(following.dataset.line);
    if (Number.isFinite(currentNumber)) {
      following.dataset.line = String(currentNumber + 2);
      const number = following.querySelector('b');
      if (number) number.textContent = String(currentNumber + 2);
    }
    following = following.nextElementSibling;
  }
  Object.values(state.nodes).forEach(node => {
    if (node.line >= nextNumber) node.line += 2;
  });
  item.sourceStartLine = nextNumber;
  item.sourceEndLine = nextNumber + 1;
  const attributeLine = document.createElement('span');
  attributeLine.className = 'state-line state-attribute-line';
  attributeLine.dataset.line = String(nextNumber);
  attributeLine.dataset.stateId = item.id;
  const propertyLine = document.createElement('span');
  propertyLine.className = 'state-line state-property-line';
  propertyLine.dataset.line = String(nextNumber + 1);
  propertyLine.dataset.stateId = item.id;
  code.insertBefore(attributeLine, visualLine);
  code.insertBefore(document.createTextNode('\n'), visualLine);
  code.insertBefore(propertyLine, visualLine);
  code.insertBefore(document.createTextNode('\n'), visualLine);
  updateStateCodeLine(item);
  if (state.graphArtifactLoaded) renderBehaviorGraph();
  markGraphDirty();
}

function markGraphDirty() {
  state.graphDirty = true;
  document.querySelector('.sync-status strong').textContent = 'PATCH PENDING';
  document.querySelector('.target-label').textContent = 'GRAPH BASELINE · DECLARATION PATCH PENDING';
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

function restoreBreakpoints(debugNodeIds) {
  if (!debugNodeIds.length) return;
  document.querySelectorAll('.breakpoint-toggle.active').forEach(button => button.classList.remove('active'));
  document.querySelectorAll('[data-debug-semantic-id]').forEach(element => {
    if (debugNodeIds.includes(element.dataset.debugSemanticId)) {
      element.querySelector('.breakpoint-toggle:not(:disabled)')?.classList.add('active');
    }
  });
}

async function authoringRequest(path, patch) {
  const response = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(patch),
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    const diagnostics = payload.diagnostics || [];
    const detail = diagnostics[0]?.message || payload.error?.message || `Authoring request returned ${response.status}`;
    const error = new Error(detail);
    error.code = payload.error?.code;
    error.diagnostics = diagnostics;
    throw error;
  }
  if (payload.schemaVersion !== 'unifiedscript.authoring-workspace/v0.1') {
    throw new Error('Unsupported authoring workspace response');
  }
  return payload;
}

async function saveDeclarationChanges(patch = null) {
  if (!state.authoringConnected) {
    showToast('Authoring host unavailable', 'Start this workbench with debug-serve to write source changes');
    return;
  }

  try {
    const nextPatch = patch || buildDeclarationPatch();
    if (nextPatch.operations.length === 0) {
      showToast('Nothing to save', `Workspace revision ${state.authoringRevision} is already synchronized`);
      return;
    }

    setAuthoringBusy(true);
    const workspace = await authoringRequest('/api/authoring/declaration-patch', nextPatch);
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.sourceText, workspace);
    showToast(`Saved revision ${workspace.revision}`, `CoreCLR generation ${workspace.generation.number} is live`);
  } catch (error) {
    document.querySelector('.sync-status strong').textContent = error.code === 'UAW1002' ? 'EXTERNAL CONFLICT' : 'PATCH REJECTED';
    showToast(error.code || 'Save failed', error.message);
    setAuthoringBusy(false);
  }
}

async function saveSemanticPatch(patch) {
  if (!state.authoringConnected) {
    showToast('Authoring host unavailable', 'Semantic body patches require debug-serve');
    return;
  }

  try {
    setAuthoringBusy(true);
    const workspace = await authoringRequest('/api/authoring/semantic-patch', patch);
    applyWorkspaceArtifacts(workspace.scriptAssets, workspace.graph, workspace.sourceText, workspace);
    showToast(`Behavior saved · R${workspace.revision}`, `CoreCLR generation ${workspace.generation.number} reloaded`);
  } catch (error) {
    document.querySelector('.sync-status strong').textContent = error.code === 'UAW1002' ? 'EXTERNAL CONFLICT' : 'PATCH REJECTED';
    showToast(error.code || 'Behavior patch failed', error.message);
    setAuthoringBusy(false);
  }
}

function renameBehaviorReference(declarationId, previousName, nextName) {
  if (!state.graphArtifactLoaded || !previousName || previousName === nextName) return;
  const escapedName = previousName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const pattern = new RegExp(`\\b${escapedName}\\b`, 'g');
  Object.values(state.nodes).forEach(node => {
    const ownsReference = node.declarationRefs?.some(reference => reference.declarationId === declarationId)
      || node.arguments?.some(argument => argument.refs?.some(reference => reference.declarationId === declarationId));
    if (!ownsReference) return;
    ['title', 'detail', 'expression', 'condition', 'target', 'operand'].forEach(key => {
      if (typeof node[key] === 'string') node[key] = node[key].replace(pattern, nextName);
    });
    node.declarationRefs?.forEach(reference => {
      if (reference.declarationId === declarationId) reference.name = nextName;
    });
    node.arguments?.forEach(argument => {
      argument.value = argument.value.replace(pattern, nextName);
      argument.refs?.forEach(reference => {
        if (reference.declarationId === declarationId) reference.name = nextName;
      });
    });
  });
  renderBehaviorGraph();
  renderAiBehavior();
}

function showCompactInspector() {
  if (window.innerWidth <= 900) document.querySelector('.inspector-panel').classList.add('compact-visible');
}

function selectDeclaration(id, revealInspector = true) {
  const item = state.declarations.find(candidate => candidate.id === id);
  if (!item) return;
  state.selected = { kind: 'state', id };
  document.querySelectorAll('.declaration-item').forEach(element => element.classList.toggle('selected', element.dataset.id === id));
  document.querySelectorAll('.flow-node, .branch-region').forEach(element => element.classList.remove('selected'));
  document.querySelectorAll('.state-line').forEach(line => line.classList.toggle('state-highlight', line.dataset.stateId === id));
  renderStateInspector(item);
  if (revealInspector) showCompactInspector();
}

function renderStateInspector(item) {
  inspectorTitle.textContent = item.name;
  inspectorBody.innerHTML = `
    <div class="selection-kind"><span class="selection-icon">S</span><div><strong>SCRIPT STATE</strong><small>Declaration · persistent identity</small></div></div>
    <section class="inspector-section">
      <h3>DECLARATION</h3>
      <label class="field-label">Name<input id="propertyName" type="text" value="${escapeHtml(item.name)}"></label>
      <div class="field-grid">
        <label class="field-label">Type<select id="propertyType">${['int','float','bool','string'].map(type => `<option${type === item.type ? ' selected' : ''}>${type}</option>`).join('')}</select></label>
        <label class="field-label">Default<input id="propertyDefault" type="text" value="${escapeHtml(item.defaultValue)}"></label>
      </div>
      <label class="field-label">Category<input id="propertyCategory" type="text" value="${escapeHtml(item.category || '')}" placeholder="Optional"></label>
    </section>
    <section class="inspector-section">
      <h3>ENGINE EXPOSURE</h3>
      ${toggleRow('toggleEditable', 'Instance Editable', 'UE Details panel', item.editable)}
      ${toggleRow('toggleSave', 'Save Game', 'Persistent state', item.save)}
      ${toggleRow('toggleReplicated', 'Replicated', 'Network contract', item.replicated)}
    </section>
    <section class="inspector-section source-contract">
      <div class="section-title-row"><h3>CANONICAL TEXT</h3><span>AI READABLE</span></div>
      <pre id="canonicalDeclaration">${escapeHtml(canonicalDeclaration(item))}</pre>
    </section>
    <section class="inspector-section identity-section">
      <h3>SEMANTIC IDENTITY</h3>
      <button id="semanticId" class="identity-value" type="button" title="复制语义 ID">${semanticShort(item.semanticId)}</button>
      <div class="identity-facts"><span>TEXT <b>✓</b></span><span>VISUAL <b>✓</b></span><span>AI <b>✓</b></span></div>
    </section>
    <button id="removeStateButton" class="inspector-danger" type="button">REMOVE STATE</button>
    <div class="prototype-warning"><strong>${state.artifactLoaded ? 'LIVE DECLARATION IR' : 'EMBEDDED FALLBACK'}</strong><p>${state.artifactLoaded ? '名称、类型、默认值、暴露策略和 Semantic ID 来自编译器生成的严格 asset.json。' : '本地服务器未加载真实产物，当前显示内嵌演示数据。'}</p></div>`;
  bindStateInspector(item);
}

function toggleRow(id, title, subtitle, checked) {
  return `<label class="toggle-row"><span><strong>${title}</strong><small>${subtitle}</small></span><input id="${id}" type="checkbox"${checked ? ' checked' : ''}><i></i></label>`;
}

function bindStateInspector(item) {
  const name = document.getElementById('propertyName');
  const type = document.getElementById('propertyType');
  const defaultValue = document.getElementById('propertyDefault');
  const category = document.getElementById('propertyCategory');
  const editable = document.getElementById('toggleEditable');
  const save = document.getElementById('toggleSave');
  const replicated = document.getElementById('toggleReplicated');
  const update = () => {
    const previousName = item.name;
    item.name = name.value || item.name;
    item.type = type.value;
    item.defaultValue = defaultValue.value;
    item.category = category.value.trim();
    item.editable = editable.checked;
    item.save = save.checked;
    item.replicated = replicated.checked;
    document.getElementById('canonicalDeclaration').textContent = canonicalDeclaration(item);
    document.getElementById('inspectorTitle').textContent = item.name;
    const row = document.querySelector(`.declaration-item[data-id="${item.id}"]`);
    if (row) {
      row.querySelector('strong').textContent = item.name;
      row.querySelector('small').textContent = `${item.type} · ${item.defaultValue}`;
    }
    updateStateCodeLine(item);
    renderAiDeclarations();
    renameBehaviorReference(item.semanticId, previousName, item.name);
    markGraphDirty();
  };
  [name, type, defaultValue, category, editable, save, replicated].forEach(control => control.addEventListener('input', update));
  document.getElementById('semanticId').addEventListener('click', () => copyText(item.semanticId, 'Semantic ID copied'));
  document.getElementById('removeStateButton').addEventListener('click', () => removeDeclaration(item));
}

function removeDeclaration(item) {
  const index = state.declarations.indexOf(item);
  if (index < 0) return;
  if (item.original) state.removedDeclarations.push(item);
  state.declarations.splice(index, 1);
  document.querySelectorAll(`.state-line[data-state-id="${item.id}"]`).forEach(line => line.remove());
  renderDeclarationList();
  renderAiDeclarations();
  markGraphDirty();
  const next = state.declarations[Math.min(index, state.declarations.length - 1)];
  if (next) selectDeclaration(next.id, false);
  else selectNode('entry', false);
  showToast(`${item.name} marked for removal`, 'Commit Patch will validate all behavior references');
}

function selectNode(id, revealInspector = true) {
  const node = state.nodes[id];
  if (!node) return;
  state.selected = { kind: 'node', id };
  document.querySelectorAll('.declaration-item').forEach(element => element.classList.remove('selected'));
  document.querySelectorAll('.state-line').forEach(line => line.classList.remove('state-highlight'));
  document.querySelectorAll('.flow-node, .branch-region').forEach(element => element.classList.toggle('selected', element.dataset.nodeId === id));
  document.querySelectorAll('#codeEditor [data-line]').forEach(line => line.classList.toggle('highlighted', Number(line.dataset.line) === node.line));
  document.getElementById('selectedLine').textContent = String(node.line);
  renderNodeInspector(node);
  if (revealInspector) showCompactInspector();
}

function renderNodeInspector(node) {
  inspectorTitle.textContent = node.title;
  const behaviorEditor = node.minimumLiteralId ? `
    <section class="inspector-section behavior-editor">
      <div class="section-title-row"><h3>BEHAVIOR INPUT</h3><span>SEMANTIC PATCH</span></div>
      <label class="field-label">Clamp minimum<input id="behaviorLiteralValue" type="text" value="${escapeHtml(node.minimumValue)}" spellcheck="false"></label>
      <button id="saveBehaviorButton" class="button primary inspector-save" type="button">SAVE EXPRESSION</button>
      <small>Writes a replaceLiteral operation against ${semanticShort(node.minimumLiteralId)} and reloads CoreCLR.</small>
    </section>` : '';
  inspectorBody.innerHTML = `
    <div class="selection-kind"><span class="selection-icon">N</span><div><strong>${node.kind}</strong><small>Executable semantic node</small></div></div>
    <section class="inspector-section"><h3>INTENT</h3><div class="node-inspector-title">${escapeHtml(node.title)}</div><p class="node-inspector-copy">${escapeHtml(node.detail)}</p></section>
    <section class="inspector-section"><h3>SOURCE LOCATION</h3><div class="fact-grid"><span>${escapeHtml(state.sourcePath.split('/').at(-1))}</span><strong>Line ${node.line}</strong></div><div class="fact-grid"><span>Graph representation</span><strong>compact projection</strong></div>${node.expressionNodeId ? `<div class="fact-grid"><span>Inline expression</span><strong>${semanticShort(node.expressionNodeId)}</strong></div>` : ''}</section>
    ${behaviorEditor}
    <section class="inspector-section source-contract"><div class="section-title-row"><h3>AI OUTLINE</h3><span>SEMANTIC</span></div><pre>${escapeHtml(node.detail)}</pre></section>
    <section class="inspector-section identity-section"><h3>GRAPH IDENTITY</h3><button id="semanticId" class="identity-value" type="button">${semanticShort(node.semanticId)}</button>${node.debugSemanticId ? `<h3 class="debug-identity-heading">DEBUG STATEMENT</h3><button id="debugSemanticId" class="identity-value debug-identity" type="button">${semanticShort(node.debugSemanticId)}</button>` : ''}<div class="identity-facts"><span>TEXT <b>✓</b></span><span>GRAPH <b>✓</b></span><span>DEBUG <b>${node.debugSemanticId ? '✓' : '—'}</b></span></div></section>
    <div class="prototype-warning"><strong>REAL GRAPH PROJECTION</strong><p>卡片直接绑定 Graph Semantic ID；字面量和普通运算作为可追溯的内联表达式，不制造逐行节点。</p></div>`;
  document.getElementById('semanticId').addEventListener('click', () => copyText(node.semanticId, 'Semantic ID copied'));
  document.getElementById('debugSemanticId')?.addEventListener('click', () => copyText(node.debugSemanticId, 'Debug statement ID copied'));
  document.getElementById('saveBehaviorButton')?.addEventListener('click', () => {
    const newValueText = document.getElementById('behaviorLiteralValue').value.trim();
    if (!newValueText || newValueText === node.minimumValue) {
      showToast('No expression change', `Clamp minimum is already ${node.minimumValue}`);
      return;
    }
    saveSemanticPatch({
      schemaVersion: 'unifiedscript.patch/v0.19',
      sourceCompilationId: state.sourceCompilationId,
      semanticConfigurationId: state.semanticConfigurationId,
      operations: [{
        operation: 'replaceLiteral',
        targetNodeId: node.minimumLiteralId,
        expectedValueText: node.minimumValue,
        newValueText,
      }],
    });
  });
}

function renderAiDeclarations() {
  document.getElementById('aiDeclarations').textContent = state.declarations.map(item => {
    const exposure = [item.editable && 'instanceEditable', item.save && 'saveGame', item.replicated && 'replicated'].filter(Boolean);
    return `state ${item.name}: ${item.type} = ${item.defaultValue}${exposure.length ? `\n  expose: ${exposure.join(', ')}` : ''}`;
  }).join('\n');
}

function bindBehaviorInteractions(root = document) {
  root.querySelectorAll('.flow-node, .branch-region').forEach(element => {
    if (element.dataset.interactionBound === 'true') return;
    element.dataset.interactionBound = 'true';
    element.addEventListener('click', event => {
      if (event.target.closest('.breakpoint-toggle')) return;
      if (event.target.closest('[data-node-id]') !== element) return;
      selectNode(element.dataset.nodeId);
    });
    element.addEventListener('keydown', event => {
      if (event.target !== element || (event.key !== 'Enter' && event.key !== ' ')) return;
      event.preventDefault();
      selectNode(element.dataset.nodeId);
    });
  });
  root.querySelectorAll('.breakpoint-toggle').forEach(button => {
    if (button.dataset.interactionBound === 'true') return;
    button.dataset.interactionBound = 'true';
    button.addEventListener('click', event => {
      event.stopPropagation();
      button.classList.toggle('active');
    });
  });
}

function addDeclaration(item) {
  state.declarations.push(item);
  const list = document.getElementById('stateList');
  const button = document.createElement('button');
  button.className = 'declaration-item';
  button.type = 'button';
  button.dataset.kind = 'state';
  button.dataset.id = item.id;
  button.innerHTML = `<span class="declaration-glyph state-glyph">S</span><span class="declaration-copy"><strong>${escapeHtml(item.name)}</strong><small>${item.type} · ${escapeHtml(item.defaultValue)}</small></span>${item.editable ? '<span class="exposure-dot" title="Instance Editable"></span>' : item.save ? '<span class="save-dot" title="SaveGame"></span>' : '<span></span>'}`;
  button.addEventListener('click', () => selectDeclaration(item.id));
  list.appendChild(button);
  insertStateCodeLine(item);
  document.getElementById('stateCount').textContent = String(state.declarations.length);
  renderAiDeclarations();
  selectDeclaration(item.id);
  showToast('Declaration created', 'Text · Visual · AI views updated');
}

function updateNewVariablePreview() {
  const item = {
    name: toPascalCase(document.getElementById('newVariableName').value),
    type: document.getElementById('newVariableType').value,
    defaultValue: document.getElementById('newVariableDefault').value,
    editable: document.getElementById('newVariableEditable').checked,
    save: document.getElementById('newVariableSave').checked,
    replicated: document.getElementById('newVariableReplicated').checked,
  };
  document.getElementById('newVariablePreview').textContent = canonicalDeclaration(item).replace('\n', ' ');
}

function changeView(view) {
  state.view = view;
  document.body.dataset.view = view;
  editorBody.className = `editor-body view-${view}`;
  document.querySelectorAll('.view-button').forEach(button => button.classList.toggle('active', button.dataset.view === view));
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
    assertArtifact(health.schemaVersion === 'unifiedscript.debug-health/v0.1', 'Unsupported debug health protocol');
    assertArtifact(health.status === 'ready', 'CoreCLR debug bridge is not ready');
    assertArtifact(health.sourceCompilationId === state.sourceCompilationId, 'Debug/source compilation identity mismatch');
    assertArtifact(health.semanticConfigurationId === state.semanticConfigurationId, 'Debug/semantic configuration identity mismatch');
    state.debugConnected = true;
    state.debugHealth = health;
    document.querySelector('.sync-status strong').textContent = state.authoringConnected
      ? `SYNCED · R${state.authoringRevision} · CLR`
      : 'IR + GRAPH + CLR';
    document.querySelector('.target-label').textContent = `CLR ${semanticShort(health.generationId)} · GRAPH v0.12`;
  } catch (error) {
    state.debugConnected = false;
    state.debugHealth = null;
    console.info('CoreCLR debug bridge is offline; run debug-serve to enable live debugging.', error.message);
  }
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
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(payload.error?.message || `Debug request returned ${response.status}`);
  if (payload.schemaVersion !== 'unifiedscript.debug-session/v0.1') throw new Error('Unsupported debug session response');
  return payload;
}

function activeBreakpointNodeIds() {
  return [...document.querySelectorAll('.breakpoint-toggle.active:not(:disabled)')]
    .map(button => button.closest('[data-debug-semantic-id]')?.dataset.debugSemanticId)
    .filter((value, index, values) => value && values.indexOf(value) === index);
}

function renderLiveDebugPause(response) {
  clearDebugVisuals(false);
  state.debugSession = response;
  const pausedElement = [...document.querySelectorAll('[data-debug-semantic-id]')]
    .find(element => element.dataset.debugSemanticId === response.pause.nodeId);
  if (!pausedElement) throw new Error(`Paused statement ${response.pause.nodeId} has no visual card`);
  pausedElement.classList.add('executing', 'paused');
  pausedElement.scrollIntoView({ behavior: 'smooth', block: 'center' });
  selectNode(pausedElement.dataset.nodeId, false);

  const frame = response.frames[0];
  const reason = response.pause.reason === 'breakpoint' ? 'BREAKPOINT' : response.pause.reason.toUpperCase();
  document.getElementById('debugReason').textContent = `PAUSED · ${reason}`;
  document.getElementById('debugLocation').textContent = `${frame.methodMetadataName} · IL ${frame.ilOffset} · generation ${response.generation.number}`;
  document.getElementById('debugLocals').innerHTML = response.locals.map(local =>
    `<span title="${escapeHtml(local.typeMetadataName)}">${escapeHtml(local.name)} <b>${escapeHtml(local.display)}</b></span>`).join('');
  debugStrip.classList.add('visible');
  document.querySelector('.target-label').textContent = `LIVE CLR · ${semanticShort(response.pause.pauseId)}`;
  setDebugBusy(false);
}

function renderDebugCompletion(response) {
  state.debugSession = null;
  clearDebugVisuals();
  const result = response.result;
  const returned = result?.returnValue?.json ?? '<void>';
  document.querySelector('.sync-status strong').textContent = state.graphDirty
    ? 'PATCH PENDING'
    : state.authoringConnected
      ? `SYNCED · R${state.authoringRevision} · CLR`
      : 'IR + GRAPH + CLR';
  document.querySelector('.target-label').textContent = `CLR GENERATION ${response.generation.number} · COMPLETED`;
  setDebugBusy(false);
  showToast(`Execution returned ${returned}`, 'Live CoreCLR debug session completed');
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

function applyDebugResponse(response) {
  if (response.state === 'paused') renderLiveDebugPause(response);
  else if (response.state === 'completed') renderDebugCompletion(response);
  else throw new Error(`Unknown debug session state ${response.state}`);
}

async function runDebug() {
  clearDebugVisuals();
  document.querySelector('.inspector-panel').classList.remove('compact-visible');
  changeView('structure');
  if (!state.debugConnected) {
    await connectDebugBridge();
    if (!state.debugConnected) {
      renderDebugError(new Error('Start the workbench with `unifiedscript debug-serve` to enable live CoreCLR debugging.'));
      return;
    }
  }

  const breakpointNodeIds = activeBreakpointNodeIds();
  if (breakpointNodeIds.length === 0) {
    renderDebugError(new Error('Enable at least one statement breakpoint before running.'));
    return;
  }

  setDebugBusy(true);
  try {
    const response = await debugRequest('/api/debug/start', {
      methodId: state.graphFunction.id,
      arguments: [12, true],
      receiver: {
        assetId: state.assetId,
        stateOverrides: state.declarations
          .filter(item => !item.semanticId.startsWith('pending_'))
          .map(item => ({ stateId: item.semanticId, value: typedDefault(item) })),
      },
      breakpointNodeIds,
    });
    applyDebugResponse(response);
  } catch (error) {
    renderDebugError(error);
  }
}

async function resumeDebug(stepOver) {
  if (state.debugSession?.state !== 'paused') return;
  setDebugBusy(true);
  clearDebugVisuals(false);
  try {
    const response = await debugRequest(stepOver ? '/api/debug/step-over' : '/api/debug/continue');
    applyDebugResponse(response);
  } catch (error) {
    renderDebugError(error);
  }
}

const continueDebug = () => resumeDebug(false);
const stepOverDebug = () => resumeDebug(true);

function showToast(title, subtitle) {
  toast.querySelector('strong').textContent = title;
  toast.querySelector('small').textContent = subtitle;
  toast.classList.add('visible');
  setTimeout(() => toast.classList.remove('visible'), 2600);
}

async function copyText(value, confirmation = 'Copied') {
  try { await navigator.clipboard.writeText(value); } catch { /* file/local contexts may deny clipboard */ }
  showToast(confirmation, value.length > 50 ? `${value.slice(0, 47)}…` : value);
}

document.querySelectorAll('.declaration-item[data-kind="state"]').forEach(button => button.addEventListener('click', () => selectDeclaration(button.dataset.id)));
document.querySelector('.function-item').addEventListener('click', () => selectNode('entry'));
bindBehaviorInteractions();
document.querySelectorAll('.view-button').forEach(button => button.addEventListener('click', () => changeView(button.dataset.view)));
document.getElementById('zoomInButton').addEventListener('click', () => setZoom(state.zoom + .1));
document.getElementById('zoomOutButton').addEventListener('click', () => setZoom(state.zoom - .1));
document.getElementById('fitButton').addEventListener('click', () => setZoom(.9));
document.getElementById('runButton').addEventListener('click', runDebug);
document.getElementById('continueButton').addEventListener('click', continueDebug);
document.getElementById('stepOverButton').addEventListener('click', stepOverDebug);
document.getElementById('closeInspectorButton').addEventListener('click', () => document.querySelector('.inspector-panel').classList.remove('compact-visible'));

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
document.getElementById('applyPreviewPatchButton').addEventListener('click', () => {
  if (state.previewPatch) saveDeclarationChanges(state.previewPatch);
});
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
  const name = toPascalCase(nameInput.value);
  const item = {
    id: `${name.charAt(0).toLowerCase()}${name.slice(1)}-${Date.now()}`,
    name,
    type: document.getElementById('newVariableType').value,
    defaultValue: document.getElementById('newVariableDefault').value,
    editable: document.getElementById('newVariableEditable').checked,
    save: document.getElementById('newVariableSave').checked,
    replicated: document.getElementById('newVariableReplicated').checked,
    semanticId: `pending_${crypto.getRandomValues(new Uint32Array(2)).reduce((value, part) => value + part.toString(16).padStart(8, '0'), '')}`,
  };
  variableDialog.close();
  addDeclaration(item);
});
['newVariableName','newVariableType','newVariableDefault','newVariableEditable','newVariableSave','newVariableReplicated'].forEach(id => document.getElementById(id).addEventListener('input', updateNewVariablePreview));

declarationSearch.addEventListener('input', () => {
  const query = declarationSearch.value.trim().toLowerCase();
  document.querySelectorAll('.declaration-item').forEach(item => {
    item.hidden = query.length > 0 && !item.textContent.toLowerCase().includes(query);
  });
});

document.addEventListener('keydown', event => {
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 's') {
    event.preventDefault();
    if (state.graphDirty && !state.authoringBusy) saveDeclarationChanges();
  }
});
document.addEventListener('keydown', event => {
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault();
    declarationSearch.focus();
  }
  if (event.key === 'Escape' && aiDrawer.classList.contains('visible')) document.getElementById('closeAiDrawer').click();
});
document.getElementById('copyCodeButton').addEventListener('click', () => copyText(document.getElementById('codeEditor').innerText, 'Canonical text copied'));

selectDeclaration('baseDamage', false);
loadWorkspaceArtifacts();

const state = {
  view: 'structure',
  zoom: 1,
  selected: { kind: 'state', id: 'baseDamage' },
  declarations: [
    { id: 'baseDamage', name: 'BaseDamage', type: 'int', defaultValue: '20', editable: true, save: false, replicated: false, semanticId: 'us_8ad1c44ff31210aaec986daa117f7f2e' },
    { id: 'criticalMultiplier', name: 'CriticalMultiplier', type: 'float', defaultValue: '2.0', editable: true, save: false, replicated: false, semanticId: 'us_a738a8a6775e38c5a3fac2f1470b8c22' },
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
  debugTimers: [],
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

function exposureText(item) {
  const values = [];
  if (item.editable) values.push('InstanceEditable = true');
  if (item.save) values.push('SaveGame = true');
  if (item.replicated) values.push('Replicated = true');
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

function codeDeclarationMarkup(item) {
  const flags = [];
  if (item.editable) flags.push('InstanceEditable = <i>true</i>');
  if (item.save) flags.push('SaveGame = <i>true</i>');
  if (item.replicated) flags.push('Replicated = <i>true</i>');
  const attribute = flags.length ? `[<u>Expose</u>(${flags.join(', ')})] ` : '';
  return `    ${attribute}<i>public ${escapeHtml(item.type)}</i> ${escapeHtml(item.name)} { <i>get</i>; <i>set</i>; } = <n>${escapeHtml(normalizedDefault(item))}</n>;`;
}

function updateStateCodeLine(item) {
  const line = document.querySelector(`.state-line[data-state-id="${item.id}"]`);
  if (!line) return;
  const number = line.querySelector('b')?.textContent || line.dataset.line || '';
  line.innerHTML = `<b>${number}</b>${codeDeclarationMarkup(item)}`;
}

function insertStateCodeLine(item) {
  const stateLines = [...document.querySelectorAll('.state-line')];
  const lastStateLine = stateLines.at(-1);
  if (!lastStateLine) return;
  const nextNumber = Number(lastStateLine.dataset.line) + 1;
  let following = lastStateLine.nextElementSibling;
  while (following) {
    const currentNumber = Number(following.dataset.line);
    if (Number.isFinite(currentNumber)) {
      following.dataset.line = String(currentNumber + 1);
      const number = following.querySelector('b');
      if (number) number.textContent = String(currentNumber + 1);
    }
    following = following.nextElementSibling;
  }
  Object.values(state.nodes).forEach(node => {
    if (node.line >= nextNumber) node.line += 1;
  });
  const line = document.createElement('span');
  line.className = 'state-line';
  line.dataset.line = String(nextNumber);
  line.dataset.stateId = item.id;
  line.innerHTML = `<b>${nextNumber}</b>${codeDeclarationMarkup(item)}`;
  lastStateLine.after(line);
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
    <div class="prototype-warning"><strong>vNEXT TARGET</strong><p>此声明交互是产品模型原型；当前编译器尚未把类型级状态纳入统一 Declaration IR。</p></div>`;
  bindStateInspector(item);
}

function toggleRow(id, title, subtitle, checked) {
  return `<label class="toggle-row"><span><strong>${title}</strong><small>${subtitle}</small></span><input id="${id}" type="checkbox"${checked ? ' checked' : ''}><i></i></label>`;
}

function bindStateInspector(item) {
  const name = document.getElementById('propertyName');
  const type = document.getElementById('propertyType');
  const defaultValue = document.getElementById('propertyDefault');
  const editable = document.getElementById('toggleEditable');
  const save = document.getElementById('toggleSave');
  const replicated = document.getElementById('toggleReplicated');
  const update = () => {
    item.name = name.value || item.name;
    item.type = type.value;
    item.defaultValue = defaultValue.value;
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
  };
  [name, type, defaultValue, editable, save, replicated].forEach(control => control.addEventListener('input', update));
  document.getElementById('semanticId').addEventListener('click', () => copyText(item.semanticId, 'Semantic ID copied'));
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
  inspectorBody.innerHTML = `
    <div class="selection-kind"><span class="selection-icon">N</span><div><strong>${node.kind}</strong><small>Executable semantic node</small></div></div>
    <section class="inspector-section"><h3>INTENT</h3><div class="node-inspector-title">${escapeHtml(node.title)}</div><p class="node-inspector-copy">${escapeHtml(node.detail)}</p></section>
    <section class="inspector-section"><h3>SOURCE LOCATION</h3><div class="fact-grid"><span>DamagePolicy.cs</span><strong>Line ${node.line}</strong></div><div class="fact-grid"><span>Graph representation</span><strong>lossless</strong></div></section>
    <section class="inspector-section source-contract"><div class="section-title-row"><h3>AI OUTLINE</h3><span>SEMANTIC</span></div><pre>${escapeHtml(node.detail)}</pre></section>
    <section class="inspector-section identity-section"><h3>SEMANTIC IDENTITY</h3><button id="semanticId" class="identity-value" type="button">${semanticShort(node.semanticId)}</button><div class="identity-facts"><span>TEXT <b>✓</b></span><span>GRAPH <b>✓</b></span><span>DEBUG <b>✓</b></span></div></section>
    <div class="prototype-warning"><strong>NOT A LINE-NODE</strong><p>这个卡片表达一个语义动作或结构区域。字面量和普通运算保留为卡片内联表达式。</p></div>`;
  document.getElementById('semanticId').addEventListener('click', () => copyText(node.semanticId, 'Semantic ID copied'));
}

function renderAiDeclarations() {
  document.getElementById('aiDeclarations').textContent = state.declarations.map(item => {
    const exposure = [item.editable && 'instanceEditable', item.save && 'saveGame', item.replicated && 'replicated'].filter(Boolean);
    return `state ${item.name}: ${item.type} = ${item.defaultValue}${exposure.length ? `\n  expose: ${exposure.join(', ')}` : ''}`;
  }).join('\n');
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
  editorBody.className = `editor-body view-${view}`;
  document.querySelectorAll('.view-button').forEach(button => button.classList.toggle('active', button.dataset.view === view));
}

function setZoom(next) {
  state.zoom = Math.max(.75, Math.min(1.25, next));
  flowCanvas.style.setProperty('--zoom', state.zoom);
  zoomLabel.textContent = `${Math.round(state.zoom * 100)}%`;
}

function clearDebug() {
  state.debugTimers.forEach(clearTimeout);
  state.debugTimers = [];
  document.querySelectorAll('.executing, .paused').forEach(element => element.classList.remove('executing', 'paused'));
  debugStrip.classList.remove('visible');
}

function runDebug() {
  clearDebug();
  document.querySelector('.inspector-panel').classList.remove('compact-visible');
  changeView('structure');
  const sequence = ['entry', 'scale', 'criticalBranch', 'criticalScale', 'clamp'];
  sequence.forEach((id, index) => {
    state.debugTimers.push(setTimeout(() => {
      document.querySelectorAll('.executing').forEach(element => element.classList.remove('executing'));
      const element = document.querySelector(`[data-node-id="${id}"]`);
      element?.classList.add('executing');
      element?.scrollIntoView({ behavior: 'smooth', block: 'center' });
      if (id === 'clamp') {
        element?.classList.add('paused');
        debugStrip.classList.add('visible');
        selectNode('clamp', false);
      }
    }, 260 + index * 480));
  });
}

function continueDebug() {
  debugStrip.classList.remove('visible');
  const clamp = document.querySelector('[data-node-id="clamp"]');
  clamp?.classList.remove('paused', 'executing');
  const returnNode = document.querySelector('[data-node-id="return"]');
  returnNode?.classList.add('executing');
  setTimeout(() => {
    returnNode?.classList.remove('executing');
    showToast('Execution returned 480', 'CoreCLR debug experience simulation');
  }, 650);
}

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
document.querySelectorAll('.flow-node, .branch-region').forEach(element => {
  element.addEventListener('click', event => {
    if (event.target.closest('.breakpoint-toggle')) return;
    selectNode(element.dataset.nodeId);
  });
  element.addEventListener('keydown', event => {
    if (event.key === 'Enter' || event.key === ' ') selectNode(element.dataset.nodeId);
  });
});
document.querySelectorAll('.breakpoint-toggle').forEach(button => button.addEventListener('click', () => button.classList.toggle('active')));
document.querySelectorAll('.view-button').forEach(button => button.addEventListener('click', () => changeView(button.dataset.view)));
document.getElementById('zoomInButton').addEventListener('click', () => setZoom(state.zoom + .1));
document.getElementById('zoomOutButton').addEventListener('click', () => setZoom(state.zoom - .1));
document.getElementById('fitButton').addEventListener('click', () => setZoom(.9));
document.getElementById('runButton').addEventListener('click', runDebug);
document.getElementById('continueButton').addEventListener('click', continueDebug);
document.getElementById('closeInspectorButton').addEventListener('click', () => document.querySelector('.inspector-panel').classList.remove('compact-visible'));

document.getElementById('aiViewButton').addEventListener('click', () => {
  renderAiDeclarations();
  aiDrawer.classList.add('visible');
  aiDrawer.setAttribute('aria-hidden', 'false');
});
document.getElementById('closeAiDrawer').addEventListener('click', () => {
  aiDrawer.classList.remove('visible');
  aiDrawer.setAttribute('aria-hidden', 'true');
});
document.getElementById('previewPatchButton').addEventListener('click', () => document.getElementById('patchPreview').classList.add('visible'));

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
    semanticId: `us_${crypto.getRandomValues(new Uint32Array(4)).reduce((value, part) => value + part.toString(16).padStart(8, '0'), '')}`,
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
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault();
    declarationSearch.focus();
  }
  if (event.key === 'Escape' && aiDrawer.classList.contains('visible')) document.getElementById('closeAiDrawer').click();
});
document.getElementById('copyCodeButton').addEventListener('click', () => copyText(document.getElementById('codeEditor').innerText, 'Canonical text copied'));

selectDeclaration('baseDamage', false);

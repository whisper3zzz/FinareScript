import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import vm from 'node:vm';

const root = dirname(fileURLToPath(import.meta.url));
const appSource = readFileSync(join(root, 'app.js'), 'utf8');
const surfacePath = join(
  root,
  '..', '..', 'tests', 'UnifiedScript.Tests', 'Fixtures', 'AuthoringInheritance',
  'InheritedAuthoring.surface.json');

function extractFunction(name) {
  const starts = [
    appSource.indexOf(`function ${name}(`),
    appSource.indexOf(`async function ${name}(`),
  ].filter(index => index >= 0);
  const start = Math.min(...starts);
  assert.notEqual(start, -1, `app.js is missing ${name}`);
  const nextMatch = /\n(?:async )?function /.exec(appSource.slice(start + 1));
  const next = nextMatch ? start + 1 + nextMatch.index : appSource.length;
  return appSource.slice(start, next);
}

const helperNames = [
  'assertArtifact',
  'cloneJsonValue',
  'canonicalJson',
  'sameJsonContract',
  'surfaceDeclarationMatches',
  'surfaceDeclarationOwner',
  'validateAuthoringSurface',
  'artifactDefaultText',
  'supportedStateType',
  'typeKindFor',
  'metadataTypeFor',
  'typedDefault',
  'exposureContract',
  'replacementContract',
  'declarationChanged',
  'buildDeclarationPatch',
  'defaultArgument',
  'runDebug',
];
const context = vm.createContext({});
vm.runInContext(
  `${helperNames.map(extractFunction).join('\n')}
   globalThis.helpers = { ${helperNames.join(', ')} };`,
  context,
);
const helpers = context.helpers;

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

const surface = JSON.parse(readFileSync(surfacePath, 'utf8'));

function editorStateItem(declaration) {
  return {
    id: declaration.id,
    semanticId: declaration.id,
    name: declaration.name,
    type: declaration.typeDisplayName,
    typeKind: declaration.typeKind,
    typeMetadataName: declaration.typeMetadataName,
    rawDefaultValue: clone(declaration.defaultValue),
    defaultValue: helpers.artifactDefaultText(declaration),
    editable: declaration.exposure.instanceEditable,
    save: declaration.exposure.saveGame,
    replicated: declaration.exposure.replicated,
    category: declaration.exposure.category || '',
    original: {
      name: declaration.name,
      typeMetadataName: declaration.typeMetadataName,
      typeKind: declaration.typeKind,
      defaultValue: clone(declaration.defaultValue),
      exposure: clone(declaration.exposure),
    },
  };
}

test('Surface v0.5 owner and graph contracts are validated by the browser helpers', () => {
  assert.doesNotThrow(() => helpers.validateAuthoringSurface(surface));

  const seed = surface.assets[0].states.find(item => item.name === 'Seed');
  const owner = helpers.surfaceDeclarationOwner(surface, seed.id, 'state');
  assert.equal(owner.assetPath, 'Game/ZBase');

  const changed = clone(surface);
  const follower = changed.assets.find(asset => asset.assetPath === 'Game/BFollower');
  const functionToChange = follower.functions[0];
  functionToChange.actions[0].properties = {
    ...functionToChange.actions[0].properties,
    authoringBoundary: 'changed in a nested graph property',
  };
  assert.throws(
    () => helpers.validateAuthoringSurface(changed),
    /inconsistent contract/,
  );

  const duplicate = clone(surface);
  duplicate.assets[0].functions.push(clone(duplicate.assets[0].functions[0]));
  assert.throws(() => helpers.validateAuthoringSurface(duplicate), /duplicate declaration/);
});

test('Surface owner and legacy validation rejects missing or foreign metadata', () => {
  const seed = surface.assets[0].states.find(item => item.name === 'Seed');
  const missingOwner = clone(surface);
  missingOwner.declarationOwners = missingOwner.declarationOwners
    .filter(owner => owner.declarationId !== seed.id);
  assert.throws(() => helpers.validateAuthoringSurface(missingOwner), /explicit source owner/);

  const foreignOwner = clone(surface);
  foreignOwner.declarationOwners = foreignOwner.declarationOwners.map(owner =>
    owner.declarationId === seed.id ? { ...owner, ownerAssetId: 'us_foreign' } : owner);
  assert.throws(() => helpers.validateAuthoringSurface(foreignOwner), /does not contain|invalid/);

  const legacyUnique = clone(surface);
  legacyUnique.schemaVersion = 'unifiedscript.authoring-surface/v0.4';
  legacyUnique.assets = [legacyUnique.assets[0]];
  legacyUnique.declarationOwners = [];
  assert.doesNotThrow(() => helpers.validateAuthoringSurface(legacyUnique));
  assert.equal(helpers.surfaceDeclarationOwner(legacyUnique, seed.id, 'state').assetPath, 'Game/AOverride');

  const legacyAmbiguous = clone(surface);
  legacyAmbiguous.schemaVersion = 'unifiedscript.authoring-surface/v0.4';
  legacyAmbiguous.assets = legacyAmbiguous.assets.map(asset => ({ ...asset, functions: [] }));
  legacyAmbiguous.declarationOwners = [];
  assert.doesNotThrow(() => helpers.validateAuthoringSurface(legacyAmbiguous));
  assert.equal(helpers.surfaceDeclarationOwner(legacyAmbiguous, seed.id, 'state'), null);

  const reordered = clone(surface);
  const reorderedSeed = reordered.assets[0].states.find(item => item.name === 'Seed');
  const replacement = Object.fromEntries(Object.entries(reorderedSeed).reverse());
  reordered.assets[0].states = reordered.assets[0].states.map(item =>
    item.id === reorderedSeed.id ? replacement : item);
  assert.doesNotThrow(() => helpers.validateAuthoringSurface(reordered));

  const nestedQualifiedName = clone(surface);
  const nestedFunction = nestedQualifiedName.assets[1].functions[0];
  nestedFunction.actions[0].properties.qualifiedName = 'changed-nested-qualified-name';
  assert.throws(() => helpers.validateAuthoringSurface(nestedQualifiedName), /inconsistent contract/);
});

test('buildDeclarationPatch only adds local state and rejects inherited edits', () => {
  const follower = surface.assets.find(asset => asset.assetPath === 'Game/BFollower');
  const followerDeclarations = follower.states.map(editorStateItem);
  const localState = {
    id: 'pending_local_state', semanticId: 'pending_local_state', name: 'LocalState',
    type: 'int', typeKind: 'integer', typeMetadataName: 'System.Int32',
    rawDefaultValue: 42, defaultValue: '42', editable: false, save: false,
    replicated: false, category: '', original: null,
  };
  context.state = {
    surface,
    selectedAssetId: follower.id,
    sourceCompilationId: surface.sourceCompilationId,
    semanticConfigurationId: surface.semanticConfigurationId,
    declarations: [...followerDeclarations, localState],
    removedDeclarations: [],
  };
  const addPatch = helpers.buildDeclarationPatch();
  assert.equal(addPatch.operations.length, 1);
  assert.equal(addPatch.operations[0].operation, 'addState');
  assert.equal(addPatch.operations[0].targetAssetId, follower.id);

  const inheritedEdit = { ...followerDeclarations.find(item => item.name === 'Seed'), defaultValue: '11' };
  context.state.declarations = [inheritedEdit, followerDeclarations.find(item => item.name === 'Values')];
  assert.throws(() => helpers.buildDeclarationPatch(), /source owner/);

  const owner = surface.assets.find(asset => asset.assetPath === 'Game/ZBase');
  const ownerDeclarations = owner.states.map(editorStateItem);
  const ownerSeed = { ...ownerDeclarations.find(item => item.name === 'Seed'), defaultValue: '11' };
  context.state.selectedAssetId = owner.id;
  context.state.declarations = [ownerSeed, ownerDeclarations.find(item => item.name === 'Values')];
  const ownerPatch = helpers.buildDeclarationPatch();
  assert.equal(ownerPatch.operations.length, 1);
  assert.equal(ownerPatch.operations[0].operation, 'updateState');
  assert.equal(ownerPatch.operations[0].expected.defaultValue, 3);
  assert.equal(ownerPatch.operations[0].replacement.defaultValue, 11);
  assert.equal(ownerPatch.operations[0].targetStateId, ownerSeed.id);
});

test('runDebug sends the selected leaf asset and typed inherited defaults', async () => {
  const selectedAsset = surface.assets.find(asset => asset.assetPath === 'Game/AOverride');
  const func = selectedAsset.functions.find(candidate => candidate.name === 'Forward');
  let capturedRequest = null;
  context.state = {
    debugConnected: true,
    selectedAssetId: selectedAsset.id,
    functionSurface: func,
    declarations: selectedAsset.states.map(editorStateItem),
    sourceCompilationId: surface.sourceCompilationId,
    semanticConfigurationId: surface.semanticConfigurationId,
    debugSession: null,
  };
  context.clearDebugVisuals = () => {};
  context.changeView = () => {};
  context.activeBreakpointNodeIds = () => [];
  context.setDebugBusy = () => {};
  context.renderDebugRunning = () => {};
  context.waitForDebugState = async () => {};
  context.renderDebugError = error => { throw error; };
  context.debugRequest = async (path, request) => {
    capturedRequest = { path, request };
    return {
      schemaVersion: 'unifiedscript.debug-session/v0.2',
      state: 'completed', executionId: 'exec-test',
      generation: { generationId: 'gen-test', number: 1 },
    };
  };
  await helpers.runDebug();

  assert.equal(capturedRequest.path, '/api/debug/start');
  assert.equal(capturedRequest.request.receiver.assetId, selectedAsset.id);
  assert.deepEqual(capturedRequest.request.receiver.stateOverrides, []);

  const seedItem = context.state.declarations.find(item => item.name === 'Seed');
  seedItem.defaultValue = '99';
  await helpers.runDebug();

  assert.equal(capturedRequest.path, '/api/debug/start');
  assert.equal(capturedRequest.request.receiver.assetId, selectedAsset.id);
  const seed = selectedAsset.states.find(item => item.name === 'Seed');
  const values = selectedAsset.states.find(item => item.name === 'Values');
  const seedOverride = capturedRequest.request.receiver.stateOverrides.find(item => item.stateId === seed.id);
  assert.equal(capturedRequest.request.receiver.stateOverrides.length, 1);
  assert.equal(seedOverride.value, 99);
  assert.equal(capturedRequest.request.receiver.stateOverrides.some(item => item.stateId === values.id), false);
});

test('Browser defaults preserve raw JSON and only supported scalar edits are typed', () => {
  const original = {
    name: 'Seed',
    typeMetadataName: 'System.Int32',
    typeKind: 'integer',
    defaultValue: 3,
    exposure: { instanceEditable: true, saveGame: false, replicated: false },
  };
  const unchanged = {
    name: 'Seed', type: 'int', typeKind: 'integer', typeMetadataName: 'System.Int32',
    defaultValue: '3', rawDefaultValue: 3, editable: true, save: false, replicated: false, category: '', original,
  };
  assert.equal(helpers.typedDefault(unchanged), 3);
  assert.equal(helpers.declarationChanged(unchanged), false);

  const changedType = { ...unchanged, type: 'string', defaultValue: '3' };
  assert.equal(helpers.typedDefault(changedType), '3');
  assert.equal(helpers.declarationChanged(changedType), true);

  const nullable = {
    name: 'Label', type: 'string', typeKind: 'string', typeMetadataName: 'System.String',
    defaultValue: 'null', rawDefaultValue: null,
    original: {
      name: 'Label', typeMetadataName: 'System.String', typeKind: 'string',
      defaultValue: null, exposure: {},
    },
  };
  assert.equal(helpers.typedDefault(nullable), null);

  const array = {
    name: 'Values', type: 'IReadOnlyList<int>', typeKind: 'array',
    typeMetadataName: 'System.Collections.Generic.IReadOnlyList<System.Int32>',
    defaultValue: '[2,4]', rawDefaultValue: [2, 4],
    original: {
      name: 'Values', typeMetadataName: 'System.Collections.Generic.IReadOnlyList<System.Int32>',
      typeKind: 'array', defaultValue: [2, 4], exposure: {},
    },
  };
  assert.deepEqual(JSON.parse(JSON.stringify(helpers.typedDefault(array))), [2, 4]);
  assert.equal(helpers.supportedStateType(array), false);
});

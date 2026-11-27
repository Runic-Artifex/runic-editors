const assert = require('node:assert/strict');
const vscode = require('vscode');
let stage = 'activation and native providers';
exports.run = async function () {
  const root = vscode.workspace.workspaceFolders[0].uri;
  const en = vscode.Uri.joinPath(root, 'en.rmf2');
  const document = await vscode.workspace.openTextDocument(en);
  const editor = await vscode.window.showTextDocument(document);
  const extension = vscode.extensions.getExtension('runic-artifex.runic-devkit');
  assert.ok(extension);
  // The former identities (identity.ts) are not installed next to it.
  assert.equal(vscode.extensions.getExtension('runic-artifex.runic'), undefined);
  assert.equal(vscode.extensions.getExtension('runic-artifex.runic-translations'), undefined);
  const api = await extension.activate(); const client = await api.ready(en);
  // The Runic protocol the server speaks: 2 for a server that advertises experimental.runic (Translations 0.14, the
  // NuGet tool CI restores), 1 for Translations 0.13. RUNIC_TRANSLATIONS_PROTOCOL (run-host.mjs sets 2 for the NuGet
  // tool) makes the run insist on one.
  const protocol = await api.protocol(en);
  if (process.env.RUNIC_TRANSLATIONS_PROTOCOL) assert.equal(protocol, Number(process.env.RUNIC_TRANSLATIONS_PROTOCOL), 'The server does not speak the requested Runic protocol');
  console.log(`Runic protocol ${protocol}`);
  const position = document.positionAt(document.getText().indexOf('payment ='));
  editor.selection = new vscode.Selection(position, position);
  const symbols = await vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', en);
  assert.ok(symbols.some(symbol => symbol.name === 'payment'));
  const info = await client.sendRequest('runic/message', { textDocument: { uri: en.toString() }, position });
  assert.equal(info.key, 'payment'); assert.ok(info.slots.includes('retry'));
  const definitions = await vscode.commands.executeCommand('vscode.executeDefinitionProvider', en, position);
  assert.equal(definitions.length, 2);
  const preview = await client.sendRequest('workspace/executeCommand', { command: 'runic.preview', arguments: [en.toString(), 'payment', 'de'] });
  assert.equal(preview.ast.astVersion, 5); assert.ok(preview.examples.length > 0);
  assert.deepEqual(preview.inputs.map(input => input.name), ['count', 'tone']);
  const rendered = await client.sendRequest('workspace/executeCommand', {command: 'runic.renderPreview', arguments: [en.toString(), 'payment', 'en', {count: '1', tone: 'positive'}]});
  assert.ok(JSON.stringify(rendered.runs).includes('runic:action'));
  assert.ok(JSON.stringify(rendered.runs).includes('positive'));
  await assert.rejects(client.sendRequest('workspace/executeCommand', {command: 'runic.renderPreview', arguments: [en.toString(), 'payment', 'en', {count: 'invalid', tone: 'positive'}]}));
  stage = 'status bar and whole-catalog diagnostics';
  // The item shows the catalog and the file's locale once the server answers, then adds the locale's coverage when
  // runic-translations status reports (a separate tool run), so wait for that final text: which one a poll sees first
  // depends on timing.
  const statusText = /^\$\(globe\) Runic: checkout · en(?: (\d+(?:\.\d+)?%))?$/;
  await waitFor(() => statusText.exec(api.status() ?? '')?.[1] !== undefined, () => `Status bar did not show the catalog, locale and coverage: ${api.status()}`, 30000);
  assert.equal(api.status(), '$(globe) Runic: checkout · en 100%');
  assert.equal(api.serverState(en), 'running');
  // A catalog error in a closed translation reaches the Problems view with its documentation link.
  const de = vscode.Uri.joinPath(root, 'de.rmf2');
  const fs = require('node:fs');
  const germanSource = fs.readFileSync(de.fsPath, 'utf8');
  fs.writeFileSync(de.fsPath, germanSource.replace('plain = Zahlungsdetails', 'plain = Zahlungsdetails {$extra}'));
  await waitFor(() => vscode.languages.getDiagnostics(de).some(item => /^RTR\d{4}$/.test(String(item.code?.value)) &&
    String(item.code?.target).endsWith(`#${String(item.code.value).toLowerCase()}`)), 'The closed translation did not report its catalog diagnostic with a link');
  fs.writeFileSync(de.fsPath, germanSource);
  await waitFor(() => vscode.languages.getDiagnostics(de).length === 0, 'Fixing the closed translation did not clear its diagnostics');
  stage = 'all examples and session sample reuse';
  // Two samples in an unsaved buffer must both be exposed by the compiler and
  // selecting the second one must skip per-input prompts and survive reopening.
  const exampleEdit = new vscode.WorkspaceEdit();
  const examplePosition = document.positionAt(document.getText().indexOf('payment ='));
  exampleEdit.insert(en, examplePosition, '@example {"count":0,"tone":"neutral"}\n');
  assert.equal(await vscode.workspace.applyEdit(exampleEdit), true);
  const samplePosition = document.positionAt(document.getText().indexOf('payment ='));
  editor.selection = new vscode.Selection(samplePosition, samplePosition);
  // The previous stage's writes to de.rmf2 reach the server as workspace/didChangeWatchedFiles, and a write can arrive
  // as more than one change event after its diagnostics already cleared. The server answers a request pending across
  // any such revision with ContentModified (#9), so retry it as the client does for its own requests.
  const examples = await retryModified(() => client.sendRequest('workspace/executeCommand', {command: 'runic.preview', arguments: [en.toString(), 'payment', 'en']}));
  assert.equal(examples.examples.length, 2);
  const selected = await vscode.commands.executeCommand('runic.translations.preview', {locale: 'en', exampleIndex: 1});
  assert.deepEqual(selected.samples, {count: '0', tone: 'neutral'});
  assert.ok(selected.html.includes('Ready'));
  assert.ok(!selected.html.includes('<button disabled>'));
  await vscode.window.showTextDocument(document, vscode.ViewColumn.One);
  const reused = await vscode.commands.executeCommand('runic.translations.preview');
  assert.deepEqual(reused.samples, selected.samples, 'Reopening must reuse locale and values without prompts');
  // The former command ID of the Translations extension still runs the command.
  const changedSamples = await vscode.commands.executeCommand('runicTranslations.previewSamples', {exampleIndex: 0});
  assert.deepEqual(changedSamples.samples, {count: '1', tone: 'positive'});
  assert.ok(changedSamples.html.includes('<button disabled>'));
  await vscode.commands.executeCommand('runic.translations.previewSamples', {exampleIndex: 1});
  const german = await vscode.commands.executeCommand('runic.translations.previewLocale', {locale: 'de', exampleIndex: 1});
  assert.equal(german.target.locale, 'de');
  assert.ok(german.html.includes('Bereit'));
  await vscode.commands.executeCommand('runic.translations.previewLocale', {locale: 'en'});
  const latestEdit = new vscode.WorkspaceEdit();
  stage = 'latest unsaved preview refresh';
  for (const match of document.getText().matchAll(/Ready/g)) {
    latestEdit.replace(en, new vscode.Range(document.positionAt(match.index), document.positionAt(match.index + match[0].length)), 'Latest unsaved');
  }
  assert.equal(await vscode.workspace.applyEdit(latestEdit), true);
  assert.equal(document.isDirty, true);
  await waitFor(() => api.previewState()?.html.includes('Latest unsaved'), 'Preview did not refresh the unsaved buffer');
  assert.deepEqual(api.previewState().samples, {count: '0', tone: 'neutral'});
  const canonical = await client.sendRequest('workspace/executeCommand', {command: 'runic.renderPreview', arguments: [en.toString(), 'payment', 'en', selected.samples]});
  assert.ok(JSON.stringify(canonical.runs).includes('Latest unsaved'));
  stage = 'rename and server restart';
  const rename = await vscode.commands.executeCommand('vscode.executeDocumentRenameProvider', en, samplePosition, 'receipt');
  assert.ok(rename.size >= 3, 'Rename must include source locales and slot configuration');
  stage = 'renamed unsaved preview contract';
  assert.equal(await vscode.workspace.applyEdit(rename), true);
  const nextPosition = document.positionAt(document.getText().indexOf('receipt ='));
  const changed = await client.sendRequest('runic/message', { textDocument: { uri: en.toString() }, position: nextPosition });
  assert.equal(changed.key, 'receipt');
  const nextPreview = await client.sendRequest('workspace/executeCommand', { command: 'runic.preview', arguments: [en.toString(), 'receipt', 'en'] });
  assert.equal(nextPreview.ast.astVersion, 5, 'Rename did not retain the selected semantic contract');
  assert.deepEqual(nextPreview.inputs.map(input => input.name), ['count', 'tone']);
  const nextRendered = await client.sendRequest('workspace/executeCommand', {command: 'runic.renderPreview', arguments: [en.toString(), 'receipt', 'en', {count: '1', tone: 'positive'}]});
  assert.ok(JSON.stringify(nextRendered.runs).includes('runic:action'));
  assert.ok(JSON.stringify(nextRendered.runs).includes('positive'));
  stage = 'server restart synchronization';
  await vscode.commands.executeCommand('runic.translations.restart');
  const restarted = await api.ready(en);
  const afterRestart = await restarted.sendRequest('runic/message', { textDocument: { uri: en.toString() }, position: nextPosition });
  assert.equal(afterRestart.key, 'receipt', 'Restart lost an unsaved resource');
  const app = vscode.Uri.joinPath(root, 'app.cs');
  await vscode.workspace.fs.writeFile(app, Buffer.from('class App { }'));
  if (protocol >= 2) {
    // Protocol 2 asks with window/showMessageRequest before renaming a resource in a mixed workspace (not driven
    // here), and its runic.* commands apply their edit through workspace/applyEdit and answer null.
    const explicit = await restarted.sendRequest('workspace/executeCommand', {command: 'runic.renameResource', arguments: [en.toString(), ['receipt'], 'confirmed']});
    assert.equal(explicit, null);
    await waitFor(() => document.getText().includes('confirmed =') && !document.getText().includes('receipt ='), 'The server did not apply runic.renameResource through workspace/applyEdit');
  } else {
    await assert.rejects(restarted.sendRequest('textDocument/rename', { textDocument: {uri: en.toString()}, position: nextPosition, newName: 'unsafe' }), /Rename refused/);
    const explicit = await restarted.sendRequest('workspace/executeCommand', {command: 'runic.renameResource', arguments: [en.toString(), ['receipt'], 'confirmed']});
    assert.ok(explicit.documentChanges.length >= 3);
    assert.ok(!JSON.stringify(explicit).includes('app.cs'));
  }
  await vscode.workspace.saveAll(false);
  await multiRoot(api, restarted);
  if (protocol >= 2) await protocol2(api, root, fs);
  await runicSdk(api, fs);
  await windowDebug(api, fs);
  require('node:fs').writeFileSync(process.env.RUNIC_VSCODE_RESULT_FILE, JSON.stringify({passed:true,message:`PASS VS Code extension-host assertions (Runic protocol ${protocol}).`}));
  console.log('PASS VS Code activation, native providers, all preview examples, sample reuse, unsaved refresh, source edits and restart.');
};
/**
 * A second Translations folder (the third workspace folder, another copy of fixtures/rmf2) starts its own language
 * server. Both servers advertise the same runic.* commands; the extension registers them once and routes each to the
 * folder of its source document, so the second client starts and its commands reach its own server.
 */
async function multiRoot(api, first) {
  stage = 'multi-root clients and shared server commands';
  const second = vscode.workspace.workspaceFolders[2];
  assert.ok(second, 'The host test needs the second Translations folder');
  const secondEn = vscode.Uri.joinPath(second.uri, 'en.rmf2');
  await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(secondEn), { preview: false });
  const secondClient = await api.ready(secondEn);
  assert.notEqual(secondClient, first);
  assert.equal(api.serverState(secondEn), 'running');
  assert.equal(await api.protocol(secondEn), await api.protocol(vscode.Uri.joinPath(vscode.workspace.workspaceFolders[0].uri, 'en.rmf2')));
  for (const command of ['runic.preview', 'runic.renderPreview', 'runic.markReviewed', 'runic.renameInput', 'runic.extractGroup'])
    assert.ok(api.serverCommands().includes(command), `${command} is not registered`);
  assert.equal((await vscode.commands.getCommands(true)).filter(command => command === 'runic.preview').length, 1);
  // Routed by the source URI: the second folder still has `payment`, which the first folder renamed.
  const preview = await retryModified(() => vscode.commands.executeCommand('runic.preview', secondEn.toString(), 'payment', 'en'));
  assert.equal(preview.key, 'payment');
  await assert.rejects(Promise.resolve(vscode.commands.executeCommand('runic.preview', vscode.Uri.joinPath(vscode.workspace.workspaceFolders[0].uri, 'en.rmf2').toString(), 'payment', 'en')));
}
/**
 * Protocol 2 (Translations 0.14): the palette refactorings invoke F2 at the symbol and the light-bulb code actions,
 * and the server applies the edits through workspace/applyEdit.
 */
async function protocol2(api, root, fs) {
  const path = require('node:path');
  const en = vscode.Uri.joinPath(root, 'en.rmf2');
  const document = await vscode.workspace.openTextDocument(en);
  let editor = await vscode.window.showTextDocument(document, { preview: false });
  stage = 'protocol 2: F2 on an input';
  const inside = document.positionAt(document.getText().indexOf('confirmed ='));
  editor.selection = new vscode.Selection(inside, inside);
  // The palette command opens the editor's rename box at the input; cancel it as a user would with Escape.
  const command = Promise.resolve(vscode.commands.executeCommand('runic.translations.renameInput', { name: 'count' }));
  let settled = false; command.then(() => { settled = true; }, () => { settled = true; });
  const deadline = Date.now() + 10000;
  while (!settled && Date.now() < deadline) { await vscode.commands.executeCommand('cancelRenameInput'); await new Promise(resolve => setTimeout(resolve, 100)); }
  const target = await command;
  assert.ok(target, 'Rename Input did not open the rename at an input');
  const position = new vscode.Position(target.line, target.character);
  assert.equal(document.getText(new vscode.Range(position, position.translate(0, 5))), 'count');
  assert.equal(document.getText(new vscode.Range(position.translate(0, -1), position)), '$');
  assert.ok(vscode.window.activeTextEditor.selection.active.isEqual(position));
  // vscode-languageclient reports a prepareRename the server answered with ContentModified as "can't be renamed".
  const prepared = await retryModified(() => vscode.commands.executeCommand('vscode.prepareRename', en, position), 20, /can't be renamed/);
  assert.equal(prepared.placeholder, 'count');
  const rename = await vscode.commands.executeCommand('vscode.executeDocumentRenameProvider', en, position, 'attempts');
  assert.ok(rename.has(vscode.Uri.joinPath(root, 'de.rmf2')), 'Renaming an input must edit every locale');
  assert.equal(await vscode.workspace.applyEdit(rename), true);
  assert.ok(document.getText().includes('{$attempts :integer}') && !/\$count\b/.test(document.getText()));
  await vscode.workspace.saveAll(false);
  assert.ok(fs.readFileSync(path.join(root.fsPath, 'de.rmf2'), 'utf8').includes('$attempts'));
  stage = 'protocol 2: inline resource code action';
  const account = vscode.Uri.joinPath(root, 'account', 'en.rmf2');
  const accountDocument = await vscode.workspace.openTextDocument(account);
  await vscode.window.showTextDocument(accountDocument, { preview: false });
  stage = 'protocol 2: inline resource code action (listing)';
  const inline = await untilCodeActions(account, new vscode.Range(1, 0, 1, 0), 'refactor.inline', 'Inline into ../en.rmf2');
  assert.deepEqual(inline.map(action => action.title), ['Inline into ../en.rmf2']);
  stage = 'protocol 2: inline resource code action (apply)';
  await untilApplied(api, async () => {
    await vscode.window.showTextDocument(accountDocument, { preview: false });
    ran(api, await vscode.commands.executeCommand('runic.translations.inlineResource'), 'refactor.inline');
  }, () => !fs.existsSync(account.fsPath) && /^account\b/m.test(document.getText()), () => `Inline Resource did not move account/en.rmf2 into en.rmf2 (account exists: ${fs.existsSync(account.fsPath)}): ${JSON.stringify(document.getText())}`);
  assert.ok(document.getText().includes('Your account'));
  // VS Code may already have saved the refactored file (files.refactoring.autoSave); save only what is still dirty,
  // then check the outcome on disk.
  await saveIfDirty(document);
  assert.ok(!fs.existsSync(account.fsPath), 'Inline Resource must delete account/en.rmf2');
  assert.ok(/^account\b/m.test(fs.readFileSync(en.fsPath, 'utf8')) && fs.readFileSync(en.fsPath, 'utf8').includes('Your account'), 'en.rmf2 on disk lacks the inlined account group');
  stage = 'protocol 2: extract group code action';
  editor = await vscode.window.showTextDocument(document, { preview: false });
  const group = document.positionAt(/^account\b/m.exec(document.getText()).index);
  editor.selection = new vscode.Selection(group, group);
  stage = 'protocol 2: extract group code action (listing)';
  const extract = await untilCodeActions(en, new vscode.Range(group, group.translate(0, 7)), 'refactor.extract', "Extract group 'account' to account/en.rmf2");
  assert.deepEqual(extract.map(action => action.title), ["Extract group 'account' to account/en.rmf2"]);
  stage = 'protocol 2: extract group code action (apply)';
  // The created file is written through the workspace edit; VS Code need not open it as a document.
  const extractedText = () => vscode.workspace.textDocuments.find(item => item.uri.fsPath === account.fsPath)?.getText() ?? (fs.existsSync(account.fsPath) ? fs.readFileSync(account.fsPath, 'utf8') : '');
  await untilApplied(api, async () => {
    editor = await vscode.window.showTextDocument(document, { preview: false });
    editor.selection = new vscode.Selection(group, group);
    ran(api, await vscode.commands.executeCommand('runic.translations.extractGroup'), 'refactor.extract');
  }, () => extractedText().includes('heading = Your account') && !/^account\b/m.test(document.getText()),
    () => `Extract Group did not move the account group back (account/en.rmf2: ${JSON.stringify(extractedText())}): ${JSON.stringify(document.getText())}`);
  await saveIfDirty(document);
  await vscode.workspace.saveAll(false);
  assert.ok(fs.readFileSync(account.fsPath, 'utf8').includes('heading = Your account'));
  assert.ok(!fs.readFileSync(en.fsPath, 'utf8').includes('Your account'));
  // Extract Group into a folder that does not exist yet: the client creates profile/ with the file,
  // and the group's messages reach it instead of being lost.
  stage = 'protocol 2: extract group into a new folder';
  const profile = vscode.Uri.joinPath(root, 'profile', 'en.rmf2');
  assert.ok(!fs.existsSync(path.dirname(profile.fsPath)), 'profile/ must not exist before the extract');
  // The server extracts only from a catalog that compiles, so de.rmf2 translates the new group too.
  const de = await vscode.workspace.openTextDocument(vscode.Uri.joinPath(root, 'de.rmf2'));
  const addGroup = async (target, title) => {
    const shown = await vscode.window.showTextDocument(target, { preview: false });
    assert.equal(await shown.edit(builder => builder.insert(target.positionAt(target.getText().length), `\nprofile {\n  title = ${title}\n}\n`)), true);
    await saveIfDirty(target);
  };
  await addGroup(de, 'Ihr Profil');
  await addGroup(document, 'Your profile');
  const profileGroup = document.positionAt(/^profile\b/m.exec(document.getText()).index);
  const profileText = () => vscode.workspace.textDocuments.find(item => item.uri.fsPath === profile.fsPath)?.getText() ?? (fs.existsSync(profile.fsPath) ? fs.readFileSync(profile.fsPath, 'utf8') : '');
  await untilApplied(api, async () => {
    editor = await vscode.window.showTextDocument(document, { preview: false });
    editor.selection = new vscode.Selection(profileGroup, profileGroup);
    ran(api, await vscode.commands.executeCommand('runic.translations.extractGroup'), 'refactor.extract');
  }, () => profileText().includes('title = Your profile') && !/^profile\b/m.test(document.getText()),
    () => `Extract Group into a new folder did not move the profile group (profile/en.rmf2: ${JSON.stringify(profileText())}): ${JSON.stringify(document.getText())}`);
  await saveIfDirty(document);
  await vscode.workspace.saveAll(false);
  assert.ok(fs.readFileSync(profile.fsPath, 'utf8').includes('title = Your profile'), 'profile/en.rmf2 on disk lacks the extracted group');
  assert.ok(!/^profile\b/m.test(fs.readFileSync(en.fsPath, 'utf8')), 'en.rmf2 on disk still has the profile group');
  // A refactoring the server refuses on resolve (here: a group that de.rmf2 lacks) warns with its reason instead of
  // applying nothing silently (#38).
  stage = 'protocol 2: refused extract group warns';
  editor = await vscode.window.showTextDocument(document, { preview: false });
  assert.equal(await editor.edit(builder => builder.insert(document.positionAt(document.getText().length), '\norphan {\n  title = Only in English\n}\n')), true);
  await saveIfDirty(document);
  const orphan = () => /^orphan\b/m.exec(document.getText());
  const refused = api.refusedActions().length;
  editor = await vscode.window.showTextDocument(document, { preview: false });
  const orphanGroup = document.positionAt(orphan().index);
  const offered = await untilCodeActions(en, new vscode.Range(orphanGroup, orphanGroup.translate(0, 6)), 'refactor.extract', "Extract group 'orphan' to orphan/en.rmf2");
  assert.deepEqual(offered.map(action => action.title), ["Extract group 'orphan' to orphan/en.rmf2"]);
  await withinApplyLimit(api, async () => {
    editor = await vscode.window.showTextDocument(document, { preview: false });
    editor.selection = new vscode.Selection(orphanGroup, orphanGroup);
    await vscode.commands.executeCommand('runic.translations.extractGroup');
  }, () => api.refusedActions().length > refused, 'no refusal warning');
  await waitFor(() => api.refusedActions().length > refused, () => `The refused Extract Group showed no warning (outdated: ${JSON.stringify(api.outdatedActions())}; en.rmf2: ${JSON.stringify(document.getText())})`);
  assert.match(api.refusedActions().at(-1), /^Runic: "Extract group 'orphan' to orphan\/en\.rmf2" was not applied: \S/);
  assert.ok(orphan(), 'A refused Extract Group must leave the group in en.rmf2');
  editor = await vscode.window.showTextDocument(document, { preview: false });
  const start = orphan().index;
  assert.equal(await editor.edit(builder => builder.delete(new vscode.Range(document.positionAt(start - 1), document.positionAt(document.getText().indexOf('}', start) + 2)))), true);
  await saveIfDirty(document);
  console.log('PASS protocol 2: F2 on an input, Inline Resource and Extract Group code actions (also into a new folder), server-applied command edits.');
}
/**
 * A palette refactoring's result: the path it took (`applied`, `menu`, `unlisted`, see codeActionAt) and, when the
 * command returned nothing, the error it showed (#40).
 */
function ran(api, result, kind) {
  assert.ok(result, `The refactoring command returned nothing; the error it showed: ${api.commandErrors().at(-1) ?? 'none recorded'}`);
  assert.equal(result.kind, kind, `The refactoring command took another path: ${JSON.stringify(result)}`);
  return result;
}
/** Saves a document that has unsaved changes; a clean one is on disk already. */
async function saveIfDirty(document) {
  if (document.isDirty) assert.equal(await document.save(), true, `${document.uri.fsPath} did not save`);
  assert.equal(document.isDirty, false, `${document.uri.fsPath} still has unsaved changes`);
}
/** The Runic SDK features in the second workspace folder, a copy of fixtures/sdk-app. */
async function runicSdk(api, fs) {
  const path = require('node:path');
  const [translations, appFolder] = vscode.workspace.workspaceFolders;
  const sdk = api.sdk;
  stage = 'workspace detection';
  await sdk.whenScanned();
  const project = path.join(appFolder.uri.fsPath, 'App.csproj');
  assert.deepEqual(sdk.workspaceKind(translations.uri).sdkProjects, [], 'The Translations-only folder must have no Runic SDK project');
  assert.deepEqual(sdk.workspaceKind(translations.uri).translationsProjects, [path.join(translations.uri.fsPath, 'runic.json')]);
  const library = path.join(appFolder.uri.fsPath, 'SharedUi', 'SharedUi.csproj');
  assert.deepEqual(sdk.workspaceKind(appFolder.uri).sdkProjects, [project, library]);
  // Only the Window project is a dev target; the client library is listed as one that is not (S8).
  assert.deepEqual(sdk.workspaceKind(appFolder.uri).windowProjects, [project]);
  assert.deepEqual(sdk.projects(), [project]);
  assert.deepEqual(sdk.libraries(), [library]);
  assert.equal(sdk.devState(project).state, 'none');
  await waitFor(() => /Runic dev: not started/.test(sdk.devLabel()), () => `Status bar: ${sdk.devLabel()}`);
  stage = 'tasks';
  const tasks = await vscode.tasks.fetchTasks({ type: 'runic' });
  assert.deepEqual(tasks.map(task => task.name).sort(), ['build', 'dev', 'dev --no-window', 'doctor']);
  const dev = tasks.find(task => task.name === 'dev --no-window');
  assert.deepEqual(dev.execution.args, ['runic', 'dev', '--no-window', '--project', project]);
  assert.equal(dev.isBackground, true);
  stage = 'dev problems clear at [dev] Ready (#36)';
  const counter = vscode.Uri.file(path.join(appFolder.uri.fsPath, 'CounterViewModel.cs'));
  const devProblems = () => vscode.languages.getDiagnostics(counter).filter(item => item.source === 'runic').map(item => [item.code, item.severity, item.range.start.line, item.range.start.character]);
  const broken = `[host] ${counter.fsPath}(7,17): error CS1002: ; expected [${project}]`;
  const warned = `[host] ${counter.fsPath}(5,16): warning CS0168: The variable 'x' is declared but never used [${project}]`;
  sdk.devOutput(project, ['[dev] Evaluating Views Window project...', warned, broken, '[host] dotnet watch ❌ Exited with error code 1']);
  await waitFor(() => devProblems().length === 2, () => `dev's problems: ${JSON.stringify(devProblems())}`);
  assert.deepEqual(devProblems(), [['CS0168', vscode.DiagnosticSeverity.Warning, 4, 15], ['CS1002', vscode.DiagnosticSeverity.Error, 6, 16]]);
  sdk.devOutput(project, ['[dev] Evaluating Views Window project...', warned, broken, warned, '[dev] Window started: process 4120', '[dev] Ready']);
  await waitFor(() => devProblems().length === 1, () => `[dev] Ready must drop the fixed error: ${JSON.stringify(devProblems())}`);
  assert.deepEqual(devProblems(), [['CS0168', vscode.DiagnosticSeverity.Warning, 4, 15]]);
  sdk.devOutput(project, []);
  await waitFor(() => devProblems().length === 0, 'A new dev run must start without the previous run\'s problems');
  stage = 'session file follower';
  const pid = Number(process.env.RUNIC_FAKE_DEV_PID);
  const sessionFile = path.join(appFolder.uri.fsPath, 'obj', 'runic', 'dev-session.json');
  fs.mkdirSync(path.dirname(sessionFile), { recursive: true });
  const write = session => { const staged = `${sessionFile}.tmp`; fs.writeFileSync(staged, JSON.stringify(session)); fs.renameSync(staged, sessionFile); };
  const session = { schema: 'runic.application.tool.dev-session/1', state: 'starting', processId: pid, startedAt: new Date().toISOString(), project, configuration: 'Debug',
    frontend: { developmentServer: 'vite', url: null, port: null, stateUrl: null, directory: path.join(appFolder.uri.fsPath, 'Frontend'), outputDirectory: path.join(appFolder.uri.fsPath, 'Frontend', 'dist'), generatedDirectory: null },
    webRoot: path.join(appFolder.uri.fsPath, 'bin', 'Debug', 'net10.0', 'www'), pages: [], window: { owner: 'ide', program: 'App', processId: null, starts: 0, environment: { DOTNET_ENVIRONMENT: 'Development' } }, debugEndpoints: [] };
  write(session);
  await waitFor(() => sdk.devState(project).state === 'starting', () => `The session file's starting state was not followed: ${JSON.stringify(sdk.devState(project))}`);
  // A session before runic-sdk 0.24 has no dependencies[]: no dependency rows, and nothing else changes.
  assert.equal(sdk.dependencies(project), undefined);
  assert.ok(!(await sdk.devTree()).some(row => row.label === 'Frontend dependencies'));
  const sharedUiDirectory = path.join(appFolder.uri.fsPath, 'shared-ui');
  const failedDependency = { package: '@acme/shared-ui', directory: sharedUiDirectory, kind: 'compiled', owner: library, ownerName: 'SharedUi', specifier: '@acme/shared-ui/generated',
    buildCommand: 'bun run --bun build', watchCommand: null, exports: [path.join(sharedUiDirectory, 'dist', 'index.js')], state: 'failed', watch: 'rebuild',
    message: 'shared-ui/src/about-panel.ts(42,31): error TS1109: Expression expected.' };
  write({ ...session, state: 'ready', frontend: { ...session.frontend, url: 'http://127.0.0.1:5173/', port: 5173 }, dependencies: [failedDependency] });
  await waitFor(() => sdk.devState(project).state === 'ready', 'The session file\'s ready state was not followed');
  stage = 'dependency tree from the session file (S8)';
  await waitFor(() => sdk.dependencies(project)?.source === 'session', () => `The session's dependencies were not read: ${JSON.stringify(sdk.dependencies(project))}`);
  const sessionRows = (await sdk.devTree()).find(row => row.label === 'Frontend dependencies');
  assert.ok(sessionRows, 'The Dev Session view must list the frontend dependencies');
  assert.equal(sessionRows.description, '1 package, 1 not built');
  assert.deepEqual(sessionRows.children.map(row => [row.label, row.description]), [['@acme/shared-ui', 'SharedUi · failed · rebuilt on change']]);
  assert.equal(sessionRows.children[0].children[0].label, failedDependency.message);
  // A running dev reports its own failures; the status bar offers nothing for them.
  assert.equal(sdk.dependencyStatus(), undefined);
  await waitFor(() => sdk.devLabel().endsWith('Runic dev: ready'), () => `Status bar: ${sdk.devLabel()}`);
  // dotnet runic dev killed: the file keeps "ready", and its process is gone.
  process.kill(pid, 'SIGKILL');
  await waitFor(() => sdk.devState(project).state === 'stopped' && sdk.devState(project).exited === true, 'A dead dotnet runic dev must read as stopped', 15000);
  assert.equal(JSON.parse(fs.readFileSync(sessionFile, 'utf8')).state, 'ready');
  // ... and its dev-stopped is touched, as dev does on a clean stop, so the next build restores www/ (#29).
  const stoppedStamp = path.join(path.dirname(sessionFile), 'dev-stopped');
  await waitFor(() => fs.existsSync(stoppedStamp), 'A dead dotnet runic dev must touch obj/runic/dev-stopped');
  assert.equal(fs.statSync(stoppedStamp).size, 0);
  assert.ok(fs.statSync(stoppedStamp).mtimeMs >= fs.statSync(sessionFile).mtimeMs, 'dev-stopped must be newer than the session file');
  write({ ...session, state: 'stopped' });
  await waitFor(() => sdk.devState(project).state === 'stopped' && !sdk.devState(project).exited, 'The stopped session file was not followed');
  stage = 'ViewModels tree from the development source map';
  const viewModel = path.join(appFolder.uri.fsPath, 'CounterViewModel.cs');
  const sources = path.join(appFolder.uri.fsPath, 'obj', 'Debug', 'net10.0', 'runic-bridge', 'sources.json');
  fs.mkdirSync(path.dirname(sources), { recursive: true });
  fs.writeFileSync(sources, JSON.stringify({ format: 'runic.views.bridge-sources/1',
    viewModels: [{ id: 'Demo.CounterViewModel', name: 'Counter', route: 'counter', symbol: 'Demo.CounterViewModel', location: { file: viewModel, line: 3, column: 29 } }],
    members: [{ id: 'Demo.CounterViewModel/command/increment', viewModel: 'Demo.CounterViewModel', member: 'increment', kind: 'command', symbol: 'Demo.CounterViewModel.Increment()', location: { file: viewModel, line: 7, column: 17 } },
      { id: 'Demo.CounterViewModel/state/count', viewModel: 'Demo.CounterViewModel', member: 'count', kind: 'state', symbol: 'Demo.CounterViewModel.Count', location: { file: viewModel, line: 5, column: 16 } }] }));
  const tree = sdk.viewModels(project);
  assert.deepEqual(tree.map(node => node.label), ['Counter']);
  assert.deepEqual(tree[0].children.map(node => node.label), ['count', 'increment']);
  await vscode.commands.executeCommand('runic.openSourceLocation', tree[0].children[1].location);
  await waitFor(() => vscode.window.activeTextEditor?.document.uri.fsPath === viewModel && vscode.window.activeTextEditor.selection.active.line === 6 && vscode.window.activeTextEditor.selection.active.character === 16,
    'Open C# member did not open CounterViewModel.cs at Increment');
  stage = 'doctor results and code actions';
  const report = await sdk.runDoctor(project);
  assert.equal(report.healthy, false);
  const projectUri = vscode.Uri.file(project);
  const problems = vscode.languages.getDiagnostics(projectUri);
  assert.deepEqual(problems.map(item => [item.code.value, item.severity]).sort(), [['desktop-runtime', vscode.DiagnosticSeverity.Error], ['lock-file', vscode.DiagnosticSeverity.Warning]]);
  assert.ok(problems.some(item => item.message.includes('Fix: Install webkitgtk_4_1.')));
  const csproj = await vscode.workspace.openTextDocument(projectUri);
  assert.ok(sdk.codeActions(csproj, problems).some(action => action.title === 'Run dotnet runic doctor again'));
  const bridge = new vscode.Diagnostic(new vscode.Range(0, 0, 0, 1), 'A ViewModel member is not supported.', vscode.DiagnosticSeverity.Warning);
  bridge.code = 'RUNICBRIDGE013';
  const help = sdk.codeActions(csproj, [bridge]);
  assert.equal(help[0].title, 'Show RUNICBRIDGE013 in the Runic diagnostics catalog');
  assert.equal(String(help[0].command.arguments[0]), 'https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicbridge013');
  await frontendDependencies(sdk, appFolder, project, csproj, fs, path);
  await sessionFailure(sdk, appFolder, project, sessionFile, write, session, fs, path);
  console.log('PASS Runic SDK: workspace detection, tasks, session file liveness, ViewModels tree, doctor and code actions, frontend dependencies, a failed dev from its session file.');
}
/**
 * D087: a dev that stops at its start (RAPPDEV1018 after a frontend package's build error; the 0.24 VM run, part 4,
 * rows 8 and 9) writes the failure into its session file, and DevKit shows it in Problems and the dependency tree after
 * the task has ended. VS Code removes a background task's problems when its process ends, so the template's problem
 * matcher cannot keep them. First the runic-app template's real tasks.json (fixtures/template-vscode): its `runic: dev`
 * task through VS Code's own task system, as F5's preLaunchTask and Terminal > Run Task run it, with run-host.mjs's
 * stand-in `dotnet` on PATH. Then DevKit's own start, whose terminal reads the same failure from dev's output: each
 * problem once. A session without `failure` (the next dev, or a tool before 0.24) shows none.
 */
async function sessionFailure(sdk, appFolder, project, sessionFile, write, session, fs, path) {
  const app = appFolder.uri.fsPath;
  const tasksFile = path.join(app, '.vscode', 'tasks.json');
  const marker = path.join(app, 'dev-session-failure');
  const argsLog = path.join(app, 'dev-args.log');
  const source = path.join(app, 'shared-ui', 'src', 'index.ts');
  const graphFile = path.join(app, 'obj', 'runic', 'frontend-graph.json');
  const runicProblems = () => vscode.languages.getDiagnostics().flatMap(([uri, items]) => items.filter(item => item.source === 'runic')
    .map(item => [uri.fsPath, String(item.code?.value ?? item.code), item.severity, item.range.start.line + 1, item.range.start.character + 1]))
    .filter(([file]) => file.startsWith(app)).sort();
  const expected = JSON.stringify([[project, 'RAPPDEV1018', vscode.DiagnosticSeverity.Error, 1, 1], [source, 'TS2322', vscode.DiagnosticSeverity.Error, 12, 14]].sort());
  const ends = name => new Promise(resolve => {
    const listener = vscode.tasks.onDidEndTask(event => { if (event.execution.task.name === name) { listener.dispose(); resolve('ended'); } });
  });
  const failedTree = async () => {
    await waitFor(() => sdk.dependencies(project)?.dependencies[0]?.state === 'failed', () => `The package must read as failed: ${JSON.stringify(sdk.dependencies(project))}`);
    assert.deepEqual(sdk.dependencies(project).dependencies.map(item => [item.state, item.message]), [['failed', "src/index.ts(12,14): error TS2322: Type 'string' is not assignable to type 'number'."]]);
    assert.deepEqual((await sdk.devTree()).find(row => row.label === 'Frontend dependencies').children.map(row => row.description), ['SharedUi · failed']);
  };
  const cleared = async message => {
    write({ ...session, state: 'stopped', startedAt: new Date().toISOString() });
    await waitFor(() => runicProblems().length === 0, () => `${message}: ${JSON.stringify(runicProblems())}`, 15000);
    assert.equal(sdk.dependencies(project).dependencies[0].state, 'built');
  };
  fs.writeFileSync(marker, '');
  try {
    stage = 'session failure: the template\'s runic: dev task, run by VS Code';
    fs.copyFileSync(path.join(app, 'template-tasks.json'), tasksFile);
    let task;
    for (let attempt = 0; !(task = (await vscode.tasks.fetchTasks()).find(item => item.name === 'runic: dev' && item.source === 'Workspace' && item.scope?.uri?.fsPath === app)); attempt++) {
      if (attempt >= 75) assert.fail('VS Code did not read the template\'s tasks.json');
      await new Promise(resolve => setTimeout(resolve, 200));
    }
    assert.ok(!(task.execution instanceof vscode.CustomExecution), 'The template\'s task must be VS Code\'s process task with its own problem matcher');
    assert.equal(sdk.dependencies(project)?.dependencies[0]?.state, 'built', 'The package\'s earlier build is on disk');
    assert.equal(runicProblems().length, 0);
    const ended = ends('runic: dev');
    await vscode.tasks.executeTask(task);
    assert.equal(await Promise.race([ended, new Promise(resolve => setTimeout(() => resolve('timeout'), 30000))]), 'ended', 'The template\'s dev task did not end');
    assert.deepEqual(fs.readFileSync(argsLog, 'utf8').split('\n').filter(Boolean).slice(0, 2), [app, 'runic dev'], 'The stand-in dev on PATH did not run');
    // After the task has ended: VS Code has removed its matcher's problems; DevKit's come from the session file.
    await new Promise(resolve => setTimeout(resolve, 1500));
    await waitFor(() => JSON.stringify(runicProblems()) === expected, () => `Problems: ${JSON.stringify(runicProblems())}`, 15000);
    assert.equal(sdk.devState(project).state, 'stopped');
    await failedTree();
    stage = 'session failure: the next session has none';
    await cleared('The next session, without a failure, must clear its Problems');

    stage = 'session failure: DevKit\'s own terminal reads the same dev';
    fs.rmSync(tasksFile);
    for (let attempt = 0; (await vscode.tasks.fetchTasks()).some(item => item.name === 'runic: dev' && item.source === 'Workspace'); attempt++) {
      if (attempt >= 75) assert.fail('VS Code kept the removed tasks.json');
      await new Promise(resolve => setTimeout(resolve, 200));
    }
    fs.writeFileSync(path.join(app, 'tool-version'), '0.24.0\n');
    fs.rmSync(argsLog, { force: true });
    const ownEnded = ends('dev');
    await vscode.commands.executeCommand('runic.dev.start', project);
    assert.equal(await Promise.race([ownEnded, new Promise(resolve => setTimeout(() => resolve('timeout'), 30000))]), 'ended', 'DevKit\'s dev task did not end');
    assert.equal(fs.readFileSync(argsLog, 'utf8').split('\n').filter(Boolean)[1].split(' ').slice(0, 2).join(' '), 'runic dev');
    await new Promise(resolve => setTimeout(resolve, 1500));
    await waitFor(() => JSON.stringify(runicProblems()) === expected, () => `Each problem once: ${JSON.stringify(runicProblems())}`, 15000);
    await failedTree();

    // The 0.24 VM run, release checklist rows D087 b and d: a never-built project has no graph file, and the session's
    // dependencies are [], so the tree had no package to mark. The failure's package gets a row of its own.
    stage = 'session failure: a never-built project, without a graph file';
    sdk.devOutput(project, []);
    await cleared('The next session must clear the failure that DevKit\'s terminal also read');
    fs.renameSync(graphFile, `${graphFile}.built`);
    await waitFor(() => sdk.dependencies(project) === undefined, () => `Without a graph or a failure there is no package: ${JSON.stringify(sdk.dependencies(project))}`);
    assert.equal((await sdk.devTree()).some(row => row.label === 'Frontend dependencies'), false);
    const unbuiltEnded = ends('dev');
    await vscode.commands.executeCommand('runic.dev.start', project);
    assert.equal(await Promise.race([unbuiltEnded, new Promise(resolve => setTimeout(() => resolve('timeout'), 30000))]), 'ended', 'DevKit\'s dev task did not end');
    await waitFor(() => sdk.dependencies(project)?.dependencies[0]?.state === 'failed', () => `The failure's package must get a failed row: ${JSON.stringify(sdk.dependencies(project))}`, 15000);
    assert.equal(sdk.dependencies(project).source, 'last-session');
    const unbuiltRow = (await sdk.devTree()).find(row => row.label === 'Frontend dependencies').children;
    assert.deepEqual(unbuiltRow.map(row => [row.label, row.description]), [['@acme/shared-ui', 'SharedUi · failed']]);
    assert.deepEqual(unbuiltRow[0].children.map(row => row.label), ["src/index.ts(12,14): error TS2322: Type 'string' is not assignable to type 'number'."]);
    // A later build's graph takes the row over.
    fs.renameSync(`${graphFile}.built`, graphFile);
    await waitFor(() => sdk.dependencies(project)?.source === 'graph', () => `The graph must take over: ${JSON.stringify(sdk.dependencies(project))}`);
    await failedTree();
    console.log('PASS Runic session failure: the template\'s task and DevKit\'s terminal leave TS2322 in the package, RAPPDEV1018 on the project and a failed package after dev ended, each once; a never-built project shows the failed package too.');
  } finally {
    if (fs.existsSync(`${graphFile}.built`)) fs.renameSync(`${graphFile}.built`, graphFile);
    for (const file of [tasksFile, marker, argsLog, path.join(app, 'tool-version')]) fs.rmSync(file, { force: true });
    sdk.devOutput(project, []);
    write({ ...session, state: 'stopped' });
  }
  await cleared('The next session must clear the failure that DevKit\'s terminal also read');
}
/**
 * F5 of the `runic` type with scenario M's `/mock` (design/ide-run-debug E1, E2): a stand-in `coreclr` debugger
 * (test/coreclr-stand-in) records the launch request that DevKit's resolver hands it, so no C# extension is needed. The
 * stand-in `dotnet` builds nothing and answers `dotnet msbuild -getProperty` from run-properties.json; a stand-in `dev`
 * process (RUNIC_FAKE_F5_DEV_PID) makes the session file ready with the Window owned by the IDE, so DevKit starts no dev.
 */
async function windowDebug(api, fs) {
  const path = require('node:path');
  const appFolder = vscode.workspace.workspaceFolders[1];
  const app = appFolder.uri.fsPath;
  const project = path.join(app, 'App.csproj');
  const sdk = api.sdk;
  stage = 'F5 /mock: setup';
  const standIn = vscode.extensions.getExtension('runic-artifex-tests.coreclr-stand-in');
  assert.ok(standIn, 'The coreclr stand-in extension is not loaded');
  const { launches } = await standIn.activate();
  const program = path.join(app, 'bin', 'Debug', 'net10.0', 'App');
  const runProperties = (RunArguments = '') => fs.writeFileSync(path.join(app, 'run-properties.json'), JSON.stringify({ Properties: { RunCommand: program, RunArguments, RunWorkingDirectory: '', TargetPath: `${program}.dll` } }));
  fs.mkdirSync(path.join(app, 'Properties'), { recursive: true });
  const launchSettings = profiles => fs.writeFileSync(path.join(app, 'Properties', 'launchSettings.json'), JSON.stringify({ profiles }, null, 2));
  const template = { App: { commandName: 'Project', environmentVariables: { DOTNET_ENVIRONMENT: 'Development' } } };
  const sessionFile = path.join(app, 'obj', 'runic', 'dev-session.json');
  const pid = Number(process.env.RUNIC_FAKE_F5_DEV_PID);
  const session = { schema: 'runic.application.tool.dev-session/1', state: 'ready', processId: pid, startedAt: new Date().toISOString(), project, configuration: 'Debug',
    frontend: { developmentServer: 'vite', url: 'http://127.0.0.1:5173/', port: 5173, stateUrl: null, directory: path.join(app, 'Frontend'), outputDirectory: path.join(app, 'Frontend', 'dist'), generatedDirectory: null },
    webRoot: path.join(app, 'bin', 'Debug', 'net10.0', 'www'), pages: [], debugEndpoints: [],
    window: { owner: 'ide', program: 'App', processId: null, starts: 0, environment: { DOTNET_ENVIRONMENT: 'Development', RUNIC_APPLICATION_DEVELOPMENT_ASSETS: path.join(app, 'Frontend', 'dist') } } };
  fs.writeFileSync(`${sessionFile}.tmp`, JSON.stringify(session)); fs.renameSync(`${sessionFile}.tmp`, sessionFile);
  await waitFor(() => sdk.devState(project).state === 'ready', () => `The F5 session file was not followed: ${JSON.stringify(sdk.devState(project))}`, 15000);
  /** Starts a `runic` configuration and returns the launch request the coreclr stand-in received, after the session ended. */
  const f5 = async (name, configuration) => {
    stage = `F5 /mock: ${name}`;
    const before = launches.length;
    const ended = new Promise(resolve => { const listener = vscode.debug.onDidTerminateDebugSession(item => { if (item.type === 'coreclr') { listener.dispose(); resolve(); } }); });
    const started = await vscode.debug.startDebugging(appFolder, { type: 'runic', request: 'launch', name, ...configuration });
    assert.ok(started, `${name}: the debug session did not start`);
    await waitFor(() => launches.length > before, `${name}: the coreclr stand-in received no launch request`, 60000);
    await Promise.race([ended, new Promise(resolve => setTimeout(resolve, 10000))]);
    return launches[launches.length - 1];
  };

  // V1, the maintainer's failure: `args` in the configuration reached nothing before E1.
  runProperties(''); launchSettings(template);
  let launch = await f5('V1 args', { args: ['/mock'] });
  assert.deepEqual(launch.args, ['/mock']);
  assert.equal(launch.program, program);
  assert.equal(launch.cwd, app);
  assert.equal(launch.launchSettingsProfile, null, 'The C# debugger must not apply the profile a second time');
  assert.equal(launch.env.DOTNET_ENVIRONMENT, 'Development');
  assert.equal(launch.env.RUNIC_APPLICATION_DEVELOPMENT_ASSETS, path.join(app, 'Frontend', 'dist'));
  // V2: the first profile's commandLineArgs.
  launchSettings({ App: { ...template.App, commandLineArgs: '/mock' } });
  launch = await f5('V2 profile', {});
  assert.deepEqual(launch.args, ['/mock']);
  // V3: StartArguments in a Debug PropertyGroup, as MSBuild's RunArguments; an older configuration with name only.
  runProperties('/mock'); launchSettings(template);
  launch = await f5('V3 StartArguments', {});
  assert.deepEqual(launch.args, ['/mock']);
  // V4: the project's and the profile's arguments are both passed, the project's first (D084.2).
  launchSettings({ App: { ...template.App, commandLineArgs: '/verbose' } });
  launch = await f5('V4 both', {});
  assert.deepEqual(launch.args, ['/mock', '/verbose']);
  // V5: launchSettingsProfile null means no profile; launchProfile names one, with its variables and working directory.
  runProperties('');
  launchSettings({ App: { ...template.App, commandLineArgs: '/wrong' }, 'Mock data': { commandName: 'Project', commandLineArgs: '/mock /config:"C:\\Program Files\\Acme\\dev.json"', environmentVariables: { ACME_DATA: 'mock' }, workingDirectory: 'data' } });
  launch = await f5('V5 no profile', { launchSettingsProfile: null });
  assert.deepEqual(launch.args, []);
  launch = await f5('named profile', { launchProfile: 'Mock data', env: { FROM_LAUNCH: '1' }, stopAtEntry: true, justMyCode: false, console: 'internalConsole' });
  assert.deepEqual(launch.args, ['/mock', '/config:C:\\Program Files\\Acme\\dev.json']);
  assert.equal(launch.cwd, path.join(app, 'data'));
  assert.deepEqual([launch.env.ACME_DATA, launch.env.FROM_LAUNCH, launch.stopAtEntry, launch.justMyCode], ['mock', '1', true, false]);
  assert.ok(!('launchProfile' in launch) && !('project' in launch), 'DevKit\'s own attributes must not reach the C# debugger');
  // A cwd and an envFile of the configuration; VS Code substitutes ${workspaceFolder} in cwd after DevKit's resolver.
  fs.writeFileSync(path.join(app, 'mock.env'), 'ACME_DATA=from-file\nFILE_ONLY=1\n');
  launch = await f5('cwd and envFile', { launchProfile: 'Mock data', cwd: '${workspaceFolder}/elsewhere', envFile: '${workspaceFolder}/mock.env', env: { ACME_DATA: 'from-env' } });
  assert.equal(launch.cwd, path.join(app, 'elsewhere'));
  assert.deepEqual([launch.env.ACME_DATA, launch.env.FILE_ONLY], ['from-env', '1']);
  // Restart (Ctrl+Shift+F5) rebuilds: VS Code reuses the resolved coreclr configuration without DevKit's resolver and
  // runs its preLaunchTask, which is the build (the 0.24 VM run started the old DLL, part 3, finding 1).
  stage = 'F5 /mock: Restart rebuilds';
  const buildsLog = path.join(app, 'builds.log');
  const builds = () => fs.existsSync(buildsLog) ? fs.readFileSync(buildsLog, 'utf8').split('\n').filter(Boolean).length : 0;
  const restartBefore = launches.length, buildsBefore = builds();
  assert.ok(buildsBefore > 0, 'F5 must have run the build');
  assert.ok(await vscode.debug.startDebugging(appFolder, { type: 'runic', request: 'launch', name: 'restart', launchProfile: 'Mock data', standInKeepRunning: true }), 'The restart session did not start');
  await waitFor(() => launches.length === restartBefore + 1, 'restart: the coreclr stand-in received no launch request', 60000);
  assert.equal(builds(), buildsBefore + 1, 'F5 must build once');
  assert.deepEqual(launches.at(-1).preLaunchTask, { type: 'runic', command: 'build', name: 'build' }, 'DevKit\'s build must be the resolved configuration\'s preLaunchTask');
  const running = vscode.debug.activeDebugSession;
  assert.equal(running?.type, 'coreclr');
  await vscode.commands.executeCommand('workbench.action.debug.restart');
  await waitFor(() => launches.length === restartBefore + 2, 'Restart: the coreclr stand-in received no second launch request', 60000);
  assert.equal(builds(), buildsBefore + 2, 'Restart must build again');
  assert.deepEqual(launches.at(-1).args, launches.at(-2).args, 'Restart keeps the profile\'s arguments');
  assert.deepEqual(launches.at(-1).args, ['/mock', '/config:C:\\Program Files\\Acme\\dev.json']);
  const stopped = new Promise(resolve => { const listener = vscode.debug.onDidTerminateDebugSession(() => { if (!vscode.debug.activeDebugSession) { listener.dispose(); resolve(); } }); });
  await vscode.debug.stopDebugging();
  await Promise.race([stopped, new Promise(resolve => setTimeout(resolve, 10000))]);
  // A missing profile stops F5 with an error instead of a launch without its arguments.
  stage = 'F5 /mock: missing profile';
  const before = launches.length;
  assert.equal(await vscode.debug.startDebugging(appFolder, { type: 'runic', request: 'launch', name: 'missing', launchProfile: 'Mock' }), false);
  assert.equal(launches.length, before);
  // E2: Run and Debug offers one entry per Window project and profile.
  stage = 'F5 /mock: Run and Debug entries';
  assert.deepEqual(sdk.debugConfigurations(appFolder.uri).map(item => [item.name, item.launchProfile]), [['Runic: App (App)', 'App'], ['Runic: App (Mock data)', 'Mock data']]);
  assert.deepEqual(sdk.debugConfigurations(vscode.workspace.workspaceFolders[0].uri), []);
  fs.rmSync(path.join(app, 'Properties'), { recursive: true });
  assert.deepEqual(sdk.debugConfigurations(appFolder.uri), [{ type: 'runic', request: 'launch', name: 'Runic: App', project: 'App.csproj' }]);
  // Run and Debug asks the dynamic provider for each folder and once without one (the user settings' launch): each
  // entry shows once, under its folder (the 0.24 VM run listed every entry a second time under "user settings").
  assert.deepEqual(sdk.dynamicDebugConfigurations(undefined), []);
  assert.deepEqual(sdk.dynamicDebugConfigurations(appFolder.uri), sdk.debugConfigurations(appFolder.uri));
  const listed = [undefined, ...vscode.workspace.workspaceFolders.map(item => item.uri)].flatMap(uri => sdk.dynamicDebugConfigurations(uri)).map(item => item.name);
  assert.deepEqual(listed, ['Runic: App']);
  fs.writeFileSync(`${sessionFile}.tmp`, JSON.stringify({ ...session, state: 'stopped' })); fs.renameSync(`${sessionFile}.tmp`, sessionFile);
  await earlyDevExit(sdk, appFolder, project, sessionFile, fs, path);
  console.log('PASS Runic F5: /mock from args, the profile, StartArguments and both; launchProfile, cwd, envFile, pass-through; one entry per profile; an early dev exit and the tool restore.');
}
/**
 * F5 when the `dev` it starts exits before it writes a session file: F5 fails at once with dev's exit code and last
 * lines, not after 10 minutes; with the local tools not restored (a fresh clone) it offers the restore, which runs only
 * on a click (D081.4). The stand-in dotnet's `runic dev` exits with 3, or with 1 and dotnet's restore message while
 * tools-missing exists; its `tool restore` removes that file.
 */
async function earlyDevExit(sdk, appFolder, project, sessionFile, fs, path) {
  const app = appFolder.uri.fsPath;
  fs.rmSync(sessionFile, { force: true });
  await waitFor(() => sdk.devState(project).state === 'none', () => `The session file was not removed: ${JSON.stringify(sdk.devState(project))}`, 15000);
  const f5 = async name => {
    const startedAt = Date.now();
    assert.equal(await vscode.debug.startDebugging(appFolder, { type: 'runic', request: 'launch', name }), false, `${name}: F5 must fail`);
    assert.ok(Date.now() - startedAt < 45000, `${name}: F5 took ${Date.now() - startedAt} ms to fail`);
    return sdk.f5Failure();
  };
  stage = 'F5: dev exits before its session (dotnet-runic 0.24, DevKit\'s terminal)';
  fs.writeFileSync(path.join(app, 'tool-version'), '0.24.0\n');
  assert.equal(await f5('early exit'), 'dotnet runic dev exited with code 3 before its session was ready: Restoring App.csproj | error RAPPCLI1001: dev failed early.');
  assert.equal(sdk.toolRestoreOffer(), undefined, 'Restored tools need no restore');
  fs.rmSync(path.join(app, 'tool-version'));
  stage = 'F5: a fresh clone without its local tools offers the restore';
  fs.writeFileSync(path.join(app, 'tools-missing'), '');
  const failure = await f5('tools missing');
  assert.match(failure, /^dotnet runic dev exited (with code 1 )?before its session was ready.* The project's local tools are not restored: choose Restore tools \(dotnet tool restore\), then press F5 again\.$/);
  assert.deepEqual(sdk.toolRestoreOffer(), { project, message: failure });
  assert.ok(fs.existsSync(path.join(app, 'tools-missing')), 'DevKit must not restore by itself');
  stage = 'F5: Restore tools';
  assert.equal(await sdk.restoreTools(project), 0);
  assert.equal(fs.existsSync(path.join(app, 'tools-missing')), false, 'Restore tools did not run dotnet tool restore');
  assert.equal(sdk.toolRestoreOffer(), undefined);
}
/** S8: the dependency tree from the graph file, the fresh-clone offers and the quick fixes of the dependency diagnostics. */
async function frontendDependencies(sdk, appFolder, project, csproj, fs, path) {
  const root = appFolder.uri.fsPath;
  const sharedUi = path.join(root, 'shared-ui');
  stage = 'dependency tree from the graph file (S8)';
  // dev has stopped; the build's graph (rax.frontend-graph/1) says what is on disk.
  const graphFile = path.join(root, 'obj', 'runic', 'frontend-graph.json');
  fs.writeFileSync(graphFile, JSON.stringify({ schema: 'rax.frontend-graph/1', workspaceRoot: root, packageManager: 'bun', lockFile: path.join(root, 'bun.lock'), member: true,
    application: { directory: path.join(root, 'Frontend'), package: 'app-frontend', kind: 'compiled', build: 'bun run --bun build', watch: null, exports: [], generatedExport: null, outputs: [] },
    members: [{ directory: path.join(root, 'Frontend'), package: 'app-frontend' }, { directory: sharedUi, package: '@acme/shared-ui' }],
    packages: [{ directory: sharedUi, package: '@acme/shared-ui', kind: 'compiled', build: 'bun run --bun build', watch: null, exports: ['./dist/index.js'], generatedExport: null, outputs: ['dist'] }] }));
  await waitFor(() => sdk.dependencies(project)?.source === 'graph', () => `The graph file was not read: ${JSON.stringify(sdk.dependencies(project))}`);
  assert.deepEqual(sdk.dependencies(project).dependencies.map(item => [item.package, item.state, item.ownerName]), [['@acme/shared-ui', 'missing-output', 'SharedUi']]);
  const graphRows = (await sdk.devTree()).find(row => row.label === 'Frontend dependencies');
  assert.deepEqual(graphRows.children.map(row => [row.label, row.description]), [['@acme/shared-ui', 'SharedUi · not built']]);
  await waitFor(() => sdk.dependencyStatus()?.command === 'runic.dependencies.build', () => `The status bar must offer the build: ${JSON.stringify(sdk.dependencyStatus())}`);
  assert.equal(sdk.dependencyStatus().text, '$(tools) Build frontend dependencies');
  fs.mkdirSync(path.join(sharedUi, 'dist'));
  fs.writeFileSync(path.join(sharedUi, 'dist', 'index.js'), 'export {};\n');
  assert.equal(sdk.dependencies(project).dependencies[0].state, 'built');
  assert.equal(sdk.dependencyStatus(), undefined, 'A built package needs no offer');
  // dev's build failed in the package (RAPPDEV1018): its earlier dist is on disk, and the tree says failed, not built.
  stage = 'dependency tree: a failed package build of the last dev';
  const brokenSource = path.join(sharedUi, 'src', 'index.ts');
  sdk.devOutput(project, [`${brokenSource}(12,14): error TS2322: Type 'string' is not assignable to type 'number'. [${path.join(root, 'SharedUi', 'SharedUi.csproj')}]`,
    `${project} : error RAPPDEV1018: A frontend package that the page depends on failed to build, so dev did not start: ${brokenSource}(12,14): error TS2322: Type 'string' is not assignable to type 'number'.`], true);
  assert.deepEqual(sdk.dependencies(project).dependencies.map(item => [item.state, item.message]), [['failed', "src/index.ts(12,14): error TS2322: Type 'string' is not assignable to type 'number'."]]);
  assert.deepEqual((await sdk.devTree()).find(row => row.label === 'Frontend dependencies').children.map(row => row.description), ['SharedUi · failed']);
  assert.equal(sdk.dependencyStatus()?.command, 'runic.dependencies.build');
  sdk.devOutput(project, []);
  assert.equal(sdk.dependencies(project).dependencies[0].state, 'built');
  stage = 'fresh clone: install offered, never run (D081.4)';
  fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ private: true, workspaces: ['Frontend', 'shared-ui'], packageManager: 'bun@1.4.4' }, null, 2));
  fs.writeFileSync(path.join(root, 'Frontend', 'package.json'), JSON.stringify({ name: 'app-frontend', private: true, dependencies: { '@acme/shared-ui': 'workspace:*' } }, null, 2));
  sdk.dependencies(project);
  assert.equal(sdk.install(project)?.lockFile, undefined, 'Without a lock file nothing is installed');
  assert.equal(sdk.dependencyStatus(), undefined);
  assert.ok((await sdk.devTree()).some(row => row.label === 'Frontend packages: not installed' && row.description === 'bun · no lock file'));
  fs.writeFileSync(path.join(root, 'bun.lock'), '{}\n');
  sdk.dependencies(project);
  assert.deepEqual(sdk.dependencyStatus(), { text: '$(package) Install frontend dependencies', command: 'runic.dependencies.install', project });
  assert.equal(fs.existsSync(path.join(root, 'node_modules')), false, 'DevKit must not install by itself');
  fs.mkdirSync(path.join(root, 'node_modules'));
  sdk.dependencies(project);
  assert.equal(sdk.dependencyStatus(), undefined);
  stage = 'quick fixes of the dependency diagnostics';
  const diagnostic = (code, message) => { const item = new vscode.Diagnostic(new vscode.Range(0, 0, 0, 0), message, vscode.DiagnosticSeverity.Error); item.code = code; return item; };
  const titles = actions => actions.map(action => action.title);
  const member = path.join(sharedUi, 'package.json');
  const mismatch = sdk.codeActions(csproj, [diagnostic('RUNICBRIDGE089', `${member} declares npm (packageManager), but its workspace ${root} uses bun (packageManager bun@1.4.4 in ${path.join(root, 'package.json')}), so the build installs and builds it with bun. Remove the member's packageManager or make it match the workspace's.`)]);
  assert.equal(mismatch[0].title, `Remove packageManager from ${member} (the workspace uses bun)`);
  assert.equal(mismatch[0].isPreferred, true);
  assert.ok(await vscode.workspace.applyEdit(mismatch[0].edit));
  const memberDocument = await vscode.workspace.openTextDocument(vscode.Uri.file(member));
  assert.equal(JSON.parse(memberDocument.getText()).packageManager, undefined);
  assert.equal(JSON.parse(memberDocument.getText()).name, '@acme/shared-ui');
  await memberDocument.save();
  const missing = sdk.codeActions(csproj, [diagnostic('RUNICBRIDGE088', `${path.join(root, 'Frontend')} depends on @acme/shared-ui (${sharedUi}), the frontend package of SharedUi, but its package.json exports ./dist/index.js, which do not exist: SharedUi's build of the package did not write them. Make its build script write them, or correct exports in its package.json.`)]);
  assert.deepEqual(titles(missing), ['Build frontend dependencies (dotnet build)', "Open @acme/shared-ui's exports", 'Show RUNICBRIDGE088 in the Runic diagnostics catalog']);
  assert.deepEqual(missing[0].command, { title: 'Build frontend dependencies (dotnet build)', command: 'runic.dependencies.build', arguments: [project] });
  await vscode.commands.executeCommand(missing[1].command.command, ...missing[1].command.arguments);
  await waitFor(() => vscode.window.activeTextEditor?.document.uri.fsPath === member, 'Open the exports did not open shared-ui/package.json');
  const lock = sdk.codeActions(csproj, [diagnostic('RUNICBRIDGE026', `The frontend ${path.join(root, 'Frontend')} is a member of the bun workspace at ${root}, which has no bun.lock, so the workspace install would not be locked. Run 'bun install' in ${root} and commit bun.lock, or set RunicApplicationFrontendAllowUnlockedInstall=true to install without one.`)]);
  assert.deepEqual(lock[0].command, { title: `Create bun.lock: run 'bun install --ignore-scripts' in ${root}`, command: 'runic.dependencies.run', arguments: [root, ['bun', 'install', '--ignore-scripts']] });
  const runtime = sdk.codeActions(csproj, [diagnostic('RUNICBRIDGE090', `The frontend workspace ${root} uses bun (packageManager bun@1.4.4 in ${path.join(root, 'package.json')}), and the build runs 'bun' to resolve it, but 'bun' did not run: it is not on PATH. Install Bun 1.4.4 or later, or set RunicApplicationFrontendPackageManager or RunicApplicationFrontendCodegenRuntime.`)]);
  assert.equal(String(runtime[0].command.arguments[0]), 'https://bun.sh/docs/installation');
  const watcher = sdk.codeActions(csproj, [diagnostic('RAPPDEV1018', '[shared-ui] bun run --bun build exited with code 1: src/about-panel.ts(42,31): error TS1109: Expression expected.')]);
  assert.deepEqual(watcher[0].command.arguments, ['shared-ui']);
}
/** Retries a request the server answered with ContentModified: a just-started server may still be reading its documents, or a watched-file change arrived while it was pending. */
async function retryModified(request, attempts = 20, also) {
  for (let attempt = 1; ; attempt++) {
    try { return await request(); }
    catch (error) {
      const message = String(error?.message ?? error);
      if (attempt >= attempts || !(/content changed while the request was pending|ContentModified/i.test(message) || also?.test(message))) throw error;
      console.log(`retrying after: ${message}`);
      await new Promise(resolve => setTimeout(resolve, 250));
    }
  }
}
/**
 * The code actions of a range once they include `title`, or the last ones at the deadline, which the caller's assertion
 * then reports. Right after the previous step's saves the server can still be reading the changed files and list none
 * yet (#39).
 */
async function untilCodeActions(uri, range, kind, title, timeout = 20000) {
  const deadline = Date.now() + timeout;
  for (;;) {
    // A request the server leaves unanswered counts as an empty answer, so a stuck request reaches the assertion too.
    const request = retryModified(() => vscode.commands.executeCommand('vscode.executeCodeActionProvider', uri, range, kind));
    let timer;
    const actions = await Promise.race([request, new Promise(resolve => { timer = setTimeout(() => resolve(undefined), 5000); })]).finally(() => clearTimeout(timer)) ?? [];
    if (actions.some(action => action.title === title) || Date.now() >= deadline) return actions;
    console.log(`${kind}: no '${title}' yet (${JSON.stringify(actions.map(action => action.title))}); asking again`);
    await new Promise(resolve => setTimeout(resolve, 250));
  }
}
/**
 * Runs a light-bulb refactoring and waits for its edit. The extension resolves an action the server rejects as outdated
 * (ContentModified after a watched-file change from the previous step's saves, #9) once more by itself; if that is
 * outdated too it warns (#32). A run that applies nothing must therefore have warned, never fail silently; only then
 * does this run the action again, as a user would, at most three times.
 */
async function untilApplied(api, run, applied, message) {
  for (let attempt = 1; ; attempt++) {
    const warned = api.outdatedActions().length;
    await withinApplyLimit(api, run, applied, message);
    try { return await waitFor(applied, message); }
    catch (error) {
      if (applied()) throw error;
      const warnings = api.outdatedActions().slice(warned);
      assert.ok(warnings.length > 0, `The code action applied nothing and showed no warning: ${error.message}`);
      if (attempt >= 3) throw error;
      console.log(`running the code action again after its warning: ${warnings.join(' ')}`);
    }
  }
}
/**
 * Runs a refactoring's command, failing the stage with its state when the command has not finished after `limit`
 * milliseconds, instead of the runner killing VS Code at 120 s with no report (#40). The state names the stage, the
 * time, the code action resolves since the command started and how each ended, the Runic documents' versions and
 * unsaved changes, and whether the expected change is there. The language client's trace is in the failure artifact
 * (logs/…/runic-artifex.runic-devkit/Runic Translations.log).
 */
async function withinApplyLimit(api, run, applied, message, limit = 15000) {
  const started = Date.now(), resolves = api.resolveLog().filter(entry => entry.started >= started - 1000).length;
  const expired = Symbol('expired');
  let timer;
  try { if (await Promise.race([run(), new Promise(resolve => { timer = setTimeout(() => resolve(expired), limit); })]) !== expired) return; } finally { clearTimeout(timer); }
  const documents = vscode.workspace.textDocuments.filter(document => /\.(rmf2|mf2)$|runic\.json$/.test(document.uri.fsPath))
    .map(document => `${vscode.workspace.asRelativePath(document.uri)} v${document.version}${document.isDirty ? ' (unsaved)' : ''}`);
  let expected;
  try { expected = applied() ? 'applied' : `not applied: ${typeof message === 'function' ? message() : message}`; } catch (error) { expected = `could not read: ${error.message}`; }
  assert.fail([`${stage}: the refactoring's command did not finish within ${Math.round((Date.now() - started) / 1000)} s (#40).`,
    `Code action resolves: ${JSON.stringify(api.resolveLog().filter(entry => entry.started >= started - 1000).map(entry => ({ ...entry, started: entry.started - started })))} (${resolves} of them when the command started)`,
    `Outdated or unlisted warnings: ${JSON.stringify(api.outdatedActions())}; refusals: ${JSON.stringify(api.refusedActions())}; command errors: ${JSON.stringify(api.commandErrors())}`,
    `Documents: ${documents.join(', ')}; active editor: ${vscode.window.activeTextEditor ? vscode.workspace.asRelativePath(vscode.window.activeTextEditor.document.uri) : 'none'}`,
    `Expected change: ${expected}`].join('\n'));
}
async function waitFor(predicate, message, timeout = 10000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) { if (predicate()) return; await new Promise(resolve => setTimeout(resolve, 50)); }
  // A function message reads the state at the timeout, not when the wait started.
  assert.fail(typeof message === 'function' ? message() : message);
}
const execute = exports.run;
exports.run = async () => {
  // The stage reached, for a run that VS Code ends or the runner kills before the assertions report.
  const progress = setInterval(() => require('node:fs').writeFileSync(`${process.env.RUNIC_VSCODE_RESULT_FILE}.stage`, stage), 500);
  try { await execute(); }
  catch (error) { require('node:fs').writeFileSync(process.env.RUNIC_VSCODE_RESULT_FILE, JSON.stringify({ passed: false, message: `${stage}: ${error.stack || String(error)}` })); throw error; }
  finally { clearInterval(progress); }
};

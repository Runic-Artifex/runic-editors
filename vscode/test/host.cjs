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
  // The Runic protocol the server speaks: 1 for Translations 0.13 (the NuGet tool CI restores), 2 for a server that
  // advertises experimental.runic (Translations 0.14). RUNIC_TRANSLATIONS_PROTOCOL makes a local run insist on one.
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
  const inline = await vscode.commands.executeCommand('vscode.executeCodeActionProvider', account, new vscode.Range(1, 0, 1, 0), 'refactor.inline');
  assert.deepEqual(inline.map(action => action.title), ['Inline into ../en.rmf2']);
  const inlined = await vscode.commands.executeCommand('runic.translations.inlineResource');
  assert.equal(inlined.kind, 'refactor.inline');
  await waitFor(() => !fs.existsSync(account.fsPath) && /^account\b/m.test(document.getText()), () => `Inline Resource did not move account/en.rmf2 into en.rmf2 (account exists: ${fs.existsSync(account.fsPath)}): ${JSON.stringify(document.getText())}`);
  assert.ok(document.getText().includes('Your account'));
  assert.equal(await document.save(), true);
  stage = 'protocol 2: extract group code action';
  editor = await vscode.window.showTextDocument(document, { preview: false });
  const group = document.positionAt(/^account\b/m.exec(document.getText()).index);
  editor.selection = new vscode.Selection(group, group);
  const extract = await vscode.commands.executeCommand('vscode.executeCodeActionProvider', en, new vscode.Range(group, group.translate(0, 7)), 'refactor.extract');
  assert.deepEqual(extract.map(action => action.title), ["Extract group 'account' to account/en.rmf2"]);
  const extracted = await vscode.commands.executeCommand('runic.translations.extractGroup');
  assert.equal(extracted.kind, 'refactor.extract');
  // The created file is written through the workspace edit; VS Code need not open it as a document.
  const extractedText = () => vscode.workspace.textDocuments.find(item => item.uri.fsPath === account.fsPath)?.getText() ?? (fs.existsSync(account.fsPath) ? fs.readFileSync(account.fsPath, 'utf8') : '');
  await waitFor(() => extractedText().includes('heading = Your account') && !/^account\b/m.test(document.getText()),
    () => `Extract Group did not move the account group back (account/en.rmf2: ${JSON.stringify(extractedText())}): ${JSON.stringify(document.getText())}`);
  assert.equal(await document.save(), true);
  await vscode.workspace.saveAll(false);
  assert.ok(fs.readFileSync(account.fsPath, 'utf8').includes('heading = Your account'));
  assert.ok(!fs.readFileSync(en.fsPath, 'utf8').includes('Your account'));
  console.log('PASS protocol 2: F2 on an input, Inline Resource and Extract Group code actions, server-applied command edits.');
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
  assert.deepEqual(sdk.workspaceKind(appFolder.uri).sdkProjects, [project]);
  assert.deepEqual(sdk.projects(), [project]);
  assert.equal(sdk.devState(project).state, 'none');
  await waitFor(() => /Runic dev: not started/.test(sdk.devLabel()), () => `Status bar: ${sdk.devLabel()}`);
  stage = 'tasks';
  const tasks = await vscode.tasks.fetchTasks({ type: 'runic' });
  assert.deepEqual(tasks.map(task => task.name).sort(), ['build', 'dev', 'dev --no-window', 'doctor']);
  const dev = tasks.find(task => task.name === 'dev --no-window');
  assert.deepEqual(dev.execution.args, ['runic', 'dev', '--no-window', '--project', project]);
  assert.equal(dev.isBackground, true);
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
  write({ ...session, state: 'ready', frontend: { ...session.frontend, url: 'http://127.0.0.1:5173/', port: 5173 } });
  await waitFor(() => sdk.devState(project).state === 'ready', 'The session file\'s ready state was not followed');
  await waitFor(() => sdk.devLabel().endsWith('Runic dev: ready'), () => `Status bar: ${sdk.devLabel()}`);
  // dotnet runic dev killed: the file keeps "ready", and its process is gone.
  process.kill(pid, 'SIGKILL');
  await waitFor(() => sdk.devState(project).state === 'stopped' && sdk.devState(project).exited === true, 'A dead dotnet runic dev must read as stopped', 15000);
  assert.equal(JSON.parse(fs.readFileSync(sessionFile, 'utf8')).state, 'ready');
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
  console.log('PASS Runic SDK: workspace detection, tasks, session file liveness, ViewModels tree, doctor and code actions.');
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

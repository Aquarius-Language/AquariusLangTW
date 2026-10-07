'use strict';
const assert = require('node:assert/strict');
const vscode = require('vscode');

async function waitFor(check) {
  for (let i = 0; i < 100; i++) {
    const result = await check();
    if (result) return result;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error('Timed out waiting for language features.');
}

async function run() {
  const extension = vscode.extensions.getExtension('aquariuslang.aquariuslang-tw');
  assert.ok(extension);
  await extension.activate();
  const document = await vscode.workspace.openTextDocument({ language: 'aquarius', content: '變數 中文 = 1;\n印出(中文);' });
  const editor = await vscode.window.showTextDocument(document);
  const completion = await waitFor(async () => {
    const result = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, new vscode.Position(1, 0));
    return result?.items.some(item => item.label === '中文') && result;
  });
  assert.ok(completion.items.some(item => item.label === '印出'));
  const definitions = await vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, new vscode.Position(1, 3));
  assert.equal(definitions[0].range.start.line, 0);
  assert.equal(definitions[0].range.start.character, 3);
  const hovers = await vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, new vscode.Position(1, 3));
  assert.ok(hovers.length > 0);
  const symbols = await vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', document.uri);
  assert.ok(symbols.some(symbol => symbol.name === '中文'));
  await editor.edit(edit => edit.delete(new vscode.Range(0, 8, 0, 9)));
  await waitFor(() => vscode.languages.getDiagnostics(document.uri).length > 0);
  await editor.edit(edit => edit.insert(new vscode.Position(0, 8), '2'));
  await waitFor(() => vscode.languages.getDiagnostics(document.uri).length === 0);
  await vscode.commands.executeCommand('aquarius.restartServer');
  await waitFor(async () => (await vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, new vscode.Position(1, 3)))?.length > 0);
  console.log('Aquarius extension host integration passed: completion, hover, definitions, outline, live diagnostics, restart.');
}

module.exports = { run };

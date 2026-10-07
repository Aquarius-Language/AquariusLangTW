'use strict';

const vscode = require('vscode');
const fs = require('node:fs');
const path = require('node:path');
const { LanguageClient, TransportKind } = require('vscode-languageclient/node');

let client;
let output;

async function activate(context) {
  output = vscode.window.createOutputChannel('Aquarius Language Server');
  context.subscriptions.push(output);

  async function start() {
    if (!vscode.workspace.isTrusted) return;
    const config = vscode.workspace.getConfiguration('aquarius');
    const configuredPath = config.get('serverPath', '').trim();
    const serverPath = configuredPath || context.asAbsolutePath(path.join('server', 'AquariusLanguageServer.dll'));
    if (!path.isAbsolute(serverPath) || !fs.existsSync(serverPath)) {
      vscode.window.showErrorMessage('Aquarius language server was not found. Install the packaged VSIX, or set aquarius.serverPath to an absolute path to AquariusLanguageServer.dll.');
      return;
    }
    const executable = { command: config.get('dotnetPath', 'dotnet'), args: [serverPath], transport: TransportKind.stdio };
    client = new LanguageClient('aquarius', 'Aquarius Language Server', executable, {
      documentSelector: [{ scheme: 'file', language: 'aquarius' }, { scheme: 'untitled', language: 'aquarius' }],
      outputChannel: output,
      traceOutputChannel: output
    });
    try {
      await client.start();
    } catch (error) {
      output.appendLine(String(error));
      client = undefined;
      vscode.window.showErrorMessage('Aquarius could not start. Install the .NET 8 runtime, check aquarius.dotnetPath, then run "Aquarius: Restart Language Server". See the Aquarius Language Server output for details.');
    }
  }

  // Serialize restarts so a settings change cannot launch overlapping server processes.
  let pending = Promise.resolve();
  function restart() {
    pending = pending.then(async () => {
      if (client) { await client.stop(); client = undefined; }
      await start();
    }).catch(error => { output.appendLine(String(error)); });
    return pending;
  }
  context.subscriptions.push(vscode.commands.registerCommand('aquarius.restartServer', restart));
  context.subscriptions.push(vscode.workspace.onDidGrantWorkspaceTrust(restart));
  context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(event => {
    if (event.affectsConfiguration('aquarius.dotnetPath') || event.affectsConfiguration('aquarius.serverPath')) void restart();
  }));
  await restart();
}

async function deactivate() {
  if (client) { await client.stop(); client = undefined; }
}

module.exports = { activate, deactivate };

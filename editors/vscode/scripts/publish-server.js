'use strict';
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const extensionRoot = path.join(__dirname, '..');
const project = path.resolve(extensionRoot, '..', '..', 'AquariusLanguageServer', 'AquariusLanguageServer.csproj');
const args = [
  'publish', project, '--configuration', 'Release', '--output', path.join(extensionRoot, 'server'),
  '--self-contained', 'false', '-p:UseAppHost=false', '-p:RollForward=Major', '--nologo'
];
if (process.env.AQUARIUS_NO_RESTORE === '1') args.push('--no-restore');
const result = spawnSync(process.env.AQUARIUS_DOTNET || 'dotnet', args, { stdio: 'inherit', windowsHide: true });
if (result.error) { console.error(result.error.message); process.exit(1); }
process.exit(result.status ?? 1);

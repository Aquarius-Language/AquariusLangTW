'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { test } = require('node:test');
const root = path.resolve(__dirname, '..');
const read = name => JSON.parse(fs.readFileSync(path.join(root, name), 'utf8'));

test('language contributions are valid and reference files that exist', () => {
  const manifest = read('package.json');
  assert.ok(manifest.contributes.languages[0].extensions.includes('.aqua'));
  assert.equal(manifest.contributes.grammars[0].scopeName, 'source.aquarius');
  read(manifest.contributes.languages[0].configuration);
  read(manifest.contributes.grammars[0].path);
  read(manifest.contributes.snippets[0].path);
  assert.ok(manifest.contributes.commands.some(command => command.command === 'aquarius.restartServer'));
});

test('snippet triggers include Chinese and emitted syntax uses Chinese keywords', () => {
  const snippets = read('snippets/aquarius.json');
  assert.ok(snippets.Function.body[0].includes('函式('));
  assert.ok(snippets['For loop'].body[0].includes('迴圈 (變數'));
});

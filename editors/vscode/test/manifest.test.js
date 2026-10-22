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
  assert.ok(snippets['Bilingual function'].body[0].includes(', ${2:englishName} = 函式('));
});

test('English snippet triggers emit English declarations and control flow', () => {
  const snippets = read('snippets/aquarius.json');
  assert.ok(snippets['English variable'].body.startsWith('var '));
  assert.ok(snippets['English function'].body[0].includes('= function('));
  assert.ok(snippets['English bilingual function'].body[0].includes(', ${2:中文名稱} = function('));
  assert.ok(snippets['English conditional'].body.includes('} else {'));
  assert.ok(snippets['English for loop'].body[0].startsWith('for (var '));
});

test('highlighting recognizes both languages without splitting Unicode identifiers', () => {
  const patterns = read('syntaxes/aquarius.tmLanguage.json').patterns;
  for (const [scope, words] of [
    ['keyword.control.aquarius', ['變數','函式','回傳','如果','否則如果','否則','迴圈','中斷','var','function','return','if','elif','else','for','break']],
    ['constant.language.boolean.aquarius', ['真','假','true','false']]
  ]) {
    const expression = new RegExp(patterns.find(pattern => pattern.name === scope).match, 'gu');
    assert.deepEqual([...words.join(' ').matchAll(expression)].map(match => match[0]), words);
    for (const word of words) {
      assert.deepEqual([...(word + '中文 _' + word + ' ' + word + '1 ' + word + '\u0301').matchAll(expression)], []);
    }
  }
});

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
  // Struct declarations: the keyword and the declared type name, only where a declaration starts.
  const struct = new RegExp(patterns.find(pattern => pattern.captures?.['2']?.name === 'entity.name.type.aquarius').match, 'gu');
  assert.deepEqual([...'結構 節點 { 值: 整數 } struct Point{ x }'.matchAll(struct)].map(match => [match[1], match[2]]), [['結構', '節點'], ['struct', 'Point']]);
  assert.deepEqual([...'var struct = 1; 變數 結構 = struct + 1; my結構 X {'.matchAll(struct)], []);
});

test('highlighting covers the core globals, conversions, portable modules and annotated struct types', () => {
  const patterns = read('syntaxes/aquarius.tmLanguage.json').patterns;
  const scoped = (scope, capture) => patterns.filter(pattern => capture ? Object.values(pattern.captures ?? {}).some(c => c.name === scope) : pattern.name === scope)
    .map(pattern => new RegExp(pattern.match, 'gu'));
  const builtins = scoped('support.function.aquarius')[0];
  const globals = ['長度','型別','目前時間','目前日期','是Android','是網頁','區域填色','len','typeOf','currentTime','currentDate','isOSAndroid','isWeb','floodFill'];
  assert.deepEqual([...globals.join(' ').matchAll(builtins)].map(m => m[0]), globals);
  // Conversions are functions only where they are called; after a colon they are types.
  const conversion = scoped('support.function.aquarius')[1];
  assert.deepEqual([...'整數(x) double(y) 浮點數 (z) 變數 a: 整數'.matchAll(conversion)].map(m => m[0]), ['整數', 'double', '浮點數']);
  // Portable modules and their members, in both languages.
  const module = patterns.find(pattern => pattern.captures?.['1']?.name === 'support.class.aquarius');
  const modules = new RegExp(module.match, 'gu');
  assert.deepEqual([...'陣列.排序(a) Array.Reduce(a, f, 0) 雜湊.鍵列表(h) String.Split(s, ",") 執行緒.啟動(f) 我的陣列.x'.matchAll(modules)].map(m => [m[1], m[3]]),
    [['陣列', '排序'], ['Array', 'Reduce'], ['雜湊', '鍵列表'], ['String', 'Split'], ['執行緒', '啟動']]);
  // Annotated declarations and results name built-in types or struct types.
  const declaration = new RegExp(patterns.find(pattern => pattern.comment?.startsWith('An annotated declaration')).match, 'gu');
  assert.deepEqual([...'變數 根: 節點 = x; var n: int[] = []; var p: Point;'.matchAll(declaration)].map(m => [m[1], m[4] ?? null, m[6] ?? null]),
    [['變數', null, '節點'], ['var', 'int', null], ['var', null, 'Point']]);
  const result = new RegExp(patterns.find(pattern => pattern.comment?.startsWith("A function's result")).match, 'gu');
  assert.deepEqual([...'函式(t: 節點): 整數 { } function(): Point { } f(x): 陣列'.matchAll(result)].map(m => m[3] ?? m[5]), ['整數', 'Point', '陣列']);
});

test('the extension has the AquariusLang version of compiler/Directory.Build.props', () => {
  const props = fs.readFileSync(path.join(root, '..', '..', 'compiler', 'Directory.Build.props'), 'utf8');
  const version = /<Version>([^<]+)<\/Version>/.exec(props)[1];
  assert.equal(read('package.json').version, version);
  assert.equal(read('package-lock.json').version, version);
  assert.ok(read('package.json').scripts.package.includes(`aquariuslang-tw-${version}.vsix`));
});

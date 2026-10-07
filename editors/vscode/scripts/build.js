'use strict';
require('esbuild').buildSync({
  entryPoints: [require('node:path').join(__dirname, '..', 'src', 'extension.js')],
  outfile: require('node:path').join(__dirname, '..', 'dist', 'extension.js'),
  bundle: true,
  platform: 'node',
  format: 'cjs',
  target: 'node18',
  external: ['vscode'],
  sourcemap: false,
  minify: false
});

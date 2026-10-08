// Virtual filesystem rules mirror AquariusPackaging. Host paths never enter this resolver.
export function resolvePath(name, current = '') {
  if (typeof name !== 'string' || !name.length) throw new Error('Empty package path');
  name = name.replaceAll('\\', '/');
  const prefix = name.startsWith('/') ? '' : current.slice(0, current.lastIndexOf('/') + 1);
  const out = [];
  for (const part of (prefix + name).split('/')) {
    if (!part || part === '.') continue;
    if (part === '..') {
      if (!out.length) throw new Error('Path escapes website package');
      out.pop();
    } else {
      if (/[\x00-\x1f:*?<>|"\\]/.test(part) || /[ .]$/.test(part) || /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/i.test(part))
        throw new Error('Invalid package path');
      out.push(part);
    }
  }
  if (!out.length) throw new Error('Empty package path');
  return out.join('/');
}

export function lookupPath(files, name, {module = false} = {}) {
  // Ordinal matching uses simple case mappings, not expansions such as ß -> SS.
  const fold = value => Array.from(value, c => {
    const upper = c.toUpperCase();
    return Array.from(upper).length === 1 && (c.codePointAt(0) < 128 || upper.codePointAt(0) >= 128) ? upper : c;
  }).join('');
  const names = Object.keys(files);
  if (module && !/\.(aqua|rius)$/i.test(name)) throw new Error('Packaged imports require an .aqua or .rius path');
  const candidates = [name];
  if (module) candidates.push(name.replace(/\.(aqua|rius)$/i, /\.aqua$/i.test(name) ? '.rius' : '.aqua'));
  for (const candidate of candidates) {
    const found = names.find(key => fold(key) === fold(candidate));
    if (found !== undefined) return found;
  }
  throw new Error(`Bundled ${module ? 'module' : 'asset'} not found: ${name}`);
}

// A small AquariusLang interpreter for the tutorial sandbox.
// It runs the subset the lessons teach (both keyword spellings) so readers can edit
// and run examples in the browser. Real programs compile to WebAssembly with `aqua`.

const KEYWORDS = {
  var: "var", 變數: "var",
  const: "const", 常數: "const",
  function: "function", 函式: "function",
  return: "return", 回傳: "return",
  if: "if", 如果: "if",
  else: "else", 否則: "else",
  elif: "elif", 否則如果: "elif",
  for: "for", 迴圈: "for",
  break: "break", 中斷: "break",
  continue: "continue", 繼續: "continue",
  true: "true", 真: "true",
  false: "false", 假: "false",
  null: "null", 空值: "null",
  struct: "struct", 結構: "struct",
  async: "async", 非同步: "async", 非同步函式: "asyncfn",
  await: "await", 等待: "await",
};

export class AquaError extends Error {
  constructor(message, line) {
    super(message);
    this.line = line;
  }
}

/* ---------------- values ---------------- */
export class Dbl {
  constructor(v, kind = "double") {
    this.v = kind === "float" ? Math.fround(v) : v;
    this.kind = kind;
  }
}
class AHash {
  constructor() {
    this.map = new Map();
  }
  static key(k) {
    if (typeof k === "string") return "s:" + k;
    if (typeof k === "number") return "i:" + k;
    if (k instanceof Dbl) return "d:" + k.v;
    if (typeof k === "boolean") return "b:" + k;
    throw new AquaError("Unusable as hash key: " + typeOf(k));
  }
  get(k) {
    const e = this.map.get(AHash.key(k));
    return e ? e[1] : null;
  }
  set(k, v) {
    this.map.set(AHash.key(k), [k, v]);
  }
}
class AFunc {
  constructor(params, body, env, opts = {}) {
    this.params = params;
    this.body = body;
    this.env = env;
    this.isAsync = !!opts.isAsync;
    this.returnType = opts.returnType || null;
    this.arrowExpr = opts.arrowExpr || null;
  }
}
class Native {
  constructor(fn) {
    this.fn = fn;
  }
}
class StructType {
  constructor(name, fields) {
    this.name = name;
    this.fields = fields;
  }
}
class StructVal {
  constructor(type) {
    this.type = type;
    this.values = new Map();
  }
}
class AModule {
  constructor(name, members) {
    this.name = name;
    this.members = members;
  }
}
class APromise {
  constructor(state, value) {
    this.state = state;
    this.value = value;
  }
}

const isNum = (v) => typeof v === "number" || v instanceof Dbl;
const numVal = (v) => (v instanceof Dbl ? v.v : v);

export function typeOf(v) {
  if (v === null || v === undefined) return "null";
  if (typeof v === "number") return "integer";
  if (v instanceof Dbl) return v.kind;
  if (typeof v === "string") return "string";
  if (typeof v === "boolean") return "boolean";
  if (Array.isArray(v)) return "array";
  if (v instanceof AHash) return "hash";
  if (v instanceof AFunc || v instanceof Native) return "function";
  if (v instanceof StructType) return "type";
  if (v instanceof StructVal) return v.type.name;
  if (v instanceof AModule) return "module";
  if (v instanceof APromise) return "promise";
  return "unknown";
}

function formatDouble(x) {
  if (Number.isNaN(x)) return "NaN";
  if (x === Infinity) return "Infinity";
  if (x === -Infinity) return "-Infinity";
  if (x === 0) return Object.is(x, -0) ? "-0" : "0";
  const [mant, expStr] = x.toExponential().split("e");
  const exp = Number(expStr);
  if (exp >= 15 || exp < -5) {
    const sign = exp < 0 ? "-" : "+";
    return mant + "E" + sign + String(Math.abs(exp)).padStart(2, "0");
  }
  return String(x);
}

export function format(v, nested = false) {
  if (v === null || v === undefined) return "null";
  if (typeof v === "number") return String(v);
  if (v instanceof Dbl) return formatDouble(v.v);
  if (typeof v === "string") return v;
  if (typeof v === "boolean") return v ? "真" : "假";
  if (Array.isArray(v)) return "[" + v.map((x) => format(x, true)).join(", ") + "]";
  if (v instanceof AHash)
    return "{" + [...v.map.values()].map(([k, x]) => format(k, true) + ": " + format(x, true)).join(", ") + "}";
  if (v instanceof StructVal)
    return v.type.name + "{" + v.type.fields.map((f) => f.name + ": " + format(v.values.get(f.name), true)).join(", ") + "}";
  if (v instanceof StructType) return "結構 " + v.name;
  if (v instanceof AFunc || v instanceof Native) return "function";
  if (v instanceof AModule) return "module " + v.name;
  if (v instanceof APromise) return "promise";
  return String(v);
}

/* ---------------- lexer ---------------- */
const IDENT_START = /[\p{L}_]/u;
const IDENT_PART = /[\p{L}\p{N}\p{M}_]/u;
const PUNCT = [">>>=", "...", "<<=", ">>=", ">>>", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "=>", "<<", ">>",
  "+", "-", "*", "/", "%", "<", ">", "=", "!", "(", ")", "{", "}", "[", "]", ",", ";", ":", ".", "&", "|", "^", "~"];

function lex(src) {
  const tokens = [];
  let i = 0, line = 1;
  const chars = [...src];
  const peek = (o = 0) => chars[i + o];
  while (i < chars.length) {
    const c = chars[i];
    if (c === "\n") { line++; i++; continue; }
    if (/\s/u.test(c) || c === "　") { i++; continue; }
    if (c === "#") { while (i < chars.length && chars[i] !== "\n") i++; continue; }
    if (c === '"') {
      let s = "";
      i++;
      while (i < chars.length && chars[i] !== '"') {
        if (chars[i] === "\\" && (chars[i + 1] === '"' || chars[i + 1] === "\\")) { s += chars[i + 1]; i += 2; continue; }
        if (chars[i] === "\n") line++;
        s += chars[i++];
      }
      if (i >= chars.length) throw new AquaError("Unterminated string", line);
      i++;
      tokens.push({ t: "str", v: s, line });
      continue;
    }
    if (/[0-9]/.test(c)) {
      let s = "";
      if (c === "0" && (peek(1) === "x" || peek(1) === "X" || peek(1) === "b" || peek(1) === "B")) {
        const base = peek(1).toLowerCase() === "x" ? 16 : 2;
        i += 2;
        while (i < chars.length && /[0-9a-fA-F_]/.test(chars[i])) s += chars[i++];
        tokens.push({ t: "num", v: parseInt(s.replaceAll("_", ""), base) | 0, line });
        continue;
      }
      while (i < chars.length && /[0-9_]/.test(chars[i])) s += chars[i++];
      let isDbl = false;
      if (chars[i] === "." && /[0-9]/.test(chars[i + 1] || "")) {
        isDbl = true;
        s += chars[i++];
        while (i < chars.length && /[0-9_]/.test(chars[i])) s += chars[i++];
      }
      if ((chars[i] === "e" || chars[i] === "E") && /[0-9+-]/.test(chars[i + 1] || "")) {
        isDbl = true;
        s += chars[i++];
        if (chars[i] === "+" || chars[i] === "-") s += chars[i++];
        while (i < chars.length && /[0-9]/.test(chars[i])) s += chars[i++];
      }
      s = s.replaceAll("_", "");
      let tok;
      if (chars[i] === "f") { i++; tok = new Dbl(Number(s), "float"); }
      else if (chars[i] === "d") { i++; tok = new Dbl(Number(s)); }
      else if (chars[i] === "L") { i++; tok = Number(s); }
      else if (isDbl) tok = new Dbl(Number(s));
      else {
        const n = Number(s);
        if (n > 2147483648) throw new AquaError("Integer literal out of range: " + s, line);
        tok = n;
      }
      tokens.push({ t: "num", v: tok, line });
      continue;
    }
    if (IDENT_START.test(c)) {
      let s = "";
      while (i < chars.length && IDENT_PART.test(chars[i])) s += chars[i++];
      if (s === "else" || s === "否則") {
        // `else if` / `否則 如果` read as one branch keyword
        let j = i;
        while (j < chars.length && /\s/u.test(chars[j])) j++;
        let w = "";
        let k = j;
        while (k < chars.length && IDENT_PART.test(chars[k])) w += chars[k++];
        if (w === "if" || w === "如果") {
          for (let x = i; x < k; x++) if (chars[x] === "\n") line++;
          i = k;
          tokens.push({ t: "kw", v: "elif", line });
          continue;
        }
      }
      if (Object.hasOwn(KEYWORDS, s)) tokens.push({ t: "kw", v: KEYWORDS[s], raw: s, line });
      else tokens.push({ t: "id", v: s, line });
      continue;
    }
    const rest = chars.slice(i, i + 4).join("");
    const p = PUNCT.find((x) => rest.startsWith(x));
    if (!p) throw new AquaError("Unexpected character '" + c + "'", line);
    tokens.push({ t: "op", v: p, line });
    i += [...p].length;
  }
  tokens.push({ t: "eof", v: "end of file", line });
  return tokens;
}

/* ---------------- parser ---------------- */
const PREC = { "||": 1, "&&": 1, "|": 2, "^": 3, "&": 4, "==": 5, "!=": 5, "<": 6, ">": 6, "<=": 6, ">=": 6, "<<": 7, ">>": 7, ">>>": 7, "+": 8, "-": 8, "*": 9, "/": 9, "%": 9 };
const ASSIGN = ["=", "+=", "-=", "*=", "/=", "%="];

class Parser {
  constructor(tokens) {
    this.toks = tokens;
    this.i = 0;
  }
  get cur() { return this.toks[this.i]; }
  peek(o = 1) { return this.toks[this.i + o]; }
  is(t, v) { const c = this.cur; return c.t === t && (v === undefined || c.v === v); }
  isOp(v) { return this.is("op", v); }
  isKw(v) { return this.is("kw", v); }
  next() { return this.toks[this.i++]; }
  expect(t, v) {
    if (!this.is(t, v)) {
      const c = this.cur;
      throw new AquaError(`Expected '${v ?? t}' but found '${c.raw ?? c.v}'`, c.line);
    }
    return this.next();
  }
  ident() {
    const c = this.cur;
    if (c.t === "id") { this.i++; return c.v; }
    // contextual words used as names
    if (c.t === "kw" && ["struct", "async", "await"].includes(c.v)) { this.i++; return c.raw; }
    throw new AquaError(`Expected a name but found '${c.raw ?? c.v}'`, c.line);
  }
  program() {
    const body = [];
    while (!this.is("eof")) body.push(this.statement());
    return body;
  }
  skipSemis() { while (this.isOp(";")) this.next(); }
  statement() {
    const c = this.cur;
    let s;
    if (c.t === "kw") {
      if (c.v === "var" || c.v === "const") s = this.decl();
      else if (c.v === "struct" && this.peek().t === "id") s = this.structDecl();
      else if (c.v === "return") {
        this.next();
        const value = this.isOp(";") || this.isOp("}") ? null : this.expression();
        s = { k: "return", value, line: c.line };
      } else if (c.v === "break" || c.v === "continue") { this.next(); s = { k: c.v, line: c.line }; }
      else if (c.v === "for") { s = this.forStmt(); this.skipSemis(); return s; }
      else if (c.v === "if") { s = { k: "expr", e: this.ifExpr(), line: c.line }; this.skipSemis(); return s; }
    }
    if (!s) s = { k: "expr", e: this.expression(), line: c.line };
    this.skipSemis();
    return s;
  }
  typeAnn() {
    let name = this.ident();
    while (this.isOp("[") && this.peek().t === "op" && this.peek().v === "]") { this.next(); this.next(); name += "[]"; }
    return name;
  }
  decl() {
    const kw = this.next();
    const names = [this.ident()];
    let type = null;
    if (this.isOp(",")) { this.next(); names.push(this.ident()); }
    if (this.isOp(":")) { this.next(); type = this.typeAnn(); }
    let value = null;
    if (this.isOp("=")) { this.next(); value = this.expression(); }
    return { k: "decl", names, type, value, constant: kw.v === "const", line: kw.line };
  }
  structDecl() {
    const kw = this.next();
    const name = this.ident();
    this.expect("op", "{");
    const fields = [];
    while (!this.isOp("}")) {
      const f = { name: this.ident(), type: null, def: null };
      if (this.isOp(":")) { this.next(); f.type = this.typeAnn(); }
      if (this.isOp("=")) { this.next(); f.def = this.expression(); }
      fields.push(f);
      if (this.isOp(";") || this.isOp(",")) this.next();
    }
    this.next();
    return { k: "struct", name, fields, line: kw.line };
  }
  forStmt() {
    const kw = this.next();
    this.expect("op", "(");
    let init = null, cond = null, update = null;
    if (!this.isOp(";")) init = this.isKw("var") ? this.decl() : { k: "expr", e: this.expression() };
    this.expect("op", ";");
    if (!this.isOp(";")) cond = this.expression();
    this.expect("op", ";");
    if (!this.isOp(")")) update = this.expression();
    this.expect("op", ")");
    const body = this.block();
    return { k: "for", init, cond, update, body, line: kw.line };
  }
  block() {
    this.expect("op", "{");
    const body = [];
    while (!this.isOp("}")) {
      if (this.is("eof")) throw new AquaError("Missing '}'", this.cur.line);
      body.push(this.statement());
    }
    this.next();
    return body;
  }
  ifExpr() {
    const kw = this.next();
    this.expect("op", "(");
    const cond = this.expression();
    this.expect("op", ")");
    const then = this.block();
    let otherwise = null;
    if (this.isKw("elif")) otherwise = [{ k: "expr", e: this.ifExpr(), line: this.cur.line }];
    else if (this.isKw("else")) { this.next(); otherwise = this.block(); }
    return { k: "if", cond, then, otherwise, line: kw.line };
  }
  expression() { return this.assignment(); }
  assignment() {
    const left = this.binary(0);
    if (this.is("op") && ASSIGN.includes(this.cur.v)) {
      const op = this.next();
      if (!["id", "index", "member"].includes(left.k)) throw new AquaError("Invalid assignment target", op.line);
      const value = this.assignment();
      return { k: "assign", op: op.v, target: left, value, line: op.line };
    }
    return left;
  }
  binary(minPrec) {
    let left = this.unary();
    for (;;) {
      const c = this.cur;
      if (c.t !== "op" || !(c.v in PREC) || PREC[c.v] <= minPrec - 1 || PREC[c.v] < minPrec) break;
      const prec = PREC[c.v];
      if (prec < minPrec) break;
      this.next();
      const right = this.binary(prec + 1);
      left = { k: c.v === "&&" || c.v === "||" ? "logic" : "bin", op: c.v, left, right, line: c.line };
    }
    return left;
  }
  unary() {
    const c = this.cur;
    if (c.t === "op" && ["-", "!", "~"].includes(c.v)) { this.next(); return { k: "unary", op: c.v, e: this.unary(), line: c.line }; }
    if (c.t === "op" && (c.v === "++" || c.v === "--")) { this.next(); return { k: "incr", op: c.v, prefix: true, target: this.unary(), line: c.line }; }
    if (c.t === "kw" && c.v === "await") { this.next(); return { k: "await", e: this.unary(), line: c.line }; }
    return this.postfix(this.primary());
  }
  args() {
    const args = [], named = [];
    this.expect("op", "(");
    while (!this.isOp(")")) {
      if ((this.is("id")) && this.peek().t === "op" && this.peek().v === ":") {
        const name = this.next().v;
        this.next();
        named.push({ name, value: this.expression() });
      } else {
        if (named.length) throw new AquaError("Positional argument after a named one", this.cur.line);
        args.push(this.expression());
      }
      if (!this.isOp(")")) this.expect("op", ",");
    }
    this.next();
    return { args, named };
  }
  postfix(e) {
    for (;;) {
      const c = this.cur;
      if (c.t !== "op") return e;
      if (c.v === "(") { const a = this.args(); e = { k: "call", fn: e, ...a, line: c.line }; }
      else if (c.v === "[") { this.next(); const index = this.expression(); this.expect("op", "]"); e = { k: "index", obj: e, index, line: c.line }; }
      else if (c.v === ".") {
        this.next();
        const t = this.cur;
        if (t.t !== "id" && t.t !== "kw") throw new AquaError("Expected a member name", t.line);
        this.next();
        e = { k: "member", obj: e, name: t.raw ?? t.v, line: c.line };
      } else if (c.v === "++" || c.v === "--") { this.next(); e = { k: "incr", op: c.v, prefix: false, target: e, line: c.line }; }
      else return e;
    }
  }
  params() {
    const params = [];
    this.expect("op", "(");
    while (!this.isOp(")")) {
      const p = { name: this.ident(), type: null, def: null };
      if (this.isOp(":")) { this.next(); p.type = this.typeAnn(); }
      if (this.isOp("=")) { this.next(); p.def = this.expression(); }
      params.push(p);
      if (!this.isOp(")")) this.expect("op", ",");
    }
    this.next();
    return params;
  }
  funcLit(isAsync) {
    const line = this.cur.line;
    const params = this.params();
    let returnType = null;
    if (this.isOp(":")) { this.next(); returnType = this.typeAnn(); }
    const body = this.block();
    return { k: "func", params, body, isAsync, returnType, line };
  }
  arrowAhead() {
    // from '(' find its matching ')' and check for '=>'
    let depth = 0;
    for (let j = this.i; j < this.toks.length; j++) {
      const t = this.toks[j];
      if (t.t === "op" && (t.v === "(" || t.v === "[" || t.v === "{")) depth++;
      else if (t.t === "op" && (t.v === ")" || t.v === "]" || t.v === "}")) {
        depth--;
        if (depth === 0) { const n = this.toks[j + 1]; return n && n.t === "op" && n.v === "=>"; }
      } else if (t.t === "eof") return false;
    }
    return false;
  }
  arrowBody(params, line) {
    this.expect("op", "=>");
    if (this.isOp("{")) return { k: "func", params, body: this.block(), line };
    const e = this.assignment();
    return { k: "func", params, body: [{ k: "expr", e, line }], arrow: true, line };
  }
  primary() {
    const c = this.cur;
    if (c.t === "num") { this.next(); return { k: "lit", v: c.v }; }
    if (c.t === "str") { this.next(); return { k: "lit", v: c.v }; }
    if (c.t === "id") {
      if (this.peek().t === "op" && this.peek().v === "=>") { this.next(); return this.arrowBody([{ name: c.v, type: null, def: null }], c.line); }
      this.next();
      return { k: "id", name: c.v, line: c.line };
    }
    if (c.t === "kw") {
      switch (c.v) {
        case "true": this.next(); return { k: "lit", v: true };
        case "false": this.next(); return { k: "lit", v: false };
        case "null": this.next(); return { k: "lit", v: null };
        case "function": this.next(); return this.funcLit(false);
        case "asyncfn": this.next(); return this.funcLit(true);
        case "async": this.next(); this.expect("kw", "function"); return this.funcLit(true);
        case "if": return this.ifExpr();
        case "struct": case "await": this.next(); return { k: "id", name: c.raw, line: c.line };
      }
      throw new AquaError(`Unexpected '${c.raw}'`, c.line);
    }
    if (c.t === "op") {
      if (c.v === "(") {
        if (this.arrowAhead()) { const params = this.params(); return this.arrowBody(params, c.line); }
        this.next();
        const e = this.expression();
        this.expect("op", ")");
        return e;
      }
      if (c.v === "[") {
        this.next();
        const items = [];
        while (!this.isOp("]")) {
          if (this.isOp("...")) { this.next(); items.push({ spread: this.expression() }); }
          else items.push({ e: this.expression() });
          if (!this.isOp("]")) this.expect("op", ",");
        }
        this.next();
        return { k: "array", items, line: c.line };
      }
      if (c.v === "{") {
        this.next();
        const pairs = [];
        while (!this.isOp("}")) {
          const key = this.expression();
          this.expect("op", ":");
          pairs.push([key, this.expression()]);
          if (!this.isOp("}")) this.expect("op", ",");
        }
        this.next();
        return { k: "hash", pairs, line: c.line };
      }
    }
    throw new AquaError(`Unexpected '${c.raw ?? c.v}'`, c.line);
  }
}

/* ---------------- runtime ---------------- */
class Env {
  constructor(parent) {
    this.parent = parent;
    this.vars = new Map();
  }
  lookup(name) {
    for (let e = this; e; e = e.parent) if (e.vars.has(name)) return e.vars.get(name);
    return null;
  }
}
const BREAK = Symbol("break"), CONTINUE = Symbol("continue");
class ReturnSignal {
  constructor(value) { this.value = value; }
}

const TYPE_ALIASES = { int: "int", 整數: "int", float: "float", 浮點數: "float", double: "double", 雙精度: "double", bool: "bool", 布林: "bool", string: "string", 字串: "string", array: "array", 陣列: "array", long: "int", 長整數: "int" };

function toInt(v) {
  const x = numVal(v);
  if (Number.isNaN(x)) return 0;
  if (x >= 2147483647) return 2147483647;
  if (x <= -2147483648) return -2147483648;
  return Math.trunc(x);
}

const wrap = (n) => n | 0;

function arith(op, a, b, line) {
  if (op === "+" && (typeof a === "string" || typeof b === "string")) return format(a) + format(b);
  if (!isNum(a) || !isNum(b)) throw new AquaError(`Cannot apply '${op}' to ${typeOf(a)} and ${typeOf(b)}`, line);
  if (typeof a === "number" && typeof b === "number") {
    switch (op) {
      case "+": return wrap(a + b);
      case "-": return wrap(a - b);
      case "*": return Math.imul(a, b);
      case "/": if (b === 0) throw new AquaError("Division by zero", line); return wrap(Math.trunc(a / b));
      case "%": if (b === 0) throw new AquaError("Division by zero", line); return wrap(a % b);
      case "&": return a & b;
      case "|": return a | b;
      case "^": return a ^ b;
      case "<<": return a << b;
      case ">>": return a >> b;
      case ">>>": return wrap(a >>> b);
    }
  }
  const kind = (a instanceof Dbl && a.kind === "double") || (b instanceof Dbl && b.kind === "double") ? "double" : "float";
  const x = numVal(a), y = numVal(b);
  switch (op) {
    case "+": return new Dbl(x + y, kind);
    case "-": return new Dbl(x - y, kind);
    case "*": return new Dbl(x * y, kind);
    case "/": return new Dbl(x / y, kind);
    case "%": return new Dbl(x % y, kind);
  }
  throw new AquaError(`Operator '${op}' needs integers`, line);
}

function equals(a, b) {
  if (isNum(a) && isNum(b)) return numVal(a) === numVal(b);
  if (a === undefined) a = null;
  if (b === undefined) b = null;
  return a === b;
}

function compare(op, a, b, line) {
  let x = a, y = b;
  if (isNum(a) && isNum(b)) { x = numVal(a); y = numVal(b); }
  else if (!(typeof a === "string" && typeof b === "string")) throw new AquaError(`Cannot compare ${typeOf(a)} and ${typeOf(b)}`, line);
  switch (op) {
    case "<": return x < y;
    case ">": return x > y;
    case "<=": return x <= y;
    case ">=": return x >= y;
  }
}

function deepCopy(v) {
  if (Array.isArray(v)) return v.map(deepCopy);
  if (v instanceof AHash) { const h = new AHash(); for (const [k, x] of v.map.values()) h.set(k, deepCopy(x)); return h; }
  return v;
}

export class Interpreter {
  constructor({ print, maxSteps = 3_000_000 } = {}) {
    this.print = print || (() => {});
    this.maxSteps = maxSteps;
    this.steps = 0;
    this.globals = new Env(null);
    this.installBuiltins();
  }

  tick(line) {
    if (++this.steps > this.maxSteps) throw new AquaError("Stopped: the program ran too long (infinite loop?)", line);
  }

  def(names, value) {
    for (const n of names) this.globals.vars.set(n, { value, constant: false, type: null });
  }

  installBuiltins() {
    const self = this;
    const N = (fn) => new Native(fn);
    const mod = (name, entries) => {
      const m = new Map();
      for (const [names, v] of entries) for (const n of names) m.set(n, v);
      return new AModule(name, m);
    };
    const need = (cond, msg) => { if (!cond) throw new AquaError(msg); };
    const arr = (a, fn) => { need(Array.isArray(a), `${fn} needs an array`); return a; };
    const hash = (h, fn) => { need(h instanceof AHash, `${fn} needs a hash`); return h; };
    const str = (s, fn) => { need(typeof s === "string", `${fn} needs a string`); return s; };

    this.def(["print", "印出"], N((...a) => { self.print(a.map((x) => format(x)).join(" ")); return null; }));
    this.def(["len", "長度"], N((v) => {
      if (typeof v === "string") return v.length;
      if (Array.isArray(v)) return v.length;
      if (v instanceof AHash) return v.map.size;
      throw new AquaError("len needs a string, array or hash");
    }));
    this.def(["typeOf", "型別"], N((v) => typeOf(v)));
    this.def(["int", "整數"], N((v) => { need(isNum(v) || typeof v === "boolean", "int() needs a number"); return toInt(v === true ? 1 : v === false ? 0 : v); }));
    this.def(["double", "雙精度"], N((v) => new Dbl(numVal(v))));
    this.def(["float", "浮點數"], N((v) => new Dbl(numVal(v), "float")));
    this.def(["currentTime", "目前時間"], N(() => new Dbl(performance.now())));
    this.def(["currentDate", "目前日期"], N(() => { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`; }));
    this.def(["arityOf", "參數個數"], N((f) => (f instanceof AFunc ? f.params.length : -1)));
    this.def(["import", "匯入"], N((name) => {
      throw new AquaError(`import("${name}") runs with the aqua CLI; this practice sandbox has only the core modules`);
    }));

    const call = (f, args) => self.callFunction(f, args, [], 0);
    const cmpSort = (f) => (x, y) => {
      if (f) return numVal(call(f, [x, y]));
      if (isNum(x) && isNum(y)) return numVal(x) - numVal(y);
      return x < y ? -1 : x > y ? 1 : 0;
    };
    const reduceNum = (a, pick) => (a.length ? a.reduce((m, x) => (pick(numVal(x), numVal(m)) ? x : m)) : null);

    this.def(["Array", "陣列"], mod("Array", [
      [["Make", "建立"], N((n, v = null) => Array.from({ length: n }, () => v))],
      [["Len", "長度"], N((a) => arr(a, "Len").length)],
      [["Append", "追加"], N((a, v) => { arr(a, "Append").push(v); return a; })],
      [["AppendArray", "追加陣列"], N((a, b) => { arr(a, "AppendArray").push(...arr(b, "AppendArray")); return a; })],
      [["Clone", "克隆"], N((a) => [...arr(a, "Clone")])],
      [["Slice", "切片"], N((a, s, e) => arr(a, "Slice").slice(s, e))],
      [["Fill", "填入"], N((a, v) => arr(a, "Fill").fill(v))],
      [["Delete", "刪除"], N((a, s, e) => { arr(a, "Delete").splice(s, e - s); return a; })],
      [["Reverse", "反轉"], N((a) => arr(a, "Reverse").reverse())],
      [["Index", "尋找"], N((a, v) => arr(a, "Index").findIndex((x) => equals(x, v)))],
      [["Contains", "包含"], N((a, v) => arr(a, "Contains").some((x) => equals(x, v)))],
      [["Sort", "排序"], N((a, f = null) => arr(a, "Sort").sort(cmpSort(f)))],
      [["Sum", "總和"], N((a) => arr(a, "Sum").reduce((s, x) => arith("+", s, x), 0))],
      [["Min", "最小值"], N((a) => reduceNum(arr(a, "Min"), (x, m) => x < m))],
      [["Max", "最大值"], N((a) => reduceNum(arr(a, "Max"), (x, m) => x > m))],
      [["Map", "映射"], N((a, f) => arr(a, "Map").map((x) => call(f, [x])))],
      [["Filter", "篩選"], N((a, f) => arr(a, "Filter").filter((x) => call(f, [x]) === true))],
      [["Reduce", "歸約"], N((a, f, init) => arr(a, "Reduce").reduce((acc, x) => call(f, [acc, x]), init))],
    ]));
    this.def(["Hash", "雜湊"], mod("Hash", [
      [["Keys", "鍵列表"], N((h) => [...hash(h, "Keys").map.values()].map((e) => e[0]))],
      [["Values", "值列表"], N((h) => [...hash(h, "Values").map.values()].map((e) => e[1]))],
      [["Entries", "項目列表"], N((h) => [...hash(h, "Entries").map.values()].map((e) => [e[0], e[1]]))],
      [["Has", "包含鍵"], N((h, k) => hash(h, "Has").map.has(AHash.key(k)))],
      [["Get", "取得"], N((h, k, d = null) => { const e = hash(h, "Get").map.get(AHash.key(k)); return e ? e[1] : d; })],
      [["Delete", "刪除鍵"], N((h, k) => hash(h, "Delete").map.delete(AHash.key(k)))],
      [["Len", "長度"], N((h) => hash(h, "Len").map.size)],
      [["Clone", "克隆"], N((h) => { const c = new AHash(); for (const [k, v] of hash(h, "Clone").map.values()) c.set(k, v); return c; })],
      [["Clear", "清空"], N((h) => { hash(h, "Clear").map.clear(); return h; })],
      [["Merge", "合併"], N((d, s) => { for (const [k, v] of hash(s, "Merge").map.values()) hash(d, "Merge").set(k, v); return d; })],
    ]));
    this.def(["String", "字串"], mod("String", [
      [["From", "轉文字"], N((v) => format(v))],
      [["Slice", "切片"], N((s, a, b) => str(s, "Slice").slice(a, b))],
      [["Find", "尋找"], N((s, p) => str(s, "Find").indexOf(p))],
      [["Contains", "包含"], N((s, p) => str(s, "Contains").includes(p))],
      [["StartsWith", "開頭是"], N((s, p) => str(s, "StartsWith").startsWith(p))],
      [["EndsWith", "結尾是"], N((s, p) => str(s, "EndsWith").endsWith(p))],
      [["Split", "分割"], N((s, sep) => (sep === "" ? [...str(s, "Split")] : str(s, "Split").split(sep)))],
      [["Join", "合併"], N((a, sep) => arr(a, "Join").map((x) => format(x)).join(sep))],
      [["Replace", "取代"], N((s, a, b) => str(s, "Replace").replaceAll(a, b))],
      [["Trim", "修剪"], N((s) => str(s, "Trim").trim())],
      [["Upper", "轉大寫"], N((s) => str(s, "Upper").toUpperCase())],
      [["Lower", "轉小寫"], N((s) => str(s, "Lower").toLowerCase())],
      [["Repeat", "重複"], N((s, n) => str(s, "Repeat").repeat(n))],
      [["Code", "字元碼"], N((s, i) => { const c = str(s, "Code").codePointAt(i); return c === undefined ? null : c; })],
      [["FromCode", "從字元碼"], N((c) => String.fromCodePoint(c))],
      [["ParseInt", "解析整數"], N((s) => (/^\s*[+-]?\d+\s*$/.test(s) ? toInt(Number(s)) : null))],
    ]));
    const dblFn = (f) => N((...a) => new Dbl(f(...a.map(numVal))));
    const sameType = (f) => N((x) => (typeof x === "number" ? x : new Dbl(f(numVal(x)), x.kind)));
    const minmax = (pick) => N((a, b) => (typeof a === "number" && typeof b === "number" ? pick(a, b) : new Dbl(pick(numVal(a), numVal(b)))));
    this.def(["Math", "數學"], mod("Math", [
      [["Abs", "絕對值"], N((x) => (typeof x === "number" ? wrap(Math.abs(x)) : new Dbl(Math.abs(x.v), x.kind)))],
      [["Min", "最小值"], minmax(Math.min)],
      [["Max", "最大值"], minmax(Math.max)],
      [["Floor", "向下取整"], sameType(Math.floor)],
      [["Ceil", "向上取整"], sameType(Math.ceil)],
      [["Round", "四捨五入"], sameType((x) => Math.sign(x) * Math.round(Math.abs(x)))],
      [["RoundInt", "四捨五入取整數"], N((x) => toInt(Math.sign(numVal(x)) * Math.round(Math.abs(numVal(x)))))],
      [["Sqrt", "平方根"], dblFn(Math.sqrt)],
      [["Sin", "正弦"], dblFn(Math.sin)],
      [["Cos", "餘弦"], dblFn(Math.cos)],
      [["Pow", "次方"], dblFn(Math.pow)],
      [["Hypot", "斜邊長"], dblFn(Math.hypot)],
      [["Random", "亂數"], N(() => new Dbl(Math.random()))],
      [["RandomInt", "亂數整數"], N((a, b) => a + Math.floor(Math.random() * (b - a)))],
      [["PI", "圓周率"], new Dbl(Math.PI)],
    ]));
    const settle = (fn) => { try { return new APromise("fulfilled", fn()); } catch (e) { return new APromise("rejected", e.message); } };
    this.def(["Promise", "承諾"], mod("Promise", [
      [["Delay", "延遲"], N((ms, v = null) => new APromise("fulfilled", v))],
      [["Resolve", "解決"], N((v = null) => (v instanceof APromise ? v : new APromise("fulfilled", v)))],
      [["Reject", "拒絕"], N((r = null) => new APromise("rejected", r))],
      [["All", "全部"], N((a) => settle(() => arr(a, "All").map((p) => self.awaitValue(p))))],
      [["IsPromise", "是承諾"], N((v) => v instanceof APromise)],
    ]));
    const handles = [];
    this.def(["Thread", "執行緒"], mod("Thread", [
      [["Start", "啟動"], N((f, args = []) => { handles.push(settle(() => deepCopy(call(f, deepCopy(args))))); return handles.length - 1; })],
      [["Wait", "等待"], N(() => true)],
      [["Result", "結果"], N((h) => self.awaitValue(handles[h]))],
      [["IsDone", "是否完成"], N(() => true)],
      [["Close", "關閉"], N(() => true)],
      [["Sleep", "休眠"], N(() => true)],
    ]));
  }

  awaitValue(v) {
    if (!(v instanceof APromise)) return v;
    if (v.state === "rejected") throw new AquaError(format(v.value));
    return v.value;
  }

  checkType(v, type, line, what) {
    if (!type) return v;
    const t = TYPE_ALIASES[type];
    const fail = () => { throw new AquaError(`${what} expects ${type} but got ${typeOf(v)}`, line); };
    if (t === "int") return typeof v === "number" ? v : fail();
    if (t === "double" || t === "float") return isNum(v) ? (typeof v === "number" || v.kind !== t ? new Dbl(numVal(v), t) : v) : fail();
    if (t === "bool") return typeof v === "boolean" ? v : fail();
    if (t === "string") return typeof v === "string" ? v : fail();
    if (t === "array" || type.endsWith("[]")) return Array.isArray(v) ? v : fail();
    const st = this.globals.lookup(type);
    if (st && st.value instanceof StructType) return v === null || (v instanceof StructVal && v.type === st.value) ? v : fail();
    return v;
  }

  run(source) {
    const program = new Parser(lex(source)).program();
    this.block(program, this.globals);
  }

  block(stmts, env) {
    let last = null;
    for (const s of stmts) {
      last = this.exec(s, env);
    }
    return last;
  }

  exec(s, env) {
    this.tick(s.line);
    switch (s.k) {
      case "expr": return this.eval(s.e, env);
      case "decl": {
        let value = s.value ? this.eval(s.value, env) : null;
        value = this.checkType(value, s.type, s.line, `'${s.names[0]}'`);
        for (const n of s.names) env.vars.set(n, { value, constant: s.constant, type: s.type });
        return null;
      }
      case "struct": {
        const fields = s.fields.map((f) => ({ ...f, defValue: f.def ? this.eval(f.def, env) : undefined }));
        env.vars.set(s.name, { value: new StructType(s.name, fields), constant: true, type: null });
        return null;
      }
      case "return": throw new ReturnSignal(s.value ? this.eval(s.value, env) : null);
      case "break": throw BREAK;
      case "continue": throw CONTINUE;
      case "for": return this.forLoop(s, env);
    }
    throw new AquaError("Unknown statement", s.line);
  }

  forLoop(s, env) {
    let loopEnv = new Env(env);
    if (s.init) this.exec(s.init, loopEnv);
    for (;;) {
      this.tick(s.line);
      if (s.cond) {
        const c = this.eval(s.cond, loopEnv);
        if (c !== true) break;
      }
      try {
        this.block(s.body, new Env(loopEnv));
      } catch (e) {
        if (e === BREAK) break;
        if (e !== CONTINUE) throw e;
      }
      // a fresh environment per iteration keeps closures' own values
      const nextEnv = new Env(env);
      for (const [k, v] of loopEnv.vars) nextEnv.vars.set(k, { ...v });
      loopEnv = nextEnv;
      if (s.update) this.eval(s.update, loopEnv);
    }
    return null;
  }

  binding(name, env, line) {
    for (let e = env; e; e = e.parent) if (e.vars.has(name)) return e.vars.get(name);
    throw new AquaError(`Identifier not found: ${name}`, line);
  }

  eval(e, env) {
    switch (e.k) {
      case "lit": return e.v;
      case "id": return this.binding(e.name, env, e.line).value;
      case "array": {
        const out = [];
        for (const it of e.items) {
          if (it.spread) out.push(...this.eval(it.spread, env));
          else out.push(this.eval(it.e, env));
        }
        return out;
      }
      case "hash": {
        const h = new AHash();
        for (const [k, v] of e.pairs) h.set(this.eval(k, env), this.eval(v, env));
        return h;
      }
      case "func":
        return new AFunc(e.params, e.body, env, { isAsync: e.isAsync, returnType: e.returnType });
      case "if": {
        const c = this.eval(e.cond, env);
        if (typeof c !== "boolean") throw new AquaError(`Condition must be a boolean, got ${typeOf(c)}`, e.line);
        if (c) return this.block(e.then, new Env(env));
        if (e.otherwise) return this.block(e.otherwise, new Env(env));
        return null;
      }
      case "logic": {
        const l = this.eval(e.left, env);
        if (typeof l !== "boolean") throw new AquaError(`'${e.op}' needs booleans`, e.line);
        if (e.op === "&&" ? !l : l) return l;
        const r = this.eval(e.right, env);
        if (typeof r !== "boolean") throw new AquaError(`'${e.op}' needs booleans`, e.line);
        return r;
      }
      case "bin": {
        const a = this.eval(e.left, env), b = this.eval(e.right, env);
        if (e.op === "==") return equals(a, b);
        if (e.op === "!=") return !equals(a, b);
        if (["<", ">", "<=", ">="].includes(e.op)) return compare(e.op, a, b, e.line);
        return arith(e.op, a, b, e.line);
      }
      case "unary": {
        const v = this.eval(e.e, env);
        if (e.op === "!") { if (typeof v !== "boolean") throw new AquaError("'!' needs a boolean", e.line); return !v; }
        if (e.op === "~") return ~v;
        if (typeof v === "number") return wrap(-v);
        if (v instanceof Dbl) return new Dbl(-v.v, v.kind);
        throw new AquaError(`Cannot negate ${typeOf(v)}`, e.line);
      }
      case "await": return this.awaitValue(this.eval(e.e, env));
      case "assign": return this.assign(e, env);
      case "incr": {
        const old = this.eval(e.target, env);
        if (!isNum(old)) throw new AquaError(`'${e.op}' needs a number`, e.line);
        const nv = arith(e.op === "++" ? "+" : "-", old, 1, e.line);
        const kept = old instanceof Dbl ? new Dbl(numVal(nv), old.kind) : nv;
        this.store(e.target, kept, env, e.line);
        return e.prefix ? kept : old;
      }
      case "index": {
        const o = this.eval(e.obj, env), i = this.eval(e.index, env);
        if (Array.isArray(o)) { if (typeof i !== "number") throw new AquaError("Array index must be an integer", e.line); const v = o[i]; return v === undefined ? null : v; }
        if (o instanceof AHash) return o.get(i);
        if (typeof o === "string") { const v = o[i]; return v === undefined ? null : v; }
        throw new AquaError(`Cannot index ${typeOf(o)}`, e.line);
      }
      case "member": {
        const o = this.eval(e.obj, env);
        return this.member(o, e.name, e.line);
      }
      case "call": return this.call(e, env);
    }
    throw new AquaError("Unknown expression", e.line);
  }

  member(o, name, line) {
    if (o instanceof AModule) {
      if (!o.members.has(name)) throw new AquaError(`${o.name} has no member '${name}'`, line);
      return o.members.get(name);
    }
    if (o instanceof StructVal) {
      if (!o.values.has(name)) throw new AquaError(`${o.type.name} has no field '${name}'`, line);
      return o.values.get(name);
    }
    if (o instanceof APromise) {
      const map = { State: "state", 狀態: "state", Value: "value", 值: "value", IsDone: "done", 是否完成: "done" };
      if (map[name] === "state") return o.state;
      if (map[name] === "value") return o.value;
      if (map[name] === "done") return true;
    }
    if (o === null) throw new AquaError("Cannot access member of NULL", line);
    throw new AquaError(`Cannot read '${name}' of ${typeOf(o)}`, line);
  }

  store(target, value, env, line) {
    if (target.k === "id") {
      const b = this.binding(target.name, env, line);
      if (b.constant) throw new AquaError(`'${target.name}' is a constant and cannot be assigned`, line);
      b.value = this.checkType(value, b.type, line, `'${target.name}'`);
      return;
    }
    if (target.k === "index") {
      const o = this.eval(target.obj, env), i = this.eval(target.index, env);
      if (Array.isArray(o)) {
        if (typeof i !== "number" || i < 0 || i >= o.length) throw new AquaError(`Array index ${format(i)} out of range`, line);
        o[i] = value;
      } else if (o instanceof AHash) o.set(i, value);
      else throw new AquaError(`Cannot index ${typeOf(o)}`, line);
      return;
    }
    if (target.k === "member") {
      const o = this.eval(target.obj, env);
      if (!(o instanceof StructVal)) throw new AquaError(`Cannot set '${target.name}' on ${typeOf(o)}`, line);
      const f = o.type.fields.find((x) => x.name === target.name);
      if (!f) throw new AquaError(`${o.type.name} has no field '${target.name}'`, line);
      o.values.set(target.name, this.checkType(value, f.type, line, `${o.type.name}.${f.name}`));
    }
  }

  assign(e, env) {
    let value = this.eval(e.value, env);
    if (e.op !== "=") {
      const old = this.eval(e.target, env);
      value = arith(e.op[0], old, value, e.line);
      if (typeof old === "number" && value instanceof Dbl) value = toInt(value);
      else if (old instanceof Dbl && isNum(value)) value = new Dbl(numVal(value), old.kind);
    }
    this.store(e.target, value, env, e.line);
    return value;
  }

  call(e, env) {
    const fn = this.eval(e.fn, env);
    const args = e.args.map((a) => this.eval(a, env));
    const named = e.named.map((n) => ({ name: n.name, value: this.eval(n.value, env) }));
    return this.callFunction(fn, args, named, e.line);
  }

  callFunction(fn, args, named = [], line = 0) {
    this.tick(line);
    if (fn instanceof Native) {
      try {
        return fn.fn(...args);
      } catch (err) {
        if (err instanceof AquaError && !err.line) err.line = line;
        throw err;
      }
    }
    if (fn instanceof StructType) {
      const v = new StructVal(fn);
      fn.fields.forEach((f, i) => {
        let val = i < args.length ? args[i] : undefined;
        const n = named.find((x) => x.name === f.name);
        if (n) val = n.value;
        if (val === undefined) val = f.defValue !== undefined ? deepCopy(f.defValue) : null;
        v.values.set(f.name, val === null ? null : this.checkType(val, f.type, line, `${fn.name}.${f.name}`));
      });
      for (const n of named) if (!fn.fields.some((f) => f.name === n.name)) throw new AquaError(`${fn.name} has no field '${n.name}'`, line);
      return v;
    }
    if (!(fn instanceof AFunc)) throw new AquaError(`Not a function: ${typeOf(fn)}`, line);
    if (args.length > fn.params.length) throw new AquaError(`Expected ${fn.params.length} arguments but got ${args.length}`, line);
    const run = () => {
      const local = new Env(fn.env);
      fn.params.forEach((p, i) => {
        let v = i < args.length ? args[i] : undefined;
        const n = named.find((x) => x.name === p.name);
        if (n) v = n.value;
        if (v === undefined) {
          if (!p.def) throw new AquaError(`Missing argument '${p.name}'`, line);
          v = this.eval(p.def, local);
        }
        local.vars.set(p.name, { value: this.checkType(v, p.type, line, `parameter '${p.name}'`), constant: false, type: p.type });
      });
      let result;
      try {
        result = this.block(fn.body, local);
      } catch (sig) {
        if (sig instanceof ReturnSignal) result = sig.value;
        else throw sig;
      }
      return this.checkType(result, fn.returnType, line, "return value");
    };
    if (fn.isAsync) {
      try { return new APromise("fulfilled", run()); }
      catch (err) { if (err instanceof AquaError) return new APromise("rejected", err.message); throw err; }
    }
    return run();
  }
}

export function runAqua(source, { maxSteps } = {}) {
  const lines = [];
  const interp = new Interpreter({ print: (s) => lines.push(s), maxSteps });
  try {
    interp.run(source);
    return { output: lines.join("\n"), error: null };
  } catch (err) {
    if (err === BREAK || err === CONTINUE) return { output: lines.join("\n"), error: { message: "break/continue outside a loop" } };
    if (err instanceof ReturnSignal) return { output: lines.join("\n"), error: null };
    if (err instanceof AquaError) return { output: lines.join("\n"), error: { message: err.message, line: err.line } };
    if (err instanceof RangeError) return { output: lines.join("\n"), error: { message: "Stack overflow: recursion too deep" } };
    throw err;
  }
}

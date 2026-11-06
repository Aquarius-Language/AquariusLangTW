const KW = "變數|常數|函式|回傳|如果|否則如果|否則|迴圈|中斷|繼續|結構|非同步|等待|真|假|空值|var|const|function|return|if|elif|else|for|break|continue|struct|async|await|true|false|null";
const BUILTIN = "印出|長度|型別|匯入|整數|雙精度|浮點數|布林|字串|陣列|雜湊|數學|承諾|執行緒|目前時間|print|len|typeOf|import|int|double|float|bool|string|array|Array|Hash|String|Math|Promise|Thread|currentTime";

const AQUA = new RegExp(
  `("(?:\\\\.|[^"\\\\\\n])*"|#.*$|\\b\\d[\\d_]*(?:\\.\\d+)?(?:[eE][+-]?\\d+)?[dfL]?\\b|(?<![\\p{L}\\p{N}_])(?:${KW})(?![\\p{L}\\p{N}_])|(?<![\\p{L}\\p{N}_])(?:${BUILTIN})(?![\\p{L}\\p{N}_])|=>|[{}()[\\]])`,
  "gu",
);
const KW_SET = new Set(KW.split("|"));

export const escapeHtml = (text) =>
  text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");

function aquaLine(line) {
  let out = "", last = 0;
  for (const m of line.matchAll(AQUA)) {
    out += escapeHtml(line.slice(last, m.index));
    const t = m[0];
    const type = t.startsWith("#") ? "comment" : t.startsWith('"') ? "string" : /^\d/.test(t) ? "number" : KW_SET.has(t) ? "keyword" : /^[{}()[\]]$|^=>$/.test(t) ? "punct" : "builtin";
    out += `<span class="tk-${type}">${escapeHtml(t)}</span>`;
    last = m.index + t.length;
  }
  return out + escapeHtml(line.slice(last));
}

function shellLine(line) {
  if (/^\s*#/.test(line)) return `<span class="tk-comment">${escapeHtml(line)}</span>`;
  return escapeHtml(line)
    .replace(/^(\s*)(aqua)\b/, '$1<span class="tk-keyword">$2</span>')
    .replace(/(\s)(--?[\w-]+)/g, '$1<span class="tk-builtin">$2</span>')
    .replace(/(&quot;[^&]*&quot;)/g, '<span class="tk-string">$1</span>')
    .replace(/^(\[[^\]]+\])$/, '<span class="tk-keyword">$1</span>')
    .replace(/^([\w_]+)(\s*=)/, '<span class="tk-builtin">$1</span>$2');
}

export function highlight(code, language = "aqua") {
  const fn = language === "aqua" ? aquaLine : shellLine;
  return code.split("\n").map((l) => fn(l) || " ").join("\n");
}

// Shared language preference for the home page and the tutorial.
const KEY = "aqua-lang";

export function storedLang() {
  try {
    const v = localStorage.getItem(KEY);
    return v === "en" || v === "zh" ? v : null;
  } catch {
    return null;
  }
}

export function urlLang() {
  const v = new URLSearchParams(location.search).get("lang");
  return v === "en" || v === "zh" ? v : null;
}

export function browserLang() {
  return (navigator.languages || [navigator.language || "en"]).some((l) => /^zh/i.test(l)) ? "zh" : "en";
}

export function rememberLang(lang) {
  try {
    localStorage.setItem(KEY, lang);
  } catch {
    /* private mode: the URL still carries the choice */
  }
}

// Puts ?lang= in the address bar without reloading, keeping the hash.
export function writeUrlLang(lang) {
  const url = new URL(location.href);
  url.searchParams.set("lang", lang);
  history.replaceState(history.state, "", url);
}

export function withLang(href, lang) {
  const [path, hash] = href.split("#");
  const sep = path.includes("?") ? "&" : "?";
  return `${path}${sep}lang=${lang}${hash !== undefined ? "#" + hash : ""}`;
}

export function applyDocumentLang(lang) {
  document.documentElement.lang = lang === "zh" ? "zh-Hant" : "en";
  document.documentElement.dataset.lang = lang;
}

import "./tutorial.css";
import { chapters, lessons, publicLibraries, publicShowcases } from "./lessons.js";
import { runAqua } from "./aqua.js";
import { highlight, escapeHtml } from "./highlight.js";
import { storedLang, urlLang, browserLang, rememberLang, writeUrlLang, withLang, applyDocumentLang } from "../i18n.js";
import { createStarfield } from "./starfield.js";
import { createOrbitPreview } from "./preview.js";

const ORG = "https://github.com/Aquarius-Language";
const REPO = ORG + "/AquariusLangTW";
const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;

let lang = urlLang() || storedLang() || browserLang();
applyDocumentLang(lang);

const UI = {
  zh: {
    title: "星泉互動教學 — 從第一行到銀河",
    home: "回到首頁",
    tutorial: "互動教學",
    chapters: "章節地圖",
    close: "關閉",
    progress: (n, t) => `已完成 ${n} / ${t}`,
    lessonOf: (i, t) => `第 ${i} 課 · 共 ${t} 課`,
    chapter: (i) => `第 ${i} 章`,
    run: "執行",
    reset: "還原",
    running: "執行中…",
    output: "輸出",
    expected: "預期輸出",
    emptyOutput: "（沒有輸出）",
    runHint: "按「執行」或 Ctrl + Enter 執行程式",
    errorLine: (l) => `⚠ 第 ${l} 行：`,
    error: "⚠ ",
    codeIn: "程式碼語言",
    codeZh: "中文",
    codeEn: "English",
    editable: "可編輯 · 試著修改看看",
    readonly: "需要 aqua 命令列 · 僅供閱讀",
    copy: "複製",
    copied: "已複製程式碼",
    tryIt: "小挑戰",
    quiz: "星光測驗",
    correct: "答對了！",
    wrong: "再想想看。",
    prev: "上一課",
    next: "下一課",
    finish: "回到起點",
    start: "開始旅程",
    resume: "繼續上次進度",
    welcomeKicker: "AQUARIUSLANG INTERACTIVE TUTORIAL",
    welcomeTitle: "從第一道星光，<br><span>航向整片銀河。</span>",
    welcomeText: "十五堂互動課程，從印出第一行字到發佈跨平台應用程式。每一課都能直接在瀏覽器中修改、執行，並用小測驗確認理解。",
    welcomeMeta: ["15 堂課", "4 個章節", "瀏覽器內練習", "中英雙語程式碼"],
    jump: "隨時跳到任何一課",
    lessonsCount: (n) => `${n} 堂課`,
    keys: "← → 切換課程",
    sandbox: "練習區使用簡化的瀏覽器直譯器，涵蓋本教學用到的核心語法；真正的星泉程式會編譯成 WebAssembly。",
    previewLabel: "即時預覽",
    ecosystemTitle: "公開的函式庫與工具",
    showcaseTitle: "用星泉做的作品",
    graduateTitle: "恭喜完成旅程！",
    graduateText: "你已經走過星泉的核心語法與生態系。下載編譯器，寫下屬於你的第一個作品吧。",
    download: "下載星泉",
    readme: "閱讀完整文件",
    lang: "English",
    langLabel: "Switch to English",
    skip: "跳至課程內容",
  },
  en: {
    title: "AquariusLang Interactive Tutorial — from first line to galaxy",
    home: "Back to home",
    tutorial: "Tutorial",
    chapters: "Chapter map",
    close: "Close",
    progress: (n, t) => `${n} / ${t} complete`,
    lessonOf: (i, t) => `Lesson ${i} of ${t}`,
    chapter: (i) => `Chapter ${i}`,
    run: "Run",
    reset: "Reset",
    running: "Running…",
    output: "Output",
    expected: "Expected output",
    emptyOutput: "(no output)",
    runHint: "Press Run or Ctrl + Enter to run the program",
    errorLine: (l) => `⚠ Line ${l}: `,
    error: "⚠ ",
    codeIn: "Code language",
    codeZh: "中文",
    codeEn: "English",
    editable: "Editable · try changing it",
    readonly: "Needs the aqua CLI · read only",
    copy: "Copy",
    copied: "Code copied",
    tryIt: "Mini challenge",
    quiz: "Starlight quiz",
    correct: "Correct!",
    wrong: "Not quite. Try again.",
    prev: "Previous",
    next: "Next",
    finish: "Back to start",
    start: "Start the journey",
    resume: "Resume where you left off",
    welcomeKicker: "AQUARIUSLANG INTERACTIVE TUTORIAL",
    welcomeTitle: "From first light<br><span>to the whole galaxy.</span>",
    welcomeText: "Fifteen interactive lessons take you from printing your first line to shipping apps on every platform. Edit and run every example right in your browser, then check yourself with a quick quiz.",
    welcomeMeta: ["15 lessons", "4 chapters", "Practice in the browser", "Code in English or 中文"],
    jump: "Jump to any lesson, any time",
    lessonsCount: (n) => `${n} lessons`,
    keys: "← → to switch lessons",
    sandbox: "The practice area uses a small in-browser interpreter that covers the core syntax this tutorial teaches. Real AquariusLang programs compile to WebAssembly.",
    previewLabel: "Live preview",
    ecosystemTitle: "Public libraries and tools",
    showcaseTitle: "Made with AquariusLang",
    graduateTitle: "You made it!",
    graduateText: "You've toured AquariusLang's core language and its ecosystem. Download the compiler and write something of your own.",
    download: "Download AquariusLang",
    readme: "Read the full docs",
    lang: "繁體中文",
    langLabel: "切換為繁體中文",
    skip: "Skip to lesson",
  },
};
const t = () => UI[lang];

/* ---------------- progress ---------------- */
const store = {
  get() {
    try {
      return JSON.parse(localStorage.getItem("aqua-tutorial") || "{}");
    } catch {
      return {};
    }
  },
  set(v) {
    try {
      localStorage.setItem("aqua-tutorial", JSON.stringify(v));
    } catch {
      /* ignore */
    }
  },
};
let progress = Object.assign({ done: [], last: null, codeLang: null }, store.get());
const markDone = (id) => {
  if (!progress.done.includes(id)) progress.done.push(id);
  store.set(progress);
  renderMap();
  renderProgress();
};

let codeLang = progress.codeLang || lang;

/* ---------------- shell ---------------- */
const icons = {
  star: '<svg viewBox="0 0 40 40" fill="none" aria-hidden="true"><path d="m20 2 4.3 13.7L38 20l-13.7 4.3L20 38l-4.3-13.7L2 20l13.7-4.3z" fill="currentColor"/><circle cx="20" cy="20" r="3" fill="#080e11"/></svg>',
  arrowL: '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M19 12H5m6-6-6 6 6 6" stroke="currentColor" stroke-width="1.6"/></svg>',
  arrowR: '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M5 12h14m-6-6 6 6-6 6" stroke="currentColor" stroke-width="1.6"/></svg>',
  out: '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M6 18 18 6M6 6h12v12" stroke="currentColor" stroke-width="1.5"/></svg>',
  play: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 5v14l12-7z" fill="currentColor"/></svg>',
  reset: '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M5 9a7 7 0 1 1 0 7M5 4v5h5" stroke="currentColor" stroke-width="1.6"/></svg>',
  copy: '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><rect x="8" y="8" width="11" height="12" rx="2" stroke="currentColor" stroke-width="1.4"/><path d="M15 8V4H4v12h4" stroke="currentColor" stroke-width="1.4"/></svg>',
  map: '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><circle cx="5" cy="6" r="2" stroke="currentColor" stroke-width="1.4"/><circle cx="18" cy="9" r="2" stroke="currentColor" stroke-width="1.4"/><circle cx="9" cy="18" r="2" stroke="currentColor" stroke-width="1.4"/><path d="m7 7 9 2M16.5 10.5 10.5 16.5" stroke="currentColor" stroke-width="1.2"/></svg>',
  check: '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="m5 12 5 5 9-10" stroke="currentColor" stroke-width="2"/></svg>',
};

const app = document.querySelector("#tutorial");
app.innerHTML = `
  <canvas id="starfield" aria-hidden="true"></canvas>
  <a class="skip-link" href="#stage"></a>
  <header class="t-header">
    <a class="t-brand" id="home-link" href="./">${icons.star}<span><b>星泉</b><small>AQUARIUSLANG</small></span></a>
    <span class="t-divider" aria-hidden="true"></span>
    <span class="t-section" id="t-section"></span>
    <div class="t-progress" aria-hidden="true"><i id="progress-bar"></i></div>
    <span class="t-progress-text" id="progress-text"></span>
    <div class="t-actions">
      <button class="t-lang" id="lang-toggle"></button>
      <button class="t-map-toggle" id="map-toggle" aria-expanded="false" aria-controls="map">${icons.map}<span></span></button>
    </div>
  </header>
  <div class="t-body">
    <nav class="t-map" id="map"></nav>
    <div class="map-scrim" id="map-scrim" hidden></div>
    <main class="t-stage" id="stage" tabindex="-1"></main>
  </div>
  <div class="chapter-card" id="chapter-card" aria-hidden="true"></div>
  <div class="t-toast" id="t-toast" role="status" aria-live="polite"></div>
`;

const stage = document.querySelector("#stage");
const starfield = createStarfield(document.querySelector("#starfield"), { reduceMotion });
let activePreview = null;

function renderChrome() {
  document.title = t().title;
  document.querySelector(".skip-link").textContent = t().skip;
  document.querySelector("#home-link").href = withLang("./", lang);
  document.querySelector("#home-link").setAttribute("aria-label", t().home);
  document.querySelector("#t-section").textContent = t().tutorial;
  const toggle = document.querySelector("#lang-toggle");
  toggle.textContent = t().lang;
  toggle.setAttribute("aria-label", t().langLabel);
  toggle.lang = lang === "zh" ? "en" : "zh-Hant";
  document.querySelector("#map-toggle span").textContent = t().chapters;
  renderProgress();
  renderMap();
}

function renderProgress() {
  const n = progress.done.length;
  document.querySelector("#progress-text").textContent = t().progress(n, lessons.length);
  document.querySelector("#progress-bar").style.width = `${(n / lessons.length) * 100}%`;
}

function renderMap() {
  const current = currentId();
  document.querySelector("#map").innerHTML =
    `<p class="map-title">${t().jump}</p>` +
    `<a class="map-welcome ${current === "welcome" ? "current" : ""}" href="#welcome">${icons.star}<span>${lang === "zh" ? "旅程起點" : "Start here"}</span></a>` +
    chapters
      .map((ch, ci) => {
        const items = lessons
          .map((l, i) => ({ l, i }))
          .filter(({ l }) => l.chapter === ci)
          .map(
            ({ l, i }) =>
              `<li><a href="#${l.id}" class="${l.id === current ? "current" : ""} ${progress.done.includes(l.id) ? "done" : ""}" ${l.id === current ? 'aria-current="page"' : ""}><span class="map-dot">${progress.done.includes(l.id) ? icons.check : String(i + 1).padStart(2, "0")}</span><span>${l.title[lang]}</span></a></li>`,
          )
          .join("");
        return `<section class="map-chapter" style="--hue:${ch.hue}"><h2><span class="map-glyph">${ch.glyph}</span><span><small>${t().chapter(ci + 1)} · ${ch.level[lang]}</small>${ch.title[lang]}</span></h2><ol>${items}</ol></section>`;
      })
      .join("") +
    `<p class="map-keys">${t().keys}</p>`;
}

/* ---------------- views ---------------- */
function welcomeView() {
  const resumeId = progress.last && lessons.some((l) => l.id === progress.last) ? progress.last : null;
  return `
  <section class="welcome">
    <div class="welcome-orb" aria-hidden="true"><span></span><span></span><span></span>${icons.star}</div>
    <p class="kicker">${t().welcomeKicker}</p>
    <h1>${t().welcomeTitle}</h1>
    <p class="welcome-text">${t().welcomeText}</p>
    <div class="welcome-cta">
      <a class="t-button primary" href="#${lessons[0].id}">${t().start} ${icons.arrowR}</a>
      ${resumeId ? `<a class="t-button ghost" href="#${resumeId}">${t().resume}: ${lessons.find((l) => l.id === resumeId).title[lang]}</a>` : ""}
    </div>
    <ul class="welcome-meta">${t().welcomeMeta.map((m) => `<li>${m}</li>`).join("")}</ul>
    <div class="chapter-grid">
      ${chapters
        .map((ch, ci) => {
          const ls = lessons.filter((l) => l.chapter === ci);
          return `<a class="chapter-tile" href="#${ls[0].id}" style="--hue:${ch.hue}"><span class="tile-glyph">${ch.glyph}</span><small>${t().chapter(ci + 1)} · ${ch.level[lang]}</small><strong>${ch.title[lang]}</strong><span class="tile-lessons">${ls.map((l) => l.title[lang]).join(" · ")}</span><span class="tile-count">${t().lessonsCount(ls.length)} ${icons.arrowR}</span></a>`;
        })
        .join("")}
    </div>
  </section>`;
}

function codePanel(lesson) {
  const runnable = lesson.runnable !== false;
  const language = lesson.language || "aqua";
  return `
  <div class="code-card ${runnable ? "runnable" : ""}">
    <div class="code-top">
      <div class="dots" aria-hidden="true"><i></i><i></i><i></i></div>
      <span class="code-file">${language === "aqua" ? (lesson.id + ".aqua") : "terminal"}</span>
      ${language === "aqua" ? `<div class="seg" role="group" aria-label="${t().codeIn}"><button data-code-lang="zh" aria-pressed="${codeLang === "zh"}">${t().codeZh}</button><button data-code-lang="en" aria-pressed="${codeLang === "en"}">${t().codeEn}</button></div>` : ""}
      <button class="icon-btn" id="copy-code" aria-label="${t().copy}">${icons.copy}</button>
    </div>
    <div class="editor"><div class="editor-inner">
      <pre class="editor-highlight" aria-hidden="true"><code id="code-highlight"></code></pre>
      <textarea id="code-input" spellcheck="false" autocapitalize="off" autocomplete="off" wrap="off" aria-label="${lesson.title[lang]}" ${runnable ? "" : "readonly"}></textarea>
    </div></div>
    <div class="code-bar">
      <span class="code-hint">${runnable ? t().editable : t().readonly}</span>
      ${runnable ? `<button class="t-button ghost small" id="reset-code">${icons.reset}${t().reset}</button><button class="t-button primary small" id="run-code">${icons.play}${t().run}</button>` : ""}
    </div>
    ${
      runnable || lesson.output
        ? `<div class="console ${runnable ? "" : "static"}"><span class="console-label" id="console-label">${runnable ? t().output : t().expected}</span><pre id="console-output" aria-live="polite">${runnable ? `<span class="muted">${t().runHint}</span>` : escapeHtml(lesson.output[codeLang])}</pre></div>`
        : ""
    }
  </div>`;
}

function lessonView(lesson, index) {
  const ch = chapters[lesson.chapter];
  const prev = lessons[index - 1];
  const next = lessons[index + 1];
  const done = progress.done.includes(lesson.id);
  return `
  <article class="lesson" style="--hue:${ch.hue}">
    <header class="lesson-head">
      <p class="lesson-meta"><span class="chip">${ch.glyph} ${t().chapter(lesson.chapter + 1)} · ${ch.level[lang]}</span><span>${t().lessonOf(index + 1, lessons.length)}</span>${done ? `<span class="chip done">${icons.check}</span>` : ""}</p>
      <h1>${lesson.title[lang]}</h1>
      <p class="tagline">${lesson.tagline[lang]}</p>
    </header>
    <div class="lesson-grid">
      <div class="lesson-text">
        <div class="prose">${lesson.body[lang]}</div>
        ${lesson.tryIt ? `<aside class="try"><strong>${t().tryIt}</strong><p>${lesson.tryIt[lang]}</p></aside>` : ""}
        ${lesson.sandboxNote ? `<p class="note">${lesson.sandboxNote[lang]}</p>` : ""}
      </div>
      <div class="lesson-code">
        ${codePanel(lesson)}
        ${lesson.preview ? `<figure class="preview"><canvas id="preview-canvas" width="640" height="400"></canvas><figcaption>${t().previewLabel}</figcaption></figure>` : ""}
        ${lesson.extra ? `<div class="code-card extra"><div class="code-top"><span class="code-file">${lesson.extra.title[lang]}</span></div><pre class="static-code">${highlight(lesson.extra.code, "toml")}</pre></div>` : ""}
      </div>
    </div>
    ${quizView(lesson)}
    ${lesson.ecosystem ? ecosystemView() : ""}
    <nav class="lesson-nav" aria-label="${lang === "zh" ? "課程導覽" : "Lesson navigation"}">
      ${prev ? `<a class="nav-card prev" href="#${prev.id}">${icons.arrowL}<span><small>${t().prev}</small>${prev.title[lang]}</span></a>` : `<a class="nav-card prev" href="#welcome">${icons.arrowL}<span><small>${t().prev}</small>${lang === "zh" ? "旅程起點" : "Start here"}</span></a>`}
      ${next ? `<a class="nav-card next" href="#${next.id}"><span><small>${t().next}</small>${next.title[lang]}</span>${icons.arrowR}</a>` : `<a class="nav-card next" href="#welcome"><span><small>${t().finish}</small>${lang === "zh" ? "旅程起點" : "Start here"}</span>${icons.arrowR}</a>`}
    </nav>
  </article>`;
}

function quizView(lesson) {
  const q = lesson.quiz;
  if (!q) return "";
  return `
  <section class="quiz" aria-labelledby="quiz-title">
    <h2 id="quiz-title"><span>✦</span>${t().quiz}</h2>
    <div class="quiz-q">${q.q[lang]}</div>
    <div class="quiz-options" role="group">
      ${q.options.map((o, i) => `<button class="quiz-option" data-option="${i}"><span class="opt-key">${"ABC"[i]}</span><span>${o[lang]}</span></button>`).join("")}
    </div>
    <p class="quiz-feedback" id="quiz-feedback" aria-live="polite"></p>
  </section>`;
}

function ecosystemView() {
  return `
  <section class="ecosystem">
    <h2>${t().ecosystemTitle}</h2>
    <div class="eco-grid">
      ${publicLibraries
        .map(
          (l, i) =>
            `<a class="eco-card" style="--i:${i}" href="${ORG}/${l.name}" target="_blank" rel="noopener noreferrer"><small>${l.kind[lang]}</small><strong>${l.name}</strong><span>${escapeHtml(l.desc[lang]).replace(/`([^`]+)`/g, "<code>$1</code>")}</span>${icons.out}</a>`,
        )
        .join("")}
    </div>
    <h2>${t().showcaseTitle}</h2>
    <div class="showcase-row">
      ${publicShowcases.map((s) => `<a href="${ORG}/${s.name}" target="_blank" rel="noopener noreferrer">${s.label[lang]} ${icons.out}</a>`).join("")}
    </div>
    <div class="graduate">
      <div class="graduate-star" aria-hidden="true">${icons.star}</div>
      <h2>${t().graduateTitle}</h2>
      <p>${t().graduateText}</p>
      <div class="welcome-cta">
        <a class="t-button primary" href="${REPO}/releases" target="_blank" rel="noopener noreferrer">${t().download} ${icons.out}</a>
        <a class="t-button ghost" href="${lang === "zh" ? REPO + "/blob/main/README.tw.md" : REPO + "#readme"}" target="_blank" rel="noopener noreferrer">${t().readme} ${icons.out}</a>
      </div>
    </div>
  </section>`;
}

/* ---------------- behaviour ---------------- */
let currentLesson = null;

function setCode(lesson) {
  const input = document.querySelector("#code-input");
  if (!input) return;
  // Only AquariusLang code has a language toggle; shell snippets follow the page.
  const shown = (lesson.language || "aqua") === "aqua" ? codeLang : lang;
  const code = lesson.code[shown] ?? lesson.code[lang];
  input.value = code;
  syncEditor();
  const out = document.querySelector("#console-output");
  if (out && lesson.runnable === false && lesson.output) out.textContent = lesson.output[shown];
}

function syncEditor() {
  const input = document.querySelector("#code-input");
  const hl = document.querySelector("#code-highlight");
  if (!input || !hl) return;
  hl.innerHTML = highlight(input.value + (input.value.endsWith("\n") ? " " : ""), currentLesson.language || "aqua");
}

function runCode() {
  const input = document.querySelector("#code-input");
  const out = document.querySelector("#console-output");
  const card = document.querySelector(".code-card.runnable");
  if (!input || !out) return;
  const { output, error } = runAqua(input.value);
  let html = escapeHtml(output);
  if (error) html += (html ? "\n" : "") + `<span class="err">${escapeHtml((error.line ? t().errorLine(error.line) : t().error) + error.message)}</span>`;
  if (!html) html = `<span class="muted">${t().emptyOutput}</span>`;
  out.innerHTML = html;
  card.classList.remove("ran");
  void card.offsetWidth;
  card.classList.add("ran");
  starfield.pulse();
}

function bindLesson(lesson) {
  const input = document.querySelector("#code-input");
  if (input) {
    setCode(lesson);
    input.addEventListener("input", syncEditor);
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
        e.preventDefault();
        runCode();
      } else if (e.key === "Tab" && !e.shiftKey && !input.readOnly) {
        e.preventDefault();
        input.setRangeText("    ", input.selectionStart, input.selectionEnd, "end");
        syncEditor();
      }
    });
  }
  document.querySelector("#run-code")?.addEventListener("click", runCode);
  document.querySelector("#reset-code")?.addEventListener("click", () => {
    setCode(lesson);
    document.querySelector("#console-output").innerHTML = `<span class="muted">${t().runHint}</span>`;
  });
  document.querySelectorAll("[data-code-lang]").forEach((b) =>
    b.addEventListener("click", () => {
      codeLang = b.dataset.codeLang;
      progress.codeLang = codeLang;
      store.set(progress);
      document.querySelectorAll("[data-code-lang]").forEach((x) => x.setAttribute("aria-pressed", String(x === b)));
      const ed = document.querySelector(".editor");
      ed.classList.remove("swap");
      void ed.offsetWidth;
      ed.classList.add("swap");
      setCode(lesson);
    }),
  );
  document.querySelector("#copy-code")?.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(document.querySelector("#code-input").value);
      toast(t().copied);
    } catch {
      document.querySelector("#code-input").select();
    }
  });
  document.querySelectorAll(".quiz-option").forEach((b) =>
    b.addEventListener("click", () => {
      const i = Number(b.dataset.option);
      const fb = document.querySelector("#quiz-feedback");
      const right = i === lesson.quiz.answer;
      b.classList.add(right ? "right" : "wrong");
      if (right) {
        document.querySelectorAll(".quiz-option").forEach((x) => (x.disabled = true));
        fb.innerHTML = `<strong>${t().correct}</strong> ${lesson.quiz.explain[lang]}`;
        fb.className = "quiz-feedback right";
        burst(b);
        markDone(lesson.id);
      } else {
        fb.innerHTML = t().wrong;
        fb.className = "quiz-feedback wrong";
      }
    }),
  );
  if (lesson.preview) {
    activePreview = createOrbitPreview(document.querySelector("#preview-canvas"), { reduceMotion });
  }
}

function burst(el) {
  if (reduceMotion) return;
  const r = el.getBoundingClientRect();
  for (let i = 0; i < 14; i++) {
    const s = document.createElement("span");
    s.className = "spark";
    s.textContent = i % 3 ? "✦" : "✧";
    const a = (Math.PI * 2 * i) / 14;
    s.style.left = r.left + r.width / 2 + "px";
    s.style.top = r.top + r.height / 2 + "px";
    s.style.setProperty("--dx", Math.cos(a) * (60 + Math.random() * 50) + "px");
    s.style.setProperty("--dy", Math.sin(a) * (40 + Math.random() * 40) + "px");
    document.body.appendChild(s);
    setTimeout(() => s.remove(), 900);
  }
  starfield.pulse(1.6);
}

let toastTimer;
function toast(msg) {
  const el = document.querySelector("#t-toast");
  el.textContent = msg;
  el.classList.add("visible");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove("visible"), 2200);
}

/* ---------------- routing + transitions ---------------- */
function currentId() {
  const id = decodeURIComponent(location.hash.slice(1));
  return lessons.some((l) => l.id === id) ? id : "welcome";
}
let shownId = null;
let shownIndex = -1;

function render(id) {
  if (activePreview) {
    activePreview.stop();
    activePreview = null;
  }
  const index = lessons.findIndex((l) => l.id === id);
  currentLesson = lessons[index] || null;
  stage.innerHTML = currentLesson ? lessonView(currentLesson, index) : welcomeView();
  stage.dataset.view = currentLesson ? "lesson" : "welcome";
  if (currentLesson) {
    bindLesson(currentLesson);
    progress.last = id;
    store.set(progress);
  }
  renderMap();
  shownId = id;
  shownIndex = index;
}

function showChapterCard(ch, ci) {
  if (reduceMotion) return;
  const card = document.querySelector("#chapter-card");
  card.style.setProperty("--hue", ch.hue);
  card.innerHTML = `<span class="cc-glyph">${ch.glyph}</span><small>${t().chapter(ci + 1)} · ${ch.level[lang]}</small><strong>${ch.title[lang]}</strong>`;
  card.classList.remove("show");
  void card.offsetWidth;
  card.classList.add("show");
}

function navigate(id, { initial = false } = {}) {
  if (id === shownId) return;
  const index = lessons.findIndex((l) => l.id === id);
  const forward = index > shownIndex;
  const oldChapter = shownIndex >= 0 ? lessons[shownIndex].chapter : -1;
  const newChapter = index >= 0 ? lessons[index].chapter : -1;
  closeMap();
  if (initial || reduceMotion) {
    render(id);
    return;
  }
  document.documentElement.dataset.dir = forward ? "forward" : "back";
  starfield.warp(forward ? 1 : -1, newChapter !== oldChapter && newChapter >= 0 ? chapters[newChapter].hue : null);
  if (newChapter !== oldChapter && newChapter >= 0) showChapterCard(chapters[newChapter], newChapter);
  const swap = () => {
    render(id);
    window.scrollTo({ top: 0, behavior: "instant" });
    stage.scrollTop = 0;
  };
  if (document.startViewTransition) {
    const vt = document.startViewTransition(swap);
    vt.finished.finally(() => stage.focus({ preventScroll: true }));
  } else {
    stage.classList.add("leaving");
    setTimeout(() => {
      swap();
      stage.classList.remove("leaving");
      stage.classList.add("entering");
      setTimeout(() => stage.classList.remove("entering"), 600);
      stage.focus({ preventScroll: true });
    }, 260);
  }
}

window.addEventListener("hashchange", () => navigate(currentId()));
document.addEventListener("keydown", (e) => {
  if (e.target.closest("textarea, input")) return;
  if (e.altKey || e.ctrlKey || e.metaKey) return;
  const i = lessons.findIndex((l) => l.id === currentId());
  if (e.key === "ArrowRight") {
    const next = lessons[i + 1];
    if (next) location.hash = next.id;
  } else if (e.key === "ArrowLeft") {
    location.hash = i > 0 ? lessons[i - 1].id : "welcome";
  } else if (e.key === "Escape") closeMap();
});

/* ---------------- map drawer (narrow screens) ---------------- */
const mapToggle = document.querySelector("#map-toggle");
const scrim = document.querySelector("#map-scrim");
function closeMap() {
  document.body.classList.remove("map-open");
  mapToggle.setAttribute("aria-expanded", "false");
  scrim.hidden = true;
}
mapToggle.addEventListener("click", () => {
  const open = !document.body.classList.contains("map-open");
  document.body.classList.toggle("map-open", open);
  mapToggle.setAttribute("aria-expanded", String(open));
  scrim.hidden = !open;
});
scrim.addEventListener("click", closeMap);

/* ---------------- language ---------------- */
document.querySelector("#lang-toggle").addEventListener("click", () => {
  lang = lang === "zh" ? "en" : "zh";
  codeLang = lang;
  progress.codeLang = null;
  store.set(progress);
  rememberLang(lang);
  writeUrlLang(lang);
  applyDocumentLang(lang);
  const swap = () => {
    renderChrome();
    render(currentId());
  };
  if (document.startViewTransition && !reduceMotion) {
    document.documentElement.dataset.dir = "lang";
    document.startViewTransition(swap);
  } else swap();
});

writeUrlLang(lang);
renderChrome();
navigate(currentId(), { initial: true });

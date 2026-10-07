import "./style.css";

const repo = "https://github.com/Aquarius-Language/AquariusLangTW";
const arrow =
  '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M5 12h14m-6-6 6 6-6 6" stroke="currentColor" stroke-width="1.5"/></svg>';
const outward =
  '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M6 18 18 6M6 6h12v12" stroke="currentColor" stroke-width="1.5"/></svg>';
const star =
  '<svg viewBox="0 0 40 40" fill="none" aria-hidden="true"><path d="m20 2 4.3 13.7L38 20l-13.7 4.3L20 38l-4.3-13.7L2 20l13.7-4.3z" fill="currentColor"/><circle cx="20" cy="20" r="3" fill="#080e11"/></svg>';
const github =
  '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 2a10 10 0 0 0-3.16 19.49c.5.09.68-.22.68-.48v-1.86c-2.78.6-3.37-1.18-3.37-1.18-.46-1.16-1.11-1.47-1.11-1.47-.91-.62.07-.61.07-.61 1 .07 1.53 1.03 1.53 1.03.89 1.52 2.34 1.08 2.91.83.09-.65.35-1.09.63-1.34-2.22-.25-4.55-1.11-4.55-4.95 0-1.09.39-1.98 1.03-2.68-.1-.25-.45-1.27.1-2.65 0 0 .84-.27 2.75 1.02A9.6 9.6 0 0 1 12 6.82c.85 0 1.71.11 2.51.33 1.91-1.29 2.75-1.02 2.75-1.02.55 1.38.2 2.4.1 2.65.64.7 1.03 1.59 1.03 2.68 0 3.85-2.34 4.69-4.57 4.94.36.31.68.92.68 1.85v2.76c0 .27.18.58.69.48A10 10 0 0 0 12 2Z"/></svg>';

document.querySelector("#app").innerHTML = `
  <a class="skip-link" href="#about">跳至主要內容</a>
  <header class="header">
    <a class="brand" href="#" aria-label="星泉首頁">${star}<span>星泉<span class="brand-en">AQUARIUSLANG</span></span></a>
    <nav class="desktop-nav" aria-label="主要導覽"><a href="#about">關於星泉</a><a href="#syntax">語法探索</a><a href="#ecosystem">生態系</a><a href="#future">未來展望</a></nav>
    <div class="header-actions"><a class="repo-link" href="${repo}" target="_blank" rel="noopener noreferrer">${github}<span>GitHub</span>${outward}</a><button class="menu-toggle" aria-expanded="false" aria-controls="mobile-nav" aria-label="開啟導覽選單"><span></span><span></span></button></div>
  </header>
  <nav id="mobile-nav" class="mobile-nav" aria-label="行動版導覽" hidden><a href="#about">關於星泉</a><a href="#syntax">語法探索</a><a href="#ecosystem">生態系</a><a href="#future">未來展望</a></nav>
  <main>
    <section class="hero" aria-labelledby="hero-title">
      <div class="hero-grain"></div>
      <div class="hero-copy">
        <div class="eyebrow hero-eyebrow"><span class="live-dot"></span>繁體中文，寫出你的世界<span class="tiny-cross">＋</span></div>
        <h1 id="hero-title">讓靈感，<br><span>湧成作品。</span></h1>
        <p class="hero-description">一門用熟悉的文字，探索未知的程式語言。<br>從第一行中文程式，到光影、圖形與互動創作。<br>星泉，讓想法自由流動。</p>
        <div class="hero-cta"><a class="button primary" href="#syntax">探索星泉 ${arrow}</a><a class="text-link" href="${repo}#執行與測試" target="_blank" rel="noopener noreferrer">開始寫程式 ${outward}</a></div>
        <div class="hero-tags"><span>開源</span><i></i><span>中文語法</span><i></i><span>為創作而生</span></div>
      </div>
      <div class="observatory" aria-label="可拖曳旋轉的水瓶與星泉粒子 3D 場景">
        <canvas id="space-canvas" aria-label="互動水瓶 3D 視覺"></canvas>
        <div class="scene-caption"><span class="scene-cross">＋</span> AQUARIUS / 水瓶座<span class="caption-coordinate">22h 20m · −10°</span></div>
        <svg class="constellation" viewBox="0 0 700 700" aria-hidden="true"><path d="m500 87-58 62 67 45 59-18 42 74-29 86"/><g><circle cx="500" cy="87" r="3"/><circle cx="442" cy="149" r="4"/><circle cx="509" cy="194" r="3"/><circle cx="568" cy="176" r="3"/><circle cx="610" cy="250" r="4"/><circle cx="581" cy="336" r="3"/></g></svg>
        <a class="space-label label-code" href="#syntax"><span class="node-dot"></span><span>中文語法<small>LANGUAGE</small></span></a>
        <a class="space-label label-graphics" href="#capabilities"><span class="node-dot"></span><span>圖形與互動<small>CREATIVE CODING</small></span></a>
        <a class="space-label label-future" href="#future"><span class="node-dot"></span><span>更多可能<small>WHAT'S NEXT</small></span></a>
        <div class="scene-controls"><span class="drag-hint"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M8 11V6a2 2 0 0 1 4 0v5m0-2a2 2 0 0 1 4 0v3m0-1a2 2 0 0 1 4 0v4c0 4-3 6-6 6h-2c-2 0-3-1-4-3l-3-5a2 2 0 0 1 3-2l2 2v-2" stroke="currentColor" stroke-width="1.3"/></svg>拖曳旋轉 · 探索這片星空</span><button id="motion-toggle" class="icon-button" aria-label="暫停星空動畫" aria-pressed="false"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M9 7v10m6-10v10" stroke="currentColor" stroke-width="1.5"/></svg></button><button id="scene-reset" class="icon-button" aria-label="重設水瓶視角"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M5 9a7 7 0 1 1 0 7M5 4v5h5" stroke="currentColor" stroke-width="1.5"/></svg></button></div>
      </div>
      <div class="hero-bottom"><a href="#about" class="scroll-link"><span>向下探索</span><span>↓</span></a><span class="hero-footnote">A LANGUAGE. A SPRING. A UNIVERSE.</span><span class="edition">01 — 無限可能</span></div>
    </section>

    <div class="feature-ribbon"><span>熟悉的文字，新的可能</span><div><span>繁體中文語法</span><span class="ribbon-star">✧</span><span>C# 位元碼 VM</span><span class="ribbon-star">✧</span><span>OpenGL 3.3</span><span class="ribbon-star">✧</span><span>Processing</span><span class="ribbon-star">✧</span><span>VS Code + LSP</span></div></div>

    <section id="about" class="section about-section reveal">
      <div class="section-marker"><span>01 / ORIGIN</span><span>名字裡的宇宙</span></div>
      <div class="about-layout"><div><h2>星光給予方向，<br>泉水帶來生長。</h2><p class="section-intro">「星泉」是 AquariusLang 的中文名字。<br>我們希望，人能用自己熟悉的文字表達想法，<br>讓程式邏輯慢慢長成能執行的作品。</p><a class="text-link subtle-link" href="${repo}#名稱與寓意" target="_blank" rel="noopener noreferrer">讀一讀星泉的起點 ${outward}</a></div><div class="name-stories"><article><span class="story-symbol">✦</span><div><h3>星 <small>THE STAR</small></h3><p>是探索，也是靈感。呼應水瓶座的星空意象，<br class="desktop-break">保留對未知的好奇，與創作的每一種可能。</p></div></article><article><span class="story-symbol water-symbol">≈</span><div><h3>泉 <small>THE SPRING</small></h3><p>是流動，也是延續。像泉水不斷湧出，<br class="desktop-break">讓知識被分享，讓創造力生生不息。</p></div></article><p class="origin-quote">靈感如星光閃耀，創意如泉水湧流。</p></div></div>
    </section>

    <section id="syntax" class="section syntax-section reveal">
      <div class="section-marker"><span>02 / EXPRESSION</span><span>用自己的語言思考</span></div>
      <div class="section-heading"><h2>讀得懂，<br>就從這一行開始。</h2><p>關鍵字、變數、函式與參數，都能以中文命名。<br>保留熟悉的括號與運算符號，讓邏輯清楚可讀。</p></div>
      <div class="syntax-layout"><div class="syntax-notes"><span class="small-overline">AQUARIUS IN PRACTICE</span><h3 id="example-heading">一句問候，<br>就是一個開始。</h3><p id="example-description">用「變數」保存想法，再用「印出」讓它出現在終端機。中文名稱可以直接成為程式的一部分。</p><div class="keyword-pair"><code id="keyword-english">let → 變數</code><span>熟悉的概念，熟悉的文字</span></div><a class="text-link" href="${repo}#語法" target="_blank" rel="noopener noreferrer">查看完整語法 ${outward}</a></div>
      <div class="code-window"><div class="editor-top"><div class="window-dots"><i></i><i></i><i></i></div><span id="code-filename">hello.aqua</span><span class="editor-language">星泉</span></div><div class="code-tabs" role="tablist" aria-label="語法範例"><button class="active" role="tab" aria-selected="true" aria-controls="code-panel" id="tab-hello" data-example="hello">初次見面</button><button role="tab" aria-selected="false" aria-controls="code-panel" id="tab-function" data-example="function" tabindex="-1">函式與判斷</button><button role="tab" aria-selected="false" aria-controls="code-panel" id="tab-loop" data-example="loop" tabindex="-1">迴圈與陣列</button></div><div id="code-panel" role="tabpanel" aria-labelledby="tab-hello" tabindex="0"><pre id="code-content"></pre></div><div class="code-output"><span>預期輸出</span><pre id="code-result" aria-live="polite"></pre><span class="output-note">星泉語法示例 · 預期執行結果</span></div><button id="copy-code" class="copy-code" aria-label="複製目前程式碼"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><rect x="8" y="8" width="11" height="12" rx="2" stroke="currentColor" stroke-width="1.4"/><path d="M15 8V4H4v12h4" stroke="currentColor" stroke-width="1.4"/></svg><span>複製</span></button></div></div>
      <div class="syntax-footer"><span><i>01</i> Unicode 中文識別字</span><span><i>02</i> 全域與區域變數、閉包</span><span><i>03</i> 陣列、雜湊表與模組</span><span><i>04</i> 前置與後置 ++ 運算</span></div>
    </section>

    <section id="capabilities" class="section capabilities-section reveal"><div class="section-marker"><span>03 / RIGHT NOW</span><span>現在，就能開始創作</span></div><div class="section-heading"><h2>從文字，走向光影。</h2><p>星泉已經有自己的圖學與開發工具。<br>這些是目前實作的能力，也是生態系的起點。</p></div><div class="capabilities">
      <a class="capability-row" href="${repo}#opengl-電腦圖學" target="_blank" rel="noopener noreferrer"><span class="capability-number">01</span><span class="capability-icon">◇</span><div><h3>OpenGL 電腦圖學</h3><p>344 個 OpenGL 3.3 core 函式；GLFW、GLAD、GLM、STBImage 原生模組。從視窗、矩陣到紋理與著色器。</p></div><span class="status">已支援</span>${outward}</a>
      <a class="capability-row" href="${repo}/blob/main/AquariusDesktopVMREPL/graphics/Processing.md" target="_blank" rel="noopener noreferrer"><span class="capability-number">02</span><span class="capability-icon">◌</span><div><h3>Processing 風格繪圖</h3><p>2D／3D 圖形、圖片、像素、文字、光源與材質。用滑鼠、鍵盤、動畫迴圈與 Perlin noise 創作互動。</p></div><span class="status">已支援</span>${outward}</a>
      <a class="capability-row" href="${repo}/blob/main/editors/vscode/README.md" target="_blank" rel="noopener noreferrer"><span class="capability-number">03</span><span class="capability-icon">⌘</span><div><h3>VS Code 與 LSP</h3><p>.aqua 語法上色、程式碼片段、即時語法錯誤、補完、滑鼠提示、同檔案定義跳轉與大綱。</p></div><span class="status">已支援</span>${outward}</a>
      <a class="capability-row" href="${repo}#桌面內建函式" target="_blank" rel="noopener noreferrer"><span class="capability-number">04</span><span class="capability-icon">↳</span><div><h3>桌面 VM 與模組</h3><p>以 C# 實作的位元碼虛擬機，提供 REPL、檔案執行、模組匯入、作業系統判斷與外部程式執行。</p></div><span class="status">已支援</span>${outward}</a>
    </div><p class="capability-note">圖學功能需先建置原生函式庫，並具備 OpenGL 3.3 支援。Windows x64 已驗證；Linux／macOS 尚待實機驗證。<a href="${repo}/blob/main/native/README.md" target="_blank" rel="noopener noreferrer">平台與建置說明 ↗</a></p></section>

    <section id="ecosystem" class="section ecosystem-section reveal"><div class="section-marker"><span>04 / CONSTELLATION</span><span>每個想法，都有自己的軌道</span></div><div class="section-heading"><h2>一門語言，<br>一片生長中的星系。</h2><p>從核心語言出發，串起工具、函式庫與作品。<br>點選一顆星，看看它與星泉的連結。</p></div>
      <div class="ecosystem-layout"><div class="ecosystem-map"><div class="map-grid"></div><svg viewBox="0 0 700 520" class="orbit-lines" aria-hidden="true"><ellipse cx="350" cy="270" rx="260" ry="170"/><ellipse cx="350" cy="270" rx="190" ry="105" transform="rotate(-25 350 270)"/><path d="M350 270 180 130M350 270 500 100M350 270 570 300M350 270 380 435M350 270 125 355"/></svg><div class="map-center">${star}<span>星泉<small>AQUARIUSLANG</small></span></div><button class="map-node active node-1" data-node="language" aria-pressed="true"><span class="planet"></span><span>語言核心<small>CORE</small></span></button><button class="map-node node-2" data-node="graphics" aria-pressed="false"><span class="planet"></span><span>圖學與互動<small>GRAPHICS</small></span></button><button class="map-node node-3" data-node="editor" aria-pressed="false"><span class="planet"></span><span>開發工具<small>TOOLING</small></span></button><button class="map-node node-4 planned-node" data-node="creation" aria-pressed="false"><span class="planet"></span><span>創作軟體<small>PLANNED</small></span></button><button class="map-node node-5 planned-node" data-node="frameworks" aria-pressed="false"><span class="planet"></span><span>函式庫與框架<small>PLANNED</small></span></button><div class="map-legend"><span><i></i>現有能力</span><span><i></i>未來計畫</span></div></div><article class="node-detail" aria-live="polite"><span class="node-index" id="node-index">01 / 05</span><span class="status" id="node-status">已支援</span><h3 id="node-title">語言核心</h3><p id="node-description"></p><ul id="node-features"></ul><a id="node-link" class="text-link" href="${repo}" target="_blank" rel="noopener noreferrer">深入了解 ${outward}</a></article></div>
    </section>

    <section id="future" class="section future-section reveal"><div class="section-marker"><span>05 / HORIZON</span><span>下一片星空</span></div><div class="section-heading"><h2>讓創作工具，<br>也能用星泉打造。</h2><p>接下來，想把語言的可能交到更多創作者手裡。<br>以下是發展方向，實作與時程將隨專案持續推進。</p></div><div class="future-columns"><div><div class="future-column-title"><span>01</span><h3>創作軟體</h3><span class="plan-badge">未來計畫</span></div><p class="future-column-intro">從一張圖像，到一個可以探索的世界。</p><ul><li><span>圖像編輯器</span><small>IMAGE EDITING</small></li><li><span>向量繪圖工具</span><small>VECTOR DRAWING</small></li><li><span>3D 建模工具</span><small>3D MODELING</small></li><li><span>影片編輯器</span><small>VIDEO EDITING</small></li><li><span>遊戲引擎</span><small>GAME ENGINE</small></li></ul></div><div><div class="future-column-title"><span>02</span><h3>函式庫與框架</h3><span class="plan-badge">未來計畫</span></div><p class="future-column-intro">把底層能力串起來，讓更多應用有路可走。</p><ul><li><span>網頁後端開發</span><small>WEB BACKEND</small></li><li><span>桌面視窗程式設計</span><small>DESKTOP GUI</small></li><li><span>遊戲開發函式庫</span><small>GAME DEVELOPMENT</small></li><li><span>資料分析與顯示</span><small>DATA & VISUALIZATION</small></li><li><span>AI 開發框架</span><small>ARTIFICIAL INTELLIGENCE</small></li></ul></div></div></section>

    <section class="join-section reveal"><div class="join-star">${star}</div><span class="small-overline">THE NEXT LINE IS YOURS.</span><h2>這片星空，<br>也留著你的位置。</h2><p>讀讀原始碼、試寫一段程式，或帶來一個新想法。<br>星泉還在生長，歡迎一起讓它走得更遠。</p><a class="button primary" href="${repo}" target="_blank" rel="noopener noreferrer">在 GitHub 探索星泉 ${outward}</a><a class="join-issues" href="${repo}/issues" target="_blank" rel="noopener noreferrer">分享想法與回饋 ↗</a></section>
  </main>
  <footer><a class="brand" href="#">${star}<span>星泉<span class="brand-en">AQUARIUSLANG</span></span></a><p>用熟悉的文字，讓靈感湧成作品。</p><div><span>語言作者 林天牧 / Temple Lin</span><a href="${repo}" target="_blank" rel="noopener noreferrer">開源專案 ${outward}</a><a href="#">回到星空 ↑</a></div></footer>
  <div id="toast" role="status" aria-live="polite"></div>
`;

const examples = {
  hello: {
    file: "hello.aqua",
    heading: "一句問候，\n就是一個開始。",
    description:
      "用「變數」保存想法，再用「印出」讓它出現在終端機。中文名稱可以直接成為程式的一部分。",
    keyword: "let → 變數",
    code: '# 用熟悉的文字，寫下第一個想法\n\n變數 名字 = "世界";\n變數 問候 = "你好，" + 名字;\n\n印出(問候);\n印出("讓靈感，湧成作品。");',
    output: "你好，世界\n讓靈感，湧成作品。",
  },
  function: {
    file: "starlight.aqua",
    heading: "把想法，\n寫成可以重用的邏輯。",
    description:
      "以「函式」組織運算，用「如果」選擇分支，再用「回傳」交出結果。函式的參數也能以中文命名。",
    keyword: "fn → 函式 · return → 回傳",
    code: '# 判斷一顆星的亮度\n變數 描述星光 = 函式(亮度) {\n    如果 (亮度 > 5) {\n        回傳 "星光閃耀";\n    } 否則 {\n        回傳 "微光，也有自己的位置";\n    }\n};\n\n印出(描述星光(8));',
    output: "星光閃耀",
  },
  loop: {
    file: "constellation.aqua",
    heading: "讓每一顆星，\n都有被看見的機會。",
    description:
      "以陣列收集資料，用「迴圈」逐一讀取。「長度」取得項目數量，++ 則讓索引向前一步。",
    keyword: "for → 迴圈 · len → 長度",
    code: '# 把星光連成一片星空\n變數 星群 = ["靈感", "探索", "創作"];\n\n迴圈 (變數 索引 = 0;\n      索引 < 長度(星群); 索引++) {\n    印出(星群[索引]);\n}',
    output: "靈感\n探索\n創作",
  },
};
let currentExample = "hello";
const escapeHtml = (text) =>
  text
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
function highlight(line) {
  const tokens =
    /("[^"\n]*"|#.*$|\b\d+\b|變數|函式|如果|否則如果|否則|回傳|迴圈|中斷|印出|長度)/g;
  let output = "",
    last = 0;
  for (const match of line.matchAll(tokens)) {
    output += escapeHtml(line.slice(last, match.index));
    const token = match[0];
    const type = token.startsWith("#")
      ? "comment"
      : token.startsWith('"')
        ? "string"
        : /^\d+$/.test(token)
          ? "number"
          : ["印出", "長度"].includes(token)
            ? "builtin"
            : "keyword";
    output += `<span class="token-${type}">${escapeHtml(token)}</span>`;
    last = match.index + token.length;
  }
  return output + escapeHtml(line.slice(last));
}
function showExample(name) {
  currentExample = name;
  const example = examples[name];
  document.querySelector("#code-filename").textContent = example.file;
  document.querySelector("#example-heading").textContent = example.heading;
  document.querySelector("#example-description").textContent =
    example.description;
  document.querySelector("#keyword-english").textContent = example.keyword;
  document.querySelector("#code-content").innerHTML = example.code
    .split("\n")
    .map(
      (line, i) =>
        `<span class="code-line"><span class="line-number" aria-hidden="true">${i + 1}</span><span>${highlight(line) || " "}</span></span>`,
    )
    .join("");
  document.querySelector("#code-result").textContent = example.output;
  document
    .querySelector("#code-panel")
    .setAttribute("aria-labelledby", `tab-${name}`);
  document.querySelectorAll("[data-example]").forEach((button) => {
    const active = button.dataset.example === name;
    button.classList.toggle("active", active);
    button.setAttribute("aria-selected", String(active));
    button.tabIndex = active ? 0 : -1;
  });
}
showExample("hello");
document.querySelectorAll("[data-example]").forEach((button) => {
  button.addEventListener("click", () => showExample(button.dataset.example));
  button.addEventListener("keydown", (event) => {
    const tabs = [...document.querySelectorAll("[data-example]")];
    let index = tabs.indexOf(button);
    if (event.key === "ArrowRight") index = (index + 1) % tabs.length;
    else if (event.key === "ArrowLeft")
      index = (index - 1 + tabs.length) % tabs.length;
    else if (event.key === "Home") index = 0;
    else if (event.key === "End") index = tabs.length - 1;
    else return;
    event.preventDefault();
    showExample(tabs[index].dataset.example);
    tabs[index].focus();
  });
});
let toastTimer;
function toast(message) {
  const target = document.querySelector("#toast");
  target.textContent = message;
  target.classList.add("visible");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => target.classList.remove("visible"), 3000);
}
document.querySelector("#copy-code").addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(examples[currentExample].code);
    toast("程式碼已複製，可以貼進 .aqua 檔案。");
  } catch {
    toast("瀏覽器未允許複製；請直接選取程式碼複製。");
  }
});
const nodes = {
  language: {
    index: "01",
    title: "語言核心",
    status: "已支援",
    description:
      "繁體中文是表達邏輯的起點。以 C# 實作的位元碼虛擬機，將熟悉的文字編譯成能執行的程式。",
    features: [
      "中文關鍵字與 Unicode 識別字",
      "函式、閉包、陣列與雜湊表",
      "模組匯入與桌面 REPL",
    ],
    url: repo + "#語法",
  },
  graphics: {
    index: "02",
    title: "圖學與互動",
    status: "已支援",
    description:
      "透過 OpenGL 與 Processing 風格 API，把程式變成可見、可動、可互動的創作。",
    features: [
      "OpenGL 3.3 core：344 個函式",
      "2D／3D、光源、材質與 GLSL",
      "滑鼠、鍵盤事件與動畫迴圈",
    ],
    url: repo + "#opengl-電腦圖學",
  },
  editor: {
    index: "03",
    title: "開發工具",
    status: "已支援",
    description:
      "讓中文程式也有熟悉的編輯體驗。VS Code 擴充套件與獨立的 LSP 語言伺服器已經就位。",
    features: [
      "語法上色與程式碼片段",
      "補完、即時錯誤與滑鼠提示",
      "同檔案定義跳轉與大綱",
    ],
    url: repo + "/blob/main/editors/vscode/README.md",
  },
  creation: {
    index: "04",
    title: "創作軟體",
    status: "未來計畫",
    description:
      "希望有一天，創作者每天使用的工具，也能以星泉打造。從圖像到影像，從建模到完整的遊戲世界。",
    features: [
      "圖像編輯器與向量繪圖工具",
      "3D 建模工具與影片編輯器",
      "遊戲引擎",
    ],
    url: "#future",
  },
  frameworks: {
    index: "05",
    title: "函式庫與框架",
    status: "未來計畫",
    description:
      "讓語言往更多應用延伸。以函式庫與框架連起底層能力，逐步降低開發完整應用的門檻。",
    features: [
      "網頁後端與桌面視窗程式設計",
      "遊戲開發、資料分析與顯示",
      "AI 開發框架",
    ],
    url: "#future",
  },
};
function selectNode(name) {
  const node = nodes[name];
  document.querySelector("#node-index").textContent = node.index + " / 05";
  document.querySelector("#node-title").textContent = node.title;
  document.querySelector("#node-description").textContent = node.description;
  document.querySelector("#node-features").innerHTML = node.features
    .map((feature) => `<li>${feature}</li>`)
    .join("");
  const status = document.querySelector("#node-status");
  status.textContent = node.status;
  status.classList.toggle("planned", node.status === "未來計畫");
  const link = document.querySelector("#node-link");
  link.href = node.url;
  link.innerHTML = node.url.startsWith("#")
    ? `看看發展方向 ${arrow}`
    : `深入了解 ${outward}`;
  if (node.url.startsWith("#")) link.removeAttribute("target");
  else link.target = "_blank";
  document.querySelectorAll("[data-node]").forEach((button) => {
    const active = button.dataset.node === name;
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", String(active));
  });
}
document
  .querySelectorAll("[data-node]")
  .forEach((button) =>
    button.addEventListener("click", () => selectNode(button.dataset.node)),
  );
selectNode("language");
const menuToggle = document.querySelector(".menu-toggle");
const mobileNav = document.querySelector("#mobile-nav");
function closeMenu() {
  mobileNav.hidden = true;
  menuToggle.setAttribute("aria-expanded", "false");
  menuToggle.setAttribute("aria-label", "開啟導覽選單");
}
menuToggle.addEventListener("click", () => {
  const expanded = menuToggle.getAttribute("aria-expanded") !== "true";
  menuToggle.setAttribute("aria-expanded", String(expanded));
  menuToggle.setAttribute(
    "aria-label",
    expanded ? "關閉導覽選單" : "開啟導覽選單",
  );
  mobileNav.hidden = !expanded;
});
mobileNav
  .querySelectorAll("a")
  .forEach((link) => link.addEventListener("click", closeMenu));
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") closeMenu();
});
const revealObserver = new IntersectionObserver(
  (entries) =>
    entries.forEach((entry) => {
      if (entry.isIntersecting) {
        entry.target.classList.add("is-visible");
        revealObserver.unobserve(entry.target);
      }
    }),
  { threshold: 0.08 },
);
document
  .querySelectorAll(".reveal")
  .forEach((section) => revealObserver.observe(section));
const navObserver = new IntersectionObserver(
  (entries) => {
    for (const entry of entries)
      if (entry.isIntersecting)
        document
          .querySelectorAll(".desktop-nav a")
          .forEach((link) =>
            link.classList.toggle(
              "current",
              link.hash === "#" + entry.target.id,
            ),
          );
  },
  { rootMargin: "-20% 0px -55% 0px" },
);
document
  .querySelectorAll("main section[id]")
  .forEach((section) => navObserver.observe(section));
import("./scene.js").then(({ createObservatory }) =>
  createObservatory(document.querySelector("#space-canvas")),
);

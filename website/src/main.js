import "./style.css";
import "./home-extra.css";
import { storedLang, urlLang, rememberLang, writeUrlLang, withLang, applyDocumentLang, browserLang } from "./i18n.js";

const repo = "https://github.com/Aquarius-Language/AquariusLangTW";
const arrow =
  '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M5 12h14m-6-6 6 6-6 6" stroke="currentColor" stroke-width="1.5"/></svg>';
const outward =
  '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M6 18 18 6M6 6h12v12" stroke="currentColor" stroke-width="1.5"/></svg>';
const star =
  '<svg viewBox="0 0 40 40" fill="none" aria-hidden="true"><path d="m20 2 4.3 13.7L38 20l-13.7 4.3L20 38l-4.3-13.7L2 20l13.7-4.3z" fill="currentColor"/><circle cx="20" cy="20" r="3" fill="#080e11"/></svg>';
const github =
  '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 2a10 10 0 0 0-3.16 19.49c.5.09.68-.22.68-.48v-1.86c-2.78.6-3.37-1.18-3.37-1.18-.46-1.16-1.11-1.47-1.11-1.47-.91-.62.07-.61.07-.61 1 .07 1.53 1.03 1.53 1.03.89 1.52 2.34 1.08 2.91.83.09-.65.35-1.09.63-1.34-2.22-.25-4.55-1.11-4.55-4.95 0-1.09.39-1.98 1.03-2.68-.1-.25-.45-1.27.1-2.65 0 0 .84-.27 2.75 1.02A9.6 9.6 0 0 1 12 6.82c.85 0 1.71.11 2.51.33 1.91-1.29 2.75-1.02 2.75-1.02.55 1.38.2 2.4.1 2.65.64.7 1.03 1.59 1.03 2.68 0 3.85-2.34 4.69-4.57 4.94.36.31.68.92.68 1.85v2.76c0 .27.18.58.69.48A10 10 0 0 0 12 2Z"/></svg>';
const book =
  '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M4 5.5A2.5 2.5 0 0 1 6.5 3H20v15H6.5A2.5 2.5 0 0 0 4 20.5v-15Z" stroke="currentColor" stroke-width="1.5"/><path d="M4 20.5A2.5 2.5 0 0 0 6.5 23H20v-5" stroke="currentColor" stroke-width="1.5"/><path d="m12 7 1.2 2.8L16 11l-2.8 1.2L12 15l-1.2-2.8L8 11l2.8-1.2z" fill="currentColor"/></svg>';

/* ───────────────────────── copy ───────────────────────── */
const COPY = {
  zh: {
    readme: repo + "/blob/main/README.tw.md",
    pageTitle: "星泉 AquariusLang — 讓靈感，湧成作品",
    description: "星泉 AquariusLang，支援繁體中文與英文語法、可在同一檔案混用的開源雙語程式語言。探索語法、圖學能力與正在生長的創作生態系。",
    skip: "跳至主要內容",
    homeLabel: "星泉首頁",
    nav: ["關於星泉", "語法探索", "生態系", "未來展望"],
    navTutorial: "互動教學",
    mainNav: "主要導覽",
    mobileNav: "行動版導覽",
    openMenu: "開啟導覽選單",
    closeMenu: "關閉導覽選單",
    langSwitch: "English",
    langSwitchLabel: "Switch to English",
    heroEyebrow: "繁體中文與英文，寫出你的世界",
    heroTitle: "讓靈感，<br><span>湧成作品。</span>",
    heroText: "一門用熟悉的文字，探索未知的程式語言。<br>從第一行雙語程式，到光影、圖形與互動創作。<br>星泉，讓想法自由流動。",
    heroTutorial: "開始互動教學",
    heroExplore: "探索星泉",
    heroTags: ["開源", "雙語語法", "為創作而生"],
    sceneLabel: "可拖曳旋轉的水瓶與星泉粒子 3D 場景",
    canvasLabel: "互動水瓶 3D 視覺",
    labels: [["雙語語法", "LANGUAGE"], ["圖形與互動", "CREATIVE CODING"], ["更多可能", "WHAT'S NEXT"]],
    dragHint: "拖曳旋轉 · 探索這片星空",
    pause: "暫停星空動畫",
    resume: "繼續星空動畫",
    resetView: "重設水瓶視角",
    scroll: "向下探索",
    edition: "01 — 無限可能",
    ribbonLead: "熟悉的文字，新的可能",
    ribbon: ["繁體中文／英文雙語語法", "WebAssembly + Wasmtime", "WebGPU + Processing", "Windows · Linux · Android · 網頁", "VS Code + LSP"],
    tutorialBanner: {
      kicker: "NEW · 互動教學",
      title: "十五堂課，<br>從第一道星光到整片銀河。",
      text: "在瀏覽器裡直接修改、執行星泉程式。從印出第一行字，到函式、結構、非同步與跨平台發佈，每一課都有動畫轉場與小測驗。",
      cta: "進入互動教學",
      steps: [["入門", "星光初現"], ["初階", "連成星座"], ["中階", "鍛造星辰"], ["高階", "航向銀河"]],
    },
    aboutMarker: ["01 / ORIGIN", "名字裡的宇宙"],
    aboutTitle: "星光給予方向，<br>泉水帶來生長。",
    aboutIntro: "「星泉」是 AquariusLang 的中文名字。<br>我們希望，人能用自己熟悉的文字表達想法，<br>讓程式邏輯慢慢長成能執行的作品。",
    aboutLink: "讀一讀星泉的起點",
    aboutAnchor: "#名稱與授權",
    stories: [
      ["✦", "星", "THE STAR", "是探索，也是靈感。呼應水瓶座的星空意象，<br class=\"desktop-break\">保留對未知的好奇，與創作的每一種可能。"],
      ["≈", "泉", "THE SPRING", "是流動，也是延續。像泉水不斷湧出，<br class=\"desktop-break\">讓知識被分享，讓創造力生生不息。"],
    ],
    quote: "靈感如星光閃耀，創意如泉水湧流。",
    syntaxMarker: ["02 / EXPRESSION", "用自己的語言思考"],
    syntaxTitle: "讀得懂，<br>就從這一行開始。",
    syntaxIntro: "關鍵字可用繁體中文或英文，並在同檔混用。<br>保留熟悉的括號與運算符號，讓邏輯清楚可讀。",
    syntaxLink: "查看完整語法",
    syntaxAnchor: "#語法",
    codeLabel: "星泉",
    exampleTabs: "語法範例",
    expected: "預期輸出",
    outputNote: "星泉語法示例 · 預期執行結果",
    copy: "複製",
    copyLabel: "複製目前程式碼",
    copied: "程式碼已複製，可以貼進 .aqua 檔案。",
    copyFailed: "瀏覽器未允許複製；請直接選取程式碼複製。",
    tryInTutorial: "在互動教學中動手試試",
    syntaxFooter: ["Unicode 中文識別字", "全域與區域變數、閉包", "陣列、雜湊表與模組", "前置與後置 ++ 運算"],
    capMarker: ["03 / RIGHT NOW", "現在，就能開始創作"],
    capTitle: "從文字，走向光影。",
    capIntro: "星泉已經有自己的圖學與開發工具。<br>這些是目前實作的能力，也是生態系的起點。",
    supported: "已支援",
    capabilities: [
      ["◇", "OpenGL 電腦圖學", "OpenGL 3.3 core 函式，搭配 GLFW、GLAD、STBImage 原生模組。從視窗、矩陣到紋理與著色器。", "/blob/main/libraries/opengl/README.md"],
      ["◌", "Processing 風格繪圖", "2D／3D 圖形、圖片、像素、文字、光源與材質。用滑鼠、鍵盤、動畫迴圈與 Perlin noise 創作互動。", "/blob/main/libraries/graphics/README.md"],
      ["⌘", "VS Code 與 LSP", ".aqua 語法上色、程式碼片段、即時語法錯誤、補完、滑鼠提示、同檔案定義跳轉與大綱。", "/blob/main/editors/vscode/README.md"],
      ["↳", "跨平台編譯與模組", "編譯為 WebAssembly，桌面以 Wasmtime 執行，瀏覽器原生載入。保留 REPL、模組匯入與主機服務。", "/blob/main/README.tw.md#編譯執行與平台輸出"],
    ],
    capNote: "圖學功能依平台使用 WebGPU 或 OpenGL；各函式庫支援的平台列於其說明文件。",
    capNoteLink: "函式庫與範例 ↗",
    capNoteHref: "/blob/main/README.tw.md#函式庫與範例",
    ecoMarker: ["04 / CONSTELLATION", "每個想法，都有自己的軌道"],
    ecoTitle: "一門語言，<br>一片生長中的星系。",
    ecoIntro: "從核心語言出發，串起工具、函式庫與作品。<br>點選一顆星，看看它與星泉的連結。",
    current: "現有能力",
    planned: "未來計畫",
    more: "深入了解",
    seePlans: "看看發展方向",
    nodeNames: { language: ["語言核心", "CORE"], graphics: ["圖學與互動", "GRAPHICS"], editor: ["開發工具", "TOOLING"], creation: ["創作軟體", "PLANNED"], frameworks: ["函式庫與框架", "PLANNED"] },
    futureMarker: ["05 / HORIZON", "下一片星空"],
    futureTitle: "讓創作工具，<br>也能用星泉打造。",
    futureIntro: "接下來，想把語言的可能交到更多創作者手裡。<br>以下是發展方向，實作與時程將隨專案持續推進。",
    futureCols: [
      ["創作軟體", "從一張圖像，到一個可以探索的世界。", [["圖像編輯器", "IMAGE EDITING"], ["向量繪圖工具", "VECTOR DRAWING"], ["3D 建模工具", "3D MODELING"], ["影片編輯器", "VIDEO EDITING"], ["遊戲引擎", "GAME ENGINE"]]],
      ["函式庫與框架", "把底層能力串起來，讓更多應用有路可走。", [["網頁後端開發", "WEB BACKEND"], ["桌面視窗程式設計", "DESKTOP GUI"], ["遊戲開發函式庫", "GAME DEVELOPMENT"], ["資料分析與顯示", "DATA & VISUALIZATION"], ["AI 開發框架", "ARTIFICIAL INTELLIGENCE"]]],
    ],
    joinKicker: "THE NEXT LINE IS YOURS.",
    joinTitle: "這片星空，<br>也留著你的位置。",
    joinText: "讀讀原始碼、試寫一段程式，或帶來一個新想法。<br>星泉還在生長，歡迎一起讓它走得更遠。",
    joinCta: "在 GitHub 探索星泉",
    joinIssues: "分享想法與回饋 ↗",
    footerText: "用熟悉的文字，讓靈感湧成作品。",
    author: "語言作者 林天牧 / Temple Lin",
    openSource: "開源專案",
    backTop: "回到星空 ↑",
  },
  en: {
    readme: repo + "#readme",
    pageTitle: "AquariusLang 星泉 — Let inspiration flow into creation",
    description: "AquariusLang (星泉) is an open-source bilingual programming language: write in Traditional Chinese, English, or both in one file. Explore the syntax, graphics and a growing creative ecosystem.",
    skip: "Skip to main content",
    homeLabel: "AquariusLang home",
    nav: ["About", "Syntax", "Ecosystem", "Future"],
    navTutorial: "Tutorial",
    mainNav: "Main navigation",
    mobileNav: "Mobile navigation",
    openMenu: "Open navigation menu",
    closeMenu: "Close navigation menu",
    langSwitch: "繁體中文",
    langSwitchLabel: "切換為繁體中文",
    heroEyebrow: "Write your world in Chinese and English",
    heroTitle: "Let inspiration<br><span>flow into creation.</span>",
    heroText: "A programming language that explores the unknown in words you already know.<br>From your first bilingual line to light, graphics and interactive art.<br>AquariusLang lets ideas flow freely.",
    heroTutorial: "Start the interactive tutorial",
    heroExplore: "Explore AquariusLang",
    heroTags: ["Open source", "Bilingual syntax", "Made for creating"],
    sceneLabel: "A draggable 3D scene of the Aquarius water jar and starry particles",
    canvasLabel: "Interactive 3D water jar",
    labels: [["Bilingual syntax", "語言"], ["Graphics & interaction", "創意程式"], ["What's next", "更多可能"]],
    dragHint: "Drag to rotate · explore the sky",
    pause: "Pause the starfield animation",
    resume: "Resume the starfield animation",
    resetView: "Reset the jar's view",
    scroll: "Scroll to explore",
    edition: "01 — Endless possibility",
    ribbonLead: "Familiar words, new possibilities",
    ribbon: ["Chinese / English syntax", "WebAssembly + Wasmtime", "WebGPU + Processing", "Desktop · Android · Web", "VS Code + LSP"],
    tutorialBanner: {
      kicker: "NEW · INTERACTIVE TUTORIAL",
      title: "Fifteen lessons,<br>from first light to the whole galaxy.",
      text: "Edit and run AquariusLang right in your browser. Go from printing your first line to functions, structs, async code and shipping on every platform, with animated transitions and a quick quiz in every lesson.",
      cta: "Enter the tutorial",
      steps: [["Beginner", "First Light"], ["Explorer", "Constellations"], ["Builder", "Star Forge"], ["Advanced", "Into the Galaxy"]],
    },
    aboutMarker: ["01 / ORIGIN", "A universe in a name"],
    aboutTitle: "Stars give direction,<br>springs bring growth.",
    aboutIntro: "星泉 (\"star spring\") is AquariusLang's Chinese name.<br>We want people to express ideas in the words they know best,<br>and let program logic grow into working creations.",
    aboutLink: "Read where AquariusLang began",
    aboutAnchor: "#name-and-license",
    stories: [
      ["✦", "星", "THE STAR", "Exploration and inspiration. It echoes the night sky of Aquarius,<br class=\"desktop-break\">keeping our curiosity for the unknown and every way to create."],
      ["≈", "泉", "THE SPRING", "Flow and continuity. Like a spring that never runs dry,<br class=\"desktop-break\">knowledge is shared and creativity keeps renewing itself."],
    ],
    quote: "Inspiration shines like starlight; creativity flows like a spring.",
    syntaxMarker: ["02 / EXPRESSION", "Think in your own language"],
    syntaxTitle: "If you can read it,<br>start with this line.",
    syntaxIntro: "Keywords come in Traditional Chinese or English, mixed freely in one file.<br>Familiar brackets and operators keep the logic clear.",
    syntaxLink: "See the full syntax",
    syntaxAnchor: "#syntax",
    codeLabel: "AquariusLang",
    exampleTabs: "Syntax examples",
    expected: "Expected output",
    outputNote: "AquariusLang example · expected result",
    copy: "Copy",
    copyLabel: "Copy this code",
    copied: "Code copied. Paste it into an .aqua file.",
    copyFailed: "The browser blocked copying; select the code to copy it.",
    tryInTutorial: "Try it yourself in the tutorial",
    syntaxFooter: ["Unicode Chinese identifiers", "Global and local variables, closures", "Arrays, hashes and modules", "Prefix and postfix ++"],
    capMarker: ["03 / RIGHT NOW", "Start creating today"],
    capTitle: "From words to light.",
    capIntro: "AquariusLang already has its own graphics and developer tools.<br>These work today, and they are where the ecosystem begins.",
    supported: "Available",
    capabilities: [
      ["◇", "OpenGL graphics", "OpenGL 3.3 core functions with native GLFW, GLAD and STBImage modules. Windows, matrices, textures and shaders.", "/blob/main/libraries/opengl/README.md"],
      ["◌", "Processing-style drawing", "2D/3D shapes, images, pixels, text, lights and materials. Build interaction with the mouse, keyboard, animation loops and Perlin noise.", "/blob/main/libraries/graphics/README.md"],
      ["⌘", "VS Code and LSP", "Highlighting for .aqua, snippets, live errors, completion, hover, go-to-definition within a file and an outline.", "/blob/main/editors/vscode/README.md"],
      ["↳", "Cross-platform builds and modules", "Compiles to WebAssembly: Wasmtime runs it on desktop, browsers load it natively. With a REPL, module imports and host services.", "#compile-run-and-export"],
    ],
    capNote: "Graphics use WebGPU or OpenGL depending on the platform; each library's README lists the platforms it supports.",
    capNoteLink: "Libraries and examples ↗",
    capNoteHref: "#libraries-and-examples",
    ecoMarker: ["04 / CONSTELLATION", "Every idea has its own orbit"],
    ecoTitle: "One language,<br>a growing galaxy.",
    ecoIntro: "The core language connects tools, libraries and creations.<br>Pick a star to see how it links to AquariusLang.",
    current: "Available now",
    planned: "Planned",
    more: "Learn more",
    seePlans: "See where it's heading",
    nodeNames: { language: ["Core language", "核心"], graphics: ["Graphics & interaction", "圖學"], editor: ["Developer tools", "工具"], creation: ["Creative software", "PLANNED"], frameworks: ["Libraries & frameworks", "PLANNED"] },
    futureMarker: ["05 / HORIZON", "The next sky"],
    futureTitle: "Creative tools,<br>built with AquariusLang.",
    futureIntro: "Next, we want to put the language in more creators' hands.<br>These are directions, not promises; work and timing will follow the project.",
    futureCols: [
      ["Creative software", "From a single image to a world you can explore.", [["Image editor", "圖像編輯"], ["Vector drawing", "向量繪圖"], ["3D modeling", "3D 建模"], ["Video editor", "影片編輯"], ["Game engine", "遊戲引擎"]]],
      ["Libraries & frameworks", "Connecting low-level power so more apps have a path.", [["Web backends", "網頁後端"], ["Desktop GUIs", "桌面視窗"], ["Game development", "遊戲開發"], ["Data & visualization", "資料與視覺化"], ["AI frameworks", "人工智慧"]]],
    ],
    joinKicker: "THE NEXT LINE IS YOURS.",
    joinTitle: "There's a place<br>for you in this sky.",
    joinText: "Read the source, write a few lines, or bring a new idea.<br>AquariusLang is still growing. Help it go further.",
    joinCta: "Explore AquariusLang on GitHub",
    joinIssues: "Share ideas and feedback ↗",
    footerText: "Familiar words, flowing into creation.",
    author: "Created by 林天牧 / Temple Lin",
    openSource: "Open source",
    backTop: "Back to the stars ↑",
  },
};

const EXAMPLES = {
  zh: {
    hello: {
      tab: "初次見面",
      file: "hello.aqua",
      heading: "一句問候，\n就是一個開始。",
      description: "用「變數」保存想法，再用「印出」讓它出現在終端機。中文名稱可以直接成為程式的一部分。",
      keyword: "var ↔ 變數",
      code: '# 用熟悉的文字，寫下第一個想法\n\n變數 名字 = "世界";\n變數 問候 = "你好，" + 名字;\n\n印出(問候);\n印出("讓靈感，湧成作品。");',
      output: "你好，世界\n讓靈感，湧成作品。",
      lesson: "hello",
    },
    function: {
      tab: "函式與判斷",
      file: "starlight.aqua",
      heading: "把想法，\n寫成可以重用的邏輯。",
      description: "以「函式」組織運算，用「如果」選擇分支，再用「回傳」交出結果。函式的參數也能以中文命名。",
      keyword: "function ↔ 函式 · return ↔ 回傳",
      code: '# 判斷一顆星的亮度\n變數 描述星光 = 函式(亮度) {\n    如果 (亮度 > 5) {\n        回傳 "星光閃耀";\n    } 否則 {\n        回傳 "微光，也有自己的位置";\n    }\n};\n\n印出(描述星光(8));',
      output: "星光閃耀",
      lesson: "conditions",
    },
    loop: {
      tab: "迴圈與陣列",
      file: "constellation.aqua",
      heading: "讓每一顆星，\n都有被看見的機會。",
      description: "以陣列收集資料，用「迴圈」逐一讀取。「長度」取得項目數量，++ 則讓索引向前一步。",
      keyword: "for → 迴圈 · len → 長度",
      code: '# 把星光連成一片星空\n變數 星群 = ["靈感", "探索", "創作"];\n\n迴圈 (變數 索引 = 0;\n      索引 < 長度(星群); 索引++) {\n    印出(星群[索引]);\n}',
      output: "靈感\n探索\n創作",
      lesson: "loops",
    },
  },
  en: {
    hello: {
      tab: "Hello",
      file: "hello.aqua",
      heading: "One greeting\nis how it begins.",
      description: "Keep an idea in a var, then print it to the terminal. Chinese names work too, right alongside English keywords.",
      keyword: "var ↔ 變數",
      code: '# Write your first idea in words you know\n\nvar name = "world";\nvar greeting = "Hello, " + name;\n\nprint(greeting);\n印出("讓靈感，湧成作品。");',
      output: "Hello, world\n讓靈感，湧成作品。",
      lesson: "hello",
    },
    function: {
      tab: "Functions",
      file: "starlight.aqua",
      heading: "Turn ideas into\nreusable logic.",
      description: "Organize work in a function, branch with if, and hand back a result with return. Parameters can have Chinese names too.",
      keyword: "function ↔ 函式 · return ↔ 回傳",
      code: '# How bright is this star?\nvar describe = function(brightness) {\n    if (brightness > 5) {\n        return "Shining bright";\n    } else {\n        return "A faint light still has its place";\n    }\n};\n\nprint(describe(8));',
      output: "Shining bright",
      lesson: "conditions",
    },
    loop: {
      tab: "Loops & arrays",
      file: "constellation.aqua",
      heading: "Give every star\na chance to be seen.",
      description: "Collect data in an array and visit each item with for. len counts the items, and ++ moves the index forward.",
      keyword: "for → 迴圈 · len → 長度",
      code: '# Connect the stars into a sky\nvar stars = ["inspire", "explore", "create"];\n\nfor (var i = 0;\n     i < len(stars); i++) {\n    print(stars[i]);\n}',
      output: "inspire\nexplore\ncreate",
      lesson: "loops",
    },
  },
};

const NODES = {
  zh: {
    language: {
      status: "current",
      description: "繁體中文與英文都能表達邏輯，也可在同一檔案混用。以 C# 實作的編譯器，將雙語語法編譯成跨平台 WebAssembly 程式。",
      features: ["繁體中文／英文關鍵字與 Unicode 識別字", "函式、閉包、結構、陣列與雜湊表", "選用型別、非同步函式與執行緒"],
      url: repo + "/blob/main/README.tw.md#語法",
    },
    graphics: {
      status: "current",
      description: "透過 WebGPU、Processing 風格 API 與 OpenGL，把程式變成可見、可動、可互動的創作。",
      features: ["Processing 2D／3D、文字與著色器", "PBR 模型與 GPU 運算", "滑鼠、鍵盤、觸控事件與動畫迴圈"],
      url: repo + "/blob/main/libraries/graphics/README.md",
    },
    editor: {
      status: "current",
      description: "讓中文程式也有熟悉的編輯體驗。VS Code 擴充套件、獨立的 LSP 語言伺服器與星泉筆記本已經就位。",
      features: ["語法上色與程式碼片段", "補完、即時錯誤與滑鼠提示", "Aqua WellPlay 瀏覽器筆記本"],
      url: repo + "/blob/main/editors/vscode/README.md",
    },
    creation: {
      status: "planned",
      description: "希望有一天，創作者每天使用的工具，也能以星泉打造。從圖像到影像，從建模到完整的遊戲世界。",
      features: ["圖像編輯器與向量繪圖工具", "3D 建模工具與影片編輯器", "遊戲引擎"],
      url: "#future",
    },
    frameworks: {
      status: "planned",
      description: "讓語言往更多應用延伸。以函式庫與框架連起底層能力，逐步降低開發完整應用的門檻。",
      features: ["網頁後端與桌面視窗程式設計", "遊戲開發、資料分析與顯示", "AI 開發框架"],
      url: "#future",
    },
  },
  en: {
    language: {
      status: "current",
      description: "Express logic in Traditional Chinese, English, or both in one file. A compiler written in C# turns the bilingual syntax into portable WebAssembly.",
      features: ["Chinese / English keywords and Unicode identifiers", "Functions, closures, structs, arrays and hashes", "Optional types, async functions and threads"],
      url: repo + "#syntax",
    },
    graphics: {
      status: "current",
      description: "WebGPU, a Processing-style API and OpenGL turn programs into creations you can see, animate and interact with.",
      features: ["Processing 2D/3D, text and shaders", "PBR models and GPU compute", "Mouse, keyboard, touch and animation loops"],
      url: repo + "/blob/main/libraries/graphics/README.md",
    },
    editor: {
      status: "current",
      description: "Chinese code deserves a familiar editor too. The VS Code extension, a standalone LSP server and browser notebooks are ready.",
      features: ["Highlighting and snippets", "Completion, live errors and hover", "Aqua WellPlay browser notebooks"],
      url: repo + "/blob/main/editors/vscode/README.md",
    },
    creation: {
      status: "planned",
      description: "One day, the tools creators use every day could be built with AquariusLang: from images to video, from modeling to full game worlds.",
      features: ["Image editor and vector drawing", "3D modeling and video editing", "Game engine"],
      url: "#future",
    },
    frameworks: {
      status: "planned",
      description: "Extending the language to more kinds of apps, with libraries and frameworks that make complete applications easier to build.",
      features: ["Web backends and desktop GUIs", "Game development, data and visualization", "AI frameworks"],
      url: "#future",
    },
  },
};

/* ───────────────────────── language gate ───────────────────────── */
function showLanguageGate(onChoose) {
  const remembered = storedLang();
  const suggested = remembered || browserLang();
  const gate = document.createElement("div");
  gate.className = "lang-gate";
  gate.setAttribute("role", "dialog");
  gate.setAttribute("aria-modal", "true");
  gate.setAttribute("aria-labelledby", "gate-title");
  gate.innerHTML = `
    <div class="gate-sky" aria-hidden="true">${Array.from({ length: 60 }, (_, i) => `<i style="--x:${(i * 37) % 100};--y:${(i * 61) % 100};--d:${(i % 7) * 0.4}s;--s:${1 + (i % 3)}"></i>`).join("")}</div>
    <div class="gate-inner">
      <div class="gate-mark" aria-hidden="true"><span></span><span></span>${star}</div>
      <p class="gate-brand">星泉 <span>AQUARIUSLANG</span></p>
      <h1 id="gate-title"><span lang="zh-Hant">選擇你的語言</span><span class="gate-sep" aria-hidden="true">／</span><span lang="en">Choose your language</span></h1>
      <div class="gate-options">
        <button class="gate-option" data-lang="zh" lang="zh-Hant">
          <span class="gate-glyph" aria-hidden="true">中</span>
          <span class="gate-name">繁體中文</span>
          <span class="gate-sub">以繁體中文瀏覽</span>
          ${suggested === "zh" ? `<span class="gate-badge">${remembered ? "上次的選擇" : "建議"}</span>` : ""}
        </button>
        <button class="gate-option" data-lang="en" lang="en">
          <span class="gate-glyph" aria-hidden="true">A</span>
          <span class="gate-name">English</span>
          <span class="gate-sub">Continue in English</span>
          ${suggested === "en" ? `<span class="gate-badge">${remembered ? "Last used" : "Suggested"}</span>` : ""}
        </button>
      </div>
      <p class="gate-note"><span lang="zh-Hant">之後隨時可在頁首切換</span> · <span lang="en">Switch any time from the header</span></p>
    </div>`;
  document.body.appendChild(gate);
  document.body.classList.add("gate-open");
  const buttons = [...gate.querySelectorAll(".gate-option")];
  (buttons.find((b) => b.dataset.lang === suggested) || buttons[0]).focus();
  gate.addEventListener("keydown", (e) => {
    if (e.key === "ArrowLeft" || e.key === "ArrowRight" || e.key === "Tab") {
      e.preventDefault();
      buttons[(buttons.indexOf(document.activeElement) + 1) % 2].focus();
    }
  });
  buttons.forEach((b) =>
    b.addEventListener("click", () => {
      const lang = b.dataset.lang;
      b.classList.add("chosen");
      gate.classList.add("leaving");
      onChoose(lang);
      const done = () => {
        gate.remove();
        document.body.classList.remove("gate-open");
      };
      if (matchMedia("(prefers-reduced-motion: reduce)").matches) done();
      else setTimeout(done, 900);
    }),
  );
}

/* ───────────────────────── page ───────────────────────── */
function renderSite(lang) {
  const c = COPY[lang];
  const ex = EXAMPLES[lang];
  const tutorialHref = withLang("tutorial.html", lang);
  applyDocumentLang(lang);
  document.title = c.pageTitle;
  document.querySelector('meta[name="description"]').content = c.description;
  const anchors = ["#about", "#syntax", "#ecosystem", "#future"];
  const navLinks = anchors.map((a, i) => `<a href="${a}">${c.nav[i]}</a>`).join("");
  const tutorialNav = `<a class="nav-tutorial" href="${tutorialHref}">${book}<span>${c.navTutorial}</span></a>`;

  document.querySelector("#app").innerHTML = `
  <a class="skip-link" href="#about">${c.skip}</a>
  <header class="header">
    <a class="brand" href="#" aria-label="${c.homeLabel}">${star}<span>星泉<span class="brand-en">AQUARIUSLANG</span></span></a>
    <nav class="desktop-nav" aria-label="${c.mainNav}">${navLinks}${tutorialNav}</nav>
    <div class="header-actions"><button class="lang-switch" id="lang-switch" aria-label="${c.langSwitchLabel}" lang="${lang === "zh" ? "en" : "zh-Hant"}"><span class="lang-globe" aria-hidden="true">◐</span>${c.langSwitch}</button><a class="repo-link" href="${repo}" target="_blank" rel="noopener noreferrer">${github}<span>GitHub</span>${outward}</a><button class="menu-toggle" aria-expanded="false" aria-controls="mobile-nav" aria-label="${c.openMenu}"><span></span><span></span></button></div>
  </header>
  <nav id="mobile-nav" class="mobile-nav" aria-label="${c.mobileNav}" hidden>${tutorialNav}${navLinks}<button class="lang-switch mobile" data-lang-switch lang="${lang === "zh" ? "en" : "zh-Hant"}">${c.langSwitch}</button></nav>
  <main>
    <section class="hero" aria-labelledby="hero-title">
      <div class="hero-grain"></div>
      <div class="hero-copy">
        <div class="eyebrow hero-eyebrow"><span class="live-dot"></span>${c.heroEyebrow}<span class="tiny-cross">＋</span></div>
        <h1 id="hero-title" class="hero-title-${lang}">${c.heroTitle}</h1>
        <p class="hero-description">${c.heroText}</p>
        <div class="hero-cta"><a class="button primary tutorial-cta" href="${tutorialHref}">${book}${c.heroTutorial} ${arrow}</a><a class="text-link" href="#syntax">${c.heroExplore} ${arrow}</a></div>
        <div class="hero-tags">${c.heroTags.map((tag) => `<span>${tag}</span>`).join("<i></i>")}</div>
      </div>
      <div class="observatory" aria-label="${c.sceneLabel}">
        <canvas id="space-canvas" aria-label="${c.canvasLabel}"></canvas>
        <div class="scene-caption"><span class="scene-cross">＋</span> AQUARIUS / 水瓶座<span class="caption-coordinate">22h 20m · −10°</span></div>
        <svg class="constellation" viewBox="0 0 700 700" aria-hidden="true"><path d="m500 87-58 62 67 45 59-18 42 74-29 86"/><g><circle cx="500" cy="87" r="3"/><circle cx="442" cy="149" r="4"/><circle cx="509" cy="194" r="3"/><circle cx="568" cy="176" r="3"/><circle cx="610" cy="250" r="4"/><circle cx="581" cy="336" r="3"/></g></svg>
        <a class="space-label label-code" href="#syntax"><span class="node-dot"></span><span>${c.labels[0][0]}<small>${c.labels[0][1]}</small></span></a>
        <a class="space-label label-graphics" href="#capabilities"><span class="node-dot"></span><span>${c.labels[1][0]}<small>${c.labels[1][1]}</small></span></a>
        <a class="space-label label-future" href="#future"><span class="node-dot"></span><span>${c.labels[2][0]}<small>${c.labels[2][1]}</small></span></a>
        <div class="scene-controls"><span class="drag-hint"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M8 11V6a2 2 0 0 1 4 0v5m0-2a2 2 0 0 1 4 0v3m0-1a2 2 0 0 1 4 0v4c0 4-3 6-6 6h-2c-2 0-3-1-4-3l-3-5a2 2 0 0 1 3-2l2 2v-2" stroke="currentColor" stroke-width="1.3"/></svg>${c.dragHint}</span><button id="motion-toggle" class="icon-button" aria-label="${c.pause}" data-label-pause="${c.pause}" data-label-resume="${c.resume}" aria-pressed="false"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M9 7v10m6-10v10" stroke="currentColor" stroke-width="1.5"/></svg></button><button id="scene-reset" class="icon-button" aria-label="${c.resetView}"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M5 9a7 7 0 1 1 0 7M5 4v5h5" stroke="currentColor" stroke-width="1.5"/></svg></button></div>
      </div>
      <div class="hero-bottom"><a href="#about" class="scroll-link"><span>${c.scroll}</span><span>↓</span></a><span class="hero-footnote">A LANGUAGE. A SPRING. A UNIVERSE.</span><span class="edition">${c.edition}</span></div>
    </section>

    <div class="feature-ribbon"><span>${c.ribbonLead}</span><div>${c.ribbon.map((r) => `<span>${r}</span>`).join('<span class="ribbon-star">✧</span>')}</div></div>

    <section class="tutorial-banner reveal" aria-labelledby="tutorial-banner-title">
      <div class="tb-sky" aria-hidden="true"><span></span><span></span><span></span></div>
      <div class="tb-copy">
        <span class="tb-kicker"><i></i>${c.tutorialBanner.kicker}</span>
        <h2 id="tutorial-banner-title">${c.tutorialBanner.title}</h2>
        <p>${c.tutorialBanner.text}</p>
        <a class="button primary tb-cta" href="${tutorialHref}">${c.tutorialBanner.cta} ${arrow}</a>
      </div>
      <ol class="tb-path">
        ${c.tutorialBanner.steps.map(([level, title], i) => `<li style="--i:${i}"><a href="${withLang("tutorial.html", lang)}#${["hello", "functions", "types", "async"][i]}"><span class="tb-glyph">${["✦", "✧", "◇", "◎"][i]}</span><span><small>${String(i + 1).padStart(2, "0")} · ${level}</small>${title}</span></a></li>`).join("")}
      </ol>
    </section>

    <section id="about" class="section about-section reveal">
      <div class="section-marker"><span>${c.aboutMarker[0]}</span><span>${c.aboutMarker[1]}</span></div>
      <div class="about-layout"><div><h2>${c.aboutTitle}</h2><p class="section-intro">${c.aboutIntro}</p><a class="text-link subtle-link" href="${c.readme.split("#")[0]}${c.aboutAnchor}" target="_blank" rel="noopener noreferrer">${c.aboutLink} ${outward}</a></div><div class="name-stories">${c.stories.map(([sym, ch, en, text], i) => `<article><span class="story-symbol${i ? " water-symbol" : ""}">${sym}</span><div><h3>${ch} <small>${en}</small></h3><p>${text}</p></div></article>`).join("")}<p class="origin-quote">${c.quote}</p></div></div>
    </section>

    <section id="syntax" class="section syntax-section reveal">
      <div class="section-marker"><span>${c.syntaxMarker[0]}</span><span>${c.syntaxMarker[1]}</span></div>
      <div class="section-heading"><h2>${c.syntaxTitle}</h2><p>${c.syntaxIntro}</p></div>
      <div class="syntax-layout"><div class="syntax-notes"><span class="small-overline">AQUARIUS IN PRACTICE</span><h3 id="example-heading"></h3><p id="example-description"></p><div class="keyword-pair"><code id="keyword-english"></code></div><a class="text-link" id="example-tutorial" href="${tutorialHref}">${c.tryInTutorial} ${arrow}</a><a class="text-link subtle-link" href="${c.readme.split("#")[0]}${c.syntaxAnchor}" target="_blank" rel="noopener noreferrer">${c.syntaxLink} ${outward}</a></div>
      <div class="code-window"><div class="editor-top"><div class="window-dots"><i></i><i></i><i></i></div><span id="code-filename">hello.aqua</span><span class="editor-language">${c.codeLabel}</span></div><div class="code-tabs" role="tablist" aria-label="${c.exampleTabs}">${Object.entries(ex).map(([key, e], i) => `<button class="${i ? "" : "active"}" role="tab" aria-selected="${!i}" aria-controls="code-panel" id="tab-${key}" data-example="${key}" ${i ? 'tabindex="-1"' : ""}>${e.tab}</button>`).join("")}</div><div id="code-panel" role="tabpanel" aria-labelledby="tab-hello" tabindex="0"><pre id="code-content"></pre></div><div class="code-output"><span>${c.expected}</span><pre id="code-result" aria-live="polite"></pre><span class="output-note">${c.outputNote}</span></div><button id="copy-code" class="copy-code" aria-label="${c.copyLabel}"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><rect x="8" y="8" width="11" height="12" rx="2" stroke="currentColor" stroke-width="1.4"/><path d="M15 8V4H4v12h4" stroke="currentColor" stroke-width="1.4"/></svg><span>${c.copy}</span></button></div></div>
      <div class="syntax-footer">${c.syntaxFooter.map((f, i) => `<span><i>0${i + 1}</i> ${f}</span>`).join("")}</div>
    </section>

    <section id="capabilities" class="section capabilities-section reveal"><div class="section-marker"><span>${c.capMarker[0]}</span><span>${c.capMarker[1]}</span></div><div class="section-heading"><h2>${c.capTitle}</h2><p>${c.capIntro}</p></div><div class="capabilities">
      ${c.capabilities.map(([icon, title, text, href], i) => `<a class="capability-row" href="${href.startsWith("#") ? c.readme.split("#")[0] + href : repo + href}" target="_blank" rel="noopener noreferrer"><span class="capability-number">0${i + 1}</span><span class="capability-icon">${icon}</span><div><h3>${title}</h3><p>${text}</p></div><span class="status">${c.supported}</span>${outward}</a>`).join("")}
    </div><p class="capability-note">${c.capNote}<a href="${c.capNoteHref.startsWith("#") ? c.readme.split("#")[0] + c.capNoteHref : repo + c.capNoteHref}" target="_blank" rel="noopener noreferrer">${c.capNoteLink}</a></p></section>

    <section id="ecosystem" class="section ecosystem-section reveal"><div class="section-marker"><span>${c.ecoMarker[0]}</span><span>${c.ecoMarker[1]}</span></div><div class="section-heading"><h2>${c.ecoTitle}</h2><p>${c.ecoIntro}</p></div>
      <div class="ecosystem-layout"><div class="ecosystem-map"><div class="map-grid"></div><svg viewBox="0 0 700 520" class="orbit-lines" aria-hidden="true"><ellipse cx="350" cy="270" rx="260" ry="170"/><ellipse cx="350" cy="270" rx="190" ry="105" transform="rotate(-25 350 270)"/><path d="M350 270 180 130M350 270 500 100M350 270 570 300M350 270 380 435M350 270 125 355"/></svg><div class="map-center">${star}<span>星泉<small>AQUARIUSLANG</small></span></div>${Object.keys(NODES[lang]).map((key, i) => `<button class="map-node node-${i + 1}${NODES[lang][key].status === "planned" ? " planned-node" : ""}${i ? "" : " active"}" data-node="${key}" aria-pressed="${!i}"><span class="planet"></span><span>${c.nodeNames[key][0]}<small>${c.nodeNames[key][1]}</small></span></button>`).join("")}<div class="map-legend"><span><i></i>${c.current}</span><span><i></i>${c.planned}</span></div></div><article class="node-detail" aria-live="polite"><span class="node-index" id="node-index">01 / 05</span><span class="status" id="node-status"></span><h3 id="node-title"></h3><p id="node-description"></p><ul id="node-features"></ul><a id="node-link" class="text-link" href="${repo}" target="_blank" rel="noopener noreferrer"></a></article></div>
    </section>

    <section id="future" class="section future-section reveal"><div class="section-marker"><span>${c.futureMarker[0]}</span><span>${c.futureMarker[1]}</span></div><div class="section-heading"><h2>${c.futureTitle}</h2><p>${c.futureIntro}</p></div><div class="future-columns">${c.futureCols.map(([title, intro, items], i) => `<div><div class="future-column-title"><span>0${i + 1}</span><h3>${title}</h3><span class="plan-badge">${c.planned}</span></div><p class="future-column-intro">${intro}</p><ul>${items.map(([a, b]) => `<li><span>${a}</span><small>${b}</small></li>`).join("")}</ul></div>`).join("")}</div></section>

    <section class="join-section reveal"><div class="join-star">${star}</div><span class="small-overline">${c.joinKicker}</span><h2>${c.joinTitle}</h2><p>${c.joinText}</p><div class="join-actions"><a class="button primary" href="${tutorialHref}">${book}${c.heroTutorial} ${arrow}</a><a class="button ghost-button" href="${repo}" target="_blank" rel="noopener noreferrer">${c.joinCta} ${outward}</a></div><a class="join-issues" href="${repo}/issues" target="_blank" rel="noopener noreferrer">${c.joinIssues}</a></section>
  </main>
  <footer><a class="brand" href="#">${star}<span>星泉<span class="brand-en">AQUARIUSLANG</span></span></a><p>${c.footerText}</p><div><span>${c.author}</span><a href="${tutorialHref}">${c.navTutorial} ${arrow}</a><a href="${repo}" target="_blank" rel="noopener noreferrer">${c.openSource} ${outward}</a><a href="#">${c.backTop}</a></div></footer>
  <div id="toast" role="status" aria-live="polite"></div>
  `;
  bind(lang);
}

/* ───────────────────────── behaviour ───────────────────────── */
const escapeHtml = (text) => text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");
function highlight(line) {
  const tokens = /("[^"\n]*"|#.*$|\b\d+\b|變數|函式|如果|否則如果|否則|回傳|迴圈|中斷|印出|長度|\b(?:var|function|if|else|return|for|break|print|len)\b)/g;
  let output = "", last = 0;
  for (const match of line.matchAll(tokens)) {
    output += escapeHtml(line.slice(last, match.index));
    const token = match[0];
    const type = token.startsWith("#") ? "comment" : token.startsWith('"') ? "string" : /^\d+$/.test(token) ? "number" : ["印出", "長度", "print", "len"].includes(token) ? "builtin" : "keyword";
    output += `<span class="token-${type}">${escapeHtml(token)}</span>`;
    last = match.index + token.length;
  }
  return output + escapeHtml(line.slice(last));
}

function bind(lang) {
  const c = COPY[lang];
  const examples = EXAMPLES[lang];
  let currentExample = "hello";
  function showExample(name) {
    currentExample = name;
    const example = examples[name];
    document.querySelector("#code-filename").textContent = example.file;
    document.querySelector("#example-heading").textContent = example.heading;
    document.querySelector("#example-description").textContent = example.description;
    document.querySelector("#keyword-english").textContent = example.keyword;
    document.querySelector("#example-tutorial").href = withLang("tutorial.html", lang) + "#" + example.lesson;
    document.querySelector("#code-content").innerHTML = example.code
      .split("\n")
      .map((line, i) => `<span class="code-line"><span class="line-number" aria-hidden="true">${i + 1}</span><span>${highlight(line) || " "}</span></span>`)
      .join("");
    document.querySelector("#code-result").textContent = example.output;
    document.querySelector("#code-panel").setAttribute("aria-labelledby", `tab-${name}`);
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
      else if (event.key === "ArrowLeft") index = (index - 1 + tabs.length) % tabs.length;
      else if (event.key === "Home") index = 0;
      else if (event.key === "End") index = tabs.length - 1;
      else return;
      event.preventDefault();
      showExample(tabs[index].dataset.example);
      tabs[index].focus();
    });
  });

  let toastTimer;
  const toast = (message) => {
    const target = document.querySelector("#toast");
    target.textContent = message;
    target.classList.add("visible");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => target.classList.remove("visible"), 3000);
  };
  document.querySelector("#copy-code").addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(examples[currentExample].code);
      toast(c.copied);
    } catch {
      toast(c.copyFailed);
    }
  });

  const nodes = NODES[lang];
  const keys = Object.keys(nodes);
  function selectNode(name) {
    const node = nodes[name];
    const planned = node.status === "planned";
    document.querySelector("#node-index").textContent = String(keys.indexOf(name) + 1).padStart(2, "0") + " / 05";
    document.querySelector("#node-title").textContent = c.nodeNames[name][0];
    document.querySelector("#node-description").textContent = node.description;
    document.querySelector("#node-features").innerHTML = node.features.map((f) => `<li>${f}</li>`).join("");
    const status = document.querySelector("#node-status");
    status.textContent = planned ? c.planned : c.supported;
    status.classList.toggle("planned", planned);
    const link = document.querySelector("#node-link");
    link.href = node.url;
    link.innerHTML = node.url.startsWith("#") ? `${c.seePlans} ${arrow}` : `${c.more} ${outward}`;
    if (node.url.startsWith("#")) link.removeAttribute("target");
    else link.target = "_blank";
    document.querySelectorAll("[data-node]").forEach((button) => {
      const active = button.dataset.node === name;
      button.classList.toggle("active", active);
      button.setAttribute("aria-pressed", String(active));
    });
  }
  document.querySelectorAll("[data-node]").forEach((button) => button.addEventListener("click", () => selectNode(button.dataset.node)));
  selectNode("language");

  const menuToggle = document.querySelector(".menu-toggle");
  const mobileNav = document.querySelector("#mobile-nav");
  const closeMenu = () => {
    mobileNav.hidden = true;
    menuToggle.setAttribute("aria-expanded", "false");
    menuToggle.setAttribute("aria-label", c.openMenu);
  };
  menuToggle.addEventListener("click", () => {
    const expanded = menuToggle.getAttribute("aria-expanded") !== "true";
    menuToggle.setAttribute("aria-expanded", String(expanded));
    menuToggle.setAttribute("aria-label", expanded ? c.closeMenu : c.openMenu);
    mobileNav.hidden = !expanded;
  });
  mobileNav.querySelectorAll("a").forEach((link) => link.addEventListener("click", closeMenu));
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") closeMenu();
  });

  document.querySelectorAll("#lang-switch, [data-lang-switch]").forEach((b) =>
    b.addEventListener("click", () => {
      const next = lang === "zh" ? "en" : "zh";
      rememberLang(next);
      switchLanguage(next);
    }),
  );

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
  document.querySelectorAll(".reveal").forEach((section) => revealObserver.observe(section));
  const navObserver = new IntersectionObserver(
    (entries) => {
      for (const entry of entries)
        if (entry.isIntersecting)
          document.querySelectorAll(".desktop-nav a").forEach((link) => link.classList.toggle("current", link.hash === "#" + entry.target.id));
    },
    { rootMargin: "-20% 0px -55% 0px" },
  );
  document.querySelectorAll("main section[id]").forEach((section) => navObserver.observe(section));
}

async function startScene() {
  const { createObservatory } = await import("./scene.js");
  createObservatory(document.querySelector("#space-canvas"));
}

// The 3D scene owns a WebGL context, so a language switch loads the page again
// in the other language instead of rebuilding it in place.
function switchLanguage(lang) {
  document.body.classList.add("lang-leaving");
  const url = new URL(location.href);
  url.searchParams.set("lang", lang);
  setTimeout(() => location.replace(url), matchMedia("(prefers-reduced-motion: reduce)").matches ? 0 : 280);
}

/* ───────────────────────── start ───────────────────────── */
const initial = urlLang();
if (initial) {
  rememberLang(initial);
  renderSite(initial);
  startScene();
} else {
  // The start of the site: ask which language to read in.
  showLanguageGate((lang) => {
    rememberLang(lang);
    writeUrlLang(lang);
    renderSite(lang);
    startScene();
  });
}

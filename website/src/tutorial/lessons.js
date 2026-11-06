// Tutorial content: four chapters from first line to shipping apps.
// Every runnable example's output is checked by `npm run check:tutorial`.
// Only the core language, the default libraries in AquariusLangTW and public
// Aquarius-Language repositories are mentioned here.

export const chapters = [
  { id: "first-light", level: { zh: "入門", en: "Beginner" }, title: { zh: "星光初現", en: "First Light" }, glyph: "✦", hue: 168 },
  { id: "constellations", level: { zh: "初階", en: "Explorer" }, title: { zh: "連成星座", en: "Constellations" }, glyph: "✧", hue: 196 },
  { id: "star-forge", level: { zh: "中階", en: "Builder" }, title: { zh: "鍛造星辰", en: "Star Forge" }, glyph: "◇", hue: 262 },
  { id: "galaxy", level: { zh: "高階", en: "Advanced" }, title: { zh: "航向銀河", en: "Into the Galaxy" }, glyph: "◎", hue: 32 },
];

export const lessons = [
  /* ───────────── Chapter 1: First Light ───────────── */
  {
    id: "hello",
    chapter: 0,
    title: { zh: "第一道星光", en: "Your first light" },
    tagline: { zh: "印出 / print：讓程式開口說話", en: "print / 印出: make a program speak" },
    body: {
      zh: `<p>每段旅程都從一句問候開始。<code>印出</code> 會把值顯示在終端機上，英文寫作 <code>print</code>，兩者完全相同。</p>
<p>星泉最特別的地方：<strong>繁體中文與英文關鍵字可以在同一個檔案裡混用</strong>，不需要任何設定。</p>
<ul><li>以 <code>;</code> 結束一行敘述</li><li><code>#</code> 之後是註解，程式會略過</li><li>字串用雙引號 <code>"..."</code> 包住</li><li><code>印出</code> 接受多個值，以空白分隔輸出</li></ul>`,
      en: `<p>Every journey starts with a greeting. <code>print</code> shows values in the terminal. Its Traditional Chinese twin <code>印出</code> does exactly the same.</p>
<p>Here is what makes AquariusLang special: <strong>Chinese and English keywords can be mixed in the same file</strong>, with no setting to switch.</p>
<ul><li>End a statement with <code>;</code></li><li>Everything after <code>#</code> is a comment</li><li>Strings live inside double quotes <code>"..."</code></li><li><code>print</code> takes several values and separates them with spaces</li></ul>`,
    },
    code: {
      zh: `# 你的第一個星泉程式
印出("你好，星泉！");
print("Hello, Aquarius!");
印出("一", "二", "三");`,
      en: `# Your first AquariusLang program
print("Hello, Aquarius!");
印出("你好，星泉！");
print("one", "two", "three");`,
    },
    output: { zh: "你好，星泉！\nHello, Aquarius!\n一 二 三", en: "Hello, Aquarius!\n你好，星泉！\none two three" },
    quiz: {
      q: { zh: "下面哪一行與 <code>印出(\"嗨\");</code> 的效果完全相同？", en: "Which line does exactly what <code>print(\"Hi\");</code> does?" },
      options: [
        { zh: `<code>print("嗨");</code>`, en: `<code>印出("Hi");</code>` },
        { zh: `<code>echo("嗨");</code>`, en: `<code>echo("Hi");</code>` },
        { zh: `<code>印出 "嗨";</code>`, en: `<code>print "Hi";</code>` },
      ],
      answer: 0,
      explain: { zh: "<code>印出</code> 與 <code>print</code> 是同一個內建函式的兩個名字，呼叫時都需要括號。", en: "<code>print</code> and <code>印出</code> are two names of one builtin, and both are called with parentheses." },
    },
    tryIt: { zh: "把問候改成你的名字，再按「執行」。", en: "Change the greeting to your own name, then press Run." },
  },
  {
    id: "variables",
    chapter: 0,
    title: { zh: "收集星塵：變數", en: "Collecting stardust: variables" },
    tagline: { zh: "變數 / var 與五種基本值", en: "var / 變數 and the basic values" },
    body: {
      zh: `<p>用 <code>變數</code>（<code>var</code>）為值取名字，之後可以再改變它。名字可以是中文、英文，甚至混合。</p>
<ul><li><strong>整數</strong>：<code>42</code>、<code>1_000_000</code>，32 位元</li><li><strong>雙精度</strong>：<code>3.14</code>、<code>2d</code></li><li><strong>字串</strong>：<code>"星泉"</code></li><li><strong>布林</strong>：<code>真</code> / <code>假</code>（<code>true</code> / <code>false</code>）</li><li><strong>空值</strong>：<code>空值</code>（<code>null</code>）</li></ul>
<p><code>型別(值)</code> 會告訴你一個值是什麼種類。布林值印出時固定顯示為「真」或「假」。</p>`,
      en: `<p>Give a value a name with <code>var</code> (<code>變數</code>). You can change it later. Names can be English, Chinese or a mix.</p>
<ul><li><strong>Integers</strong>: <code>42</code>, <code>1_000_000</code> (32-bit)</li><li><strong>Doubles</strong>: <code>3.14</code>, <code>2d</code></li><li><strong>Strings</strong>: <code>"Aquarius"</code></li><li><strong>Booleans</strong>: <code>true</code> / <code>false</code> (<code>真</code> / <code>假</code>)</li><li><strong>Null</strong>: <code>null</code> (<code>空值</code>)</li></ul>
<p><code>typeOf(value)</code> tells you what kind of value you have. Booleans always print as 真 (true) and 假 (false).</p>`,
    },
    code: {
      zh: `變數 星星數量 = 3;
變數 亮度 = 4.5;
變數 名稱 = "織女星";
變數 看得見 = 真;

星星數量 = 星星數量 + 1;
印出(名稱, 星星數量, 亮度, 看得見);
印出(型別(星星數量), 型別(亮度), 型別(名稱));`,
      en: `var starCount = 3;
var brightness = 4.5;
var name = "Vega";
var visible = true;

starCount = starCount + 1;
print(name, starCount, brightness, visible);
print(typeOf(starCount), typeOf(brightness), typeOf(name));`,
    },
    output: { zh: "織女星 4 4.5 真\ninteger double string", en: "Vega 4 4.5 真\ninteger double string" },
    quiz: {
      q: { zh: "<code>印出(型別(2d));</code> 會印出什麼？", en: "What does <code>print(typeOf(2d));</code> print?" },
      options: [{ zh: "integer", en: "integer" }, { zh: "double", en: "double" }, { zh: "string", en: "string" }],
      answer: 1,
      explain: { zh: "後綴 <code>d</code> 讓數字成為雙精度浮點數，即使它沒有小數點。", en: "The <code>d</code> suffix makes a double even without a decimal point." },
    },
    tryIt: { zh: "新增一個 <code>空值</code> 變數，看看 <code>型別</code> 回傳什麼。", en: "Add a variable set to <code>null</code> and see what <code>typeOf</code> says." },
  },
  {
    id: "operators",
    chapter: 0,
    title: { zh: "星際算術", en: "Cosmic arithmetic" },
    tagline: { zh: "運算子、整數除法與 ++", en: "Operators, integer division and ++" },
    body: {
      zh: `<p>星泉有你熟悉的 <code>+ - * / %</code>。要注意一個細節：<strong>兩個整數相除，結果仍是整數</strong>（向零截斷），只要有一邊是雙精度就會得到小數。</p>
<ul><li><code>+=</code>、<code>-=</code>、<code>*=</code> 等複合指派</li><li><code>值++</code> 回傳舊值；<code>++值</code> 回傳新值</li><li>兩個字串可以用 <code>+</code> 串接</li><li>整數超出 32 位元會環繞，所有平台結果一致</li></ul>`,
      en: `<p>You get the familiar <code>+ - * / %</code>. One detail matters: <strong>dividing two integers gives an integer</strong> (truncated toward zero). Make either side a double to get a fraction.</p>
<ul><li>Compound assignment: <code>+=</code>, <code>-=</code>, <code>*=</code> ...</li><li><code>value++</code> returns the old value, <code>++value</code> the new one</li><li><code>+</code> also joins two strings</li><li>Integers wrap at 32 bits, identically on every platform</li></ul>`,
    },
    code: {
      zh: `印出(7 / 2, 7.0 / 2, 7 % 2);

變數 燃料 = 10;
燃料 += 5;
燃料 *= 2;
印出("燃料", 燃料);

變數 步數 = 1;
印出(步數++, 步數, ++步數);`,
      en: `print(7 / 2, 7.0 / 2, 7 % 2);

var fuel = 10;
fuel += 5;
fuel *= 2;
print("fuel", fuel);

var steps = 1;
print(steps++, steps, ++steps);`,
    },
    output: { zh: "3 3.5 1\n燃料 30\n1 2 3", en: "3 3.5 1\nfuel 30\n1 2 3" },
    quiz: {
      q: { zh: "<code>印出(9 / 4);</code> 的結果是？", en: "What does <code>print(9 / 4);</code> show?" },
      options: [{ zh: "2.25", en: "2.25" }, { zh: "2", en: "2" }, { zh: "3", en: "3" }],
      answer: 1,
      explain: { zh: "兩個整數相除會向零截斷，所以是 2。寫成 <code>9.0 / 4</code> 才會得到 2.25。", en: "Integer division truncates toward zero, so it's 2. Write <code>9.0 / 4</code> to get 2.25." },
    },
    tryIt: { zh: "試試 <code>-7 / 2</code> 與 <code>-7 % 3</code>。", en: "Try <code>-7 / 2</code> and <code>-7 % 3</code>." },
  },
  {
    id: "conditions",
    chapter: 0,
    title: { zh: "岔路與抉擇", en: "Forks in the path" },
    tagline: { zh: "如果 / 否則如果 / 否則", en: "if / else if / else" },
    body: {
      zh: `<p>用 <code>如果</code>（<code>if</code>）讓程式依條件選擇路線。條件必須是布林值。</p>
<ul><li><code>否則如果</code> 也可以寫成 <code>else if</code>、<code>elif</code> 或 <code>否則 如果</code></li><li>比較：<code>== != &lt; &gt; &lt;= &gt;=</code></li><li>邏輯：<code>&amp;&amp;</code>（且）、<code>||</code>（或）、<code>!</code>（非），會短路求值</li><li><code>如果</code> 也是運算式：可以直接取得它的值</li></ul>`,
      en: `<p><code>if</code> (<code>如果</code>) picks a path based on a condition, which must be a boolean.</p>
<ul><li>Write extra branches as <code>else if</code>, <code>elif</code> or <code>否則如果</code></li><li>Compare with <code>== != &lt; &gt; &lt;= &gt;=</code></li><li>Combine with <code>&amp;&amp;</code>, <code>||</code> and <code>!</code>, which short-circuit</li><li><code>if</code> is also an expression, so it can produce a value</li></ul>`,
    },
    code: {
      zh: `變數 描述星光 = 函式(亮度) {
    如果 (亮度 > 8) {
        回傳 "耀眼";
    } 否則如果 (亮度 > 4 && 亮度 <= 8) {
        回傳 "閃爍";
    } 否則 {
        回傳 "微光";
    }
};
印出(描述星光(9), 描述星光(5), 描述星光(1));

變數 夜晚 = 真;
變數 天空 = 如果 (夜晚) { "滿天星斗"; } 否則 { "一片蔚藍"; };
印出(天空);`,
      en: `var describe = function(brightness) {
    if (brightness > 8) {
        return "dazzling";
    } else if (brightness > 4 && brightness <= 8) {
        return "twinkling";
    } else {
        return "faint";
    }
};
print(describe(9), describe(5), describe(1));

var night = true;
var sky = if (night) { "full of stars"; } else { "bright blue"; };
print(sky);`,
    },
    output: { zh: "耀眼 閃爍 微光\n滿天星斗", en: "dazzling twinkling faint\nfull of stars" },
    quiz: {
      q: { zh: "以下哪一種寫法 <strong>不是</strong> 合法的「否則如果」？", en: "Which spelling is <strong>not</strong> a valid else-if?" },
      options: [{ zh: "<code>elif</code>", en: "<code>elif</code>" }, { zh: "<code>否則 如果</code>", en: "<code>否則 如果</code>" }, { zh: "<code>elseif</code>", en: "<code>elseif</code>" }],
      answer: 2,
      explain: { zh: "<code>else if</code> 是兩個字；<code>elseif</code> 會被當成一個普通的名稱。", en: "<code>else if</code> is two words; <code>elseif</code> would be read as an ordinary name." },
    },
    tryIt: { zh: "加一個「看不見」的分支：亮度小於 0 時回傳別的文字。", en: "Add a branch for brightness below 0 that returns something new." },
  },
  {
    id: "loops",
    chapter: 0,
    title: { zh: "環繞軌道：迴圈", en: "Orbits: loops" },
    tagline: { zh: "迴圈 / for、中斷 / break、繼續 / continue", en: "for / 迴圈, break / 中斷, continue / 繼續" },
    body: {
      zh: `<p>星泉只有一種迴圈：<code>迴圈 (初始; 條件; 更新) { ... }</code>。三個部分都可以省略，<code>迴圈 (;;)</code> 會一直執行直到 <code>中斷</code>。</p>
<ul><li><code>中斷</code>（<code>break</code>）立刻離開迴圈</li><li><code>繼續</code>（<code>continue</code>）跳到下一輪</li><li>迴圈變數只存在於迴圈內</li></ul>`,
      en: `<p>AquariusLang has one loop: <code>for (start; condition; update) { ... }</code>. Each part is optional, and <code>for (;;)</code> runs until a <code>break</code>.</p>
<ul><li><code>break</code> (<code>中斷</code>) leaves the loop at once</li><li><code>continue</code> (<code>繼續</code>) jumps to the next round</li><li>The loop variable lives only inside the loop</li></ul>`,
    },
    code: {
      zh: `# 數一數偶數的星星
迴圈 (變數 i = 1; i <= 10; i++) {
    如果 (i % 2 == 1) { 繼續; }
    如果 (i > 8) { 中斷; }
    印出("第", i, "顆星");
}

變數 倒數 = 3;
迴圈 (;;) {
    印出(倒數);
    倒數--;
    如果 (倒數 == 0) { 中斷; }
}
印出("發射！");`,
      en: `# Count the even stars
for (var i = 1; i <= 10; i++) {
    if (i % 2 == 1) { continue; }
    if (i > 8) { break; }
    print("star", i);
}

var countdown = 3;
for (;;) {
    print(countdown);
    countdown--;
    if (countdown == 0) { break; }
}
print("Liftoff!");`,
    },
    output: { zh: "第 2 顆星\n第 4 顆星\n第 6 顆星\n第 8 顆星\n3\n2\n1\n發射！", en: "star 2\nstar 4\nstar 6\nstar 8\n3\n2\n1\nLiftoff!" },
    quiz: {
      q: { zh: "這段程式印出幾行？<pre>迴圈 (變數 i = 0; i < 5; i++) {\n    如果 (i == 3) { 中斷; }\n    印出(i);\n}</pre>", en: "How many lines does this print?<pre>for (var i = 0; i < 5; i++) {\n    if (i == 3) { break; }\n    print(i);\n}</pre>" },
      options: [{ zh: "3", en: "3" }, { zh: "4", en: "4" }, { zh: "5", en: "5" }],
      answer: 0,
      explain: { zh: "印出 0、1、2，當 i 等於 3 時就中斷了。", en: "It prints 0, 1 and 2, then breaks when i reaches 3." },
    },
    tryIt: { zh: "改寫成印出 1 到 20 中 3 的倍數。", en: "Make it print the multiples of 3 from 1 to 20." },
  },

  /* ───────────── Chapter 2: Constellations ───────────── */
  {
    id: "functions",
    chapter: 1,
    title: { zh: "召喚咒語：函式", en: "Spells: functions" },
    tagline: { zh: "函式、預設參數與箭頭函式", en: "Functions, defaults and arrow functions" },
    body: {
      zh: `<p>函式是可以重複使用的邏輯。寫成 <code>變數 名稱 = 函式(參數) { ... };</code>，用 <code>回傳</code> 交出結果；若沒寫 <code>回傳</code>，最後一個運算式就是結果。</p>
<ul><li>參數可以有預設值：<code>函式(名字, 問候 = "你好")</code></li><li>呼叫時可以用名稱指定參數：<code>打招呼("小明", 問候: "嗨")</code></li><li>短函式：<code>x => x * 2</code>、<code>(a, b) => a + b</code></li><li>函式可以遞迴呼叫自己</li></ul>`,
      en: `<p>A function is reusable logic. Write <code>var name = function(params) { ... };</code> and hand back a result with <code>return</code>. Without one, the last expression is the result.</p>
<ul><li>Parameters can have defaults: <code>function(name, greeting = "Hello")</code></li><li>Pass parameters by name: <code>greet("Mia", greeting: "Hi")</code></li><li>Short functions: <code>x => x * 2</code>, <code>(a, b) => a + b</code></li><li>Functions can call themselves recursively</li></ul>`,
    },
    code: {
      zh: `變數 打招呼 = 函式(名字, 問候 = "你好") {
    回傳 問候 + "，" + 名字 + "！";
};
印出(打招呼("小明"));
印出(打招呼("小華", 問候: "早安"));

變數 平方 = x => x * x;
變數 相加 = (a, b) => a + b;
印出(平方(7), 相加(2, 3));

變數 階乘 = 函式(n) {
    如果 (n <= 1) { 回傳 1; }
    n * 階乘(n - 1);
};
印出(階乘(5));`,
      en: `var greet = function(name, greeting = "Hello") {
    return greeting + ", " + name + "!";
};
print(greet("Mia"));
print(greet("Leo", greeting: "Good morning"));

var square = x => x * x;
var add = (a, b) => a + b;
print(square(7), add(2, 3));

var factorial = function(n) {
    if (n <= 1) { return 1; }
    n * factorial(n - 1);
};
print(factorial(5));`,
    },
    output: { zh: "你好，小明！\n早安，小華！\n49 5\n120", en: "Hello, Mia!\nGood morning, Leo!\n49 5\n120" },
    quiz: {
      q: { zh: "<code>變數 f = (a, b = 10) => a + b;</code><br><code>印出(f(1));</code> 會印出？", en: "<code>var f = (a, b = 10) => a + b;</code><br>What does <code>print(f(1));</code> show?" },
      options: [{ zh: "1", en: "1" }, { zh: "11", en: "11" }, { zh: "錯誤：缺少參數", en: "An error: missing argument" }],
      answer: 1,
      explain: { zh: "b 沒有傳入，所以使用預設值 10。", en: "b is left out, so it takes its default of 10." },
    },
    tryIt: { zh: "寫一個 <code>費波那契(n)</code> 函式，印出第 10 項（答案是 55）。", en: "Write <code>fib(n)</code> and print fib(10), which is 55." },
  },
  {
    id: "arrays",
    chapter: 1,
    title: { zh: "星群：陣列", en: "Star clusters: arrays" },
    tagline: { zh: "陣列、索引與 陣列 模組", en: "Arrays, indexes and the Array module" },
    body: {
      zh: `<p>陣列用 <code>[ ]</code> 收集一串值，索引從 0 開始。<code>長度(陣列)</code> 取得數量；超出範圍的讀取會得到 <code>空值</code>。</p>
<p>內建的 <code>陣列</code>（<code>Array</code>）模組不需匯入，提供常用操作：</p>
<ul><li><code>追加</code>／<code>Append</code>：原地加入一個值</li><li><code>排序</code>、<code>總和</code>、<code>最大值</code></li><li><code>映射</code>／<code>Map</code>、<code>篩選</code>／<code>Filter</code>、<code>歸約</code>／<code>Reduce</code></li><li><code>...陣列</code> 可以把另一個陣列展開到新陣列中</li></ul>`,
      en: `<p>An array collects values in <code>[ ]</code>, indexed from 0. <code>len(array)</code> counts them, and reading past the end gives <code>null</code>.</p>
<p>The built-in <code>Array</code> (<code>陣列</code>) module needs no import:</p>
<ul><li><code>Append</code>: add a value in place</li><li><code>Sort</code>, <code>Sum</code>, <code>Max</code></li><li><code>Map</code>, <code>Filter</code>, <code>Reduce</code></li><li><code>...array</code> spreads another array into a new one</li></ul>`,
    },
    code: {
      zh: `變數 星等 = [5, 2, 9, 1];
陣列.追加(星等, 7);
印出(星等, 長度(星等), 星等[0]);

陣列.排序(星等);
印出("排序後", 星等);
印出("總和", 陣列.總和(星等), "最亮", 陣列.最大值(星等));

變數 加倍 = 陣列.映射(星等, x => x * 2);
變數 明亮 = 陣列.篩選(星等, x => x > 4);
印出(加倍, 明亮);
印出([0, ...明亮, 10]);`,
      en: `var magnitudes = [5, 2, 9, 1];
Array.Append(magnitudes, 7);
print(magnitudes, len(magnitudes), magnitudes[0]);

Array.Sort(magnitudes);
print("sorted", magnitudes);
print("sum", Array.Sum(magnitudes), "brightest", Array.Max(magnitudes));

var doubled = Array.Map(magnitudes, x => x * 2);
var bright = Array.Filter(magnitudes, x => x > 4);
print(doubled, bright);
print([0, ...bright, 10]);`,
    },
    output: {
      zh: "[5, 2, 9, 1, 7] 5 5\n排序後 [1, 2, 5, 7, 9]\n總和 24 最亮 9\n[2, 4, 10, 14, 18] [5, 7, 9]\n[0, 5, 7, 9, 10]",
      en: "[5, 2, 9, 1, 7] 5 5\nsorted [1, 2, 5, 7, 9]\nsum 24 brightest 9\n[2, 4, 10, 14, 18] [5, 7, 9]\n[0, 5, 7, 9, 10]",
    },
    quiz: {
      q: { zh: "<code>變數 a = [10, 20, 30];</code><br><code>印出(a[3]);</code> 會印出？", en: "<code>var a = [10, 20, 30];</code><br>What does <code>print(a[3]);</code> show?" },
      options: [{ zh: "30", en: "30" }, { zh: "null", en: "null" }, { zh: "錯誤：索引超出範圍", en: "An index error" }],
      answer: 1,
      explain: { zh: "索引從 0 開始，a[3] 超出範圍。未標註型別的陣列讀取越界時回傳空值。", en: "Indexes start at 0, so a[3] is past the end. Reading past the end of an untyped array gives null." },
    },
    tryIt: { zh: "用 <code>陣列.歸約</code> 算出所有星等的乘積。", en: "Use <code>Array.Reduce</code> to multiply all the magnitudes." },
  },
  {
    id: "hashes-strings",
    chapter: 1,
    title: { zh: "星圖索引：雜湊與字串", en: "Star charts: hashes and strings" },
    tagline: { zh: "雜湊表 {鍵: 值} 與 字串 模組", en: "Hashes {key: value} and the String module" },
    body: {
      zh: `<p><strong>雜湊表</strong>以鍵找值：<code>{"織女星": 0, "牛郎星": 1}</code>。用 <code>表[鍵]</code> 讀寫，找不到的鍵會得到空值。<code>雜湊</code>／<code>Hash</code> 模組列出鍵、檢查鍵是否存在等。</p>
<p><code>字串</code>／<code>String</code> 模組處理文字：分割、合併、取代、尋找、大小寫轉換。</p>
<ul><li>雜湊表保留插入順序</li><li><code>長度()</code> 也能計算雜湊表的項目數</li><li>字串也能用 <code>[索引]</code> 取得單一字元</li></ul>`,
      en: `<p>A <strong>hash</strong> looks values up by key: <code>{"Vega": 0, "Altair": 1}</code>. Read and write with <code>table[key]</code>; a missing key gives null. The <code>Hash</code> (<code>雜湊</code>) module lists keys, checks membership and more.</p>
<p>The <code>String</code> (<code>字串</code>) module splits, joins, replaces, searches and changes case.</p>
<ul><li>Hashes keep insertion order</li><li><code>len()</code> counts hash entries too</li><li><code>text[index]</code> reads one character</li></ul>`,
    },
    code: {
      zh: `變數 距離 = {"織女星": 25, "牛郎星": 17};
距離["天狼星"] = 9;
印出(雜湊.鍵列表(距離));
印出(距離["牛郎星"], 雜湊.包含鍵(距離, "北極星"));
印出(雜湊.取得(距離, "北極星", "未知"));

變數 句子 = "星光,泉水,靈感";
變數 片段 = 字串.分割(句子, ",");
印出(片段, 長度(片段));
印出(字串.合併(片段, " ✦ "));
印出(字串.轉大寫("aquarius"), 字串.取代("我愛程式", "程式", "星泉"));`,
      en: `var distance = {"Vega": 25, "Altair": 17};
distance["Sirius"] = 9;
print(Hash.Keys(distance));
print(distance["Altair"], Hash.Has(distance, "Polaris"));
print(Hash.Get(distance, "Polaris", "unknown"));

var sentence = "starlight,spring,idea";
var parts = String.Split(sentence, ",");
print(parts, len(parts));
print(String.Join(parts, " ✦ "));
print(String.Upper("aquarius"), String.Replace("I love code", "code", "stars"));`,
    },
    output: {
      zh: "[織女星, 牛郎星, 天狼星]\n17 假\n未知\n[星光, 泉水, 靈感] 3\n星光 ✦ 泉水 ✦ 靈感\nAQUARIUS 我愛星泉",
      en: "[Vega, Altair, Sirius]\n17 假\nunknown\n[starlight, spring, idea] 3\nstarlight ✦ spring ✦ idea\nAQUARIUS I love stars",
    },
    quiz: {
      q: { zh: "<code>變數 h = {\"a\": 1};</code><br><code>印出(h[\"b\"]);</code> 會印出？", en: "<code>var h = {\"a\": 1};</code><br>What does <code>print(h[\"b\"]);</code> show?" },
      options: [{ zh: "0", en: "0" }, { zh: "null", en: "null" }, { zh: "錯誤：找不到鍵", en: "A missing-key error" }],
      answer: 1,
      explain: { zh: "不存在的鍵回傳空值。想要預設值時可用 <code>雜湊.取得(表, 鍵, 預設)</code>。", en: "A missing key gives null. Use <code>Hash.Get(table, key, default)</code> when you want a fallback." },
    },
    tryIt: { zh: "用迴圈走訪 <code>雜湊.鍵列表(距離)</code>，印出每顆星與它的距離。", en: "Loop over <code>Hash.Keys(distance)</code> and print each star with its distance." },
  },
  {
    id: "closures",
    chapter: 1,
    title: { zh: "記憶的星光：閉包", en: "Remembered light: closures" },
    tagline: { zh: "作用域、閉包與雙名函式", en: "Scope, closures and two-name functions" },
    body: {
      zh: `<p>函式會「記住」它被建立時周圍的變數，這就是<strong>閉包</strong>。每次呼叫 <code>建立計數器()</code> 都得到一個擁有自己計數的新函式。</p>
<ul><li>變數屬於宣告它的區塊；內層可以讀寫外層的變數</li><li>迴圈的每一輪都有自己的變數副本，閉包會保留那一輪的值</li><li><code>變數 加法, add = 函式(...)</code> 讓同一個函式有中文與英文兩個名字，方便雙語函式庫</li></ul>`,
      en: `<p>A function remembers the variables around it when it was created: that's a <strong>closure</strong>. Each call to <code>makeCounter()</code> returns a new function with its own count.</p>
<ul><li>A variable belongs to the block that declares it; inner code can read and write outer variables</li><li>Each loop round gets its own copy of the loop variable, so closures keep that round's value</li><li><code>var add, 加法 = function(...)</code> gives one function an English and a Chinese name, perfect for bilingual libraries</li></ul>`,
    },
    code: {
      zh: `變數 建立計數器 = 函式() {
    變數 次數 = 0;
    回傳 函式() { 次數++; 回傳 次數; };
};
變數 甲 = 建立計數器();
變數 乙 = 建立計數器();
甲(); 甲();
印出(甲(), 乙());

變數 回呼 = [];
迴圈 (變數 i = 0; i < 3; i++) {
    陣列.追加(回呼, () => i * 10);
}
印出(回呼[0](), 回呼[1](), 回呼[2]());

變數 加法, add = 函式(a, b) { a + b; };
印出(加法(1, 2), add(3, 4));`,
      en: `var makeCounter = function() {
    var count = 0;
    return function() { count++; return count; };
};
var a = makeCounter();
var b = makeCounter();
a(); a();
print(a(), b());

var callbacks = [];
for (var i = 0; i < 3; i++) {
    Array.Append(callbacks, () => i * 10);
}
print(callbacks[0](), callbacks[1](), callbacks[2]());

var add, 加法 = function(x, y) { x + y; };
print(add(1, 2), 加法(3, 4));`,
    },
    output: { zh: "3 1\n0 10 20\n3 7", en: "3 1\n0 10 20\n3 7" },
    quiz: {
      q: { zh: "上面的程式中，為什麼 <code>乙()</code> 印出 1 而不是 4？", en: "In the example, why does <code>b()</code> print 1 and not 4?" },
      options: [
        { zh: "每個計數器都捕獲了自己的「次數」變數", en: "Each counter captured its own count variable" },
        { zh: "乙 比 甲 晚建立，所以會重設全域變數", en: "b was created later, so it reset a global" },
        { zh: "閉包只能被呼叫一次", en: "A closure can only be called once" },
      ],
      answer: 0,
      explain: { zh: "每次呼叫 <code>建立計數器()</code> 都會建立新的作用域與新的「次數」。", en: "Each <code>makeCounter()</code> call creates a fresh scope with a fresh count." },
    },
    tryIt: { zh: "讓 <code>建立計數器(起點)</code> 接受一個起始值。", en: "Let <code>makeCounter(start)</code> take a starting value." },
  },

  /* ───────────── Chapter 3: Star Forge ───────────── */
  {
    id: "types",
    chapter: 2,
    title: { zh: "定型的星核：型別", en: "Solid cores: optional types" },
    tagline: { zh: "選用型別標註、常數與轉換", en: "Optional type annotations, constants and conversions" },
    body: {
      zh: `<p>型別是<strong>選用</strong>的：在名稱後加 <code>: 型別</code>，編譯器就能抓出錯誤，而且完整標註型別的函式會編譯成更快的原生 WebAssembly。</p>
<table><tr><th>中文</th><th>English</th><th>意義</th></tr><tr><td><code>整數</code></td><td><code>int</code></td><td>32 位元整數</td></tr><tr><td><code>雙精度</code></td><td><code>double</code></td><td>64 位元浮點數</td></tr><tr><td><code>布林</code></td><td><code>bool</code></td><td>真／假</td></tr><tr><td><code>字串</code></td><td><code>string</code></td><td>文字</td></tr><tr><td><code>整數[]</code></td><td><code>int[]</code></td><td>固定元素型別的陣列</td></tr></table>
<ul><li>整數會自動加寬成雙精度；反方向需要 <code>整數(x)</code>（向零截斷）</li><li><code>常數</code>（<code>const</code>）宣告後不能再指派</li></ul>`,
      en: `<p>Types are <strong>optional</strong>: add <code>: type</code> after a name and the compiler catches mistakes. Fully typed functions also compile to faster native WebAssembly.</p>
<table><tr><th>English</th><th>中文</th><th>Meaning</th></tr><tr><td><code>int</code></td><td><code>整數</code></td><td>32-bit integer</td></tr><tr><td><code>double</code></td><td><code>雙精度</code></td><td>64-bit float</td></tr><tr><td><code>bool</code></td><td><code>布林</code></td><td>true / false</td></tr><tr><td><code>string</code></td><td><code>字串</code></td><td>text</td></tr><tr><td><code>int[]</code></td><td><code>整數[]</code></td><td>array with a fixed element type</td></tr></table>
<ul><li>Integers widen to double automatically; the other way needs <code>int(x)</code>, which truncates toward zero</li><li><code>const</code> (<code>常數</code>) can never be assigned again</li></ul>`,
    },
    code: {
      zh: `常數 光速: 整數 = 299792;
變數 費氏 = 函式(n: 整數): 整數 {
    如果 (n < 2) { 回傳 n; }
    回傳 費氏(n - 1) + 費氏(n - 2);
};
印出(費氏(20));

變數 比例 = 函式(a: 整數, b: 整數): 雙精度 {
    回傳 雙精度(a) / b;
};
印出(比例(1, 4), 整數(3.99), 整數(-3.99));

變數 半徑: 雙精度 = 2;   # 整數自動加寬
印出(半徑 * 半徑 * 數學.圓周率);

變數 計數: 整數 = 0;
計數 = 2.5;              # 型別不符，會出錯！`,
      en: `const speedOfLight: int = 299792;
var fib = function(n: int): int {
    if (n < 2) { return n; }
    return fib(n - 1) + fib(n - 2);
};
print(fib(20));

var ratio = function(a: int, b: int): double {
    return double(a) / b;
};
print(ratio(1, 4), int(3.99), int(-3.99));

var radius: double = 2;   # the integer widens
print(radius * radius * Math.PI);

var count: int = 0;
count = 2.5;              # type mismatch: an error!`,
    },
    output: {
      zh: "6765\n0.25 3 -3\n12.566370614359172\n⚠ 第 17 行：'計數' expects 整數 but got double",
      en: "6765\n0.25 3 -3\n12.566370614359172\n⚠ Line 17: 'count' expects int but got double",
    },
    expectError: true,
    sandboxNote: {
      zh: "真正的編譯器在執行前就會拒絕最後一行，整個程式不會執行；練習區則在執行到那一行時才回報。",
      en: "The real compiler rejects the last line before anything runs; the sandbox reports it when it reaches that line.",
    },
    quiz: {
      q: { zh: "<code>變數 x: 雙精度 = 5;</code> 會發生什麼？", en: "What happens with <code>var x: double = 5;</code>?" },
      options: [{ zh: "編譯錯誤", en: "A compile error" }, { zh: "x 成為雙精度 5", en: "x becomes the double 5" }, { zh: "x 仍是整數 5", en: "x stays the integer 5" }],
      answer: 1,
      explain: { zh: "整數可以自動加寬成雙精度或浮點數。反過來的方向才需要明確轉換。", en: "Integers widen to double and float automatically. Only narrowing needs an explicit conversion." },
    },
    tryIt: { zh: "把最後一行改成 <code>計數 = 整數(2.5);</code>，錯誤就消失了。", en: "Change the last line to <code>count = int(2.5);</code> and the error goes away." },
  },
  {
    id: "structs",
    chapter: 2,
    title: { zh: "打造星艦：結構", en: "Building starships: structs" },
    tagline: { zh: "結構 / struct：有名字的資料", en: "struct / 結構: named records" },
    body: {
      zh: `<p><strong>結構</strong>把相關的欄位組成一個有名字的型別。呼叫型別就能建立值，可以依序或以名稱指定欄位，以 <code>.</code> 讀寫。</p>
<ul><li>欄位可以有型別與預設值：<code>燃料: 整數 = 100</code></li><li>省略且沒有預設值的欄位是 <code>空值</code></li><li>結構是參考值：指派與傳遞時共用同一份</li><li><code>型別(值)</code> 回傳結構的名稱；結構也可以做為型別標註</li></ul>`,
      en: `<p>A <strong>struct</strong> groups related fields into a named type. Call the type to build a value, positionally or by field name, and use <code>.</code> to read and write.</p>
<ul><li>Fields can have types and defaults: <code>fuel: int = 100</code></li><li>A left-out field without a default is <code>null</code></li><li>Structs are references: assignment and calls share the same one</li><li><code>typeOf(value)</code> gives the struct's name, and struct names work as annotations</li></ul>`,
    },
    code: {
      zh: `結構 星艦 {
    名稱: 字串;
    燃料: 整數 = 100;
    船員 = [];
}

變數 曙光號 = 星艦("曙光號");
變數 天鵝號 = 星艦(名稱: "天鵝號", 燃料: 60);
陣列.追加(曙光號.船員, "林艦長");
曙光號.燃料 -= 30;

印出(曙光號);
印出(天鵝號.名稱, 天鵝號.燃料, 型別(天鵝號));

變數 加油 = 函式(船: 星艦, 數量: 整數) { 船.燃料 += 數量; };
加油(天鵝號, 15);
印出(天鵝號.燃料);`,
      en: `struct Starship {
    name: string;
    fuel: int = 100;
    crew = [];
}

var dawn = Starship("Dawn");
var cygnus = Starship(name: "Cygnus", fuel: 60);
Array.Append(dawn.crew, "Captain Lin");
dawn.fuel -= 30;

print(dawn);
print(cygnus.name, cygnus.fuel, typeOf(cygnus));

var refuel = function(ship: Starship, amount: int) { ship.fuel += amount; };
refuel(cygnus, 15);
print(cygnus.fuel);`,
    },
    output: {
      zh: "星艦{名稱: 曙光號, 燃料: 70, 船員: [林艦長]}\n天鵝號 60 星艦\n75",
      en: "Starship{name: Dawn, fuel: 70, crew: [Captain Lin]}\nCygnus 60 Starship\n75",
    },
    quiz: {
      q: { zh: "為什麼 <code>加油(天鵝號, 15)</code> 之後，天鵝號的燃料真的變了？", en: "Why does <code>refuel(cygnus, 15)</code> really change cygnus's fuel?" },
      options: [
        { zh: "結構是參考值，函式拿到的是同一艘船", en: "Structs are references, so the function gets the same ship" },
        { zh: "函式會自動回傳並覆寫參數", en: "Functions copy their parameters back automatically" },
        { zh: "燃料是全域變數", en: "fuel is a global variable" },
      ],
      answer: 0,
      explain: { zh: "結構、陣列與雜湊表都是參考值，修改會被所有持有者看到。", en: "Structs, arrays and hashes are references; every holder sees a change." },
    },
    tryIt: { zh: "新增一個 <code>速度: 雙精度 = 1.0</code> 欄位並印出。", en: "Add a <code>speed: double = 1.0</code> field and print it." },
  },
  {
    id: "modules",
    chapter: 2,
    title: { zh: "星系之間：模組與專案", en: "Between systems: modules and projects" },
    tagline: { zh: "匯入、Bottle 專案與預設函式庫", en: "import, Bottle projects and default libraries" },
    body: {
      zh: `<p>程式長大後，就拆成多個 <code>.aqua</code> 檔。<code>匯入("路徑.aqua")</code> 取得另一個檔案的變數、函式與結構；<code>匯入("Processing")</code> 這類名稱則載入函式庫。</p>
<p><strong>Bottle</strong> 是星泉的專案與套件工具：<code>aqua new</code> 建立專案，<code>aqua.toml</code> 記錄來源、平台與相依套件，版本會被鎖定並快取。</p>
<p>星泉內建的預設函式庫包括：<code>Processing</code>／<code>WGPU</code>（圖學）、<code>Network</code>（HTTP、WebSocket、TCP／UDP）、<code>Audio</code>（音訊）、<code>Files</code>、<code>Storage</code>、<code>Thread</code>、<code>ONNX</code>（推論）、<code>SaturnUI</code>（介面）等。</p>
<p class="note">此課需要 <code>aqua</code> 命令列工具，所以練習區只顯示程式碼。</p>`,
      en: `<p>As a program grows, split it into several <code>.aqua</code> files. <code>import("path.aqua")</code> gives you another file's variables, functions and structs, and names such as <code>import("Processing")</code> load libraries.</p>
<p><strong>Bottle</strong> is AquariusLang's project and package tool: <code>aqua new</code> creates a project, and <code>aqua.toml</code> records sources, platforms and dependencies, with locked, cached versions.</p>
<p>Default libraries ship with the language, including <code>Processing</code> / <code>WGPU</code> (graphics), <code>Network</code> (HTTP, WebSocket, TCP/UDP), <code>Audio</code>, <code>Files</code>, <code>Storage</code>, <code>Thread</code>, <code>ONNX</code> (inference) and <code>SaturnUI</code> (interfaces).</p>
<p class="note">This lesson needs the <code>aqua</code> command line, so the practice area shows code only.</p>`,
    },
    code: {
      zh: `# 檔案：lib/星圖.aqua
結構 星 { 名稱: 字串; 星等: 雙精度 }
變數 最亮 = 函式(星群) {
    陣列.排序(星群, (a, b) => 如果 (a.星等 < b.星等) { -1; } 否則 { 1; });
    星群[0];
};

# 檔案：main.aqua
變數 星圖 = 匯入("lib/星圖.aqua");
變數 天狼 = 星圖.星(名稱: "天狼星", 星等: -1.46d);
變數 織女 = 星圖.星(名稱: "織女星", 星等: 0.03d);
印出(星圖.最亮([織女, 天狼]).名稱);

# 終端機：
#   aqua new 星圖專案
#   aqua build main.aqua lib/星圖.aqua --root . -o app.wasm
#   aqua run app.wasm`,
      en: `# File: lib/starmap.aqua
struct Star { name: string; magnitude: double }
var brightest = function(stars) {
    Array.Sort(stars, (a, b) => if (a.magnitude < b.magnitude) { -1; } else { 1; });
    stars[0];
};

# File: main.aqua
var starmap = import("lib/starmap.aqua");
var sirius = starmap.Star(name: "Sirius", magnitude: -1.46d);
var vega = starmap.Star(name: "Vega", magnitude: 0.03d);
print(starmap.brightest([vega, sirius]).name);

# Terminal:
#   aqua new StarMap
#   aqua build main.aqua lib/starmap.aqua --root . -o app.wasm
#   aqua run app.wasm`,
    },
    output: { zh: "天狼星", en: "Sirius" },
    runnable: false,
    extra: {
      kind: "toml",
      title: { zh: "aqua.toml 範例", en: "Example aqua.toml" },
      code: `manifest_version = 2

[project]
name = "StarMap"
version = "1.0.0"
entry = "main.aqua"
sources = ["main.aqua", "lib/starmap.aqua"]
platforms = ["windows", "linux", "web", "android"]

# aqua bottle install https://github.com/Aquarius-Language/Aqua_JoltPhysics.git --project .
# writes the dependency into [dependencies] for you
[dependencies]`,
    },
    quiz: {
      q: { zh: "要讓專案使用 GitHub 上的第三方函式庫，應該修改哪個檔案？", en: "Which file declares a third-party library from GitHub?" },
      options: [{ zh: "<code>aqua.toml</code>", en: "<code>aqua.toml</code>" }, { zh: "<code>package.json</code>", en: "<code>package.json</code>" }, { zh: "<code>main.aqua</code> 的第一行", en: "The first line of <code>main.aqua</code>" }],
      answer: 0,
      explain: { zh: "Bottle 從 <code>aqua.toml</code> 的 <code>[dependencies]</code> 讀取相依套件，也可用 <code>aqua bottle install</code> 自動加入。", en: "Bottle reads <code>[dependencies]</code> from <code>aqua.toml</code>; <code>aqua bottle install</code> can add entries for you." },
    },
  },

  /* ───────────── Chapter 4: Into the Galaxy ───────────── */
  {
    id: "async",
    chapter: 3,
    title: { zh: "平行宇宙：非同步與執行緒", en: "Parallel worlds: async and threads" },
    tagline: { zh: "非同步 / async、等待 / await、執行緒 / Thread", en: "async / 非同步, await / 等待, Thread / 執行緒" },
    body: {
      zh: `<p>網路請求或計時這類需要等待的工作，用 <strong>非同步函式</strong> 處理：呼叫會立刻回傳一個 <code>承諾</code>（Promise），<code>等待</code> 會暫停目前的工作直到結果出現，卻不會卡住整個程式。</p>
<ul><li><code>非同步 函式(...) { ... }</code>／<code>async function(...)</code></li><li><code>承諾.全部([...])</code> 同時等待多個工作，結果依原順序排列</li><li>非同步函式中的錯誤會拒絕承諾，可以用 <code>.捕捉()</code> 處理</li></ul>
<p>需要真正的平行運算時，用 <code>執行緒</code>（<code>Thread</code>）：每個工作者拿到函式與引數的獨立副本，桌面、網頁與 Android 都能用。</p>`,
      en: `<p>Work that waits, such as network requests or timers, belongs in an <strong>async function</strong>. A call returns a <code>Promise</code> at once, and <code>await</code> pauses only the current task until the result arrives, without blocking the program.</p>
<ul><li><code>async function(...) { ... }</code> / <code>非同步 函式(...)</code></li><li><code>Promise.All([...])</code> waits for several tasks, keeping their order</li><li>An error inside an async function rejects its promise; handle it with <code>.Catch()</code></li></ul>
<p>For real parallel computation use <code>Thread</code> (<code>執行緒</code>): each worker gets its own copy of the function and arguments, on desktop, the web and Android alike.</p>`,
    },
    code: {
      zh: `變數 掃描 = 非同步 函式(星區, 毫秒) {
    等待 承諾.延遲(毫秒);
    回傳 星區 + "：完成";
};
變數 結果 = 等待 承諾.全部([掃描("北天", 30), 掃描("南天", 10)]);
印出(結果);

變數 總和到 = 函式(n) {
    變數 s = 0;
    迴圈 (變數 i = 1; i <= n; i++) { s += i; }
    回傳 s;
};
變數 工作 = 執行緒.啟動(總和到, [1000]);
印出("背景計算：", 執行緒.結果(工作));
執行緒.關閉(工作);`,
      en: `var scan = async function(sector, ms) {
    await Promise.Delay(ms);
    return sector + ": done";
};
var results = await Promise.All([scan("north", 30), scan("south", 10)]);
print(results);

var sumTo = function(n) {
    var s = 0;
    for (var i = 1; i <= n; i++) { s += i; }
    return s;
};
var job = Thread.Start(sumTo, [1000]);
print("background:", Thread.Result(job));
Thread.Close(job);`,
    },
    output: { zh: "[北天：完成, 南天：完成]\n背景計算： 500500", en: "[north: done, south: done]\nbackground: 500500" },
    sandboxNote: {
      zh: "練習區依序模擬非同步與執行緒，結果與真實執行相同，但不會真的等待。",
      en: "The sandbox simulates async work and threads in order. Results match a real run, but nothing actually waits.",
    },
    quiz: {
      q: { zh: "南天的掃描比較快，為什麼結果仍然是北天在前？", en: "The south scan finishes first. Why is north still first in the result?" },
      options: [
        { zh: "承諾.全部 依照輸入的順序排列結果", en: "Promise.All keeps the input order" },
        { zh: "非同步函式其實是依序執行的", en: "Async functions really run one after another" },
        { zh: "字母順序", en: "Alphabetical order" },
      ],
      answer: 0,
      explain: { zh: "<code>承諾.全部</code> 等待所有工作，結果陣列與輸入陣列一一對應。", en: "<code>Promise.All</code> waits for every task, and its result lines up with its input." },
    },
    tryIt: { zh: "多加一個 <code>掃描(\"銀河\", 5)</code> 到陣列中。", en: "Add <code>scan(\"galactic\", 5)</code> to the array." },
  },
  {
    id: "creative",
    chapter: 3,
    title: { zh: "點亮畫布：創意程式設計", en: "Light up the canvas: creative coding" },
    tagline: { zh: "Processing 風格繪圖，跑在 WebGPU 上", en: "Processing-style drawing on WebGPU" },
    body: {
      zh: `<p>星泉內建 <code>Processing</code> 函式庫，以 wgpu-native 或瀏覽器的 WebGPU 繪圖。寫一個 <code>設定</code> 函式與一個每格執行的 <code>繪製</code> 函式，交給 <code>畫.run()</code> 就有動畫了。</p>
<ul><li>2D／3D 圖形、文字、圖片、像素與著色器</li><li>滑鼠、鍵盤與觸控事件：<code>畫.on("mousePressed", ...)</code></li><li>Perlin noise、亂數種子、變形矩陣</li><li>同一份程式可匯出為網站、Windows／Linux 執行檔或 Android APK</li></ul>
<p class="note">右側預覽以網頁 Canvas 重現這段程式的效果；滑鼠移過去試試看。真正執行需要 <code>aqua</code>。</p>`,
      en: `<p>The built-in <code>Processing</code> library draws with wgpu-native or the browser's WebGPU. Write a <code>setup</code> function and a <code>draw</code> function that runs every frame, hand them to <code>p.run()</code>, and it animates.</p>
<ul><li>2D/3D shapes, text, images, pixels and shaders</li><li>Mouse, keyboard and touch events: <code>p.on("mousePressed", ...)</code></li><li>Perlin noise, seeded randomness and transforms</li><li>One program exports to a website, a Windows/Linux executable or an Android APK</li></ul>
<p class="note">The preview recreates this sketch on a web canvas. Move your pointer over it. Running it for real needs <code>aqua</code>.</p>`,
    },
    code: {
      zh: `變數 畫 = 匯入("Processing");
變數 時間 = 0.0d;

變數 設定 = 函式() {
    畫.size(640, 400, 畫.P2D, "星泉軌道");
    畫.frameRate(60);
};

變數 繪製 = 函式() {
    畫.background(8, 14, 17);
    畫.noStroke();
    迴圈 (變數 i = 0; i < 48; i++) {
        變數 角度 = 時間 + i * 0.26d;
        變數 半徑 = 60 + i * 3.2d;
        變數 x = 畫.mouseX + 畫.cos(角度) * 半徑;
        變數 y = 畫.mouseY + 畫.sin(角度) * 半徑 * 0.6d;
        畫.fill(165, 231, 216, 255 - i * 4);
        畫.circle(x, y, 10 - i * 0.15d);
    }
    時間 += 0.02d;
};

畫.run(設定, 繪製);`,
      en: `var p = import("Processing");
var time = 0.0d;

var setup = function() {
    p.size(640, 400, p.P2D, "Aquarius orbit");
    p.frameRate(60);
};

var draw = function() {
    p.background(8, 14, 17);
    p.noStroke();
    for (var i = 0; i < 48; i++) {
        var angle = time + i * 0.26d;
        var radius = 60 + i * 3.2d;
        var x = p.mouseX + p.cos(angle) * radius;
        var y = p.mouseY + p.sin(angle) * radius * 0.6d;
        p.fill(165, 231, 216, 255 - i * 4);
        p.circle(x, y, 10 - i * 0.15d);
    }
    time += 0.02d;
};

p.run(setup, draw);`,
    },
    runnable: false,
    preview: "orbit",
    quiz: {
      q: { zh: "<code>繪製</code> 函式多久執行一次？", en: "How often does <code>draw</code> run?" },
      options: [{ zh: "只在程式開始時執行一次", en: "Once, at startup" }, { zh: "每一格畫面執行一次", en: "Once every frame" }, { zh: "只有滑鼠移動時", en: "Only when the mouse moves" }],
      answer: 1,
      explain: { zh: "<code>設定</code> 執行一次，<code>繪製</code> 依 <code>frameRate</code> 每格執行一次。", en: "<code>setup</code> runs once; <code>draw</code> runs every frame at the <code>frameRate</code>." },
    },
  },
  {
    id: "ship",
    chapter: 3,
    title: { zh: "揚帆遠航：發佈與生態系", en: "Set sail: shipping and the ecosystem" },
    tagline: { zh: "一份 .wasm，到處執行", en: "One .wasm, everywhere" },
    body: {
      zh: `<p>星泉把程式編譯成標準的 <strong>WebAssembly</strong>：桌面與 Android 由 Wasmtime 執行，瀏覽器直接載入。同一個應用程式可以匯出成網站、單一執行檔或 APK，素材也會一起打包。</p>
<p>準備好更大的冒險了嗎？以下是 Aquarius-Language 組織上公開的函式庫與工具，都能透過 Bottle 加入你的專案：</p>`,
      en: `<p>AquariusLang compiles to standard <strong>WebAssembly</strong>: Wasmtime runs it on desktop and Android, and browsers load it directly. One application exports as a website, a single executable or an APK, with its assets packed in.</p>
<p>Ready for something bigger? These public libraries and tools from the Aquarius-Language organization plug into your project through Bottle:</p>`,
    },
    code: {
      zh: `# 編譯、執行與互動式 REPL
aqua build main.aqua --root . --assets assets -o app.wasm
aqua run app.wasm
aqua repl

# 匯出到各平台
aqua build app.wasm --target web -o dist/web
aqua build app.wasm --target windows -o dist/app.exe
aqua build app.wasm --target linux -o dist/app
aqua build app.wasm --target android -o dist/app.apk

# 在瀏覽器裡開啟星泉筆記本
aqua well`,
      en: `# Compile, run and the interactive REPL
aqua build main.aqua --root . --assets assets -o app.wasm
aqua run app.wasm
aqua repl

# Export to every platform
aqua build app.wasm --target web -o dist/web
aqua build app.wasm --target windows -o dist/app.exe
aqua build app.wasm --target linux -o dist/app
aqua build app.wasm --target android -o dist/app.apk

# Open AquariusLang notebooks in the browser
aqua well`,
    },
    language: "shell",
    runnable: false,
    ecosystem: true,
    quiz: {
      q: { zh: "星泉程式在瀏覽器中是如何執行的？", en: "How does an AquariusLang program run in a browser?" },
      options: [{ zh: "轉譯成 JavaScript", en: "It is translated to JavaScript" }, { zh: "以瀏覽器原生的 WebAssembly 執行", en: "As WebAssembly, natively in the browser" }, { zh: "需要安裝瀏覽器外掛", en: "Through a browser plug-in" }],
      answer: 1,
      explain: { zh: "所有平台執行同一份可攜的 <code>.wasm</code>；網頁版只多了很薄的 Web API 膠合層。", en: "Every platform runs the same portable <code>.wasm</code>; the web adds only a thin layer that exposes Web APIs." },
    },
  },
];

// Public repositories in the Aquarius-Language organization (checked 2026-11-06).
export const publicLibraries = [
  { name: "aqua_wellplay", kind: { zh: "工具", en: "Tool" }, desc: { zh: "JupyterLab 風格的星泉筆記本：`aqua well`", en: "JupyterLab-style AquariusLang notebooks: `aqua well`" } },
  { name: "Aqua_JoltPhysics", kind: { zh: "函式庫", en: "Library" }, desc: { zh: "跨平台 Jolt Physics 3D 物理", en: "Cross-platform Jolt Physics 3D physics" } },
  { name: "Aqua_Filament", kind: { zh: "函式庫", en: "Library" }, desc: { zh: "Filament 即時 PBR 渲染引擎", en: "The Filament real-time PBR renderer" } },
  { name: "Aqua_OpenCV", kind: { zh: "函式庫", en: "Library" }, desc: { zh: "OpenCV 電腦視覺", en: "OpenCV computer vision" } },
  { name: "Aqua_Torch", kind: { zh: "函式庫", en: "Library" }, desc: { zh: "以 Torch 進行機器學習", en: "Machine learning with Torch" } },
  { name: "Aqua_WebSocket", kind: { zh: "函式庫", en: "Library" }, desc: { zh: "WebSocket 用戶端與伺服器", en: "WebSocket clients and servers" } },
  { name: "Aqua_JSON-RPC", kind: { zh: "函式庫", en: "Library" }, desc: { zh: "JSON-RPC 協定實作", en: "A JSON-RPC protocol implementation" } },
  { name: "Aqua_Embedded_LSP_Editor", kind: { zh: "工具", en: "Tool" }, desc: { zh: "遵循 LSP 的星泉嵌入式編輯器", en: "An embeddable LSP-based AquariusLang editor" } },
];

export const publicShowcases = [
  { name: "AquariusLangTW_Snake", label: { zh: "貪吃蛇", en: "Snake" } },
  { name: "AquariusLangTW_MarbleRun_3D", label: { zh: "Marble Run 3D", en: "Marble Run 3D" } },
  { name: "AquariusLangTW_Painter", label: { zh: "星泉塗鴉室", en: "Painter" } },
  { name: "AquariusLangTW_SingleUserKanban", label: { zh: "星泉看板", en: "Kanban board" } },
  { name: "AquariusLangTW_NetworkLab", label: { zh: "網路全端實驗", en: "Full-stack network lab" } },
  { name: "AquariusLangTW_ProcessingShowcase", label: { zh: "Processing 展示", en: "Processing showcase" } },
];

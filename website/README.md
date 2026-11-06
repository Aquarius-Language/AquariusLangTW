# 星泉 AquariusLang 介紹網站

純前端、以繁體中文與英文介紹 AquariusLang 繁體中文／英文雙語程式語言的網站，並附十五堂課的互動教學（`tutorial.html`）。語法可在同一檔案混用。以 Three.js 製作可拖曳旋轉的玻璃水瓶、星泉粒子與軌道，搭配語法示例、現有能力、可點選的生態系星圖與未來展望。

## 本機開發

```sh
npm install
npm run dev
```

開啟終端機顯示的網址，預設為 http://127.0.0.1:5173/。

## 正式建置

```sh
npm run build
npm run preview
```

`dist/` 為可部署的靜態網站。無後端服務、帳號、資料庫或 API 金鑰需求。使用相對資源路徑，可部署至靜態主機的子目錄。

本站原始碼位於語言儲存庫的 `website/` 目錄。`main` 上的網站變更會由根目錄工作流程自動建置並部署至 GitHub Pages；首次啟用與自動化規則請見 [部署指南](docs/github-pages.md)。

## 內容與互動

- 首次進入網站（網址沒有 `?lang=`）會先顯示語言選擇畫面：繁體中文或 English，並標示上次的選擇。選擇後寫入 `?lang=zh|en` 與 localStorage，頁首可隨時切換。
- 頁首「互動教學」按鈕、首頁主視覺按鈕、首頁教學橫幅與語法範例都連到 `tutorial.html`。
- 互動教學分四章十五課（入門、初階、中階、高階）。左側章節地圖可隨時跳到任何一課，課程之間有方向感的轉場動畫，跨章節時顯示章節標題卡；← → 鍵切換課程。
- 每課的範例可切換中文／英文關鍵字、直接編輯並執行。練習區使用 `src/tutorial/aqua.js` 這個簡化的瀏覽器直譯器，只涵蓋教學用到的核心語法；`npm run check:tutorial` 會執行所有範例並比對課文中的預期輸出。
- 教學只提到語言儲存庫內的預設函式庫，以及 Aquarius-Language 組織上的公開儲存庫（`src/tutorial/lessons.js` 的 `publicLibraries`，2026-11-06 查核）。新增函式庫前請先確認其儲存庫為公開。

- 語言內容依據 [AquariusLangTW 官方 README](https://github.com/Aquarius-Language/AquariusLangTW)，查閱日期：2026-10-07。
- 包含命名寓意、中文關鍵字、Unicode 識別字、函式與閉包、陣列與模組，以及現有 OpenGL、Processing、VS Code／LSP 與桌面 VM 能力。
- 未來方向依照本網站需求列出，皆標示為「未來計畫」，未承諾完成時間。
- 三組語法示例可切換、複製；輸出是示例的預期結果，網站不在瀏覽器內執行星泉 VM。
- 3D 場景可拖曳旋轉、暫停動畫、重設視角。場景離開畫面或分頁隱藏時停止繪製。
- 尊重 `prefers-reduced-motion`，預設暫停 3D 動畫並移除捲動動畫。不支援 WebGL 時呈現 SVG 水瓶插畫。
- 手機導覽、鍵盤語法分頁、可見焦點、跳至主要內容與生態系狀態播報。
- Google Fonts 提供 Noto Sans TC、Space Grotesk 與 IBM Plex Mono；未連線時使用系統字型。3D 與內容沒有外部 API 依賴。

## 檔案

- `src/main.js`：首頁中英文內容、語言選擇畫面、語法分頁、複製、生態系與導覽。
- `src/i18n.js`：首頁與教學共用的語言偏好。
- `src/home-extra.css`：語言選擇畫面、語言切換與教學入口的樣式。
- `tutorial.html`、`src/tutorial/`：互動教學（課程內容 `lessons.js`、練習直譯器 `aqua.js`、星空轉場 `starfield.js`）。
- `scripts/check-tutorial.mjs`：驗證教學範例輸出。
- `src/scene.js`：Three.js 水瓶建模、粒子、光影與互動。
- `src/style.css`：版型、視覺、響應式排版與減少動畫偏好。
- `vite.config.js`：相對資源路徑與 3D 程式碼分包。

語言作者：林天牧 / Temple Lin。這個目錄是星泉的介紹網站，與 VM 原始碼一同維護於 AquariusLangTW 儲存庫。

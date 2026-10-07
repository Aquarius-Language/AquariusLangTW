# 星泉 AquariusLang 介紹網站

純前端、繁體中文的程式語言介紹網站。以 Three.js 製作可拖曳旋轉的玻璃水瓶、星泉粒子與軌道，搭配語法示例、現有能力、可點選的生態系星圖與未來展望。

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

- 語言內容依據 [AquariusLangTW 官方 README](https://github.com/Aquarius-Language/AquariusLangTW)，查閱日期：2026-10-07。
- 包含命名寓意、中文關鍵字、Unicode 識別字、函式與閉包、陣列與模組，以及現有 OpenGL、Processing、VS Code／LSP 與桌面直譯器能力。
- 未來方向依照本網站需求列出，皆標示為「未來計畫」，未承諾完成時間。
- 三組語法示例可切換、複製；輸出是示例的預期結果，網站不在瀏覽器內執行星泉直譯器。
- 3D 場景可拖曳旋轉、暫停動畫、重設視角。場景離開畫面或分頁隱藏時停止繪製。
- 尊重 `prefers-reduced-motion`，預設暫停 3D 動畫並移除捲動動畫。不支援 WebGL 時呈現 SVG 水瓶插畫。
- 手機導覽、鍵盤語法分頁、可見焦點、跳至主要內容與生態系狀態播報。
- Google Fonts 提供 Noto Sans TC、Space Grotesk 與 IBM Plex Mono；未連線時使用系統字型。3D 與內容沒有外部 API 依賴。

## 檔案

- `src/main.js`：頁面內容、語法分頁、複製、生態系與導覽。
- `src/scene.js`：Three.js 水瓶建模、粒子、光影與互動。
- `src/style.css`：版型、視覺、響應式排版與減少動畫偏好。
- `vite.config.js`：相對資源路徑與 3D 程式碼分包。

語言作者：林天牧 / Temple Lin。這個目錄是星泉的介紹網站，與直譯器原始碼一同維護於 AquariusLangTW 儲存庫。

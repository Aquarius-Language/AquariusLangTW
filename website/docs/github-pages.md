# 星泉介紹網站：GitHub Pages 部署

網站原始碼已整合到 `Aquarius-Language/AquariusLangTW` 的 `website/` 目錄。工作流程位於根目錄的 `.github/workflows/deploy-website.yml`，直接建置同一儲存庫的程式碼。

預定公開網址：

https://aquarius-language.github.io/AquariusLangTW/

## 首次啟用

1. 前往 [AquariusLangTW → Settings → Pages](https://github.com/Aquarius-Language/AquariusLangTW/settings/pages)，將 **Build and deployment → Source** 設為 **GitHub Actions**。
2. 將 `website/`、`.github/workflows/deploy-website.yml` 與相關文件提交並推送或合併至 `main`。
3. 在 **Actions → Deploy AquariusLang website** 查看建置與部署結果。初次推送上述檔案會自動觸發部署，也可選擇 **Run workflow → main** 手動執行。
4. 部署成功後，將公開網址加入語言儲存庫的 **About → Website**。

## 自動化行為

- `main` 上的 `website/**` 或部署工作流程變更：自動建置並部署。
- 指向 `main` 的 pull request 修改上述路徑：僅建置驗證，不發佈網站。
- 其他語言原始碼變更：不會單獨觸發網站部署。
- 手動執行：在 `main` 上建置並部署。
- 建置使用 Node.js 22、`npm ci` 與 `npm run build -- --base=/AquariusLangTW/`。
- 正式產物位於 `website/dist/`。Vite 本機設定保持相對資源路徑，CI 則指定 GitHub Pages 的專案路徑。
- npm 快取使用 `website/package-lock.json`；部署工作使用 GitHub 自動提供的 token，不需要個人存取 token。
- 部署版本與觸發工作流程的 commit 一致；網站與語言可以在同一個 pull request 中一起修改。
- 相同分支的舊 pull request 建置可取消；正式部署依序執行，避免中途取消正在發佈的網站。

## 本機操作

從語言儲存庫根目錄執行：

```sh
cd website
npm ci
npm run dev
```

一般本機建置與預覽：

```sh
npm run build
npm run preview
```

檢查 GitHub Pages 專案路徑：

```sh
npm run build -- --base=/AquariusLangTW/
npm run preview -- --base=/AquariusLangTW/ --port 4174
```

開啟 http://127.0.0.1:4174/AquariusLangTW/。回到一般預覽時，重新執行 `npm run build` 即可恢復相對資源路徑。

## 原獨立網站儲存庫

介紹網站先前位於 `Aquarius-Language/AquariusLangShowPage`。本次整合保留原本的工作目錄作為備份；後續網站修改應以此儲存庫的 `website/` 為準。新工作流程不再讀取獨立網站儲存庫。

## 官方文件

- [GitHub Pages 自訂工作流程](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages)
- [Vite 的 GitHub Pages 部署](https://vite.dev/guide/static-deploy.html#github-pages)

# webframe

`WebFrame` / `網頁框架` embeds other web pages (iframes) in a web application's page and places them in the
application's own coordinates over its drawing: a live preview beside an editor (AquaLab), documentation, another
application's page. Desktop and mobile windows have no page to embed into: `Supported()` is false there and `Create`
fails with `Unsupported`, so applications check it and fall back (open a window, or the system browser).

| Function | 繁體中文 | |
| --- | --- | --- |
| `Supported()` | `支援()` | Whether this target can embed pages (the web). |
| `Create(url)` | `建立(網址)` | A frame showing the page (hidden until placed). |
| `frame.Place(x, y, width, height)` | `放置` | Position and size in page units from the top left of the application's canvas. |
| `frame.Navigate(url)`, `frame.Reload()` | `前往`, `重新載入` | Show another page, or the same one again (hot reload). |
| `frame.Show(visible)`, `frame.Close()` | `顯示`, `關閉` | Hide or show; remove it (closing twice is fine). |

`javascript:` URLs are refused. The page's own security rules apply: a frame shows another origin only when that origin
allows framing, and its content is isolated from the application (talk to it through a server or the network library).

How it is built: `src/webframe.c` holds the objects and checks; `src/platform/web.c` calls the `frame` glue domain
(`web-glue/frame`), which creates and moves `<iframe>` elements; `src/platform/none.c` serves every target without a
page, so a new target needs nothing here. Test: `tests/frame.aqua` (every target).

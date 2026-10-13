import {createServer as 建立伺服器} from 'node:http';
import {readFile as 讀取檔案} from 'node:fs/promises';
import 路徑 from 'node:path';
import {fileURLToPath as 檔案網址轉路徑} from 'node:url';
const 根目錄 = 路徑.join(路徑.dirname(檔案網址轉路徑(import.meta.url)), 'web');
const 伺服器 = 建立伺服器(async (請求, 回應) => {
  try {
    const 網址 = new URL(請求.url, 'http://localhost');
    const 名稱 = decodeURIComponent(網址.pathname === '/' ? '/index.html' : 網址.pathname);
    const 檔案 = 路徑.resolve(根目錄, '.' + 名稱);
    if (!檔案.startsWith(根目錄 + 路徑.sep)) throw new Error('路徑無效');
    回應.setHeader('Content-Type', ({'.html':'text/html; charset=utf-8','.mjs':'text/javascript','.json':'application/json','.wasm':'application/wasm'})[路徑.extname(檔案)] ?? 'application/octet-stream');
    回應.end(await 讀取檔案(檔案));
  } catch { 回應.statusCode = 404; 回應.end('找不到檔案'); }
});
伺服器.listen(8787, '127.0.0.1', () => console.log('星泉姿態觀測站：http://127.0.0.1:8787'));

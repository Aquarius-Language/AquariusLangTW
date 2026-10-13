import {ApplicationRuntime} from './application-runtime.mjs';
import {BrowserStorage,BrowserFiles} from './application-files.mjs';
import {BrowserImages} from './application-images.mjs';
import {BrowserClipboard,BrowserServices,BrowserFonts} from './application-services.mjs';
import {BrowserWindow} from './application-window.mjs';
export {toValue,fromValue} from './application-runtime.mjs';
export class BrowserApplication extends ApplicationRuntime {constructor(host){const storage=new BrowserStorage(host.bundle.entry??'application'),images=new BrowserImages(),files=new BrowserFiles(storage,(name,blob)=>host.download(name,blob));super(host,{storage,images,files,clipboard:new BrowserClipboard(images),services:new BrowserServices(files,images),fonts:new BrowserFonts(),window:(canvas,files)=>new BrowserWindow(canvas,files)});}}

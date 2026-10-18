import {resizeSurfaceSize} from './surface-size.mjs';
// GLFW key numbering for DOM KeyboardEvent.code, so programs use one set of key codes on every host.
const NAMED={Space:32,Quote:39,Comma:44,Minus:45,Period:46,Slash:47,Semicolon:59,Equal:61,BracketLeft:91,Backslash:92,BracketRight:93,Backquote:96,
  Escape:256,Enter:257,Tab:258,Backspace:259,Insert:260,Delete:261,ArrowRight:262,ArrowLeft:263,ArrowDown:264,ArrowUp:265,PageUp:266,PageDown:267,Home:268,End:269,
  CapsLock:280,ScrollLock:281,NumLock:282,PrintScreen:283,Pause:284,NumpadDecimal:330,NumpadDivide:331,NumpadMultiply:332,NumpadSubtract:333,NumpadAdd:334,NumpadEnter:335,NumpadEqual:336,
  ShiftLeft:340,ControlLeft:341,AltLeft:342,MetaLeft:343,ShiftRight:344,ControlRight:345,AltRight:346,MetaRight:347,ContextMenu:348};
export function glfwKey(code){
  if(code in NAMED)return NAMED[code];
  let m=/^Key([A-Z])$/.exec(code);if(m)return m[1].charCodeAt(0);
  m=/^Digit([0-9])$/.exec(code);if(m)return 48+Number(m[1]);
  m=/^Numpad([0-9])$/.exec(code);if(m)return 320+Number(m[1]);
  m=/^F([1-9]|1[0-9]|2[0-5])$/.exec(code);if(m)return 289+Number(m[1]);
  return -1;
}
// DOM buttons 0 left, 1 middle, 2 right; GLFW uses 0 left, 1 right, 2 middle.
const button=b=>b===1?2:b===2?1:b;
/** Browser counterpart of the desktop GLFW WGPU window: a full-viewport canvas with polled input.
 * Pointer lock needs a user gesture, so a pending request is retried inside the next key/mouse handler. */
export class BrowserWgpuWindow {
  constructor(container,width,height,title){
    this.canvas=document.createElement('canvas');this.canvas.tabIndex=0;this.canvas.style.touchAction='none';this.canvas.setAttribute('aria-label',title);
    container.append(this.canvas);document.title=title;this.disposed=false;this.device=null;
    this.keys=new Set();this.pendingPressed=new Set();this.pendingReleased=new Set();this.pressed=new Set();this.released=new Set();
    this.buttons=new Set();this.pendingButtonPressed=new Set();this.pendingButtonReleased=new Set();this.buttonPressed=new Set();this.buttonReleased=new Set();
    this.dx=0;this.dy=0;this.wheel=0;this.x=0;this.y=0;this.focused=document.hasFocus();this.locked=false;this.wantLock=false;this.closeRequested=false;
    this.size={width,height,pixelWidth:width,pixelHeight:height};this.applySize(this.readSize());
    this.state={width:this.size.width,height:this.size.height,pixelWidth:this.size.pixelWidth,pixelHeight:this.size.pixelHeight,mouseX:0,mouseY:0,mouseDeltaX:0,mouseDeltaY:0,wheel:0,focused:this.focused,pointerLocked:false,shouldClose:false,resized:false};
    const abort=this.abort=new AbortController(),signal=abort.signal,c=this.canvas;
    const gesture=()=>{if(this.wantLock&&!this.locked)this.requestLock();};
    window.addEventListener('keydown',e=>{const k=glfwKey(e.code);if(k<0)return;if(!this.keys.has(k))this.pendingPressed.add(k);this.keys.add(k);
      // Keep game keys from scrolling or moving focus; leave browser/OS shortcuts with modifiers alone.
      if(!e.ctrlKey&&!e.metaKey&&!e.altKey&&(k<290||k>314))e.preventDefault();gesture();},{signal});
    window.addEventListener('keyup',e=>{const k=glfwKey(e.code);if(k<0)return;this.keys.delete(k);this.pendingReleased.add(k);},{signal});
    // Clicks also refresh the cursor position: a press can arrive without any preceding mousemove (touch, focus clicks, automation).
    const track=e=>{const r=c.getBoundingClientRect();this.x=e.clientX-r.left;this.y=e.clientY-r.top;};
    c.addEventListener('mousedown',e=>{track(e);const b=button(e.button);this.buttons.add(b);this.pendingButtonPressed.add(b);c.focus();e.preventDefault();gesture();},{signal});
    window.addEventListener('mouseup',e=>{const b=button(e.button);if(this.buttons.delete(b))this.pendingButtonReleased.add(b);},{signal});
    window.addEventListener('mousemove',e=>{this.dx+=e.movementX||0;this.dy+=e.movementY||0;track(e);},{signal});
    c.addEventListener('wheel',e=>{this.wheel+=-e.deltaY/(e.deltaMode===0?100:e.deltaMode===1?3:1);e.preventDefault();},{signal,passive:false});
    c.addEventListener('contextmenu',e=>e.preventDefault(),{signal});
    const blur=()=>{this.focused=false;for(const k of this.keys)this.pendingReleased.add(k);this.keys.clear();for(const b of this.buttons)this.pendingButtonReleased.add(b);this.buttons.clear();};
    window.addEventListener('blur',blur,{signal});window.addEventListener('focus',()=>{this.focused=true;},{signal});
    document.addEventListener('visibilitychange',()=>{if(document.hidden)blur();},{signal});
    document.addEventListener('pointerlockchange',()=>{this.locked=document.pointerLockElement===c;if(!this.locked)this.wantLock=false;},{signal});
    document.addEventListener('pointerlockerror',()=>{this.locked=false;},{signal});
    c.focus();
  }
  readSize(){const b=this.canvas.getBoundingClientRect(),density=window.devicePixelRatio||1;return resizeSurfaceSize(this.size,Math.round(b.width),Math.round(b.height),Math.round(b.width*density),Math.round(b.height*density));}
  applySize(size){if(this.canvas.width!==size.pixelWidth)this.canvas.width=Math.max(1,size.pixelWidth);if(this.canvas.height!==size.pixelHeight)this.canvas.height=Math.max(1,size.pixelHeight);this.size=size;}
  live(){if(this.disposed)throw new Error('WGPU window has been disposed');}
  requestLock(){try{const r=this.canvas.requestPointerLock({unadjustedMovement:true});r?.catch?.(()=>{try{this.canvas.requestPointerLock()?.catch?.(()=>{});}catch{}});}catch{try{this.canvas.requestPointerLock();}catch{}}}
  /** Waits for the next animation frame, then publishes input gathered since the previous poll. */
  async poll(){
    // Hidden pages get no animation frames; keep a slow tick so programs can still pause, save and stream.
    this.live();await new Promise(r=>{if(document.hidden){setTimeout(r,100);return;}let done=false;const finish=()=>{if(!done){done=true;r();}};requestAnimationFrame(finish);setTimeout(()=>{if(document.hidden)finish();},250);});if(this.disposed)return;
    this.pressed=this.pendingPressed;this.pendingPressed=new Set();this.released=this.pendingReleased;this.pendingReleased=new Set();
    this.buttonPressed=this.pendingButtonPressed;this.pendingButtonPressed=new Set();this.buttonReleased=this.pendingButtonReleased;this.pendingButtonReleased=new Set();
    const size=this.readSize(),s=this.state,resized=size.width!==s.width||size.height!==s.height||size.pixelWidth!==s.pixelWidth||size.pixelHeight!==s.pixelHeight;this.applySize(size);
    this.state={width:size.width,height:size.height,pixelWidth:size.pixelWidth,pixelHeight:size.pixelHeight,mouseX:this.x,mouseY:this.y,mouseDeltaX:this.dx,mouseDeltaY:this.dy,wheel:this.wheel,focused:this.focused&&!document.hidden,pointerLocked:this.locked,shouldClose:this.closeRequested,resized};
    this.dx=this.dy=this.wheel=0;
  }
  keyDown(k){return this.keys.has(k);}keyPressed(k){return this.pressed.has(k);}keyReleased(k){return this.released.has(k);}
  mouseDown(b){return this.buttons.has(b);}mousePressed(b){return this.buttonPressed.has(b);}mouseReleased(b){return this.buttonReleased.has(b);}
  setPointerLock(value){this.live();if(value){this.wantLock=true;if(!this.locked)this.requestLock();}else{this.wantLock=false;if(document.pointerLockElement===this.canvas)document.exitPointerLock();this.locked=false;}this.state={...this.state,pointerLocked:this.locked,mouseDeltaX:0,mouseDeltaY:0};this.dx=this.dy=0;}
  setTitle(title){this.live();document.title=String(title);this.canvas.setAttribute('aria-label',String(title));}
  close(){this.live();this.closeRequested=true;this.state={...this.state,shouldClose:true};}
  dispose(){if(this.disposed)return;this.device?.dispose();this.device=null;this.abort.abort();if(document.pointerLockElement===this.canvas)document.exitPointerLock();this.canvas.remove();this.disposed=true;}
}

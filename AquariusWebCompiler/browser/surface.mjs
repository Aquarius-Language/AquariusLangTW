export {resizeSurfaceSize} from './surface-size.mjs';
import {resizeSurfaceSize} from './surface-size.mjs';
export class BrowserSurface {
  constructor(container,width,height,title) {
    this.canvas=document.createElement('canvas');this.canvas.tabIndex=0;this.canvas.style.touchAction='none';
    this.canvas.setAttribute('aria-label',title);
    this.size={width,height,pixelWidth:width,pixelHeight:height};
    container.append(this.canvas);this.apply(this.readSize());
    this.canvas.focus();
  }
  readSize() {
    // Sample once per frame, just like GLFW. This also catches zoom/display DPR
    // changes without changing the backing store halfway through a draw callback.
    const bounds=this.canvas.getBoundingClientRect(),density=window.devicePixelRatio||1;
    return resizeSurfaceSize(this.size,Math.round(bounds.width),Math.round(bounds.height),
      Math.round(bounds.width*density),Math.round(bounds.height*density));
  }
  apply(size) {
    if(this.canvas.width!==size.pixelWidth)this.canvas.width=size.pixelWidth;
    if(this.canvas.height!==size.pixelHeight)this.canvas.height=size.pixelHeight;
    this.size=size;
  }
  requestSize(width,height) {
    if(!Number.isInteger(width)||!Number.isInteger(height)||width<1||height<1||width>8192||height>8192)throw new Error('Dimension must be 1..8192');
    // The browser owns its viewport. A script cannot resize the browser window.
    // Its main surface continues to use the actual viewport size.
  }
  dispose(){this.canvas.remove();}
}

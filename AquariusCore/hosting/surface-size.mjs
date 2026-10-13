// Browser counterpart of AquariusLang.Graphics.GraphicsSurfaceSize. DOM/window
// details stay in the host; layout and GPU attachments consume only this snapshot.
export function resizeSurfaceSize(previous,width,height,pixelWidth,pixelHeight) {
  if([width,height,pixelWidth,pixelHeight].some(n=>!Number.isInteger(n)||n<0))throw new Error('Host surface sizes must be nonnegative integers');
  if(width===0||height===0){width=previous.width;height=previous.height;}
  if(pixelWidth===0||pixelHeight===0)pixelWidth=pixelHeight=0;
  return {width,height,pixelWidth,pixelHeight};
}


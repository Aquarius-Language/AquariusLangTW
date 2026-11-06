// Canvas recreation of the creative-coding lesson's Processing sketch:
// 48 dots orbiting the pointer, fading outward.
export function createOrbitPreview(canvas, { reduceMotion = false } = {}) {
  const ctx = canvas.getContext("2d");
  const W = 640, H = 400;
  let mx = W / 2, my = H / 2, tx = mx, ty = my, time = 0, raf = 0, running = true;
  const toLocal = (e) => {
    const r = canvas.getBoundingClientRect();
    tx = ((e.clientX - r.left) / r.width) * W;
    ty = ((e.clientY - r.top) / r.height) * H;
  };
  canvas.addEventListener("pointermove", toLocal);
  canvas.addEventListener("pointerleave", () => { tx = W / 2; ty = H / 2; });
  function draw() {
    mx += (tx - mx) * 0.12;
    my += (ty - my) * 0.12;
    ctx.fillStyle = "rgb(8, 14, 17)";
    ctx.fillRect(0, 0, W, H);
    for (let i = 0; i < 48; i++) {
      const angle = time + i * 0.26;
      const radius = 60 + i * 3.2;
      const x = mx + Math.cos(angle) * radius;
      const y = my + Math.sin(angle) * radius * 0.6;
      ctx.fillStyle = `rgba(165, 231, 216, ${(255 - i * 4) / 255})`;
      ctx.beginPath();
      ctx.arc(x, y, (10 - i * 0.15) / 2, 0, Math.PI * 2);
      ctx.fill();
    }
    time += 0.02;
  }
  function frame() {
    if (!running) return;
    draw();
    raf = requestAnimationFrame(frame);
  }
  if (reduceMotion) draw();
  else frame();
  return {
    stop() {
      running = false;
      cancelAnimationFrame(raf);
    },
  };
}

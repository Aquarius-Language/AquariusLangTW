// Background starfield. warp() streaks the stars sideways during lesson changes,
// pulse() brightens them briefly (runs and correct answers).
export function createStarfield(canvas, { reduceMotion = false } = {}) {
  const ctx = canvas.getContext("2d");
  let w = 0, h = 0, dpr = 1;
  const stars = [];
  let velocity = 0, target = 0, glow = 0, hue = 168, hueTarget = 168;
  let raf = 0, last = performance.now();

  function resize() {
    dpr = Math.min(window.devicePixelRatio || 1, 2);
    w = canvas.clientWidth;
    h = canvas.clientHeight;
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const want = Math.round((w * h) / 5200);
    while (stars.length < want) stars.push(makeStar(true));
    stars.length = want;
    if (reduceMotion) draw(0);
  }
  function makeStar(anywhere) {
    return {
      x: anywhere ? Math.random() * (w || 1) : Math.random() < 0.5 ? -10 : (w || 1) + 10,
      y: Math.random() * (h || 1),
      z: 0.2 + Math.random() * 0.8,
      tw: Math.random() * Math.PI * 2,
    };
  }
  function draw(dt) {
    ctx.clearRect(0, 0, w, h);
    velocity += (target - velocity) * Math.min(1, dt * 6);
    target *= Math.pow(0.02, dt);
    glow *= Math.pow(0.08, dt);
    hue += (hueTarget - hue) * Math.min(1, dt * 2);
    for (const s of stars) {
      s.tw += dt * (0.6 + s.z);
      const drift = (6 + velocity * 900) * s.z;
      s.x -= drift * dt;
      s.y += Math.sin(s.tw * 0.2) * 0.02;
      if (s.x < -40) { s.x = w + Math.random() * 40; s.y = Math.random() * h; }
      if (s.x > w + 40) { s.x = -Math.random() * 40; s.y = Math.random() * h; }
      const a = (0.25 + 0.55 * s.z) * (0.75 + 0.25 * Math.sin(s.tw)) + glow * 0.4;
      const len = Math.abs(velocity) * 140 * s.z;
      ctx.strokeStyle = `hsla(${hue}, 70%, ${78 + s.z * 15}%, ${Math.min(1, a)})`;
      ctx.lineWidth = 0.6 + s.z * 1.1;
      ctx.lineCap = "round";
      ctx.beginPath();
      ctx.moveTo(s.x, s.y);
      ctx.lineTo(s.x + Math.sign(velocity || 1) * Math.max(len, 0.01), s.y);
      ctx.stroke();
    }
  }
  function frame(now) {
    const dt = Math.min(0.05, (now - last) / 1000);
    last = now;
    draw(dt);
    raf = requestAnimationFrame(frame);
  }
  window.addEventListener("resize", resize);
  resize();
  if (!reduceMotion) raf = requestAnimationFrame(frame);
  document.addEventListener("visibilitychange", () => {
    if (reduceMotion) return;
    cancelAnimationFrame(raf);
    if (!document.hidden) { last = performance.now(); raf = requestAnimationFrame(frame); }
  });
  return {
    warp(direction = 1, newHue = null) {
      if (reduceMotion) return;
      target = direction * 1.3;
      if (newHue !== null) hueTarget = newHue;
    },
    pulse(strength = 1) {
      glow = Math.min(2, glow + strength);
    },
  };
}

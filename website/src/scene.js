import * as THREE from "three";
import { RoomEnvironment } from "three/addons/environments/RoomEnvironment.js";
import { EffectComposer } from "three/addons/postprocessing/EffectComposer.js";
import { RenderPass } from "three/addons/postprocessing/RenderPass.js";
import { UnrealBloomPass } from "three/addons/postprocessing/UnrealBloomPass.js";
import { OutputPass } from "three/addons/postprocessing/OutputPass.js";

export function createObservatory(canvas) {
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
  let paused = reducedMotion.matches;
  let renderer;
  const motionButton = document.querySelector("#motion-toggle");
  const resetButton = document.querySelector("#scene-reset");
  function updateMotionButton() {
    motionButton.setAttribute("aria-pressed", String(paused));
    motionButton.setAttribute(
      "aria-label",
      paused ? "繼續星空動畫" : "暫停星空動畫",
    );
    motionButton.innerHTML = paused
      ? '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="m9 6 9 6-9 6V6Z" stroke="currentColor" stroke-width="1.5"/></svg>'
      : '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M9 7v10m6-10v10" stroke="currentColor" stroke-width="1.5"/></svg>';
  }
  updateMotionButton();
  try {
    renderer = new THREE.WebGLRenderer({
      canvas,
      antialias: false,
      alpha: true,
      powerPreference: "low-power",
    });
  } catch {
    canvas.hidden = true;
    const fallback = document.createElement("div");
    fallback.className = "scene-fallback";
    fallback.innerHTML =
      '<svg viewBox="0 0 500 600" fill="none" aria-label="水瓶星座插畫"><defs><linearGradient id="vase-glow" x1="180" y1="120" x2="330" y2="490" gradientUnits="userSpaceOnUse"><stop stop-color="#aee7d8"/><stop offset=".5" stop-color="#355c61"/><stop offset="1" stop-color="#ac94c5"/></linearGradient></defs><g stroke="url(#vase-glow)"><ellipse cx="250" cy="480" rx="150" ry="35" opacity=".5"/><path d="M220 140h60v45c0 35 65 50 65 150s-35 120-95 120-95-20-95-120 65-115 65-150v-45Z" stroke-width="2"/><path d="M280 185c120-40 115 130 55 120" stroke-width="2"/><ellipse cx="250" cy="140" rx="30" ry="9"/>' +
      Array.from(
        { length: 12 },
        (_, i) =>
          `<path d="M${224 + i * 4.8} 145C${180 + i * 12} 280 ${165 + i * 15} 380 250 455" opacity=".6"/>`,
      ).join("") +
      '</g><g fill="#bfe9df">' +
      Array.from(
        { length: 80 },
        (_, i) =>
          `<circle cx="${60 + ((i * 83) % 390)}" cy="${40 + ((i * 61) % 480)}" r="${i % 5 === 0 ? 1.6 : 0.6}"/>`,
      ).join("") +
      "</g></svg>";
    canvas.after(fallback);
    canvas.parentElement.classList.add("webgl-unavailable");
    document.querySelector(".drag-hint").textContent = "水瓶座 · 星泉的起點";
    motionButton.hidden = true;
    resetButton.hidden = true;
    return;
  }

  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 1.65));
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.05;
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x080e11);
  const camera = new THREE.PerspectiveCamera(38, 1, 0.1, 80);
  camera.position.set(0, 0.45, 8.9);
  camera.lookAt(0, 0.15, 0);
  const pmrem = new THREE.PMREMGenerator(renderer);
  const room = new RoomEnvironment();
  const environment = pmrem.fromScene(room, 0.04);
  scene.environment = environment.texture;
  room.dispose();
  pmrem.dispose();
  const composer = new EffectComposer(renderer);
  composer.addPass(new RenderPass(scene, camera));
  const bloom = new UnrealBloomPass(new THREE.Vector2(1, 1), 0.3, 0.5, 0.9);
  composer.addPass(bloom);
  composer.addPass(new OutputPass());

  const ambient = new THREE.HemisphereLight(0xb6ffeb, 0x44466c, 1.5);
  scene.add(ambient);
  const key = new THREE.DirectionalLight(0xbdf7e9, 4);
  key.position.set(-3, 3, 4);
  scene.add(key);
  const edge = new THREE.DirectionalLight(0x80cdd1, 3);
  edge.position.set(3, 2, -3);
  scene.add(edge);
  const lavender = new THREE.PointLight(0xb693e8, 24, 10, 2);
  lavender.position.set(0.5, -2, 2);
  scene.add(lavender);
  const glow = new THREE.PointLight(0x81f6da, 12, 9, 2);
  glow.position.set(-2, 0.3, 1);
  scene.add(glow);

  const vessel = new THREE.Group();
  vessel.position.set(0.4, 0.13, 0);
  vessel.rotation.set(0.07, -0.45, -0.17);
  scene.add(vessel);
  const profile = [
    [0.14, -1.65],
    [0.39, -1.61],
    [0.56, -1.56],
    [0.54, -1.49],
    [0.39, -1.44],
    [0.4, -1.31],
    [0.64, -1.14],
    [0.87, -0.89],
    [1.02, -0.52],
    [1.08, -0.14],
    [1.04, 0.23],
    [0.92, 0.59],
    [0.71, 0.9],
    [0.45, 1.13],
    [0.32, 1.31],
    [0.31, 1.53],
    [0.34, 1.72],
    [0.46, 1.81],
    [0.48, 1.89],
    [0.43, 1.93],
  ].map(([x, y]) => new THREE.Vector2(x, y));
  const profileCurve = new THREE.SplineCurve(profile);
  const smoothProfile = profileCurve.getPoints(150);
  const geometry = new THREE.LatheGeometry(smoothProfile, 160);
  const position = geometry.attributes.position;
  for (let i = 0; i < position.count; i++) {
    const x = position.getX(i),
      z = position.getZ(i),
      y = position.getY(i);
    const theta = Math.atan2(z, x);
    const influence =
      THREE.MathUtils.smoothstep(y, -1.5, -1.1) *
      (1 - THREE.MathUtils.smoothstep(y, 1.25, 1.8));
    const rib = 1 + Math.sin(theta * 40) * 0.012 * influence;
    position.setXYZ(i, x * rib, y, z * rib);
  }
  geometry.computeVertexNormals();
  const glass = new THREE.MeshPhysicalMaterial({
    color: 0x67958b,
    metalness: 0.18,
    roughness: 0.16,
    transmission: 0.83,
    thickness: 0.36,
    ior: 1.46,
    clearcoat: 1,
    clearcoatRoughness: 0.06,
    side: THREE.DoubleSide,
    envMapIntensity: 1.65,
    transparent: true,
    opacity: 0.9,
  });
  vessel.add(new THREE.Mesh(geometry, glass));
  const fineMaterial = new THREE.LineBasicMaterial({
    color: 0x91cfbc,
    transparent: true,
    opacity: 0.2,
    blending: THREE.AdditiveBlending,
  });
  for (let i = 0; i < 40; i++) {
    const angle = (i / 40) * Math.PI * 2;
    const points = smoothProfile.map(
      (point) =>
        new THREE.Vector3(
          Math.sin(angle) * point.x * 1.015,
          point.y,
          Math.cos(angle) * point.x * 1.015,
        ),
    );
    vessel.add(
      new THREE.Line(
        new THREE.BufferGeometry().setFromPoints(points),
        fineMaterial,
      ),
    );
  }
  const rimMaterial = new THREE.MeshPhysicalMaterial({
    color: 0xa0d6c2,
    metalness: 0.4,
    roughness: 0.18,
    transparent: true,
    opacity: 0.72,
    emissive: 0x24453a,
    envMapIntensity: 2.3,
  });
  const rim = new THREE.Mesh(
    new THREE.TorusGeometry(0.445, 0.023, 12, 96),
    rimMaterial,
  );
  rim.rotation.x = Math.PI / 2;
  rim.position.y = 1.91;
  vessel.add(rim);
  const bottom = new THREE.Mesh(
    new THREE.TorusGeometry(0.5, 0.035, 12, 80),
    rimMaterial,
  );
  bottom.rotation.x = Math.PI / 2;
  bottom.position.y = -1.55;
  vessel.add(bottom);
  const handleCurve = new THREE.CatmullRomCurve3([
    new THREE.Vector3(0.32, 1.45, 0),
    new THREE.Vector3(0.82, 1.54, 0),
    new THREE.Vector3(1.3, 1.26, 0),
    new THREE.Vector3(1.45, 0.82, 0),
    new THREE.Vector3(1.32, 0.37, 0),
    new THREE.Vector3(1.0, 0.19, 0),
  ]);
  vessel.add(
    new THREE.Mesh(
      new THREE.TubeGeometry(handleCurve, 80, 0.063, 12, false),
      glass,
    ),
  );
  vessel.add(
    new THREE.Line(
      new THREE.BufferGeometry().setFromPoints(handleCurve.getPoints(90)),
      new THREE.LineBasicMaterial({
        color: 0xa4e6d1,
        transparent: true,
        opacity: 0.38,
      }),
    ),
  );

  let randomState = 947;
  const random = () => {
    randomState = (randomState * 1664525 + 1013904223) >>> 0;
    return randomState / 4294967296;
  };
  function pointsCloud(
    count,
    getPosition,
    color,
    size,
    opacity,
    animated = false,
  ) {
    const positions = new Float32Array(count * 3),
      seeds = new Float32Array(count);
    for (let i = 0; i < count; i++) {
      const pos = getPosition(i);
      positions.set(pos, i * 3);
      seeds[i] = random();
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.BufferAttribute(positions, 3));
    geo.setAttribute("seed", new THREE.BufferAttribute(seeds, 1));
    const material = new THREE.ShaderMaterial({
      uniforms: {
        color: { value: new THREE.Color(color) },
        pointSize: { value: size * renderer.getPixelRatio() },
        alpha: { value: opacity },
        time: { value: 0 },
        animated: { value: animated ? 1 : 0 },
      },
      vertexShader: `attribute float seed; uniform float pointSize; uniform float time; uniform float animated; varying float vAlpha; void main(){vec3 p=position; p.x+=sin(time*.35+seed*40.)*.035*animated; p.y+=cos(time*.4+seed*50.)*.04*animated; vec4 mv=modelViewMatrix*vec4(p,1.);gl_Position=projectionMatrix*mv;gl_PointSize=clamp(pointSize*(7./-mv.z)*(0.5+seed),1.,7.);vAlpha=.45+.55*sin(seed*150.+time*.55)*sin(seed*150.+time*.55);}`,
      fragmentShader: `uniform vec3 color;uniform float alpha;varying float vAlpha;void main(){float d=length(gl_PointCoord-.5);float a=smoothstep(.5,.06,d);gl_FragColor=vec4(color,a*alpha*vAlpha);}`,
      transparent: true,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    });
    const cloud = new THREE.Points(geo, material);
    scene.add(cloud);
    return cloud;
  }
  const stars = pointsCloud(
    1150,
    () => [(random() - 0.5) * 14, (random() - 0.5) * 10, -2 - random() * 8],
    0xb6d9d0,
    1.65,
    0.6,
  );
  const dust = pointsCloud(
    2000,
    () => {
      const t = random(),
        angle = t * Math.PI * 5.3 + random() * 0.3;
      const radius =
        1.16 + Math.sin(t * Math.PI) * 0.6 + (random() - 0.5) * 0.3;
      return [
        Math.cos(angle) * radius + 0.4,
        -1.7 + t * 3.6 + (random() - 0.5) * 0.26,
        Math.sin(angle) * radius,
      ];
    },
    0xa0ead5,
    1.6,
    0.8,
    true,
  );
  const water = pointsCloud(
    950,
    () => {
      const t = random(),
        a = random() * Math.PI * 2;
      const r = 0.32 * (1 - t) + 0.08;
      return [
        0.1 - Math.sin(t * Math.PI) * 0.45 + Math.cos(a) * r,
        1.4 + t * 1.7,
        Math.sin(a) * r,
      ];
    },
    0xb6fff0,
    1.5,
    0.75,
    true,
  );
  const outerDust = pointsCloud(
    750,
    () => {
      const t = random(),
        a = t * Math.PI * 2;
      const r = 2.05 + random() * 0.65;
      return [
        Math.cos(a) * r + 0.35,
        -1.8 + Math.sin(a) * 0.5 + (random() - 0.5) * 0.13,
        Math.sin(a) * r,
      ];
    },
    0xb0a7d2,
    1.25,
    0.45,
    true,
  );

  const orbitGroup = new THREE.Group();
  orbitGroup.position.set(0.4, -1.84, 0);
  scene.add(orbitGroup);
  for (let i = 0; i < 3; i++) {
    const ring = new THREE.LineLoop(
      new THREE.BufferGeometry().setFromPoints(
        Array.from({ length: 180 }, (_, j) => {
          const a = (j / 180) * Math.PI * 2;
          return new THREE.Vector3(
            Math.cos(a) * (2.08 + i * 0.23),
            0,
            Math.sin(a) * (2.08 + i * 0.23),
          );
        }),
      ),
      new THREE.LineBasicMaterial({
        color: i === 1 ? 0x80779b : 0x609e8b,
        transparent: true,
        opacity: i === 1 ? 0.23 : 0.19,
      }),
    );
    ring.rotation.z = 0.1 + i * 0.06;
    ring.rotation.x = 0.12 + i * 0.08;
    orbitGroup.add(ring);
  }
  const trailMaterial = new THREE.LineBasicMaterial({
    color: 0xa0daca,
    transparent: true,
    opacity: 0.11,
    blending: THREE.AdditiveBlending,
  });
  for (let i = 0; i < 4; i++) {
    const points = Array.from({ length: 200 }, (_, j) => {
      const t = j / 199,
        a = t * Math.PI * 5.3 + i * 0.03,
        r = 1.2 + Math.sin(t * Math.PI) * 0.6;
      return new THREE.Vector3(
        Math.cos(a) * r + 0.4,
        -1.7 + t * 3.6,
        Math.sin(a) * r,
      );
    });
    scene.add(
      new THREE.Line(
        new THREE.BufferGeometry().setFromPoints(points),
        trailMaterial,
      ),
    );
  }

  let width = 0,
    height = 0;
  function resize() {
    const rect = canvas.parentElement.getBoundingClientRect();
    if (!rect.width || !rect.height) return;
    width = rect.width;
    height = rect.height;
    renderer.setSize(width, height, false);
    composer.setSize(width, height);
    camera.aspect = width / height;
    camera.position.z = camera.aspect < 0.9 ? 10.6 : 8.9;
    camera.updateProjectionMatrix();
  }
  const resizeObserver = new ResizeObserver(resize);
  resizeObserver.observe(canvas.parentElement);
  resize();
  let targetRotation = -0.45,
    targetTilt = 0.07,
    pointerX = 0,
    pointerY = 0;
  let dragging = false,
    lastX = 0,
    lastY = 0;
  canvas.addEventListener("pointerdown", (event) => {
    dragging = true;
    lastX = event.clientX;
    lastY = event.clientY;
    canvas.setPointerCapture(event.pointerId);
  });
  canvas.addEventListener("pointermove", (event) => {
    if (dragging) {
      targetRotation += (event.clientX - lastX) * 0.009;
      targetTilt = THREE.MathUtils.clamp(
        targetTilt + (event.clientY - lastY) * 0.004,
        -0.45,
        0.45,
      );
      lastX = event.clientX;
      lastY = event.clientY;
    } else {
      const rect = canvas.getBoundingClientRect();
      pointerX = (event.clientX - rect.left) / width - 0.5;
      pointerY = (event.clientY - rect.top) / height - 0.5;
    }
  });
  const release = () => {
    dragging = false;
  };
  canvas.addEventListener("pointerup", release);
  canvas.addEventListener("pointercancel", release);
  canvas.addEventListener("pointerleave", () => {
    pointerX = 0;
    pointerY = 0;
  });
  resetButton.addEventListener("click", () => {
    targetRotation = -0.45;
    targetTilt = 0.07;
    pointerX = 0;
    pointerY = 0;
  });
  motionButton.addEventListener("click", () => {
    paused = !paused;
    updateMotionButton();
  });
  reducedMotion.addEventListener("change", (event) => {
    paused = event.matches;
    updateMotionButton();
  });
  let visible = true;
  const visibilityObserver = new IntersectionObserver(
    (entries) => {
      visible = entries[0].isIntersecting;
    },
    { threshold: 0 },
  );
  visibilityObserver.observe(canvas);
  let lastTime = 0,
    elapsed = 0;
  function animate(now) {
    requestAnimationFrame(animate);
    if (now - lastTime < 1000 / 30) return;
    const delta = Math.min((now - lastTime) / 1000, 0.04);
    lastTime = now;
    if (!visible || document.hidden) return;
    if (!paused) elapsed += delta;
    vessel.rotation.y = THREE.MathUtils.lerp(
      vessel.rotation.y,
      targetRotation + (!paused ? Math.sin(elapsed * 0.16) * 0.12 : 0),
      0.045,
    );
    vessel.rotation.x = THREE.MathUtils.lerp(
      vessel.rotation.x,
      targetTilt,
      0.045,
    );
    vessel.position.y = 0.13 + (!paused ? Math.sin(elapsed * 0.7) * 0.05 : 0);
    dust.rotation.y = elapsed * 0.022;
    outerDust.rotation.y = -elapsed * 0.012;
    stars.rotation.y = pointerX * 0.018;
    stars.rotation.x = pointerY * 0.014;
    [stars, dust, water, outerDust].forEach((points) => {
      points.material.uniforms.time.value = elapsed;
    });
    composer.render();
  }
  requestAnimationFrame(animate);
  canvas.addEventListener("webglcontextlost", (event) => {
    event.preventDefault();
    paused = true;
    updateMotionButton();
    document.querySelector(".drag-hint").textContent =
      "3D 場景暫停，重新整理可恢復";
  });
}

import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";

type AxisKey = "x" | "y" | "z";
type ModeKey = "view" | "cut";
type PresetKey = "1x1x1" | "2x2x2" | "4x4x4" | "1x2x4";
type KeepSide = "min" | "max";

type Voxel = {
  x: number;
  y: number;
  z: number;
};

const presets: Record<PresetKey, [number, number, number]> = {
  "1x1x1": [1, 1, 1],
  "2x2x2": [2, 2, 2],
  "4x4x4": [4, 4, 4],
  "1x2x4": [1, 2, 4],
};

const previewSize = 16;
const previewCenter = new THREE.Vector3(previewSize / 2, previewSize / 2, previewSize / 2);
const cameraPosition = new THREE.Vector3(27, 23, 31);
const cutColors: Record<AxisKey, number> = {
  x: 0xf87171,
  y: 0x4ade80,
  z: 0x60a5fa,
};

const state = {
  mode: "view" as ModeKey,
  presetKey: "4x4x4" as PresetKey,
  dims: { x: 4, y: 4, z: 4 },
  cutAxis: "x" as AxisKey,
  keepSide: "min" as KeepSide,
  cutPosition: 2,
};

document.body.innerHTML = `
  <div id="app">
    <aside id="panel">
      <h1>Modulus Cut Playground</h1>
      <p>
        Voxels stay anchored at <code>0,0,0</code>. The camera is framed for a fixed
        <code>16x16x16</code> workspace.
      </p>

      <section class="section">
        <h2>Mode</h2>
        <div class="button-grid two" id="modeButtons">
          <button type="button" data-mode="view">View</button>
          <button type="button" data-mode="cut">Cut</button>
        </div>
      </section>

      <section class="section">
        <h2>Presets</h2>
        <div class="button-grid two" id="presetButtons">
          <button type="button" data-preset="1x1x1">1x1x1</button>
          <button type="button" data-preset="2x2x2">2x2x2</button>
          <button type="button" data-preset="4x4x4">4x4x4</button>
          <button type="button" data-preset="1x2x4">1x2x4</button>
        </div>
      </section>

      <section class="section" id="cutSection">
        <h2>Cut</h2>
        <div class="inline">
          <div class="control">
            <label for="cutAxis">Axis</label>
            <select id="cutAxis">
              <option value="x">X</option>
              <option value="y">Y</option>
              <option value="z">Z</option>
            </select>
          </div>
          <div class="control">
            <label for="keepSide">Keep</label>
            <select id="keepSide">
              <option value="min">Min side</option>
              <option value="max">Max side</option>
            </select>
          </div>
        </div>

        <div class="control">
          <label for="cutPosition">Plane <span id="cutPositionValue">2</span></label>
          <input id="cutPosition" type="range" min="0" max="4" step="1" value="2" />
        </div>
      </section>

      <section class="section">
        <h2>Stats</h2>
        <div id="stats"></div>
      </section>
    </aside>

    <main id="viewport">
      <div id="hint">Drag to orbit. Right-drag to pan. Zoom is locked.</div>
    </main>
  </div>
`;

const style = document.createElement("style");
style.textContent = `
  :root {
    --bg: #060b14;
    --panel: rgba(9, 14, 24, 0.94);
    --panel-border: rgba(148, 163, 184, 0.16);
    --text: #f8fafc;
    --muted: #94a3b8;
    --button: rgba(30, 41, 59, 0.88);
    --button-hover: rgba(51, 65, 85, 0.92);
    --button-active: rgba(15, 23, 42, 1);
    --button-ring: rgba(125, 211, 252, 0.7);
  }

  * {
    box-sizing: border-box;
  }

  html,
  body {
    margin: 0;
    height: 100%;
    overflow: hidden;
    background:
      radial-gradient(circle at top, rgba(96, 165, 250, 0.08), transparent 34%),
      radial-gradient(circle at right, rgba(148, 163, 184, 0.08), transparent 30%),
      var(--bg);
    color: var(--text);
    font: 14px/1.4 "Segoe UI", Tahoma, sans-serif;
  }

  body {
    min-height: 100%;
  }

  #app {
    display: grid;
    grid-template-columns: 320px 1fr;
    height: 100%;
  }

  #panel {
    padding: 20px 18px;
    background: var(--panel);
    backdrop-filter: blur(14px);
    border-right: 1px solid var(--panel-border);
    overflow: auto;
  }

  h1 {
    margin: 0 0 10px;
    font-size: 24px;
    line-height: 1.1;
  }

  p {
    margin: 0 0 12px;
    color: var(--muted);
  }

  code {
    color: #e2e8f0;
  }

  .section {
    margin-top: 18px;
    padding-top: 14px;
    border-top: 1px solid rgba(148, 163, 184, 0.1);
  }

  .section h2 {
    margin: 0 0 10px;
    font-size: 13px;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--muted);
  }

  .button-grid {
    display: grid;
    gap: 8px;
  }

  .button-grid.two {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  button {
    border: 1px solid rgba(148, 163, 184, 0.18);
    background: var(--button);
    color: var(--text);
    border-radius: 12px;
    padding: 10px 12px;
    cursor: pointer;
    font: inherit;
    transition: background-color 120ms ease, border-color 120ms ease;
  }

  button:hover {
    background: var(--button-hover);
  }

  button.active {
    background: var(--button-active);
    border-color: var(--button-ring);
  }

  .inline {
    display: flex;
    gap: 8px;
  }

  .inline > * {
    flex: 1 1 0;
  }

  .control {
    margin-bottom: 12px;
  }

  .control label {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 6px;
    font-weight: 600;
  }

  input[type="range"],
  select {
    width: 100%;
  }

  #stats {
    color: var(--muted);
    font-variant-numeric: tabular-nums;
  }

  #viewport {
    position: relative;
    min-width: 0;
    min-height: 0;
  }

  canvas {
    display: block;
    width: 100%;
    height: 100%;
  }

  #hint {
    position: absolute;
    left: 16px;
    bottom: 16px;
    padding: 10px 12px;
    border-radius: 12px;
    background: rgba(9, 14, 24, 0.78);
    border: 1px solid rgba(148, 163, 184, 0.14);
    color: var(--muted);
    pointer-events: none;
  }

  #cutSection.hidden {
    display: none;
  }

  @media (max-width: 920px) {
    #app {
      grid-template-columns: 1fr;
      grid-template-rows: auto 1fr;
    }

    #panel {
      border-right: 0;
      border-bottom: 1px solid var(--panel-border);
      max-height: 44vh;
    }
  }
`;
document.head.appendChild(style);

const viewport = document.getElementById("viewport") as HTMLDivElement;
const statsEl = document.getElementById("stats") as HTMLDivElement;
const cutSectionEl = document.getElementById("cutSection") as HTMLElement;
const modeButtonsEl = document.getElementById("modeButtons") as HTMLDivElement;
const presetButtonsEl = document.getElementById("presetButtons") as HTMLDivElement;
const cutAxisInput = document.getElementById("cutAxis") as HTMLSelectElement;
const keepSideInput = document.getElementById("keepSide") as HTMLSelectElement;
const cutPositionInput = document.getElementById("cutPosition") as HTMLInputElement;
const cutPositionValueEl = document.getElementById("cutPositionValue") as HTMLSpanElement;

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x050a12);

const camera = new THREE.PerspectiveCamera(42, 1, 0.1, 200);
camera.position.copy(cameraPosition);

const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.outputColorSpace = THREE.SRGBColorSpace;
viewport.appendChild(renderer.domElement);

const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
controls.enableZoom = false;
controls.target.copy(previewCenter);

scene.add(new THREE.AmbientLight(0xffffff, 1.2));

const keyLight = new THREE.DirectionalLight(0xffffff, 1.75);
keyLight.position.set(12, 18, 12);
scene.add(keyLight);

const fillLight = new THREE.DirectionalLight(0x9fb8ff, 0.55);
fillLight.position.set(-10, -8, -10);
scene.add(fillLight);

const voxelGroup = new THREE.Group();
scene.add(voxelGroup);

const floorGroup = new THREE.Group();
scene.add(floorGroup);

const cutPlaneGeometry = new THREE.PlaneGeometry(1, 1);
const activeCutPlane = new THREE.Mesh(
  cutPlaneGeometry,
  new THREE.MeshBasicMaterial({
    color: cutColors.x,
    transparent: true,
    opacity: 0.24,
    side: THREE.DoubleSide,
    depthWrite: false,
  })
);
activeCutPlane.renderOrder = 2;
scene.add(activeCutPlane);

const cubeGeometry = new THREE.BoxGeometry(0.999, 0.999, 0.999);
const cubeEdgesGeometry = new THREE.EdgesGeometry(cubeGeometry);
const cubeMaterial = new THREE.MeshStandardMaterial({
  color: 0xffffff,
  roughness: 0.3,
  metalness: 0.02,
});
const edgeMaterial = new THREE.LineBasicMaterial({
  color: 0x111827,
  transparent: true,
  opacity: 0.24,
});
const floorMaterials = [
  new THREE.MeshBasicMaterial({
    color: 0x0f172a,
    transparent: true,
    opacity: 0.6,
    side: THREE.DoubleSide,
    depthWrite: false,
  }),
  new THREE.MeshBasicMaterial({
    color: 0x020617,
    transparent: true,
    opacity: 0.74,
    side: THREE.DoubleSide,
    depthWrite: false,
  }),
];

function buildFloor(): void {
  for (let x = 0; x < previewSize; x++) {
    for (let z = 0; z < previewSize; z++) {
      const tile = new THREE.Mesh(
        cutPlaneGeometry,
        floorMaterials[(x + z) & 1]
      );
      tile.rotation.x = -Math.PI * 0.5;
      tile.position.set(x + 0.5, -0.002, z + 0.5);
      tile.renderOrder = 0;
      floorGroup.add(tile);
    }
  }
}

function buildBaseVoxels(): Voxel[] {
  const voxels: Voxel[] = [];
  for (let x = 0; x < state.dims.x; x++) {
    for (let y = 0; y < state.dims.y; y++) {
      for (let z = 0; z < state.dims.z; z++) {
        voxels.push({ x, y, z });
      }
    }
  }
  return voxels;
}

function voxelPassesCut(voxel: Voxel): boolean {
  if (state.mode !== "cut") {
    return true;
  }
  const position = state.cutPosition;
  const coordinate = voxel[state.cutAxis];
  if (state.keepSide === "min") {
    return coordinate < position;
  }
  return coordinate >= position;
}

function clearGroup(group: THREE.Group, disposeGeometry = false): void {
  while (group.children.length > 0) {
    const child = group.children[group.children.length - 1];
    if (disposeGeometry && "geometry" in child && child.geometry) {
      child.geometry.dispose();
    }
    group.remove(child);
  }
}

function syncCutRange(): void {
  cutAxisInput.value = state.cutAxis;
  keepSideInput.value = state.keepSide;
  cutPositionInput.min = "0";
  cutPositionInput.max = String(state.dims[state.cutAxis]);
  cutPositionInput.value = String(state.cutPosition);
  cutPositionValueEl.textContent = String(state.cutPosition);
}

function syncButtons(): void {
  for (const button of modeButtonsEl.querySelectorAll("button[data-mode]")) {
    button.classList.toggle("active", button.getAttribute("data-mode") === state.mode);
  }

  for (const button of presetButtonsEl.querySelectorAll("button[data-preset]")) {
    button.classList.toggle("active", button.getAttribute("data-preset") === state.presetKey);
  }

  cutSectionEl.classList.toggle("hidden", state.mode !== "cut");
}

function resetCamera(): void {
  camera.position.copy(cameraPosition);
  controls.target.copy(previewCenter);
  controls.update();
}

function applyPreset(presetKey: PresetKey): void {
  const dims = presets[presetKey];
  state.presetKey = presetKey;
  state.dims = { x: dims[0], y: dims[1], z: dims[2] };
  state.cutPosition = Math.round(state.dims[state.cutAxis] / 2);
  syncCutRange();
  syncButtons();
  rebuild(true);
}

function updateCutPlane(): void {
  const dims = state.dims;
  const material = activeCutPlane.material as THREE.MeshBasicMaterial;
  material.color.setHex(cutColors[state.cutAxis]);

  if (state.cutAxis === "x") {
    activeCutPlane.position.set(state.cutPosition, previewSize * 0.5, previewSize * 0.5);
    activeCutPlane.rotation.set(0, Math.PI * 0.5, 0);
    activeCutPlane.scale.set(previewSize, previewSize, 1);
  } else if (state.cutAxis === "y") {
    activeCutPlane.position.set(previewSize * 0.5, state.cutPosition, previewSize * 0.5);
    activeCutPlane.rotation.set(-Math.PI * 0.5, 0, 0);
    activeCutPlane.scale.set(previewSize, previewSize, 1);
  } else {
    activeCutPlane.position.set(previewSize * 0.5, previewSize * 0.5, state.cutPosition);
    activeCutPlane.rotation.set(0, 0, 0);
    activeCutPlane.scale.set(previewSize, previewSize, 1);
  }

  activeCutPlane.visible = state.mode === "cut";
}

function rebuild(recenterCamera = false): void {
  clearGroup(voxelGroup);

  const baseVoxels = buildBaseVoxels();
  const visibleVoxels = baseVoxels.filter(voxelPassesCut);

  for (const voxel of visibleVoxels) {
    const mesh = new THREE.Mesh(cubeGeometry, cubeMaterial);
    mesh.position.set(voxel.x + 0.5, voxel.y + 0.5, voxel.z + 0.5);
    voxelGroup.add(mesh);

    const edges = new THREE.LineSegments(cubeEdgesGeometry, edgeMaterial);
    edges.position.copy(mesh.position);
    voxelGroup.add(edges);
  }

  updateCutPlane();

  const total = baseVoxels.length;
  const kept = visibleVoxels.length;
  const removed = total - kept;
  const cutText = `${state.cutAxis.toUpperCase()} ${state.keepSide === "min" ? "<" : ">="} ${state.cutPosition}`;

  statsEl.innerHTML =
    `Mode: ${state.mode}<br>` +
    `Preset: ${state.presetKey}<br>` +
    `Bounds: ${state.dims.x} x ${state.dims.y} x ${state.dims.z}<br>` +
    `Visible voxels: ${kept} / ${total}<br>` +
    `Removed by cut: ${removed}<br>` +
    `Cut rule: ${state.mode === "cut" ? cutText : "hidden in view mode"}`;

  if (recenterCamera) {
    resetCamera();
  }
}

function resize(): void {
  const width = viewport.clientWidth;
  const height = viewport.clientHeight;
  camera.aspect = width / Math.max(height, 1);
  camera.updateProjectionMatrix();
  renderer.setSize(width, height, false);
}

modeButtonsEl.addEventListener("click", (event) => {
  const target = event.target as HTMLElement | null;
  const button = target?.closest("button[data-mode]") as HTMLButtonElement | null;
  if (!button) {
    return;
  }
  state.mode = button.dataset.mode as ModeKey;
  syncButtons();
  rebuild(false);
});

presetButtonsEl.addEventListener("click", (event) => {
  const target = event.target as HTMLElement | null;
  const button = target?.closest("button[data-preset]") as HTMLButtonElement | null;
  if (!button) {
    return;
  }
  applyPreset(button.dataset.preset as PresetKey);
});

cutAxisInput.addEventListener("input", () => {
  state.cutAxis = cutAxisInput.value as AxisKey;
  state.cutPosition = Math.min(state.cutPosition, state.dims[state.cutAxis]);
  syncCutRange();
  rebuild(false);
});

keepSideInput.addEventListener("input", () => {
  state.keepSide = keepSideInput.value as KeepSide;
  rebuild(false);
});

cutPositionInput.addEventListener("input", () => {
  state.cutPosition = Number(cutPositionInput.value);
  cutPositionValueEl.textContent = String(state.cutPosition);
  rebuild(false);
});

window.addEventListener("resize", resize);

buildFloor();
syncButtons();
syncCutRange();
resize();
resetCamera();
rebuild(true);

function animate(): void {
  requestAnimationFrame(animate);
  controls.update();
  renderer.render(scene, camera);
}

animate();

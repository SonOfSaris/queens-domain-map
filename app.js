// Queen's Domain interactive world map.
// Geometry, textures and markers are produced from the game's pak file by tools/Exporter (see README.md).
import * as THREE from './lib/three.module.min.js';
import { readSave, matchSave } from './save.js';

const DATA = './data/';
let stamp = '';
const dataUrl = f => DATA + f + stamp;
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

// ------------------------------------------------------------------ categories

const ITEM_ORDER = ['KeyItems', 'Weapons', 'Accessories', 'Upgrades', 'Artifacts', 'Treasure', 'Consumables', 'Coins', 'Other'];
const CATS = [
  { k: 'KeyItems', label: 'Key items', color: '#f2c14e', group: 'Items' },
  { k: 'Weapons', label: 'Weapons', color: '#ef5a5f', group: 'Items' },
  { k: 'Accessories', label: 'Accessories', color: '#b47cf5', group: 'Items' },
  { k: 'Upgrades', label: 'Upgrades', color: '#4cc38a', group: 'Items' },
  { k: 'Treasure', label: 'Treasure', color: '#ff9f43', group: 'Items' },
  { k: 'Artifacts', label: 'Artifacts', color: '#35c9c3', group: 'Items' },
  { k: 'Consumables', label: 'Consumables', color: '#5eb0ef', group: 'Items' },
  { k: 'Coins', label: 'Coins', color: '#c8913e', group: 'Items' },
  { k: 'Other', label: 'Other items', color: '#aab2c0', group: 'Items' },
  { k: 'loot', label: 'Loot on the ground (save)', color: '#9ad17a', glyph: '⇣', group: 'Items', off: true },
  { k: 'secret', label: 'Secret walls', color: '#ff6ad5', glyph: '?', group: 'World' },
  { k: 'boss', label: 'Bosses', color: '#ff3b30', glyph: '♛', group: 'World' },
  { k: 'npc', label: 'NPCs', color: '#7ee081', glyph: '☺', group: 'World' },
  { k: 'save', label: 'Save points', color: '#ffe08a', glyph: '✦', group: 'World' },
  { k: 'teleport', label: 'Teleporters', color: '#6fd3ff', glyph: '◎', group: 'World' },
  { k: 'door', label: 'Locked doors', color: '#d6a2ff', glyph: '⚿', group: 'World' },
  { k: 'keyslot', label: 'Key slots', color: '#c9b6ff', glyph: '◈', group: 'World' },
  { k: 'lore', label: 'Inscriptions & notes', color: '#d8c8a0', glyph: '✎', group: 'World' },
  { k: 'poi', label: 'Points of interest', color: '#ffffff', glyph: '★', group: 'World' },
  { k: 'chest', label: 'Chests', color: '#c58b4b', glyph: '▣', group: 'World', off: true },
  { k: 'enemy', label: 'Enemies', color: '#b05555', glyph: '☠', group: 'World', off: true },
  { k: 'switch', label: 'Levers & switches', color: '#8aa0b8', glyph: '⏻', group: 'World', off: true },
  { k: 'hint', label: 'Wall hints', color: '#9fe3d0', glyph: '!', group: 'World', off: true },
];
const CAT = Object.fromEntries(CATS.map(c => [c.k, c]));
CAT.player = { k: 'player', label: 'Your character', color: '#3ddc97', glyph: '▲' }; // only exists while a save is loaded
const DIFF = { Hidden: 'Hidden', OutOfView: 'Out of view', AccessibleLater: 'Accessible later', MainPath: 'On the main path' };
const AB = { GR: 'Green Ruins', AQ: 'Aqua Ruins', LIB: 'Library', MAUS: 'Mausoleum', AUT: 'Autumn Island', SZ: 'Secret Zone', SG: 'Stone Garden' };

// ------------------------------------------------------------------ persisted state

const store = {
  get(k, d) { try { const v = localStorage.getItem('qd.' + k); return v == null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem('qd.' + k, JSON.stringify(v)); } catch { /* storage unavailable */ } },
};
const S = Object.assign({
  catOn: {}, levelOn: {}, xray: true, range: 300, size: 1, hideFound: false,
  place: '', cut: 'off', cutOffset: 3, cutHeight: 0, textures: true, water: true, fog: false, fov: 55, panel: true,
}, store.get('settings', {}));
if (S.v !== 2) { S.levelOn = {}; S.v = 2; }
const found = new Set(store.get('found', []));
const saveSettings = () => store.set('settings', S);
const saveFound = () => store.set('found', [...found]);
// Item names can stay hidden until you know the item (S.spoilers, see "spoilers" further down). These are the items
// whose name was asked for by hand, kept by the item's path in the game data.
const revealed = new Set(store.get('revealed', []));

let world, md, levels = [], markers = [], byId = new Map();
let selected = null, highlight = null; // highlight = {cat, name}

// A loaded save slot ticks markers off on top of the manual marks.
let save = null, saveMatch = null, playerM = null;
const isFound = m => found.has(m.id) || !!saveMatch?.found.has(m.id);

// ------------------------------------------------------------------ renderer / scene

const canvas = $('#view');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, powerPreference: 'high-performance' });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
renderer.setClearColor(0x161a21);
const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(S.fov, 1, 0.3, 9000);
scene.add(new THREE.HemisphereLight(0xffffff, 0x777f8c, 1.25));
const sun = new THREE.DirectionalLight(0xfff4e0, 1.1);
sun.position.set(0.45, 1, 0.3);
scene.add(sun);
const haze = new THREE.Fog(0x161a21, 150, 1400);

const NO_CUT = 1e6;
const cutPlane = new THREE.Plane(new THREE.Vector3(0, -1, 0), NO_CUT); // keeps y < constant
renderer.clippingPlanes = [cutPlane];

const worldRoot = new THREE.Group();
scene.add(worldRoot);

let dirty = true;
const invalidate = () => { dirty = true; };

function resize() {
  const w = innerWidth, h = innerHeight;
  renderer.setSize(w, h, false);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
  invalidate();
}
addEventListener('resize', resize);

// ------------------------------------------------------------------ materials & textures

const texLoader = new THREE.TextureLoader();
const texCache = new Map(), matCache = new Map(), waterMats = [];
const maxAniso = renderer.capabilities.getMaxAnisotropy();
const srgb = v => new THREE.Color().setRGB(v[0], v[1], v[2], THREE.SRGBColorSpace);

function getTexture(t) {
  let p = texCache.get(t.id);
  if (p) return p;
  p = new Promise(resolve => {
    if (!t.file) return resolve(null);
    texLoader.load(dataUrl(t.file), tex => {
      tex.flipY = false;
      tex.colorSpace = THREE.SRGBColorSpace;
      tex.wrapS = tex.wrapT = THREE.RepeatWrapping;
      tex.magFilter = t.nearest ? THREE.NearestFilter : THREE.LinearFilter;
      tex.minFilter = t.nearest ? THREE.NearestMipmapLinearFilter : THREE.LinearMipmapLinearFilter;
      tex.anisotropy = Math.min(4, maxAniso);
      tex.needsUpdate = true;
      resolve(tex);
    }, undefined, () => resolve(null));
  });
  texCache.set(t.id, p);
  return p;
}

function getMaterial(sid) {
  let m = matCache.get(sid);
  if (m) return m;
  const s = world.states[sid];
  m = new THREE.MeshLambertMaterial({ side: s.twoSided ? THREE.DoubleSide : THREE.FrontSide });
  const t = s.tex >= 0 ? world.textures[s.tex] : null;
  const base = t ? srgb(t.avg) : s.color ? srgb(s.color) : new THREE.Color(0x8a8f98);
  m.userData = { state: s, base, tex: null };
  if (s.water) {
    m.color.copy(base).lerp(new THREE.Color(0x2f7fb0), 0.65);
    m.transparent = true; m.opacity = 0.42; m.depthWrite = false; m.side = THREE.DoubleSide;
    m.visible = S.water;
    waterMats.push(m);
  } else {
    m.color.copy(base);
    if (s.blend === 1) m.alphaTest = clamp(s.clip || 0.33, 0.05, 0.9);
    else if (s.blend >= 2) { m.transparent = true; m.opacity = 0.85; m.depthWrite = false; }
    if (t) getTexture(t).then(tex => {
      if (!tex) return;
      m.userData.tex = tex;
      if (S.textures) { m.map = tex; m.color.set(0xffffff); m.needsUpdate = true; invalidate(); }
    });
  }
  matCache.set(sid, m);
  return m;
}

function applyTextureToggle() {
  for (const m of matCache.values()) {
    if (m.userData.state.water || !m.userData.tex) continue;
    m.map = S.textures ? m.userData.tex : null;
    m.color.copy(S.textures ? new THREE.Color(0xffffff) : m.userData.base);
    m.needsUpdate = true;
  }
  invalidate();
}

// ------------------------------------------------------------------ level geometry

function dropArray() { this.array = null; }
let loadedBytes = 0, totalBytes = 0, loadQueue = [], loading = 0;

async function loadLevel(L) {
  if (L.status !== 'none') return;
  L.status = 'loading';
  updateZoneRow(L);
  try {
    const res = await fetch(dataUrl(L.file));
    if (!res.ok) throw new Error(res.status);
    const buf = await res.arrayBuffer();
    const dv = new DataView(buf);
    if (dv.getUint32(0, true) !== 0x31474451) throw new Error('bad magic');
    const n = dv.getUint32(4, true);
    let off = 8;
    const heads = [];
    for (let i = 0; i < n; i++, off += 12)
      heads.push({ state: dv.getInt32(off, true), v: dv.getUint32(off + 4, true), ic: dv.getUint32(off + 8, true) });
    const group = new THREE.Group();
    group.name = L.id;
    for (const h of heads) {
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(buf, off, h.v * 3), 3).onUpload(dropArray)); off += h.v * 12;
      g.setAttribute('normal', new THREE.BufferAttribute(new Int8Array(buf, off, h.v * 4), 4, true).onUpload(dropArray)); off += h.v * 4;
      g.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(buf, off, h.v * 2), 2).onUpload(dropArray)); off += h.v * 8;
      g.setIndex(new THREE.BufferAttribute(new Uint32Array(buf, off, h.ic), 1).onUpload(dropArray)); off += h.ic * 4;
      g.computeBoundingSphere();
      const mat = getMaterial(h.state);
      const mesh = new THREE.Mesh(g, mat);
      mesh.matrixAutoUpdate = false;
      if (mat.transparent) mesh.renderOrder = mat.userData.state.water ? 2 : 1;
      group.add(mesh);
    }
    group.visible = !!S.levelOn[L.id];
    worldRoot.add(group);
    L.group = group;
    L.status = 'ready';
    loadedBytes += L.bytes;
  } catch (e) {
    console.error('level failed', L.id, e);
    L.status = 'error';
  }
  updateZoneRow(L);
  updateLoadHud();
  invalidate();
}

function pump() {
  while (loading < 3 && loadQueue.length) {
    const L = loadQueue.shift();
    if (L.status !== 'none') continue;
    loading++;
    loadLevel(L).finally(() => { loading--; pump(); });
  }
}
function requestLevel(L, front = false) {
  if (L.status !== 'none' || loadQueue.includes(L)) return;
  front ? loadQueue.unshift(L) : loadQueue.push(L);
  pump();
}

function updateLoadHud() {
  const el = $('#load');
  const pending = levels.filter(L => S.levelOn[L.id] && (L.status === 'none' || L.status === 'loading'));
  const want = levels.filter(L => S.levelOn[L.id]);
  const tot = want.reduce((a, L) => a + L.bytes, 0) || 1;
  const done = want.filter(L => L.status === 'ready' || L.status === 'error').reduce((a, L) => a + L.bytes, 0);
  $('i', el).style.width = (100 * done / tot).toFixed(1) + '%';
  $('span', el).textContent = pending.length ? `Loading world… ${want.length - pending.length}/${want.length} zones` : '';
  el.classList.toggle('done', !pending.length);
}

// ------------------------------------------------------------------ camera

const cam = { pos: new THREE.Vector3(0, 200, 200), yaw: 0.7, pitch: -0.75, dist: 150, fly: false };
let camAnim = null;
const fwd = (yaw = cam.yaw, pitch = cam.pitch, out = new THREE.Vector3()) =>
  out.set(Math.sin(yaw) * Math.cos(pitch), Math.sin(pitch), Math.cos(yaw) * Math.cos(pitch));
const pivot = (out = new THREE.Vector3()) => fwd(cam.yaw, cam.pitch, out).multiplyScalar(cam.dist).add(cam.pos);
const _v = new THREE.Vector3(), _v2 = new THREE.Vector3(), _v3 = new THREE.Vector3();

function setPivot(p) { fwd(cam.yaw, cam.pitch, _v).multiplyScalar(-cam.dist); cam.pos.copy(p).add(_v); }

function applyCamera() {
  camera.position.copy(cam.pos);
  camera.lookAt(pivot(_v));
  camera.near = cam.fly ? 0.15 : clamp(cam.dist * 0.01, 0.15, 4);
  camera.updateProjectionMatrix();
  camera.updateMatrixWorld();
  const p = cam.fly ? cam.pos : pivot(_v2);
  $('#coords').textContent = `x ${p.x.toFixed(1)}   y ${p.z.toFixed(1)}   height ${p.y.toFixed(1)} m`;
  const cutY = S.cut === 'auto' && !cam.fly ? pivot(_v3).y + S.cutOffset : S.cut === 'manual' ? S.cutHeight : NO_CUT;
  cutPlane.constant = cutY;
  pivotDot.visible = !cam.fly;
  if (!cam.fly) { pivotDot.position.copy(pivot(_v3)); pivotDot.scale.setScalar(cam.dist * 0.006); }
}

function flyTo({ p, dist, yaw, pitch }, ms = 650, then = null) {
  const from = { p: pivot(), dist: cam.dist, yaw: cam.yaw, pitch: cam.pitch };
  const to = { p: p ? p.clone() : from.p, dist: dist ?? from.dist, yaw: yaw ?? from.yaw, pitch: pitch ?? from.pitch };
  let dy = (to.yaw - from.yaw) % (Math.PI * 2);
  if (dy > Math.PI) dy -= Math.PI * 2; else if (dy < -Math.PI) dy += Math.PI * 2;
  to.yaw = from.yaw + dy;
  if (cam.fly) { cam.fly = false; updateMode(); }
  camAnim = { t0: performance.now(), ms, from, to, then };
  invalidate();
}

function stepAnim(now) {
  if (!camAnim) return;
  const k = clamp((now - camAnim.t0) / camAnim.ms, 0, 1);
  const e = k < 0.5 ? 4 * k * k * k : 1 - Math.pow(-2 * k + 2, 3) / 2;
  const { from, to } = camAnim;
  cam.yaw = from.yaw + (to.yaw - from.yaw) * e;
  cam.pitch = from.pitch + (to.pitch - from.pitch) * e;
  cam.dist = from.dist * Math.pow(to.dist / from.dist, e);
  setPivot(_v2.copy(from.p).lerp(to.p, e));
  if (k >= 1) { const done = camAnim.then; camAnim = null; done?.(); saveCam(); }
  invalidate();
}

let camSaveT = 0;
function saveCam() {
  clearTimeout(camSaveT);
  camSaveT = setTimeout(() => store.set('cam', { p: cam.pos.toArray(), yaw: cam.yaw, pitch: cam.pitch, dist: cam.dist }), 400);
}

function frameLevel(L) {
  if (!L.min) return;
  const c = new THREE.Vector3().fromArray(L.min).add(_v.fromArray(L.max)).multiplyScalar(0.5);
  const r = clamp(_v.fromArray(L.max).sub(_v2.fromArray(L.min)).length() * 0.5, 15, 900);
  flyTo({ p: c, dist: r * 1.25, pitch: -0.85 });
}

const pivotDot = new THREE.Mesh(new THREE.SphereGeometry(1, 12, 8), new THREE.MeshBasicMaterial({ color: 0xf2c14e, depthTest: false, transparent: true, opacity: 0.85, clippingPlanes: [] }));
pivotDot.renderOrder = 50;
scene.add(pivotDot);

function updateMode() {
  $('#mode').textContent = cam.fly ? 'First person' : 'Orbit view';
  invalidate();
}

// First person puts the camera on the dot (at eye height, looking the way the orbit view was facing);
// switching back pulls the camera out again to the distance and tilt it had, around wherever you walked to.
const EYE = 1.6;
let orbitMemo = null, modeSwitch = null; // modeSwitch = how to finish the transition that is under way
function toggleFirstPerson() {
  if (modeSwitch) return;
  camAnim = null;
  const end = () => { modeSwitch = null; updateMode(); saveCam(); };
  if (!cam.fly) {
    orbitMemo = { dist: cam.dist, pitch: cam.pitch };
    const eye = pivot();
    eye.y += EYE;
    modeSwitch = () => { cam.dist = 10; cam.fly = true; end(); };
    flyTo({ p: eye, dist: 0.05, pitch: clamp(cam.pitch, -0.3, 0.5) }, 480, () => { cam.pos.copy(eye); modeSwitch?.(); });
  } else {
    const back = orbitMemo || { dist: 40, pitch: -0.7 };
    const feet = cam.pos.clone();
    feet.y -= EYE;
    cam.fly = false;
    cam.dist = 0.05;
    setPivot(cam.pos.clone());
    updateMode();
    modeSwitch = end;
    flyTo({ p: feet, dist: back.dist, pitch: back.pitch }, 480, () => modeSwitch?.());
  }
  // a transition cut short (drag, wheel, key) is finished by the frame loop, which calls modeSwitch once no animation is left
}

// ---- input
const keys = new Set();
let drag = null;
canvas.addEventListener('contextmenu', e => e.preventDefault());
canvas.addEventListener('pointerdown', e => {
  canvas.setPointerCapture(e.pointerId);
  drag = { x: e.clientX, y: e.clientY, sx: e.clientX, sy: e.clientY, t: performance.now(), pan: e.button !== 0 || e.shiftKey, moved: 0, btn: e.button };
  camAnim = null;
  canvas.classList.add('dragging');
});
canvas.addEventListener('pointermove', e => {
  if (!drag) { hover(e.clientX, e.clientY); return; }
  const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
  drag.x = e.clientX; drag.y = e.clientY;
  drag.moved += Math.abs(dx) + Math.abs(dy);
  if (drag.moved < 4) return;
  hideTip();
  if (drag.pan) {
    const k = (cam.fly ? 12 : cam.dist) * 2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) / innerHeight;
    const right = _v.setFromMatrixColumn(camera.matrixWorld, 0), up = _v2.setFromMatrixColumn(camera.matrixWorld, 1);
    cam.pos.addScaledVector(right, -dx * k).addScaledVector(up, dy * k);
  } else {
    // Orbit view: the camera swings around the dot, so the scene turns with the cursor.
    // First person: the scene follows the cursor too — dragging right turns the view left, dragging down looks up.
    const p = cam.fly ? null : pivot();
    const look = cam.fly ? -1 : 1;
    cam.yaw -= dx * 0.005 * look;
    cam.pitch = clamp(cam.pitch - dy * 0.005 * look, -1.55, 1.55);
    if (p) setPivot(p);
  }
  saveCam();
  invalidate();
});
canvas.addEventListener('pointerup', e => {
  canvas.classList.remove('dragging');
  const d = drag;
  drag = null;
  if (!d) return;
  if (d.moved < 5 && performance.now() - d.t < 500 && d.btn === 0) {
    const m = pickMarker(e.clientX, e.clientY);
    if (m) select(m); else if (selected) select(null);
  }
});
canvas.addEventListener('wheel', e => {
  e.preventDefault();
  camAnim = null;
  const step = clamp(e.deltaY, -240, 240) / 120;
  if (cam.fly) cam.pos.addScaledVector(fwd(), -step * (e.shiftKey ? 12 : 3));
  else {
    const p = pivot();
    cam.dist = clamp(cam.dist * Math.pow(1.15, step), 1.5, 4000);
    setPivot(p);
  }
  saveCam();
  invalidate();
}, { passive: false });
canvas.addEventListener('dblclick', e => {
  const hit = pickSurface(e.clientX, e.clientY);
  if (!hit) return;
  const d = hit.distanceTo(cam.pos);
  flyTo({ p: hit, dist: clamp(Math.min(cam.dist, d * 0.6), 4, 400) });
});

addEventListener('keydown', e => {
  if ($('dialog[open]')) return; // a dialog is up: Tab, Escape and letters belong to it
  if (e.target.matches?.('input, textarea, select')) { if (e.key === 'Escape') e.target.blur(); return; }
  const k = e.key.toLowerCase();
  if (k === 'tab') { e.preventDefault(); toggleFirstPerson(); return; }
  if (e.ctrlKey || e.metaKey || e.altKey) return;
  if ('wasdqe'.includes(k) || k === 'shift' || k.startsWith('arrow')) { keys.add(k); invalidate(); return; }
  if (k === 'f' && selected) focusMarker(selected);
  else if (k === 'p') goToPlayer();
  else if (k === 'm' && selected) toggleFound(selected);
  else if (k === 'x') { S.xray = !S.xray; syncViewControls(); applyMarkerSettings(); saveSettings(); }
  else if (k === 'c') { S.cut = S.cut === 'off' ? 'auto' : 'off'; syncViewControls(); saveSettings(); invalidate(); }
  else if (k === 'h') togglePanel();
  else if (k === 'escape') { if (highlight) setHighlight(null); else select(null); }
  else if (k === '/') { e.preventDefault(); showTab('guide'); $('#search').focus(); }
});
addEventListener('keyup', e => keys.delete(e.key.toLowerCase()));
addEventListener('blur', () => keys.clear());

function stepKeys(dt) {
  if (!keys.size) return false;
  let f = 0, r = 0, u = 0;
  if (keys.has('w') || keys.has('arrowup')) f++;
  if (keys.has('s') || keys.has('arrowdown')) f--;
  if (keys.has('d') || keys.has('arrowright')) r++;
  if (keys.has('a') || keys.has('arrowleft')) r--;
  if (keys.has('e')) u++;
  if (keys.has('q')) u--;
  if (!f && !r && !u) return false;
  camAnim = null;
  const speed = (cam.fly ? 9 : Math.max(6, cam.dist * 0.9)) * (keys.has('shift') ? 4 : 1) * dt;
  const dir = cam.fly ? fwd() : _v.set(Math.sin(cam.yaw), 0, Math.cos(cam.yaw));
  const right = _v2.set(-Math.cos(cam.yaw), 0, Math.sin(cam.yaw));
  cam.pos.addScaledVector(dir, f * speed).addScaledVector(right, r * speed);
  cam.pos.y += u * speed;
  saveCam();
  return true;
}

// ------------------------------------------------------------------ surface picking (depth render of one pixel)

const pickRT = new THREE.WebGLRenderTarget(1, 1);
const pickBuf = new Uint8Array(4);
const pickMat = new THREE.ShaderMaterial({
  clipping: true, side: THREE.DoubleSide, uniforms: { uFar: { value: 9000 } },
  vertexShader: `#include <clipping_planes_pars_vertex>
    varying float vZ;
    void main() {
      #include <begin_vertex>
      #include <project_vertex>
      #include <clipping_planes_vertex>
      vZ = -mvPosition.z;
    }`,
  fragmentShader: `#include <clipping_planes_pars_fragment>
    uniform float uFar; varying float vZ;
    void main() {
      #include <clipping_planes_fragment>
      float d = clamp(vZ / uFar, 0.0, 0.999999);
      vec3 enc = fract(vec3(1.0, 255.0, 65025.0) * d);
      enc -= enc.yzz * vec3(1.0 / 255.0, 1.0 / 255.0, 0.0);
      gl_FragColor = vec4(enc, 1.0);
    }`,
});

function pickSurface(x, y) {
  const w = innerWidth, h = innerHeight;
  const hidden = [points, pivotDot, beam];
  const vis = hidden.map(o => o.visible);
  hidden.forEach(o => { o.visible = false; });
  const wv = waterMats.map(m => m.visible);
  waterMats.forEach(m => { m.visible = false; });
  const fogWas = scene.fog;
  scene.fog = null;
  camera.setViewOffset(w, h, x, y, 1, 1);
  scene.overrideMaterial = pickMat;
  renderer.setRenderTarget(pickRT);
  renderer.setClearColor(0x000000, 0);
  renderer.clear();
  renderer.render(scene, camera);
  renderer.readRenderTargetPixels(pickRT, 0, 0, 1, 1, pickBuf);
  renderer.setRenderTarget(null);
  renderer.setClearColor(0x161a21, 1);
  scene.overrideMaterial = null;
  camera.clearViewOffset();
  scene.fog = fogWas;
  hidden.forEach((o, i) => { o.visible = vis[i]; });
  waterMats.forEach((m, i) => { m.visible = wv[i]; });
  invalidate();
  if (pickBuf[3] === 0) return null;
  const d = (pickBuf[0] / 255 + pickBuf[1] / 255 / 255 + pickBuf[2] / 255 / 65025) * 9000;
  const dir = new THREE.Vector3((x / w) * 2 - 1, -(y / h) * 2 + 1, 0.5).unproject(camera).sub(camera.position).normalize();
  const cosA = dir.dot(camera.getWorldDirection(_v));
  return camera.position.clone().addScaledVector(dir, d / cosA);
}

// ------------------------------------------------------------------ markers

const ATLAS_COLS = 32, CELL = 64;
let atlasCanvas, atlasTex, cellCount = 0, iconsImg = null;
const cellFor = new Map();

const pointsGeo = new THREE.BufferGeometry();
const pointsMat = new THREE.ShaderMaterial({
  transparent: true, depthWrite: false, depthTest: false, clipping: false,
  uniforms: {
    uAtlas: { value: null }, uGrid: { value: new THREE.Vector2(ATLAS_COLS, 1) }, uScale: { value: 1 }, uSize: { value: 1 },
    uRange: { value: 300 }, uCutY: { value: NO_CUT }, uPR: { value: 1 },
  },
  vertexShader: `
    attribute float cell; attribute float mstate;
    uniform float uScale, uSize, uRange, uCutY, uPR;
    varying float vCell; varying float vAlpha;
    void main() {
      vec4 mv = modelViewMatrix * vec4(position, 1.0);
      float dist = max(-mv.z, 0.01);
      gl_Position = projectionMatrix * mv;
      float px = clamp(1.4 * uSize * uScale / dist, 9.0 * uSize, 40.0 * uSize);
      float fade = 1.0 - smoothstep(uRange * 0.75, uRange, dist);
      bool hi = mstate > 1.5;
      if (hi) { px = max(px, 30.0 * uSize); fade = 1.0; }
      else if (position.y > uCutY + 0.6) fade = 0.0;
      float dim = (mstate > 0.5 && mstate < 1.5) ? 0.3 : 1.0;
      vAlpha = fade * dim;
      vCell = cell;
      gl_PointSize = px * uPR;
      if (fade < 0.01 || mv.z > 0.0) gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
    }`,
  fragmentShader: `
    uniform sampler2D uAtlas; uniform vec2 uGrid;
    varying float vCell; varying float vAlpha;
    void main() {
      vec2 c = vec2(mod(vCell, uGrid.x), floor(vCell / uGrid.x));
      vec4 t = texture2D(uAtlas, (c + gl_PointCoord) / uGrid);
      if (t.a < 0.06) discard;
      gl_FragColor = vec4(t.rgb, t.a * vAlpha);
      #include <colorspace_fragment>
    }`,
});
const points = new THREE.Points(pointsGeo, pointsMat);
points.frustumCulled = false;
points.renderOrder = 100;
scene.add(points);

const beam = new THREE.Line(
  new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(0, -1.5, 0), new THREE.Vector3(0, 60, 0)]),
  new THREE.LineBasicMaterial({ color: 0xf2c14e, transparent: true, opacity: 0.7, depthTest: false, clippingPlanes: [] }));
beam.visible = false;
beam.renderOrder = 90;
scene.add(beam);

function primaryItem(m) {
  let best = null, bo = 99;
  for (const [idx, n] of m.it || []) {
    const o = ITEM_ORDER.indexOf(md.items[idx].c);
    const oo = o < 0 ? 98 : o;
    if (oo < bo) { bo = oo; best = [idx, n]; }
  }
  return best;
}

function prepareMarkers() {
  for (const m of md.markers) {
    m.pos = new THREE.Vector3(m.p[0], m.p[1], m.p[2]);
    m.level = levels.find(L => L.id === m.l);
    if (m.t === 'item') {
      const pr = primaryItem(m);
      const it = md.items[pr[0]];
      m.cat = CAT[it.c] ? it.c : 'Other';
      m.item = it;
      m.name = it.n;
      m.title = it.n + (pr[1] > 1 ? ` ×${pr[1]}` : '') + (m.it.length > 1 ? ` +${m.it.length - 1} more` : '');
      m.key = 'i' + pr[0];
      m.pi = pr[0];
      if (m.by != null) m.carrier = md.enemies[m.by]; // not lying around: a one-time enemy carries it (Star Lizard loot sacks...)
    } else {
      m.cat = m.t;
      m.name = m.n || CAT[m.t].label;
      m.title = m.name;
      m.key = m.t;
    }
    m.search = (m.title + ' ' + (m.it || []).map(i => md.items[i[0]].n).join(' ') + ' ' + (m.c || '') + ' ' + (m.note || '') + ' ' + (m.carrier?.n || '')).toLowerCase();
    // the part of that without item names, for searching while names are hidden (see searchText)
    m.sbase = (m.t === 'item' ? CAT[m.cat].label : m.title) + ' ' + (m.c || '') + ' ' + (m.note || '') + ' ' + (m.carrier?.n || '');
    byId.set(m.id, m);
    markers.push(m);
  }
}

function buildAtlas() {
  const keys = new Map();
  for (const m of markers) if (!keys.has(m.key)) keys.set(m.key, m);
  for (const k of ['loot', 'player']) keys.set(k, { cat: k }); // badges for markers that only a save file adds
  for (const c of CATS) if (c.group === 'Items') keys.set('c:' + c.k, { cat: c.k, unknown: true }); // an item whose name is hidden shows only its kind
  const rows = Math.ceil(keys.size / ATLAS_COLS);
  atlasCanvas = document.createElement('canvas');
  atlasCanvas.width = ATLAS_COLS * CELL;
  atlasCanvas.height = rows * CELL;
  const g = atlasCanvas.getContext('2d');
  let i = 0;
  for (const [key, m] of keys) {
    const x = (i % ATLAS_COLS) * CELL, y = Math.floor(i / ATLAS_COLS) * CELL, c = CAT[m.cat];
    g.save();
    g.translate(x + CELL / 2, y + CELL / 2);
    g.beginPath(); g.arc(0, 0, 28, 0, Math.PI * 2);
    g.fillStyle = '#14171dee'; g.fill();
    g.lineWidth = 6; g.strokeStyle = c.color; g.stroke();
    g.lineWidth = 1.5; g.strokeStyle = '#000000aa'; g.beginPath(); g.arc(0, 0, 31, 0, Math.PI * 2); g.stroke();
    if (m.item && m.item.i >= 0 && iconsImg) {
      const sc = md.iconCell;
      g.beginPath(); g.arc(0, 0, 24.5, 0, Math.PI * 2); g.clip();
      g.drawImage(iconsImg, (m.item.i % md.iconCols) * sc, Math.floor(m.item.i / md.iconCols) * sc, sc, sc, -23, -23, 46, 46);
    } else {
      g.fillStyle = c.color;
      g.font = '600 32px "Segoe UI Symbol", "Segoe UI", system-ui, sans-serif';
      g.textAlign = 'center'; g.textBaseline = 'middle';
      g.fillText(m.unknown ? '?' : c.glyph || '•', 0, 2);
    }
    g.restore();
    cellFor.set(key, i++);
  }
  cellCount = i;
  atlasTex = new THREE.CanvasTexture(atlasCanvas);
  atlasTex.flipY = false;
  atlasTex.colorSpace = THREE.SRGBColorSpace;
  atlasTex.generateMipmaps = true;
  atlasTex.minFilter = THREE.LinearMipmapLinearFilter;
  pointsMat.uniforms.uAtlas.value = atlasTex;
  pointsMat.uniforms.uGrid.value.set(ATLAS_COLS, rows);
  for (const m of markers) m.cell = cellFor.get(m.key);
}

/**
 * CSS that cuts a marker's badge out of the atlas. The element also takes the class "badge", which carries the atlas
 * picture: once, in a style rule. As part of every badge's own style it made each list many megabytes of HTML.
 */
let _atlasCss = null;
function badgeStyle(m, size) {
  if (!atlasCanvas || m.cell == null) return '';
  if (!_atlasCss) {
    _atlasCss = document.head.appendChild(document.createElement('style'));
    _atlasCss.textContent = `.badge{background-image:url(${atlasCanvas.toDataURL('image/png')})}`;
  }
  const k = size / CELL, cell = cellOf(m);
  return `background-size:${atlasCanvas.width * k}px ${atlasCanvas.height * k}px;` +
    `background-position:-${(cell % ATLAS_COLS) * size}px -${Math.floor(cell / ATLAS_COLS) * size}px;`;
}

const isShown = m => S.catOn[m.cat] && m.level && S.levelOn[m.level.id] && !(S.hideFound && isFound(m) && m !== selected) &&
  !(S.place && m.t === 'item' && (S.place === 'chest' ? !m.c : S.place === 'enemy' ? !m.carrier : m.df !== S.place));
// With "Hide found" on, found markers stay out of group highlights and of the card's ‹ › navigation too (the selected one always shows).
const hiddenAsFound = m => S.hideFound && m !== selected && isFound(m);
const isHi = m => m === selected || m === playerM || (highlight && m.cat === highlight.cat && m.name === highlight.name && !hiddenAsFound(m));
let shown = [];

function rebuildPoints() {
  known = null; // whatever changed may have made an item known (a pickup found, a save loaded)
  shown = markers.filter(m => isShown(m) || isHi(m));
  const n = shown.length;
  pointsGeo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(n * 3), 3));
  pointsGeo.setAttribute('cell', new THREE.BufferAttribute(new Float32Array(n), 1));
  pointsGeo.setAttribute('mstate', new THREE.BufferAttribute(new Float32Array(n), 1));
  sortedAt = null;
  fillPoints();
  invalidate();
}

let sortedAt = null, sortT = 0;
function fillPoints() {
  const cp = camera.position;
  for (const m of shown) m._d = m.pos.distanceToSquared(cp);
  shown.sort((a, b) => (isHi(a) - isHi(b)) || (b._d - a._d)); // far first, highlighted last (on top)
  const P = pointsGeo.attributes.position.array, C = pointsGeo.attributes.cell.array, St = pointsGeo.attributes.mstate.array;
  shown.forEach((m, i) => {
    P[i * 3] = m.pos.x; P[i * 3 + 1] = m.pos.y; P[i * 3 + 2] = m.pos.z;
    C[i] = cellOf(m);
    St[i] = isHi(m) ? 2 : isFound(m) ? 1 : 0;
  });
  pointsGeo.attributes.position.needsUpdate = pointsGeo.attributes.cell.needsUpdate = pointsGeo.attributes.mstate.needsUpdate = true;
  sortedAt = cp.clone();
}

function applyMarkerSettings() {
  pointsMat.depthTest = !S.xray;
  pointsMat.uniforms.uRange.value = S.range;
  pointsMat.uniforms.uSize.value = S.size;
  pointsMat.needsUpdate = true;
  invalidate();
}

const pxScale = () => innerHeight / (2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)));
function markerPx(m, dist) {
  if (isHi(m)) return Math.max(clamp(1.4 * S.size * pxScale() / dist, 9 * S.size, 40 * S.size), 30 * S.size);
  if (dist > S.range) return 0;
  if (m.pos.y > cutPlane.constant + 0.6) return 0;
  return clamp(1.4 * S.size * pxScale() / dist, 9 * S.size, 40 * S.size);
}

const _p = new THREE.Vector3();
function pickMarker(x, y, slack = 3) {
  let best = null, bd = Infinity;
  const w = innerWidth, h = innerHeight;
  for (const m of shown) {
    _p.copy(m.pos).applyMatrix4(camera.matrixWorldInverse);
    if (_p.z > -0.05) continue;
    const dist = -_p.z;
    const px = markerPx(m, dist);
    if (!px) continue;
    _p.applyMatrix4(camera.projectionMatrix);
    const sx = (_p.x * 0.5 + 0.5) * w, sy = (-_p.y * 0.5 + 0.5) * h;
    const r = px / 2 + slack;
    if (Math.abs(sx - x) > r || Math.abs(sy - y) > r) continue;
    const score = dist - (isHi(m) ? 1e6 : 0);
    if (score < bd) { bd = score; best = m; }
  }
  return best;
}

let hoverT = 0;
function hover(x, y, slack) {
  const now = performance.now();
  if (now - hoverT < 40) return;
  hoverT = now;
  const m = pickMarker(x, y, slack);
  canvas.classList.toggle('hot', !!m);
  if (!m) return hideTip();
  const tip = $('#tip');
  tip.innerHTML = `${esc(titleOf(m))}<small>${esc(CAT[m.cat].label)}${isFound(m) ? ' · found' : ''}</small>`;
  tip.style.left = x + 'px'; tip.style.top = y + 'px';
  tip.hidden = false;
}
function hideTip() { $('#tip').hidden = true; }

function updateRing() {
  const ring = $('#ring');
  if (!selected) { ring.hidden = true; return; }
  _p.copy(selected.pos).project(camera);
  const behind = _p.z > 1;
  ring.hidden = behind;
  if (behind) return;
  ring.style.left = ((_p.x * 0.5 + 0.5) * innerWidth) + 'px';
  ring.style.top = ((-_p.y * 0.5 + 0.5) * innerHeight) + 'px';
}

// ------------------------------------------------------------------ selection & card

const TOK = {
  dungeon: 'Dungeon', goldenroom: 'Golden Room', CubeHall: 'Cube Hall', outerisles: 'Outer Isles', ShopIsleOnly: 'Shop Isle',
  underground: 'Underground', exterior: 'Exterior', basement: 'Basement', entrance: 'Entrance', mid: 'Middle', darkmaze: 'Dark Maze',
  droptunnel: 'Drop Tunnel', Ext: 'Exterior', MAUS2: 'Mausoleum 2', JigekiTemple: 'Jigeki Temple', FinalZone: 'Final Zone',
  SipehaWorld: 'Sipeha World', UpperShell: 'Upper World Shell', Ruins: 'World', Level: 'Root', Intro: 'Intro',
};
const FIXED_NAMES = { Ruins_Level_00: 'World root (persistent level)', VideoConfigMap: 'Isle of Neasied · Shop Isle terrain', Intro_Level_00: 'Intro cutscene set 1', Intro_Level_01: 'Intro cutscene set 2' };
const ownRegion = L => L.region && L.region !== '???' ? L.region : null;
/** Display name of a zone: the in-game region name where the game data provides one, otherwise the internal level name spelled out. */
function levelName(L) {
  if (!L) return '';
  if (L._name) return L._name;
  let name = ownRegion(L) || FIXED_NAMES[L.id];
  if (!name) {
    const parts = L.id.split('_').filter(p => !/^\d+$/.test(p));
    const main = parts[0] !== 'SZ' && parts.length > 1 &&
      levels.find(o => o !== L && ownRegion(o) && o.id[0] === L.id[0] && o.id.split('_')[1] === parts[0]);
    name = main ? ownRegion(main) + ' · ' + parts.slice(1).map(p => TOK[p] || AB[p] || p).join(' ')
      : parts.map(p => AB[p] || TOK[p] || p).join(' · ');
    L._inherited = !!main;
  }
  return L._name = name;
}

function focusMarker(m) {
  if (m.level && !S.levelOn[m.level.id]) setLevel(m.level, true);
  flyTo({ p: m.pos, dist: clamp(cam.dist, 6, 28), pitch: cam.pitch > -0.35 ? -0.7 : cam.pitch });
}

function select(m, opts = {}) {
  selected = m;
  beam.visible = !!m;
  if (m) {
    beam.position.copy(m.pos);
    if (opts.fly) focusMarker(m);
  }
  renderCard();
  rebuildPoints();
  $$('.entry.active').forEach(e => e.classList.remove('active'));
}

/** The markers the card's ‹ › buttons step through: all of this kind, minus the found ones while "Hide found" is on. */
function sameKind(m) {
  return markers.filter(o => o.cat === m.cat && o.name === m.name && (o === m || !hiddenAsFound(o)));
}

function itemRow(idx, n, extra = '') {
  return `<li>${itemIco(idx)}<span>${itemLabel(idx)}${n > 1 ? ` ×${n}` : ''}${extra}</span></li>`;
}
/** An item's picture as an icon; a question mark while its name is hidden. */
function itemIco(idx, size = 22, cls = 'ico') {
  const it = md.items[idx];
  if (!itemKnown(idx)) return `<span class="${cls} unknown">?</span>`;
  const cut = it.i >= 0 ? `background-image:url(${dataUrl('icons.png')});background-size:${md.iconCols * size}px auto;background-position:-${(it.i % md.iconCols) * size}px -${Math.floor(it.i / md.iconCols) * size}px` : '';
  return `<span class="${cls}" style="${cut}"></span>`;
}
const small = t => ` <small style="color:var(--dim)">${esc(t)}</small>`;

/** Card for an item that has no pickup marker (bought, given, dropped...). */
let cardItem = null; // the item this card is showing, null while the card is a marker's
function showItemCard(idx) {
  select(null);
  cardItem = idx;
  const it = md.items[idx], c = CAT[it.c] || CAT.Other, el = $('#card');
  const named = itemKnown(idx);
  const src = it.src || [];
  const used = markers.filter(m => m.rq?.includes(idx));
  const usedRows = used.slice(0, 8).map((m, i) =>
    `<li><span style="flex:1">${esc(m.name || CAT[m.cat]?.label || m.t)}${small(levelName(m.level))}</span><button data-use="${i}">Go</button></li>`).join('');
  const grimoire = src.some(s => s[2] === 'grimoire');
  el.style.setProperty('--c', c.color);
  el.innerHTML = `
    <div class="top">
      ${itemIco(idx, 52, 'big')}
      <div style="min-width:0;flex:1"><div class="kind">${esc(c.label)} · not a world pickup</div><h2>${itemLabel(idx)}</h2></div>
      <button class="close" title="Close (Esc)">×</button>
    </div>
    ${named && it.d ? `<p class="desc">${esc(it.d)}</p>` : ''}
    <div class="sect">How to get it</div>
    ${src.length ? `<ul>${src.map((s, i) => `<li><span style="flex:1">${esc(srcText(s, named))}</span>${s[1] && byId.has(s[1]) ? `<button data-go="${i}">Go</button>` : ''}</li>`).join('')}</ul>`
      : `<p class="hint" style="margin-top:6px">Nothing in the game data hands this item out${used.length ? ' — only the lock that uses it refers to it' : ''}. It may be unused content.</p>`}
    ${grimoire ? `<p class="hint" style="margin-top:6px">Find the Witch's Grimoire (Go above) and write the matching words in it. The game stores the accepted inscriptions only in hashed form, so this map cannot list them.</p>` : ''}
    ${used.length ? `<div class="sect">Used at</div><ul>${usedRows}</ul>` : ''}
    ${saveMatch ? `<div class="tags"><span class="tag ${saveMatch.owned.has(idx) ? 'have' : ''}">${esc(ownedText(idx))}</span></div>` : ''}`;
  el.hidden = false;
  $('.close', el).onclick = () => { el.hidden = true; cardItem = null; };
  $$('[data-go]', el).forEach(b => { b.onclick = () => select(byId.get(src[+b.dataset.go][1]), { fly: true }); });
  $$('[data-use]', el).forEach(b => { b.onclick = () => select(used[+b.dataset.use], { fly: true }); });
}

/**
 * An enemy spawner's condition ([save flag, min, max, why the flag is never set]) in words, plus whether the loaded save meets it.
 * `never` is the exporter's note for a flag nothing in the game sets: that enemy cannot appear at all.
 */
function spawnRule(sc) {
  const [flag, min, max, never] = sc;
  const open = max >= 9999;
  if (min <= 0 && open) return null; // any value passes
  const when = min === max ? (min === 0 ? 'is not set' : `is ${min}`) : open ? (min === 1 ? 'is set' : `is at least ${min}`)
    : min <= 0 ? `is at most ${max}` : `is ${min} to ${max}`;
  const v = save ? save.named.get(flag) ?? 0 : null;
  return { flag, never, text: `Appears only while the save flag ${flag} ${when}`, met: v == null ? null : v >= min && v <= max };
}
function spawnTags(m, tags) {
  const r = m.sc && spawnRule(m.sc);
  if (!r) return '';
  if (r.never) tags.push(`<span class="tag warn">Never appears (game bug)</span>`);
  else tags.push(`<span class="tag${r.met === false ? ' warn' : ''}" title="The game checks this before it lets the enemy spawn">${esc(r.text)}${r.met == null ? '' : r.met ? ' — met in your save' : ' — not met in your save'}</span>`);
  return r.never ? `<p class="hint" style="margin-top:6px">${esc(r.text)}. ${esc(r.never)}</p>` : '';
}

/** What the loaded save says about an item: how many you carry, or that you once had it. */
function ownedText(idx) {
  const n = saveMatch?.owned.get(idx);
  if (n == null) return 'Not obtained in this save';
  return n > 0 ? `You have ${n > 1 ? '×' + n : 'it'}` : 'You had it (used, sold or evolved)';
}

function renderCard() {
  const el = $('#card');
  const m = selected;
  cardItem = null;
  if (!m) { el.hidden = true; return; }
  if (m === playerM) return renderPlayerCard(el);
  const c = CAT[m.cat];
  const kin = sameKind(m);
  const pos = kin.indexOf(m);
  const inSave = !!saveMatch?.found.has(m.id);
  const done = inSave || found.has(m.id);
  // "2 / 5" normally; with Hide found on the count is of what is left ("2 / 3 left", or "3 left" when this one is already found)
  const left = S.hideFound ? kin.filter(o => !isFound(o)) : null;
  const navLabel = !left ? `${pos + 1} / ${kin.length}` : left.includes(m) ? `${left.indexOf(m) + 1} / ${left.length} left` : `${left.length} left`;
  const tags = [];
  if (m.c) tags.push(`<span class="tag">In: ${esc(m.c)}</span>`);
  if (m.carrier) tags.push(`<span class="tag">${m.how === 'open' ? 'Inside' : 'Carried by'}: ${esc(m.carrier.n)}</span>`);
  if (m.df) tags.push(`<span class="tag warn">${esc(DIFF[m.df] || m.df)}</span>`);
  if (m.h && !m.c) tags.push(`<span class="tag">Appears after a trigger</span>`);
  if (m.note) tags.push(`<span class="tag">${esc(m.note)}</span>`);
  if (saveMatch) {
    const sn = saveMatch.note.get(m.id);
    if (sn) tags.push(`<span class="tag have">${esc(sn)}</span>`);
    // The inventory count is about the item in general, not about this pickup (you can carry an egg and still have this one to fetch),
    // so it is worded as a count and kept neutral; whether this pickup is taken is what the button below says.
    const pi = m.item ? primaryItem(m)[0] : null;
    const carried = pi != null ? saveMatch.owned.get(pi) : null;
    const invTip = 'What your save carries of this item — not whether this pickup has been taken';
    if (carried > 0) tags.push(`<span class="tag" title="${invTip}">${carried} in your inventory</span>`);
    else if (carried === 0 && inSave) tags.push(`<span class="tag" title="${invTip}">No longer in your inventory (used, sold or evolved)</span>`);
    if (m.e != null && saveMatch.kills.has(m.e)) tags.push(`<span class="tag have">${saveMatch.kills.get(m.e)} defeated in total</span>`);
  }
  let body = '';
  const desc = m.item ? (markerKnown(m) ? m.item.d : '') : m.d;
  if (desc) body += `<p class="desc">${esc(desc)}</p>`;
  if (m.item && m.it.length > 1 || m.t === 'chest' && m.it?.length)
    body += `<div class="sect">Contents</div><ul>${m.it.map(([i, n]) => itemRow(i, n)).join('')}</ul>`;
  if (m.t === 'npc' && m.it?.length)
    body += `<div class="sect">Gives</div><ul>${m.it.map(([i, n]) => itemRow(i, n)).join('')}</ul>`;
  if (m.shop?.length)
    body += `<div class="sect">Sells</div><ul>${m.shop.map(([i, price, stock]) => itemRow(i, 1, small(`${price} coin${price === 1 ? '' : 's'}${stock < 0 ? ' · unlimited' : stock > 1 ? ` · stock ${stock}` : ''}`))).join('')}</ul>`;
  if (m.carrier) {
    const e = m.carrier, hp = `HP ${Math.round(e.hp)}`;
    const never = m.sc && spawnRule(m.sc)?.never;
    body += `<div class="sect">How to get it</div><p class="hint" style="margin-top:6px">${esc(never
      ? `The ${e.n} placed here would drop this, but it cannot spawn, so this one is out of reach. Mark it found to take it off your list.`
      : m.how === 'open' ? `Defeat the ${e.n} that waits here (${hp}), then open the chest it leaves behind.`
      : `Defeat the ${e.n} that spawns here (${hp}) and it drops this. It does not come back afterwards, so this is a one-off like any pickup.`)}</p>`;
  }
  body += spawnTags(m, tags);
  if (m.rq?.length)
    body += `<div class="sect">${m.t === 'keyslot' ? 'Accepts' : 'Requires'}</div><ul>${m.rq.map(i => itemRow(i, 1)).join('')}</ul>`;
  if (m.e != null) {
    const e = md.enemies[m.e];
    if (e.d && !desc) body += `<p class="desc">${esc(e.d)}</p>`;
    tags.push(`<span class="tag">HP ${Math.round(e.hp)}</span>`, `<span class="tag">XP ${e.xp}</span>`);
    if (e.drops?.length)
      body += `<div class="sect">Drops</div><ul>${e.drops.map(([i, ch, n]) => itemRow(i, n, ` <small style="color:var(--dim)">${(ch * 100).toFixed(ch < 0.1 ? 1 : 0)}%</small>`)).join('')}</ul>`;
  }
  el.style.setProperty('--c', c.color);
  el.innerHTML = `
    <div class="top">
      <div class="big badge" style="${badgeStyle(m, 52)}border:none"></div>
      <div style="min-width:0;flex:1">
        <div class="kind">${esc(c.label)}</div>
        <h2>${markerKnown(m) ? esc(m.title) : itemLabel(m.pi) + esc(m.title.slice(m.name.length))}</h2>
      </div>
      <button class="close" title="Close (Esc)">×</button>
    </div>
    <p class="where">${esc(levelName(m.level))} <span title="internal level name">· ${esc(m.l)}</span><br>
      x ${m.pos.x.toFixed(1)} · y ${m.pos.z.toFixed(1)} · height ${m.pos.y.toFixed(1)} m</p>
    ${tags.length ? `<div class="tags">${tags.join('')}</div>` : ''}
    ${body}
    <div class="acts">
      ${inSave ? `<button class="mark found" disabled title="Your save file already has this one">✓ Done in your save</button>`
        : `<button class="mark ${done ? 'found' : ''}">${done ? '✓ Found' : 'Mark found'} <kbd>M</kbd></button>`}
      <button class="focus">Focus <kbd>F</kbd></button>
      ${kin.length > 1 ? `<span class="nav"><button class="prev">‹</button>${navLabel}<button class="next">›</button></span>` : ''}
    </div>`;
  el.hidden = false;
  $('.close', el).onclick = () => select(null);
  if (!inSave) $('.mark', el).onclick = () => toggleFound(m);
  $('.focus', el).onclick = () => focusMarker(m);
  if (kin.length > 1) {
    $('.prev', el).onclick = () => select(kin[(pos - 1 + kin.length) % kin.length], { fly: true });
    $('.next', el).onclick = () => select(kin[(pos + 1) % kin.length], { fly: true });
  }
}

function toggleFound(m) {
  if (m === playerM || saveMatch?.found.has(m.id)) return; // the save file decides those
  found.has(m.id) ? found.delete(m.id) : found.add(m.id);
  saveFound();
  known = null;
  renderCard();
  rebuildPoints();
  refreshCounts();
}

function renderPlayerCard(el) {
  const c = CAT.player, m = playerM;
  const at = markers.find(o => o.t === 'save' && (o.a || '').toLowerCase() === save.savePoint);
  const picks = markers.filter(o => o.t === 'item');
  el.style.setProperty('--c', c.color);
  el.innerHTML = `
    <div class="top">
      <div class="big badge" style="${badgeStyle(m, 52)}border:none"></div>
      <div style="min-width:0;flex:1"><div class="kind">${esc(c.label)}</div><h2>${esc(saveLabel())}</h2></div>
      <button class="close" title="Close (Esc)">×</button>
    </div>
    <p class="where">${esc(levelName(m.level))}${at ? ` · saved at ${esc(at.name)}` : ''}<br>
      x ${m.pos.x.toFixed(1)} · y ${m.pos.z.toFixed(1)} · height ${m.pos.y.toFixed(1)} m</p>
    <div class="tags">
      <span class="tag have">Level ${save.level}</span>
      <span class="tag">${esc(playTime(save.playSeconds))} played</span>
      ${saveWhen() ? `<span class="tag">Saved ${esc(saveWhen())}</span>` : ''}
    </div>
    <p class="desc">This is where the save file puts you — the game writes it when you save, so it moves the next time you do.
      ${picks.filter(isFound).length} of ${picks.length} pickups are collected.</p>
    <div class="acts"><button class="focus">Focus <kbd>F</kbd></button><button class="fp">Stand here</button></div>`;
  el.hidden = false;
  $('.close', el).onclick = () => select(null);
  $('.focus', el).onclick = () => focusMarker(m);
  $('.fp', el).onclick = () => standAtPlayer();
}

function setHighlight(h) {
  highlight = h;
  rebuildPoints();
}

// ------------------------------------------------------------------ spoilers: item names shown or hidden
// S.spoilers: true = every item is named. false = an item is named once you know it: your save has or had it, one of
// its pickups is found, or you asked for its name on a card. Until then it shows only its kind ("Unknown weapon"),
// without picture or description. undefined = not asked yet (the page asks on its first start); names stay hidden.

const ITEM_KIND = { KeyItems: 'key item', Weapons: 'weapon', Accessories: 'accessory', Upgrades: 'upgrade', Treasure: 'treasure', Artifacts: 'artifact', Consumables: 'consumable' };
let known = null; // item indices that may be named while names are hidden; worked out again after every change
const namesHidden = () => S.spoilers !== true;

function knownItems() {
  if (known) return known;
  known = new Set();
  md.items.forEach((it, i) => { if (it.c === 'Coins' || revealed.has(it.p)) known.add(i); }); // a coin is no secret
  if (saveMatch) for (const i of saveMatch.owned.keys()) known.add(i);
  for (const m of markers) if (m.it && (m.t === 'item' || m.t === 'chest') && isFound(m)) for (const [i] of m.it) known.add(i);
  return known;
}
const itemKnown = idx => !namesHidden() || knownItems().has(idx);
const itemName = idx => itemKnown(idx) ? md.items[idx].n : 'Unknown ' + (ITEM_KIND[md.items[idx].c] || 'item');
/** An item's name as HTML: while it is hidden, its kind and a button that names this one item. */
const itemLabel = idx => itemKnown(idx) ? esc(md.items[idx].n)
  : `<i class="unk">${esc(itemName(idx))}</i> <button class="show-name" data-reveal="${idx}" title="Show the name, picture and description of this one item">show</button>`;
/** False for a pickup (or loot) marker whose item is not to be named yet. */
const markerKnown = m => !m.item || m.pi == null || itemKnown(m.pi);
const titleOf = m => markerKnown(m) ? m.title : itemName(m.pi) + m.title.slice(m.name.length);
/** The badge a marker shows on the map and in lists: the item's picture, or a "?" in its category's colour. */
const cellOf = m => markerKnown(m) ? m.cell : cellFor.get('c:' + m.cat) ?? m.cell;
/** True for a source line that would give the item away: the weapon it evolves from, or its own description. */
const SRC_TELLS = s => s[2] === 'evolve' || /^Item description: /.test(s[0]);
const srcText = (s, named) => named || !SRC_TELLS(s) ? s[0] : s[2] === 'evolve' ? 'Evolves from another weapon' : 'Hinted at in its own description';
/** What the search box looks through for a marker: while names are hidden, only the names of items you know. */
function searchText(m) {
  if (!namesHidden()) return m.search;
  return (m.sbase + ' ' + (m.it || []).filter(([i]) => itemKnown(i)).map(([i]) => md.items[i].n).join(' ')).toLowerCase();
}

function revealItem(idx) {
  revealed.add(md.items[idx].p);
  store.set('revealed', [...revealed]);
  namesChanged();
}

/** Names were switched on or off, or an item became known: everything that shows a name is drawn again. */
function namesChanged() {
  known = null;
  syncViewControls();
  rebuildPoints();
  renderCats();
  if (cardItem != null) showItemCard(cardItem); else renderCard();
  if (!$('#results').hidden) runSearch();
  hideTip();
}

// ------------------------------------------------------------------ guide panel

const openCats = new Set();
function catStats(k) {
  const list = markers.filter(m => m.cat === k);
  return { total: list.length, done: list.filter(isFound).length, list };
}

function renderCats() {
  const root = $('#cats');
  const scroll = root.scrollTop;
  root.innerHTML = '';
  let group = null;
  for (const c of CATS) {
    const st = catStats(c.k);
    if (!st.total) continue;
    if (c.group !== group) {
      group = c.group;
      root.insertAdjacentHTML('beforeend', `<div class="group-title">${group === 'Items' ? 'Collectible items' : 'World'}</div>`);
    }
    const el = document.createElement('div');
    el.className = 'cat' + (S.catOn[c.k] ? '' : ' off') + (openCats.has(c.k) ? ' open' : '');
    el.dataset.k = c.k;
    el.innerHTML = `<div class="head" title="Click to show / hide on the map">
        <input type="checkbox" ${S.catOn[c.k] ? 'checked' : ''} style="accent-color:${c.color}">
        <span class="sw" style="--c:${c.color}">${c.glyph ? esc(c.glyph) : ''}</span>
        <span class="name">${esc(c.label)}</span>
        <button class="chev" type="button" title="Show or hide the list"><span class="count">${st.done} / ${st.total}</span><i>▸</i></button>
      </div><div class="sub"></div>`;
    const head = $('.head', el);
    head.addEventListener('click', e => {
      if (e.target.closest('.chev')) {
        openCats.has(c.k) ? openCats.delete(c.k) : openCats.add(c.k);
        el.classList.toggle('open');
        if (openCats.has(c.k)) fillSub(el, c.k); else $('.sub', el).innerHTML = '';
        return;
      }
      S.catOn[c.k] = !S.catOn[c.k];
      saveSettings();
      $('input', head).checked = S.catOn[c.k];
      el.classList.toggle('off', !S.catOn[c.k]);
      rebuildPoints();
    });
    if (openCats.has(c.k)) fillSub(el, c.k);
    root.appendChild(el);
  }
  const extra = unplacedItems();
  if (extra.length) {
    root.insertAdjacentHTML('beforeend', `<div class="group-title">Not on the map</div>`);
    const el = document.createElement('div');
    el.className = 'cat' + (openCats.has('_extra') ? ' open' : '');
    const have = saveMatch ? extra.filter(o => saveMatch.owned.has(o.idx)).length : 0;
    el.innerHTML = `<div class="head" title="Items that are bought, given by NPCs, dropped by enemies or awarded by scripts">
        <span class="sw" style="--c:#8a93a3">…</span><span class="name">Shops, rewards &amp; drops</span>
        <button class="chev" type="button" title="Show or hide the list"><span class="count">${saveMatch ? `${have} / ` : ''}${extra.length}</span><i>▸</i></button></div><div class="sub"></div>`;
    const fill = () => {
      const groups = new Map(SRC_KINDS.map(([k]) => [k, []]));
      for (const o of extra) {
        if (S.hideFound && saveMatch?.owned.has(o.idx)) continue;
        (groups.get(srcKind(o.it)) || groups.get('place')).push(o);
      }
      $('.sub', el).innerHTML = SRC_KINDS.filter(([k]) => groups.get(k).length).map(([k, label]) =>
        `<div class="subhead">${esc(label)}<span>${groups.get(k).length}</span></div>` + groups.get(k).map(extraHtml).join('')).join('');
    };
    $('.head', el).onclick = () => {
      openCats.has('_extra') ? openCats.delete('_extra') : openCats.add('_extra');
      el.classList.toggle('open');
      if (openCats.has('_extra')) fill();
    };
    if (openCats.has('_extra')) fill();
    root.appendChild(el);
  }
  root.scrollTop = scroll;
  refreshProgress();
}

// Internal placeholder definitions that are not real items.
const JUNK_ITEM = /^[{]|^Null |^No Items$|Added Desc$/;
let _unplaced = null;
function unplacedItems() {
  if (_unplaced) return _unplaced;
  const list = md.items.map((it, idx) => ({ it, idx })).filter(o => !o.it.placed && !JUNK_ITEM.test(o.it.n));
  const ord = o => { const i = ITEM_ORDER.indexOf(o.it.c); return i < 0 ? 99 : i; };
  list.sort((a, b) => (!!b.it.src - !!a.it.src) || (ord(a) - ord(b)) || a.it.n.localeCompare(b.it.n));
  return _unplaced = list;
}
// How an item that is not a world pickup is obtained (third element of each source entry, set by the exporter).
const SRC_KINDS = [
  ['grimoire', "Witch's Grimoire inscriptions"], ['boss', 'Boss & enemy rewards'], ['npc', 'Given by NPCs'],
  ['shop', 'Sold in shops'], ['evolve', 'Weapon evolutions'], ['drop', 'Enemy drops'],
  ['quest', 'Quests & scripted events'], ['challenge', 'Challenges'], ['place', 'Other places'], ['', 'Source not found'],
];
const srcKind = it => it.src?.length ? (it.src[0][2] || 'place') : '';
function shortSrc(s) {
  if (s[2] === 'grimoire') return 'inscription';
  return s[0].replace(/^Reward for defeating |^Given by |^Obtained at the /, '').replace(/^Sold by (.*) for (\d+) coins?$/, '$1 · $2')
    .replace(/^Evolution of the /, 'from ').replace(/^Listed in the (.*)'s drop table.*/, '$1 (0%)')
    .replace(/^Dropped by (.*) [(](.*)[)]$/, '$1 · $2').replace(/^Likely a scripted event in /, '').replace(/^Item description: /, '');
}
function extraHtml(o) {
  const s = o.it.src?.[0];
  const have = saveMatch?.owned.get(o.idx);
  const named = itemKnown(o.idx);
  const tip = !named ? 'Its name is hidden. Click for how to get it.'
    : (s ? o.it.src.map(x => x[0]).join('\n') : 'No source found in the game data') + (saveMatch ? '\n' + ownedText(o.idx) : '');
  return `<div class="entry${have != null ? ' done' : ''}" data-item="${o.idx}" title="${esc(tip)}">
      ${itemIco(o.idx)}<span class="label">${named ? esc(o.it.n) : `<i class="unk">${esc(itemName(o.idx))}</i>`}</span>
      <span class="where">${esc(!s ? '—' : named || !SRC_TELLS(s) ? shortSrc(s) : s[2] === 'evolve' ? 'evolution' : '—')}</span>${have != null ? `<span class="have">✓${have > 1 ? ' ×' + have : ''}</span>` : ''}</div>`;
}

function groupByName(list) {
  const map = new Map();
  for (const m of list) {
    let g = map.get(m.name);
    if (!g) map.set(m.name, g = { name: m.name, list: [], done: 0, m });
    g.list.push(m);
    if (isFound(m)) g.done++;
  }
  return [...map.values()];
}

function entryHtml(g, cat) {
  const all = g.done === g.list.length;
  const lv = new Set(g.list.map(m => m.l));
  const where = g.list.length === 1 ? levelName(g.list[0].level) : `${lv.size} zone${lv.size > 1 ? 's' : ''}`;
  return `<div class="entry${all ? ' done' : ''}" data-cat="${esc(cat)}" data-name="${esc(g.name)}" title="Click to fly to the next one">
      <span class="ico badge" style="${badgeStyle(g.m, 22)}"></span>
      <span class="label">${markerKnown(g.m) ? esc(g.name) : `<i class="unk">${esc(itemName(g.m.pi))}</i>`}</span>
      <span class="where">${esc(where)}</span>
      <span class="n">${g.list.length > 1 || g.done ? `${g.done}/${g.list.length}` : ''}</span>
    </div>`;
}

function fillSub(el, k) {
  let groups = groupByName(catStats(k).list);
  if (S.hideFound) groups = groups.filter(g => g.done < g.list.length);
  const sortKey = g => g.m.item ? g.m.item.s : 0;
  // items whose name is hidden come after the named ones, by zone: their place in the alphabet would be a hint
  const hid = g => !markerKnown(g.m), zone = g => levelName(g.list[0].level);
  groups.sort((a, b) => (sortKey(a) - sortKey(b)) || (hid(a) - hid(b)) || (hid(a) ? zone(a).localeCompare(zone(b)) : 0) || a.name.localeCompare(b.name));
  $('.sub', el).innerHTML = groups.map(g => entryHtml(g, k)).join('');
}

function cycleEntry(cat, name, el) {
  const all = markers.filter(m => m.cat === cat && m.name === name);
  if (!all.length) return;
  // with Hide found on, step through the ones that are left; if none are, the found ones are all there is to show
  const rest = S.hideFound ? all.filter(m => !isFound(m)) : all;
  const list = rest.length ? rest : all;
  if (!S.catOn[cat]) { S.catOn[cat] = true; saveSettings(); renderCats(); }
  let next;
  if (selected && selected.cat === cat && selected.name === name) next = list[(list.indexOf(selected) + 1) % list.length];
  else next = list.find(m => !isFound(m)) || list[0];
  highlight = list.length > 1 ? { cat, name } : null;
  select(next, { fly: true });
  $$('.entry').forEach(e => e.classList.toggle('active', e.dataset.cat === cat && e.dataset.name === name));
}

document.addEventListener('click', e => {
  const en = e.target.closest('.entry');
  if (!en) return;
  if (en.dataset.item != null) showItemCard(+en.dataset.item);
  else if (en.dataset.name != null) cycleEntry(en.dataset.cat, en.dataset.name, en);
});

function refreshCounts() {
  for (const el of $$('#cats .cat[data-k]')) {
    const st = catStats(el.dataset.k);
    $('.count', el).textContent = `${st.done} / ${st.total}`;
    if (openCats.has(el.dataset.k)) fillSub(el, el.dataset.k);
  }
  refreshProgress();
  if (!$('#results').hidden) runSearch();
}

function refreshProgress() {
  const coll = markers.filter(m => m.t === 'item');
  const done = coll.filter(isFound).length;
  $('#progress i').style.width = (coll.length ? 100 * done / coll.length : 0) + '%';
  $('#progress span').textContent = `${done} / ${coll.length} pickups found`;
}

function runSearch() {
  const q = $('#search').value.trim().toLowerCase();
  const res = $('#results'), cats = $('#cats');
  if (!q) { res.hidden = true; cats.hidden = false; return; }
  const terms = q.split(/\s+/);
  const hits = markers.filter(m => { const text = searchText(m); return terms.every(t => text.includes(t)); });
  const groups = new Map();
  for (const m of hits) {
    const key = m.cat + '|' + m.name;
    let g = groups.get(key);
    if (!g) groups.set(key, g = { name: m.name, cat: m.cat, list: [], done: 0, m });
    g.list.push(m);
    if (isFound(m)) g.done++;
  }
  const arr = [...groups.values()].sort((a, b) => CATS.indexOf(CAT[a.cat]) - CATS.indexOf(CAT[b.cat]) || a.name.localeCompare(b.name)).slice(0, 150);
  const extra = unplacedItems().filter(o => itemKnown(o.idx) && terms.every(t => o.it.n.toLowerCase().includes(t))).slice(0, 40);
  res.innerHTML = arr.length || extra.length ? arr.map(g => entryHtml(g, g.cat)).join('') + extra.map(extraHtml).join('')
    : '<p class="hint" style="padding:8px">Nothing found.</p>';
  res.hidden = false; cats.hidden = true;
}

// ------------------------------------------------------------------ zones panel

function setLevel(L, on) {
  S.levelOn[L.id] = on;
  saveSettings();
  if (L.group) L.group.visible = on;
  if (on && L.status === 'none') requestLevel(L, true);
  updateZoneRow(L);
  updateLoadHud();
  rebuildPoints();
}

function renderZones() {
  const root = $('#zones');
  root.innerHTML = '';
  for (const L of levels) {
    const el = document.createElement('div');
    el.className = 'zone';
    el.dataset.id = L.id;
    const count = markers.filter(m => m.l === L.id && m.t === 'item').length;
    el.innerHTML = `<input type="checkbox">
      <div class="zname" title="Fly to this zone"><b>${esc(levelName(L))}</b><small>${esc(L.id)} · ${(L.tris / 1000).toFixed(0)}k tris${count ? ` · ${count} pickup${count > 1 ? 's' : ''}` : ''}${L.note ? ' · ' + esc(L.note) : ''}</small></div>
      <span class="st"></span><button class="solo" title="Show only this zone">◎</button>`;
    $('input', el).onchange = e => setLevel(L, e.target.checked);
    $('.zname', el).onclick = () => { if (!S.levelOn[L.id]) setLevel(L, true); frameLevel(L); };
    $('.solo', el).onclick = () => { for (const o of levels) setLevel(o, o === L); frameLevel(L); };
    root.appendChild(el);
    L.row = el;
    updateZoneRow(L);
  }
}

function updateZoneRow(L) {
  if (!L.row) return;
  $('input', L.row).checked = !!S.levelOn[L.id];
  L.row.classList.toggle('off', !S.levelOn[L.id]);
  $('.st', L.row).textContent = L.status === 'loading' ? '…' : L.status === 'error' ? 'error' : '';
}

// ------------------------------------------------------------------ floating zone labels

const labels = [];
function buildLabels() {
  const layer = document.createElement('div');
  layer.id = 'labels';
  document.body.appendChild(layer);
  const groups = new Map();
  const skip = ['Ruins_Level_00', '050_Ocean', '070_UpperShell', '990_B33sment'];
  for (const L of levels) {
    if (!L.min || L.hidden || skip.includes(L.id)) continue;
    const name = levelName(L);
    if (L._inherited) continue; // sub-areas share their region's label
    const g = groups.get(name);
    if (!g || L.tris > g.tris) groups.set(name, L);
  }
  for (const [name, L] of groups) {
    const el = document.createElement('div');
    el.className = 'zlabel';
    el.textContent = name;
    el.title = 'Fly to ' + name;
    el.onclick = () => frameLevel(L);
    layer.appendChild(el);
    labels.push({ el, L, pos: new THREE.Vector3((L.min[0] + L.max[0]) / 2, L.max[1], (L.min[2] + L.max[2]) / 2) });
  }
}
function updateLabels() {
  const far = !cam.fly && cam.dist > 110;
  for (const lb of labels) {
    let show = far && S.levelOn[lb.L.id];
    if (show) {
      _p.copy(lb.pos).project(camera);
      show = _p.z < 1 && Math.abs(_p.x) < 1.05 && Math.abs(_p.y) < 1.05;
      if (show) {
        lb.el.style.left = ((_p.x * 0.5 + 0.5) * innerWidth) + 'px';
        lb.el.style.top = ((-_p.y * 0.5 + 0.5) * innerHeight) + 'px';
      }
    }
    lb.el.style.display = show ? '' : 'none';
  }
}

// ------------------------------------------------------------------ view panel & chrome

function showTab(name) {
  $$('#tabs button').forEach(b => b.classList.toggle('on', b.dataset.tab === name));
  $$('.tab').forEach(t => t.classList.toggle('on', t.id === 'tab-' + name));
}
function togglePanel() {
  S.panel = !S.panel;
  saveSettings();
  $('#side').classList.toggle('closed', !S.panel);
  $('#expand').hidden = S.panel;
}

function syncViewControls() {
  $$('#cut-mode button').forEach(b => b.classList.toggle('on', b.dataset.v === S.cut));
  $('#cut-offset-row').style.display = S.cut === 'auto' ? '' : 'none';
  $('#cut-height-row').style.display = S.cut === 'manual' ? '' : 'none';
  $('#cut-offset').value = S.cutOffset; $('#cut-offset-out').textContent = `+${S.cutOffset} m`;
  $('#cut-height').value = S.cutHeight; $('#cut-height-out').textContent = `${S.cutHeight} m`;
  $('#xray').checked = S.xray;
  $('#range').value = S.range; $('#range-out').textContent = `${S.range} m`;
  $('#size').value = S.size; $('#size-out').textContent = `${Math.round(S.size * 100)}%`;
  $('#textures').checked = S.textures; $('#water').checked = S.water; $('#fog').checked = S.fog;
  $('#fov').value = S.fov; $('#fov-out').textContent = `${S.fov}°`;
  $('#hide-found').checked = S.hideFound;
  $('#place').value = S.place || '';
  $('#spoilers').checked = S.spoilers === true;
  $('#spoil-forget').hidden = !revealed.size || S.spoilers === true;
}

function wireUi() {
  $$('#tabs button').forEach(b => { b.onclick = () => showTab(b.dataset.tab); });
  $('#collapse').onclick = $('#expand').onclick = togglePanel;
  $('#mode').onclick = toggleFirstPerson;
  $('#search').addEventListener('input', runSearch);
  $('#cats-all').onclick = () => { CATS.forEach(c => { S.catOn[c.k] = true; }); saveSettings(); renderCats(); rebuildPoints(); };
  $('#cats-none').onclick = () => { CATS.forEach(c => { S.catOn[c.k] = false; }); saveSettings(); renderCats(); rebuildPoints(); };
  $('#cats-collect').onclick = () => { CATS.forEach(c => { S.catOn[c.k] = c.group === 'Items'; }); saveSettings(); renderCats(); rebuildPoints(); };
  $('#hide-found').onchange = e => {
    S.hideFound = e.target.checked;
    saveSettings(); rebuildPoints(); renderCats();
    if (selected) renderCard(); // its ‹ › count follows the setting
    if (!$('#results').hidden) runSearch();
  };
  $('#place').onchange = e => { S.place = e.target.value; saveSettings(); rebuildPoints(); };
  $('#zones-all').onclick = () => levels.forEach(L => setLevel(L, !L.hidden));
  $('#zones-none').onclick = () => levels.forEach(L => setLevel(L, false));
  $$('#cut-mode button').forEach(b => { b.onclick = () => { S.cut = b.dataset.v; saveSettings(); syncViewControls(); invalidate(); }; });
  const bind = (id, key, after, parse = Number) => $(id).addEventListener('input', e => {
    S[key] = e.target.type === 'checkbox' ? e.target.checked : parse(e.target.value);
    saveSettings(); syncViewControls(); after?.(); invalidate();
  });
  bind('#cut-offset', 'cutOffset');
  bind('#cut-height', 'cutHeight');
  bind('#xray', 'xray', applyMarkerSettings);
  bind('#range', 'range', applyMarkerSettings);
  bind('#size', 'size', applyMarkerSettings);
  bind('#textures', 'textures', applyTextureToggle);
  bind('#water', 'water', () => waterMats.forEach(m => { m.visible = S.water; }));
  bind('#fog', 'fog', () => { scene.fog = S.fog ? haze : null; for (const m of matCache.values()) m.needsUpdate = true; });
  bind('#fov', 'fov', () => { camera.fov = S.fov; camera.updateProjectionMatrix(); });
  $('#export').onclick = () => {
    const blob = new Blob([JSON.stringify({ game: 'QueensDomain', found: [...found] }, null, 1)], { type: 'application/json' });
    const a = Object.assign(document.createElement('a'), { href: URL.createObjectURL(blob), download: 'queens-domain-progress.json' });
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 2000);
  };
  $('#import').onclick = () => $('#import-file').click();
  $('#import-file').onchange = async e => {
    const f = e.target.files[0];
    if (!f) return;
    try {
      const j = JSON.parse(await f.text());
      for (const id of j.found || []) if (byId.has(id)) found.add(id);
      known = null;
      saveFound(); refreshCounts(); rebuildPoints(); renderCard();
    } catch { alert('That file is not a progress export.'); }
    e.target.value = '';
  };
  $('#reset').onclick = () => {
    if (!found.size || !confirm(`Clear all ${found.size} found marks?`)) return;
    found.clear(); known = null; saveFound(); refreshCounts(); rebuildPoints(); renderCard();
  };
  bind('#spoilers', 'spoilers', namesChanged);
  $('#spoil-forget').onclick = () => { revealed.clear(); store.set('revealed', []); namesChanged(); };
  $('#spoil-ask').addEventListener('cancel', e => e.preventDefault()); // it wants an answer, not Escape
  $$('#spoil-ask [data-spoilers]').forEach(b => {
    b.onclick = () => { S.spoilers = b.dataset.spoilers === 'show'; saveSettings(); $('#spoil-ask').close(); syncViewControls(); namesChanged(); };
  });
  $('#card').addEventListener('click', e => {
    const b = e.target.closest('[data-reveal]');
    if (b) revealItem(+b.dataset.reveal);
  });
  if (!S.panel) { $('#side').classList.add('closed'); $('#expand').hidden = false; }
}

// ------------------------------------------------------------------ save file

const playTime = sec => `${Math.floor(sec / 3600)} h ${String(Math.floor(sec % 3600 / 60)).padStart(2, '0')} m`;
const fmtWhen = d => d && !isNaN(d) ? d.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }) : '';
const slotName = s => `Slot ${s.slot + 1}${s.backup ? ` backup (${s.file})` : ''}`;
// list = save slots the local server found; id / mtime = the slot that is loaded (id null = a file opened by hand)
const saveUi = { list: [], server: false, id: null, mtime: 0, name: '', when: null, busy: false };
const saveLabel = () => saveUi.name || 'Save file';
const saveWhen = () => fmtWhen(saveUi.when || save?.savedAt);

let saveMarkers = []; // markers that only exist while a save is loaded: loot on the ground and the player
function rebuildSaveMarkers() {
  const selId = selected && saveMarkers.includes(selected) ? selected.id : null;
  if (saveMarkers.length) {
    const old = new Set(saveMarkers);
    markers = markers.filter(m => !old.has(m));
    for (const m of saveMarkers) byId.delete(m.id);
  }
  saveMarkers = [];
  playerM = null;
  if (save) {
    const base = markers.filter(m => m.level);
    const nearestLevel = pos => {
      let best = null, bd = Infinity;
      for (const m of base) { const d = m.pos.distanceToSquared(pos); if (d < bd) { bd = d; best = m.level; } }
      return best;
    };
    const itemAt = new Map(md.items.map((it, i) => [(it.p || '').toLowerCase(), i]));
    for (const d of save.drops) {
      const it = d.items.map(([p, n]) => [itemAt.get(p), n]).filter(x => x[0] != null);
      if (!it.length) continue;
      const m = { id: 'drop/' + d.id, t: 'loot', cat: 'loot', it, pos: new THREE.Vector3(...d.pos), note: 'Dropped earlier and not picked up' };
      const pr = primaryItem(m);
      m.item = md.items[pr[0]];
      m.pi = pr[0];
      m.name = m.item.n;
      m.title = m.name + (pr[1] > 1 ? ` ×${pr[1]}` : '');
      m.key = cellFor.has('i' + pr[0]) ? 'i' + pr[0] : 'loot';
      m.level = nearestLevel(m.pos);
      m.search = (m.title + ' loot drop').toLowerCase();
      m.sbase = 'loot drop';
      saveMarkers.push(m);
    }
    if (save.pos) {
      playerM = { id: '@player', t: 'player', cat: 'player', name: 'You', title: 'You (where you last saved)', key: 'player', pos: new THREE.Vector3(...save.pos), search: 'you player character', sbase: 'you player character' };
      playerM.level = nearestLevel(playerM.pos);
      saveMarkers.push(playerM);
    }
    for (const m of saveMarkers) {
      m.l = m.level?.id || '';
      m.cell = cellFor.get(m.key);
      markers.push(m);
      byId.set(m.id, m);
    }
  }
  if (selId) {
    selected = byId.get(selId) || null;
    beam.visible = !!selected;
    if (selected) beam.position.copy(selected.pos);
  }
}

function afterSaveChange() {
  known = null;
  renderSaveInfo();
  renderSaveSelect();
  renderCats();
  rebuildPoints();
  renderCard();
  if (!$('#results').hidden) runSearch();
}

function applySave(buf, meta) {
  let sv;
  try { sv = readSave(buf); } catch (e) {
    console.warn('save not readable:', e);
    throw new Error(`${meta.name || 'That file'} could not be read as a Queen's Domain save slot${/slot|settings/.test(e.message) ? ` (${e.message})` : ''}.`);
  }
  save = sv;
  saveMatch = matchSave(sv, md);
  Object.assign(saveUi, { id: null, mtime: 0, name: '', when: null }, meta);
  rebuildSaveMarkers();
  if (!S.saveSeen) { S.saveSeen = true; S.hideFound = true; saveSettings(); syncViewControls(); } // first save: show only what is left
  afterSaveChange();
}

function clearSave() {
  save = saveMatch = null;
  Object.assign(saveUi, { id: null, mtime: 0, name: '', when: null });
  rebuildSaveMarkers();
  afterSaveChange();
}

let saveInfoT = 0;
function renderSaveInfo(err) {
  const el = $('#save-info');
  $('#save-you').hidden = !playerM;
  el.classList.toggle('err', !!err);
  clearTimeout(saveInfoT);
  if (err) saveInfoT = setTimeout(() => renderSaveInfo(), 8000); // then back to the loaded save's summary
  if (err) el.textContent = String(err.message || err);
  else if (save) {
    const at = markers.find(o => o.t === 'save' && (o.a || '').toLowerCase() === save.savePoint);
    el.innerHTML = `<b>Level ${save.level}</b> · ${esc(playTime(save.playSeconds))} played${saveWhen() ? ` · saved ${esc(saveWhen())}` : ''}${at ? ` at ${esc(at.name)}` : ''}`;
  } else {
    el.textContent = !saveUi.server ? 'Saves are read automatically when the map is started with Open Map.bat. You can also open a World<N>.sav file by hand.'
      : saveUi.list.length ? '' : 'No save slots found. Pick "Game and save folders…" in the list above to say where they are, or "Open a save file…" for a single World<N>.sav.';
  }
  el.hidden = !el.textContent;
}

function renderSaveSelect() {
  const sel = $('#save-slot');
  const profiles = new Set(saveUi.list.map(s => s.profile)).size > 1;
  const opt = s => `<option value="${esc(s.id)}">${esc(slotName(s))}${profiles ? ` · profile ${s.profile + 1}` : ''} — ${esc(fmtWhen(new Date(s.mtime)))}</option>`;
  const order = (a, b) => a.profile - b.profile || a.slot - b.slot || a.file.localeCompare(b.file);
  const main = saveUi.list.filter(s => !s.backup).sort(order), back = saveUi.list.filter(s => s.backup).sort(order);
  sel.innerHTML = `<option value="">No save — manual marks only</option>` +
    (main.length ? `<optgroup label="Save slots">${main.map(opt).join('')}</optgroup>` : '') +
    (back.length ? `<optgroup label="Backups">${back.map(opt).join('')}</optgroup>` : '') +
    (save && !saveUi.id ? `<option value="@loaded">File: ${esc(saveUi.name)}</option>` : '') +
    `<option value="@file">Open a save file…</option>` +
    (saveUi.server ? `<option value="@folders">Game and save folders…</option>` : '');
  sel.value = save ? (saveUi.id || '@loaded') : '';
}

async function fetchSaveList() {
  try {
    const r = await fetch('api/saves', { cache: 'no-store' });
    if (!r.ok) throw new Error(r.status);
    saveUi.list = (await r.json()).saves || [];
    saveUi.server = true;
  } catch {
    saveUi.list = [];
    saveUi.server = false;
  }
}

async function loadSlot(entry) {
  const r = await fetch('api/save?id=' + encodeURIComponent(entry.id), { cache: 'no-store' });
  if (!r.ok) throw new Error(`Could not read ${entry.file}`);
  applySave(await r.arrayBuffer(), { id: entry.id, mtime: entry.mtime, name: slotName(entry), when: new Date(entry.mtime) });
}

async function loadSaveFile(f) {
  try {
    applySave(await f.arrayBuffer(), { name: f.name, when: new Date(f.lastModified) });
    S.save = '';
    saveSettings();
  } catch (e) { renderSaveInfo(e); }
  renderSaveSelect();
}

/** Picks up a slot again when the game has written it (and new or removed slots). */
async function pollSaves() {
  if (document.hidden || saveUi.busy || !saveUi.server) return;
  saveUi.busy = true;
  try {
    const before = JSON.stringify(saveUi.list);
    await fetchSaveList();
    if (JSON.stringify(saveUi.list) !== before) renderSaveSelect();
    const cur = saveUi.id && saveUi.list.find(s => s.id === saveUi.id);
    if (cur && cur.mtime !== saveUi.mtime && Date.now() - cur.mtime > 1200) await loadSlot(cur); // not while it is still being written
  } catch { /* half-written file: the next round reads it */ }
  saveUi.busy = false;
}

async function initSaves() {
  $('#save-slot').onchange = async e => {
    const v = e.target.value;
    if (v === '@file') { renderSaveSelect(); $('#save-file').click(); return; }
    if (v === '@folders') { renderSaveSelect(); openFolders(); return; }
    if (v === '@loaded') return;
    S.save = v;
    saveSettings();
    if (!v) { clearSave(); return; }
    const entry = saveUi.list.find(s => s.id === v);
    try { if (entry) await loadSlot(entry); } catch (err) { renderSaveInfo(err); renderSaveSelect(); }
  };
  $('#save-file').onchange = e => { const f = e.target.files[0]; e.target.value = ''; if (f) loadSaveFile(f); };
  $('#save-you').onclick = $('#youlabel').onclick = goToPlayer;
  $('#youlabel').textContent = 'You';
  addEventListener('dragover', e => { if (e.dataTransfer?.types?.includes('Files')) e.preventDefault(); });
  addEventListener('drop', e => {
    const f = [...(e.dataTransfer?.files || [])].find(x => /\.sav$/i.test(x.name));
    if (!f) return;
    e.preventDefault();
    loadSaveFile(f);
  });

  await fetchSaveList();
  const pick = slotToLoad();
  renderSaveSelect();
  renderSaveInfo();
  if (pick) { try { await loadSlot(pick); } catch (err) { renderSaveInfo(err); } }
  setInterval(pollSaves, 4000);
}

/** S.save: undefined = the slot played most recently, '' = no save, otherwise the chosen slot. */
function slotToLoad() {
  return S.save === '' ? null : saveUi.list.find(s => s.id === S.save) || saveUi.list.filter(s => !s.backup).sort((a, b) => b.mtime - a.mtime)[0];
}

// ------------------------------------------------------------------ game and save folders
// The map program keeps them in folders.json beside the map data, looks for the save slots there and builds the map
// from the game folder.

const folderUi = { ready: false, check: 0, timer: 0 };
const countOf = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;

async function foldersApi(path, body) {
  const r = await fetch(path, body ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : { cache: 'no-store' });
  if (!r.ok) throw new Error(`${path}: ${r.status}`);
  return r.json();
}

function folderLine(id, cls, text) {
  const el = $(id);
  el.className = 'hint state' + (cls ? ' ' + cls : '');
  el.textContent = text;
  if (id === '#fold-note') el.hidden = !text;
}

/** The line under each field: what the folder in it (or, left empty, the map's own search) leads to. */
function renderFolderState(st) {
  const g = st.game, s = st.saves;
  const pak = g.pak ? `${g.pak}, ${new Date(g.mtime).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })}` : '';
  if (g.error) folderLine('#fold-game-state', 'err', g.error);
  else if (g.forced) folderLine('#fold-game-state', g.found ? '' : 'warn', `The map was started with ${g.forced} as its game folder, which counts until it is closed.`);
  else if (g.set) folderLine('#fold-game-state', 'ok', `✓ Game found (${pak}).`);
  else if (g.found) folderLine('#fold-game-state', '', `Found by itself: ${g.home} (${pak}).`);
  else folderLine('#fold-game-state', 'warn', 'Not found by itself (looked around the map\'s own folder and in your Steam libraries). A map that is built already works without it: the game folder is used to find your saves and to build the map.');

  const n = [s.slots && countOf(s.slots, 'save slot'), s.backups && countOf(s.backups, 'backup')].filter(Boolean).join(' and ');
  if (s.error) folderLine('#fold-saves-state', 'err', s.error);
  else if (s.forced) folderLine('#fold-saves-state', n ? '' : 'warn', `The map was started with ${s.forced} as its save folder, which counts until it is closed${n ? ` (${n} found)` : ''}.`);
  else if (s.set) folderLine('#fold-saves-state', n ? 'ok' : 'warn', n ? `✓ ${n} found.` : 'No World<N>.sav files found in that folder.');
  else if (n) folderLine('#fold-saves-state', '', `Found by itself: ${n} in ${s.dirs.join(' and ')}.`);
  else folderLine('#fold-saves-state', 'warn', `None found by itself. Looked in ${s.looked.join(' and ')}.`);
}

const folderValues = () => ({ game: $('#fold-game').value, saves: $('#fold-saves').value });

function setFoldersBusy(busy) {
  $$('#folders input, #folders button').forEach(el => { el.disabled = busy || (el.id !== 'fold-cancel' && (!folderUi.ready || el.dataset.locked === '1')); });
}

async function openFolders() {
  let st = null;
  try { st = await foldersApi('api/folders'); } catch { /* no server, or one from before this existed */ }
  folderUi.ready = !!st;
  folderUi.check++;
  folderLine('#fold-note', 'warn', st ? '' : saveUi.server
    ? 'The program that serves the map is an older one. Close it and start the map again, then come back here.'
    : 'Folders can be set when the map is started with its program, QueensDomainMap.exe.');
  $('#fold-game').value = st?.game.set || '';
  $('#fold-saves').value = st?.saves.set || '';
  $('#fold-game').dataset.locked = st?.game.forced ? '1' : '';
  $('#fold-saves').dataset.locked = st?.saves.forced ? '1' : '';
  $$('#folders [data-browse]').forEach(b => { b.hidden = !st?.canBrowse; b.dataset.locked = $(b.dataset.browse === 'game' ? '#fold-game' : '#fold-saves').dataset.locked; });
  if (st) renderFolderState(st); else { folderLine('#fold-game-state', '', ''); folderLine('#fold-saves-state', '', ''); }
  setFoldersBusy(false);
  $('#folders').showModal();
}

/** While typing: ask the server what the folders would lead to, without storing them. */
function checkFolders() {
  clearTimeout(folderUi.timer);
  folderUi.timer = setTimeout(async () => {
    const n = ++folderUi.check;
    try {
      const st = await foldersApi('api/folders', { ...folderValues(), check: true });
      if (n === folderUi.check) renderFolderState(st);
    } catch { /* the next keystroke asks again */ }
  }, 300);
}

/** The slot list comes from other folders now: keep the loaded slot if it is still there, else take the most recent one. */
async function foldersChanged() {
  if (app) { app = await appStatus() || app; renderProgram(); }
  if (!md) { if ($('#setup').open) renderSetup(); return; } // no map loaded yet: only the first-run dialog cares
  await fetchSaveList();
  if (saveUi.id && !saveUi.list.some(s => s.id === saveUi.id)) {
    if (S.save) { delete S.save; saveSettings(); }
    clearSave();
  }
  const pick = save ? null : slotToLoad();
  renderSaveSelect();
  renderSaveInfo();
  if (pick) { try { await loadSlot(pick); } catch (err) { renderSaveInfo(err); } }
}

function initFolders() {
  const dlg = $('#folders');
  $('#folders-open').onclick = openFolders;
  $('#fold-cancel').onclick = () => dlg.close();
  $('#fold-game').oninput = $('#fold-saves').oninput = checkFolders;
  $$('#folders [data-browse]').forEach(b => {
    b.onclick = async () => {
      const input = $(b.dataset.browse === 'game' ? '#fold-game' : '#fold-saves');
      setFoldersBusy(true);
      folderLine('#fold-note', '', 'A Windows folder window is opening. Pick the folder there, or paste its path into the window\'s Folder field.');
      let note = '';
      try {
        const r = await foldersApi('api/browse', { which: b.dataset.browse, start: input.value });
        if (r.path) { input.value = r.path; checkFolders(); }
      } catch { note = 'The folder window did not open. Paste the folder\'s path into the field instead.'; }
      folderLine('#fold-note', 'warn', note);
      setFoldersBusy(false);
    };
  });
  $('#folders form').onsubmit = async e => {
    e.preventDefault();
    clearTimeout(folderUi.timer);
    folderUi.check++;
    try {
      const st = await foldersApi('api/folders', folderValues());
      renderFolderState(st);
      if (!st.saved) return; // the line under the field says what is wrong
      dlg.close();
      await foldersChanged();
    } catch { folderLine('#fold-note', 'err', 'The folders could not be stored. Is the map program still running?'); }
  };
}

// ------------------------------------------------------------------ the map program: first build, rebuild, quit
// QueensDomainMap.exe serves this page and makes the data folder from the game's files. Another web server does not
// answer api/status; the page then just loads the data that is there.

let app = null;
const setupUi = { timer: 0 };
const fmtDay = ms => new Date(ms).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
const fmtClock = s => `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
const STALE = {
  game: 'The game\'s files have changed since this map was built: a game update, or a mod was added or removed.',
  version: 'This map was built by an older version of the map program.',
};

async function appStatus() {
  try {
    const r = await fetch('api/status', { cache: 'no-store' });
    const j = r.ok ? await r.json() : null;
    return j?.app === 'QueensDomainMap' ? j : null;
  } catch { return null; }
}

/** View tab > Map program, and the notice above the search box when the game no longer matches the map. */
function renderProgram() {
  $('#program').hidden = !app;
  if (!app) return;
  $('#program-info').textContent = [
    app.data.built ? `Map built ${fmtDay(app.data.built)}.` : '',
    app.game.found ? `Game: ${app.game.home}.` : 'Game not found.',
    `Program version ${app.version}.`,
    app.tray ? 'It keeps running next to the clock until you quit it.' : '',
  ].filter(Boolean).join(' ');
  const why = STALE[app.data.stale];
  $('#stale').hidden = !why || store.get('staleSeen', 0) === app.data.built;
  $('#stale span').textContent = why || '';
}

function renderSetup() {
  const b = app.build, running = b.state === 'running', failed = b.state === 'failed', first = !app.data.ready, g = app.game;
  $('#setup-title').textContent = running ? 'Building the map' : first ? 'Welcome! Your map has to be built first' : 'Rebuild the map';
  $('#setup-text').textContent = running
    ? 'Reading the game\'s files. This takes about a minute; the map opens by itself when it is done.'
    : (first ? 'The map is made from your own copy of Queen\'s Domain. ' : STALE[app.data.stale] ? STALE[app.data.stale] + ' ' : '') +
      'Building reads the game\'s files (nothing in the game folder is changed) and takes about a minute and 600 MB of disk space.' +
      (first ? ' It is done once, and again after a game update.' : ' Your found marks are kept.');
  const line = $('#setup-game');
  line.className = 'hint state ' + (g.found ? 'ok' : 'warn');
  line.textContent = g.found ? `✓ Game found: ${g.home} (${g.pak}, ${fmtDay(g.mtime)})`
    : 'The game was not found on this PC. Say where it is with "Game folder…".';
  $('#setup-progress').hidden = !running;
  $('#setup-progress i').style.width = `${(b.fraction * 100).toFixed(1)}%`;
  $('#setup-progress span').textContent = `${b.text} · ${fmtClock(b.seconds)}`;
  $('#setup-error').hidden = !failed;
  $('#setup-error').textContent = failed ? `The build did not finish. ${b.error || ''}` : '';
  $('#setup-log').hidden = !failed || !b.log.length;
  $('#setup-log').textContent = b.log.join('\n');
  $('#setup-folders').hidden = $('#setup-go').hidden = running;
  $('#setup-cancel').hidden = running || first;
  $('#setup-go').disabled = !g.found;
  $('#setup-go').textContent = failed ? 'Try again' : first ? 'Build the map' : 'Rebuild';
}

function openSetup() {
  renderSetup();
  if (!$('#setup').open) $('#setup').showModal();
  if (app.build.state === 'running') watchBuild();
}

/** Follows a running build; when it is done the page starts over with the new data. */
function watchBuild() {
  clearInterval(setupUi.timer);
  setupUi.timer = setInterval(async () => {
    const st = await appStatus();
    if (!st) return; // the program is busy or gone: the next round tells
    app = st;
    if (st.build.state === 'running') { renderSetup(); return; }
    clearInterval(setupUi.timer);
    if (st.build.state === 'done' && st.data.ready) { location.reload(); return; }
    renderSetup();
  }, 700);
}

async function startBuild() {
  $('#setup-go').disabled = true;
  try {
    const r = await foldersApi('api/build', {});
    app = await appStatus() || app;
    if (!r.started) { app.build = { ...app.build, state: 'failed', error: r.error, log: [] }; renderSetup(); return; }
    renderSetup();
    watchBuild();
  } catch {
    app.build = { ...app.build, state: 'failed', error: 'The map program does not answer. Is it still running?', log: [] };
    renderSetup();
  }
}

function initProgram() {
  const dlg = $('#setup');
  dlg.addEventListener('cancel', e => { if ($('#setup-cancel').hidden) e.preventDefault(); }); // Escape: only when "Not now" is on offer
  $('#setup-cancel').onclick = () => dlg.close();
  $('#setup-folders').onclick = openFolders;
  $('#setup-go').onclick = startBuild;
  $('#rebuild').onclick = $('#stale-go').onclick = async () => { app = await appStatus() || app; openSetup(); };
  $('#stale-no').onclick = () => { store.set('staleSeen', app.data.built); renderProgram(); };
  $('#quit').onclick = async () => {
    if (!confirm('Stop the map program? Start QueensDomainMap.exe again to use the map.')) return;
    try { await foldersApi('api/quit', {}); } catch { /* gone already */ }
    document.body.insertAdjacentHTML('beforeend', '<div id="gone"><div><h2>The map program was stopped</h2><p>Start QueensDomainMap.exe again to use the map. You can close this tab.</p></div></div>');
  };
}

function goToPlayer() {
  if (!playerM) { showTab('guide'); renderSaveInfo(new Error('Load a save slot first to see where your character is.')); return; }
  select(playerM, { fly: true });
}

/** First person at the spot and facing of the last save. */
function standAtPlayer() {
  if (!playerM) return;
  camAnim = null;
  modeSwitch = null;
  if (!cam.fly) orbitMemo = { dist: cam.dist, pitch: cam.pitch };
  if (playerM.level && !S.levelOn[playerM.level.id]) setLevel(playerM.level, true);
  cam.pos.copy(playerM.pos);
  cam.pos.y += 0.7; // the saved position is the character's middle
  cam.yaw = save.yaw;
  cam.pitch = 0;
  cam.dist = 10;
  cam.fly = true;
  updateMode();
  saveCam();
}

function updateYouLabel() {
  const el = $('#youlabel');
  let on = !!playerM && playerM !== selected;
  if (on) {
    _p.copy(playerM.pos).project(camera);
    on = _p.z < 1 && Math.abs(_p.x) < 1 && Math.abs(_p.y) < 1;
    if (on) {
      el.style.left = ((_p.x * 0.5 + 0.5) * innerWidth) + 'px';
      el.style.top = ((-_p.y * 0.5 + 0.5) * innerHeight) + 'px';
    }
  }
  el.hidden = !on;
}

// ------------------------------------------------------------------ controller (Gamepad API, polled every frame)
// Layouts as worked out in RoomExplorer: with the "standard" mapping the sticks are axes 0-3 and the triggers buttons 6 / 7;
// the raw XInput layout (no mapping, 6+ axes) has LT on axis 2, the right stick on axes 3 / 4, RT on axis 5 and the d-pad on axes 6 / 7.

const pad = { prev: {}, connected: false, active: false, seenTrig: [false, false] };
const deadZone = v => { const a = Math.abs(v || 0); return a < 0.16 ? 0 : Math.sign(v) * (a - 0.16) / 0.84; };

function readPad() {
  const list = navigator.getGamepads ? navigator.getGamepads() : [];
  let gp = null;
  for (const g of list) if (g && g.connected && (!gp || (g.mapping === 'standard' && gp.mapping !== 'standard'))) gp = g;
  if (!gp) return null;
  const ax = gp.axes, on = i => !!gp.buttons[i]?.pressed;
  const raw = gp.mapping !== 'standard' && ax.length >= 6;
  let lt, rt, b;
  if (raw) {
    // trigger axes rest at -1 but report 0 until they are first touched
    [lt, rt] = [ax[2], ax[5]].map((v, i) => { if (v !== 0) pad.seenTrig[i] = true; return pad.seenTrig[i] ? (v + 1) / 2 : 0; });
    b = { a: on(0), b: on(1), x: on(2), y: on(3), lb: on(4), rb: on(5), back: on(6), start: on(7), l3: on(8), r3: on(9),
      up: ax[7] < -0.5, down: ax[7] > 0.5, left: ax[6] < -0.5, right: ax[6] > 0.5 };
  } else {
    lt = gp.buttons[6]?.value || 0; rt = gp.buttons[7]?.value || 0;
    b = { a: on(0), b: on(1), x: on(2), y: on(3), lb: on(4), rb: on(5), back: on(8), start: on(9), l3: on(10), r3: on(11),
      up: on(12), down: on(13), left: on(14), right: on(15) };
  }
  return { id: gp.id, b, lx: deadZone(ax[0]), ly: deadZone(ax[1]), rx: deadZone(raw ? ax[3] : ax[2]), ry: deadZone(raw ? ax[4] : ax[3]),
    lt: lt < 0.08 ? 0 : lt, rt: rt < 0.08 ? 0 : rt };
}

function stepPad(dt) {
  const p = readPad();
  if (!!p !== pad.connected) {
    pad.connected = !!p;
    $('#pad').hidden = !p;
    $('#pad-status').textContent = p ? `Connected: ${p.id.replace(/\s*\(.*$/, '').slice(0, 44)}` : 'Press any button on your controller to activate it.';
    if (!p) { pad.active = false; $('#reticle').hidden = true; hideTip(); }
  }
  if (!p) return false;
  const hit = k => p.b[k] && !pad.prev[k];
  let used = false;
  const cx = innerWidth / 2, cy = innerHeight / 2;

  if (hit('y')) { toggleFirstPerson(); used = true; }
  if (hit('a')) { const m = pickMarker(cx, cy, 26); if (m) select(m); used = true; }
  if (hit('b')) { if (highlight) setHighlight(null); else select(null); used = true; }
  if (hit('x') && selected) { toggleFound(selected); used = true; }
  if (hit('r3') && selected) { focusMarker(selected); used = true; }
  if ((hit('left') || hit('right')) && selected) {
    const kin = sameKind(selected);
    if (kin.length > 1) select(kin[(kin.indexOf(selected) + (p.b.right ? 1 : -1) + kin.length) % kin.length], { fly: true });
    used = true;
  }
  if (hit('up')) { goToPlayer(); used = true; }
  if (hit('down')) { S.cut = S.cut === 'off' ? 'auto' : 'off'; syncViewControls(); saveSettings(); used = true; }
  if (hit('start')) { togglePanel(); used = true; }
  if (hit('back')) { S.xray = !S.xray; syncViewControls(); applyMarkerSettings(); saveSettings(); used = true; }

  const lift = (p.b.rb ? 1 : 0) - (p.b.lb ? 1 : 0); // RB up, LB down
  if (p.lx || p.ly || p.rx || p.ry || p.lt || p.rt || lift) {
    camAnim = null;
    if (p.rx || p.ry) {
      const piv = cam.fly ? null : pivot();
      cam.yaw -= p.rx * 2.2 * dt;
      cam.pitch = clamp(cam.pitch - p.ry * 1.6 * dt, -1.55, 1.55);
      if (piv) setPivot(piv);
    }
    let speed = (p.b.l3 ? 4 : 1) * dt;
    if (cam.fly) speed *= 9 * (1 + 3 * p.rt) * (1 - 0.75 * p.lt);
    else {
      if (p.lt || p.rt) {
        const piv = pivot();
        cam.dist = clamp(cam.dist * Math.exp((p.lt - p.rt) * 1.8 * dt), 1.5, 4000);
        setPivot(piv);
      }
      speed *= Math.max(6, cam.dist * 0.9);
    }
    const dir = cam.fly ? fwd() : _v.set(Math.sin(cam.yaw), 0, Math.cos(cam.yaw));
    const right = _v2.set(-Math.cos(cam.yaw), 0, Math.sin(cam.yaw));
    cam.pos.addScaledVector(dir, -p.ly * speed).addScaledVector(right, p.lx * speed);
    cam.pos.y += lift * speed;
    saveCam();
    used = true;
  }

  pad.prev = p.b;
  if (used) {
    if (!pad.active) { pad.active = true; $('#reticle').hidden = false; }
    hover(cx, cy, 26); // tooltip for whatever is under the crosshair
  }
  return used;
}
canvas.addEventListener('pointermove', () => {
  if (pad.active) { pad.active = false; $('#reticle').hidden = true; }
});

// ------------------------------------------------------------------ main loop

let last = performance.now();
function frame(now) {
  requestAnimationFrame(frame);
  tick(now);
}
function tick(now) {
  const dt = Math.min(0.1, (now - last) / 1000);
  last = now;
  stepAnim(now);
  if (modeSwitch && !camAnim) { modeSwitch(); dirty = true; }
  if (stepKeys(dt)) dirty = true;
  if (stepPad(dt)) dirty = true;
  if (!dirty) return;
  dirty = false;
  applyCamera();
  pointsMat.uniforms.uScale.value = pxScale();
  pointsMat.uniforms.uPR.value = renderer.getPixelRatio();
  pointsMat.uniforms.uCutY.value = cutPlane.constant;
  if (shown.length && (!sortedAt || (now - sortT > 180 && sortedAt.distanceToSquared(camera.position) > 0.25))) {
    sortT = now;
    fillPoints();
  }
  renderer.render(scene, camera);
  updateRing();
  updateLabels();
  updateYouLabel();
}

async function main() {
  resize();
  wireUi();
  initFolders();
  initProgram();
  app = await appStatus();
  if (app && (!app.data.ready || app.build.state === 'running')) { openSetup(); return; } // nothing to show yet
  [world, md] = await Promise.all([
    fetch(DATA + 'world.json', { cache: 'no-store' }).then(r => r.json()),
    fetch(DATA + 'markers.json', { cache: 'no-store' }).then(r => r.json()),
  ]);
  stamp = '?v=' + encodeURIComponent(world.generated || '');
  levels = world.levels.filter(L => L.tris > 0);
  levels.sort((a, b) => a.id.localeCompare(b.id, 'en', { numeric: true }));
  for (const L of levels) {
    L.status = 'none';
    L.bytes = L.verts * 24 + L.tris * 12;
    if (S.levelOn[L.id] == null) S.levelOn[L.id] = !L.hidden;
  }
  for (const c of CATS) if (S.catOn[c.k] == null) S.catOn[c.k] = !c.off;

  prepareMarkers();
  iconsImg = await new Promise(res => { const im = new Image(); im.onload = () => res(im); im.onerror = () => res(null); im.src = dataUrl('icons.png'); });
  buildAtlas();
  syncViewControls();
  applyMarkerSettings();
  scene.fog = S.fog ? haze : null;
  renderCats();
  renderZones();
  buildLabels();
  rebuildPoints();

  const saved = store.get('cam', null);
  if (saved) {
    cam.pos.fromArray(saved.p); cam.yaw = saved.yaw; cam.pitch = saved.pitch; cam.dist = saved.dist;
  } else {
    const start = levels.find(L => L.id === '100_GR') || levels[0];
    const c = new THREE.Vector3().fromArray(start.min).add(_v.fromArray(start.max)).multiplyScalar(0.5);
    cam.dist = 260; cam.yaw = 0.6; cam.pitch = -0.7;
    setPivot(c);
  }
  updateMode();

  // nearest zones first
  const pv = pivot();
  const order = levels.filter(L => S.levelOn[L.id]).sort((a, b) => {
    const ca = _v.fromArray(a.min).add(_v2.fromArray(a.max)).multiplyScalar(0.5).distanceTo(pv);
    const cb = _v.fromArray(b.min).add(_v2.fromArray(b.max)).multiplyScalar(0.5).distanceTo(pv);
    return ca - cb;
  });
  order.forEach(L => requestLevel(L));
  updateLoadHud();
  requestAnimationFrame(frame);
  initSaves();
  renderProgram();
  if (S.spoilers == null) $('#spoil-ask').showModal(); // first start: item names shown or hidden?

  // debugging hooks
  window.qd = {
    THREE, scene, camera, renderer, cam, S, levels, select, flyTo, pickSurface, invalidate, toggleFirstPerson, goToPlayer, standAtPlayer, stepPad, isFound,
    step: dt => { stepKeys(dt); applyCamera(); }, tick,
    get markers() { return markers; }, get shown() { return shown; }, get save() { return save; }, get saveMatch() { return saveMatch; }, get player() { return playerM; },
  };
}

main().catch(e => {
  console.error(e);
  document.body.insertAdjacentHTML('beforeend', `<div style="position:fixed;inset:0;display:grid;place-items:center;z-index:99;background:#12151bee;padding:24px;text-align:center">
    <div><h2>Could not load the map data</h2><p>${esc(e.message)}</p><p>Start the map with <b>QueensDomainMap.exe</b> (it serves this page and builds the map data) instead of opening index.html directly.</p></div></div>`);
});

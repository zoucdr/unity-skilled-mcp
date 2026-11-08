/* RA2 Allied armour: ProBuilder modelling + UV atlas + texgen.for_mesh end-to-end verification.
 *
 * Reusable verification driver for the Unity Skilled MCP demo project.
 *   node docs/ra2_allied_build.js --rebuild              full rebuild (shapes -> merge -> atlas -> texture)
 *   node docs/ra2_allied_build.js --retexture            reuse existing meshes, re-atlas + repaint only
 *   node docs/ra2_allied_build.js --retexture --only=grizzly
 *
 * Requires a running Unity Editor with the MCP service on :8000 and the demo project open.
 * The JS client has no third-party dependencies (Node.js 18+).
 */
'use strict';
const fs = require('fs');
const path = require('path');

const REPO = path.resolve(__dirname, '..');
const CLIENT = path.join(REPO, 'unity-package/Skills/unity-skilled-mcp/scripts/unity_bridge.js');
const unity = require(CLIENT);
const PROJECT = path.join(REPO, 'demo').replace(/\\/g, '/');
unity.configure({ expected_project: PROJECT, timeout: 120 });

const OUT = path.join(require('os').tmpdir(), 'ra2/result.json');
const ROOT = 'RA2_Allied_Tanks';
const W = 512, H = 512;
const SPACING = 5.6;

const argv = process.argv.slice(2);
const only = (argv.find(a => a.startsWith('--only=')) || '').split('=')[1] || '';
const rebuild = argv.includes('--rebuild');
const doScene = argv.includes('--new-scene');

async function call(name, args) {
  const r = await unity.callSkillJson(name, args);
  if (r && r.success === false) throw new Error(name + ': ' + (r.message || JSON.stringify(r).slice(0, 300)));
  return r;
}

function saveResult(entry) {
  let prior = [];
  if (fs.existsSync(OUT)) { try { prior = JSON.parse(fs.readFileSync(OUT, 'utf8')); } catch (e) { prior = []; } }
  const byId = Object.fromEntries(prior.map(r => [r.id, r]));
  byId[entry.id] = entry;
  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, JSON.stringify(Object.values(byId), null, 2));
}

function mk(tag, shape, size, pos, rot) {
  return { tag, shape, size, pos, rot: rot || [0, 0, 0] };
}

function chassis(o) {
  const hull = o.hull || [1.9, 0.5, 2.9];
  const hy = o.hullY || 0.82;
  const tw = o.trackW || 0.42, th = o.trackH || 0.60, tl = o.trackLen || 3.1, tx = o.trackX || 0.94;
  const wheels = o.wheels || 3;
  const p = [];
  p.push(mk('hull', 'Cube', hull, [0, hy, 0]));
  p.push(mk('hull', 'Cube', [hull[0] * 0.94, 0.26, 0.9], [0, hy + 0.30, 1.28], [22, 0, 0]));
  p.push(mk('hull', 'Cube', [hull[0] * 0.88, 0.16, 0.75], [0, hy + 0.33, -1.03]));
  p.push(mk('track', 'Cube', [tw, th, tl], [-tx, hy - 0.34, 0]));
  p.push(mk('track', 'Cube', [tw, th, tl], [tx, hy - 0.34, 0]));
  for (let i = 0; i < wheels; i++) {
    const z = -tl / 2 + 0.55 + i * (tl - 1.1) / Math.max(1, wheels - 1);
    p.push(mk('wheel', 'Cylinder', [0.44, 0.10, 0.44], [-tx - 0.32, hy - 0.37, z], [0, 0, 90]));
    p.push(mk('wheel', 'Cylinder', [0.44, 0.10, 0.44], [tx + 0.32, hy - 0.37, z], [0, 0, 90]));
  }
  return p;
}

const PALETTES = {
  grizzly:        { hull: '#8d9aa7', turret: '#a6b3be', track: '#2b313a', wheel: '#4d5560', barrel: '#39434d', detail: '#2f6fd8', camo: '#5b7050', prism: '#7fd7e8' },
  ifv:            { hull: '#98a6b3', turret: '#b5c2cd', track: '#2e353d', wheel: '#535c66', barrel: '#3d4650', detail: '#356fd0', camo: '#5b7050', prism: '#7fd7e8' },
  mirage:         { hull: '#5d7150', turret: '#6d815d', track: '#272d2a', wheel: '#495349', barrel: '#333c36', detail: '#e0c76a', camo: '#3f5a3a', prism: '#7fd7e8' },
  prism:          { hull: '#8f9daa', turret: '#aab8c4', track: '#2b313a', wheel: '#4d5560', barrel: '#39434d', detail: '#2f6fd8', camo: '#5b7050', prism: '#69cfe6' },
  tankdestroyer:  { hull: '#6c7666', turret: '#7c8674', track: '#272c29', wheel: '#4a5247', barrel: '#333b42', detail: '#b8a25c', camo: '#4a5c40', prism: '#7fd7e8' },
  battlefortress: { hull: '#8795a1', turret: '#9eacb8', track: '#282e36', wheel: '#515a64', barrel: '#39434d', detail: '#2f6fd8', camo: '#5b7050', prism: '#7fd7e8' }
};

const TANKS = [
  {
    id: 'grizzly', label: '灰熊坦克 Grizzly Battle Tank', x: -2 * SPACING,
    parts: chassis({}).concat([
      mk('turret', 'Cube', [1.30, 0.42, 1.45], [0, 1.45, -0.10]),
      mk('turret', 'Cube', [1.08, 0.30, 0.55], [0, 1.48, 0.74], [18, 0, 0]),
      mk('barrel', 'Cylinder', [0.12, 1.55, 0.12], [0, 1.48, 1.62], [90, 0, 0]),
      mk('detail', 'Cylinder', [0.34, 0.10, 0.34], [0.38, 1.72, -0.44]),
      mk('detail', 'Cylinder', [0.035, 0.72, 0.035], [-0.54, 1.92, -0.56])
    ])
  },
  {
    id: 'ifv', label: '多功能步兵战车 IFV', x: -1 * SPACING,
    parts: chassis({ hull: [1.7, 0.42, 2.7], hullY: 0.75, trackW: 0.36, trackH: 0.52, trackLen: 2.9, trackX: 0.86 }).concat([
      mk('turret', 'Cylinder', [0.62, 0.28, 0.62], [0, 1.24, -0.15]),
      mk('detail', 'Cube', [0.90, 0.24, 0.70], [0, 1.50, -0.48]),
      mk('barrel', 'Cylinder', [0.08, 1.00, 0.08], [0, 1.30, 0.78], [90, 0, 0]),
      mk('detail', 'Cylinder', [0.04, 0.60, 0.04], [0.46, 1.72, -0.62])
    ])
  },
  {
    id: 'mirage', label: '幻影坦克 Mirage Tank', x: 0,
    parts: chassis({ hull: [2.0, 0.38, 3.0], hullY: 0.72, trackW: 0.40, trackH: 0.54, trackLen: 3.15, trackX: 0.98 }).concat([
      mk('turret', 'Cube', [1.15, 0.26, 1.25], [0, 1.10, -0.05]),
      mk('barrel', 'Cylinder', [0.10, 1.85, 0.10], [0, 1.12, 1.48], [90, 0, 0]),
      mk('camo', 'Cylinder', [0.09, 0.55, 0.09], [0, 1.52, -0.78]),
      mk('camo', 'Cone', [0.86, 0.86, 0.86], [0, 2.08, -0.78])
    ])
  },
  {
    id: 'prism', label: '光棱坦克 Prism Tank', x: 1 * SPACING,
    parts: chassis({}).concat([
      mk('turret', 'Cylinder', [0.72, 0.22, 0.72], [0, 1.24, -0.15]),
      mk('prism', 'Cone', [0.86, 1.15, 0.86], [0, 2.00, -0.15], [0, 45, 0]),
      mk('prism', 'Cube', [0.50, 0.50, 0.50], [0, 1.60, -0.15], [0, 45, 45]),
      mk('detail', 'Cylinder', [0.04, 0.50, 0.04], [0.52, 1.62, -0.62])
    ])
  },
  {
    id: 'tankdestroyer', label: '坦克杀手 Tank Destroyer (德国)', x: 2 * SPACING,
    parts: chassis({ hull: [1.95, 0.44, 3.0], hullY: 0.72, trackW: 0.42, trackH: 0.56, trackLen: 3.2, trackX: 0.96 }).concat([
      mk('turret', 'Cube', [1.25, 0.50, 1.60], [0, 1.28, -0.38]),
      mk('barrel', 'Cylinder', [0.16, 2.00, 0.16], [0, 1.24, 1.38], [90, 0, 0]),
      mk('detail', 'Cube', [1.00, 0.12, 0.50], [0, 1.56, -0.82])
    ])
  },
  {
    id: 'battlefortress', label: '战斗要塞 Battle Fortress', x: 3 * SPACING,
    parts: chassis({ hull: [2.3, 0.70, 3.40], hullY: 0.86, trackW: 0.50, trackH: 0.72, trackLen: 3.6, trackX: 1.16, wheels: 3 }).concat([
      mk('turret', 'Cube', [2.00, 0.65, 2.60], [0, 1.56, -0.20]),
      mk('turret', 'Cube', [1.60, 0.35, 0.55], [0, 1.56, 1.28]),
      mk('detail', 'Cube', [0.35, 0.30, 0.35], [-0.76, 1.96, 0.42]),
      mk('detail', 'Cube', [0.35, 0.30, 0.35], [0.76, 1.96, 0.42]),
      mk('detail', 'Cylinder', [0.06, 0.60, 0.06], [0, 2.22, -0.86])
    ])
  }
];

/* ---------------------------------------------------------------------------
 * Texture layout (v2)
 *
 * v1 painted ONE motif per atlas cell, stretched across the whole cell. Box
 * projection splits a cell into up to six axis rectangles, so that single
 * motif was clipped into six fragments (half circles, broken stripes, cut-off
 * stars) — and because every cell of the same tag got the same motif, the PNG
 * was full of repeated primitives.
 *
 * v2 paints PER FACE using uv_atlas' face_rects, and budgets the decorative
 * motifs per tank instead of stamping them on every cell.
 * ------------------------------------------------------------------------- */
function layersFor(cells, tagOfCell, pal, seedBase) {
  const L = [];
  // One insignia / one stripe / one prism highlight per tank, not per cell.
  let starBudget = 1, stripeBudget = 1, prismBudget = 1, camoBudget = 2;

  // A single global wear layer instead of one noise rect per face. TgRasterizer
  // walks the whole supersampled canvas for EVERY layer, so layer count is the
  // dominant cost: 218 layers used to occupy the main thread long enough to drop
  // the MCP response. The UV mask crops this layer to the used regions anyway.
  L.push({
    shape: 'rect', center: [W / 2, H / 2], size: [W, H], fill: 'noise',
    noise: 'fbm', scale: 6, seed: seedBase, color: '#ffffff', edgeColor: '#000000',
    opacity: 0.05, blend: 'overlay', name: 'wear_global'
  });

  for (const c of cells) {
    const [x, y, w, h] = c.rect_px;
    const tag = tagOfCell[c.id] || 'hull';
    const color = pal[tag] || pal.hull;
    const cx = x + w / 2, cy = y + h / 2;

    // A flat colour can safely cover the whole cell: box projection splits it
    // into six faces but each face sees the same colour, so nothing looks cut.
    L.push({ shape: 'rect', center: [cx, cy], size: [w * 0.99, h * 0.99], fill: 'solid', color, name: 'base_' + c.id });

    // Motifs MUST be drawn inside ONE axis face rectangle, otherwise the six
    // sub-rectangles each show a fragment of the same shape (the "repeated
    // primitives" look). Pick the first real face rect when available.
    const face = (c.face_rects && c.face_rects.length) ? c.face_rects[0] : { rect_px: c.rect_px };
    const [fx, fy, fw, fh] = face.rect_px;
    if (fw < 3 || fh < 3) continue;
    const fcx = fx + fw / 2, fcy = fy + fh / 2, fs = Math.min(fw, fh);

    if (tag === 'hull' && stripeBudget > 0) {
      L.push({ shape: 'line', from: [fx + fw * 0.14, fy + fh * 0.76], to: [fx + fw * 0.86, fy + fh * 0.76], width: 2, color: '#ffffff', opacity: 0.42, name: 'stripe_' + c.id });
      stripeBudget--;
    }
    if (tag === 'turret' && starBudget > 0) {
      L.push({ shape: 'star', center: [fcx, fcy], radius: fs * 0.28, innerRadius: fs * 0.12, pointCount: 5, fill: 'solid', color: '#eaf2fb', opacity: 0.94, name: 'allied_star_' + c.id });
      starBudget--;
    }
    if (tag === 'track') {
      // Grousers are the only legitimately repeated primitive; keep them sparse.
      for (let i = 1; i <= 3; i++) {
        L.push({ shape: 'line', from: [x + w * 0.08, y + h * i / 4], to: [x + w * 0.92, y + h * i / 4], width: 2, color: '#12161b', opacity: 0.75 });
      }
    }
    if (tag === 'wheel') {
      // A small flat hub in one face, not a full radial disc on every face.
      L.push({ shape: 'circle', center: [fcx, fcy], radius: fs * 0.15, fill: 'solid', color: '#20262d', opacity: 0.65, name: 'hub_' + c.id });
    }
    if (tag === 'barrel') {
      L.push({ shape: 'line', from: [fx + fw * 0.14, fcy], to: [fx + fw * 0.86, fcy], width: Math.max(1, fh * 0.10), color: '#9aa4ad', opacity: 0.28 });
    }
    if (tag === 'prism' && prismBudget > 0) {
      L.push({ shape: 'rect', center: [fcx, fcy], size: [fw * 0.88, fh * 0.88], fill: 'linear', color: '#d8f6ff', edgeColor: '#1f7fa8', name: 'prism_' + c.id });
      prismBudget--;
    }
    if (tag === 'camo' && camoBudget > 0) {
      L.push({ shape: 'circle', center: [fcx, fcy], radius: fs * 0.24, fill: 'solid', color: '#6f8a5c', opacity: 0.55, name: 'camo_' + c.id });
      camoBudget--;
    }
    if (tag === 'detail') {
      L.push({ shape: 'rect', center: [fcx, fcy], size: [fw * 0.48, fh * 0.48], fill: 'solid', color: pal.detail, opacity: 0.85 });
    }
  }
  return L;
}

/* Match atlas cells back to parts by BOTH centre distance and bounding-box size.
 * v1 used centre distance only with a forced 1:1 "used" set, which mislabelled
 * small wheels as hull parts and produced many identical motifs. */
function matchTags(cells, parts, hullPos) {
  const map = {};
  for (const c of cells) {
    const center = [
      (c.bounds_min[0] + c.bounds_max[0]) / 2,
      (c.bounds_min[1] + c.bounds_max[1]) / 2,
      (c.bounds_min[2] + c.bounds_max[2]) / 2
    ];
    const size = [
      c.bounds_max[0] - c.bounds_min[0],
      c.bounds_max[1] - c.bounds_min[1],
      c.bounds_max[2] - c.bounds_min[2]
    ].sort((a, b) => b - a);
    let best = -1, bestD = Infinity;
    for (let i = 0; i < parts.length; i++) {
      const p = parts[i];
      const expected = [p.pos[0] - hullPos[0], p.pos[1] - hullPos[1], p.pos[2] - hullPos[2]];
      const ps = p.size.slice().sort((a, b) => b - a);
      let dPos = 0, dSize = 0;
      for (let k = 0; k < 3; k++) {
        dPos += Math.pow(center[k] - expected[k], 2);
        dSize += Math.pow(size[k] - ps[k], 2);
      }
      const d = dPos + dSize * 0.55;
      if (d < bestD) { bestD = d; best = i; }
    }
    if (best >= 0) map[c.id] = parts[best].tag;
  }
  return map;
}

async function buildOne(tank, pal, seedBase) {
  const prefix = 'RA2_' + tank.id;
  const groupPath = ROOT;   // attach parts to the permanent root; avoids async parent lookup races
  const matPath = 'Assets/RA2Allied/Materials/' + prefix + '.mat';
  const texPath = 'Assets/RA2Allied/Textures/' + prefix + '.png';
  const meshPath = 'Assets/RA2Allied/Meshes/' + prefix + '_UvAtlas.asset';
  const partNames = tank.parts.map((p, i) => prefix + '_' + p.tag + '_' + i);
  const targetName = partNames[0];

  if (rebuild) {
    for (let i = 0; i < tank.parts.length; i++) {
      const p = tank.parts[i];
      const world = [tank.x + p.pos[0], p.pos[1], p.pos[2]];
      await call('probuilder.createShape', {
        shapeType: p.shape, name: partNames[i], parent: ROOT,
        position: world, rotation: p.rot, size: p.size
      });
    }
    console.log('  [model] ' + tank.id + ': ' + partNames.length + ' parts');
    const merged = await call('probuilder.mergeObjects', { gameObjects: partNames, deleteSourceObjects: true });
    const mdata = merged.data || merged;
    const faceCount = mdata.totalFacesAfter || 0;
    await call('material.create', { path: matPath });
    if (faceCount > 0) {
      await call('probuilder.setFaceMaterial', {
        gameObject: targetName, materialPath: matPath,
        faceIndices: Array.from({ length: faceCount }, (_, i) => i)
      });
    }
  }

  // uv_atlas now persists the rebuilt Mesh as an .asset and tolerates an object
  // whose ProBuilderMesh was already detached by a previous bake.
  const atlas = await call('probuilder.uv_atlas', {
    gameObject: targetName, columns: 4, margin: 0.06,
    projection: 'box', bake: true, save_mesh: true, mesh_path: meshPath,
    width: W, height: H
  });
  const adata = atlas.data || atlas;
  console.log('  [uv] ' + tank.id + ': ' + adata.islandCount + ' cells / ' + adata.triangleCount +
    ' tris, asset=' + (adata.meshAssetPath || 'NONE') + ', isAsset=' + adata.meshIsAsset);

  // A write call may still lose its HTTP response (texture import on the main
  // thread). Never replay a write: verify the side effect read-only instead.
  const errors = [];
  async function tolerant(name, args) {
    try { return await call(name, args); }
    catch (e) {
      const msg = (e && e.message) || String(e);
      // A tool returning success=false is a real failure, not a lost HTTP response.
      // Swallowing it used to make the driver "verify" a stale PNG as a new write.
      if (/MCP isError|"success"\s*:\s*false|success:\s*false/.test(msg)) throw e;
      errors.push(name + ': ' + msg);
      unity.configure({ expected_project: PROJECT, timeout: 120 });
      return null;
    }
  }

  const uvinfo = await tolerant('probuilder.uv_info', { gameObject: targetName, width: W, height: H });
  const udata = (uvinfo && uvinfo.data) || {};

  const tagMap = matchTags(adata.cells, tank.parts, tank.parts[0].pos);
  const layers = layersFor(adata.cells, tagMap, pal, seedBase);

  // Read the persisted .asset, not a scene hierarchy path: the latter breaks
  // after domain reloads / reparenting, and the asset is now the single source
  // of truth for both the mesh and its UV layout.
  const absTexForStamp = path.join(REPO, 'demo', texPath).replace(/\\/g, '/');
  const stampBefore = fs.existsSync(absTexForStamp) ? fs.statSync(absTexForStamp).mtimeMs : 0;
  const painted = await tolerant('texgen.for_mesh', {
    mesh_path: meshPath,
    layers, group_by: 'all', padding: 4, fill_mode: 'nearest',
    width: W, height: H, background: '#2b3138',
    origin: 'topLeft', supersample: 2,
    output_path: texPath, overwrite: true, select_asset: false,
    material_path: matPath, texture_property: '_MainTex',
    filter_mode: 'Bilinear', wrap_mode: 'Clamp'
  });
  const pdata = (painted && painted.data) || {};

  const absTex = path.join(REPO, 'demo', texPath).replace(/\\/g, '/');
  const absMat = path.join(REPO, 'demo', matPath).replace(/\\/g, '/');
  const absMesh = path.join(REPO, 'demo', meshPath).replace(/\\/g, '/');
  const deadline = Date.now() + 12000;
  while (Date.now() < deadline &&
         !(fs.existsSync(absTex) && fs.statSync(absTex).size > 1000 &&
           fs.statSync(absTex).mtimeMs > stampBefore)) {
    await new Promise(r => setTimeout(r, 250));
  }
  const pngOk = fs.existsSync(absTex) && fs.statSync(absTex).size > 1000 &&
    fs.statSync(absTex).mtimeMs > stampBefore;
  const matOk = fs.existsSync(absMat);
  const meshOk = fs.existsSync(absMesh);
  const info = await tolerant('material.get_info', { path: matPath });
  const yaml = (info && info.data && info.data.yaml) || '';
  const liveMatch = yaml.match(/mainTexture:\s*(\S+)/);
  let assigned = !!liveMatch && liveMatch[1] !== 'None' && liveMatch[1].length > 0;
  if (!assigned && matOk) {
    const matText = fs.readFileSync(absMat, 'utf8');
    assigned = /_MainTex:[\s\S]{0,400}?m_Texture:\s*\{fileID:\s*2800000,\s*guid:\s*([0-9a-f]{32})/.test(matText);
  }
  if (!pngOk || !matOk || !assigned || !meshOk) {
    throw new Error('write verification failed for ' + tank.id +
      ' (png=' + pngOk + ', mat=' + matOk + ', assigned=' + assigned + ', mesh=' + meshOk + ') ' + errors.join(' | '));
  }
  console.log('  [tex] ' + tank.id + ': png=' + fs.statSync(absTex).size + 'B, layers=' + layers.length +
    ', response_lost=' + (painted === null) + ', material=' + assigned + ', meshAsset=' + meshOk);

  const entry = {
    id: tank.id, label: tank.label, group: groupPath, mesh: targetName,
    parts: partNames.length, textureMode: 'all',
    atlas: adata, uvInfo: udata, paint: pdata,
    errors, pngBytes: fs.statSync(absTex).size, materialAssigned: assigned,
    meshAsset: meshPath, meshPersisted: adata.meshPersisted,
    material: matPath, texture: texPath, tagMap
  };
  saveResult(entry);
  return entry;
}

(async () => {
  try {
    if (doScene) {
      await call('scene.create', { name: 'RA2Allied', path: 'Assets/RA2Allied/RA2Allied.unity' });
      await call('hierarchy.create_empty', { name: ROOT });
      console.log('[scene] new scene RA2Allied, root ' + ROOT);
    } else if (rebuild) {
      const roots = await call('scene.get_hierarchy', {});
      if (!JSON.stringify(roots).includes(ROOT)) await call('hierarchy.create_empty', { name: ROOT });
    }

    const skip = (argv.find(a => a.startsWith('--skip=')) || '').split('=')[1] || '';
    const skipSet = new Set(skip ? skip.split(',') : []);
    const list = TANKS.filter(t => (!only || t.id === only) && !skipSet.has(t.id));
    for (let i = 0; i < list.length; i++) {
      console.log('==> ' + list[i].label);
      saveResult(await buildOne(list[i], PALETTES[list[i].id], 17 + i * 31));
    }
    // Persist the MeshFilter -> .asset references into the .unity file. Without
    // this the rebuilt meshes live on disk but the scene still points at the old
    // embedded objects until the user manually saves.
    await call('scene.save', { name: 'RA2Allied', path: 'Assets/RA2Allied/RA2Allied.unity' });
    console.log('[scene] saved Assets/RA2Allied/RA2Allied.unity');
    console.log('[done] wrote ' + OUT);
  } catch (e) {
    console.error('[FAILED] ' + (e && e.message ? e.message : String(e)));
    if (e && e.body) console.error('body: ' + String(e.body).slice(0, 600));
    process.exitCode = 1;
  }
})();

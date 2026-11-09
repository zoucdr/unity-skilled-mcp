/**
 * UI Spec —— 效果图 → Unity UGUI 的唯一真值源。
 *
 * 设计原则：
 *  1. 所有坐标只用一种表示：rect = [left, top, width, height]，效果图左上原点、像素。
 *     到 UGUI / UISourceGen / Sprite 裁切的三套换算全部收口在 coords.js，禁止别处手写。
 *  2. 所有控件只用一种表示：type 是抽象控件类型，到真实 UGUI 组件的映射收口在 controls.js。
 *  3. Spec 是纯数据，不含工具名、不含工程绝对路径，换工程/换工具集不用改 Spec。
 *  4. 每个不确定处必须显式登记到 uncertainties，不允许悄悄猜。
 *
 * 本文件不连 Unity，可离线跑（check.js 直接 require）。
 */
'use strict';

const SPEC_VERSION = '1.0';

/** 12 个锚点预设，与 ugui.tattoo 的 tattoo_preset 枚举逐字对齐（实测 schema）。 */
const ANCHOR_PRESETS = [
  'top_left', 'top_center', 'top_right',
  'middle_left', 'middle_center', 'middle_right',
  'bottom_left', 'bottom_center', 'bottom_right',
  'stretch_horizontal', 'stretch_vertical', 'stretch',
];

/** 图资源来源。crop = 从效果图按 rect 裁；regenerate = 交给 UISourceGen 重绘；external = 外部已有 PNG。 */
const ASSET_SOURCES = ['crop', 'regenerate', 'external', 'none'];

/** 控件状态，与 UISourceGen 的 UiState 对齐。 */
const STATES = ['normal', 'hover', 'pressed', 'disabled', 'selected', 'checked'];

const DEFAULTS = {
  anchor: 'middle_center',
  slice: [0, 0, 0, 0],
  asset: 'crop',
  states: ['normal'],
  confidence: 1,
  visible: true,
  interactive: false,
};

function fail(problems, msg, path) {
  problems.push(path ? `${path}: ${msg}` : msg);
}

function num(v) { return typeof v === 'number' && Number.isFinite(v); }
function rectOk(r) { return Array.isArray(r) && r.length === 4 && r.every(num); }
function colorOk(c) { return c === undefined || c === null || (typeof c === 'string' && /^#([0-9a-fA-F]{3,8})$/.test(c)); }

/**
 * 校验并归一化一份 UI Spec。
 * @returns {{ok:boolean, spec:object|null, problems:string[], warnings:string[]}}
 *  归一化会补齐默认值、建立 id→node 索引、把 children 展平成 parent 引用、算好 depth/order。
 */
function normalize(input, opts = {}) {
  const problems = [];
  const warnings = [];
  if (!input || typeof input !== 'object' || Array.isArray(input)) {
    return { ok: false, spec: null, problems: ['Spec 必须是 JSON 对象'], warnings };
  }
  if (input.spec_version && input.spec_version !== SPEC_VERSION) {
    fail(problems, `spec_version 期望 ${SPEC_VERSION}，收到 ${input.spec_version}`);
  }
  if (!input.name || typeof input.name !== 'string') fail(problems, 'name 必填且为字符串');

  const src = input.source || {};
  if (!src.width || !num(src.width) || src.width <= 0) fail(problems, 'source.width 必填且为正数');
  if (!src.height || !num(src.height) || src.height <= 0) fail(problems, 'source.height 必填且为正数');
  if (src.image !== undefined && typeof src.image !== 'string') fail(problems, 'source.image 必须是字符串路径');
  if (!src.image && (input.asset_strategy !== 'regenerate')) {
    warnings.push('source.image 缺省：只有 asset=regenerate / external 的节点能拿到贴图，crop 节点将失败');
  }

  const canvasIn = input.canvas || {};
  const refW = (canvasIn.reference_resolution && canvasIn.reference_resolution[0]) || src.width;
  const refH = (canvasIn.reference_resolution && canvasIn.reference_resolution[1]) || src.height;
  if (!num(refW) || !num(refH) || refW <= 0 || refH <= 0) fail(problems, 'canvas.reference_resolution 非法');
  if (refW !== src.width || refH !== src.height) {
    warnings.push(`效果图 ${src.width}x${src.height} 与参考分辨率 ${refW}x${refH} 不同，coords 会按比例缩放所有 rect`);
  }

  // 统一：先把嵌套 children 展平为 parent 引用
  const ids = new Set();
  if (Array.isArray(input.nodes)) {
    // 统一：先把嵌套 children 展平为 parent 引用
    const walk = (list, parentId, depth) => {
      if (!Array.isArray(list)) { fail(problems, `children 必须是数组 (${parentId})`); return; }
      list.forEach((n, i) => {
        if (!n || typeof n !== 'object') { fail(problems, '必须是对象', `${parentId}.children[${i}]`); return; }
        const p = `${n.id || `${parentId}.children[${i}]`}`;
        if (!n.id || typeof n.id !== 'string') fail(problems, 'id 必填且为字符串', p);
        else if (ids.has(n.id)) fail(problems, `id 重复：${n.id}`, p);
        else ids.add(n.id);
        const merged = Object.assign({}, DEFAULTS, n);
        merged.parent = parentId || n.parent || null;
        merged.depth = depth;
        merged.order = i;
        merged.children = undefined;
        outNodes.push(merged);
        if (n.children !== undefined) walk(n.children, n.id, depth + 1);
      });
    };
    var outNodes = [];
    walk(input.nodes, null, 0);
  } else {
    fail(problems, 'nodes 必填且为数组');
    var outNodes = [];
  }

  const idSet = new Set(outNodes.map(n => n.id));
  outNodes.forEach((n, i) => {
    const p = `nodes[${i}](${n.id})`;
    if (!rectOk(n.rect)) fail(problems, 'rect 必须是 [left,top,width,height] 四个有限数', p);
    else {
      if (n.rect[2] <= 0 || n.rect[3] <= 0) fail(problems, 'rect 宽高必须为正', p);
      if (n.rect[0] < 0 || n.rect[1] < 0) warnings.push(`${p}: rect 起点为负，裁切时会被 clamp`);
      if (n.rect[0] + n.rect[2] > src.width + 1 || n.rect[1] + n.rect[3] > src.height + 1) {
        warnings.push(`${p}: rect 超出效果图边界，裁切时会被 clamp`);
      }
    }
    if (typeof n.type !== 'string' || !n.type) fail(problems, 'type 必填', p);
    if (!ANCHOR_PRESETS.includes(n.anchor)) fail(problems, `anchor 必须是 ${ANCHOR_PRESETS.join('/')} 之一，收到 ${n.anchor}`, p);
    if (!ASSET_SOURCES.includes(n.asset)) fail(problems, `asset 必须是 ${ASSET_SOURCES.join('/')} 之一`, p);
    if (n.parent !== null && !idSet.has(n.parent)) fail(problems, `parent "${n.parent}" 不存在`, p);
    if (!Array.isArray(n.slice) || n.slice.length !== 4 || !n.slice.every(x => num(x) && x >= 0)) {
      fail(problems, 'slice 必须是 [l,t,r,b] 四个非负数', p);
    }
    if (!Array.isArray(n.states) || !n.states.every(s => STATES.includes(s))) {
      fail(problems, `states 必须是 ${STATES.join('/')} 的子集`, p);
    }
    if (!colorOk(n.color)) fail(problems, 'color 必须是 #RGB/#RRGGBB/#RRGGBBAA', p);
    if (!num(n.confidence) || n.confidence < 0 || n.confidence > 1) fail(problems, 'confidence 必须在 [0,1]', p);
    if (n.asset === 'external' && !n.asset_path) fail(problems, 'asset=external 时必须给 asset_path', p);
    if (n.confidence < 0.6) warnings.push(`${p}: 置信度 ${n.confidence} 偏低，建议人工复核`);
  });

  // 层级成环检测
  const byId = Object.fromEntries(outNodes.map(n => [n.id, n]));
  outNodes.forEach(n => {
    const seen = new Set([n.id]);
    let cur = n.parent;
    while (cur) {
      if (seen.has(cur)) { fail(problems, `parent 成环：${n.id}`, `nodes(${n.id})`); break; }
      seen.add(cur);
      cur = byId[cur] ? byId[cur].parent : null;
    }
  });

  const spec = {
    spec_version: SPEC_VERSION,
    name: input.name,
    source: {
      image: src.image || '',
      width: src.width,
      height: src.height,
      note: src.note || '',
    },
    canvas: {
      reference_resolution: [refW, refH],
      scale_mode: canvasIn.scale_mode || 'ScaleWithScreenSize',
      match: num(canvasIn.match) ? canvasIn.match : 0.5,
      root: canvasIn.root || 'Canvas',
      sorting_order: num(canvasIn.sorting_order) ? canvasIn.sorting_order : 0,
    },
    palette: Object.assign({
      primary: '#4C8DFF', surface: '#1E2433', on_surface: '#EEF2FF',
      border: '#FFFFFF22', radius: 12, font_size: 24,
    }, input.palette || {}),
    nodes: outNodes,
    uncertainties: Array.isArray(input.uncertainties) ? input.uncertainties : [],
  };

  // 控件类型合法性交给 controls.js（那里才是唯一映射表），此处只做存在性提示
  if (opts.requireKnownTypes !== false) {
    const { isKnown } = require('./controls');
    outNodes.forEach(n => {
      if (!isKnown(n.type)) fail(problems, `未知控件类型 "${n.type}"（见 controls.js 映射表）`, `nodes(${n.id})`);
    });
  }

  return { ok: problems.length === 0, spec: problems.length ? null : spec, problems, warnings };
}

/** 读文件并归一化；路径相对工程根时由调用方解析。 */
function load(file) {
  const fs = require('fs');
  const raw = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
  return normalize(raw);
}

/** 生成一份最小可跑的 Spec 骨架，供大模型对照填写。 */
function skeleton(name, width, height) {
  return {
    spec_version: SPEC_VERSION,
    name: name || 'UntitledUI',
    source: { image: 'Assets/AI-Generate/UGUI/<name>/reference.png', width, height },
    canvas: { reference_resolution: [width, height], root: 'Canvas' },
    palette: { primary: '#4C8DFF', surface: '#1E2433', on_surface: '#EEF2FF', radius: 12 },
    nodes: [],
    uncertainties: [],
  };
}

module.exports = {
  SPEC_VERSION, ANCHOR_PRESETS, ASSET_SOURCES, STATES,
  normalize, load, skeleton,
};

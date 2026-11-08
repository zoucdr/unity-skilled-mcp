'use strict';
/**
 * tex.js — texgen 贴图封装（建模流水线的第二半）。
 *
 * 这个文件回答三个问题：
 *   1) 贴图怎么和"单 mesh + 单图集"对上？   → atlasRectToLayer()
 *   2) 无缝材质怎么做？                     → pbr()
 *   3) 画完怎么确认没画错？                 → inspect.js
 *
 * 【关键语义，均来自源码 + 实测，不是推断】
 *
 *   A. for_mesh 的 regions 是 [{ id, layers }]，id 对应 UV 分区编号。
 *      传全局 layers 时，所有分区共用**同一张画布**（只渲染一次，会被缓存）；
 *      传 regions 覆盖时，每个分区单独渲染一张**整幅尺寸**的画布，
 *      再按该分区的 UV 遮罩取样。所以 layer 坐标永远是整图坐标，
 *      不是"相对分区"的坐标 —— 这点极易误解。
 *
 *   B. texgen.for_mesh / uv_atlas 返回的 rect 是 [x0, y0, x1, y1]，
 *      **y 直接就是 UV 的 v**（v=0 在底边）。不要再做 1-v。
 *      实测证据：columns=2,rows=2 时 row0 的 y 是 0.04..0.46（低），
 *      row1 是 0.54..0.96（高）。
 *
 *   C. 渐变写纯颜色数组（["#000","#fff"]）会**静默失效**，整层变白。
 *      必须用 [{offset,color}] / [[0,"#000"]] / {"0":"#000"} 三种之一。
 *
 *   D. linear 渐变的投影长度永远取 size.x，方向由 rotation 决定（0=从左到右）。
 *      想要竖直渐变，rotation=90 且 size 应接近正方形。
 */

const path = require('path');
const mcp = require('./mcp');
const UV = require('./uvspace');

function configure(opts) { return mcp.configure(opts); }

// ---------------------------------------------------------------- 画布

/**
 * 通用画布渲染（不涉及 mesh）。
 * @param {object} o { width, height, layers, background, tiling, outputPath, ... }
 */
async function render(o) {
  const opt = o || {};
  if (!Array.isArray(opt.layers) || opt.layers.length === 0) {
    throw new Error('render 需要非空 layers 数组');
  }
  const args = Object.assign({
    width: opt.width || 512,
    height: opt.height || 512,
    background: opt.background === undefined ? '#00000000' : opt.background,
    origin: opt.origin || 'topLeft',
    supersample: opt.supersample === undefined ? 2 : opt.supersample,
    overwrite: opt.overwrite !== false,
    layers: opt.layers,
  }, opt.extra || {});
  if (opt.outputPath) args.output_path = opt.outputPath;
  if (opt.tiling !== undefined) args.tiling = opt.tiling;
  if (opt.textureType) args.texture_type = opt.textureType;
  if (opt.wrapMode) args.wrap_mode = opt.wrapMode;
  if (opt.filterMode) args.filter_mode = opt.filterMode;
  return await mcp.call('texgen.render', args);
}

/** 一次性派生整套 PBR：BaseColor / Height / Normal / Roughness / AO / ORM。 */
async function pbr(o) {
  const opt = o || {};
  if (!Array.isArray(opt.layers) || opt.layers.length === 0) {
    throw new Error('pbr 需要非空 layers 数组');
  }
  const args = {
    width: opt.width || 512,
    height: opt.height || 512,
    layers: opt.layers,
    output_dir: opt.outputDir || 'Assets/Textures',
    prefix: opt.prefix || 'Mat',
    tiling: opt.tiling !== false,
    normal_strength: opt.normalStrength === undefined ? 5 : opt.normalStrength,
    metallic: opt.metallic === undefined ? 0 : opt.metallic,
    supersample: opt.supersample === undefined ? 2 : opt.supersample,
    overwrite: opt.overwrite !== false,
  };
  if (opt.origin) args.origin = opt.origin;
  if (opt.background !== undefined) args.background = opt.background;
  return await mcp.call('texgen.pbr', args);
}

// ---------------------------------------------------------------- mesh 贴图

/**
 * 把图集 cell 的 UV 矩形换成 texgen layer 的 center/size。
 *
 * @param {object} cell  uv_atlas 返回的 cells[i]（rect 是 [x0,y0,x1,y1]）
 * @param {number} W     画布宽
 * @param {number} H     画布高
 * @param {object} extra 额外的 layer 字段，如 { fill:'solid', color:'#ccc' }
 */
function atlasRectToLayer(cell, W, H, extra) {
  if (!cell || !Array.isArray(cell.rect) || cell.rect.length !== 4) {
    throw new Error('atlasRectToLayer 需要 cell.rect = [x0,y0,x1,y1]');
  }
  const [x0, y0, x1, y1] = cell.rect;
  const r = UV.rectUV(x0, y0, x1, y1, W, H);
  return UV.layerRect(r, extra);
}

/** 同上，但直接用 face_rects（部件内部每个轴向面的矩形）。 */
function faceRectToLayer(faceRect, W, H, extra) {
  if (!faceRect || !Array.isArray(faceRect.rect) || faceRect.rect.length !== 4) {
    throw new Error('faceRectToLayer 需要 faceRect.rect = [x0,y0,x1,y1]');
  }
  const [x0, y0, x1, y1] = faceRect.rect;
  const r = UV.rectUV(x0, y0, x1, y1, W, H);
  return UV.layerRect(r, extra);
}

/**
 * 给 uv_atlas 的每个 cell 生成一个彩色方块层，用于"贴图对齐自检"。
 * 每个 cell 一个醒目颜色，画完看一眼就知道 UV 和贴图是否对得上。
 */
function debugAtlasLayers(atlasData, W, H, opts) {
  const o = opts || {};
  const palette = o.palette || [
    '#e74c3c', '#3498db', '#2ecc71', '#f1c40f', '#9b59b6',
    '#1abc9c', '#e67e22', '#34495e', '#e91e63', '#00bcd4',
    '#8bc34a', '#ff5722', '#795548', '#607d8b', '#cddc39', '#ffc107',
  ];
  const cells = (atlasData && atlasData.cells) || [];
  return cells.map((c, i) => atlasRectToLayer(c, W, H, {
    shape: 'rect',
    fill: 'solid',
    color: palette[i % palette.length],
  }));
}

/**
 * 按 mesh 的 UV 分区绘制贴图。
 *
 * @param {object} o
 *   o.meshPath      必填，Assets/ 下的 mesh 资源路径
 *   o.layers        全局指令表（所有分区共用一张画布）
 *   o.regions       [{ id, layers }] 按分区覆盖，可选
 *   o.groupBy       'submesh'（默认）| 'island'
 *   o.width/height  画布尺寸
 *   o.outputPath    PNG 输出路径
 *   o.materialPath  可选，画完直接挂到材质
 *   o.textureProperty 默认 _MainTex
 */
async function forMesh(o) {
  const opt = o || {};
  if (!opt.meshPath) throw new Error('forMesh 需要 meshPath');
  if (!Array.isArray(opt.layers) && !Array.isArray(opt.regions)) {
    throw new Error('forMesh 需要 layers 或 regions 之一');
  }
  const args = {
    mesh_path: opt.meshPath,
    width: opt.width || 1024,
    height: opt.height || 1024,
    group_by: opt.groupBy || 'submesh',
    padding: opt.padding === undefined ? 4 : opt.padding,
    overwrite: opt.overwrite !== false,
    supersample: opt.supersample === undefined ? 2 : opt.supersample,
  };
  if (Array.isArray(opt.layers)) args.layers = opt.layers;
  if (Array.isArray(opt.regions)) args.regions = opt.regions;
  if (opt.outputPath) args.output_path = opt.outputPath;
  if (opt.background !== undefined) args.background = opt.background;
  if (opt.origin) args.origin = opt.origin;
  if (opt.fillMode) args.fill_mode = opt.fillMode;
  if (opt.materialPath) args.material_path = opt.materialPath;
  if (opt.textureProperty) args.texture_property = opt.textureProperty;
  if (opt.textureType) args.texture_type = opt.textureType;
  if (opt.wrapMode) args.wrap_mode = opt.wrapMode;
  if (opt.filterMode) args.filter_mode = opt.filterMode;
  return await mcp.call('texgen.for_mesh', args);
}

/** 查看 mesh 的 UV 布局（画之前的必备动作）。 */
async function uvInfo(meshPath, o) {
  const opt = o || {};
  if (!meshPath) throw new Error('uvInfo 需要 meshPath');
  return await mcp.call('texgen.uv_info', {
    mesh_path: meshPath,
    width: opt.width || 512,
    height: opt.height || 512,
    group_by: opt.groupBy || 'submesh',
  });
}

/** 校验指令表（不渲染，先查语法错误）。 */
async function validate(layers, o) {
  const opt = o || {};
  return await mcp.call('texgen.validate', {
    layers: layers,
    width: opt.width || 512,
    height: opt.height || 512,
  });
}

// ---------------------------------------------------------------- 材质

/**
 * 建材质并挂贴图。**幂等**：材质已存在时复用而不是报错，
 * 这样同一个脚本可以反复跑（实测 material.create 对已存在路径直接失败）。
 */
async function material(o) {
  const opt = o || {};
  if (!opt.path) throw new Error('material 需要 path（Assets/.../M_X.mat）');
  let created = false;
  try {
    await mcp.call('material.create', { path: opt.path });
    created = true;
  } catch (e) {
    if (!/already exists/i.test(e.message)) throw e;
    // 已存在 → 复用
  }
  const props = {};
  if (opt.mainTex) props._MainTex = opt.mainTex;
  if (opt.normalMap) props._BumpMap = opt.normalMap;
  if (opt.metallic !== undefined) props._Metallic = opt.metallic;
  if (opt.glossiness !== undefined) props._Glossiness = opt.glossiness;
  if (opt.color) props._Color = opt.color;
  if (Object.keys(props).length) {
    await mcp.call('material.set_properties', { path: opt.path, properties: props });
  }
  if (opt.normalMap) {
    try { await mcp.call('material.enable_keyword', { path: opt.path, keyword: '_NORMALMAP' }); } catch (e) { /* 关键字可失败 */ }
  }
  return { path: opt.path, created: created, reused: !created, properties: props };
}

/**
 * 把材质挂到场景对象上。
 *
 * 【实测坑】uv_atlas 会重建 Mesh 并把 renderer 重置为 ProBuilderDefault，
 * 所以挂材质的动作**必须放在 UV 处理之后**，否则会被覆盖回默认材质。
 * 另外 `mr.sharedMaterial = mat` 光改内存还不够，要 SetDirty + 标脏场景，
 * 不然重新加载场景就丢了。
 */
async function assignMaterial(objectName, materialPath, o) {
  const opt = o || {};
  if (!objectName) throw new Error('assignMaterial 需要对象名');
  if (!materialPath) throw new Error('assignMaterial 需要材质路径');
  const code = `
GameObject obj = GameObject.Find(${JSON.stringify(objectName)});
if (obj == null) { Debug.LogError("ASSIGN: 找不到对象 ${objectName}"); }
else {
    Material mt = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(${JSON.stringify(materialPath)});
    if (mt == null) { Debug.LogError("ASSIGN: 材质加载失败 ${materialPath}"); }
    else {
        MeshRenderer rend = obj.GetComponent<MeshRenderer>();
        if (rend == null) { Debug.LogError("ASSIGN: 没有 MeshRenderer"); }
        else {
            rend.sharedMaterial = mt;
            UnityEditor.EditorUtility.SetDirty(obj);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            Texture tx = mt.mainTexture;
            Debug.Log("ASSIGN: OK mats=" + rend.sharedMaterials.Length + " mat=" + mt.name + " tex=" + (tx == null ? "none" : tx.name));
        }
    }
}
`;
  const r = await mcp.call('code.execute', { mode: 'runtime', code, description: 'assign material' });
  const logs = (r && r.logs) || '';
  if (/ASSIGN: OK/.test(logs)) return { ok: true, logs: logs.trim() };
  // 失败时不要静默：把 Unity 的日志原样带出来
  const err = new Error('挂材质失败：' + (logs.trim() || JSON.stringify(r).slice(0, 300)));
  err.logs = logs;
  throw err;
}

module.exports = {
  configure, render, pbr, forMesh, uvInfo, validate, material, assignMaterial,
  atlasRectToLayer, faceRectToLayer, debugAtlasLayers,
};

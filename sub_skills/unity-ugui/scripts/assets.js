/**
 * 图资源阶段 —— 统一管理三种来源，产出同一份 assets_manifest.json。
 *
 *  crop        保真/降级：离线从效果图按 rect 裁切（slice.js）
 *  regenerate  首选：交给内置 uisourcegen.render 生成控件图 + 读 layout.json 取 slice/states
 *  external    直接引用已有 PNG
 *
 * 然后 importSprites() 通过 code.execute 把 PNG 在 Unity 里设成 Sprite，
 * 并按 slice [l,t,r,b] 写入 TextureImporter.spriteBorder（九宫格）。
 * 这是关键补强：UISourceGen 只在 layout.json 里写 slice 元数据，
 * 从不写 TextureImporter，不导入就永远是「有图没九宫格」。
 */
'use strict';

const fs = require('fs');
const path = require('path');
const mcp = require('./mcp');
const slice = require('./slice');
const controls = require('./controls');

/** 从效果图裁切（位图保真或内置图资源工具缺失时的降级路径）。 */
function fromReference(spec, o) {
  return slice.slice(spec, Object.assign({}, o, { onlySources: ['crop'] }));
}

/** 调 UISourceGen 重绘；返回统一 manifest（不信任 layout.json 里的 image 路径，见 semantics.md 坑 9）。 */
async function regenerate(spec, o = {}) {
  const project = o.project || mcp.resolveProject();
  const outDirRel = o.outputDirRel || `Assets/AI-Generate/UGUI/${spec.name}`;
  const baseName = o.baseName || spec.name;
  const screen = controls.toScreen(spec);
  const res = await mcp.call('uisourcegen.render', {
    screen,
    output_dir: outDirRel,
    base_name: baseName,
    preview: true,
    supersample: o.supersample || 2,
    select_asset: false,
  });

  const folderRel = `${outDirRel}/${baseName}`;
  const folderAbs = mcp.resolveOut(folderRel, project);
  const layoutFile = path.join(folderAbs, 'layout.json');
  const problems = [], warnings = [], items = [];
  if (!fs.existsSync(layoutFile)) {
    return { ok: false, manifest: null, warnings, problems: [`uisourcegen.render 未产出 layout.json：${layoutFile}`, `返回=${JSON.stringify(res).slice(0, 400)}`] };
  }
  const layout = JSON.parse(fs.readFileSync(layoutFile, 'utf8').replace(/^\uFEFF/, ''));
  for (const c of (layout.controls || [])) {
    const normal = path.join(folderAbs, `${c.id}_normal.png`);
    if (!fs.existsSync(normal)) { problems.push(`${c.id}: 缺 normal 图 ${normal}`); continue; }
    const states = {};
    for (const [st, rel] of Object.entries(c.states || {})) {
      const f = path.join(folderAbs, `${c.id}_${st}.png`);
      states[st] = fs.existsSync(f) ? f : normal;
    }
    if (!states.normal) states.normal = normal;
    items.push({
      id: c.id, path: normal, source: 'regenerate',
      rect: c.rect, specRect: c.rect,
      slice: c.slice || [0, 0, 0, 0], states,
    });
  }
  const manifest = {
    generator: 'unity-ugui/regenerate', version: '1.0', screen: spec.name,
    layout_file: layoutFile, folder: folderRel, produced_at: new Date().toISOString(), items,
  };
  const mf = path.join(folderAbs, 'assets_manifest.json');
  fs.writeFileSync(mf, JSON.stringify(manifest, null, 2));
  return { ok: problems.length === 0, manifest, manifestFile: mf, warnings, problems };
}

/**
 * 把 manifest 里的 PNG 在 Unity 里导入成 Sprite，并写九宫格 border。
 * 全部走 code.execute 语句片段；C#5 兼容（无字符串插值/局部函数/?./nameof），
 * 数据用 C# 字面量注入，不依赖 SimpleJson（实测工程里没有）。
 */
/** 纯函数：生成导入 C#（供 check.js 离线 lint，不连 Unity）。 */
function generateImportCSharp(relBorders) {
  const lines = [];
  lines.push('System.Action<string, UnityEngine.Vector4> __importSprite = (rel, border) => {');
  lines.push('  var imp = UnityEditor.AssetImporter.GetAtPath(rel) as UnityEditor.TextureImporter;');
  lines.push('  if (imp != null) {');
  lines.push('    imp.textureType = UnityEditor.TextureImporterType.Sprite;');
  lines.push('    imp.spriteImportMode = UnityEditor.SpriteImportMode.Single;');
  lines.push('    imp.alphaIsTransparency = true;');
  lines.push('    imp.mipmapEnabled = false;');
  lines.push('    imp.filterMode = UnityEngine.FilterMode.Bilinear;');
  lines.push('    imp.textureCompression = UnityEditor.TextureImporterCompression.Uncompressed;');
  lines.push('    imp.spriteBorder = border;');
  lines.push('    imp.SaveAndReimport();');
  lines.push('    UnityEngine.Debug.Log("[unity-ugui] sprite ready: " + rel);');
  lines.push('  } else { UnityEngine.Debug.LogWarning("[unity-ugui] 找不到 TextureImporter: " + rel); }');
  lines.push('};');
  for (const [rel, b] of relBorders) {
    lines.push(`__importSprite("${rel.replace(/\\/g, '/').replace(/"/g, '\\"')}", new UnityEngine.Vector4(${b[0].toFixed(2)}f, ${b[1].toFixed(2)}f, ${b[2].toFixed(2)}f, ${b[3].toFixed(2)}f));`);
  }
  lines.push('UnityEditor.AssetDatabase.SaveAssets();');
  lines.push(`UnityEngine.Debug.Log("[unity-ugui] imported sprites: ${relBorders.length}");`);
  return lines.join('\n');
}

async function importSprites(manifest, o = {}) {
  const project = o.project || mcp.resolveProject();
  const relBorders = [];
  for (const it of manifest.items) {
    // 只导入工程 Assets 内的文件；外部绝对路径先拷进输出目录
    let rel = toAssetRel(it.path, project);
    if (!rel) {
      if (!o.assetDirRel) continue;
      const dest = path.join(mcp.resolveOut(o.assetDirRel, project), `${slice.safeId(it.id)}.png`);
      fs.mkdirSync(path.dirname(dest), { recursive: true });
      fs.copyFileSync(it.path, dest);
      rel = toAssetRel(dest, project);
    }
    if (!rel) continue;
    relBorders.push([rel, it.slice || [0, 0, 0, 0]]);
  }
  const code = generateImportCSharp(relBorders);
  mcp.assertLint(code);
  const r = await mcp.call('code.execute', {
    mode: 'runtime', code,
    description: `Import ${relBorders.length} UI sprites with 9-slice borders`,
    includes: ['UnityEditor', 'UnityEngine', 'System.IO'],
    return_output: true,
    timeout: 120,
  });
  return { ok: true, count: relBorders.length, result: r, code };
}

/** 绝对/相对路径 → Unity 的 Assets/... 相对路径；不在工程内返回 null。 */
function toAssetRel(file, project) {
  const abs = path.isAbsolute(file) ? path.resolve(file) : path.resolve(project, file);
  const root = path.resolve(project);
  const r = path.relative(root, abs).replace(/\\/g, '/');
  if (r.startsWith('..') || !r.startsWith('Assets/')) return null;
  return r;
}

module.exports = { fromReference, regenerate, generateImportCSharp, importSprites, toAssetRel };

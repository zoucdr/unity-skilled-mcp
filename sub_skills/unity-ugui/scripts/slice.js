/**
 * 切图（离线，零依赖）—— 从效果图按 UI Spec 的 rect 裁出每个控件的独立 PNG。
 *
 * 为什么需要这一步：
 *  demo/Assets/Extends/CustomTools/UISourceGen 只能「按 screen JSON 重绘控件图」，
 *  全目录没有 TextureImporter / SpriteMetaData / 大图切片代码（实测），
 *  也就是说「给一张效果图 → 把里面的按钮/面板切出来」这条能力原本不存在。
 *  本文件补上它：Node 侧直接解码 PNG、按 rect 裁切、写 RGBA PNG，
 *  再由 assets.js 导入 Unity 并设置九宫格 spriteBorder。
 *
 * 裁切矩形一律走 coords.toCropRect()，不在这里重算坐标。
 */
'use strict';

const fs = require('fs');
const path = require('path');
const PNG = require('./png');
const coords = require('./coords');
const controls = require('./controls');

function safeId(s) { return String(s).replace(/[^A-Za-z0-9_\-.]/g, '_'); }

/** 把 PNG 解码结果的任意通道数归一成 RGBA Buffer。 */
function toRGBA(img) {
  const { w, h, channels, data } = img;
  const out = Buffer.alloc(w * h * 4);
  for (let i = 0; i < w * h; i++) {
    const s = i * channels, d = i * 4;
    if (channels === 1) { out[d] = out[d + 1] = out[d + 2] = data[s]; out[d + 3] = 255; }
    else if (channels === 2) { out[d] = out[d + 1] = out[d + 2] = data[s]; out[d + 3] = data[s + 1]; }
    else if (channels === 3) { out[d] = data[s]; out[d + 1] = data[s + 1]; out[d + 2] = data[s + 2]; out[d + 3] = 255; }
    else { out[d] = data[s]; out[d + 1] = data[s + 1]; out[d + 2] = data[s + 2]; out[d + 3] = data[s + 3]; }
  }
  return out;
}

function crop(img, r) {
  const src = toRGBA(img);
  const out = Buffer.alloc(r.w * r.h * 4);
  for (let y = 0; y < r.h; y++) {
    for (let x = 0; x < r.w; x++) {
      const s = ((r.y + y) * img.w + (r.x + x)) * 4;
      const d = (y * r.w + x) * 4;
      out[d] = src[s]; out[d + 1] = src[s + 1]; out[d + 2] = src[s + 2]; out[d + 3] = src[s + 3];
    }
  }
  return out;
}

/**
 * 按 Spec 裁切。
 * @param {object} spec 归一化后的 UI Spec
 * @param {object} o { imageFile, outDir, overwrite, onlySources }
 * @returns {{ok:boolean, manifest:object, warnings:string[], problems:string[]}}
 */
function slice(spec, o = {}) {
  const problems = [], warnings = [];
  const outDir = o.outDir;
  if (!outDir) throw new Error('slice: outDir 必填');
  fs.mkdirSync(outDir, { recursive: true });

  const imageFile = o.imageFile || spec.source.image;
  if (!imageFile || !fs.existsSync(imageFile)) {
    return { ok: false, manifest: null, warnings, problems: [`效果图不存在：${imageFile}`] };
  }
  const img = PNG.readPNG(imageFile);
  if (img.w !== spec.source.width || img.h !== spec.source.height) {
    warnings.push(`效果图实际 ${img.w}x${img.h} 与 spec.source ${spec.source.width}x${spec.source.height} 不一致，按实际尺寸裁切；请校正 Spec`);
  }

  const only = o.onlySources || ['crop'];
  const items = [];
  for (const node of spec.nodes) {
    const d = controls.def(node.type);
    if (!d || !d.needsSprite) continue;
    if (!only.includes(node.asset)) {
      if (node.asset === 'external' && node.asset_path) {
        if (!fs.existsSync(node.asset_path)) problems.push(`${node.id}: external 资源不存在 ${node.asset_path}`);
        else items.push({ id: node.id, path: node.asset_path, source: 'external', rect: node.rect, slice: node.slice, states: { normal: node.asset_path } });
      }
      continue;
    }
    const r = coords.toCropRect(node, spec);
    if (r.w <= 0 || r.h <= 0) { problems.push(`${node.id}: 裁切矩形为空 ${JSON.stringify(r)}`); continue; }
    if (r.clipped) warnings.push(`${node.id}: rect 超出效果图边界，已 clamp 到 ${r.w}x${r.h}`);
    const file = path.join(outDir, `${safeId(node.id)}.png`);
    if (fs.existsSync(file) && !o.overwrite) { /* 幂等：复用已有文件 */ }
    else PNG.writePNG(file, r.w, r.h, crop(img, r), { alpha: true });
    items.push({
      id: node.id, path: file, source: 'crop',
      rect: [r.x, r.y, r.w, r.h], specRect: node.rect,
      slice: node.slice, states: { normal: file },
    });
  }

  const manifest = {
    generator: 'unity-ugui/slice',
    version: '1.0',
    screen: spec.name,
    reference_image: imageFile,
    image_size: [img.w, img.h],
    produced_at: new Date().toISOString(),
    items,
  };
  const mf = path.join(outDir, 'assets_manifest.json');
  fs.writeFileSync(mf, JSON.stringify(manifest, null, 2));

  return { ok: problems.length === 0, manifest, manifestFile: mf, warnings, problems };
}

/** 只读检查：产物是否齐全、是否非纯色（复用 inspect.analyze）。 */
function audit(manifest) {
  const inspect = require('./inspect');
  const out = { ok: true, items: [], problems: [] };
  for (const it of manifest.items) {
    if (!fs.existsSync(it.path)) { out.ok = false; out.problems.push(`缺文件 ${it.id}: ${it.path}`); continue; }
    let flat = null;
    // inspect.analyze 的 opt 没有默认值，不传 {} 会在 opt.normal 处抛 TypeError（复用底座实测坑）
    try { flat = inspect.analyze(PNG.readPNG(it.path), {}).flat; } catch (e) { /* 解码失败不致命，交给 Unity 导入器 */ }
    out.items.push({ id: it.id, bytes: fs.statSync(it.path).size, flat });
    if (flat === true) out.problems.push(`${it.id}: 切出的图是纯色，可能 rect 落在空白处`);
  }
  return out;
}

module.exports = { safeId, toRGBA, crop, slice, audit };

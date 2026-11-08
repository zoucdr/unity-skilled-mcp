'use strict';
/**
 * check.js — 技能自检。改动任何脚本后先跑一次。
 *   node check.js
 *
 * 只做本地断言，不连 Unity，秒级完成。
 */
const fs = require('fs');
const path = require('path');
const UV = require('./uvspace');
const { Field, gradientToNormal } = require('./field');
const PNG = require('./png');
const tx = require('./tx');
const inspect = require('./inspect');

let pass = 0, fail = 0;
const fails = [];
function ok(name, cond, detail) {
  if (cond) { pass++; console.log(`  ok   ${name}${detail ? '  — ' + detail : ''}`); }
  else { fail++; fails.push(name + (detail ? ' :: ' + detail : '')); console.log(`  FAIL ${name}${detail ? '  — ' + detail : ''}`); }
}
function throws(name, fn, match) {
  try { fn(); ok(name, false, '本应抛错'); }
  catch (e) {
    const m = match ? new RegExp(match).test(e.message) : true;
    ok(name, m, m ? e.message.split('\n')[0].slice(0, 80) : '信息不含 ' + match);
  }
}
function sec(t) { console.log('\n=== ' + t + ' ==='); }

// ---------------------------------------------------------- 1. 坐标
sec('1. 坐标语义（须与 references/calibration.json 一致）');
{
  const W = 1024, H = 1024;
  const calibPath = path.join(__dirname, '..', 'references', 'calibration.json');
  let calib = null;
  if (fs.existsSync(calibPath)) {
    calib = JSON.parse(fs.readFileSync(calibPath, 'utf8'));
    ok('标定结论：画布 y 向上', calib.y_axis === 'up', 'y_axis=' + calib.y_axis);
    ok('标定结论：画布 x 向右', calib.x_axis === 'right', 'x_axis=' + calib.x_axis);
  } else {
    ok('存在 calibration.json', false, '请先运行 node calibrate.js');
  }

  ok('canvas v=0 -> y=0', UV.uvToPx(0, 0, W, H, UV.SPACE.CANVAS).y === 0);
  ok('canvas v=1 -> y=H', UV.uvToPx(0, 1, W, H, UV.SPACE.CANVAS).y === H);
  ok('image v=1 -> row=0', UV.uvToPx(0, 1, W, H, UV.SPACE.IMAGE).y === 0);
  ok('image v=0 -> row=H', UV.uvToPx(0, 0, W, H, UV.SPACE.IMAGE).y === H);

  const r = UV.rectUV(0.0, 0.25, 1.0, 0.75, 100, 300);
  ok('canvas cy 与 v 同向', r.cy === 150, `cy=${r.cy}`);
  ok('canvas h 取 v 跨度', r.h === 150, `h=${r.h}`);
  ok('image top/bottom 互为镜像', r.top === 75 && r.bottom === 225, `top=${r.top} bottom=${r.bottom}`);
  ok('canvas 与 image 的 y 相加等于 H', r.cy + (r.top + r.bottom) / 2 === 300);

  const c = UV.forOrigin(r, 'center', 100, 300);
  ok('origin=center 只平移', c.cy === r.cy - 150 && c.h === r.h, `cy ${r.cy} -> ${c.cy}`);
  throws('未知 origin 抛错', () => UV.forOrigin(r, 'bogus', 100, 300), '未知 origin');

  ok('layerRect 返回 center/size 数组', (() => {
    const L = UV.layerRect(r);
    return Array.isArray(L.center) && L.center.length === 2 && Array.isArray(L.size);
  })());
}

// ---------------------------------------------------------- 2. Field
sec('2. Field 把静默失败变硬失败');
{
  const raw = new Float32Array(16);
  raw[3.5] = 1;
  ok('证据：浮点索引静默丢弃', raw[3] === 0 && raw[4] === 0);

  const f = new Field(8, 8);
  throws('非整数索引抛错', () => f.set(1.5, 0, 1), '整数');
  throws('越界抛错', () => f.set(8, 0, 1), '越界');
  throws('NaN 抛错', () => f.set(0, 0, NaN), '有限数');

  const flat = new Field(8, 8);
  throws('平面场被拦下', () => flat.assertNotFlat('t'), '起伏');

  const h = new Field(32, 32);
  for (let y = 0; y < 32; y++) for (let x = 0; x < 32; x++) h.set(x, y, Math.sin(x / 5) * 0.01);
  ok('有起伏的场通过', h.assertNotFlat('t').dev > 1e-3);

  const n = gradientToNormal(h, 256, 1);
  let maxErr = 0;
  for (let y = 0; y < 32; y++) for (let x = 0; x < 32; x++) {
    maxErr = Math.max(maxErr, Math.abs(Math.hypot(n.nx.get(x, y), n.ny.get(x, y), n.nz.get(x, y)) - 1));
  }
  ok('法线归一化', maxErr < 1e-5, 'maxErr=' + maxErr.toExponential(2));
}

// ---------------------------------------------------------- 3. PNG
sec('3. PNG 解码（含 filter 1-4）');
{
  const w = 17, h = 13;
  const pix = Buffer.alloc(w * h * 3);
  for (let i = 0; i < pix.length; i++) pix[i] = (i * 53) & 0xff;
  const dec = PNG.decodePNG(PNG.encodePNG(w, h, pix));
  ok('编码往返一致', dec.w === w && dec.h === h && dec.data.equals(pix));

  // 用工作区里真实的 texgen 输出验证（若存在）
  const sample = path.join(__dirname, '..', 'references', 'samples', 'texgen_rgba.png');
  if (fs.existsSync(sample)) {
    const img = PNG.readPNG(sample);
    const d = PNG.neighborDelta(img);
    ok('真实 texgen RGBA 图解码正常', d < 40, `相邻差 ${d.toFixed(2)}`);
  }
}

// ---------------------------------------------------------- 4. tx 预检
sec('4. tx 参数预检（不发请求就能拦）');
{
  throws('尺寸超上限被拦', () => tx.precheck('render', { width: 4096, height: 512 }), '上限|超出');
  throws('坐标写成字符串被拦', () => tx.precheck('render', {
    layers: [{ shape: 'rect', center: ['128', '128'], size: [10, 10] }],
  }), '必须是');
  throws('坐标只有一个分量被拦', () => tx.precheck('render', {
    layers: [{ shape: 'rect', center: [128], size: [10, 10] }],
  }), '必须是');
  throws('layers_json 非法 JSON 被拦', () => tx.precheck('render', { layers_json: '{oops' }), 'JSON');
  ok('合法参数放行', (() => {
    tx.precheck('render', { width: 512, height: 512, layers: [{ shape: 'rect', center: [10, 20], size: [5, 5] }] });
    return true;
  })());
  ok('region 内坐标也被检查', (() => {
    try {
      tx.precheck('for_mesh', { regions: [{ id: 0, layers: [{ center: [1, 'x'] }] }] });
      return false;
    } catch (e) { return /regions\[0\]/.test(e.message); }
  })());
}

// ---------------------------------------------------------- 5. inspect
sec('5. inspect 指标');
{
  // 纯色图应被判为 flat
  const flatBuf = Buffer.alloc(32 * 32 * 3, 128);
  const flatImg = PNG.decodePNG(PNG.encodePNG(32, 32, flatBuf));
  const fr = inspect.analyze(flatImg, {});
  ok('纯色图被判为平', fr.flat === true);

  // 棋盘格应有梯度且声明接缝
  const bw = 32;
  const chess = Buffer.alloc(bw * bw * 3);
  for (let y = 0; y < bw; y++) for (let x = 0; x < bw; x++) {
    const v = ((x >> 2) + (y >> 2)) % 2 ? 240 : 20;
    const i = (y * bw + x) * 3;
    chess[i] = chess[i + 1] = chess[i + 2] = v;
  }
  const cr = inspect.analyze(PNG.decodePNG(PNG.encodePNG(bw, bw, chess)), {});
  ok('棋盘图有梯度', cr.gradient.horizontal > 5, 'gx=' + cr.gradient.horizontal);
  ok('棋盘图非平', cr.flat === false);

  // 平法线图（128,128,255）应报 xy 偏移过小
  const nrm = Buffer.alloc(16 * 16 * 3);
  for (let i = 0; i < 16 * 16; i++) { nrm[i * 3] = 128; nrm[i * 3 + 1] = 128; nrm[i * 3 + 2] = 255; }
  const nr = inspect.analyze(PNG.decodePNG(PNG.encodePNG(16, 16, nrm)), { normal: true });
  ok('平法线被识别', nr.warnings.some(x => /几乎没有偏移/.test(x)), JSON.stringify(nr.normal));

  // 平铺预览
  const tileSrc = PNG.decodePNG(PNG.encodePNG(8, 8, Buffer.alloc(8 * 8 * 3, 200)));
  const tiled = inspect.tilePreview(tileSrc, 3);
  ok('tilePreview 尺寸 = 原图 x times', tiled.w === 24 && tiled.h === 24, `${tiled.w}x${tiled.h}`);
  ok('tilePreview 保留通道数', tiled.channels === 3);
}

console.log('\n' + '='.repeat(56));
console.log(`通过 ${pass} / 失败 ${fail}`);
if (fail) { console.log('\n失败项：'); fails.forEach(f => console.log('  - ' + f)); process.exit(1); }
console.log('技能自检通过。');

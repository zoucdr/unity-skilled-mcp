'use strict';
/**
 * calibrate.js — 一次性测定 texgen 画布坐标方向，把结论写成 calibration.json。
 *   node calibrate.js [--project E:/unity3d-skilled-mcp/demo]
 *
 * 为什么必须先跑这个：
 *   texgen 的 layer 坐标到底是「y 向上（Unity SetPixels 约定）」还是
 *   「y 向下（图像行号）」，决定了每一个 center/size 该不该翻转。
 *   这个问题的答案无法从参数说明里读出来——只能画两个已知位置的方块，
 *   导出 PNG，再解码看它们落在文件的上面还是下面。
 *
 * 做法：512x512 黑底，红块 center [128,128]、蓝块 center [384,384]。
 *   若红块落在文件下半部 → 画布 y 向上（原点在左下）
 *   若红块落在文件上半部 → 画布 y 向下（原点在左上）
 * 再用 origin=center 画一个 center [0,0] 的绿块，验证原点偏移是否生效。
 */
const fs = require('fs');
const path = require('path');
const tx = require('./tx');
const PNG = require('./png');

const PROJECT = (() => {
  const i = process.argv.indexOf('--project');
  if (i >= 0 && process.argv[i + 1]) return process.argv[i + 1];
  return 'E:/unity3d-skilled-mcp/demo';
})();

const SIZE = 512;
const BOX = 160;
const OUT_DIR = 'Assets/TexGenCalib';

function centroid(img, pred) {
  let sx = 0, sy = 0, n = 0;
  for (let y = 0; y < img.h; y++) {
    for (let x = 0; x < img.w; x++) {
      const i = (y * img.w + x) * img.channels;
      if (pred(img.data[i], img.data[i + 1], img.data[i + 2])) { sx += x; sy += y; n++; }
    }
  }
  return n ? { x: +(sx / n).toFixed(1), y: +(sy / n).toFixed(1), pixels: n } : null;
}

const isRed = (r, g, b) => r > 150 && g < 90 && b < 90;
const isBlue = (r, g, b) => b > 150 && r < 90 && g < 90;
const isGreen = (r, g, b) => g > 150 && r < 90 && b < 90;

async function render(file, layers, origin) {
  const rel = `${OUT_DIR}/${file}`;
  await tx.call('texgen.render', {
    width: SIZE, height: SIZE, background: '#000000', origin,
    supersample: 1, overwrite: true,
    output_path: rel,
    layers,
  });
  const abs = path.join(PROJECT, rel);
  if (!fs.existsSync(abs)) throw new Error('工具报告成功但文件不存在：' + abs);
  return { abs, img: PNG.readPNG(abs) };
}

async function main() {
  console.log('工程：' + PROJECT);
  tx.configure({ project: PROJECT });

  // --- 1. origin=topLeft，考察两个已知坐标落点 ---
  const tl = await render('calib_topleft.png', [
    { shape: 'rect', center: [128, 128], size: [BOX, BOX], fill: 'solid', color: '#FF0000' },
    { shape: 'rect', center: [384, 384], size: [BOX, BOX], fill: 'solid', color: '#0000FF' },
  ], 'topLeft');

  const red = centroid(tl.img, isRed);
  const blue = centroid(tl.img, isBlue);
  if (!red || !blue) throw new Error('标定失败：没找到红/蓝方块，检查 fill:"solid" 是否生效');

  const mid = SIZE / 2;
  const yUp = red.y > mid;          // 画布 y=128 落在文件下半部 => 原点在左下、y 向上
  const xRight = red.x < mid;       // 画布 x=128 在左侧

  // --- 2. origin=center，验证原点偏移 ---
  const ct = await render('calib_center.png', [
    { shape: 'rect', center: [0, 0], size: [BOX, BOX], fill: 'solid', color: '#00FF00' },
  ], 'center');
  const green = centroid(ct.img, isGreen);
  const centerOffsetOk = green && Math.abs(green.x - mid) < 8 && Math.abs(green.y - mid) < 8;

  const result = {
    measured_at: new Date().toISOString(),
    project: PROJECT,
    canvas: { width: SIZE, height: SIZE },
    // 核心结论
    y_axis: yUp ? 'up' : 'down',
    x_axis: xRight ? 'right' : 'left',
    origin: {
      topLeft: { origin_px: [0, 0] },
      center: { origin_px: [mid, mid], verified: !!centerOffsetOk },
    },
    evidence: {
      'center[128,128] 的红色块质心': red,
      'center[384,384] 的蓝色块质心': blue,
      'origin=center 时 center[0,0] 的绿色块质心': green,
    },
    // 由此推导的直接可用的换算规则
    rules: yUp ? {
      layer_y: 'y 向上：canvas_y = originY + y，y=0 在画布底边（origin=topLeft 时 originY=0）',
      uv_to_layer: 'UV 的 v 向上，与画布同向 → 直接用 y = v * height，不要写 1-v',
      on_screen: '画布 y 越大，在导出的 PNG 里越靠上',
    } : {
      layer_y: 'y 向下：canvas_y = originY + y，y=0 在画布顶边',
      uv_to_layer: 'UV 的 v 向上而画布 y 向下 → 必须用 y = (1 - v) * height',
      on_screen: '画布 y 越大，在导出的 PNG 里越靠下',
    },
  };

  const outFile = path.join(__dirname, '..', 'references', 'calibration.json');
  fs.mkdirSync(path.dirname(outFile), { recursive: true });
  fs.writeFileSync(outFile, JSON.stringify(result, null, 2), 'utf8');

  console.log('\n=== 标定结果 ===');
  console.log(`画布 x 轴：向 ${result.x_axis}`);
  console.log(`画布 y 轴：向 ${result.y_axis}  ${yUp ? '（原点在左下，符合 Unity SetPixels）' : '（原点在左上，图像行号）'}`);
  console.log(`origin=center 原点偏移：${centerOffsetOk ? '已验证生效' : '未按预期生效，需人工确认'}`);
  console.log('\n证据：');
  for (const [k, v] of Object.entries(result.evidence)) console.log(`  ${k}: ${JSON.stringify(v)}`);
  console.log('\n换算规则：');
  for (const [k, v] of Object.entries(result.rules)) console.log(`  ${k}: ${v}`);
  console.log('\n已写入 ' + outFile);
  console.log('标定图：' + tl.abs + ' / ' + ct.abs + '（确认后可删除）');
}

main().catch(e => { console.error('\n标定失败：' + e.message); process.exit(1); });

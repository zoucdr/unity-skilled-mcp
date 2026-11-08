'use strict';
/**
 * inspect.js — 检查 texgen 产出的贴图，把「我觉得还行」变成可判定的数字。
 *   node inspect.js <png> [--ref <png>] [--normal] [--crop x,y,w,h] [--json]
 *
 * 为什么需要它：
 *   - 贴图平了 / 糊了 / 过曝了，肉眼在缩略图上看不出来（4 倍面积平均会把细节抹平）。
 *   - 法线图「看着是蓝紫色」不代表正确，得看 xy 偏移量和归一化程度。
 *   - 无缝平铺不能靠"看不出来接缝"，得量化左右边、上下边的差异。
 *
 * 输出：尺寸、各通道统计、对比度、噪声能量、是否接近纯色、接缝差异、法线合理性。
 */
const path = require('path');
const PNG = require('./png');

function parseArgs(argv) {
  const o = { files: [], ref: null, normal: false, crop: null, json: false, tile: 0 };
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--ref') o.ref = argv[++i];
    else if (a === '--normal') o.normal = true;
    else if (a === '--json') o.json = true;
    else if (a === '--tile') {
      const n = Number(argv[i + 1]);
      o.tile = Number.isInteger(n) && n >= 2 && n <= 4 ? (++i, n) : 2;
    } else if (a === '--crop') {
      const v = argv[++i].split(',').map(Number);
      if (v.length !== 4 || v.some(n => !Number.isFinite(n))) throw new Error('--crop 需要 x,y,w,h 四个数字');
      o.crop = v;
    } else if (a.startsWith('--')) throw new Error('未知参数 ' + a);
    else o.files.push(a);
  }
  return o;
}

function cropImg(img, [x, y, w, h]) {
  const ch = img.channels;
  const out = Buffer.alloc(w * h * ch);
  for (let j = 0; j < h; j++) {
    for (let i = 0; i < w; i++) {
      const sx = Math.min(img.w - 1, Math.max(0, x + i));
      const sy = Math.min(img.h - 1, Math.max(0, y + j));
      img.data.copy(out, (j * w + i) * ch, (sy * img.w + sx) * ch, (sy * img.w + sx) * ch + ch);
    }
  }
  return { w, h, channels: ch, data: out };
}

function channelStats(img) {
  const { w, h, channels, data } = img;
  const n = w * h;
  const out = [];
  const names = channels >= 3 ? ['r', 'g', 'b', 'a'] : ['v'];
  for (let c = 0; c < channels; c++) {
    let min = 255, max = 0, sum = 0;
    for (let i = 0; i < n; i++) {
      const v = data[i * channels + c];
      if (v < min) min = v;
      if (v > max) max = v;
      sum += v;
    }
    const mean = sum / n;
    let acc = 0;
    for (let i = 0; i < n; i++) { const d = data[i * channels + c] - mean; acc += d * d; }
    out.push({ channel: names[c], min, max, mean: +mean.toFixed(2), dev: +Math.sqrt(acc / n).toFixed(3) });
  }
  return out;
}

/** 水平 + 垂直梯度的平均能量，用于判断「有没有细节」以及「是不是噪点」。 */
function gradientEnergy(img) {
  const { w, h, channels, data } = img;
  const c = Math.min(channels, 3);
  let gx = 0, gy = 0, nx = 0, ny = 0;
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const i = (y * w + x) * channels;
      if (x + 1 < w) {
        for (let k = 0; k < c; k++) gx += Math.abs(data[i + k] - data[i + channels + k]);
        nx++;
      }
      if (y + 1 < h) {
        for (let k = 0; k < c; k++) gy += Math.abs(data[i + k] - data[i + w * channels + k]);
        ny++;
      }
    }
  }
  return { horizontal: +(gx / Math.max(1, nx)).toFixed(3), vertical: +(gy / Math.max(1, ny)).toFixed(3) };
}

/** 平铺接缝：比较左/右边缘列、上/下边缘行。差异小 = 无缝。 */
function seamDelta(img) {
  const { w, h, channels, data } = img;
  const c = Math.min(channels, 3);
  let lr = 0, tb = 0;
  for (let y = 0; y < h; y++) {
    const a = (y * w) * channels;
    const b = (y * w + w - 1) * channels;
    for (let k = 0; k < c; k++) lr += Math.abs(data[a + k] - data[b + k]);
  }
  for (let x = 0; x < w; x++) {
    const a = x * channels;
    const b = ((h - 1) * w + x) * channels;
    for (let k = 0; k < c; k++) tb += Math.abs(data[a + k] - data[b + k]);
  }
  return {
    leftRight: +(lr / (h * c)).toFixed(3),
    topBottom: +(tb / (w * c)).toFixed(3),
  };
}

/** 法线图合理性：xy 偏移量决定凹凸强弱；归一化程度判断是否被当颜色图破坏过。 */
function normalReport(img) {
  const { w, h, channels, data } = img;
  let nx2 = 0, ny2 = 0, lenSum = 0, lenMin = Infinity, lenMax = 0, n = 0;
  let blueTooLow = 0;
  for (let i = 0; i < w * h; i++) {
    const r = data[i * channels], g = data[i * channels + 1], b = data[i * channels + 2];
    const x = (r / 255) * 2 - 1;
    const y = (g / 255) * 2 - 1;
    const z = (b / 255) * 2 - 1;
    nx2 += x * x; ny2 += y * y;
    const len = Math.hypot(x, y, z);
    lenSum += len;
    if (len < lenMin) lenMin = len;
    if (len > lenMax) lenMax = len;
    if (b < 100) blueTooLow++;
    n++;
  }
  return {
    meanAbsX: +Math.sqrt(nx2 / n).toFixed(4),
    meanAbsY: +Math.sqrt(ny2 / n).toFixed(4),
    meanLength: +(lenSum / n).toFixed(4),
    lengthRange: [+lenMin.toFixed(3), +lenMax.toFixed(3)],
    blueBelow100Ratio: +(blueTooLow / n).toFixed(4),
  };
}

/** 拼成 N×N 平铺预览，这是判断「有没有缝」最可靠的方式。 */
function tilePreview(img, times = 2) {
  const { w, h, channels, data } = img;
  const W = w * times, H = h * times;
  const out = Buffer.alloc(W * H * channels);
  const rowBytes = w * channels;
  for (let ty = 0; ty < times; ty++) {
    for (let tx = 0; tx < times; tx++) {
      for (let y = 0; y < h; y++) {
        const src = y * rowBytes;
        const dst = ((ty * h + y) * W + tx * w) * channels;
        data.copy(out, dst, src, src + rowBytes);
      }
    }
  }
  return { w: W, h: H, channels, data: out };
}

/** 与参考图的差异（需同尺寸）。 */
function diffReport(a, b) {
  if (a.w !== b.w || a.h !== b.h) return { comparable: false, reason: `尺寸不同 ${a.w}x${a.h} vs ${b.w}x${b.h}` };
  const ch = Math.min(a.channels, b.channels, 3);
  let sum = 0, big = 0, n = 0;
  for (let i = 0; i < a.w * a.h; i++) {
    for (let k = 0; k < ch; k++) {
      const d = Math.abs(a.data[i * a.channels + k] - b.data[i * b.channels + k]);
      sum += d;
      if (d > 16) big++;
      n++;
    }
  }
  return {
    comparable: true,
    meanDiff: +(sum / n).toFixed(3),
    bigDiffRatio: +(big / n).toFixed(4),
  };
}

function analyze(img, opt) {
  // 兼容两种调用：传解码后的 {w,h,channels,data}，或直接传文件路径。
  // 早期版本只接受对象，verify.js 传路径时静默得到 NaN 统计 —— 这种"静默错"
  // 比抛错更贵，所以这里显式支持并校验。
  if (typeof img === 'string') img = PNG.readPNG(img);
  if (!img || !img.w || !img.h || !img.data) {
    throw new TypeError('analyze 需要 {w,h,channels,data} 或图片文件路径');
  }
  const rep = {
    size: [img.w, img.h],
    channels: img.channels,
    channelsStats: channelStats(img),
    gradient: gradientEnergy(img),
    seam: seamDelta(img),
    flat: false,
    warnings: [],
  };

  const maxDev = Math.max(...rep.channelsStats.map(c => c.dev));
  if (maxDev < 0.5) { rep.flat = true; rep.warnings.push('整张图几乎是纯色（所有通道 dev < 0.5）'); }

  const maxed = rep.channelsStats.filter(c => c.channel !== 'a' && c.max >= 255).length;
  if (maxed >= 2) rep.warnings.push('有通道达到 255 上限，注意过曝/裁切');

  // 接缝用「相对内部相邻差」判断，绝对值没有意义：
  // 带随机抖动的 pattern（cobble / scale）边缘两侧本就是不同的格子，色差天然存在。
  const innerBaseline = (rep.gradient.horizontal + rep.gradient.vertical) / 2;
  const seamWorst = Math.max(rep.seam.leftRight, rep.seam.topBottom);
  rep.seam.ratio = +(seamWorst / Math.max(innerBaseline, 0.5)).toFixed(2);
  rep.seam.baseline = +innerBaseline.toFixed(3);
  // 注意：这个指标只在「连续型纹理」（噪声、石材、砂土）上可靠。
  // 结构化图案（brick / plank / tile / hex）的首行与末行本来就画的是不同的砖，
  // 差值天然偏大，此时应看 --tile 生成的 2x2 拼贴预览，而不是看这个比值。
  if (rep.seam.ratio > 2.5) {
    rep.warnings.push(
      `边缘差异是内部相邻差异的 ${rep.seam.ratio} 倍。` +
      `若这是结构化图案（砖/板/瓦），多半是正常现象；` +
      `用 --tile 生成 2x2 拼贴预览再判断是否有真缝`
    );
  }

  if (opt.normal) {
    rep.normal = normalReport(img);
    const n = rep.normal;
    if (n.blueBelow100Ratio > 0.02) {
      rep.warnings.push(`有 ${(n.blueBelow100Ratio * 100).toFixed(1)}% 像素蓝色通道 < 100，法线可能被当成颜色图处理（sRGB 未关或格式错）`);
    }
    if (n.meanLength < 0.5 || n.meanLength > 1.15) {
      rep.warnings.push(`法线平均长度 ${n.meanLength}，正常应接近 1（编码/解码有误）`);
    }
    if (n.meanAbsX < 0.01 && n.meanAbsY < 0.01) {
      rep.warnings.push('法线 xy 几乎没有偏移 → 表面是平的，高度场可能没写进去');
    }
  }
  return rep;
}

function main() {
  const opt = parseArgs(process.argv);
  if (!opt.files.length) {
    console.log('用法：node inspect.js <png> [--ref <png>] [--normal] [--crop x,y,w,h] [--json]');
    process.exit(2);
  }

  const reports = [];
  for (const f of opt.files) {
    const abs = path.resolve(f);
    let img;
    try { img = PNG.readPNG(abs); }
    catch (e) { console.error(`读取失败 ${f}: ${e.message}`); process.exitCode = 1; continue; }
    if (opt.crop) img = cropImg(img, opt.crop);

    const rep = analyze(img, opt);
    rep.file = abs;
    if (opt.ref) {
      try { rep.diffVsRef = diffReport(img, PNG.readPNG(path.resolve(opt.ref))); }
      catch (e) { rep.diffVsRef = { comparable: false, reason: e.message }; }
    }
    reports.push(rep);
  }

  if (opt.json) {
    console.log(JSON.stringify(reports.length === 1 ? reports[0] : reports, null, 2));
    return;
  }

  for (const r of reports) {
    console.log('\n' + '='.repeat(60));
    console.log(r.file);
    console.log(`尺寸 ${r.size[0]}x${r.size[1]}  channels=${r.channels}`);
    console.log('通道：');
    for (const c of r.channelsStats) {
      console.log(`  ${c.channel}  min=${c.min} max=${c.max} mean=${c.mean} dev=${c.dev}`);
    }
    console.log(`梯度能量：水平 ${r.gradient.horizontal} / 垂直 ${r.gradient.vertical}`);
    console.log(`平铺接缝：左右 ${r.seam.leftRight} / 上下 ${r.seam.topBottom}` +
      `（内部基线 ${r.seam.baseline}，比值 ${r.seam.ratio}）`);
    if (r.normal) {
      const n = r.normal;
      console.log(`法线：|x|̄=${n.meanAbsX} |y|̄=${n.meanAbsY} 平均长度=${n.meanLength} ` +
        `范围=[${n.lengthRange.join(', ')}] 蓝<100占比=${(n.blueBelow100Ratio * 100).toFixed(2)}%`);
    }
    if (r.diffVsRef) {
      const d = r.diffVsRef;
      console.log(d.comparable
        ? `与参考图差异：平均 ${d.meanDiff} / 显著差异像素占比 ${(d.bigDiffRatio * 100).toFixed(2)}%`
        : `与参考图不可比：${d.reason}`);
    }
    if (r.warnings.length) {
      console.log('⚠ 警告：');
      r.warnings.forEach(w => console.log('  - ' + w));
    } else {
      console.log('未发现明显问题。');
    }
  }

  if (opt.tile) {
    for (const f of opt.files) {
      const abs = path.resolve(f);
      try {
        const img = PNG.readPNG(abs);
        const tiled = tilePreview(img, opt.tile);
        const out = abs.replace(/\.png$/i, '') + `_tiled${opt.tile}.png`;
        PNG.writePNG(out, tiled.w, tiled.h, tiled.data);
        console.log(`\n平铺预览（${opt.tile}x${opt.tile}）：${out}`);
        console.log('打开这张图判断有没有真缝 —— 比看接缝数值可靠。');
      } catch (e) {
        console.error(`生成平铺预览失败 ${f}: ${e.message}`);
      }
    }
  }
}

if (require.main === module) main();

module.exports = { analyze, channelStats, gradientEnergy, seamDelta, normalReport, diffReport, cropImg, tilePreview };

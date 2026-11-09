'use strict';
/**
 * lathe.js — 程序化曲面（旋转体 / 扫掠管）的几何护栏。
 *
 * 分工：uvspace.js 管 2D 画布坐标；lathe.js 管 3D 曲面展开的几何语义。
 * 目标：把「必须渲染出来、肉眼看三张截图才发现」的五类问题，提前到生成时抛错。
 *
 *   const L = require('./lathe');
 *   L.guard({
 *     texSize: [4096, 1024], radius: 0.85, height: 1.15,
 *     basis: 'sin-cos', cameraSide: '-Z',
 *     metallic: 0.4, hasReflectionProbe: false,
 *     outerRadius: 0.850, innerRadius: 0.814,
 *     csharp: fs.readFileSync('examples/fish_mug.cs', 'utf8'),
 *   });
 *
 * 所有断言都是纯函数、离线可跑，不连 Unity。check.js 里有对应的回归断言。
 * 数字来自 references/semantics.md 第六节的实测，别凭印象改。
 */

const LIMITS = {
  aspectTol: 0.15,           // 贴图宽高比与「周长:高度」允许的相对误差
  metallicMaxNoProbe: 0.60,  // 无反射探针时金属度上限
  rimRatioMin: 0.015,        // 口沿壁厚 / 外半径
  rimRatioMax: 0.075,
  outwardMinRatio: 0.90,     // 外壁三角形法线朝外比例下限
};

function fail(msg, hint) {
  throw new Error('曲面护栏：' + msg + (hint ? '\n  修复：' + hint : ''));
}

function normSize(texSize) {
  const w = Array.isArray(texSize) ? texSize[0] : texSize && texSize.w;
  const h = Array.isArray(texSize) ? texSize[1] : texSize && texSize.h;
  if (!Number.isFinite(w) || !Number.isFinite(h) || w <= 0 || h <= 0) {
    fail('贴图尺寸无效：' + JSON.stringify(texSize), '传 [宽, 高] 或 {w, h}');
  }
  return { w, h };
}

/**
 * 1) 柱面展开贴图的宽高比必须 ≈ 周长 : 高度。
 * 用 2:1 的图贴 4.37:1 的杯子，图案会被横向拉伸约 2 倍（实测踩过）。
 */
function assertTextureAspect(texSize, geometry, opts) {
  const o = opts || {};
  const { w, h } = normSize(texSize);
  const C = Number.isFinite(geometry.circumference)
    ? geometry.circumference : 2 * Math.PI * geometry.radius;
  const H = geometry.height;
  if (!Number.isFinite(C) || !Number.isFinite(H) || C <= 0 || H <= 0) {
    fail('曲面几何无效：' + JSON.stringify(geometry), '给 {radius, height} 或 {circumference, height}');
  }
  const want = C / H;
  const got = w / h;
  const rel = Math.abs(got - want) / want;
  const tol = o.tol === undefined ? LIMITS.aspectTol : o.tol;
  if (rel > tol) {
    const suggestedW = Math.round(h * want / 64) * 64;
    fail(
      `贴图宽高比 ${got.toFixed(2)} 与柱面周长:高度 ${want.toFixed(2)} 相差 ${(rel * 100).toFixed(0)}%（容差 ${(tol * 100).toFixed(0)}%），图案会横向拉伸或压扁`,
      `把贴图改成约 ${suggestedW}×${h}，或调整 radius / height`
    );
  }
  return { want, got, rel, suggestedWidth: Math.round(h * want / 64) * 64 };
}

/**
 * 2) 由网格参数基与相机所在侧，算出 u 是否要取反。
 *
 * 锚点（实测）：x = r·sinθ, z = r·cosθ，相机在 -Z 侧看向 +Z 时，
 * θ 增大对应世界 -X（屏幕左），所以 u = 1 - θ/2π 才能让贴图不左右镜像。
 * 其余三格由 180° 对称性推导，标 measured:false，换基后建议重新标定一次。
 */
const FLIP_TABLE = {
  'sin-cos': { '-Z': true, '+Z': false, '-X': false, '+X': true },
  'cos-sin': { '-Z': false, '+Z': true, '-X': true, '+X': false },
};

function uvOrientation(o) {
  const basis = (o && o.basis) || 'sin-cos';
  const side = (o && o.cameraSide) || '-Z';
  const row = FLIP_TABLE[basis];
  if (!row || !(side in row)) {
    fail('未知的网格基/相机侧：' + basis + ' / ' + side,
      'basis 取 sin-cos 或 cos-sin；cameraSide 取 -Z / +Z / -X / +X');
  }
  const flip = row[side];
  return {
    flip,
    formula: flip ? 'u = 1 - theta / (2*PI)' : 'u = theta / (2*PI)',
    csharp: flip ? 'uvs.Add(new Vector2(1f - (float)j / N, ...));'
                 : 'uvs.Add(new Vector2((float)j / N, ...));',
    reason: flip
      ? 'θ 增大方向在屏幕上朝左，贴图会左右镜像，必须取反'
      : 'θ 增大方向在屏幕上朝右，直接用 u = θ/2π',
    measured: basis === 'sin-cos' && side === '-Z',
  };
}

/** 检查一段 C# 里写的 u 公式是否与推导出的朝向一致。 */
function assertUVOrientation(o) {
  const ori = uvOrientation(o);
  const code = String(o.csharp || o.uFormula || '');
  if (!code) fail('没有可检查的 UV 公式', '传 csharp（C# 源码）或 uFormula');
  const flipped = /1f\s*-\s*\(float\)\s*j|1\.0f\s*-\s*\(float\)\s*j|1f\s*-\s*j\s*\/|1\s*-\s*\(float\)\s*j/.test(code);
  if (flipped !== ori.flip) {
    fail(
      `UV 朝向不符：当前推导要求 ${ori.formula}，但代码里是 ${flipped ? '取反' : '未取反'}`,
      ori.csharp
    );
  }
  return ori;
}

/**
 * 3) 扫掠管的三角形绕序。
 * 按 (a,b,c) 生成时法线朝管内，整个把手会被背面剔除而完全看不见（实测踩过）。
 * 既提供正确绕序常量，也提供对 C# 源码的静态检查。
 */
const SWEEP_TRIANGLES = [[0, 2, 1], [1, 2, 3]]; // [a,c,b] / [b,c,d]

function assertSweepWinding(csharp, opts) {
  const o = opts || {};
  const cs = String(csharp || '');
  if (!cs) fail('没有可检查的扫掠管代码', '传 C# 源码');
  const problems = [];
  // 必须按变量名限定：杯身旋转体的 `tl.Add(a); tl.Add(b); tl.Add(c);` 是正确的外侧绕序，
  // 只有扫掠管的 list（默认 ht）才需要反转成 (a,c,b)。混检会把旋转体误报成错误。
  const v = String(o.varName || 'ht').replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const wrong = new RegExp(v + '\\.Add\\(a\\)\\s*;\\s*' + v + '\\.Add\\(b\\)\\s*;\\s*' + v + '\\.Add\\(c\\)\\s*;');
  const right = new RegExp(v + '\\.Add\\(a\\)\\s*;\\s*' + v + '\\.Add\\(c\\)\\s*;\\s*' + v + '\\.Add\\(b\\)\\s*;');
  if (wrong.test(cs)) {
    problems.push(`扫掠管 ${v} 用了 (a,b,c) 内侧绕序，法线朝内会被背面剔除`);
  }
  if (o.requirePositive !== false && !right.test(cs)) {
    problems.push(`没找到 ${v} 的 (a,c,b) 外侧绕序，确认扫掠管是否用了正确三角形`);
  }
  if (problems.length) {
    fail(problems.join('；'),
      'ht.Add(a); ht.Add(c); ht.Add(b); / ht.Add(b); ht.Add(c); ht.Add(d);（若变量名不同，传 varName）');
  }
  return { ok: true, triangles: SWEEP_TRIANGLES, varName: o.varName || 'ht' };
}

/** 4) 无反射探针时，金属度给满会让描金发黑（实测 0.86 → 黑，0.4 → 正常）。 */
function assertMetallic(value, opts) {
  const o = opts || {};
  const max = o.max === undefined ? LIMITS.metallicMaxNoProbe : o.max;
  if (o.hasReflectionProbe === true) return { ok: true, value, capped: false };
  if (!Number.isFinite(value) || value < 0 || value > 1) {
    fail('金属度必须是 [0,1] 的数值，收到 ' + value);
  }
  if (value > max) {
    fail(`无反射探针时金属度 ${value} 超过上限 ${max}，金色会发黑`,
      `降到 ~0.4，或给场景加 ReflectionProbe 并传 hasReflectionProbe:true`);
  }
  return { ok: true, value, capped: true };
}

/** 5) 口沿壁厚。0.064/0.85 ≈ 7.5% 会变成一圈粗蓝环（实测）。 */
function assertRimThickness(outerRadius, innerRadius, opts) {
  const o = opts || {};
  const r = Number.isFinite(o.radius) ? o.radius : outerRadius;
  const ratio = (outerRadius - innerRadius) / r;
  const min = o.min === undefined ? LIMITS.rimRatioMin : o.min;
  const max = o.max === undefined ? LIMITS.rimRatioMax : o.max;
  if (!(ratio >= min && ratio <= max)) {
    fail(`口沿壁厚比 ${ratio.toFixed(3)} 超出 [${min}, ${max}]，会变成粗色环或薄到穿帮`,
      'fish_mug 实测 (0.850-0.814)/0.850 = 0.042');
  }
  return { ok: true, ratio, thickness: outerRadius - innerRadius };
}

/**
 * 对 C# 源码做整组静态检查，防止「文档写了、代码没跟着改」。
 * 返回 {ok, checks, problems}；有硬性问题时抛错（除非 opts.noThrow）。
 */
function assertCSharp(csharp, spec) {
  const s = spec || {};
  const cs = String(csharp || '');
  const checks = [];
  const problems = [];
  const add = (name, ok, detail) => {
    checks.push({ name, ok, detail });
    if (!ok) problems.push(`${name}：${detail}`);
  };

  if (!cs) {
    add('C# 源码非空', false, '传空字符串');
  } else {
    add('C# 源码非空', true, cs.length + ' 字符');

    // UV 朝向
    if (s.basis || s.cameraSide) {
      try {
        const ori = assertUVOrientation({ basis: s.basis, cameraSide: s.cameraSide, csharp: cs });
        add('UV 朝向与网格基一致', true, ori.formula + (ori.measured ? '（实测锚点）' : '（推导）'));
      } catch (e) { add('UV 朝向与网格基一致', false, e.message.split('\n')[0]); }
    }

    // 扫掠绕序
    try {
      const vname = s.sweepVar || 'ht';
      assertSweepWinding(cs, { requirePositive: s.requireSweep !== false, varName: vname });
      add('扫掠管外侧绕序', true, vname + ': (a,c,b) / (b,c,d)');
    } catch (e) { add('扫掠管外侧绕序', false, e.message.split('\n')[0]); }

    // 贴图导入分辨率不能小于贴图本身
    if (s.texSize) {
      const { w } = normSize(s.texSize);
      const sizes = [...cs.matchAll(/maxTextureSize\s*=\s*(\d+)/g)].map(m => Number(m[1]));
      add('导入分辨率不小于贴图宽', sizes.length > 0 && sizes.every(v => v >= w),
        sizes.length ? `maxTextureSize=${sizes.join(',')} / 贴图宽 ${w}` : 'C# 里没有 maxTextureSize');
    }

    // 釉面金属度与内壁自发光
    add('使用 MetallicGlossMap', /_MetallicGlossMap/.test(cs), /_MetallicGlossMap/.test(cs) ? '已挂' : '未挂');
    add('内壁有自发光补偿', /_EmissionColor/.test(cs) && /EnableKeyword\s*\(\s*"_EMISSION"/.test(cs),
      /_EmissionColor/.test(cs) ? '有' : '内壁背光会发黑');

    // 口沿壁厚：从 pRim / pInner 的第一行读半径
    if (Number.isFinite(s.outerRadius) && Number.isFinite(s.innerRadius)) {
      const m1 = /pRim\s*=\s*\{[\s\S]*?new Vector2\(\s*([\d.]+)f/.exec(cs);
      const m2 = /pInner\s*=\s*\{[\s\S]*?new Vector2\(\s*([\d.]+)f/.exec(cs);
      const ro = m1 ? Number(m1[1]) : null;
      const ri = m2 ? Number(m2[1]) : null;
      add('C# 口沿半径与 spec 一致', ro === s.outerRadius && ri === s.innerRadius,
        `C# pRim=${ro} pInner=${ri} / spec ${s.outerRadius}, ${s.innerRadius}`);
      if (ro !== null && ri !== null) {
        try {
          const t = assertRimThickness(ro, ri, { radius: ro });
          add('C# 口沿壁厚合理', true, t.ratio.toFixed(3));
        } catch (e) { add('C# 口沿壁厚合理', false, e.message.split('\n')[0]); }
      }
    }
  }

  const ok = problems.length === 0;
  if (!ok && !s.noThrow) {
    const err = new Error('曲面护栏：C# 静态检查未通过\n  - ' + problems.join('\n  - '));
    err.checks = checks;
    err.problems = problems;
    throw err;
  }
  return { ok, checks, problems };
}

/** 综合入口：fish_mug.js 在提交 code.execute 之前调用。 */
function guard(o) {
  const s = o || {};
  const report = { checks: [], problems: [] };
  const step = (name, fn) => {
    try { report.checks.push({ name, ok: true, detail: fn() }); }
    catch (e) { report.checks.push({ name, ok: false, detail: e.message.split('\n')[0] }); report.problems.push(name + '：' + e.message.split('\n')[0]); }
  };

  if (s.texSize && s.radius !== undefined && s.height !== undefined) {
    step('贴图宽高比', () => {
      const a = assertTextureAspect(s.texSize, { radius: s.radius, height: s.height }, s);
      return `${a.got.toFixed(2)} vs ${a.want.toFixed(2)}`;
    });
  }
  if (Number.isFinite(s.metallic)) {
    step('金属度', () => { const m = assertMetallic(s.metallic, s); return `${m.value}`; });
  }
  if (Number.isFinite(s.outerRadius) && Number.isFinite(s.innerRadius)) {
    step('口沿壁厚', () => { const t = assertRimThickness(s.outerRadius, s.innerRadius, s); return t.ratio.toFixed(3); });
  }
  if (s.csharp) {
    const r = assertCSharp(s.csharp, Object.assign({}, s, { noThrow: true }));
    report.csharp = r;
    report.checks.push({
      name: 'C# 静态检查', ok: r.ok,
      detail: `${r.checks.filter(c => c.ok).length}/${r.checks.length} 项通过`,
    });
    if (!r.ok) report.problems.push(...r.problems.map(p => 'C# 静态检查：' + p));
  }

  report.ok = report.problems.length === 0;
  if (!report.ok && !s.noThrow) {
    const err = new Error('曲面护栏：参数未通过\n  - ' + report.problems.join('\n  - '));
    err.checks = report.checks;
    err.problems = report.problems;
    throw err;
  }
  return report;
}

module.exports = {
  LIMITS, FLIP_TABLE, SWEEP_TRIANGLES,
  assertTextureAspect, uvOrientation, assertUVOrientation,
  assertSweepWinding, assertMetallic, assertRimThickness,
  assertCSharp, guard,
};

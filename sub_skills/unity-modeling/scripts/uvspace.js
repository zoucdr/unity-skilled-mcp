'use strict';
/**
 * uvspace.js — texgen 坐标语义的唯一收口。
 *
 * 【重要】本文件的结论来自实测标定（scripts/calibrate.js），不是从参数说明推断。
 * 早期版本曾把 texgen 判定成「三套互相矛盾的坐标」，那是误诊。真相只有一套，
 * 详见 _guard 与技能里的 references/calibration.json。
 *
 * 实测事实：
 *   1) 画布 layer 坐标 y 向上，y = 0 在画布底边（对应 Unity 的 SetPixels 约定）。
 *   2) origin 只平移原点，**不翻转方向**：topLeft -> 原点 (0,0)；center -> 原点 (W/2,H/2)。
 *   3) UV 的 v 也向上，与画布同向 → 画布 y = v * H，**不需要翻转**。
 *   4) 导出的 PNG 文件行号 y 向下 → 文件行 = (1 - v) * H。
 *
 * 所以只需要两个空间，各自含义清楚：
 *
 *     CANVAS   texgen layer 的 center / size / from / to 等字段
 *              y 向上，与 v 同向            y = v * H
 *
 *     IMAGE    解码 PNG 后的像素数组下标
 *              y 向下，与 v 反向            row = (1 - v) * H
 *
 * 唯一的真值仍然是 UV：u 向右，v 向上，v = 0 是贴图底边。
 * 「看起来反了」几乎总是因为把 IMAGE 行号当成了 CANVAS 坐标。
 */

const SPACE = {
  CANVAS: 'canvas',   // texgen layer 坐标，y 向上
  IMAGE: 'image',     // PNG 像素行号，y 向下
};

/** +1：y 与 v 同向（v=0 在画布底边）；-1：y 与 v 反向（v=0 在文件最后一行）。 */
const YDIR = {
  [SPACE.CANVAS]: +1,
  [SPACE.IMAGE]: -1,
};

function assertUV(u, v) {
  if (!Number.isFinite(u) || !Number.isFinite(v)) {
    throw new TypeError(`UV 必须是有限数，收到 u=${u} v=${v}`);
  }
  if (u < -1e-6 || u > 1 + 1e-6 || v < -1e-6 || v > 1 + 1e-6) {
    throw new RangeError(`UV 越界：u=${u} v=${v}（允许 0..1，UV 重复请在材质 wrap_mode 处理）`);
  }
}

function checkCanvas(W, H) {
  if (!(W > 0) || !(H > 0)) throw new RangeError(`画布尺寸非法 ${W}x${H}`);
}

/** UV -> 指定空间的像素坐标。 */
function uvToPx(u, v, W, H, space) {
  assertUV(u, v);
  const dir = YDIR[space];
  if (dir === undefined) throw new Error(`未知空间 ${space}（只接受 canvas / image）`);
  checkCanvas(W, H);
  return {
    x: u * W,
    y: dir > 0 ? v * H : (1 - v) * H,
  };
}

/** 指定空间的像素坐标 -> UV。 */
function pxToUV(x, y, W, H, space) {
  const dir = YDIR[space];
  if (dir === undefined) throw new Error(`未知空间 ${space}（只接受 canvas / image）`);
  checkCanvas(W, H);
  return {
    u: x / W,
    v: dir > 0 ? y / H : 1 - y / H,
  };
}

/**
 * UV 矩形 -> 两个空间的等价表示。一次拿全，字段名自带语义。
 *
 * @param {number} u0 小 u   @param {number} v0 小 v（贴图下方）
 * @param {number} u1 大 u   @param {number} v1 大 v（贴图上方）
 *
 * 返回：
 *   cx, cy, w, h        CANVAS 空间，**可直接填进 texgen layer 的 center/size**
 *                       （前提是 origin=topLeft；origin=center 时用 forOrigin 转换）
 *   left, top, right, bottom, w, h
 *                       IMAGE 空间，用于读解码后的像素数组；top 对应 v1
 */
function rectUV(u0, v0, u1, v1, W, H) {
  const a = Math.min(u0, u1), b = Math.max(u0, u1);
  const c = Math.min(v0, v1), d = Math.max(v0, v1);
  assertUV(a, c); assertUV(b, d);
  checkCanvas(W, H);
  return {
    u0: a, v0: c, u1: b, v1: d,
    // CANVAS：y 向上，与 v 同向
    cx: ((a + b) / 2) * W,
    cy: ((c + d) / 2) * H,
    w: (b - a) * W,
    h: (d - c) * H,
    // IMAGE：y 向下
    left: a * W,
    top: (1 - d) * H,
    right: b * W,
    bottom: (1 - c) * H,
  };
}

/**
 * 把 rectUV 的 canvas 坐标换算到指定 origin。
 * origin='topLeft' 时原点在左下（y 不变）；'center' 时原点在画布中心（减去半宽半高）。
 * 注意「topLeft」这个名字的含义是"原点位于画布左上角的那个点"，但 y 轴仍然向上。
 */
function forOrigin(r, origin, W, H) {
  if (origin === 'center') {
    return { cx: r.cx - W / 2, cy: r.cy - H / 2, w: r.w, h: r.h };
  }
  if (origin === undefined || origin === null || origin === 'topLeft') {
    return { cx: r.cx, cy: r.cy, w: r.w, h: r.h };
  }
  throw new Error(`未知 origin ${origin}（只接受 topLeft / center）`);
}

/** 便捷：以 UV 中心 + UV 尺寸描述一个层。 */
function boxUV(cu, cv, su, sv, W, H) {
  return rectUV(cu - su / 2, cv - sv / 2, cu + su / 2, cv + sv / 2, W, H);
}

/** texgen layer 的 center/size 片段（origin=topLeft 直接用）。 */
function layerRect(r, extra) {
  return Object.assign({ center: [r.cx, r.cy], size: [r.w, r.h] }, extra || {});
}

/** 兼容旧调用点：等同 layerRect。 */
function renderRect(r, extra) {
  return layerRect(r, extra);
}

function describe() {
  return [
    'texgen 坐标契约（唯一真值：UV，u 向右 / v 向上 / v=0 是底边）',
    '  CANVAS  texgen layer 坐标   y 向上，y = v*H        （实测：origin 只平移不翻转）',
    '  IMAGE   PNG 像素行号        y 向下，row = (1-v)*H',
    '  常见错误：把 IMAGE 行号当成 CANVAS 坐标，于是多写了一次 1-v',
  ].join('\n');
}

module.exports = {
  SPACE, YDIR, assertUV, uvToPx, pxToUV,
  rectUV, forOrigin, boxUV, layerRect, renderRect, describe,
};

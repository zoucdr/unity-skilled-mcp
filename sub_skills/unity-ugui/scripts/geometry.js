/**
 * 层级 / 重叠静态校验 —— 纯计算，不连 Unity。
 *
 * 为什么必须独立存在：verify.js 早期只把「layout_plan 的数值」与「Unity 回读的数值」
 * 对比，而两者都来自 coords.toUGUI 同一个函数，属于自证循环：换算写错了，验收照样 10/10。
 * 本文件从 SPEC.rect（整图绝对像素）出发，按几何事实检查包含/交叉，
 * 再由 verify.js 用 coords.screenRectFromReadback() 从 Unity 回读值独立反算屏幕位置。
 *
 * 规则（SPEC.rect = [left, top, width, height]，左上原点、整图绝对坐标）：
 *  R1 包含即父子：A 完全包住 B、A 是容器、且 A 与 B 当前同级 → B 必须 parent=A.id。
 *  R2 同级不重叠：同父的两个可见节点若部分交叉（非包含），必须显式 allow_overlap。
 *  R3 跨父交叉只记录不判错：不同锚点父容器在效果图里可能视觉相邻，
 *     但响应式布局后未必重叠，交人工 / 预览目检。
 *  R4 平铺率：根节点占比过高说明层级没分组，给出提示（不阻断）。
 */
'use strict';

const CONTAINERS = new Set(['panel', 'card', 'group', 'scroll', 'dropdown', 'tab']);
const NON_VISUAL = new Set(['group', 'canvas']);

function boxOf(n) {
  const r = n.rect;
  if (!Array.isArray(r) || r.length !== 4) return null;
  return { l: r[0], t: r[1], r: r[0] + r[2], b: r[1] + r[3], w: r[2], h: r[3] };
}

function visible(n) {
  return n && n.visible !== false && !NON_VISUAL.has(n.type) && n.type !== 'canvas';
}

/** 相交面积；0 表示不相交或仅贴边。 */
function overlapArea(a, b) {
  const w = Math.min(a.r, b.r) - Math.max(a.l, b.l);
  const h = Math.min(a.b, b.b) - Math.max(a.t, b.t);
  return w > 1e-6 && h > 1e-6 ? w * h : 0;
}

function contains(a, b, eps = 1e-6) {
  return a.l <= b.l + eps && a.t <= b.t + eps && a.r >= b.r - eps && a.b >= b.b - eps;
}

function ancestors(spec, id) {
  const byId = Object.fromEntries(spec.nodes.map(n => [n.id, n]));
  const out = new Set();
  let p = byId[id] && byId[id].parent, guard = 0;
  while (p && guard++ < 64) { out.add(p); p = byId[p] && byId[p].parent; }
  return out;
}

/**
 * @returns {{
 *   ok:boolean, problems:string[], warnings:string[], stats:object,
 *   containsNotNested:Array, siblingOverlaps:Array, crossParentOverlaps:Array,
 *   suggestedParents:object
 * }}
 */
function audit(spec, o = {}) {
  const problems = [], warnings = [];
  const containsNotNested = [], siblingOverlaps = [], crossParentOverlaps = [];
  const suggestedParents = {};
  const eps = o.epsilon || 1; // 允许 1px 描边误差
  const nodes = (spec.nodes || []).filter(n => n.type !== 'canvas' && boxOf(n));
  const boxes = Object.fromEntries(nodes.map(n => [n.id, boxOf(n)]));

  for (let i = 0; i < nodes.length; i++) {
    for (let j = i + 1; j < nodes.length; j++) {
      const a = nodes[i], b = nodes[j];
      if (!visible(a) || !visible(b)) continue;
      const A = boxes[a.id], B = boxes[b.id];
      const area = overlapArea(A, B);
      if (area <= 0) continue;

      const aInB = contains(B, A, eps), bInA = contains(A, B, eps);
      const sameParent = (a.parent || null) === (b.parent || null);
      const ancA = ancestors(spec, a.id), ancB = ancestors(spec, b.id);
      const nested = ancA.has(b.id) || ancB.has(a.id);

      if ((aInB || bInA) && !nested) {
        const outer = bInA ? a : b, inner = bInA ? b : a;
        if (sameParent) {
          if (CONTAINERS.has(outer.type) || outer.type === 'progress' || outer.type === 'slider') {
            containsNotNested.push({ outer: outer.id, inner: inner.id, area: Math.round(area) });
            if (!suggestedParents[inner.id]) suggestedParents[inner.id] = outer.id;
          }
        } else {
          crossParentOverlaps.push({ a: a.id, b: b.id, relation: 'contains', area: Math.round(area) });
        }
      } else if (area > 0 && !aInB && !bInA && sameParent && !nested) {
        if (a.allow_overlap === true || b.allow_overlap === true) continue;
        siblingOverlaps.push({ a: a.id, b: b.id, area: Math.round(area) });
      } else if (area > 0 && !sameParent && !nested) {
        crossParentOverlaps.push({ a: a.id, b: b.id, relation: 'intersect', area: Math.round(area) });
      }
    }
  }

  for (const x of containsNotNested) {
    problems.push(`包含未嵌套：${x.outer} 完全包住 ${x.inner}，但两者同级；应设 ${x.inner}.parent = "${x.outer}"`);
  }
  for (const x of siblingOverlaps) {
    problems.push(`同级重叠：${x.a} 与 ${x.b} 相交 ${x.area}px²；请错开 rect 或设 allow_overlap: true 表明是有意叠放`);
  }

  const roots = nodes.filter(n => !n.parent).length;
  const ratio = nodes.length ? roots / nodes.length : 0;
  const stats = {
    nodes: nodes.length, roots, flatRatio: Number(ratio.toFixed(2)),
    containsNotNested: containsNotNested.length,
    siblingOverlaps: siblingOverlaps.length,
    crossParentOverlaps: crossParentOverlaps.length,
  };
  if (ratio >= (o.flatWarnRatio || 0.8) && nodes.length >= 6) {
    warnings.push(`层级过于扁平：${roots}/${nodes.length} 个节点直接挂在 Canvas 下；容器内的文字/进度条/按钮应设为子节点`);
  }
  if (crossParentOverlaps.length) {
    warnings.push(`${crossParentOverlaps.length} 处跨父交叉需人工目检（不同锚点容器响应式后可能分离）`);
  }
  return {
    ok: problems.length === 0, problems, warnings, stats,
    containsNotNested, siblingOverlaps, crossParentOverlaps, suggestedParents,
  };
}

module.exports = { audit, boxOf, overlapArea, contains, CONTAINERS };

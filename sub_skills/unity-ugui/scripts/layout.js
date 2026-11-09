/**
 * 布局阶段 —— 串联 unity-package/Editor/Tools/UI 的 ugui.do_layout / tattoo / get_layout。
 *
 * 分工：build.js 用 C# 一次性写准 RectTransform（stretch 的 offsetMin/offsetMax 只有 C# 能写），
 * 本文件负责：
 *   1) 产出 layout_plan.json（人类/大模型可审阅的锚点与坐标计划）；
 *   2) 把非 stretch 节点交给 ugui.do_layout 显式设置（官方布局模块，3 元向量）；
 *   3) 把锚点预设交给 ugui.tattoo（12 预设，等价于编辑器 Anchor Presets）；
 *   4) 用 ugui.get_layout 回读，交 verify.js 做误差断言。
 *
 * 实测约束（references/semantics.md）：
 *  - ugui.do_layout / get_layout / tattoo 的 schema required 只有 ["path"]，instance_id 反而是可选。
 *  - anchored_pos / size_delta / anchor_min / anchor_max / pivot 是 vector3（minItems=maxItems=3），
 *    传 2 个数会被 schema 拒；coords.toUGUI().doLayout 已统一给 3 个数。
 *  - tattoo 不传 tattoo_self 时走 ApplyAnchorPresetWithVisualPositionPreserved，保持视觉位置。
 */
'use strict';

const fs = require('fs');
const path = require('path');
const mcp = require('./mcp');
const coords = require('./coords');

function rootName(spec) { return `UGUI_${spec.name.replace(/[^A-Za-z0-9_]/g, '_')}`; }

/** 生成每个节点的层级路径与布局计划。纯计算，不连 Unity。 */
function plan(spec, o = {}) {
  const root = o.rootName || rootName(spec);
  const byId = Object.fromEntries(spec.nodes.map(n => [n.id, n]));
  const pathOf = n => {
    const parts = [n.name || n.id];
    let p = n.parent;
    let guard = 0;
    while (p && byId[p] && guard++ < 64) {
      parts.unshift(byId[p].name || byId[p].id);
      p = byId[p].parent;
    }
    parts.unshift(root);
    return parts.join('/');
  };
  const items = spec.nodes.filter(n => n.type !== 'canvas').map(n => {
    const u = coords.toUGUI(n, spec);
    return {
      id: n.id,
      path: pathOf(n),
      type: n.type,
      rect: n.rect,
      anchorPreset: n.anchor,
      stretch: u.stretch,
      doLayout: u.doLayout,
      offsetMin: u.offsetMin,
      offsetMax: u.offsetMax,
    };
  });
  return {
    generator: 'unity-ugui/layout', version: '1.0',
    screen: spec.name, root,
    reference_resolution: spec.canvas.reference_resolution,
    items,
    produced_at: new Date().toISOString(),
  };
}

function savePlan(p, outDir) {
  fs.mkdirSync(outDir, { recursive: true });
  const f = path.join(outDir, 'layout_plan.json');
  fs.writeFileSync(f, JSON.stringify(p, null, 2));
  return f;
}

/** 实际调用官方布局工具。返回逐条结果，不因单条失败中断（写操作可能已生效）。 */
async function apply(p, o = {}) {
  const results = [];
  const useDoLayout = o.useDoLayout !== false;
  const useTattoo = o.useTattoo !== false;
  for (const it of p.items) {
    const entry = { id: it.id, path: it.path, calls: [], problems: [] };
    if (useDoLayout && !it.stretch) {
      try {
        const r = await mcp.call('ugui.do_layout', Object.assign({ path: it.path }, it.doLayout));
        entry.calls.push({ tool: 'ugui.do_layout', ok: true, result: r });
      } catch (e) { entry.problems.push(`ugui.do_layout: ${e.message}`); entry.calls.push({ tool: 'ugui.do_layout', ok: false, error: e.message }); }
    }
    if (useTattoo) {
      try {
        const r = await mcp.call('ugui.tattoo', { path: it.path, tattoo_preset: it.anchorPreset, tattoo_self: false });
        entry.calls.push({ tool: 'ugui.tattoo', ok: true, result: r });
      } catch (e) { entry.problems.push(`ugui.tattoo: ${e.message}`); entry.calls.push({ tool: 'ugui.tattoo', ok: false, error: e.message }); }
    }
    results.push(entry);
  }
  return results;
}

/** 回读布局，交 verify 比对。 */
async function readback(p, o = {}) {
  const out = [];
  for (const it of p.items) {
    try {
      const r = await mcp.call('ugui.get_layout', { path: it.path });
      out.push(Object.assign({ id: it.id, path: it.path }, r));
    } catch (e) { out.push({ id: it.id, path: it.path, error: e.message }); }
  }
  const f = o.outFile || path.join(o.outDir || '.', 'layout_actual.json');
  fs.mkdirSync(path.dirname(f), { recursive: true });
  fs.writeFileSync(f, JSON.stringify({ screen: p.screen, items: out, produced_at: new Date().toISOString() }, null, 2));
  return { items: out, file: f };
}

module.exports = { rootName, plan, savePlan, apply, readback };

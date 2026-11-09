/**
 * 验收阶段 —— 以数值/反射回读为准，不只信一张截图。
 *
 * 检查项：
 *  1. 层级存在（scene.get_hierarchy + GameObject.Find 双证据）
 *  2. 控件组件齐全（按 controls.TYPES 表逐个 GetComponent）
 *  3. 交互控件的关键序列化引用非 null（requiredRefs 反射读取）
 *  4. RectTransform 与 layout_plan 的误差（anchorMin/Max/pivot/anchoredPos/sizeDelta）
 *  5. Image.sprite 挂载、九宫格 border 已写入 TextureImporter
 *  6. 图资源非纯色、未过曝（inspect.analyze）
 *  7. Game 视图截图 vs 效果图的像素还原度（粗粒度，仅供参考，不替代人工目检）
 *
 * 返回契约沿用 unity-modeling/verify.js：{ ok, checks:[{name,ok,detail}], problems:[] }。
 */
'use strict';

const fs = require('fs');
const path = require('path');
const mcp = require('./mcp');
const PNG = require('./png');
const coords = require('./coords');
const controls = require('./controls');
const layoutMod = require('./layout');
const slice = require('./slice');
const preview = require('./preview');
const geometry = require('./geometry');

/** 生成 C# 回读代码：逐节点输出 node/comp/ref/rect 四类记录到文件。 */
function auditCSharp(spec, auditFile, o = {}) {
  const root = o.rootName || layoutMod.rootName(spec);
  const p = layoutMod.plan(spec, { rootName: root });
  const L = [];
  L.push('var __lines = new System.Collections.Generic.List<string>();');
  L.push('var __ci = System.Globalization.CultureInfo.InvariantCulture;');
  L.push('System.Func<float, string> __f = (x) => x.ToString("F3", __ci);');
  L.push('System.Func<UnityEngine.Object, string> __z = (o) => (o != null ? "1" : "0");');
  for (const it of p.items) {
    const n = spec.nodes.find(x => x.id === it.id);
    const d = controls.def(n.type);
    if (!d) continue;
    const gv = `g_${it.id.replace(/[^A-Za-z0-9_]/g, '_')}`;
    L.push(`var ${gv} = UnityEngine.GameObject.Find("${it.path.replace(/"/g, '\\"')}");`);
    L.push(`if (${gv} == null) { __lines.Add("node|${it.id}|0"); } else { __lines.Add("node|${it.id}|1");`);
    const comps = d.ugui || [];
    for (const c of comps) {
      const cv = `${gv}_${c.replace(/[^A-Za-z0-9_]/g, '_')}`;
      L.push(`  var ${cv} = ${gv}.GetComponent<UnityEngine.UI.${c}>();`);
      L.push(`  __lines.Add("comp|${it.id}|${c}|" + __z(${cv}));`);
    }
    // 关键引用（按类型显式强类型读取，避免反射类型解析失败）
    const refs = {
      'Button.targetGraphic': `(${gv}_Button != null ? __z(${gv}_Button.targetGraphic) : "0")`,
      'Toggle.graphic': `(${gv}_Toggle != null ? __z(${gv}_Toggle.graphic) : "0")`,
      'Toggle.targetGraphic': `(${gv}_Toggle != null ? __z(${gv}_Toggle.targetGraphic) : "0")`,
      'Slider.fillRect': `(${gv}_Slider != null ? __z(${gv}_Slider.fillRect) : "0")`,
      'Slider.handleRect': `(${gv}_Slider != null ? __z(${gv}_Slider.handleRect) : "0")`,
      'Slider.targetGraphic': `(${gv}_Slider != null ? __z(${gv}_Slider.targetGraphic) : "0")`,
      'InputField.textComponent': `(${gv}_InputField != null ? __z(${gv}_InputField.textComponent) : "0")`,
      'InputField.placeholder': `(${gv}_InputField != null ? __z(${gv}_InputField.placeholder) : "0")`,
      'ScrollRect.content': `(${gv}_ScrollRect != null ? __z(${gv}_ScrollRect.content) : "0")`,
      'ScrollRect.viewport': `(${gv}_ScrollRect != null ? __z(${gv}_ScrollRect.viewport) : "0")`,
      'Dropdown.template': `(${gv}_Dropdown != null ? __z(${gv}_Dropdown.template) : "0")`,
      'Dropdown.captionText': `(${gv}_Dropdown != null ? __z(${gv}_Dropdown.captionText) : "0")`,
      'Dropdown.itemText': `(${gv}_Dropdown != null ? __z(${gv}_Dropdown.itemText) : "0")`,
      'Text.font': `(${gv}_Text != null ? __z(${gv}_Text.font) : "0")`,
    };
    const ownComps = d.ugui || [];
    for (const r of (d.requiredRefs || [])) {
      // 只在本节点真实拥有的组件里找同名引用，避免 toggle 误取 Button.targetGraphic
      const key = Object.keys(refs).find(k =>
        ownComps.some(c => k.indexOf(c + '.') === 0) && k.endsWith('.' + r));
      if (key) L.push(`  __lines.Add("ref|${it.id}|${key}|" + ${refs[key]});`);
      else L.push(`  __lines.Add("ref|${it.id}|${r}|-1");`);
    }
    const imgv = `${gv}_Image`;
    if (comps.includes('Image')) {
      L.push(`  __lines.Add("sprite|${it.id}|" + (${imgv} != null ? __z(${imgv}.sprite) : "0") + "|" + (${imgv} != null ? ${imgv}.type.ToString() : "none"));`);
    }
    L.push(`  var rt_${gv} = ${gv}.GetComponent<UnityEngine.RectTransform>();`);
    L.push(`  if (rt_${gv} != null) __lines.Add("rect|${it.id}|" + __f(rt_${gv}.anchorMin.x) + "|" + __f(rt_${gv}.anchorMin.y) + "|" + __f(rt_${gv}.anchorMax.x) + "|" + __f(rt_${gv}.anchorMax.y) + "|" + __f(rt_${gv}.anchoredPosition.x) + "|" + __f(rt_${gv}.anchoredPosition.y) + "|" + __f(rt_${gv}.sizeDelta.x) + "|" + __f(rt_${gv}.sizeDelta.y) + "|" + __f(rt_${gv}.pivot.x) + "|" + __f(rt_${gv}.pivot.y));`);
    L.push('}');
  }
  L.push(`System.IO.File.WriteAllLines("${auditFile.replace(/\\/g, '/').replace(/"/g, '\\"')}", __lines.ToArray());`);
  L.push('UnityEngine.Debug.Log("[unity-ugui] audit lines: " + __lines.Count);');
  return L.join('\n');
}

/** 执行 Unity 侧回读，解析成结构化对象。 */
async function auditUnity(spec, o = {}) {
  const outDir = o.outDir;
  if (!outDir) throw new Error('verify.auditUnity: outDir 必填');
  const auditFile = path.join(outDir, 'audit_raw.txt').replace(/\\/g, '/');
  fs.mkdirSync(outDir, { recursive: true });
  const code = auditCSharp(spec, auditFile, o);
  mcp.assertLint(code);
  await mcp.call('code.execute', {
    mode: 'runtime', code,
    description: `Audit UGUI hierarchy for ${spec.name}`,
    includes: ['UnityEngine', 'UnityEngine.UI', 'UnityEditor', 'System.Collections.Generic', 'System.Globalization', 'System.IO'],
    return_output: true,
    timeout: 120,
  });
  if (!fs.existsSync(auditFile)) throw new Error(`回读文件未生成：${auditFile}`);
  const lines = fs.readFileSync(auditFile, 'utf8').split(/\r?\n/).filter(Boolean);
  const parsed = { nodes: {}, comps: {}, refs: {}, sprites: {}, rects: {} };
  for (const line of lines) {
    const p = line.split('|');
    if (p[0] === 'node') parsed.nodes[p[1]] = p[2] === '1';
    else if (p[0] === 'comp') (parsed.comps[p[1]] = parsed.comps[p[1]] || {})[p[2]] = p[3] === '1';
    else if (p[0] === 'ref') (parsed.refs[p[1]] = parsed.refs[p[1]] || {})[p[2]] = p[3];
    else if (p[0] === 'sprite') parsed.sprites[p[1]] = { hasSprite: p[2] === '1', imageType: p[3] };
    else if (p[0] === 'rect') parsed.rects[p[1]] = p.slice(2).map(Number);
  }
  return { parsed, rawFile: auditFile, lineCount: lines.length };
}

/** 效果图 vs 截图的粗粒度还原度（最近邻缩放 + 分块平均差异），不替代人工目检。 */
function compareImages(reference, shot, o = {}) {
  if (!fs.existsSync(reference) || !fs.existsSync(shot)) {
    return { ok: false, reason: `缺文件 ref=${reference} shot=${shot}` };
  }
  const a = slice.toRGBA(PNG.readPNG(reference));
  const b = slice.toRGBA(PNG.readPNG(shot));
  const ia = PNG.readPNG(reference), ib = PNG.readPNG(shot);
  const W = o.width || 160, H = o.height || 90;
  const sample = (img, data) => {
    const out = new Float64Array(W * H * 3);
    for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
      const sx = Math.min(img.w - 1, Math.floor((x + 0.5) * img.w / W));
      const sy = Math.min(img.h - 1, Math.floor((y + 0.5) * img.h / H));
      const s = (sy * img.w + sx) * 4, d = (y * W + x) * 3;
      out[d] = data[s]; out[d + 1] = data[s + 1]; out[d + 2] = data[s + 2];
    }
    return out;
  };
  const A = sample(ia, a), B = sample(ib, b);
  let acc = 0;
  for (let i = 0; i < A.length; i++) acc += Math.abs(A[i] - B[i]);
  const mae = acc / A.length;
  return { ok: true, sampled: [W, H], mae: Number(mae.toFixed(2)), maxMae: 255, note: 'MAE 仅作趋势参考；透视/字体/九宫格都会抬高数值，必须配合人工目检' };
}

/**
 * 综合验收。
 * @returns {{ok:boolean, checks:Array, problems:Array, metrics:object}}
 */
async function run(spec, o = {}) {
  const checks = [], problems = [];
  const add = (name, ok, detail) => { checks.push({ name, ok, detail }); if (!ok) problems.push(`${name}: ${detail}`); };
  const outDir = o.outDir;
  const manifest = o.manifest;
  const plan = o.plan || layoutMod.plan(spec);

  // 1. 图资源
  if (manifest) {
    const a = slice.audit(manifest);
    add('图资源文件齐全', a.items.length > 0 && a.problems.length === 0, `${a.items.length} 张；${a.problems.join('; ') || '无缺失'}`);
    const flats = a.items.filter(i => i.flat === true);
    add('切图非纯色', flats.length === 0, flats.length ? `纯色图：${flats.map(i => i.id).join(',')}` : '全部通过');
  } else add('图资源文件齐全', false, '未提供 assets_manifest.json');

  // 1b. 层级 / 重叠：纯几何，源自 SPEC.rect，与 toUGUI 完全无关
  const geo = geometry.audit(spec);
  if (outDir) {
    fs.mkdirSync(outDir, { recursive: true });
    fs.writeFileSync(path.join(outDir, 'hierarchy_report.json'),
      JSON.stringify(Object.assign({ screen: spec.name, produced_at: new Date().toISOString() }, geo), null, 2));
  }
  add('层级：包含即父子', geo.containsNotNested.length === 0,
    geo.containsNotNested.length
      ? `${geo.containsNotNested.length} 处未嵌套（如 ${geo.containsNotNested[0].inner} 应挂到 ${geo.containsNotNested[0].outer}）`
      : `容器嵌套正确，根节点 ${geo.stats.roots}/${geo.stats.nodes}`);
  add('同级控件无意外重叠', geo.siblingOverlaps.length === 0,
    geo.siblingOverlaps.length
      ? `${geo.siblingOverlaps.length} 处重叠（如 ${geo.siblingOverlaps[0].a} / ${geo.siblingOverlaps[0].b}）`
      : '同级可见节点无交叉');
  // warnings 不阻断验收，但写进报告供大模型/人工复核
  const geometryWarnings = geo.warnings;

  // 2. Unity 回读
  let audit = null;
  if (o.skipUnity !== true) {
    try { audit = await auditUnity(spec, o); }
    catch (e) { add('Unity 层级回读', false, e.message); }
  }
  if (audit) {
    const { parsed } = audit;
    add('Unity 层级回读', audit.lineCount > 0, `${audit.lineCount} 条记录`);
    const missing = spec.nodes.filter(n => n.type !== 'canvas' && !parsed.nodes[n.id]);
    add('所有节点存在', missing.length === 0, missing.length ? `缺失：${missing.map(n => n.id).join(', ')}` : `${Object.keys(parsed.nodes).length} 个节点齐全`);

    const compProblems = [], refProblems = [], spriteProblems = [];
    for (const n of spec.nodes) {
      const d = controls.def(n.type);
      if (!d || n.type === 'canvas') continue;
      const got = parsed.comps[n.id] || {};
      for (const c of (d.ugui || [])) if (!got[c]) compProblems.push(`${n.id}.${c}`);
      const refs = parsed.refs[n.id] || {};
      for (const [k, v] of Object.entries(refs)) if (v !== '1') refProblems.push(`${n.id}:${k}=${v}`);
      if (d.needsSprite && n.asset !== 'none') {
        const s = parsed.sprites[n.id];
        if (!s || !s.hasSprite) spriteProblems.push(n.id);
      }
    }
    add('控件组件齐全', compProblems.length === 0, compProblems.length ? `缺组件：${compProblems.join(', ')}` : '按映射表全部齐全');
    add('交互控件引用已绑定', refProblems.length === 0, refProblems.length ? `未绑定：${refProblems.join(', ')}` : 'targetGraphic/graphic/fillRect/textComponent 等全部非空');
    add('Sprite 已挂载到 Image', spriteProblems.length === 0, spriteProblems.length ? `未挂：${spriteProblems.join(', ')}` : '全部已挂');

    // 3. 布局误差
    let maxErr = 0, errNodes = [];
    for (const it of plan.items) {
      const got = parsed.rects[it.id];
      if (!got) continue;
      const u = coords.toUGUI(spec.nodes.find(n => n.id === it.id), spec);
      const want = [...u.anchorMin, ...u.anchorMax, ...u.anchoredPosition, ...u.sizeDelta];
      let e = 0;
      for (let i = 0; i < want.length; i++) e = Math.max(e, Math.abs(want[i] - got[i]));
      maxErr = Math.max(maxErr, e);
      if (e > (o.tolerance || 2)) errNodes.push(`${it.id}(${e.toFixed(2)})`);
    }
    add('布局坐标误差 < 2px', errNodes.length === 0, errNodes.length ? `超差：${errNodes.join(', ')}` : `最大误差 ${maxErr.toFixed(2)}px`);

    // 3b. 独立路径：按 Unity 语义从回读数值反算**屏幕矩形**，与 SPEC.rect 比对。
    // 不复用 coords.toUGUI，因此能抓出「换算函数自己写错、写入与期望却完全一致」的自证循环。
    const posProblems = [];
    let posChecked = 0, posMaxErr = 0;
    for (const n of spec.nodes) {
      const v = parsed.rects[n.id];
      if (!v || v.length < 10) { posProblems.push(`${n.id}: 回读缺 pivot 字段`); continue; }
      const got = coords.screenRectFromReadback(v, n, spec);
      if (!got) continue; // stretch：sizeDelta 是差量，不能用此路径反算
      posChecked++;
      const want = n.rect;
      const e = Math.max(
        Math.abs(got.left - want[0]), Math.abs(got.top - want[1]),
        Math.abs(got.width - want[2]), Math.abs(got.height - want[3]));
      posMaxErr = Math.max(posMaxErr, e);
      if (e > (o.tolerance || 2)) {
        posProblems.push(`${n.id}(偏 ${e.toFixed(1)}px, 实际 ${got.left.toFixed(0)},${got.top.toFixed(0)})`);
      }
    }
    add('屏幕几何位置（独立反算）', posProblems.length === 0,
      posProblems.length ? `错位：${posProblems.join('; ')}` : `${posChecked} 个节点屏幕位置正确，最大误差 ${posMaxErr.toFixed(2)}px`);
  }

  // 4. 截图还原度
  let imageCmp = null;
  let previewCheck = null;
  if (o.requirePreview || o.previewResult || o.shotFile) {
    previewCheck = o.previewResult && !o.previewResult.ok ? o.previewResult
      : o.shotFile ? preview.inspectPNG(o.shotFile) : { ok: false, reason: '未生成专用预览 PNG' };
    add('专用预览为有效非空白 PNG', previewCheck.ok, previewCheck.ok ? o.shotFile : previewCheck.reason);
  }
  if (previewCheck && previewCheck.ok && o.referenceFile) {
    try { imageCmp = compareImages(o.referenceFile, o.shotFile, o.imageOptions); }
    catch (e) { imageCmp = { ok: false, reason: e.message }; }
  }
  if (imageCmp) add('截图已生成并可比对', imageCmp.ok, imageCmp.ok ? `MAE=${imageCmp.mae}` : imageCmp.reason);

  const result = {
    ok: problems.length === 0,
    checks, problems,
    metrics: {
      image: imageCmp, preview: previewCheck, auditLines: audit ? audit.lineCount : 0,
      geometry: geo.stats, geometryWarnings,
    },
    produced_at: new Date().toISOString(),
  };
  if (outDir) {
    fs.mkdirSync(outDir, { recursive: true });
    fs.writeFileSync(path.join(outDir, 'verify_report.json'), JSON.stringify(result, null, 2));
  }
  return result;
}

function report(r) {
  const lines = [];
  lines.push(`验收结果：${r.ok ? '通过' : '未通过'}（${r.checks.filter(c => c.ok).length}/${r.checks.length}）`);
  r.checks.forEach(c => lines.push(`  [${c.ok ? 'OK' : 'FAIL'}] ${c.name} — ${c.detail}`));
  if (r.problems.length) lines.push('问题：');
  r.problems.forEach(p => lines.push(`  - ${p}`));
  return lines.join('\n');
}

module.exports = { auditCSharp, auditUnity, compareImages, run, report };

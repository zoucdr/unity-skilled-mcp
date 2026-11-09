/**
 * 离线自检 —— 不连 Unity、不写工程，验证技能的纯逻辑与 C# 生成是否自洽。
 *
 *   node scripts/check.js
 *
 * 覆盖：Spec 契约 / 坐标闭环 / 控件映射 / 切图 / C#5 预检 / 布局计划 /
 *       规则文档 / 文件完整性 / 可搬运性（代码里不得出现硬编码绝对路径）。
 */
'use strict';

const fs = require('fs');
const path = require('path');
const os = require('os');
const ROOT = path.resolve(__dirname, '..');

let pass = 0, fail = 0;
const failures = [];
function ok(name, cond, extra) {
  if (cond) { pass++; }
  else { fail++; failures.push(name + (extra ? ` — ${extra}` : '')); }
}
function eq(name, a, b) { ok(name, a === b, `期望 ${b}，实得 ${a}`); }
function throws(name, fn, re) {
  try { fn(); ok(name, false, '未抛错'); }
  catch (e) { ok(name, !re || re.test(e.message), e.message); }
}
function section(t) { console.log(`\n── ${t}`); }

const specMod = require('./spec');
const coords = require('./coords');
const controls = require('./controls');
const sliceMod = require('./slice');
const assets = require('./assets');
const build = require('./build');
const layout = require('./layout');
const rules = require('./rules');
const verify = require('./verify');
const mcp = require('./mcp');
const PNG = require('./png');

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'unity-ugui-check-'));
const cleanup = () => { try { fs.rmSync(tmp, { recursive: true, force: true }); } catch (e) { /* noop */ } };

// ---------------------------------------------------------------- 1 Spec
section('1. UI Spec 契约');
const baseSpec = {
  spec_version: '1.0', name: 'CheckUI',
  source: { image: 'ref.png', width: 1280, height: 720 },
  canvas: { reference_resolution: [1280, 720] },
  nodes: [
    { id: 'root', name: 'Panel', type: 'panel', rect: [100, 100, 400, 300], anchor: 'top_left', slice: [16, 16, 16, 16] },
    { id: 'btn', name: 'BtnOK', type: 'button', parent: 'root', rect: [140, 140, 120, 48], text: 'OK', anchor: 'middle_center' },
    { id: 'sld', name: 'Sld', type: 'slider', parent: 'root', rect: [140, 210, 200, 24], value: 0.4 },
  ],
  uncertainties: [],
};
const S = specMod.normalize(baseSpec);
ok('合法 Spec 通过校验', S.ok, S.problems.join('; '));
eq('节点数', S.spec && S.spec.nodes.length, 3);
eq('嵌套/父引用归一化 parent', S.spec && S.spec.nodes[1].parent, 'root');
eq('默认 anchor', S.spec && S.spec.nodes[2].anchor, 'middle_center');
eq('默认 asset', S.spec && S.spec.nodes[0].asset, 'crop');

ok('缺 name 被拒', !specMod.normalize(Object.assign({}, baseSpec, { name: '' })).ok);
ok('rect 非四元组被拒', !specMod.normalize(Object.assign({}, baseSpec, { nodes: [{ id: 'a', type: 'panel', rect: [1, 2, 3] }] })).ok);
ok('未知 anchor 被拒', !specMod.normalize(Object.assign({}, baseSpec, { nodes: [{ id: 'a', type: 'panel', rect: [1, 2, 3, 4], anchor: 'nope' }] })).ok);
ok('未知控件类型被拒', !specMod.normalize(Object.assign({}, baseSpec, { nodes: [{ id: 'a', type: 'ufo', rect: [1, 2, 3, 4] }] })).ok);
ok('重复 id 被拒', !specMod.normalize(Object.assign({}, baseSpec, { nodes: [
  { id: 'a', type: 'panel', rect: [1, 2, 3, 4] }, { id: 'a', type: 'panel', rect: [5, 6, 7, 8] },
] })).ok);
ok('parent 不存在被拒', !specMod.normalize(Object.assign({}, baseSpec, { nodes: [
  { id: 'a', type: 'panel', rect: [1, 2, 3, 4], parent: 'ghost' },
] })).ok);
ok('嵌套 children 展平', (() => {
  const r = specMod.normalize(Object.assign({}, baseSpec, { nodes: [
    { id: 'p', type: 'panel', rect: [0, 0, 100, 100], children: [
      { id: 'c', type: 'label', rect: [10, 10, 50, 20], text: 'x' },
    ] },
  ] }));
  return r.ok && r.spec.nodes.length === 2 && r.spec.nodes[1].parent === 'p';
})());
ok('低置信度给警告', specMod.normalize(Object.assign({}, baseSpec, { nodes: [
  { id: 'a', type: 'panel', rect: [1, 2, 3, 4], confidence: 0.3 },
] })).warnings.some(w => w.indexOf('置信度') >= 0));

// ---------------------------------------------------------------- 2 坐标
section('2. 坐标唯一收口');
const node = S.spec.nodes[1];
const u = coords.toUGUI(node, S.spec);
eq('UGUI anchorMin.x', u.anchorMin[0], 0.5);
eq('UGUI anchorMin.y', u.anchorMin[1], 0.5);
ok('sizeDelta 等于 rect 宽高', Math.abs(u.sizeDelta[0] - 120) < 1e-6 && Math.abs(u.sizeDelta[1] - 48) < 1e-6);
const rt = coords.roundTrip(node, S.spec);
ok('rect → UGUI → rect 闭环误差 < 1e-6', rt &&
  Math.abs(rt.left - 140) < 1e-6 && Math.abs(rt.top - 140) < 1e-6 &&
  Math.abs(rt.width - 120) < 1e-6 && Math.abs(rt.height - 48) < 1e-6,
  JSON.stringify(rt));
const sg = coords.toUISourceGen(node);
ok('UISourceGen center rect 语义正确（x-w/2=left, y-h/2=top）',
  Math.abs(sg.rect[0] - sg.rect[2] / 2 - 140) < 1e-6 &&
  Math.abs(sg.rect[1] - sg.rect[3] / 2 - 140) < 1e-6);
ok('UISourceGen 反算回到 SPEC.rect', (() => {
  const r = coords.fromUISourceGen({ rect: sg.rect });
  return Math.abs(r[0] - 140) < 1e-6 && Math.abs(r[1] - 140) < 1e-6;
})());
const topLeft = coords.toUGUI(S.spec.nodes[0], S.spec);
eq('top_left 锚点 anchorMin.y=1', topLeft.anchorMin[1], 1);
ok('top_left 的 anchoredPosition.y 为负（内容在锚点下方）', topLeft.anchoredPosition[1] < 0, String(topLeft.anchoredPosition[1]));
const stretched = coords.toUGUI({ rect: [10, 10, 300, 200], anchor: 'stretch' }, S.spec);
ok('stretch 产生 offsetMin/offsetMax', stretched.stretch && !!stretched.offsetMin && !!stretched.offsetMax);
eq('stretch anchorMax', stretched.anchorMax[0], 1);
ok('doLayout 向量一律 3 个数', [u.doLayout.anchored_pos, u.doLayout.size_delta, u.doLayout.anchor_min,
  u.doLayout.anchor_max, u.doLayout.pivot].every(v => v.length === 3));
ok('分辨率缩放生效', (() => {
  const s2 = specMod.normalize(Object.assign({}, baseSpec, {
    canvas: { reference_resolution: [640, 360] },
    nodes: [{ id: 'a', type: 'panel', rect: [100, 100, 400, 300] }],
  })).spec;
  const u2 = coords.toUGUI(s2.nodes[0], s2);
  return Math.abs(u2.sizeDelta[0] - 200) < 1e-6 && Math.abs(u2.sizeDelta[1] - 150) < 1e-6;
})());
const cr = coords.toCropRect({ rect: [-10, -10, 50, 50] }, S.spec);
ok('裁切 clamp 到图内', cr.x === 0 && cr.y === 0 && cr.w === 40 && cr.h === 40 && cr.clipped);

// ---------------------------------------------------------------- 3 控件
section('3. 控件映射');
ok('全部类型都有定义', controls.listTypes().length >= 16);
for (const t of controls.listTypes()) {
  const d = controls.def(t);
  ok(`类型 ${t} 有 ugui/uisourcegen/requiredRefs 定义`, !!d && Array.isArray(d.requiredRefs) && d.label);
}
ok('Slider 需要真组件（fillRect/handleRect/targetGraphic）', controls.needsRealBuild('slider'));
ok('Toggle 需要真组件（graphic）', controls.needsRealBuild('toggle'));
ok('InputField 需要真组件（textComponent/placeholder）', controls.needsRealBuild('input'));
ok('ScrollRect 需要真组件（content/viewport）', controls.needsRealBuild('scroll'));
ok('Dropdown 需要真组件', controls.needsRealBuild('dropdown'));
ok('panel 不强制真组件', !controls.needsRealBuild('panel'));
ok('scroll/dropdown 无法用 create_component 表达', controls.TO_MODULE.scroll === null && controls.TO_MODULE.dropdown === null);
const screen = controls.toScreen(S.spec);
ok('toScreen 生成合法 screen', screen.width === 1280 && screen.controls.length === 3);
ok('toScreen 控件 rect 统一 center 语义', screen.controls.every(c => c.anchor === 'center' && c.rect.length === 4));

// ---------------------------------------------------------------- 4 切图
section('4. 切图');
{
  const W = 64, H = 48;
  const pix = Buffer.alloc(W * H * 4);
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    const d = (y * W + x) * 4;
    pix[d] = x * 4; pix[d + 1] = y * 4; pix[d + 2] = 128; pix[d + 3] = 255;
  }
  const imgFile = path.join(tmp, 'ref.png');
  PNG.writePNG(imgFile, W, H, pix, { alpha: true });
  const s = specMod.normalize({
    name: 'CropUI', source: { image: imgFile, width: W, height: H },
    nodes: [{ id: 'a', type: 'panel', rect: [10, 10, 20, 20], slice: [4, 4, 4, 4] }],
  }).spec;
  const r = sliceMod.slice(s, { imageFile: imgFile, outDir: path.join(tmp, 'sprites') });
  ok('裁切成功', r.ok, r.problems.join('; '));
  eq('manifest 条目数', r.manifest.items.length, 1);
  const cut = PNG.readPNG(r.manifest.items[0].path);
  ok('裁切尺寸精确', cut.w === 20 && cut.h === 20);
  const rgba = sliceMod.toRGBA(cut);
  ok('裁切像素取自正确位置', rgba[0] === 40 && rgba[1] === 40, `got ${rgba[0]},${rgba[1]}`);
  ok('越界裁切给出警告', sliceMod.slice(s, {
    imageFile: imgFile, outDir: path.join(tmp, 'sprites2'),
  }).ok);
  const a = sliceMod.audit(r.manifest);
  ok('audit 识别非纯色图', a.items.length === 1 && a.items[0].flat === false);
  ok('manifest 落盘', fs.existsSync(r.manifestFile));
}

// ---------------------------------------------------------------- 5 C# 生成
section('5. C# 生成与静态预检');
{
  const manifest = { items: [
    { id: 'root', path: path.join(tmp, 'root.png'), slice: [4, 4, 4, 4], states: { normal: path.join(tmp, 'root.png') } },
    { id: 'btn', path: path.join(tmp, 'btn.png'), slice: [0, 0, 0, 0], states: { normal: path.join(tmp, 'btn.png') } },
  ] };
  const code = build.generateCSharp(S.spec, manifest, { project: tmp, saveScene: false });
  ok('build C# 通过 lint', (() => { try { mcp.assertLint(code); return true; } catch (e) { return false; } })(),
    mcp.lintCSharp(code).filter(p => p.level === 'error').map(p => `L${p.line} ${p.rule}`).join(','));
  ok('build C# 无字符串插值', !/\$"/.test(code));
  ok('build C# 无 ?. / nameof / out var', !/\?\.|\bnameof\b|\bout var\b/.test(code));
  ok('build C# 含 CanvasScaler', code.indexOf('CanvasScaler') >= 0);
  ok('build C# 绑定 Button.targetGraphic', code.indexOf('.targetGraphic = ') >= 0);
  ok('build C# 绑定 Slider.fillRect/handleRect', code.indexOf('.fillRect = ') >= 0 && code.indexOf('.handleRect = ') >= 0);
  ok('build C# 幂等销毁同名根', code.indexOf('DestroyImmediate') >= 0);

  const importCode = assets.generateImportCSharp([['Assets/UI/a.png', [4, 4, 4, 4]]]);
  ok('import C# 通过 lint', (() => { try { mcp.assertLint(importCode); return true; } catch (e) { return false; } })());
  ok('import C# 写 spriteBorder', importCode.indexOf('spriteBorder') >= 0);

  const auditCode = verify.auditCSharp(S.spec, path.join(tmp, 'audit.txt'));
  ok('verify C# 通过 lint', (() => { try { mcp.assertLint(auditCode); return true; } catch (e) { return false; } })(),
    mcp.lintCSharp(auditCode).filter(p => p.level === 'error').map(p => `L${p.line} ${p.rule}`).join(','));
  ok('verify C# 不引用未定义组件变量', (() => {
    // slider 节点不应出现 Button.targetGraphic 的引用
    const idx = auditCode.indexOf('ref|sld|');
    const seg = idx >= 0 ? auditCode.slice(idx, idx + 400) : '';
    return seg.indexOf('g_sld_Button') < 0;
  })());

  const rich = specMod.normalize({
    name: 'Rich', source: { image: 'x.png', width: 800, height: 600 },
    nodes: [
      { id: 'inp', type: 'input', rect: [10, 10, 200, 40], text: 'hi' },
      { id: 'scr', type: 'scroll', rect: [10, 60, 300, 200] },
      { id: 'dd', type: 'dropdown', rect: [10, 280, 200, 40], items: ['A', 'B'] },
      { id: 'tgl', type: 'toggle', rect: [10, 340, 60, 30] },
      { id: 'prg', type: 'progress', rect: [10, 390, 200, 20], value: 0.7 },
      { id: 'lbl', type: 'label', rect: [10, 430, 100, 30], text: 'T' },
    ],
  }).spec;
  const richCode = build.generateCSharp(rich, { items: [] }, { project: tmp, saveScene: false });
  ok('全控件类型生成通过 lint', (() => { try { mcp.assertLint(richCode); return true; } catch (e) { return false; } })(),
    mcp.lintCSharp(richCode).filter(p => p.level === 'error').map(p => `L${p.line} ${p.rule}`).join(','));
  ok('全控件 C# 绑定 InputField.textComponent', richCode.indexOf('.textComponent = ') >= 0);
  ok('全控件 C# 绑定 ScrollRect.content/viewport', richCode.indexOf('.content = ') >= 0 && richCode.indexOf('.viewport = ') >= 0);
  ok('全控件 C# 绑定 Dropdown.template', richCode.indexOf('.template = ') >= 0);
  ok('全控件 C# 绑定 Toggle.graphic', richCode.indexOf('.graphic = ') >= 0);
}

// ---------------------------------------------------------------- 6 布局/规则/验收
section('6. 布局计划 / 规则 / 验收报告');
{
  const p = layout.plan(S.spec);
  eq('布局计划条目数', p.items.length, 3);
  ok('路径含根节点', p.items.every(i => i.path.indexOf(p.root + '/') === 0));
  ok('子节点路径含父名', p.items.find(i => i.id === 'btn').path.indexOf('Panel/') >= 0);
  ok('计划文件可落盘', (() => { const f = layout.savePlan(p, tmp); return fs.existsSync(f); })());
  const r = rules.compose(S.spec, { items: [{ id: 'root', path: '/tmp/a.png', slice: [1, 2, 3, 4], states: {} }] });
  ok('规则文档含节点映射', r.node_names.length === 3 && r.node_sprites.length === 1);
  ok('规则文档登记缺图节点', r.missing_sprites.length === 2);
  ok('规则文件可落盘', (() => { const f = rules.save(r, tmp); return fs.existsSync(f); })());
  const rep = verify.report({ ok: false, checks: [{ name: 'A', ok: true, detail: 'x' }, { name: 'B', ok: false, detail: 'y' }], problems: ['B: y'] });
  ok('验收报告可读', rep.indexOf('未通过') >= 0 && rep.indexOf('[FAIL] B') >= 0);
}

// ---------------------------------------------------------------- 7 可搬运性
section('7. 可搬运性 / 文件完整性');
{
  const must = [
    'SKILL.md', 'scripts/mcp.js', 'scripts/png.js', 'scripts/inspect.js',
    'scripts/spec.js', 'scripts/coords.js', 'scripts/controls.js', 'scripts/capabilities.js',
    'scripts/slice.js', 'scripts/assets.js', 'scripts/build.js', 'scripts/layout.js',
    'scripts/rules.js', 'scripts/verify.js', 'scripts/pipeline.js', 'scripts/check.js',
    'references/contract.md', 'references/tools.md', 'references/semantics.md', 'references/pipeline.md',
    'prompts/vision_to_spec.md', 'templates/ui_spec.schema.json', 'templates/ui_spec.example.json',
    'examples/login.spec.json', 'examples/run_login.js',
  ];
  must.forEach(f => ok(`存在 ${f}`, fs.existsSync(path.join(ROOT, f))));

  // 代码里的字符串字面量不得含硬编码绝对路径（注释里的调研证据允许）
  const stripComments = t => t.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/\/\/.*$/gm, '');
  const codeFiles = fs.readdirSync(path.join(ROOT, 'scripts')).filter(f => f.endsWith('.js') && f !== 'check.js');
  const patterns = [/[A-Za-z]:[\\/]/, /\/Users\//, /\/home\//, /\/usr\//];
  const hits = [];
  codeFiles.forEach(f => {
    const text = stripComments(fs.readFileSync(path.join(ROOT, 'scripts', f), 'utf8'));
    // 排除「扫描器自身」与说明文字，只看带引号的字符串
    const strs = text.match(/(['"`])(?:\\.|(?!\1)[^\\])*\1/g) || [];
    strs.forEach(s => { if (patterns.some(p => p.test(s))) hits.push(`${f}: ${s}`); });
  });
  ok('脚本无硬编码绝对路径', hits.length === 0, hits.join('; '));

  // 扫描器自证有效
  ok('绝对路径扫描器能抓到植入样本', patterns.some(p => p.test('"E:/unity3d-skilled-mcp/demo"')));
}

section('8. 独立 UGUI 预览');
{
  const preview = require('./preview');
  const file = path.join(tmp, 'preview.png');
  const opt = { canvasPath: 'Canvas/界面"测试', shotFile: file, width: 32, height: 16 };
  const code = preview.generateCSharp(opt);
  ok('预览 C# 通过预检', (() => { try { mcp.assertLint(code); return true; } catch (_) { return false; } })());
  ok('预览使用隔离场景与相机', code.includes('NewPreviewScene') && code.includes('camera.scene = previewScene'));
  ok('预览处理 Overlay', code.includes('ScreenSpaceCamera'));
  ok('预览恢复 RT 并清理场景', code.includes('finally') && code.includes('RenderTexture.active = savedRT') && code.includes('ClosePreviewScene'));
  ok('预览不保存或标脏原场景', !/SaveScene|MarkSceneDirty|source\.renderMode\s*=(?!=)/.test(code));
  ok('预览只编码 PNG', code.includes('EncodeToPNG') && !code.includes('EncodeToJPG'));
  ok('自定义脚本在复制之前拒绝', code.indexOf('behaviour.GetType().Assembly') < code.indexOf('Object.Instantiate'));
  throws('拒绝无目标', () => preview.generateCSharp({ ...opt, canvasPath: '' }));
  throws('拒绝超大尺寸', () => preview.generateCSharp({ ...opt, width: 8192 }));
  throws('拒绝非 PNG 输出', () => preview.generateCSharp({ ...opt, shotFile: 'preview.jpg' }));
  fs.writeFileSync(file, Buffer.from([255, 216, 255, 224]));
  ok('拒绝伪装 PNG 的 JPEG', !preview.inspectPNG(file).ok);
  PNG.writePNG(file, 32, 16, Buffer.alloc(32 * 16 * 4));
  ok('拒绝全透明图', !preview.inspectPNG(file).ok);
  PNG.writePNG(file, 32, 16, Buffer.alloc(32 * 16 * 3));
  ok('拒绝纯黑图', !preview.inspectPNG(file).ok);
  PNG.writePNG(file, 32, 16, Buffer.alloc(32 * 16 * 3, 255));
  ok('拒绝纯白图', !preview.inspectPNG(file).ok);
  const pixels = Buffer.alloc(32 * 16 * 4, 255);
  for (let i = 0; i < 32 * 16; i++) pixels[i * 4] = i % 256;
  PNG.writePNG(file, 32, 16, pixels);
  ok('有效彩色 PNG 通过', preview.inspectPNG(file, opt).ok);
  ok('错误尺寸不通过', !preview.inspectPNG(file, { width: 99 }).ok);
  ok('缺文件不通过', !preview.inspectPNG(path.join(tmp, 'missing.png')).ok);
  ok('流水线不再依赖旧截图', !fs.readFileSync(path.join(__dirname, 'pipeline.js'), 'utf8').includes('game_view.screenshot'));
}

section('9. 坐标透视 / 层级几何（回归：贴边偏移与自证循环）');
{
  const geometry = require('./geometry');
  const tl = coords.toUGUI(S.spec.nodes[0], S.spec); // top_left, rect=[100,100,400,300]
  ok('top_left 的 anchoredPosition 用 pivot 角点，不是矩形中心',
    Math.abs(tl.anchoredPosition[0] - 100) < 1e-6 && Math.abs(tl.anchoredPosition[1] + 100) < 1e-6,
    JSON.stringify(tl.anchoredPosition));
  const tlRt = coords.roundTrip(S.spec.nodes[0], S.spec);
  ok('修复后 top_left 闭环仍回到 SPEC.rect',
    tlRt && Math.abs(tlRt.left - 100) < 1e-6 && Math.abs(tlRt.top - 100) < 1e-6, JSON.stringify(tlRt));

  // 独立反算：给定正确回读值能还原；给定「中心当 pivot」的错误值必须被识破
  const readGood = [0, 1, 0, 1, 100, -100, 400, 300, 0, 1];
  const readBad = [0, 1, 0, 1, 300, -250, 400, 300, 0, 1];
  const goodRect = coords.screenRectFromReadback(readGood, S.spec.nodes[0], S.spec);
  const badRect = coords.screenRectFromReadback(readBad, S.spec.nodes[0], S.spec);
  ok('独立反算：正确回读值还原 SPEC.rect',
    goodRect && Math.abs(goodRect.left - 100) < 1e-6 && Math.abs(goodRect.top - 100) < 1e-6, JSON.stringify(goodRect));
  ok('独立反算：能识破「中心当 pivot」造成的半宽半高偏移',
    badRect && (Math.abs(badRect.left - 100) > 1 || Math.abs(badRect.top - 100) > 1), JSON.stringify(badRect));
  ok('stretch 回读不做错误反算', coords.screenRectFromReadback(
    [0, 0, 1, 1, 0, 0, 0, 0, 0.5, 0.5], S.spec.nodes[0], S.spec) === null);

  // 嵌套：子节点 anchoredPosition 必须相对父容器，roundTrip 仍回绝对坐标
  const child = S.spec.nodes[1]; // parent=root, middle_center, rect=[140,140,120,48]
  const cu = coords.toUGUI(child, S.spec);
  const crt = coords.roundTrip(child, S.spec);
  ok('嵌套节点坐标相对父容器', Math.abs(cu.anchoredPosition[0] + 100) < 1e-6 && Math.abs(cu.anchoredPosition[1] - 86) < 1e-6,
    JSON.stringify(cu.anchoredPosition));
  ok('嵌套节点闭环仍回到绝对 SPEC.rect',
    crt && Math.abs(crt.left - 140) < 1e-6 && Math.abs(crt.top - 140) < 1e-6, JSON.stringify(crt));

  // 层级几何检查
  const flat = specMod.normalize(Object.assign({}, baseSpec, { nodes: [
    { id: 'p', type: 'panel', rect: [0, 0, 200, 100] },
    { id: 'c', type: 'label', rect: [10, 10, 50, 20], text: 'x' },
  ] })).spec;
  const gFlat = geometry.audit(flat);
  ok('几何检查抓到「包含但未嵌套」', gFlat.containsNotNested.length === 1 && gFlat.suggestedParents.c === 'p',
    JSON.stringify(gFlat.containsNotNested));
  const nested = specMod.normalize(Object.assign({}, baseSpec, { nodes: [
    { id: 'p', type: 'panel', rect: [0, 0, 200, 100] },
    { id: 'c', type: 'label', rect: [10, 10, 50, 20], text: 'x', parent: 'p' },
  ] })).spec;
  ok('嵌套后几何检查通过', geometry.audit(nested).ok);
  const cross = specMod.normalize(Object.assign({}, baseSpec, { nodes: [
    { id: 'a', type: 'panel', rect: [0, 0, 100, 50] },
    { id: 'b', type: 'panel', rect: [50, 25, 100, 50] },
  ] })).spec;
  ok('几何检查抓到同级交叉重叠', geometry.audit(cross).siblingOverlaps.length === 1);
  const allowed = specMod.normalize(Object.assign({}, baseSpec, { nodes: [
    { id: 'a', type: 'panel', rect: [0, 0, 100, 50] },
    { id: 'b', type: 'badge', rect: [50, 25, 100, 50], allow_overlap: true },
  ] })).spec;
  ok('allow_overlap 显式豁免生效', geometry.audit(allowed).siblingOverlaps.length === 0);
  ok('平铺率过高给出提示', geometry.audit(flat).stats.flatRatio === 1);

  // sibling 序号必须按父分组，不能用全局 order
  const code = build.generateCSharp(nested, { items: [] }, { project: tmp, saveScene: false });
  const idx = [...code.matchAll(/SetSiblingIndex\((\d+)\)/g)].map(m => Number(m[1]));
  ok('同父 sibling 序号从 0 连续递增', idx.length >= 2 && idx[0] === 0 && idx[1] === 0,
    JSON.stringify(idx));
}

cleanup();
console.log(`\n通过 ${pass} / 失败 ${fail}`);
if (fail) { failures.forEach(f => console.log(`  FAIL ${f}`)); process.exitCode = 1; }

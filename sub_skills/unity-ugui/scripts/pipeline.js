/**
 * 编排入口 —— 效果图 → UI Spec → 切图 → UGUI 搭建 → 布局 → 规则 → 验收。
 *
 * 用法：
 *   node scripts/pipeline.js --spec examples/login.spec.json [--project PATH] [--instance ID]
 *                            [--out DIR] [--stage all|assets|build|layout|rules|verify]
 *                            [--dry-run] [--no-shot] [--no-unity-push]
 *
 * 设计约定（沿用 unity-modeling 的分阶段幂等）：
 *  - 每阶段独立可重跑，状态写 stage_state.json；中断后从 --stage 续跑，不盲目重放整段序列。
 *  - 写操作响应丢失 ≠ 未执行：失败时先只读核实，pipeline 不自动重试同一写调用。
 *  - --dry-run 只做能力发现 + Spec 校验 + 计划生成，不碰 Unity，用于交付前自检。
 *  - 任何能力缺失都走降级并在报告里明写，不假装模块可用。
 */
'use strict';

const fs = require('fs');
const path = require('path');
const mcp = require('./mcp');
const specMod = require('./spec');
const caps = require('./capabilities');
const assets = require('./assets');
const slice = require('./slice');
const build = require('./build');
const layout = require('./layout');
const rules = require('./rules');
const verify = require('./verify');
const preview = require('./preview');
const geometry = require('./geometry');

const STAGES = ['capabilities', 'assets', 'build', 'layout', 'rules', 'verify'];

function parseArgs(argv) {
  const o = { stage: 'all', dryRun: false, noShot: false, noUnityPush: false, supersample: 2 };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--dry-run') o.dryRun = true;
    else if (a === '--no-shot') o.noShot = true;
    else if (a === '--no-unity-push') o.noUnityPush = true;
    else if (a.startsWith('--') && i + 1 < argv.length && !argv[i + 1].startsWith('--')) o[a.slice(2).replace(/-([a-z])/g, (_, c) => c.toUpperCase())] = argv[++i];
    else if (a.startsWith('--')) o[a.slice(2).replace(/-([a-z])/g, (_, c) => c.toUpperCase())] = true;
  }
  return o;
}

function resolveImage(spec, project, explicit) {
  const p = explicit || spec.source.image;
  if (!p) return null;
  const abs = path.isAbsolute(p) ? p : path.resolve(project, p);
  return fs.existsSync(abs) ? abs : null;
}

async function main() {
  const o = parseArgs(process.argv.slice(2));
  if (!o.spec) {
    console.error('用法：node scripts/pipeline.js --spec <ui_spec.json> [--project PATH] [--out DIR] [--dry-run]');
    process.exitCode = 2; return;
  }
  const project = o.project || mcp.resolveProject();
  mcp.configure({ project, instance_id: o.instance, timeout: 180 });

  const specFile = path.resolve(o.spec);
  const loaded = specMod.load(specFile);
  if (!loaded.ok) {
    console.error('UI Spec 校验失败：\n  - ' + loaded.problems.join('\n  - '));
    process.exitCode = 2; return;
  }
  const spec = loaded.spec;
  loaded.warnings.forEach(w => console.error(`[spec] 警告：${w}`));

  const outDir = o.out ? path.resolve(o.out) : path.resolve(project, `Assets/AI-Generate/UGUI/${spec.name}`);
  fs.mkdirSync(outDir, { recursive: true });
  const stateFile = path.join(outDir, 'stage_state.json');
  const state = fs.existsSync(stateFile) ? JSON.parse(fs.readFileSync(stateFile, 'utf8')) : {};
  const save = () => fs.writeFileSync(stateFile, JSON.stringify(state, null, 2));
  const reportPath = path.join(outDir, 'pipeline_report.json');
  const report = { screen: spec.name, project: path.basename(project), started_at: new Date().toISOString(), stages: {}, warnings: [], problems: [] };
  const flush = () => fs.writeFileSync(reportPath, JSON.stringify(report, null, 2));
  const want = s => o.stage === 'all' ? true : STAGES.indexOf(s) >= STAGES.indexOf(o.stage);

  console.log(`[unity-ugui] ${spec.name} | 工程=${project} | 产物=${outDir}${o.dryRun ? ' | DRY-RUN' : ''}`);

  // ---- S0a 层级 / 重叠几何自检（纯计算，建 UI 之前就拦住平铺与压盖）----
  const geo = geometry.audit(spec);
  fs.writeFileSync(path.join(outDir, 'hierarchy_report.json'),
    JSON.stringify(Object.assign({ screen: spec.name, produced_at: new Date().toISOString() }, geo), null, 2));
  report.geometry = geo.stats;
  report.problems.push(...geo.problems);
  report.warnings.push(...geo.warnings);
  if (geo.problems.length) {
    console.error(`[S0a] 层级/重叠检查不通过（${geo.problems.length} 项），先看 hierarchy_report.json：`);
    geo.problems.forEach(p => console.error('  - ' + p));
    if (o.dryRun) { flush(); process.exitCode = 3; return; }
  } else {
    console.log(`[S0a] 层级/重叠检查通过：${geo.stats.roots}/${geo.stats.nodes} 根节点，同级重叠 ${geo.stats.siblingOverlaps}`);
  }

  // ---- S0 能力发现 ----
  let capability = state.capability;
  if (!capability || want('capabilities')) {
    capability = await caps.detect({ project, instance_id: o.instance, verbose: !!o.verbose });
    state.capability = capability; save();
  }
  caps.save(capability, outDir);
  report.capabilities = capability.strategies;
  report.warnings.push(...capability.warnings);
  console.log('[S0] 策略=' + JSON.stringify(capability.strategies));
  capability.warnings.forEach(w => console.warn(`      降级：${w}`));
  if (o.dryRun) {
    const p = layout.plan(spec);
    layout.savePlan(p, outDir);
    fs.writeFileSync(path.join(outDir, 'ui_spec.normalized.json'), JSON.stringify(spec, null, 2));
    report.stages.dryRun = { planItems: p.items.length, specNodes: spec.nodes.length };
    flush();
    console.log(`[DRY-RUN] Spec ${spec.nodes.length} 节点 / 布局计划 ${p.items.length} 项已生成，未改动 Unity。`);
    return;
  }

  // ---- S1 图资源 ----
  let manifest = null;
  if (want('assets')) {
    const imgFile = resolveImage(spec, project, o.image);
    const strategy = o.assets || capability.strategies.assets;
    let res;
    if (strategy === 'regenerate') {
      res = await assets.regenerate(spec, { project, outputDirRel: `Assets/AI-Generate/UGUI/${spec.name}`, baseName: 'Generated', supersample: o.supersample });
    } else {
      if (!imgFile) throw new Error(`效果图不存在：${spec.source.image}；请用 --image 指定绝对路径`);
      res = assets.fromReference(spec, { imageFile: imgFile, outDir: path.join(outDir, 'Sprites'), overwrite: true });
    }
    report.stages.assets = { strategy, warnings: res.warnings, problems: res.problems, items: res.manifest ? res.manifest.items.length : 0 };
    report.warnings.push(...res.warnings); report.problems.push(...res.problems);
    if (!res.ok) { flush(); throw new Error('图资源阶段失败：' + res.problems.join('; ')); }
    manifest = res.manifest;
    state.manifestFile = res.manifestFile; save();
    const imp = await assets.importSprites(manifest, { project, assetDirRel: `Assets/AI-Generate/UGUI/${spec.name}/Sprites` });
    report.stages.assets.imported = imp.count;
    console.log(`[S1] 图资源 ${manifest.items.length} 张，导入 Sprite ${imp.count} 个（含九宫格 border）`);
    flush();
  } else if (state.manifestFile && fs.existsSync(state.manifestFile)) {
    manifest = JSON.parse(fs.readFileSync(state.manifestFile, 'utf8'));
  }

  // ---- S2 搭建 ----
  if (want('build')) {
    const strategy = o.build || capability.strategies.build;
    if (strategy === 'code') {
      const r = await build.build(spec, manifest, { project, saveScene: true });
      fs.writeFileSync(path.join(outDir, 'build_ugui.generated.cs'), r.code);
      report.stages.build = { strategy: 'code', nodes: r.nodeCount };
    } else if (strategy === 'module') {
      const r = await build.buildViaModule(spec);
      report.stages.build = { strategy: 'module', nodes: r.nodeCount, warnings: r.warnings };
      report.warnings.push(...r.warnings);
    } else throw new Error('没有可用的 UGUI 搭建能力（code.execute / ugui.create_component 均缺失）');
    console.log(`[S2] 搭建完成，策略=${strategy}`);
    flush();
  }

  // ---- S3 布局 ----
  const p = layout.plan(spec);
  layout.savePlan(p, outDir);
  if (want('layout') && capability.strategies.layout === 'module') {
    const applied = await layout.apply(p);
    const bad = applied.filter(a => a.problems.length);
    report.stages.layout = { strategy: 'module', applied: applied.length, problems: bad.flatMap(b => b.problems) };
    report.problems.push(...report.stages.layout.problems);
    const rb = await layout.readback(p, { outDir });
    report.stages.layout.readbackFile = rb.file;
    console.log(`[S3] 官方布局工具已应用 ${applied.length} 节点，回读 ${rb.items.length} 条`);
  } else {
    report.stages.layout = { strategy: capability.strategies.layout === 'module' ? 'skipped' : 'code-in-build', planItems: p.items.length };
    console.log(`[S3] 布局计划 ${p.items.length} 项（${report.stages.layout.strategy}）`);
  }
  flush();

  // ---- S4 结构化规则 ----
  if (want('rules')) {
    const r = rules.compose(spec, manifest, { desc: `由效果图 ${spec.source.image} 生成 ${spec.name}` });
    const f = rules.save(r, outDir);
    report.stages.rules = { file: f, nodes: r.node_names.length, sprites: r.node_sprites.length, missing_sprites: r.missing_sprites };
    if (capability.strategies.rules === 'module' && !o.noUnityPush) {
      const pushed = await rules.pushToUnity(r, { ruleName: spec.name });
      report.stages.rules.unity = { pushed: pushed.pushed, warnings: pushed.warnings };
      report.warnings.push(...pushed.warnings);
    }
    console.log(`[S4] ui_rules.json 已落盘（${r.node_names.length} 节点 / ${r.node_sprites.length} 图片）`);
    flush();
  }

  // ---- S5 验收 ----
  if (want('verify')) {
    let shotFile = null, previewResult = null;
    if (!o.noShot) {
      try {
        previewResult = await preview.capture({
          project, instance_id: o.instance, canvasPath: p.root,
          width: spec.source.width, height: spec.source.height,
          shotFile: path.join(outDir, 'ugui_preview.png'),
        });
        shotFile = previewResult.shotFile;
      } catch (e) { previewResult = { ok: false, reason: e.message }; }
      report.stages.preview = previewResult;
    }
    const v = await verify.run(spec, {
      outDir, manifest, plan: p, previewResult, requirePreview: !o.noShot,
      shotFile, referenceFile: shotFile ? resolveImage(spec, project, o.image) : null,
      tolerance: Number(o.tolerance || 2),
    });
    report.stages.verify = v;
    report.problems.push(...v.problems);
    console.log(verify.report(v));
    flush();
  }

  report.finished_at = new Date().toISOString();
  report.ok = report.problems.length === 0;
  flush();
  console.log(`[unity-ugui] ${report.ok ? '完成' : '完成（有问题待修）'}，报告：${reportPath}`);
  if (report.problems.length) process.exitCode = 1;
}

main().catch(e => { console.error(`[unity-ugui] 流水线中断：${e.message}`); process.exitCode = 1; });

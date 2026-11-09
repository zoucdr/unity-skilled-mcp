/**
 * 能力发现与降级矩阵。
 *
 * 实测事实（2026-11-09，两个 Unity 工程对比）：
 *  - unity-package/Editor/Tools/UI 里的 ugui.do_layout / get_layout / tattoo 属于「包内工具」，
 *    任何装了包的工程都有（D:\WorkSpace\tdshoweditor 实测存在）。
 *  - demo/Assets/Extends/CustomTools/ 下的 uisourcegen.* / ugui.generate / ugui.create_component /
 *    ui_rule.* / figma.* 是 demo 工程私有扩展，换工程即消失（tdshoweditor 实测 find 返回 0）。
 *  - 工具名由 [ToolModule(prefix)] + [ToolAction(action)] 拼成 prefix.action，
 *    ToolCatalog.cs:205-209；官方提示词里的 figma_manage / ugui_layout / ui_rule_manage 全是过时别名。
 *
 * 所以技能启动必须做能力发现，按结果决定每条子链走「真模块」还是「降级路径」，
 * 并把结论写进 capabilities.json 供后续阶段和交付报告引用。禁止凭目录存在就假定工具可用。
 */
'use strict';

const fs = require('fs');
const mcp = require('./mcp');

/** 需要探测的工具，按能力分组。名字全部来自实测源码/活服务 find。 */
const GROUPS = {
  design: ['figma.fetch_document', 'figma.download_images', 'figma.preview', 'figma.get_conversion_rules'],
  assets: ['uisourcegen.render', 'uisourcegen.batch', 'uisourcegen.templates', 'uisourcegen.spec', 'uisourcegen.draw'],
  create: ['ugui.generate', 'ugui.create_component', 'ugui.list_templates'],
  layout: ['ugui.do_layout', 'ugui.get_layout', 'ugui.tattoo'],
  rules: ['ui_rule.record_renames', 'ui_rule.record_download_sprites', 'ui_rule.get_download_sprites', 'ui_rule.record_modify'],
  core: ['code.execute', 'scene.get_hierarchy', 'scene.get_active', 'hierarchy.find_by_query', 'game_view.screenshot', 'camera.screenshot'],
};

const QUERIES = ['figma', 'uisourcegen', 'ugui', 'ui_rule', 'code.execute', 'scene.get_hierarchy', 'scene.get_active', 'game_view.screenshot', 'camera.screenshot', 'hierarchy.find_by_query'];

/** 找到并加载当前生效的 unity-skilled-mcp 客户端（不写死盘符/工程）。 */
function bridge() {
  for (const f of mcp.loaderCandidates()) {
    if (fs.existsSync(f)) return require(f);
  }
  throw new Error('找不到 unity-skilled-mcp 客户端；请检查 skill.ini 或 UNITY_MCP_LOADER');
}

/**
 * 探测活服务里真实注册的工具名集合。
 * 目录查询走 find（按名字过滤），不取能力全集详情，符合技能契约。
 */
async function detect(opts = {}) {
  const b = bridge();
  const project = opts.project || mcp.resolveProject();
  if (opts.verbose) console.error(`[capabilities] project=${project}`);
  const cfg = { expected_project: project };
  if (opts.instance_id) cfg.instance_id = opts.instance_id;
  if (b.configure) b.configure(cfg);

  const present = new Set();
  for (const q of QUERIES) {
    let res;
    try { res = await b.findCapabilities(q, 'tools'); }
    catch (e) { if (opts.verbose) console.error(`[capabilities] find(${q}) 失败: ${e.message}`); continue; }
    const matches = (res && (res.matches || res.tools)) || [];
    matches.forEach(m => present.add(typeof m === 'string' ? m : m.name));
  }

  const groups = {};
  for (const [g, names] of Object.entries(GROUPS)) {
    groups[g] = {
      present: names.filter(n => present.has(n)),
      missing: names.filter(n => !present.has(n)),
    };
  }

  const has = n => present.has(n);
  const strategies = {
    // 设计稿解析：Figma 结构化节点 > 大模型视觉解析效果图（本技能 prompts/vision_to_spec.md）
    design: has('figma.fetch_document') ? 'figma' : 'vision',
    // 图资源：UISourceGen 重绘 > 从效果图裁切（Node 零依赖）> 外部 PNG
    assets: has('uisourcegen.render') ? 'regenerate' : 'crop',
    // 搭建：code.execute 建真组件（能绑 Slider/Toggle/InputField 引用）> 模块 create_component（残缺）
    build: has('code.execute') ? 'code' : (has('ugui.create_component') ? 'module' : 'unavailable'),
    // 布局：ugui.do_layout/tattoo > C# 直接写 RectTransform
    layout: has('ugui.do_layout') ? 'module' : (has('code.execute') ? 'code' : 'unavailable'),
    // 规则记录：ui_rule 资产 > 本地 rules.json
    rules: has('ui_rule.record_renames') ? 'module' : 'local',
    // 验收：回读层级 + 布局 + 截图
    verify: has('scene.get_hierarchy') && has('code.execute') ? 'full' : (has('code.execute') ? 'basic' : 'unavailable'),
  };

  const warnings = [];
  if (strategies.build === 'module') warnings.push('code.execute 不可用，只能走 ugui.create_component：Slider/Toggle/InputField/ScrollRect 会是残缺控件');
  if (strategies.assets === 'crop') warnings.push('uisourcegen.render 不可用，图资源改从效果图按 rect 裁切（保真度取决于效果图清晰度）');
  if (strategies.rules === 'local') warnings.push('ui_rule.* 不可用，节点映射与图片记录改落本地 rules.json');
  if (strategies.layout === 'code') warnings.push('ugui.do_layout 不可用，锚点改由 C# 直接写 RectTransform');
  if (strategies.design === 'vision') warnings.push('figma.* 不可用，需由大模型按 prompts/vision_to_spec.md 从效果图解析出 UI Spec');
  if (strategies.build === 'unavailable' || strategies.verify === 'unavailable') {
    warnings.push('核心能力缺失：无法完成 UGUI 搭建或验收，建议切换到装有完整扩展的 Unity 工程');
  }

  return {
    project,
    detected_at: new Date().toISOString(),
    tool_count: present.size,
    groups,
    strategies,
    warnings,
    tools: Array.from(present).sort(),
  };
}

/** 把 capabilities.json 写到产物目录；返回绝对路径。 */
function save(result, outDir) {
  const path = require('path');
  fs.mkdirSync(outDir, { recursive: true });
  const file = path.join(outDir, 'capabilities.json');
  fs.writeFileSync(file, JSON.stringify(result, null, 2));
  return file;
}

module.exports = { GROUPS, QUERIES, bridge, detect, save };

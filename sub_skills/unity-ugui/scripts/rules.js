/**
 * 规则/结构化信息阶段 —— 串联 demo 的 ui_rule.*，并始终落一份可交付的 ui_rules.json。
 *
 * ui_rule.* 真实位置是 demo/Assets/Extends/CustomTools/Figma/Editor/UIRuleManage.cs，
 * 前缀 ui_rule（不是官方提示词里写的 ui_rule_manage）。它把数据写进
 * UIDefineRuleObject.asset（ScriptableObject）：node_names / node_sprites / modify_records。
 * 实测：代码层没有下游消费者，消费方是 AI Agent 的构建步骤与编辑器 Inspector。
 *
 * 本技能把「结构化 JSON 信息」做成一等产物：无论 ui_rule 工具在不在，
 * 都会写出 ui_rules.json（节点映射、图片映射、修改记录、不确定项），
 * 供后续迭代、回归对比、人工复核使用。
 */
'use strict';

const fs = require('fs');
const path = require('path');
const mcp = require('./mcp');

/** 由 Spec + manifest 生成结构化规则文档。纯计算。 */
function compose(spec, manifest, o = {}) {
  const items = (manifest && manifest.items) || [];
  const assetById = Object.fromEntries(items.map(i => [i.id, i]));
  return {
    generator: 'unity-ugui/rules', version: '1.0',
    screen: spec.name,
    source_image: spec.source.image,
    reference_resolution: spec.canvas.reference_resolution,
    created_at: new Date().toISOString(),
    node_names: spec.nodes.map(n => ({
      id: n.id,
      name: n.name || n.id,
      originName: n.origin_name || n.name || n.id,
      type: n.type,
      parent: n.parent,
      rect: n.rect,
      anchor: n.anchor,
      confidence: n.confidence,
    })),
    node_sprites: items.map(i => ({
      id: i.id,
      fileName: path.basename(i.path),
      path: i.path,
      slice: i.slice,
      states: Object.fromEntries(Object.entries(i.states || {}).map(([k, v]) => [k, path.basename(v)])),
    })),
    missing_sprites: spec.nodes
      .filter(n => !assetById[n.id] && (n.asset !== 'none'))
      .map(n => n.id),
    modify_records: [{ at: new Date().toISOString(), desc: o.desc || `按效果图 ${spec.source.image} 生成 ${spec.name}` }],
    uncertainties: spec.uncertainties || [],
    low_confidence_nodes: spec.nodes.filter(n => n.confidence < 0.6).map(n => ({ id: n.id, confidence: n.confidence })),
  };
}

function save(rules, outDir) {
  fs.mkdirSync(outDir, { recursive: true });
  const f = path.join(outDir, 'ui_rules.json');
  fs.writeFileSync(f, JSON.stringify(rules, null, 2));
  return f;
}

/** 把规则同步进 Unity 的 UIDefineRuleObject 资产；能力缺失时静默降级。 */
async function pushToUnity(rules, o = {}) {
  const name = o.ruleName || rules.screen;
  const calls = [], warnings = [];
  const namesData = {};
  rules.node_names.forEach(n => { namesData[n.id] = { name: n.name, originName: n.originName }; });
  const spritesData = {};
  rules.node_sprites.forEach(s => { spritesData[s.id] = s.fileName; });

  const tryCall = async (tool, args) => {
    try { calls.push({ tool, ok: true, result: await mcp.call(tool, args) }); }
    catch (e) { calls.push({ tool, ok: false, error: e.message }); warnings.push(`${tool}: ${e.message}`); }
  };
  await tryCall('ui_rule.record_renames', { name, names_data: namesData });
  await tryCall('ui_rule.record_download_sprites', { name, sprites_data: spritesData, auto_load_sprites: o.autoLoad !== false });
  await tryCall('ui_rule.record_modify', { name, modify_desc: (rules.modify_records[0] || {}).desc || '' });
  return { calls, warnings, pushed: calls.some(c => c.ok) };
}

module.exports = { compose, save, pushToUnity };

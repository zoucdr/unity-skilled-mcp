'use strict';
/**
 * tx.js — texgen MCP 调用封装。
 *
 * 统一做三件事：
 *   1) 找到 unity_mcp.js（工程内副本优先，其次用户 skills 目录）；
 *   2) 规范化响应：工具返回 {success:false} 时抛出可读错误，不把半截 JSON 往上丢；
 *   3) 校验 texgen 的硬上限（尺寸 <= 2048、layers <= 512），在发请求前就拦住，
 *      省掉一次网络往返和一次 Unity 侧报错。
 *
 * 用法：
 *   const tx = require('./tx');
 *   tx.configure({ project: 'E:/unity3d-skilled-mcp/demo' });
 *   const data = await tx.call('texgen.render', { width: 512, height: 512, ... });
 */

const fs = require('fs');
const path = require('path');

const MAX_DIMENSION = 2048;
const MAX_LAYERS = 512;

const CANDIDATES = [
  'E:/unity3d-skilled-mcp/unity-package/Skills/unity-skilled-mcp/scripts/unity_mcp.js',
  path.join(process.env.USERPROFILE || process.env.HOME || '', '.workbuddy/skills/unity-skilled-mcp/scripts/unity_mcp.js'),
];

let _unity = null;
let _cfg = {};

function findLoader() {
  for (const c of CANDIDATES) {
    try { if (c && fs.existsSync(c)) return c; } catch (e) { /* ignore */ }
  }
  throw new Error(
    '找不到 unity_mcp.js。请用 tx.configure({ loader: "<绝对路径>/scripts/unity_mcp.js" }) 指定，' +
    '常见位置：<工程>/unity-package/Skills/unity-skilled-mcp/scripts/unity_mcp.js'
  );
}

function configure(opts) {
  _cfg = Object.assign({}, _cfg, opts || {});
  _unity = null;
  return _cfg;
}

function unity() {
  if (_unity) return _unity;
  const loader = _cfg.loader || findLoader();
  _unity = require(loader);
  const cfg = {};
  if (_cfg.project) cfg.expected_project = _cfg.project;
  if (_cfg.instance_id) cfg.instance_id = _cfg.instance_id;
  if (_cfg.timeout) cfg.timeout = _cfg.timeout;
  if (Object.keys(cfg).length) _unity.configure(cfg);
  return _unity;
}

/** texgen 的硬上限预检。命中就本地抛错，不发请求。 */
function precheck(tool, args) {
  const problems = [];
  const w = args && args.width, h = args && args.height;
  if (w !== undefined && (w < 1 || w > MAX_DIMENSION)) problems.push(`width=${w} 超出 1..${MAX_DIMENSION}`);
  if (h !== undefined && (h < 1 || h > MAX_DIMENSION)) problems.push(`height=${h} 超出 1..${MAX_DIMENSION}`);

  let layers = args && (args.layers || args.layers_json);
  if (typeof layers === 'string') {
    try { layers = JSON.parse(layers); } catch (e) { problems.push('layers_json 不是合法 JSON：' + e.message); }
  }
  if (Array.isArray(layers) && layers.length > MAX_LAYERS) {
    problems.push(`layers 数量 ${layers.length} 超过上限 ${MAX_LAYERS}`);
  }
  // 坐标必须是数字对，字符串会被 SimpleJson 静默当 0
  const walk = (list, where) => {
    if (!Array.isArray(list)) return;
    list.forEach((L, i) => {
      for (const k of ['center', 'size', 'from', 'to', 'gridCell']) {
        if (L && L[k] !== undefined) {
          const v = L[k];
          if (!Array.isArray(v) || v.length !== 2 || !Number.isFinite(v[0]) || !Number.isFinite(v[1])) {
            problems.push(`${where}[${i}].${k} 必须是 [数值, 数值]，收到 ${JSON.stringify(v)}`);
          }
        }
      }
    });
  };
  walk(layers, 'layers');
  if (Array.isArray(args && args.regions)) {
    args.regions.forEach((r, i) => walk(r && r.layers, `regions[${i}].layers`));
  }

  if (problems.length) {
    const err = new Error(`texgen.${tool} 参数预检失败：\n  - ` + problems.join('\n  - '));
    err.problems = problems;
    throw err;
  }
}

function unwrap(res) {
  if (res && res.success === false) {
    const detail = res.message || JSON.stringify(res).slice(0, 500);
    throw new Error('texgen 工具返回失败：' + detail);
  }
  return res && res.data !== undefined ? res.data : res;
}

async function call(tool, args) {
  precheck(tool, args);
  let raw;
  try {
    raw = await unity().callSkillJson(tool, args || {});
  } catch (e) {
    throw new Error(`调用 ${tool} 失败：${e.message}`);
  }
  return unwrap(raw);
}

/** 把结果里的 output_path（工程相对）解析成绝对路径。 */
function resolveOut(p, projectRoot) {
  if (!p) return null;
  if (/^[A-Za-z]:[\\/]/.test(p) || p.startsWith('/')) return p;
  const root = projectRoot || _cfg.project;
  if (!root) throw new Error('相对路径 ' + p + ' 需要 projectRoot（tx.configure({project})）');
  return path.join(root, p);
}

/** 快速探活。 */
async function ping() {
  const u = unity();
  if (typeof u.workflowContext === 'function') {
    return await u.workflowContext();
  }
  return await u.callSkillJson('texgen.spec', {});
}

module.exports = { configure, call, precheck, unwrap, resolveOut, ping, MAX_DIMENSION, MAX_LAYERS };

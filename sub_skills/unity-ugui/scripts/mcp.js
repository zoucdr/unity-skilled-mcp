'use strict';
/**
 * mcp.js — Unity Skilled MCP 的通用调用层（建模 / 贴图 / UI 共用）。
 *
 * 与旧的 tx.js 的区别：tx.js 只认 texgen，这个不限定命名空间，
 * 但把踩过的坑固化成通用预检：
 *
 *   1) 异步任务：工具可能返回 {status:"running", task_id, query_path}。
 *      call() 默认自动轮询到终态，调用方拿到的永远是结果本身。
 *   2) 参数预检：坐标字段必须是 [数值, 数值]（字符串会被静默当 0）；
 *      layers_json / regions_json 必须是合法 JSON；尺寸不得超过工具上限。
 *   3) 成功判定：返回 {success:false} 直接抛错，不把半截 JSON 往上丢。
 *
 * 4) 路径全部运行时解析：**本文件不含任何绝对路径**。
 *    - 客户端脚本（unity_bridge.js / unity_mcp.js）按 loaderCandidates() 顺序探测；
 *    - Unity 工程根按 resolveProject() 顺序推导（显式 > 环境变量 > cwd 上溯）。
 *    技能目录随便搬、unity-skilled-mcp 装在哪里都不影响。
 *
 * 用法：
 *   const mcp = require('./mcp');
 *   mcp.configure({ project: '<Unity 工程根>' });   // 省略则按 cwd 上溯
 *   const data = await mcp.call('probuilder.createShape', { shapeType: 'Cube', name: 'A' });
 */

const fs = require('fs');
const os = require('os');
const path = require('path');

/** 客户端脚本的两个历史文件名，新包用 unity_bridge.js。 */
const LOADER_FILES = ['unity_bridge.js', 'unity_mcp.js'];

/** 各命名空间的尺寸/数量硬上限，超出在本地就拒绝，省一次往返。 */
const LIMITS = {
  texgen: { maxDim: 2048, maxLayers: 512 },
  uisourcegen: { maxDim: 16384, maxLayers: 4096 },
};

/** 已知的坐标字段：值必须是两个有限数。 */
const PAIR_FIELDS = [
  'center', 'size', 'from', 'to', 'gridCell', 'rect', 'position', 'rotation', 'scale',
  'points_', 'translate', 'offset', 'pivot', 'a', 'b',
];

let _unity = null;
let _cfg = {};

// ---------------------------------------------------------------- 路径解析
// 本文件里不写死任何盘符、用户目录或仓库位置：装载器与工程根都在运行时探测。

function expandHome(p) {
  const s = String(p);
  if (s === '~') return os.homedir();
  if (/^~[\\/]/.test(s)) return path.join(os.homedir(), s.slice(2));
  return s;
}

/** 极简 INI 读取，只为拿 skill.ini 的字段，不引外部依赖。失败一律返回 {}。 */
function readIniSection(file, section) {
  try {
    const text = fs.readFileSync(expandHome(file), 'utf8');
    const out = {};
    let cur = null;
    for (const line of text.split(/\r?\n/)) {
      const t = line.trim();
      if (!t || t[0] === '#' || t[0] === ';') continue;
      const h = /^\[([^\]]+)\]/.exec(t);
      if (h) { cur = h[1]; continue; }
      if (cur !== section) continue;
      const m = /^([^=:]+?)\s*[=:]\s*(.*)$/.exec(t);
      if (m) out[m[1].trim().toLowerCase()] = m[2].trim();
    }
    return out;
  } catch (e) {
    return {};
  }
}

/** 判定一个目录是不是 Unity 工程根。 */
function isUnityProject(dir) {
  try {
    return fs.existsSync(path.join(dir, 'Assets'))
      && fs.existsSync(path.join(dir, 'ProjectSettings'));
  } catch (e) {
    return false;
  }
}

/** 技能包相对 Unity 工程的常见落位，用于从任意目录上溯查找。 */
const LOADER_LAYOUTS = [
  path.join('unity-package', 'Skills', 'unity-skilled-mcp', 'scripts'),
  path.join('Skills', 'unity-skilled-mcp', 'scripts'),
  path.join('skills', 'unity-skilled-mcp', 'scripts'),
  path.join('sub_skills', 'unity-skilled-mcp', 'scripts'),
];

/**
 * 客户端脚本候选路径（按可信度排序）。
 *
 * 顺序：显式 loader > 环境变量 > 运行中服务发布的 skill_dir >
 *       自身位置/当前目录上溯的常见布局 > 各 AI 工具的用户级技能目录。
 *
 * @returns {string[]} 已展开为绝对路径的候选列表
 */
function loaderCandidates() {
  const list = [];
  const push = (p) => {
    if (!p) return;
    const abs = path.resolve(expandHome(p));
    if (!list.includes(abs)) list.push(abs);
  };
  const pushDir = (scriptsDir) => { for (const f of LOADER_FILES) push(path.join(scriptsDir, f)); };
  const walkUp = (start) => {
    let cur = path.resolve(expandHome(start));
    for (let i = 0; i < 16; i++) {
      for (const layout of LOADER_LAYOUTS) pushDir(path.join(cur, layout));
      const parent = path.dirname(cur);
      if (parent === cur) break;
      cur = parent;
    }
  };

  // 1) 显式指定
  push(_cfg.loader);
  push(process.env.UNITY_MCP_LOADER);

  // 2) 服务发布的 skill_dir（本机 INI，只当路径提示）
  const ini = readIniSection(path.join(os.homedir(), '.unity-mcp', 'skill.ini'), 'unity3d-skilled-mcp');
  if (ini.skill_dir) pushDir(path.join(expandHome(ini.skill_dir), 'scripts'));

  // 3) 从当前目录与自身位置分别上溯
  walkUp(process.cwd());
  walkUp(__dirname);

  // 4) 各工具的用户级技能目录
  const home = os.homedir();
  pushDir(path.join(home, '.workbuddy', 'skills', 'unity-skilled-mcp', 'scripts'));
  pushDir(path.join(home, '.claude', 'skills', 'unity-skilled-mcp', 'scripts'));
  pushDir(path.join(home, 'skills', 'unity-skilled-mcp', 'scripts'));

  return list;
}

function findLoader() {
  const cands = loaderCandidates();
  for (const c of cands) {
    try { if (fs.existsSync(c)) return c; } catch (e) { /* ignore */ }
  }
  throw new Error(
    '找不到 unity-skilled-mcp 的客户端脚本（unity_bridge.js / unity_mcp.js）。\n' +
    '  已探测：\n' + cands.map(c => '    - ' + c).join('\n') + '\n' +
    '  指定方式（任选）：\n' +
    '    mcp.configure({ loader: "<任意位置>/scripts/unity_bridge.js" })\n' +
    '    UNITY_MCP_LOADER="<任意位置>/scripts/unity_bridge.js"\n' +
    '    把本脚本放在 Unity 工程目录内运行（会从 cwd 上溯）'
  );
}

/**
 * 解析 Unity 工程根，全程不含硬编码路径。
 *
 * 顺序：显式 project > UNITY_PROJECT / UNITY_PROJECT_PATH 环境变量 >
 *       本机 skill.ini 的 project_path > 从 cwd 与自身位置上溯找含
 *       Assets + ProjectSettings 的目录。
 *
 * @param {string} [explicit] 显式路径（可含 ~）
 * @param {{strict?:boolean}} [opts] strict=false 时找不到返回 null 而不抛
 * @returns {string|null} 绝对路径
 */
function resolveProject(explicit, opts) {
  const strict = !opts || opts.strict !== false;
  const tried = [];

  const accept = (p, why) => {
    const abs = path.resolve(expandHome(p));
    tried.push(`${why}: ${abs}`);
    return isUnityProject(abs) ? abs : null;
  };

  const given = explicit !== undefined ? explicit : _cfg.project;
  if (given) {
    const hit = accept(given, '显式');
    if (hit) return hit;
    // 显式给了但无效：严格模式立刻报错（别静默连到别的工程）；
    // 非严格模式（unity() 内部用）继续往下探测，避免配置过期导致完全不可用
    if (strict) {
      throw new Error(
        `指定的 Unity 工程无效：${path.resolve(expandHome(given))}\n` +
        '  必须是同时含 Assets/ 与 ProjectSettings/ 的目录。'
      );
    }
  }

  for (const key of ['UNITY_PROJECT', 'UNITY_PROJECT_PATH']) {
    if (process.env[key]) {
      const hit = accept(process.env[key], 'env ' + key);
      if (hit) return hit;
    }
  }

  const ini = readIniSection(path.join(os.homedir(), '.unity-mcp', 'skill.ini'), 'unity3d-skilled-mcp');
  if (ini.project_path) {
    const hit = accept(ini.project_path, 'skill.ini');
    if (hit) return hit;
  }

  for (const start of [process.cwd(), __dirname]) {
    let cur = path.resolve(start);
    for (let i = 0; i < 16; i++) {
      if (isUnityProject(cur)) return cur;
      const parent = path.dirname(cur);
      if (parent === cur) break;
      cur = parent;
    }
  }

  if (strict) {
    const detail = tried.length ? '\n  已试过：\n' + tried.map(t => '    - ' + t).join('\n') : '';
    throw new Error(
      '无法确定 Unity 工程根（未找到同时含 Assets/ 与 ProjectSettings/ 的目录）。' + detail + '\n' +
      '  指定方式（任选）：\n' +
      '    mcp.configure({ project: "<Unity 工程根>" })\n' +
      '    UNITY_PROJECT="<Unity 工程根>"\n' +
      '    在工程目录内运行脚本'
    );
  }
  return null;
}

function configure(opts) {
  _cfg = Object.assign({}, _cfg, opts || {});
  _unity = null;
  return _cfg;
}

function unity() {
  if (_unity) return _unity;
  const loader = _cfg.loader ? path.resolve(expandHome(_cfg.loader)) : findLoader();
  _unity = require(loader);
  const cfg = {};
  const proj = resolveProject(undefined, { strict: false });
  if (proj) cfg.expected_project = proj;
  if (_cfg.instance_id) cfg.instance_id = _cfg.instance_id;
  if (_cfg.timeout) cfg.timeout = _cfg.timeout;
  if (Object.keys(cfg).length) _unity.configure(cfg);
  return _unity;
}

function namespaceOf(tool) {
  const i = String(tool).indexOf('.');
  return i > 0 ? tool.slice(0, i) : '';
}

// ---------------------------------------------------------------- 预检

function precheck(tool, args) {
  const problems = [];
  const a = args || {};
  const ns = namespaceOf(tool);
  const lim = LIMITS[ns];

  if (lim) {
    for (const k of ['width', 'height', 'max_size']) {
      const v = a[k];
      if (v !== undefined && v !== null) {
        if (!Number.isFinite(v)) problems.push(`${k}=${JSON.stringify(v)} 不是数值`);
        else if (k === 'max_size') { if (v < 1 || v > lim.maxDim) problems.push(`max_size=${v} 超出 1..${lim.maxDim}`); }
        else if (v < 1 || v > lim.maxDim) problems.push(`${k}=${v} 超出 1..${lim.maxDim}`);
      }
    }
  }

  // 字符串形式的 JSON 参数必须先能解析，否则 Unity 侧只会给一句模糊报错
  for (const k of ['layers_json', 'regions_json', 'canvases', 'screens', 'properties']) {
    const v = a[k];
    if (typeof v === 'string' && (k.endsWith('_json'))) {
      try { JSON.parse(v); } catch (e) { problems.push(`${k} 不是合法 JSON：${e.message}`); }
    }
  }

  // 【同名不同义】这是本文件最危险的坑，也是实测踩出来的：
  //   probuilder / mesh 的 position / rotation / scale / size  → 三维数组
  //   texgen / uisourcegen 的 layer.rotation                    → 单个角度数值
  //   texgen 的 layer.size                                     → 二维数组
  // 所以三维校验**只对建模命名空间**生效，不能一刀切。
  const spatial3D = /^(probuilder|mesh)\./.test(tool);
  if (spatial3D) {
    for (const k of ['position', 'rotation', 'scale', 'size']) {
      const v = a[k];
      if (v === undefined) continue;
      if (!Array.isArray(v) || v.length !== 3 || !v.every(Number.isFinite)) {
        problems.push(`${k}（${namespaceOf(tool)}）必须是 [x, y, z] 三个数值，收到 ${JSON.stringify(v)}`);
      }
    }
  }
  for (const k of ['center', 'from', 'to']) {
    const v = a[k];
    if (v === undefined) continue;
    if (!Array.isArray(v) || v.length !== 2 || !v.every(Number.isFinite)) {
      problems.push(`${k} 必须是 [数值, 数值]，收到 ${JSON.stringify(v)}`);
    }
  }

  // layers / canvases / controls 里的坐标必须是数值
  const walkLayer = (list, where) => {
    if (!Array.isArray(list)) return;
    if (lim && list.length > lim.maxLayers) {
      problems.push(`${where} 数量 ${list.length} 超过上限 ${lim.maxLayers}`);
    }
    list.forEach((L, i) => {
      if (!L || typeof L !== 'object') return;
      // 二元组：center / size / from / to / gridCell
      for (const k of ['center', 'size', 'from', 'to', 'gridCell']) {
        const v = L[k];
        if (v === undefined) continue;
        if (!Array.isArray(v) || v.length !== 2 || !Number.isFinite(v[0]) || !Number.isFinite(v[1])) {
          problems.push(`${where}[${i}].${k} 必须是 [数值, 数值]，收到 ${JSON.stringify(v)}`);
        }
      }
      // 四元组：uisourcegen 的 rect 是 [x, y, w, h]
      for (const k of ['rect']) {
        const v = L[k];
        if (v === undefined) continue;
        if (!Array.isArray(v) || v.length !== 4 || !v.every(Number.isFinite)) {
          problems.push(`${where}[${i}].${k} 必须是 [x, y, w, h] 四个数值，收到 ${JSON.stringify(v)}`);
        }
      }
      // 三元组：仅建模上下文的 position / rotation / scale
      if (spatial3D) {
        for (const k of ['position', 'rotation', 'scale']) {
          const v = L[k];
          if (v === undefined) continue;
          if (!Array.isArray(v) || v.length !== 3 || !v.every(Number.isFinite)) {
            problems.push(`${where}[${i}].${k} 必须是 [x, y, z] 三个数值，收到 ${JSON.stringify(v)}`);
          }
        }
      }
      if (Array.isArray(L.layers)) walkLayer(L.layers, `${where}[${i}].layers`);
      if (Array.isArray(L.controls)) walkLayer(L.controls, `${where}[${i}].controls`);
    });
  };
  for (const k of ['layers', 'canvases', 'screens']) {
    if (Array.isArray(a[k])) walkLayer(a[k], k);
  }
  if (Array.isArray(a.regions)) {
    a.regions.forEach((r, i) => walkLayer(r && r.layers, `regions[${i}].layers`));
  }
  if (a.screen) walkLayer(a.screen.controls, 'screen.controls');

  // 必填参数缺失时提前说清，别让 Unity 回一句 "Parameter 'xxx' is required"
  if (problems.length) {
    const err = new Error(`${tool} 参数预检失败：\n  - ` + problems.join('\n  - '));
    err.problems = problems;
    throw err;
  }
}

/** 提交给 Unity 的 C# 代码的静态检查（code.execute 用）。 */
function lintCSharp(code) {
  const out = [];
  const lines = String(code || '').split(/\r?\n/);
  lines.forEach((raw, i) => {
    const L = raw.replace(/\/\/.*$/, '');
    if (/^\s*return\s*;/.test(L) || /\breturn\s*;/.test(L) && !/=>/.test(L)) {
      out.push({ level: 'error', line: i + 1, rule: 'no-return', msg: 'code.execute 返回 object，写 return; 会 CS0126。用 Debug.Log 输出或去掉' });
    }
    if (/\?\?/.test(L) && !/\/\/ guard:allow-null-coalesce/.test(raw)) {
      out.push({ level: 'warn', line: i + 1, rule: 'null-coalesce', msg: '工程若不是 C# 8+ 则 ?? 不可用；Unity 2020 之前会编译失败' });
    }
    if (/Newtonsoft|JsonConvert/.test(L)) {
      // 别建议 SimpleJson：本工程没有该类型，写了会 CS0103 并级联出一堆假错误（见 semantics 第 16 条）
      out.push({ level: 'error', line: i + 1, rule: 'newtonsoft', msg: '工程无 Newtonsoft / SimpleJson，不要运行时解析 JSON；把数据编成 C# 字面量（new float[] { 1f, 2f }）' });
    }
    // 浮点索引写入数组：静默失败，最难查。外层恒真条件是历史残留，直接单层判断
    if (/\[\s*[\w.]+\s*\*\s*0\.\d+[^\]]*\]\s*=/.test(L)) {
      out.push({ level: 'error', line: i + 1, rule: 'float-index', msg: '浮点索引写入 TypedArray 会静默丢弃，索引必须 Math.round' });
    }
  });
  return out;
}

/** 提交前检查，有 error 就抛。 */
function assertLint(code) {
  const problems = lintCSharp(code).filter(p => p.level === 'error');
  if (problems.length) {
    throw new Error('C# 预检失败：\n  - ' + problems.map(p => `L${p.line} ${p.rule}: ${p.msg}`).join('\n  - '));
  }
}

// ---------------------------------------------------------------- 调用

function unwrap(res, tool) {
  if (res && res.success === false) {
    const detail = res.message || JSON.stringify(res).slice(0, 600);
    throw new Error(`${tool} 返回失败：${detail}`);
  }
  return res && res.data !== undefined ? res.data : res;
}

/**
 * C# 预检闸门：call() 每次提交 code.execute 前必过，纯同步、可离线单测。
 * 只有显式 skipLint:true 才豁免；返回是否真的执行了预检。
 */
function guardCSharp(tool, args, opts) {
  const o = opts || {};
  if (String(tool).toLowerCase() !== 'code.execute' || o.skipLint === true) return false;
  const cs = args && args.code;
  if (typeof cs !== 'string' || !cs.length) return false;
  assertLint(cs);
  return true;
}

function isRunning(res) {
  return res && (res.status === 'running' || res.status === 'accepted')
    && (res.task_id || res.request_id);
}

async function waitTask(id, opts) {
  const o = Object.assign({ timeoutMs: 300000, intervalMs: 700 }, opts || {});
  // 【坑】opts 里显式传 undefined 会覆盖默认值，导致 timeoutMs 变成 undefined，
  // 循环条件 `Date.now() - started < undefined` 立刻为 false → 立刻"超时"。
  if (!Number.isFinite(o.timeoutMs) || o.timeoutMs <= 0) o.timeoutMs = 300000;
  const u = unity();
  const started = Date.now();
  let last = null;
  while (Date.now() - started < o.timeoutMs) {
    try {
      last = u.requestStatus
        ? await u.requestStatus(id, _cfg.instance_id)
        : await u.callSkillJson('_.status', { request_id: id });
    } catch (e) {
      last = { status: 'unknown', error: e.message };
    }
    const st = last && last.status;
    if (st && st !== 'running' && st !== 'accepted' && st !== 'unknown') return last;
    await new Promise(r => setTimeout(r, o.intervalMs));
  }
  const err = new Error(`任务 ${id} 等待超时（${o.timeoutMs}ms），最后状态：${JSON.stringify(last).slice(0, 300)}`);
  err.status = last;
  throw err;
}

/**
 * 调用一个工具。异步任务会自动轮询到终态。
 * @param {string} tool 形如 'texgen.render'
 * @param {object} args
 * @param {object} [opts] { wait:false 立即返回 task_id, timeoutMs }
 */
async function call(tool, args, opts) {
  precheck(tool, args);
  const o = opts || {};
  // C# 预检不可绕过：此前只有 runCode() 会 lint，而全项目所有调用都直接走
  // call('code.execute', ...)，结果最大的 C# 片段从未被检查过。统一收口到这一层。
  guardCSharp(tool, args, o);
  let raw;
  try {
    raw = await unity().callSkillJson(tool, args || {});
  } catch (e) {
    throw new Error(`调用 ${tool} 失败：${e.message}`);
  }

  // 后台工具先返回 running；默认把它等完，调用方不需要关心
  if (isRunning(raw)) {
    const id = raw.task_id || raw.request_id;
    if (o.wait === false) return raw;
    const done = await waitTask(id, { timeoutMs: o.timeoutMs });
    if (done && done.status === 'failed') {
      throw new Error(`${tool} 后台任务失败：${JSON.stringify(done).slice(0, 500)}`);
    }
    // 状态记录里若带结果体，优先用它
    if (done && done.result !== undefined) return unwrap(done.result, tool);
    if (done && done.data !== undefined) return unwrap(done, tool);
    // 否则回读一次（多数实现会缓存到 query_path）
    if (raw.query_path) {
      try {
        const again = await unity().request(raw.query_path);
        return unwrap(again, tool);
      } catch (e) { /* 落到下面返回 done */ }
    }
    return done;
  }

  return unwrap(raw, tool);
}

/** 执行一段 C#（code.execute）。预检由 call() 统一执行，不再重复。 */
async function runCode(code, opts) {
  const o = opts || {};
  const args = { code };
  if (o.usings) args.usings = o.usings;
  if (o.define) args.define = o.define;
  return await call('code.execute', args, o);
}

/** 探活：拿连接信息与工程路径。 */
async function ping() {
  const u = unity();
  // connectionInfo() 是 discover 的封装，不需要回调；workflowContext 需要回调，别用错
  if (typeof u.connectionInfo === 'function') return await u.connectionInfo();
  if (typeof u.callSkillJson === 'function') return await u.callSkillJson('texgen.spec', {});
  throw new Error('客户端脚本既没有 connectionInfo 也没有 callSkillJson，版本不兼容');
}

/** 把结果里的工程相对路径解析成绝对路径。 */
function resolveOut(p, projectRoot) {
  if (!p) return null;
  if (/^[A-Za-z]:[\\/]/.test(p) || p.startsWith('/') || /^~[\\/]/.test(p)) return expandHome(p);
  const root = projectRoot || resolveProject(undefined, { strict: false });
  if (!root) {
    throw new Error(
      `相对路径 "${p}" 需要工程根才能解析；用 mcp.configure({ project }) 或 UNITY_PROJECT 指定。`
    );
  }
  return path.join(root, p);
}

module.exports = {
  configure, call, runCode, waitTask, ping,
  precheck, lintCSharp, assertLint, guardCSharp, unwrap, resolveOut,
  resolveProject, loaderCandidates, isUnityProject, readIniSection,
  namespaceOf, LIMITS,
};

#!/usr/bin/env node
'use strict';

// 仅使用 Node.js 18+ 标准库；运行时由技能读取 INI 后选择。
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const https = require('node:https');
const { TextDecoder } = require('node:util');
const { AsyncLocalStorage } = require('node:async_hooks');
const { randomUUID } = require('node:crypto');

function expectedProject(value) {
  if (value !== undefined) {
    if (typeof value !== 'string' || !value.trim()) throw new ClientError('expected_project 不能为空。', 2);
    return path.resolve(expandHome(value));
  }
  let current = process.cwd();
  while (true) {
    if (fs.existsSync(path.join(current, 'Assets')) && fs.existsSync(path.join(current, 'ProjectSettings'))) return current;
    const parent = path.dirname(current);
    if (parent === current) return null;
    current = parent;
  }
}

function projectKey(value) {
  const normalized = path.resolve(value);
  return process.platform === 'win32' ? normalized.toLowerCase() : normalized;
}

function requestId(value) {
  if (typeof value !== 'string' || !value.trim() || value.length > 256) {
    throw new ClientError('request_id 必须为 1-256 个字符。', 2);
  }
  return value;
}

function queryPath(id, instance) {
  return '/requests/' + encodeURIComponent(id) + '?instance_id=' + encodeURIComponent(instance);
}

function bind(info, args, required) {
  const client = args.client;
  const expected = client.expected_project;
  const connection = origin(info.base_url);
  if (client.connection && client.connection !== connection) console.error('警告：Unity 连接地址已变更。');
  client.connection = connection;
  if (client.instance_id && client.instance_id !== info.instance_id
      && !['discover', 'status'].includes(args.command)) {
    console.error('警告：Unity 实例已变更。');
    throw new ClientError('实例漂移；显式 discover 重新确认，禁止自动重放。', 2);
  }
  const matches = expected && projectKey(expected) === projectKey(info.project_path);
  if (!matches) {
    console.error('警告：' + (expected ? 'Unity 工程不匹配。' : '未确定预期 Unity 工程。'));
    if (required) throw new ClientError('请明确 --expected-project 并确认目标工程；未执行。', 2);
    return {};
  }
  if (client.instance_id && client.instance_id !== info.instance_id) {
    console.error('警告：Unity 实例已变更。');
  }
  if (args.command !== 'status') client.instance_id = info.instance_id;
  return { 'X-Mcp-Expected-Project': encodeURIComponent(expected), 'X-Mcp-Instance-Id': info.instance_id };
}

class ClientError extends Error {
  constructor(message, code) {
    super(message);
    this.code = code;
  }
}

// CLI 按原样打印连接信息；作为库被 import 时默认静默，避免程序化循环刷屏。
const quietStorage = new AsyncLocalStorage();
const workflowStorage = new AsyncLocalStorage();

function logInfo(message) {
  if (!quietStorage.getStore()) console.error(message);
}

function checkedUrl(value) {
  try {
    if (typeof value !== 'string' || !/^https?:\/\//i.test(value)
        || /[\s\u0000-\u001f\u007f\\#]/u.test(value)) throw new Error();
    const authority = value.split('/')[2].split('?')[0];
    if (!authority || authority.includes('@') || authority.endsWith(':')) throw new Error();
    const url = new URL(value);
    if (!['http:', 'https:'].includes(url.protocol) || !url.hostname
        || url.username || url.password || (url.port && Number(url.port) < 1)) throw new Error();
    return url;
  } catch {
    throw new ClientError('URL 必须是有效的绝对 http/https 地址，不含凭据、片段或反斜杠。', 2);
  }
}

function origin(value) {
  return checkedUrl(value).origin;
}

// Git Bash / MSYS 在调用 Windows 原生程序时会把以 / 开头的参数
// 转成 <Git 安装根>/<原路由>，例如 /groups -> C:/.../PortableGit/groups。
// EXEPATH 指向 <Git 安装根>/bin，据此可精确还原，而不误判合法的绝对 URL。
function msysRoot() {
  const exe = process.env.EXEPATH;
  if (!exe) return null;
  const root = exe.replace(/\\/g, '/').replace(/\/+$/, '')
    .replace(/\/usr\/bin$/i, '').replace(/\/bin$/i, '');
  return /^[A-Za-z]:/.test(root) ? root : null;
}

const BARE_ROUTE = /^[A-Za-z0-9._~%!$&'()*+,;=@-]+(?:\/[A-Za-z0-9._~%!$&'()*+,;=@-]*)*(?:\?[A-Za-z0-9._~%!$&'()*+,;=:@/?-]*)?$/u;
const BAD_ROUTE_CHARS = /[\s\u0000-\u001f\u007f\\#]/u;

function normalizeRequestTarget(target) {
  if (typeof target !== 'string' || !target) return target;
  // 绝对 http/https 地址原样交给 checkedUrl（含凭据、片段等非法情形由它拒绝）。
  if (/^https?:\/\//i.test(target)) return target;
  // 显式相对路由：禁止 // 协议相对地址与空白/控制/反斜杠/片段。
  if (target.startsWith('/')) {
    if (target.startsWith('//') || BAD_ROUTE_CHARS.test(target)) {
      throw new ClientError('相对路由只能以单个 / 开头，且不含空白、反斜杠或片段。', 2);
    }
    return target;
  }
  // 被 MSYS 转换过的本地路径：按 EXEPATH 精确剥离根前缀，还原成 /ROUTE。
  if (/^[A-Za-z]:[\\/]/.test(target)) {
    const normalized = target.replace(/\\/g, '/');
    const root = msysRoot();
    if (root && normalized.toLowerCase().startsWith(root.toLowerCase() + '/')) {
      const route = normalized.slice(root.length);
      if (!BAD_ROUTE_CHARS.test(route)) return route;
    }
    throw new ClientError('请求地址看起来是 Git Bash 自动转换的本地路径（' + target
      + '）；请改用省略前导斜杠的路由名、绝对 http/https URL，或设置 MSYS2_ARG_CONV_EXCL="*"。', 2);
  }
  // 省略前导斜杠的裸路由名不会触发 shell 路径转换。
  if (BARE_ROUTE.test(target)) return '/' + target;
  // 其它形态（file://、ftp:// 等）保持原样，由 checkedUrl 统一拒绝。
  return target;
}

function decode(buffer) {
  return new TextDecoder('utf-8', { fatal: true }).decode(buffer);
}

function expandHome(value) {
  return value === '~' ? os.homedir()
    : /^~[\\/]/.test(value) ? path.join(os.homedir(), value.slice(2)) : value;
}

function readIni(filename) {
  const sections = new Map();
  let section;
  let previous;
  let previousIndent = 0;
  for (const line of decode(fs.readFileSync(expandHome(filename))).split(/\r?\n/)) {
    const text = line.trim();
    if (!text || /^[#;]/.test(text)) continue;
    const indent = line.length - line.trimStart().length;
    if (section && previous !== undefined && indent > previousIndent) {
      section.set(previous, section.get(previous) + '\n' + text);
      continue;
    }
    const heading = /^\[([^\]]+)\]/.exec(text);
    if (heading) {
      if (sections.has(heading[1])) throw new Error('INI 节重复');
      section = new Map();
      sections.set(heading[1], section);
      previous = undefined;
      continue;
    }
    const entry = /^([^=:]+?)\s*[=:]\s*(.*)$/.exec(text);
    if (!section || !entry) throw new Error('INI 格式无效');
    const key = entry[1].trim().toLowerCase();
    if (section.has(key)) throw new Error('INI 字段重复：' + key);
    section.set(key, entry[2].trim());
    previous = key;
    previousIndent = indent;
  }
  const selected = sections.get('unity3d-skilled-mcp');
  if (!selected) throw new Error('INI 缺少 unity3d-skilled-mcp 节');
  return new Map([...(sections.get('DEFAULT') || []), ...selected]);
}

function requestHttp(value, timeout, method = 'GET', payload = null, headers = {}, maxBytes = Infinity) {
  const url = checkedUrl(value);
  headers = { ...headers };
  let data;
  if (payload !== null) {
    data = Buffer.from(JSON.stringify(payload), 'utf8');
    if (!Object.keys(headers).some(key => key.toLowerCase() === 'content-type')) {
      headers['Content-Type'] = 'application/json; charset=utf-8';
    }
    if (!Object.keys(headers).some(key => key.toLowerCase() === 'content-length')) {
      headers['Content-Length'] = String(data.length);
    }
  }
  return new Promise((resolve, reject) => {
    let finished = false;
    let timer;
    let req;
    const finish = (error, result) => {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      if (error) reject(error);
      else resolve(result);
    };
    const networkError = error => finish(new ClientError(
      '连接或 TLS/超时失败：' + error.message + '；未重试，写操作结果可能未知。', 3));
    try {
      // 不共享连接，不跟随重定向；每个 HTTP 操作仅发送一次。
      req = (url.protocol === 'https:' ? https : http).request(url, {
        method, headers, agent: false,
      }, response => {
        const chunks = [];
        let bytes = 0;
        response.on('data', chunk => {
          bytes += chunk.length;
          if (bytes > maxBytes) {
            finish(new ClientError('详情响应超过 65536 字节；未重试。', 5));
            response.destroy();
          } else chunks.push(chunk);
        });
        response.on('error', networkError);
        response.on('aborted', () => networkError(new Error('响应中断')));
        response.on('end', () => {
          let text;
          try {
            text = decode(Buffer.concat(chunks));
          } catch {
            finish(new ClientError('响应不是 UTF-8 文本。', 5));
            return;
          }
          let result = text;
          try { result = JSON.parse(text); } catch { /* 非 JSON 响应按文本输出。 */ }
          if (response.statusCode < 200 || response.statusCode >= 300) {
            const error = new ClientError('HTTP ' + response.statusCode + '：' + text + '；未重试。', 4);
            error.body = result;
            error.status = response.statusCode;
            finish(error);
          } else finish(null, result);
        });
      });
      req.on('error', networkError);
      const start = Date.now();
      const armTimer = () => {
        const remaining = timeout * 1000 - (Date.now() - start);
        if (remaining <= 0) {
          networkError(new Error('请求超时'));
          req.destroy();
        } else {
          timer = setTimeout(armTimer, Math.min(remaining, 2147483647));
        }
      };
      armTimer();
      req.end(data);
    } catch (error) {
      finish(new ClientError('请求参数无效：' + error.message + '；未重试。', 2));
      if (req) req.destroy();
    }
  });
}

const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);

async function discover(ini, timeout) {
  let base;
  let iniLimit;
  try {
    const config = readIni(ini);
    if (config.has('max_details')) {
      const value = config.get('max_details');
      if (!/^\+?[0-9]+$/.test(value) || !validDetailsLimit(Number(value))) {
        throw new Error('INI max_details 必须为非负整数，0 表示不限数量');
      }
      iniLimit = Number(value);
    }
    base = (config.get('base_url') || '').replace(/\/+$/, '');
    let port;
    if (config.has('port')) {
      if (!/^[+-]?\d+$/.test(config.get('port'))) throw new Error('INI port 无效');
      port = Number(config.get('port'));
      if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('INI port 无效');
    }
    if (!base) {
      if (port === undefined) throw new Error('INI 缺少有效 base_url 或 port');
      base = 'http://localhost:' + port;
    }
    const url = checkedUrl(base);
    if (!/^https?:\/\/[^/?]+$/i.test(base)) throw new Error('base_url 不能带路径或查询参数');
    if (port !== undefined && Number(url.port || (url.protocol === 'https:' ? 443 : 80)) !== port) {
      throw new Error('INI 的 base_url 与 port 不一致');
    }
  } catch (error) {
    throw new ClientError('无法读取连接配置 ' + ini + '：' + error.message + '；请在 Unity 中启动服务。', 2);
  }
  const info = await requestHttp(base + '/discovery', timeout);
  if (!isObject(info)) throw new ClientError('discovery 响应必须是 JSON 对象。', 5);
  try {
    const url = checkedUrl(base);
    if (origin(info.base_url) !== url.origin
        || info.port !== Number(url.port || (url.protocol === 'https:' ? 443 : 80))) {
      throw new Error('discovery 与 INI 端口/地址不一致');
    }
    for (const key of ['tools_url', 'schema_url_template']) {
      if (origin(info[key]) !== url.origin) throw new Error('discovery 端点不在同一服务');
    }
    for (const key of ['details_url', 'schemas_url']) {
      if (Object.hasOwn(info, key) && origin(info[key]) !== url.origin) throw new Error('discovery 端点不在同一服务');
    }
    if (!info.schema_url_template.includes('{name}')) throw new Error('schema URL 缺少 {name}');
    if (typeof info.instance_id !== 'string' || !info.instance_id.trim()) throw new Error('instance_id 缺失');
    for (const key of ['project_path', 'updated_at']) {
      if (typeof info[key] !== 'string') throw new Error('discovery 字段类型错误：' + key);
    }
    if (!Number.isInteger(info.process_id)) throw new Error('discovery process_id 类型错误');
    if (Object.hasOwn(info, 'max_details') && !validDetailsLimit(info.max_details)) {
      throw new Error('discovery max_details 必须为非负整数');
    }
  } catch (error) {
    throw new ClientError('无效 discovery：' + error.message, 5);
  }
  if (iniLimit !== undefined && Object.hasOwn(info, 'max_details') && iniLimit !== info.max_details) {
    throw new ClientError('INI max_details 与 discovery 不一致；请重启目标 Unity 服务以发布启动配置。', 2);
  }
  // 老服务缺字段时使用 INI；两处都缺失才采用默认值，不抬高服务显式限制。
  if (!Object.hasOwn(info, 'max_details')) info.max_details = iniLimit ?? DEFAULT_MAX_DETAILS;
  // 原样保留扩展字段；旧 INI 中可能残留 script_language，客户端不据此选择解释器或执行命令。
  logInfo('连接信息：' + JSON.stringify(info));
  return info;
}

function inputJson(args) {
  if (!args.jsonSource) return null;
  try {
    const text = args.jsonSource === 'file' ? decode(fs.readFileSync(args.jsonValue))
      : args.jsonSource === 'stdin' ? decode(fs.readFileSync(0)) : args.jsonValue;
    return JSON.parse(text, (_key, value) => {
      if (typeof value === 'number' && !Number.isFinite(value)) throw new Error('JSON 数值必须为有限数');
      return value;
    });
  } catch (error) {
    throw new ClientError('无法读取 UTF-8 JSON 输入：' + error.message, 2);
  }
}

const DEFAULT_MAX_DETAILS = 32;
const MAX_RESPONSE_BYTES = 65536;
const validDetailsLimit = value => Number.isSafeInteger(value) && value >= 0;

function responseDetailsLimit(result, limit) {
  // 旧响应省略该字段时继承 discovery 的有效限制；显式发布则必须一致。
  return !Object.hasOwn(result, 'max_details')
    || (validDetailsLimit(result.max_details) && result.max_details === limit);
}

function selectionValue(value) {
  return typeof value === 'string' && value.trim().length > 0
    && value === value.trim() && value.toLowerCase() !== 'all'
    && !/[\u0000-\u001f\u007f*]/u.test(value);
}

function detailsPayload(args) {
  if ((args.tag !== undefined) === Boolean(args.jsonSource)) {
    throw new ClientError('details 必须选择 --tag 或一种 --members 输入，不能同时使用。', 2);
  }
  if (args.tag !== undefined) {
    if (!selectionValue(args.tag)) throw new ClientError('tag 不能为空或 all/*；从 groups 获取实际标签。', 2);
    return { tag: args.tag };
  }
  const members = inputJson(args);
  if (!Array.isArray(members) || members.length === 0) throw new ClientError('members 必须是非空 JSON 数组。', 2);
  const unique = new Map();
  for (const member of members) {
    if (!isObject(member) || !['tool', 'prompt', 'resource'].includes(member.kind)) {
      throw new ClientError('成员 kind 仅允许 tool、prompt、resource。', 2);
    }
    const key = member.kind === 'resource' ? 'uri' : 'name';
    if (Object.keys(member).length !== 2 || !Object.hasOwn(member, key)
        || !selectionValue(member[key])) throw new ClientError('成员仅允许 kind 和 ' + key + '，值不能为空或 all/*。', 2);
    if (key === 'uri' && (!/^[A-Za-z][A-Za-z0-9+.-]*:[^\s]+$/u.test(member.uri))) {
      throw new ClientError('资源 uri 必须是带 scheme 的完整 URI；不读取资源内容。', 2);
    }
    unique.set(JSON.stringify([member.kind, member[key]]), member);
  }
  return { members: [...unique.values()] };
}

function findQuery(value) {
  if (typeof value !== 'string' || !value.trim() || value !== value.trim()
      || value.length > 128 || /[\u0000-\u001f\u007f]/u.test(value)) {
    throw new ClientError('检索关键字须为 1-128 个非控制字符，且不含首尾空白。', 2);
  }
  return value;
}

function rpcEnvelope(method, params, id) {
  return { jsonrpc: '2.0', id, method, params };
}

function rpcResult(envelope, id, expectedArray) {
  if (!isObject(envelope) || envelope.jsonrpc !== '2.0' || envelope.id !== id) {
    throw new ClientError('无效 JSON-RPC 响应。', 5);
  }
  if (Object.hasOwn(envelope, 'error')) {
    throw new ClientError('JSON-RPC error：' + JSON.stringify(envelope.error), 6);
  }
  if (!isObject(envelope.result)) throw new ClientError('JSON-RPC 响应缺少 result 对象。', 5);
  if (envelope.result.isError) throw new ClientError('MCP isError：' + JSON.stringify(envelope.result), 6);
  if (expectedArray && !Array.isArray(envelope.result[expectedArray])) {
    throw new ClientError('JSON-RPC 响应缺少 ' + expectedArray + ' 数组。', 5);
  }
  return envelope;
}

async function rpcCall(info, args, method, params, id, expectedArray) {
  const result = await requestHttp(info.base_url.replace(/\/+$/, '') + '/mcp',
    args.timeout, 'POST', rpcEnvelope(method, params, id), args.binding);
  return rpcResult(result, id, expectedArray);
}

function directoryUrl(info, kind) {
  return kind === 'tools' ? info.tools_url : origin(info.base_url) + '/' + kind;
}

async function directory(info, args, kind) {
  const result = await requestHttp(directoryUrl(info, kind), args.timeout);
  if (!isObject(result) || !Array.isArray(result[kind])) {
    throw new ClientError(kind + ' 响应缺少目录列表。', 5);
  }
  return result[kind];
}

function schemaNames(args) {
  let list = args.targets;
  if (!list.length) {
    const value = inputJson(args);
    if (!Array.isArray(value) || value.length === 0) {
      throw new ClientError('names 必须是非空 JSON 字符串数组。', 2);
    }
    list = value;
  }
  const unique = [];
  const seen = new Set();
  for (const name of list) {
    if (!selectionValue(name)) throw new ClientError('工具名必须是非空字符串，禁止 all/* 或控制字符。', 2);
    if (!seen.has(name)) { seen.add(name); unique.push(name); }
  }
  return unique;
}

function help(command) {
  const usage = {
    discover: 'discover（显式重新确认工程及实例绑定；不重放操作）',
    status: 'status REQUEST_ID --instance-id INSTANCE_ID\n  查询 accepted/running/completed/failed/interrupted/unknown；结论落盘，Unity 重启后同一 ID 仍可查。unknown 不证明未执行。',
    groups: 'groups',
    list: 'list [--kind tools|prompts|resources|all]',
    details: 'details (--tag TAG | --members JSON数组 | --members-file FILE | --members-stdin)\n  去重成员上限由 discovery 的 max_details 决定（默认32，0不限数量），响应最多 65536 字节；禁止全量选择；标签从 groups 获取。',
    schema: 'schema NAME',
    schemas: 'schemas NAME [NAME ...] | schemas (--names JSON数组 | --names-file FILE | --names-stdin)\n  一次获取多个工具的完整 Schema；去重后上限由 max_details 决定（默认32，0不限数量），响应最多 65536 字节；禁止全量选择。',
    call: 'call NAME [--args JSON | --args-file FILE | --args-stdin]',
    find: 'find QUERY [--kind tools|prompts|resources|all]\n  在目录摘要中按名称/描述/标签/URI 做本地关键字过滤，不拉取详情；不带 --kind 或 --kind all 时同时搜索工具+提示词+资源三类。',
    prompt: 'prompt NAME\n  名称必须来自 list --kind prompts；读取 prompts/get 的消息正文，不隐式调用旧目录接口。',
    resource: 'resource URI\n  URI 必须来自 list --kind resources；读取 resources/read 内容，拒绝未注册的 http/file URI。',
    request: 'request TARGET [--method GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS]\n  [--header NAME:VALUE ...] [--data JSON | --data-file FILE | --data-stdin]',
  };
  return '用法：node scripts/unity_bridge.js [--ini PATH] [--timeout SECONDS] '
    + (usage[command] || 'discover|groups|list|find|details|schema|schemas|call|prompt|resource|request')
    + '\n--expected-project PATH 显式指定工程，否则从 cwd 向上寻找 Unity 根；--instance-id ID 锁定实例。'
    + '\n--verbose 才打印完整连接；call 可用 --request-id ID，失败凭 status 查询，不重放。'
    + '\n默认 INI：~/.unity-mcp/skill.ini；超时：15 秒（正有限数）。'
    + '\n每次读取 INI 并验证 discovery；不重试、不跟随重定向，验证 TLS。'
    + '\nJSON 文件/标准输入支持 UTF-8 BOM；call 默认传 {}；request 支持 /ROUTE、省略斜杠的路由名或授权的绝对 HTTP URL（Git Bash 路径转换自动还原）。'
    + '\n本文件也可 require 作为库使用（callSkill / workflowContext 等），详见 SKILL.md。'
    + '\n-h / --help 显示帮助；各子命令可用 --help 查看选项。'
    + '\n退出码：0 成功；2 参数/配置；3 连接/TLS/超时；4 HTTP；5 响应格式；6 MCP 执行失败；130 中断。';
}

function parseArgs(argv) {
  const args = { ini: path.join(os.homedir(), '.unity-mcp', 'skill.ini'), timeout: 15, headers: [], method: 'GET', kind: 'tools', kindExplicit: false, targets: [] };
  let positionalOnly = false;
  for (let i = 0; i < argv.length; i++) {
    const token = argv[i];
    if (!positionalOnly && (token === '--help' || token === '-h')) return { help: help(args.command) };
    if (!positionalOnly && token === '--') { positionalOnly = true; continue; }
    if (!positionalOnly && token.startsWith('-')) {
      const split = token.indexOf('=');
      const flag = split < 0 ? token : token.slice(0, split);
      const prefix = { call: '--args', request: '--data', details: '--members', schemas: '--names' }[args.command];
      const source = prefix && (flag === prefix ? 'text' : flag === prefix + '-file' ? 'file' : flag === prefix + '-stdin' ? 'stdin' : null);
      if (flag === '--verbose') { args.verbose = true; continue; }
      const global = ['--expected-project', '--instance-id'].includes(flag)
        || (!args.command && ['--ini', '--timeout'].includes(flag))
        || (args.command === 'call' && flag === '--request-id');
      const requestOption = args.command === 'request' && ['--method', '--header'].includes(flag);
      const selectionOption = (['list', 'find'].includes(args.command) && flag === '--kind') || (args.command === 'details' && flag === '--tag');
      if (!global && !requestOption && !selectionOption && !source) throw new ClientError('未知参数：' + token, 2);
      let value;
      if (source === 'stdin') {
        if (split >= 0) throw new ClientError(flag + ' 不接受值。', 2);
      } else {
        value = split < 0 ? argv[++i] : token.slice(split + 1);
        if (value === undefined || (split < 0 && value.startsWith('--'))) throw new ClientError(flag + ' 缺少值。', 2);
      }
      if (source) {
        if (args.jsonSource) throw new ClientError('JSON 输入选项必须三选一。', 2);
        args.jsonSource = source;
        args.jsonValue = value;
      } else if (flag === '--tag') {
        if (args.tag !== undefined) throw new ClientError('--tag 只能指定一次。', 2);
        args.tag = value;
      } else if (flag === '--kind') { args.kind = value; args.kindExplicit = true; }
      else if (flag === '--expected-project') args.expected_project = value;
      else if (flag === '--instance-id') args.instance_id = value;
      else if (flag === '--request-id') args.request_id = requestId(value);
      else if (flag === '--ini') args.ini = value;
      else if (flag === '--timeout') args.timeout = value.trim() ? Number(value) : NaN;
      else if (flag === '--method') args.method = value;
      else args.headers.push(value);
    } else if (!args.command) {
      if (!['discover', 'groups', 'list', 'find', 'details', 'schema', 'schemas', 'call', 'prompt', 'resource', 'request', 'status'].includes(token)) throw new ClientError('未知命令：' + token, 2);
      args.command = token;
    } else if (['schema', 'call', 'prompt', 'resource', 'find', 'request', 'status'].includes(args.command) && args.target === undefined) {
      args.target = token;
    } else if (args.command === 'schemas') {
      args.targets.push(token);
    } else throw new ClientError('多余参数：' + token, 2);
  }
  if (!args.command) throw new ClientError('缺少命令；使用 --help 查看用法。', 2);
  if (['schema', 'call', 'prompt', 'resource', 'find', 'request', 'status'].includes(args.command) && args.target === undefined) {
    throw new ClientError('缺少名称、URI、关键字或请求地址。', 2);
  }
  if (args.command === 'schemas') {
    if (args.targets.length && args.jsonSource) throw new ClientError('schemas 的名称列表与 --names 输入只能二选一。', 2);
    if (!args.targets.length && !args.jsonSource) throw new ClientError('缺少工具名称；使用 --help 查看用法。', 2);
  }
  if (!Number.isFinite(args.timeout) || args.timeout <= 0) throw new ClientError('--timeout 必须为正有限数。', 2);
  if (!['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'].includes(args.method)) throw new ClientError('不支持的 HTTP 方法。', 2);
  if (!['tools', 'prompts', 'resources', 'all'].includes(args.kind)) throw new ClientError('--kind 仅允许 tools、prompts、resources、all。', 2);
  return args;
}

async function execute(args) {
  // 路由规范化在 discovery 之前完成；非法输入不产生任何网络请求。
  if (args.command === 'request') args.target = normalizeRequestTarget(args.target);
  let payload = null;
  const headers = Object.create(null);
  if (['call', 'request'].includes(args.command)) {
    payload = inputJson(args);
    if (args.command === 'call') {
      if (!args.jsonSource) payload = {};
      if (!isObject(payload)) throw new ClientError('call 参数必须是 JSON 对象。', 2);
    } else {
      for (const header of args.headers) {
        const colon = header.indexOf(':');
        if (colon < 1 || /[\r\n]/.test(header)) throw new ClientError('请求头格式应为 NAME:VALUE，不能包含换行。', 2);
        const name = header.slice(0, colon).trim();
        const value = header.slice(colon + 1).trim();
        try { http.validateHeaderName(name); http.validateHeaderValue(name, value); }
        catch { throw new ClientError('请求头格式无效。', 2); }
        if (['x-mcp-expected-project', 'x-mcp-instance-id'].includes(name.toLowerCase())) {
          throw new ClientError('绑定头仅由客户端管理，禁止手动注入。', 2);
        }
        headers[name] = value;
      }
    }
  }
  if (args.command === 'details') payload = detailsPayload(args);
  if (args.command === 'schemas') payload = { tools: schemaNames(args) };
  // 名称/关键字/URI 的格式校验在 discovery 之前完成，非法输入不产生任何网络请求。
  if (args.command === 'find') payload = { query: findQuery(args.target) };
  if (args.command === 'prompt' || args.command === 'resource') {
    if (args.command === 'prompt') {
      if (!selectionValue(args.target)) throw new ClientError('提示词名称无效；从 list --kind prompts 选取。', 2);
      payload = { name: args.target };
    } else {
      if (!selectionValue(args.target) || !/^[A-Za-z][A-Za-z0-9+.-]*:[^\s]+$/u.test(args.target)) {
        throw new ClientError('资源 URI 必须是带 scheme 的完整 URI。', 2);
      }
      payload = { uri: args.target };
    }
  }
  const info = await discover(args.ini, args.timeout);
  const base = origin(info.base_url);
  let sameOrigin = false;
  if (args.command === 'request') {
    if (args.target.startsWith('/')) args.target = base + args.target;
    sameOrigin = origin(args.target) === base;
  }
  args.binding = bind(info, args, ['call', 'prompt', 'resource'].includes(args.command) || sameOrigin);
  if (args.command === 'discover') return info;
  if (args.command === 'status') {
    const id = requestId(args.target);
    const instance = args.statusInstance ?? args.client.instance_id;
    if (!instance) throw new ClientError('status 需要 --instance-id 或已有绑定实例。', 2);
    return requestHttp(base + queryPath(id, instance), args.timeout);
  }
  if (args.command === 'groups') {
    const result = await requestHttp(base + '/groups', args.timeout);
    // v2 契约用 tags 索引；服务尚未域重载时仍返回旧 groups 数组，过渡期两者皆接受。
    const hasTags = isObject(result) && isObject(result.tags);
    const hasLegacyGroups = isObject(result) && Array.isArray(result.groups);
    if (!isObject(result) || !(hasTags || hasLegacyGroups) || !responseDetailsLimit(result, info.max_details)
        || result.max_response_bytes !== MAX_RESPONSE_BYTES) throw new ClientError('groups 响应格式或限制无效。', 5);
    logInfo('组详情数量限制：' + (info.max_details === 0 ? '不限数量' : info.max_details) + '；仍受 65536 字节与禁止全集规则约束。');
    return result;
  }
  if (args.command === 'list') {
    if (args.kind !== 'all') {
      const result = await requestHttp(args.kind === 'tools' ? info.tools_url : base + '/' + args.kind, args.timeout);
      if (!isObject(result) || !Array.isArray(result[args.kind])) throw new ClientError(args.kind + ' 响应缺少目录列表。', 5);
      return result;
    }
    const combined = { kind: 'all' };
    for (const k of ['tools', 'prompts', 'resources']) {
      const result = await requestHttp(k === 'tools' ? info.tools_url : base + '/' + k, args.timeout);
      if (!isObject(result) || !Array.isArray(result[k])) throw new ClientError(k + ' 响应缺少目录列表。', 5);
      combined[k] = result[k];
    }
    combined.counts = { tools: combined.tools.length, prompts: combined.prompts.length, resources: combined.resources.length };
    return combined;
  }
  if (args.command === 'details') {
    const limit = info.max_details;
    if (limit > 0 && payload.members && payload.members.length > limit) {
      throw new ClientError('去重后最多选择 ' + limit + ' 个成员（discovery max_details）；未发送详情请求。', 2);
    }
    const result = await requestHttp(base + '/capabilities/details', args.timeout, 'POST', payload, {}, MAX_RESPONSE_BYTES);
    if (!isObject(result) || !Array.isArray(result.items) || result.count !== result.items.length
        || result.count < 1 || (limit > 0 && result.count > limit) || !responseDetailsLimit(result, limit)
        || !result.items.every(isObject)) throw new ClientError('details 响应格式或成员数量无效。', 5);
    return result;
  }
  if (args.command === 'schema') {
    const name = encodeURIComponent(args.target).replace(/[!'()*]/g, c => '%' + c.charCodeAt(0).toString(16).toUpperCase());
    const result = await requestHttp(info.schema_url_template.split('{name}').join(name), args.timeout);
    if (!isObject(result) || result.name !== args.target || !isObject(result.inputSchema)) {
      throw new ClientError('schema 响应缺少匹配名称或完整 inputSchema。', 5);
    }
    return result;
  }
  if (args.command === 'schemas') {
    const limit = info.max_details;
    const names = payload.tools;
    if (limit > 0 && names.length > limit) {
      throw new ClientError('去重后最多一次获取 ' + limit + ' 个 Schema（discovery max_details）；未发送请求。', 2);
    }
    const result = await requestHttp(info.schemas_url || base + '/capabilities/schemas',
      args.timeout, 'POST', payload, {}, MAX_RESPONSE_BYTES);
    if (!isObject(result) || !Array.isArray(result.items) || result.count !== result.items.length
        || result.count < 1 || (limit > 0 && result.count > limit) || !responseDetailsLimit(result, limit)) {
      throw new ClientError('schemas 响应格式或数量无效。', 5);
    }
    const matched = result.items.length === names.length && result.items.every((item, index) =>
      isObject(item) && item.name === names[index] && isObject(item.inputSchema));
    if (!matched) throw new ClientError('schemas 响应的工具名顺序或 inputSchema 与请求不一致。', 5);
    return result;
  }
  if (args.command === 'find') {
    const query = payload.query;
    const needle = query.toLowerCase();
    // 不带 --kind 或显式 --kind all 时，同时搜索工具、提示词、资源三类目录。
    const searchKinds = (!args.kindExplicit || args.kind === 'all')
      ? ['tools', 'prompts', 'resources'] : [args.kind];
    const matchItem = item => isObject(item) && [item.name, item.uri, item.description,
      ...(Array.isArray(item.tags) ? item.tags : [])]
      .some(value => typeof value === 'string' && value.toLowerCase().includes(needle));
    if (searchKinds.length === 1) {
      const entries = await directory(info, args, searchKinds[0]);
      const matches = entries.filter(matchItem);
      return { kind: searchKinds[0], query, matches, count: matches.length, total: entries.length };
    }
    const matches = [];
    const breakdown = {};
    let total = 0;
    for (const k of searchKinds) {
      const entries = await directory(info, args, k);
      const found = entries.filter(matchItem).map(item => Object.assign({ category: k }, item));
      breakdown[k] = { count: found.length, total: entries.length };
      matches.push(...found);
      total += entries.length;
    }
    return { kind: 'all', query, matches, count: matches.length, total, breakdown };
  }
  if (args.command === 'prompt') {
    const name = payload.name;
    const entries = await directory(info, args, 'prompts');
    if (!entries.some(item => isObject(item) && item.name === name)) {
      throw new ClientError('提示词不在 /prompts 目录中：' + name + '；先运行 list --kind prompts。', 2);
    }
    return rpcCall(info, args, 'prompts/get', { name }, 1, 'messages');
  }
  if (args.command === 'resource') {
    const uri = payload.uri;
    const entries = await directory(info, args, 'resources');
    if (!entries.some(item => isObject(item) && item.uri === uri)) {
      throw new ClientError('资源 URI 不在 /resources 目录中，禁止读取未注册 URI：' + uri, 2);
    }
    return rpcCall(info, args, 'resources/read', { uri }, 1, 'contents');
  }
  if (args.command === 'call') {
    return rpcCall(info, args, 'tools/call', { name: args.target, arguments: payload, request_id: args.request_id }, 1);
  }
  let target = args.target;
  if (target.startsWith('/') && !target.startsWith('//')) target = info.base_url.replace(/\/+$/, '') + target;
  if (sameOrigin && isObject(payload) && payload.method === 'tools/call') {
    if (!isObject(payload.params)) throw new ClientError('tools/call 需要 params。', 2);
    payload.params.request_id = requestId(payload.params.request_id ?? randomUUID());
    args.request_id = payload.params.request_id;
  }
  return requestHttp(target, args.timeout, args.method, payload, { ...headers, ...(sameOrigin ? args.binding : {}) });
}

async function main() {
  try {
    const args = parseArgs(process.argv.slice(2));
    if (args.help) { console.log(args.help); return; }
    const client = new Client(args);
    const result = await client._exec(args.command, args);
    console.log(typeof result === 'string' ? result : JSON.stringify(result, null, 2));
  } catch (error) {
    console.error('错误：' + error.message);
    process.exitCode = error instanceof ClientError ? error.code : 2;
  }
}

// ---------------------------------------------------------------------------
// 作为代码库使用：require('../scripts/unity_bridge.js')
// 所有库函数复用上面的 execute()，因此与 CLI 共享同一套 INI 读取、discovery
// 校验、数量/字节上限、目录白名单、不重试与错误码，不存在两套实现。
// ---------------------------------------------------------------------------

function resultText(result) {
  const content = isObject(result) && Array.isArray(result.content) ? result.content : [];
  return content.filter(isObject).filter(item => typeof item.text === 'string')
    .map(item => item.text).join('');
}

function resultJson(result) {
  const text = resultText(result);
  if (!text) throw new ClientError('工具结果不含文本内容。', 5);
  try { return JSON.parse(text); }
  catch { throw new ClientError('工具结果文本不是 JSON：' + text.slice(0, 200), 5); }
}

function recordCall(entry) {
  const wf = workflowStorage.getStore();
  if (wf) wf.calls.push(entry);
}

// 库参数先序列化成文本再交给 CLI 的 inputJson，从而继承其有限数/UTF-8 校验。
function jsonSourceValue(value) {
  let text;
  try {
    text = JSON.stringify(value === undefined ? null : value, (_key, item) => {
      if (typeof item === 'number' && !Number.isFinite(item)) {
        throw new ClientError('参数含非有限数值（NaN/Infinity）。', 2);
      }
      return item;
    });
  } catch (error) {
    if (error instanceof ClientError) throw error;
    throw new ClientError('参数无法序列化为 JSON：' + error.message, 2);
  }
  if (typeof text !== 'string') throw new ClientError('参数无法序列化为 JSON。', 2);
  return text;
}

class Client {
  constructor(options = {}) {
    this.ini = options.ini ?? path.join(os.homedir(), '.unity-mcp', 'skill.ini');
    this.timeout = options.timeout ?? 15;
    this.verbose = Boolean(options.verbose);
    this.expected_project = expectedProject(options.expected_project);
    this.instance_id = options.instance_id;
    if (this.instance_id !== undefined && (typeof this.instance_id !== 'string' || !this.instance_id.trim())) {
      throw new ClientError('instance_id 不能为空。', 2);
    }
  }

  _exec(command, overrides = {}) {
    const args = {
      command, ini: this.ini, timeout: this.timeout,
      method: 'GET', kind: 'tools', headers: [], targets: [], ...overrides,
    };
    args.client = this;
    if (command === 'call') args.request_id = requestId(args.request_id ?? randomUUID());
    return quietStorage.run(!this.verbose, async () => {
      try { return await execute(args); }
      catch (error) {
        if (args.request_id) {
          error.request_id = args.request_id;
          error.instance_id = args.binding?.['X-Mcp-Instance-Id'] ?? this.instance_id ?? '';
          error.query_path = queryPath(error.request_id, error.instance_id);
          error.message += '；' + JSON.stringify({ request_id: error.request_id,
            instance_id: error.instance_id, query_path: error.query_path });
        }
        throw error;
      }
    });
  }

  // 所有方法统一为 async：参数校验失败也是 rejected Promise，调用方只需 await / catch。
  async discover() { return this._exec('discover'); }
  async groups() { return this._exec('groups'); }
  async list(kind = 'tools') { return this._exec('list', { kind }); }
  async find(query, kind = 'all') { return this._exec('find', { target: query, kind, kindExplicit: true }); }
  async schema(name) { return this._exec('schema', { target: name }); }
  async schemas(names) {
    return this._exec('schemas', { targets: Array.isArray(names) ? names : [names] });
  }
  async details(options = {}) {
    const { tag, members } = options || {};
    if ((tag !== undefined) === (members !== undefined)) {
      throw new ClientError('details 需要 tag 或 members，且只能选一种。', 2);
    }
    return tag !== undefined
      ? this._exec('details', { tag })
      : this._exec('details', { jsonSource: 'text', jsonValue: jsonSourceValue(members) });
  }
  async prompt(name) { return this._exec('prompt', { target: name }); }
  async resource(uri) { return this._exec('resource', { target: uri }); }
  async requestStatus(id, instanceId) {
    return this._exec('status', { target: requestId(id), statusInstance: instanceId });
  }
  async call(name, args = {}, options = {}) {
    return this._exec('call', { target: name, request_id: options.request_id,
      jsonSource: 'text', jsonValue: jsonSourceValue(args) });
  }
  async request(target, options = {}) {
    const overrides = { target, method: options.method || 'GET' };
    if (Array.isArray(options.headers)) overrides.headers = options.headers.slice();
    if (options.data !== undefined) {
      overrides.jsonSource = 'text';
      overrides.jsonValue = jsonSourceValue(options.data);
    }
    return this._exec('request', overrides);
  }

  /** 调用工具并返回 MCP result 对象（不是完整 JSON-RPC 信封）。 */
  async callSkill(name, args = {}, options = {}) {
    const started = Date.now();
    try {
      const result = (await this.call(name, args, options)).result;
      recordCall({ name, arguments: args, ok: true, result, elapsed_ms: Date.now() - started });
      return result;
    } catch (error) {
      recordCall({
        name, arguments: args, ok: false, elapsed_ms: Date.now() - started,
        error: { code: error instanceof ClientError ? error.code : 5, message: error.message },
      });
      throw error;
    }
  }
  callSkillText(name, args = {}) { return this.callSkill(name, args).then(resultText); }
  callSkillJson(name, args = {}) { return this.callSkill(name, args).then(resultJson); }
}

let defaultClient = null;
function getClient() {
  if (!defaultClient) defaultClient = new Client();
  return defaultClient;
}
function configure(options = {}) {
  defaultClient = new Client(options);
  return defaultClient;
}

/** 把一组程序化调用归入命名工作流；回调内的 callSkill 等自动记录。 */
async function workflowContext(name, description, fn, options = {}) {
  if (typeof description === 'function') {
    options = fn && typeof fn === 'object' ? fn : {};
    fn = description;
    description = '';
  }
  if (typeof fn !== 'function') throw new ClientError('workflowContext 需要回调函数。', 2);
  const started = Date.now();
  const wf = {
    name, description: description || '', calls: [], startedMs: started, duration_ms: null,
    summary() {
      return {
        name: this.name, description: this.description, calls: this.calls.length,
        succeeded: this.calls.filter(item => item.ok).length,
        failed: this.calls.filter(item => !item.ok).length,
        duration_ms: this.duration_ms === null ? Date.now() - this.startedMs : this.duration_ms,
        steps: this.calls,
      };
    },
  };
  return workflowStorage.run(wf, async () => {
    try { return await fn(wf); }
    finally {
      wf.duration_ms = Date.now() - started;
      if (options.verbose) {
        console.error('工作流 ' + wf.name + '：' + wf.calls.length + ' 次调用，耗时 ' + wf.duration_ms + ' ms');
      }
    }
  });
}

const connectionInfo = () => getClient().discover();
const getGroups = () => getClient().groups();
const listCapabilities = (kind = 'tools') => getClient().list(kind);
const findCapabilities = (query, kind = 'all') => getClient().find(query, kind);
const getDetails = options => getClient().details(options);
const getSchema = name => getClient().schema(name);
const getSchemas = names => getClient().schemas(names);
const callTool = (name, args = {}, options = {}) => getClient().call(name, args, options);
const callSkill = (name, args = {}, options = {}) => getClient().callSkill(name, args, options);
const requestStatus = (id, instanceId) => getClient().requestStatus(id, instanceId);
const callSkillText = (name, args = {}) => getClient().callSkillText(name, args);
const callSkillJson = (name, args = {}) => getClient().callSkillJson(name, args);
const readPrompt = name => getClient().prompt(name);
const readResource = uri => getClient().resource(uri);
const requestRoute = (target, options = {}) => getClient().request(target, options);

module.exports = {
  Client, ClientError, configure, getClient,
  connectionInfo, getGroups, listCapabilities, findCapabilities, getDetails,
  getSchema, getSchemas, callTool, callSkill, callSkillText, callSkillJson,
  readPrompt, readResource, requestRoute, requestStatus, workflowContext,
  resultText, resultJson, normalizeRequestTarget,
  DEFAULT_MAX_DETAILS, MAX_RESPONSE_BYTES,
};

if (require.main === module) {
  process.once('SIGINT', () => {
    console.error('已中断；未重试，写操作结果可能未知。');
    process.exit(130);
  });
  main();
}
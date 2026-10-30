#!/usr/bin/env node
'use strict';

// 仅使用 Node.js 18+ 标准库；运行时由技能读取 INI 后选择。
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const https = require('node:https');
const { TextDecoder } = require('node:util');

class ClientError extends Error {
  constructor(message, code) {
    super(message);
    this.code = code;
  }
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
        if (response.statusCode < 200 || response.statusCode >= 300) {
          finish(new ClientError('HTTP ' + response.statusCode + '：请求失败或重定向被拒绝；未重试。', 4));
          response.destroy();
          return;
        }
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
          finish(null, result);
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
    if (!info.schema_url_template.includes('{name}')) throw new Error('schema URL 缺少 {name}');
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
  // 原样保留 script_language 及将来扩展的字段，不据此执行任何命令。
  console.error('连接信息：' + JSON.stringify(info));
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

function help(command) {
  const usage = {
    discover: 'discover',
    groups: 'groups',
    list: 'list [--kind tools|prompts|resources]',
    details: 'details (--tag TAG | --members JSON数组 | --members-file FILE | --members-stdin)\n  去重成员上限由 discovery 的 max_details 决定（默认32，0不限数量），响应最多 65536 字节；禁止全量选择；标签从 groups 获取。',
    schema: 'schema NAME',
    call: 'call NAME [--args JSON | --args-file FILE | --args-stdin]',
    request: 'request TARGET [--method GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS]\n  [--header NAME:VALUE ...] [--data JSON | --data-file FILE | --data-stdin]',
  };
  return '用法：node scripts/unity_mcp.js [--ini PATH] [--timeout SECONDS] '
    + (usage[command] || 'discover|groups|list|details|schema|call|request')
    + '\n默认 INI：~/.unity-mcp/skill.ini；超时：15 秒（正有限数）。'
    + '\n每次读取 INI 并验证 discovery；不重试、不跟随重定向，验证 TLS。'
    + '\nJSON 文件/标准输入支持 UTF-8 BOM；call 默认传 {}，request 支持服务路由或授权的绝对 HTTP URL。'
    + '\n-h / --help 显示帮助；各子命令可用 --help 查看选项。'
    + '\n退出码：0 成功；2 参数/配置；3 连接/TLS/超时；4 HTTP；5 响应格式；6 MCP 执行失败；130 中断。';
}

function parseArgs(argv) {
  const args = { ini: path.join(os.homedir(), '.unity-mcp', 'skill.ini'), timeout: 15, headers: [], method: 'GET', kind: 'tools' };
  let positionalOnly = false;
  for (let i = 0; i < argv.length; i++) {
    const token = argv[i];
    if (!positionalOnly && (token === '--help' || token === '-h')) return { help: help(args.command) };
    if (!positionalOnly && token === '--') { positionalOnly = true; continue; }
    if (!positionalOnly && token.startsWith('-')) {
      const split = token.indexOf('=');
      const flag = split < 0 ? token : token.slice(0, split);
      const prefix = { call: '--args', request: '--data', details: '--members' }[args.command];
      const source = prefix && (flag === prefix ? 'text' : flag === prefix + '-file' ? 'file' : flag === prefix + '-stdin' ? 'stdin' : null);
      const global = !args.command && ['--ini', '--timeout'].includes(flag);
      const requestOption = args.command === 'request' && ['--method', '--header'].includes(flag);
      const selectionOption = (args.command === 'list' && flag === '--kind') || (args.command === 'details' && flag === '--tag');
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
      } else if (flag === '--kind') args.kind = value;
      else if (flag === '--ini') args.ini = value;
      else if (flag === '--timeout') args.timeout = value.trim() ? Number(value) : NaN;
      else if (flag === '--method') args.method = value;
      else args.headers.push(value);
    } else if (!args.command) {
      if (!['discover', 'groups', 'list', 'details', 'schema', 'call', 'request'].includes(token)) throw new ClientError('未知命令：' + token, 2);
      args.command = token;
    } else if (['schema', 'call', 'request'].includes(args.command) && args.target === undefined) {
      args.target = token;
    } else throw new ClientError('多余参数：' + token, 2);
  }
  if (!args.command) throw new ClientError('缺少命令；使用 --help 查看用法。', 2);
  if (['schema', 'call', 'request'].includes(args.command) && args.target === undefined) throw new ClientError('缺少工具名称或请求地址。', 2);
  if (!Number.isFinite(args.timeout) || args.timeout <= 0) throw new ClientError('--timeout 必须为正有限数。', 2);
  if (!['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'].includes(args.method)) throw new ClientError('不支持的 HTTP 方法。', 2);
  if (!['tools', 'prompts', 'resources'].includes(args.kind)) throw new ClientError('--kind 仅允许 tools、prompts、resources。', 2);
  return args;
}

async function execute(args) {
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
        headers[name] = value;
      }
    }
  }
  if (args.command === 'details') payload = detailsPayload(args);
  const info = await discover(args.ini, args.timeout);
  if (args.command === 'discover') return info;
  const base = origin(info.base_url);
  if (args.command === 'groups') {
    const result = await requestHttp(base + '/groups', args.timeout);
    if (!isObject(result) || !Array.isArray(result.groups) || !responseDetailsLimit(result, info.max_details)
        || result.max_response_bytes !== MAX_RESPONSE_BYTES) throw new ClientError('groups 响应格式或限制无效。', 5);
    console.error('组详情数量限制：' + (info.max_details === 0 ? '不限数量' : info.max_details) + '；仍受 65536 字节与禁止全集规则约束。');
    return result;
  }
  if (args.command === 'list') {
    const result = await requestHttp(args.kind === 'tools' ? info.tools_url : base + '/' + args.kind, args.timeout);
    if (!isObject(result) || !Array.isArray(result[args.kind])) throw new ClientError(args.kind + ' 响应缺少目录列表。', 5);
    return result;
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
  if (args.command === 'call') {
    const envelope = { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: args.target, arguments: payload } };
    const result = await requestHttp(info.base_url.replace(/\/+$/, '') + '/mcp', args.timeout, 'POST', envelope);
    if (!isObject(result) || result.jsonrpc !== '2.0' || result.id !== 1) throw new ClientError('无效 JSON-RPC 响应。', 5);
    if (Object.hasOwn(result, 'error')) throw new ClientError('JSON-RPC error：' + JSON.stringify(result.error), 6);
    if (!isObject(result.result)) throw new ClientError('JSON-RPC 响应缺少 result 对象。', 5);
    if (result.result.isError) throw new ClientError('MCP isError：' + JSON.stringify(result.result), 6);
    return result;
  }
  let target = args.target;
  if (target.startsWith('/') && !target.startsWith('//')) target = info.base_url.replace(/\/+$/, '') + target;
  return requestHttp(target, args.timeout, args.method, payload, headers);
}

async function main() {
  try {
    const args = parseArgs(process.argv.slice(2));
    if (args.help) { console.log(args.help); return; }
    const result = await execute(args);
    console.log(typeof result === 'string' ? result : JSON.stringify(result, null, 2));
  } catch (error) {
    console.error('错误：' + error.message);
    process.exitCode = error instanceof ClientError ? error.code : 2;
  }
}

process.once('SIGINT', () => {
  console.error('已中断；未重试，写操作结果可能未知。');
  process.exit(130);
});
main();
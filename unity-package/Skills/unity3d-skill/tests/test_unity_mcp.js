'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');

const cli = path.resolve(__dirname, '../scripts/unity_mcp.js');
const lib = require(cli);

async function fixture(t) {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'unity-mcp-js-'));
  const ini = path.join(dir, 'unity.ini');
  const requests = [];
  const state = { discovery: { max_details: 8 }, mode: 'ok' };
  const server = http.createServer(async (req, res) => {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const text = Buffer.concat(chunks).toString('utf8');
    const entry = { url: req.url, method: req.method, headers: req.headers, body: text ? JSON.parse(text) : null };
    requests.push(entry);
    const send = (data, status = 200, headers = {}) => {
      res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', ...headers });
      res.end((state.bom ? '\ufeff' : '') + JSON.stringify(data));
    };
    const base = 'http://' + req.headers.host;
    if (req.url === '/discovery') {
      const info = {
        base_url: base, port: server.address().port,
        tools_url: base + '/tools', schema_url_template: base + '/tools/{name}/schema',
        details_url: base + '/capabilities/details', schemas_url: base + '/capabilities/schemas',
        project_path: '测试项目', process_id: 123, updated_at: 'now', ...state.discovery,
      };
      send(state.discoveryRaw === undefined ? info : state.discoveryRaw);
    } else if (req.url === '/groups') {
      send(state.groupsResponse === undefined ? { groups: [{ tag: 'hierarchy-management', label: '层级管理', counts: { tools: 2, prompts: 1, resources: 1 } }], max_details: state.discovery.max_details ?? 32, max_response_bytes: 65536 } : state.groupsResponse);
    } else if (req.url === '/prompts') {
      send(state.promptsResponse === undefined
        ? { prompts: [{ name: 'foo', description: '提示词', tags: ['hierarchy-management'] }], count: 1 }
        : state.promptsResponse);
    } else if (req.url === '/resources') {
      send(state.resourcesResponse === undefined
        ? { resources: [{ uri: 'res://foo', name: 'foo', description: '资源定义', mimeType: 'text/plain', tags: ['hierarchy-management'] }], count: 1 }
        : state.resourcesResponse);
    } else if (req.url === '/capabilities/details') {
      if (state.detailsStatus) { send({ error: '详情选择被拒绝' }, state.detailsStatus); return; }
      if (state.detailsDisconnect) { req.socket.destroy(); return; }
      if (state.detailsTimeout) return;
      const members = entry.body.members || [{ kind: 'tool', name: 'hierarchy_search' }];
      const items = members.map(member => ({ ...member, description: '注册定义', ...(member.kind === 'tool' ? { inputSchema: { type: 'object' } } : member.kind === 'prompt' ? { arguments: [] } : { mimeType: 'text/plain' }) }));
      send(state.detailsResponse === undefined ? { items, count: items.length, max_details: state.discovery.max_details ?? 32 } : state.detailsResponse);
    } else if (req.url === '/capabilities/schemas') {
      if (state.schemasStatus) { send({ error: '批量 schema 被拒绝' }, state.schemasStatus); return; }
      if (state.schemasResponse !== undefined) { send(state.schemasResponse); return; }
      const items = entry.body.tools.map(name => ({
        name, description: '完整说明 ' + name,
        inputSchema: { type: 'object', properties: { [name]: { type: 'string' } } },
      }));
      send({ items, count: items.length, max_details: state.discovery.max_details ?? 32 });
    } else if (req.url === '/tools') {
      send(state.toolsResponse !== undefined ? state.toolsResponse
        : state.mode === 'bad_tools' ? {} : { tools: [{ name: 'demo', description: '示例工具' }], count: 1 });
    } else if (req.url.startsWith('/tools/') && req.url.endsWith('/schema')) {
      const name = decodeURIComponent(req.url.slice(7, -7));
      if (name === 'unknown') { send({}, 404); return; }
      send(state.mode === 'bad_schema' ? { name: 'wrong' } : { name, description: '完整说明', inputSchema: { type: 'object', properties: { '值': { type: 'string' } } } });
    } else if (req.url === '/mcp') {
      const rpcId = entry.body.id;
      if (state.mode === 'redirect') { send({}, 307, { Location: base + '/replayed' }); return; }
      if (state.mode === 'http_error') { send({}, 503); return; }
      if (state.mode === 'disconnect') { req.socket.destroy(); return; }
      if (state.mode === 'truncated') { res.writeHead(200, { 'Content-Length': '1000' }); res.end('{}'); return; }
      if (state.mode === 'timeout') return;
      if (state.mode === 'rpc_error') { send({ jsonrpc: '2.0', id: rpcId, error: { code: -1, message: '失败' } }); return; }
      if (state.mode === 'is_error') { send({ jsonrpc: '2.0', id: rpcId, result: { isError: true } }); return; }
      if (state.mode === 'bad_rpc') { send({ jsonrpc: '2.0', id: 2, result: {} }); return; }
      if (state.mode === 'no_result') { send({ jsonrpc: '2.0', id: rpcId }); return; }
      if (state.failToolName && entry.body.params && entry.body.params.name === state.failToolName) {
        send({ jsonrpc: '2.0', id: rpcId, result: { isError: true, content: [{ type: 'text', text: '工具失败' }] } });
        return;
      }
      if (entry.body.method === 'prompts/get') {
        send({ jsonrpc: '2.0', id: rpcId, result: { description: '提示词', messages: [{ role: 'user', content: [{ type: 'text', text: '提示词正文' }] }] } });
        return;
      }
      if (entry.body.method === 'resources/read') {
        send({ jsonrpc: '2.0', id: rpcId, result: { contents: [{ uri: entry.body.params.uri, mimeType: 'text/plain', text: '资源内容' }] } });
        return;
      }
      send({ jsonrpc: '2.0', id: rpcId, result: { content: [{ type: 'text', text: '完成' }] } });
    } else if (req.url === '/invalid-utf8') {
      res.end(Buffer.from([0xc3, 0x28]));
    } else if (req.url === '/text') {
      res.end('\ufeff普通文本');
    } else if (req.url === '/redirect') {
      send({}, 302, { Location: base + '/replayed' });
    } else if (req.url === '/custom' || req.url === '/replayed') {
      send({ custom: true, method: req.method, body: entry.body });
    } else send({}, 404);
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, resolve);
  });
  t.after(async () => {
    const closed = new Promise(resolve => server.close(resolve));
    server.closeAllConnections();
    await closed;
    await fs.rm(dir, { recursive: true, force: true });
  });
  const port = server.address().port;
  const base = 'http://127.0.0.1:' + port;
  const writeIni = (text = '[unity3d-skilled-mcp]\nbase_url=' + base + '\nport=' + port + '\n') => fs.writeFile(ini, text);
  await writeIni();
  function run(args, input = '', env = {}, useIni = true) {
    return new Promise((resolve, reject) => {
      const child = spawn(process.execPath, [cli, ...(useIni ? ['--ini', ini] : []), ...args], {
        env: { ...process.env, ...env }, stdio: ['pipe', 'pipe', 'pipe'],
      });
      let stdout = '';
      let stderr = '';
      const timer = setTimeout(() => { child.kill(); reject(new Error('CLI 测试超时')); }, 10000);
      child.stdout.setEncoding('utf8').on('data', text => { stdout += text; });
      child.stderr.setEncoding('utf8').on('data', text => { stderr += text; });
      child.on('error', error => { clearTimeout(timer); reject(error); });
      child.on('close', (code, signal) => { clearTimeout(timer); resolve({ code, signal, stdout, stderr }); });
      child.stdin.on('error', error => { if (error.code !== 'EPIPE') reject(error); });
      child.stdin.end(input);
    });
  }
  return { dir, ini, port, base, state, requests, writeIni, run };
}

function success(result) {
  assert.equal(result.code, 0, result.stderr);
  assert.equal(result.signal, null);
  return JSON.parse(result.stdout);
}
function failure(result, code) {
  assert.equal(result.code, code, result.stderr);
  assert.equal(result.stdout, '');
  assert.match(result.stderr, /错误：/);
  assert.doesNotMatch(result.stderr, /\n\s+at /);
}

test('discover 保留未知扩展字段；旧 INI 的 script_language 被忽略', async t => {
  const f = await fixture(t);
  assert.equal(success(await f.run(['discover'])).port, f.port);
  await f.writeIni('[unity3d-skilled-mcp]\nbase_url=' + f.base + '\nport=' + f.port + '\nscript_language=python\nfuture_setting=保留\n');
  f.state.discovery = { extension: { version: 2 } };
  const result = await f.run(['discover']);
  assert.deepEqual(success(result).extension, { version: 2 });
  assert.equal(success(result).script_language, undefined);
  assert.match(result.stderr, /连接信息：/);
  assert.match(result.stderr, /测试项目/);
});

test('list 只取摘要；schema 按需获取并编码工具名', async t => {
  const f = await fixture(t);
  const tools = success(await f.run(['list']));
  assert.equal(tools.tools[0].name, 'demo');
  assert.equal(JSON.stringify(tools).includes('inputSchema'), false);
  assert.deepEqual(f.requests.map(r => r.url), ['/discovery', '/tools']);
  assert.deepEqual(success(await f.run(['list', '--kind', 'tools'])), tools);
  assert.deepEqual(f.requests.map(r => r.url), ['/discovery', '/tools', '/discovery', '/tools']);
  const name = "中文 /?#&!'()*";
  const schema = success(await f.run(['schema', name]));
  assert.equal(schema.name, name);
  assert.equal(schema.inputSchema.properties['值'].type, 'string');
  assert.equal(f.requests.at(-1).url, '/tools/%E4%B8%AD%E6%96%87%20%2F%3F%23%26%21%27%28%29%2A/schema');
});

test('call 默认对象、内联 JSON 和 JSON-RPC 信封', async t => {
  const f = await fixture(t);
  success(await f.run(['call', 'demo']));
  success(await f.run(['call', 'demo', '--args={"文本":"你好"}']));
  const calls = f.requests.filter(r => r.url === '/mcp');
  assert.equal(calls.length, 2);
  assert.deepEqual(calls[0].body, { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'demo', arguments: {} } });
  assert.deepEqual(calls[1].body.params.arguments, { '文本': '你好' });
  assert.equal(calls[1].method, 'POST');
  assert.match(calls[1].headers['content-type'], /application\/json/);
});

test('INI、响应、JSON 文件和 stdin 支持 UTF-8 BOM', async t => {
  const f = await fixture(t);
  await f.writeIni('\ufeff[unity3d-skilled-mcp]\nbase_url=' + f.base + '\nport=' + f.port + '\n');
  f.state.bom = true;
  const file = path.join(f.dir, '中文 参数.json');
  await fs.writeFile(file, '\ufeff{"文本":"你好"}');
  success(await f.run(['call', 'demo', '--args-file', file]));
  success(await f.run(['call', 'demo', '--args-stdin'], '\ufeff{"文本":"你好"}'));
  for (const call of f.requests.filter(r => r.url === '/mcp')) assert.deepEqual(call.body.params.arguments, { '文本': '你好' });
});

test('request 支持所有方法、重复头、JSON 文件与 stdin', async t => {
  const f = await fixture(t);
  const file = path.join(f.dir, 'body.json');
  await fs.writeFile(file, '\ufeff{"value":1}');
  for (const method of ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS']) {
    const result = await f.run(['request', '/custom', '--method', method, '--header', 'X-First: one', '--header', 'X-Second: two', '--data-file', file]);
    assert.equal(result.code, 0, result.stderr);
    const request = f.requests.at(-1);
    assert.equal(request.method, method);
    assert.deepEqual(request.body, { value: 1 });
    assert.equal(request.headers['x-first'], 'one');
    assert.equal(request.headers['x-second'], 'two');
  }
  success(await f.run(['request', '/custom', '--method', 'POST', '--data-stdin'], '\ufeff[1,"中文"]'));
  assert.deepEqual(f.requests.at(-1).body, [1, '中文']);
  success(await f.run(['request', '/custom', '--method', 'POST', '--data', 'false']));
  assert.equal(f.requests.at(-1).body, false);
});

test('外部绝对 HTTP 地址仍先验证本地 discovery', async t => {
  const local = await fixture(t);
  const external = await fixture(t);
  success(await local.run(['request', external.base + '/custom', '--method', 'POST', '--data', '{"外部":true}']));
  assert.deepEqual(local.requests.map(r => r.url), ['/discovery']);
  assert.deepEqual(external.requests.map(r => r.url), ['/custom']);
  assert.deepEqual(external.requests[0].body, { '外部': true });
  local.state.discoveryRaw = 'invalid';
  failure(await local.run(['request', external.base + '/custom']), 5);
  assert.equal(external.requests.length, 1);
});

test('默认用户 INI 和 ~/ 路径展开', async t => {
  const f = await fixture(t);
  const configDir = path.join(f.dir, '.unity-mcp');
  await fs.mkdir(configDir, { recursive: true });
  await fs.copyFile(f.ini, path.join(configDir, 'skill.ini'));
  const env = { HOME: f.dir, USERPROFILE: f.dir };
  assert.equal(success(await f.run(['discover'], '', env, false)).port, f.port);
  assert.equal(success(await f.run(['--ini', '~/.unity-mcp/skill.ini', 'discover'], '', env, false)).port, f.port);
});

test('discovery 不发布 mcp_url，call 由 base_url 推导 /mcp', async t => {
  const f = await fixture(t);
  assert.equal(Object.hasOwn(success(await f.run(['discover'])), 'mcp_url'), false);
  success(await f.run(['call', 'demo']));
  assert.equal(f.requests.at(-1).url, '/mcp');
});

test('每次调用重新读取 INI 端口，不使用旧缓存', async t => {
  const first = await fixture(t);
  const second = await fixture(t);
  assert.equal(success(await first.run(['discover'])).port, first.port);
  await first.writeIni('[unity3d-skilled-mcp]\nbase_url=' + second.base + '\nport=' + second.port + '\n');
  assert.equal(success(await first.run(['discover'])).port, second.port);
  assert.notEqual(first.port, second.port);
  assert.equal(first.requests.length, 1);
  assert.equal(second.requests.length, 1);
});

test('仅 port 的旧 INI 支持 localhost 回退及 DEFAULT 节', async t => {
  const f = await fixture(t);
  await f.writeIni('\ufeff[DEFAULT]\nport=' + f.port + '\n[unity3d-skilled-mcp]\nfuture=unknown\n');
  assert.equal(success(await f.run(['discover'])).base_url, 'http://localhost:' + f.port);
});

test('缺失、重复、错误配置及端口不一致返回 2 且不发送请求', async t => {
  const f = await fixture(t);
  await fs.unlink(f.ini);
  failure(await f.run(['discover']), 2);
  for (const body of ['', '[other]\nport=1', '[unity3d-skilled-mcp]\nport=0', '[unity3d-skilled-mcp]\nport=65536', '[unity3d-skilled-mcp]\nport=no', '[unity3d-skilled-mcp]\nport=1\nport=2', '[unity3d-skilled-mcp]\nbase_url=' + f.base + '\nport=1', '[unity3d-skilled-mcp]\nbase_url=' + f.base + '/path', '[unity3d-skilled-mcp]\nbase_url=' + f.base + '?q=1']) {
    await f.writeIni(body);
    failure(await f.run(['discover']), 2);
  }
  for (const suffix of ['/./', '/%2e/', '/path/../', '?']) {
    await f.writeIni('[unity3d-skilled-mcp]\nbase_url=' + f.base + suffix + '\n');
    failure(await f.run(['discover']), 2);
  }
  assert.equal(f.requests.length, 0);
});

test('拒绝不安全 URL，不跟随 scheme-relative 地址或凭据', async t => {
  const f = await fixture(t);
  for (const target of ['//example.invalid/custom', 'file:///tmp/data', 'ftp://example.invalid', 'http://user:pass@127.0.0.1:' + f.port, f.base + '/custom#secret', f.base + '/a b', f.base + '/a\nb', 'http://127.0.0.1:0', 'http://127.0.0.1:65536', 'http://127.0.0.1:/', 'http://127.0.0.1\\@example.invalid', 'https://', '/\\example.invalid']) {
    failure(await f.run(['request', target]), 2);
  }
  assert.ok(f.requests.every(r => r.url === '/discovery'));
});

test('拒绝伪造 discovery 的跨源端点、错误类型和缺失字段', async t => {
  const f = await fixture(t);
  const external = await fixture(t);
  const cases = [
    { base_url: external.base }, { port: f.port + 1 },
    { tools_url: external.base + '/tools' }, { schema_url_template: external.base + '/{name}' },
    { schema_url_template: f.base + '/schema' },
    { project_path: 1 }, { updated_at: null }, { process_id: '123' }, { process_id: true },
  ];
  for (const discovery of cases) {
    f.state.discovery = discovery;
    failure(await f.run(['call', 'demo']), 5);
  }
  for (const raw of [null, [], 'not json', {}]) {
    f.state.discoveryRaw = raw;
    failure(await f.run(['discover']), 5);
  }
  assert.ok(f.requests.every(r => r.url === '/discovery'));
  assert.equal(external.requests.length, 0);
});

test('HTTP 错误与重定向不重试、不重放 POST', async t => {
  const f = await fixture(t);
  for (const mode of ['http_error', 'redirect']) {
    f.state.mode = mode;
    const result = await f.run(['call', 'demo']);
    failure(result, 4);
    assert.match(result.stderr, /未重试/);
  }
  failure(await f.run(['request', '/redirect']), 4);
  failure(await f.run(['schema', 'unknown']), 4);
  assert.equal(f.requests.filter(r => r.url === '/mcp').length, 2);
  assert.equal(f.requests.filter(r => r.url === '/replayed').length, 0);
});

test('JSON-RPC error 和 MCP isError 返回 6 且仅调用一次', async t => {
  const f = await fixture(t);
  for (const mode of ['rpc_error', 'is_error']) {
    f.state.mode = mode;
    failure(await f.run(['call', 'demo']), 6);
  }
  assert.equal(f.requests.filter(r => r.url === '/mcp').length, 2);
});

test('响应格式错误返回 5，普通文本按原文输出', async t => {
  const f = await fixture(t);
  for (const [mode, args] of [['bad_tools', ['list']], ['bad_schema', ['schema', 'demo']], ['bad_rpc', ['call', 'demo']], ['no_result', ['call', 'demo']]]) {
    f.state.mode = mode;
    failure(await f.run(args), 5);
  }
  failure(await f.run(['request', '/invalid-utf8']), 5);
  const text = await f.run(['request', '/text']);
  assert.equal(text.code, 0, text.stderr);
  assert.equal(text.stdout, '普通文本\n');
});

test('真实断开、中断响应和超时返回 3 且写操作不重试', async t => {
  const f = await fixture(t);
  for (const mode of ['disconnect', 'truncated', 'timeout']) {
    f.state.mode = mode;
    const result = await f.run(['--timeout', '0.2', 'call', 'demo']);
    failure(result, 3);
    assert.match(result.stderr, /未重试，写操作结果可能未知/);
  }
  assert.equal(f.requests.filter(r => r.url === '/mcp').length, 3);
});

test('真实连接拒绝返回 3', async t => {
  const f = await fixture(t);
  const reservation = http.createServer();
  await new Promise(resolve => reservation.listen(0, '127.0.0.1', resolve));
  const port = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  await f.writeIni('[unity3d-skilled-mcp]\nbase_url=http://127.0.0.1:' + port + '\nport=' + port + '\n');
  failure(await f.run(['--timeout', '1', 'discover']), 3);
  assert.equal(f.requests.length, 0);
});

test('HTTPS 握手失败返回 3，不降级为 HTTP', async t => {
  const f = await fixture(t);
  failure(await f.run(['--timeout', '1', 'request', f.base.replace('http:', 'https:') + '/custom']), 3);
  assert.deepEqual(f.requests.map(r => r.url), ['/discovery']);
});

test('非法 JSON、非对象、互斥输入及请求头在网络前失败', async t => {
  const f = await fixture(t);
  const cases = [
    ['call', 'demo', '--args', '{'], ['call', 'demo', '--args', 'NaN'],
    ['call', 'demo', '--args', 'null'], ['call', 'demo', '--args', '[]'],
    ['call', 'demo', '--args', '{"value":1e400}'],
    ['call', 'demo', '--args', '{}', '--args-stdin'], ['call', 'demo', '--args-file', path.join(f.dir, 'missing.json')],
    ['request', '/custom', '--header', 'invalid'], ['request', '/custom', '--header', 'X: one\r\nTwo: bad'],
    ['request', '/custom', '--header', 'Bad Name: value'], ['request', '/custom', '--method', 'TRACE'],
    ['request', '/custom', '--data', 'Infinity'], ['request', '/custom', '--data', '{}', '--data-file', 'other'],
  ];
  for (const args of cases) failure(await f.run(args), 2);
  failure(await f.run(['call', 'demo', '--args-stdin'], Buffer.from([0xc3, 0x28])), 2);
  assert.equal(f.requests.length, 0);
});

test('帮助、缺失参数、未知选项、超时数值和 -- 分隔符', async t => {
  const f = await fixture(t);
  for (const args of [['--help'], ['-h'], ['request', '--help'], ['call', '--help']]) {
    const result = await f.run(args);
    assert.equal(result.code, 0, result.stderr);
    assert.match(result.stdout, /用法：/);
  }
  for (const args of [[], ['schema'], ['call'], ['request'], ['invalid'], ['discover', 'extra'], ['list', '--other'], ['call', 'demo', '--args'], ['call', 'demo', '--args-stdin=true']]) failure(await f.run(args), 2);
  for (const value of ['0', '-1', 'NaN', 'Infinity', '', 'bad']) failure(await f.run(['--timeout=' + value, 'discover']), 2);
  assert.equal(f.requests.length, 0);
  assert.equal(success(await f.run(['--timeout=1', 'schema', '--', '-tool'])).name, '-tool');
});

const detailCases = require('./details_cases.json');

test('groups 与三类目录只取摘要，不调用旧 MCP list', async t => {
  const f = await fixture(t);
  const groups = success(await f.run(['groups']));
  assert.deepEqual(groups.groups[0].counts, { tools: 2, prompts: 1, resources: 1 });
  assert.equal(groups.max_details, 8);
  assert.equal(groups.max_response_bytes, 65536);
  for (const kind of ['tools', 'prompts', 'resources']) {
    const result = success(await f.run(['list', '--kind', kind]));
    assert.equal(result.count, 1);
    assert.equal(result[kind].length, 1);
    assert.doesNotMatch(JSON.stringify(result), /inputSchema|arguments|contents/);
  }
  assert.deepEqual(f.requests.map(r => r.url), ['/discovery', '/groups', '/discovery', '/tools', '/discovery', '/prompts', '/discovery', '/resources']);
});

test('固定 tag 原样 POST，仅获取指定组一次', async t => {
  const f = await fixture(t);
  const groups = success(await f.run(['groups']));
  const tag = groups.groups[0].tag;
  const result = success(await f.run(['--timeout', '2', 'details', '--tag', tag]));
  assert.equal(result.count, 1);
  assert.equal(result.items[0].inputSchema.type, 'object');
  assert.deepEqual(f.requests.map(r => r.url), ['/discovery', '/groups', '/discovery', '/capabilities/details']);
  assert.equal(f.requests.at(-1).method, 'POST');
  assert.deepEqual(f.requests.at(-1).body, { tag });
});

test('动态混合成员去重、保持顺序和类型，不隐式读资源', async t => {
  const f = await fixture(t);
  const mixed = detailCases.mixed;
  const input = [mixed[0], mixed[1], mixed[0], mixed[2], { kind: 'tool', name: 'foo' }];
  const expected = [mixed[0], mixed[1], mixed[2], { kind: 'tool', name: 'foo' }];
  const result = success(await f.run(['details', '--members', JSON.stringify(input)]));
  assert.equal(result.count, 4);
  assert.equal(result.items[2].mimeType, 'text/plain');
  assert.equal(Object.hasOwn(result.items[2], 'contents'), false);
  assert.deepEqual(f.requests.at(-1).body, { members: expected });
  assert.deepEqual(f.requests.map(r => r.url), ['/discovery', '/capabilities/details']);
});

test('members 文件与 stdin 支持 BOM、中文路径和 UTF-8', async t => {
  const f = await fixture(t);
  f.state.bom = true;
  const file = path.join(f.dir, '选择 集合.json');
  const text = '\ufeff' + JSON.stringify(detailCases.mixed);
  await fs.writeFile(file, text);
  success(await f.run(['details', '--members-file', file]));
  success(await f.run(['details', '--members-stdin'], text));
  assert.equal(f.requests.filter(r => r.url === '/capabilities/details').length, 2);
  for (const r of f.requests.filter(r => r.url === '/capabilities/details')) assert.deepEqual(r.body, { members: detailCases.mixed });
});

test('资源 URI 作为 JSON 值原样发送，不作为 HTTP 地址访问', async t => {
  const f = await fixture(t);
  for (const uri of detailCases.uris) {
    const members = [{ kind: 'resource', uri }];
    success(await f.run(['details', '--members', JSON.stringify(members)]));
    assert.deepEqual(f.requests.at(-1).body, { members });
  }
  assert.ok(f.requests.every(r => ['/discovery', '/capabilities/details'].includes(r.url)));
});

test('详情选择器与成员字段校验在网络前失败', async t => {
  const f = await fixture(t);
  for (const tag of detailCases.invalid_tags) failure(await f.run(['details', '--tag', tag]), 2);
  for (const members of detailCases.invalid_members) failure(await f.run(['details', '--members', JSON.stringify(members)]), 2);
  const invalid = [
    ['details'], ['details', '--all'], ['details', '--tag', 'x', '--members', '[]'],
    ['details', '--tag', 'x', '--tag', 'y'], ['details', '--members', '[]', '--members-stdin'],
    ['details', '--members-file', 'missing.json'], ['details', '--members', '{'],
    ['details', '--members', 'NaN'], ['details', '--members-stdin=true'],
    ['details', '--members-stdin', '--unknown'], ['details', '--kind', 'tools'],
    ['details', '--tag'], ['details', '--members'], ['details', '--members-file'],
    ['list', '--kind', 'all'], ['list', '--kind', '*'], ['list', '--kind', 'tool'],
    ['details', '--ta', 'hierarchy-management'], ['list', '--ki', 'tools'],
  ];
  for (const args of invalid) failure(await f.run(args), 2);
  failure(await f.run(['details', '--members-stdin'], Buffer.from([0xc3, 0x28])), 2);
  assert.equal(f.requests.length, 0);
});

test('旧服务显式8：去重八项及单项允许，第九项在 discovery 后 POST 前拒绝', async t => {
  const f = await fixture(t);
  const members = Array.from({ length: 8 }, (_, i) => ({ kind: 'tool', name: 'tool_' + i }));
  success(await f.run(['details', '--members', JSON.stringify([...members, ...members])]));
  assert.deepEqual(f.requests.at(-1).body, { members });
  success(await f.run(['details', '--members', JSON.stringify([members[0]])]));
  const before = f.requests.length;
  failure(await f.run(['details', '--members', JSON.stringify([...members, { kind: 'tool', name: 'ninth' }])]), 2);
  assert.deepEqual(f.requests.slice(before).map(r => r.url), ['/discovery']);
});

for (const [limit, count, code] of [[12, 9, 0], [2, 3, 2], [0, 40, 0]]) {
  test('动态限制 ' + limit + ' 验证 ' + count + ' 项，数量检查在 discovery 后', async t => {
    const f = await fixture(t);
    f.state.discovery.max_details = limit;
    await f.writeIni('[unity3d-skilled-mcp]\nbase_url=' + f.base + '\nmax_details=' + limit + '\n');
    const members = Array.from({ length: count }, (_, i) => ({ kind: 'tool', name: 'tool_' + i }));
    const result = await f.run(['details', '--members', JSON.stringify([...members, ...members])]);
    if (code) {
      failure(result, code);
      assert.deepEqual(f.requests.map(r => r.url), ['/discovery']);
    } else {
      assert.equal(success(result).count, count);
      assert.equal(success(result).max_details, limit);
      assert.deepEqual(f.requests.map(r => r.url), ['/discovery', '/capabilities/details']);
      assert.deepEqual(f.requests.at(-1).body, { members });
    }
    const groups = await f.run(['groups']);
    assert.equal(success(groups).max_details, limit);
    if (limit === 0) assert.match(groups.stderr, /不限数量/);
  });
}

test('旧 INI 和 discovery 都无上限时默认32，33项仅发现不 POST', async t => {
  const f = await fixture(t);
  delete f.state.discovery.max_details;
  assert.equal(success(await f.run(['discover'])).max_details, 32);
  const members = Array.from({ length: 32 }, (_, i) => ({ kind: 'tool', name: 'tool_' + i }));
  assert.equal(success(await f.run(['details', '--members', JSON.stringify(members)])).count, 32);
  const before = f.requests.length;
  failure(await f.run(['details', '--members', JSON.stringify([...members, { kind: 'tool', name: 'extra' }])]), 2);
  assert.deepEqual(f.requests.slice(before).map(r => r.url), ['/discovery']);
});

test('旧 INI 缺字段尊重 discovery，旧 discovery 缺字段回退 INI', async t => {
  const f = await fixture(t);
  f.state.discovery.max_details = 12;
  const members = Array.from({ length: 9 }, (_, i) => ({ kind: 'tool', name: 'tool_' + i }));
  assert.equal(success(await f.run(['details', '--members', JSON.stringify(members)])).count, 9);
  delete f.state.discovery.max_details;
  for (const limit of [12, 0]) {
    await f.writeIni('[unity3d-skilled-mcp]\nbase_url=' + f.base + '\nmax_details=' + limit + '\n');
    assert.equal(success(await f.run(['discover'])).max_details, limit);
    f.state.detailsResponse = { items: members, count: members.length };
    assert.equal(success(await f.run(['details', '--members', JSON.stringify(members)])).count, 9);
  }
});

test('INI max_details 非法值在网络前报配置错误', async t => {
  const f = await fixture(t);
  for (const value of ['-1', '-0', 'NaN', 'Infinity', '1.5', '2.0', '1e2', '', 'no', 'true', '9007199254740992']) {
    await f.writeIni('[unity3d-skilled-mcp]\nbase_url=' + f.base + '\nmax_details=' + value + '\n');
    const result = await f.run(['details', '--tag', 'hierarchy-management']);
    failure(result, 2);
    assert.match(result.stderr, /max_details/);
  }
  assert.equal(f.requests.length, 0);
});

test('INI 与 discovery 上限不一致要求重启，不能抬高或悄悄忽略', async t => {
  const f = await fixture(t);
  for (const [ini, service] of [[32, 8], [2, 12], [0, 12], [12, 0]]) {
    f.state.discovery.max_details = service;
    await f.writeIni('[unity3d-skilled-mcp]\nbase_url=' + f.base + '\nmax_details=' + ini + '\n');
    const before = f.requests.length;
    const result = await f.run(['details', '--tag', 'hierarchy-management']);
    failure(result, 2);
    assert.match(result.stderr, /重启目标 Unity 服务/);
    assert.deepEqual(f.requests.slice(before).map(r => r.url), ['/discovery']);
  }
});

test('discovery 上限非法类型不回退默认也不 POST', async t => {
  const f = await fixture(t);
  for (const limit of [-1, 1.5, '12', null, true, 9007199254740992]) {
    f.state.discovery.max_details = limit;
    failure(await f.run(['details', '--tag', 'hierarchy-management']), 5);
  }
  assert.ok(f.requests.every(r => r.url === '/discovery'));
});

test('详情响应数量动态验证，缺上限字段兼容，显式上限必须一致', async t => {
  const f = await fixture(t);
  for (const [limit, count, code] of [[12, 9, 0], [12, 13, 5], [2, 3, 5], [0, 40, 0]]) {
    f.state.discovery.max_details = limit;
    for (const publish of [true, false]) {
      f.state.detailsResponse = { items: Array(count).fill({}), count, ...(publish ? { max_details: limit } : {}) };
      const result = await f.run(['details', '--tag', 'hierarchy-management']);
      if (code) failure(result, code);
      else assert.equal(success(result).count, count);
    }
  }
  f.state.discovery.max_details = 12;
  for (const limit of [0, 8, 32, -1, 1.5, '12', null, true]) {
    f.state.detailsResponse = { items: [{}], count: 1, max_details: limit };
    failure(await f.run(['details', '--tag', 'hierarchy-management']), 5);
  }
  f.state.discovery.max_details = 0;
  for (const count of [0, -1, '1', true, 1.5, 2]) {
    f.state.detailsResponse = { items: count === 0 ? [] : [{}], count, max_details: 0 };
    failure(await f.run(['details', '--tag', 'hierarchy-management']), 5);
  }
  assert.ok(f.requests.every(r => ['/discovery', '/capabilities/details'].includes(r.url)));
});

test('组响应必须匹配 discovery，缺上限字段兼容且字节限制不变', async t => {
  const f = await fixture(t);
  f.state.discovery.max_details = 12;
  for (const limit of [0, 8, 32, -1, 1.5, '12', null, true]) {
    f.state.groupsResponse = { groups: [], max_details: limit, max_response_bytes: 65536 };
    failure(await f.run(['groups']), 5);
  }
  f.state.groupsResponse = { groups: [], max_response_bytes: 65536 };
  success(await f.run(['groups']));
  f.state.groupsResponse.max_response_bytes = 131072;
  failure(await f.run(['groups']), 5);
});

test('组超限、全集及未知禁用成员错误不回退也不拆分重试', async t => {
  const f = await fixture(t);
  for (const [status, args] of [
    [400, ['--tag', 'oversized-group']],
    [400, ['--members', JSON.stringify(detailCases.mixed)]],
    [404, ['--tag', 'unknown-group']],
    [404, ['--members', '[{"kind":"tool","name":"disabled"}]']],
  ]) {
    f.state.detailsStatus = status;
    const before = f.requests.length;
    const result = await f.run(['details', ...args]);
    failure(result, 4);
    assert.match(result.stderr, new RegExp('HTTP ' + status));
    assert.deepEqual(f.requests.slice(before).map(r => r.url), ['/discovery', '/capabilities/details']);
  }
});

test('详情断连和超时不重试', async t => {
  const f = await fixture(t);
  f.state.detailsDisconnect = true;
  failure(await f.run(['details', '--tag', 'hierarchy-management']), 3);
  f.state.detailsDisconnect = false;
  f.state.detailsTimeout = true;
  failure(await f.run(['--timeout', '0.2', 'details', '--tag', 'hierarchy-management']), 3);
  assert.equal(f.requests.filter(r => r.url === '/capabilities/details').length, 2);
});

test('详情响应计数与八项上限校验', async t => {
  const f = await fixture(t);
  for (const value of [{}, { items: [], count: 0, max_details: 8 }, { items: [{}], count: 2, max_details: 8 },
    { items: Array(9).fill({}), count: 9, max_details: 8 }, { items: [{}], count: 1, max_details: 9 },
    { items: [null], count: 1, max_details: 8 }]) {
    f.state.detailsResponse = value;
    failure(await f.run(['details', '--tag', 'hierarchy-management']), 5);
  }
});

test('详情响应按 UTF-8 字节限制，精确 64 KiB 允许，超过拒绝', async t => {
  const f = await fixture(t);
  const response = { items: [{ kind: 'resource', uri: 'res://foo', description: '' }], count: 1, max_details: 8 };
  const overhead = Buffer.byteLength(JSON.stringify(response));
  response.items[0].description = 'a'.repeat(65536 - overhead);
  f.state.detailsResponse = response;
  success(await f.run(['details', '--members', '[{"kind":"resource","uri":"res://foo"}]']));
  response.items[0].description += 'a';
  failure(await f.run(['details', '--tag', 'hierarchy-management']), 5);
  response.items[0].description = '中'.repeat(22000);
  failure(await f.run(['details', '--tag', 'hierarchy-management']), 5);
  assert.equal(f.requests.filter(r => r.url === '/capabilities/details').length, 3);
});

test('schemas 一次请求多个工具，去重保序且单次 POST', async t => {
  const f = await fixture(t);
  const result = success(await f.run(['schemas', 'hierarchy_search', 'hierarchy_apply', 'hierarchy_search']));
  assert.equal(result.count, 2);
  assert.deepEqual(result.items.map(item => item.name), ['hierarchy_search', 'hierarchy_apply']);
  assert.equal(result.items[1].inputSchema.type, 'object');
  assert.equal(result.max_details, 8);
  assert.deepEqual(f.requests.map(r => r.url), ['/discovery', '/capabilities/schemas']);
  assert.equal(f.requests.at(-1).method, 'POST');
  assert.deepEqual(f.requests.at(-1).body, { tools: ['hierarchy_search', 'hierarchy_apply'] });
});

test('schemas 支持 --names 内联、文件与 stdin，包含 BOM 和中文路径', async t => {
  const f = await fixture(t);
  f.state.bom = true;
  const names = ['层级搜索', 'apply prefab'];
  success(await f.run(['schemas', '--names', JSON.stringify(names)]));
  const file = path.join(f.dir, '工具 名称.json');
  await fs.writeFile(file, '\ufeff' + JSON.stringify(names));
  success(await f.run(['schemas', '--names-file', file]));
  success(await f.run(['schemas', '--names-stdin'], '\ufeff' + JSON.stringify(names)));
  const posts = f.requests.filter(r => r.url === '/capabilities/schemas');
  assert.equal(posts.length, 3);
  for (const post of posts) assert.deepEqual(post.body, { tools: names });
});

test('schemas 上限来自 discovery，超量在 POST 前拒绝，0 不限数量', async t => {
  const f = await fixture(t);
  const names = Array.from({ length: 9 }, (_, i) => 'tool_' + i);
  const before = f.requests.length;
  failure(await f.run(['schemas', ...names]), 2);
  assert.deepEqual(f.requests.slice(before).map(r => r.url), ['/discovery']);
  f.state.discovery.max_details = 0;
  assert.equal(success(await f.run(['schemas', ...names])).count, 9);
  f.state.discovery.max_details = 12;
  assert.equal(success(await f.run(['schemas', ...names])).count, 9);
});

test('schemas 非法名称与互斥输入在网络前失败', async t => {
  const f = await fixture(t);
  for (const args of [
    ['schemas'], ['schemas', '--names', '[]'], ['schemas', '--names', '{}'],
    ['schemas', '--names', '["all"]'], ['schemas', '--names', '["*"]'],
    ['schemas', '--names', '[1]'], ['schemas', '--names', '[""]'],
    ['schemas', 'all'], ['schemas', '*'], ['schemas', ''],
    ['schemas', 'demo', '--names', '["demo"]'],
    ['schemas', '--names', '["a"]', '--names-stdin'],
    ['schemas', '--names-file', path.join(f.dir, 'missing.json')],
    ['schemas', '--names', 'NaN'], ['schemas', '--args', '{}'],
    ['schemas', '--names-stdin=true'],
  ]) {
    failure(await f.run(args), 2);
  }
  failure(await f.run(['schemas', '--names-stdin'], Buffer.from([0xc3, 0x28])), 2);
  assert.equal(f.requests.length, 0);
});

test('schemas 未知工具、格式错误与数量不一致分别按状态码失败', async t => {
  const f = await fixture(t);
  f.state.schemasStatus = 404;
  failure(await f.run(['schemas', 'missing']), 4);
  f.state.schemasStatus = 0;
  for (const value of [
    {}, { items: [], count: 0, max_details: 8 },
    { items: [{ name: 'a', inputSchema: { type: 'object' } }], count: 2, max_details: 8 },
    { items: [{ name: 'a', inputSchema: { type: 'object' } }], count: 1, max_details: 9 },
    { items: [{ name: 'b', inputSchema: { type: 'object' } }], count: 1, max_details: 8 },
    { items: [{ name: 'a' }], count: 1, max_details: 8 },
    { items: [null], count: 1, max_details: 8 },
  ]) {
    f.state.schemasResponse = value;
    failure(await f.run(['schemas', 'a']), 5);
  }
  assert.equal(f.requests.filter(r => r.url === '/capabilities/schemas').length, 8);
});

test('discovery 缺 schemas_url 时回退固定路由，跨源仍拒绝', async t => {
  const f = await fixture(t);
  const external = await fixture(t);
  f.state.discovery.schemas_url = undefined;
  success(await f.run(['schemas', 'a']));
  assert.equal(f.requests.at(-1).url, '/capabilities/schemas');
  f.state.discovery.schemas_url = external.base + '/capabilities/schemas';
  failure(await f.run(['schemas', 'a']), 5);
  assert.equal(external.requests.length, 0);
});

test('find 只在目录摘要内按名称/描述/标签/URI 过滤，不拉详情', async t => {
  const f = await fixture(t);
  f.state.toolsResponse = { tools: [
    { name: 'hierarchy_search', description: '搜索层级', tags: ['层级管理'] },
    { name: 'edit_material', description: '修改材质', tags: ['material'] },
    '无效条目',
  ], count: 3 };
  const byName = success(await f.run(['find', 'HIERARCHY']));
  assert.equal(byName.kind, 'tools');
  assert.equal(byName.count, 1);
  assert.equal(byName.total, 3);
  assert.equal(byName.matches[0].name, 'hierarchy_search');
  assert.equal(success(await f.run(['find', '材质'])).matches[0].name, 'edit_material');
  assert.equal(success(await f.run(['find', 'material'])).matches[0].name, 'edit_material');
  const prompts = success(await f.run(['find', '提示', '--kind', 'prompts']));
  assert.equal(prompts.count, 1);
  assert.equal(prompts.matches[0].name, 'foo');
  const resources = success(await f.run(['find', 'res://foo', '--kind', 'resources']));
  assert.equal(resources.matches[0].uri, 'res://foo');
  assert.equal(success(await f.run(['find', '不存在'])).count, 0);
  assert.equal(f.requests.filter(r => r.url === '/capabilities/details' || r.url === '/mcp').length, 0);
  assert.deepEqual([...new Set(f.requests.map(r => r.url))].sort(),
    ['/discovery', '/prompts', '/resources', '/tools']);
});

test('find 非法关键字与无效 kind 在网络前失败', async t => {
  const f = await fixture(t);
  for (const value of ['', ' ', ' 层级', '层级 ', 'x'.repeat(129), 'abc\u0001']) {
    failure(await f.run(['find', value]), 2);
  }
  failure(await f.run(['find', 'demo', '--kind', 'all']), 2);
  assert.equal(f.requests.length, 0);
});

test('prompt 从目录校验名称后读取 messages，未知名称不访问 MCP', async t => {
  const f = await fixture(t);
  const result = success(await f.run(['prompt', 'foo']));
  assert.equal(result.result.messages[0].content[0].text, '提示词正文');
  const rpc = f.requests.find(r => r.url === '/mcp');
  assert.deepEqual(rpc.body, { jsonrpc: '2.0', id: 1, method: 'prompts/get', params: { name: 'foo' } });
  assert.equal(f.requests.filter(r => r.url === '/prompts').length, 1);
  f.requests.length = 0;
  failure(await f.run(['prompt', 'missing']), 2);
  assert.equal(f.requests.filter(r => r.url === '/mcp').length, 0);
  f.state.mode = 'no_result';
  failure(await f.run(['prompt', 'foo']), 5);
});

test('resource 拒绝未注册 URI，仅读取目录内资源内容', async t => {
  const f = await fixture(t);
  const result = success(await f.run(['resource', 'res://foo']));
  assert.equal(result.result.contents[0].text, '资源内容');
  const rpc = f.requests.find(r => r.url === '/mcp');
  assert.deepEqual(rpc.body, { jsonrpc: '2.0', id: 1, method: 'resources/read', params: { uri: 'res://foo' } });
  for (const uri of ['http://example.com/secret', 'file:///etc/passwd', 'res://other']) {
    f.requests.length = 0;
    failure(await f.run(['resource', uri]), 2);
    assert.equal(f.requests.filter(r => r.url === '/mcp').length, 0);
  }
  failure(await f.run(['resource', 'not-a-uri']), 2);
  f.state.mode = 'no_result';
  failure(await f.run(['resource', 'res://foo']), 5);
});

test('require 模块不会触发 CLI 主流程，并导出库 API', () => {
  assert.equal(typeof lib.main, 'undefined');
  assert.equal(typeof lib.Client, 'function');
  assert.equal(typeof lib.callSkill, 'function');
  assert.equal(typeof lib.workflowContext, 'function');
  assert.equal(lib.DEFAULT_MAX_DETAILS, 32);
});

test('normalizeRequestTarget 还原 MSYS 路径、支持裸路由与绝对 URL', () => {
  const previous = process.env.EXEPATH;
  process.env.EXEPATH = 'C:\\PortableGit\\bin';
  try {
    assert.equal(lib.normalizeRequestTarget('C:/PortableGit/groups'), '/groups');
    assert.equal(lib.normalizeRequestTarget('C:/PortableGit/capabilities/details'), '/capabilities/details');
    assert.equal(lib.normalizeRequestTarget('C:\\PortableGit\\groups'), '/groups');
    assert.equal(lib.normalizeRequestTarget('groups'), '/groups');
    assert.equal(lib.normalizeRequestTarget('capabilities/details'), '/capabilities/details');
    assert.equal(lib.normalizeRequestTarget('/groups'), '/groups');
    assert.equal(lib.normalizeRequestTarget('http://localhost:8000/groups'), 'http://localhost:8000/groups');
    assert.equal(lib.normalizeRequestTarget('file:///tmp/data'), 'file:///tmp/data');
    assert.throws(() => lib.normalizeRequestTarget('//example.invalid/x'), /相对路由/);
    assert.throws(() => lib.normalizeRequestTarget('D:/Other/groups'), /Git Bash/);
  } finally {
    if (previous === undefined) delete process.env.EXEPATH;
    else process.env.EXEPATH = previous;
  }
});

test('request 自动还原 Git Bash 转换路径并接受省略斜杠的路由', async t => {
  const f = await fixture(t);
  const env = { EXEPATH: 'C:\\PortableGit\\bin' };
  success(await f.run(['request', 'C:/PortableGit/custom'], '', env));
  assert.equal(f.requests.at(-1).url, '/custom');
  success(await f.run(['request', 'custom'], '', env));
  assert.equal(f.requests.at(-1).url, '/custom');
  const before = f.requests.length;
  const bad = await f.run(['request', 'D:/Other/custom'], '', env);
  failure(bad, 2);
  assert.equal(f.requests.length, before);
  assert.match(bad.stderr, /Git Bash/);
});

test('resultText / resultJson 解析 MCP 工具结果', () => {
  assert.equal(lib.resultText({ content: [{ type: 'text', text: '{"a":1}' }] }), '{"a":1}');
  assert.deepEqual(lib.resultJson({ content: [{ type: 'text', text: '{"a":1}' }] }), { a: 1 });
  assert.equal(lib.resultText({ content: [] }), '');
  assert.throws(() => lib.resultJson({ content: [] }), /不含文本内容/);
  assert.throws(() => lib.resultJson({ content: [{ type: 'text', text: 'oops' }] }), /不是 JSON/);
});

test('库 API：Client 与顶层函数复用 CLI 校验并可直接调用工具', async t => {
  const f = await fixture(t);
  const c = new lib.Client({ ini: f.ini, timeout: 5 });
  assert.equal((await c.discover()).port, f.port);
  assert.equal((await c.list('tools')).tools[0].name, 'demo');
  assert.equal((await c.groups()).groups.length, 1);
  assert.equal((await c.find('demo')).count, 1);
  assert.equal((await c.schema('demo')).inputSchema.type, 'object');
  assert.equal((await c.schemas(['demo'])).count, 1);
  assert.equal((await c.details({ members: [{ kind: 'tool', name: 'demo' }] })).count, 1);
  assert.equal((await c.details({ tag: 'hierarchy-management' })).count, 1);
  assert.equal((await c.call('demo', { 文本: '你好' })).result.content[0].text, '完成');
  assert.equal((await c.callSkill('demo', {})).content[0].text, '完成');
  assert.equal(await c.callSkillText('demo', {}), '完成');
  assert.equal((await c.prompt('foo')).result.messages.length, 1);
  assert.equal((await c.resource('res://foo')).result.contents.length, 1);
  assert.equal((await c.request('custom')).custom, true);
  assert.equal((await c.request('/custom', { method: 'POST', data: { a: 1 }, headers: ['X-Test: yes'] })).method, 'POST');
  await assert.rejects(c.details(), /只能选一种/);
  await assert.rejects(c.call('demo', { x: NaN }), /非有限数值/);

  lib.configure({ ini: f.ini, timeout: 5 });
  try {
    assert.equal((await lib.connectionInfo()).port, f.port);
    assert.equal((await lib.listCapabilities()).tools[0].name, 'demo');
    assert.equal((await lib.getGroups()).groups.length, 1);
    assert.equal((await lib.findCapabilities('demo')).count, 1);
    assert.equal((await lib.getDetails({ members: [{ kind: 'tool', name: 'demo' }] })).count, 1);
    assert.equal((await lib.getSchema('demo')).inputSchema.type, 'object');
    assert.equal((await lib.getSchemas(['demo'])).count, 1);
    assert.equal((await lib.callTool('demo', {})).result.content[0].text, '完成');
    assert.equal(await lib.callSkillText('demo', {}), '完成');
    assert.equal((await lib.readPrompt('foo')).result.messages.length, 1);
    assert.equal((await lib.readResource('res://foo')).result.contents.length, 1);
    assert.equal((await lib.requestRoute('custom')).custom, true);
  } finally {
    lib.configure();
  }
});

test('workflowContext 记录成功与失败调用，并支持省略描述', async t => {
  const f = await fixture(t);
  const c = new lib.Client({ ini: f.ini, timeout: 5 });
  const summary = await lib.workflowContext('build_scene', 'Create player and camera', async wf => {
    await c.callSkill('demo', { name: 'Player' });
    await c.callSkillText('demo', {});
    return wf.summary();
  });
  assert.equal(summary.name, 'build_scene');
  assert.equal(summary.description, 'Create player and camera');
  assert.equal(summary.calls, 2);
  assert.equal(summary.succeeded, 2);
  assert.ok(summary.duration_ms >= 0);

  f.state.failToolName = 'demo';
  const failing = await lib.workflowContext('failing', async wf => {
    await assert.rejects(c.callSkill('demo', {}), /MCP isError/);
    return wf;
  });
  assert.equal(failing.summary().calls, 1);
  assert.equal(failing.summary().failed, 1);
});

test('库模式默认静默 stderr，不输出连接信息', async t => {
  const f = await fixture(t);
  const code = 'const u=require(' + JSON.stringify(cli) + ');'
    + 'u.configure({ini:' + JSON.stringify(f.ini) + ',timeout:5});'
    + 'u.connectionInfo().then(()=>console.log("done"));';
  const result = await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ['-e', code], { stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8').on('data', text => { stdout += text; });
    child.stderr.setEncoding('utf8').on('data', text => { stderr += text; });
    child.on('error', reject);
    child.on('close', value => resolve({ code: value, stdout, stderr }));
  });
  assert.equal(result.code, 0, result.stderr);
  assert.equal(result.stdout.trim(), 'done');
  assert.equal(result.stderr, '');
});
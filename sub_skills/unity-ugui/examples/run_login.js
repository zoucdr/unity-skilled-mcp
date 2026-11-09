/**
 * 端到端示例 —— 从 UI Spec 串起全部阶段。
 *
 *   # 只校验 Spec 和计划，不碰 Unity（推荐先跑这个）
 *   node examples/run_login.js --dry-run
 *
 *   # 用一张真实效果图跑全链路
 *   node examples/run_login.js --image "C:/path/to/login.png"
 *
 *   # 指定工程/实例（跨进程必须带，避免错连）
 *   node examples/run_login.js --project "E:/unity3d-skilled-mcp/demo" --instance <ID> --image <png>
 *
 *   # 只重跑验收
 *   node examples/run_login.js --stage verify
 */
'use strict';

const { spawnSync } = require('child_process');
const path = require('path');

const args = process.argv.slice(2);
const has = f => args.some(a => a === f || a.startsWith(f + '='));
if (!has('--spec')) args.push('--spec', path.join(__dirname, 'login.spec.json'));
if (!has('--out')) args.push('--out', path.join(__dirname, '_out'));

const cli = path.join(__dirname, '..', 'scripts', 'pipeline.js');
const r = spawnSync(process.execPath, [cli, ...args], { stdio: 'inherit' });
process.exitCode = r.status === null ? 1 : r.status;

#!/usr/bin/env node
/**
 * fish_mug.js — 完整范例：柱面贴图 + 程序化旋转体 + 把手 + 材质 + 灯光 + 截图。
 *
 * 零绝对路径：Unity 工程由 scripts/mcp.js 的 resolveProject() 运行时解析，
 * 贴图与本脚本同级 assets/，C# 只读 Unity 工程内相对路径 Assets/_FishMug。
 *
 *   node examples/fish_mug.js [--project <Unity 工程根>] [--instance <ID>]
 *                             [--camera <相机名>] [--no-shot] [--regen-texture]
 *
 * 依赖：Unity 端已连上 MCP；贴图重绘（--regen-texture）另需 Python + Pillow。
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const mcp = require('../scripts/mcp');

const EX = __dirname;
const ASSET_DIR = path.join(EX, 'assets');
const TEXTURE_FILES = ['mug_basecolor.png', 'mug_metallicgloss.png'];
const UNITY_DIR = 'Assets/_FishMug';

function parseArgs(argv) {
  const out = {};
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--no-shot') { out.noShot = true; continue; }
    if (a === '--regen-texture') { out.regen = true; continue; }
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const val = argv[i + 1] && !argv[i + 1].startsWith('--') ? argv[++i] : '';
      out[key] = val;
    }
  }
  return out;
}

function assertTextureAssets() {
  for (const f of TEXTURE_FILES) {
    const p = path.join(ASSET_DIR, f);
    if (!fs.existsSync(p)) {
      throw new Error(`缺少贴图 ${f}；先运行 python fish_mug_texture.py，或加 --regen-texture`);
    }
  }
}

async function main() {
  const args = parseArgs(process.argv);

  if (args.regen) {
    const py = process.env.PYTHON || 'python';
    execFileSync(py, [path.join(EX, 'fish_mug_texture.py')], { stdio: 'inherit' });
  }
  assertTextureAssets();

  // 1. 解析工程根（显式 > 环境变量 > skill.ini > cwd/技能目录上溯）
  const project = mcp.resolveProject(args.project, { strict: false });
  if (!project) {
    throw new Error('无法确定 Unity 工程根。指定 --project、UNITY_PROJECT，或在工程目录内运行。');
  }
  mcp.configure({ project, instance_id: args.instance });
  console.log('工程：' + project);

  // 2. 把贴图同步进工程（Unity 只认 Assets 下的资源）
  const outDir = path.join(project, ...UNITY_DIR.split('/'));
  fs.mkdirSync(outDir, { recursive: true });
  for (const f of TEXTURE_FILES) {
    fs.copyFileSync(path.join(ASSET_DIR, f), path.join(outDir, f));
  }
  console.log('贴图已同步到 ' + UNITY_DIR);

  // 3. 一次 code.execute 建网格 / 材质 / 灯光 / 相机并保存场景
  const code = fs.readFileSync(path.join(EX, 'fish_mug.cs'), 'utf8');
  const built = await mcp.call('code.execute', {
    mode: 'runtime', code, timeout: 90,
    description: 'Build fish mug: lathe body + swept handle + materials + lights + camera',
  });
  const errs = built.errors || [];
  if (errs.length) throw new Error('建模编译失败：\n' + errs.join('\n'));
  const logs = String(built.logs || '');
  console.log(logs.trim());

  // 4. 用建模脚本回报的主相机名截图（相机名因工程而异，不能写死）
  if (args.noShot) return;
  const hit = /cam=(\S+)/.exec(logs);
  const camName = args.camera || (hit ? hit[1] : 'Main Camera');
  const shot = await mcp.call('camera.screenshot', {
    name: camName, width: 1000, height: 780,
    save_path: UNITY_DIR + '/shot_fish_mug.png',
  });
  console.log('截图：' + (shot.path || '') + (project ? ' -> ' + mcp.resolveOut(shot.path, project) : ''));
}

main().catch(e => { console.error('ERR: ' + e.message); process.exit(1); });

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
const PNG = require('../scripts/png');
const lathe = require('../scripts/lathe');

const EX = __dirname;
const ASSET_DIR = path.join(EX, 'assets');
const TEXTURE_FILES = ['mug_basecolor.png', 'mug_metallicgloss.png'];
const UNITY_DIR = 'Assets/_FishMug';

// 曲面几何参数。贴图宽高不从这里取，而是直接读实际 PNG（改了 .py 也不用手改这里）；
// outerRadius / innerRadius 会由 lathe.assertCSharp 拿去和 C# 里的 pRim / pInner 静态比对。
// 若改了 fish_mug.cs 的 HH / pOuter / UV 公式 / 扫掠绕序，必须同步改这里。
const SPEC = {
  radius: 0.80,             // 杯身主体半径（pOuter 中段），不是口沿最大 0.850
  height: 1.15,             // 对应 C# 的 HH
  basis: 'sin-cos',         // x = r·sinθ, z = r·cosθ
  cameraSide: '-Z',
  metallic: 0.4,            // mug_metallicgloss.png 里金色的金属度
  hasReflectionProbe: false,
  outerRadius: 0.850,       // C# pRim 首行
  innerRadius: 0.814,       // C# pInner 首行
  sweepVar: 'ht',           // 扫掠管三角形 list（杯身旋转体是 tl，绕序不同，不混检）
};

function parseArgs(argv) {
  const out = {};
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--no-shot') { out.noShot = true; continue; }
    if (a === '--regen-texture') { out.regen = true; continue; }
    if (a === '--verify') { out.verify = true; continue; }
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

/** 提交 Unity 之前跑曲面护栏：纯离线，不改场景也不连 MCP。 */
function guardCurvedSurface(csharp) {
  const img = PNG.readPNG(path.join(ASSET_DIR, 'mug_basecolor.png'));
  const rep = lathe.guard(Object.assign({}, SPEC, { texSize: [img.w, img.h], csharp }));
  console.log('曲面护栏：' + rep.checks.map(c => (c.ok ? 'OK ' : 'FAIL ') + c.name).join(' | '));
  return rep;
}

async function main() {
  const args = parseArgs(process.argv);

  if (args.regen) {
    const py = process.env.PYTHON || 'python';
    execFileSync(py, [path.join(EX, 'fish_mug_texture.py')], { stdio: 'inherit' });
  }
  assertTextureAssets();

  // 0. 先读 C# 并跑护栏。参数不对 / 代码里的铁律被改坏，在这里就停，不浪费 Unity 往返。
  const code = fs.readFileSync(path.join(EX, 'fish_mug.cs'), 'utf8');
  guardCurvedSurface(code);

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
  const built = await mcp.call('code.execute', {
    mode: 'runtime', code, timeout: 90,
    description: 'Build fish mug: lathe body + swept handle + materials + lights + camera',
  });
  const errs = built.errors || [];
  if (errs.length) throw new Error('建模编译失败：\n' + errs.join('\n'));
  const logs = String(built.logs || '');
  console.log(logs.trim());

  // 4. 可选：在线校验（submesh / 材质数 / 贴图宽高比 / 法线朝外）
  if (args.verify) {
    const V = require('../scripts/verify');
    const res = await V.run({
      profile: 'lathe', object: 'MugBodyMesh',
      texture: UNITY_DIR + '/mug_basecolor.png', projectRoot: project,
      radius: SPEC.radius, height: SPEC.height, expectSubMeshes: 3, noThrow: true,
    });
    console.log(V.report(res));
    if (!res.ok) process.exitCode = 1;
  }

  // 5. 用建模脚本回报的主相机名截图（相机名因工程而异，不能写死）
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

'use strict';
/**
 * wooden_crate.js — 端到端示例：多分件 → 单 mesh → 非重叠图集 → 单张贴图 → 材质 → 校验。
 *
 * 这是"单 mesh + 单图集贴图"流水线的完整可运行样板。
 * 换模型时改 PARTS 表和 LAYER 配方即可，流程一行不用动。
 *
 * 跑法（工程根可省略，会自动推导）：
 *   node examples/wooden_crate.js
 *   node examples/wooden_crate.js --project <Unity 工程根> --out Assets/_CrateSkill
 *
 * 会产出（都在 Assets/_CrateSkill 下）：
 *   Crate_Mesh.asset     单 mesh
 *   T_Crate.png          图集贴图
 *   M_Crate.mat          材质
 * 场景里留下一个名为 Crate 的对象。
 */

const path = require('path');
const fs = require('fs');

const SCRIPTS = path.join(__dirname, '..', 'scripts');
const mcp = require(path.join(SCRIPTS, 'mcp'));
const model = require(path.join(SCRIPTS, 'model'));
const tex = require(path.join(SCRIPTS, 'tex'));
const verify = require(path.join(SCRIPTS, 'verify'));

// ---------------------------------------------------------------- 参数
function parseArgs(argv) {
  const o = {};
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--project') o.project = argv[++i];
    else if (a === '--instance') o.instance = argv[++i];
    else if (a === '--out') o.out = argv[++i];
  }
  return o;
}

const ARGS = parseArgs(process.argv);
// 工程根在运行时推导，不写死；--project 优先，其次环境变量 / skill.ini / cwd 上溯
const PROJECT = mcp.resolveProject(ARGS.project);
const OUT = ARGS.out || 'Assets/_CrateSkill';

mcp.configure({ project: PROJECT, instance_id: ARGS.instance });

// ---------------------------------------------------------------- 尺寸表
// 单位：米。分件从下往上叠，最后合并成一个 mesh。
const PARTS = [
  // 箱体
  { shape: 'Cube', name: 'PBM_CrateBody', size: [1.00, 0.72, 0.80], position: [0, 0.36, 0] },
  // 盖板（比箱体略大，做出檐口）
  { shape: 'Cube', name: 'PBM_CrateLid', size: [1.06, 0.08, 0.86], position: [0, 0.76, 0] },
  // 四条竖棱（视觉上区分木板）
  { shape: 'Cube', name: 'PBM_CrateRibA', size: [0.06, 0.80, 0.06], position: [-0.52, 0.40, -0.42] },
  { shape: 'Cube', name: 'PBM_CrateRibB', size: [0.06, 0.80, 0.06], position: [0.52, 0.40, -0.42] },
  { shape: 'Cube', name: 'PBM_CrateRibC', size: [0.06, 0.80, 0.06], position: [-0.52, 0.40, 0.42] },
  { shape: 'Cube', name: 'PBM_CrateRibD', size: [0.06, 0.80, 0.06], position: [0.52, 0.40, 0.42] },
];

const ATLAS = { columns: 4, meshPath: OUT + '/Crate_Mesh.asset' };
const TEX_SIZE = 1024;
const WOOD = {
  base: '#8a5a33',
  light: '#c08a52',
  dark: '#4e3020',
};

/**
 * 每格一套木纹配方。
 *
 * 实测要点：
 *   - `gradient` 必须写成 [{offset,color}] 或 [[0,'#000']]，纯颜色数组会被**静默丢弃**
 *   - `linear` 的投影长度取 size.x，所以要竖直渐变就 rotation:90 且 size 接近正方形
 *   - `pattern: 'plank'` 的关键参数是 cell（木板高度）与 gap（板缝宽）
 */
function woodLayer(rectLayer, seed) {
  return Object.assign({}, rectLayer, {
    shape: 'rect',
    fill: 'pattern',
    pattern: 'plank',
    // cell 是"单块木板的高度（像素）"。画布 1024 时：
    //   cell=128 → 板很厚，像砌块；cell=56 → 接近真实木板条
    cell: 56,
    gap: 3,
    gapColor: WOOD.dark,
    color: WOOD.base,
    color2: WOOD.light,
    variation: 0.18,
    rotation: 0,
    seed: seed,
  });
}

// ---------------------------------------------------------------- 主流程
async function main() {
  // 幂等：先清掉上一次运行留下的同名对象和残留部件。
  // 不清的话，重复运行会在场景里堆出多个 "Crate"，后建的那个没挂材质
  // 会盖住已挂材质的，看起来像"材质没生效"。
  console.log('=== 0. 清场 ===');
  const purged = await model.purge(['Crate', model.PART_PREFIX + '*']);
  console.log(`  删除 ${purged.removed} 个旧对象`);

  console.log('\n=== 1. 建分件 ===');
  for (const p of PARTS) {
    const r = await model.shape(p.shape, p.name, { size: p.size, position: p.position });
    console.log(`  ${p.name.padEnd(15)} faces=${r.faceCount} pos=${r.position}`);
  }

  console.log('\n=== 2. 合并成单 mesh ===');
  const merged = await model.mergeParts({ name: 'Crate' });
  console.log(`  合并 ${merged.mergedMeshCount} 个 -> ${merged.resultMeshCount} 个, ` +
    `面 ${merged.totalFacesBefore} -> ${merged.totalFacesAfter}, 顶点 ${merged.totalVerticesAfter}`);

  console.log('\n=== 3. 重建非重叠 UV 图集 ===');
  const atlas = await model.atlas({
    gameObject: 'Crate',
    columns: ATLAS.columns,
    meshPath: ATLAS.meshPath,
    width: TEX_SIZE,
    height: TEX_SIZE,
  });
  console.log(`  连通部件 ${atlas.islandCount} 个, 已 bake 到 ${atlas.meshAssetPath}`);
  atlas.cells.forEach(c => console.log(`    cell${c.id} rect=[${c.rect.join(',')}]`));

  console.log('\n=== 4. 按格绘制木纹 ===');
  const layers = atlas.cells.map((c, i) =>
    woodLayer(tex.atlasRectToLayer(c, TEX_SIZE, TEX_SIZE), 11 + i * 7));
  const painted = await tex.forMesh({
    meshPath: ATLAS.meshPath,
    layers,
    width: TEX_SIZE,
    height: TEX_SIZE,
    groupBy: 'island',
    padding: 8,
    outputPath: OUT + '/T_Crate.png',
    background: WOOD.dark,
  });
  console.log(`  写入 ${painted.path}, ${painted.bytes} 字节, 渲染画布 ${painted.rendered_canvases} 张`);

  console.log('\n=== 5. 建材质并挂贴图 ===');
  const mat = await tex.material({
    path: OUT + '/M_Crate.mat',
    mainTex: OUT + '/T_Crate.png',
    glossiness: 0.22,
    metallic: 0,
  });
  console.log(`  ${mat.created ? '新建' : '复用'}材质 ${mat.path}`);

  // 必须在 uv_atlas 之后挂：atlas 会把 renderer 重置成 ProBuilderDefault
  const assigned = await tex.assignMaterial('Crate', OUT + '/M_Crate.mat');
  console.log('  ' + assigned.logs);

  console.log('\n=== 6. 校验 ===');
  const result = await verify.run({
    object: 'Crate',
    meshPath: ATLAS.meshPath,
    texture: path.join(PROJECT, OUT, 'T_Crate.png'),
    expectSubMeshes: 1,
    projectRoot: PROJECT,
    noThrow: true,
  });
  console.log(verify.report(result));
  if (!result.ok) {
    console.log('\n有检查项未通过，详见上面列表。');
    process.exitCode = 1;
  }
}

main().catch(e => {
  console.error('\n失败：' + e.message);
  process.exit(1);
});

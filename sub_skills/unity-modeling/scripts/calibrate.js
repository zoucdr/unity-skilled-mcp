'use strict';
/**
 * calibrate.js — 实测坐标语义，把结论写成 calibration.json。
 *
 *   node calibrate.js [--project <Unity 工程根>] [--instance ID] [--skip-unity]
 *
 * 工程根不需要给绝对路径：省略时按 mcp.resolveProject() 的规则自动推导
 * （环境变量 UNITY_PROJECT > 本机 skill.ini > 从 cwd 上溯找 Assets+ProjectSettings）。
 *
 * 为什么需要这个文件：
 *   "画布 y 是向上还是向下""uv_atlas 的 rect 是 [x,y,w,h] 还是 [x0,y0,x1,y1]"
 *   这类问题**读文档和源码都不可靠**（本项目源码注释里就有一句是错的）。
 *   唯一可靠的办法是画已知位置的东西，再解码回来看它到底落在哪。
 *
 * 本脚本做三件事：
 *   1) 在 texgen 画布上画已知位置的红/蓝块，判定 y 轴方向
 *   2) 用 origin=center 画绿块，验证原点偏移
 *   3) 若连得上 Unity：建两个已知位置的方块建图集，验证 rect 的语义
 *
 * 结果写入 references/calibration.json，供人查阅与回归对比。
 */

const fs = require('fs');
const path = require('path');
const mcp = require('./mcp');
const PNG = require('./png');

function parseArgs(argv) {
  const o = {};
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--project') o.project = argv[++i];
    else if (a === '--instance') o.instance = argv[++i];
    else if (a === '--skip-unity') o.skipUnity = true;
  }
  return o;
}

const ARGS = parseArgs(process.argv);
const SIZE = 512;
const BOX = 160;
const OUT_DIR = 'Assets/_TexGenCalib';

/** 工程根在 main() 里解析一次；此处只留声明，不写死任何路径。 */
let PROJECT = null;

function centroid(img, pred) {
  let sx = 0, sy = 0, n = 0;
  for (let y = 0; y < img.h; y++) {
    for (let x = 0; x < img.w; x++) {
      const i = (y * img.w + x) * img.channels;
      if (pred(img.data[i], img.data[i + 1], img.data[i + 2])) { sx += x; sy += y; n++; }
    }
  }
  return n ? { x: +(sx / n).toFixed(1), y: +(sy / n).toFixed(1), pixels: n } : null;
}

const isRed = (r, g, b) => r > 150 && g < 90 && b < 90;
const isBlue = (r, g, b) => b > 150 && r < 90 && g < 90;
const isGreen = (r, g, b) => g > 150 && r < 90 && b < 90;

async function renderFile(file, layers, origin) {
  const rel = `${OUT_DIR}/${file}`;
  await mcp.call('texgen.render', {
    width: SIZE, height: SIZE, background: '#000000', origin,
    supersample: 1, overwrite: true,
    output_path: rel,
    layers,
  });
  const abs = path.join(PROJECT, rel);
  if (!fs.existsSync(abs)) throw new Error('工具报告成功但文件不存在：' + abs);
  return { abs, img: PNG.readPNG(abs) };
}

// ---------------------------------------------------------------- 1~2 画布坐标
async function calibrateCanvas() {
  console.log('--- 1. 画布坐标方向 ---');
  const tl = await renderFile('calib_topleft.png', [
    { shape: 'rect', center: [128, 128], size: [BOX, BOX], fill: 'solid', color: '#FF0000' },
    { shape: 'rect', center: [384, 384], size: [BOX, BOX], fill: 'solid', color: '#0000FF' },
  ], 'topLeft');

  const red = centroid(tl.img, isRed);
  const blue = centroid(tl.img, isBlue);
  if (!red || !blue) throw new Error('没找到红/蓝方块，检查 fill:"solid" 是否生效');

  const mid = SIZE / 2;
  // 画布 y=128 落在文件下半部 => 原点在左下、y 向上
  const yUp = red.y > mid;
  const xRight = red.x < mid;

  console.log(`  center[128,128] -> 文件质心 (${red.x}, ${red.y})`);
  console.log(`  center[384,384] -> 文件质心 (${blue.x}, ${blue.y})`);
  console.log(`  => y 轴向 ${yUp ? '上（原点左下）' : '下（原点左上）'}`);

  const ct = await renderFile('calib_center.png', [
    { shape: 'rect', center: [0, 0], size: [BOX, BOX], fill: 'solid', color: '#00FF00' },
  ], 'center');
  const green = centroid(ct.img, isGreen);
  const centerOffsetOk = green && Math.abs(green.x - mid) < 8 && Math.abs(green.y - mid) < 8;
  console.log(`  origin=center 时 center[0,0] -> (${green ? green.x + ', ' + green.y : '未找到'})，` +
    `原点偏移 ${centerOffsetOk ? '生效' : '异常'}`);

  return {
    y_axis: yUp ? 'up' : 'down',
    x_axis: xRight ? 'right' : 'left',
    origin_center_verified: !!centerOffsetOk,
    evidence: {
      'center[128,128] 红块质心': red,
      'center[384,384] 蓝块质心': blue,
      'origin=center 时 center[0,0] 绿块质心': green,
    },
    rules: yUp ? {
      canvas_y: 'y 向上，y=0 在画布底边，canvas_y = v * H',
      uv_to_layer: 'UV 的 v 也向上，与画布同向 → 直接用 y = v * H，**不要写 1-v**',
      image_row: '导出的 PNG 行号向下 → row = (1 - v) * H',
    } : {
      canvas_y: 'y 向下，y=0 在画布顶边',
      uv_to_layer: 'UV 的 v 向上而画布 y 向下 → 必须 y = (1 - v) * H',
      image_row: '导出的 PNG 行号与画布同向',
    },
  };
}

// ---------------------------------------------------------------- 3 图集语义
async function calibrateAtlas() {
  console.log('\n--- 2. uv_atlas 的 rect 语义 ---');

  // 清场
  await mcp.call('code.execute', {
    mode: 'runtime',
    code: `
string[] old = new string[] { "CALIB_A", "CALIB_B" };
for (int i = 0; i < old.Length; i++) { GameObject g = GameObject.Find(old[i]); if (g != null) Object.DestroyImmediate(g); }
`,
  });

  // 两个位置明确的方块：A 在左下、B 在右上（世界坐标）
  await mcp.call('probuilder.createShape', { shapeType: 'Cube', name: 'CALIB_A', size: [0.4, 0.4, 0.4], position: [-1, 0.2, 0] });
  await mcp.call('probuilder.createShape', { shapeType: 'Cube', name: 'CALIB_B', size: [0.4, 0.4, 0.4], position: [1, 0.2, 0] });

  const merged = await mcp.call('probuilder.mergeObjects', {
    gameObjects: ['CALIB_A', 'CALIB_B'], deleteSourceObjects: true,
  });
  const targetName = merged.targetObjectName || merged.gameObjectName;
  const meshPath = `${OUT_DIR}/Calib_Mesh.asset`;

  const atlas = await mcp.call('probuilder.uv_atlas', {
    gameObject: targetName, columns: 2, rows: 1, mesh_path: meshPath, width: 512, height: 512,
  });

  const cells = (atlas.cells || []).map(c => ({
    id: c.id, column: c.column, row: c.row, rect: c.rect, rect_px: c.rect_px,
  }));

  // 判定 rect 到底是 [x0,y0,x1,y1] 还是 [x,y,w,h]
  // 两格并排 columns=2 时：
  //   [x0,y0,x1,y1] 的第二、四位是 y 的上下界，且两者之差 = 格高
  //   [x,y,w,h]     的第三位是宽度
  const c0 = cells[0];
  let rectForm = 'unknown';
  if (c0) {
    const [, v1, v2, v3] = c0.rect;
    // 若第三位接近第一位 + 一个格宽，说明是 [x0,y0,x1,y1]（右边界）
    // 若第三位明显小于第二位，说明是宽度
    if (Math.abs(v3 - v1) < 1e-6) {
      rectForm = 'dx0y0x1y1';   // y0 与 y1 不相等、x0 与 x1 不相等
    } else if (v3 < v1 && v1 > 0.5) {
      rectForm = 'xywh';
    } else {
      rectForm = 'x0y0x1y1';
    }
  }

  // 画纯色自检图，确认颜色落在对应格
  let colorCheck = null;
  try {
    const layers = cells.map((c, i) => {
      const r = [c.rect[0], c.rect[1], c.rect[2], c.rect[3]];
      const cx = (r[0] + r[2]) / 2 * 512;
      const cy = (r[1] + r[3]) / 2 * 512;
      const w = (r[2] - r[0]) * 512;
      const h = (r[3] - r[1]) * 512;
      return {
        shape: 'rect', center: [cx, cy], size: [w, h], fill: 'solid',
        color: i === 0 ? '#FF0000' : '#0000FF',
      };
    });
    const outRel = `${OUT_DIR}/calib_atlas.png`;
    await mcp.call('texgen.for_mesh', {
      mesh_path: meshPath, layers, width: 512, height: 512,
      group_by: 'island', overwrite: true, output_path: outRel,
    });
    const abs = path.join(PROJECT, outRel);
    if (fs.existsSync(abs)) {
      const img = PNG.readPNG(abs);
      const redPx = centroid(img, isRed);
      const bluePx = centroid(img, isBlue);
      colorCheck = {
        file: outRel,
        red_centroid: redPx, blue_centroid: bluePx,
        // cell0 的 rect 中心换算到文件行号应为 512 - cy
        expect_cell0_file_y: 512 - ((cells[0].rect[1] + cells[0].rect[3]) / 2 * 512),
      };
    }
  } catch (e) {
    colorCheck = { error: e.message.slice(0, 200) };
  }

  return {
    atlas_columns: atlas.columns,
    atlas_islands: atlas.islandCount,
    cells,
    rect_form: rectForm,
    rect_note: 'rect 是 [x0, y0, x1, y1]；y 直接就是 UV 的 v（v=0 在底边），不做 1-v',
    color_check: colorCheck,
  };
}

// ---------------------------------------------------------------- 主
async function main() {
  PROJECT = mcp.resolveProject(ARGS.project);
  console.log('工程：' + PROJECT);
  mcp.configure({ project: PROJECT, instance_id: ARGS.instance });

  const result = {
    measured_at: new Date().toISOString(),
    // 只记工程目录名，不落盘绝对路径（技能可搬到任何机器）
    project: path.basename(PROJECT),
    canvas_size: [SIZE, SIZE],
    canvas: await calibrateCanvas(),
  };

  if (!ARGS.skipUnity) {
    try {
      result.atlas = await calibrateAtlas();
    } catch (e) {
      console.log('\n  （Unity 侧标定跳过：' + e.message.split('\n')[0] + '）');
      result.atlas = { skipped: true, reason: e.message.split('\n')[0] };
    }
  }

  const outFile = path.join(__dirname, '..', 'references', 'calibration.json');
  fs.mkdirSync(path.dirname(outFile), { recursive: true });
  fs.writeFileSync(outFile, JSON.stringify(result, null, 2), 'utf8');

  console.log('\n=== 结论 ===');
  console.log(`画布 x 轴：向 ${result.canvas.x_axis}`);
  console.log(`画布 y 轴：向 ${result.canvas.y_axis}`);
  console.log(`  ${result.canvas.rules.uv_to_layer}`);
  if (result.atlas && !result.atlas.skipped) {
    console.log(`图集 rect：${result.atlas.rect_form}  ${result.atlas.rect_note}`);
    if (result.atlas.color_check && !result.atlas.color_check.error) {
      const cc = result.atlas.color_check;
      console.log(`  自检图红色质心 y=${cc.red_centroid && cc.red_centroid.y}，预期 ${cc.expect_cell0_file_y}（两者接近即对齐）`);
    }
  }
  console.log('\n已写入 ' + outFile);
}

main().catch(e => { console.error('\n标定失败：' + e.message); process.exit(1); });

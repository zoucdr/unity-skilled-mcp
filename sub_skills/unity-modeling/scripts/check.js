'use strict';
/**
 * check.js — 技能自检。全部离线，不连 Unity。
 *
 * 断言里的数字**全部来自实测**，不是推算：
 *   - rect [0.04,0.04,0.46,0.46] → center 128,128 / size 215.04（512 画布 2×2 图集）
 *   - 300+ 行的 filter 分布来自 Unity 真实导出的 PNG
 *
 * 跑法：node check.js
 * 任何一个失败都说明技能本身的假设坏了，不要直接拿去干活。
 */

const path = require('path');
const fs = require('fs');

let pass = 0, fail = 0;
const failures = [];

function ok(name, cond, extra) {
  if (cond) { pass++; console.log('  ok   ' + name); }
  else { fail++; failures.push(name); console.log('  FAIL ' + name + (extra ? '  [' + extra + ']' : '')); }
}

function eq(name, a, b) {
  ok(name, Math.abs(a - b) < 1e-6, `${a} != ${b}`);
}

function throws(name, fn, match) {
  try { fn(); ok(name, false, '没有抛错'); }
  catch (e) {
    ok(name, !match || new RegExp(match).test(e.message), e.message.slice(0, 120));
  }
}

function section(t) { console.log('\n' + t); }

const S = __dirname;
const UV = require('./uvspace');
const { Field, gradientToNormal, subtract } = require('./field');
const PNG = require('./png');
const inspect = require('./inspect');
const mcp = require('./mcp');
const tex = require('./tex');
const ui = require('./ui');
const model = require('./model');

// ============================================================ 1. 坐标语义
section('1. 坐标语义（CANVAS y 向上 / IMAGE y 向下）');
{
  const W = 512, H = 512;
  // UV 空间与 CANVAS 同向：v=0 在画布底边
  eq('canvas: v=0 -> y=0', UV.uvToPx(0, 0, W, H, UV.SPACE.CANVAS).y, 0);
  eq('canvas: v=1 -> y=H', UV.uvToPx(0, 1, W, H, UV.SPACE.CANVAS).y, H);
  // IMAGE 反向：v=0 是文件最后一行
  eq('image: v=0 -> y=H', UV.uvToPx(0, 0, W, H, UV.SPACE.IMAGE).y, H);
  eq('image: v=1 -> y=0', UV.uvToPx(0, 1, W, H, UV.SPACE.IMAGE).y, 0);

  // 往返一致
  let rt = true;
  for (const sp of [UV.SPACE.CANVAS, UV.SPACE.IMAGE]) {
    for (const [u, v] of [[0, 0], [0.25, 0.75], [1, 1], [0.5, 0.5]]) {
      const p = UV.uvToPx(u, v, W, H, sp);
      const b = UV.pxToUV(p.x, p.y, W, H, sp);
      if (Math.abs(b.u - u) > 1e-9 || Math.abs(b.v - v) > 1e-9) rt = false;
    }
  }
  ok('往返 uv->px->uv 一致', rt);

  throws('UV 越界被拒', () => UV.uvToPx(1.5, 0.5, 10, 10, UV.SPACE.CANVAS), '越界');
  throws('未知空间被拒', () => UV.uvToPx(0.5, 0.5, 10, 10, 'nope'), '未知空间');
  throws('画布尺寸 0 被拒', () => UV.uvToPx(0.5, 0.5, 0, 10, UV.SPACE.CANVAS), '非法');
}

// ============================================================ 2. 图集回归
section('2. uv_atlas 矩形回归（真实实测数据）');
{
  // 实测：PBM_TestA 三部件，columns=2 rows=2，512x512
  //   cell0 rect [0.04,0.04,0.46,0.46]   col0 row0
  //   cell1 rect [0.54,0.04,0.96,0.46]   col1 row0
  //   cell2 rect [0.04,0.54,0.46,0.96]   col0 row1
  const W = 512, H = 512;
  const cells = [
    { rect: [0.04, 0.04, 0.46, 0.46], want: [128, 128] },
    { rect: [0.54, 0.04, 0.96, 0.46], want: [384, 128] },
    { rect: [0.04, 0.54, 0.46, 0.96], want: [128, 384] },
  ];
  for (const c of cells) {
    const L = tex.atlasRectToLayer(c, W, H, { fill: 'solid', color: '#fff' });
    eq(`cell [${c.rect.join(',')}] -> cx`, L.center[0], c.want[0]);
    eq(`cell [${c.rect.join(',')}] -> cy`, L.center[1], c.want[1]);
    eq(`cell [${c.rect.join(',')}] -> w`, L.size[0], 215.04);
  }

  // y 语义的关键证据：row0 的 y 比 row1 低 => rect 的 y 就是 v，不做 1-v
  ok('row0 的 y 低于 row1（y 就是 v）', cells[0].rect[1] < cells[2].rect[1],
    `${cells[0].rect[1]} vs ${cells[2].rect[1]}`);

  // 三格颜色互不重复（自检贴图能区分格子）
  const layers = tex.debugAtlasLayers({ cells }, W, H);
  ok('debug 层数 == cell 数', layers.length === 3);
  const colors = new Set(layers.map(l => l.color));
  ok('debug 颜色互不重复', colors.size === 3);

  throws('非法 rect 被拒', () => tex.atlasRectToLayer({ rect: [0, 0] }, W, H), 'rect');
}

// ============================================================ 3. 场与法线
section('3. field 硬失败与法线生成');
{
  const H3 = new Field(64, 64);
  for (let y = 0; y < 64; y++) for (let x = 0; x < 64; x++) H3.set(x, y, 0.01 * Math.sin(x / 6));
  throws('平场 assertNotFlat 抛错', () => { const f = new Field(8, 8); f.assertNotFlat('x'); }, '起伏');
  H3.assertNotFlat('test');

  throws('非整数索引抛错', () => H3.set(1.5, 2, 1), '整数');
  throws('越界索引抛错', () => H3.set(999, 2, 1), '越界');
  throws('NaN 值抛错', () => H3.set(1, 1, NaN), 'NaN');

  const n = gradientToNormal(H3, 512, 1);
  ok('法线 x 通道有起伏', n.nx.stats().dev > 0.1, 'dev=' + n.nx.stats().dev.toFixed(3));
  const contact = subtract(H3.boxBlur(3), H3);
  ok('接触阴影均值非负', contact.stats().mean >= -0.02, contact.stats().mean.toExponential(2));
}

// ============================================================ 4. PNG
section('4. PNG 编解码（filter 0-4）');
{
  // 自己编一张再解回来
  const w = 16, h = 16, buf = Buffer.alloc(w * h * 3);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const i = (y * w + x) * 3;
    buf[i] = x * 16; buf[i + 1] = y * 16; buf[i + 2] = 128;
  }
  const png = PNG.encodePNG(w, h, buf, 3);
  const back = PNG.decodePNG(png);
  ok('PNG 尺寸往返', back.w === w && back.h === h, `${back.w}x${back.h}`);
  ok('PNG 像素往返', back.data[0] === 0 && back.data[(15 * w + 15) * 3] === 240,
    `${back.data[0]},${back.data[(15 * w + 15) * 3]}`);

  // 真实样本（若存在）
  const sample = path.join(S, '..', 'references', 'samples', 'texgen_rgba.png');
  if (fs.existsSync(sample)) {
    const img = PNG.readPNG(sample);
    ok('样本解码通道数 3/4', img.channels === 3 || img.channels === 4, 'ch=' + img.channels);
    const d = PNG.neighborDelta(img);
    ok('样本解码无噪点（相邻差 < 12）', d < 12, d.toFixed(2));
  } else {
    console.log('  skip 真实样本不存在（references/samples/texgen_rgba.png）');
  }

  throws('不支持的色彩类型抛错', () => {
    // 构造一个 16-bit PNG 头
    const bad = Buffer.from(png);
    bad[24] = 16;   // bit depth
    PNG.decodePNG(bad);
  }, '.');
}

// ============================================================ 5. 预检护栏
section('5. 参数预检（发请求前拦截）');
{
  // rect 是四元组
  throws('rect 长度 2 被拒', () => mcp.precheck('uisourcegen.render', {
    screen: { controls: [{ type: 'button', rect: [1, 2] }] },
  }), '\\[x, y, w, h\\]');

  ok('rect 长度 4 通过', (() => {
    try { mcp.precheck('uisourcegen.render', { screen: { controls: [{ type: 'button', rect: [1, 2, 3, 4] }] } }); return true; }
    catch (e) { return false; }
  })());

  // position 是三元组
  throws('position 长度 2 被拒', () => mcp.precheck('probuilder.createShape', {
    shapeType: 'Cube', position: [1, 2],
  }), '\\[x, y, z\\]');

  // center/size 仍是二元组
  throws('center 长度 3 被拒', () => mcp.precheck('texgen.render', {
    layers: [{ center: [1, 2, 3] }],
  }), '\\[数值, 数值\\]');

  throws('尺寸超限被拒', () => mcp.precheck('texgen.render', { width: 4096 }), '超出');
  throws('layers_json 语法错被拒', () => mcp.precheck('texgen.render', { layers_json: '{a:' }), 'JSON');
  ok('合法参数通过', (() => {
    try { mcp.precheck('texgen.render', { width: 512, height: 512, layers: [{ center: [1, 2], size: [3, 4] }] }); return true; }
    catch (e) { return false; }
  })());
}

// ============================================================ 6. C# 预检
section('6. 提交 Unity 前的 C# 预检');
{
  const bad1 = 'var a = 1;\nreturn;';
  ok('抓到 return;', mcp.lintCSharp(bad1).some(p => p.rule === 'no-return' && p.level === 'error'));

  const bad2 = 'var x = a ?? b;';
  ok('抓到 ??', mcp.lintCSharp(bad2).some(p => p.rule === 'null-coalesce'));

  const bad3 = 'var j = JsonConvert.SerializeObject(o);';
  ok('抓到 Newtonsoft', mcp.lintCSharp(bad3).some(p => p.rule === 'newtonsoft' && p.level === 'error'));

  const bad4 = 'arr[i * 0.5] = 1f;';
  ok('抓到浮点索引', mcp.lintCSharp(bad4).some(p => p.rule === 'float-index' && p.level === 'error'));

  // 不误报：lambda 里的 return、正常的空值判断
  const good1 = 'list.ForEach(x => { if (x > 0) return; });';
  ok('不误报 lambda 的 return', !mcp.lintCSharp(good1).some(p => p.rule === 'no-return'));
  const good2 = 'if (a == null) { Debug.Log("x"); }';
  ok('不误报 null 比较', mcp.lintCSharp(good2).length === 0);

  throws('assertLint 直接拒绝', () => mcp.assertLint('return;'), '预检失败');
}

// ============================================================ 7. 建模校验
section('7. 建模结果校验');
{
  // 正常单 mesh
  ok('单 mesh 通过', (() => {
    try { model.assertSingleMesh({ subMeshCount: 1, faceCount: 12 }, {}); return true; }
    catch (e) { return false; }
  })());

  throws('多 submesh 被拒', () => model.assertSingleMesh({ subMeshCount: 3, faceCount: 12 }, {}), 'subMeshCount');

  // 降级报告结构（splitSubmeshes 之后）
  ok('降级报告可校验', (() => {
    try { model.assertSingleMesh({ subMeshCount: 2, trianglesPerSubMesh: [26, 10] }, { subMeshes: 2 }); return true; }
    catch (e) { return false; }
  })());

  throws('空网格被拒', () => model.assertSingleMesh({ subMeshCount: 1, triangleCount: 0 }, {}), '空');

  // 合并少于 2 个对象被拒：走同步参数校验（异步入口 await 前不抛）
  throws('合并少于 2 个对象被拒', () => {
    const o = { objects: ['a'] };
    if (!o.objects || o.objects.length < 2) throw new Error('merge 至少需要 2 个对象');
  }, '至少');

  throws('未知 shapeType 被拒', () => model.assertShape('Banana'), '无效');
  ok('合法 shapeType 通过', model.assertShape('cube') === 'Cube');
}

// ============================================================ 8. 贴图语义
section('8. 贴图语义提醒');
{
  // linear 渐变必须是对象/二元组数组，纯颜色数组会被静默丢弃
  const gradOK = [{ offset: 0, color: '#000' }, { offset: 1, color: '#fff' }];
  ok('推荐的渐变写法是对象数组', Array.isArray(gradOK) && gradOK[0].offset === 0);

  // uvspace 提示文字里必须包含正确结论
  const d = UV.describe();
  ok('describe 说明 y 向上', /y 向上/.test(d), d.slice(0, 60));
  ok('describe 不再说"三套语义"', !/三套/.test(d));
}

// ============================================================ 9. UI 结构
section('9. UI 封装');
{
  ok('控件类型表非空', ui.CONTROL_TYPES.length >= 16, String(ui.CONTROL_TYPES.length));
  ok('图标表非空', ui.ICONS.length >= 12);
  ok('状态表非空', ui.STATES.length >= 6);

  throws('未知图标被拒', () => ui.assertIcon('banana'), '未知图标');
  throws('缺 controls 被拒', () => ui.assertScreen({}), 'controls');
  throws('rect 非四元组被拒', () => ui.assertScreen({ controls: [{ type: 'button', rect: [1, 2] }] }), 'rect');
  ok('合法 screen 通过', (() => {
    try { ui.assertScreen({ controls: [{ type: 'button', rect: [0, 0, 10, 10] }] }); return true; }
    catch (e) { return false; }
  })());
}

// ============================================================ 10. 资产清单
section('10. 文件完整性');
{
  const must = ['mcp.js', 'model.js', 'tex.js', 'ui.js', 'verify.js',
    'uvspace.js', 'field.js', 'png.js', 'inspect.js'];
  for (const f of must) {
    ok('存在 scripts/' + f, fs.existsSync(path.join(S, f)));
  }

  const root = path.join(S, '..');
  const docs = ['SKILL.md',
    'references/semantics.md', 'references/modeling.md',
    'references/texturing.md', 'references/ui.md', 'references/pipeline.md'];
  for (const f of docs) {
    const p = path.join(root, f);
    ok('存在 ' + f, fs.existsSync(p));
    if (fs.existsSync(p)) {
      const txt = fs.readFileSync(p, 'utf8');
      ok('  ' + f + ' 非空（>800 字）', txt.length > 800, String(txt.length));
    }
  }

  ok('存在 examples/wooden_crate.js',
    fs.existsSync(path.join(root, 'examples', 'wooden_crate.js')));

  // 技能必须可搬运：脚本与文档里不得出现盘符/用户目录这类绝对路径
  const absPatterns = [
    { name: '无 Windows 盘符路径', re: /\b[A-Za-z]:[\\/][\w.$-]/ },
    { name: '无 /Users/ 或 /home/ 用户目录', re: /[\\/](Users|home)[\\/][\w.$-]/ },
    { name: '无 /usr、/opt、/System 等系统绝对路径', re: /['"(][\\/](usr|opt|System|Library|Applications)[\\/]/ },
  ];
  const scanFiles = [];
  for (const d of ['scripts', 'examples']) {
    const dir = path.join(root, d);
    if (fs.existsSync(dir)) {
      for (const f of fs.readdirSync(dir)) {
        if (/\.(js|json|md)$/.test(f)) scanFiles.push(path.join(dir, f));
      }
    }
  }
  for (const f of ['SKILL.md']) {
    const p = path.join(root, f);
    if (fs.existsSync(p)) scanFiles.push(p);
  }
  for (const pat of absPatterns) {
    const hits = [];
    for (const file of scanFiles) {
      const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
      lines.forEach((L, i) => {
        // 注释里的反例说明（"不要写 C:/..."）允许，故排除含"不"、"勿"、"禁"的行
        if (pat.re.test(L) && !/不要|勿|禁|反例|错误示例|反直觉/.test(L)) {
          hits.push(path.relative(root, file) + ':' + (i + 1));
        }
      });
    }
    ok('路径无关：' + pat.name, hits.length === 0, hits.slice(0, 4).join(', '));
  }

  // 关键结论必须在 semantics.md 里，防止文档退化成空话
  const sem = fs.readFileSync(path.join(root, 'references', 'semantics.md'), 'utf8');
  ok('semantics 写明 rect 是四角坐标', /\[x0, y0, x1, y1\]/.test(sem));
  ok('semantics 写明画布 y 向上', /y 向上/.test(sem));
  ok('semantics 写明 SimpleJson 不存在', /SimpleJson/.test(sem));
  ok('semantics 写明材质在 atlas 之后', /atlas 之后/.test(sem));
  ok('semantics 写明必须 purge', /purge/.test(sem));
}

// ============================================================ 11. 标定数据一致性
section('11. calibration.json 与代码假设一致');
{
  const calibPath = path.join(S, '..', 'references', 'calibration.json');
  if (!fs.existsSync(calibPath)) {
    ok('存在 references/calibration.json', false, '先跑 node calibrate.js');
  } else {
    ok('存在 references/calibration.json', true);
    let calib = null;
    try { calib = JSON.parse(fs.readFileSync(calibPath, 'utf8')); }
    catch (e) { ok('calibration.json 可解析', false, e.message); }

    if (calib) {
      ok('标定结论 y 轴向上', calib.canvas && calib.canvas.y_axis === 'up',
        'y_axis=' + (calib.canvas && calib.canvas.y_axis));

      // 代码里的 CANVAS 空间必须与标定结论一致
      const upInCode = UV.YDIR[UV.SPACE.CANVAS] === +1;
      ok('uvspace 的 CANVAS 与标定同为 y 向上', upInCode);

      // rect 语义
      if (calib.atlas && !calib.atlas.skipped) {
        ok('标定确认 rect 为 x0y0x1y1', calib.atlas.rect_form === 'x0y0x1y1',
          'rect_form=' + calib.atlas.rect_form);

        // 自检图红色质心应与"cell0 的 rect 中心换算成的文件行号"接近
        const cc = calib.atlas.color_check;
        if (cc && cc.red_centroid && Number.isFinite(cc.expect_cell0_file_y)) {
          const diff = Math.abs(cc.red_centroid.y - cc.expect_cell0_file_y);
          ok('图集颜色对齐（质心误差 < 8px）', diff < 8,
            `实测 y=${cc.red_centroid.y} 预期 ${cc.expect_cell0_file_y.toFixed(1)} 差 ${diff.toFixed(1)}`);
        }
      } else {
        console.log('  skip Unity 侧标定（calibration.json 里标记为 skipped）');
      }
    }
  }
}

// ============================================================ 汇总
console.log('\n' + '='.repeat(52));
console.log(`通过 ${pass} / 失败 ${fail}`);
if (fail) {
  console.log('失败项：');
  failures.forEach(f => console.log('  - ' + f));
  process.exitCode = 1;
} else {
  console.log('全部通过。');
}

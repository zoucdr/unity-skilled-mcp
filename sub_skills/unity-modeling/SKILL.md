---
name: unity-modeling
description: Unity 建模全链路技能。硬表面走 ProBuilder 分件→合并→UV 图集→TexGen 贴图/PBR；曲面（杯体/花瓶/圆柱、贴图沿柱面连续展开）走 C# 程序化旋转体+扫掠+柱面 UV，由 lathe.js 硬护栏与 verify 的 lathe 档兜底。另可调度 UISourceGen 做 UI/图标/标牌。适用于"建个模型带贴图""单 mesh 一张贴图""单 mesh 多材质""做个杯子/瓶子""做套无缝材质""给场景做块招牌"等请求。
agent_created: true
---

# Unity 建模技能

## 这个技能解决什么

**两条互斥的主干**，先选路再开工。选错会浪费整轮往返：曲面模型用 ProBuilder 图集会破坏 UV 连续性，硬表面用旋转体是杀鸡用牛刀。

| 主干 | 适用 | 核心脚本 | 文档 |
|---|---|---|---|
| **图集流水线 A–D** | 硬表面、能拆成平面分区 | `model.js` + `tex.js` / `ui.js` | `pipeline.md` A–D |
| **曲面流水线 E** | 圆柱 / 杯 / 瓶，图案沿柱面连续展开 | `lathe.js` + C# 程序化网格 | `pipeline.md` E |

两者共用底座 `mcp.js`（MCP 调用、运行时路径解析、C# 静态预检）和交付校验 `verify.js`
（曲面要传 `profile:'lathe'`）。每段都有一层脚本把已知的坑挡掉。

## 开工前必读

**`TUTORIAL.md`** —— 教学文档：六层架构、提示词五段式模板、锦鲤杯全流程案例复盘，以及覆盖 A–E 的八个案例库（墙 / 猴子 / 手机 / 打雪仗海报 / 电视 / 彩虹球 / 网球场 / 铁门）。第一次用这个技能，或要写提示词把活派给 AI，先读它；本篇 `SKILL.md` 是干活时的速查手册。

**`references/semantics.md`** —— 24 条实测结论，每一条都是踩过的坑，并且和源码注释/官方文档相矛盾的地方都标了出来。不看这个直接干，会重复付出代价。第六节（20–24）专讲曲面，由 `scripts/lathe.js` 强制执行。

尤其这三条：

1. **画布 y 向上**，`y = v * H`，不需要 `1 - v`。源码注释写 `Y always grows downward` 是**错的**。
2. **`uv_atlas` 的 `rect` 是 `[x0, y0, x1, y1]`**，`y` 直接就是 `v`。不是 `[x, y, w, h]`。
3. **同名不同义**：`rotation` 在 texgen 是单个角度，在 probuilder 是 `[x,y,z]`。校验必须按工具区分。

## 标准流程（单 mesh + 单图集贴图）

这是最常用的一条，`examples/wooden_crate.js` 是完整可跑样板。

```js
const mcp    = require('./scripts/mcp');
const model  = require('./scripts/model');
const tex    = require('./scripts/tex');
const verify = require('./scripts/verify');

const project = mcp.resolveProject();   // 自动按 cwd / 环境变量 / skill.ini 推导
mcp.configure({ project });

// 0. 幂等清场 —— 不清理会堆出同名对象，看起来像"材质没生效"
await model.purge(['Crate', model.PART_PREFIX + '*']);

// 1. 建分件（统一用 PBM_ 前缀，便于筛选）
await model.box('PBM_Body', { size: [1.0, 0.72, 0.8], position: [0, 0.36, 0] });
await model.box('PBM_Lid',  { size: [1.06, 0.08, 0.86], position: [0, 0.76, 0] });

// 2. 合并成单 mesh
await model.mergeParts({ name: 'Crate' });

// 3. 重建非重叠 UV 图集，并把 mesh 存成 .asset
const atlas = await model.atlas({
  gameObject: 'Crate', columns: 4,
  meshPath: 'Assets/MyModel/Crate_Mesh.asset',
  width: 1024, height: 1024,
});

// 4. 每个图集格画一块内容
const layers = atlas.cells.map(c =>
  tex.atlasRectToLayer(c, 1024, 1024, { shape: 'rect', fill: 'solid', color: '#8a5a33' }));
await tex.forMesh({
  meshPath: 'Assets/MyModel/Crate_Mesh.asset',
  layers, width: 1024, height: 1024, groupBy: 'island',
  outputPath: 'Assets/MyModel/T_Crate.png',
});

// 5. 材质必须在 atlas 之后挂（atlas 会把 renderer 重置成 ProBuilderDefault）
await tex.material({ path: 'Assets/MyModel/M_Crate.mat', mainTex: 'Assets/MyModel/T_Crate.png' });
await tex.assignMaterial('Crate', 'Assets/MyModel/M_Crate.mat');

// 6. 校验
const result = await verify.run({
  object: 'Crate', meshPath: 'Assets/MyModel/Crate_Mesh.asset',
  texture: 'Assets/MyModel/T_Crate.png', projectRoot: project,
  expectSubMeshes: 1, noThrow: true,
});
console.log(verify.report(result));
```

## 选路决策表

先按交付物形态选一条，**别直接照搬下面的标准流程**。

| 你要什么 | 走哪条 | 入口 |
|---|---|---|
| 单个硬表面模型 + 一张贴图 | A | `examples/wooden_crate.js`（下方标准流程） |
| 单 mesh + 多材质（多 submesh） | B | `model.splitSubmeshes({ target, groups, materialPaths })` |
| 一套可平铺 PBR 材质，不要求特定模型 | C | `tex.pbr()`；**不要自己写高度场求梯度**，原生支持且同源对齐 |
| UI / 图标 / 文字标牌当贴图 | D | `ui.signboard()` / `ui.icon()` / `ui.render()` |
| **曲面（杯 / 瓶 / 柱），图案沿柱面连续展开** | **E** | `examples/fish_mug.js`（下方流水线 E） |
| 复杂曲线图案（鱼、花纹、书法） | 外部绘制 PNG → 塞进 `Assets/` → 再走 A 或 E | `examples/fish_mug_texture.py` |

最后一行是**能力边界**：TexGen 图层和 UISourceGen 画布画不了复杂贝塞尔曲线，
别在里面反复试探（实测过，白白浪费整轮往返）。

B 的注意事项：`splitSubmeshes` 会**剥掉 ProBuilderMesh**，之后 `model.info()` 自动降级到纯 Mesh 读取，
且不能再挤出/倒角 —— 几何编辑要在 split 之前做完。

## 路径与环境（脚本内无任何绝对路径）

技能里**不写死盘符、用户目录、仓库位置**，全部运行时解析。搬运技能目录、把
`unity-skilled-mcp` 装在任意位置，都不需要改代码。

**客户端脚本**（`unity_bridge.js` 或旧名 `unity_mcp.js`）探测顺序：

1. `configure({ loader })` 显式指定
2. 环境变量 `UNITY_MCP_LOADER`
3. 运行中的 Unity 写下的 `~/.unity-mcp/skill.ini` 的 `skill_dir`
4. 从 **当前目录** 与 **技能自身目录** 逐级上溯，找 `unity-package/Skills/unity-skilled-mcp/scripts` 等常见落位
5. 各工具的用户级技能目录（`~/.workbuddy/skills/`、`~/.claude/skills/` 等）

**Unity 工程根**解析顺序：

1. `configure({ project })` 或 `--project` 显式指定
2. 环境变量 `UNITY_PROJECT` / `UNITY_PROJECT_PATH`
3. `~/.unity-mcp/skill.ini` 的 `project_path`（当前连着的工程）
4. 从 cwd 与技能目录上溯，找同时含 `Assets/` 与 `ProjectSettings/` 的目录

解析失败时报错会列出**所有试过的路径**和三种指定方式，不再出现"路径写死但机器不同"的静默错连。

```js
const mcp = require('./scripts/mcp');
mcp.resolveProject();          // 只求工程根，不发请求
mcp.loaderCandidates();        // 看它打算从哪里加载客户端脚本
```

## 流水线 E：程序化曲面（与 A 并列）

圆柱、杯体、花瓶，或贴图必须沿柱面精确展开时走这条。样板是 `examples/fish_mug.js`：
C# 旋转体 + 扫掠把手 + 柱面 UV + 多 submesh 材质，零绝对路径、可重复执行。

| # | 动作 | 关键点 |
|---|---|---|
| 0 | `lathe.guard({...})` **离线**跑护栏 | 贴图尺寸从实际 PNG 读，C# 源码静态比对；改坏即抛错，不浪费 Unity 往返 |
| 1 | 外部绘制柱面展开 PNG | 宽高比 ≈ 周长:高度，本例 4096×1024 对应 4.37:1 |
| 2 | C# lathe 生成外壁 / 口沿 / 内壁三组 profile | `subMeshCount=3`；柱面 UV `u = 1 - j/N`（取反抵消镜像） |
| 3 | 扫掠管生成把手 | 三角形 `(a,c,b)` / `(b,c,d)`，法线朝外 |
| 4 | 导入贴图，按 submesh 顺序挂材质 | `[外壁贴图, 内壁, 口沿]`；**无图集，不要调 `uv_atlas`** |
| 5 | `verify.run({ profile:'lathe', ... })` | 查 submesh 数、材质数、贴图宽高比、外壁法线朝外比例 |

```bash
node examples/fish_mug.js                 # 自动解析工程，建模 + 截图
node examples/fish_mug.js --verify       # 追加在线校验
node examples/fish_mug.js --regen-texture --no-shot   # 只重绘贴图（需 Pillow）
```

五条实测铁律（20–24）已固化成 `scripts/lathe.js` 的硬断言，不再只靠读文档：
宽高比 4.37:1、`u = 1 - j/N`、扫掠绕序、金属度 ~0.4、口沿壁厚 0.036。
详见 `references/semantics.md` 第六节与 `examples/README.md`。

**场景隔离**：示例会隐藏所有非 `Mug*` 且带 renderer 的根对象。这是权宜之计，
根治方案是新建独立 Scene（当前 demo 场景曾被残留的 `Monkey` 污染过）。

## 脚本清单

| 脚本 | 职责 | 关键导出 |
|---|---|---|
| `mcp.js` | MCP 调用底座 + 路径运行时解析 | `call` `runCode` `waitTask` `precheck` `lintCSharp` `resolveProject` `loaderCandidates` |
| `model.js` | 建模 | `box` `cylinder` `sphere` `prism` `build` `mergeParts` `atlas` `splitSubmeshes` `purge` `info` `assertSingleMesh` |
| `tex.js` | 贴图 | `forMesh` `pbr` `render` `atlasRectToLayer` `material` `assignMaterial` `debugAtlasLayers` |
| `ui.js` | UI | `render` `draw` `signboard` `icon` `measure` `svg` `resolveFont` |
| `verify.js` | 校验 | `run` `runLathe` `report`（曲面传 `profile:'lathe'`） |
| `calibrate.js` | **实测坐标语义**，产出 `references/calibration.json` | 命令行脚本 |
| `uvspace.js` | 坐标唯一收口 | `rectUV` `layerRect` `uvToPx` |
| `field.js` | 高度场 | `Field` `gradientToNormal` `subtract` |
| `png.js` | PNG 编解码 | `readPNG` `decodePNG` `encodePNG` `neighborDelta` |
| `inspect.js` | 贴图体检 | `analyze` `seamDelta` `normalReport` `tilePreview` |
| `lathe.js` | **曲面几何硬护栏** | `guard` `assertTextureAspect` `uvOrientation` `assertSweepWinding` `assertCSharp` |
| `check.js` | 技能自检 | `node check.js`（150 项，离线） |
| `export_html.py` | 教学文档导出 | `python scripts/export_html.py` → 自包含可打印 `TUTORIAL.html` |

## 铁律

1. **分件统一 `PBM_` 前缀**，否则 `mergeParts` 找不到。合并后前缀消失，重命名走 `gameobject.modify`。
2. **材质在 `atlas` 之后挂**。`uv_atlas` 会重建 Mesh 并把 renderer 重置为 `ProBuilderDefault`。
3. **脚本必须幂等**：开头 `model.purge([...])`。连跑两次不清理会堆出多个同名对象，后建的白色版本盖住有材质的，看起来像"材质没生效"——实测踩过，连跑 3 次留下 6 个。
4. **贴图画完必须 `inspect.js` 看一眼**，尤其结构化图案：接缝数值会误报（砖墙 5.69 但实际无缝），要 `--tile` 生成 2×2 拼贴肉眼判断。
5. **`code.execute` 只接受 C# 语句片段**，不能 `return;`（返回类型是 object），工程里**没有 `SimpleJson`**，不要试图反序列化 JSON。
6. **交付前跑 `verify.run()`**。图集流水线把"单 mesh / 非空 / UV 覆盖 / 贴图非纯色 / 材质挂载"逐项断言；**曲面要传 `profile:'lathe'`**，默认档假设单 submesh + 图集，会误报。
7. **曲面参数别凭印象改**：`fish_mug.js` 在提交前会跑 `lathe.guard()`，从实际 PNG 读尺寸、静态比对 C# 里的 UV 公式 / 绕序 / 口沿半径。改了 C# 或 Python 却没同步 `SPEC`，会直接抛错。

## 参考文档

- `TUTORIAL.md` — **教学文档**：架构分层、如何写提示词、锦鲤杯复盘 + 八案例库（不教功能细节）
- `TUTORIAL.html` — 教学文档的 A4 可打印版（图片内嵌，浏览器 Ctrl+P 另存为 PDF），由 `scripts/export_html.py` 生成
- `references/semantics.md` — **24 条实测铁律**（第六节为曲面），含"源码注释是错的"这类反直觉结论
- `references/modeling.md` — ProBuilder / mesh 工具全表、分件策略、单 mesh 与多材质
- `references/texturing.md` — TexGen 图层语法、图案/渐变/法线、PBR 派生、无缝平铺
- `references/ui.md` — UISourceGen 语法、控件类型、九宫格、做贴图的用法
- `references/pipeline.md` — 三条流水线的详细步骤与排错清单
- `references/samples/` — 实测证据：图集贴图、场景截图、UV 对齐自检图、UI 标牌

## 自检

```bash
node scripts/check.js     # 150 项离线断言，全绿才可信
node scripts/calibrate.js --project <工程>   # 换工程/升级 MCP 后重新实测坐标语义
```

`check.js` 会校验 `calibration.json` 的实测结论与 `uvspace.js` 的代码假设是否一致 ——
两者漂移会立刻报错，避免"文档写一套、代码跑另一套"。

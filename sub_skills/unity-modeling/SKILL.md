---
name: unity-modeling
description: Unity 建模全链路技能。用 ProBuilder 建分件、合并成单 mesh、重建非重叠 UV 图集，用 TexGen 按 UV 分区贴图或派生整套 PBR，用 UISourceGen 画 UI/图标/标牌当贴图，最后跑校验确认交付。适用于"建个模型带贴图""单 mesh 一张贴图""单 mesh 多材质""做套无缝材质""给场景做块招牌"等请求。
agent_created: true
---

# Unity 建模技能

## 这个技能解决什么

把 Unity 里"从零做一个能用的模型"拆成三段可控的流水线，每段都有一层脚本把已知的坑挡掉：

| 段 | 做什么 | 谁负责 |
|---|---|---|
| **建模** | 分件 → 合并 → 单 mesh（可选多 submesh） | `scripts/model.js` |
| **贴图** | UV 图集 → 按分区绘制 / PBR 派生 | `scripts/tex.js` |
| **UI** | 界面 / 图标 / 文字标牌当贴图 | `scripts/ui.js` |
| **校验** | 交付前逐项断言 | `scripts/verify.js` |

底座是 `scripts/mcp.js`：统一 MCP 调用、异步任务轮询、参数预检、C# 静态检查。

## 开工前必读

**`references/semantics.md`** —— 19 条实测结论，每一条都是踩过的坑，并且和源码注释/官方文档相矛盾的地方都标了出来。不看这个直接干，会重复付出代价。

尤其这三条：

1. **画布 y 向上**，`y = v * H`，不需要 `1 - v`。源码注释写 `Y always grows downward` 是**错的**。
2. **`uv_atlas` 的 `rect` 是 `[x0, y0, x1, y1]`**，`y` 直接就是 `v`。不是 `[x, y, w, h]`。
3. **同名不同义**：`rotation` 在 texgen 是单个角度，在 probuilder 是 `[x,y,z]`。校验必须按工具区分。

## 标准流程（单 mesh + 单图集贴图）

这是最常用的一条，`examples/wooden_crate.js` 是完整可跑样板。

```js
const model = require('./scripts/model');
const tex   = require('./scripts/tex');
const verify = require('./scripts/verify');

mcp.configure({ project: '<Unity 工程根>' });   // 可省略：自动按 cwd/环境变量推导

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
  texture: '/abs/path/Assets/MyModel/T_Crate.png', expectSubMeshes: 1, noThrow: true,
});
console.log(verify.report(result));
```

## 三条常见需求的走法

**单 mesh + 一张贴图** → 上面的标准流程。

**单 mesh + 多个材质（多 submesh）** → 合并后调 `model.splitSubmeshes({ target, groups, materialPaths })`，按世界坐标范围把三角形分到不同 submesh。
注意：这会**剥掉 ProBuilderMesh**，之后 `model.info()` 会自动降级到纯 Mesh 读取。

**一套无缝 PBR 材质（不需要特定模型）** → 直接 `tex.pbr({ layers, outputDir, prefix })`，
一次产出 BaseColor / Height / Normal / Roughness / AO / ORM。**不要自己写高度场求梯度**（本项目原生支持，且同源对齐）。

**给场景做 UI / 图标 / 招牌** → `ui.signboard()` / `ui.icon()` / `ui.render()`，产出透明 PNG，可当贴图或世界空间 UI 用。

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

## 曲面贴图 / 旋转体（进阶）

模型是圆柱、杯体、花瓶，或贴图必须沿柱面精确展开时，ProBuilder 图集不够用，
改走 `examples/fish_mug.js`：C# 程序化旋转体 + 扫掠把手 + 柱面 UV + 多 submesh 材质，
零绝对路径、可重复执行。四条硬约束：

1. 贴图宽高比 ≈ **柱面周长 : 高度**（本例 4096×1024 对应 4.37:1），用 2:1 会把图案横向拉伸约 2 倍。
2. `u = 1 - j/N`：相机在 `-Z` 看向 `+Z` 时 `x = r·sinθ` 会让贴图左右镜像，必须取反。
3. 扫掠管三角形绕序反转 `(a,c,b)`，否则法线朝内、把手被背面剔除。
4. 无反射探针时金属度别给满（~0.4），内壁补一点 `_EMISSION` 才有亮釉色。

完整踩坑记录见 `examples/README.md`。

## 脚本清单

| 脚本 | 职责 | 关键导出 |
|---|---|---|
| `mcp.js` | MCP 调用底座 + 路径运行时解析 | `call` `runCode` `waitTask` `precheck` `lintCSharp` `resolveProject` `loaderCandidates` |
| `model.js` | 建模 | `box` `cylinder` `sphere` `prism` `build` `mergeParts` `atlas` `splitSubmeshes` `purge` `info` `assertSingleMesh` |
| `tex.js` | 贴图 | `forMesh` `pbr` `render` `atlasRectToLayer` `material` `assignMaterial` `debugAtlasLayers` |
| `ui.js` | UI | `render` `draw` `signboard` `icon` `measure` `svg` `resolveFont` |
| `verify.js` | 校验 | `run` `report` |
| `calibrate.js` | **实测坐标语义**，产出 `references/calibration.json` | 命令行脚本 |
| `uvspace.js` | 坐标唯一收口 | `rectUV` `layerRect` `uvToPx` |
| `field.js` | 高度场 | `Field` `gradientToNormal` `subtract` |
| `png.js` | PNG 编解码 | `readPNG` `decodePNG` `encodePNG` `neighborDelta` |
| `inspect.js` | 贴图体检 | `analyze` `seamDelta` `normalReport` `tilePreview` |
| `check.js` | 技能自检 | `node check.js`（96 项，离线） |

## 铁律

1. **分件统一 `PBM_` 前缀**，否则 `mergeParts` 找不到。合并后前缀消失，重命名走 `gameobject.modify`。
2. **材质在 `atlas` 之后挂**。`uv_atlas` 会重建 Mesh 并把 renderer 重置为 `ProBuilderDefault`。
3. **脚本必须幂等**：开头 `model.purge([...])`。连跑两次不清理会堆出多个同名对象，后建的白色版本盖住有材质的，看起来像"材质没生效"——实测踩过，连跑 3 次留下 6 个。
4. **贴图画完必须 `inspect.js` 看一眼**，尤其结构化图案：接缝数值会误报（砖墙 5.69 但实际无缝），要 `--tile` 生成 2×2 拼贴肉眼判断。
5. **`code.execute` 只接受 C# 语句片段**，不能 `return;`（返回类型是 object），工程里**没有 `SimpleJson`**，不要试图反序列化 JSON。
6. **交付前跑 `verify.run()`**。它把"单 mesh / 非空 / UV 覆盖 / 贴图非纯色 / 材质挂载"逐项断言，失败直接说哪一项、大概什么原因。

## 参考文档

- `references/semantics.md` — **19 条实测铁律**，含"源码注释是错的"这类反直觉结论
- `references/modeling.md` — ProBuilder / mesh 工具全表、分件策略、单 mesh 与多材质
- `references/texturing.md` — TexGen 图层语法、图案/渐变/法线、PBR 派生、无缝平铺
- `references/ui.md` — UISourceGen 语法、控件类型、九宫格、做贴图的用法
- `references/pipeline.md` — 三条流水线的详细步骤与排错清单
- `references/samples/` — 实测证据：图集贴图、场景截图、UV 对齐自检图、UI 标牌

## 自检

```bash
node scripts/check.js     # 96 项离线断言，全绿才可信
node scripts/calibrate.js --project <工程>   # 换工程/升级 MCP 后重新实测坐标语义
```

`check.js` 会校验 `calibration.json` 的实测结论与 `uvspace.js` 的代码假设是否一致 ——
两者漂移会立刻报错，避免"文档写一套、代码跑另一套"。

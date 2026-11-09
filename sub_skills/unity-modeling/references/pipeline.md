# 流水线

五条路线、**两条主干**，按交付物选一条。A–D 属图集主干（ProBuilder + TexGen），
E 属曲面主干（C# 程序化网格 + 柱面 UV）。两条主干互斥，选错会破坏 UV 连续性或白做功夫。
每条都有可运行的样例或命令；选路表见 `SKILL.md`。

---

## A. 单 mesh + 单张图集贴图

最常用。多个分件拼出一个物体，合并成一个 mesh，一张贴图覆盖全部。

**可运行样板**：`examples/wooden_crate.js`

```bash
# 工程根可省略：在工程目录内运行，或用 UNITY_PROJECT 指定
node examples/wooden_crate.js
node examples/wooden_crate.js --project <Unity 工程根>
```

### 步骤

| # | 动作 | 关键点 |
|---|---|---|
| 0 | `model.purge(['Name', 'PBM_*'])` | 幂等。不清会堆同名对象 |
| 1 | `model.box/cylinder/sphere/prism(...)` | 统一 `PBM_` 前缀，`position` 是几何中心 |
| 2 | `model.mergeParts({ name })` | 校验 `totalFacesAfter == totalFacesBefore` |
| 3 | `model.atlas({ columns, meshPath })` | 连通件每件一格；bake 成 `.asset` 让 UV 持久化 |
| 4 | `tex.atlasRectToLayer(cell, W, H)` → `tex.forMesh` | 每格画一块；`groupBy: 'island'` |
| 5 | `tex.material` → `tex.assignMaterial` | **必须在 atlas 之后** |
| 6 | `verify.run(...)` | 全项断言 |

### 实测数字（木箱示例）

```
6 个分件 → 合并后 1 个，面 36，顶点 144
图集：6 个连通件，columns=4 → rows=2
cell0 rect=[0.02,0.04,0.23,0.46]
cell5 rect=[0.27,0.54,0.48,0.96]
贴图 1024×1024，218587 字节，渲染画布 1 张（全局 layers 被缓存）
校验：单 mesh ✓ 非空 ✓ UV 覆盖 0.44 ✓ 贴图有内容 ✓ 材质 ✓
```

### UV 对齐自检（可选的保险）

不确定 UV 和贴图对不对时，先画纯色格：

```js
const layers = tex.debugAtlasLayers(atlas, 1024, 1024);   // 每格一个醒目颜色
await tex.forMesh({ meshPath, layers, groupBy: 'island', outputPath: 'Assets/debug.png' });
```

每格颜色不同且位置与 `cells[i].rect` 对应，说明映射正确。见 `samples/atlas_alignment_debug.png`。

---

## B. 单 mesh + 多材质（多 submesh）

例：一张网球场地，地面一个材质、围网另一个材质，但只有一个 mesh。

### 步骤

| # | 动作 | 关键点 |
|---|---|---|
| 1 | 建分件并合并 | `mergeObjects` 会把所有面塞进 submesh 0 |
| 2 | `model.splitSubmeshes({ target, groups })` | 按**世界坐标 AABB** 分组三角形 |
| 3 | 各自贴图 / 各自建材质 | 每个 submesh 一套 |
| 4 | `verify.run({ expectSubMeshes: N })` | 校验 submesh 数量 |

```js
await model.splitSubmeshes({
  target: 'Court',
  groups: [
    { name: 'ground', yMin: -99, yMax: 0.05 },
    { name: 'fence',  yMin: 0.05, yMax: 99 },
  ],
  materialPaths: ['Assets/Tennis/M_Ground.mat', 'Assets/Tennis/M_Fence.mat'],
});
```

返回的 `logs` 里会打印每组的三角形数与 `fallback`（没落进任何组的数量，应为 0）：

```
SPLIT: Court subMeshCount=2 tris ground=26 fence=10  fallback=0
```

### 代价

**这一步之后对象脱离 ProBuilder**：`probuilder.getMeshInfo` 会报"没有 ProBuilderMesh 组件"。
- `model.info()` 会自动降级到纯 Mesh 读取（`source: 'mesh-fallback'`）
- 但**不能再挤出/倒角**。几何编辑要在 split 之前做完

### 分组边界是局部还是世界？

**世界坐标**。合并后顶点是局部坐标（相对合并对象的 transform），`splitSubmeshes` 内部做了 `TransformPoint`。
实测：三部件世界 y 0~1.1，合并后 Mesh 局部 y 范围是 `-0.5~0.6` —— 按局部坐标分组会全落空。

---

## C. 一套无缝 PBR 材质

不需要特定模型，只要一套能平铺的材质（石头、砖、木头）。

```js
await tex.pbr({
  layers: [
    { shape: 'rect', center: [512, 512], size: [1024, 1024],
      fill: 'pattern', pattern: 'brick', cell: 128, gap: 6,
      gapColor: '#2a2a28', color: '#9a8d7a', variation: 0.25, seed: 3 },
  ],
  width: 1024, height: 1024,
  outputDir: 'Assets/Textures', prefix: 'Brick',
  tiling: true, normalStrength: 5, metallic: 0,
});
```

产出 6 张：`BaseColor` / `Height` / `Normal` / `Roughness` / `AO` / `ORM`。

### 验证

```bash
node scripts/inspect.js Assets/Textures/Brick_BaseColor.png --tile 2   # 看拼贴有没有缝
node scripts/inspect.js Assets/Textures/Brick_Normal.png --normal      # 法线合理性
```

法线合格标准（实测参考）：平均长度 ≈ 1.000、范围在 [0.99, 1.01]、蓝通道无像素 < 100。

### 挂到材质

```js
await tex.material({
  path: 'Assets/Textures/M_Brick.mat',
  mainTex: 'Assets/Textures/Brick_BaseColor.png',
  normalMap: 'Assets/Textures/Brick_Normal.png',
  glossiness: 0.15, metallic: 0,
});
```

法线图需要在导入设置里是 **NormalMap 类型**（`sRGBTexture = false`），否则光照会怪。
`texgen` 写 PNG 时会自动处理；手动导入的图要自己改。

---

## D. UI / 标牌当贴图

```js
const U = require('./scripts/ui');
await U.signboard({ text: 'OPEN', width: 512, height: 256,
                    outputDir: 'Assets/Sign', baseName: 'Sign' });
// → Assets/Sign/Sign/preview.png
```

然后当成普通图片用：`fill: 'image'` 的 layer，或直接做材质 `_MainTex`。

细节见 `ui.md`。

---

## E. 程序化曲面 + 柱面展开贴图

圆柱、杯体、花瓶，或图案必须沿柱面**连续**展开时走这条。ProBuilder 图集会把曲面切成孤岛，
贴上去必然断裂。完整样板 `examples/fish_mug.js`（零绝对路径，可重复执行）。

### 步骤

| # | 动作 | 关键点 |
|---|---|---|
| 0 | `lathe.guard({...})` 离线跑护栏 | 从实际 PNG 读尺寸，静态比对 C#；改坏即抛错 |
| 1 | `fish_mug_texture.py` 画柱面展开图 | BaseColor + RGBA MetallicGloss；复杂曲线用 PIL，别用 TexGen 图层 |
| 2 | 贴图同步进 `Assets/` | Unity 只认工程内资源 |
| 3 | `code.execute` 提交 C#：lathe 三组 profile | 外壁 / 口沿 / 内壁 → `subMeshCount=3`；柱面 `u = 1 - j/N` |
| 4 | 同一次 C# 生成扫掠把手、材质、灯光、相机 | 材质顺序 `[外壁贴图, 内壁, 口沿]` |
| 5 | `camera.screenshot` + `verify.run({profile:'lathe'})` | 看渲染图，再跑在线断言 |

```bash
node examples/fish_mug.js                    # 建模 + 截图
node examples/fish_mug.js --verify          # 追加在线校验
node examples/fish_mug.js --regen-texture --no-shot   # 只重绘贴图（需 Pillow）
```

### 五条硬约束（由 `scripts/lathe.js` 强制执行）

| 约束 | 实测值 | 断言函数 |
|---|---|---|
| 贴图宽高比 ≈ 周长:高度 | 4.37:1 → `4096×1024`（误差 8.5%） | `assertTextureAspect` |
| `u = 1 - j/N` | `x=r·sinθ` + 相机在 `-Z` 时必取反 | `uvOrientation` / `assertCSharp` |
| 扫掠管 `(a,c,b)` | 内侧绕序会被背面剔除 | `assertSweepWinding` |
| 金属度 ~0.4 | 无反射探针时 0.86 会发黑 | `assertMetallic` |
| 口沿壁厚 0.036 | `0.064/0.85=7.5%` 会成粗环 | `assertRimThickness` |

`verify.run({profile:'lathe'})` 在线查：submesh 数、材质数、贴图宽高比、
外壁法线朝外比例（实测 1536/1536 = 100%）。默认档假设单 submesh + 图集，**不能用于曲面**。

### 场景隔离

示例会隐藏所有非 `Mug*` 且带 MeshRenderer 的根对象（相机、灯无 renderer，不受影响）。
曾只靠硬编码名单，结果场景里后加的 `Monkey` 没被隐藏、俯视时坐在杯子里。
**根治方案是新建独立 Scene**，不要长期依赖 `SetActive(false)` 名单。

---

## 通用排错清单

按现象查，不按步骤查。

| 现象 | 最可能原因 | 立刻可跑的检查 |
|---|---|---|
| 模型全白 | 渲染器是 `ProBuilderDefault` | `tex.assignMaterial` 是否在 atlas 之后 |
| 场景里多个同名对象 | 没 purge | `model.findObjects('^Name$').length` |
| `mergeParts` 找到 0 个 | 分件没加 `PBM_` 前缀 | `model.listParts()` |
| 贴图整张纯色 | `gradient` 写成纯颜色数组 | 改对象数组 |
| 贴图上下颠倒 | 多写了一次 `1 - v` | 画布 y 就是 v |
| 只有部分格有内容 | `cells` 与 `layers` 数量不匹配 | `atlas.cells.length` vs `layers.length` |
| `getMeshInfo` 报没有 ProBuilderMesh | 已 split 过 | 用 `model.info()` 自动降级 |
| `code.execute` 编译失败一堆错 | 用了 `SimpleJson` 或 `return;` | 改用 C# 字面量、去掉 return |
| 子网格材质丢失 | `mergeObjects` 抹掉了归属 | 用 `splitSubmeshes` 重建 |
| 任务"等待超时（undefinedms）" | `opts.timeoutMs` 传了 undefined | 已在 `waitTask` 里兜底 |
| `material.create` 报已存在 | 非幂等 | `tex.material()` 已处理复用 |
| 杯身图案横向拉伸约 2 倍 | 贴图宽高比没匹配周长:高度 | `lathe.assertTextureAspect` |
| 贴图 / 文字左右镜像 | `x=r·sinθ` 时 u 没取反 | `lathe.uvOrientation` |
| 把手完全看不见 | 扫掠管用了内侧绕序 | `lathe.assertSweepWinding` |
| 金色发黑、内壁发暗 | 无探针却给满金属度 / 内壁无自发光 | `lathe.assertMetallic`、`_EMISSION` |
| 口沿一圈粗色环 | 壁厚过大 | `lathe.assertRimThickness` |
| 场景里冒出别的对象 | 隐藏名单漏了新增对象 | 兜底规则，或新建独立 Scene |
| 曲面跑默认 `verify.run` 误报 | 默认档假设单 submesh + 图集 | 传 `profile:'lathe'` |

---

## 交付前的最后三件事

1. `node scripts/check.js` —— 技能自检全绿（说明脚本假设没坏）
2. `verify.run({...})` —— 本次结果逐项通过（曲面加 `profile:'lathe'`）
3. `inspect.js` 看一眼贴图（结构化图案用 `--tile 2`）

三件都过，才算可以交付。

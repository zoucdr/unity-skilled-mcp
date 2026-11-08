# 建模参考

## 工具全表

### `probuilder.*` — 几何编辑

| 动作 | 关键参数 | 用途 |
|---|---|---|
| `createShape` | `shapeType` `size` `position` `rotation` `name` `isLocalSpace` | 建基本体 |
| `createPolyShape` | `points:[[x,z],...]` `height` `flipNormals` | 任意平面轮廓拉伸成棱柱 |
| `extrude` | `faceIndices` `distance` `extrudeMethod` | 面挤出 |
| `deleteFaces` | `faceIndices` `faceDirection` | 删面 |
| `flipNormals` | `faceIndices` | 翻法线 |
| `setFaceMaterial` | `materialPath` `faceIndices` | 按面指定材质 |
| `bevel` | `edges` `amount` | 边倒角 |
| `bridge` | `edgeA` `edgeB` | 边桥接 |
| `connectEdges` | `edges` | 连接边（加线） |
| `subdivideEdges` | `edges` `subdivisions` | 细分边 |
| `mergeObjects` | `gameObjects` `deleteSourceObjects` | **合并多对象 → 单 mesh** |
| `boolean` | `target` `tool` `operation` | 并/差/交 |
| `setPivot` | `pivotLocation` `customPosition` | 改轴心 |
| `getMeshInfo` | `detail` | 面数/顶点/bounds/面朝向 |
| `uv_atlas` | `columns` `rows` `margin` `projection` `mesh_path` | **重建非重叠 UV 图集** |
| `uv_info` | `width` `height` | 查 UV0 岛/覆盖率/重叠 |

`shapeType` 全 12 种：`Cube` `Cylinder` `Sphere` `Plane` `Prism` `Cone` `Stair` `Door` `Pipe` `Arch` `Sprite` `Torus`

### `mesh.*` — 网格资产（不走 ProBuilder）

| 动作 | 参数 | 用途 |
|---|---|---|
| `generate_primitive` | `path` `mesh_type`(cube/sphere/cylinder/plane/custom) `properties` | 直接生成 mesh 资产 |
| `optimize` | `path` `optimization_level` | 减面/去重顶点 |
| `subdivide` | `path` `subdivision_level` | 细分 |
| `smooth` | `path` `smooth_factor` | 平滑法线 |
| `get_info` | `path` | 统计 |
| `export_asset` / `export_obj` / `import` | `path` `destination` | 存取 |

**何时用 `mesh.*`**：只要一个纯 mesh 资产，不需要编辑操作。`probuilder.*` 的对象带 `ProBuilderMesh` 组件，编辑完还要 `ToMesh()`。

---

## 分件策略

### 命名前缀是唯一可靠的分组手段

工程里没有 Newtonsoft / SimpleJson，不能靠 JSON 存部件归属。**用前缀**：

```js
const P = model.PART_PREFIX;   // 'PBM_'
await model.box(P + 'Body',  {...});
await model.box(P + 'Lid',   {...});
await model.mergeParts({ name: 'MyModel' });   // 自动只合并 PBM_*
```

合并后前缀消失（成为单 mesh），所以**合并前必须记下想保留的分组信息**（比如要做多材质的分组范围）。

### 尺寸与位置约定

- 单位是**米**，`size` 是几何实际尺寸（顶点会被缩放，不是 `transform.scale`）
- `position` 是**几何中心**（ProBuilder 的 pivot 在中心）
- 想"底面贴地"：`position.y = size.y / 2`

```js
// 一个 1m 高的柱子立在地面
await model.box('PBM_Post', { size: [0.1, 1.0, 0.1], position: [0, 0.5, 0] });
```

### 分件 vs 单体的取舍

**多分件再合并**（推荐）：
- 每件可以用不同形状，拼复杂形体很容易
- 合并后 `uv_atlas` 能自动给每个连通件分一格
- 代价：内部面会保留（看不见但占面数）。需要的话用 `mesh.optimize` 或删面

**单体 + 挤出**：
- 面数省，但要算 `faceIndices`，且面索引会随编辑变化
- 适合规则形状（墙、地板、盒子）

---

## 单 mesh 的两种交付

### A. 单 submesh（一张贴图）

合并后 `uv_atlas` 直接上图集，一个材质走天下。见 `examples/wooden_crate.js`。

### B. 多 submesh（多材质）

`mergeObjects` 会把所有面塞进 submesh 0，材质归属丢失。要恢复必须按几何特征重新分组：

```js
await model.splitSubmeshes({
  target: 'MyObject',
  groups: [
    { name: 'ground', yMin: -99, yMax: 0.05 },   // 世界坐标范围
    { name: 'walls',  yMin: 0.05, yMax: 99 },
  ],
  materialPaths: ['Assets/.../M_Ground.mat', 'Assets/.../M_Wall.mat'],
});
```

分组依据是**三角形质心的世界坐标**落在哪个 AABB 里，先匹配到的胜出，都不匹配的进最后一组（并计数 `fallback`）。

**注意代价**：这一步之后对象脱离 ProBuilder，不能再挤出/倒角。几何编辑要在 split 之前做完。

### 校验

```js
const info = await model.info('MyObject');
model.assertSingleMesh(info, { subMeshes: 1 });   // 或 2、3
```

`assertSingleMesh` 检查：`subMeshCount` 是否符合、三角形数 > 0。
`verify.run()` 额外检查顶点冗余（顶点/三角形 > 8 说明有大量重复顶点，可 `mesh.optimize`）。

---

## 幂等与清理

### 为什么要 purge

脚本重复运行会在场景里**累积同名对象**。多个同名对象重叠时，后建的（无材质的白模）会盖住先建的（有材质的），视觉上像"材质完全没生效"。

```js
await model.purge(['Crate', 'PBM_*']);   // 支持精确名和前缀通配
```

### 其他可重跑要点

| 操作 | 非幂等表现 | 处理 |
|---|---|---|
| `material.create` | 已存在则报错 | `tex.material()` 已 try/catch 复用 |
| `mergeParts` | 累积对象 | 开头 purge |
| `uv_atlas` | 覆盖同名 .asset（按 GUID 保留引用，安全） | 无需处理 |
| `texgen.for_mesh` | 覆盖同名 PNG | `overwrite: true` 默认 |

---

## 常见错误对照

| 现象 | 原因 | 检查 |
|---|---|---|
| `mergeParts` 报"找到 0 个部件" | 分件没加 `PBM_` 前缀 | `model.listParts()` |
| 合并后模型位置偏移 | 合并对象继承第一个部件的 transform | 看 `info().boundsCenter` |
| `getMeshInfo` 报没有 ProBuilderMesh | 已被 `splitSubmeshes` 或手改 mesh | 用 `model.info()`（自动降级） |
| 模型显示纯白 | 渲染器是 `ProBuilderDefault` | 材质要在 atlas 之后挂 |
| 场景里多个同名对象 | 没 purge | `model.findObjects('^Name$')` 数一下 |
| 空心/破面 | 分件之间有穿插或布尔没做 | 用 `boolean` 而不是直接位置重叠 |

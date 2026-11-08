# 贴图参考（TexGen）

坐标语义先看 `semantics.md` 第 1~3 条。这里只讲怎么把图**画出来**。

## 一个图层长什么样

```jsonc
{
  "shape": "rect",           // 几何：circle/ellipse/ring/rect/roundRect/line/polygon/star
  "center": [512, 512],      // 画布坐标，y 向上
  "size": [1024, 1024],      // rect/roundRect 的宽高
  "cornerRadius": 24,        // roundRect 圆角
  "rotation": 0,             // 度，影响 linear 渐变方向
  "fill": "solid",           // solid/linear/radial/noise/ripple/grid/pattern
  "color": "#8a5a33",
  "blend": "normal"          // normal/multiply/screen/overlay/mask/erase
}
```

### 几何字段

| 字段 | 适用 shape | 说明 |
|---|---|---|
| `center` | 全部 | 中心点，**也可写 `at`** |
| `size` | rect / roundRect / ellipse | `[宽, 高]` |
| `radius` | circle / ring | 圆半径 |
| `innerRadius` | ring | 内半径，别名 `inner_radius` |
| `radiusX` / `radiusY` | ellipse | 别名 `radius_x` / `radius_y` |
| `thickness` | ring | 环宽（不填则由 radius/innerRadius 推出） |
| `from` / `to` | line | 端点 |
| `width` | line | 线宽 |
| `roundCap` | line | 圆头，别名 `round_cap`，默认 true |
| `points` | polygon / star | `[[x,y],...]` |
| `pointCount` | star | 角数，别名 `points_count` / `spikes`，3~64 |

### 填充

| `fill` | 关键字段 |
|---|---|
| `solid` | `color` |
| `linear` | `color` `color2` 或 `gradient`，`rotation` 控方向 |
| `radial` | `color` `color2`，从中心向外 |
| `noise` | `color` `color2` `scale` `seed` `octaves` |
| `ripple` | `color` `color2` `scale` |
| `grid` | `color` `color2` `cell` `gap` |
| `pattern` | `pattern` `cell` `gap` `gapColor` `color` `color2` `variation` `stagger` `seed` |

`pattern` 可选值：`brick` `plank` `tile` `checker` `stripe` `hex` `scale` `cobble`（另有若干有机图案，见 `texgen.templates`）

---

## 渐变：三种写法，第四种会静默失效

```jsonc
// 1. 对象数组（推荐，可读性最好）
"gradient": [{ "offset": 0, "color": "#0a0a1e" }, { "offset": 0.55, "color": "#ff3366" }, { "offset": 1, "color": "#ffe0f0" }]

// 2. 二元组数组
"gradient": [[0, "#0a0a1e"], [0.55, "#ff3366"], [1, "#ffe0f0"]]

// 3. 偏移为键的映射
"gradient": { "0": "#0a0a1e", "0.55": "#ff3366", "1": "#ffe0f0" }
```

`offset` 别名 `t`，范围 0..1。

**不要写 `"gradient": ["#000", "#fff"]`** —— 不报错，整层变成纯白，是实测确认的静默失效。

### 竖直渐变的正确姿势

`linear` 的投影长度**永远取 `size.x`**，方向由 `rotation` 决定。

```jsonc
// 要竖直渐变：rotation 90 + size 接近正方形
{ "shape": "rect", "center": [512, 512], "size": [1024, 1024],
  "fill": "linear", "rotation": 90, "gradient": [[0,"#000"],[1,"#fff"]] }
```

若 `size` 写成 `[2048, 1024]`（宽大于高），渐变会沿宽方向铺开，竖直方向只用到中间一段 —— 粉色区域"消失"就是这个原因。

---

## 按 UV 分区贴图（建模主力用法）

```js
// 单 mesh + 单图集：每个图集格画一块
const atlas = await model.atlas({ gameObject: 'Crate', columns: 4, meshPath: 'Assets/.../Mesh.asset' });
const layers = atlas.cells.map(c =>
  tex.atlasRectToLayer(c, 1024, 1024, { shape: 'rect', fill: 'solid', color: '#8a5a33' }));

await tex.forMesh({
  meshPath: 'Assets/.../Mesh.asset',
  layers,                    // 全局：所有分区共用一张画布（只渲染一次）
  width: 1024, height: 1024,
  groupBy: 'island',         // 或 'submesh'
  outputPath: 'Assets/.../T_Atlas.png',
});
```

### 分区覆盖

某个分区要用完全不同的图案时：

```js
await tex.forMesh({
  meshPath,
  layers: baseLayers,                        // 其余分区用这个
  regions: [{ id: 3, layers: specialLayers }],  // id 来自 atlas.cells[].id
  groupBy: 'island',
});
```

**每个被覆盖的分区都会单独渲染一张整幅画布**，然后只取它的 UV 部分。所以 region 的 layer 坐标仍是整图坐标，不是"相对于这一格"。

### `groupBy` 的选择

| 值 | 分区单位 | 适合 |
|---|---|---|
| `submesh`（默认） | 每个 submesh 一区 | 多材质模型 |
| `island` | 每个 UV 连通岛一区 | 图集化后的单 mesh（**建模默认用这个**） |

---

## 画之前先看一眼 UV

```js
const uvi = await tex.uvInfo('Assets/.../Mesh.asset', { width: 1024, height: 1024, groupBy: 'island' });
// 关注 coverage_ratio（覆盖率）与 overlap（跨岛重叠像素）
```

覆盖率低于 0.3 通常意味着：UV 没做图集、或者 `uv_atlas` 之后 mesh 又被别的操作覆盖了。

---

## PBR：一套材质，一次派生

```js
await tex.pbr({
  layers: [...],                    // 和 render 一样
  width: 1024, height: 1024,
  outputDir: 'Assets/Textures', prefix: 'Rock',
  tiling: true,                     // 无缝
  normalStrength: 5,                // 法线强度，默认 5
  metallic: 0,
  supersample: 2,
});
// → Rock_BaseColor.png / Rock_Height.png / Rock_Normal.png
//   Rock_Roughness.png / Rock_AO.png / Rock_ORM.png
```

派生规则：`height = clamp01(lum × alpha)`、`roughness = clamp01(bias + (0.5-height) × contrast)`。
即**透明 = 凹陷、越暗越粗糙**。

产出的法线实测：平均长度 `1.000`、范围 `[0.993, 1.005]`、蓝通道无像素 < 100 —— 合格。

**不要自己写高度场求梯度。** 原生实现同源对齐，手写的做不到（还慢）。

---

## 无缝平铺

```js
await tex.render({ ..., tiling: true, wrapMode: 'Repeat', filterMode: 'Bilinear' });
```

- `tiling: true` 让周期填充在边界环绕
- `wrapMode: 'Repeat'` 写进 `.meta`，材质才能真正平铺
- **不能让随机抖动跨边界连续**：cobble 每格颜色各不相同，接缝两侧本就是两块不同石头，有色差是正常的

### 怎么判断真的有缝

**看 2×2 拼贴，不要看数值。**

```bash
node scripts/inspect.js path/to/Tex.png --tile 2
```

实测：砖墙贴图"边缘差异/内部相邻差异"比值 **5.69**，但 2×2 拼贴完全无缝。试了 `variation` 0/0.25 × `stagger` 0/0.5 四组，比值全在 5.57~5.77 —— 与接缝无关，是错缝砖墙首末行内容天然不同。

比值只对**连续纹理**（cobble、噪声）有效：`tiling` 开关会让它从 1.79 降到 1.24。

---

## 贴图体检

```bash
node scripts/inspect.js T_Atlas.png              # 尺寸/通道/梯度/接缝
node scripts/inspect.js T_Normal.png --normal    # 法线合理性
node scripts/inspect.js T_Atlas.png --crop 0,0,256,256   # 1:1 裁局部
node scripts/inspect.js T_Tile.png --tile 2      # 2×2 拼贴
node scripts/inspect.js A.png --ref B.png        # 两张图差异
```

**判断观感必须裁 1:1 原始像素**。缩略图按面积平均会把细节糊成灰片。

---

## 排错

| 现象 | 原因 | 处理 |
|---|---|---|
| 整层纯白 | `gradient` 写成纯颜色数组 | 改对象数组 |
| 渐变只有一段 | `size.x` 决定的投影长度 | 改 `rotation` + 正方形 size |
| 贴图上下颠倒 | 多写了一次 `1 - v` | 画布 y 就是 v，不翻 |
| 只有部分格子有内容 | `cells` 与 `layers` 数量不匹配 | 检查 `atlas.cells.length` |
| 整张图空 | `layers` 坐标落在画布外 | 打印 `layers[0].center` 对照 `width/height` |
| 平铺有硬缝 | 周期填充没环绕 | `tiling: true` + `wrap_mode: Repeat` |
| 接缝数值大但看着无缝 | 结构化图案的数值误报 | `--tile 2` 看图 |
| 法线一片平 | 高度场没有起伏（常见于写入索引非整数被丢弃） | 用 `pbr` 而不是自己算 |

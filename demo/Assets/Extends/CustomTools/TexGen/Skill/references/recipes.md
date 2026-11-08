# 贴图配方

> 工程里已有 **35 个现成模板**（`TexGen/Templates/*.json`），覆盖面远比从零写新配方划算。
> 本文件的定位是：**先查模板，再按需改造**，以及几条实测过的关键参数。

---

## 0. 先查模板，别从零写

```bash
node -e "..."   # 或在对话里直接调用工具
```

工具 `texgen.templates` 列出全部模板；`texgen.templates` 带名字能取回完整 spec。
模板文件本身在 `TexGen/Templates/` 下，可直接读 JSON 当起点。

现有模板分组：

| 类别 | 模板 |
|---|---|
| 砌体/石材 | `brick_wall` `stone_wall` `cobblestone_path` `marble_stone` `rock_cliff` `plaster_wall` `ceramic_tile` `hex_panel` |
| 木材/织物 | `wood_grain` `wood_planks` `denim` `linen_weave` `fish_scale` |
| 金属 | `brushed_metal` `hammered_copper` `rusty_iron` |
| 地面 | `dirt_ground` `gravel_path` `grass_field` `sand_dunes` `snow_field` |
| 特效/天空 | `cloud_layer` `lava_flow` `water_surface` `starfield` `star_burst` `magic_orb` |
| UI | `ui_button` `ui_panel` `crosshair` `progress_fill` `skill_icon` |
| 基础 | `noise_height`（法线输入） |

改造模板的标准动作：换 `color` / `gapColor` 配色 → 调 `cell` 改尺度 →
调 `seed` 换具体形态 → 需要平铺就加顶层 `tiling: true`。

---

## 1. 竖向渐变（实测通过）

```json
{
  "width": 512, "height": 512, "background": "#00000000", "origin": "topLeft",
  "layers": [{
    "shape": "rect", "center": [256, 256], "size": [512, 512],
    "fill": "linear", "rotation": 90,
    "gradient": [{ "offset": 0, "color": "#2B5FA8" },
                 { "offset": 1, "color": "#FFFFFF" }]
  }]
}
```

三条必须同时成立，缺一个就出错：

1. `rotation: 90` —— 默认 0 是**从左到右**，不是从上到下。
2. `size` 的**两个分量相等**（或 x 分量等于期望的渐变长度）—— 渐变长度只取 `size.x`。
3. `gradient` 用**对象数组或二元组** —— 纯颜色数组会静默失效，整层变纯白。

实测验证：这样渲染后，PNG 顶部是 `#FFFFFF`、底部是 `#2B5FA8`。

---

## 2. 无缝平铺图案（实测通过）

```json
{
  "width": 256, "height": 256, "background": "#333333", "origin": "topLeft",
  "tiling": true,
  "layers": [{
    "shape": "rect", "center": [128, 128], "size": [256, 256],
    "fill": "pattern", "pattern": "cobble",
    "cell": 64, "gap": 5, "gapColor": "#222222",
    "color": "#8a7f6d", "variation": 0.3, "seed": 7
  }]
}
```

要点：

- 图案层必须**铺满整张画布**，且 `cell` 要能整除画布尺寸（256 / 64 = 4），否则边界切在半格上。
- 「`cell` = 砖块宽度」；`brick` 的行高是 `cell / 2`（2:1 砖），所以 256 画布 + `cell: 64` = 4 列 × 8 行。
- `tiling: true` 让周期填充在边界环绕。**它不能让随机抖动跨边界连续** ——
  cobble 每格颜色各不相同，接缝两侧本来就是两块不同的石头，有色差是正常的。
- 材质要真正平铺，还要 `wrap_mode: "Repeat"`（写进 `.meta`）。

### 判断有没有缝：看拼贴图，不要看数值

```bash
node inspect.js wall.png --tile 2      # 生成 wall_tiled2.png
```

实测对照：

| 纹理 | 接缝比值 | 2×2 拼贴实际观感 |
|---|---|---|
| cobble `tiling:false` | 1.79 | 有缝 |
| cobble `tiling:true` | 1.24 | 无缝 |
| **brick `tiling:true`** | **5.69** | **完全无缝** |

砖墙这一行说明：**结构化图案的接缝比值不可信**。错缝砌法下首行与末行画的就是不同的砖，
像素差天然很大，与「有没有缝」无关。试过 `variation` 0/0.25 与 `stagger` 0/0.5 四种组合，
比值都在 5.6 左右，进一步证明它与接缝无关。

结论：`cobble` / `noise` / 石材这类**连续型**纹理看比值；`brick` / `plank` / `tile` / `hex`
这类**结构化图案**一律用 `--tile` 生成拼贴图，直接看。

---

## 3. 法线与 PBR：用内置，别手写

### 只要法线

```json
{ "width": 512, "height": 512, "normal_map": true, "normal_strength": 4 }
```

layers 画成**灰度高度**（亮 = 凸起），`normal_map` 会把它转成切线空间法线。
注意：此时 layers 的颜色不再作为 albedo 输出，别在这里画彩色图案。

已有 PNG 想转法线，用 `texgen.normal_from_image`：

```
image_path, output_path, normal_strength（默认 4）
```

### 整套 PBR

`texgen.pbr` 一次渲染派生 6 张：BaseColor / Normal / Height / Roughness / AO / ORM。

| 参数 | 默认 | 作用 |
|---|---|---|
| `normal_strength` | 4 | 凹凸强度 |
| `roughness_bias` | 0.55 | 整体粗糙度基线 |
| `roughness_contrast` | 0.6 | 明暗对粗糙度的影响幅度 |
| `ao_strength` | 0.7 | 环境光遮蔽强度 |
| `ao_radius` | 6 | 遮蔽半径（1..64） |
| `metallic` | 0 | 金属度（整张图统一值） |
| `output_path` | — | 只取文件名作前缀，其余放同目录 |
| `material_path` | — | 给了就直接建/改材质 |

内部逻辑（源码 `ActionPbr`）：

```
height    = clamp01(luminance(rgb) * alpha)        // 透明区域算低
roughness = clamp01(bias + (0.5 - height) * contrast)
```

所以**画高度时透明 = 凹陷**，且「越暗越粗糙」。想要的观感不对，先调 `roughness_bias`
（整体更粗糙/更光滑）再调 `roughness_contrast`（明暗差异带来的粗糙度差）。

**别用 `render` 连调两次拿 albedo + normal**：两次独立渲染，噪声相位和抖动可能不一致，
法线和颜色会对不上。`pbr` 是同一次渲染派生，天然对齐。

---

## 4. 给模型贴图：先看 UV 再画

```
texgen.uv_info  { mesh_path, width, height, group_by }   # 先看
texgen.for_mesh { mesh_path, regions, group_by, padding, ... }  # 再画
```

`uv_info` 返回每个 region 的 `PxX / PxY / PxW / PxH`（画布像素矩形）和覆盖率。
`group_by`：`submesh`（按子网格）或 `island`（按 UV 岛）。

`for_mesh` 的两种用法：

- **全局 layers**：所有 region 用同一套画法，适合重复纹理
- **`regions: [{id, layers}]`**：按 region 覆盖，适合每个部件不同内容

`padding` 是接缝外扩像素（默认 4），防止 mipmap 采样到邻区颜色。

关键：`for_mesh` 把 UV 的 `v` 直接映射成画布 `y`（`PxY = v * height`，源码 `TexGenMeshUv.cs:181`），
**与 `render` 的坐标约定一致，不需要翻转**。画完后用 `inspect.js` 检查，别靠肉眼猜方向。

---

## 5. 批量出图

```
texgen.batch { specs: [ {...}, {...} ], output_dir, prefix, width, height, ... }
```

`specs` 里每项是独立 spec（可含自己的 `layers` / `width` / `background`）；
顶层给的 `width`/`height`/`origin`/`supersample` 作为公共默认值。

比循环调 `render` 快，且失败时能整批报告。

---

## 6. 出图前写死这些，能省掉一轮返工

| 项 | 为什么 |
|---|---|
| `origin` | 决定坐标系原点，中途改会让所有 layer 位移 |
| `tiling` / `wrap_mode` | 后补要重渲染，且 `wrap_mode` 还写进了 `.meta` |
| `supersample` | 细线（网格、缝）在 1 下会断续，2 起才稳 |
| `filter_mode` | 像素风用 `Point`，写实用 `Trilinear` |

## 7. 出图后必跑

```bash
node inspect.js <png>                 # 纯色 / 过曝 / 接缝
node inspect.js <png> --normal        # 法线是否真的有效
node inspect.js <png> --crop 0,0,256,256   # 1:1 看细节，别看缩略图
```

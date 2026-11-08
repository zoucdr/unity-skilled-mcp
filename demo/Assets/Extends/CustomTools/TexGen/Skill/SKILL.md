---
name: texgen-texture
description: 用 unity3d-skilled-mcp 的 texgen 工具制作贴图与材质。当任务涉及程序化贴图、albedo/法线/PBR 贴图、无缝平铺纹理、给 mesh 按 UV 分区绘制贴图，或需要检查贴图是否过曝/平掉/接缝明显、判断法线图是否真的生效时，使用本技能。触发词：贴图、纹理、材质、法线贴图、normal map、PBR、roughness、AO、ORM、无缝、平铺、tiling、UV、texgen、石墙、木纹、金属、砖墙。
agent_created: true
---

# texgen 贴图制作

texgen 能把一份「图层指令表（JSON）」渲染成 PNG，并可选转为法线图、派生整套 PBR 贴图。
本技能提供**实测过的坐标语义**、**参数红线**和一批**通用处理脚本**，避免在方向、字段拼写、
静默失效这类非能力问题上反复返工。

---

## 开工顺序

1. **确认标定仍有效**（1 秒，纯本地）
   ```bash
   node scripts/check.js
   ```
   若提示缺少 `references/calibration.json`，先跑 `node scripts/calibrate.js`（会连 Unity 画两张标定图）。

2. **先查现成模板**，别从零写 spec
   ```bash
   ls TexGen/Templates/          # 35 个模板
   ```
   工具侧：`texgen.templates`（列出）、`texgen.templates {name}`（取完整 spec）、
   `texgen.examples`（内置示例）、`texgen.spec`（参数全貌）。

3. **画，然后立刻体检**，不要凭肉眼在缩略图上判断
   ```bash
   node scripts/inspect.js out.png [--normal] [--crop 0,0,256,256]
   ```

---

## 铁律一：画布 y 向上

`TexGenModel.cs:103` 的注释写着 `Y always grows downward`，**这句是错的**。
`scripts/calibrate.js` 的实测结果：

| layer 坐标 | 导出 PNG 里的质心 |
|---|---|
| `center: [128, 128]`（512 画布） | `(127.5, **383.5**)` → 文件下半部 |
| `center: [384, 384]` | `(383.5, **127.5**)` → 文件上半部 |

因此：

- 画布 `y` 向上，`y=0` 在底边（对应 Unity `SetPixels` + `EncodeToPNG`）
- `origin` **只平移原点，不翻转**：`topLeft` → 原点 `(0,0)`；`center` → 原点 `(W/2,H/2)`
- **UV 的 `v` 也向上 → `y = v * H`，不要写 `1 - v`**
- 只有读解码后的 PNG 像素行时才需要 `1 - v`

```js
const UV = require('./scripts/uvspace');
const r = UV.rectUV(0.0, 0.5, 1.0, 1.0, 1024, 1024);
UV.layerRect(r, { fill: 'solid', color: '#FF0000' });
// -> { center: [512, 768], size: [1024, 512], fill: 'solid', color: '#FF0000' }
```

`origin: "center"` 时用 `UV.forOrigin(r, 'center', W, H)` 转换（只做平移）。

> 历史教训：曾把画布坐标误当文件行号，在不该翻的地方翻了，贴图上下颠倒，
> 靠反复试错凑对，还把原因错误归为「三个入口三套语义」。实际只有一套。

---

## 铁律二：这些是内置能力，别手写

| 需求 | 用 | 不要 |
|---|---|---|
| 法线贴图 | `normal_map: true` + `normal_strength`，或 `normal_from_image` | 自己算高度场求梯度 |
| 整套 PBR | `texgen.pbr`（一次出 BaseColor/Normal/Height/Roughness/AO/ORM） | 分别 render 五次再对齐 |
| 无缝平铺 | 顶层 `tiling: true` + `wrap_mode: "Repeat"` | 手动镜像拼接 |
| 批量出图 | `texgen.batch` | 循环调 render |
| 按 mesh UV 分区 | `uv_info` 先看，再 `for_mesh` | 自己算 UV 包围盒 |

`pbr` 内部逻辑：`height = clamp01(luminance(rgb) * alpha)`、`roughness = clamp01(bias + (0.5-height)*contrast)`。
即**透明 = 凹陷**，**越暗越粗糙**。观感不对先调 `roughness_bias`，再调 `roughness_contrast`。

---

## 铁律三：静默失效清单

这些写法**不报错**，只是结果不对，最难排查：

| 写法 | 后果 | 检查方法 |
|---|---|---|
| `gradient: ["#000","#FFF"]`（纯颜色数组） | 渐变被丢弃 → 整层纯白 | 看是否纯色 |
| `shape: "roundrectt"` 等拼错 | 回退默认值（如 `circle`） | 对照 `references/layer-reference.md` |
| `center: "128,128"` 字符串 | 被当 `[0,0]` | `tx.js` 会拦住 |
| `center: [128]` 单分量 | 用默认值 | `tx.js` 会拦住 |
| `linear` 渐变不写 `rotation: 90` | 变成横向渐变 | 抽样上下/左右像素 |
| `linear` 渐变 `size.x` 与画布高不等 | 只显示渐变中段 | 同上 |
| 尺寸超过 2048、layers 超过 512 | 工具报错 | `tx.js` 预检 |

`scripts/tx.js` 在发请求前就拦住尺寸、坐标格式、`layers_json` 语法问题，
枚举拼写和渐变写法还得自己按表核对。

---

## 工具速查

| 工具 | 用途 | 关键参数 |
|---|---|---|
| `render` | 指令表 → PNG | `layers` `width` `height` `origin` `tiling` `normal_map` `wrap_mode` |
| `for_mesh` | 按 mesh UV 分区绘制 | `mesh_path` `regions` `group_by` `padding` `fill_mode` |
| `uv_info` | 绘制前查看 UV 布局 | `mesh_path` `group_by` `width` `height` |
| `batch` | 多 spec 批量出图 | `specs` `output_dir` `prefix` |
| `pbr` | 一次派生整套 PBR | `normal_strength` `roughness_bias` `roughness_contrast` `ao_*` `metallic` |
| `normal_from_image` | 现有 PNG → 法线 | `image_path` `output_path` `normal_strength` |
| `preview` | 预览窗口 | `layers` ... |
| `validate` | 只校验不渲染 | `layers` |
| `templates` / `examples` / `spec` | 查现成内容与参数全貌 | — |

`group_by`：`submesh`（按子网格）或 `island`（按 UV 岛）。

---

## 脚本

| 脚本 | 作用 |
|---|---|
| `scripts/calibrate.js` | 画标定图、解码判定 y 轴方向与 origin 行为，写 `references/calibration.json` |
| `scripts/check.js` | 本地自检（坐标/Field/PNG/预检/指标），改动脚本后先跑 |
| `scripts/inspect.js` | 贴图体检：纯色、过曝、梯度能量、接缝比值、法线合理性、1:1 裁剪 |
| `scripts/tx.js` | MCP 调用封装 + 参数预检 + 错误规范化 |
| `scripts/uvspace.js` | UV ↔ 画布/图像 坐标换算（唯一真值） |
| `scripts/field.js` | 安全标量场，浮点索引等静默失败直接抛错 |
| `scripts/png.js` | 完整 PNG 编解码（filter 0-4），不支持的格式抛错 |

`inspect.js` 的接缝判据是**相对值**：`边缘差异 / 内部相邻差异`。
但它的适用范围有限：

- **连续型纹理**（cobble、noise、石材、砂土）看这个比值有效，贴近平滑 = 无缝。
- **结构化图案**（brick、plank、tile、hex）看比值会误报。错缝砌法下首行与末行画的
  本就是不同的砖，像素差天然很大。实测砖墙比值 5.69，而 2×2 拼贴**完全无缝**。

所以判断平铺一律以拼贴图为准：

```bash
node inspect.js wall.png --tile 2        # 生成 wall_tiled2.png，打开看
```

---

## 参考

| 文件 | 内容 |
|---|---|
| `references/semantics.md` | 坐标语义契约、源码注释与实测的矛盾、参数红线、交付前检查 |
| `references/layer-reference.md` | layer 全部字段、枚举取值、别名、常见拼错对照 |
| `references/recipes.md` | 模板清单、竖向渐变/无缝平铺的实测配方、PBR 与 for_mesh 工作流 |
| `references/calibration.json` | 标定实测数据（机器可读，`check.js` 会校验） |

## 收工前

```bash
node scripts/inspect.js <产物>.png --normal      # 若是法线图
node scripts/inspect.js <产物>.png               # 普通图
```

确认 `.meta` 两件事：法线图 `textureType = NormalMap` + `sRGB = false`；
平铺材质 `wrap_mode = Repeat`。

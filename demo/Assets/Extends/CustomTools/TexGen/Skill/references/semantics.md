# texgen 语义契约

> **本文件的每一条都经过实测或源码核对。**
> 凡是「注释说法 ≠ 实际行为」的地方，一律以实测为准，并标注证据。
> 配套脚本在 `scripts/`，能自动验证的部分不要靠记忆。

---

## 1. 最重要的一条：y 轴方向

源码 `TexGenModel.cs` 第 103 行的注释写着：

```csharp
/// <summary>Canvas coordinate origin. Y always grows downward.</summary>
public enum TgOrigin { TopLeft, Center }
```

**这个注释是错的。**

实测（`scripts/calibrate.js`，512×512 画布，`origin: "topLeft"`）：

| layer 坐标 | 导出 PNG 中的质心 | 说明 |
|---|---|---|
| 红块 `center: [128, 128]` | `x=127.5, y=383.5` | 落在**文件下半部** |
| 蓝块 `center: [384, 384]` | `x=383.5, y=127.5` | 落在**文件上半部** |

结论：

- **画布 y 向上**，`y = 0` 在画布**底边**，与 Unity 的 `SetPixels` 约定一致（源码用 `tex.SetPixels()` + `EncodeToPNG()`，见 `TexGenRasterizer.cs:64`）。
- `origin` **只平移原点，不翻转方向**。`center` 时原点在 `(W/2, H/2)`，实测绿块 `center:[0,0]` 的质心是 `(255.5, 255.5)`，验证通过。
- **UV 的 v 也向上，与画布同向**。所以 `v → 画布 y` 是 `y = v * H`，**不需要 `1-v`**。
- 需要 `1-v` 的只有一处：解码 PNG 后的**文件行号**（`row = (1-v) * H`）。

历史教训：此前把画布坐标误当作文件行号，于是在不该翻的地方翻了，导致贴图上下颠倒，靠反复试错才凑对，还错误地把原因归结为「三个入口三套语义」。

### 两个空间，别无其他

| 空间 | 含义 | 换算 |
|---|---|---|
| `CANVAS` | texgen layer 的 `center` / `size` / `from` / `to` | `y = v * H`（y 向上） |
| `IMAGE` | 解码 PNG 后的像素数组下标 | `row = (1-v) * H`（y 向下） |

```js
const UV = require('./uvspace');
const r = UV.rectUV(0.0, 0.5, 1.0, 1.0, 1024, 1024);   // 画布上半部分
r.cy        // 768  → 直接填进 layer.center[1]
r.h         // 512  → 直接填进 layer.size[1]
r.top       // 0    → 读 PNG 像素时用
UV.layerRect(r, { fill: 'solid', color: '#FF0000' })   // 展开成 layer 字段
```

`origin: "center"` 时用 `UV.forOrigin(r, 'center', W, H)` 转换，它只做平移。

---

## 2. 已经内置的能力（不要重复造轮子）

以下几件事 texgen 原生就有，手写纯属浪费：

| 需求 | 现成方案 | 不要做什么 |
|---|---|---|
| 法线贴图 | `normal_map: true` + `normal_strength`，或 `normal_from_image` | 自己算高度场求梯度 |
| 整套 PBR | `texgen.pbr` 一次出 BaseColor/Normal/Height/Roughness/AO/ORM | 分别渲染五张再对齐 |
| 无缝平铺 | 顶层 `tiling: true` | 手动镜像拼接 |
| 批量出图 | `texgen.batch`，`specs` 数组 + `output_dir` | 循环调 render |
| 按 mesh UV 分区绘制 | `for_mesh` + `regions` + `group_by` | 自己算 UV 包围盒 |
| 看 UV 布局 | `uv_info`，返回每个 region 的 `PxX/PxY/PxW/PxH` | 猜 UV 在哪 |

**`normal_map` 的用法有歧义，必须注意**：它把**渲染结果**当成高度场转成法线，
所以 layers 应该画成灰度高度的样子，而不是画彩色图案。若要颜色和法线都要，
用 `texgen.pbr`（一次渲染派生全套），别调两次 `render`。

---

## 3. 参数红线

| 项 | 限制 / 陷阱 |
|---|---|
| `width` / `height` | 1..**2048**（`TgSpec.MaxDimension`） |
| `layers` | ≤ **512** 条（`TgSpec.MaxLayers`） |
| `center` / `size` / `from` / `to` | 必须是 `[数值, 数值]`，写成字符串会被静默当 0 |
| 枚举值 | 大小写不敏感，且会去掉 `_` 和 `-`：`round_rect` = `roundRect` = `roundrect` |
| 未知枚举值 | **静默回退到默认值**（`GetEnum` 返回 fallback），拼错不报错 |
| `output_path` | 工程相对路径或绝对路径；父目录不存在会自动创建 |
| `color` | `#RRGGBB` 或 `#RRGGBBAA` |

「枚举拼错静默回退」是最隐蔽的坑：`shape: "roundrectt"` 不会报错，只会变成默认的 `circle`。
所以 `tx.js` 会预检坐标格式，但**枚举拼写需要自己核对**（见 `references/layers.md`）。

---

## 4. 图层混合

默认 `blend: "normal"`。两个特殊值不是普通混合模式：

- `blend: "mask"`（别名 `clip` / `stencil`）—— 只保留该形状**内部**的背景
- `blend: "erase"`（别名 `maskout` / `cutout` / `hole`）—— 挖掉该形状**内部**的背景

这两个对做镂空、开窗、做 alpha 遮罩很有用，但不能和透明度 `opacity` 混为一谈。

其余：`add` `multiply` `screen` `overlay` `subtract` `difference` `darken` `lighten`。

---

## 5. 分辨率与观感

- **必须裁 1:1 原始像素判断**。缩略图按面积平均会把细节糊平，石墙那次因此误判过。
  用 `node inspect.js <png> --crop x,y,w,h` 裁一块看。
- `supersample` 1..4，是内部 SSAA。2 通常够，纹理边缘硬（如网格线、细缝）时用 3-4。
- `filter_mode` 与 `wrap_mode` 在建贴图时就写进 `.meta`：平铺材质必须 `wrap_mode: "Repeat"`，
  否则边缘会 clamp 出条纹。

---

## 6. 交付前必查

```bash
node inspect.js out.png --normal          # 法线：看 xy 偏移、归一化、蓝通道
node inspect.js out.png                   # 普通图：看是否纯色、过曝、接缝
node inspect.js out.png --crop 0,0,256,256   # 1:1 局部
```

`inspect.js` 会直接给出警告，例如：

- 「整张图几乎是纯色」→ 高度场没写进去，或图层被完全遮挡
- 「法线 xy 几乎没有偏移」→ 表面是平的
- 「有 N% 像素蓝色通道 < 100」→ 法线被当颜色图处理了（`.meta` 里 sRGB 未关 / textureType 不是 NormalMap）
- 「左右边缘差异过大」→ 平铺会有缝

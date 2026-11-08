# layer 字段参考

> 整理自 `TexGenModel.cs` 的 `TgLayer` 与 `TexGenParser.cs` 的键名读取逻辑（含别名）。
> 枚举值大小写不敏感，`_` 与 `-` 会被忽略。**拼错不报错，会静默回退默认值。**

---

## 1. 几何

| 字段 | 类型 | 默认 | 适用 | 说明 |
|---|---|---|---|---|
| `shape` | enum | `circle` | — | `circle` `ellipse` `ring` `rect` `roundRect` `line` `polygon` `star` |
| `center` | `[x,y]` | `[0,0]` | 全部 | 画布像素坐标。**y 向上**，y 越大越靠 PNG 上方 |
| `size` | `[w,h]` | `[128,128]` | rect / roundRect | 像素宽高 |
| `radius` | number | `64` | circle / ring 中径 / star 外径 | |
| `innerRadius` | number | `0` | ring / star | 别名 `inner_radius` |
| `thickness` | number | `0` | ring | 环带宽；0 = 由内外径推导 |
| `radiusX` `radiusY` | number | `0` | ellipse | 0 则退化为 `radius` |
| `cornerRadius` | number | `0` | roundRect | 别名 `corner_radius` |
| `from` `to` | `[x,y]` | `[0,0]` | line | 线段两端 |
| `width` | number | `2` | line | 线宽 |
| `roundCap` | bool | `true` | line | 圆头端点 |
| `points` | `[[x,y],...]` | — | polygon | 顶点列表 |
| `points_count` | int | `5` | star | 3..64，别名 `spikes` |
| `rotation` | number | `0` | 全部 | 角度 |

`center` 的别名是 `at`。

---

## 2. 填充

`fill`：`solid` `linear` `radial` `noise` `ripple` `grid` `pattern`

| 字段 | 默认 | 适用 | 说明 |
|---|---|---|---|
| `color` | 白 | 全部 | 主色。别名 `centerColor` |
| `edgeColor` | 白 | 全部 | 边缘色。别名 `color2` / `toColor` |
| `midColor` | — | linear / radial | 中间色，设了才算三段 |
| `gradient` | — | linear / radial | 自定义多段渐变（见下） |
| `noise` | `fbm` | noise | `value` `perlin` `fbm` `worley` `ridged` |
| `scale` | `48` | noise | 噪声格大小，像素 |
| `octaves` | `4` | noise | 1..10 |
| `seed` | `1337` | noise / pattern | 换种子换纹理实例 |
| `frequency` | `6` | ripple | 每 100px 的波数 |
| `phase` | `0` | ripple | 相位（turns） |
| `amplitude` | `1` | ripple | 对比度 |
| `cell` | `[32,32]` | grid / pattern | `grid` 是网格尺寸；`pattern` 是**图案周期**（单个数字等价于正方形） |
| `lineWidth` | `2` | grid | 网格线宽；pattern 下作为 `gap` 的默认值 |

### 渐变对象

键名 `gradient`，别名 `stops`。**实测有效的只有下面三种**：

```json
// 1. 对象数组（最推荐）
"gradient": [{ "offset": 0, "color": "#0000FF" },
             { "offset": 1, "color": "#FFFFFF" }]

// 2. 二元组数组
"gradient": [[0, "#0000FF"], [1, "#FFFFFF"]]

// 3. 偏移为键的映射
"gradient": { "0": "#0000FF", "1": "#FFFFFF" }
```

`offset` 别名 `t`，范围 0..1。

**⚠ 不要写「只有颜色、偏移自动均分」的数组**：

```json
"gradient": ["#0000FF", "#FFFFFF"]     // ← 静默失效
```

解析器只处理对象和二元组元素，纯字符串元素会被**直接丢弃**，渐变点数变成 0，
最终整层退化成 `color`（默认白）。实测：这样写出来是一张纯白图，不报任何错。
遇到「整层纯色、渐变像没画」，先检查是不是这个写法。

**`linear` 渐变的两个陷阱**（源码 `TexGenRasterizer.cs:398`，实测踩过）：

```csharp
dir = (cos(rotation), sin(rotation));
len = layer.Size.x;                    // ← 永远取 size.x
t   = dot(p - center, dir) / len + 0.5;
```

1. 方向由 `rotation` 决定，**`rotation: 0` 是从左到右**，不是从上到下。
   要竖直渐变必须显式写 `"rotation": 90`。
2. 渐变总长度**永远等于 `size.x`**，跟 `size.y` 无关。
   即 rotate 90 之后，`size` 要写成 `[长度, 长度]` 才铺满；
   若写 `[2048, 1024]` 再旋转，实际渐变长度是 2048，比画布高度还长，只能看到中段。

```json
{ "shape": "rect", "center": [512, 512], "size": [1024, 1024],
  "fill": "linear", "rotation": 90,
  "gradient": ["#2B5FA8", "#7FC1E8", "#FFFFFF"] }
```

`radial` 的半径取 `radius`（未设则 64），与 `size` 无关。

### 2.1 pattern 专属

`pattern`：`brick` `plank` `tile` `checker` `stripe` `hex` `scale` `weave` `dot` `cobble`

| 字段 | 默认 | 别名 | 说明 |
|---|---|---|---|
| `gap` | `lineWidth` | `patternGap` `mortar` | 缝宽，像素 |
| `gapColor` | 透明 | `mortarColor` | 缝内颜色 |
| `stagger` | `0.5` | `offset` | 行错位比例；0.5 = 错缝砌法 |
| `variation` | `0.12` | `jitter` | 每格亮度抖动，避免"复制粘贴感" |
| `roundness` | `1` | — | 0..1，cobble / dot / scale 的圆角 |

所有 pattern 以 `cell` 为周期重复，配合顶层 `tiling: true` 即可无缝。

---

## 3. 域扭曲（把直线变成纹理）

```json
"warp": { "enabled": true, "noise": "fbm", "scale": 64, "amount": 12, "octaves": 4 }
```

也可写成 `"warp": true` 加同级 `warpScale` / `warpAmount` / `warpOctaves`。

扭曲只改变**填充取样位置**，不改变几何轮廓 —— 木纹、大理石纹路就靠它。

---

## 4. 合成

| 字段 | 默认 | 说明 |
|---|---|---|
| `blend` | `normal` | `normal` `add` `multiply` `screen` `overlay` `subtract` `difference` `darken` `lighten` `mask` `erase` |
| `opacity` | `1` | 0..1 |
| `feather` | `0` | 边缘柔化带宽，像素 |
| `blur` | `0` | 整层模糊半径，像素 |

`mask` 只保留形状内部；`erase` 挖掉形状内部。用于镂空与 alpha 遮罩。

## 5. 外发光

| 字段 | 默认 |
|---|---|
| `glow` (启用) | `false` |
| `glowRadius` | `12` |
| `glowColor` | 白 |
| `glowIntensity` | `1` |

---

## 6. 顶层 spec

| 字段 | 默认 | 说明 |
|---|---|---|
| `width` `height` | `256` | 1..2048 |
| `background` | 透明 | `#RRGGBBAA` |
| `origin` | `topLeft` | `topLeft`（原点左下）/ `center`（原点居中）。**y 始终向上** |
| `supersample` | `2` | 1..4 内部 SSAA |
| `tiling` | `false` | 别名 `tileable` / `seamless`。让周期填充在边界环绕 |
| `normalMap` | `false` | JSON 中写 `normal_map`：把渲染结果当高度场转法线 |
| `normalStrength` | `1` | JSON 中写 `normal_strength` |

---

## 7. 常见拼错对照

这些**不会报错**，只会静默用默认值，所以必须自查：

| 错误写法 | 实际生效 |
|---|---|
| `shape: "roundrectt"` | `circle` |
| `fill: "gredient"` | `solid` |
| `patternName: "cobble"` | 应写 `pattern` |
| `blend: "multipy"` | `normal` |
| `center: "128,128"` | `[0,0]`（`tx.js` 会拦住这一条） |
| `size: [128]` | 默认 `[128,128]`（`tx.js` 会拦住） |

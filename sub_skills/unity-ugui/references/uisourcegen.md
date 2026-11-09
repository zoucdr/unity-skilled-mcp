# 内置 UISourceGen 图资源生成与调整

> 2026-11-09 在 `E:/unity3d-skilled-mcp/demo`、Unity 2021.3.45f2c1、MCP 实例
> `3ddf80760bc14501851328a805139876` 实测。能力随工程变化；每次开工仍须先
> `find uisourcegen` / `capabilities.detect()`，目录返回空时不得假定工具存在。

## 为什么是首选

`uisourcegen.*` 是 Unity 工程内已注册的图资源工具，直接产出工程内 PNG，能处理：

- 控件图与多状态：`render` 一次生成 normal / hover / pressed / disabled；
- 九宫格元数据：`layout.json` 的 `slice`（仍需 `assets.importSprites()` 写入 `TextureImporter.spriteBorder`）；
- 整屏素材合成预览：`preview.png`；
- 自由图形调整：`draw` 支持渐变、圆角、路径、图标、文字和字体；
- 批量：`batch` / `draw_batch`；
- 字体发现：`fonts`；文字测量：`measure`；规格与模板：`spec` / `examples` / `templates` / `canvas_spec`。

**不要默认改用外部绘图库或仅从参考图裁切。** 外部绘图只应在内置工具缺失、
需要位图纹理保真，或用户明确指定时使用，并在 `pipeline_report.json` 明写降级。

## 当前服务实测目录

`find uisourcegen` 返回 13 个工具：
`render, batch, draw, draw_batch, canvas_spec, canvas_examples, spec, examples,
templates, fonts, measure, svg, pdf`。

实时 `schemas` 的关键参数：

| 工具 | 必需 | 常用可选 | 输出 |
|---|---|---|---|
| `uisourcegen.render` | `screen` 对象 | `output_dir, base_name, preview=true, supersample=2, select_asset=false` | 每状态 PNG、`preview.png`、`layout.json` |
| `uisourcegen.draw` | `canvas` 对象 | `output_dir, base_name, format=png, quality, name_suffix, supersample, select_asset` | 合成 PNG、`canvas.json` |
| `uisourcegen.draw_batch` | `canvases` 数组 | `output_dir, format, quality, supersample` | 多张合成图 |
| `uisourcegen.fonts` | — | `filter` | 字体族列表与默认字体路径 |

`uisourcegen.spec` 返回控件体系的 16 种 `type`、6 种状态和 `anchor=center`
中心点坐标说明。`canvas_spec` 返回自由画布的 17 种 shape、paint、编码格式和
元素坐标语义。`templates` 当前返回 `hud/login/mainmenu/settings/shop` 五份模板文本
（实测按 `tag` 子串筛选，返回的是 JSON 字符串，须解析后修改）。

## 生成：render

实测 `screen` 480×220，含 label / progressbar / button / badge，中文正常。
一次调用成功写入 9 个文件：

```
title_normal.png
health_normal.png
button_normal.png / button_hover.png / button_pressed.png / button_disabled.png
badge_normal.png
preview.png
layout.json
```

调用要点：

- `screen.controls[].rect` 在 `anchor=center` 时 `[x, y, w, h]` 的 `x,y` 是中心点；
  UI Spec 的 `[left, top, w, h]` 必须经 `coords.toUISourceGen()` 转换。
- `states` 可声明 hover / pressed 等；`render` 对 button 还会补 `disabled`。
- `style` 可设置 `background/background_end/corner_radius/font_size/text_color/font_weight`
  和 progressbar 的 `track_color/fill_color/fill_color_end`。
- `output_dir` 必须在 `Assets/AI-Generate/UGUI/<界面名>/` 下，遵守
  `ai-generate_save_path`：每组制作单独一个文件夹。
- `preview.png` 是控件素材合成图，**不是场景里 UGUI 的截图**。

实测输入存于当前会话工作区 `uisourcegen_smoke.json`；
生成目录 `Assets/AI-Generate/UGUI/UguiSkillSmoke/Generated/`。
目检结果：中文标题、进度条、按钮、星标徽章清晰完整，颜色与渐变符合输入。

## 调整：draw

自由画布适合修改单个控件外观、整块装饰图或中文标题。

实测 `canvas` 480×220，含线性渐变圆角背景、渐变圆、中文标题、圆角胶囊和中文按钮，
`font_family="Microsoft YaHei UI"`，`supersample=2`。输出
`Adjusted_tuned/Adjusted_tuned.png`（960×440 像素，因 2× 超采样）和 `canvas.json`。
目检中文无缺字，渐变和圆角正常。

要点：

- 元素坐标 `x,y,w,h` 是自由画布局部矩形，与 UI Spec 的控件 rect 不同；
  `circle/oval/arc` 把 `x,y` 当外框原点。
- `fill` 可为字符串颜色或 `{paint:"linear",color,color2}`；`space:"canvas"`
  可把渐变锚点改成画布坐标。
- `font_family` 由 `uisourcegen.fonts` 的可用族名确定；中文实测可用
  `Microsoft YaHei UI`，默认路径 `C:/Windows/Fonts/msyh.ttc`。
- `supersample>1` 会让 PNG 像素尺寸为逻辑尺寸乘以倍数；作为 Sprite 使用时
  以导入后的实际尺寸为准，不要误当布局漂移。
- `draw` 产的是一张合成图；需要交互控件 / 九宫格 / 多状态时仍用 `render`。

实测输入存于当前会话工作区 `uisourcegen_draw.json`。

## 与流水线的关系

1. S0：`capabilities.detect()` 记录 `uisourcegen.render` 是否注册；
2. S1：优先 `assets.regenerate()`（`render`），按 `layout.json` 读取 `slice` 与状态；
   只对 `asset=crop` 的位图节点用 `slice.js`；
3. `assets.importSprites()` 始终负责把 PNG 导入为 Sprite 并写 `spriteBorder`；
4. `layout.json` 中的 `image` 路径按输出目录重算，不信任文件内记录；
5. 场景搭建完成后另走 `preview.capture()` 生成真实 Canvas 的 `ugui_preview*.png`。

## 边界

- 工具目录返回 0 时，不能用 `code.execute` 模拟同名能力，也不能重放旧实例请求；
  重新 `discover` 后仍缺失才允许降级。
- `render` 只产素材，不创建 UGUI 控件、不绑定序列化引用、不写场景。
- `layout.json` 的 `slice` 只是元数据；九宫格必须落到 `TextureImporter.spriteBorder`。
- 生成结果返回 `success:true` 仍要核实 PNG 与 `layout.json` 真的存在。
- `templates` 的 `tag` 是文本子串筛选，不是结构化 tags 查询；不保证参数变化后仍等价。
- 素材预览与 UGUI 专用预览是两件事，不能互相替代。

# UI 参考（UISourceGen）

在建模流水线里，UISourceGen 的角色是**贴图的一个来源**：

1. **世界空间 UI**：`render()` 出整屏图，贴到一块面板 mesh 上
2. **图标 / 精灵**：`draw()` 或 `icon()` 出透明 PNG
3. **文字标牌**：`signboard()` 出带 alpha 的字

## 工具全表

| 动作 | 参数 | 用途 |
|---|---|---|
| `render` | `screen` `output_dir` `base_name` `preview` `supersample` | 渲染整个界面 |
| `batch` | `screens` | 批量渲染多屏 |
| `draw` | `canvas` `output_dir` `base_name` `format` `width` `height` | 通用画布（比 render 灵活，Skia 语义） |
| `draw_batch` | `canvases` | 批量画布 |
| `pdf` | `canvases` `output_dir` | 输出 PDF |
| `svg` | `path` `output_dir` `width` `height` `fill` `stroke` `padding` | SVG 栅格化 |
| `measure` | `text` `font_size` `font_path` `font_family` `bold` `letter_spacing` | 量文字尺寸 |
| `fonts` | `filter` | 列可用字体 |
| `templates` | `tag` `name` | 列模板 |
| `spec` | — | 语法说明（含控件类型、状态、图标名） |

## screen 结构

```jsonc
{
  "name": "HudPanel",
  "width": 1024,
  "height": 512,
  "background": "#00000000",        // 8 位 hex 支持 alpha；透明传 #00000000
  "background_end": "#080a0e",      // 渐变终点
  "background_gradient": false,
  "theme": {
    "primary": "#3399F5", "primary_end": "#1f66da",
    "surface": "#1c1d22", "surface_end": "#15161a",
    "on_surface": "#f2f3f7", "border": "#ffffff22",     "radius": 12,
    // font_path 可省略：signboard() 会自动挑一个系统中文字体（ui.resolveFont()）。
    // 需要指定时传绝对路径，或用 ui.fonts() 先列出可用字体名。
    "font_size": 24, "font_weight": "normal", "text_align": "middle_center"
  },
  "controls": [ /* ... */ ]
}
```

## control 结构

```jsonc
{
  "id": "btn_ok",
  "type": "button",            // 见下表
  "anchor": "center",          // center：rect 的 x,y 是**中心点**；topLeft：x,y 是左上角
  "rect": [512, 256, 200, 56], // [x, y, w, h]
  "text": "OK",
  "icon": "check",
  "value": 0.5,                // slider / progress
  "checked": false,            // toggle / checkbox / radio
  "selected": false,           // tab
  "items": ["A", "B"],         // dropdown / tab
  "style": {
    "background": "#3399F5", "background_end": "#1f66da",
    "corner_radius": 12, "border_color": "#ffffff33", "border_width": 1,
    "text_color": "#ffffff", "font_size": 30, "text_align": "middle_center",
    "shadow_color": "#00000066", "shadow_blur": 8, "shadow_y": 4
  },
  "state_styles": { "hover": { "background": "#48a8ff" } }
}
```

**`rect` 是四元组，`anchor` 决定 `x,y` 的含义** —— 两种混用是错位的主因。

### 控件类型

`Button` `Panel` `Card` `Slider` `Toggle` `ProgressBar` `Label` `Input` `Checkbox` `Radio` `Icon` `Divider` `Badge` `Tab` `Dropdown` `Scrollbar`

### 状态

`Normal` `Hover` `Pressed` `Disabled` `Selected` `Checked`

每个控件会把各状态**分别导出成一张 PNG**（`<id>_normal.png`、`<id>_hover.png`…），
另加 `preview.png`（整屏合成）和 `layout.json`（含九宫格元数据）。

### 内置图标

`check` `cross` `arrow` `chevron-down` `star` `menu` `play` `plus` `minus` `search` `gear` `heart`

---

## 便捷封装

### 文字标牌

```js
const U = require('./scripts/ui');
const r = await U.signboard({
  text: 'OPEN', width: 512, height: 256,
  outputDir: 'Assets/MyModel', baseName: 'Sign',
  surface: '#1c1d22', textColor: '#f2f3f7', radius: 16,
});
// → Assets/MyModel/Sign/preview.png（透明背景，圆角面板 + 居中文字）
```

实测输出：`control_count: 2`，`written: [plate_normal.png, label_normal.png, preview.png, layout.json]`。
见 `samples/ui_signboard.png`。

### 图标

```js
await U.icon('gear', { size: 128, color: '#f2f3f7', outputDir: 'Assets/Icons' });
```

### 整屏

```js
await U.render({
  name: 'Login', width: 960, height: 600,
  background: '#121319',
  theme: { primary: '#3399F5', surface: '#1c1d22', on_surface: '#f2f3f7', radius: 12 },
  controls: [
    { id: 'card',  type: 'card',   anchor: 'center', rect: [480, 300, 520, 440] },
    { id: 'title', type: 'label',  anchor: 'center', rect: [480, 130, 400, 40], text: 'Sign In',
      style: { text_color: '#ffffff', font_size: 30, text_align: 'middle_center' } },
    { id: 'user',  type: 'input',  anchor: 'center', rect: [480, 210, 420, 52], text: 'Username' },
    { id: 'go',    type: 'button', anchor: 'center', rect: [480, 360, 420, 56], text: 'Login',
      style: { background: '#3399F5', corner_radius: 12 } },
  ],
}, { outputDir: 'Assets/UI', baseName: 'Login' });
```

---

## 当贴图用

### 贴到面板上

```js
// 建一块和 UI 同比例的板
await model.box('PBM_Screen', { size: [2.0, 1.0, 0.04], position: [0, 1.2, 0] });
await model.mergeParts({ name: 'HudScreen' });
const atlas = await model.atlas({ gameObject: 'HudScreen', columns: 1,
  meshPath: 'Assets/UI/Hud_Mesh.asset' });

// 用 UI 预览图当贴图（整张铺满）
await tex.forMesh({
  meshPath: 'Assets/UI/Hud_Mesh.asset',
  layers: [{ shape: 'rect', center: [512, 256], size: [1024, 512],
             fill: 'image', image: 'Assets/UI/Login/preview.png' }],
  width: 1024, height: 512,
  outputPath: 'Assets/UI/T_Hud.png',
});
```

### 注意 alpha

透明背景的 PNG 用作 `_MainTex` 时，Standard shader 需要改渲染模式（Fade/Transparent）。
简单做法是给不透明内容垫一层底色（`background: '#1c1d22'`），避免半透明排序问题。

---

## 排错

| 现象 | 原因 | 处理 |
|---|---|---|
| 控件位置全偏 | `anchor` 与 `rect` 语义混用 | `anchor: 'center'` 时 x,y 是中心点 |
| 文字不显示 | 字体缺中文 | `signboard()` 会自动挑系统中文字体；手写 screen 时显式传 `theme.font_path`（用 `ui.resolveFont()` 取，勿写死盘符） |
| 全部输出空白 | `background` 全透明且控件也透明 | 换不透明底色先确认结构 |
| 输出路径奇怪 | `output_dir` 是**文件夹**，`base_name` 是子文件夹名 | 实际落到 `<output_dir>/<base_name>/` |
| `JSON.parse` 失败在 examples 输出 | **`uisourcegen.examples` 返回的示例 JSON 本身有语法错误**（label 的 style 缺闭合括号） | 当结构参考看，别直接喂给 render |

> **实测提醒**：`examples` 的返回值不能直接 `JSON.parse`。这是工具输出本身的 bug，不是你的问题。

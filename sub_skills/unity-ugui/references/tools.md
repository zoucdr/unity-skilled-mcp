# 工具契约（实测，勿凭注释猜名）

> 全部结论来自 2026-11-09 对源码与活 MCP 服务的核对。
> **工具名 = `[ToolModule(prefix)]` + `[ToolAction(action)]` → `prefix.action`**
> （`unity-package/Editor/ToolFramework/ToolCatalog.cs:205-209`）。

---

## 0. 一条最重要的结论：能力随工程变

| 工程 | 包内工具 | demo 扩展工具 |
|---|---|---|
| `E:\unity3d-skilled-mcp\demo`（Built-in，端口 8000） | `ugui.do_layout/get_layout/tattoo`、`ui.*` | `uisourcegen.*`、`ugui.generate/create_component/list_templates`、`ui_rule.*`、`figma.*` |
| `D:\WorkSpace\tdshoweditor`（URP，端口 8008） | `ugui.do_layout/get_layout/tattoo`、`ui.*` | **全部缺失**（`find uisourcegen` / `ui_rule` / `figma` 返回 0） |

原因：demo 的扩展都在 `Assets/Extends/CustomTools/` 下，属于该工程私有脚本；
`unity-package/Editor/Tools/UI/` 是包内代码，随包分发。

**因此技能启动必须先跑 `capabilities.detect()`**，按结果选路径，禁止假定工具存在。

---

## 1. 布局模块：`unity-package/Editor/Tools/UI/UGUILayoutTool.cs`

`[ToolModule("ugui")]`。三个动作，**只读写已有 RectTransform，不创建任何控件**。

| 工具 | 参数 | schema required |
|---|---|---|
| `ugui.do_layout` | `instance_id`, `path`, `anchored_pos`, `size_delta`, `anchor_min`, `anchor_max`, `pivot`, `sibling_index` | **`["path"]`** |
| `ugui.get_layout` | `instance_id`, `path` | **`["path"]`** |
| `ugui.tattoo` | `instance_id`, `path`, `tattoo_preset`, `tattoo_self` | **`["path"]`** |

活服务实测 `ugui.do_layout` 的向量参数：

```json
"anchored_pos": {
  "type": "array", "format": "vector3",
  "minItems": 3, "maxItems": 3,
  "items": { "type": "number" }
}
```

`size_delta` / `anchor_min` / `anchor_max` / `pivot` 同构。
**描述文字写 `[x, y]`，但 JSON Schema 要求 3 个数** → 一律传 `[x, y, 0]`。
`coords.toUGUI().doLayout` 已经统一输出 3 元向量。

`tattoo_preset` 枚举（12，实测 schema）：
```
top_left, top_center, top_right,
middle_left, middle_center, middle_right,
bottom_left, bottom_center, bottom_right,
stretch_horizontal, stretch_vertical, stretch
```
与 `UI Spec` 的 `anchor` 枚举**逐字对齐**，可直接透传。
`tattoo_self=false` 走 `ApplyAnchorPresetWithVisualPositionPreserved()`，
改锚点时保持视觉位置不变——这正是本技能二次校准锚点所需要的行为。

`path` 是层级路径，示例 `Canvas/Panel/Button`。技能生成的根名是 `UGUI_<name>`。

`ui.*`（`UiToolkitTool.cs`）：UXML/USS 文本编辑 16 个动作，属于 **UI Toolkit** 而非 UGUI，
与本链路平行，不要混用。

---

## 2. 创建模块：`demo/Assets/Extends/CustomTools/UGUI/Editor/`

`UguiToolModule.cs` 也用 `[ToolModule("ugui")]`，与布局模块共享前缀。

| 工具 | 声明参数 | required | 备注 |
|---|---|---|---|
| `ugui.list_templates` | 无 | 无 | 返回 `dashboard/login/settings/cardGrid/statPanel` |
| `ugui.generate` | `template` | `template` | 整屏模板，C# 硬编码 |
| `ugui.create_component` | `component`, `name` | 两者 | `component` 枚举 13 值 |

`component` 枚举：
`panel, image, text, button, kpi, barchart, ring, alert, progress, toggle, slider, input, card`

**参数被 schema 吞掉（实测 `ToolCatalog.cs:140-170`）**：
`[ToolAction("create_component", "component=require", "name=require")]` 只声明两个参数，
但 handler 读 `parent/x/y/w/h/label/value`。简写形式下 `Parameters` 是权威 schema，
所以 `CreateKeys()` 里定义的这些键**外部传不进去**，只能吃默认值
`x=100, y=100, w=200, h=60, value=0`。

**控件残缺（实测 `UguiToolkit.cs`）**：

| 控件 | 真实情况 |
|---|---|
| `Slider()` | 两张 `Image.Type.Filled` 假扮，**没有 `AddComponent<Slider>()`** |
| `InputField()` | 有组件，但 `textComponent` / `placeholder` 未赋值 |
| `Toggle()` | 有组件，但 `graphic` / `targetGraphic` 未绑 |
| `ScrollRect` / `Dropdown` / `RawImage` / `Mask` | **完全不存在** |

且 `UguiGenerator` 依赖静态 `Dictionary map`，`Init()` 会 Clear，
`parent` 只在「同一会话内已创建」有效，域重载即失效。

→ 本技能把它作为**降级路径**（`buildViaModule`），主路径走 `code.execute` 建真组件。

`UguiTemplateWindow.cs` 没有模板文件格式，只有 5 个字符串 + `[MenuItem]` 快捷入口；
与 UISourceGen 的 `Templates/*.json` 是两套完全不同的机制。

---

## 3. 图资源模块：`demo/Assets/Extends/CustomTools/UISourceGen/`

> **首选路径**：生成控件图和调整图形优先使用这一组内置工具。完整实测用法、
> `render` / `draw` 参数、中文与字体验证及边界见 `references/uisourcegen.md`。
> 仅在工具目录缺这组能力或需要位图纹理保真时才降级到 `slice.js` 裁切。

`[ToolModule("uisourcegen")]`，`UiGenTool.cs` + `UiGenTool.Canvas.cs` 是同一个 partial class。

### 体系一：控件图工厂（本技能 `asset=regenerate` 用）

| 工具 | 参数 | required |
|---|---|---|
| `uisourcegen.render` | `screen`, `output_dir`, `base_name`, `preview`, `supersample`, `select_asset` | `screen` |
| `uisourcegen.batch` | `screens`, `output_dir`, `preview`, `supersample` | `screens` |
| `uisourcegen.draw` | `canvas`, `output_dir`, `base_name`, `format`, `quality`, `name_suffix`, `supersample`, `select_asset` | `canvas` |
| `uisourcegen.draw_batch` | `canvases`, `output_dir`, `format`, `quality`, `supersample` | `canvases` |
| `uisourcegen.templates` | `tag`, `name` | 无 |
| `uisourcegen.fonts` / `measure` | `filter` / `text,font_size,font_path,font_family,bold,letter_spacing,text_scale_x,path` | 无 |
| `uisourcegen.spec` / `examples` / `canvas_spec` / `canvas_examples` | 无 | 无 |

默认值：`output_dir="Assets/UISourceGen"`，`preview=true`，`supersample=2`。

产出：`<output_dir>/<base_name>/` 下
`{id}_{state}.png`（每控件每状态一张）、`preview.png`、`layout.json`。

`layout.json` 实测结构：
```json
{"generator":"UISourceGen","version":"1.1","screen":"CoverKit","width":720,"height":520,
 "controls":[{"id":"btn_primary","type":"button","anchor":"center",
 "rect":[70,58,220,56],"text":"开始生成","image":".../btn_primary_normal.png",
 "slice":[32,32,32,32],"states":{"normal":"...","hover":"..."}}]}
```
`slice` 由 `corner_radius + border_width` 算出，是**九宫格元数据，不写 TextureImporter**。

`screen` 根字段：`name, width, height, background, background_end, background_gradient, theme, controls`。
`controls[]`：`id, type, anchor, rect:[x,y,w,h], text, icon, value, checked, selected, items, style, states`。
`type` 16 种：`Button Panel Card Slider Toggle ProgressBar Label Input Checkbox Radio Icon Divider Badge Tab Dropdown Scrollbar`。
`anchor` 9 值：`topleft/topcenter/topright/middleleft/center/middleright/bottomleft/bottomcenter/bottomright`。

**坐标语义（`UiGenModel.cs:308-347` 实测）**：左上原点、y 向下；
`anchor=center` 时 `X,Y` 是中心点（`Left() = X - W/2`，`Top() = Y - H/2`）。
→ 本技能 `coords.toUISourceGen()` 统一输出 `anchor:"center"` + `[cx, cy, w, h]`，
与 UI Spec 的 `[left, top, w, h]` 精确互转，零歧义。

### 体系二：通用 2D 画布

`draw / draw_batch / pdf / fonts / measure / svg / canvas_spec / canvas_examples`。
输入 `canvas = {name,width,height,background,elements:[...]}`，输出整图或 PDF。
本技能不依赖它，仅作扩展位。

### 调整图形：`draw` / `draw_batch`

`canvas = {name,width,height,background,elements:[...]}` 是**自由画布**语义，
不是 `screen.controls`：支持 `roundrect/circle/text/icon/image/path` 等 17 种 shape、
线性/径向渐变、中文 `font_family`、批量输出与 png/jpg/webp/bmp/gif 编码。
用 `canvas_spec` / `canvas_examples` 读活服务语法，`fonts` 找可用中文字体，
`measure` 量文字。实测及可复现参数见 `references/uisourcegen.md`。

`draw` 生成的是整张合成位图；需要独立多状态控件、九宫格元数据时仍用 `render`。
两者都只生成图资源，不创建或修改场景 UGUI。

### 关键能力缺口

**全目录没有 `TextureImporter` / `SpriteMetaData` / 大图切片代码。**
即：
- 不能「给一张效果图，把里面的按钮/面板切成多个 Sprite」；
- 不能把 `layout.json` 的 `slice` 写进 Unity 的九宫格设置；
- `CanvasRenderer.cs:502-504` 注释声称支持 `src` 裁剪，但 `CanvasParser` 从未读取 `src`，实为整图。

→ 本技能用 `assets.importSprites()`（`code.execute` 写 `spriteBorder`）补九宫格；
仅在需要保留参考图位图纹理时才用 `slice.js` 裁切。

`IconLibrary.Names` 12 个：`check cross arrow chevron-down star menu play plus minus search gear heart`，
未知名字 fallback 成实心圆。

---

## 4. 规则模块：`demo/Assets/Extends/CustomTools/Figma/Editor/UIRuleManage.cs`

`[ToolModule("ui_rule")]`（**不在用户列出的三个目录里，属于 Figma 扩展**）。

| 工具 | 参数 | required |
|---|---|---|
| `ui_rule.record_modify` | `name`, `modify_desc` | `name` |
| `ui_rule.record_renames` | `name`, `names_data` | 两者 |
| `ui_rule.get_renames` | `name` | `name` |
| `ui_rule.record_download_sprites` | `name`, `sprites_data`, `auto_load_sprites` | 前两者 |
| `ui_rule.get_download_sprites` | `name` | `name` |

写入 `UIDefineRuleObject.asset`（ScriptableObject）的
`node_names` / `node_sprites` / `modify_records`。
**代码层没有下游消费者**，真正消费方是 AI Agent 的构建步骤和编辑器 Inspector。
`auto_load_sprites=true` 会按 `img_save_to + fileName` 把磁盘 PNG 载入为 `Sprite` 引用。

---

## 5. 设计稿模块（可选）：`demo/.../Figma/Editor/FigmaManage.cs`

`figma.fetch_document`、`figma.download_images`、`figma.get_conversion_rules`、`figma.preview`。
「效果图 → 结构化描述」在仓库里**没有 C# 实现**；最接近的是 Figma API 拉节点树，
以及大模型视觉解析（本技能 `prompts/vision_to_spec.md`）。

`scene_describe_full` 只是 `ProjectSettings/McpSettings.asset` 里一条可配置提示词，
用于三维场景重建，**不是 UGUI 链路的 C# 工具**。

---

## 6. 过时名字对照表（官方提示词里的坑）

`demo/.../Figma/Editor/McpUISettings.cs:178-202` 的 `GetDefaultBuildSteps()` 有 21 步中文编排，
思路可借鉴，但工具名全是过时别名：

| 提示词写法 | 真实工具名 |
|---|---|
| `figma_manage` | `figma.*` |
| `ugui_layout` | `ugui.do_layout` / `ugui.get_layout` / `ugui.tattoo` |
| `ui_rule_manage` | `ui_rule.*` |
| `ui_toolkit`（源码注释） | `ui.*` |
| `ugui_layout`（`UGUILayoutTool.cs:37` 注释） | `ugui.do_layout` |

源码注释里的「对应方法名」普遍过时，一律以 `[ToolModule]` + `[ToolAction]` 为准。

---

## 7. 通用能力（包内，两个工程都有）

| 工具 | 用途 | 关键参数 |
|---|---|---|
| `code.execute` | 提交 C# 语句片段 | `mode:"runtime"`, `code`, `description`, `includes`, `timeout`, `return_output` |
| `scene.get_hierarchy` | 回读层级 | 无 |
| `scene.get_active` | 回读当前场景/dirty | 无 |
| `hierarchy.find_by_query` | 按条件找对象 | 返回 `[{yaml:"..."}]`，**坐标只保留 1 位小数，不可用于精确校验** |
| `game_view.screenshot` | 截 Game 视图 | `save_path`；**不再作为 UGUI 验收主路径**，实测可能返回黑色 JPEG 而扩展名为 `.png` |
| `scripts/preview.js` + `code.execute` | 隔离复制 Canvas 后用独立相机渲染 UGUI | 产出唯一 `ugui_preview-<uuid>.png`，检查非空与原场景状态；详见 `references/preview.md` |
| `camera.screenshot` | 相机截图 | `name,width,height,save_path` |
| `game_view.set_resolution` | 对齐 Game 尺寸与设计稿 | `width,height` |

`code.execute` 的 `mode=runtime` 接受**语句片段**，返回类型是 `object`，
输出用 `Debug.Log`（结果在 `logs` 字段），写 `return;` 会 CS0126。
完整类/命名空间要用 `mode=dll`。

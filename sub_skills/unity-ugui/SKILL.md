---
name: unity-ugui
description: Unity UGUI 全链路技能。把一张 UI 效果图解析成 UI Spec 结构化 JSON，优先用内置 uisourcegen.* 生成与调整图资源，再串联真 UGUI 创建、布局、独立 UI 预览和数值验收，在 Unity 场景里建出「图切好、UGUI 搭好、控件正确、AI 可预览」的可交互界面。适用于「给张效果图生成 UI」「切图并搭 UGUI」「效果图还原成界面」「调整 UI 图形」「UGUI 控件为什么点不动」「让 AI 看见做好的 UI」等请求。
agent_created: true
---

# Unity UGUI 制作技能

## 这个技能解决什么

**一句话**：给大模型一张效果图 → 输出 `UI Spec` → 自动在 Unity 里建好带贴图、九宫格、真实可交互控件的 UGUI。

三个模块原本各说各话、互不调用：

| 模块 | 位置 | 会做什么 | 不会做什么 |
|---|---|---|---|
| UI 布局 | `unity-package/Editor/Tools/UI` | 改/读已有 RectTransform、设锚点预设 | 创建控件 |
| UGUI 创建 | `demo/Assets/Extends/CustomTools/UGUI/Editor` | 按 13 种 component 建 GameObject | 精确布局；建真 Slider/ScrollRect |
| 图资源生产 | `demo/Assets/Extends/CustomTools/UISourceGen` | 按 `screen` JSON 重绘控件图 + `layout.json`（含 slice） | 从大图切 Sprite；写 TextureImporter |

再加上 `ui_rule.*` 只记录不被消费、`figma.*` 只服务 Figma 文档、
`layout.json` 没有任何下游消费者 —— **链路是断的**。

本技能就是补上这段编排：定义唯一真值源 `UI Spec`，
把坐标、控件、图资源三套语义统一，再分派到各模块并做数值验收。

---

## 开工前必读

| 文件 | 为什么必须读 |
|---|---|
| `references/contract.md` | UI Spec 数据契约；不懂它就会写出坐标漂移的 Spec |
| `references/tools.md` | **权威工具名与参数**。源码注释和官方提示词里的名字全是过时的 |
| `references/semantics.md` | 40 条实测铁律，尤其 3/4/5/6/9/10/14/15/17/36/37/38 条 |
| `prompts/vision_to_spec.md` | 大模型看效果图产出 Spec 的提示词（S1 入口） |
| `references/uisourcegen.md` | **内置图资源生成与调整实测手册**：render/draw 两条路、模板与字体、参数与坐标语义、复现实验 |
| `references/pipeline.md` | S0a–S5 详解、降级矩阵、失败恢复、案例归因、交付清单 |
| `references/preview.md` | 独立 UGUI 预览：隔离渲染、AI 读图、有效性与状态验收 |

> 姊妹技能 `unity-modeling` 的 `references/semantics.md` 讲的是**建模/贴图**坐标语义
> （texgen 画布 y 向上、`uv_atlas.rect` 是 x0y0x1y1）。
> 本技能是 **UGUI** 语义（效果图左上原点 y 向下、RectTransform 左下原点 y 向上）。
> **不要交叉套用**。

---

## 选路决策表

| 你要什么 | 走哪条 |
|---|---|
| 一张效果图 → 完整 UGUI | **全链路** `pipeline.js --spec <spec>` |
| 只要结构化描述 / 先确认解析对不对 | `pipeline.js --spec <spec> --dry-run` |
| 生成/重绘控件图、多状态图、九宫格 | 内置 `uisourcegen.render`（优先），或 `pipeline.js --stage assets --assets regenerate` |
| 调整单个 UI 图形、中文标题、图标组合 | 内置 `uisourcegen.draw` / `draw_batch`（优先）；见 `references/uisourcegen.md` |
| 只要从参考图切图 + Sprite + 九宫格 | `--stage assets --assets crop`（仅位图保真或降级时使用） |
| 已有 Spec，只要搭 UGUI | `--stage build` |
| 只重排锚点 / 复核布局 | `--stage layout` |
| 独立预览已搭好的界面、让 AI 看见实际 UGUI | `scripts/preview.js` 的 `capture()`，或 `--stage verify` 自动产出 `ugui_preview*.png` |
| 只验收现有界面 | `--stage verify` |
| 来源是 Figma 而非位图 | 先 `figma.fetch_document` + `get_conversion_rules`，再按 `vision_to_spec.md` 产出 Spec |
| 当前工程没有 demo 扩展 | 走降级路径，报告会明写缺什么（见 `capabilities.json`） |

---

## 标准流程

```bash
# 0. 离线自检（不连 Unity）
node scripts/check.js

# 1. 按 prompts/vision_to_spec.md 产出 ui_spec.json。
#    图资源优先标 asset=regenerate，交给内置 uisourcegen.* 生成/调整；
#    只有需要严格保留参考图位图纹理时才标 asset=crop。
#    层级：容器内的节点必须写 parent；同级节点 rect 不得交叉。

# 2. 只看 Spec 与计划，不碰 Unity（同时做层级/重叠几何检查）
node scripts/pipeline.js --spec examples/login.spec.json --dry-run
#    若 hierarchy_report.json 里 containsNotNested / siblingOverlaps 非 0，
#    命令退出码为 3，先修 Spec 再继续，不要跳过。

# 3. 全链路（跨进程务必带工程与实例；自动使用内置图资源与独立 UI 预览）
node scripts/pipeline.js \
  --spec examples/login.spec.json \
  --project "E:/unity3d-skilled-mcp/demo" \
  --instance <DISCOVER 返回的 instance_id> \
  --image "<效果图绝对路径>" \
  --assets regenerate

# 4. 看验收报告，并把 ugui_preview*.png 交给 AI/用户目检
cat Assets/AI-Generate/UGUI/Login/verify_report.json
```

实测结论：`uisourcegen.render` 可一次产出控件的 normal/hover/pressed/disabled PNG、
整屏 `preview.png` 和 `layout.json`；`uisourcegen.draw` 可用 `canvas.elements` 调整
渐变、圆角、图标与中文字体（如 `Microsoft YaHei UI`）。两者不能替代场景控件的
独立预览：图资源预览是「控件素材」，UGUI 预览是「场景中真实 Canvas 的渲染结果」。

或用封装好的样例：

```bash
node examples/run_login.js --dry-run
node examples/run_login.js --image "<效果图.png>"
```

程序化调用：

```js
const mcp   = require('./scripts/mcp');           // 复用 unity-modeling 的通用底座
const spec  = require('./scripts/spec');
const caps  = require('./scripts/capabilities');
const assets= require('./scripts/assets');
const build = require('./scripts/build');
const layout= require('./scripts/layout');
const rules = require('./scripts/rules');
const verify= require('./scripts/verify');

const project = mcp.resolveProject();
mcp.configure({ project, instance_id: process.env.UNITY_INSTANCE });

const s = spec.load('path/to/ui_spec.json');            // {ok, spec, problems, warnings}
const c = await caps.detect({ project });               // 能力发现（必做）
const a = await assets.fromReference(s.spec, { imageFile, outDir });
await assets.importSprites(a.manifest, { project });   // 九宫格在这里落地
await build.build(s.spec, a.manifest, { project });    // 真 UGUI 组件 + 引用绑定
const p = layout.plan(s.spec);
await layout.apply(p);
const r = await verify.run(s.spec, { outDir, manifest: a.manifest, plan: p });
console.log(verify.report(r));
```

---

## 六阶段与模块映射

| 阶段 | 脚本 | 串联的模块 | 关键产物 |
|---|---|---|---|
| S0 | `capabilities.js` | 活 MCP 服务 `/tools` 目录 | `capabilities.json`（含降级决策） |
| S0a | `geometry.js` | 纯几何：包含/交叉/平铺率（建 UI 之前就拦） | `hierarchy_report.json` |
| S1 | `spec.js` + `prompts/` | 大模型视觉解析 / `figma.*` | `ui_spec.json` |
| S1b | `assets.js` / `slice.js` | **内置 `uisourcegen.render`（首选）** / `draw` 调整 / `crop`（保真或降级）+ 导入 | `Generated/*.png`、`preview.png`、`layout.json`、`Sprites/*.png`、`assets_manifest.json` |
| S2 | `build.js` + `controls.js` | `code.execute`（主）/ `ugui.create_component`（降级） | `build_ugui.generated.cs` |
| S3 | `layout.js` | `ugui.do_layout` / `ugui.tattoo` / `ugui.get_layout` | `layout_plan.json`、`layout_actual.json` |
| S4 | `rules.js` | `ui_rule.*` | `ui_rules.json` |
| S5 | `preview.js` / `verify.js` | **隔离 Canvas 专用预览** + `code.execute` 反射回读 + 几何独立反算 | `ugui_preview*.png`、`audit_raw.txt`、`verify_report.json` |

`coords.js` 横切全部阶段，是**坐标唯一收口**；`controls.js` 是**控件唯一映射表**。

---

## 路径与环境

脚本内**不写死盘符、用户目录或工程位置**，全部运行时解析（复用
`unity-modeling/scripts/mcp.js`）：

工程根顺序：显式 `--project` → `UNITY_PROJECT` / `UNITY_PROJECT_PATH`
→ `~/.unity-mcp/skill.ini` 中最新启动端的 `project_path` → `process.cwd()` 上溯 → 技能目录上溯。

客户端顺序：`UNITY_MCP_LOADER` → ini 的 `skill_dir` → 上溯候选 → 用户级技能目录。
ini 只从 `[project:...]` 各节里取 `started_at` 最大的一端；旧版兼容节 `[unity3d-skilled-mcp]` 已不再发布，仅在旧文件存在时兜底。

跨进程调用必须带 `--expected-project` + `--instance-id`；
实例漂移要先重新 `discover`，**不自动重放**。

产物统一落在 `Assets/AI-Generate/UGUI/<界面名>/`（`ai-generate_save_path` 约定：每组一个文件夹）。

---

## 铁律

1. **先发现再使用**：`capabilities.detect()` → 按结果选路径。换工程后 demo 扩展可能全部消失。
2. **工具名以 `[ToolModule]+[ToolAction]` 为准**，不信注释、不信官方提示词里的别名。
3. **UI Spec 是唯一真值源**：所有阶段只从它派生，不反写；坐标只用 `rect=[left,top,w,h]`，且**永远是整图绝对坐标**，嵌套后也不改。
4. **层级由 `parent` 决定，不靠绘制顺序**：容器完全包住某节点时该节点必须挂到容器下（优先最近的容器）；同一父节点下的可见节点不得部分交叉，确有叠放需 `allow_overlap: true` 显式声明。`geometry.audit()` 在 S0a 强制检查，`--dry-run` 不通过则退出码 3。
5. **`anchoredPosition` 是 pivot 点偏移，不是矩形中心**：贴边预设 pivot 在角点上，用中心会漂移半个宽高（这是「层级混乱+重叠」的实际成因）。坐标换算只由 `coords.js` 收口，禁止在别处手写 y 翻转或 pivot 计算。
6. **图资源优先走内置工具**：先发现 `uisourcegen.render` / `draw` / `draw_batch`；生成控件图用 `render`，调整自由图形用 `draw`，批量调整用 `draw_batch`。不要默认用外部绘图脚本或只裁参考图。能力缺失时登记降级，再走 `crop`。
7. **`ugui.do_layout` 的向量必须给 3 个数**（schema 是 vector3），`path` 必填。
8. **控件正确 = 序列化引用非 null**：`Button.targetGraphic`、`Toggle.graphic`、
   `Slider.fillRect/handleRect`、`InputField.textComponent/placeholder`、
   `ScrollRect.content/viewport`。截图像不算数。
9. **九宫格必须落到 `TextureImporter.spriteBorder`**：`layout.json` 的 `slice` 只是元数据。
10. **主路径用 `code.execute` 建真组件**；`ugui.create_component` 只是降级（其 Slider 是假的）。
11. **C#5 语句片段**：提交前跑 `mcp.assertLint()`；不写 `$"` / `?.` / `nameof` / `out var` / 裸 `return;`，数据用 C# 字面量，不用 SimpleJson。
12. **脚本幂等**：先销毁同名根 `UGUI_<name>` 再重建。
13. **写操作响应丢失 ≠ 未执行**：失败先只读核实，从 `--stage` 续跑，不盲目重放整段序列。
14. **验收禁止自证循环**：`toUGUI → 写入 → 回读` 是同一函数的自我确认，换算写错也会「0.00px 通过」。必须叠加两条独立证据：`geometry.audit()` 的纯几何检查，和 `coords.screenRectFromReadback()` 从 Unity 回读值反算屏幕矩形。三项断言全过才算布局正确。
15. **预览必须专用且可被 AI 读取**：UI 验收不依赖旧 Game 视图截图；`preview.capture()` 在独立预览场景复制目标 Canvas、用独立相机输出真实 PNG，并检查 PNG 签名、尺寸、非空白与场景状态。必须把该 PNG 路径交给调用方查看。**数值全绿也要目检画面**。
16. **降级必须明写**：`capabilities.json` / `pipeline_report.json` 里列清缺什么、代价是什么，
    不把降级当完整交付。

---

## 脚本清单

| 脚本 | 职责 | 主要导出 |
|---|---|---|
| `mcp.js` | 通用底座（从 unity-modeling 复用，零改动） | `resolveProject` `configure` `call` `assertLint` `lintCSharp` |
| `png.js` / `inspect.js` | 零依赖 PNG 编解码与体检（复用） | `readPNG` `writePNG` `analyze` |
| `spec.js` | UI Spec 校验/归一化 | `normalize` `load` `skeleton` |
| `coords.js` | 坐标唯一收口（pivot 语义 / 父容器局部坐标 / 独立屏幕反算） | `toCropRect` `toUISourceGen` `toUGUI` `roundTrip` `frameOf` `screenRectFromReadback` |
| `controls.js` | 控件映射表 + screen 编译 | `TYPES` `needsRealBuild` `toScreen` |
| `capabilities.js` | 能力发现与降级矩阵 | `detect` `save` `GROUPS` |
| `slice.js` | 参考图 → 多 Sprite PNG（保真或降级） | `slice` `audit` `toRGBA` |
| `assets.js` | 内置 UISourceGen 重绘/裁剪 + Sprite/九宫格导入 | `fromReference` `regenerate` `importSprites` |
| `build.js` | 生成 C# 建真 UGUI 层级 | `generateCSharp` `build` `buildViaModule` |
| `layout.js` | 布局计划 / 官方工具 / 回读 | `plan` `apply` `readback` |
| `geometry.js` | 层级与重叠静态校验（含 `suggestedParents`） | `audit` `boxOf` `overlapArea` `contains` |
| `rules.js` | 结构化规则文档 + ui_rule 同步 | `compose` `save` `pushToUnity` |
| `preview.js` | 隔离复制 Canvas、独立相机渲染并检测有效 PNG | `options` `generateCSharp` `inspectPNG` `capture` |
| `verify.js` | 反射回读 + 几何独立反算 + 专用预览有效性 + 数值验收 + 图像比对 | `auditUnity` `run` `report` |
| `pipeline.js` | 六阶段编排 CLI | `--spec --stage --dry-run --image` |
| `check.js` | 离线自检 | `node scripts/check.js` |

---

## 参考文档

- `references/contract.md` — UI Spec 数据契约与产物清单
- `references/tools.md` — 权威工具表、参数 schema、过时名字对照
- `references/uisourcegen.md` — 内置图资源生成/调整能力、实测结果、示例及边界
- `references/semantics.md` — 40 条实测铁律（含源码行号）
- `references/pipeline.md` — 阶段详解、降级、故障恢复、交付边界
- `references/preview.md` — 独立 UGUI 预览实现、调用与验收契约
- `prompts/vision_to_spec.md` — 效果图 → Spec 的大模型提示词
- `templates/ui_spec.schema.json` — JSON Schema（Draft-07）
- `templates/ui_spec.example.json` — 完整 Spec 示例
- `examples/login.spec.json` + `run_login.js` — 端到端可跑样例

---

## 自检

```bash
node scripts/check.js                                      # 离线，不连 Unity
node scripts/pipeline.js --spec examples/login.spec.json --dry-run
```

第一项验证 Spec/坐标/控件/切图/C# 生成/文件完整性/可搬运性；
第二项验证 Spec 能通过校验、布局计划能生成、当前工程具备哪些能力。

---

## 接入 WorkBuddy / Unity MCP

本技能与 `unity-modeling` 并列放在 `sub_skills/` 下。

### Unity 侧：`UnityUguiSkillPrompt.cs`（已提供）

`demo/Assets/Extends/CustomPrompts/UnityUguiSkillPrompt.cs` 是仿照
`UnityModelingSkillPrompt.cs` 写的 `IPrompts` 提示词脚本，注册名为 **`unity_ugui_skill`**，
把本技能的绝对路径、必读文件、执行顺序和三条关键坑直接喂给连上 MCP 的大模型。

- **无需手工登记**：`McpService.DiscoverPrompts()` 反射扫描所有 `IPrompts` 实现类
  （`unity-package/Editor/Connect/McpService.cs:940-997`），
  与 `ProjectSettings/McpSettings.asset` 的 `configurablePrompts` 是两套机制。
  把脚本放进工程、Unity 编译后即出现在 `prompts/list` 与 GUI 的「代码实现的提示词」列表。
- **路径运行时解析**：环境变量 `UNITY_UGUI_SKILL_DIR` → `<工程根>/sub_skills/unity-ugui`
  → `<工程根父目录>/sub_skills/unity-ugui`（与建模技能一致的仓库布局）→ 逐级上溯；
  以目录内存在 `SKILL.md` 为命中条件，不依赖进程工作目录。
- **跨工程保护**：当前 Editor 打开的不是含 `sub_skills` 的仓库时（例如
  `D:\WorkSpace\tdshoweditor`），提示词会追加一句明确警告，并提醒 demo 私有扩展不可用，
  不会让 AI 拿着推断出来的假路径去读文件。此时设 `UNITY_UGUI_SKILL_DIR` 即可指回真实位置。
- 中英双语由 `UniMcp.L.T` 切换；`Keys => null`（无参数提示词）。

### WorkBuddy 侧

直接把本目录作为技能加载即可；技能内不依赖任何绝对工程路径。

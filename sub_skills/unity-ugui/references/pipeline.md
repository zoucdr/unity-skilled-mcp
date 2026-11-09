# 流水线：效果图 → UGUI（S0a–S5）

入口：`node scripts/pipeline.js --spec <ui_spec.json> [选项]`

```
S0a geometry      层级/重叠纯几何自检（建 UI 之前就拦）
S0 capabilities   发现工具 / 定降级策略
S1 assets         Spec → 切图 → Sprite + 九宫格
S2 build          Spec → 真实 UGUI 层级（组件 + 引用绑定）
S3 layout         ugui.do_layout / tattoo / get_layout
S4 rules          ui_rules.json + ui_rule.*
S5 verify         反射回读 / 几何反算 / 专用预览 / 报告
```

每阶段独立可重跑，`--stage <name>` 从该阶段起续跑；`stage_state.json` 记录断点。

---

## S0a 层级与重叠几何自检

建任何对象之前先跑 `geometry.audit(spec)`，只读 `SPEC.rect`：

| 检查 | 判定 | 修复动作 |
|---|---|---|
| 包含即父子 | 容器 A 完全包住 B 却同级 → `containsNotNested` | `B.parent = A.id`（取报告里的 `suggestedParents`） |
| 同级不重叠 | 同父可见节点部分交叉 → `siblingOverlaps` | 错开 `rect`，或 `allow_overlap: true` 声明有意叠放 |
| 平铺率 | `flatRatio ≥ 0.8` 且节点 ≥ 6 → warning | 把容器内元素挂到容器下 |

前两项是**硬失败**：`--dry-run` 直接退出码 3，全链路也会把问题写进 `pipeline_report.json`。
产物 `hierarchy_report.json` 里带 `suggestedParents`，可直接照着补 `parent`。

---

## S0 能力发现

```js
const caps = require('./scripts/capabilities');
const c = await caps.detect({ project, instance_id });
```

按活服务里**真实注册**的工具名决定策略：

| 子链 | 首选 | 降级 | 降级代价 |
|---|---|---|---|
| 设计稿解析 | `figma.fetch_document` | 大模型视觉解析（prompts/vision_to_spec.md） | 坐标/文字靠目测，需登记 uncertainties |
| 图资源 | **内置 `uisourcegen.render` / `draw`** | `slice.js` 从效果图裁切 | 无多状态图/自由图形调整；保真度取决于效果图清晰度 |
| 搭建 | `code.execute` | `ugui.create_component` | **控件残缺**，verify 会报 |
| 布局 | `ugui.do_layout` / `tattoo` | C# 写 RectTransform | 不走官方布局模块 |
| 规则 | `ui_rule.*` | 本地 `ui_rules.json` | 不写进 Unity 规则资产 |
| 验收 | 专用 UGUI 预览 + 回读 | 仅基础回读 | 无 AI 可见的实际界面渲染 |

结果写 `capabilities.json`，警告全部进 `pipeline_report.json`。
**禁止跳过**：`references/semantics.md` 第 3 条实测换工程后扩展工具全消失。

---

## S1 图资源

### 路径 A（首选，工程注册 `uisourcegen.*` 时）：内置生成与调整

```js
const res = await assets.regenerate(spec, { outputDirRel, baseName, supersample });
// → Generated/<id>_<state>.png + preview.png + layout.json
```

- `controls.toScreen(spec)` 把 UI Spec 编译成内置 `uisourcegen.render` 的 `screen`。
- `coords.toUISourceGen()` 把 `[left,top,w,h]` 统一转成 `anchor=center` 中心点坐标。
- 从 `layout.json` 取 `slice` 和 `states`；不信任其中的 `image` 路径（铁律 16），
  按 `outputDir/baseName` 重算。
- 调整单个 UI 图形用 `uisourcegen.draw`，批量用 `draw_batch`；语法来自
  `canvas_spec` / `canvas_examples`，中文字体来自 `fonts`。
- 完整实测、示例、边界与复现输入见 `references/uisourcegen.md`。
- `preview.png` 只是素材合成预览，不能用作场景 UGUI 验收。

### 路径 B（保真或降级）：从参考图裁切

```js
const res = assets.fromReference(spec, { imageFile, outDir, overwrite: true });
// → Sprites/<id>.png + assets_manifest.json
```

- `slice.js` 用零依赖 PNG 解码，按 `coords.toCropRect()` clamp 后裁切，输出 RGBA PNG。
- 仅在 `uisourcegen.*` 未注册、或必须保留参考图位图纹理时使用，并在报告里登记降级。
- 越界会 clamp 并记 warning；纯色切图会被 `slice.audit()` 标记（多半是 rect 落在空白处）。

### 两条路径都汇入：导入 Unity

```js
await assets.importSprites(manifest, { project, assetDirRel });
```

生成 C# 逐张设置：
`textureType=Sprite` / `spriteBorder=slice` / `alphaIsTransparency` /
`mipmapEnabled=false` / `textureCompression=Uncompressed` → `SaveAndReimport()`。

这一步是**九宫格真正生效**的地方（UISourceGen 只给元数据，铁律 15）。

---

## S2 搭建

```js
const p = layout.plan(spec);              // 先算好所有路径与坐标
const r = await build.build(spec, manifest, { project });
fs.writeFileSync('.../build_ugui.generated.cs', r.code);   // 留档可复现
```

`build.generateCSharp()` 生成一条 `code.execute` 语句片段：

1. `DestroyImmediate` 同名根 `UGUI_<name>`（幂等，铁律 33）；
2. 建 Canvas + CanvasScaler（`reference_resolution` / `match`）+ GraphicRaycaster；
3. 若无 EventSystem 则补建；
4. 按 `depth/order` 排序逐节点建 GameObject + RectTransform，
   用 `__setRect` 写入 `coords.toUGUI()` 的结果（stretch 走 `offsetMin/offsetMax`）；
5. 按 `controls.TYPES` 挂真实组件并绑引用：
   Button.targetGraphic、Toggle.graphic、Slider.fillRect/handleRect、
   InputField.textComponent/placeholder、ScrollRect.content/viewport、
   Dropdown.template/captionText/itemText；
6. 多状态图存在时给 Button 设 `SpriteSwap` + `SpriteState`；
7. `MarkSceneDirty` + `SaveScene`。

生成的代码先过 `mcp.assertLint()`（C#5 静态预检，铁律 27/28/29），再提交。

**降级**：`buildViaModule()` 用 `ugui.create_component` 逐个建，
报告里明确写出「Slider/Toggle/InputField 无真实绑定、ScrollRect/Dropdown 缺失」。

---

## S3 布局

`build.js` 已经写准 RectTransform（stretch 只能 C# 写），S3 负责把官方布局模块显式串进来：

```js
const p = layout.plan(spec);                     // layout_plan.json
const applied = await layout.apply(p);          // do_layout + tattoo
const rb = await layout.readback(p);            // get_layout → layout_actual.json
```

- `doLayout` 的每个向量都是 3 元（铁律 5）。
- `tattoo` 传 `tattoo_self:false`，改锚点但保持视觉位置（铁律 7）。
- 单节点失败不中断整批（写操作可能已生效，铁律 32），逐条记进报告。

`layout_plan.json` 是人类/大模型可审阅的中间产物：每个节点的
`path` / `rect` / `anchorPreset` / `doLayout` / `offsetMin` / `offsetMax` 一目了然。

---

## S4 结构化规则

```js
const r = rules.compose(spec, manifest);         // ui_rules.json
rules.save(r, outDir);
if (strategy === 'module') await rules.pushToUnity(r, { ruleName });
```

`ui_rules.json` 始终产出，含：
`node_names`（id ↔ Unity 名 ↔ 原始节点名 + 类型 + 层级 + 置信度）、
`node_sprites`（id ↔ PNG + slice + 多状态）、
`missing_sprites`、`modify_records`、`uncertainties`、`low_confidence_nodes`。

模块可用时再同步进 `UIDefineRuleObject.asset`。
注意：这些记录**没有代码层消费者**（铁律 24），价值在迭代与人工复核，不要当成自动生效。

---

## S5 验收

```js
const v = await verify.run(spec, { outDir, manifest, plan, shotFile, referenceFile });
console.log(verify.report(v));
```

### S5 前新增：专用 UGUI 预览

`preview.capture()` 在隔离预览场景复制目标 Canvas，用独立相机写
`ugui_preview-<uuid>.png`，检查 PNG 签名、尺寸、非空白和原场景状态。
把返回路径交给 AI 图片读取工具查看。详见 `references/preview.md`。
`game_view.screenshot` 不再是验收主路径：实测可能产出 JPEG 内容的 `.png` 黑图。

### 数值断言（不看图拍脑袋）

| 检查 | 方法 |
|---|---|
| 节点存在 | `code.execute` 反射 `GameObject.Find(path)` → `audit_raw.txt` |
| 控件组件齐全 | 按 `controls.TYPES` 逐个 `GetComponent` |
| 交互引用已绑定 | 读 `targetGraphic/graphic/fillRect/...` 是否非 null |
| Sprite 已挂载 | `Image.sprite != null` |
| 层级：包含即父子 | `geometry.audit()`，`containsNotNested` 必须为 0 |
| 同级控件无意外重叠 | `geometry.audit()`，`siblingOverlaps` 必须为 0 |
| 布局误差 < 2px | 回读 `anchorMin/Max/anchoredPosition/sizeDelta` 与 `coords.toUGUI()` 比对 |
| **屏幕几何位置（独立反算）** | `coords.screenRectFromReadback()` 用回读的 pivot 反算屏幕矩形，与 `SPEC.rect` 比对；**不复用 `toUGUI`** |
| 图资源非纯色 | `inspect.analyze()` |
| 专用预览有效 | `preview.inspectPNG()`：PNG 签名/尺寸/可见像素/颜色范围/非空白 |
| 截图还原度 | 专用预览 PNG 与效果图采样比较 MAE |

`ugui.get_layout` 只用来交叉验证；精确坐标一律用反射回读，
因为 `hierarchy.find_by_query` 的 yaml 只保留 1 位小数（铁律 30）。

> **不要只信「布局误差 0.00px」。** 该项拿 `toUGUI()` 的输出去比对 Unity 回读值，
> 而回读值就是同一个函数写进去的 —— 换算写错时两边一起错，报告照样全绿。
> 必须同时有「独立反算」和「几何检查」两项通过，最后再看预览图。

**MAE 只能看趋势**：透视、字体差异、九宫格拉伸都会抬高数值，
必须配合人工目检，不能作为「还原度达标」的唯一判据。

### 验收通过的完整定义

数值全绿 **且** 目检 `ugui_preview*.png` 无错位/压盖/越界才算交付。
历史上出现过 13/13 全绿、画面却错位重叠的情况，成因是坐标函数 bug + 自证循环验收。

---

## 失败恢复

| 症状 | 处理 |
|---|---|
| 实例漂移 / 工程不匹配 | 重新 `discover`，带新的 `--instance`；不重放旧请求 |
| 写调用超时 | 先只读核实（产物文件、`scene.get_active`、反射回读），再决定续跑；不换 request_id 重放 |
| `uisourcegen.render` 返回 Success 但没文件 | 检查 `layout.json` 与 PNG 是否真实存在（铁律 21），改走 crop |
| `ugui.do_layout` 报 schema 错 | 十有八九向量给了 2 个数，改成 3 元 |
| C# 编译失败 | 看 `build_ugui.generated.cs`；先本地 `mcp.assertLint(code)`，再查委托元数 |
| 切图全纯色 | rect 落在空白处或效果图尺寸与 `source.width/height` 不符，校正 Spec |
| 验收报「引用未绑定」 | 说明走了 module 降级路径，改用 `code` 路径重建 |
| 圆角控件拉伸变形 | `slice` 没填或没跑 `importSprites()`，检查 `spriteBorder` |
| 场景里出现多个同名根 | 幂等没生效，检查 `DestroyImmediate` 的根名是否与 `rootName` 一致 |
| **所有控件挤在一角 / 互相压盖** | 90% 是坐标函数把「矩形中心」当 pivot 位置（见 `semantics.md` 36 条）；查 `audit_raw.txt` 的 `rect|` 行，若 `anchoredPosition` ≈ 半宽半高偏移即确诊 |
| **层级看起来全平铺、没有分组** | Spec 里 `parent` 全为空。跑 `geometry.audit()` 拿 `suggestedParents` 直接补 |
| **升级后嵌套节点整体漂移** | 漏了父容器局部坐标换算，确认走 `coords.frameOf()`，别自己减偏移 |
| 报告全绿但画面错位 | 自证循环：确认 `verify_report.json` 里有「屏幕几何位置（独立反算）」这一项且为 OK |

---

## 案例：一次「层级混乱 + 重叠」的完整归因

现象：28 个节点的 HUD 里，文字压在血条上、面板缩成一条、整体错位，
但流水线报告 **10/10 全绿、布局误差 0.00px**。

三个独立缺陷叠加：

1. `coords.toUGUI()` 用矩形中心当 `anchoredPosition`，而 pivot 是锚点角点
   → 每个贴边控件偏移自身半个宽高，偏移量各异 → 互相覆盖。
2. 验收拿 `toUGUI()` 的期望值比对 Unity 回读值，两者同源 → 错误被完整掩盖。
3. Spec 的 28 个节点全是根节点、无 `parent`，靠全局 `order` 当绘制顺序，
   容器内的文字/血条从未挂在容器下。

修复：坐标改用 pivot 点、`verify` 增加独立反算与几何检查、Spec 补 `parent`
（20/28 嵌套，8 个根分组）、`SetSiblingIndex` 改同父局部序号。
修复后同一份报告新增 3 项断言，MAE 从 18.18 降到 1.02，画面才真正正常。

> 教训：**「数值都绿」不等于「画面对」。期望值与写入值同源时，验收就是自己给自己盖章。**

---

## 交付前三件事

1. `node scripts/check.js` —— 离线自检全绿（技能假设没坏）。
2. `node scripts/pipeline.js --spec <spec> --dry-run` —— Spec 与计划合法、能力已探测。
3. 全链路跑完后看 `verify_report.json` + AI/人工目检 `ugui_preview*.png`。

三件都过才算交付。若当前工程缺扩展工具，报告里会明写降级项——
**不要把降级当成完整交付**。

---

## 边界

- 本技能产出**结构化、可交互、带贴图与九宫格的低/中保真 UGUI**，
  不产出运行时业务逻辑（点击事件、网络请求、动画状态机）。
- 效果图只有一张位图时，hover/pressed 等状态无法凭空生成；
  需要多状态请用 `asset: regenerate` 或额外提供状态图。
- 文字是 `UnityEngine.UI.Text + Arial`，不含 TMP 字体资产与富文本排版。
- 复杂自绘（贝塞尔插画、粒子特效）请作为整图节点 `asset: crop` 处理。

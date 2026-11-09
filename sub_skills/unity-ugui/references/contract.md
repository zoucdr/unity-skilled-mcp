# UI Spec 数据契约 v1.0

> 权威定义：`templates/ui_spec.schema.json`，校验实现：`scripts/spec.js`。
> 本文件讲「为什么这么设计」与「每个字段下游被谁消费」。

---

## 1. 为什么需要一份中间格式

三套模块原先**彼此不调用**，各自说各自的语言：

| 模块 | 输入 | 输出 |
|---|---|---|
| UISourceGen | `screen` JSON（16 控件类型、9 锚点、center 时 rect 是中心点） | 每控件每状态 PNG + `layout.json`（含 `slice`） |
| UGUI 创建模块 | `component` 字符串 + 默认坐标 | 场景里的 GameObject（部分控件残缺） |
| UGUI 布局模块 | `path` + RectTransform 原生属性（vector3、y 向上） | 修改后的 RectTransform |
| `ui_rule.*` | `names_data` / `sprites_data` 字典 | `UIDefineRuleObject.asset` |

`layout.json` **没有任何消费者**；`screen` 和 `RectTransform` 的坐标语义互相冲突；
`component` 与真实 UGUI 组件不是一一对应。

所以技能引入 `UI Spec` 作为唯一真值源：

```
效果图 ─(视觉解析 / Figma)→ UI Spec ─┬→ screen JSON      → UISourceGen 出图
                                    ├→ rect 裁切          → Sprite + 九宫格
                                    ├→ RectTransform 计划 → code.execute / ugui.do_layout
                                    ├→ 控件映射表         → 真实 UGUI 组件 + 引用绑定
                                    ├→ ui_rules.json      → ui_rule.* / 人工复核
                                    └→ 验收基准           → verify.js 数值回读
```

**任何阶段都不改 Spec**，只从它派生。返工时改 Spec 再重跑，不产生坐标漂移。

---

## 2. 坐标：唯一真值源

```
rect = [left, top, width, height]
       效果图左上原点，x 向右，y 向下，单位像素
```

三套目标语义全部由 `scripts/coords.js` 换算，**禁止在别处手写 y 翻转**：

| 目标 | 语义 | 换算 |
|---|---|---|
| Sprite 裁切 | 左上原点 y 向下 | 直接 clamp `rect` |
| UISourceGen | 左上原点 y 向下，`center` 时 rect 是中心点 | `anchor:"center"`, `rect:[left+w/2, top+h/2, w, h]` |
| UGUI RectTransform | 左下原点 y 向上，anchor 归一化 0..1 | `anchorMin/Max` + `pivot` + `anchoredPosition` + `sizeDelta`；stretch 用 `offsetMin/offsetMax` |

`canvas.reference_resolution` 与 `source.width/height` 不同时，
`coords.scaleOf()` 会按 `sx=refW/srcW`、`sy=refH/srcH` 缩放所有坐标（不能只缩一边）。

`coords.roundTrip()` 提供闭环自检：`rect → UGUI → rect` 误差必须为 0，
`check.js` 有对应回归断言。

**但闭环自检只证明函数自洽，不证明 Unity 里位置正确**：期望值和写入值同源，
换算写错时两边一起错。真正的验收走两条独立路径：

| 路径 | 输入 | 作用 |
|---|---|---|
| `geometry.audit(spec)` | 只读 `SPEC.rect` | 检查「包含即父子」「同级不重叠」「平铺率」，完全不碰 RectTransform |
| `coords.screenRectFromReadback(v, node, spec)` | Unity 回读的 RectTransform | 按 Unity 语义反算屏幕矩形，**不复用 `toUGUI`**，与 `SPEC.rect` 比对 |

### 2.1 坐标的三个易错点

1. **`anchoredPosition` 是 pivot 点偏移，不是矩形中心。**
   非 stretch 分支的 pivot 取锚点角 `(minx,miny)`，所以必须先用 pivot 在矩形内取点
   再去减锚点参考点；用中心会让节点漂移自身宽高的一半（详见 `semantics.md` 36 条）。
2. **`rect` 永远是整图绝对坐标。** 有 `parent` 时由 `coords.frameOf()` 换算成父容器
   局部坐标，锚点参考尺寸也从整屏换成父容器尺寸；`roundTrip()` 会再加回偏移。
3. **`sizeDelta` 在 stretch 下是差量**，不是绝对宽高，不能用它反算屏幕尺寸；
   `screenRectFromReadback()` 遇到 `anchorMin !== anchorMax` 直接返回 `null`。

---

## 3. 控件：抽象类型 → 真实组件

权威表在 `scripts/controls.js`。核心原则：
**抽象类型决定「挂什么组件 + 绑什么引用」，而不是决定「画成什么样」**。

| Spec type | 真实 UGUI 组件 | 必须绑定的引用 |
|---|---|---|
| `canvas` | Canvas + CanvasScaler + GraphicRaycaster | — |
| `panel` / `card` / `image` / `icon` / `divider` | Image | sprite |
| `label` | Text | font |
| `button` | Image + Button | `targetGraphic`（+ SpriteSwap 多状态） |
| `toggle` / `tab` | Image + Toggle | `targetGraphic`, `graphic` |
| `slider` | Slider | `fillRect`, `handleRect`, `targetGraphic` |
| `input` | Image + InputField | `textComponent`, `placeholder` |
| `progress` | Image (Filled) | `fillMethod`, `fillAmount` |
| `scroll` | Image + ScrollRect + RectMask2D | `content`, `viewport`（自动建子节点） |
| `dropdown` | Image + Dropdown | `template`, `captionText`, `itemText` |
| `badge` | Image + 子 Text | — |
| `group` | 仅 RectTransform | — |

这张表是「控件正确」的验收依据：`verify.js` 会逐个 `GetComponent` 并读引用字段，
非 null 才算通过。**只靠 `ugui.create_component` 无法通过这张表**。

`usesRealBuild` / `needsRealBuild(type)` 标出必须走 `code.execute` 的类型。

---

## 4. 图资源字段

| 字段 | 含义 |
|---|---|
| `asset: crop` | 保真或降级。内置图资源工具缺失 / 需保留参考位图纹理时，从效果图按 `rect` 裁出独立 PNG（`slice.js`） |
| `asset: regenerate` | **默认/首选**。交给内置 `uisourcegen.render` 生成，可产多状态图；自由图形调整用 `uisourcegen.draw` |
| `asset: external` | 已有 PNG，`asset_path` 必填 |
| `asset: none` | 纯色/程序化外观，不产图（纯文字、进度条、滑条轨道） |
| `slice: [l,t,r,b]` | 九宫格边距，写进 `TextureImporter.spriteBorder`；有圆角必须填 |
| `states` | 控件状态清单；`crop` 只产 `normal`，多状态需 `regenerate` |

> **重要**：UISourceGen 只在 `layout.json` 里写 `slice` 元数据，从不写 TextureImporter。
> `assets.importSprites()` 是本链路里唯一把九宫格真正落到 Unity 的地方。

---

## 5. 不确定性（强制）

- 每节点 `confidence ∈ [0,1]`，低于 0.6 会被 `spec.js` 警告、
  被 `ui_rules.json` 列入 `low_confidence_nodes`。
- 根级 `uncertainties[]` 登记所有猜测：`{node, field, reason, alternatives}`。
- **禁止静默臆测**：`prompts/vision_to_spec.md` 把「宁可登记不确定也不要编造」写成硬规则。

---

## 6. 产物清单（每次运行写在 `<outDir>` 下）

| 文件 | 阶段 | 内容 |
|---|---|---|
| `capabilities.json` | S0 | 工具探测结果 + 降级策略 + 警告 |
| `ui_spec.normalized.json` | dry-run | 归一化后的 Spec |
| `Sprites/*.png` | S1 | 按 rect 裁出的控件图 |
| `assets_manifest.json` | S1 | id → PNG / rect / slice / states |
| `build_ugui.generated.cs` | S2 | 实际提交的 C#（可审阅、可复现） |
| `layout_plan.json` | S3 | 每节点 path + 锚点 + RectTransform 计划 |
| `layout_actual.json` | S3 | `ugui.get_layout` 回读 |
| `ui_rules.json` | S4 | 节点映射 / 图片映射 / 修改记录 / 不确定项 |
| `hierarchy_report.json` | S0a / S5 | 层级与重叠几何检查（包含未嵌套、同级交叉、平铺率、建议 parent） |
| `audit_raw.txt` | S5 | Unity 反射回读的 node/comp/ref/rect 记录（rect 含 pivot 两个分量） |
| `hierarchy_actual.txt` | 人工/调试 | 从 Unity 导出的真实父子树，用于核对 `parent` 是否落地 |
| `ugui_preview-<uuid>.png` | S5 | 独立 UGUI 预览 PNG（隔离 Canvas 渲染，供 AI 目检） |
| `verify_report.json` | S5 | 逐项验收结果 |
| `pipeline_report.json` | 全程 | 阶段状态、警告、问题汇总 |
| `stage_state.json` | 全程 | 断点续跑状态 |

---

## 7. 版本与兼容

- `spec_version: "1.0"`；`spec.js` 对未知版本直接报错，不做静默兼容。
- Spec 是纯数据：不含工具名、不含工程绝对路径、不含 C#。
  换工程、换渲染管线（Built-in / URP）、换工具集都不用改 Spec。
- 新增控件类型只需扩 `controls.js` 的 `TYPES` + schema 枚举 + `build.js` 的 case。

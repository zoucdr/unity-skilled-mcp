# 实测铁律（踩过的坑，按编号引用）

> 每条都有源码行号或活服务返回作为证据。注释与实现矛盾处一律以实测为准。
> 与 `unity-modeling/references/semantics.md` 同风格，但结论**不通用**——
> 那是建模/贴图的坐标语义，这里是 UGUI 链路的，不要交叉套用。

---

## 一、工具与能力

### 1. 工具名 = `[ToolModule] + "." + [ToolAction]` `[源码]`
`unity-package/Editor/ToolFramework/ToolCatalog.cs:205-209`。
源码注释里的「对应方法名」普遍过时：`ugui_layout` → `ugui.do_layout`，
`ui_toolkit` → `ui.*`，`ui_rule_manage` → `ui_rule.*`。

### 2. 官方 21 步构建提示词里的工具名全是别名 `[源码]`
`demo/Assets/Extends/CustomTools/Figma/Editor/McpUISettings.cs:178-202`
写的是 `figma_manage` / `ugui_layout` / `ui_rule_manage`，真实前缀是
`figma` / `ugui` / `ui_rule`。思路可借鉴，名字不可照抄。

### 3. 能力随工程变，必须先发现 `[实测]`
`E:\unity3d-skilled-mcp\demo` 有 `uisourcegen.*` / `ugui.generate` / `ui_rule.*` / `figma.*`；
换到 `D:\WorkSpace\tdshoweditor`（URP）后 `find uisourcegen` / `ui_rule` / `figma` 全部返回 0，
只剩包内的 `ugui.do_layout/get_layout/tattoo`。
原因：demo 扩展在 `Assets/Extends/CustomTools/` 下，是工程私有脚本。

### 4. `[ToolAction]` 简写形式的 `Parameters` 是权威 schema `[源码]`
`ToolCatalog.cs:140-170`。`UguiToolModule.cs:72` 只声明
`component=require, name=require`，但 handler `:77-83` 读
`parent/x/y/w/h/label/value`——这些参数**外部传不进去**，只能吃默认值
`x=100,y=100,w=200,h=60`。`CreateKeys()` 里定义了也没用。

### 5. `ugui.do_layout` 的向量参数必须是 3 个数 `[实测]`
活服务 schema：`anchored_pos/size_delta/anchor_min/anchor_max/pivot` 全是
`{"type":"array","format":"vector3","minItems":3,"maxItems":3}`。
描述文字写 `[x, y]`，传 2 个数会被拒。`coords.toUGUI().doLayout` 统一给 `[x,y,0]`。

### 6. `ugui.do_layout/get_layout/tattoo` 的 required 只有 `path` `[实测]`
`UGUILayoutTool.cs:63` 把 `path` 声明为 `optional=false`，而 `instance_id` 反而可选。
文件头示例 `:28` 不传 path 直接写 `anchored_pos`，按 schema 应被拒——注释与实现矛盾。

### 7. `ugui.tattoo` 会保持视觉位置 `[源码]`
`tattoo_self=false` → `ApplyAnchorPresetWithVisualPositionPreserved()`（`:450, :458`）。
`tattoo_self=true` 才基于元素当前位置重算锚点。
`preserve_visual_position` 参数在注释里提到，但 `CreateKeys` 和 ToolAction 都没有。

### 8. `select_many` 未暴露，批量修改形同虚设 `[源码]`
`UGUILayoutTool.cs:1071` 读 `select_many`，但既不在 `CreateKeys()` 也不在参数表；
`:1051-1059` 自承「JsonArray 不能直接转换为 GameObject[]…返回 new GameObject[0]」。
→ 本技能逐节点调用，不依赖批量。

---

## 二、控件

### 9. `Slider` 是假的 `[源码]`
`UguiToolkit.cs:230-240` 用两张 `Image.Type.Filled` 拼视觉，
**没有 `AddComponent<Slider>()`**。

### 10. `InputField` / `Toggle` 引用未绑 `[源码]`
`InputField()` `:241-249` 未赋 `textComponent` / `placeholder`；
`Toggle()` `:218-229` 未绑 `graphic` / `targetGraphic`。
没有 `ScrollRect` / `Dropdown` / `RawImage` / `Mask`。

### 11. `UguiGenerator` 依赖静态 map，域重载即失效 `[源码]`
`UguiToolkit.cs:74` 静态 `Dictionary`，`Init()` `:78` 会 Clear；
`EnsureRoot()` `:353-358` 依赖它。`UguiToolModule.cs:33` 自己注明「同一会话内已创建」。
→ 本技能用 `code.execute` 一次成型，不依赖跨调用状态。

### 12. `UguiTemplateWindow` 没有模板文件格式 `[源码]`
只有 5 个字符串 + `[MenuItem]`。与 UISourceGen 的 `Templates/*.json` 是两套机制，
不要指望把 Spec 写成「模板」喂给它。

### 13. 控件正确性的判据是序列化引用，不是外观 `[实测结论]`
`verify.js` 逐项读 `targetGraphic` / `graphic` / `fillRect` / `handleRect` /
`textComponent` / `placeholder` / `content` / `viewport` / `template`，
非 null 才通过。截图看着像不算。

---

## 三、图资源

### 14. UISourceGen 不支持「大图切多 Sprite」 `[源码]`
全目录 grep 无 `TextureImporter` / `SpriteMetaData` / `spritesheet`。
它只能「按 screen JSON 重绘控件图」，产出每控件每状态**独立** PNG。

### 15. `layout.json` 的 `slice` 只是元数据 `[源码]`
`UiGenTool.cs:373-381` 把九宫格边距写进 JSON，从不写 `TextureImporter.spriteBorder`。
→ `assets.importSprites()` 补上这一步，否则九宫格拉伸一定变形。

### 16. `layout.json` 里的 image 路径会脱节 `[实测]`
`demo/Assets/AI-Generate/UISourceGen/CoverKit/layout.json` 里写
`Assets/UISourceGen/CoverKit/*.png`，文件实际在
`Assets/AI-Generate/UISourceGen/CoverKit/`。它记录的是**生成时传入的 `output_dir`**，
产物一移动就失效。→ `assets.regenerate()` 不信任该字段，按 `outputDir/baseName` 重算路径。

### 17. UISourceGen 坐标是左上原点、y 向下 `[源码·关键]`
`UiGenModel.cs:308-347`：
`Left() = X - W/2`（center）、`Top() = Y - H/2`（center）、
`Top() = screenH - Y - H`（bottom 系列，Y 是距底距离）。
→ 与 UI Spec 完全同构，`coords.toUISourceGen()` 统一输出 `anchor:"center"`，
避免 9 种锚点语义换算出错。

### 18. `CanvasRenderer` 的 `src` 裁剪是空承诺 `[源码]`
`CanvasRenderer.cs:502-504` 注释说支持 `src` 区域裁剪，
但 `CanvasParser` 从未读取 `src`，代码里 `src` 恒为整图。

### 19. 模板字段有解析器不识别的项 `[源码]`
`shop.json` 的 `style.surface` 被 `UiGenParser.ParseStyle()` 静默忽略；
`templates` 的 `tag` 是对整份 JSON 文本做 `IndexOf` 子串匹配，不是读 `tags` 字段。
`tags` 字段 `UiParser.Parse()` 完全不消费。

### 20. 模板目录硬编码 `[源码]`
`UiGenTool.cs:270` 写死 `Assets/Extends/CustomTools/UISourceGen/Templates`，
换工程即失效。本技能不依赖 `uisourcegen.templates`。

### 21. UISourceGen 写文件失败只记 warning `[源码]`
`UiGenTool.cs:129/140/146` 的 catch 只加 `result.Warnings`，仍返回 Success。
→ `assets.regenerate()` 自己检查 `layout.json` 与 PNG 是否真实存在。

### 22. `Background=true` 的动作禁止直接碰 Unity API `[源码]`
`ToolAttributes.cs:64-70`；渲染跑线程池，Unity API 必须 `CoroutineRunner.Enqueue` 回主线程。
`mcp.call()` 会自动轮询异步任务，不要手动拆。

---

## 四、规则与链路

### 23. `ui_rule.*` 在 Figma 扩展目录，不在三个目标目录 `[源码]`
`demo/Assets/Extends/CustomTools/Figma/Editor/UIRuleManage.cs`。
写进 `UIDefineRuleObject.asset` 的 `node_names` / `node_sprites` / `modify_records`。

### 24. `ui_rule.*` 没有代码层消费者 `[源码]`
三个目标目录没有任何文件引用 `UIDefineRuleObject` 或这些字段。
真正消费方是 AI Agent 的构建步骤与编辑器 Inspector。
→ 本技能始终落一份 `ui_rules.json`，不把「已记录到资产」当成「已被使用」。

### 25. `layout.json` 没有消费者 `[源码]`
仓库里没有任何 C# 读它来自动创建 UGUI。
`uisourcegen.templates` 返回模板 JSON **文本**，也不自动接 `render`。
→ 串联逻辑全在本技能，不指望模块之间自动衔接。

### 26. `figma.*` / 视觉解析是唯一「效果图 → 结构化」入口 `[实测]`
三个目录内没有任何读图片/调视觉模型/输出结构描述的 C#。
`scene_describe_full` 只是 `McpSettings.asset` 里给三维重建用的可配置提示词，
不是 UGUI 工具。

---

## 五、C# 与工程约束

### 27. `code.execute` 只接受语句片段，C#5 `[实测]`
`mode:"runtime"`；禁 `$"`、`?.`、`??=`、`nameof`、`out var`、`using static`、局部函数。
返回类型是 `object`，写 `return;` 直接 CS0126；输出用 `Debug.Log`。
本技能所有生成代码由 `mcp.assertLint()` 在提交前静态预检。

### 28. 工程里没有 SimpleJson / Newtonsoft `[实测]`
`SimpleJson.DeserializeObject` 直接 CS0103 并级联出一堆假错误。
→ 数据一律编成 C# 字面量数组，不运行时解析 JSON。

### 29. 委托元数必须严格匹配 `[实测]`
`Action<T…>` 泛型参数 = 入参个数；`Func<T…,R>` = 入参个数 + 1。
本技能用 `Action<string, Vector4>`（2 入参）、`Func<string, Sprite>`（1 入参 + 返回）等。

### 30. `hierarchy.find_by_query` 的坐标只有 1 位小数 `[实测]`
返回 yaml 里 `0.25` 显示成 `0.3`，**不能用于精确布局校验**。
→ `verify.js` 用 `code.execute` 反射读 `RectTransform`，写 `audit_raw.txt` 再回读比较。

### 31. 旧 Game 视图截图不可作为 UGUI 专用验收 `[实测]`
`camera.screenshot` 走相机渲染，不含 Overlay UI；`game_view.screenshot` 又曾返回
「JPEG 内容 + `.png` 扩展名」的黑图，导致 `png.js` 报签名不符。
→ 验收主路径改为 `scripts/preview.js` 的隔离 Canvas 独立相机渲染，
并检查 PNG 签名、尺寸、非空白和原场景状态；详见 `references/preview.md`。

### 32. 写操作响应丢失 ≠ 未执行 `[契约]`
域重载会清空请求台账，`status=unknown` 也不证明未执行。
流水线每阶段落 `stage_state.json`，中断后从 `--stage` 续跑，
**不盲目重放整段序列**；`request_id` 幂等由 `mcp.js` 保证。

### 33. 脚本必须幂等 `[实测]`
`build.js` 开头 `DestroyImmediate` 同名根对象；
`slice.js` 覆盖同名 PNG；`assets.importSprites()` 对同一路径重复导入无害。
连跑两次不应堆出多个同名 Canvas。

### 34. 不写死工程路径 `[契约]`
复用 `unity-modeling/scripts/mcp.js` 的 `resolveProject()`：
显式参数 → 环境变量 → `~/.unity-mcp/skill.ini` → cwd 上溯 → 技能目录上溯。
`check.js` 会扫描 `scripts/*.js` 的字符串字面量，出现盘符路径即失败。

### 35. `inspect.analyze(img, opt)` 的 opt 必须传 `[实测]`
复用底座 `inspect.js:218` 直接读 `opt.normal`，而 `opt` 没有默认值。
`analyze(img)` 会抛 `Cannot read properties of undefined (reading 'normal')`；
`slice.audit()` 里若用 try/catch 吞掉，会得到 `flat=null` 并让「非纯色」检查静默失效。
调用一律写 `inspect.analyze(img, {})`。`check.js` 有回归断言。

### 36. `anchoredPosition` 是 **pivot 点** 相对锚点的偏移，不是矩形中心 `[实测 bug，已修]`
`coords.toUGUI()` 旧版把 `pivot` 设成 `(anchorMin.x, anchorMin.y)`，
却用矩形中心 `cx,cy` 当 `anchoredPosition`。只有 `middle_center`（pivot=0.5,0.5）碰巧正确；
`top_left` / `bottom_right` 等贴边预设会整体漂移**自身宽高的一半**。
实测：`rect=[43,18,266,60]` 的面板在 Unity 里落在 `[176,48]`，偏 `+133,+30`；
文字、血条、底板各自偏不同的量，于是互相压盖，看起来像「层级混乱 + 重叠」。
→ 正确做法：先按 pivot 在矩形内取点 `px=left+pivot.x*w, py=bottom+pivot.y*h`，
再用它减锚点参考点。`check.js` §9 有断言，并保留一条「错误旧值必须被识破」的反例回归。

### 37. 嵌套节点必须用父容器局部坐标 `[实测]`
`SPEC.rect` 是整图绝对坐标，但 Unity 里 `anchoredPosition` 相对**直接父 RectTransform**。
`coords.frameOf()` 在有 `parent` 时把绝对坐标减去父容器左上角，并把锚点参考尺寸
从整屏换成父容器尺寸；`roundTrip()` 再加回父偏移，仍返回绝对坐标。
漏掉这一步时，文档要求「包含即父子」与实现互相矛盾，嵌套必错位。

### 38. 验收不能自证循环 `[实测 bug，已修]`
早期 `verify.js` 把「`coords.toUGUI()` 算出的期望值」与「Unity 回读值」比对，
而回读值正是同一个函数写进去的 —— 换算写错时写入与期望完全一致，
`布局坐标误差 0.00px`、`10/10 通过`，画面却全是错位和重叠。
→ 现在有两条独立证据链：
1. `geometry.audit(spec)` 纯几何检查包含/交叉/平铺率（不看任何 RectTransform）；
2. `coords.screenRectFromReadback()` 按 Unity 语义从回读值反算**屏幕矩形**，
   与 `SPEC.rect` 比对，不复用 `toUGUI`。
`verify.js` 的「屏幕几何位置（独立反算）」「层级：包含即父子」「同级控件无意外重叠」
三项断言必须同时通过。**数值自洽 ≠ 画面正确**，最后仍要 AI 目检专用预览 PNG。

### 39. 层级靠 `parent`，不靠绘制顺序；`SetSiblingIndex` 是同父局部序号 `[实测]`
旧版 `build.js` 直接把 Spec 的全局 `order` 塞进 `SetSiblingIndex`，
平铺时碰巧正确；一旦有 `parent` 分组就会越界被 Unity clamp 到末尾，绘制顺序全乱。
→ 现在按父分组计数，同父内从 0 连续递增。`geometry.audit()` 的 `flatRatio`
过高（≥0.8 且节点 ≥6）会给「层级过于扁平」提示。

### 40. 内置 Arial 不含中文字形 `[实测]`
`Resources.GetBuiltinResource<Font>("Arial.ttf")` 在中文界面上会出方框/缺字。
`build.js` 改为 `Font.CreateDynamicFontFromOSFont(字体名数组, 32)`，
优先 Microsoft YaHei UI / SimHei，失败才回退 Arial；
字体名可由 `palette.font_family` 覆盖，不写死绝对路径（铁律 34）。

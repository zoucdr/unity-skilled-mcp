# 独立 UGUI 预览（AI 可见）

## 问题

旧的 `game_view.screenshot` 依赖编辑器 Game 窗口状态。实测在 UGUI 制作流水线中，
它产出过一个 JPEG 内容、`.png` 扩展名的黑图；`png.js` 随后报「不是 PNG（签名不符）」。
即使截图文件存在，也不能证明 AI 能看到场景里的控件。

`uisourcegen.render` 的 `preview.png` 是控件素材合成图，不是场景中 UGUI 的最终渲染。
因此需要一套独立的 UGUI 预览。

## 实现：`scripts/preview.js`

`capture({ project, instance_id, canvasPath, width, height, shotFile })`

1. 校验 `canvasPath`、PNG 输出路径和尺寸；
2. 显式连接预期工程并锁定本次 `instance_id`；
3. 从活服务目录确认 `code.execute` 存在，读取实时 schema 确认 `mode=runtime`；
4. 在当前活动场景里按名称或层级路径查找唯一、已加载的根 Canvas；
5. 在 `EditorSceneManager.NewPreviewScene()` 里复制目标 Canvas；
6. 给副本挂独立正交相机，把 `ScreenSpaceOverlay` 副本改为 `ScreenSpaceCamera`；
7. 用 `RenderTexture` + `Texture2D.EncodeToPNG()` 输出真实 PNG；
8. 销毁所有临时对象、关闭预览场景、恢复 `RenderTexture.active` 和活动场景；
9. 回读原场景 dirty/rootCount/Canvas JSON/RectTransform JSON/Selection，防止预览污染场景；
10. 本地检查 PNG 签名、尺寸、可见像素数、颜色范围与是否空白；
11. 返回 `shotFile` 绝对路径，交给 AI 图片读取工具或 `present_files` 查看。

每次调用写唯一的 `ugui_preview-<uuid>.png`，避免把上一次截图误认成本次结果。

## 流水线集成

`pipeline.js --stage verify` 与全链路的 S5 自动调用 `preview.capture()`，
产物写入 `Assets/AI-Generate/UGUI/<界面名>/ugui_preview-<uuid>.png`。
`verify.run()` 新增「专用预览为有效非空白 PNG」检查；无效预览会让验收失败。
MAE 仍只作趋势，AI/人工必须实际查看预览图。

单独调用：

```js
const preview = require('./scripts/preview');
const result = await preview.capture({
  project: 'E:/unity3d-skilled-mcp/demo',
  instance_id: 'DISCOVER 返回的 instance_id',
  canvasPath: 'UGUI_RainbowBattleHUD',
  width: 1080, height: 608,
  shotFile: 'C:/work/ugui_preview.png',
});
console.log(result.shotFile);
```

## 2026-11-09 实测

对当前 `LoftBed_Showcase` 场景中的 `UGUI_RainbowBattleHUD` 调用：

- 输出 1080×608 有效 PNG；
- `visiblePixels=80751`、`blank=false`、颜色范围约 254.7；
- 原场景状态回读 `UGUI_PREVIEW_STATE_OK|dirty=True|roots=9`；
- 预览后原 Canvas、RectTransform、Selection 状态一致。

目检预览发现 HUD 的文字、面板、Sprite 均存在，但白色文字在透明/白底上对比度不足，
且部分元素近似不可见。这正是专用预览的价值：它让 AI 能发现控件存在性验收
通过、但视觉呈现不合格的问题。

## 边界

- 仅支持编辑模式下的根 `ScreenSpaceOverlay` / `ScreenSpaceCamera` Canvas；
  不支持 `WorldSpace`。
- 若目标包含自定义或缺失 `MonoBehaviour`，拒绝复制，避免生命周期副作用。
- 仅激活副本根节点；不验证按钮点击、动画、运行时数据绑定。
- 纯色 UI 可能被「空白」启发式拒绝，应加视觉层次或明确说明。
- 返回 `ok:true` 只证明预览 PNG 有效、非空白且场景状态未受破坏，
  不等于 UI 设计达标；仍需 AI/人工目检。

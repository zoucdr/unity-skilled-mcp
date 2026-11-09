'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const mcp = require('./mcp');
const PNG = require('./png');

function options(o = {}) {
  if (typeof o.canvasPath !== 'string' || !o.canvasPath.trim() || /[\r\n\0]/.test(o.canvasPath)) throw new Error('canvasPath 必须是明确的 Canvas 名称或层级路径');
  const width = Number(o.width || 1280), height = Number(o.height || 720);
  if (![width, height].every(n => Number.isInteger(n) && n > 0 && n <= 4096)) throw new Error('预览尺寸必须为 1..4096 的整数');
  if (!o.shotFile || path.extname(o.shotFile).toLowerCase() !== '.png') throw new Error('shotFile 必须是 PNG 路径');
  return { ...o, width, height, shotFile: path.resolve(o.shotFile) };
}

const cs = s => '"' + String(s).replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/[\u0000-\u001f\u2028\u2029]/g, c => '\\u' + c.charCodeAt(0).toString(16).padStart(4, '0')) + '"';

function generateCSharp(input) {
  const o = options(input);
  return `var originalScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) throw new System.Exception("预览仅支持编辑模式");
UnityEngine.Canvas source = null;
int matches = 0;
foreach (var candidate in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Canvas>()) {
  if (!candidate.gameObject.scene.IsValid() || !candidate.gameObject.scene.isLoaded || UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(candidate.gameObject.scene)) continue;
  string fullPath = candidate.name;
  for (var t = candidate.transform.parent; t != null; t = t.parent) fullPath = t.name + "/" + fullPath;
  if (fullPath == ${cs(o.canvasPath)} || (${cs(o.canvasPath)}.IndexOf('/') < 0 && candidate.name == ${cs(o.canvasPath)})) { source = candidate; matches++; }
}
if (matches != 1) throw new System.Exception("目标 Canvas 不存在或不唯一：" + matches);
if (!source.isRootCanvas) throw new System.Exception("请选择根 Canvas");
if (source.renderMode == UnityEngine.RenderMode.WorldSpace) throw new System.Exception("暂不支持 WorldSpace Canvas；不会改变原场景");
foreach (var behaviour in source.GetComponentsInChildren<UnityEngine.MonoBehaviour>(true)) {
  if (behaviour == null || behaviour.GetType().Assembly != typeof(UnityEngine.UI.Graphic).Assembly) throw new System.Exception("存在缺失或自定义脚本，拒绝复制以免触发生命周期副作用");
}
var originalDirty = originalScene.isDirty;
var originalRoots = originalScene.rootCount;
var originalSelection = UnityEditor.Selection.instanceIDs;
var originalCanvas = UnityEditor.EditorJsonUtility.ToJson(source);
var originalRects = new System.Collections.Generic.Dictionary<UnityEngine.RectTransform, string>();
foreach (var rect in source.GetComponentsInChildren<UnityEngine.RectTransform>(true)) originalRects.Add(rect, UnityEditor.EditorJsonUtility.ToJson(rect));
var sourceId = source.GetInstanceID();
var savedRT = UnityEngine.RenderTexture.active;
var previewScene = new UnityEngine.SceneManagement.Scene();
UnityEngine.GameObject holder = null;
UnityEngine.GameObject cameraObject = null;
UnityEngine.RenderTexture rt = null;
UnityEngine.Texture2D image = null;
try {
  previewScene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
  holder = new UnityEngine.GameObject("__UGUI_PREVIEW__");
  holder.SetActive(false);
  UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(holder, previewScene);
  holder.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
  var copy = UnityEngine.Object.Instantiate(source.gameObject, holder.transform, false);
  copy.name = "__UGUI_PREVIEW_CANVAS__";
  foreach (var t in copy.GetComponentsInChildren<UnityEngine.Transform>(true)) t.gameObject.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
  cameraObject = new UnityEngine.GameObject("__UGUI_PREVIEW_CAMERA__");
  UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, previewScene);
  cameraObject.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
  var camera = cameraObject.AddComponent<UnityEngine.Camera>();
  camera.enabled = false;
  camera.scene = previewScene;
  camera.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
  camera.backgroundColor = UnityEngine.Color.clear;
  camera.orthographic = true;
  camera.orthographicSize = ${o.height}f / 2f;
  camera.nearClipPlane = 0.1f;
  camera.farClipPlane = 1000f;
  camera.allowHDR = false;
  camera.allowMSAA = false;
  camera.cullingMask = -1;
  rt = new UnityEngine.RenderTexture(${o.width}, ${o.height}, 24, UnityEngine.RenderTextureFormat.ARGB32);
  rt.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
  rt.Create();
  camera.targetTexture = rt;
  var canvas = copy.GetComponent<UnityEngine.Canvas>();
  canvas.renderMode = UnityEngine.RenderMode.ScreenSpaceCamera;
  canvas.worldCamera = camera;
  canvas.planeDistance = 100f;
  canvas.targetDisplay = 0;
  foreach (var nested in copy.GetComponentsInChildren<UnityEngine.Canvas>(true)) nested.worldCamera = camera;
  foreach (var raycaster in copy.GetComponentsInChildren<UnityEngine.UI.GraphicRaycaster>(true)) raycaster.enabled = false;
  copy.SetActive(true);
  canvas.enabled = true;
  holder.SetActive(true);
  UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(copy.GetComponent<UnityEngine.RectTransform>());
  UnityEngine.Canvas.ForceUpdateCanvases();
  camera.Render();
  UnityEngine.RenderTexture.active = rt;
  image = new UnityEngine.Texture2D(${o.width}, ${o.height}, UnityEngine.TextureFormat.RGBA32, false);
  image.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
  image.ReadPixels(new UnityEngine.Rect(0, 0, ${o.width}, ${o.height}), 0, 0);
  image.Apply();
  System.IO.File.WriteAllBytes(${cs(o.shotFile.replace(/\\/g, '/'))}, image.EncodeToPNG());
} finally {
  UnityEngine.RenderTexture.active = savedRT;
  if (cameraObject != null) { var c = cameraObject.GetComponent<UnityEngine.Camera>(); if (c != null) c.targetTexture = null; }
  if (image != null) UnityEngine.Object.DestroyImmediate(image);
  if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
  if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
  if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
  if (previewScene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(previewScene);
  if (originalScene.IsValid() && UnityEngine.SceneManagement.SceneManager.GetActiveScene() != originalScene) UnityEngine.SceneManagement.SceneManager.SetActiveScene(originalScene);
}
if (originalScene.isDirty != originalDirty || originalScene.rootCount != originalRoots || UnityEditor.EditorJsonUtility.ToJson(source) != originalCanvas) throw new System.Exception("预览前后场景或 Canvas 状态不一致，请检查；未强行清除 dirty");
foreach (var pair in originalRects) if (pair.Key == null || UnityEditor.EditorJsonUtility.ToJson(pair.Key) != pair.Value) throw new System.Exception("预览前后原 RectTransform 状态不一致");
var selectionAfter = UnityEditor.Selection.instanceIDs;
if (selectionAfter.Length != originalSelection.Length) throw new System.Exception("预览前后选择状态不一致");
for (int i = 0; i < originalSelection.Length; i++) if (selectionAfter[i] != originalSelection[i]) throw new System.Exception("预览前后选择状态不一致");
UnityEngine.Debug.Log("UGUI_PREVIEW_STATE_OK|source=" + sourceId + "|dirty=" + originalDirty + "|roots=" + originalRoots);
UnityEngine.Debug.Log("UGUI_PREVIEW_OK");`;
}

function inspectPNG(file, expected = {}) {
  try {
    const img = PNG.readPNG(file);
    if ((expected.width && img.w !== expected.width) || (expected.height && img.h !== expected.height)) throw new Error('PNG 尺寸不符合请求');
    let visible = 0, min = [255, 255, 255], max = [0, 0, 0];
    for (let i = 0; i < img.w * img.h; i++) {
      const p = i * img.channels;
      const alpha = img.channels === 4 ? img.data[p + 3] : img.channels === 2 ? img.data[p + 1] : 255;
      if (alpha <= 2) continue;
      visible++;
      for (let c = 0; c < 3; c++) {
        const value = img.data[p + (img.channels <= 2 ? 0 : c)] * alpha / 255;
        min[c] = Math.min(min[c], value); max[c] = Math.max(max[c], value);
      }
    }
    const range = Math.max(...max.map((v, i) => v - min[i]));
    const blank = visible === 0 || range <= 2;
    return { ok: !blank, format: 'PNG', width: img.w, height: img.h, visiblePixels: visible, range, blank, reason: blank ? '透明、纯色或近纯色预览，不能作为界面验收证据' : null };
  } catch (e) { return { ok: false, reason: e.message }; }
}

async function capture(input) {
  const o = options(input);
  const client = require(mcp.loaderCandidates().find(f => fs.existsSync(f)));
  // 工程必须来自显式参数或调用方环境，不使用 INI 自报工程作为预期来源。
  const project = o.project || process.env.UNITY_PROJECT || process.env.UNITY_PROJECT_PATH;
  if (!project) throw new Error('独立预览必须显式指定 project');
  client.configure({ expected_project: project, instance_id: o.instance_id, timeout: 180 });
  await client.connectionInfo();
  const found = await client.findCapabilities('code.execute');
  if (!found.matches.some(t => t.category === 'tools' && t.name === 'code.execute')) throw new Error('当前服务未注册 code.execute，无法独立预览');
  const schema = await client.getSchema('code.execute');
  const properties = schema.inputSchema && schema.inputSchema.properties;
  if (!properties || !properties.code || !properties.mode || !properties.mode.enum.includes('runtime')) throw new Error('实时 code.execute schema 不支持 runtime 片段');
  if (!fs.existsSync(path.dirname(o.shotFile))) throw new Error('预览输出父目录不存在');
  // 每次使用唯一文件，失败不可能把上次截图误认成本次结果。
  const unique = o.shotFile.replace(/\.png$/i, '-' + crypto.randomUUID() + '.png');
  const code = generateCSharp({ ...o, shotFile: unique });
  mcp.assertLint(code);
  const raw = await client.callSkillJson('code.execute', { mode: 'runtime', code, description: '隔离复制现有 Canvas 并渲染专用 PNG 预览', return_output: true, timeout: 120 });
  if (!raw || raw.success === false || !raw.data || raw.data.success !== true || !String(raw.data.logs).includes('UGUI_PREVIEW_OK')) throw new Error('独立预览未成功：' + JSON.stringify(raw));
  const validation = inspectPNG(unique, o);
  return { ...validation, shotFile: unique, canvasPath: o.canvasPath, renderer: 'isolated-canvas', stateCheck: String(raw.data.logs).split('\n').find(s => s.includes('UGUI_PREVIEW_STATE_OK')), warnings: ['仅支持编辑模式的根屏幕空间 Canvas；仅激活副本根节点；纯色界面可能被空白检测拒绝；不验证交互行为'] };
}

module.exports = { options, generateCSharp, inspectPNG, capture };

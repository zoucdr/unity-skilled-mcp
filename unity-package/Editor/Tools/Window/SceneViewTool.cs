using System;
using System.IO;
using System.Reflection;
// Migrated from Newtonsoft.Json to SimpleJson
using UnityEditor;
using UnityEngine;
using UniMcp.Models;
using UniMcp.Utils;
namespace UniMcp.Tools
{
    /// <summary>
    /// Handles Unity Scene window management and control
    /// 对应方法名: scene_view
    /// </summary>
    [ToolModule("scene_view", GroupEn = "Window Management", GroupZh = "窗口管理")]
    public class SceneViewTool : ToolModuleBase
    {
        /// <summary>
        /// 工具方法的描述，用于在状态树中引用
        /// </summary>
        public override string Description => L.T("Manage and control Unity Scene view window", "管理和控制Unity场景视图窗口");

        /// <summary>
        /// Create the list of parameter keys supported by this method
        /// </summary>
        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                // Action type
                new MethodStr("action", L.T("Action type", "操作类型"), false)
                    .SetEnumValues("get_info", "focus", "maximize", "screenshot", "set_pivot", "set_rotation", "set_2d_mode", "align_with_view", "frame_selected"),
                
                // Screenshot save path
                new MethodStr("save_path", L.T("Screenshot save path", "截图保存路径"))
                    .AddExample("Assets/Screenshots/scene.png"),
                
                // Pivot position
                new MethodVector("pivot_position", L.T("Pivot position [x,y,z]", "枢轴位置 [x,y,z]")),
                
                // Camera rotation
                new MethodVector("rotation", L.T("Camera rotation [x,y,z]", "相机旋转 [x,y,z]")),
                
                // Orthographic mode
                new MethodBool("orthographic", L.T("Set orthographic mode", "设置正交模式")),
                
                // Align view direction
                new MethodStr("align_view", L.T("Align view direction", "对齐视图方向"))
                    .SetEnumValues("top", "bottom", "left", "right", "front", "back")
            };
        }

        [ToolAction("get_info", Inject = new string[] { "action=get_info" })]
        private object HandleGetInfoAction(JsonClass args)
        {
            try
            {
                SceneView sceneView = SceneView.lastActiveSceneView;
                if ((UnityEngine.Object)(object)sceneView == (UnityEngine.Object)null)
                {
                    return Response.Error("No active SceneView found.");
                }
                Vector3 pivot = sceneView.pivot;
                Quaternion rotation = sceneView.rotation;
                Vector3 eulerAngles = rotation.eulerAngles;
                float size = sceneView.size;
                bool orthographic = sceneView.orthographic;
                bool in2DMode = sceneView.in2DMode;
                Vector3 position = ((Component)sceneView.camera).transform.position;
                rotation = ((Component)sceneView.camera).transform.rotation;
                var camera = new
                {
                    position = position,
                    rotation = rotation.eulerAngles,
                    fieldOfView = sceneView.camera.fieldOfView
                };
                Rect position2 = ((EditorWindow)sceneView).position;
                float width = position2.width;
                position2 = ((EditorWindow)sceneView).position;
                float height = position2.height;
                position2 = ((EditorWindow)sceneView).position;
                float x = position2.x;
                position2 = ((EditorWindow)sceneView).position;
                var info = new
                {
                    pivot = pivot,
                    rotation = eulerAngles,
                    size = size,
                    orthographic = orthographic,
                    in2DMode = in2DMode,
                    camera = camera,
                    window = new { width, height, x, position2.y }
                };
                return Response.Success("SceneView information retrieved.", info);
            }
            catch (Exception ex)
            {
                Debug.LogError((object)("[SceneView] Error getting SceneView info: " + ex.Message));
                return Response.Error("Failed to get SceneView info: " + ex.Message);
            }
        }

        [ToolAction("focus", Inject = new string[] { "action=focus" })]
        /// <summary>
        /// 处理聚焦Scene窗口的操作
        /// </summary>
        private object HandleFocusAction(JsonClass args)
        {
            try
            {
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    return Response.Error("No active SceneView found.");
                }

                sceneView.Focus();
                return Response.Success("SceneView focused.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneView] Error focusing SceneView: {e.Message}");
                return Response.Error($"Failed to focus SceneView: {e.Message}");
            }
        }

        [ToolAction("maximize", Inject = new string[] { "action=maximize" })]
        /// <summary>
        /// 处理最大化Scene窗口的操作
        /// </summary>
        private object HandleMaximizeAction(JsonClass args)
        {
            try
            {
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    return Response.Error("No active SceneView found.");
                }

                sceneView.maximized = !sceneView.maximized;
                return Response.Success($"SceneView {(sceneView.maximized ? "maximized" : "restored")}.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneView] Error maximizing SceneView: {e.Message}");
                return Response.Error($"Failed to maximize SceneView: {e.Message}");
            }
        }

        [ToolAction("screenshot", Parameters = new string[] { "save_path" }, Required = new string[] { "save_path" }, Inject = new string[] { "action=screenshot" })]
        /// <summary>
        /// 处理Scene窗口截图的操作
        /// </summary>
        private object HandleScreenshotAction(JsonClass args)
        {
            try
            {
                var savePath = args["save_path"]?.Value;
                if (string.IsNullOrEmpty(savePath)) savePath = "Assets/Screenshots/sceneview_screenshot.jpg";

                Debug.Log($"[SceneView] Taking screenshot to {savePath}");

                var result = ScreenCaptureUtil.CaptureSceneView(savePath);

                if (!result.success)
                {
                    return Response.Error(result.error);
                }
                var data = new JsonClass
                {
                    { "path", result.path },
                    { "width", result.width },
                    { "height", result.height },
                    { "format", result.format },
                    { "size_bytes", result.size }
                };
                var resources = new JsonClass
                {
                    { "type", "image" },
                    { "path", result.path },
                    { "format", result.format }
                };
                return Response.Success("SceneView screenshot saved successfully", data, resources);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneView] Screenshot failed: {e.Message}");
                return Response.Error($"Screenshot failed: {e.Message}");
            }
        }

        [ToolAction("set_pivot", Parameters = new string[] { "pivot_position" }, Required = new string[] { "pivot_position" }, Inject = new string[] { "action=set_pivot" })]
        /// <summary>
        /// 处理设置Scene窗口pivot点的操作
        /// </summary>
        private object HandleSetPivotAction(JsonClass args)
        {
            try
            {
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    return Response.Error("No active SceneView found.");
                }

                var pivotArray = args["pivot_position"].AsArray;
                if (pivotArray == null || pivotArray.Count < 3)
                {
                    return Response.Error("Invalid pivot_position. Expected [x,y,z] array.");
                }

                float x = pivotArray[0].AsFloatDefault(0);
                float y = pivotArray[1].AsFloatDefault(0);
                float z = pivotArray[2].AsFloatDefault(0);

                Vector3 pivot = new Vector3(x, y, z);
                sceneView.pivot = pivot;
                sceneView.Repaint();

                return Response.Success($"SceneView pivot set to {pivot}.", new
                {
                    pivot = new { x, y, z }
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneView] Error setting pivot: {e.Message}");
                return Response.Error($"Failed to set pivot: {e.Message}");
            }
        }

        [ToolAction("set_rotation", Parameters = new string[] { "rotation" }, Required = new string[] { "rotation" }, Inject = new string[] { "action=set_rotation" })]
        /// <summary>
        /// 处理设置Scene窗口旋转的操作
        /// </summary>
        private object HandleSetRotationAction(JsonClass args)
        {
            try
            {
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    return Response.Error("No active SceneView found.");
                }

                var rotationArray = args["rotation"].AsArray;
                if (rotationArray == null || rotationArray.Count < 3)
                {
                    return Response.Error("Invalid rotation. Expected [x,y,z] array.");
                }

                float x = rotationArray[0].AsFloatDefault(0);
                float y = rotationArray[1].AsFloatDefault(0);
                float z = rotationArray[2].AsFloatDefault(0);

                Quaternion rotation = Quaternion.Euler(x, y, z);
                sceneView.rotation = rotation;
                sceneView.Repaint();

                return Response.Success($"SceneView rotation set to {rotation.eulerAngles}.", new
                {
                    rotation = new { x, y, z }
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneView] Error setting rotation: {e.Message}");
                return Response.Error($"Failed to set rotation: {e.Message}");
            }
        }

        [ToolAction("set_2d_mode", Parameters = new string[] { "orthographic" }, Required = new string[] { "orthographic" }, Inject = new string[] { "action=set_2d_mode" })]
        /// <summary>
        /// 处理设置Scene窗口2D模式的操作
        /// </summary>
        private object HandleSet2DModeAction(JsonClass args)
        {
            try
            {
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    return Response.Error("No active SceneView found.");
                }

                bool orthographic = args["orthographic"].AsBoolDefault(true);
                sceneView.orthographic = orthographic;

                // 设置2D模式
                sceneView.in2DMode = orthographic;
                sceneView.Repaint();

                return Response.Success($"SceneView 2D mode set to {orthographic}.", new
                {
                    orthographic = orthographic,
                    in2DMode = sceneView.in2DMode
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneView] Error setting 2D mode: {e.Message}");
                return Response.Error($"Failed to set 2D mode: {e.Message}");
            }
        }

        [ToolAction("align_with_view", Parameters = new string[] { "align_view" }, Required = new string[] { "align_view" }, Inject = new string[] { "action=align_with_view" })]
        /// <summary>
        /// 处理与视图对齐的操作
        /// </summary>
        private object HandleAlignWithViewAction(JsonClass args)
        {
            try
            {
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    return Response.Error("No active SceneView found.");
                }

                var alignView = args["align_view"]?.Value;
                if (string.IsNullOrEmpty(alignView))
                {
                    return Response.Error("align_view parameter is required.");
                }

                Quaternion rotation;
                switch (alignView.ToLower())
                {
                    case "top":
                        rotation = Quaternion.Euler(90, 0, 0);
                        break;
                    case "bottom":
                        rotation = Quaternion.Euler(-90, 0, 0);
                        break;
                    case "left":
                        rotation = Quaternion.Euler(0, -90, 0);
                        break;
                    case "right":
                        rotation = Quaternion.Euler(0, 90, 0);
                        break;
                    case "front":
                        rotation = Quaternion.Euler(0, 0, 0);
                        break;
                    case "back":
                        rotation = Quaternion.Euler(0, 180, 0);
                        break;
                    default:
                        return Response.Error("Invalid align_view value. Expected: top, bottom, left, right, front, back");
                }

                sceneView.rotation = rotation;
                sceneView.Repaint();

                return Response.Success($"SceneView aligned to {alignView} view.", new
                {
                    alignView = alignView,
                    rotation = rotation.eulerAngles
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneView] Error aligning view: {e.Message}");
                return Response.Error($"Failed to align view: {e.Message}");
            }
        }

        [ToolAction("frame_selected", Inject = new string[] { "action=frame_selected" })]
        /// <summary>
        /// 处理将视图帧定位到选中对象的操作
        /// </summary>
        private object HandleFrameSelectedAction(JsonClass args)
        {
            try
            {
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    return Response.Error("No active SceneView found.");
                }

                if (Selection.gameObjects.Length == 0)
                {
                    return Response.Error("No objects selected.");
                }

                // 使用SceneView的FrameSelected方法
                sceneView.FrameSelected();

                return Response.Success($"SceneView framed to selected objects ({Selection.gameObjects.Length}).");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneView] Error framing selected: {e.Message}");
                return Response.Error($"Failed to frame selected: {e.Message}");
            }
        }
    }
}

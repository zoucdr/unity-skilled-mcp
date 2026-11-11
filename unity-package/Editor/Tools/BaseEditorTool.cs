using System;
using System.Collections.Generic;
using System.Linq;
// Migrated from Newtonsoft.Json to SimpleJson
using UnityEditor;
using UnityEditorInternal; // Required for tag management
using UnityEngine;
using UniMcp.Models; // For Response class
using UniMcp;

namespace UniMcp.Tools
{
    /// <summary>
    /// Handles Unity Editor state management and controls.
    /// 对应方法名: base_editor
    /// </summary>
    [ToolModule("editor", GroupEn = "Editor", GroupZh = "编辑器",
        Tags = new[] { "editor-automation", "selection", "window-management", "inspector" })]
    public class BaseEditorTool : ToolModuleBase
    {
        public override string Description => L.T("Manage Unity Editor state and controls", "管理Unity编辑器状态和控制");

        /// <summary>
        /// Create the list of parameter keys supported by this method
        /// </summary>
        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                // Action type
                new MethodStr("action", L.T("Action type", "操作类型"), false)
                    .SetEnumValues("get_state", "get_windows", "get_selection", "execute_menu", "get_menu_items",
                        "refresh_assets", "save_assets", "set_auto_refresh", "set_run_in_background")
                    .AddExamples("get_state", "execute_menu", "set_auto_refresh"),
                
                // Wait for completion
                new MethodBool("wait_for_completion", L.T("Whether to wait for operation completion", "是否等待操作完成")),
                
                // Enabled state for toggles
                new MethodBool("enabled", L.T("Target enabled state", "目标启用状态")),
                
                // Menu path
                new MethodStr("menu_path", L.T("Menu path", "菜单路径"))
                    .AddExample("File/New Scene"),
                
                // Root menu path
                new MethodStr("root_path", L.T("Root menu path (used when getting menu items)", "根菜单路径（获取菜单项时使用）"))
                    .SetDefault(""),
                
                // Include submenus
                new MethodBool("include_submenus", L.T("Include submenus (used when getting menu items)", "包含子菜单（获取菜单项时使用）")),
                
                // Verify exists
                new MethodBool("verify_exists", L.T("Verify if menu item exists (used when getting menu items)", "验证菜单项是否存在（获取菜单项时使用）"))
            };
        }

        [ToolAction("get_state", Inject = new string[] { "action=get_state" })]
        private object HandleGetStateAction(JsonClass args)
        {
            McpLogger.Log("[BaseEditor] Getting editor state");
            return GetEditorState();
        }

        [ToolAction("get_windows", Inject = new string[] { "action=get_windows" })]
        private object HandleGetWindowsAction(JsonClass args)
        {
            McpLogger.Log("[BaseEditor] Getting editor windows");
            return GetEditorWindows();
        }

        [ToolAction("get_selection", Inject = new string[] { "action=get_selection" })]
        /// <summary>
        /// 处理获取选择对象的操作
        /// </summary>
        private object HandleGetSelectionAction(JsonClass args)
        {
            McpLogger.Log("[BaseEditor] Getting selection");
            return GetSelection();
        }

        // --- Editor State/Info Methods ---
        private object GetEditorState()
        {
            try
            {
                var state = new
                {
                    isPlaying = EditorApplication.isPlaying,
                    isPaused = EditorApplication.isPaused,
                    isCompiling = EditorApplication.isCompiling,
                    isUpdating = EditorApplication.isUpdating,
                    applicationPath = EditorApplication.applicationPath,
                    applicationContentsPath = EditorApplication.applicationContentsPath,
                    timeSinceStartup = EditorApplication.timeSinceStartup,
                    autoRefresh = GetAutoRefreshState(),
                    runInBackground = PlayerSettings.runInBackground,
                };
                return Response.Success("Retrieved editor state.", state);
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting editor state: {e.Message}");
            }
        }

        // --- Auto Refresh ---
        private const string AutoRefreshKey = "kAutoRefresh";
        private const string AutoRefreshModeKey = "kAutoRefreshMode";

        /// <summary>
        /// Unity 2020.1+ 使用 kAutoRefreshMode（0=禁用，1=启用，2=仅编辑模式启用），
        /// 更早的版本使用 kAutoRefresh 布尔值。
        /// </summary>
        private static object GetAutoRefreshState()
        {
            if (EditorPrefs.HasKey(AutoRefreshModeKey))
            {
                int mode = EditorPrefs.GetInt(AutoRefreshModeKey, 1);
                return new
                {
                    enabled = mode != 0,
                    mode = mode == 0 ? "disabled" : (mode == 2 ? "enabled_outside_playmode" : "enabled"),
                };
            }

            bool enabled = !EditorPrefs.HasKey(AutoRefreshKey) || EditorPrefs.GetBool(AutoRefreshKey, true);
            return new
            {
                enabled,
                mode = enabled ? "enabled" : "disabled",
            };
        }

        [ToolAction("set_auto_refresh", Parameters = new string[] { "enabled" }, Required = new string[] { "enabled" }, Inject = new string[] { "action=set_auto_refresh" })]
        /// <summary>
        /// 开关Unity资源导入的自动刷新
        /// </summary>
        private object HandleSetAutoRefreshAction(JsonClass args)
        {
            try
            {
                if (!HasArgument(args, "enabled"))
                    return Response.Error("'enabled' parameter is required for set_auto_refresh.");

                bool enabled = args["enabled"].AsBoolDefault(false);
                EditorPrefs.SetBool(AutoRefreshKey, enabled);
                EditorPrefs.SetInt(AutoRefreshModeKey, enabled ? 1 : 0);

                McpLogger.Log($"[BaseEditor] Auto refresh {(enabled ? "enabled" : "disabled")}");
                return Response.Success(
                    enabled ? "Auto refresh enabled." : "Auto refresh disabled.",
                    GetAutoRefreshState());
            }
            catch (Exception e)
            {
                return Response.Error($"Error setting auto refresh: {e.Message}");
            }
        }

        [ToolAction("refresh_assets", Inject = new string[] { "action=refresh_assets" })]
        /// <summary>
        /// 强制刷新AssetDatabase，即使关闭了自动刷新也会执行一次
        /// </summary>
        private object HandleRefreshAssetsAction(JsonClass args)
        {
            try
            {
                AssetDatabase.Refresh();
                McpLogger.Log("[BaseEditor] Asset database refresh requested");
                return Response.Success("Asset database refresh requested.", new
                {
                    isUpdating = EditorApplication.isUpdating,
                    isCompiling = EditorApplication.isCompiling,
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error refreshing assets: {e.Message}");
            }
        }

        [ToolAction("save_assets", Inject = new string[] { "action=save_assets" })]
        /// <summary>
        /// 保存所有已修改的资源，避免切换/关闭编辑器时丢失改动
        /// </summary>
        private object HandleSaveAssetsAction(JsonClass args)
        {
            try
            {
                AssetDatabase.SaveAssets();
                McpLogger.Log("[BaseEditor] Assets saved");
                return Response.Success("Assets saved.");
            }
            catch (Exception e)
            {
                return Response.Error($"Error saving assets: {e.Message}");
            }
        }

        [ToolAction("set_run_in_background", Parameters = new string[] { "enabled" }, Required = new string[] { "enabled" }, Inject = new string[] { "action=set_run_in_background" })]
        /// <summary>
        /// 开关后台运行（Player Settings 与 Application 同时生效）
        /// </summary>
        private object HandleSetRunInBackgroundAction(JsonClass args)
        {
            try
            {
                if (!HasArgument(args, "enabled"))
                    return Response.Error("'enabled' parameter is required for set_run_in_background.");

                bool enabled = args["enabled"].AsBoolDefault(false);
                PlayerSettings.runInBackground = enabled;
                Application.runInBackground = enabled;
                AssetDatabase.SaveAssets();

                McpLogger.Log($"[BaseEditor] Run in background {(enabled ? "enabled" : "disabled")}");
                return Response.Success(
                    enabled ? "Run in background enabled." : "Run in background disabled.",
                    new
                    {
                        playerSettingsRunInBackground = PlayerSettings.runInBackground,
                        applicationRunInBackground = Application.runInBackground,
                    });
            }
            catch (Exception e)
            {
                return Response.Error($"Error setting run in background: {e.Message}");
            }
        }

        private static bool HasArgument(JsonClass args, string key)
        {
            return args != null && args.ContainsKey(key) && args[key] != null && !args[key].IsNull();
        }


        private object GetEditorWindows()
        {
            try
            {
                // Get all types deriving from EditorWindow
                var windowTypes = AppDomain
                    .CurrentDomain.GetAssemblies()
                    .SelectMany(assembly => assembly.GetTypes())
                    .Where(type => type.IsSubclassOf(typeof(EditorWindow)))
                    .ToList();

                var openWindows = new List<object>();

                // Find currently open instances
                // Resources.FindObjectsOfTypeAll seems more reliable than GetWindow for finding *all* open windows
                EditorWindow[] allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();

                foreach (EditorWindow window in allWindows)
                {
                    if (window == null)
                        continue; // Skip potentially destroyed windows

                    try
                    {
                        openWindows.Add(
                            new
                            {
                                title = window.titleContent.text,
                                typeName = window.GetType().FullName,
                                isFocused = EditorWindow.focusedWindow == window,
                                position = new
                                {
                                    x = window.position.x,
                                    y = window.position.y,
                                    width = window.position.width,
                                    height = window.position.height,
                                },
                                instanceID = window.GetInstanceID(),
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogWarning(
                            $"Could not get info for window {window.GetType().Name}: {ex.Message}"
                        );
                    }
                }

                return Response.Success("Retrieved list of open editor windows.", openWindows);
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting editor windows: {e.Message}");
            }
        }

        private object GetSelection()
        {
            try
            {
                var selectionInfo = new
                {
                    activeObject = Selection.activeObject?.name,
                    activeGameObject = Selection.activeGameObject?.name,
                    activeTransform = Selection.activeTransform?.name,
                    activeInstanceID = Selection.activeInstanceID,
                    count = Selection.count,
                    objects = Selection
                        .objects.Select(obj => new
                        {
                            name = obj?.name,
                            type = obj?.GetType().FullName,
                            instanceID = obj?.GetInstanceID(),
                        })
                        .ToList(),
                    gameObjects = Selection
                        .gameObjects.Select(go => new
                        {
                            name = go?.name,
                            instanceID = go?.GetInstanceID(),
                        })
                        .ToList(),
                    assetGUIDs = Selection.assetGUIDs, // GUIDs for selected assets in Project view
                };

                return Response.Success("Retrieved current selection details.", selectionInfo);
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting selection: {e.Message}");
            }
        }

        // --- Example Implementations for Settings ---
        /*
        private object SetQualityLevel(JsonNode qualityLevelToken) { ... }
        */
        [ToolAction("execute_menu", Parameters = new string[] { "menu_path" }, Required = new string[] { "menu_path" }, Inject = new string[] { "action=execute_menu" })]
        public object ToolActionHandler_execute_menu(JsonClass args)
        {
            return MenuUtils.HandleExecuteMenu(args);
        }

        [ToolAction("get_menu_items", Parameters = new string[] { "root_path", "include_submenus" }, Inject = new string[] { "action=get_menu_items" })]
        public object ToolActionHandler_get_menu_items(JsonClass args)
        {
            return MenuUtils.HandleGetMenuItems(args);
        }

    }
}


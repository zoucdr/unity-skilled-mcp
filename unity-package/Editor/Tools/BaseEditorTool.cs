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
    [ToolModule("editor", GroupEn = "System Management", GroupZh = "系统管理")]
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
                    .SetEnumValues("get_state", "get_windows", "get_selection", "execute_menu", "get_menu_items")
                    .AddExamples("get_state", "execute_menu"),
                
                // Wait for completion
                new MethodBool("wait_for_completion", L.T("Whether to wait for operation completion", "是否等待操作完成")),
                
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
                };
                return Response.Success("Retrieved editor state.", state);
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting editor state: {e.Message}");
            }
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


// Real top-level tool class (split from a nested-class pseudo-merge).
// Discovered by ToolCatalog reflection via [ToolModule] / [ToolAction].

using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System;
using UniMcp.Models;
using UniMcp;
using UnityEditor.SceneManagement;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine.SceneManagement;
using UnityEngine;

namespace UniMcp.Tools
{
    [ToolModule("hierarchy", GroupEn = "Hierarchy Management", GroupZh = "层级管理")]
    public sealed class HierarchyCreateTool : ToolModuleBase
    {
        public override string Description => L.T("Hierarchy creation tool, supports creating various types of game objects in scene hierarchy", "层级创建工具，支持在场景层级中创建各种类型的游戏对象");

        /// <summary>
        /// Create the list of parameter keys supported by this method
        /// </summary>
        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                // GameObject name - required
                new MethodStr("name", L.T("GameObject name", "游戏对象名称"), false)
                    .AddExample("Player"),
                
                // Creation source type - enumeration
                new MethodStr("source", L.T("Template source", "模板来源"), false)
                    .SetEnumValues("primitive","menu", "prefab", "empty", "copy"),
                
                // GameObject tag
                new MethodStr("tag", L.T("GameObject tag", "游戏对象标签"))
                    .AddExample("Player"),
                
                // GameObject layer
                new MethodStr("layer", L.T("GameObject layer", "游戏对象层"))
                    .AddExample("Default"),
                
                // Parent object path
                new MethodStr("parent", L.T("Full hierarchy path of the parent object, e.g. Root/Group", "父对象的完整层级路径，例如 Root/Group"))
                    .AddExample("Canvas"),
                
                // Parent object ID
                new MethodStr("parent_id", L.T("Parent object unique ID", "父对象唯一ID"))
                    .AddExample("-1234"),
                
                // Transform values are LOCAL to the parent (see GameObjectUtils.ApplyCommonGameObjectSettings)
                new MethodVector("position", L.T("Local position relative to the parent [x, y, z], not world coordinates", "相对父对象的局部坐标 [x, y, z]，非世界坐标")),
                
                new MethodVector("rotation", L.T("Local Euler rotation relative to the parent [x, y, z] in degrees", "相对父对象的局部欧拉角 [x, y, z]，单位为度")),
                
                new MethodVector("scale", L.T("Local scale [x, y, z]", "局部缩放 [x, y, z]")),
                
                // Primitive type
                new MethodStr("primitive_type", L.T("Primitive type when source=primitive: Cube, Sphere, Cylinder, Capsule, Plane, Quad", "source=primitive 时的基元类型：Cube / Sphere / Cylinder / Capsule / Plane / Quad"))
                    .AddExample("Cube"),
                
                // Prefab path
                new MethodStr("prefab_path", L.T("Prefab path", "预制体路径"))
                    .AddExample("Assets/Prefabs/Player.prefab"),
                
                // Menu path
                new MethodStr("menu_path", L.T("Menu path", "菜单路径"))
                    .AddExample("GameObject/3D Object/Cube"),
                
                // Copy source object
                new MethodStr("copy_source", L.T("GameObject name to copy", "要复制的游戏对象名称"))
                    .AddExample("Player"),
                
                // Save as prefab
                new MethodBool("save_as_prefab", L.T("Whether to save as prefab", "是否保存为预制体")),
                
                // Set active state
                new MethodBool("set_active", L.T("Set active state", "设置激活状态"))
            };
        }

        private IEnumerator HandleCreateFromMenuAsync(ToolContext ctx)
        {
            string menuPath = ctx["menu_path"]?.ToString();
            if (string.IsNullOrEmpty(menuPath))
            {
                yield return Response.Error("'menu_path' parameter is required for menu creation.");
                yield break;
            }
            McpLogger.Log("[HierarchyCreate] Creating GameObject source menu: '" + menuPath + "'");
            if (!menuPath.StartsWith("GameObject"))
            {
                yield return Response.Error("'menu_path' parameter must start with 'GameObject'");
                yield break;
            }
            GameObject previousSelection = Selection.activeGameObject;
            int previousSelectionID = (((UnityEngine.Object)(object)previousSelection != (UnityEngine.Object)null) ? ((UnityEngine.Object)previousSelection).GetInstanceID() : 0);
            JsonClass menuResult = MenuUtils.TryExecuteMenuItem(menuPath);
            if (!menuResult["success"].AsBoolDefault(defaultValue: false))
            {
                McpLogger.Log($"[HierarchyCreate] Menu execution failed: {menuResult}");
                yield return menuResult;
                yield break;
            }
            GameObject newObject = null;
            int maxRetries = 10;
            int retryCount = 0;
            while (retryCount < maxRetries)
            {
                newObject = Selection.activeGameObject;
                if ((UnityEngine.Object)(object)newObject != (UnityEngine.Object)null && ((UnityEngine.Object)(object)previousSelection == (UnityEngine.Object)null || ((UnityEngine.Object)newObject).GetInstanceID() != previousSelectionID))
                {
                    McpLogger.Log($"[HierarchyCreate] Found newly created object: '{((UnityEngine.Object)newObject).name}' (ID: {((UnityEngine.Object)newObject).GetInstanceID()}) after {retryCount} retries");
                    break;
                }
                retryCount++;
                if (retryCount < maxRetries)
                {
                    yield return null;
                }
            }
            if ((UnityEngine.Object)(object)newObject != (UnityEngine.Object)null && ((UnityEngine.Object)(object)previousSelection == (UnityEngine.Object)null || ((UnityEngine.Object)newObject).GetInstanceID() != previousSelectionID))
            {
                yield return null;
                McpLogger.Log($"[HierarchyCreate] Finalizing newly created object: '{((UnityEngine.Object)newObject).name}' (ID: {((UnityEngine.Object)newObject).GetInstanceID()})");
                Selection.activeGameObject = null;
                EditorGUIUtility.editingTextField = false;
                GUIUtility.keyboardControl = 0;
                GUIUtility.keyboardControl = 0;
                Event escapeEvent = new Event();
                escapeEvent.type = (EventType)4;
                escapeEvent.keyCode = (KeyCode)27;
                EditorWindow focusedWindow = EditorWindow.focusedWindow;
                if (focusedWindow != null)
                {
                    focusedWindow.SendEvent(escapeEvent);
                }
                yield return null;
                object finalizeResult = FinalizeGameObjectCreation(ctx.JsonData, newObject, createdNewObject: false);
                McpLogger.Log($"[HierarchyCreate] Finalization result: {finalizeResult}");
                yield return finalizeResult;
            }
            else
            {
                object arg = maxRetries;
                string arg2 = ((previousSelection != null) ? ((UnityEngine.Object)previousSelection).name : null);
                GameObject val = newObject;
                McpLogger.Log($"[HierarchyCreate] Menu executed but no new object was detected after {arg} retries. Previous: {arg2}, Current: {((val != null) ? ((UnityEngine.Object)val).name : null)}");
                yield return Response.Success("Menu item '" + menuPath + "' executed successfully, but no new GameObject was detected.");
            }
        }

        [ToolAction("create_menu", Parameters = new string[] { "name", "tag", "layer", "parent", "parent_id", "position", "rotation", "scale", "prefab_path", "menu_path", "save_as_prefab" }, Required = new string[] { "name" }, Inject = new string[] { "source=menu" })]
        /// <summary>
        /// 处理从菜单创建GameObject的操作
        /// </summary>
        public object HandleCreateFromMenu(ToolContext ctx)
        {
            // 为层次结构创建操作设置超时时间（60秒）
            return ctx.AsyncReturn(HandleCreateFromMenuAsync(ctx), 60f);
        }

        [ToolAction("create_prefab", Parameters = new string[] { "name", "tag", "layer", "parent", "parent_id", "position", "rotation", "scale", "prefab_path", "save_as_prefab" }, Required = new string[] { "name" }, Inject = new string[] { "source=prefab" })]
        /// <summary>
        /// 处理从预制体创建GameObject的操作
        /// </summary>
        public object HandleCreateFromPrefab(JsonClass args)
        {
            string prefabPath = args["prefab_path"]?.Value;
            if (string.IsNullOrEmpty(prefabPath))
            {
                return Response.Error("'prefab_path' parameter is required for prefab instantiation.");
            }

            McpLogger.Log($"[HierarchyCreate] Creating GameObject source prefab: '{prefabPath}'");
            return CreateGameObjectFromPrefab(args, prefabPath);
        }

        [ToolAction("create_primitive", Parameters = new string[] { "name", "tag", "layer", "parent", "parent_id", "position", "rotation", "scale", "primitive_type", "prefab_path", "save_as_prefab" }, Required = new string[] { "name" }, Inject = new string[] { "source=primitive" })]
        /// <summary>
        /// 处理从基元类型创建GameObject的操作
        /// </summary>
        public object HandleCreateFromPrimitive(JsonClass args)
        {
            string primitiveType = args["primitive_type"]?.Value;
            if (string.IsNullOrEmpty(primitiveType))
            {
                // 默认使用Cube作为基元类型
                primitiveType = "Cube";
                McpLogger.Log("[HierarchyCreate] No primitive_type specified, using default: Cube");
            }

            McpLogger.Log($"[HierarchyCreate] Creating GameObject source primitive: '{primitiveType}'");
            return CreateGameObjectFromPrimitive(args, primitiveType);
        }

        [ToolAction("create_cube", Parameters = new string[] { "name", "tag", "layer", "parent", "parent_id", "position", "rotation", "scale", "prefab_path", "save_as_prefab" }, Required = new string[] { "name" }, Inject = new string[] { "source=primitive", "primitive_type=Cube" })]
        /// <summary>
        /// 处理创建Cube的操作
        /// </summary>
        public object HandleCreateCube(JsonClass args)
        {
            McpLogger.Log("[HierarchyCreate] Creating Cube primitive using specialized handler");
            return CreateGameObjectFromPrimitive(args, "Cube");
        }

        [ToolAction("create_sphere", Parameters = new string[] { "name", "tag", "layer", "parent", "parent_id", "position", "rotation", "scale", "prefab_path", "save_as_prefab" }, Required = new string[] { "name" }, Inject = new string[] { "source=primitive", "primitive_type=Sphere" })]
        /// <summary>
        /// 处理创建Sphere的操作
        /// </summary>
        public object HandleCreateSphere(JsonClass args)
        {
            McpLogger.Log("[HierarchyCreate] Creating Sphere primitive using specialized handler");
            return CreateGameObjectFromPrimitive(args, "Sphere");
        }

        [ToolAction("create_cylinder", Parameters = new string[] { "name", "tag", "layer", "parent", "parent_id", "position", "rotation", "scale", "prefab_path", "save_as_prefab" }, Required = new string[] { "name" }, Inject = new string[] { "source=primitive", "primitive_type=Cylinder" })]
        /// <summary>
        /// 处理创建Cylinder的操作
        /// </summary>
        public object HandleCreateCylinder(JsonClass args)
        {
            McpLogger.Log("[HierarchyCreate] Creating Cylinder primitive using specialized handler");
            return CreateGameObjectFromPrimitive(args, "Cylinder");
        }

        [ToolAction("create_capsule", Parameters = new string[] { "name", "tag", "layer", "parent", "parent_id", "position", "rotation", "scale", "prefab_path", "save_as_prefab" }, Required = new string[] { "name" }, Inject = new string[] { "source=primitive", "primitive_type=Capsule" })]
        /// <summary>
        /// 处理创建Capsule的操作
        /// </summary>
        public object HandleCreateCapsule(JsonClass args)
        {
            McpLogger.Log("[HierarchyCreate] Creating Capsule primitive using specialized handler");
            return CreateGameObjectFromPrimitive(args, "Capsule");
        }

        [ToolAction("create_plane", Parameters = new string[] { "name", "tag", "layer", "parent", "parent_id", "position", "rotation", "scale", "prefab_path", "save_as_prefab" }, Required = new string[] { "name" }, Inject = new string[] { "source=primitive", "primitive_type=Plane" })]
        /// <summary>
        /// 处理创建Plane的操作
        /// </summary>
        public object HandleCreatePlane(JsonClass args)
        {
            McpLogger.Log("[HierarchyCreate] Creating Plane primitive using specialized handler");
            return CreateGameObjectFromPrimitive(args, "Plane");
        }

        [ToolAction("create_quad", Parameters = new string[] { "name", "tag", "layer", "parent", "parent_id", "position", "rotation", "scale", "prefab_path", "save_as_prefab" }, Required = new string[] { "name" }, Inject = new string[] { "source=primitive", "primitive_type=Quad" })]
        /// <summary>
        /// 处理创建Quad的操作
        /// </summary>
        public object HandleCreateQuad(JsonClass args)
        {
            McpLogger.Log("[HierarchyCreate] Creating Quad primitive using specialized handler");
            return CreateGameObjectFromPrimitive(args, "Quad");
        }

        [ToolAction("create_empty", Parameters = new string[] { "name", "tag", "layer", "parent", "parent_id", "position", "rotation", "scale", "prefab_path", "save_as_prefab" }, Required = new string[] { "name" }, Inject = new string[] { "source=empty" })]
        /// <summary>
        /// 处理创建空GameObject的操作（直接代码创建，智能处理UI/非UI对象）
        /// </summary>
        public object HandleCreateEmpty(JsonClass args)
        {
            string name = args["name"]?.Value;
            if (string.IsNullOrEmpty(name))
            {
                name = "GameObject";
                McpLogger.Log("[HierarchyCreate] No name specified for empty GameObject, using default: 'GameObject'");
            }

            McpLogger.Log($"[HierarchyCreate] Creating empty GameObject: '{name}'");

            try
            {
                // 预先选中父对象（如果指定了）
                GameObjectUtils.PreselectParentIfSpecified(args, McpLogger.Log);

                // 获取父对象
                GameObject parentObject = Selection.activeGameObject;

                // 检查父对象是否有RectTransform（UI元素）
                bool parentIsUI = parentObject != null && parentObject.GetComponent<RectTransform>() != null;

                McpLogger.Log($"[HierarchyCreate] Parent is UI: {parentIsUI}, creating appropriate empty object");

                GameObject newGo = null;

                if (parentIsUI)
                {
                    // 在UI父对象下创建，需要添加RectTransform
                    newGo = new GameObject(name, typeof(RectTransform));

                    // 设置父对象（第二个参数false表示保持本地坐标）
                    if (parentObject != null)
                    {
                        newGo.transform.SetParent(parentObject.transform, false);
                    }

                    McpLogger.Log($"[HierarchyCreate] Created UI GameObject with RectTransform: '{newGo.name}'");
                }
                else
                {
                    // 普通对象，直接创建
                    newGo = new GameObject(name);

                    // 设置父对象（如果有）
                    if (parentObject != null)
                    {
                        newGo.transform.SetParent(parentObject.transform, true);
                    }

                    McpLogger.Log($"[HierarchyCreate] Created standard GameObject: '{newGo.name}'");
                }

                // 注册撤销操作
                Undo.RegisterCreatedObjectUndo(newGo, $"Create Empty GameObject '{newGo.name}'");

                McpLogger.Log($"[HierarchyCreate] Finalizing empty object: '{newGo.name}' (ID: {newGo.GetInstanceID()})");

                // 应用其他设置（名称、位置等）
                return FinalizeGameObjectCreation(args, newGo, true);
            }
            catch (Exception e)
            {
                McpLogger.Log($"[HierarchyCreate] Failed to create empty GameObject '{name}': {e.Message}");
                return Response.Error($"Failed to create empty GameObject '{name}': {e.Message}");
            }
        }

        [ToolAction("create_copy", Parameters = new string[] { "name", "tag", "layer", "parent", "parent_id", "position", "rotation", "scale", "prefab_path", "copy_source", "save_as_prefab" }, Required = new string[] { "name" }, Inject = new string[] { "source=copy" })]
        /// <summary>
        /// 处理从现有对象复制的操作
        /// </summary>
        public object HandleCreateFromCopy(JsonClass args)
        {
            string copySource = args["copy_source"]?.Value;
            if (string.IsNullOrEmpty(copySource))
            {
                return Response.Error("'copy_source' parameter is required for copy creation.");
            }

            McpLogger.Log($"[HierarchyCreate] Copying GameObject from source: '{copySource}'");
            return CreateGameObjectFromCopy(args, copySource);
        }

        // --- Core Creation Methods ---

        /// <summary>
        /// 从预制体创建GameObject
        /// </summary>
        private object CreateGameObjectFromPrefab(JsonClass args, string prefabPath)
        {
            try
            {
                // 预先选中父对象（如果指定了）
                GameObjectUtils.PreselectParentIfSpecified(args, McpLogger.Log);

                // 处理预制体路径查找逻辑
                string resolvedPath = ResolvePrefabPath(prefabPath);
                if (string.IsNullOrEmpty(resolvedPath))
                {
                    McpLogger.Log($"[HierarchyCreate] Prefab not found at path: '{prefabPath}'");
                    return Response.Error($"Prefab not found at path: '{prefabPath}'");
                }

                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(resolvedPath);
                if (prefabAsset == null)
                {
                    McpLogger.Log($"[HierarchyCreate] Failed to load prefab asset at: '{resolvedPath}'");
                    return Response.Error($"Failed to load prefab asset at: '{resolvedPath}'");
                }

                // 实例化预制体
                GameObject newGo = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;
                if (newGo == null)
                {
                    McpLogger.Log($"[HierarchyCreate] Failed to instantiate prefab: '{resolvedPath}'");
                    return Response.Error($"Failed to instantiate prefab: '{resolvedPath}'");
                }

                // 等待Unity完成对象初始化
                //Thread.Sleep(10);

                // 设置名称
                string name = args["name"]?.Value;
                if (!string.IsNullOrEmpty(name))
                {
                    newGo.name = name;
                }

                // 注册撤销操作
                Undo.RegisterCreatedObjectUndo(newGo, $"Instantiate Prefab '{prefabAsset.name}' as '{newGo.name}'");
                McpLogger.Log($"[HierarchyCreate] Instantiated prefab '{prefabAsset.name}' source path '{resolvedPath}' as '{newGo.name}'");

                return FinalizeGameObjectCreation(args, newGo, false);
            }
            catch (Exception e)
            {
                McpLogger.Log($"[HierarchyCreate] Error instantiating prefab '{prefabPath}': {e.Message}");
                return Response.Error($"Error instantiating prefab '{prefabPath}': {e.Message}");
            }
        }

        /// <summary>
        /// 从基元类型创建GameObject
        /// </summary>
        private object CreateGameObjectFromPrimitive(JsonClass args, string primitiveType)
        {
            try
            {
                // 预先选中父对象（如果指定了）
                GameObjectUtils.PreselectParentIfSpecified(args, McpLogger.Log);

                PrimitiveType type = (PrimitiveType)Enum.Parse(typeof(PrimitiveType), primitiveType, true);
                GameObject newGo = GameObject.CreatePrimitive(type);

                // 等待Unity完成对象初始化
                //Thread.Sleep(10);

                // 设置名称
                string name = args["name"]?.Value;
                if (!string.IsNullOrEmpty(name))
                {
                    newGo.name = name;
                }
                else
                {
                    McpLogger.Log("[HierarchyCreate] 'name' parameter is recommended when creating a primitive.");
                }

                // 注册撤销操作
                Undo.RegisterCreatedObjectUndo(newGo, $"Create GameObject '{newGo.name}'");
                return FinalizeGameObjectCreation(args, newGo, true);
            }
            catch (ArgumentException)
            {
                McpLogger.Log($"[HierarchyCreate] Invalid primitive type: '{primitiveType}'. Valid types: {string.Join(", ", Enum.GetNames(typeof(PrimitiveType)))}");
                return Response.Error($"Invalid primitive type: '{primitiveType}'. Valid types: {string.Join(", ", Enum.GetNames(typeof(PrimitiveType)))}");
            }
            catch (Exception e)
            {
                McpLogger.Log($"[HierarchyCreate] Failed to create primitive '{primitiveType}': {e.Message}");
                return Response.Error($"Failed to create primitive '{primitiveType}': {e.Message}");
            }
        }


        /// <summary>
        /// 从现有GameObject复制创建新对象
        /// </summary>
        private object CreateGameObjectFromCopy(JsonClass args, string copySource)
        {
            try
            {
                // 预先选中父对象（如果指定了）
                GameObjectUtils.PreselectParentIfSpecified(args, McpLogger.Log);

                // 查找源对象
                GameObject sourceObject = GameObject.Find(copySource);
                if (sourceObject == null)
                {
                    // 如果直接查找失败，尝试在场景中搜索
                    GameObject[] allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
                    sourceObject = allObjects.FirstOrDefault(go =>
                        go.name.Equals(copySource, StringComparison.OrdinalIgnoreCase));

                    if (sourceObject == null)
                    {
                        McpLogger.Log($"[HierarchyCreate] Source GameObject '{copySource}' not found in scene");
                        return Response.Error($"Source GameObject '{copySource}' not found in scene");
                    }
                }

                McpLogger.Log($"[HierarchyCreate] Found source object: '{sourceObject.name}' (ID: {sourceObject.GetInstanceID()})");

                // 复制对象
                GameObject newGo = UnityEngine.Object.Instantiate(sourceObject);

                if (newGo == null)
                {
                    McpLogger.Log($"[HierarchyCreate] Failed to instantiate copy of '{copySource}'");
                    return Response.Error($"Failed to instantiate copy of '{copySource}'");
                }

                // 等待Unity完成对象初始化
                //Thread.Sleep(10);

                // 设置名称
                string name = args["name"]?.Value;
                if (!string.IsNullOrEmpty(name))
                {
                    newGo.name = name;
                }
                else
                {
                    // 默认使用源对象名称（Unity会自动添加(Clone)后缀）
                    McpLogger.Log($"[HierarchyCreate] No name specified, copied object named: '{newGo.name}'");
                }

                // 注册撤销操作
                Undo.RegisterCreatedObjectUndo(newGo, $"Copy GameObject '{sourceObject.name}' as '{newGo.name}'");

                McpLogger.Log($"[HierarchyCreate] Successfully copied '{sourceObject.name}' to '{newGo.name}' (ID: {newGo.GetInstanceID()})");

                return FinalizeGameObjectCreation(args, newGo, true);
            }
            catch (Exception e)
            {
                McpLogger.Log($"[HierarchyCreate] Error copying GameObject '{copySource}': {e.Message}");
                return Response.Error($"Error copying GameObject '{copySource}': {e.Message}");
            }
        }

        /// <summary>
        /// 解析预制体路径
        /// </summary>
        private string ResolvePrefabPath(string prefabPath)
        {
            // 如果没有路径分隔符且没有.prefab扩展名，搜索预制体
            if (!prefabPath.Contains("/") && !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                string prefabNameOnly = prefabPath;
                McpLogger.Log($"[HierarchyCreate] Searching for prefab named: '{prefabNameOnly}'");

                string[] guids = AssetDatabase.FindAssets($"t:Prefab {prefabNameOnly}");
                if (guids.Length == 0)
                {
                    return null; // 未找到
                }
                else if (guids.Length > 1)
                {
                    string foundPaths = string.Join(", ", guids.Select(g => AssetDatabase.GUIDToAssetPath(g)));
                    McpLogger.Log($"[HierarchyCreate] Multiple prefabs found matching name '{prefabNameOnly}': {foundPaths}. Using first one.");
                }

                string resolvedPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                McpLogger.Log($"[HierarchyCreate] Found prefab at path: '{resolvedPath}'");
                return resolvedPath;
            }
            else if (!prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                // 自动添加.prefab扩展名
                McpLogger.Log($"[HierarchyCreate] Adding .prefab extension to path: '{prefabPath}'");
                return prefabPath + ".prefab";
            }

            return prefabPath;
        }

        /// <summary>
        /// 完成GameObject创建的通用设置
        /// </summary>
        private object FinalizeGameObjectCreation(JsonClass args, GameObject newGo, bool createdNewObject)
        {
            if (newGo == null)
            {
                return Response.Error("GameObject creation failed.");
            }

            try
            {
                McpLogger.Log($"[HierarchyCreate] Starting finalization for '{newGo.name}' (ID: {newGo.GetInstanceID()})");

                // 记录变换和属性的变更
                Undo.RecordObject(newGo.transform, "Set GameObject Transform");
                Undo.RecordObject(newGo, "Set GameObject Properties");

                // 应用通用设置（包括名称设置）
                GameObjectUtils.ApplyCommonGameObjectSettings(args, newGo, McpLogger.Log);

                McpLogger.Log($"[HierarchyCreate] Applied settings to '{newGo.name}' (ID: {newGo.GetInstanceID()})");

                // 处理预制体保存
                GameObject finalInstance = newGo;
                bool saveAsPrefab = args["save_as_prefab"].AsBoolDefault(false);

                if (createdNewObject && saveAsPrefab)
                {
                    finalInstance = HandlePrefabSaving(args, newGo);
                    if (finalInstance == null)
                    {
                        return Response.Error("Failed to save GameObject as prefab.");
                    }
                }

                // 取消选中并退出重命名模式
                Selection.activeGameObject = null;

                // 延迟调用确保退出重命名状态
                EditorApplication.delayCall += () =>
                {
                    // 多种方法尝试退出编辑状态
                    Selection.activeGameObject = null;
                    EditorGUIUtility.editingTextField = false;

                    // 发送ESC键事件来退出编辑
                    Event escapeEvent = new Event();
                    escapeEvent.type = EventType.KeyDown;
                    escapeEvent.keyCode = KeyCode.Escape;
                    EditorWindow.focusedWindow?.SendEvent(escapeEvent);

                    // 强制结束编辑状态
                    GUIUtility.keyboardControl = 0;
                    EditorGUIUtility.keyboardControl = 0;

                    // 刷新相关窗口
                    EditorApplication.RepaintHierarchyWindow();
                    if (EditorWindow.focusedWindow != null)
                    {
                        EditorWindow.focusedWindow.Repaint();
                    }
                };

                McpLogger.Log($"[HierarchyCreate] Finalized '{finalInstance.name}' (ID: {finalInstance.GetInstanceID()})");

                // 生成成功消息
                string successMessage = GenerateCreationSuccessMessage(args, finalInstance, createdNewObject, saveAsPrefab);
                return Response.Success(successMessage, GameObjectUtils.GetGameObjectData(finalInstance));
            }
            catch (Exception e)
            {
                LogError($"[HierarchyCreate] Error finalizing GameObject creation: {e.Message}");
                // 清理失败的对象
                if (newGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(newGo);
                }
                return Response.Error($"Error finalizing GameObject creation: {e.Message}");
            }
        }


        /// <summary>
        /// 处理预制体保存
        /// </summary>
        private GameObject HandlePrefabSaving(JsonClass args, GameObject newGo)
        {
            string prefabPath = args["prefab_path"]?.Value;
            if (string.IsNullOrEmpty(prefabPath))
            {
                McpLogger.Log("[HierarchyCreate] 'prefab_path' is required when 'save_as_prefab' is true.");
                return null;
            }

            string finalPrefabPath = prefabPath;
            if (!finalPrefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                McpLogger.Log($"[HierarchyCreate] Adding .prefab extension to save path: '{finalPrefabPath}'");
                finalPrefabPath += ".prefab";
            }

            try
            {
                // 确保目录存在
                string directoryPath = System.IO.Path.GetDirectoryName(finalPrefabPath);
                if (!string.IsNullOrEmpty(directoryPath) && !System.IO.Directory.Exists(directoryPath))
                {
                    System.IO.Directory.CreateDirectory(directoryPath);
                    AssetDatabase.Refresh();
                    McpLogger.Log($"[HierarchyCreate] Created directory for prefab: {directoryPath}");

                    // 等待Unity完成目录创建
                    //Thread.Sleep(50);
                }

                // 保存为预制体
                GameObject finalInstance = PrefabUtility.SaveAsPrefabAssetAndConnect(
                    newGo,
                    finalPrefabPath,
                    InteractionMode.UserAction
                );

                if (finalInstance == null)
                {
                    UnityEngine.Object.DestroyImmediate(newGo);
                    return null;
                }

                // 等待预制体保存完成
                //Thread.Sleep(10);

                McpLogger.Log($"[HierarchyCreate] GameObject '{newGo.name}' saved as prefab to '{finalPrefabPath}' and instance connected.");
                return finalInstance;
            }
            catch (Exception e)
            {
                McpLogger.Log($"[HierarchyCreate] Error saving prefab '{finalPrefabPath}': {e.Message}");
                UnityEngine.Object.DestroyImmediate(newGo);
                return null;
            }
        }

        /// <summary>
        /// 生成创建成功消息
        /// </summary>
        private string GenerateCreationSuccessMessage(JsonClass args, GameObject finalInstance, bool createdNewObject, bool saveAsPrefab)
        {
            string messagePrefabPath = AssetDatabase.GetAssetPath(
                PrefabUtility.GetCorrespondingObjectFromSource(finalInstance) ?? (UnityEngine.Object)finalInstance
            );

            if (!createdNewObject && !string.IsNullOrEmpty(messagePrefabPath))
            {
                return $"Prefab '{messagePrefabPath}' instantiated successfully as '{finalInstance.name}'.";
            }
            else if (createdNewObject && saveAsPrefab && !string.IsNullOrEmpty(messagePrefabPath))
            {
                return $"GameObject '{finalInstance.name}' created and saved as prefab to '{messagePrefabPath}'.";
            }
            else
            {
                return $"GameObject '{finalInstance.name}' created successfully in scene.";
            }
        }
    
}
    }

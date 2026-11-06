// Real top-level tool class (split from a nested-class pseudo-merge).
// Discovered by ToolCatalog reflection via [ToolModule] / [ToolAction].

using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System;
using UniMcp.Models;
using UniMcp.Tools;
using UniMcp;
using UnityEditor;
using UnityEngine;

namespace UniMcp.Tools
{
    [ToolModule("project", GroupEn = "Project Management", GroupZh = "项目管理", LegacyName = "project_create")]
    [ToolName("project_create", "Project Management", "项目管理")]
    public sealed class ProjectCreateTool : ToolModuleBase
    {
        public override string Description => L.T("Create assets in the Project window", "在项目窗口中创建资源");

        /// <summary>
        /// Create the list of parameter keys supported by this method
        /// </summary>
        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                // Asset file name - required
                new MethodStr("name", L.T("Asset file name", "资源文件名称"), false)
                    .AddExample("MyScript"),
                
                // Creation source type - enum
                new MethodStr("source", L.T("Operation type", "操作类型"), false)
                    .SetEnumValues("menu", "empty", "template", "copy"),
                
                // Target folder path
                new MethodStr("folder_path", L.T("Target folder path (relative to Assets)", "目标文件夹路径（相对于Assets）"))
                    .AddExample("Scripts"),
                
                // Menu path
                new MethodStr("menu_path", L.T("Menu path", "菜单路径"))
                    .AddExample("Assets/Create/C# Script"),
                
                // Template file path
                new MethodStr("template_path", L.T("Template file path", "模板文件路径"))
                    .AddExample("Assets/Templates/ScriptTemplate.cs"),
                
                // Copy source path
                new MethodStr("copy_source", L.T("Asset path to copy", "要复制的资源路径"))
                    .AddExample("Assets/Scripts/BaseScript.cs"),
                
                // File extension
                new MethodStr("extension", L.T("File extension (without .)", "文件扩展名（不带.）"))
                    .SetEnumValues("cs", "mat", "prefab", "asset", "txt", "json"),
                
                // File content
                new MethodStr("content", L.T("File content (for empty type)", "文件内容（用于空类型）"))
                    .AddExample("// New C# Script"),
                
                // Force overwrite
                new MethodBool("force", L.T("Force overwrite existing file", "强制覆盖现有文件")),
                
                // Open after create
                new MethodBool("open_after_create", L.T("Open file after creation", "创建后打开文件")),
                
                // Select after create
                new MethodBool("select_after_create", L.T("Select file after creation", "创建后选中文件"))
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
            McpLogger.Log("[ProjectCreate] Creating asset from menu: '" + menuPath + "'");
            if (!menuPath.StartsWith("Assets"))
            {
                yield return Response.Error("'menu_path' parameter must start with 'Assets'");
                yield break;
            }
            string folderPath = ctx["folder_path"]?.ToString() ?? "Assets";
            if (!folderPath.StartsWith("Assets"))
            {
                folderPath = "Assets/" + folderPath;
            }
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                McpLogger.Log("[ProjectCreate] Creating folder path: " + folderPath);
                CreateFolderRecursive(folderPath);
            }
            UnityEngine.Object[] previousSelection = Selection.objects;
            UnityEngine.Object folder = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folderPath);
            if (folder != (UnityEngine.Object)null)
            {
                Selection.activeObject = folder;
                McpLogger.Log("[ProjectCreate] Selected folder: " + folderPath);
            }
            else
            {
                McpLogger.Log("[ProjectCreate] Failed to select folder: " + folderPath);
            }
            JsonClass menuResult = MenuUtils.TryExecuteMenuItem(menuPath);
            if (!menuResult["success"].AsBoolDefault(defaultValue: false))
            {
                McpLogger.Log($"[ProjectCreate] Menu execution failed: {menuResult}");
                yield return menuResult;
                yield break;
            }
            UnityEngine.Object newAsset = null;
            int maxRetries = 10;
            int retryCount = 0;
            while (retryCount < maxRetries)
            {
                UnityEngine.Object[] currentSelection = Selection.objects;
                UnityEngine.Object[] array = currentSelection;
                foreach (UnityEngine.Object obj in array)
                {
                    if (obj != (UnityEngine.Object)null && !Enumerable.Contains(previousSelection, obj))
                    {
                        newAsset = obj;
                        McpLogger.Log("[ProjectCreate] Found newly created asset: '" + AssetDatabase.GetAssetPath(newAsset) + "'");
                        break;
                    }
                }
                if (newAsset != (UnityEngine.Object)null)
                {
                    break;
                }
                retryCount++;
                if (retryCount < maxRetries)
                {
                    yield return null;
                }
            }
            if (newAsset != (UnityEngine.Object)null)
            {
                string assetPath = AssetDatabase.GetAssetPath(newAsset);
                string fileName = ctx["name"]?.ToString();
                if (!string.IsNullOrEmpty(fileName))
                {
                    string newPath = Path.Combine(path2: fileName + Path.GetExtension(assetPath), path1: Path.GetDirectoryName(assetPath));
                    newPath = newPath.Replace('\\', '/');
                    McpLogger.Log("[ProjectCreate] Renaming asset from '" + assetPath + "' to '" + newPath + "'");
                    AssetDatabase.MoveAsset(assetPath, newPath);
                    assetPath = newPath;
                    newAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                }
                if (ctx.JsonData["select_after_create"].AsBoolDefault(defaultValue: true))
                {
                    Selection.activeObject = newAsset;
                }
                if (ctx.JsonData["open_after_create"].AsBoolDefault(defaultValue: false))
                {
                    AssetDatabase.OpenAsset(newAsset);
                }
                yield return Response.Success("Asset created successfully at '" + assetPath + "'", new JsonClass
                {
                    ["path"] = assetPath,
                    ["type"] = ((object)newAsset).GetType().Name,
                    ["name"] = Path.GetFileName(assetPath)
                });
            }
            else
            {
                yield return Response.Error($"Menu item '{menuPath}' executed, but no new asset was detected after {maxRetries} retries.");
            }
        }

        [ToolAction("create_menu", Parameters = new string[] { "name", "folder_path", "menu_path", "open_after_create", "select_after_create" }, Required = new string[] { "name" }, Inject = new string[] { "source=menu" })]
        /// <summary>
        /// 处理从菜单创建资源的操作
        /// </summary>
        public object HandleCreateFromMenu(ToolContext ctx)
        {
            // 为项目创建操作设置超时时间（60秒）
            return ctx.AsyncReturn(HandleCreateFromMenuAsync(ctx), 60f);
        }

        [ToolAction("create_empty", Parameters = new string[] { "name", "folder_path", "extension", "content", "force", "open_after_create", "select_after_create" }, Required = new string[] { "name" }, Inject = new string[] { "source=empty" })]
        /// <summary>
        /// 处理创建空文件的操作
        /// </summary>
        public object HandleCreateEmpty(JsonClass args)
        {
            string fileName = args["name"]?.Value;
            if (string.IsNullOrEmpty(fileName))
            {
                return Response.Error("'name' parameter is required for empty file creation.");
            }

            string folderPath = args["folder_path"]?.Value ?? "Assets";
            if (!folderPath.StartsWith("Assets"))
            {
                folderPath = "Assets/" + folderPath;
            }

            string extension = args["extension"]?.Value;
            if (string.IsNullOrEmpty(extension))
            {
                extension = "txt";
            }
            else if (extension.StartsWith("."))
            {
                extension = extension.Substring(1);
            }

            string content = args["content"]?.Value ?? string.Empty;
            bool force = args["force"].AsBoolDefault(false);

            // 确保目标文件夹存在
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                McpLogger.Log($"[ProjectCreate] Creating folder path: {folderPath}");
                CreateFolderRecursive(folderPath);
            }

            // 构建完整文件路径
            string filePath = $"{folderPath}/{fileName}.{extension}";

            // 检查文件是否已存在
            if (File.Exists(filePath) && !force)
            {
                return Response.Error($"File '{filePath}' already exists. Use 'force' parameter to overwrite.");
            }

            try
            {
                // 创建文件
                File.WriteAllText(filePath, content);
                AssetDatabase.Refresh();

                McpLogger.Log($"[ProjectCreate] Created empty file: '{filePath}'");

                // 获取创建的资源
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);

                bool selectAfterCreate = args["select_after_create"].AsBoolDefault(true);
                if (selectAfterCreate && asset != null)
                {
                    Selection.activeObject = asset;
                }

                bool openAfterCreate = args["open_after_create"].AsBoolDefault(false);
                if (openAfterCreate && asset != null)
                {
                    AssetDatabase.OpenAsset(asset);
                }

                return Response.Success($"Empty file created successfully at '{filePath}'", new JsonClass
                {
                    ["path"] = filePath,
                    ["type"] = asset?.GetType().Name ?? "Unknown",
                    ["name"] = Path.GetFileName(filePath)
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to create empty file '{filePath}': {e.Message}");
            }
        }

        [ToolAction("create_template", Parameters = new string[] { "name", "folder_path", "template_path", "force", "open_after_create", "select_after_create" }, Required = new string[] { "name" }, Inject = new string[] { "source=template" })]
        /// <summary>
        /// 处理从模板创建资源的操作
        /// </summary>
        public object HandleCreateFromTemplate(JsonClass args)
        {
            string fileName = args["name"]?.Value;
            if (string.IsNullOrEmpty(fileName))
            {
                return Response.Error("'name' parameter is required for template creation.");
            }

            string templatePath = args["template_path"]?.Value;
            if (string.IsNullOrEmpty(templatePath))
            {
                return Response.Error("'template_path' parameter is required for template creation.");
            }

            string folderPath = args["folder_path"]?.Value ?? "Assets";
            if (!folderPath.StartsWith("Assets"))
            {
                folderPath = "Assets/" + folderPath;
            }

            bool force = args["force"].AsBoolDefault(false);

            // 确保模板文件存在
            if (!File.Exists(templatePath) && !AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(templatePath))
            {
                return Response.Error($"Template file '{templatePath}' does not exist.");
            }

            // 确保目标文件夹存在
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                McpLogger.Log($"[ProjectCreate] Creating folder path: {folderPath}");
                CreateFolderRecursive(folderPath);
            }

            // 获取模板文件的扩展名
            string extension = Path.GetExtension(templatePath);

            // 构建完整文件路径
            string filePath = $"{folderPath}/{fileName}{extension}";

            // 检查文件是否已存在
            if (File.Exists(filePath) && !force)
            {
                return Response.Error($"File '{filePath}' already exists. Use 'force' parameter to overwrite.");
            }

            try
            {
                // 复制模板文件
                if (File.Exists(templatePath))
                {
                    // 从文件系统复制
                    File.Copy(templatePath, filePath, force);
                }
                else
                {
                    // 从Asset数据库复制
                    string result = AssetDatabase.CopyAsset(templatePath, filePath) ? "success" : "failed";
                    if (result != "success")
                    {
                        return Response.Error($"Failed to copy asset from '{templatePath}' to '{filePath}'");
                    }
                }

                AssetDatabase.Refresh();

                McpLogger.Log($"[ProjectCreate] Created file from template: '{filePath}'");

                // 获取创建的资源
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);

                bool selectAfterCreate = args["select_after_create"].AsBoolDefault(true);
                if (selectAfterCreate && asset != null)
                {
                    Selection.activeObject = asset;
                }

                bool openAfterCreate = args["open_after_create"].AsBoolDefault(false);
                if (openAfterCreate && asset != null)
                {
                    AssetDatabase.OpenAsset(asset);
                }

                return Response.Success($"File created from template successfully at '{filePath}'", new JsonClass
                {
                    ["path"] = filePath,
                    ["type"] = asset?.GetType().Name ?? "Unknown",
                    ["name"] = Path.GetFileName(filePath)
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to create file from template '{templatePath}': {e.Message}");
            }
        }

        [ToolAction("create_copy", Parameters = new string[] { "name", "folder_path", "copy_source", "force", "open_after_create", "select_after_create" }, Required = new string[] { "name" }, Inject = new string[] { "source=copy" })]
        /// <summary>
        /// 处理从现有资源复制的操作
        /// </summary>
        public object HandleCreateFromCopy(JsonClass args)
        {
            string fileName = args["name"]?.Value;

            string copySource = args["copy_source"]?.Value;
            if (string.IsNullOrEmpty(copySource))
            {
                return Response.Error("'copy_source' parameter is required for copy creation.");
            }

            string folderPath = args["folder_path"]?.Value ?? "Assets";
            if (!folderPath.StartsWith("Assets"))
            {
                folderPath = "Assets/" + folderPath;
            }

            bool force = args["force"].AsBoolDefault(false);

            // 确保源文件存在
            if (!AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(copySource))
            {
                return Response.Error($"Source asset '{copySource}' does not exist.");
            }

            // 确保目标文件夹存在
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                McpLogger.Log($"[ProjectCreate] Creating folder path: {folderPath}");
                CreateFolderRecursive(folderPath);
            }

            // 如果没有指定文件名，使用源文件名
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = Path.GetFileNameWithoutExtension(copySource);
            }

            // 获取源文件的扩展名
            string extension = Path.GetExtension(copySource);

            // 构建完整文件路径
            string filePath = $"{folderPath}/{fileName}{extension}";

            // 检查文件是否已存在
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath) != null && !force)
            {
                return Response.Error($"Asset '{filePath}' already exists. Use 'force' parameter to overwrite.");
            }

            try
            {
                // 复制资源
                string result = AssetDatabase.CopyAsset(copySource, filePath) ? "success" : "failed";
                if (result != "success")
                {
                    return Response.Error($"Failed to copy asset from '{copySource}' to '{filePath}'");
                }

                AssetDatabase.Refresh();

                McpLogger.Log($"[ProjectCreate] Created file from copy: '{filePath}'");

                // 获取创建的资源
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);

                bool selectAfterCreate = args["select_after_create"].AsBoolDefault(true);
                if (selectAfterCreate && asset != null)
                {
                    Selection.activeObject = asset;
                }

                bool openAfterCreate = args["open_after_create"].AsBoolDefault(false);
                if (openAfterCreate && asset != null)
                {
                    AssetDatabase.OpenAsset(asset);
                }

                return Response.Success($"File copied successfully to '{filePath}'", new JsonClass
                {
                    ["path"] = filePath,
                    ["type"] = asset?.GetType().Name ?? "Unknown",
                    ["name"] = Path.GetFileName(filePath)
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to copy asset '{copySource}': {e.Message}");
            }
        }

        /// <summary>
        /// 递归创建文件夹
        /// </summary>
        private void CreateFolderRecursive(string folderPath)
        {
            // 如果文件夹已存在，直接返回
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            // 分割路径
            string[] pathParts = folderPath.Split('/');
            string currentPath = pathParts[0]; // 应该是"Assets"

            // 从第二部分开始，逐级创建文件夹
            for (int i = 1; i < pathParts.Length; i++)
            {
                string folderName = pathParts[i];
                string parentPath = currentPath;
                currentPath = $"{currentPath}/{folderName}";

                // 如果当前级别的文件夹不存在，创建它
                if (!AssetDatabase.IsValidFolder(currentPath))
                {
                    string guid = AssetDatabase.CreateFolder(parentPath, folderName);
                    if (string.IsNullOrEmpty(guid))
                    {
                        McpLogger.Log($"[ProjectCreate] Failed to create folder: {currentPath}");
                    }
                    else
                    {
                        McpLogger.Log($"[ProjectCreate] Created folder: {currentPath}");
                    }
                }
            }

            // 刷新资源数据库以确保文件夹创建成功
            AssetDatabase.Refresh();
        }
    
}
    }

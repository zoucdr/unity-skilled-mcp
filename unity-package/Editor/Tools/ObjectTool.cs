using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
// Migrated from Newtonsoft.Json to SimpleJson
using UnityEditor;
using UnityEngine;
using UniMcp.Models; // For Response class

namespace UniMcp.Tools
{
    /// <summary>
    /// Handles UnityEngine.Object deletion operations with interactive confirmation.
    /// Supports GameObjects, assets, and other Unity objects.
    /// Target resolution: the target is located once by ResolveTarget before the handler runs.
    /// Action tree: 'confirm' parameter determines confirmation behavior:
    ///   - confirm=true: caller explicitly authorizes deletion, skip the modal dialog (required for unattended calls)
    ///   - confirm=false/unset: asset deletion shows a modal confirmation dialog; scene objects are deleted directly
    /// A modal dialog blocks the editor main thread, so unattended clients must pass confirm=true.
    /// 对应方法名: object_delete
    /// </summary>
    [ToolModule("object", GroupEn = "Scene", GroupZh = "场景层级",
        Tags = new[] { "gameobject", "asset-management", "deletion" })]
    public class ObjectTool : ToolModuleBase, ITargetedTool
    {
        public override string Description => L.T("Delete GameObjects or assets", "删除游戏对象或资源");

        /// <summary>Resolve the single UnityEngine.Object this call targets.</summary>
        public object ResolveTarget(ToolContext ctx)
        {
            return ToolTargetResolver.Resolve<UnityEngine.Object>(ctx, TargetScope.Any);
        }

        /// <summary>
        /// Create the list of parameter keys supported by this method
        /// </summary>
        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                // Target path
                new MethodStr("path", L.T("Object hierarchy path", "对象层级路径"), false)
                    .AddExample("UI/Canvas/Button"),
                
                // Target instance ID
                new MethodStr("instance_id", L.T("Object instance ID", "对象实例ID"))
                    .AddExample("-2524"),
                
                // Confirmation control: true means the caller has explicitly authorized the deletion
                new MethodBool("confirm", L.T("Explicit authorization: true=delete directly without a modal dialog (needed for unattended calls); false/unset=asset deletion shows a confirmation dialog, scene objects are deleted directly", "显式授权：true=跳过模态确认框直接删除（无人值守调用必须用此项）；false/未设置=删除资源时弹出确认框，场景对象直接删除"))
            };
        }


        /// <summary>
        /// 异步处理调用方已显式授权（confirm=true）的删除：场景对象与资源都直接删除，不弹模态框。
        /// 模态框会阻塞 Editor 主线程，无人值守的 MCP 调用必须走这条路径。
        /// </summary>
        private IEnumerator HandleConfirmedDeleteActionAsync(ToolContext ctx)
        {
            UnityEngine.Object target = ExtractTargetFromContext(ctx);
            if (target == null)
            {
                yield return Response.Error("No target Object found for deletion.");
                yield break;
            }

            McpLogger.Log($"[ObjectDelete] Authorized deletion of {target.GetType().Name} '{target.name}' without dialog");
            var result = DeleteSingleObject(target);
            yield return result;
        }

        [ToolAction("delete", Parameters = new string[] { "path", "instance_id" }, Inject = new string[] { "confirm=true" })]
        /// <summary>
        /// 处理调用方已显式授权的删除操作
        /// </summary>
        private object HandleConfirmedDeleteAction(ToolContext ctx)
        {
            // 为对象删除操作设置超时时间（30秒）
            return ctx.AsyncReturn(HandleConfirmedDeleteActionAsync(ctx), 30f);
        }

        /// <summary>
        /// 异步处理未明确确认的删除操作，检查是否是资源删除来决定是否需要确认
        /// </summary>
        private IEnumerator HandleUnconfirmedDeleteActionAsync(ToolContext ctx)
        {
            UnityEngine.Object target = ExtractTargetFromContext(ctx);
            if (target == null)
            {
                yield return Response.Error("No target Object found for deletion.");
                yield break;
            }

            // 检查预制体重定向（仅对GameObject适用）
            if (target is GameObject gameObject)
            {
                object redirectResult = CheckPrefabRedirection(gameObject);
                if (redirectResult != null)
                {
                    yield return redirectResult;
                    yield break;
                }
            }

            // 检查是否是资源删除
            bool isAssetDeletion = IsAssetDeletion(ctx);
            if (!isAssetDeletion)
            {
                // 场景Object删除，直接删除无需确认
                McpLogger.Log($"[ObjectDelete] Direct deletion of {target.GetType().Name} '{target.name}' without confirmation");
                var result = DeleteSingleObject(target);
                yield return result;
                yield break;
            }

            // 资源删除需要用户确认
            McpLogger.Log($"[ObjectDelete] Asset deletion detected for '{target.name}', showing confirmation dialog");

            string confirmationMessage = $"You are about to delete the asset '{target.name}' ({target.GetType().Name}).\n\nThis action cannot be undone. Continue?";

            bool confirmed = EditorUtility.DisplayDialog(
                "Confirm Asset Deletion",
                confirmationMessage,
                "Delete Asset",
                "Cancel"
            );

            if (!confirmed)
            {
                McpLogger.Log($"[ObjectDelete] User cancelled asset deletion for '{target.name}'");
                yield return Response.Success($"Asset deletion cancelled by user. Object '{target.name}' was not deleted.", new { cancelled = true, target_name = target.name });
                yield break;
            }

            McpLogger.Log($"[ObjectDelete] User confirmed asset deletion for '{target.name}'");

            // 用户确认后执行删除
            var deleteResult = DeleteSingleObject(target);
            yield return deleteResult;
        }

        /// <summary>
        /// 处理未确认的删除操作
        /// </summary>
        private object HandleUnconfirmedDeleteAction(ToolContext ctx)
        {
            // 为对象删除操作设置超时时间（30秒）
            return ctx.AsyncReturn(HandleUnconfirmedDeleteActionAsync(ctx), 30f);
        }

        /// <summary>
        /// 从执行上下文中提取唯一目标UnityEngine.Object
        /// </summary>
        private UnityEngine.Object ExtractTargetFromContext(ToolContext context)
        {
            // 先尝试从ObjectReferences获取（避免序列化问题）
            if (context.TryGetObjectReference("_resolved_targets", out object targetsObj))
            {
                if (targetsObj is UnityEngine.Object singleObject)
                {
                    return singleObject;
                }
                else if (targetsObj is UnityEngine.Object[] objectArray && objectArray.Length > 0)
                {
                    return objectArray[0]; // 只取第一个
                }
                else if (targetsObj is System.Collections.IList list && list.Count > 0)
                {
                    foreach (var item in list)
                    {
                        if (item is UnityEngine.Object obj)
                            return obj; // 返回第一个找到的UnityEngine.Object
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 检查是否是资源删除操作
        /// </summary>
        private bool IsAssetDeletion(ToolContext context)
        {
            // 通过path参数判断是否是资源路径
            if (context.TryGetValue("path", out object pathObj) && pathObj != null)
            {
                string path = pathObj.ToString();
                // 如果路径以Assets/开头，则认为是资源删除
                if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 检查预制体重定向逻辑
        /// </summary>
        private object CheckPrefabRedirection(GameObject target)
        {
            if (target == null)
                return null;

            // 检查是否是预制体实例，如果是预制体资产本身，应该使用manage_asset命令
            string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target);
            if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                // 这是预制体实例，可以正常删除
                return null;
            }

            return null; // 继续正常处理
        }

        /// <summary>
        /// 删除单个UnityEngine.Object
        /// </summary>
        private object DeleteSingleObject(UnityEngine.Object targetObject)
        {
            if (targetObject == null)
            {
                return Response.Error("Target Object is null.");
            }

            string objectName = targetObject.name;
            int objectId = targetObject.GetInstanceID();
            string objectType = targetObject.GetType().Name;

            try
            {
                // 判断是否为资源文件
                string assetPath = AssetDatabase.GetAssetPath(targetObject);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    // 资源文件删除
                    bool success = AssetDatabase.DeleteAsset(assetPath);
                    if (success)
                    {
                        var deletedObject = new { name = objectName, instanceID = objectId, type = objectType, assetPath = assetPath };
                        return Response.Success($"{objectType} asset '{objectName}' deleted successfully.", deletedObject);
                    }
                    else
                    {
                        return Response.Error($"Failed to delete {objectType} asset '{objectName}' at path: {assetPath}");
                    }
                }
                else
                {
                    // 场景对象删除
                    Undo.DestroyObjectImmediate(targetObject);
                    var deletedObject = new { name = objectName, instanceID = objectId, type = objectType };
                    return Response.Success($"{objectType} '{objectName}' deleted successfully.", deletedObject);
                }
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to delete {objectType} '{objectName}': {e.Message}");
            }
        }


    }
}
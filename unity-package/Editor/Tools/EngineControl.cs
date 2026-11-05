using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UniMcp.Models;

namespace UniMcp.Tools
{
    /// <summary>
    /// Handles engine-level operations: build pipeline, editor tool selection, engine/project
    /// settings, compilation state and undo. Complements base_editor (editor windows/menus)
    /// and gameplay (play mode/input/screenshots) instead of duplicating them.
    /// 对应方法名: engine_control
    /// </summary>
    [ToolName("engine_control", "Engine Control", "引擎控制")]
    public class EngineControl : StateMethodBase
    {
        public override string Description => L.T(
            "Control build pipeline, editor tools, engine settings and compilation",
            "控制构建流水线、编辑器工具、引擎设置与编译");

        private const int MaxCompileMessages = 100;

        private static readonly object CompileLock = new object();
        private static readonly List<CompilerMessage> CompileMessages = new List<CompilerMessage>();
        private static string lastCompileAt = "";
        private static bool compileListenerReady;
        private static bool compileDiagnosticsAvailable;

        private static readonly object BuildLock = new object();
        private static string buildState = "idle";
        private static string buildJobId = "";
        private static string buildMessage = "";
        private static string buildResult = "";
        private static string buildOutputPath = "";
        private static string buildTargetName = "";
        private static string buildStartedAt = "";
        private static string buildFinishedAt = "";
        private static int buildErrors;
        private static int buildWarnings;
        private static long buildSizeBytes;
        private static double buildDurationSeconds;
        private static int buildCounter;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            RegisterCompilationEvents();
        }

        private static void RegisterCompilationEvents()
        {
            if (compileListenerReady)
                return;

            CompilationPipeline.compilationStarted += OnCompilationStarted;
            AttachAssemblyCompilationFinished();
            compileListenerReady = true;
        }

        /// <summary>
        /// Unity 各版本的编译诊断事件名/签名并不统一（有的没有 assemblyCompilationFinished，
        /// 也没有 assemblyCompilationErrors）。用反射挂载，避免编译期依赖不存在的成员。
        /// </summary>
        private static void AttachAssemblyCompilationFinished()
        {
            try
            {
                var evt = typeof(CompilationPipeline).GetEvent(
                    "assemblyCompilationFinished",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (evt == null || evt.EventHandlerType == null)
                    return;

                var add = evt.GetAddMethod(true);
                var handler = typeof(EngineControl).GetMethod(
                    "OnAssemblyCompilationFinished",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (add == null || handler == null)
                    return;

                var callback = Delegate.CreateDelegate(evt.EventHandlerType, handler);
                if (callback == null)
                    return;

                add.Invoke(null, new object[] { callback });
                compileDiagnosticsAvailable = true;
            }
            catch (Exception)
            {
                // 编译诊断是可选能力，挂载失败不应影响其他引擎操作。
                compileDiagnosticsAvailable = false;
            }
        }

        private static void OnCompilationStarted(object context)
        {
            lock (CompileLock)
            {
                CompileMessages.Clear();
                lastCompileAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null)
                return;

            lock (CompileLock)
            {
                foreach (var message in messages)
                {
                    if (message.type != CompilerMessageType.Error && message.type != CompilerMessageType.Warning)
                        continue;
                    if (CompileMessages.Count >= MaxCompileMessages)
                        break;
                    CompileMessages.Add(message);
                }
                lastCompileAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                new MethodStr("action", L.T("Action type", "操作类型"), false)
                    .SetEnumValues(
                        "get_info", "get_selection", "set_selection", "set_active_tool", "clear_selection",
                        "get_build_settings", "set_build_target", "set_build_scenes", "build_player", "get_build_status",
                        "get_project_settings", "set_project_settings",
                        "get_compilation", "recompile",
                        "undo", "redo", "step"),

                // Selection / editor tool
                new MethodStr("tool", L.T("Editor tool: none, move, rotate, scale, rect, transform, custom, view", "编辑器工具：none、move、rotate、scale、rect、transform、custom、view"))
                    .AddExamples("move", "rotate", "scale"),
                new MethodArr("instance_ids", L.T("Object instance IDs to select", "要选择的对象 instanceID 列表"), true, "integer"),
                new MethodArr("paths", L.T("Scene object paths or asset paths to select", "要选择的场景对象路径或资源路径"), true, "string"),
                new MethodBool("clear", L.T("Clear the current selection before selecting (or clear only)", "选择前清空当前选择（或仅清空）"))
                    .SetDefault(false),

                // Build settings / build
                new MethodStr("target", L.T("Build target name, for example StandaloneWindows64, Android, WebGL", "构建目标平台名称，例如 StandaloneWindows64、Android、WebGL"))
                    .AddExamples("StandaloneWindows64", "Android", "WebGL"),
                new MethodStr("mode", L.T("Build scene list mode: replace, add, remove, enable, disable, clear", "构建场景列表模式：replace、add、remove、enable、disable、clear"))
                    .SetEnumValues("replace", "add", "remove", "enable", "disable", "clear")
                    .SetDefault("replace"),
                new MethodArr("scenes", L.T("Scene asset paths used by set_build_scenes or build_player", "set_build_scenes 或 build_player 使用的场景资源路径"), true, "string"),
                new MethodStr("output_path", L.T("Build output path", "构建输出路径"))
                    .AddExample("Builds/Windows/Game.exe"),
                new MethodBool("development", L.T("Build a development player", "构建开发版本"))
                    .SetDefault(false),
                new MethodBool("auto_run", L.T("Run the player after building", "构建完成后自动运行"))
                    .SetDefault(false),
                new MethodBool("wait", L.T("Wait for the build to finish before returning (may block for a long time)", "等待构建完成后再返回（可能长时间阻塞）"))
                    .SetDefault(false),

                // Project settings
                new MethodStr("company_name", L.T("Company name", "公司名称")),
                new MethodStr("product_name", L.T("Product name", "产品名称")),
                new MethodStr("bundle_version", L.T("Bundle version, for example 1.0.0", "版本号，例如 1.0.0")),
                new MethodInt("bundle_version_code", L.T("Android bundle version code", "Android 版本号代码"))
                    .SetRange(0, 1000000),
                new MethodBool("run_in_background", L.T("Run in background", "后台运行")),
                new MethodStr("color_space", L.T("Color space: gamma or linear", "色彩空间：gamma 或 linear"))
                    .SetEnumValues("gamma", "linear"),
                new MethodFloat("fixed_delta_time", L.T("Fixed timestep in seconds", "固定时间步长（秒）"))
                    .SetRange(0.0001f, 1.0f),
                new MethodFloat("time_scale", L.T("Time scale; 0 pauses the game", "时间缩放；0 表示暂停"))
                    .SetRange(0f, 100f),
                new MethodInt("quality_level", L.T("Quality level index", "质量等级索引"))
                    .SetRange(0, 100),
                new MethodArr("gravity", L.T("Physics gravity as [x, y, z]", "物理重力 [x, y, z]"), true, "number"),
                new MethodStr("enter_play_mode_options", L.T("Enter play mode options: off, none, disable_domain_reload, disable_scene_reload, disable_both", "进入播放模式选项：off、none、disable_domain_reload、disable_scene_reload、disable_both"))
                    .SetEnumValues("off", "none", "disable_domain_reload", "disable_scene_reload", "disable_both"),
                new MethodStr("serialization_mode", L.T("Asset serialization mode: mixed, force_binary, force_text", "资源序列化模式：mixed、force_binary、force_text"))
                    .SetEnumValues("mixed", "force_binary", "force_text")
            };
        }

        protected override StateTree CreateStateTree()
        {
            RegisterCompilationEvents();

            return StateTreeBuilder
                .Create()
                .Key("action")
                    .Leaf("get_info", HandleGetInfo)
                    .Leaf("get_selection", HandleGetSelection)
                    .Leaf("set_selection", HandleSetSelection)
                    .Leaf("set_active_tool", HandleSetActiveTool)
                    .Leaf("clear_selection", HandleClearSelection)

                    .Leaf("get_build_settings", HandleGetBuildSettings)
                    .Leaf("set_build_target", HandleSetBuildTarget)
                    .Leaf("set_build_scenes", HandleSetBuildScenes)
                    .Leaf("build_player", HandleBuildPlayer)
                    .Leaf("get_build_status", HandleGetBuildStatus)

                    .Leaf("get_project_settings", HandleGetProjectSettings)
                    .Leaf("set_project_settings", HandleSetProjectSettings)

                    .Leaf("get_compilation", HandleGetCompilation)
                    .Leaf("recompile", HandleRecompile)

                    .Leaf("undo", HandleUndo)
                    .Leaf("redo", HandleRedo)
                    .Leaf("step", HandleStep)
                .Build();
        }

        // ---------------- Editor / engine state ----------------

        private object HandleGetInfo(JsonClass args)
        {
            try
            {
                var scene = EditorSceneManager.GetActiveScene();
                var focused = EditorWindow.focusedWindow;

                return Response.Success("Retrieved engine state.", new
                {
                    unity_version = Application.unityVersion,
                    platform = Application.platform.ToString(),
                    project_path = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath,
                    product_name = Application.productName,
                    company_name = Application.companyName,
                    is_playing = EditorApplication.isPlaying,
                    is_paused = EditorApplication.isPaused,
                    is_compiling = EditorApplication.isCompiling,
                    is_updating = EditorApplication.isUpdating,
                    is_focused = EditorApplication.isFocused,
                    script_compilation_failed = EditorUtility.scriptCompilationFailed,
                    active_build_target = EditorUserBuildSettings.activeBuildTarget.ToString(),
                    active_tool = UnityEditor.Tools.current.ToString(),
                    pivot_mode = UnityEditor.Tools.pivotMode.ToString(),
                    pivot_rotation = UnityEditor.Tools.pivotRotation.ToString(),
                    selection_count = Selection.count,
                    active_scene = new
                    {
                        name = scene.name,
                        path = scene.path,
                        is_dirty = scene.isDirty,
                        is_loaded = scene.isLoaded,
                        root_count = scene.rootCount
                    },
                    focused_window = focused == null ? "" : focused.titleContent.text,
                    time_since_startup = EditorApplication.timeSinceStartup
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting engine state: {e.Message}");
            }
        }

        private object HandleGetSelection(JsonClass args)
        {
            try
            {
                var objects = Selection.objects;
                var items = new List<object>(objects.Length);
                foreach (var obj in objects)
                {
                    if (obj == null)
                        continue;

                    items.Add(new
                    {
                        name = obj.name,
                        type = obj.GetType().FullName,
                        instance_id = obj.GetInstanceID(),
                        asset_path = AssetDatabase.Contains(obj) ? AssetDatabase.GetAssetPath(obj) : ""
                    });
                }

                return Response.Success("Retrieved current selection.", new
                {
                    count = items.Count,
                    active_name = Selection.activeObject == null ? "" : Selection.activeObject.name,
                    objects = items
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting selection: {e.Message}");
            }
        }

        private object HandleSetSelection(JsonClass args)
        {
            try
            {
                var instanceIds = ReadIntArray(args, "instance_ids");
                var paths = ReadStringArray(args, "paths");
                var clear = args["clear"].AsBoolDefault(false);

                if (!clear && instanceIds.Count == 0 && paths.Count == 0)
                    return Response.Error("选择失败：请提供 instance_ids、paths，或将 clear 设为 true。");

                var objects = new List<UnityEngine.Object>();
                var unresolved = new List<string>();

                foreach (var id in instanceIds)
                {
                    var obj = EditorUtility.InstanceIDToObject(id);
                    if (obj == null)
                        unresolved.Add($"instance_id:{id}");
                    else
                        objects.Add(obj);
                }

                foreach (var path in paths)
                {
                    var normalized = NormalizeAssetPath(path);
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalized);
                    if (obj == null)
                    {
                        var go = GameObject.Find(normalized);
                        if (go != null)
                            obj = go;
                    }

                    if (obj == null)
                        unresolved.Add(path);
                    else
                        objects.Add(obj);
                }

                Selection.objects = objects.ToArray();
                Selection.activeObject = objects.Count > 0 ? objects[0] : null;

                var selected = new List<object>(objects.Count);
                foreach (var obj in objects)
                {
                    selected.Add(new
                    {
                        name = obj.name,
                        type = obj.GetType().FullName,
                        instance_id = obj.GetInstanceID()
                    });
                }

                return Response.Success(
                    clear && objects.Count == 0 ? "Cleared selection." : $"Selected {objects.Count} object(s).",
                    new { count = objects.Count, unresolved, objects = selected });
            }
            catch (Exception e)
            {
                return Response.Error($"Error setting selection: {e.Message}");
            }
        }

        private object HandleClearSelection(JsonClass args)
        {
            try
            {
                Selection.objects = new UnityEngine.Object[0];
                Selection.activeObject = null;
                return Response.Success("Cleared selection.", new { count = 0 });
            }
            catch (Exception e)
            {
                return Response.Error($"Error clearing selection: {e.Message}");
            }
        }

        private object HandleSetActiveTool(JsonClass args)
        {
            try
            {
                var name = args["tool"]?.Value;
                if (string.IsNullOrWhiteSpace(name))
                    return Response.Error("请提供 tool，例如 move、rotate、scale、rect、transform、view、custom、none。");

                UnityEditor.Tool tool;
                if (!Enum.TryParse(name.Trim(), true, out tool) || !Enum.IsDefined(typeof(UnityEditor.Tool), tool))
                    return Response.Error($"未知的编辑器工具：{name}。可用值：none、move、rotate、scale、rect、transform、custom、view。");

                UnityEditor.Tools.current = tool;
                return Response.Success($"Active editor tool set to {tool}.", new
                {
                    active_tool = UnityEditor.Tools.current.ToString(),
                    pivot_mode = UnityEditor.Tools.pivotMode.ToString(),
                    pivot_rotation = UnityEditor.Tools.pivotRotation.ToString()
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error setting active tool: {e.Message}");
            }
        }

        // ---------------- Build settings and build pipeline ----------------

        private object HandleGetBuildSettings(JsonClass args)
        {
            try
            {
                var scenes = new List<object>();
                var entries = EditorBuildSettings.scenes;
                for (int i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i];
                    scenes.Add(new
                    {
                        index = i,
                        path = entry.path,
                        enabled = entry.enabled,
                        exists = AssetDatabase.LoadAssetAtPath<SceneAsset>(entry.path) != null
                    });
                }

                return Response.Success("Retrieved build settings.", new
                {
                    active_build_target = EditorUserBuildSettings.activeBuildTarget.ToString(),
                    active_build_target_group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget).ToString(),
                    development = EditorUserBuildSettings.development,
                    allow_debugging = EditorUserBuildSettings.allowDebugging,
                    scene_count = entries.Length,
                    enabled_scene_count = CountEnabledScenes(entries),
                    scenes
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting build settings: {e.Message}");
            }
        }

        private object HandleSetBuildTarget(JsonClass args)
        {
            try
            {
                var name = args["target"]?.Value;
                if (string.IsNullOrWhiteSpace(name))
                    return Response.Error("请提供 target，例如 StandaloneWindows64、Android、WebGL。");

                BuildTarget target;
                if (!Enum.TryParse(name.Trim(), true, out target) || !Enum.IsDefined(typeof(BuildTarget), target))
                    return Response.Error($"未知的构建目标：{name}。");

                if (EditorUserBuildSettings.activeBuildTarget == target)
                    return Response.Success($"Already on build target {target}.", new
                    {
                        switched = false,
                        target = target.ToString(),
                        target_group = BuildPipeline.GetBuildTargetGroup(target).ToString()
                    });

                if (EditorApplication.isPlaying)
                    return Response.Error("播放模式中不能切换构建目标，请先停止播放。");

                var group = BuildPipeline.GetBuildTargetGroup(target);
                var started = EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);

                return Response.Success(
                    started ? $"Build target switch requested: {target}." : $"Build target switch was not started: {target}.",
                    new
                    {
                        switched = started,
                        target = target.ToString(),
                        target_group = group.ToString(),
                        note = "平台切换会触发资源重新导入；完成后用 get_info 或 get_build_settings 确认 active_build_target。"
                    });
            }
            catch (Exception e)
            {
                return Response.Error($"Error switching build target: {e.Message}");
            }
        }

        private object HandleSetBuildScenes(JsonClass args)
        {
            try
            {
                var modeText = args["mode"]?.Value;
                var mode = string.IsNullOrWhiteSpace(modeText) ? "replace" : modeText.Trim().ToLowerInvariant();
                var paths = ReadStringArray(args, "scenes");
                var current = EditorBuildSettings.scenes;

                if (mode != "clear" && paths.Count == 0)
                    return Response.Error("请提供 scenes 场景路径列表（mode=clear 时除外）。");

                if (mode != "replace" && mode != "add" && mode != "remove" && mode != "enable" && mode != "disable" && mode != "clear")
                    return Response.Error($"未知的 mode：{mode}。可用 replace、add、remove、enable、disable、clear。");

                var normalized = new List<string>(paths.Count);
                var missing = new List<string>();
                foreach (var path in paths)
                {
                    var value = NormalizeAssetPath(path);
                    if (value.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                        && AssetDatabase.LoadAssetAtPath<SceneAsset>(value) != null)
                        normalized.Add(value);
                    else
                        missing.Add(path);
                }

                if (missing.Count > 0 && (mode == "replace" || mode == "add"))
                    return Response.Error("以下场景不存在或不是 .unity 资源，未修改构建列表。", new { missing });

                var next = new List<EditorBuildSettingsScene>();
                var set = new HashSet<string>(normalized, StringComparer.OrdinalIgnoreCase);

                switch (mode)
                {
                    case "replace":
                        foreach (var path in normalized)
                            next.Add(new EditorBuildSettingsScene(path, true));
                        break;

                    case "add":
                        next.AddRange(current);
                        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var entry in current)
                            existing.Add(entry.path);
                        foreach (var path in normalized)
                        {
                            if (!existing.Contains(path))
                                next.Add(new EditorBuildSettingsScene(path, true));
                        }
                        break;

                    case "remove":
                        foreach (var entry in current)
                        {
                            if (!set.Contains(entry.path))
                                next.Add(entry);
                        }
                        break;

                    case "enable":
                    case "disable":
                        var enabled = mode == "enable";
                        foreach (var entry in current)
                        {
                            if (set.Contains(entry.path))
                                next.Add(new EditorBuildSettingsScene(entry.path, enabled));
                            else
                                next.Add(entry);
                        }
                        break;
                }

                EditorBuildSettings.scenes = next.ToArray();

                var result = new List<object>(next.Count);
                for (int i = 0; i < next.Count; i++)
                {
                    result.Add(new
                    {
                        index = i,
                        path = next[i].path,
                        enabled = next[i].enabled
                    });
                }

                return Response.Success(
                    $"Build scene list updated ({mode}).",
                    new
                    {
                        mode,
                        scene_count = next.Count,
                        enabled_scene_count = CountEnabledScenes(next.ToArray()),
                        missing,
                        scenes = result
                    });
            }
            catch (Exception e)
            {
                return Response.Error($"Error updating build scenes: {e.Message}");
            }
        }

        private object HandleBuildPlayer(JsonClass args)
        {
            try
            {
                lock (BuildLock)
                {
                    if (buildState == "running" || buildState == "queued")
                        return Response.Error("已有构建任务在进行中，请先用 get_build_status 查询。", new { job_id = buildJobId });
                }

                if (EditorApplication.isPlaying)
                    return Response.Error("播放模式中不能构建，请先停止播放。");

                var outputPath = args["output_path"]?.Value;
                if (string.IsNullOrWhiteSpace(outputPath))
                    return Response.Error("请提供 output_path，例如 Builds/Windows/Game.exe。");

                var fullOutputPath = Path.GetFullPath(outputPath.Trim());

                var targetName = args["target"]?.Value;
                BuildTarget target;
                if (string.IsNullOrWhiteSpace(targetName))
                {
                    target = EditorUserBuildSettings.activeBuildTarget;
                }
                else if (!Enum.TryParse(targetName.Trim(), true, out target) || !Enum.IsDefined(typeof(BuildTarget), target))
                {
                    return Response.Error($"未知的构建目标：{targetName}。");
                }

                var scenePaths = ReadStringArray(args, "scenes");
                var sceneList = new List<string>();
                if (scenePaths.Count > 0)
                {
                    var missing = new List<string>();
                    foreach (var path in scenePaths)
                    {
                        var value = NormalizeAssetPath(path);
                        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(value) == null)
                            missing.Add(path);
                        else
                            sceneList.Add(value);
                    }

                    if (missing.Count > 0)
                        return Response.Error("以下场景不存在，未开始构建。", new { missing });
                }
                else
                {
                    foreach (var entry in EditorBuildSettings.scenes)
                    {
                        if (entry.enabled)
                            sceneList.Add(entry.path);
                    }
                }

                if (sceneList.Count == 0)
                    return Response.Error("没有可构建的场景：请用 set_build_scenes 启用场景，或显式传入 scenes。");

                var development = args["development"].AsBoolDefault(false);
                var autoRun = args["auto_run"].AsBoolDefault(false);
                var wait = args["wait"].AsBoolDefault(false);

                buildCounter++;
                var request = new BuildRequest
                {
                    jobId = $"build_{DateTime.Now:yyyyMMdd_HHmmss}_{buildCounter}",
                    outputPath = fullOutputPath,
                    scenes = sceneList.ToArray(),
                    target = target,
                    targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                    development = development,
                    autoRun = autoRun
                };

                lock (BuildLock)
                {
                    buildJobId = request.jobId;
                    buildState = wait ? "running" : "queued";
                    buildMessage = "";
                    buildResult = "";
                    buildOutputPath = fullOutputPath;
                    buildTargetName = target.ToString();
                    buildStartedAt = "";
                    buildFinishedAt = "";
                    buildErrors = 0;
                    buildWarnings = 0;
                    buildSizeBytes = 0;
                    buildDurationSeconds = 0;
                }

                if (wait)
                {
                    ExecuteBuild(request);
                    return Response.Success("Build finished.", GetBuildStatusSnapshot());
                }

                EditorApplication.delayCall += () => ExecuteBuild(request);

                return Response.Success("Build queued. Poll with get_build_status.", new
                {
                    job_id = request.jobId,
                    state = "queued",
                    output_path = fullOutputPath,
                    target = request.target.ToString(),
                    target_group = request.targetGroup.ToString(),
                    development,
                    auto_run = autoRun,
                    scene_count = request.scenes.Length,
                    scenes = request.scenes,
                    note = "构建在主线程执行；构建期间编辑器会阻塞，get_build_status 会在构建结束后返回最终状态。"
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error queueing build: {e.Message}");
            }
        }

        private object HandleGetBuildStatus(JsonClass args)
        {
            return Response.Success("Retrieved build status.", GetBuildStatusSnapshot());
        }

        private void ExecuteBuild(BuildRequest request)
        {
            lock (BuildLock)
            {
                buildState = "running";
                buildStartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                buildMessage = "Building...";
            }

            BuildReport report = null;
            string failure = null;

            try
            {
                var directory = Path.GetDirectoryName(request.outputPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var options = new BuildPlayerOptions
                {
                    scenes = request.scenes,
                    locationPathName = request.outputPath,
                    target = request.target,
                    targetGroup = request.targetGroup,
                    options = request.development ? BuildOptions.Development : BuildOptions.None
                };

                if (request.autoRun)
                    options.options |= BuildOptions.AutoRunPlayer;

                report = BuildPipeline.BuildPlayer(options);
            }
            catch (Exception e)
            {
                failure = e.Message;
            }

            lock (BuildLock)
            {
                buildFinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                if (report == null)
                {
                    buildState = "failed";
                    buildResult = "Failed";
                    buildMessage = failure ?? "Build pipeline returned no report.";
                    buildErrors = 1;
                    return;
                }

                var summary = report.summary;
                buildResult = summary.result.ToString();
                buildErrors = summary.totalErrors;
                buildWarnings = summary.totalWarnings;
                buildOutputPath = string.IsNullOrEmpty(summary.outputPath) ? request.outputPath : summary.outputPath;
                buildSizeBytes = (long)summary.totalSize;
                buildDurationSeconds = summary.totalTime.TotalSeconds;
                buildState = summary.result == BuildResult.Succeeded && summary.totalErrors == 0 ? "succeeded" : "failed";
                buildMessage = summary.result == BuildResult.Succeeded
                    ? "Build succeeded."
                    : $"Build {summary.result} with {summary.totalErrors} error(s).";
            }
        }

        private static object GetBuildStatusSnapshot()
        {
            lock (BuildLock)
            {
                return new
                {
                    state = buildState,
                    job_id = buildJobId,
                    result = buildResult,
                    message = buildMessage,
                    output_path = buildOutputPath,
                    target = buildTargetName,
                    started_at = buildStartedAt,
                    finished_at = buildFinishedAt,
                    duration_seconds = buildDurationSeconds,
                    size_bytes = buildSizeBytes,
                    total_errors = buildErrors,
                    total_warnings = buildWarnings
                };
            }
        }

        // ---------------- Engine / project settings ----------------

        private object HandleGetProjectSettings(JsonClass args)
        {
            try
            {
                return Response.Success("Retrieved project settings.", ReadProjectSettings());
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting project settings: {e.Message}");
            }
        }

        private object HandleSetProjectSettings(JsonClass args)
        {
            try
            {
                var errors = new List<string>();
                var applied = new List<string>();

                var hasCompany = args.ContainsKey("company_name");
                var company = hasCompany ? args["company_name"].Value : null;
                if (hasCompany && string.IsNullOrWhiteSpace(company))
                    errors.Add("company_name 不能为空。");

                var hasProduct = args.ContainsKey("product_name");
                var product = hasProduct ? args["product_name"].Value : null;
                if (hasProduct && string.IsNullOrWhiteSpace(product))
                    errors.Add("product_name 不能为空。");

                var hasBundleVersion = args.ContainsKey("bundle_version");
                var bundleVersion = hasBundleVersion ? args["bundle_version"].Value : null;
                if (hasBundleVersion && string.IsNullOrWhiteSpace(bundleVersion))
                    errors.Add("bundle_version 不能为空。");

                var hasVersionCode = args.ContainsKey("bundle_version_code");
                var versionCode = args["bundle_version_code"].AsIntDefault(-1);
                if (hasVersionCode && versionCode < 0)
                    errors.Add("bundle_version_code 必须大于等于 0。");

                var hasRunInBackground = args.ContainsKey("run_in_background");
                var runInBackground = args["run_in_background"].AsBoolDefault(false);

                var hasColorSpace = args.ContainsKey("color_space");
                var colorSpaceText = args["color_space"]?.Value;
                ColorSpace colorSpace = ColorSpace.Gamma;
                if (hasColorSpace
                    && !string.Equals(colorSpaceText, "gamma", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(colorSpaceText, "linear", StringComparison.OrdinalIgnoreCase))
                    errors.Add("color_space 只能是 gamma 或 linear。");

                var hasFixedDelta = args.ContainsKey("fixed_delta_time");
                var fixedDelta = args["fixed_delta_time"].AsFloatDefault(0.02f);
                if (hasFixedDelta && (fixedDelta <= 0f || fixedDelta > 1f))
                    errors.Add("fixed_delta_time 必须在 (0, 1] 范围内。");

                var hasTimeScale = args.ContainsKey("time_scale");
                var timeScale = args["time_scale"].AsFloatDefault(1f);
                if (hasTimeScale && (timeScale < 0f || timeScale > 100f))
                    errors.Add("time_scale 必须在 [0, 100] 范围内。");

                var hasQuality = args.ContainsKey("quality_level");
                var qualityLevel = args["quality_level"].AsIntDefault(-1);
                if (hasQuality && (qualityLevel < 0 || qualityLevel >= QualitySettings.names.Length))
                    errors.Add($"quality_level 必须在 0 到 {QualitySettings.names.Length - 1} 之间。");

                var hasGravity = args.ContainsKey("gravity");
                Vector3 gravity = Physics.gravity;
                if (hasGravity)
                {
                    var values = ReadFloatArray(args, "gravity");
                    if (values.Count != 3)
                        errors.Add("gravity 必须是包含 3 个数字的数组。");
                    else
                        gravity = new Vector3(values[0], values[1], values[2]);
                }

                var hasPlayModeOptions = args.ContainsKey("enter_play_mode_options");
                var playModeOptionsText = (args["enter_play_mode_options"]?.Value ?? "").Trim().ToLowerInvariant();
                if (hasPlayModeOptions
                    && playModeOptionsText != "off"
                    && playModeOptionsText != "none"
                    && playModeOptionsText != "disable_domain_reload"
                    && playModeOptionsText != "disable_scene_reload"
                    && playModeOptionsText != "disable_both")
                    errors.Add("enter_play_mode_options 只能是 off、none、disable_domain_reload、disable_scene_reload 或 disable_both。");

                var hasSerialization = args.ContainsKey("serialization_mode");
                var serializationText = (args["serialization_mode"]?.Value ?? "").Trim().ToLowerInvariant();
                SerializationMode serializationMode = SerializationMode.Mixed;
                if (hasSerialization
                    && serializationText != "mixed"
                    && serializationText != "force_binary"
                    && serializationText != "force_text")
                    errors.Add("serialization_mode 只能是 mixed、force_binary 或 force_text。");

                if (errors.Count > 0)
                    return Response.Error("设置未应用，存在无效参数。", new { errors });

                if (hasCompany)
                {
                    PlayerSettings.companyName = company;
                    applied.Add("company_name");
                }

                if (hasProduct)
                {
                    PlayerSettings.productName = product;
                    applied.Add("product_name");
                }

                if (hasBundleVersion)
                {
                    PlayerSettings.bundleVersion = bundleVersion;
                    applied.Add("bundle_version");
                }

                if (hasVersionCode)
                {
                    PlayerSettings.Android.bundleVersionCode = versionCode;
                    applied.Add("bundle_version_code");
                }

                if (hasRunInBackground)
                {
                    PlayerSettings.runInBackground = runInBackground;
                    applied.Add("run_in_background");
                }

                if (hasColorSpace)
                {
                    colorSpace = string.Equals(colorSpaceText, "linear", StringComparison.OrdinalIgnoreCase)
                        ? ColorSpace.Linear
                        : ColorSpace.Gamma;
                    PlayerSettings.colorSpace = colorSpace;
                    applied.Add("color_space");
                }

                if (hasFixedDelta)
                {
                    Time.fixedDeltaTime = fixedDelta;
                    applied.Add("fixed_delta_time");
                }

                if (hasTimeScale)
                {
                    Time.timeScale = timeScale;
                    applied.Add("time_scale");
                }

                if (hasQuality)
                {
                    QualitySettings.SetQualityLevel(qualityLevel, true);
                    applied.Add("quality_level");
                }

                if (hasGravity)
                {
                    Physics.gravity = gravity;
                    applied.Add("gravity");
                }

                if (hasPlayModeOptions)
                {
                    if (playModeOptionsText == "off")
                    {
                        EditorSettings.enterPlayModeOptionsEnabled = false;
                        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.None;
                    }
                    else
                    {
                        EditorSettings.enterPlayModeOptionsEnabled = true;
                        switch (playModeOptionsText)
                        {
                            case "disable_domain_reload":
                                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
                                break;
                            case "disable_scene_reload":
                                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableSceneReload;
                                break;
                            case "disable_both":
                                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
                                break;
                            default:
                                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.None;
                                break;
                        }
                    }
                    applied.Add("enter_play_mode_options");
                }

                if (hasSerialization)
                {
                    switch (serializationText)
                    {
                        case "force_binary":
                            serializationMode = SerializationMode.ForceBinary;
                            break;
                        case "force_text":
                            serializationMode = SerializationMode.ForceText;
                            break;
                        default:
                            serializationMode = SerializationMode.Mixed;
                            break;
                    }
                    EditorSettings.serializationMode = serializationMode;
                    applied.Add("serialization_mode");
                }

                AssetDatabase.SaveAssets();

                return Response.Success($"Applied {applied.Count} project setting(s).", new
                {
                    applied,
                    settings = ReadProjectSettings()
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error applying project settings: {e.Message}");
            }
        }

        private static object ReadProjectSettings()
        {
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);

            return new
            {
                product_name = PlayerSettings.productName,
                company_name = PlayerSettings.companyName,
                bundle_version = PlayerSettings.bundleVersion,
                android_bundle_version_code = PlayerSettings.Android.bundleVersionCode,
                application_identifier = PlayerSettings.GetApplicationIdentifier(group),
                color_space = PlayerSettings.colorSpace.ToString(),
                scripting_backend = PlayerSettings.GetScriptingBackend(group).ToString(),
                run_in_background = PlayerSettings.runInBackground,
                default_screen_width = PlayerSettings.defaultScreenWidth,
                default_screen_height = PlayerSettings.defaultScreenHeight,
                full_screen_mode = PlayerSettings.fullScreenMode.ToString(),
                quality_level = QualitySettings.GetQualityLevel(),
                quality_names = QualitySettings.names,
                time = new
                {
                    fixed_delta_time = Time.fixedDeltaTime,
                    maximum_delta_time = Time.maximumDeltaTime,
                    time_scale = Time.timeScale
                },
                gravity = Physics.gravity,
                serialization_mode = EditorSettings.serializationMode.ToString(),
                enter_play_mode_options_enabled = EditorSettings.enterPlayModeOptionsEnabled,
                enter_play_mode_options = EditorSettings.enterPlayModeOptions.ToString(),
                active_build_target = EditorUserBuildSettings.activeBuildTarget.ToString(),
                active_build_target_group = group.ToString()
            };
        }

        // ---------------- Compilation ----------------

        private object HandleGetCompilation(JsonClass args)
        {
            try
            {
                var messages = new List<object>();
                var errorCount = 0;
                var warningCount = 0;
                var lastCompile = "";

                lock (CompileLock)
                {
                    lastCompile = lastCompileAt;
                    foreach (var message in CompileMessages)
                    {
                        if (message.type == CompilerMessageType.Error)
                            errorCount++;
                        else
                            warningCount++;

                        messages.Add(new
                        {
                            type = message.type.ToString(),
                            file = message.file,
                            line = message.line,
                            column = message.column,
                            message = message.message
                        });
                    }
                }

                return Response.Success("Retrieved compilation state.", new
                {
                    is_compiling = EditorApplication.isCompiling,
                    is_updating = EditorApplication.isUpdating,
                    script_compilation_failed = EditorUtility.scriptCompilationFailed,
                    diagnostics_available = compileDiagnosticsAvailable,
                    last_compilation_at = lastCompile,
                    error_count = errorCount,
                    warning_count = warningCount,
                    message_count = messages.Count,
                    truncated = messages.Count >= MaxCompileMessages,
                    messages
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting compilation state: {e.Message}");
            }
        }

        private object HandleRecompile(JsonClass args)
        {
            try
            {
                if (EditorApplication.isCompiling)
                    return Response.Success("Compilation is already running.", new { requested = false });

                CompilationPipeline.RequestScriptCompilation();
                return Response.Success("Script compilation requested.", new
                {
                    requested = true,
                    note = "编译异步进行，稍后用 get_compilation 查询结果。"
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error requesting recompilation: {e.Message}");
            }
        }

        // ---------------- Undo / step ----------------

        private object HandleUndo(JsonClass args)
        {
            try
            {
                var group = Undo.GetCurrentGroupName();
                Undo.PerformUndo();
                return Response.Success("Undo performed.", new { group, undone = true });
            }
            catch (Exception e)
            {
                return Response.Error($"Error performing undo: {e.Message}");
            }
        }

        private object HandleRedo(JsonClass args)
        {
            try
            {
                var group = Undo.GetCurrentGroupName();
                Undo.PerformRedo();
                return Response.Success("Redo performed.", new { group, redone = true });
            }
            catch (Exception e)
            {
                return Response.Error($"Error performing redo: {e.Message}");
            }
        }

        private object HandleStep(JsonClass args)
        {
            try
            {
                if (!EditorApplication.isPlaying)
                    return Response.Error("单步执行仅在播放模式（含暂停）下可用。");

                EditorApplication.Step();
                return Response.Success("Stepped one frame.", new
                {
                    is_playing = EditorApplication.isPlaying,
                    is_paused = EditorApplication.isPaused
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error stepping frame: {e.Message}");
            }
        }

        // ---------------- Helpers ----------------

        private class BuildRequest
        {
            public string jobId;
            public string outputPath;
            public string[] scenes;
            public BuildTarget target;
            public BuildTargetGroup targetGroup;
            public bool development;
            public bool autoRun;
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? "").Trim().Replace('\\', '/');
        }

        private static List<string> ReadStringArray(JsonClass args, string key)
        {
            var result = new List<string>();
            if (!(args[key] is JsonArray array))
                return result;

            for (int i = 0; i < array.Count; i++)
            {
                var value = array[i]?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(value.Trim());
            }

            return result;
        }

        private static List<int> ReadIntArray(JsonClass args, string key)
        {
            var result = new List<int>();
            if (!(args[key] is JsonArray array))
                return result;

            for (int i = 0; i < array.Count; i++)
            {
                var node = array[i];
                int value;
                if (node != null && int.TryParse(node.Value, out value))
                    result.Add(value);
            }

            return result;
        }

        private static List<float> ReadFloatArray(JsonClass args, string key)
        {
            var result = new List<float>();
            if (!(args[key] is JsonArray array))
                return result;

            for (int i = 0; i < array.Count; i++)
            {
                var node = array[i];
                float value;
                if (node != null && float.TryParse(node.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
                    result.Add(value);
            }

            return result;
        }

        private static int CountEnabledScenes(EditorBuildSettingsScene[] scenes)
        {
            var count = 0;
            if (scenes == null)
                return count;

            foreach (var scene in scenes)
            {
                if (scene != null && scene.enabled)
                    count++;
            }

            return count;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UniMcp.Models;

namespace UniMcp.Tools
{
    /// <summary>
    /// Animator Controller（状态机）编辑与运行时驱动。
    /// 与 edit_anim_clip（片段/曲线/事件）互补：本工具负责控制器结构（层、状态、过渡、参数、混合树）。
    /// 对应方法名: edit_animator
    /// </summary>
    [ToolModule("animator", GroupEn = "Animation", GroupZh = "动画")]
    public class AnimatorTool : ToolModuleBase
    {
        public override string Description => L.T(
            "Create and edit Animator controllers (layers, states, transitions, parameters, blend trees) and drive a runtime Animator",
            "创建与编辑 Animator 控制器（层、状态、过渡、参数、混合树）并驱动运行时 Animator");

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                new MethodStr("action", L.T("Operation type", "操作类型"), false)
                    .SetEnumValues(
                        "create", "get_info", "list_states",
                        "add_parameter", "remove_parameter",
                        "add_layer", "remove_layer",
                        "add_state", "remove_state", "set_state", "set_default_state",
                        "add_transition", "set_transition", "remove_transition",
                        "add_blend_tree", "add_blend_tree_child",
                        "play", "crossfade", "set_parameter", "get_runtime"),

                new MethodStr("path", L.T("Animator controller asset path, for example Assets/Anim/Player.controller", "Animator 控制器资源路径，例如 Assets/Anim/Player.controller"))
                    .AddExample("Assets/Anim/Player.controller"),

                new MethodStr("layer", L.T("Layer name or zero-based index; defaults to the first layer", "层名称或从 0 开始的索引；默认第一层"))
                    .AddExamples("Base Layer", "1"),

                new MethodStr("state", L.T("State name", "状态名称"))
                    .AddExample("Idle"),

                new MethodStr("target_state", L.T("Destination state name, used by transitions", "过渡目标状态名称"))
                    .AddExample("Run"),

                new MethodStr("motion", L.T("Motion asset path (AnimationClip) assigned to the state or blend tree child", "赋给状态或混合树子项的动画片段路径"))
                    .AddExample("Assets/Anim/Idle.anim"),

                new MethodStr("parameter", L.T("Animator parameter name", "Animator 参数名称"))
                    .AddExample("Speed"),

                new MethodStr("parameter_type", L.T("Parameter type: float, int, bool, trigger", "参数类型：float、int、bool、trigger"))
                    .SetEnumValues("float", "int", "bool", "trigger")
                    .SetDefault("float"),

                new MethodFloat("default_value", L.T("Default numeric value for a new parameter", "新建参数的默认数值"))
                    .SetDefault(0f),

                new MethodBool("default_bool", L.T("Default value for a bool parameter", "bool 参数的默认值"))
                    .SetDefault(false),

                new MethodFloat("speed", L.T("State or Animator speed multiplier", "状态或 Animator 速度倍率"))
                    .SetRange(0f, 100f),

                new MethodFloat("weight", L.T("Layer weight", "层权重"))
                    .SetRange(0f, 1f),

                new MethodFloat("duration", L.T("Transition duration in seconds", "过渡时长（秒）"))
                    .SetRange(0f, 60f),

                new MethodBool("has_exit_time", L.T("Transition waits for the exit time", "过渡是否等待退出时间"))
                    .SetDefault(true),

                new MethodFloat("exit_time", L.T("Transition exit time, 0-1 normalized", "过渡退出时间，0-1 归一化"))
                    .SetRange(0f, 1f),

                new MethodArr("conditions", L.T("Transition conditions written as NAME:MODE:THRESHOLD, for example Speed:Greater:0.5", "过渡条件，格式 NAME:MODE:THRESHOLD，例如 Speed:Greater:0.5"), true, "string")
                    .AddExample("Speed:Greater:0.5", "Jump:If:0"),

                new MethodStr("blend_parameter", L.T("Blend tree parameter name", "混合树参数名称"))
                    .AddExample("Speed"),

                new MethodFloat("threshold", L.T("Blend tree child threshold", "混合树子项阈值"))
                    .SetRange(-1000f, 1000f),

                new MethodStr("target", L.T("Target GameObject path or instance ID that owns the Animator", "拥有 Animator 的目标对象路径或 instanceID"))
                    .AddExample("Player"),

                new MethodFloat("value", L.T("Parameter value used by set_parameter", "set_parameter 使用的参数值"))
                    .SetRange(-10000f, 10000f)
            };
        }

        [ToolAction("create", Parameters = new string[] { "path" }, Required = new string[] { "path" }, Inject = new string[] { "action=create" })]
        private object CreateController(JsonClass args)
        {
            string path = SanitizeAssetPath(args["path"]?.Value);
            if (string.IsNullOrEmpty(path))
            {
                return Response.Error("'path' is required for create.");
            }
            if (!path.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
            {
                return Response.Error("Controller path must end with '.controller'.");
            }
            if ((UnityEngine.Object)(object)AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != (UnityEngine.Object)null)
            {
                return Response.Error("Animator controller already exists: " + path);
            }
            try
            {
                EnsureDirectory(path);
                AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(path);
                if ((UnityEngine.Object)(object)controller == (UnityEngine.Object)null)
                {
                    return Response.Error("Failed to create Animator controller at " + path);
                }
                AssetDatabase.SaveAssets();
                return Response.Success("Animator controller created.", BuildInfo(controller, path));
            }
            catch (Exception ex)
            {
                return Response.Error("Failed to create Animator controller: " + ex.Message);
            }
        }

        [ToolAction("get_info", Parameters = new string[] { "path" }, Required = new string[] { "path" }, Inject = new string[] { "action=get_info" })]
        private object GetInfo(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            return Response.Success("Animator controller info retrieved.", BuildInfo(controller, path));
        }

        [ToolAction("list_states", Parameters = new string[] { "path" }, Required = new string[] { "path" }, Inject = new string[] { "action=list_states" })]
        private object ListStates(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            var layers = controller.layers;
            var result = new List<object>();
            for (int i = 0; i < layers.Length; i++)
            {
                var machine = layers[i].stateMachine;
                var states = new List<object>();
                if (machine != null)
                {
                    var children = machine.states;
                    for (int s = 0; s < children.Length; s++)
                    {
                        var state = children[s].state;
                        if (state == null)
                            continue;

                        states.Add(new
                        {
                            name = state.name,
                            motion = state.motion != null ? AssetDatabase.GetAssetPath(state.motion) : null,
                            speed = state.speed,
                            is_default = machine.defaultState == state,
                            transition_count = state.transitions != null ? state.transitions.Length : 0
                        });
                    }
                }

                result.Add(new { index = i, name = layers[i].name, state_count = states.Count, states = states });
            }

            return Response.Success("States listed.", new { path = path, layers = result });
        }

        [ToolAction("add_parameter", Parameters = new string[] { "path", "parameter", "parameter_type", "default_value", "default_bool" }, Inject = new string[] { "action=add_parameter" })]
        private object AddParameter(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            var name = (args["parameter"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'parameter' is required for add_parameter.");

            if (controller.parameters.Any(p => string.Equals(p.name, name, StringComparison.Ordinal)))
                return Response.Error("Parameter already exists: " + name);

            var type = ParseParameterType(args["parameter_type"]?.Value);
            controller.AddParameter(name, type);

            var parameters = controller.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (!string.Equals(parameters[i].name, name, StringComparison.Ordinal))
                    continue;

                if (type == AnimatorControllerParameterType.Float)
                    parameters[i].defaultFloat = args["default_value"].AsFloatDefault(0f);
                else if (type == AnimatorControllerParameterType.Int)
                    parameters[i].defaultInt = args["default_value"].AsIntDefault(0);
                else if (type == AnimatorControllerParameterType.Bool)
                    parameters[i].defaultBool = args["default_bool"].AsBoolDefault(false);
                break;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return Response.Success("Parameter added.", BuildInfo(controller, path));
        }

        [ToolAction("remove_parameter", Parameters = new string[] { "path", "parameter" }, Inject = new string[] { "action=remove_parameter" })]
        private object RemoveParameter(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            var name = (args["parameter"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'parameter' is required for remove_parameter.");

            var parameters = controller.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (!string.Equals(parameters[i].name, name, StringComparison.Ordinal))
                    continue;

                controller.RemoveParameter(i);
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                return Response.Success("Parameter removed.", BuildInfo(controller, path));
            }

            return Response.Error("Parameter not found: " + name);
        }

        [ToolAction("add_layer", Parameters = new string[] { "path", "layer", "weight" }, Inject = new string[] { "action=add_layer" })]
        private object AddLayer(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            var name = (args["layer"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'layer' is required for add_layer.");

            var layers = controller.layers;
            if (layers.Any(l => string.Equals(l.name, name, StringComparison.OrdinalIgnoreCase)))
                return Response.Error("Layer already exists: " + name);

            controller.AddLayer(name);

            var updated = controller.layers;
            var weight = args["weight"].AsFloatDefault(-1f);
            if (weight >= 0f && updated.Length > 0)
            {
                updated[updated.Length - 1].defaultWeight = weight;
                controller.layers = updated;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return Response.Success("Layer added.", BuildInfo(controller, path));
        }

        [ToolAction("remove_layer", Parameters = new string[] { "path", "layer" }, Inject = new string[] { "action=remove_layer" })]
        private object RemoveLayer(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            int index;
            var layerError = ResolveLayerIndex(controller, args["layer"]?.Value, out index);
            if (layerError != null)
                return layerError;

            if (controller.layers.Length <= 1)
                return Response.Error("Cannot remove the last remaining layer.");

            controller.RemoveLayer(index);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return Response.Success("Layer removed.", BuildInfo(controller, path));
        }

        // ---------------- 状态 ----------------

        [ToolAction("add_state", Parameters = new string[] { "path", "layer", "state", "motion", "speed" }, Inject = new string[] { "action=add_state" })]
        private object AddState(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            AnimatorStateMachine machine;
            var machineError = ResolveLayerMachine(controller, args["layer"]?.Value, out machine);
            if (machineError != null)
                return machineError;

            var name = (args["state"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'state' is required for add_state.");

            if (FindState(machine, name) != null)
                return Response.Error("State already exists: " + name);

            Motion motion = null;
            var motionPath = SanitizeAssetPath(args["motion"]?.Value);
            if (!string.IsNullOrEmpty(motionPath))
            {
                motion = AssetDatabase.LoadAssetAtPath<Motion>(motionPath);
                if (motion == null)
                    return Response.Error("Motion not found: " + motionPath);
            }

            try
            {
                var position = new Vector3(300f * machine.states.Length, 60f, 0f);
                var state = machine.AddState(name, position);
                if (motion != null)
                    state.motion = motion;
                state.name = name;

                var speed = args["speed"].AsFloatDefault(-1f);
                if (speed > 0f)
                    state.speed = speed;

                if (machine.defaultState == null)
                    machine.defaultState = state;

                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                return Response.Success("State added.", BuildInfo(controller, path));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to add state: " + e.Message);
            }
        }

        [ToolAction("remove_state", Parameters = new string[] { "path", "layer", "state" }, Inject = new string[] { "action=remove_state" })]
        private object RemoveState(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            AnimatorStateMachine machine;
            var machineError = ResolveLayerMachine(controller, args["layer"]?.Value, out machine);
            if (machineError != null)
                return machineError;

            var name = (args["state"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'state' is required for remove_state.");

            var state = FindState(machine, name);
            if (state == null)
                return Response.Error("State not found: " + name);

            machine.RemoveState(state);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return Response.Success("State removed.", BuildInfo(controller, path));
        }

        [ToolAction("set_state", Parameters = new string[] { "path", "layer", "state", "motion", "speed" }, Inject = new string[] { "action=set_state" })]
        private object SetState(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            AnimatorControllerLayer layer;
            var layerError = ResolveLayer(controller, args["layer"]?.Value, out layer);
            if (layerError != null)
                return layerError;

            var name = (args["state"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'state' is required for set_state.");

            var state = FindState(layer.stateMachine, name);
            if (state == null)
                return Response.Error("State not found: " + name);

            Undo.RecordObject(controller, "Set Animator State");

            var speed = args["speed"].AsFloatDefault(-1f);
            if (speed >= 0f)
                state.speed = speed;

            var motionPath = SanitizeAssetPath(args["motion"]?.Value);
            if (!string.IsNullOrEmpty(motionPath))
            {
                var motion = AssetDatabase.LoadAssetAtPath<Motion>(motionPath);
                if (motion == null)
                    return Response.Error("Motion not found: " + motionPath);
                state.motion = motion;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return Response.Success("State updated.", BuildInfo(controller, path));
        }

        [ToolAction("set_default_state", Parameters = new string[] { "path", "layer", "state" }, Inject = new string[] { "action=set_default_state" })]
        private object SetDefaultState(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            AnimatorStateMachine machine;
            var machineError = ResolveLayerMachine(controller, args["layer"]?.Value, out machine);
            if (machineError != null)
                return machineError;

            var name = (args["state"]?.Value ?? "").Trim();
            var state = FindState(machine, name);
            if (state == null)
                return Response.Error("State not found: " + name);

            machine.defaultState = state;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return Response.Success("Default state set to '" + name + "'.", BuildInfo(controller, path));
        }

        // ---------------- 过渡 ----------------

        [ToolAction("add_transition", Parameters = new string[] { "path", "layer", "state", "target_state", "duration", "has_exit_time", "exit_time", "conditions" }, Inject = new string[] { "action=add_transition" })]
        private object AddTransition(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            AnimatorStateMachine machine;
            var machineError = ResolveLayerMachine(controller, args["layer"]?.Value, out machine);
            if (machineError != null)
                return machineError;

            var fromName = (args["state"]?.Value ?? "").Trim();
            var toName = (args["target_state"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(fromName) || string.IsNullOrEmpty(toName))
                return Response.Error("'state' and 'target_state' are required for add_transition.");

            var from = FindState(machine, fromName);
            var to = FindState(machine, toName);
            if (from == null)
                return Response.Error("Source state not found: " + fromName);
            if (to == null)
                return Response.Error("Target state not found: " + toName);

            try
            {
                var transition = from.AddTransition(to);
                ApplyTransitionSettings(transition, controller, args);
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                return Response.Success("Transition added: " + fromName + " -> " + toName + ".", BuildInfo(controller, path));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to add transition: " + e.Message);
            }
        }

        [ToolAction("set_transition", Parameters = new string[] { "path", "layer", "state", "target_state", "duration", "has_exit_time", "exit_time", "conditions" }, Inject = new string[] { "action=set_transition" })]
        private object SetTransition(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            AnimatorStateMachine machine;
            var machineError = ResolveLayerMachine(controller, args["layer"]?.Value, out machine);
            if (machineError != null)
                return machineError;

            var fromName = (args["state"]?.Value ?? "").Trim();
            var toName = (args["target_state"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(fromName) || string.IsNullOrEmpty(toName))
                return Response.Error("'state' and 'target_state' are required for set_transition.");

            var transition = FindTransition(machine, fromName, toName);
            if (transition == null)
                return Response.Error("Transition not found: " + fromName + " -> " + toName);

            var replaced = ApplyTransitionSettings(transition, controller, args);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return Response.Success("Transition updated.", new { path = path, conditions = replaced });
        }

        [ToolAction("remove_transition", Parameters = new string[] { "path", "layer", "state", "target_state" }, Inject = new string[] { "action=remove_transition" })]
        private object RemoveTransition(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            AnimatorStateMachine machine;
            var machineError = ResolveLayerMachine(controller, args["layer"]?.Value, out machine);
            if (machineError != null)
                return machineError;

            var fromName = (args["state"]?.Value ?? "").Trim();
            var toName = (args["target_state"]?.Value ?? "").Trim();
            var transition = FindTransition(machine, fromName, toName);
            if (transition == null)
                return Response.Error("Transition not found: " + fromName + " -> " + toName);

            var from = FindState(machine, fromName);
            from.RemoveTransition(transition);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return Response.Success("Transition removed.", new { path = path });
        }

        private List<string> ApplyTransitionSettings(AnimatorStateTransition transition, AnimatorController controller, JsonClass args)
        {
            var hasExitTime = args["has_exit_time"].AsBoolDefault(true);
            transition.hasExitTime = hasExitTime;

            var duration = args["duration"].AsFloatDefault(-1f);
            if (duration >= 0f)
                transition.duration = duration;

            var exitTime = args["exit_time"].AsFloatDefault(-1f);
            if (exitTime >= 0f)
                transition.exitTime = exitTime;

            var applied = new List<string>();
            var conditionList = ReadStringArray(args, "conditions");
            if (conditionList.Count == 0)
                return applied;

            transition.conditions = new AnimatorCondition[0];
            for (int i = 0; i < conditionList.Count; i++)
            {
                string parameterName;
                string modeName;
                float threshold;
                if (!TryParseCondition(conditionList[i], out parameterName, out modeName, out threshold))
                {
                    applied.Add(conditionList[i] + " -> skipped (expected NAME:MODE:THRESHOLD)");
                    continue;
                }

                var mode = ParseConditionMode(modeName);
                if (mode == null)
                {
                    applied.Add(conditionList[i] + " -> skipped (unknown mode)");
                    continue;
                }

                if (!controller.parameters.Any(p => string.Equals(p.name, parameterName, StringComparison.Ordinal)))
                {
                    applied.Add(conditionList[i] + " -> skipped (unknown parameter)");
                    continue;
                }

                transition.AddCondition((AnimatorConditionMode)mode.Value, threshold, parameterName);
                applied.Add(conditionList[i]);
            }

            return applied;
        }

        // ---------------- 混合树 ----------------

        [ToolAction("add_blend_tree", Parameters = new string[] { "path", "layer", "state", "motion", "speed", "blend_parameter" }, Inject = new string[] { "action=add_blend_tree" })]
        private object AddBlendTree(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            AnimatorStateMachine machine;
            var machineError = ResolveLayerMachine(controller, args["layer"]?.Value, out machine);
            if (machineError != null)
                return machineError;

            var name = (args["state"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'state' is required for add_blend_tree.");

            if (FindState(machine, name) != null)
                return Response.Error("State already exists: " + name);

            var blendParameter = (args["blend_parameter"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(blendParameter))
                return Response.Error("'blend_parameter' is required for add_blend_tree.");

            if (!controller.parameters.Any(p => string.Equals(p.name, blendParameter, StringComparison.Ordinal)))
                return Response.Error("Blend parameter not found in controller: " + blendParameter);

            try
            {
                var tree = new BlendTree
                {
                    name = name + "_BlendTree",
                    blendType = BlendTreeType.Simple1D,
                    blendParameter = blendParameter,
                    useAutomaticThresholds = false
                };
                AssetDatabase.AddObjectToAsset(tree, controller);
                EditorUtility.SetDirty(tree);

                var state = machine.AddState(name, new Vector3(300f * machine.states.Length, 60f, 0f));
                state.motion = tree;

                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                return Response.Success("Blend tree state added.", BuildInfo(controller, path));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to add blend tree: " + e.Message);
            }
        }

        [ToolAction("add_blend_tree_child", Parameters = new string[] { "path", "state", "motion", "threshold" }, Inject = new string[] { "action=add_blend_tree_child" })]
        private object AddBlendTreeChild(JsonClass args)
        {
            AnimatorController controller;
            string path;
            var error = LoadController(args, out controller, out path);
            if (error != null)
                return error;

            var name = (args["state"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'state' is required for add_blend_tree_child.");

            var motionPath = SanitizeAssetPath(args["motion"]?.Value);
            if (string.IsNullOrEmpty(motionPath))
                return Response.Error("'motion' is required for add_blend_tree_child.");

            var motion = AssetDatabase.LoadAssetAtPath<Motion>(motionPath);
            if (motion == null)
                return Response.Error("Motion not found: " + motionPath);

            BlendTree tree = null;
            var layers = controller.layers;
            for (int i = 0; i < layers.Length && tree == null; i++)
            {
                var machine = layers[i].stateMachine;
                if (machine == null)
                    continue;

                var state = FindState(machine, name);
                if (state != null)
                    tree = state.motion as BlendTree;
            }

            if (tree == null)
                return Response.Error("Blend tree state not found: " + name);

            try
            {
                tree.AddChild(motion, args["threshold"].AsFloatDefault(0f));
                EditorUtility.SetDirty(tree);
                AssetDatabase.SaveAssets();

                var children = new List<object>();
                var treeChildren = tree.children;
                for (int i = 0; i < treeChildren.Length; i++)
                {
                    children.Add(new
                    {
                        motion = treeChildren[i].motion != null ? AssetDatabase.GetAssetPath(treeChildren[i].motion) : null,
                        threshold = treeChildren[i].threshold
                    });
                }

                return Response.Success("Blend tree child added.", new
                {
                    path = path,
                    state = name,
                    blend_parameter = tree.blendParameter,
                    child_count = children.Count,
                    children = children
                });
            }
            catch (Exception e)
            {
                return Response.Error("Failed to add blend tree child: " + e.Message);
            }
        }

        // ---------------- 运行时 ----------------

        [ToolAction("play", Parameters = new string[] { "layer", "state", "target" }, Inject = new string[] { "action=play" })]
        private object Play(JsonClass args)
        {
            Animator animator;
            var error = ResolveAnimator(args, out animator);
            if (error != null)
                return error;

            var name = (args["state"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'state' is required for play.");

            var layer = args["layer"].AsIntDefault(0);
            animator.Play(name, layer, 0f);
            return Response.Success("Playing state '" + name + "'.", DescribeAnimator(animator));
        }

        [ToolAction("crossfade", Parameters = new string[] { "layer", "state", "duration", "target" }, Inject = new string[] { "action=crossfade" })]
        private object CrossFade(JsonClass args)
        {
            Animator animator;
            var error = ResolveAnimator(args, out animator);
            if (error != null)
                return error;

            var name = (args["state"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'state' is required for crossfade.");

            var layer = args["layer"].AsIntDefault(0);
            var duration = args["duration"].AsFloatDefault(0.25f);
            animator.CrossFade(name, duration, layer, 0f);
            return Response.Success("Cross fading to '" + name + "'.", DescribeAnimator(animator));
        }

        [ToolAction("set_parameter", Parameters = new string[] { "parameter", "default_bool", "target", "value" }, Inject = new string[] { "action=set_parameter" })]
        private object SetParameter(JsonClass args)
        {
            Animator animator;
            var error = ResolveAnimator(args, out animator);
            if (error != null)
                return error;

            var name = (args["parameter"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'parameter' is required for set_parameter.");

            var value = args["value"].AsFloatDefault(0f);
            var boolean = args["default_bool"].AsBoolDefault(false);

            animator.SetFloat(name, value);
            animator.SetInteger(name, Mathf.RoundToInt(value));
            animator.SetBool(name, boolean);
            if (boolean)
                animator.SetTrigger(name);

            return Response.Success("Parameter '" + name + "' updated.", DescribeAnimator(animator));
        }

        [ToolAction("get_runtime", Parameters = new string[] { "target" }, Required = new string[] { "target" }, Inject = new string[] { "action=get_runtime" })]
        private object GetRuntime(JsonClass args)
        {
            Animator animator;
            var error = ResolveAnimator(args, out animator);
            if (error != null)
                return error;

            return Response.Success("Animator runtime info retrieved.", DescribeAnimator(animator));
        }

        // ---------------- 辅助 ----------------

        private object LoadController(JsonClass args, out AnimatorController controller, out string path)
        {
            controller = null;
            path = SanitizeAssetPath(args["path"]?.Value);
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required.");

            controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                return Response.Error("Animator controller not found: " + path);

            return null;
        }

        private object ResolveLayer(AnimatorController controller, string spec, out AnimatorControllerLayer layer)
        {
            layer = default(AnimatorControllerLayer);
            int index;
            var error = ResolveLayerIndex(controller, spec, out index);
            if (error != null)
                return error;

            layer = controller.layers[index];
            return null;
        }

        private object ResolveLayerIndex(AnimatorController controller, string spec, out int index)
        {
            index = 0;
            var layers = controller.layers;
            if (layers.Length == 0)
                return Response.Error("Animator controller has no layers.");

            var value = (spec ?? "").Trim();
            if (string.IsNullOrEmpty(value))
                return null;

            int parsed;
            if (int.TryParse(value, out parsed))
            {
                if (parsed < 0 || parsed >= layers.Length)
                    return Response.Error("Layer index out of range: " + parsed);
                index = parsed;
                return null;
            }

            for (int i = 0; i < layers.Length; i++)
            {
                if (!string.Equals(layers[i].name, value, StringComparison.OrdinalIgnoreCase))
                    continue;
                index = i;
                return null;
            }

            return Response.Error("Layer not found: " + value);
        }

        private object ResolveLayerMachine(AnimatorController controller, string spec, out AnimatorStateMachine machine)
        {
            machine = null;
            AnimatorControllerLayer layer;
            var error = ResolveLayer(controller, spec, out layer);
            if (error != null)
                return error;

            machine = layer.stateMachine;
            if (machine == null)
                return Response.Error("Layer has no state machine.");

            return null;
        }

        private static AnimatorState FindState(AnimatorStateMachine machine, string name)
        {
            if (machine == null || string.IsNullOrEmpty(name))
                return null;

            var children = machine.states;
            for (int i = 0; i < children.Length; i++)
            {
                var state = children[i].state;
                if (state != null && string.Equals(state.name, name, StringComparison.Ordinal))
                    return state;
            }

            return null;
        }

        private static AnimatorStateTransition FindTransition(AnimatorStateMachine machine, string fromName, string toName)
        {
            var from = FindState(machine, fromName);
            if (from == null || from.transitions == null)
                return null;

            for (int i = 0; i < from.transitions.Length; i++)
            {
                var transition = from.transitions[i];
                if (transition != null && transition.destinationState != null &&
                    string.Equals(transition.destinationState.name, toName, StringComparison.Ordinal))
                    return transition;
            }

            return null;
        }

        private object ResolveAnimator(JsonClass args, out Animator animator)
        {
            animator = null;

            if (!Application.isPlaying)
                return Response.Error("Runtime actions require Play Mode. Enter Play Mode first (gameplay action 'play').");

            var target = (args["target"]?.Value ?? "").Trim();
            if (!string.IsNullOrEmpty(target))
            {
                var go = GameObjectUtils.FindObjectByIdOrPath(target);
                if (go == null)
                    return Response.Error("Target GameObject not found: " + target);
                animator = go.GetComponent<Animator>();
            }
            else
            {
                var candidate = UnityEngine.Object.FindObjectOfType<Animator>();
                animator = candidate;
            }

            if (animator == null)
                return Response.Error("No Animator found. Provide 'target' with the GameObject path or instance ID.");

            return null;
        }

        private static object DescribeAnimator(Animator animator)
        {
            var current = animator.GetCurrentAnimatorStateInfo(0);
            var parameters = new List<object>();
            if (animator.parameterCount > 0)
            {
                var list = animator.parameters;
                for (int i = 0; i < list.Length && i < 64; i++)
                {
                    var parameter = list[i];
                    object value = null;
                    switch (parameter.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            value = animator.GetFloat(parameter.name);
                            break;
                        case AnimatorControllerParameterType.Int:
                            value = animator.GetInteger(parameter.name);
                            break;
                        case AnimatorControllerParameterType.Bool:
                        case AnimatorControllerParameterType.Trigger:
                            value = animator.GetBool(parameter.name);
                            break;
                    }

                    parameters.Add(new { name = parameter.name, type = parameter.type.ToString(), value = value });
                }
            }

            return new
            {
                game_object = animator.gameObject.name,
                path = BuildTransformPath(animator.gameObject.transform),
                speed = animator.speed,
                layer_count = animator.layerCount,
                current_state_hash = current.fullPathHash,
                current_state_normalized_time = current.normalizedTime,
                current_state_is_looping = current.loop,
                parameters = parameters
            };
        }

        private object BuildInfo(AnimatorController controller, string path)
        {
            var parameters = new List<object>();
            var parameterList = controller.parameters;
            for (int i = 0; i < parameterList.Length && i < 200; i++)
            {
                var parameter = parameterList[i];
                object defaultValue = null;
                if (parameter.type == AnimatorControllerParameterType.Float)
                    defaultValue = parameter.defaultFloat;
                else if (parameter.type == AnimatorControllerParameterType.Int)
                    defaultValue = parameter.defaultInt;
                else
                    defaultValue = parameter.defaultBool;

                parameters.Add(new
                {
                    name = parameter.name,
                    type = parameter.type.ToString(),
                    default_value = defaultValue
                });
            }

            var layers = new List<object>();
            var layerList = controller.layers;
            for (int i = 0; i < layerList.Length; i++)
            {
                var machine = layerList[i].stateMachine;
                layers.Add(new
                {
                    index = i,
                    name = layerList[i].name,
                    weight = layerList[i].defaultWeight,
                    blending_mode = layerList[i].blendingMode.ToString(),
                    state_count = machine != null && machine.states != null ? machine.states.Length : 0,
                    default_state = machine != null && machine.defaultState != null ? machine.defaultState.name : null
                });
            }

            return new
            {
                path = path,
                name = Path.GetFileNameWithoutExtension(path),
                parameter_count = parameters.Count,
                layer_count = layers.Count,
                parameters = parameters,
                layers = layers
            };
        }

        private static AnimatorControllerParameterType ParseParameterType(string value)
        {
            var type = (value ?? "").Trim().ToLowerInvariant();
            switch (type)
            {
                case "int":
                case "integer":
                    return AnimatorControllerParameterType.Int;
                case "bool":
                case "boolean":
                    return AnimatorControllerParameterType.Bool;
                case "trigger":
                    return AnimatorControllerParameterType.Trigger;
                default:
                    return AnimatorControllerParameterType.Float;
            }
        }

        private static AnimatorConditionMode? ParseConditionMode(string value)
        {
            var mode = (value ?? "").Trim().ToLowerInvariant();
            switch (mode)
            {
                case "if":
                    return AnimatorConditionMode.If;
                case "ifnot":
                case "if_not":
                    return AnimatorConditionMode.IfNot;
                case "greater":
                    return AnimatorConditionMode.Greater;
                case "less":
                    return AnimatorConditionMode.Less;
                case "equals":
                case "equal":
                    return AnimatorConditionMode.Equals;
                case "notequal":
                case "not_equal":
                    return AnimatorConditionMode.NotEqual;
                default:
                    return null;
            }
        }

        private static bool TryParseCondition(string raw, out string parameter, out string mode, out float threshold)
        {
            parameter = null;
            mode = null;
            threshold = 0f;

            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var parts = raw.Split(':');
            if (parts.Length < 2 || parts.Length > 3)
                return false;

            parameter = parts[0].Trim();
            mode = parts[1].Trim();
            if (string.IsNullOrEmpty(parameter) || string.IsNullOrEmpty(mode))
                return false;

            if (parts.Length == 3)
            {
                if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out threshold))
                    threshold = 0f;
            }

            return true;
        }

        private static List<string> ReadStringArray(JsonClass args, string key)
        {
            var result = new List<string>();
            var array = args[key] as JsonArray;
            if (array == null)
            {
                var single = args[key]?.Value;
                if (!string.IsNullOrWhiteSpace(single))
                    result.Add(single.Trim());
                return result;
            }

            for (int i = 0; i < array.Count; i++)
            {
                var value = array[i]?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(value.Trim());
            }

            return result;
        }

        private static string BuildTransformPath(Transform transform)
        {
            if (transform == null)
                return null;

            var parts = new List<string>();
            var current = transform;
            while (current != null)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }

            return string.Join("/", parts.ToArray());
        }

        private static string SanitizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            path = path.Trim().Replace('\\', '/');
            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return path;

            return "Assets/" + path.TrimStart('/');
        }

        private static void EnsureDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(directory))
                return;

            var full = Path.Combine(Directory.GetCurrentDirectory(), directory);
            if (!Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
                AssetDatabase.Refresh();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniMcp;
using UniMcp.Models;

namespace UniMcp.Tools
{
    /// <summary>
    /// Geometry-aware transform tools for a model group (a root GameObject with
    /// many child MeshRenderers). The central concept is the ORIENTED BOUNDING BOX:
    /// all child mesh corners are transformed into the root's local space first,
    /// so the reported size follows the model's own rotation instead of inflating
    /// like Unity's world-axis-aligned Renderer.bounds does.
    ///
    /// All write actions move the ROOT transform only; the child hierarchy is never
    /// touched, and rotations/scales are compensated around a geometric pivot so
    /// the model stays where the caller expects it.
    /// </summary>
    [ToolModule("transform", GroupEn = "Scene", GroupZh = "场景层级",
        Tags = new[] { "transform", "bounds", "placement", "fill" })]
    public class TransformTool : ToolModuleBase
    {
        public override string Description => L.T(
            "Inspect oriented 3D bounds of a model group, move/rotate/scale it by geometric anchors, or fill it into a hole / door frame",
            "查询模型组（含多个子 MeshRenderer）的有向 3D 包围盒，按几何锚点移动/旋转/缩放，或把模型填充进门框/洞口");

        // ------------------------------------------------------------------
        // Parameter contract
        // ------------------------------------------------------------------

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                new MethodStr("action", L.T("Action type", "操作类型"), false)
                    .SetEnumValues("bounds", "get", "set", "align", "rotate", "scale", "place", "fill"),

                // Target resolution. When nothing is supplied the current selection is used.
                new MethodStr("target", L.T("Target selector: hierarchy path, object name, 'selection', or '#<instanceId>'", "目标选择器：层级路径、对象名、'selection' 或 '#<实例ID>'"))
                    .AddExample("Props/Chair"),
                new MethodStr("path", L.T("Hierarchy path of the target GameObject", "目标对象的层级路径"))
                    .AddExample("Props/Chair"),
                new MethodInt("instance_id", L.T("GameObject instance ID", "对象实例ID")),
                new MethodStr("name", L.T("GameObject name (searched also among inactive objects)", "对象名称（也会在非激活对象中查找）")),
                new MethodBool("selection", L.T("Use the currently selected GameObject", "使用当前选中的对象")),

                // bounds
                new MethodStr("space", L.T("OBB basis: 'root' follows the model rotation, 'world' is axis aligned, 'minimal' searches the tightest-volume orientation. For fill this selects the HOLE basis.", "OBB 基准：'root' 跟随模型旋转，'world' 世界轴对齐，'minimal' 搜索体积最小的朝向；fill 时该值用于选择洞口基底。"))
                    .SetEnumValues("root", "world", "minimal").SetDefault("root"),
                new MethodBool("include_inactive", L.T("Include inactive child renderers", "包含未激活的子 Renderer")),
                new MethodBool("include_renderers", L.T("Return per-renderer bounds details", "返回每个 Renderer 的包围盒明细"))
                    .SetDefault(true),
                new MethodBool("tight", L.T("Use real mesh vertices instead of mesh.bounds corners; removes a second AABB inflation on non-box meshes", "使用真实网格顶点替代 mesh.bounds 角点，消除异形网格上的二次 AABB 膨胀")),
                new MethodBool("include_minimal", L.T("Also return an independent minimum-volume OBB as read-only reference (bounds)", "额外返回一个独立的最小体积 OBB 作只读参考（bounds）"))
                    .SetDefault(true),
                new MethodStr("obb_quality", L.T("Minimum-OBB search quality: fast, normal, fine", "最小 OBB 搜索质量：fast、normal、fine"))
                    .SetEnumValues("fast", "normal", "fine").SetDefault("normal"),

                // transform values
                new MethodVector("position", L.T("Position [x,y,z]; world by default, local when local=true", "位置 [x,y,z]；默认世界坐标，local=true 时为局部坐标")),
                new MethodVector("euler", L.T("Euler angles in degrees [x,y,z]", "欧拉角（度）[x,y,z]")),
                new MethodVector("rotation", L.T("Quaternion [x,y,z,w]", "四元数 [x,y,z,w]"), true, 4),
                new MethodVector("scale", L.T("Local scale [x,y,z]", "局部缩放 [x,y,z]")),
                new MethodBool("local", L.T("Interpret position/rotation/scale as local values", "把 position/rotation/scale 当作局部值")),
                new MethodBool("relative", L.T("Treat values as deltas instead of absolute values", "把数值当作增量而非绝对值")),
                new MethodStr("axes", L.T("Axis mask for movement, e.g. 'xyz', 'xz', 'y' or '1,1,0'", "位移轴掩码，如 'xyz'、'xz'、'y' 或 '1,1,0'"))
                    .SetDefault("xyz"),
                new MethodVector("offset", L.T("World-space offset added to the current anchor", "叠加到当前锚点的世界空间偏移")),

                // anchors / pivots
                new MethodStr("anchor", L.T("Bounds anchor used by align/place/fill: center, bottom_center, min, max, pivot, custom. For fill the SAME anchor is used on both the model and the hole.", "align/place/fill 使用的包围盒锚点：center、bottom_center、min、max、pivot、custom；fill 时模型与洞口使用同名锚点对齐。"))
                    .SetEnumValues("center", "bottom_center", "min", "max", "pivot", "custom").SetDefault("center"),
                new MethodVector("anchor_point", L.T("Custom anchor in box-local space (anchor=custom); equals root-local when space=root", "自定义锚点（包围盒局部坐标，anchor=custom 时生效；space=root 时即根节点局部坐标）")),
                new MethodVector("to", L.T("Target WORLD position for the chosen anchor", "所选锚点要对齐到的世界坐标")),
                new MethodStr("pivot", L.T("Rotation/scale pivot: center (geometry center), origin (transform position), custom", "旋转/缩放的轴心：center（几何中心）、origin（变换原点）、custom"))
                    .SetEnumValues("center", "origin", "custom").SetDefault("center"),
                new MethodVector("pivot_point", L.T("Custom WORLD pivot point (pivot=custom)", "自定义世界轴心点（pivot=custom 时生效）")),

                // scale / fit
                new MethodFloat("factor", L.T("Uniform scale multiplier", "统一缩放倍数")).AddExample(2.0f),
                new MethodStr("mode", L.T("Scale mode: multiply, absolute, fit", "缩放模式：multiply、absolute、fit"))
                    .SetEnumValues("multiply", "absolute", "fit").SetDefault("multiply"),
                new MethodVector("size", L.T("Target WORLD size [x,y,z] used by mode=fit", "mode=fit 时的目标世界尺寸 [x,y,z]")),
                new MethodStr("axis", L.T("Fit reference axis: longest, x, y, z, all (also the uniform fill reference axis)", "fit 参考轴：longest、x、y、z、all（也作为 uniform fill 的参考轴）"))
                    .SetEnumValues("longest", "x", "y", "z", "all").SetDefault("longest"),
                new MethodBool("uniform", L.T("Force a uniform scale factor (scale/place/fill)", "强制等比缩放（scale/place/fill）")),

                // fill: fit a model into a hole / door frame
                new MethodStr("hole", L.T("Hole selector: hierarchy path, object name, 'selection', or '#<instanceId>'; its oriented bounds define the cavity. Must not be the target itself or one of its parents.", "洞口选择器：层级路径、对象名、'selection' 或 '#<实例ID>'；用其有向包围盒定义洞口腔体。不能是目标本身或其父级。"))
                    .AddExample("Building/DoorFrame_01"),
                new MethodVector("hole_size", L.T("Explicit hole size [x,y,z] along the hole basis (used when 'hole' is absent)", "显式洞口尺寸 [x,y,z]（沿洞口基底轴；未提供 'hole' 时生效）")),
                new MethodVector("hole_center", L.T("Explicit hole centre in WORLD space (default [0,0,0])", "显式洞口中心（世界坐标，默认 [0,0,0]）")),
                new MethodVector("hole_min", L.T("Explicit WORLD AABB min of the hole (alternative to hole_size/hole_center)", "洞口的世界 AABB 最小点（可替代 hole_size/hole_center）")),
                new MethodVector("hole_max", L.T("Explicit WORLD AABB max of the hole", "洞口的世界 AABB 最大点")),
                new MethodVector("hole_euler", L.T("Explicit hole basis rotation as euler degrees [x,y,z]", "显式洞口基底旋转（欧拉角，度）[x,y,z]")),
                new MethodVector("hole_rotation", L.T("Explicit hole basis rotation as quaternion [x,y,z,w]", "显式洞口基底旋转（四元数 xyzw）"), true, 4),
                new MethodStr("fill_axes", L.T("Axes IN THE HOLE BASIS stretched to the hole size; unlisted axes keep their own thickness. 'xy' fits a door's width+height into a frame without thickening it.", "在洞口基底下被拉伸到洞口尺寸的轴；未列出的轴保持原厚度。'xy' 即把门的宽+高填进门框，而不会把门扇拉成墙厚。"))
                    .SetEnumValues("xy", "xz", "yz", "xyz", "x", "y", "z").SetDefault("xy"),
                new MethodVector("margin", L.T("Per-axis clearance subtracted from the hole size (world units), e.g. [0.02,0.02,0] leaves a 2cm door gap", "按轴从洞口尺寸中扣掉的间隙（世界单位），如 [0.02,0.02,0] 表示留 2cm 门缝")),
                new MethodBool("align_rotation", L.T("Rotate the model so its axes match the hole basis BEFORE scaling; required for non-uniform fills", "缩放前先把模型旋转到与洞口基底一致；非等比填充时必需"))
                    .SetDefault(true),

                new MethodBool("dry_run", L.T("Compute and return the result without writing to the scene", "只计算并返回结果，不写入场景"))
            };
        }

        // ------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------

        [ToolAction("bounds",
            DescriptionEn = "Get 3D bounds of a model group: OBB + world AABB + 8 corners + pivot offset + per-child-renderer details. space=root follows the model rotation (no AABB inflation); space=world is axis aligned; space=minimal searches the least-volume orientation. tight=true uses real mesh vertices (removes mesh.bounds inflation). include_minimal adds a read-only minimum-volume OBB reference.",
            DescriptionZh = "查询模型组的 3D 包围盒：OBB + 世界 AABB + 8 角点 + pivot 偏移 + 每个子 Renderer 明细。space=root 沿模型旋转轴计算（不随世界旋转膨胀）；space=world 世界轴对齐；space=minimal 搜索体积最小朝向。tight=true 使用真实网格顶点（消除 mesh.bounds 膨胀）。include_minimal 额外返回只读的最小体积 OBB 参考。",
            Parameters = new[] { "target", "path", "instance_id", "name", "selection", "space", "include_inactive", "include_renderers", "tight", "include_minimal", "obb_quality" },
            Inject = new[] { "action=bounds" })]
        private object ActionBounds(JsonClass args)
        {
            Transform root;
            string error = Resolve(args, out root);
            if (error != null) return Response.Error(error);

            Box box = ComputeBox(root, Options(args, Bool(args, "include_renderers", true),
                Bool(args, "include_minimal", true)));

            JsonClass data = BoxJson(box, root);
            return Response.Success(
                L.T("Oriented bounds computed: " + box.RendererCount + " renderer(s), size " + Fmt(box.Size) + ".",
                    "已计算有向包围盒：" + box.RendererCount + " 个 Renderer，尺寸 " + Fmt(box.Size) + "。"), data);
        }

        [ToolAction("get",
            DescriptionEn = "Read a GameObject's transform (world/local position, rotation, euler, scale, lossyScale) together with a compact bounds summary.",
            DescriptionZh = "读取对象的变换信息（世界/局部位置、四元数、欧拉角、缩放、lossyScale），并附带简要包围盒摘要。",
            Parameters = new[] { "target", "path", "instance_id", "name", "selection", "local", "include_inactive", "space", "tight", "obb_quality" },
            Inject = new[] { "action=get" })]
        private object ActionGet(JsonClass args)
        {
            Transform root;
            string error = Resolve(args, out root);
            if (error != null) return Response.Error(error);

            JsonClass data = PoseJson(root);
            Box box = ComputeBox(root, Options(args, false, false));
            JsonClass summary = new JsonClass();
            summary["center"] = V3(box.Center);
            summary["size"] = V3(box.Size);
            summary["aabb_min"] = V3(box.AabbMin);
            summary["aabb_max"] = V3(box.AabbMax);
            summary["renderer_count"] = new JsonData(box.RendererCount);
            data["bounds"] = summary;
            return Response.Success(L.T("Transform of '" + root.name + "'.", "'" + root.name + "' 的变换信息。"), data);
        }

        [ToolAction("set",
            DescriptionEn = "Set position / euler / quaternion / scale in one call. Supports world or local space (local=true lifts local values through the parent), per-axis position mask, relative deltas (always on world axes), and dry_run. 'scale' sets localScale.",
            DescriptionZh = "一次调用设置 position / 欧拉角 / 四元数 / 缩放。支持世界或局部坐标（local=true 时经父级换算）、按轴掩码、增量模式（增量一律按世界轴）与 dry_run 预演；scale 设置的是 localScale。",
            Parameters = new[] { "target", "path", "instance_id", "name", "selection", "position", "euler", "rotation", "scale", "local", "relative", "axes", "offset", "dry_run", "include_inactive", "space", "tight", "obb_quality" },
            Inject = new[] { "action=set" })]
        private object ActionSet(JsonClass args)
        {
            Transform root;
            string error = Resolve(args, out root);
            if (error != null) return Response.Error(error);

            bool local = Bool(args, "local", false);
            bool relative = Bool(args, "relative", false);
            Vector3 mask = AxisMask(Str(args, "axes", "xyz"));
            JsonClass before = PoseJson(root);

            Vector3 pos;
            bool hasPos = Vec3(args, "position", out pos);
            Vector3 euler;
            bool hasEuler = Vec3(args, "euler", out euler);
            Quaternion quat;
            bool hasQuat = Vec4(args, "rotation", out quat);
            Vector3 scl;
            bool hasScale = Vec3(args, "scale", out scl);
            Vector3 offset;
            bool hasOffset = Vec3(args, "offset", out offset);

            if (!hasPos && !hasEuler && !hasQuat && !hasScale && !hasOffset)
                return Response.Error(L.T("Nothing to set: provide position, euler, rotation, scale or offset.", "没有可设置的内容：请提供 position、euler、rotation、scale 或 offset。"));

            Vector3 newWorldPos = root.position;
            if (hasPos)
            {
                if (local)
                {
                    Vector3 targetLocal = relative ? root.localPosition + Vector3.Scale(pos, mask) : Vector3.Scale(pos, mask) + Vector3.Scale(root.localPosition, Vector3.one - mask);
                    newWorldPos = root.parent != null ? root.parent.TransformPoint(targetLocal) : targetLocal;
                }
                else
                {
                    newWorldPos = relative
                        ? root.position + Vector3.Scale(pos, mask)
                        : Vector3.Scale(pos, mask) + Vector3.Scale(root.position, Vector3.one - mask);
                }
            }
            if (hasOffset) newWorldPos += Vector3.Scale(offset, mask);

            Quaternion newWorldRot = root.rotation;
            // Absolute values in local mode must be lifted into world space via
            // the parent rotation. Relative deltas are always applied on world axes.
            Quaternion parentRot = (local && root.parent != null) ? root.parent.rotation : Quaternion.identity;
            if (hasQuat)
                newWorldRot = relative ? quat * root.rotation : parentRot * quat;
            else if (hasEuler)
            {
                Quaternion eulerQ = Quaternion.Euler(euler);
                newWorldRot = relative ? eulerQ * root.rotation : parentRot * eulerQ;
            }

            Vector3 newLocalScale = root.localScale;
            if (hasScale)
                newLocalScale = relative ? Vector3.Scale(root.localScale, scl) : scl;

            JsonClass preview = new JsonClass();
            preview["position"] = V3(newWorldPos);
            preview["rotation"] = V4(newWorldRot);
            preview["euler"] = V3(newWorldRot.eulerAngles);
            preview["local_scale"] = V3(newLocalScale);

            if (Bool(args, "dry_run", false))
            {
                JsonClass dry = new JsonClass();
                dry["applied"] = new JsonData(false);
                dry["before"] = before;
                dry["after"] = preview;
                return Response.Success(L.T("Dry run: no changes written.", "预演：未写入任何改动。"), dry);
            }

            Undo.RecordObject(root, "UniMcp Transform Set");
            root.position = newWorldPos;
            root.rotation = newWorldRot;
            root.localScale = newLocalScale;
            Commit(root);

            JsonClass data = new JsonClass();
            data["applied"] = new JsonData(true);
            data["before"] = before;
            data["after"] = PoseJson(root);
            return Response.Success(L.T("Transform updated on '" + root.name + "'.", "已更新 '" + root.name + "' 的变换。"), data);
        }

        [ToolAction("align",
            DescriptionEn = "Move the ROOT so a chosen bounds anchor lands on a world point. Fixes the classic 'pivot is not at the geometry center' problem. Child hierarchy stays untouched; supports axis masks, offsets and dry_run. Ground snapping: space=world, anchor=min, axes=y, to=[x,groundY,z]. With space=minimal, anchors refer to the least-volume box.",
            DescriptionZh = "移动根节点，让所选包围盒锚点对齐到目标世界坐标，解决 pivot 不在几何中心导致模型放偏的问题。子层级保持不变；支持轴掩码、偏移与 dry_run。贴地用法：space=world、anchor=min、axes=y、to=[x,地面高度,z]；space=minimal 时锚点基于最小体积盒。",
            Parameters = new[] { "target", "path", "instance_id", "name", "selection", "anchor", "anchor_point", "to", "offset", "axes", "dry_run", "include_inactive", "space", "tight", "obb_quality" },
            Inject = new[] { "action=align" })]
        private object ActionAlign(JsonClass args)
        {
            Transform root;
            string error = Resolve(args, out root);
            if (error != null) return Response.Error(error);

            Box box = ComputeBox(root, Options(args, false, false));
            Vector3 anchorWorld = AnchorWorld(box, Str(args, "anchor", "center"), args);
            Vector3 to;
            bool hasTo = Vec3(args, "to", out to);
            Vector3 offset;
            bool hasOffset = Vec3(args, "offset", out offset);
            if (!hasTo && !hasOffset)
                return Response.Error(L.T("Provide 'to' (target world point) or 'offset'.", "请提供 'to'（目标世界坐标）或 'offset'。"));

            Vector3 goal = hasTo ? to : anchorWorld + offset;
            Vector3 mask = AxisMask(Str(args, "axes", "xyz"));
            Vector3 delta = Vector3.Scale(goal - anchorWorld, mask);
            Vector3 newPos = root.position + delta;

            JsonClass preview = new JsonClass();
            preview["anchor"] = new JsonData(Str(args, "anchor", "center"));
            preview["anchor_before"] = V3(anchorWorld);
            preview["anchor_after"] = V3(anchorWorld + delta);
            preview["position_before"] = V3(root.position);
            preview["position_after"] = V3(newPos);
            preview["delta"] = V3(delta);

            if (Bool(args, "dry_run", false))
            {
                preview["applied"] = new JsonData(false);
                return Response.Success(L.T("Dry run: no changes written.", "预演：未写入任何改动。"), preview);
            }

            Undo.RecordObject(root, "UniMcp Transform Align");
            ApplyMove(root, delta);
            Commit(root);

            Box afterBox = ComputeBox(root, Options(args, false, false));
            preview["applied"] = new JsonData(true);
            preview["bounds_after"] = BoundsSummary(afterBox);
            return Response.Success(
                L.T("Aligned '" + root.name + "' by " + Fmt(delta) + ".", "'" + root.name + "' 已对齐，位移 " + Fmt(delta) + "。"), preview);
        }

        [ToolAction("rotate",
            DescriptionEn = "Rotate the group around a geometric pivot (center/origin/custom) with automatic position compensation, so the model does not drift. Accepts euler deltas or an absolute quaternion.",
            DescriptionZh = "绕几何轴心（center/origin/custom）旋转整组并自动补偿位置，模型不会「转着转着飘走」。接受欧拉角增量或绝对四元数。",
            Parameters = new[] { "target", "path", "instance_id", "name", "selection", "euler", "rotation", "relative", "pivot", "pivot_point", "dry_run", "include_inactive", "space", "tight", "obb_quality" },
            Inject = new[] { "action=rotate" })]
        private object ActionRotate(JsonClass args)
        {
            Transform root;
            string error = Resolve(args, out root);
            if (error != null) return Response.Error(error);

            Vector3 euler;
            bool hasEuler = Vec3(args, "euler", out euler);
            Quaternion quat;
            bool hasQuat = Vec4(args, "rotation", out quat);
            if (!hasEuler && !hasQuat)
                return Response.Error(L.T("Provide 'euler' (degrees) or 'rotation' (quaternion xyzw).", "请提供 'euler'（度）或 'rotation'（四元数 xyzw）。"));

            bool relative = Bool(args, "relative", true);
            Quaternion deltaQ;
            if (hasQuat)
                deltaQ = relative ? quat.normalized : quat.normalized * Quaternion.Inverse(root.rotation);
            else
                deltaQ = relative ? Quaternion.Euler(euler) : Quaternion.Euler(euler) * Quaternion.Inverse(root.rotation);

            Box box = ComputeBox(root, Options(args, false, false));
            Vector3 pivotWorld = PivotWorld(box, Str(args, "pivot", "center"), args);
            Vector3 newPos = pivotWorld + deltaQ * (root.position - pivotWorld);
            Quaternion newRot = deltaQ * root.rotation;

            JsonClass preview = new JsonClass();
            preview["pivot"] = V3(pivotWorld);
            preview["pivot_mode"] = new JsonData(Str(args, "pivot", "center"));
            preview["position_before"] = V3(root.position);
            preview["position_after"] = V3(newPos);
            preview["euler_before"] = V3(root.rotation.eulerAngles);
            preview["euler_after"] = V3(newRot.eulerAngles);
            preview["delta_quaternion"] = V4(deltaQ);

            if (Bool(args, "dry_run", false))
            {
                preview["applied"] = new JsonData(false);
                return Response.Success(L.T("Dry run: no changes written.", "预演：未写入任何改动。"), preview);
            }

            Undo.RecordObject(root, "UniMcp Transform Rotate");
            ApplyRotate(root, deltaQ, pivotWorld);
            Commit(root);

            preview["applied"] = new JsonData(true);
            preview["actual_euler"] = V3(root.rotation.eulerAngles);
            return Response.Success(L.T("Rotated '" + root.name + "'.", "已旋转 '" + root.name + "'。"), preview);
        }

        [ToolAction("scale",
            DescriptionEn = "Scale the group around a geometric pivot with position compensation. mode=multiply (factor or scale vector), absolute, or fit (resize the oriented bounds to a target world size by longest/x/y/z/all axis). space=minimal requires a UNIFORM factor: a Transform can only scale along its own axes.",
            DescriptionZh = "绕几何轴心缩放整组并补偿位置。mode=multiply（factor 或 scale 向量）、absolute，或 fit（按 longest/x/y/z/all 把有向包围盒缩放到目标世界尺寸）。space=minimal 时必须等比缩放：Transform 只能沿自身轴缩放。",
            Parameters = new[] { "target", "path", "instance_id", "name", "selection", "scale", "factor", "mode", "size", "axis", "uniform", "pivot", "pivot_point", "dry_run", "include_inactive", "space", "tight", "obb_quality" },
            Inject = new[] { "action=scale" })]
        private object ActionScale(JsonClass args)
        {
            Transform root;
            string error = Resolve(args, out root);
            if (error != null) return Response.Error(error);

            Box box = ComputeBox(root, Options(args, false, false));
            Vector3 ratio;
            string ratioError = ResolveScaleRatio(args, box, root, false, out ratio);
            if (ratioError != null) return Response.Error(ratioError);
            ratioError = ValidateScaleBasis(box, ratio);
            if (ratioError != null) return Response.Error(ratioError);

            Vector3 pivotWorld = PivotWorld(box, Str(args, "pivot", "center"), args);
            Vector3 newScale = Vector3.Scale(root.localScale, ratio);
            Vector3 newPos = CompensatedPosition(root, box, pivotWorld, ratio);

            JsonClass preview = new JsonClass();
            preview["mode"] = new JsonData(Str(args, "mode", "multiply"));
            preview["ratio"] = V3(ratio);
            preview["pivot"] = V3(pivotWorld);
            preview["size_before"] = V3(box.Size);
            preview["size_after_expected"] = V3(Vector3.Scale(box.Size, Abs(ratio)));
            preview["position_before"] = V3(root.position);
            preview["position_after"] = V3(newPos);
            preview["local_scale_before"] = V3(root.localScale);
            preview["local_scale_after"] = V3(newScale);

            if (Bool(args, "dry_run", false))
            {
                preview["applied"] = new JsonData(false);
                return Response.Success(L.T("Dry run: no changes written.", "预演：未写入任何改动。"), preview);
            }

            Undo.RecordObject(root, "UniMcp Transform Scale");
            ApplyScale(root, ratio, pivotWorld, box);
            Commit(root);

            Box afterBox = ComputeBox(root, Options(args, false, false));
            preview["applied"] = new JsonData(true);
            preview["size_after"] = V3(afterBox.Size);
            preview["actual_position"] = V3(root.position);
            return Response.Success(
                L.T("Scaled '" + root.name + "' by " + Fmt(ratio) + ".", "'" + root.name + "' 已缩放 " + Fmt(ratio) + "。"), preview);
        }

        [ToolAction("place",
            DescriptionEn = "One-call placement: fit size, then rotate around pivot, then align an anchor to a target world point. Each stage recomputes the oriented bounds, so the final placement is exact. Undo is recorded once. space=minimal re-searches the box at every stage, so its basis can jump after a resize/rotate; prefer space=root for chained edits.",
            DescriptionZh = "一次性摆放：先按目标尺寸缩放，再绕轴心旋转，最后把锚点对齐到目标世界坐标。每步都重新计算有向包围盒，保证最终位置精确；只记录一次 Undo。space=minimal 时每步会重新搜索盒，缩放/旋转后基底可能跳变，链式编辑建议用 space=root。",
            Parameters = new[] { "target", "path", "instance_id", "name", "selection", "to", "anchor", "anchor_point", "axes", "offset", "euler", "rotation", "pivot", "pivot_point", "size", "axis", "uniform", "dry_run", "include_inactive", "space", "tight", "obb_quality" },
            Inject = new[] { "action=place" })]
        private object ActionPlace(JsonClass args)
        {
            Transform root;
            string error = Resolve(args, out root);
            if (error != null) return Response.Error(error);

            bool dry = Bool(args, "dry_run", false);

            Vector3 size;
            bool hasSize = Vec3(args, "size", out size);
            Vector3 euler;
            bool hasEuler = Vec3(args, "euler", out euler);
            Quaternion quat;
            bool hasQuat = Vec4(args, "rotation", out quat);
            Vector3 to;
            bool hasTo = Vec3(args, "to", out to);
            Vector3 offset;
            bool hasOffset = Vec3(args, "offset", out offset);

            if (!hasSize && !hasEuler && !hasQuat && !hasTo && !hasOffset)
                return Response.Error(L.T("Nothing to place: provide size, euler/rotation, to or offset.", "没有可摆放的内容：请提供 size、euler/rotation、to 或 offset。"));

            Vector3 p0 = root.position;
            Quaternion r0 = root.rotation;
            Vector3 s0 = root.localScale;
            JsonClass before = new JsonClass();
            before["position"] = V3(p0);
            before["rotation"] = V4(r0);
            before["local_scale"] = V3(s0);
            before["bounds"] = BoundsSummary(ComputeBox(root, Options(args, false, false)));

            JsonClass stages = new JsonClass();
            if (!dry) Undo.RecordObject(root, "UniMcp Transform Place");

            // Stage 1: fit size.
            if (hasSize)
            {
                Box b = ComputeBox(root, Options(args, false, false));
                Vector3 ratio;
                string fitError = ResolveScaleRatio(args, b, root, true, out ratio);
                if (fitError == null) fitError = ValidateScaleBasis(b, ratio);
                if (fitError != null)
                {
                    if (!dry) { root.position = p0; root.rotation = r0; root.localScale = s0; }
                    return Response.Error(fitError);
                }
                Vector3 pivot = PivotWorld(b, Str(args, "pivot", "center"), args);
                ApplyScale(root, ratio, pivot, b);
                stages["fit"] = StageJson(ratio, ComputeBox(root, Options(args, false, false)));
            }

            // Stage 2: rotate around the (possibly resized) geometry center.
            if (hasEuler || hasQuat)
            {
                Box b = ComputeBox(root, Options(args, false, false));
                Quaternion deltaQ = hasQuat
                    ? quat.normalized * Quaternion.Inverse(root.rotation)
                    : Quaternion.Euler(euler);
                Vector3 pivot = PivotWorld(b, Str(args, "pivot", "center"), args);
                ApplyRotate(root, deltaQ, pivot);
                stages["rotate"] = StageJson(deltaQ.eulerAngles, ComputeBox(root, Options(args, false, false)));
            }

            // Stage 3: align the anchor last, so the final position is exact.
            if (hasTo || hasOffset)
            {
                Box b = ComputeBox(root, Options(args, false, false));
                Vector3 anchor = AnchorWorld(b, Str(args, "anchor", "center"), args);
                Vector3 goal = hasTo ? to : anchor + offset;
                Vector3 delta = Vector3.Scale(goal - anchor, AxisMask(Str(args, "axes", "xyz")));
                ApplyMove(root, delta);
                JsonClass st = new JsonClass();
                st["delta"] = V3(delta);
                st["bounds"] = BoundsSummary(ComputeBox(root, Options(args, false, false)));
                stages["align"] = st;
            }

            JsonClass result = new JsonClass();
            result["applied"] = new JsonData(!dry);
            result["before"] = before;
            result["stages"] = stages;

            if (dry)
            {
                root.position = p0;
                root.rotation = r0;
                root.localScale = s0;
                return Response.Success(L.T("Dry run: no changes written.", "预演：未写入任何改动。"), result);
            }

            Commit(root);
            result["after"] = PoseJson(root);
            result["bounds_after"] = BoundsSummary(ComputeBox(root, Options(args, false, false)));
            return Response.Success(L.T("Placed '" + root.name + "'.", "已摆放 '" + root.name + "'。"), result);
        }

        [ToolAction("fill",
            DescriptionEn = "Fit a model INTO a hole / door frame: measure the cavity (a separate GameObject, or hole_size/hole_center, or hole_min/hole_max) in an oriented basis, rotate the model to that basis, stretch only fill_axes to the hole size, then align an anchor. Unlisted axes KEEP their thickness, so a door is not inflated to wall depth. margin leaves a gap; fit_check reports the residual. Use space=minimal for a rotated frame.",
            DescriptionZh = "把模型填充进门框/洞口：用有向基底测量洞口腔体（独立对象、hole_size/hole_center 或 hole_min/hole_max），把模型旋转到该基底，仅将 fill_axes 指定的轴拉伸到洞口尺寸，最后按锚点对齐。未列出的轴保持原厚度，门扇不会被拉成墙厚。margin 可留门缝，fit_check 返回残差便于校验。斜放门框可用 space=minimal。",
            Parameters = new[] { "target", "path", "instance_id", "name", "selection", "hole", "hole_size", "hole_center", "hole_min", "hole_max", "hole_euler", "hole_rotation", "fill_axes", "margin", "uniform", "axis", "align_rotation", "anchor", "anchor_point", "offset", "pivot", "pivot_point", "dry_run", "include_inactive", "space", "tight", "obb_quality" },
            Inject = new[] { "action=fill" })]
        private object ActionFill(JsonClass args)
        {
            Transform root;
            string error = Resolve(args, out root);
            if (error != null) return Response.Error(error);

            Box hole;
            string holeError = ResolveHole(args, root, out hole);
            if (holeError != null) return Response.Error(holeError);
            hole = NormaliseHole(hole, root);

            Vector3 mask = AxisMask(Str(args, "fill_axes", "xy"));
            Vector3 margin;
            if (!Vec3(args, "margin", out margin)) margin = Vector3.zero;
            Vector3 targetSize = new Vector3(
                hole.Size.x - Mathf.Abs(margin.x),
                hole.Size.y - Mathf.Abs(margin.y),
                hole.Size.z - Mathf.Abs(margin.z));
            float[] maskCheck = { mask.x, mask.y, mask.z };
            float[] targetCheck = { targetSize.x, targetSize.y, targetSize.z };
            for (int i = 0; i < 3; i++)
                if (maskCheck[i] > 0.5f && targetCheck[i] < 1e-4f)
                    return Response.Error(L.T(
                        "margin is larger than the hole on a filled axis; the target size would be zero or negative.",
                        "某个待填充轴上的 margin 已超过洞口尺寸，目标尺寸会变成零或负数。"));

            bool alignRot = Bool(args, "align_rotation", true);
            bool uniform = Bool(args, "uniform", false);
            bool dry = Bool(args, "dry_run", false);
            string anchor = Str(args, "anchor", "center");
            string pivotMode = Str(args, "pivot", "center");
            BoxOptions opt = Options(args, false, false);

            Vector3 p0 = root.position;
            Quaternion r0 = root.rotation;
            Vector3 s0 = root.localScale;
            int dummy;

            List<Vector3> pts0 = CollectWorldPoints(root, opt, null, null, out dummy);
            Vector3 min0, max0, size0;
            ProjectPoints(pts0, hole.Rotation, out min0, out max0, out size0);
            Box door0 = SyntheticBox(root, "hole-basis", hole.Rotation, Vector3.zero, min0, max0);

            JsonClass before = new JsonClass();
            before["position"] = V3(p0);
            before["rotation"] = V4(r0);
            before["local_scale"] = V3(s0);
            before["size_in_hole_basis"] = V3(size0);

            JsonClass stages = new JsonClass();
            JsonArray warnings = new JsonArray();
            if (root.parent != null &&
                (root.parent.rotation != Quaternion.identity || root.parent.lossyScale != Vector3.one))
                warnings.Add(null, new JsonData(L.T(
                    "The parent is rotated or scaled, so localScale axes may not match world axes; the fit can be approximate. Check fit_check.residual.",
                    "父级存在旋转或缩放，localScale 轴可能与世界轴不一致，填充可能只是近似。请检查 fit_check.residual。")));

            if (!dry) Undo.RecordObject(root, "UniMcp Transform Fill");

            // Stage 1 -- align the model's own axes to the hole basis. This is a
            // mathematical prerequisite for a non-uniform fill: localScale can
            // only stretch along the Transform's own axes.
            if (alignRot)
            {
                Vector3 pivot = PivotWorld(door0, pivotMode, args);
                Quaternion deltaQ = hole.Rotation * Quaternion.Inverse(root.rotation);
                ApplyRotate(root, deltaQ, pivot);
                JsonClass st = new JsonClass();
                st["from_euler"] = V3(r0.eulerAngles);
                st["to_euler"] = V3(root.rotation.eulerAngles);
                st["pivot"] = V3(pivot);
                stages["align_rotation"] = st;
            }

            // Stage 2 -- measure the (now aligned) model in the HOLE basis and
            // stretch only the requested axes. Using one shared basis for both
            // objects is what makes a rotated frame fit exactly.
            List<Vector3> pts1 = CollectWorldPoints(root, opt, null, null, out dummy);
            Vector3 min1, max1, curSize;
            ProjectPoints(pts1, hole.Rotation, out min1, out max1, out curSize);

            float[] t = { targetSize.x, targetSize.y, targetSize.z };
            float[] c = { curSize.x, curSize.y, curSize.z };
            float[] m = { mask.x, mask.y, mask.z };
            float[] rr = { 1f, 1f, 1f };
            int axisCount = 0;
            for (int i = 0; i < 3; i++) if (m[i] > 0.5f) axisCount++;

            if (axisCount > 0 && uniform)
            {
                int refI = 0;
                float best = -1f;
                for (int i = 0; i < 3; i++)
                    if (m[i] > 0.5f && c[i] > best) { best = c[i]; refI = i; }
                string axis = Str(args, "axis", "longest").ToLowerInvariant();
                if (!string.Equals(axis, "longest", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(axis, "all", StringComparison.OrdinalIgnoreCase))
                {
                    int a = string.Equals(axis, "y", StringComparison.OrdinalIgnoreCase) ? 1
                        : string.Equals(axis, "z", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
                    if (m[a] > 0.5f) refI = a;
                }
                if (c[refI] < 1e-6f)
                {
                    Restore(root, p0, r0, s0);
                    return Response.Error(L.T("Cannot fill: the model is degenerate on the reference axis.", "无法填充：模型在参考轴上退化（尺寸为零）。"));
                }
                float s = SafeDiv(t[refI], c[refI]);
                rr[0] = rr[1] = rr[2] = s;
                if (axisCount < 3)
                    warnings.Add(null, new JsonData(L.T(
                        "uniform=true scales ALL axes, so unlisted axes also changed. Use uniform=false to keep the model's thickness.",
                        "uniform=true 会等比缩放所有轴，未列出的轴也会变化。若要保持模型厚度请设 uniform=false。")));
            }
            else if (axisCount > 0)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (m[i] <= 0.5f) continue;
                    if (c[i] < 1e-6f)
                    {
                        Restore(root, p0, r0, s0);
                        return Response.Error(L.T("Cannot fill: the model is degenerate on a filled axis.", "无法填充：模型在某个待填充轴上退化（尺寸为零）。"));
                    }
                    rr[i] = SafeDiv(t[i], c[i]);
                }
            }
            Vector3 ratio = new Vector3(rr[0], rr[1], rr[2]);

            bool nonUniform = Mathf.Abs(ratio.x - ratio.y) > 1e-5f || Mathf.Abs(ratio.y - ratio.z) > 1e-5f;
            if (!alignRot && nonUniform)
            {
                Restore(root, p0, r0, s0);
                return Response.Error(L.T(
                    "align_rotation=false requires a uniform fill: a Transform can only scale along its own axes, so a non-uniform factor along the hole basis would shear the model. Set align_rotation=true (default) or uniform=true.",
                    "align_rotation=false 时只能等比填充：Transform 只能沿自身轴缩放，沿洞口基底做非等比会剪切模型。请设 align_rotation=true（默认）或 uniform=true。"));
            }

            Box door1 = SyntheticBox(root, "hole-basis", hole.Rotation, Vector3.zero, min1, max1);
            Vector3 scalePivot = PivotWorld(door1, pivotMode, args);
            ApplyScale(root, ratio, scalePivot, door1);

            JsonClass fitStage = new JsonClass();
            fitStage["ratio"] = V3(ratio);
            fitStage["size_before"] = V3(curSize);
            fitStage["target_size"] = V3(targetSize);
            stages["fit"] = fitStage;

            // Stage 3 -- put the chosen anchor of the model on the SAME anchor of
            // the hole. Both are expressed in world space, so any basis works.
            List<Vector3> pts2 = CollectWorldPoints(root, opt, null, null, out dummy);
            Vector3 min2, max2, size2;
            ProjectPoints(pts2, hole.Rotation, out min2, out max2, out size2);
            Box door2 = SyntheticBox(root, "hole-basis", hole.Rotation, Vector3.zero, min2, max2);
            Vector3 doorAnchor = AnchorWorld(door2, anchor, args);
            Vector3 holeAnchor = AnchorWorld(hole, anchor, args);
            Vector3 off;
            bool hasOffset = Vec3(args, "offset", out off);
            Vector3 delta = holeAnchor - doorAnchor + (hasOffset ? off : Vector3.zero);
            ApplyMove(root, delta);

            JsonClass alignStage = new JsonClass();
            alignStage["anchor"] = new JsonData(anchor);
            alignStage["hole_anchor"] = V3(holeAnchor);
            alignStage["model_anchor_before"] = V3(doorAnchor);
            alignStage["delta"] = V3(delta);
            stages["align"] = alignStage;

            // Verify the final extent against the hole, in the SAME basis.
            List<Vector3> pts3 = CollectWorldPoints(root, opt, null, null, out dummy);
            Vector3 min3, max3, finalSize;
            ProjectPoints(pts3, hole.Rotation, out min3, out max3, out finalSize);
            Vector3 residual = finalSize - targetSize;
            JsonClass fitCheck = new JsonClass();
            fitCheck["hole_size"] = V3(hole.Size);
            fitCheck["target_size"] = V3(targetSize);
            fitCheck["final_size"] = V3(finalSize);
            fitCheck["residual"] = V3(residual);
            fitCheck["residual_on_filled_axes"] = V3(new Vector3(
                m[0] > 0.5f ? residual.x : 0f,
                m[1] > 0.5f ? residual.y : 0f,
                m[2] > 0.5f ? residual.z : 0f));
            fitCheck["note"] = new JsonData(L.T(
                "Residual on unlisted fill_axes is intentional: those axes keep the model's own thickness instead of being stretched to the hole depth. Judge success by residual_on_filled_axes.",
                "未列入 fill_axes 的轴上的残差是刻意的：这些轴保持模型自身厚度，不拉伸到洞口深度。是否填充成功请以 residual_on_filled_axes 为准。"));
            fitCheck["fill_axes"] = new JsonData(Str(args, "fill_axes", "xy"));
            fitCheck["max_residual_on_filled_axes"] = new JsonData(Mathf.Max(
                m[0] > 0.5f ? Mathf.Abs(residual.x) : 0f,
                Mathf.Max(m[1] > 0.5f ? Mathf.Abs(residual.y) : 0f,
                    m[2] > 0.5f ? Mathf.Abs(residual.z) : 0f)));

            JsonClass result = new JsonClass();
            result["applied"] = new JsonData(!dry);
            result["before"] = before;
            result["hole"] = HoleJson(hole, args);
            result["stages"] = stages;
            result["fit_check"] = fitCheck;
            result["warnings"] = warnings;

            if (dry)
            {
                Restore(root, p0, r0, s0);
                return Response.Success(L.T("Dry run: no changes written.", "预演：未写入任何改动。"), result);
            }

            Commit(root);
            result["after"] = PoseJson(root);
            return Response.Success(
                L.T("Filled '" + root.name + "' into the hole.", "已把 '" + root.name + "' 填充到洞口。"), result);
        }

        /// <summary>
        /// Re-express any hole box in the hole basis, using the WORLD origin as
        /// its local origin. The model's projected box uses exactly the same
        /// frame, so center/min/max/custom anchors are directly comparable.
        /// </summary>
        private static Box NormaliseHole(Box hole, Transform root)
        {
            List<Vector3> pts = new List<Vector3>(hole.Corners);
            Vector3 hMin, hMax, hSize;
            ProjectPoints(pts, hole.Rotation, out hMin, out hMax, out hSize);
            return SyntheticBox(root, hole.Space, hole.Rotation, Vector3.zero, hMin, hMax);
        }

        private static void Restore(Transform root, Vector3 p, Quaternion r, Vector3 s)
        {
            root.position = p;
            root.rotation = r;
            root.localScale = s;
        }

        private static JsonClass HoleJson(Box hole, JsonClass args)
        {
            JsonClass d = new JsonClass();
            string selector = Str(args, "hole", "");
            d["source"] = new JsonData(!string.IsNullOrEmpty(selector) ? "gameobject:" + selector : "explicit");
            d["center"] = V3(hole.Center);
            d["size"] = V3(hole.Size);
            d["rotation"] = V4(hole.Rotation);
            d["euler"] = V3(hole.Rotation.eulerAngles);
            d["basis"] = new JsonData(hole.Space == "minimal" ? "minimal-search" : hole.Space);
            d["world_aabb"] = ExtentJson(hole.AabbMin, hole.AabbMax);
            return d;
        }

        // ------------------------------------------------------------------
        // Oriented bounds core
        // ------------------------------------------------------------------

        private const int MaxObbSamples = 1024;
        private const int MaxTightVertices = 200000;

        private sealed class BoxOptions
        {
            public bool IncludeInactive;
            public bool Details;
            public string Space;
            public bool Tight;
            public string Quality;
            public bool NeedMinimal;
        }

        /// <summary>
        /// A box defined in an arbitrary orthonormal basis. 'RootBasis' keeps the
        /// exact Unity semantics (localScale included) via TransformPoint; the
        /// world/minimal bases use Origin + Rotation * local instead.
        /// </summary>
        private sealed class Box
        {
            public Transform Root;
            public string Space;
            public bool RootBasis;
            public Vector3 Origin;
            public Quaternion Rotation;
            public Vector3 LocalMin;
            public Vector3 LocalMax;
            public Vector3 Center;
            public Vector3 Size;
            public Vector3[] Corners;
            public Vector3 AabbMin;
            public Vector3 AabbMax;
            public int RendererCount;
            public int PointCount;
            public JsonArray Renderers;
            public JsonArray Warnings;
            public JsonClass Minimal;

            public Vector3 ToWorld(Vector3 local)
            {
                if (RootBasis) return Root.TransformPoint(local);
                return Origin + Rotation * local;
            }
        }

        private struct MinimalObb
        {
            public Quaternion Rotation;
            public Vector3 Center;
            public Vector3 Size;
            public float Volume;
            /// <summary>Search objective. Volume alone is flat (zero) for planar
            /// or linear geometry, so a tiny surface term breaks those ties.</summary>
            public float Score;
            public int SampledPoints;
            public int TotalPoints;
            public int Evaluations;
        }

        private static string NormaliseSpace(string space)
        {
            if (string.Equals(space, "minimal", StringComparison.OrdinalIgnoreCase)) return "minimal";
            if (string.Equals(space, "world", StringComparison.OrdinalIgnoreCase)) return "world";
            return "root";
        }

        private static BoxOptions Options(JsonClass args, bool details, bool needMinimal)
        {
            return new BoxOptions
            {
                IncludeInactive = Bool(args, "include_inactive", false),
                Details = details,
                Space = NormaliseSpace(Str(args, "space", "root")),
                Tight = Bool(args, "tight", false),
                Quality = Str(args, "obb_quality", "normal"),
                NeedMinimal = needMinimal,
            };
        }

        // ------------------------------------------------------------------
        // Approximate minimum-volume OBB
        //
        // Orientation is searched on an Euler grid over [0,90)^3 (box axes may be
        // permuted and flipped without changing volume), then refined locally
        // with halving steps. The winning basis is finally re-evaluated against
        // ALL points, so the returned box always encloses the geometry even
        // though the orientation is only an approximation of the global optimum.
        // ------------------------------------------------------------------

        private static MinimalObb SolveMinimalObb(List<Vector3> all, string quality)
        {
            List<Vector3> sample = Decimate(all, MaxObbSamples);
            int steps = 6;
            if (string.Equals(quality, "fast", StringComparison.OrdinalIgnoreCase)) steps = 3;
            else if (string.Equals(quality, "fine", StringComparison.OrdinalIgnoreCase)) steps = 10;
            float coarse = 90f / steps;

            int evaluations = 0;
            float bestScore = float.MaxValue;
            Vector3 bestEuler = Vector3.zero;
            for (int i = 0; i < steps; i++)
                for (int j = 0; j < steps; j++)
                    for (int k = 0; k < steps; k++)
                    {
                        Vector3 e = new Vector3(i * coarse, j * coarse, k * coarse);
                        float v = Evaluate(sample, e).Score;
                        evaluations++;
                        if (v < bestScore) { bestScore = v; bestEuler = e; }
                    }

            float step = coarse * 0.5f;
            int passes = string.Equals(quality, "fine", StringComparison.OrdinalIgnoreCase) ? 6 : 4;
            for (int pass = 0; pass < passes; pass++)
            {
                Vector3 baseEuler = bestEuler;
                float localScore = bestScore;
                Vector3 localEuler = bestEuler;
                for (int a = -1; a <= 1; a++)
                    for (int b = -1; b <= 1; b++)
                        for (int c = -1; c <= 1; c++)
                        {
                            Vector3 e = baseEuler + new Vector3(a * step, b * step, c * step);
                            float v = Evaluate(sample, e).Score;
                            evaluations++;
                            if (v < localScore) { localScore = v; localEuler = e; }
                        }
                bestScore = localScore;
                bestEuler = localEuler;
                step *= 0.5f;
            }

            // Final extents against the FULL point set: the box is always valid.
            MinimalObb result = Evaluate(all, bestEuler);
            result.SampledPoints = sample.Count;
            result.TotalPoints = all.Count;
            result.Evaluations = evaluations + 1;
            return result;
        }

        /// <summary>
        /// Pure-managed equivalent of Quaternion.Euler (Unity applies rotations
        /// in Z, X, Y order). Avoids the native ECall on every orientation probe
        /// -- hundreds of them per search -- and keeps the algorithm testable
        /// outside the editor.
        /// </summary>
        private static Quaternion EulerToQuaternion(Vector3 e)
        {
            const float halfDeg = Mathf.Deg2Rad * 0.5f;
            float hx = e.x * halfDeg, hy = e.y * halfDeg, hz = e.z * halfDeg;
            float sx = Mathf.Sin(hx), cx = Mathf.Cos(hx);
            float sy = Mathf.Sin(hy), cy = Mathf.Cos(hy);
            float sz = Mathf.Sin(hz), cz = Mathf.Cos(hz);
            return new Quaternion(
                sx * cy * cz + cx * sy * sz,
                cx * sy * cz - sx * cy * sz,
                cx * cy * sz - sx * sy * cz,
                cx * cy * cz + sx * sy * sz);
        }

        private static MinimalObb Evaluate(List<Vector3> pts, Vector3 euler)
        {
            Quaternion q = EulerToQuaternion(euler);
            Vector3 ex = q * Vector3.right;
            Vector3 ey = q * Vector3.up;
            Vector3 ez = q * Vector3.forward;
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, z0 = float.PositiveInfinity;
            float x1 = float.NegativeInfinity, y1 = float.NegativeInfinity, z1 = float.NegativeInfinity;
            foreach (Vector3 p in pts)
            {
                float x = Vector3.Dot(p, ex), y = Vector3.Dot(p, ey), z = Vector3.Dot(p, ez);
                x0 = Mathf.Min(x0, x); x1 = Mathf.Max(x1, x);
                y0 = Mathf.Min(y0, y); y1 = Mathf.Max(y1, y);
                z0 = Mathf.Min(z0, z); z1 = Mathf.Max(z1, z);
            }
            MinimalObb m = new MinimalObb();
            m.Rotation = q;
            m.Size = new Vector3(x1 - x0, y1 - y0, z1 - z0);
            m.Center = ex * ((x0 + x1) * 0.5f) + ey * ((y0 + y1) * 0.5f) + ez * ((z0 + z1) * 0.5f);
            m.Volume = m.Size.x * m.Size.y * m.Size.z;
            m.Score = m.Volume + 1e-6f *
                (m.Size.x * m.Size.y + m.Size.y * m.Size.z + m.Size.z * m.Size.x);
            m.SampledPoints = pts.Count;
            m.TotalPoints = pts.Count;
            m.Evaluations = 1;
            return m;
        }

        /// <summary>Keep all points when small; otherwise keep the 6 axis extremes
        /// plus an even stride sample. Used only for the orientation search.</summary>
        private static List<Vector3> Decimate(List<Vector3> pts, int max)
        {
            if (pts.Count <= max) return new List<Vector3>(pts);
            List<Vector3> res = new List<Vector3>(max);
            int ix0 = 0, ix1 = 0, iy0 = 0, iy1 = 0, iz0 = 0, iz1 = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                Vector3 p = pts[i];
                if (p.x < pts[ix0].x) ix0 = i;
                if (p.x > pts[ix1].x) ix1 = i;
                if (p.y < pts[iy0].y) iy0 = i;
                if (p.y > pts[iy1].y) iy1 = i;
                if (p.z < pts[iz0].z) iz0 = i;
                if (p.z > pts[iz1].z) iz1 = i;
            }
            int[] extremes = { ix0, ix1, iy0, iy1, iz0, iz1 };
            foreach (int idx in extremes) res.Add(pts[idx]);
            float stride = (float)pts.Count / Mathf.Max(1, max - res.Count);
            for (int k = 0; res.Count < max; k++)
                res.Add(pts[Mathf.Min(pts.Count - 1, (int)(k * stride))]);
            return res;
        }

        private static Vector3[] BoundsCorners(Vector3 min, Vector3 max)
        {
            Vector3[] p = new Vector3[8];
            for (int i = 0; i < 8; i++) p[i] = Corner(min, max, i);
            return p;
        }

        private static JsonClass MinimalObbJson(MinimalObb m)
        {
            JsonClass d = new JsonClass();
            d["center"] = V3(m.Center);
            d["size"] = V3(m.Size);
            d["rotation"] = V4(m.Rotation);
            d["euler"] = V3(m.Rotation.eulerAngles);
            d["volume"] = new JsonData(m.Volume);
            d["method"] = new JsonData("approximate-minimum-volume: euler grid + local refinement; orientation is near-optimal, extents are exact for all points");
            d["sampled_points"] = new JsonData(m.SampledPoints);
            d["total_points"] = new JsonData(m.TotalPoints);
            d["orientations_tested"] = new JsonData(m.Evaluations);
            return d;
        }

        /// <summary>
        /// Collect every child renderer's geometry as WORLD points, then express
        /// them in the requested basis. 'root' factors out the root rotation so
        /// the size is rotation-invariant; 'world' is the axis-aligned box;
        /// 'minimal' searches for the least-volume orientation. With tight=true
        /// real mesh vertices are used, removing the extra inflation that
        /// mesh.bounds introduces on non-box meshes.
        /// </summary>
        /// <summary>
        /// Collect world-space geometry points from every child renderer. Both
        /// ComputeBox and the fill action need the RAW point set: fill projects
        /// it onto the hole's basis, which ComputeBox's own basis cannot express.
        /// </summary>
        private static List<Vector3> CollectWorldPoints(Transform root, BoxOptions opt,
            JsonArray renderers, JsonArray warnings, out int rendererCount)
        {
            List<Vector3> worldPoints = new List<Vector3>();
            rendererCount = 0;
            Renderer[] rs = root.GetComponentsInChildren<Renderer>(opt.IncludeInactive);
            foreach (Renderer r in rs)
            {
                if (r == null) continue;
                Mesh mesh = null;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
                SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
                if (mesh == null && smr != null) mesh = smr.sharedMesh;

                bool meshLocal = mesh != null;
                Bounds b = meshLocal ? mesh.bounds : r.bounds;
                Vector3[] src;
                string source;
                if (opt.Tight && meshLocal && mesh.isReadable && mesh.vertexCount <= MaxTightVertices)
                {
                    src = mesh.vertices;
                    source = "mesh.vertices";
                }
                else
                {
                    src = BoundsCorners(b.min, b.max);
                    source = meshLocal ? "mesh.bounds" : "renderer.bounds";
                    if (opt.Tight && meshLocal && warnings != null)
                    {
                        string why = !mesh.isReadable
                            ? L.T("mesh is not readable", "网格不可读")
                            : L.T("vertex count exceeds the limit", "顶点数超过上限");
                        warnings.Add(null, new JsonData(L.T(
                            "'tight' fell back to mesh.bounds on '" + mesh.name + "': " + why + ".",
                            "'tight' 在 '" + mesh.name + "' 上回退到 mesh.bounds：" + why + "。")));
                    }
                }
                rendererCount++;
                if (src == null || src.Length == 0) continue;

                Matrix4x4 toWorld = r.transform.localToWorldMatrix;
                Vector3 rMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 rMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                foreach (Vector3 c in src)
                {
                    Vector3 w = meshLocal ? toWorld.MultiplyPoint3x4(c) : c;
                    worldPoints.Add(w);
                    Accumulate(ref rMin, ref rMax, w);
                }

                if (renderers != null)
                {
                    JsonClass d = new JsonClass();
                    d["path"] = new JsonData(HierarchyPath(r.transform));
                    d["type"] = new JsonData(r.GetType().Name);
                    d["mesh"] = new JsonData(meshLocal ? mesh.name : "");
                    d["using"] = new JsonData(source);
                    d["point_count"] = new JsonData(src.Length);
                    d["local_bounds"] = BoundsJson(b);
                    d["world_aabb"] = ExtentJson(rMin, rMax);
                    renderers.Add(null, d);
                }
            }

            if (worldPoints.Count == 0)
            {
                worldPoints.Add(root.position);
                if (warnings != null) warnings.Add(null, new JsonData(L.T(
                    "No Renderer found under the target; a zero-size box at the pivot is returned. Consider include_inactive=true.",
                    "目标下没有 Renderer，返回 pivot 处的零尺寸包围盒。可尝试 include_inactive=true。")));
            }
            return worldPoints;
        }

        /// <summary>
        /// Project world points into an arbitrary basis. Returned min/max are
        /// coordinates relative to the WORLD origin, so a synthetic box with
        /// Origin=zero reproduces the same world points exactly.
        /// </summary>
        private static void ProjectPoints(List<Vector3> pts, Quaternion basis,
            out Vector3 min, out Vector3 max, out Vector3 size)
        {
            Vector3 ex = basis * Vector3.right;
            Vector3 ey = basis * Vector3.up;
            Vector3 ez = basis * Vector3.forward;
            min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            foreach (Vector3 p in pts)
                Accumulate(ref min, ref max, new Vector3(
                    Vector3.Dot(p, ex), Vector3.Dot(p, ey), Vector3.Dot(p, ez)));
            size = max - min;
        }

        /// <summary>
        /// Build a box in an arbitrary basis from explicit local extents. Used
        /// for explicit holes and for the model's projected extent inside the
        /// hole basis, so the shared AnchorWorld() logic can be reused.
        /// </summary>
        private static Box SyntheticBox(Transform root, string space, Quaternion rotation,
            Vector3 origin, Vector3 localMin, Vector3 localMax)
        {
            Box box = new Box();
            box.Root = root;
            box.Space = space;
            box.RootBasis = false;
            box.Rotation = rotation;
            box.Origin = origin;
            box.LocalMin = localMin;
            box.LocalMax = localMax;
            box.Renderers = new JsonArray();
            box.Warnings = new JsonArray();
            Vector3[] corners = new Vector3[8];
            Vector3 aMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 aMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < 8; i++)
            {
                Vector3 w = box.ToWorld(Corner(localMin, localMax, i));
                corners[i] = w;
                Accumulate(ref aMin, ref aMax, w);
            }
            box.Corners = corners;
            box.AabbMin = aMin;
            box.AabbMax = aMax;
            box.Center = (aMin + aMax) * 0.5f;
            box.Size = localMax - localMin;
            return box;
        }

        /// <summary>
        /// Resolve the cavity. Either 'hole' selects a separate GameObject whose
        /// oriented bounds define it, or hole_size/hole_center (or hole_min/
        /// hole_max) describe it explicitly. The basis is whatever 'space'
        /// selects, so a rotated frame is measured without AABB inflation.
        /// </summary>
        private static string ResolveHole(JsonClass args, Transform root, out Box hole)
        {
            hole = null;
            string selector = Str(args, "hole", "");
            Vector3 size, centre, min, max, euler;
            Quaternion quat;
            bool hasSize = Vec3(args, "hole_size", out size);
            bool hasMin = Vec3(args, "hole_min", out min);
            bool hasMax = Vec3(args, "hole_max", out max);
            bool hasCentre = Vec3(args, "hole_center", out centre);
            bool hasEuler = Vec3(args, "hole_euler", out euler);
            bool hasQuat = Vec4(args, "hole_rotation", out quat);

            if (!string.IsNullOrEmpty(selector))
            {
                JsonClass q = new JsonClass();
                q["target"] = new JsonData(selector);
                Transform holeRoot;
                string e = Resolve(q, out holeRoot);
                if (e != null) return L.T("Hole not found: ", "未找到洞口对象：") + e;
                if (holeRoot == root)
                    return L.T("The hole must not be the target itself. Use a separate frame/cavity object, or hole_size/hole_center.",
                        "洞口不能是目标对象本身。请使用独立的门框/洞口腔体对象，或改用 hole_size/hole_center。");
                if (root.IsChildOf(holeRoot))
                    return L.T("The hole object is a PARENT of the target, so its bounds would include the model being filled. Use a separate cavity object, or hole_size/hole_center.",
                        "洞口对象是目标的父级，其包围盒会包含被填充的模型。请使用独立的洞口腔体对象，或改用 hole_size/hole_center。");
                hole = ComputeBox(holeRoot, Options(args, false, false));
                return null;
            }

            if (hasMin || hasMax)
            {
                if (!hasMin || !hasMax)
                    return L.T("hole_min and hole_max must be supplied together.", "hole_min 与 hole_max 必须同时提供。");
                size = max - min;
                centre = (min + max) * 0.5f;
                hasSize = true;
                hasCentre = true;
                if (hasEuler || hasQuat)
                    return L.T("hole_min/hole_max describe a world AABB and cannot be combined with hole_euler/hole_rotation. Use hole_size + hole_center instead.",
                        "hole_min/hole_max 描述的是世界 AABB，不能与 hole_euler/hole_rotation 组合。请改用 hole_size + hole_center。");
            }
            if (!hasSize)
                return L.T("Define the hole: 'hole' (a GameObject), or hole_size + hole_center, or hole_min + hole_max.",
                    "请定义洞口：'hole'（对象）、hole_size + hole_center，或 hole_min + hole_max。");
            if (size.x < 1e-6f || size.y < 1e-6f || size.z < 1e-6f)
                return L.T("Hole size must be positive on all three axes.", "洞口尺寸三个轴都必须为正。");
            if (!hasCentre) centre = Vector3.zero;

            Quaternion rot = Quaternion.identity;
            if (hasQuat) rot = quat;
            else if (hasEuler) rot = Quaternion.Euler(euler);

            hole = SyntheticBox(root, "explicit", rot, centre, -size * 0.5f, size * 0.5f);
            return null;
        }

        private static Box ComputeBox(Transform root, BoxOptions opt)
        {
            Box box = new Box();
            box.Root = root;
            box.Space = opt.Space;
            box.RootBasis = string.Equals(opt.Space, "root", StringComparison.OrdinalIgnoreCase);
            box.Rotation = box.RootBasis ? root.rotation : Quaternion.identity;
            box.Origin = box.RootBasis ? root.position : Vector3.zero;
            box.Renderers = new JsonArray();
            box.Warnings = new JsonArray();

            int rendererCount;
            List<Vector3> worldPoints = CollectWorldPoints(root, opt,
                opt.Details ? box.Renderers : null, box.Warnings, out rendererCount);
            box.RendererCount = rendererCount;
            box.PointCount = worldPoints.Count;

            bool useMinimal = string.Equals(opt.Space, "minimal", StringComparison.OrdinalIgnoreCase);
            if (useMinimal || opt.NeedMinimal)
            {
                MinimalObb mo = SolveMinimalObb(worldPoints, opt.Quality);
                box.Minimal = MinimalObbJson(mo);
                if (useMinimal)
                {
                    box.Rotation = mo.Rotation;
                    box.Origin = mo.Center;
                    box.LocalMin = -mo.Size * 0.5f;
                    box.LocalMax = mo.Size * 0.5f;
                }
            }

            if (box.RootBasis || string.Equals(opt.Space, "world", StringComparison.OrdinalIgnoreCase))
            {
                Vector3 lMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 lMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                foreach (Vector3 w in worldPoints)
                    Accumulate(ref lMin, ref lMax, box.RootBasis ? root.InverseTransformPoint(w) : w);
                box.LocalMin = lMin;
                box.LocalMax = lMax;
            }

            Vector3[] corners = new Vector3[8];
            Vector3 aMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 aMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < 8; i++)
            {
                Vector3 l = Corner(box.LocalMin, box.LocalMax, i);
                Vector3 w = box.ToWorld(l);
                corners[i] = w;
                Accumulate(ref aMin, ref aMax, w);
            }
            box.Corners = corners;
            box.AabbMin = aMin;
            box.AabbMax = aMax;
            box.Center = (aMin + aMax) * 0.5f;

            Vector3 ex = box.Rotation * Vector3.right;
            Vector3 ey = box.Rotation * Vector3.up;
            Vector3 ez = box.Rotation * Vector3.forward;
            box.Size = ProjectedExtent(corners, ex, ey, ez);
            return box;
        }

        private static JsonClass BoxJson(Box box, Transform root)
        {
            JsonClass oriented = new JsonClass();
            oriented["center"] = V3(box.Center);
            oriented["size"] = V3(box.Size);
            oriented["local_min"] = V3(box.LocalMin);
            oriented["local_max"] = V3(box.LocalMax);
            oriented["local_size"] = V3(box.LocalMax - box.LocalMin);
            oriented["local_center"] = V3((box.LocalMin + box.LocalMax) * 0.5f);
            oriented["rotation"] = V4(box.Rotation);
            oriented["euler"] = V3(box.Rotation.eulerAngles);
            JsonClass axes = new JsonClass();
            axes["right"] = V3(box.Rotation * Vector3.right);
            axes["up"] = V3(box.Rotation * Vector3.up);
            axes["forward"] = V3(box.Rotation * Vector3.forward);
            oriented["axes"] = axes;

            JsonArray cw = new JsonArray();
            JsonArray cl = new JsonArray();
            for (int i = 0; i < 8; i++)
            {
                cw.Add(null, V3(box.Corners[i]));
                cl.Add(null, V3(Corner(box.LocalMin, box.LocalMax, i)));
            }
            oriented["corners_world"] = cw;
            oriented["corners_local"] = cl;
            oriented["corner_order"] = new JsonData("bit order: i&1=x, i&2=y, i&4=z (0=min, 1=max)");

            JsonClass data = new JsonClass();
            data["target"] = new JsonData(HierarchyPath(root));
            data["space"] = new JsonData(box.Space);
            data["basis"] = new JsonData(box.RootBasis ? "root" : (box.Space == "minimal" ? "minimal-search" : "world"));
            data["renderer_count"] = new JsonData(box.RendererCount);
            data["point_count"] = new JsonData(box.PointCount);
            data["oriented"] = oriented;
            data["world_aabb"] = ExtentJson(box.AabbMin, box.AabbMax);
            data["pivot"] = V3(root.position);
            data["pivot_offset"] = V3(box.Center - root.position);
            if (box.Minimal != null) data["minimal_obb"] = box.Minimal;
            data["renderers"] = box.Renderers;
            data["warnings"] = box.Warnings;
            return data;
        }

        private static JsonClass BoundsSummary(Box box)
        {
            JsonClass d = new JsonClass();
            d["center"] = V3(box.Center);
            d["size"] = V3(box.Size);
            d["aabb_min"] = V3(box.AabbMin);
            d["aabb_max"] = V3(box.AabbMax);
            d["renderer_count"] = new JsonData(box.RendererCount);
            return d;
        }

        // ------------------------------------------------------------------
        // Anchors, pivots, transform math
        // ------------------------------------------------------------------

        private static Vector3 AnchorWorld(Box box, string anchor, JsonClass args)
        {
            Vector3 lo = box.LocalMin;
            Vector3 hi = box.LocalMax;
            if (string.Equals(anchor, "pivot", StringComparison.OrdinalIgnoreCase))
                return box.Root.position;

            Vector3 l;
            switch (anchor.ToLowerInvariant())
            {
                case "min": l = lo; break;
                case "max": l = hi; break;
                case "bottom_center":
                    l = new Vector3((lo.x + hi.x) * 0.5f, lo.y, (lo.z + hi.z) * 0.5f);
                    break;
                case "custom":
                    Vector3 custom;
                    l = Vec3(args, "anchor_point", out custom)
                        ? custom
                        : (lo + hi) * 0.5f;
                    break;
                default:
                    l = (lo + hi) * 0.5f;
                    break;
            }
            return box.ToWorld(l);
        }

        private static Vector3 PivotWorld(Box box, string pivot, JsonClass args)
        {
            if (string.Equals(pivot, "origin", StringComparison.OrdinalIgnoreCase))
                return box.Root.position;
            if (string.Equals(pivot, "custom", StringComparison.OrdinalIgnoreCase))
            {
                Vector3 custom;
                if (Vec3(args, "pivot_point", out custom)) return custom;
            }
            return box.Center;
        }

        private static void ApplyMove(Transform root, Vector3 delta)
        {
            root.position += delta;
        }

        private static void ApplyRotate(Transform root, Quaternion deltaQ, Vector3 pivotWorld)
        {
            Vector3 p = root.position;
            root.rotation = deltaQ * root.rotation;
            root.position = pivotWorld + deltaQ * (p - pivotWorld);
        }

        private static void ApplyScale(Transform root, Vector3 ratio, Vector3 pivotWorld, Box box)
        {
            root.localScale = Vector3.Scale(root.localScale, ratio);
            root.position = CompensatedPosition(root, box, pivotWorld, ratio);
        }

        /// <summary>
        /// Scaling around a geometric pivot must also move the transform origin,
        /// otherwise the model drifts. 'ratio' is expressed along the root's own
        /// axes (that is what localScale actually scales), so the pivot offset is
        /// rotated into those axes, scaled, and rotated back -- independent of
        /// which basis was used to pick the pivot.
        /// </summary>
        private static Vector3 CompensatedPosition(Transform root, Box box, Vector3 pivotWorld, Vector3 ratio)
        {
            Quaternion basis = root.rotation;
            Vector3 v = root.position - pivotWorld;
            Vector3 localV = Quaternion.Inverse(basis) * v;
            Vector3 scaledV = Vector3.Scale(localV, ratio);
            return pivotWorld + basis * scaledV;
        }

        /// <summary>
        /// A Transform can only scale along its OWN axes. A non-uniform factor
        /// therefore cannot shrink a minimal-OBB (whose basis is independent of
        /// the Transform) to a requested size; it would also rotate/reshape that
        /// box. Uniform factors commute with any rotation and are always safe.
        /// </summary>
        private static string ValidateScaleBasis(Box box, Vector3 ratio)
        {
            if (!string.Equals(box.Space, "minimal", StringComparison.OrdinalIgnoreCase)) return null;
            const float eps = 1e-5f;
            if (Mathf.Abs(ratio.x - ratio.y) > eps || Mathf.Abs(ratio.y - ratio.z) > eps)
                return L.T(
                    "space=minimal supports uniform scaling only: a Transform can scale just along its own axes, so a non-uniform factor would rotate/reshape the minimal OBB. Use uniform=true, axis=longest/x/y/z, or space=root.",
                    "space=minimal 仅支持等比缩放：Transform 只能沿自身轴缩放，非等比会改变最小 OBB 的朝向与形状。请改用 uniform=true、axis=longest/x/y/z，或 space=root。");
            return null;
        }

        /// <summary>'forceFit' is used by place: supplying 'size' always means fit,
        /// regardless of the caller's 'mode' value.</summary>
        private static string ResolveScaleRatio(JsonClass args, Box box, Transform root, bool forceFit, out Vector3 ratio)
        {
            ratio = Vector3.one;
            string mode = forceFit ? "fit" : Str(args, "mode", "multiply").ToLowerInvariant();

            if (string.Equals(mode, "fit", StringComparison.OrdinalIgnoreCase))
            {
                Vector3 target;
                if (!Vec3(args, "size", out target))
                    return L.T("mode=fit requires 'size' (target world size [x,y,z]).", "mode=fit 需要 'size'（目标世界尺寸 [x,y,z]）。");
                string axis = Str(args, "axis", "longest").ToLowerInvariant();
                bool uniform = Bool(args, "uniform", true);
                Vector3 cur = box.Size;
                if (cur.x < 1e-6f || cur.y < 1e-6f || cur.z < 1e-6f)
                    return L.T("Cannot fit: the current oriented bounds are degenerate (zero size on at least one axis).", "无法 fit：当前有向包围盒退化（至少一个轴尺寸为零）。");

                if (string.Equals(axis, "all", StringComparison.OrdinalIgnoreCase) && !uniform)
                {
                    ratio = new Vector3(SafeDiv(target.x, cur.x), SafeDiv(target.y, cur.y), SafeDiv(target.z, cur.z));
                }
                else
                {
                    float s;
                    if (string.Equals(axis, "longest", StringComparison.OrdinalIgnoreCase))
                    {
                        float longest = Mathf.Max(cur.x, Mathf.Max(cur.y, cur.z));
                        float wanted = Mathf.Max(target.x, Mathf.Max(target.y, target.z));
                        s = SafeDiv(wanted, longest);
                    }
                    else
                    {
                        int i = string.Equals(axis, "y", StringComparison.OrdinalIgnoreCase) ? 1
                            : string.Equals(axis, "z", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
                        float wantedAxis = i == 0 ? target.x : (i == 1 ? target.y : target.z);
                        float curAxis = i == 0 ? cur.x : (i == 1 ? cur.y : cur.z);
                        s = SafeDiv(wantedAxis, curAxis);
                    }
                    ratio = new Vector3(s, s, s);
                }
                return null;
            }

            if (string.Equals(mode, "absolute", StringComparison.OrdinalIgnoreCase))
            {
                Vector3 target;
                if (!Vec3(args, "scale", out target))
                    return L.T("mode=absolute requires 'scale' (target local scale).", "mode=absolute 需要 'scale'（目标 localScale）。");
                ratio = new Vector3(
                    SafeDiv(target.x, root.localScale.x),
                    SafeDiv(target.y, root.localScale.y),
                    SafeDiv(target.z, root.localScale.z));
                return null;
            }

            // multiply (default)
            Vector3 vec;
            bool hasVec = Vec3(args, "scale", out vec);
            float factor = Float(args, "factor", 1f);
            if (hasVec) ratio = vec;
            else if (args.ContainsKey("factor")) ratio = new Vector3(factor, factor, factor);
            else return L.T("mode=multiply requires 'factor' or 'scale'.", "mode=multiply 需要 'factor' 或 'scale'。");

            if (ratio.x <= 0f || ratio.y <= 0f || ratio.z <= 0f)
                return L.T("Scale factors must be positive and non-zero.", "缩放倍数必须为正且非零。");
            return null;
        }

        // ------------------------------------------------------------------
        // Target resolution
        // ------------------------------------------------------------------

        private static string Resolve(JsonClass args, out Transform root)
        {
            root = null;
            GameObject go = null;

            int id = Int(args, "instance_id", 0);
            if (id != 0) go = EditorUtility.InstanceIDToObject(id) as GameObject;

            string selector = Str(args, "target", "");
            string path = Str(args, "path", "");
            string name = Str(args, "name", "");
            if (go == null && Bool(args, "selection", false)) go = Selection.activeGameObject;

            string query = !string.IsNullOrEmpty(selector) ? selector : path;
            if (go == null && !string.IsNullOrEmpty(query))
            {
                if (query[0] == '#' && query.Length > 1)
                {
                    int parsed;
                    if (int.TryParse(query.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        go = EditorUtility.InstanceIDToObject(parsed) as GameObject;
                }
                else if (string.Equals(query, "selection", StringComparison.OrdinalIgnoreCase))
                {
                    go = Selection.activeGameObject;
                }
                else
                {
                    go = GameObject.Find(query);
                    if (go == null) go = FindByPath(query);
                    if (go == null) go = FindByName(query);
                }
            }
            if (go == null && !string.IsNullOrEmpty(name)) go = FindByName(name);
            if (go == null && !args.ContainsKey("target") && !args.ContainsKey("path")
                && !args.ContainsKey("name") && !args.ContainsKey("instance_id"))
                go = Selection.activeGameObject;

            if (go == null)
                return L.T("Target GameObject not found. Provide target/path/name/instance_id, or pass selection=true.",
                    "未找到目标对象。请提供 target/path/name/instance_id，或传 selection=true。");
            root = go.transform;
            return null;
        }

        private static GameObject FindByPath(string path)
        {
            string clean = path.TrimStart('/');
            string[] parts = clean.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            foreach (GameObject r in AllSceneRoots())
            {
                Transform cur = r.transform;
                int start = 0;
                if (cur.name == parts[0]) start = 1;
                else
                {
                    Transform found = cur.Find(clean);
                    if (found != null) return found.gameObject;
                    continue;
                }
                bool ok = true;
                for (int i = start; i < parts.Length; i++)
                {
                    cur = cur.Find(parts[i]);
                    if (cur == null) { ok = false; break; }
                }
                if (ok) return cur.gameObject;
            }
            return null;
        }

        private static GameObject FindByName(string name)
        {
            GameObject inactiveHit = null;
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || !t.gameObject.scene.IsValid()) continue;
                if (!string.Equals(t.name, name, StringComparison.Ordinal)) continue;
                if (t.gameObject.activeInHierarchy) return t.gameObject;
                if (inactiveHit == null) inactiveHit = t.gameObject;
            }
            return inactiveHit;
        }

        private static IEnumerable<GameObject> AllSceneRoots()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded) continue;
                foreach (GameObject go in sc.GetRootGameObjects()) yield return go;
            }
        }

        // ------------------------------------------------------------------
        // Small helpers
        // ------------------------------------------------------------------

        private static void Commit(Transform root)
        {
            EditorUtility.SetDirty(root);
            if (PrefabUtility.IsPartOfPrefabInstance(root.gameObject))
                PrefabUtility.RecordPrefabInstancePropertyModifications(root);
            if (root.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
        }

        private static JsonClass PoseJson(Transform t)
        {
            JsonClass d = new JsonClass();
            d["name"] = new JsonData(t.name);
            d["path"] = new JsonData(HierarchyPath(t));
            d["instance_id"] = new JsonData(t.gameObject.GetInstanceID());
            d["position"] = V3(t.position);
            d["local_position"] = V3(t.localPosition);
            d["rotation"] = V4(t.rotation);
            d["local_rotation"] = V4(t.localRotation);
            d["euler"] = V3(t.eulerAngles);
            d["local_euler"] = V3(t.localEulerAngles);
            d["local_scale"] = V3(t.localScale);
            d["lossy_scale"] = V3(t.lossyScale);
            d["active"] = new JsonData(t.gameObject.activeInHierarchy);
            return d;
        }

        private static JsonClass StageJson(Vector3 value, Box after)
        {
            JsonClass d = new JsonClass();
            d["value"] = V3(value);
            d["bounds"] = BoundsSummary(after);
            return d;
        }

        private static string HierarchyPath(Transform t)
        {
            string p = t.name;
            Transform cur = t.parent;
            while (cur != null)
            {
                p = cur.name + "/" + p;
                cur = cur.parent;
            }
            return p;
        }

        private static void Accumulate(ref Vector3 min, ref Vector3 max, Vector3 p)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        private static Vector3 Corner(Vector3 min, Vector3 max, int i)
        {
            return new Vector3(
                (i & 1) != 0 ? max.x : min.x,
                (i & 2) != 0 ? max.y : min.y,
                (i & 4) != 0 ? max.z : min.z);
        }

        private static Vector3 ProjectedExtent(Vector3[] pts, Vector3 ex, Vector3 ey, Vector3 ez)
        {
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, z0 = float.PositiveInfinity;
            float x1 = float.NegativeInfinity, y1 = float.NegativeInfinity, z1 = float.NegativeInfinity;
            foreach (Vector3 p in pts)
            {
                float px = Vector3.Dot(p, ex);
                float py = Vector3.Dot(p, ey);
                float pz = Vector3.Dot(p, ez);
                x0 = Mathf.Min(x0, px); x1 = Mathf.Max(x1, px);
                y0 = Mathf.Min(y0, py); y1 = Mathf.Max(y1, py);
                z0 = Mathf.Min(z0, pz); z1 = Mathf.Max(z1, pz);
            }
            return new Vector3(x1 - x0, y1 - y0, z1 - z0);
        }

        private static Vector3 AxisMask(string axes)
        {
            if (string.IsNullOrEmpty(axes)) return Vector3.one;
            string s = axes.Trim().ToLowerInvariant();
            if (s.IndexOf('x') >= 0 || s.IndexOf('y') >= 0 || s.IndexOf('z') >= 0)
                return new Vector3(s.IndexOf('x') >= 0 ? 1f : 0f,
                    s.IndexOf('y') >= 0 ? 1f : 0f,
                    s.IndexOf('z') >= 0 ? 1f : 0f);
            float[] nums = ParseFloats(s);
            if (nums.Length >= 3) return new Vector3(nums[0], nums[1], nums[2]);
            return Vector3.one;
        }

        private static float SafeDiv(float a, float b)
        {
            if (Mathf.Abs(b) < 1e-6f) return 1f;
            float r = a / b;
            return Mathf.Abs(r) < 1e-6f ? 1f : r;
        }

        private static Vector3 Abs(Vector3 v)
        {
            return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        }

        private static JsonClass BoundsJson(Bounds b)
        {
            JsonClass d = new JsonClass();
            d["center"] = V3(b.center);
            d["size"] = V3(b.size);
            d["min"] = V3(b.min);
            d["max"] = V3(b.max);
            return d;
        }

        private static JsonClass ExtentJson(Vector3 min, Vector3 max)
        {
            JsonClass d = new JsonClass();
            d["center"] = V3((min + max) * 0.5f);
            d["size"] = V3(max - min);
            d["min"] = V3(min);
            d["max"] = V3(max);
            return d;
        }

        private static JsonArray V3(Vector3 v)
        {
            JsonArray a = new JsonArray();
            a.Add(null, new JsonData(v.x));
            a.Add(null, new JsonData(v.y));
            a.Add(null, new JsonData(v.z));
            return a;
        }

        private static JsonArray V4(Quaternion q)
        {
            JsonArray a = new JsonArray();
            a.Add(null, new JsonData(q.x));
            a.Add(null, new JsonData(q.y));
            a.Add(null, new JsonData(q.z));
            a.Add(null, new JsonData(q.w));
            return a;
        }

        private static string Fmt(Vector3 v)
        {
            return "(" + v.x.ToString("F3", CultureInfo.InvariantCulture) + ", "
                + v.y.ToString("F3", CultureInfo.InvariantCulture) + ", "
                + v.z.ToString("F3", CultureInfo.InvariantCulture) + ")";
        }

        private static string Str(JsonClass a, string key, string fallback)
        {
            if (!a.ContainsKey(key)) return fallback;
            string s = a[key].Value;
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        private static bool Bool(JsonClass a, string key, bool fallback)
        {
            if (!a.ContainsKey(key)) return fallback;
            JsonNode n = a[key];
            string s = n.Value;
            if (string.IsNullOrEmpty(s)) return fallback;
            bool parsed;
            if (bool.TryParse(s, out parsed)) return parsed;
            if (s == "1") return true;
            if (s == "0") return false;
            return n.AsBool;
        }

        private static float Float(JsonClass a, string key, float fallback)
        {
            if (!a.ContainsKey(key)) return fallback;
            float parsed;
            return float.TryParse(a[key].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed : fallback;
        }

        private static int Int(JsonClass a, string key, int fallback)
        {
            if (!a.ContainsKey(key)) return fallback;
            int parsed;
            return int.TryParse(a[key].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed : fallback;
        }

        private static float[] ParseFloats(string raw)
        {
            string[] parts = raw.Replace('[', ' ').Replace(']', ' ').Replace(',', ' ')
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            List<float> values = new List<float>();
            foreach (string p in parts)
            {
                float f;
                if (float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) values.Add(f);
            }
            return values.ToArray();
        }

        private static bool Vec3(JsonClass a, string key, out Vector3 value)
        {
            value = Vector3.zero;
            if (!a.ContainsKey(key)) return false;
            JsonNode n = a[key];
            JsonArray arr = n as JsonArray;
            if (arr != null && arr.Count >= 3)
            {
                value = new Vector3(arr[0].AsFloat, arr[1].AsFloat, arr[2].AsFloat);
                return true;
            }
            float[] nums = ParseFloats(n.Value);
            if (nums.Length < 3) return false;
            value = new Vector3(nums[0], nums[1], nums[2]);
            return true;
        }

        private static bool Vec4(JsonClass a, string key, out Quaternion value)
        {
            value = Quaternion.identity;
            if (!a.ContainsKey(key)) return false;
            JsonNode n = a[key];
            JsonArray arr = n as JsonArray;
            float[] nums;
            if (arr != null && arr.Count >= 4)
            {
                nums = new[] { arr[0].AsFloat, arr[1].AsFloat, arr[2].AsFloat, arr[3].AsFloat };
            }
            else
            {
                nums = ParseFloats(n.Value);
                if (nums.Length < 4) return false;
            }
            Quaternion q = new Quaternion(nums[0], nums[1], nums[2], nums[3]);
            float sqrLen = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (sqrLen < 1e-8f) return false;
            value = q.normalized;
            return true;
        }
    }
}

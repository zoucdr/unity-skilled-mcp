using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UniMcp.Models;

namespace UniMcp.Tools
{
    [ToolModule("physics", GroupEn = "Physics", GroupZh = "物理与导航",
        Tags = new[] { "navigation", "collision", "query" })]
    public sealed class PhysicsWorkflowTool : ToolModuleBase, IStrictToolModule
    {
        public override string Description => "2D/3D刚体、碰撞检测、关节与NavMesh路径查询";
        protected override MethodKey[] CreateKeys() => new MethodKey[] {
            new MethodInt("instance_id", "目标对象ID"), new MethodInt("connected_id", "连接对象ID"),
            new MethodBool("is_2d", "使用2D物理"), new MethodBool("kinematic", "运动学刚体"),
            new MethodFloat("mass", "质量，大于0"), new MethodFloat("radius", "查询半径，大于0"),
            new MethodVector("origin", "查询起点"), new MethodVector("direction", "查询方向"), new MethodVector("destination", "导航终点"),
            new MethodFloat("distance", "最大距离，默认100"), new MethodInt("layer_mask", "层位掩码，默认全部"),
            new MethodInt("layer_a", "层索引0至31"), new MethodInt("layer_b", "层索引0至31"), new MethodBool("ignore", "是否忽略碰撞"),
            new MethodInt("area_mask", "NavMesh区域位掩码，默认全部"),
            new MethodStr("shape", "碰撞器形状").SetEnumValues("box", "sphere", "capsule"),
            new MethodStr("joint", "关节类型").SetEnumValues("fixed", "hinge", "spring") };

        [ToolAction("rigidbody_set", "instance_id=require", "is_2d", "mass", "kinematic")]
        private object Body(JsonClass a) => ExtensionSafety.Run(() => {
            var go = ExtensionSafety.Target(a); bool two = ExtensionSafety.Flag(a, "is_2d");
            float mass = ExtensionSafety.Number(a, "mass", 1, 0.0001f, 1e8f); bool k = ExtensionSafety.Flag(a, "kinematic");
            if (two) {
                if (go.GetComponent<Rigidbody>()) throw new ArgumentException("对象已有3D刚体");
                var b = go.GetComponent<Rigidbody2D>() ?? Undo.AddComponent<Rigidbody2D>(go);
                Undo.RecordObject(b, "MCP 配置2D刚体"); b.mass = mass; b.bodyType = k ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic; ExtensionSafety.Dirty(b);
            } else {
                if (go.GetComponent<Rigidbody2D>()) throw new ArgumentException("对象已有2D刚体");
                var b = go.GetComponent<Rigidbody>() ?? Undo.AddComponent<Rigidbody>(go);
                Undo.RecordObject(b, "MCP 配置3D刚体"); b.mass = mass; b.isKinematic = k; ExtensionSafety.Dirty(b);
            }
            return ExtensionSafety.Ok(new { instance_id = go.GetInstanceID(), is_2d = two, mass, kinematic = k });
        });

        [ToolAction("collider_add", "instance_id=require", "shape=require", "is_2d")]
        private object Collider(JsonClass a) => ExtensionSafety.Run(() => {
            var go = ExtensionSafety.Target(a); string shape = ExtensionSafety.Text(a, "shape"); bool two = ExtensionSafety.Flag(a, "is_2d");
            Type type;
            switch (shape) {
                case "box": type = two ? typeof(BoxCollider2D) : typeof(BoxCollider); break;
                case "sphere": type = two ? typeof(CircleCollider2D) : typeof(SphereCollider); break;
                case "capsule": type = two ? typeof(CapsuleCollider2D) : typeof(CapsuleCollider); break;
                default: throw new ArgumentException("未知shape");
            }
            if (go.GetComponent(type)) throw new ArgumentException("已有同类型碰撞器");
            var c = Undo.AddComponent(go, type); ExtensionSafety.Dirty(c); return ExtensionSafety.Ok(new { component_id = c.GetInstanceID(), type = type.Name });
        });

        [ToolAction("raycast", "origin=require", "direction=require", "distance", "layer_mask", "is_2d")]
        private object Ray(JsonClass a) => ExtensionSafety.Run(() => {
            var origin = ExtensionSafety.Vector(a, "origin"); var direction = ExtensionSafety.Vector(a, "direction");
            float distance = ExtensionSafety.Number(a, "distance", 100, 0.0001f, 1e7f); int mask = ExtensionSafety.Integer(a, "layer_mask", ~0);
            bool two = ExtensionSafety.Flag(a, "is_2d");
            if (direction.sqrMagnitude < 1e-12f || (two && new Vector2(direction.x, direction.y).sqrMagnitude < 1e-12f)) throw new ArgumentException("方向不能为零");
            if (two) return ExtensionSafety.Ok(Physics2D.RaycastAll(origin, direction, distance, mask).Select(h => new { instance_id = h.collider.gameObject.GetInstanceID(), h.distance, point = new[] { h.point.x, h.point.y }, normal = new[] { h.normal.x, h.normal.y } }).ToArray());
            return ExtensionSafety.Ok(Physics.RaycastAll(origin, direction.normalized, distance, mask, QueryTriggerInteraction.UseGlobal).OrderBy(h => h.distance).Select(h => new { instance_id = h.collider.gameObject.GetInstanceID(), h.distance, point = SceneWorkflowTool.V(h.point), normal = SceneWorkflowTool.V(h.normal) }).ToArray());
        });

        [ToolAction("overlap", "origin=require", "radius=require", "layer_mask", "is_2d")]
        private object Overlap(JsonClass a) => ExtensionSafety.Run(() => {
            var p = ExtensionSafety.Vector(a, "origin"); var radius = ExtensionSafety.Number(a, "radius", 1, 0.0001f, 1e5f); var mask = ExtensionSafety.Integer(a, "layer_mask", ~0);
            return ExtensionSafety.Ok(ExtensionSafety.Flag(a, "is_2d") ? Physics2D.OverlapCircleAll(p, radius, mask).Select(c => c.GetInstanceID()).ToArray() : Physics.OverlapSphere(p, radius, mask).Select(c => c.GetInstanceID()).ToArray());
        });

        [ToolAction("collision_matrix", "layer_a=require", "layer_b=require", "is_2d", "ignore")]
        private object Matrix(JsonClass a) => ExtensionSafety.Run(() => {
            int x = ExtensionSafety.Integer(a, "layer_a", 0, 0, 31), y = ExtensionSafety.Integer(a, "layer_b", 0, 0, 31); bool two = ExtensionSafety.Flag(a, "is_2d");
            if (a.ContainsKey("ignore")) {
                if (!Application.isPlaying) throw new InvalidOperationException("碰撞矩阵写入仅允许运行时；不会修改项目设置");
                bool ignore = ExtensionSafety.Flag(a, "ignore"); if (two) Physics2D.IgnoreLayerCollision(x, y, ignore); else Physics.IgnoreLayerCollision(x, y, ignore);
            }
            return ExtensionSafety.Ok(new { ignore = two ? Physics2D.GetIgnoreLayerCollision(x, y) : Physics.GetIgnoreLayerCollision(x, y), undo_supported = false });
        });

        [ToolAction("joint_add", "instance_id=require", "joint=require", "connected_id", "is_2d")]
        private object Joint(JsonClass a) => ExtensionSafety.Run(() => {
            var go = ExtensionSafety.Target(a); bool two = ExtensionSafety.Flag(a, "is_2d"); string kind = ExtensionSafety.Text(a, "joint");
            GameObject connected = a.ContainsKey("connected_id") ? ExtensionSafety.Target(a, "connected_id") : null;
            if (connected == go) throw new ArgumentException("不能连接自身");
            Type type;
            switch (kind) {
                case "fixed": type = two ? typeof(FixedJoint2D) : typeof(FixedJoint); break;
                case "hinge": type = two ? typeof(HingeJoint2D) : typeof(HingeJoint); break;
                case "spring": type = two ? typeof(SpringJoint2D) : typeof(SpringJoint); break;
                default: throw new ArgumentException("未知joint");
            }
            if (two && (!go.GetComponent<Rigidbody2D>() || (connected && !connected.GetComponent<Rigidbody2D>()))) throw new ArgumentException("目标及连接对象必须已有Rigidbody2D");
            if (!two && (!go.GetComponent<Rigidbody>() || (connected && !connected.GetComponent<Rigidbody>()))) throw new ArgumentException("目标及连接对象必须已有Rigidbody");
            var joint = Undo.AddComponent(go, type);
            if (two) ((Joint2D)joint).connectedBody = connected ? connected.GetComponent<Rigidbody2D>() : null;
            else ((Joint)joint).connectedBody = connected ? connected.GetComponent<Rigidbody>() : null;
            ExtensionSafety.Dirty(joint); return ExtensionSafety.Ok(joint.GetInstanceID());
        });

        [ToolAction("navmesh_path", "origin=require", "destination=require", "area_mask")]
        private object Path(JsonClass a) => ExtensionSafety.Run(() => {
            var start = ExtensionSafety.Vector(a, "origin"); var end = ExtensionSafety.Vector(a, "destination"); var path = new NavMeshPath();
            bool found = NavMesh.CalculatePath(start, end, ExtensionSafety.Integer(a, "area_mask", NavMesh.AllAreas), path);
            return ExtensionSafety.Ok(new { found, status = path.status.ToString(), corners = path.corners.Select(SceneWorkflowTool.V).ToArray() });
        });

        [ToolAction("navmesh_sample", "origin=require", "distance", "area_mask")]
        private object Sample(JsonClass a) => ExtensionSafety.Run(() => {
            var p = ExtensionSafety.Vector(a, "origin"); float d = ExtensionSafety.Number(a, "distance", 2, 0.0001f, 1000);
            NavMeshHit hit; bool found = NavMesh.SamplePosition(p, out hit, d, ExtensionSafety.Integer(a, "area_mask", NavMesh.AllAreas));
            return ExtensionSafety.Ok(new { found, position = SceneWorkflowTool.V(hit.position), hit.distance, hit.mask });
        });

        [ToolAction("navmesh_destination", "instance_id=require", "destination=require")]
        private object Destination(JsonClass a) => ExtensionSafety.Run(() => {
            var agent = ExtensionSafety.Component<NavMeshAgent>(a); var destination = ExtensionSafety.Vector(a, "destination");
            if (!Application.isPlaying || !agent.isOnNavMesh) throw new InvalidOperationException("需要运行模式且Agent位于NavMesh上");
            if (!agent.SetDestination(destination)) throw new InvalidOperationException("目标设置失败");
            return ExtensionSafety.Ok(new { accepted = true, undo_supported = false });
        });
    }
}

'use strict';
/**
 * model.js — 建模链路封装：多分件 → 合并 → 单 mesh（可选多 submesh）。
 *
 * 设计原则：只暴露"建什么"，不暴露"怎么调 ProBuilder"。
 * 每个方法内部处理了以下几件容易错的事：
 *
 *   - createShape 的 size 是**顶点缩放后**的尺寸，不是 transform.scale，
 *     所以报错信息里同时给出 faceCount / vertexCount，便于判断形状是否正确。
 *   - mergeObjects 会**抹掉部件的材质归属与命名**，必须在合并前记下分组；
 *     需要多材质时走 splitSubmeshes（合并后按面重建 submesh）。
 *   - uv_atlas 的 bounds_* 是**合并后 Mesh 的局部坐标**，不是世界坐标。
 *     本模块的 atlas() 返回每个面的局部矩形，调用方不要再自己加 position。
 *
 * 用法：
 *   const M = require('./model');
 *   M.configure({ project: '<Unity 工程根>' });   // 省略则运行时自动推导
 *   await M.begin('KidsTable');                  // 建空根节点并清场
 *   await M.box('Leg_FL', { size:[.06,.62,.06], position:[-.55,.31,-.35] });
 *   await M.merge({ name:'KidsTable_Mesh' });    // 得到单 mesh
 *   const uv = await M.atlas({ columns: 4 });    // 非重叠图集
 *   await M.info();                              // 打印校验信息
 */

const mcp = require('./mcp');

const SHAPES = ['Cube', 'Cylinder', 'Sphere', 'Plane', 'Prism', 'Cone', 'Stair', 'Door', 'Pipe', 'Arch', 'Sprite', 'Torus'];

function configure(opts) { return mcp.configure(opts); }

// ---------------------------------------------------------------- 建形状

const PART_PREFIX = 'PBM_';   // 部件统一前缀，合并前据此筛选

/**
 * 同步校验形状名。async 函数被 await 之前不会抛错，所以校验必须单独可调用，
 * 否则 try/catch 抓不到（自检里踩过这个坑）。
 */
function assertShape(shapeType) {
  const t = String(shapeType);
  const canon = SHAPES.find(s => s.toLowerCase() === t.toLowerCase());
  if (!canon) {
    throw new Error(`shapeType "${shapeType}" 无效。可用：${SHAPES.join(' / ')}`);
  }
  return canon;
}

async function shape(shapeType, name, opts) {
  const canon = assertShape(shapeType);
  const o = opts || {};
  const args = { shapeType: canon, name: name };
  if (o.position) args.position = o.position;
  if (o.rotation) args.rotation = o.rotation;
  if (o.size) args.size = o.size;
  if (o.parent) args.parent = o.parent;
  if (o.isLocalSpace) args.isLocalSpace = true;
  return await mcp.call('probuilder.createShape', args);
}

const box = (name, o) => shape('Cube', name, o);
const cylinder = (name, o) => shape('Cylinder', name, o);
const sphere = (name, o) => shape('Sphere', name, o);
const plane = (name, o) => shape('Plane', name, o);
const cone = (name, o) => shape('Cone', name, o);
const torus = (name, o) => shape('Torus', name, o);
const pipe = (name, o) => shape('Pipe', name, o);

/** 任意多边形棱柱：points 是 [[x,z],...]，沿 Y 拉伸 height。 */
async function prism(name, points, height, opts) {
  if (!Array.isArray(points) || points.length < 3) {
    throw new Error('prism 需要至少 3 个 [x,z] 点');
  }
  const o = opts || {};
  const args = { points, height, name };
  if (o.position) args.position = o.position;
  if (o.rotation) args.rotation = o.rotation;
  if (o.flipNormals) args.flipNormals = true;
  return await mcp.call('probuilder.createPolyShape', args);
}

// ---------------------------------------------------------------- 查

/**
 * 按名称前缀列出场景里的对象。
 *
 * 实测：工具名是 hierarchy.find_by_query（没有 hierarchy.list），
 * 返回数组元素是 { yaml: "name: ...\nid: ..." } —— **不是 JSON 对象**，
 * 所以只能从 yaml 文本里抽字段。这个结构变化过，故用宽松解析。
 */
async function findObjects(query, opts) {
  const o = opts || {};
  const data = await mcp.call('hierarchy.find_by_query', {
    query: query,
    use_regex: o.useRegex !== false,
    select_many: true,
    include_inactive: o.includeInactive === true,
  });
  const arr = Array.isArray(data) ? data : (data && data.items) || [];
  return arr.map(item => {
    const yaml = (item && (item.yaml || item.text)) || '';
    const get = (k) => {
      const m = yaml.match(new RegExp('^' + k + ':\\s*(.*)$', 'm'));
      return m ? m[1].trim() : undefined;
    };
    return {
      name: (item && item.name) || get('name'),
      id: Number((item && item.id) || get('id')),
      path: (item && item.path) || get('path'),
      scene: get('scene'),
      yaml,
    };
  }).filter(x => x.name);
}

async function listParts(prefix) {
  const pre = prefix === undefined ? PART_PREFIX : prefix;
  const pattern = pre ? '^' + pre.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '.*' : '.*';
  const found = await findObjects(pattern);
  return found.map(f => f.name);
}

/**
 * 网格统计。
 *
 * 【实测坑】一旦用 splitSubmeshes 直接替换了 MeshFilter.sharedMesh，
 * 对象上的 ProBuilderMesh 就没了，probuilder.getMeshInfo 会报
 * "没有 ProBuilderMesh 组件"。所以这里必须能降级到纯 Mesh 读取。
 */
async function info(target) {
  const args = {};
  if (typeof target === 'number') args.instanceId = target;
  else if (typeof target === 'string') args.gameObject = target;
  args.detail = 'normal';
  try {
    return await mcp.call('probuilder.getMeshInfo', args);
  } catch (e) {
    if (!/ProBuilderMesh/.test(e.message)) throw e;
    // 降级：直接读 MeshFilter.sharedMesh
    const name = typeof target === 'string' ? target : null;
    if (!name) throw e;
    const code = `
GameObject go3 = GameObject.Find(${JSON.stringify(name)});
if (go3 == null) { Debug.LogError("INFO: 找不到对象"); }
else {
    MeshFilter mf3 = go3.GetComponent<MeshFilter>();
    if (mf3 == null || mf3.sharedMesh == null) { Debug.LogError("INFO: 没有 mesh"); }
    else {
        Mesh m3 = mf3.sharedMesh;
        Bounds b3 = m3.bounds;
        string sub = "";
        for (int si = 0; si < m3.subMeshCount; si++) { if (si > 0) sub = sub + ","; sub = sub + (m3.GetTriangles(si).Length / 3); }
        Debug.Log("INFO: name=" + go3.name + "|verts=" + m3.vertexCount + "|subMeshes=" + m3.subMeshCount + "|trisPerSub=" + sub + "|boundsCenter=" + b3.center.x + "," + b3.center.y + "," + b3.center.z + "|boundsSize=" + b3.size.x + "," + b3.size.y + "," + b3.size.z);
    }
}
`;
    const r = await mcp.call('code.execute', { mode: 'runtime', code, description: 'mesh info fallback' });
    const log = (r && r.logs) || '';
    const mm = log.match(/INFO: (.+)$/m);
    if (!mm) return { raw: r, note: 'ProBuilderMesh 缺失，降级读取失败' };
    const kv = {};
    mm[1].split('|').forEach(p => {
      const i = p.indexOf('=');
      if (i > 0) kv[p.slice(0, i).trim()] = p.slice(i + 1).trim();
    });
    return {
      gameObjectName: kv.name,
      vertexCount: Number(kv.verts),
      subMeshCount: Number(kv.subMeshes),
      trianglesPerSubMesh: (kv.trisPerSub || '').split(',').filter(Boolean).map(Number),
      boundsCenter: kv.boundsCenter,
      boundsSize: kv.boundsSize,
      source: 'mesh-fallback',
      note: '该对象已被 splitSubmeshes 处理，不再有 ProBuilderMesh',
    };
  }
}

/**
 * 断言结果是单 mesh。
 * @param {object} rep info() 的返回
 * @param {object} [o] { subMeshes: 期望的 submesh 数 }
 */
function assertSingleMesh(rep, o) {
  const opt = o || {};
  const want = opt.subMeshes === undefined ? 1 : opt.subMeshes;
  const problems = [];

  // 两种数据来源：ProBuilder 报告 / 降级读取的 Mesh 报告
  const sm = rep && (rep.subMeshCount !== undefined ? rep.subMeshCount
    : (rep.submeshes !== undefined ? rep.submeshes : undefined));
  if (sm !== undefined && Number(sm) !== want) {
    problems.push(`subMeshCount=${sm}，期望 ${want}`);
  }
  const tris = rep && (rep.triangleCount !== undefined ? rep.triangleCount
    : (Array.isArray(rep.trianglesPerSubMesh) ? rep.trianglesPerSubMesh.reduce((a, b) => a + b, 0) : undefined));
  if (tris !== undefined && Number(tris) <= 0) {
    problems.push(`三角形数=${tris}，网格是空的`);
  }
  if (rep && rep.subMeshCount === undefined && rep.submeshes === undefined) {
    problems.push('报告里没有 subMeshCount，无法确认是单 mesh');
  }
  if (problems.length) {
    const err = new Error('单 mesh 校验失败：\n  - ' + problems.join('\n  - '));
    err.report = rep;
    throw err;
  }
  return rep;
}

// ---------------------------------------------------------------- 合并

/** 合并多个对象成一个 mesh。返回合并结果。 */
async function merge(opts) {
  const o = opts || {};
  const args = {};
  if (Array.isArray(o.objects)) args.gameObjects = o.objects;
  else if (Array.isArray(o.instanceIds)) args.instanceIds = o.instanceIds;
  else if (Array.isArray(o.names)) args.gameObjects = o.names;
  else throw new Error('merge 需要 objects / names / instanceIds 之一');
  if (args.gameObjects && args.gameObjects.length < 2 && (!args.instanceIds || args.instanceIds.length < 2)) {
    throw new Error('merge 至少需要 2 个对象');
  }
  if (o.deleteSources !== undefined) args.deleteSourceObjects = o.deleteSources;
  const r = await mcp.call('probuilder.mergeObjects', args);
  if (o.name && r) {
    // 实测：合并结果字段是 targetObjectName（不是 gameObjectName）
    const target = r.targetObjectName || r.gameObjectName || r.name;
    if (target) {
      try { await mcp.call('gameobject.modify', { path: target, name: o.name }); } catch (e) { /* 改名失败不影响建模 */ }
      if (r.targetObjectName) r.renamedTo = o.name;
    }
  }
  return r;
}

/**
 * 合并"所有 PBM_ 前缀的部件"成一个对象。
 * 这是最常用的单 mesh 收口动作。
 */
async function mergeParts(opts) {
  const o = opts || {};
  const names = o.names || await listParts(o.prefix === undefined ? PART_PREFIX : o.prefix);
  if (names.length < 2) {
    throw new Error(`找到 ${names.length} 个${o.prefix === undefined ? ' PBM_' : ''}部件，合并至少需要 2 个。检查命名前缀是否一致`);
  }
  return await merge({ names, deleteSources: o.deleteSources !== false, name: o.name });
}

// ---------------------------------------------------------------- UV

/**
 * 重建非重叠 UV 图集：每个几何连通部件占一格。
 * 返回 { meshPath, cells, faces }，faces 里的矩形是**局部坐标**，直接喂给 texgen。
 */
async function atlas(opts) {
  const o = opts || {};
  const args = { columns: o.columns || 4, margin: o.margin === undefined ? 0.08 : o.margin };
  if (o.rows !== undefined) args.rows = o.rows;
  if (o.gameObject) args.gameObject = o.gameObject;
  if (o.instanceId !== undefined) args.instanceId = o.instanceId;
  if (o.projection) args.projection = o.projection;
  if (o.bake !== undefined) args.bake = o.bake;
  if (o.meshPath) args.mesh_path = o.meshPath;
  if (o.width) args.width = o.width;
  if (o.height) args.height = o.height;
  return await mcp.call('probuilder.uv_atlas', args);
}

/** 查看现有 UV0：岛、覆盖率、跨岛重叠。 */
async function uvInfo(opts) {
  const o = opts || {};
  const args = { width: o.width || 512, height: o.height || 512 };
  if (o.gameObject) args.gameObject = o.gameObject;
  if (o.instanceId !== undefined) args.instanceId = o.instanceId;
  return await mcp.call('probuilder.uv_info', args);
}

// ---------------------------------------------------------------- submesh

/**
 * 合并之后按"面归属"拆出多个 submesh。
 *
 * 为什么需要这个：mergeObjects 会把所有面塞进 submesh 0，材质归属丢失。
 * 想在单 mesh 上挂多个材质，只能按几何特征（高度、朝向、命名来源）
 * 把三角形分组，然后 SetTriangles 一次写成 N 个 submesh。
 *
 * @param {object} o
 *   o.target     对象名
 *   o.groups     [{ name, yMin, yMax, facesFilter? }] 按 Y 范围分组，或用 o.classify 自定义
 *   o.classify   可选，字符串形式的 C# 分组表达式（谨慎使用）
 *   o.materialPaths 与 groups 一一对应的材质路径，可选，自动 setFaceMaterial
 */
async function splitSubmeshes(o) {
  const opt = o || {};
  if (!opt.target) throw new Error('splitSubmeshes 需要 target（对象名）');
  if (!Array.isArray(opt.groups) || opt.groups.length < 2) {
    throw new Error('splitSubmeshes 需要至少 2 个 groups');
  }

  const gs = opt.groups.map((g, i) => ({
    name: g.name || `group${i}`,
    yMin: g.yMin === undefined ? -1e9 : g.yMin,
    yMax: g.yMax === undefined ? 1e9 : g.yMax,
    xMin: g.xMin === undefined ? -1e9 : g.xMin,
    xMax: g.xMax === undefined ? 1e9 : g.xMax,
    zMin: g.zMin === undefined ? -1e9 : g.zMin,
    zMax: g.zMax === undefined ? 1e9 : g.zMax,
  }));

  // 实测：该工程**没有 SimpleJson**，任何 JSON 反序列化都会 CS0103。
  // 所以分组边界直接编成 C# 字面量数组，不让运行时解析字符串。
  const f = (v) => (Number.isFinite(v) ? v : 0) + 'f';
  const lit = (k) => 'new float[] { ' + gs.map(g => f(g[k])).join(', ') + ' }';
  const namesLit = 'new string[] { ' + gs.map(g => JSON.stringify(g.name)).join(', ') + ' }';

  // 逐三角形求质心，落进哪个 group 就把该三角形写进对应 submesh。
  // 不用 return：code.execute 的返回类型是 object，return; 会 CS0126。
  const code = `
var targetName = ${JSON.stringify(opt.target)};
int gcount = ${gs.length};
float[] gYMin = ${lit('yMin')};
float[] gYMax = ${lit('yMax')};
float[] gXMin = ${lit('xMin')};
float[] gXMax = ${lit('xMax')};
float[] gZMin = ${lit('zMin')};
float[] gZMax = ${lit('zMax')};
string[] gNames = ${namesLit};

GameObject go = GameObject.Find(targetName);
if (go == null)
{
    Debug.LogError("SPLIT: 找不到对象 " + targetName);
}
else
{
    MeshFilter mf = go.GetComponent<MeshFilter>();
    if (mf == null || mf.sharedMesh == null)
    {
        Debug.LogError("SPLIT: 对象没有 MeshFilter/sharedMesh");
    }
    else
    {
        Mesh src = mf.sharedMesh;
        Vector3[] verts = src.vertices;
        Vector2[] uvs = src.uv;
        Vector3[] nrms = src.normals;
        Color[] cols = src.colors;
        var buckets = new System.Collections.Generic.List<int>[gcount];
        for (int gi = 0; gi < gcount; gi++) { buckets[gi] = new System.Collections.Generic.List<int>(); }

        int unassigned = 0;
        for (int si = 0; si < src.subMeshCount; si++)
        {
            int[] tris = src.GetTriangles(si);
            for (int ti = 0; ti + 2 < tris.Length; ti += 3)
            {
                Vector3 c = (verts[tris[ti]] + verts[tris[ti + 1]] + verts[tris[ti + 2]]) / 3f;
                // 实测：合并后的顶点是**局部坐标**（相对合并对象的 transform）。
                // 分组边界按世界坐标描述才符合直觉，所以这里先转到世界。
                // 注意 TransformPoint 对当前顶点位置是幂等的：transform 未变。
                Vector3 wc = go.transform.TransformPoint(c);
                int best = -1;
                for (int gi = 0; gi < gcount; gi++)
                {
                    if (wc.x >= gXMin[gi] && wc.x <= gXMax[gi] && wc.y >= gYMin[gi] && wc.y <= gYMax[gi] && wc.z >= gZMin[gi] && wc.z <= gZMax[gi]) { best = gi; break; }
                }
                if (best < 0) { best = gcount - 1; unassigned++; }
                buckets[best].Add(tris[ti]);
                buckets[best].Add(tris[ti + 1]);
                buckets[best].Add(tris[ti + 2]);
            }
        }

        Mesh outMesh = new Mesh();
        outMesh.name = src.name + "_SubMesh";
        outMesh.indexFormat = verts.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        outMesh.vertices = verts;
        if (nrms != null && nrms.Length == verts.Length) outMesh.normals = nrms;
        if (uvs != null && uvs.Length == verts.Length) outMesh.uv = uvs;
        if (cols != null && cols.Length == verts.Length) outMesh.colors = cols;
        outMesh.subMeshCount = gcount;
        string counts = "";
        for (int gi = 0; gi < gcount; gi++)
        {
            outMesh.SetTriangles(buckets[gi], gi, true);
            counts = counts + gNames[gi] + "=" + (buckets[gi].Count / 3) + " ";
        }
        outMesh.RecalculateBounds();
        mf.sharedMesh = outMesh;
        Debug.Log("SPLIT: " + targetName + " subMeshCount=" + outMesh.subMeshCount + " tris " + counts + " fallback=" + unassigned);
    }
}
`;

  const res = await mcp.call('code.execute', { mode: 'runtime', code, description: 'split submeshes' });

  // 材质按组挂上去
  if (Array.isArray(opt.materialPaths) && opt.materialPaths.length === opt.groups.length) {
    const matCode = `
GameObject go2 = GameObject.Find(${JSON.stringify(opt.target)});
if (go2 == null) { Debug.LogError("MAT: 找不到对象"); }
else {
    MeshRenderer mr2 = go2.GetComponent<MeshRenderer>();
    if (mr2 == null) { Debug.LogError("MAT: 没有 MeshRenderer"); }
    else {
        string[] paths = new string[] { ${opt.materialPaths.map(p => JSON.stringify(p)).join(', ')} };
        Material[] mats = new Material[paths.Length];
        for (int i = 0; i < paths.Length; i++) {
            mats[i] = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(paths[i]);
            if (mats[i] == null) Debug.LogError("MAT: 加载失败 " + paths[i]);
        }
        mr2.sharedMaterials = mats;
        Debug.Log("MAT: 已挂 " + mats.Length + " 个材质, 实际=" + mr2.sharedMaterials.Length);
    }
}
`;
    await mcp.call('code.execute', { mode: 'runtime', code: matCode, description: 'assign submesh materials' });
  }

  return res;
}

// ---------------------------------------------------------------- 辅助

/**
 * 删除场景里名字匹配的对象（精确名或前缀）。
 *
 * 为什么必须提供：脚本反复运行时，合并结果会以同一个名字不断新增，
 * 旧的同类对象不会自动消失 —— 于是场景里堆出多个同名对象，
 * 后建的那个没挂材质就盖在已挂材质的上头，看起来像"材质没生效"。
 * 实测踩过：连跑 3 次留下 6 个 Crate，只有 1 个有材质。
 *
 * @param {string[]} names 精确名或 "前缀*" 形式
 */
async function purge(names) {
  const list = Array.isArray(names) ? names : [names];
  if (!list.length) return { removed: 0 };
  const exact = list.filter(n => !/\*$/.test(n));
  const prefixes = list.filter(n => /\*$/.test(n)).map(n => n.slice(0, -1));
  const code = `
int removed = 0;
UnityEngine.SceneManagement.Scene sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
GameObject[] roots = sc.GetRootGameObjects();
string[] exactNames = new string[] { ${exact.map(n => JSON.stringify(n)).join(', ')} };
string[] prefixes = new string[] { ${prefixes.map(n => JSON.stringify(n)).join(', ')} };
for (int i = 0; i < roots.Length; i++)
{
    GameObject g = roots[i];
    if (g == null) continue;
    bool hit = false;
    for (int k = 0; k < exactNames.Length; k++) { if (g.name == exactNames[k]) hit = true; }
    for (int k = 0; k < prefixes.Length; k++) { if (g.name.StartsWith(prefixes[k])) hit = true; }
    if (hit) { UnityEngine.Object.DestroyImmediate(g); removed++; }
}
Debug.Log("PURGE: 删除 " + removed + " 个对象");
`;
  const r = await mcp.call('code.execute', { mode: 'runtime', code, description: 'purge objects' });
  const m = ((r && r.logs) || '').match(/PURGE: 删除 (\d+)/);
  return { removed: m ? Number(m[1]) : null, logs: ((r && r.logs) || '').trim() };
}

/** 新建空根节点，便于整组管理。 */
async function begin(rootName, opts) {
  const o = opts || {};
  if (o.clean) await purge([rootName]);
  try { await mcp.call('hierarchy.create_empty', { name: rootName, position: o.position || [0, 0, 0] }); } catch (e) {
    // 已存在则忽略
  }
  return rootName;
}

/** 批量建部件：specs = [{ shape:'Cube', name:'...', size, position, rotation }] */
async function build(specs, opts) {
  const o = opts || {};
  if (!Array.isArray(specs)) throw new Error('build 需要数组');
  const made = [];
  for (const s of specs) {
    if (!s || !s.shape) throw new Error('每个 spec 需要 shape 字段');
    const r = await shape(s.shape, s.name, {
      size: s.size, position: s.position, rotation: s.rotation,
      parent: s.parent || o.parent, isLocalSpace: s.isLocalSpace,
    });
    made.push({ name: s.name, result: r });
  }
  return made;
}

/** 挤出指定面（用于把平面长出厚度）。 */
async function extrude(target, faceIndices, distance, opts) {
  const o = opts || {};
  const args = { distance: distance === undefined ? 0.1 : distance, extrudeMethod: o.method || 'IndividualFaces' };
  if (typeof target === 'string') args.gameObject = target;
  else if (typeof target === 'number') args.instanceId = target;
  if (Array.isArray(faceIndices)) args.faceIndices = faceIndices;
  if (o.faceDirection) args.faceDirection = o.faceDirection;
  return await mcp.call('probuilder.extrude', args);
}

/** 布尔运算：target 与 tool 合并 / 相减。 */
async function boolean(target, tool, operation, opts) {
  const o = opts || {};
  const args = { operation: operation || 'Union', deleteSourceObjects: o.deleteSources !== false };
  if (typeof target === 'string') args.target = target;
  else args.instanceId = target;
  if (typeof tool === 'string') args.tool = tool;
  else args.toolInstanceId = tool;
  return await mcp.call('probuilder.boolean', args);
}

/** 存场景。 */
async function saveScene(scenePath) {
  if (scenePath) return await mcp.call('scene.save', { path: scenePath });
  return await mcp.call('scene.save', {});
}

module.exports = {
  configure, SHAPES, PART_PREFIX, assertShape,
  shape, box, cylinder, sphere, plane, cone, torus, pipe, prism,
  findObjects, listParts, info, assertSingleMesh,
  merge, mergeParts,
  atlas, uvInfo, splitSubmeshes,
  begin, purge, build, extrude, boolean, saveScene,
};

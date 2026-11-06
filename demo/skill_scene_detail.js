// 一次性验证脚本：按 SKILL.md「先发现、再取定义、后调用」实机丰富当前场景。
// 已通过 discover / groups / list / find / schemas / schema 得到下列工具名与参数。
// 重构后工具改为「分组.动作」二级名称，批量调用接口已移除：多步访问由本脚本逐次编排。
// 运行：node skill_scene_detail.js   （工程根目录，Unity 不导入）
const unity = require('../unity-package/Skills/unity3d-skill/scripts/unity_mcp.js');

unity.configure({ timeout: 60 });

const ROOT = 'CannonScene/Details';
const PARENT_GROUND = ROOT + '/GroundDetails';
const PARENT_CANNON = ROOT + '/CannonDetails';
const PARENT_SKY = ROOT + '/SkyDetails';

const groups = [
  { name: 'Details', parent: 'CannonScene' },
  { name: 'GroundDetails', parent: ROOT },
  { name: 'CannonDetails', parent: ROOT },
  { name: 'SkyDetails', parent: ROOT },
];

const props = [];
const push = (parent, name, primitive_type, position, scale, rotation) => {
  props.push({ parent, name, primitive_type, position, scale, rotation });
};

// 地面：石块、草丛、踏石、靶子
[
  [-9.5, 0.25, -6, 1.1, 0.5, 0.9], [-8.2, 0.2, 4.5, 0.8, 0.4, 1.0],
  [9.5, 0.3, 6, 1.4, 0.6, 1.1], [-12, 0.18, -1, 0.7, 0.35, 0.6],
].forEach((r, i) => push(PARENT_GROUND, 'Rock_' + (i + 1), 'Sphere', [r[0], r[1], r[2]], [r[3], r[4], r[5]]));

[
  [-11, 0.3, -3], [-10.7, 0.28, -2.7], [-10.9, 0.32, -3.3],
  [6, 0.3, 3], [6.3, 0.28, 3.2], [5.8, 0.32, 2.8],
].forEach((p, i) => push(PARENT_GROUND, 'Grass_' + (i + 1), 'Cylinder', p, [0.05, 0.3, 0.05], [0, 0, (i % 3 - 1) * 12]));

[[-3, 0.03, -4, 12], [-1, 0.03, -2, -8], [1, 0.03, 0, 6], [3, 0.03, 2, -10]]
  .forEach((p, i) => push(PARENT_GROUND, 'Path_' + (i + 1), 'Cube', [p[0], p[1], p[2]], [1.4, 0.05, 0.8], [0, p[3], 0]));

push(PARENT_GROUND, 'Target_Post', 'Cylinder', [8, 1, -10], [0.12, 1, 0.12]);
push(PARENT_GROUND, 'Target_Board', 'Cylinder', [8, 2, -10], [1.1, 0.06, 1.1], [90, 0, 0]);

// 炮台：炮口环、铆钉、弹药箱与炮弹、旗杆
push(PARENT_CANNON, 'Muzzle_Ring', 'Cylinder', [-4.15, 2.05, 0], [0.42, 0.1, 0.42], [0, 0, 90]);
[[-6.9, 1.0, -0.55], [-6.9, 1.0, 0.55], [-5.1, 1.0, -0.55], [-5.1, 1.0, 0.55]]
  .forEach((p, i) => push(PARENT_CANNON, 'Rivet_' + (i + 1), 'Sphere', p, [0.1, 0.1, 0.1]));
push(PARENT_CANNON, 'Ammo_Crate', 'Cube', [-7.8, 0.35, 2.0], [0.9, 0.7, 0.7]);
[[-7.3, 0.25, 2.5], [-7.0, 0.25, 2.7], [-6.7, 0.25, 2.5]]
  .forEach((p, i) => push(PARENT_CANNON, 'Ammo_Ball_' + (i + 1), 'Sphere', p, [0.24, 0.24, 0.24]));
push(PARENT_CANNON, 'Flag_Pole', 'Cylinder', [-7.6, 1.4, -1.8], [0.05, 1.4, 0.05]);
push(PARENT_CANNON, 'Flag_Cloth', 'Cube', [-7.15, 2.5, -1.8], [0.85, 0.45, 0.03]);

// 天空：远山与云团
push(PARENT_SKY, 'Mountain_1', 'Sphere', [-13, 0.8, -16], [7, 2.5, 3]);
push(PARENT_SKY, 'Mountain_2', 'Sphere', [0, 1.2, -19], [9, 3.2, 3]);
push(PARENT_SKY, 'Mountain_3', 'Sphere', [13, 0.9, -15], [7, 2.6, 3]);
[[6, 12, -8], [-4, 13, -10], [10, 11, -12]]
  .forEach((p, i) => push(PARENT_SKY, 'Cloud_Puff_' + (i + 1), 'Sphere', p, [1.4, 0.7, 1]));

// 每个动作是独立工具：primitive / empty 由不同工具承担，source 已由服务端注入。
function createStep(o) {
  const args = { name: o.name, parent: o.parent };
  if (o.position) args.position = o.position;
  if (o.rotation) args.rotation = o.rotation;
  if (o.scale) args.scale = o.scale;
  if (o.primitive_type) {
    args.primitive_type = o.primitive_type;
    return { name: 'hierarchy.create_primitive', arguments: args };
  }
  return { name: 'hierarchy.create_empty', arguments: args };
}

const CS_CODE = `
var dir = "Assets/CannonBirdDemo/Materials/";
var matNames = new string[] { "Detail_Grass", "Detail_Stone", "Detail_Wood", "Detail_Accent", "Detail_Eye", "Detail_Dark", "Detail_Sand" };
var matHex = new string[] { "#4C8B3B", "#8A8D92", "#8A5A35", "#D9603B", "#F7F5EE", "#20242B", "#C8A66B" };
var mats = new UnityEngine.Material[matNames.Length];
for (int i = 0; i < matNames.Length; i++) {
    string mp = dir + matNames[i] + ".mat";
    mats[i] = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(mp);
    if (mats[i] == null) {
        mats[i] = new UnityEngine.Material(UnityEngine.Shader.Find("Standard"));
        UnityEngine.Color cc;
        UnityEngine.ColorUtility.TryParseHtmlString(matHex[i], out cc);
        mats[i].color = cc;
        UnityEditor.AssetDatabase.CreateAsset(mats[i], mp);
    }
}
var metal = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(dir + "Metal.mat");
var cloud = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(dir + "Cloud.mat");
var details = UnityEngine.GameObject.Find("CannonScene/Details");
int painted = 0;
if (details != null) {
    foreach (var r in details.GetComponentsInChildren<UnityEngine.Renderer>(true)) {
        string n = r.gameObject.name;
        UnityEngine.Material m = null;
        if (n.StartsWith("Rock_")) m = mats[1];
        else if (n.StartsWith("Grass_")) m = mats[0];
        else if (n.StartsWith("Path_")) m = mats[6];
        else if (n.StartsWith("Target_Post")) m = mats[2];
        else if (n.StartsWith("Target_Board")) m = mats[3];
        else if (n.StartsWith("Muzzle_Ring") || n.StartsWith("Rivet_") || n.StartsWith("Ammo_Ball_")) m = metal;
        else if (n.StartsWith("Ammo_Crate") || n.StartsWith("Flag_Pole")) m = mats[2];
        else if (n.StartsWith("Flag_Cloth")) m = mats[3];
        else if (n.StartsWith("Mountain_")) m = mats[1];
        else if (n.StartsWith("Cloud_Puff_")) m = cloud;
        if (m != null) { r.sharedMaterial = m; painted++; }
    }
}
int eyes = 0;
for (int i = 1; i <= 3; i++) {
    var head = UnityEngine.GameObject.Find("CannonScene/Birds/Bird_" + i + "/Head");
    if (head == null) continue;
    for (int s = -1; s <= 1; s += 2) {
        string en = s < 0 ? "Eye_L" : "Eye_R";
        if (head.transform.Find(en) != null) continue;
        var e = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Sphere);
        e.name = en;
        e.transform.SetParent(head.transform, false);
        e.transform.localPosition = new UnityEngine.Vector3(0.16f * s, 0.10f, 0.24f);
        e.transform.localScale = new UnityEngine.Vector3(0.14f, 0.14f, 0.14f);
        var ec = e.GetComponent<UnityEngine.Collider>();
        if (ec != null) UnityEngine.Object.DestroyImmediate(ec);
        e.GetComponent<UnityEngine.Renderer>().sharedMaterial = mats[4];
        var pupil = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Sphere);
        pupil.name = "Pupil";
        pupil.transform.SetParent(e.transform, false);
        pupil.transform.localPosition = new UnityEngine.Vector3(0f, 0f, 0.42f);
        pupil.transform.localScale = new UnityEngine.Vector3(0.45f, 0.45f, 0.45f);
        var pc = pupil.GetComponent<UnityEngine.Collider>();
        if (pc != null) UnityEngine.Object.DestroyImmediate(pc);
        pupil.GetComponent<UnityEngine.Renderer>().sharedMaterial = mats[5];
        eyes++;
    }
}
int lights = 0;
var cd = UnityEngine.GameObject.Find("CannonScene/Details/CannonDetails");
if (cd != null && cd.transform.Find("Muzzle_Glow") == null) {
    var glow = new UnityEngine.GameObject("Muzzle_Glow");
    glow.transform.SetParent(cd.transform, false);
    glow.transform.localPosition = new UnityEngine.Vector3(-4.1f, 2.05f, 0f);
    var light = glow.AddComponent<UnityEngine.Light>();
    light.type = UnityEngine.LightType.Point;
    light.color = new UnityEngine.Color(1f, 0.62f, 0.28f);
    light.intensity = 3.2f;
    light.range = 7f;
    light.shadows = UnityEngine.LightShadows.Soft;
    lights = 1;
}
UnityEditor.AssetDatabase.SaveAssets();
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
return string.Format("painted={0}, eyes={1}, lights={2}", painted, eyes, lights);
`;

(async () => {
  const report = await unity.workflowContext('scene_detail', 'Enrich current scene via discovered tools', async wf => {
    const notes = [];

    const all = await unity.callSkillJson('hierarchy.find_by_name', {
      query: '*', select_many: true, include_hierarchy: false,
    });
    const paths = new Set((all.data || []).map(o => {
      const m = (o.yaml || '').match(/path: (.+)/);
      return m ? m[1].trim() : '';
    }));
    const wanted = groups.concat(props);
    const missing = wanted.filter(o => !paths.has(o.parent + '/' + o.name));
    notes.push('现有对象=' + paths.size + '，计划新增=' + missing.length + '（跳过 ' + (wanted.length - missing.length) + '）');

    // 无批量接口：逐次调用，遇错即由 workflowContext 记录并抛出。
    let created = 0;
    for (const step of missing.map(createStep)) {
      await unity.callSkillText(step.name, step.arguments);
      created++;
    }
    notes.push('逐次创建: ' + created + '/' + missing.length);

    // 视觉修正：把首轮落在相机视野外的远山、云团、靶子移入可见区；幂等。
    const tweaks = [
      { path: 'SkyDetails/Mountain_1', position: [-12, 0.2, 17], scale: [9, 2.2, 2.5] },
      { path: 'SkyDetails/Mountain_2', position: [0, 0.4, 18], scale: [11, 2.8, 2.5] },
      { path: 'SkyDetails/Mountain_3', position: [12, 0.2, 16.5], scale: [8, 2.0, 2.5] },
      { path: 'SkyDetails/Cloud_Puff_1', position: [6.3, 10.7, 9.2], scale: [0.9, 0.4, 0.7] },
      { path: 'SkyDetails/Cloud_Puff_2', position: [-5.2, 9.7, 9.2], scale: [1.0, 0.4, 0.7] },
      { path: 'SkyDetails/Cloud_Puff_3', position: [11.2, 8.8, 9.2], scale: [0.8, 0.35, 0.6] },
      { path: 'GroundDetails/Target_Post', position: [-1.5, 1, 7], scale: [0.12, 1, 0.12] },
      { path: 'GroundDetails/Target_Board', position: [-1.5, 2, 7], scale: [1.1, 0.06, 1.1], rotation: [90, 0, 0] },
    ];
    let adjusted = 0;
    for (const t of tweaks) {
      const args = { path: 'CannonScene/Details/' + t.path };
      if (t.position) args.position = t.position;
      if (t.scale) args.scale = t.scale;
      if (t.rotation) args.rotation = t.rotation;
      await unity.callSkillText('gameobject.modify', args);
      adjusted++;
    }
    notes.push('视觉修正: ' + adjusted + '/' + tweaks.length);

    const cs = await unity.callSkillJson('code.execute', {
      mode: 'runtime', code: CS_CODE, return_output: true,
    });
    if (cs && cs.success === false) throw new Error('运行时代码失败: ' + JSON.stringify(cs).slice(0, 900));
    notes.push('runtime: ' + JSON.stringify(cs).slice(0, 300));

    const save = await unity.callSkillText('scene.save', {});
    notes.push('save: ' + String(save).slice(0, 160).replace(/\s+/g, ' '));

    for (const [tool, args, label] of [
      ['scene_view.screenshot', { save_path: 'Assets/CannonBirdDemo/Preview_Detailed.png' }, 'scene_view 截图'],
      ['camera.screenshot', { name: 'Main Camera', width: 1280, height: 720, save_path: 'Assets/CannonBirdDemo/Preview_Game.png' }, 'camera 截图'],
    ]) {
      try {
        const t = await unity.callSkillText(tool, args);
        notes.push(label + ': ' + String(t).slice(0, 160).replace(/\s+/g, ' '));
      } catch (e) {
        notes.push(label + ' 失败(不致命): ' + e.message.slice(0, 180));
      }
    }

    return { notes, summary: wf.summary() };
  });

  const s = report.summary;
  console.log(JSON.stringify({
    notes: report.notes,
    summary: {
      calls: s.calls, succeeded: s.succeeded, failed: s.failed, duration_ms: s.duration_ms,
      failed_steps: s.steps.filter(x => !x.ok).map(x => ({ name: x.name, error: x.error })),
    },
  }, null, 2));
})().catch(e => {
  console.error('FAILED', e.code || '', e.message);
  process.exit(1);
});

// FishMug — 程序化旋转体 + 把手 + 材质 + 灯光 + 相机（code.execute / runtime 语句片段）
// 约定：u = theta/2pi，theta=PI 处位于 -Z，正对相机；v = y / 1.15，v=1 为杯口。

// ---------- 0. 清场 ----------
string[] oldObjs = { "MugBodyMesh", "MugHandleMesh", "MugGround", "MugKeyLight", "MugFillLight", "MugRimLight", "MugInnerLight" };
for (int i = 0; i < oldObjs.Length; i++) { var g = GameObject.Find(oldObjs[i]); if (g != null) Object.DestroyImmediate(g); }
// demo 场景自带的石砖墙 / 地面 / 灯 / 标定块会挡住画面，只禁用不删除
string[] hideNames = { "StoneWall", "SW_Ground", "SW_KeyLight", "SW_FillLight", "CALIB_A", "Crate" };
var allGo = Object.FindObjectsOfType<GameObject>();
for (int i = 0; i < allGo.Length; i++) {
    for (int k = 0; k < hideNames.Length; k++) {
        if (allGo[i].name == hideNames[k]) { allGo[i].SetActive(false); break; }
    }
}

string dir = "Assets/_FishMug";
if (!UnityEditor.AssetDatabase.IsValidFolder(dir)) UnityEditor.AssetDatabase.CreateFolder("Assets", "_FishMug");
UnityEditor.AssetDatabase.Refresh();
string[] oldAssets = { "M_FishOuter.mat", "M_Inner.mat", "M_Rim.mat", "M_Handle.mat", "M_Table.mat", "MugBody.asset", "MugHandle.asset" };
for (int i = 0; i < oldAssets.Length; i++) UnityEditor.AssetDatabase.DeleteAsset(dir + "/" + oldAssets[i]);

// ---------- 1. 贴图导入 ----------
string pBase = dir + "/mug_basecolor.png";
string pMG = dir + "/mug_metallicgloss.png";
UnityEditor.AssetDatabase.ImportAsset(pBase, UnityEditor.ImportAssetOptions.ForceUpdate);
UnityEditor.AssetDatabase.ImportAsset(pMG, UnityEditor.ImportAssetOptions.ForceUpdate);
var tiB = (UnityEditor.TextureImporter)UnityEditor.AssetImporter.GetAtPath(pBase);
tiB.textureType = UnityEditor.TextureImporterType.Default;
tiB.sRGBTexture = true;
tiB.wrapMode = TextureWrapMode.Repeat;
tiB.maxTextureSize = 4096;
tiB.textureCompression = UnityEditor.TextureImporterCompression.Uncompressed;
tiB.mipmapEnabled = true;
tiB.SaveAndReimport();
var tiM = (UnityEditor.TextureImporter)UnityEditor.AssetImporter.GetAtPath(pMG);
tiM.textureType = UnityEditor.TextureImporterType.Default;
tiM.sRGBTexture = false;
tiM.wrapMode = TextureWrapMode.Repeat;
tiM.maxTextureSize = 4096;
tiM.textureCompression = UnityEditor.TextureImporterCompression.Uncompressed;
tiM.SaveAndReimport();
Texture2D texBase = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(pBase);
Texture2D texMG = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(pMG);
Debug.Log("TEX base=" + (texBase != null) + " metalgloss=" + (texMG != null));

// ---------- 2. 材质 ----------
Shader sh = Shader.Find("Standard");
Material matOuter = new Material(sh);
matOuter.name = "M_FishOuter";
matOuter.SetColor("_Color", new Color(1f, 1f, 1f, 1f));
matOuter.SetTexture("_MainTex", texBase);
matOuter.SetTexture("_MetallicGlossMap", texMG);
matOuter.SetFloat("_Metallic", 1f);
matOuter.SetFloat("_Glossiness", 1f);
matOuter.SetFloat("_GlossMapScale", 1f);
matOuter.EnableKeyword("_METALLICGLOSSMAP");
UnityEditor.AssetDatabase.CreateAsset(matOuter, dir + "/M_FishOuter.mat");

Material matInner = new Material(sh);
matInner.name = "M_Inner";
matInner.SetColor("_Color", new Color(0.247f, 0.471f, 0.745f, 1f));
matInner.SetFloat("_Metallic", 0.02f);
matInner.SetFloat("_Glossiness", 0.90f);
// 内壁背光，给一点自发光才显出亮钴蓝釉色
matInner.SetColor("_EmissionColor", new Color(0.05f, 0.13f, 0.28f, 1f));
matInner.EnableKeyword("_EMISSION");
UnityEditor.AssetDatabase.CreateAsset(matInner, dir + "/M_Inner.mat");

Material matRim = new Material(sh);
matRim.name = "M_Rim";
matRim.SetColor("_Color", new Color(0.357f, 0.561f, 0.816f, 1f));
matRim.SetFloat("_Metallic", 0.05f);
matRim.SetFloat("_Glossiness", 0.94f);
UnityEditor.AssetDatabase.CreateAsset(matRim, dir + "/M_Rim.mat");

Material matHandle = new Material(sh);
matHandle.name = "M_Handle";
matHandle.SetColor("_Color", new Color(0.184f, 0.353f, 0.659f, 1f));
matHandle.SetFloat("_Metallic", 0.03f);
matHandle.SetFloat("_Glossiness", 0.82f);
UnityEditor.AssetDatabase.CreateAsset(matHandle, dir + "/M_Handle.mat");

Material matTable = new Material(sh);
matTable.name = "M_Table";
matTable.SetColor("_Color", new Color(0.49f, 0.30f, 0.21f, 1f));
matTable.SetFloat("_Metallic", 0f);
matTable.SetFloat("_Glossiness", 0.38f);
UnityEditor.AssetDatabase.CreateAsset(matTable, dir + "/M_Table.mat");

// ---------- 3. 杯身旋转体 ----------
int N = 96;
float HH = 1.15f;
// Vector2.x = 半径, .y = 高度
Vector2[] pOuter = {
    new Vector2(0.00f, 0.005f), new Vector2(0.68f, 0.005f), new Vector2(0.735f, 0.020f),
    new Vector2(0.758f, 0.060f), new Vector2(0.774f, 0.160f), new Vector2(0.794f, 0.400f),
    new Vector2(0.814f, 0.700f), new Vector2(0.834f, 0.950f), new Vector2(0.845f, 1.060f),
    new Vector2(0.850f, 1.100f)
};
// 收窄口沿：壁厚约 0.036（原 0.064 会让亮蓝环过粗）
Vector2[] pRim = {
    new Vector2(0.850f, 1.100f), new Vector2(0.853f, 1.110f), new Vector2(0.849f, 1.124f),
    new Vector2(0.838f, 1.134f), new Vector2(0.826f, 1.134f), new Vector2(0.816f, 1.124f),
    new Vector2(0.814f, 1.110f)
};
Vector2[] pInner = {
    new Vector2(0.814f, 1.110f), new Vector2(0.801f, 1.000f), new Vector2(0.779f, 0.720f),
    new Vector2(0.753f, 0.440f), new Vector2(0.727f, 0.220f), new Vector2(0.697f, 0.140f),
    new Vector2(0.647f, 0.098f), new Vector2(0.505f, 0.076f), new Vector2(0.285f, 0.068f),
    new Vector2(0.000f, 0.066f)
};
var verts = new System.Collections.Generic.List<Vector3>();
var uvs = new System.Collections.Generic.List<Vector2>();
var t0 = new System.Collections.Generic.List<int>();   // 外壁 + 外底
var t1 = new System.Collections.Generic.List<int>();   // 内壁 + 内底
var t2 = new System.Collections.Generic.List<int>();   // 口沿
for (int g = 0; g < 3; g++) {
    Vector2[] pr = g == 0 ? pOuter : (g == 1 ? pRim : pInner);
    int st = verts.Count;
    for (int i = 0; i < pr.Length; i++) {
        for (int j = 0; j <= N; j++) {
            float th = 6.2831853f * j / N;
            float rr = pr[i].x; float yy = pr[i].y;
            verts.Add(new Vector3(rr * Mathf.Sin(th), yy, rr * Mathf.Cos(th)));
            // 相机在 -Z 看向 +Z 时屏幕右 = +X，而 x=r*sin(theta) 使 theta 增大对应 -X，
            // 因此贴图会在屏幕上左右镜像；u 取反抵消，保证鱼头朝左、文字不反。
            uvs.Add(new Vector2(1f - (float)j / N, yy / HH));
        }
    }
    var tl = g == 0 ? t0 : (g == 1 ? t2 : t1);
    for (int i = 0; i < pr.Length - 1; i++) {
        for (int j = 0; j < N; j++) {
            int a = st + i * (N + 1) + j;
            int b = a + 1; int c = a + N + 1; int d = c + 1;
            tl.Add(a); tl.Add(b); tl.Add(c);
            tl.Add(b); tl.Add(d); tl.Add(c);
        }
    }
}
Mesh bm = new Mesh();
bm.name = "MugBody";
bm.SetVertices(verts);
bm.SetUVs(0, uvs);
bm.subMeshCount = 3;
bm.SetTriangles(t0, 0);
bm.SetTriangles(t1, 1);
bm.SetTriangles(t2, 2);
bm.RecalculateNormals();
bm.RecalculateBounds();
UnityEditor.AssetDatabase.CreateAsset(bm, dir + "/MugBody.asset");
GameObject body = new GameObject("MugBodyMesh");
var mfB = body.AddComponent<MeshFilter>(); mfB.sharedMesh = bm;
var mrB = body.AddComponent<MeshRenderer>(); mrB.sharedMaterials = new Material[] { matOuter, matInner, matRim };

// ---------- 4. 把手（椭圆弧扫掠扁椭圆截面） ----------
int M = 48, K = 16;
var hv = new System.Collections.Generic.List<Vector3>();
var hu = new System.Collections.Generic.List<Vector2>();
var ht = new System.Collections.Generic.List<int>();
for (int i = 0; i <= M; i++) {
    float t = (float)i / M;
    float ang = -1.5707963f + 3.1415926f * t;
    float cx = -0.80f - 0.62f * Mathf.Cos(ang);
    float cy = 0.55f + 0.34f * Mathf.Sin(ang);
    float dx = 0.62f * Mathf.Sin(ang) * 3.1415926f;
    float dy = 0.34f * Mathf.Cos(ang) * 3.1415926f;
    Vector3 T = new Vector3(dx, dy, 0f).normalized;
    Vector3 Bv = new Vector3(0f, 0f, 1f);
    Vector3 Nr = Vector3.Cross(T, Bv).normalized;
    for (int k = 0; k <= K; k++) {
        float ph = 6.2831853f * k / K;
        Vector3 p = new Vector3(cx, cy, 0f) + Nr * (0.072f * Mathf.Cos(ph)) + Bv * (0.105f * Mathf.Sin(ph));
        hv.Add(p);
        hu.Add(new Vector2(t, (float)k / K));
    }
}
for (int i = 0; i < M; i++) {
    for (int k = 0; k < K; k++) {
        int a = i * (K + 1) + k; int b = a + 1; int c = a + K + 1; int d = c + 1;
        // 反向绕序：扫掠管的法线必须朝外，否则被背面剔除
        ht.Add(a); ht.Add(c); ht.Add(b);
        ht.Add(b); ht.Add(c); ht.Add(d);
    }
}
Mesh hm = new Mesh();
hm.name = "MugHandle";
hm.SetVertices(hv);
hm.SetUVs(0, hu);
hm.SetTriangles(ht, 0);
hm.RecalculateNormals();
hm.RecalculateBounds();
UnityEditor.AssetDatabase.CreateAsset(hm, dir + "/MugHandle.asset");
GameObject handle = new GameObject("MugHandleMesh");
var mfH = handle.AddComponent<MeshFilter>(); mfH.sharedMesh = hm;
var mrH = handle.AddComponent<MeshRenderer>(); mrH.sharedMaterial = matHandle;

// ---------- 5. 桌面 ----------
GameObject table = GameObject.CreatePrimitive(PrimitiveType.Plane);
table.name = "MugGround";
table.transform.position = new Vector3(0f, -0.012f, 0f);
table.transform.localScale = new Vector3(3f, 1f, 3f);
table.GetComponent<MeshRenderer>().sharedMaterial = matTable;

// ---------- 6. 灯光 ----------
RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
RenderSettings.ambientLight = new Color(0.34f, 0.37f, 0.44f, 1f);
GameObject kl = new GameObject("MugKeyLight");
Light l1 = kl.AddComponent<Light>();
l1.type = LightType.Directional; l1.color = new Color(1f, 0.96f, 0.90f); l1.intensity = 1.25f;
l1.shadows = LightShadows.Soft;
kl.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
GameObject fl = new GameObject("MugFillLight");
Light l2 = fl.AddComponent<Light>();
l2.type = LightType.Directional; l2.color = new Color(0.62f, 0.76f, 1f); l2.intensity = 0.55f;
l2.shadows = LightShadows.None;
fl.transform.rotation = Quaternion.Euler(25f, 140f, 0f);
GameObject pl = new GameObject("MugRimLight");
Light l3 = pl.AddComponent<Light>();
l3.type = LightType.Point; l3.range = 7f; l3.intensity = 2.0f;
l3.color = new Color(0.85f, 0.92f, 1f);
pl.transform.position = new Vector3(-1.4f, 2.4f, -1.2f);
GameObject il = new GameObject("MugInnerLight");
Light l4 = il.AddComponent<Light>();
l4.type = LightType.Point; l4.range = 4.5f; l4.intensity = 1.5f;
l4.color = new Color(0.75f, 0.86f, 1f);
il.transform.position = new Vector3(0f, 1.80f, -0.72f);

// ---------- 7. 相机 ----------
Camera cam = Camera.main;
if (cam == null) { GameObject cg = new GameObject("Main Camera"); cg.tag = "MainCamera"; cam = cg.AddComponent<Camera>(); }
cam.transform.position = new Vector3(0f, 2.35f, -3.05f);
cam.transform.LookAt(new Vector3(0f, 0.55f, 0f));
cam.fieldOfView = 36f;
cam.nearClipPlane = 0.05f; cam.farClipPlane = 100f;
cam.clearFlags = CameraClearFlags.SolidColor;
cam.backgroundColor = new Color(0.35f, 0.22f, 0.16f, 1f);

UnityEditor.AssetDatabase.SaveAssets();
UnityEditor.SceneManagement.EditorSceneManager.SaveScene(
    UnityEngine.SceneManagement.SceneManager.GetActiveScene(), dir + "/FishMug.unity");
Debug.Log("MUG OK bodyV=" + bm.vertexCount + " bodyT=" + ((t0.Count + t1.Count + t2.Count) / 3)
          + " handleV=" + hm.vertexCount + " subs=" + bm.subMeshCount + " cam=" + cam.name);

# 示例：锦鲤描金马克杯（曲面贴图 + 旋转体建模）

`fish_mug.js` 是一条端到端样板，覆盖「护栏 → 贴图 → 网格 → 材质 → 灯光 → 校验 → 截图」全链路，
且脚本内**没有任何绝对路径**：Unity 工程由 `scripts/mcp.js` 的 `resolveProject()` 运行时解析。

提交 `code.execute` 之前会先跑 `scripts/lathe.js` 的 `guard()`：从实际 PNG 读尺寸、
静态比对 C# 里的 UV 公式 / 扫掠绕序 / 口沿半径，参数或代码被改坏会**直接抛错**，
不浪费 Unity 往返。改了 `fish_mug.cs` 的几何或 `fish_mug_texture.py` 的贴图尺寸，
记得同步 `fish_mug.js` 里的 `SPEC`。

## 跑法

```bash
node examples/fish_mug.js
node examples/fish_mug.js --project <Unity 工程根> --instance <ID>
node examples/fish_mug.js --verify      # 追加在线校验（submesh / 材质 / 宽高比 / 法线朝外）
python examples/fish_mug_texture.py     # 可选：重绘贴图（需 Pillow）
node examples/fish_mug.js --regen-texture --no-shot
```

产物落在工程的 `Assets/_FishMug/`：`MugBody.asset`、`MugHandle.asset`、4 张材质、
`FishMug.unity`、`shot_fish_mug.png`。

## 目录

| 文件 | 作用 |
|---|---|
| `fish_mug.js` | 主流程：`lathe.guard` → 同步贴图 → `code.execute` → `verify`（可选）→ `camera.screenshot` |
| `fish_mug.cs` | C# 语句片段：旋转体、把手、材质、灯光、相机 |
| `fish_mug_texture.py` | 重绘柱面展开贴图（BaseColor + MetallicGloss） |
| `assets/*.png` | 预烘焙贴图，开箱即用 |

## 五条实测铁律（改这个例子前先看）

以下五条**不只是文档**：`scripts/lathe.js` 把它们做成硬断言，`check.js` 第 12 节有 26 项回归测试
（含「故意把 UV 公式 / 绕序改坏，必须能被抓到」）。完整推导见 `references/semantics.md` 第六节。

1. **贴图的宽高比必须匹配柱面周长:杯高**。本例 `C=2π·0.8≈5.03`、`H=1.15`，
   比值约 `4.37`，所以贴图取 `4096×1024`。用 `2048×1024` 会把鱼横向拉伸约 2 倍。
2. **UV 的 u 要取反**。网格用 `x = r·sinθ, z = r·cosθ`，相机在 `-Z` 看向 `+Z` 时
   屏幕右是 `+X`，而 θ 增大会跑到 `-X`，贴图会左右镜像（文字反、鱼头朝右）。
   故 `u = 1 - j/N`。
3. **扫掠管的三角形绕序必须反转**。按 `(a,b,c)` 生成时法线朝管内，
   把手会被背面剔除完全看不见；改成 `(a,c,b)` 才朝外。
4. **金属度不能给满**。场景没有反射探针时，`_MetallicGlossMap` 里金色若给到 0.86
   会发黑；控制在 ~0.4，保留亮金漫反射，内壁另给一点 `_EMISSION` 才有亮钴蓝。
5. **材质要按 submesh 顺序挂**：`sharedMaterials = [外壁贴图, 内壁, 口沿]`，
   对应 `subMeshCount=3`；`uv_atlas` 之类操作会重置渲染器，务必在其后再挂材质。

另外：口沿壁厚约 0.036（相对半径 0.85）才不会变成一圈粗蓝环，由 `assertRimThickness()` 锁在
`[0.015, 0.075]`；`camera.screenshot` 的相机名因工程而异，示例从建模日志的 `cam=...` 解析，不写死。

## 校验与场景隔离

- **曲面别用默认校验档**：`verify.run({ profile:'lathe', object, texture, radius, height,
  expectSubMeshes:3 })`。默认档假设单 submesh + ProBuilder 图集，用在曲面上会误报。
- **场景隔离是权宜之计**：示例隐藏所有非 `Mug*` 且带 MeshRenderer 的根对象。
  曾只靠硬编码名单，后来场景里多出的 `Monkey` 没被隐藏、俯视时坐在杯子里。
  根治方案是新建独立 Scene，不要长期依赖 `SetActive(false)` 名单。

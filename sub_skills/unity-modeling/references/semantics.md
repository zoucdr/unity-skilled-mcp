# 实测铁律

本文件只写**跑出来的事实**，不写"应该是"。

每条都标注了证据形态：`[实测]` 是脚本跑出来的数据，`[源码]` 是读实现确认的，`[反例]` 是曾经因此出错的记录。

---

## 一、坐标系

### 1. 画布 y 向上，`y = v * H`，不要翻转 `[实测]`

`texgen` 的 layer 坐标（`center` / `size` / `from` / `to`）原点在画布**左下**，y 轴向上，与 UV 的 `v` 同向。

标定方法：画两个已知位置的方块，解码 PNG 看质心。

| layer 输入 | 导出 PNG 中的质心 | 位置 |
|---|---|---|
| 红块 `center:[128,128]`（512 画布） | `(127.5, **383.5**)` | 文件下半部 |
| 蓝块 `center:[384,384]` | `(383.5, **127.5**)` | 文件上半部 |

`center.y = 128` 落在文件第 383 行 = `512 - 128 - 1`，说明 **canvas y 与 image row 互为镜像**，但 canvas 本身与 `v` 同向。

> **`[反例]`** `TexGenModel.cs` 第 103 行注释写着 `Y always grows downward`。
> **这句注释是错的。** 照它写代码会多翻一次，导致贴图上下颠倒。
> 结论：坐标类语义只能标定，不能读注释。

### 2. 只需要两个空间 `[实测]`

```
CANVAS   texgen layer 坐标     y 向上    y = v * H
IMAGE    解码后的 PNG 行号      y 向下    row = (1 - v) * H
```

唯一的真值是 UV：`u` 向右、`v` 向上、`v = 0` 是贴图**底边**。

「看起来反了」几乎总是因为把 IMAGE 行号当成了 CANVAS 坐标。

`scripts/uvspace.js` 是唯一出口，不要手写 `1 - v`。

### 3. `origin` 只平移原点，不翻转方向 `[源码]`

- `topLeft` → 原点在 (0,0)，y 仍向上
- `center` → 原点在 (W/2, H/2)

名字叫 `topLeft` 有误导性：它指的是"原点位于画布左上角那个位置"，不是"y 轴向下"。

---

## 二、UV 图集

### 4. `uv_atlas` 的 `rect` 是 `[x0, y0, x1, y1]`，不是 `[x, y, w, h]` `[实测]`

512 画布、`columns=2, rows=2`、3 个连通部件：

| cell | col | row | rect | rect_px |
|---|---|---|---|---|
| 0 | 0 | 0 | `[0.04, 0.04, 0.46, 0.46]` | `[20, 20, 215, 215]` |
| 1 | 1 | 0 | `[0.54, 0.04, 0.96, 0.46]` | `[276, 20, 215, 215]` |
| 2 | 0 | 1 | `[0.04, 0.54, 0.46, 0.96]` | `[20, 276, 215, 215]` |

`rect_px` 的第三、四位是 `x1, y1`（20+215=235 不是 215），证实是两组对角坐标。

### 5. `rect` 的 y 就是 `v`，不做 `1 - v` `[实测]`

`row 0` 的 y 是 `0.04..0.46`（低），`row 1` 是 `0.54..0.96`（高）。行号从下往上数，与 UV 一致。

`tex.atlasRectToLayer(cell, W, H)` 已封装，直接给出 `{ center:[cx,cy], size:[w,h] }`。

### 6. 端到端对齐已验证 `[实测]`

按 `atlasRectToLayer` 画三格纯色，解码 PNG 取样：

| cell | 期望 | 实际像素 |
|---|---|---|
| 0 红 `#e74c3c` | 231,76,60 | **231,76,60** |
| 1 蓝 `#3498db` | 52,152,219 | **52,152,219** |
| 2 绿 `#2ecc71` | 46,204,113 | **46,204,113** |

见 `samples/atlas_alignment_debug.png`。

---

## 三、贴图工具

### 7. `for_mesh` 的 `regions` 是 `[{ id, layers }]` `[源码]`

- 传**全局 `layers`**：所有分区共用同一张画布，只渲染一次（`rendered_canvases: 1`），各分区按自己的 UV 遮罩取样。
- 传 `regions` 覆盖：每个分区单独渲染一张**整幅尺寸**的画布，再取该分区的部分。

**关键**：无论哪种，layer 坐标都是**整图坐标**，不是"相对分区"的坐标。这点极易误解。

### 8. 渐变写纯颜色数组会静默失效 `[实测]`

```jsonc
// 有效，三种等价写法
"gradient": [{ "offset": 0, "color": "#000" }, { "offset": 1, "color": "#fff" }]
"gradient": [[0, "#000"], [1, "#fff"]]
"gradient": { "0": "#000", "1": "#fff" }

// 无效：不报错，整层变纯白
"gradient": ["#000", "#fff"]
```

实测四种写法各渲一张，前三种正常渐变，第四种全白。

### 9. `linear` 渐变的投影长度永远取 `size.x` `[源码]`

方向由 `rotation` 决定（`0` = 从左到右），但**长度**取 `size.x`。

所以 2048×1024 的画布上写 `size:[2048,1024]` 画竖直渐变，只会用到中间一段。
**想要竖直渐变：`rotation: 90` 且 `size` 接近正方形。**

> **`[反例]`** 彩虹球那次粉色区域消失，就是这条。当时把画布拆成两块并排才解决。

### 10. 接缝数值会误报，以 2×2 拼贴为准 `[实测]`

砖墙贴图的"边缘差异 / 内部相邻差异"比值是 **5.69**，看起来有大缝。
生成 2×2 拼贴一看：**完全无缝**。

试了 `variation` 0/0.25 × `stagger` 0/0.5 四组，比值全在 5.57~5.77 —— 与接缝无关，是错缝砖墙首末行内容天然不同。

**结论**：结构化图案必须看图；连续纹理（cobble、噪声）用比值才有效。
`inspect.js --tile 2` 生成拼贴图。

### 11. TexGen 原生有 PBR，不要自己求梯度 `[实测]`

`texgen.pbr` 一次派生 BaseColor / Height / Normal / Roughness / AO / ORM。

产出的法线实测：平均长度 `1.000`、范围 `[0.993, 1.005]`、蓝通道无像素低于 100 —— 合格。

内部规则：`height = clamp01(lum × alpha)`、`roughness = clamp01(bias + (0.5-height) × contrast)`。
即**透明 = 凹陷、越暗越粗糙**。

---

## 四、建模工具

### 12. `createShape` 的 `position` 是精确的 `[实测]`

传 `[1, 0.25, 0]` 得到 `(1.00, 0.25, 0.00)`，Cube/Cylinder/Sphere、各种 size 都一致。

> **`[反例]`** 曾经以为有 0.05 的系统偏移。真相是 `hierarchy.find_by_query` 返回的 **yaml 里位置只保留 1 位小数**：`0.25` 显示成 `0.3`、`1.05` 显示成 `1.1`。
> **不要用 `find_by_query` 的 yaml 做精确位置校验**，读 Transform 或 `probuilder.getMeshInfo`。

### 13. `mergeObjects` 返回 `targetObjectName` `[实测]`

```json
{ "mergedMeshCount": 3, "resultMeshCount": 1, "targetObjectName": "PBM_TestA",
  "objectsDeleted": 2, "totalFacesBefore": 18, "totalFacesAfter": 18 }
```

阈值：`totalFacesAfter` 应等于 `totalFacesBefore`（合并不增删面）。

**改名走 `gameobject.modify({ path, name })`**，没有 `gameobject.set_property`。

### 14. 合并后顶点是**局部坐标** `[实测]`

合并对象的 transform 落在第一个部件的原位，顶点相对它。

三个部件在世界 y 0~1.1，合并后 Mesh 局部 y 范围是 `-0.5 ~ 0.6`。

所以按世界范围分组时必须 `transform.TransformPoint(centroid)`。`splitSubmeshes` 已内置。

### 15. `splitSubmeshes` 之后 ProBuilderMesh 消失 `[实测]`

直接替换 `MeshFilter.sharedMesh` 会让对象脱离 ProBuilder，`probuilder.getMeshInfo` 报
`没有 ProBuilderMesh 组件`。

`model.info()` 会自动降级到纯 Mesh 读取，返回结构里 `source: 'mesh-fallback'` 和 `trianglesPerSubMesh`。

**这意味着分材质之后再想挤出/倒角就不行了** —— 需要几何编辑要在 split 之前做完。

### 16. 工程里没有 `SimpleJson` `[实测]`

`code.execute` 里写 `SimpleJson.DeserializeObject(...)` 直接 `CS0103`，并且会级联出十几个假错误。

**正确做法**：把数据编成 C# 字面量（`new float[] { 1f, 2f }`），不要运行时解析 JSON。
`splitSubmeshes` 就是这么做的。

### 17. `code.execute` 的 `mode` 与 `return` `[实测]`

- `mode: 'runtime'`（默认有意义）→ 接受 C# **语句片段**
- 完整类/命名空间要用 `mode: 'dll'`
- 返回类型是 `object`，写 `return;` 直接 `CS0126`
- 输出用 `Debug.Log`，结果在 `logs` 字段里

---

## 五、材质与对象

### 18. `uv_atlas` 会把 renderer 重置成 `ProBuilderDefault` `[实测]`

所以**挂材质必须在 atlas 之后**。

另外 `sharedMaterial = mat` 只改内存，要 `EditorUtility.SetDirty(obj)` + 标记场景 dirty，否则重载场景就丢。

`tex.assignMaterial()` 已含这两步，并且失败时把 Unity 的日志原样抛出（不静默）。

### 19. 脚本必须幂等，否则堆出同名对象 `[实测]`

连跑 3 次示例脚本，场景里出现 **6 个都叫 `Crate` 的对象**：只有 1 个挂了材质，另外 5 个是 `ProBuilderDefault`（白色），渲染时白色盖住木色 —— 看起来像"材质完全没生效"。

排查花了很久才发现是重复对象，不是材质问题。

**规则**：脚本开头 `await model.purge(['Crate', model.PART_PREFIX + '*'])`。

其他幂等要点：
- `material.create` 对已存在路径**报错**，要 try/catch 后复用
- `purge` 只处理场景**根对象**，嵌套的要传完整路径或用递归版本

---

## 附：工具名易错清单

| 想当然的名字 | 实际名字 |
|---|---|
| `hierarchy.list` | `hierarchy.find_by_query`（返回 `[{yaml:"..."}]`，不是 JSON 对象） |
| `gameobject.set_property` | `gameobject.modify` |
| `gameobject.create` | `hierarchy.create_empty` |
| `texture.*` 做贴图 | 贴图在 `texgen.*` |
| `Probuilder.*` | 命名空间小写 `probuilder.*` |

**教训**：工具名必须从 `groups` / `schema` 现查，不能凭命名习惯猜。猜错会浪费一整轮往返。

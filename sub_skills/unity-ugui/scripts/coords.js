/**
 * 坐标唯一收口 —— SPEC.rect [left, top, width, height]（效果图左上原点、y 向下、像素）
 * 是全链路唯一坐标表示。所有其它坐标系只能由本文件换算，禁止在别处手写 y 翻转或锚点计算。
 *
 * 三套目标语义（均已核对源码 / 实测 schema）：
 *
 *  A. 效果图裁切 / Sprite     —— 左上原点 y 向下，直接用 rect（clamp 到图内）。
 *  B. UISourceGen screen      —— 左上原点 y 向下；anchor=center 时 rect 的 x,y 是中心点
 *                                 （UiGenModel.cs:316-347 的 Left()/Top() 实测）。
 *                                 统一输出 anchor="center" + rect=[cx, cy, w, h]，
 *                                 这样 Left()=cx-w/2=left、Top()=cy-h/2=top，零歧义。
 *  C. Unity UGUI RectTransform —— 左下原点 y 向上；anchorMin/Max 是 0..1 归一化，
 *                                 anchoredPosition 是 pivot 相对锚点参考点的像素偏移。
 *                                 固定锚点用 anchoredPos+sizeDelta；stretch 用 offsetMin/offsetMax。
 *
 * 实测坑（references/semantics.md 有证据）：
 *  - ugui.do_layout 的 anchored_pos/size_delta/anchor_min/anchor_max/pivot 在 JSON Schema 里
 *    是 minItems=3 & maxItems=3 的 vector3，必须传 3 个数，传 [x,y] 会被拒。
 *  - 效果图分辨率与 CanvasScaler.referenceResolution 不同时，必须按比例缩放，不能只改一边。
 */
'use strict';

/** 12 预设 → (ax, ay) / 拉伸区间。 */
const PRESETS = {
  top_left: { min: [0, 1], max: [0, 1] },
  top_center: { min: [0.5, 1], max: [0.5, 1] },
  top_right: { min: [1, 1], max: [1, 1] },
  middle_left: { min: [0, 0.5], max: [0, 0.5] },
  middle_center: { min: [0.5, 0.5], max: [0.5, 0.5] },
  middle_right: { min: [1, 0.5], max: [1, 0.5] },
  bottom_left: { min: [0, 0], max: [0, 0] },
  bottom_center: { min: [0.5, 0], max: [0.5, 0] },
  bottom_right: { min: [1, 0], max: [1, 0] },
  stretch_horizontal: { min: [0, 0.5], max: [1, 0.5], stretch: true },
  stretch_vertical: { min: [0.5, 0], max: [0.5, 1], stretch: true },
  stretch: { min: [0, 0], max: [1, 1], stretch: true },
};

function scaleOf(spec) {
  const sw = spec.source.width, sh = spec.source.height;
  const rw = spec.canvas.reference_resolution[0], rh = spec.canvas.reference_resolution[1];
  return { sx: rw / sw, sy: rh / sh, rw, rh, sw, sh };
}

/**
 * 节点所在的参考矩形（画布像素、左上原点 y 向下）。
 *
 * SPEC.rect 永远是「整张效果图的绝对坐标」，但 Unity 里 anchoredPosition 是
 * 相对**直接父 RectTransform** 的。因此有 parent 时必须把绝对坐标减去父容器
 * 左上角，并把锚点参考尺寸从整屏换成父容器尺寸，否则嵌套节点会整体漂移。
 * 无 parent 时参考框就是整屏画布（历史行为，向后兼容）。
 */
function frameOf(node, spec) {
  const s = scaleOf(spec);
  const canvas = { left: 0, top: 0, w: s.rw, h: s.rh, parentId: null };
  if (!node || !node.parent) return canvas;
  const p = (spec.nodes || []).find(n => n.id === node.parent);
  if (!p || !Array.isArray(p.rect) || p.rect.length !== 4) return canvas;
  return {
    left: p.rect[0] * s.sx, top: p.rect[1] * s.sy,
    w: p.rect[2] * s.sx, h: p.rect[3] * s.sy,
    parentId: p.id,
  };
}

/** 裁切矩形：clamp 到效果图边界，返回整数像素，越界会记录在 clipped 中。 */
function toCropRect(node, spec) {
  const { sw, sh } = scaleOf(spec);
  const [l, t, w, h] = node.rect;
  const x0 = Math.max(0, Math.min(sw, Math.round(l)));
  const y0 = Math.max(0, Math.min(sh, Math.round(t)));
  const x1 = Math.max(x0, Math.min(sw, Math.round(l + w)));
  const y1 = Math.max(y0, Math.min(sh, Math.round(t + h)));
  return {
    x: x0, y: y0, w: x1 - x0, h: y1 - y0,
    clipped: x1 - x0 !== Math.round(w) || y1 - y0 !== Math.round(h),
  };
}

/** UISourceGen：统一 anchor=center，rect=[中心x, 中心y, w, h]（左上原点 y 向下）。 */
function toUISourceGen(node) {
  const [l, t, w, h] = node.rect;
  return { anchor: 'center', rect: [l + w / 2, t + h / 2, w, h] };
}

/** 反向：UISourceGen layout.json 的 center rect → SPEC.rect。 */
function fromUISourceGen(control) {
  const [cx, cy, w, h] = control.rect;
  return [cx - w / 2, cy - h / 2, w, h];
}

/**
 * UGUI：把 SPEC.rect 编译成 RectTransform 的完整布局。
 * @returns {{
 *   anchorMin:[number,number], anchorMax:[number,number], pivot:[number,number],
 *   anchoredPosition:[number,number], sizeDelta:[number,number],
 *   stretch:boolean, offsetMin:[number,number]|null, offsetMax:[number,number]|null,
 *   tattooPreset:string,
 *   doLayout:{anchored_pos:number[],size_delta:number[],anchor_min:number[],anchor_max:number[],pivot:number[]}
 * }}
 * doLayout 的向量一律给 3 个数，直接可喂 ugui.do_layout（实测 schema 要求 vector3）。
 */
function toUGUI(node, spec) {
  const s = scaleOf(spec);
  const f = frameOf(node, spec);
  const [l, t, w, h] = node.rect;
  const preset = PRESETS[node.anchor] || PRESETS.middle_center;
  const [minx, miny] = preset.min;
  const [maxx, maxy] = preset.max;

  // 效果图绝对坐标（左上原点 y 向下）→ 参考框内局部坐标（左下原点 y 向上），含缩放
  const left = l * s.sx - f.left;
  const right = (l + w) * s.sx - f.left;
  const bottom = f.h - ((t + h) * s.sy - f.top);
  const top = f.h - (t * s.sy - f.top);
  const cx = (left + right) / 2;
  const cy = (bottom + top) / 2;

  let out;
  if (preset.stretch) {
    out = {
      anchorMin: [minx, miny], anchorMax: [maxx, maxy], pivot: [0.5, 0.5],
      anchoredPosition: [(cx - (minx + maxx) / 2 * f.w), (cy - (miny + maxy) / 2 * f.h)],
      sizeDelta: [(right - left) - (maxx - minx) * f.w, (top - bottom) - (maxy - miny) * f.h],
      stretch: true,
      offsetMin: [left - minx * f.w, bottom - miny * f.h],
      offsetMax: [right - maxx * f.w, top - maxy * f.h],
    };
  } else {
    // 铁律：anchoredPosition 是 **pivot 点** 相对锚点参考点的偏移，不是矩形中心。
    // 贴边预设把 pivot 设成 (minx,miny) 角点，就必须取该角点坐标；
    // 用中心会让节点整体漂移自身宽高的一半（top_left 偏 +w/2,+h/2）。
    const pivot = [minx, miny];
    const px = left + pivot[0] * (right - left);
    const py = bottom + pivot[1] * (top - bottom);
    out = {
      anchorMin: [minx, miny], anchorMax: [maxx, maxy], pivot,
      anchoredPosition: [px - minx * f.w, py - miny * f.h],
      sizeDelta: [right - left, top - bottom],
      stretch: false, offsetMin: null, offsetMax: null,
    };
  }
  out.tattooPreset = node.anchor;
  out.doLayout = {
    anchored_pos: [out.anchoredPosition[0], out.anchoredPosition[1], 0],
    size_delta: [out.sizeDelta[0], out.sizeDelta[1], 0],
    anchor_min: [out.anchorMin[0], out.anchorMin[1], 0],
    anchor_max: [out.anchorMax[0], out.anchorMax[1], 0],
    pivot: [out.pivot[0], out.pivot[1], 0],
  };
  return out;
}

/**
 * 自检闭环：SPEC.rect → UGUI → 反算回**绝对屏幕**矩形，误差应为 0。
 * 注意它复用 toUGUI 的输出，只能证明「函数自洽」，不能证明 Unity 里位置正确；
 * 真正验收必须用 screenRectFromReadback() 走独立路径（见 verify.js）。
 */
function roundTrip(node, spec) {
  const s = scaleOf(spec);
  const f = frameOf(node, spec);
  const u = toUGUI(node, spec);
  // 注意 stretch 模式 sizeDelta 是「相对拉伸父矩形的差量」，不是绝对宽高
  if (u.stretch) return null;
  const [minx, miny] = u.anchorMin;
  const [pvx, pvy] = u.pivot;
  const px = u.anchoredPosition[0] + minx * f.w;
  const py = u.anchoredPosition[1] + miny * f.h;
  const w = u.sizeDelta[0], h = u.sizeDelta[1];
  const localLeft = px - pvx * w;
  const localBottom = py - pvy * h;
  return {
    left: (f.left + localLeft) / s.sx,
    top: (f.top + f.h - (localBottom + h)) / s.sy,
    width: w / s.sx,
    height: h / s.sy,
  };
}

/**
 * 【独立验收路径】按 Unity 语义，从 verify 回读的 RectTransform 数值反算绝对屏幕矩形。
 * 不调用 toUGUI、不依赖它的任何假设，用于打破「期望值与写入值同源」的自证循环。
 *
 * @param {number[]} v 回读数组：
 *   [anchorMin.x, anchorMin.y, anchorMax.x, anchorMax.y,
 *    anchoredPosition.x, anchoredPosition.y, sizeDelta.x, sizeDelta.y, pivot.x, pivot.y]
 * @returns {{left:number,top:number,width:number,height:number}|null} 效果图像素、左上原点
 */
function screenRectFromReadback(v, node, spec) {
  if (!Array.isArray(v) || v.length < 10) return null;
  const s = scaleOf(spec);
  const f = frameOf(node, spec);
  const [ax, ay, , , apx, apy, sw, sh, pvx, pvy] = v;
  // stretch 的 sizeDelta 是差量，不是绝对宽高，不能这样反算
  if (Math.abs(ax - v[2]) > 1e-6 || Math.abs(ay - v[3]) > 1e-6) return null;
  const px = ax * f.w + apx;
  const py = ay * f.h + apy;
  const localLeft = px - pvx * sw;
  const localBottom = py - pvy * sh;
  return {
    left: (f.left + localLeft) / s.sx,
    top: (f.top + f.h - (localBottom + sh)) / s.sy,
    width: sw / s.sx,
    height: sh / s.sy,
  };
}

/** 生成 C# 字面量（数组），供 build_ugui.cs 模板注入。 */
function csVec2(v) { return `new Vector2(${v[0].toFixed(3)}f, ${v[1].toFixed(3)}f)`; }

module.exports = {
  PRESETS, scaleOf, frameOf, toCropRect, toUISourceGen, fromUISourceGen,
  toUGUI, roundTrip, screenRectFromReadback, csVec2,
};

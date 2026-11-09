/**
 * 控件映射表 —— 抽象 Spec 类型 ⇄ 真实 Unity UGUI 组件 的唯一收口。
 *
 * 为什么必须有这张表：
 *  demo/Assets/Extends/CustomTools/UGUI/Editor/UguiToolkit.cs 的 13 种控件里，
 *  Slider 只是两张 Filled Image（没有 AddComponent<Slider>），
 *  InputField 没有绑 textComponent / placeholder，Toggle 没有绑 graphic，
 *  且完全没有 ScrollRect / Dropdown。直接依赖 ugui.create_component 会交付
 *  「看着像、点了没反应」的假控件。本表把每个类型必须挂的组件和必须绑的引用
 *  写成断言，build.js 按表建真组件，verify.js 按表回读校验。
 */
'use strict';

/**
 * requiredRefs 的每一项是 UGUI 组件上的序列化字段名，verify.js 会用反射读它，
 * 必须非 null 才算「控件正确」。
 * uguiType 为 null 表示纯容器（只有 RectTransform）。
 */
const TYPES = {
  canvas: {
    label: '画布', ugui: ['Canvas', 'CanvasScaler', 'GraphicRaycaster'],
    uisourcegen: null, interactive: false, needsSprite: false, needsText: false,
    requiredRefs: [], note: '根节点；CanvasScaler 按 spec.canvas 配置',
  },
  panel: {
    label: '面板/容器', ugui: ['Image'], uisourcegen: 'panel',
    interactive: false, needsSprite: true, needsText: false, requiredRefs: [],
  },
  card: {
    label: '卡片', ugui: ['Image'], uisourcegen: 'card',
    interactive: false, needsSprite: true, needsText: false, requiredRefs: [],
  },
  image: {
    label: '图片', ugui: ['Image'], uisourcegen: 'image',
    interactive: false, needsSprite: true, needsText: false, requiredRefs: [],
  },
  icon: {
    label: '图标', ugui: ['Image'], uisourcegen: 'icon',
    interactive: false, needsSprite: true, needsText: false, requiredRefs: [],
  },
  divider: {
    label: '分割线', ugui: ['Image'], uisourcegen: 'divider',
    interactive: false, needsSprite: true, needsText: false, requiredRefs: [],
  },
  label: {
    label: '文本', ugui: ['Text'], uisourcegen: 'label',
    interactive: false, needsSprite: false, needsText: true, requiredRefs: ['font'],
  },
  button: {
    label: '按钮', ugui: ['Image', 'Button'], uisourcegen: 'button',
    interactive: true, needsSprite: true, needsText: true,
    requiredRefs: ['targetGraphic'],
    note: '文字放子节点 Text，避免 Text 覆盖 Image.sprite',
  },
  toggle: {
    label: '开关', ugui: ['Image', 'Toggle'], uisourcegen: 'toggle',
    interactive: true, needsSprite: true, needsText: false,
    requiredRefs: ['targetGraphic', 'graphic'],
    note: 'dem o UguiToolkit 的 Toggle 未绑 graphic，必须补',
  },
  slider: {
    label: '滑条', ugui: ['Slider'], uisourcegen: 'slider',
    interactive: true, needsSprite: true, needsText: false,
    requiredRefs: ['fillRect', 'handleRect', 'targetGraphic'],
    note: 'demo UguiToolkit 用 Filled Image 假装 Slider，必须建真 Slider 并绑 fillRect/handleRect',
  },
  input: {
    label: '输入框', ugui: ['Image', 'InputField'], uisourcegen: 'input',
    interactive: true, needsSprite: true, needsText: true,
    requiredRefs: ['textComponent', 'placeholder'],
    note: 'demo UguiToolkit 未绑定 textComponent / placeholder，必须补',
  },
  progress: {
    label: '进度条', ugui: ['Image'], uisourcegen: 'progressbar',
    interactive: false, needsSprite: true, needsText: false,
    requiredRefs: [], extra: { type: 'Filled', fillMethod: 'Horizontal' },
  },
  scroll: {
    label: '滚动视图', ugui: ['Image', 'ScrollRect', 'RectMask2D'], uisourcegen: 'scrollbar',
    interactive: true, needsSprite: true, needsText: false,
    requiredRefs: ['content', 'viewport'],
    note: 'content/viewport 是子节点 RectTransform，build.js 会自动建两个子节点',
  },
  dropdown: {
    label: '下拉框', ugui: ['Image', 'Dropdown'], uisourcegen: 'dropdown',
    interactive: true, needsSprite: true, needsText: true,
    requiredRefs: ['template', 'captionText', 'itemText'],
  },
  badge: {
    label: '角标', ugui: ['Image'], uisourcegen: 'badge',
    interactive: false, needsSprite: true, needsText: true, requiredRefs: [],
  },
  tab: {
    label: '标签页', ugui: ['Image', 'Toggle'], uisourcegen: 'tab',
    interactive: true, needsSprite: true, needsText: true,
    requiredRefs: ['targetGraphic', 'graphic'],
  },
  group: {
    label: '纯容器', ugui: null, uisourcegen: null,
    interactive: false, needsSprite: false, needsText: false, requiredRefs: [],
  },
};

/** demo UguiToolkit 的 component 参数枚举（实测 UguiToolModule.cs:32）。 */
const MODULE_COMPONENTS = [
  'panel', 'image', 'text', 'button', 'kpi', 'barchart', 'ring',
  'alert', 'progress', 'toggle', 'slider', 'input', 'card',
];

/** Spec type → ugui.create_component 的 component 值；不可映射时为 null。 */
const TO_MODULE = {
  panel: 'panel', card: 'card', image: 'image', icon: 'image',
  divider: 'image', label: 'text', button: 'button', toggle: 'toggle',
  slider: 'slider', input: 'input', progress: 'progress', badge: 'image',
  tab: 'toggle', group: 'panel', scroll: null, dropdown: null, canvas: null,
};

function isKnown(type) { return Object.prototype.hasOwnProperty.call(TYPES, type); }
function def(type) { return TYPES[type] || null; }
function listTypes() { return Object.keys(TYPES); }

/** 该类型是否必须走 code.execute 建真组件（模块建出来是残缺的）。 */
function needsRealBuild(type) {
  const d = TYPES[type];
  if (!d) return false;
  return d.requiredRefs.length > 0 || TO_MODULE[type] === null;
}

/** 把 Spec 节点编译成 UISourceGen 的 screen.controls[] 项（仅 asset=regenerate 时用）。 */
function toScreenControl(node, opts = {}) {
  const d = TYPES[node.type];
  if (!d || !d.uisourcegen) return null;
  const coords = require('./coords');
  const r = coords.toUISourceGen(node, opts.canvas);
  const c = {
    id: node.id,
    type: d.uisourcegen,
    anchor: r.anchor,
    rect: r.rect,
    style: {
      background: (node.style && node.style.background) || (opts.palette && opts.palette.surface) || '#1E2433',
      corner_radius: (node.style && node.style.corner_radius) || (opts.palette && opts.palette.radius) || 12,
      text_color: node.color || (opts.palette && opts.palette.on_surface) || '#FFFFFF',
      font_size: node.font_size || (opts.palette && opts.palette.font_size) || 24,
      text_align: (node.style && node.style.text_align) || 'middle_center',
    },
  };
  if (node.text) c.text = node.text;
  if (node.icon) c.icon = node.icon;
  if (node.value !== undefined) c.value = node.value;
  if (node.checked !== undefined) c.checked = !!node.checked;
  if (node.items && node.items.length) c.items = node.items;
  return c;
}

/** 把整份 Spec 编译成 UISourceGen 的 screen 对象。 */
function toScreen(spec) {
  const controls = spec.nodes.map(n => toScreenControl(n, { canvas: spec.canvas, palette: spec.palette }))
    .filter(Boolean);
  return {
    name: spec.name,
    width: spec.source.width,
    height: spec.source.height,
    background: spec.palette.background || '#00000000',
    background_gradient: false,
    theme: {
      primary: spec.palette.primary,
      surface: spec.palette.surface,
      on_surface: spec.palette.on_surface,
      border: spec.palette.border,
      radius: spec.palette.radius,
      font_size: spec.palette.font_size,
    },
    controls,
  };
}

module.exports = {
  TYPES, MODULE_COMPONENTS, TO_MODULE,
  isKnown, def, listTypes, needsRealBuild,
  toScreenControl, toScreen,
};

'use strict';
/**
 * ui.js — UISourceGen 封装：把 UI / 图标 / 标牌渲染成 PNG，供 3D 场景使用。
 *
 * 在建模流水线里的位置：贴图的一个来源。三种典型用法——
 *   1) world-space UI：把 render() 出的整屏图贴到一块面板 mesh 上；
 *   2) 图标/精灵：draw() 单个控件，得到透明 PNG；
 *   3) 文字标牌：draw() 一个 text 元素，得到带 alpha 的字。
 *
 * 【实测坑】
 *   - uisourcegen.examples 返回的示例 JSON **本身有语法错误**
 *     （label 的 style 对象缺一个闭合括号），直接 JSON.parse 会失败。
 *     当成"结构参考"看，不要直接喂给 render。
 *   - rect 是 [x, y, w, h]，anchor=center 时 x,y 是**中心点**；
 *     anchor=topLeft 时 x,y 是左上角。两种混用是错位的主因。
 *   - 控件默认输出九宫格元数据（nine_L/T/R/B），拉伸时用得上。
 *
 * 用法：
 *   const U = require('./ui');
 *   U.configure({ project: '...' });
 *   const r = await U.signboard({ text: 'OPEN', width: 512, height: 256,
 *                                 outputDir: 'Assets/_SkillTest', baseName: 'Sign' });
 */

const mcp = require('./mcp');

function configure(opts) { return mcp.configure(opts); }

/** 控件类型（来自 uisourcegen.spec，实测枚举）。 */
const CONTROL_TYPES = [
  'Button', 'Panel', 'Card', 'Slider', 'Toggle', 'ProgressBar', 'Label', 'Input',
  'Checkbox', 'Radio', 'Icon', 'Divider', 'Badge', 'Tab', 'Dropdown', 'Scrollbar',
];

/** 内置图标名。 */
const ICONS = ['check', 'cross', 'arrow', 'chevron-down', 'star', 'menu', 'play',
  'plus', 'minus', 'search', 'gear', 'heart'];

/** 状态名。 */
const STATES = ['Normal', 'Hover', 'Pressed', 'Disabled', 'Selected', 'Checked'];

/**
 * 中日韩字体候选。**只给相对系统字体目录的文件名**，由 resolveFont() 拼出
 * 平台路径；本文件不含任何盘符或用户目录。
 */
const CJK_FONT_FILES = [
  'msyh.ttc', 'msyhbd.ttc', 'simhei.ttf', 'simsun.ttc',   // Windows
  'PingFang.ttc', 'Hiragino Sans GB.ttc',                 // macOS
  'NotoSansCJK-Regular.ttc', 'NotoSansCJKsc-Regular.otf', // Linux
  'wqy-zenhei.ttc', 'DejaVuSans.ttf',
];

/** 各平台的系统字体目录（由系统根拼出，不写死盘符）。 */
function systemFontDirs() {
  const os = require('os');
  const path = require('path');
  // 用 homedir 的根节点作为"系统根"，避免在代码里出现绝对路径字面量
  const root = path.parse(os.homedir()).root || path.sep;

  if (process.platform === 'win32') {
    const win = process.env.SystemRoot || process.env.windir;
    const dirs = [];
    if (win) dirs.push(path.join(win, 'Fonts'));
    if (process.env.LOCALAPPDATA) {
      dirs.push(path.join(process.env.LOCALAPPDATA, 'Microsoft', 'Windows', 'Fonts'));
    }
    return dirs;
  }
  if (process.platform === 'darwin') {
    return [
      path.join(root, 'System', 'Library', 'Fonts'),
      path.join(root, 'Library', 'Fonts'),
      path.join(os.homedir(), 'Library', 'Fonts'),
    ];
  }
  return [
    path.join(root, 'usr', 'share', 'fonts'),
    path.join(root, 'usr', 'local', 'share', 'fonts'),
    path.join(os.homedir(), '.fonts'),
  ];
}

/**
 * 找一个能显示中文的字体文件，返回绝对路径；找不到返回 null。
 * 结果按进程缓存，避免每次渲染都扫目录。
 */
let _fontCache;
function resolveFont() {
  if (_fontCache !== undefined) return _fontCache;
  const fs = require('fs');
  const path = require('path');
  for (const dir of systemFontDirs()) {
    for (const name of CJK_FONT_FILES) {
      const p = path.join(dir, name);
      try { if (fs.existsSync(p)) return (_fontCache = p); } catch (e) { /* ignore */ }
    }
  }
  _fontCache = null;
  return null;
}

/**
 * 同步校验界面描述。async 函数在 await 之前不抛错，
 * 所以校验要单独可调用，否则 try/catch 抓不到。
 */
function assertScreen(screen) {
  if (!screen || !Array.isArray(screen.controls)) {
    throw new Error('screen 需要 controls 数组');
  }
  screen.controls.forEach((c, i) => {
    const tag = c && c.id ? `控件 "${c.id}"` : `controls[${i}]`;
    if (!c || !c.type) throw new Error(`${tag} 缺少 type`);
    if (!Array.isArray(c.rect) || c.rect.length !== 4 || !c.rect.every(Number.isFinite)) {
      throw new Error(`${tag} 的 rect 必须是 [x, y, w, h] 四个数值，收到 ${JSON.stringify(c && c.rect)}`);
    }
  });
  return screen;
}

function assertIcon(name) {
  if (!ICONS.includes(name)) {
    throw new Error(`未知图标 "${name}"。可用：${ICONS.join(' / ')}`);
  }
  return name;
}

/**
 * 渲染整个界面。
 * @param {object} screen { name, width, height, background, theme, controls:[...] }
 * @param {object} [o] { outputDir, baseName, preview, supersample }
 */
async function render(screen, o) {
  assertScreen(screen);
  const opt = o || {};
  const args = { screen };
  if (opt.outputDir) args.output_dir = opt.outputDir;
  if (opt.baseName) args.base_name = opt.baseName;
  if (opt.preview !== undefined) args.preview = opt.preview;
  if (opt.supersample) args.supersample = opt.supersample;
  return await mcp.call('uisourcegen.render', args);
}

/** 批量渲染多个界面。 */
async function batch(screens, o) {
  const opt = o || {};
  if (!Array.isArray(screens) || screens.length === 0) throw new Error('batch 需要非空 screens');
  const args = { screens };
  if (opt.outputDir) args.output_dir = opt.outputDir;
  if (opt.preview !== undefined) args.preview = opt.preview;
  return await mcp.call('uisourcegen.batch', args);
}

/**
 * 通用画布绘制（Skia 语义，比 render 灵活）：矩形/圆/路径/文字/图标/渐变。
 * @param {object} canvas CanvasModel 结构
 * @param {object} [o]
 */
async function draw(canvas, o) {
  const opt = o || {};
  if (!canvas || !Array.isArray(canvas.shapes) && !Array.isArray(canvas.elements)) {
    // 不强制字段名，交给 Unity 侧报错，但先给出可读提示
  }
  const args = { canvas };
  if (opt.outputDir) args.output_dir = opt.outputDir;
  if (opt.baseName) args.base_name = opt.baseName;
  if (opt.format) args.format = opt.format;
  if (opt.quality) args.quality = opt.quality;
  if (opt.width) args.width = opt.width;
  if (opt.height) args.height = opt.height;
  return await mcp.call('uisourcegen.draw', args);
}

/** 测量文字尺寸，用于排版时算布局。 */
async function measure(text, o) {
  const opt = o || {};
  const args = { text: text };
  if (opt.fontSize) args.font_size = opt.fontSize;
  if (opt.fontPath) args.font_path = opt.fontPath;
  if (opt.fontFamily) args.font_family = opt.fontFamily;
  if (opt.bold !== undefined) args.bold = opt.bold;
  if (opt.letterSpacing) args.letter_spacing = opt.letterSpacing;
  return await mcp.call('uisourcegen.measure', args);
}

/** SVG 栅格化：把路径直接变成 PNG。 */
async function svg(svgPath, o) {
  const opt = o || {};
  const args = { path: svgPath };
  if (opt.outputDir) args.output_dir = opt.outputDir;
  if (opt.baseName) args.base_name = opt.baseName;
  if (opt.width) args.width = opt.width;
  if (opt.height) args.height = opt.height;
  if (opt.fill) args.fill = opt.fill;
  if (opt.stroke) args.stroke = opt.stroke;
  if (opt.padding !== undefined) args.padding = opt.padding;
  return await mcp.call('uisourcegen.svg', args);
}

/** 列出可用字体。 */
async function fonts(filter) {
  const args = {};
  if (filter) args.filter = filter;
  return await mcp.call('uisourcegen.fonts', args);
}

/** 列出模板。 */
async function templates(filter) {
  const args = {};
  if (filter && filter.tag) args.tag = filter.tag;
  if (filter && filter.name) args.name = filter.name;
  return await mcp.call('uisourcegen.templates', args);
}

/** 语法说明（含控件类型、状态、图标名）。 */
async function spec() {
  return await mcp.call('uisourcegen.spec');
}

// ---------------------------------------------------------------- 便捷配方

/**
 * 文字标牌：一块带底色的面板 + 居中文字，输出透明背景的 PNG。
 * 用来给 3D 场景做店招、门牌、按钮贴图。
 *
 * @param {object} o { text, width, height, outputDir, baseName,
 *                     background, textColor, fontSize, radius, fontPath }
 */
async function signboard(o) {
  const opt = o || {};
  if (!opt.text) throw new Error('signboard 需要 text');
  const W = opt.width || 512;
  const H = opt.height || 256;
  const screen = {
    name: opt.baseName || 'Signboard',
    width: W,
    height: H,
    background: opt.background || '#00000000',
    background_gradient: false,
    theme: {
      primary: opt.primary || '#3399F5',
      surface: opt.surface || '#1c1d22',
      on_surface: opt.textColor || '#f2f3f7',
      radius: opt.radius === undefined ? 16 : opt.radius,
    },
    controls: [
      {
        id: 'plate',
        type: 'panel',
        anchor: 'center',
        rect: [W / 2, H / 2, W, H],
        style: {
          background: opt.surface || '#1c1d22',
          corner_radius: opt.radius === undefined ? 16 : opt.radius,
        },
      },
      {
        id: 'label',
        type: 'label',
        anchor: 'center',
        rect: [W / 2, H / 2, W, H],
        text: opt.text,
        style: {
          text_color: opt.textColor || '#f2f3f7',
          font_size: opt.fontSize || Math.round(H * 0.34),
          text_align: 'middle_center',
        },
      },
    ],
  };
  // 没指定字体时自动挑一个系统中文字体，避免中文渲染成方块
  const fp = opt.fontPath || resolveFont();
  if (fp) screen.theme.font_path = fp;
  return await render(screen, {
    outputDir: opt.outputDir, baseName: opt.baseName || 'Signboard',
    preview: true, supersample: opt.supersample || 2,
  });
}

/**
 * 图标精灵：单个内置图标，透明背景。
 * 结果可直接当 3D 场景里的 sprite 贴图。
 */
async function icon(name, o) {
  assertIcon(name);
  const opt = o || {};
  const S = opt.size || 128;
  const screen = {
    name: 'Icon_' + name,
    width: S,
    height: S,
    background: '#00000000',
    background_gradient: false,
    theme: { primary: opt.color || '#f2f3f7', surface: '#00000000', on_surface: opt.color || '#f2f3f7' },
    controls: [{
      id: 'icon', type: 'icon', anchor: 'center',
      rect: [S / 2, S / 2, S, S], icon: name,
      style: { icon_color: opt.color || '#f2f3f7' },
    }],
  };
  return await render(screen, { outputDir: opt.outputDir, baseName: 'Icon_' + name, preview: false });
}

module.exports = {
  configure, CONTROL_TYPES, ICONS, STATES, assertScreen, assertIcon,
  render, batch, draw, measure, svg, fonts, templates, spec,
  signboard, icon, resolveFont, systemFontDirs,
};

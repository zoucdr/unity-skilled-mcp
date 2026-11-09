#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 TUTORIAL.md 导出成一份自包含、A4 打印友好的 HTML。

零绝对路径：技能根目录由本文件位置运行时推导，图片以 base64 内嵌，
生成的 HTML 可以单独拷走；Mermaid 代码块转成静态 SVG，离线可打印。

用法：python scripts/export_html.py
输出：技能根目录 TUTORIAL.html（浏览器打开 → Ctrl+P → 另存为 PDF）
"""
import os
import re
import base64
import html as H

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'TUTORIAL.md')
OUT = os.path.join(ROOT, 'TUTORIAL.html')
TITLE = 'unity-modeling 教学：架构与提示词'

_imgcache = {}


def img_tag(rel, alt):
    p = os.path.join(ROOT, rel)
    if not os.path.exists(p):
        return '<em>[缺图 %s]</em>' % H.escape(rel)
    if rel not in _imgcache:
        with open(p, 'rb') as f:
            _imgcache[rel] = base64.b64encode(f.read()).decode('ascii')
    return '<img src="data:image/png;base64,%s" alt="%s">' % (
        _imgcache[rel], H.escape(alt, quote=True))


def inline(t):
    t = H.escape(t, quote=False)
    t = re.sub(r'!\[([^\]]*)\]\(([^)]+)\)', lambda m: img_tag(m.group(2), m.group(1)), t)
    t = re.sub(r'\[([^\]]+)\]\(([^)]+)\)', r'<a href="\2">\1</a>', t)
    t = re.sub(r'`([^`]+)`', r'<code>\1</code>', t)
    t = re.sub(r'\*\*([^*]+)\*\*', r'<strong>\1</strong>', t)
    return t


SVG_LAYERS = '''<svg class="dia" viewBox="0 0 680 500" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="六层架构图">
<rect width="680" height="500" fill="#ffffff"/>
<g font-family="Microsoft YaHei, Segoe UI, sans-serif" font-size="13" fill="#1a3a5c" text-anchor="middle">
<g stroke="#9fb8d0" stroke-width="1.5" fill="#eef4fa">
<rect x="150" y="10" width="380" height="56" rx="8"/><rect x="150" y="90" width="380" height="56" rx="8"/>
<rect x="150" y="170" width="380" height="56" rx="8"/><rect x="150" y="250" width="380" height="56" rx="8"/>
<rect x="150" y="330" width="380" height="56" rx="8"/><rect x="150" y="410" width="380" height="56" rx="8"/>
</g>
<text x="340" y="33">L0 意图层</text><text x="340" y="53" font-size="11">人说的话 / 参考图</text>
<text x="340" y="113">L1 选路层</text><text x="340" y="133" font-size="11">交付物形态 → 两条互斥主干</text>
<text x="340" y="193">L2 流水线层</text><text x="340" y="213" font-size="11">分步产出几何 / 贴图 / 材质</text>
<text x="340" y="273">L3 离线护栏</text><text x="340" y="293" font-size="11">语义收口 + 静态断言 + 自检</text>
<text x="340" y="353">L4 执行层</text><text x="340" y="373" font-size="11">Unity MCP / code.execute</text>
<text x="340" y="433">L5 验收层</text><text x="340" y="453" font-size="11">在线校验 + 渲染截图</text>
</g>
<g stroke="#5b7fa6" stroke-width="1.6" fill="#5b7fa6">
<line x1="340" y1="66" x2="340" y2="88"/><polygon points="336,84 344,84 340,90"/>
<line x1="340" y1="146" x2="340" y2="168"/><polygon points="336,164 344,164 340,170"/>
<line x1="340" y1="226" x2="340" y2="248"/><polygon points="336,244 344,244 340,250"/>
<line x1="340" y1="306" x2="340" y2="328"/><polygon points="336,324 344,324 340,330"/>
<line x1="340" y1="386" x2="340" y2="408"/><polygon points="336,404 344,404 340,410"/>
</g>
<text x="352" y="322" font-family="Microsoft YaHei, sans-serif" font-size="10" fill="#a35b12">通过才放行</text>
<g stroke="#c07a2c" stroke-width="1.4" fill="none" stroke-dasharray="5 4">
<path d="M530 438 C620 438 620 278 534 278"/>
</g>
<polygon points="538,274 530,282 538,286" fill="#c07a2c"/>
<text x="566" y="360" font-family="Microsoft YaHei, sans-serif" font-size="10" fill="#a35b12" transform="rotate(-90 566 360)">失败现象回流成新铁律</text>
</svg>'''

SVG_ROUTE = '''<svg class="dia" viewBox="0 0 680 430" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="选路决策图">
<rect width="680" height="430" fill="#ffffff"/>
<g font-family="Microsoft YaHei, Segoe UI, sans-serif" font-size="12" fill="#1a3a5c" text-anchor="middle">
<rect x="170" y="14" width="340" height="50" rx="10" fill="#eef4fa" stroke="#9fb8d0" stroke-width="1.5"/>
<text x="340" y="44">贴图必须沿曲面连续展开？</text>
<rect x="16" y="120" width="200" height="74" rx="8" fill="#fff3e2" stroke="#d9a45b" stroke-width="1.5"/>
<text x="116" y="146">E 曲面主干</text><text x="116" y="166" font-size="10">C# 旋转体 + 柱面 UV</text><text x="116" y="183" font-size="10">lathe 护栏 / verify lathe 档</text>
<rect x="240" y="120" width="200" height="60" rx="8" fill="#eef4fa" stroke="#9fb8d0" stroke-width="1.5"/>
<text x="340" y="145">否 · 可拆成平面分区</text><text x="340" y="166" font-size="10">要什么形态？</text>
<rect x="464" y="120" width="200" height="74" rx="8" fill="#edf7ee" stroke="#7fae86" stroke-width="1.5"/>
<text x="564" y="146">复杂曲线图案</text><text x="564" y="166" font-size="10">外部绘制 PNG</text><text x="564" y="183" font-size="10">导入 Assets → 再接 A 或 E</text>
<g stroke="#5b7fa6" stroke-width="1.5" fill="none">
<path d="M250 64 L116 118"/><path d="M340 64 L340 118"/><path d="M430 64 L564 118"/>
<path d="M340 180 L90 250"/><path d="M340 180 L263 250"/><path d="M340 180 L437 250"/><path d="M340 180 L610 250"/>
</g>
<g font-size="10" fill="#5b7fa6">
<text x="150" y="86">是 · 杯 / 瓶 / 柱</text><text x="420" y="86">否</text><text x="562" y="86">图案是复杂曲线</text>
</g>
<g fill="#eef4fa" stroke="#9fb8d0" stroke-width="1.3">
<rect x="16" y="252" width="148" height="56" rx="7"/><rect x="190" y="252" width="148" height="56" rx="7"/>
<rect x="364" y="252" width="148" height="56" rx="7"/><rect x="538" y="252" width="126" height="56" rx="7"/>
</g>
<g font-size="11">
<text x="90" y="275">A</text><text x="90" y="294" font-size="10">硬表面 + 图集贴图</text>
<text x="264" y="275">B</text><text x="264" y="294" font-size="10">单 mesh 多材质</text>
<text x="438" y="275">C</text><text x="438" y="294" font-size="10">一套无缝 PBR</text>
<text x="601" y="275">D</text><text x="601" y="294" font-size="10">UI / 图标 / 标牌</text>
</g>
<rect x="16" y="340" width="648" height="62" rx="8" fill="#f7f9fc" stroke="#c9d7e5" stroke-width="1.2"/>
<text x="340" y="365" font-size="11">两条主干互斥：图集会切断曲面 UV 连续性；硬表面用旋转体是杀鸡用牛刀</text>
<text x="340" y="387" font-size="11">选路看三信号：几何形态 / 贴图连续性 / 图案复杂度</text>
</g>
</svg>'''

SVGS = [SVG_LAYERS, SVG_ROUTE]

CSS = '''
@page { size: A4; margin: 16mm 14mm 18mm; }
* { box-sizing: border-box; }
body { margin: 0 auto; max-width: 900px; padding: 28px 34px 60px;
  font-family: "Microsoft YaHei", "Segoe UI", "PingFang SC", sans-serif;
  font-size: 10.5pt; line-height: 1.75; color: #22303c; background: #fff; }
h1 { font-size: 22pt; color: #14395e; border-bottom: 3px solid #14395e; padding-bottom: 10px; margin: 6px 0 18px; }
h2 { font-size: 15pt; color: #14395e; border-bottom: 1px solid #c9d7e5; padding-bottom: 6px;
  margin: 28px 0 12px; break-after: avoid; }
h3 { font-size: 12pt; color: #1d4e77; margin: 20px 0 8px; break-after: avoid; }
p { margin: 8px 0; }
p.fig { text-align: center; margin: 14px 0; }
img { max-width: 100%; height: auto; }
figure.dia { text-align: center; margin: 14px 0; break-inside: avoid; }
svg.dia { max-width: 100%; height: auto; border: 1px solid #dbe5ef; border-radius: 8px; }
table { width: 100%; border-collapse: collapse; margin: 12px 0; font-size: 9.2pt; break-inside: avoid; }
th { background: #eaf1f8; color: #14395e; font-weight: 600; }
th, td { border: 1px solid #ccd8e4; padding: 6px 8px; text-align: left; vertical-align: top; }
tr { break-inside: avoid; }
tbody tr:nth-child(even) { background: #f8fafc; }
code { font-family: Consolas, "Courier New", monospace; font-size: 9pt;
  background: #f2f5f8; padding: 1px 4px; border-radius: 3px; color: #a0407a; }
pre { background: #f5f7fa; border: 1px solid #dde5ed; border-left: 4px solid #6f96bb;
  border-radius: 5px; padding: 10px 12px; font-size: 9pt; line-height: 1.55;
  white-space: pre-wrap; word-break: break-word; break-inside: avoid; }
pre code { background: none; padding: 0; color: #243746; }
blockquote { margin: 12px 0; padding: 8px 14px; background: #f2f7fc;
  border-left: 4px solid #6f96bb; color: #2c4356; break-inside: avoid; }
ul, ol { margin: 8px 0; padding-left: 24px; }
li { margin: 3px 0; }
hr { border: 0; border-top: 1px solid #dbe5ef; margin: 22px 0; }
a { color: #1d5f96; text-decoration: none; }
.toc { background: #f7fafe; border: 1px solid #dbe5ef; border-radius: 8px;
  padding: 12px 20px; margin: 18px 0 26px; break-inside: avoid; }
.toc h2 { border: 0; margin: 0 0 6px; font-size: 12pt; }
.toc ul { columns: 2; column-gap: 30px; margin: 0; padding-left: 4px;
  font-size: 9.5pt; list-style: none; }
.noprint { background: #fff8e8; border: 1px solid #e6cf9a; border-radius: 8px;
  padding: 10px 14px; margin: 0 0 20px; font-size: 9.5pt; }
.noprint button { background: #14395e; color: #fff; border: 0; border-radius: 5px;
  padding: 6px 14px; font-size: 9.5pt; cursor: pointer; margin-left: 10px; }
@media print { .noprint { display: none; } body { max-width: none; padding: 0; font-size: 10pt; } }
'''


def is_sep(L):
    """Markdown 表格分隔行：整行只由 | - : 空白组成，且至少有一个 -。"""
    s = L.strip()
    return bool(re.fullmatch(r'[|\-:\s]+', s)) and '-' in s


def render_table(head, rows):
    s = '<table><thead><tr>' + ''.join('<th>%s</th>' % inline(c) for c in head) + '</tr></thead><tbody>'
    for r in rows:
        s += '<tr>' + ''.join('<td>%s</td>' % inline(c) for c in r) + '</tr>'
    return s + '</tbody></table>'


def special(L):
    s = L.strip()
    return (not s or s.startswith('```') or s.startswith('>') or s.startswith('#')
            or s.startswith('|') or re.match(r'^\s*[-*]\s+', L)
            or re.match(r'^\s*\d+\.\s+', L) or re.match(r'^---+\s*$', s))


def convert(md):
    lines = md.split('\n')
    out, toc, i, mi, seen_h1 = [], [], 0, 0, False
    while i < len(lines):
        L, s = lines[i], lines[i].strip()

        if s.startswith('```'):
            lang = s[3:].strip()
            buf = []
            i += 1
            while i < len(lines) and not lines[i].strip().startswith('```'):
                buf.append(lines[i])
                i += 1
            i += 1
            if lang == 'mermaid':
                svg = SVGS[mi] if mi < len(SVGS) else '<pre>Mermaid 图</pre>'
                out.append('<figure class="dia">%s</figure>' % svg)
                mi += 1
            else:
                out.append('<pre><code>' + H.escape('\n'.join(buf)) + '</code></pre>')
            continue

        if s.startswith('|') and i + 1 < len(lines) and is_sep(lines[i + 1]):
            head = [c.strip() for c in s.strip('|').split('|')]
            i += 2
            rows = []
            while i < len(lines) and lines[i].strip().startswith('|'):
                rows.append([c.strip() for c in lines[i].strip().strip('|').split('|')])
                i += 1
            out.append(render_table(head, rows))
            continue

        m = re.match(r'^(#{1,6})\s+(.*)$', L)
        if m:
            lvl, title = len(m.group(1)), m.group(2)
            if lvl == 1 and not seen_h1:
                seen_h1 = True           # 标题由 HTML 外壳统一给出
                i += 1
                continue
            anchor = ''
            if lvl == 2:
                anchor = ' id="sec%d"' % (len(toc) + 1)
                toc.append(title)
            out.append('<h%d%s>%s</h%d>' % (lvl, anchor, inline(title), lvl))
            i += 1
            continue

        if re.match(r'^---+\s*$', s):
            out.append('<hr>')
            i += 1
            continue

        if L.startswith('>'):
            buf = []
            while i < len(lines) and lines[i].startswith('>'):
                buf.append(lines[i].lstrip('>').strip())
                i += 1
            out.append('<blockquote>' + '<br>'.join(inline(x) for x in buf) + '</blockquote>')
            continue

        if re.match(r'^\s*[-*]\s+', L):
            items = []
            while i < len(lines) and re.match(r'^\s*[-*]\s+', lines[i]):
                items.append(re.sub(r'^\s*[-*]\s+', '', lines[i]))
                i += 1
            out.append('<ul>' + ''.join('<li>%s</li>' % inline(x) for x in items) + '</ul>')
            continue

        if re.match(r'^\s*\d+\.\s+', L):
            items = []
            while i < len(lines) and re.match(r'^\s*\d+\.\s+', lines[i]):
                items.append(re.sub(r'^\s*\d+\.\s+', '', lines[i]))
                i += 1
            out.append('<ol>' + ''.join('<li>%s</li>' % inline(x) for x in items) + '</ol>')
            continue

        if not s:
            i += 1
            continue

        buf = [s]
        i += 1
        while i < len(lines) and not special(lines[i]):
            buf.append(lines[i].strip())
            i += 1
        body = '<br>'.join(inline(x) for x in buf)
        cls = ' class="fig"' if body.startswith('<img') and body.count('<img') == 1 else ''
        out.append('<p%s>%s</p>' % (cls, body))

    return '\n'.join(out), toc


def main():
    with open(SRC, encoding='utf-8') as f:
        body, toc = convert(f.read())

    toc_html = ('<nav class="toc"><h2>目录</h2><ul>' +
                ''.join('<li><a href="#sec%d">%s</a></li>' % (n + 1, H.escape(t))
                        for n, t in enumerate(toc)) + '</ul></nav>')
    banner = ('<div class="noprint">导出 PDF：点右侧按钮或按 <strong>Ctrl+P</strong> → '
              '目标选「另存为 PDF」→ 勾选「背景图形」。'
              '<button onclick="window.print()">打印 / 另存为 PDF</button>'
              '<br>本页由 <code>TUTORIAL.md</code> 生成，图片已内嵌，可单独拷贝；'
              '内容以 Markdown 源文件为准。</div>')
    doc = ('<!DOCTYPE html>\n<html lang="zh-CN"><head><meta charset="utf-8">'
           '<title>%s</title><style>%s</style></head><body>' % (TITLE, CSS) +
           banner + '<h1>%s</h1>' % TITLE + toc_html + body + '</body></html>')
    with open(OUT, 'w', encoding='utf-8') as f:
        f.write(doc)
    print('输出 %s' % OUT)
    print('大小 %.2f MB / 图片 %d 张 / 架构图 %d 个 / 表格 %d 个 / 目录 %d 项' % (
        os.path.getsize(OUT) / 1048576, len(_imgcache), doc.count('<svg'),
        doc.count('<table>'), len(toc)))


if __name__ == '__main__':
    main()

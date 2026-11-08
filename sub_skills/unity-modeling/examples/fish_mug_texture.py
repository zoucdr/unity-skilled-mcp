# -*- coding: utf-8 -*-
"""生成星巴克锦鲤杯的柱面展开贴图（BaseColor）。
u = theta/2pi -> x;  PIL y 向下, 顶部=杯口(v=1), 底部=杯底(v=0)
"""
import math, os, sys
from PIL import Image, ImageDraw, ImageFilter, ImageChops, ImageFont

# 产物写到脚本同级的 assets/，全部相对解析，不含任何绝对路径
OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'assets')
os.makedirs(OUT_DIR, exist_ok=True)

# 柱面周长 C=2*pi*0.8≈5.03, 杯高 H=1.15 -> C:H ≈ 4.37:1
# 贴图宽高比需接近该值，否则图案在杯面上会被横向拉伸
W, H = 4096, 1024
OUTER_D = (12, 22, 38)
OUTER_L = (33, 55, 86)
OUTER   = (22, 39, 62)
GOLD_HI = (233, 214, 150)
GOLD_LO = (146, 126, 72)
INK     = (22, 39, 62)

def bez(p0, p1, p2, p3, n=30):
    out = []
    for i in range(n + 1):
        t = i/n; s = 1-t
        out.append((s**3*p0[0] + 3*s*s*t*p1[0] + 3*s*t*t*p2[0] + t**3*p3[0],
                    s**3*p0[1] + 3*s*s*t*p1[1] + 3*s*t*t*p2[1] + t**3*p3[1]))
    return out

def path(*segs):
    pts = []
    for s in segs: pts.extend(bez(*s))
    return pts

def poly(draw, pts, fill, off=(0, 0)):
    draw.polygon([(x+off[0], y+off[1]) for x, y in pts], fill=fill)

def ellipse_poly(cx, cy, rx, ry, rot=0.0, n=56):
    ca, sa = math.cos(rot), math.sin(rot)
    return [(cx + rx*math.cos(t)*ca - ry*math.sin(t)*sa,
             cy + rx*math.cos(t)*sa + ry*math.sin(t)*ca)
            for t in [2*math.pi*i/n for i in range(n)]]

def crescent(cx, cy, rx, ry, rot=0.0, a0=-0.85, a1=3.95, w=0.45, n=22):
    """C 形月牙（开口朝右下），用作镂空鳞片。"""
    ca, sa = math.cos(rot), math.sin(rot)
    def tf(x, y): return (cx + x*ca - y*sa, cy + x*sa + y*ca)
    pts = []
    for i in range(n+1):
        t = a0 + (a1-a0)*i/n
        pts.append(tf(rx*math.cos(t), ry*math.sin(t)))
    for i in range(n+1):
        t = a1 - (a1-a0)*i/n
        pts.append(tf((rx-w*rx)*math.cos(t), (ry-w*ry)*math.sin(t)))
    return pts

# ---------- 1. 釉底 ----------
base = Image.new('RGB', (W, H)); bd = ImageDraw.Draw(base)
for y in range(H):
    t = y/(H-1)
    k = 0.55 + 0.45*math.sin(math.pi*min(1, max(0, (t+0.12)/1.24)))
    bd.line([(0, y), (W, y)], fill=tuple(int(OUTER_D[i] + (OUTER_L[i]-OUTER_D[i])*k*0.72) for i in range(3)))
glow = Image.new('L', (W, H), 0); gd = ImageDraw.Draw(glow)
for cx, amp, sig in [(600, 62, 210), (3440, 40, 180)]:
    for x in range(max(0, cx-3*sig), min(W, cx+3*sig)):
        gd.line([(x, 0), (x, H)], fill=int(amp*math.exp(-((x-cx)/sig)**2/2)))
base = Image.composite(Image.new('RGB', (W, H), (63, 92, 132)), base,
                       glow.filter(ImageFilter.GaussianBlur(28)))
noise = Image.effect_noise((W, H), 9).convert('L').point(lambda v: 128+int((v-128)*0.14))
base = Image.blend(base, Image.merge('RGB', (noise,)*3), 0.12)
bd = ImageDraw.Draw(base)
for y in range(0, 34):
    a = (34-y)/34
    bd.line([(0, y), (W, y)], fill=tuple(int(OUTER[i]*(1-a) + (74,124,190)[i]*a) for i in range(3)))
for y in range(H-26, H):
    a = (y-(H-26))/26
    bd.line([(0, y), (W, y)], fill=tuple(int(OUTER[i]*(1-a) + OUTER_D[i]*a) for i in range(3)))

# ---------- 2. 鱼的几何 ----------
OX, OY = 1728, 190          # 装饰中心 = W/2 - 320，鱼正对相机且位于杯身中部
fish = Image.new('L', (W, H), 0)          # 所有金色区域
bodym = Image.new('L', (W, H), 0)         # 仅鱼身
tailm = Image.new('L', (W, H), 0)        # 仅尾鳍
pfinm = Image.new('L', (W, H), 0)        # 仅胸鳍
cut  = Image.new('L', (W, H), 0)          # 镂空
fd, cd = ImageDraw.Draw(fish), ImageDraw.Draw(cut)
bdm, tdm, pdm = ImageDraw.Draw(bodym), ImageDraw.Draw(tailm), ImageDraw.Draw(pfinm)

# 2.1 鱼身：细长弧带（头左、背拱向右上、尾柄右下）
body = path(
    ((56, 244), (128, 152), (268, 92),  (396, 88)),      # 上缘/背
    ((396, 88), (498, 86),  (566, 132), (586, 196)),
    ((586, 196), (598, 234), (578, 254), (548, 258)),    # 尾柄圆钝
    ((548, 258), (420, 268), (252, 262), (140, 258)),    # 下缘/腹（略上收成月牙）
    ((140, 258), (96, 252),  (68, 248),  (56, 244)),
)
for d in (fd, bdm): poly(d, body, 255, (OX, OY))

# 上缘背鳍：低矮波浪（并入鱼身），不是圆珠
for i in range(11):
    t = 0.06 + i*0.084
    x = 56 + 520*t
    y = 244 - 156*math.sin(math.pi*t*0.95) - 4
    r = 17 + 9*math.sin(math.pi*t)
    pts = ellipse_poly(x, y, r, r*0.42)
    for d in (fd, bdm): poly(d, pts, 255, (OX, OY))

# 2.2 尾柄区：把鱼身右端 / 尾柄 / 尾鳍根连成一整片金色
join = path(
    ((500, 196), (578, 202), (618, 262), (612, 340)),
    ((612, 340), (606, 398), (556, 428), (492, 400)),
    ((492, 400), (430, 358), (406, 292), (438, 238)),
    ((438, 238), (458, 208), (480, 196), (500, 196)),
)
poly(fd, join, 255, (OX, OY))

# 2.3 大扇形尾鳍（尾柄左下方展开，右端与 join 相接）
tail = path(
    ((556, 372), (516, 430), (416, 486), (296, 504)),
    ((296, 504), (206, 512), (140, 476), (120, 414)),
    ((120, 414), (168, 398), (236, 392), (296, 366)),
    ((296, 366), (388, 340), (482, 340), (556, 372)),
)
# 第二片下垂尾叶
tail2 = path(
    ((296, 504), (352, 534), (442, 526), (500, 468)),
    ((500, 468), (452, 460), (378, 464), (322, 484)),
)
for pts in (tail, tail2):
    for d in (fd, tdm): poly(d, pts, 255, (OX, OY))

# 2.4 胸鳍（鱼腹下方的小叶）
pfin = path(
    ((296, 272), (306, 316), (340, 348), (372, 338)),
    ((372, 338), (358, 302), (330, 278), (308, 268)),
)
for d in (fd, pdm): poly(d, pfin, 255, (OX, OY))

# 2.5 鱼身鳞片：逐列扫描线铺满，保证覆盖整条鱼身
scales = Image.new('L', (W, H), 0); sd = ImageDraw.Draw(scales)
bp = bodym.load()
x0, x1 = OX+70, OX+570
for x in range(x0, x1, 33):
    ys = [y for y in range(OY+70, OY+290) if bp[x, y] > 128]
    if len(ys) < 40: continue
    top, bot = min(ys), max(ys)
    hh = bot-top
    rows = max(2, min(6, int(hh/37)))
    t = (x-x0)/(x1-x0)                      # 0 头 -> 1 尾
    for r in range(rows):
        cx = x + (16 if r % 2 else 0)
        cy = top + hh*(r+0.5)/rows
        if cx >= W or cy >= H or bp[cx, cy] < 128: continue
        rx = 21*(1-0.48*t)*(0.86 if r in (0, rows-1) else 1.0)
        ry = rx*0.70
        rot = -0.5 + 0.75*t + 0.06*r
        poly(sd, crescent(cx, cy, rx, ry, rot=rot, w=0.46), 255)
        if t > 0.5 and r < rows-1:
            poly(sd, crescent(cx+15, cy-12, rx*0.40, ry*0.44, rot=rot+0.3, w=0.5), 255)
scales = ImageChops.multiply(scales, bodym)
cut = ImageChops.lighter(cut, scales)

# 2.6 上缘锯齿深色缺口（背鳍鳞沿）
edge_cut = Image.new('L', (W, H), 0); ed = ImageDraw.Draw(edge_cut)
for i in range(12):
    t = 0.06 + i*0.078
    x = 56 + 520*t
    y = 244 - 156*math.sin(math.pi*t*0.95) - 8
    poly(ed, crescent(x+OX, y+OY+4, 15*(1-0.35*t), 10, rot=-0.2,
                       a0=-1.8, a1=3.3, w=0.55), 255)
cut = ImageChops.lighter(cut, ImageChops.multiply(edge_cut, bodym))

# 2.7 尾鳍放射脉络（严格裁到尾鳍内）
vein = Image.new('L', (W, H), 0); vd = ImageDraw.Draw(vein)
hub = (556, 372)
for i in range(10):
    a = math.radians(158 + i*11.5)
    ex, ey = hub[0] + 440*math.cos(a), hub[1] - 310*math.sin(a)
    seg = bez(hub, (hub[0]*0.7+ex*0.3, hub[1]*0.94+ey*0.06),
              (ex*0.78+hub[0]*0.22, ey*0.86+hub[1]*0.14), (ex, ey), 28)
    vd.line([(x+OX, y+OY) for x, y in seg], fill=255, width=4)
cut = ImageChops.lighter(cut, ImageChops.multiply(vein, tailm))
# 大水滴镂空：在尾柄金块内（原图最显眼的深色椭圆），用 fish 裁剪而非 tailm
drop = Image.new('L', (W, H), 0); dd = ImageDraw.Draw(drop)
poly(dd, ellipse_poly(536, 330, 54, 82, rot=-0.30, n=64), 255, (OX, OY))
cut = ImageChops.lighter(cut, ImageChops.multiply(drop, fish))
# 尾鳍与鱼身之间的深色分界弧（只到鱼腹中段，不切断尾柄）
sep = Image.new('L', (W, H), 0); sp = ImageDraw.Draw(sep)
seg = bez((168, 272), (250, 288), (320, 292), (382, 282), 26)
sp.line([(x+OX, y+OY) for x, y in seg], fill=255, width=6)
cut = ImageChops.lighter(cut, ImageChops.multiply(sep, fish))

# 2.8 胸鳍脉络
pv = Image.new('L', (W, H), 0); pvd = ImageDraw.Draw(pv)
for i in range(4):
    seg = bez((304, 280), (314, 302), (330+i*7, 322), (352+i*5, 334), 14)
    pvd.line([(x+OX, y+OY) for x, y in seg], fill=255, width=3)
cut = ImageChops.lighter(cut, ImageChops.multiply(pv, pfinm))

# 2.9 眼、张开的嘴、鳃线、头须
poly(cd, ellipse_poly(112, 226, 12, 12), 255, (OX, OY))
poly(cd, [(52, 240), (92, 250), (66, 266)], 255, (OX, OY))     # 嘴部楔形开口
cd.line([(66+OX, 266+OY), (96+OX, 264+OY), (118+OX, 258+OY)], fill=255, width=4)
seg = bez((158, 150), (176, 196), (172, 244), (152, 276), 22)
cd.line([(x+OX, y+OY) for x, y in seg], fill=255, width=4)
seg = bez((58, 250), (26, 242), (6, 222), (2, 206), 14)
fd.line([(x+OX, y+OY) for x, y in seg], fill=255, width=5)

# 镂空只在金色区域内生效
cut = ImageChops.multiply(cut, fish)
fish = ImageChops.subtract(fish, cut)

# ---------- 3. 金色渐变 + 金属颗粒 + 内缘高光 ----------
gold = Image.new('RGB', (W, H)); gd = ImageDraw.Draw(gold)
for y in range(H):
    t = y/(H-1); k = 0.5+0.5*math.sin(math.pi*(t*0.8+0.1))
    gd.line([(0, y), (W, y)], fill=tuple(int(GOLD_LO[i]+(GOLD_HI[i]-GOLD_LO[i])*k) for i in range(3)))
gn = Image.effect_noise((W, H), 14).convert('L').point(lambda v: 128+int((v-128)*0.32))
gold = Image.blend(gold, Image.merge('RGB', (gn,)*3), 0.17)
edge = ImageChops.subtract(fish, fish.filter(ImageFilter.MinFilter(7))).point(lambda v: min(255, int(v*0.8)))
gold = Image.composite(Image.new('RGB', (W, H), GOLD_HI), gold, edge)
out = Image.composite(gold, base, fish)
halo = fish.filter(ImageFilter.GaussianBlur(9)).point(lambda v: min(255, int(v*0.16)))
out = Image.composite(Image.new('RGB', (W, H), (128, 108, 62)), out, halo)

# ---------- 4. 文字与落款 ----------
d = ImageDraw.Draw(out)
def font_dirs():
    """运行时解析系统字体目录，不写死任何盘符或用户目录。"""
    home = os.path.expanduser('~')
    dirs = []
    if os.name == 'nt':
        windir = os.environ.get('WINDIR') or os.environ.get('SystemRoot')
        if windir: dirs.append(os.path.join(windir, 'Fonts'))
        local = os.environ.get('LOCALAPPDATA')
        if local: dirs.append(os.path.join(local, 'Microsoft', 'Windows', 'Fonts'))
    else:
        dirs += [os.path.join(os.path.sep, 'usr', 'share', 'fonts'),
                 os.path.join(os.path.sep, 'usr', 'local', 'share', 'fonts'),
                 os.path.join(home, '.fonts')]
        if sys.platform == 'darwin':
            dirs += [os.path.join(os.path.sep, 'System', 'Library', 'Fonts'),
                     os.path.join(os.path.sep, 'Library', 'Fonts'),
                     os.path.join(home, 'Library', 'Fonts')]
    return dirs

def font(name, s):
    for base in font_dirs():
        p = os.path.join(base, name)
        if os.path.isfile(p):
            try: return ImageFont.truetype(p, s)
            except Exception: pass
    return ImageFont.load_default()
F_BI = font('arialbi.ttf', 46)
F_R  = font('arial.ttf', 29)
F_S  = font('arial.ttf', 21)
WC = (214, 198, 142)
for i, ch in enumerate('STARBUCKS'):
    tmp = Image.new('RGBA', (62, 62), (0, 0, 0, 0))
    ImageDraw.Draw(tmp).text((8, 4), ch, font=F_BI, fill=WC+(255,))
    tmp = tmp.rotate(-11, resample=Image.BICUBIC, expand=True)
    out.paste(tmp, (1636, 350+i*46), tmp)
d.text((1784, 852), '8 1/2 fl oz', font=F_R, fill=(196, 182, 138))
d.text((2312, 806), '238 ml', font=F_S, fill=(176, 164, 124))
d.ellipse([2314, 846, 2366, 898], outline=(176, 164, 124), width=3)
d.text((2326, 860), 'S', font=font('arialbd.ttf', 26), fill=(196, 182, 138))

for y, a in [(46, 26), (H-44, 22)]:
    d.line([(0, y), (W, y)], fill=tuple(int(OUTER[i]*0.5+GOLD_LO[i]*0.5) for i in range(3)), width=2)

out.save(os.path.join(OUT_DIR, 'mug_basecolor.png'))
# Unity Standard 的 _MetallicGlossMap：R=金属度, A=光滑度
# 没有环境反射探针时金属度过高会让金彩发黑，控制在 ~0.4 保留亮金漫反射
mt = fish.filter(ImageFilter.GaussianBlur(1.2)).point(lambda v: 18 + int(v*0.38))
sm = fish.filter(ImageFilter.GaussianBlur(2)).point(lambda v: 200 + int(v*0.21))
zero = Image.new('L', (W, H), 0)
Image.merge('RGBA', (mt, zero, zero, sm)).save(os.path.join(OUT_DIR, 'mug_metallicgloss.png'))
out.resize((1024, 256), Image.LANCZOS).save(os.path.join(OUT_DIR, 'texture_preview.png'))
# 正面 90 度区域单独放大检查
out.crop((1448, 180, 2648, 900)).resize((900, 540), Image.LANCZOS).save(os.path.join(OUT_DIR, 'texture_center.png'))
print('saved', out.size, '| gold px', sum(1 for v in fish.get_flattened_data() if v > 128))

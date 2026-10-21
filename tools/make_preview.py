# -*- coding: utf-8 -*-
"""生成 AutoReconnectMinCore 的 Steam 工坊预览图 image.png（<1MB）。
用法: python tools/make_preview.py [输出路径]
设计: 深靛蓝渐变背景 + 金色「断链重连」意象 + 微软雅黑标题。零外部素材依赖。
"""
import math
import random
import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont

W, H = 1024, 576
GOLD = (232, 182, 76)
GOLD_HI = (245, 212, 136)
CREAM = (240, 234, 216)
MUTED = (168, 160, 188)

FONT_BD = r"C:\Windows\Fonts\msyhbd.ttc"
FONT_RG = r"C:\Windows\Fonts\msyh.ttc"


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def background():
    """对角线深色渐变 + 暗角。"""
    img = Image.new("RGB", (W, H))
    px = img.load()
    c1 = (18, 16, 28)      # 深靛
    c2 = (34, 28, 56)      # 靛紫
    c3 = (10, 9, 16)       # 近黑
    for y in range(H):
        for x in range(W):
            t = (x / W * 0.55 + y / H * 0.45)
            c = lerp(c1, c2, t) if t < 0.5 else lerp(c2, c3, (t - 0.5) * 2)
            px[x, y] = c
    # 暗角
    vig = Image.new("L", (W, H), 0)
    vd = ImageDraw.Draw(vig)
    vd.ellipse([-W * 0.35, -H * 0.55, W * 1.35, H * 1.55], fill=110)
    vig = vig.filter(ImageFilter.GaussianBlur(120))
    dark = Image.new("RGB", (W, H), (4, 3, 8))
    img = Image.composite(dark, img, vig.point(lambda v: 255 - v))
    return img


def embers(img, n=90, seed=7):
    """漂浮的火星/尘埃（STS2 标志性氛围元素）。"""
    rnd = random.Random(seed)
    ov = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(ov)
    for _ in range(n):
        x, y = rnd.uniform(0, W), rnd.uniform(0, H)
        r = rnd.uniform(0.6, 2.4)
        a = int(rnd.uniform(24, 120))
        warm = rnd.random() < 0.75
        col = GOLD_HI if warm else (150, 170, 220)
        d.ellipse([x - r, y - r, x + r, y + r], fill=col + (a,))
    ov = ov.filter(ImageFilter.GaussianBlur(0.6))
    img.alpha_composite(ov)


def glow(img, cx, cy, radius, color, peak=150):
    """径向光晕。"""
    ov = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(ov)
    steps = 26
    for i in range(steps, 0, -1):
        r = radius * i / steps
        a = int(peak * (1 - i / steps) ** 2)
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color + (a,))
    ov = ov.filter(ImageFilter.GaussianBlur(18))
    img.alpha_composite(ov)


def chain_link(draw, cx, cy, w, h, angle, color, width, spark=False):
    """一节链条：旋转的空心圆角矩形环。"""
    link = Image.new("RGBA", (w * 2 + 8, h * 2 + 8), (0, 0, 0, 0))
    ld = ImageDraw.Draw(link)
    ld.rounded_rectangle([4, 4, 4 + w * 2, 4 + h * 2], radius=h, outline=color, width=width)
    link = link.rotate(angle, resample=Image.BICUBIC, expand=True)
    img_layer = link
    return img_layer, (cx, cy)


def chain_motif(img, cx, cy):
    """两节断开又即将重连的链条 + 缺口电弧。"""
    ov = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    # 左链节（顺时针倾角）
    lw, lh, lw2 = 150, 58, 12
    left = Image.new("RGBA", (400, 220), (0, 0, 0, 0))
    ld = ImageDraw.Draw(left)
    ld.rounded_rectangle([20, 20, 20 + lw, 20 + lh], radius=lh // 2, outline=GOLD, width=lw2)
    left = left.rotate(-18, resample=Image.BICUBIC, expand=True)
    ov.alpha_composite(left, (int(cx - 250), int(cy - 90)))
    # 右链节
    right = Image.new("RGBA", (400, 220), (0, 0, 0, 0))
    rd = ImageDraw.Draw(right)
    rd.rounded_rectangle([20, 20, 20 + lw, 20 + lh], radius=lh // 2, outline=GOLD_HI, width=lw2)
    right = right.rotate(18, resample=Image.BICUBIC, expand=True)
    ov.alpha_composite(right, (int(cx + 40), int(cy - 90)))
    # 缺口处的重连电弧（之字形亮线）
    pts = []
    x0, x1 = cx - 55, cx + 55
    for i in range(25):
        t = i / 24
        x = x0 + (x1 - x0) * t
        y = cy + 26 * math.sin(t * math.pi * 3) * (1 - t * 0.35)
        pts.append((x, y))
    ad = ImageDraw.Draw(ov)
    ad.line(pts, fill=GOLD_HI + (235,), width=5)
    ad.line([(x, y - 2) for x, y in pts], fill=(255, 244, 210, 160), width=2)
    ov = ov.filter(ImageFilter.GaussianBlur(0.4))
    img.alpha_composite(ov)
    glow(img, cx, cy + 4, 90, (255, 214, 130), peak=110)


def text_block(img):
    d = ImageDraw.Draw(img)
    x = 470
    # 顶部小标签
    tag_font = ImageFont.truetype(FONT_RG, 22)
    tag = "SLay the Spire 2 MOD".upper().replace("SLay", "SLAY")
    tag = "SLAY THE SPIRE 2  MOD"
    tw = d.textlength(tag, font=tag_font)
    d.rounded_rectangle([x, 96, x + tw + 36, 96 + 40], radius=20, outline=GOLD, width=2)
    d.text((x + 18, 105), tag, font=tag_font, fill=GOLD)
    # 主标题
    title_font = ImageFont.truetype(FONT_BD, 96)
    d.text((x - 4, 150), "断线重连", font=title_font, fill=CREAM)
    tw2 = d.textlength("断线重连", font=title_font)
    # 标题下金色分隔线
    d.rectangle([x, 282, x + tw2, 288], fill=GOLD)
    # 英文名
    en_font = ImageFont.truetype(FONT_BD, 34)
    d.text((x, 308), "AutoReconnectMinCore", font=en_font, fill=GOLD_HI)
    # 功能说明
    desc_font = ImageFont.truetype(FONT_RG, 27)
    d.text((x, 372), "客机掉线自动重连 · 暂停菜单「邀请重连」按钮", font=desc_font, fill=MUTED)
    d.text((x, 412), "适配游戏 v0.111.0  ·  需 RitsuLib / JmcModLib", font=desc_font, fill=MUTED)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "image.png"
    img = background().convert("RGBA")
    embers(img)
    chain_motif(img, 285, 300)
    # 左侧暗化，突出右侧文字
    shade = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shade)
    for i in range(140):
        a = int(90 * (1 - i / 140))
        sd.line([(440 + i, 0), (440 + i, H)], fill=(8, 7, 14, a))
    img.alpha_composite(shade)
    text_block(img)
    img = img.convert("RGB")
    img.save(out, optimize=True)
    import os
    print(f"已生成 {out}  {os.path.getsize(out)/1024:.0f} KB  {img.size}")


if __name__ == "__main__":
    main()

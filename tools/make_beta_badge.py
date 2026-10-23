# -*- coding: utf-8 -*-
"""在工坊预览图右上角叠加「测试版 BETA」角标，避免与正式版物品混淆。

用法: python tools/make_beta_badge.py <图片路径>
依赖: Pillow（受管 venv: .workbuddy/binaries/python/envs/default/Scripts/python.exe）
"""
import sys

from PIL import Image, ImageDraw, ImageFont

BADGE_BG = (196, 92, 48)
BADGE_TEXT = (255, 255, 255)
FONT_BD = r"C:\Windows\Fonts\msyhbd.ttc"


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "image.png"
    img = Image.open(path).convert("RGB")
    W, H = img.size
    d = ImageDraw.Draw(img)

    label = "测试版 BETA"
    font = ImageFont.truetype(FONT_BD, 34)

    # 量文字尺寸（Pillow ≥8 用 textbbox）
    box = d.textbbox((0, 0), label, font=font)
    tw, th = box[2] - box[0], box[3] - box[1]

    pad_x, pad_y = 22, 12
    bw, bh = tw + pad_x * 2, th + pad_y * 2
    margin = 28
    x0, y0 = W - margin - bw, margin
    x1, y1 = W - margin, margin + bh

    d.rounded_rectangle([x0, y0, x1, y1], radius=10, fill=BADGE_BG)
    # textbbox 的偏移补偿：按 baseline 对齐到矩形垂直中心
    d.text((x0 + pad_x - box[0], y0 + pad_y - box[1]), label, font=font, fill=BADGE_TEXT)

    img.save(path, optimize=True)
    print(f"已叠加测试版角标: {path}")


if __name__ == "__main__":
    main()

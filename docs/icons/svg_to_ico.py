"""SVGをICOファイルに変換するスクリプト（svglib + reportlab + Pillow使用）"""
import io
import sys
from svglib.svglib import svg2rlg
from reportlab.graphics import renderPM
from PIL import Image

def svg_to_ico(svg_path, ico_path):
    # SVGをreportlab Drawingオブジェクトに変換
    drawing = svg2rlg(svg_path)
    if drawing is None:
        print(f"ERROR: SVGの読み込みに失敗: {svg_path}")
        sys.exit(1)

    sizes = [256, 64, 48, 32, 16]
    images = []

    for size in sizes:
        # スケール計算
        scale_x = size / drawing.width
        scale_y = size / drawing.height
        drawing.width = size
        drawing.height = size
        drawing.transform = (scale_x, 0, 0, scale_y, 0, 0)

        # PNG バイト列にレンダリング
        buf = io.BytesIO()
        renderPM.drawToFile(drawing, buf, fmt="PNG", bg=0xFFFFFF)
        buf.seek(0)
        img = Image.open(buf).convert("RGBA")

        # 白背景を透明化（角丸の外側）
        # SVGのrx=40の角丸に合わせ、白ピクセルを透明に（近似）
        img_data = img.load()
        for y in range(img.height):
            for x in range(img.width):
                r, g, b, a = img_data[x, y]
                # 完全な白（背景）を透明にする
                if r > 250 and g > 250 and b > 250:
                    img_data[x, y] = (r, g, b, 0)
        images.append(img)

        print(f"  {size}x{size} レンダリング完了")

    # ICOファイルとして保存
    images[0].save(
        ico_path,
        format="ICO",
        sizes=[(img.width, img.height) for img in images],
        append_images=images[1:]
    )
    print(f"\nICO作成完了: {ico_path}")

if __name__ == "__main__":
    svg_path = r"c:\Users\masanori\source\repos\Nimono\docs\icons\icon_n03.svg"
    ico_path = r"c:\Users\masanori\source\repos\Nimono\app.ico"
    svg_to_ico(svg_path, ico_path)

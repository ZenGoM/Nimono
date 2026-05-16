using Svg.Skia;
using SkiaSharp;

var svgPath = args.Length > 0 ? args[0] : @"c:\Users\masanori\source\repos\Nimono\docs\icons\icon_n03.svg";
var icoPath = args.Length > 1 ? args[1] : @"c:\Users\masanori\source\repos\Nimono\app.ico";

int[] sizes = [256, 64, 48, 32, 16];

// SVGをロード
var svg = new SKSvg();
svg.Load(svgPath);
if (svg.Picture is null)
{
    Console.Error.WriteLine("SVGの読み込みに失敗しました");
    return 1;
}

// 各サイズのPNGバイト列を生成
var pngBuffers = new List<byte[]>();
foreach (var size in sizes)
{
    var imageInfo = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(imageInfo);
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);

    float scale = size / 256f;
    canvas.Scale(scale, scale);
    canvas.DrawPicture(svg.Picture);

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    pngBuffers.Add(data.ToArray());
    Console.WriteLine($"  {size}x{size} レンダリング完了");
}

// ICOファイルを生成（バイナリ手書き）
// ICO形式: ヘッダー(6byte) + ディレクトリエントリ×N(16byte each) + 画像データ
using var ico = new FileStream(icoPath, FileMode.Create);
using var bw = new BinaryWriter(ico);

// ICO Header
bw.Write((ushort)0);            // Reserved
bw.Write((ushort)1);            // Type: 1=ICO
bw.Write((ushort)sizes.Length); // Count

// Calculate offsets
int headerSize = 6 + sizes.Length * 16;
int[] offsets = new int[sizes.Length];
int offset = headerSize;
for (int i = 0; i < sizes.Length; i++)
{
    offsets[i] = offset;
    offset += pngBuffers[i].Length;
}

// Directory entries
for (int i = 0; i < sizes.Length; i++)
{
    int sz = sizes[i];
    bw.Write((byte)(sz >= 256 ? 0 : sz)); // Width  (0 = 256)
    bw.Write((byte)(sz >= 256 ? 0 : sz)); // Height (0 = 256)
    bw.Write((byte)0);    // ColorCount
    bw.Write((byte)0);    // Reserved
    bw.Write((ushort)1);  // Planes
    bw.Write((ushort)32); // BitCount
    bw.Write((uint)pngBuffers[i].Length);
    bw.Write((uint)offsets[i]);
}

// Image data
foreach (var buf in pngBuffers)
    bw.Write(buf);

Console.WriteLine($"\nICO作成完了: {icoPath}");
return 0;

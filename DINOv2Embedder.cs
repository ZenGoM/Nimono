using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Nimono;

internal sealed class DINOv2Embedder : IDisposable
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std  = [0.229f, 0.224f, 0.225f];
    private const int InputSize = 224;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly bool _isSequenceOutput; // true = [batch, seq, hidden], false = [batch, hidden]

    public int EmbeddingDim { get; }

    private DINOv2Embedder(InferenceSession session)
    {
        _session = session;
        _inputName = session.InputNames[0];

        // pooler_output が存在すれば優先（CLS の集約済みベクトル）
        if (session.OutputNames.Contains("pooler_output"))
        {
            _outputName = "pooler_output";
            _isSequenceOutput = false;
        }
        else
        {
            _outputName = session.OutputNames[0];
            var dims = session.OutputMetadata[_outputName].Dimensions;
            _isSequenceOutput = dims.Length == 3;
        }

        var outMeta = session.OutputMetadata[_outputName];
        EmbeddingDim = (int)outMeta.Dimensions[^1];
    }

    public static bool TryCreate(
        string modelPath,
        [NotNullWhen(true)] out DINOv2Embedder? embedder,
        out string error)
    {
        embedder = null;
        error = "";
        try
        {
            var opts = new SessionOptions();
            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            opts.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            var session = new InferenceSession(modelPath, opts);
            embedder = new DINOv2Embedder(session);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public float[] Embed(string imagePath)
    {
        using var bmp = LoadResized(imagePath, InputSize);
        var tensor = BitmapToTensor(bmp);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };

        using var results = _session.Run(inputs, [_outputName]);
        var outTensor = results.First().AsTensor<float>();

        float[] embedding;
        if (_isSequenceOutput)
        {
            // shape [1, seq, hidden] — インデックス 0 が CLS トークン
            int hidden = outTensor.Dimensions[2];
            embedding = new float[hidden];
            for (int i = 0; i < hidden; i++)
                embedding[i] = outTensor[0, 0, i];
        }
        else
        {
            // shape [1, hidden]
            int hidden = outTensor.Dimensions[1];
            embedding = new float[hidden];
            for (int i = 0; i < hidden; i++)
                embedding[i] = outTensor[0, i];
        }

        return L2Normalize(embedding);
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        // 両ベクトルは L2 正規化済みなのでドット積 = コサイン類似度
        float dot = 0f;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return Math.Clamp(dot, 0f, 1f);
    }

    public void Dispose() => _session.Dispose();

    // ── 内部ヘルパー ──────────────────────────────────────────────────

    private static DenseTensor<float> BitmapToTensor(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        int stride = data.Stride;
        var raw = new byte[stride * h];
        Marshal.Copy(data.Scan0, raw, 0, raw.Length);
        bmp.UnlockBits(data);

        var tensor = new DenseTensor<float>([1, 3, h, w]);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * stride + x * 3;
                // GDI+ Format24bppRgb は BGR 順で格納
                tensor[0, 0, y, x] = (raw[idx + 2] / 255f - Mean[0]) / Std[0]; // R
                tensor[0, 1, y, x] = (raw[idx + 1] / 255f - Mean[1]) / Std[1]; // G
                tensor[0, 2, y, x] = (raw[idx + 0] / 255f - Mean[2]) / Std[2]; // B
            }
        }
        return tensor;
    }

    private static Bitmap LoadResized(string path, int size)
    {
        using var src = new Bitmap(path);
        var dst = new Bitmap(size, size, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, size, size);
        return dst;
    }

    private static float[] L2Normalize(float[] v)
    {
        float norm = 0f;
        foreach (var x in v) norm += x * x;
        norm = MathF.Sqrt(norm);
        if (norm < 1e-9f) return v;
        var result = new float[v.Length];
        for (int i = 0; i < v.Length; i++)
            result[i] = v[i] / norm;
        return result;
    }
}

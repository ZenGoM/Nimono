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

    /// <summary>
    /// GPU 実行プロバイダー（DirectML 等）で動作している場合 true。
    /// DirectML EP は Run() のマルチスレッド呼び出しに対して安全でないため、
    /// 呼び出し側で並列度を 1 にするためのフラグ。
    /// </summary>
    public bool IsGpu { get; }

    private DINOv2Embedder(InferenceSession session, bool isGpu)
    {
        _session = session;
        IsGpu = isGpu;
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
        bool useGpu,
        [NotNullWhen(true)] out DINOv2Embedder? embedder,
        out string error)
    {
        embedder = null;
        error = "";
        InferenceSession? session = null;
        try
        {
            var opts = new SessionOptions();
            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            opts.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            if (useGpu)
            {
                // DirectML EP 用の推奨設定（公式ガイダンス）:
                //   - GraphOptimizationLevel は BASIC まで下げる（ALL だと DML 非対応の最適化でクラッシュ）
                //   - ExecutionMode は SEQUENTIAL（DML は内部で並列化するため OUT-OF-ORDER と相性が悪い）
                //   - EnableMemoryPattern は false（DML は動的形状を内部で扱うため MemPattern が有害）
                opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
                opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                opts.EnableMemoryPattern = false;
                opts.AppendExecutionProvider_DML(0); // 失敗時は外側 catch で error にメッセージを格納
            }
            session = new InferenceSession(modelPath, opts);

            // GPU 指定時は実際に推論を1回走らせて、Run() 時のエラー（カーネル不在・ドライバ不整合等）を起動時に検出する。
            if (useGpu)
                SmokeTest(session);

            embedder = new DINOv2Embedder(session, useGpu);
            return true;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            error = ex.Message;
            return false;
        }
    }

    private static void SmokeTest(InferenceSession session)
    {
        var inputName = session.InputNames[0];
        var outputName = session.OutputNames.Contains("pooler_output")
            ? "pooler_output"
            : session.OutputNames[0];
        var dummy = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, dummy) };
        using var _ = session.Run(inputs, [outputName]);
    }

    /// <summary>
    /// 画像のロード・リサイズ・正規化テンソル変換を実施する。スレッド安全。
    /// GPU 利用時は本メソッドを並列実行して CPU 前処理を稼ぎ、Run() のみ <see cref="Infer"/> でロック直列化する。
    /// </summary>
    public DenseTensor<float> Preprocess(string imagePath)
    {
        using var bmp = LoadResized(imagePath, InputSize);
        return BitmapToTensor(bmp);
    }

    /// <summary>
    /// 前処理済みテンソルから埋め込みベクトルを生成する。
    /// DirectML EP の <c>session.Run()</c> はスレッドセーフではないため、GPU 利用時は外側でロックすること。
    /// </summary>
    public float[] Infer(DenseTensor<float> input)
    {
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) };
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

    public float[] Embed(string imagePath) => Infer(Preprocess(imagePath));

    /// <summary>
    /// 複数枚を 1 回の <c>Run()</c> で推論する（バッチ推論）。
    /// per-Run overhead をバッチ枚数に分散できるため、特に DirectML EP で大幅な高速化が見込める。
    /// 戻り値は入力と同じ順序の L2 正規化済み埋め込みベクトル配列。
    /// </summary>
    public float[][] InferBatch(IReadOnlyList<DenseTensor<float>> inputs)
    {
        int batchSize = inputs.Count;
        if (batchSize == 0) return [];
        if (batchSize == 1) return [Infer(inputs[0])];

        // 各入力テンソル ([1,3,H,W]) を [B,3,H,W] に積み上げる
        int planeSize = 3 * InputSize * InputSize;
        var batched = new DenseTensor<float>([batchSize, 3, InputSize, InputSize]);
        var batchSpan = batched.Buffer.Span;
        for (int b = 0; b < batchSize; b++)
            inputs[b].Buffer.Span.CopyTo(batchSpan.Slice(b * planeSize));

        var named = new[] { NamedOnnxValue.CreateFromTensor(_inputName, batched) };
        using var results = _session.Run(named, [_outputName]);
        var outTensor = results.First().AsTensor<float>();
        var flat = outTensor.ToArray(); // ORT バッキングが native の場合があるため一度フラット化

        int hidden = (int)outTensor.Dimensions[^1];
        var output = new float[batchSize][];

        if (_isSequenceOutput)
        {
            // [B, seq, hidden] — CLS = 各バッチ要素の seq=0 位置
            int seq = (int)outTensor.Dimensions[1];
            int bStride = seq * hidden;
            for (int b = 0; b < batchSize; b++)
            {
                var emb = new float[hidden];
                Array.Copy(flat, b * bStride, emb, 0, hidden);
                output[b] = L2Normalize(emb);
            }
        }
        else
        {
            // [B, hidden]
            for (int b = 0; b < batchSize; b++)
            {
                var emb = new float[hidden];
                Array.Copy(flat, b * hidden, emb, 0, hidden);
                output[b] = L2Normalize(emb);
            }
        }
        return output;
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

    // ImageNet 正規化を `pixel * scale + offset` 形式に畳み込んでおく（inner loop の演算削減）
    private static readonly float ScaleR = 1f / 255f / Std[0];
    private static readonly float ScaleG = 1f / 255f / Std[1];
    private static readonly float ScaleB = 1f / 255f / Std[2];
    private static readonly float OffsetR = -Mean[0] / Std[0];
    private static readonly float OffsetG = -Mean[1] / Std[1];
    private static readonly float OffsetB = -Mean[2] / Std[2];

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
        // 多次元インデクサ tensor[0,c,y,x] は ND→flat 計算がループ毎に走り遅い。
        // NCHW の flat レイアウトに直接書き込む（plane 0=R, 1=G, 2=B）。
        var buf = tensor.Buffer.Span;
        int planeSize = h * w;
        int planeR = 0;
        int planeG = planeSize;
        int planeB = 2 * planeSize;

        for (int y = 0; y < h; y++)
        {
            int rowBase = y * stride;
            int outBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = rowBase + x * 3;
                int o = outBase + x;
                // GDI+ Format24bppRgb は BGR 順で格納されているので、+2=R, +1=G, +0=B
                buf[planeR + o] = raw[p + 2] * ScaleR + OffsetR;
                buf[planeG + o] = raw[p + 1] * ScaleG + OffsetG;
                buf[planeB + o] = raw[p + 0] * ScaleB + OffsetB;
            }
        }
        return tensor;
    }

    private static Bitmap LoadResized(string path, int size)
    {
        // ICC プロファイル読み込み (useEmbeddedColorManagement) と検証 (validateImageData) を切る。
        // 大量画像の一括処理では数十%の高速化になる。FileStream 経由で開くことで Bitmap(path) のロックも回避。
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var src = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);
        var dst = new Bitmap(size, size, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        // DINOv2 のような大規模ニューラルネットは補間品質に頑健なため Bilinear で十分。
        // HighQualityBicubic は 3〜5× 遅く、品質差はモデル精度にほぼ影響しない。
        g.InterpolationMode  = InterpolationMode.Bilinear;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.SmoothingMode      = SmoothingMode.HighSpeed;
        g.PixelOffsetMode    = PixelOffsetMode.HighSpeed;
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

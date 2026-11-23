namespace Nimono;

/// <summary>基準と比べて違う部分を赤で示すための、色付きの文字片。</summary>
internal readonly record struct DiffRun(string Text, bool IsDiff);

internal static class DiffText
{
    /// <summary>
    /// 共通プレフィックス／サフィックスを除いた、text の中で other と違う中間部分の範囲 [Start, End) を返す。
    /// 同じ（または比べる相手が無い）なら空の範囲。
    /// </summary>
    public static (int Start, int End) DiffRange(string text, string? other)
    {
        if (other == null || text == other) return (0, 0);

        int prefixLen = 0;
        int maxPrefix = Math.Min(text.Length, other.Length);
        while (prefixLen < maxPrefix && text[prefixLen] == other[prefixLen])
            prefixLen++;

        // サフィックスはプレフィックスを除いた残りの部分で数える
        int suffixLen = 0;
        int maxSuffix = Math.Min(text.Length - prefixLen, other.Length - prefixLen);
        while (suffixLen < maxSuffix &&
               text[text.Length - 1 - suffixLen] == other[other.Length - 1 - suffixLen])
            suffixLen++;

        return (prefixLen, Math.Max(prefixLen, text.Length - suffixLen));
    }

    /// <summary>文字単位で比べ、text を「同じ部分・違う部分」の文字片に分ける。</summary>
    public static IEnumerable<DiffRun> CharDiff(string text, string? other)
    {
        var (start, end) = DiffRange(text, other);
        if (start > 0) yield return new DiffRun(text[..start], false);
        if (end > start) yield return new DiffRun(text[start..end], true);
        if (end < text.Length) yield return new DiffRun(text[end..], false);
    }

    /// <summary>項目単位で比べる（形式・サイズなど）。違えば項目全体を赤にする。</summary>
    public static DiffRun Field(string text, string? other) => new(text, other != null && text != other);
}

/// <summary>
/// 文字片ごとに色を変えて描くラベル。行は Lines で渡し、幅に収まらない行は空白の位置で折り返す。
/// SingleLine では折り返さず、はみ出す分を「…」で省略する。
/// </summary>
internal sealed class DiffLabel : Control
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private static readonly Color DiffColor = Color.FromArgb(200, 0, 0);

    private IReadOnlyList<IReadOnlyList<DiffRun>> _lines = Array.Empty<IReadOnlyList<DiffRun>>();

    public DiffLabel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SingleLine { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<IReadOnlyList<DiffRun>> Lines
    {
        get => _lines;
        set { _lines = value; Invalidate(); }
    }

    /// <summary>幅 width で描いたときに必要な高さ。</summary>
    public int MeasureHeight(int width)
    {
        int rows = SingleLine ? Math.Min(1, _lines.Count) : LayoutPieces(width).Max(p => (int?)p.Row + 1) ?? 0;
        return rows * Font.Height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (SingleLine)
        {
            PaintSingleLine(e.Graphics);
            return;
        }
        foreach (var p in LayoutPieces(Width))
            TextRenderer.DrawText(e.Graphics, p.Text, Font, new Point(p.X, p.Row * Font.Height),
                p.IsDiff ? DiffColor : ForeColor, Flags);
    }

    private void PaintSingleLine(Graphics g)
    {
        if (_lines.Count == 0) return;
        int x = 0;
        int ellipsisWidth = Measure("…");
        int lineHeight = Font.Height;
        int y = Math.Max(0, (Height - lineHeight) / 2);
        var runs = _lines[0];
        int total = runs.Sum(r => Measure(r.Text));
        bool overflow = total > Width;

        foreach (var run in runs)
        {
            int w = Measure(run.Text);
            if (!overflow || x + w <= Width - ellipsisWidth)
            {
                TextRenderer.DrawText(g, run.Text, Font, new Point(x, y), ColorOf(run), Flags);
                x += w;
                continue;
            }
            // 入りきる文字数まで描いて「…」を付ける
            int n = run.Text.Length;
            while (n > 0 && x + Measure(run.Text[..n]) > Width - ellipsisWidth) n--;
            if (n > 0) TextRenderer.DrawText(g, run.Text[..n], Font, new Point(x, y), ColorOf(run), Flags);
            x += n > 0 ? Measure(run.Text[..n]) : 0;
            TextRenderer.DrawText(g, "…", Font, new Point(x, y), ColorOf(run), Flags);
            return;
        }
    }

    private Color ColorOf(DiffRun run) => run.IsDiff ? DiffColor : ForeColor;

    private int Measure(string text) =>
        text.Length == 0 ? 0 : TextRenderer.MeasureText(text, Font, new Size(int.MaxValue, int.MaxValue), Flags).Width;

    private readonly record struct Piece(string Text, bool IsDiff, int X, int Row);

    /// <summary>
    /// 文字片を幅 width に並べる。空白の直後で区切った単語単位で折り返し、
    /// 1 単語が幅に収まらないときは文字単位で折り返す。
    /// </summary>
    private List<Piece> LayoutPieces(int width)
    {
        var pieces = new List<Piece>();
        width = Math.Max(1, width);
        int row = 0;
        foreach (var line in _lines)
        {
            int x = 0;
            foreach (var run in line)
            {
                foreach (string word in SplitWords(run.Text))
                {
                    int w = Measure(word);
                    if (x > 0 && x + w > width)
                    {
                        row++;
                        x = 0;
                    }
                    if (w <= width - x)
                    {
                        pieces.Add(new Piece(word, run.IsDiff, x, row));
                        x += w;
                        continue;
                    }
                    // 1 単語が 1 行に収まらない（長いフォルダー名など）→ 文字単位で詰める
                    int start = 0;
                    while (start < word.Length)
                    {
                        int n = 1;
                        while (start + n < word.Length && x + Measure(word.Substring(start, n + 1)) <= width) n++;
                        string part = word.Substring(start, n);
                        pieces.Add(new Piece(part, run.IsDiff, x, row));
                        x += Measure(part);
                        start += n;
                        if (start < word.Length) { row++; x = 0; }
                    }
                }
            }
            row++;
        }
        return pieces;
    }

    // 空白の直後で区切る（空白は前の単語に付けたまま）
    private static IEnumerable<string> SplitWords(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }
        if (start < text.Length) yield return text[start..];
    }
}

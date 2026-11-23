namespace Nimono;

/// <summary>
/// ステータスバーの文言。文字をあらかじめ Bitmap に描いておき、画面には 1 回の転送で出す。
///
/// 標準の <see cref="Label"/> では、DINOv2 を GPU で計算している間、文言を書き換えるたびに
/// 文字列が途中（「特徴量計算中 (DI」）で切れたまま 100ms 近く表示され、ちらついて見えた
/// （ダブルバッファーにしても同じ。ウィンドウがアクティブなときに起きる）。
/// 文字を画面に直接描かず、描き終えた Bitmap を転送するだけにすると起きない。
/// 幅が足りないときは折り返さず、1 行のまま「…」で省略する。
/// </summary>
internal sealed class StatusText : Control
{
    private Bitmap? _rendered;

    public StatusText()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Rerender(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Rerender(); }
    protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); Rerender(); }
    protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); Rerender(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Rerender(); }

    private void Rerender()
    {
        _rendered?.Dispose();
        _rendered = null;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        if (_rendered is null)
        {
            // TextRenderer（GDI）はアルファを書かないため、アルファを持たない形式にする
            _rendered = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using var g = Graphics.FromImage(_rendered);
            g.Clear(BackColor);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor, BackColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
        e.Graphics.DrawImageUnscaled(_rendered, 0, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rendered?.Dispose();
            _rendered = null;
        }
        base.Dispose(disposing);
    }
}

using System.Diagnostics;

namespace Nimono;

/// <summary>
/// 進捗の通知を一定間隔に間引く <see cref="IProgress{T}"/>。
///
/// <see cref="Progress{T}"/> は通知のたびに UI スレッドへ Post する。キャッシュが効いて計算が速いと
/// 数万件の通知が一気に溜まり、UI スレッドがそれを捌き終わるまで再描画（WM_PAINT）が後回しになって、
/// 画面が途中まで描かれたまま止まる。フェーズの切り替わりと完了（Current == Total）は必ず通知する。
/// </summary>
internal sealed class ThrottledProgress : IProgress<GroupingProgress>
{
    private readonly Progress<GroupingProgress> _inner;
    private readonly long _intervalTicks;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private long _lastReportTicks = long.MinValue;
    private string? _lastPhase;

    public ThrottledProgress(Action<GroupingProgress> handler, TimeSpan interval)
    {
        _inner = new Progress<GroupingProgress>(handler);
        _intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
    }

    public void Report(GroupingProgress value)
    {
        lock (_lock)
        {
            long now = _clock.ElapsedTicks;
            bool force = value.Phase != _lastPhase || value.Current >= value.Total;
            if (!force && now - _lastReportTicks < _intervalTicks) return;

            _lastReportTicks = now;
            _lastPhase = value.Phase;
        }
        ((IProgress<GroupingProgress>)_inner).Report(value);
    }
}

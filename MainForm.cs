using System.Diagnostics;

namespace Nimono;

public class MainForm : Form
{
    private const int ThumbnailSize = 140;

    private readonly List<string> _folders = new();
    private readonly List<Bitmap> _allocatedThumbnails = new();
    private readonly List<ImageGroup> _groups = new();
    private readonly Dictionary<int, FlowLayoutPanel> _groupFlows = new();
    private CancellationTokenSource? _cts;
    private AppSettings _settings = null!;

    private Button _selectFoldersButton = null!;
    private Button _scanButton = null!;
    private Button _cancelButton = null!;
    private NumericUpDown _thresholdInput = null!;
    private Label _statusLabel = null!;
    private ProgressBar _progressBar = null!;
    private Panel _resultsPanel = null!;
    private ToolTip _toolTip = null!;

    public MainForm()
    {
        InitializeComponents();
        _settings = SettingsStorage.Load();
        _folders.AddRange(_settings.SearchFolders.Where(Directory.Exists));
        _thresholdInput.Value = Math.Clamp(_settings.SimilarityThreshold, 50, 100);
        _thresholdInput.ValueChanged += (_, _) => SaveSettings();
        Size = new Size(_settings.WindowWidth, _settings.WindowHeight);
        ResizeEnd += (_, _) => SaveSettings();
        if (_folders.Count > 0)
        {
            _scanButton.Enabled = true;
            _statusLabel.Text = $"{_folders.Count} フォルダー選択中 — 「スキャン開始」を押してください";
        }
    }

    private void InitializeComponents()
    {
        Text = "Nimono — 似た画像をグルーピング";
        Size = new Size(1100, 750);
        MinimumSize = new Size(700, 500);
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;

        _toolTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 400 };

        var toolPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = SystemColors.Control,
        };

        _selectFoldersButton = new Button
        {
            Text = "フォルダー選択...",
            Width = 140,
            Left = 0,
            Top = 4,
            Height = 28,
        };
        _selectFoldersButton.Click += SelectFolders_Click;

        _scanButton = new Button
        {
            Text = "スキャン開始",
            Width = 110,
            Left = 148,
            Top = 4,
            Height = 28,
            Enabled = false,
        };
        _scanButton.Click += Scan_Click;

        var thresholdLabel = new Label
        {
            Text = "類似度しきい値:",
            Left = 270,
            Top = 9,
            Width = 100,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _thresholdInput = new NumericUpDown
        {
            Left = 370,
            Top = 6,
            Width = 70,
            Minimum = 50,
            Maximum = 100,
            Value = 85,
            Increment = 1,
        };
        var pctLabel = new Label
        {
            Text = "%",
            Left = 442,
            Top = 9,
            Width = 20,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _cancelButton = new Button
        {
            Text = "キャンセル",
            Width = 100,
            Dock = DockStyle.Right,
            Enabled = false,
        };
        _cancelButton.Click += (_, _) => _cts?.Cancel();

        toolPanel.Controls.Add(_selectFoldersButton);
        toolPanel.Controls.Add(_scanButton);
        toolPanel.Controls.Add(thresholdLabel);
        toolPanel.Controls.Add(_thresholdInput);
        toolPanel.Controls.Add(pctLabel);
        toolPanel.Controls.Add(_cancelButton);

        var statusPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Padding = new Padding(8, 0, 8, 0),
        };
        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Right,
            Width = 220,
            Style = ProgressBarStyle.Continuous,
            Minimum = 0,
            Maximum = 100,
        };
        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "フォルダーを選択してください",
        };
        statusPanel.Controls.Add(_statusLabel);
        statusPanel.Controls.Add(_progressBar);

        _resultsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.FromArgb(245, 245, 248),
            Padding = new Padding(8),
        };

        Controls.Add(_resultsPanel);
        Controls.Add(statusPanel);
        Controls.Add(toolPanel);
    }

    private void SelectFolders_Click(object? sender, EventArgs e)
    {
        using var dlg = new SearchFolderDialog(_folders);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _folders.Clear();
        _folders.AddRange(dlg.SelectedFolders);
        _scanButton.Enabled = _folders.Count > 0;
        SaveSettings();

        if (_folders.Count == 0)
            _statusLabel.Text = "フォルダーを選択してください";
        else
            _statusLabel.Text = $"{_folders.Count} フォルダー選択中 — 「スキャン開始」を押してください";
    }

    private void SaveSettings()
    {
        _settings.SearchFolders = _folders.ToList();
        _settings.SimilarityThreshold = (int)_thresholdInput.Value;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        SettingsStorage.Save(_settings);
    }

    private async void Scan_Click(object? sender, EventArgs e)
    {
        if (_folders.Count == 0) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SetScanningState(true);
        ClearResults();

        double threshold = (double)_thresholdInput.Value / 100.0;
        var folders = _folders.ToList();

        var progress = new Progress<GroupingProgress>(OnProgress);

        try
        {
            // 1) ファイル列挙
            _statusLabel.Text = "ファイルを列挙中...";
            _progressBar.Style = ProgressBarStyle.Marquee;
            var files = await Task.Run(
                () => ImageGrouper.EnumerateImages(folders, token), token);

            if (token.IsCancellationRequested) return;

            if (files.Count == 0)
            {
                _statusLabel.Text = "画像ファイルが見つかりませんでした";
                return;
            }

            // 2) ハッシュ計算
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
            var entries = await Task.Run(
                () => ImageGrouper.ComputeHashes(files, progress, token), token);

            if (token.IsCancellationRequested) return;

            // 3) グルーピング
            var groups = await Task.Run(
                () => ImageGrouper.Group(entries, threshold, progress, token), token);

            if (token.IsCancellationRequested) return;

            // 4) 表示
            RenderGroups(groups);

            int dupes = groups.Sum(g => g.Paths.Count);
            _statusLabel.Text = groups.Count == 0
                ? $"似た画像は見つかりませんでした（{files.Count:N0} 件をスキャン）"
                : $"{groups.Count:N0} グループ / {dupes:N0} 枚（{files.Count:N0} 件をスキャン）";
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "キャンセルされました";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"エラー: {ex.Message}";
        }
        finally
        {
            SetScanningState(false);
        }
    }

    private void OnProgress(GroupingProgress p)
    {
        _statusLabel.Text = $"{p.Phase} — {p.Current:N0} / {p.Total:N0}";
        if (p.Total > 0)
        {
            int pct = (int)Math.Clamp((long)p.Current * 100 / p.Total, 0, 100);
            _progressBar.Value = pct;
        }
    }

    private void SetScanningState(bool scanning)
    {
        _selectFoldersButton.Enabled = !scanning;
        _scanButton.Enabled = !scanning && _folders.Count > 0;
        _thresholdInput.Enabled = !scanning;
        _cancelButton.Enabled = scanning;
        if (!scanning)
        {
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
        }
    }

    private void ClearResults()
    {
        _resultsPanel.SuspendLayout();
        foreach (Control c in _resultsPanel.Controls)
            c.Dispose();
        _resultsPanel.Controls.Clear();
        foreach (var bmp in _allocatedThumbnails)
            bmp.Dispose();
        _allocatedThumbnails.Clear();
        _groups.Clear();
        _groupFlows.Clear();
        _resultsPanel.ResumeLayout();
    }

    private void RenderGroups(IReadOnlyList<ImageGroup> groups)
    {
        _groups.AddRange(groups);
        _resultsPanel.SuspendLayout();
        int top = 0;
        foreach (var group in groups)
        {
            var panel = BuildGroupPanel(group);
            panel.Left = 0;
            panel.Width = _resultsPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
            panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _resultsPanel.Controls.Add(panel);
            panel.PerformLayout();
            int desiredHeight = panel.PreferredSize.Height;
            panel.AutoSize = false;
            panel.Height = desiredHeight;
            panel.Top = top;
            top += desiredHeight + 8;
        }
        _resultsPanel.ResumeLayout(true);
        _resultsPanel.PerformLayout();
    }

    private Panel BuildGroupPanel(ImageGroup group)
    {
        var header = new Label
        {
            Text = $"グループ {group.Id} — {group.Paths.Count} 枚",
            Dock = DockStyle.Top,
            Height = 24,
            BackColor = Color.FromArgb(60, 90, 140),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(6),
            BackColor = Color.White,
        };

        _groupFlows[group.Id] = flow;
        foreach (var path in group.Paths)
        {
            var thumb = CreateThumbnail(path, group.Similarities[path], group.Id);
            flow.Controls.Add(thumb);
        }

        var container = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Padding = new Padding(0),
        };
        container.Controls.Add(flow);
        container.Controls.Add(header);
        return container;
    }

    private Control CreateThumbnail(string path, double similarity, int groupId)
    {
        var (bmp, originalSize) = TryLoadThumbnail(path);

        var fi = new FileInfo(path);
        string formatText = fi.Extension.TrimStart('.').ToUpperInvariant();
        string sizeText = FormatFileSize(fi.Length);
        string resText = originalSize != Size.Empty
            ? $"{originalSize.Width}×{originalSize.Height}"
            : "—";
        string dateText = fi.LastWriteTime.ToString("yyyy/MM/dd");
        string relativePath = GetRelativePath(path);

        const int SimLabelHeight = 16;
        const int PicTop = 4 + SimLabelHeight;

        var infoFont = new Font("Segoe UI", 7.5f);
        string infoText = $"{formatText} · {sizeText}\n{resText}\n{dateText}\n{relativePath}";
        int infoHeight = TextRenderer.MeasureText(
            infoText, infoFont, new Size(ThumbnailSize, int.MaxValue),
            TextFormatFlags.WordBreak).Height + 2;

        var wrap = new Panel
        {
            Width = ThumbnailSize + 8,
            Height = PicTop + ThumbnailSize + 24 + infoHeight + 6,
            Margin = new Padding(4),
            BackColor = Color.White,
        };

        var simLabel = new Label
        {
            Text = $"類似度: {similarity:P0}",
            Left = 4,
            Top = 4,
            Width = ThumbnailSize,
            Height = SimLabelHeight,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(60, 90, 140),
        };

        var pic = new PictureBox
        {
            Width = ThumbnailSize,
            Height = ThumbnailSize,
            Left = 4,
            Top = PicTop,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(230, 230, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Hand,
        };

        if (bmp != null)
        {
            pic.Image = bmp;
            _allocatedThumbnails.Add(bmp);
        }

        var nameLabel = new Label
        {
            Text = Path.GetFileName(path),
            Left = 4,
            Top = PicTop + ThumbnailSize + 6,
            Width = ThumbnailSize,
            Height = 18,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 8.25f),
        };

        var infoLabel = new Label
        {
            Text = infoText,
            Left = 4,
            Top = PicTop + ThumbnailSize + 24,
            Width = ThumbnailSize,
            Height = infoHeight,
            TextAlign = ContentAlignment.TopLeft,
            Font = infoFont,
            ForeColor = Color.FromArgb(100, 100, 100),
        };

        _toolTip.SetToolTip(pic, path);
        _toolTip.SetToolTip(nameLabel, path);

        pic.Click += (_, _) => SetGroupReference(groupId, path);
        simLabel.Click += (_, _) => SetGroupReference(groupId, path);
        nameLabel.Click += (_, _) => SetGroupReference(groupId, path);

        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => OpenExternal(path));
        menu.Items.Add("フォルダーを開く", null, (_, _) => OpenFolder(path));
        menu.Items.Add("パスをコピー", null, (_, _) =>
        {
            try { Clipboard.SetText(path); } catch { }
        });
        pic.ContextMenuStrip = menu;
        nameLabel.ContextMenuStrip = menu;

        wrap.Controls.Add(simLabel);
        wrap.Controls.Add(pic);
        wrap.Controls.Add(nameLabel);
        wrap.Controls.Add(infoLabel);
        return wrap;
    }

    private void SetGroupReference(int groupId, string referencePath)
    {
        int idx = _groups.FindIndex(g => g.Id == groupId);
        if (idx < 0) return;
        var newGroup = RecalculateGroup(_groups[idx], referencePath);
        _groups[idx] = newGroup;
        if (_groupFlows.TryGetValue(groupId, out var flow))
            RebuildGroupFlow(flow, newGroup);
    }

    private static ImageGroup RecalculateGroup(ImageGroup group, string referencePath)
    {
        ulong refHash = group.Hashes[referencePath];
        var newSimilarities = group.Hashes.ToDictionary(
            kv => kv.Key,
            kv => ImageHasher.Similarity(refHash, kv.Value));
        var newPaths = newSimilarities
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();
        return group with { Paths = newPaths, Similarities = newSimilarities };
    }

    private void RebuildGroupFlow(FlowLayoutPanel flow, ImageGroup group)
    {
        flow.SuspendLayout();
        foreach (Control c in flow.Controls)
        {
            if (c is Panel wrap)
                foreach (Control inner in wrap.Controls)
                    if (inner is PictureBox pb && pb.Image is Bitmap bmp)
                        _allocatedThumbnails.Remove(bmp);
            c.Dispose();
        }
        flow.Controls.Clear();
        foreach (var path in group.Paths)
            flow.Controls.Add(CreateThumbnail(path, group.Similarities[path], group.Id));
        flow.ResumeLayout(true);
    }

    private string GetRelativePath(string filePath)
    {
        foreach (var folder in _folders)
        {
            if (filePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                return filePath.Substring(folder.Length).TrimStart(Path.DirectorySeparatorChar);
        }
        return filePath;
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F0} KB",
        _ => $"{bytes} B",
    };

    private static (Bitmap? Bmp, Size OriginalSize) TryLoadThumbnail(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var src = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);

            var originalSize = new Size(src.Width, src.Height);

            int w = ThumbnailSize, h = ThumbnailSize;
            double ratio = (double)src.Width / src.Height;
            if (ratio > 1) h = (int)(ThumbnailSize / ratio);
            else w = (int)(ThumbnailSize * ratio);
            if (w < 1) w = 1;
            if (h < 1) h = 1;

            var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
            return (bmp, originalSize);
        }
        catch
        {
            return (null, Size.Empty);
        }
    }

    private static void OpenExternal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        ClearResults();
        base.OnFormClosing(e);
    }
}

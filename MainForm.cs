using System.Diagnostics;

namespace Nimono;

public class MainForm : Form
{
    private const int ThumbnailSize = 140;

    // メインビューに表示するグループあたりのサムネイル最大数。
    // これを超えるグループは先頭 N 枚のみ描画し、残りは「比較」画面で確認する。
    // 巨大グループ（数千〜数万枚）をそのまま描画すると 1 サムネイルあたり ~5 USER ハンドル
    // 必要なため、Win32 のプロセスあたりハンドル上限 (10,000) を即超えて
    // Win32Exception 1158（ウィンドウのハンドル作成エラー）が発生する。
    private const int MaxThumbnailsPerGroup = 50;

    private readonly List<string> _folders = new();
    private readonly List<Bitmap> _allocatedThumbnails = new();
    private readonly List<ImageGroup> _groups = new();
    private readonly Dictionary<int, FlowLayoutPanel> _groupFlows = new();
    private readonly Dictionary<int, Panel> _groupPanels = new();
    private readonly Dictionary<int, int> _groupTops = new();
    private readonly Dictionary<int, int> _groupHeightCache = new();
    private readonly HashSet<int> _renderedGroupIds = new();
    private CancellationTokenSource? _cts;
    private AppSettings _settings = null!;

    private Button _selectFoldersButton = null!;
    private Button _scanButton = null!;
    private Button _clearCacheButton = null!;
    private Button _cancelButton = null!;
    private NumericUpDown _thresholdInput = null!;
    private ComboBox _methodCombo = null!;
    private Label _statusLabel = null!;
    private ProgressBar _progressBar = null!;
    private VirtualScrollPanel _resultsPanel = null!;
    private ToolTip _toolTip = null!;
    private DINOv2Embedder? _embedder;

    public MainForm()
    {
        InitializeComponents();
        _settings = SettingsStorage.Load();

        // ── ウィンドウサイズと最大化状態は最優先で適用する ──
        // この後の設定反映（_methodCombo.SelectedItem の代入による SelectedIndexChanged や
        // bundledModel 検出のための SaveSettings 等）の中で SaveSettings が呼ばれると、
        // その時点の WindowState が Normal のまま _settings.MainWindowMaximized=false で
        // 上書き保存されてしまう。それを防ぐため、ここで先に WindowState を確定させる。
        Size = new Size(_settings.WindowWidth, _settings.WindowHeight);
        if (_settings.MainWindowMaximized)
            WindowState = FormWindowState.Maximized;

        _folders.AddRange(_settings.SearchFolders.Where(Directory.Exists));
        _thresholdInput.Value = Math.Clamp(_settings.SimilarityThreshold, 50, 100);
        _thresholdInput.ValueChanged += (_, _) => SaveSettings();
        // 実行ファイルと同じフォルダーの model.onnx を自動検出
        var bundledModel = Path.Combine(AppContext.BaseDirectory, "model.onnx");
        if (File.Exists(bundledModel) &&
            (string.IsNullOrEmpty(_settings.DINOv2ModelPath) || !File.Exists(_settings.DINOv2ModelPath)))
        {
            _settings.DINOv2ModelPath = bundledModel;
            SaveSettings();
        }
        _methodCombo.SelectedItem = _settings.SimilarityMethod switch
        {
            "DINOv2_DML" or "DINOv2_CUDA" => "DINOv2 GPU (DirectML)",
            "DINOv2"                       => "DINOv2 CPU",
            _                              => "pHash（高速）",
        };
        UpdateClearCacheButton();
        ResizeEnd += (_, _) => { SaveSettings(); RecalculateAllPanelPositions(); };
        var prevState = WindowState;
        SizeChanged += (_, _) =>
        {
            if (WindowState == prevState) return;
            prevState = WindowState;
            SaveSettings(); // 最大化状態を永続化
            RecalculateAllPanelPositions();
        };
        FormClosed += (_, _) => { _embedder?.Dispose(); _cts?.Cancel(); };
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
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (exeIcon is not null)
            Icon = exeIcon;

        _toolTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 400 };

        var toolPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            // 最小化されてもツールバーの高さが 0 まで縮まないようにする。
            // これを設定しないと、最小化→復元の遷移で Height=0 のまま戻らず非表示になる。
            MinimumSize = new Size(0, 44),
            Padding = new Padding(8, 6, 8, 6),
            BackColor = SystemColors.Control,
        };

        // ── ツールバー: 左から右に進めて X 座標を計算する ──
        // WinForms の Panel は絶対配置の子コントロールに対しては Padding を反映しないため、
        // 明示的な LeftMargin で左端の余白を確保する。
        const int LeftMargin = 8;
        const int ControlGap = 8;
        const int GroupGap = 16; // 機能グループ間はやや広めにスペースを取る

        _selectFoldersButton = new Button
        {
            Text = "フォルダー選択...",
            Width = 140,
            Top = 4,
            Height = 28,
        };
        _selectFoldersButton.Click += SelectFolders_Click;

        _scanButton = new Button
        {
            Text = "スキャン開始",
            Width = 110,
            Top = 4,
            Height = 28,
            Enabled = false,
        };
        _scanButton.Click += Scan_Click;

        var methodLabel = new Label
        {
            Text = "計算方式:",
            Top = 9,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _methodCombo = new ComboBox
        {
            Top = 6,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _methodCombo.Items.AddRange(["pHash（高速）", "DINOv2 CPU", "DINOv2 GPU (DirectML)"]);
        _methodCombo.SelectedIndex = 0;
        _methodCombo.SelectedIndexChanged += (_, _) =>
        {
            string newMethod = _methodCombo.SelectedIndex switch
            {
                1 => "DINOv2",
                2 => "DINOv2_DML",
                _ => "pHash",
            };
            bool epChanged = _settings.SimilarityMethod != newMethod;
            _settings.SimilarityMethod = newMethod;
            if (epChanged) { _embedder?.Dispose(); _embedder = null; }
            SaveSettings();
        };
        // 最も長い項目に合わせてドロップダウン幅を決める。
        // 注意: コントロールが親に追加される前は Font プロパティがデフォルト
        // (Microsoft Sans Serif 8.25pt) を返してしまうため、明示的にフォーム自身の Font
        // (Segoe UI 9.5pt) で計測する。これを怠るとデフォルトフォントが幅狭のため
        // 計測値が実描画より小さくなり、コンボの文字がクリップされる。
        // 余白 = ドロップダウン矢印(SystemInformation.VerticalScrollBarWidth) + 内側 padding(14)
        int maxComboTextWidth = 0;
        foreach (string item in _methodCombo.Items)
            maxComboTextWidth = Math.Max(maxComboTextWidth,
                TextRenderer.MeasureText(item, this.Font).Width);
        _methodCombo.Width = maxComboTextWidth + SystemInformation.VerticalScrollBarWidth + 14;

        var thresholdLabel = new Label
        {
            Text = "類似度しきい値:",
            Top = 9,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _thresholdInput = new NumericUpDown
        {
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
            Top = 9,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _clearCacheButton = new Button
        {
            Text = "キャッシュクリア",
            Width = 200,
            Top = 4,
            Height = 28,
            Enabled = false,
        };
        _clearCacheButton.Click += (_, _) =>
        {
            var dr = MessageBox.Show(this, "キャッシュをクリアしますか？\n（次回のスキャンに時間がかかるようになります）",
                "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dr == DialogResult.Yes)
            {
                CacheManager.ClearCache();
                UpdateClearCacheButton();
            }
        };

        _cancelButton = new Button
        {
            Text = "キャンセル",
            Width = 100,
            Dock = DockStyle.Right,
            Enabled = false,
        };
        _cancelButton.Click += (_, _) => _cts?.Cancel();

        // ── 左から右に Left を進めて配置 ──
        // ラベル幅も親追加前の Font (デフォルト = Microsoft Sans Serif 8.25pt) ではなく
        // フォーム自身の Font (Segoe UI 9.5pt) で計測する必要がある。
        int x = LeftMargin;
        _selectFoldersButton.Left = x; x = _selectFoldersButton.Right + ControlGap;
        _scanButton.Left          = x; x = _scanButton.Right + GroupGap;
        methodLabel.Left          = x; x += TextRenderer.MeasureText(methodLabel.Text, this.Font).Width + ControlGap;
        _methodCombo.Left         = x; x = _methodCombo.Right + GroupGap;
        thresholdLabel.Left       = x; x += TextRenderer.MeasureText(thresholdLabel.Text, this.Font).Width + ControlGap;
        _thresholdInput.Left      = x; x = _thresholdInput.Right + 4;
        pctLabel.Left             = x; x += TextRenderer.MeasureText(pctLabel.Text, this.Font).Width + GroupGap;
        _clearCacheButton.Left    = x;

        toolPanel.Controls.Add(_selectFoldersButton);
        toolPanel.Controls.Add(_scanButton);
        toolPanel.Controls.Add(methodLabel);
        toolPanel.Controls.Add(_methodCombo);
        toolPanel.Controls.Add(thresholdLabel);
        toolPanel.Controls.Add(_thresholdInput);
        toolPanel.Controls.Add(pctLabel);
        toolPanel.Controls.Add(_clearCacheButton);
        toolPanel.Controls.Add(_cancelButton);

        // ── 全コントロールが折り返さず収まる最小フォーム幅を計算 ──
        // 必要なパネル幅 = clearCache の右端 + 余白 + cancel ボタン(Dock=Right) + パネルの右パディング
        int requiredPanelWidth = _clearCacheButton.Right + ControlGap + _cancelButton.Width + toolPanel.Padding.Right;
        int nonClientHorizontal = Width - ClientSize.Width;
        if (nonClientHorizontal <= 0)
            nonClientHorizontal = SystemInformation.HorizontalResizeBorderThickness * 2;
        MinimumSize = new Size(requiredPanelWidth + nonClientHorizontal, 500);

        var statusPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            // 最小化時の高さ崩壊を防ぐ（toolPanel と同じ理由）
            MinimumSize = new Size(0, 26),
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

        _resultsPanel = new VirtualScrollPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.FromArgb(245, 245, 248),
            Padding = new Padding(8),
        };
        _resultsPanel.Scrolled += (_, _) => UpdateVisibleGroups();

        // WinForms の Dock は逆 Z オーダー（後から Add したものが先に処理される）で
        // 計算されるため、Fill→Bottom→Top の順で Add する必要がある。
        // 逆順で Add すると Fill が全クライアント領域を占有してしまい、Top の
        // ツールバーがその上に被さって _resultsPanel の先頭が隠れる。
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
        _settings.MainWindowMaximized = WindowState == FormWindowState.Maximized;

        // 最大化中は通常サイズを上書きしない（最大化を解除したときに復元するため）
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }

        SettingsStorage.Save(_settings);
    }


    private static void Log(string msg) => ScanLogger.Log(msg);

    private async void Scan_Click(object? sender, EventArgs e)
    {
        if (_folders.Count == 0) return;

        bool isDINOv2 = _settings.SimilarityMethod is "DINOv2" or "DINOv2_DML" or "DINOv2_CUDA";
        bool useGpu   = _settings.SimilarityMethod is "DINOv2_DML" or "DINOv2_CUDA";

        // DINOv2 の場合はモデルが必要
        if (isDINOv2)
        {
            if (string.IsNullOrEmpty(_settings.DINOv2ModelPath) || !File.Exists(_settings.DINOv2ModelPath))
            {
                MessageBox.Show(this,
                    "DINOv2 モデルファイルが設定されていません。\n" +
                    "「参照...」ボタンでモデル（.onnx）を選択してください。\n\n" +
                    "モデルは HuggingFace の onnx-community/dinov2-small などから入手できます。",
                    "DINOv2 モデル未設定", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_embedder is null)
            {
                if (!DINOv2Embedder.TryCreate(_settings.DINOv2ModelPath, useGpu, out var emb, out string err))
                {
                    string body = useGpu
                        ? $"DirectML (GPU) の初期化に失敗しました。スキャンを中止します。\n\n" +
                          $"GPU を使用するには、DirectX 12 対応の GPU と最新のグラフィックドライバーが必要です。\n" +
                          $"CPU で実行するには、計算方式を「DINOv2 CPU」に切り替えてください。\n\n{err}"
                        : $"モデルの読み込みに失敗しました。\n\n{err}";
                    MessageBox.Show(this, body, "DINOv2 エラー",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _embedder = emb;
            }
        }

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
            ScanLogger.Reset($"スキャン開始 mode={(isDINOv2 ? (useGpu ? "DINOv2_DML" : "DINOv2") : "pHash")}");
            _statusLabel.Text = "ファイルを列挙中...";
            _progressBar.Style = ProgressBarStyle.Marquee;
            var files = await Task.Run(
                () => ImageGrouper.EnumerateImages(folders, token), token);

            token.ThrowIfCancellationRequested();

            if (files.Count == 0)
            {
                _statusLabel.Text = "画像ファイルが見つかりませんでした";
                return;
            }

            Log($"ファイル列挙完了: {files.Count}件");

            _statusLabel.Text = "キャッシュを読み込み中...";
            Log("キャッシュ読み込み開始");
            await CacheManager.LoadCacheAsync(files);
            Log("キャッシュ読み込み完了");

            // 2) 特徴量計算
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;

            IReadOnlyList<ImageGroup> groups;
            IReadOnlyList<string> newPaths;
            if (isDINOv2 && _embedder is not null)
            {
                Log("DINOv2埋め込み計算開始");
                var (embeddings, embNewPaths) = await Task.Run(
                    () => ImageGrouper.ComputeEmbeddings(files, _embedder, progress, token), token);
                newPaths = embNewPaths;
                Log($"DINOv2埋め込み計算完了: {embeddings.Count}件");

                token.ThrowIfCancellationRequested();

                Log("DINOv2グルーピング開始");
                groups = await Task.Run(
                    () => ImageGrouper.GroupByEmbedding(embeddings, threshold, progress, token), token);
                Log($"DINOv2グルーピング完了: {groups.Count}グループ");
            }
            else
            {
                Log("pHashハッシュ計算開始");
                var (entries, hashNewPaths) = await Task.Run(
                    () => ImageGrouper.ComputeHashes(files, progress, token), token);
                newPaths = hashNewPaths;
                Log($"pHashハッシュ計算完了: {entries.Count}件");

                token.ThrowIfCancellationRequested();

                Log("pHashグルーピング開始");
                groups = await Task.Run(
                    () => ImageGrouper.Group(entries, threshold, progress, token), token);
                Log($"pHashグルーピング完了: {groups.Count}グループ");
            }

            token.ThrowIfCancellationRequested();

            Log("キャッシュ保存開始");
            _statusLabel.Text = "キャッシュを保存中...";
            await CacheManager.SaveCacheAsync(newPaths);
            Log("キャッシュ保存完了");

            // 4) 表示
            Log("結果表示開始");
            RenderGroups(groups);
            Log("結果表示完了");

            int dupes = groups.Sum(g => g.Paths.Count);
            _statusLabel.Text = groups.Count == 0
                ? $"似た画像は見つかりませんでした（{files.Count:N0} 件をスキャン）"
                : $"{groups.Count:N0} グループ / {dupes:N0} 枚（{files.Count:N0} 件をスキャン）";
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "キャンセルされました";
            Log("キャンセル");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"エラー: {ex.Message}";
            Log($"例外: {ex}");
        }
        finally
        {
            SetScanningState(false);
        }
    }

    private void OnProgress(GroupingProgress p)
    {
        // Progress<T> は UI thread に Post で配信されるため、
        // cancel が catch ブロックで処理された後にも遅延到着する可能性がある。
        // その場合 "キャンセルされました" を上書きしないよう、ここで弾く。
        if (_cts is null || _cts.IsCancellationRequested) return;
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
        _methodCombo.Enabled = !scanning;
        _cancelButton.Enabled = scanning;
        if (scanning)
        {
            _clearCacheButton.Enabled = false;
        }
        else
        {
            UpdateClearCacheButton();
        }
        if (!scanning)
        {
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
        }
    }

    private void ClearResults()
    {
        _resultsPanel.SuspendLayout();
        _resultsPanel.Controls.Clear();
        foreach (var p in _groupPanels.Values)
            p.Dispose();
        foreach (var bmp in _allocatedThumbnails)
            bmp.Dispose();
        _allocatedThumbnails.Clear();
        _groups.Clear();
        _groupFlows.Clear();
        _groupPanels.Clear();
        _groupTops.Clear();
        _groupHeightCache.Clear();
        _renderedGroupIds.Clear();
        _resultsPanel.AutoScrollMinSize = Size.Empty;
        _resultsPanel.AutoScrollPosition = new Point(0, 0);
        _resultsPanel.ResumeLayout();
    }

    private void RenderGroups(IReadOnlyList<ImageGroup> groups)
    {
        _groups.AddRange(groups);
        int panelWidth = _resultsPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
        if (panelWidth < 100) panelWidth = 100;
        // パネルは生成せず、高さを推定してスクロール領域だけ確保する
        // 実際のパネル生成は UpdateVisibleGroups で可視域に入った時に行う
        int top = 0;
        foreach (var group in groups)
        {
            int height = EstimateGroupHeight(group.Paths.Count, panelWidth);
            _groupHeightCache[group.Id] = height;
            _groupTops[group.Id] = top;
            top += height + 8;
        }
        _resultsPanel.AutoScrollMinSize = new Size(0, top + _resultsPanel.Padding.Bottom);
        UpdateVisibleGroups();
    }

    /// <summary>
    /// サムネイルの個数とパネル幅からグループパネルの高さを推定する。
    /// 実際にパネルを構築しないため高速だが、あくまで推定値。
    /// </summary>
    private static int EstimateGroupHeight(int pathCount, int panelWidth)
    {
        // BuildGroupPanel と同じ上限を適用（超過分は「...他 N 枚」ラベル 1 行で表現）
        int displayCount = Math.Min(pathCount, MaxThumbnailsPerGroup);
        int truncatedExtra = pathCount > displayCount ? 28 : 0;

        const int headerHeight = 28;
        const int wrapOuterWidth = ThumbnailSize + 8 + 8;  // wrap幅 + 左右マージン
        const int wrapOuterHeight = 250;                    // 推定wrap高さ
        const int flowPadding = 12;                         // FlowLayoutPanel上下パディング
        int flowInnerWidth = Math.Max(1, panelWidth - 2 - flowPadding);
        int itemsPerRow = Math.Max(1, flowInnerWidth / wrapOuterWidth);
        int rows = (displayCount + itemsPerRow - 1) / itemsPerRow;
        return headerHeight + rows * wrapOuterHeight + flowPadding + 2 + truncatedExtra;
    }

    /// <summary>
    /// 指定グループのパネルを破棄し、サムネイルのメモリを解放する。
    /// 実測高さを _groupHeightCache に保存してから破棄する。
    /// </summary>
    private void DisposeGroupPanel(int groupId)
    {
        if (_groupFlows.TryGetValue(groupId, out var flow))
            foreach (Control c in flow.Controls)
                if (c is Panel wrap)
                    foreach (Control inner in wrap.Controls)
                        if (inner is PictureBox pb && pb.Image is Bitmap bmp)
                        {
                            _allocatedThumbnails.Remove(bmp);
                            bmp.Dispose();
                        }
        if (_groupPanels.TryGetValue(groupId, out var panel))
        {
            _groupHeightCache[groupId] = panel.Height; // 実測高さを保存
            _resultsPanel.Controls.Remove(panel);
            panel.Dispose();
            _renderedGroupIds.Remove(groupId);
        }
        _groupPanels.Remove(groupId);
        _groupFlows.Remove(groupId);
    }

    private void SetGroupPanelHeight(Panel panel, int groupId)
    {
        var flow = _groupFlows[groupId];
        int flowWidth = Math.Max(1, panel.Width - 2); // FixedSingle border = 1px each side
        int flowHeight = flow.GetPreferredSize(new Size(flowWidth, int.MaxValue)).Height;
        panel.Height = 28 + flowHeight + 2; // 28 = header panel height
    }

    private void RecalculateAllPanelPositions()
    {
        if (_groups.Count == 0) return;
        int savedScrollY = Math.Abs(_resultsPanel.AutoScrollPosition.Y);
        int panelWidth = _resultsPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
        if (panelWidth < 100) panelWidth = 100;
        int top = 0;
        foreach (var g in _groups)
        {
            int height;
            if (_groupPanels.TryGetValue(g.Id, out var panel))
            {
                panel.Width = panelWidth;
                SetGroupPanelHeight(panel, g.Id);
                height = panel.Height;
            }
            else
            {
                // 未構築パネルは推定高さを使用
                height = EstimateGroupHeight(g.Paths.Count, panelWidth);
                _groupHeightCache[g.Id] = height;
            }
            _groupTops[g.Id] = top;
            top += height + 8;
        }
        int totalH = top + _resultsPanel.Padding.Bottom;

        _resultsPanel.SuspendLayout();
        _resultsPanel.AutoScroll = false;
        _resultsPanel.AutoScrollPosition = new Point(0, 0);

        foreach (var id in _renderedGroupIds)
        {
            if (_groupPanels.TryGetValue(id, out var p) && _groupTops.TryGetValue(id, out int absTop))
                p.Top = absTop;
        }

        _resultsPanel.AutoScrollMinSize = new Size(0, totalH);
        _resultsPanel.AutoScroll = true;

        int maxScroll = Math.Max(0, totalH - _resultsPanel.ClientSize.Height);
        int newScrollY = Math.Min(savedScrollY, maxScroll);
        _resultsPanel.AutoScrollPosition = new Point(0, Math.Max(0, newScrollY));

        _resultsPanel.ResumeLayout(true);
        UpdateVisibleGroups();
    }

    private void UpdateVisibleGroups()
    {
        if (_groups.Count == 0) return;
        int scrollY = -_resultsPanel.AutoScrollPosition.Y;
        int viewportH = _resultsPanel.ClientSize.Height;
        int buffer = Math.Max(viewportH, 300);
        int disposeBuffer = buffer * 3; // この範囲外のパネルはメモリ解放
        int panelWidth = _resultsPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
        if (panelWidth < 100) panelWidth = 100;
        var toAdd = new List<Panel>();
        var toRemove = new List<int>();
        var toDispose = new List<int>();
        bool needRecalc = false;

        _resultsPanel.SuspendLayout();

        foreach (var g in _groups)
        {
            if (!_groupTops.TryGetValue(g.Id, out int absTop)) continue;

            // パネルが構築済みなら実測高さ、未構築なら推定高さを使用
            int height = _groupPanels.TryGetValue(g.Id, out var panel)
                ? panel.Height
                : (_groupHeightCache.TryGetValue(g.Id, out var ch) ? ch : 250);
            int absBottom = absTop + height;
            bool inView = absBottom > scrollY - buffer && absTop < scrollY + viewportH + buffer;
            bool rendered = _renderedGroupIds.Contains(g.Id);
            bool built = panel != null;

            if (inView)
            {
                if (!built)
                {
                    // 可視域に入ったので初めてパネルを構築する
                    panel = BuildGroupPanel(g);
                    panel.Width = panelWidth;
                    SetGroupPanelHeight(panel, g.Id);
                    // 実測高さが推定と異なる場合、後で位置を再計算
                    if (panel.Height != height)
                    {
                        _groupHeightCache[g.Id] = panel.Height;
                        needRecalc = true;
                    }
                    panel.Top = absTop - scrollY;
                    panel.Left = 0;
                    toAdd.Add(panel);
                    _renderedGroupIds.Add(g.Id);
                }
                else if (!rendered)
                {
                    panel!.Top = absTop - scrollY;
                    panel.Left = 0;
                    panel.Width = panelWidth;
                    toAdd.Add(panel);
                    _renderedGroupIds.Add(g.Id);
                }
                else
                {
                    panel!.Top = absTop - scrollY;
                }
            }
            else
            {
                if (rendered)
                {
                    toRemove.Add(g.Id);
                }
                // 可視域から大きく離れたパネルはメモリ解放
                if (built && (absBottom < scrollY - disposeBuffer || absTop > scrollY + viewportH + disposeBuffer))
                {
                    toDispose.Add(g.Id);
                }
            }
        }

        // Controls の追加・削除
        foreach (var id in toRemove)
        {
            if (_groupPanels.TryGetValue(id, out var p))
                _resultsPanel.Controls.Remove(p);
            _renderedGroupIds.Remove(id);
        }
        foreach (var p in toAdd)
            _resultsPanel.Controls.Add(p);

        // 実測高さと推定高さが異なった場合、全体の位置を再計算し表示中パネルを再配置
        // (レイアウト再開前に行うことで描画の破綻や座標ズレを防ぐ)
        if (needRecalc)
        {
            RecalculateGroupPositions();
            int scrollY2 = -_resultsPanel.AutoScrollPosition.Y;
            foreach (var id in _renderedGroupIds)
            {
                if (_groupPanels.TryGetValue(id, out var rp) && _groupTops.TryGetValue(id, out int newTop))
                    rp.Top = newTop - scrollY2;
            }
        }

        _resultsPanel.ResumeLayout(true);

        // 遠くなったパネルを破棄してメモリを解放
        foreach (var id in toDispose)
            DisposeGroupPanel(id);
    }

    /// <summary>
    /// グループの top 座標と AutoScrollMinSize を再計算する（パネルの再配置なし）。
    /// </summary>
    private void RecalculateGroupPositions()
    {
        int panelWidth = _resultsPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
        if (panelWidth < 100) panelWidth = 100;
        int top = 0;
        foreach (var g in _groups)
        {
            _groupTops[g.Id] = top;
            int height = _groupPanels.TryGetValue(g.Id, out var panel)
                ? panel.Height
                : (_groupHeightCache.TryGetValue(g.Id, out var ch) ? ch
                    : EstimateGroupHeight(g.Paths.Count, panelWidth));
            top += height + 8;
        }
        _resultsPanel.AutoScrollMinSize = new Size(0, top + _resultsPanel.Padding.Bottom);
    }

    private Panel BuildGroupPanel(ImageGroup group)
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 28,
            BackColor = Color.FromArgb(60, 90, 140),
        };
        var headerLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = $"グループ {group.Id} — {group.Paths.Count} 枚",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };
        var compareButton = new Button
        {
            Text = "比較",
            Dock = DockStyle.Right,
            Width = 56,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(45, 75, 125),
            Font = new Font("Segoe UI", 8.5f),
            Cursor = Cursors.Hand,
        };
        compareButton.FlatAppearance.BorderColor = Color.FromArgb(80, 110, 160);
        compareButton.FlatAppearance.BorderSize = 0;
        compareButton.Click += (_, _) =>
        {
            var current = _groups.FirstOrDefault(g => g.Id == group.Id) ?? group;
            using var f = new CompareForm(current, _settings);
            f.ShowDialog(this);
            SyncGroupFromCompareForm(f, group.Id);
        };
        header.Controls.Add(headerLabel);
        header.Controls.Add(compareButton);

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(6),
            BackColor = Color.White,
        };

        _groupFlows[group.Id] = flow;

        // 大量サムネイルでハンドル枯渇しないよう上限を設ける。
        int totalCount = group.Paths.Count;
        int displayCount = Math.Min(totalCount, MaxThumbnailsPerGroup);
        for (int i = 0; i < displayCount; i++)
        {
            var path = group.Paths[i];
            var thumb = CreateThumbnail(path, group.Similarities[path], group.Id);
            flow.Controls.Add(thumb);
        }
        if (totalCount > displayCount)
        {
            var moreLabel = new Label
            {
                Text = $"...他 {totalCount - displayCount} 枚（「比較」ボタンで全件表示）",
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font("Segoe UI", 9f, FontStyle.Italic),
                Margin = new Padding(8, 12, 8, 8),
            };
            flow.Controls.Add(moreLabel);
        }

        var container = new Panel
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Padding = new Padding(0),
        };
        container.Controls.Add(flow);
        container.Controls.Add(header);
        _groupPanels[group.Id] = container;
        return container;
    }

    private Control CreateThumbnail(string path, double similarity, int groupId)
    {
        var (bmp, originalSize) = TryLoadThumbnail(path);

        // 保存済みの回転を適用
        if (bmp != null && _settings.ThumbnailRotations.TryGetValue(path, out int savedRot) && savedRot > 0)
        {
            var rotType = savedRot switch
            {
                1 => RotateFlipType.Rotate90FlipNone,
                2 => RotateFlipType.Rotate180FlipNone,
                _ => RotateFlipType.Rotate270FlipNone,
            };
            bmp.RotateFlip(rotType);
        }

        var fi = new FileInfo(path);
        string formatText = fi.Extension.TrimStart('.').ToUpperInvariant();
        string sizeText = FormatFileSize(fi.Length);
        string resText = originalSize != Size.Empty
            ? $"{originalSize.Width}×{originalSize.Height}"
            : "—";
        string dateText = fi.LastWriteTime.ToString("yyyy/MM/dd");
        // ファイル名を除いたディレクトリパスのみ表示（比較画面と統一）
        string dirPath = Path.GetDirectoryName(path) ?? path;

        const int SimLabelHeight = 16;
        const int PicTop = 4 + SimLabelHeight;

        var infoFont = new Font("Segoe UI", 7.5f);
        string infoText = $"{formatText} · {sizeText}\n{resText}\n{dateText}\n{dirPath}";
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

        // similarity == 1.0 は基準画像（グループ内で最も類似度の高い、または明示的に
        // 基準として選ばれた画像）。CompareForm の表記と合わせて「【基準】」と表示する。
        bool isReference = similarity >= 0.9999;
        var simLabel = new Label
        {
            Text = isReference ? "【基準】" : $"類似度: {similarity:P0}",
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
        menu.Items.Add("回転", null, (_, _) =>
        {
            if (pic.Image is not Bitmap bmpCurrent) return;
            var rotated = (Bitmap)bmpCurrent.Clone();
            rotated.RotateFlip(RotateFlipType.Rotate90FlipNone);
            _allocatedThumbnails.Remove(bmpCurrent);
            bmpCurrent.Dispose();
            pic.Image = rotated;
            _allocatedThumbnails.Add(rotated);

            _settings.ThumbnailRotations.TryGetValue(path, out int prevRot);
            int newRot = (prevRot + 1) % 4;
            if (newRot == 0)
                _settings.ThumbnailRotations.Remove(path);
            else
                _settings.ThumbnailRotations[path] = newRot;
            SaveSettings();
        });
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
        Dictionary<string, double> newSimilarities;

        if (group.Embeddings is { Count: > 0 } embs && embs.ContainsKey(referencePath))
        {
            // DINOv2: コサイン類似度で再計算
            var refEmb = embs[referencePath];
            newSimilarities = embs.ToDictionary(
                kv => kv.Key,
                kv => (double)DINOv2Embedder.CosineSimilarity(refEmb, kv.Value));
        }
        else if (group.Hashes.TryGetValue(referencePath, out ulong refHash))
        {
            // pHash: ハミング距離で再計算
            newSimilarities = group.Hashes.ToDictionary(
                kv => kv.Key,
                kv => ImageHasher.Similarity(refHash, kv.Value));
        }
        else return group;

        // referencePath を必ず先頭に置く（同一スコアの画像との不安定ソートを防ぐ）
        var newPaths = newSimilarities
            .Where(kv => kv.Key != referencePath)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .Prepend(referencePath)
            .ToList();
        return group with { Paths = newPaths, Similarities = newSimilarities };
    }

    // 比較画面の結果（基準変更・ファイル削除）を MainForm に反映する
    private void SyncGroupFromCompareForm(CompareForm f, int originalGroupId)
    {
        var updated = f.CurrentGroup;
        int groupIdx = _groups.FindIndex(g => g.Id == originalGroupId);
        if (groupIdx < 0) return;

        var original = _groups[groupIdx];
        if (updated.Paths.SequenceEqual(original.Paths) && f.DeletedPaths.Count == 0) return;

        if (updated.Paths.Count < 2)
        {
            // グループ消滅：パネルだけ除去してトップ座標を再計算
            RemoveGroupAt(groupIdx);
        }
        else
        {
            // 基準変更 or ファイル削除（グループ存続）→ RebuildGroupFlow で当該グループのみ差し替え
            _groups[groupIdx] = updated;
            if (_groupFlows.TryGetValue(updated.Id, out var flow))
                RebuildGroupFlow(flow, updated);
            int dupes = _groups.Sum(g => g.Paths.Count);
            _statusLabel.Text = $"{_groups.Count:N0} グループ / {dupes:N0} 枚";
        }
    }

    private void RemoveGroupAt(int groupIdx)
    {
        int savedScrollY = -_resultsPanel.AutoScrollPosition.Y;
        var groupId = _groups[groupIdx].Id;
        _groups.RemoveAt(groupIdx);

        // パネルが構築済みなら破棄
        DisposeGroupPanel(groupId);
        _groupTops.Remove(groupId);
        _groupHeightCache.Remove(groupId);

        // 残グループのトップ座標を先頭から再計算
        int panelWidth = _resultsPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
        if (panelWidth < 100) panelWidth = 100;
        int top = 0;
        foreach (var g in _groups)
        {
            _groupTops[g.Id] = top;
            int height = _groupPanels.TryGetValue(g.Id, out var p)
                ? p.Height
                : (_groupHeightCache.TryGetValue(g.Id, out var ch) ? ch
                    : EstimateGroupHeight(g.Paths.Count, panelWidth));
            top += height + 8;
        }
        int totalH = top + _resultsPanel.Padding.Bottom;

        _resultsPanel.SuspendLayout();
        _resultsPanel.AutoScroll = false;
        _resultsPanel.AutoScrollPosition = new Point(0, 0);

        foreach (var id in _renderedGroupIds)
        {
            if (_groupPanels.TryGetValue(id, out var p) && _groupTops.TryGetValue(id, out int absTop))
                p.Top = absTop;
        }

        _resultsPanel.AutoScrollMinSize = new Size(0, totalH);
        _resultsPanel.AutoScroll = true;

        int maxScroll = Math.Max(0, totalH - _resultsPanel.ClientSize.Height);
        int newScrollY = Math.Min(savedScrollY, maxScroll);
        _resultsPanel.AutoScrollPosition = new Point(0, Math.Max(0, newScrollY));

        _resultsPanel.ResumeLayout(true);

        UpdateVisibleGroups();

        if (_groups.Count > 0)
        {
            int dupes = _groups.Sum(g => g.Paths.Count);
            _statusLabel.Text = $"{_groups.Count:N0} グループ / {dupes:N0} 枚";
        }
        else
        {
            _statusLabel.Text = "似た画像は見つかりませんでした";
        }
    }

    private void RebuildGroupFlow(FlowLayoutPanel flow, ImageGroup group)
    {
        var savedScroll = _resultsPanel.AutoScrollPosition;
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

        if (_groupPanels.TryGetValue(group.Id, out var panel))
            SetGroupPanelHeight(panel, group.Id);

        // Recompute tops from this group onwards
        bool found = false;
        int top = 0;
        foreach (var g in _groups)
        {
            if (!found)
            {
                if (g.Id != group.Id) continue;
                found = true;
                top = _groupTops.TryGetValue(g.Id, out var t) ? t : 0;
            }
            _groupTops[g.Id] = top;
            if (_groupPanels.TryGetValue(g.Id, out var p)) top += p.Height + 8;
        }
        int totalH = top + _resultsPanel.Padding.Bottom;

        _resultsPanel.SuspendLayout();
        _resultsPanel.AutoScroll = false;
        _resultsPanel.AutoScrollPosition = new Point(0, 0);

        foreach (var id in _renderedGroupIds)
        {
            if (_groupPanels.TryGetValue(id, out var rp) && _groupTops.TryGetValue(id, out int absTop))
                rp.Top = absTop;
        }

        _resultsPanel.AutoScrollMinSize = new Size(0, totalH);
        _resultsPanel.AutoScroll = true;

        int maxScroll = Math.Max(0, totalH - _resultsPanel.ClientSize.Height);
        int newScrollY = Math.Min(Math.Abs(savedScroll.Y), maxScroll);
        _resultsPanel.AutoScrollPosition = new Point(0, Math.Max(0, newScrollY));

        _resultsPanel.ResumeLayout(true);
        UpdateVisibleGroups();
    }

    private void UpdateClearCacheButton()
    {
        bool hasCache = CacheManager.HasCacheData();
        _clearCacheButton.Enabled = hasCache;
        if (hasCache)
        {
            long sizeBytes = CacheManager.GetCacheSize();
            string sizeText = FormatFileSize(sizeBytes);
            _clearCacheButton.Text = $"キャッシュクリア ({sizeText})";
        }
        else
        {
            _clearCacheButton.Text = "キャッシュクリア";
        }
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
            using var src = Image.FromFile(path);

            var originalSize = new Size(src.Width, src.Height);

            int w = ThumbnailSize, h = ThumbnailSize;
            double ratio = (double)src.Width / src.Height;
            if (ratio > 1) h = (int)(ThumbnailSize / ratio);
            else w = (int)(ThumbnailSize * ratio);
            if (w < 1) w = 1;
            if (h < 1) h = 1;

            // GetThumbnailImage reads the embedded Exif thumbnail for JPEG (fast path);
            // falls back to full-image resize for PNG/BMP/etc.
            using var thumb = src.GetThumbnailImage(w, h, () => false, IntPtr.Zero);
            return (new Bitmap(thumb), originalSize);
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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_thresholdInput.ContainsFocus || _methodCombo.ContainsFocus)
            return base.ProcessCmdKey(ref msg, keyData);

        if (keyData is Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End)
        {
            int currentY = Math.Abs(_resultsPanel.AutoScrollPosition.Y);
            int maxScroll = Math.Max(0, _resultsPanel.AutoScrollMinSize.Height - _resultsPanel.ClientSize.Height);
            int pageStep = Math.Max(10, _resultsPanel.ClientSize.Height - 40);
            int newY = currentY;

            switch (keyData)
            {
                case Keys.Home:
                    newY = 0;
                    break;
                case Keys.End:
                    newY = maxScroll;
                    break;
                case Keys.PageUp:
                    newY = Math.Max(0, currentY - pageStep);
                    break;
                case Keys.PageDown:
                    newY = Math.Min(maxScroll, currentY + pageStep);
                    break;
            }

            if (newY != currentY)
            {
                _resultsPanel.AutoScrollPosition = new Point(0, newY);
                UpdateVisibleGroups();
                return true;
            }
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private sealed class VirtualScrollPanel : Panel
    {
        public event EventHandler? Scrolled;

        protected override Point ScrollToControl(Control activeControl)
        {
            // WinFormsがフォーカスされたコントロールに合わせて勝手にスクロールするのを防ぐ。
            // これにより、仮想スクロールでコントロールが追加・削除された際のガタつき（元の位置に戻る等）を完全に防止する。
            return DisplayRectangle.Location;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            const int WM_VSCROLL = 0x115;
            const int WM_MOUSEWHEEL = 0x20A;
            const int WM_KEYDOWN = 0x100;
            if (m.Msg == WM_VSCROLL || m.Msg == WM_MOUSEWHEEL || m.Msg == WM_KEYDOWN)
                Scrolled?.Invoke(this, EventArgs.Empty);
        }
    }
}

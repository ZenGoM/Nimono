using System.Diagnostics;

namespace Nimono;

internal sealed class CompareForm : Form
{
    private readonly ImageGroup _group;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, string> _sizeCache = new();
    private readonly Dictionary<string, string> _dateCache = new();

    private SplitContainer _mainSplitter = null!;
    private ZoomableImagePanel _leftView = null!;
    private RichTextBox _leftInfo = null!;
    private ZoomableImagePanel _rightView = null!;
    private RichTextBox _rightInfo = null!;
    private ListView _fileList = null!;
    private ToolTip _toolTip = null!;

    private Bitmap? _leftBitmap;
    private Bitmap? _rightBitmap;
    private bool _suppressEvent;
    private bool _syncingView;
    private bool _loading = true;
    private int _currentRightIndex;
    // 比較画面だけの表示上の回転回数（1=90°, 2=180°, 3=270°）。設定には保存せず、画像を差し替えても同じ向きで表示する
    private int _viewRotation;
    // 基準の向きにそろえるための回転回数（右回り 90° 単位）。向きが違うだけの画像を、基準と同じ向きで表示する
    private Dictionary<string, int> _alignRotations = new();
    private List<string> _paths = null!;
    private Dictionary<string, double> _similarities = null!;
    private readonly List<string> _deletedPaths = new();

    public IReadOnlyList<string> DeletedPaths => _deletedPaths;

    public ImageGroup CurrentGroup
    {
        get
        {
            var hashes = _group.Hashes.Count > 0
                ? (IReadOnlyDictionary<string, ulong>)_paths.ToDictionary(p => p, p => _group.Hashes[p])
                : new Dictionary<string, ulong>();
            var embeddings = _group.Embeddings is { Count: > 0 } embs
                ? (IReadOnlyDictionary<string, float[]>)_paths.ToDictionary(p => p, p => embs[p])
                : null;
            return new ImageGroup(
                _group.Id,
                _paths.ToList(),
                new Dictionary<string, double>(_similarities),
                hashes,
                embeddings);
        }
    }

    public CompareForm(ImageGroup group, AppSettings settings)
    {
        _group = group;
        _settings = settings;
        _paths = group.Paths.ToList();
        _similarities = group.Similarities.ToDictionary(kv => kv.Key, kv => kv.Value);
        CompareWithReference();
        InitializeComponents();
        RebuildList();
        LoadLeft();
        SetRightIndex(Math.Min(1, _paths.Count - 1));
    }

    private void InitializeComponents()
    {
        Text = $"比較 — グループ {_group.Id}（{_group.Paths.Count} 枚）";
        MinimumSize = new Size(600, 400);
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterParent;
        var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (exeIcon is not null)
            Icon = exeIcon;
        Size = new Size(
            Math.Max(600, _settings.CompareFormWidth),
            Math.Max(400, _settings.CompareFormHeight));
        // Size を設定した後で最大化状態を復元する
        if (_settings.CompareWindowMaximized)
            WindowState = FormWindowState.Maximized;

        _toolTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 400 };

        _mainSplitter = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            BackColor = Color.FromArgb(160, 160, 170),
        };

        var imageTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(160, 160, 170),
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        imageTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        imageTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        imageTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        (_leftView, _leftInfo) = BuildSide(imageTable, 0, () => ExecuteDelete(true), () => RotateView());
        (_rightView, _rightInfo) = BuildSide(imageTable, 1, () => ExecuteDelete(false), () => RotateView());

        // 左右の拡大・位置を同期
        _leftView.ViewChanged += (_, vs) =>
        {
            if (_syncingView) return;
            _syncingView = true;
            _rightView.SetView(vs.Zoom, vs.Pan);
            _syncingView = false;
        };
        _rightView.ViewChanged += (_, vs) =>
        {
            if (_syncingView) return;
            _syncingView = true;
            _leftView.SetView(vs.Zoom, vs.Pan);
            _syncingView = false;
        };

        _mainSplitter.Panel1.Controls.Add(imageTable);

        // ── 下部: ファイルリスト ──
        _fileList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            Font = new Font("Segoe UI", 9f),
        };
        _fileList.Columns.Add("類似度", 70);
        _fileList.Columns.Add("ファイル名", 200);
        _fileList.Columns.Add("形式", 55);
        _fileList.Columns.Add("サイズ", 80);
        _fileList.Columns.Add("解像度", 100);
        _fileList.Columns.Add("日付", 90);
        _fileList.Columns.Add("パス", 300);
        _fileList.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressEvent || _fileList.SelectedItems.Count == 0) return;
            int idx = (int)_fileList.SelectedItems[0].Tag!;
            if (idx == 0)
            {
                _suppressEvent = true;
                _fileList.Items[_currentRightIndex].Selected = true;
                _suppressEvent = false;
                return;
            }
            SetRightIndex(idx);
        };
        _fileList.DoubleClick += (_, _) =>
        {
            var pos = _fileList.PointToClient(Cursor.Position);
            var hit = _fileList.HitTest(pos).Item;
            if (hit == null) return;
            int idx = (int)hit.Tag!;
            if (idx == 0) return; // 基準行は無視
            SwapReference(idx);
        };

        var listMenu = new ContextMenuStrip();
        listMenu.Items.Add("開く", null, (_, _) =>
        {
            if (_fileList.SelectedItems.Count > 0)
                OpenExternal(_paths[(int)_fileList.SelectedItems[0].Tag!]);
        });
        listMenu.Items.Add("フォルダーを開く", null, (_, _) =>
        {
            if (_fileList.SelectedItems.Count > 0)
                OpenFolder(_paths[(int)_fileList.SelectedItems[0].Tag!]);
        });
        listMenu.Items.Add("パスをコピー", null, (_, _) =>
        {
            if (_fileList.SelectedItems.Count > 0)
                try { Clipboard.SetText(_paths[(int)_fileList.SelectedItems[0].Tag!]); } catch { }
        });
        _fileList.ContextMenuStrip = listMenu;
        _fileList.ColumnWidthChanged += (_, _) => PersistSettings();

        _mainSplitter.Panel2.Controls.Add(_fileList);
        Controls.Add(_mainSplitter);

        Load += (_, _) =>
        {
            // 列幅を先に取得（後続イベントで _settings が上書きされる前に）
            var savedWidths = _settings.CompareListColumnWidths.ToList();

            // _loading = true: Load 中の連鎖 PersistSettings を全て抑制
            _loading = true;

            // スプリッター位置を復元（SplitterMoved → PersistSettings は _loading で抑制）
            try
            {
                int dist = _settings.CompareSplitterDistance;
                int h = _mainSplitter.Height;
                _mainSplitter.SplitterDistance =
                    dist > 80 && dist < h - 60 ? dist : (int)(h * 0.65);
            }
            catch { }

            // 列幅は BeginInvoke で初期レイアウト完了後に復元し、その後 _loading を解除
            // （Load 直後のレイアウトイベントによる ColumnWidthChanged を避けるため）
            BeginInvoke(() =>
            {
                for (int i = 0; i < _fileList.Columns.Count && i < savedWidths.Count; i++)
                    _fileList.Columns[i].Width = savedWidths[i];
                _loading = false;
            });
        };
        ResizeEnd += (_, _) => { _leftView.ResetView(); PersistSettings(); };
        var prevState = WindowState;
        SizeChanged += (_, _) =>
        {
            if (WindowState == prevState) return;
            prevState = WindowState;
            _leftView.ResetView();
            PersistSettings(); // 最大化状態を永続化
        };
        _mainSplitter.SplitterMoved += (_, _) =>
        {
            _leftView.ResetView();
            PersistSettings();
        };
    }

    private (ZoomableImagePanel view, RichTextBox info) BuildSide(
        TableLayoutPanel table, int column, Action onDelete, Action onRotate)
    {
        bool isLeft = column == 0;
        var outer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(235, 235, 240),
            Margin = isLeft ? new Padding(0, 0, 1, 0) : new Padding(1, 0, 0, 0),
        };

        var roleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = isLeft ? "基準（固定）" : "比較対象",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = isLeft ? Color.FromArgb(60, 90, 140) : Color.FromArgb(60, 120, 60),
            ForeColor = Color.White,
        };

        var view = new ZoomableImagePanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(210, 210, 218),
        };

        // 情報ラベルコンテナ（削除ボタンをオーバーレイ）
        var infoPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 80,
            BackColor = Color.FromArgb(245, 245, 248),
        };

        var info = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 4, 8, 4),
            ReadOnly = true,
            BackColor = Color.FromArgb(245, 245, 248),
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.None,
            DetectUrls = false,
            WordWrap = false,
            Font = new Font("Segoe UI", 8.5f),
            Cursor = Cursors.Default,
            TabStop = false,
        };

        var deleteBtn = new Button
        {
            Text = "削除",
            Size = new Size(44, 22),
            Font = new Font("Segoe UI", 8f),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(190, 55, 55),
            ForeColor = Color.White,
            TabStop = false,
            Cursor = Cursors.Default,
        };
        deleteBtn.FlatAppearance.BorderSize = 0;
        deleteBtn.Click += (_, _) => onDelete();
        infoPanel.SizeChanged += (_, _) =>
            deleteBtn.Location = new Point(infoPanel.Width - deleteBtn.Width - 2, 2);

        infoPanel.Controls.Add(info);
        infoPanel.Controls.Add(deleteBtn);
        deleteBtn.BringToFront();

        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => OpenExternal(view.Tag as string));
        menu.Items.Add("回転", null, (_, _) => onRotate());
        menu.Items.Add("フォルダーを開く", null, (_, _) => OpenFolder(view.Tag as string));
        menu.Items.Add("パスをコピー", null, (_, _) =>
        {
            if (view.Tag is string p) try { Clipboard.SetText(p); } catch { }
        });
        view.ContextMenuStrip = menu;

        outer.Controls.Add(view);
        outer.Controls.Add(infoPanel);
        outer.Controls.Add(roleLabel);
        table.Controls.Add(outer, column, 0);
        return (view, info);
    }

    private void RebuildList()
    {
        _suppressEvent = true;
        _fileList.BeginUpdate();
        _fileList.Items.Clear();
        for (int i = 0; i < _paths.Count; i++)
        {
            var path = _paths[i];
            try
            {
                var fi = new FileInfo(path);
                double sim = _similarities.TryGetValue(path, out var s) ? s : 0.0;
                if (!_sizeCache.ContainsKey(path))
                    _sizeCache[path] = ReadImageSizeText(path);
                string sizeText = _sizeCache[path];

                string simText = i == 0 ? "【基準】" : $"{sim:P0}";
                var item = new ListViewItem(simText) { Tag = i };
                item.SubItems.Add(Path.GetFileName(path));
                item.SubItems.Add(fi.Extension.TrimStart('.').ToUpperInvariant());
                item.SubItems.Add(FormatFileSize(fi.Length));
                item.SubItems.Add(sizeText);
                item.SubItems.Add(GetDateText(path));
                item.SubItems.Add(Path.GetDirectoryName(path) ?? path);
                _fileList.Items.Add(item);
            }
            catch { }
        }
        _fileList.EndUpdate();
        _suppressEvent = false;
    }

    private void LoadLeft()
    {
        if (_paths.Count == 0) return;
        var path = _paths[0];
        _leftBitmap?.Dispose();
        _leftBitmap = LoadAndRotate(path);
        _leftView.Image = _leftBitmap;
        _leftView.Tag = path;
        _toolTip.SetToolTip(_leftView, path);
        SetInfoContent(_leftInfo, path, 0, null);
    }

    private void SetRightIndex(int index)
    {
        if (index < 0 || index >= _paths.Count) return;
        _suppressEvent = true;
        _fileList.Items[index].Selected = true;
        _fileList.Items[index].EnsureVisible();
        _suppressEvent = false;

        _currentRightIndex = index;
        var path = _paths[index];
        _rightBitmap?.Dispose();
        _rightBitmap = LoadAndRotate(path);
        _rightView.Image = _rightBitmap;
        _rightView.Tag = path;
        _toolTip.SetToolTip(_rightView, path);

        string leftPath = _paths[0];
        SetInfoContent(_leftInfo, leftPath, 0, path);
        SetInfoContent(_rightInfo, path, index, leftPath);
    }

    private void SetInfoContent(RichTextBox rtb, string path, int index, string? otherPath)
    {
        rtb.Clear();
        var fi = new FileInfo(path);
        double sim = _similarities.TryGetValue(path, out var s) ? s : 0.0;
        string res = _sizeCache.TryGetValue(path, out var r) ? r : ReadImageSizeText(path);
        string ext = fi.Extension.TrimStart('.').ToUpperInvariant();
        string fileName = Path.GetFileName(path);
        string sizeText = FormatFileSize(fi.Length);
        string dateText = GetDateText(path);

        FileInfo? otherFi = otherPath != null ? new FileInfo(otherPath) : null;
        string? otherExt = otherFi?.Extension.TrimStart('.').ToUpperInvariant();
        string? otherRes = otherPath != null
            ? (_sizeCache.TryGetValue(otherPath, out var or) ? or : ReadImageSizeText(otherPath))
            : null;
        string? otherDate = otherPath != null ? GetDateText(otherPath) : null;
        string? otherFileName = otherPath != null ? Path.GetFileName(otherPath) : null;

        // Line 1: ファイル名（文字単位diff）
        AppendCharDiff(rtb, fileName, otherFileName);
        AppendColored(rtb, "\n", false);
        // Line 2: 役割
        if (index == 0)
        {
            AppendColored(rtb, "【基準】\n", false);
        }
        else
        {
            AppendColored(rtb, $"類似度: {sim:P0}", sim < 0.9999);
            // 自動で回したことが分かるようにする（ファイルは変えていない）
            if (_alignRotations.TryGetValue(path, out int align) && align > 0)
                AppendColored(rtb, $"（基準の向きにそろえて右に {align * 90}° 回して表示）", false);
            AppendColored(rtb, "\n", false);
        }
        // Line 3: 形式 · サイズ  解像度  日付（フィールド単位diff）
        AppendColored(rtb, ext, otherExt != null && ext != otherExt);
        AppendColored(rtb, " · ", false);
        // バイト数ではなく表示文字列で比べる（見た目が同じ「3.9 MB」なのに赤字になるのを避ける）
        AppendColored(rtb, sizeText, otherFi != null && sizeText != FormatFileSize(otherFi.Length));
        AppendColored(rtb, "  ", false);
        AppendColored(rtb, res, otherRes != null && res != otherRes);
        AppendColored(rtb, "  ", false);
        AppendColored(rtb, dateText + "\n", otherDate != null && dateText != otherDate);
        // Line 4: ディレクトリパス（ファイル名を除く、文字単位diff）
        string dirPath = Path.GetDirectoryName(path) ?? path;
        string? otherDirPath = otherPath != null ? (Path.GetDirectoryName(otherPath) ?? otherPath) : null;
        AppendCharDiff(rtb, dirPath, otherDirPath);
    }

    /// <summary>
    /// 左右の画像を両方とも 90° 右回転する。どちらの「回転」から呼ばれても、左右の向きをそろえたまま比べられるようにする。
    /// 表示だけの回転で、設定（メイン画面のサムネイルの向き）には残さない。
    /// </summary>
    private void RotateView()
    {
        _viewRotation = (_viewRotation + 1) % 4;
        RotateSide(ref _leftBitmap, _leftView);
        RotateSide(ref _rightBitmap, _rightView);
    }

    private static void RotateSide(ref Bitmap? bmpRef, ZoomableImagePanel view)
    {
        if (bmpRef == null) return;

        var rotated = (Bitmap)bmpRef.Clone();
        rotated.RotateFlip(RotateFlipType.Rotate90FlipNone);
        bmpRef.Dispose();
        bmpRef = rotated;
        view.Image = rotated;
    }

    private void ExecuteDelete(bool isLeft)
    {
        string path = isLeft ? _paths[0] : _paths[_currentRightIndex];

        if (!new FileDeleter(this, _settings).Delete(path)) return;

        _deletedPaths.Add(path);
        _paths.Remove(path);
        _similarities.Remove(path);

        if (_paths.Count < 2) { Close(); return; }

        if (isLeft)
        {
            // 基準が削除されたので 2 番目を基準に再計算
            RebuildAfterReferenceChange();
        }
        else
        {
            // 比較対象が削除されたので次を選択
            int nextIndex = Math.Min(_currentRightIndex, _paths.Count - 1);
            if (nextIndex == 0) nextIndex = 1;
            RebuildList();
            SetRightIndex(nextIndex);
        }
    }

    private void RebuildAfterReferenceChange(string? preferredRightPath = null)
    {
        CompareWithReference();

        var rest = _paths.Skip(1).OrderByDescending(p => _similarities[p]).ToList();
        _paths = new List<string> { _paths[0] };
        _paths.AddRange(rest);

        RebuildList();
        LoadLeft();

        int rightIndex = preferredRightPath != null
            ? _paths.IndexOf(preferredRightPath)
            : -1;
        SetRightIndex(rightIndex > 0 ? rightIndex : 1);
    }

    /// <summary>
    /// 今の基準（_paths[0]）と比べ直し、類似度と、基準の向きにそろえる回転を決める。
    /// 比べられない場合（ハッシュも埋め込みも無い）は類似度をそのままにし、回さずに表示する。
    /// </summary>
    private void CompareWithReference()
    {
        var matches = ImageGrouper.MatchToReference(_paths[0], _paths, _group.Hashes, _group.Embeddings);
        if (matches is null)
        {
            _alignRotations = new();
            return;
        }
        _similarities = matches.ToDictionary(kv => kv.Key, kv => kv.Value.Similarity);
        _alignRotations = matches.ToDictionary(kv => kv.Key, kv => kv.Value.Rotation);
    }

    private void SwapReference(int clickedIdx)
    {
        string oldRef = _paths[0];
        string newRef = _paths[clickedIdx];
        _paths.RemoveAt(clickedIdx);
        _paths.Insert(0, newRef);
        RebuildAfterReferenceChange(preferredRightPath: oldRef);
    }

    // 共通プレフィックス／サフィックスを通常色、中間の差分部分を赤で追記する
    private static void AppendCharDiff(RichTextBox rtb, string text, string? other)
    {
        foreach (var run in DiffText.CharDiff(text, other))
            AppendColored(rtb, run.Text, run.IsDiff);
    }

    private static void AppendColored(RichTextBox rtb, string text, bool isRed)
    {
        rtb.SelectionStart = rtb.TextLength;
        rtb.SelectionLength = 0;
        rtb.SelectionColor = isRed ? Color.FromArgb(200, 0, 0) : Color.FromArgb(40, 40, 40);
        rtb.AppendText(text);
    }

    private Bitmap? LoadAndRotate(string path)
    {
        var bmp = LoadBitmap(path);
        // 基準の向きにそろえる回転に、比較画面での回転を重ねる
        int rotation = (_viewRotation + (_alignRotations.TryGetValue(path, out int align) ? align : 0)) % 4;
        if (bmp != null && rotation > 0)
        {
            var rotType = rotation switch
            {
                1 => RotateFlipType.Rotate90FlipNone,
                2 => RotateFlipType.Rotate180FlipNone,
                _ => RotateFlipType.Rotate270FlipNone,
            };
            bmp.RotateFlip(rotType);
        }
        return bmp;
    }

    private static Bitmap? LoadBitmap(string path, int maxDim = 2000)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var src = Image.FromStream(fs, false, false);
            Bitmap bmp;
            if (src.Width <= maxDim && src.Height <= maxDim)
            {
                bmp = new Bitmap(src);
            }
            else
            {
                double r = Math.Min((double)maxDim / src.Width, (double)maxDim / src.Height);
                int w = (int)(src.Width * r), h = (int)(src.Height * r);
                bmp = new Bitmap(w, h);
                using var g = Graphics.FromImage(bmp);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }

            // メイン画面のサムネイルと同じく EXIF の向きに合わせて回す（基準にそろえる回転と比較画面での回転は LoadAndRotate でこのあとに重ねる）
            ImageOrientation.Apply(bmp, ImageOrientation.Read(src));
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>表示用の日付（EXIF の日時、なければ更新日時）。開き直さないようパスごとに覚えておく。</summary>
    private string GetDateText(string path)
    {
        if (!_dateCache.TryGetValue(path, out var text))
            _dateCache[path] = text = ImageDate.Read(path).ToString("yyyy/MM/dd");
        return text;
    }

    private static string ReadImageSizeText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var img = Image.FromStream(fs, false, false);
            return $"{img.Width}×{img.Height}";
        }
        catch { return "—"; }
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F0} KB",
        _ => $"{bytes} B",
    };

    private void PersistSettings()
    {
        if (_loading) return;

        _settings.CompareWindowMaximized = WindowState == FormWindowState.Maximized;

        // 最大化中は通常サイズを上書きしない
        if (WindowState == FormWindowState.Normal)
        {
            _settings.CompareFormWidth = Width;
            _settings.CompareFormHeight = Height;
        }

        _settings.CompareSplitterDistance = _mainSplitter.SplitterDistance;
        _settings.CompareListColumnWidths = _fileList.Columns
            .Cast<ColumnHeader>().Select(c => c.Width).ToList();
        SettingsStorage.Save(_settings);
    }

    private static void OpenExternal(string? path)
    {
        if (path == null) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    private static void OpenFolder(string? path)
    {
        if (path == null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_paths.Count > 1)
        {
            int newIndex = _currentRightIndex;

            switch (keyData)
            {
                case Keys.Home:
                    newIndex = 1;
                    break;
                case Keys.End:
                    newIndex = _paths.Count - 1;
                    break;
                case Keys.PageUp:
                case Keys.PageDown:
                    int pageSize = 10;
                    try
                    {
                        if (_fileList.Items.Count > 0)
                        {
                            var rect = _fileList.GetItemRect(0);
                            if (rect.Height > 0)
                                pageSize = Math.Max(1, _fileList.ClientSize.Height / rect.Height);
                        }
                    }
                    catch { }

                    if (keyData == Keys.PageUp)
                        newIndex = Math.Max(1, _currentRightIndex - pageSize);
                    else
                        newIndex = Math.Min(_paths.Count - 1, _currentRightIndex + pageSize);
                    break;
            }

            if (newIndex != _currentRightIndex && 
                (keyData == Keys.Home || keyData == Keys.End || keyData == Keys.PageUp || keyData == Keys.PageDown))
            {
                SetRightIndex(newIndex);
                return true;
            }
            else if (keyData == Keys.Home || keyData == Keys.End || keyData == Keys.PageUp || keyData == Keys.PageDown)
            {
                return true; // Consume the key if it's already at the boundary
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        PersistSettings();
        _leftBitmap?.Dispose();
        _rightBitmap?.Dispose();
        base.OnFormClosing(e);
    }

    // ──────────────────────────────────────────────────────────────
    // カスタム画像パネル（ズーム・パン対応）
    // ──────────────────────────────────────────────────────────────

    private sealed class ZoomableImagePanel : Panel
    {
        private Bitmap? _image;
        private float _zoom = 1f;
        private PointF _pan;
        private Point _dragStart;
        private PointF _panStart;
        private bool _dragging;

        public event EventHandler<ViewState>? ViewChanged;

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Bitmap? Image
        {
            get => _image;
            set { _image = value; Invalidate(); }
        }

        public ZoomableImagePanel()
        {
            SetStyle(
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.Selectable,
                true);
            UpdateStyles();
            TabStop = false;
            Cursor = Cursors.Hand;
        }

        // 外部からビューを設定（同期用。ViewChanged は発火しない）
        public void SetView(float zoom, PointF pan)
        {
            _zoom = zoom;
            _pan = pan;
            Invalidate();
        }

        public void ResetView()
        {
            _dragging = false;
            Cursor = Cursors.Hand;
            _zoom = 1f;
            _pan = PointF.Empty;
            Invalidate();
            ViewChanged?.Invoke(this, new ViewState(_zoom, _pan));
        }

        // 画像をパネルに収める矩形（ズーム・パン適用済み）
        private RectangleF ComputeDrawRect()
        {
            if (_image == null || Width <= 0 || Height <= 0) return RectangleF.Empty;
            float imgAspect = (float)_image.Width / _image.Height;
            float panelAspect = (float)Width / Height;
            float fitW = imgAspect > panelAspect ? Width : Height * imgAspect;
            float fitH = imgAspect > panelAspect ? Width / imgAspect : Height;
            float drawW = fitW * _zoom;
            float drawH = fitH * _zoom;
            return new RectangleF(
                (Width - drawW) / 2f + _pan.X,
                (Height - drawH) / 2f + _pan.Y,
                drawW, drawH);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_image == null) return;
            var rect = ComputeDrawRect();
            if (rect.Width <= 0 || rect.Height <= 0) return;
            e.Graphics.InterpolationMode =
                System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode =
                System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(_image, rect);
        }

        // マウスホイール: カーソル位置を中心にズーム
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float ratio = e.Delta > 0 ? 1.25f : 1f / 1.25f;
            float newZoom = Math.Clamp(_zoom * ratio, 0.05f, 50f);
            ratio = newZoom / _zoom; // クランプ後の実際の比率
            // カーソル位置を固定したままパンを調整
            _pan = new PointF(
                ratio * _pan.X + (1f - ratio) * (e.X - Width / 2f),
                ratio * _pan.Y + (1f - ratio) * (e.Y - Height / 2f));
            _zoom = newZoom;
            Invalidate();
            ViewChanged?.Invoke(this, new ViewState(_zoom, _pan));
        }

        // 左ドラッグ: パン
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragStart = e.Location;
                _panStart = _pan;
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            _pan = new PointF(
                _panStart.X + (e.X - _dragStart.X),
                _panStart.Y + (e.Y - _dragStart.Y));
            Invalidate();
            ViewChanged?.Invoke(this, new ViewState(_zoom, _pan));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = false;
                Cursor = Cursors.Hand;
            }
        }

        // マウスカーソルが入ったらフォーカスを取得（ホイールイベント受信のため）
        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Focus();
        }

        // ダブルクリックでビューをリセット
        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            ResetView();
        }
    }

    private readonly record struct ViewState(float Zoom, PointF Pan);
}

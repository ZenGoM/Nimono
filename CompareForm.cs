using System.Diagnostics;

namespace Nimono;

internal sealed class CompareForm : Form
{
    private readonly ImageGroup _group;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, string> _sizeCache = new();

    private SplitContainer _mainSplitter = null!;
    private ZoomableImagePanel _leftView = null!;
    private Label _leftInfo = null!;
    private ZoomableImagePanel _rightView = null!;
    private Label _rightInfo = null!;
    private ListView _fileList = null!;
    private ToolTip _toolTip = null!;

    private Bitmap? _leftBitmap;
    private Bitmap? _rightBitmap;
    private bool _suppressEvent;
    private bool _syncingView;

    public CompareForm(ImageGroup group, AppSettings settings)
    {
        _group = group;
        _settings = settings;
        InitializeComponents();
        PopulateList();
        LoadLeft();
        SetRightIndex(Math.Min(1, group.Paths.Count - 1));
    }

    private void InitializeComponents()
    {
        Text = $"比較 — グループ {_group.Id}（{_group.Paths.Count} 枚）";
        MinimumSize = new Size(600, 400);
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(
            Math.Max(600, _settings.CompareFormWidth),
            Math.Max(400, _settings.CompareFormHeight));

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

        (_leftView, _leftInfo) = BuildSide(imageTable, 0);
        (_rightView, _rightInfo) = BuildSide(imageTable, 1);

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
        _fileList.Columns.Add("フルパス", 300);
        _fileList.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressEvent || _fileList.SelectedItems.Count == 0) return;
            SetRightIndex((int)_fileList.SelectedItems[0].Tag!);
        };
        _fileList.DoubleClick += (_, _) =>
        {
            if (_fileList.SelectedItems.Count == 0) return;
            OpenExternal(_group.Paths[(int)_fileList.SelectedItems[0].Tag!]);
        };

        var listMenu = new ContextMenuStrip();
        listMenu.Items.Add("開く", null, (_, _) =>
        {
            if (_fileList.SelectedItems.Count > 0)
                OpenExternal(_group.Paths[(int)_fileList.SelectedItems[0].Tag!]);
        });
        listMenu.Items.Add("フォルダーを開く", null, (_, _) =>
        {
            if (_fileList.SelectedItems.Count > 0)
                OpenFolder(_group.Paths[(int)_fileList.SelectedItems[0].Tag!]);
        });
        listMenu.Items.Add("パスをコピー", null, (_, _) =>
        {
            if (_fileList.SelectedItems.Count > 0)
                try { Clipboard.SetText(_group.Paths[(int)_fileList.SelectedItems[0].Tag!]); } catch { }
        });
        _fileList.ContextMenuStrip = listMenu;

        _mainSplitter.Panel2.Controls.Add(_fileList);
        Controls.Add(_mainSplitter);

        Load += (_, _) =>
        {
            int dist = _settings.CompareSplitterDistance;
            int h = _mainSplitter.Height;
            _mainSplitter.SplitterDistance =
                dist > 80 && dist < h - 60 ? dist : (int)(h * 0.65);
        };
        ResizeEnd += (_, _) => PersistSettings();
        _mainSplitter.SplitterMoved += (_, _) => PersistSettings();
    }

    private (ZoomableImagePanel view, Label info) BuildSide(TableLayoutPanel table, int column)
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

        var info = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 80,
            Padding = new Padding(8, 4, 8, 4),
            BackColor = Color.FromArgb(245, 245, 248),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(40, 40, 40),
            TextAlign = ContentAlignment.TopLeft,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => OpenExternal(view.Tag as string));
        menu.Items.Add("フォルダーを開く", null, (_, _) => OpenFolder(view.Tag as string));
        menu.Items.Add("パスをコピー", null, (_, _) =>
        {
            if (view.Tag is string p) try { Clipboard.SetText(p); } catch { }
        });
        view.ContextMenuStrip = menu;

        outer.Controls.Add(view);
        outer.Controls.Add(info);
        outer.Controls.Add(roleLabel);
        table.Controls.Add(outer, column, 0);
        return (view, info);
    }

    private void PopulateList()
    {
        _fileList.BeginUpdate();
        for (int i = 0; i < _group.Paths.Count; i++)
        {
            var path = _group.Paths[i];
            try
            {
                var fi = new FileInfo(path);
                double sim = _group.Similarities.TryGetValue(path, out var s) ? s : 0.0;
                string sizeText = ReadImageSizeText(path);
                _sizeCache[path] = sizeText;

                string simText = i == 0 ? "【基準】" : $"{sim:P0}";
                var item = new ListViewItem(simText) { Tag = i };
                item.SubItems.Add(Path.GetFileName(path));
                item.SubItems.Add(fi.Extension.TrimStart('.').ToUpperInvariant());
                item.SubItems.Add(FormatFileSize(fi.Length));
                item.SubItems.Add(sizeText);
                item.SubItems.Add(fi.LastWriteTime.ToString("yyyy/MM/dd"));
                item.SubItems.Add(path);
                _fileList.Items.Add(item);
            }
            catch { }
        }
        _fileList.EndUpdate();
    }

    private void LoadLeft()
    {
        if (_group.Paths.Count == 0) return;
        var path = _group.Paths[0];
        _leftBitmap?.Dispose();
        _leftBitmap = LoadBitmap(path);
        _leftView.Image = _leftBitmap;
        _leftView.Tag = path;
        _toolTip.SetToolTip(_leftView, path);
        _leftInfo.Text = BuildInfoText(path, 0);
    }

    private void SetRightIndex(int index)
    {
        if (index < 0 || index >= _group.Paths.Count) return;
        _suppressEvent = true;
        _fileList.Items[index].Selected = true;
        _fileList.Items[index].EnsureVisible();
        _suppressEvent = false;

        var path = _group.Paths[index];
        _rightBitmap?.Dispose();
        _rightBitmap = LoadBitmap(path);
        _rightView.Image = _rightBitmap;
        _rightView.Tag = path;
        _toolTip.SetToolTip(_rightView, path);
        _rightInfo.Text = BuildInfoText(path, index);
    }

    private string BuildInfoText(string path, int index)
    {
        var fi = new FileInfo(path);
        double sim = _group.Similarities.TryGetValue(path, out var s) ? s : 0.0;
        string role = index == 0 ? "【基準】" : $"類似度: {sim:P0}";
        string res = _sizeCache.TryGetValue(path, out var r) ? r : ReadImageSizeText(path);
        string ext = fi.Extension.TrimStart('.').ToUpperInvariant();
        return $"{Path.GetFileName(path)}\n{role}\n{ext} · {FormatFileSize(fi.Length)}  {res}  {fi.LastWriteTime:yyyy/MM/dd}\n{path}";
    }

    private static Bitmap? LoadBitmap(string path, int maxDim = 2000)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var src = Image.FromStream(fs, false, false);
            if (src.Width <= maxDim && src.Height <= maxDim)
                return new Bitmap(src);
            double r = Math.Min((double)maxDim / src.Width, (double)maxDim / src.Height);
            int w = (int)(src.Width * r), h = (int)(src.Height * r);
            var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
            return bmp;
        }
        catch { return null; }
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
        _settings.CompareFormWidth = Width;
        _settings.CompareFormHeight = Height;
        _settings.CompareSplitterDistance = _mainSplitter.SplitterDistance;
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
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

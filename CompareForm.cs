using System.Diagnostics;

namespace Nimono;

internal sealed class CompareForm : Form
{
    private readonly ImageGroup _group;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, string> _sizeCache = new();

    private SplitContainer _mainSplitter = null!;
    private PictureBox _leftPic = null!;
    private Label _leftInfo = null!;
    private PictureBox _rightPic = null!;
    private Label _rightInfo = null!;
    private ListView _fileList = null!;
    private ToolTip _toolTip = null!;

    private Bitmap? _leftBitmap;
    private Bitmap? _rightBitmap;
    private bool _suppressEvent;

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

        // ── 上部: 左右画像パネル ──
        var imageTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(160, 160, 170), // セパレーター色
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        imageTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        imageTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        imageTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        (_leftPic, _leftInfo) = BuildSide(imageTable, 0);
        (_rightPic, _rightInfo) = BuildSide(imageTable, 1);

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
        _fileList.Columns.Add("ファイル名", 220);
        _fileList.Columns.Add("形式", 55);
        _fileList.Columns.Add("サイズ", 80);
        _fileList.Columns.Add("解像度", 100);
        _fileList.Columns.Add("日付", 90);
        _fileList.Columns.Add("類似度", 70);
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

    private (PictureBox pic, Label info) BuildSide(TableLayoutPanel table, int column)
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

        var pic = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(210, 210, 218),
        };

        var info = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            Padding = new Padding(8, 4, 8, 4),
            BackColor = Color.FromArgb(245, 245, 248),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(40, 40, 40),
            TextAlign = ContentAlignment.TopLeft,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => OpenExternal(pic.Tag as string));
        menu.Items.Add("フォルダーを開く", null, (_, _) => OpenFolder(pic.Tag as string));
        menu.Items.Add("パスをコピー", null, (_, _) =>
        {
            if (pic.Tag is string p) try { Clipboard.SetText(p); } catch { }
        });
        pic.ContextMenuStrip = menu;
        pic.DoubleClick += (_, _) => OpenExternal(pic.Tag as string);

        outer.Controls.Add(pic);
        outer.Controls.Add(info);
        outer.Controls.Add(roleLabel);
        table.Controls.Add(outer, column, 0);
        return (pic, info);
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

                string name = i == 0
                    ? $"{Path.GetFileName(path)}  [基準]"
                    : Path.GetFileName(path);
                var item = new ListViewItem(name) { Tag = i };
                item.SubItems.Add(fi.Extension.TrimStart('.').ToUpperInvariant());
                item.SubItems.Add(FormatFileSize(fi.Length));
                item.SubItems.Add(sizeText);
                item.SubItems.Add(fi.LastWriteTime.ToString("yyyy/MM/dd"));
                item.SubItems.Add($"{sim:P0}");
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
        _leftPic.Image = _leftBitmap;
        _leftPic.Tag = path;
        _toolTip.SetToolTip(_leftPic, path);
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
        _rightPic.Image = _rightBitmap;
        _rightPic.Tag = path;
        _toolTip.SetToolTip(_rightPic, path);
        _rightInfo.Text = BuildInfoText(path, index);
    }

    private string BuildInfoText(string path, int index)
    {
        var fi = new FileInfo(path);
        double sim = _group.Similarities.TryGetValue(path, out var s) ? s : 0.0;
        string role = index == 0 ? "【基準】" : $"類似度: {sim:P0}";
        string res = _sizeCache.TryGetValue(path, out var r) ? r : ReadImageSizeText(path);
        string ext = fi.Extension.TrimStart('.').ToUpperInvariant();
        return $"{Path.GetFileName(path)}\n{role}  {ext} · {FormatFileSize(fi.Length)}  {res}  {fi.LastWriteTime:yyyy/MM/dd}\n{path}";
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
}

namespace Nimono;

public class SearchFolderDialog : Form
{
    private ListBox _listBox = null!;
    private Button _addButton = null!;
    private Button _removeButton = null!;
    private Button _upButton = null!;
    private Button _downButton = null!;

    public IReadOnlyList<string> SelectedFolders =>
        _listBox.Items.Cast<string>().ToList();

    public SearchFolderDialog(IEnumerable<string> initialFolders)
    {
        InitializeComponents();
        foreach (var folder in initialFolders.Where(Directory.Exists))
            _listBox.Items.Add(folder);
        UpdateButtons();
    }

    private void InitializeComponents()
    {
        Text = "検索フォルダーの設定";
        Size = new Size(600, 420);
        MinimumSize = new Size(450, 300);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);

        var topLabel = new Label
        {
            Text = "似た写真を探すフォルダーを指定してください（複数指定可）",
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
        };

        _listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            SelectionMode = SelectionMode.MultiExtended,
            Font = new Font("Segoe UI", 9.5f),
            HorizontalScrollbar = true,
        };
        _listBox.SelectedIndexChanged += (_, _) => UpdateButtons();

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(8, 6, 8, 6),
        };

        var okButton = new Button
        {
            Text = "OK",
            Width = 90,
            Dock = DockStyle.Right,
            DialogResult = DialogResult.OK,
        };
        var cancelButton = new Button
        {
            Text = "キャンセル",
            Width = 90,
            Dock = DockStyle.Right,
            DialogResult = DialogResult.Cancel,
        };
        _removeButton = new Button
        {
            Text = "削除",
            Width = 80,
            Dock = DockStyle.Left,
            Enabled = false,
        };
        _addButton = new Button
        {
            Text = "フォルダーを追加",
            Width = 130,
            Dock = DockStyle.Left,
        };

        _upButton = new Button
        {
            Text = "上へ",
            Width = 60,
            Dock = DockStyle.Left,
            Enabled = false,
        };
        _downButton = new Button
        {
            Text = "下へ",
            Width = 60,
            Dock = DockStyle.Left,
            Enabled = false,
        };

        _addButton.Click += AddButton_Click;
        _removeButton.Click += RemoveButton_Click;
        _upButton.Click += (_, _) => MoveSelected(-1);
        _downButton.Click += (_, _) => MoveSelected(+1);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        bottomPanel.Controls.Add(okButton);
        bottomPanel.Controls.Add(cancelButton);
        // Dock=Left は後から追加したものほど左に置かれる。左から「追加」「削除」「上へ」「下へ」の順にする
        bottomPanel.Controls.Add(_downButton);
        bottomPanel.Controls.Add(_upButton);
        bottomPanel.Controls.Add(_removeButton);
        bottomPanel.Controls.Add(_addButton);

        Controls.Add(_listBox);
        Controls.Add(bottomPanel);
        Controls.Add(topLabel);

        // ボタンの幅を文言に合わせ、最小幅はボタン・説明文が欠けずに収まる幅にする
        // （固定幅だと、DPI やフォントによって文言が切れ、最小幅では右のボタンに重なっていた）
        Button[] leftButtons = [_addButton, _removeButton, _upButton, _downButton];
        Button[] rightButtons = [okButton, cancelButton];
        foreach (var b in leftButtons.Concat(rightButtons))
            b.Width = Math.Max(b.Width, TextRenderer.MeasureText(b.Text, Font).Width + 24);
        const int GroupGap = 24; // 左右のボタンの間に最低限あける幅
        int buttonsWidth = leftButtons.Concat(rightButtons).Sum(b => b.Width) + GroupGap + bottomPanel.Padding.Horizontal;
        int labelWidth = topLabel.Text.Split('\n').Max(line => TextRenderer.MeasureText(line, Font).Width)
                         + topLabel.Padding.Horizontal + 8;
        int minClientWidth = Math.Max(buttonsWidth, labelWidth);
        MinimumSize = SizeFromClientSize(new Size(minClientWidth, 240));
        Width = Math.Max(Width, MinimumSize.Width);
    }

    private void AddButton_Click(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "検索対象フォルダーを選択してください",
            UseDescriptionForTitle = true,
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var path = Path.GetFullPath(dlg.SelectedPath);

        if (!_listBox.Items.Cast<string>()
            .Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
        {
            _listBox.Items.Add(path);
        }
        UpdateButtons();
    }

    private void RemoveButton_Click(object? sender, EventArgs e)
    {
        var toRemove = _listBox.SelectedItems.Cast<string>().ToList();
        foreach (var item in toRemove)
            _listBox.Items.Remove(item);
        UpdateButtons();
    }

    // 並びを変えられるのは 1 つだけ選んでいるとき
    private void MoveSelected(int delta)
    {
        if (_listBox.SelectedIndices.Count != 1) return;
        int i = _listBox.SelectedIndex;
        int j = i + delta;
        if (j < 0 || j >= _listBox.Items.Count) return;
        var item = _listBox.Items[i];
        _listBox.Items.RemoveAt(i);
        _listBox.Items.Insert(j, item);
        _listBox.ClearSelected();
        _listBox.SelectedIndex = j;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _removeButton.Enabled = _listBox.SelectedItems.Count > 0;
        bool single = _listBox.SelectedIndices.Count == 1;
        _upButton.Enabled = single && _listBox.SelectedIndex > 0;
        _downButton.Enabled = single && _listBox.SelectedIndex < _listBox.Items.Count - 1;
    }
}

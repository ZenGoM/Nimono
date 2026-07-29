namespace Nimono;

public class SearchFolderDialog : Form
{
    private ListBox _listBox = null!;
    private Button _addButton = null!;
    private Button _removeButton = null!;

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

        _addButton.Click += AddButton_Click;
        _removeButton.Click += RemoveButton_Click;
        AcceptButton = okButton;
        CancelButton = cancelButton;

        bottomPanel.Controls.Add(okButton);
        bottomPanel.Controls.Add(cancelButton);
        bottomPanel.Controls.Add(_removeButton);
        bottomPanel.Controls.Add(_addButton);

        Controls.Add(_listBox);
        Controls.Add(bottomPanel);
        Controls.Add(topLabel);
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

    private void UpdateButtons()
    {
        _removeButton.Enabled = _listBox.SelectedItems.Count > 0;
    }
}

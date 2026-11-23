namespace Nimono;

/// <summary>
/// 基準画像の優先順位を決める画面。使う項目にチェックを入れ、上から順に比べる並びと、優先する向きを決める。
/// </summary>
internal sealed class ReferencePriorityDialog : Form
{
    private readonly List<ReferencePriorityRule> _rules;
    private CheckedListBox _list = null!;
    private Button _upButton = null!;
    private Button _downButton = null!;
    private Button _flipButton = null!;
    private bool _refilling;

    /// <summary>OK で閉じたときの優先順位（上から順）。</summary>
    public IReadOnlyList<ReferencePriorityRule> Rules => _rules;

    public ReferencePriorityDialog(IEnumerable<ReferencePriorityRule> rules)
    {
        _rules = ReferencePriority.Normalize(rules);
        InitializeComponents();
        Refill(0);
    }

    private void InitializeComponents()
    {
        Text = "基準画像の優先順位";
        Size = new Size(700, 420);
        MinimumSize = new Size(600, 360);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var topLabel = new Label
        {
            Text = "基準画像を選択するための優先順を指定してください。",
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(4, 6, 4, 0),
        };

        _list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
        };
        _list.ItemCheck += (_, e) =>
        {
            if (_refilling) return;
            _rules[e.Index].Enabled = e.NewValue == CheckState.Checked;
        };
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();

        // ボタン幅は最も長い文言に合わせる（親に追加する前の Font は既定のフォントを返すため、フォーム自身の Font で測る）
        int buttonWidth = TextRenderer.MeasureText("優先の向きを反転", Font).Width + 32;
        var sidePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = buttonWidth + 16,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(8, 0, 0, 0),
        };
        _upButton = new Button { Text = "上へ", Width = buttonWidth, Height = 30 };
        _downButton = new Button { Text = "下へ", Width = buttonWidth, Height = 30 };
        _flipButton = new Button { Text = "優先の向きを反転", Width = buttonWidth, Height = 30 };
        var resetButton = new Button { Text = "初期設定に戻す", Width = buttonWidth, Height = 30, Margin = new Padding(3, 18, 3, 3) };
        _upButton.Click += (_, _) => MoveSelected(-1);
        _downButton.Click += (_, _) => MoveSelected(+1);
        _flipButton.Click += (_, _) =>
        {
            int i = _list.SelectedIndex;
            if (i < 0) return;
            _rules[i].PreferHigher = !_rules[i].PreferHigher;
            Refill(i);
        };
        resetButton.Click += (_, _) =>
        {
            _rules.Clear();
            _rules.AddRange(ReferencePriority.DefaultRules());
            Refill(0);
        };
        sidePanel.Controls.AddRange([_upButton, _downButton, _flipButton, resetButton]);

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(0, 6, 0, 6),
        };
        var okButton = new Button { Text = "OK", Width = 90, Dock = DockStyle.Right, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "キャンセル", Width = 90, Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
        bottomPanel.Controls.Add(okButton);
        bottomPanel.Controls.Add(cancelButton);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 0) };
        body.Controls.Add(_list);
        body.Controls.Add(sidePanel);

        // Dock は後から追加したものほど外側に置かれるため、Fill → Bottom → Top の順で追加する
        Controls.Add(body);
        Controls.Add(bottomPanel);
        Controls.Add(topLabel);

        // 最小幅は、説明文・項目の文言・右のボタンが欠けずに収まる幅にする
        foreach (var b in new[] { okButton, cancelButton })
            b.Width = Math.Max(b.Width, TextRenderer.MeasureText(b.Text, Font).Width + 24);
        int labelWidth = topLabel.Text.Split('\n').Max(line => TextRenderer.MeasureText(line, Font).Width)
                         + topLabel.Padding.Horizontal + 8;
        int itemWidth = Enum.GetValues<ReferencePriorityKey>()
            .SelectMany(k => new[] { new ReferencePriorityRule(k, false, false), new ReferencePriorityRule(k, false, true) })
            .Max(r => TextRenderer.MeasureText(r.Describe(), Font).Width)
            + SystemInformation.MenuCheckSize.Width + 16 + SystemInformation.VerticalScrollBarWidth;
        int bodyWidth = itemWidth + sidePanel.Width + body.Padding.Horizontal;
        int minClientWidth = Math.Max(labelWidth, bodyWidth);
        MinimumSize = SizeFromClientSize(new Size(minClientWidth, 300));
        Width = Math.Max(Width, MinimumSize.Width);
    }

    private void MoveSelected(int delta)
    {
        int i = _list.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _rules.Count) return;
        (_rules[i], _rules[j]) = (_rules[j], _rules[i]);
        Refill(j);
    }

    private void Refill(int selectedIndex)
    {
        _refilling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var rule in _rules)
            _list.Items.Add(rule.Describe(), rule.Enabled);
        _list.EndUpdate();
        _list.SelectedIndex = Math.Clamp(selectedIndex, 0, _rules.Count - 1);
        _refilling = false;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        int i = _list.SelectedIndex;
        _upButton.Enabled = i > 0;
        _downButton.Enabled = i >= 0 && i < _rules.Count - 1;
        _flipButton.Enabled = i >= 0;
    }
}

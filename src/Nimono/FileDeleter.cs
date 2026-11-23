using System.Runtime.InteropServices;

namespace Nimono;

/// <summary>
/// 画像ファイルの削除（ゴミ箱への移動、できなければ完全削除）と、その確認ダイアログ。
/// 比較画面の「削除」とメイン画面の「基準以外を削除」で共通に使う。
/// </summary>
internal sealed class FileDeleter
{
    // 「次回以降確認しない」はアプリを閉じるまで有効（画面をまたいで共通）
    private static bool s_skipDeleteConfirmation;
    // 完全削除の確認を省略するクラウド同期プロバイダー名（セッション中のみ）
    private static readonly HashSet<string> s_skipPermanentDeleteProviders = new();

    // 一括削除の確認に並べるファイルの最大数（それ以上は「ほか N 件」）
    private const int MaxListedPaths = 10;

    private readonly IWin32Window _owner;
    private readonly AppSettings _settings;

    public FileDeleter(IWin32Window owner, AppSettings settings)
    {
        _owner = owner;
        _settings = settings;
    }

    // ゴミ箱アイコン（空／満杯）の再描画をシェルに要求する専用 API
    [DllImport("shell32.dll")]
    private static extern int SHUpdateRecycleBinIcon();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_NO_CONNECTED_ELEMENTS = 0x2000;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;
    // クラウド同期プロバイダーが操作に対応していない（ゴミ箱への移動を拒否した）
    private const int ERROR_CLOUD_FILE_NOT_SUPPORTED = 0x17B;

    /// <summary>ゴミ箱への移動を拒否するクラウド同期フォルダー内なら、そのプロバイダー名。</summary>
    private string? NoRecycleProvider(string path) =>
        CloudSyncRoot.GetProviderName(path) is { } provider && _settings.NoRecycleProviders.Contains(provider)
            ? provider
            : null;

    /// <summary>
    /// 1 件を確認してから削除する。ゴミ箱に移動できないクラウド同期フォルダーなら、最初から完全削除の確認だけを出す。
    /// </summary>
    /// <returns>ファイルが削除された場合 true</returns>
    public bool Delete(string path)
    {
        if (NoRecycleProvider(path) is { } noRecycle)
        {
            return ConfirmAndDeletePermanently(path, noRecycle,
                $"このフォルダーを同期している {noRecycle} は、ゴミ箱への移動に対応していません。");
        }
        return ConfirmAndRecycle(path, CloudSyncRoot.GetProviderName(path));
    }

    /// <summary>
    /// 基準以外の画像をまとめて削除する。一度にまとめて消すため「次回以降確認しない」に関係なく必ず確認し、
    /// ゴミ箱に移動できないもの（完全削除になるもの）はその旨を確認に書く。
    /// </summary>
    /// <returns>削除できたファイル</returns>
    public List<string> DeleteAllExceptReference(string referencePath, IReadOnlyList<string> paths)
    {
        var deleted = new List<string>();
        if (paths.Count == 0) return deleted;

        var permanent = paths.Select(p => (Path: p, Provider: NoRecycleProvider(p)))
                             .Where(x => x.Provider != null)
                             .ToList();
        var recyclable = paths.Except(permanent.Select(x => x.Path)).ToList();

        var text = new System.Text.StringBuilder();
        text.Append($"基準: {referencePath}\n");
        if (recyclable.Count > 0)
        {
            text.Append($"\nゴミ箱に移動: {recyclable.Count} 件\n");
            AppendPathList(text, recyclable);
        }
        if (permanent.Count > 0)
        {
            string providers = string.Join("、", permanent.Select(x => x.Provider).Distinct());
            text.Append($"\n完全に削除（{providers} はゴミ箱への移動に対応していません）: {permanent.Count} 件\n");
            AppendPathList(text, permanent.Select(x => x.Path).ToList());
            text.Append("\n完全に削除したファイルはゴミ箱から元に戻せません。" +
                        "クラウド同期フォルダーの場合は、クラウド上のファイルも削除されます。");
        }

        var page = new TaskDialogPage()
        {
            Caption = "削除の確認",
            Heading = permanent.Count > 0
                ? $"基準以外の {paths.Count} 件を削除しますか？（{permanent.Count} 件は完全に削除されます）"
                : $"基準以外の {paths.Count} 件をゴミ箱に移動しますか？",
            Text = text.ToString().TrimEnd(),
            Icon = TaskDialogIcon.Warning,
            Buttons = { TaskDialogButton.Yes, TaskDialogButton.No },
            DefaultButton = TaskDialogButton.No,
        };
        if (TaskDialog.ShowDialog(_owner, page) != TaskDialogButton.Yes) return deleted;

        var errors = new List<string>();
        using (new WaitCursor())
        {
            foreach (var path in recyclable)
            {
                // 移動に失敗したものは、1 件ずつ完全削除するかを確認する
                if (RecycleWithFallback(path, CloudSyncRoot.GetProviderName(path), notifyIcon: false))
                    deleted.Add(path);
            }
            if (recyclable.Count > 0) SHUpdateRecycleBinIcon();

            // 完全削除になるものは上の確認で了承済み
            foreach (var (path, _) in permanent)
            {
                try
                {
                    File.Delete(path);
                    deleted.Add(path);
                }
                catch (Exception ex)
                {
                    errors.Add($"{path}\n  {ex.Message}");
                }
            }
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(_owner, $"{errors.Count} 件の削除に失敗しました:\n\n{string.Join("\n", errors.Take(MaxListedPaths))}",
                "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return deleted;
    }

    private static void AppendPathList(System.Text.StringBuilder text, IReadOnlyList<string> paths)
    {
        foreach (var p in paths.Take(MaxListedPaths))
            text.Append($"  {p}\n");
        if (paths.Count > MaxListedPaths)
            text.Append($"  ほか {paths.Count - MaxListedPaths} 件\n");
    }

    /// <summary>
    /// ゴミ箱に移動してよいかを確認してから移動する。移動できなければ完全削除するかを確認する。
    /// </summary>
    /// <returns>ファイルが削除された場合 true</returns>
    private bool ConfirmAndRecycle(string path, string? provider)
    {
        if (!s_skipDeleteConfirmation)
        {
            var page = new TaskDialogPage()
            {
                Caption = "削除の確認",
                Heading = "次のファイルをゴミ箱に移動しますか？",
                Text = path,
                Icon = TaskDialogIcon.Warning,
                Buttons = { TaskDialogButton.Yes, TaskDialogButton.No },
                DefaultButton = TaskDialogButton.No,
                Verification = new TaskDialogVerificationCheckBox()
                {
                    Text = "次回以降確認しない"
                }
            };

            var dlgResult = TaskDialog.ShowDialog(_owner, page);
            if (dlgResult != TaskDialogButton.Yes) return false;

            if (page.Verification.Checked)
            {
                s_skipDeleteConfirmation = true;
            }
        }

        return RecycleWithFallback(path, provider, notifyIcon: true);
    }

    /// <summary>
    /// ゴミ箱への移動を試み、できなければ完全削除するかをユーザーに確認する。
    /// iCloud 写真などのクラウド同期フォルダーでは、プロバイダーが同期フォルダー外への
    /// 移動を拒否するためゴミ箱に入れられない（0x8007017B）。
    /// </summary>
    /// <param name="notifyIcon">移動できたらゴミ箱アイコンを更新する（一括削除では最後に 1 回だけ行う）</param>
    /// <returns>ファイルが削除された場合 true</returns>
    private bool RecycleWithFallback(string path, string? provider, bool notifyIcon)
    {
        // シェルのエラーダイアログは出さず、失敗理由はこちらで扱う
        var op = new SHFILEOPSTRUCT
        {
            hwnd = _owner.Handle,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI
                   | FOF_NO_CONNECTED_ELEMENTS | FOF_WANTNUKEWARNING,
        };
        int result = SHFileOperation(ref op);

        if (result == 0)
        {
            // 完全削除の警告でユーザーが中止した
            if (op.fAnyOperationsAborted) return false;

            // ゴミ箱のアイコンを更新するようシェルに通知
            if (notifyIcon) SHUpdateRecycleBinIcon();
            return true;
        }

        if (!File.Exists(path)) return true;

        // 次からはこのプロバイダーのファイルで、最初から完全削除の確認を出す
        if (result == ERROR_CLOUD_FILE_NOT_SUPPORTED && provider != null
            && !_settings.NoRecycleProviders.Contains(provider))
        {
            _settings.NoRecycleProviders.Add(provider);
            SettingsStorage.Save(_settings);
        }

        return ConfirmAndDeletePermanently(path, provider,
            $"理由: {new System.ComponentModel.Win32Exception(result).Message} (0x{result:X})",
            "ゴミ箱に移動できませんでした。完全に削除しますか？");
    }

    /// <summary>
    /// ゴミ箱を通さずに完全削除してよいかを確認してから削除する。
    /// ゴミ箱への移動の「次回以降確認しない」とは別に、同期プロバイダーごとに確認を省略できる。
    /// 同期フォルダーの外（provider が null）では省略できず、毎回確認する。
    /// </summary>
    /// <returns>ファイルが削除された場合 true</returns>
    private bool ConfirmAndDeletePermanently(string path, string? provider, string reason,
        string heading = "このファイルはゴミ箱に移動できません。完全に削除しますか？")
    {
        if (provider == null || !s_skipPermanentDeleteProviders.Contains(provider))
        {
            var page = new TaskDialogPage()
            {
                Caption = "削除の確認",
                Heading = heading,
                Text = $"{path}\n\n{reason}\n\n" +
                       "完全に削除したファイルはゴミ箱から元に戻せません。" +
                       "クラウド同期フォルダーの場合は、クラウド上のファイルも削除されます。",
                Icon = TaskDialogIcon.Warning,
                Buttons = { TaskDialogButton.Yes, TaskDialogButton.No },
                DefaultButton = TaskDialogButton.No,
            };
            if (provider != null)
            {
                page.Verification = new TaskDialogVerificationCheckBox()
                {
                    Text = $"次回以降確認しない（{provider} のファイルのみ）"
                };
            }

            if (TaskDialog.ShowDialog(_owner, page) != TaskDialogButton.Yes) return false;

            if (provider != null && page.Verification!.Checked)
            {
                s_skipPermanentDeleteProviders.Add(provider);
            }
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(_owner, $"削除に失敗しました:\n{ex.Message}", "エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private sealed class WaitCursor : IDisposable
    {
        private readonly Cursor _previous = Cursor.Current;
        public WaitCursor() => Cursor.Current = Cursors.WaitCursor;
        public void Dispose() => Cursor.Current = _previous;
    }
}

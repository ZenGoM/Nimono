using System.Runtime.InteropServices;

namespace Nimono;

/// <summary>
/// iCloud 写真や OneDrive などのクラウド同期フォルダー（Cloud Files API の同期ルート）を調べる。
/// </summary>
internal static class CloudSyncRoot
{
    private const int CF_SYNC_ROOT_INFO_PROVIDER = 2;
    // CF_SYNC_ROOT_PROVIDER_INFO: ProviderStatus(4 バイト) の後に ProviderName[256], ProviderVersion[256]（UTF-16）
    private const int ProviderNameOffset = 4;
    private const int ProviderInfoSize = 4 + 256 * 2 + 256 * 2;

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    private static extern int CfGetSyncRootInfoByPath(
        string filePath, int infoClass, IntPtr infoBuffer, uint infoBufferLength, out uint returnedLength);

    /// <summary>
    /// パスを同期しているプロバイダーの名前（例: "iCloudPhotos", "OneDrive - Personal"）を返す。
    /// 同期フォルダーの外なら null。
    /// </summary>
    public static string? GetProviderName(string path)
    {
        IntPtr buffer = Marshal.AllocHGlobal(ProviderInfoSize);
        try
        {
            int hr = CfGetSyncRootInfoByPath(path, CF_SYNC_ROOT_INFO_PROVIDER, buffer, ProviderInfoSize, out _);
            if (hr != 0) return null;

            string? name = Marshal.PtrToStringUni(buffer + ProviderNameOffset);
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception)
        {
            // cldapi.dll が無い環境などでは、同期フォルダーではないものとして扱う
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

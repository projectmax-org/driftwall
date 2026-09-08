using System.Runtime.InteropServices;
using System.Text;

namespace Driftwall.Core;

/// <summary>
/// Windows Data Protection for the API keys the user pastes in.
/// <para>
/// Keys are encrypted to the current user account, so settings.json copied to another machine simply
/// loses its keys rather than leaking them. Called directly through crypt32 to avoid taking a NuGet
/// dependency for two functions.
/// </para>
/// </summary>
internal static class Dpapi
{
    private const string Prefix = "dpapi:";

    /// <summary>Encrypts a value, returning a prefixed base64 string. Returns the input on failure.</summary>
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;
        if (plainText.StartsWith(Prefix, StringComparison.Ordinal)) return plainText;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            return Prefix + Convert.ToBase64String(Transform(bytes, protect: true));
        }
        catch (Exception ex)
        {
            Log.Warn("DPAPI protect failed; storing the value as-is.", ex);
            return plainText;
        }
    }

    /// <summary>Decrypts a value produced by <see cref="Protect"/>. Plain values pass through unchanged.</summary>
    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;

        try
        {
            var bytes = Convert.FromBase64String(value[Prefix.Length..]);
            return Encoding.UTF8.GetString(Transform(bytes, protect: false));
        }
        catch (Exception ex)
        {
            // Usually means the settings file came from another user account or machine.
            Log.Warn("DPAPI unprotect failed; the stored key cannot be read on this account.", ex);
            return string.Empty;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        try
        {
            inBlob.cbData = input.Length;
            inBlob.pbData = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);

            bool ok = protect
                ? CryptProtectData(ref inBlob, "Driftwall", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);

            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

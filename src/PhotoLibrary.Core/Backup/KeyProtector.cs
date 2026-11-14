using System.Runtime.InteropServices;

namespace PhotoLibrary.Core.Backup;

/// <summary>
/// Keeps a secret for the current Windows user with DPAPI (CryptProtectData), the same protection Windows uses
/// for saved Wi-Fi and browser passwords: only this user, signed in on this PC, can read it back. Used so scheduled
/// encrypted backups don't have to ask for the password. Elsewhere it isn't available and the password is asked for.
/// </summary>
public static class KeyProtector
{
    private static readonly byte[] Entropy = "Keepsake backup key v1"u8.ToArray();
    private const int UiForbidden = 0x1;

    public static bool IsAvailable => OperatingSystem.IsWindows();

    public static string? Protect(byte[] secret)
    {
        if (!IsAvailable) return null;
        return Transform(secret, protect: true) is { } blob ? Convert.ToBase64String(blob) : null;
    }

    public static byte[]? Unprotect(string protectedBase64)
    {
        if (!IsAvailable) return null;
        try { return Transform(Convert.FromBase64String(protectedBase64), protect: false); }
        catch (FormatException) { return null; }
    }

    private static byte[]? Transform(byte[] input, bool protect)
    {
        var inBlob = new DataBlob();
        var entropy = new DataBlob();
        var outBlob = new DataBlob();
        try
        {
            inBlob = DataBlob.From(input);
            entropy = DataBlob.From(Entropy);
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob);
            if (!ok || outBlob.pbData == IntPtr.Zero) return null;
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            inBlob.Free();
            entropy.Free();
            if (outBlob.pbData != IntPtr.Zero)
            {
                // Zero the plaintext copy Windows handed back before releasing it.
                Marshal.Copy(new byte[outBlob.cbData], 0, outBlob.pbData, outBlob.cbData);
                LocalFree(outBlob.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;

        public static DataBlob From(byte[] data)
        {
            var blob = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
            Marshal.Copy(data, 0, blob.pbData, data.Length);
            return blob;
        }

        public void Free()
        {
            if (pbData == IntPtr.Zero) return;
            Marshal.Copy(new byte[cbData], 0, pbData, cbData);
            Marshal.FreeHGlobal(pbData);
            pbData = IntPtr.Zero;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

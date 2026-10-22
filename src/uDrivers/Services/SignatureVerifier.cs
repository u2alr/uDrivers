using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace uDrivers.Services;

/// <summary>
/// Checks a downloaded installer's Authenticode signature with WinVerifyTrust and reads who signed it.
/// Nothing is run unless the signature is valid.
/// </summary>
public static class SignatureVerifier
{
    public sealed record Result(bool IsValid, string Signer);

    public static Result Check(string path)
    {
        bool valid = VerifyTrust(path);
        string signer = "";
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            signer = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch
        {
            // unsigned or unreadable
        }
        return new Result(valid, signer);
    }

    /// <summary>
    /// Throws unless the file has a valid signature. If <paramref name="hashVerified"/> is false,
    /// the signer must also contain one of <paramref name="expectedSigners"/>.
    /// </summary>
    public static void Require(string path, bool hashVerified, params string[] expectedSigners)
    {
        var r = Check(path);
        if (!r.IsValid)
            throw new InvalidOperationException("The download isn't validly signed, so it wasn't run.");

        bool signerOk = expectedSigners.Any(s => r.Signer.Contains(s, StringComparison.OrdinalIgnoreCase));
        if (!signerOk && !hashVerified)
            throw new InvalidOperationException($"The download is signed by \"{r.Signer}\", not the expected vendor, so it wasn't run.");

        Log.Info($"Signature OK ({r.Signer}): {Path.GetFileName(path)}");
    }

    // ---- WinVerifyTrust interop ----

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static bool VerifyTrust(string path)
    {
        var fileInfo = new WintrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = path,
        };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,
                dwProvFlags = 0x00000010,  // WTD_REVOCATION_CHECK_NONE
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WintrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WintrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}

using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Cleanup.Platform.Windows;

/// <summary>Who signed an executable, per Windows' own Authenticode check (WinVerifyTrust).</summary>
public static class Authenticode
{
    /// <summary>
    /// The signer's simple name (the leaf certificate's CN, e.g. "SignPath Foundation") when the file carries a valid
    /// embedded signature chaining to a trusted root; null when it is unsigned, tampered with, untrusted or unreadable.
    /// Revocation is not checked, so a machine without internet still gets an answer.
    /// </summary>
    public static string? Signer(string path)
    {
        var file = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = Marshal.StringToHGlobalUni(path),
        };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = WTD_CHOICE_FILE,
            pFile = filePtr,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_REVOCATION_CHECK_NONE,
        };
        var action = GenericVerifyV2;
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) return null;
            // The leaf certificate of the signature WinVerifyTrust just accepted, read from its own state (not the file again).
            var provider = WTHelperProvDataFromStateData(data.hWVTStateData);
            var signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            var leaf = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
            // CRYPT_PROVIDER_CERT: { DWORD cbStruct; PCCERT_CONTEXT pCert; … }, pCert pointer-aligned after the DWORD.
            var context = leaf == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(leaf, IntPtr.Size);
            if (context == IntPtr.Zero) return null;
            using var certificate = new X509Certificate2(context);
            var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or System.Security.Cryptography.CryptographicException) { return null; }
        finally
        {
            if (data.hWVTStateData != IntPtr.Zero)
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(file.pcwszFilePath);
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
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

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);
}

using System.Runtime.InteropServices;
using System.Text;

namespace Mirador.Ipc;

/// <summary>
/// The Authenticode signer subject of a file, through <c>WinVerifyTrust</c> and the crypto API: the subject string
/// (<c>CN=Valve Corp., O=…</c>) when the file is signed and its chain is trusted, else null. Passed to
/// <see cref="SteamVrDriverCheck.Verify"/> (the helper) and <see cref="SteamVrDriverCheck.UnsignedFiles"/> (the consent
/// step), which parse it and require its CN and O to be exactly <c>Valve Corp.</c> Callers hold the file open without
/// write or delete sharing while asking, so the bytes checked are the bytes that are hashed and loaded.
/// </summary>
public static class WinTrustSigner
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdSaferFlag = 0x100;

    private const uint CertQueryObjectFile = 1;
    private const uint CertQueryContentFlagPkcs7SignedEmbed = 1 << 10;
    private const uint CertQueryFormatFlagBinary = 2;
    private const uint Pkcs7OrX509AsnEncoding = 0x00010001;
    private const uint CmsgSignerCertInfoParam = 7;
    private const uint CertX500NameStr = 3;

    private static Guid genericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>The signer subject, or null when the file isn't a trusted Authenticode signature Windows accepts.</summary>
    public static string? SubjectOf(string path)
    {
        if (!IsTrusted(path))
        {
            return null;
        }

        return SignerSubject(path);
    }

    private static bool IsTrusted(string path)
    {
        // WINTRUST_DATA points at the WINTRUST_FILE_INFO, which points at the path: both live in unmanaged memory for
        // the two calls (a struct with a string in it can't be pinned), and are freed whatever happens.
        var pathText = IntPtr.Zero;
        var fileInfo = IntPtr.Zero;
        try
        {
            pathText = Marshal.StringToHGlobalUni(path);
            var file = new WintrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                pcwszFilePath = pathText,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };
            fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(file, fileInfo, fDeleteOld: false);

            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfo,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdSaferFlag,
            };

            var result = WinVerifyTrust(IntPtr.Zero, ref genericVerifyV2, ref data);
            data.dwStateAction = WtdStateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref genericVerifyV2, ref data);
            return result == 0;
        }
        finally
        {
            if (fileInfo != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfo);
            }

            if (pathText != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pathText);
            }
        }
    }

    private static string? SignerSubject(string path)
    {
        if (!CryptQueryObject(
                CertQueryObjectFile, path, CertQueryContentFlagPkcs7SignedEmbed, CertQueryFormatFlagBinary, 0,
                out _, out _, out _, out var store, out var message, out _))
        {
            return null;
        }

        try
        {
            uint size = 0;
            if (!CryptMsgGetParam(message, CmsgSignerCertInfoParam, 0, IntPtr.Zero, ref size) || size == 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!CryptMsgGetParam(message, CmsgSignerCertInfoParam, 0, buffer, ref size))
                {
                    return null;
                }

                var cert = CertFindCertificateInStore(store, Pkcs7OrX509AsnEncoding, 0, CertFindSubjectCert, buffer, IntPtr.Zero);
                if (cert == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return SubjectName(cert);
                }
                finally
                {
                    CertFreeCertificateContext(cert);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            if (store != IntPtr.Zero)
            {
                CertCloseStore(store, 0);
            }

            if (message != IntPtr.Zero)
            {
                CryptMsgClose(message);
            }
        }
    }

    // CERT_FIND_SUBJECT_CERT: CERT_COMPARE_SUBJECT_CERT (11) << CERT_COMPARE_SHIFT (16). (0x00010000 is CERT_FIND_SHA1_HASH.)
    private const uint CertFindSubjectCert = 0x000B0000;

    private static string? SubjectName(IntPtr cert)
    {
        // CERT_CONTEXT.pCertInfo is the 4th pointer-sized field (dwCertEncodingType, pbCertEncoded, cbCertEncoded, pCertInfo).
        var context = (CertContext)Marshal.PtrToStructure(cert, typeof(CertContext))!;
        if (context.pCertInfo == IntPtr.Zero)
        {
            return null;
        }

        // CERT_INFO.Subject (a CERT_NAME_BLOB) sits after the fields before it; marshal the whole struct.
        var info = (CertInfo)Marshal.PtrToStructure(context.pCertInfo, typeof(CertInfo))!;
        var subject = info.Subject;

        var size = CertNameToStr(Pkcs7OrX509AsnEncoding, ref subject, CertX500NameStr, null, 0);
        if (size <= 1)
        {
            return null;
        }

        var text = new StringBuilder((int)size);
        CertNameToStr(Pkcs7OrX509AsnEncoding, ref subject, CertX500NameStr, text, size);
        return text.ToString();
    }

    /// <summary>WINTRUST_FILE_INFO, blittable: the path is a pointer to a string the caller allocated.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
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

    [StructLayout(LayoutKind.Sequential)]
    private struct CertContext
    {
        public uint dwCertEncodingType;
        public IntPtr pbCertEncoded;
        public uint cbCertEncoded;
        public IntPtr pCertInfo;
        public IntPtr hCertStore;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptBlob
    {
        public uint cbData;
        public IntPtr pbData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertInfo
    {
        public uint dwVersion;
        public CryptBlob SerialNumber;
        public CryptAlgorithmIdentifier SignatureAlgorithm;
        public CryptBlob Issuer;
        public long NotBefore;
        public long NotAfter;
        public CryptBlob Subject;
        public CertPublicKeyInfo SubjectPublicKeyInfo;
        public CryptBlob IssuerUniqueId;
        public CryptBlob SubjectUniqueId;
        public uint cExtension;
        public IntPtr rgExtension;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAlgorithmIdentifier
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pszObjId;
        public CryptBlob Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertPublicKeyInfo
    {
        public CryptAlgorithmIdentifier Algorithm;
        public CryptBitBlob PublicKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptBitBlob
    {
        public uint cbData;
        public IntPtr pbData;
        public uint cUnusedBits;
    }

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptQueryObject(
        uint objectType, [MarshalAs(UnmanagedType.LPWStr)] string obj, uint expectedContentType, uint expectedFormatType,
        uint flags, out uint encodingType, out uint contentType, out uint formatType, out IntPtr certStore, out IntPtr message,
        out IntPtr context);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptMsgGetParam(IntPtr message, uint paramType, uint index, IntPtr data, ref uint size);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern IntPtr CertFindCertificateInStore(
        IntPtr certStore, uint encodingType, uint findFlags, uint findType, IntPtr findPara, IntPtr prevContext);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CertNameToStr(uint encodingType, ref CryptBlob name, uint strType, StringBuilder? text, uint textSize);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertFreeCertificateContext(IntPtr context);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertCloseStore(IntPtr certStore, uint flags);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptMsgClose(IntPtr message);
}

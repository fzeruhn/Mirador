using Mirador.Ipc;

namespace Mirador.Helper.Tests;

/// <summary>
/// The real <see cref="WinTrustSigner"/> (WinVerifyTrust and the crypto API) on real files: a broken marshalling or a
/// wrong constant makes every file look unsigned (or throws), which would refuse every consent and every elevated Start.
/// </summary>
public class WinTrustSignerTests
{
    [Fact]
    public void Microsoft_signed_kernel32_has_a_subject()
    {
        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");

        var subject = WinTrustSigner.SubjectOf(kernel32);

        Assert.NotNull(subject);
        Assert.Contains("CN=Microsoft Windows", subject);
        Assert.Contains("O=Microsoft Corporation", subject);
    }

    [Fact]
    public void Microsoft_signed_runtime_dll_has_a_subject()
    {
        // The runtime's own CoreLib carries an Authenticode signature in the file, as Valve's DLLs carry Valve's.
        var subject = WinTrustSigner.SubjectOf(typeof(object).Assembly.Location);

        Assert.NotNull(subject);
        Assert.Contains("O=Microsoft Corporation", subject);
    }

    [Fact]
    public void Unsigned_dll_has_no_subject()
    {
        using var dir = new TempDir();

        // This test assembly is a real PE file with no signature.
        var unsigned = dir.File("unsigned.dll");
        File.Copy(typeof(WinTrustSignerTests).Assembly.Location, unsigned);

        Assert.Null(WinTrustSigner.SubjectOf(unsigned));
    }

    [Fact]
    public void Missing_file_has_no_subject()
    {
        using var dir = new TempDir();

        Assert.Null(WinTrustSigner.SubjectOf(dir.File("missing.dll")));
    }
}

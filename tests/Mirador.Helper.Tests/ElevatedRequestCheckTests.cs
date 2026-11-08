using Mirador.Helper.ElevatedMonado;

namespace Mirador.Helper.Tests;

public class ElevatedRequestCheckTests
{
    private const string InstallDir = @"C:\Program Files\Mirador";
    private static readonly string Mirador = Path.Combine(InstallDir, "Mirador.exe");

    [Fact]
    public void Mirador_from_the_install_folder_with_consent_passes() =>
        Assert.Null(ElevatedRequestCheck.Check(consented: true, imagePath: Mirador, InstallDir, hasLinkedToken: true, running: false));

    [Fact]
    public void The_image_is_compared_without_case_after_normalising_the_path()
    {
        // A different spelling of the same file: upper case, a redundant segment.
        var odd = Path.Combine(InstallDir, "sub", "..", "MIRADOR.EXE").ToUpperInvariant();

        Assert.Null(ElevatedRequestCheck.Check(consented: true, imagePath: odd, InstallDir, hasLinkedToken: true, running: false));
    }

    [Fact]
    public void Without_consent_it_is_refused() =>
        Assert.Equal(
            ElevatedRequestCheck.NotAllowed,
            ElevatedRequestCheck.Check(consented: false, imagePath: Mirador, InstallDir, hasLinkedToken: true, running: false));

    [Fact]
    public void Another_program_is_refused() =>
        Assert.Equal(
            ElevatedRequestCheck.NotMirador,
            ElevatedRequestCheck.Check(consented: true, imagePath: @"C:\Games\game.exe", InstallDir, hasLinkedToken: true, running: false));

    [Fact]
    public void A_caller_whose_image_is_unknown_is_refused() =>
        Assert.Equal(
            ElevatedRequestCheck.NotMirador,
            ElevatedRequestCheck.Check(consented: true, imagePath: null, InstallDir, hasLinkedToken: true, running: false));

    [Fact]
    public void A_standard_account_is_refused() =>
        Assert.Equal(
            ElevatedRequestCheck.NoAdmin,
            ElevatedRequestCheck.Check(consented: true, imagePath: Mirador, InstallDir, hasLinkedToken: false, running: false));

    [Fact]
    public void A_second_start_is_refused_while_one_runs() =>
        Assert.Equal(
            ElevatedRequestCheck.AlreadyRunning,
            ElevatedRequestCheck.Check(consented: true, imagePath: Mirador, InstallDir, hasLinkedToken: true, running: true));
}

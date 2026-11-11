namespace Mirador.Core.Tests;

public class AppArgsTests
{
    [Fact]
    public void No_arguments()
    {
        Assert.Equal(new AppArgs(false, false), AppArgs.Parse([]));
    }

    [Fact]
    public void Start_and_background()
    {
        Assert.Equal(new AppArgs(true, true), AppArgs.Parse(["--background", "--start"]));
    }

    [Fact]
    public void The_old_set_runtime_step_is_gone_and_its_manifest_is_ignored()
    {
        Assert.Equal(new AppArgs(false, false), AppArgs.Parse(["--set-runtime", @"C:\Users\Zoë\My Builds\monado\build\openxr_monado-dev.json"]));
    }

    [Fact]
    public void Parse_restore_openvr()
    {
        Assert.Equal(new AppArgs(false, false, RestoreOpenVr: true), AppArgs.Parse(["--restore-openvr"]));
        Assert.True(AppArgs.Parse(["--Restore-OpenVR"]).RestoreOpenVr);
        Assert.False(AppArgs.Parse(["--start"]).RestoreOpenVr);
    }

    [Fact]
    public void Parses_sign_in_and_remove_sign_in()
    {
        Assert.True(AppArgs.Parse(["--sign-in"]).SignIn);
        Assert.False(AppArgs.Parse(["--sign-in"]).RemoveSignIn);
        Assert.True(AppArgs.Parse(["--Remove-Sign-In"]).RemoveSignIn);
        Assert.False(AppArgs.Parse(["--remove-sign-in"]).SignIn);
        Assert.False(AppArgs.Parse(["--start"]).SignIn);
    }

    [Fact]
    public void Case_doesnt_matter_and_unknown_arguments_are_ignored()
    {
        Assert.Equal(new AppArgs(true, true), AppArgs.Parse(["--START", "-Embedding", "--Background", "x"]));
    }

    [Fact]
    public void Parses_allow_elevated_monado()
    {
        Assert.True(AppArgs.Parse(["--allow-elevated-monado"]).AllowElevatedMonado);
        Assert.True(AppArgs.Parse(["--Allow-Elevated-Monado"]).AllowElevatedMonado);
        Assert.False(AppArgs.Parse(["--start"]).AllowElevatedMonado);
        Assert.False(AppArgs.Parse([]).AllowElevatedMonado);
    }
}

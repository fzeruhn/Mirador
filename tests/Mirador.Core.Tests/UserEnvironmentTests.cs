namespace Mirador.Core.Tests;

public class UserEnvironmentTests
{
    [Fact]
    public void A_block_gives_its_variables_without_the_drive_folders()
    {
        var env = UserEnvironment.Parse("=C:=C:\\Users\\finn\0Path=C:\\Windows;C:\\Tools\0USERNAME=finn\0EMPTY=\0A=b=c\0\0");

        Assert.Equal(
            new Dictionary<string, string> { ["Path"] = @"C:\Windows;C:\Tools", ["USERNAME"] = "finn", ["EMPTY"] = "", ["A"] = "b=c" },
            env);
        Assert.Equal("finn", env["username"]);
    }

    [Fact]
    public void The_users_environment_has_what_windows_gives_every_program()
    {
        // Windows only: the block comes from the signed-in user's profile.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var env = UserEnvironment.Default();

        Assert.True(env.ContainsKey("SystemRoot"));
        Assert.True(env.ContainsKey("USERPROFILE"));
        Assert.True(env.ContainsKey("Path"));
    }
}

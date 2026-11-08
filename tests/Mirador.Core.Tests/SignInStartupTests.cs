using Mirador.Core.SignIn;

namespace Mirador.Core.Tests;

public class SignInStartupTests
{
    private const string Exe = @"C:\Program Files\Mirador\Mirador.exe";

    private sealed class FakeRunKeys : IRunKeys
    {
        public Dictionary<string, string> Run { get; } = [];

        public Dictionary<string, byte[]> Approved { get; } = [];

        public string? GetRun(string name) => Run.GetValueOrDefault(name);

        public void SetRun(string name, string command) => Run[name] = command;

        public void DeleteRun(string name) => Run.Remove(name);

        public byte[]? GetApproved(string name) => Approved.GetValueOrDefault(name);

        public void DeleteApproved(string name) => Approved.Remove(name);
    }

    private static byte[] Approval(byte first) => [first, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void Off_without_a_run_value()
    {
        Assert.False(new SignInStartup(new FakeRunKeys(), Exe).IsOn);
    }

    [Fact]
    public void Turn_on_writes_the_quoted_exe_with_sign_in()
    {
        var keys = new FakeRunKeys();
        var startup = new SignInStartup(keys, Exe);

        startup.TurnOn();

        Assert.Equal(@"""C:\Program Files\Mirador\Mirador.exe"" --sign-in", keys.Run[SignInStartup.ValueName]);
        Assert.Equal(startup.Command, keys.Run["Mirador"]);
        Assert.True(startup.IsOn);
    }

    [Fact]
    public void Turn_on_clears_task_managers_disable()
    {
        var keys = new FakeRunKeys();
        keys.Approved["Mirador"] = Approval(0x03);
        var startup = new SignInStartup(keys, Exe);

        startup.TurnOn();

        Assert.Null(keys.GetApproved("Mirador"));
        Assert.True(startup.IsOn);
    }

    [Theory]
    [InlineData(0x03)]
    [InlineData(0x07)]
    public void Off_when_task_manager_disabled_it(byte first)
    {
        var keys = new FakeRunKeys();
        var startup = new SignInStartup(keys, Exe);
        startup.TurnOn();
        keys.Approved["Mirador"] = Approval(first);

        Assert.False(startup.IsOn);
    }

    [Theory]
    [InlineData(0x02)]
    [InlineData(0x06)]
    public void On_when_task_manager_enabled_it(byte first)
    {
        var keys = new FakeRunKeys();
        var startup = new SignInStartup(keys, Exe);
        startup.TurnOn();
        keys.Approved["Mirador"] = Approval(first);

        Assert.True(startup.IsOn);
    }

    [Fact]
    public void Off_when_the_value_names_another_exe()
    {
        var keys = new FakeRunKeys();
        keys.Run["Mirador"] = @"""D:\Old\Mirador.exe"" --sign-in";

        Assert.False(new SignInStartup(keys, Exe).IsOn);
    }

    [Fact]
    public void The_path_is_compared_without_case()
    {
        var keys = new FakeRunKeys();
        keys.Run["Mirador"] = @"""c:\program files\mirador\MIRADOR.EXE"" --sign-in";

        Assert.True(new SignInStartup(keys, Exe).IsOn);
    }

    [Fact]
    public void Turn_off_deletes_the_run_value()
    {
        var keys = new FakeRunKeys();
        var startup = new SignInStartup(keys, Exe);
        startup.TurnOn();

        startup.TurnOff();

        Assert.False(keys.Run.ContainsKey("Mirador"));
        Assert.False(startup.IsOn);
    }
}

using System.IO.Pipes;
using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Monado;

public class MonadoPipeTests
{
    private static Dictionary<string, string> Env(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value);

    private static readonly string WindowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Fact]
    public void Pipe_name_matches_monado() =>
        Assert.Equal(@"\\.\pipe\C:\Users\finn\AppData\Local\Temp\/monado_comp_ipc",
            new MonadoPipe().NameFor(Env(("TMP", @"C:\Users\finn\AppData\Local\Temp"))));

    [Fact]
    public void Pipe_name_follows_xdg_runtime_dir() =>
        Assert.Equal(@"\\.\pipe\D:\rt/monado_comp_ipc",
            new MonadoPipe().NameFor(Env(("XDG_RUNTIME_DIR", @"D:\rt"), ("XDG_CACHE_HOME", @"D:\cache"), ("TMP", @"C:\Temp"))));

    [Fact]
    public void Pipe_name_follows_xdg_cache_home_when_there_is_no_runtime_dir() =>
        Assert.Equal(@"\\.\pipe\D:\cache/monado_comp_ipc",
            new MonadoPipe().NameFor(Env(("XDG_CACHE_HOME", @"D:\cache"), ("TMP", @"C:\Temp"))));

    [Fact]
    public void Empty_xdg_variables_count_as_unset() =>
        Assert.Equal(@"\\.\pipe\C:\Temp\/monado_comp_ipc",
            new MonadoPipe().NameFor(Env(("XDG_RUNTIME_DIR", ""), ("XDG_CACHE_HOME", ""), ("TMP", @"C:\Temp"))));

    [Fact]
    public void Variable_names_are_matched_ignoring_case() =>
        Assert.Equal(@"\\.\pipe\D:\rt/monado_comp_ipc",
            new MonadoPipe().NameFor(Env(("xdg_runtime_dir", @"D:\rt"))));

    [Fact]
    public void Temp_path_gets_a_trailing_backslash() =>
        Assert.Equal(@"C:\Temp\", MonadoPipe.TempPathFrom(Env(("TMP", @"C:\Temp"))));

    [Fact]
    public void Temp_path_keeps_an_existing_trailing_backslash() =>
        Assert.Equal(@"C:\Temp\", MonadoPipe.TempPathFrom(Env(("TMP", @"C:\Temp\"))));

    [Fact]
    public void Temp_path_prefers_tmp_then_temp_then_userprofile()
    {
        var all = Env(("TMP", @"C:\a"), ("TEMP", @"C:\b"), ("USERPROFILE", @"C:\c"));

        Assert.Equal(@"C:\a\", MonadoPipe.TempPathFrom(all));

        all.Remove("TMP");
        Assert.Equal(@"C:\b\", MonadoPipe.TempPathFrom(all));

        all.Remove("TEMP");
        Assert.Equal(@"C:\c\", MonadoPipe.TempPathFrom(all));
    }

    [Fact]
    public void Temp_path_skips_empty_variables() =>
        Assert.Equal(@"C:\b\", MonadoPipe.TempPathFrom(Env(("TMP", ""), ("TEMP", @"C:\b"))));

    [Fact]
    public void Temp_path_falls_back_to_the_windows_folder() =>
        Assert.Equal(WindowsFolder + @"\", MonadoPipe.TempPathFrom(Env(("SystemRoot", @"C:\Windows"))));

    [Fact]
    public void A_missing_pipe_does_not_exist() =>
        Assert.False(new MonadoPipe().Exists(@"\\.\pipe\mirador-test-" + Guid.NewGuid()));

    [Fact]
    public void A_pipe_that_waits_for_a_client_exists()
    {
        var name = "mirador-test-" + Guid.NewGuid();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Assert.True(new MonadoPipe().Exists(@"\\.\pipe\" + name));
    }

    [Fact]
    public async Task A_pipe_whose_only_instance_is_busy_exists()
    {
        var name = "mirador-test-" + Guid.NewGuid();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        await client.ConnectAsync(5000);

        Assert.True(new MonadoPipe().Exists(@"\\.\pipe\" + name));
    }

    [Fact]
    public void A_pipe_under_the_name_monado_uses_is_found()
    {
        var pipe = new MonadoPipe();
        var full = pipe.NameFor(Env(("XDG_RUNTIME_DIR", @"C:\mirador-test-" + Guid.NewGuid().ToString("N"))));
        using var server = new NamedPipeServerStream(full[@"\\.\pipe\".Length..], PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Assert.True(pipe.Exists(full));
    }
}

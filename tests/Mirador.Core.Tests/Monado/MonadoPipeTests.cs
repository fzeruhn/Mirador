using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
    public void A_folder_outside_ascii_goes_through_the_ansi_code_page_as_in_monado()
    {
        // As on a Western Windows (code page 1252), which has no Ł and gives its best fit, L.
        var pipe = new MonadoPipe(folder => folder.Replace('\u0141', 'L'));

        Assert.Equal(@"\\.\pipe\C:\Users\Lukasz\AppData\Local\Temp\/monado_comp_ipc",
            pipe.NameFor(Env(("TMP", "C:\\Users\\\u0141ukasz\\AppData\\Local\\Temp"))));
    }

    [Fact]
    public void An_xdg_folder_outside_ascii_goes_through_the_ansi_code_page_too()
    {
        var pipe = new MonadoPipe(folder => folder.Replace('\u7fd4', '?'));

        Assert.Equal(@"\\.\pipe\D:\?/monado_comp_ipc", pipe.NameFor(Env(("XDG_RUNTIME_DIR", "D:\\\u7fd4"))));
    }

    [Fact]
    public void An_ascii_folder_is_used_as_it_is() =>
        Assert.Equal(@"\\.\pipe\C:\Temp\/monado_comp_ipc",
            new MonadoPipe(_ => throw new InvalidOperationException("no conversion needed")).NameFor(Env(("TMP", @"C:\Temp"))));

    [Fact]
    public void The_ansi_round_trip_keeps_ascii_and_loses_what_the_code_page_lacks()
    {
        Assert.Equal(@"C:\Users\finn\AppData\Local\Temp\", Mirador.Ipc.AnsiCodePage.RoundTrip(@"C:\Users\finn\AppData\Local\Temp\"));
        Assert.Equal("", Mirador.Ipc.AnsiCodePage.RoundTrip(""));
        if (GetACP() != 65001)
        {
            // No ANSI code page but UTF-8 has an emoji.
            Assert.DoesNotContain("\U0001F600", Mirador.Ipc.AnsiCodePage.RoundTrip("C:\\Users\\\U0001F600\\Temp\\"));
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

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

    [Fact]
    public void A_pipe_created_under_the_literal_name_monado_uses_is_found()
    {
        // The usual case: no XDG variable, so the name has the temp path's trailing backslash followed by "/".
        var pipe = new MonadoPipe();
        var literal = pipe.NameFor(Env(("TMP", @"C:\mirador-test-" + Guid.NewGuid().ToString("N"))));
        Assert.Contains(@"\/monado_comp_ipc", literal);

        // Monado calls CreateNamedPipe with that string as it is. Windows files the pipe under the normalized name
        // (the "/" becomes "\" and the doubled backslash collapses), and a wait on the literal string would not find it.
        using var server = CreateNamedPipe(literal);
        Assert.False(server.IsInvalid, "CreateNamedPipe failed: " + Marshal.GetLastWin32Error());

        Assert.True(pipe.Exists(literal));
    }

    [Fact]
    public void A_missing_pipe_with_a_monado_style_name_does_not_exist()
    {
        var pipe = new MonadoPipe();

        Assert.False(pipe.Exists(pipe.NameFor(Env(("TMP", @"C:\mirador-test-" + Guid.NewGuid().ToString("N"))))));
    }

    /// <summary>What Monado does to open its pipe: the plain Win32 call, with the name untouched.</summary>
    private static SafeFileHandle CreateNamedPipe(string name) =>
        CreateNamedPipeW(name, 0x3 /* PIPE_ACCESS_DUPLEX */, 0 /* byte stream, blocking */, 1, 4096, 4096, 0, IntPtr.Zero);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateNamedPipeW(
        string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint defaultTimeout, IntPtr security);
}

using Mirador.Core.DirectMode;
namespace Mirador.Core.Tests.DirectMode;

public class DisplayCheckTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private const string Exe = "mirador-display-check.exe";

    private static Task<DisplayCheckResult> Run(int exitCode, params string[] lines)
    {
        var starter = new FakeConsoleStarter(_ =>
        {
            var check = new FakeConsole(lines);
            check.End(exitCode);
            return check;
        });
        return DisplayCheck.RunAsync(Exe, starter.Start, CancellationToken.None).WaitAsync(Limit);
    }

    [Fact]
    public async Task A_pass_reads_its_line()
    {
        var result = await Run(0, "[dvl] info: driver 621.01 (tested)", "PASS: 1800 flips in 20.0 s, 0 missed");

        Assert.Equal(
            new DisplayCheckResult(true, "1800 flips in 20.0 s, 0 missed", null, "[dvl] info: driver 621.01 (tested)\nPASS: 1800 flips in 20.0 s, 0 missed\n"),
            result);
    }

    [Fact]
    public async Task A_fail_reads_its_line_and_what_to_do()
    {
        var result = await Run(
            1,
            "[dvl] warn: driver 622.10 is not tested",
            "FAIL: NVAPI call 0x1F2E3D4C is missing in this driver",
            "what it means: NVIDIA moved the private interface direct mode uses",
            "what to do: go back to driver 621.01, or wait for a layer update");

        Assert.False(result.Pass);
        Assert.Equal("NVAPI call 0x1F2E3D4C is missing in this driver", result.Message);
        Assert.Equal("go back to driver 621.01, or wait for a layer update", result.WhatToDo);
    }

    [Fact]
    public async Task The_exit_code_decides_and_the_last_verdict_line_counts()
    {
        var result = await Run(2, "PASS: the NVAPI part works", "FAIL: no flips within 20 s");

        Assert.False(result.Pass);
        Assert.Equal("no flips within 20 s", result.Message);
    }

    [Fact]
    public async Task Without_a_verdict_line_the_exit_code_speaks()
    {
        Assert.Equal("The display check failed (exit code 3).", (await Run(3, "something else")).Message);
        Assert.Equal("Direct mode works.", (await Run(0)).Message);
    }

    [Fact]
    public async Task A_word_ending_in_pass_isnt_a_verdict()
    {
        var result = await Run(0, "[dvl] info: BYPASS: off", "PASS: fine");

        Assert.Equal("fine", result.Message);
    }

    [Fact]
    public async Task A_check_that_wont_start_is_a_fail_with_the_reason()
    {
        var starter = new FakeConsoleStarter(_ => new FakeConsole()) { Refusal = new Core.MiradorException("Couldn't start mirador-display-check.exe: not found.") };

        var result = await DisplayCheck.RunAsync(Exe, starter.Start, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(new DisplayCheckResult(false, "Couldn't start mirador-display-check.exe: not found.", null, "") { Finished = false }, result);
    }

    [Fact]
    public async Task It_runs_in_its_own_folder_with_its_input_closed()
    {
        var starter = new FakeConsoleStarter(_ =>
        {
            var check = new FakeConsole("PASS: ok");
            check.End(0);
            return check;
        });
        var exe = Path.Combine(Path.GetTempPath(), "layer", Exe);

        await DisplayCheck.RunAsync(exe, starter.Start, CancellationToken.None).WaitAsync(Limit);

        var spec = Assert.Single(starter.Specs);
        Assert.Equal((exe, "", Path.Combine(Path.GetTempPath(), "layer")), (spec.FileName, spec.Arguments, spec.WorkingDirectory));
        Assert.True(starter.Last!.InputClosed);
        Assert.True(starter.Last.Disposed);
    }

    [Fact]
    public async Task Cancelling_ends_the_check()
    {
        var starter = new FakeConsoleStarter(_ => new FakeConsole());
        using var cts = new CancellationTokenSource();
        var run = DisplayCheck.RunAsync(Exe, starter.Start, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Limit));
        Assert.True(starter.Last!.Killed);
    }
}

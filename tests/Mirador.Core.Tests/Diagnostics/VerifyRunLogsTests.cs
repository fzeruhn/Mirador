using Mirador.Core.Diagnostics;

namespace Mirador.Core.Tests.Diagnostics;

public class VerifyRunLogsTests
{
    private static string Name(int day) => $"verify-run-202611{day:00}-120000.log";

    [Fact]
    public void Keeps_the_newest_runs_and_those_a_result_names()
    {
        using var dir = new TempDir();
        for (var day = 1; day <= 14; day++)
        {
            dir.Touch(Name(day));
        }

        dir.Touch("verify-monado-20261101-120000.log");
        dir.Touch("monado-20261101-120000.log");

        // Day 2's run is still a profile's result; a result whose log is gone, or none, changes nothing.
        VerifyRunLogs.Prune(dir.Path, [dir.File(Name(2)), dir.File("verify-run-20261001-120000.log"), null]);

        var left = Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            [
                "monado-20261101-120000.log",
                "verify-monado-20261101-120000.log",
                Name(2),
                .. Enumerable.Range(6, 9).Select(Name),
            ],
            left);
    }

    [Fact]
    public void Fewer_runs_than_kept_stay()
    {
        using var dir = new TempDir();
        dir.Touch(Name(1));

        VerifyRunLogs.Prune(dir.Path, []);

        Assert.True(File.Exists(dir.File(Name(1))));
    }

    [Fact]
    public void A_missing_folder_is_no_error()
    {
        using var dir = new TempDir();
        VerifyRunLogs.Prune(dir.File("logs"), []);
    }
}

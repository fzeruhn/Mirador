using System.IO.Compression;
using Mirador.Core.Diagnostics;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Tests.Session;

namespace Mirador.Core.Tests.Diagnostics;

public class DiagnosticsZipTests
{
    private static Dictionary<string, string> Read(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    private static string Collect(Harness h, ModuleContext ctx)
    {
        var zipPath = Path.Combine(h.Root, "diagnostics.zip");
        DiagnosticsZip.Collect(h.Paths, Harness.DreamAir, h.Controller.LastMiradorEnvironment, "Mirador 0.1.0\n", h.Module, ctx).Save(zipPath);
        return zipPath;
    }

    private static ModuleContext Context(Harness h) => new(h.Paths, new FakeHelper(), h.Processes, new FakeUsb(), TimeProvider.System);

    [Fact]
    public async Task After_a_start_the_zip_holds_the_logs_and_what_monado_got()
    {
        await using var h = new Harness();
        h.Settings.Save(new MonadoSettings(RenderScalePercent: 120));
        h.Launcher.FirstLines = ["INFO [main] Monado service starting"];
        await h.StartRunningAsync();
        Directory.CreateDirectory(h.Paths.LogsDir);
        File.WriteAllText(h.Paths.AppLogFile, "2026-11-04 12:00:00 Mirador started\n");

        var entries = Read(Collect(h, Context(h)));

        Assert.Equal(
            ["environment.txt", "mirador.log", "monado-settings.json", "monado.log", "profile.json", "versions.txt"],
            entries.Keys.Where(k => k != "fake.txt").Order());
        Assert.Contains("XRT_COMPOSITOR_SCALE_PERCENTAGE=120\n", entries["environment.txt"]);
        Assert.DoesNotContain("USERPROFILE", entries["environment.txt"]); // only what Mirador set
        Assert.Contains("\"name\": \"Dream Air\"", entries["profile.json"]);
        Assert.Equal("Mirador 0.1.0\n", entries["versions.txt"]);
        Assert.Contains("120", entries["monado-settings.json"]);
        Assert.Contains("Monado service starting", entries["monado.log"]);
        Assert.False(entries.ContainsKey("missing.txt"));
    }

    [Fact]
    public async Task Without_a_monado_log_it_is_named_as_missing()
    {
        await using var h = new Harness();

        var entries = Read(Collect(h, Context(h)));

        Assert.False(entries.ContainsKey("monado.log"));
        Assert.Contains("monado.log: ", entries["missing.txt"]);
        Assert.Contains("mirador.log: ", entries["missing.txt"]);
        Assert.Equal("Monado hasn't been started since Mirador started.\n", entries["environment.txt"]);
    }

    [Fact]
    public void The_newest_monado_log_is_the_one()
    {
        using var dir = new TempDir();
        foreach (var name in new[] { "monado-20261103-221500.log", "monado-20261104-090000.log", "monado-20261104-085959.log", "mirador.log" })
        {
            File.WriteAllText(dir.File(name), name);
        }

        Assert.Equal(dir.File("monado-20261104-090000.log"), DiagnosticsZip.NewestMonadoLog(dir.Path));
    }

    [Fact]
    public void A_log_being_written_is_read_as_it_is()
    {
        using var dir = new TempDir();
        using var log = new TextLog(dir.File("mirador.log"));
        log.Write("first line");
        var zip = new DiagnosticsZip();
        zip.AddFile("mirador.log", dir.File("mirador.log"));

        zip.Save(dir.File("d.zip"));

        Assert.Equal($"first line{Environment.NewLine}", Read(dir.File("d.zip"))["mirador.log"]);
    }
}

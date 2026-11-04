using Mirador.Core.Runtime;
using Mirador.Core.Tests.Monado;

namespace Mirador.Core.Tests.Runtime;

public class SetRuntimeCommandTests
{
    // Nothing here touches the registry: the writer is a delegate that records what it was given.
    private sealed class RecordingWriter
    {
        public List<string> Written { get; } = [];

        public void Write(string manifest) => Written.Add(manifest);
    }

    private static string GoodManifest(TempDir dir)
    {
        File.WriteAllText(dir.File("openxr_monado.dll"), "");
        File.WriteAllText(dir.File("openxr_monado.json"), TestManifest.Json("./openxr_monado.dll"));
        return dir.File("openxr_monado.json");
    }

    [Fact]
    public void The_flag_is_the_one_the_app_relaunches_itself_with()
    {
        Assert.Equal("--set-runtime", SetRuntimeCommand.Flag);
    }

    [Fact]
    public void A_good_manifest_is_written_by_its_full_path()
    {
        using var dir = new TempDir();
        var manifest = GoodManifest(dir);
        var writer = new RecordingWriter();
        var error = new StringWriter();

        var code = SetRuntimeCommand.Run(manifest, writer.Write, error);

        Assert.Equal(0, code);
        Assert.Equal([manifest], writer.Written);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void A_relative_path_is_written_as_a_full_path()
    {
        using var dir = new TempDir();
        var manifest = GoodManifest(dir);
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, manifest);
        var writer = new RecordingWriter();

        var code = SetRuntimeCommand.Run(relative, writer.Write, new StringWriter());

        Assert.Equal(0, code);
        Assert.Equal([Path.GetFullPath(relative)], writer.Written);
        Assert.True(Path.IsPathFullyQualified(writer.Written[0]));
    }

    [Fact]
    public void A_manifest_that_does_not_exist_is_refused_and_nothing_is_written()
    {
        using var dir = new TempDir();
        var writer = new RecordingWriter();
        var error = new StringWriter();

        var code = SetRuntimeCommand.Run(dir.File("openxr_monado.json"), writer.Write, error);

        Assert.Equal(1, code);
        Assert.Empty(writer.Written);
        Assert.Contains(dir.File("openxr_monado.json"), error.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void No_manifest_path_is_refused_and_nothing_is_written(string manifest)
    {
        var writer = new RecordingWriter();

        var code = SetRuntimeCommand.Run(manifest, writer.Write, new StringWriter());

        Assert.Equal(1, code);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public void A_manifest_that_is_not_json_is_refused_and_nothing_is_written()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("openxr_monado.json"), "{ nope");
        var writer = new RecordingWriter();
        var error = new StringWriter();

        var code = SetRuntimeCommand.Run(dir.File("openxr_monado.json"), writer.Write, error);

        Assert.Equal(1, code);
        Assert.Empty(writer.Written);
        Assert.Contains("JSON", error.ToString());
    }

    [Fact]
    public void A_manifest_whose_runtime_dll_is_missing_is_refused_and_nothing_is_written()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("openxr_monado.json"), TestManifest.Json("./openxr_monado.dll"));
        var writer = new RecordingWriter();
        var error = new StringWriter();

        var code = SetRuntimeCommand.Run(dir.File("openxr_monado.json"), writer.Write, error);

        Assert.Equal(1, code);
        Assert.Empty(writer.Written);
        Assert.Contains(dir.File("openxr_monado.dll"), error.ToString());
    }

    [Fact]
    public void A_library_path_that_is_not_a_valid_path_is_refused_and_nothing_is_written()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("openxr_monado.json"), TestManifest.Json("a\0b"));
        var writer = new RecordingWriter();
        var error = new StringWriter();

        var code = SetRuntimeCommand.Run(dir.File("openxr_monado.json"), writer.Write, error);

        Assert.Equal(1, code);
        Assert.Empty(writer.Written);
        Assert.Contains(dir.File("openxr_monado.json"), error.ToString());
        Assert.Contains("library_path", error.ToString());
    }

    [Fact]
    public void A_manifest_that_cannot_be_read_is_refused_and_nothing_is_written()
    {
        using var dir = new TempDir();
        var manifest = GoodManifest(dir);
        using var lock_ = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None);
        var writer = new RecordingWriter();
        var error = new StringWriter();

        var code = SetRuntimeCommand.Run(manifest, writer.Write, error);

        Assert.Equal(1, code);
        Assert.Empty(writer.Written);
        Assert.Contains(manifest, error.ToString());
    }

    [Fact]
    public void A_file_that_is_not_a_runtime_manifest_at_all_is_refused()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("notes.json"), """{"hello": "world"}""");
        var writer = new RecordingWriter();

        var code = SetRuntimeCommand.Run(dir.File("notes.json"), writer.Write, new StringWriter());

        Assert.Equal(1, code);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public void A_failing_write_is_reported_and_gives_code_2()
    {
        using var dir = new TempDir();
        var manifest = GoodManifest(dir);
        var error = new StringWriter();

        var code = SetRuntimeCommand.Run(manifest, _ => throw new UnauthorizedAccessException("Access to the registry key is denied."), error);

        Assert.Equal(2, code);
        Assert.Contains("denied", error.ToString());
        Assert.Contains("unchanged", error.ToString());
    }

    [Fact]
    public void The_overload_the_app_calls_takes_just_the_manifest_and_the_writer()
    {
        using var dir = new TempDir();
        var manifest = GoodManifest(dir);
        var writer = new RecordingWriter();

        var code = SetRuntimeCommand.Run(manifest, writer.Write);

        Assert.Equal(0, code);
        Assert.Equal([manifest], writer.Written);
    }
}

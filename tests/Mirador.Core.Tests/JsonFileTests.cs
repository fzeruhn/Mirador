using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mirador.Core.Tests;

public partial class JsonFileTests
{
    [Fact]
    public void Save_replaces_a_file_a_reader_holds_open_the_whole_time()
    {
        using var dir = new TempDir();
        var path = dir.File("file.json");
        JsonFile.Save(path, new Sample(1), SampleJsonContext.Default.Sample);

        // As Load opens it: shared with writers and deleters.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            JsonFile.Save(path, new Sample(2), SampleJsonContext.Default.Sample);
            Assert.Equal(1, JsonSerializer.Deserialize(new StreamReader(reader).ReadToEnd(), SampleJsonContext.Default.Sample)!.Value);
        }

        Assert.Equal(2, JsonFile.Load(path, SampleJsonContext.Default.Sample)!.Value);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Save_replaces_a_file_being_read()
    {
        using var dir = new TempDir();
        var path = dir.File("file.json");
        JsonFile.Save(path, new Sample(1), SampleJsonContext.Default.Sample);

        // Load and Save on two threads at once: every save succeeds and every load finds the file, whole.
        var saving = true;
        var loads = 0;
        var wrong = new List<string>();
        using var reading = new ManualResetEventSlim();
        var reads = Task.Run(() =>
        {
            reading.Set();
            while (Volatile.Read(ref saving))
            {
                var loaded = JsonFile.Load(path, SampleJsonContext.Default.Sample);
                loads++;
                if (loaded is not { Value: >= 0 and < 300 })
                {
                    wrong.Add(loaded is null ? "null" : loaded.Value.ToString());
                }
            }
        });
        // The saves start once the reader runs: on a fast machine they could otherwise all be done before it starts.
        reading.Wait();
        try
        {
            for (var i = 0; i < 300; i++)
            {
                JsonFile.Save(path, new Sample(i), SampleJsonContext.Default.Sample);
            }
        }
        finally
        {
            Volatile.Write(ref saving, false);
        }

        await reads;
        Assert.Empty(wrong);
        Assert.True(loads > 0);
        Assert.Equal(new Sample(299), JsonFile.Load(path, SampleJsonContext.Default.Sample));
    }

    public sealed record Sample(int Value);

    [JsonSerializable(typeof(Sample))]
    internal sealed partial class SampleJsonContext : JsonSerializerContext;
}

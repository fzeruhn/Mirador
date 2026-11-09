using System.Text.Json.Serialization;

namespace Mirador.Core.Tests;

public partial class JsonFileTests
{
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

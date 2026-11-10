using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Monado;

public class FileHashCacheTests
{
    [Fact]
    public void A_file_is_hashed_again_only_after_it_changed()
    {
        using var dir = new TempDir();
        var file = dir.File("openxr_monado.dll");
        File.WriteAllText(file, "v1");
        var hashed = 0;
        var cache = new FileHashCache(f =>
        {
            hashed++;
            return File.ReadAllText(f);
        });

        Assert.Equal("v1", cache.HashOf(file));
        Assert.Equal("v1", cache.HashOf(file.ToUpperInvariant()));
        Assert.Equal(1, hashed);

        File.WriteAllText(file, "v2, longer");
        Assert.Equal("v2, longer", cache.HashOf(file));
        Assert.Equal(2, hashed);

        File.WriteAllText(file, "v3, longer");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("v3, longer", cache.HashOf(file));
        Assert.Equal(3, hashed);
    }

    [Fact]
    public void A_missing_file_throws_and_is_not_kept()
    {
        using var dir = new TempDir();
        var hashed = 0;
        var cache = new FileHashCache(f =>
        {
            hashed++;
            return File.ReadAllText(f);
        });

        Assert.ThrowsAny<IOException>(() => cache.HashOf(dir.File("missing.dll")));
        Assert.Equal(0, hashed);

        File.WriteAllText(dir.File("missing.dll"), "now there");
        Assert.Equal("now there", cache.HashOf(dir.File("missing.dll")));
        Assert.Equal(1, hashed);
    }
}

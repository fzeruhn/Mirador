using Mirador.Helper.ElevatedMonado;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

public class PimaxElevatedTests
{
    private static readonly UserFolders Folders = new(@"C:\Users\finn", @"C:\Users\finn\AppData\Roaming", @"C:\Users\finn\AppData\Local", @"C:\Users\finn\AppData\Local\Temp");

    private static IReadOnlyDictionary<string, string> Build(string module) =>
        ElevatedEnvironment.Build(new ElevatedStart(3000, 100, 6, false, module), Folders, @"C:\Program Files\Mirador\monado\server", @"C:\SteamVR", []);

    [Fact]
    public void The_pimax_module_gets_the_dream_airs_table_as_its_former_id_did()
    {
        var pimax = Build(PimaxEnvironment.ModuleId);
        var former = Build(DreamAirEnvironment.ModuleId);

        Assert.Equal(former, pimax);
        foreach (var (name, value) in DreamAirEnvironment.FixedEnvironment)
        {
            Assert.Equal(value, pimax[name]);
        }
    }
}

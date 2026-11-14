namespace Mirador.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Paths_are_under_the_root()
    {
        var p = new AppPaths(@"C:\data\Mirador");
        Assert.Equal(@"C:\data\Mirador\profiles.json", p.ProfilesFile);
        Assert.Equal(@"C:\data\Mirador\monado-settings.json", p.MonadoSettingsFile);
        Assert.Equal(@"C:\data\Mirador\logs\mirador.log", p.AppLogFile);
        Assert.Equal(@"C:\data\Mirador\calibration-backups", p.BackupsDir);
        Assert.Equal(@"C:\data\Mirador\logs\monado-20261103-141502.log",
            p.NewMonadoLogFile(new DateTime(2026, 11, 3, 14, 15, 2)));
    }

    [Fact]
    public void The_other_files_and_folders_are_under_the_root_too()
    {
        var p = new AppPaths(@"C:\data\Mirador");
        Assert.Equal(@"C:\data\Mirador", p.Root);
        Assert.Equal(@"C:\data\Mirador\settings.json", p.SettingsFile);
        Assert.Equal(@"C:\data\Mirador\session.json", p.SessionFile);
        Assert.Equal(@"C:\data\Mirador\dreamair.json", p.DreamAirFile);
        Assert.Equal(@"C:\data\Mirador\logs", p.LogsDir);
        Assert.Equal(@"C:\data\Mirador\geometry", p.GeometryDir);
        Assert.Equal(@"C:\data\Mirador\checks", p.ChecksDir);
    }

    [Fact]
    public void Default_is_Mirador_under_local_app_data()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "Mirador"), AppPaths.Default.Root);
    }

    [Fact]
    public void Monado_log_names_do_not_depend_on_the_culture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("th-TH"); // Buddhist calendar
            var p = new AppPaths(@"C:\data\Mirador");
            Assert.Equal(@"C:\data\Mirador\logs\monado-20261103-141502.log",
                p.NewMonadoLogFile(new DateTime(2026, 11, 3, 14, 15, 2)));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }
}

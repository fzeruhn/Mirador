using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.EventLog;
using Mirador.Ipc;
using Mirador.Helper;
using Mirador.Helper.ElevatedMonado;

// `Mirador.Helper.exe --send-exit-key <pid>`: started by the helper with an elevated Monado's own token, in its session,
// to end it cleanly (ConsoleExitKey). Writes one key into that Monado's console and exits.
if (ConsoleExitKey.Requested(args) is { } monado)
{
    return ConsoleExitKey.Send(monado) is null ? 0 : 1; // no console left to say why: the exit code does
}

// `Mirador.Helper.exe --restore-runtime`: what the uninstaller runs. Puts back the OpenXR runtime Mirador replaced,
// once, and exits.
if (args is ["--restore-runtime"])
{
    var restorer = new HelperHandler(
        new SystemServiceControl(), new SystemProcessControl(), new SystemRuntimeRegistry(),
    monado: MonadoInstaller.ForInstall(new HttpClient(), new SystemRuntimeRegistry(), "0"));
    var result = await restorer.RestoreRuntimeAsync();
    if (!result.Ok)
    {
        Console.Error.WriteLine(result.Error);
        return 1;
    }

    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "MiradorHelper");
builder.Services.Configure<EventLogSettings>(settings => settings.SourceName = "Mirador helper");
builder.Services.AddSingleton<IServiceControl, SystemServiceControl>();
builder.Services.AddSingleton<IProcessControl, SystemProcessControl>();
builder.Services.AddSingleton<IRuntimeRegistry, SystemRuntimeRegistry>();
var elevatedConsent = new ElevatedConsent(new RegistryConsentValues());
builder.Services.AddSingleton(services =>
{
    var registry = services.GetRequiredService<IRuntimeRegistry>();
    var monado = MonadoInstaller.ForInstall(new HttpClient(), registry, HelperHandler.OwnVersionNumber());
    var approved = new RegistryApprovedBuildStore();
    return new HelperHandler(
        services.GetRequiredService<IServiceControl>(),
        services.GetRequiredService<IProcessControl>(),
        registry,
        consent: elevatedConsent,
        elevated: new ElevatedMonadoRunner(elevatedConsent, monado: monado, approved: approved),
        monado: monado,
        approved: approved);
});
builder.Services.AddHostedService<HelperPipeServer>();
builder.Build().Run();
return 0;

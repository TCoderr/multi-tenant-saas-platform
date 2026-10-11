using Kulvar.Api.Hosting;

namespace Kulvar.Api;

/// <summary>
/// Tek image, beş kullanım: API (varsayılan), <c>--worker</c>, <c>--migrate [--grant-operator e-posta]</c>,
/// <c>--seed</c> (yalnız demo), <c>--probe &lt;url&gt;</c>.
/// </summary>
public sealed class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--probe") || args.Contains("--worker-heartbeat"))
        {
            return await HealthProbe.RunAsync(args);
        }
        if (args.Contains("--seed"))
        {
            return await Seeding.SeedHost.RunAsync(args);
        }
        if (args.Contains("--migrate"))
        {
            return await MigratorHost.RunAsync(args);
        }
        if (args.Contains("--worker"))
        {
            return await WorkerHost.RunAsync(args);
        }

        var app = ApiHost.Build(args);
        await app.RunAsync();
        return 0;
    }
}

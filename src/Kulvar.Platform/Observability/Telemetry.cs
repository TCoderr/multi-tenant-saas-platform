using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Kulvar.Platform.Observability;

/// <summary>
/// Yerleşik iz ve metrik kaynakları. Etiketler sabit kümelerden gelir (iş türü, sonuç); şirket, kullanıcı veya müşteri kimliği
/// asla metrik etiketi olmaz, kişisel veri asla iz etiketi olmaz.
/// </summary>
public static class Telemetry
{
    public const string SourceName = "Kulvar";

    public static readonly ActivitySource Activities = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> OutboxProcessed = Meter.CreateCounter<long>("kulvar.outbox.processed");
    public static readonly Counter<long> QuotaRejections = Meter.CreateCounter<long>("kulvar.quota.rejections");
    public static readonly Counter<long> WebhookEvents = Meter.CreateCounter<long>("kulvar.webhook.events");
    public static readonly Counter<long> TenantAccessDenied = Meter.CreateCounter<long>("kulvar.tenant.access_denied");
    public static readonly Counter<long> LoginAttempts = Meter.CreateCounter<long>("kulvar.auth.login_attempts");
    public static readonly Counter<long> ExportsCompleted = Meter.CreateCounter<long>("kulvar.exports.completed");
}

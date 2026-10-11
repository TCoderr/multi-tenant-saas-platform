using System.Text.Json;
using Kulvar.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kulvar.Platform.Audit;

/// <summary>
/// Platform düzeyi denetim kaydı: operatör eylemleri, çözüm bekleyen ödeme sonuçları, webhook çelişkileri, şirket temizleme.
/// Şirket içeriği veya kişisel veri taşımaz; şirket silindikten sonra da kalır.
/// </summary>
public sealed class PlatformAuditEvent
{
    private PlatformAuditEvent() { }

    public Guid Id { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string Action { get; private set; } = null!;
    public Guid? TenantId { get; private set; }
    public string Data { get; private set; } = "{}";
    public DateTimeOffset OccurredAt { get; private set; }

    public static PlatformAuditEvent Create(Guid? actorUserId, string action, Guid? tenantId, object? data, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        ActorUserId = actorUserId,
        Action = action,
        TenantId = tenantId,
        Data = data is null ? "{}" : JsonSerializer.Serialize(data, JsonDefaults.Storage),
        OccurredAt = now,
    };
}

internal sealed class PlatformAuditEventConfiguration : IEntityTypeConfiguration<PlatformAuditEvent>
{
    public void Configure(EntityTypeBuilder<PlatformAuditEvent> b)
    {
        b.ToTable("platform_audit_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Action).HasMaxLength(80);
        b.Property(x => x.Data).HasColumnType("jsonb");
        b.HasIndex(x => new { x.OccurredAt, x.Id }).IsDescending(true, true);
        b.HasIndex(x => x.TenantId);
    }
}

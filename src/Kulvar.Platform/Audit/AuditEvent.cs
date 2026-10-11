using System.Text.Json;
using Kulvar.Platform.Data;
using Kulvar.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kulvar.Platform.Audit;

/// <summary>Şirket denetim kaydı ve talep zaman çizelgesi etkinliği. Yalnız eklenir.</summary>
public sealed class AuditEvent : ITenantOwned
{
    private AuditEvent() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string Action { get; private set; } = null!;
    public string TargetType { get; private set; } = null!;
    public Guid? TargetId { get; private set; }
    public Guid? TicketId { get; private set; }
    public string Data { get; private set; } = "{}";
    public DateTimeOffset OccurredAt { get; private set; }

    public static AuditEvent Create(Guid tenantId, Guid? actorUserId, string action, string targetType, Guid? targetId, Guid? ticketId, object? data, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        TenantId = tenantId,
        ActorUserId = actorUserId,
        Action = action,
        TargetType = targetType,
        TargetId = targetId,
        TicketId = ticketId,
        Data = data is null ? "{}" : JsonSerializer.Serialize(data, JsonDefaults.Storage),
        OccurredAt = now,
    };
}

public static class JsonDefaults
{
    /// <summary>Veritabanında saklanan gövdeler için: camelCase, enum'lar metin.</summary>
    public static JsonSerializerOptions Storage { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Action).HasMaxLength(80);
        b.Property(x => x.TargetType).HasMaxLength(40);
        b.Property(x => x.Data).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.OccurredAt, x.Id }).IsDescending(false, true, true);
        b.HasIndex(x => new { x.TenantId, x.TicketId, x.OccurredAt, x.Id });
    }
}

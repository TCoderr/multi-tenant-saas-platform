using System.Text.Json;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kulvar.Platform.Messaging;

public enum OutboxStatus { Pending, Done, Dead }

/// <summary>
/// İş etkisiyle aynı transaction'da yazılan mesaj. Uygulama rolü yalnız ekleyebilir; worker kiralayıp işler.
/// Şirket kapsamlı (tenant_id) veya kullanıcı kapsamlı (user_id) olabilir. Gövde kişisel veri/sır taşımaz.
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public Guid Id { get; private set; }
    public Guid? TenantId { get; private set; }
    public Guid? UserId { get; private set; }
    public string Type { get; private set; } = null!;
    public string Payload { get; private set; } = "{}";
    public OutboxStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset AvailableAt { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public long LeaseToken { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    public static OutboxMessage Create(Guid? tenantId, Guid? userId, string type, object payload, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        TenantId = tenantId,
        UserId = userId,
        Type = type,
        Payload = JsonSerializer.Serialize(payload, JsonDefaults.Storage),
        Status = OutboxStatus.Pending,
        AvailableAt = now,
        CreatedAt = now,
    };

    public T Read<T>() => JsonSerializer.Deserialize<T>(Payload, JsonDefaults.Storage)
        ?? throw new InvalidOperationException($"Outbox gövdesi okunamadı: {Type}");
}

/// <summary>Demo posta kutusu iletisi: gerçek e-posta yerine geçer. Yalnız alıcı kullanıcı okuyabilir (RLS).</summary>
public sealed class SinkMessage
{
    private SinkMessage() { }

    public Guid Id { get; private set; }
    public Guid? TenantId { get; private set; }
    public string RecipientNormalizedEmail { get; private set; } = null!;
    public string Kind { get; private set; } = null!;
    public string Subject { get; private set; } = null!;
    public string Body { get; private set; } = null!;
    public string? ActionPath { get; private set; }
    public string DedupeKey { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static SinkMessage Create(Guid? tenantId, string recipientNormalizedEmail, string kind, string subject, string body, string? actionPath, string dedupeKey, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        TenantId = tenantId,
        RecipientNormalizedEmail = recipientNormalizedEmail,
        Kind = kind,
        Subject = subject,
        Body = body,
        ActionPath = actionPath,
        DedupeKey = dedupeKey,
        CreatedAt = now,
    };
}

public static class MailSink
{
    /// <summary>
    /// İletiyi ekler; aynı <paramref name="dedupeKey"/> ile ileti zaten varsa hiçbir şey yapmaz ve false döner.
    /// İşleyiciler bunu ilk iş olarak çağırıp false ise diğer etkilerini de (ör. yeni token üretimi) atlayabilir.
    /// </summary>
    public static async Task<bool> AlreadyDeliveredAsync(Data.AppDbContext db, string dedupeKey, CancellationToken ct) =>
        await db.Set<SinkMessage>().AnyAsync(m => m.DedupeKey == dedupeKey, ct);
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages", t => t.HasCheckConstraint("ck_outbox_messages_status", "status IN ('Pending','Done','Dead')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Type).HasMaxLength(100);
        b.Property(x => x.Payload).HasColumnType("jsonb");
        b.Property(x => x.LeaseOwner).HasMaxLength(100);
        b.Property(x => x.LastError).HasMaxLength(200);
        b.HasIndex(x => x.AvailableAt).HasFilter("status = 'Pending'");
        b.HasIndex(x => x.TenantId);
    }
}

internal sealed class SinkMessageConfiguration : IEntityTypeConfiguration<SinkMessage>
{
    public void Configure(EntityTypeBuilder<SinkMessage> b)
    {
        b.ToTable("sink_messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.RecipientNormalizedEmail).HasMaxLength(254);
        b.Property(x => x.Kind).HasMaxLength(60);
        b.Property(x => x.Subject).HasMaxLength(200);
        b.Property(x => x.ActionPath).HasMaxLength(400);
        b.Property(x => x.DedupeKey).HasMaxLength(200);
        b.HasIndex(x => x.DedupeKey).IsUnique();
        b.HasIndex(x => new { x.RecipientNormalizedEmail, x.CreatedAt }).IsDescending(false, true);
        b.HasIndex(x => x.TenantId);
    }
}

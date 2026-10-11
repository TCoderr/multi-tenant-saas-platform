using Kulvar.Platform.Data;
using Kulvar.Platform.Tenancy;

namespace Kulvar.Modules.Billing.Domain;

/// <summary>Plan kodları ve referans plan sürümlerinin sabit kimlikleri (migration bu kimliklerle ekler).</summary>
public static class PlanCatalog
{
    public const string Trial = "trial";
    public const string Starter = "starter";
    public const string Team = "team";
    public const string Currency = "TRY";

    public static readonly Guid TrialV1 = Guid.Parse("018f1000-0000-7000-8000-000000000001");
    public static readonly Guid StarterV1 = Guid.Parse("018f1000-0000-7000-8000-000000000002");
    public static readonly Guid TeamV1 = Guid.Parse("018f1000-0000-7000-8000-000000000003");

    public static readonly TimeSpan TrialLength = TimeSpan.FromDays(14);
    public static readonly TimeSpan GraceLength = TimeSpan.FromDays(7);
}

public sealed class Plan
{
    private Plan() { }

    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public int SortOrder { get; private set; }
}

/// <summary>
/// Planın bir sürümü. Abonelik plan koduna değil sürüme bağlanır: fiyat/limit değişikliği yeni sürümdür,
/// mevcut abonelikler kendi anlık görüntüsünde kalır.
/// </summary>
public sealed class PlanVersion
{
    private PlanVersion() { }

    public Guid Id { get; private set; }
    public string PlanCode { get; private set; } = null!;
    public int Version { get; private set; }
    public long PriceMinor { get; private set; }
    public string Currency { get; private set; } = null!;
    public int SeatLimit { get; private set; }
    public int MonthlyTicketLimit { get; private set; }
    public bool ExportEnabled { get; private set; }
    public int? TrialDays { get; private set; }
    public bool IsCurrent { get; private set; }
}

public enum SubscriptionChange { None, TrialExpired, GraceExpired }

/// <summary>Şirketin aboneliği ve durum makinesi (02 §5). Geçiş kuralları saftır; saat dışarıdan verilir.</summary>
public sealed class Subscription : ITenantOwned
{
    private Subscription() { }

    public Guid TenantId { get; private set; }
    public Guid PlanVersionId { get; private set; }
    public SubscriptionStatus Status { get; private set; }

    /// <summary>İptal ve silme isteğinde artar. Bir ödeme işleminin başarısı yalnız istendiği andaki epoch hâlâ geçerliyse uygulanır.</summary>
    public int Epoch { get; private set; }
    public DateTimeOffset? TrialEndsAt { get; private set; }
    public DateTimeOffset? GraceEndsAt { get; private set; }
    public DateTimeOffset? CurrentPeriodEnd { get; private set; }
    public string? ProviderSubscriptionId { get; private set; }

    /// <summary>Sağlayıcının abonelik olay sırası. Yalnız daha büyük sıra uygulanır; zaman damgasına göre "en yeni" seçilmez.</summary>
    public long ProviderSequence { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; }

    public static Subscription StartTrial(Guid tenantId, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        PlanVersionId = PlanCatalog.TrialV1,
        Status = SubscriptionStatus.Trialing,
        TrialEndsAt = now + PlanCatalog.TrialLength,
        UpdatedAt = now,
        Version = 1,
    };

    /// <summary>Zamana bağlı geçişler: trial bitişi → PastDue (ödemesiz dönem), ödemesiz dönem bitişi → Suspended.</summary>
    public SubscriptionChange AdvanceTime(DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Trialing && TrialEndsAt is { } trialEnd && trialEnd <= now)
        {
            Status = SubscriptionStatus.PastDue;
            GraceEndsAt = trialEnd + PlanCatalog.GraceLength;
            Touch(now);
            return SubscriptionChange.TrialExpired;
        }
        if (Status == SubscriptionStatus.PastDue && GraceEndsAt is { } graceEnd && graceEnd <= now)
        {
            Status = SubscriptionStatus.Suspended;
            Touch(now);
            return SubscriptionChange.GraceExpired;
        }
        return SubscriptionChange.None;
    }

    /// <summary>Başarılı ödeme işlemi uygulandı: plan değişir, abonelik Active olur. Epoch kontrolünü çağıran yapar.</summary>
    public void ApplyPlan(Guid planVersionId, DateTimeOffset now)
    {
        PlanVersionId = planVersionId;
        Status = SubscriptionStatus.Active;
        TrialEndsAt = null;
        GraceEndsAt = null;
        CurrentPeriodEnd = now.AddMonths(1);
        Touch(now);
    }

    public bool IsNewerSequence(long sequence) => sequence > ProviderSequence;

    /// <summary>Sağlayıcı yenileme başarısızlığı bildirdi. Yalnız Active abonelikte anlamlıdır; sıra her durumda ilerler.</summary>
    public bool OnRenewalFailed(long sequence, DateTimeOffset now)
    {
        ProviderSequence = sequence;
        if (Status != SubscriptionStatus.Active)
        {
            Touch(now);
            return false;
        }
        Status = SubscriptionStatus.PastDue;
        GraceEndsAt = now + PlanCatalog.GraceLength;
        Touch(now);
        return true;
    }

    /// <summary>Sağlayıcı yenilemeyi (veya gecikmiş ödemeyi) aldı. İptal edilmiş aboneliği yeniden etkinleştirmez.</summary>
    public bool OnRenewed(long sequence, DateTimeOffset now)
    {
        ProviderSequence = sequence;
        if (Status is not (SubscriptionStatus.Active or SubscriptionStatus.PastDue or SubscriptionStatus.Suspended))
        {
            Touch(now);
            return false;
        }
        Status = SubscriptionStatus.Active;
        GraceEndsAt = null;
        CurrentPeriodEnd = now.AddMonths(1);
        Touch(now);
        return true;
    }

    public void BindProvider(string providerSubscriptionId, long sequence)
    {
        ProviderSubscriptionId = providerSubscriptionId;
        if (sequence > ProviderSequence)
        {
            ProviderSequence = sequence;
        }
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = SubscriptionStatus.Cancelled;
        GraceEndsAt = null;
        Epoch++;
        Touch(now);
    }

    /// <summary>Şirket silme isteği: bekleyen ödeme işlemlerinin geç başarısı artık uygulanamaz.</summary>
    public void InvalidatePendingOperations(DateTimeOffset now)
    {
        Epoch++;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}

/// <summary>Kullanım dönemi: UTC takvim ayı. Yerel saat ve yaz saati geçişleri dönemi etkilemez.</summary>
public static class UsagePeriod
{
    public static DateOnly Of(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        return new DateOnly(utc.Year, utc.Month, 1);
    }

    public static DateTimeOffset ResetsAt(DateTimeOffset instant)
    {
        var start = Of(instant);
        return new DateTimeOffset(start.Year, start.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
    }
}

/// <summary>Dönem sayacı: kotanın karar verildiği tek satır.</summary>
public sealed class UsageCounter : ITenantOwned
{
    private UsageCounter() { }

    public Guid TenantId { get; private set; }
    public string Metric { get; private set; } = null!;
    public DateOnly PeriodStart { get; private set; }
    public int Used { get; private set; }
}

/// <summary>Değişmez kullanım kaydı ("dönemde oluşturulan"). Yalnız eklenir; talebi kapatmak/yeniden açmak dokunmaz.</summary>
public sealed class UsageEvent : ITenantOwned
{
    private UsageEvent() { }

    public Guid TenantId { get; private set; }
    public string Metric { get; private set; } = null!;
    public Guid SourceId { get; private set; }
    public DateOnly PeriodStart { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}

using Kulvar.Platform.Data;
using Kulvar.Platform.Security;

namespace Kulvar.Modules.Billing.Domain;

public enum BillingOperationStatus
{
    /// <summary>Kaydedildi; sağlayıcıya gönderilecek veya sağlayıcı "beklemede" dedi.</summary>
    Pending,

    /// <summary>Sağlayıcı çağrısı zaman aşımına uğradı: sonuç bilinmiyor. Başarısızlık DEĞİLDİR; mutabakat sorgular.</summary>
    Unknown,

    /// <summary>Ödeme alındı ve plan uygulandı (terminal).</summary>
    Succeeded,

    /// <summary>Sağlayıcı reddetti; plan değişmedi (terminal).</summary>
    Declined,

    /// <summary>Ödeme alındı ama uygulanamaz (iptal/silme/yerini yeni işlem aldı): iade edilecek.</summary>
    RefundPending,

    /// <summary>İade edildi (terminal).</summary>
    Refunded,

    /// <summary>Operatör: sağlayıcıda tahsilat olmadığı doğrulandı (terminal).</summary>
    Voided,

    /// <summary>Operatör: elle kapatıldı, gerekçesiyle (terminal).</summary>
    ManuallyClosed,

    /// <summary>Otomatik çözülemedi; operatör eylemi bekliyor. Kuyrukta durmak telafi sayılmaz.</summary>
    NeedsResolution,
}

public static class BillingOperationRules
{
    public const int MaxAutomaticAttempts = 6;

    public static bool IsTerminal(BillingOperationStatus status) => status is
        BillingOperationStatus.Succeeded or BillingOperationStatus.Declined or BillingOperationStatus.Refunded or
        BillingOperationStatus.Voided or BillingOperationStatus.ManuallyClosed;

    /// <summary>Sağlayıcı sonucu henüz belli olmayan işlem: şirket aynı anda ikinci bir plan değişikliği başlatamaz.</summary>
    public static bool IsInFlight(BillingOperationStatus status) => status is BillingOperationStatus.Pending or BillingOperationStatus.Unknown;

    /// <summary>Üstel bekleme: 30 sn, 1 dk, 2 dk, … en çok 30 dk.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Math.Max(attempts - 1, 0)), 1800));

    /// <summary>
    /// Başarı yalnız şu koşullarda entitlement'a uygulanır: işlem hâlâ aboneliğin güncel epoch'unu taşıyor (arada iptal, silme isteği veya
    /// yeni plan işlemi olmadı) ve şirket etkin. Aksi halde alınan ödeme "sahipsiz"dir ve iade yoluna girer.
    /// </summary>
    public static bool CanApply(int operationEpoch, int subscriptionEpoch, bool tenantActive) => tenantActive && operationEpoch == subscriptionEpoch;
}

/// <summary>
/// Plan değişikliği ödeme işlemi. Sağlayıcıya gitmeden önce kaydedilir; her sağlayıcı çağrısı aynı <see cref="MerchantReference"/> ile
/// yapılır (yeniden deneme yeni ücret üretmez). Terminal durum değişmez.
/// </summary>
public sealed class BillingOperation : ITenantOwned
{
    private BillingOperation() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string MerchantReference { get; private set; } = null!;
    public Guid TargetPlanVersionId { get; private set; }
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = null!;
    public BillingOperationStatus Status { get; private set; }
    public int Epoch { get; private set; }
    public Guid RequestedByUserId { get; private set; }
    public string? SimulatedOutcome { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public string? FailureCode { get; private set; }
    public string? ResolutionNote { get; private set; }
    public Guid? ResolvedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public int Version { get; private set; }

    public bool IsTerminal => BillingOperationRules.IsTerminal(Status);

    public static BillingOperation Request(Guid tenantId, Guid userId, Guid targetPlanVersionId, long amountMinor, int epoch, string? simulatedOutcome, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        TenantId = tenantId,
        MerchantReference = "mref_" + Tokens.NewToken(18),
        TargetPlanVersionId = targetPlanVersionId,
        AmountMinor = amountMinor,
        Currency = PlanCatalog.Currency,
        Status = BillingOperationStatus.Pending,
        Epoch = epoch,
        RequestedByUserId = userId,
        SimulatedOutcome = simulatedOutcome,
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1,
    };

    /// <summary>Sağlayıcı "beklemede" dedi veya sorgu sonuç vermedi: daha sonra yeniden bakılacak.</summary>
    public void ScheduleRetry(DateTimeOffset now)
    {
        Attempts++;
        NextAttemptAt = now + BillingOperationRules.Backoff(Attempts);
        Touch(now);
    }

    public void MarkUnknown(DateTimeOffset now)
    {
        Status = BillingOperationStatus.Unknown;
        FailureCode = "provider_timeout";
        ScheduleRetry(now);
    }

    public void MarkSucceeded(DateTimeOffset now) => Complete(BillingOperationStatus.Succeeded, null, now);

    public void MarkDeclined(string? failureCode, DateTimeOffset now) => Complete(BillingOperationStatus.Declined, failureCode ?? "declined", now);

    public void MarkRefunded(DateTimeOffset now) => Complete(BillingOperationStatus.Refunded, FailureCode, now);

    /// <summary>Ödeme alındı ama uygulanamıyor: iade kuyruğu.</summary>
    public void MarkRefundPending(string reason, DateTimeOffset now)
    {
        Status = BillingOperationStatus.RefundPending;
        FailureCode = reason;
        Attempts = 0;
        NextAttemptAt = now;
        Touch(now);
    }

    public void MarkNeedsResolution(string reason, DateTimeOffset now)
    {
        Status = BillingOperationStatus.NeedsResolution;
        FailureCode = reason;
        NextAttemptAt = null;
        Touch(now);
    }

    /// <summary>Operatör çözümü: gerekçe zorunludur. "refund" otomatik iade yoluna geri gönderir; diğerleri terminaldir.</summary>
    public void Resolve(BillingOperationStatus target, Guid operatorUserId, string note, DateTimeOffset now)
    {
        if (Status != BillingOperationStatus.NeedsResolution)
        {
            throw new InvalidOperationException("Yalnız çözüm bekleyen işlem çözülebilir.");
        }
        ResolvedByUserId = operatorUserId;
        ResolutionNote = note;
        switch (target)
        {
            case BillingOperationStatus.RefundPending:
                MarkRefundPending("operator_refund", now);
                break;
            case BillingOperationStatus.Voided or BillingOperationStatus.ManuallyClosed:
                Complete(target, FailureCode, now);
                break;
            default:
                throw new InvalidOperationException("Geçersiz çözüm hedefi.");
        }
    }

    private void Complete(BillingOperationStatus status, string? failureCode, DateTimeOffset now)
    {
        Status = status;
        FailureCode = failureCode;
        NextAttemptAt = null;
        CompletedAt = now;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}

public enum ProviderEventStatus { Received, Applied, Ignored, Stale, UnknownMapping, Conflict }

/// <summary>
/// Webhook inbox'ı. Olay kimliği birincil anahtardır: aynı kimlik ikinci kez gelirse gövde hash'i karşılaştırılır
/// (aynı → yinelenen, farklı → çelişki). Olay ve etkisi worker'da tek transaction'da uygulanır.
/// </summary>
public sealed class ProviderEvent
{
    private ProviderEvent() { }

    public string ProviderEventId { get; private set; } = null!;
    public byte[] PayloadHash { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public DateTimeOffset ReceivedAt { get; private set; }
    public ProviderEventStatus Status { get; private set; }
    public Guid? TenantId { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public string? Note { get; private set; }

    public static ProviderEvent Receive(string id, byte[] payloadHash, string payload, DateTimeOffset now) => new()
    {
        ProviderEventId = id,
        PayloadHash = payloadHash,
        Payload = payload,
        ReceivedAt = now,
        Status = ProviderEventStatus.Received,
        NextAttemptAt = now,
    };

    public void Finish(ProviderEventStatus status, Guid? tenantId, string? note, DateTimeOffset now)
    {
        Status = status;
        TenantId = tenantId;
        Note = note;
        ProcessedAt = now;
        NextAttemptAt = null;
    }

    /// <summary>Eşleşme henüz yok (ör. olay, işlem kaydımızdan önce geldi): sınırlı süre yeniden denenir, sonra operatöre görünür kalır.</summary>
    public void DeferUnknownMapping(DateTimeOffset now, int maxAttempts)
    {
        Attempts++;
        Status = ProviderEventStatus.UnknownMapping;
        NextAttemptAt = Attempts >= maxAttempts ? null : now + TimeSpan.FromSeconds(30 * Attempts);
        Note = "mapping_not_found";
    }
}

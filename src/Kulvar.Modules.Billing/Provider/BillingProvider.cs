using System.Text.Json;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Security;

namespace Kulvar.Modules.Billing.Provider;

public enum ProviderOutcome { Succeeded, Declined, Pending }

public sealed record ProviderChargeRequest(string MerchantReference, string CustomerReference, string PlanCode, long AmountMinor, string Currency, string? SimulatedOutcome);

public sealed record ProviderResult(ProviderOutcome Outcome, string? SubscriptionId, long Sequence, string? FailureCode);

/// <summary>Sağlayıcı yanıt vermedi: sonuç BİLİNMİYOR (tahsilat yapılmış olabilir). Başarısızlık olarak yorumlanmaz.</summary>
public sealed class ProviderTimeoutException() : Exception("Ödeme sağlayıcısı zaman aşımına uğradı; sonuç bilinmiyor.");

/// <summary>Sağlayıcı isteği işlemeden reddetti (geçici): aynı referansla yeniden denenebilir.</summary>
public sealed class ProviderTransientException() : Exception("Ödeme sağlayıcısı geçici olarak kullanılamıyor.");

/// <summary>
/// Ödeme sağlayıcısı sınırı. Tüm çağrılar <c>MerchantReference</c> ile idempotenttir: aynı referansla yinelenen tahsilat isteği
/// yeni ücret üretmez, ilk sonucu döndürür.
/// </summary>
public interface IBillingProvider
{
    Task<ProviderResult> SubmitAsync(ProviderChargeRequest request, CancellationToken ct);

    /// <summary>İşlemin sağlayıcıdaki durumu; sağlayıcı bu referansı hiç görmediyse <c>null</c>.</summary>
    Task<ProviderResult?> QueryAsync(string merchantReference, CancellationToken ct);

    /// <summary>İade (idempotent). Tahsilat yoksa da başarıyla döner.</summary>
    Task RefundAsync(string merchantReference, CancellationToken ct);
}

public static class SimulatedOutcomes
{
    public const string Success = "success";
    public const string Decline = "decline";
    public const string TransientThenSuccess = "transient_then_success";

    /// <summary>Tahsilat yapılır ama yanıt kaybolur (zaman aşımı). Sorgu başarıyı gösterir.</summary>
    public const string TimeoutThenSuccess = "timeout_then_success";

    /// <summary>İlk çağrı kaybolur ve sağlayıcıya hiç ulaşmaz. Sorgu "bulunamadı" der; aynı referansla yeniden gönderim başarılı olur.</summary>
    public const string TimeoutNotRecorded = "timeout_not_recorded";

    /// <summary>Sağlayıcı "beklemede" der; sonuç daha sonra webhook ile gelir.</summary>
    public const string PendingThenWebhook = "pending_then_webhook";

    /// <summary>Sağlayıcı hiç yanıt vermez, sorgu da sonuç vermez: otomatik çözüm sınırı aşılır.</summary>
    public const string Unresolvable = "timeout_unresolvable";

    /// <summary>İade çağrıları da başarısız olur (operatör çözümü gerekir).</summary>
    public const string SuccessRefundFails = "success_refund_fails";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Success, Decline, TransientThenSuccess, TimeoutThenSuccess, TimeoutNotRecorded, PendingThenWebhook, Unresolvable, SuccessRefundFails,
    };
}

/// <summary>
/// Simüle ödeme sağlayıcısı. Gerçek bir sağlayıcı entegrasyonu DEĞİLDİR: hiçbir yere para hareketi olmaz. Durumunu <c>fakepsp</c> şemasında,
/// ürünün iş transaction'larından bağımsız kendi transaction'larında tutar (dış sistemi temsil eder). Senaryo, işlemin
/// <c>SimulatedOutcome</c> alanıyla seçilir; verilmezse "success".
/// </summary>
public sealed class FakeBillingProvider(WorkerDb db) : IBillingProvider
{
    private sealed record Row(string Status, string SubscriptionId, long Sequence, int Calls, string Script);

    public async Task<ProviderResult> SubmitAsync(ProviderChargeRequest request, CancellationToken ct)
    {
        var script = request.SimulatedOutcome ?? SimulatedOutcomes.Success;
        Exception? lostResponse = null;

        var result = await db.RunAsync(DbScope.None, async (session, c) =>
        {
            // Aynı referansla eşzamanlı iki gönderim bu satırda sıraya girer.
            await session.Db.ExecuteAsync(
                "INSERT INTO fakepsp.operations (merchant_reference, plan_code, amount_minor, status, script, calls, charge_count, refunded, created_at) VALUES ($1, $2, $3, 'none', $4, 0, 0, false, $5) ON CONFLICT (merchant_reference) DO NOTHING",
                c, request.MerchantReference, request.PlanCode, request.AmountMinor, script, session.Now);
            var row = (await session.Db.QueryAsync(
                "SELECT status, coalesce(subscription_id, ''), sequence, calls, script FROM fakepsp.operations WHERE merchant_reference = $1 FOR UPDATE",
                r => new Row(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3), r.GetString(4)), c, request.MerchantReference)).Single();
            await session.Db.ExecuteAsync("UPDATE fakepsp.operations SET calls = calls + 1 WHERE merchant_reference = $1", c, request.MerchantReference);

            // Idempotency: sonuçlanmış işlem için ilk sonuç döner; yeniden tahsilat yapılmaz.
            if (row.Status is "succeeded" or "declined" or "pending")
            {
                return ToResult(row.Status, row.SubscriptionId, row.Sequence);
            }

            switch (row.Script)
            {
                case SimulatedOutcomes.Decline:
                    await session.Db.ExecuteAsync("UPDATE fakepsp.operations SET status = 'declined' WHERE merchant_reference = $1", c, request.MerchantReference);
                    return new ProviderResult(ProviderOutcome.Declined, null, 0, "card_declined");
                case SimulatedOutcomes.PendingThenWebhook:
                    await session.Db.ExecuteAsync("UPDATE fakepsp.operations SET status = 'pending' WHERE merchant_reference = $1", c, request.MerchantReference);
                    return new ProviderResult(ProviderOutcome.Pending, null, 0, null);
                case SimulatedOutcomes.TransientThenSuccess when row.Calls == 0:
                    lostResponse = new ProviderTransientException();
                    return null;
                case SimulatedOutcomes.TimeoutNotRecorded when row.Calls == 0:
                case SimulatedOutcomes.Unresolvable:
                    lostResponse = new ProviderTimeoutException(); // İstek sağlayıcıya ulaşmadı: kayıt 'none' kalır.
                    return null;
            }

            var captured = await CaptureAsync(session, request, c);
            if (row.Script == SimulatedOutcomes.TimeoutThenSuccess && row.Calls == 0)
            {
                lostResponse = new ProviderTimeoutException(); // Tahsilat yapıldı ama yanıt yolda kayboldu.
            }
            return captured;
        }, ct);

        if (lostResponse is not null)
        {
            throw lostResponse;
        }
        return result!;
    }

    public async Task<ProviderResult?> QueryAsync(string merchantReference, CancellationToken ct) =>
        await db.RunAsync(DbScope.None, async (session, c) =>
        {
            var rows = await session.Db.QueryAsync(
                "SELECT status, coalesce(subscription_id, ''), sequence, calls, script FROM fakepsp.operations WHERE merchant_reference = $1",
                r => new Row(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3), r.GetString(4)), c, merchantReference);
            if (rows.Count == 0 || rows[0].Status == "none")
            {
                return null; // Sağlayıcı bu işlemi hiç görmedi.
            }
            return ToResult(rows[0].Status, rows[0].SubscriptionId, rows[0].Sequence);
        }, ct);

    public async Task RefundAsync(string merchantReference, CancellationToken ct)
    {
        var failed = await db.RunAsync(DbScope.None, async (session, c) =>
        {
            var script = await session.Db.ScalarAsync("SELECT script FROM fakepsp.operations WHERE merchant_reference = $1 FOR UPDATE", c, merchantReference) as string;
            if (script == SimulatedOutcomes.SuccessRefundFails)
            {
                return true;
            }
            await session.Db.ExecuteAsync("UPDATE fakepsp.operations SET refunded = true WHERE merchant_reference = $1 AND status = 'succeeded'", c, merchantReference);
            return false;
        }, ct);
        if (failed)
        {
            throw new ProviderTransientException();
        }
    }

    /// <summary>Tahsilat: işlem "succeeded" olur, şirketin sağlayıcı aboneliği oluşur/ilerler ve bir webhook olayı kuyruğa girer.</summary>
    private static async Task<ProviderResult> CaptureAsync(DbSession session, ProviderChargeRequest request, CancellationToken ct)
    {
        var subscription = (await session.Db.QueryAsync(
            """
            INSERT INTO fakepsp.subscriptions AS s (id, customer_reference, plan_code, sequence)
            VALUES ($1, $2, $3, 1)
            ON CONFLICT (customer_reference) DO UPDATE SET plan_code = EXCLUDED.plan_code, sequence = s.sequence + 1
            RETURNING id, sequence
            """,
            r => (Id: r.GetString(0), Sequence: r.GetInt64(1)), ct, "psub_" + Tokens.NewToken(12), request.CustomerReference, request.PlanCode)).Single();

        await session.Db.ExecuteAsync(
            "UPDATE fakepsp.operations SET status = 'succeeded', subscription_id = $2, sequence = $3, charge_count = charge_count + 1 WHERE merchant_reference = $1",
            ct, request.MerchantReference, subscription.Id, subscription.Sequence);
        await EnqueueWebhookAsync(session, "operation.succeeded", subscription.Id, subscription.Sequence, request.MerchantReference, request.PlanCode, ct);
        return new ProviderResult(ProviderOutcome.Succeeded, subscription.Id, subscription.Sequence, null);
    }

    /// <summary>Sağlayıcının göndereceği olay. Teslimatı (imzalı HTTP isteği) simülatör döngüsü veya test yapar.</summary>
    public static async Task<string> EnqueueWebhookAsync(DbSession session, string type, string subscriptionId, long sequence, string? merchantReference, string? planCode, CancellationToken ct)
    {
        var id = "evt_" + Tokens.NewToken(12);
        var payload = JsonSerializer.Serialize(new WebhookPayload(id, type, subscriptionId, sequence, merchantReference, planCode, session.Now), JsonDefaults.Storage);
        await session.Db.ExecuteAsync("INSERT INTO fakepsp.webhooks (id, payload, created_at, attempts) VALUES ($1, $2, $3, 0)", ct, id, payload, session.Now);
        return payload;
    }

    private static ProviderResult ToResult(string status, string subscriptionId, long sequence) => status switch
    {
        "succeeded" => new ProviderResult(ProviderOutcome.Succeeded, subscriptionId, sequence, null),
        "declined" => new ProviderResult(ProviderOutcome.Declined, null, 0, "card_declined"),
        _ => new ProviderResult(ProviderOutcome.Pending, null, 0, null),
    };
}

/// <summary>Sağlayıcı webhook gövdesi. Şirket kimliği alanı yoktur: yönlendirme bizim kayıtlarımızdaki eşleşmeden yapılır.</summary>
public sealed record WebhookPayload(string Id, string Type, string? SubscriptionId, long Sequence, string? MerchantReference, string? PlanCode, DateTimeOffset OccurredAt);

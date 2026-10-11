using System.Text.Json;
using Kulvar.Modules.Billing.Domain;
using Kulvar.Modules.Billing.Provider;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Messaging;
using Kulvar.Platform.Observability;
using Kulvar.Platform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kulvar.Modules.Billing;

/// <summary>
/// Ödeme işlemlerinin worker tarafı. Başarının entitlement'a uygulandığı TEK yer <see cref="SettleSuccessAsync"/>'tir:
/// sonuç ister eşzamanlı yanıttan, ister webhook'tan, ister mutabakattan gelsin aynı kurallar çalışır ve terminal durum değişmez.
/// </summary>
public sealed class BillingProcessor(IBillingProvider provider, ILogger<BillingProcessor> log)
{
    /// <summary>İşlemi sağlayıcıya gönderir (veya yeniden gönderir). Çağıran şirket kapsamını kurmuş ve şirket satırını kilitlemiştir.</summary>
    public async Task SubmitAsync(DbSession session, Guid operationId, bool tenantActive, CancellationToken ct)
    {
        var operation = await LockOperationAsync(session, operationId, ct);
        if (operation is null || operation.Status != BillingOperationStatus.Pending)
        {
            return; // Başka bir yol (webhook, mutabakat) işlemi zaten sonuçlandırmış.
        }
        var planCode = await session.Db.Set<PlanVersion>().Where(v => v.Id == operation.TargetPlanVersionId).Select(v => v.PlanCode).SingleAsync(ct);
        ProviderResult result;
        try
        {
            result = await provider.SubmitAsync(
                new ProviderChargeRequest(operation.MerchantReference, operation.TenantId.ToString("N"), planCode, operation.AmountMinor, operation.Currency, operation.SimulatedOutcome), ct);
        }
        catch (ProviderTimeoutException)
        {
            // Zaman aşımı başarısızlık değildir: tahsilat yapılmış olabilir. Sonuç mutabakatla öğrenilir.
            operation.MarkUnknown(session.Now);
            await session.Db.SaveChangesAsync(ct);
            return;
        }
        // ProviderTransientException yukarı fırlar: outbox aynı işlemi (aynı merchant referansıyla) sınırlı sayıda yeniden dener.
        await ApplyProviderResultAsync(session, operation, result, tenantActive, ct);
    }

    /// <summary>Sonucu bilinmeyen/bekleyen işlemi sağlayıcıdan sorgular; sağlayıcı işlemi hiç görmediyse aynı referansla yeniden gönderir.</summary>
    public async Task ReconcileAsync(DbSession session, Guid operationId, bool tenantActive, CancellationToken ct)
    {
        var operation = await LockOperationAsync(session, operationId, ct);
        if (operation is null)
        {
            return;
        }
        if (operation.Status == BillingOperationStatus.RefundPending)
        {
            await RefundAsync(session, operation, ct);
            return;
        }
        if (!BillingOperationRules.IsInFlight(operation.Status))
        {
            return;
        }
        try
        {
            var result = await provider.QueryAsync(operation.MerchantReference, ct);
            if (result is null)
            {
                var planCode = await session.Db.Set<PlanVersion>().Where(v => v.Id == operation.TargetPlanVersionId).Select(v => v.PlanCode).SingleAsync(ct);
                result = await provider.SubmitAsync(
                    new ProviderChargeRequest(operation.MerchantReference, operation.TenantId.ToString("N"), planCode, operation.AmountMinor, operation.Currency, operation.SimulatedOutcome), ct);
            }
            await ApplyProviderResultAsync(session, operation, result, tenantActive, ct);
        }
        catch (Exception ex) when (ex is ProviderTimeoutException or ProviderTransientException)
        {
            if (operation.Attempts + 1 >= BillingOperationRules.MaxAutomaticAttempts)
            {
                // Otomatik yollar tükendi: para sonucu belirsiz. Operatör kuyruğuna alınır; orada durmak çözüm değildir.
                operation.MarkNeedsResolution("outcome_unknown", session.Now);
                session.Db.Add(PlatformAuditEvent.Create(null, "billing.needs_resolution", operation.TenantId, new { operationId = operation.Id, reason = "outcome_unknown" }, session.Now));
            }
            else
            {
                operation.MarkUnknown(session.Now);
            }
            await session.Db.SaveChangesAsync(ct);
        }
    }

    private async Task ApplyProviderResultAsync(DbSession session, BillingOperation operation, ProviderResult result, bool tenantActive, CancellationToken ct)
    {
        switch (result.Outcome)
        {
            case ProviderOutcome.Succeeded:
                await SettleSuccessAsync(session, operation, result.SubscriptionId, result.Sequence, tenantActive, ct);
                break;
            case ProviderOutcome.Declined:
                await SettleDeclineAsync(session, operation, result.FailureCode, ct);
                break;
            default:
                operation.ScheduleRetry(session.Now); // Sağlayıcı "beklemede": sonuç webhook veya sonraki sorguyla gelir.
                await session.Db.SaveChangesAsync(ct);
                break;
        }
    }

    /// <summary>
    /// Başarılı ödemenin uygulanması. İşlem satırı çağıran tarafından kilitlidir. Epoch veya şirket durumu uymuyorsa ödeme "sahipsiz"dir:
    /// entitlement değişmez, işlem iade kuyruğuna girer. İptal/silme sonrası geç gelen başarı şirketi yeniden etkinleştirmez.
    /// </summary>
    public async Task SettleSuccessAsync(DbSession session, BillingOperation operation, string? providerSubscriptionId, long sequence, bool tenantActive, CancellationToken ct)
    {
        if (operation.IsTerminal || operation.Status is BillingOperationStatus.RefundPending or BillingOperationStatus.NeedsResolution)
        {
            return; // Aynı başarı ikinci kez geldi (yinelenen webhook, mutabakat + webhook): etkisiz.
        }
        await session.Db.ExecuteAsync("SELECT 1 FROM app.subscriptions WHERE tenant_id = $1 FOR UPDATE", ct, operation.TenantId);
        var subscription = await session.Db.Set<Subscription>().SingleOrDefaultAsync(s => s.TenantId == operation.TenantId, ct);

        if (subscription is null || !BillingOperationRules.CanApply(operation.Epoch, subscription.Epoch, tenantActive))
        {
            operation.MarkRefundPending("orphaned_success", session.Now);
            session.Db.Add(PlatformAuditEvent.Create(null, "billing.orphaned_success", operation.TenantId, new { operationId = operation.Id }, session.Now));
            await session.Db.SaveChangesAsync(ct);
            log.LogWarning("Sahipsiz ödeme başarısı iade kuyruğuna alındı: {OperationId}", operation.Id);
            return;
        }

        subscription.ApplyPlan(operation.TargetPlanVersionId, session.Now);
        if (providerSubscriptionId is not null)
        {
            subscription.BindProvider(providerSubscriptionId, sequence);
        }
        operation.MarkSucceeded(session.Now);
        session.Db.Add(AuditEvent.Create(operation.TenantId, null, "billing.plan_applied", "subscription", operation.TenantId, null, new { operationId = operation.Id }, session.Now));
        session.Db.Add(OutboxMessage.Create(operation.TenantId, null, BillingOutbox.SubscriptionChanged, new BillingOutbox.SubscriptionChangedPayload("PlanApplied"), session.Now));
        await session.Db.SaveChangesAsync(ct);
    }

    public static async Task<bool> SettleDeclineAsync(DbSession session, BillingOperation operation, string? failureCode, CancellationToken ct)
    {
        if (!BillingOperationRules.IsInFlight(operation.Status))
        {
            return false; // Terminal işlem değişmez; çelişen sonuç çağıran tarafından kaydedilir.
        }
        operation.MarkDeclined(failureCode, session.Now);
        session.Db.Add(AuditEvent.Create(operation.TenantId, null, "billing.payment_declined", "subscription", operation.TenantId, null, new { operationId = operation.Id }, session.Now));
        session.Db.Add(OutboxMessage.Create(operation.TenantId, null, BillingOutbox.SubscriptionChanged, new BillingOutbox.SubscriptionChangedPayload("PaymentDeclined"), session.Now));
        await session.Db.SaveChangesAsync(ct);
        return true;
    }

    private async Task RefundAsync(DbSession session, BillingOperation operation, CancellationToken ct)
    {
        try
        {
            await provider.RefundAsync(operation.MerchantReference, ct);
            operation.MarkRefunded(session.Now);
            session.Db.Add(PlatformAuditEvent.Create(null, "billing.refunded", operation.TenantId, new { operationId = operation.Id }, session.Now));
            session.Db.Add(OutboxMessage.Create(operation.TenantId, null, BillingOutbox.SubscriptionChanged, new BillingOutbox.SubscriptionChangedPayload("Refunded"), session.Now));
        }
        catch (Exception ex) when (ex is ProviderTimeoutException or ProviderTransientException)
        {
            if (operation.Attempts + 1 >= BillingOperationRules.MaxAutomaticAttempts)
            {
                operation.MarkNeedsResolution("refund_failed", session.Now);
                session.Db.Add(PlatformAuditEvent.Create(null, "billing.needs_resolution", operation.TenantId, new { operationId = operation.Id, reason = "refund_failed" }, session.Now));
            }
            else
            {
                operation.ScheduleRetry(session.Now);
            }
        }
        await session.Db.SaveChangesAsync(ct);
    }

    internal static async Task<BillingOperation?> LockOperationAsync(DbSession session, Guid operationId, CancellationToken ct)
    {
        await session.Db.ExecuteAsync("SELECT 1 FROM app.billing_operations WHERE id = $1 FOR UPDATE", ct, operationId);
        return await session.Db.Set<BillingOperation>().SingleOrDefaultAsync(o => o.Id == operationId, ct);
    }
}

/// <summary>Plan değişikliği istendi: outbox üzerinden sağlayıcıya gönderilir.</summary>
internal sealed class OperationRequestedHandler(BillingProcessor processor) : IOutboxHandler
{
    public string Type => BillingOutbox.OperationRequested;

    public Task HandleAsync(DbSession session, OutboxMessageEnvelope message, CancellationToken ct) =>
        // Outbox işleyici şirket satırını kilitleyip durumunu doğrulamış ve kapsamı kurmuştur: buraya gelen şirket etkindir.
        processor.SubmitAsync(session, message.Read<BillingOutbox.OperationPayload>().OperationId, tenantActive: true, ct);
}

/// <summary>
/// Mutabakat döngüsü: sonucu bilinmeyen/bekleyen işlemler ve bekleyen iadeler. Worker işlem METADATA'sını şirketler arası görür;
/// her işlem kendi transaction'ında, kendi şirket kapsamıyla işlenir.
/// </summary>
public sealed class BillingReconciler(WorkerDb db, BillingProcessor processor)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var due = await db.RunAsync(DbScope.None, (session, c) => session.Db.QueryAsync(
            """
            SELECT id, tenant_id FROM app.billing_operations
            WHERE status IN ('Unknown', 'RefundPending', 'Pending') AND next_attempt_at IS NOT NULL AND next_attempt_at <= $1
            ORDER BY next_attempt_at, id LIMIT 50
            """,
            r => (Id: r.GetGuid(0), TenantId: r.GetGuid(1)), c, session.Now), ct);

        foreach (var (id, tenantId) in due)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            await db.RunAsync(DbScope.None, async (session, c) =>
            {
                // Şirket silinmiş olsa da ödeme sonucu çözülmelidir (sahipsiz başarı iade edilir); bu yüzden iş atlanmaz, yalnız "etkin değil" bilgisi taşınır.
                var status = await TenantRegistry.LockSharedAsync(session.Db, tenantId, c);
                await session.EnterTenantAsync(tenantId, c);
                await processor.ReconcileAsync(session, id, tenantActive: status == TenantStatus.Active, c);
            }, ct);
        }
        return due.Count;
    }
}

/// <summary>
/// Webhook inbox işleyici. Her olay tek transaction'da: inbox durumu + etkisi. Şirket, bizim kayıtlarımızdaki eşleşmeden bulunur
/// (işlem için merchant referansı, abonelik için sağlayıcı abonelik kimliği); gövdedeki herhangi bir şirket alanı kullanılmaz.
/// </summary>
public sealed class ProviderInboxProcessor(WorkerDb db, BillingProcessor processor, ILogger<ProviderInboxProcessor> log)
{
    private const int MaxMappingAttempts = 5;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var due = await db.RunAsync(DbScope.None, (session, c) => session.Db.QueryAsync(
            "SELECT provider_event_id FROM app.provider_events WHERE next_attempt_at IS NOT NULL AND next_attempt_at <= $1 ORDER BY received_at, provider_event_id LIMIT 50",
            r => r.GetString(0), c, session.Now), ct);

        foreach (var eventId in due)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            var outcome = await db.RunAsync(DbScope.None, (session, c) => ProcessAsync(session, eventId, c), ct);
            Telemetry.WebhookEvents.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        }
        return due.Count;
    }

    private async Task<string> ProcessAsync(DbSession session, string eventId, CancellationToken ct)
    {
        await session.Db.ExecuteAsync("SELECT 1 FROM app.provider_events WHERE provider_event_id = $1 FOR UPDATE", ct, eventId);
        var inbox = await session.Db.Set<ProviderEvent>().SingleAsync(e => e.ProviderEventId == eventId, ct);
        if (inbox.NextAttemptAt is null)
        {
            return "already_processed";
        }
        var payload = JsonSerializer.Deserialize<WebhookPayload>(inbox.Payload, JsonDefaults.Storage)!;

        // Eşleşme: yalnız kendi kayıtlarımızdan.
        Guid? tenantId = null;
        Guid? operationId = null;
        if (payload.Type.StartsWith("operation.", StringComparison.Ordinal) && payload.MerchantReference is { Length: > 0 } reference)
        {
            var match = await session.Db.QueryAsync("SELECT id, tenant_id FROM app.billing_operations WHERE merchant_reference = $1", r => (Id: r.GetGuid(0), TenantId: r.GetGuid(1)), ct, reference);
            if (match.Count == 1)
            {
                (operationId, tenantId) = (match[0].Id, match[0].TenantId);
            }
        }
        else if (payload.Type.StartsWith("subscription.", StringComparison.Ordinal) && payload.SubscriptionId is { Length: > 0 } subscriptionId)
        {
            tenantId = await session.Db.ScalarAsync("SELECT tenant_id FROM app.subscriptions WHERE provider_subscription_id = $1", ct, subscriptionId) as Guid?;
        }
        else
        {
            inbox.Finish(ProviderEventStatus.Ignored, null, "unsupported_type", session.Now);
            await session.Db.SaveChangesAsync(ct);
            return "ignored";
        }

        if (tenantId is null)
        {
            inbox.DeferUnknownMapping(session.Now, MaxMappingAttempts);
            if (inbox.NextAttemptAt is null)
            {
                session.Db.Add(PlatformAuditEvent.Create(null, "webhook.unknown_mapping", null, new { eventId, type = payload.Type }, session.Now));
            }
            await session.Db.SaveChangesAsync(ct);
            return "unknown_mapping";
        }

        var tenantStatus = await TenantRegistry.LockSharedAsync(session.Db, tenantId.Value, ct);
        var tenantActive = tenantStatus == TenantStatus.Active;
        await session.EnterTenantAsync(tenantId.Value, ct);

        var (status, note) = operationId is { } opId
            ? await ApplyOperationEventAsync(session, payload, opId, tenantActive, ct)
            : await ApplySubscriptionEventAsync(session, payload, tenantId.Value, tenantActive, ct);
        inbox.Finish(status, tenantId, note, session.Now);
        await session.Db.SaveChangesAsync(ct);
        if (status == ProviderEventStatus.Conflict)
        {
            log.LogWarning("Çelişen sağlayıcı olayı: {EventId} {Note}", eventId, note);
        }
        return status.ToString().ToLowerInvariant();
    }

    private async Task<(ProviderEventStatus, string?)> ApplyOperationEventAsync(DbSession session, WebhookPayload payload, Guid operationId, bool tenantActive, CancellationToken ct)
    {
        var operation = (await BillingProcessor.LockOperationAsync(session, operationId, ct))!;
        switch (payload.Type)
        {
            case "operation.succeeded":
                if (operation.Status == BillingOperationStatus.Declined)
                {
                    // Reddedilmiş diye kapattığımız işlem için başarı: terminal durum değişmez; para alınmış olabilir → operatöre.
                    session.Db.Add(PlatformAuditEvent.Create(null, "billing.conflicting_outcome", operation.TenantId, new { operationId, got = "succeeded", have = "Declined" }, session.Now));
                    return (ProviderEventStatus.Conflict, "success_after_decline");
                }
                if (!BillingOperationRules.IsInFlight(operation.Status))
                {
                    return (ProviderEventStatus.Ignored, "already_settled");
                }
                await processor.SettleSuccessAsync(session, operation, payload.SubscriptionId, payload.Sequence, tenantActive, ct);
                return (ProviderEventStatus.Applied, operation.Status == BillingOperationStatus.Succeeded ? "applied" : "orphaned_refund_pending");
            case "operation.declined":
                if (operation.Status == BillingOperationStatus.Succeeded)
                {
                    session.Db.Add(PlatformAuditEvent.Create(null, "billing.conflicting_outcome", operation.TenantId, new { operationId, got = "declined", have = "Succeeded" }, session.Now));
                    return (ProviderEventStatus.Conflict, "decline_after_success");
                }
                return await BillingProcessor.SettleDeclineAsync(session, operation, "card_declined", ct)
                    ? (ProviderEventStatus.Applied, "declined")
                    : (ProviderEventStatus.Ignored, "already_settled");
            default:
                return (ProviderEventStatus.Ignored, "unsupported_type");
        }
    }

    private static async Task<(ProviderEventStatus, string?)> ApplySubscriptionEventAsync(DbSession session, WebhookPayload payload, Guid tenantId, bool tenantActive, CancellationToken ct)
    {
        await session.Db.ExecuteAsync("SELECT 1 FROM app.subscriptions WHERE tenant_id = $1 FOR UPDATE", ct, tenantId);
        var subscription = await session.Db.Set<Subscription>().SingleAsync(s => s.TenantId == tenantId, ct);

        // Sıra: sağlayıcının sıra numarası. Daha eski veya aynı sıralı olay uygulanmaz (geç gelen olay durumu geri alamaz).
        if (!subscription.IsNewerSequence(payload.Sequence))
        {
            return (ProviderEventStatus.Stale, $"sequence {payload.Sequence} <= {subscription.ProviderSequence}");
        }
        var changed = payload.Type switch
        {
            "subscription.renewal_failed" => subscription.OnRenewalFailed(payload.Sequence, session.Now),
            "subscription.renewed" => tenantActive && subscription.OnRenewed(payload.Sequence, session.Now),
            _ => false,
        };
        if (!changed)
        {
            return (ProviderEventStatus.Ignored, "no_state_change");
        }
        var reason = payload.Type == "subscription.renewed" ? "Renewed" : "RenewalFailed";
        session.Db.Add(AuditEvent.Create(tenantId, null, "subscription.status_changed", "subscription", tenantId, null, new { reason, to = subscription.Status }, session.Now));
        session.Db.Add(OutboxMessage.Create(tenantId, null, BillingOutbox.SubscriptionChanged, new BillingOutbox.SubscriptionChangedPayload(reason), session.Now));
        return (ProviderEventStatus.Applied, reason);
    }
}

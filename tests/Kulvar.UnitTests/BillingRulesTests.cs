using System.Text;
using Kulvar.Modules.Billing;
using Kulvar.Modules.Billing.Domain;
using Kulvar.Platform.Tenancy;

namespace Kulvar.UnitTests;

public sealed class SubscriptionRulesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.Parse("018f0000-0000-7000-8000-0000000000aa");

    [Fact]
    public void Trial_Expires_ToPastDue_ThenSuspended()
    {
        var subscription = Subscription.StartTrial(Tenant, Start);
        Assert.Equal(SubscriptionStatus.Trialing, subscription.Status);
        Assert.Equal(Start.AddDays(14), subscription.TrialEndsAt);

        Assert.Equal(SubscriptionChange.None, subscription.AdvanceTime(Start.AddDays(14).AddSeconds(-1)));
        Assert.Equal(SubscriptionChange.TrialExpired, subscription.AdvanceTime(Start.AddDays(14)));
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
        Assert.Equal(Start.AddDays(21), subscription.GraceEndsAt); // Ödemesiz dönem trial bitişinden sayılır, zamanlayıcının çalıştığı andan değil.

        Assert.Equal(SubscriptionChange.None, subscription.AdvanceTime(Start.AddDays(21).AddSeconds(-1)));
        Assert.Equal(SubscriptionChange.GraceExpired, subscription.AdvanceTime(Start.AddDays(40)));
        Assert.Equal(SubscriptionStatus.Suspended, subscription.Status);
        Assert.Equal(SubscriptionChange.None, subscription.AdvanceTime(Start.AddDays(400)));
    }

    [Fact]
    public void ApplyPlan_Activates_FromAnyState_AndClearsDeadlines()
    {
        var subscription = Subscription.StartTrial(Tenant, Start);
        subscription.AdvanceTime(Start.AddDays(14));
        subscription.AdvanceTime(Start.AddDays(21));
        subscription.ApplyPlan(PlanCatalog.TeamV1, Start.AddDays(30));

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(PlanCatalog.TeamV1, subscription.PlanVersionId);
        Assert.Null(subscription.TrialEndsAt);
        Assert.Null(subscription.GraceEndsAt);
        Assert.Equal(Start.AddDays(30).AddMonths(1), subscription.CurrentPeriodEnd);
    }

    [Fact]
    public void ProviderSequence_OnlyNewerApplies()
    {
        var subscription = Subscription.StartTrial(Tenant, Start);
        subscription.ApplyPlan(PlanCatalog.StarterV1, Start);
        subscription.BindProvider("psub_1", 3);

        Assert.False(subscription.IsNewerSequence(3));
        Assert.False(subscription.IsNewerSequence(2));
        Assert.True(subscription.IsNewerSequence(4));

        Assert.True(subscription.OnRenewalFailed(4, Start.AddDays(1)));
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
        Assert.True(subscription.OnRenewed(5, Start.AddDays(2)));
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(5, subscription.ProviderSequence);
        subscription.BindProvider("psub_1", 2); // Daha eski sıra geri yazılmaz.
        Assert.Equal(5, subscription.ProviderSequence);
    }

    [Fact]
    public void Cancel_BumpsEpoch_AndRenewalNeverReactivates()
    {
        var subscription = Subscription.StartTrial(Tenant, Start);
        subscription.ApplyPlan(PlanCatalog.TeamV1, Start);
        var epoch = subscription.Epoch;

        subscription.Cancel(Start.AddDays(1));
        Assert.Equal(SubscriptionStatus.Cancelled, subscription.Status);
        Assert.Equal(epoch + 1, subscription.Epoch);

        Assert.False(subscription.OnRenewed(9, Start.AddDays(2)));
        Assert.Equal(SubscriptionStatus.Cancelled, subscription.Status);
        Assert.False(subscription.OnRenewalFailed(10, Start.AddDays(3)));
        Assert.Equal(SubscriptionStatus.Cancelled, subscription.Status);
        Assert.Equal(SubscriptionChange.None, subscription.AdvanceTime(Start.AddYears(1)));
    }

    [Fact]
    public void RenewalFailed_OnlyAffectsActive()
    {
        var trial = Subscription.StartTrial(Tenant, Start);
        Assert.False(trial.OnRenewalFailed(1, Start.AddDays(1)));
        Assert.Equal(SubscriptionStatus.Trialing, trial.Status);
    }
}

public sealed class UsagePeriodTests
{
    [Theory]
    [InlineData("2026-12-31T23:59:59+00:00", "2026-12-01", "2027-01-01T00:00:00+00:00")]
    [InlineData("2027-01-01T00:00:00+00:00", "2027-01-01", "2027-02-01T00:00:00+00:00")]
    // İstanbul'da (UTC+3) yeni yıl gecesi 01:30: UTC'de hâlâ 31 Aralık 22:30 → dönem Aralık.
    [InlineData("2027-01-01T01:30:00+03:00", "2026-12-01", "2027-01-01T00:00:00+00:00")]
    // New York'ta (UTC-5) 31 Aralık 20:00: UTC'de 1 Ocak 01:00 → dönem Ocak.
    [InlineData("2026-12-31T20:00:00-05:00", "2027-01-01", "2027-02-01T00:00:00+00:00")]
    [InlineData("2028-02-29T12:00:00+00:00", "2028-02-01", "2028-03-01T00:00:00+00:00")]
    public void Period_IsUtcCalendarMonth_RegardlessOfOffset(string instant, string expectedPeriod, string expectedReset)
    {
        var at = DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(DateOnly.Parse(expectedPeriod, System.Globalization.CultureInfo.InvariantCulture), UsagePeriod.Of(at));
        Assert.Equal(DateTimeOffset.Parse(expectedReset, System.Globalization.CultureInfo.InvariantCulture), UsagePeriod.ResetsAt(at));
    }

    [Fact]
    public void Period_DoesNotDependOnProcessTimeZone()
    {
        // Aynı an, farklı gösterimler: dönem aynıdır.
        var utc = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero); // Avrupa'da yaz saatine geçiş gecesi
        Assert.Equal(UsagePeriod.Of(utc), UsagePeriod.Of(utc.ToOffset(TimeSpan.FromHours(3))));
        Assert.Equal(UsagePeriod.Of(utc), UsagePeriod.Of(utc.ToOffset(TimeSpan.FromHours(-11))));
    }
}

public sealed class BillingOperationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Operator = Guid.Parse("018f0000-0000-7000-8000-0000000000bb");

    private static BillingOperation NewOperation() => BillingOperation.Request(Guid.NewGuid(), Guid.NewGuid(), PlanCatalog.TeamV1, 149000, epoch: 3, simulatedOutcome: null, Now);

    [Theory]
    [InlineData(3, 3, true, true)]
    [InlineData(3, 4, true, false)]   // iptal / silme isteği / yeni işlem epoch'u ilerletti
    [InlineData(3, 3, false, false)]  // şirket etkin değil
    public void CanApply_RequiresMatchingEpoch_AndActiveTenant(int operationEpoch, int subscriptionEpoch, bool tenantActive, bool expected) =>
        Assert.Equal(expected, BillingOperationRules.CanApply(operationEpoch, subscriptionEpoch, tenantActive));

    [Fact]
    public void Request_CreatesPendingOperation_WithUnguessableReference_AndIntegerMinorAmount()
    {
        var a = NewOperation();
        var b = NewOperation();
        Assert.Equal(BillingOperationStatus.Pending, a.Status);
        Assert.StartsWith("mref_", a.MerchantReference, StringComparison.Ordinal);
        Assert.True(a.MerchantReference.Length >= 29);
        Assert.NotEqual(a.MerchantReference, b.MerchantReference);
        Assert.Equal(149000L, a.AmountMinor);
        Assert.Equal("TRY", a.Currency);
    }

    [Fact]
    public void Timeout_IsUnknown_NotFailure()
    {
        var operation = NewOperation();
        operation.MarkUnknown(Now);
        Assert.Equal(BillingOperationStatus.Unknown, operation.Status);
        Assert.False(operation.IsTerminal);
        Assert.True(BillingOperationRules.IsInFlight(operation.Status));
        Assert.Equal(Now.AddSeconds(30), operation.NextAttemptAt);
        operation.MarkUnknown(Now);
        Assert.Equal(Now.AddSeconds(60), operation.NextAttemptAt); // üstel bekleme
    }

    [Fact]
    public void Backoff_IsCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), BillingOperationRules.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(240), BillingOperationRules.Backoff(4));
        Assert.Equal(TimeSpan.FromMinutes(30), BillingOperationRules.Backoff(40));
    }

    [Fact]
    public void TerminalStates_AreExactlyTheSettledOnes()
    {
        var terminal = Enum.GetValues<BillingOperationStatus>().Where(BillingOperationRules.IsTerminal).ToHashSet();
        Assert.Equal(
            [BillingOperationStatus.Succeeded, BillingOperationStatus.Declined, BillingOperationStatus.Refunded, BillingOperationStatus.Voided, BillingOperationStatus.ManuallyClosed],
            terminal.Order().ToList());
        // Operatör kuyruğu ve iade kuyruğu terminal değildir.
        Assert.False(BillingOperationRules.IsTerminal(BillingOperationStatus.NeedsResolution));
        Assert.False(BillingOperationRules.IsTerminal(BillingOperationStatus.RefundPending));
    }

    [Fact]
    public void Resolve_OnlyFromNeedsResolution_AndOnlyToAllowedTargets()
    {
        var operation = NewOperation();
        Assert.Throws<InvalidOperationException>(() => operation.Resolve(BillingOperationStatus.Voided, Operator, "not", Now));

        operation.MarkNeedsResolution("outcome_unknown", Now);
        Assert.Throws<InvalidOperationException>(() => operation.Resolve(BillingOperationStatus.Succeeded, Operator, "elle başarı", Now));
        Assert.Equal(BillingOperationStatus.NeedsResolution, operation.Status);

        operation.Resolve(BillingOperationStatus.Voided, Operator, "tahsilat yok", Now.AddHours(1));
        Assert.Equal(BillingOperationStatus.Voided, operation.Status);
        Assert.True(operation.IsTerminal);
        Assert.Equal(Operator, operation.ResolvedByUserId);
        Assert.Equal(Now.AddHours(1), operation.CompletedAt);
    }

    [Fact]
    public void Resolve_Refund_ReturnsToRefundQueue()
    {
        var operation = NewOperation();
        operation.MarkNeedsResolution("refund_failed", Now);
        operation.Resolve(BillingOperationStatus.RefundPending, Operator, "yeniden dene", Now);
        Assert.Equal(BillingOperationStatus.RefundPending, operation.Status);
        Assert.Equal(0, operation.Attempts);
        Assert.Equal(Now, operation.NextAttemptAt);
        operation.MarkRefunded(Now.AddMinutes(1));
        Assert.True(operation.IsTerminal);
    }
}

public sealed class WebhookSignatureTests
{
    private const string Secret = "unit-test-webhook-secret-0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"id\":\"evt_1\",\"type\":\"subscription.renewed\"}");

    [Fact]
    public void ValidSignature_Verifies()
    {
        var header = WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body);
        Assert.Matches("^t=\\d+,v1=[0-9a-f]{64}$", header);
        Assert.True(WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));
        Assert.True(WebhookSignature.Verify(Secret, header, Body, Now.AddMinutes(4), Tolerance));
    }

    [Fact]
    public void Signature_CoversRawBytes_NotParsedJson()
    {
        var header = WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body);
        // Anlamca aynı JSON, farklı baytlar (boşluk): imza tutmaz.
        var reformatted = Encoding.UTF8.GetBytes("{ \"id\": \"evt_1\", \"type\": \"subscription.renewed\" }");
        Assert.False(WebhookSignature.Verify(Secret, header, reformatted, Now, Tolerance));
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void Timestamp_OutsideTolerance_Rejected_EvenWithValidMac(int offsetSeconds)
    {
        var header = WebhookSignature.Sign(Secret, Now.AddSeconds(offsetSeconds).ToUnixTimeSeconds(), Body);
        Assert.False(WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));
    }

    [Fact]
    public void TamperedTimestamp_InvalidatesMac()
    {
        var header = WebhookSignature.Sign(Secret, Now.AddHours(-2).ToUnixTimeSeconds(), Body);
        var mac = header[(header.IndexOf("v1=", StringComparison.Ordinal) + 3)..];
        // Eski imzanın zaman damgasını "şimdi" yapmak: MAC zaman damgasını da kapsadığı için geçmez.
        Assert.False(WebhookSignature.Verify(Secret, $"t={Now.ToUnixTimeSeconds()},v1={mac}", Body, Now, Tolerance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=abc")]
    [InlineData("t=abc,v1=abc")]
    [InlineData("t=1,v1=zz")]
    [InlineData("garbage")]
    public void MalformedHeader_Rejected(string? header) => Assert.False(WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));

    [Fact]
    public void WrongOrWeakSecret_Rejected()
    {
        var header = WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body);
        Assert.False(WebhookSignature.Verify("another-webhook-secret-0123456789abcdef", header, Body, Now, Tolerance));
        // Sır yapılandırılmamışsa (boş/kısa) hiçbir imza kabul edilmez.
        var weak = WebhookSignature.Sign("short", Now.ToUnixTimeSeconds(), Body);
        Assert.False(WebhookSignature.Verify("short", weak, Body, Now, Tolerance));
        Assert.False(WebhookSignature.Verify(string.Empty, header, Body, Now, Tolerance));
    }
}

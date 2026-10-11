using Kulvar.Modules.Billing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kulvar.Modules.Billing.Persistence;

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.ToTable("plans");
        b.HasKey(x => x.Code);
        b.Property(x => x.Code).HasMaxLength(30);
        b.Property(x => x.Name).HasMaxLength(60);
    }
}

internal sealed class PlanVersionConfiguration : IEntityTypeConfiguration<PlanVersion>
{
    public void Configure(EntityTypeBuilder<PlanVersion> b)
    {
        b.ToTable("plan_versions", t =>
        {
            t.HasCheckConstraint("ck_plan_versions_currency", "currency = 'TRY'");
            t.HasCheckConstraint("ck_plan_versions_limits", "price_minor >= 0 AND seat_limit > 0 AND monthly_ticket_limit >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PlanCode).HasMaxLength(30);
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanCode).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PlanCode, x.Version }).IsUnique();
        b.HasIndex(x => x.PlanCode).IsUnique().HasFilter("is_current");
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.ToTable("subscriptions", t => t.HasCheckConstraint("ck_subscriptions_status", "status IN ('Trialing','Active','PastDue','Suspended','Cancelled')"));
        b.HasKey(x => x.TenantId);
        b.Property(x => x.TenantId).ValueGeneratedNever();
        b.Property(x => x.ProviderSubscriptionId).HasMaxLength(80);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasOne<PlanVersion>().WithMany().HasForeignKey(x => x.PlanVersionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ProviderSubscriptionId).IsUnique();
        b.HasIndex(x => x.TrialEndsAt).HasFilter("status = 'Trialing'");
        b.HasIndex(x => x.GraceEndsAt).HasFilter("status = 'PastDue'");
    }
}

internal sealed class BillingOperationConfiguration : IEntityTypeConfiguration<BillingOperation>
{
    public void Configure(EntityTypeBuilder<BillingOperation> b)
    {
        b.ToTable("billing_operations", t => t.HasCheckConstraint("ck_billing_operations_status",
            "status IN ('Pending','Unknown','Succeeded','Declined','RefundPending','Refunded','Voided','ManuallyClosed','NeedsResolution')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.MerchantReference).HasMaxLength(80);
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.SimulatedOutcome).HasMaxLength(40);
        b.Property(x => x.FailureCode).HasMaxLength(60);
        b.Property(x => x.ResolutionNote).HasMaxLength(500);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Ignore(x => x.IsTerminal);
        b.HasOne<PlanVersion>().WithMany().HasForeignKey(x => x.TargetPlanVersionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.MerchantReference).IsUnique();
        // Şirket başına sağlayıcı sonucu bekleyen en çok bir işlem.
        b.HasIndex(x => x.TenantId).IsUnique().HasFilter("status IN ('Pending','Unknown')");
        b.HasIndex(x => x.NextAttemptAt).HasFilter("next_attempt_at IS NOT NULL");
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
    }
}

internal sealed class ProviderEventConfiguration : IEntityTypeConfiguration<ProviderEvent>
{
    public void Configure(EntityTypeBuilder<ProviderEvent> b)
    {
        b.ToTable("provider_events", t => t.HasCheckConstraint("ck_provider_events_status",
            "status IN ('Received','Applied','Ignored','Stale','UnknownMapping','Conflict')"));
        b.HasKey(x => x.ProviderEventId);
        b.Property(x => x.ProviderEventId).HasMaxLength(80);
        b.Property(x => x.Payload).HasColumnType("jsonb");
        b.Property(x => x.Note).HasMaxLength(200);
        b.HasIndex(x => x.NextAttemptAt).HasFilter("next_attempt_at IS NOT NULL");
    }
}

internal sealed class UsageCounterConfiguration : IEntityTypeConfiguration<UsageCounter>
{
    public void Configure(EntityTypeBuilder<UsageCounter> b)
    {
        b.ToTable("usage_counters", t => t.HasCheckConstraint("ck_usage_counters_used", "used >= 0"));
        b.HasKey(x => new { x.TenantId, x.Metric, x.PeriodStart });
        b.Property(x => x.Metric).HasMaxLength(40);
    }
}

internal sealed class UsageEventConfiguration : IEntityTypeConfiguration<UsageEvent>
{
    public void Configure(EntityTypeBuilder<UsageEvent> b)
    {
        b.ToTable("usage_events");
        b.HasKey(x => new { x.TenantId, x.Metric, x.SourceId });
        b.Property(x => x.Metric).HasMaxLength(40);
        b.HasIndex(x => new { x.TenantId, x.Metric, x.PeriodStart });
    }
}

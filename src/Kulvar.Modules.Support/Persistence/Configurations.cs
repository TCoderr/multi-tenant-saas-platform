using Kulvar.Modules.Customers;
using Kulvar.Modules.Support.Domain;
using Kulvar.Modules.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kulvar.Modules.Support.Persistence;

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> b)
    {
        b.ToTable("tickets", t =>
        {
            t.HasCheckConstraint("ck_tickets_status", "status IN ('Open','InProgress','Resolved','Closed')");
            t.HasCheckConstraint("ck_tickets_priority", "priority IN ('Low','Normal','High','Urgent')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(10000);
        b.Property(x => x.Version).IsConcurrencyToken();

        // Bileşik FK'ler: talep yalnız AYNI şirketin müşterisine ve AYNI şirketin üyelik satırına bağlanabilir.
        // tenant_id'yi başka şirkete çevirip yabancı kimlik vermek veritabanında FK ihlalidir (RLS'ten bağımsız).
        b.HasOne<Customer>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.CustomerId }).HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.AssigneeUserId }).HasPrincipalKey(m => new { m.TenantId, m.UserId })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.CreatedByUserId }).HasPrincipalKey(m => new { m.TenantId, m.UserId })
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.CreatedAt, x.Id }).IsDescending(false, true, true);
        b.HasIndex(x => new { x.TenantId, x.UpdatedAt, x.Id }).IsDescending(false, true, true);
        b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt, x.Id }).IsDescending(false, false, true, true);
        b.HasIndex(x => new { x.TenantId, x.AssigneeUserId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.CustomerId });
    }
}

internal sealed class TicketCommentConfiguration : IEntityTypeConfiguration<TicketComment>
{
    public void Configure(EntityTypeBuilder<TicketComment> b)
    {
        b.ToTable("ticket_comments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Body).HasMaxLength(5000);
        b.HasOne<Ticket>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.TicketId }).HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.AuthorUserId }).HasPrincipalKey(m => new { m.TenantId, m.UserId })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.TicketId, x.CreatedAt, x.Id });
    }
}

internal sealed class TenantCounterConfiguration : IEntityTypeConfiguration<TenantCounter>
{
    public void Configure(EntityTypeBuilder<TenantCounter> b)
    {
        b.ToTable("tenant_counters");
        b.HasKey(x => x.TenantId);
        b.Property(x => x.TenantId).ValueGeneratedNever();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

using Kulvar.Modules.Identity.Domain;
using Kulvar.Modules.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kulvar.Modules.Tenancy.Persistence;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("tenants", t => t.HasCheckConstraint("ck_tenants_status", "status IN ('Active','Suspended','DeletionPending','Purging','Deleted')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(120);
        b.Property(x => x.SuspendedReason).HasMaxLength(300);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PurgeAfter).HasFilter("status = 'DeletionPending'");
    }
}

internal sealed class InviteConfiguration : IEntityTypeConfiguration<Invite>
{
    public void Configure(EntityTypeBuilder<Invite> b)
    {
        b.ToTable("invites", t =>
        {
            t.HasCheckConstraint("ck_invites_role", "role IN ('Admin','Agent','Viewer')"); // Owner davetle verilemez.
            t.HasCheckConstraint("ck_invites_status", "status IN ('Pending','Accepted','Revoked')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Email).HasMaxLength(254);
        b.Property(x => x.NormalizedEmail).HasMaxLength(254);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.InvitedByUserId }).HasPrincipalKey(m => new { m.TenantId, m.UserId })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TokenHash).IsUnique();
        // Bir adrese şirket başına en çok bir bekleyen davet.
        b.HasIndex(x => new { x.TenantId, x.NormalizedEmail }).IsUnique().HasFilter("status = 'Pending'");
    }
}

internal sealed class OwnershipOfferConfiguration : IEntityTypeConfiguration<OwnershipOffer>
{
    public void Configure(EntityTypeBuilder<OwnershipOffer> b)
    {
        b.ToTable("ownership_offers", t => t.HasCheckConstraint("ck_ownership_offers_status", "status IN ('Pending','Accepted','Cancelled','Declined')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.FromUserId }).HasPrincipalKey(m => new { m.TenantId, m.UserId })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ToUserId }).HasPrincipalKey(m => new { m.TenantId, m.UserId })
            .OnDelete(DeleteBehavior.Restrict);
        // Şirket başına en çok bir açık teklif.
        b.HasIndex(x => x.TenantId).IsUnique().HasFilter("status = 'Pending'");
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> b)
    {
        b.ToTable("memberships", t =>
        {
            t.HasCheckConstraint("ck_memberships_role", "role IN ('Owner','Admin','Agent','Viewer')");
            t.HasCheckConstraint("ck_memberships_status", "status IN ('Active','Removed')");
        });
        // Bileşik birincil anahtar: diğer tabloların (atama, yazar, davet eden) bileşik FK'leri buna bağlanır.
        // Ad açıkça verilir: başka yapılandırmalar bu anahtarı önce "alternatif anahtar" olarak görürse adı değişmesin.
        b.HasKey(x => new { x.TenantId, x.UserId }).HasName("pk_memberships");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Ignore(x => x.IsActive);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.UserId);
    }
}

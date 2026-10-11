using Kulvar.Platform.Data;
using Kulvar.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kulvar.Modules.Customers;

/// <summary>Şirketin müşteri kaydı. E-posta ve telefon kişisel veridir: Viewer rolüne maskeli döner, log ve denetim kaydına yazılmaz.</summary>
public sealed class Customer : ITenantOwned
{
    private Customer() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? CompanyTitle { get; private set; }
    public string? Email { get; private set; }
    public string? NormalizedEmail { get; private set; }
    public string? Phone { get; private set; }
    public string? Notes { get; private set; }
    public string SearchText { get; private set; } = null!;
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; }

    /// <summary>Şirket kimliği çağıranın doğrulanmış kapsamından gelir (istek gövdesinden değil) ve sonradan değişmez.</summary>
    public static Customer Create(Guid tenantId, Guid createdByUserId, string name, string? companyTitle, string? email, string? phone, string? notes, DateTimeOffset now)
    {
        var customer = new Customer { Id = Ids.New(), TenantId = tenantId, CreatedByUserId = createdByUserId, CreatedAt = now, Version = 0 };
        customer.Update(name, companyTitle, email, phone, notes, now);
        return customer;
    }

    public void Update(string name, string? companyTitle, string? email, string? phone, string? notes, DateTimeOffset now)
    {
        Name = name;
        CompanyTitle = companyTitle;
        Email = email;
        NormalizedEmail = email is null ? null : EmailNormalizer.Normalize(email);
        Phone = phone;
        Notes = notes;
        UpdatedAt = now;
        Version++;
    }

    public static string MaskPhone(string phone)
    {
        var digits = phone.Where(char.IsAsciiDigit).ToArray();
        return digits.Length < 2 ? "***" : $"*** ** {new string(digits[^2..])}";
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.ToTable("customers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        // Bileşik FK'lerin hedefi: bir talep yalnız aynı şirketin müşterisine bağlanabilir.
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Name).HasMaxLength(120);
        b.Property(x => x.CompanyTitle).HasMaxLength(160);
        b.Property(x => x.Email).HasMaxLength(254);
        b.Property(x => x.NormalizedEmail).HasMaxLength(254);
        b.Property(x => x.Phone).HasMaxLength(40);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.SearchText)
            .HasComputedColumnSql("lower(name || ' ' || coalesce(company_title, '') || ' ' || coalesce(email, ''))", stored: true);
        b.Property(x => x.Version).IsConcurrencyToken();
        // Şirket içinde tekil; A ve B şirketleri aynı e-postayı ayrı ayrı tutabilir.
        b.HasIndex(x => new { x.TenantId, x.NormalizedEmail }).IsUnique().HasFilter("normalized_email IS NOT NULL");
        b.HasIndex(x => new { x.TenantId, x.CreatedAt, x.Id }).IsDescending(false, true, true);
        b.HasIndex(x => new { x.TenantId, x.Name, x.Id });
    }
}

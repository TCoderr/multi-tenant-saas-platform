using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Kulvar.Platform.Data;

/// <summary>Şirkete ait satır: <c>tenant_id</c> oluşturulurken sunucu tarafından verilir ve sonra değişmez.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

/// <summary>Modelin hangi modül derlemelerinden kurulacağını taşır (bileşim kökü doldurur).</summary>
public sealed class AppModelSource(IEnumerable<Assembly> assemblies)
{
    public IReadOnlyList<Assembly> Assemblies { get; } = assemblies.Distinct().ToList();
}

/// <summary>
/// Tek DbContext. Her iş birimi için yeni örnek üretilir (havuz yok); şirket kapsamı örnek ömrü boyunca en çok bir kez bağlanır.
/// Kapsam bağlanmamışsa filtre hiçbir satırla eşleşmeyen boş kimliği kullanır (fail closed).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, AppModelSource model) : DbContext(options)
{
    private Guid? _tenantId;

    public Guid TenantFilterId => _tenantId ?? Guid.Empty;

    internal void BindTenant(Guid tenantId)
    {
        if (_tenantId is { } current && current != tenantId)
        {
            throw new InvalidOperationException("Şirket kapsamı bu iş biriminde zaten bağlı; değiştirilemez.");
        }
        _tenantId = tenantId;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("app");
        foreach (var assembly in model.Assemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }

        var applyFilter = typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                applyFilter.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }

            // Enum'lar okunabilir metin olarak saklanır; CHECK kısıtları aynı adları kullanır.
            foreach (var property in entityType.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clrType.IsEnum)
                {
                    property.SetProviderClrType(typeof(string));
                }
            }
        }
    }

    private void ApplyTenantFilter<T>(ModelBuilder modelBuilder) where T : class, ITenantOwned =>
        modelBuilder.Entity<T>().HasQueryFilter(e => e.TenantId == TenantFilterId);
}

using Kulvar.Platform.Data;

namespace Kulvar.Platform.Tenancy;

/// <summary>Şirket kaydının (registry) çekirdek düzeyindeki okuma/kilit yardımcıları. Worker ve yürütücüler ortak kullanır.</summary>
public static class TenantRegistry
{
    public static async Task<TenantStatus?> GetStatusAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var value = await db.ScalarAsync("SELECT status FROM app.tenants WHERE id = $1", ct, tenantId);
        return value is string s ? Enum.Parse<TenantStatus>(s) : null;
    }

    /// <summary>
    /// Şirket satırını FOR NO KEY UPDATE kilitler ve güncel durumunu döner. Üyelik/koltuk işlemleri bununla kendi aralarında seri çalışır.
    /// Bilinçli olarak FOR UPDATE değildir: içerik yazmaları şirket satırına FK üzerinden FOR KEY SHARE alır; FOR UPDATE onların hepsini
    /// bekletirdi (ve onlar tarafından bekletilirdi). Bu kilit yalnız diğer üyelik işlemleriyle ve FOR SHARE alan işlerle çakışır.
    /// </summary>
    public static async Task<TenantStatus?> LockAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var value = await db.ScalarAsync("SELECT status FROM app.tenants WHERE id = $1 FOR NO KEY UPDATE", ct, tenantId);
        return value is string s ? Enum.Parse<TenantStatus>(s) : null;
    }

    /// <summary>
    /// Yaşam döngüsü değişikliği (silme isteği, temizleme) için FOR UPDATE: şirkete satır ekleyen (FK ile FOR KEY SHARE tutan) devam eden
    /// tüm yazmaların bitmesini bekler; böylece durum değiştikten sonra eski bir yazma commit edilemez.
    /// </summary>
    public static async Task<TenantStatus?> LockForLifecycleAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var value = await db.ScalarAsync("SELECT status FROM app.tenants WHERE id = $1 FOR UPDATE", ct, tenantId);
        return value is string s ? Enum.Parse<TenantStatus>(s) : null;
    }

    /// <summary>Şirket satırını FOR SHARE kilitler: iş bitene kadar durumu (ör. Purging'e) değiştirilemez.</summary>
    public static async Task<TenantStatus?> LockSharedAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var value = await db.ScalarAsync("SELECT status FROM app.tenants WHERE id = $1 FOR SHARE", ct, tenantId);
        return value is string s ? Enum.Parse<TenantStatus>(s) : null;
    }
}

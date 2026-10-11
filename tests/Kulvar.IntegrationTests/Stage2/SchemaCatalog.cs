namespace Kulvar.IntegrationTests.Stage2;

public enum TableClass
{
    /// <summary>Global kimlik: kullanıcı kapsamlı RLS.</summary>
    GlobalIdentity,

    /// <summary>Şirket kaydı: üyelik/kapsam RLS.</summary>
    Registry,

    /// <summary>Kiracı tablosu: tenant_id NOT NULL + kiracı politikası.</summary>
    Tenant,

    /// <summary>Kuyruk / teslimat hedefi: tenant_id boş olabilir; uygulama rolü sınırlı.</summary>
    Queue,

    /// <summary>Global referans veri (public fiyat): RLS yok, yalnız SELECT.</summary>
    Reference,

    /// <summary>Platform operasyon verisi (webhook inbox, platform denetimi): uygulama rolünün hiç yetkisi yok.</summary>
    Platform,
}

/// <summary>
/// Bir kiracı tablosu ve RLS testlerinde ona satır eklemenin yolu. <c>InsertSelect</c>, <c>p</c> adlı tek satırlık parametre
/// tablosundan okur: <c>p.tenant_id</c>, <c>p.owner_id</c> (o şirketin Owner'ı), <c>p.spare_id</c> (o şirkete üye olmayan kullanıcı).
/// </summary>
public sealed record TenantTable(string Name, string InsertSelect, bool AppCanUpdate, bool AppCanDelete)
{
    public string InsertSql => "WITH p AS (SELECT $1::uuid AS tenant_id, $2::uuid AS owner_id, $3::uuid AS spare_id) " + InsertSelect;
}

/// <summary>
/// <c>app</c> şemasındaki her tablonun sınıfı. Yeni tablo buraya eklenmezse <see cref="SchemaConventionTests"/> kırılır:
/// sınıflandırılmamış (dolayısıyla izolasyonu düşünülmemiş) tablo şemaya giremez.
/// </summary>
public static class SchemaCatalog
{
    public static readonly IReadOnlyDictionary<string, TableClass> Tables = new Dictionary<string, TableClass>
    {
        ["users"] = TableClass.GlobalIdentity,
        ["sessions"] = TableClass.GlobalIdentity,
        ["user_tokens"] = TableClass.GlobalIdentity,
        ["platform_operators"] = TableClass.GlobalIdentity,
        ["tenants"] = TableClass.Registry,
        ["memberships"] = TableClass.Tenant,
        ["audit_events"] = TableClass.Tenant,
        ["outbox_messages"] = TableClass.Queue,
        ["sink_messages"] = TableClass.Queue,
        ["customers"] = TableClass.Tenant,
        ["tickets"] = TableClass.Tenant,
        ["ticket_comments"] = TableClass.Tenant,
        ["tenant_counters"] = TableClass.Tenant,
        ["idempotency_keys"] = TableClass.Tenant,
        ["invites"] = TableClass.Tenant,
        ["ownership_offers"] = TableClass.Tenant,
        ["plans"] = TableClass.Reference,
        ["plan_versions"] = TableClass.Reference,
        ["subscriptions"] = TableClass.Tenant,
        ["usage_counters"] = TableClass.Tenant,
        ["usage_events"] = TableClass.Tenant,
        ["billing_operations"] = TableClass.Tenant,
        ["export_jobs"] = TableClass.Tenant,
        ["provider_events"] = TableClass.Platform,
        ["platform_audit_events"] = TableClass.Platform,
    };

    public static readonly IReadOnlyList<TenantTable> TenantTables =
    [
        new("memberships",
            "INSERT INTO app.memberships (tenant_id, user_id, role, status, created_at, version) SELECT p.tenant_id, p.spare_id, 'Viewer', 'Active', now(), 1 FROM p",
            AppCanUpdate: true, AppCanDelete: false),
        new("audit_events",
            "INSERT INTO app.audit_events (id, tenant_id, actor_user_id, action, target_type, data, occurred_at) SELECT gen_random_uuid(), p.tenant_id, p.owner_id, 'test.canary', 'test', '{}', now() FROM p WHERE p.spare_id IS NOT NULL",
            AppCanUpdate: false, AppCanDelete: false),
        // Sıra önemlidir: talep müşteriye, yorum talebe bağlanır (aynı şirket içinde).
        new("customers",
            "INSERT INTO app.customers (id, tenant_id, name, email, normalized_email, created_by_user_id, created_at, updated_at, version) SELECT gen_random_uuid(), p.tenant_id, 'CANARY RLS müşteri', 'canary-rls@example.test', 'canary-rls@example.test', p.owner_id, now(), now(), 1 FROM p WHERE p.spare_id IS NOT NULL",
            AppCanUpdate: true, AppCanDelete: true),
        new("tickets",
            "INSERT INTO app.tickets (id, tenant_id, number, customer_id, title, description, priority, status, created_by_user_id, created_at, updated_at, reopen_count, version) SELECT gen_random_uuid(), p.tenant_id, 9000, coalesce((SELECT c.id FROM app.customers c WHERE c.tenant_id = p.tenant_id LIMIT 1), p.spare_id), 'CANARY RLS talep', 'x', 'Normal', 'Open', p.owner_id, now(), now(), 0, 1 FROM p",
            AppCanUpdate: true, AppCanDelete: false),
        new("ticket_comments",
            "INSERT INTO app.ticket_comments (id, tenant_id, ticket_id, author_user_id, body, created_at) SELECT gen_random_uuid(), p.tenant_id, coalesce((SELECT t.id FROM app.tickets t WHERE t.tenant_id = p.tenant_id LIMIT 1), p.spare_id), p.owner_id, 'CANARY RLS yorum', now() FROM p",
            AppCanUpdate: false, AppCanDelete: false),
        new("tenant_counters",
            "INSERT INTO app.tenant_counters (tenant_id, next_ticket_number) SELECT p.tenant_id, 1 FROM p WHERE p.spare_id IS NOT NULL AND p.owner_id IS NOT NULL",
            AppCanUpdate: true, AppCanDelete: false),
        new("idempotency_keys",
            "INSERT INTO app.idempotency_keys (tenant_id, user_id, operation, key, request_hash, response_status, response_body, created_at, expires_at) SELECT p.tenant_id, p.owner_id, 'test', gen_random_uuid()::text, '\\x00'::bytea, 200, '{}', now(), now() + interval '1 day' FROM p WHERE p.spare_id IS NOT NULL",
            AppCanUpdate: true, AppCanDelete: false),
        new("invites",
            "INSERT INTO app.invites (id, tenant_id, email, normalized_email, role, status, token_hash, invited_by_user_id, created_at, expires_at) SELECT gen_random_uuid(), p.tenant_id, 'canary-rls-' || p.spare_id || '@example.test', 'canary-rls-' || p.spare_id || '@example.test', 'Agent', 'Pending', sha256(gen_random_uuid()::text::bytea), p.owner_id, now(), now() + interval '7 days' FROM p",
            AppCanUpdate: true, AppCanDelete: false),
        // Hedef (spare) kurulumda o şirketin üyesi yapılmıştır (yukarıdaki memberships satırı).
        new("ownership_offers",
            "INSERT INTO app.ownership_offers (id, tenant_id, from_user_id, to_user_id, demote_self, status, created_at, expires_at) SELECT gen_random_uuid(), p.tenant_id, p.owner_id, p.spare_id, true, 'Pending', now(), now() + interval '3 days' FROM p",
            AppCanUpdate: true, AppCanDelete: false),
        // Abonelik satırı şirket oluşturulurken zaten açılır; kurulumda çakışma yok sayılır (RLS kontrolü çakışmadan önce yapılır).
        new("subscriptions",
            "INSERT INTO app.subscriptions (tenant_id, plan_version_id, status, epoch, provider_sequence, updated_at, version) SELECT p.tenant_id, '018f1000-0000-7000-8000-000000000001', 'Trialing', 0, 0, now(), 1 FROM p WHERE p.owner_id IS NOT NULL AND p.spare_id IS NOT NULL ON CONFLICT (tenant_id) DO NOTHING",
            AppCanUpdate: true, AppCanDelete: false),
        new("usage_counters",
            "INSERT INTO app.usage_counters (tenant_id, metric, period_start, used) SELECT p.tenant_id, 'rls_test', date '2026-01-01', 7 FROM p WHERE p.owner_id IS NOT NULL AND p.spare_id IS NOT NULL",
            AppCanUpdate: true, AppCanDelete: false),
        new("usage_events",
            "INSERT INTO app.usage_events (tenant_id, metric, source_id, period_start, occurred_at) SELECT p.tenant_id, 'rls_test', gen_random_uuid(), date '2026-01-01', now() FROM p WHERE p.owner_id IS NOT NULL AND p.spare_id IS NOT NULL",
            AppCanUpdate: false, AppCanDelete: false),
        new("billing_operations",
            "INSERT INTO app.billing_operations (id, tenant_id, merchant_reference, target_plan_version_id, amount_minor, currency, status, epoch, requested_by_user_id, attempts, created_at, updated_at, version) SELECT gen_random_uuid(), p.tenant_id, 'mref_rls_' || gen_random_uuid(), '018f1000-0000-7000-8000-000000000002', 49000, 'TRY', 'Declined', 0, p.owner_id, 0, now(), now(), 1 FROM p WHERE p.spare_id IS NOT NULL",
            AppCanUpdate: false, AppCanDelete: false),
        new("export_jobs",
            "INSERT INTO app.export_jobs (id, tenant_id, requested_by_user_id, status, created_at, attempts, lease_token) SELECT gen_random_uuid(), p.tenant_id, p.owner_id, 'Failed', now(), 0, 0 FROM p WHERE p.spare_id IS NOT NULL",
            AppCanUpdate: true, AppCanDelete: false),
    ];

    /// <summary>Uygulama rolünün hiçbir yetkisinin olmaması gereken platform tabloları.</summary>
    public static readonly IReadOnlyList<string> AppForbidden = ["provider_events", "platform_audit_events"];

    /// <summary>Platform operatörü rolünün hiçbir yetkisinin olmaması gereken tablolar (içerik ve kişisel veri).</summary>
    public static readonly IReadOnlyList<string> PlatformForbidden =
    [
        "users", "sessions", "user_tokens", "memberships", "audit_events", "sink_messages",
        "customers", "tickets", "ticket_comments", "tenant_counters", "idempotency_keys", "invites", "ownership_offers",
        "usage_counters", "usage_events", "export_jobs",
    ];
}

namespace Kulvar.Api.Migrations.Sql;

/// <summary>
/// 0004: plan referans verisi, mevcut şirketlere deneme aboneliği (veri taşıyan adım), abonelik/kullanım tablolarının RLS politikaları ve yetkileri.
/// </summary>
internal static class M0004
{
    public const string Up = """
        ------------------------------------------------------------------ referans veri (public fiyat listesi; RLS yok)
        INSERT INTO app.plans (code, name, sort_order) VALUES
          ('trial', 'Deneme', 1), ('starter', 'Starter', 2), ('team', 'Team', 3);
        INSERT INTO app.plan_versions (id, plan_code, version, price_minor, currency, seat_limit, monthly_ticket_limit, export_enabled, trial_days, is_current) VALUES
          ('018f1000-0000-7000-8000-000000000001', 'trial',   1,      0, 'TRY',  3,   20, false, 14,   true),
          ('018f1000-0000-7000-8000-000000000002', 'starter', 1,  49000, 'TRY',  5,  200, true,  NULL, true),
          ('018f1000-0000-7000-8000-000000000003', 'team',    1, 149000, 'TRY', 25, 2000, true,  NULL, true);
        GRANT SELECT ON app.plans, app.plan_versions TO kulvar_app, kulvar_worker, kulvar_platform;

        ALTER TABLE app.subscriptions  ADD CONSTRAINT fk_subscriptions_tenants_tenant_id  FOREIGN KEY (tenant_id) REFERENCES app.tenants (id) ON DELETE RESTRICT;
        ALTER TABLE app.usage_counters ADD CONSTRAINT fk_usage_counters_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES app.tenants (id) ON DELETE RESTRICT;
        ALTER TABLE app.usage_events   ADD CONSTRAINT fk_usage_events_tenants_tenant_id   FOREIGN KEY (tenant_id) REFERENCES app.tenants (id) ON DELETE RESTRICT;

        ------------------------------------------------------------------ veri tasiyan adim: mevcut sirketlere deneme aboneligi
        -- Migration rolu FORCE ROW LEVEL SECURITY nedeniyle sirket satirlarini goremez. Bu adim icin (ve yalniz bu transaction icinde)
        -- sirket kaydinda FORCE kaldirilir, satirlar okunur ve FORCE geri konur. DDL transaction'a dahil oldugundan arada pencere yoktur.
        ALTER TABLE app.tenants NO FORCE ROW LEVEL SECURITY;
        INSERT INTO app.subscriptions (tenant_id, plan_version_id, status, epoch, trial_ends_at, provider_sequence, updated_at, version)
        SELECT t.id, '018f1000-0000-7000-8000-000000000001', 'Trialing', 0, now() + interval '14 days', 0, now(), 1
        FROM app.tenants t
        WHERE t.status NOT IN ('Purging', 'Deleted');
        ALTER TABLE app.tenants FORCE ROW LEVEL SECURITY;

        ------------------------------------------------------------------ subscriptions
        ALTER TABLE app.subscriptions ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.subscriptions FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.subscriptions TO kulvar_app;
        GRANT SELECT, UPDATE ON app.subscriptions TO kulvar_worker;
        GRANT SELECT ON app.subscriptions TO kulvar_platform;
        CREATE POLICY tenant_isolation ON app.subscriptions FOR ALL TO kulvar_app
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());
        -- Abonelik METADATA'si (plan, durum, tarihler): worker zamanlayicilari ve platform operatoru sirketler arasi gorur.
        CREATE POLICY subscriptions_meta ON app.subscriptions FOR ALL TO kulvar_worker, kulvar_platform USING (true) WITH CHECK (true);

        ------------------------------------------------------------------ usage_counters / usage_events
        ALTER TABLE app.usage_counters ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.usage_counters FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.usage_counters TO kulvar_app;
        GRANT SELECT, DELETE ON app.usage_counters TO kulvar_worker;
        CREATE POLICY tenant_isolation ON app.usage_counters FOR ALL TO kulvar_app, kulvar_worker
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());

        ALTER TABLE app.usage_events ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.usage_events FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT ON app.usage_events TO kulvar_app;
        GRANT SELECT, DELETE ON app.usage_events TO kulvar_worker;
        CREATE POLICY tenant_isolation ON app.usage_events FOR ALL TO kulvar_app, kulvar_worker
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());
        """;

    public const string DownBefore = "SELECT 1;";

    public const string DownAfter = "SELECT 1;";
}

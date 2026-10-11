namespace Kulvar.Api.Migrations.Sql;

/// <summary>
/// 0005: ödeme işlemleri, webhook inbox'ı ve platform denetim kaydı için RLS/yetkiler; simüle ödeme sağlayıcısının kendi şeması (fakepsp).
/// </summary>
internal static class M0005
{
    public const string Up = """
        ALTER TABLE app.billing_operations ADD CONSTRAINT fk_billing_operations_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES app.tenants (id) ON DELETE RESTRICT;

        ------------------------------------------------------------------ billing_operations
        ALTER TABLE app.billing_operations ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.billing_operations FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT ON app.billing_operations TO kulvar_app;
        GRANT SELECT, UPDATE ON app.billing_operations TO kulvar_worker, kulvar_platform;
        CREATE POLICY tenant_isolation ON app.billing_operations FOR ALL TO kulvar_app
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());
        -- Odeme islemi METADATA'si (tutar, durum, referans): worker mutabakati ve operator cozum kuyrugu sirketler arasi gorur. Kisisel veri icermez.
        CREATE POLICY billing_operations_meta ON app.billing_operations FOR ALL TO kulvar_worker, kulvar_platform USING (true) WITH CHECK (true);

        ------------------------------------------------------------------ provider_events (webhook inbox; global)
        ALTER TABLE app.provider_events ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.provider_events FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.provider_events TO kulvar_platform;   -- alim (API)
        GRANT SELECT, UPDATE ON app.provider_events TO kulvar_worker;             -- isleme
        CREATE POLICY provider_events_inbox ON app.provider_events FOR ALL TO kulvar_platform, kulvar_worker USING (true) WITH CHECK (true);

        ------------------------------------------------------------------ platform_audit_events
        ALTER TABLE app.platform_audit_events ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.platform_audit_events FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT ON app.platform_audit_events TO kulvar_platform;
        GRANT INSERT ON app.platform_audit_events TO kulvar_worker;
        CREATE POLICY platform_audit_read ON app.platform_audit_events FOR SELECT TO kulvar_platform USING (true);
        CREATE POLICY platform_audit_write ON app.platform_audit_events FOR INSERT TO kulvar_platform, kulvar_worker WITH CHECK (true);

        ------------------------------------------------------------------ fakepsp: simule odeme saglayicisinin kendi durumu
        -- Dis sistemi temsil eder; urun tablolarina FK yoktur. Yalniz worker rolu erisir (platform rolu yalniz simulasyon istegi ekleyebilir).
        CREATE TABLE fakepsp.subscriptions (
          id                 text PRIMARY KEY,
          customer_reference text NOT NULL UNIQUE,
          plan_code          text NOT NULL,
          sequence           bigint NOT NULL
        );
        CREATE TABLE fakepsp.operations (
          merchant_reference text PRIMARY KEY,
          subscription_id    text NULL,
          plan_code          text NOT NULL,
          amount_minor       bigint NOT NULL,
          status             text NOT NULL CHECK (status IN ('none', 'pending', 'succeeded', 'declined')),
          script             text NOT NULL,
          sequence           bigint NOT NULL DEFAULT 0,
          calls              integer NOT NULL,
          charge_count       integer NOT NULL,
          refunded           boolean NOT NULL,
          created_at         timestamptz NOT NULL
        );
        CREATE TABLE fakepsp.webhooks (
          id           text PRIMARY KEY,
          payload      text NOT NULL,
          created_at   timestamptz NOT NULL,
          delivered_at timestamptz NULL,
          attempts     integer NOT NULL
        );
        CREATE TABLE fakepsp.simulation_requests (
          id                 uuid PRIMARY KEY,
          customer_reference text NOT NULL,
          kind               text NOT NULL CHECK (kind IN ('renewal_failed', 'renewed')),
          created_at         timestamptz NOT NULL,
          processed_at       timestamptz NULL
        );
        GRANT USAGE ON SCHEMA fakepsp TO kulvar_worker, kulvar_platform;
        GRANT SELECT, INSERT, UPDATE ON fakepsp.subscriptions, fakepsp.operations, fakepsp.webhooks TO kulvar_worker;
        GRANT SELECT, UPDATE ON fakepsp.simulation_requests TO kulvar_worker;
        GRANT INSERT ON fakepsp.simulation_requests TO kulvar_platform;
        """;

    public const string DownBefore = """
        DROP TABLE IF EXISTS fakepsp.simulation_requests;
        DROP TABLE IF EXISTS fakepsp.webhooks;
        DROP TABLE IF EXISTS fakepsp.operations;
        DROP TABLE IF EXISTS fakepsp.subscriptions;
        REVOKE USAGE ON SCHEMA fakepsp FROM kulvar_worker, kulvar_platform;
        """;

    public const string DownAfter = "SELECT 1;";
}

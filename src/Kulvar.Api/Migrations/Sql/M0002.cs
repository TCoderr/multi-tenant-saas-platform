namespace Kulvar.Api.Migrations.Sql;

/// <summary>0002: müşteri, talep, yorum, talep sayacı ve idempotency tablolarının RLS politikaları, yetkileri ve model dışı FK'ler.</summary>
internal static class M0002
{
    public const string Up = """
        -- EF modelinin disinda kalan FK'ler.
        ALTER TABLE app.customers        ADD CONSTRAINT fk_customers_tenants_tenant_id        FOREIGN KEY (tenant_id) REFERENCES app.tenants (id) ON DELETE RESTRICT;
        ALTER TABLE app.tickets          ADD CONSTRAINT fk_tickets_tenants_tenant_id          FOREIGN KEY (tenant_id) REFERENCES app.tenants (id) ON DELETE RESTRICT;
        ALTER TABLE app.ticket_comments  ADD CONSTRAINT fk_ticket_comments_tenants_tenant_id  FOREIGN KEY (tenant_id) REFERENCES app.tenants (id) ON DELETE RESTRICT;
        ALTER TABLE app.idempotency_keys ADD CONSTRAINT fk_idempotency_keys_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES app.tenants (id) ON DELETE RESTRICT;
        -- Talep etkinligi yalniz AYNI sirketin talebine baglanabilir.
        ALTER TABLE app.audit_events ADD CONSTRAINT fk_audit_events_tickets_tenant_id_ticket_id
          FOREIGN KEY (tenant_id, ticket_id) REFERENCES app.tickets (tenant_id, id) ON DELETE RESTRICT;

        ------------------------------------------------------------------ customers
        ALTER TABLE app.customers ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.customers FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE, DELETE ON app.customers TO kulvar_app;
        GRANT SELECT, DELETE ON app.customers TO kulvar_worker;
        CREATE POLICY tenant_isolation ON app.customers FOR ALL TO kulvar_app, kulvar_worker
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());

        ------------------------------------------------------------------ tickets
        ALTER TABLE app.tickets ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.tickets FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.tickets TO kulvar_app;
        GRANT SELECT, DELETE ON app.tickets TO kulvar_worker;
        CREATE POLICY tenant_isolation ON app.tickets FOR ALL TO kulvar_app, kulvar_worker
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());

        ------------------------------------------------------------------ ticket_comments
        ALTER TABLE app.ticket_comments ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.ticket_comments FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT ON app.ticket_comments TO kulvar_app;
        GRANT SELECT, DELETE ON app.ticket_comments TO kulvar_worker;
        CREATE POLICY tenant_isolation ON app.ticket_comments FOR ALL TO kulvar_app, kulvar_worker
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());

        ------------------------------------------------------------------ tenant_counters
        ALTER TABLE app.tenant_counters ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.tenant_counters FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.tenant_counters TO kulvar_app;
        GRANT SELECT, DELETE ON app.tenant_counters TO kulvar_worker;
        CREATE POLICY tenant_isolation ON app.tenant_counters FOR ALL TO kulvar_app, kulvar_worker
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());

        ------------------------------------------------------------------ idempotency_keys
        ALTER TABLE app.idempotency_keys ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.idempotency_keys FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.idempotency_keys TO kulvar_app;
        GRANT SELECT, DELETE ON app.idempotency_keys TO kulvar_worker;
        CREATE POLICY tenant_isolation ON app.idempotency_keys FOR ALL TO kulvar_app, kulvar_worker
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());
        """;

    public const string DownBefore = """
        ALTER TABLE app.audit_events DROP CONSTRAINT IF EXISTS fk_audit_events_tickets_tenant_id_ticket_id;
        """;

    public const string DownAfter = "SELECT 1;";
}

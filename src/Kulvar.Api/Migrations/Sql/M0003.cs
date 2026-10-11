namespace Kulvar.Api.Migrations.Sql;

/// <summary>0003: davet ve sahiplik teklifi tablolarının RLS politikaları ve yetkileri.</summary>
internal static class M0003
{
    public const string Up = """
        ------------------------------------------------------------------ invites
        ALTER TABLE app.invites ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.invites FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.invites TO kulvar_app;
        GRANT SELECT, UPDATE, DELETE ON app.invites TO kulvar_worker;
        CREATE POLICY tenant_isolation ON app.invites FOR ALL TO kulvar_app, kulvar_worker
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());
        -- Davet kabulu: sirket kapsami yokken, yalniz sunulan token'in hash'ine sahip TEK satir okunabilir (arama anahtari kapsami).
        CREATE POLICY invites_token_lookup ON app.invites FOR SELECT TO kulvar_app
          USING (token_hash = decode(nullif(current_setting('app.invite_token_hash', true), ''), 'hex'));

        ------------------------------------------------------------------ ownership_offers
        ALTER TABLE app.ownership_offers ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.ownership_offers FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.ownership_offers TO kulvar_app;
        GRANT SELECT, DELETE ON app.ownership_offers TO kulvar_worker;
        CREATE POLICY tenant_isolation ON app.ownership_offers FOR ALL TO kulvar_app, kulvar_worker
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());
        """;

    public const string DownBefore = "SELECT 1;";

    public const string DownAfter = "SELECT 1;";
}

namespace Kulvar.Api.Migrations.Sql;

/// <summary>0006: export işleri tablosunun RLS politikaları ve yetkileri.</summary>
internal static class M0006
{
    public const string Up = """
        ALTER TABLE app.export_jobs ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.export_jobs FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.export_jobs TO kulvar_app;
        GRANT SELECT, UPDATE, DELETE ON app.export_jobs TO kulvar_worker;
        CREATE POLICY tenant_isolation ON app.export_jobs FOR ALL TO kulvar_app
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());
        -- Is kuyrugu METADATA'si (kimlik, durum, kiralama): worker sirketler arasi gorur. Export ICERIGI (musteri/talep) icin
        -- worker yine sirket kapsami ayarlamak zorundadir; o tablolarda boyle bir politika yoktur.
        CREATE POLICY export_jobs_queue ON app.export_jobs FOR ALL TO kulvar_worker USING (true) WITH CHECK (true);
        """;

    public const string DownBefore = "SELECT 1;";

    public const string DownAfter = "SELECT 1;";
}

namespace Kulvar.Api.Migrations.Sql;

/// <summary>
/// 0001: şema yetkileri, RLS yardımcı fonksiyonları, kimlik/şirket/üyelik/kuyruk tablolarının politikaları ve
/// "en az bir Owner" trigger'ı. Roller migration tarafından oluşturulmaz (db/bootstrap); burada yalnız varlıkları doğrulanır.
/// </summary>
internal static class M0001
{
    public const string Up = """
        DO $$
        DECLARE missing text;
        BEGIN
          SELECT string_agg(r, ', ') INTO missing
          FROM unnest(ARRAY['kulvar_app','kulvar_platform','kulvar_worker']) AS r
          WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r);
          IF missing IS NOT NULL THEN
            RAISE EXCEPTION 'Runtime rolleri eksik: %. Once db/bootstrap betikleri calistirilmali.', missing;
          END IF;
        END $$;

        GRANT USAGE ON SCHEMA app TO kulvar_app, kulvar_platform, kulvar_worker;

        -- Kapsam ayarlanmamissa NULL doner: "tenant_id = NULL" hicbir satirla eslesmez (fail closed).
        CREATE FUNCTION app.current_tenant_id() RETURNS uuid LANGUAGE sql STABLE
          SET search_path = pg_catalog
          AS $fn$ SELECT nullif(current_setting('app.tenant_id', true), '')::uuid $fn$;

        CREATE FUNCTION app.current_user_id() RETURNS uuid LANGUAGE sql STABLE
          SET search_path = pg_catalog
          AS $fn$ SELECT nullif(current_setting('app.user_id', true), '')::uuid $fn$;

        -- EF modelinin disinda kalan FK'ler (cekirdek tablolar sirket kaydina baglanir).
        ALTER TABLE app.audit_events    ADD CONSTRAINT fk_audit_events_tenants_tenant_id    FOREIGN KEY (tenant_id) REFERENCES app.tenants (id) ON DELETE RESTRICT;
        ALTER TABLE app.outbox_messages ADD CONSTRAINT fk_outbox_messages_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES app.tenants (id) ON DELETE RESTRICT;
        ALTER TABLE app.sink_messages   ADD CONSTRAINT fk_sink_messages_tenants_tenant_id   FOREIGN KEY (tenant_id) REFERENCES app.tenants (id) ON DELETE RESTRICT;

        ------------------------------------------------------------------ users (global kimlik)
        ALTER TABLE app.users ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.users FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.users TO kulvar_app;
        GRANT SELECT ON app.users TO kulvar_worker;
        -- Kendim, login aramasi (yalniz dogrulanacak adresin satiri) veya kapsamdaki sirketin uyesi.
        CREATE POLICY users_select ON app.users FOR SELECT TO kulvar_app, kulvar_worker
          USING (id = app.current_user_id()
                 OR normalized_email = nullif(current_setting('app.login_email', true), '')
                 OR EXISTS (SELECT 1 FROM app.memberships m WHERE m.user_id = users.id AND m.tenant_id = app.current_tenant_id()));
        CREATE POLICY users_insert ON app.users FOR INSERT TO kulvar_app
          WITH CHECK (id = app.current_user_id());
        CREATE POLICY users_update ON app.users FOR UPDATE TO kulvar_app
          USING (id = app.current_user_id() OR normalized_email = nullif(current_setting('app.login_email', true), ''))
          WITH CHECK (id = app.current_user_id() OR normalized_email = nullif(current_setting('app.login_email', true), ''));

        -- Kurulum komutu (operator kaydi) icin: sahip rol yalniz aranan e-postanin satirini gorebilir; baska hicbir kullanici satirini goremez.
        CREATE POLICY users_setup_lookup ON app.users FOR SELECT TO kulvar_owner
          USING (normalized_email = nullif(current_setting('app.login_email', true), ''));

        ------------------------------------------------------------------ sessions
        ALTER TABLE app.sessions ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.sessions FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.sessions TO kulvar_app;
        GRANT SELECT, DELETE ON app.sessions TO kulvar_worker;
        CREATE POLICY sessions_owner ON app.sessions FOR ALL TO kulvar_app
          USING (user_id = app.current_user_id()) WITH CHECK (user_id = app.current_user_id());
        CREATE POLICY sessions_cleanup ON app.sessions FOR ALL TO kulvar_worker USING (true) WITH CHECK (false);

        ------------------------------------------------------------------ user_tokens
        ALTER TABLE app.user_tokens ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.user_tokens FORCE ROW LEVEL SECURITY;
        GRANT SELECT, UPDATE ON app.user_tokens TO kulvar_app;
        GRANT SELECT, INSERT, DELETE ON app.user_tokens TO kulvar_worker;
        CREATE POLICY user_tokens_owner ON app.user_tokens FOR ALL TO kulvar_app
          USING (user_id = app.current_user_id()) WITH CHECK (user_id = app.current_user_id());
        CREATE POLICY user_tokens_worker_select ON app.user_tokens FOR SELECT TO kulvar_worker USING (true);
        CREATE POLICY user_tokens_worker_insert ON app.user_tokens FOR INSERT TO kulvar_worker WITH CHECK (user_id = app.current_user_id());
        CREATE POLICY user_tokens_worker_delete ON app.user_tokens FOR DELETE TO kulvar_worker USING (true);

        ------------------------------------------------------------------ platform_operators
        -- Uygulama rolu yalniz kendi satirini okuyabilir; yazma yetkisi hicbir runtime rolunde yoktur (kurulum komutu sahip rolle ekler).
        ALTER TABLE app.platform_operators ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.platform_operators FORCE ROW LEVEL SECURITY;
        GRANT SELECT ON app.platform_operators TO kulvar_app;
        CREATE POLICY platform_operators_self ON app.platform_operators FOR SELECT TO kulvar_app
          USING (user_id = app.current_user_id());
        CREATE POLICY platform_operators_setup ON app.platform_operators FOR ALL TO kulvar_owner USING (true) WITH CHECK (true);

        ------------------------------------------------------------------ memberships
        ALTER TABLE app.memberships ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.memberships FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.memberships TO kulvar_app;
        GRANT SELECT, DELETE ON app.memberships TO kulvar_worker;
        -- Sirket kapsami VEYA (sirket kapsami yokken) kendi uyeliklerim. Iki kapsam birbirini dislar:
        -- sirket kapsamina girildikten sonra baska sirketlerdeki uyeliklerim gorunmez.
        CREATE POLICY memberships_select ON app.memberships FOR SELECT TO kulvar_app, kulvar_worker
          USING (tenant_id = app.current_tenant_id()
                 OR (app.current_tenant_id() IS NULL AND user_id = app.current_user_id()));
        CREATE POLICY memberships_insert ON app.memberships FOR INSERT TO kulvar_app
          WITH CHECK (tenant_id = app.current_tenant_id());
        CREATE POLICY memberships_update ON app.memberships FOR UPDATE TO kulvar_app
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());
        CREATE POLICY memberships_purge ON app.memberships FOR DELETE TO kulvar_worker
          USING (tenant_id = app.current_tenant_id());

        ------------------------------------------------------------------ tenants (kayit)
        ALTER TABLE app.tenants ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.tenants FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT, UPDATE ON app.tenants TO kulvar_app;
        GRANT SELECT, UPDATE ON app.tenants TO kulvar_worker, kulvar_platform;
        CREATE POLICY tenants_select ON app.tenants FOR SELECT TO kulvar_app
          USING (id = app.current_tenant_id()
                 OR (app.current_tenant_id() IS NULL AND EXISTS (
                       SELECT 1 FROM app.memberships m
                       WHERE m.tenant_id = tenants.id AND m.user_id = app.current_user_id() AND m.status = 'Active')));
        CREATE POLICY tenants_insert ON app.tenants FOR INSERT TO kulvar_app
          WITH CHECK (id = app.current_tenant_id());
        CREATE POLICY tenants_update ON app.tenants FOR UPDATE TO kulvar_app
          USING (id = app.current_tenant_id()) WITH CHECK (id = app.current_tenant_id());
        -- Worker ve platform operatoru sirket METADATA'sini sirketler arasi gorur; icerik tablolarinda boyle bir politika yoktur.
        CREATE POLICY tenants_meta ON app.tenants FOR ALL TO kulvar_worker, kulvar_platform USING (true) WITH CHECK (true);

        ------------------------------------------------------------------ audit_events (kiraci)
        ALTER TABLE app.audit_events ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.audit_events FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT ON app.audit_events TO kulvar_app;
        GRANT SELECT, INSERT, DELETE ON app.audit_events TO kulvar_worker;
        CREATE POLICY tenant_isolation ON app.audit_events FOR ALL TO kulvar_app, kulvar_worker
          USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());

        ------------------------------------------------------------------ outbox_messages (kuyruk)
        ALTER TABLE app.outbox_messages ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.outbox_messages FORCE ROW LEVEL SECURITY;
        GRANT INSERT ON app.outbox_messages TO kulvar_app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON app.outbox_messages TO kulvar_worker;
        GRANT SELECT, UPDATE ON app.outbox_messages TO kulvar_platform;
        -- Uygulama yalniz kendi kapsaminda mesaj ekleyebilir; okuyamaz.
        CREATE POLICY outbox_enqueue ON app.outbox_messages FOR INSERT TO kulvar_app
          WITH CHECK (tenant_id = app.current_tenant_id()
                      OR (tenant_id IS NULL AND user_id = app.current_user_id()));
        CREATE POLICY outbox_queue ON app.outbox_messages FOR ALL TO kulvar_worker, kulvar_platform USING (true) WITH CHECK (true);

        ------------------------------------------------------------------ sink_messages (demo posta kutusu)
        ALTER TABLE app.sink_messages ENABLE ROW LEVEL SECURITY;
        ALTER TABLE app.sink_messages FORCE ROW LEVEL SECURITY;
        GRANT SELECT ON app.sink_messages TO kulvar_app;
        GRANT SELECT, INSERT, DELETE ON app.sink_messages TO kulvar_worker;
        -- Yalniz alicinin kendi adresi.
        CREATE POLICY sink_recipient ON app.sink_messages FOR SELECT TO kulvar_app
          USING (recipient_normalized_email = (SELECT u.normalized_email FROM app.users u WHERE u.id = app.current_user_id()));
        CREATE POLICY sink_delivery ON app.sink_messages FOR ALL TO kulvar_worker USING (true) WITH CHECK (true);

        ------------------------------------------------------------------ en az bir Owner
        CREATE FUNCTION app.assert_tenant_has_owner() RETURNS trigger LANGUAGE plpgsql
          SET search_path = pg_catalog, app
          AS $fn$
        DECLARE
          t  uuid;
          st text;
        BEGIN
          IF TG_OP = 'DELETE' THEN t := OLD.tenant_id; ELSE t := NEW.tenant_id; END IF;
          -- FOR NO KEY UPDATE: uyelik islemleriyle seri calisir; sirkete FK ile baglanan icerik yazmalarini (FOR KEY SHARE) bekletmez.
          SELECT status INTO st FROM app.tenants WHERE id = t FOR NO KEY UPDATE;
          IF st IS NULL THEN
            -- Sirket satiri gorunmuyorsa (kapsam yok) kural dogrulanamaz: reddet.
            RAISE EXCEPTION 'tenant % is not visible in the current scope', t USING ERRCODE = '42501';
          END IF;
          IF st IN ('Purging', 'Deleted') THEN
            RETURN NULL;
          END IF;
          IF NOT EXISTS (SELECT 1 FROM app.memberships WHERE tenant_id = t AND role = 'Owner' AND status = 'Active') THEN
            RAISE EXCEPTION 'tenant % must keep at least one active owner', t
              USING ERRCODE = '23514', CONSTRAINT = 'ck_tenant_has_owner';
          END IF;
          RETURN NULL;
        END
        $fn$;

        CREATE CONSTRAINT TRIGGER trg_tenant_has_owner
          AFTER INSERT OR UPDATE OR DELETE ON app.memberships
          DEFERRABLE INITIALLY DEFERRED
          FOR EACH ROW EXECUTE FUNCTION app.assert_tenant_has_owner();
        """;

    /// <summary>Tablolar düşürülmeden önce: başka tabloya başvuran politikalar kaldırılır (bağımlılık).</summary>
    public const string DownBefore = """
        DROP POLICY IF EXISTS users_select ON app.users;
        DROP POLICY IF EXISTS tenants_select ON app.tenants;
        DROP POLICY IF EXISTS sink_recipient ON app.sink_messages;
        """;

    public const string DownAfter = """
        DROP FUNCTION IF EXISTS app.assert_tenant_has_owner();
        DROP FUNCTION IF EXISTS app.current_tenant_id();
        DROP FUNCTION IF EXISTS app.current_user_id();
        REVOKE USAGE ON SCHEMA app FROM kulvar_app, kulvar_platform, kulvar_worker;
        """;
}

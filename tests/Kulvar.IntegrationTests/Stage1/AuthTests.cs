using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Npgsql;

namespace Kulvar.IntegrationTests.Stage1;

public sealed class AuthTests(TestEnvironment env)
{
    private World World => env.World;

    [Fact]
    public async Task Signup_SameResponse_ForNewAndExistingEmail()
    {
        var email = World.UniqueEmail("signup");
        var client = env.App.NewClient();

        var first = await client.PostAsync("/api/auth/signup", new { email, displayName = "İlk Kayıt", password = World.DefaultPassword });
        var firstBody = await first.Content.ReadAsStringAsync();
        // Aynı adres, farklı büyük/küçük harf ve farklı parola: hesap var ama yanıt aynı olmalı.
        var second = await client.PostAsync("/api/auth/signup", new { email = email.ToUpperInvariant(), displayName = "Başkası", password = "Bambaska-Bir-Parola-9!" });
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(firstBody, secondBody);
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.users WHERE normalized_email = $1", email));

        // İkinci kayıt mevcut hesabın parolasını değiştirmemiştir.
        await env.App.NewClient().LoginAsync(email, "Bambaska-Bir-Parola-9!").ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "invalid_credentials");
        await env.App.NewClient().LoginAsync(email, World.DefaultPassword).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_UnknownEmail_And_WrongPassword_SameResponse()
    {
        var user = await World.CreateUnverifiedUserAsync();
        var unknown = await env.App.NewClient().LoginAsync(World.UniqueEmail("nobody"), World.DefaultPassword);
        var wrong = await env.App.NewClient().LoginAsync(user.Email, "yanlis-parola-yanlis");

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var a = await unknown.JsonAsync();
        var b = await wrong.JsonAsync();
        a.Remove("traceId");
        b.Remove("traceId");
        Assert.Equal(a.ToJsonString(), b.ToJsonString());
    }

    [Fact]
    public async Task Logout_RevokesSession_CopiedCookieRejected()
    {
        var user = await World.CreateUnverifiedUserAsync();
        var stolen = user.Client.Clone(); // Saldırgan cookie'yi logout'tan önce kopyaladı.
        await stolen.GetAsync("/api/me").ShouldBeAsync(HttpStatusCode.OK);

        await user.Client.LogoutAsync().ShouldBeAsync(HttpStatusCode.NoContent);

        await stolen.GetAsync("/api/me").ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        await user.Client.GetAsync("/api/me").ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Session_ExpiresServerSide_AfterLifetime()
    {
        var user = await World.CreateUnverifiedUserAsync();
        // Cookie bileti de aynı anda biter; satırı ayrıca geçersiz kılmak için süreyi veritabanında kısaltıyoruz.
        await env.AdminExecuteAsync("UPDATE app.sessions SET expires_at = $2 WHERE user_id = $1", user.Id, env.Clock.GetUtcNow().AddSeconds(-1));
        await user.Client.GetAsync("/api/me").ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task UnsafeRequest_WithoutCsrfToken_Rejected()
    {
        var user = await World.CreateUserAsync();
        var noCsrf = user.Client.Clone();
        noCsrf.AutoCsrf = false;
        noCsrf.CsrfToken = null;

        await noCsrf.PostAsync("/api/tenants", new { name = "CSRF'siz Şirket" }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "csrf_invalid");
        await noCsrf.PostAsync("/api/auth/logout").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "csrf_invalid");
        // Oturum hâlâ geçerli: logout uygulanmadı.
        await user.Client.GetAsync("/api/me").ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CsrfToken_FromBeforeLogin_RejectedAfterLogin()
    {
        var email = World.UniqueEmail("csrf");
        var client = env.App.NewClient();
        await client.PostAsync("/api/auth/signup", new { email, displayName = "CSRF", password = World.DefaultPassword }).ShouldBeAsync(HttpStatusCode.Accepted);
        var anonymousToken = client.CsrfToken;

        await client.PostAsync("/api/auth/login", new { email, password = World.DefaultPassword }).ShouldBeAsync(HttpStatusCode.OK);

        // Anonimken alınan token artık bu kimlik için geçerli değildir.
        client.AutoCsrf = false;
        client.CsrfToken = anonymousToken;
        await client.PostAsync("/api/auth/verify-email/resend").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "csrf_invalid");

        await client.RefreshCsrfAsync();
        await client.PostAsync("/api/auth/verify-email/resend").ShouldBeAsync(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Login_LocksAfterFailures_GenericResponse()
    {
        var user = await World.CreateUnverifiedUserAsync();
        var attacker = env.App.NewClient();
        for (var i = 0; i < 5; i++)
        {
            await attacker.LoginAsync(user.Email, $"yanlis-parola-{i}-xxxxx").ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "invalid_credentials");
        }

        // Kilitliyken doğru parola da aynı genel yanıtı alır.
        await env.App.NewClient().LoginAsync(user.Email, user.Password).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "invalid_credentials");

        env.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        await env.App.NewClient().LoginAsync(user.Email, user.Password).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unauthenticated_Gets401ProblemDetails_NoRedirect()
    {
        var anonymous = env.App.NewClient();
        var response = await anonymous.GetAsync("/api/me");
        Assert.Null(response.Headers.Location);
        await Task.FromResult(response).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        await anonymous.GetAsync($"/api/t/{Guid.NewGuid()}/").ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        await anonymous.GetAsync("/api/does-not-exist").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Signup_CannotCreatePlatformOperator()
    {
        // 1) API: bilinmeyen alan yok sayılmaz, reddedilir.
        var client = env.App.NewClient();
        var email = World.UniqueEmail("op");
        await client.PostAsync("/api/auth/signup", new { email, displayName = "Operatör Adayı", password = World.DefaultPassword, isPlatformOperator = true })
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "bad_request");
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.users WHERE normalized_email = $1", email));

        // 2) Veritabanı: uygulama rolünün platform_operators tablosuna yazma yetkisi yoktur.
        var user = await World.CreateUnverifiedUserAsync();
        await using var connection = new NpgsqlConnection(env.Postgres.App());
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.user_id', $1, true)", connection, tx))
        {
            scope.Parameters.Add(new NpgsqlParameter { Value = user.Id.ToString() });
            await scope.ExecuteNonQueryAsync();
        }
        await using var insert = new NpgsqlCommand("INSERT INTO app.platform_operators (user_id, created_at) VALUES ($1, now())", connection, tx);
        insert.Parameters.Add(new NpgsqlParameter { Value = user.Id });
        var denied = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", denied.SqlState);

        var me = await user.Client.GetAsync("/api/me").ShouldBeAsync(HttpStatusCode.OK);
        Assert.False(me["isPlatformOperator"]!.GetValue<bool>());
    }

    [Fact]
    public async Task EmailVerification_Required_ForTenantCreation_AndTokenIsSingleUse()
    {
        var user = await World.CreateUnverifiedUserAsync();
        await user.Client.PostAsync("/api/tenants", new { name = "Doğrulanmamış" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "email_not_verified");

        await env.App.DrainOutboxAsync();
        var token = await World.LatestMailTokenAsync(user.Client, "email_verification");

        // Başka bir kullanıcı bu token'ı kullanamaz.
        var other = await World.CreateUnverifiedUserAsync();
        await other.Client.PostAsync("/api/auth/verify-email", new { token }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid_or_expired_token");

        await user.Client.PostAsync("/api/auth/verify-email", new { token }).ShouldBeAsync(HttpStatusCode.NoContent);
        await user.Client.PostAsync("/api/auth/verify-email", new { token }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid_or_expired_token");
        await user.Client.PostAsync("/api/tenants", new { name = "Doğrulanmış Şirket" }).ShouldBeAsync(HttpStatusCode.Created);

        // Ham token veritabanında yok: yalnız hash'i saklanır.
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.user_tokens WHERE encode(token_hash, 'escape') LIKE '%' || $1 || '%'", token));
        Assert.Equal(32L, await env.AdminScalarAsync<long>("SELECT max(length(token_hash)) FROM app.user_tokens WHERE user_id = $1", user.Id));
    }
}

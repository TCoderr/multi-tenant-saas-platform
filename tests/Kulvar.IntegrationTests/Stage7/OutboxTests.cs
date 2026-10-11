using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Messaging;
using Kulvar.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kulvar.IntegrationTests.Stage7;

/// <summary>
/// Outbox işleyicinin kiralama, fencing, yeniden deneme, ölü mektup ve kapatma davranışı. "İki worker" iki ayrı işleyici örneğidir;
/// sıralama test tarafından açıkça verilir (sahte saat, arka plan döngüsü yok).
/// </summary>
public sealed class OutboxTests(TestEnvironment env) : IAsyncLifetime
{
    private const string EffectType = "test.effect";
    private const string FailingType = "test.failing";
    private World World => env.World;

    /// <summary>Etkisi şirket kapsamında bir denetim kaydı eklemek olan test işleyicisi (etki sayısı veritabanından sayılır).</summary>
    private sealed class EffectHandler(string type) : IOutboxHandler
    {
        public string Type => type;
        public Func<OutboxMessageEnvelope, Task>? OnHandle { get; set; }
        public bool Fail { get; set; }

        public async Task HandleAsync(DbSession session, OutboxMessageEnvelope message, CancellationToken ct)
        {
            if (OnHandle is not null)
            {
                await OnHandle(message);
            }
            if (Fail)
            {
                throw new InvalidOperationException("test handler failure");
            }
            session.Db.Add(AuditEvent.Create(session.TenantId!.Value, null, "test.effect_applied", "test", message.Id, null, null, session.Now));
        }
    }

    private (OutboxProcessor Processor, EffectHandler Effect, EffectHandler Failing) NewWorker(string id, int maxAttempts = 8, int perTenant = 5)
    {
        var effect = new EffectHandler(EffectType);
        var failing = new EffectHandler(FailingType) { Fail = true };
        // Gerçek işleyiciler de dahildir: kuyrukta başka bir testten kalmış gerçek mesaj varsa doğru işlenir.
        var handlers = env.App.Services.GetServices<IOutboxHandler>().Concat([effect, failing]);
        var options = Options.Create(new OutboxOptions { MaxAttempts = maxAttempts, PerTenantBatch = perTenant, BatchSize = 20, Lease = TimeSpan.FromSeconds(60) });
        return (new OutboxProcessor(env.App.WorkerDb, handlers, options, NullLogger<OutboxProcessor>.Instance) { WorkerId = id }, effect, failing);
    }

    private Task<Guid> EnqueueAsync(Guid tenantId, string type = EffectType) => env.App.WorkerDb.RunAsync(DbScope.None, async (session, ct) =>
    {
        var message = OutboxMessage.Create(tenantId, null, type, new { n = 1 }, session.Now);
        session.Db.Add(message);
        await session.Db.SaveChangesAsync(ct);
        return message.Id;
    }, CancellationToken.None);

    private Task<long> EffectsAsync(Guid tenantId) => env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'test.effect_applied'", tenantId);
    private Task<string?> StateAsync(Guid id) => env.AdminScalarAsync<string>("SELECT status || ':' || attempts || ':' || lease_token FROM app.outbox_messages WHERE id = $1", id);

    public async ValueTask InitializeAsync() => await env.App.DrainOutboxAsync(); // Kuyruk boş başlar.

    public async ValueTask DisposeAsync() => await env.AdminExecuteAsync("DELETE FROM app.outbox_messages WHERE type LIKE 'test.%'"); // Geride test mesajı kalmaz.

    private async Task<Guid> NewTenantAsync() => await World.CreateTenantAsync(await World.CreateUserAsync());

    [Fact]
    public async Task Rollback_LeavesNoMessage_Commit_LeavesOne()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var scope = new DbScope(UserId: owner.Id, TenantId: tenant);
        Task<long> CountAsync() => env.AdminScalarAsync<long>("SELECT (SELECT count(*) FROM app.outbox_messages WHERE tenant_id = $1 AND type = 'test.atomic') + (SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'test.atomic')", tenant);

        // İş etkisi + outbox aynı transaction'da: hata ikisini birlikte geri alır.
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.App.AppDb.RunAsync(scope, async (session, ct) =>
        {
            session.Db.Add(AuditEvent.Create(tenant, owner.Id, "test.atomic", "test", null, null, null, session.Now));
            session.Db.Add(OutboxMessage.Create(tenant, null, "test.atomic", new { }, session.Now));
            await session.Db.SaveChangesAsync(ct);
            throw new InvalidOperationException("commit öncesi hata");
        }, CancellationToken.None));
        Assert.Equal(0L, await CountAsync());

        await env.App.AppDb.RunAsync(scope, async (session, ct) =>
        {
            session.Db.Add(AuditEvent.Create(tenant, owner.Id, "test.atomic", "test", null, null, null, session.Now));
            session.Db.Add(OutboxMessage.Create(tenant, null, "test.atomic", new { }, session.Now));
            await session.Db.SaveChangesAsync(ct);
        }, CancellationToken.None);
        Assert.Equal(2L, await CountAsync());
        await env.AdminExecuteAsync("DELETE FROM app.outbox_messages WHERE tenant_id = $1 AND type = 'test.atomic'", tenant);
    }

    [Fact]
    public async Task ExpiredLease_IsTakenOver_SingleEffect()
    {
        var tenant = await NewTenantAsync();
        var messageId = await EnqueueAsync(tenant);
        var (crashed, _, _) = NewWorker("worker-crashed");
        var (survivor, _, _) = NewWorker("worker-survivor");

        // Birinci worker mesajı kiralar ve işlemeden "çöker".
        var leased = await crashed.ClaimAsync(CancellationToken.None);
        Assert.Contains(leased, m => m.Id == messageId);
        Assert.Equal("Pending:1:1", await StateAsync(messageId));

        // Kiralama geçerliyken ikinci worker aynı mesajı alamaz.
        Assert.DoesNotContain(await survivor.ClaimAsync(CancellationToken.None), m => m.Id == messageId);
        Assert.Equal(0L, await EffectsAsync(tenant));

        // Kiralama süresi dolunca devralır ve tek etki üretir.
        env.Clock.Advance(TimeSpan.FromSeconds(61));
        var outcomes = await survivor.RunOnceAsync(CancellationToken.None);
        Assert.Contains(OutboxOutcome.Done, outcomes);
        Assert.Equal("Done:2:2", await StateAsync(messageId));
        Assert.Equal(1L, await EffectsAsync(tenant));
        Assert.Empty(await survivor.RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StaleWorker_CompleteWithOldToken_IsRejected()
    {
        var tenant = await NewTenantAsync();
        var messageId = await EnqueueAsync(tenant);
        var (slow, _, _) = NewWorker("worker-slow");
        var (fast, _, _) = NewWorker("worker-fast");

        // Yavaş worker mesajı kiralar (token 1) ama işlemesi kiralama süresinden uzun sürer.
        var stale = (await slow.ClaimAsync(CancellationToken.None)).Single(m => m.Id == messageId);
        env.Clock.Advance(TimeSpan.FromSeconds(61));
        // Hızlı worker devralır (token 2) ve tamamlar.
        var fresh = (await fast.ClaimAsync(CancellationToken.None)).Single(m => m.Id == messageId);
        Assert.Equal(stale.LeaseToken + 1, fresh.LeaseToken);
        Assert.Equal(OutboxOutcome.Done, await fast.ProcessAsync(fresh, CancellationToken.None));
        Assert.Equal(1L, await EffectsAsync(tenant));

        // Yavaş worker sonunda eski token'la tamamlamaya çalışır: etkisi geri alınır, mesaj durumu değişmez.
        Assert.Equal(OutboxOutcome.LeaseLost, await slow.ProcessAsync(stale, CancellationToken.None));
        Assert.Equal(1L, await EffectsAsync(tenant));
        Assert.Equal("Done:2:2", await StateAsync(messageId));

        // Ters sıra: yavaş worker etkisini uygular ama commit'ten ÖNCE kiralamayı kaybeder (devralan tamamlar).
        var second = await EnqueueAsync(tenant);
        var staleSecond = (await slow.ClaimAsync(CancellationToken.None)).Single(m => m.Id == second);
        slow.BeforeCompleteHook = async _ =>
        {
            env.Clock.Advance(TimeSpan.FromSeconds(61));
            var taken = (await fast.ClaimAsync(CancellationToken.None)).Single(m => m.Id == second);
            Assert.Equal(OutboxOutcome.Done, await fast.ProcessAsync(taken, CancellationToken.None));
        };
        Assert.Equal(OutboxOutcome.LeaseLost, await slow.ProcessAsync(staleSecond, CancellationToken.None));
        Assert.Equal(2L, await EffectsAsync(tenant)); // İkinci mesaj için de tam bir etki.
    }

    [Fact]
    public async Task Failing_Handler_BacksOff_ThenDead_ThenReplay()
    {
        var tenant = await NewTenantAsync();
        var messageId = await EnqueueAsync(tenant, FailingType);
        var (worker, _, failing) = NewWorker("worker-retry", maxAttempts: 3);

        Assert.Equal([OutboxOutcome.Retried], await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal("Pending:1:1", await StateAsync(messageId));
        // Bekleme süresi dolmadan yeniden denenmez.
        Assert.Empty(await worker.RunOnceAsync(CancellationToken.None));
        env.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal([OutboxOutcome.Retried], await worker.RunOnceAsync(CancellationToken.None));
        env.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal([OutboxOutcome.Dead], await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal("Dead:3:3", await StateAsync(messageId));
        Assert.Equal("InvalidOperationException", await env.AdminScalarAsync<string>("SELECT last_error FROM app.outbox_messages WHERE id = $1", messageId));

        // Ölü mesaj kendiliğinden yeniden denenmez.
        env.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(await worker.RunOnceAsync(CancellationToken.None));

        // Operatör görür (gövde yok) ve yeniden kuyruğa alır; sorun giderilmişse tamamlanır.
        var operatorUser = await World.CreateUserAsync("Kuyruk Operatörü");
        await using (var owner = Npgsql.NpgsqlDataSource.Create(env.Postgres.Owner()))
        {
            Assert.True(await Kulvar.Api.Hosting.OperatorSetup.GrantAsync(owner, operatorUser.Email, env.Clock.GetUtcNow(), CancellationToken.None));
        }
        var dead = await (await operatorUser.Client.GetAsync("/api/platform/jobs/dead")).Content.ReadAsStringAsync();
        Assert.Contains(messageId.ToString(), dead);
        Assert.DoesNotContain("payload", dead);
        await operatorUser.Client.PostAsync($"/api/platform/jobs/{messageId}/replay").ShouldBeAsync(HttpStatusCode.NoContent);
        await operatorUser.Client.PostAsync($"/api/platform/jobs/{messageId}/replay").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        failing.Fail = false;
        Assert.Equal([OutboxOutcome.Done], await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal(1L, await EffectsAsync(tenant));
    }

    [Fact]
    public async Task Cancellation_StopsBetweenMessages_NoLoss()
    {
        var tenant = await NewTenantAsync();
        var (worker, effect, _) = NewWorker("worker-shutdown", perTenant: 10);
        for (var i = 0; i < 5; i++)
        {
            await EnqueueAsync(tenant);
        }

        // İlk mesaj işlenirken kapatma istenir: o mesaj tamamlanır, kalanlara başlanmaz.
        using var shutdown = new CancellationTokenSource();
        effect.OnHandle = async _ => await shutdown.CancelAsync();
        var outcomes = await worker.RunOnceAsync(shutdown.Token);
        Assert.Equal([OutboxOutcome.Done], outcomes);
        Assert.Equal(1L, await EffectsAsync(tenant));
        Assert.Equal(4L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.outbox_messages WHERE tenant_id = $1 AND type = $2 AND status = 'Pending'", tenant, EffectType));

        // Yarım kalanlar kaybolmaz: kiralama dolunca (yeniden başlayan worker) hepsi birer kez işlenir.
        effect.OnHandle = null;
        env.Clock.Advance(TimeSpan.FromSeconds(61));
        var resumed = await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(4, resumed.Count(o => o == OutboxOutcome.Done));
        Assert.Equal(5L, await EffectsAsync(tenant));
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.outbox_messages WHERE tenant_id = $1 AND status <> 'Done'", tenant));
    }

    [Fact]
    public async Task ForgedTenantInPayload_CannotReachOtherTenant()
    {
        var ownerA = await World.CreateUserAsync();
        var ownerB = await World.CreateUserAsync();
        var tenantA = await World.CreateTenantAsync(ownerA);
        var tenantB = await World.CreateTenantAsync(ownerB);
        var inviteB = await World.InviteAsync(ownerB, tenantB, World.UniqueEmail("b-davetli"), TenantRole.Agent);
        // B'nin gerçek mesajını beklet (sonra işlenecek); A adına, B'nin davet kimliğini gösteren sahte bir mesaj üret.
        await env.AdminExecuteAsync("UPDATE app.outbox_messages SET available_at = $2 WHERE tenant_id = $1", tenantB, env.Clock.GetUtcNow().AddHours(1));
        await env.App.WorkerDb.RunAsync(DbScope.None, async (session, ct) =>
        {
            session.Db.Add(OutboxMessage.Create(tenantA, null, "tenancy.invite_created", new { inviteId = inviteB }, session.Now));
            await session.Db.SaveChangesAsync(ct);
        }, CancellationToken.None);

        var (worker, _, _) = NewWorker("worker-forged");
        Assert.Equal([OutboxOutcome.Done], await worker.RunOnceAsync(CancellationToken.None));

        // İşleyici A kapsamında aradı: B'nin daveti görünmez. Token üretilmedi, ileti yazılmadı.
        Assert.Null(await env.AdminScalarAsync<string>("SELECT encode(token_hash, 'hex') FROM app.invites WHERE id = $1", inviteB));
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.sink_messages WHERE kind = 'invite' AND tenant_id IN ($1, $2)", tenantA, tenantB));

        // B'nin kendi mesajı zamanı gelince normal işlenir.
        env.Clock.Advance(TimeSpan.FromHours(1));
        await env.App.DrainOutboxAsync();
        Assert.NotNull(await env.AdminScalarAsync<string>("SELECT encode(token_hash, 'hex') FROM app.invites WHERE id = $1", inviteB));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.sink_messages WHERE kind = 'invite' AND tenant_id = $1", tenantB));
    }

    [Fact]
    public async Task Claim_LimitsPerTenant_SoOthersProgress()
    {
        var noisy = await NewTenantAsync();
        var quiet = await NewTenantAsync();
        for (var i = 0; i < 30; i++)
        {
            await EnqueueAsync(noisy);
        }
        for (var i = 0; i < 2; i++)
        {
            await EnqueueAsync(quiet); // Gürültülü şirketin 30 mesajından SONRA kuyruğa girdi.
        }

        var (worker, _, _) = NewWorker("worker-fair", perTenant: 5);
        var batch = await worker.ClaimAsync(CancellationToken.None);

        // Parti şirket başına en çok 5 mesaj alır: sessiz şirketin iki mesajı ilk partide yer bulur.
        Assert.Equal(5, batch.Count(m => m.TenantId == noisy));
        Assert.Equal(2, batch.Count(m => m.TenantId == quiet));
        foreach (var message in batch)
        {
            await worker.ProcessAsync(message, CancellationToken.None);
        }
        Assert.Equal(2L, await EffectsAsync(quiet));
        Assert.Equal(5L, await EffectsAsync(noisy));

        // Kalanlar sonraki partilerde tamamlanır.
        for (var round = 0; round < 10 && (await worker.RunOnceAsync(CancellationToken.None)).Count > 0; round++)
        {
        }
        Assert.Equal(30L, await EffectsAsync(noisy));
    }

    [Fact]
    public async Task UnknownMessageType_FailsClosed_IntoDeadLetter()
    {
        var tenant = await NewTenantAsync();
        var messageId = await EnqueueAsync(tenant, "test.no_such_handler");
        var (worker, _, _) = NewWorker("worker-unknown", maxAttempts: 1);
        Assert.Equal([OutboxOutcome.Dead], await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal("Dead:1:1", await StateAsync(messageId));
    }
}

using Kulvar.Modules.Identity.Domain;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Messaging;
using Kulvar.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kulvar.Modules.Identity.Features;

/// <summary>
/// Demo posta kutusu: gerçek e-posta sağlayıcısının yerine geçer. Kullanıcı yalnız kendi adresine gelen iletileri görür
/// (sorgu filtresi + RLS). Şirket yöneticileri başkasının iletilerini (dolayısıyla davet token'larını) göremez.
/// </summary>
internal static class MailboxEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/me/mailbox");
        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
    }

    private static async Task<IResult> List(ICurrentUser current, AppDb db, CancellationToken ct)
    {
        var userId = current.UserId;
        var items = await db.RunAsync(DbScope.ForUser(userId), async (session, c) =>
        {
            var email = await session.Db.Set<User>().Where(u => u.Id == userId).Select(u => u.NormalizedEmail).SingleAsync(c);
            return await session.Db.Set<SinkMessage>().AsNoTracking()
                .Where(m => m.RecipientNormalizedEmail == email)
                .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .Take(100)
                .Select(m => new { m.Id, m.Kind, m.Subject, m.CreatedAt })
                .ToListAsync(c);
        }, ct);
        return Results.Ok(new { items });
    }

    private static async Task<IResult> Get(Guid id, ICurrentUser current, AppDb db, CancellationToken ct)
    {
        var userId = current.UserId;
        var message = await db.RunAsync(DbScope.ForUser(userId), async (session, c) =>
        {
            var email = await session.Db.Set<User>().Where(u => u.Id == userId).Select(u => u.NormalizedEmail).SingleAsync(c);
            return await session.Db.Set<SinkMessage>().AsNoTracking()
                .Where(m => m.Id == id && m.RecipientNormalizedEmail == email)
                .Select(m => new { m.Id, m.Kind, m.Subject, m.Body, m.ActionPath, m.CreatedAt })
                .SingleOrDefaultAsync(c);
        }, ct);
        return message is null ? throw Problems.NotFound() : Results.Ok(message);
    }
}

/// <summary>
/// E-posta doğrulama iletisini üretir. Token burada (worker'da) oluşturulur: hash'i <c>user_tokens</c>'a, ham değeri yalnız
/// posta kutusu iletisine aynı transaction'da yazılır. Yeniden teslimat tek ileti üretir (dedupe_key + atomik tamamlama).
/// </summary>
internal sealed class EmailVerificationHandler(IOptions<AuthOptions> options) : IOutboxHandler
{
    public string Type => IdentityOutbox.EmailVerificationRequested;

    public async Task HandleAsync(DbSession session, OutboxMessageEnvelope message, CancellationToken ct)
    {
        var payload = message.Read<IdentityOutbox.EmailVerificationPayload>();
        // Arama kurulu kullanıcı kapsamında yapılır; gövdedeki kimlik başka kullanıcıyı gösteriyorsa RLS satır döndürmez.
        var user = await session.Db.Set<User>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == payload.UserId, ct);
        var dedupeKey = $"email_verification:{message.Id:N}";
        if (user is null || user.IsEmailVerified || await MailSink.AlreadyDeliveredAsync(session.Db, dedupeKey, ct))
        {
            return; // Konu yok, zaten doğrulanmış veya bu mesajın iletisi zaten üretilmiş (yeniden teslimat etkisizdir).
        }

        await session.Db.Set<UserToken>()
            .Where(t => t.UserId == user.Id && t.Purpose == UserTokenPurpose.EmailVerification && t.ConsumedAt == null)
            .ExecuteDeleteAsync(ct);

        var token = Tokens.NewToken();
        session.Db.Add(UserToken.Create(user.Id, UserTokenPurpose.EmailVerification, Tokens.Hash(token), session.Now, TimeSpan.FromHours(options.Value.VerificationTokenHours)));
        session.Db.Add(SinkMessage.Create(
            tenantId: null,
            recipientNormalizedEmail: user.NormalizedEmail,
            kind: "email_verification",
            subject: "Kulvar: e-posta adresinizi doğrulayın",
            body: $"Merhaba {user.DisplayName},\n\nKulvar hesabınızı kullanmaya başlamak için e-posta adresinizi doğrulayın. Bağlantı {options.Value.VerificationTokenHours} saat geçerlidir.",
            actionPath: $"/verify-email#token={token}",
            dedupeKey: dedupeKey,
            now: session.Now));
    }
}

using Kulvar.Modules.Identity.Domain;
using Kulvar.Modules.Tenancy.Domain;
using Kulvar.Platform.Data;
using Kulvar.Platform.Messaging;
using Kulvar.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace Kulvar.Modules.Tenancy.Features;

/// <summary>
/// Davet iletisini üretir. Token burada (worker'da) oluşturulur; hash'i davet satırına, ham değeri yalnız alıcının demo posta kutusu
/// iletisine aynı transaction'da yazılır. Arama kurulu şirket kapsamında yapılır: gövdedeki kimlik başka şirketin davetini gösteremez.
/// </summary>
internal sealed class InviteCreatedHandler : IOutboxHandler
{
    public string Type => TenancyOutbox.InviteCreated;

    public async Task HandleAsync(DbSession session, OutboxMessageEnvelope message, CancellationToken ct)
    {
        var payload = message.Read<TenancyOutbox.InvitePayload>();
        var invite = await session.Db.Set<Invite>().SingleOrDefaultAsync(i => i.Id == payload.InviteId, ct);
        if (invite is null || invite.Status != InviteStatus.Pending || invite.IsExpired(session.Now) || invite.TokenHash is not null)
        {
            return; // Konu yok, iptal edilmiş, süresi dolmuş veya zaten gönderilmiş.
        }
        var tenantName = await session.Db.Set<Tenant>().Where(t => t.Id == invite.TenantId).Select(t => t.Name).SingleAsync(ct);

        var token = Tokens.NewToken();
        invite.MarkSent(Tokens.Hash(token), session.Now);
        session.Db.Add(SinkMessage.Create(
            tenantId: invite.TenantId,
            recipientNormalizedEmail: invite.NormalizedEmail,
            kind: "invite",
            subject: $"Kulvar: {tenantName} şirketine davet edildiniz",
            body: $"{tenantName} sizi Kulvar çalışma alanına {invite.Role} rolüyle davet etti. Davet {invite.ExpiresAt:yyyy-MM-dd} tarihine kadar geçerlidir. " +
                  "Kabul etmek için bu e-posta adresiyle giriş yapmış ve adresinizi doğrulamış olmalısınız.",
            actionPath: $"/invite#token={token}",
            dedupeKey: $"invite:{invite.Id:N}",
            now: session.Now));
    }
}

internal sealed class InviteAcceptedHandler : IOutboxHandler
{
    public string Type => TenancyOutbox.InviteAccepted;

    public async Task HandleAsync(DbSession session, OutboxMessageEnvelope message, CancellationToken ct)
    {
        var payload = message.Read<TenancyOutbox.InvitePayload>();
        var row = await (from i in session.Db.Set<Invite>()
                         join inviter in session.Db.Set<User>() on i.InvitedByUserId equals inviter.Id
                         join accepter in session.Db.Set<User>() on i.AcceptedByUserId equals (Guid?)accepter.Id
                         join t in session.Db.Set<Tenant>() on i.TenantId equals t.Id
                         where i.Id == payload.InviteId && i.Status == InviteStatus.Accepted
                         select new { i.Id, i.TenantId, i.Role, InviterEmail = inviter.NormalizedEmail, AccepterName = accepter.DisplayName, TenantName = t.Name })
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (row is null || await MailSink.AlreadyDeliveredAsync(session.Db, $"invite_accepted:{row.Id:N}", ct))
        {
            return;
        }
        session.Db.Add(SinkMessage.Create(
            row.TenantId, row.InviterEmail, "invite_accepted",
            $"Kulvar: {row.AccepterName} davetinizi kabul etti",
            $"{row.AccepterName}, {row.TenantName} şirketine {row.Role} rolüyle katıldı.",
            actionPath: $"/t/{row.TenantId}/team",
            dedupeKey: $"invite_accepted:{row.Id:N}",
            now: session.Now));
    }
}

internal sealed class OwnershipOfferedHandler : IOutboxHandler
{
    public string Type => TenancyOutbox.OwnershipOffered;

    public async Task HandleAsync(DbSession session, OutboxMessageEnvelope message, CancellationToken ct)
    {
        var payload = message.Read<TenancyOutbox.OfferPayload>();
        var row = await (from o in session.Db.Set<OwnershipOffer>()
                         join sender in session.Db.Set<User>() on o.FromUserId equals sender.Id
                         join recipient in session.Db.Set<User>() on o.ToUserId equals recipient.Id
                         join t in session.Db.Set<Tenant>() on o.TenantId equals t.Id
                         where o.Id == payload.OfferId && o.Status == OwnershipOfferStatus.Pending
                         select new { o.Id, o.TenantId, o.DemoteSelf, FromName = sender.DisplayName, ToEmail = recipient.NormalizedEmail, TenantName = t.Name })
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (row is null || await MailSink.AlreadyDeliveredAsync(session.Db, $"ownership_offer:{row.Id:N}", ct))
        {
            return;
        }
        session.Db.Add(SinkMessage.Create(
            row.TenantId, row.ToEmail, "ownership_offer",
            $"Kulvar: {row.TenantName} için sahiplik teklifi",
            row.DemoteSelf
                ? $"{row.FromName}, {row.TenantName} şirketinin sahipliğini size devretmek istiyor. Kabul ederseniz Owner olursunuz, {row.FromName} Admin olur."
                : $"{row.FromName}, sizi {row.TenantName} şirketinin ortak sahibi yapmak istiyor. Kabul ederseniz Owner olursunuz.",
            actionPath: $"/t/{row.TenantId}/team",
            dedupeKey: $"ownership_offer:{row.Id:N}",
            now: session.Now));
    }
}

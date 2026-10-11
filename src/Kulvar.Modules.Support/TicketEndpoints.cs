using Kulvar.Modules.Customers;
using Kulvar.Modules.Identity.Domain;
using Kulvar.Modules.Support.Domain;
using Kulvar.Modules.Tenancy.Domain;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Security;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kulvar.Modules.Support;

public sealed record CreateTicketRequest(Guid? CustomerId, string? Title, string? Description, TicketPriority? Priority, Guid? AssigneeUserId);
public sealed record EditTicketRequest(string? Title, string? Description, TicketPriority? Priority);
public sealed record TransitionRequest(TicketAction? Action);
public sealed record AssignRequest(Guid? UserId);
public sealed record CommentRequest(string? Body);

public sealed record TicketResponse(
    Guid Id, int Number, string Title, string Description, TicketPriority Priority, TicketStatus Status,
    Guid CustomerId, string CustomerName, Guid? AssigneeUserId, string? AssigneeName, Guid CreatedByUserId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ResolvedAt, DateTimeOffset? ClosedAt, int ReopenCount, int Version,
    IReadOnlyList<TicketAction> AllowedActions, bool CanModify);

public static class SupportModule
{
    public static IServiceCollection AddSupportModule(this IServiceCollection services)
    {
        services.AddScoped<IMemberRemovalStep, UnassignTicketsOnMemberRemoval>();
        return services;
    }

    public static IEndpointRouteBuilder MapSupportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/t/{tenantId:guid}");
        tenant.MapGet("/tickets", TicketEndpoints.List);
        tenant.MapPost("/tickets", TicketEndpoints.Create);
        tenant.MapGet("/tickets/{id:guid}", TicketEndpoints.Get);
        tenant.MapPatch("/tickets/{id:guid}", TicketEndpoints.Edit);
        tenant.MapPost("/tickets/{id:guid}/transition", TicketEndpoints.Transition);
        tenant.MapPut("/tickets/{id:guid}/assignee", TicketEndpoints.Assign);
        tenant.MapGet("/tickets/{id:guid}/timeline", TicketEndpoints.Timeline);
        tenant.MapPost("/tickets/{id:guid}/comments", TicketEndpoints.Comment);
        tenant.MapGet("/assignable-members", TicketEndpoints.AssignableMembers);
        return endpoints;
    }
}

/// <summary>Üyelik kaldırılırken (aynı transaction) o üyenin üzerindeki talepler boşa alınır: kaldırılmış üye atanan olarak kalmaz.</summary>
internal sealed class UnassignTicketsOnMemberRemoval : IMemberRemovalStep
{
    public async Task OnMemberRemovedAsync(TenantContext context, Guid removedUserId, CancellationToken ct)
    {
        var count = await context.Db.Set<Ticket>()
            .Where(t => t.AssigneeUserId == removedUserId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(t => t.AssigneeUserId, (Guid?)null)
                .SetProperty(t => t.UpdatedAt, context.Now)
                .SetProperty(t => t.Version, t => t.Version + 1), ct);
        if (count > 0)
        {
            context.Audit("tickets.unassigned_on_member_removal", "membership", removedUserId, data: new { count });
        }
    }
}

internal static class TicketEndpoints
{
    private const string ListName = "tickets";
    public const string CreateOperation = "ticket.create";

    public static async Task<IResult> List(Guid tenantId, HttpContext http, ITenantExecutor executor, CursorCodec cursors, CancellationToken ct)
    {
        QueryGuard.AllowOnly(http, "status", "priority", "assignee", "customerId", "q", "sort", "dir", "limit", "cursor");
        var limit = QueryGuard.Limit(http);
        var status = QueryGuard.Enum<TicketStatus>(http, "status");
        var priority = QueryGuard.Enum<TicketPriority>(http, "priority");
        var customerId = QueryGuard.Id(http, "customerId");
        var search = QueryGuard.Text(http, "q", 100);
        var sort = QueryGuard.OneOf(http, "sort", "createdAt", "createdAt", "updatedAt");
        var dir = QueryGuard.OneOf(http, "dir", "desc", "asc", "desc");
        var assignee = http.Request.Query["assignee"].ToString();
        Guid? assigneeId = null;
        if (assignee.Length > 0 && assignee is not ("me" or "none"))
        {
            assigneeId = Guid.TryParse(assignee, out var parsed) ? parsed : throw Problems.Validation("assignee", "me, none veya bir üye kimliği olmalıdır.");
        }
        var filterKey = $"{status}|{priority}|{assignee}|{customerId}|{search}|{sort}|{dir}";
        var rawCursor = http.Request.Query["cursor"].ToString();

        var page = await executor.RunAsync(tenantId, Permission.TicketsRead, TenantLock.None, async (ctx, c) =>
        {
            var after = cursors.Decode(ctx, ListName, filterKey, rawCursor, 2);
            var query = ctx.Db.Set<Ticket>().AsNoTracking();
            if (status is { } s)
            {
                query = query.Where(t => t.Status == s);
            }
            if (priority is { } p)
            {
                query = query.Where(t => t.Priority == p);
            }
            if (customerId is { } cid)
            {
                query = query.Where(t => t.CustomerId == cid);
            }
            query = assignee switch
            {
                "" => query,
                "me" => query.Where(t => t.AssigneeUserId == ctx.UserId),
                "none" => query.Where(t => t.AssigneeUserId == null),
                _ => query.Where(t => t.AssigneeUserId == assigneeId),
            };
            if (search is not null)
            {
                var needle = search.ToLowerInvariant();
                query = query.Where(t => t.Title.ToLower().Contains(needle));
            }

            var descending = dir == "desc";
            var byUpdated = sort == "updatedAt";
            if (after is not null)
            {
                var (at, id) = (CursorCodec.ParseTime(after[0]), Guid.Parse(after[1]));
                query = (byUpdated, descending) switch
                {
                    (false, true) => query.Where(t => EF.Functions.LessThan(ValueTuple.Create(t.CreatedAt, t.Id), ValueTuple.Create(at, id))),
                    (false, false) => query.Where(t => EF.Functions.GreaterThan(ValueTuple.Create(t.CreatedAt, t.Id), ValueTuple.Create(at, id))),
                    (true, true) => query.Where(t => EF.Functions.LessThan(ValueTuple.Create(t.UpdatedAt, t.Id), ValueTuple.Create(at, id))),
                    (true, false) => query.Where(t => EF.Functions.GreaterThan(ValueTuple.Create(t.UpdatedAt, t.Id), ValueTuple.Create(at, id))),
                };
            }
            var joined = from t in query
                         join cu in ctx.Db.Set<Customer>() on t.CustomerId equals cu.Id
                         join u in ctx.Db.Set<User>() on t.AssigneeUserId equals (Guid?)u.Id into assignees
                         from a in assignees.DefaultIfEmpty()
                         select new { Ticket = t, CustomerName = cu.Name, AssigneeName = a != null ? a.DisplayName : null };
            joined = (byUpdated, descending) switch
            {
                (false, true) => joined.OrderByDescending(x => x.Ticket.CreatedAt).ThenByDescending(x => x.Ticket.Id),
                (false, false) => joined.OrderBy(x => x.Ticket.CreatedAt).ThenBy(x => x.Ticket.Id),
                (true, true) => joined.OrderByDescending(x => x.Ticket.UpdatedAt).ThenByDescending(x => x.Ticket.Id),
                (true, false) => joined.OrderBy(x => x.Ticket.UpdatedAt).ThenBy(x => x.Ticket.Id),
            };
            var rows = await joined.Take(limit + 1).ToListAsync(c);

            string? next = null;
            if (rows.Count > limit)
            {
                rows.RemoveAt(limit);
                var last = rows[^1].Ticket;
                next = cursors.Encode(ctx, ListName, filterKey, CursorCodec.Format(byUpdated ? last.UpdatedAt : last.CreatedAt), last.Id.ToString("D"));
            }
            return new
            {
                items = rows.Select(r => new
                {
                    id = r.Ticket.Id,
                    number = r.Ticket.Number,
                    title = r.Ticket.Title,
                    status = r.Ticket.Status,
                    priority = r.Ticket.Priority,
                    customerId = r.Ticket.CustomerId,
                    customerName = r.CustomerName,
                    assigneeUserId = r.Ticket.AssigneeUserId,
                    assigneeName = r.AssigneeName,
                    createdAt = r.Ticket.CreatedAt,
                    updatedAt = r.Ticket.UpdatedAt,
                    version = r.Ticket.Version,
                }).ToList(),
                nextCursor = next,
            };
        }, ct);
        return Results.Ok(page);
    }

    public static async Task<IResult> Create(Guid tenantId, CreateTicketRequest request, HttpContext http, ITenantExecutor executor, IEnumerable<IUsageGate> gates, CancellationToken ct)
    {
        var key = Idempotency.RequireKey(http);
        var validator = new Validator();
        var title = validator.Required("title", request.Title, 200);
        var description = validator.Required("description", request.Description, 10000);
        if (request.CustomerId is null)
        {
            validator.Add("customerId", "Müşteri zorunludur.");
        }
        validator.ThrowIfInvalid();
        var priority = request.Priority ?? TicketPriority.Normal;
        var hash = Idempotency.HashRequest(request);

        var result = await executor.RunAsync(tenantId, Permission.TicketsWrite, TenantLock.ActorShared, (ctx, c) =>
            Idempotency.ExecuteAsync(ctx, CreateOperation, key, hash, async () =>
            {
                // Müşteri kimliği kurulu şirket kapsamında aranır: başka şirketin kimliği "yok" demektir.
                var customer = await ctx.Db.Set<Customer>().AsNoTracking().Where(x => x.Id == request.CustomerId).Select(x => new { x.Id, x.Name }).SingleOrDefaultAsync(c)
                    ?? throw Problems.NotFound();

                string? assigneeName = null;
                if (request.AssigneeUserId is { } assigneeId)
                {
                    if (!TicketWorkflow.CanAssign(ctx.Role, ctx.UserId, null, assigneeId))
                    {
                        throw Problems.Forbidden(AccessRules.Code(Permission.TicketsWrite));
                    }
                    assigneeName = await RequireAssignableAsync(ctx, assigneeId, c);
                }

                var id = Ids.New();
                // Kota tüketimi (kayıtlı kapı varsa) talep eklemeyle aynı transaction'dadır; aşılırsa buradan hiçbir şey kalmaz.
                foreach (var gate in gates)
                {
                    await gate.ConsumeAsync(ctx, UsageMetrics.TicketsCreated, id, c);
                }
                var number = await NextNumberAsync(ctx, c);
                var ticket = Ticket.Create(id, ctx.TenantId, number, customer.Id, title, description, priority, request.AssigneeUserId, ctx.UserId, ctx.Now);
                ctx.Db.Add(ticket);
                // Önce talep, sonra etkinlik: etkinliğin (tenant_id, ticket_id) FK'si EF modelinin dışında olduğundan sıra açıkça verilir.
                await ctx.Db.SaveChangesAsync(c);
                ctx.Audit("ticket.created", "ticket", id, ticketId: id, data: new { number, priority });
                await ctx.Db.SaveChangesAsync(c);
                return (StatusCodes.Status201Created, ToResponse(ctx, ticket, customer.Name, assigneeName));
            }, c), ct);

        if (result.ReadInt("version") is { } version)
        {
            ETags.Set(http, version);
        }
        return result.ToHttpResult(http);
    }

    public static async Task<IResult> Get(Guid tenantId, Guid id, HttpContext http, ITenantExecutor executor, CancellationToken ct)
    {
        var response = await executor.RunAsync(tenantId, Permission.TicketsRead, TenantLock.None, async (ctx, c) =>
        {
            var ticket = await ctx.Db.Set<Ticket>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, c) ?? throw Problems.NotFound();
            return await LoadResponseAsync(ctx, ticket, c);
        }, ct);
        ETags.Set(http, response.Version);
        return Results.Ok(response);
    }

    public static async Task<IResult> Edit(Guid tenantId, Guid id, EditTicketRequest request, HttpContext http, ITenantExecutor executor, CancellationToken ct)
    {
        var expected = ETags.RequireIfMatch(http);
        var response = await executor.RunAsync(tenantId, Permission.TicketsWrite, TenantLock.ActorShared, async (ctx, c) =>
        {
            var ticket = await LoadForChangeAsync(ctx, id, expected, c);
            var validator = new Validator();
            var title = request.Title is null ? ticket.Title : validator.Required("title", request.Title, 200);
            var description = request.Description is null ? ticket.Description : validator.Required("description", request.Description, 10000);
            validator.ThrowIfInvalid();
            ticket.Edit(title, description, request.Priority ?? ticket.Priority, ctx.Now);
            ctx.Audit("ticket.edited", "ticket", id, ticketId: id);
            await ctx.Db.SaveChangesAsync(c);
            return await LoadResponseAsync(ctx, ticket, c);
        }, ct);
        ETags.Set(http, response.Version);
        return Results.Ok(response);
    }

    public static async Task<IResult> Transition(Guid tenantId, Guid id, TransitionRequest request, HttpContext http, ITenantExecutor executor, CancellationToken ct)
    {
        var expected = ETags.RequireIfMatch(http);
        if (request.Action is not { } action)
        {
            throw Problems.Validation("action", "Eylem zorunludur.");
        }
        var response = await executor.RunAsync(tenantId, Permission.TicketsWrite, TenantLock.ActorShared, async (ctx, c) =>
        {
            var ticket = await LoadForChangeAsync(ctx, id, expected, c);
            switch (TicketWorkflow.Evaluate(ticket.Status, action, ctx.Role, ticket.ClosedAt, ctx.Now))
            {
                case TransitionDecision.Invalid:
                    throw Problems.Conflict("invalid_transition", "Talep bu durumdayken bu eylem yapılamaz.",
                        new Dictionary<string, object?> { ["ticketStatus"] = ticket.Status.ToString(), ["action"] = action.ToString() });
                case TransitionDecision.Forbidden:
                    throw Problems.Forbidden("tickets.reopen_closed");
                case TransitionDecision.ReopenWindowExpired:
                    throw Problems.Conflict("reopen_window_expired", "Kapalı talep yalnız kapatıldıktan sonraki 30 gün içinde yeniden açılabilir.");
            }
            var from = ticket.Status;
            ticket.Apply(action, ctx.UserId, ctx.Role, ctx.Now);
            ctx.Audit("ticket.status_changed", "ticket", id, ticketId: id, data: new { action, from, to = ticket.Status });
            await ctx.Db.SaveChangesAsync(c);
            return await LoadResponseAsync(ctx, ticket, c);
        }, ct);
        ETags.Set(http, response.Version);
        return Results.Ok(response);
    }

    public static async Task<IResult> Assign(Guid tenantId, Guid id, AssignRequest request, HttpContext http, ITenantExecutor executor, CancellationToken ct)
    {
        var expected = ETags.RequireIfMatch(http);
        var response = await executor.RunAsync(tenantId, Permission.TicketsWrite, TenantLock.ActorShared, async (ctx, c) =>
        {
            var ticket = await ctx.Db.Set<Ticket>().SingleOrDefaultAsync(t => t.Id == id, c) ?? throw Problems.NotFound();
            ETags.EnsureMatch(expected, ticket.Version);
            if (ticket.Status == TicketStatus.Closed)
            {
                throw Problems.Conflict("ticket_closed", "Kapalı talebin ataması değiştirilemez; önce yeniden açın.");
            }
            if (!TicketWorkflow.CanAssign(ctx.Role, ctx.UserId, ticket.AssigneeUserId, request.UserId))
            {
                throw Problems.Forbidden(AccessRules.Code(Permission.TicketsWrite));
            }
            if (request.UserId is { } assigneeId)
            {
                await RequireAssignableAsync(ctx, assigneeId, c);
            }
            var from = ticket.AssigneeUserId;
            ticket.Assign(request.UserId, ctx.Now);
            ctx.Audit("ticket.assigned", "ticket", id, ticketId: id, data: new { from, to = request.UserId });
            await ctx.Db.SaveChangesAsync(c);
            return await LoadResponseAsync(ctx, ticket, c);
        }, ct);
        ETags.Set(http, response.Version);
        return Results.Ok(response);
    }

    public static async Task<IResult> Comment(Guid tenantId, Guid id, CommentRequest request, ITenantExecutor executor, CancellationToken ct)
    {
        var validator = new Validator();
        var body = validator.Required("body", request.Body, 5000);
        validator.ThrowIfInvalid();

        var response = await executor.RunAsync(tenantId, Permission.TicketsWrite, TenantLock.ActorShared, async (ctx, c) =>
        {
            // Satır kilidi: eşzamanlı kapatma ile yorum yarışmaz.
            var lockedRows = await ctx.Db.QueryAsync(
                "SELECT assignee_user_id, status FROM app.tickets WHERE tenant_id = $1 AND id = $2 FOR UPDATE",
                r => (Assignee: r.GetGuidOrNull(0), Status: Enum.Parse<TicketStatus>(r.GetString(1))), c, ctx.TenantId, id);
            if (lockedRows.Count == 0)
            {
                throw Problems.NotFound();
            }
            var locked = lockedRows[0];
            if (!TicketWorkflow.CanModify(ctx.Role, ctx.UserId, locked.Assignee))
            {
                throw Problems.Forbidden(AccessRules.Code(Permission.TicketsWrite));
            }
            if (locked.Status == TicketStatus.Closed)
            {
                throw Problems.Conflict("ticket_closed", "Kapalı talebe yorum eklenemez; önce yeniden açın.");
            }
            var comment = TicketComment.Create(ctx.TenantId, id, ctx.UserId, body, ctx.Now);
            ctx.Db.Add(comment);
            await ctx.Db.SaveChangesAsync(c);
            // Yorum talebin sürümünü değiştirmez (başkasının açık düzenlemesi 412 almaz); yalnız "son hareket" zamanı ilerler.
            await ctx.Db.Set<Ticket>().Where(t => t.Id == id).ExecuteUpdateAsync(set => set.SetProperty(t => t.UpdatedAt, ctx.Now), c);
            var authorName = await ctx.Db.Set<User>().Where(u => u.Id == ctx.UserId).Select(u => u.DisplayName).SingleAsync(c);
            return new { id = comment.Id, kind = "comment", authorUserId = ctx.UserId, authorName, text = comment.Body, at = comment.CreatedAt };
        }, ct);
        return Results.Created($"/api/t/{tenantId}/tickets/{id}/timeline", response);
    }

    public static async Task<IResult> Timeline(Guid tenantId, Guid id, ITenantExecutor executor, CancellationToken ct)
    {
        var items = await executor.RunAsync(tenantId, Permission.TicketsRead, TenantLock.None, async (ctx, c) =>
        {
            if (!await ctx.Db.Set<Ticket>().AnyAsync(t => t.Id == id, c))
            {
                throw Problems.NotFound();
            }
            var comments = await (from cm in ctx.Db.Set<TicketComment>()
                                  join u in ctx.Db.Set<User>() on cm.AuthorUserId equals u.Id
                                  where cm.TicketId == id
                                  select new { cm.Id, cm.AuthorUserId, AuthorName = u.DisplayName, cm.Body, cm.CreatedAt })
                .AsNoTracking().ToListAsync(c);
            var events = await (from e in ctx.Db.Set<AuditEvent>()
                                join u in ctx.Db.Set<User>() on e.ActorUserId equals (Guid?)u.Id into actors
                                from a in actors.DefaultIfEmpty()
                                where e.TicketId == id
                                select new { e.Id, e.ActorUserId, ActorName = a != null ? a.DisplayName : null, e.Action, e.Data, e.OccurredAt })
                .AsNoTracking().ToListAsync(c);

            // Yorum gövdesi yalnız "text" alanında, düz metin olarak döner; istemci metin düğümü olarak basar.
            return comments.Select(x => new TimelineItem(x.Id, "comment", x.AuthorUserId, x.AuthorName, x.Body, null, null, x.CreatedAt))
                .Concat(events.Select(x => new TimelineItem(x.Id, "event", x.ActorUserId, x.ActorName, null, x.Action, System.Text.Json.Nodes.JsonNode.Parse(x.Data), x.OccurredAt)))
                .OrderBy(x => x.At).ThenBy(x => x.Id)
                .ToList();
        }, ct);
        return Results.Ok(new { items });
    }

    public sealed record TimelineItem(Guid Id, string Kind, Guid? AuthorUserId, string? AuthorName, string? Text, string? Action, System.Text.Json.Nodes.JsonNode? Data, DateTimeOffset At);

    public static async Task<IResult> AssignableMembers(Guid tenantId, ITenantExecutor executor, CancellationToken ct)
    {
        var items = await executor.RunAsync(tenantId, Permission.TicketsRead, TenantLock.None, async (ctx, c) =>
            await (from m in ctx.Db.Set<Membership>()
                   join u in ctx.Db.Set<User>() on m.UserId equals u.Id
                   where m.Status == MembershipStatus.Active && (m.Role == TenantRole.Owner || m.Role == TenantRole.Admin || m.Role == TenantRole.Agent)
                   orderby u.DisplayName, u.Id
                   select new { userId = m.UserId, displayName = u.DisplayName, role = m.Role })
                .AsNoTracking().ToListAsync(c), ct);
        return Results.Ok(new { items });
    }

    private static async Task<Ticket> LoadForChangeAsync(TenantContext ctx, Guid id, int expectedVersion, CancellationToken ct)
    {
        var ticket = await ctx.Db.Set<Ticket>().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw Problems.NotFound();
        if (!TicketWorkflow.CanModify(ctx.Role, ctx.UserId, ticket.AssigneeUserId))
        {
            throw Problems.Forbidden(AccessRules.Code(Permission.TicketsWrite));
        }
        ETags.EnsureMatch(expectedVersion, ticket.Version);
        return ticket;
    }

    /// <summary>
    /// Atanacak kişi bu şirketin AKTİF ve talep üstlenebilen üyesi olmalıdır. Satır FOR SHARE kilitlenir: eşzamanlı üyelik kaldırma
    /// bu transaction'ı bekler ve ardından (kendi transaction'ında) atamayı boşaltır. Global kullanıcı varlığı tek başına yetmez.
    /// </summary>
    private static async Task<string> RequireAssignableAsync(TenantContext ctx, Guid userId, CancellationToken ct)
    {
        var role = await ctx.Db.ScalarAsync("SELECT role FROM app.memberships WHERE tenant_id = $1 AND user_id = $2 AND status = 'Active' FOR SHARE", ct, ctx.TenantId, userId) as string;
        if (role is null || !TicketWorkflow.IsAssignableRole(Enum.Parse<TenantRole>(role)))
        {
            throw Problems.Validation("assigneeUserId", "Atanacak kişi bu şirketin aktif Owner, Admin veya Agent üyesi olmalıdır.");
        }
        return await ctx.Db.Set<User>().Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(ct);
    }

    /// <summary>Şirket içi artan numara: tek deyim, satır kilidiyle. Sayaç satırı yoksa oluşturur.</summary>
    private static async Task<int> NextNumberAsync(TenantContext ctx, CancellationToken ct) =>
        (int)(await ctx.Db.ScalarAsync(
            """
            INSERT INTO app.tenant_counters AS c (tenant_id, next_ticket_number) VALUES ($1, 2)
            ON CONFLICT (tenant_id) DO UPDATE SET next_ticket_number = c.next_ticket_number + 1
            RETURNING next_ticket_number - 1
            """, ct, ctx.TenantId))!;

    private static async Task<TicketResponse> LoadResponseAsync(TenantContext ctx, Ticket ticket, CancellationToken ct)
    {
        var customerName = await ctx.Db.Set<Customer>().Where(x => x.Id == ticket.CustomerId).Select(x => x.Name).SingleAsync(ct);
        var assigneeName = ticket.AssigneeUserId is { } assignee
            ? await ctx.Db.Set<User>().Where(u => u.Id == assignee).Select(u => u.DisplayName).SingleOrDefaultAsync(ct)
            : null;
        return ToResponse(ctx, ticket, customerName, assigneeName);
    }

    private static TicketResponse ToResponse(TenantContext ctx, Ticket t, string customerName, string? assigneeName)
    {
        var canWrite = ctx.Can(Permission.TicketsWrite);
        return new TicketResponse(
            t.Id, t.Number, t.Title, t.Description, t.Priority, t.Status, t.CustomerId, customerName, t.AssigneeUserId, assigneeName, t.CreatedByUserId,
            t.CreatedAt, t.UpdatedAt, t.ResolvedAt, t.ClosedAt, t.ReopenCount, t.Version,
            canWrite ? TicketWorkflow.AllowedActions(t.Status, ctx.Role, ctx.UserId, t.AssigneeUserId, t.ClosedAt, ctx.Now) : [],
            canWrite && TicketWorkflow.CanModify(ctx.Role, ctx.UserId, t.AssigneeUserId));
    }
}

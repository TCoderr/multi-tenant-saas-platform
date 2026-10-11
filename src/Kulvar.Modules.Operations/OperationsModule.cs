using Kulvar.Platform.Audit;
using Kulvar.Platform.Http;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kulvar.Modules.Operations;

public static class OperationsModule
{
    /// <summary>Süreçten bağımsız kayıtlar (API ve worker).</summary>
    public static IServiceCollection AddOperationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ExportOptions>(configuration.GetSection("Exports"));
        services.Configure<LifecycleOptions>(configuration.GetSection("Lifecycle"));
        services.AddSingleton<ExportRunner>();
        services.AddSingleton<TenantPurger>();
        return services;
    }

    /// <summary>Yalnız API süreci: platform operatörü yürütücüsü.</summary>
    public static IServiceCollection AddOperationsApi(this IServiceCollection services)
    {
        services.AddScoped<PlatformExecutor>();
        return services;
    }

    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        PlatformEndpoints.Map(endpoints);
        var tenant = endpoints.MapGroup("/api/t/{tenantId:guid}");
        tenant.MapGet("/exports", ExportEndpoints.List);
        tenant.MapPost("/exports", ExportEndpoints.Request);
        tenant.MapPost("/exports/{id:guid}/download-link", ExportEndpoints.DownloadLink);
        tenant.MapGet("/exports/{id:guid}/file", ExportEndpoints.Download);
        tenant.MapGet("/audit", AuditEndpoints.List);
        return endpoints;
    }
}

internal static class AuditEndpoints
{
    private const string ListName = "audit";

    /// <summary>Şirket denetim kaydı (Owner/Admin). Kayıtlar kişisel veri içermez: kimlikler, rol/durum adları.</summary>
    public static async Task<IResult> List(Guid tenantId, HttpContext http, ITenantExecutor executor, CursorCodec cursors, CancellationToken ct)
    {
        QueryGuard.AllowOnly(http, "limit", "cursor");
        var limit = QueryGuard.Limit(http);
        var rawCursor = http.Request.Query["cursor"].ToString();
        var page = await executor.RunAsync(tenantId, Permission.AuditRead, TenantLock.None, async (ctx, c) =>
        {
            var after = cursors.Decode(ctx, ListName, string.Empty, rawCursor, 2);
            var query = ctx.Db.Set<AuditEvent>().AsNoTracking();
            if (after is not null)
            {
                var (at, id) = (CursorCodec.ParseTime(after[0]), Guid.Parse(after[1]));
                query = query.Where(e => EF.Functions.LessThan(ValueTuple.Create(e.OccurredAt, e.Id), ValueTuple.Create(at, id)));
            }
            var rows = await query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(limit + 1).ToListAsync(c);
            string? next = null;
            if (rows.Count > limit)
            {
                rows.RemoveAt(limit);
                next = cursors.Encode(ctx, ListName, string.Empty, CursorCodec.Format(rows[^1].OccurredAt), rows[^1].Id.ToString("D"));
            }
            return new
            {
                items = rows.Select(e => new
                {
                    id = e.Id,
                    action = e.Action,
                    actorUserId = e.ActorUserId,
                    targetType = e.TargetType,
                    targetId = e.TargetId,
                    ticketId = e.TicketId,
                    data = System.Text.Json.Nodes.JsonNode.Parse(e.Data),
                    occurredAt = e.OccurredAt,
                }).ToList(),
                nextCursor = next,
            };
        }, ct);
        return Results.Ok(page);
    }
}

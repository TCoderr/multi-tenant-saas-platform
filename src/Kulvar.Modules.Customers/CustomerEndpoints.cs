using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Security;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kulvar.Modules.Customers;

public sealed record CustomerRequest(string? Name, string? CompanyTitle, string? Email, string? Phone, string? Notes);

public sealed record CustomerResponse(Guid Id, string Name, string? CompanyTitle, string? Email, string? Phone, string? Notes, bool PiiMasked, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Version);

public static class CustomersModule
{
    public const string UniqueEmailIndex = "ix_customers_tenant_id_normalized_email";

    public static IServiceCollection AddCustomersModule(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapCustomersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/t/{tenantId:guid}/customers");
        group.MapGet("/", CustomerEndpoints.List);
        group.MapPost("/", CustomerEndpoints.Create);
        group.MapGet("/{id:guid}", CustomerEndpoints.Get);
        group.MapPut("/{id:guid}", CustomerEndpoints.Update);
        group.MapDelete("/{id:guid}", CustomerEndpoints.Delete);
        return endpoints;
    }
}

internal static class CustomerEndpoints
{
    private const string ListName = "customers";

    public static async Task<IResult> List(Guid tenantId, HttpContext http, ITenantExecutor executor, CursorCodec cursors, CancellationToken ct)
    {
        QueryGuard.AllowOnly(http, "q", "sort", "dir", "limit", "cursor");
        var limit = QueryGuard.Limit(http);
        var sort = QueryGuard.OneOf(http, "sort", "createdAt", "createdAt", "name");
        var dir = QueryGuard.OneOf(http, "dir", sort == "name" ? "asc" : "desc", "asc", "desc");
        var search = QueryGuard.Text(http, "q", 100)?.ToLowerInvariant();
        var filterKey = $"{sort}|{dir}|{search}";
        var rawCursor = http.Request.Query["cursor"].ToString();

        var page = await executor.RunAsync(tenantId, Permission.CustomersRead, TenantLock.None, async (ctx, c) =>
        {
            var after = cursors.Decode(ctx, ListName, filterKey, rawCursor, 2);
            var query = ctx.Db.Set<Customer>().AsNoTracking();
            if (search is not null)
            {
                // Maskeli gören rol (Viewer) e-posta üzerinden arayamaz: arama, maskelenen alanı tahmin etme aracına dönüşmemeli.
                query = ctx.Can(Permission.CustomersPii)
                    ? query.Where(x => x.SearchText.Contains(search))
                    : query.Where(x => (x.Name + " " + (x.CompanyTitle ?? "")).ToLower().Contains(search));
            }

            var descending = dir == "desc";
            if (sort == "name")
            {
                if (after is not null)
                {
                    var (name, id) = (after[0], Guid.Parse(after[1]));
                    query = descending
                        ? query.Where(x => EF.Functions.LessThan(ValueTuple.Create(x.Name, x.Id), ValueTuple.Create(name, id)))
                        : query.Where(x => EF.Functions.GreaterThan(ValueTuple.Create(x.Name, x.Id), ValueTuple.Create(name, id)));
                }
                query = descending ? query.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id) : query.OrderBy(x => x.Name).ThenBy(x => x.Id);
            }
            else
            {
                if (after is not null)
                {
                    var (at, id) = (CursorCodec.ParseTime(after[0]), Guid.Parse(after[1]));
                    query = descending
                        ? query.Where(x => EF.Functions.LessThan(ValueTuple.Create(x.CreatedAt, x.Id), ValueTuple.Create(at, id)))
                        : query.Where(x => EF.Functions.GreaterThan(ValueTuple.Create(x.CreatedAt, x.Id), ValueTuple.Create(at, id)));
                }
                query = descending ? query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id) : query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id);
            }

            var rows = await query.Take(limit + 1).ToListAsync(c);
            string? next = null;
            if (rows.Count > limit)
            {
                rows.RemoveAt(limit);
                var last = rows[^1];
                next = cursors.Encode(ctx, ListName, filterKey, sort == "name" ? last.Name : CursorCodec.Format(last.CreatedAt), last.Id.ToString("D"));
            }
            var unmasked = ctx.Can(Permission.CustomersPii);
            return new { items = rows.Select(r => ToResponse(r, unmasked)).ToList(), nextCursor = next };
        }, ct);
        return Results.Ok(page);
    }

    public static async Task<IResult> Create(Guid tenantId, CustomerRequest request, HttpContext http, ITenantExecutor executor, CancellationToken ct)
    {
        var input = Validate(request);
        var response = await executor.RunAsync(tenantId, Permission.CustomersWrite, TenantLock.ActorShared, async (ctx, c) =>
        {
            // tenant_id doğrulanmış kapsamdan gelir; istek gövdesinde böyle bir alan yoktur (olsaydı 400 olurdu).
            var customer = Customer.Create(ctx.TenantId, ctx.UserId, input.Name, input.CompanyTitle, input.Email, input.Phone, input.Notes, ctx.Now);
            ctx.Db.Add(customer);
            ctx.Audit("customer.created", "customer", customer.Id);
            await SaveAsync(ctx, c);
            return ToResponse(customer, unmasked: true);
        }, ct);
        ETags.Set(http, response.Version);
        return Results.Created($"/api/t/{tenantId}/customers/{response.Id}", response);
    }

    public static async Task<IResult> Get(Guid tenantId, Guid id, HttpContext http, ITenantExecutor executor, CancellationToken ct)
    {
        var response = await executor.RunAsync(tenantId, Permission.CustomersRead, TenantLock.None, async (ctx, c) =>
        {
            var customer = await ctx.Db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, c) ?? throw Problems.NotFound();
            return ToResponse(customer, ctx.Can(Permission.CustomersPii));
        }, ct);
        ETags.Set(http, response.Version);
        return Results.Ok(response);
    }

    public static async Task<IResult> Update(Guid tenantId, Guid id, CustomerRequest request, HttpContext http, ITenantExecutor executor, CancellationToken ct)
    {
        var expected = ETags.RequireIfMatch(http);
        var input = Validate(request);
        var response = await executor.RunAsync(tenantId, Permission.CustomersWrite, TenantLock.ActorShared, async (ctx, c) =>
        {
            var customer = await ctx.Db.Set<Customer>().SingleOrDefaultAsync(x => x.Id == id, c) ?? throw Problems.NotFound();
            ETags.EnsureMatch(expected, customer.Version);
            customer.Update(input.Name, input.CompanyTitle, input.Email, input.Phone, input.Notes, ctx.Now);
            ctx.Audit("customer.updated", "customer", customer.Id);
            await SaveAsync(ctx, c);
            return ToResponse(customer, unmasked: true);
        }, ct);
        ETags.Set(http, response.Version);
        return Results.Ok(response);
    }

    public static async Task<IResult> Delete(Guid tenantId, Guid id, ITenantExecutor executor, CancellationToken ct)
    {
        await executor.RunAsync(tenantId, Permission.CustomersDelete, TenantLock.ActorShared, async (ctx, c) =>
        {
            var customer = await ctx.Db.Set<Customer>().SingleOrDefaultAsync(x => x.Id == id, c) ?? throw Problems.NotFound();
            ctx.Db.Remove(customer);
            ctx.Audit("customer.deleted", "customer", id);
            try
            {
                await ctx.Db.SaveChangesAsync(c);
            }
            catch (DbUpdateException ex) when (PostgresErrors.SqlState(ex) is PostgresErrors.ForeignKeyViolation or PostgresErrors.RestrictViolation)
            {
                throw Problems.Conflict("customer_has_tickets", "Talebi olan müşteri silinemez.");
            }
            return true;
        }, ct);
        return Results.NoContent();
    }

    private static async Task SaveAsync(TenantContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, CustomersModule.UniqueEmailIndex))
        {
            throw Problems.Conflict("customer_email_exists", "Bu e-posta adresiyle bir müşteri zaten var.");
        }
    }

    private sealed record ValidInput(string Name, string? CompanyTitle, string? Email, string? Phone, string? Notes);

    private static ValidInput Validate(CustomerRequest request)
    {
        var validator = new Validator();
        var name = validator.Required("name", request.Name, 120);
        var companyTitle = validator.Optional("companyTitle", request.CompanyTitle, 160);
        var email = validator.Optional("email", request.Email, 254);
        if (email is not null && !EmailNormalizer.IsPlausible(email))
        {
            validator.Add("email", "Geçerli bir e-posta adresi girin.");
        }
        var phone = validator.Optional("phone", request.Phone, 40);
        if (phone is not null && (phone.Any(ch => !(char.IsAsciiDigit(ch) || ch is '+' or ' ' or '-' or '(' or ')')) || phone.Count(char.IsAsciiDigit) < 7))
        {
            validator.Add("phone", "Telefon yalnız rakam, boşluk ve + - ( ) içerebilir; en az 7 rakam olmalıdır.");
        }
        var notes = validator.Optional("notes", request.Notes, 2000);
        validator.ThrowIfInvalid();
        return new ValidInput(name, companyTitle, email, phone, notes);
    }

    private static CustomerResponse ToResponse(Customer c, bool unmasked) => new(
        c.Id, c.Name, c.CompanyTitle,
        c.Email is null ? null : unmasked ? c.Email : EmailNormalizer.Mask(c.Email),
        c.Phone is null ? null : unmasked ? c.Phone : Customer.MaskPhone(c.Phone),
        unmasked ? c.Notes : null,
        PiiMasked: !unmasked,
        c.CreatedAt, c.UpdatedAt, c.Version);
}

using Kulvar.Modules.Tenancy.Features;
using Kulvar.Platform.Messaging;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kulvar.Modules.Tenancy;

public static class TenancyModule
{
    /// <summary>Süreçten bağımsız kayıtlar (API ve worker).</summary>
    public static IServiceCollection AddTenancyModule(this IServiceCollection services)
    {
        services.AddSingleton<IOutboxHandler, InviteCreatedHandler>();
        services.AddSingleton<IOutboxHandler, InviteAcceptedHandler>();
        services.AddSingleton<IOutboxHandler, OwnershipOfferedHandler>();
        return services;
    }

    /// <summary>Yalnız API süreci: istek kullanıcısına bağlı şirket yürütücüsü.</summary>
    public static IServiceCollection AddTenancyApi(this IServiceCollection services)
    {
        services.AddScoped<ITenantExecutor, TenantExecutor>();
        return services;
    }

    public static IEndpointRouteBuilder MapTenancyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        TenantEndpoints.Map(endpoints);
        MemberEndpoints.Map(endpoints);
        InviteEndpoints.Map(endpoints);
        OwnershipEndpoints.Map(endpoints);
        return endpoints;
    }
}

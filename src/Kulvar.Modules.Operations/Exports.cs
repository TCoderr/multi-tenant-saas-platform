using Kulvar.Modules.Tenancy.Domain;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Security;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;

namespace Kulvar.Modules.Operations;

public sealed class ExportOptions
{
    /// <summary>Artefaktların yazıldığı özel kök dizin (web kökünün dışında). Boşsa export devre dışıdır.</summary>
    public string Root { get; set; } = string.Empty;
    public int RetentionHours { get; set; } = 24;
    public int DownloadTokenSeconds { get; set; } = 60;
    public int LeaseSeconds { get; set; } = 120;
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Artefakt yolu yalnız sunucunun ürettiği iki GUID'den kurulur; istemci girdisi yola hiç girmez.
    /// Yine de çözümlenen yolun kökün altında kaldığı doğrulanır.
    /// </summary>
    public string ArtifactPath(Guid tenantId, Guid jobId)
    {
        if (string.IsNullOrWhiteSpace(Root))
        {
            throw new InvalidOperationException("Exports:Root yapılandırılmamış.");
        }
        var root = Path.GetFullPath(Root);
        var path = Path.GetFullPath(Path.Combine(root, tenantId.ToString("D"), jobId.ToString("D") + ".zip"));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Artefakt yolu kök dizinin dışına çıkıyor.");
        }
        return path;
    }

    public string TenantDirectory(Guid tenantId) => Path.GetDirectoryName(ArtifactPath(tenantId, Guid.Empty))!;
}

public enum ExportStatus { Queued, Running, Ready, Failed, Expired, Cancelled }

/// <summary>Async şirket export'u. İsteyen kullanıcı ve şirket satırda sabittir; indirme yetkisi ikisine de bakar.</summary>
public sealed class ExportJob : ITenantOwned
{
    private ExportJob() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RequestedByUserId { get; private set; }
    public ExportStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public long? SizeBytes { get; private set; }
    public string? Sha256 { get; private set; }
    public byte[]? DownloadTokenHash { get; private set; }
    public DateTimeOffset? DownloadTokenExpiresAt { get; private set; }
    public int Attempts { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public long LeaseToken { get; private set; }
    public string? FailureCode { get; private set; }

    public static ExportJob Queue(Guid tenantId, Guid userId, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        TenantId = tenantId,
        RequestedByUserId = userId,
        Status = ExportStatus.Queued,
        CreatedAt = now,
    };

    public bool IsDownloadable(DateTimeOffset now) => Status == ExportStatus.Ready && ExpiresAt is { } expires && expires > now;

    /// <summary>Kısa ömürlü, tek kullanımlık indirme yetkisi. Yalnız hash saklanır.</summary>
    public void IssueDownloadToken(byte[] tokenHash, DateTimeOffset expiresAt)
    {
        DownloadTokenHash = tokenHash;
        DownloadTokenExpiresAt = expiresAt;
    }

    public bool TryConsumeDownloadToken(byte[] presentedHash, DateTimeOffset now)
    {
        var valid = DownloadTokenHash is not null && DownloadTokenExpiresAt is { } expires && expires > now
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(DownloadTokenHash, presentedHash);
        if (valid)
        {
            DownloadTokenHash = null; // Tek kullanım.
            DownloadTokenExpiresAt = null;
        }
        return valid;
    }
}

internal sealed class ExportJobConfiguration : IEntityTypeConfiguration<ExportJob>
{
    public void Configure(EntityTypeBuilder<ExportJob> b)
    {
        b.ToTable("export_jobs", t => t.HasCheckConstraint("ck_export_jobs_status", "status IN ('Queued','Running','Ready','Failed','Expired','Cancelled')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.Property(x => x.LeaseOwner).HasMaxLength(100);
        b.Property(x => x.FailureCode).HasMaxLength(60);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.RequestedByUserId }).HasPrincipalKey(m => new { m.TenantId, m.UserId })
            .OnDelete(DeleteBehavior.Restrict);
        // Şirket başına aynı anda en çok bir etkin export (adillik: bir şirket export kuyruğunu dolduramaz).
        b.HasIndex(x => x.TenantId).IsUnique().HasFilter("status IN ('Queued','Running')");
        b.HasIndex(x => x.CreatedAt).HasFilter("status IN ('Queued','Running')");
        b.HasIndex(x => x.ExpiresAt).HasFilter("status = 'Ready'");
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
    }
}

internal static class ExportEndpoints
{
    private const string ActiveIndex = "ix_export_jobs_tenant_id";

    public static async Task<IResult> List(Guid tenantId, ITenantExecutor executor, CancellationToken ct)
    {
        var items = await executor.RunAsync(tenantId, Permission.ExportManage, TenantLock.None, async (ctx, c) =>
            (await ctx.Db.Set<ExportJob>().AsNoTracking().OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).Take(50).ToListAsync(c))
            .Select(j => ToDto(j, ctx)).ToList(), ct);
        return Results.Ok(new { items });
    }

    public static async Task<IResult> Request(Guid tenantId, ITenantExecutor executor, CancellationToken ct)
    {
        var dto = await executor.RunAsync(tenantId, Permission.ExportManage, TenantLock.ActorShared, async (ctx, c) =>
        {
            if (!ctx.Entitlements.ExportEnabled)
            {
                throw new ProblemException(403, "feature_not_in_plan", "Dışa aktarma planınıza dahil değil.", new Dictionary<string, object?> { ["feature"] = "export", ["planCode"] = ctx.Entitlements.PlanCode });
            }
            var job = ExportJob.Queue(ctx.TenantId, ctx.UserId, ctx.Now);
            ctx.Db.Add(job);
            ctx.Audit("export.requested", "export", job.Id);
            try
            {
                await ctx.Db.SaveChangesAsync(c);
            }
            catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, ActiveIndex))
            {
                throw Problems.Conflict("export_in_progress", "Devam eden bir dışa aktarma var. Tamamlanınca yenisini isteyebilirsiniz.");
            }
            return ToDto(job, ctx);
        }, ct);
        return Results.Json(dto, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>İndirme bağlantısı: yalnız işi isteyen kullanıcıya, kısa ömürlü ve tek kullanımlık token ile.</summary>
    public static async Task<IResult> DownloadLink(Guid tenantId, Guid id, ITenantExecutor executor, IOptions<ExportOptions> options, CancellationToken ct)
    {
        var response = await executor.RunAsync(tenantId, Permission.ExportManage, TenantLock.ActorShared, async (ctx, c) =>
        {
            var job = await FindOwnAsync(ctx, id, c);
            EnsureDownloadable(job, ctx.Now);
            var token = Tokens.NewToken();
            var expiresAt = ctx.Now.AddSeconds(options.Value.DownloadTokenSeconds);
            job.IssueDownloadToken(Tokens.Hash(token), expiresAt);
            ctx.Audit("export.download_link_issued", "export", job.Id);
            await ctx.Db.SaveChangesAsync(c);
            return new { url = $"/api/t/{tenantId}/exports/{id}/file?token={token}", expiresAt };
        }, ct);
        return Results.Ok(response);
    }

    /// <summary>
    /// Dosya indirme. Token tek başına yetmez: oturum + güncel üyelik + izin + işi isteyen kullanıcı + geçerli token birlikte aranır.
    /// Başka şirketin yolu, başka kullanıcının işi veya silinmiş şirket 404'tür; dosya yolu istemci girdisinden kurulmaz.
    /// </summary>
    public static async Task<IResult> Download(Guid tenantId, Guid id, HttpContext http, ITenantExecutor executor, IOptions<ExportOptions> options, CancellationToken ct)
    {
        var token = http.Request.Query["token"].ToString();
        if (!Tokens.LooksValid(token))
        {
            throw new ProblemException(403, "invalid_download_token", "İndirme bağlantısı geçersiz veya süresi dolmuş.");
        }
        var path = await executor.RunAsync(tenantId, Permission.ExportManage, TenantLock.ActorShared, async (ctx, c) =>
        {
            var job = await FindOwnAsync(ctx, id, c);
            EnsureDownloadable(job, ctx.Now);
            if (!job.TryConsumeDownloadToken(Tokens.Hash(token), ctx.Now))
            {
                throw new ProblemException(403, "invalid_download_token", "İndirme bağlantısı geçersiz veya süresi dolmuş.");
            }
            ctx.Audit("export.downloaded", "export", job.Id);
            await ctx.Db.SaveChangesAsync(c);
            return options.Value.ArtifactPath(ctx.TenantId, job.Id);
        }, ct);

        if (!File.Exists(path))
        {
            throw new ProblemException(410, "export_expired", "Dışa aktarma dosyası artık mevcut değil.");
        }
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true), "application/zip", $"kulvar-export-{id:N}.zip");
    }

    private static async Task<ExportJob> FindOwnAsync(TenantContext ctx, Guid id, CancellationToken ct) =>
        // Başkasının (aynı şirketten başka bir Owner'ın bile) işi "yok"tur.
        await ctx.Db.Set<ExportJob>().SingleOrDefaultAsync(j => j.Id == id && j.RequestedByUserId == ctx.UserId, ct) ?? throw Problems.NotFound();

    private static void EnsureDownloadable(ExportJob job, DateTimeOffset now)
    {
        if (job.Status == ExportStatus.Ready && !job.IsDownloadable(now) || job.Status == ExportStatus.Expired)
        {
            throw new ProblemException(410, "export_expired", "Dışa aktarma dosyasının süresi dolmuş. Yeni bir dışa aktarma isteyin.");
        }
        if (job.Status != ExportStatus.Ready)
        {
            throw Problems.Conflict("export_not_ready", "Dışa aktarma henüz hazır değil.", new Dictionary<string, object?> { ["exportStatus"] = job.Status.ToString() });
        }
    }

    private static object ToDto(ExportJob j, TenantContext ctx) => new
    {
        id = j.Id,
        status = j.Status == ExportStatus.Ready && !j.IsDownloadable(ctx.Now) ? ExportStatus.Expired : j.Status,
        createdAt = j.CreatedAt,
        completedAt = j.CompletedAt,
        expiresAt = j.ExpiresAt,
        sizeBytes = j.SizeBytes,
        requestedByMe = j.RequestedByUserId == ctx.UserId,
        failureCode = j.FailureCode,
    };
}

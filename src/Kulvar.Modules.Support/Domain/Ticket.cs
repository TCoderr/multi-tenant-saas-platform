using Kulvar.Platform.Data;
using Kulvar.Platform.Security;
using Kulvar.Platform.Tenancy;

namespace Kulvar.Modules.Support.Domain;

public enum TicketStatus { Open, InProgress, Resolved, Closed }

public enum TicketPriority { Low, Normal, High, Urgent }

public enum TicketAction { Start, Release, Resolve, Close, Reopen }

public enum TransitionDecision
{
    Allowed,

    /// <summary>Bu durumdan bu eylem tanımlı değil.</summary>
    Invalid,

    /// <summary>Eylem tanımlı ama aktörün rolü yetmiyor (kapalı talebi yeniden açma).</summary>
    Forbidden,

    /// <summary>Kapalı talep yeniden açma penceresinin dışında.</summary>
    ReopenWindowExpired,
}

/// <summary>Destek talebi durum makinesi ve rol/atanan kuralları (02 §2). Saf fonksiyonlar.</summary>
public static class TicketWorkflow
{
    public static readonly TimeSpan ReopenWindow = TimeSpan.FromDays(30);

    public static TransitionDecision Evaluate(TicketStatus current, TicketAction action, TenantRole role, DateTimeOffset? closedAt, DateTimeOffset now) =>
        (current, action) switch
        {
            (TicketStatus.Open, TicketAction.Start) => TransitionDecision.Allowed,
            (TicketStatus.InProgress, TicketAction.Release) => TransitionDecision.Allowed,
            (TicketStatus.Open or TicketStatus.InProgress, TicketAction.Resolve) => TransitionDecision.Allowed,
            (TicketStatus.Resolved, TicketAction.Close) => TransitionDecision.Allowed,
            (TicketStatus.Resolved, TicketAction.Reopen) => TransitionDecision.Allowed,
            (TicketStatus.Closed, TicketAction.Reopen) when role is not (TenantRole.Owner or TenantRole.Admin) => TransitionDecision.Forbidden,
            (TicketStatus.Closed, TicketAction.Reopen) when closedAt is null || now - closedAt.Value > ReopenWindow => TransitionDecision.ReopenWindowExpired,
            (TicketStatus.Closed, TicketAction.Reopen) => TransitionDecision.Allowed,
            _ => TransitionDecision.Invalid,
        };

    public static TicketStatus Target(TicketAction action) => action switch
    {
        TicketAction.Start => TicketStatus.InProgress,
        TicketAction.Release or TicketAction.Reopen => TicketStatus.Open,
        TicketAction.Resolve => TicketStatus.Resolved,
        TicketAction.Close => TicketStatus.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Owner/Admin her talebi; Agent yalnız kendine atanmış veya atanmamış talebi değiştirebilir; Viewer hiçbirini.</summary>
    public static bool CanModify(TenantRole role, Guid actorUserId, Guid? assigneeUserId) => role switch
    {
        TenantRole.Owner or TenantRole.Admin => true,
        TenantRole.Agent => assigneeUserId is null || assigneeUserId == actorUserId,
        _ => false,
    };

    /// <summary>Owner/Admin herhangi bir (atanabilir) üyeye atar; Agent yalnız kendine alabilir veya kendinden bırakabilir.</summary>
    public static bool CanAssign(TenantRole role, Guid actorUserId, Guid? currentAssignee, Guid? newAssignee) => role switch
    {
        TenantRole.Owner or TenantRole.Admin => true,
        TenantRole.Agent => (currentAssignee is null && newAssignee == actorUserId) || (currentAssignee == actorUserId && newAssignee is null) || (currentAssignee == actorUserId && newAssignee == actorUserId),
        _ => false,
    };

    /// <summary>Viewer talep üstlenemez.</summary>
    public static bool IsAssignableRole(TenantRole role) => role is TenantRole.Owner or TenantRole.Admin or TenantRole.Agent;

    public static IReadOnlyList<TicketAction> AllowedActions(TicketStatus current, TenantRole role, Guid actorUserId, Guid? assigneeUserId, DateTimeOffset? closedAt, DateTimeOffset now)
    {
        if (!CanModify(role, actorUserId, assigneeUserId))
        {
            return [];
        }
        return Enum.GetValues<TicketAction>().Where(a => Evaluate(current, a, role, closedAt, now) == TransitionDecision.Allowed).ToList();
    }
}

public sealed class Ticket : ITenantOwned
{
    private Ticket() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public int Number { get; private set; }
    public Guid CustomerId { get; private set; }
    public string Title { get; private set; } = null!;
    public string Description { get; private set; } = null!;
    public TicketPriority Priority { get; private set; }
    public TicketStatus Status { get; private set; }
    public Guid? AssigneeUserId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public int ReopenCount { get; private set; }
    public int Version { get; private set; }

    public static Ticket Create(Guid id, Guid tenantId, int number, Guid customerId, string title, string description, TicketPriority priority, Guid? assigneeUserId, Guid createdByUserId, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        Number = number,
        CustomerId = customerId,
        Title = title,
        Description = description,
        Priority = priority,
        Status = TicketStatus.Open,
        AssigneeUserId = assigneeUserId,
        CreatedByUserId = createdByUserId,
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1,
    };

    public void Edit(string title, string description, TicketPriority priority, DateTimeOffset now)
    {
        Title = title;
        Description = description;
        Priority = priority;
        Touch(now);
    }

    public void Assign(Guid? assigneeUserId, DateTimeOffset now)
    {
        AssigneeUserId = assigneeUserId;
        Touch(now);
    }

    /// <summary>Geçişi uygular. Çağıran önce <see cref="TicketWorkflow.Evaluate"/> ile izin almış olmalıdır.</summary>
    public void Apply(TicketAction action, Guid actorUserId, TenantRole actorRole, DateTimeOffset now)
    {
        switch (action)
        {
            case TicketAction.Start:
                // Üstlenen yoksa ve aktör talep üstlenebiliyorsa talep ona atanır.
                if (AssigneeUserId is null && TicketWorkflow.IsAssignableRole(actorRole))
                {
                    AssigneeUserId = actorUserId;
                }
                break;
            case TicketAction.Release:
                AssigneeUserId = null;
                break;
            case TicketAction.Resolve:
                ResolvedAt = now;
                break;
            case TicketAction.Close:
                ClosedAt = now;
                break;
            case TicketAction.Reopen:
                ReopenCount++;
                ResolvedAt = null;
                ClosedAt = null;
                break;
        }
        Status = TicketWorkflow.Target(action);
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}

/// <summary>Düz metin yorum. HTML olarak yorumlanmaz; olduğu gibi saklanır ve metin olarak döner.</summary>
public sealed class TicketComment : ITenantOwned
{
    private TicketComment() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TicketId { get; private set; }
    public Guid AuthorUserId { get; private set; }
    public string Body { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static TicketComment Create(Guid tenantId, Guid ticketId, Guid authorUserId, string body, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        TenantId = tenantId,
        TicketId = ticketId,
        AuthorUserId = authorUserId,
        Body = body,
        CreatedAt = now,
    };
}

/// <summary>Şirket içi artan talep numarası sayacı.</summary>
public sealed class TenantCounter : ITenantOwned
{
    private TenantCounter() { }

    public Guid TenantId { get; private set; }
    public int NextTicketNumber { get; private set; }
}

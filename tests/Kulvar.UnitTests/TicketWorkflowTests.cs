using Kulvar.Modules.Customers;
using Kulvar.Modules.Support.Domain;
using Kulvar.Platform.Tenancy;

namespace Kulvar.UnitTests;

public sealed class TicketWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Actor = Guid.Parse("018f0000-0000-7000-8000-000000000001");
    private static readonly Guid Other = Guid.Parse("018f0000-0000-7000-8000-000000000002");

    [Theory]
    [InlineData(TicketStatus.Open, TicketAction.Start, TransitionDecision.Allowed)]
    [InlineData(TicketStatus.Open, TicketAction.Resolve, TransitionDecision.Allowed)]
    [InlineData(TicketStatus.Open, TicketAction.Release, TransitionDecision.Invalid)]
    [InlineData(TicketStatus.Open, TicketAction.Close, TransitionDecision.Invalid)]
    [InlineData(TicketStatus.Open, TicketAction.Reopen, TransitionDecision.Invalid)]
    [InlineData(TicketStatus.InProgress, TicketAction.Release, TransitionDecision.Allowed)]
    [InlineData(TicketStatus.InProgress, TicketAction.Resolve, TransitionDecision.Allowed)]
    [InlineData(TicketStatus.InProgress, TicketAction.Start, TransitionDecision.Invalid)]
    [InlineData(TicketStatus.InProgress, TicketAction.Close, TransitionDecision.Invalid)]
    [InlineData(TicketStatus.Resolved, TicketAction.Close, TransitionDecision.Allowed)]
    [InlineData(TicketStatus.Resolved, TicketAction.Reopen, TransitionDecision.Allowed)]
    [InlineData(TicketStatus.Resolved, TicketAction.Start, TransitionDecision.Invalid)]
    [InlineData(TicketStatus.Resolved, TicketAction.Resolve, TransitionDecision.Invalid)]
    [InlineData(TicketStatus.Closed, TicketAction.Start, TransitionDecision.Invalid)]
    [InlineData(TicketStatus.Closed, TicketAction.Resolve, TransitionDecision.Invalid)]
    [InlineData(TicketStatus.Closed, TicketAction.Close, TransitionDecision.Invalid)]
    public void Evaluate_FollowsStateMachine_ForAgent(TicketStatus status, TicketAction action, TransitionDecision expected) =>
        Assert.Equal(expected, TicketWorkflow.Evaluate(status, action, TenantRole.Agent, closedAt: Now.AddDays(-1), Now));

    [Theory]
    [InlineData(TenantRole.Owner, TransitionDecision.Allowed)]
    [InlineData(TenantRole.Admin, TransitionDecision.Allowed)]
    [InlineData(TenantRole.Agent, TransitionDecision.Forbidden)]
    [InlineData(TenantRole.Viewer, TransitionDecision.Forbidden)]
    public void ClosedTicket_ReopenedOnlyByOwnerOrAdmin(TenantRole role, TransitionDecision expected) =>
        Assert.Equal(expected, TicketWorkflow.Evaluate(TicketStatus.Closed, TicketAction.Reopen, role, Now.AddDays(-1), Now));

    [Fact]
    public void ClosedTicket_ReopenWindow_Is30Days_Inclusive()
    {
        Assert.Equal(TransitionDecision.Allowed, TicketWorkflow.Evaluate(TicketStatus.Closed, TicketAction.Reopen, TenantRole.Owner, Now.AddDays(-30), Now));
        Assert.Equal(TransitionDecision.ReopenWindowExpired, TicketWorkflow.Evaluate(TicketStatus.Closed, TicketAction.Reopen, TenantRole.Owner, Now.AddDays(-30).AddSeconds(-1), Now));
        Assert.Equal(TransitionDecision.ReopenWindowExpired, TicketWorkflow.Evaluate(TicketStatus.Closed, TicketAction.Reopen, TenantRole.Owner, closedAt: null, Now));
    }

    [Fact]
    public void CanModify_AgentOnlyOwnOrUnassigned()
    {
        Assert.True(TicketWorkflow.CanModify(TenantRole.Agent, Actor, null));
        Assert.True(TicketWorkflow.CanModify(TenantRole.Agent, Actor, Actor));
        Assert.False(TicketWorkflow.CanModify(TenantRole.Agent, Actor, Other));
        Assert.True(TicketWorkflow.CanModify(TenantRole.Admin, Actor, Other));
        Assert.True(TicketWorkflow.CanModify(TenantRole.Owner, Actor, Other));
        Assert.False(TicketWorkflow.CanModify(TenantRole.Viewer, Actor, null));
        Assert.False(TicketWorkflow.CanModify(TenantRole.Viewer, Actor, Actor));
    }

    [Fact]
    public void CanAssign_AgentOnlyTakesOrReleasesOwn()
    {
        Assert.True(TicketWorkflow.CanAssign(TenantRole.Agent, Actor, null, Actor));    // kendine al
        Assert.True(TicketWorkflow.CanAssign(TenantRole.Agent, Actor, Actor, null));    // bırak
        Assert.False(TicketWorkflow.CanAssign(TenantRole.Agent, Actor, null, Other));   // başkasına ver
        Assert.False(TicketWorkflow.CanAssign(TenantRole.Agent, Actor, Actor, Other));  // devret
        Assert.False(TicketWorkflow.CanAssign(TenantRole.Agent, Actor, Other, Actor));  // başkasından al
        Assert.False(TicketWorkflow.CanAssign(TenantRole.Agent, Actor, Other, null));   // başkasınınkini boşalt
        Assert.True(TicketWorkflow.CanAssign(TenantRole.Admin, Actor, Other, Actor));
        Assert.False(TicketWorkflow.CanAssign(TenantRole.Viewer, Actor, null, Actor));
        Assert.False(TicketWorkflow.IsAssignableRole(TenantRole.Viewer));
    }

    [Fact]
    public void Apply_Start_AssignsActor_WhenUnassigned_AndReopen_ClearsTimestamps()
    {
        var ticket = Ticket.Create(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), "Başlık", "Açıklama", TicketPriority.Normal, null, Actor, Now);

        ticket.Apply(TicketAction.Start, Actor, TenantRole.Agent, Now);
        Assert.Equal(TicketStatus.InProgress, ticket.Status);
        Assert.Equal(Actor, ticket.AssigneeUserId);
        Assert.Equal(2, ticket.Version);

        ticket.Apply(TicketAction.Resolve, Actor, TenantRole.Agent, Now.AddHours(1));
        ticket.Apply(TicketAction.Close, Actor, TenantRole.Agent, Now.AddHours(2));
        Assert.Equal(Now.AddHours(1), ticket.ResolvedAt);
        Assert.Equal(Now.AddHours(2), ticket.ClosedAt);

        ticket.Apply(TicketAction.Reopen, Other, TenantRole.Owner, Now.AddHours(3));
        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Null(ticket.ResolvedAt);
        Assert.Null(ticket.ClosedAt);
        Assert.Equal(1, ticket.ReopenCount);
        Assert.Equal(Actor, ticket.AssigneeUserId); // Yeniden açmak atamayı değiştirmez.
        Assert.Equal(5, ticket.Version);

        ticket.Apply(TicketAction.Start, Other, TenantRole.Owner, Now.AddHours(4));
        ticket.Apply(TicketAction.Release, Other, TenantRole.Owner, Now.AddHours(5));
        Assert.Null(ticket.AssigneeUserId);
        Assert.Equal(TicketStatus.Open, ticket.Status);
    }

    [Fact]
    public void AllowedActions_EmptyForReadersAndForeignAgents()
    {
        Assert.Empty(TicketWorkflow.AllowedActions(TicketStatus.Open, TenantRole.Viewer, Actor, null, null, Now));
        Assert.Empty(TicketWorkflow.AllowedActions(TicketStatus.Open, TenantRole.Agent, Actor, Other, null, Now));
        Assert.Equal([TicketAction.Start, TicketAction.Resolve], TicketWorkflow.AllowedActions(TicketStatus.Open, TenantRole.Agent, Actor, null, null, Now));
        Assert.Equal([TicketAction.Reopen], TicketWorkflow.AllowedActions(TicketStatus.Closed, TenantRole.Admin, Actor, Other, Now.AddDays(-2), Now));
    }

    [Theory]
    [InlineData("+90 555 123 45 67", "*** ** 67")]
    [InlineData("5", "***")]
    public void Customer_MaskPhone_KeepsOnlyLastTwoDigits(string phone, string expected) => Assert.Equal(expected, Customer.MaskPhone(phone));
}

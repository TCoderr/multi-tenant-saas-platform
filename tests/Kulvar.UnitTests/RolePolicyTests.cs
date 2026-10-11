using Kulvar.Modules.Tenancy.Domain;
using Kulvar.Platform.Tenancy;

namespace Kulvar.UnitTests;

public sealed class RolePolicyTests
{
    [Theory]
    [InlineData(TenantRole.Owner, TenantRole.Admin, true)]
    [InlineData(TenantRole.Owner, TenantRole.Agent, true)]
    [InlineData(TenantRole.Owner, TenantRole.Viewer, true)]
    [InlineData(TenantRole.Owner, TenantRole.Owner, false)]
    [InlineData(TenantRole.Admin, TenantRole.Agent, true)]
    [InlineData(TenantRole.Admin, TenantRole.Viewer, true)]
    [InlineData(TenantRole.Admin, TenantRole.Admin, false)]
    [InlineData(TenantRole.Admin, TenantRole.Owner, false)]
    [InlineData(TenantRole.Agent, TenantRole.Viewer, false)]
    [InlineData(TenantRole.Viewer, TenantRole.Viewer, false)]
    public void CanInvite_FollowsMatrix(TenantRole actor, TenantRole invited, bool expected) =>
        Assert.Equal(expected, RolePolicy.CanInvite(actor, invited));

    [Fact]
    public void OwnerRole_IsNeverGrantedByRoleChange()
    {
        foreach (var actor in Enum.GetValues<TenantRole>())
        {
            foreach (var target in Enum.GetValues<TenantRole>())
            {
                Assert.Equal(MemberDecision.OwnerRequiresOffer, RolePolicy.CanChangeRole(actor, target, TenantRole.Owner, activeOwnerCount: 5));
            }
        }
    }

    [Theory]
    [InlineData(TenantRole.Admin, TenantRole.Agent, MemberDecision.Allowed)]
    [InlineData(TenantRole.Agent, TenantRole.Admin, MemberDecision.Allowed)]
    [InlineData(TenantRole.Viewer, TenantRole.Agent, MemberDecision.Allowed)]
    public void Owner_CanChangeNonOwnerRoles(TenantRole from, TenantRole to, MemberDecision expected) =>
        Assert.Equal(expected, RolePolicy.CanChangeRole(TenantRole.Owner, from, to, activeOwnerCount: 1));

    [Fact]
    public void Owner_DemotingAnOwner_RequiresAnotherOwner()
    {
        Assert.Equal(MemberDecision.LastOwner, RolePolicy.CanChangeRole(TenantRole.Owner, TenantRole.Owner, TenantRole.Admin, activeOwnerCount: 1));
        Assert.Equal(MemberDecision.Allowed, RolePolicy.CanChangeRole(TenantRole.Owner, TenantRole.Owner, TenantRole.Admin, activeOwnerCount: 2));
    }

    [Theory]
    [InlineData(TenantRole.Agent, TenantRole.Viewer, MemberDecision.Allowed)]
    [InlineData(TenantRole.Viewer, TenantRole.Agent, MemberDecision.Allowed)]
    [InlineData(TenantRole.Agent, TenantRole.Admin, MemberDecision.Forbidden)]
    [InlineData(TenantRole.Admin, TenantRole.Agent, MemberDecision.Forbidden)]
    [InlineData(TenantRole.Owner, TenantRole.Viewer, MemberDecision.Forbidden)]
    public void Admin_ManagesOnlyAgentAndViewer(TenantRole from, TenantRole to, MemberDecision expected) =>
        Assert.Equal(expected, RolePolicy.CanChangeRole(TenantRole.Admin, from, to, activeOwnerCount: 2));

    [Theory]
    [InlineData(TenantRole.Agent)]
    [InlineData(TenantRole.Viewer)]
    public void AgentAndViewer_CannotManageAnyone(TenantRole actor)
    {
        foreach (var target in Enum.GetValues<TenantRole>())
        {
            Assert.Equal(MemberDecision.Forbidden, RolePolicy.CanRemove(actor, target, 2));
            foreach (var to in new[] { TenantRole.Admin, TenantRole.Agent, TenantRole.Viewer })
            {
                if (to != target)
                {
                    Assert.Equal(MemberDecision.Forbidden, RolePolicy.CanChangeRole(actor, target, to, 2));
                }
            }
        }
    }

    [Fact]
    public void Remove_And_Leave_ProtectLastOwner()
    {
        Assert.Equal(MemberDecision.LastOwner, RolePolicy.CanRemove(TenantRole.Owner, TenantRole.Owner, 1));
        Assert.Equal(MemberDecision.Allowed, RolePolicy.CanRemove(TenantRole.Owner, TenantRole.Owner, 2));
        Assert.Equal(MemberDecision.Forbidden, RolePolicy.CanRemove(TenantRole.Admin, TenantRole.Owner, 2));
        Assert.Equal(MemberDecision.Forbidden, RolePolicy.CanRemove(TenantRole.Admin, TenantRole.Admin, 2));
        Assert.Equal(MemberDecision.Allowed, RolePolicy.CanRemove(TenantRole.Admin, TenantRole.Viewer, 1));
        Assert.Equal(MemberDecision.LastOwner, RolePolicy.CanLeave(TenantRole.Owner, 1));
        Assert.Equal(MemberDecision.Allowed, RolePolicy.CanLeave(TenantRole.Owner, 2));
        Assert.Equal(MemberDecision.Allowed, RolePolicy.CanLeave(TenantRole.Viewer, 1));
    }
}

public sealed class AccessModeRulesTests
{
    [Theory]
    [InlineData(TenantStatus.Active, SubscriptionStatus.Trialing, AccessMode.Full)]
    [InlineData(TenantStatus.Active, SubscriptionStatus.Active, AccessMode.Full)]
    [InlineData(TenantStatus.Active, SubscriptionStatus.PastDue, AccessMode.Full)]
    [InlineData(TenantStatus.Active, SubscriptionStatus.Suspended, AccessMode.Recovery)]
    [InlineData(TenantStatus.Active, SubscriptionStatus.Cancelled, AccessMode.Recovery)]
    [InlineData(TenantStatus.DeletionPending, SubscriptionStatus.Active, AccessMode.ReadOnly)]
    [InlineData(TenantStatus.Suspended, SubscriptionStatus.Active, AccessMode.Locked)]
    public void ComputeMode_FollowsMatrix(TenantStatus tenant, SubscriptionStatus subscription, AccessMode expected) =>
        Assert.Equal(expected, AccessRules.ComputeMode(tenant, subscription));

    [Theory]
    [InlineData(TenantStatus.Purging)]
    [InlineData(TenantStatus.Deleted)]
    public void GoneTenants_HaveNoMode(TenantStatus tenant) =>
        Assert.Null(AccessRules.ComputeMode(tenant, SubscriptionStatus.Active));

    [Fact]
    public void Recovery_IsReadOnlyForContent_ButOwnerCanRecover()
    {
        Assert.True(AccessRules.IsAllowed(Permission.CustomersRead, TenantRole.Viewer, AccessMode.Recovery));
        Assert.False(AccessRules.IsAllowed(Permission.CustomersWrite, TenantRole.Owner, AccessMode.Recovery));
        Assert.False(AccessRules.IsAllowed(Permission.TicketsWrite, TenantRole.Agent, AccessMode.Recovery));
        Assert.False(AccessRules.IsAllowed(Permission.MembersInvite, TenantRole.Owner, AccessMode.Recovery));
        Assert.True(AccessRules.IsAllowed(Permission.BillingManage, TenantRole.Owner, AccessMode.Recovery));
        Assert.True(AccessRules.IsAllowed(Permission.MembersManage, TenantRole.Owner, AccessMode.Recovery));
        Assert.False(AccessRules.IsAllowed(Permission.BillingManage, TenantRole.Admin, AccessMode.Recovery));
    }

    [Fact]
    public void Locked_AllowsOnlyWorkspaceSummaryAndLeaving()
    {
        foreach (var permission in Enum.GetValues<Permission>())
        {
            var expected = permission is Permission.WorkspaceRead or Permission.MembersLeave;
            Assert.Equal(expected, AccessRules.IsAllowed(permission, TenantRole.Owner, AccessMode.Locked));
        }
    }

    [Fact]
    public void ReadOnly_LetsOwnerCancelDeletion_AndNothingElseWrites()
    {
        Assert.True(AccessRules.IsAllowed(Permission.TenantDeleteCancel, TenantRole.Owner, AccessMode.ReadOnly));
        Assert.False(AccessRules.IsAllowed(Permission.TenantDeleteCancel, TenantRole.Owner, AccessMode.Full));
        Assert.False(AccessRules.IsAllowed(Permission.TicketsWrite, TenantRole.Owner, AccessMode.ReadOnly));
        Assert.False(AccessRules.IsAllowed(Permission.BillingManage, TenantRole.Owner, AccessMode.ReadOnly));
        Assert.True(AccessRules.IsAllowed(Permission.ExportManage, TenantRole.Owner, AccessMode.ReadOnly));
    }

    [Fact]
    public void Viewer_NeverGetsWriteOrPiiPermissions()
    {
        var granted = AccessRules.GrantedCodes(TenantRole.Viewer, AccessMode.Full);
        Assert.Equal(["customers.read", "members.leave", "members.read", "ownership.accept", "tickets.read", "workspace.read"], granted);
    }

    [Fact]
    public void EveryPermission_HasARule()
    {
        foreach (var permission in Enum.GetValues<Permission>())
        {
            Assert.False(string.IsNullOrWhiteSpace(AccessRules.Code(permission)));
        }
    }
}

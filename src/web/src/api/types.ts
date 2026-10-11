// Sunucu yanıt şekilleri (docs/05-api-contracts.md ve uçların kendisi). Enum'lar sunucuda metin olarak serileşir.

export type TenantRole = 'Owner' | 'Admin' | 'Agent' | 'Viewer';
export type TenantStatus = 'Active' | 'Suspended' | 'DeletionPending' | 'Purging' | 'Deleted';
export type SubscriptionStatus = 'Trialing' | 'Active' | 'PastDue' | 'Suspended' | 'Cancelled';
export type AccessMode = 'Full' | 'Recovery' | 'ReadOnly' | 'Locked';

export type PermissionCode =
  | 'workspace.read'
  | 'members.leave'
  | 'tenant.settings'
  | 'tenant.delete'
  | 'tenant.delete.cancel'
  | 'customers.read'
  | 'customers.pii'
  | 'customers.write'
  | 'customers.delete'
  | 'tickets.read'
  | 'tickets.write'
  | 'members.read'
  | 'members.pii'
  | 'members.invite'
  | 'members.manage'
  | 'ownership.offer'
  | 'ownership.accept'
  | 'billing.read'
  | 'billing.manage'
  | 'audit.read'
  | 'export.manage';

export interface User {
  id: string;
  email: string;
  displayName: string;
  emailVerified: boolean;
  isPlatformOperator: boolean;
}

export interface Membership {
  tenantId: string;
  tenantName: string;
  role: TenantRole;
  tenantStatus: TenantStatus;
}

export interface Plan {
  code: string;
  name: string;
  priceMinor: number;
  currency: string;
  seatLimit: number;
  monthlyTicketLimit: number;
  exportEnabled: boolean;
  trialDays: number | null;
}

export interface PlansResponse {
  currency: string;
  items: Plan[];
}

export interface WorkspaceSummary {
  tenant: {
    id: string;
    name: string;
    status: TenantStatus;
    version: number;
    purgeAfter: string | null;
    suspendedReason: string | null;
  };
  userId: string;
  role: TenantRole;
  permissions: PermissionCode[];
  accessMode: AccessMode;
  subscription: {
    status: SubscriptionStatus;
    planCode: string;
    planName: string;
    trialEndsAt: string | null;
    graceEndsAt: string | null;
  };
  entitlements: { seatLimit: number; monthlyTicketLimit: number; exportEnabled: boolean };
  usage: { seatsUsed: number; pendingInvites: number; ticketsThisPeriod: number; periodResetsAt: string };
}

export interface Member {
  userId: string;
  displayName: string;
  email: string | null;
  role: TenantRole;
  joinedAt: string;
  version: number;
  isSelf: boolean;
}

export type InviteState = 'Sending' | 'Pending' | 'Expired' | 'Accepted' | 'Revoked';

export interface Invite {
  id: string;
  email: string;
  role: TenantRole;
  state: InviteState;
  invitedByName?: string;
  createdAt?: string;
  expiresAt: string;
}

export interface InviteLookup {
  tenantName: string;
  role: TenantRole;
  state: InviteState;
  expiresAt: string;
  invitedEmail: string;
  emailMatches: boolean;
  emailVerified: boolean;
}

export interface OwnershipOffer {
  id: string;
  fromUserId: string;
  fromName: string;
  toUserId: string;
  toName: string;
  demoteSelf: boolean;
  expiresAt: string;
  canAccept: boolean;
}

export interface Customer {
  id: string;
  name: string;
  companyTitle: string | null;
  email: string | null;
  phone: string | null;
  notes: string | null;
  piiMasked: boolean;
  createdAt: string;
  updatedAt: string;
  version: number;
}

export interface CustomerInput {
  name: string;
  companyTitle: string | null;
  email: string | null;
  phone: string | null;
  notes: string | null;
}

export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

export type TicketStatus = 'Open' | 'InProgress' | 'Resolved' | 'Closed';
export type TicketPriority = 'Low' | 'Normal' | 'High' | 'Urgent';
export type TicketAction = 'Start' | 'Release' | 'Resolve' | 'Close' | 'Reopen';

export interface TicketListItem {
  id: string;
  number: number;
  title: string;
  status: TicketStatus;
  priority: TicketPriority;
  customerId: string;
  customerName: string;
  assigneeUserId: string | null;
  assigneeName: string | null;
  createdAt: string;
  updatedAt: string;
  version: number;
}

export interface Ticket extends TicketListItem {
  description: string;
  createdByUserId: string;
  resolvedAt: string | null;
  closedAt: string | null;
  reopenCount: number;
  allowedActions: TicketAction[];
  canModify: boolean;
}

export interface TimelineItem {
  id: string;
  kind: 'comment' | 'event';
  authorUserId: string | null;
  authorName: string | null;
  text: string | null;
  action?: string | null;
  data?: Record<string, unknown> | null;
  at: string;
}

export interface AssignableMember {
  userId: string;
  displayName: string;
  role: TenantRole;
}

export type BillingOperationStatus =
  | 'Pending'
  | 'Unknown'
  | 'Succeeded'
  | 'Declined'
  | 'RefundPending'
  | 'Refunded'
  | 'Voided'
  | 'ManuallyClosed'
  | 'NeedsResolution';

export interface BillingOperation {
  operationId: string;
  planCode: string;
  amountMinor: number;
  currency: string;
  status: BillingOperationStatus;
  terminal: boolean;
  failureCode: string | null;
  createdAt: string;
  completedAt: string | null;
}

export interface BillingOverview {
  subscription: {
    status: SubscriptionStatus;
    planCode: string;
    planName: string;
    trialEndsAt: string | null;
    graceEndsAt: string | null;
    currentPeriodEnd: string | null;
  };
  entitlements: { seatLimit: number; monthlyTicketLimit: number; exportEnabled: boolean };
  usage: { ticketsThisPeriod: number; periodResetsAt: string };
  canManage: boolean;
  plans: Plan[];
  operations: BillingOperation[];
  simulation: { outcomeSelection: boolean };
}

export interface AuditEvent {
  id: string;
  action: string;
  actorUserId: string | null;
  targetType: string | null;
  targetId: string | null;
  ticketId: string | null;
  data: Record<string, unknown> | null;
  occurredAt: string;
}

export type ExportStatus = 'Queued' | 'Running' | 'Ready' | 'Failed' | 'Expired' | 'Cancelled';

export interface ExportJob {
  id: string;
  status: ExportStatus;
  createdAt: string;
  completedAt: string | null;
  expiresAt: string | null;
  sizeBytes: number | null;
  requestedByMe: boolean;
  failureCode: string | null;
}

export interface MailboxItem {
  id: string;
  kind: string;
  subject: string;
  createdAt: string;
}

export interface MailboxMessage extends MailboxItem {
  body: string;
  actionPath: string | null;
}

export interface PlatformTenant {
  id: string;
  name: string;
  status: TenantStatus;
  createdAt: string;
  suspendedReason: string | null;
  deletionRequestedAt: string | null;
  purgeAfter: string | null;
  subscriptionStatus: SubscriptionStatus | null;
  planCode: string | null;
  planName: string | null;
  trialEndsAt: string | null;
  graceEndsAt: string | null;
}

export interface PlatformOperation {
  operationId: string;
  tenantId: string;
  status: BillingOperationStatus;
  amountMinor: number;
  currency: string;
  failureCode: string | null;
  attempts: number;
  createdAt: string;
  updatedAt: string;
  resolutionNote: string | null;
}

export interface DeadJob {
  id: string;
  tenantId: string | null;
  type: string;
  attempts: number;
  lastError: string | null;
  createdAt: string;
}

export interface PlatformAuditEvent {
  id: string;
  action: string;
  tenantId: string | null;
  actorUserId: string | null;
  data: Record<string, unknown> | null;
  occurredAt: string;
}

export interface Items<T> {
  items: T[];
}

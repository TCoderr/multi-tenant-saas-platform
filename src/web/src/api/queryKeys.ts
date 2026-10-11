// Sorgu anahtarları. Şirket verisi her zaman ['u', userId, 't', tenantId, …] altında durur: bir kullanıcının ya da bir şirketin
// yanıtı başka bir kullanıcının/şirketin anahtarıyla aynı önbellek girdisine düşemez. Global "aktif şirket" yoktur.

import type { QueryKey } from '@tanstack/react-query';

export type TenantKey = readonly ['u', string, 't', string, ...unknown[]];
export type UserKey = readonly ['u', string, ...unknown[]];

function requireId(kind: string, value: string): string {
  if (value.length === 0) {
    throw new Error(`${kind} kimliği olmadan sorgu anahtarı kurulamaz.`);
  }
  return value;
}

export const queryKeys = {
  /** Oturumdan bağımsız, herkese açık veri (plan kataloğu). */
  publicPlans: ['public', 'plans'] as const,

  user: (userId: string, ...rest: unknown[]): UserKey => ['u', requireId('Kullanıcı', userId), ...rest] as const,

  memberships: (userId: string): UserKey => queryKeys.user(userId, 'memberships'),
  mailbox: (userId: string): UserKey => queryKeys.user(userId, 'mailbox'),
  mailboxMessage: (userId: string, messageId: string): UserKey => queryKeys.user(userId, 'mailbox', messageId),
  inviteLookup: (userId: string, tokenFingerprint: string): UserKey => queryKeys.user(userId, 'invite', tokenFingerprint),
  platform: (userId: string, ...rest: unknown[]): UserKey => queryKeys.user(userId, 'platform', ...rest),

  tenant: (userId: string, tenantId: string, ...rest: unknown[]): TenantKey =>
    ['u', requireId('Kullanıcı', userId), 't', requireId('Şirket', tenantId), ...rest] as const,

  workspace: (userId: string, tenantId: string): TenantKey => queryKeys.tenant(userId, tenantId, 'workspace'),
  customers: (userId: string, tenantId: string, filter?: Record<string, string>): TenantKey =>
    filter === undefined ? queryKeys.tenant(userId, tenantId, 'customers') : queryKeys.tenant(userId, tenantId, 'customers', 'list', filter),
  customer: (userId: string, tenantId: string, customerId: string): TenantKey =>
    queryKeys.tenant(userId, tenantId, 'customers', 'detail', customerId),
  tickets: (userId: string, tenantId: string, filter?: Record<string, string>): TenantKey =>
    filter === undefined ? queryKeys.tenant(userId, tenantId, 'tickets') : queryKeys.tenant(userId, tenantId, 'tickets', 'list', filter),
  ticket: (userId: string, tenantId: string, ticketId: string): TenantKey => queryKeys.tenant(userId, tenantId, 'tickets', 'detail', ticketId),
  timeline: (userId: string, tenantId: string, ticketId: string): TenantKey =>
    queryKeys.tenant(userId, tenantId, 'tickets', 'timeline', ticketId),
  assignable: (userId: string, tenantId: string): TenantKey => queryKeys.tenant(userId, tenantId, 'assignable-members'),
  members: (userId: string, tenantId: string): TenantKey => queryKeys.tenant(userId, tenantId, 'members'),
  invites: (userId: string, tenantId: string): TenantKey => queryKeys.tenant(userId, tenantId, 'invites'),
  offers: (userId: string, tenantId: string): TenantKey => queryKeys.tenant(userId, tenantId, 'ownership-offers'),
  billing: (userId: string, tenantId: string): TenantKey => queryKeys.tenant(userId, tenantId, 'billing'),
  billingOperation: (userId: string, tenantId: string, operationId: string): TenantKey =>
    queryKeys.tenant(userId, tenantId, 'billing', 'operation', operationId),
  audit: (userId: string, tenantId: string): TenantKey => queryKeys.tenant(userId, tenantId, 'audit'),
  exports: (userId: string, tenantId: string): TenantKey => queryKeys.tenant(userId, tenantId, 'exports'),
};

/** Anahtar bir şirkete aitse o şirketin kimliğini döndürür. */
export function tenantOfKey(key: QueryKey): string | null {
  return key[0] === 'u' && typeof key[1] === 'string' && key[2] === 't' && typeof key[3] === 'string' ? key[3] : null;
}

/** Anahtar bir kullanıcıya aitse o kullanıcının kimliğini döndürür. */
export function userOfKey(key: QueryKey): string | null {
  return key[0] === 'u' && typeof key[1] === 'string' ? key[1] : null;
}

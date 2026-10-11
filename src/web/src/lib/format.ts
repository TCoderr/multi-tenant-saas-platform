// Biçimlendirme ve Türkçe etiketler. Sunucu enum adları arayüzde yalnız buradan metne çevrilir.

import type {
  AccessMode,
  BillingOperationStatus,
  ExportStatus,
  InviteState,
  PermissionCode,
  SubscriptionStatus,
  TenantRole,
  TenantStatus,
  TicketAction,
  TicketPriority,
  TicketStatus,
} from '../api/types';

const LOCALE = 'tr-TR';

const dateTimeFormat = new Intl.DateTimeFormat(LOCALE, { dateStyle: 'medium', timeStyle: 'short' });
const dateFormat = new Intl.DateTimeFormat(LOCALE, { dateStyle: 'long' });
const numberFormat = new Intl.NumberFormat(LOCALE);

export function formatDateTime(value: string | null | undefined): string {
  if (value === null || value === undefined) {
    return '—';
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : dateTimeFormat.format(date);
}

export function formatDate(value: string | null | undefined): string {
  if (value === null || value === undefined) {
    return '—';
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : dateFormat.format(date);
}

export function formatNumber(value: number): string {
  return numberFormat.format(value);
}

/** Tutar kuruş (minor) olarak gelir. */
export function formatMoney(amountMinor: number, currency: string): string {
  return new Intl.NumberFormat(LOCALE, { style: 'currency', currency, maximumFractionDigits: amountMinor % 100 === 0 ? 0 : 2 }).format(
    amountMinor / 100,
  );
}

export function formatBytes(bytes: number | null): string {
  if (bytes === null) {
    return '—';
  }
  if (bytes < 1024) {
    return `${formatNumber(bytes)} B`;
  }
  if (bytes < 1024 * 1024) {
    return `${formatNumber(Math.round(bytes / 102.4) / 10)} KB`;
  }
  return `${formatNumber(Math.round(bytes / (1024 * 102.4)) / 10)} MB`;
}

export function shortId(id: string | null | undefined): string {
  return id === null || id === undefined ? '—' : id.slice(0, 8);
}

/** Kulvar şeridi tonu: şirket kimliğinden türetilir (0–5). Aynı şirket her oturumda aynı şeridi alır. */
export function laneIndex(tenantId: string): number {
  let hash = 0;
  for (let i = 0; i < tenantId.length; i += 1) {
    hash = (hash * 31 + tenantId.charCodeAt(i)) >>> 0;
  }
  return hash % 6;
}

export function monogram(name: string): string {
  const words = name
    .trim()
    .split(/\s+/)
    .filter((word) => word.length > 0);
  const first = words[0] ?? '';
  const second = words[1] ?? '';
  const letters = second.length > 0 ? [...first][0]! + [...second][0]! : [...first].slice(0, 2).join('');
  return letters.toLocaleUpperCase(LOCALE);
}

export const roleLabel: Record<TenantRole, string> = { Owner: 'Owner', Admin: 'Admin', Agent: 'Agent', Viewer: 'Viewer' };

export const roleDescription: Record<TenantRole, string> = {
  Owner: 'Şirketin sahibi: plan, silme ve tüm ayarlar',
  Admin: 'Ekip ve içerik yönetimi',
  Agent: 'Müşteri ve talep işleri',
  Viewer: 'Yalnız okuma; kişisel veriler maskeli',
};

export const tenantStatusLabel: Record<TenantStatus, string> = {
  Active: 'Etkin',
  Suspended: 'Askıda',
  DeletionPending: 'Silme bekliyor',
  Purging: 'Siliniyor',
  Deleted: 'Silindi',
};

export const subscriptionStatusLabel: Record<SubscriptionStatus, string> = {
  Trialing: 'Deneme',
  Active: 'Etkin',
  PastDue: 'Ödeme gecikti',
  Suspended: 'Askıda',
  Cancelled: 'İptal edildi',
};

export const accessModeLabel: Record<AccessMode, string> = {
  Full: 'Tam erişim',
  Recovery: 'Salt-okunur (abonelik)',
  ReadOnly: 'Salt-okunur (silme bekliyor)',
  Locked: 'Kilitli',
};

export const ticketStatusLabel: Record<TicketStatus, string> = {
  Open: 'Açık',
  InProgress: 'İşlemde',
  Resolved: 'Çözüldü',
  Closed: 'Kapalı',
};

export const ticketPriorityLabel: Record<TicketPriority, string> = { Low: 'Düşük', Normal: 'Normal', High: 'Yüksek', Urgent: 'Acil' };

export const ticketActionLabel: Record<TicketAction, string> = {
  Start: 'İşleme al',
  Release: 'Bırak',
  Resolve: 'Çözüldü olarak işaretle',
  Close: 'Kapat',
  Reopen: 'Yeniden aç',
};

export const inviteStateLabel: Record<InviteState, string> = {
  Sending: 'Gönderiliyor',
  Pending: 'Bekliyor',
  Expired: 'Süresi doldu',
  Accepted: 'Kabul edildi',
  Revoked: 'İptal edildi',
};

export const exportStatusLabel: Record<ExportStatus, string> = {
  Queued: 'Sırada',
  Running: 'Hazırlanıyor',
  Ready: 'Hazır',
  Failed: 'Başarısız',
  Expired: 'Süresi doldu',
  Cancelled: 'İptal edildi',
};

export const operationStatusLabel: Record<BillingOperationStatus, string> = {
  Pending: 'Sonuç bekleniyor',
  Unknown: 'Sonuç belirsiz, doğrulanıyor',
  Succeeded: 'Uygulandı',
  Declined: 'Ödeme reddedildi',
  RefundPending: 'İade bekleniyor',
  Refunded: 'İade edildi',
  Voided: 'İptal edildi (ücret alınmadı)',
  ManuallyClosed: 'Operatör kapattı',
  NeedsResolution: 'Operatör incelemesi bekliyor',
};

export type Tone = 'ok' | 'warn' | 'critical' | 'info' | 'muted' | 'plain';

export const ticketStatusTone: Record<TicketStatus, Tone> = { Open: 'info', InProgress: 'warn', Resolved: 'ok', Closed: 'muted' };
export const ticketPriorityTone: Record<TicketPriority, Tone> = { Low: 'muted', Normal: 'plain', High: 'warn', Urgent: 'critical' };
export const subscriptionStatusTone: Record<SubscriptionStatus, Tone> = {
  Trialing: 'info',
  Active: 'ok',
  PastDue: 'warn',
  Suspended: 'critical',
  Cancelled: 'critical',
};
export const tenantStatusTone: Record<TenantStatus, Tone> = {
  Active: 'ok',
  Suspended: 'critical',
  DeletionPending: 'critical',
  Purging: 'critical',
  Deleted: 'muted',
};
export const exportStatusTone: Record<ExportStatus, Tone> = {
  Queued: 'info',
  Running: 'info',
  Ready: 'ok',
  Failed: 'critical',
  Expired: 'muted',
  Cancelled: 'muted',
};
export const operationStatusTone: Record<BillingOperationStatus, Tone> = {
  Pending: 'info',
  Unknown: 'warn',
  Succeeded: 'ok',
  Declined: 'critical',
  RefundPending: 'warn',
  Refunded: 'muted',
  Voided: 'muted',
  ManuallyClosed: 'muted',
  NeedsResolution: 'warn',
};
export const inviteStateTone: Record<InviteState, Tone> = {
  Sending: 'info',
  Pending: 'warn',
  Expired: 'muted',
  Accepted: 'ok',
  Revoked: 'muted',
};

/** Yalnız açıklama metni içindir; yetkiyi sunucu belirler ve `permissions` listesiyle bildirir. */
const permissionRoles: Record<PermissionCode, string> = {
  'workspace.read': 'şirket üyeliği',
  'members.leave': 'şirket üyeliği',
  'tenant.settings': 'Owner rolü',
  'tenant.delete': 'Owner rolü',
  'tenant.delete.cancel': 'Owner rolü',
  'customers.read': 'şirket üyeliği',
  'customers.pii': 'Owner, Admin veya Agent rolü',
  'customers.write': 'Owner, Admin veya Agent rolü',
  'customers.delete': 'Owner veya Admin rolü',
  'tickets.read': 'şirket üyeliği',
  'tickets.write': 'Owner, Admin veya Agent rolü',
  'members.read': 'şirket üyeliği',
  'members.pii': 'Owner veya Admin rolü',
  'members.invite': 'Owner veya Admin rolü',
  'members.manage': 'Owner veya Admin rolü',
  'ownership.offer': 'Owner rolü',
  'ownership.accept': 'şirket üyeliği',
  'billing.read': 'Owner veya Admin rolü',
  'billing.manage': 'Owner rolü',
  'audit.read': 'Owner veya Admin rolü',
  'export.manage': 'Owner rolü',
};

const permissionRoleSets: Record<PermissionCode, readonly TenantRole[]> = {
  'workspace.read': ['Owner', 'Admin', 'Agent', 'Viewer'],
  'members.leave': ['Owner', 'Admin', 'Agent', 'Viewer'],
  'tenant.settings': ['Owner'],
  'tenant.delete': ['Owner'],
  'tenant.delete.cancel': ['Owner'],
  'customers.read': ['Owner', 'Admin', 'Agent', 'Viewer'],
  'customers.pii': ['Owner', 'Admin', 'Agent'],
  'customers.write': ['Owner', 'Admin', 'Agent'],
  'customers.delete': ['Owner', 'Admin'],
  'tickets.read': ['Owner', 'Admin', 'Agent', 'Viewer'],
  'tickets.write': ['Owner', 'Admin', 'Agent'],
  'members.read': ['Owner', 'Admin', 'Agent', 'Viewer'],
  'members.pii': ['Owner', 'Admin'],
  'members.invite': ['Owner', 'Admin'],
  'members.manage': ['Owner', 'Admin'],
  'ownership.offer': ['Owner'],
  'ownership.accept': ['Owner', 'Admin', 'Agent', 'Viewer'],
  'billing.read': ['Owner', 'Admin'],
  'billing.manage': ['Owner'],
  'audit.read': ['Owner', 'Admin'],
  'export.manage': ['Owner'],
};

export function isPermissionCode(value: unknown): value is PermissionCode {
  return typeof value === 'string' && value in permissionRoles;
}

export function permissionRequirement(permission: string): string {
  return isPermissionCode(permission) ? permissionRoles[permission] : `"${permission}" yetkisi`;
}

/**
 * Bir eylemin neden kapalı olduğunu söyler. Rol yetmiyorsa gereken rolü, rol yetiyor ama şirket salt-okunur/kısıtlı ise
 * o durumu anlatır.
 */
export function deniedReason(permission: PermissionCode, role: TenantRole, mode: AccessMode): string {
  if (!permissionRoleSets[permission].includes(role)) {
    return `Bu işlem için ${permissionRoles[permission]} gerekir. Rolünüz: ${roleLabel[role]}.`;
  }
  switch (mode) {
    case 'Recovery':
      return 'Abonelik askıda veya iptal edilmiş: içerik salt-okunur. Yazmaya devam etmek için bir plan seçilmelidir.';
    case 'ReadOnly':
      return 'Şirket için silme isteği bekliyor: içerik salt-okunur.';
    case 'Locked':
      return 'Şirket platform tarafından askıya alındı.';
    default:
      return `Bu işlem için ${permissionRoles[permission]} gerekir.`;
  }
}

const auditActionLabels: Record<string, string> = {
  'tenant.created': 'Şirket oluşturuldu',
  'tenant.renamed': 'Şirket adı değişti',
  'tenant.deletion_requested': 'Şirket silme isteği verildi',
  'tenant.deletion_cancelled': 'Şirket silme isteği iptal edildi',
  'tenant.suspended': 'Şirket askıya alındı',
  'tenant.reactivated': 'Şirket yeniden etkinleştirildi',
  'customer.created': 'Müşteri eklendi',
  'customer.updated': 'Müşteri güncellendi',
  'customer.deleted': 'Müşteri silindi',
  'ticket.created': 'Talep açıldı',
  'ticket.edited': 'Talep düzenlendi',
  'ticket.status_changed': 'Talep durumu değişti',
  'ticket.assigned': 'Talep ataması değişti',
  'tickets.unassigned_on_member_removal': 'Ayrılan üyenin talepleri boşa alındı',
  'member.role_changed': 'Üye rolü değişti',
  'member.removed': 'Üye kaldırıldı',
  'member.left': 'Üye ayrıldı',
  'invite.created': 'Davet gönderildi',
  'invite.revoked': 'Davet iptal edildi',
  'invite.accepted': 'Davet kabul edildi',
  'ownership.offered': 'Sahiplik teklif edildi',
  'ownership.transferred': 'Sahiplik devredildi',
  'ownership.shared': 'Sahiplik paylaşıldı',
  'ownership.offer_withdrawn': 'Sahiplik teklifi geri çekildi',
  'billing.plan_change_requested': 'Plan değişikliği istendi',
  'billing.plan_applied': 'Plan uygulandı',
  'billing.payment_declined': 'Ödeme reddedildi',
  'billing.subscription_cancelled': 'Abonelik iptal edildi',
  'billing.operation_resolved': 'Ödeme işlemi çözüldü',
  'export.requested': 'Dışa aktarma istendi',
  'export.downloaded': 'Dışa aktarma indirildi',
  'job.replayed': 'Ölü iş yeniden kuyruğa alındı',
  'simulation.provider_event_requested': 'Simülasyon: sağlayıcı olayı istendi',
};

export function auditActionLabel(action: string): string {
  return auditActionLabels[action] ?? action;
}

function isTicketStatus(value: unknown): value is TicketStatus {
  return typeof value === 'string' && value in ticketStatusLabel;
}

/** Zaman çizelgesindeki denetim olayını cümleye çevirir. */
export function timelineEventText(action: string | null | undefined, data: Record<string, unknown> | null | undefined): string {
  if (action === 'ticket.status_changed' && data) {
    const from = isTicketStatus(data.from) ? ticketStatusLabel[data.from] : null;
    const to = isTicketStatus(data.to) ? ticketStatusLabel[data.to] : null;
    if (from !== null && to !== null) {
      return `Durum değişti: ${from} → ${to}`;
    }
  }
  if (action === 'ticket.assigned' && data) {
    return data.to === null || data.to === undefined ? 'Atama kaldırıldı' : 'Atama değişti';
  }
  return auditActionLabel(action ?? 'olay');
}

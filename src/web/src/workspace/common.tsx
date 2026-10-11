// Çalışma alanı ekranlarının ortak parçaları: başlık (şirket adıyla), yetki durumu, kota ve çakışma kutuları.

import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { ApiError } from '../api/client';
import type { PermissionCode } from '../api/types';
import { Button, Note, PageHeader, type PageHeaderProps } from '../components/ui';
import { formatDateTime, permissionRequirement, roleLabel } from '../lib/format';
import { useWorkspace } from './context';

/** Sayfa başlığı: şirket adı hem başlığın üstünde hem belge başlığında (document.title) durur. */
export function WsHeader(props: Omit<PageHeaderProps, 'context'>) {
  const { summary } = useWorkspace();
  return <PageHeader {...props} context={summary.tenant.name} />;
}

export function Forbidden({ permission }: { permission: PermissionCode }) {
  const { summary, denied } = useWorkspace();
  return (
    <div className="state" role="alert">
      <p className="state__title">Bu içerik için yetkiniz yok</p>
      <p>
        Gereken yetki: {permissionRequirement(permission)}. Rolünüz: {roleLabel[summary.role]}.
      </p>
      {summary.accessMode !== 'Full' && <p>{denied(permission)}</p>}
    </div>
  );
}

/** Sunucuya gitmeden önce bilinen yetkiyi denetler; karar yine sunucunundur (403 ayrıca ele alınır). */
export function RequirePermission({ permission, title, children }: { permission: PermissionCode; title: string; children: ReactNode }) {
  const { can } = useWorkspace();
  if (!can(permission)) {
    return (
      <>
        <WsHeader title={title} />
        <Forbidden permission={permission} />
      </>
    );
  }
  return <>{children}</>;
}

export interface QuotaInfo {
  metric: 'tickets_created' | 'seats';
  limit: number;
  used: number;
  resetsAt: string | null;
}

/** 403 quota_exceeded yanıtından kota bilgisini çıkarır (hız sınırı 429 değildir). */
export function quotaFromError(error: unknown): QuotaInfo | null {
  if (!(error instanceof ApiError) || error.code !== 'quota_exceeded') {
    return null;
  }
  const { metric, limit, used, resetsAt } = error.extra;
  if ((metric !== 'tickets_created' && metric !== 'seats') || typeof limit !== 'number' || typeof used !== 'number') {
    return null;
  }
  return { metric, limit, used, resetsAt: typeof resetsAt === 'string' ? resetsAt : null };
}

export function QuotaNote({ quota }: { quota: QuotaInfo }) {
  const { summary, tenantId } = useWorkspace();
  const isOwner = summary.role === 'Owner';
  return (
    <Note tone="warn" title={quota.metric === 'seats' ? 'Koltuk limiti dolu' : 'Bu dönemin talep kotası doldu'} role="alert">
      {quota.metric === 'seats' ? (
        <p>
          Kullanılan koltuk: <strong className="num">{quota.used}</strong> / <span className="num">{quota.limit}</span>. Yeni üye eklemek için
          plan yükseltilmeli ya da bir koltuk boşaltılmalıdır.
        </p>
      ) : (
        <p>
          Kullanılan: <strong className="num">{quota.used}</strong> / <span className="num">{quota.limit}</span> talep.
          {quota.resetsAt !== null && <> Kota {formatDateTime(quota.resetsAt)} tarihinde sıfırlanır.</>}
        </p>
      )}
      <p>
        {isOwner ? <Link to={`/t/${tenantId}/plan`}>Planı yükselt</Link> : 'Planı yalnız Owner değiştirebilir; şirket sahibinize haber verin.'}
      </p>
    </Note>
  );
}

export interface ConflictNoteProps {
  busy?: boolean;
  onReload: () => void;
  /** Verilmezse yalnız "güncel sürümü yükle" sunulur (korunacak metin yoksa). */
  onKeepMine?: () => void;
}

export function ConflictNote({ busy, onReload, onKeepMine }: ConflictNoteProps) {
  return (
    <Note tone="warn" title="Bu kayıt siz düzenlerken değişti" role="alert">
      <p>Başka biri aynı kaydı güncelledi; değişikliğiniz kaydedilmedi. Nasıl devam etmek istersiniz?</p>
      <div className="row">
        <Button onClick={onReload} busy={busy}>
          Güncel sürümü yükle
        </Button>
        {onKeepMine !== undefined && (
          <Button onClick={onKeepMine} busy={busy}>
            Kendi metnimi koru
          </Button>
        )}
      </div>
    </Note>
  );
}

export function isConflict(error: unknown): boolean {
  return error instanceof ApiError && error.status === 412;
}

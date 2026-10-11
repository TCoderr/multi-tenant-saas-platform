// Platform operatörü: şirket metadata'sı, askıya alma, çözüm bekleyen ödeme işlemleri, ölü işler, simülasyon ve platform denetimi.
// Operatör müşteri/talep içeriğini göremez: bu uçlar içerik döndürmez ve veritabanı rolünün içerik tablolarında yetkisi yoktur.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { ApiError, get, request } from '../api/client';
import { queryKeys } from '../api/queryKeys';
import type { DeadJob, Items, PlatformAuditEvent, PlatformOperation, PlatformTenant } from '../api/types';
import { useFormState } from '../components/form';
import { useNotice } from '../components/notice';
import {
  Badge,
  Button,
  DataList,
  Dialog,
  EmptyState,
  ErrorState,
  ErrorSummary,
  Field,
  Loading,
  Note,
  PageHeader,
  problemText,
  type Column,
} from '../components/ui';
import {
  auditActionLabel,
  formatDate,
  formatDateTime,
  formatMoney,
  operationStatusLabel,
  operationStatusTone,
  shortId,
  subscriptionStatusLabel,
  tenantStatusLabel,
  tenantStatusTone,
} from '../lib/format';
import { useUser } from '../session/SessionProvider';

function usePlatformKeys() {
  const user = useUser();
  return {
    tenants: queryKeys.platform(user.id, 'tenants'),
    operations: queryKeys.platform(user.id, 'operations'),
    deadJobs: queryKeys.platform(user.id, 'dead-jobs'),
    audit: queryKeys.platform(user.id, 'audit'),
  };
}

function TenantsSection({ tenants }: { tenants: PlatformTenant[] }) {
  const keys = usePlatformKeys();
  const queryClient = useQueryClient();
  const notify = useNotice();
  const [target, setTarget] = useState<{ tenant: PlatformTenant; action: 'suspend' | 'reactivate' } | null>(null);
  const form = useFormState({ reason: '' });

  const act = useMutation({
    mutationFn: (input: { id: string; action: 'suspend' | 'reactivate'; reason: string }) =>
      request<{ status: string }>(`/api/platform/tenants/${input.id}/${input.action}`, { method: 'POST', body: { reason: input.reason } }),
    onSuccess: (_, input) => {
      void queryClient.invalidateQueries({ queryKey: keys.tenants });
      void queryClient.invalidateQueries({ queryKey: keys.audit });
      notify(input.action === 'suspend' ? 'Şirket askıya alındı.' : 'Şirket yeniden etkinleştirildi.');
      close();
    },
    onError: (error) => form.failWith(error),
  });

  const close = () => {
    setTarget(null);
    form.setValues({ reason: '' });
    form.clear();
  };

  const submit = () => {
    if (target === null) {
      return;
    }
    const reason = form.values.reason.trim();
    if (reason.length < 5) {
      form.fail('İşlem gönderilmedi.', { reason: 'Gerekçe en az 5 karakter olmalıdır.' });
      return;
    }
    form.clear();
    act.mutate({ id: target.tenant.id, action: target.action, reason });
  };

  const columns: Column<PlatformTenant>[] = [
    { key: 'name', header: 'Şirket', rowHeader: true, cell: (tenant) => tenant.name },
    {
      key: 'status',
      header: 'Durum',
      cell: (tenant) => (
        <>
          <Badge tone={tenantStatusTone[tenant.status]}>{tenantStatusLabel[tenant.status]}</Badge>
          {tenant.suspendedReason !== null && <div className="small muted break">Gerekçe: {tenant.suspendedReason}</div>}
          {tenant.purgeAfter !== null && <div className="small muted">Kalıcı silme: {formatDate(tenant.purgeAfter)}</div>}
        </>
      ),
    },
    {
      key: 'plan',
      header: 'Plan',
      cell: (tenant) =>
        tenant.planName !== null ? `${tenant.planName}${tenant.subscriptionStatus !== null ? ` · ${subscriptionStatusLabel[tenant.subscriptionStatus]}` : ''}` : '—',
    },
    { key: 'created', header: 'Oluşturma', cell: (tenant) => <span className="num">{formatDateTime(tenant.createdAt)}</span> },
    {
      key: 'action',
      header: 'İşlem',
      cell: (tenant) =>
        tenant.status === 'Active' ? (
          <Button small variant="danger" onClick={() => setTarget({ tenant, action: 'suspend' })} ariaLabel={`${tenant.name} şirketini askıya al`}>
            Askıya al
          </Button>
        ) : tenant.status === 'Suspended' ? (
          <Button small onClick={() => setTarget({ tenant, action: 'reactivate' })} ariaLabel={`${tenant.name} şirketini yeniden etkinleştir`}>
            Yeniden etkinleştir
          </Button>
        ) : (
          <span className="muted">—</span>
        ),
    },
  ];

  return (
    <section className="stack" aria-labelledby="platform-tenants">
      <h2 id="platform-tenants">Şirketler</h2>
      {tenants.length === 0 ? <EmptyState title="Henüz şirket yok." /> : <DataList caption="Şirketler" columns={columns} rows={tenants} rowKey={(tenant) => tenant.id} />}
      <Dialog
        open={target !== null}
        title={target?.action === 'suspend' ? 'Şirketi askıya al' : 'Şirketi yeniden etkinleştir'}
        confirmLabel={target?.action === 'suspend' ? 'Askıya al' : 'Yeniden etkinleştir'}
        danger={target?.action === 'suspend'}
        busy={act.isPending}
        onClose={close}
        onSubmit={submit}
      >
        <div id={form.formId} className="stack">
          <p>
            <strong>{target?.tenant.name}</strong>{' '}
            {target?.action === 'suspend'
              ? 'askıya alınacak: üyeler şirketin içeriğine erişemez. Veriler silinmez.'
              : 'yeniden etkinleştirilecek: üyeler içeriğe yeniden erişir.'}
          </p>
          {form.summary !== null && <ErrorSummary title={form.summary} />}
          <Field
            label="Gerekçe"
            name="reason"
            as="textarea"
            rows={3}
            required
            maxLength={500}
            hint="Platform denetim kaydına yazılır; askı gerekçesi şirket üyelerine gösterilir. Kişisel veri yazmayın."
            value={form.values.reason}
            onChange={form.set('reason')}
            error={form.errors.reason}
          />
        </div>
      </Dialog>
    </section>
  );
}

function OperationsSection({ tenantNames }: { tenantNames: Map<string, string> }) {
  const keys = usePlatformKeys();
  const queryClient = useQueryClient();
  const notify = useNotice();
  const [target, setTarget] = useState<PlatformOperation | null>(null);
  const form = useFormState({ action: 'refund', note: '' });

  const operations = useQuery({
    queryKey: keys.operations,
    queryFn: ({ signal }) => get<Items<PlatformOperation>>('/api/platform/billing/operations?status=NeedsResolution', signal),
  });

  const resolve = useMutation({
    mutationFn: (input: { id: string; action: string; note: string }) =>
      request<unknown>(`/api/platform/billing/operations/${input.id}/resolve`, { method: 'POST', body: { action: input.action, note: input.note } }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: keys.operations });
      void queryClient.invalidateQueries({ queryKey: keys.audit });
      notify('İşlem çözüldü.');
      close();
    },
    onError: (error) => form.failWith(error),
  });

  const close = () => {
    setTarget(null);
    form.setValues({ action: 'refund', note: '' });
    form.clear();
  };

  const submit = () => {
    if (target === null) {
      return;
    }
    const note = form.values.note.trim();
    if (note.length < 10) {
      form.fail('Çözüm gönderilmedi.', { note: 'Not en az 10 karakter olmalıdır.' });
      return;
    }
    form.clear();
    resolve.mutate({ id: target.operationId, action: form.values.action, note });
  };

  const columns: Column<PlatformOperation>[] = [
    { key: 'id', header: 'İşlem', rowHeader: true, cell: (operation) => <span className="num">{shortId(operation.operationId)}</span> },
    { key: 'tenant', header: 'Şirket', cell: (operation) => tenantNames.get(operation.tenantId) ?? shortId(operation.tenantId) },
    { key: 'amount', header: 'Tutar', cell: (operation) => <span className="num">{formatMoney(operation.amountMinor, operation.currency)}</span> },
    { key: 'status', header: 'Durum', cell: (operation) => <Badge tone={operationStatusTone[operation.status]}>{operationStatusLabel[operation.status]}</Badge> },
    { key: 'attempts', header: 'Deneme', cell: (operation) => <span className="num">{operation.attempts}</span> },
    { key: 'failure', header: 'Hata kodu', cell: (operation) => operation.failureCode ?? '—' },
    { key: 'updated', header: 'Güncelleme', cell: (operation) => <span className="num">{formatDateTime(operation.updatedAt)}</span> },
    {
      key: 'action',
      header: 'İşlem',
      cell: (operation) => (
        <Button small onClick={() => setTarget(operation)} ariaLabel={`${shortId(operation.operationId)} işlemini çöz`}>
          Çöz
        </Button>
      ),
    },
  ];

  return (
    <section className="section stack" aria-labelledby="platform-operations">
      <h2 id="platform-operations">Çözüm bekleyen ödeme işlemleri</h2>
      {operations.isPending ? (
        <Loading label="İşlemler yükleniyor…" />
      ) : operations.isError ? (
        <ErrorState error={operations.error} onRetry={() => void operations.refetch()} />
      ) : operations.data.items.length === 0 ? (
        <EmptyState title="Çözüm bekleyen işlem yok." />
      ) : (
        <DataList caption="Çözüm bekleyen ödeme işlemleri" columns={columns} rows={operations.data.items} rowKey={(operation) => operation.operationId} />
      )}
      <Dialog open={target !== null} title="Ödeme işlemini çöz" confirmLabel="Çözümü kaydet" busy={resolve.isPending} onClose={close} onSubmit={submit}>
        <div id={form.formId} className="stack">
          <p>
            İşlem <span className="num">{shortId(target?.operationId)}</span>: sonucu kendiliğinden belirlenemedi. Sağlayıcı kaydını denetleyip
            kararı seçin.
          </p>
          {form.summary !== null && <ErrorSummary title={form.summary} />}
          <Field label="Karar" name="action" as="select" value={form.values.action} onChange={form.set('action')} error={form.errors.action}>
            <option value="refund">İade et (ücret alınmış)</option>
            <option value="void">İptal et (ücret alınmamış)</option>
            <option value="close">Elle kapat</option>
          </Field>
          <Field
            label="Not"
            name="note"
            as="textarea"
            rows={3}
            required
            maxLength={500}
            hint="En az 10 karakter. Platform denetim kaydına yazılır."
            value={form.values.note}
            onChange={form.set('note')}
            error={form.errors.note}
          />
        </div>
      </Dialog>
    </section>
  );
}

function DeadJobsSection({ tenantNames }: { tenantNames: Map<string, string> }) {
  const keys = usePlatformKeys();
  const queryClient = useQueryClient();
  const notify = useNotice();
  const jobs = useQuery({ queryKey: keys.deadJobs, queryFn: ({ signal }) => get<Items<DeadJob>>('/api/platform/jobs/dead', signal) });
  const replay = useMutation({
    mutationFn: (id: string) => request<void>(`/api/platform/jobs/${id}/replay`, { method: 'POST' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: keys.deadJobs });
      void queryClient.invalidateQueries({ queryKey: keys.audit });
      notify('İş yeniden kuyruğa alındı.');
    },
  });

  const columns: Column<DeadJob>[] = [
    { key: 'type', header: 'Tür', rowHeader: true, cell: (job) => <span className="break">{job.type}</span> },
    { key: 'tenant', header: 'Şirket', cell: (job) => (job.tenantId === null ? '—' : (tenantNames.get(job.tenantId) ?? shortId(job.tenantId))) },
    { key: 'attempts', header: 'Deneme', cell: (job) => <span className="num">{job.attempts}</span> },
    { key: 'error', header: 'Son hata', cell: (job) => <span className="break">{job.lastError ?? '—'}</span> },
    { key: 'created', header: 'Oluşturma', cell: (job) => <span className="num">{formatDateTime(job.createdAt)}</span> },
    {
      key: 'action',
      header: 'İşlem',
      cell: (job) => (
        <Button small busy={replay.isPending && replay.variables === job.id} onClick={() => replay.mutate(job.id)} ariaLabel={`${job.type} işini yeniden kuyruğa al`}>
          Yeniden kuyruğa al
        </Button>
      ),
    },
  ];

  return (
    <section className="section stack" aria-labelledby="platform-jobs">
      <h2 id="platform-jobs">Ölü işler</h2>
      <p className="muted">Tüm denemeleri tükenen arka plan işleri. İş gövdesi gösterilmez; yalnız tür, deneme sayısı ve hata sınıfı.</p>
      {replay.isError && <ErrorSummary title={problemText(replay.error)} />}
      {jobs.isPending ? (
        <Loading label="Ölü işler yükleniyor…" />
      ) : jobs.isError ? (
        <ErrorState error={jobs.error} onRetry={() => void jobs.refetch()} />
      ) : jobs.data.items.length === 0 ? (
        <EmptyState title="Ölü iş yok." />
      ) : (
        <DataList caption="Ölü işler" columns={columns} rows={jobs.data.items} rowKey={(job) => job.id} />
      )}
    </section>
  );
}

function SimulationSection({ tenants }: { tenants: PlatformTenant[] }) {
  const keys = usePlatformKeys();
  const queryClient = useQueryClient();
  const [tenantId, setTenantId] = useState('');
  const [kind, setKind] = useState('renewal_failed');
  const send = useMutation({
    mutationFn: () => request<void>('/api/platform/simulation/provider-event', { method: 'POST', body: { tenantId, kind } }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: keys.audit }),
  });
  const disabled = send.error instanceof ApiError && send.error.status === 404;

  return (
    <section className="section stack" aria-labelledby="platform-simulation">
      <h2 id="platform-simulation">Simülasyon: sağlayıcı olayı</h2>
      <p className="muted">Demo ödeme sağlayıcısından imzalı bir abonelik olayı gönderilmesini ister. Yalnız simülasyonu açık ortamlarda çalışır.</p>
      <form
        className="form"
        onSubmit={(event: FormEvent) => {
          event.preventDefault();
          if (tenantId.length > 0 && !send.isPending) {
            send.mutate();
          }
        }}
      >
        <Field label="Şirket" name="tenantId" as="select" required value={tenantId} onChange={setTenantId}>
          <option value="">Şirket seçin</option>
          {tenants.map((tenant) => (
            <option key={tenant.id} value={tenant.id}>
              {tenant.name}
            </option>
          ))}
        </Field>
        <Field label="Olay" name="kind" as="select" value={kind} onChange={setKind}>
          <option value="renewal_failed">Yenileme ödemesi alınamadı</option>
          <option value="renewed">Abonelik yenilendi</option>
        </Field>
        {send.isSuccess && (
          <Note tone="ok" role="status">
            <p>Olay isteği alındı; webhook ile işlenecek.</p>
          </Note>
        )}
        {send.isError && <ErrorSummary title={disabled ? 'Simülasyon bu ortamda kapalı.' : problemText(send.error)} />}
        <div className="row row--top">
          <Button type="submit" busy={send.isPending} disabledReason={tenantId.length === 0 ? 'Önce bir şirket seçin.' : null}>
            Olayı gönder
          </Button>
        </div>
      </form>
    </section>
  );
}

function PlatformAuditSection({ tenantNames }: { tenantNames: Map<string, string> }) {
  const keys = usePlatformKeys();
  const audit = useQuery({ queryKey: keys.audit, queryFn: ({ signal }) => get<Items<PlatformAuditEvent>>('/api/platform/audit?limit=50', signal) });
  const columns: Column<PlatformAuditEvent>[] = [
    { key: 'at', header: 'Zaman', cell: (event) => <span className="num">{formatDateTime(event.occurredAt)}</span> },
    { key: 'action', header: 'Olay', rowHeader: true, cell: (event) => auditActionLabel(event.action) },
    { key: 'tenant', header: 'Şirket', cell: (event) => (event.tenantId === null ? '—' : (tenantNames.get(event.tenantId) ?? shortId(event.tenantId))) },
    { key: 'actor', header: 'Operatör', cell: (event) => <span className="num">{shortId(event.actorUserId)}</span> },
  ];
  return (
    <section className="section stack" aria-labelledby="platform-audit">
      <h2 id="platform-audit">Platform denetimi</h2>
      {audit.isPending ? (
        <Loading label="Platform denetimi yükleniyor…" />
      ) : audit.isError ? (
        <ErrorState error={audit.error} onRetry={() => void audit.refetch()} />
      ) : audit.data.items.length === 0 ? (
        <EmptyState title="Henüz platform olayı yok." />
      ) : (
        <DataList caption="Platform denetimi" columns={columns} rows={audit.data.items} rowKey={(event) => event.id} />
      )}
    </section>
  );
}

export function PlatformPage() {
  const user = useUser();
  const keys = usePlatformKeys();
  const tenants = useQuery({
    queryKey: keys.tenants,
    queryFn: ({ signal }) => get<Items<PlatformTenant>>('/api/platform/tenants?limit=100', signal),
    enabled: user.isPlatformOperator,
  });

  if (!user.isPlatformOperator) {
    // Operatör olmayan için bu ekran "yok"tur (sunucu da 404 döndürür).
    return (
      <div className="full-state">
        <PageHeader title="Sayfa bulunamadı" />
        <p>Bu adreste bir sayfa yok.</p>
      </div>
    );
  }

  const items = tenants.data?.items ?? [];
  const tenantNames = new Map(items.map((tenant) => [tenant.id, tenant.name]));

  return (
    <div className="page stack-lg">
      <PageHeader title="Platform">
        <p className="muted">
          Operatör görünümü yalnız metadata içerir: şirket adı, durumu, planı ve işlem kayıtları. Müşteri, talep, yorum ve üye bilgileri bu
          ekranda yoktur ve operatör tarafından açılamaz.
        </p>
      </PageHeader>
      {tenants.isPending ? (
        <Loading label="Şirketler yükleniyor…" />
      ) : tenants.isError ? (
        <ErrorState error={tenants.error} onRetry={() => void tenants.refetch()} />
      ) : (
        <TenantsSection tenants={items} />
      )}
      <OperationsSection tenantNames={tenantNames} />
      <DeadJobsSection tenantNames={tenantNames} />
      <SimulationSection tenants={items} />
      <PlatformAuditSection tenantNames={tenantNames} />
    </div>
  );
}

// Denetim kaydı, dışa aktarma ve şirket ayarları.

import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { ApiError, get, request, withQuery } from '../api/client';
import { queryKeys } from '../api/queryKeys';
import type { AuditEvent, ExportJob, Items, Member, Page, WorkspaceSummary } from '../api/types';
import { safeInternalPath, useFormState } from '../components/form';
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
  LoadMore,
  Loading,
  Note,
  problemText,
  type Column,
} from '../components/ui';
import { auditActionLabel, exportStatusLabel, exportStatusTone, formatBytes, formatDate, formatDateTime, shortId } from '../lib/format';
import { LeaveCompany } from '../workspace/LeaveCompany';
import { ConflictNote, RequirePermission, WsHeader, isConflict } from '../workspace/common';
import { useTenantMutation, useWorkspace } from '../workspace/context';

/* ---------- Denetim ---------- */

const targetTypeLabel: Record<string, string> = {
  tenant: 'Şirket',
  customer: 'Müşteri',
  ticket: 'Talep',
  membership: 'Üyelik',
  invite: 'Davet',
  subscription: 'Abonelik',
  export: 'Dışa aktarma',
};

function AuditList() {
  const ws = useWorkspace();
  const audit = useInfiniteQuery({
    queryKey: ws.key('audit'),
    queryFn: ({ pageParam, signal }) => get<Page<AuditEvent>>(withQuery(ws.api('/audit'), { cursor: pageParam }), signal),
    initialPageParam: '',
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  // Aktör adları üye listesinden çözülür; kayıtların kendisi kişisel veri içermez (yalnız kimlikler).
  const members = useQuery({ queryKey: ws.key('members'), queryFn: ({ signal }) => get<Items<Member>>(ws.api('/members'), signal) });
  const names = new Map((members.data?.items ?? []).map((member) => [member.userId, member.displayName]));

  const columns: Column<AuditEvent>[] = [
    { key: 'at', header: 'Zaman', cell: (event) => <span className="num">{formatDateTime(event.occurredAt)}</span> },
    { key: 'action', header: 'Olay', rowHeader: true, cell: (event) => auditActionLabel(event.action) },
    {
      key: 'actor',
      header: 'Yapan',
      cell: (event) => (event.actorUserId === null ? 'Sistem' : (names.get(event.actorUserId) ?? `Eski üye (${shortId(event.actorUserId)})`)),
    },
    {
      key: 'target',
      header: 'Hedef',
      cell: (event) =>
        event.ticketId !== null ? (
          <Link to={`/t/${ws.tenantId}/tickets/${event.ticketId}`}>Talep {shortId(event.ticketId)}</Link>
        ) : event.targetType !== null ? (
          `${targetTypeLabel[event.targetType] ?? event.targetType} ${shortId(event.targetId)}`
        ) : (
          '—'
        ),
    },
  ];

  if (audit.isPending) {
    return <Loading label="Denetim kaydı yükleniyor…" />;
  }
  if (audit.isError) {
    return <ErrorState error={audit.error} onRetry={() => void audit.refetch()} />;
  }
  const rows = audit.data.pages.flatMap((page) => page.items);
  if (rows.length === 0) {
    return <EmptyState title="Henüz denetim kaydı yok." />;
  }
  return (
    <>
      <DataList caption="Denetim kaydı" columns={columns} rows={rows} rowKey={(event) => event.id} />
      <LoadMore hasMore={audit.hasNextPage} loading={audit.isFetchingNextPage} onLoad={() => void audit.fetchNextPage()} />
    </>
  );
}

export function AuditPage() {
  return (
    <RequirePermission permission="audit.read" title="Denetim">
      <div className="stack">
        <WsHeader title="Denetim" />
        <p className="muted">Şirkette kimin ne zaman ne yaptığının kaydı. Kayıtlar müşteri adı, e-posta gibi kişisel veri içermez.</p>
        <AuditList />
      </div>
    </RequirePermission>
  );
}

/* ---------- Dışa aktarma ---------- */

function ExportList() {
  const ws = useWorkspace();
  const notify = useNotice();
  const [error, setError] = useState<unknown>(null);
  const exportsKey = ws.key('exports');

  const jobs = useQuery({
    queryKey: exportsKey,
    queryFn: ({ signal }) => get<Items<ExportJob>>(ws.api('/exports'), signal),
    // Hazırlanan iş varken 2 saniyede bir yoklanır.
    refetchInterval: (query) => (query.state.data?.items.some((job) => job.status === 'Queued' || job.status === 'Running') === true ? 2000 : false),
  });

  const start = useTenantMutation<ExportJob>({
    mutationFn: async () => (await request<ExportJob>(ws.api('/exports'), { method: 'POST' })).data,
    invalidate: [exportsKey],
    onSuccess: () => {
      setError(null);
      notify('Dışa aktarma sıraya alındı.');
    },
  });

  const download = useTenantMutation<{ url: string; expiresAt: string }, string>({
    mutationFn: async (id) => (await request<{ url: string; expiresAt: string }>(ws.api(`/exports/${id}/download-link`), { method: 'POST' })).data,
    invalidate: [exportsKey],
    onSuccess: ({ url }) => {
      setError(null);
      // Kısa ömürlü, tek şirkete bağlı aynı-origin bağlantı; dosya tarayıcının indirme akışıyla alınır.
      const path = safeInternalPath(url);
      if (path !== null) {
        window.location.assign(path);
      }
    },
  });

  const planDenied = ws.summary.entitlements.exportEnabled ? null : 'Dışa aktarma planınıza dahil değil. Starter veya Team planına geçilmelidir.';
  const running = jobs.data?.items.some((job) => job.status === 'Queued' || job.status === 'Running') === true;
  const startDenied = ws.denied('export.manage') ?? planDenied ?? (running ? 'Devam eden bir dışa aktarma var.' : null);

  const columns: Column<ExportJob>[] = [
    { key: 'created', header: 'İstek zamanı', rowHeader: true, cell: (job) => <span className="num">{formatDateTime(job.createdAt)}</span> },
    {
      key: 'status',
      header: 'Durum',
      cell: (job) => (
        <>
          <Badge tone={exportStatusTone[job.status]}>{exportStatusLabel[job.status]}</Badge>
          {job.failureCode !== null && <span className="small muted"> ({job.failureCode})</span>}
        </>
      ),
    },
    { key: 'size', header: 'Boyut', cell: (job) => <span className="num">{formatBytes(job.sizeBytes)}</span> },
    { key: 'expires', header: 'Son indirme', cell: (job) => <span className="num">{formatDateTime(job.expiresAt)}</span> },
    {
      key: 'action',
      header: 'İşlem',
      cell: (job) =>
        job.status === 'Ready' ? (
          <Button
            small
            busy={download.isPending && download.variables === job.id}
            disabledReason={job.requestedByMe ? null : 'Dosyayı yalnız isteyen kişi indirebilir.'}
            onClick={() => download.mutate(job.id, { onError: setError })}
            ariaLabel={`${formatDateTime(job.createdAt)} tarihli dışa aktarmayı indir`}
          >
            İndir
          </Button>
        ) : (
          <span className="muted">—</span>
        ),
    },
  ];

  return (
    <>
      <div className="row row--top">
        <Button variant="primary" busy={start.isPending} disabledReason={startDenied} onClick={() => start.mutate(undefined, { onError: setError })}>
          Yeni dışa aktarma iste
        </Button>
        {planDenied !== null && ws.summary.role === 'Owner' && <Link to={`/t/${ws.tenantId}/plan`}>Planları gör</Link>}
      </div>
      {error !== null && <ErrorSummary title={problemText(error)} />}
      <div aria-live="polite">
        {jobs.isPending ? (
          <Loading label="Dışa aktarmalar yükleniyor…" />
        ) : jobs.isError ? (
          <ErrorState error={jobs.error} onRetry={() => void jobs.refetch()} />
        ) : jobs.data.items.length === 0 ? (
          <EmptyState title="Henüz dışa aktarma yok.">
            <p>İstek sıraya alınır ve arka planda hazırlanır; hazır olunca buradan indirilir.</p>
          </EmptyState>
        ) : (
          <DataList caption="Dışa aktarmalar" columns={columns} rows={jobs.data.items} rowKey={(job) => job.id} />
        )}
      </div>
    </>
  );
}

export function ExportsPage() {
  return (
    <RequirePermission permission="export.manage" title="Dışa aktarma">
      <div className="stack">
        <WsHeader title="Dışa aktarma" />
        <p className="muted">
          Şirketin müşteri, talep ve yorum verileri CSV dosyaları olarak tek bir ZIP içinde hazırlanır. Dosya yalnız bu şirketin verisini içerir ve
          sınırlı süre indirilebilir.
        </p>
        <ExportList />
      </div>
    </RequirePermission>
  );
}

/* ---------- Ayarlar ---------- */

function RenameForm() {
  const ws = useWorkspace();
  const queryClient = useQueryClient();
  const notify = useNotice();
  const form = useFormState({ name: ws.summary.tenant.name });
  const [baseVersion, setBaseVersion] = useState(ws.summary.tenant.version);
  const [conflict, setConflict] = useState(false);
  const [reloading, setReloading] = useState(false);
  const denied = ws.denied('tenant.settings');

  const rename = useTenantMutation<{ version: number }, string>({
    mutationFn: async (name) => (await request<{ version: number }>(ws.api('/settings'), { method: 'PATCH', body: { name }, ifMatch: baseVersion })).data,
    onSuccess: ({ version }) => {
      setBaseVersion(version);
      void queryClient.invalidateQueries({ queryKey: queryKeys.memberships(ws.userId) });
      notify('Şirket adı güncellendi.');
    },
  });

  const loadLatest = (keepMine: boolean) => {
    setReloading(true);
    get<WorkspaceSummary>(`/api/t/${ws.tenantId}`)
      .then((latest) => {
        queryClient.setQueryData(ws.key('workspace'), latest);
        setBaseVersion(latest.tenant.version);
        if (!keepMine) {
          form.setValues({ name: latest.tenant.name });
        }
        setConflict(false);
        form.clear();
      })
      .catch((error: unknown) => form.failWith(error))
      .finally(() => setReloading(false));
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (denied !== null || rename.isPending) {
      return;
    }
    const name = form.values.name.trim();
    if (name.length < 2) {
      form.fail('Ad kaydedilmedi.', { name: 'Şirket adı en az 2 karakter olmalıdır.' });
      return;
    }
    form.clear();
    rename.mutate(name, {
      onError: (error) => {
        if (isConflict(error)) {
          setConflict(true);
        } else {
          form.failWith(error);
        }
      },
    });
  };

  return (
    <form id={form.formId} className="form" onSubmit={submit} noValidate aria-label="Şirket adı">
      {conflict && <ConflictNote busy={reloading} onReload={() => loadLatest(false)} onKeepMine={() => loadLatest(true)} />}
      {form.summary !== null && <ErrorSummary title={form.summary} />}
      <Field label="Şirket adı" name="name" required maxLength={120} value={form.values.name} onChange={form.set('name')} error={form.errors.name} disabled={denied !== null} />
      <div className="row row--top">
        <Button type="submit" variant="primary" busy={rename.isPending} disabledReason={denied ?? (conflict ? 'Önce çakışmayı çözün.' : null)}>
          Adı kaydet
        </Button>
      </div>
    </form>
  );
}

function DeletionSection() {
  const ws = useWorkspace();
  const notify = useNotice();
  const [open, setOpen] = useState(false);
  const form = useFormState({ confirmName: '', password: '' });
  const pending = ws.summary.tenant.status === 'DeletionPending';

  const requestDeletion = useTenantMutation<{ purgeAfter: string }, { confirmName: string; password: string }>({
    mutationFn: async (body) => (await request<{ purgeAfter: string }>(ws.api('/deletion'), { method: 'POST', body })).data,
    onSuccess: ({ purgeAfter }) => {
      setOpen(false);
      form.setValues({ confirmName: '', password: '' });
      notify(`Silme isteği alındı. Kalıcı silme: ${formatDate(purgeAfter)}.`);
    },
  });

  const cancelDeletion = useTenantMutation<void>({
    mutationFn: async () => {
      await request<void>(ws.api('/deletion'), { method: 'DELETE' });
    },
    onSuccess: () => notify('Silme isteği iptal edildi.'),
  });

  const close = () => {
    setOpen(false);
    form.clear();
    form.setValues({ confirmName: '', password: '' });
    requestDeletion.reset();
  };

  const submit = () => {
    const fields: Record<string, string> = {};
    if (form.values.confirmName.trim() !== ws.summary.tenant.name) {
      fields.confirmName = 'Şirket adını aynen yazın.';
    }
    if (form.values.password.length === 0) {
      fields.password = 'Parolanızı girin.';
    }
    if (Object.keys(fields).length > 0) {
      form.fail('Silme isteği gönderilmedi.', fields);
      return;
    }
    form.clear();
    requestDeletion.mutate(
      { confirmName: form.values.confirmName.trim(), password: form.values.password },
      {
        onError: (error) => {
          // Parola formda tutulmaz.
          form.setValues({ confirmName: form.values.confirmName, password: '' });
          form.fail(error instanceof ApiError && error.code === 'confirmation_failed' ? 'Onay başarısız: şirket adını aynen yazın ve parolanızı girin.' : problemText(error));
        },
      },
    );
  };

  if (pending) {
    return (
      <div className="stack">
        <Note tone="critical" title="Silme bekliyor">
          <p>
            Bu şirket {formatDate(ws.summary.tenant.purgeAfter)} tarihinde tüm müşteri, talep ve üyelik verileriyle birlikte kalıcı olarak
            silinecek. O tarihe kadar içerik salt-okunurdur ve silme iptal edilebilir.
          </p>
        </Note>
        {cancelDeletion.isError && <ErrorSummary title={problemText(cancelDeletion.error)} />}
        <div className="row row--top">
          <Button variant="primary" busy={cancelDeletion.isPending} disabledReason={ws.denied('tenant.delete.cancel')} onClick={() => cancelDeletion.mutate()}>
            Silmeyi iptal et
          </Button>
        </div>
      </div>
    );
  }

  return (
    <div className="stack">
      <p className="muted">
        Silme isteği şirketi hemen salt-okunur yapar; bekleme süresi dolunca tüm veriler kalıcı olarak silinir. Süre dolmadan Owner iptal edebilir.
      </p>
      <div className="row row--top">
        <Button variant="danger" onClick={() => setOpen(true)} disabledReason={ws.denied('tenant.delete')}>
          Şirketi sil
        </Button>
      </div>
      <Dialog open={open} title="Şirketi sil" confirmLabel="Silme isteğini gönder" danger busy={requestDeletion.isPending} onClose={close} onSubmit={submit}>
        <div id={form.formId} className="stack">
          <p>
            <strong>{ws.summary.tenant.name}</strong> silinmek üzere işaretlenecek. Onaylamak için şirket adını aynen yazın ve parolanızı girin.
          </p>
          {form.summary !== null && <ErrorSummary title={form.summary} />}
          <Field label="Şirket adı" name="confirmName" required value={form.values.confirmName} onChange={form.set('confirmName')} error={form.errors.confirmName} />
          <Field
            label="Parolanız"
            name="password"
            type="password"
            required
            autoComplete="current-password"
            value={form.values.password}
            onChange={form.set('password')}
            error={form.errors.password}
          />
        </div>
      </Dialog>
    </div>
  );
}

export function SettingsPage() {
  return (
    <div className="stack-lg">
      <WsHeader title="Ayarlar" />
      <section className="stack" aria-labelledby="name-title">
        <h2 id="name-title">Şirket adı</h2>
        <RenameForm />
      </section>
      <section className="section stack" aria-labelledby="leave-title">
        <h2 id="leave-title">Şirketten ayrıl</h2>
        <p className="muted">Ayrılınca bu şirketin içeriğine erişiminiz kalkar. Şirketin tek Owner'ı ayrılamaz; önce sahipliği devretmelidir.</p>
        <div className="row row--top">
          <LeaveCompany />
        </div>
      </section>
      <section className="section stack" aria-labelledby="delete-title">
        <h2 id="delete-title">Şirketi sil</h2>
        <DeletionSection />
      </section>
    </div>
  );
}

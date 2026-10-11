// Talepler: liste + filtre, yeni talep (Idempotency-Key, kota), detay (durum geçişi, atama, düzenleme, zaman çizelgesi, yorum).

import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query';
import { useRef, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { get, newIdempotencyKey, request, withQuery } from '../api/client';
import type {
  AssignableMember,
  Customer,
  Items,
  Page,
  Ticket,
  TicketAction,
  TicketListItem,
  TicketPriority,
  TicketStatus,
  TimelineItem,
} from '../api/types';
import { useFormState } from '../components/form';
import { useNotice } from '../components/notice';
import {
  Badge,
  Button,
  DataList,
  EmptyState,
  ErrorState,
  ErrorSummary,
  Field,
  LinkButton,
  LoadMore,
  Loading,
  Note,
  problemText,
  type Column,
} from '../components/ui';
import {
  formatDateTime,
  ticketActionLabel,
  ticketPriorityLabel,
  ticketPriorityTone,
  ticketStatusLabel,
  ticketStatusTone,
  timelineEventText,
} from '../lib/format';
import { ConflictNote, QuotaNote, WsHeader, isConflict, quotaFromError, type QuotaInfo } from '../workspace/common';
import { useTenantMutation, useWorkspace } from '../workspace/context';

const STATUSES: TicketStatus[] = ['Open', 'InProgress', 'Resolved', 'Closed'];
const PRIORITIES: TicketPriority[] = ['Low', 'Normal', 'High', 'Urgent'];
const FILTER_KEYS = ['status', 'priority', 'assignee', 'q', 'customerId', 'sort'] as const;
type FilterKey = (typeof FILTER_KEYS)[number];
type Filter = Record<FilterKey, string>;

function readFilter(params: URLSearchParams): Filter {
  const status = params.get('status') ?? '';
  const priority = params.get('priority') ?? '';
  return {
    status: (STATUSES as string[]).includes(status) ? status : '',
    priority: (PRIORITIES as string[]).includes(priority) ? priority : '',
    assignee: params.get('assignee') ?? '',
    q: params.get('q') ?? '',
    customerId: params.get('customerId') ?? '',
    sort: params.get('sort') === 'updatedAt' ? 'updatedAt' : 'createdAt',
  };
}

function useAssignableMembers() {
  const ws = useWorkspace();
  return useQuery({
    queryKey: ws.key('assignable-members'),
    queryFn: ({ signal }) => get<Items<AssignableMember>>(ws.api('/assignable-members'), signal),
    staleTime: 60_000,
  });
}

export function TicketsPage() {
  const ws = useWorkspace();
  const [params, setParams] = useSearchParams();
  const filter = readFilter(params);
  const [draft, setDraft] = useState<Filter>(filter);
  const members = useAssignableMembers();

  const list = useInfiniteQuery({
    queryKey: ws.key('tickets', 'list', filter),
    queryFn: ({ pageParam, signal }) => get<Page<TicketListItem>>(withQuery(ws.api('/tickets'), { ...filter, cursor: pageParam }), signal),
    initialPageParam: '',
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });

  const apply = (event: FormEvent) => {
    event.preventDefault();
    const next = new URLSearchParams();
    for (const key of FILTER_KEYS) {
      const value = draft[key].trim();
      if (value.length > 0 && !(key === 'sort' && value === 'createdAt')) {
        next.set(key, value);
      }
    }
    setParams(next);
  };

  const clear = () => {
    setDraft(readFilter(new URLSearchParams()));
    setParams(new URLSearchParams());
  };

  const setField = (key: FilterKey) => (value: string) => setDraft((previous) => ({ ...previous, [key]: value }));
  const rows = list.data?.pages.flatMap((page) => page.items) ?? [];
  const filtered = FILTER_KEYS.some((key) => key !== 'sort' && filter[key].length > 0);

  const columns: Column<TicketListItem>[] = [
    { key: 'number', header: 'No', tight: true, cell: (row) => <span className="num">#{row.number}</span> },
    { key: 'title', header: 'Başlık', rowHeader: true, cell: (row) => <Link to={`/t/${ws.tenantId}/tickets/${row.id}`}>{row.title}</Link> },
    { key: 'customer', header: 'Müşteri', cell: (row) => <Link to={`/t/${ws.tenantId}/customers/${row.customerId}`}>{row.customerName}</Link> },
    { key: 'status', header: 'Durum', cell: (row) => <Badge tone={ticketStatusTone[row.status]}>{ticketStatusLabel[row.status]}</Badge> },
    { key: 'priority', header: 'Öncelik', cell: (row) => <Badge tone={ticketPriorityTone[row.priority]}>{ticketPriorityLabel[row.priority]}</Badge> },
    { key: 'assignee', header: 'Atanan', cell: (row) => row.assigneeName ?? <span className="muted">Atanmamış</span> },
    { key: 'updated', header: 'Güncelleme', cell: (row) => <span className="num">{formatDateTime(row.updatedAt)}</span> },
  ];

  return (
    <div className="stack">
      <WsHeader
        title="Talepler"
        actions={
          <LinkButton to={`/t/${ws.tenantId}/tickets/new`} variant="primary" disabledReason={ws.denied('tickets.write')}>
            Yeni talep
          </LinkButton>
        }
      />
      <form className="filters" role="search" aria-label="Talep filtreleri" onSubmit={apply}>
        <Field label="Durum" name="status" as="select" value={draft.status} onChange={setField('status')}>
          <option value="">Tümü</option>
          {STATUSES.map((status) => (
            <option key={status} value={status}>
              {ticketStatusLabel[status]}
            </option>
          ))}
        </Field>
        <Field label="Öncelik" name="priority" as="select" value={draft.priority} onChange={setField('priority')}>
          <option value="">Tümü</option>
          {PRIORITIES.map((priority) => (
            <option key={priority} value={priority}>
              {ticketPriorityLabel[priority]}
            </option>
          ))}
        </Field>
        <Field label="Atanan" name="assignee" as="select" value={draft.assignee} onChange={setField('assignee')}>
          <option value="">Tümü</option>
          <option value="me">Bana atananlar</option>
          <option value="none">Atanmamış</option>
          {(members.data?.items ?? []).map((member) => (
            <option key={member.userId} value={member.userId}>
              {member.displayName}
            </option>
          ))}
        </Field>
        <Field label="Sıralama" name="sort" as="select" value={draft.sort} onChange={setField('sort')}>
          <option value="createdAt">En yeni önce</option>
          <option value="updatedAt">Son güncellenen önce</option>
        </Field>
        <Field className="field--search" label="Başlıkta ara" name="q" type="search" maxLength={100} value={draft.q} onChange={setField('q')} />
        <Button type="submit">Uygula</Button>
        {filtered && <Button onClick={clear}>Filtreleri temizle</Button>}
      </form>
      {filter.customerId.length > 0 && <p className="muted small">Yalnız seçili müşterinin talepleri gösteriliyor.</p>}
      {list.isPending ? (
        <Loading label="Talepler yükleniyor…" />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} />
      ) : rows.length === 0 ? (
        filtered ? (
          <EmptyState title="Bu filtrelerle eşleşen talep yok.">
            <p>Filtreleri gevşetin ya da temizleyin.</p>
          </EmptyState>
        ) : (
          <EmptyState title="Henüz talep yok.">
            <p>İlk talebi açın; talepler bir müşteri kaydına bağlanır.</p>
          </EmptyState>
        )
      ) : (
        <>
          <DataList caption="Talepler" columns={columns} rows={rows} rowKey={(row) => row.id} />
          <LoadMore hasMore={list.hasNextPage} loading={list.isFetchingNextPage} onLoad={() => void list.fetchNextPage()} />
        </>
      )}
    </div>
  );
}

/* ---------- Yeni talep ---------- */

interface CreateTicketBody {
  customerId: string;
  title: string;
  description: string;
  priority: TicketPriority;
  assigneeUserId: string | null;
}

export function TicketNewPage() {
  const ws = useWorkspace();
  const navigate = useNavigate();
  const notify = useNotice();
  const [params] = useSearchParams();
  const presetCustomer = params.get('customerId') ?? '';
  const form = useFormState({ customerId: presetCustomer, title: '', description: '', priority: 'Normal', assigneeUserId: '' });
  const [quota, setQuota] = useState<QuotaInfo | null>(null);
  // Aynı gönderimin yeniden denemesi aynı anahtarı taşır (çift kayıt oluşmaz); içerik değişirse yeni anahtar üretilir.
  const attempt = useRef<{ key: string; payload: string } | null>(null);

  const customers = useQuery({
    queryKey: ws.key('customers', 'list', { sort: 'name', scope: 'picker' }),
    queryFn: ({ signal }) => get<Page<Customer>>(withQuery(ws.api('/customers'), { sort: 'name', limit: 100 }), signal),
  });
  const preset = useQuery({
    queryKey: ws.key('customers', 'detail', presetCustomer),
    queryFn: ({ signal }) => get<Customer>(ws.api(`/customers/${presetCustomer}`), signal),
    enabled: presetCustomer.length > 0,
  });
  const members = useAssignableMembers();

  const create = useTenantMutation<Ticket, CreateTicketBody>({
    mutationFn: async (body) => {
      const payload = JSON.stringify(body);
      if (attempt.current === null || attempt.current.payload !== payload) {
        attempt.current = { key: newIdempotencyKey(), payload };
      }
      return (await request<Ticket>(ws.api('/tickets'), { method: 'POST', body, idempotencyKey: attempt.current.key })).data;
    },
    invalidate: [ws.key('tickets')],
    onSuccess: (ticket) => {
      attempt.current = null;
      notify(`Talep #${ticket.number} açıldı.`);
      void navigate(`/t/${ws.tenantId}/tickets/${ticket.id}`);
    },
  });

  const denied = ws.denied('tickets.write');
  const usage = ws.summary.usage;
  const limit = ws.summary.entitlements.monthlyTicketLimit;
  const knownQuota: QuotaInfo | null =
    quota ?? (usage.ticketsThisPeriod >= limit ? { metric: 'tickets_created', limit, used: usage.ticketsThisPeriod, resetsAt: usage.periodResetsAt } : null);

  const options = [...(customers.data?.items ?? [])];
  if (preset.data !== undefined && !options.some((option) => option.id === preset.data.id)) {
    options.unshift(preset.data);
  }
  const isAgent = ws.summary.role === 'Agent';

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (denied !== null || create.isPending) {
      return;
    }
    const fields: Record<string, string> = {};
    if (form.values.customerId.length === 0) {
      fields.customerId = 'Müşteri seçin.';
    }
    if (form.values.title.trim().length === 0) {
      fields.title = 'Bu alan zorunludur.';
    }
    if (form.values.description.trim().length === 0) {
      fields.description = 'Bu alan zorunludur.';
    }
    if (Object.keys(fields).length > 0) {
      form.fail('Talep açılmadı: düzeltilmesi gereken alanlar var.', fields);
      return;
    }
    form.clear();
    setQuota(null);
    create.mutate(
      {
        customerId: form.values.customerId,
        title: form.values.title.trim(),
        description: form.values.description.trim(),
        priority: form.values.priority as TicketPriority,
        assigneeUserId: form.values.assigneeUserId.length > 0 ? form.values.assigneeUserId : null,
      },
      {
        onError: (error) => {
          const info = quotaFromError(error);
          if (info !== null) {
            setQuota(info);
          } else {
            form.failWith(error);
          }
        },
      },
    );
  };

  return (
    <div className="stack">
      <WsHeader title="Yeni talep" />
      <p className="muted small">
        Bu dönem açılan talep: <span className="num">{usage.ticketsThisPeriod}</span> / <span className="num">{limit}</span> (
        {ws.summary.subscription.planName} planı).
      </p>
      {knownQuota !== null && <QuotaNote quota={knownQuota} />}
      {customers.isError && <ErrorState error={customers.error} onRetry={() => void customers.refetch()} />}
      {customers.isSuccess && options.length === 0 && (
        <Note tone="info" title="Önce bir müşteri ekleyin">
          <p>
            Talepler bir müşteri kaydına bağlanır. <Link to={`/t/${ws.tenantId}/customers/new`}>Müşteri ekle</Link>
          </p>
        </Note>
      )}
      <form id={form.formId} className="form form--wide" onSubmit={submit} noValidate>
        {form.summary !== null && <ErrorSummary title={form.summary} />}
        <Field
          label="Müşteri"
          name="customerId"
          as="select"
          required
          value={form.values.customerId}
          onChange={form.set('customerId')}
          error={form.errors.customerId}
          hint={customers.data?.nextCursor != null ? 'İlk 100 müşteri listelenir. Diğerleri için müşteri sayfasındaki "talep aç" düğmesini kullanın.' : undefined}
        >
          <option value="">{customers.isPending ? 'Müşteriler yükleniyor…' : 'Müşteri seçin'}</option>
          {options.map((customer) => (
            <option key={customer.id} value={customer.id}>
              {customer.companyTitle !== null ? `${customer.name} — ${customer.companyTitle}` : customer.name}
            </option>
          ))}
        </Field>
        <Field label="Başlık" name="title" required maxLength={200} value={form.values.title} onChange={form.set('title')} error={form.errors.title} />
        <Field
          label="Açıklama"
          name="description"
          as="textarea"
          required
          rows={6}
          maxLength={10000}
          value={form.values.description}
          onChange={form.set('description')}
          error={form.errors.description}
        />
        <Field label="Öncelik" name="priority" as="select" value={form.values.priority} onChange={form.set('priority')} error={form.errors.priority}>
          {PRIORITIES.map((priority) => (
            <option key={priority} value={priority}>
              {ticketPriorityLabel[priority]}
            </option>
          ))}
        </Field>
        <Field
          label="Atanan"
          name="assigneeUserId"
          as="select"
          value={form.values.assigneeUserId}
          onChange={form.set('assigneeUserId')}
          error={form.errors.assigneeUserId}
          hint={isAgent ? 'Agent rolü talebi yalnız kendi üzerine alabilir.' : undefined}
        >
          <option value="">Atanmamış</option>
          {(members.data?.items ?? [])
            .filter((member) => !isAgent || member.userId === ws.userId)
            .map((member) => (
              <option key={member.userId} value={member.userId}>
                {member.userId === ws.userId ? `${member.displayName} (siz)` : member.displayName}
              </option>
            ))}
        </Field>
        <div className="row row--top">
          <Button type="submit" variant="primary" busy={create.isPending} disabledReason={denied}>
            Talebi aç
          </Button>
          <LinkButton to={`/t/${ws.tenantId}/tickets`}>Vazgeç</LinkButton>
        </div>
      </form>
    </div>
  );
}

/* ---------- Detay ---------- */

const ACTIONS_BY_STATUS: Record<TicketStatus, TicketAction[]> = {
  Open: ['Start', 'Resolve'],
  InProgress: ['Release', 'Resolve'],
  Resolved: ['Close', 'Reopen'],
  Closed: ['Reopen'],
};

function TicketEditForm({ ticket, onDone }: { ticket: Ticket; onDone: () => void }) {
  const ws = useWorkspace();
  const queryClient = useQueryClient();
  const notify = useNotice();
  const form = useFormState({ title: ticket.title, description: ticket.description, priority: ticket.priority as string });
  const [baseVersion, setBaseVersion] = useState(ticket.version);
  const [conflict, setConflict] = useState(false);
  const [keptMine, setKeptMine] = useState(false);
  const [reloading, setReloading] = useState(false);
  const detailKey = ws.key('tickets', 'detail', ticket.id);

  const save = useTenantMutation<Ticket, { title: string; description: string; priority: TicketPriority }>({
    mutationFn: async (body) => (await request<Ticket>(ws.api(`/tickets/${ticket.id}`), { method: 'PATCH', body, ifMatch: baseVersion })).data,
    invalidate: [ws.key('tickets', 'list'), ws.key('tickets', 'timeline', ticket.id)],
    onSuccess: (updated) => {
      queryClient.setQueryData(detailKey, updated);
      notify('Talep güncellendi.');
      onDone();
    },
  });

  const loadLatest = (keepMine: boolean) => {
    setReloading(true);
    get<Ticket>(ws.api(`/tickets/${ticket.id}`))
      .then((latest) => {
        queryClient.setQueryData(detailKey, latest);
        setBaseVersion(latest.version);
        if (!keepMine) {
          form.setValues({ title: latest.title, description: latest.description, priority: latest.priority });
        }
        setKeptMine(keepMine);
        setConflict(false);
        form.clear();
      })
      .catch((error: unknown) => form.failWith(error))
      .finally(() => setReloading(false));
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (save.isPending) {
      return;
    }
    const fields: Record<string, string> = {};
    if (form.values.title.trim().length === 0) {
      fields.title = 'Bu alan zorunludur.';
    }
    if (form.values.description.trim().length === 0) {
      fields.description = 'Bu alan zorunludur.';
    }
    if (Object.keys(fields).length > 0) {
      form.fail('Talep kaydedilmedi.', fields);
      return;
    }
    form.clear();
    setKeptMine(false);
    save.mutate(
      { title: form.values.title.trim(), description: form.values.description.trim(), priority: form.values.priority as TicketPriority },
      {
        onError: (error) => {
          if (isConflict(error)) {
            setConflict(true);
          } else {
            form.failWith(error);
          }
        },
      },
    );
  };

  return (
    <form id={form.formId} className="form form--wide" onSubmit={submit} noValidate aria-label="Talebi düzenle">
      {conflict && <ConflictNote busy={reloading} onReload={() => loadLatest(false)} onKeepMine={() => loadLatest(true)} />}
      {keptMine && (
        <Note tone="info" role="status">
          <p>Metniniz korundu. Kaydederseniz güncel sürümün üzerine yazılır.</p>
        </Note>
      )}
      {form.summary !== null && <ErrorSummary title={form.summary} />}
      <Field label="Başlık" name="title" required maxLength={200} value={form.values.title} onChange={form.set('title')} error={form.errors.title} />
      <Field
        label="Açıklama"
        name="description"
        as="textarea"
        required
        rows={6}
        maxLength={10000}
        value={form.values.description}
        onChange={form.set('description')}
        error={form.errors.description}
      />
      <Field label="Öncelik" name="priority" as="select" value={form.values.priority} onChange={form.set('priority')}>
        {PRIORITIES.map((priority) => (
          <option key={priority} value={priority}>
            {ticketPriorityLabel[priority]}
          </option>
        ))}
      </Field>
      <div className="row">
        <Button type="submit" variant="primary" busy={save.isPending} disabledReason={conflict ? 'Önce çakışmayı çözün.' : null}>
          Değişiklikleri kaydet
        </Button>
        <Button onClick={onDone}>Vazgeç</Button>
      </div>
    </form>
  );
}

function modifyDeniedReason(ticket: Ticket, writeDenied: string | null): string | null {
  if (writeDenied !== null) {
    return writeDenied;
  }
  return ticket.canModify ? null : 'Bu talep başka bir üyeye atanmış. Yalnız atanan kişi, Admin veya Owner değiştirebilir.';
}

function Timeline({ ticketId }: { ticketId: string }) {
  const ws = useWorkspace();
  const timeline = useQuery({
    queryKey: ws.key('tickets', 'timeline', ticketId),
    queryFn: ({ signal }) => get<Items<TimelineItem>>(ws.api(`/tickets/${ticketId}/timeline`), signal),
  });
  if (timeline.isPending) {
    return <Loading label="Zaman çizelgesi yükleniyor…" />;
  }
  if (timeline.isError) {
    return <ErrorState error={timeline.error} onRetry={() => void timeline.refetch()} />;
  }
  if (timeline.data.items.length === 0) {
    return <EmptyState title="Henüz kayıt yok." />;
  }
  return (
    <ol className="timeline" aria-label="Zaman çizelgesi">
      {timeline.data.items.map((item) => (
        <li key={`${item.kind}-${item.id}`} className={item.kind === 'comment' ? 'timeline__item timeline__item--comment' : 'timeline__item'}>
          <div className="timeline__meta">
            <span>{item.authorName ?? 'Sistem'}</span> · <span className="num">{formatDateTime(item.at)}</span> ·{' '}
            {item.kind === 'comment' ? 'yorum' : 'olay'}
          </div>
          {item.kind === 'comment' ? <p className="pre-wrap">{item.text}</p> : <p>{timelineEventText(item.action, item.data)}</p>}
        </li>
      ))}
    </ol>
  );
}

function CommentForm({ ticket, deniedReason }: { ticket: Ticket; deniedReason: string | null }) {
  const ws = useWorkspace();
  const notify = useNotice();
  const form = useFormState({ body: '' });
  const comment = useTenantMutation<TimelineItem, string>({
    mutationFn: async (body) => (await request<TimelineItem>(ws.api(`/tickets/${ticket.id}/comments`), { method: 'POST', body: { body } })).data,
    invalidate: [ws.key('tickets', 'timeline', ticket.id), ws.key('tickets', 'list')],
    onSuccess: () => {
      form.setValues({ body: '' });
      notify('Yorum eklendi.');
    },
  });
  const blocked = deniedReason ?? (ticket.status === 'Closed' ? 'Kapalı talebe yorum eklenemez; önce yeniden açın.' : null);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (blocked !== null || comment.isPending) {
      return;
    }
    if (form.values.body.trim().length === 0) {
      form.fail('Yorum gönderilmedi.', { body: 'Yorum metni boş olamaz.' });
      return;
    }
    form.clear();
    // Hata durumunda metin formda kalır.
    comment.mutate(form.values.body.trim(), { onError: (error) => form.failWith(error) });
  };

  return (
    <form id={form.formId} className="form form--wide" onSubmit={submit} noValidate aria-label="Yorum ekle">
      {form.summary !== null && <ErrorSummary title={form.summary} />}
      <Field label="Yorum" name="body" as="textarea" required rows={4} maxLength={5000} value={form.values.body} onChange={form.set('body')} error={form.errors.body} />
      <div className="row row--top">
        <Button type="submit" variant="primary" busy={comment.isPending} disabledReason={blocked}>
          Yorum ekle
        </Button>
      </div>
    </form>
  );
}

export function TicketDetailPage() {
  const ws = useWorkspace();
  const { ticketId = '' } = useParams();
  const queryClient = useQueryClient();
  const notify = useNotice();
  const [editing, setEditing] = useState(false);
  const [assignee, setAssignee] = useState<string | null>(null);
  const [actionError, setActionError] = useState<unknown>(null);
  const detailKey = ws.key('tickets', 'detail', ticketId);

  const ticket = useQuery({ queryKey: detailKey, queryFn: ({ signal }) => get<Ticket>(ws.api(`/tickets/${ticketId}`), signal) });
  const members = useAssignableMembers();

  const afterChange = (updated: Ticket) => {
    queryClient.setQueryData(detailKey, updated);
    setActionError(null);
  };
  const onActionError = (error: unknown) => {
    setActionError(error);
    if (isConflict(error)) {
      void ticket.refetch();
    }
  };
  const invalidate = [ws.key('tickets', 'list'), ws.key('tickets', 'timeline', ticketId)];

  const transition = useTenantMutation<Ticket, TicketAction>({
    mutationFn: async (action) =>
      (await request<Ticket>(ws.api(`/tickets/${ticketId}/transition`), { method: 'POST', body: { action }, ifMatch: ticket.data?.version })).data,
    invalidate,
    onSuccess: (updated) => {
      afterChange(updated);
      notify(`Talep durumu: ${ticketStatusLabel[updated.status]}.`);
    },
  });

  const assign = useTenantMutation<Ticket, string | null>({
    mutationFn: async (userId) =>
      (await request<Ticket>(ws.api(`/tickets/${ticketId}/assignee`), { method: 'PUT', body: { userId }, ifMatch: ticket.data?.version })).data,
    invalidate,
    onSuccess: (updated) => {
      afterChange(updated);
      setAssignee(null);
      notify(updated.assigneeName !== null ? `Talep ${updated.assigneeName} üzerine atandı.` : 'Talebin ataması kaldırıldı.');
    },
  });

  if (ticket.isPending) {
    return (
      <div className="stack">
        <WsHeader title="Talep" />
        <Loading label="Talep yükleniyor…" />
      </div>
    );
  }
  if (ticket.isError && ticket.data === undefined) {
    return (
      <div className="stack">
        <WsHeader title="Talep" />
        <ErrorState error={ticket.error} onRetry={() => void ticket.refetch()} notFoundTitle="Talep bulunamadı" />
        <p>
          <Link to={`/t/${ws.tenantId}/tickets`}>Talep listesine dön</Link>
        </p>
      </div>
    );
  }

  const data = ticket.data;
  if (data === undefined) {
    return null;
  }
  const writeDenied = ws.denied('tickets.write');
  const modifyDenied = modifyDeniedReason(data, writeDenied);
  const isAgent = ws.summary.role === 'Agent';
  const currentAssignee = data.assigneeUserId ?? '';
  const selectedAssignee = assignee ?? currentAssignee;
  const assignDenied =
    writeDenied ??
    (data.status === 'Closed'
      ? 'Kapalı talebin ataması değiştirilemez; önce yeniden açın.'
      : isAgent && data.assigneeUserId !== null && data.assigneeUserId !== ws.userId
        ? 'Agent rolü yalnız atanmamış talebi üzerine alabilir ya da kendi talebini bırakabilir.'
        : null);

  const actionReason = (action: TicketAction): string | null => {
    if (data.allowedActions.includes(action)) {
      return null;
    }
    if (modifyDenied !== null) {
      return modifyDenied;
    }
    if (action === 'Reopen' && data.status === 'Closed') {
      return 'Kapalı talebi yalnız Admin veya Owner, kapanıştan sonraki 30 gün içinde yeniden açabilir.';
    }
    return 'Talep bu durumdayken bu eylem yapılamaz.';
  };

  return (
    <div className="stack-lg">
      <WsHeader title={data.title}>
        <p className="row">
          <span className="num muted">#{data.number}</span>
          <Badge tone={ticketStatusTone[data.status]}>{ticketStatusLabel[data.status]}</Badge>
          <Badge tone={ticketPriorityTone[data.priority]}>Öncelik: {ticketPriorityLabel[data.priority]}</Badge>
        </p>
      </WsHeader>

      {actionError !== null &&
        (isConflict(actionError) ? (
          <Note tone="warn" title="Talep siz bakarken değişti" role="alert">
            <p>Güncel hali yüklendi; işlem uygulanmadı. Durumu denetleyip yeniden deneyin.</p>
          </Note>
        ) : (
          <ErrorSummary title={problemText(actionError)} />
        ))}

      {editing ? (
        <TicketEditForm key={data.id} ticket={data} onDone={() => setEditing(false)} />
      ) : (
        <section className="stack" aria-label="Talep bilgileri">
          <p className="pre-wrap">{data.description}</p>
          <dl className="deflist">
            <dt>Müşteri</dt>
            <dd>
              <Link to={`/t/${ws.tenantId}/customers/${data.customerId}`}>{data.customerName}</Link>
            </dd>
            <dt>Atanan</dt>
            <dd>{data.assigneeName ?? 'Atanmamış'}</dd>
            <dt>Açılma</dt>
            <dd className="num">{formatDateTime(data.createdAt)}</dd>
            <dt>Son güncelleme</dt>
            <dd className="num">{formatDateTime(data.updatedAt)}</dd>
            {data.resolvedAt !== null && (
              <>
                <dt>Çözülme</dt>
                <dd className="num">{formatDateTime(data.resolvedAt)}</dd>
              </>
            )}
            {data.closedAt !== null && (
              <>
                <dt>Kapanma</dt>
                <dd className="num">{formatDateTime(data.closedAt)}</dd>
              </>
            )}
            {data.reopenCount > 0 && (
              <>
                <dt>Yeniden açılma</dt>
                <dd className="num">{data.reopenCount} kez</dd>
              </>
            )}
          </dl>
          <div className="row row--top">
            <Button onClick={() => setEditing(true)} disabledReason={modifyDenied}>
              Düzenle
            </Button>
          </div>
        </section>
      )}

      <section className="section stack" aria-labelledby="ticket-status">
        <h2 id="ticket-status">Durum</h2>
        <div className="row row--top">
          {ACTIONS_BY_STATUS[data.status].map((action) => (
            <Button
              key={action}
              variant={action === 'Resolve' || action === 'Start' ? 'primary' : 'default'}
              busy={transition.isPending && transition.variables === action}
              disabledReason={actionReason(action)}
              onClick={() => transition.mutate(action, { onError: onActionError })}
            >
              {ticketActionLabel[action]}
            </Button>
          ))}
        </div>
      </section>

      <section className="section stack" aria-labelledby="ticket-assign">
        <h2 id="ticket-assign">Atama</h2>
        <form
          className="inline-form"
          onSubmit={(event) => {
            event.preventDefault();
            if (assignDenied === null && !assign.isPending) {
              assign.mutate(selectedAssignee.length > 0 ? selectedAssignee : null, { onError: onActionError });
            }
          }}
        >
          <Field label="Atanan üye" name="assignee" as="select" value={selectedAssignee} onChange={setAssignee} disabled={assignDenied !== null}>
            <option value="">Atanmamış</option>
            {(members.data?.items ?? [])
              .filter((member) => !isAgent || member.userId === ws.userId || member.userId === data.assigneeUserId)
              .map((member) => (
                <option key={member.userId} value={member.userId}>
                  {member.userId === ws.userId ? `${member.displayName} (siz)` : member.displayName}
                </option>
              ))}
          </Field>
          <Button type="submit" busy={assign.isPending} disabledReason={assignDenied}>
            Atamayı kaydet
          </Button>
        </form>
      </section>

      <section className="section stack" aria-labelledby="ticket-timeline">
        <h2 id="ticket-timeline">Zaman çizelgesi</h2>
        <Timeline ticketId={data.id} />
        <CommentForm ticket={data} deniedReason={modifyDenied} />
      </section>
    </div>
  );
}

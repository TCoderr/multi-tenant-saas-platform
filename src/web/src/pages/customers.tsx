// Müşteriler: liste + arama, yeni kayıt, detay (düzenleme, silme, müşterinin talepleri).

import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { get, request, withQuery } from '../api/client';
import type { Customer, CustomerInput, Page, TicketListItem } from '../api/types';
import { orNull, useFormState } from '../components/form';
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
  LinkButton,
  LoadMore,
  Loading,
  Note,
  problemText,
  type Column,
} from '../components/ui';
import { formatDateTime, ticketStatusLabel, ticketStatusTone } from '../lib/format';
import { ConflictNote, WsHeader, isConflict } from '../workspace/common';
import { useTenantMutation, useWorkspace } from '../workspace/context';

const SORTS = ['createdAt', 'name'] as const;

export function CustomersPage() {
  const ws = useWorkspace();
  const [params, setParams] = useSearchParams();
  const q = params.get('q') ?? '';
  const sortParam = params.get('sort');
  const sort = sortParam === 'name' ? 'name' : 'createdAt';
  const [draft, setDraft] = useState({ q, sort: sort as string });

  const list = useInfiniteQuery({
    queryKey: ws.key('customers', 'list', { q, sort }),
    queryFn: ({ pageParam, signal }) => get<Page<Customer>>(withQuery(ws.api('/customers'), { q, sort, cursor: pageParam }), signal),
    initialPageParam: '',
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next = new URLSearchParams();
    if (draft.q.trim().length > 0) {
      next.set('q', draft.q.trim());
    }
    if (draft.sort !== 'createdAt') {
      next.set('sort', draft.sort);
    }
    setParams(next);
  };

  const rows = list.data?.pages.flatMap((page) => page.items) ?? [];
  const masked = rows.some((row) => row.piiMasked);

  const columns: Column<Customer>[] = [
    { key: 'name', header: 'Ad', rowHeader: true, cell: (row) => <Link to={`/t/${ws.tenantId}/customers/${row.id}`}>{row.name}</Link> },
    { key: 'title', header: 'Unvan', cell: (row) => row.companyTitle ?? '—' },
    { key: 'email', header: 'E-posta', cell: (row) => <span className="break">{row.email ?? '—'}</span> },
    { key: 'phone', header: 'Telefon', cell: (row) => <span className="num">{row.phone ?? '—'}</span> },
    { key: 'created', header: 'Eklenme', cell: (row) => <span className="num">{formatDateTime(row.createdAt)}</span> },
  ];

  return (
    <div className="stack">
      <WsHeader
        title="Müşteriler"
        actions={
          <LinkButton to={`/t/${ws.tenantId}/customers/new`} variant="primary" disabledReason={ws.denied('customers.write')}>
            Yeni müşteri
          </LinkButton>
        }
      />
      <form className="filters" role="search" aria-label="Müşteri arama" onSubmit={submit}>
        <Field
          className="field--search"
          label="Ara"
          name="q"
          type="search"
          maxLength={100}
          hint="Ad, unvan veya e-posta"
          value={draft.q}
          onChange={(value) => setDraft((previous) => ({ ...previous, q: value }))}
        />
        <Field label="Sıralama" name="sort" as="select" value={draft.sort} onChange={(value) => setDraft((previous) => ({ ...previous, sort: value }))}>
          {SORTS.map((value) => (
            <option key={value} value={value}>
              {value === 'name' ? 'Ada göre' : 'En yeni önce'}
            </option>
          ))}
        </Field>
        <Button type="submit">Uygula</Button>
      </form>
      {masked && <p className="muted small">Rolünüz için e-posta ve telefon maskelenir, notlar gösterilmez.</p>}
      {list.isPending ? (
        <Loading label="Müşteriler yükleniyor…" />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} />
      ) : rows.length === 0 ? (
        q.length > 0 ? (
          <EmptyState title="Aramanızla eşleşen müşteri yok.">
            <p>Yazımı denetleyin ya da aramayı temizleyin.</p>
          </EmptyState>
        ) : (
          <EmptyState title="Henüz müşteri yok.">
            <p>İlk müşteriyi ekleyin; talepler müşteri kayıtlarına bağlanır.</p>
          </EmptyState>
        )
      ) : (
        <>
          <DataList caption="Müşteriler" columns={columns} rows={rows} rowKey={(row) => row.id} />
          <LoadMore hasMore={list.hasNextPage} loading={list.isFetchingNextPage} onLoad={() => void list.fetchNextPage()} />
        </>
      )}
    </div>
  );
}

/* ---------- Form ---------- */

type CustomerFormValues = { name: string; companyTitle: string; email: string; phone: string; notes: string };

function toValues(customer: Customer | null): CustomerFormValues {
  return {
    name: customer?.name ?? '',
    companyTitle: customer?.companyTitle ?? '',
    email: customer?.email ?? '',
    phone: customer?.phone ?? '',
    notes: customer?.notes ?? '',
  };
}

function toInput(values: CustomerFormValues): CustomerInput {
  return {
    name: values.name.trim(),
    companyTitle: orNull(values.companyTitle),
    email: orNull(values.email),
    phone: orNull(values.phone),
    notes: orNull(values.notes),
  };
}

function CustomerFields({ form }: { form: ReturnType<typeof useFormState<CustomerFormValues>> }) {
  return (
    <>
      <Field label="Ad soyad" name="name" required maxLength={120} value={form.values.name} onChange={form.set('name')} error={form.errors.name} />
      <Field label="Unvan" name="companyTitle" maxLength={160} value={form.values.companyTitle} onChange={form.set('companyTitle')} error={form.errors.companyTitle} />
      <Field label="E-posta" name="email" type="email" maxLength={254} value={form.values.email} onChange={form.set('email')} error={form.errors.email} />
      <Field
        label="Telefon"
        name="phone"
        type="tel"
        maxLength={40}
        hint="Rakam, boşluk ve + - ( ) kullanılabilir; en az 7 rakam."
        value={form.values.phone}
        onChange={form.set('phone')}
        error={form.errors.phone}
      />
      <Field label="Notlar" name="notes" as="textarea" rows={4} maxLength={2000} value={form.values.notes} onChange={form.set('notes')} error={form.errors.notes} />
    </>
  );
}

export function CustomerNewPage() {
  const ws = useWorkspace();
  const navigate = useNavigate();
  const notify = useNotice();
  const form = useFormState<CustomerFormValues>(toValues(null));

  const create = useTenantMutation<Customer, CustomerInput>({
    mutationFn: async (input) => (await request<Customer>(ws.api('/customers'), { method: 'POST', body: input })).data,
    invalidate: [ws.key('customers')],
    onSuccess: (customer) => {
      notify('Müşteri eklendi.');
      void navigate(`/t/${ws.tenantId}/customers/${customer.id}`);
    },
  });

  const denied = ws.denied('customers.write');

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (denied !== null || create.isPending) {
      return;
    }
    if (form.values.name.trim().length === 0) {
      form.fail('Müşteri kaydedilmedi.', { name: 'Bu alan zorunludur.' });
      return;
    }
    form.clear();
    create.mutate(toInput(form.values), { onError: (error) => form.failWith(error) });
  };

  return (
    <div className="stack">
      <WsHeader title="Yeni müşteri" />
      <form id={form.formId} className="form" onSubmit={submit} noValidate>
        {form.summary !== null && <ErrorSummary title={form.summary} />}
        <CustomerFields form={form} />
        <div className="row row--top">
          <Button type="submit" variant="primary" busy={create.isPending} disabledReason={denied}>
            Müşteriyi kaydet
          </Button>
          <LinkButton to={`/t/${ws.tenantId}/customers`}>Vazgeç</LinkButton>
        </div>
      </form>
    </div>
  );
}

function CustomerEditForm({ customer, onDone }: { customer: Customer; onDone: () => void }) {
  const ws = useWorkspace();
  const queryClient = useQueryClient();
  const notify = useNotice();
  const form = useFormState<CustomerFormValues>(toValues(customer));
  // Düzenlemenin dayandığı sürüm: If-Match olarak gider. Çakışmada kullanıcı güncel sürümü yükler ya da kendi metnini korur.
  const [baseVersion, setBaseVersion] = useState(customer.version);
  const [conflict, setConflict] = useState(false);
  const [keptMine, setKeptMine] = useState(false);
  const [reloading, setReloading] = useState(false);
  const detailKey = ws.key('customers', 'detail', customer.id);

  const save = useTenantMutation<Customer, CustomerInput>({
    mutationFn: async (input) => (await request<Customer>(ws.api(`/customers/${customer.id}`), { method: 'PUT', body: input, ifMatch: baseVersion })).data,
    invalidate: [ws.key('customers', 'list')],
    onSuccess: (updated) => {
      queryClient.setQueryData(detailKey, updated);
      notify('Müşteri güncellendi.');
      onDone();
    },
  });

  const loadLatest = (keepMine: boolean) => {
    setReloading(true);
    get<Customer>(ws.api(`/customers/${customer.id}`))
      .then((latest) => {
        queryClient.setQueryData(detailKey, latest);
        setBaseVersion(latest.version);
        if (!keepMine) {
          form.setValues(toValues(latest));
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
    if (form.values.name.trim().length === 0) {
      form.fail('Müşteri kaydedilmedi.', { name: 'Bu alan zorunludur.' });
      return;
    }
    form.clear();
    setKeptMine(false);
    save.mutate(toInput(form.values), {
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
    <form id={form.formId} className="form" onSubmit={submit} noValidate aria-label="Müşteriyi düzenle">
      {conflict && <ConflictNote busy={reloading} onReload={() => loadLatest(false)} onKeepMine={() => loadLatest(true)} />}
      {keptMine && (
        <Note tone="info" role="status">
          <p>Metniniz korundu. Kaydederseniz güncel sürümün üzerine yazılır.</p>
        </Note>
      )}
      {form.summary !== null && <ErrorSummary title={form.summary} />}
      <CustomerFields form={form} />
      <div className="row">
        <Button type="submit" variant="primary" busy={save.isPending} disabledReason={conflict ? 'Önce çakışmayı çözün.' : null}>
          Değişiklikleri kaydet
        </Button>
        <Button onClick={onDone}>Vazgeç</Button>
      </div>
    </form>
  );
}

function CustomerTickets({ customerId }: { customerId: string }) {
  const ws = useWorkspace();
  const tickets = useQuery({
    queryKey: ws.key('tickets', 'list', { customerId, scope: 'customer-detail' }),
    queryFn: ({ signal }) => get<Page<TicketListItem>>(withQuery(ws.api('/tickets'), { customerId, limit: 10 }), signal),
  });
  const columns: Column<TicketListItem>[] = [
    { key: 'number', header: 'No', tight: true, cell: (row) => <span className="num">#{row.number}</span> },
    { key: 'title', header: 'Başlık', rowHeader: true, cell: (row) => <Link to={`/t/${ws.tenantId}/tickets/${row.id}`}>{row.title}</Link> },
    { key: 'status', header: 'Durum', cell: (row) => <Badge tone={ticketStatusTone[row.status]}>{ticketStatusLabel[row.status]}</Badge> },
    { key: 'updated', header: 'Güncelleme', cell: (row) => <span className="num">{formatDateTime(row.updatedAt)}</span> },
  ];
  if (tickets.isPending) {
    return <Loading label="Talepler yükleniyor…" />;
  }
  if (tickets.isError) {
    return <ErrorState error={tickets.error} onRetry={() => void tickets.refetch()} />;
  }
  if (tickets.data.items.length === 0) {
    return <EmptyState title="Bu müşterinin talebi yok." />;
  }
  return (
    <>
      <DataList caption="Müşterinin talepleri" columns={columns} rows={tickets.data.items} rowKey={(row) => row.id} />
      {tickets.data.nextCursor !== null && (
        <p>
          <Link to={`/t/${ws.tenantId}/tickets?customerId=${customerId}`}>Bu müşterinin tüm taleplerini göster</Link>
        </p>
      )}
    </>
  );
}

export function CustomerDetailPage() {
  const ws = useWorkspace();
  const { customerId = '' } = useParams();
  const navigate = useNavigate();
  const notify = useNotice();
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);

  const customer = useQuery({
    queryKey: ws.key('customers', 'detail', customerId),
    queryFn: ({ signal }) => get<Customer>(ws.api(`/customers/${customerId}`), signal),
  });

  const remove = useTenantMutation<void>({
    mutationFn: async () => {
      await request<void>(ws.api(`/customers/${customerId}`), { method: 'DELETE' });
    },
    invalidate: [ws.key('customers', 'list')],
    onSuccess: () => {
      notify('Müşteri silindi.');
      void navigate(`/t/${ws.tenantId}/customers`);
    },
  });

  if (customer.isPending) {
    return (
      <div className="stack">
        <WsHeader title="Müşteri" />
        <Loading label="Müşteri yükleniyor…" />
      </div>
    );
  }
  if (customer.isError) {
    return (
      <div className="stack">
        <WsHeader title="Müşteri" />
        <ErrorState error={customer.error} onRetry={() => void customer.refetch()} notFoundTitle="Müşteri bulunamadı" />
        <p>
          <Link to={`/t/${ws.tenantId}/customers`}>Müşteri listesine dön</Link>
        </p>
      </div>
    );
  }

  const data = customer.data;
  return (
    <div className="stack-lg">
      <WsHeader
        title={data.name}
        actions={
          <>
            <LinkButton to={`/t/${ws.tenantId}/tickets/new?customerId=${data.id}`} variant="primary" disabledReason={ws.denied('tickets.write')}>
              Bu müşteri için talep aç
            </LinkButton>
          </>
        }
      />
      {editing ? (
        <CustomerEditForm key={data.id} customer={data} onDone={() => setEditing(false)} />
      ) : (
        <section className="stack" aria-label="Müşteri bilgileri">
          <dl className="deflist">
            <dt>Unvan</dt>
            <dd>{data.companyTitle ?? '—'}</dd>
            <dt>E-posta</dt>
            <dd>{data.email ?? '—'}</dd>
            <dt>Telefon</dt>
            <dd className="num">{data.phone ?? '—'}</dd>
            <dt>Notlar</dt>
            <dd className="pre-wrap">{data.piiMasked ? 'Rolünüz için gösterilmez.' : (data.notes ?? '—')}</dd>
            <dt>Eklenme</dt>
            <dd className="num">{formatDateTime(data.createdAt)}</dd>
            <dt>Son güncelleme</dt>
            <dd className="num">{formatDateTime(data.updatedAt)}</dd>
          </dl>
          {data.piiMasked && <p className="muted small">Rolünüz için e-posta ve telefon maskelenir.</p>}
          <div className="row row--top">
            <Button onClick={() => setEditing(true)} disabledReason={ws.denied('customers.write')}>
              Düzenle
            </Button>
            <Button variant="danger" onClick={() => setConfirmDelete(true)} disabledReason={ws.denied('customers.delete')}>
              Müşteriyi sil
            </Button>
          </div>
        </section>
      )}

      <section className="section stack" aria-labelledby="customer-tickets">
        <h2 id="customer-tickets">Talepler</h2>
        <CustomerTickets customerId={data.id} />
      </section>

      <Dialog
        open={confirmDelete}
        title="Müşteriyi sil"
        confirmLabel="Sil"
        danger
        busy={remove.isPending}
        error={remove.isError ? problemText(remove.error) : null}
        onClose={() => {
          setConfirmDelete(false);
          remove.reset();
        }}
        onSubmit={() => remove.mutate()}
      >
        <p>
          <strong>{data.name}</strong> kaydı kalıcı olarak silinecek. Talebi olan müşteri silinemez.
        </p>
      </Dialog>
    </div>
  );
}

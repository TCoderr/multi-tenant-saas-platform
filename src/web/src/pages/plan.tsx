// Plan ve kullanım: limit çubukları, plan değişikliği (202 → 2 sn yoklama), işlem geçmişi, iptal.
// "İstek kabul edildi" ile "plan uygulandı" ayrıdır: plan yalnız işlem "Uygulandı" olunca değişir.

import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { get, newIdempotencyKey, request } from '../api/client';
import type { BillingOperation, BillingOverview, Plan } from '../api/types';
import { useNotice } from '../components/notice';
import { Badge, Button, DataList, Dialog, ErrorState, ErrorSummary, Field, Loading, Note, UsageMeter, problemText, type Column } from '../components/ui';
import {
  formatDate,
  formatDateTime,
  formatMoney,
  formatNumber,
  operationStatusLabel,
  operationStatusTone,
  subscriptionStatusLabel,
  subscriptionStatusTone,
} from '../lib/format';
import { WsHeader } from '../workspace/common';
import { useTenantMutation, useWorkspace } from '../workspace/context';

const OUTCOMES: { value: string; label: string }[] = [
  { value: '', label: 'Varsayılan (başarılı)' },
  { value: 'success', label: 'Başarılı' },
  { value: 'decline', label: 'Ödeme reddedilir' },
  { value: 'transient_then_success', label: 'Geçici hata, sonra başarılı' },
  { value: 'timeout_then_success', label: 'Zaman aşımı, sonra başarılı' },
  { value: 'timeout_not_recorded', label: 'Zaman aşımı, sağlayıcıda kayıt yok' },
  { value: 'pending_then_webhook', label: 'Beklemede, sonuç webhook ile gelir' },
  { value: 'timeout_unresolvable', label: 'Zaman aşımı, çözülemez (operatöre düşer)' },
  { value: 'success_refund_fails', label: 'Başarılı, iade başarısız' },
];

function operationExplanation(operation: BillingOperation): string {
  switch (operation.status) {
    case 'Pending':
      return 'İstek alındı; ödeme sağlayıcısının sonucu bekleniyor. Plan henüz değişmedi.';
    case 'Unknown':
      return 'Sağlayıcıdan yanıt alınamadı; sonuç doğrulanıyor. Plan henüz değişmedi, yeniden ücret alınmaz.';
    case 'Succeeded':
      return 'Plan değişikliği uygulandı. Yeni limitler geçerli.';
    case 'Declined':
      return 'Ödeme reddedildi. Planınız ve limitleriniz değişmedi.';
    case 'RefundPending':
      return 'Değişiklik uygulanamadı; alınan ödemenin iadesi bekleniyor.';
    case 'Refunded':
      return 'Değişiklik uygulanamadı; ödeme iade edildi.';
    case 'Voided':
      return 'İşlem iptal edildi; ücret alınmadı.';
    case 'ManuallyClosed':
      return 'İşlem platform operatörü tarafından kapatıldı.';
    case 'NeedsResolution':
      return 'Sonuç kendiliğinden belirlenemedi; platform operatörünün incelemesi bekleniyor. Plan değişmedi.';
  }
}

function OperationWatcher({ operationId, onSettled }: { operationId: string; onSettled: (operation: BillingOperation) => void }) {
  const ws = useWorkspace();
  const operation = useQuery({
    queryKey: ws.key('billing', 'operation', operationId),
    queryFn: ({ signal }) => get<BillingOperation>(ws.api(`/billing/operations/${operationId}`), signal),
    // Gerçek zamanlı bağlantı yok: işlem sonuçlanana kadar 2 saniyede bir yoklanır.
    refetchInterval: (query) => (query.state.data?.terminal === true ? false : 2000),
    staleTime: 0,
  });
  const settled = useRef(false);
  const data = operation.data;

  useEffect(() => {
    if (data?.terminal === true && !settled.current) {
      settled.current = true;
      onSettled(data);
    }
  }, [data, onSettled]);

  if (data === undefined) {
    return operation.isError ? <ErrorState error={operation.error} onRetry={() => void operation.refetch()} /> : <Loading label="İşlem durumu okunuyor…" />;
  }
  const tone = data.status === 'Succeeded' ? 'ok' : data.status === 'Declined' ? 'critical' : data.terminal ? 'info' : 'warn';
  return (
    <Note tone={tone} title={`Plan değişikliği: ${operationStatusLabel[data.status]}`} role="status">
      <p>{operationExplanation(data)}</p>
      <p className="small muted">
        İşlem {operationId.slice(0, 8)} · {formatMoney(data.amountMinor, data.currency)} · {data.terminal ? 'sonuçlandı' : 'durum 2 saniyede bir yenileniyor'}
      </p>
    </Note>
  );
}

function BillingDetails() {
  const ws = useWorkspace();
  const queryClient = useQueryClient();
  const notify = useNotice();
  const [outcome, setOutcome] = useState('');
  const [watching, setWatching] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [cancelOpen, setCancelOpen] = useState(false);
  const attempt = useRef<{ key: string; payload: string } | null>(null);
  const billingKey = ws.key('billing');

  const billing = useQuery({ queryKey: billingKey, queryFn: ({ signal }) => get<BillingOverview>(ws.api('/billing'), signal) });

  const change = useTenantMutation<BillingOperation, string>({
    mutationFn: async (planCode) => {
      const body = outcome.length > 0 ? { planCode, simulatedOutcome: outcome } : { planCode };
      const payload = JSON.stringify(body);
      if (attempt.current === null || attempt.current.payload !== payload) {
        attempt.current = { key: newIdempotencyKey(), payload };
      }
      return (await request<BillingOperation>(ws.api('/billing/plan-change'), { method: 'POST', body, idempotencyKey: attempt.current.key })).data;
    },
    invalidate: [billingKey],
    onSuccess: (operation) => {
      attempt.current = null;
      setError(null);
      setWatching(operation.operationId);
    },
  });

  const cancel = useTenantMutation<void>({
    mutationFn: async () => {
      await request<void>(ws.api('/billing/cancel'), { method: 'POST' });
    },
    invalidate: [billingKey],
    onSuccess: () => {
      setCancelOpen(false);
      notify('Abonelik iptal edildi.');
    },
  });

  const onSettled = (operation: BillingOperation) => {
    void queryClient.invalidateQueries({ queryKey: billingKey });
    void queryClient.invalidateQueries({ queryKey: ws.key('workspace') });
    if (operation.status === 'Succeeded') {
      notify('Plan değişikliği uygulandı.');
    }
  };

  if (billing.isPending) {
    return <Loading label="Plan bilgisi yükleniyor…" />;
  }
  if (billing.isError) {
    return <ErrorState error={billing.error} onRetry={() => void billing.refetch()} />;
  }

  const data = billing.data;
  const manageDenied = ws.denied('billing.manage');
  const openOperation = data.operations.find((operation) => !operation.terminal) ?? null;
  const watchedId = watching ?? openOperation?.operationId ?? null;
  const inFlight = data.operations.some((operation) => operation.status === 'Pending' || operation.status === 'Unknown');

  const planReason = (plan: Plan): string | null => {
    if (manageDenied !== null) {
      return manageDenied;
    }
    if (plan.code === 'trial') {
      return 'Deneme planına geri dönülemez.';
    }
    if (plan.code === data.subscription.planCode && data.subscription.status === 'Active') {
      return 'Şirket zaten bu planda.';
    }
    if (inFlight) {
      return 'Sonucu bekleyen bir plan değişikliği var.';
    }
    return null;
  };

  const planColumns: Column<Plan>[] = [
    {
      key: 'name',
      header: 'Plan',
      rowHeader: true,
      cell: (plan) => (
        <>
          {plan.name} {plan.code === data.subscription.planCode && <Badge tone="info">Geçerli plan</Badge>}
        </>
      ),
    },
    { key: 'price', header: 'Aylık ücret', cell: (plan) => <span className="num">{plan.priceMinor === 0 ? 'Ücretsiz' : formatMoney(plan.priceMinor, plan.currency)}</span> },
    { key: 'seats', header: 'Koltuk', cell: (plan) => <span className="num">{formatNumber(plan.seatLimit)}</span> },
    { key: 'tickets', header: 'Aylık talep', cell: (plan) => <span className="num">{formatNumber(plan.monthlyTicketLimit)}</span> },
    { key: 'export', header: 'Dışa aktarma', cell: (plan) => (plan.exportEnabled ? 'Var' : 'Yok') },
    {
      key: 'action',
      header: 'İşlem',
      cell: (plan) => (
        <Button
          small
          variant="primary"
          busy={change.isPending && change.variables === plan.code}
          disabledReason={planReason(plan)}
          onClick={() => change.mutate(plan.code, { onError: setError })}
          ariaLabel={`${plan.name} planına geç`}
        >
          Bu plana geç
        </Button>
      ),
    },
  ];

  const operationColumns: Column<BillingOperation>[] = [
    { key: 'at', header: 'Tarih', cell: (operation) => <span className="num">{formatDateTime(operation.createdAt)}</span> },
    { key: 'plan', header: 'Hedef plan', rowHeader: true, cell: (operation) => data.plans.find((plan) => plan.code === operation.planCode)?.name ?? operation.planCode },
    { key: 'amount', header: 'Tutar', cell: (operation) => <span className="num">{formatMoney(operation.amountMinor, operation.currency)}</span> },
    { key: 'status', header: 'Durum', cell: (operation) => <Badge tone={operationStatusTone[operation.status]}>{operationStatusLabel[operation.status]}</Badge> },
    { key: 'done', header: 'Sonuçlanma', cell: (operation) => <span className="num">{formatDateTime(operation.completedAt)}</span> },
  ];

  const cancelDenied = manageDenied ?? (data.subscription.status === 'Cancelled' ? 'Abonelik zaten iptal edilmiş.' : null);

  return (
    <>
      <section className="stack" aria-labelledby="change-title">
        <h2 id="change-title">Plan değiştir</h2>
        <p className="muted">Demo: ödeme simüle edilir, gerçek ücret alınmaz ve kart bilgisi istenmez.</p>
        {watchedId !== null && <OperationWatcher key={watchedId} operationId={watchedId} onSettled={onSettled} />}
        {error !== null && <ErrorSummary title={problemText(error)} />}
        {data.simulation.outcomeSelection && (
          <div className="form">
            <Field
              label="Simülasyon sonucu"
              name="simulatedOutcome"
              as="select"
              value={outcome}
              onChange={setOutcome}
              hint="Yalnız demo ortamında görünür: ödeme sağlayıcısının nasıl davranacağını seçer."
              disabled={manageDenied !== null}
            >
              {OUTCOMES.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </Field>
          </div>
        )}
        <DataList caption="Planlar" columns={planColumns} rows={data.plans} rowKey={(plan) => plan.code} />
      </section>

      <section className="section stack" aria-labelledby="operations-title">
        <h2 id="operations-title">Plan işlemleri</h2>
        {data.operations.length === 0 ? (
          <p className="muted">Henüz plan değişikliği yapılmadı.</p>
        ) : (
          <DataList caption="Plan işlemleri" columns={operationColumns} rows={data.operations} rowKey={(operation) => operation.operationId} />
        )}
      </section>

      <section className="section stack" aria-labelledby="cancel-title">
        <h2 id="cancel-title">Aboneliği iptal et</h2>
        <p className="muted">İptal edilince içerik salt-okunur olur; veriler silinmez. Owner yeni bir plan seçerek yeniden etkinleştirebilir.</p>
        <div className="row row--top">
          <Button variant="danger" onClick={() => setCancelOpen(true)} disabledReason={cancelDenied}>
            Aboneliği iptal et
          </Button>
        </div>
        <Dialog
          open={cancelOpen}
          title="Aboneliği iptal et"
          confirmLabel="Aboneliği iptal et"
          danger
          busy={cancel.isPending}
          error={cancel.isError ? problemText(cancel.error) : null}
          onClose={() => {
            setCancelOpen(false);
            cancel.reset();
          }}
          onSubmit={() => cancel.mutate()}
        >
          <p>
            <strong>{ws.summary.tenant.name}</strong> aboneliği iptal edilecek. İçerik salt-okunur olur; müşteri ve talep verileri silinmez.
          </p>
        </Dialog>
      </section>
    </>
  );
}

export function PlanPage() {
  const ws = useWorkspace();
  const { subscription, entitlements, usage } = ws.summary;
  return (
    <div className="stack-lg">
      <WsHeader title="Plan ve kullanım" />
      <section className="stack" aria-labelledby="current-title">
        <h2 id="current-title">Geçerli plan</h2>
        <dl className="deflist">
          <dt>Plan</dt>
          <dd>
            <strong>{subscription.planName}</strong> <Badge tone={subscriptionStatusTone[subscription.status]}>{subscriptionStatusLabel[subscription.status]}</Badge>
          </dd>
          {subscription.status === 'Trialing' && subscription.trialEndsAt !== null && (
            <>
              <dt>Deneme bitişi</dt>
              <dd className="num">{formatDate(subscription.trialEndsAt)}</dd>
            </>
          )}
          {subscription.graceEndsAt !== null && (
            <>
              <dt>Ödemesiz dönem bitişi</dt>
              <dd className="num">{formatDate(subscription.graceEndsAt)}</dd>
            </>
          )}
          <dt>Dışa aktarma</dt>
          <dd>{entitlements.exportEnabled ? 'Plana dahil' : 'Plana dahil değil'}</dd>
        </dl>
        <UsageMeter label="Koltuk" used={usage.seatsUsed} limit={entitlements.seatLimit} unit="üye" />
        <UsageMeter label="Bu dönem açılan talep" used={usage.ticketsThisPeriod} limit={entitlements.monthlyTicketLimit} unit="talep" />
        <p className="muted small">
          Talep kotası {formatDateTime(usage.periodResetsAt)} tarihinde sıfırlanır. Sayaçlar sunucudan gelir; bekleyen davet:{' '}
          <span className="num">{usage.pendingInvites}</span>.
        </p>
      </section>
      {ws.can('billing.read') ? (
        <BillingDetails />
      ) : (
        <Note tone="info" title="Plan ayrıntıları ve değişikliği">
          <p>{ws.denied('billing.read')}</p>
        </Note>
      )}
    </div>
  );
}

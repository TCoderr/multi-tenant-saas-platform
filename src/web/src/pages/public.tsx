// Herkese açık ekranlar: ürün/plan, giriş, kayıt, bulunamadı.

import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link, Navigate, useLocation } from 'react-router-dom';
import { ApiError, get, request } from '../api/client';
import { queryKeys } from '../api/queryKeys';
import type { Plan, PlansResponse } from '../api/types';
import { safeInternalPath, useFormState } from '../components/form';
import { Button, DataList, ErrorState, ErrorSummary, Field, LinkButton, Loading, Note, PageHeader, type Column } from '../components/ui';
import { formatMoney, formatNumber } from '../lib/format';
import { useSession } from '../session/SessionProvider';

export function planColumns(): Column<Plan>[] {
  return [
    { key: 'name', header: 'Plan', rowHeader: true, cell: (plan) => plan.name },
    {
      key: 'price',
      header: 'Aylık ücret',
      cell: (plan) => <span className="num">{plan.priceMinor === 0 ? 'Ücretsiz' : formatMoney(plan.priceMinor, plan.currency)}</span>,
    },
    { key: 'seats', header: 'Koltuk', cell: (plan) => <span className="num">{formatNumber(plan.seatLimit)} üye</span> },
    { key: 'tickets', header: 'Aylık talep', cell: (plan) => <span className="num">{formatNumber(plan.monthlyTicketLimit)} talep</span> },
    { key: 'export', header: 'Dışa aktarma', cell: (plan) => (plan.exportEnabled ? 'Var' : 'Yok') },
    { key: 'trial', header: 'Süre', cell: (plan) => (plan.trialDays !== null && plan.trialDays > 0 ? `${plan.trialDays} gün deneme` : 'Aylık') },
  ];
}

export function LandingPage() {
  const { user } = useSession();
  const plans = useQuery({ queryKey: queryKeys.publicPlans, queryFn: ({ signal }) => get<PlansResponse>('/api/plans', signal), staleTime: 5 * 60_000 });

  return (
    <div className="page stack-lg">
      <section className="hero">
        <div className="hero__lanes" aria-hidden="true">
          {[0, 1, 2, 3, 4, 5].map((lane) => (
            <span key={lane} className={`hero__lane lane-${lane}`} />
          ))}
        </div>
        <PageHeader title="Her şirket kendi kulvarında" />
        <p className="hero__lead">
          Kulvar, destek ekiplerinin müşteri kayıtlarını ve taleplerini tuttuğu çalışma alanıdır. Aynı gün iki şirkette çalışsanız bile hangi
          şirkette olduğunuz ekranın kenarındaki şeritten bellidir; bir şirketin verisi diğerine karışmaz.
        </p>
        <div className="row">
          {user === null ? (
            <>
              <LinkButton to="/signup" variant="primary">
                Hesap oluştur
              </LinkButton>
              <LinkButton to="/login">Giriş yap</LinkButton>
            </>
          ) : (
            <LinkButton to="/app" variant="primary">
              Şirketlerime git
            </LinkButton>
          )}
        </div>
      </section>

      <section className="section stack" aria-labelledby="plans-title">
        <h2 id="plans-title">Planlar</h2>
        <p className="muted">
          Her şirket deneme planıyla başlar. Bu bir demodur: plan değişikliği simüle edilir, gerçek ödeme alınmaz ve kart bilgisi istenmez.
        </p>
        {plans.isPending ? (
          <Loading label="Planlar yükleniyor…" />
        ) : plans.isError ? (
          <ErrorState error={plans.error} onRetry={() => void plans.refetch()} />
        ) : (
          <DataList caption="Plan karşılaştırması" columns={planColumns()} rows={plans.data.items} rowKey={(plan) => plan.code} />
        )}
      </section>
    </div>
  );
}

export function LoginPage() {
  const { state, login } = useSession();
  const location = useLocation();
  const form = useFormState({ email: '', password: '' });
  const [busy, setBusy] = useState(false);

  const routeState = (location.state ?? null) as { from?: unknown; expired?: unknown } | null;
  const from = safeInternalPath(routeState?.from);
  const expired = routeState?.expired === true;

  if (state.status === 'authenticated') {
    return <Navigate to={from !== null && !from.startsWith('/login') ? from : '/app'} replace />;
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (busy) {
      return;
    }
    const fields: Record<string, string> = {};
    if (form.values.email.trim().length === 0) {
      fields.email = 'E-posta adresinizi girin.';
    }
    if (form.values.password.length === 0) {
      fields.password = 'Parolanızı girin.';
    }
    if (Object.keys(fields).length > 0) {
      form.fail('Giriş yapılamadı: eksik alanlar var.', fields);
      return;
    }
    form.clear();
    setBusy(true);
    login(form.values.email.trim(), form.values.password).catch((error: unknown) => {
      setBusy(false);
      if (error instanceof ApiError && error.code === 'invalid_credentials') {
        // Hangi alanın yanlış olduğu söylenmez (hesap varlığı sızdırılmaz).
        form.fail('E-posta veya parola hatalı.');
      } else {
        form.failWith(error);
      }
    });
  };

  return (
    <div className="page page--narrow stack">
      <PageHeader title="Giriş yap" />
      {expired && <Note tone="info" title="Oturumunuz sona erdi" role="status"><p>Kaldığınız yere dönmek için yeniden giriş yapın.</p></Note>}
      <form id={form.formId} className="form" onSubmit={submit} noValidate>
        {form.summary !== null && <ErrorSummary title={form.summary} />}
        <Field label="E-posta" name="email" type="email" required autoComplete="username" value={form.values.email} onChange={form.set('email')} error={form.errors.email} />
        <Field
          label="Parola"
          name="password"
          type="password"
          required
          autoComplete="current-password"
          value={form.values.password}
          onChange={form.set('password')}
          error={form.errors.password}
        />
        <div className="row">
          <Button type="submit" variant="primary" busy={busy}>
            {busy ? 'Giriş yapılıyor…' : 'Giriş yap'}
          </Button>
        </div>
      </form>
      <p>
        Hesabınız yok mu? <Link to="/signup">Kayıt olun</Link>
      </p>
    </div>
  );
}

export function SignupPage() {
  const { state } = useSession();
  const form = useFormState({ displayName: '', email: '', password: '' });
  const [busy, setBusy] = useState(false);
  const [accepted, setAccepted] = useState<string | null>(null);

  if (state.status === 'authenticated') {
    return <Navigate to="/app" replace />;
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (busy) {
      return;
    }
    const fields: Record<string, string> = {};
    if (form.values.displayName.trim().length === 0) {
      fields.displayName = 'Adınızı girin.';
    }
    if (form.values.email.trim().length === 0) {
      fields.email = 'E-posta adresinizi girin.';
    }
    if (form.values.password.length < 12) {
      fields.password = 'Parola en az 12 karakter olmalıdır.';
    }
    if (Object.keys(fields).length > 0) {
      form.fail('Kayıt gönderilmedi: düzeltilmesi gereken alanlar var.', fields);
      return;
    }
    form.clear();
    setBusy(true);
    request<{ message: string }>('/api/auth/signup', {
      method: 'POST',
      body: { email: form.values.email.trim(), displayName: form.values.displayName.trim(), password: form.values.password },
    })
      .then(({ data }) => setAccepted(data.message))
      .catch((error: unknown) => form.failWith(error))
      .finally(() => setBusy(false));
  };

  if (accepted !== null) {
    return (
      <div className="page page--narrow stack">
        <PageHeader title="Kayıt alındı" />
        <Note tone="ok" role="status">
          <p>{accepted}</p>
        </Note>
        <p>Giriş yaptıktan sonra demo posta kutunuzdaki bağlantıyla e-posta adresinizi doğrulayın.</p>
        <div className="row">
          <LinkButton to="/login" variant="primary">
            Giriş yap
          </LinkButton>
        </div>
      </div>
    );
  }

  return (
    <div className="page page--narrow stack">
      <PageHeader title="Kayıt ol" />
      <p className="muted">Demo ortamı: gerçek e-posta gönderilmez. Doğrulama iletisi, giriş yaptıktan sonra demo posta kutunuzda görünür.</p>
      <form id={form.formId} className="form" onSubmit={submit} noValidate>
        {form.summary !== null && <ErrorSummary title={form.summary} />}
        <Field label="Ad soyad" name="displayName" required autoComplete="name" maxLength={100} value={form.values.displayName} onChange={form.set('displayName')} error={form.errors.displayName} />
        <Field label="E-posta" name="email" type="email" required autoComplete="email" maxLength={254} value={form.values.email} onChange={form.set('email')} error={form.errors.email} />
        <Field
          label="Parola"
          name="password"
          type="password"
          required
          autoComplete="new-password"
          maxLength={200}
          hint="En az 12 karakter."
          value={form.values.password}
          onChange={form.set('password')}
          error={form.errors.password}
        />
        <div className="row">
          <Button type="submit" variant="primary" busy={busy}>
            {busy ? 'Gönderiliyor…' : 'Kayıt ol'}
          </Button>
        </div>
      </form>
      <p>
        Hesabınız var mı? <Link to="/login">Giriş yapın</Link>
      </p>
    </div>
  );
}

export function NotFoundPage() {
  return (
    <div className="full-state">
      <PageHeader title="Sayfa bulunamadı" />
      <p>Bu adreste bir sayfa yok.</p>
      <p>
        <Link to="/">Ana sayfaya dön</Link>
      </p>
    </div>
  );
}

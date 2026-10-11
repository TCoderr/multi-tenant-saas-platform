// Şirketsiz hesap ekranları: şirketlerim, demo posta kutusu, e-posta doğrulama, davet kabul.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom';
import { ApiError, get, request } from '../api/client';
import { queryKeys } from '../api/queryKeys';
import type { InviteLookup, Items, MailboxItem, MailboxMessage, Membership } from '../api/types';
import { hashToken, safeInternalPath, useFormState } from '../components/form';
import { useNotice } from '../components/notice';
import {
  Badge,
  Button,
  DataList,
  DemoTag,
  EmptyState,
  ErrorState,
  ErrorSummary,
  Field,
  LinkButton,
  Loading,
  Note,
  PageHeader,
  problemText,
  type Column,
} from '../components/ui';
import { formatDateTime, inviteStateLabel, roleDescription, roleLabel, tenantStatusLabel, tenantStatusTone } from '../lib/format';
import { useSession, useUser } from '../session/SessionProvider';

/* ---------- Şirketlerim ---------- */

function ResendVerification() {
  const resend = useMutation({ mutationFn: () => request<void>('/api/auth/verify-email/resend', { method: 'POST' }) });
  return (
    <Note tone="warn" title="E-posta adresiniz henüz doğrulanmadı">
      <p>
        Şirket oluşturmak ve davet kabul etmek için önce adresinizi doğrulayın. Doğrulama bağlantısı <Link to="/mail">demo posta kutunuzda</Link>.
      </p>
      <div className="row">
        <Button small onClick={() => resend.mutate()} busy={resend.isPending}>
          Doğrulama iletisini yeniden gönder
        </Button>
        {resend.isSuccess && <span role="status">İstek alındı. İleti birazdan posta kutunuzda görünür.</span>}
        {resend.isError && <span className="field__error" role="alert">{problemText(resend.error)}</span>}
      </div>
    </Note>
  );
}

export function CompaniesPage() {
  const user = useUser();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const form = useFormState({ name: '' });

  const memberships = useQuery({
    queryKey: queryKeys.memberships(user.id),
    queryFn: ({ signal }) => get<Items<Membership>>('/api/me/memberships', signal),
  });

  const create = useMutation({
    mutationFn: (name: string) => request<{ tenantId: string }>('/api/tenants', { method: 'POST', body: { name } }),
    onSuccess: ({ data }) => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.memberships(user.id) });
      void navigate(`/t/${data.tenantId}/tickets`);
    },
    onError: (error) => form.failWith(error),
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!user.emailVerified || create.isPending) {
      return;
    }
    const name = form.values.name.trim();
    if (name.length < 2) {
      form.fail('Şirket oluşturulamadı.', { name: 'Şirket adı en az 2 karakter olmalıdır.' });
      return;
    }
    form.clear();
    create.mutate(name);
  };

  const columns: Column<Membership>[] = [
    {
      key: 'name',
      header: 'Şirket',
      rowHeader: true,
      cell: (membership) => <Link to={`/t/${membership.tenantId}/tickets`}>{membership.tenantName}</Link>,
    },
    { key: 'role', header: 'Rolünüz', cell: (membership) => roleLabel[membership.role] },
    {
      key: 'status',
      header: 'Durum',
      cell: (membership) => <Badge tone={tenantStatusTone[membership.tenantStatus]}>{tenantStatusLabel[membership.tenantStatus]}</Badge>,
    },
  ];

  return (
    <div className="page stack-lg">
      <PageHeader title="Şirketlerim" />
      {!user.emailVerified && <ResendVerification />}

      <section className="stack" aria-labelledby="memberships-title">
        <h2 id="memberships-title" className="visually-hidden">
          Üyesi olduğunuz şirketler
        </h2>
        {memberships.isPending ? (
          <Loading label="Şirketler yükleniyor…" />
        ) : memberships.isError ? (
          <ErrorState error={memberships.error} onRetry={() => void memberships.refetch()} />
        ) : memberships.data.items.length === 0 ? (
          <EmptyState title="Henüz bir şirkete üye değilsiniz.">
            <p>Aşağıdan ilk şirketinizi oluşturun ya da size gönderilen davet bağlantısını demo posta kutunuzdan açın.</p>
          </EmptyState>
        ) : (
          <DataList caption="Üyesi olduğunuz şirketler" columns={columns} rows={memberships.data.items} rowKey={(membership) => membership.tenantId} />
        )}
      </section>

      <section className="section stack" aria-labelledby="create-title">
        <h2 id="create-title">Yeni şirket oluştur</h2>
        <p className="muted">Oluşturan kişi şirketin Owner'ı olur. Şirket deneme planıyla başlar.</p>
        <form id={form.formId} className="form" onSubmit={submit} noValidate>
          {form.summary !== null && <ErrorSummary title={form.summary} />}
          <Field label="Şirket adı" name="name" required maxLength={120} value={form.values.name} onChange={form.set('name')} error={form.errors.name} />
          <div className="row">
            <Button
              type="submit"
              variant="primary"
              busy={create.isPending}
              disabledReason={user.emailVerified ? null : 'Şirket oluşturmak için önce e-posta adresinizi doğrulayın.'}
            >
              Şirket oluştur
            </Button>
          </div>
        </form>
      </section>
    </div>
  );
}

/* ---------- Demo posta kutusu ---------- */

const mailKindLabel: Record<string, string> = {
  email_verification: 'E-posta doğrulama',
  invite: 'Davet',
  invite_accepted: 'Davet kabul edildi',
  ownership_offer: 'Sahiplik teklifi',
  subscription_changed: 'Abonelik',
};

const mailActionLabel: Record<string, string> = {
  email_verification: 'E-posta adresimi doğrula',
  invite: 'Daveti aç',
  invite_accepted: 'Ekibi aç',
  ownership_offer: 'Teklifi aç',
  subscription_changed: 'Planı aç',
};

function MailMessage({ userId, messageId }: { userId: string; messageId: string }) {
  const message = useQuery({
    queryKey: queryKeys.mailboxMessage(userId, messageId),
    queryFn: ({ signal }) => get<MailboxMessage>(`/api/me/mailbox/${messageId}`, signal),
  });
  if (message.isPending) {
    return <Loading label="İleti yükleniyor…" />;
  }
  if (message.isError) {
    return <ErrorState error={message.error} onRetry={() => void message.refetch()} notFoundTitle="İleti bulunamadı" />;
  }
  const actionPath = safeInternalPath(message.data.actionPath);
  return (
    <article className="panel stack" aria-labelledby="mail-subject">
      <h2 id="mail-subject" className="break">
        {message.data.subject}
      </h2>
      <p className="muted small">
        {mailKindLabel[message.data.kind] ?? message.data.kind} · {formatDateTime(message.data.createdAt)}
      </p>
      <p className="pre-wrap">{message.data.body}</p>
      {actionPath !== null && (
        <div className="row">
          <LinkButton to={actionPath} variant="primary">
            {mailActionLabel[message.data.kind] ?? 'Bağlantıyı aç'}
          </LinkButton>
        </div>
      )}
    </article>
  );
}

export function MailboxPage() {
  const user = useUser();
  const { messageId } = useParams();
  const list = useQuery({
    queryKey: queryKeys.mailbox(user.id),
    queryFn: ({ signal }) => get<Items<MailboxItem>>('/api/me/mailbox', signal),
    refetchInterval: 4000,
  });

  const columns: Column<MailboxItem>[] = [
    { key: 'subject', header: 'Konu', rowHeader: true, cell: (item) => <Link to={`/mail/${item.id}`}>{item.subject}</Link> },
    { key: 'kind', header: 'Tür', cell: (item) => mailKindLabel[item.kind] ?? item.kind },
    { key: 'at', header: 'Tarih', cell: (item) => <span className="num">{formatDateTime(item.createdAt)}</span> },
  ];

  return (
    <div className="page stack">
      <PageHeader title="Posta kutusu">
        <p className="row">
          <DemoTag label="Demo posta kutusu" />
          <span className="muted">Gerçek e-posta gönderilmez; {user.email} adresine giden iletiler yalnız burada görünür.</span>
        </p>
      </PageHeader>
      {messageId !== undefined && (
        <>
          <MailMessage userId={user.id} messageId={messageId} />
          <p>
            <Link to="/mail">Tüm iletilere dön</Link>
          </p>
        </>
      )}
      {list.isPending ? (
        <Loading label="İletiler yükleniyor…" />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} />
      ) : list.data.items.length === 0 ? (
        <EmptyState title="Henüz ileti yok.">
          <p>Doğrulama, davet ve abonelik iletileri gönderildiklerinde burada görünür. Liste kendiliğinden yenilenir.</p>
        </EmptyState>
      ) : (
        <DataList caption="İletiler" columns={columns} rows={list.data.items} rowKey={(item) => item.id} />
      )}
    </div>
  );
}

/* ---------- E-posta doğrulama ---------- */

export function VerifyEmailPage() {
  const location = useLocation();
  const { refresh } = useSession();
  const user = useUser();
  const [token] = useState(() => hashToken(location.hash));
  const started = useRef(false);
  const verify = useMutation({
    mutationFn: (value: string) => request<void>('/api/auth/verify-email', { method: 'POST', body: { token: value } }),
    onSuccess: () => refresh(),
  });
  const { mutate } = verify;

  useEffect(() => {
    // Token tek kullanımlıktır: geliştirme modundaki çift effect çağrısında da yalnız bir kez gönderilir.
    if (token !== null && !started.current) {
      started.current = true;
      mutate(token);
    }
  }, [token, mutate]);

  return (
    <div className="page page--narrow stack">
      <PageHeader title="E-posta doğrulama" />
      {token === null ? (
        <Note tone="critical" title="Bağlantı eksik" role="alert">
          <p>Bu adreste doğrulama belirteci yok. Bağlantıyı demo posta kutunuzdaki iletiden açın.</p>
        </Note>
      ) : verify.isSuccess ? (
        <Note tone="ok" title="E-posta adresiniz doğrulandı" role="status">
          <p>Artık şirket oluşturabilir ve davetleri kabul edebilirsiniz.</p>
        </Note>
      ) : verify.isError ? (
        user.emailVerified ? (
          <Note tone="info" title="Adresiniz zaten doğrulanmış" role="status">
            <p>Bu bağlantı daha önce kullanılmış. Yapmanız gereken bir şey yok.</p>
          </Note>
        ) : (
          <Note tone="critical" title="Doğrulama başarısız" role="alert">
            <p>{problemText(verify.error)}</p>
            <p>Şirketlerim sayfasından yeni bir doğrulama iletisi isteyebilirsiniz.</p>
          </Note>
        )
      ) : (
        <Loading label="Doğrulanıyor…" />
      )}
      <div className="row">
        <LinkButton to="/app" variant="primary">
          Şirketlerime git
        </LinkButton>
        <LinkButton to="/mail">Posta kutusu</LinkButton>
      </div>
    </div>
  );
}

/* ---------- Davet kabul ---------- */

function inviteAcceptError(error: unknown): string {
  if (error instanceof ApiError && error.code === 'quota_exceeded') {
    return 'Şirketin koltuk limiti dolu. Yöneticinizden planı yükseltmesini veya bir koltuk açmasını isteyin.';
  }
  return problemText(error);
}

export function InviteAcceptPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const notify = useNotice();
  const user = useUser();
  const token = hashToken(location.hash);

  const lookup = useQuery({
    // Önbellek anahtarında token'ın kendisi değil kısa bir izi durur.
    queryKey: queryKeys.inviteLookup(user.id, token === null ? 'yok' : `${token.length}:${token.slice(-6)}`),
    queryFn: async () => (await request<InviteLookup>('/api/invites/lookup', { method: 'POST', body: { token } })).data,
    enabled: token !== null,
    staleTime: 0,
  });

  const accept = useMutation({
    mutationFn: () => request<{ tenantId: string }>('/api/invites/accept', { method: 'POST', body: { token } }),
    onSuccess: ({ data }) => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.memberships(user.id) });
      notify('Davet kabul edildi.');
      void navigate(`/t/${data.tenantId}/tickets`);
    },
    onError: () => void lookup.refetch(),
  });

  let body;
  if (token === null) {
    body = (
      <Note tone="critical" title="Bağlantı eksik" role="alert">
        <p>Bu adreste davet belirteci yok. Bağlantıyı demo posta kutunuzdaki davet iletisinden açın.</p>
      </Note>
    );
  } else if (lookup.isPending) {
    body = <Loading label="Davet okunuyor…" />;
  } else if (lookup.isError) {
    body =
      lookup.error instanceof ApiError && lookup.error.code === 'invite_not_found' ? (
        <Note tone="critical" title="Davet bulunamadı" role="alert">
          <p>Bağlantı geçersiz ya da davet artık yok. Yöneticinizden yeni davet isteyin.</p>
        </Note>
      ) : (
        <ErrorState error={lookup.error} onRetry={() => void lookup.refetch()} />
      );
  } else {
    const invite = lookup.data;
    const blocked =
      invite.state === 'Expired'
        ? 'Davetin süresi dolmuş. Yöneticinizden yeni davet isteyin.'
        : invite.state === 'Revoked'
          ? 'Davet iptal edilmiş ya da şirket şu anda üye kabul etmiyor. Yöneticinizden yeni davet isteyin.'
          : invite.state === 'Accepted'
            ? 'Bu davet daha önce kabul edilmiş.'
            : !invite.emailMatches
              ? `Bu davet ${invite.invitedEmail} adresine gönderilmiş; siz ${user.email} ile giriş yaptınız. Davet edilen hesapla giriş yapın.`
              : !invite.emailVerified
                ? 'Daveti kabul etmek için önce e-posta adresinizi doğrulayın (posta kutunuzdaki doğrulama iletisi).'
                : null;
    body = (
      <div className="stack">
        <dl className="deflist">
          <dt>Şirket</dt>
          <dd>
            <strong>{invite.tenantName}</strong>
          </dd>
          <dt>Verilecek rol</dt>
          <dd>
            {roleLabel[invite.role]} — {roleDescription[invite.role]}
          </dd>
          <dt>Davet edilen adres</dt>
          <dd>{invite.invitedEmail}</dd>
          <dt>Durum</dt>
          <dd>{inviteStateLabel[invite.state]}</dd>
          <dt>Son geçerlilik</dt>
          <dd>{formatDateTime(invite.expiresAt)}</dd>
        </dl>
        {accept.isError && <ErrorSummary title={inviteAcceptError(accept.error)} />}
        <div className="row row--top">
          <Button variant="primary" onClick={() => accept.mutate()} busy={accept.isPending} disabledReason={blocked}>
            Daveti kabul et
          </Button>
          <LinkButton to="/app">Şirketlerim</LinkButton>
          {!invite.emailVerified && <LinkButton to="/mail">Posta kutusu</LinkButton>}
        </div>
      </div>
    );
  }

  return (
    <div className="page stack">
      <PageHeader title="Şirket daveti" />
      {body}
    </div>
  );
}

// Ekip: üyeler ve roller, davetler (koltuk limiti), sahiplik teklifi.

import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { get, request } from '../api/client';
import type { Invite, Items, Member, OwnershipOffer, TenantRole } from '../api/types';
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
  UsageMeter,
  problemText,
  type Column,
} from '../components/ui';
import { formatDateTime, inviteStateLabel, inviteStateTone, roleDescription, roleLabel } from '../lib/format';
import { ConflictNote, QuotaNote, WsHeader, isConflict, quotaFromError, type QuotaInfo } from '../workspace/common';
import { useTenantMutation, useWorkspace } from '../workspace/context';

const GRANTABLE: Record<TenantRole, TenantRole[]> = {
  Owner: ['Admin', 'Agent', 'Viewer'],
  Admin: ['Agent', 'Viewer'],
  Agent: [],
  Viewer: [],
};

/** Yalnız gösterim: Admin yalnız Agent/Viewer üyeleri yönetebilir. Kararı sunucu verir. */
function manageReason(actor: TenantRole, target: Member, manageDenied: string | null): string | null {
  if (manageDenied !== null) {
    return manageDenied;
  }
  if (actor === 'Admin' && (target.role === 'Owner' || target.role === 'Admin')) {
    return 'Owner ve Admin üyeleri yalnız Owner yönetebilir.';
  }
  return null;
}

function MemberActions({ member, onRemove }: { member: Member; onRemove: (member: Member) => void }) {
  const ws = useWorkspace();
  const notify = useNotice();
  const [role, setRole] = useState<TenantRole>(member.role);
  const [error, setError] = useState<unknown>(null);
  const membersKey = ws.key('members');

  const change = useTenantMutation<{ version: number }, TenantRole>({
    mutationFn: async (next) =>
      (await request<{ version: number }>(ws.api(`/members/${member.userId}`), { method: 'PATCH', body: { role: next }, ifMatch: member.version })).data,
    invalidate: [membersKey, ws.key('assignable-members')],
    onSuccess: (_, next) => {
      setError(null);
      notify(`${member.displayName}: rol ${roleLabel[next]} oldu.`);
    },
  });

  const reason = manageReason(ws.summary.role, member, ws.denied('members.manage'));
  if (reason !== null) {
    return <span className="muted small">{reason}</span>;
  }

  const options = GRANTABLE[ws.summary.role];
  return (
    <div className="stack">
      <form
        className="row"
        onSubmit={(event: FormEvent) => {
          event.preventDefault();
          if (role !== member.role && !change.isPending) {
            change.mutate(role, { onError: setError });
          }
        }}
      >
        <label className="visually-hidden" htmlFor={`role-${member.userId}`}>
          {member.displayName} için rol
        </label>
        <select id={`role-${member.userId}`} className="select select--auto" value={role} onChange={(event) => setRole(event.target.value as TenantRole)}>
          {member.role === 'Owner' && <option value="Owner">Owner</option>}
          {options.map((option) => (
            <option key={option} value={option}>
              {roleLabel[option]}
            </option>
          ))}
        </select>
        <Button type="submit" small busy={change.isPending}>
          Rolü kaydet
        </Button>
        {member.isSelf ? (
          <span className="muted small">
            Ayrılmak için <Link to={`/t/${ws.tenantId}/settings`}>Ayarlar</Link>
          </span>
        ) : (
          <Button small variant="danger" onClick={() => onRemove(member)} ariaLabel={`${member.displayName} üyesini kaldır`}>
            Kaldır
          </Button>
        )}
      </form>
      {error !== null &&
        (isConflict(error) ? (
          <ConflictNote
            onReload={() => {
              setError(null);
              setRole(member.role);
            }}
          />
        ) : (
          <span className="field__error" role="alert">
            {problemText(error)}
          </span>
        ))}
    </div>
  );
}

function MembersSection() {
  const ws = useWorkspace();
  const notify = useNotice();
  const [removing, setRemoving] = useState<Member | null>(null);

  const members = useQuery({ queryKey: ws.key('members'), queryFn: ({ signal }) => get<Items<Member>>(ws.api('/members'), signal) });

  const remove = useTenantMutation<void, Member>({
    mutationFn: async (member) => {
      await request<void>(ws.api(`/members/${member.userId}`), { method: 'DELETE' });
    },
    invalidate: [ws.key('members'), ws.key('assignable-members'), ws.key('tickets'), ws.key('ownership-offers')],
    onSuccess: (_, member) => {
      setRemoving(null);
      notify(`${member.displayName} şirketten kaldırıldı.`);
    },
  });

  const columns: Column<Member>[] = [
    {
      key: 'name',
      header: 'Üye',
      rowHeader: true,
      cell: (member) => (
        <>
          {member.displayName} {member.isSelf && <Badge tone="info">Siz</Badge>}
        </>
      ),
    },
    { key: 'email', header: 'E-posta', cell: (member) => (member.email !== null ? <span className="break">{member.email}</span> : <span className="muted">Rolünüz için gizli</span>) },
    { key: 'role', header: 'Rol', cell: (member) => <Badge>{roleLabel[member.role]}</Badge> },
    { key: 'joined', header: 'Katılma', cell: (member) => <span className="num">{formatDateTime(member.joinedAt)}</span> },
    { key: 'actions', header: 'Yönetim', cell: (member) => <MemberActions key={`${member.userId}:${member.version}`} member={member} onRemove={setRemoving} /> },
  ];

  return (
    <section className="stack" aria-labelledby="members-title">
      <h2 id="members-title">Üyeler</h2>
      <UsageMeter label="Koltuk kullanımı" used={ws.summary.usage.seatsUsed} limit={ws.summary.entitlements.seatLimit} unit="koltuk" />
      {members.isPending ? (
        <Loading label="Üyeler yükleniyor…" />
      ) : members.isError ? (
        <ErrorState error={members.error} onRetry={() => void members.refetch()} />
      ) : (
        <DataList caption="Üyeler" columns={columns} rows={members.data.items} rowKey={(member) => member.userId} />
      )}
      <p className="muted small">Owner rolü rol listesinden verilmez; yalnız sahiplik teklifi ve hedef üyenin kabulüyle geçer.</p>
      <Dialog
        open={removing !== null}
        title="Üyeyi kaldır"
        confirmLabel="Kaldır"
        danger
        busy={remove.isPending}
        error={remove.isError ? problemText(remove.error) : null}
        onClose={() => {
          setRemoving(null);
          remove.reset();
        }}
        onSubmit={() => {
          if (removing !== null) {
            remove.mutate(removing);
          }
        }}
      >
        <p>
          <strong>{removing?.displayName}</strong> şirketten kaldırılacak; erişimi hemen sona erer ve üzerindeki talepler boşa alınır.
        </p>
      </Dialog>
    </section>
  );
}

function InvitesSection() {
  const ws = useWorkspace();
  const notify = useNotice();
  const grantable = GRANTABLE[ws.summary.role];
  const form = useFormState({ email: '', role: (grantable.at(-1) ?? 'Viewer') as string });
  const [quota, setQuota] = useState<QuotaInfo | null>(null);
  const canList = ws.can('members.manage');

  const invites = useQuery({
    queryKey: ws.key('invites'),
    queryFn: ({ signal }) => get<Items<Invite>>(ws.api('/invites'), signal),
    enabled: canList,
    // "Gönderiliyor" durumundaki davet, ileti üretildiğinde "Bekliyor" olur.
    refetchInterval: (query) => (query.state.data?.items.some((invite) => invite.state === 'Sending') === true ? 2000 : false),
  });

  const create = useTenantMutation<Invite, { email: string; role: string }>({
    mutationFn: async (body) => (await request<Invite>(ws.api('/invites'), { method: 'POST', body })).data,
    invalidate: [ws.key('invites')],
    onSuccess: (invite) => {
      form.setValues({ email: '', role: form.values.role });
      notify(`${invite.email} adresine davet oluşturuldu.`);
    },
  });

  const revoke = useTenantMutation<void, Invite>({
    mutationFn: async (invite) => {
      await request<void>(ws.api(`/invites/${invite.id}`), { method: 'DELETE' });
    },
    invalidate: [ws.key('invites')],
    onSuccess: (_, invite) => notify(`${invite.email} daveti iptal edildi.`),
  });

  const inviteDenied = ws.denied('members.invite');
  const usage = ws.summary.usage;
  const seatLimit = ws.summary.entitlements.seatLimit;
  const knownQuota: QuotaInfo | null = quota ?? (usage.seatsUsed >= seatLimit ? { metric: 'seats', limit: seatLimit, used: usage.seatsUsed, resetsAt: null } : null);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (inviteDenied !== null || create.isPending) {
      return;
    }
    if (form.values.email.trim().length === 0) {
      form.fail('Davet gönderilmedi.', { email: 'E-posta adresi girin.' });
      return;
    }
    form.clear();
    setQuota(null);
    create.mutate(
      { email: form.values.email.trim(), role: form.values.role },
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

  const columns: Column<Invite>[] = [
    { key: 'email', header: 'E-posta', rowHeader: true, cell: (invite) => <span className="break">{invite.email}</span> },
    { key: 'role', header: 'Rol', cell: (invite) => roleLabel[invite.role] },
    { key: 'state', header: 'Durum', cell: (invite) => <Badge tone={inviteStateTone[invite.state]}>{inviteStateLabel[invite.state]}</Badge> },
    { key: 'by', header: 'Davet eden', cell: (invite) => invite.invitedByName ?? '—' },
    { key: 'expires', header: 'Son geçerlilik', cell: (invite) => <span className="num">{formatDateTime(invite.expiresAt)}</span> },
    {
      key: 'actions',
      header: 'İşlem',
      cell: (invite) =>
        invite.state === 'Pending' || invite.state === 'Sending' ? (
          <Button small variant="danger" busy={revoke.isPending && revoke.variables?.id === invite.id} onClick={() => revoke.mutate(invite)} ariaLabel={`${invite.email} davetini iptal et`}>
            İptal et
          </Button>
        ) : (
          <span className="muted">—</span>
        ),
    },
  ];

  return (
    <section className="section stack" aria-labelledby="invites-title">
      <h2 id="invites-title">Davetler</h2>
      <p className="muted small">
        Kullanılan koltuk: <span className="num">{usage.seatsUsed}</span> / <span className="num">{seatLimit}</span> · Bekleyen davet:{' '}
        <span className="num">{usage.pendingInvites}</span>. Demo: davet iletisi gerçek e-posta olarak gönderilmez; davet edilen kişinin demo posta
        kutusuna düşer.
      </p>
      {knownQuota !== null && <QuotaNote quota={knownQuota} />}
      <form id={form.formId} className="form" onSubmit={submit} noValidate aria-label="Üye davet et">
        {form.summary !== null && <ErrorSummary title={form.summary} />}
        <Field label="Davet edilecek e-posta" name="email" type="email" required maxLength={254} value={form.values.email} onChange={form.set('email')} error={form.errors.email} />
        <Field
          label="Verilecek rol"
          name="role"
          as="select"
          value={form.values.role}
          onChange={form.set('role')}
          error={form.errors.role}
          hint={roleDescription[(form.values.role as TenantRole) in roleDescription ? (form.values.role as TenantRole) : 'Viewer']}
        >
          {(grantable.length > 0 ? grantable : (['Viewer'] as TenantRole[])).map((role) => (
            <option key={role} value={role}>
              {roleLabel[role]}
            </option>
          ))}
        </Field>
        <div className="row row--top">
          <Button type="submit" variant="primary" busy={create.isPending} disabledReason={inviteDenied}>
            Davet gönder
          </Button>
        </div>
      </form>
      {revoke.isError && <ErrorSummary title={problemText(revoke.error)} />}
      {!canList ? (
        <p className="muted small">Davet listesini görmek için Owner veya Admin rolü gerekir.</p>
      ) : invites.isPending ? (
        <Loading label="Davetler yükleniyor…" />
      ) : invites.isError ? (
        <ErrorState error={invites.error} onRetry={() => void invites.refetch()} />
      ) : invites.data.items.length === 0 ? (
        <EmptyState title="Henüz davet yok." />
      ) : (
        <DataList caption="Davetler" columns={columns} rows={invites.data.items} rowKey={(invite) => invite.id} />
      )}
    </section>
  );
}

function OwnershipSection() {
  const ws = useWorkspace();
  const notify = useNotice();
  const [target, setTarget] = useState('');
  const [demoteSelf, setDemoteSelf] = useState(true);
  const [error, setError] = useState<unknown>(null);

  const members = useQuery({ queryKey: ws.key('members'), queryFn: ({ signal }) => get<Items<Member>>(ws.api('/members'), signal) });
  const offers = useQuery({
    queryKey: ws.key('ownership-offers'),
    queryFn: ({ signal }) => get<Items<OwnershipOffer>>(ws.api('/ownership-offers'), signal),
  });
  const invalidate = [ws.key('ownership-offers'), ws.key('members')];

  const offer = useTenantMutation<unknown, { toUserId: string; demoteSelf: boolean }>({
    mutationFn: async (body) => (await request<unknown>(ws.api('/ownership-offers'), { method: 'POST', body })).data,
    invalidate,
    onSuccess: () => {
      setTarget('');
      setError(null);
      notify('Sahiplik teklifi gönderildi.');
    },
  });
  const accept = useTenantMutation<unknown, string>({
    mutationFn: async (id) => (await request<unknown>(ws.api(`/ownership-offers/${id}/accept`), { method: 'POST' })).data,
    invalidate,
    onSuccess: () => {
      setError(null);
      notify('Sahiplik kabul edildi: artık Owner rolündesiniz.');
    },
  });
  const withdraw = useTenantMutation<void, string>({
    mutationFn: async (id) => {
      await request<void>(ws.api(`/ownership-offers/${id}`), { method: 'DELETE' });
    },
    invalidate,
    onSuccess: () => {
      setError(null);
      notify('Sahiplik teklifi geri çekildi.');
    },
  });

  const offerDenied = ws.denied('ownership.offer');
  const candidates = (members.data?.items ?? []).filter((member) => !member.isSelf && member.role !== 'Owner');

  return (
    <section className="section stack" aria-labelledby="ownership-title">
      <h2 id="ownership-title">Sahiplik</h2>
      <p className="muted">
        Owner rolü yalnız teklif ve hedef üyenin kabulüyle geçer. Şirkette her zaman en az bir Owner kalır.
      </p>
      {error !== null && <ErrorSummary title={problemText(error)} />}
      {offers.isSuccess && offers.data.items.length > 0 && (
        <ul className="cards" aria-label="Bekleyen sahiplik teklifleri">
          {offers.data.items.map((item) => (
            <li key={item.id} className="card stack">
              <p>
                <strong>{item.fromName}</strong> → <strong>{item.toName}</strong>:{' '}
                {item.demoteSelf ? 'sahipliği devretmeyi (kendisi Admin olur)' : 'sahipliği paylaşmayı (kendisi Owner kalır)'} teklif etti. Son
                geçerlilik: <span className="num">{formatDateTime(item.expiresAt)}</span>
              </p>
              <div className="row row--top">
                {item.toUserId === ws.userId && (
                  <Button
                    variant="primary"
                    small
                    busy={accept.isPending}
                    disabledReason={item.canAccept ? null : 'Şirket şu anda salt-okunur; teklif kabul edilemez.'}
                    onClick={() => accept.mutate(item.id, { onError: setError })}
                  >
                    Sahipliği kabul et
                  </Button>
                )}
                {item.fromUserId === ws.userId && (
                  <Button small busy={withdraw.isPending} onClick={() => withdraw.mutate(item.id, { onError: setError })}>
                    Teklifi geri çek
                  </Button>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
      <form
        className="form"
        aria-label="Sahiplik teklif et"
        onSubmit={(event: FormEvent) => {
          event.preventDefault();
          if (offerDenied === null && target.length > 0 && !offer.isPending) {
            offer.mutate({ toUserId: target, demoteSelf }, { onError: setError });
          }
        }}
      >
        <Field label="Sahiplik teklif edilecek üye" name="toUserId" as="select" required value={target} onChange={setTarget} disabled={offerDenied !== null}>
          <option value="">Üye seçin</option>
          {candidates.map((member) => (
            <option key={member.userId} value={member.userId}>
              {member.displayName} ({roleLabel[member.role]})
            </option>
          ))}
        </Field>
        <label className="check">
          <input type="checkbox" checked={demoteSelf} disabled={offerDenied !== null} onChange={(event) => setDemoteSelf(event.target.checked)} />
          <span>Kabul edilince kendi rolüm Admin olsun (devret). İşaretli değilse sahiplik paylaşılır.</span>
        </label>
        <div className="row row--top">
          <Button type="submit" busy={offer.isPending} disabledReason={offerDenied ?? (target.length === 0 ? 'Önce bir üye seçin.' : null)}>
            Sahiplik teklif et
          </Button>
        </div>
      </form>
    </section>
  );
}

export function TeamPage() {
  const ws = useWorkspace();
  return (
    <div className="stack-lg">
      <WsHeader title="Ekip" />
      {!ws.can('members.manage') && (
        <Note tone="info">
          <p>
            Rolünüz: {roleLabel[ws.summary.role]}. Üye yönetimi ve davet için Owner veya Admin rolü gerekir; aşağıdaki işlemler bu yüzden kapalı
            görünür.
          </p>
        </Note>
      )}
      <MembersSection />
      <InvitesSection />
      <OwnershipSection />
    </div>
  );
}

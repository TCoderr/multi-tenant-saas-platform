// Yerleşimler: herkese açık, hesap (şirketsiz) ve çalışma alanı (kulvar şeridi + gezinme + içerik).

import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link, NavLink, Navigate, Outlet, useLocation, useParams } from 'react-router-dom';
import { ApiError, request } from '../api/client';
import { queryKeys, tenantOfKey } from '../api/queryKeys';
import type { WorkspaceSummary } from '../api/types';
import { Button, DemoTag, ErrorState, Loading, OfflineBand, PageHeader, problemText } from '../components/ui';
import { formatDate, formatDateTime, laneIndex, monogram, roleLabel, subscriptionStatusLabel } from '../lib/format';
import { useSession, useUser } from '../session/SessionProvider';
import { LeaveCompany } from '../workspace/LeaveCompany';
import { WorkspaceContext, buildWorkspaceValue } from '../workspace/context';

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function SkipLink() {
  return (
    <a className="skip-link" href="#main">
      İçeriğe atla
    </a>
  );
}

export function Brand({ to = '/' }: { to?: string }) {
  return (
    <span className="row">
      <Link to={to} className="brand">
        Kulvar
      </Link>
      <DemoTag />
    </span>
  );
}

export function Footer() {
  return (
    <footer className="footer">
      Kulvar bir demo üründür: gerçek e-posta, SMS veya ödeme gönderilmez; görünen tüm kişi ve şirket verileri sentetiktir.
    </footer>
  );
}

function LogoutButton({ variant = 'default' }: { variant?: 'default' | 'link' }) {
  const { logout } = useSession();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const run = () => {
    setBusy(true);
    setError(null);
    logout().catch((reason: unknown) => {
      setError(problemText(reason));
      setBusy(false);
    });
  };
  return (
    <>
      <Button variant={variant} small onClick={run} busy={busy}>
        Çıkış yap
      </Button>
      {error !== null && (
        <span className="field__error" role="alert">
          {error}
        </span>
      )}
    </>
  );
}

/* ---------- Oturum zorunluluğu ---------- */

export function RequireAuth() {
  const { state, refresh } = useSession();
  const location = useLocation();
  if (state.status === 'loading') {
    return (
      <div className="full-state">
        <Loading label="Oturum denetleniyor…" />
      </div>
    );
  }
  if (state.status === 'error') {
    return (
      <div className="full-state">
        <ErrorState error={state.error} onRetry={() => void refresh()} />
      </div>
    );
  }
  if (state.status === 'anonymous') {
    // Geldiği yol yönlendirme durumunda (history.state) taşınır: sorgu dizesine yazılmaz, sunucuya gitmez.
    const from = `${location.pathname}${location.search}${location.hash}`;
    return <Navigate to="/login" replace state={state.reason === 'logout' ? null : { from, expired: state.reason === 'expired' }} />;
  }
  return <Outlet />;
}

/* ---------- Herkese açık ---------- */

export function PublicLayout() {
  const { user } = useSession();
  return (
    <>
      <SkipLink />
      <OfflineBand />
      <header className="topbar">
        <Brand />
        <nav aria-label="Genel">
          <ul className="topbar__nav">
            <li>
              <NavLink to="/" end>
                Ürün ve planlar
              </NavLink>
            </li>
            {user === null ? (
              <>
                <li>
                  <NavLink to="/login">Giriş yap</NavLink>
                </li>
                <li>
                  <NavLink to="/signup">Kayıt ol</NavLink>
                </li>
              </>
            ) : (
              <li>
                <NavLink to="/app">Şirketlerim</NavLink>
              </li>
            )}
          </ul>
        </nav>
      </header>
      <main id="main" tabIndex={-1}>
        <Outlet />
      </main>
      <Footer />
    </>
  );
}

/* ---------- Hesap (şirketsiz): şirketlerim, posta kutusu, platform ---------- */

export function AccountLayout() {
  const user = useUser();
  return (
    <>
      <SkipLink />
      <OfflineBand />
      <header className="topbar">
        <Brand to="/app" />
        <nav aria-label="Hesap">
          <ul className="topbar__nav">
            <li>
              <NavLink to="/app">Şirketlerim</NavLink>
            </li>
            <li>
              <NavLink to="/mail">Posta kutusu</NavLink>
            </li>
            {user.isPlatformOperator && (
              <li>
                <NavLink to="/platform">Platform</NavLink>
              </li>
            )}
            <li className="muted small break">{user.displayName}</li>
            <li>
              <LogoutButton />
            </li>
          </ul>
        </nav>
      </header>
      <main id="main" tabIndex={-1}>
        <Outlet />
      </main>
      <Footer />
    </>
  );
}

/* ---------- Çalışma alanı ---------- */

function AccountShell({ children }: { children: ReactNode }) {
  return (
    <>
      <SkipLink />
      <header className="topbar">
        <Brand to="/app" />
        <nav aria-label="Hesap">
          <ul className="topbar__nav">
            <li>
              <Link to="/app">Şirketlerim</Link>
            </li>
            <li>
              <LogoutButton />
            </li>
          </ul>
        </nav>
      </header>
      <main id="main" tabIndex={-1}>
        {children}
      </main>
    </>
  );
}

function TenantNotFound() {
  return (
    <AccountShell>
      <div className="full-state">
        <PageHeader title="Şirket bulunamadı" />
        <p>Bu adreste bir şirket yok ya da bu şirkete erişiminiz yok. Üyeliğiniz kaldırıldıysa şirketin verileri artık size gösterilmez.</p>
        <p>
          <Link to="/app">Şirketlerime dön</Link>
        </p>
      </div>
    </AccountShell>
  );
}

function SubscriptionBands({ summary, tenantId }: { summary: WorkspaceSummary; tenantId: string }) {
  const isOwner = summary.role === 'Owner';
  const planPath = `/t/${tenantId}/plan`;
  const bands: ReactNode[] = [];

  if (summary.tenant.status === 'DeletionPending') {
    bands.push(
      <div key="deletion" className="band band--critical" role="status">
        <span className="band__icon" aria-hidden="true">
          !
        </span>
        <span>
          Silme bekliyor: bu şirket {formatDate(summary.tenant.purgeAfter)} tarihinde kalıcı olarak silinecek. İçerik salt-okunur.
        </span>
        {isOwner ? <Link to={`/t/${tenantId}/settings`}>Silmeyi iptal et</Link> : <span>Silmeyi yalnız Owner iptal edebilir.</span>}
      </div>,
    );
  }

  const status = summary.subscription.status;
  if (status === 'PastDue') {
    bands.push(
      <div key="pastdue" className="band band--warn" role="status">
        <span className="band__icon" aria-hidden="true">
          !
        </span>
        <span>
          Ödeme alınamadı. Ödemesiz dönem {formatDate(summary.subscription.graceEndsAt)} tarihinde biter; sonrasında içerik salt-okunur olur.
        </span>
        {isOwner ? <Link to={planPath}>Planı güncelle</Link> : <span>Planı yalnız Owner güncelleyebilir.</span>}
      </div>,
    );
  } else if (status === 'Suspended' || status === 'Cancelled') {
    bands.push(
      <div key="recovery" className="band band--critical" role="status">
        <span className="band__icon" aria-hidden="true">
          !
        </span>
        <span>
          Abonelik {status === 'Cancelled' ? 'iptal edildi' : 'askıda'}: içerik salt-okunur. Verileriniz duruyor; yazma işlemleri kapalı.
        </span>
        {isOwner ? <Link to={planPath}>Plan seçerek yeniden etkinleştir</Link> : <span>Yeniden etkinleştirmeyi yalnız Owner yapabilir.</span>}
      </div>,
    );
  }
  return <>{bands}</>;
}

function LockedWorkspace({ summary }: { summary: WorkspaceSummary }) {
  return (
    <AccountShell>
      <div className="full-state stack">
        <PageHeader title="Şirket askıya alındı" context={summary.tenant.name} />
        <p>
          <strong>{summary.tenant.name}</strong> platform tarafından askıya alındı. Şirketin içeriği askı kalkana kadar kapalıdır; verileriniz
          silinmedi.
        </p>
        {summary.tenant.suspendedReason !== null && (
          <p>
            Platform notu: <span className="break">{summary.tenant.suspendedReason}</span>
          </p>
        )}
        <p className="muted">Rolünüz: {roleLabel[summary.role]}. Askı kalktığında bu sayfa kendiliğinden açılır.</p>
        <LeaveCompany />
        <p>
          <Link to="/app">Şirketlerime dön</Link>
        </p>
      </div>
    </AccountShell>
  );
}

const NAV_ITEMS: { to: string; label: string }[] = [
  { to: 'tickets', label: 'Talepler' },
  { to: 'customers', label: 'Müşteriler' },
  { to: 'team', label: 'Ekip' },
  { to: 'plan', label: 'Plan ve kullanım' },
  { to: 'audit', label: 'Denetim' },
  { to: 'exports', label: 'Dışa aktarma' },
  { to: 'settings', label: 'Ayarlar' },
];

function WorkspaceShell({ userId, tenantId }: { userId: string; tenantId: string }) {
  const user = useUser();
  const queryClient = useQueryClient();
  const location = useLocation();
  const [menu, setMenu] = useState({ open: false, at: location.key });
  const menuOpen = menu.open && menu.at === location.key;

  const query = useQuery({
    queryKey: queryKeys.workspace(userId, tenantId),
    queryFn: async ({ signal }) => (await request<WorkspaceSummary>(`/api/t/${tenantId}`, { signal })).data,
    refetchInterval: 30_000,
  });

  const gone = query.error instanceof ApiError && query.error.code === 'tenant_not_found';
  const locked = !gone && query.data?.accessMode === 'Locked';

  useEffect(() => {
    // Erişim bittiyse (üyelik kaldırıldı, şirket silindi) veya şirket kilitlendiyse o şirkete ait içerik bellekten atılır.
    if (gone || locked) {
      queryClient.removeQueries({ predicate: (cached) => tenantOfKey(cached.queryKey) === tenantId && cached.queryKey[4] !== 'workspace' });
    }
  }, [gone, locked, queryClient, tenantId]);

  // Rol, yetki listesi veya erişim modu değiştiyse (rol düşürme, abonelik/askı durumu) bu şirketin önbellekteki içeriği atılır ve
  // yeniden okunur: önceki yetkiyle alınmış veri (ör. maskesiz e-posta) yeni yetkiyle ekranda kalmaz. Boyamadan önce çalışır.
  const signature = query.data !== undefined ? [query.data.role, query.data.accessMode, ...query.data.permissions].join('|') : null;
  const lastSignature = useRef<string | null>(null);
  useLayoutEffect(() => {
    if (signature === null) {
      return;
    }
    if (lastSignature.current !== null && lastSignature.current !== signature) {
      void queryClient.resetQueries({ predicate: (cached) => tenantOfKey(cached.queryKey) === tenantId && cached.queryKey[4] !== 'workspace' });
    }
    lastSignature.current = signature;
  }, [signature, queryClient, tenantId]);

  const value = useMemo(
    () => (query.data !== undefined ? buildWorkspaceValue(userId, tenantId, query.data) : null),
    [query.data, tenantId, userId],
  );

  if (gone) {
    return <TenantNotFound />;
  }
  if (value === null) {
    return (
      <AccountShell>
        <div className="full-state">{query.isError ? <ErrorState error={query.error} onRetry={() => void query.refetch()} /> : <Loading label="Şirket yükleniyor…" />}</div>
      </AccountShell>
    );
  }

  const summary = value.summary;
  if (summary.accessMode === 'Locked') {
    return (
      <WorkspaceContext.Provider value={value}>
        <LockedWorkspace summary={summary} />
      </WorkspaceContext.Provider>
    );
  }

  const name = summary.tenant.name;
  return (
    <WorkspaceContext.Provider value={value}>
      <div className={`ws lane-${laneIndex(tenantId)}`} data-tenant-id={tenantId}>
        <SkipLink />
        <div className="lane" aria-hidden="true">
          <span className="lane__mono">{monogram(name)}</span>
          <span className="lane__pattern" />
          <span className="lane__name">{name}</span>
        </div>
        <div className="ws-nav">
          <div className="ws-nav__head">
            <Brand to="/app" />
            <div className="ws-nav__company" data-testid="company-name">
              {name}
            </div>
            <div className="small muted">
              Rol: <strong>{roleLabel[summary.role]}</strong> · {summary.subscription.planName} ({subscriptionStatusLabel[summary.subscription.status]}
              {summary.subscription.status === 'Trialing' && summary.subscription.trialEndsAt !== null
                ? `, ${formatDateTime(summary.subscription.trialEndsAt)} tarihine kadar`
                : ''}
              )
            </div>
            <span className="ws-nav__toggle">
              <button
                type="button"
                className="btn btn--small"
                aria-expanded={menuOpen}
                aria-controls="ws-menu"
                onClick={() => setMenu({ open: !menuOpen, at: location.key })}
              >
                Menü
              </button>
            </span>
          </div>
          <div id="ws-menu" className={menuOpen ? 'ws-nav__body is-open' : 'ws-nav__body'}>
            <nav aria-label={`${name} gezinmesi`}>
              <ul className="ws-nav__links">
                {NAV_ITEMS.map((item) => (
                  <li key={item.to}>
                    <NavLink to={`/t/${tenantId}/${item.to}`}>{item.label}</NavLink>
                  </li>
                ))}
              </ul>
            </nav>
            <div className="ws-nav__account">
              <div className="ws-nav__user">{user.displayName}</div>
              <nav aria-label="Hesap">
                <ul className="ws-nav__links">
                  <li>
                    <Link to="/app">Şirketlerim</Link>
                  </li>
                  <li>
                    <Link to="/mail">Posta kutusu</Link>
                  </li>
                </ul>
              </nav>
              <div className="ws-nav__user">
                <LogoutButton />
              </div>
            </div>
          </div>
        </div>
        <div className="ws-main">
          <OfflineBand />
          <SubscriptionBands summary={summary} tenantId={tenantId} />
          <main id="main" tabIndex={-1} className="ws-content">
            <Outlet />
          </main>
        </div>
      </div>
    </WorkspaceContext.Provider>
  );
}

export function WorkspaceLayout() {
  const { tenantId = '' } = useParams();
  const user = useUser();
  if (!GUID.test(tenantId)) {
    return <TenantNotFound />;
  }
  // Anahtar: şirket (veya kullanıcı) değişince alt ağaç baştan kurulur; önceki şirketin yerel form/diyalog durumu taşınmaz.
  const id = tenantId.toLowerCase();
  return <WorkspaceShell key={`${user.id}:${id}`} userId={user.id} tenantId={id} />;
}

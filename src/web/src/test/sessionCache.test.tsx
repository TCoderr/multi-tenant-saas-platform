import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { request } from '../api/client';
import { userOfKey } from '../api/queryKeys';
import { TENANT_A, USER_1, USER_2, json, makeCustomer, makeUser, makeWorkspace, mockApi, noContent, problem, renderApp } from './helpers';

const membership = { tenantId: TENANT_A, tenantName: 'Akasya Mobilya', role: 'Owner', tenantStatus: 'Active' };

function routes() {
  return {
    'GET /api/me': () => json(200, makeUser()),
    'GET /api/me/memberships': () => json(200, { items: [membership] }),
    'GET /api/me/mailbox': () => json(200, { items: [] }),
    [`GET /api/t/${TENANT_A}`]: () => json(200, makeWorkspace(TENANT_A, 'Akasya Mobilya')),
    [`GET /api/t/${TENANT_A}/customers`]: () => json(200, { items: [makeCustomer('a0000000-0000-4000-8000-000000000001', 'Deniz Arslan')], nextCursor: null }),
  };
}

function cachedUsers(client: ReturnType<typeof renderApp>['client']): (string | null)[] {
  return client
    .getQueryCache()
    .getAll()
    .map((query) => userOfKey(query.queryKey))
    .filter((id) => id !== null);
}

describe('oturum ve önbellek', () => {
  it('401 gelince önbellek tamamen temizlenir, girişe gidilir ve giriş sonrası kalınan yola dönülür', async () => {
    const api = mockApi(routes());
    const user = userEvent.setup();
    const app = renderApp(`/t/${TENANT_A}/customers`);
    expect(await screen.findByText('Deniz Arslan')).toBeInTheDocument();
    expect(cachedUsers(app.client)).toContain(USER_1);

    // Oturum sunucuda sona erdi: sıradaki istek 401 döner.
    api.on('GET /api/me/mailbox', () => problem(401, 'unauthenticated'));
    api.on('GET /api/me', () => problem(401, 'unauthenticated'));
    await act(async () => {
      await app.navigate('/mail');
    });

    expect(await screen.findByRole('heading', { name: 'Giriş yap' })).toBeInTheDocument();
    expect(screen.getByText('Oturumunuz sona erdi')).toBeInTheDocument();
    expect(screen.queryByText('Deniz Arslan')).not.toBeInTheDocument();
    expect(cachedUsers(app.client)).toEqual([]);

    // Yeniden giriş: kalınan yol (/mail) korunmuştur.
    api.on('POST /api/auth/login', () => json(200, { user: makeUser() }));
    api.on('GET /api/me/mailbox', () => json(200, { items: [] }));
    await user.type(screen.getByLabelText('E-posta'), 'ayse@ornek.test');
    await user.type(screen.getByLabelText('Parola'), 'gizli-parola-123');
    await user.click(screen.getByRole('button', { name: 'Giriş yap' }));
    expect(await screen.findByRole('heading', { name: 'Posta kutusu' })).toBeInTheDocument();
  });

  it('çıkışta sunucuya CSRF başlıklı istek gider ve önceki kullanıcının verisi bellekte kalmaz', async () => {
    const api = mockApi({ ...routes(), 'POST /api/auth/logout': () => noContent() });
    const user = userEvent.setup();
    const app = renderApp('/app');
    expect(await screen.findByRole('link', { name: 'Akasya Mobilya' })).toBeInTheDocument();
    expect(cachedUsers(app.client)).toContain(USER_1);

    await user.click(screen.getByRole('button', { name: 'Çıkış yap' }));

    expect(await screen.findByRole('heading', { name: 'Giriş yap' })).toBeInTheDocument();
    const logout = api.callsTo('POST /api/auth/logout');
    expect(logout).toHaveLength(1);
    expect(logout[0]?.headers['x-csrf-token']).toBe('csrf-1');
    expect(cachedUsers(app.client)).toEqual([]);
    // Açık çıkışta "oturum sona erdi" uyarısı ve dönüş yolu taşınmaz.
    expect(screen.queryByText('Oturumunuz sona erdi')).not.toBeInTheDocument();
  });

  it('kullanıcı değişince (başka sekmede farklı hesap) önceki kullanıcının önbelleği atılır', async () => {
    const api = mockApi(routes());
    const app = renderApp('/app');
    expect(await screen.findByRole('link', { name: 'Akasya Mobilya' })).toBeInTheDocument();
    expect(cachedUsers(app.client)).toContain(USER_1);

    api.on('GET /api/me', () => json(200, makeUser({ id: USER_2, displayName: 'Burak Aydın', email: 'burak@ornek.test' })));
    api.on('GET /api/me/memberships', () => json(200, { items: [] }));
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'));
      await Promise.resolve();
    });

    expect(await screen.findByText('Henüz bir şirkete üye değilsiniz.')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Akasya Mobilya' })).not.toBeInTheDocument();
    await waitFor(() => expect(cachedUsers(app.client)).not.toContain(USER_1));
  });

  it('oturum yokken korumalı yol girişe yönlenir; şirket isteği hiç atılmaz', async () => {
    const api = mockApi({ 'GET /api/me': () => problem(401, 'unauthenticated') });
    renderApp(`/t/${TENANT_A}/customers`);
    expect(await screen.findByRole('heading', { name: 'Giriş yap' })).toBeInTheDocument();
    expect(api.calls.some((call) => call.path.startsWith('/api/t/'))).toBe(false);
  });

  it('CSRF: token giriş sonrası yeniden alınır; "csrf_invalid" yanıtında bir kez yenilenip istek yinelenir', async () => {
    let issued = 0;
    let rejectNext = true;
    const api = mockApi({
      'GET /api/auth/csrf': () => {
        issued += 1;
        return json(200, { token: `csrf-${issued}`, headerName: 'X-CSRF-TOKEN' });
      },
      'POST /api/tenants': () => {
        if (rejectNext) {
          rejectNext = false;
          return problem(400, 'csrf_invalid');
        }
        return json(201, { tenantId: TENANT_A });
      },
    });

    const result = await request<{ tenantId: string }>('/api/tenants', { method: 'POST', body: { name: 'Yeni' } });
    expect(result.data.tenantId).toBe(TENANT_A);
    const posts = api.callsTo('POST /api/tenants');
    expect(posts.map((call) => call.headers['x-csrf-token'])).toEqual(['csrf-1', 'csrf-2']);
    expect(posts[1]?.body).toEqual(posts[0]?.body);
    expect(api.callsTo('GET /api/auth/csrf')).toHaveLength(2);
  });
});

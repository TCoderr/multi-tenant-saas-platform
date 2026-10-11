import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { queryKeys, tenantOfKey } from '../api/queryKeys';
import { TENANT_A, TENANT_B, USER_1, deferred, json, makeCustomer, makeUser, makeWorkspace, mockApi, renderApp } from './helpers';

const customerA = makeCustomer('a0000000-0000-4000-8000-000000000001', 'Deniz Arslan');
const customerB = makeCustomer('b0000000-0000-4000-8000-000000000001', 'Kerem Aksoy');

function baseRoutes() {
  return {
    'GET /api/me': () => json(200, makeUser()),
    [`GET /api/t/${TENANT_A}`]: () => json(200, makeWorkspace(TENANT_A, 'Akasya Mobilya')),
    [`GET /api/t/${TENANT_B}`]: () => json(200, makeWorkspace(TENANT_B, 'Boreas Yazılım')),
    [`GET /api/t/${TENANT_A}/customers`]: () => json(200, { items: [customerA], nextCursor: null }),
    [`GET /api/t/${TENANT_B}/customers`]: () => json(200, { items: [customerB], nextCursor: null }),
  };
}

describe('şirket değiştirme', () => {
  it("A şirketinin geç gelen yanıtı B şirketinin ekranına düşmez", async () => {
    const lateA = deferred<Response>();
    const api = mockApi({ ...baseRoutes(), [`GET /api/t/${TENANT_A}/customers`]: () => lateA.promise });
    const app = renderApp(`/t/${TENANT_A}/customers`);

    // A'nın kabuğu açıldı, müşteri isteği havada.
    expect(await screen.findByTestId('company-name')).toHaveTextContent('Akasya Mobilya');
    await waitFor(() => expect(api.callsTo(`GET /api/t/${TENANT_A}/customers`)).toHaveLength(1));

    await act(async () => {
      await app.navigate(`/t/${TENANT_B}/customers`);
    });
    expect(await screen.findByText('Kerem Aksoy')).toBeInTheDocument();
    expect(screen.getByTestId('company-name')).toHaveTextContent('Boreas Yazılım');

    // A'nın yanıtı şimdi geliyor.
    await act(async () => {
      lateA.resolve(json(200, { items: [customerA], nextCursor: null }));
      await lateA.promise;
      await new Promise((resolve) => setTimeout(resolve, 20));
    });

    expect(screen.queryByText('Deniz Arslan')).not.toBeInTheDocument();
    expect(screen.getByText('Kerem Aksoy')).toBeInTheDocument();
    expect(document.title).toContain('Boreas Yazılım');
    expect(document.title).not.toContain('Akasya');

    // Önbellekte B'nin anahtarı altında A'nın verisi yoktur.
    for (const query of app.client.getQueryCache().getAll()) {
      if (tenantOfKey(query.queryKey) === TENANT_B) {
        expect(JSON.stringify(query.state.data ?? null)).not.toContain('Deniz Arslan');
      }
    }
  });

  it('şirket yalnız URL’den okunur: her şirketin isteği kendi yoluna gider ve şerit şirketi gösterir', async () => {
    const api = mockApi(baseRoutes());
    const app = renderApp(`/t/${TENANT_A}/customers`);
    expect(await screen.findByText('Deniz Arslan')).toBeInTheDocument();
    expect(document.title).toBe('Müşteriler · Akasya Mobilya · Kulvar');
    expect(document.querySelector('[data-tenant-id]')).toHaveAttribute('data-tenant-id', TENANT_A);

    await act(async () => {
      await app.navigate(`/t/${TENANT_B}/customers`);
    });
    expect(await screen.findByText('Kerem Aksoy')).toBeInTheDocument();
    expect(screen.queryByText('Deniz Arslan')).not.toBeInTheDocument();
    expect(document.title).toBe('Müşteriler · Boreas Yazılım · Kulvar');
    expect(document.querySelector('[data-tenant-id]')).toHaveAttribute('data-tenant-id', TENANT_B);

    // İki şirketin verisi ayrı anahtarlarda durur.
    const cachedA = app.client.getQueriesData({ queryKey: queryKeys.customers(USER_1, TENANT_A) });
    const cachedB = app.client.getQueriesData({ queryKey: queryKeys.customers(USER_1, TENANT_B) });
    expect(JSON.stringify(cachedA)).toContain('Deniz Arslan');
    expect(JSON.stringify(cachedA)).not.toContain('Kerem Aksoy');
    expect(JSON.stringify(cachedB)).toContain('Kerem Aksoy');
    expect(JSON.stringify(cachedB)).not.toContain('Deniz Arslan');
    expect(api.callsTo(`GET /api/t/${TENANT_B}/customers`).length).toBeGreaterThan(0);
  });

  it('yerel form durumu şirketler arasında taşınmaz', async () => {
    mockApi(baseRoutes());
    const user = userEvent.setup();
    const app = renderApp(`/t/${TENANT_A}/customers`);
    const search = await screen.findByLabelText('Ara');
    await user.type(search, 'gizli arama');
    expect(search).toHaveValue('gizli arama');

    await act(async () => {
      await app.navigate(`/t/${TENANT_B}/customers`);
    });
    await screen.findByText('Kerem Aksoy');
    expect(screen.getByLabelText('Ara')).toHaveValue('');
  });

  it('geçersiz şirket kimliği için istek atılmaz ve "bulunamadı" gösterilir', async () => {
    const api = mockApi(baseRoutes());
    renderApp('/t/gecersiz-kimlik/customers');
    expect(await screen.findByRole('heading', { name: 'Şirket bulunamadı' })).toBeInTheDocument();
    expect(api.calls.some((call) => call.path.startsWith('/api/t/'))).toBe(false);
  });
});

import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { WorkspaceSummary } from '../api/types';
import { TENANT_A, deferred, json, makeCustomer, makeUser, makeWorkspace, mockApi, problem, renderApp } from './helpers';

const customersPath = `GET /api/t/${TENANT_A}/customers`;

function workspaceRoutes(overrides: Partial<WorkspaceSummary> = {}) {
  return {
    'GET /api/me': () => json(200, makeUser()),
    [`GET /api/t/${TENANT_A}`]: () => json(200, makeWorkspace(TENANT_A, 'Akasya Mobilya', overrides)),
    [customersPath]: () => json(200, { items: [], nextCursor: null }),
    [`GET /api/t/${TENANT_A}/tickets`]: () => json(200, { items: [], nextCursor: null }),
    [`GET /api/t/${TENANT_A}/assignable-members`]: () => json(200, { items: [] }),
    [`GET /api/t/${TENANT_A}/members`]: () => json(200, { items: [] }),
    [`GET /api/t/${TENANT_A}/ownership-offers`]: () => json(200, { items: [] }),
    [`GET /api/t/${TENANT_A}/invites`]: () => json(200, { items: [] }),
  };
}

describe('durumlar', () => {
  it('yüklenirken aria-busy bildirilir; veri gelince kalkar', async () => {
    const pending = deferred<Response>();
    mockApi({ ...workspaceRoutes(), [customersPath]: () => pending.promise });
    renderApp(`/t/${TENANT_A}/customers`);
    const busy = await screen.findByText('Müşteriler yükleniyor…');
    expect(busy.closest('[aria-busy="true"]')).not.toBeNull();

    await act(async () => {
      pending.resolve(json(200, { items: [makeCustomer('a0000000-0000-4000-8000-000000000001', 'Deniz Arslan')], nextCursor: null }));
      await pending.promise;
    });
    expect(await screen.findByText('Deniz Arslan')).toBeInTheDocument();
    expect(screen.queryByText('Müşteriler yükleniyor…')).not.toBeInTheDocument();
  });

  it('boş durum ne olmadığını ve ilk adımı söyler', async () => {
    mockApi(workspaceRoutes());
    renderApp(`/t/${TENANT_A}/customers`);
    expect(await screen.findByText('Henüz müşteri yok.')).toBeInTheDocument();
    expect(screen.getByText(/İlk müşteriyi ekleyin/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Yeni müşteri' })).toBeInTheDocument();
  });

  it('hata durumu: açıklama, iz kimliği ve çalışan "yeniden dene"', async () => {
    const api = mockApi({ ...workspaceRoutes(), [customersPath]: () => problem(503, 'dependency_unavailable') });
    const user = userEvent.setup();
    renderApp(`/t/${TENANT_A}/customers`);
    expect(await screen.findByText('Hizmet geçici olarak kullanılamıyor', {}, { timeout: 4000 })).toBeInTheDocument();
    expect(screen.getByText('İz kimliği: trace-dependency_unavailable')).toBeInTheDocument();

    api.on(customersPath, () => json(200, { items: [makeCustomer('a0000000-0000-4000-8000-000000000001', 'Deniz Arslan')], nextCursor: null }));
    await user.click(screen.getByRole('button', { name: 'Yeniden dene' }));
    expect(await screen.findByText('Deniz Arslan')).toBeInTheDocument();
  });

  it('yetkisiz (403): gereken rol belirtilir ve sunucuya içerik isteği atılmaz', async () => {
    const api = mockApi(workspaceRoutes({ role: 'Agent' }));
    renderApp(`/t/${TENANT_A}/audit`);
    expect(await screen.findByText('Bu içerik için yetkiniz yok')).toBeInTheDocument();
    expect(screen.getByText(/Gereken yetki: Owner veya Admin rolü\. Rolünüz: Agent\./)).toBeInTheDocument();
    expect(api.callsTo(`GET /api/t/${TENANT_A}/audit`)).toHaveLength(0);
  });

  it('sunucunun 403 yanıtı gereken yetkiyle birlikte gösterilir', async () => {
    mockApi({ ...workspaceRoutes(), [customersPath]: () => problem(403, 'forbidden', { requiredPermission: 'customers.read' }) });
    renderApp(`/t/${TENANT_A}/customers`);
    expect(await screen.findByText('Bu içerik için yetkiniz yok')).toBeInTheDocument();
    expect(screen.getByText('Gereken yetki: şirket üyeliği.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Yeniden dene' })).not.toBeInTheDocument();
  });

  it('rol şeffaflığı: yapılamayan eylem gizlenmez, devre dışıdır ve nedenini söyler', async () => {
    mockApi(workspaceRoutes({ role: 'Viewer' }));
    renderApp(`/t/${TENANT_A}/customers`);
    const button = await screen.findByRole('button', { name: 'Yeni müşteri' });
    expect(button).toHaveAttribute('aria-disabled', 'true');
    expect(button).toHaveAccessibleDescription('Bu işlem için Owner, Admin veya Agent rolü gerekir. Rolünüz: Viewer.');
    expect(screen.queryByRole('link', { name: 'Yeni müşteri' })).not.toBeInTheDocument();
  });

  it('çevrimdışı bandı bağlantı kesilince görünür, gelince kalkar', async () => {
    mockApi(workspaceRoutes());
    renderApp(`/t/${TENANT_A}/customers`);
    await screen.findByText('Henüz müşteri yok.');
    expect(screen.queryByText(/Bağlantı yok/)).not.toBeInTheDocument();

    const online = vi.spyOn(window.navigator, 'onLine', 'get').mockReturnValue(false);
    act(() => {
      window.dispatchEvent(new Event('offline'));
    });
    expect(screen.getByText(/Bağlantı yok\. Değişiklikler gönderilmedi/)).toBeInTheDocument();

    online.mockReturnValue(true);
    act(() => {
      window.dispatchEvent(new Event('online'));
    });
    expect(screen.queryByText(/Bağlantı yok/)).not.toBeInTheDocument();
  });

  it('PastDue: sarı bant ödemesiz dönemin bitişini ve Owner için plan bağlantısını gösterir', async () => {
    mockApi(
      workspaceRoutes({
        subscription: { status: 'PastDue', planCode: 'team', planName: 'Team', trialEndsAt: null, graceEndsAt: '2026-10-20T12:00:00Z' },
      }),
    );
    renderApp(`/t/${TENANT_A}/customers`);
    const band = (await screen.findByText(/Ödeme alınamadı\. Ödemesiz dönem/)).closest('.band');
    expect(band).toHaveClass('band--warn');
    expect(band).toHaveTextContent('20 Ekim 2026');
    expect(within(band as HTMLElement).getByRole('link', { name: 'Planı güncelle' })).toHaveAttribute('href', `/t/${TENANT_A}/plan`);
  });

  it('abonelik iptal/askıda: kırmızı bant, içerik salt-okunur, yazma düğmeleri nedenli devre dışı', async () => {
    const recovery = makeWorkspace(TENANT_A, 'Akasya Mobilya', {
      accessMode: 'Recovery',
      permissions: ['workspace.read', 'members.leave', 'tenant.delete', 'customers.read', 'customers.pii', 'tickets.read', 'members.read', 'members.manage', 'billing.read', 'billing.manage'],
      subscription: { status: 'Cancelled', planCode: 'team', planName: 'Team', trialEndsAt: null, graceEndsAt: null },
    });
    mockApi({ ...workspaceRoutes(), [`GET /api/t/${TENANT_A}`]: () => json(200, recovery) });
    renderApp(`/t/${TENANT_A}/customers`);
    const band = (await screen.findByText(/Abonelik iptal edildi: içerik salt-okunur/)).closest('.band');
    expect(band).toHaveClass('band--critical');
    expect(within(band as HTMLElement).getByRole('link', { name: 'Plan seçerek yeniden etkinleştir' })).toBeInTheDocument();
    const create = screen.getByRole('button', { name: 'Yeni müşteri' });
    expect(create).toHaveAttribute('aria-disabled', 'true');
    expect(create).toHaveAccessibleDescription(/Abonelik askıda veya iptal edilmiş: içerik salt-okunur/);
  });

  it('silme bekliyor: kırmızı bant kalıcı silme tarihini ve Owner için iptal bağlantısını gösterir', async () => {
    const pending = makeWorkspace(TENANT_A, 'Akasya Mobilya', {
      accessMode: 'ReadOnly',
      permissions: ['workspace.read', 'members.leave', 'tenant.delete.cancel', 'customers.read', 'customers.pii', 'tickets.read', 'members.read', 'billing.read', 'audit.read', 'export.manage'],
    });
    pending.tenant = { ...pending.tenant, status: 'DeletionPending', purgeAfter: '2026-11-09T12:00:00Z' };
    mockApi({ ...workspaceRoutes(), [`GET /api/t/${TENANT_A}`]: () => json(200, pending) });
    renderApp(`/t/${TENANT_A}/settings`);
    const band = (await screen.findByText(/Silme bekliyor: bu şirket/)).closest('.band');
    expect(band).toHaveClass('band--critical');
    expect(band).toHaveTextContent('9 Kasım 2026');
    expect(within(band as HTMLElement).getByRole('link', { name: 'Silmeyi iptal et' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Silmeyi iptal et' })).not.toHaveAttribute('aria-disabled');
    expect(screen.getByRole('button', { name: 'Adı kaydet' })).toHaveAttribute('aria-disabled', 'true');
  });

  it('platform askısı: tam sayfa durum; gezinme ve içerik kapalı, içerik isteği yok', async () => {
    const locked = makeWorkspace(TENANT_A, 'Akasya Mobilya', { accessMode: 'Locked', permissions: ['workspace.read', 'members.leave'] });
    locked.tenant = { ...locked.tenant, status: 'Suspended', suspendedReason: 'Ödeme incelemesi' };
    const api = mockApi({ ...workspaceRoutes(), [`GET /api/t/${TENANT_A}`]: () => json(200, locked) });
    renderApp(`/t/${TENANT_A}/customers`);
    expect(await screen.findByRole('heading', { name: 'Şirket askıya alındı' })).toBeInTheDocument();
    expect(screen.getByText('Ödeme incelemesi')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Müşteriler' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Şirketten ayrıl' })).toBeInTheDocument();
    expect(api.callsTo(customersPath)).toHaveLength(0);
  });

  it('üyelik kaldırılınca (404 tenant_not_found) şirket içeriği ekrandan ve bellekten kalkar', async () => {
    const api = mockApi({
      ...workspaceRoutes(),
      [customersPath]: () => json(200, { items: [makeCustomer('a0000000-0000-4000-8000-000000000001', 'Deniz Arslan')], nextCursor: null }),
    });
    const app = renderApp(`/t/${TENANT_A}/customers`);
    expect(await screen.findByText('Deniz Arslan')).toBeInTheDocument();

    api.on(`GET /api/t/${TENANT_A}`, () => problem(404, 'tenant_not_found'));
    api.on(`GET /api/t/${TENANT_A}/tickets`, () => problem(404, 'tenant_not_found'));
    api.on('GET /api/me/memberships', () => json(200, { items: [] }));
    await act(async () => {
      await app.navigate(`/t/${TENANT_A}/tickets`);
    });

    expect(await screen.findByRole('heading', { name: 'Şirket bulunamadı' })).toBeInTheDocument();
    expect(screen.queryByText('Deniz Arslan')).not.toBeInTheDocument();
    await waitFor(() => expect(JSON.stringify(app.client.getQueryCache().getAll().map((query) => query.state.data ?? null))).not.toContain('Deniz Arslan'));
  });

  it('rol düşünce önceki rolle alınmış veri (maskesiz e-posta) önbellekten atılır ve yeni rolle yeniden okunur', async () => {
    const id = 'a0000000-0000-4000-8000-000000000001';
    const api = mockApi({
      ...workspaceRoutes({ role: 'Agent' }),
      [customersPath]: () => json(200, { items: [makeCustomer(id, 'Deniz Arslan', { email: 'deniz.arslan@musteri-a.test' })], nextCursor: null }),
    });
    const app = renderApp(`/t/${TENANT_A}/customers`);
    expect(await screen.findByText('deniz.arslan@musteri-a.test')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Yeni müşteri' })).toBeInTheDocument();

    // Owner bu sırada rolü Viewer yaptı; çalışma alanı özeti yeniden okununca yetkiler değişir.
    api.on(`GET /api/t/${TENANT_A}`, () => json(200, makeWorkspace(TENANT_A, 'Akasya Mobilya', { role: 'Viewer' })));
    api.on(customersPath, () => json(200, { items: [makeCustomer(id, 'Deniz Arslan', { email: 'd***@musteri-a.test', piiMasked: true })], nextCursor: null }));
    await act(async () => {
      await app.client.invalidateQueries({ queryKey: ['u', makeUser().id, 't', TENANT_A, 'workspace'] });
    });

    expect(await screen.findByText('d***@musteri-a.test')).toBeInTheDocument();
    expect(screen.queryByText('deniz.arslan@musteri-a.test')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Yeni müşteri' })).toHaveAttribute('aria-disabled', 'true');
    expect(JSON.stringify(app.client.getQueryCache().getAll().map((query) => query.state.data ?? null))).not.toContain('deniz.arslan@musteri-a.test');
  });

  it('kota doldu: talep formu limit, kullanılan ve sıfırlanma tarihini gösterir; Owner plan bağlantısı görür', async () => {
    const full = { seatsUsed: 3, pendingInvites: 0, ticketsThisPeriod: 20, periodResetsAt: '2026-11-01T12:00:00Z' };
    const api = mockApi({
      ...workspaceRoutes({ usage: full, entitlements: { seatLimit: 3, monthlyTicketLimit: 20, exportEnabled: false } }),
      [customersPath]: () => json(200, { items: [makeCustomer('a0000000-0000-4000-8000-000000000001', 'Deniz Arslan')], nextCursor: null }),
      [`POST /api/t/${TENANT_A}/tickets`]: () => problem(403, 'quota_exceeded', { metric: 'tickets_created', limit: 20, used: 20, resetsAt: '2026-11-01T12:00:00Z' }),
    });
    const user = userEvent.setup();
    renderApp(`/t/${TENANT_A}/tickets/new`);

    const note = (await screen.findByText('Bu dönemin talep kotası doldu')).closest('.note') as HTMLElement;
    expect(note).toHaveTextContent('20 / 20 talep');
    expect(note).toHaveTextContent('1 Kas 2026');
    expect(within(note).getByRole('link', { name: 'Planı yükselt' })).toHaveAttribute('href', `/t/${TENANT_A}/plan`);

    // Karar sunucunundur: gönderim denenir, 403 quota_exceeded yanıtı aynı kutuda gösterilir ve metin korunur.
    await screen.findByRole('option', { name: 'Deniz Arslan' });
    await user.selectOptions(screen.getByLabelText('Müşteri'), 'a0000000-0000-4000-8000-000000000001');
    await user.type(screen.getByLabelText('Başlık'), 'Kargo gecikmesi');
    await user.type(screen.getByLabelText('Açıklama'), 'Üç gündür bekliyor.');
    await user.click(screen.getByRole('button', { name: 'Talebi aç' }));
    await waitFor(() => expect(api.callsTo(`POST /api/t/${TENANT_A}/tickets`)).toHaveLength(1));
    expect(await screen.findByText('Bu dönemin talep kotası doldu')).toBeInTheDocument();
    expect(screen.getByLabelText('Başlık')).toHaveValue('Kargo gecikmesi');
  });

  it('koltuk doldu: davet formu aynı kalıbı gösterir', async () => {
    const api = mockApi({
      ...workspaceRoutes({
        usage: { seatsUsed: 2, pendingInvites: 1, ticketsThisPeriod: 1, periodResetsAt: '2026-11-01T12:00:00Z' },
        entitlements: { seatLimit: 3, monthlyTicketLimit: 20, exportEnabled: false },
      }),
      [`POST /api/t/${TENANT_A}/invites`]: () => problem(403, 'quota_exceeded', { metric: 'seats', limit: 3, used: 3 }),
    });
    const user = userEvent.setup();
    renderApp(`/t/${TENANT_A}/team`);
    await user.type(await screen.findByLabelText('Davet edilecek e-posta'), 'yeni@ornek.test');
    await user.click(screen.getByRole('button', { name: 'Davet gönder' }));
    await waitFor(() => expect(api.callsTo(`POST /api/t/${TENANT_A}/invites`)).toHaveLength(1));
    const note = (await screen.findByText('Koltuk limiti dolu')).closest('.note') as HTMLElement;
    expect(note).toHaveTextContent('3 / 3');
    expect(within(note).getByRole('link', { name: 'Planı yükselt' })).toBeInTheDocument();
    expect(screen.getByLabelText('Davet edilecek e-posta')).toHaveValue('yeni@ornek.test');
  });
});

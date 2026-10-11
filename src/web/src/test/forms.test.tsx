import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { TENANT_A, json, makeCustomer, makeTicket, makeUser, makeWorkspace, mockApi, problem, renderApp } from './helpers';

const CUSTOMER_ID = 'a0000000-0000-4000-8000-000000000001';
const TICKET_ID = 'd0000000-0000-4000-8000-000000000001';

function workspaceRoutes() {
  return {
    'GET /api/me': () => json(200, makeUser()),
    [`GET /api/t/${TENANT_A}`]: () => json(200, makeWorkspace(TENANT_A, 'Akasya Mobilya')),
    [`GET /api/t/${TENANT_A}/tickets`]: () => json(200, { items: [], nextCursor: null }),
    [`GET /api/t/${TENANT_A}/assignable-members`]: () => json(200, { items: [] }),
    [`GET /api/t/${TENANT_A}/customers`]: () => json(200, { items: [makeCustomer(CUSTOMER_ID, 'Deniz Arslan')], nextCursor: null }),
  };
}

describe('formlar', () => {
  it('giriş formu: görünür etiketler, hatalı alanlar aria-invalid + aria-describedby, özet role="alert", odak ilk hatalı alanda', async () => {
    const api = mockApi({ 'GET /api/me': () => problem(401, 'unauthenticated') });
    const user = userEvent.setup();
    renderApp('/login');

    const email = await screen.findByLabelText('E-posta');
    const password = screen.getByLabelText('Parola');
    expect(password).toHaveAttribute('type', 'password');
    await user.click(screen.getByRole('button', { name: 'Giriş yap' }));

    expect(screen.getByRole('alert')).toHaveTextContent('Giriş yapılamadı: eksik alanlar var.');
    expect(email).toHaveAttribute('aria-invalid', 'true');
    expect(email).toHaveAccessibleDescription('E-posta adresinizi girin.');
    expect(password).toHaveAttribute('aria-invalid', 'true');
    expect(password).toHaveAccessibleDescription('Parolanızı girin.');
    await waitFor(() => expect(email).toHaveFocus());
    expect(api.callsTo('POST /api/auth/login')).toHaveLength(0);
  });

  it('giriş hatası hangi alanın yanlış olduğunu söylemez; hız sınırı bekleme süresini söyler', async () => {
    const api = mockApi({ 'GET /api/me': () => problem(401, 'unauthenticated'), 'POST /api/auth/login': () => problem(401, 'invalid_credentials') });
    const user = userEvent.setup();
    renderApp('/login');
    await user.type(await screen.findByLabelText('E-posta'), 'ayse@ornek.test');
    await user.type(screen.getByLabelText('Parola'), 'yanlis-parola-1');
    await user.click(screen.getByRole('button', { name: 'Giriş yap' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('E-posta veya parola hatalı.');
    expect(screen.getByLabelText('E-posta')).not.toHaveAttribute('aria-invalid');
    // Girilen e-posta korunur.
    expect(screen.getByLabelText('E-posta')).toHaveValue('ayse@ornek.test');

    api.on('POST /api/auth/login', () => problem(429, 'rate_limited', {}, { 'Retry-After': '42' }));
    await user.click(screen.getByRole('button', { name: 'Giriş yap' }));
    expect(await screen.findByText('Çok fazla istek gönderildi. 42 saniye sonra yeniden deneyin.')).toBeInTheDocument();
  });

  it('sunucu doğrulama hataları ilgili alana bağlanır', async () => {
    mockApi({
      ...workspaceRoutes(),
      [`POST /api/t/${TENANT_A}/customers`]: () => problem(400, 'validation_failed', { errors: { phone: ['Telefon en az 7 rakam olmalıdır.'] } }),
    });
    const user = userEvent.setup();
    renderApp(`/t/${TENANT_A}/customers/new`);
    await user.type(await screen.findByLabelText('Ad soyad'), 'Elif Korkmaz');
    await user.type(screen.getByLabelText(/^Telefon/), '12');
    await user.click(screen.getByRole('button', { name: 'Müşteriyi kaydet' }));

    const phone = screen.getByLabelText(/^Telefon/);
    await waitFor(() => expect(phone).toHaveAttribute('aria-invalid', 'true'));
    expect(phone).toHaveAccessibleDescription(/Telefon en az 7 rakam olmalıdır\./);
    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByLabelText('Ad soyad')).toHaveValue('Elif Korkmaz');
  });

  it('412 çakışma: "kendi metnimi koru" metni saklar ve sonraki kayıt güncel sürümle gider', async () => {
    let current = makeCustomer(CUSTOMER_ID, 'Deniz Arslan', { version: 4, notes: 'ilk not' });
    const api = mockApi({
      ...workspaceRoutes(),
      [`GET /api/t/${TENANT_A}/customers/${CUSTOMER_ID}`]: () => json(200, current, { ETag: `"${current.version}"` }),
      [`PUT /api/t/${TENANT_A}/customers/${CUSTOMER_ID}`]: (call) => {
        if (call.headers['if-match'] !== `"${current.version}"`) {
          return problem(412, 'precondition_failed');
        }
        current = { ...current, ...(call.body as object), version: current.version + 1 };
        return json(200, current, { ETag: `"${current.version}"` });
      },
    });
    const user = userEvent.setup();
    renderApp(`/t/${TENANT_A}/customers/${CUSTOMER_ID}`);
    await user.click(await screen.findByRole('button', { name: 'Düzenle' }));
    const notes = screen.getByLabelText(/^Notlar/);
    await user.clear(notes);
    await user.type(notes, 'benim notum');

    // Bu sırada başka biri kaydı değiştirdi.
    current = { ...current, version: 5, notes: 'başkasının notu' };
    await user.click(screen.getByRole('button', { name: 'Değişiklikleri kaydet' }));

    expect(await screen.findByText('Bu kayıt siz düzenlerken değişti')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Güncel sürümü yükle' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Kendi metnimi koru' }));

    await waitFor(() => expect(screen.queryByText('Bu kayıt siz düzenlerken değişti')).not.toBeInTheDocument());
    expect(screen.getByLabelText(/^Notlar/)).toHaveValue('benim notum');
    await user.click(screen.getByRole('button', { name: 'Değişiklikleri kaydet' }));

    await waitFor(() => expect(api.callsTo(`PUT /api/t/${TENANT_A}/customers/${CUSTOMER_ID}`)).toHaveLength(2));
    const puts = api.callsTo(`PUT /api/t/${TENANT_A}/customers/${CUSTOMER_ID}`);
    expect(puts.map((call) => call.headers['if-match'])).toEqual(['"4"', '"5"']);
    expect(puts[1]?.body).toMatchObject({ notes: 'benim notum' });
    expect(await screen.findByText('benim notum')).toBeInTheDocument();
  });

  it('412 çakışma: "güncel sürümü yükle" formu sunucudaki değerlerle değiştirir', async () => {
    let current = makeCustomer(CUSTOMER_ID, 'Deniz Arslan', { version: 4, notes: 'ilk not' });
    mockApi({
      ...workspaceRoutes(),
      [`GET /api/t/${TENANT_A}/customers/${CUSTOMER_ID}`]: () => json(200, current),
      [`PUT /api/t/${TENANT_A}/customers/${CUSTOMER_ID}`]: () => problem(412, 'precondition_failed'),
    });
    const user = userEvent.setup();
    renderApp(`/t/${TENANT_A}/customers/${CUSTOMER_ID}`);
    await user.click(await screen.findByRole('button', { name: 'Düzenle' }));
    await user.type(screen.getByLabelText(/^Notlar/), ' + ekim');
    current = { ...current, version: 5, notes: 'başkasının notu' };
    await user.click(screen.getByRole('button', { name: 'Değişiklikleri kaydet' }));
    await user.click(await screen.findByRole('button', { name: 'Güncel sürümü yükle' }));
    await waitFor(() => expect(screen.getByLabelText(/^Notlar/)).toHaveValue('başkasının notu'));
  });

  it('talep oluşturma: aynı gönderimin yeniden denemesi aynı Idempotency-Key ile gider; içerik değişince anahtar değişir', async () => {
    let attempts = 0;
    const api = mockApi({
      ...workspaceRoutes(),
      [`POST /api/t/${TENANT_A}/tickets`]: () => {
        attempts += 1;
        return attempts < 3 ? problem(503, 'dependency_unavailable') : json(201, makeTicket(TICKET_ID));
      },
      [`GET /api/t/${TENANT_A}/tickets/${TICKET_ID}`]: () => json(200, makeTicket(TICKET_ID)),
      [`GET /api/t/${TENANT_A}/tickets/${TICKET_ID}/timeline`]: () => json(200, { items: [] }),
    });
    const user = userEvent.setup();
    renderApp(`/t/${TENANT_A}/tickets/new`);
    await screen.findByRole('option', { name: 'Deniz Arslan' });
    await user.selectOptions(screen.getByLabelText('Müşteri'), CUSTOMER_ID);
    await user.type(screen.getByLabelText('Başlık'), 'Teslimat gecikti');
    await user.type(screen.getByLabelText('Açıklama'), 'Sipariş üç gündür kargoda.');

    const submit = screen.getByRole('button', { name: 'Talebi aç' });
    await user.click(submit);
    await screen.findByRole('alert');
    await user.click(submit);
    await waitFor(() => expect(api.callsTo(`POST /api/t/${TENANT_A}/tickets`)).toHaveLength(2));

    await user.type(screen.getByLabelText('Başlık'), ' (acil)');
    await user.click(submit);
    await waitFor(() => expect(api.callsTo(`POST /api/t/${TENANT_A}/tickets`)).toHaveLength(3));

    const keys = api.callsTo(`POST /api/t/${TENANT_A}/tickets`).map((call) => call.headers['idempotency-key']);
    expect(keys[0]).toMatch(/^[0-9a-f-]{36}$/);
    expect(keys[1]).toBe(keys[0]);
    expect(keys[2]).not.toBe(keys[0]);
    // Gövdede şirket kimliği taşınmaz: şirket yalnız URL'dedir.
    expect(api.callsTo(`POST /api/t/${TENANT_A}/tickets`)[0]?.body).not.toHaveProperty('tenantId');
    expect(await screen.findByRole('heading', { name: 'Teslimat gecikti' })).toBeInTheDocument();
  });

  it('kullanıcı metni HTML olarak yorumlanmaz, metin olarak basılır', async () => {
    const hostile = '<img src=x onerror="window.__xss=1"><script>window.__xss=1</script>';
    mockApi({
      ...workspaceRoutes(),
      [`GET /api/t/${TENANT_A}/customers`]: () => json(200, { items: [makeCustomer(CUSTOMER_ID, hostile, { companyTitle: '<b>kalın</b>' })], nextCursor: null }),
    });
    renderApp(`/t/${TENANT_A}/customers`);
    expect(await screen.findByText(hostile)).toBeInTheDocument();
    expect(screen.getByText('<b>kalın</b>')).toBeInTheDocument();
    expect(document.querySelector('main img')).toBeNull();
    expect(document.querySelector('main script')).toBeNull();
    expect((window as unknown as { __xss?: number }).__xss).toBeUndefined();
  });

  it('şirket oluşturma: e-posta doğrulanmamışsa düğme nedenli devre dışıdır ve istek atılmaz', async () => {
    const api = mockApi({ 'GET /api/me': () => json(200, makeUser({ emailVerified: false })), 'GET /api/me/memberships': () => json(200, { items: [] }) });
    const user = userEvent.setup();
    renderApp('/app');
    const button = await screen.findByRole('button', { name: 'Şirket oluştur' });
    expect(button).toHaveAttribute('aria-disabled', 'true');
    expect(button).toHaveAccessibleDescription('Şirket oluşturmak için önce e-posta adresinizi doğrulayın.');
    await user.type(screen.getByLabelText('Şirket adı'), 'Deneme Ltd');
    await user.click(button);
    expect(api.callsTo('POST /api/tenants')).toHaveLength(0);
  });
});

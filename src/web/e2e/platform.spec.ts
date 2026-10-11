// Platform operatörü: yalnız metadata; müşteri/talep içeriği yok, impersonation yok. Askıya alma üyelerin ekranına yansır.

import { expect, test, type Page } from '@playwright/test';
import { TENANT_A, TENANT_B, apiPost, companyName, demo, loginAs, newSession, openWorkspace, tenantIdOf, uniqueSuffix } from './support';

interface PlatformTenantRow {
  id: string;
  name: string;
}

async function platformTenantId(page: Page, name: string): Promise<string> {
  const response = await page.request.get('/api/platform/tenants?limit=100');
  expect(response.status()).toBe(200);
  const row = ((await response.json()) as { items: PlatformTenantRow[] }).items.find((item) => item.name === name);
  expect(row, `${name} platform listesinde olmalı`).toBeDefined();
  return row!.id;
}

test('operatör şirket metadata’sını görür; müşteri ve talep içeriğine ulaşamaz', async ({ page }) => {
  await loginAs(page, demo.operator);
  await page.goto('/platform');
  await expect(page.getByRole('heading', { level: 1, name: 'Platform' })).toBeVisible();

  const tenants = page.getByRole('table', { name: 'Şirketler' });
  await expect(tenants.getByRole('row', { name: new RegExp(TENANT_A) })).toContainText('Team');
  await expect(tenants.getByRole('row', { name: new RegExp(TENANT_B) })).toContainText('Etkin');
  await expect(page.getByRole('heading', { name: 'Çözüm bekleyen ödeme işlemleri' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Ölü işler' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Platform denetimi' })).toBeVisible();

  // İçerik yok: müşteri adı, talep başlığı, üye e-postası bu ekranda hiçbir yerde geçmez.
  const text = await page.locator('main').innerText();
  for (const secret of ['Deniz Arslan', 'Kerem Aksoy', 'Yemek masası teslimatı gecikti', 'Raporlar sayfası açılmıyor', '@musteri-a.test', 'ayse.owner@akasya.test']) {
    expect(text).not.toContain(secret);
  }

  // Operatör hiçbir şirketin üyesi değildir ve şirket adresini açamaz (impersonation yok).
  const tenantA = await platformTenantId(page, TENANT_A);
  await page.goto(`/t/${tenantA}/customers`);
  await expect(page.getByRole('heading', { name: 'Şirket bulunamadı' })).toBeVisible();
  await expect(page.getByText('Deniz Arslan')).toHaveCount(0);
  expect((await page.request.get(`/api/t/${tenantA}/customers`)).status()).toBe(404);

  await page.goto('/app');
  await expect(page.getByText('Henüz bir şirkete üye değilsiniz.')).toBeVisible();
});

test('operatör olmayan kullanıcı için platform ekranı ve uçları yoktur', async ({ page }) => {
  await loginAs(page, demo.ownerA);
  await page.goto('/app');
  await expect(page.getByRole('link', { name: TENANT_A })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Platform' })).toHaveCount(0);

  await page.goto('/platform');
  await expect(page.getByRole('heading', { name: 'Sayfa bulunamadı' })).toBeVisible();
  await expect(page.getByRole('table')).toHaveCount(0);
  expect((await page.request.get('/api/platform/tenants')).status()).toBe(404);
});

test('askıya alınan şirket üyeler için tam sayfa kapanır; yeniden etkinleştirince açılır', async ({ page, browser }) => {
  const company = `Askı Deneme ${uniqueSuffix()}`;

  // Şirketi demo Owner'ı kendi oturumunda, arayüzden kurar (diğer testlerin şirketlerine dokunulmaz).
  await loginAs(page, demo.ownerB);
  await page.goto('/app');
  await page.getByLabel('Şirket adı').fill(company);
  await page.getByRole('button', { name: 'Şirket oluştur' }).click();
  await expect(companyName(page)).toHaveText(company);
  const tenantId = /\/t\/([0-9a-f-]{36})\//.exec(page.url())![1]!;

  const operator = await newSession(browser, demo.operator);
  try {
    await operator.page.goto('/platform');
    const row = operator.page.getByRole('table', { name: 'Şirketler' }).getByRole('row', { name: new RegExp(company) });

    await operator.page.getByRole('button', { name: `${company} şirketini askıya al` }).click();
    const suspend = operator.page.getByRole('dialog', { name: 'Şirketi askıya al' });
    // Gerekçesiz gönderim reddedilir (istemci ve sunucu).
    await suspend.getByRole('button', { name: 'Askıya al' }).click();
    await expect(suspend.getByText('Gerekçe en az 5 karakter olmalıdır.')).toBeVisible();
    await suspend.getByLabel('Gerekçe').fill('E2E askı denemesi');
    await suspend.getByRole('button', { name: 'Askıya al' }).click();
    await expect(suspend).toBeHidden();
    await expect(row).toContainText('Askıda');
    await expect(row).toContainText('E2E askı denemesi');

    // Üyenin açık sekmesi: yenilemede tam sayfa durum; gezinme ve içerik yok.
    await page.reload();
    await expect(page.getByRole('heading', { level: 1, name: 'Şirket askıya alındı' })).toBeVisible();
    await expect(page.getByText('E2E askı denemesi')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Müşteriler' })).toHaveCount(0);
    expect((await page.request.get(`/api/t/${tenantId}/customers`)).status()).toBe(403);

    await page.goto('/app');
    await expect(page.getByRole('row', { name: new RegExp(company) })).toContainText('Askıda');

    await operator.page.getByRole('button', { name: `${company} şirketini yeniden etkinleştir` }).click();
    const reactivate = operator.page.getByRole('dialog', { name: 'Şirketi yeniden etkinleştir' });
    await reactivate.getByLabel('Gerekçe').fill('E2E askı kaldırıldı');
    await reactivate.getByRole('button', { name: 'Yeniden etkinleştir' }).click();
    await expect(reactivate).toBeHidden();
    await expect(row).toContainText('Etkin');

    await openWorkspace(page, tenantId, 'tickets', company);
    await expect(page.getByText('Henüz talep yok.')).toBeVisible();

    // İki işlem de platform denetimine düşmüştür.
    const audit = operator.page.getByRole('table', { name: 'Platform denetimi' });
    await expect(audit.getByRole('row', { name: new RegExp(`Şirket askıya alındı.*${company}`) })).toBeVisible();
    await expect(audit.getByRole('row', { name: new RegExp(`Şirket yeniden etkinleştirildi.*${company}`) })).toBeVisible();
  } finally {
    // Test yarıda kalırsa şirket askıda bırakılmaz.
    await apiPost(operator.page, `/api/platform/tenants/${tenantId}/reactivate`, { reason: 'E2E temizlik' });
    await operator.context.close();
  }
});

test('simüle yenileme hatası Owner’a sarı bant olarak görünür; yenilenince kalkar', async ({ page, browser }) => {
  await loginAs(page, demo.ownerA);
  const tenantA = await tenantIdOf(page, TENANT_A);
  await openWorkspace(page, tenantA, 'tickets', TENANT_A);
  const band = page.getByText(/Ödeme alınamadı\. Ödemesiz dönem/);
  await expect(band).toHaveCount(0);

  const operator = await newSession(browser, demo.operator);
  const send = async (kind: string) => {
    await operator.page.getByLabel('Şirket', { exact: true }).selectOption({ label: TENANT_A });
    await operator.page.getByLabel('Olay', { exact: true }).selectOption(kind);
    await operator.page.getByRole('button', { name: 'Olayı gönder' }).click();
    await expect(operator.page.getByText('Olay isteği alındı; webhook ile işlenecek.')).toBeVisible();
  };
  try {
    await operator.page.goto('/platform');
    await send('renewal_failed');

    // Olay kuyruk + imzalı webhook üzerinden işlenir: koşul sağlanana kadar yenilenir (sabit bekleme yok).
    await expect(async () => {
      await page.reload();
      await expect(band).toBeVisible({ timeout: 4000 });
    }).toPass({ timeout: 90_000 });
    await expect(page.getByRole('link', { name: 'Planı güncelle' })).toBeVisible();
    // Ödemesiz dönemde yazma açık kalır.
    await expect(page.getByRole('link', { name: 'Yeni talep' })).toBeVisible();

    await send('renewed');
    await expect(async () => {
      await page.reload();
      await expect(companyName(page)).toHaveText(TENANT_A);
      await expect(band).toHaveCount(0, { timeout: 4000 });
    }).toPass({ timeout: 90_000 });
  } finally {
    // Akasya aboneliği diğer testler için Etkin durumda bırakılır.
    await apiPost(operator.page, '/api/platform/simulation/provider-event', { tenantId: tenantA, kind: 'renewed' });
    await operator.context.close();
  }
});

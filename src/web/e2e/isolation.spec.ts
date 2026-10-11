// Şirket yalıtımı arayüzde: A'nın verisi B'de görünmez, URL ile başka şirkete erişilemez, iki sekme karışmaz,
// geç gelen A yanıtı B ekranına düşmez, çıkıştan sonra önceki kullanıcının verisi kalmaz.

import { expect, test, type Route } from '@playwright/test';
import { TENANT_A, TENANT_B, companyName, customersTable, demo, demoPassword, loginAs, openWorkspace, tenantIdOf, ticketsTable } from './support';

const A_CUSTOMER = 'Deniz Arslan';
const B_CUSTOMER = 'Kerem Aksoy';
const A_TICKET = 'Yemek masası teslimatı gecikti';
const B_TICKET = 'Raporlar sayfası açılmıyor';

test('iki şirketin üyesi her şirkette yalnız o şirketin verisini görür', async ({ page }) => {
  await loginAs(page, demo.shared);
  const tenantA = await tenantIdOf(page, TENANT_A);
  const tenantB = await tenantIdOf(page, TENANT_B);

  await openWorkspace(page, tenantA, 'customers', TENANT_A);
  await expect(page).toHaveTitle(`Müşteriler · ${TENANT_A} · Kulvar`);
  await expect(customersTable(page).getByRole('link', { name: A_CUSTOMER })).toBeVisible();
  await expect(page.getByText(B_CUSTOMER)).toHaveCount(0);
  await expect(page.getByText(/Rol: Agent/)).toBeVisible();

  // A içinde B'nin müşterisini aramak sonuç vermez.
  await page.getByRole('searchbox', { name: 'Ara' }).fill('Aksoy');
  await page.getByRole('button', { name: 'Uygula' }).click();
  await expect(page.getByText('Aramanızla eşleşen müşteri yok.')).toBeVisible();

  await page.getByRole('link', { name: 'Talepler' }).click();
  await expect(ticketsTable(page).getByRole('link', { name: A_TICKET })).toBeVisible();
  await expect(page.getByText(B_TICKET)).toHaveCount(0);

  // Şirket değişimi: şerit, başlık ve içerik birlikte değişir.
  await page.getByRole('link', { name: 'Şirketlerim' }).click();
  await page.getByRole('link', { name: TENANT_B }).click();
  await expect(companyName(page)).toHaveText(TENANT_B);
  await expect(page).toHaveURL(new RegExp(`/t/${tenantB}/tickets$`));
  await expect(page).toHaveTitle(`Talepler · ${TENANT_B} · Kulvar`);
  await expect(ticketsTable(page).getByRole('link', { name: B_TICKET })).toBeVisible();
  await expect(page.getByText(A_TICKET)).toHaveCount(0);
  await expect(page.getByText(/Rol: Admin/)).toBeVisible();

  await page.getByRole('link', { name: 'Müşteriler' }).click();
  await expect(customersTable(page).getByRole('link', { name: B_CUSTOMER })).toBeVisible();
  await expect(page.getByText(A_CUSTOMER)).toHaveCount(0);

  // Tarayıcı deposuna hiçbir şey yazılmamıştır (token, şirket kimliği, kişisel veri).
  // eslint-disable-next-line no-restricted-properties -- yalnız okuma: test, uygulamanın depoya hiçbir şey yazmadığını doğrular
  const stored = await page.evaluate(() => ({ local: window.localStorage.length, session: window.sessionStorage.length }));
  expect(stored).toEqual({ local: 0, session: 0 });
});

test('başka şirketin adresi "bulunamadı" verir; kimlik tahmini veri sızdırmaz', async ({ page, browser }) => {
  // B'nin gerçek kimlikleri B üyesinin oturumundan alınır.
  const other = await browser.newContext();
  const otherPage = await other.newPage();
  await loginAs(otherPage, demo.ownerB);
  const tenantB = await tenantIdOf(otherPage, TENANT_B);
  const customers = (await (await otherPage.request.get(`/api/t/${tenantB}/customers`)).json()) as { items: { id: string; name: string }[] };
  const customerB = customers.items.find((item) => item.name === B_CUSTOMER)!;
  await other.close();

  await loginAs(page, demo.ownerA);
  const tenantA = await tenantIdOf(page, TENANT_A);

  // 1) B'nin çalışma alanı adresi.
  await page.goto(`/t/${tenantB}/customers`);
  await expect(page.getByRole('heading', { name: 'Şirket bulunamadı' })).toBeVisible();
  await expect(page.getByText(B_CUSTOMER)).toHaveCount(0);
  await expect(page.getByText(TENANT_B)).toHaveCount(0);

  // 2) Kendi şirketinin adresinde B'nin müşteri kimliği.
  await openWorkspace(page, tenantA, `customers/${customerB.id}`, TENANT_A);
  await expect(page.getByText('Müşteri bulunamadı')).toBeVisible();
  await expect(page.getByText(B_CUSTOMER)).toHaveCount(0);

  // 3) Sunucu da aynı cevabı verir (arayüz gizlemesine güvenilmez).
  const direct = await page.request.get(`/api/t/${tenantB}/customers`);
  expect(direct.status()).toBe(404);
  expect(((await direct.json()) as { code: string }).code).toBe('tenant_not_found');
  const crossed = await page.request.get(`/api/t/${tenantA}/customers/${customerB.id}`);
  expect(crossed.status()).toBe(404);
});

test('iki sekme iki şirket: yenileme ve gezinme sekmeleri karıştırmaz', async ({ context, page }) => {
  await loginAs(page, demo.shared);
  const tenantA = await tenantIdOf(page, TENANT_A);
  const tenantB = await tenantIdOf(page, TENANT_B);
  const second = await context.newPage();

  await openWorkspace(page, tenantA, 'customers', TENANT_A);
  await openWorkspace(second, tenantB, 'customers', TENANT_B);
  await expect(customersTable(page).getByRole('link', { name: A_CUSTOMER })).toBeVisible();
  await expect(customersTable(second).getByRole('link', { name: B_CUSTOMER })).toBeVisible();

  await page.reload();
  await expect(companyName(page)).toHaveText(TENANT_A);
  await expect(customersTable(page).getByRole('link', { name: A_CUSTOMER })).toBeVisible();
  await expect(page.getByText(B_CUSTOMER)).toHaveCount(0);

  await second.getByRole('link', { name: 'Talepler' }).click();
  await expect(ticketsTable(second).getByRole('link', { name: B_TICKET })).toBeVisible();
  await expect(second.getByText(A_TICKET)).toHaveCount(0);
  await expect(companyName(page)).toHaveText(TENANT_A);

  // Sekmeler şerit üzerinden ayırt edilir: her biri kendi şirket kimliğini taşır.
  await expect(page.locator('[data-tenant-id]')).toHaveAttribute('data-tenant-id', tenantA);
  await expect(second.locator('[data-tenant-id]')).toHaveAttribute('data-tenant-id', tenantB);
});

test('A şirketinin geç gelen yanıtı B ekranına düşmez', async ({ page }) => {
  await loginAs(page, demo.shared);
  const tenantA = await tenantIdOf(page, TENANT_A);
  const tenantB = await tenantIdOf(page, TENANT_B);

  // A'nın müşteri listesi isteği tutulur; yanıt, kullanıcı B'ye geçtikten sonra bırakılır.
  let held: Route | null = null;
  let seen!: () => void;
  const requested = new Promise<void>((resolve) => {
    seen = resolve;
  });
  await page.route(`**/api/t/${tenantA}/customers*`, (route) => {
    held = route;
    seen();
  });

  await openWorkspace(page, tenantA, 'customers', TENANT_A);
  await requested;

  // Tam sayfa yenilemesi olmadan (SPA içi) şirket değiştirilir.
  await page.getByRole('link', { name: 'Şirketlerim' }).click();
  await page.getByRole('link', { name: TENANT_B }).click();
  await page.getByRole('link', { name: 'Müşteriler' }).click();
  await expect(companyName(page)).toHaveText(TENANT_B);
  await expect(customersTable(page).getByRole('link', { name: B_CUSTOMER })).toBeVisible();

  const settled = page.waitForResponse((response) => response.url().includes(`/api/t/${tenantA}/customers`)).catch(() => null);
  await (held as Route | null)?.continue().catch(() => undefined);
  await Promise.race([settled, page.waitForLoadState('networkidle')]);

  await expect(page.getByText(A_CUSTOMER)).toHaveCount(0);
  await expect(customersTable(page).getByRole('link', { name: B_CUSTOMER })).toBeVisible();
  await expect(page).toHaveTitle(new RegExp(TENANT_B));
  expect(tenantB).not.toBe(tenantA);
});

test('çıkıştan sonra aynı tarayıcıda giren ikinci kullanıcı öncekinin verisini görmez', async ({ page }) => {
  await loginAs(page, demo.ownerA);
  const tenantA = await tenantIdOf(page, TENANT_A);
  await openWorkspace(page, tenantA, 'customers', TENANT_A);
  await expect(customersTable(page).getByRole('link', { name: A_CUSTOMER })).toBeVisible();

  await page.getByRole('button', { name: 'Çıkış yap' }).click();
  await expect(page.getByRole('heading', { name: 'Giriş yap' })).toBeVisible();

  // Aynı sekmede (tam yenileme olmadan) B'nin sahibi girer.
  await page.getByLabel('E-posta').fill(demo.ownerB.email);
  await page.getByLabel('Parola').fill(demoPassword(demo.ownerB));
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect(page).toHaveURL(/\/app$/);
  await expect(page.getByRole('link', { name: TENANT_B })).toBeVisible();
  await expect(page.getByText(TENANT_A)).toHaveCount(0);

  // Önceki kullanıcının şirket adresi açılsa bile içerik gelmez.
  await page.goto(`/t/${tenantA}/customers`);
  await expect(page.getByRole('heading', { name: 'Şirket bulunamadı' })).toBeVisible();
  await expect(page.getByText(A_CUSTOMER)).toHaveCount(0);
});

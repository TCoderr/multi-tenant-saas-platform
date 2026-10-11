// Yetki geri alma: rolü düşürülen ve üyeliği kaldırılan kullanıcının açık ekranı eski yetkilerle kalmaz.
// Karar her istekte sunucuda verilir; arayüz reddi görünce çalışma alanı özetini yeniden okur ve içeriği bellekten atar.

import { expect, test } from '@playwright/test';
import { TENANT_A, TENANT_B, companyName, countMail, customersTable, demo, loginAs, newSession, openMail, openWorkspace, tenantIdOf } from './support';

test('rol düşürme ve üyelikten çıkarma açık oturuma hemen yansır', async ({ page, browser }) => {
  await loginAs(page, demo.ownerA);
  const tenantA = await tenantIdOf(page, TENANT_A);
  const guest = await newSession(browser, demo.agentB);
  const guestPage = guest.page;

  // Yinelenebilirlik: önceki yarım kalmış bir koşudan davetli hâlâ üyeyse önce kaldırılır (taze yığında etkisizdir).
  const existing = (await (await page.request.get(`/api/t/${tenantA}/members`)).json()) as { items: { userId: string; email: string | null }[] };
  const leftover = existing.items.find((member) => member.email === demo.agentB.email);
  if (leftover !== undefined) {
    const token = ((await (await page.request.get('/api/auth/csrf')).json()) as { token: string }).token;
    expect((await page.request.delete(`/api/t/${tenantA}/members/${leftover.userId}`, { headers: { 'X-CSRF-TOKEN': token } })).status()).toBe(204);
  }

  try {
    await test.step('Owner, başka şirketin üyesini Agent olarak davet eder; davetli kabul eder', async () => {
      const inviteMail = new RegExp(`${TENANT_A} şirketine davet edildiniz`);
      const mailsBefore = await countMail(guestPage, inviteMail);
      await openWorkspace(page, tenantA, 'team', TENANT_A);
      await page.getByLabel('Davet edilecek e-posta').fill(demo.agentB.email);
      await page.getByLabel('Verilecek rol').selectOption('Agent');
      await page.getByRole('button', { name: 'Davet gönder' }).click();
      const pending = page.getByRole('table', { name: 'Davetler' }).getByRole('row').filter({ hasText: demo.agentB.email }).filter({ hasText: 'Bekliyor' });
      await expect(pending).toHaveCount(1, { timeout: 60_000 });

      await openMail(guestPage, inviteMail, mailsBefore + 1);
      await guestPage.getByRole('link', { name: 'Daveti aç' }).click();
      await guestPage.getByRole('button', { name: 'Daveti kabul et' }).click();
      await expect(companyName(guestPage)).toHaveText(TENANT_A);
      await expect(guestPage.getByText(/Rol: Agent/)).toBeVisible();
    });

    await test.step('davetli Agent olarak müşteri formunu açar', async () => {
      await guestPage.getByRole('link', { name: 'Müşteriler' }).click();
      await expect(customersTable(guestPage).getByRole('link', { name: 'Deniz Arslan' })).toBeVisible();
      await guestPage.getByRole('link', { name: 'Yeni müşteri' }).click();
      await guestPage.getByLabel('Ad soyad').fill('Geri Alma Denemesi');
      await expect(guestPage.getByRole('button', { name: 'Müşteriyi kaydet' })).toBeEnabled();
    });

    await test.step('Owner rolü Viewer’a düşürür', async () => {
      await page.reload();
      const row = page.getByRole('table', { name: 'Üyeler' }).getByRole('row', { name: new RegExp(demo.agentB.name) });
      await page.getByLabel(`${demo.agentB.name} için rol`).selectOption('Viewer');
      await row.getByRole('button', { name: 'Rolü kaydet' }).click();
      await expect(row.locator('.badge', { hasText: 'Viewer' })).toBeVisible();
    });

    await test.step('eski ekrandaki kayıt denemesi reddedilir; ekran yeni role geçer, kayıt oluşmaz', async () => {
      // Sayfa yenilenmedi: form hâlâ Agent yetkisiyle açık görünüyor.
      await guestPage.getByRole('button', { name: 'Müşteriyi kaydet' }).click();
      await expect(guestPage.getByRole('alert').filter({ hasText: 'Owner, Admin veya Agent rolü gerekir' })).toBeVisible();
      await expect(guestPage.getByText(/Rol: Viewer/)).toBeVisible();
      await expect(guestPage.getByRole('button', { name: 'Müşteriyi kaydet' })).toBeDisabled();
      // Yazdığı metin korunur.
      await expect(guestPage.getByLabel('Ad soyad')).toHaveValue('Geri Alma Denemesi');

      await guestPage.getByRole('link', { name: 'Müşteriler' }).click();
      await expect(customersTable(guestPage).getByRole('link', { name: 'Deniz Arslan' })).toBeVisible();
      await expect(guestPage.getByText('Geri Alma Denemesi')).toHaveCount(0);
      await expect(guestPage.getByRole('button', { name: 'Yeni müşteri' })).toBeDisabled();
      // Viewer artık kişisel veriyi maskeli görür.
      await expect(guestPage.getByText('deniz.arslan@musteri-a.test')).toHaveCount(0);
    });

    await test.step('Owner üyeyi şirketten kaldırır', async () => {
      await page.getByRole('button', { name: `${demo.agentB.name} üyesini kaldır` }).click();
      const dialog = page.getByRole('dialog', { name: 'Üyeyi kaldır' });
      await expect(dialog).toBeVisible();
      await dialog.getByRole('button', { name: 'Kaldır' }).click();
      await expect(dialog).toBeHidden();
      await expect(page.getByRole('table', { name: 'Üyeler' }).getByRole('row', { name: new RegExp(demo.agentB.name) })).toHaveCount(0);
    });

    await test.step('kaldırılan üyenin açık ekranı bir sonraki istekte kapanır; şirket verisi görünmez', async () => {
      await guestPage.getByRole('link', { name: 'Talepler' }).click();
      await expect(guestPage.getByRole('heading', { name: 'Şirket bulunamadı' })).toBeVisible();
      await expect(guestPage.getByText('Deniz Arslan')).toHaveCount(0);
      await expect(guestPage.getByText('Yemek masası teslimatı gecikti')).toHaveCount(0);

      await guestPage.getByRole('link', { name: 'Şirketlerime dön' }).click();
      await expect(guestPage.getByRole('link', { name: TENANT_B })).toBeVisible();
      await expect(guestPage.getByRole('link', { name: TENANT_A })).toHaveCount(0);

      const direct = await guestPage.request.get(`/api/t/${tenantA}/customers`);
      expect(direct.status()).toBe(404);
    });
  } finally {
    await guest.context.close();
  }
});

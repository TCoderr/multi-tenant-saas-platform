// Rol şeffaflığı: yapılamayan eylem gizlenmez, devre dışıdır ve nedenini söyler. Yetkiyi sunucu belirler; arayüz yalnız gösterir.

import { expect, test } from '@playwright/test';
import { TENANT_A, TENANT_B, apiPost, customersTable, demo, loginAs, openWorkspace, tenantIdOf } from './support';

test('Viewer: içerik okunur, kişisel veri maskeli, yazma eylemleri nedenli devre dışı', async ({ page }) => {
  await loginAs(page, demo.viewerA);
  const tenantA = await tenantIdOf(page, TENANT_A);

  await openWorkspace(page, tenantA, 'customers', TENANT_A);
  await expect(page.getByText(/Rol: Viewer/)).toBeVisible();
  await expect(customersTable(page).getByRole('link', { name: 'Deniz Arslan' })).toBeVisible();
  // E-posta maskelidir: tam adres hiçbir yerde görünmez.
  await expect(page.getByText('deniz.arslan@musteri-a.test')).toHaveCount(0);
  await expect(page.getByText('Rolünüz için e-posta ve telefon maskelenir, notlar gösterilmez.')).toBeVisible();

  const create = page.getByRole('button', { name: 'Yeni müşteri' });
  await expect(create).toBeDisabled();
  await expect(create).toHaveAccessibleDescription('Bu işlem için Owner, Admin veya Agent rolü gerekir. Rolünüz: Viewer.');

  await customersTable(page).getByRole('link', { name: 'Deniz Arslan' }).click();
  await expect(page.getByRole('button', { name: 'Düzenle' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Müşteriyi sil' })).toBeDisabled();
  await expect(page.getByText('Rolünüz için gösterilmez.')).toBeVisible();

  await page.getByRole('link', { name: 'Talepler' }).click();
  await expect(page.getByRole('button', { name: 'Yeni talep' })).toBeDisabled();
  await page.getByRole('link', { name: 'Yemek masası teslimatı gecikti' }).click();
  await expect(page.getByRole('button', { name: 'Yorum ekle' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Düzenle' })).toBeDisabled();

  // Yetkisiz sayfa: gereken rol söylenir.
  await page.getByRole('link', { name: 'Denetim' }).click();
  await expect(page.getByText('Bu içerik için yetkiniz yok')).toBeVisible();
  await expect(page.getByText(/Gereken yetki: Owner veya Admin rolü\. Rolünüz: Viewer\./)).toBeVisible();

  // Arayüz atlanarak yapılan yazma denemesini sunucu reddeder.
  const forced = await apiPost(page, `/api/t/${tenantA}/customers`, { name: 'İzinsiz Kayıt' });
  expect(forced.status()).toBe(403);
});

test('Agent: müşteri ve talep işleri açık; ekip, plan, denetim ve dışa aktarma yönetimi kapalı', async ({ page }) => {
  await loginAs(page, demo.agentA);
  const tenantA = await tenantIdOf(page, TENANT_A);

  await openWorkspace(page, tenantA, 'customers', TENANT_A);
  await expect(page.getByRole('link', { name: 'Yeni müşteri' })).toBeVisible();
  // Agent kişisel veriyi maskesiz görür.
  await expect(page.getByText('deniz.arslan@musteri-a.test')).toBeVisible();
  await customersTable(page).getByRole('link', { name: 'Deniz Arslan' }).click();
  await expect(page.getByRole('button', { name: 'Düzenle' })).toBeEnabled();
  const remove = page.getByRole('button', { name: 'Müşteriyi sil' });
  await expect(remove).toBeDisabled();
  await expect(remove).toHaveAccessibleDescription('Bu işlem için Owner veya Admin rolü gerekir. Rolünüz: Agent.');

  await page.getByRole('link', { name: 'Ekip' }).click();
  await expect(page.getByRole('table', { name: 'Üyeler' }).getByRole('row', { name: /Ayşe Yıldız/ })).toContainText('Rolünüz için gizli');
  const invite = page.getByRole('button', { name: 'Davet gönder' });
  await expect(invite).toBeDisabled();
  await expect(invite).toHaveAccessibleDescription('Bu işlem için Owner veya Admin rolü gerekir. Rolünüz: Agent.');
  await expect(page.getByRole('button', { name: 'Sahiplik teklif et' })).toBeDisabled();

  await page.getByRole('link', { name: 'Plan ve kullanım' }).click();
  await expect(page.getByRole('heading', { name: 'Geçerli plan' })).toBeVisible();
  await expect(page.getByText('Bu işlem için Owner veya Admin rolü gerekir. Rolünüz: Agent.')).toBeVisible();
  await expect(page.getByRole('button', { name: /planına geç/ })).toHaveCount(0);

  await page.getByRole('link', { name: 'Dışa aktarma' }).click();
  await expect(page.getByText(/Gereken yetki: Owner rolü\. Rolünüz: Agent\./)).toBeVisible();

  await page.getByRole('link', { name: 'Ayarlar' }).click();
  await expect(page.getByRole('button', { name: 'Adı kaydet' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Şirketi sil' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Şirketten ayrıl' })).toBeEnabled();
});

test('Admin: ekip ve denetim açık; plan değişikliği, dışa aktarma ve şirket silme yalnız Owner’da', async ({ page }) => {
  await loginAs(page, demo.adminA);
  const tenantA = await tenantIdOf(page, TENANT_A);

  await openWorkspace(page, tenantA, 'team', TENANT_A);
  const members = page.getByRole('table', { name: 'Üyeler' });
  await expect(members.getByRole('row', { name: /Aslı Kaya/ })).toContainText('asli.agent@akasya.test');
  await expect(members.getByRole('row', { name: /Aslı Kaya/ }).getByRole('button', { name: 'Rolü kaydet' })).toBeEnabled();
  await expect(members.getByRole('row', { name: /Ayşe Yıldız/ })).toContainText('Owner ve Admin üyeleri yalnız Owner yönetebilir.');
  await expect(page.getByRole('button', { name: 'Davet gönder' })).toBeEnabled();
  // Admin yalnız Agent ve Viewer rolü verebilir.
  await expect(page.getByLabel('Verilecek rol').locator('option')).toHaveText(['Agent', 'Viewer']);
  const offer = page.getByRole('button', { name: 'Sahiplik teklif et' });
  await expect(offer).toBeDisabled();
  await expect(offer).toHaveAccessibleDescription('Bu işlem için Owner rolü gerekir. Rolünüz: Admin.');

  await page.getByRole('link', { name: 'Denetim' }).click();
  await expect(page.getByRole('table', { name: 'Denetim kaydı' })).toBeVisible();

  await page.getByRole('link', { name: 'Plan ve kullanım' }).click();
  const change = page.getByRole('button', { name: 'Starter planına geç' });
  await expect(change).toBeDisabled();
  await expect(change).toHaveAccessibleDescription('Bu işlem için Owner rolü gerekir. Rolünüz: Admin.');
  await expect(page.getByRole('button', { name: 'Aboneliği iptal et' })).toBeDisabled();

  await page.getByRole('link', { name: 'Dışa aktarma' }).click();
  await expect(page.getByText(/Gereken yetki: Owner rolü\. Rolünüz: Admin\./)).toBeVisible();
});

test('Owner: tüm yönetim eylemleri açık; koltuğu dolu şirkette davet formu limiti gösterir', async ({ page }) => {
  await loginAs(page, demo.ownerB);
  const tenantB = await tenantIdOf(page, TENANT_B);

  await openWorkspace(page, tenantB, 'team', TENANT_B);
  // Boreas deneme planında: 3 koltuğun 3'ü dolu. Sayaç sunucudan gelir.
  const note = page.locator('.note', { hasText: 'Koltuk limiti dolu' });
  await expect(note).toContainText('3 / 3');
  await expect(note.getByRole('link', { name: 'Planı yükselt' })).toBeVisible();

  // Karar sunucunundur: davet denemesi 403 quota_exceeded ile reddedilir ve aynı açıklama kalır.
  await page.getByLabel('Davet edilecek e-posta').fill('fazla.koltuk@deneme.test');
  await page.getByRole('button', { name: 'Davet gönder' }).click();
  await expect(note).toBeVisible();
  await expect(page.getByLabel('Davet edilecek e-posta')).toHaveValue('fazla.koltuk@deneme.test');
  await expect(page.getByRole('table', { name: 'Davetler' }).getByText('fazla.koltuk@deneme.test')).toHaveCount(0);

  await page.getByRole('link', { name: 'Dışa aktarma' }).click();
  const start = page.getByRole('button', { name: 'Yeni dışa aktarma iste' });
  await expect(start).toBeDisabled();
  await expect(start).toHaveAccessibleDescription(/Dışa aktarma planınıza dahil değil/);

  await page.getByRole('link', { name: 'Ayarlar' }).click();
  await expect(page.getByRole('button', { name: 'Adı kaydet' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Şirketi sil' })).toBeEnabled();

  await page.getByRole('link', { name: 'Plan ve kullanım' }).click();
  await expect(page.getByRole('button', { name: 'Starter planına geç' })).toBeEnabled();
});

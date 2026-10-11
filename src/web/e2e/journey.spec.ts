// Ana kullanıcı yolculuğu: kayıt → giriş → e-posta doğrulama → şirket → müşteri → talep → yorum → durum →
// davet ve kabul → plan değişikliği (simüle ödeme) → dışa aktarma ve indirme → çıkış.

import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { companyName, demo, loginThroughForm, newSession, openMail, uniqueSuffix } from './support';

test('yeni kullanıcı kayıttan dışa aktarmaya kadar tüm yolu tamamlar', async ({ page, browser }) => {
  const suffix = uniqueSuffix();
  const email = `yolcu.${suffix}@deneme.test`;
  const password = `Kv!${randomUUID()}`; // Koşuya özel sentetik parola; hiçbir yere yazdırılmaz.
  const company = `Yolculuk ${suffix}`;
  let tenantId = '';

  await test.step('ürün sayfası planları API’den gösterir ve Demo etiketi görünür', async () => {
    await page.goto('/');
    await expect(page.getByRole('heading', { level: 1, name: 'Her şirket kendi kulvarında' })).toBeVisible();
    await expect(page.getByText('Demo', { exact: true }).first()).toBeVisible();
    const plans = page.getByRole('table', { name: 'Plan karşılaştırması' });
    await expect(plans.getByRole('rowheader', { name: 'Starter' })).toBeVisible();
    await expect(plans.getByRole('rowheader', { name: 'Team' })).toBeVisible();
    await expect(plans.getByRole('row', { name: /Starter/ })).toContainText('₺490');
  });

  await test.step('kayıt: genel yanıt, ardından giriş', async () => {
    await page.getByRole('link', { name: 'Hesap oluştur' }).click();
    await page.getByLabel('Ad soyad').fill('Yolcu Deneme');
    await page.getByLabel('E-posta').fill(email);
    await page.getByLabel('Parola').fill(password);
    await page.getByRole('button', { name: 'Kayıt ol' }).click();
    await expect(page.getByRole('heading', { name: 'Kayıt alındı' })).toBeVisible();
    await loginThroughForm(page, email, password);
    await expect(page.getByText('E-posta adresiniz henüz doğrulanmadı')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Şirket oluştur' })).toBeDisabled();
  });

  await test.step('e-posta doğrulama demo posta kutusundaki bağlantıyla yapılır', async () => {
    await openMail(page, /e-posta adresinizi doğrulayın/);
    await expect(page.getByText('Demo posta kutusu')).toBeVisible();
    await page.getByRole('link', { name: 'E-posta adresimi doğrula' }).click();
    await expect(page.getByText('E-posta adresiniz doğrulandı')).toBeVisible();
  });

  await test.step('şirket oluşturulur; çalışma alanı şirketin şeridiyle açılır', async () => {
    await page.getByRole('link', { name: 'Şirketlerime git' }).click();
    await expect(page.getByText('Henüz bir şirkete üye değilsiniz.')).toBeVisible();
    await page.getByLabel('Şirket adı').fill(company);
    await page.getByRole('button', { name: 'Şirket oluştur' }).click();
    await expect(page).toHaveURL(/\/t\/[0-9a-f-]{36}\/tickets$/);
    tenantId = /\/t\/([0-9a-f-]{36})\//.exec(page.url())![1]!;
    await expect(companyName(page)).toHaveText(company);
    await expect(page).toHaveTitle(`Talepler · ${company} · Kulvar`);
    await expect(page.getByText('Henüz talep yok.')).toBeVisible();
    await expect(page.getByText(/Rol: Owner/)).toBeVisible();
  });

  await test.step('müşteri eklenir', async () => {
    await page.getByRole('link', { name: 'Müşteriler' }).click();
    await expect(page.getByText('Henüz müşteri yok.')).toBeVisible();
    await page.getByRole('link', { name: 'Yeni müşteri' }).click();
    await page.getByLabel('Ad soyad').fill('Zeynep Deneme');
    await page.getByLabel(/^Unvan/).fill('Deneme Tekstil Ltd. Şti.');
    await page.getByLabel(/^E-posta/).fill(`zeynep.${suffix}@musteri-deneme.test`);
    await page.getByLabel(/^Telefon/).fill('+90 555 030 03 01');
    await page.getByRole('button', { name: 'Müşteriyi kaydet' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Zeynep Deneme' })).toBeVisible();
    await expect(page.getByText('Bu müşterinin talebi yok.')).toBeVisible();
  });

  await test.step('talep açılır, yorum eklenir, durum ilerler', async () => {
    await page.getByRole('link', { name: 'Bu müşteri için talep aç' }).click();
    await expect(page.getByLabel('Müşteri')).toHaveValue(/[0-9a-f-]{36}/);
    await page.getByLabel('Başlık').fill('Kumaş teslimatı eksik geldi');
    await page.getByLabel('Açıklama').fill('40 top yerine 36 top teslim edildi.');
    await page.getByLabel('Öncelik').selectOption('High');
    await page.getByRole('button', { name: 'Talebi aç' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Kumaş teslimatı eksik geldi' })).toBeVisible();
    await expect(page.getByText('#1', { exact: true })).toBeVisible();
    const header = page.locator('.page-head');
    await expect(header.getByText('Açık', { exact: true })).toBeVisible();

    const comment = page.getByRole('textbox', { name: 'Yorum' });
    await comment.fill('Depo sayımı istendi.');
    await page.getByRole('button', { name: 'Yorum ekle' }).click();
    const timeline = page.getByRole('list', { name: 'Zaman çizelgesi' });
    await expect(timeline.getByText('Depo sayımı istendi.')).toBeVisible();
    await expect(comment).toHaveValue('');

    await page.getByRole('button', { name: 'İşleme al' }).click();
    await expect(header.getByText('İşlemde', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Çözüldü olarak işaretle' }).click();
    await expect(header.getByText('Çözüldü', { exact: true })).toBeVisible();
    await expect(timeline.getByText('Durum değişti: İşlemde → Çözüldü')).toBeVisible();
  });

  await test.step('ekip: davet gönderilir, davetli kendi oturumunda kabul eder', async () => {
    await page.getByRole('link', { name: 'Ekip' }).click();
    await page.getByLabel('Davet edilecek e-posta').fill(demo.agentB.email);
    await page.getByLabel('Verilecek rol').selectOption('Agent');
    await page.getByRole('button', { name: 'Davet gönder' }).click();
    const invites = page.getByRole('table', { name: 'Davetler' });
    await expect(invites.getByRole('row', { name: new RegExp(demo.agentB.email) })).toContainText('Bekliyor', { timeout: 60_000 });

    const invitee = await newSession(browser, demo.agentB);
    try {
      await openMail(invitee.page, new RegExp(`${company} şirketine davet edildiniz`));
      await invitee.page.getByRole('link', { name: 'Daveti aç' }).click();
      await expect(invitee.page.getByRole('heading', { name: 'Şirket daveti' })).toBeVisible();
      await expect(invitee.page.getByText(company)).toBeVisible();
      await invitee.page.getByRole('button', { name: 'Daveti kabul et' }).click();
      await expect(invitee.page).toHaveURL(new RegExp(`/t/${tenantId}/tickets$`));
      await expect(companyName(invitee.page)).toHaveText(company);
      await expect(invitee.page.getByText(/Rol: Agent/)).toBeVisible();
    } finally {
      await invitee.context.close();
    }

    await page.reload();
    const members = page.getByRole('table', { name: 'Üyeler' });
    await expect(members.getByRole('row', { name: new RegExp(demo.agentB.name) })).toContainText('Agent');
    await expect(page.getByText('2 / 3 koltuk')).toBeVisible();
  });

  await test.step('plan değişikliği: istek kabul edilir, sonuç yoklanır, plan uygulanınca limitler değişir', async () => {
    await page.getByRole('link', { name: 'Plan ve kullanım' }).click();
    await expect(page.getByText('Plana dahil değil')).toBeVisible();
    await page.getByLabel('Simülasyon sonucu').selectOption('success');
    await page.getByRole('button', { name: 'Starter planına geç' }).click();
    await expect(page.getByText('Plan değişikliği: Uygulandı')).toBeVisible({ timeout: 90_000 });
    await expect(page.getByText('Plana dahil', { exact: true })).toBeVisible();
    await expect(page.getByText('2 / 5 üye')).toBeVisible();
    await expect(page.getByRole('table', { name: 'Plan işlemleri' }).getByRole('row', { name: /Starter/ })).toContainText('Uygulandı');
  });

  await test.step('dışa aktarma hazırlanır ve indirilir', async () => {
    await page.getByRole('link', { name: 'Dışa aktarma' }).click();
    await expect(page.getByText('Henüz dışa aktarma yok.')).toBeVisible();
    await page.getByRole('button', { name: 'Yeni dışa aktarma iste' }).click();
    const jobs = page.getByRole('table', { name: 'Dışa aktarmalar' });
    await expect(jobs.getByText('Hazır', { exact: true })).toBeVisible({ timeout: 90_000 });
    const download = page.waitForEvent('download');
    await jobs.getByRole('button', { name: /indir/i }).click();
    const file = await download;
    expect(file.suggestedFilename()).toMatch(/\.zip$/);
    expect(await file.failure()).toBeNull();
  });

  await test.step('çıkıştan sonra şirket sayfası girişe yönlenir ve içerik görünmez', async () => {
    await page.getByRole('button', { name: 'Çıkış yap' }).click();
    await expect(page.getByRole('heading', { name: 'Giriş yap' })).toBeVisible();
    await page.goto(`/t/${tenantId}/customers`);
    await expect(page.getByRole('heading', { name: 'Giriş yap' })).toBeVisible();
    await expect(page.getByText('Zeynep Deneme')).toHaveCount(0);
  });
});

// Erişilebilirlik: kritik ekranlarda axe (WCAG 2.1 A/AA) 360/768/1440 genişlikte; serious/critical ihlal = 0.
// Ayrıca klavye turu (atlama bağlantısı, odak halkası, sayfa değişiminde odak, iletişim kutusu, form hatası, dar ekran menüsü).
// Ekran görüntüleri artifacts/verification/screenshots/<ekran>-<genişlik>.png olarak yazılır (parola alanı içeren ekranlar boş çekilir).

import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { TENANT_A, companyName, demo, loginAs, openWorkspace, tenantIdOf } from './support';

const WIDTHS = [1440, 768, 360] as const;
const SHOTS = path.resolve(process.cwd(), '../../artifacts/verification/screenshots');
mkdirSync(SHOTS, { recursive: true });

interface Violation {
  id: string;
  impact: string | null | undefined;
  nodes: number;
  sample: string;
}

async function seriousViolations(page: Page): Promise<Violation[]> {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
  return results.violations
    .filter((violation) => violation.impact === 'serious' || violation.impact === 'critical')
    .map((violation) => ({ id: violation.id, impact: violation.impact, nodes: violation.nodes.length, sample: violation.nodes[0]?.target.join(' ') ?? '' }));
}

/** Açık ekranı üç genişlikte denetler: axe, yatay taşma yok, ekran görüntüsü. */
async function audit(page: Page, name: string, options: { listScreen?: boolean } = {}): Promise<void> {
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    await page.waitForFunction((expected) => window.innerWidth === expected, width);
    if (options.listScreen === true) {
      // Dar ekranda tablolar satır kartlarına (tanım listesi) döner; geniş ekranda gerçek tablo kalır.
      if (width <= 480) {
        await expect(page.locator('main table')).toHaveCount(0);
        await expect(page.locator('main ul.cards dl').first()).toBeVisible();
      } else {
        await expect(page.locator('main table th[scope="col"]').first()).toBeVisible();
      }
    }
    const violations = await seriousViolations(page);
    expect(violations, `${name} @${width}px: serious/critical axe ihlali olmamalı`).toEqual([]);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow, `${name} @${width}px: yatay taşma olmamalı`).toBeLessThanOrEqual(1);
    await page.screenshot({ path: path.join(SHOTS, `${name}-${width}.png`), fullPage: true });
  }
  await page.setViewportSize({ width: 1440, height: 900 });
}

test('herkese açık ekranlar: ürün/plan, giriş, kayıt', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('table', { name: 'Plan karşılaştırması' })).toBeVisible();
  await expect(page.locator('html')).toHaveAttribute('lang', 'tr');
  await audit(page, 'urun-planlar', { listScreen: true });

  await page.goto('/login');
  await expect(page.getByRole('heading', { name: 'Giriş yap' })).toBeVisible();
  await audit(page, 'giris');

  // Hata durumu da denetlenir (özet + alan hataları).
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  await audit(page, 'giris-hata');

  await page.goto('/signup');
  await expect(page.getByRole('heading', { name: 'Kayıt ol' })).toBeVisible();
  await audit(page, 'kayit');
});

test('hesap ekranları: şirketlerim ve demo posta kutusu', async ({ page }) => {
  await loginAs(page, demo.shared);
  await page.goto('/app');
  await expect(page.getByRole('link', { name: TENANT_A })).toBeVisible();
  await audit(page, 'sirketlerim', { listScreen: true });

  await page.goto('/mail');
  await expect(page.getByRole('link', { name: /davet edildiniz/ }).first()).toBeVisible();
  await audit(page, 'posta-kutusu', { listScreen: true });
});

test('çalışma alanı ekranları: müşteri, talep, ekip, plan, denetim, dışa aktarma, ayarlar', async ({ page }) => {
  await loginAs(page, demo.ownerA);
  const tenantA = await tenantIdOf(page, TENANT_A);

  await openWorkspace(page, tenantA, 'tickets', TENANT_A);
  await expect(page.getByRole('link', { name: 'Yemek masası teslimatı gecikti' })).toBeVisible();
  await audit(page, 'talepler', { listScreen: true });

  await page.getByRole('link', { name: 'Yemek masası teslimatı gecikti' }).click();
  await expect(page.getByRole('list', { name: 'Zaman çizelgesi' })).toBeVisible();
  await audit(page, 'talep-detay');

  await openWorkspace(page, tenantA, 'tickets/new', TENANT_A);
  await expect(page.getByRole('option', { name: /Deniz Arslan/ })).toBeAttached();
  await audit(page, 'talep-yeni');

  await openWorkspace(page, tenantA, 'customers', TENANT_A);
  await expect(page.getByRole('link', { name: 'Deniz Arslan' })).toBeVisible();
  await audit(page, 'musteriler', { listScreen: true });

  await page.getByRole('link', { name: 'Deniz Arslan' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Deniz Arslan' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Talepler' })).toBeVisible();
  await audit(page, 'musteri-detay');

  await openWorkspace(page, tenantA, 'team', TENANT_A);
  await expect(page.getByText('Aslı Kaya').first()).toBeVisible();
  await audit(page, 'ekip', { listScreen: true });

  await openWorkspace(page, tenantA, 'plan', TENANT_A);
  await expect(page.getByRole('heading', { name: 'Plan değiştir' })).toBeVisible();
  await audit(page, 'plan', { listScreen: true });

  await openWorkspace(page, tenantA, 'audit', TENANT_A);
  await expect(page.getByRole('table', { name: 'Denetim kaydı' })).toBeVisible();
  await audit(page, 'denetim', { listScreen: true });

  await openWorkspace(page, tenantA, 'exports', TENANT_A);
  await expect(page.getByRole('button', { name: 'Yeni dışa aktarma iste' })).toBeVisible();
  await audit(page, 'disa-aktarma');

  await openWorkspace(page, tenantA, 'settings', TENANT_A);
  await expect(page.getByRole('button', { name: 'Adı kaydet' })).toBeVisible();
  await audit(page, 'ayarlar');

  // Açık iletişim kutusu da denetlenir.
  await page.getByRole('button', { name: 'Şirketi sil' }).click();
  await expect(page.getByRole('dialog', { name: 'Şirketi sil' })).toBeVisible();
  const violations = await seriousViolations(page);
  expect(violations, 'açık iletişim kutusu: serious/critical axe ihlali olmamalı').toEqual([]);
  await page.screenshot({ path: path.join(SHOTS, 'ayarlar-silme-diyalogu-1440.png') });
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('rol şeffaflığı ve durum ekranları: Viewer görünümü, yetkisiz sayfa, bulunamayan şirket', async ({ page }) => {
  await loginAs(page, demo.viewerA);
  const tenantA = await tenantIdOf(page, TENANT_A);
  await openWorkspace(page, tenantA, 'customers', TENANT_A);
  await expect(page.getByRole('button', { name: 'Yeni müşteri' })).toBeDisabled();
  await audit(page, 'musteriler-viewer', { listScreen: true });

  await openWorkspace(page, tenantA, 'audit', TENANT_A);
  await expect(page.getByText('Bu içerik için yetkiniz yok')).toBeVisible();
  await audit(page, 'yetkisiz');

  await page.goto('/t/00000000-0000-4000-8000-000000000000/tickets');
  await expect(page.getByRole('heading', { name: 'Şirket bulunamadı' })).toBeVisible();
  await audit(page, 'sirket-bulunamadi');
});

test('platform ekranı', async ({ page }) => {
  await loginAs(page, demo.operator);
  await page.goto('/platform');
  await expect(page.getByRole('table', { name: 'Şirketler' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Platform denetimi' })).toBeVisible();
  await audit(page, 'platform', { listScreen: true });
});

test('klavye turu: atlama bağlantısı, odak halkası, sayfa değişiminde odak, iletişim kutusu, form hatası, dar ekran menüsü', async ({ page }) => {
  await loginAs(page, demo.ownerA);
  const tenantA = await tenantIdOf(page, TENANT_A);
  await openWorkspace(page, tenantA, 'tickets', TENANT_A);
  await expect(page.getByRole('link', { name: 'Yemek masası teslimatı gecikti' })).toBeVisible();

  await test.step('ilk Tab "içeriğe atla" bağlantısına gelir; Enter odağı ana içeriğe taşır', async () => {
    await page.keyboard.press('Tab');
    const skip = page.getByRole('link', { name: 'İçeriğe atla' });
    await expect(skip).toBeFocused();
    await expect(skip).toBeInViewport();
    await page.keyboard.press('Enter');
    await expect.poll(() => page.evaluate(() => document.querySelector('main')?.contains(document.activeElement) ?? false)).toBe(true);
  });

  await test.step('odak halkası görünür (2 px eylem rengi)', async () => {
    await page.keyboard.press('Tab');
    const outline = await page.evaluate(() => {
      const style = window.getComputedStyle(document.activeElement as Element);
      return { tag: document.activeElement?.tagName, style: style.outlineStyle, width: style.outlineWidth, color: style.outlineColor };
    });
    expect(outline.style).toBe('solid');
    expect(outline.width).toBe('2px');
    expect(outline.color).toBe('rgb(15, 110, 92)');
  });

  await test.step('klavyeyle sayfa değişince odak yeni sayfanın ana başlığına gelir ve başlık şirket adını taşır', async () => {
    await page.getByRole('link', { name: 'Müşteriler' }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { level: 1, name: 'Müşteriler' })).toBeFocused();
    await expect(page).toHaveTitle(`Müşteriler · ${TENANT_A} · Kulvar`);
  });

  await test.step('iletişim kutusu klavyeyle açılır, odak içinde kalır, Esc ile kapanır ve odak düğmeye döner', async () => {
    await page.getByRole('link', { name: 'Deniz Arslan' }).focus();
    await page.keyboard.press('Enter');
    const trigger = page.getByRole('button', { name: 'Müşteriyi sil' });
    await trigger.focus();
    await page.keyboard.press('Enter');
    const dialog = page.getByRole('dialog', { name: 'Müşteriyi sil' });
    await expect(dialog).toBeVisible();
    // Odak tuzağı: art arda Tab ile odak iletişim kutusunun dışına (arkadaki sayfaya) çıkmaz.
    for (let i = 0; i < 6; i += 1) {
      await page.keyboard.press('Tab');
      const outside = await page.evaluate(() => {
        const active = document.activeElement;
        return active !== null && active !== document.body && document.querySelector('.ws')?.contains(active) === true && active.closest('dialog') === null;
      });
      expect(outside, 'odak arkadaki sayfaya kaçmamalı').toBe(false);
    }
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(trigger).toBeFocused();
    // Müşteri silinmedi.
    await expect(page.getByRole('heading', { level: 1, name: 'Deniz Arslan' })).toBeVisible();
  });

  await test.step('form hatasında odak ilk hatalı alana gider ve hata alana bağlıdır', async () => {
    await openWorkspace(page, tenantA, 'customers/new', TENANT_A);
    await page.getByRole('button', { name: 'Müşteriyi kaydet' }).focus();
    await page.keyboard.press('Enter');
    const name = page.getByLabel('Ad soyad');
    await expect(name).toBeFocused();
    await expect(name).toHaveAttribute('aria-invalid', 'true');
    await expect(name).toHaveAccessibleDescription('Bu alan zorunludur.');
    await expect(page.getByRole('alert')).toContainText('Müşteri kaydedilmedi.');
  });

  await test.step('360 px: şerit üstte, gezinme "Menü" düğmesiyle açılır', async () => {
    await page.setViewportSize({ width: 360, height: 800 });
    await openWorkspace(page, tenantA, 'tickets', TENANT_A);
    await expect(companyName(page)).toBeVisible();
    const menu = page.getByRole('button', { name: 'Menü' });
    await expect(menu).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByRole('link', { name: 'Ekip' })).toBeHidden();
    await menu.focus();
    await page.keyboard.press('Enter');
    await expect(menu).toHaveAttribute('aria-expanded', 'true');
    const team = page.getByRole('link', { name: 'Ekip' });
    await expect(team).toBeVisible();
    await team.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { level: 1, name: 'Ekip' })).toBeFocused();
    // Sayfa değişince menü kendiliğinden kapanır.
    await expect(page.getByRole('button', { name: 'Menü' })).toHaveAttribute('aria-expanded', 'false');
  });

  await test.step('hareket azaltma tercihi: geçiş ve animasyon süreleri sıfırlanır', async () => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    const duration = await page.evaluate(() => window.getComputedStyle(document.querySelector('.btn') as Element).transitionDuration);
    expect(Number.parseFloat(duration)).toBeLessThan(0.01);
  });
});

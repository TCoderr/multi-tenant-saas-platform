// Uçtan uca test yardımcıları. Demo parolaları koşuya özel yerel dizinden okunur; hiçbir yere yazdırılmaz.

import { expect, type APIResponse, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

const localDir = path.resolve(process.cwd(), process.env.KULVAR_E2E_LOCAL_DIR ?? '../../.local-e2e');

export interface Account {
  key: string;
  email: string;
  name: string;
}

export const demo = {
  ownerA: { key: 'ayse.owner', email: 'ayse.owner@akasya.test', name: 'Ayşe Yıldız' },
  adminA: { key: 'ali.admin', email: 'ali.admin@akasya.test', name: 'Ali Demir' },
  agentA: { key: 'asli.agent', email: 'asli.agent@akasya.test', name: 'Aslı Kaya' },
  viewerA: { key: 'arda.viewer', email: 'arda.viewer@akasya.test', name: 'Arda Şahin' },
  ownerB: { key: 'burak.owner', email: 'burak.owner@boreas.test', name: 'Burak Aydın' },
  agentB: { key: 'belma.agent', email: 'belma.agent@boreas.test', name: 'Belma Çelik' },
  shared: { key: 'cem.ortak', email: 'cem.ortak@serbest.test', name: 'Cem Arslan' },
  operator: { key: 'operator', email: 'operator@kulvar.test', name: 'Platform Operatörü' },
} satisfies Record<string, Account>;

export const TENANT_A = 'Akasya Mobilya';
export const TENANT_B = 'Boreas Yazılım';

export function demoPassword(account: Account): string {
  return readFileSync(path.join(localDir, 'seed', `Seed__Passwords__${account.key}`), 'utf8');
}

async function csrfToken(page: Page): Promise<string> {
  const response = await page.request.get('/api/auth/csrf');
  expect(response.status(), 'CSRF token alınabilmeli').toBe(200);
  return ((await response.json()) as { token: string }).token;
}

/** Tarayıcı bağlamının cookie'leriyle (aynı oturum) CSRF başlıklı POST. */
export async function apiPost(page: Page, url: string, data?: unknown, headers: Record<string, string> = {}): Promise<APIResponse> {
  const token = await csrfToken(page);
  return page.request.post(url, { data, headers: { 'X-CSRF-TOKEN': token, ...headers } });
}

/** Hızlı giriş: gerçek giriş ucu, tarayıcı bağlamının cookie kabıyla. Parola hiçbir çıktıya yazılmaz. */
export async function loginAs(page: Page, account: Account, password: string = demoPassword(account)): Promise<void> {
  const response = await apiPost(page, '/api/auth/login', { email: account.email, password });
  expect(response.status(), `${account.key} giriş yapabilmeli`).toBe(200);
}

/** Giriş formu üzerinden giriş. */
export async function loginThroughForm(page: Page, email: string, password: string): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('E-posta').fill(email);
  await page.getByLabel('Parola').fill(password);
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect(page).toHaveURL(/\/app$/);
}

export async function newSession(browser: Browser, account: Account): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await loginAs(page, account);
  return { context, page };
}

interface MembershipRow {
  tenantId: string;
  tenantName: string;
  role: string;
  tenantStatus: string;
}

export async function memberships(page: Page): Promise<MembershipRow[]> {
  const response = await page.request.get('/api/me/memberships');
  expect(response.status()).toBe(200);
  return ((await response.json()) as { items: MembershipRow[] }).items;
}

export async function tenantIdOf(page: Page, tenantName: string): Promise<string> {
  const row = (await memberships(page)).find((item) => item.tenantName === tenantName);
  expect(row, `${tenantName} üyeliği bulunmalı`).toBeDefined();
  return row!.tenantId;
}

/** Müşteri listesi tablosu (aynı ad başka tablolarda da bağlantı olabildiği için sorgular tabloya daraltılır). */
export function customersTable(page: Page) {
  return page.getByRole('table', { name: 'Müşteriler' });
}

export function ticketsTable(page: Page) {
  return page.getByRole('table', { name: 'Talepler', exact: true });
}

export function companyName(page: Page) {
  return page.getByTestId('company-name');
}

/** Çalışma alanı kabuğu verilen şirketle açılana kadar bekler. */
export async function openWorkspace(page: Page, tenantId: string, section: string, tenantName: string): Promise<void> {
  await page.goto(`/t/${tenantId}/${section}`);
  await expect(companyName(page)).toHaveText(tenantName);
}

export function uniqueSuffix(): string {
  return `${Date.now().toString(36)}${Math.floor(Math.random() * 1296)
    .toString(36)
    .padStart(2, '0')}`;
}

/** Kullanıcının demo posta kutusunda konusu eşleşen ileti sayısı. */
export async function countMail(page: Page, subjectPart: RegExp): Promise<number> {
  const response = await page.request.get('/api/me/mailbox');
  expect(response.status()).toBe(200);
  return ((await response.json()) as { items: { subject: string }[] }).items.filter((item) => subjectPart.test(item.subject)).length;
}

/** Demo posta kutusunda konusu verilen metni içeren (en yeni) iletiyi açar (ileti arka planda üretilir; liste kendiliğinden yenilenir). */
export async function openMail(page: Page, subjectPart: RegExp, minimumCount = 1): Promise<void> {
  // İleti kuyruktan üretilir: koşul (en az N eşleşen ileti) sağlanana kadar yoklanır; sabit bekleme yoktur.
  await expect.poll(() => countMail(page, subjectPart), { timeout: 60_000 }).toBeGreaterThanOrEqual(minimumCount);
  await page.goto('/mail');
  const link = page.getByRole('link', { name: subjectPart }).first();
  await expect(link).toBeVisible();
  await link.click();
  await expect(page.getByRole('article')).toBeVisible();
}

import { defineConfig } from '@playwright/test';

// Uçtan uca testler yalnız scripts/e2e.ps1'in kurduğu izole, tek kullanımlık yığına karşı koşar (proje: kulvar-e2e).
// Kurulu Chrome kanalı kullanılır; yerel geliştirici sertifikası güvenilir olmadığı için HTTPS hataları yok sayılır.
// İz (trace) ve video kapalıdır: istek gövdeleri (giriş parolası) koşu çıktısına yazılmasın.
const baseURL = process.env.KULVAR_E2E_BASE_URL ?? 'https://localhost:8863';

export default defineConfig({
  testDir: './e2e',
  outputDir: './test-results/e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: true,
  timeout: 180_000,
  expect: { timeout: 20_000 },
  reporter: [['list'], ['json', { outputFile: './test-results/e2e-results.json' }]],
  use: {
    baseURL,
    channel: 'chrome',
    ignoreHTTPSErrors: true,
    locale: 'tr-TR',
    timezoneId: 'Europe/Istanbul',
    viewport: { width: 1440, height: 900 },
    trace: 'off',
    video: 'off',
    screenshot: 'only-on-failure',
    actionTimeout: 20_000,
    navigationTimeout: 30_000,
  },
});

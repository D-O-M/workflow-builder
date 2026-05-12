import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env['PLAYWRIGHT_BASE_URL'] ?? 'http://127.0.0.1:4200';
const skipWebServer = process.env['PLAYWRIGHT_SKIP_WEB_SERVER'] === '1';

export default defineConfig({
  testDir: './e2e/specs',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 2 : 0,
  workers: process.env['CI'] ? 2 : undefined,
  reporter: [
    ['list'],
    ['html', { open: 'never' }],
  ],
  expect: {
    timeout: 10_000,
  },
  use: {
    baseURL,
    testIdAttribute: 'data-testid',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  ...(skipWebServer
    ? {}
    : {
        webServer: {
          command: 'npm start -- --host 127.0.0.1 --port 4200',
          url: baseURL,
          reuseExistingServer: !process.env['CI'],
          timeout: 120_000,
        },
      }),
  projects: [
    {
      name: 'chromium',
      grepInvert: /@runtime|@mobile|@cross-browser/,
      use: { ...devices['Desktop Chrome'] },
    },
    {
      name: 'chromium-mobile',
      grep: /@mobile/,
      grepInvert: /@runtime/,
      use: { ...devices['Pixel 5'] },
    },
    {
      name: 'firefox-smoke',
      grep: /@cross-browser/,
      grepInvert: /@runtime/,
      use: { ...devices['Desktop Firefox'] },
    },
    {
      name: 'webkit-smoke',
      grep: /@cross-browser/,
      grepInvert: /@runtime/,
      use: { ...devices['Desktop Safari'] },
    },
    {
      name: 'runtime-chromium',
      grep: /@runtime/,
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});

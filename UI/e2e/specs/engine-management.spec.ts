import { expect, Page, test } from '@playwright/test';

async function mockManagement(page: Page, status = 200, managementOnly = false) {
  let revision = 2;
  let override = false;
  let endpoint: string | null = null;
  let application = 'Applied';
  let enabled = true;
  let conflict = false;
  const writes: { path: string; method: string; body: unknown }[] = [];
  const authority = 'https://identity.test/realms/workflow-builder';
  await page.addInitScript(({ authority }) => {
    sessionStorage.setItem('oidc.user:' + authority + ':workflow-builder-spa', JSON.stringify({
      access_token: 'management-test-token', token_type: 'Bearer', profile: { sub: 'operator' },
      expires_at: Math.floor(Date.now() / 1000) + 3600,
    }));
  }, { authority });
  await page.route('**/runtime-bootstrap.json', route => route.fulfill({ json: { apiBaseUrl: '' } }));
  await page.route('**/api/client-config', route => route.fulfill({ json: {
    apiBaseUrl: '', signalRBaseUrl: '', grafanaBaseUrl: '',
    authentication: { enabled: !managementOnly, authority, clientId: 'workflow-builder-spa', scope: 'openid' },
    managementAuthentication: { enabled: true, authority, clientId: 'workflow-builder-spa', scope: 'openid' },
  }}));
  await page.route('**/api/engine-management/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (status !== 200) return route.fulfill({ status, json: {} });
    if (request.headers()['authorization'] !== 'Bearer management-test-token') return route.fulfill({ status: 401, json: {} });
    if (request.method() !== 'GET') {
      writes.push({ path: decodeURIComponent(path), method: request.method(), body: request.postDataJSON() });
      if (conflict) { conflict = false; revision++; return route.fulfill({ status: 409, json: {} }); }
      revision++;
      if (path.includes('/plugins/')) enabled = request.postDataJSON().enabled;
      else if (decodeURIComponent(path).endsWith('EndpointUrl')) endpoint = request.method() === 'PUT' ? request.postDataJSON().value : null;
      else override = request.method() === 'PUT';
      return route.fulfill({ json: { revision, status: 'Saved' } });
    }
    if (path.endsWith('/settings')) return route.fulfill({ json: { revision, settings: [{
      key: 'Dcc:Gateway:ApiKey', capabilityId: 'network:dcc:gateway', capabilityName: 'DCC gateway access',
      secret: true, kind: 'String', overridden: override, value: null, required: false, description: 'Access token',
    }, {
      key: 'Dcc:Gateway:EndpointUrl', capabilityId: 'network:dcc:gateway', capabilityName: 'DCC gateway access',
      secret: false, kind: 'Uri', overridden: endpoint !== null, value: endpoint, required: false, description: 'Gateway endpoint',
    }] } });
    if (path.endsWith('/plugins')) return route.fulfill({ json: { revision, plugins: [{
      id: 1, name: 'Dcc.GatewaySubmit', displayName: 'Gateway submit', enabled: true,
      versions: ['1.2.3', '1.2.2'].map((version, index) => ({
        id: index + 10, version, lane: '1.2', enabled: index > 0 || enabled,
        effectiveEnabled: index > 0 || enabled, runnable: true, serving: index === (enabled ? 0 : 1), status: 'Runnable',
      })),
    }] } });
    return route.fulfill({ json: { revision, writesEnabled: true, status: application, engines: application === 'Awaiting an Engine' ? [] : [{
      engineId: 'engine-a', appliedRevision: application === 'Applied' ? revision : revision - 1, lastSeenAt: '2026-11-06T09:10:00Z', failureCode: null,
    }] } });
  });
  return { writes, conflict: () => { conflict = true; }, report: (state: string) => { application = state; } };
}

test('sets and removes a write-only secret and refreshes after a conflict', async ({ page }) => {
  const api = await mockManagement(page);
  await page.goto('/management/settings');
  await page.getByText('Engine status', { exact: true }).click();
  await expect(page.getByText('Applied revision 2')).toBeVisible();
  await page.getByRole('button', { name: 'Set secret' }).click();
  const input = page.getByLabel('New secret value');
  await expect(input).toHaveAttribute('type', 'password');
  await input.fill('test-rotation-value');
  await page.getByRole('button', { name: 'Save override' }).click();
  await expect(page.getByText('Saved revision 3.')).toBeVisible();
  expect(api.writes[0].body).toEqual({ expectedRevision: 2, value: 'test-rotation-value' });
  await expect(page.getByText('test-rotation-value')).toHaveCount(0);
  await page.getByRole('button', { name: 'Replace secret' }).click();
  await expect(input).toHaveValue('');
  await input.fill('stale-test-value');
  api.conflict();
  await page.getByRole('button', { name: 'Save override' }).click();
  await expect(page.getByRole('alert')).toContainText('Another operator');
  await page.getByRole('button', { name: 'Remove override' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Remove override' }).click();
  await expect(page.getByRole('button', { name: 'Set secret' })).toBeVisible();
});

test('shows the permission boundary on a direct management link', async ({ page }) => {
  await mockManagement(page, 403);
  await page.goto('/management/plugins');
  await expect(page.getByRole('alert')).toContainText('runtime engineer or administrator');
  await expect(page.getByRole('button', { name: 'Disable Plugin' })).toHaveCount(0);
});

test('uses a signed management session when the product allows anonymous access', async ({ page }) => {
  await mockManagement(page, 200, true);
  await page.goto('/management/');
  await expect(page.getByRole('button', { name: 'Set secret' })).toBeEnabled();
});

test('sets a declared endpoint while the fleet scales up and reports application', async ({ page }) => {
  const api = await mockManagement(page);
  api.report('Awaiting an Engine');
  await page.goto('/management/settings');
  await expect(page.getByText('Awaiting an Engine', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  await page.getByLabel('Override value').fill('https://gateway.example.test/');
  api.report('Applying');
  await page.getByRole('button', { name: 'Save override' }).click();
  await expect(page.getByText('Applying', { exact: true })).toBeVisible();
  await expect(page.getByText('https://gateway.example.test/', { exact: true })).toBeVisible();
  api.report('Applied');
  await page.getByRole('button', { name: 'Refresh management data' }).click();
  await expect(page.getByText('Applied', { exact: true })).toBeVisible();
  expect(api.writes[0].path).toBe('/api/engine-management/settings/Dcc:Gateway:EndpointUrl');
});

test('reviews a patch fallback and disables a version on a narrow screen @mobile', async ({ page }) => {
  const api = await mockManagement(page);
  await page.goto('/management/settings');
  await page.getByRole('button', { name: 'Toggle navigation' }).click();
  await page.getByRole('link', { name: 'Plugins' }).click();
  await page.getByRole('button', { name: 'Disable version' }).first().click();
  await expect(page.getByRole('dialog')).toContainText('1.2.2');
  await expect(page.getByRole('dialog')).toContainText('already admitted will finish');
  await page.getByRole('button', { name: 'Disable', exact: true }).click();
  await expect(page.getByText('Disabled by operator')).toBeVisible();
  expect(api.writes[0]).toMatchObject({ path: '/api/engine-management/plugins/1/versions/10', body: { expectedRevision: 2, enabled: false } });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

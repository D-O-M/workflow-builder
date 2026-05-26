import { test, expect } from '@playwright/test';
import { StartProcessPage } from '../pages/start-process-page';
import { ProcessExplorerPage } from '../pages/process-explorer-page';

test.describe('Runtime smoke', () => {
  test.setTimeout(120_000);

  test('starts a seeded process and inspects it in Process Explorer @runtime', async ({ page }) => {
    const startProcess = new StartProcessPage(page);
    await startProcess.goto();
    await startProcess.selectBlueprint('Showcase Airport Disruption Early Warning');
    await page.getByTestId('start-process-initial-input-AirportCode').fill('LHR');
    await page.getByTestId('start-process-initial-input-WindowStartIsoDate').fill('05/12/2026');
    await page.getByTestId('start-process-initial-input-WindowEndIsoDate').fill('05/13/2026');
    await page.getByTestId('start-process-continue-runtime-overrides').click();
    await page.getByTestId('start-process-submit').click();
    await expect(page).toHaveURL(/\/processes\?blueprintId=\d+&processId=/, { timeout: 60_000 });
    const processUrl = new URL(page.url());
    processUrl.searchParams.set('e2e', '1');
    await page.goto(`${processUrl.pathname}${processUrl.search}`);

    const explorer = new ProcessExplorerPage(page);
    await expect(page.getByTestId('process-explorer-workbench')).toBeVisible();
    await expect(page.getByTestId('process-explorer-process-pill')).toContainText(/Pending|In progress|Completed|Failed/i);
    await explorer.canvas.clickFirstNode();
    await expect(page.getByTestId('process-explorer-step-drawer')).toBeVisible();
  });
});

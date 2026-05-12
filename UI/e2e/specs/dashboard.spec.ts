import { test, expect } from '../support/test';
import { expectNoCriticalA11yViolations } from '../support/a11y';
import { DashboardPage } from '../pages/dashboard-page';

test.describe('Dashboard', () => {
  test('renders process health, recent processes, and observability controls @a11y @visual', async ({ page, workflowApi: _workflowApi }) => {
    const dashboard = new DashboardPage(page);
    await dashboard.goto();
    await dashboard.expectLoaded();

    await dashboard.selectWindow('24h');
    await dashboard.openBlueprintFilter();
    await page.getByRole('option', { name: 'Airport Disruption Advisor' }).click();
    await expect(page.getByTestId('dashboard-step-reliability-card')).toContainText('Dispatch Summary');
    await expectNoCriticalA11yViolations(page);
    await expect(page.getByTestId('dashboard-page')).toHaveScreenshot('dashboard-loaded.png', {
      animations: 'disabled',
      caret: 'hide',
    });
  });

  test('shows dashboard API failure state [dashboard-error]', async ({ page, workflowApi: _workflowApi }) => {
    const dashboard = new DashboardPage(page);
    await dashboard.goto();
    await expect(page.getByTestId('dashboard-error-state')).toContainText('Unable to load dashboard observability');
  });
});

import { test, expect } from '../support/test';
import { AppShellPage } from '../pages/app-shell-page';

test.describe('App shell', () => {
  test('navigates primary routes @cross-browser', async ({ page, workflowApi: _workflowApi }) => {
    const shell = new AppShellPage(page);
    await shell.goto('/dashboard');
    await shell.expectTitle('Dashboard');

    await shell.navigate('app-shell-nav-blueprints', 'Blueprint Builder');
    await expect(page).toHaveURL(/\/blueprints/);

    await shell.navigate('app-shell-nav-phases', 'Phases');
    await expect(page).toHaveURL(/\/phases/);

    await shell.navigate('app-shell-nav-start-process', 'Start Process');
    await expect(page).toHaveURL(/\/processes\/new/);

    await shell.navigate('app-shell-nav-processes', 'Process Explorer');
    await expect(page).toHaveURL(/\/processes/);
  });

  test('keeps mobile navigation usable @mobile', async ({ page, workflowApi: _workflowApi }) => {
    const shell = new AppShellPage(page);
    await shell.goto('/dashboard');
    await shell.openMobileNavigation();
    await page.getByTestId('app-shell-nav-blueprints').click();
    await shell.expectTitle('Blueprint Builder');
  });
});

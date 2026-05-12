import { test, expect } from '../support/test';
import { expectNoCriticalA11yViolations } from '../support/a11y';
import { ProcessExplorerPage } from '../pages/process-explorer-page';

test.describe('Process Explorer', () => {
  test('loads history, selects a process, opens step details, and shows logs @a11y @visual', async ({ page, workflowApi: _workflowApi }) => {
    const explorer = new ProcessExplorerPage(page);
    await explorer.goto();
    await explorer.selectFirstProcess();
    await explorer.canvas.expectNode('Weather Lookup');
    await explorer.openFirstStep();

    await expect(page.getByTestId('process-explorer-step-inputs')).toContainText('London');
    await page.getByRole('tab', { name: 'Logs' }).click();
    await expect(page.getByTestId('process-explorer-step-log-row').first()).toContainText('Loaded weather inputs');
    await expectNoCriticalA11yViolations(page);
    await expect(page.getByTestId('process-explorer-workbench')).toHaveScreenshot('process-explorer-detail.png', {
      animations: 'disabled',
      caret: 'hide',
    });
  });

  test('applies advanced search filters and opens a matching process', async ({ page, workflowApi: _workflowApi }) => {
    const explorer = new ProcessExplorerPage(page);
    await explorer.goto();
    await explorer.openAdvancedSearch();

    await page.getByTestId('process-search-blueprint-input').fill('Airport');
    await page.getByRole('option', { name: /Airport Disruption Advisor/ }).click();
    await page.getByTestId('process-search-blueprint-status-option-10-Completed').click();
    await page.getByTestId('process-search-apply').click();

    await expect(page.getByTestId('process-explorer-search-results')).toBeVisible();
    await page.getByTestId('process-explorer-search-result-row').first().click();
    await expect(page.getByTestId('process-explorer-workbench')).toBeVisible();
  });
});

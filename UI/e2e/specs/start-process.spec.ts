import { test, expect } from '../support/test';
import { StartProcessPage } from '../pages/start-process-page';

test.describe('Start Process', () => {
  test('selects a blueprint, fills dynamic inputs, and starts a process', async ({ page, workflowApi: _workflowApi }) => {
    const startProcess = new StartProcessPage(page);
    await startProcess.goto();
    await startProcess.selectBlueprint('Airport Disruption Advisor');
    await startProcess.launch();
    await expect(page).toHaveURL(/\/processes\?blueprintId=10&processId=/);
    await expect(page.getByTestId('process-explorer-workbench')).toContainText('Airport Disruption Advisor');
  });

  test('keeps the wizard usable on mobile @mobile', async ({ page, workflowApi: _workflowApi }) => {
    const startProcess = new StartProcessPage(page);
    await startProcess.goto();
    await startProcess.selectBlueprint('Airport Disruption Advisor');
    await expect(page.getByTestId('start-process-stepper')).toBeVisible();
    await expect(page.getByTestId('start-process-continue-global-state')).toBeVisible();
  });

  test('shows launch failures on the final step [start-process-error]', async ({ page, workflowApi: _workflowApi }) => {
    const startProcess = new StartProcessPage(page);
    await startProcess.goto();
    await startProcess.selectBlueprint('Airport Disruption Advisor');
    await startProcess.launch();
    await expect(page.getByTestId('start-process-error-message')).toContainText('Unable to start');
  });
});

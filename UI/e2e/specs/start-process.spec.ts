import { test, expect } from '../support/test';
import { StartProcessPage } from '../pages/start-process-page';
import { initialBlueprints } from '../fixtures/workflow-fixtures';

function createScrollableBlueprints(count: number) {
  const template = initialBlueprints[0]!;
  return Array.from({ length: count }, (_, index) => {
    const id = 100 + index;
    return {
      ...structuredClone(template),
      id,
      name: `Scrollable Blueprint ${String(index).padStart(2, '0')}`,
      versions: template.versions.map((version) => ({
        ...structuredClone(version),
        blueprintDefinitionId: id,
        blueprintVersionId: 10_000 + index,
      })),
    };
  });
}

test.describe('Start Process', () => {
  test('keeps a clicked blueprint selected from filtered suggestions', async ({ page, workflowApi: _workflowApi }) => {
    const startProcess = new StartProcessPage(page);
    await startProcess.goto();

    await page.getByTestId('start-process-search-input').fill('Municipal');
    await page.getByRole('option', { name: 'Municipal Readiness Advisor Blueprint' }).click();

    await expect(page.getByTestId('start-process-picker-trigger')).toContainText('Municipal Readiness Advisor');
    await expect(page.getByTestId('start-process-search-input')).toHaveCount(0);
    await expect(page.getByTestId('start-process-initial-input-forecast')).toBeVisible();
  });

  test('allows scrolling through long blueprint suggestion lists', async ({ page, workflowApi }) => {
    workflowApi.addBlueprints(createScrollableBlueprints(32));
    const startProcess = new StartProcessPage(page);
    await startProcess.goto();

    await page.getByTestId('start-process-search-input').click();
    const listbox = page.getByRole('listbox');
    await expect(listbox).toBeVisible();

    const before = await listbox.evaluate((element) => ({
      clientHeight: element.clientHeight,
      overflowY: getComputedStyle(element).overflowY,
      scrollHeight: element.scrollHeight,
      scrollTop: element.scrollTop,
    }));
    expect(before.scrollHeight).toBeGreaterThan(before.clientHeight);
    expect(['auto', 'scroll']).toContain(before.overflowY);

    const box = await listbox.boundingBox();
    if (!box) {
      throw new Error('Blueprint suggestion list was not laid out.');
    }

    await page.mouse.move(box.x + box.width / 2, box.y + Math.min(box.height - 4, box.height / 2));
    await page.mouse.wheel(0, 900);
    await expect.poll(() => listbox.evaluate((element) => element.scrollTop)).toBeGreaterThan(before.scrollTop);
  });

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
    await expect(page.getByTestId('start-process-continue-runtime-overrides')).toBeVisible();
  });

  test('shows launch failures on the final step [start-process-error]', async ({ page, workflowApi: _workflowApi }) => {
    const startProcess = new StartProcessPage(page);
    await startProcess.goto();
    await startProcess.selectBlueprint('Airport Disruption Advisor');
    await startProcess.launch();
    await expect(page.getByTestId('start-process-error-message')).toContainText('Unable to start');
  });
});

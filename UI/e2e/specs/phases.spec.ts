import { test, expect } from '../support/test';
import { PhasesPage } from '../pages/phases-page';

test.describe('Phases', () => {
  test('opens a phase series, toggles split mode, and renders Data Bridge mappings @visual', async ({ page, workflowApi: _workflowApi }) => {
    const phases = new PhasesPage(page);
    await phases.goto();
    await phases.openExistingSeries();

    await phases.sourceCanvas.expectNode('Dispatch Summary');
    await phases.destinationCanvas.expectNode('Dispatch Summary');
    await expect(page.getByTestId('phases-bridge-source-anchor')).toBeVisible();
    await expect(page.getByTestId('phases-bridge-destination-anchor')).toBeVisible();

    await phases.switchVertical();
    await expect(page.getByTestId('phases-bridge-guidance')).toBeVisible();
    await expect(page.getByTestId('phases-workbench')).toHaveScreenshot('phases-vertical-mapping.png', {
      animations: 'disabled',
      caret: 'hide',
    });

    await page.getByTestId('phases-horizontal-split').click();
    await expect(page.getByTestId('phases-canvas-stack')).toHaveClass(/is-horizontal/);
  });

  test('creates a new draft series and keeps setup open until enough phases exist', async ({ page, workflowApi: _workflowApi }) => {
    const phases = new PhasesPage(page);
    await phases.goto();
    await page.getByTestId('phases-empty-new-series').or(page.getByTestId('phases-new-series')).first().click();
    await expect(page.getByTestId('phases-setup-panel')).toBeVisible();
    await expect(page.getByTestId('phases-series-name-input')).toHaveValue('Untitled phase series');
    await expect(page.getByTestId('phases-add-phase-select')).toBeVisible();
  });
});

import { test, expect } from '../support/test';
import { expectNoCriticalA11yViolations } from '../support/a11y';
import { BlueprintBuilderPage } from '../pages/blueprint-builder-page';

test.describe('Blueprint Builder', () => {
  test('creates a draft, adds nodes, edits the canvas, and saves @a11y @visual', async ({ page, workflowApi }) => {
    const builder = new BlueprintBuilderPage(page);
    await builder.goto();
    await builder.createDraft();

    await builder.addPlugin('Weather Lookup');
    await builder.addPlugin('Dispatch Summary');
    await builder.closePluginLibrary();
    await page.getByTestId('blueprints-reset-view').click();
    await builder.canvas.dragNode('Dispatch Summary', 370, 90);
    await builder.canvas.connectOutputToInput('Weather Lookup', 'forecast', 'Dispatch Summary', 'forecast');
    await builder.canvas.connectOutputToInput('Weather Lookup', 'riskScore', 'Dispatch Summary', 'riskScore');

    await builder.openValidationMenu();
    await expect(page.getByTestId('blueprints-validation-menu')).toBeVisible();
    await page.keyboard.press('Escape');

    await builder.save();
    await expect.poll(() => workflowApi.lastCreatedBlueprint()?.name).toBe('E2E Blueprint Draft');
    await expectNoCriticalA11yViolations(page);
    await expect(page.getByTestId('blueprints-workspace')).toHaveScreenshot('blueprint-builder-graph.png', {
      animations: 'disabled',
      caret: 'hide',
    });
  });

  test('reports save failures without leaving the authoring surface [blueprint-save-error]', async ({ page, workflowApi: _workflowApi }) => {
    const builder = new BlueprintBuilderPage(page);
    await builder.goto();
    await builder.createDraft('Save Failure Draft');
    await builder.addPlugin('Weather Lookup');
    await builder.save();
    await expect(page.getByTestId('blueprints-save-error')).toContainText('Unable to save');
  });
});

import { test, expect } from '../support/test';
import { expectNoCriticalA11yViolations } from '../support/a11y';
import { BlueprintBuilderPage } from '../pages/blueprint-builder-page';
import { initialBlueprints, taskDefinitions } from '../fixtures/workflow-fixtures';

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
    await builder.canvas.expectNode('Weather Lookup');
    const renderState = await builder.canvas.waitForRender();
    expect(renderState?.nodeCount).toBeGreaterThan(0);
    await expect(page.getByTestId('blueprints-workspace')).toHaveScreenshot('blueprint-builder-graph.png', {
      animations: 'disabled',
      caret: 'hide',
    });
  });

  test('authors typed Conditional Branch rules and saves nested decisions', async ({ page, workflowApi }) => {
    const builder = new BlueprintBuilderPage(page);
    await builder.goto();
    await builder.createDraft('Forecast Decision');
    await builder.addPlugin('Weather Lookup');
    await builder.addPlugin('Conditional Branch');
    await builder.closePluginLibrary();
    await page.getByTestId('blueprints-reset-view').click();
    await builder.canvas.dragNode('Conditional Branch', 185, 75);
    await builder.canvas.connectSummaryPorts('Weather Lookup', 'Conditional Branch');
    await expect.poll(async () => (await builder.canvas.waitForRender())?.connectionCount).toBe(1);
    await builder.canvas.clickNode('Conditional Branch');

    const drawer = page.getByTestId('blueprints-node-settings');
    await drawer.getByRole('button', { name: 'Add condition', exact: true }).click();
    await drawer.getByRole('combobox', { name: 'Field', exact: true }).selectOption('forecast');
    const comparison = drawer.getByRole('combobox', { name: 'Comparison', exact: true });
    await expect(comparison.locator('option[value="gt"]')).toHaveCount(0);
    await comparison.selectOption('startsWith');
    await drawer.getByRole('textbox', { name: 'Value 1', exact: true }).fill('rain');
    await drawer.getByLabel('Ignore case').check();
    await drawer.getByRole('button', { name: 'Add group', exact: true }).click();
    const groups = drawer.getByRole('group', { name: 'Group', exact: true });
    await expect(groups).toHaveCount(2);
    const group = groups.nth(1);
    await group.getByRole('combobox', { name: 'Match conditions', exact: true }).selectOption('any');
    await group.getByLabel('NOT', { exact: true }).check();
    await group.getByRole('button', { name: 'Add condition' }).focus();
    await page.keyboard.press('Enter');
    await group.getByRole('combobox', { name: 'Field', exact: true }).selectOption('riskScore');
    const numericComparison = group.getByRole('combobox', { name: 'Comparison', exact: true });
    await expect(numericComparison.locator('option[value="startsWith"]')).toHaveCount(0);
    await numericComparison.selectOption('gte');
    await group.getByRole('textbox', { name: 'Value 1', exact: true }).fill('9007199254740993.25');
    await group.getByRole('textbox', { name: 'Value 1', exact: true }).press('Tab');

    const moveGroupUp = group.getByRole('button', { name: 'Move group up', exact: true });
    const moveGroupDown = group.getByRole('button', { name: 'Move group down', exact: true });
    await expect(drawer.getByRole('button', { name: 'Move condition up', exact: true }).first()).toBeDisabled();
    await expect(moveGroupDown).toBeDisabled();
    await expect(group.getByRole('button', { name: 'Move condition down', exact: true })).toBeDisabled();
    await moveGroupUp.focus();
    await page.keyboard.press('Enter');
    await expect(moveGroupUp).toBeDisabled();
    await expect(moveGroupDown).toBeFocused();
    await expect(drawer.getByRole('button', { name: 'Move condition down', exact: true }).last()).toBeDisabled();
    await expect(drawer).toContainText('(NOT (riskScore Greater than or equal to 9007199254740993.25) AND forecast Starts with "rain" (ignore case))');

    await expect(drawer.getByRole('button', { name: /Rule\./ })).toHaveCount(0);
    await page.getByTestId('blueprints-close-node-settings').click();
    await builder.save();
    await expect.poll(() => workflowApi.lastCreatedBlueprint()?.name).toBe('Forecast Decision');
    const branch = workflowApi.lastCreatedBlueprint()!.versions[0].tasks.find(node => node.nodeKind === 'ConditionalBranch')!;
    expect(branch.taskDefinitionId).toBeNull();
    expect(branch.conditionalFields?.map(field => field.name)).toEqual(['forecast', 'riskScore', 'confidence']);
    expect(branch.conditionalRule).toMatchObject({ kind: 'all', children: [
      { kind: 'any', negated: true, children: [
        { field: 'riskScore', operator: 'gte', operands: [{ literal: '9007199254740993.25' }] },
      ] },
      { field: 'forecast', operator: 'startsWith', ignoreCase: true, operands: [{ literal: 'rain' }] },
    ] });

    const saved = workflowApi.lastCreatedBlueprint()!;
    await page.goto('/blueprints?e2e=1&blueprintId=' + saved.id + '&versionId=' + saved.versions[0].blueprintVersionId);
    await expect.poll(async () => (await builder.canvas.snapshot()).nodes.length).toBe(2);
    await page.getByTestId('blueprints-reset-view').click();
    await builder.canvas.clickNode('Conditional Branch');
    await expect(drawer.getByRole('combobox', { name: 'Field', exact: true }).first()).toHaveValue('riskScore');
    await expect(drawer.getByRole('textbox', { name: 'Value 1', exact: true }).first()).toHaveValue('9007199254740993.25');
  });

  test('wraps a saved single comparison without changing its negation', async ({ page, workflowApi }) => {
    const source = initialBlueprints[0].versions[0].tasks[0];
    const fields = taskDefinitions[0].versions[0].outputChannels[0].properties;
    workflowApi.addBlueprints([{
      id: 75, name: 'Forecast threshold', description: '',
      versions: [{
        blueprintDefinitionId: 75, blueprintVersionId: 750, version: 1, startingBlueprintNodeId: source.instanceId,
        tasks: [
          { ...source, outgoingConnections: [{ ...source.outgoingConnections[0], blueprintNodeId: 'decision', propertyMappings: [] }] },
          {
            instanceId: 'decision', nodeKind: 'ConditionalBranch', taskDefinitionId: null,
            position: { x: 550, y: 80 }, allowedMajor: 0, allowedMinor: 0, resolvedVersionId: 0,
            designTimeInputOverrides: {}, outgoingConnections: [], conditionalFields: fields,
            conditionalRule: { kind: 'comparison', field: 'riskScore', operator: 'lt', negated: true,
              operands: [{ literal: '9007199254740993.25' }] },
          },
        ],
      }],
    }]);

    const builder = new BlueprintBuilderPage(page);
    await page.goto('/blueprints?e2e=1&blueprintId=75&versionId=750');
    await expect.poll(async () => (await builder.canvas.snapshot()).nodes.some(node => node.name === 'Conditional Branch')).toBe(true);
    await page.getByTestId('blueprints-reset-view').click();
    await builder.canvas.clickNode('Conditional Branch');
    const drawer = page.getByTestId('blueprints-node-settings');
    const original = 'NOT riskScore Less than 9007199254740993.25';
    await expect(drawer).toContainText(original);
    await drawer.getByRole('button', { name: 'Wrap in group' }).click();
    await expect(drawer).toContainText('(' + original + ')');
    await expect(drawer.getByRole('group', { name: 'Condition', exact: true }).getByRole('checkbox', { name: 'NOT', exact: true })).toBeChecked();
    await drawer.getByRole('combobox', { name: 'Match conditions' }).selectOption('any');
    await drawer.getByRole('button', { name: 'Add condition', exact: true }).click();
    await expect(drawer.getByRole('group', { name: 'Condition', exact: true })).toHaveCount(2);
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

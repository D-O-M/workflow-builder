import { test, expect } from '../support/test';
import { expectNoCriticalA11yViolations } from '../support/a11y';
import { ProcessExplorerPage } from '../pages/process-explorer-page';
import { processDetail, stepDetail } from '../fixtures/workflow-fixtures';

test.describe('Process Explorer', () => {
  test('shows the versioned rule and individual routing decisions when reopening a Process', async ({ page, workflowApi: _workflowApi }) => {
    const detail = processDetail();
    const branch = detail.nodes[0]!;
    branch.nodeKind = 'ConditionalBranch';
    branch.name = 'Conditional Branch';
    branch.taskDefinitionId = 0;
    branch.resolvedVersion = '0.0.0';
    branch.conditionalFields = [{ name: 'riskScore', kind: 'Decimal', required: true, enumValues: [] }];
    branch.conditionalRule = { kind: 'comparison', field: 'riskScore', operator: 'gt', operands: [{ literal: '0.5' }] };
    branch.outputChannelNames = ['True', 'False'];
    branch.selectedChannels = ['True', 'False'];
    branch.outgoingConnections[0]!.channelName = 'True';
    const step = {
      ...stepDetail(branch.instanceId), nodeKind: branch.nodeKind, name: branch.name, resolvedVersion: '0.0.0',
      conditionalRule: branch.conditionalRule, conditionalFields: branch.conditionalFields,
      selectedChannel: null, iterations: ['True', 'False'].map((selectedChannel, itemIndex) => ({
        selectedChannel, itemIndex, itemKey: String(itemIndex), status: 'Completed', runtimeStepId: 'step-' + itemIndex,
        sourceIteratorStepId: 'iterator-step', sourceIteratorBlueprintNodeId: 'iterator-node',
      })),
    };
    const apiPath = '**/api/blueprints/' + detail.blueprintDefinitionId + '/processes/' + detail.processId;
    await page.route(apiPath, route => route.fulfill({ json: detail }));
    await page.route(apiPath + '/steps/' + branch.instanceId, route => route.fulfill({ json: step }));
    const explorer = new ProcessExplorerPage(page);
    await page.goto('/processes?e2e=1&blueprintId=' + detail.blueprintDefinitionId + '&processId=' + detail.processId);
    for (let visit = 0; visit < 2; visit++) {
      if (visit) await page.reload();
      await explorer.canvas.clickNode('Conditional Branch');
      await expect(page.getByTestId('process-explorer-step-drawer')).toContainText('Blueprint v2');
      await expect(page.getByTestId('process-explorer-step-drawer')).toContainText('Native Step');
      await expect(page.getByTestId('process-conditional-decision')).toContainText(/riskScore Greater than 0\.5/i);
      await expect(page.getByTestId('process-conditional-decision')).not.toContainText('Selected output');
      const outcomes = page.getByRole('list', { name: 'Iteration outcomes' });
      await expect(outcomes.getByRole('listitem')).toHaveText(['Item 1 · Succeeded · True', 'Item 2 · Succeeded · False']);
    }
  });

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

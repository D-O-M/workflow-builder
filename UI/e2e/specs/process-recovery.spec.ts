import { test, expect } from '../support/test';
import { initialProcesses, stepDetail } from '../fixtures/workflow-fixtures';
import { ProcessExplorerPage } from '../pages/process-explorer-page';
import type { RecoveryPage, RecoveryStep } from '../../src/app/domain/process-recovery.service';
import type { Page } from '@playwright/test';

async function enableRecovery(page: Page) {
  await page.route('**/api/client-config', route => route.fulfill({ json: {
    apiBaseUrl: '', signalRBaseUrl: '', grafanaBaseUrl: '', features: { processRecoveryEnabled: true },
  } }));
}

test('recovers a failed Step with a refreshed lookup and the original payment receipt', async ({ page, workflowApi: _api }) => {
  await page.setViewportSize({ width: 1800, height: 1000 });
  const process = initialProcesses[1]!;
  const failedId = 'failed-delivery';
  await enableRecovery(page);
  await page.route('**/steps/node-summary', route => route.fulfill({ json: {
    ...stepDetail('node-summary'), runtimeStepId: failedId, status: 'Failed',
  } }));
  const step = (stepId: string, name: string, action: 'Run' | 'Reuse'): RecoveryStep => ({
    stepId, logicalStepId: stepId, blueprintNodeId: stepId, name,
    status: action === 'Reuse' ? 'Completed' : 'Failed', action, canStartHere: true,
    inScope: action === 'Run', history: [], missingInputs: [],
  });
  const result: RecoveryPage = {
    id: 'recovery-1', revision: 1, status: 'Preview', processCount: 1, stepCount: 1,
    createdAt: '2026-11-01T12:00:00Z', actor: 'operator', confirmedRevisions: [], hasMore: false,
    cases: [{ processId: process.processId, blueprintDefinitionId: 10, blueprintVersionId: 100, status: 'Preview',
      plan: { targetStepIds: [failedId], continuationStepIds: [failedId], newWorkPolicy: 'Pause', issues: [],
        steps: [step('lookup', 'Address lookup', 'Reuse'),
          { ...step('payment', 'Take payment', 'Reuse'), recoveryAction: 'Reuse', reusedFromStepId: 'original-payment',
            outputs: { receipt: 'receipt-17' }, history: [
              { stepId: 'original-payment', status: 'Completed', outputs: { receipt: 'receipt-17' } },
              { stepId: 'payment', status: 'Completed', recoveryAction: 'Reuse', reusedFromStepId: 'original-payment', outputs: { receipt: 'receipt-17' } },
            ] },
          step(failedId, 'Receipt delivery', 'Run')] } }],
  };
  const plan = result.cases[0]!.plan!;
  await page.route('**/api/process-recoveries/preview', async route => {
    expect(route.request().postDataJSON()).toEqual({ processId: process.processId, runtimeStepId: failedId });
    await route.fulfill({ json: result });
  });
  await page.route('**/api/process-recoveries/recovery-1/preview', async route => {
    const body = route.request().postDataJSON();
    expect(body.newWorkPolicy).toBeUndefined();
    expect(body.edits).toHaveLength(1);
    expect(body.edits[0].stepId).toBe('lookup');
    if (result.revision === 1) {
      expect(body.edits[0].continuation).toBe(true);
      plan.continuationStepIds = ['lookup'];
      plan.steps.forEach(step => step.inScope = true);
    } else {
      expect(body.edits[0].action).toBe('Run');
      plan.steps[0]!.action = 'Run';
    }
    result.revision++;
    await route.fulfill({ json: result });
  });
  await page.route('**/api/process-recoveries/recovery-1/confirm', async route => {
    expect(route.request().postDataJSON()).toEqual({ revision: 3, reason: 'Refresh delivery address' });
    await route.fulfill({ json: { ...result, reason: 'Refresh delivery address', status: 'Completed',
      confirmedRevisions: [3], cases: [{ ...result.cases[0], status: 'Completed' }] } });
  });
  await page.goto('/processes?e2e=1&blueprintId=10&processId=' + process.processId);
  await new ProcessExplorerPage(page).canvas.clickNode('Dispatch Summary');
  await page.getByTestId('process-recover-step').click();
  const dialog = page.getByRole('dialog', { name: 'Step recovery' });
  await dialog.locator('.step').filter({ hasText: 'Address lookup' }).getByRole('button', { name: 'Start here' }).click();
  await dialog.getByTestId('recovery-revise').click();
  await expect(dialog.getByLabel('Decision for Take payment')).toHaveValue('Reuse');
  await expect(dialog.getByLabel('Source attempt').getByRole('option', { name: /Used recorded outcome.*Source original-payment/ })).toHaveCount(1);
  await dialog.getByLabel('Decision for Address lookup').selectOption('Run');
  await expect(dialog.getByTestId('recovery-confirm')).toHaveCount(0);
  await dialog.getByTestId('recovery-revise').click();
  await dialog.locator('.step').filter({ hasText: 'Take payment' }).getByText('Recorded inputs and outputs').click();
  await expect(dialog.getByText('receipt-17', { exact: false })).toBeVisible();
  await dialog.getByTestId('recovery-reason').fill('Refresh delivery address');
  await dialog.getByTestId('recovery-confirm').click();
  await expect(dialog.getByRole('heading', { name: 'Recovery progress' })).toBeVisible();
});

test('previews all search matches across pages with exclusions and shows missing inputs for skipped payments', async ({ page, workflowApi: _api }) => {
  await enableRecovery(page);
  const [excluded, target] = initialProcesses;
  await page.route('**/api/processes/search', route => {
    const next = !!route.request().postDataJSON().cursor;
    return route.fulfill({ json: { items: [next ? target : excluded], hasMore: !next, nextCursor: next ? null : 'page-two' } });
  });
  const failed: RecoveryStep = { stepId: 'payment', logicalStepId: 'payment', blueprintNodeId: 'charge', name: 'Charge card',
    status: 'Failed', action: 'Run', canStartHere: true, inScope: true, history: [], missingInputs: [] };
  const delivery: RecoveryStep = { stepId: 'delivery', logicalStepId: 'delivery', blueprintNodeId: 'delivery', name: 'Delivery',
    status: 'NotExecuted', action: 'Run', canStartHere: false, inScope: true, history: [], missingInputs: [] };
  const result: RecoveryPage = { id: 'batch', revision: 1, status: 'Preview', processCount: 1, stepCount: 1,
    createdAt: '2026-11-01T12:00:00Z', actor: 'operator', confirmedRevisions: [], hasMore: false, cases: [
      { processId: target!.processId, blueprintDefinitionId: 10, blueprintVersionId: 100, status: 'Preview',
        plan: { steps: [failed, delivery], targetStepIds: ['payment'], continuationStepIds: ['payment'], newWorkPolicy: 'Pause', issues: [] } },
    ] };
  await page.route('**/api/process-recoveries/preview', route => {
    const selected = route.request().postDataJSON();
    expect(selected.allMatches).toBe(true);
    expect(selected.excludedProcessIds).toEqual([excluded!.processId]);
    expect(selected.search.cursor).toBeFalsy();
    return route.fulfill({ json: result });
  });
  await page.route('**/api/process-recoveries/batch/preview', route => {
    const revision = route.request().postDataJSON();
    expect(revision.edits[0]).toMatchObject({ stepId: 'payment', processId: target!.processId, action: 'Skip' });
    expect(revision.newWorkPolicy).toBe('Run');
    failed.action = 'Skip';
    delivery.missingInputs = ['paymentReference'];
    result.newWorkPolicy = 'Run';
    result.cases[0]!.plan!.newWorkPolicy = 'Run';
    result.revision++;
    return route.fulfill({ json: result });
  });
  const explorer = new ProcessExplorerPage(page);
  await explorer.goto();
  await explorer.openAdvancedSearch();
  await page.getByTestId('process-search-blueprint-input').fill('Airport');
  await page.getByRole('option', { name: /Airport Disruption Advisor/ }).click();
  await page.getByTestId('process-search-apply').click();
  await page.getByTestId('recovery-select-all').check();
  await page.getByRole('checkbox', { name: 'Include Process ' + excluded!.processId + ' in recovery' }).uncheck();
  await page.getByTestId('process-explorer-load-more-search-results').click();
  await expect(page.getByRole('checkbox', { name: 'Include Process ' + target!.processId + ' in recovery' })).toBeChecked();
  await page.getByRole('button', { name: 'Recover all matches' }).click();
  const dialog = page.getByRole('dialog', { name: 'Step recovery' });
  await dialog.getByLabel('Decision for Charge card').selectOption('Skip');
  await dialog.getByTestId('recovery-new-work-policy').selectOption('Run');
  await dialog.getByTestId('recovery-revise').click();
  await expect(dialog.getByText('Delivery requires paymentReference')).toBeVisible();
  await expect(dialog.getByLabel('Decision for Delivery').locator('option[value="Reuse"]')).toBeDisabled();
});

test('reopens a held recovery, reads an earlier decision and revises the current case', async ({ page, workflowApi: _api }) => {
  await enableRecovery(page);
  const { processId, blueprintDefinitionId, blueprintVersionId } = initialProcesses[1]!;
  const batch: RecoveryPage = {
    id: 'held-batch', revision: 4, status: 'Completed', actor: 'Ada', reason: 'Review the changed path',
    confirmedRevisions: [2, 4], createdAt: '2026-11-01T12:00:00Z', processCount: 1, stepCount: 1, hasMore: false,
    cases: [{ processId, blueprintDefinitionId, blueprintVersionId, status: 'Held', message: 'New branch requires review.',
      plan: { steps: [], targetStepIds: [], continuationStepIds: [], newWorkPolicy: 'Pause', issues: [] } }],
  };
  await page.route('**/api/process-recoveries?**', route => route.fulfill({ json: [batch] }));
  await page.route('**/api/process-recoveries/held-batch?**', route => {
    const revision = new URL(route.request().url()).searchParams.get('revision');
    return route.fulfill({ json: revision === '2' ? { ...batch, revision: 2, reason: 'Original decision' } : batch });
  });
  await page.route('**/api/process-recoveries/held-batch/preview', route => {
    expect(route.request().postDataJSON()).toEqual({ revision: 4, edits: [] });
    return route.fulfill({ json: { ...batch, revision: 5, status: 'Preview', reason: null,
      cases: [{ ...batch.cases[0], status: 'Preview', message: null }] } });
  });
  const explorer = new ProcessExplorerPage(page);
  await explorer.goto();
  await explorer.openHistory();
  await page.getByTestId('process-recovery-history').click();
  const dialog = page.getByRole('dialog', { name: 'Step recovery' });
  await dialog.getByRole('button', { name: /Review the changed path/ }).click();
  await dialog.getByLabel('Recovery revision').selectOption('2');
  await expect(dialog.getByText('Original decision')).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Review unresolved cases' })).toHaveCount(0);
  await dialog.getByLabel('Recovery revision').selectOption('');
  await dialog.getByRole('button', { name: 'Review unresolved cases' }).click();
  await expect(dialog.getByRole('heading', { name: 'Recovery preview' })).toBeVisible();
  await dialog.getByRole('link', { name: 'Open Process history' }).click();
  await expect(page).toHaveURL(new RegExp('processId=' + processId));
  await expect(dialog).toHaveCount(0);
});

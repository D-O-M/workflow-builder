import type { Page } from '@playwright/test';
import type { HandoffValues, PhaseSeriesOverview, PhaseSeriesSummary } from '../../src/app/domain/phase-series-explorer.service';
import { initialPhasedBlueprints, processDetail, stepDetail } from '../fixtures/workflow-fixtures';
import { ProcessExplorerPage } from '../pages/process-explorer-page';
import { StartProcessPage } from '../pages/start-process-page';
import { expectNoCriticalA11yViolations } from '../support/a11y';
import { expect, test } from '../support/test';

const definition = initialPhasedBlueprints[0]!;
const parentId = '73680bb2-a8ea-49a0-8a75-c9c8295394f8';
const sourceId = 'db96973b-5555-45ef-91c5-780606a66e64';
const destinationId = 'fa24eaa7-b83b-4b39-b875-9542378836fb';
const childId = 'ff958ee6-ecf7-485b-8bb8-ae7d267ff2b0';
const started = '2026-05-12T10:10:00Z';
const updated = '2026-05-12T10:12:30Z';
const baseUrl = `/api/phased-blueprints/${definition.id}/processes`;
const parentPath = `/processes/phase-series/${definition.id}/${parentId}`;

function waitingParent(): PhaseSeriesOverview {
  const first = {
    phaseId: sourceId, phaseIndex: 0, phaseName: 'Check score', blueprintDefinitionId: 10,
    blueprintName: 'Score check', blueprintVersionId: 100, blueprintVersionNumber: 2,
    versionPolicy: 'Pinned' as const, status: 'Completed' as const,
    childProcessId: 'e70e4e03-b5ed-49d0-a9ea-d63c80e590b8', startedAt: started, completedAt: updated,
  };
  return {
    phasedProcessId: parentId, phasedBlueprintDefinitionId: definition.id, phasedBlueprintName: definition.name,
    phasedBlueprintVersionId: 500, phasedBlueprintVersionNumber: 1, currentPhaseId: destinationId,
    status: 'WaitingForTrigger', createdAt: started, updatedAt: updated,
    phases: [first, { ...first, phaseId: destinationId, phaseIndex: 1, phaseName: 'Review score', status: 'Pending',
      childProcessId: null, startedAt: null, completedAt: null }],
    transitions: [{ transitionId: 'eca76638-8a74-41d5-9189-daa97af39178', sourcePhaseId: sourceId, destinationPhaseId: destinationId }],
  };
}

function summary(parent: PhaseSeriesOverview): PhaseSeriesSummary {
  return {
    phasedProcessId: parent.phasedProcessId, phasedBlueprintDefinitionId: parent.phasedBlueprintDefinitionId,
    phasedBlueprintName: parent.phasedBlueprintName, phasedBlueprintVersionId: parent.phasedBlueprintVersionId,
    phasedBlueprintVersionNumber: parent.phasedBlueprintVersionNumber, status: parent.status,
    createdAt: parent.createdAt, updatedAt: parent.updatedAt, phaseCount: 2, completedPhaseCount: 1,
    currentPhaseName: parent.phases[1]!.phaseName, currentPhaseIndex: 1,
  };
}

async function mockOverview(page: Page, parent: PhaseSeriesOverview) {
  await page.route(`**${baseUrl}/${parentId}/overview`, route => route.fulfill({ json: parent }));
}

test('finds repeated starts by name, status and parent ID and preserves the search on Back', async ({ page, workflowApi: _api }) => {
  const parent = waitingParent();
  await mockOverview(page, parent);
  const waiting = summary(parent);
  const results = [
    { ...waiting, phasedProcessId: '6e9eaebd-fddb-46e3-827e-d6113762f673', status: 'Completed' },
    waiting, { ...waiting, phasedBlueprintName: 'Claims review', phasedProcessId: '97a08980-e7c1-466c-858a-d085a6056562' },
  ];
  await page.route('**/api/phased-processes?*', route => {
    const params = new URL(route.request().url()).searchParams;
    const q = (params.get('q') ?? '').toLowerCase();
    const matches = results.filter(row => (!params.get('status') || row.status === params.get('status'))
      && (row.phasedProcessId === q || row.phasedBlueprintName.toLowerCase().includes(q)));
    const offset = Number(params.get('continuationToken'));
    return route.fulfill({ json: { items: matches.slice(offset, offset + 2), continuationToken: matches.length > offset + 2 ? '2' : null } });
  });

  await page.goto('/processes');
  await page.getByTestId('process-explorer-phase-series-link').click();
  const rows = page.getByTestId('phase-series-result');
  await expect(rows).toHaveCount(2);
  await page.getByTestId('phase-series-next').click();
  await expect(rows).toHaveCount(3);
  await page.getByTestId('phase-series-search').fill('AIRPORT');
  await page.getByTestId('phase-series-status').selectOption('WaitingForTrigger');
  await page.getByTestId('phase-series-apply').click();
  await expect(rows).toHaveCount(1);
  await expect(rows).toContainText(parentId);
  await rows.focus();
  await rows.press('Enter');
  await expect(page.getByTestId('phase-series-overview')).toContainText('Review score');
  await expect(page.getByTestId('app-shell-nav-processes')).toHaveAttribute('aria-current', 'page');
  await page.goBack();
  await expect(page.getByTestId('phase-series-search')).toHaveValue('AIRPORT');
  await expect(page.getByTestId('phase-series-status')).toHaveValue('WaitingForTrigger');
  await page.getByTestId('phase-series-search').fill(parentId);
  await page.getByTestId('phase-series-apply').click();
  await expect(rows).toHaveCount(1);
  await page.getByTestId('phase-series-search').fill('unknown series');
  await page.getByTestId('phase-series-apply').click();
  await expect(rows).toHaveCount(0);
  await expect(page.getByText(/No started Phase Series/)).toBeVisible();
  await expectNoCriticalA11yViolations(page);
});

test('opens the new parent on start, inspects a waiting handoff and drills into the triggered child', async ({ page, workflowApi: _api }) => {
  const parent = waitingParent();
  await mockOverview(page, parent);
  await page.route(`**${baseUrl}`, route => route.fulfill({ status: 202, json: {
    phasedProcessId: parentId, phasedBlueprintDefinitionId: definition.id, phasedBlueprintVersionId: 500,
    childProcessId: parent.phases[0]!.childProcessId, phaseIndex: 0, acceptedAt: started,
  } }));
  const values: HandoffValues = {
    ...parent.transitions[0]!, mappings: [{
      sourceNodeId: 'score', sourceNodeName: 'Score check', sourceChannel: 'Success', sourceProperty: 'Health',
      targetNodeId: 'review', targetNodeName: 'Review', targetProperty: 'Score', hasValue: true, valueJson: '14',
    }], unattributedValues: [{ property: 'Audit', valueJson: '{"id":9223372036854775807}' }],
  };
  let handoffReads = 0;
  await page.route(`**${baseUrl}/${parentId}/handoffs/${sourceId}`, route => {
    handoffReads++;
    return route.fulfill({ json: values });
  });
  const triggerUrl = `/api/phased-blueprints/${definition.id}/phases/1/triggers`;
  await page.route(`**${triggerUrl}`, route => {
    expect(route.request().postDataJSON().triggerInput.Score).toBe(1);
    Object.assign(parent.phases[1]!, { childProcessId: childId, startedAt: updated, status: 'InProgress' });
    parent.status = 'InProgress';
    return route.fulfill({ status: 202, json: { phasedProcessId: parentId, childProcessId: childId } });
  });
  let childReads = 0;
  const childApi = `**/api/blueprints/10/processes/${childId}`;
  await page.route(childApi, route => {
    childReads++;
    return route.fulfill({ json: { ...processDetail(childId), phase: { phasedProcessId: parentId,
      phasedBlueprintDefinitionId: definition.id, phasedBlueprintVersionId: 500, phaseId: destinationId,
      phaseIndex: 1, phaseName: 'Review score' } } });
  });
  await page.route(childApi + '/steps/node-weather', route =>
    route.fulfill({ json: { ...stepDetail('node-weather'), inputs: { Score: 14 } } }));

  const start = new StartProcessPage(page);
  await start.goto();
  await page.getByTestId('start-process-search-input').fill(definition.name);
  await page.getByRole('option', { name: new RegExp(definition.name) }).click();
  await start.launch();
  await expect(page).toHaveURL(url => url.pathname === parentPath);
  await expect(page.getByTestId('phase-series-overview')).toContainText('Waiting for trigger');
  expect(handoffReads).toBe(0);
  await page.getByTestId('phase-series-handoff').press('Enter');
  const bridge = page.getByTestId('phase-series-values');
  await expect(bridge).toContainText('Health');
  await expect(bridge).toContainText('Score');
  await expect(bridge).toContainText('14');
  await expect(bridge).toContainText('9223372036854775807');
  expect(handoffReads).toBe(1);
  await expectNoCriticalA11yViolations(page);
  const status = await page.evaluate(async url => (await fetch(url, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ phasedBlueprintVersionId: 500, triggerInput: { Score: 1, Speed: 14 } }),
  })).status, triggerUrl);
  expect(status).toBe(202);
  const child = page.getByTestId('phase-series-phase').last().getByTestId('phase-series-open-child');
  await expect(child).toBeVisible();
  expect(childReads).toBe(0);
  // Enable the existing read-only canvas inspection seam for the child page.
  await page.evaluate(() => sessionStorage.setItem('workflowBuilderE2eCanvas', '1'));
  await child.click();
  await expect(page.getByTestId('process-explorer-parent-link')).toHaveAttribute('href', parentPath);
  await new ProcessExplorerPage(page).openFirstStep();
  await expect(page.getByTestId('process-explorer-step-inputs')).toContainText('14');
  await page.goBack();
  await expect(page).toHaveURL(url => url.pathname === parentPath);
});

test('keeps the recorded startup failure accessible when a reserved child cannot be opened', async ({ page, workflowApi: _api }) => {
  const parent = waitingParent();
  parent.status = 'Failed';
  Object.assign(parent.phases[1]!, { status: 'Failed', childProcessId: childId, failureMessage: 'Child startup failed.' });
  await mockOverview(page, parent);
  await page.route(`**/api/blueprints/10/processes/${childId}`, route => route.fulfill({ status: 404 }));
  await page.goto(parentPath);
  await expect(page.getByTestId('phase-series-overview')).toContainText('Child startup failed.');
  await page.getByTestId('phase-series-open-child').last().click();
  await page.getByTestId('process-explorer-unavailable-child-parent').click();
  await expect(page.getByTestId('phase-series-overview')).toContainText('Child startup failed.');
});

test('opens a shared parent link and returns to its filtered search @mobile @cross-browser', async ({ page, workflowApi: _api }) => {
  const parent = waitingParent();
  await mockOverview(page, parent);
  await page.route('**/api/phased-processes?*', route => route.fulfill({ json: { items: [summary(parent)], continuationToken: null } }));
  await page.goto(parentPath + '?q=Airport&status=WaitingForTrigger');
  const phases = page.getByTestId('phase-series-phase');
  await expect(phases).toHaveCount(2);
  await expect(phases.last()).toContainText('Waiting for trigger');
  await expect(page.getByTestId('phase-series-open-child')).toBeVisible();
  await page.getByRole('link', { name: 'Phase Series search' }).click();
  await expect(page.getByTestId('phase-series-search')).toHaveValue('Airport');
  await expect(page.getByTestId('phase-series-result')).toContainText(parentId);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

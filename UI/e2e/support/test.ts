import { test as base, expect } from '@playwright/test';
import { MockWorkflowApi } from './mock-workflow-api';

interface WorkflowFixtures {
  readonly workflowApi: MockWorkflowApi;
}

export const test = base.extend<WorkflowFixtures>({
  workflowApi: async ({ page }, use, testInfo) => {
    const api = new MockWorkflowApi({
      failDashboard: testInfo.title.includes('[dashboard-error]'),
      failBlueprintSave: testInfo.title.includes('[blueprint-save-error]'),
      failStartProcess: testInfo.title.includes('[start-process-error]'),
    });
    await api.install(page);
    await use(api);
  },
});

export { expect };

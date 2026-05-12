import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { expect } from './test';

export async function expectNoCriticalA11yViolations(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page })
    .disableRules([
      // Angular Material controls can produce transient duplicate-id reports while overlays animate.
      'duplicate-id',
    ])
    .analyze();

  expect(results.violations.filter((violation) =>
    violation.impact === 'critical' || violation.impact === 'serious',
  )).toEqual([]);
}

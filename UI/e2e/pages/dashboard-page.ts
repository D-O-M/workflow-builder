import type { Page } from '@playwright/test';
import { expect } from '../support/test';

export class DashboardPage {
  constructor(private readonly page: Page) {}

  async goto(): Promise<void> {
    await this.page.goto('/dashboard?e2e=1');
    await expect(this.page.getByTestId('dashboard-page')).toBeVisible();
  }

  async expectLoaded(): Promise<void> {
    await expect(this.page.getByTestId('dashboard-kpi-strip')).toBeVisible();
    await expect(this.page.getByTestId('dashboard-kpi-succeeded')).toContainText('8');
    await expect(this.page.getByTestId('dashboard-recent-processes-card')).toContainText('Airport Disruption Advisor');
  }

  async selectWindow(label: string): Promise<void> {
    await this.page.getByTestId('dashboard-window-toggle').getByRole('radio', { name: label }).click();
  }

  async openBlueprintFilter(): Promise<void> {
    await this.page.getByTestId('dashboard-blueprint-filter').click();
  }
}

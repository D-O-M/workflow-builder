import type { Page } from '@playwright/test';
import { expect } from '../support/test';

export class AppShellPage {
  constructor(private readonly page: Page) {}

  async goto(path: string): Promise<void> {
    await this.page.goto(path.includes('?') ? `${path}&e2e=1` : `${path}?e2e=1`);
    await expect(this.page.getByTestId('app-shell')).toBeVisible();
  }

  async expectTitle(title: string): Promise<void> {
    await expect(this.page.getByTestId('app-shell-title')).toHaveText(title);
  }

  async navigate(testId: string, title: string): Promise<void> {
    await this.page.getByTestId(testId).click();
    await this.expectTitle(title);
  }

  async openMobileNavigation(): Promise<void> {
    await this.page.getByTestId('app-shell-nav-toggle').click();
    await expect(this.page.getByTestId('app-shell-nav')).toBeVisible();
  }
}

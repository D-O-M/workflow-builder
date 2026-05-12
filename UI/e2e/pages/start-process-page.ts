import type { Page } from '@playwright/test';
import { expect } from '../support/test';

export class StartProcessPage {
  constructor(private readonly page: Page) {}

  async goto(): Promise<void> {
    await this.page.goto('/processes/new?e2e=1');
    await expect(this.page.getByTestId('start-process-page')).toBeVisible();
  }

  async selectBlueprint(name: string): Promise<void> {
    await this.page.getByTestId('start-process-search-input').fill(name);
    await this.page.getByRole('option', { name: new RegExp(name) }).click();
    await expect(this.page.getByTestId('start-process-picker-trigger')).toContainText(name);
  }

  async fillInitialInput(): Promise<void> {
    await this.page.getByTestId('start-process-initial-input-city').fill('London');
    await this.page.getByTestId('start-process-initial-select-severity').click();
    await this.page.getByRole('option', { name: 'High' }).click();
  }

  async fillGlobalState(): Promise<void> {
    await this.page.getByTestId('start-process-global-input-operator').fill('Ops Desk');
    await this.page.getByTestId('start-process-global-select-region').click();
    await this.page.getByRole('option', { name: 'EMEA' }).click();
  }

  async launch(): Promise<void> {
    await this.fillInitialInput();
    await this.page.getByTestId('start-process-continue-global-state').click();
    await this.fillGlobalState();
    await this.page.getByTestId('start-process-continue-runtime-overrides').click();
    await this.page.getByTestId('start-process-submit').click();
  }
}

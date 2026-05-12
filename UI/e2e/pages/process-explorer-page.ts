import type { Page } from '@playwright/test';
import { expect } from '../support/test';
import { CanvasDriver } from './canvas-driver';

export class ProcessExplorerPage {
  readonly canvas = new CanvasDriver(this.page, 'process-explorer-canvas');

  constructor(private readonly page: Page) {}

  async goto(): Promise<void> {
    await this.page.goto('/processes?e2e=1');
    await expect(this.page.getByTestId('process-explorer-page')).toBeVisible();
  }

  async openHistory(): Promise<void> {
    await this.page.getByTestId('process-explorer-toggle-history').click();
    await expect(this.page.getByTestId('process-explorer-history-drawer')).toBeVisible();
  }

  async selectFirstProcess(): Promise<void> {
    await this.openHistory();
    await this.page.getByTestId('process-explorer-blueprint-row').first().click();
    await expect(this.page.getByTestId('process-explorer-process-list')).toBeVisible();
    await this.page.getByTestId('process-explorer-process-row').first().click();
    await expect(this.page.getByTestId('process-explorer-workbench')).toBeVisible();
  }

  async openAdvancedSearch(): Promise<void> {
    await this.page.getByTestId('process-explorer-open-advanced-search').click();
    await expect(this.page.getByTestId('process-search-dialog')).toBeVisible();
  }

  async openFirstStep(): Promise<void> {
    await this.canvas.clickNode('Weather Lookup');
    await expect(this.page.getByTestId('process-explorer-step-drawer')).toBeVisible();
  }
}

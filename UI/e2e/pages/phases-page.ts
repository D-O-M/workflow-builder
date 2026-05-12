import type { Page } from '@playwright/test';
import { expect } from '../support/test';
import { CanvasDriver } from './canvas-driver';

export class PhasesPage {
  readonly sourceCanvas = new CanvasDriver(this.page, 'phases-source-canvas');
  readonly destinationCanvas = new CanvasDriver(this.page, 'phases-destination-canvas');

  constructor(private readonly page: Page) {}

  async goto(): Promise<void> {
    await this.page.goto('/phases?e2e=1');
    await expect(this.page.getByTestId('phases-page')).toBeVisible();
  }

  async openExistingSeries(): Promise<void> {
    await this.page.getByTestId('phases-series-search').fill('Airport Readiness');
    await this.page.getByRole('option', { name: /Airport Readiness Series/ }).click();
    await expect(this.page.getByTestId('phases-source-pane')).toBeVisible();
    await expect(this.page.getByTestId('phases-destination-pane')).toBeVisible();
  }

  async createSeries(): Promise<void> {
    await this.page.getByTestId('phases-panel-new-series').or(this.page.getByTestId('phases-new-series')).first().click();
    await expect(this.page.getByTestId('phases-setup-panel')).toBeVisible();
  }

  async switchVertical(): Promise<void> {
    await this.page.getByTestId('phases-vertical-split').click();
    await expect(this.page.getByTestId('phases-canvas-stack')).toHaveClass(/is-vertical/);
  }
}

import type { Page } from '@playwright/test';
import { expect } from '../support/test';
import { CanvasDriver } from './canvas-driver';

export class BlueprintBuilderPage {
  readonly canvas = new CanvasDriver(this.page, 'blueprints-canvas');

  constructor(private readonly page: Page) {}

  async goto(): Promise<void> {
    await this.page.goto('/blueprints?e2e=1');
    await expect(this.page.getByTestId('blueprints-page')).toBeVisible();
  }

  async createDraft(name = 'E2E Blueprint Draft'): Promise<void> {
    await this.page.getByTestId('blueprints-create-new').click();
    await expect(this.page.getByTestId('blueprints-picker-shell')).toContainText('Draft Blueprint');
    await this.page.getByTestId('blueprints-edit-name').click();
    await expect(this.page.getByTestId('blueprints-draft-name-input')).toBeVisible();
    await this.page.getByTestId('blueprints-draft-name-input').fill(name);
    await this.page.keyboard.press('Enter');
  }

  async openPluginLibrary(): Promise<void> {
    if (await this.page.getByTestId('blueprints-plugin-library').isVisible()) {
      return;
    }

    await this.page.getByTestId('blueprints-toggle-plugin-library').click();
    await expect(this.page.getByTestId('blueprints-plugin-library')).toBeVisible();
  }

  async addPlugin(name: string): Promise<void> {
    await this.openPluginLibrary();
    await this.page.getByTestId('blueprints-plugin-search').fill(name);
    await this.page.getByTestId('blueprints-plugin-card').filter({ hasText: name }).first().click();
    await this.canvas.expectNode(name);
  }

  async closePluginLibrary(): Promise<void> {
    if (!await this.page.getByTestId('blueprints-plugin-library').isVisible()) {
      return;
    }

    await this.page.getByTestId('blueprints-close-plugin-library').click();
    await expect(this.page.getByTestId('blueprints-plugin-library')).toBeHidden();
  }

  async save(): Promise<void> {
    await this.page.getByTestId('blueprints-save').click();
  }

  async openValidationMenu(): Promise<void> {
    await this.page.getByTestId('blueprints-validation-badge').click();
    await expect(this.page.getByTestId('blueprints-validation-menu')).toBeVisible();
  }
}

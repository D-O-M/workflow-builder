import type { Locator, Page } from '@playwright/test';
import { expect } from '../support/test';

interface CanvasE2eSnapshot {
  readonly id: string;
  readonly mode: string;
  readonly zoomPercent: number;
  readonly canvasRect: { readonly left: number; readonly top: number; readonly width: number; readonly height: number };
  readonly nodes: readonly {
    readonly id: number;
    readonly name: string;
    readonly kind: string;
    readonly clientBounds: { readonly left: number; readonly top: number; readonly right: number; readonly bottom: number } | null;
    readonly ports: readonly {
      readonly kind: string;
      readonly channelName: string | null;
      readonly propertyName: string | null;
      readonly clientX: number;
      readonly clientY: number;
    }[];
    readonly sections: readonly {
      readonly key: string;
      readonly name: string;
      readonly label: string;
      readonly side: 'left' | 'right';
      readonly expanded: boolean;
      readonly headerBounds: { readonly left: number; readonly top: number; readonly right: number; readonly bottom: number } | null;
      readonly toggleBounds: { readonly left: number; readonly top: number; readonly right: number; readonly bottom: number } | null;
    }[];
  }[];
}

export class CanvasDriver {
  constructor(
    private readonly page: Page,
    readonly testId: string,
  ) {}

  locator(): Locator {
    return this.page.getByTestId(this.testId);
  }

  async snapshot(): Promise<CanvasE2eSnapshot> {
    await expect(this.locator()).toBeVisible();
    const snapshot = await this.page.evaluate((canvasId) => {
      const api = (window as typeof window & {
        __workflowBuilderE2e?: {
          getCanvasSnapshot(id: string): CanvasE2eSnapshot | null;
        };
      }).__workflowBuilderE2e;
      return api?.getCanvasSnapshot(canvasId) ?? null;
    }, this.testId);

    expect(snapshot, `Expected e2e canvas snapshot for ${this.testId}`).not.toBeNull();
    return snapshot!;
  }

  async expectNode(name: string): Promise<void> {
    const snapshot = await this.snapshot();
    expect(snapshot.nodes.some((node) => node.name === name)).toBe(true);
  }

  async clickNode(name: string): Promise<void> {
    const snapshot = await this.snapshot();
    const node = this.findNode(snapshot, name);
    expect(node.clientBounds).not.toBeNull();
    const width = node.clientBounds!.right - node.clientBounds!.left;
    const height = node.clientBounds!.bottom - node.clientBounds!.top;
    const clientX = node.clientBounds!.left + width / 2;
    const clientY = node.clientBounds!.top + Math.min(24, height / 3);
    await this.locator().click({
      position: {
        x: clientX - snapshot.canvasRect.left,
        y: clientY - snapshot.canvasRect.top,
      },
    });
  }

  async clickFirstNode(): Promise<void> {
    const snapshot = await this.snapshot();
    const node = snapshot.nodes
      .filter((candidate) =>
        candidate.clientBounds &&
        candidate.clientBounds.right > snapshot.canvasRect.left &&
        candidate.clientBounds.left < snapshot.canvasRect.left + snapshot.canvasRect.width &&
        candidate.clientBounds.bottom > snapshot.canvasRect.top &&
        candidate.clientBounds.top < snapshot.canvasRect.top + snapshot.canvasRect.height)
      .sort((left, right) => left.clientBounds!.left - right.clientBounds!.left || left.clientBounds!.top - right.clientBounds!.top)[0];
    expect(node, 'Expected at least one canvas node').toBeTruthy();
    await this.clickNode(node.name);
  }

  async dragNode(name: string, deltaX: number, deltaY: number): Promise<void> {
    const node = await this.node(name);
    expect(node.clientBounds).not.toBeNull();
    const width = node.clientBounds!.right - node.clientBounds!.left;
    const height = node.clientBounds!.bottom - node.clientBounds!.top;
    const x = node.clientBounds!.left + width / 2;
    const y = node.clientBounds!.top + Math.min(28, height / 3);
    await this.page.mouse.move(x, y);
    await this.page.mouse.down();
    await this.page.mouse.move(x + deltaX, y + deltaY, { steps: 8 });
    await this.page.mouse.up();
  }

  async connectOutputToInput(
    sourceNodeName: string,
    sourcePropertyName: string,
    destinationNodeName: string,
    destinationPropertyName: string,
  ): Promise<void> {
    await this.expandSection(sourceNodeName, 'right');
    await this.expandSection(destinationNodeName, 'left');
    const sourcePort = await this.port(sourceNodeName, 'output-property', sourcePropertyName);
    const destinationPort = await this.port(destinationNodeName, 'input-property', destinationPropertyName);
    await this.page.mouse.move(sourcePort.clientX, sourcePort.clientY);
    await this.page.mouse.down();
    await this.page.mouse.move(destinationPort.clientX, destinationPort.clientY, { steps: 12 });
    await this.page.mouse.up();
  }

  async connectSummaryPorts(sourceNodeName: string, destinationNodeName: string): Promise<void> {
    const sourcePort = await this.summaryPort(sourceNodeName, 'output-summary');
    const destinationPort = await this.summaryPort(destinationNodeName, 'input-summary');
    await this.page.mouse.move(sourcePort.clientX, sourcePort.clientY);
    await this.page.mouse.down();
    await this.page.mouse.move(destinationPort.clientX, destinationPort.clientY, { steps: 12 });
    await this.page.mouse.up();
  }

  async port(nodeName: string, kind: string, propertyName: string): Promise<CanvasE2eSnapshot['nodes'][number]['ports'][number]> {
    const node = await this.node(nodeName);
    const port = node.ports.find((candidate) =>
      candidate.kind === kind &&
      candidate.propertyName === propertyName);
    expect(port, `Expected ${kind} ${propertyName} on ${nodeName}`).toBeTruthy();
    return port!;
  }

  async expandSection(nodeName: string, side: 'left' | 'right', sectionName?: string): Promise<void> {
    const section = await this.section(nodeName, side, sectionName);
    if (section.expanded) {
      return;
    }

    const bounds = section.headerBounds ?? section.toggleBounds;
    expect(bounds, `Expected section bounds for ${side} section on ${nodeName}`).not.toBeNull();
    await this.page.mouse.click(
      (bounds!.left + bounds!.right) / 2,
      (bounds!.top + bounds!.bottom) / 2,
    );

    await expect.poll(async () => {
      const current = await this.section(nodeName, side, sectionName);
      return current.expanded;
    }, { message: `Expected ${side} section on ${nodeName} to expand` }).toBe(true);
  }

  private async node(name: string): Promise<CanvasE2eSnapshot['nodes'][number]> {
    const snapshot = await this.snapshot();
    return this.findNode(snapshot, name);
  }

  private findNode(snapshot: CanvasE2eSnapshot, name: string): CanvasE2eSnapshot['nodes'][number] {
    const node = snapshot.nodes.find((candidate) => candidate.name === name);
    expect(node, `Expected node ${name}`).toBeTruthy();
    return node!;
  }

  private async section(
    nodeName: string,
    side: 'left' | 'right',
    sectionName?: string,
  ): Promise<CanvasE2eSnapshot['nodes'][number]['sections'][number]> {
    const node = await this.node(nodeName);
    const section = node.sections.find((candidate) =>
      candidate.side === side &&
      (!sectionName || candidate.name === sectionName || candidate.label === sectionName));
    expect(section, `Expected ${side} section ${sectionName ?? ''} on ${nodeName}`).toBeTruthy();
    return section!;
  }

  private async summaryPort(
    nodeName: string,
    kind: 'input-summary' | 'output-summary',
  ): Promise<CanvasE2eSnapshot['nodes'][number]['ports'][number]> {
    const node = await this.node(nodeName);
    const port = node.ports.find((candidate) => candidate.kind === kind);
    expect(port, `Expected ${kind} on ${nodeName}`).toBeTruthy();
    return port!;
  }
}

import { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';
import {
  NodeAnchor,
  NodeChannelSectionLayout,
  NodeLayout,
  NodeModel,
  NodePortDefinition,
  NodeRenderStatus,
  NodeVisualKind,
  Side,
  TaskPropertyType,
} from './node-model';
import { CanvasBounds } from './canvas-render-types';
import { pointInsideExpandedRect, portBounds, unionBounds } from './canvas-render-geometry-utilities';

export class RenderedNode {
  readonly id: number;
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
  readonly layout: NodeLayout;
  readonly inputChannelName: string | null;
  readonly outputChannelNames: readonly string[];
  readonly hasUpgradeWarning: boolean;
  readonly renderStatus: NodeRenderStatus | null;
  readonly failureMessage: string | null;
  readonly inputProperties: Readonly<Record<string, TaskPropertyType>>;
  readonly outputChannelProperties: Readonly<Record<string, Readonly<Record<string, TaskPropertyType>>>>;
  readonly designTimeInputOverrides: Readonly<Record<string, unknown>>;
  readonly inputPropertyDefinitions: readonly ITaskPropertyDefinition[];
  readonly inputChannelDisplayName: string | null;
  readonly outputChannelDisplayNames: readonly string[];
  readonly outputChannelPropertyDefinitions: Readonly<Record<string, readonly ITaskPropertyDefinition[]>>;
  readonly resolvedVersion: string | null;
  readonly name: string;
  readonly taskDefinitionId: number;
  readonly allowedMajor: number;
  readonly allowedMinor: number;
  readonly resolvedVersionId: number;
  private readonly visiblePortsList: readonly NodePortDefinition[];
  private readonly portByKey: ReadonlyMap<string, NodePortDefinition>;
  private readonly anchorDefinitionsList: readonly NodeAnchor[];
  private readonly interactionBoundsValue: CanvasBounds;
  readonly anySectionExpanded: boolean;
  readonly allSectionsExpanded: boolean;

  constructor(
    public readonly base: NodeModel,
    public readonly expandedSectionKeys: ReadonlySet<string>,
  ) {
    this.id = base.id;
    this.x = base.x;
    this.y = base.y;
    this.width = base.width;
    this.layout = base.layoutForExpandedSections(expandedSectionKeys);
    this.height = this.layout.totalHeight;
    this.inputChannelName = base.inputChannelName;
    this.outputChannelNames = base.outputChannelNames;
    this.hasUpgradeWarning = base.hasUpgradeWarning;
    this.renderStatus = base.renderStatus;
    this.failureMessage = base.failureMessage;
    this.inputProperties = base.inputProperties;
    this.outputChannelProperties = base.outputChannelProperties;
    this.designTimeInputOverrides = base.designTimeInputOverrides;
    this.inputPropertyDefinitions = base.inputPropertyDefinitions;
    this.inputChannelDisplayName = base.inputChannelDisplayName;
    this.outputChannelDisplayNames = base.outputChannelDisplayNames;
    this.outputChannelPropertyDefinitions = base.outputChannelPropertyDefinitions;
    this.resolvedVersion = base.resolvedVersion;
    this.name = base.name;
    this.taskDefinitionId = base.taskDefinitionId;
    this.allowedMajor = base.allowedMajor;
    this.allowedMinor = base.allowedMinor;
    this.resolvedVersionId = base.resolvedVersionId;
    this.visiblePortsList = [...this.layout.summaryPorts, ...this.layout.propertyPorts];
    this.portByKey = new Map(this.visiblePortsList.map((port) => [port.key, port] as const));
    this.anchorDefinitionsList = this.buildAnchorDefinitions();
    this.interactionBoundsValue = this.computeInteractionBounds();
    const sectionCount = (this.layout.inputSection ? 1 : 0) + this.layout.outputSections.length;
    this.anySectionExpanded = this.sections().some((section) => section.expanded);
    this.allSectionsExpanded = sectionCount > 0 && this.sections().every((section) => section.expanded);
  }

  get expanded(): boolean {
    return this.anySectionExpanded;
  }

  get visualKind(): NodeVisualKind {
    return this.base.visualKind;
  }

  get isCollectionIteratorOperator(): boolean {
    return this.base.isCollectionIteratorOperator;
  }

  get isDataBridge(): boolean {
    return this.base.isDataBridge;
  }

  contains(x: number, y: number): boolean {
    return x >= this.x && x <= this.x + this.width && y >= this.y && y <= this.y + this.height;
  }

  containsBuffered(x: number, y: number, buffer: number): boolean {
    return (
      x >= this.x - buffer &&
      x <= this.x + this.width + buffer &&
      y >= this.y - buffer &&
      y <= this.y + this.height + buffer
    );
  }

  containsInteractionPoint(x: number, y: number): boolean {
    return this.contains(x, y) || this.portHit(x, y, 0) !== null;
  }

  interactionBounds(): CanvasBounds {
    return this.interactionBoundsValue;
  }

  isFullyWithin(left: number, top: number, right: number, bottom: number): boolean {
    return (
      this.x >= left &&
      this.y >= top &&
      this.x + this.width <= right &&
      this.y + this.height <= bottom
    );
  }

  toggleRect(): { x: number; y: number; width: number; height: number } {
    return this.layout.toggleRect;
  }

  settingsRect(): { x: number; y: number; width: number; height: number } {
    return this.layout.toggleRect;
  }

  visiblePorts(): readonly NodePortDefinition[] {
    return this.visiblePortsList;
  }

  portDefinition(portKey: string): NodePortDefinition | null {
    return this.portByKey.get(portKey) ?? null;
  }

  portHit(x: number, y: number, extraHitRadius = 5): NodePortDefinition | null {
    for (let index = this.visiblePortsList.length - 1; index >= 0; index -= 1) {
      const port = this.visiblePortsList[index]!;
      if (pointInsideExpandedRect(x, y, port.hitBounds, extraHitRadius)) {
        return port;
      }

      if (Math.hypot(x - port.x, y - port.y) <= port.radius + extraHitRadius) {
        return port;
      }
    }

    return null;
  }

  sectionToggleHit(x: number, y: number): string | null {
    if (this.isCollectionIteratorOperator) {
      return null;
    }

    const section = this.sections().find((candidate) =>
      x >= candidate.toggleRect.x &&
      x <= candidate.toggleRect.x + candidate.toggleRect.width &&
      y >= candidate.toggleRect.y &&
      y <= candidate.toggleRect.y + candidate.toggleRect.height,
    );
    return section?.key ?? null;
  }

  sectionHeaderHit(x: number, y: number): string | null {
    if (this.isCollectionIteratorOperator) {
      return null;
    }

    const section = this.sections().find((candidate) =>
      x >= this.x + 14 &&
      x <= this.x + this.width - 14 &&
      y >= candidate.headerTop &&
      y <= candidate.headerTop + candidate.headerHeight,
    );
    return section?.key ?? null;
  }

  allSectionKeys(): string[] {
    if (this.isCollectionIteratorOperator) {
      return [];
    }

    return this.sections().map((section) => section.key);
  }

  sectionForPort(port: NodePortDefinition): NodeChannelSectionLayout | null {
    return this.sections().find((section) => section.key === `${port.side}:${port.channelName}`) ?? null;
  }

  inputPropertyPort(propertyName: string): NodePortDefinition | null {
    return this.layout.inputSection?.propertyRows.find((row) => row.name === propertyName)?.port ?? null;
  }

  outputPropertyPort(anchorIndex: number, propertyName: string): NodePortDefinition | null {
    const section = this.outputSectionForAnchor(anchorIndex);
    return section?.propertyRows.find((row) => row.name === propertyName)?.port ?? null;
  }

  outputSummaryPort(anchorIndex: number): NodePortDefinition | null {
    return this.outputSectionForAnchor(anchorIndex)?.summaryPort ?? null;
  }

  inputSummaryPort(): NodePortDefinition | null {
    return this.layout.inputSection?.summaryPort ?? null;
  }

  inputAnchorIndex(): number | null {
    return this.anchorDefinitionsList.find((anchor) => anchor.kind === 'input')?.index ?? null;
  }

  outputAnchors(): readonly NodeAnchor[] {
    return this.anchorDefinitionsList.filter((anchor) => anchor.kind === 'output');
  }

  anchorDefinitions(): readonly NodeAnchor[] {
    return this.anchorDefinitionsList;
  }

  anchorDefinition(idx: number): NodeAnchor | null {
    return this.anchorDefinitionsList.find((anchor) => anchor.index === idx) ?? null;
  }

  anchorSide(idx: number): Side {
    return this.anchorDefinition(idx)?.side ?? 'right';
  }

  anchorRadius(idx: number): number {
    return this.anchorDefinition(idx)?.kind === 'input' ? 8 : 7;
  }

  outputChannelNameForAnchor(idx: number): string | null {
    const outputIndex = idx - (this.inputChannelName ? 1 : 0);
    return outputIndex >= 0 ? this.outputChannelNames[outputIndex] ?? null : null;
  }

  outputPropertiesForAnchor(idx: number): Record<string, TaskPropertyType> {
    const channelName = this.outputChannelNameForAnchor(idx);
    return channelName ? this.base.outputPropertiesForAnchor(idx) : {};
  }

  outputPropertyDefinitionsForAnchor(idx: number): ITaskPropertyDefinition[] {
    return this.base.outputPropertyDefinitionsForAnchor(idx);
  }

  inputAnchorSnap(): { x: number; y: number; side: Side; pos: number } | null {
    const anchor = this.inputSummaryPort();
    if (!anchor) {
      return null;
    }

    return {
      x: anchor.x,
      y: anchor.y,
      side: 'left',
      pos: anchor.y - this.y,
    };
  }

  sideFromPoint(x: number, y: number): Side {
    const cx = this.x + this.width / 2;
    const cy = this.y + this.height / 2;
    const dx = x - cx;
    const dy = y - cy;
    if (Math.abs(dx) * this.height > Math.abs(dy) * this.width) {
      return dx < 0 ? 'left' : 'right';
    }

    return dy < 0 ? 'top' : 'bottom';
  }

  snapPointFixedSide(x: number, y: number, side: Side): { x: number; y: number; side: Side; pos: number } {
    switch (side) {
      case 'left': {
        const yy = NodeModel.clamp(y, this.y, this.y + this.height);
        return { x: this.x, y: yy, side: 'left', pos: yy - this.y };
      }
      case 'right': {
        const yy = NodeModel.clamp(y, this.y, this.y + this.height);
        return { x: this.x + this.width, y: yy, side: 'right', pos: yy - this.y };
      }
      case 'top': {
        const xx = NodeModel.clamp(x, this.x, this.x + this.width);
        return { x: xx, y: this.y, side: 'top', pos: xx - this.x };
      }
      default: {
        const xx = NodeModel.clamp(x, this.x, this.x + this.width);
        return { x: xx, y: this.y + this.height, side: 'bottom', pos: xx - this.x };
      }
    }
  }

  get designTimeOverrideCount(): number {
    return this.base.designTimeOverrideCount;
  }

  get hasDesignTimeOverrides(): boolean {
    return this.base.hasDesignTimeOverrides;
  }

  get inputChannelLabel(): string | null {
    return this.base.inputChannelLabel;
  }

  designTimeOverrideKeys(): string[] {
    return this.base.designTimeOverrideKeys();
  }

  private outputSectionForAnchor(idx: number): NodeChannelSectionLayout | null {
    const outputIndex = idx - (this.inputChannelName ? 1 : 0);
    return outputIndex >= 0 ? this.layout.outputSections[outputIndex] ?? null : null;
  }

  private sections(): NodeChannelSectionLayout[] {
    return [
      ...(this.layout.inputSection ? [this.layout.inputSection] : []),
      ...this.layout.outputSections,
    ];
  }

  private buildAnchorDefinitions(): NodeAnchor[] {
    const anchors: NodeAnchor[] = [];
    let index = 0;

    if (this.layout.inputSection) {
      anchors.push({
        index,
        kind: 'input',
        label: this.layout.inputSection.label,
        side: 'left',
        x: this.layout.inputSection.summaryPort.x,
        y: this.layout.inputSection.summaryPort.y,
      });
      index += 1;
    }

    for (const section of this.layout.outputSections) {
      anchors.push({
        index,
        kind: 'output',
        label: section.label,
        side: 'right',
        x: section.summaryPort.x,
        y: section.summaryPort.y,
      });
      index += 1;
    }

    return anchors;
  }

  private computeInteractionBounds(): CanvasBounds {
    let bounds: CanvasBounds = {
      left: this.x,
      top: this.y,
      right: this.x + this.width,
      bottom: this.y + this.height,
    };

    for (const port of this.visiblePortsList) {
      bounds = unionBounds(bounds, portBounds(port));
    }

    return bounds;
  }
}

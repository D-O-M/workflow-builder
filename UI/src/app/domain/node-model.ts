import { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';
import { CanvasNodeLayoutCache } from './canvas-node-layout-cache';
import {
    CanvasNodeLayoutFactory,
    CanvasNodeLayoutRequest,
    CANVAS_NODE_LAYOUT_TOKENS,
} from './canvas-node-layout-factory';
import {
    anchorDefinitionsFromLayout,
    clampNumber,
    portHit,
    rectContainsBufferedPoint,
    rectContainsPoint,
    rectIsFullyWithin,
    sideFromNodePoint,
    snapPointToFixedSide,
    snapPointToNearestSide,
    visiblePortsFromLayout,
} from './canvas-node-geometry-utility';

export interface Point {
    x: number;
    y: number;
}

export interface Rect {
    x: number;
    y: number;
    width: number;
    height: number;
}

export type Side = 'left' | 'right' | 'top' | 'bottom';
export type NodeRenderStatus = 'NotExecuted' | 'Pending' | 'InProgress' | 'Completed' | 'Failed';
export type NodeKind = 'PluginTask' | 'CollectionIterator' | 'DataBridge';
export type NodeVisualKind = 'TaskCard' | 'CollectionIteratorOperator' | 'DataBridge';
export type DataBridgeKind = 'input' | 'output';
export type TaskPropertyBaseType = 'String' | 'Integer' | 'Decimal' | 'Boolean' | 'Guid' | 'Date' | 'DateTime' | 'Enum';
export type TaskPropertyType = TaskPropertyBaseType | `${TaskPropertyBaseType}[]`;

export interface NodeAnchor {
    index: number;
    kind: 'input' | 'output';
    label: string;
    side: Side;
    x: number;
    y: number;
}

export interface NodePortDefinition {
    key: string;
    kind: 'input-summary' | 'input-property' | 'output-summary' | 'output-property';
    anchorIndex: number | null;
    channelName: string | null;
    channelLabel: string | null;
    propertyName: string | null;
    label: string;
    side: Side;
    x: number;
    y: number;
    radius: number;
    visualBounds?: Rect;
    hitBounds?: Rect;
}

export interface NodePropertyRow {
    key: string;
    name: string;
    label: string;
    kind: TaskPropertyType;
    displayKind: string;
    required: boolean;
    top: number;
    height: number;
    centerY: number;
    port: NodePortDefinition;
}

export interface NodeChannelSectionLayout {
    key: string;
    name: string;
    label: string;
    countLabel: string;
    side: 'left' | 'right';
    expanded: boolean;
    top: number;
    headerTop: number;
    headerHeight: number;
    toggleRect: { x: number; y: number; width: number; height: number };
    bottom: number;
    summaryPort: NodePortDefinition;
    propertyRows: NodePropertyRow[];
}

export interface NodeLayout {
    totalHeight: number;
    titleBaseline: number;
    versionBaseline: number;
    titleMaxWidth: number;
    toggleRect: { x: number; y: number; width: number; height: number };
    inputSection: NodeChannelSectionLayout | null;
    outputSections: NodeChannelSectionLayout[];
    summaryPorts: NodePortDefinition[];
    propertyPorts: NodePortDefinition[];
}

export class NodeModel {
    static buffer = 20;
    static readonly collectionIteratorOperatorWidth = CANVAS_NODE_LAYOUT_TOKENS.collectionIteratorOperatorWidth;
    static readonly collectionIteratorOperatorHeight = CANVAS_NODE_LAYOUT_TOKENS.collectionIteratorOperatorHeight;

    private readonly layoutCache = new CanvasNodeLayoutCache();
    private readonly layoutFactory = new CanvasNodeLayoutFactory();

    static clamp(v: number, min: number, max: number): number {
        return clampNumber(v, min, max);
    }

    constructor(
        public id: number,
        public taskDefinitionId: number,
        public allowedMajor: number,
        public allowedMinor: number,
        public resolvedVersionId: number,
        public resolvedVersion: string | null,
        public name: string,
        public x: number,
        public y: number,
        public width: number,
        public height: number,
        public inputChannelName: string | null = null,
        public outputChannelNames: string[] = [],
        public hasUpgradeWarning: boolean = false,
        public renderStatus: NodeRenderStatus | null = null,
        public failureMessage: string | null = null,
        public inputProperties: Record<string, TaskPropertyType> = {},
        public outputChannelProperties: Record<string, Record<string, TaskPropertyType>> = {},
        public designTimeInputOverrides: Record<string, unknown> = {},
        public inputPropertyDefinitions: ITaskPropertyDefinition[] = [],
        public inputChannelDisplayName: string | null = null,
        public outputChannelDisplayNames: string[] = [],
        public outputChannelPropertyDefinitions: Record<string, ITaskPropertyDefinition[]> = {},
        public expanded: boolean = false,
        public nodeKind: NodeKind = 'PluginTask',
        public collectionIteratorItemKind: TaskPropertyBaseType | null = null,
        public instanceId: string | null = null,
    ) {}

    withExpanded(expanded: boolean): NodeModel {
        return new NodeModel(
            this.id,
            this.taskDefinitionId,
            this.allowedMajor,
            this.allowedMinor,
            this.resolvedVersionId,
            this.resolvedVersion,
            this.name,
            this.x,
            this.y,
            this.width,
            this.heightForState(expanded),
            this.inputChannelName,
            [...this.outputChannelNames],
            this.hasUpgradeWarning,
            this.renderStatus,
            this.failureMessage,
            { ...this.inputProperties },
            Object.fromEntries(
                Object.entries(this.outputChannelProperties).map(([channelName, properties]) => [channelName, { ...properties }]),
            ),
            { ...this.designTimeInputOverrides },
            this.clonePropertyDefinitions(this.inputPropertyDefinitions),
            this.inputChannelDisplayName,
            [...this.outputChannelDisplayNames],
            Object.fromEntries(
                Object.entries(this.outputChannelPropertyDefinitions).map(([channelName, properties]) => [
                    channelName,
                    this.clonePropertyDefinitions(properties),
                ]),
            ),
            expanded,
            this.nodeKind,
            this.collectionIteratorItemKind,
            this.instanceId,
        );
    }

    withInstanceId(instanceId: string | null): NodeModel {
        return new NodeModel(
            this.id,
            this.taskDefinitionId,
            this.allowedMajor,
            this.allowedMinor,
            this.resolvedVersionId,
            this.resolvedVersion,
            this.name,
            this.x,
            this.y,
            this.width,
            this.height,
            this.inputChannelName,
            [...this.outputChannelNames],
            this.hasUpgradeWarning,
            this.renderStatus,
            this.failureMessage,
            { ...this.inputProperties },
            Object.fromEntries(
                Object.entries(this.outputChannelProperties).map(([channelName, properties]) => [channelName, { ...properties }]),
            ),
            { ...this.designTimeInputOverrides },
            this.clonePropertyDefinitions(this.inputPropertyDefinitions),
            this.inputChannelDisplayName,
            [...this.outputChannelDisplayNames],
            Object.fromEntries(
                Object.entries(this.outputChannelPropertyDefinitions).map(([channelName, properties]) => [
                    channelName,
                    this.clonePropertyDefinitions(properties),
                ]),
            ),
            this.expanded,
            this.nodeKind,
            this.collectionIteratorItemKind,
            instanceId,
        );
    }

    contains(x: number, y: number): boolean {
        return rectContainsPoint(this.nodeRect(), x, y);
    }

    containsBuffered(x: number, y: number, buffer: number): boolean {
        return rectContainsBufferedPoint(this.nodeRect(), x, y, buffer);
    }

    isFullyWithin(left: number, top: number, right: number, bottom: number): boolean {
        return rectIsFullyWithin(this.nodeRect(), left, top, right, bottom);
    }

    draw(ctx: CanvasRenderingContext2D, drawText: (
      ctx: CanvasRenderingContext2D,
      text: string,
      x: number,
      y: number,
      maxWidth: number) => void): void {
        ctx.strokeRect(this.x, this.y, this.width, this.height);
        drawText(ctx, this.name, this.x + 10, this.y + 20, this.width - 20);
    }

    heightForState(expanded: boolean): number {
        return this.layoutForState(expanded).totalHeight;
    }

    layout(): NodeLayout {
        return this.layoutForState(this.expanded);
    }

    layoutForState(expanded: boolean): NodeLayout {
        return this.layoutCache.layoutForState(this.layoutRequest(), expanded);
    }

    layoutForExpandedSections(expandedSectionKeys: ReadonlySet<string>): NodeLayout {
        return this.layoutCache.layoutForExpandedSections(this.layoutRequest(), expandedSectionKeys);
    }

    toggleRect(): { x: number; y: number; width: number; height: number } {
        return this.layout().toggleRect;
    }

    settingsRect(): { x: number; y: number; width: number; height: number } {
        return this.layout().toggleRect;
    }

    anchorPositions(): Point[] {
        return this.anchorDefinitions().map(({ x, y }) => ({ x, y }));
    }

    anchorDefinitions(): NodeAnchor[] {
        return anchorDefinitionsFromLayout(this.layout());
    }

    visiblePorts(): NodePortDefinition[] {
        return visiblePortsFromLayout(this.layout());
    }

    portDefinition(portKey: string): NodePortDefinition | null {
        return this.visiblePorts().find((port) => port.key === portKey) ?? null;
    }

    portHit(x: number, y: number, extraHitRadius = 5): NodePortDefinition | null {
        return portHit(this.visiblePorts(), x, y, extraHitRadius);
    }

    inputPropertyPort(propertyName: string): NodePortDefinition | null {
        return this.layout().inputSection?.propertyRows.find((row) => row.name === propertyName)?.port ?? null;
    }

    outputPropertyPort(anchorIndex: number, propertyName: string): NodePortDefinition | null {
        const section = this.outputSectionForAnchor(anchorIndex);
        return section?.propertyRows.find((row) => row.name === propertyName)?.port ?? null;
    }

    outputSummaryPort(anchorIndex: number): NodePortDefinition | null {
        return this.outputSectionForAnchor(anchorIndex)?.summaryPort ?? null;
    }

    inputSummaryPort(): NodePortDefinition | null {
        return this.layout().inputSection?.summaryPort ?? null;
    }

    inputAnchorIndex(): number | null {
        const inputAnchor = this.anchorDefinitions().find((anchor) => anchor.kind === 'input');
        return inputAnchor?.index ?? null;
    }

    outputAnchors(): NodeAnchor[] {
        return this.anchorDefinitions().filter((anchor) => anchor.kind === 'output');
    }

    anchorDefinition(idx: number): NodeAnchor | null {
        return this.anchorDefinitions().find((anchor) => anchor.index === idx) ?? null;
    }

    anchorSide(idx: number): Side {
        return this.anchorDefinition(idx)?.side ?? 'right';
    }

    anchorRadius(idx: number): number {
        return this.anchorDefinition(idx)?.kind === 'input'
            ? CANVAS_NODE_LAYOUT_TOKENS.summaryPortRadius + 1
            : CANVAS_NODE_LAYOUT_TOKENS.summaryPortRadius;
    }

    isInputAnchor(idx: number): boolean {
        return this.anchorDefinition(idx)?.kind === 'input';
    }

    isOutputAnchor(idx: number): boolean {
        return this.anchorDefinition(idx)?.kind === 'output';
    }

    outputChannelNameForAnchor(idx: number): string | null {
        const outputIndex = idx - (this.inputChannelName ? 1 : 0);
        return outputIndex >= 0 ? this.outputChannelNames[outputIndex] ?? null : null;
    }

    outputChannelLabelForAnchor(idx: number): string | null {
        const outputIndex = idx - (this.inputChannelName ? 1 : 0);
        const channelName = this.outputChannelNames[outputIndex] ?? null;
        if (!channelName) {
            return null;
        }

        return this.outputChannelLabel(channelName, outputIndex);
    }

    outputPropertiesForAnchor(idx: number): Record<string, TaskPropertyType> {
        const channelName = this.outputChannelNameForAnchor(idx);
        if (!channelName) {
            return {};
        }

        return this.outputChannelProperties[channelName] ?? {};
    }

    outputPropertyDefinitionsForAnchor(idx: number): ITaskPropertyDefinition[] {
        const channelName = this.outputChannelNameForAnchor(idx);
        if (!channelName) {
            return [];
        }

        return this.outputPropertyDefinitions(channelName);
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

    inAnchorCircle(x: number, y: number, extraHitRadius = 5): number | null {
        const anchors = this.anchorDefinitions();
        for (const anchor of anchors) {
            const hitRadius = this.anchorRadius(anchor.index) + extraHitRadius;
            if (Math.hypot(x - anchor.x, y - anchor.y) <= hitRadius) {
                return anchor.index;
            }
        }
        return null;
    }

    sideFromPoint(x: number, y: number): Side {
        return sideFromNodePoint(this.nodeRect(), x, y);
    }

    snapPointToSide(x: number, y: number): { x: number; y: number; side: Side; pos: number } {
        return snapPointToNearestSide(this.nodeRect(), x, y);
    }

    snapPointFixedSide(x: number, y: number, side: Side): { x: number; y: number; side: Side; pos: number } {
        return snapPointToFixedSide(this.nodeRect(), x, y, side);
    }

    get designTimeOverrideCount(): number {
        return Object.keys(this.designTimeInputOverrides).length;
    }

    get hasDesignTimeOverrides(): boolean {
        return this.designTimeOverrideCount > 0;
    }

    get inputChannelLabel(): string | null {
        return this.layoutFactory.inputChannelLabel(this.layoutRequest());
    }

    get visualKind(): NodeVisualKind {
        if (this.nodeKind === 'CollectionIterator') {
            return 'CollectionIteratorOperator';
        }

        if (this.nodeKind === 'DataBridge') {
            return 'DataBridge';
        }

        return 'TaskCard';
    }

    get isCollectionIteratorOperator(): boolean {
        return this.visualKind === 'CollectionIteratorOperator';
    }

    get isDataBridge(): boolean {
        return this.visualKind === 'DataBridge';
    }

    get dataBridgeKind(): DataBridgeKind | null {
        if (!this.isDataBridge || !this.instanceId) {
            return null;
        }

        if (this.instanceId.startsWith('data-bridge:input:')) {
            return 'input';
        }

        if (this.instanceId.startsWith('data-bridge:output:')) {
            return 'output';
        }

        return null;
    }

    get collectionIteratorResolved(): boolean {
        return this.nodeKind === 'CollectionIterator' && this.collectionIteratorItemKind !== null;
    }

    get collectionIteratorInputDisplayKind(): string {
        return this.nodeKind === 'CollectionIterator' && this.collectionIteratorItemKind
            ? `${this.collectionIteratorItemKind}[]`
            : 'T[]';
    }

    get collectionIteratorItemDisplayKind(): string {
        return this.nodeKind === 'CollectionIterator' && this.collectionIteratorItemKind
            ? this.collectionIteratorItemKind
            : 'T';
    }

    get collectionIteratorEquation(): string {
        return `${this.collectionIteratorInputDisplayKind} -> ${this.collectionIteratorItemDisplayKind}`;
    }

    designTimeOverrideKeys(): string[] {
        return Object.keys(this.designTimeInputOverrides).sort((left, right) => left.localeCompare(right));
    }

    private nodeRect(): { x: number; y: number; width: number; height: number } {
        return {
            x: this.x,
            y: this.y,
            width: this.width,
            height: this.height,
        };
    }

    private layoutRequest(): CanvasNodeLayoutRequest {
        return {
            x: this.x,
            y: this.y,
            width: this.width,
            inputChannelName: this.inputChannelName,
            outputChannelNames: this.outputChannelNames,
            inputProperties: this.inputProperties,
            outputChannelProperties: this.outputChannelProperties,
            inputPropertyDefinitions: this.inputPropertyDefinitions,
            inputChannelDisplayName: this.inputChannelDisplayName,
            outputChannelDisplayNames: this.outputChannelDisplayNames,
            outputChannelPropertyDefinitions: this.outputChannelPropertyDefinitions,
            nodeKind: this.nodeKind,
            collectionIteratorItemKind: this.collectionIteratorItemKind,
        };
    }

    private outputSectionForAnchor(idx: number): NodeChannelSectionLayout | null {
        const outputIndex = idx - (this.inputChannelName ? 1 : 0);
        return outputIndex >= 0 ? this.layout().outputSections[outputIndex] ?? null : null;
    }

    private outputPropertyDefinitions(channelName: string): ITaskPropertyDefinition[] {
        return this.layoutFactory.outputPropertyDefinitions(this.layoutRequest(), channelName);
    }

    private outputChannelLabel(channelName: string, outputIndex: number): string {
        return this.layoutFactory.outputChannelLabel(this.layoutRequest(), channelName, outputIndex);
    }

    private clonePropertyDefinitions(properties: ITaskPropertyDefinition[]): ITaskPropertyDefinition[] {
        return this.layoutFactory.clonePropertyDefinitions(properties);
    }
}

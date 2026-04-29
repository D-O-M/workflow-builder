import { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';

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
export type NodeKind = 'PluginTask' | 'CollectionIterator';
export type NodeVisualKind = 'TaskCard' | 'CollectionIteratorOperator';
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
    static readonly collectionIteratorOperatorWidth = 240;
    static readonly collectionIteratorOperatorHeight = 112;

    private static readonly titleAreaHeight = 60;
    private static readonly sectionHeaderHeight = 30;
    private static readonly collapsedRowHeight = 36;
    private static readonly propertyRowHeight = 28;
    private static readonly sectionGap = 8;
    private static readonly contentPaddingX = 18;
    private static readonly bottomPadding = 12;
    private static readonly summaryPortRadius = 6.8;
    private static readonly propertyPortRadius = 4.8;
    private static readonly summarySocketHitPaddingX = 9;
    private static readonly summarySocketHitPaddingY = 8;
    private static readonly propertyPortOffset = 0;
    private static readonly propertyPortHitPadding = 7;
    private static readonly toggleSize = 24;
    private collapsedLayoutCache: NodeLayout | null = null;
    private expandedLayoutCache: NodeLayout | null = null;
    private readonly sectionLayoutCache = new Map<string, NodeLayout>();

    static clamp(v: number, min: number, max: number): number {
        return Math.min(Math.max(v, min), max);
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
        );
    }

    contains(x: number, y: number): boolean {
        return (
            x >= this.x &&
            x <= this.x + this.width &&
            y >= this.y &&
            y <= this.y + this.height
        );
    }

    containsBuffered(x: number, y: number, buffer: number): boolean {
        return (
            x >= this.x - buffer &&
            x <= this.x + this.width + buffer &&
            y >= this.y - buffer &&
            y <= this.y + this.height + buffer
        );
    }

    isFullyWithin(left: number, top: number, right: number, bottom: number): boolean {
        return (
            this.x >= left &&
            this.y >= top &&
            this.x + this.width <= right &&
            this.y + this.height <= bottom
        );
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
        if (this.isCollectionIteratorOperator) {
            this.collapsedLayoutCache ??= this.buildCollectionIteratorOperatorLayout();
            return this.collapsedLayoutCache;
        }

        if (expanded) {
            this.expandedLayoutCache ??= this.buildLayout(true);
            return this.expandedLayoutCache;
        }

        this.collapsedLayoutCache ??= this.buildLayout(false);
        return this.collapsedLayoutCache;
    }

    layoutForExpandedSections(expandedSectionKeys: ReadonlySet<string>): NodeLayout {
        if (this.isCollectionIteratorOperator) {
            return this.layoutForState(false);
        }

        if (expandedSectionKeys.size === 0) {
            return this.layoutForState(false);
        }

        const allSectionKeys = this.allSectionKeys();
        if (expandedSectionKeys.size === allSectionKeys.length && allSectionKeys.every((key) => expandedSectionKeys.has(key))) {
            return this.layoutForState(true);
        }

        const cacheKey = [...expandedSectionKeys].sort((left, right) => left.localeCompare(right)).join('|');
        const cachedLayout = this.sectionLayoutCache.get(cacheKey);
        if (cachedLayout) {
            return cachedLayout;
        }

        const layout = this.buildLayout(expandedSectionKeys);
        this.sectionLayoutCache.set(cacheKey, layout);
        return layout;
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
        const layout = this.layout();
        const anchors: NodeAnchor[] = [];
        let index = 0;

        if (layout.inputSection) {
            anchors.push({
                index,
                kind: 'input',
                label: layout.inputSection.label,
                side: 'left',
                x: layout.inputSection.summaryPort.x,
                y: layout.inputSection.summaryPort.y,
            });
            index += 1;
        }

        for (const section of layout.outputSections) {
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

    visiblePorts(): NodePortDefinition[] {
        const layout = this.layout();
        return [...layout.summaryPorts, ...layout.propertyPorts];
    }

    portDefinition(portKey: string): NodePortDefinition | null {
        return this.visiblePorts().find((port) => port.key === portKey) ?? null;
    }

    portHit(x: number, y: number, extraHitRadius = 5): NodePortDefinition | null {
        const ports = [...this.visiblePorts()].reverse();
        for (const port of ports) {
            if (NodeModel.pointInsideExpandedRect(x, y, port.hitBounds, extraHitRadius)) {
                return port;
            }

            if (Math.hypot(x - port.x, y - port.y) <= port.radius + extraHitRadius) {
                return port;
            }
        }

        return null;
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
            ? NodeModel.summaryPortRadius + 1
            : NodeModel.summaryPortRadius;
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
        const cx = this.x + this.width / 2;
        const cy = this.y + this.height / 2;
        const dx = x - cx;
        const dy = y - cy;
        if (Math.abs(dx) * this.height > Math.abs(dy) * this.width) {
            return dx < 0 ? 'left' : 'right';
        }
        return dy < 0 ? 'top' : 'bottom';
    }

    snapPointToSide(x: number, y: number): { x: number; y: number; side: Side; pos: number } {
        const left = Math.abs(x - this.x);
        const right = Math.abs(x - (this.x + this.width));
        const top = Math.abs(y - this.y);
        const bottom = Math.abs(y - (this.y + this.height));
        const min = Math.min(left, right, top, bottom);
        if (min === left) {
            const yy = NodeModel.clamp(y, this.y, this.y + this.height);
            return { x: this.x, y: yy, side: 'left', pos: yy - this.y };
        } else if (min === right) {
            const yy = NodeModel.clamp(y, this.y, this.y + this.height);
            return { x: this.x + this.width, y: yy, side: 'right', pos: yy - this.y };
        } else if (min === top) {
            const xx = NodeModel.clamp(x, this.x, this.x + this.width);
            return { x: xx, y: this.y, side: 'top', pos: xx - this.x };
        } else {
            const xx = NodeModel.clamp(x, this.x, this.x + this.width);
            return { x: xx, y: this.y + this.height, side: 'bottom', pos: xx - this.x };
        }
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
        return Object.keys(this.designTimeInputOverrides).length;
    }

    get hasDesignTimeOverrides(): boolean {
        return this.designTimeOverrideCount > 0;
    }

    get inputChannelLabel(): string | null {
        return this.channelLabel(this.inputChannelName, this.inputChannelDisplayName);
    }

    get visualKind(): NodeVisualKind {
        return this.nodeKind === 'CollectionIterator'
            ? 'CollectionIteratorOperator'
            : 'TaskCard';
    }

    get isCollectionIteratorOperator(): boolean {
        return this.visualKind === 'CollectionIteratorOperator';
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

    private buildLayout(expanded: boolean | ReadonlySet<string>): NodeLayout {
        let cursorY = this.y + NodeModel.titleAreaHeight;
        const summaryPorts: NodePortDefinition[] = [];
        const propertyPorts: NodePortDefinition[] = [];
        const toggleRect = {
            x: this.x + this.width - NodeModel.toggleSize - 14,
            y: this.y + 12,
            width: NodeModel.toggleSize,
            height: NodeModel.toggleSize,
        };

        const inputSection = this.inputChannelName
            ? this.buildSection(
                this.inputChannelName,
                this.inputChannelLabel ?? this.inputChannelName,
                'left',
                cursorY,
                this.isSectionExpanded(expanded, 'left', this.inputChannelName),
                this.inputPropertyDefinitions.length > 0
                    ? this.clonePropertyDefinitions(this.inputPropertyDefinitions)
                    : this.propertyDefinitionsFromKinds(this.inputProperties),
                0,
              )
            : null;

        if (inputSection) {
            summaryPorts.push(inputSection.summaryPort);
            propertyPorts.push(...inputSection.propertyRows.map((row) => row.port));
            cursorY = inputSection.bottom + NodeModel.sectionGap;
        }

        const outputSections = this.outputChannelNames.map((channelName, outputIndex) => {
            const section = this.buildSection(
                channelName,
                this.outputChannelLabel(channelName, outputIndex),
                'right',
                cursorY,
                this.isSectionExpanded(expanded, 'right', channelName),
                this.outputPropertyDefinitions(channelName),
                outputIndex + (this.inputChannelName ? 1 : 0),
            );
            summaryPorts.push(section.summaryPort);
            propertyPorts.push(...section.propertyRows.map((row) => row.port));
            cursorY = section.bottom + NodeModel.sectionGap;
            return section;
        });

        const anyExpanded = typeof expanded === 'boolean'
            ? expanded
            : expanded.size > 0;
        const totalHeight = Math.max(
            anyExpanded ? 188 : 156,
            cursorY - this.y + NodeModel.bottomPadding - NodeModel.sectionGap,
        );

        return {
            totalHeight,
            titleBaseline: this.y + 29,
            versionBaseline: this.y + 48,
            titleMaxWidth: this.width - 86,
            toggleRect,
            inputSection,
            outputSections,
            summaryPorts,
            propertyPorts,
        };
    }

    private buildCollectionIteratorOperatorLayout(): NodeLayout {
        const totalHeight = NodeModel.collectionIteratorOperatorHeight;
        const centerY = this.y + totalHeight / 2;
        const operatorHeaderHeight = 40;
        const sectionTop = centerY - operatorHeaderHeight / 2;
        const toggleRect = {
            x: this.x + this.width - NodeModel.toggleSize - 12,
            y: this.y + 10,
            width: NodeModel.toggleSize,
            height: NodeModel.toggleSize,
        };
        const inputSection = this.buildOperatorSection(
            'Collection',
            'Collection',
            'left',
            sectionTop,
            operatorHeaderHeight,
            0,
            this.collectionIteratorInputDisplayKind,
        );
        const outputSection = this.buildOperatorSection(
            'Item',
            'Item',
            'right',
            sectionTop,
            operatorHeaderHeight,
            1,
            this.collectionIteratorItemDisplayKind,
        );

        return {
            totalHeight,
            titleBaseline: this.y + 78,
            versionBaseline: this.y + 94,
            titleMaxWidth: this.width - 44,
            toggleRect,
            inputSection,
            outputSections: [outputSection],
            summaryPorts: [inputSection.summaryPort, outputSection.summaryPort],
            propertyPorts: [],
        };
    }

    private buildOperatorSection(
        channelName: string,
        channelLabel: string,
        side: 'left' | 'right',
        top: number,
        headerHeight: number,
        anchorIndex: number,
        countLabel: string,
    ): NodeChannelSectionLayout {
        const centerY = top + headerHeight / 2;
        const summarySocketSize = NodeModel.summarySocketSize(headerHeight);
        const summarySocketRect = NodeModel.summarySocketRect(this.x, this.width, side, centerY, summarySocketSize);
        const summaryPortX = side === 'left'
            ? this.x
            : this.x + this.width;
        const summaryPort: NodePortDefinition = {
            key: `${side}:${channelName}:summary`,
            kind: side === 'left' ? 'input-summary' : 'output-summary',
            anchorIndex,
            channelName,
            channelLabel,
            propertyName: null,
            label: channelLabel,
            side,
            x: summaryPortX,
            y: centerY,
            radius: NodeModel.summaryPortRadius,
            visualBounds: summarySocketRect,
            hitBounds: NodeModel.inflateRect(
                summarySocketRect,
                NodeModel.summarySocketHitPaddingX,
                NodeModel.summarySocketHitPaddingY,
            ),
        };

        return {
            key: this.sectionKey(side, channelName),
            name: channelName,
            label: channelLabel,
            countLabel,
            side,
            expanded: false,
            top,
            headerTop: top,
            headerHeight,
            toggleRect: {
                x: this.x + this.width - NodeModel.toggleSize - 18,
                y: top + (headerHeight - NodeModel.toggleSize) / 2,
                width: NodeModel.toggleSize,
                height: NodeModel.toggleSize,
            },
            bottom: top + headerHeight,
            summaryPort,
            propertyRows: [],
        };
    }

    private buildSection(
        channelName: string,
        channelLabel: string,
        side: 'left' | 'right',
        top: number,
        expanded: boolean,
        propertyDefinitions: ITaskPropertyDefinition[],
        anchorIndex: number,
    ): NodeChannelSectionLayout {
        const normalizedProperties = propertyDefinitions
            .map((property) => ({
                ...property,
                enumValues: [...property.enumValues],
            }))
            .sort((left, right) => left.name.localeCompare(right.name));
        const headerHeight = expanded ? NodeModel.sectionHeaderHeight : NodeModel.collapsedRowHeight;
        const summaryY = top + headerHeight / 2;
        const key = this.sectionKey(side, channelName);
        const summarySocketSize = NodeModel.summarySocketSize(headerHeight);
        const summarySocketRect = NodeModel.summarySocketRect(this.x, this.width, side, summaryY, summarySocketSize);
        const summaryPortX = side === 'left'
            ? this.x
            : this.x + this.width;
        const toggleRect = {
            x: this.x + this.width - NodeModel.toggleSize - 18,
            y: top + (headerHeight - NodeModel.toggleSize) / 2,
            width: NodeModel.toggleSize,
            height: NodeModel.toggleSize,
        };
        const summaryPort: NodePortDefinition = {
            key: `${side}:${channelName}:summary`,
            kind: side === 'left' ? 'input-summary' : 'output-summary',
            anchorIndex,
            channelName,
            channelLabel,
            propertyName: null,
            label: channelLabel,
            side,
            x: summaryPortX,
            y: summaryY,
            radius: NodeModel.summaryPortRadius,
            visualBounds: summarySocketRect,
            hitBounds: NodeModel.inflateRect(
                summarySocketRect,
                NodeModel.summarySocketHitPaddingX,
                NodeModel.summarySocketHitPaddingY,
            ),
        };

        if (!expanded) {
            return {
                key,
                name: channelName,
                label: channelLabel,
                countLabel: this.propertyCountLabel(normalizedProperties.length),
                side,
                expanded,
                top,
                headerTop: top,
                headerHeight,
                toggleRect,
                bottom: top + headerHeight,
                summaryPort,
                propertyRows: [],
            };
        }

        const propertyRows: NodePropertyRow[] = [];
        let cursorY = top + headerHeight;
        for (const property of normalizedProperties) {
            const centerY = cursorY + NodeModel.propertyRowHeight / 2;
            const portX = side === 'left'
                ? this.x - NodeModel.propertyPortOffset
                : this.x + this.width + NodeModel.propertyPortOffset;
            const port: NodePortDefinition = {
                key: `${side}:${channelName}:${property.name}`,
                kind: side === 'left' ? 'input-property' : 'output-property',
                anchorIndex,
                channelName,
                channelLabel,
                propertyName: property.name,
                label: property.name,
                side,
                x: portX,
                y: centerY,
                radius: NodeModel.propertyPortRadius,
                hitBounds: {
                    x: portX - NodeModel.propertyPortRadius - NodeModel.propertyPortHitPadding,
                    y: centerY - NodeModel.propertyPortRadius - NodeModel.propertyPortHitPadding,
                    width: (NodeModel.propertyPortRadius + NodeModel.propertyPortHitPadding) * 2,
                    height: (NodeModel.propertyPortRadius + NodeModel.propertyPortHitPadding) * 2,
                },
            };

            propertyRows.push({
                key: port.key,
                name: property.name,
                label: property.name,
                kind: property.kind,
                displayKind: this.propertyDisplayKind(side, channelName, property),
                required: property.required,
                top: cursorY,
                height: NodeModel.propertyRowHeight,
                centerY,
                port,
            });
            cursorY += NodeModel.propertyRowHeight;
        }

        return {
            key,
            name: channelName,
            label: channelLabel,
            countLabel: this.propertyCountLabel(normalizedProperties.length),
            side,
            expanded,
            top,
            headerTop: top,
            headerHeight,
            toggleRect,
            bottom: cursorY,
            summaryPort,
            propertyRows,
        };
    }

    private isSectionExpanded(expanded: boolean | ReadonlySet<string>, side: 'left' | 'right', channelName: string): boolean {
        return typeof expanded === 'boolean'
            ? expanded
            : expanded.has(this.sectionKey(side, channelName));
    }

    private sectionKey(side: 'left' | 'right', channelName: string): string {
        return `${side}:${channelName}`;
    }

    private static summarySocketSize(_headerHeight: number): number {
        return NodeModel.summaryPortRadius * 2;
    }

    private static summarySocketRect(nodeX: number, nodeWidth: number, side: 'left' | 'right', centerY: number, size: number): Rect {
        const edgeX = side === 'left'
            ? nodeX
            : nodeX + nodeWidth;
        return {
            x: edgeX - size / 2,
            y: centerY - size / 2,
            width: size,
            height: size,
        };
    }

    private static inflateRect(rect: Rect, paddingX: number, paddingY: number): Rect {
        return {
            x: rect.x - paddingX,
            y: rect.y - paddingY,
            width: rect.width + paddingX * 2,
            height: rect.height + paddingY * 2,
        };
    }

    private static pointInsideExpandedRect(x: number, y: number, rect: Rect | undefined, padding: number): boolean {
        return !!rect &&
            x >= rect.x - padding &&
            x <= rect.x + rect.width + padding &&
            y >= rect.y - padding &&
            y <= rect.y + rect.height + padding;
    }

    private allSectionKeys(): string[] {
        if (this.isCollectionIteratorOperator) {
            return [];
        }

        return [
            ...(this.inputChannelName ? [this.sectionKey('left', this.inputChannelName)] : []),
            ...this.outputChannelNames.map((channelName) => this.sectionKey('right', channelName)),
        ];
    }

    private outputSectionForAnchor(idx: number): NodeChannelSectionLayout | null {
        const outputIndex = idx - (this.inputChannelName ? 1 : 0);
        return outputIndex >= 0 ? this.layout().outputSections[outputIndex] ?? null : null;
    }

    private outputPropertyDefinitions(channelName: string): ITaskPropertyDefinition[] {
        const explicitDefinitions = this.outputChannelPropertyDefinitions[channelName];
        if (explicitDefinitions && explicitDefinitions.length > 0) {
            return this.clonePropertyDefinitions(explicitDefinitions);
        }

        return this.propertyDefinitionsFromKinds(this.outputChannelProperties[channelName] ?? {});
    }

    private outputChannelLabel(channelName: string, outputIndex: number): string {
        return this.channelLabel(channelName, this.outputChannelDisplayNames[outputIndex] ?? null) ?? channelName;
    }

    private propertyDisplayKind(
        side: 'left' | 'right',
        channelName: string,
        property: ITaskPropertyDefinition,
    ): string {
        if (this.nodeKind !== 'CollectionIterator' || this.collectionIteratorItemKind !== null) {
            return property.kind;
        }

        if (side === 'left' && channelName === 'Collection' && property.name === 'items') {
            return 'T[]';
        }

        if (side === 'right' && channelName === 'Item' && property.name === 'item') {
            return 'T';
        }

        return property.kind;
    }

    private channelLabel(name: string | null, displayName: string | null | undefined): string | null {
        const trimmedDisplayName = displayName?.trim();
        return trimmedDisplayName && trimmedDisplayName.length > 0
            ? trimmedDisplayName
            : name;
    }

    private propertyCountLabel(propertyCount: number): string {
        return propertyCount === 1 ? '1 property' : `${propertyCount} properties`;
    }

    private propertyDefinitionsFromKinds(
        properties: Record<string, TaskPropertyType>,
    ): ITaskPropertyDefinition[] {
        return Object.entries(properties)
            .sort(([left], [right]) => left.localeCompare(right))
            .map(([name, kind]) => ({
                name,
                kind,
                required: false,
                enumValues: [],
            }));
    }

    private clonePropertyDefinitions(properties: ITaskPropertyDefinition[]): ITaskPropertyDefinition[] {
        return properties.map((property) => ({
            ...property,
            enumValues: [...property.enumValues],
        }));
    }
}

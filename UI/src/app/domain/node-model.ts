export interface Point {
    x: number;
    y: number;
}

export type Side = 'left' | 'right' | 'top' | 'bottom';
export type NodeRenderStatus = 'NotExecuted' | 'Pending' | 'InProgress' | 'Completed' | 'Failed';
export type TaskPropertyType = 'Number' | 'String' | 'Boolean' | 'Guid';

export interface NodeAnchor {
    index: number;
    kind: 'input' | 'output';
    label: string;
    side: Side;
    x: number;
    y: number;
}

export class NodeModel {
    static buffer = 20;

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
    ) {}

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

    anchorPositions(): Point[] {
        return this.anchorDefinitions().map(({ x, y }) => ({ x, y }));
    }

    anchorDefinitions(): NodeAnchor[] {
        const anchors: NodeAnchor[] = [];
        let index = 0;

        if (this.inputChannelName) {
            anchors.push({
                index,
                kind: 'input',
                label: this.inputChannelName,
                side: 'left',
                x: this.x,
                y: this.y + 62,
            });
            index += 1;
        }

        const startY = this.y + 140;
        const rowGap = 34;

        this.outputChannelNames.forEach((label, outputIndex) => {
            anchors.push({
                index: index + outputIndex,
                kind: 'output',
                label,
                side: 'right',
                x: this.x + this.width,
                y: startY + outputIndex * rowGap,
            });
        });

        return anchors;
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
        return this.anchorDefinition(idx)?.kind === 'input' ? 8 : 7;
    }

    isInputAnchor(idx: number): boolean {
        return this.anchorDefinition(idx)?.kind === 'input';
    }

    isOutputAnchor(idx: number): boolean {
        return this.anchorDefinition(idx)?.kind === 'output';
    }

    outputChannelNameForAnchor(idx: number): string | null {
        const anchor = this.anchorDefinition(idx);
        return anchor?.kind === 'output' ? anchor.label : null;
    }

    outputPropertiesForAnchor(idx: number): Record<string, TaskPropertyType> {
        const channelName = this.outputChannelNameForAnchor(idx);
        if (!channelName) {
            return {};
        }

        return this.outputChannelProperties[channelName] ?? {};
    }

    inputAnchorSnap(): { x: number; y: number; side: Side; pos: number } | null {
        const idx = this.inputAnchorIndex();
        if (idx === null) {
            return null;
        }

        const anchor = this.anchorDefinition(idx);
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
        return dy < 0 ? "top" : "bottom";
    }

    snapPointToSide(x: number, y: number): { x: number; y: number; side: Side; pos: number } {
        const left = Math.abs(x - this.x);
        const right = Math.abs(x - (this.x + this.width));
        const top = Math.abs(y - this.y);
        const bottom = Math.abs(y - (this.y + this.height));
        const min = Math.min(left, right, top, bottom);
        if (min === left) {
            const yy = NodeModel.clamp(y, this.y, this.y + this.height);
            return { x: this.x, y: yy, side: "left", pos: yy - this.y };
        } else if (min === right) {
            const yy = NodeModel.clamp(y, this.y, this.y + this.height);
            return { x: this.x + this.width, y: yy, side: "right", pos: yy - this.y };
        } else if (min === top) {
            const xx = NodeModel.clamp(x, this.x, this.x + this.width);
            return { x: xx, y: this.y, side: "top", pos: xx - this.x };
        } else {
            const xx = NodeModel.clamp(x, this.x, this.x + this.width);
            return { x: xx, y: this.y + this.height, side: "bottom", pos: xx - this.x };
        }
    }

    snapPointFixedSide(x: number, y: number, side: Side): { x: number; y: number; side: Side; pos: number } {
        switch (side) {
            case "left":
                const yyL = NodeModel.clamp(y, this.y, this.y + this.height);
                return { x: this.x, y: yyL, side: "left", pos: yyL - this.y };
            case "right":
                const yyR = NodeModel.clamp(y, this.y, this.y + this.height);
                return { x: this.x + this.width, y: yyR, side: "right", pos: yyR - this.y };
            case "top":
                const xxT = NodeModel.clamp(x, this.x, this.x + this.width);
                return { x: xxT, y: this.y, side: "top", pos: xxT - this.x };
            default:
                const xxB = NodeModel.clamp(x, this.x, this.x + this.width);
                return { x: xxB, y: this.y + this.height, side: "bottom", pos: xxB - this.x };
        }
    }

    get designTimeOverrideCount(): number {
        return Object.keys(this.designTimeInputOverrides).length;
    }

    get hasDesignTimeOverrides(): boolean {
        return this.designTimeOverrideCount > 0;
    }

    designTimeOverrideKeys(): string[] {
        return Object.keys(this.designTimeInputOverrides).sort((left, right) => left.localeCompare(right));
    }
}

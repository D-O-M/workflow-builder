import { Signal, signal } from '@angular/core';

export type Canvas2DContext =
    | CanvasRenderingContext2D
    | OffscreenCanvasRenderingContext2D;

export interface CanvasRenderSurface {
    readonly ctx: Canvas2DContext;
    readonly fontReady: Signal<boolean>;
    readonly materialIconReady: boolean;
    readonly materialIconFontFamily: string;
    readonly materialIconFontUrl: string | null;
    setFont(weight: 500 | 600 | 700, size: number): void;
    roundedRect(
        x: number,
        y: number,
        width: number,
        height: number,
        radius: number,
    ): void;
    fillRect(x: number, y: number, width: number, height: number): void;
    drawWrappedText(text: string, x: number, y: number, maxWidth: number): void;
    drawEllipsizedText(
        text: string,
        x: number,
        y: number,
        maxWidth: number,
    ): string;
    ellipsizeText(text: string, maxWidth: number): string;
    wrapText(text: string, maxWidth: number): string[];
}

export interface CanvasDrawSurfaceOptions {
    readonly fontFamily?: string;
    readonly fontReady?: boolean;
    readonly materialIconReady?: boolean;
    readonly materialIconFontUrl?: string | null;
    readonly loadDocumentFonts?: boolean;
}

export class CanvasDrawSurface implements CanvasRenderSurface {
    private static readonly primaryFontFamilyName = '"Inter"';
    private static readonly materialIconFontFamilyName = '"Material Icons"';
    private static readonly materialIconStylesheetUrlPattern =
        /fonts\.googleapis\.com\/icon\?.*family=Material\+Icons/i;
    private static readonly stylesheetFontUrlPattern =
        /url\((['"]?)(?<url>[^'")]+)\1\)\s+format\((['"]?)woff2\3\)/i;
    private static readonly fallbackFontFamily =
        "Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif";

    readonly fontReady = signal(false);
    readonly materialIconFontFamily = "'Material Icons'";
    private materialIconResolvedFontUrl: string | null = null;
    private fontFamily: string;
    private materialIconLoaded = false;

    constructor(
        readonly ctx: Canvas2DContext,
        options: CanvasDrawSurfaceOptions = {},
    ) {
        this.fontFamily = options.fontFamily ?? this.resolveFontFamily();
        this.materialIconResolvedFontUrl =
            options.materialIconFontUrl ?? this.materialIconResolvedFontUrl;
        this.materialIconLoaded = options.materialIconReady ?? false;
        if (options.fontReady === true || options.loadDocumentFonts === false) {
            this.fontReady.set(true);
            return;
        }

        void this.ensureFontLoaded();
    }

    get materialIconReady(): boolean {
        return this.materialIconLoaded;
    }

    get materialIconFontUrl(): string | null {
        return this.materialIconResolvedFontUrl;
    }

    get resolvedFontFamily(): string {
        return this.fontFamily;
    }

    updateFontState(options: CanvasDrawSurfaceOptions): void {
        this.fontFamily = options.fontFamily ?? this.fontFamily;
        this.materialIconResolvedFontUrl =
            options.materialIconFontUrl ?? this.materialIconResolvedFontUrl;
        this.materialIconLoaded =
            options.materialIconReady ?? this.materialIconLoaded;
        if (options.fontReady !== undefined) {
            this.fontReady.set(options.fontReady);
        }
    }

    setFont(weight: 500 | 600 | 700, size: number): void {
        this.ctx.font = this.fontReady()
            ? `${weight} ${size}px ${this.fontFamily}`
            : `${weight} ${size}px ${CanvasDrawSurface.fallbackFontFamily}`;
    }

    roundedRect(
        x: number,
        y: number,
        width: number,
        height: number,
        radius: number,
    ): void {
        const maxRadius = Math.min(radius, width / 2, height / 2);
        this.ctx.beginPath();
        this.ctx.moveTo(x + maxRadius, y);
        this.ctx.lineTo(x + width - maxRadius, y);
        this.ctx.quadraticCurveTo(x + width, y, x + width, y + maxRadius);
        this.ctx.lineTo(x + width, y + height - maxRadius);
        this.ctx.quadraticCurveTo(
            x + width,
            y + height,
            x + width - maxRadius,
            y + height,
        );
        this.ctx.lineTo(x + maxRadius, y + height);
        this.ctx.quadraticCurveTo(x, y + height, x, y + height - maxRadius);
        this.ctx.lineTo(x, y + maxRadius);
        this.ctx.quadraticCurveTo(x, y, x + maxRadius, y);
        this.ctx.closePath();
    }

    fillRect(x: number, y: number, width: number, height: number): void {
        this.ctx.beginPath();
        this.ctx.rect(x, y, width, height);
        this.ctx.fill();
    }

    drawWrappedText(
        text: string,
        x: number,
        y: number,
        maxWidth: number,
    ): void {
        const lines = this.wrapText(text, maxWidth);
        for (const line of lines) {
            this.ctx.fillText(line, x, y);
            y += 18;
        }
    }

    drawEllipsizedText(
        text: string,
        x: number,
        y: number,
        maxWidth: number,
    ): string {
        const displayText = this.ellipsizeText(text, maxWidth);
        if (displayText.length > 0) {
            this.ctx.fillText(displayText, x, y);
        }

        return displayText;
    }

    ellipsizeText(text: string, maxWidth: number): string {
        if (maxWidth <= 0 || text.length === 0) {
            return '';
        }

        if (this.ctx.measureText(text).width <= maxWidth) {
            return text;
        }

        const ellipsis = '...';
        if (this.ctx.measureText(ellipsis).width > maxWidth) {
            return '';
        }

        let low = 0;
        let high = text.length;
        let best = ellipsis;
        while (low <= high) {
            const mid = Math.floor((low + high) / 2);
            const candidate = `${text.slice(0, mid).trimEnd()}${ellipsis}`;
            if (this.ctx.measureText(candidate).width <= maxWidth) {
                best = candidate;
                low = mid + 1;
            } else {
                high = mid - 1;
            }
        }

        return best;
    }

    wrapText(text: string, maxWidth: number): string[] {
        const words = text.split(/\s+/);
        let line = '';
        const lines: string[] = [];
        for (const word of words) {
            const test = line ? `${line} ${word}` : word;
            if (this.ctx.measureText(test).width > maxWidth && line) {
                lines.push(line);
                line = word;
            } else {
                line = test;
            }
        }
        if (line) {
            lines.push(line);
        }
        return lines;
    }

    private async ensureFontLoaded(): Promise<void> {
        try {
            const fonts = (document as unknown as { fonts?: FontFaceSet })
                .fonts;
            if (!fonts) {
                this.fontReady.set(true);
                return;
            }

            await fonts.load(
                `600 14px ${CanvasDrawSurface.primaryFontFamilyName}`,
            );
            try {
                await fonts.load(
                    `400 20px ${CanvasDrawSurface.materialIconFontFamilyName}`,
                );
                this.materialIconResolvedFontUrl =
                    await this.resolveMaterialIconFontUrl();
                this.materialIconLoaded = true;
            } catch {
                this.materialIconLoaded = false;
            }
            await fonts.ready;
            this.materialIconLoaded =
                this.materialIconLoaded &&
                fonts.check(
                    `400 20px ${CanvasDrawSurface.materialIconFontFamilyName}`,
                );
            this.fontReady.set(true);
        } catch {
            this.materialIconLoaded = false;
            this.fontReady.set(true);
        }
    }

    private async resolveMaterialIconFontUrl(): Promise<string | null> {
        if (typeof document === 'undefined' || typeof fetch !== 'function') {
            return this.materialIconResolvedFontUrl;
        }

        const stylesheet = [...document.querySelectorAll<HTMLLinkElement>('link[rel="stylesheet"]')]
            .find((link) => CanvasDrawSurface.materialIconStylesheetUrlPattern.test(link.href));
        if (!stylesheet) {
            return this.materialIconResolvedFontUrl;
        }

        try {
            const response = await fetch(stylesheet.href);
            if (!response.ok) {
                return this.materialIconResolvedFontUrl;
            }

            const css = await response.text();
            const match = CanvasDrawSurface.stylesheetFontUrlPattern.exec(css);
            return match?.groups?.['url'] ?? this.materialIconResolvedFontUrl;
        } catch {
            return this.materialIconResolvedFontUrl;
        }
    }

    private resolveFontFamily(): string {
        if (typeof document === 'undefined') {
            return CanvasDrawSurface.fallbackFontFamily;
        }

        const configuredFontFamily = getComputedStyle(document.documentElement)
            .getPropertyValue('--app-font-family')
            .trim();

        return configuredFontFamily || CanvasDrawSurface.fallbackFontFamily;
    }
}

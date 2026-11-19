import {
  CanvasDetailTier,
  CanvasPortConnectivityIndex,
  CanvasValidationSeverity,
  RenderedNode,
} from './canvas-render-model';
import { CanvasRenderSurface } from './canvas-draw-surface';
import { CanvasNodeIssueSummary } from './canvas-node-render-types';
import { CanvasTheme } from './canvas-theme';
import { NodeChannelSectionLayout, NodeModel, NodePortDefinition, Side } from './node-model';

export interface CanvasNodeRenderDrag {
  readonly from: number;
  readonly anchor: number;
  readonly x: number;
  readonly y: number;
  readonly side?: Side;
  readonly portKey?: string | null;
  readonly sourceProperty?: string | null;
}

export interface CanvasNodeValidationFocus {
  readonly severity: CanvasValidationSeverity;
  readonly nodeId: number | null;
  readonly sectionKey?: string | null;
  readonly portKeys?: ReadonlySet<string>;
}

export interface CanvasNodeRenderOptions {
  readonly readOnly?: boolean;
  readonly detailTier?: CanvasDetailTier;
  readonly blockedTargetNodeIds?: ReadonlySet<number>;
  readonly focusedPortKeys?: ReadonlySet<string>;
  readonly collectionIteratorPreviewLabels?: ReadonlyMap<number, string>;
  readonly nodeIssueSummaries?: ReadonlyMap<number, CanvasNodeIssueSummary>;
  readonly passiveSummaryPorts?: boolean;
  readonly validationFocus?: CanvasNodeValidationFocus | null;
}

export interface CanvasNodeRenderRequest {
  readonly nodes: readonly RenderedNode[];
  readonly portConnectivity: CanvasPortConnectivityIndex;
  readonly drag: CanvasNodeRenderDrag | null;
  readonly hoveredNodeId: number | null;
  readonly hoveredPortKey: string | null;
  readonly selectedNodeIds: ReadonlySet<number>;
  readonly options?: CanvasNodeRenderOptions;
}

export class CanvasNodeRenderer {
  constructor(private readonly surface: CanvasRenderSurface) {}

  drawNodes(request: CanvasNodeRenderRequest): void {
    const readOnly = request.options?.readOnly ?? false;
    const detailTier = request.options?.detailTier ?? 'full';
    const blockedTargetNodeIds = request.options?.blockedTargetNodeIds ?? new Set<number>();
    const focusedPortKeys = request.options?.focusedPortKeys ?? new Set<string>();
    const collectionIteratorPreviewLabels = request.options?.collectionIteratorPreviewLabels ?? new Map<number, string>();
    const nodeIssueSummaries = request.options?.nodeIssueSummaries ?? new Map<number, CanvasNodeIssueSummary>();
    const passiveSummaryPorts = request.options?.passiveSummaryPorts ?? false;
    const validationFocus = request.options?.validationFocus ?? null;
    const validationPortKeys = validationFocus?.portKeys ?? new Set<string>();
    const connectedSummaryPortKeys = request.portConnectivity.summaryPortKeys;
    const connectedPropertyPortKeys = request.portConnectivity.propertyPortKeys;

    for (const node of request.nodes) {
      const layout = node.layout;
      const statusTheme = node.renderStatus ? CanvasTheme.status[node.renderStatus] : null;
      const isSelected = request.selectedNodeIds.has(node.id);
      const validationNodeFocused = validationFocus?.nodeId === node.id;
      const validationTheme = validationNodeFocused && validationFocus
        ? CanvasTheme.validation[validationFocus.severity]
        : null;
      const issueSummary = !readOnly ? nodeIssueSummaries.get(node.id) ?? null : null;
      const titleAccessoryRect = readOnly && statusTheme && node.renderStatus
        ? node.settingsRect()
        : issueSummary
          ? node.settingsRect()
          : null;

      if (node.isDataBridge) {
        this.drawDataBridgeNode(
          node,
          connectedSummaryPortKeys,
          connectedPropertyPortKeys,
          request.drag,
          request.hoveredNodeId,
          request.hoveredPortKey,
          isSelected,
          focusedPortKeys,
          validationNodeFocused ? validationFocus : null,
          validationPortKeys,
          passiveSummaryPorts,
        );
        continue;
      }

      if (node.isCollectionIteratorOperator) {
        this.drawCollectionIteratorOperator(
          node,
          request.portConnectivity,
          request.drag,
          request.hoveredNodeId,
          request.hoveredPortKey,
          isSelected,
          readOnly,
          detailTier,
          blockedTargetNodeIds,
          focusedPortKeys,
          validationNodeFocused ? validationFocus : null,
          validationPortKeys,
          issueSummary,
          collectionIteratorPreviewLabels.get(node.id) ?? null,
          passiveSummaryPorts,
        );
        continue;
      }

      this.surface.ctx.save();
      this.surface.ctx.shadowColor = detailTier === 'full' ? CanvasTheme.nodeShadow : 'transparent';
      this.surface.ctx.shadowBlur = detailTier === 'full' ? 10 : 0;
      this.surface.ctx.shadowOffsetY = detailTier === 'full' ? 5 : 0;
      this.surface.ctx.fillStyle = detailTier === 'full'
        ? this.createNodeGradient(node)
        : CanvasTheme.surface;
      this.surface.ctx.strokeStyle = isSelected
        ? CanvasTheme.connection.selected
        : statusTheme?.border ?? CanvasTheme.nodeStroke;
      this.surface.ctx.lineJoin = 'round';
      this.surface.ctx.lineWidth = isSelected ? 2.1 : 1;

      const isHoveredDest = request.drag && node.id !== request.drag.from && request.hoveredNodeId === node.id;
      const eligible = request.drag && node.id !== request.drag.from && !blockedTargetNodeIds.has(node.id);

      if (isHoveredDest) {
        this.surface.ctx.strokeStyle = CanvasTheme.nodeStrokeHover;
        this.surface.ctx.setLineDash([5, 4]);
      } else if (eligible) {
        this.surface.ctx.strokeStyle = CanvasTheme.eligibleDash;
        this.surface.ctx.globalAlpha = 0.55;
      }

      this.surface.roundedRect(node.x, node.y, node.width, node.height, 10);
      this.surface.ctx.fill();
      this.surface.ctx.shadowColor = 'transparent';
      this.surface.ctx.shadowBlur = 0;
      if (validationTheme) {
        this.surface.ctx.save();
        this.surface.ctx.fillStyle = validationTheme.surface;
        this.surface.ctx.globalAlpha = 0.22;
        this.surface.roundedRect(node.x + 2, node.y + 2, node.width - 4, node.height - 4, 8);
        this.surface.ctx.fill();
        this.surface.ctx.restore();
      }
      this.surface.ctx.stroke();

      const iconTileX = node.x + 18;
      const iconTileY = node.y + 16;
      const iconTileSize = 30;
      this.surface.ctx.fillStyle = statusTheme?.badgeFill ?? CanvasTheme.surfaceAccent;
      this.surface.roundedRect(iconTileX, iconTileY, iconTileSize, iconTileSize, 8);
      this.surface.ctx.fill();
      this.surface.ctx.strokeStyle = statusTheme?.border ?? 'rgba(151, 166, 204, 0.30)';
      this.surface.ctx.lineWidth = 1;
      this.surface.roundedRect(iconTileX + 0.5, iconTileY + 0.5, iconTileSize - 1, iconTileSize - 1, 7.5);
      this.surface.ctx.stroke();

      this.drawTaskPluginIcon(iconTileX, iconTileY, iconTileSize, iconTileSize, statusTheme?.accent ?? CanvasTheme.accent);

      const titleX = node.x + 66;
      const titleMaxWidth = titleAccessoryRect
        ? Math.max(24, titleAccessoryRect.x - titleX - 10)
        : layout.titleMaxWidth;
      this.surface.ctx.fillStyle = CanvasTheme.nodeText;
      this.surface.setFont(700, 15);
      this.surface.drawEllipsizedText(node.name, titleX, layout.titleBaseline, titleMaxWidth);

      if (issueSummary && titleAccessoryRect) {
        this.drawNodeIssueIcon(titleAccessoryRect, issueSummary);
      }

      this.surface.ctx.fillStyle = CanvasTheme.nodeTextMuted;
      this.surface.setFont(500, 11);
      this.surface.drawEllipsizedText(node.resolvedVersion ?? '0.0.1', titleX, layout.versionBaseline, titleMaxWidth);

      if (readOnly && statusTheme && node.renderStatus) {
        this.drawNodeStatusIcon(node, node.renderStatus, statusTheme.badgeFill, statusTheme.text, statusTheme.accent);
      }

      if (!readOnly && node.hasDesignTimeOverrides) {
        this.drawNodeOverrideBadge(node, `${node.designTimeOverrideCount} override${node.designTimeOverrideCount === 1 ? '' : 's'}`);
      }

      if (layout.inputSection) {
        this.drawChannelSection(
          node,
          layout.inputSection,
          request.hoveredNodeId === node.id,
          request.hoveredPortKey,
          connectedSummaryPortKeys,
          connectedPropertyPortKeys,
          request.drag?.from === node.id ? request.drag.portKey ?? null : null,
          focusedPortKeys,
          validationNodeFocused ? validationFocus : null,
          validationPortKeys,
          issueSummary,
          passiveSummaryPorts,
        );
      }
      layout.outputSections.forEach((section) => {
        this.drawChannelSection(
          node,
          section,
          request.hoveredNodeId === node.id,
          request.hoveredPortKey,
          connectedSummaryPortKeys,
          connectedPropertyPortKeys,
          request.drag?.from === node.id ? request.drag.portKey ?? null : null,
          focusedPortKeys,
          validationNodeFocused ? validationFocus : null,
          validationPortKeys,
          issueSummary,
          passiveSummaryPorts,
        );
      });

      this.surface.ctx.restore();
    }
  }

  private drawDataBridgeNode(
    node: RenderedNode,
    connectedSummaryPortKeys: ReadonlySet<string>,
    connectedPropertyPortKeys: ReadonlySet<string>,
    drag: CanvasNodeRenderDrag | null,
    hovered: number | null,
    hoveredPortKey: string | null,
    isSelected: boolean,
    focusedPortKeys: ReadonlySet<string>,
    validationFocus: CanvasNodeValidationFocus | null,
    validationPortKeys: ReadonlySet<string>,
    passiveSummaryPorts: boolean,
  ): void {
    const layout = node.layout;
    this.surface.ctx.save();
    this.surface.ctx.shadowColor = CanvasTheme.nodeShadow;
    this.surface.ctx.shadowBlur = 10;
    this.surface.ctx.shadowOffsetY = 5;

    const gradient = this.surface.ctx.createLinearGradient(node.x, node.y, node.x, node.y + node.height);
    gradient.addColorStop(0, '#ecfeff');
    gradient.addColorStop(1, '#eff6ff');
    this.surface.ctx.fillStyle = gradient;
    this.surface.ctx.strokeStyle = isSelected
      ? CanvasTheme.connection.selected
      : 'rgba(14, 116, 144, 0.42)';
    this.surface.ctx.lineWidth = isSelected ? 2.1 : 1.2;
    this.surface.roundedRect(node.x, node.y, node.width, node.height, 12);
    this.surface.ctx.fill();
    this.surface.ctx.shadowColor = 'transparent';
    this.surface.ctx.stroke();

    this.surface.ctx.fillStyle = '#0e7490';
    this.surface.roundedRect(node.x + 16, node.y + 14, 32, 32, 10);
    this.surface.ctx.fill();
    this.surface.ctx.strokeStyle = 'rgba(14, 116, 144, 0.28)';
    this.surface.roundedRect(node.x + 16.5, node.y + 14.5, 31, 31, 9.5);
    this.surface.ctx.stroke();
    this.drawDataBridgeIcon(node.x + 22, node.y + 20, 20);

    this.surface.ctx.fillStyle = CanvasTheme.nodeText;
    this.surface.setFont(700, 15);
    this.surface.drawEllipsizedText('Data Bridge', node.x + 62, layout.titleBaseline, node.width - 88);
    this.surface.ctx.fillStyle = CanvasTheme.nodeTextMuted;
    this.surface.setFont(600, 10.5);
    this.surface.drawEllipsizedText(node.name.replace(/^Data Bridge\s*/i, '') || 'Phase data contract', node.x + 62, layout.versionBaseline, node.width - 88);

    if (layout.inputSection) {
      this.drawChannelSection(node, layout.inputSection, hovered === node.id, hoveredPortKey, connectedSummaryPortKeys, connectedPropertyPortKeys, drag?.from === node.id ? drag.portKey ?? null : null, focusedPortKeys, validationFocus, validationPortKeys, null, passiveSummaryPorts);
    }

    for (const section of layout.outputSections) {
      this.drawChannelSection(node, section, hovered === node.id, hoveredPortKey, connectedSummaryPortKeys, connectedPropertyPortKeys, drag?.from === node.id ? drag.portKey ?? null : null, focusedPortKeys, validationFocus, validationPortKeys, null, passiveSummaryPorts);
    }

    this.surface.ctx.restore();
  }

  private drawDataBridgeIcon(x: number, y: number, size: number): void {
    this.surface.ctx.save();
    this.surface.ctx.strokeStyle = '#ecfeff';
    this.surface.ctx.lineWidth = 2.4;
    this.surface.ctx.lineCap = 'round';
    this.surface.ctx.lineJoin = 'round';
    const midY = y + size / 2;
    this.surface.ctx.beginPath();
    this.surface.ctx.moveTo(x, y + 4);
    this.surface.ctx.lineTo(x + size * 0.34, y + 4);
    this.surface.ctx.quadraticCurveTo(x + size * 0.5, y + 4, x + size * 0.5, midY);
    this.surface.ctx.quadraticCurveTo(x + size * 0.5, y + size - 4, x + size * 0.66, y + size - 4);
    this.surface.ctx.lineTo(x + size, y + size - 4);
    this.surface.ctx.stroke();
    this.surface.ctx.beginPath();
    this.surface.ctx.moveTo(x, y + size - 4);
    this.surface.ctx.lineTo(x + size * 0.34, y + size - 4);
    this.surface.ctx.quadraticCurveTo(x + size * 0.5, y + size - 4, x + size * 0.5, midY);
    this.surface.ctx.quadraticCurveTo(x + size * 0.5, y + 4, x + size * 0.66, y + 4);
    this.surface.ctx.lineTo(x + size, y + 4);
    this.surface.ctx.stroke();
    this.surface.ctx.restore();
  }

  private drawCollectionIteratorOperator(
    node: RenderedNode,
    portConnectivity: CanvasPortConnectivityIndex,
    drag: CanvasNodeRenderDrag | null,
    hovered: number | null,
    hoveredPortKey: string | null,
    isSelected: boolean,
    readOnly: boolean,
    detailTier: CanvasDetailTier,
    blockedTargetNodeIds: ReadonlySet<number>,
    focusedPortKeys: ReadonlySet<string>,
    validationFocus: CanvasNodeValidationFocus | null,
    validationPortKeys: ReadonlySet<string>,
    issueSummary: CanvasNodeIssueSummary | null,
    previewEquation: string | null,
    passiveSummaryPorts: boolean,
  ): void {
    const statusTheme = node.renderStatus ? CanvasTheme.status[node.renderStatus] : null;
    const validationTheme = validationFocus ? CanvasTheme.validation[validationFocus.severity] : null;
    const isHoveredDest = !!drag && node.id !== drag.from && hovered === node.id;
    const isBlockedDest = !!drag && blockedTargetNodeIds.has(node.id);
    const eligible = !!drag && node.id !== drag.from && !isBlockedDest;
    const isResolved = node.base.collectionIteratorResolved || previewEquation !== null;
    const equation = node.base.collectionIteratorResolved
      ? node.base.collectionIteratorEquation
      : previewEquation ?? node.base.collectionIteratorEquation;

    this.surface.ctx.save();
    this.surface.ctx.shadowColor = detailTier === 'full' ? CanvasTheme.nodeShadow : 'transparent';
    this.surface.ctx.shadowBlur = detailTier === 'full' ? 12 : 0;
    this.surface.ctx.shadowOffsetY = detailTier === 'full' ? 6 : 0;
    this.surface.ctx.fillStyle = detailTier === 'full'
      ? this.createCollectionIteratorGradient(node, isResolved)
      : CanvasTheme.surface;
    this.surface.ctx.strokeStyle = isSelected
      ? CanvasTheme.connection.selected
      : statusTheme?.border ?? (isResolved ? 'rgba(45, 75, 221, 0.28)' : CanvasTheme.nodeStroke);
    this.surface.ctx.lineJoin = 'round';
    this.surface.ctx.lineWidth = isSelected ? 2.25 : 1.15;

    if (validationTheme) {
      this.surface.ctx.strokeStyle = validationTheme.stroke;
      this.surface.ctx.lineWidth = 1.8;
    } else if (isHoveredDest && isBlockedDest) {
      this.surface.ctx.strokeStyle = 'rgba(217, 45, 32, 0.62)';
      this.surface.ctx.setLineDash([4, 4]);
    } else if (isHoveredDest) {
      this.surface.ctx.strokeStyle = CanvasTheme.nodeStrokeHover;
      this.surface.ctx.setLineDash([5, 4]);
    } else if (eligible) {
      this.surface.ctx.strokeStyle = CanvasTheme.eligibleDash;
      this.surface.ctx.globalAlpha = 0.58;
    }

    this.collectionIteratorOperatorPath(node.x, node.y, node.width, node.height);
    this.surface.ctx.fill();
    this.surface.ctx.shadowColor = 'transparent';
    this.surface.ctx.shadowBlur = 0;

    if (validationTheme) {
      this.surface.ctx.save();
      this.surface.ctx.fillStyle = validationTheme.surface;
      this.surface.ctx.globalAlpha = 0.22;
      this.collectionIteratorOperatorPath(node.x + 2, node.y + 2, node.width - 4, node.height - 4);
      this.surface.ctx.fill();
      this.surface.ctx.restore();
    }

    this.collectionIteratorOperatorPath(node.x, node.y, node.width, node.height);
    this.surface.ctx.stroke();
    this.surface.ctx.setLineDash([]);
    this.surface.ctx.globalAlpha = 1;

    const centerX = node.x + node.width / 2;
    const label = 'Collection Iterator';
    this.surface.setFont(700, 10);
    const labelWidth = this.surface.ctx.measureText(label).width;
    const iconSize = 18;
    const labelGroupWidth = iconSize + 7 + labelWidth;
    const labelGroupX = centerX - labelGroupWidth / 2;
    const labelY = node.y + 31;

    this.drawCollectionIteratorIcon(
      labelGroupX,
      labelY - iconSize + 4,
      iconSize,
      isResolved ? CanvasTheme.accent : CanvasTheme.nodeTextMuted,
    );

    this.surface.ctx.fillStyle = CanvasTheme.nodeTextMuted;
    this.surface.setFont(700, 10);
    this.surface.ctx.fillText(label, labelGroupX + iconSize + 7, labelY);

    this.surface.ctx.fillStyle = isResolved
      ? CanvasTheme.accent
      : CanvasTheme.nodeText;
    this.surface.setFont(700, 18);
    this.surface.ctx.textAlign = 'center';
    this.surface.ctx.textBaseline = 'alphabetic';
    this.surface.ctx.fillText(equation, centerX, node.y + 67);

    this.surface.ctx.fillStyle = isResolved
      ? 'rgba(45, 75, 221, 0.72)'
      : CanvasTheme.nodeTextMuted;
    this.surface.setFont(700, 8.5);
    this.surface.ctx.textAlign = 'left';
    this.surface.ctx.fillText('COLLECTION', node.x + 24, node.y + 91);
    this.surface.ctx.textAlign = 'right';
    this.surface.ctx.fillText('ITEM', node.x + node.width - 24, node.y + 91);
    this.surface.ctx.textAlign = 'start';

    if (issueSummary) {
      this.drawNodeIssueIcon(node.settingsRect(), issueSummary);
    }

    if (readOnly && statusTheme && node.renderStatus) {
      this.drawNodeStatusIcon(node, node.renderStatus, statusTheme.badgeFill, statusTheme.text, statusTheme.accent);
    }

    const draggingPortKey = drag?.from === node.id ? drag.portKey ?? null : null;
    const inputSection = node.layout.inputSection;
    const outputSection = node.layout.outputSections[0] ?? null;
    if (inputSection) {
      const inputSummaryKey = this.portFocusKey(node, inputSection.summaryPort);
      const inputValidationFocused = !passiveSummaryPorts && (validationPortKeys.has(inputSummaryKey) ||
        validationFocus?.sectionKey === inputSection.key);
      const inputFocused = !passiveSummaryPorts && (focusedPortKeys.has(inputSummaryKey) || inputValidationFocused);
      const inputHovered = !passiveSummaryPorts && ((hovered === node.id && hoveredPortKey === inputSection.summaryPort.key) ||
        inputFocused ||
        (isHoveredDest && !isBlockedDest));
      this.drawSummarySocket(
        inputSection,
        inputHovered,
        !passiveSummaryPorts && portConnectivity.summaryPortKeys.has(inputSummaryKey),
        inputValidationFocused ? validationFocus?.severity ?? null : null,
      );
    }

    if (outputSection) {
      const outputSummaryKey = this.portFocusKey(node, outputSection.summaryPort);
      const outputValidationFocused = !passiveSummaryPorts && (validationPortKeys.has(outputSummaryKey) ||
        validationFocus?.sectionKey === outputSection.key);
      const outputFocused = !passiveSummaryPorts && (focusedPortKeys.has(outputSummaryKey) || outputValidationFocused);
      const outputHovered = !passiveSummaryPorts && ((hovered === node.id && hoveredPortKey === outputSection.summaryPort.key) ||
        outputFocused ||
        draggingPortKey === outputSection.summaryPort.key);
      this.drawSummarySocket(
        outputSection,
        outputHovered,
        !passiveSummaryPorts &&
          (portConnectivity.summaryPortKeys.has(outputSummaryKey) ||
            draggingPortKey === outputSection.summaryPort.key),
        outputValidationFocused ? validationFocus?.severity ?? null : null,
      );
    }

    this.surface.ctx.restore();
  }

  private drawChannelSection(
    node: RenderedNode,
    section: NodeChannelSectionLayout,
    isHoveredNode: boolean,
    hoveredPortKey: string | null,
    connectedSummaryPortKeys: ReadonlySet<string>,
    connectedPropertyPortKeys: ReadonlySet<string>,
    draggingPortKey: string | null,
    focusedPortKeys: ReadonlySet<string>,
    validationFocus: CanvasNodeValidationFocus | null,
    validationPortKeys: ReadonlySet<string>,
    issueSummary: CanvasNodeIssueSummary | null,
    passiveSummaryPorts: boolean,
  ): void {
    this.surface.ctx.save();

    const summaryPort = section.summaryPort;
    const summaryScopedKey = this.portFocusKey(node, summaryPort);
    const validationTheme = validationFocus ? CanvasTheme.validation[validationFocus.severity] : null;
    const validationSectionFocused = validationFocus?.sectionKey === section.key;
    const summaryValidationFocused = !passiveSummaryPorts &&
      (validationSectionFocused || validationPortKeys.has(summaryScopedKey));
    const summaryFocused = !passiveSummaryPorts && (focusedPortKeys.has(summaryScopedKey) || summaryValidationFocused);
    const summaryHover = !passiveSummaryPorts && ((isHoveredNode && hoveredPortKey === summaryPort.key) || summaryFocused);
    const summaryConnected = !passiveSummaryPorts && connectedSummaryPortKeys.has(summaryScopedKey);
    const summaryActive = summaryHover || (!passiveSummaryPorts && draggingPortKey === summaryPort.key);

    const sectionX = node.x + 16;
    const sectionWidth = node.width - 32;
    const sectionHeight = Math.max(section.headerHeight, section.bottom - section.headerTop);
    const sectionRadius = 7;
    const rowCenterY = section.headerTop + section.headerHeight / 2;

    this.surface.ctx.fillStyle = CanvasTheme.sectionSurfaceAlt;
    this.surface.roundedRect(sectionX, section.headerTop, sectionWidth, sectionHeight, sectionRadius);
    this.surface.ctx.fill();

    const headerFill = summaryValidationFocused && validationTheme
      ? validationTheme.surface
      : summaryActive
        ? CanvasTheme.sectionSurfaceActive
        : CanvasTheme.sectionSurface;
    this.surface.ctx.fillStyle = headerFill;
    this.surface.roundedRect(sectionX, section.headerTop, sectionWidth, section.headerHeight, sectionRadius);
    this.surface.ctx.fill();

    this.surface.ctx.strokeStyle = summaryValidationFocused && validationTheme
      ? validationTheme.stroke
      : summaryActive
        ? CanvasTheme.sectionBorderActive
        : CanvasTheme.sectionBorder;
    this.surface.ctx.lineWidth = summaryValidationFocused ? 1.7 : summaryActive ? 1.2 : 1;
    this.surface.roundedRect(sectionX, section.headerTop, sectionWidth, sectionHeight, sectionRadius);
    this.surface.ctx.stroke();

    const countLabel = this.compactPropertyCountLabel(section.countLabel).toUpperCase();
    this.surface.setFont(700, 8.5);
    const countWidth = this.surface.ctx.measureText(countLabel).width;
    const countChipPaddingX = 6;
    const countChipHeight = 16;
    const countChipWidth = countWidth + countChipPaddingX * 2;
    const countChipX = section.toggleRect.x - 8 - countChipWidth;
    const countChipY = rowCenterY - countChipHeight / 2;

    this.surface.ctx.fillStyle = summaryConnected || summaryActive || summaryValidationFocused
      ? 'rgba(45, 75, 221, 0.09)'
      : 'rgba(113, 131, 166, 0.08)';
    this.surface.roundedRect(countChipX, countChipY, countChipWidth, countChipHeight, 5);
    this.surface.ctx.fill();

    this.surface.ctx.strokeStyle = summaryConnected || summaryActive || summaryValidationFocused
      ? 'rgba(45, 75, 221, 0.22)'
      : 'rgba(151, 166, 204, 0.24)';
    this.surface.ctx.lineWidth = 1;
    this.surface.roundedRect(countChipX + 0.5, countChipY + 0.5, countChipWidth - 1, countChipHeight - 1, 4.5);
    this.surface.ctx.stroke();

    this.surface.ctx.fillStyle = summaryConnected || summaryActive || summaryValidationFocused
      ? CanvasTheme.accent
      : CanvasTheme.nodeTextMuted;
    this.surface.ctx.fillText(countLabel, countChipX + countChipPaddingX, rowCenterY + 3);

    this.surface.ctx.fillStyle = summaryActive || summaryValidationFocused
      ? CanvasTheme.nodeText
      : CanvasTheme.nodeSectionText;
    this.surface.setFont(700, 10);
    const sectionLabelX = node.x + 34;
    this.surface.drawEllipsizedText(
      section.label.toUpperCase(),
      sectionLabelX,
      rowCenterY + 4,
      Math.max(48, countChipX - sectionLabelX - 12),
    );
    this.drawSectionToggle(section);

    if (section.propertyRows.length > 0) {
      this.surface.ctx.strokeStyle = CanvasTheme.sectionDivider;
      this.surface.ctx.lineWidth = 1;
      this.surface.ctx.beginPath();
      this.surface.ctx.moveTo(sectionX + 10, section.headerTop + section.headerHeight);
      this.surface.ctx.lineTo(sectionX + sectionWidth - 10, section.headerTop + section.headerHeight);
      this.surface.ctx.stroke();

      for (const row of section.propertyRows) {
        const rowScopedKey = this.portFocusKey(node, row.port);
        const rowValidationFocused = validationPortKeys.has(rowScopedKey);
        const isFocusedEndpoint = focusedPortKeys.has(rowScopedKey);
        const isHover = (isHoveredNode && hoveredPortKey === row.port.key) || isFocusedEndpoint || rowValidationFocused;
        const isConnected = connectedPropertyPortKeys.has(rowScopedKey);
        const propertyIssue = issueSummary?.propertyIssues.get(this.propertyIssueKey(row.port)) ?? null;

        this.surface.ctx.fillStyle = rowValidationFocused && validationTheme
          ? validationTheme.fill
          : isHover || isFocusedEndpoint
            ? CanvasTheme.sectionRowActive
            : CanvasTheme.sectionRowSurface;
        this.surface.fillRect(sectionX + 1, row.top, sectionWidth - 2, row.height);

        if (row !== section.propertyRows[0]) {
          this.surface.ctx.strokeStyle = CanvasTheme.sectionDivider;
          this.surface.ctx.lineWidth = 1;
          this.surface.ctx.beginPath();
          this.surface.ctx.moveTo(sectionX + 12, row.top);
          this.surface.ctx.lineTo(sectionX + sectionWidth - 12, row.top);
          this.surface.ctx.stroke();
        }

        const propertyType = row.displayKind.toUpperCase();
        this.surface.setFont(600, 8.5);
        const propertyTypeWidth = this.surface.ctx.measureText(propertyType).width;
        const propertyTypeX = node.x + node.width - 36 - propertyTypeWidth;
        const propertyIssueSpace = propertyIssue ? 14 : 0;
        const propertyIssueX = propertyTypeX - 9;
        const labelX = node.x + 34;
        const labelMaxWidth = Math.max(40, propertyTypeX - labelX - propertyIssueSpace - (row.required ? 18 : 8));

        this.surface.ctx.fillStyle = CanvasTheme.nodeText;
        this.surface.setFont(600, 11.5);
        const displayedLabel = this.surface.drawEllipsizedText(row.label, labelX, row.centerY + 4, labelMaxWidth);

        if (row.required) {
          this.surface.ctx.fillStyle = CanvasTheme.accent;
          this.surface.setFont(700, 11.5);
          const labelWidth = Math.min(this.surface.ctx.measureText(displayedLabel).width, labelMaxWidth);
          this.surface.ctx.fillText('*', labelX + labelWidth + 4, row.centerY + 4);
        }

        this.surface.ctx.fillStyle = CanvasTheme.nodePropertyType;
        this.surface.setFont(600, 8.5);
        this.surface.ctx.fillText(propertyType, propertyTypeX, row.centerY + 3);

        if (propertyIssue) {
          this.drawInlineIssueIcon(propertyIssueX, row.centerY, propertyIssue.severity);
        }

        const shouldShow = section.expanded ||
          isConnected ||
          isHoveredNode ||
          draggingPortKey === row.port.key ||
          isFocusedEndpoint ||
          rowValidationFocused;
        if (shouldShow) {
          this.drawPropertySocket(
            row.port,
            isHover || draggingPortKey === row.port.key,
            isConnected || draggingPortKey === row.port.key,
            draggingPortKey === row.port.key,
            rowValidationFocused ? validationFocus?.severity ?? null : null,
          );
        }
      }
    }

    this.drawSummarySocket(
      section,
      summaryActive,
      summaryConnected || (!passiveSummaryPorts && draggingPortKey === summaryPort.key),
      summaryValidationFocused ? validationFocus?.severity ?? null : null,
    );

    this.surface.ctx.restore();
  }

  private drawSectionToggle(section: NodeChannelSectionLayout): void {
    const rect = section.toggleRect;
    const centerX = rect.x + rect.width / 2;
    const centerY = rect.y + rect.height / 2;

    this.surface.ctx.save();
    this.surface.ctx.fillStyle = section.expanded ? 'rgba(45, 75, 221, 0.08)' : 'rgba(16, 23, 40, 0.02)';
    this.surface.roundedRect(rect.x, rect.y, rect.width, rect.height, 6);
    this.surface.ctx.fill();

    this.surface.ctx.strokeStyle = section.expanded ? 'rgba(45, 75, 221, 0.62)' : 'rgba(115, 132, 170, 0.34)';
    this.surface.ctx.lineWidth = 1.2;
    this.surface.roundedRect(rect.x + 0.5, rect.y + 0.5, rect.width - 1, rect.height - 1, 5.5);
    this.surface.ctx.stroke();

    this.surface.ctx.strokeStyle = section.expanded ? CanvasTheme.accent : 'rgba(88, 106, 146, 0.58)';
    this.surface.ctx.lineWidth = 1.45;
    this.surface.ctx.lineCap = 'round';
    this.surface.ctx.beginPath();
    this.surface.ctx.moveTo(centerX - 4.5, centerY);
    this.surface.ctx.lineTo(centerX + 4.5, centerY);
    this.surface.ctx.stroke();

    if (!section.expanded) {
      this.surface.ctx.beginPath();
      this.surface.ctx.moveTo(centerX, centerY - 4.5);
      this.surface.ctx.lineTo(centerX, centerY + 4.5);
      this.surface.ctx.stroke();
    }

    this.surface.ctx.restore();
  }

  private portFocusKey(node: RenderedNode, port: NodePortDefinition): string {
    return `${node.id}:${port.key}`;
  }

  private compactPropertyCountLabel(label: string): string {
    return label.replace(' properties', ' props').replace(' property', ' prop');
  }

  private createNodeGradient(node: RenderedNode): CanvasGradient {
    const gradient = this.surface.ctx.createLinearGradient(node.x, node.y, node.x + node.width, node.y + node.height);
    gradient.addColorStop(0, CanvasTheme.surface);
    gradient.addColorStop(1, CanvasTheme.surfaceAlt);
    return gradient;
  }

  private createCollectionIteratorGradient(node: RenderedNode, resolved: boolean): CanvasGradient {
    const gradient = this.surface.ctx.createLinearGradient(node.x, node.y, node.x + node.width, node.y + node.height);
    if (resolved) {
      gradient.addColorStop(0, '#fbfdff');
      gradient.addColorStop(1, '#eef5ff');
    } else {
      gradient.addColorStop(0, CanvasTheme.surface);
      gradient.addColorStop(1, CanvasTheme.surfaceAlt);
    }

    return gradient;
  }

  private collectionIteratorOperatorPath(x: number, y: number, width: number, height: number): void {
    const chamfer = Math.min(22, width * 0.11, height * 0.32);
    const centerY = y + height / 2;

    this.surface.ctx.beginPath();
    this.surface.ctx.moveTo(x + chamfer, y);
    this.surface.ctx.lineTo(x + width - chamfer, y);
    this.surface.ctx.lineTo(x + width, centerY);
    this.surface.ctx.lineTo(x + width - chamfer, y + height);
    this.surface.ctx.lineTo(x + chamfer, y + height);
    this.surface.ctx.lineTo(x, centerY);
    this.surface.ctx.closePath();
  }

  private drawTaskPluginIcon(x: number, y: number, width: number, height: number, color: string): void {
    const centerX = x + width / 2;
    const centerY = y + height / 2;

    this.surface.ctx.save();
    this.surface.ctx.fillStyle = color;

    if (this.surface.materialIconReady) {
      this.surface.ctx.font = `400 20px ${this.surface.materialIconFontFamily}`;
      this.surface.ctx.textAlign = 'center';
      this.surface.ctx.textBaseline = 'middle';
      this.surface.ctx.fillText('extension', centerX, centerY + 0.5);
    } else {
      this.drawFallbackPuzzleIcon(centerX, centerY, color);
    }

    this.surface.ctx.restore();
  }

  private drawCollectionIteratorIcon(x: number, y: number, size: number, color: string): void {
    const centerX = x + size / 2;
    const centerY = y + size / 2;

    this.surface.ctx.save();
    this.surface.ctx.fillStyle = color;
    this.surface.ctx.strokeStyle = color;

    if (this.surface.materialIconReady) {
      this.surface.ctx.font = `400 ${Math.round(size + 2)}px ${this.surface.materialIconFontFamily}`;
      this.surface.ctx.textAlign = 'center';
      this.surface.ctx.textBaseline = 'middle';
      this.surface.ctx.fillText('call_split', centerX, centerY + 0.5);
      this.surface.ctx.restore();
      return;
    }

    this.surface.ctx.lineWidth = 1.7;
    this.surface.ctx.lineCap = 'round';
    this.surface.ctx.lineJoin = 'round';
    this.surface.ctx.beginPath();
    this.surface.ctx.moveTo(x + 2.5, centerY);
    this.surface.ctx.lineTo(centerX - 1, centerY);
    this.surface.ctx.lineTo(x + size - 3, y + 4);
    this.surface.ctx.moveTo(centerX - 1, centerY);
    this.surface.ctx.lineTo(x + size - 3, y + size - 4);
    this.surface.ctx.stroke();

    this.surface.ctx.lineWidth = 1.2;
    this.surface.ctx.beginPath();
    this.surface.ctx.moveTo(x + 1.5, y + 3);
    this.surface.ctx.lineTo(x + 1.5, y + size - 3);
    this.surface.ctx.moveTo(x + size - 1.5, y + 3);
    this.surface.ctx.lineTo(x + size - 1.5, y + size - 3);
    this.surface.ctx.stroke();
    this.surface.ctx.restore();
  }

  private drawFallbackPuzzleIcon(centerX: number, centerY: number, color: string): void {
    const size = 15;
    const x = centerX - size / 2;
    const y = centerY - size / 2;

    this.surface.ctx.save();
    this.surface.ctx.fillStyle = color;
    this.surface.roundedRect(x, y + 2, size, size - 2, 3);
    this.surface.ctx.fill();

    this.surface.ctx.globalCompositeOperation = 'destination-out';
    this.surface.ctx.beginPath();
    this.surface.ctx.arc(centerX, y + 2, 2.6, 0, Math.PI * 2);
    this.surface.ctx.arc(x + size, centerY + 1, 2.6, 0, Math.PI * 2);
    this.surface.ctx.fill();
    this.surface.ctx.restore();
  }

  private drawSummarySocket(
    section: NodeChannelSectionLayout,
    isHover: boolean,
    isConnected: boolean,
    validationSeverity: CanvasValidationSeverity | null = null,
  ): void {
    const port = section.summaryPort;
    const validationTheme = validationSeverity ? CanvasTheme.validation[validationSeverity] : null;
    const radius = port.radius;
    const haloRadius = radius + 6;

    this.surface.ctx.save();

    if (isHover || isConnected || validationTheme) {
      this.surface.ctx.fillStyle = validationTheme?.surface ?? 'rgba(31, 73, 255, 0.11)';
      this.surface.ctx.beginPath();
      this.surface.ctx.arc(port.x, port.y, haloRadius, 0, Math.PI * 2);
      this.surface.ctx.fill();
    }

    this.surface.ctx.shadowColor = isHover || isConnected
      ? validationTheme?.glow ?? 'rgba(45, 75, 221, 0.18)'
      : CanvasTheme.anchor.socketShadow;
    this.surface.ctx.shadowBlur = isHover || isConnected ? 8 : 3;
    this.surface.ctx.shadowOffsetY = 1.5;

    this.surface.ctx.fillStyle = isHover || isConnected
      ? validationTheme?.fill ?? CanvasTheme.anchor.socketFocusFill
      : CanvasTheme.anchor.socketFill;
    this.surface.ctx.beginPath();
    this.surface.ctx.arc(port.x, port.y, radius, 0, Math.PI * 2);
    this.surface.ctx.fill();

    this.surface.ctx.shadowColor = 'transparent';
    this.surface.ctx.shadowBlur = 0;
    this.surface.ctx.shadowOffsetY = 0;

    this.surface.ctx.strokeStyle = isHover || isConnected
      ? validationTheme?.stroke ?? CanvasTheme.anchor.connected
      : CanvasTheme.anchor.socketOutline;
    this.surface.ctx.lineWidth = isHover || isConnected ? 1.9 : 1.25;
    this.surface.ctx.beginPath();
    this.surface.ctx.arc(port.x, port.y, radius, 0, Math.PI * 2);
    this.surface.ctx.stroke();

    if (isConnected) {
      this.surface.ctx.fillStyle = validationTheme?.stroke ?? CanvasTheme.anchor.connected;
      this.surface.ctx.beginPath();
      this.surface.ctx.arc(port.x, port.y, 2.45, 0, Math.PI * 2);
      this.surface.ctx.fill();
    }

    this.surface.ctx.restore();
  }

  private drawPropertySocket(
    port: NodePortDefinition,
    isHover: boolean,
    isConnected: boolean,
    isGrabbed: boolean,
    validationSeverity: CanvasValidationSeverity | null = null,
  ): void {
    const x = port.x;
    const y = port.y;
    const validationTheme = validationSeverity ? CanvasTheme.validation[validationSeverity] : null;
    const radius = port.radius;
    const activeRadius = radius + 5.5;

    this.surface.ctx.save();

    if (isHover || isGrabbed || validationTheme) {
      this.surface.ctx.beginPath();
      this.surface.ctx.fillStyle = isGrabbed
        ? CanvasTheme.anchor.connected
        : validationTheme?.surface ?? 'rgba(31, 73, 255, 0.13)';
      this.surface.ctx.arc(x, y, activeRadius, 0, 2 * Math.PI);
      this.surface.ctx.fill();
    }

    this.surface.ctx.fillStyle = isHover || isGrabbed || isConnected
      ? validationTheme?.fill ?? CanvasTheme.anchor.socketFocusFill
      : CanvasTheme.anchor.socketFill;
    this.surface.ctx.beginPath();
    this.surface.ctx.arc(x, y, radius, 0, 2 * Math.PI);
    this.surface.ctx.fill();

    this.surface.ctx.strokeStyle = isHover || isGrabbed || isConnected
      ? validationTheme?.stroke ?? CanvasTheme.anchor.connected
      : CanvasTheme.anchor.socketOutlineMuted;
    this.surface.ctx.lineWidth = isHover || isGrabbed || isConnected ? 1.65 : 1.1;
    this.surface.ctx.beginPath();
    this.surface.ctx.arc(x, y, radius, 0, 2 * Math.PI);
    this.surface.ctx.stroke();

    if (isConnected || isGrabbed) {
      this.surface.ctx.fillStyle = validationTheme?.stroke ?? CanvasTheme.anchor.connected;
      this.surface.ctx.beginPath();
      this.surface.ctx.arc(x, y, isGrabbed ? 2.4 : 1.9, 0, 2 * Math.PI);
      this.surface.ctx.fill();
    }

    this.surface.ctx.restore();
  }

  private drawNodeIssueIcon(rect: { x: number; y: number; width: number; height: number }, issue: CanvasNodeIssueSummary): void {
    const theme = CanvasTheme.validation[issue.severity];
    const centerX = rect.x + rect.width / 2;
    const centerY = rect.y + rect.height / 2;

    this.surface.ctx.save();
    this.surface.ctx.fillStyle = issue.severity === 'error'
      ? 'rgba(255, 232, 229, 0.82)'
      : 'rgba(255, 247, 220, 0.86)';
    this.surface.roundedRect(rect.x, rect.y, rect.width, rect.height, 6);
    this.surface.ctx.fill();

    this.surface.ctx.strokeStyle = theme.stroke;
    this.surface.ctx.globalAlpha = 0.28;
    this.surface.ctx.lineWidth = 1.2;
    this.surface.roundedRect(rect.x + 0.6, rect.y + 0.6, rect.width - 1.2, rect.height - 1.2, 5.5);
    this.surface.ctx.stroke();

    this.surface.ctx.globalAlpha = 1;
    this.surface.ctx.fillStyle = theme.stroke;
    if (this.surface.materialIconReady) {
      this.surface.ctx.font = `400 17px ${this.surface.materialIconFontFamily}`;
      this.surface.ctx.textAlign = 'center';
      this.surface.ctx.textBaseline = 'middle';
      this.surface.ctx.fillText(issue.severity === 'error' ? 'error' : 'warning', centerX, centerY + 0.5);
    } else {
      this.surface.setFont(700, 13);
      this.surface.ctx.textAlign = 'center';
      this.surface.ctx.textBaseline = 'middle';
      this.surface.ctx.fillText(issue.severity === 'error' ? '!' : '!', centerX, centerY + 0.5);
    }

    this.surface.ctx.restore();
  }

  private drawInlineIssueIcon(centerX: number, centerY: number, severity: CanvasValidationSeverity): void {
    const theme = CanvasTheme.validation[severity];

    this.surface.ctx.save();
    this.surface.ctx.fillStyle = theme.stroke;
    if (this.surface.materialIconReady) {
      this.surface.ctx.font = `400 11px ${this.surface.materialIconFontFamily}`;
      this.surface.ctx.textAlign = 'center';
      this.surface.ctx.textBaseline = 'middle';
      this.surface.ctx.fillText(severity === 'error' ? 'error' : 'warning', centerX, centerY + 0.5);
    } else {
      this.surface.setFont(700, 9);
      this.surface.ctx.textAlign = 'center';
      this.surface.ctx.textBaseline = 'middle';
      this.surface.ctx.fillText('!', centerX, centerY + 0.5);
    }
    this.surface.ctx.restore();
  }

  private propertyIssueKey(port: NodePortDefinition): string {
    return `${port.side}:${port.channelName ?? ''}:${port.propertyName ?? ''}`;
  }

  private drawNodeStatusIcon(
    node: RenderedNode,
    status: NonNullable<NodeModel['renderStatus']>,
    fill: string,
    text: string,
    accent: string,
  ): void {
    const rect = node.settingsRect();
    const centerX = rect.x + rect.width / 2;
    const centerY = rect.y + rect.height / 2;

    this.surface.ctx.save();

    this.surface.ctx.fillStyle = fill;
    this.surface.roundedRect(rect.x, rect.y, rect.width, rect.height, 6);
    this.surface.ctx.fill();

    this.surface.ctx.strokeStyle = accent;
    this.surface.ctx.globalAlpha = 0.34;
    this.surface.ctx.lineWidth = 1.4;
    this.surface.roundedRect(rect.x + 0.7, rect.y + 0.7, rect.width - 1.4, rect.height - 1.4, 5.5);
    this.surface.ctx.stroke();

    this.surface.ctx.globalAlpha = 1;
    this.surface.ctx.strokeStyle = text;
    this.surface.ctx.fillStyle = text;
    this.surface.ctx.lineWidth = 2.2;
    this.surface.ctx.lineCap = 'round';
    this.surface.ctx.lineJoin = 'round';

    switch (status) {
      case 'Completed':
        this.surface.ctx.beginPath();
        this.surface.ctx.moveTo(centerX - 6, centerY + 0.5);
        this.surface.ctx.lineTo(centerX - 2.2, centerY + 4.6);
        this.surface.ctx.lineTo(centerX + 7, centerY - 5);
        this.surface.ctx.stroke();
        break;
      case 'Failed':
        this.surface.ctx.beginPath();
        this.surface.ctx.moveTo(centerX - 5.5, centerY - 5.5);
        this.surface.ctx.lineTo(centerX + 5.5, centerY + 5.5);
        this.surface.ctx.moveTo(centerX + 5.5, centerY - 5.5);
        this.surface.ctx.lineTo(centerX - 5.5, centerY + 5.5);
        this.surface.ctx.stroke();
        break;
      case 'Skipped':
        this.surface.ctx.beginPath();
        this.surface.ctx.moveTo(centerX - 5, centerY - 5);
        this.surface.ctx.lineTo(centerX + 1, centerY);
        this.surface.ctx.lineTo(centerX - 5, centerY + 5);
        this.surface.ctx.moveTo(centerX + 5, centerY - 5);
        this.surface.ctx.lineTo(centerX + 5, centerY + 5);
        this.surface.ctx.stroke();
        break;
      case 'InProgress':
        this.surface.ctx.beginPath();
        this.surface.ctx.arc(centerX, centerY, 6.4, -Math.PI * 0.88, Math.PI * 0.62);
        this.surface.ctx.stroke();
        this.surface.ctx.beginPath();
        this.surface.ctx.moveTo(centerX + 5.7, centerY - 4.8);
        this.surface.ctx.lineTo(centerX + 8.2, centerY - 1.4);
        this.surface.ctx.lineTo(centerX + 4.1, centerY - 1.1);
        this.surface.ctx.fill();
        break;
      case 'Pending':
        this.surface.ctx.beginPath();
        this.surface.ctx.arc(centerX, centerY, 6.4, 0, Math.PI * 2);
        this.surface.ctx.stroke();
        this.surface.ctx.beginPath();
        this.surface.ctx.moveTo(centerX, centerY - 3.8);
        this.surface.ctx.lineTo(centerX, centerY + 0.6);
        this.surface.ctx.lineTo(centerX + 3.4, centerY + 3.2);
        this.surface.ctx.stroke();
        break;
      default:
        this.surface.ctx.beginPath();
        this.surface.ctx.arc(centerX, centerY, 3.1, 0, Math.PI * 2);
        this.surface.ctx.fill();
        break;
    }

    this.surface.ctx.restore();
  }

  private drawNodeOverrideBadge(node: RenderedNode, label: string): void {
    this.surface.ctx.save();
    this.surface.setFont(700, 11);
    const paddingX = 10;
    const iconSize = 16;
    const width = this.surface.ctx.measureText(label).width + paddingX * 2 + iconSize + 8;
    const height = 24;
    const x = node.x + 24;
    const y = node.y + 74;

    this.surface.ctx.fillStyle = 'rgba(242, 140, 24, 0.12)';
    this.surface.roundedRect(x, y, width, height, 12);
    this.surface.ctx.fill();

    this.surface.ctx.fillStyle = '#c2410c';
    this.surface.ctx.beginPath();
    this.surface.ctx.arc(x + 12, y + height / 2, 5, 0, Math.PI * 2);
    this.surface.ctx.fill();

    this.surface.ctx.fillStyle = '#9a3412';
    this.surface.ctx.fillText(label, x + 12 + iconSize, y + 16);
    this.surface.ctx.restore();
  }
}

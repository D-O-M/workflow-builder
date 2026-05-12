import { subtractRouteProblemSegments } from './pathfinder';
import {
  canvasConnectionAlpha,
  canvasConnectionLineWidth,
  CANVAS_AMBIENT_CONNECTION_STROKE,
  CANVAS_PROBLEM_CONNECTION_SOFT_STROKE,
  CANVAS_PROBLEM_CONNECTION_STROKE,
  resolveCanvasConnectionPresentationState,
  shouldDrawCanvasConnectionEndpointCaps,
  shouldDrawCanvasConnectionHalo,
} from './canvas-connection-presentation';
import {
  CanvasConnectionPresentation,
  CanvasValidationSeverity,
  CONNECTION_FOCUSED_STROKE_WIDTH,
  CONNECTION_HALO_WIDTH,
  CONNECTION_STROKE_WIDTH,
} from './canvas-render-types';
import type { RenderedConnection } from './canvas-render-connection-model';
import {
  fillRouteEndpointCaps,
  strokeContinuousRoute,
  strokeSegments,
} from './canvas-render-geometry-utilities';
import {
  connectionShadeFill,
  connectionShadeStroke,
  DEFAULT_CONNECTION_SHADE,
  validationConnectionColor,
} from './canvas-render-lane-shade';

export function drawRenderedConnection(
  connection: RenderedConnection,
  wrapper: { ctx: CanvasRenderingContext2D },
  options?: {
    selected?: boolean;
    hovered?: boolean;
    dimmed?: boolean;
    presentation?: CanvasConnectionPresentation;
    hoveredRouteIndex?: number | null;
    validationSeverity?: CanvasValidationSeverity | null;
  },
): void {
  const selected = options?.selected ?? false;
  const hovered = options?.hovered ?? false;
  const dimmed = options?.dimmed ?? false;
  const presentation = options?.presentation ?? 'normal';
  const hoveredRouteIndex = options?.hoveredRouteIndex ?? null;
  const validationSeverity = options?.validationSeverity ?? null;
  const hasRouteFocus = hovered && hoveredRouteIndex !== null;
  const presentationState = resolveCanvasConnectionPresentationState({
    presentation,
    selected,
    hovered,
    validationSeverity,
    hasProblemRoutes: connection.routes.some((route) => route.status === 'obstacleViolation'),
  });

  wrapper.ctx.save();
  const validationStroke = validationSeverity ? validationConnectionColor(validationSeverity, true) : null;
  const validationSoft = validationSeverity ? validationConnectionColor(validationSeverity, false) : null;

  wrapper.ctx.strokeStyle = presentationState.hasProblemRoutes
    ? CANVAS_PROBLEM_CONNECTION_SOFT_STROKE
    : validationSoft ?? (presentationState.ambient
      ? CANVAS_AMBIENT_CONNECTION_STROKE
      : connectionShadeStroke(connection.routes[0]?.shade ?? DEFAULT_CONNECTION_SHADE, presentationState.focused));
  wrapper.ctx.fillStyle = presentationState.hasProblemRoutes
    ? CANVAS_PROBLEM_CONNECTION_STROKE
    : validationStroke ?? (presentationState.ambient
      ? CANVAS_AMBIENT_CONNECTION_STROKE
      : connectionShadeFill(connection.routes[0]?.shade ?? DEFAULT_CONNECTION_SHADE, presentationState.focused));
  wrapper.ctx.lineCap = presentationState.ambient ? 'round' : 'butt';
  wrapper.ctx.lineJoin = 'round';

  if (shouldDrawCanvasConnectionHalo(presentationState)) {
    wrapper.ctx.save();
    wrapper.ctx.globalAlpha = presentationState.hasProblemRoutes ? 0.13 : 0.12;
    const haloWidth = CONNECTION_HALO_WIDTH;
    wrapper.ctx.lineWidth = haloWidth;
    wrapper.ctx.setLineDash([]);
    for (const [routeIndex, route] of connection.routes.entries()) {
      if (hasRouteFocus && routeIndex !== hoveredRouteIndex) {
        continue;
      }

      wrapper.ctx.strokeStyle = route.status === 'obstacleViolation'
        ? CANVAS_PROBLEM_CONNECTION_STROKE
        : validationStroke
          ? validationStroke
          : connectionShadeStroke(route.shade, true);
      wrapper.ctx.fillStyle = wrapper.ctx.strokeStyle;
      strokeContinuousRoute(wrapper.ctx, route.visualSegments);
      fillRouteEndpointCaps(wrapper.ctx, route, haloWidth);
    }
    wrapper.ctx.restore();
  }

  for (const [routeIndex, route] of connection.routes.entries()) {
    if (route.visualSegments.length === 0) {
      continue;
    }

    const routeFocused = presentationState.focused && (!hasRouteFocus || routeIndex === hoveredRouteIndex);
    const routeDimmed = dimmed || (hasRouteFocus && routeIndex !== hoveredRouteIndex);
    const lineWidth = canvasConnectionLineWidth(
      presentationState,
      routeFocused,
      CONNECTION_FOCUSED_STROKE_WIDTH,
      CONNECTION_STROKE_WIDTH,
    );
    const routeStroke = route.status === 'obstacleViolation'
      ? (presentationState.ambient ? CANVAS_AMBIENT_CONNECTION_STROKE : CANVAS_PROBLEM_CONNECTION_SOFT_STROKE)
      : validationSoft ?? (presentationState.ambient
        ? CANVAS_AMBIENT_CONNECTION_STROKE
        : connectionShadeStroke(route.shade, routeFocused));

    wrapper.ctx.save();
    wrapper.ctx.lineWidth = lineWidth;
    wrapper.ctx.globalAlpha = canvasConnectionAlpha(
      presentationState,
      routeDimmed,
      routeFocused,
      route.status === 'obstacleViolation',
    );
    wrapper.ctx.strokeStyle = routeStroke;
    wrapper.ctx.fillStyle = routeStroke;

    if (!presentationState.ambient && route.visualProblemSegments.length > 0) {
      strokeSegments(wrapper.ctx, subtractRouteProblemSegments(route.visualSegments, route.visualProblemSegments));
      wrapper.ctx.save();
      wrapper.ctx.strokeStyle = CANVAS_PROBLEM_CONNECTION_STROKE;
      wrapper.ctx.lineWidth = routeFocused ? 10 : 6.8;
      wrapper.ctx.setLineDash([8, 6]);
      strokeSegments(wrapper.ctx, route.visualProblemSegments);
      wrapper.ctx.restore();
    } else {
      strokeContinuousRoute(wrapper.ctx, route.visualSegments);
    }

    if (shouldDrawCanvasConnectionEndpointCaps(presentationState)) {
      fillRouteEndpointCaps(wrapper.ctx, route, lineWidth);
    }
    wrapper.ctx.restore();
  }

  wrapper.ctx.restore();
}

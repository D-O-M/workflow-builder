import type { CanvasConnectionPresentation, CanvasValidationSeverity } from './canvas-render-model';

export const CANVAS_AMBIENT_CONNECTION_STROKE = 'rgba(100, 116, 139, 0.85)';
export const CANVAS_PROBLEM_CONNECTION_STROKE = '#d92d20';
export const CANVAS_PROBLEM_CONNECTION_SOFT_STROKE = 'rgba(217, 45, 32, 0.72)';

export interface CanvasConnectionPresentationState {
  readonly ambient: boolean;
  readonly validationFocused: boolean;
  readonly focused: boolean;
  readonly hasProblemRoutes: boolean;
}

export function resolveCanvasConnectionPresentationState(options: {
  readonly presentation: CanvasConnectionPresentation;
  readonly selected: boolean;
  readonly hovered: boolean;
  readonly validationSeverity: CanvasValidationSeverity | null;
  readonly hasProblemRoutes: boolean;
}): CanvasConnectionPresentationState {
  const ambient = options.presentation === 'ambient';
  const validationFocused = options.validationSeverity !== null;
  return {
    ambient,
    validationFocused,
    focused: ambient
      ? validationFocused
      : options.selected || options.hovered || validationFocused,
    hasProblemRoutes: ambient ? false : options.hasProblemRoutes,
  };
}

export function canvasConnectionLineWidth(
  state: Pick<CanvasConnectionPresentationState, 'ambient' | 'validationFocused'>,
  routeFocused: boolean,
  focusedWidth: number,
  defaultWidth: number,
): number {
  if (state.ambient) {
    return state.validationFocused ? 1.8 : 1.4;
  }

  return routeFocused ? focusedWidth : defaultWidth;
}

export function canvasConnectionAlpha(
  state: Pick<CanvasConnectionPresentationState, 'ambient' | 'validationFocused'>,
  routeDimmed: boolean,
  routeFocused: boolean,
  routeHasProblem: boolean,
): number {
  if (state.ambient) {
    return state.validationFocused ? 0.72 : routeDimmed ? 0.1 : 0.18;
  }

  if (routeDimmed) {
    return 0.13;
  }

  if (routeFocused) {
    return 1;
  }

  return routeHasProblem ? 0.9 : 0.42;
}

export function shouldDrawCanvasConnectionHalo(
  state: Pick<CanvasConnectionPresentationState, 'ambient' | 'focused'>,
): boolean {
  return state.focused && !state.ambient;
}

export function shouldDrawCanvasConnectionEndpointCaps(
  state: Pick<CanvasConnectionPresentationState, 'ambient'>,
): boolean {
  return !state.ambient;
}

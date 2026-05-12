import { ConnectionModel, ConnectionPropertyMappingModel } from './connection-model';
import { NodeModel, Point, TaskPropertyBaseType, TaskPropertyType } from './node-model';
import { ITaskDefinition, ITaskVersion } from '../interfaces/ITaskDefinition';

export interface BlueprintGraphDraft {
  readonly nodes: readonly NodeModel[];
  readonly connections: readonly ConnectionModel[];
}

export interface BlueprintNodePlacement {
  readonly coordinates: Point;
}

export interface BlueprintTaskNodeCreateRequest {
  readonly taskDefinition: ITaskDefinition;
  readonly placement: BlueprintNodePlacement;
}

export interface BlueprintCollectionIteratorCreateRequest {
  readonly placement: BlueprintNodePlacement;
  readonly itemKind?: TaskPropertyBaseType | null;
}

export interface BlueprintConnectionEndpoint {
  readonly nodeId: number;
}

export interface BlueprintConnectionSourceEndpoint extends BlueprintConnectionEndpoint {
  readonly anchor: number;
}

export interface BlueprintConnectionDestinationEndpoint extends BlueprintConnectionEndpoint {
  readonly side: ConnectionModel['side'];
  readonly position: number;
}

export interface BlueprintConnectionCreateRequest {
  readonly source: BlueprintConnectionSourceEndpoint;
  readonly destination: BlueprintConnectionDestinationEndpoint;
}

export interface BlueprintDataBridgeInputPropertyRequest {
  readonly bridgeNodeId: number;
  readonly property: {
    readonly name: string;
    readonly kind: TaskPropertyType;
  };
}

export interface BlueprintNodeVersionChangeRequest {
  readonly nodeId: number;
  readonly version: ITaskVersion;
}

export interface BlueprintNodeDesignTimeOverridesUpdate {
  readonly nodeId: number;
  readonly overrides: Record<string, unknown>;
}

export interface BlueprintConnectionMappingUpdate {
  readonly connectionId: string;
  readonly propertyMappings: readonly ConnectionPropertyMappingModel[];
}

export interface BlueprintMarqueeSelectionRequest {
  readonly bounds: {
    readonly left: number;
    readonly top: number;
    readonly right: number;
    readonly bottom: number;
  };
  readonly additive: boolean;
}

export interface BlueprintNodePositionUpdate {
  readonly nodeId: number;
  readonly coordinates: Point;
}

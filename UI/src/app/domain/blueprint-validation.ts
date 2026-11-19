import { conditionalRuleIssues } from './conditional-rule';
import { ConnectionModel, resolveEffectivePropertyMappings } from './connection-model';
import { NodeModel } from './node-model';

export type BlueprintValidationSeverity = 'error' | 'warning';

export type BlueprintValidationIssueCode =
  | 'blueprint.name.required'
  | 'blueprint.graph.empty'
  | 'connection.self'
  | 'connection.cycle'
  | 'connection.mapping.sourceMissing'
  | 'connection.mapping.targetMissing'
  | 'connection.mapping.typeMismatch'
  | 'node.conditionalBranch.invalid'
  | 'node.collectionIterator.start'
  | 'node.unreachable'
  | 'node.input.requiredMissing'
  | 'connection.route.obstructed'
  | 'node.output.propertyUnused'
  | 'node.output.channelUnmapped';

export interface BlueprintValidationIssue {
  readonly id: string;
  readonly severity: BlueprintValidationSeverity;
  readonly code: BlueprintValidationIssueCode;
  readonly message: string;
  readonly nodeId?: number;
  readonly connectionId?: string;
  readonly routeIndex?: number;
  readonly propertyName?: string;
  readonly channelName?: string;
}

export interface BlueprintValidationSummary {
  readonly errors: readonly BlueprintValidationIssue[];
  readonly warnings: readonly BlueprintValidationIssue[];
  readonly errorCount: number;
  readonly warningCount: number;
  readonly canSave: boolean;
}

export interface BlueprintRouteValidationError {
  readonly connectionId: string;
  readonly routeIndex: number;
  readonly message: string;
}

export interface BlueprintValidationInput {
  readonly blueprintName: string;
  readonly nodes: readonly NodeModel[];
  readonly connections: readonly ConnectionModel[];
  readonly routeValidationErrors?: readonly BlueprintRouteValidationError[];
}

export function validateBlueprintGraph(input: BlueprintValidationInput): BlueprintValidationSummary {
  const issues: BlueprintValidationIssue[] = [];
  const nodes = input.nodes;
  const connections = input.connections;
  const nodeById = new Map(nodes.map((node) => [node.id, node] as const));
  const connectionsBySource = groupConnectionsByNode(connections, 'from');
  const connectionsByTarget = groupConnectionsByNode(connections, 'to');
  const startingNode = findStartingNode(nodes, connections);

  if (input.blueprintName.trim().length === 0) {
    issues.push(errorIssue('blueprint-name-required', 'blueprint.name.required', 'Blueprint name is required.'));
  }

  if (nodes.length === 0) {
    issues.push(errorIssue('blueprint-empty', 'blueprint.graph.empty', 'Add at least one node before saving.'));
  }

  if (startingNode?.nodeKind === 'CollectionIterator') {
    issues.push(errorIssue(
      `collection-iterator-start:${startingNode.id}`,
      'node.collectionIterator.start',
      'Collection Iterator must be triggered by an upstream collection output.',
      { nodeId: startingNode.id },
    ));
  }

  for (const node of nodes.filter(node => node.nodeKind === 'ConditionalBranch')) {
    const incoming = connectionsByTarget.get(node.id) ?? [];
    const messages = conditionalRuleIssues(node.conditionalRule, node.inputPropertyDefinitions);
    if (incoming.length !== 1) messages.unshift('Connect one upstream output channel to the Conditional Branch input.');
    messages.forEach((message, i) => issues.push(errorIssue(
      'conditional:' + node.id + ':' + i, 'node.conditionalBranch.invalid', message, { nodeId: node.id },
    )));
  }

  for (const connection of connections) {
    if (connection.from.id === connection.to.id) {
      issues.push(errorIssue(
        `connection-self:${connection.id}`,
        'connection.self',
        `${connection.from.name} cannot connect to itself.`,
        { nodeId: connection.from.id, connectionId: connection.id },
      ));
    }

    if (!nodeById.has(connection.from.id) || !nodeById.has(connection.to.id)) {
      continue;
    }

    addStaleMappingIssues(issues, connection);
  }

  addCycleIssues(issues, nodes, connectionsBySource);
  if (startingNode) {
    addReachabilityIssues(issues, nodes, connectionsBySource, startingNode);
    addRequiredInputIssues(issues, nodes, connectionsByTarget, startingNode.id);
  }

  addRouteWarnings(issues, input.routeValidationErrors ?? []);
  addUnusedOutputWarnings(issues, nodes, connectionsBySource);

  const errors = issues.filter((issue) => issue.severity === 'error');
  const warnings = issues.filter((issue) => issue.severity === 'warning');
  return {
    errors,
    warnings,
    errorCount: errors.length,
    warningCount: warnings.length,
    canSave: errors.length === 0,
  };
}

function addStaleMappingIssues(issues: BlueprintValidationIssue[], connection: ConnectionModel): void {
  const sourceProperties = connection.from.outputPropertiesForAnchor(connection.anchor);
  const targetProperties = connection.to.inputProperties;

  for (const mapping of connection.propertyMappings) {
    if (!Object.prototype.hasOwnProperty.call(sourceProperties, mapping.sourceProperty)) {
      issues.push(errorIssue(
        `mapping-source:${connection.id}:${mapping.targetProperty}:${mapping.sourceProperty}`,
        'connection.mapping.sourceMissing',
        `${connection.from.name} no longer has output property ${mapping.sourceProperty}.`,
        {
          nodeId: connection.from.id,
          connectionId: connection.id,
          propertyName: mapping.sourceProperty,
          channelName: connection.from.outputChannelNameForAnchor(connection.anchor) ?? undefined,
        },
      ));
    }

    if (!Object.prototype.hasOwnProperty.call(targetProperties, mapping.targetProperty)) {
      issues.push(errorIssue(
        `mapping-target:${connection.id}:${mapping.targetProperty}`,
        'connection.mapping.targetMissing',
        `${connection.to.name} no longer has input property ${mapping.targetProperty}.`,
        {
          nodeId: connection.to.id,
          connectionId: connection.id,
          propertyName: mapping.targetProperty,
          channelName: connection.to.inputChannelName ?? undefined,
        },
      ));
    }

    const sourceKind = sourceProperties[mapping.sourceProperty];
    const targetKind = targetProperties[mapping.targetProperty];
    if (sourceKind && targetKind && sourceKind !== targetKind) {
      issues.push(errorIssue(
        `mapping-type:${connection.id}:${mapping.sourceProperty}:${mapping.targetProperty}`,
        'connection.mapping.typeMismatch',
        `${connection.from.name} output ${mapping.sourceProperty} (${sourceKind}) cannot map to ${connection.to.name} input ${mapping.targetProperty} (${targetKind}).`,
        {
          nodeId: connection.to.id,
          connectionId: connection.id,
          propertyName: mapping.targetProperty,
          channelName: connection.to.inputChannelName ?? undefined,
        },
      ));
    }
  }
}

function addCycleIssues(
  issues: BlueprintValidationIssue[],
  nodes: readonly NodeModel[],
  connectionsBySource: ReadonlyMap<number, readonly ConnectionModel[]>,
): void {
  const visiting = new Set<number>();
  const visited = new Set<number>();
  const reportedConnectionIds = new Set<string>();

  const visit = (node: NodeModel): void => {
    if (visited.has(node.id)) {
      return;
    }

    visiting.add(node.id);
    for (const connection of connectionsBySource.get(node.id) ?? []) {
      if (visiting.has(connection.to.id)) {
        if (!reportedConnectionIds.has(connection.id)) {
          reportedConnectionIds.add(connection.id);
          issues.push(errorIssue(
            `connection-cycle:${connection.id}`,
            'connection.cycle',
            `${connection.from.name} cannot connect back to an upstream node (${connection.to.name}).`,
            { nodeId: connection.from.id, connectionId: connection.id },
          ));
        }
        continue;
      }

      visit(connection.to);
    }
    visiting.delete(node.id);
    visited.add(node.id);
  };

  for (const node of nodes) {
    visit(node);
  }
}

function addReachabilityIssues(
  issues: BlueprintValidationIssue[],
  nodes: readonly NodeModel[],
  connectionsBySource: ReadonlyMap<number, readonly ConnectionModel[]>,
  startingNode: NodeModel,
): void {
  const reachableNodeIds = new Set<number>();
  const visit = (node: NodeModel): void => {
    if (reachableNodeIds.has(node.id)) {
      return;
    }

    reachableNodeIds.add(node.id);
    for (const connection of connectionsBySource.get(node.id) ?? []) {
      visit(connection.to);
    }
  };
  visit(startingNode);

  for (const node of nodes) {
    if (!reachableNodeIds.has(node.id)) {
      issues.push(errorIssue(
        `node-unreachable:${node.id}`,
        'node.unreachable',
        `${node.name} is not reachable from the start node.`,
        { nodeId: node.id },
      ));
    }
  }
}

function addRequiredInputIssues(
  issues: BlueprintValidationIssue[],
  nodes: readonly NodeModel[],
  connectionsByTarget: ReadonlyMap<number, readonly ConnectionModel[]>,
  startingNodeId: number,
): void {
  for (const node of nodes) {
    if (node.id === startingNodeId) {
      continue;
    }

    const mappedInputs = collectMappedInputProperties(node, connectionsByTarget.get(node.id) ?? []);
    for (const property of node.inputPropertyDefinitions) {
      if (!property.required) {
        continue;
      }

      if (mappedInputs.has(property.name) || hasDesignTimeOverrideValue(node, property.name)) {
        continue;
      }

      issues.push(errorIssue(
        `node-required-input:${node.id}:${property.name}`,
        'node.input.requiredMissing',
        `${node.name} requires input property ${property.name} to be mapped or configured.`,
        {
          nodeId: node.id,
          propertyName: property.name,
          channelName: node.inputChannelName ?? undefined,
        },
      ));
    }
  }
}

function collectMappedInputProperties(node: NodeModel, incomingConnections: readonly ConnectionModel[]): Set<string> {
  const mappedInputs = new Set<string>();
  for (const connection of incomingConnections) {
    const sourceProperties = connection.from.outputPropertiesForAnchor(connection.anchor);
    for (const mapping of resolveEffectivePropertyMappings(connection)) {
      if (!Object.prototype.hasOwnProperty.call(node.inputProperties, mapping.targetProperty) ||
        !Object.prototype.hasOwnProperty.call(sourceProperties, mapping.sourceProperty) ||
        sourceProperties[mapping.sourceProperty] !== node.inputProperties[mapping.targetProperty]) {
        continue;
      }

      mappedInputs.add(mapping.targetProperty);
    }
  }

  return mappedInputs;
}

function addRouteWarnings(
  issues: BlueprintValidationIssue[],
  routeValidationErrors: readonly BlueprintRouteValidationError[],
): void {
  for (const routeError of routeValidationErrors) {
    issues.push(warningIssue(
      `route-warning:${routeError.connectionId}:${routeError.routeIndex}`,
      'connection.route.obstructed',
      routeError.message,
      { connectionId: routeError.connectionId, routeIndex: routeError.routeIndex },
    ));
  }
}

function addUnusedOutputWarnings(
  issues: BlueprintValidationIssue[],
  nodes: readonly NodeModel[],
  connectionsBySource: ReadonlyMap<number, readonly ConnectionModel[]>,
): void {
  for (const node of nodes) {
    // Either outcome may intentionally terminate, and pass-through fields may remain unused.
    if (node.nodeKind === 'ConditionalBranch') continue;
    const outgoingConnections = connectionsBySource.get(node.id) ?? [];
    if (outgoingConnections.length === 0) {
      continue;
    }

    const usedPropertiesByChannel = new Map<string, Set<string>>();
    const connectedChannels = new Set<string>();
    for (const connection of outgoingConnections) {
      const channelName = node.outputChannelNameForAnchor(connection.anchor);
      if (!channelName) {
        continue;
      }

      connectedChannels.add(channelName);
      const usedProperties = usedPropertiesByChannel.get(channelName) ?? new Set<string>();
      const outputProperties = node.outputPropertiesForAnchor(connection.anchor);
      for (const mapping of resolveEffectivePropertyMappings(connection)) {
        if (Object.prototype.hasOwnProperty.call(outputProperties, mapping.sourceProperty)) {
          usedProperties.add(mapping.sourceProperty);
        }
      }
      usedPropertiesByChannel.set(channelName, usedProperties);
    }

    for (const [outputIndex, channelName] of node.outputChannelNames.entries()) {
      const anchor = outputIndex + (node.inputChannelName ? 1 : 0);
      if (!connectedChannels.has(channelName)) {
        issues.push(warningIssue(
          `channel-unmapped:${node.id}:${channelName}`,
          'node.output.channelUnmapped',
          `${node.name} output channel ${node.outputChannelLabelForAnchor(anchor) ?? channelName} is not connected.`,
          { nodeId: node.id, channelName },
        ));
        continue;
      }

      const usedProperties = usedPropertiesByChannel.get(channelName) ?? new Set<string>();
      for (const property of node.outputPropertyDefinitionsForAnchor(anchor)) {
        if (usedProperties.has(property.name)) {
          continue;
        }

        issues.push(warningIssue(
          `output-unused:${node.id}:${channelName}:${property.name}`,
          'node.output.propertyUnused',
          `${node.name} output property ${property.name} is not used downstream.`,
          {
            nodeId: node.id,
            propertyName: property.name,
            channelName,
          },
        ));
      }
    }
  }
}

function findStartingNode(nodes: readonly NodeModel[], connections: readonly ConnectionModel[]): NodeModel | null {
  if (nodes.length === 0) {
    return null;
  }

  const destinationNodeIds = new Set(connections.map((connection) => connection.to.id));
  return nodes.find((node) => !destinationNodeIds.has(node.id)) ?? nodes[0]!;
}

function groupConnectionsByNode(
  connections: readonly ConnectionModel[],
  key: 'from' | 'to',
): ReadonlyMap<number, readonly ConnectionModel[]> {
  const grouped = new Map<number, ConnectionModel[]>();
  for (const connection of connections) {
    const nodeId = connection[key].id;
    const existing = grouped.get(nodeId);
    if (existing) {
      existing.push(connection);
    } else {
      grouped.set(nodeId, [connection]);
    }
  }

  return grouped;
}

function hasDesignTimeOverrideValue(node: NodeModel, propertyName: string): boolean {
  if (!Object.prototype.hasOwnProperty.call(node.designTimeInputOverrides, propertyName)) {
    return false;
  }

  const value = node.designTimeInputOverrides[propertyName];
  return value !== null && value !== undefined && (typeof value !== 'string' || value.trim().length > 0);
}

function errorIssue(
  id: string,
  code: BlueprintValidationIssueCode,
  message: string,
  details: Omit<BlueprintValidationIssue, 'id' | 'severity' | 'code' | 'message'> = {},
): BlueprintValidationIssue {
  return {
    ...details,
    id,
    severity: 'error',
    code,
    message,
  };
}

function warningIssue(
  id: string,
  code: BlueprintValidationIssueCode,
  message: string,
  details: Omit<BlueprintValidationIssue, 'id' | 'severity' | 'code' | 'message'> = {},
): BlueprintValidationIssue {
  return {
    ...details,
    id,
    severity: 'warning',
    code,
    message,
  };
}

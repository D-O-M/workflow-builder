import { TaskPropertyType } from '../domain/node-model';

export interface ITaskDefinition {
    id: number;
    name: string;
    displayName?: string | null;
    versions: ITaskVersion[];
}

export interface ITaskVersion {
    id: number;
    version: `${number}.${number}.${number}`;
    state: 'Registered' | 'Active' | 'Draining' | 'Superseded' | 'Failed';
    description: string | null;
    inputChannel: ITaskChannel | null;
    outputChannels: ITaskChannel[];
}

export interface ITaskChannel {
    id: number;
    name: string;
    displayName?: string | null;
    properties: ITaskPropertyDefinition[];
}

export interface ITaskPropertyDefinition {
    name: string;
    kind: TaskPropertyType;
    required: boolean;
    enumValues: string[];
}

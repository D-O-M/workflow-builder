export interface ITaskDefinition {
    id: number;
    name: string;
    versions: ITaskVersion[];
}

export interface ITaskVersion {
    id: number;
    version: `${number}.${number}.${number}`;
    state: 'Registered' | 'Active' | 'Draining' | 'Superseded' | 'Failed';
    description: string | null;
    requiredGlobalState: ITaskChannel | null;
    inputChannel: ITaskChannel | null;
    outputChannels: ITaskChannel[];
}

export interface ITaskChannel {
    id: number;
    name: string;
    properties: Record<string, "Number" | "String" | "Boolean" | "Guid">;
}

import { Injectable } from '@angular/core';

export interface RuntimeClientConfig {
  apiBaseUrl: string;
  signalRBaseUrl: string;
  grafanaBaseUrl: string;
}

@Injectable({ providedIn: 'root' })
export class RuntimeClientConfigService {
  private config: RuntimeClientConfig | null = null;

  async load(): Promise<void> {
    const response = await fetch('/api/client-config', { credentials: 'omit' });
    if (!response.ok) {
      throw new Error(`Unable to load runtime client config (${response.status}).`);
    }

    const config = await response.json() as RuntimeClientConfig;
    this.config = {
      apiBaseUrl: this.normalizeBaseUrl(config.apiBaseUrl),
      signalRBaseUrl: this.normalizeBaseUrl(config.signalRBaseUrl),
      grafanaBaseUrl: this.normalizeBaseUrl(config.grafanaBaseUrl),
    };
  }

  get apiBaseUrl(): string {
    return this.requireConfig().apiBaseUrl;
  }

  get signalRBaseUrl(): string {
    return this.requireConfig().signalRBaseUrl;
  }

  get grafanaBaseUrl(): string {
    return this.requireConfig().grafanaBaseUrl;
  }

  private requireConfig(): RuntimeClientConfig {
    if (!this.config) {
      throw new Error('Runtime client config has not been loaded yet.');
    }

    return this.config;
  }

  private normalizeBaseUrl(value: string): string {
    return value.replace(/\/+$/, '');
  }
}

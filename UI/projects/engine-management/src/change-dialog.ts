import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { CapabilitySetting, ManagedPlugin, ManagedVersion } from './management-api';

export type Change =
  | { action: 'edit' | 'remove'; setting: CapabilitySetting }
  | { action: 'toggle'; plugin: ManagedPlugin; version: ManagedVersion | null; enable: boolean };

@Component({
  selector: 'management-change-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ title }}</h2>
    <form (ngSubmit)="confirm()">
      <mat-dialog-content>
        @if (change.action === 'toggle') {
          <p>{{ change.plugin.displayName || change.plugin.name }} · {{ change.version?.version || 'All versions, including future releases' }}</p>
          @if (!change.enable && !change.version && change.plugin.versions.length) {
            <p>Registered versions: {{ registeredVersions }}</p>
          }
          <p>{{ consequence }}</p>
          @if (!change.enable) { <p>Steps already admitted will finish.</p> }
        } @else {
          <p><code>{{ change.setting.key }}</code></p>
          @if (change.action === 'edit') {
            <mat-form-field appearance="outline">
              <mat-label>{{ change.setting.secret ? 'New secret value' : 'Override value' }}</mat-label>
              <input matInput name="value" [(ngModel)]="value" required
                [type]="change.setting.secret ? 'password' : 'text'"
                [attr.autocomplete]="change.setting.secret ? 'new-password' : 'off'">
              <mat-hint>{{ change.setting.kind }}</mat-hint>
            </mat-form-field>
          } @else { <p>The Engine will use the deployment value when available and rerun the capability check.</p> }
        }
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-flat-button type="submit" [disabled]="change.action === 'edit' && !value.length">{{ confirmLabel }}</button>
      </mat-dialog-actions>
    </form>`,
  styles: [`mat-form-field { width: 100%; } code { overflow-wrap: anywhere; } p { line-height: 1.5; }`],
})
export class ChangeDialog {
  readonly change = inject<Change>(MAT_DIALOG_DATA);
  private readonly dialog = inject(MatDialogRef<ChangeDialog, { value: string }>);
  value = this.change.action === 'edit' && !this.change.setting.secret ? this.change.setting.value ?? '' : '';

  get confirmLabel() {
    if (this.change.action === 'edit') return 'Save override';
    if (this.change.action === 'remove') return 'Remove override';
    return this.change.action === 'toggle' && this.change.enable ? 'Enable' : 'Disable';
  }

  get title() {
    if (this.change.action === 'edit') return this.change.setting.secret ? 'Set secret' : 'Edit capability setting';
    if (this.change.action === 'remove') return 'Remove override?';
    return this.confirmLabel + (this.change.action === 'toggle' && this.change.version ? ' this version?' : ' this Plugin?');
  }

  get consequence() {
    const c = this.change;
    if (c.action !== 'toggle') return '';
    if (c.enable) return 'Waiting Steps can resume when a compatible patch passes its capability checks. Individual version rules still apply.';
    if (!c.version) return 'All versions will stop accepting new Steps. Affected Steps wait until the Plugin is enabled and runnable.';
    const target = c.version;
    const alternatives = c.plugin.versions.filter(v => v.id !== target.id && v.lane === target.lane && v.effectiveEnabled);
    if (!alternatives.length) return 'This lane has no other enabled patch. Affected Steps will wait.';
    // The API lists patches in descending version order.
    const replacement = alternatives.find(v => v.runnable);
    if (replacement) return `Patch ${replacement.version} is available in lane ${target.lane}. Engines recheck readiness before routing queued Steps.`;
    return `Engines will check ${alternatives.map(v => v.version).join(', ')} in lane ${target.lane}. No replacement is currently reported runnable. Queued Steps wait until a patch is ready.`;
  }

  get registeredVersions() {
    return this.change.action === 'toggle' ? this.change.plugin.versions.map(version => version.version).join(', ') : '';
  }

  confirm() {
    this.dialog.close({ value: this.value });
    this.value = '';
  }
}

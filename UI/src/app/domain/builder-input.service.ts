import { DOCUMENT } from '@angular/common';
import { Injectable, OnDestroy, inject } from '@angular/core';
import { BlueprintEditorStore } from './blueprint-editor.store';

@Injectable({ providedIn: 'root' })
export class BuilderInputService implements OnDestroy {
  private readonly document = inject(DOCUMENT);
  private readonly editorStore = inject(BlueprintEditorStore);
  private spacePressed = false;

  constructor() {
    if (typeof window === 'undefined') {
      return;
    }

    window.addEventListener('keydown', this.handleKeyDown, true);
    window.addEventListener('keyup', this.handleKeyUp, true);
    window.addEventListener('blur', this.handleWindowBlur);
  }

  ngOnDestroy(): void {
    if (typeof window === 'undefined') {
      return;
    }

    window.removeEventListener('keydown', this.handleKeyDown, true);
    window.removeEventListener('keyup', this.handleKeyUp, true);
    window.removeEventListener('blur', this.handleWindowBlur);
  }

  private readonly handleKeyDown = (event: KeyboardEvent): void => {
    if (event.code === 'Space') {
      this.spacePressed = true;
    }

    this.updateModifierState(event);

    const canvasFocused = this.editorStore.canvasFocused();
    const editableTarget = this.isEditableTarget(event.target);

    if (event.code === 'Space' && canvasFocused && !editableTarget) {
      event.preventDefault();
    }

    if (!canvasFocused || editableTarget) {
      return;
    }

    const ctrlOrMeta = event.ctrlKey || event.metaKey;
    const lowerKey = event.key.toLocaleLowerCase();

    if (event.key === 'Delete') {
      event.preventDefault();
      this.editorStore.deleteSelection();
      return;
    }

    if (!ctrlOrMeta) {
      return;
    }

    switch (lowerKey) {
      case 'c':
        event.preventDefault();
        this.editorStore.copySelection();
        break;
      case 'v':
        event.preventDefault();
        this.editorStore.pasteClipboard();
        break;
      case 'z':
        event.preventDefault();
        if (event.shiftKey) {
          this.editorStore.redo();
        } else {
          this.editorStore.undo();
        }
        break;
      default:
        break;
    }
  };

  private readonly handleKeyUp = (event: KeyboardEvent): void => {
    if (event.code === 'Space') {
      this.spacePressed = false;
    }

    this.updateModifierState(event);
  };

  private readonly handleWindowBlur = (): void => {
    this.spacePressed = false;
    this.editorStore.setModifierState({ shift: false, ctrlOrMeta: false, space: false });
  };

  private updateModifierState(event: KeyboardEvent): void {
    this.editorStore.setModifierState({
      shift: event.shiftKey,
      ctrlOrMeta: event.ctrlKey || event.metaKey,
      space: this.spacePressed,
    });
  }

  private isEditableTarget(target: EventTarget | null): boolean {
    const element = target instanceof this.document.defaultView!.HTMLElement
      ? target
      : null;

    if (!element) {
      return false;
    }

    if (element.isContentEditable) {
      return true;
    }

    return ['INPUT', 'TEXTAREA', 'SELECT'].includes(element.tagName);
  }
}

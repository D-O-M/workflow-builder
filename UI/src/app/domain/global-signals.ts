import { signal, WritableSignal } from "@angular/core";
import { Coordinates } from "./coordinates";

export class GlobalSignals {
    readonly mousePosition: WritableSignal<Coordinates>;

    constructor(initialMouseCoordinates: Coordinates) {
        this.mousePosition = signal(initialMouseCoordinates);
    }
}

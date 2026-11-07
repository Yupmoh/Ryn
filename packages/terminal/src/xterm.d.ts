import type { VtBoundaryTracker } from "./vt-boundary.js";
import type { IDisposable, ITerminalAddon, ITerminalOptions, Terminal } from "@xterm/xterm";
import type {
  RynBridge,
  RynTerminalClient,
  RynTerminalClientOptions,
  TerminalCheckpoint,
  TerminalRetentionMode,
} from "./client.js";

export declare const XTERM_CHECKPOINT_FORMAT: "xterm-addon-serialize@0.14.0:text-v1";

export interface StableCheckpointContext {
  offset: bigint;
  attestStableBoundary?: (offset: bigint) => boolean | Promise<boolean>;
}

export interface XtermCheckpointProvider {
  readonly format: string;
  capture(context: StableCheckpointContext): Promise<TerminalCheckpoint>;
  restore(checkpoint: TerminalCheckpoint): Promise<void>;
}

export interface XtermTerminalLike {
  readonly cols: number;
  readonly rows: number;
  write(data: string | Uint8Array, callback?: () => void): void;
  open(element: HTMLElement): void;
  resize(cols: number, rows: number): void;
  reset(): void;
  refresh(start: number, end: number): void;
  loadAddon(addon: ITerminalAddon): void;
  onData(listener: (data: string) => void): IDisposable;
  onBinary(listener: (data: string) => void): IDisposable;
  dispose(): void;
}

export type RynXtermAdapterOptions = Omit<RynTerminalClientOptions, "consumer" | "onError" | "onStateChange"> & {
  terminal: XtermTerminalLike;
  bridge?: RynBridge;
  client?: RynTerminalClient;
  element?: HTMLElement;
  fitAddon?: { fit(): void } | null;
  checkpointProvider?: XtermCheckpointProvider | null;
  boundaryTracker?: VtBoundaryTracker;
  inputEnabled?: boolean;
  requestResizeLease?: boolean;
  resizeDebounceMilliseconds?: number;
  onStateChange?: (state: string, adapter: RynXtermAdapter) => void;
  onError?: (error: unknown, context: { terminal: boolean; adapter?: RynXtermAdapter }) => void;
  onRendererChange?: (renderer: "webgl" | "dom", reason: string) => void;
  linkPolicy?: ((url: URL, event?: MouseEvent) => boolean | Promise<boolean>) | null;
  clipboardPolicy?: ((request: unknown) => unknown | Promise<unknown>) | null;
};
export interface ConvenienceXtermOptions extends Omit<RynXtermAdapterOptions, "terminal"> {
  terminal?: Terminal;
  Terminal: new (options?: ITerminalOptions) => Terminal;
  terminalOptions?: ITerminalOptions;
  fitAddon?: (ITerminalAddon & { fit(): void }) | null;
}

export declare function writeXterm(terminal: Pick<Terminal, "write">, bytes: Uint8Array): Promise<void>;
export declare function createXtermSerializeCheckpointProvider(options: {
  terminal: Pick<Terminal, "cols" | "rows" | "resize" | "reset" | "write">;
  serializeAddon: { serialize(): string };
}): XtermCheckpointProvider;

export declare class RynXtermAdapter {
  constructor(options: RynXtermAdapterOptions);
  readonly terminal: XtermTerminalLike;
  readonly client: RynTerminalClient;
  readonly resizeLease: { leaseId: string; leaseGeneration: string } | null;
  readonly disposed: boolean;
  create(command: string, args?: readonly string[], dimensions?: { cols?: number; rows?: number; retentionMode?: TerminalRetentionMode }): Promise<{ sessionId: string; accessToken: string }>;
  open(element?: HTMLElement): Promise<boolean>;
  attach(element?: HTMLElement): Promise<void>;
  fit(): Promise<boolean>;
  scheduleFit(): void;
  claimResize(): Promise<boolean>;
  releaseResize(): Promise<void>;
  captureCheckpoint(): Promise<TerminalCheckpoint>;
  useWebgl(createAddon: () => ITerminalAddon & { onContextLoss?: (listener: () => void) => IDisposable }): boolean;
  disableWebgl(reason?: string): void;
  openLink(uri: string, event?: MouseEvent): Promise<boolean>;
  clipboard(request: unknown): Promise<unknown | null>;
  dispose(): Promise<void>;
}

export declare function createXtermAdapter(options: ConvenienceXtermOptions): RynXtermAdapter;

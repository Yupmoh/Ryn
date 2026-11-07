export type TerminalOffset = bigint;
export type TerminalRetentionMode = "streaming" | "reattachable";
export type TerminalMode = "controller" | "mirror";
export type TerminalErrorCode =
  | "unknown_session"
  | "unauthorized_attachment"
  | "stale_generation"
  | "invalid_offset"
  | "budget_exceeded"
  | "history_unavailable"
  | "incompatible_checkpoint"
  | "terminal_exited"
  | "cancelled"
  | "invalid_request"
  | "resync_required";

export interface RynBridge {
  invoke(command: string, args?: Record<string, unknown>): Promise<any>;
}

export interface TerminalWriteContext {
  startOffset: bigint;
  endOffset: bigint;
  client: RynTerminalClient;
  lifecycle: number;
}

export interface TerminalCheckpointContext {
  client: RynTerminalClient;
  lifecycle: number;
}

export interface TerminalCheckpoint {
  offset: bigint;
  format: string;
  payloadBase64: string;
  cols: number;
  rows: number;
}

export interface TerminalConsumer {
  prepareAttachment?(context: {
    nextOffset: bigint;
    retainedStart: bigint;
    retainedEnd: bigint;
    checkpointAvailable: boolean;
    client: RynTerminalClient;
    lifecycle: number;
  }): void | Promise<void>;
  write(bytes: Uint8Array, context: TerminalWriteContext): void | Promise<void>;
  restoreCheckpoint?(checkpoint: TerminalCheckpoint, context: TerminalCheckpointContext): void | Promise<void>;
  /** Finish restore-dependent setup before any live output is read. */
  attachmentReady?(context: TerminalCheckpointContext): void | Promise<void>;
}
export interface TerminalStatus {
  state: string;
  retainedStart: string;
  retainedEnd: string;
  readOffset: string;
  deliveredOffset: string;
  acknowledgedOffset: string;
  outstandingBytes: number;
  creditBytes: number;
  bytesRead: string;
  bytesSent: string;
  bytesConsumed: string;
  retainedBytes: number;
  queueHighWaterBytes: number;
  pauseMilliseconds: string;
  /** Last sampled host delivery-to-cumulative-ACK latency; null until a sample completes. */
  ackLatencyMilliseconds?: number | null;
  processExited: boolean;
  exitCode: number | null;
  finalOffset: string | null;
  needsCheckpoint: boolean;
  hasResizeLease: boolean;
  outputTruncated: boolean;
}


export interface RynTerminalClientOptions {
  bridge: RynBridge;
  consumer: TerminalConsumer;
  sessionId?: string;
  accessToken?: string;
  cursor?: string | bigint | null;
  mode?: TerminalMode;
  creditBytes?: number;
  maxReadBytes?: number;
  readWaitMilliseconds?: number;
  ackThresholdBytes?: number;
  ackDelayMilliseconds?: number;
  onStateChange?: (state: string, client: RynTerminalClient) => void;
  onError?: (error: RynTerminalError, context: { terminal: boolean; client: RynTerminalClient }) => void;
}

export class RynTerminalError extends Error {
  readonly code: TerminalErrorCode | string;
  constructor(code: string, message?: string, cause?: unknown);
}

export class RynTerminalClient {
  constructor(options: RynTerminalClientOptions);
  readonly sessionId: string | null;
  readonly accessToken: string | null;
  readonly attachmentId: string | null;
  readonly generation: string | null;
  readonly state: string;
  readonly nextOffset: bigint;
  readonly consumedOffset: bigint;
  readonly acknowledgedOffset: bigint;
  readonly processExited: boolean;
  readonly exitCode: number | null;
  readonly finalOffset: bigint | null;
  readonly outputTruncated: boolean;
  readonly disposed: boolean;
  create(command: string, args?: readonly string[], dimensions?: { cols?: number; rows?: number; retentionMode?: TerminalRetentionMode }): Promise<{ sessionId: string; accessToken: string }>;
  attach(): Promise<void>;
  closed(): Promise<void>;
  writeText(text: string): Promise<void>;
  writeBytes(bytes: Uint8Array): Promise<void>;
  terminate(): Promise<void>;
  status(): Promise<TerminalStatus>;
  claimResize(): Promise<{ leaseId: string; leaseGeneration: string }>;
  resize(lease: { leaseId: string; leaseGeneration: string }, cols: number, rows: number): Promise<void>;
  releaseResize(lease: { leaseId: string; leaseGeneration: string }): Promise<void>;
  saveCheckpoint(checkpoint: TerminalCheckpoint): Promise<void>;
  withReadsPaused<T>(callback: (context: { client: RynTerminalClient; offset: bigint; lifecycle: number }) => T | Promise<T>): Promise<T>;
  dispose(): Promise<void>;
}

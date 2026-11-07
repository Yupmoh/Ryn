export declare class VtBoundaryTracker {
  constructor(offset?: bigint);
  readonly offset: bigint;
  readonly state: string;
  readonly stable: boolean;
  readonly checkpointUnsafeReason: string | null;
  reset(offset?: bigint): void;
  accept(bytes: Uint8Array, startOffset?: bigint, endOffset?: bigint): boolean;
  isStableAt(offset: bigint): boolean;
}

const ENCODE_CHUNK_BYTES = 0x8000;

/** @param {string} value */
export function decodeBase64(value) {
  if (typeof value !== "string") throw new TypeError("Expected base64 data to be a string");
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
  return bytes;
}

/** @param {Uint8Array} bytes */
export function encodeBase64(bytes) {
  if (!(bytes instanceof Uint8Array)) throw new TypeError("Expected Uint8Array data");
  let binary = "";
  for (let offset = 0; offset < bytes.byteLength; offset += ENCODE_CHUNK_BYTES) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + ENCODE_CHUNK_BYTES));
  }
  return btoa(binary);
}

/** xterm onBinary emits a byte-string, not UTF-8 text. @param {string} value */
export function binaryStringToBytes(value) {
  const bytes = new Uint8Array(value.length);
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    if (code > 0xff) throw new TypeError("xterm binary input contains a non-byte character");
    bytes[index] = code;
  }
  return bytes;
}

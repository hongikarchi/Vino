/**
 * Opaque ARGB int (the layer-color wire format — a signed 32-bit int from C#) to a CSS hex color.
 * Alpha is intentionally dropped: layer colors are opaque by contract (the server forces 0xFF),
 * and a swatch with alpha would just dim against the panel background.
 */
export function argbToCssHex(argb: number): string {
  // >>> 0 folds C#'s signed int (e.g. -8355712) into the unsigned 32-bit value it encodes.
  const unsigned = argb >>> 0;
  const rgb = unsigned & 0xffffff;
  return `#${rgb.toString(16).padStart(6, "0")}`;
}

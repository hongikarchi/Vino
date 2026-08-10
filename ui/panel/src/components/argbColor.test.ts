import { describe, expect, it } from "vitest";
import { argbToCssHex } from "./argbColor";

describe("argbToCssHex", () => {
  it("folds C#'s signed ARGB ints into CSS hex", () => {
    // -16777216 = 0xFF000000 (opaque black), -1 = 0xFFFFFFFF (opaque white).
    expect(argbToCssHex(-16777216)).toBe("#000000");
    expect(argbToCssHex(-1)).toBe("#ffffff");
    // -6250332 = 0xFFA0A0A4 — the classic grey.
    expect(argbToCssHex(-6250332)).toBe("#a0a0a4");
  });

  it("pads small channel values", () => {
    // 0xFF000102 as signed int.
    expect(argbToCssHex(-16776958)).toBe("#000102");
  });
});

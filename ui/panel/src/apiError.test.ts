import { afterEach, describe, expect, it } from "vitest";
import { apiErrorText, setUiLanguage } from "./i18n";

// Display-layer contract for server ApiError codes: render BY CODE at the last moment, never
// show the code itself, and never lose a server detail the dictionary cannot reproduce.
describe("apiErrorText", () => {
  afterEach(() => setUiLanguage("en"));

  it("replaces a fixed-text code entirely, following the language toggle", () => {
    setUiLanguage("ko");
    expect(apiErrorText("session_paused", "Session x is paused.")).toBe(
      "세션이 일시정지 상태입니다 — 재개한 뒤 다시 시도하세요.",
    );
    setUiLanguage("en");
    expect(apiErrorText("session_paused", "Session x is paused.")).toBe(
      "The session is paused — resume it and try again.",
    );
  });

  it("keeps the variable server detail behind a localized headline for prefixed codes", () => {
    setUiLanguage("ko");
    expect(apiErrorText("bridge_error", "GrasshopperDocumentUnavailable")).toBe(
      "Rhino 브리지 오류 — GrasshopperDocumentUnavailable",
    );
  });

  it("returns null for unknown or missing codes so the caller falls back to the server sentence", () => {
    expect(apiErrorText("some_future_code", "detail")).toBeNull();
    expect(apiErrorText(null, "detail")).toBeNull();
  });

  it("renders the Claude-backend codes in both languages", () => {
    setUiLanguage("ko");
    expect(apiErrorText("unknown_backend", "Backend 'x' is not supported.")).toContain("엔진");
    expect(apiErrorText("model_backend_mismatch", "raw")).toContain("고정");
    setUiLanguage("en");
    expect(apiErrorText("unknown_backend", "raw")).toContain("engine");
    expect(apiErrorText("model_backend_mismatch", "raw")).toContain("engine");
  });
});

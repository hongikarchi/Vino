import { describe, expect, it } from "vitest";
import { deriveWorkPhase } from "./workPhase";
import type { SessionActivity } from "./types";

const activityOf = (kind: string): SessionActivity[] => [
  { at: "2026-08-14T00:00:00Z", kind, summary: kind } as SessionActivity,
];

describe("deriveWorkPhase", () => {
  it("returns null when the session is not actively working", () => {
    expect(deriveWorkPhase({ status: "idle" })).toBeNull();
    expect(deriveWorkPhase({ status: "paused" })).toBeNull();
    expect(deriveWorkPhase({ status: "blocked" })).toBeNull();
    expect(deriveWorkPhase({ status: "queued" })).toBeNull();
  });

  it("verifying wins over everything — it is the only live server phase", () => {
    expect(deriveWorkPhase({ status: "verifying" })).toBe("verifying");
    expect(
      deriveWorkPhase({
        status: "working",
        job: { phase: "verifying" },
        activity: activityOf("arrange_layout"),
      }),
    ).toBe("verifying");
  });

  it("a queued or executing job reads as drafting", () => {
    expect(deriveWorkPhase({ status: "working", job: { phase: "applying" } })).toBe("drafting");
    expect(deriveWorkPhase({ status: "working", job: { phase: "waiting" } })).toBe("drafting");
  });

  it("maps the latest finished tool call to its phase", () => {
    expect(deriveWorkPhase({ status: "working", activity: activityOf("snapshot_read") })).toBe("reading");
    expect(deriveWorkPhase({ status: "working", activity: activityOf("artifact_write") })).toBe("drafting");
    expect(deriveWorkPhase({ status: "working", activity: activityOf("arrange_layout") })).toBe("tidying");
  });

  it("working with no tool activity yet is planning", () => {
    expect(deriveWorkPhase({ status: "working" })).toBe("planning");
    expect(deriveWorkPhase({ status: "working", activity: [] })).toBe("planning");
    expect(deriveWorkPhase({ status: "working", activity: activityOf("unknown_tool") })).toBe("planning");
  });

  it("optimistic just-sent state is planning — last turn's activity tail must not lie", () => {
    expect(deriveWorkPhase({ status: "drafting" })).toBe("planning");
    expect(deriveWorkPhase({ status: "drafting", activity: activityOf("snapshot_read") })).toBe("planning");
    // A live job still wins: the send raced a queued ChangeSet.
    expect(deriveWorkPhase({ status: "drafting", job: { phase: "applying" } })).toBe("drafting");
  });
});

import { describe, expect, it } from "vitest";
import { effectiveApprovalChoices } from "./approvalAnswer";
import type { ApprovalItem } from "../types";

const triage: ApprovalItem = {
  id: "lay-4",
  label: "misc-stuff-01",
  targets: [{ objectId: "b0c1d2e3-0000-4b4b-a1a1-000000000004", fingerprint: "fp" }],
  choices: ["concrete", "steel", "wood"],
};

const plain: ApprovalItem = {
  id: "lay-1",
  label: "콘크리트 벽",
  targets: [{ objectId: "b0c1d2e3-0000-4b4b-a1a1-000000000001", fingerprint: "fp" }],
};

describe("effectiveApprovalChoices", () => {
  it("sends the displayed default when the user never clicked a radio", () => {
    // The card shows "concrete" selected; approving without touching it must still answer
    // "concrete" — otherwise the triage layer is granted with no material to write.
    expect(effectiveApprovalChoices([triage], { "lay-4": true }, {})).toEqual({ "lay-4": "concrete" });
  });

  it("keeps an explicit pick", () => {
    expect(effectiveApprovalChoices([triage], { "lay-4": true }, { "lay-4": "steel" })).toEqual({
      "lay-4": "steel",
    });
  });

  it("ignores unticked items and items without choices", () => {
    expect(effectiveApprovalChoices([triage, plain], { "lay-1": true }, { "lay-4": "steel" })).toEqual({});
  });
});

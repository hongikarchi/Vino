import type { ApprovalItem } from "../types";

/**
 * The choices an approval answer must carry, given what the user ticked and what they explicitly
 * clicked. The first radio of a choice group renders as selected before it is touched, so an
 * untouched group has to answer with that same default — otherwise the option the user SAW never
 * reaches the grant, and the agent either asks again or (on a layer triage row) has no material
 * to write at all. Only ticked items contribute: a choice attached to a refused item is not a
 * decision about anything that will happen.
 */
export function effectiveApprovalChoices(
  items: ApprovalItem[],
  checked: Record<string, boolean>,
  explicit: Record<string, string>,
): Record<string, string> {
  const result: Record<string, string> = {};
  for (const item of items) {
    if (!checked[item.id] || !item.choices?.length) continue;
    result[item.id] = explicit[item.id] ?? item.choices[0];
  }
  return result;
}

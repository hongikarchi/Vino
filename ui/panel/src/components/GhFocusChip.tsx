import { useState } from "react";
import { fmt, t } from "../i18n";
import type { CanvasFocusResult } from "../types";

/**
 * The Grasshopper-canvas twin of FocusChip: one chip = a set of components Vino worked on.
 * Click selects them on the GH canvas and frames them in the viewport (POST /canvas/focus).
 * Unlike the Rhino FocusChip there is no isolate/lock — a canvas has no "hide the rest" notion —
 * so this is a plain select+zoom with no shared restore stack to manage.
 *
 * The chip carries the session's bound document. Without it the host resolves to the
 * FIRST-REGISTERED definition, so with two .gh files open a chip for the second one selected
 * nothing there and cleared the other document's selection instead.
 */
interface GhFocusChipProps {
  objectIds: string[];
  label: string;
  /** docKey of the definition these ids live in; null falls back to the single-document default. */
  docId?: string | null;
  onFocusCanvas(objectIds: string[], docId?: string | null): Promise<CanvasFocusResult>;
}

// Selecting and framing fail independently, and the user reads "nothing happened" for both. Name
// the one that actually occurred.
function describeOutcome(outcome: CanvasFocusResult): string {
  const selected = outcome.selectedCount ?? 0;
  const missing = outcome.missingCount ?? 0;
  if (selected === 0) {
    return missing > 0 ? fmt.ghNotFoundMissing(missing) : t("notFound");
  }
  const found = missing > 0
    ? `${fmt.countSelected(selected)} · ${fmt.countGone(missing)}`
    : fmt.countSelected(selected);
  if (outcome.framed === false) {
    switch (outcome.skipReason) {
      case "otherDocumentShown":
        return `${found} · ${t("ghSkipOtherDoc")}`;
      case "editorClosed":
        return `${found} · ${t("ghSkipEditorClosed")}`;
      case "noBounds":
        return `${found} · ${t("ghSkipNoBounds")}`;
      case "zoomNotRequested":
        return found;
      default:
        return `${found} · ${t("ghSkipZoom")}`;
    }
  }
  return found;
}

export function GhFocusChip({ objectIds, label, docId, onFocusCanvas }: GhFocusChipProps) {
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);

  const activate = async () => {
    if (busy) return;
    setBusy(true);
    setNote(null);
    try {
      setNote(describeOutcome(await onFocusCanvas(objectIds, docId)));
    } catch (cause) {
      setNote(cause instanceof Error ? cause.message : String(cause));
    } finally {
      setBusy(false);
    }
  };

  return (
    <span className="focus-chip-wrap">
      <button
        type="button"
        className="focus-chip gh"
        disabled={busy}
        onClick={() => void activate()}
        title={fmt.ghChipTitle(objectIds.length)}
      >
        <span aria-hidden="true">◇</span>
        {label}
      </button>
      {note ? <span className="focus-chip-note">{note}</span> : null}
    </span>
  );
}

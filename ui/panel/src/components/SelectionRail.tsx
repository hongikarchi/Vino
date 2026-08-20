/**
 * The composer's context rail: one always-present row showing what this message is about.
 *
 * Two things about it are load-bearing.
 *
 * 1. **It is always rendered.** The old strip mounted only when something was selected, so every
 *    click in Rhino or Grasshopper grew or shrank the composer by exactly 30px (measured), which
 *    resized the transcript and — at the scroll threshold — flipped its scrollbar and moved every
 *    message bubble ~16px sideways. A row that is always there cannot do that. When nothing is
 *    selected the chips read 0 and simply look inert.
 *
 * 2. **Rhino and Grasshopper are separate chips.** Pinning used to be one slot holding a single
 *    snapshot of both domains, so pinning GH threw away a Rhino pin and unpinning dropped both.
 *    The wire contract (PinnedSelection) always allowed both independently; only the UI collapsed
 *    them.
 *
 * No pushpin glyph: a chip is pinned when it is filled with the accent colour, which is also what
 * hover previews. The state IS the colour.
 */
import { fmt, t } from "../i18n";

interface SelectionRailProps {
  /** Live counts streamed from the runtime — what is selected in Rhino/GH right now. */
  liveRhinoCount: number;
  liveGhCount: number;
  /** How many are pinned to this message, or null when that domain is not pinned. */
  pinnedRhinoCount: number | null;
  pinnedGhCount: number | null;
  busy?: boolean;
  disabled?: boolean;
  /** Click on the Rhino chip: pin the live selection, or unpin if already pinned. */
  onToggleRhino(): void;
  onToggleGh(): void;
  /** Click on a pinned chip's count re-reveals it in the viewport / on the canvas. */
  onRevealRhino(): void;
  onRevealGh(): void;
}

function chipTitle(domain: string, pinnedCount: number | null, liveCount: number): string {
  if (pinnedCount !== null) {
    return fmt.railPinnedTitle(domain, pinnedCount);
  }
  if (liveCount > 0) {
    return fmt.railLiveTitle(domain, liveCount);
  }
  return fmt.railEmptyTitle(domain);
}

export function SelectionRail({
  liveRhinoCount,
  liveGhCount,
  pinnedRhinoCount,
  pinnedGhCount,
  busy = false,
  disabled = false,
  onToggleRhino,
  onToggleGh,
  onRevealRhino,
  onRevealGh,
}: SelectionRailProps) {
  const renderChip = (
    domain: "Rhino" | "GH",
    glyph: string,
    pinnedCount: number | null,
    liveCount: number,
    onToggle: () => void,
    onReveal: () => void,
  ) => {
    const pinned = pinnedCount !== null;
    // The count is the live selection until you pin, then it is what you pinned — the number the
    // message will actually carry.
    const count = pinned ? pinnedCount : liveCount;
    const actionable = pinned || liveCount > 0;
    return (
      <span className={`rail-chip-wrap${pinned ? " pinned" : ""}`}>
        <button
          type="button"
          className={`rail-chip${pinned ? " pinned" : ""}${actionable ? "" : " empty"}`}
          onClick={onToggle}
          disabled={busy || disabled || !actionable}
          aria-pressed={pinned}
          title={chipTitle(domain, pinnedCount, liveCount)}
        >
          <span className="rail-chip-glyph" aria-hidden="true">{glyph}</span>
          <span className="rail-chip-name">{domain}</span>
          <span className="rail-chip-count">{count}</span>
        </button>
        {pinned ? (
          <button
            type="button"
            className="rail-chip-reveal"
            onClick={onReveal}
            disabled={busy || disabled}
            title={fmt.railRevealTitle(domain)}
            aria-label={fmt.railRevealAria(domain)}
          >
            ◎
          </button>
        ) : null}
      </span>
    );
  };

  return (
    <div className="selection-rail" aria-label={t("railAria")}>
      {renderChip("Rhino", "◈", pinnedRhinoCount, liveRhinoCount, onToggleRhino, onRevealRhino)}
      {renderChip("GH", "◇", pinnedGhCount, liveGhCount, onToggleGh, onRevealGh)}
    </div>
  );
}

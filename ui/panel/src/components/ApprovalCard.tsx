import { useState } from "react";
import type { ApprovalCard as ApprovalCardData, CanvasFocusResult, FocusMode, FocusResult } from "../types";
import { effectiveApprovalChoices } from "./approvalAnswer";
import { argbToCssHex } from "./argbColor";
import { approvalTargetRows } from "./approvalTargets";
import { GhFocusChip } from "./GhFocusChip";
import { useFocusTarget } from "./useFocusTarget";

/**
 * Approve-what-you-saw for the user's OWN geometry. The broker refuses destructive ops on objects
 * GPTino did not create, and this card is the only way to lift that for specific objects: ticking
 * an item grants exactly its (objectId, fingerprint) pairs. Pinning to fingerprints is the point —
 * if the object moved after the audit, the grant no longer matches and the fix fails instead of
 * hitting something the user never saw. Choices exist where a machine must not decide (which of two
 * near-duplicates to keep is a design decision, not a cleanup).
 */
interface ApprovalCardProps {
  card: ApprovalCardData;
  busy?: boolean;
  /** The last failure from answering THIS card, rendered inline instead of only as a chip. */
  failure?: string;
  /** Whether a Grasshopper definition is open at all. Without one there is no canvas to zoom. */
  hasGrasshopper?: boolean;
  /** Clear an ANSWERED card. Nothing else ever emptied the slot, so answered cards never left. */
  onDismiss?(): void;
  onAnswer(answer: {
    status: "granted" | "rejected";
    approvedItemIds?: string[];
    choices?: Record<string, string>;
    preset?: string;
    /** Why the user refused. Delivered to the agent, so "no, because…" needs no second message. */
    reason?: string;
  }): void;
  onFocus?(objectIds: string[], mode: FocusMode): Promise<FocusResult>;
  /**
   * The panel's existing Grasshopper canvas-focus channel (POST /canvas/focus — the same one
   * [[ghfocus:…]] chips use). Destructive-cleanup targets are GH components, so their zoom chips
   * go to the canvas, not the Rhino viewport. Optional — without it the chips simply don't render.
   */
  onFocusCanvas?(objectIds: string[]): Promise<CanvasFocusResult>;
}

export function ApprovalCard({ card, busy = false, failure, hasGrasshopper = false, onAnswer, onDismiss, onFocus, onFocusCanvas }: ApprovalCardProps) {
  // Layer-curation rows arrive with a server-computed default check state (high/medium matches
  // pre-checked, triage and custom-colored layers not) — a lazy initializer, so the user's later
  // toggles are never overwritten by a re-render.
  const [checked, setChecked] = useState<Record<string, boolean>>(() => {
    const initial: Record<string, boolean> = {};
    for (const item of card.items) {
      if (item.layerRow?.preChecked) initial[item.id] = true;
    }
    return initial;
  });
  const [choices, setChoices] = useState<Record<string, string>>({});
  const [preset, setPreset] = useState<string | undefined>(card.preset?.selected);
  const [reason, setReason] = useState("");
  const focus = useFocusTarget(onFocus);
  const answered = card.status !== "proposing";
  const approvedCount = card.items.filter((item) => checked[item.id]).length;
  // A granted card outlives its key: the grant is held in host memory for 15 minutes while the
  // card is a durable row. Showing "승인됨" over a dead key is how "승인했는데 또 안 됨" happened,
  // so the badge tells the truth about the key, not just about the click.
  const grantExpired =
    card.status === "granted" &&
    Boolean(card.grantExpiresAt) &&
    Date.parse(card.grantExpiresAt!) <= Date.now();

  return (
    <section className={`approval-card approval-${card.status}`} aria-label="변경 승인">
      <header className="goal-card-head">
        <strong>{answered ? "승인 결과" : "이 변경을 승인하시겠어요?"}</strong>
        {card.status === "granted" ? (
          <span className={`goal-card-badge${grantExpired ? " expired" : ""}`}>
            {grantExpired ? "승인 만료됨" : "승인됨"}
          </span>
        ) : null}
        {card.status === "rejected" ? <span className="goal-card-badge">거절됨</span> : null}
        {answered && onDismiss ? (
          <button
            type="button"
            className="chip-remove"
            disabled={busy}
            onClick={onDismiss}
            title="이 카드를 닫습니다 — 기록은 대화에 남습니다"
            aria-label="승인 카드 닫기"
          >
            ×
          </button>
        ) : null}
      </header>
      <p className="goal-card-objective">{card.summary}</p>
      {failure ? (
        <p className="card-failure" role="alert">{failure}</p>
      ) : null}
      {grantExpired ? (
        <p className="approval-expiry-note" role="status">
          승인 키의 유효시간(15분)이 지났습니다. 같은 작업이 여전히 필요하면 다시 요청하도록 말해 주세요.
        </p>
      ) : null}
      {card.status === "rejected" && card.rejectedReason ? (
        <p className="approval-expiry-note">거절 사유: {card.rejectedReason}</p>
      ) : null}

      {/* Colour convention for the whole card. Switching it re-derives every proposed colour on
          the server when the answer lands, and the choice is remembered for later scans. */}
      {!answered && card.preset && card.preset.options.length > 1 ? (
        <div className="approval-preset" role="radiogroup" aria-label="색 프리셋">
          <span className="approval-preset-label">색 프리셋</span>
          {card.preset.options.map((option) => (
            <label key={option.id}>
              <input
                type="radio"
                name="approval-preset"
                disabled={busy}
                checked={(preset ?? card.preset!.selected) === option.id}
                onChange={() => setPreset(option.id)}
              />
              {option.label}
            </label>
          ))}
        </div>
      ) : null}

      <ul className="approval-card-list">
        {card.items.map((item) => {
          const granted = card.approvedItemIds?.includes(item.id);
          const targetRows = approvalTargetRows(item);
          return (
            <li key={item.id} className={answered && granted ? "granted" : ""}>
              <label>
                {answered ? (
                  <span aria-hidden="true">{granted ? "✔ " : "· "}</span>
                ) : (
                  <input
                    type="checkbox"
                    checked={checked[item.id] ?? false}
                    disabled={busy}
                    onChange={(event) =>
                      setChecked((current) => ({ ...current, [item.id]: event.target.checked }))
                    }
                  />
                )}
                <span>{item.label}</span>
                {item.measure ? <span className="approval-card-measure"> {item.measure}</span> : null}
              </label>
              {/* Layer rows focus their SAMPLE OBJECTS — the targets carry the layer GUID, which
                  the viewport cannot select ("0 selected" silent failure). */}
              {onFocus && (item.layerRow ? item.layerRow.focusObjectIds?.length : item.targets.length) ? (
                <button
                  type="button"
                  className="goal-card-show"
                  disabled={busy}
                  title="이 항목의 객체를 뷰포트에서 보기"
                  onClick={() =>
                    void focus.focus(
                      item.id,
                      item.layerRow?.focusObjectIds ?? item.targets.map((target) => target.objectId),
                      "select",
                    )
                  }
                >
                  ◎
                </button>
              ) : null}
              {item.schemeRow ? (
                <span className="approval-scheme-row">
                  {/* Two axes, shown apart: an element the user's own words name, and a material
                      the colour comes from. Either may be missing — that is a real state. */}
                  {item.schemeRow.element ? (
                    <span className="approval-scheme-axis">
                      <span className="approval-axis-label">요소</span>
                      {item.schemeRow.element}
                    </span>
                  ) : null}
                  {item.schemeRow.material ? (
                    <span className="approval-scheme-axis">
                      <span className="approval-axis-label">재료</span>
                      {item.schemeRow.material}
                      {item.schemeRow.underPath ? ` (${item.schemeRow.underPath} 아래 전체)` : null}
                    </span>
                  ) : null}
                  <span className="approval-scheme-count">레이어 {item.schemeRow.members.length}개</span>
                  {item.schemeRow.evidence ? (
                    <span className="approval-evidence" title={item.schemeRow.evidence}>
                      {item.schemeRow.evidence}
                    </span>
                  ) : null}
                  <details className="approval-scheme-members">
                    <summary>대상 레이어 보기</summary>
                    <ul>
                      {item.schemeRow.members.map((member) => (
                        <li key={member}>{member}</li>
                      ))}
                    </ul>
                  </details>
                </span>
              ) : null}
              {item.layerRow ? (
                <span className="approval-layer-row">
                  <span
                    className="approval-swatch"
                    title={`현재 색 ${argbToCssHex(item.layerRow.currentArgbColor)}`}
                    style={{ background: argbToCssHex(item.layerRow.currentArgbColor) }}
                  />
                  {item.layerRow.proposedArgbColor !== item.layerRow.currentArgbColor ? (
                    <>
                      <span aria-hidden="true">→</span>
                      <span
                        className="approval-swatch"
                        title={`제안 색 ${argbToCssHex(item.layerRow.proposedArgbColor)}`}
                        style={{ background: argbToCssHex(item.layerRow.proposedArgbColor) }}
                      />
                    </>
                  ) : null}
                  {item.layerRow.canonical ? (
                    <span className="approval-layer-canonical">
                      {item.layerRow.canonical}
                      {item.layerRow.material ? ` · ${item.layerRow.material}` : null}
                    </span>
                  ) : null}
                  <span className={`approval-confidence ${item.layerRow.confidence}`}>
                    {item.layerRow.confidence}
                  </span>
                  <span className="approval-evidence" title={item.layerRow.evidence}>
                    {item.layerRow.evidence}
                  </span>
                  <span className="approval-layer-note">이름은 바뀌지 않음</span>
                </span>
              ) : null}
              {/* A choice only matters for an item the user is actually granting. */}
              {!answered && item.choices?.length && checked[item.id] ? (
                <span className="approval-card-choices" role="radiogroup" aria-label="어느 것을 남길까요">
                  {item.choices.map((choice) => (
                    <label key={choice}>
                      <input
                        type="radio"
                        name={`choice-${item.id}`}
                        checked={(choices[item.id] ?? item.choices![0]) === choice}
                        onChange={() => setChoices((current) => ({ ...current, [item.id]: choice }))}
                      />
                      {choice}
                    </label>
                  ))}
                </span>
              ) : null}
              {/* Model-authored per-target context so a destructive cleanup can actually be judged:
                  what each component is, what it does, and what changes if it goes. Legacy cards
                  (bare objectId+fingerprint targets) produce no rows and render exactly as before. */}
              {targetRows.length > 0 ? (
                <ul className="approval-card-targets">
                  {targetRows.map((row) => (
                    <li key={row.key}>
                      <span className="approval-target-head">
                        <strong className="approval-target-label">{row.heading}</strong>
                        {/* The zoom follows the TARGET's world, not a hardcoded one. This chip was
                            wired to the Grasshopper canvas for every row, so a card about Rhino
                            meshes (or layers) answered "No Grasshopper definition is open" — and in
                            a Rhino-only session there is no canvas to point at in the first place.
                            Canvas rows keep the canvas chip; Rhino rows get the viewport chip, which
                            is the one the user found working. */}
                        {row.onCanvas ? (
                          onFocusCanvas && hasGrasshopper ? (
                            <GhFocusChip objectIds={row.zoomObjectIds} label="확대" onFocusCanvas={onFocusCanvas} />
                          ) : null
                        ) : onFocus ? (
                          <button
                            type="button"
                            className="goal-card-show"
                            disabled={busy}
                            title="이 대상을 Rhino 뷰포트에서 보기"
                            onClick={() => void focus.focus(row.key, row.zoomObjectIds, "select")}
                          >
                            ◎
                          </button>
                        ) : null}
                        {focus.notes[row.key] ? (
                          <span className="focus-chip-note">{focus.notes[row.key]}</span>
                        ) : null}
                      </span>
                      {row.role ? <span className="approval-target-line">역할: {row.role}</span> : null}
                      {row.impact ? <span className="approval-target-line">변경: {row.impact}</span> : null}
                    </li>
                  ))}
                </ul>
              ) : null}
            </li>
          );
        })}
      </ul>

      {!answered ? (
        <div className="goal-card-actions">
          {/* Optional, and deliberately not required: a refusal must stay one click. The text
              only exists so "이건 두고 저것만" does not have to be retyped as a chat message. */}
          <input
            type="text"
            className="approval-reason"
            value={reason}
            disabled={busy}
            placeholder="거절 사유 (선택)"
            aria-label="거절 사유 (선택)"
            onChange={(event) => setReason(event.target.value)}
          />
          <button
            type="button"
            className="goal-card-choose"
            disabled={busy || approvedCount === 0}
            title={approvedCount === 0 ? "고칠 항목을 하나 이상 선택하세요" : undefined}
            onClick={() =>
              onAnswer({
                status: "granted",
                approvedItemIds: card.items.filter((item) => checked[item.id]).map((item) => item.id),
                choices: effectiveApprovalChoices(card.items, checked, choices),
                preset: card.preset ? (preset ?? card.preset.selected) : undefined,
              })
            }
          >
            선택한 {approvedCount}개 승인
          </button>
          <button
            type="button"
            className="secondary-button"
            disabled={busy}
            onClick={() => onAnswer({ status: "rejected", reason: reason.trim() || undefined })}
          >
            하지 마세요
          </button>
        </div>
      ) : null}
    </section>
  );
}

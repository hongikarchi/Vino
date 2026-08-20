import { useState, type KeyboardEvent } from "react";
import { t } from "../i18n";
import type { AskCard as AskCardData } from "../types";

/**
 * A question the agent needs answered before it can continue, rendered as buttons.
 *
 * This is the affordance for the decisions that used to end a turn as prose ("shall I replace 03B
 * in parallel, then remove the old one?") — a question you cannot click, which cost a typed reply
 * every time. Here the answer IS a click: pressing an option delivers it to the agent as a turn,
 * and the agent, which stopped because it had asked, simply continues.
 *
 * Ctrl/Cmd+Enter accepts the recommended option (or the first, if none is marked), so the common
 * case — "yes, your plan is fine" — needs no reading and no mouse.
 */
interface AskCardProps {
  card: AskCardData;
  busy?: boolean;
  failure?: string;
  onAnswer(optionId: string, note?: string): void;
  /** Clear an ANSWERED card. Nothing else ever emptied the slot, so answered cards never left. */
  onDismiss?(): void;
}

export function AskCard({ card, busy = false, failure, onAnswer, onDismiss }: AskCardProps) {
  const [note, setNote] = useState("");
  const answered = card.status !== "asking";
  const defaultOption = card.options.find((option) => option.recommended) ?? card.options[0];

  const answer = (optionId: string) => {
    if (busy || answered) return;
    onAnswer(optionId, note.trim() || undefined);
  };

  // Ctrl/Cmd+Enter from the note field accepts the default — the whole point is that agreeing is
  // one gesture, whether or not the user wrote a note.
  const handleKey = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "Enter" && (event.ctrlKey || event.metaKey) && defaultOption) {
      event.preventDefault();
      answer(defaultOption.id);
    }
  };

  return (
    <section className="ask-card" aria-label={t("askCardAria")}>
      <header className="goal-card-head">
        <strong>{answered ? t("askAnswered") : t("askHeading")}</strong>
        {answered ? <span className="goal-card-badge">{t("askAnswered")}</span> : null}
        {answered && onDismiss ? (
          <button
            type="button"
            className="chip-remove"
            disabled={busy}
            onClick={onDismiss}
            title={t("cardDismissTitle")}
            aria-label={t("askCloseAria")}
          >
            ×
          </button>
        ) : null}
      </header>
      <p className="ask-card-question">{card.question}</p>
      {card.because ? <p className="ask-card-because">{card.because}</p> : null}
      {failure ? <p className="card-failure" role="alert">{failure}</p> : null}

      {answered ? (
        <ul className="ask-card-answered">
          {card.options.map((option) => (
            <li key={option.id} className={option.id === card.chosenOptionId ? "chosen" : ""}>
              <span aria-hidden="true">{option.id === card.chosenOptionId ? "✔ " : "· "}</span>
              {option.label}
            </li>
          ))}
          {card.note ? <li className="ask-card-note-echo">{t("noteLabel")}: {card.note}</li> : null}
        </ul>
      ) : (
        <>
          <div className="ask-card-options">
            {card.options.map((option) => (
              <button
                key={option.id}
                type="button"
                className={`ask-card-option${option.recommended ? " recommended" : ""}`}
                disabled={busy}
                onClick={() => answer(option.id)}
                title={option.detail ?? undefined}
              >
                <span className="ask-card-option-label">{option.label}</span>
                {option.recommended ? <span className="ask-card-rec">{t("askRecommended")}</span> : null}
                {option.detail ? <span className="ask-card-option-detail">{option.detail}</span> : null}
              </button>
            ))}
          </div>
          <input
            type="text"
            className="approval-reason"
            value={note}
            disabled={busy}
            placeholder={t("askNotePlaceholder")}
            aria-label={t("askNoteAria")}
            onChange={(event) => setNote(event.target.value)}
            onKeyDown={handleKey}
          />
        </>
      )}
    </section>
  );
}

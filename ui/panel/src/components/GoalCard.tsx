import { useState } from "react";
import type { FocusMode, FocusResult, GoalCard as GoalCardData } from "../types";
import { useFocusTarget } from "./useFocusTarget";

/**
 * The goal card: what Vino understood the request to be, shown BEFORE the work starts so
 * correcting it is cheap. Same approve-what-you-saw contract as the audit card — whatever the
 * user confirms here is what the agent is held to, and the closing self-score answers these
 * exact criteria. Options are the structured reply (approve / narrow / correct …); an option
 * carrying objectIds also points the viewport at the geometry it is about, which is the part a
 * plain chat question cannot do.
 */
interface GoalCardProps {
  card: GoalCardData;
  busy?: boolean;
  /** The last failure from answering THIS card, rendered inline so a rejected PUT is visible. */
  failure?: string;
  /**
   * Whether the SESSION is actually running right now. The card's own `status` is a lifecycle
   * (proposing → confirmed → scored), not a runtime state, so a confirmed card claimed "진행 중"
   * forever — including while the session sat idle, which is exactly why the user kept having to
   * ask "끝난 거야, 도는 중이야?". The badge now reports the live session, not the paperwork.
   */
  running?: boolean;
  onAnswer(answer: {
    status: "confirmed" | "rejected";
    chosenOption?: string;
    objective?: string;
    criteria?: string[];
  }): void;
  /** Clear a SETTLED card (confirmed/rejected/scored) — never offered while it is proposing. */
  onDismiss?(): void;
  onFocus?(objectIds: string[], mode: FocusMode, ownerToken?: string): Promise<FocusResult>;
}

/** The badge text for a goal that has been agreed but not yet scored. */
function progressBadge(running: boolean): { label: string; className: string } {
  return running
    ? { label: "진행 중", className: "goal-card-badge running" }
    : { label: "대기 중", className: "goal-card-badge" };
}

export function GoalCard({ card, busy = false, running = false, failure, onAnswer, onDismiss, onFocus }: GoalCardProps) {
  const [editing, setEditing] = useState(false);
  const [objective, setObjective] = useState(card.objective);
  const [criteria, setCriteria] = useState(card.criteria.join("\n"));
  const focus = useFocusTarget(onFocus);

  // Confirmed/scored cards are a record, not a prompt: show the verdict, take no more answers.
  const answered = card.status !== "proposing";
  const progress = progressBadge(running);

  return (
    <section className={`goal-card goal-${card.status}`} aria-label="목표 확인">
      <header className="goal-card-head">
        <strong>{answered ? "목표" : "이렇게 이해했습니다 — 맞나요?"}</strong>
        {card.status === "scored" ? <span className="goal-card-badge">채점됨</span> : null}
        {card.status === "confirmed" ? (
          <span className={progress.className}>{progress.label}</span>
        ) : null}
        {card.status === "rejected" ? <span className="goal-card-badge">거절됨</span> : null}
      </header>
      {failure ? (
        <p className="card-failure" role="alert">{failure}</p>
      ) : null}

      {editing ? (
        <textarea
          className="goal-card-edit"
          value={objective}
          rows={2}
          onChange={(event) => setObjective(event.target.value)}
        />
      ) : (
        <p className="goal-card-objective">{card.objective}</p>
      )}

      <div className="goal-card-block">
        <span className="goal-card-label">이러면 성공</span>
        {editing ? (
          <textarea
            className="goal-card-edit"
            value={criteria}
            rows={Math.max(3, card.criteria.length)}
            onChange={(event) => setCriteria(event.target.value)}
          />
        ) : (
          <ul>
            {(card.scores ?? card.criteria.map((criterion) => ({ criterion, passed: null, evidence: "" }))).map(
              (row: { criterion: string; passed: boolean | null; evidence: string }, index: number) => (
                <li key={index} className={row.passed === null ? "" : row.passed ? "pass" : "fail"}>
                  {row.passed === null ? null : <span aria-hidden="true">{row.passed ? "✔ " : "✘ "}</span>}
                  {row.criterion}
                  {row.evidence ? <span className="goal-card-evidence"> — {row.evidence}</span> : null}
                </li>
              ),
            )}
          </ul>
        )}
      </div>

      {card.assumptions?.length ? (
        <div className="goal-card-block">
          <span className="goal-card-label">이렇게 가정했습니다</span>
          <ul>{card.assumptions.map((item, index) => <li key={index}>{item}</li>)}</ul>
        </div>
      ) : null}

      {card.outOfScope?.length ? (
        <div className="goal-card-block">
          <span className="goal-card-label">이번엔 안 합니다</span>
          <ul>{card.outOfScope.map((item, index) => <li key={index}>{item}</li>)}</ul>
        </div>
      ) : null}

      {!answered ? (
        <div className="goal-card-actions">
          {(card.options ?? [{ id: "approve", label: "이대로 진행" }]).map((option) => (
            <span key={option.id} className="goal-card-option">
              <button
                type="button"
                className="goal-card-choose"
                disabled={busy}
                title={option.detail ?? undefined}
                onClick={() =>
                  onAnswer({
                    status: "confirmed",
                    chosenOption: option.id,
                    objective: editing ? objective : undefined,
                    criteria: editing
                      ? criteria.split("\n").map((line) => line.trim()).filter(Boolean)
                      : undefined,
                  })
                }
              >
                {option.label}
              </button>
              {option.objectIds?.length && onFocus ? (
                <button
                  type="button"
                  className="goal-card-show"
                  disabled={busy}
                  title="이 선택지가 가리키는 객체를 뷰포트에서 보기"
                  onClick={() => void focus.focus(option.id, option.objectIds!, "select")}
                >
                  ◎
                </button>
              ) : null}
            </span>
          ))}
          <button type="button" className="secondary-button" disabled={busy} onClick={() => setEditing((v) => !v)}>
            {editing ? "편집 취소" : "고쳐서 승인"}
          </button>
          <button
            type="button"
            className="secondary-button"
            disabled={busy}
            onClick={() => onAnswer({ status: "rejected" })}
          >
            아니요
          </button>
        </div>
      ) : null}

      {/* A settled card is a record; clearing it frees the shelf. Never shown while proposing —
          the server refuses to delete the live question (400 goal_card_pending). */}
      {answered && onDismiss ? (
        <div className="goal-card-actions">
          <button
            type="button"
            className="secondary-button"
            disabled={busy}
            title="정리된 목표 카드를 닫습니다 — 기록은 대화에 남습니다"
            onClick={onDismiss}
          >
            목표 해제
          </button>
        </div>
      ) : null}
    </section>
  );
}

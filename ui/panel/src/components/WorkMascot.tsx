import type { WorkPhase } from "../workPhase";

// The Vino mascot: a simple round line-drawn character (user's reference: chubby blob, stubby
// limbs, pencil-sketch simplicity), one pose + expression per work phase. CSS-only animation
// (no rAF) and mounted only while working, so a hidden panel with occlusion detection disabled
// never runs a loop for nothing. Face/limbs draw in currentColor so it follows the theme.
export function WorkMascot({ phase, size = 26 }: { phase: WorkPhase; size?: number }) {
  return (
    <svg
      className={`work-mascot phase-${phase}`}
      viewBox="0 0 32 28"
      width={size}
      height={Math.round((size * 28) / 32)}
      aria-hidden="true"
    >
      <g className="m-figure">
        {/* stubby legs behind the body */}
        <path d="M13,21.6 L12.8,25.2" className="m-limb" />
        <path d="M19,21.6 L19.2,25.2" className="m-limb" />
        {/* arms per pose */}
        {phase === "planning" ? (
          // hand up to the chin
          <path d="M8,16.6 Q10.6,18.9 13.2,17.2" className="m-limb" />
        ) : phase === "drafting" ? (
          <>
            <path d="M8.2,15 L6.6,18.2" className="m-limb" />
            <g className="m-swing">
              <path d="M22.6,14.4 L26.6,9.4" className="m-limb" />
            </g>
          </>
        ) : phase === "tidying" ? (
          <>
            <g className="m-wave">
              <path d="M9.4,14.4 L5.4,9.4" className="m-limb" />
            </g>
            <path d="M23.8,15 L25.4,18.2" className="m-limb" />
          </>
        ) : (
          <>
            <path d="M8.2,15 L6.6,18.2" className="m-limb" />
            <path d="M23.8,15 L25.4,18.2" className="m-limb" />
          </>
        )}
        {/* body */}
        <circle cx="16" cy="12.8" r="9.4" className="m-body" />
        {/* face */}
        {phase === "drafting" || phase === "tidying" ? (
          <>
            {/* happy closed-arc eyes */}
            <path d="M11.4,11.8 Q12.7,10 14,11.8" className="m-line" />
            <path d="M18,11.8 Q19.3,10 20.6,11.8" className="m-line" />
          </>
        ) : (
          <>
            <circle cx="12.9" cy="11.4" r="1.05" className="m-dot m-pupil" />
            <circle cx="19.1" cy="11.4" r="1.05" className="m-dot m-pupil" />
          </>
        )}
        {phase === "verifying" || phase === "trouble" ? (
          <>
            {/* determined brows + a worried bead of sweat (trouble reuses the face; the red
                tint and the halted walk carry the difference) */}
            <path d="M11.3,9 L14.1,9.9" className="m-line" />
            <path d="M20.7,9 L17.9,9.9" className="m-line" />
            <path d="M14.7,16 L17.3,16" className="m-line" />
            <path d="M26,9.6 Q27.2,11.3 26,12.3 Q24.8,11.3 26,9.6" className="m-sweat" />
          </>
        ) : null}
        {phase === "planning" ? <path d="M14.7,15.8 L17.3,15.8" className="m-line" /> : null}
        {phase === "reading" ? <circle cx="16" cy="15.7" r="0.95" className="m-line" fill="none" /> : null}
        {phase === "drafting" ? <path d="M14.2,14.9 Q16,17.2 17.8,14.9 Z" className="m-mouth" /> : null}
        {phase === "tidying" ? <path d="M13.6,14.6 Q16,18.2 18.4,14.6 Z" className="m-mouth" /> : null}
      </g>
      {/* phase props outside the bobbing figure */}
      {phase === "planning" ? (
        <g className="m-q">
          <path d="M25.2,3.4 Q27.9,2.2 27.5,4.6 Q27.2,6.1 25.9,6.7" className="m-line" />
          <circle cx="26" cy="8.9" r="0.75" className="m-dot" />
        </g>
      ) : null}
      {phase === "tidying" ? (
        <g className="m-spark">
          <path d="M26.4,3.6 L28.4,2.2" className="m-line" />
          <path d="M27.2,6.4 L29.6,6" className="m-line" />
        </g>
      ) : null}
    </svg>
  );
}

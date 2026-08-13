# Heavy-script reliability plan — 2026-08-13

Design converged with the user on 2026-08-13 (chat). Four user requests started this; request 1
(data-tab GH zoom) is already implemented on `reliability-2026-08-11` and awaits its live gate.
This plan covers the remaining three, which collapse into one problem: **model-authored C# scripts
that freeze Rhino's UI thread**, and the authoring workflow around them.

Confirmed decisions (user-approved):

- Watchdog = **server-side injection** at dispatch, not a "reject if guard missing" rule. C# first;
  Python deferred (needs AST rewrite).
- Injected guard block is **visible** at the top of the source with explicit markers (user liked it
  as proof-of-plugin); stripped only on **model-facing** reads.
- Strip/inject lives at the shared boundary, idempotent (strip-then-inject on every write).
- Consolidation of staged scripts = **time-cap driven, mechanical merge** (no LLM re-authoring),
  block-structured output, ID-addressed block edits, reversible split.
- Defense layers: ① slider-product gate (exists) → ② measured input-volume gate → ③ watchdog →
  ④ bridge timeout (diagnosis + halt; stays 45s until telemetry justifies 30s).
- house-rules self-guard recipe is replaced by timeout-response guidance; the unbounded-loop
  backstop (`PreflightSourceBudgetGuard`) stays (zero cost, pre-write, covers unparsable sources).

Decisions — CONFIRMED by the user 2026-08-13:

| # | Decision | Confirmed value |
|---|----------|-----------------|
| D1 | Watchdog deadline | 30 000 ms |
| D2 | Predicted-time gate threshold (W2) | block > 20s (warn band 5–20s) |
| D3 | Merge time cap (W3) | 2s measured per merged component |
| D4 | Merge initiation (W3) | model proposes after stages verified; old-chain cleanup verification-gated, own ChangeSet |
| D5 | Bridge budget | stays 45s (no reduction; former Wave 4 dropped) |

---

## Wave 1 — C# watchdog injection (the acute fix)

**STATUS 2026-08-13: implemented AND live-gated PASS (docs/live-gate-2026-08-13.md) — fired at
30,025 ms with clean Failed, byte-exact strip, sub-budget scripts unaffected. Wave 0 (GH zoom)
live-gated PASS in the same run. Deployed to the installed package.** Implementation notes vs. the spec below: the real source shape is script-mode
top-level statements (gh-csharp-cookbook.md — no class/RunScript wrapper), so the prologue is two
top-level locals that re-run (and thus reset) on every solve; checks are injected only where those
locals are in scope (type-declaration bodies are skipped — a check there would not compile); the
sampled check reads the stopwatch every 16th iteration; single-statement loop bodies are wrapped in
a marker-tagged block whose injector tokens all end their own lines, so a reparse of the stored
text re-attaches user trivia to user nodes and Strip stays byte-exact (regression-tested);
sourceSha256 and component fingerprints deliberately keep their stored-state (guarded) values in
model projections — they are opaque CAS tokens the adapter compares against the stored source.

Goal: any model-authored C# script self-aborts at the deadline with a clean runtime error instead
of freezing Rhino. Nothing outside the script can stop a running solve, so the trigger must live
inside the code (user's "hidden trigger" idea, made mechanical).

- **W1-1 Injector** — new `GPTino.AgentHost/Runtime/CSharpWatchdogInjector.cs`, Roslyn
  (`Microsoft.CodeAnalysis.CSharp` package, new AgentHost dependency). Pure static transform,
  unit-testable without a document:
  - Prologue at script top inside `// <gptino:guard v1>` … `// </gptino:guard>` markers: stopwatch
    + counter declarations + short comment explaining the guard to a human reader.
  - Check statement injected as the FIRST statement of: every `for`/`while`/`foreach`/`do` body
    (brace single-statement bodies), every method/local-function statement body, every statement
    lambda (covers `Parallel.For` bodies; a throw there aggregates and still aborts the solve).
  - Sampled check to bound overhead: `if (((++__gptino_i) & 15L) == 0L && __gptino_sw.ElapsedMilliseconds
    > BUDGET) throw new System.TimeoutException("GPTino solve budget (…ms) exceeded - reduce the
    workload or split this stage.");` — every inline statement carries a `/*gptino:guard*/` token.
  - **Strip** = remove marker block + every statement carrying the token. `Strip(Inject(s)) == s`
    and `Inject(Strip(Inject(s))) == Inject(s)` are the invariants; corrupted markers ⇒ treat the
    whole text as model-owned and re-inject clean on next write.
  - Parse failure ⇒ inject nothing (the source cannot compile; the execute surfaces the compile
    error as today). Never block on injection failure.
  - Known v1 gap (documented): expression-lambda LINQ bodies are not instrumented. Runaway deep
    recursion → StackOverflow still kills the process (uncatchable in .NET); recorded as residual risk.
- **W1-2 Dispatch hook** — alongside the existing server-owned rewrites
  (`InjectRhinoUpsertSourceDocKey` LiveDocumentBackend.cs:2226, `InjectWireDeferSolve` :2268):
  `InjectCSharpWatchdog(preparedOperations, options)` rewrites `python.setSource` arguments where
  `runtime == "csharp"`. Same contract as the precedents: dispatched Arguments only, FrozenPayload
  (idempotency hash) untouched.
- **W1-3 Strip on model-facing reads** — wherever source text is projected for the model
  (`script:<guid>` read scope, any job result echoing source). Panel/human paths untouched — the
  guard is meant to be seen in the GH editor.
- **W1-4 Config** — `AgentHostOptions.ScriptWatchdogMilliseconds` (default per D1); validated
  < bridge budget.
- **W1-5 Instructions** — house-rules.md: replace the self-limiting-guard recipe (§"Self-limiting
  budget guard") with: the server injects the watchdog; on a `GPTino solve budget` runtime error do
  NOT resubmit as-is — reduce workload / split the stage / lower resolution. Backstop text stays.
- **W1-6 Tests** — injector round-trip/idempotence over loop forms, methods, lambdas, nested
  cases, parse-fail passthrough; marker-corruption recovery; dispatch test (csharp-only rewrite,
  FrozenPayload stable); strip-projection test.
- **W1-7 Live gate (dev-mode Rhino)** — (a) deliberately heavy loop self-aborts ≈ deadline,
  Rhino stays responsive, job fails clean (Failed, not recoveryRequired), runtime message reaches
  the model; (b) read-back is byte-clean model text; (c) guard visible in GH editor;
  (d) regression: normal scripts and Parallel.For scripts behave identically.
- Before W1-1 freezes: **live-probe the real Rhino 8 C# script component source shape** (usings /
  class wrapper / RunScript signature) so the injector handles the actual format.

## Wave 2 — measurement-driven cost gate

Goal: upgrade the pre-execution gate from "slider-product guess" to "measured volume × calibrated
time", per the 08-13 discussion.

- **W2-1 Component measurement table** — persist per component: last solve duration (op_duration
  already measured), last per-socket input/output DataCounts (Verify already runs
  `inspect_outputs` on every committed job — record what it sees). Restart-safe small store.
- **W2-2 Live input volume at preflight** — prefer the table's Verify-fresh counts for upstream
  components; cap any live `inspect_outputs` fallback; unknown ⇒ conservative (existing slider
  gate + first-solve ceiling still apply).
- **W2-3 Predicted-time gate** — predicted = last duration × (current volume / last volume);
  thresholds per D2. Assumes ~linear scaling; superlinear code is the watchdog's job. The forced
  cheap first solve (existing 10k ceiling) doubles as the calibration probe.
- **W2-4 Cost declaration** — optional `costExpr` (symbolic, e.g. `"x_count * y_count"`) +
  `costOps` on execute payloads; server substitutes MEASURED counts (model never guesses absolute
  n); static plausibility check vs loop-nesting arity; instruction paragraph teaching it.
- **W2-5 Weights v0** — curated SDK cost buckets (~ns vector / ~µs curve / ~ms intersection-offset /
  10ms+ boolean-fitting / ~s NetworkSrf-class); log (declared ops, measured counts, measured
  duration) triples for future regression; per-machine microbenchmark calibration via the dev-mode
  bench loop = optional follow-up, not a blocker.

## Wave 3 — consolidation lifecycle (merge / block edit / split)

Goal: author fine-grained (cheap verify, fresh budgets), then mechanically merge stable chains under
a time cap — moving seams from between components (sockets/wires, the #1 measured failure surface:
fingerprint conflicts in the 199-job analysis) to inside components (atomic ID-addressed blocks).

- **W3-1 Stage markers + merge metadata** — `// <stage:NAME>` blocks; per-block consumed/produced
  variable interface recorded as metadata.
- **W3-2 Mechanical merger** — server-side deterministic transform (no LLM text): topological
  concat, output→local-variable substitution, collision rename (`s1_`-style), socket schema =
  external inputs ∪ final outputs. Exposed as a host tool (`consolidate_stages`) taking a stage
  group; group must satisfy the D3 cap using W2-1 measured durations, and must respect seam rules
  (never merge across a user-slider boundary, a heavy checkpoint stage, or a multi-consumer output).
- **W3-3 Equivalence verification** — execute merged component; compare `inspect_outputs`
  (DataCount/types/bounds) against the recorded chain outputs; only then delete old stages, in
  their own ChangeSet per cleanup discipline. Mismatch ⇒ discard merged, chain untouched.
- **W3-4 Block-replace op** — `python.replaceBlock {componentId, blockId, source}`: ID-addressed
  (no content matching ⇒ no patch failure mode), parse-level interface validation (declared outputs
  still assigned), whole-component fingerprint semantics unchanged, watchdog re-injected over the
  recomposed source. Full setSource stays as the fallback/big-edit path.
- **W3-5 Reversible split** — un-merge deterministically from markers + metadata when an edit
  outgrows block boundaries.
- **W3-6 Instructions** — seam-placement rule (component boundaries only where they earn their
  cost) + consolidation workflow.

## Wave 0 closeout (parallel, small)

Data-tab GH zoom (implemented 08-13): publish-deploy the full DLL set (dev-mode lesson — never
single-DLL hotswap), live-gate reference-click → parameter framing and bake-click → bake_manager
framing (needs a re-bake to acquire `gptino_bake_component` stamps; legacy bakes stay Rhino-only).

## Order & dependencies

W1 is independent and first (acute pain) — done at the code level; its live gate rides the next
deploy together with the Wave 0 closeout. W2-1's measurement table is a prerequisite for W3's cap
planner — keep that ordering.

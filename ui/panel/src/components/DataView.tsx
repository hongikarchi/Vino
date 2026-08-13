import { useEffect, useState } from "react";
import type {
  CanvasFocusResult,
  DataFlowDetail,
  DocDataFlow,
  FocusResult,
  GrasshopperDocInfo,
} from "../types";
import { Icon } from "./Icons";

interface DataViewProps {
  /** Registered GH docs; null on a legacy single-doc server. */
  docs: GrasshopperDocInfo[] | null;
  /** Per-doc summaries pushed with the runtime snapshot (counts only). */
  summaries: DocDataFlow[];
  /** Stamped bakes whose source document predates provenance stamping. */
  unattributedBakeCount: number;
  rhinoFile: string;
  grasshopperFile: string;
  getDetail(docId?: string | null): Promise<DataFlowDetail>;
  /** Select + zoom the given Rhino objects in the viewport (reference IDs are clickable). */
  onSelectRhino?(objectIds: string[]): Promise<FocusResult>;
  /** Select + frame the GH-canvas counterpart of a clicked row (referencing parameter, baking
   *  component), so a data click zooms both viewports, not only Rhino. */
  onSelectCanvas?(objectIds: string[], docId?: string | null): Promise<CanvasFocusResult>;
}

const shortId = (id: string) => id.slice(0, 8);
const shortFile = (path: string) => path.split(/[\\/]/).pop() ?? path;

/**
 * The Data tab: what Grasshopper reads from the Rhino document and what it writes back.
 * Summaries ride the runtime snapshot (never a second source of truth); the per-parameter and
 * per-family detail is fetched on demand and stamped with the revision it was observed at.
 */
export function DataView({
  docs,
  summaries,
  unattributedBakeCount,
  rhinoFile,
  grasshopperFile,
  getDetail,
  onSelectRhino,
  onSelectCanvas,
}: DataViewProps) {
  const [selectedDocId, setSelectedDocId] = useState<string | null>(docs?.[0]?.id ?? null);
  const [detail, setDetail] = useState<DataFlowDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [reloadKey, setReloadKey] = useState(0);
  // Reference groups start collapsed so a definition with many referenced objects doesn't force a
  // long scroll; the user opens the ones they care about. Keyed by parameter id.
  const [openGroups, setOpenGroups] = useState<Set<string>>(() => new Set());
  const toggleGroup = (key: string) =>
    setOpenGroups((current) => {
      const next = new Set(current);
      next.has(key) ? next.delete(key) : next.add(key);
      return next;
    });

  // The outcome of the last select/zoom click. The server reports how many objects it actually
  // selected and how many no longer exist — without this a bake group whose objects were all
  // deleted zoomed to nothing with zero feedback, and a failure vanished into a voided promise.
  const [focusNote, setFocusNote] = useState<string | null>(null);
  const errorText = (cause: unknown) => (cause instanceof Error ? cause.message : String(cause));
  // A data click zooms BOTH viewports: the Rhino objects behind the row, and (when the row has a
  // canvas counterpart — the referencing parameter or the baking component) the GH canvas. The two
  // calls fail independently, so the note reports each side rather than letting one hide the other.
  const selectRhino = (objectIds: string[], canvasIds: string[] = []) => {
    const jobs: Promise<string>[] = [];
    if (onSelectRhino) {
      jobs.push(
        onSelectRhino(objectIds).then(
          (result) =>
            `Selected ${result.selectedCount}${result.missingCount > 0 ? ` · ${result.missingCount} missing` : ""}`,
          (cause) => `Selection failed: ${errorText(cause)}`,
        ),
      );
    }
    if (onSelectCanvas && canvasIds.length > 0) {
      jobs.push(
        onSelectCanvas(canvasIds, detail?.docId ?? selectedDocId).then(
          (result) =>
            result.framed !== false
              ? `GH: framed ${result.selectedCount}`
              : result.missingCount >= canvasIds.length
                ? "GH: component missing"
                : `GH: ${result.skipReason ?? "not framed"}`,
          (cause) => `GH: ${errorText(cause)}`,
        ),
      );
    }
    if (jobs.length === 0) return;
    void Promise.all(jobs).then((parts) => setFocusNote(parts.join(" · ")));
  };

  // Follow the doc set: a Save As or a closed document must not leave the view pointed at a
  // document the runtime no longer knows.
  useEffect(() => {
    if (docs == null) return;
    if (selectedDocId == null || !docs.some((doc) => doc.id === selectedDocId)) {
      setSelectedDocId(docs[0]?.id ?? null);
    }
  }, [docs, selectedDocId]);

  useEffect(() => {
    let disposed = false;
    setLoading(true);
    setError(null);
    setFocusNote(null);
    getDetail(selectedDocId)
      .then((payload) => {
        if (disposed) return;
        setDetail(payload);
        setLoading(false);
      })
      .catch((cause) => {
        if (disposed) return;
        setError(cause instanceof Error ? cause.message : String(cause));
        setLoading(false);
      });
    return () => {
      disposed = true;
    };
  }, [selectedDocId, getDetail, reloadKey]);

  const references = detail?.references;
  const bakes = detail?.bakes;
  const ownGroups = (bakes?.groups ?? []).filter(
    (group) =>
      group.sourceDocKey != null &&
      detail?.docId != null &&
      group.sourceDocKey.toLowerCase() === detail.docId.toLowerCase(),
  );
  const foreign = (bakes?.groups ?? [])
    .filter(
      (group) =>
        group.sourceDocKey != null &&
        (detail?.docId == null || group.sourceDocKey.toLowerCase() !== detail.docId.toLowerCase()),
    )
    .reduce((total, group) => total + group.count, 0);
  const brokenTotal = summaries.reduce((total, flow) => total + flow.missingReferenceCount, 0);

  return (
    <div className="data-view">
      <header className="data-view-header">
        <div className="data-view-flow" aria-label="Document data flow">
          <span className="data-view-doc">{shortFile(rhinoFile)}</span>
          <span className="data-view-arrow" title="Rhino objects referenced by Grasshopper">⇢</span>
          <span className="data-view-doc">
            {docs?.find((doc) => doc.id === selectedDocId)?.file
              ? shortFile(docs.find((doc) => doc.id === selectedDocId)!.file)
              : shortFile(grasshopperFile)}
          </span>
          <span className="data-view-arrow" title="Objects baked back into Rhino">⇠</span>
        </div>
        <div className="data-view-actions">
          {detail?.revision != null ? (
            <span className="audit-card-meta">as of r{detail.revision}</span>
          ) : null}
          <button
            type="button"
            className="secondary-button"
            onClick={() => setReloadKey((key) => key + 1)}
            disabled={loading}
          >
            Rescan
          </button>
        </div>
      </header>

      {docs != null && docs.length > 1 ? (
        <div className="data-view-docs" role="tablist" aria-label="Grasshopper documents">
          {docs.map((doc) => {
            const summary = summaries.find((flow) => flow.docId === doc.id);
            return (
              <button
                key={doc.id}
                type="button"
                role="tab"
                aria-selected={doc.id === selectedDocId}
                className={`secondary-button${doc.id === selectedDocId ? " active" : ""}`}
                onClick={() => setSelectedDocId(doc.id)}
              >
                {shortFile(doc.file)}
                {summary ? (
                  <span className={summary.missingReferenceCount > 0 ? "data-view-chip warning" : "data-view-chip"}>
                    ⇢{summary.referenceCount} ⇠{summary.bakeCount}
                    {summary.missingReferenceCount > 0 ? ` · ${summary.missingReferenceCount}!` : ""}
                  </span>
                ) : null}
              </button>
            );
          })}
        </div>
      ) : null}

      {brokenTotal > 0 ? (
        <p className="data-view-alert">
          {brokenTotal} broken reference{brokenTotal === 1 ? "" : "s"}: a definition points at Rhino
          objects that no longer exist, so those components emit empty data with no error.
        </p>
      ) : null}

      {focusNote ? (
        <p className="archive-note" role="status">
          {focusNote}
        </p>
      ) : null}

      <div className="data-view-body">
        {loading ? <p className="archive-note">Reading references and bakes…</p> : null}
        {error ? <p className="archive-error">{error}</p> : null}
        {!loading && !error && detail?.writerActive ? (
          <p className="archive-note">{detail.message ?? "A writer session holds the document; retry shortly."}</p>
        ) : null}

        {!loading && !error && references ? (
          <section className="data-drawer-section">
            <h3>
              References <span className="data-drawer-count">⇢{references.referenceCount}</span>
              {references.missingCount > 0 ? (
                <span className="data-drawer-count warning"> · {references.missingCount} broken</span>
              ) : null}
            </h3>
            {references.parameters.length === 0 ? (
              <p className="archive-note">This definition references no Rhino objects.</p>
            ) : (
              <ul className="data-drawer-list">
                {references.parameters.map((parameter) => {
                  const groupKey = `ref-${parameter.parameterId}`;
                  const open = openGroups.has(groupKey);
                  const liveIds = parameter.objects.filter((object) => object.exists).map((object) => object.rhinoObjectId);
                  return (
                    <li key={parameter.parameterId} className="data-group">
                      <button
                        type="button"
                        className="data-group-head"
                        aria-expanded={open}
                        onClick={() => toggleGroup(groupKey)}
                      >
                        <Icon name="chevron" className={`data-group-caret ${open ? "open" : ""}`} width={12} height={12} />
                        <span className="data-drawer-param">
                          {parameter.nickName || parameter.parameterType}
                          <span className="data-drawer-type"> {parameter.parameterType}</span>
                        </span>
                        <span className="data-group-count">{parameter.objects.length}</span>
                      </button>
                      {open ? (
                        <ul className="data-group-body">
                          {onSelectRhino && liveIds.length > 1 ? (
                            <li className="data-group-actions">
                              <button
                                type="button"
                                className="data-id-button"
                                title="Select and zoom all existing objects in this group, and frame the referencing parameter in Grasshopper"
                                onClick={() => selectRhino(liveIds, [parameter.parameterId])}
                              >
                                Select all {liveIds.length}
                              </button>
                            </li>
                          ) : null}
                          {parameter.objects.map((object) => (
                            <li key={object.rhinoObjectId} className={object.exists ? undefined : "data-drawer-missing"}>
                              {object.exists && onSelectRhino ? (
                                <button
                                  type="button"
                                  className="data-id-button"
                                  title="Select and zoom this object in Rhino, and frame the referencing parameter in Grasshopper"
                                  onClick={() => selectRhino([object.rhinoObjectId], [parameter.parameterId])}
                                >
                                  <code>{shortId(object.rhinoObjectId)}</code>
                                </button>
                              ) : (
                                <code>{shortId(object.rhinoObjectId)}</code>
                              )}{" "}
                              {object.exists ? object.layerFullPath ?? "" : "missing — referenced object was deleted"}
                            </li>
                          ))}
                        </ul>
                      ) : null}
                    </li>
                  );
                })}
              </ul>
            )}
          </section>
        ) : null}

        {!loading && !error && bakes ? (
          <section className="data-drawer-section">
            <h3>
              Bakes{" "}
              <span className="data-drawer-count">
                ⇠{ownGroups.reduce((total, group) => total + group.count, 0)}
              </span>
            </h3>
            {ownGroups.length === 0 ? (
              <p className="archive-note">No stamped bakes from this definition yet.</p>
            ) : (
              <ul className="data-drawer-list">
                {ownGroups.map((group) => {
                  // Tolerate a legacy server that predates the objectIds field.
                  const ids = group.objectIds ?? [];
                  const label = (
                    <>
                      <span className="data-drawer-param">{group.bakeFamily ?? "(no family)"}</span>{" "}
                      {group.count} object{group.count === 1 ? "" : "s"}
                    </>
                  );
                  return (
                    <li key={`${group.sourceDocKey}|${group.bakeFamily}`}>
                      {onSelectRhino && ids.length > 0 ? (
                        <button
                          type="button"
                          className="data-id-button"
                          title={`Select and zoom this bake group in Rhino${
                            (group.sourceComponentIds?.length ?? 0) > 0
                              ? ", and frame its baking component in Grasshopper"
                              : ""
                          }${
                            // The server caps the ids it ships per group (50), so say so.
                            ids.length < group.count ? ` — zooms first ${ids.length} of ${group.count}` : ""
                          }`}
                          onClick={() => selectRhino(ids, group.sourceComponentIds ?? [])}
                        >
                          {label}
                        </button>
                      ) : (
                        label
                      )}
                    </li>
                  );
                })}
              </ul>
            )}
            {foreign > 0 ? (
              <p className="archive-note">
                {foreign} tracked bake{foreign === 1 ? "" : "s"} belong to other or re-keyed
                definitions (a Save As re-keys the document; re-bake to re-attribute).
              </p>
            ) : null}
            {unattributedBakeCount > 0 ? (
              <p className="archive-note">
                {unattributedBakeCount} tracked bake{unattributedBakeCount === 1 ? "" : "s"} predate
                provenance stamping (unattributed — re-bake to attribute).
              </p>
            ) : null}
          </section>
        ) : null}
      </div>
    </div>
  );
}

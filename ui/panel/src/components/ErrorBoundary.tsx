import { Component, type ErrorInfo, type ReactNode } from "react";
import { t } from "../i18n";

interface ErrorBoundaryProps {
  /** Shown in place of the failed subtree — keep it small; the rest of the panel stays alive. */
  fallback?: ReactNode;
  children: ReactNode;
}

interface ErrorBoundaryState {
  failed: boolean;
}

/**
 * Contains a render crash to its own subtree. A single malformed card — a field the panel expected
 * but a newer or older host did not send — used to throw during render and, with no boundary
 * anywhere in the tree, blanked the ENTIRE panel. Wrapping each card (and the app root) means the
 * worst case is one card that cannot draw, not a white screen with no way back.
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { failed: false };

  static getDerivedStateFromError(): ErrorBoundaryState {
    return { failed: true };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error("A panel subtree failed to render.", error, info);
  }

  render(): ReactNode {
    if (this.state.failed) {
      return (
        this.props.fallback ?? (
          <div className="render-error" role="alert">
            {t("errorItemFallback")}
          </div>
        )
      );
    }
    return this.props.children;
  }
}

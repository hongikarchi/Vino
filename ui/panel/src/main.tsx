import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import { ErrorBoundary } from "./components/ErrorBoundary";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <ErrorBoundary
      fallback={
        <div className="render-error render-error-root" role="alert">
          패널을 표시하는 중 문제가 발생했습니다. 새로고침하거나 Rhino에서 패널을 다시 열어 주세요.
        </div>
      }
    >
      <App />
    </ErrorBoundary>
  </StrictMode>,
);

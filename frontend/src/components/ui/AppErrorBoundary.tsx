import { Component, type ErrorInfo, type ReactNode } from "react";

type BoundaryState = { error: Error | null };

export class AppErrorBoundary extends Component<{ children: ReactNode }, BoundaryState> {
  state: BoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): BoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error("Wukna encountered a rendering error", error, info.componentStack);
  }

  render() {
    if (this.state.error) {
      return (
        <main className="wk-app-error" role="alert">
          <h1>This screen could not be opened</h1>
          <p>Something went wrong while opening this screen. Reload Wukna to try again.</p>
          <button type="button" onClick={() => window.location.reload()}>Reload Wukna</button>
        </main>
      );
    }
    return this.props.children;
  }
}

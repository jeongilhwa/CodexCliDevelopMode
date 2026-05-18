# InterceptKit Design

Date: 2026-05-18
Project: InterceptKit
Scope: Separate reusable interception library plus MAUI demo app, designed for future Windows, Android, iOS, and Mac expansion

## Goal

Create a new project in this repository that reproduces the Burp-style workflow inside an app-owned WebView:

- pause outgoing requests before they are sent
- inspect and edit request details
- decide between forward and drop
- record response details when the platform supports it

The first working implementation targets Windows WebView2. The structure must clearly preserve future expansion points for Android, iOS, Mac, and app-level HttpClient interception.

## Approved Decisions

- Build a separate project instead of modifying the current CodexDesk app directly.
- Use a reusable library plus a demo app, not a demo app only.
- Treat WebView interception as v1.
- Leave HttpClient interception as a planned v2 expansion point with placeholder files and comments.
- Keep the design compatible with future Android, iOS, and Mac platform adapters.

## Product Shape

The new work is a small solution area under `src/` with one shared interception core, platform-specific adapters, and a MAUI Blazor Hybrid demo app.

The demo app is not the architecture center. It is only a host that exercises the reusable interception contracts and shows the queue/editor/decision workflow.

## Project Structure

The initial structure should be:

- `src/InterceptKit.Core`
  Shared domain contracts and state management with no platform-specific WebView code.
- `src/InterceptKit.WebView`
  Shared WebView-facing abstractions for request pause, resume, mutation, and response capture.
- `src/InterceptKit.WebView.Windows`
  Windows WebView2 implementation for real interception in v1.
- `src/InterceptKit.Http`
  Placeholder project for future HttpClient interception via handler-based plumbing.
- `src/InterceptKit.App`
  MAUI Blazor Hybrid demo app that hosts the WebView and operator UI.
- `src/InterceptKit.Tests`
  Tests for the core state machine, decision flow, and non-UI support code.

## Core Model

`InterceptKit.Core` owns the contracts that must remain stable across platforms:

- `InterceptedRequest`
  Immutable snapshot of the paused request at capture time.
- `EditableInterceptedRequest`
  Mutable draft used by the UI before a decision is submitted.
- `InterceptedResponse`
  Optional response snapshot attached after the request is forwarded.
- `InterceptDecision`
  Result type representing `ForwardOriginal`, `ForwardModified`, or `Drop`.
- `InterceptSession`
  Aggregate that tracks one intercepted request from pending state through resolution.
- `InterceptSessionStatus`
  Lifecycle states such as `Pending`, `Forwarded`, `Dropped`, `Completed`, `Failed`.
- `IInterceptSessionStore`
  Queue-facing contract for adding, updating, resolving, and querying sessions.
- `IInterceptDecisionSink`
  Platform callback contract used by adapters to wait for the operator decision.

The core must not know how WebView2, Android WebView, WKWebView, or HttpClient actually continue or cancel traffic. It only knows request state, edits, decisions, and recorded results.

## Interception Flow

The v1 request lifecycle is:

1. A platform adapter receives a pause-capable request event.
2. The adapter converts the raw event into an `InterceptedRequest`.
3. The adapter creates an `InterceptSession` in the shared store with `Pending` status.
4. The demo UI renders the pending queue and lets the operator inspect or edit the request.
5. The operator chooses `Forward` or `Drop`.
6. The adapter consumes the decision and either:
   - continues the original request
   - continues a modified request
   - fails or cancels the request
7. If the request is forwarded and response capture is available, the adapter records the response into the same session.
8. The session transitions to its final state and remains queryable for history.

This separation is the main architectural boundary. The core owns session truth. The platform adapter owns the actual transport-side continue or fail behavior.

## WebView Layer

`InterceptKit.WebView` is the shared abstraction layer between the core and platform adapters.

Expected contents:

- `IWebViewInterceptController`
  Start, stop, and configure interception for a specific WebView host.
- `WebViewInterceptOptions`
  Flags for request interception, response capture, body limits, and filtering.
- `PausedWebViewRequestContext`
  Platform-neutral wrapper describing a request that can still be continued or dropped.
- `IWebViewInterceptPlatformAdapter`
  Contract implemented by Windows now, and Android/iOS/Mac later.

This project exists to prevent the Windows implementation from leaking WebView2-specific details into the core contracts.

## Windows v1 Implementation

`InterceptKit.WebView.Windows` is the only fully implemented adapter in the first iteration.

Implementation direction:

- use WebView2 DevTools Protocol interception primitives
- pause requests before they leave the WebView
- extract request method, URL, headers, and body when available
- allow continue with original or modified request data
- allow fail/drop behavior
- capture response metadata when practical

The current CodexDesk code already proves that WebView2 DevTools event wiring is viable for network capture. This new project should reuse that knowledge but move it into a cleaner adapter boundary instead of extending the existing page code.

## Future Platform Expansion

The repository should contain placeholder files and comments for:

- `InterceptKit.WebView.Android`
- `InterceptKit.WebView.iOS`
- `InterceptKit.WebView.MacCatalyst`

These placeholders should explain the expected native hook points:

- Android: `WebViewClient` / request interception path
- iOS and MacCatalyst: `WKWebView`-related navigation or custom scheme/network interception strategy

Not every platform will expose the exact same pause/mutate capability as WebView2. The shared contracts should therefore express capability flags instead of assuming all platforms can do full request-body mutation.

## HttpClient v2 Placeholder

`InterceptKit.Http` is intentionally not implemented in v1, but the project and file layout should exist.

Placeholder contents should document:

- future `DelegatingHandler` interception path
- mapping from `HttpRequestMessage` and `HttpResponseMessage` into the core model
- the need to handle non-UI decisions if requests originate outside the WebView

This project is a marker for planned scope, not a working transport in the first pass.

## Demo App Design

`InterceptKit.App` should be a .NET MAUI Blazor Hybrid app because that matches the repository conventions and keeps the future UI close to the intended stack.

The demo app UI should include:

- embedded WebView host
- interception toggle
- pending queue list
- request detail panels for method, URL, headers, and body
- editable request draft view
- `Forward` and `Drop` actions
- resolved history list or status view

The operator experience should resemble Burp's core loop, but it does not need to clone Burp's full multi-tool layout. The first goal is a reliable pause-edit-decide flow.

## Error Handling and Constraints

Design constraints for v1:

- if a request cannot be modified safely, the UI should still allow `Forward original` or `Drop`
- if body extraction is unavailable, show that clearly instead of pretending the body is empty
- if the platform adapter fails during continue or drop, mark the session as failed and surface the adapter error
- cap stored body size to avoid unbounded memory growth
- isolate platform exceptions from the core queue so one bad request does not poison the whole session store

## Testing Strategy

`InterceptKit.Tests` should focus on deterministic logic first.

Test targets:

- session creation from a paused request
- queue ordering and lookup
- request edit application rules
- decision transitions for forward original, forward modified, and drop
- invalid transition handling
- capability flag behavior for platforms that cannot support all edits

The first pass does not require automated end-to-end WebView interception tests across all platforms. Windows adapter testing can begin with narrower support-code tests while the demo app is used for manual verification.

## Non-Goals for v1

- app-wide HttpClient interception
- cross-platform working implementations beyond Windows
- full Burp feature parity such as repeater, intruder, or passive scanning
- persistence, export, or session replay unless needed to support the core request workflow
- modifying the existing CodexDesk UI as the primary delivery path

## Initial File Plan

The first implementation pass should create:

- a new solution area or solution entries for the InterceptKit projects
- placeholder platform projects and folders for Android, iOS, and Mac expansion
- placeholder HttpClient project and comments
- Windows WebView2 adapter skeleton
- shared core models and store
- MAUI demo app shell with empty or partial interception workspace
- initial tests for the core session flow

## Summary

This design keeps the first working scope narrow, but it leaves unmistakable structure for future expansion. Windows WebView2 becomes the proving ground for real-time WebView interception, while the core contracts and placeholder projects preserve the path toward Android, iOS, Mac, and HttpClient support without forcing those implementations into v1.

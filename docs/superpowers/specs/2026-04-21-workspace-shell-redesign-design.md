# Workspace Shell Redesign

Date: 2026-04-21
Project: CodexDesk.App
Scope: Main MAUI Blazor workspace UI refresh for desktop-first use

## Goal

Reframe the current home screen into a workbench-style shell that feels closer to Visual Studio or a modern browser workspace than a landing page. The new shell should remove promotional or explanatory chrome from the main view, keep the active conversation front and center, use moderate acrylic blur, and move secondary tools into docked side panels.

## User Intent

- Keep the main screen focused on working, not on explaining the app
- Add a stronger tab-oriented workflow similar to Visual Studio or a web browser
- Preserve split-view style capabilities, but make tabs the primary mental model
- Use a moderate glass or blur effect so the background softly shows through without hurting readability
- Remove unnecessary titles and move secondary controls off the center stage

## Proposed Direction

The app will use a three-region shell:

- Left dock: narrow tool rail for workspace sections such as settings, macros, browser tools, and utilities
- Center workspace: primary document area with tab strip, transcript, composer, and run-state feedback
- Right context panel: collapsible detail surface for the currently selected tool or context

Both side panels should default to a compact or hidden state so the central tab workspace remains dominant.

## Information Architecture

### Center Workspace

The center region becomes the default focus target and should contain:

- A slim top workspace bar instead of the current hero section
- A document-style tab strip with active, busy, new-tab, and close affordances
- The active conversation transcript
- Prompt composer and send controls
- Lightweight inline run feedback, not a large status card

The current large title, descriptive intro text, and duplicate status controls will be removed from the main flow.

### Left Dock

The left dock owns navigation to non-primary tools. It should be visually compact and icon-first. Expected entries:

- Session or workspace controls
- Prompt macros
- Naver Talk tools
- External tools or shell actions

Selecting a dock item should open either the left panel itself or the right context panel depending on the item, but should not displace the center workspace.

### Right Context Panel

The right panel holds detail-heavy or form-heavy UI that should not compete with the conversation surface. Initial candidates:

- Settings currently shown in the modal
- Macro editor and list management
- Browser and collection status details
- Tool-specific contextual information

The right panel should be dismissible and collapsed by default.

## Visual Design

### Overall Tone

Adopt a desktop productivity look inspired by Fluent acrylic and modern IDE shells rather than a marketing page. The UI should feel quiet, structured, and intentionally layered.

### Blur and Materials

- Use moderate blur on major containers and dock surfaces
- Keep transcript cards and text-entry surfaces more opaque than surrounding chrome
- Let background ambient shapes remain visible only as soft depth, not as a focal point
- Use translucent borders and restrained shadows for panel separation

### Hierarchy

- Replace the current oversized hero with a thin workspace header
- Make tabs visually dominant over section titles
- Reduce repeated labels and explanatory text
- Keep status signals compact and local to the element they describe

## Interaction Model

### Tabs

Tabs become the default unit of work.

- Each chat session remains a tab
- Active tab is clearly separated from inactive tabs
- Busy tabs show a compact live indicator
- New tab remains always available at the end of the strip
- Closing a tab should move focus to the nearest surviving tab

Tab labels should continue to derive from the first meaningful user prompt, with truncation for long titles.

### Panels

- Left dock stays available at all times in compact form
- Left and right detail panels can be toggled without leaving the current tab
- Secondary tools should no longer require a blocking modal for common use
- Mobile and narrow widths may still fall back to overlay or drawer behavior

### Status and Feedback

- Replace the prominent status card with slim inline indicators in the workspace bar or panel headers
- Keep long-running activity visible in the active tab and transcript loading state
- Preserve approval prompts and collection result modals where blocking confirmation is still appropriate

## Component Boundaries

The redesign should be implemented with focused UI pieces inside the existing Blazor page rather than a single expanding page file.

Recommended boundaries:

- Workspace shell container
- Left dock and optional tool pane
- Top workspace bar
- Tab strip
- Transcript panel
- Composer panel
- Right context panel

If extraction is done incrementally, visual restructuring can begin in the existing page, but the end state should reduce layout and styling concentration inside `Home.razor`.

## Data and State Handling

No new backend or persistence model is required for the redesign. Existing tab, busy-state, modal, macro, and Naver Talk state should be reused.

State changes that matter to layout:

- Active tab selection
- Left dock selection
- Right panel open or closed state
- Mobile drawer visibility
- Busy status per tab

New UI state should remain local to the page unless another component genuinely needs to share it.

## Error Handling and Risk

- The redesign must not break existing send, resume, cancel, approval, macro, or browser workflows
- Hidden panels must remain keyboard and pointer accessible
- Heavy blur must degrade gracefully if a platform does not fully support the effect
- Narrow layouts must avoid trapping critical controls off-screen

The main implementation risk is layout churn inside a large page component. To reduce that risk, ship the redesign in small slices and verify behavior after each slice.

## Testing Strategy

### Manual Verification

- Open the app and confirm the hero section is removed
- Verify the center workspace remains functional for send, resume, cancel, and loading states
- Create, switch, and close multiple tabs
- Open and close left and right tool panels
- Use settings, macros, and Naver Talk tools from their new locations
- Confirm narrow-width behavior still exposes all critical controls

### Automated Verification

- Add or update Razor component tests only if the current test setup supports the affected UI layer cleanly
- Preserve existing unit tests for command-building and service logic
- Prefer focused state and interaction tests for any extracted UI logic

## Out of Scope

- Rewriting the Codex interaction pipeline
- Changing the tab data model beyond what layout behavior requires
- Replacing existing approval or collection result flows unless the redesign forces a follow-up pass
- Large service-layer refactors unrelated to the new workspace shell

## Implementation Summary

The redesign should convert the current page from a hero-plus-panel layout into a workbench shell. The center tabbed conversation area becomes the product, side tools are docked and collapsible, and acrylic blur is used as supporting depth rather than decoration. The result should feel cleaner, denser, and more tool-like without removing existing capabilities.

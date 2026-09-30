---
name: saletracking-ui
description: Design or refine SaleTracking screens in Figma, implement Figma designs in the existing SaleTracking ASP.NET Core MVC app, and review responsive UI parity. Use for SaleTracking UI design, Figma-to-Razor implementation, or code-to-Figma synchronization; not for general sales analytics or unrelated backend work.
---

# SaleTracking UI

Turn the requested SaleTracking screen into an editable Figma design, working application code, or both, according to the user's request. Follow the user's current scope; a design-only request does not authorize application edits, and implementation from an existing Figma frame does not require a new design file.

## Establish the target

1. Read the applicable repository instructions and identify the active checkout. Read [project-context.md](references/project-context.md) for the known SaleTracking structure, then verify the relevant paths in the current checkout.
2. Identify the screen, requested behavior, and design direction from the conversation and existing application. Ask only for a missing detail that materially changes the result. If the user gives a Figma URL, use that file and node; do not create a competing file.
3. Inspect the relevant controller, view model, Razor view, shared layout, CSS, and JavaScript before making layout decisions. Establish which values, actions, validation messages, and role-specific controls actually exist.
4. Check the available Figma tools. Use the connected Figma app declared by this plugin. If its connection or file access is unavailable, report the actual blocker and continue independent local preparation. Never invent a Figma file URL or claim that a local mockup has been saved to Figma.

## Design in Figma

Use the installed Figma guidance for the action being performed, loading its skill before the associated tool:

| Action | Figma skills to load |
| --- | --- |
| Create a new design file | `figma-create-new-file` |
| Build or update a composed screen | `figma-use` and `figma-generate-design` |
| Create components, variants, or design tokens | `figma-use` and `figma-generate-library` |
| Implement a supplied Figma design | `figma-design-to-code` before `get_design_context` |

Discover these skills from the current skill catalog; do not assume a machine-specific installation path. When only Figma's documented skill-resource reader is available, use it. If required guidance is unavailable, stop the dependent Figma operation and finish preparation that does not require it.

- For a new file, resolve the destination using the Figma create-file skill and the authenticated user's actual plans. Reuse a sole eligible plan; if several plans exist, ask which to use before creation. Do not store personal team IDs in this plugin.
- Inspect existing file structure and discover relevant library components, styles, and variables before inventing replacements. Reuse the established visual language unless the user asks for a redesign.
- Build screens with editable text, meaningful layer names, auto-layout, reusable components, and appropriate token bindings. Treat screenshots as reference material, not the editable deliverable.
- For initial capture of a running web page, follow the Figma generate-design skill's capture and component reconstruction workflow. For an existing Figma screen, update it through the documented Figma editing workflow.
- Represent the actual price-tracking product: target/current price, tracking status, schedule, product link, and alert state where relevant. Use clearly synthetic examples for mockups. Do not add revenue, orders, checkout, or customer-management features solely because the application is called SaleTracking.
- Include useful empty, loading, validation, and error states for the requested screen. Keep destructive and primary actions distinguishable. Resolve small-screen navigation and dense product data explicitly.
- Inspect the resulting Figma screen visually and correct clipping, overlap, text wrapping, alignment, and inconsistent spacing. Return the verified file/frame link.

## Implement in the application

1. Read design context and a screenshot of the selected Figma node using the required Figma guidance. Resolve missing assets through the provided Figma assets or existing project assets.
2. Map the design to the existing MVC view, shared layout or partials, CSS, and JavaScript. Generated React or Tailwind snippets are visual references; implement in the repository's actual stack unless the user explicitly requests a migration.
3. Preserve Razor model binding, `asp-for`, `asp-action`, `asp-controller`, validation spans, form methods, anti-forgery tokens, authorization-dependent rendering, and existing JavaScript selectors. Read event handlers before changing IDs, classes, or data attributes.
4. Reuse CSS variables, Bootstrap behavior, partials, and existing icons where suitable. Make focused changes near the relevant rules instead of accumulating contradictory overrides. Check the cascade before deciding which color, spacing, or font is active.
5. Keep existing routes and data semantics. Mock values belong in design previews or isolated fixtures, not production views. A UI change must not silently change tracking schedules, notification behavior, or data storage.
6. Implement keyboard access, visible focus, associated labels, error announcements where appropriate, and readable contrast. Design data tables and cards to remain usable on narrow screens and with long product names or URLs.

## Verify and deliver

- Build the affected project, normally `dotnet build SaleTracking/SaleTracking.csproj`. Run existing relevant tests when behavior changes; the test project may require a newer SDK than the app. Report tool or environment failures separately from failures introduced by the change.
- Preview the actual modified route in an authorized local session. Inspect desktop and mobile widths, meaningful UI states, navigation, validation, dropdowns, and changed interactions. Inspect browser errors when available. Do not substitute a static mockup for verification of the Razor page.
- Before starting the app for preview, inspect its worker configuration and data location. It contains price-tracking, email, and WhatsApp workers; use isolated test data and disable external sending through supported settings or test doubles when needed. A visual verification task is not authorization to send messages or operate live trackers.
- Compare the rendered page with the selected Figma screen and correct material visual differences. If login, missing credentials, or runtime dependencies prevent a check, state exactly what remains unverified.
- For combined design-and-implementation requests, complete both within the authorized scope. Do not introduce a design approval gate unless the user requests one or a concrete unresolved choice requires input.
- Finish with the Figma link when created or changed, concise implementation details, relevant file links, and checks actually performed. Separate completed work from any blocked checks. Deploy or publish only when requested.

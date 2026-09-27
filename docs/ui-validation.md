# UI implementation evidence and remaining gates

Recorded 2026-09-27. Scope: first sample screen and live delegation status;
not integrated workflow V1 acceptance.

## Completed checks

- TypeScript strict `tsc --noEmit`: passed in the staged frontend and after
  copying to `src/Marang.Ui` in Marang.
- Vite production build: passed in the Marang repository (React, React Flow,
  ELK, sample fixture). The
  initial bundle is about 1.89 MB uncompressed / 581 KB gzip; split the graph
  and layout code before the V1 performance gate.
- Local production preview: sample route rendered in the browser. Reviewed
  desktop at 1440×900 and narrow layout at 390×844. The first desktop fit made
  graph labels too small, so the initial viewpoint was changed to center the
  active checkpoint/parallel branch; the Fit control still exposes the full
  workflow. Narrow screens now default to the list.
- Browser interaction at 390×844: selected `Validate rotation` from the list,
  opened its full-width inspector, and returned to the workflow with the Back
  button. The accessibility tree exposed the run title, list, inspector tabs,
  and step labels. This is a smoke check, not a screen-reader pass.
- Live delegation UI typecheck and Vite production build passed. Marang tests
  passed on .NET 8 and .NET 10 (51 each), including cross-caller denial for
  MCP reads/cancel and acceptance-index ownership. The server built on .NET 10.
- Local HTTP smoke: `/api/delegations` returned a real empty list; an unknown
  ID returned 404. The preview proxy returned the same live list through
  `/api/delegations`, and `/ui/delegations` served successfully. No provider
  run was demonstrated because none is registered in the server.

## Remaining frontend work

- Broader UI-01 visual review, retained screenshots, and a versioned event
  fixture. The sample has been built in the repository and the mobile inspector
  interaction was verified in the browser.
- UI-02 fixture scenarios and versioned JSON/event scripts, navigation/search
  and error/empty states.
- UI-03/04 stable topology updates, list parity, revision explanation,
  evidence/artifact inspection, long-journal behavior, and keyboard refinement.
- UI-05 workflow-run adapter/recovery; delegation polling is an interim live
  status slice, not a replacement for the durable workflow contract.
- UI-06 host packaging, build pipeline, frontend CI, accessibility and
  responsive review. UI-07 user and performance studies and real-run evidence.

## Release status

**Sample preview is underway. Integrated V1 is blocked by the backend features
listed in [ui-contract.md](ui-contract.md).** No usability participant results,
performance measurements, security test results, or real-run validation have
been recorded. The reviewed spec's 4-of-5 comprehension and load budgets remain
release targets, not achieved results.

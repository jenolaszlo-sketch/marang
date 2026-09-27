# Marang UI preview development

Status: UI-01 sample and first live delegation slice, 2026-09-27.

The frontend is in `src/Marang.Ui`. This task host had Node 24 and pnpm 11,
but no npm executable. The implementation uses pnpm with a committed lockfile
instead of the handoff's proposed npm toolchain; the same package scripts and
build output are retained. `pnpm-workspace.yaml` explicitly permits esbuild's
install script. Use a supported Node version and the pinned package manager.

From `src/Marang.Ui`:

```powershell
pnpm install --frozen-lockfile
pnpm run typecheck
pnpm run build
pnpm run preview
```

Open `http://127.0.0.1:4173/ui/` for the sample. The direct sample route is
`/ui/sample/auth-refresh`. Start `Marang.Server` with `dotnet run --project
src/Marang.Server/Marang.Server.csproj` from the repository root, then open
`/ui/delegations` for actual caller-scoped Qingniao status. The Vite dev and
preview servers proxy `/api` to `http://127.0.0.1:5107` by default; override
with `MARANG_API_ORIGIN` if the server uses another origin. Both servers must
run on loopback because the browser session for remote deployment is pending.
All visible workflow sample data is synthetic and permanently labeled.
`Marang.Server` does not yet serve the built UI assets.

The application uses the Vite `runner` config loader on this Windows task host
because config bundling through the bundled Node/pnpm runtime hit a path access
error. The production build succeeded with this setting. Dev-server dependency
optimization also encountered a path access error here, so use the production
build and preview for visual review until the development toolchain is validated
in the intended environment. Do not report this as a product runtime failure.

The UI has a polling adapter for delegation list/detail only. It has no
workflow-run HTTP/SignalR adapter, browser session, host asset publish step,
frontend CI, or automated browser suite. These are tracked in
[the contract gap list](ui-contract.md) and handoff UI-05/06/07.

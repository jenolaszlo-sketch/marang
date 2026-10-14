# Operating the bounded M3 workflow

Use effect catch-up on return, then drill into the current revision and its prior physical effect. The current checkout observation, effect identity, base/patch/post identities, durable stage and validation evidence are separate from logical execution result authority. An earlier applied effect can be superseded and still physically real.

| Observation | Next supported action |
|---|---|
| Definitely not applied | Continue the same exact intent only after current approval, authority and base checks; catch-up itself grants no permission |
| Definitely applied, result missing | Reconcile/record and validate the exact state; do not repeat the replacement |
| Applied, validation failed | Inspect failure evidence and approve a separately identified correction against this actual post-state |
| Partial | Keep the entire patch parked, inspect before/after evidence and discuss deliberate recovery; do not replay or overwrite the remaining files |
| Unresolved | Keep writes blocked, inspect checkout identity/HEAD/index/unrelated changes and durable intent; no automatic acceptance or merge |
| Pending corrective approval | Review exact patch/base and preserved work; restart retains the proposal and decision context; commit checks authority again |
| Cancelled | Logical contribution stops; already changed files remain historical physical effects |

M3 deliberately has no generic recovery command that clears a parked partial/unresolved effect. A deliberate future resolution needs its own reviewed contract. Moving or manually restoring files is not authority to continue. An unreadable/unsupported checkout observation fails closed; restore inspectability before making a decision.

The canonical probe uses a local clone pinned to qualified M2 commit `a0916534d0e82e499cffb2dea526b64357eb32e2`, on fixture branch `m3-effect-qualification`. Patch A tentatively extracts the fingerprint length constant but uses 63, so the existing real plan-resolution validation fails. Patch B uses the observed post-A base and corrects the constant to 64. Both also update a README note. The original source checkout is untouched. Inventory refers to the immutable source commit and is reused explicitly through both successor revisions.

Reproduce with `./eng/qualify-aaaw-m3.ps1 -ReuseQualifiedPackages` after installing the exact local M1 qualification packages. Without that switch, the script builds packages from the locked source checkouts. The solution must have the pinned M2 commit locally available for the controlled clone; CI fetches full source history. Runtime evidence is written under ignored `artifacts/aaaw-m3/`, including exact effect records, kill receipts, validation output and catch-up. No package release, push or publication is part of this command.

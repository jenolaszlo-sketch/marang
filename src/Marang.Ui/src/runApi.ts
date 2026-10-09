import type { RunSnapshot } from './contracts'

export async function getRunProjection(id: string, signal?: AbortSignal): Promise<RunSnapshot> {
  const response = await fetch(`/api/runs/${encodeURIComponent(id)}`, { headers: { Accept: 'application/json' }, signal })
  if (!response.ok) {
    const error = new Error(response.status === 404 ? 'This run could not be found.' : `The run service returned ${response.status}.`)
    Object.assign(error, { status: response.status })
    throw error
  }
  return response.json() as Promise<RunSnapshot>
}

export async function getDelegationRunId(delegationId: string, signal?: AbortSignal): Promise<string | null> {
  const response = await fetch(`/api/delegations/${encodeURIComponent(delegationId)}/run`, { headers: { Accept: 'application/json' }, signal })
  if (response.status === 404) return null
  if (!response.ok) {
    const error = new Error(`The run service returned ${response.status}.`)
    Object.assign(error, { status: response.status })
    throw error
  }
  const body = await response.json() as { runId?: string }
  return typeof body.runId === 'string' ? body.runId : null
}

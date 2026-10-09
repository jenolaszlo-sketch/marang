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

export interface JournalEntryView {
  sequence: number
  eventType: string
  committedAt: string
  participant: string
  refs: Record<string, string>
}

export interface JournalIncidentView {
  incidentId: string
  reasonCode: string | null
  severity: string
  detectedAt: string
}

export interface JournalView {
  available: boolean
  unavailableReason: string | null
  sessionId: string
  appliedSequence: number
  operatorState: string
  recoveryState: string
  totalEvents: number
  incidents: JournalIncidentView[]
  entries: JournalEntryView[]
  hasMore: boolean
  nextSequence: number | null
}

export async function getRunJournal(id: string, signal?: AbortSignal): Promise<JournalView | null> {
  const response = await fetch(`/api/runs/${encodeURIComponent(id)}/journal`, { headers: { Accept: 'application/json' }, signal })
  if (response.status === 404) return null
  if (!response.ok) {
    const error = new Error(`The journal service returned ${response.status}.`)
    Object.assign(error, { status: response.status })
    throw error
  }
  return response.json() as Promise<JournalView>
}

export interface DelegationSummary {
  id: string
  objective: string
  provider: string
  workspace: string
  state: string
  updatedAt: string
  revision: number
}

export interface WaitingSummary {
  checkpointId: string
  reason: string
  summary: string
  requestedAction: string
  canIntervene: boolean
  expectedRevision: number
}

export interface DelegationDetail extends DelegationSummary {
  currentSteps: string[]
  completedSteps: string[]
  workerCalls: number
  retries: number
  resultSummary?: string
  unresolvedConcerns?: string[]
  waiting?: WaitingSummary | null
  canCancel?: boolean
}

interface DelegationListResponse { items: DelegationSummary[] }

async function readJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, { headers: { Accept: 'application/json' }, signal })
  if (!response.ok) {
    const error = new Error(response.status === 404 ? 'This delegation could not be found.' : `The delegation service returned ${response.status}.`)
    Object.assign(error, { status: response.status })
    throw error
  }
  return response.json() as Promise<T>
}

export async function listDelegations(signal?: AbortSignal): Promise<DelegationSummary[]> {
  const response = await readJson<DelegationListResponse>('/api/delegations', signal)
  if (!response || !Array.isArray(response.items)) throw new Error('The delegation service returned an invalid list response.')
  return response.items
}

export function getDelegation(id: string, signal?: AbortSignal): Promise<DelegationDetail> {
  return readJson<DelegationDetail>(`/api/delegations/${encodeURIComponent(id)}`, signal)
}

export interface InterventionResult {
  state: string
  revision: number
}

export interface CancelResult {
  state: string
  revision: number
}

export async function approveIntervention(
  id: string,
  checkpointId: string,
  expectedRevision: number,
  signal?: AbortSignal,
): Promise<InterventionResult> {
  const response = await fetch(`/api/delegations/${encodeURIComponent(id)}/interventions`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify({ action: 'approve', checkpointId, expectedRevision }),
    signal,
  })
  if (!response.ok) {
    const message = response.status === 404
      ? 'This delegation could not be found.'
      : response.status === 409
        ? 'The checkpoint changed. Showing the current status.'
        : `The delegation service returned ${response.status}.`
    const error = new Error(message)
    Object.assign(error, { status: response.status })
    throw error
  }
  return response.json() as Promise<InterventionResult>
}

export async function cancelDelegation(id: string, signal?: AbortSignal): Promise<CancelResult> {
  const response = await fetch(`/api/delegations/${encodeURIComponent(id)}/cancel`, {
    method: 'POST',
    headers: { Accept: 'application/json' },
    signal,
  })
  if (!response.ok) {
    const error = new Error(response.status === 404
      ? 'This delegation could not be found.'
      : `The delegation service returned ${response.status}.`)
    Object.assign(error, { status: response.status })
    throw error
  }
  return response.json() as Promise<CancelResult>
}


export type WorkStatus = 'pending' | 'running' | 'completed' | 'failed' | 'waiting'
export type NodeKind = 'plan' | 'activity' | 'checkpoint' | 'completion'
export type RunStatus = 'running' | 'waiting' | 'completed' | 'failed'

export interface WorkflowNode {
  id: string
  name: string
  kind: NodeKind
  status: WorkStatus
  membership: 'current' | 'superseded'
  detail: string
  progress?: number
  actor?: string
  duration?: string
  attempt?: number
}

export interface WorkflowEdge {
  id: string
  source: string
  target: string
  membership: 'current' | 'superseded'
}

export interface JournalEntry {
  id: string
  time: string
  type: 'update' | 'tool' | 'evidence' | 'decision' | 'warning'
  source: string
  message: string
}

export interface RunSnapshot {
  schemaVersion: 1
  id: string
  title: string
  workspace: string
  status: RunStatus
  currentRevisionId: string
  durableSequence: string
  elapsed: string
  summary: string
  attention: string
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
  journal: Record<string, JournalEntry[]>
}

export interface RunDataSource {
  getRun(runId: string, signal?: AbortSignal): Promise<RunSnapshot>
}

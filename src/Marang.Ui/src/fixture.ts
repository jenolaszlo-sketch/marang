import type { RunDataSource, RunSnapshot, WorkStatus } from './contracts'

const base: RunSnapshot = {
  schemaVersion: 1,
  id: 'sample-auth-refresh',
  title: 'Implement token refresh',
  workspace: 'marang / identity-service',
  status: 'running',
  currentRevisionId: 'revision-2',
  durableSequence: '184',
  elapsed: '04:32',
  summary: 'Validation and independent review are running in parallel.',
  attention: 'No action needed',
  nodes: [
    { id: 'plan', name: 'Plan', kind: 'plan', status: 'completed', membership: 'current', detail: 'Scope the token refresh change', duration: '24s' },
    { id: 'research', name: 'Inspect auth flow', kind: 'activity', status: 'completed', membership: 'current', detail: 'Mapped existing session and token handling', duration: '42s', actor: 'Sol' },
    { id: 'implement', name: 'Implement refresh', kind: 'activity', status: 'completed', membership: 'current', detail: 'Refresh endpoint and token rotation complete', duration: '2m 14s', actor: 'Sol' },
    { id: 'checkpoint', name: 'Evaluate evidence', kind: 'checkpoint', status: 'completed', membership: 'current', detail: 'Validation found an edge case; workflow revised', duration: '18s' },
    { id: 'old-validate', name: 'Original validation', kind: 'activity', status: 'failed', membership: 'superseded', detail: 'Two compatibility tests failed', duration: '38s', actor: 'Test runner', attempt: 1 },
    { id: 'validate', name: 'Validate rotation', kind: 'activity', status: 'running', membership: 'current', detail: '43 of 45 tests passing · adjusting token handling', progress: 72, actor: 'Test runner', duration: '01:12', attempt: 2 },
    { id: 'review', name: 'Independent review', kind: 'activity', status: 'running', membership: 'current', detail: 'Reviewing API and failure paths', actor: 'Astra', duration: '00:48', attempt: 1 },
    { id: 'finish', name: 'Ready for decision', kind: 'completion', status: 'pending', membership: 'current', detail: 'Await validation and review' },
  ],
  edges: [
    { id: 'e1', source: 'plan', target: 'research', membership: 'current' },
    { id: 'e2', source: 'research', target: 'implement', membership: 'current' },
    { id: 'e3', source: 'implement', target: 'checkpoint', membership: 'current' },
    { id: 'e4', source: 'checkpoint', target: 'old-validate', membership: 'superseded' },
    { id: 'e5', source: 'checkpoint', target: 'validate', membership: 'current' },
    { id: 'e6', source: 'checkpoint', target: 'review', membership: 'current' },
    { id: 'e7', source: 'validate', target: 'finish', membership: 'current' },
    { id: 'e8', source: 'review', target: 'finish', membership: 'current' },
  ],
  journal: {
    validate: [
      { id: 'j1', time: '10:42', type: 'decision', source: 'Marang', message: 'Validation restarted after checkpoint revision 2.' },
      { id: 'j2', time: '10:43', type: 'tool', source: 'Test runner', message: 'Running authentication test suite.' },
      { id: 'j3', time: '10:44', type: 'evidence', source: 'Test runner', message: '43 of 45 tests passed. Two token compatibility cases need adjustment.' },
      { id: 'j4', time: '10:45', type: 'update', source: 'Sol', message: 'Adjusting token handling before the next validation pass.' },
    ],
    checkpoint: [
      { id: 'c1', time: '10:42', type: 'evidence', source: 'Test runner', message: 'Initial validation failed two compatibility checks.' },
      { id: 'c2', time: '10:42', type: 'decision', source: 'Marang', message: 'Revision 2 retained implementation and replaced validation work.' },
    ],
    review: [
      { id: 'r1', time: '10:44', type: 'update', source: 'Astra', message: 'Inspecting API behavior and failure paths.' },
    ],
  },
}

export const sampleRun = base

export const sampleSource: RunDataSource = {
  async getRun(runId, signal) {
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
    if (runId !== base.id) throw new Error('Sample run not found')
    return structuredClone(base)
  },
}

export const currentCounts = (run: RunSnapshot): Record<WorkStatus, number> =>
  run.nodes.reduce((counts, node) => {
    if (node.membership === 'current' && node.kind === 'activity') counts[node.status]++
    return counts
  }, { pending: 0, running: 0, completed: 0, failed: 0, waiting: 0 })

import { useEffect, useRef, useState } from 'react'
import { Activity, ArrowLeft, ArrowRight, BookOpen, Check, CircleHelp, Clock3, GitBranch, Layers3, List, PanelRightClose, PanelRightOpen, Play, Radio, ShieldCheck, Sparkles, X } from 'lucide-react'
import type { JournalEntry, RunSnapshot, WorkflowNode } from './contracts'
import { currentCounts, sampleRun, sampleSource } from './fixture'
import { WorkflowGraph } from './WorkflowGraph'
import { getDelegation, listDelegations, approveIntervention, cancelDelegation, getEvidence } from './delegationApi'
import { getRunProjection, getDelegationRunId, getRunJournal } from './runApi'
import type { JournalView } from './runApi'
import type { DelegationDetail, DelegationSummary, EvidenceView } from './delegationApi'

type InspectorTab = 'Summary' | 'Journal' | 'Evidence' | 'Attempts'
const tabs: InspectorTab[] = ['Summary', 'Journal', 'Evidence', 'Attempts']

function StatusIcon({ status }: { status: WorkflowNode['status'] }) {
  if (status === 'completed') return <Check size={15} />
  if (status === 'running') return <Play size={15} fill="currentColor" />
  if (status === 'failed') return <X size={15} />
  return <Clock3 size={15} />
}

function Pill({ children, tone = 'neutral' }: { children: React.ReactNode; tone?: string }) {
  return <span className={`pill ${tone}`}>{children}</span>
}

function Header({ onRuns, onSample }: { onRuns?: () => void; onSample?: () => void }) {
  return <header className="global-header">
    <div className="brand"><span className="brand-mark"><Layers3 size={19} strokeWidth={2.6} /></span><span>marang</span><span className="brand-separator" /><span className="brand-subtitle">Workflow observability</span></div>
    <nav aria-label="Primary navigation">{onRuns ? <button type="button" onClick={onRuns}>Runs</button> : <span className="nav-current" aria-current="page">Runs</span>}{onSample ? <button type="button" onClick={onSample}>Sample workflow</button> : <span className="nav-current" aria-current="page">Sample workflow</span>}</nav>
    <span className="preview-label"><Sparkles size={14} /> V1 preview</span>
  </header>
}

function RunHeader({ run, live }: { run: RunSnapshot; live?: boolean }) {
  const counts = currentCounts(run)
  return <section className="run-header">
    <div className="breadcrumb"><span>Runs</span><ArrowRight size={13} /><span>{live ? 'Live run' : 'Sample workflow'}</span><ArrowRight size={13} /><strong>{run.title}</strong></div>
    <div className="run-heading-row"><div><div className="eyebrow">RUN / {run.id}</div><h1>{run.title}</h1><div className="workspace">{run.workspace}</div></div><div className="run-heading-right">{live ? <><Pill tone="live"><Radio size={13} /> LIVE DATA</Pill></> : <><Pill tone="sample"><Sparkles size={13} /> SAMPLE DATA</Pill><Pill tone="neutral"><Radio size={13} /> Static preview</Pill></>}</div></div>
    <div className="run-summary-row"><div className="summary-main"><span className="summary-icon"><Activity size={19} /></span><div><strong>Validation and review in progress</strong><span>{run.summary}</span></div></div><div className="metric"><strong>{counts.completed}</strong><span>completed</span></div><div className="metric"><strong>{counts.running}</strong><span>running</span></div><div className="metric"><strong>{counts.waiting}</strong><span>waiting</span></div><div className="metric elapsed"><strong>{run.elapsed}</strong><span>elapsed</span></div></div>
  </section>
}

function Journal({ entries }: { entries: JournalEntry[] }) {
  return <div className="journal-list">{entries.length ? entries.map(entry => <article className="journal-entry" key={entry.id}><div className={`journal-glyph ${entry.type}`}>{entry.type === 'evidence' ? <ShieldCheck size={15} /> : entry.type === 'decision' ? <GitBranch size={15} /> : <Activity size={15} />}</div><div><div className="entry-top"><strong>{entry.source}</strong><span>{entry.time}</span></div><p>{entry.message}</p><span className="entry-type">{entry.type}</span></div></article>) : <div className="empty-mini">No journal entries for this step yet.</div>}</div>
}

function Inspector({ run, work, onClose }: { run: RunSnapshot; work: WorkflowNode; onClose: () => void }) {
  const [tab, setTab] = useState<InspectorTab>('Summary')
  useEffect(() => setTab('Summary'), [work.id])
  const entries = run.journal[work.id] ?? []
  return <aside className="inspector" aria-label="Step inspector"><div className="inspector-head"><div className="eyebrow">STEP DETAILS</div><button type="button" className="icon-button close-inspector" onClick={onClose} aria-label="Back to workflow"><PanelRightClose className="desktop-close" size={19} /><ArrowLeft className="mobile-back" size={18} /><span className="mobile-back-label">Workflow</span></button><div className="inspector-title-row"><span className={`inspector-icon ${work.status}`}><StatusIcon status={work.status} /></span><h2>{work.name}</h2></div><div className="inspector-meta"><Pill tone={work.membership === 'superseded' ? 'muted' : work.status}>{work.membership === 'superseded' ? 'Superseded' : work.status}</Pill>{work.attempt && <span>Attempt {work.attempt}</span>}{work.duration && <span>{work.duration}</span>}</div></div>
    <div className="inspector-tabs" role="tablist" aria-label="Step details">{tabs.map(name => <button type="button" role="tab" aria-selected={tab === name} className={tab === name ? 'active' : ''} key={name} onClick={() => setTab(name)}>{name}</button>)}</div>
    <div className="inspector-body" role="tabpanel">
      {tab === 'Summary' && <><div className="field-label">Current state</div><p className="detail-lead">{work.detail}</p>{work.progress !== undefined && <div className="progress-panel"><div><strong>Reported progress</strong><strong>{work.progress}%</strong></div><div className="progress-track"><span style={{ width: `${work.progress}%` }} /></div><small>Reported by the activity; final validation is separate.</small></div>}<div className="detail-grid"><div><span>Actor</span><strong>{work.actor ?? 'Marang'}</strong></div><div><span>Revision</span><strong>{work.membership === 'superseded' ? 'Previous path' : 'Revision 2'}</strong></div><div><span>Execution</span><strong>{work.attempt ? `Attempt ${work.attempt}` : '—'}</strong></div><div><span>Duration</span><strong>{work.duration ?? 'Pending'}</strong></div></div><div className="section-label">Latest meaningful update</div><div className="latest-update"><span className="update-mark" /><p>{entries.at(-1)?.message ?? 'No updates reported yet.'}</p></div>{work.kind === 'checkpoint' && <div className="checkpoint-explanation"><GitBranch size={17} /><div><strong>Workflow revised at this checkpoint</strong><p>The implementation was retained. Validation was replaced after two compatibility tests failed.</p></div></div>}</>}
      {tab === 'Journal' && <><div className="panel-intro">Activity journal <span>{entries.length} entries</span></div><Journal entries={entries} /></>}
      {tab === 'Evidence' && <><div className="panel-intro">Evidence</div><div className="evidence-card"><ShieldCheck size={18} /><div><strong>{work.id === 'validate' ? 'Authentication test suite' : 'Evidence for this step'}</strong><p>{work.id === 'validate' ? '43 of 45 tests passing. Two compatibility cases remain.' : 'Evidence appears here when produced by the activity.'}</p><span>Sample evidence</span></div></div></>}
      {tab === 'Attempts' && <><div className="panel-intro">Execution attempts</div>{work.id === 'validate' ? <div className="attempt-list"><div><span className="attempt-dot fail" /><div><strong>Attempt 1</strong><p>Failed · two compatibility tests</p></div><span>Previous</span></div><div><span className="attempt-dot active" /><div><strong>Attempt 2</strong><p>Running · token handling adjustment</p></div><span>Current</span></div></div> : <div className="empty-mini">{work.attempt ? `Attempt ${work.attempt} · ${work.status}` : 'No execution attempt for this workflow step.'}</div>}</>}
    </div>
  </aside>
}

function WorkflowList({ run, selectedId, onSelect }: { run: RunSnapshot; selectedId: string; onSelect: (id: string) => void }) {
  return <div className="workflow-list" role="list" aria-label="Workflow steps">{run.nodes.map(work => <button type="button" role="listitem" className={`workflow-list-row ${work.id === selectedId ? 'selected' : ''} ${work.membership}`} key={work.id} onClick={() => onSelect(work.id)}><span className={`list-status ${work.status}`}><StatusIcon status={work.status} /></span><span><strong>{work.name}</strong><small>{work.kind === 'checkpoint' ? 'Checkpoint' : work.detail}</small></span><Pill tone={work.membership === 'superseded' ? 'muted' : work.status}>{work.membership === 'superseded' ? 'Superseded' : work.status}</Pill></button>)}</div>
}

function Milestones({ onSelect }: { onSelect: (id: string) => void }) {
  return <div className="milestones"><div className="milestones-title"><GitBranch size={16} /><strong>Workflow story</strong><span>Revision 2 of 2</span></div><div className="milestone-items"><div><span className="timeline-dot done"><Check size={12} /></span><div><strong>Run started</strong><small>10:39 · Plan accepted</small></div></div><button type="button" onClick={() => onSelect('checkpoint')}><span className="timeline-dot revision"><GitBranch size={12} /></span><div><strong>Workflow revised</strong><small>10:42 · Validation replaced</small></div></button><div><span className="timeline-dot active"><Play size={11} /></span><div><strong>Now running</strong><small>Validation + review</small></div></div></div></div>
}

function JournalCard({ view }: { view: JournalView }) {
  return <section className="live-card" aria-label="Session journal">
    <div className="live-card-heading"><div><span className="panel-icon"><BookOpen size={17} /></span><div><strong>Session journal</strong><small>Reconciled session evidence · {view.recoveryState}</small></div></div></div>
    {!view.available ? <div className="steps-empty">{view.unavailableReason ?? 'No session evidence available.'}</div> : <>
      {view.incidents.length ? <div className="panel-intro">Active incidents <span>{view.incidents.length}</span></div> : null}
      {view.incidents.map(incident => <div className="live-step" key={incident.incidentId}><span className="step-indicator done" /><span><strong>[{incident.severity}]</strong> {incident.reasonCode ?? 'incident'}</span><span className="step-state-label">{incident.incidentId.slice(0, 8)}</span></div>)}
      <div className="panel-intro">Entries <span>{view.entries.length}{view.hasMore ? '+' : ''}</span></div>
      {view.entries.length ? <div className="journal-list">{view.entries.map(entry => <article className="journal-entry" key={entry.sequence}><div className="journal-glyph"><Activity size={15} /></div><div><div className="entry-top"><strong>#{entry.sequence} {entry.eventType}</strong><span>{entry.committedAt}</span></div><p>{entry.participant}</p></div></article>)}</div> : <div className="steps-empty">No journal entries recorded.</div>}
    </>}
  </section>
}

function RunScreen({ run, onRuns, live, journal }: { run: RunSnapshot; onRuns: () => void; live?: boolean; journal?: JournalView | null }) {
  const [selectedId, setSelectedId] = useState('validate')
  const [inspectorOpen, setInspectorOpen] = useState(() => !window.matchMedia('(max-width: 560px)').matches)
  const [view, setView] = useState<'graph' | 'list'>(() => window.matchMedia('(max-width: 560px)').matches ? 'list' : 'graph')
  useEffect(() => {
    const query = window.matchMedia('(max-width: 560px)')
    const onChange = () => { if (query.matches) setView('list') }
    query.addEventListener('change', onChange)
    return () => query.removeEventListener('change', onChange)
  }, [])
  const work = run.nodes.find(node => node.id === selectedId) ?? run.nodes[0]
  const select = (id: string) => { setSelectedId(id); setInspectorOpen(true) }
  return <><Header onRuns={onRuns} /><main className="page"><RunHeader run={run} live={live} /><div className="workspace-layout"><section className="workflow-panel" aria-label="Workflow"><div className="panel-toolbar"><div className="panel-title"><span className="panel-icon"><GitBranch size={17} /></span><div><strong>Workflow</strong><small>Current path and retained history</small></div></div><div className="toolbar-actions"><div className="segmented" aria-label="Workflow view"><button type="button" className={view === 'graph' ? 'active' : ''} onClick={() => setView('graph')}><GitBranch size={14} /> Graph</button><button type="button" className={view === 'list' ? 'active' : ''} onClick={() => setView('list')}><List size={14} /> List</button></div>{!inspectorOpen && <button type="button" className="icon-button" aria-label="Open inspector" onClick={() => setInspectorOpen(true)}><PanelRightOpen size={17} /></button>}</div></div><div className="canvas-wrap">{view === 'graph' ? <WorkflowGraph run={run} selectedId={selectedId} onSelect={select} /> : <WorkflowList run={run} selectedId={selectedId} onSelect={select} />}</div><Milestones onSelect={select} /></section>{inspectorOpen && <Inspector run={run} work={work} onClose={() => setInspectorOpen(false)} />}</div>{live ? (journal ? <JournalCard view={journal} /> : <div className="live-card"><div className="steps-empty">Loading session journal…</div></div>) : null}<footer className="run-footer">{live ? <><span><Radio size={14} /> Live projection. Refreshes automatically.</span><span>Run ID: {run.id} · Snapshot {run.durableSequence}</span></> : <><span><CircleHelp size={14} /> This is a deterministic sample. No live execution is connected.</span><span>Run ID: {run.id} · Snapshot {run.durableSequence}</span></>}</footer></main></>
}

function RunsScreen({ onOpen }: { onOpen: () => void }) {
  return <><Header onRuns={() => {}} onSample={onOpen} /><main className="page runs-page"><div className="eyebrow">WORKSPACE OVERVIEW</div><h1>Runs</h1><p className="runs-intro">Watch work unfold, understand decisions, and inspect the evidence behind each outcome.</p><div className="live-unavailable"><Radio size={17} /><div><strong>Delegations are available live</strong><p>Inspect caller-scoped delegation status and reported progress.</p></div><button type="button" className="primary-button" onClick={() => { window.history.pushState({}, '', '/ui/delegations'); window.dispatchEvent(new PopStateEvent('popstate')) }}>View delegations <ArrowRight size={16} /></button></div><div className="empty-state"><span className="empty-emblem"><BookOpen size={25} /></span><h2>Explore the sample workflow</h2><p>Explore a plan, parallel execution, a checkpoint, and a revised path. The sample is deterministic and clearly marked.</p><button type="button" className="primary-button" onClick={onOpen}>Explore sample <ArrowRight size={16} /></button></div></main></>
}

function displayState(state: string) {
  return state.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[-_]+/g, ' ').toLowerCase().replace(/^\w/, value => value.toUpperCase())
}

function stateTone(state: string) {
  const normalized = state.toLowerCase()
  if (normalized.includes('complete') || normalized === 'succeeded') return 'completed'
  if (normalized.includes('fail') || normalized.includes('reject')) return 'failed'
  if (normalized.includes('wait') || normalized.includes('supervis')) return 'waiting'
  if (normalized.includes('run') || normalized.includes('execut')) return 'running'
  return 'neutral'
}

function formatUpdated(value: string) {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date)
}

function LiveHeader({ onRuns, onSample }: { onRuns: () => void; onSample: () => void }) {
  return <header className="global-header"><div className="brand"><span className="brand-mark"><Layers3 size={19} strokeWidth={2.6} /></span><span>marang</span><span className="brand-separator" /><span className="brand-subtitle">Workflow observability</span></div><nav aria-label="Primary navigation"><button type="button" className="nav-current" aria-current="page" onClick={onRuns}>Delegations</button><button type="button" onClick={onSample}>Sample workflow</button></nav><span className="preview-label"><Radio size={14} /> LIVE DATA</span></header>
}

function DelegationList({ onOpen, onSample }: { onOpen: (id: string) => void; onSample: () => void }) {
  const [items, setItems] = useState<DelegationSummary[] | null>(null)
  const [error, setError] = useState('')
  const [refresh, setRefresh] = useState(0)
  useEffect(() => {
    let active = true
    let timer: number | undefined
    const load = async () => {
      try { const result = await listDelegations(); if (active) { setItems(result); setError('') } }
      catch (reason) { if (active) setError(reason instanceof Error ? reason.message : 'Could not load delegations.') }
      finally { if (active) timer = window.setTimeout(load, 10000) }
    }
    void load()
    return () => { active = false; if (timer) window.clearTimeout(timer) }
  }, [refresh])
  return <><LiveHeader onRuns={() => {}} onSample={onSample} /><main className="page live-page"><div className="live-page-top"><div><div className="eyebrow">CALLER-SCOPED ACTIVITY</div><h1>Delegations</h1><p className="runs-intro">Live execution status and progress reported by the delegation service.</p></div><button type="button" className="refresh-button" onClick={() => setRefresh(value => value + 1)}><Radio size={15} /> Refresh</button></div>
    {error && <div className="live-error" role="alert"><strong>{items ? 'Could not refresh' : 'Delegations unavailable'}</strong><span>{error}</span>{!items && <button type="button" onClick={() => setRefresh(value => value + 1)}>Try again</button>}</div>}
    {items === null && !error ? <div className="live-loading"><span className="spinner" />Loading caller-scoped delegations…</div> : items?.length === 0 ? <div className="live-empty"><span className="empty-emblem"><Activity size={24} /></span><h2>No delegations yet</h2><p>When a delegation is accepted for this caller, its live status will appear here.</p></div> : <section className="delegation-table" aria-label="Delegations">{items?.map(item => <button type="button" className="delegation-row" key={item.id} onClick={() => onOpen(item.id)}><div className="delegation-row-main"><div className="delegation-row-title"><strong>{item.objective || 'Delegation'}</strong><span className={`live-state ${stateTone(item.state)}`}>{displayState(item.state)}</span></div><span className="delegation-context">{item.provider} <i>·</i> {item.workspace}</span><span className="delegation-updated">Updated {formatUpdated(item.updatedAt)}</span></div><span className="delegation-id">{item.id}</span><ArrowRight size={17} className="delegation-arrow" /></button>)}</section>}
    <div className="live-poll-note"><Radio size={13} /> Refreshes automatically every 10 seconds <span>·</span> Last update is supplied by the service</div>
  </main></>
}

function WaitingCard({ waiting, approving, note, onApprove }: {
  waiting: NonNullable<DelegationDetail['waiting']>
  approving: boolean
  note: string
  onApprove: () => void
}) {
  return <section className="live-card waiting-card" aria-label="Needs your approval">
    <div className="live-card-heading"><div><span className="panel-icon"><CircleHelp size={17} /></span><div><strong>Needs your approval</strong><small>{waiting.reason}</small></div></div></div>
    <div className="waiting-body"><p>{waiting.summary}</p>
    <p><strong>{waiting.requestedAction}</strong></p>
    <button type="button" className="primary-button" disabled={!waiting.canIntervene || approving} onClick={onApprove}>{approving ? 'Approving…' : 'Approve / Resume'}</button>
    {note ? <p className="action-note" role="status">{note}</p> : null}</div>
  </section>
}

function FailureCard({ summary, concerns }: { summary?: string; concerns: string[] }) {
  return <section className="live-card failure-card" aria-label="Failure details">
    <div className="live-card-heading"><div><span className="panel-icon"><X size={17} /></span><div><strong>Failed</strong><small>What went wrong</small></div></div></div>
    <div className="failure-body">{summary ? <><div className="field-label">Summary</div><p>{summary}</p></> : null}
      <div className="field-label">Unresolved concerns ({concerns.length})</div>
      {concerns.length ? <ol className="concern-list">{concerns.map((concern, index) => <li key={index}>{concern}</li>)}</ol> : <p>No specific concerns were recorded.</p>}
    </div>
  </section>
}

const terminalStates = ['Completed', 'Failed', 'Cancelled', 'BudgetExceeded', 'NeedsSupervisor']

function EvidenceCard({ view, pending }: { view: EvidenceView | null; pending: boolean }) {
  return <section className="live-card" aria-label="Evidence and artifacts">
    <div className="live-card-heading"><div><span className="panel-icon"><BookOpen size={17} /></span><div><strong>Evidence & artifacts</strong><small>What the execution produced</small></div></div></div>
    {!view ? <div className="steps-empty">{pending ? 'Loading evidence…' : 'Evidence appears here once the delegation finishes.'}</div> :
      !view.hasResult ? <div className="steps-empty">No evidence yet — the delegation has no terminal result.</div> : <>
        {view.evidence ? <div className="detail-metrics"><div><strong>{view.evidence.testsPassed}</strong><span>tests passed</span></div><div><strong>{view.evidence.testsFailed}</strong><span>tests failed</span></div><div><strong>{view.evidence.reviewApproved === null || view.evidence.reviewApproved === undefined ? '—' : view.evidence.reviewApproved ? 'Yes' : 'No'}</strong><span>review approved</span></div><div><strong>{view.evidence.reviewFindingsResolved}</strong><span>findings resolved</span></div></div> : null}
        <div className="panel-intro">Findings <span>{view.findings.length}</span></div>
        {view.findings.length ? <ul className="concern-list">{view.findings.map((finding, index) => <li key={index}><strong>[{finding.severity}] {finding.code}</strong> — {finding.summary}{finding.resolved ? ' (resolved)' : ''}</li>)}</ul> : <div className="steps-empty">No findings recorded.</div>}
        <div className="panel-intro">Artifacts <span>{view.artifacts.length}</span></div>
        {view.artifacts.length ? <ul className="concern-list">{view.artifacts.map(artifact => <li key={artifact.artifactId}><strong>{artifact.kind}</strong> {artifact.artifactId}<br /><small>{artifact.provider} · {artifact.repository} · {artifact.location}</small></li>)}</ul> : <div className="steps-empty">No artifacts recorded.</div>}
      </>}
  </section>
}

function DelegationDetailScreen({ id, onBack, onSample, onOpenRun }: { id: string; onBack: () => void; onSample: () => void; onOpenRun: (runId: string) => void }) {
  const [detail, setDetail] = useState<DelegationDetail | null>(null)
  const [error, setError] = useState('')
  const [refresh, setRefresh] = useState(0)
  const [approving, setApproving] = useState(false)
  const [actionNote, setActionNote] = useState('')
  const [cancelling, setCancelling] = useState(false)
  const [cancelNote, setCancelNote] = useState('')
  const [runId, setRunId] = useState<string | null>(null)
  const [evidence, setEvidence] = useState<EvidenceView | null>(null)
  const evidenceFor = useRef<string | null>(null)
  const heroHeading = useRef<HTMLHeadingElement>(null)
  useEffect(() => {
    setEvidence(null)
    evidenceFor.current = null
    setRunId(null)
    let active = true
    void getDelegationRunId(id).then(value => { if (active) setRunId(value) }).catch(() => { if (active) setRunId(null) })
    return () => { active = false }
  }, [id])
  useEffect(() => {
    let active = true
    let timer: number | undefined
    const load = async () => {
      try {
        const result = await getDelegation(id)
        if (!active) return
        setDetail(result)
        setError('')
        if (terminalStates.includes(result.state)) setCancelNote('')
        if (evidenceFor.current !== id && terminalStates.includes(result.state)) {
          evidenceFor.current = id
          try {
            const view = await getEvidence(id)
            if (active) setEvidence(view)
          } catch {
            evidenceFor.current = null
          }
        }
      }
      catch (reason) { if (active) setError(reason instanceof Error ? reason.message : 'Could not load this delegation.') }
      finally { if (active) timer = window.setTimeout(load, 5000) }
    }
    void load()
    return () => { active = false; if (timer) window.clearTimeout(timer) }
  }, [id, refresh])
  const current = detail?.currentSteps ?? []
  const completed = detail?.completedSteps ?? []
  const approve = async () => {
    const waiting = detail?.waiting
    if (!waiting || approving) return
    setApproving(true)
    setActionNote('')
    try {
      await approveIntervention(id, waiting.checkpointId, waiting.expectedRevision)
      setRefresh(value => value + 1)
      heroHeading.current?.focus()
    } catch (reason) {
      const status = (reason as { status?: number }).status
      if (status === 409) {
        setActionNote('Already decided — showing the current status.')
        setRefresh(value => value + 1)
      } else if (status === 404) {
        setActionNote('You no longer have access to this delegation.')
      } else {
        setActionNote(reason instanceof Error ? reason.message : 'Could not approve.')
      }
    } finally {
      setApproving(false)
    }
  }
  const cancel = async () => {
    if (!detail?.canCancel || cancelling) return
    if (!window.confirm('Cancel this delegation? In-flight work stops and the delegation ends as cancelled.')) return
    setCancelling(true)
    setCancelNote('')
    try {
      const outcome = await cancelDelegation(id)
      setRefresh(value => value + 1)
      if (!terminalStates.includes(outcome.state)) setCancelNote('Cancellation requested — finishing current work.')
      heroHeading.current?.focus()
    } catch (reason) {
      setCancelNote(reason instanceof Error ? reason.message : 'Could not cancel.')
    } finally {
      setCancelling(false)
    }
  }
  return <><LiveHeader onRuns={onBack} onSample={onSample} /><main className="page live-page detail-page"><button type="button" className="back-link" onClick={onBack}><ArrowLeft size={15} /> All delegations</button>
    {error && <div className="live-error" role="alert"><strong>{detail ? 'Could not refresh' : 'Delegation unavailable'}</strong><span>{error}</span>{!detail && <button type="button" onClick={() => setRefresh(value => value + 1)}>Try again</button>}</div>}
    {!detail && !error ? <div className="live-loading"><span className="spinner" />Loading delegation…</div> : detail && <>
      <section className="delegation-hero"><div className="eyebrow">DELEGATION / {detail.id}</div><div className="detail-title-line"><div><h1 ref={heroHeading} tabIndex={-1}>{detail.objective || 'Delegation'}</h1><div className="delegation-subtitle">{detail.provider} <i>·</i> {detail.workspace}</div></div><span className={`live-state large ${stateTone(detail.state)}`}>{detail.waiting ? 'Needs your approval' : displayState(detail.state)}</span></div><div className="detail-updated"><Radio size={13} /> Live status <span>·</span> Updated {formatUpdated(detail.updatedAt)} <span>·</span> Revision {detail.revision}</div></section>
      {detail.waiting ? <WaitingCard waiting={detail.waiting} approving={approving} note={actionNote} onApprove={() => void approve()} /> : null}
      {detail.state === 'Failed' ? <FailureCard summary={detail.resultSummary} concerns={detail.unresolvedConcerns ?? []} /> : null}
      <EvidenceCard view={evidence} pending={terminalStates.includes(detail.state) && !evidence} />
      {detail.canCancel ? <div className="cancel-row"><button type="button" className="danger-button" disabled={cancelling} onClick={() => void cancel()}>{cancelling ? 'Cancelling…' : 'Cancel delegation'}</button>{cancelNote ? <p className="action-note" role="status">{cancelNote}</p> : null}</div> : null}
      {runId ? <div className="cancel-row"><button type="button" className="refresh-button" onClick={() => onOpenRun(runId)}>View run graph <ArrowRight size={15} /></button></div> : null}
      <section className="detail-metrics"><div><strong>{current.length}</strong><span>current steps</span></div><div><strong>{completed.length}</strong><span>completed steps</span></div><div><strong>{detail.workerCalls}</strong><span>worker calls</span></div><div><strong>{detail.retries}</strong><span>retries</span></div></section>
      <div className="live-detail-grid"><section className="live-card"><div className="live-card-heading"><div><span className="panel-icon"><Activity size={17} /></span><div><strong>Reported progress</strong><small>Current and completed step labels</small></div></div></div>
        {current.length > 0 && <div className="step-group"><div className="step-group-label"><span className="step-indicator active" /> CURRENT</div>{current.map((step, index) => <div className="live-step current-step" key={`${step}-${index}`}><span className="step-indicator active" /><span>{step}</span><span className="step-state-label">In progress</span></div>)}</div>}
        {completed.length > 0 && <div className="step-group"><div className="step-group-label"><span className="step-indicator done"><Check size={11} /></span> COMPLETED</div>{completed.map((step, index) => <div className="live-step" key={`${step}-${index}`}><span className="step-indicator done"><Check size={11} /></span><span>{step}</span><span className="step-state-label">Completed</span></div>)}</div>}
        {current.length === 0 && completed.length === 0 && <div className="steps-empty">The service has not reported any step labels yet.</div>}
      </section><aside className="live-card result-card"><div className="live-card-heading"><div><span className="panel-icon"><BookOpen size={17} /></span><div><strong>Result</strong><small>Terminal summary from the service</small></div></div></div>{detail.resultSummary ? <p className="result-summary">{detail.resultSummary}</p> : <div className="steps-empty">No result summary has been reported.</div>}<div className="result-meta"><span>Delegation ID</span><strong>{detail.id}</strong></div></aside></div>
      <div className="live-poll-note"><Radio size={13} /> Automatically refreshes every 5 seconds <span>·</span> Progress reflects the service report</div>
    </>}
  </main></>
}

function LiveRunScreen({ id, onRuns, onSample }: { id: string; onRuns: () => void; onSample: () => void }) {
  const [run, setRun] = useState<RunSnapshot | null>(null)
  const [journal, setJournal] = useState<JournalView | null>(null)
  const [error, setError] = useState('')
  const [refresh, setRefresh] = useState(0)
  useEffect(() => {
    let active = true
    let timer: number | undefined
    const load = async () => {
      try {
        const result = await getRunProjection(id)
        if (!active) return
        setRun(result)
        setError('')
        try {
          const view = await getRunJournal(id)
          if (active) setJournal(view)
        } catch {
          if (active) setJournal(null)
        }
      }
      catch (reason) { if (active) setError(reason instanceof Error ? reason.message : 'Could not load this run.') }
      finally { if (active) timer = window.setTimeout(load, 5000) }
    }
    void load()
    return () => { active = false; if (timer) window.clearTimeout(timer) }
  }, [id, refresh])
  if (error && !run) return <><LiveHeader onRuns={onRuns} onSample={onSample} /><main className="page"><div className="live-error" role="alert"><strong>Run unavailable</strong><span>{error}</span><button type="button" onClick={() => setRefresh(value => value + 1)}>Try again</button></div></main></>
  if (!run) return <><LiveHeader onRuns={onRuns} onSample={onSample} /><main className="page"><div className="live-loading"><span className="spinner" />Loading run…</div></main></>
  return <RunScreen run={run} onRuns={onRuns} live journal={journal} />
}

export function App() {
  const [path, setPath] = useState(window.location.pathname)
  const [run, setRun] = useState<RunSnapshot | null>(null)
  const samplePath = '/ui/sample/auth-refresh'
  const isSample = path === samplePath || path === '/ui/' || path === '/ui' || path === '/ui/sample'
  const navigate = (next: string) => { window.history.pushState({}, '', next); setPath(next) }
  useEffect(() => { const onPop = () => setPath(window.location.pathname); window.addEventListener('popstate', onPop); return () => window.removeEventListener('popstate', onPop) }, [])
  useEffect(() => {
    if (!isSample) return
    const controller = new AbortController()
    void sampleSource.getRun(sampleRun.id, controller.signal).then(setRun).catch(error => { if (error?.name !== 'AbortError') console.error(error) })
    return () => controller.abort()
  }, [path, isSample])
  if (isSample) return run ? <RunScreen run={run} onRuns={() => navigate('/ui/runs')} /> : <div className="loading">Loading sample workflow…</div>
  const runMatch = path.match(/^\/ui\/runs\/([^/]+)\/?$/)
  if (runMatch) return <LiveRunScreen id={decodeURIComponent(runMatch[1])} onRuns={() => navigate('/ui/delegations')} onSample={() => navigate(samplePath)} />
  const delegationMatch = path.match(/^\/ui\/delegations\/([^/]+)\/?$/)
  if (delegationMatch) return <DelegationDetailScreen id={decodeURIComponent(delegationMatch[1])} onBack={() => navigate('/ui/delegations')} onSample={() => navigate(samplePath)} onOpenRun={runId => navigate(`/ui/runs/${encodeURIComponent(runId)}`)} />
  if (path === '/ui/delegations' || path === '/ui/delegations/') return <DelegationList onOpen={id => navigate(`/ui/delegations/${encodeURIComponent(id)}`)} onSample={() => navigate(samplePath)} />
  return <RunsScreen onOpen={() => navigate(samplePath)} />
}

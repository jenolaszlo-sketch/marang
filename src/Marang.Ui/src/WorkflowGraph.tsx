import { useEffect, useMemo, useState } from 'react'
import ELK from 'elkjs/lib/elk.bundled.js'
import {
  Background, BackgroundVariant, Handle, MarkerType, Position, ReactFlow,
  ReactFlowProvider, useReactFlow, type Edge, type Node, type NodeProps,
} from '@xyflow/react'
import { Check, Circle, Flag, GitBranch, Maximize2, Play, Search, Sparkles, X } from 'lucide-react'
import type { RunSnapshot, WorkflowNode } from './contracts'

type WorkFlowNode = Node<{ work: WorkflowNode; selected: boolean }, 'work'>

const elk = new ELK()
const WIDTH = 220
const HEIGHT = 104

const glyphs = {
  completed: Check,
  running: Play,
  pending: Circle,
  waiting: Flag,
  failed: X,
}

function WorkCard({ data }: NodeProps<WorkFlowNode>) {
  const work = data.work
  const Icon = work.kind === 'checkpoint' ? GitBranch : glyphs[work.status]
  return (
    <div className={`work-node ${work.status} ${work.membership} ${data.selected ? 'selected' : ''}`}>
      <Handle type="target" position={Position.Left} isConnectable={false} />
      <div className="work-node-top">
        <span className="work-node-icon"><Icon size={15} strokeWidth={2.4} /></span>
        <span className="work-node-kind">{work.kind === 'checkpoint' ? 'Checkpoint' : work.kind === 'completion' ? 'Outcome' : work.kind}</span>
        {work.status === 'running' && <span className="live-pulse" aria-label="Running" />}
      </div>
      <div className="work-node-name">{work.name}</div>
      <div className="work-node-foot">
        <span>{work.membership === 'superseded' ? 'Superseded' : work.status}</span>
        {work.duration && <span>{work.duration}</span>}
      </div>
      {work.progress !== undefined && <div className="node-progress"><span style={{ width: `${work.progress}%` }} /></div>}
      <Handle type="source" position={Position.Right} isConnectable={false} />
    </div>
  )
}

const nodeTypes = { work: WorkCard }

function GraphControls({ focusActive }: { focusActive: () => void }) {
  const flow = useReactFlow<WorkFlowNode>()
  return (
    <div className="graph-controls">
      <button type="button" onClick={() => void flow.fitView({ padding: 0.2, duration: 250 })} title="Fit workflow to view"><Maximize2 size={15} /> Fit</button>
      <button type="button" onClick={focusActive} title="Focus active work"><Sparkles size={15} /> Focus active</button>
    </div>
  )
}

function GraphInner({ run, selectedId, onSelect }: { run: RunSnapshot; selectedId: string; onSelect: (id: string) => void }) {
  const [positions, setPositions] = useState<Record<string, { x: number; y: number }>>({})
  const [query, setQuery] = useState('')
  const [searchOpen, setSearchOpen] = useState(false)
  const flow = useReactFlow<WorkFlowNode>()

  const topologyKey = useMemo(() => `${run.currentRevisionId}:${run.nodes.map(n => n.id).join(',')}:${run.edges.map(e => e.id).join(',')}`, [run])

  useEffect(() => {
    let cancelled = false
    const graph = {
      id: 'workflow',
      layoutOptions: {
        'elk.algorithm': 'layered',
        'elk.direction': 'RIGHT',
        'elk.spacing.nodeNode': '48',
        'elk.layered.spacing.nodeNodeBetweenLayers': '74',
      },
      children: run.nodes.map(n => ({ id: n.id, width: WIDTH, height: HEIGHT })),
      edges: run.edges.map(e => ({ id: e.id, sources: [e.source], targets: [e.target] })),
    }
    void elk.layout(graph).then(result => {
      if (cancelled) return
      setPositions(Object.fromEntries((result.children ?? []).map(n => [n.id, { x: n.x ?? 0, y: n.y ?? 0 }])))
    })
    return () => { cancelled = true }
    // A progress update does not change topology, so layout remains stable.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [topologyKey])

  const nodes: WorkFlowNode[] = useMemo(() => run.nodes.map((work, index) => ({
    id: work.id,
    type: 'work',
    position: positions[work.id] ?? { x: index * 260, y: 80 },
    data: { work, selected: work.id === selectedId },
    draggable: false,
    selectable: true,
    sourcePosition: Position.Right,
    targetPosition: Position.Left,
    style: { width: WIDTH, height: HEIGHT },
  })), [run.nodes, selectedId, positions])

  const edges: Edge[] = useMemo(() => run.edges.map(edge => ({
    id: edge.id,
    source: edge.source,
    target: edge.target,
    type: 'smoothstep',
    animated: false,
    className: edge.membership === 'superseded' ? 'edge-superseded' : 'edge-current',
    markerEnd: { type: MarkerType.ArrowClosed, width: 14, height: 14, color: edge.membership === 'superseded' ? '#b9b9b3' : '#80a19b' },
    style: { stroke: edge.membership === 'superseded' ? '#b9b9b3' : '#80a19b', strokeWidth: edge.membership === 'superseded' ? 1.5 : 2, strokeDasharray: edge.membership === 'superseded' ? '5 5' : undefined },
  })), [run.edges])

  useEffect(() => {
    if (!Object.keys(positions).length) return
    const frame = requestAnimationFrame(() => {
      const meaningful = nodes.filter(n => n.data.work.kind === 'checkpoint' || n.data.work.status === 'running' || n.data.work.id === 'finish')
      if (meaningful.length) void flow.fitView({ nodes: meaningful, padding: 0.18, duration: 0, maxZoom: 0.95 })
    })
    return () => cancelAnimationFrame(frame)
    // The initial viewpoint changes only when topology receives a new layout.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [positions])

  const focusActive = () => {
    const active = nodes.filter(n => n.data.work.status === 'running')
    if (active.length) void flow.fitView({ nodes: active, padding: 0.8, duration: 250 })
  }

  const matches = run.nodes.filter(n => n.name.toLowerCase().includes(query.toLowerCase()))
  return (
    <div className="graph-stage">
      <ReactFlow<WorkFlowNode>
        nodes={nodes} edges={edges} nodeTypes={nodeTypes}
        onNodeClick={(_, node) => onSelect(node.id)}
        nodesConnectable={false} nodesDraggable={false} elementsSelectable
        fitView fitViewOptions={{ padding: 0.2 }} minZoom={0.35} maxZoom={1.5}
        proOptions={{ hideAttribution: true }}
        ariaLabelConfig={{ 'node.a11yDescription.default': 'Workflow step. Select to inspect.' }}
      >
        <Background variant={BackgroundVariant.Dots} gap={22} size={1} color="#deded8" />
      </ReactFlow>
      <GraphControls focusActive={focusActive} />
      <div className="graph-search">
        <button type="button" className="icon-button" aria-label="Search workflow" title="Search workflow" onClick={() => setSearchOpen(!searchOpen)}><Search size={17} /></button>
        {searchOpen && <div className="graph-search-popover">
          <label htmlFor="node-search">Find a step</label>
          <input id="node-search" autoFocus value={query} onChange={e => setQuery(e.target.value)} placeholder="Search workflow" />
          <div className="graph-search-results">{matches.map(node => <button type="button" key={node.id} onClick={() => {
            onSelect(node.id)
            const match = nodes.find(n => n.id === node.id)
            if (match) void flow.setCenter(match.position.x + WIDTH / 2, match.position.y + HEIGHT / 2, { zoom: 1, duration: 250 })
            setSearchOpen(false)
          }}>{node.name}<small>{node.status}</small></button>)}</div>
        </div>}
      </div>
      <div className="graph-legend" aria-label="Workflow legend">
        <span><i className="legend-dot completed" /> Completed</span>
        <span><i className="legend-dot running" /> Running</span>
        <span><i className="legend-dot pending" /> Pending</span>
        <span><i className="legend-line" /> Superseded</span>
      </div>
    </div>
  )
}

export function WorkflowGraph(props: { run: RunSnapshot; selectedId: string; onSelect: (id: string) => void }) {
  return <ReactFlowProvider><GraphInner {...props} /></ReactFlowProvider>
}

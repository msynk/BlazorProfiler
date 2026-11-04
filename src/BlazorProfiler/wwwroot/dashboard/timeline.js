// Canvas timeline with lanes, zoom, pan, range selection, search, and causal-link highlighting.
import { color, fmtMs } from './util.js';

export const LANES = [
  { id: 'interaction', label: 'Interactions', color: '--lane-interaction', types: ['eventHandler', 'browserInteraction'] },
  { id: 'operation', label: 'Operations', color: '--lane-operation', types: ['operation'] },
  { id: 'navigation', label: 'Navigation', color: '--lane-navigation', types: ['navigation'] },
  { id: 'batch', label: 'Render batches', color: '--lane-batch', types: ['renderBatch'] },
  { id: 'component', label: 'Components', color: '--lane-component', types: ['componentRender', 'parametersSet', 'lifecycle', 'componentCreated', 'componentDisposed'] },
  { id: 'http', label: 'HTTP', color: '--lane-http', types: ['http'] },
  { id: 'interop', label: 'JS interop', color: '--lane-interop', types: ['jsInterop'] },
  { id: 'circuit', label: 'Circuit', color: '--lane-circuit', types: ['circuit', 'circuitActivity'] },
  { id: 'browser', label: 'Browser', color: '--lane-browser', types: ['browserLongTask', 'browserResource'] },
  { id: 'runtime', label: 'Runtime (heap)', color: '--lane-marker', types: ['runtime'] },
  { id: 'marker', label: 'Markers', color: '--lane-marker', types: ['marker'] },
];

const LANE_BY_TYPE = new Map(LANES.flatMap(l => l.types.map(t => [t, l.id])));
const LABEL_WIDTH = 120;
const ROW = 14;
const MAX_ROWS = 3;
const AXIS = 22;

export function laneOf(e) {
  return LANE_BY_TYPE.get(e.type) || 'marker';
}

export class Timeline {
  constructor({ onSelect, onRange }) {
    this.onSelect = onSelect;
    this.onRange = onRange;
    this.events = [];
    this.byLane = new Map();
    this.visible = new Set(LANES.map(l => l.id));
    this.search = '';
    this.selected = null;
    this.range = null;
    this.follow = true;
    this.viewStart = 0;
    this.viewEnd = 10000;
    this.maxMs = 0;
    this.hits = [];
    this.canvas = document.createElement('canvas');
    this.canvas.tabIndex = 0;
    this.canvas.setAttribute('role', 'img');
    this.canvas.setAttribute('aria-label', 'Timeline. Use plus and minus to zoom, arrow keys to pan, click an event for details, shift and drag to select a range.');
    this.wrap = document.createElement('div');
    this.wrap.className = 'timeline-wrap';
    this.wrap.appendChild(this.canvas);
    this.attach();
    this.resizeObserver = new ResizeObserver(() => this.draw());
    this.resizeObserver.observe(this.wrap);
  }

  get element() {
    return this.wrap;
  }

  destroy() {
    this.resizeObserver.disconnect();
  }

  /** Sets the events and a function converting an event timestamp to milliseconds since session start. */
  setData(events, toMs) {
    this.toMs = toMs;
    this.events = events;
    this.byLane = new Map(LANES.map(l => [l.id, []]));
    let max = 0;
    for (const e of events) {
      const start = toMs(e.timestamp);
      const end = toMs(e.timestamp + (e.duration || 0));
      e.__s = start;
      e.__e = end;
      if (end > max) {
        max = end;
      }

      this.byLane.get(laneOf(e)).push(e);
    }

    for (const [id, list] of this.byLane) {
      list.sort((a, b) => a.__s - b.__s);
      if (id !== 'runtime' && id !== 'marker') {
        assignRows(list);
      }
    }

    this.maxMs = max;
    if (this.follow) {
      const span = Math.max(1000, this.viewEnd - this.viewStart);
      this.viewEnd = Math.max(span, max + span * 0.02);
      this.viewStart = Math.max(0, this.viewEnd - span);
    }

    this.draw();
  }

  fit() {
    this.follow = false;
    this.viewStart = 0;
    this.viewEnd = Math.max(1000, this.maxMs * 1.02);
    this.draw();
  }

  zoom(factor, centerMs) {
    const center = centerMs ?? (this.viewStart + this.viewEnd) / 2;
    const span = Math.min(Math.max(2, (this.viewEnd - this.viewStart) * factor), Math.max(1000, this.maxMs * 1.5));
    this.viewStart = Math.max(0, center - (center - this.viewStart) * (span / (this.viewEnd - this.viewStart)));
    this.viewEnd = this.viewStart + span;
    this.follow = false;
    this.draw();
  }

  pan(deltaMs) {
    const span = this.viewEnd - this.viewStart;
    this.viewStart = Math.max(0, this.viewStart + deltaMs);
    this.viewEnd = this.viewStart + span;
    this.follow = false;
    this.draw();
  }

  focusEvent(e) {
    const span = Math.max(50, (e.__e - e.__s) * 6, Math.min(2000, this.viewEnd - this.viewStart));
    this.viewStart = Math.max(0, e.__s - span / 3);
    this.viewEnd = this.viewStart + span;
    this.follow = false;
    this.selected = e;
    this.draw();
  }

  height() {
    return AXIS + LANES.filter(l => this.visible.has(l.id)).reduce((sum, l) => sum + laneHeight(l.id), 0) + 6;
  }

  x(ms, width) {
    return LABEL_WIDTH + ((ms - this.viewStart) / (this.viewEnd - this.viewStart)) * (width - LABEL_WIDTH - 8);
  }

  msAt(px, width) {
    return this.viewStart + ((px - LABEL_WIDTH) / (width - LABEL_WIDTH - 8)) * (this.viewEnd - this.viewStart);
  }

  draw() {
    const canvas = this.canvas;
    const width = this.wrap.clientWidth || 800;
    const height = this.height();
    const ratio = window.devicePixelRatio || 1;
    canvas.width = width * ratio;
    canvas.height = height * ratio;
    canvas.style.height = `${height}px`;
    const ctx = canvas.getContext('2d');
    ctx.scale(ratio, ratio);
    ctx.clearRect(0, 0, width, height);
    ctx.font = '11px system-ui, sans-serif';
    this.hits = [];
    const muted = color('--muted');
    const border = color('--border');
    const text = color('--text');
    const search = this.search.toLowerCase();
    const selectedOp = this.selected && this.selected.operationId ? this.selected.operationId : 0;

    // Axis.
    ctx.fillStyle = muted;
    ctx.strokeStyle = border;
    const span = this.viewEnd - this.viewStart;
    const step = niceStep(span / 8);
    for (let t = Math.ceil(this.viewStart / step) * step; t <= this.viewEnd; t += step) {
      const px = this.x(t, width);
      ctx.beginPath();
      ctx.moveTo(px, AXIS - 6);
      ctx.lineTo(px, height);
      ctx.stroke();
      ctx.fillText(fmtMs(t), px + 2, 12);
    }

    // Range selection.
    if (this.range) {
      const a = this.x(Math.min(this.range.start, this.range.end), width);
      const b = this.x(Math.max(this.range.start, this.range.end), width);
      ctx.fillStyle = 'rgba(91, 63, 209, 0.12)';
      ctx.fillRect(a, AXIS, Math.max(1, b - a), height - AXIS);
    }

    let y = AXIS;
    for (const lane of LANES) {
      if (!this.visible.has(lane.id)) {
        continue;
      }

      const laneH = laneHeight(lane.id);
      ctx.fillStyle = text;
      ctx.fillText(lane.label, 6, y + 12);
      ctx.strokeStyle = border;
      ctx.beginPath();
      ctx.moveTo(0, y + laneH);
      ctx.lineTo(width, y + laneH);
      ctx.stroke();

      const list = this.byLane.get(lane.id) || [];
      const fill = color(lane.color);
      if (lane.id === 'runtime') {
        this.drawHeap(ctx, list, y, laneH, width, fill);
      } else {
        const first = lowerBound(list, this.viewStart - 60000);
        let lastPx = -1;
        let lastRow = -1;
        for (let i = first; i < list.length; i++) {
          const e = list[i];
          if (e.__s > this.viewEnd) {
            break;
          }

          if (e.__e < this.viewStart) {
            continue;
          }

          const x1 = this.x(e.__s, width);
          const x2 = Math.max(x1 + 2, this.x(e.__e, width));
          const row = Math.min(e.__row || 0, MAX_ROWS - 1);
          // Skip sub-pixel duplicates in dense regions (they are still reachable by zooming in).
          if (Math.floor(x1) === lastPx && row === lastRow && x2 - x1 < 2 && e !== this.selected) {
            continue;
          }

          lastPx = Math.floor(x1);
          lastRow = row;
          const ry = y + 3 + row * ROW;
          const matches = !search || describeShort(e).toLowerCase().includes(search);
          ctx.globalAlpha = matches ? 1 : 0.15;
          ctx.fillStyle = e.outcome === 'Error' ? color('--bad') : fill;
          if (lane.id === 'marker') {
            ctx.fillStyle = e.markerType === 'EventsDropped' ? color('--bad') : fill;
            ctx.beginPath();
            ctx.moveTo(x1, ry);
            ctx.lineTo(x1 + 5, ry + 6);
            ctx.lineTo(x1, ry + 12);
            ctx.fill();
          } else {
            ctx.fillRect(x1, ry, x2 - x1, ROW - 3);
          }

          if (selectedOp && e.operationId === selectedOp) {
            ctx.strokeStyle = text;
            ctx.lineWidth = e === this.selected ? 2 : 1;
            ctx.strokeRect(x1 - 0.5, ry - 0.5, x2 - x1 + 1, ROW - 2);
          }

          if (x2 - x1 > 60) {
            ctx.fillStyle = '#fff';
            ctx.fillText(describeShort(e).slice(0, Math.floor((x2 - x1) / 6)), x1 + 3, ry + 9);
          }

          ctx.globalAlpha = 1;
          this.hits.push({ x1, x2: Math.max(x2, x1 + 5), y1: ry, y2: ry + ROW, e });
        }
      }

      y += laneH;
    }
  }

  drawHeap(ctx, list, y, laneH, width, fill) {
    const points = list.filter(e => (e.heapSizeBytes ?? e.totalMemoryBytes) != null && e.__e >= this.viewStart && e.__s <= this.viewEnd);
    if (!points.length) {
      ctx.fillStyle = color('--muted');
      ctx.fillText('no samples in view', LABEL_WIDTH + 4, y + 14);
      return;
    }

    const values = points.map(e => e.heapSizeBytes ?? e.totalMemoryBytes);
    const max = Math.max(...values) || 1;
    ctx.strokeStyle = fill;
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    points.forEach((e, i) => {
      const px = this.x(e.__e, width);
      const py = y + laneH - 4 - (values[i] / max) * (laneH - 8);
      if (i) {
        ctx.lineTo(px, py);
      } else {
        ctx.moveTo(px, py);
      }

      this.hits.push({ x1: px - 3, x2: px + 3, y1: y, y2: y + laneH, e });
    });
    ctx.stroke();
  }

  hitTest(px, py) {
    let best = null;
    for (const hit of this.hits) {
      if (px >= hit.x1 - 2 && px <= hit.x2 + 2 && py >= hit.y1 && py <= hit.y2) {
        if (!best || (hit.x2 - hit.x1) < (best.x2 - best.x1)) {
          best = hit;
        }
      }
    }

    return best ? best.e : null;
  }

  attach() {
    const canvas = this.canvas;
    let drag = null;
    canvas.addEventListener('mousedown', e => {
      const rect = canvas.getBoundingClientRect();
      drag = { x: e.clientX - rect.left, startView: this.viewStart, select: e.shiftKey, moved: false };
      if (drag.select) {
        const ms = this.msAt(drag.x, rect.width);
        this.range = { start: ms, end: ms };
      }
    });
    window.addEventListener('mousemove', e => {
      if (!drag) {
        return;
      }

      const rect = canvas.getBoundingClientRect();
      const px = e.clientX - rect.left;
      if (Math.abs(px - drag.x) > 3) {
        drag.moved = true;
      }

      if (drag.select) {
        this.range.end = this.msAt(px, rect.width);
        this.draw();
      } else if (drag.moved) {
        const msPerPx = (this.viewEnd - this.viewStart) / (rect.width - LABEL_WIDTH - 8);
        const span = this.viewEnd - this.viewStart;
        this.viewStart = Math.max(0, drag.startView - (px - drag.x) * msPerPx);
        this.viewEnd = this.viewStart + span;
        this.follow = false;
        this.draw();
      }
    });
    window.addEventListener('mouseup', e => {
      if (!drag) {
        return;
      }

      const rect = canvas.getBoundingClientRect();
      if (drag.select && drag.moved) {
        const start = Math.min(this.range.start, this.range.end);
        const end = Math.max(this.range.start, this.range.end);
        this.range = { start, end };
        this.onRange?.(this.range);
      } else if (!drag.moved && e.target === canvas) {
        const hit = this.hitTest(e.clientX - rect.left, e.clientY - rect.top);
        this.selected = hit;
        this.draw();
        if (hit) {
          this.onSelect?.(hit);
        }
      }

      drag = null;
    });
    canvas.addEventListener('wheel', e => {
      e.preventDefault();
      const rect = canvas.getBoundingClientRect();
      if (e.shiftKey) {
        this.pan(((e.deltaY || e.deltaX) / rect.width) * (this.viewEnd - this.viewStart));
      } else {
        this.zoom(e.deltaY > 0 ? 1.25 : 0.8, this.msAt(e.clientX - rect.left, rect.width));
      }
    }, { passive: false });
    canvas.addEventListener('keydown', e => {
      const span = this.viewEnd - this.viewStart;
      switch (e.key) {
        case '+':
        case '=':
          this.zoom(0.8);
          break;
        case '-':
          this.zoom(1.25);
          break;
        case 'ArrowLeft':
          this.pan(-span * 0.1);
          break;
        case 'ArrowRight':
          this.pan(span * 0.1);
          break;
        case '0':
          this.fit();
          break;
        case 'Escape':
          this.selected = null;
          this.range = null;
          this.draw();
          this.onRange?.(null);
          break;
        default:
          return;
      }

      e.preventDefault();
    });
  }
}

function laneHeight(id) {
  return id === 'runtime' ? 40 : id === 'marker' ? 20 : 3 + ROW * MAX_ROWS + 3;
}

function assignRows(list) {
  const rowEnds = [];
  for (const e of list) {
    let row = rowEnds.findIndex(end => end <= e.__s);
    if (row < 0) {
      row = rowEnds.length < MAX_ROWS ? rowEnds.length : MAX_ROWS - 1;
    }

    rowEnds[row] = Math.max(rowEnds[row] ?? 0, e.__e);
    e.__row = row;
  }
}

function lowerBound(list, ms) {
  let lo = 0;
  let hi = list.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (list[mid].__s < ms) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }

  return lo;
}

function niceStep(raw) {
  const pow = Math.pow(10, Math.floor(Math.log10(Math.max(raw, 0.001))));
  const n = raw / pow;
  return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * pow;
}

/** A short label for an event. */
export function describeShort(e) {
  const short = t => (t ? t.slice(t.lastIndexOf('.') + 1) : '?');
  switch (e.type) {
    case 'eventHandler': return `${e.attributeName || 'event'} → ${short(e.componentType)}.${e.methodName || '?'}`;
    case 'browserInteraction': return `${e.eventType || 'interaction'} (browser)`;
    case 'renderBatch': return `Batch: ${e.updatedComponentCount >= 0 ? e.updatedComponentCount : '?'} components`;
    case 'componentRender': return `Render ${short(e.componentType)}`;
    case 'parametersSet': return `Params ${short(e.componentType)}`;
    case 'lifecycle': return `${e.method} ${short(e.componentType)}`;
    case 'componentCreated': return `Created ${short(e.componentType)}`;
    case 'componentDisposed': return `Disposed ${short(e.componentType)}`;
    case 'componentChildren': return `Children of ${short(e.componentType)}`;
    case 'renderRequest': return `StateHasChanged ${short(e.componentType)} (${e.result})`;
    case 'navigation': return `Navigate ${e.route || short(e.componentType)}`;
    case 'http': return `${e.method} ${e.url}`;
    case 'jsInterop': return `${e.direction === 'JSToDotNet' ? '←' : '→'} ${e.identifier}`;
    case 'runtime': return 'Runtime sample';
    case 'circuit': return `Circuit ${e.change}`;
    case 'circuitActivity': return `Circuit ${e.activityName || 'activity'}`;
    case 'browserLongTask': return `${e.entryType || 'Long task'}`;
    case 'browserResource': return `${e.initiatorType || 'resource'} ${e.url}`;
    case 'operation': return e.name || 'Operation';
    case 'marker': return e.name || e.markerType || 'Marker';
    default: return e.type;
  }
}

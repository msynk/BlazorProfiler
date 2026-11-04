// Shared helpers: DOM creation, formatting, virtualized lists, and small canvas charts.

export const settings = {
  timeUnit: 'auto',
  byteUnit: 'binary',
  rangeSeconds: 0,
  refreshMs: 1000,
  theme: 'system',
};

const SETTINGS_KEY = 'blazor-profiler.settings';

export function loadSettings() {
  try {
    Object.assign(settings, JSON.parse(localStorage.getItem(SETTINGS_KEY) || '{}'));
  } catch { /* storage unavailable */ }
}

export function saveSettings() {
  try {
    localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
  } catch { /* storage unavailable */ }
}

/** Creates an element. Text children are inserted as text nodes, never as HTML. */
export function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  if (attrs) {
    for (const [key, value] of Object.entries(attrs)) {
      if (value === undefined || value === null || value === false) {
        continue;
      }

      if (key === 'class') {
        el.className = value;
      } else if (key === 'style' && typeof value === 'object') {
        Object.assign(el.style, value);
      } else if (key.startsWith('on') && typeof value === 'function') {
        el.addEventListener(key.slice(2).toLowerCase(), value);
      } else if (key === 'dataset') {
        Object.assign(el.dataset, value);
      } else if (value === true) {
        el.setAttribute(key, '');
      } else {
        el.setAttribute(key, String(value));
      }
    }
  }

  append(el, children);
  return el;
}

function append(el, children) {
  for (const child of children) {
    if (child === null || child === undefined || child === false) {
      continue;
    }

    if (Array.isArray(child)) {
      append(el, child);
    } else if (child instanceof Node) {
      el.appendChild(child);
    } else {
      el.appendChild(document.createTextNode(String(child)));
    }
  }
}

export function clear(el) {
  while (el.firstChild) {
    el.removeChild(el.firstChild);
  }

  return el;
}

export function fmtMs(ms) {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) {
    return '—';
  }

  const unit = settings.timeUnit;
  if (unit === 's' || (unit === 'auto' && Math.abs(ms) >= 1000)) {
    return `${(ms / 1000).toFixed(ms >= 10000 ? 1 : 2)} s`;
  }

  if (Math.abs(ms) < 1) {
    return `${ms.toFixed(3)} ms`;
  }

  return `${ms.toFixed(ms < 10 ? 2 : ms < 100 ? 1 : 0)} ms`;
}

export function fmtBytes(bytes) {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes)) {
    return '—';
  }

  const base = settings.byteUnit === 'decimal' ? 1000 : 1024;
  const units = settings.byteUnit === 'decimal' ? ['B', 'kB', 'MB', 'GB'] : ['B', 'KiB', 'MiB', 'GiB'];
  let value = bytes;
  let i = 0;
  while (Math.abs(value) >= base && i < units.length - 1) {
    value /= base;
    i++;
  }

  return `${value.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

export function fmtNum(n, digits = 0) {
  if (n === null || n === undefined || !Number.isFinite(n)) {
    return '—';
  }

  return Number(n).toLocaleString(undefined, { maximumFractionDigits: digits });
}

export function fmtPct(ratio) {
  return Number.isFinite(ratio) ? `${(ratio * 100).toFixed(ratio < 0.1 ? 1 : 0)}%` : '—';
}

export function shortType(type) {
  if (!type) {
    return '(unknown)';
  }

  const generic = type.indexOf('`');
  const name = generic > 0 ? type.slice(0, generic) : type;
  const dot = name.lastIndexOf('.');
  return dot >= 0 ? name.slice(dot + 1) : name;
}

export function debounce(fn, ms) {
  let timer = 0;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}

export function qualityBadge(quality) {
  const q = (quality || 'Measured').toString();
  return h('span', { class: `badge ${q.toLowerCase()}`, title: qualityTitle(q) }, q.toLowerCase());
}

function qualityTitle(q) {
  switch (q) {
    case 'Estimated': return 'Estimated value (for example from sampling or payload-size heuristics).';
    case 'Inferred': return 'Inferred from other evidence, not measured directly.';
    case 'Unavailable': return 'Not available on this platform or with the current configuration.';
    default: return 'Measured directly.';
  }
}

export function emptyState(title, text) {
  return h('div', { class: 'empty', role: 'status' }, h('strong', null, title), text || '');
}

/**
 * A virtualized list with fixed row height. Only visible rows are in the DOM, so large event lists stay responsive.
 * Rows are focusable and support Arrow/Home/End/Enter keyboard navigation.
 */
export function virtualList({ rows, rowHeight = 28, height = 460, columns, header, renderRow, onSelect, label }) {
  const container = h('div', { class: 'vlist', style: { height: `${height}px` }, role: 'grid', 'aria-label': label || 'List', 'aria-rowcount': rows.length + 1 });
  const headerRow = header ? h('div', { class: 'vrow header', role: 'row', style: { gridTemplateColumns: columns, height: `${rowHeight}px`, top: '0' } }, header.map(c => h('div', { role: 'columnheader' }, c))) : null;
  const inner = h('div', { class: 'vlist-inner', style: { height: `${rows.length * rowHeight + (header ? rowHeight : 0)}px` } });
  if (headerRow) {
    container.appendChild(headerRow);
  }

  container.appendChild(inner);
  let focusIndex = -1;
  let frame = 0;

  const render = () => {
    frame = 0;
    const offset = header ? rowHeight : 0;
    const start = Math.max(0, Math.floor((container.scrollTop - offset) / rowHeight) - 10);
    const end = Math.min(rows.length, Math.ceil((container.scrollTop + container.clientHeight) / rowHeight) + 10);
    clear(inner);
    for (let i = start; i < end; i++) {
      const row = h('div', {
        class: 'vrow',
        role: 'row',
        tabindex: i === focusIndex ? '0' : '-1',
        'aria-rowindex': i + 2,
        style: { top: `${i * rowHeight + offset}px`, height: `${rowHeight}px`, gridTemplateColumns: columns },
        dataset: { index: i },
      }, renderRow(rows[i], i));
      inner.appendChild(row);
    }
  };

  const schedule = () => {
    if (!frame) {
      frame = requestAnimationFrame(render);
    }
  };

  const focusRow = index => {
    focusIndex = Math.max(0, Math.min(rows.length - 1, index));
    const offset = header ? rowHeight : 0;
    const top = focusIndex * rowHeight + offset;
    if (top < container.scrollTop + offset) {
      container.scrollTop = top - offset;
    } else if (top + rowHeight > container.scrollTop + container.clientHeight) {
      container.scrollTop = top + rowHeight - container.clientHeight;
    }

    render();
    const el = inner.querySelector(`[data-index="${focusIndex}"]`);
    if (el) {
      el.focus();
    }
  };

  container.addEventListener('scroll', schedule, { passive: true });
  container.addEventListener('click', e => {
    const row = e.target.closest('.vrow[data-index]');
    if (row && onSelect) {
      focusIndex = Number(row.dataset.index);
      onSelect(rows[focusIndex], focusIndex);
    }
  });
  container.addEventListener('keydown', e => {
    if (!rows.length) {
      return;
    }

    const page = Math.max(1, Math.floor(container.clientHeight / rowHeight) - 1);
    const keys = { ArrowDown: 1, ArrowUp: -1, PageDown: page, PageUp: -page };
    if (e.key in keys) {
      e.preventDefault();
      focusRow((focusIndex < 0 ? -1 : focusIndex) + keys[e.key]);
    } else if (e.key === 'Home') {
      e.preventDefault();
      focusRow(0);
    } else if (e.key === 'End') {
      e.preventDefault();
      focusRow(rows.length - 1);
    } else if (e.key === 'Enter' && focusIndex >= 0 && onSelect) {
      onSelect(rows[focusIndex], focusIndex);
    }
  });
  container.tabIndex = 0;
  container.addEventListener('focus', () => {
    if (focusIndex < 0 && rows.length) {
      focusRow(0);
    }
  });

  requestAnimationFrame(render);
  return container;
}

/** Sortable table for modest row counts (aggregates). */
export function sortableTable({ columns, rows, onSelect, initialSort, label, maxHeight }) {
  let sortKey = initialSort?.key ?? columns[0].key;
  let sortDir = initialSort?.dir ?? -1;
  const wrap = h('div', { class: 'table-wrap', style: maxHeight ? { maxHeight: `${maxHeight}px` } : null });
  const table = h('table', { 'aria-label': label || 'Table' });
  wrap.appendChild(table);

  const render = () => {
    const sorted = [...rows].sort((a, b) => {
      const col = columns.find(c => c.key === sortKey);
      const va = col.sort ? col.sort(a) : a[sortKey];
      const vb = col.sort ? col.sort(b) : b[sortKey];
      if (va === vb) {
        return 0;
      }

      if (va === undefined || va === null) {
        return 1;
      }

      if (vb === undefined || vb === null) {
        return -1;
      }

      return (va > vb ? 1 : -1) * sortDir;
    });

    clear(table);
    table.appendChild(h('thead', null, h('tr', null, columns.map(col => h('th', {
      class: col.num ? 'num' : null,
      scope: 'col',
      'aria-sort': col.key === sortKey ? (sortDir > 0 ? 'ascending' : 'descending') : 'none',
    }, h('button', {
      type: 'button',
      onclick: () => {
        if (sortKey === col.key) {
          sortDir = -sortDir;
        } else {
          sortKey = col.key;
          sortDir = col.num ? -1 : 1;
        }

        render();
      },
    }, col.title, col.key === sortKey ? (sortDir > 0 ? ' ▲' : ' ▼') : ''))))));

    const body = h('tbody');
    for (const row of sorted) {
      const tr = h('tr', onSelect ? { tabindex: '0' } : null, columns.map(col => h('td', { class: col.num ? 'num' : null }, col.render ? col.render(row) : row[col.key])));
      if (onSelect) {
        tr.addEventListener('click', () => {
          table.querySelectorAll('tr.selected').forEach(r => r.classList.remove('selected'));
          tr.classList.add('selected');
          onSelect(row);
        });
        tr.addEventListener('keydown', e => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            tr.click();
          }
        });
      }

      body.appendChild(tr);
    }

    table.appendChild(body);
    if (!rows.length) {
      table.appendChild(h('tbody', null, h('tr', null, h('td', { colspan: columns.length, class: 'muted' }, 'No data.'))));
    }
  };

  render();
  return wrap;
}

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || '#888';
}

/** Draws a small multi-series line or bar chart on a canvas. Series: { label, color, values: [{x, y}] }. */
export function lineChart({ title, series, yFormat = v => fmtNum(v, 1), height = 160, bars = false }) {
  const wrap = h('div', { class: 'chart-wrap' });
  const titleEl = h('div', { class: 'chart-title' }, h('span', null, title), h('span', { class: 'legend' }, series.map(s => h('span', null, h('span', { class: 'swatch', style: { background: s.color } }), ' ', s.label))));
  const canvas = h('canvas', { class: 'chart', role: 'img', 'aria-label': `${title} chart` });
  wrap.append(titleEl, canvas);

  const draw = () => {
    const ratio = window.devicePixelRatio || 1;
    const width = canvas.clientWidth || 400;
    canvas.width = width * ratio;
    canvas.height = height * ratio;
    canvas.style.height = `${height}px`;
    const ctx = canvas.getContext('2d');
    ctx.scale(ratio, ratio);
    ctx.clearRect(0, 0, width, height);
    const points = series.flatMap(s => s.values);
    if (!points.length) {
      ctx.fillStyle = cssVar('--muted');
      ctx.font = '12px system-ui';
      ctx.fillText('No data in range', 10, height / 2);
      return;
    }

    const minX = Math.min(...points.map(p => p.x));
    const maxX = Math.max(...points.map(p => p.x), minX + 1);
    const maxY = Math.max(...points.map(p => p.y), 0) || 1;
    const left = 48;
    const bottom = height - 18;
    const plotW = width - left - 8;
    const plotH = bottom - 8;
    ctx.strokeStyle = cssVar('--border');
    ctx.fillStyle = cssVar('--muted');
    ctx.font = '11px system-ui';
    ctx.lineWidth = 1;
    for (let i = 0; i <= 3; i++) {
      const y = bottom - (plotH * i) / 3;
      ctx.beginPath();
      ctx.moveTo(left, y);
      ctx.lineTo(width - 8, y);
      ctx.stroke();
      ctx.fillText(yFormat((maxY * i) / 3), 2, y + 4);
    }

    ctx.fillText(`${Math.round(minX)}s`, left, height - 4);
    ctx.fillText(`${Math.round(maxX)}s`, width - 40, height - 4);
    const sx = x => left + ((x - minX) / (maxX - minX)) * plotW;
    const sy = y => bottom - (y / maxY) * plotH;
    series.forEach((s, index) => {
      ctx.strokeStyle = s.color;
      ctx.fillStyle = s.color;
      if (bars) {
        const barW = Math.max(1, plotW / Math.max(1, maxX - minX + 1) / series.length - 1);
        for (const p of s.values) {
          ctx.fillRect(sx(p.x) + index * barW, sy(p.y), barW, bottom - sy(p.y));
        }
      } else {
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        s.values.forEach((p, i) => (i ? ctx.lineTo(sx(p.x), sy(p.y)) : ctx.moveTo(sx(p.x), sy(p.y))));
        ctx.stroke();
      }
    });
  };

  requestAnimationFrame(draw);
  return wrap;
}

export function color(name) {
  return cssVar(name);
}

export function downloadBlob(blob, fileName) {
  const url = URL.createObjectURL(blob);
  const a = h('a', { href: url, download: fileName });
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 5000);
}

// JavaScript used by the interop demo.
let plotted = 0;

export function plot(x, y) {
  plotted += x + y;
  return plotted;
}

export function plotMany(points) {
  for (const p of points) {
    plotted += p.x + p.y;
  }

  return points.length;
}

export function wait(ms) {
  return new Promise(resolve => setTimeout(() => resolve(ms), ms));
}

export function tick(label) {
  const el = document.getElementById('leaky-status');
  if (el) {
    el.textContent = label;
  }
}

function boxes(count) {
  const host = document.getElementById('layout-boxes');
  if (!host) {
    return [];
  }

  while (host.children.length < count) {
    const box = document.createElement('div');
    box.style.cssText = 'width:20px;height:10px;background:#93c5fd';
    host.appendChild(box);
  }

  return [...host.children].slice(0, count);
}

// Layout thrashing: write a style, then read geometry, for every box. Each read forces a synchronous layout.
export function thrashLayout(count) {
  let total = 0;
  for (let round = 0; round < 5; round++) {
    for (const box of boxes(count)) {
      box.style.width = `${10 + Math.random() * 30}px`;
      total += box.offsetHeight + box.getBoundingClientRect().width;
    }
  }

  return total;
}

// The fix: all writes first, then all reads, so the browser computes layout once.
export function batchedLayout(count) {
  const list = boxes(count);
  for (const box of list) {
    box.style.width = `${10 + Math.random() * 30}px`;
  }

  let total = 0;
  for (const box of list) {
    total += box.offsetHeight + box.getBoundingClientRect().width;
  }

  return total;
}

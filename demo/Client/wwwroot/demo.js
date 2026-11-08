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

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

// A long task: keeps the main thread busy, so the frame that runs it is long (BP7002).
export function blockMainThread(ms) {
  const until = performance.now() + ms;
  let spins = 0;
  while (performance.now() < until) {
    spins++;
  }

  return spins;
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

// Calls .NET for every simulated pointer move, the way an unthrottled pointermove listener would (BP4007).
export function streamToDotNet(ref, count, intervalMs) {
  return new Promise(resolve => {
    let sent = 0;
    const timer = setInterval(() => {
      ref.invokeMethodAsync('OnPointerMove', sent, sent * 2);
      sent++;
      if (sent >= count) {
        clearInterval(timer);
        resolve(sent);
      }
    }, intervalMs);
  });
}

// The fix: collect moves in JavaScript and send them to .NET in one call (here: once at the end; in an app, at most once
// per animation frame).
export function streamBatchedToDotNet(ref, count, intervalMs) {
  return new Promise(resolve => {
    const moves = [];
    const timer = setInterval(() => {
      moves.push([moves.length, moves.length * 2]);
      if (moves.length >= count) {
        clearInterval(timer);
        ref.invokeMethodAsync('OnPointerMoves', moves).then(() => resolve(moves.length));
      }
    }, intervalMs);
  });
}

// A static JS-to-.NET call (DotNet.invokeMethodAsync).
export function pingStatic() {
  return DotNet.invokeMethodAsync('BlazorProfiler.Demo.Client', 'DemoPing');
}

// Streaming interop: reads a .NET stream (DotNetStreamReference) and returns its length.
export async function receiveStream(streamRef) {
  const data = await streamRef.arrayBuffer();
  return data.byteLength;
}

// Streaming interop: passes a JavaScript stream reference to .NET.
export function sendStream(ref, size) {
  return ref.invokeMethodAsync('ReceiveStream', DotNet.createJSStreamReference(new Uint8Array(size)));
}

// Binary interop: a byte[] argument arrives as a Uint8Array (on Blazor Server it travels in a message of its own).
export function receiveBytes(bytes) {
  return bytes.length;
}

// Streaming interop: returns data that .NET receives as a stream (InvokeAsync<IJSStreamReference>; the framework creates
// the reference from the returned typed array).
export function makeStream(size) {
  return new Uint8Array(size);
}

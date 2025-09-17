const personaProfiles = new Map();

const defaultTypingProfile = {
  clearFirst: false,
  charDelayMs: 120,
  varianceMs: 45,
  maxDelayMs: 4000,
  breathEvery: 12,
  breathDelayMs: 420,
  errorRate: 0.015,
  correctionDelayMs: 180
};

const defaultClickProfile = {
  preMoveDelayMs: 150,
  hoverMs: 120,
  pressDurationMs: 60,
  releaseDelayMs: 80,
  pointerSteps: 10,
  pointerJitterPx: 6,
  pointerDurationMs: 220,
  pointerVarianceMs: 80,
  targetXRatio: 0.5,
  targetYRatio: 0.5,
  targetVariancePx: 6,
  microAdjustments: 2,
  scrollAlignment: 'center'
};

const defaultScrollProfile = {
  direction: 'down',
  chunkPx: 280,
  chunks: 3,
  delayMs: 180,
  varianceMs: 60,
  stepCount: 8,
  stepDelayMs: 16
};

const defaultMouseProfile = {
  enable: true,
  steps: 12,
  jitterPx: 6,
  durationMs: 240,
  varianceMs: 90
};

const pointerState = {
  pointerId: 1,
  x: window.innerWidth / 2,
  y: window.innerHeight / 2,
  lastTarget: null
};

function wait(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function randomDelay(base, variance) {
  const jitter = (Math.random() - 0.5) * 2 * (variance ?? base * 0.35);
  return Math.max(8, base + jitter);
}

function easeInOutQuad(t) {
  return t < 0.5 ? 2 * t * t : -1 + (4 - 2 * t) * t;
}

function getKeyCodeForChar(char) {
  if (/^[a-z]$/i.test(char)) {
    return `Key${char.toUpperCase()}`;
  }

  if (/^[0-9]$/.test(char)) {
    return `Digit${char}`;
  }

  if (char === ' ') {
    return 'Space';
  }

  return undefined;
}

function dispatchPointerEvent(target, type, x, y, options = {}) {
  const eventInit = {
    bubbles: true,
    cancelable: true,
    view: window,
    pointerId: pointerState.pointerId,
    pointerType: 'mouse',
    isPrimary: true,
    clientX: x,
    clientY: y,
    buttons: options.buttons ?? 0,
    pressure: options.pressure ?? (type === 'pointerdown' ? 0.5 : 0),
    ...options
  };

  const pointerEvent = new PointerEvent(type, eventInit);
  target.dispatchEvent(pointerEvent);

  const mouseTypeMap = {
    pointerover: 'mouseover',
    pointerenter: 'mouseenter',
    pointermove: 'mousemove',
    pointerdown: 'mousedown',
    pointerup: 'mouseup',
    pointerout: 'mouseout',
    pointerleave: 'mouseleave'
  };

  const mouseType = mouseTypeMap[type];
  if (mouseType) {
    const mouseEvent = new MouseEvent(mouseType, eventInit);
    target.dispatchEvent(mouseEvent);
  }
}

function dispatchPointerFrame(type, x, y, options = {}, explicitTarget) {
  const target = explicitTarget || document.elementFromPoint(x, y) || document.body || document.documentElement;
  dispatchPointerEvent(target, type, x, y, options);
  pointerState.lastTarget = target;
}

async function smoothPointerMove(targetX, targetY, options) {
  const steps = Math.max(2, Math.floor(options.steps ?? defaultMouseProfile.steps));
  const jitterPx = options.jitterPx ?? defaultMouseProfile.jitterPx;
  const durationMs = options.durationMs ?? defaultMouseProfile.durationMs;
  const varianceMs = options.varianceMs ?? defaultMouseProfile.varianceMs;

  const startX = pointerState.x;
  const startY = pointerState.y;
  const controlX = startX + (targetX - startX) * (0.3 + Math.random() * 0.4) + (Math.random() - 0.5) * jitterPx * 2;
  const controlY = startY + (targetY - startY) * (0.3 + Math.random() * 0.4) + (Math.random() - 0.5) * jitterPx * 2;

  for (let i = 1; i <= steps; i += 1) {
    const progress = i / steps;
    const eased = easeInOutQuad(progress);
    const bezierT = eased;
    const deltaX = (1 - bezierT) * (1 - bezierT) * startX +
      2 * (1 - bezierT) * bezierT * controlX +
      bezierT * bezierT * targetX +
      (Math.random() - 0.5) * jitterPx;
    const deltaY = (1 - bezierT) * (1 - bezierT) * startY +
      2 * (1 - bezierT) * bezierT * controlY +
      bezierT * bezierT * targetY +
      (Math.random() - 0.5) * jitterPx;

    pointerState.x = deltaX;
    pointerState.y = deltaY;
    dispatchPointerFrame('pointermove', pointerState.x, pointerState.y, { pressure: 0.05 });
    dispatchPointerFrame('pointerrawupdate', pointerState.x, pointerState.y, { pressure: 0.04 });

    const delay = randomDelay(durationMs / steps, varianceMs / steps);
    await wait(delay);
  }
}

function applyValueInsertion(element, char) {
  if ('value' in element) {
    const input = element;
    const currentValue = input.value ?? '';
    const selectionStart = input.selectionStart ?? currentValue.length;
    const selectionEnd = input.selectionEnd ?? currentValue.length;
    const before = currentValue.slice(0, selectionStart);
    const after = currentValue.slice(selectionEnd);
    input.value = `${before}${char}${after}`;
    const newCaret = selectionStart + char.length;
    try {
      input.selectionStart = input.selectionEnd = newCaret;
    } catch (_) {
      // ignore selection errors for non-text inputs
    }
  } else {
    element.textContent = (element.textContent || '') + char;
  }
}

async function simulateBackspace(element, options) {
  const keyOptions = { key: 'Backspace', code: 'Backspace', bubbles: true, cancelable: true };
  element.dispatchEvent(new KeyboardEvent('keydown', keyOptions));

  if ('value' in element) {
    const input = element;
    const value = input.value ?? '';
    const selectionStart = input.selectionStart ?? value.length;
    const selectionEnd = input.selectionEnd ?? value.length;

    if (selectionStart === selectionEnd && selectionStart > 0) {
      const before = value.slice(0, selectionStart - 1);
      const after = value.slice(selectionEnd);
      input.value = `${before}${after}`;
      try {
        input.selectionStart = input.selectionEnd = selectionStart - 1;
      } catch (_) {
        // ignore selection errors
      }
    }
  } else {
    const text = element.textContent || '';
    element.textContent = text.slice(0, -1);
  }

  if (typeof InputEvent === 'function') {
    element.dispatchEvent(new InputEvent('input', { inputType: 'deleteContentBackward', data: null, bubbles: true }));
  } else {
    element.dispatchEvent(new Event('input', { bubbles: true }));
  }

  element.dispatchEvent(new KeyboardEvent('keyup', keyOptions));
  await wait(options.correctionDelayMs ?? 150);
}

function randomMistypeCharacter(sourceChar) {
  const alphabet = 'abcdefghijklmnopqrstuvwxyz';
  const index = Math.max(0, alphabet.indexOf(sourceChar.toLowerCase()));
  const offset = Math.random() < 0.5 ? -1 : 1;
  const candidate = alphabet[(index + offset + alphabet.length) % alphabet.length];
  return sourceChar === sourceChar.toUpperCase() ? candidate.toUpperCase() : candidate;
}

export function registerPersonaProfile(personaId, profile = {}) {
  if (!personaId) {
    return;
  }

  const normalized = {
    typing: profile.typing || {},
    click: profile.click || {},
    scroll: profile.scroll || {},
    mouse: profile.mouse || {}
  };

  personaProfiles.set(String(personaId), normalized);
}

export async function humanLikeType(selector, text, profile = {}) {
  const { persona, personaId, ...inlineOverrides } = profile || {};
  const personaKey = personaId || persona;
  const personaOverrides = personaKey ? personaProfiles.get(String(personaKey))?.typing : undefined;
  const options = mergeProfiles(defaultTypingProfile, personaOverrides, inlineOverrides);
  const element = document.querySelector(selector);
  if (!element) {
    throw new Error(`Element not found: ${selector}`);
  }

  element.focus();
  element.dispatchEvent(new FocusEvent('focus', { bubbles: true, relatedTarget: document.activeElement }));
  element.dispatchEvent(new FocusEvent('focusin', { bubbles: true, relatedTarget: document.activeElement }));

  if (options.clearFirst) {
    if (typeof InputEvent === 'function') {
      element.dispatchEvent(new InputEvent('beforeinput', { inputType: 'deleteByCommand', bubbles: true, cancelable: true }));
    }
    if ('value' in element) {
      element.value = '';
    } else {
      element.textContent = '';
    }
    element.dispatchEvent(new Event('input', { bubbles: true }));
  }

  let typedCount = 0;
  let totalDelay = 0;

  for (const char of text) {
    const sequence = [];
    const shouldMistype = options.errorRate > 0 && Math.random() < options.errorRate;
    if (shouldMistype) {
      sequence.push({ char: randomMistypeCharacter(char), countsTowardsTotal: false });
    }
    sequence.push({ char, countsTowardsTotal: true });

    for (let i = 0; i < sequence.length; i += 1) {
      const { char: currentChar, countsTowardsTotal } = sequence[i];
      const code = getKeyCodeForChar(currentChar);
      const keydownEvent = new KeyboardEvent('keydown', { key: currentChar, code, bubbles: true, cancelable: true });
      element.dispatchEvent(keydownEvent);

      let beforeInputCancelled = false;
      if (typeof InputEvent === 'function') {
        const beforeInput = new InputEvent('beforeinput', { data: currentChar, inputType: 'insertText', bubbles: true, cancelable: true });
        beforeInputCancelled = !element.dispatchEvent(beforeInput);
      }

      if (!beforeInputCancelled) {
        applyValueInsertion(element, currentChar);
        if (typeof InputEvent === 'function') {
          element.dispatchEvent(new InputEvent('input', { data: currentChar, inputType: 'insertText', bubbles: true }));
        } else {
          element.dispatchEvent(new Event('input', { bubbles: true }));
        }
      }

      element.dispatchEvent(new KeyboardEvent('keypress', { key: currentChar, code, bubbles: true }));
      element.dispatchEvent(new KeyboardEvent('keyup', { key: currentChar, code, bubbles: true }));

      if (countsTowardsTotal) {
        typedCount += 1;
      }

      if (shouldMistype && i === 0) {
        const correctionPause = options.correctionDelayMs + Math.random() * options.varianceMs;
        totalDelay += correctionPause;
        await wait(correctionPause);
        await simulateBackspace(element, options);
      } else {
        const delay = Math.min(options.maxDelayMs, Math.max(0, randomDelay(options.charDelayMs, options.varianceMs)));
        totalDelay += delay;
        await wait(delay);
      }
    }

    if (options.breathEvery > 0 && typedCount > 0 && typedCount % options.breathEvery === 0) {
      const pause = options.breathDelayMs + Math.random() * options.varianceMs;
      totalDelay += pause;
      await wait(pause);
    }
  }

  return { typed: typedCount, totalDelayMs: Math.round(totalDelay) };
}

export async function humanLikeClick(selector, profile = {}) {
  const { persona, personaId, ...inlineOverrides } = profile || {};
  const personaKey = personaId || persona;
  const personaOverrides = personaKey ? personaProfiles.get(String(personaKey))?.click : undefined;
  const options = mergeProfiles(defaultClickProfile, personaOverrides, inlineOverrides);
  const element = document.querySelector(selector);
  if (!element) {
    throw new Error(`Element not found: ${selector}`);
  }

  element.scrollIntoView({ behavior: 'smooth', block: options.scrollAlignment });
  await wait(options.preMoveDelayMs);

  const rect = element.getBoundingClientRect();
  const offsetX = (Math.random() - 0.5) * (options.targetVariancePx ?? 4);
  const offsetY = (Math.random() - 0.5) * (options.targetVariancePx ?? 4);
  const targetX = rect.left + rect.width * options.targetXRatio + offsetX;
  const targetY = rect.top + rect.height * options.targetYRatio + offsetY;

  await smoothPointerMove(targetX, targetY, {
    steps: options.pointerSteps,
    jitterPx: options.pointerJitterPx,
    durationMs: options.pointerDurationMs,
    varianceMs: options.pointerVarianceMs
  });

  dispatchPointerFrame('pointerover', pointerState.x, pointerState.y, { pressure: 0.05 }, element);
  await wait(options.hoverMs);
  dispatchPointerFrame('pointerenter', pointerState.x, pointerState.y, { pressure: 0.08 }, element);
  await wait(options.hoverMs / 2);

  dispatchPointerFrame('pointerdown', pointerState.x, pointerState.y, { buttons: 1, pressure: 0.65 }, element);
  await wait(options.pressDurationMs);
  dispatchPointerFrame('pointerup', pointerState.x, pointerState.y, { buttons: 0, pressure: 0 }, element);

  element.dispatchEvent(new MouseEvent('click', {
    bubbles: true,
    cancelable: true,
    clientX: pointerState.x,
    clientY: pointerState.y,
    view: window,
    detail: 1
  }));

  await wait(options.releaseDelayMs);

  for (let i = 0; i < (options.microAdjustments ?? 0); i += 1) {
    const microX = pointerState.x + (Math.random() - 0.5) * 3;
    const microY = pointerState.y + (Math.random() - 0.5) * 3;
    dispatchPointerFrame('pointermove', microX, microY, { pressure: 0.02 }, element);
    await wait(12 + Math.random() * 18);
  }
  return { clicked: true, target: selector };
}

async function performSmoothScroll(distance, options) {
  const steps = Math.max(1, Math.floor(options.stepCount));
  const stepDelay = Math.max(8, options.stepDelayMs);
  const perStep = distance / steps;

  for (let i = 0; i < steps; i += 1) {
    window.scrollBy({ top: perStep, left: 0, behavior: 'auto' });
    await wait(stepDelay + Math.random() * 6);
  }
}

export async function humanLikeScroll(profile = {}) {
  const { persona, personaId, ...inlineOverrides } = profile || {};
  const personaKey = personaId || persona;
  const personaOverrides = personaKey ? personaProfiles.get(String(personaKey))?.scroll : undefined;
  const options = mergeProfiles(defaultScrollProfile, personaOverrides, inlineOverrides);
  let total = 0;
  const directionMultiplier = options.direction === 'up' ? -1 : 1;

  for (let i = 0; i < options.chunks; i += 1) {
    const magnitude = options.chunkPx * (0.7 + Math.random() * 0.6);
    const delta = magnitude * directionMultiplier;
    await performSmoothScroll(delta, options);
    total += Math.abs(delta);
    await wait(randomDelay(options.delayMs, options.varianceMs));
  }

  return { scrolledPx: Math.round(total) };
}

export async function humanLikeMouseMove(profile = {}) {
  const { persona, personaId, ...inlineOverrides } = profile || {};
  const personaKey = personaId || persona;
  const personaOverrides = personaKey ? personaProfiles.get(String(personaKey))?.mouse : undefined;
  const options = mergeProfiles(defaultMouseProfile, personaOverrides, inlineOverrides);
  if (!options.enable) {
    return { moved: false };
  }

  let targetX = options.x;
  let targetY = options.y;

  if (!Number.isFinite(targetX) || !Number.isFinite(targetY)) {
    if (options.targetSelector) {
      const target = document.querySelector(options.targetSelector);
      if (target) {
        const rect = target.getBoundingClientRect();
        targetX = rect.left + rect.width / 2;
        targetY = rect.top + rect.height / 2;
      }
    }
  }

  if (!Number.isFinite(targetX) || !Number.isFinite(targetY)) {
    targetX = Math.random() * (window.innerWidth || document.documentElement.clientWidth || 1);
    targetY = Math.random() * (window.innerHeight || document.documentElement.clientHeight || 1);
  }

  await smoothPointerMove(targetX, targetY, options);
  return { moved: true, targetX: pointerState.x, targetY: pointerState.y };
}

function mergeProfiles(...profiles) {
  const result = {};
  profiles.filter(Boolean).forEach(profile => {
    Object.entries(profile).forEach(([key, value]) => {
      if (value === undefined) {
        return;
      }

      if (value && typeof value === 'object' && !Array.isArray(value)) {
        result[key] = mergeProfiles(result[key] || {}, value);
      } else {
        result[key] = value;
      }
    });
  });
  return result;
}

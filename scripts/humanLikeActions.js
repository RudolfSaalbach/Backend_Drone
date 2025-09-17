const defaultTypingProfile = {
  clearFirst: false,
  charDelayMs: 120,
  varianceMs: 45,
  maxDelayMs: 4000
};

const defaultClickProfile = {
  preMoveDelayMs: 150,
  hoverMs: 120
};

const defaultScrollProfile = {
  direction: 'down',
  chunkPx: 280,
  chunks: 3,
  delayMs: 180,
  varianceMs: 60
};

const defaultMouseProfile = {
  enable: true,
  hoverMs: 120,
  steps: 5
};

function wait(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function randomDelay(base, variance) {
  const delta = (Math.random() - 0.5) * 2 * variance;
  return Math.max(10, base + delta);
}

export async function humanLikeType(selector, text, profile = {}) {
  const options = { ...defaultTypingProfile, ...profile };
  const element = document.querySelector(selector);
  if (!element) {
    throw new Error(`Element not found: ${selector}`);
  }

  element.focus();
  element.dispatchEvent(new Event('focus', { bubbles: true }));

  if (options.clearFirst) {
    if ('value' in element) {
      element.value = '';
    } else {
      element.textContent = '';
    }
    element.dispatchEvent(new Event('input', { bubbles: true }));
  }

  let totalDelay = 0;
  let typedCount = 0;
  for (const char of text) {
    if ('value' in element) {
      element.value += char;
    } else {
      element.textContent = (element.textContent || '') + char;
    }

    element.dispatchEvent(new KeyboardEvent('keydown', { key: char, bubbles: true }));
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new KeyboardEvent('keyup', { key: char, bubbles: true }));

    const rawDelay = randomDelay(options.charDelayMs, options.varianceMs);
    const delay = Math.max(0, Math.min(rawDelay, options.maxDelayMs));
    totalDelay += delay;
    typedCount += 1;
    if (delay > 0) {
      await wait(delay);
    }
  }

  return { typed: typedCount, totalDelayMs: totalDelay };
}

export async function humanLikeClick(selector, profile = {}) {
  const options = { ...defaultClickProfile, ...profile };
  const element = document.querySelector(selector);
  if (!element) {
    throw new Error(`Element not found: ${selector}`);
  }

  element.scrollIntoView({ behavior: 'smooth', block: 'center' });
  await wait(options.preMoveDelayMs);

  const rect = element.getBoundingClientRect();
  const x = rect.left + rect.width / 2;
  const y = rect.top + rect.height / 2;

  const eventInit = { view: window, bubbles: true, cancelable: true, clientX: x, clientY: y };
  element.dispatchEvent(new MouseEvent('mouseenter', eventInit));
  await wait(options.hoverMs);
  element.dispatchEvent(new MouseEvent('mouseover', eventInit));
  await wait(options.hoverMs);
  element.dispatchEvent(new MouseEvent('mousedown', eventInit));
  await wait(50);
  element.dispatchEvent(new MouseEvent('mouseup', eventInit));
  element.dispatchEvent(new MouseEvent('click', eventInit));

  return { clicked: true };
}

export async function humanLikeScroll(profile = {}) {
  const options = { ...defaultScrollProfile, ...profile };
  let total = 0;

  for (let i = 0; i < options.chunks; i += 1) {
    const amount = options.direction === 'up' ? -options.chunkPx : options.chunkPx;
    window.scrollBy({ top: amount, behavior: 'smooth' });
    total += Math.abs(amount);
    await wait(randomDelay(options.delayMs, options.varianceMs));
  }

  return { scrolledPx: total };
}

export async function humanLikeMouseMove(profile = {}) {
  const options = { ...defaultMouseProfile, ...profile };
  if (!options.enable) {
    return { moved: false };
  }

  const rawSteps = Number(options.steps);
  const steps = Number.isFinite(rawSteps) ? Math.max(1, Math.floor(rawSteps)) : Math.max(1, defaultMouseProfile.steps);
  const waitPerStep = steps > 0 ? options.hoverMs / steps : options.hoverMs;

  for (let i = 0; i < steps; i += 1) {
    const x = Math.random() * window.innerWidth;
    const y = Math.random() * window.innerHeight;
    document.dispatchEvent(new MouseEvent('mousemove', { clientX: x, clientY: y, bubbles: true }));
    if (waitPerStep > 0) {
      await wait(waitPerStep);
    }
  }

  return { moved: true };
}

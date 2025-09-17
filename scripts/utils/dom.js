const VISIBILITY_THRESHOLD = 0.1;
const SHADOW_SELECTOR_CACHE = new Map();

export function isVisible(element) {
  if (!element) {
    return false;
  }

  const style = window.getComputedStyle(element);
  if (!style) {
    return false;
  }

  if (style.visibility === 'hidden' || style.display === 'none' || Number(style.opacity) === 0) {
    return false;
  }

  const rect = element.getBoundingClientRect();
  if (rect.width === 0 || rect.height === 0) {
    return false;
  }

  const inViewport =
    rect.bottom >= 0 &&
    rect.right >= 0 &&
    rect.top <= (window.innerHeight || document.documentElement.clientHeight) &&
    rect.left <= (window.innerWidth || document.documentElement.clientWidth);

  if (!inViewport) {
    return false;
  }

  const area = rect.width * rect.height;
  const viewportArea = (window.innerWidth || 1) * (window.innerHeight || 1);
  const areaRatio = area / viewportArea;
  return areaRatio >= VISIBILITY_THRESHOLD || isElementInteractable(element, rect, style);
}

export function getVisibilityMetrics(element) {
  if (!element) {
    return { visible: false, areaRatio: 0, inViewport: false, rect: null };
  }

  const rect = element.getBoundingClientRect();
  const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 1;
  const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 1;
  const viewportArea = viewportWidth * viewportHeight;
  const area = rect.width * rect.height;
  const inViewport =
    rect.bottom >= 0 &&
    rect.right >= 0 &&
    rect.top <= viewportHeight &&
    rect.left <= viewportWidth;

  return {
    visible: isVisible(element),
    areaRatio: viewportArea === 0 ? 0 : area / viewportArea,
    inViewport,
    rect
  };
}

export function elementContainsText(element, patterns) {
  if (!element) {
    return false;
  }

  const text = (element.textContent || element.value || '').trim();
  if (!text) {
    return false;
  }

  return patterns.some(pattern => pattern.test(text));
}

export function collectEvidence(elements) {
  return elements
    .filter(Boolean)
    .map(el => {
      const metrics = getVisibilityMetrics(el);
      return {
        selector: describeElement(el),
        areaRatio: Number(metrics.areaRatio.toFixed(3)),
        inViewport: metrics.inViewport,
        role: getElementRole(el),
        text: truncateTextContent(el)
      };
    });
}

export function queryDeep(selector, root = document) {
  if (!selector) {
    return [];
  }

  const results = new Set();
  const stack = [root];
  while (stack.length > 0) {
    const node = stack.pop();
    if (!node) {
      continue;
    }

    if (node instanceof Element || node instanceof Document || node instanceof ShadowRoot) {
      const scopedSelector = getScopedSelector(selector, node);
      node.querySelectorAll(scopedSelector).forEach(el => results.add(el));

      node.children?.forEach(child => {
        if (child.shadowRoot) {
          stack.push(child.shadowRoot);
        }
      });
    }
  }

  return Array.from(results);
}

export function computeOverlayScore(element) {
  if (!element) {
    return 0;
  }

  const metrics = getVisibilityMetrics(element);
  if (!metrics.visible) {
    return 0;
  }

  const style = window.getComputedStyle(element);
  const zIndex = Number.parseInt(style.zIndex || '0', 10) || 0;
  const fixedWeight = style.position === 'fixed' || style.position === 'sticky' ? 0.15 : 0;
  const backdropWeight = style.backdropFilter && style.backdropFilter !== 'none' ? 0.2 : 0;
  const dimWeight = style.backgroundColor && /rgba\(0, 0, 0, 0\.\d+\)/i.test(style.backgroundColor) ? 0.15 : 0;
  const areaWeight = Math.min(0.4, metrics.areaRatio * 1.5);
  const zIndexWeight = Math.min(0.3, Math.max(0, zIndex) / 100);
  return fixedWeight + backdropWeight + dimWeight + areaWeight + zIndexWeight;
}

export function getElementRole(element) {
  if (!(element instanceof Element)) {
    return undefined;
  }

  return element.getAttribute('role') || element.getAttribute('aria-role') || element.tagName.toLowerCase();
}

export function extractTextTokens(element) {
  if (!element) {
    return [];
  }

  const text = (element.textContent || '').trim().toLowerCase();
  if (!text) {
    return [];
  }

  return text.split(/[^a-z0-9]+/i).filter(Boolean);
}

export function gatherCandidates(selectors, { includeShadowDom = true } = {}) {
  const elements = new Set();
  selectors.forEach(selector => {
    document.querySelectorAll(selector).forEach(el => elements.add(el));
    if (includeShadowDom) {
      queryDeep(selector).forEach(el => elements.add(el));
    }
  });
  return Array.from(elements);
}

function isElementInteractable(element, rect, style) {
  if (style.pointerEvents === 'none') {
    return false;
  }

  const centerX = rect.left + rect.width / 2;
  const centerY = rect.top + rect.height / 2;
  const topElement = document.elementFromPoint(centerX, centerY);
  return topElement === element || element.contains(topElement);
}

function describeElement(element) {
  if (!(element instanceof Element)) {
    return 'unknown';
  }

  const parts = [element.tagName.toLowerCase()];
  if (element.id) {
    parts.push(`#${element.id}`);
  }
  if (element.classList && element.classList.length > 0) {
    parts.push(`.${Array.from(element.classList).slice(0, 3).join('.')}`);
  }

  return parts.join('');
}

function truncateTextContent(element, maxLength = 80) {
  const text = (element.textContent || '').trim();
  if (!text) {
    return '';
  }

  if (text.length <= maxLength) {
    return text;
  }

  return `${text.slice(0, maxLength)}…`;
}

function getScopedSelector(selector, node) {
  if (node === document || node === document.documentElement || node === document.body) {
    return selector;
  }

  if (!node.host) {
    return selector;
  }

  if (SHADOW_SELECTOR_CACHE.has(selector)) {
    return SHADOW_SELECTOR_CACHE.get(selector);
  }

  // Prefix :host to preserve context when querying inside a shadow root
  const scoped = selector
    .split(',')
    .map(segment => segment.trim())
    .filter(Boolean)
    .map(segment => segment.startsWith(':host') ? segment : `:host ${segment}`)
    .join(', ');
  SHADOW_SELECTOR_CACHE.set(selector, scoped);
  return scoped;
}

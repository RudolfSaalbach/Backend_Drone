const VISIBILITY_THRESHOLD = 0.1;

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
        inViewport: metrics.inViewport
      };
    });
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

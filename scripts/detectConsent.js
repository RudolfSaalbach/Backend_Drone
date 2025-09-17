import { collectEvidence, elementContainsText, getVisibilityMetrics, isVisible } from './utils/dom.js';

const CONSENT_SELECTORS = [
  '[class*="consent" i]',
  '[class*="cookie" i]',
  '[id*="consent" i]',
  '[id*="cookie" i]',
  '[aria-label*="consent" i]'
];

const ACTION_PATTERNS = [
  /accept/i,
  /agree/i,
  /allow/i,
  /preferences/i,
  /manage/i
];

const EXPLANATION_PATTERNS = [
  /we use cookies/i,
  /privacy/i,
  /consent/i,
  /personalise/i
];

export function detectConsent() {
  const matchedElements = [];
  const visited = new Set();
  let score = 0;

  const track = (element, weight) => {
    if (!element || visited.has(element) || !isVisible(element)) {
      return;
    }
    visited.add(element);
    matchedElements.push(element);
    score += weight;
  };

  CONSENT_SELECTORS.forEach(selector => {
    document.querySelectorAll(selector).forEach(el => track(el, 0.3));
  });

  const visibleButtons = Array.from(document.querySelectorAll('button, a, input[type="button"], input[type="submit"]'))
    .filter(el => isVisible(el) && elementContainsText(el, ACTION_PATTERNS));
  visibleButtons.forEach(el => track(el, 0.35));

  const explanatoryCopy = Array.from(document.querySelectorAll('div, p, span'))
    .filter(el => isVisible(el) && elementContainsText(el, EXPLANATION_PATTERNS));
  explanatoryCopy.forEach(el => track(el, 0.2));

  const overlays = Array.from(document.querySelectorAll('[role="dialog"], [class*="modal" i], [class*="banner" i]'))
    .filter(isVisible)
    .filter(el => getVisibilityMetrics(el).areaRatio >= 0.15);
  overlays.forEach(el => track(el, 0.2));

  const confidence = Math.min(1, score);
  return {
    detected: confidence >= 0.5,
    confidence: Number(confidence.toFixed(2)),
    reason: 'consent_wall',
    evidence: collectEvidence(matchedElements)
  };
}

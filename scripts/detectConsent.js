import {
  collectEvidence,
  computeOverlayScore,
  elementContainsText,
  extractTextTokens,
  gatherCandidates,
  getVisibilityMetrics,
  isVisible
} from './utils/dom.js';

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

const VENDOR_PATTERNS = [/iab/i, /one\strust/i, /trustarc/i, /cookiebot/i];

const BLOCKING_SELECTORS = ['[data-testid*="consent" i]', '[class*="banner" i]', '[id*="gdpr" i]'];

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
    .concat(gatherCandidates(['button', 'a', 'input[type="button"]', 'input[type="submit"]']))
    .filter(el => isVisible(el) && (elementContainsText(el, ACTION_PATTERNS) || hasActionTokens(el)));
  visibleButtons.forEach(el => track(el, 0.25));

  const explanatoryCopy = Array.from(document.querySelectorAll('div, p, span'))
    .concat(gatherCandidates(['div', 'p', 'span']))
    .filter(el => isVisible(el) && elementContainsText(el, EXPLANATION_PATTERNS));
  explanatoryCopy.forEach(el => track(el, 0.15));

  const overlays = Array.from(document.querySelectorAll('[role="dialog"], [class*="modal" i], [class*="banner" i]'))
    .concat(gatherCandidates(BLOCKING_SELECTORS))
    .filter(isVisible)
    .map(el => ({ el, metrics: getVisibilityMetrics(el), overlayScore: computeOverlayScore(el) }));
  overlays
    .filter(({ metrics }) => metrics.areaRatio >= 0.08)
    .forEach(({ el, overlayScore }) => track(el, Math.min(0.25, overlayScore + 0.15)));

  if (document.body) {
    const bodyStyle = window.getComputedStyle(document.body);
    if (bodyStyle.overflow === 'hidden') {
      score += 0.1;
    }
  }

  if (document.cookie && /consent/.test(document.cookie)) {
    score += 0.05;
  }

  const vendorScripts = Array.from(document.scripts || [])
    .filter(script => VENDOR_PATTERNS.some(pattern => pattern.test(script.src || script.text || '')));
  if (vendorScripts.length > 0) {
    score += 0.1;
  }

  const confidence = Math.min(1, score + overlays.reduce((acc, { overlayScore }) => acc + overlayScore, 0) * 0.3);
  return {
    detected: confidence >= 0.5,
    confidence: Number(confidence.toFixed(2)),
    reason: 'consent_wall',
    evidence: collectEvidence(matchedElements)
  };
}

function hasActionTokens(element) {
  const tokens = extractTextTokens(element);
  return tokens.includes('accept') || tokens.includes('agree') || tokens.includes('reject') || tokens.includes('consent');
}

import {
  collectEvidence,
  computeOverlayScore,
  elementContainsText,
  extractTextTokens,
  gatherCandidates,
  getVisibilityMetrics,
  isVisible
} from './utils/dom.js';

const AUTH_SELECTORS = [
  'form[action*="login" i]',
  'form[action*="signin" i]',
  'form[action*="auth" i]',
  'div[class*="login" i]',
  'div[id*="login" i]'
];

const KEYWORD_PATTERNS = [
  /sign\s?in/i,
  /log\s?in/i,
  /welcome\sback/i,
  /continue/i
];

const ENFORCED_SELECTORS = ['[data-test*="login" i]', '[class*="wall" i]', '[class*="paywall" i]'];

const SCRIPT_HINTS = [/authwall/i, /paywall/i, /loginwall/i];

export function detectLoginWall() {
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

  AUTH_SELECTORS.forEach(selector => {
    document.querySelectorAll(selector).forEach(el => track(el, 0.4));
  });

  gatherCandidates(ENFORCED_SELECTORS).forEach(el => track(el, 0.2));

  const passwordInputs = gatherCandidates(['input[type="password"]', 'input[type="email"]'])
    .filter(isVisible);
  passwordInputs.forEach(el => track(el, 0.35));

  const keywordButtons = Array.from(document.querySelectorAll('button, a, input[type="submit"]'))
    .concat(gatherCandidates(['button', 'a', 'input[type="submit"]']))
    .filter(el => isVisible(el) && (elementContainsText(el, KEYWORD_PATTERNS) || hasLoginTokens(el)));
  keywordButtons.forEach(el => track(el, 0.2));

  const overlays = Array.from(document.querySelectorAll('[class*="modal" i], [class*="overlay" i], div[role="dialog"]'))
    .concat(gatherCandidates(['[data-testid*="login" i]']))
    .filter(isVisible)
    .map(el => ({ el, metrics: getVisibilityMetrics(el), overlayScore: computeOverlayScore(el) }));
  overlays
    .filter(({ metrics }) => metrics.areaRatio >= 0.18)
    .forEach(({ el, overlayScore }) => track(el, Math.min(0.35, overlayScore + 0.2)));

  if (document.body && window.getComputedStyle(document.body).overflow === 'hidden') {
    score += 0.1;
  }

  const scriptHints = Array.from(document.scripts || [])
    .some(script => SCRIPT_HINTS.some(pattern => pattern.test(script.src || script.text || '')));
  if (scriptHints) {
    score += 0.1;
  }

  const confidence = Math.min(1, score);
  return {
    detected: confidence >= 0.6,
    confidence: Number(confidence.toFixed(2)),
    reason: 'login_wall',
    evidence: collectEvidence(matchedElements)
  };
}

function hasLoginTokens(element) {
  const tokens = extractTextTokens(element);
  return tokens.includes('signin') || tokens.includes('login') || tokens.includes('subscribe');
}

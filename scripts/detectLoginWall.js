import { collectEvidence, elementContainsText, getVisibilityMetrics, isVisible } from './utils/dom.js';

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

  const passwordInputs = Array.from(document.querySelectorAll('input[type="password"]'))
    .filter(isVisible);
  passwordInputs.forEach(el => track(el, 0.5));

  const keywordButtons = Array.from(document.querySelectorAll('button, a, input[type="submit"]'))
    .filter(el => isVisible(el) && elementContainsText(el, KEYWORD_PATTERNS));
  keywordButtons.forEach(el => track(el, 0.2));

  const overlays = Array.from(document.querySelectorAll('[class*="modal" i], [class*="overlay" i], div[role="dialog"]'))
    .filter(isVisible)
    .filter(el => getVisibilityMetrics(el).areaRatio >= 0.25);
  overlays.forEach(el => track(el, 0.3));

  if (document.body && window.getComputedStyle(document.body).overflow === 'hidden') {
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

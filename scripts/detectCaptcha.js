import { collectEvidence, elementContainsText, isVisible } from './utils/dom.js';

const FRAME_SELECTORS = [
  'iframe[src*="recaptcha" i]',
  'iframe[src*="hcaptcha" i]',
  'iframe[src*="turnstile" i]',
  'iframe[src*="captcha" i]'
];

const KEYWORD_PATTERNS = [
  /captcha/i,
  /robot/i,
  /select all images/i,
  /verify you/i,
  /security check/i
];

const TOKEN_SELECTORS = [
  '[data-sitekey]',
  'div[id*="captcha" i]',
  'div[class*="captcha" i]'
];

export function detectCaptcha() {
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

  FRAME_SELECTORS.forEach(selector => {
    document.querySelectorAll(selector).forEach(el => track(el, 0.5));
  });

  TOKEN_SELECTORS.forEach(selector => {
    document.querySelectorAll(selector).forEach(el => track(el, 0.25));
  });

  const keywordContainers = Array.from(document.querySelectorAll('div, span, label, p'))
    .filter(el => isVisible(el) && elementContainsText(el, KEYWORD_PATTERNS));
  keywordContainers.forEach(el => track(el, 0.2));

  if (window.grecaptcha || window.hcaptcha) {
    score += 0.2;
  }

  const confidence = Math.min(1, score);
  return {
    detected: confidence >= 0.55,
    confidence: Number(confidence.toFixed(2)),
    reason: 'captcha',
    evidence: collectEvidence(matchedElements)
  };
}

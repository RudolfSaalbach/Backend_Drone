import {
  collectEvidence,
  computeOverlayScore,
  elementContainsText,
  extractTextTokens,
  gatherCandidates,
  getElementRole,
  isVisible,
  queryDeep
} from './utils/dom.js';

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

const BADGE_SELECTORS = [
  '.grecaptcha-badge',
  '.hcaptcha-badge',
  '[class*="-captcha-badge" i]'
];

const SCRIPT_HINTS = [/recaptcha/i, /hcaptcha/i, /turnstile/i, /captchaService/i];

const ACCESSIBLE_HINTS = [/captcha/i, /are you human/i, /security challenge/i];

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

  const keywordContainers = Array.from(document.querySelectorAll('div, span, label, p, button'))
    .concat(queryDeep('div, span, label, p, button'))
    .filter(el => isVisible(el) && elementContainsText(el, KEYWORD_PATTERNS));
  keywordContainers.forEach(el => track(el, 0.15));

  gatherCandidates(BADGE_SELECTORS).forEach(el => track(el, 0.2));

  const overlayCandidates = gatherCandidates(['[role="dialog"]', '[class*="modal" i]', '[class*="overlay" i]']);
  overlayCandidates
    .map(el => ({ el, overlayScore: computeOverlayScore(el) }))
    .filter(({ overlayScore }) => overlayScore >= 0.2)
    .forEach(({ el, overlayScore }) => track(el, Math.min(0.25, overlayScore)));

  const scripts = Array.from(document.scripts || []);
  if (scripts.some(script => SCRIPT_HINTS.some(pattern => pattern.test(script.src || script.text || '')))) {
    score += 0.15;
  }

  const accessibleHints = gatherCandidates(['[aria-label]', '[aria-describedby]', '[aria-labelledby]'])
    .filter(el => ACCESSIBLE_HINTS.some(pattern => pattern.test((el.getAttribute('aria-label') || '').toLowerCase())));
  accessibleHints.forEach(el => track(el, 0.1));

  const interactiveTokens = gatherCandidates(['button', 'input[type="button"]', 'input[type="submit"]'])
    .filter(el => {
      const tokens = extractTextTokens(el);
      return tokens.includes('captcha') || tokens.includes('verify');
    });
  interactiveTokens.forEach(el => track(el, 0.1));

  if (window.grecaptcha || window.hcaptcha) {
    score += 0.2;
  }

  if (document.body) {
    const bodyStyle = window.getComputedStyle(document.body);
    if (bodyStyle.filter && bodyStyle.filter.includes('blur')) {
      score += 0.1;
    }
  }

  const confidence = Math.min(1, score + matchedElements.reduce((acc, el) => acc + computeOverlayScore(el), 0) * 0.3);
  return {
    detected: confidence >= 0.55,
    confidence: Number(confidence.toFixed(2)),
    reason: inferReason(matchedElements),
    evidence: collectEvidence(matchedElements)
  };
}

function inferReason(elements) {
  if (elements.some(el => getElementRole(el) === 'dialog')) {
    return 'captcha_dialog';
  }

  if (elements.some(el => el.tagName && el.tagName.toLowerCase() === 'iframe')) {
    return 'captcha_iframe';
  }

  return 'captcha';
}

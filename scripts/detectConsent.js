function elementContainsText(element, patterns) {
  const text = (element.textContent || '').trim();
  return patterns.some(pattern => pattern.test(text));
}

export function detectConsent() {
  const containers = Array.from(document.querySelectorAll('[class*="consent" i], [class*="cookie" i], [id*="consent" i], [id*="cookie" i]'))
    .filter(isVisible)
    .filter(el => el.getBoundingClientRect().height > 40);

  const actionButtons = Array.from(document.querySelectorAll('button, a, input[type="submit"]'))
    .filter(isVisible)
    .filter(el => elementContainsText(el, [/accept/i, /agree/i, /consent/i]));

  if (containers.length > 0 && actionButtons.length > 0) {
    return {
      detected: true,
      reason: 'consent_wall',
      evidence: 'consent_container'
    };
  }

  return { detected: false };
}

function isVisible(element) {
  const rect = element.getBoundingClientRect();
  return rect.width > 0 && rect.height > 0 && window.getComputedStyle(element).visibility !== 'hidden';
}

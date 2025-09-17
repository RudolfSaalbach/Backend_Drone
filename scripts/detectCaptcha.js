export function detectCaptcha() {
  const selectors = [
    'iframe[src*="recaptcha" i]',
    'iframe[src*="hcaptcha" i]',
    'iframe[src*="captcha" i]',
    '[class*="captcha" i]',
    '[id*="captcha" i]'
  ];

  for (const selector of selectors) {
    const element = document.querySelector(selector);
    if (element && isVisible(element)) {
      return {
        detected: true,
        reason: 'captcha',
        evidence: selector
      };
    }
  }

  return { detected: false };
}

function isVisible(element) {
  const rect = element.getBoundingClientRect();
  return rect.width > 0 && rect.height > 0 && window.getComputedStyle(element).visibility !== 'hidden';
}

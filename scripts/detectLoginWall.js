export function detectLoginWall() {
  const selectors = [
    'input[type="password"]',
    'form[action*="login" i]',
    'form[action*="signin" i]',
    'form[action*="auth" i]'
  ];

  for (const selector of selectors) {
    const element = document.querySelector(selector);
    if (element && isVisible(element)) {
      return {
        detected: true,
        reason: 'login_wall',
        evidence: selector
      };
    }
  }

  const keywordButtons = Array.from(document.querySelectorAll('button, a, input[type="submit"]'))
    .filter(el => isVisible(el))
    .filter(el => /sign\s?in|log\s?in/i.test(el.textContent || el.value || ''));

  if (keywordButtons.length > 0) {
    return {
      detected: true,
      reason: 'login_wall',
      evidence: 'keyword_button'
    };
  }

  return { detected: false };
}

function isVisible(element) {
  const rect = element.getBoundingClientRect();
  return rect.width > 0 && rect.height > 0 && window.getComputedStyle(element).visibility !== 'hidden';
}

(() => {
  const toggle = document.querySelector('[data-nav-toggle]');
  const navigation = document.getElementById('primary-navigation');

  if (!toggle || !navigation) return;

  toggle.addEventListener('click', () => {
    const open = toggle.getAttribute('aria-expanded') === 'true';
    toggle.setAttribute('aria-expanded', String(!open));
    navigation.dataset.open = String(!open);
  });

  navigation.addEventListener('click', (event) => {
    if (event.target instanceof HTMLAnchorElement && window.matchMedia('(max-width: 700px)').matches) {
      toggle.setAttribute('aria-expanded', 'false');
      navigation.dataset.open = 'false';
    }
  });
})();

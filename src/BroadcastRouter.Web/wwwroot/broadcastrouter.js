(() => {
    let pendingG = false;
    let timer = 0;

    const editable = element => element && (element.matches('input, textarea, select') || element.isContentEditable);
    const navigate = path => window.location.assign(path);
    const shortcutSheet = () => document.getElementById('br-shortcuts');

    document.addEventListener('keydown', event => {
        const dialog = shortcutSheet();
        if (event.key === 'Escape' && dialog && dialog.open) {
            dialog.close();
            event.preventDefault();
            return;
        }
        if (editable(document.activeElement)) return;
        if (event.key === '/') {
            const filter = document.querySelector('[data-operator-filter]');
            if (filter) { filter.focus(); event.preventDefault(); }
            return;
        }
        if (event.key === '?') {
            if (dialog && !dialog.open) dialog.showModal();
            event.preventDefault();
            return;
        }
        if (pendingG) {
            pendingG = false;
            clearTimeout(timer);
            const destinations = { d: '/', r: '/routes', s: '/sources', o: '/outputs', l: '/logs' };
            const path = destinations[event.key.toLowerCase()];
            if (path) { navigate(path); event.preventDefault(); }
            return;
        }
        if (event.key.toLowerCase() === 'g') {
            pendingG = true;
            clearTimeout(timer);
            timer = setTimeout(() => pendingG = false, 1200);
        }
    });
})();

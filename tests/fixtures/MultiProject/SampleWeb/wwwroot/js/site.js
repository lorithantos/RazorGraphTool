// External asset, so the graph must keep distinguishing it from the inline block.
document.addEventListener('DOMContentLoaded', () => {
    const el = document.getElementById('catalogs');
    if (el) console.log(el.dataset.catalogCount);
});

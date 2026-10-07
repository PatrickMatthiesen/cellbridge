// Search and sort the authorized documents already rendered by the server.
// Native links, details menus and protected forms also work without this script.
(() => {
  const documents = document.querySelector('#documents');
  if (!documents) return;
  const rows = Array.from(documents.querySelectorAll('[data-document]'));
  const search = document.querySelector('#search');
  const sort = document.querySelector('#sort');
  const count = document.querySelector('#file-count');
  const empty = document.querySelector('#empty-state');
  const title = document.querySelector('#view-title');
  const filters = Array.from(document.querySelectorAll('[data-filter]'));
  const labels = { all: 'All documents', docx: 'Word documents', xlsx: 'Excel workbooks', pptx: 'PowerPoint slides' };
  const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });
  let filter = 'all';

  function update() {
    const query = search.value.trim().toLocaleLowerCase();
    const sorted = [...rows].sort((a, b) => {
      const order = sort.value === 'modified' ? Number(b.dataset.modified) - Number(a.dataset.modified)
        : sort.value === 'size' ? Number(b.dataset.size) - Number(a.dataset.size) : 0;
      return order || collator.compare(a.dataset.name, b.dataset.name);
    });
    let visible = 0;
    sorted.forEach(row => {
      row.hidden = !(filter === 'all' || row.dataset.type === filter)
        || !row.dataset.name.toLocaleLowerCase().includes(query);
      if (!row.hidden) visible++;
      else row.querySelectorAll('details[open]').forEach(menu => { menu.open = false; });
      documents.append(row);
    });
    count.textContent = `${visible} ${visible === 1 ? 'file' : 'files'}${visible !== rows.length ? ` of ${rows.length}` : ''}`;
    empty.hidden = visible > 0;
    title.textContent = labels[filter];
  }

  filters.forEach(button => button.addEventListener('click', event => {
    event.preventDefault();
    filter = button.dataset.filter;
    filters.forEach(item => {
      const active = item === button;
      item.classList.toggle('active', active);
      if (item.tagName === 'BUTTON') item.setAttribute('aria-pressed', String(active));
      else if (active) item.setAttribute('aria-current', 'page');
      else item.removeAttribute('aria-current');
    });
    update();
  }));
  search.addEventListener('input', update);
  sort.addEventListener('change', update);
  document.querySelectorAll('[data-enhanced]').forEach(control => { control.hidden = false; });
  update();

  document.addEventListener('click', event => {
    document.querySelectorAll('details[open]').forEach(menu => {
      if (!menu.contains(event.target)) menu.open = false;
    });
  });
  document.addEventListener('keydown', event => {
    if (event.key !== 'Escape') return;
    const openMenus = Array.from(document.querySelectorAll('details[open]'));
    const focused = openMenus.find(menu => menu.contains(document.activeElement));
    openMenus.forEach(menu => { menu.open = false; });
    focused?.querySelector('summary').focus();
  });
})();

(function () {
  var section = location.hash.slice(1);
  var pages = {
    architecture: 'architecture.html', syntax: 'architecture.html',
    documents: 'reading-pdfs.html', authoring: 'creating-pdfs.html',
    writer: 'editing-pdfs.html', security: 'signing-encryption.html',
    quickstart: 'getting-started.html', 'create-example': 'creating-pdfs.html',
    'inspect-example': 'reading-pdfs.html', 'edit-example': 'editing-pdfs.html',
    validation: 'validation.html', regression: 'validation.html',
    incremental: 'validation.html', import: 'validation.html', validators: 'validation.html',
    integration: 'getting-started.html', workflow: 'getting-started.html',
    'integration-docs': 'api.html'
  };
  location.replace('./engine/' + (pages[section] || 'index.html'));
})();
